using System;
using NAudio.Dsp;

namespace ElansAddonHub.Services
{
    // The mic-side building blocks of voice: noise suppression, automatic gain control + limiter, and the
    // voice-activation gate with its pre-roll. All plain managed DSP, no native code, no devices: the self-test
    // drives them with synthetic buffers. Samples are 16-bit values in floats / shorts, 48 kHz mono, 20 ms frames.

    // Spectral noise suppression. Overlap-add STFT (512-point sqrt-Hann, hop 256 = 5.3 ms, delay 512 samples = 10.7 ms),
    // a per-bin noise-floor tracker (falls at once, rises slowly while speech is present, quickly when it isn't) and a
    // Wiener-style gain with an over-subtraction factor, smoothed over time (fast attack so word onsets survive) and
    // over frequency (so no "musical noise" twinkle). Roughly 15 dB of attenuation on steady noise (fan, hiss, hum).
    public class NoiseSuppressor
    {
        const int N = 512, Hop = N / 2, Bins = N / 2 + 1, LogN = 9;
        public double MaxReductionDb { get; set; } = 20;   // the floor of the gain
        const float OverSub = 2.4f;                         // over-subtraction: take a bit more than the mean noise out
        const int WarmupHops = 24;                          // ~130 ms: pass the signal through while the first noise floor is learnt

        readonly float[] win = new float[N];
        readonly float[] inq = new float[VoiceEngine.Frame + N + Hop];
        readonly float[] ola = new float[N];
        readonly float[] outq = new float[VoiceEngine.Frame + N + Hop + N];
        readonly Complex[] spec = new Complex[N];
        readonly float[] ps = new float[Bins], noise = new float[Bins], gain = new float[Bins], gprev = new float[Bins], graw = new float[Bins];
        int inCount, outCount, hops;

        public NoiseSuppressor()
        {
            for (int i = 0; i < N; i++) win[i] = (float)Math.Sqrt(0.5 * (1 - Math.Cos(2 * Math.PI * i / N))); // periodic sqrt-Hann
            Reset();
        }

        public void Reset()
        {
            Array.Clear(inq, 0, inq.Length); Array.Clear(ola, 0, ola.Length); Array.Clear(outq, 0, outq.Length);
            Array.Clear(ps, 0, Bins); Array.Clear(noise, 0, Bins);
            for (int i = 0; i < Bins; i++) gprev[i] = 1;
            inCount = 0; hops = 0;
            outCount = N; // prefill with silence: output is a constant N samples behind the input
        }

        public static int LatencySamples => N;

        // in place; x.Length samples (any size up to a few frames)
        public void Process(float[] x, int n)
        {
            if (inCount + n > inq.Length) { Reset(); }
            Array.Copy(x, 0, inq, inCount, n); inCount += n;
            while (inCount >= N)
            {
                Hop1();
                Array.Copy(inq, Hop, inq, 0, inCount - Hop); inCount -= Hop;
            }
            if (outCount < n) { for (int i = 0; i < n; i++) x[i] = 0; return; } // cannot happen after the prefill; stay safe
            Array.Copy(outq, 0, x, 0, n);
            Array.Copy(outq, n, outq, 0, outCount - n); outCount -= n;
        }

        void Hop1()
        {
            for (int i = 0; i < N; i++) { spec[i].X = inq[i] * win[i]; spec[i].Y = 0; }
            FastFourierTransform.FFT(true, LogN, spec);

            float floorG = (float)Math.Pow(10, -MaxReductionDb / 20);
            for (int k = 0; k < Bins; k++)
            {
                float p = spec[k].X * spec[k].X + spec[k].Y * spec[k].Y;
                ps[k] = hops == 0 ? p : 0.55f * ps[k] + 0.45f * p;
                // noise floor: the running MEAN of the bin while it looks like noise (a min-tracker sits well below the mean and lets
                // noise through); frozen, creeping up ~1 dB/s, while a bin is far above it (speech); it follows a quieter room down quickly
                if (hops < WarmupHops) noise[k] += (p - noise[k]) / (hops + 1);
                else if (ps[k] < 3 * noise[k]) noise[k] += (p - noise[k]) * (ps[k] < noise[k] ? 0.03f : 0.012f);
                else noise[k] *= 1.0015f;
                float r = OverSub * noise[k] / (ps[k] + 1e-3f);
                float g = 1 - r;
                graw[k] = g < floorG ? floorG : g > 1 ? 1 : g;
            }
            // frequency smoothing, then time smoothing: quick to open (onsets), slower to close
            for (int k = 0; k < Bins; k++)
            {
                float a = graw[k == 0 ? 0 : k - 1], b = graw[k], c = graw[k == Bins - 1 ? k : k + 1];
                float g = 0.25f * a + 0.5f * b + 0.25f * c;
                g = g > gprev[k] ? 0.25f * gprev[k] + 0.75f * g : 0.8f * gprev[k] + 0.2f * g;
                gprev[k] = g;
                gain[k] = hops < WarmupHops ? 1f : g;
            }
            for (int k = 0; k < Bins; k++) { spec[k].X *= gain[k]; spec[k].Y *= gain[k]; }
            for (int k = Bins; k < N; k++) { spec[k].X = spec[N - k].X; spec[k].Y = -spec[N - k].Y; }
            FastFourierTransform.FFT(false, LogN, spec);

            for (int i = 0; i < N; i++) ola[i] += spec[i].X * win[i];
            Array.Copy(ola, 0, outq, outCount, Hop); outCount += Hop;
            Array.Copy(ola, Hop, ola, 0, N - Hop);
            Array.Clear(ola, N - Hop, Hop);
            hops++;
        }
    }

    // Brings a quiet (or loud) mic to a steady speaking level, then a soft limiter keeps whatever the voice changer does under full scale.
    public class AutoGain
    {
        public double TargetDb = -18, MaxGainDb = 22, MinGainDb = -8, GateDb = -54; // below the gate (silence, noise) the gain is held, never raised
        double gain = 1, env = 0;
        public double GainDb => 20 * Math.Log10(gain);

        public void Reset() { gain = 1; env = 0; }

        public void Process(float[] x, int n)
        {
            double sum = 0;
            for (int i = 0; i < n; i++) sum += (double)x[i] * x[i];
            double rms = Math.Sqrt(sum / n) / 32768.0;
            double db = rms > 1e-9 ? 20 * Math.Log10(rms) : -120;
            double g0 = gain, g1 = gain;
            if (db > GateDb)
            {
                // level envelope: follows rises fast, falls slowly, so the loudest part of a word sets the gain
                var lin = rms;
                env = lin > env ? env + (lin - env) * 0.5 : env + (lin - env) * 0.06;
                double envDb = 20 * Math.Log10(Math.Max(env, 1e-9));
                double want = Math.Max(MinGainDb, Math.Min(MaxGainDb, TargetDb - envDb));
                double wantLin = Math.Pow(10, want / 20);
                g1 = wantLin < gain ? gain + (wantLin - gain) * 0.6 : gain + (wantLin - gain) * 0.035; // down quickly, up in ~0.6 s
            }
            else env *= 0.97;
            for (int i = 0; i < n; i++)
            {
                double g = g0 + (g1 - g0) * (i + 1) / n; // ramp across the frame: no steps, no zipper noise
                x[i] = (float)(x[i] * g);
            }
            gain = g1;
        }

        // soft knee above 90 % of full scale; never reaches 32767
        public static void Limit(short[] f, int n)
        {
            const double T = 29500, L = 32760;
            for (int i = 0; i < n; i++)
            {
                double a = Math.Abs((double)f[i]);
                if (a <= T) continue;
                double y = T + (L - T) * Math.Tanh((a - T) / (L - T));
                f[i] = (short)(f[i] < 0 ? -y : y);
            }
        }
    }

    // Voice activation: opens above the threshold, stays open while the level is within `HysteresisDb` of it,
    // and closes only after `HangoverFrames` (300 ms) below that, so word endings aren't cut and a
    // breath between two words doesn't flap the gate.
    public class VoiceGate
    {
        public double ThresholdDb = -45, HysteresisDb = 4;
        public int HangoverFrames = 15; // x 20 ms
        public bool Open { get; private set; }
        int hang;

        public bool Step(double levelDb)
        {
            if (levelDb > ThresholdDb) { Open = true; hang = HangoverFrames; }
            else if (Open)
            {
                if (levelDb > ThresholdDb - HysteresisDb) hang = HangoverFrames;
                else if (--hang <= 0) Open = false;
            }
            return Open;
        }

        public void Reset() { Open = false; hang = 0; }
    }

    // The last PreRollFrames (60 ms) of audio while the gate is closed: when it opens they are sent first, so the first syllable isn't lost.
    public class PreRoll
    {
        public const int Frames = 3;
        readonly short[][] ring = new short[Frames][];
        int head, count;
        public PreRoll() { for (int i = 0; i < Frames; i++) ring[i] = new short[VoiceEngine.Frame]; }
        public int Count => count;

        public void Push(short[] f)
        {
            Array.Copy(f, ring[head], VoiceEngine.Frame);
            head = (head + 1) % Frames; if (count < Frames) count++;
        }

        // oldest first; empties the ring
        public void Drain(Action<short[]> emit)
        {
            for (int i = 0; i < count; i++) emit(ring[(head - count + i + 2 * Frames) % Frames]);
            count = 0;
        }

        public void Clear() { count = 0; }
    }
}
