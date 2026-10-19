using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Concentus;

namespace ElansAddonHub.Services
{
    // Offline checks for the voice pipeline: synthetic buffers and a virtual clock only, no microphone, speakers or network.
    public static class VoiceSelfTest
    {
        const int Frame = VoiceEngine.Frame, Rate = VoiceEngine.Rate;

        static string L(bool ok, string text) => (ok ? "ok   " : "FAIL ") + text;

        public static string Run()
        {
            var lines = new List<string>();
            foreach (var t in new Func<string>[] { Jitter, JitterCalm, JitterDrift, Plc, PeerPlayout, RoomOnly, Noise, Gain, Gate, PreRollTest, EncoderSettings, Codec, Cpu })
            {
                try { lines.Add(t()); }
                catch (Exception e) { lines.Add("FAIL voice test " + t.Method.Name + " threw " + e.GetType().Name + ": " + e.Message); }
            }
            return string.Join(Environment.NewLine, lines);
        }

        // ---- synthetic signals

        static double Db(double rmsFull) => 20 * Math.Log10(Math.Max(rmsFull, 1e-9) / 32768.0);

        static double Rms(IList<float> x, int from, int to)
        {
            double s = 0; for (int i = from; i < to; i++) s += (double)x[i] * x[i];
            return Math.Sqrt(s / Math.Max(1, to - from));
        }

        // white-ish noise (sum of uniforms), rms = `rms` (full scale 32768)
        static void Noise(float[] x, Random r, double rms)
        {
            for (int i = 0; i < x.Length; i++)
            {
                double u = r.NextDouble() + r.NextDouble() + r.NextDouble() + r.NextDouble() - 2; // var 1/3
                x[i] = (float)(u * Math.Sqrt(3) * rms);
            }
        }

        // a voiced-speech stand-in: 20 harmonics of 120 Hz sliding up and down, with a 4 Hz syllable envelope; rms about `rms`
        static void Voiced(float[] x, long start, double rms)
        {
            for (int i = 0; i < x.Length; i++)
            {
                double t = (start + i) / (double)Rate;
                double f0 = 120 + 25 * Math.Sin(2 * Math.PI * 0.7 * t);
                double env = 0.55 + 0.45 * Math.Sin(2 * Math.PI * 4 * t);
                double v = 0;
                for (int h = 1; h <= 20; h++) v += Math.Sin(2 * Math.PI * f0 * h * t) / Math.Pow(h, 1.1);
                x[i] = (float)(v * env * rms * 1.6);
            }
        }

        static short[] ToShort(float[] f) { var s = new short[f.Length]; for (int i = 0; i < f.Length; i++) s[i] = (short)Math.Max(-32768, Math.Min(32767, Math.Round(f[i]))); return s; }

        // ---- jitter buffer on a virtual clock

        struct Arr { public long At; public byte[] Data; }

        class SimResult { public int MaxDepth, MaxTarget, Plc, None, Packets, Squash; public List<int> Depth = new List<int>(); public JitterBuffer Jb; }

        // Sender: 50 packets/s from t=0, `speedup` times faster than the receiver's clock. Arrival = nominal + base + jitter, but in order (TCP).
        // stallAt/stallMs: a pause on the way (packets queue up and then all arrive together).
        static SimResult Simulate(int seconds, int jitterMs, int stallAtMs, int stallMs, double speedup, int seed)
        {
            var rng = new Random(seed);
            var jb = new JitterBuffer();
            var res = new SimResult { Jb = jb };
            long prev = 0;
            var q = new Queue<Arr>();
            int count = (int)(seconds * 50 * speedup);
            for (int i = 0; i < count; i++)
            {
                double nominal = i * 20 / speedup;
                long at = 50 + (long)nominal + (jitterMs > 0 ? rng.Next(jitterMs + 1) : 0);
                if (stallMs > 0 && nominal >= stallAtMs && nominal < stallAtMs + stallMs) at = Math.Max(at, 50 + stallAtMs + stallMs);
                at = Math.Max(at, prev); prev = at;
                q.Enqueue(new Arr { At = at, Data = new byte[] { 1 } });
            }
            for (long now = 0; now < seconds * 1000L; now += 20)
            {
                while (q.Count > 0 && q.Peek().At <= now) { var a = q.Dequeue(); jb.Enqueue(a.Data, a.At); }
                var k = jb.Next(now + 7, out _, out _);
                if (k == PlayKind.Conceal) res.Plc++; else if (k == PlayKind.None) res.None++; else if (k == PlayKind.Squash) res.Squash++; else res.Packets++;
                res.Depth.Add(jb.DepthMs);
                res.MaxDepth = Math.Max(res.MaxDepth, jb.DepthMs);
                res.MaxTarget = Math.Max(res.MaxTarget, (int)jb.TargetMs);
            }
            return res;
        }

        static double Mean(List<int> l, int from, int to) { double s = 0; for (int i = from; i < to; i++) s += l[i]; return s / (to - from); }

        static string Jitter()
        {
            // 0..35 ms of jitter and a 180 ms stall at t=8 s, 30 s run
            var r = Simulate(30, 35, 8000, 180, 1.0, 7);
            var jb = r.Jb;
            int n = r.Depth.Count;
            double tail = Mean(r.Depth, n - 500, n);   // the last 10 s
            bool bounded = r.MaxDepth <= JitterBuffer.MaxQueue * 20 && r.MaxDepth <= JitterBuffer.MaxMs + 200;
            bool near = tail <= jb.TargetMs + 40 && tail >= 0;
            bool plc = jb.Concealed > 0 && jb.Underruns > 0 && r.Plc > 0;
            bool targetOk = jb.TargetMs >= JitterBuffer.MinMs && jb.TargetMs <= JitterBuffer.MaxMs;
            // after the stall the queue is drained back to the target, not left behind as lag
            double after = Mean(r.Depth, 8000 / 20 + 150, 8000 / 20 + 300);
            bool caught = after <= r.MaxTarget + 40 && jb.TargetMs < r.MaxTarget && jb.Squashed > 0;
            return L(bounded && near && plc && targetOk && caught,
                $"voice jitter buffer (35 ms jitter + 180 ms stall): max depth {r.MaxDepth} ms (cap {JitterBuffer.MaxQueue * 20}), mean depth {tail:0} ms vs target {jb.TargetMs:0} ms, "
                + $"3 s after the stall {after:0} ms (target peaked at {r.MaxTarget}, ended {jb.TargetMs:0}), concealed {jb.Concealed} (PLC frames played {r.Plc}), underruns {jb.Underruns}, squashed {jb.Squashed}, jitter est {jb.JitterMs:0.0} ms");
        }

        static string JitterCalm()
        {
            var r = Simulate(40, 2, 0, 0, 1.0, 3);
            var jb = r.Jb;
            bool ok = jb.TargetMs == JitterBuffer.MinMs && jb.Concealed == 0 && Mean(r.Depth, r.Depth.Count - 500, r.Depth.Count) <= JitterBuffer.MinMs + 20;
            return L(ok, $"voice jitter buffer, calm line: target shrinks to {jb.TargetMs:0} ms (min {JitterBuffer.MinMs}), concealed {jb.Concealed}, mean depth {Mean(r.Depth, r.Depth.Count - 500, r.Depth.Count):0} ms");
        }

        static string JitterDrift()
        {
            // the sender's clock runs 0.5 % fast for 3 minutes: without catch-up that is 540 ms of extra lag
            var r = Simulate(180, 10, 0, 0, 1.005, 5);
            var jb = r.Jb;
            double tail = Mean(r.Depth, r.Depth.Count - 500, r.Depth.Count);
            bool ok = r.MaxDepth <= JitterBuffer.MaxMs + 100 && tail <= jb.TargetMs + 50 && jb.Squashed > 0;
            return L(ok, $"voice jitter buffer, 0.5 % clock drift over 3 min: max depth {r.MaxDepth} ms, final depth {tail:0} ms, squashed {jb.Squashed}, dropped {jb.Dropped}");
        }

        // ---- Opus packet loss concealment

        static string Plc()
        {
            var enc = VoiceChain.CreateEncoder(false);
            var dec = OpusCodecFactory.CreateDecoder(Rate, 1);
            var pcm = new short[Frame]; var pkt = new byte[1500]; var o = new short[5760];
            var all = new List<float>(); int plcFrames = 0;
            for (int f = 0; f < 60; f++)
            {
                for (int i = 0; i < Frame; i++) pcm[i] = (short)(Math.Sin(2 * Math.PI * 300 * (f * Frame + i) / Rate) * 9000);
                int len = enc.Encode(pcm, Frame, pkt, pkt.Length);
                bool lost = f >= 30 && f < 32; // two frames missing
                int n;
                if (lost) { n = dec.Decode(ReadOnlySpan<byte>.Empty, o, Frame, false); plcFrames++; }
                else n = dec.Decode(new ReadOnlySpan<byte>(pkt, 0, len), o, o.Length, false);
                for (int i = 0; i < n; i++) all.Add(o[i]);
            }
            double eGap = Rms(all, 30 * Frame, 31 * Frame), eRef = Rms(all, 20 * Frame, 21 * Frame);
            double maxStep = 0; for (int i = 25 * Frame; i < 40 * Frame; i++) maxStep = Math.Max(maxStep, Math.Abs(all[i] - all[i - 1]));
            double natural = 2 * Math.PI * 300 / Rate * 9000;
            bool ok = eGap > eRef * 0.4 && eGap < eRef * 1.5 && maxStep < natural * 2.5;
            return L(ok, $"voice PLC: 2 missing frames concealed with a null packet, gap energy {eGap / eRef * 100:0} % of the tone, largest sample step {maxStep:0} (natural {natural:0})");
        }

        // The whole receive path with a real Peer: Opus packets in with jitter and a stall, 48 kHz float out, level for the ring.
        static string PeerPlayout()
        {
            var enc = VoiceChain.CreateEncoder(false);
            long clock = 0;
            var peer = new VoiceEngine.Peer { Clock = () => clock };
            var pcm = new short[Frame]; var pkt = new byte[1500];
            var rng = new Random(11);
            var arrivals = new Queue<Tuple<long, byte[]>>();
            long prev = 0;
            for (int f = 0; f < 400; f++) // 8 s of a speaking tone with a 140 ms stall at 3 s
            {
                for (int i = 0; i < Frame; i++) pcm[i] = (short)(Math.Sin(2 * Math.PI * 220 * (f * Frame + i) / Rate) * 7000);
                int len = enc.Encode(pcm, Frame, pkt, pkt.Length);
                long at = 30 + f * 20 + rng.Next(25);
                if (f * 20 >= 3000 && f * 20 < 3140) at = Math.Max(at, 30 + 3140);
                at = Math.Max(at, prev); prev = at;
                arrivals.Enqueue(Tuple.Create(at, pkt.Take(len).ToArray()));
            }
            var outp = new List<float>(); var buf = new float[Frame]; int flips = 0; bool was = false; var levels = new List<float>();
            for (clock = 0; clock < 9500; clock += 20)
            {
                while (arrivals.Count > 0 && arrivals.Peek().Item1 <= clock) { var a = arrivals.Dequeue(); peer.Jb.Enqueue(a.Item2, a.Item1); }
                peer.Read(buf, 0, Frame);
                outp.AddRange(buf);
                if (peer.Speaking != was) { flips++; was = peer.Speaking; }
                levels.Add(peer.Level);
            }
            double maxStep = 0; int from = 60 * Frame;
            for (int i = from; i < 380 * Frame && i < outp.Count; i++) maxStep = Math.Max(maxStep, Math.Abs(outp[i] - outp[i - 1]));
            double natural = 2 * Math.PI * 220 / Rate * 7000 / 32768;
            double rms = Rms(outp, 100 * Frame, 140 * Frame);
            bool smooth = maxStep < natural * 3;
            // speaking ring: on once when the voice starts, off once when it stops: never flickers on jitter or the stall
            bool ring = flips <= 2;
            return L(smooth && ring && rms > 0.1 && peer.Jb.Concealed > 0,
                $"voice receive path: {outp.Count / Frame} frames out, largest step {maxStep / natural:0.0}x natural, speaking flips {flips} (want 2), concealed {peer.Jb.Concealed}, level in speech {Db(rms * 32768):0} dBFS");
        }

        // 3.1.1: a hostile Lodge can't make the Outpost build a decoder and a mixer input per invented sender id: only the roster of my room
        // is played, never more than MaxPeers, and the ones who leave are removed
        static string RoomOnly()
        {
            using (var e = new VoiceEngine())
            {
                e.UseSilentMixerForTest();
                var pkt = new byte[] { 0xF8, 0xFF, 0xFE };   // any bytes: Incoming only queues them
                e.SetRoomMembers(new[] { 1, 2 });
                for (int id = 3; id < 2000; id++) e.Incoming(id, pkt, 0, pkt.Length);
                bool strangers = e.PeerCount == 0 && e.MixerInputCount == 0;
                e.Incoming(1, pkt, 0, pkt.Length); e.Incoming(2, pkt, 0, pkt.Length); e.Incoming(1, pkt, 0, pkt.Length);
                bool members = e.PeerCount == 2 && e.MixerInputCount == 2;
                e.SetRoomMembers(new[] { 2 });
                bool left = e.PeerCount == 1 && e.MixerInputCount == 1 && !e.IsSpeaking(1);
                e.Incoming(1, pkt, 0, pkt.Length);
                bool leftStaysOut = e.PeerCount == 1;
                e.SetRoomMembers(Enumerable.Range(1, 500));
                for (int id = 1; id <= 500; id++) e.Incoming(id, pkt, 0, pkt.Length);
                bool capped = e.PeerCount == VoiceEngine.MaxPeers && e.MixerInputCount == VoiceEngine.MaxPeers;
                e.SetRoomMembers(null);
                bool empty = e.PeerCount == 0 && e.MixerInputCount == 0;
                return L(strangers && members && left && leftStaysOut && capped && empty,
                    $"voice: only my room's roster is played (strangers {strangers}, members {members}, a leaver removed {left} and stays out {leftStaysOut}, capped at {VoiceEngine.MaxPeers} {capped}, empty room {empty})");
            }
        }

        // ---- noise suppression


        static string Noise()
        {
            var rng = new Random(2);
            const int frames = 250; // 5 s
            var ns = new NoiseSuppressor();
            // A: noise only at -50 dBFS
            var inN = new List<float>(); var outN = new List<float>(); var f = new float[Frame];
            for (int k = 0; k < frames; k++) { Noise(f, rng, 32768 * Math.Pow(10, -50 / 20.0)); inN.AddRange(f); ns.Process(f, Frame); outN.AddRange(f); }
            int a0 = 150 * Frame, a1 = frames * Frame;
            double redDb = Db(Rms(inN, a0, a1)) - Db(Rms(outN, a0, a1));
            // B: the same noise under a voiced signal at -22 dBFS, versus the clean voice
            ns = new NoiseSuppressor();
            var clean = new List<float>(); var outV = new List<float>(); var withN = new List<float>(); var v = new float[Frame]; var nz = new float[Frame];
            for (int k = 0; k < frames; k++)
            {
                Voiced(v, (long)k * Frame, 32768 * Math.Pow(10, -22 / 20.0));
                Noise(nz, rng, 32768 * Math.Pow(10, -50 / 20.0));
                clean.AddRange(v);
                for (int i = 0; i < Frame; i++) f[i] = v[i] + nz[i];
                withN.AddRange(f);
                ns.Process(f, Frame); outV.AddRange(f);
            }
            int d = NoiseSuppressor.LatencySamples;
            double clean0 = Db(Rms(clean, 100 * Frame, frames * Frame - Frame)), outDb = Db(Rms(outV, 100 * Frame + d, frames * Frame + d - Frame));
            double voiceDiff = outDb - clean0;
            // B2: a quiet voice (-34 dBFS, 16 dB over the noise) must survive too
            ns = new NoiseSuppressor(); var cq = new List<float>(); var oq = new List<float>();
            for (int k = 0; k < frames; k++)
            {
                Voiced(v, (long)k * Frame, 32768 * Math.Pow(10, -34 / 20.0)); Noise(nz, rng, 32768 * Math.Pow(10, -50 / 20.0));
                cq.AddRange(v); for (int i = 0; i < Frame; i++) f[i] = v[i] + nz[i];
                ns.Process(f, Frame); oq.AddRange(f);
            }
            double quietDiff = Db(Rms(oq, 100 * Frame + d, frames * Frame + d - Frame)) - Db(Rms(cq, 100 * Frame, frames * Frame - Frame));
            // C: the same voice with no noise at all passes (nearly) untouched
            ns = new NoiseSuppressor(); var outC = new List<float>();
            for (int k = 0; k < frames; k++) { Voiced(v, (long)k * Frame, 32768 * Math.Pow(10, -22 / 20.0)); Array.Copy(v, f, Frame); ns.Process(f, Frame); outC.AddRange(f); }
            double cleanPass = Db(Rms(outC, 100 * Frame + d, frames * Frame + d - Frame)) - clean0;
            // D: a pause after speech: noise during the gap is pulled down as well
            return L(redDb >= 10 && Math.Abs(voiceDiff) <= 2 && Math.Abs(quietDiff) <= 2.5 && Math.Abs(cleanPass) <= 1,
                $"voice noise suppression: noise floor -50 dBFS reduced by {redDb:0.0} dB (want >= 10); voice at -22 dBFS in the same noise changes by {voiceDiff:+0.0;-0.0} dB (want within 2); a quiet -34 dBFS voice {quietDiff:+0.0;-0.0} dB; clean voice changes {cleanPass:+0.0;-0.0} dB");
        }

        // ---- AGC + limiter

        static string Gain()
        {
            var gain = new AutoGain(); var f = new float[Frame]; var outs = new List<float>();
            for (int k = 0; k < 300; k++) { Voiced(f, (long)k * Frame, 32768 * Math.Pow(10, -42 / 20.0)); gain.Process(f, Frame); outs.AddRange(f); }
            double quiet = Db(Rms(outs, 200 * Frame, 300 * Frame)); // after 4 s
            double boost = gain.GainDb;
            // loud input: -6 dBFS rms with peaks that would clip; the limiter keeps it under full scale
            gain = new AutoGain(); double peak = 0; var sh = new short[Frame]; var loud = new List<float>();
            for (int k = 0; k < 200; k++)
            {
                Voiced(f, (long)k * Frame, 32768 * Math.Pow(10, -6 / 20.0)); gain.Process(f, Frame);
                for (int i = 0; i < Frame; i++) sh[i] = (short)Math.Max(-32768, Math.Min(32767, Math.Round(f[i] * 1.0)));
                AutoGain.Limit(sh, Frame);
                if (k >= 100) { loud.AddRange(sh.Select(x => (float)x)); for (int i = 0; i < Frame; i++) peak = Math.Max(peak, Math.Abs((int)sh[i])); }
            }
            double loudDb = Db(Rms(loud, 0, loud.Count));
            // silence (noise below the gate) must not be pumped up
            gain = new AutoGain(); var rng = new Random(4);
            for (int k = 0; k < 200; k++) { Noise(f, rng, 32768 * Math.Pow(10, -62 / 20.0)); gain.Process(f, Frame); }
            bool ok = Math.Abs(quiet - (-20)) <= 4 && boost > 12 && peak < 32767 && loudDb < -9 && gain.GainDb < 1;
            return L(ok, $"voice AGC: a -42 dBFS voice reaches {quiet:0.0} dBFS (target {gain.TargetDb:0}, gain +{boost:0.0} dB); a -6 dBFS voice ends at {loudDb:0.0} dBFS with peak {peak:0} (< 32767); -62 dBFS room tone gets {gain.GainDb:0.0} dB (held)");
        }

        // ---- voice activation

        static string Gate()
        {
            var g = new VoiceGate { ThresholdDb = -45 };
            // loud for 10 frames, then silence: should stay open for the 300 ms hangover and close right after
            int framesOpenAfter = 0; bool opened = false;
            for (int i = 0; i < 10; i++) opened |= g.Step(-30);
            for (int i = 0; i < 60; i++) { if (g.Step(-70)) framesOpenAfter++; }
            // a level in the hysteresis band (-47) keeps it open, and holds the hangover back
            g.Reset(); g.Step(-30); int bandOpen = 0;
            for (int i = 0; i < 40; i++) if (g.Step(-47)) bandOpen++;
            // a breath between words (5 frames = 100 ms of silence) doesn't close it
            g.Reset(); g.Step(-30); bool flapped = false;
            for (int i = 0; i < 5; i++) if (!g.Step(-70)) flapped = true;
            g.Step(-30);
            for (int i = 0; i < 5; i++) if (!g.Step(-70)) flapped = true;
            bool ok = opened && framesOpenAfter >= 14 && framesOpenAfter <= 15 && bandOpen == 40 && !flapped;
            return L(ok, $"voice activation: stays open {framesOpenAfter * 20} ms after the voice stops (want ~300), holds through the -47 dB hysteresis band ({bandOpen}/40), no flapping on a 100 ms breath: {!flapped}");
        }

        static string PreRollTest()
        {
            var chain = new VoiceChain { NoiseSuppress = false, Agc = false, ThresholdDb = -40 };
            var sent = new List<int>();   // packet index at which each packet went out
            chain.Send = (p, n) => sent.Add(sent.Count);
            var f = new short[Frame];
            // 30 quiet frames, then speech starts at frame 30
            int firstLoud = 30, sentBefore = 0;
            for (int k = 0; k < 60; k++)
            {
                for (int i = 0; i < Frame; i++) f[i] = (short)(k >= firstLoud ? Math.Sin(2 * Math.PI * 200 * (k * Frame + i) / Rate) * 6000 : Math.Sin(2 * Math.PI * 200 * i / Rate) * 20);
                if (k == firstLoud) sentBefore = sent.Count;
                chain.Process(f);
            }
            int atOpen = sent.Count - sentBefore; // packets that went out because of frames 30..59
            bool ok = sentBefore == 0 && chain.PreRollSent == 3 && sent.Count == 3 + 30;
            return L(ok, $"voice pre-roll: nothing sent while quiet ({sentBefore}); on the first loud frame {chain.PreRollSent} earlier frames (= {chain.PreRollSent * 20} ms) go out first, {sent.Count} packets in total");
        }

        // ---- encoder

        static string EncoderSettings()
        {
            var e = VoiceChain.CreateEncoder(false);
            bool std = e.Bitrate == 32000 && e.SignalType == Concentus.Enums.OpusSignal.OPUS_SIGNAL_VOICE && e.UseInbandFEC && e.PacketLossPercent == 5 && !e.UseDTX && e.Complexity == 6;
            VoiceChain.Configure(e, true);
            bool hi = e.Bitrate == 48000;
            var chain = new VoiceChain(); chain.SetQuality(true);
            bool live = chain.Encoder.Bitrate == 48000;
            // the 48 kbps stream is really bigger
            int Bytes(bool high)
            {
                var enc = VoiceChain.CreateEncoder(high); var f = new short[Frame]; var pk = new byte[1500]; int tot = 0; var r = new Random(1);
                for (int k = 0; k < 100; k++) { for (int i = 0; i < Frame; i++) f[i] = (short)(Math.Sin(2 * Math.PI * 180 * (k * Frame + i) / Rate) * 6000 + Math.Sin(2 * Math.PI * 1500 * i / Rate) * 2000 + r.Next(-800, 800)); tot += enc.Encode(f, Frame, pk, pk.Length); }
                return tot / 100;
            }
            int lo = Bytes(false), hi2 = Bytes(true);
            return L(std && hi && live && hi2 > lo, $"voice encoder: VOICE signal, 32 kbps, in-band FEC on with 5 % expected loss, DTX off, complexity 6 -> {std}; High quality 48 kbps -> {hi}, live switch -> {live}; speech-like frame {lo} B at 32k, {hi2} B at 48k");
        }

        static string Codec()
        {
            var s = VoiceEngine.CodecSelfTest();
            return L(s.Contains("ok=True"), "voice " + s);
        }

        // ---- CPU

        static string Cpu()
        {
            string Measure(VoicePreset p, out double mean, out double p99)
            {
                var chain = new VoiceChain { Preset = p }; chain.Send = (b, n) => { };
                var f = new short[Frame]; var v = new float[Frame]; var rng = new Random(9); var nz = new float[Frame];
                var times = new List<double>();
                for (int k = 0; k < 700; k++)
                {
                    Voiced(v, (long)k * Frame, 32768 * Math.Pow(10, -26 / 20.0)); Noise(nz, rng, 32768 * Math.Pow(10, -50 / 20.0));
                    for (int i = 0; i < Frame; i++) f[i] = (short)(v[i] + nz[i]);
                    long t0 = Stopwatch.GetTimestamp();
                    chain.Process(f);
                    if (k >= 100) times.Add((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
                }
                times.Sort(); mean = times.Average(); p99 = times[(int)(times.Count * 0.99)];
                return $"{p.Name}: mean {mean:0.00} ms, p99 {p99:0.00} ms";
            }
            string a = Measure(VoicePresets.Off, out var m1, out var p1);
            string b = Measure(VoicePresets.Get("orc"), out var m2, out var p2);
            // receive: decode + level for one speaker, per 20 ms
            var enc = VoiceChain.CreateEncoder(false); var peer = new VoiceEngine.Peer(); var pcm = new short[Frame]; var pk = new byte[1500];
            for (int i = 0; i < Frame; i++) pcm[i] = (short)(Math.Sin(2 * Math.PI * 200 * i / Rate) * 6000);
            int len = enc.Encode(pcm, Frame, pk, pk.Length); var one = pk.Take(len).ToArray();
            long now = 0; peer.Clock = () => now; var buf = new float[Frame]; var rx = new List<double>();
            for (int k = 0; k < 400; k++)
            {
                now += 20; peer.Jb.Enqueue(one, now); peer.Jb.Enqueue(one, now); peer.Jb.Enqueue(one, now);
                long t0 = Stopwatch.GetTimestamp(); peer.Read(buf, 0, Frame); if (k >= 50) rx.Add((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
            }
            double rxMean = rx.Average();
            // Opus encode cost by complexity, on speech-like frames (why the shipped value is what it is)
            var cx = new System.Text.StringBuilder();
            foreach (var c in new[] { 4, 6, 8, 10 })
            {
                var e2 = VoiceChain.CreateEncoder(false); e2.Complexity = c;
                var fr = new short[Frame]; var pk2 = new byte[1500]; var vv = new float[Frame]; double tot = 0; int bytes = 0;
                for (int k = 0; k < 300; k++)
                {
                    Voiced(vv, (long)k * Frame, 32768 * Math.Pow(10, -24 / 20.0)); for (int i = 0; i < Frame; i++) fr[i] = (short)vv[i];
                    long t0 = Stopwatch.GetTimestamp(); int n = e2.Encode(fr, Frame, pk2, pk2.Length);
                    if (k >= 50) { tot += (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency; bytes += n; }
                }
                cx.Append($" c{c} {tot / 250:0.00} ms/{bytes / 250} B;");
            }
            bool ok = m1 < 2 && p1 < 5 && p2 < 8 && m2 < 4 && rxMean < 1;
            return L(ok, $"voice cpu per 20 ms frame (budget 2 ms normal voice): {a}; {b}; receive per speaker {rxMean:0.00} ms; Opus encode by complexity:{cx}");
        }
    }
}
