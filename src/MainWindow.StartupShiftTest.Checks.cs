using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // The "startup" self-test section: cold starts of the Addons page with a slow (fake) update server, asserting that nothing above the
    // bottom of a list moves (see MainWindow.StartupShiftTest.cs for the sampler).
    public partial class MainWindow
    {
        static int SlotIndex(string key) => int.Parse(key.Substring(key.IndexOf(".slot") + 5));

        // what must hold for one cold start, given what the lists showed first (skeletons) and what they ended with
        string ShiftVerdict(string name, ShiftRun r)
        {
            var bad = new List<string>();
            int oursKeep = Math.Min(r.OursFirst, r.OursFinal), othersKeep = Math.Min(r.OthersFirst, r.OthersFinal);
            // a row of the grid more or fewer = one card + the gap; the section below moves as one block
            int gridCols = Math.Max(1, CardColumnsForTest);
            double oursShift = ((r.OursFinal + gridCols - 1) / gridCols - (r.OursFirst + gridCols - 1) / gridCols) * ((double)FindResource("Size.Card.Addon") + 12);
            double Delta(string k) => r.Settled[k][0] - r.First[k][0];
            if (r.TimedOut) bad.Add("the run timed out");
            if (r.OursFirst == 0 || r.OthersFirst == 0) bad.Add($"no placeholders in the first frame (ours {r.OursFirst}, others {r.OthersFirst})");
            if (!r.First.ContainsKey("others.header")) bad.Add("the Other addons header is missing in the first frame");
            // above everything: the page itself (a banner would push it), the check row, our cards that have a placeholder
            foreach (var k in new[] { "page", "refresh-bar" })
                if (r.First.ContainsKey(k) && r.Settled.ContainsKey(k) && (Math.Abs(Delta(k)) > 1 || r.Peak[k] > 1)) bad.Add($"{k} moved {Delta(k):0.#} px (peak {r.Peak[k]:0.#})");
            for (int i = 0; i < oursKeep; i++)
            {
                var k = "ours.slot" + i;
                if (!r.Settled.ContainsKey(k) || !r.First.ContainsKey(k)) { bad.Add(k + " missing"); continue; }
                if (Math.Abs(Delta(k)) > 1 || r.Peak[k] > 1) bad.Add($"{k} moved {Delta(k):0.#} px (peak {r.Peak[k]:0.#})");
                if (Math.Abs(r.Settled[k][1] - r.First[k][1]) > 1) bad.Add($"{k} height {r.First[k][1]:0.#} -> {r.Settled[k][1]:0.#}");
            }
            // only the bottom of a list may come or go
            foreach (var k in r.Appeared().Concat(r.Vanished()).Distinct())
            {
                if (k.StartsWith("ours.slot") && SlotIndex(k) >= oursKeep) continue;
                if (k.StartsWith("others.slot") && SlotIndex(k) >= othersKeep) continue;
                bad.Add(k + (r.First.ContainsKey(k) ? " vanished" : " appeared") + " (not at the bottom of a list)");
            }
            // the section below a list that gained or lost rows moves as one block; with equal counts nothing moves at all
            if (r.First.ContainsKey("others.header") && r.Settled.ContainsKey("others.header"))
            {
                var keys = new List<string> { "others.header" };
                for (int i = 0; i < othersKeep; i++) keys.Add("others.slot" + i);
                foreach (var k in keys)
                {
                    if (!r.Settled.ContainsKey(k) || !r.First.ContainsKey(k)) { bad.Add(k + " missing"); continue; }
                    if (Math.Abs(Delta(k) - oursShift) > 1) bad.Add($"{k} moved {Delta(k):0.#} px, expected {oursShift:0.#}");
                    if (oursShift == 0 && r.Peak[k] > 1) bad.Add($"{k} peaked {r.Peak[k]:0.#} px away on the way");
                    if (Math.Abs(r.Settled[k][1] - r.First[k][1]) > 1) bad.Add($"{k} height {r.First[k][1]:0.#} -> {r.Settled[k][1]:0.#}");
                }
            }
            // stats, suggestions and a found update arrive after everything else: they must not move or resize anything
            if (r.BeforeLate == null) bad.Add("the late arrivals were never applied");
            else foreach (var k in r.Settled.Keys.Where(r.BeforeLate.ContainsKey))
                    if (Math.Abs(r.Settled[k][0] - r.BeforeLate[k][0]) > 1 || Math.Abs(r.Settled[k][1] - r.BeforeLate[k][1]) > 1)
                        bad.Add($"{k} changed after the late arrivals: y {r.BeforeLate[k][0]:0.#} -> {r.Settled[k][0]:0.#}, h {r.BeforeLate[k][1]:0.#} -> {r.Settled[k][1]:0.#}");
            var head = $"startup shift, {name}: ours {r.OursFirst}->{r.OursFinal}, others {r.OthersFirst}->{r.OthersFinal}, {r.Frames} frames";
            return bad.Count == 0 ? "ok   " + head : "FAIL " + head + "\r\n       " + string.Join("\r\n       ", bad.Take(8)) + "\r\n" + r.Report();
        }

        async Task<string> StartupShiftTest(string dir)
        {
            var sb = new StringBuilder();
            try
            {
                Width = 920; Height = 760;
                await CheckNow();
                for (int i = 0; i < 100 && (checking || scanning || checkingOthers || suggesting); i++) await Task.Delay(100);
                int realOurs = cards.Count, realOthers = others.Count;
                if (realOurs < 3 || realOthers < 4) return "FAIL startup shift: fixture too small to test (ours " + realOurs + ", others " + realOthers + ")";
                // 1. what was loaded last time is what is shown first: the placeholder counts match, nothing moves
                settings.LastAddonCount = realOurs; settings.LastOtherCount = realOthers;
                sb.AppendLine(ShiftVerdict("remembered counts match", await ShiftColdStart(600, true)));
                // 2. our list shorter / longer than remembered: only its bottom changes
                settings.LastAddonCount = realOurs + 2; settings.LastOtherCount = realOthers;
                sb.AppendLine(ShiftVerdict("our list shorter than remembered", await ShiftColdStart(600, false)));
                settings.LastAddonCount = realOurs - 1; settings.LastOtherCount = realOthers;
                sb.AppendLine(ShiftVerdict("our list longer than remembered", await ShiftColdStart(600, false)));
                // 3. the same for the Other addons list
                settings.LastAddonCount = realOurs; settings.LastOtherCount = realOthers + 3;
                sb.AppendLine(ShiftVerdict("other addons shorter than remembered", await ShiftColdStart(600, false)));
                settings.LastAddonCount = realOurs; settings.LastOtherCount = realOthers - 3;
                sb.AppendLine(ShiftVerdict("other addons longer than remembered", await ShiftColdStart(600, false)));
                // 4. first run: nothing remembered -> the defaults; and what was loaded is then saved (in memory and on disk)
                settings.LastAddonCount = null; settings.LastOtherCount = null;
                sb.AppendLine(ShiftVerdict("first run (defaults " + DefaultAddonSlots + " / " + DefaultOtherSlots + ")", await ShiftColdStart(600, false)));
                var disk = SettingsStore.Load();
                bool saved = settings.LastAddonCount == cards.Count && settings.LastOtherCount == others.Count && disk.LastAddonCount == cards.Count && disk.LastOtherCount == others.Count;
                sb.AppendLine((saved ? "ok   " : "FAIL ") + $"startup shift: remembered counts are saved after a load (memory {settings.LastAddonCount}/{settings.LastOtherCount}, disk {disk.LastAddonCount}/{disk.LastOtherCount}, shown {cards.Count}/{others.Count})");
                // 5. a placeholder is exactly as tall as the card that replaces it, with the same margins (same size tokens, so also at 125 % DPI: layout is in DIPs)
                var skelCard = (FrameworkElement)AddonSkeletonCard(0); var skelRow = (FrameworkElement)OtherSkeletonRow(0);
                var realCard = CardVisual(Cards, 0);
                var quiet = others.Select((c, i) => new { c, i }).FirstOrDefault(x => x.c.State == TpState.Local || x.c.State == TpState.UpToDate);
                var realRow = CardVisual(OtherCards, quiet?.i ?? 0);
                bool sizes = realCard != null && realRow != null
                    && Math.Abs(skelCard.Height - realCard.ActualHeight) < 0.5 && Math.Abs(skelRow.Height - realRow.ActualHeight) < 0.5
                    && skelCard.Margin == realCard.Margin && skelRow.Margin == realRow.Margin;
                sb.AppendLine((sizes ? "ok   " : "FAIL ") + $"startup shift: skeleton card {skelCard.Height} / row {skelRow.Height} = real card {realCard?.ActualHeight} / row {realRow?.ActualHeight}, same margins");
                // 6. after the swap no skeleton is left and the real lists are fully opaque
                bool clean = !CardsSkeletonForTest && Cards.Opacity == 1 && OtherCards.Opacity == 1 && OtherSkeleton.Visibility != Visibility.Visible;
                sb.AppendLine((clean ? "ok   " : "FAIL ") + "startup shift: after the swap no skeleton is left and the real lists are fully opaque");
            }
            catch (Exception e) { sb.AppendLine("FAIL startup shift crashed: " + e); }
            finally
            {
                // leave the window as the other sections expect it
                cards.Clear(); others.Clear(); manifest = null;
                await CheckNow();
                for (int i = 0; i < 100 && (checking || scanning || checkingOthers || suggesting); i++) await Task.Delay(100);
            }
            return sb.ToString();
        }
    }
}
