using System;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace ElansAddonHub.Services
{
    // Small synthesized chimes (no sound files): someone joins/leaves your voice room, you get mentioned.
    public static class Sounds
    {
        public static bool Off;
        const int Rate = 44100;

        public static void Join() => Play(new[] { 660.0, 880.0 });
        public static void Leave() => Play(new[] { 880.0, 587.0 });
        public static void Mention() => Play(new[] { 988.0, 1319.0, 988.0 }, 0.07);

        static void Play(double[] notes, double noteLen = 0.09)
        {
            if (Off) return;
            try
            {
                int per = (int)(Rate * noteLen);
                var samples = new float[per * notes.Length];
                for (int n = 0; n < notes.Length; n++)
                    for (int i = 0; i < per; i++)
                    {
                        double env = Math.Min(1, i / (Rate * 0.006)) * Math.Exp(-4.0 * i / per); // soft attack, quick decay
                        samples[n * per + i] = (float)(Math.Sin(2 * Math.PI * notes[n] * i / Rate) * 0.18 * env);
                    }
                var provider = new RawSourceWaveStream(ToBytes(samples), 0, samples.Length * 4, WaveFormat.CreateIeeeFloatWaveFormat(Rate, 1));
                var output = new WaveOutEvent { DesiredLatency = 80 };
                output.Init(provider);
                output.PlaybackStopped += (s, e) => { output.Dispose(); provider.Dispose(); };
                output.Play();
            }
            catch (Exception e) { Util.Log("sound: " + e.Message); }
        }

        static byte[] ToBytes(float[] s)
        {
            var b = new byte[s.Length * 4];
            Buffer.BlockCopy(s, 0, b, 0, b.Length);
            return b;
        }
    }
}
