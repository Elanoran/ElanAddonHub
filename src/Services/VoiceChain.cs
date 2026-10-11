using System;
using System.Diagnostics;
using Concentus;
using Concentus.Enums;

namespace ElansAddonHub.Services
{
    // Everything between "a 20 ms mic frame" and "an Opus packet handed to the network", with no audio device involved
    // (the engine feeds it from the mic; the self-test feeds it synthetic buffers).
    //
    //   raw frame --level (voice activation looks at the raw mic level, so the Sensitivity slider means what it always did)
    //             --> noise suppression --> AGC --> voice changer --> soft limiter --> gate (+ pre-roll) --> Opus --> Send
    //
    // Why this order: suppression goes first because it needs the natural spectrum of speech and room noise; after a pitch
    // shift, drive or echo the noise is smeared into something it can no longer tell from voice, and the effects would also
    // amplify it. AGC comes before the effects so presets like Orc (drive) always start from a steady level, and the limiter is
    // last because the effects (drive, echo, EQ boost) are what can push a loud frame past full scale.
    public class VoiceChain
    {
        public const int StandardBitrate = 32000, HighBitrate = 48000;
        public bool NoiseSuppress { get; set; } = true;
        public bool Agc { get; set; } = true;
        public VoicePreset Preset { get; set; } = VoicePresets.Off;
        public bool Muted { get; set; }
        public bool PushToTalk { get; set; }
        public Func<bool> PttDown;
        public double ThresholdDb { get => gate.ThresholdDb; set => gate.ThresholdDb = value; }
        public Action<byte[], int> Send;
        public Action<short[]> Monitor;       // the processed frame, before the gate (for "Hear myself")

        public bool Transmitting { get; private set; }
        public double LevelDb { get; private set; } = -90;
        public double GainDb => agc.GainDb;
        public int FramesSent { get; private set; }
        public int PreRollSent { get; private set; }
        public double ProcessMs { get; private set; } // smoothed time per frame, whole chain including Opus

        public IOpusEncoder Encoder { get; private set; }
        readonly NoiseSuppressor ns = new NoiseSuppressor();
        readonly AutoGain agc = new AutoGain();
        readonly VoiceEffects effects = new VoiceEffects();
        readonly VoiceGate gate = new VoiceGate();
        readonly PreRoll preroll = new PreRoll();
        readonly float[] f = new float[VoiceEngine.Frame];
        readonly byte[] packet = new byte[1500];
        bool lastNs;

        public VoiceChain(bool highQuality = false)
        {
            Encoder = CreateEncoder(highQuality);
        }

        // Opus settings for speech over a lossy-looking link
        public static IOpusEncoder CreateEncoder(bool highQuality)
        {
            var e = OpusCodecFactory.CreateEncoder(VoiceEngine.Rate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
            Configure(e, highQuality);
            return e;
        }

        public static void Configure(IOpusEncoder e, bool highQuality)
        {
            e.Bitrate = highQuality ? HighBitrate : StandardBitrate;
            e.SignalType = OpusSignal.OPUS_SIGNAL_VOICE;
            e.Complexity = 6;               // measured: see the "voice cpu" self-test line; 8 costs ~1.5x for no audible gain on speech at 32 kbps
            e.UseVBR = true;
            e.UseInbandFEC = true;          // the packet carries a low-rate copy of the previous frame: free insurance after a reconnect
            e.PacketLossPercent = 5;        // tells the encoder how much of the bit budget to spend on that
            e.UseDTX = false;               // silence is simply not sent (gate / push-to-talk); see VoiceEngine for why DTX isn't used
        }

        public void SetQuality(bool highQuality) => Encoder.Bitrate = highQuality ? HighBitrate : StandardBitrate;

        // one 20 ms frame, in place
        public void Process(short[] frame)
        {
            long t0 = Stopwatch.GetTimestamp();
            int n = VoiceEngine.Frame;
            double sum = 0;
            for (int i = 0; i < n; i++) sum += (double)frame[i] * frame[i];
            var rms = Math.Sqrt(sum / n) / 32768.0;
            LevelDb = rms > 0 ? 20 * Math.Log10(rms) : -90;

            if (NoiseSuppress || Agc)
            {
                for (int i = 0; i < n; i++) f[i] = frame[i];
                if (NoiseSuppress) { if (!lastNs) ns.Reset(); ns.Process(f, n); }
                if (Agc) agc.Process(f, n);
                for (int i = 0; i < n; i++) { var v = f[i]; frame[i] = (short)(v > 32767 ? 32767 : v < -32768 ? -32768 : Math.Round(v)); }
            }
            lastNs = NoiseSuppress;
            try { effects.Process(frame, Preset); } catch (Exception e) { Util.Log("voice effect: " + e.Message); }
            if (Agc) AutoGain.Limit(frame, n);
            Monitor?.Invoke(frame);

            var send = Send; // Dispose may clear it from another thread
            bool open;
            if (Muted || send == null) { open = false; gate.Reset(); preroll.Clear(); }
            else if (PushToTalk) { open = PttDown != null && PttDown(); gate.Reset(); preroll.Clear(); }
            else
            {
                bool was = gate.Open;
                open = gate.Step(LevelDb);
                if (open && !was) // just opened: the 60 ms before it go first
                    preroll.Drain(p => { if (Encode(p, send)) PreRollSent++; });
                else if (!open) preroll.Push(frame);
            }
            Transmitting = open;
            if (open) Encode(frame, send);

            ProcessMs = ProcessMs * 0.95 + (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency * 0.05;
        }

        bool Encode(short[] pcm, Action<byte[], int> send)
        {
            try
            {
                int len = Encoder.Encode(pcm, VoiceEngine.Frame, packet, packet.Length);
                if (len > 0) { send(packet, len); FramesSent++; return true; }
            }
            catch (Exception e) { Util.Log("encode: " + e.Message); }
            return false;
        }
    }
}
