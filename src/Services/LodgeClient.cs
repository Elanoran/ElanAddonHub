using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ElansAddonHub.Services
{
    // Connection to a Lodge server (see server/PROTOCOL.md). The invite code only ever travels in the
    // X-Lodge-Code header - never in a URL, so it can't end up in proxy logs.
    // JSON events are raised on the UI thread; voice frames on the network thread.
    public class LodgeClient
    {
        public string BaseUrl { get; }
        readonly string code;
        readonly string name;
        readonly SynchronizationContext ui;
        ClientWebSocket ws;
        CancellationTokenSource cts;
        readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);
        volatile bool stopped;

        public bool Online { get; private set; }
        public bool IsRunning => !stopped;
        public event Action<Dictionary<string, object>> Received;   // UI thread
        public event Action<int, byte[], int, int> Voice;            // network thread: sender, data, offset, count
        public event Action<string, bool> Status;                    // UI thread: text, online

        public LodgeClient(string baseUrl, string code, string name)
        {
            BaseUrl = baseUrl.Trim().TrimEnd('/');
            this.code = code.Trim();
            this.name = name ?? "";
            ui = SynchronizationContext.Current ?? new System.Windows.Threading.DispatcherSynchronizationContext();
        }

        string WsUrl()
        {
            var b = BaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "wss://" + BaseUrl.Substring(8)
                  : BaseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "ws://" + BaseUrl.Substring(7) : BaseUrl;
            return $"{b}/ws?name={Uri.EscapeDataString(name)}&client={Uri.EscapeDataString("hub/" + App.Version)}";
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

        async Task Loop()
        {
            int[] backoff = { 2, 4, 8, 15, 30 };
            int attempt = 0;
            while (!stopped)
            {
                string why = null;
                try
                {
                    SetStatus("Connecting...", false);
                    ws = new ClientWebSocket();
                    ws.Options.SetRequestHeader("X-Lodge-Code", code);
                    ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                    cts = new CancellationTokenSource();
                    using (var t = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                        await ws.ConnectAsync(new Uri(WsUrl()), t.Token);
                    attempt = 0;
                    await ReceiveLoop(cts.Token);
                    if (ws.CloseStatus == WebSocketCloseStatus.PolicyViolation)
                    {
                        stopped = true;
                        SetStatus(ws.CloseStatusDescription ?? "The lodge closed the connection", false);
                        return;
                    }
                }
                catch (Exception e)
                {
                    var all = e.ToString();
                    if (all.Contains("(401)") || all.Contains("'401'"))
                    {
                        stopped = true;
                        SetStatus("Wrong invite code", false);
                        return;
                    }
                    if (all.Contains("(429)") || all.Contains("'429'")) why = "Too many wrong codes - wait 15 minutes";
                    if (!stopped) Util.Log("lodge: " + e.GetBaseException().Message);
                }
                if (stopped) return;
                var wait = backoff[Math.Min(attempt++, backoff.Length - 1)];
                SetStatus(why ?? $"Offline - reconnecting in {wait}s", false);
                try { await Task.Delay(TimeSpan.FromSeconds(wait)); } catch { }
            }
        }

        async Task ReceiveLoop(CancellationToken ct)
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
            req.Headers.Add("X-Lodge-Code", code);
            return req;
        }

        public async Task<Dictionary<string, object>> Upload(string file)
        {
            using (var fs = File.OpenRead(file))
            using (var req = Request(HttpMethod.Post, "/files"))
            {
                req.Headers.Add("X-File-Name", Uri.EscapeDataString(Path.GetFileName(file)));
                req.Headers.Add("X-Lodge-Name", Uri.EscapeDataString(name));
                req.Content = new StreamContent(fs);
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
                case 429: return "Too many uploads right now - try again in a few minutes";
                case 507: return "The lodge's file storage is full - ask the Guild Master to free some space";
                case 403: return "Initiates can't share files";
                case 401: return "Your invite code isn't valid any more";
                default: return string.IsNullOrEmpty(serverText) ? $"Upload failed ({status})" : serverText;
            }
        }

        public async Task Download(string id, string toFile)
        {
            using (var req = Request(HttpMethod.Get, "/files/" + id))
            using (var resp = await Net.Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead))
            {
                if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"Download failed ({(int)resp.StatusCode})");
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
