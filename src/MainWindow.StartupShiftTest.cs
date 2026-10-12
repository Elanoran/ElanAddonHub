using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // Start-up layout shift: the Addons page is put back into its cold-start state (no list, nothing scanned), the (fake) update server
    // answers slowly, and the Y of every section header and card slot is sampled every 50 ms until everything has arrived (the update check,
    // the other-addons scan and lookups, then the late arrivals: GitHub stats, link suggestions, a found update). The first sample is the
    // "first painted frame". ELANSHUB_DESIGN_SHOTS=startup writes the report plus a few frames (st-*.png); the "startup" self-test section
    // asserts it.
    public partial class MainWindow
    {
        public class ShiftRun
        {
            public Dictionary<string, double[]> First = new Dictionary<string, double[]>();     // key -> { y, height } in the first frame
            public Dictionary<string, double[]> Settled = new Dictionary<string, double[]>();
            public Dictionary<string, double[]> BeforeLate;                                      // the page once the check and the scan were done, just before stats / suggestions / a found update arrive
            public Dictionary<string, double> Peak = new Dictionary<string, double>();          // key -> largest |y - first y| at any sample
            public Dictionary<string, double[]> Seen = new Dictionary<string, double[]>();      // key -> { y, height } the first time it was on screen
            public Dictionary<string, double[]> Drift = new Dictionary<string, double[]>();     // key -> { largest |dy|, largest |dh| } since it was first on screen
            public int Frames, OursFirst, OursFinal, OthersFirst, OthersFinal;
            public bool TimedOut;
            public List<string> Notes = new List<string>();

            // keys that moved more than 1 px (settled vs first frame, or at any sample in between); keys that were not there in the first frame are "appears"
            public IEnumerable<string> Moved() => Settled.Keys.Where(k => First.ContainsKey(k) && (Math.Abs(Settled[k][0] - First[k][0]) > 1 || (Peak.ContainsKey(k) && Peak[k] > 1)));
            public IEnumerable<string> Appeared() => Settled.Keys.Where(k => !First.ContainsKey(k));
            public IEnumerable<string> Vanished() => First.Keys.Where(k => !Settled.ContainsKey(k));
            public IEnumerable<string> HeightChanged() => Settled.Keys.Where(k => First.ContainsKey(k) && Math.Abs(Settled[k][1] - First[k][1]) > 1);

            public void Track(Dictionary<string, double[]> s)
            {
                foreach (var kv in s)
                {
                    if (!Seen.ContainsKey(kv.Key)) { Seen[kv.Key] = kv.Value; continue; }
                    var sn = Seen[kv.Key]; var dr = Drift.TryGetValue(kv.Key, out var o) ? o : new double[2];
                    dr[0] = Math.Max(dr[0], Math.Abs(kv.Value[0] - sn[0])); dr[1] = Math.Max(dr[1], Math.Abs(kv.Value[1] - sn[1])); Drift[kv.Key] = dr;
                }
            }

            public string Report()
            {
                var sb = new StringBuilder();
                sb.AppendLine($"frames={Frames} ours {OursFirst}->{OursFinal} others {OthersFirst}->{OthersFinal}{(TimedOut ? " TIMED OUT" : "")}");
                foreach (var k in Moved().OrderBy(x => x, StringComparer.Ordinal))
                    sb.AppendLine($"  MOVED    {k}: y {First[k][0]:0.#} -> {Settled[k][0]:0.#} (settled {Settled[k][0] - First[k][0]:+0.#;-0.#;0}, peak {Peak[k]:0.#})");
                foreach (var k in HeightChanged().OrderBy(x => x, StringComparer.Ordinal))
                    sb.AppendLine($"  HEIGHT   {k}: {First[k][1]:0.#} -> {Settled[k][1]:0.#}");
                foreach (var k in Appeared().OrderBy(x => x, StringComparer.Ordinal))
                    sb.AppendLine($"  APPEARS  {k}: not in the first frame; first seen y {Seen[k][0]:0.#} h {Seen[k][1]:0.#}, ends y {Settled[k][0]:0.#} h {Settled[k][1]:0.#}");
                foreach (var k in Drift.Keys.Where(k => Drift[k][0] > 1 || Drift[k][1] > 1).OrderBy(x => x, StringComparer.Ordinal))
                    sb.AppendLine($"  DRIFTS   {k}: after it was first on screen it moved up to {Drift[k][0]:0.#} px and changed height by up to {Drift[k][1]:0.#} px");
                foreach (var k in Vanished().OrderBy(x => x, StringComparer.Ordinal))
                    sb.AppendLine($"  VANISHES {k}: was at y {First[k][0]:0.#} (h {First[k][1]:0.#})");
                foreach (var n in Notes) sb.AppendLine("  " + n);
                return sb.ToString();
            }
        }

        static double[] ShiftPos(FrameworkElement el, FrameworkElement rel)
        {
            if (el == null || !el.IsVisible || !el.IsLoaded || el.ActualHeight <= 0) return null;
            try { var p = el.TranslatePoint(new Point(0, 0), rel); return new[] { Math.Round(p.Y, 2), Math.Round(el.ActualHeight, 2) }; }
            catch { return null; }
        }

        // the element standing in for slot i of a list: the real card when there is one, else the skeleton card
        static FrameworkElement ShiftSlot(ItemsControl real, int realCount, Panel skeleton, int i)
        {
            if (i < realCount && real.ItemContainerGenerator.ContainerFromIndex(i) is DependencyObject cp && FindDown<FrameworkElement>(cp) is FrameworkElement c && c.IsVisible && c.ActualHeight > 0) return c;   // the card itself (the container also holds its margin)
            if (skeleton != null && skeleton.IsVisible && i < skeleton.Children.Count) return skeleton.Children[i] as FrameworkElement;
            return null;
        }

        Dictionary<string, double[]> ShiftSample()
        {
            UpdateLayout();
            var d = new Dictionary<string, double[]>();
            void Put(string k, double[] v) { if (v != null) d[k] = v; }
            Put("page", ShiftPos(AddonsPage, (FrameworkElement)Content));
            Put("refresh-bar", ShiftPos(RefreshBar, AddonsRoot));
            var oskel = FindName("OtherSkeleton") as Panel;
            for (int i = 0; i < 60; i++)
            {
                var e = ShiftSlot(Cards, cards.Count, CardsSkeleton, i);
                if (e == null) break;
                Put("ours.slot" + i, ShiftPos(e, AddonsRoot));
            }
            if (OtherPanel.IsVisible) Put("others.header", ShiftPos(OtherTitle, AddonsRoot));
            for (int i = 0; i < 80; i++)
            {
                var e = ShiftSlot(OtherCards, others.Count, oskel, i);
                if (e == null) break;
                Put("others.slot" + i, ShiftPos(e, AddonsRoot));
            }
            return d;
        }

        // late arrivals that real use produces after the lists are up: GitHub stats on our cards, WoWInterface suggestions, an update found by a lookup
        async Task ShiftLateArrivals()
        {
            foreach (var c in cards) c.SetStats(new AddonStats { Released = DateTime.UtcNow.AddDays(-3), Downloads = 1234, Releases = 7 });
            await Task.Delay(120);
            var locals = others.Where(c => c.State == TpState.Local && !c.Entry.IsDev && c.Entry.Cf == null).Take(2).ToList();
            foreach (var c in locals)
            {
                c.Suggested = new Suggestion { File = new WowiFile { Id = "1", Name = c.Entry.Title, Author = "someone", Version = "9.9" }, Exact = true, FlavorFits = true, Hint = "exact folder match" };
                c.Refresh();
            }
            UpdateOthersHeader();
            await Task.Delay(120);
            var upd = others.FirstOrDefault(c => c.State == TpState.Local && !c.Entry.IsDev && c.Entry.Cf == null && !locals.Contains(c));
            if (upd != null) { upd.Remote = new RemoteInfo { Source = "GitHub", Version = "99.0", Url = "https://example.invalid/x.zip", Asset = "x.zip" }; upd.Refresh(); UpdateOthersHeader(); }
            await Task.Delay(120);
        }

        // Cold start with a slow update server. frames: optional (ms -> file) pictures taken when that time has passed.
        public async Task<ShiftRun> ShiftColdStart(int serverDelayMs, bool hubNewer, string frameDir = null, int[] frameAtMs = null)
        {
            var run = new ShiftRun();
            FakeUpdateServer.Start();
            FakeUpdateServer.DelayMs = serverDelayMs;
            FakeUpdateServer.HubVersion = hubNewer ? "99.0.0" : null;
            try
            {
                for (int i = 0; i < 100 && (checking || scanning || checkingOthers || suggesting); i++) await Task.Delay(100);
                ShowTab("addons");
                AddonsPage.ScrollToTop();
                ResetColdState();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                await Task.Delay(60);
                run.OursFirst = CardsSkeleton.IsVisible ? CardsSkeleton.Children.Count : cards.Count;
                run.OthersFirst = (FindName("OtherSkeleton") as Panel)?.Children.Count ?? 0;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                run.First = ShiftSample();
                run.Track(run.First);
                var frames = frameAtMs?.ToList() ?? new List<int>();
                int idleSamples = 0;
                var started = CheckNow();
                bool late = false;
                while (sw.ElapsedMilliseconds < 20000)
                {
                    await Task.Delay(50);
                    var s = ShiftSample();
                    run.Frames++;
                    run.Track(s);
                    foreach (var kv in s)
                        if (run.First.TryGetValue(kv.Key, out var f)) { var dv = Math.Abs(kv.Value[0] - f[0]); if (!run.Peak.TryGetValue(kv.Key, out var pk) || dv > pk) run.Peak[kv.Key] = dv; }
                    while (frameDir != null && frames.Count > 0 && sw.ElapsedMilliseconds >= frames[0])
                    {
                        SnapshotScaled(Path.Combine(frameDir, $"st-{frames[0]:0000}.png"), 1);
                        frames.RemoveAt(0);
                    }
                    bool busy = checking || scanning || checkingOthers || !started.IsCompleted;
                    idleSamples = busy ? 0 : idleSamples + 1;
                    if (idleSamples >= 3 && !late) { late = true; run.BeforeLate = ShiftSample(); await ShiftLateArrivals(); idleSamples = 0; run.Notes.Add($"late arrivals applied at {sw.ElapsedMilliseconds} ms"); continue; }
                    if (late && idleSamples >= 10) break;
                }
                run.TimedOut = sw.ElapsedMilliseconds >= 20000;
                if (frameDir != null) { SnapshotScaled(Path.Combine(frameDir, "st-final.png"), 1); }
                run.Settled = ShiftSample();
                run.Track(run.Settled);
                run.OursFinal = cards.Count; run.OthersFinal = others.Count;
            }
            finally { FakeUpdateServer.DelayMs = 0; FakeUpdateServer.HubVersion = null; }
            return run;
        }

        async Task StartupShots(string dir)
        {
            try
            {
                Width = 920; Height = 760;
                var r = await ShiftColdStart(700, true, dir, new[] { 0, 300, 800, 1100, 1600 });
                File.WriteAllText(Path.Combine(dir, "startup-shift.txt"), r.Report());
                File.WriteAllText(Path.Combine(dir, "result.txt"), "startup shift shots done\r\n" + r.Report());
            }
            catch (Exception e) { File.WriteAllText(Path.Combine(dir, "result.txt"), "startup shots crashed: " + e); }
            Quit();
        }
    }
}
