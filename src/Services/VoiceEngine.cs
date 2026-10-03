using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

        class Peer
        {
            public IOpusDecoder Decoder;
            public BufferedWaveProvider Buffer;
            public ISampleProvider Input;
            public VolumeSampleProvider Volume;
            public DateTime Last;
        }

        IWaveIn mic;
        WaveOutEvent speakers;
        MixingSampleProvider mixer;
        VolumeSampleProvider volume;
        IOpusEncoder encoder;
        readonly ConcurrentDictionary<int, Peer> peers = new ConcurrentDictionary<int, Peer>();
        readonly short[] frame = new short[Frame];
        int frameFill;
        readonly byte[] packet = new byte[1500];
        readonly short[] decoded = new short[5760];
        DateTime holdUntil;

        public Action<byte[], int> Send;           // encoded packet out
        public bool Muted { get; set; }
        public bool Deafened { get; set; }
        public bool PushToTalk { get; set; }
        public int PttKey { get; set; } = 0x05;    // mouse button 4
        public double ThresholdDb { get; set; } = -45;
        public bool Transmitting { get; private set; }
        public double LevelDb { get; private set; } = -90;
        public string MicError { get; private set; }
        public int FramesSent { get; private set; }

        public float Volume { get => volume?.Volume ?? 1f; set { if (volume != null) volume.Volume = value; } }

        // test hook: feed this instead of a microphone (a tone), so voice can be tested without recording anyone
        public static bool TestTone;

        public void Start(int inputDevice, int outputDevice, float vol)
        {
            encoder = OpusCodecFactory.CreateEncoder(Rate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
            encoder.Bitrate = 32000;
            encoder.SignalType = OpusSignal.OPUS_SIGNAL_VOICE;
            encoder.Complexity = 8;

            mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(Rate, 1)) { ReadFully = true };
            volume = new VolumeSampleProvider(mixer) { Volume = vol };
            try
            {
                speakers = new WaveOutEvent { DeviceNumber = outputDevice, DesiredLatency = 120, NumberOfBuffers = 3 };
                speakers.Init(volume);
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
            // one frame at a time: the encoder isn't thread-safe and audio callbacks can overlap
            lock (encodeLock)
            {
                for (int i = 0; i + 1 < count; i += 2)
                {
                    frame[frameFill++] = (short)(buf[i] | (buf[i + 1] << 8));
                    if (frameFill == Frame) { frameFill = 0; ProcessFrame(); }
                }
            }
        }

        void ProcessFrame()
        {
            double sum = 0;
            for (int i = 0; i < Frame; i++) sum += (double)frame[i] * frame[i];
            var rms = Math.Sqrt(sum / Frame) / 32768.0;
            LevelDb = rms > 0 ? 20 * Math.Log10(rms) : -90;

            var send = Send; // Dispose may clear it from another thread
            bool open;
            if (Muted || send == null) open = false;
            else if (PushToTalk) open = KeyDown(PttKey);
            else
            {
                if (LevelDb > ThresholdDb) holdUntil = DateTime.UtcNow.AddMilliseconds(350); // keep the ends of words
                open = DateTime.UtcNow < holdUntil;
            }
            Transmitting = open;
            if (!open) return;
            try
            {
                int len = encoder.Encode(frame, Frame, packet, packet.Length);
                if (len > 0) { send(packet, len); FramesSent++; }
            }
            catch (Exception e) { Util.Log("encode: " + e.Message); }
        }

        // network thread
        public void Incoming(int sender, byte[] data, int offset, int count)
        {
            if (Deafened || mixer == null) return;
            var p = peers.GetOrAdd(sender, _ =>
            {
                var peer = new Peer
                {
                    Decoder = OpusCodecFactory.CreateDecoder(Rate, 1),
                    Buffer = new BufferedWaveProvider(Pcm) { BufferDuration = TimeSpan.FromSeconds(1), DiscardOnBufferOverflow = true },
                };
                peer.Volume = new VolumeSampleProvider(peer.Buffer.ToSampleProvider()) { Volume = PeerVolume?.Invoke(sender) ?? 1f };
                peer.Input = peer.Volume;
                mixer.AddMixerInput(peer.Input);
                return peer;
            });
            try
            {
                int n = p.Decoder.Decode(new ReadOnlySpan<byte>(data, offset, count), decoded, decoded.Length, false);
                if (n <= 0) return;
                // a new burst of talking: start with 60 ms of cushion so network jitter doesn't stutter
                if (p.Buffer.BufferedBytes == 0) p.Buffer.AddSamples(new byte[Rate / 1000 * 60 * 2], 0, Rate / 1000 * 60 * 2);
                var bytes = new byte[n * 2];
                Buffer.BlockCopy(decoded, 0, bytes, 0, bytes.Length);
                p.Buffer.AddSamples(bytes, 0, bytes.Length);
                p.Last = DateTime.UtcNow;
            }
            catch (Exception e) { Util.Log("decode: " + e.Message); }
        }

        public Func<int, float> PeerVolume; // per-person volume, asked when someone starts talking

        public void SetPeerVolume(int sender, float v)
        {
            if (peers.TryGetValue(sender, out var p)) p.Volume.Volume = v;
        }

        public bool IsSpeaking(int sender) =>
            peers.TryGetValue(sender, out var p) && (DateTime.UtcNow - p.Last).TotalMilliseconds < 300;

        public void RemovePeer(int sender)
        {
            if (peers.TryRemove(sender, out var p)) mixer?.RemoveMixerInput(p.Input);
        }

        public void Dispose()
        {
            Send = null;
            try { tone?.Dispose(); } catch { }
            try { mic?.StopRecording(); mic?.Dispose(); } catch { }
            try { speakers?.Stop(); speakers?.Dispose(); } catch { }
            foreach (var id in new List<int>(peers.Keys)) RemovePeer(id);
            mic = null; speakers = null; mixer = null;
            Transmitting = false;
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
            var enc = OpusCodecFactory.CreateEncoder(Rate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
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
