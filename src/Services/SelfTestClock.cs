using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;

namespace ElansAddonHub
{
    // Self-test timing: every Check() calls Mark(what), which records how long the work since the previous check took. The run prints the
    // slowest stretches at the end ("slow checks"), so what to trim is measured rather than guessed. Test-only; the app never calls it.
    public static class SelfTestClock
    {
        static readonly Stopwatch Sw = Stopwatch.StartNew();
        static double last;
        static string section = "setup";
        static readonly List<Tuple<double, string, string>> Gaps = new List<Tuple<double, string, string>>();

        public static void Section(string name) { section = name; last = Sw.Elapsed.TotalSeconds; }

        // called at the top of a check: the time since the previous check (or the section start) belongs to the work that led to this one
        public static void Mark(string what)
        {
            var now = Sw.Elapsed.TotalSeconds;
            var gap = now - last; last = now;
            if (gap >= 0.5) lock (Gaps) Gaps.Add(Tuple.Create(gap, section, what ?? ""));
        }

        // PNG renders for docs/design are not assertions: they run only when ELANSHUB_SHOTS=1 (or a picture run sets another *_SHOTS variable); a -Full run
        // does not take them any more (they cost minutes of renders, encodes and settle waits that check nothing)
        public static bool Full;
        public static int ShotsTaken, ShotsSkipped; public static double ShotSeconds;
        static bool? envShots;
        public static bool ShotsOn
        {
            get
            {
                var flag = Environment.GetEnvironmentVariable("ELANSHUB_SHOTS");
                if (flag == "0") return false;   // forces them off, even in a picture run
                if (flag == "1") return true;
                if (envShots.HasValue) return envShots.Value;
                envShots = false;
                // the design / browser / video picture runs exist to make pictures
                foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
                    if (((string)e.Key).EndsWith("_SHOTS", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(e.Value as string)) envShots = true;
                return envShots.Value;
            }
        }

        public static string Report(int top = 150)
        {
            List<Tuple<double, string, string>> g;
            lock (Gaps) g = Gaps.OrderByDescending(x => x.Item1).Take(top).ToList();
            return "pictures: " + ShotsTaken + " taken in " + ShotSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s, " + ShotsSkipped + " skipped (ELANSHUB_SHOTS=1 takes them)\r\n"
                + string.Join("\r\n", g.Select(x => "slow " + x.Item1.ToString("0.0", CultureInfo.InvariantCulture) + " s before [" + x.Item2 + "] " + (x.Item3.Length > 110 ? x.Item3.Substring(0, 110) : x.Item3)));
        }
    }
}
