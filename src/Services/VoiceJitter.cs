using System;
using System.Collections.Generic;

namespace ElansAddonHub.Services
{
    public enum PlayKind { None, Packet, Squash, Conceal }

    // One remote speaker's jitter buffer. Pure logic: arrival and playout times are passed in (milliseconds), so the
    // self-test can run it on a virtual clock. Network side calls Enqueue; the audio thread calls Next once per 20 ms
    // frame it needs.
    //
    // Frames carry no sequence number (the relay forwards the raw Opus packet and the server protocol must not change),
    // but the link is a WebSocket over TCP: frames arrive in order and none are lost, they only arrive late and in bursts.
    // So "reordering" is not needed and "loss" is lateness: a frame that is not there when its slot comes up is concealed
    // with Opus PLC, and whatever piles up afterwards is caught up by squashing two frames into one with a cross-fade.
    //
    // Target delay (the cushion before playout starts, and the depth we aim to hold) is adaptive, 40..120 ms:
    //   grows at once to (recent peak lateness + 20 ms), +20 ms after every underrun,
    //   shrinks 5 ms per second once nothing has been late for 5 s.
    public class JitterBuffer
    {
        public const int FrameMs = 20, MinMs = 40, MaxMs = 120, MaxQueue = 25, PlcFrames = 3;
        struct Item { public byte[] Data; public long At; }

        readonly Queue<Item> q = new Queue<Item>();
        readonly object gate = new object();

        public double TargetMs { get; private set; } = 60;
        public double JitterMs { get; private set; }   // RFC 3550 style inter-arrival jitter
        public int Concealed, Underruns, Squashed, Dropped, Received;
        public int DepthMs { get { lock (gate) return q.Count * FrameMs; } }

        // arrival model: transit = arrival - nominal send time, lateness = transit - smallest transit seen in this burst of speech
        long lastArrival = long.MinValue; int burstIdx; long burstBase; double prevTransit, minTransit, peakLate;
        const int SkipFirst = 4; // the pre-roll burst (3 old frames + the current one) arrives at once: not jitter

        // playout state
        bool playing; int plcLeft, pendingConceal, sinceSquash; long lastPlayAt = long.MinValue, lastGrowAt, lastShrinkAt;

        public void Enqueue(byte[] data, long now)
        {
            lock (gate)
            {
                Received++;
                if (lastArrival == long.MinValue || now - lastArrival > 200) { burstIdx = 0; burstBase = now; minTransit = double.MaxValue; prevTransit = 0; peakLate = 0; }
                lastArrival = now;
                double transit = now - (burstBase + burstIdx * (double)FrameMs);
                if (burstIdx >= SkipFirst)
                {
                    if (burstIdx > SkipFirst) JitterMs += (Math.Abs(transit - prevTransit) - JitterMs) / 16;
                    minTransit = Math.Min(minTransit + 0.02, transit); // slow leak: sender and speaker clocks aren't the same clock
                    double late = transit - minTransit;
                    peakLate = Math.Max(late, peakLate * 0.985);        // forgets over ~1 s
                    double wanted = Math.Max(MinMs, Math.Min(MaxMs, peakLate + 20));
                    if (wanted > TargetMs) { TargetMs = wanted; lastGrowAt = now; }
                }
                prevTransit = transit;
                burstIdx++;
                q.Enqueue(new Item { Data = data, At = now });
                while (q.Count > MaxQueue) { q.Dequeue(); Dropped++; } // hard cap: a stall can never turn into half a second of lag
            }
        }

        // Called once per output frame. a (and b for Squash) are the Opus packets to decode.
        public PlayKind Next(long now, out byte[] a, out byte[] b)
        {
            a = b = null;
            lock (gate)
            {
                // slow shrink while the line is calm
                if (now - lastGrowAt > 5000 && now - lastShrinkAt >= 1000 && TargetMs > MinMs && TargetMs > Math.Max(MinMs, peakLate + 20) + 2)
                { TargetMs = Math.Max(MinMs, TargetMs - 5); lastShrinkAt = now; }

                if (playing && q.Count == 0) { playing = false; plcLeft = PlcFrames; }
                if (!playing)
                {
                    bool ready = q.Count > 0 && (q.Count * FrameMs >= TargetMs || now - q.Peek().At >= TargetMs);
                    if (!ready)
                    {
                        if (plcLeft > 0) { plcLeft--; pendingConceal++; return PlayKind.Conceal; }
                        return PlayKind.None;
                    }
                    playing = true; plcLeft = 0; sinceSquash = 0;
                    if (pendingConceal > 0)
                    {
                        if (now - lastPlayAt <= 300) // a stall in the middle of speech, not the end of a sentence
                        {
                            Concealed += pendingConceal; Underruns++;
                            TargetMs = Math.Min(MaxMs, TargetMs + 20); lastGrowAt = now;
                        }
                        pendingConceal = 0;
                    }
                }
                lastPlayAt = now;
                sinceSquash++;
                // too much piled up: squash two frames into one now and then (about every other frame, so it's a quick glide, not a jump)
                if (q.Count >= 2 && q.Count * FrameMs > TargetMs + 60 && sinceSquash >= 2)
                {
                    a = q.Dequeue().Data; b = q.Dequeue().Data; sinceSquash = 0; Squashed++;
                    return PlayKind.Squash;
                }
                a = q.Dequeue().Data;
                return PlayKind.Packet;
            }
        }

        public void Reset()
        {
            lock (gate) { q.Clear(); playing = false; plcLeft = 0; pendingConceal = 0; lastArrival = long.MinValue; }
        }
    }
}
