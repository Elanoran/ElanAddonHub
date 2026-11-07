using System;
using ElansAddonHub.Services;

namespace ElansAddonHub.Lodge
{
    // The Camp Boss over the game (toast style "boss"): what she shows at a moment of one toast. Pure, so the self-test can check it.
    // The timings are Elan's Toolkit's (UI/Talker.lua): she slides in, types the line at 45 characters a second with her mouth moving,
    // blinks now and then, waits, on every other line turns her head away and back, then slides out.
    // A face sheet is 4 x 4 frames: 0 idle, 1 blink, 2 / 3 mouth half / wide open, 4-15 her head turning away (front to side).
    public static class BossPose
    {
        public const int Idle = 0, Blink = 1, Talk1 = 2, Talk2 = 3, TurnFirst = 4, TurnLast = 15;
        const int TurnSteps = TurnLast - TurnFirst + 1;
        public const double TypeSpeed = 45, MouthEvery = 0.17, BlinkTime = 0.18, BlinkEvery = 3.7;
        public const double Hold = 2.5, HoldPerChar = 0.03, SlideIn = 0.35, SlideOut = 0.3;
        public const double TurnStep = 0.1, TurnHold = 1, TurnWait = 0.8;
        public static double TurnTime => 2 * TurnSteps * TurnStep + TurnHold;

        public struct Pose
        {
            public int Chars;          // how much of the text is typed
            public int From, To;       // face frames: To fades in over From by Blend (From == To = no blend)
            public double Blend;
            public double Down;        // 0 = in place, 1 = fully above (sliding in / out)
            public double Left;        // 0..1: what is left of her time (the bar at the bottom)
        }

        // a toast stays at least as long as a card would (ToastCenter.Stay), however short her line
        public const double MinTotal = 6;

        // seconds from her arrival until she has left
        public static double Duration(int chars, bool turn)
        {
            double typing = chars / TypeSpeed, hold = Hold + HoldPerChar * chars;
            if (turn) hold = Math.Max(hold, TurnWait + TurnTime + 0.3);
            return Math.Max(MinTotal, SlideIn + typing + hold + SlideOut);
        }

        static double Eased(double p) => p * p * (3 - 2 * p);

        // position 0 is idle, 1 to TurnSteps the turn frames
        static (int, int, double) TurnPair(double pos)
        {
            int at = (int)Math.Floor(pos);
            int from = at == 0 ? Idle : TurnFirst + at - 1;
            if (at >= TurnSteps) return (from, from, 0);
            return (from, TurnFirst + at, pos - at);
        }

        // this long after she started turning away; null when she has turned back
        static (int, int, double)? Turn(double t)
        {
            double span = TurnSteps * TurnStep;
            if (t < span) return TurnPair(Eased(t / span) * TurnSteps);
            double back = t - span - TurnHold;
            if (back < 0) return (TurnLast, TurnLast, 0);
            if (back < span) return TurnPair((1 - Eased(back / span)) * TurnSteps);
            return null;
        }

        public static Pose At(double t, int chars, bool turn)
        {
            double total = Duration(chars, turn);
            var p = new Pose { From = Idle, To = Idle, Left = Math.Max(0, Math.Min(1, 1 - t / total)) };
            if (t < SlideIn) { double q = Math.Max(0, t / SlideIn); p.Down = (1 - q) * (1 - q); }
            else if (t > total - SlideOut) { double q = Math.Min(1, (t - (total - SlideOut)) / SlideOut); p.Down = q * q; }
            double typed = t - SlideIn, typeEnd = chars / TypeSpeed;
            p.Chars = typed <= 0 ? 0 : Math.Min(chars, (int)Math.Floor(typed * TypeSpeed));
            if (typed > 0 && typed < typeEnd)
            {
                p.From = p.To = ((int)(typed / MouthEvery)) % 2 == 0 ? Talk1 : Talk2;
                return p;
            }
            if (turn && typed >= typeEnd + TurnWait)
            {
                var tp = Turn(typed - typeEnd - TurnWait);
                if (tp.HasValue) { (p.From, p.To, p.Blend) = tp.Value; return p; }
            }
            if (t % BlinkEvery < BlinkTime) p.From = p.To = Blink;
            return p;
        }
    }

    // Elan's Toolkit tells the Outpost where she stands through notes on the strip (Elan's Hub 1.19.0+, ARCHITECTURE.md "Notes"):
    //   kind 64: id = x (12 bits, her centre, 0..4095 of the screen width) << 12 | top (12 bits, her top edge from the top of the screen),
    //            extra = 1 she is on in game, + face index * 2 (bits 1-3)
    //   kind 65: id = her width, 0..65535 of the screen width
    public static class BossNotes
    {
        public const int Place = 64, Size = 65;

        // true = a setting changed (save them)
        public static bool Apply(Settings s, StripNote n)
        {
            if (s == null || n == null) return false;
            bool changed = false;
            void Set<T>(T now, T was, Action<T> put) { if (!Equals(now, was)) { put(now); changed = true; } }
            if (n.Kind == Place)
            {
                double x = Math.Round(((n.Id >> 12) & 4095) / 4095.0, 4), top = Math.Round((n.Id & 4095) / 4095.0, 4);
                Set<double?>(x, s.BossX, v => s.BossX = v);
                Set<double?>(top, s.BossTop, v => s.BossTop = v);
                Set((n.Extra & 1) != 0, s.BossInGame, v => s.BossInGame = v);
                Set((n.Extra >> 1) & 7, s.BossFace, v => s.BossFace = v);
            }
            else if (n.Kind == Size)
            {
                double w = Math.Round(n.Id / 65535.0, 4);
                if (w >= 0.05 && w <= 0.8) Set<double?>(w, s.BossWidth, v => s.BossWidth = v);
            }
            return changed;
        }

        // where her box goes, as fractions of the game window: centre x, top, width. The default is the Toolkit's (TOP, 0, -70 of a 768 high
        // screen, 330 wide); while she is on in game the overlay stands just below her own box so the two never cover each other.
        public static (double x, double top, double width) Spot(Settings s, double aspect)
        {
            aspect = Math.Max(0.5, aspect);   // width / height of the game window
            double w = s.BossWidth ?? 330.0 / (768 * aspect);
            double x = s.BossX ?? 0.5, top = s.BossTop ?? 70.0 / 768;
            if (s.BossInGame) top += w * (76.0 + 10) / 330 * aspect;   // her own box is 330 x 76 in game; 10 to spare
            return (Math.Max(w / 2, Math.Min(1 - w / 2, x)), Math.Max(0, Math.Min(0.9, top)), w);
        }
    }
}
