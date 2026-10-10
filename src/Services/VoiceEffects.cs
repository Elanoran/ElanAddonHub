using System;
using System.Collections.Generic;
using NAudio.Dsp;

namespace ElansAddonHub.Services
{
    // Voice presets (orc, gnome, ...): plain DSP on the 20 ms mic frames before they are Opus-encoded,
    // so everyone in the Lodge hears the effect and the server needs nothing. Pitch shift + EQ + a few
    // modulation effects; each preset is just a set of numbers (see Presets).
    public class VoicePreset
    {
        public string Id, Name, Blurb;
        public float Semitones;                          // pitch shift
        public float Jitter;                             // random pitch flutter per frame, semitones
        public float VibratoDepth, VibratoHz;            // slow pitch wobble, semitones
        public float TremoloDepth, TremoloHz;            // amplitude wobble, 0..1
        public float Drive;                              // saturation, 0 = clean
        public float HighPassHz;                         // 0 = off
        public float LowDb, MidHz, MidDb, MidQ, HighDb;  // low shelf @ 200 Hz, peak, high shelf @ 4 kHz
        public float EchoMs, EchoMix;                    // 0 = off
        public float ChorusMix;                          // doubled voice, 0 = off
        public override string ToString() => Name;
    }

    public static class VoicePresets
    {
        public static readonly VoicePreset Off = new VoicePreset { Id = "", Name = "Normal voice", Blurb = "No effect" };

        public static readonly List<VoicePreset> All = new List<VoicePreset>
        {
            Off,
            new VoicePreset { Id = "orc", Name = "Orc", Blurb = "Gravelly and heavy", Semitones = -5, Drive = 2.2f, LowDb = 6, MidHz = 150, MidDb = 3, MidQ = 1, HighDb = -3 },
            new VoicePreset { Id = "tauren", Name = "Tauren", Blurb = "Deep, slow and big-chested", Semitones = -8, Drive = 1.2f, LowDb = 8, MidHz = 120, MidDb = 3, MidQ = 1, HighDb = -5, EchoMs = 70, EchoMix = 0.18f },
            new VoicePreset { Id = "gnome", Name = "Gnome", Blurb = "High and frantic", Semitones = 6, LowDb = -6, MidHz = 2500, MidDb = 3, MidQ = 1, HighDb = 4, HighPassHz = 200 },
            new VoicePreset { Id = "goblin", Name = "Goblin", Blurb = "Squeaky and shifty", Semitones = 3.5f, VibratoDepth = 0.35f, VibratoHz = 6, LowDb = -4, MidHz = 1500, MidDb = 6, MidQ = 1.4f, HighDb = 2, HighPassHz = 150 },
            new VoicePreset { Id = "undead", Name = "Undead", Blurb = "Hollow and raspy", Semitones = -1.5f, TremoloDepth = 0.35f, TremoloHz = 4, Drive = 1.4f, HighPassHz = 250, MidHz = 1200, MidDb = 4, MidQ = 2, HighDb = -2, EchoMs = 95, EchoMix = 0.25f },
            new VoicePreset { Id = "murloc", Name = "Murloc", Blurb = "Mrglglgl", Semitones = 3, Jitter = 1.6f, TremoloDepth = 0.45f, TremoloHz = 17, LowDb = -4, MidHz = 1800, MidDb = 4, MidQ = 1.2f, HighDb = 2, HighPassHz = 150 },
        };

        public static VoicePreset Get(string id)
        {
            foreach (var p in All) if (p.Id == (id ?? "")) return p;
            return Off;
        }
    }

    // Stateful per-stream processor. Not thread safe: call from the one audio thread.
    public class VoiceEffects
    {
        const int Fft = 2048, Osamp = 4; // ~32 ms of delay

        readonly float[] buf = new float[VoiceEngine.Frame];
        readonly BiQuadFilter[] filters = new BiQuadFilter[4];
        readonly float[] echo = new float[VoiceEngine.Rate / 2];
        readonly float[] chorus = new float[VoiceEngine.Rate / 10];
        int echoPos, chorusPos;
        double vibPhase, tremPhase, choPhase;
        readonly SmbPitchShifter pitch = new SmbPitchShifter();
        readonly Random rng = new Random();
        VoicePreset current;

        void Reset(VoicePreset p)
        {
            current = p;
            Array.Clear(echo, 0, echo.Length); Array.Clear(chorus, 0, chorus.Length);
            echoPos = chorusPos = 0;
            const float r = VoiceEngine.Rate;
            filters[0] = p.HighPassHz > 0 ? BiQuadFilter.HighPassFilter(r, p.HighPassHz, 0.7f) : null;
            filters[1] = p.LowDb != 0 ? BiQuadFilter.LowShelf(r, 200, 1f, p.LowDb) : null;
            filters[2] = p.MidDb != 0 ? BiQuadFilter.PeakingEQ(r, p.MidHz, p.MidQ <= 0 ? 1f : p.MidQ, p.MidDb) : null;
            filters[3] = p.HighDb != 0 ? BiQuadFilter.HighShelf(r, 4000, 1f, p.HighDb) : null;
        }

        // self-test: a 220 Hz tone through each preset: pitch moved the right way, output sane (no silence, no clipping wall)
        public static string SelfTest()
        {
            var lines = new List<string>(); bool all = true;
            foreach (var p in VoicePresets.All)
            {
                var fx = new VoiceEffects(); var f = new short[VoiceEngine.Frame];
                double ph = 0; int crossings = 0, tail = 0, clipped = 0; double peak = 0; short prev = 0;
                for (int k = 0; k < 50; k++)
                {
                    for (int i = 0; i < f.Length; i++) { f[i] = (short)(Math.Sin(ph) * 8000); ph += 2 * Math.PI * 220 / VoiceEngine.Rate; }
                    fx.Process(f, p);
                    if (k >= 25) for (int i = 0; i < f.Length; i++)
                    {
                        if ((f[i] >= 0) != (prev >= 0)) crossings++;
                        prev = f[i]; tail++; peak = Math.Max(peak, Math.Abs(f[i]));
                        if (Math.Abs((int)f[i]) >= 32767) clipped++;
                    }
                }
                double hz = crossings / 2.0 / (tail / (double)VoiceEngine.Rate);
                double want = 220 * Math.Pow(2, p.Semitones / 12.0);
                // tremolo/jitter/echo smear the count: only the plain presets get the tight pitch check
                bool plainPitch = p.Jitter == 0 && p.TremoloDepth == 0 && p.EchoMs == 0 && p.VibratoDepth == 0;
                bool ok = peak > 500 && clipped < tail / 50 && (p.Semitones == 0 || !plainPitch || Math.Abs(hz - want) < want * 0.25);
                if (p == VoicePresets.Off) ok = peak > 7900 && Math.Abs(hz - 220) < 5;
                all &= ok;
                lines.Add((ok ? "ok   " : "FAIL ") + "voice preset " + p.Name + ": " + hz.ToString("0") + " Hz (want ~" + want.ToString("0") + "), peak " + peak.ToString("0"));
            }
            return "voice presets: " + (all ? "all ok" : "FAILURES") + Environment.NewLine + string.Join(Environment.NewLine, lines);
        }

        // processes a 20 ms frame in place
        public void Process(short[] frame, VoicePreset p)
        {
            if (p == null || p == VoicePresets.Off) return;
            if (p != current) Reset(p);
            int n = VoiceEngine.Frame;
            for (int i = 0; i < n; i++) buf[i] = frame[i] / 32768f;

            // pitch (vibrato and flutter move it a little every frame)
            vibPhase += 2 * Math.PI * p.VibratoHz * n / VoiceEngine.Rate;
            float semis = p.Semitones + (float)(Math.Sin(vibPhase) * p.VibratoDepth) + (p.Jitter > 0 ? (float)((rng.NextDouble() * 2 - 1) * p.Jitter) : 0f);
            if (Math.Abs(semis) > 0.01f)
                pitch.PitchShift((float)Math.Pow(2, semis / 12.0), n, Fft, Osamp, VoiceEngine.Rate, buf);

            var cho = p.ChorusMix;
            float makeup = (float)Math.Pow(10, -(Math.Max(0, p.LowDb) + Math.Max(0, p.MidDb)) / 40.0);
            for (int i = 0; i < n; i++)
            {
                float x = buf[i];
                foreach (var f in filters) if (f != null) x = f.Transform(x);
                if (p.Drive > 0) x = (float)(Math.Tanh(x * (1 + p.Drive)) / Math.Tanh(1 + p.Drive));
                if (p.TremoloDepth > 0)
                {
                    tremPhase += 2 * Math.PI * p.TremoloHz / VoiceEngine.Rate;
                    x *= 1 - p.TremoloDepth * (0.5f + 0.5f * (float)Math.Sin(tremPhase));
                }
                if (cho > 0)
                {
                    chorus[chorusPos] = x;
                    choPhase += 2 * Math.PI * 0.6 / VoiceEngine.Rate;
                    int d = (int)((0.018 + 0.004 * Math.Sin(choPhase)) * VoiceEngine.Rate);
                    x += cho * chorus[(chorusPos - d + chorus.Length) % chorus.Length];
                    chorusPos = (chorusPos + 1) % chorus.Length;
                }
                if (p.EchoMs > 0 && p.EchoMix > 0)
                {
                    int d = (int)(p.EchoMs / 1000.0 * VoiceEngine.Rate);
                    float e = echo[(echoPos - d + echo.Length) % echo.Length];
                    echo[echoPos] = x + e * 0.35f;
                    echoPos = (echoPos + 1) % echo.Length;
                    x += e * p.EchoMix;
                }
                x *= makeup; // the low/mid boosts would otherwise clip loud voices
                // soft knee above 0.6: loud peaks round off instead of clipping
                var ax = Math.Abs(x);
                if (ax > 0.6f) x = Math.Sign(x) * (0.6f + 0.4f * (float)Math.Tanh((ax - 0.6f) / 0.4f));
                frame[i] = (short)(x * 32767f);
            }
        }
    }
}
