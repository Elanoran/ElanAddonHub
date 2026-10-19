using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace ElansAddonHub.Services
{
    // --selftest "radio": plays a Radio Paradise channel (Outpost 2.72). One HTTP connection, opened only when the user presses Play and closed (aborted)
    // on Pause / Stop / channel change / quality change / exit; there is no muted background streaming. Decoding (2.75: the Sound quality setting):
    // Standard = the MP3 192k stream (NAudio's Mp3Frame + the Windows ACM MP3 decoder), High = AAC 320k (Media Foundation's AAC decoder, part of
    // Windows), Lossless = Ogg FLAC (the Outpost's own decoder); no extra package for any of them (RadioDecoders.cs). A quality the channel lacks or
    // this PC can't decode steps down (never up), with a note. The PCM goes into a small buffer that a WaveOutEvent on the default device plays. The volume and the voice-room ducking are a gain on the samples (never the device or the
    // session volume, which the Lodge voice shares). The ICY metadata gives the title and the cover's address (2.73); nothing is recorded or saved.
    public enum RadioState { Stopped, Connecting, Playing, Waiting, GaveUp, Unsupported, NoDevice }

    public sealed class RadioPlayer : IDisposable
    {
        // ---- self-test: a self-test never reaches the network or the speakers (checked in UrlFor / the output factory)
        public static readonly bool SelfTestRun = Environment.GetCommandLineArgs().Contains("--selftest");
        public string TestBaseUrl;                         // http://127.0.0.1:<port>/ (the fake ICY server)
        public double BackoffScale = 1;                    // the self-test shrinks the waits (the sequence stays the same)
        public Random Rnd = new Random();
        public Func<IWavePlayer> OutputFactory;            // null = WaveOutEvent on the default device (a NullOutput in a self-test)
        public bool Silent;                                // decode but never reach a sound device (a developer's check)

        readonly SynchronizationContext ui;
        readonly object gate = new object();
        Run current;
        RadioChannel channel = RadioParadise.Channels[0];
        RadioQuality quality = RadioQuality.Standard;
        float volume = 0.6f, duck = 1f;
        GainProvider gain;

        public event Action Changed;                      // on the UI thread (the context the player was made on)
        public RadioState State { get; private set; }
        public string Title { get; private set; }         // "Artist - Title" from the stream, null until the first one
        public string CoverUrl { get; private set; }      // the song's StreamUrl from the same metadata block (null = none); RadioNowPlaying checks it
        public int Attempt { get; private set; }           // the reconnect try that is waiting / running (0 = the first connection)
        public TimeSpan NextWait { get; private set; }
        public string LastError { get; private set; }
        public int ConnectionsOpened;                      // requests sent (self-test)
        public long DecodedBytes;                          // PCM bytes decoded (self-test)
        public readonly List<TimeSpan> Waits = new List<TimeSpan>();   // the backoff waits of this play session (self-test)
        public bool WantsPlayback { get { lock (gate) return current != null; } }
        public RadioChannel Channel => channel;
        public RadioQuality Quality => quality;            // what Settings asks for
        public RadioStream Stream { get; private set; }    // what the current / last connection plays (its quality, format and why it differs)
        public Func<RadioFormat, bool> CanDecode = RadioDecoders.CanDecode;   // the self-test can swap it
        bool Decodable(RadioFormat f) => !RadioDecoders.HasFailed(f) && CanDecode(f);   // a decoder that failed to start this run never counts
        public RadioStream Planned => RadioParadise.Resolve(channel, quality, Decodable);   // what Play would open now
        public RadioStream PlanFor(RadioQuality q) => RadioParadise.Resolve(channel, q, Decodable);   // what Play would open at another quality (the pop-out's segments)
        public float AppliedGain => gain?.Current ?? Gain;

        public RadioPlayer() { ui = SynchronizationContext.Current; }

        // one HttpClient for the streams and the covers (3.1: HttpWebRequest is obsolete on .NET 10 and closed an aborted connection only later,
        // so a switch briefly had two open): no redirects (only the published address itself), no decompression, no cookies, the system proxy as
        // before; the timeouts are per request (a stream runs for hours), every request says Connection: close
        internal static readonly HttpClient Http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            MaxResponseDrainSize = 0,                  // a stopped stream is never read on to reuse its connection: disposing it closes the socket
            ConnectTimeout = TimeSpan.FromSeconds(15),
        }) { Timeout = Timeout.InfiniteTimeSpan };

        // a GET with the Outpost's User-Agent and Connection: close (nothing lingers after Pause); the headers go out as they are
        internal static HttpRequestMessage NewGet(string url)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", RadioParadise.UserAgent);
            req.Headers.ConnectionClose = true;
            return req;
        }

        // sends it and returns at the answer's headers (the body is read as it comes); connect + headers longer than ms is a timeout
        internal static HttpResponseMessage Send(HttpRequestMessage req, CancellationTokenSource cts, int ms)
        {
            cts.CancelAfter(ms);
            try { return Http.Send(req, HttpCompletionOption.ResponseHeadersRead, cts.Token); }
            catch (OperationCanceledException) { throw new TimeoutException("The operation has timed out."); }
            finally { try { cts.CancelAfter(Timeout.Infinite); } catch { } }
        }

        // a header of the answer as it was sent, not validated (null = none)
        internal static string Header(HttpResponseMessage resp, string name)
        {
            if (resp.Headers.NonValidated.TryGetValues(name, out var v) || resp.Content.Headers.NonValidated.TryGetValues(name, out v)) return v.ToString();
            return null;
        }

        // ---- volume: the slider (0..1, a squared curve so the low end is usable) times the ducking factor
        public float Volume { get => volume; set { volume = Math.Max(0, Math.Min(1, value)); ApplyGain(); } }
        public float Duck { get => duck; set { duck = Math.Max(0, Math.Min(1, value)); ApplyGain(); } }
        public bool Muted { get => muted; set { muted = value; ApplyGain(); } }   // the speaker in the title bar (this session only; the volume is kept)
        bool muted;
        public float Gain => muted ? 0 : volume * volume * duck;
        void ApplyGain() { var g = gain; if (g != null) g.Target = Gain; }

        public void SetChannel(string id)
        {
            var c = RadioParadise.Channel(id);
            if (c == channel) return;
            channel = c;
            bool playing;
            lock (gate) playing = current != null;
            if (playing) { StopCore(false); Title = null; CoverUrl = null; Start(); }   // the old connection is closed before the new one opens
            else { Title = null; CoverUrl = null; Raise(); }
        }

        // Settings > Sound quality: while it plays, the old connection is closed before the new one opens (like a channel change)
        public void SetQuality(RadioQuality q)
        {
            if (q == quality) return;
            quality = q;
            bool playing;
            lock (gate) playing = current != null;
            if (playing) { StopCore(false); Title = null; CoverUrl = null; Start(); }
            else { Stream = null; Raise(); }
        }

        public void Play() { lock (gate) if (current != null) return; Start(); }
        public void Retry() { StopCore(false); Start(); }
        public void Toggle() { if (WantsPlayback) Stop(); else Play(); }

        public void Stop() { StopCore(true); }

        void StopCore(bool raise)
        {
            Run r;
            lock (gate) { r = current; current = null; }
            r?.Cancel();
            State = RadioState.Stopped; Attempt = 0;
            if (raise) Raise();
        }

        void Start()
        {
            Run prev, run = new Run();
            lock (gate) { prev = last; current = run; last = run; }
            Waits.Clear(); Attempt = 0; LastError = null;
            State = RadioState.Connecting;
            Raise();
            run.Thread = new Thread(() => Worker(run, prev)) { IsBackground = true, Name = "Radio" };
            run.Thread.Start();
        }
        Run last;

        public void Dispose() => StopCore(false);

        // ---- the address: the published link, or the fake server in a self-test (never the real one there)
        public string UrlFor(RadioChannel c) => UrlFor(c.Path);
        public string UrlFor(string path)
        {
            if (TestBaseUrl != null) return TestBaseUrl.TrimEnd('/') + "/" + path;
            return RadioParadise.StreamUrl(path);
        }

        public bool UrlAllowed(string url)
        {
            if (SelfTestRun || TestBaseUrl != null)
                return Uri.TryCreate(url, UriKind.Absolute, out var u) && u.IsLoopback && u.Scheme == Uri.UriSchemeHttp;
            return RadioParadise.IsAllowedStreamUrl(url);
        }

        sealed class Run
        {
            public volatile bool Cancelled;
            public Thread Thread;
            public CancellationTokenSource Cts;           // the current connection's: cancelling it aborts the request or the read in progress
            public volatile HttpResponseMessage Response;
            public readonly ManualResetEvent Wake = new ManualResetEvent(false);
            public void Cancel()
            {
                Cancelled = true;
                try { Cts?.Cancel(); } catch { }
                try { Response?.Dispose(); } catch { }   // the socket closes now, not when the worker gets to it
                Wake.Set();
            }
        }

        void Worker(Run run, Run prev)
        {
            bool handover = prev?.Thread?.IsAlive == true;
            try { prev?.Thread?.Join(3000); } catch { }   // one connection at a time: the old one is gone before this one opens
            if (handover) run.Wake.WaitOne(200);           // 3.1: and its close has reached the server (HttpClient connects again within a millisecond)
            int attempt = 0;
            while (!run.Cancelled)
            {
                Set(run, () => { State = RadioState.Connecting; Attempt = attempt; });
                bool longPlay = false;
                RadioState? final = null;
                try { longPlay = Connect(run, out final); }
                catch (RadioDecoderUnavailableException e)
                {
                    // this PC can't decode it after all: never again this run, and straight on with the next lower quality (no wait)
                    RadioDecoders.MarkFailed(e.Format);
                    if (!run.Cancelled) LastError = e.Message;
                    if (!run.Cancelled && RadioParadise.Resolve(channel, quality, Decodable) != null) { run.Wake.WaitOne(250); continue; }   // the old socket is gone first
                    final = RadioState.Unsupported;
                }
                catch (Exception e) { if (!run.Cancelled) LastError = e.Message; }
                if (run.Cancelled) break;
                if (final != null) { var f = final.Value; Finish(run, () => State = f); break; }   // a format we can't play / no sound device: retrying won't help
                if (longPlay) attempt = 0;                   // it played for a while: a later drop starts the waits from 2 s again
                attempt++;
                if (attempt > RadioParadise.MaxTries) { int n = attempt - 1; Finish(run, () => { State = RadioState.GaveUp; Attempt = n; }); break; }
                var wait = RadioParadise.Backoff(attempt, Rnd);
                lock (Waits) Waits.Add(wait);
                Set(run, () => { State = RadioState.Waiting; Attempt = attempt; NextWait = wait; });
                run.Wake.WaitOne(TimeSpan.FromMilliseconds(Math.Max(1, wait.TotalMilliseconds * BackoffScale)));
            }
        }

        // gave up / unsupported: the user no longer "wants" playback (the button shows Retry / Play), the thread ends
        void Finish(Run run, Action change) { lock (gate) { if (current != run) return; current = null; } change(); Raise(); }

        // one connection: true when it played long enough to count as a success
        bool Connect(Run run, out RadioState? final)
        {
            final = null;
            var stream = RadioParadise.Resolve(channel, quality, Decodable);
            if (stream == null)
            {
                // nothing this PC can decode at or below the chosen quality (Serenity, whose lossy stream is AAC, on a PC without the AAC decoder)
                LastError = "this PC can't play " + channel.Name + "'s " + RadioParadise.QualityName(quality) + " stream";
                Set(run, () => Stream = null);
                final = RadioState.Unsupported; return false;
            }
            Set(run, () => Stream = stream);
            var url = UrlFor(stream.Path);
            if (!UrlAllowed(url)) { LastError = "not an allowed stream address"; final = RadioState.Unsupported; return false; }
            var cts = new CancellationTokenSource();
            run.Cts = cts;
            if (run.Cancelled) { cts.Dispose(); return false; }
            Interlocked.Increment(ref ConnectionsOpened);
            IWavePlayer output = null; IRadioDecoder decoder = null;
            HttpResponseMessage resp = null;
            var clock = Stopwatch.StartNew();
            try
            {
                using (var req = NewGet(url))
                {
                    req.Headers.TryAddWithoutValidation("Icy-MetaData", "1");
                    resp = Send(req, cts, 15000);   // connect + headers in 15 s; then each read in 20 s (ReadTimeoutStream)
                }
                run.Response = resp;
                if (run.Cancelled) return false;
                using (resp)
                {
                    if ((int)resp.StatusCode != 200) throw new WebException("HTTP " + (int)resp.StatusCode);
                    var type = Header(resp, "Content-Type") ?? "";
                    // the format the answer says it is (no type: the one asked for); a type the player has no decoder for is not retried
                    var fmt = type.Length == 0 ? stream.Format : RadioParadise.FormatOf(type);
                    if (fmt == null) { LastError = "stream format " + type.ToLowerInvariant(); final = RadioState.Unsupported; return false; }
                    int.TryParse(Header(resp, "icy-metaint"), out var metaInt);
                    using (var icy = new IcyStream(new ReadTimeoutStream(resp.Content.ReadAsStream(cts.Token), cts, 20000), Math.Max(0, metaInt)))
                    {
                        icy.MetaChanged += (t, u) => Set(run, () => { Title = t; CoverUrl = u; });
                        decoder = RadioDecoders.Create(fmt.Value, icy);
                        decoder.TagsChanged += (t, u) => Set(run, () => { Title = t; CoverUrl = u; });   // FLAC: the song in the stream's own comments
                        BufferedWaveProvider buffer = null;
                        byte[] pcm = null;
                        bool started = false;
                        while (!run.Cancelled)
                        {
                            if (buffer != null && buffer.BufferedDuration.TotalSeconds > 3) { run.Wake.WaitOne(100); continue; }
                            int n = decoder.Decode(ref pcm);
                            if (n < 0) break;
                            if (n == 0) continue;
                            var of = decoder.OutputFormat;
                            if (buffer == null || !SameFormat(buffer.WaveFormat, of))
                            {
                                // the first audio, or a new format mid-stream (the next link of an Ogg chain at another rate): a fresh buffer and output
                                if (output != null) { try { output.Stop(); output.Dispose(); } catch { } output = null; started = false; }
                                buffer = new BufferedWaveProvider(of) { BufferDuration = TimeSpan.FromSeconds(6), DiscardOnBufferOverflow = true };
                                var g = new GainProvider(buffer.ToSampleProvider()) { Target = Gain };
                                g.Current = g.Target;
                                gain = g;
                                try { output = MakeOutput(); output.Init(new SampleToWaveProvider16(g)); }
                                catch (Exception e) { LastError = "no sound device: " + e.Message; final = RadioState.NoDevice; return false; }
                            }
                            buffer.AddSamples(pcm, 0, n); Interlocked.Add(ref DecodedBytes, n);
                            if (!started && buffer.BufferedDuration.TotalMilliseconds >= 400)
                            {
                                output.Play(); started = true;
                                Set(run, () => { State = RadioState.Playing; LastError = null; });
                            }
                        }
                    }
                }
            }
            finally
            {
                try { output?.Stop(); } catch { }
                try { output?.Dispose(); } catch { }
                try { decoder?.Dispose(); } catch { }
                gain = null;
                try { resp?.Dispose(); } catch { }        // closes the socket (Connection: close) before the next connection may open
                try { cts.Dispose(); } catch { }
            }
            return clock.Elapsed.TotalSeconds >= 30;
        }

        static bool SameFormat(WaveFormat a, WaveFormat b) => a != null && b != null && a.Encoding == b.Encoding && a.SampleRate == b.SampleRate && a.Channels == b.Channels && a.BitsPerSample == b.BitsPerSample;

        IWavePlayer MakeOutput()
        {
            if (OutputFactory != null) return OutputFactory();
            if (SelfTestRun || Silent) return new NullOutput();
            return new WaveOutEvent { DesiredLatency = 200, NumberOfBuffers = 3 };
        }

        // a change from the worker, only while that worker is still the current one (a stopped run never writes over a newer state)
        void Set(Run run, Action change)
        {
            lock (gate) if (current != run) return;
            change();
            Raise();
        }

        void Raise()
        {
            var h = Changed;
            if (h == null) return;
            if (ui != null && SynchronizationContext.Current != ui) ui.Post(_ => h(), null); else h();
        }

        // the volume as a gain on the samples, eased over each buffer so a change never clicks
        sealed class GainProvider : ISampleProvider
        {
            readonly ISampleProvider src;
            public volatile float Target;
            public float Current;
            public GainProvider(ISampleProvider src) { this.src = src; }
            public WaveFormat WaveFormat => src.WaveFormat;
            public int Read(float[] buffer, int offset, int count)
            {
                int n = src.Read(buffer, offset, count);
                float from = Current, to = Target;
                for (int i = 0; i < n; i++) buffer[offset + i] *= from + (to - from) * (i + 1) / n;
                Current = to;
                return n;
            }
        }
    }

    // a response stream with the old ReadWriteTimeout: a read that waits longer than ms - or a stop (the token) - aborts the connection
    sealed class ReadTimeoutStream : Stream
    {
        readonly Stream src;
        readonly CancellationTokenSource cts;
        readonly int ms;
        public ReadTimeoutStream(Stream src, CancellationTokenSource cts, int ms) { this.src = src; this.cts = cts; this.ms = ms; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            cts.CancelAfter(ms);
            try { return src.ReadAsync(buffer, offset, count, cts.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { throw new TimeoutException("The operation has timed out."); }
            finally { try { cts.CancelAfter(Timeout.Infinite); } catch { } }
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) src.Dispose(); base.Dispose(disposing); }
    }

    // plays nothing: pulls the samples at real-time pace (the self-test's "speaker", so the buffer drains like a real device)
    public sealed class NullOutput : IWavePlayer
    {
        IWaveProvider src;
        Thread thread;
        volatile bool run;
        public long BytesRead;
        public float Volume { get; set; } = 1;
        public PlaybackState PlaybackState { get; private set; }
        public WaveFormat OutputWaveFormat => src?.WaveFormat;
        public event EventHandler<StoppedEventArgs> PlaybackStopped;
        public void Init(IWaveProvider waveProvider) { src = waveProvider; }
        public void Play()
        {
            if (run) return;
            run = true; PlaybackState = PlaybackState.Playing;
            thread = new Thread(() =>
            {
                var chunk = new byte[Math.Max(64, src.WaveFormat.AverageBytesPerSecond / 50)];
                var clock = Stopwatch.StartNew(); long due = 0;
                while (run)
                {
                    BytesRead += src.Read(chunk, 0, chunk.Length);
                    due += 20;
                    var ahead = due - clock.ElapsedMilliseconds;
                    if (ahead > 0) Thread.Sleep((int)ahead);
                }
            }) { IsBackground = true, Name = "Radio null output" };
            thread.Start();
        }
        public void Stop() { run = false; PlaybackState = PlaybackState.Stopped; try { thread?.Join(500); } catch { } PlaybackStopped?.Invoke(this, new StoppedEventArgs()); }
        public void Pause() => Stop();
        public void Dispose() => Stop();
    }
}
