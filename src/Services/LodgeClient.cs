using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ElansAddonHub.Lodge;

namespace ElansAddonHub.Services
{
    // Connection to a Lodge server (see server/PROTOCOL.md). The invite code only ever travels in the
    // X-Lodge-Code header (or, since 2.38 / server 2.10, the per-device token in X-Lodge-Token) - never in a URL, so it can't end up in proxy logs.
    // JSON events are raised on the UI thread; voice frames on the network thread.
    public class LodgeClient
    {
        public string BaseUrl { get; }
        readonly string code;
        readonly bool isToken;                                       // the credential is a device token (X-Lodge-Token), not a permanent code
        readonly string name;
        readonly SynchronizationContext ui;
        ClientWebSocket ws;
        CancellationTokenSource cts;
        readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);
        volatile bool stopped;
        DateTime lastRx = DateTime.UtcNow;                           // the last frame of any kind (message, pong, voice)
        volatile bool pongSeen;                                      // the server answers pings (2.x): only then is silence a dead connection
        public static readonly TimeSpan PingEvery = TimeSpan.FromSeconds(20), DeadAfter = TimeSpan.FromSeconds(45);
        public static readonly TimeSpan FullRetry = TimeSpan.FromSeconds(60);

        public Func<bool> Invisible;                                 // asked at every (re)connect: appear offline from the first moment
        public bool Online { get; private set; }
        public bool IsRunning => !stopped;
        public bool SignedOut { get; private set; }                  // stopped because the server no longer knows this device (revoked)
        public event Action<Dictionary<string, object>> Received;   // UI thread
        public event Action<int, byte[], int, int> Voice;            // network thread: sender, data, offset, count
        public event Action<string, bool> Status;                    // UI thread: text, online

        public LodgeClient(string baseUrl, string code, string name, bool isToken = false)
        {
            this.isToken = isToken;
            BaseUrl = baseUrl.Trim().TrimEnd('/');
            this.code = code.Trim();
            this.name = name ?? "";
            ui = SynchronizationContext.Current ?? new System.Windows.Threading.DispatcherSynchronizationContext();
        }

        string WsUrl()
        {
            var b = BaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "wss://" + BaseUrl.Substring(8)
                  : BaseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "ws://" + BaseUrl.Substring(7) : BaseUrl;
            return $"{b}/ws?name={Uri.EscapeDataString(name)}&client={Uri.EscapeDataString("hub/" + App.Version)}" + (Invisible?.Invoke() == true ? "&vis=invisible" : "");
        }

        void Post(Action a) => ui.Post(_ => a(), null);
        void SetStatus(string text, bool online) { Online = online; Post(() => Status?.Invoke(text, online)); }

        public void Start()
        {
            stopped = false;
            _ = Loop();
        }

        public void Stop()
        {
            stopped = true;
            try { cts?.Cancel(); } catch { }
            try { ws?.Abort(); } catch { }
            Online = false;
        }

        // the HTTP status behind a failed handshake: the response of the inner WebException when there is one, else the number
        // in the message ("... returned status code '401' ...") - .NET Framework gives no property for it on the WebSocketException
        public static int StatusOf(Exception e)
        {
            for (var x = e; x != null; x = x.InnerException)
            {
                if (x is System.Net.WebException we && we.Response is System.Net.HttpWebResponse hr) return (int)hr.StatusCode;
            }
            for (var x = e; x != null; x = x.InnerException)
            {
                var m = System.Text.RegularExpressions.Regex.Match(x.Message ?? "", @"status code '?\(?(\d{3})");
                if (m.Success) return int.Parse(m.Groups[1].Value);
                m = System.Text.RegularExpressions.Regex.Match(x.Message ?? "", @"\((\d{3})\)");
                if (m.Success) return int.Parse(m.Groups[1].Value);
            }
            return 0;
        }

        public const string SignedOutText = "This device was signed out - join again with a new invite link";

        // the server accepted the socket and closed it again at once with this reason
        public static bool IsFullClose(string reason) => reason != null && reason.IndexOf("is full", StringComparison.OrdinalIgnoreCase) >= 0;

        async Task Loop()
        {
            int[] backoff = { 2, 4, 8, 15, 30 };
            int attempt = 0;
            while (!stopped)
            {
                string why = null;
                int waitOverride = 0;
                ClientWebSocket mine = null;
                try
                {
                    SetStatus("Connecting...", false);
                    mine = ws = new ClientWebSocket();
                    ws.Options.SetRequestHeader(isToken ? "X-Lodge-Token" : "X-Lodge-Code", code);
                    ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                    cts = new CancellationTokenSource();
                    using (var t = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                        await ws.ConnectAsync(new Uri(WsUrl()), t.Token);
                    attempt = 0;
                    lastRx = DateTime.UtcNow; pongSeen = false;
                    await ReceiveLoop(cts.Token);
                    if (ws.CloseStatus == WebSocketCloseStatus.PolicyViolation)
                    {
                        if (IsFullClose(ws.CloseStatusDescription) && !stopped)
                        {
                            // not a verdict: somebody leaves sooner or later - keep knocking, calmly
                            why = "The lodge is full - trying again every minute";
                            waitOverride = (int)FullRetry.TotalSeconds;
                        }
                        else
                        {
                            stopped = true;
                            if (isToken && (ws.CloseStatusDescription ?? "").IndexOf("signed out", StringComparison.OrdinalIgnoreCase) >= 0) SignedOut = true;
                            SetStatus(ws.CloseStatusDescription ?? "The lodge closed the connection", false);
                            return;
                        }
                    }
                    else if (ws.CloseStatus == WebSocketCloseStatus.EndpointUnavailable)
                    {
                        // the server is restarting (update, or it dropped a silent connection): come back quickly (voice is rejoined by the session)
                        if (stopped) return;
                        SetStatus("Lodge restarting - reconnecting...", false);
                        try { await Task.Delay(TimeSpan.FromSeconds(2)); } catch { }
                        attempt = 0;
                        continue;
                    }
                }
                catch (Exception e)
                {
                    var status = StatusOf(e);
                    if (status == 401)
                    {
                        stopped = true;
                        SignedOut = isToken;
                        SetStatus(isToken ? SignedOutText : "Wrong invite code", false);
                        return;
                    }
                    if (status == 429) why = "Too many wrong codes - wait 15 minutes";
                    if (!stopped) Util.Log("lodge: " + e.GetBaseException().Message);
                }
                finally
                {
                    // every socket is disposed - a reconnect loop must not pile up handles
                    try { mine?.Dispose(); } catch { }
                    if (ReferenceEquals(ws, mine)) ws = null;
                }
                if (stopped) return;
                var wait = waitOverride > 0 ? waitOverride : backoff[Math.Min(attempt++, backoff.Length - 1)];
                SetStatus(why ?? $"Offline - reconnecting in {wait}s", false);
                try { await Task.Delay(TimeSpan.FromSeconds(wait)); } catch { }
            }
        }

        // an app-level ping every 20 s (the proxy chain can hold a dead connection open for minutes), and when the server is known
        // to answer them, 45 s without any frame means the connection is gone: abort it so the loop reconnects
        async Task Keepalive(ClientWebSocket sock, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested && sock.State == WebSocketState.Open)
                {
                    await Task.Delay(PingEvery, ct);
                    if (sock.State != WebSocketState.Open) return;
                    if (pongSeen && DateTime.UtcNow - lastRx > DeadAfter)
                    {
                        Util.Log("lodge: no sign of life for " + (int)(DateTime.UtcNow - lastRx).TotalSeconds + " s - reconnecting");
                        try { sock.Abort(); } catch { }
                        return;
                    }
                    await SendRaw(Encoding.UTF8.GetBytes("{\"t\":\"ping\"}"), WebSocketMessageType.Text, 1000);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { Util.Log("lodge keepalive: " + e.Message); }
        }

        async Task ReceiveLoop(CancellationToken ct)
        {
            using (var ka = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                var kat = Keepalive(ws, ka.Token);
                try { await ReceiveFrames(ct); }
                finally { ka.Cancel(); }
            }
        }

        async Task ReceiveFrames(CancellationToken ct)
        {
            var buf = new byte[16 * 1024];
            var msg = new MemoryStream();
            bool first = true;
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                msg.SetLength(0);
                WebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    msg.Write(buf, 0, r.Count);
                } while (!r.EndOfMessage);

                lastRx = DateTime.UtcNow;
                var data = msg.GetBuffer();
                var len = (int)msg.Length;
                if (r.MessageType == WebSocketMessageType.Binary)
                {
                    if (len > 4) Voice?.Invoke(BitConverter.ToInt32(data, 0), data, 4, len - 4);
                    continue;
                }
                Dictionary<string, object> obj;
                try { obj = Json.Obj(Encoding.UTF8.GetString(data, 0, len)); } catch { continue; }
                if (obj == null) continue;
                if (obj.Str("t") == "pong") { pongSeen = true; continue; }
                if (first && obj.Str("t") == "welcome") { first = false; SetStatus("Online", true); }
                Post(() => Received?.Invoke(obj));
            }
        }

        public Task Send(Dictionary<string, object> obj) => SendRaw(Encoding.UTF8.GetBytes(Json.Write(obj)), WebSocketMessageType.Text, 5000);

        // voice: drop the frame rather than queue up behind a slow send
        public Task SendVoice(byte[] packet, int count)
        {
            var copy = new byte[count];
            Buffer.BlockCopy(packet, 0, copy, 0, count);
            return SendRaw(copy, WebSocketMessageType.Binary, 0);
        }

        async Task SendRaw(byte[] data, WebSocketMessageType type, int waitMs)
        {
            var sock = ws;
            if (sock == null || sock.State != WebSocketState.Open) return;
            if (!await sendLock.WaitAsync(waitMs)) return;
            try { await sock.SendAsync(new ArraySegment<byte>(data), type, true, CancellationToken.None); }
            catch (Exception e) { Util.Log("lodge send: " + e.Message); }
            finally { sendLock.Release(); }
        }

        // ---- files

        HttpRequestMessage Request(HttpMethod m, string path)
        {
            var req = new HttpRequestMessage(m, BaseUrl + path);
            req.Headers.Add(isToken ? "X-Lodge-Token" : "X-Lodge-Code", code);
            return req;
        }

        // a read-through stream that reports how far the upload is
        sealed class ProgressStream : Stream
        {
            readonly Stream inner; readonly Action<double> report; long pos;
            public ProgressStream(Stream inner, Action<double> report) { this.inner = inner; this.report = report; }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => inner.Length;
            public override long Position { get => pos; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count)
            {
                var n = inner.Read(buffer, offset, count);
                pos += n;
                if (inner.Length > 0) report?.Invoke(Math.Min(1.0, (double)pos / inner.Length));
                return n;
            }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        public async Task<Dictionary<string, object>> Upload(string file, Action<double> progress = null)
        {
            using (var fs = File.OpenRead(file))
            using (var req = Request(HttpMethod.Post, "/files"))
            {
                req.Headers.Add("X-File-Name", Uri.EscapeDataString(SafeFile.Name(Path.GetFileName(file))));
                req.Headers.Add("X-Lodge-Name", Uri.EscapeDataString(name));
                req.Content = new StreamContent(progress == null ? (Stream)fs : new ProgressStream(fs, progress));
                using (var resp = await Net.Http.SendAsync(req))
                {
                    var text = await resp.Content.ReadAsStringAsync();
                    Dictionary<string, object> body = null;
                    try { body = Json.Obj(text); } catch { } // a proxy's 413 page isn't JSON
                    if (!resp.IsSuccessStatusCode) throw new InvalidOperationException(UploadError((int)resp.StatusCode, body.Str("error")));
                    return body ?? new Dictionary<string, object>();
                }
            }
        }

        // friendly texts for the server's upload limits (413 too big, 429 too many, 507 storage full)
        public static string UploadError(int status, string serverText)
        {
            switch (status)
            {
                case 413: return "That file is too big for this lodge";
                case 408: return "The upload took too long and was stopped";
                case 429: return string.IsNullOrEmpty(serverText) ? "Too many uploads right now - try again in a few minutes" : serverText;
                case 507: return "The lodge's file storage is full - ask the Guild Master to free some space";
                case 403: return "Initiates can't share files";
                case 401: return "Your sign-in isn't valid any more - join again with a new invite link";
                default: return string.IsNullOrEmpty(serverText) ? $"Upload failed ({status})" : serverText;
            }
        }

        public class DownloadException : InvalidOperationException
        {
            public int Status { get; }
            public DownloadException(int status) : base($"Download failed ({status})") { Status = status; }
        }

        public async Task Download(string id, string toFile)
        {
            if (SafeFile.Id(id) == null) throw new InvalidOperationException("That file can't be downloaded");
            using (var req = Request(HttpMethod.Get, "/files/" + id))
            using (var resp = await Net.Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead))
            {
                if (!resp.IsSuccessStatusCode) throw new DownloadException((int)resp.StatusCode);
                var tmp = toFile + ".part";
                using (var src = await resp.Content.ReadAsStreamAsync())
                using (var dst = File.Create(tmp))
                    await src.CopyToAsync(dst);
                if (File.Exists(toFile)) File.Delete(toFile);
                File.Move(tmp, toFile);
            }
        }
    }
}
