using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace ElansAddonHub.Services
{
    // --selftest "radio": plays a Radio Paradise channel (Outpost 2.72). One HTTP connection, opened only when the user presses Play and closed (aborted)
    // on Pause / Stop / channel change / exit; there is no muted background streaming. Decoding: the channel's MP3 192k stream, frame by frame
    // (NAudio's Mp3Frame + the Windows ACM MP3 decoder, already part of NAudio.WinMM and Windows: no extra package), into a small buffer that a
    // WaveOutEvent on the default device plays. The volume and the voice-room ducking are a gain on the samples (never the device or the
    // session volume, which the Lodge voice shares). The ICY metadata gives the title; nothing is recorded or saved.
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
        float volume = 0.6f, duck = 1f;
        GainProvider gain;

        public event Action Changed;                      // on the UI thread (the context the player was made on)
        public RadioState State { get; private set; }
        public string Title { get; private set; }         // "Artist - Title" from the stream, null until the first one
        public int Attempt { get; private set; }           // the reconnect try that is waiting / running (0 = the first connection)
        public TimeSpan NextWait { get; private set; }
        public string LastError { get; private set; }
        public int ConnectionsOpened;                      // requests sent (self-test)
        public long DecodedBytes;                          // PCM bytes decoded (self-test)
        public readonly List<TimeSpan> Waits = new List<TimeSpan>();   // the backoff waits of this play session (self-test)
        public bool WantsPlayback { get { lock (gate) return current != null; } }
        public RadioChannel Channel => channel;
        public float AppliedGain => gain?.Current ?? Gain;

        public RadioPlayer() { ui = SynchronizationContext.Current; }

        // ---- volume: the slider (0..1, a squared curve so the low end is usable) times the ducking factor
        public float Volume { get => volume; set { volume = Math.Max(0, Math.Min(1, value)); ApplyGain(); } }
        public float Duck { get => duck; set { duck = Math.Max(0, Math.Min(1, value)); ApplyGain(); } }
        public float Gain => volume * volume * duck;
        void ApplyGain() { var g = gain; if (g != null) g.Target = Gain; }

        public void SetChannel(string id)
        {
            var c = RadioParadise.Channel(id);
            if (c == channel) return;
            channel = c;
            bool playing;
            lock (gate) playing = current != null;
            if (playing) { StopCore(false); Title = null; Start(); }   // the old connection is closed before the new one opens
            else { Title = null; Raise(); }
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
        public string UrlFor(RadioChannel c)
        {
            if (TestBaseUrl != null) return TestBaseUrl.TrimEnd('/') + "/" + c.Path;
            return RadioParadise.StreamUrl(c);
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
            public HttpWebRequest Request;
            public readonly ManualResetEvent Wake = new ManualResetEvent(false);
            public void Cancel()
            {
                Cancelled = true;
                try { Request?.Abort(); } catch { }
                Wake.Set();
            }
        }

        void Worker(Run run, Run prev)
        {
            try { prev?.Thread?.Join(3000); } catch { }   // one connection at a time: the old one is gone before this one opens
            int attempt = 0;
            while (!run.Cancelled)
            {
                Set(run, () => { State = RadioState.Connecting; Attempt = attempt; });
                bool longPlay = false;
                RadioState? final = null;
                try { longPlay = Connect(run, out final); }
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
            var url = UrlFor(channel);
            if (!UrlAllowed(url)) { LastError = "not an allowed stream address"; final = RadioState.Unsupported; return false; }
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.UserAgent = RadioParadise.UserAgent;
            req.Headers["Icy-MetaData"] = "1";
            req.KeepAlive = false;                      // Connection: close - nothing lingers after Pause
            req.Timeout = 15000; req.ReadWriteTimeout = 20000;
            req.AllowAutoRedirect = false;              // only the published address itself
            req.AutomaticDecompression = DecompressionMethods.None;
            run.Request = req;
            if (run.Cancelled) return false;
            Interlocked.Increment(ref ConnectionsOpened);
            IWavePlayer output = null; IMp3FrameDecompressor decomp = null;
            var clock = Stopwatch.StartNew();
            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    if ((int)resp.StatusCode != 200) throw new WebException("HTTP " + (int)resp.StatusCode);
                    var type = (resp.ContentType ?? "").ToLowerInvariant();
                    if (type.Length > 0 && !(type.Contains("mpeg") || type.Contains("mp3"))) { LastError = "stream format " + type; final = RadioState.Unsupported; return false; }
                    int.TryParse(resp.Headers["icy-metaint"], out var metaInt);
                    using (var icy = new IcyStream(resp.GetResponseStream(), Math.Max(0, metaInt)))
                    {
                        icy.TitleChanged += t => Set(run, () => Title = t);
                        BufferedWaveProvider buffer = null;
                        var pcm = new byte[16384 * 4];
                        bool started = false;
                        while (!run.Cancelled)
                        {
                            if (buffer != null && buffer.BufferedDuration.TotalSeconds > 3) { run.Wake.WaitOne(100); continue; }
                            Mp3Frame frame;
                            try { frame = Mp3Frame.LoadFromStream(icy); }
                            catch (System.IO.EndOfStreamException) { break; }
                            if (frame == null) break;
                            if (decomp == null)
                            {
                                var fmt = new Mp3WaveFormat(frame.SampleRate, frame.ChannelMode == ChannelMode.Mono ? 1 : 2, frame.FrameLength, frame.BitRate);
                                try { decomp = new AcmMp3FrameDecompressor(fmt); }
                                catch (Exception e) { LastError = "no MP3 decoder: " + e.Message; final = RadioState.Unsupported; return false; }
                                buffer = new BufferedWaveProvider(decomp.OutputFormat) { BufferDuration = TimeSpan.FromSeconds(6), DiscardOnBufferOverflow = true };
                                var g = new GainProvider(buffer.ToSampleProvider()) { Target = Gain };
                                g.Current = g.Target;
                                gain = g;
                                try { output = MakeOutput(); output.Init(new SampleToWaveProvider16(g)); }
                                catch (Exception e) { LastError = "no sound device: " + e.Message; final = RadioState.NoDevice; return false; }
                            }
                            int n = decomp.DecompressFrame(frame, pcm, 0);
                            if (n > 0) { buffer.AddSamples(pcm, 0, n); Interlocked.Add(ref DecodedBytes, n); }
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
                try { decomp?.Dispose(); } catch { }
                gain = null;
                try { req.Abort(); } catch { }
            }
            return clock.Elapsed.TotalSeconds >= 30;
        }

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
