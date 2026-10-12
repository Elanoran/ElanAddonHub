using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Concentus;
using Concentus.Enums;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace ElansAddonHub.Services
{
    // Mic -> Opus (48 kHz mono, 20 ms frames) -> Lodge; incoming Opus -> one jitter buffer per
    // speaker -> mixer -> speakers. Voice activation or push-to-talk (works while WoW has focus).
    public class Device
    {
        public int Id { get; }
        public string Name { get; }
        public Device(int id, string name) { Id = id; Name = name; }
        public override string ToString() => Name; // what the dropdown shows
    }

    public class VoiceEngine : IDisposable
    {
        public const int Rate = 48000, Frame = 960; // 20 ms
        static readonly WaveFormat Pcm = new WaveFormat(Rate, 16, 1);

        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
        [DllImport("winmm.dll")] static extern int waveOutGetNumDevs();
        [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "waveOutGetDevCapsW")]
        static extern int waveOutGetDevCaps(IntPtr deviceId, ref WaveOutCaps caps, int size);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WaveOutCaps
        {
            public ushort wMid, wPid;
            public uint vDriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname;
            public uint dwFormats;
            public ushort wChannels, wReserved1;
            public uint dwSupport;
        }
        public static bool KeyDown(int vk) => vk > 0 && (GetAsyncKeyState(vk) & 0x8000) != 0;

        // One remote speaker: a jitter buffer fed by the network thread and drained by the audio thread, which decodes
        // (so the Opus decoder is only ever touched from one thread). Silence between sentences costs nothing.
        public class Peer : ISampleProvider
        {
            public int Id;
            public readonly JitterBuffer Jb = new JitterBuffer();
            public readonly IOpusDecoder Decoder = OpusCodecFactory.CreateDecoder(Rate, 1);
            public volatile float Volume = 1f;
            public volatile float Level;        // 0..1, smooth: what the speaking ring shows
            public volatile bool Speaking;
            public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 1);
            readonly short[] a = new short[5760], b = new short[5760];
            readonly float[] pcm = new float[5760 * 2];
            int pos, len;
            public Func<long> Clock = NowMs;

            public int Read(float[] buffer, int offset, int count)
            {
                int got = 0;
                while (got < count)
                {
                    if (pos >= len) { Fill(); }
                    int n = Math.Min(count - got, len - pos);
                    for (int i = 0; i < n; i++) buffer[offset + got + i] = pcm[pos + i] * Volume;
                    pos += n; got += n;
                }
                return count; // always full: a mixer input that returns short is dropped
            }

            // decode (or conceal, or skip) the next 20 ms into pcm
            public void Fill()
            {
                pos = 0; len = Frame;
                int n = 0;
                try
                {
                    var kind = Jb.Next(Clock(), out var p1, out var p2);
                    switch (kind)
                    {
                        case PlayKind.Packet:
                            n = Decoder.Decode(new ReadOnlySpan<byte>(p1), a, a.Length, false);
                            for (int i = 0; i < n; i++) pcm[i] = a[i] / 32768f;
                            break;
                        case PlayKind.Conceal:
                            n = Decoder.Decode(ReadOnlySpan<byte>.Empty, a, Frame, false); // packet loss concealment: carries on from the last sound
                            for (int i = 0; i < n; i++) pcm[i] = a[i] / 32768f;
                            break;
                        case PlayKind.Squash:
                            // two frames into one: cross-fade the first into the second so there is no click and no gap
                            int n1 = Decoder.Decode(new ReadOnlySpan<byte>(p1), a, a.Length, false);
                            int n2 = Decoder.Decode(new ReadOnlySpan<byte>(p2), b, b.Length, false);
                            n = Math.Min(n1, n2);
                            for (int i = 0; i < n; i++) { float t = (i + 0.5f) / n; pcm[i] = (a[i] * (1 - t) + b[i] * t) / 32768f; }
                            break;
                    }
                }
                catch (Exception e) { Util.Log("decode: " + e.Message); n = 0; }
                if (n <= 0) { n = Frame; Array.Clear(pcm, 0, n); }
                len = n;
                double sum = 0;
                for (int i = 0; i < n; i++) sum += pcm[i] * pcm[i];
                double db = sum > 0 ? 10 * Math.Log10(sum / n) : -120;
                float lvl = (float)Math.Max(0, Math.Min(1, (db + 55) / 30)); // -55 dBFS .. -25 dBFS
                Level = lvl > Level ? Level + (lvl - Level) * 0.6f : Level + (lvl - Level) * 0.12f; // quick up, ~150 ms down: no flicker between syllables
                Speaking = Speaking ? Level > 0.08f : Level > 0.2f;
            }

            public float[] Pcm => pcm;
        }

        public static long NowMs() => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;

        IWaveIn mic;
        WaveOutEvent speakers;
        MixingSampleProvider mixer;
        VolumeSampleProvider volume;
        readonly ConcurrentDictionary<int, Peer> peers = new ConcurrentDictionary<int, Peer>();
        readonly short[] frame = new short[Frame];
        int frameFill;
        readonly VoiceChain chain;
        BufferedWaveProvider monitorBuf;

        public VoiceEngine()
        {
            chain = new VoiceChain();
            chain.PttDown = () => KeyDown(PttKey);
            chain.Monitor = f =>
            {
                if (!Monitor || monitorBuf == null) return;
                var mb = new byte[Frame * 2];
                Buffer.BlockCopy(f, 0, mb, 0, mb.Length);
                monitorBuf.AddSamples(mb, 0, mb.Length);
            };
        }

        public Action<byte[], int> Send { get => chain.Send; set => chain.Send = value; }   // encoded packet out
        public bool Muted { get => chain.Muted; set => chain.Muted = value; }
        public bool Deafened { get; set; }
        public bool PushToTalk { get => chain.PushToTalk; set => chain.PushToTalk = value; }
        public int PttKey { get; set; } = 0x05;    // mouse button 4
        public double ThresholdDb { get => chain.ThresholdDb; set => chain.ThresholdDb = value; }
        public VoicePreset Preset { get => chain.Preset; set => chain.Preset = value; } // voice changer (orc, gnome, ...), applied before encoding
        public bool NoiseSuppression { get => chain.NoiseSuppress; set => chain.NoiseSuppress = value; }
        public bool AutoGain { get => chain.Agc; set => chain.Agc = value; }
        public bool HighQuality { get => highQuality; set { highQuality = value; chain.SetQuality(value); } } // 48 kbps instead of 32
        bool highQuality;
        public bool Monitor { get; set; }                           // hear yourself (with the effect): use headphones
        public bool Transmitting => chain.Transmitting;
        public double LevelDb => chain.LevelDb;
        public double ProcessMs => chain.ProcessMs;
        public float MyLevel => (float)chain.Level01;
        public string MicError { get; private set; }
        public int FramesSent => chain.FramesSent;

        public float Volume { get => volume?.Volume ?? 1f; set { if (volume != null) volume.Volume = value; } }

        // test hook: feed this instead of a microphone (a tone), so voice can be tested without recording anyone
        public static bool TestTone;

        public void Start(int inputDevice, int outputDevice, float vol)
        {
            mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(Rate, 1)) { ReadFully = true };
            monitorBuf = new BufferedWaveProvider(Pcm) { DiscardOnBufferOverflow = true, BufferDuration = TimeSpan.FromMilliseconds(400) };
            mixer.AddMixerInput(monitorBuf.ToSampleProvider());
            volume = new VolumeSampleProvider(mixer) { Volume = vol };
            try
            {
                speakers = new WaveOutEvent { DeviceNumber = outputDevice, DesiredLatency = 120, NumberOfBuffers = 3 };
                speakers.Init(Sounds.Muted ? new VolumeSampleProvider(volume) { Volume = 0f } : (ISampleProvider)volume);   // self-test: same pipeline, no sound
                speakers.Play();
            }
            catch (Exception e) { Util.Log("speakers: " + e.Message); }

            if (TestTone) { StartTone(); return; }
            try
            {
                var w = new WaveInEvent { DeviceNumber = inputDevice, WaveFormat = Pcm, BufferMilliseconds = 20, NumberOfBuffers = 4 };
                w.DataAvailable += (s, e) => OnMic(e.Buffer, e.BytesRecorded);
                w.StartRecording();
                mic = w;
            }
            catch (Exception e)
            {
                MicError = "No microphone found";
                Util.Log("mic: " + e.Message);
            }
        }

        System.Threading.Timer tone;
        void StartTone()
        {
            double phase = 0;
            var buf = new byte[Frame * 2];
            tone = new System.Threading.Timer(_ =>
            {
                for (int i = 0; i < Frame; i++)
                {
                    var s = (short)(Math.Sin(phase) * 8000);
                    phase += 2 * Math.PI * 440 / Rate;
                    buf[i * 2] = (byte)s;
                    buf[i * 2 + 1] = (byte)(s >> 8);
                }
                OnMic(buf, buf.Length);
            }, null, 0, 20);
        }

        readonly object encodeLock = new object();

        void OnMic(byte[] buf, int count)
        {
            // one frame at a time: the chain isn't thread-safe and audio callbacks can overlap
            lock (encodeLock)
            {
                for (int i = 0; i + 1 < count; i += 2)
                {
                    frame[frameFill++] = (short)(buf[i] | (buf[i + 1] << 8));
                    if (frameFill == Frame) { frameFill = 0; chain.Process(frame); }
                }
            }
        }

        // network thread
        public void Incoming(int sender, byte[] data, int offset, int count)
        {
            if (Deafened || mixer == null || count <= 0) return;
            var p = peers.GetOrAdd(sender, _ =>
            {
                var peer = new Peer { Id = sender, Volume = PeerVolume?.Invoke(sender) ?? 1f };
                mixer.AddMixerInput(peer);
                return peer;
            });
            var copy = new byte[count];
            Buffer.BlockCopy(data, offset, copy, 0, count);
            p.Jb.Enqueue(copy, NowMs());
        }

        public Func<int, float> PeerVolume; // per-person volume, asked when someone starts talking

        public void SetPeerVolume(int sender, float v)
        {
            if (peers.TryGetValue(sender, out var p)) p.Volume = v;
        }

        // decoded loudness, smoothed (not network events): 0..1 for the speaking ring, and a yes/no with hysteresis
        public bool IsSpeaking(int sender) => peers.TryGetValue(sender, out var p) && p.Speaking;
        public float SpeakLevel(int sender) => peers.TryGetValue(sender, out var p) ? p.Level : 0f;

        public class PeerStat { public int Id; public double JitterMs, BufferMs, TargetMs; public int Concealed, Underruns, Squashed, Dropped, Received; }

        // Settings > Voice > Connection quality
        public List<PeerStat> Stats()
        {
            var l = new List<PeerStat>();
            foreach (var kv in peers)
            {
                var j = kv.Value.Jb;
                if (j.Received == 0) continue;
                l.Add(new PeerStat { Id = kv.Key, JitterMs = j.JitterMs, BufferMs = j.DepthMs, TargetMs = j.TargetMs, Concealed = j.Concealed, Underruns = j.Underruns, Squashed = j.Squashed, Dropped = j.Dropped, Received = j.Received });
            }
            return l;
        }

        public void RemovePeer(int sender)
        {
            if (peers.TryRemove(sender, out var p)) mixer?.RemoveMixerInput(p);
        }

        public void Dispose()
        {
            Send = null;
            try { tone?.Dispose(); } catch { }
            try { mic?.StopRecording(); mic?.Dispose(); } catch { }
            try { speakers?.Stop(); speakers?.Dispose(); } catch { }
            foreach (var id in new List<int>(peers.Keys)) RemovePeer(id);
            mic = null; speakers = null; mixer = null;
        }

        // ---- devices and keys

        public static List<Device> InputDevices()
        {
            var l = new List<Device> { new Device(-1, "Default microphone") };
            try { for (int i = 0; i < WaveInEvent.DeviceCount; i++) l.Add(new Device(i, WaveInEvent.GetCapabilities(i).ProductName)); } catch { }
            return l;
        }

        public static List<Device> OutputDevices()
        {
            var l = new List<Device> { new Device(-1, "Default speakers") };
            try
            {
                int n = waveOutGetNumDevs();
                for (int i = 0; i < n; i++)
                {
                    var caps = new WaveOutCaps();
                    if (waveOutGetDevCaps((IntPtr)i, ref caps, Marshal.SizeOf(caps)) == 0) l.Add(new Device(i, caps.szPname));
                }
            }
            catch { }
            return l;
        }

        public static string KeyName(int vk)
        {
            switch (vk)
            {
                case 0x04: return "Middle mouse";
                case 0x05: return "Mouse 4";
                case 0x06: return "Mouse 5";
                case 0x02: return "Right mouse";
            }
            var k = System.Windows.Input.KeyInterop.KeyFromVirtualKey(vk);
            return k == System.Windows.Input.Key.None ? $"Key {vk}" : k.ToString();
        }

        // Codec self-check for the test run: encode a tone, decode it, compare energy.
        public static string CodecSelfTest()
        {
            var enc = VoiceChain.CreateEncoder(false);
            var dec = OpusCodecFactory.CreateDecoder(Rate, 1);
            var pcm = new short[Frame];
            var outPcm = new short[5760];
            var pkt = new byte[1500];
            double inE = 0, outE = 0;
            int bytes = 0;
            for (int f = 0; f < 50; f++)
            {
                for (int i = 0; i < Frame; i++) pcm[i] = (short)(Math.Sin(2 * Math.PI * 440 * (f * Frame + i) / Rate) * 8000);
                int len = enc.Encode(pcm, Frame, pkt, pkt.Length);
                bytes += len;
                int n = dec.Decode(new ReadOnlySpan<byte>(pkt, 0, len), outPcm, outPcm.Length, false);
                if (f < 10) continue; // codec warm-up
                for (int i = 0; i < Frame; i++) inE += (double)pcm[i] * pcm[i];
                for (int i = 0; i < n; i++) outE += (double)outPcm[i] * outPcm[i];
            }
            return $"codec ok={outE / inE > 0.5 && outE / inE < 2}, energy ratio {outE / inE:0.00}, {bytes / 50} bytes/frame";
        }
    }
}
