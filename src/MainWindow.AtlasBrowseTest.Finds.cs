using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;

namespace ElansAddonHub
{
    // --selftest "atlasbrowse": the latest-finds strip on top of the Finds page (Controls\FindsStrip.cs, AtlasPage.Finds.cs)
    public partial class MainWindow
    {
        static LodgeAtlas AtlasFindsLodge(int n, bool guest, int rev = 20)
        {
            var finds = new List<string>();
            for (int k = 0; k < n; k++)
            {
                long seq = 1 + k;
                int item = 100 + (int)(seq % 6);
                finds.Add("{\"i\":" + item + ",\"c\":" + (seq % 5 == 0 ? 3 : 1) + ",\"at\":" + (AtNow - 600 - (n - k) * 60) + ",\"src\":\"npc:1\",\"z\":11,\"x\":0.3,\"y\":0.3" + (guest ? "" : ",\"by\":\"Tess\"") + ",\"s\":" + seq + "}");
            }
            var l = new LodgeAtlas();
            if (!l.Apply(Json.Obj("{\"t\":\"atlas.data\",\"epoch\":\"e1\",\"rev\":" + rev + ",\"at\":" + (AtNow - 5) + ",\"full\":true,\"finds\":[" + string.Join(",", finds) + "]}"))) throw new InvalidOperationException("finds page refused");
            return l;
        }

        static void WheelOn(FrameworkElement el, int delta)
            => el.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = UIElement.PreviewMouseWheelEvent, Source = el });

        async Task AtlasFindsStripTest(string dir, Action<string, bool, string> Check)
        {
            var saved = AtlasQuery.Context.Lodge;
            var wasW = Width; var wasH = Height;
            try
            {
                Width = 1300; Height = 900; await Task.Delay(100);
                // ---- a few finds (the invented Lodge: four, Tess and Bob)
                await AtlasView.Open(AtlasNav.FindsId); await Task.Delay(200); UpdateLayout();
                var st = AtlasView.FindsStripForTest;
                Check("finds strip: the Finds page starts with the strip, newest find first (Ruby Ring, then the older ones)", st != null && st.IsVisible && st.Entries.Count == 4 && st.Entries.Select(e => e.Seq).SequenceEqual(new long[] { 4, 3, 2, 1 }) && st.Entries[0].Row.Title == "Ruby Ring", st == null ? "no strip" : string.Join(",", st.Entries.Select(e => e.Seq)));
                Check("finds strip: one icon per find, left to right, under a 'Latest finds' heading", st.TilesForTest.Count == 4 && AtlasTexts(AtlasView.BodyForTest).Contains("Latest finds"), null);
                var badges = st.TilesForTest.Select(t => AtlasWalk(t).OfType<TextBlock>().Select(x => x.Text).FirstOrDefault(x => x == "2")).ToList();
                Check("finds strip: a count badge only where more than one was found (the 2 wolf pelts)", st.Entries.Select((e, i) => e.Count > 1 == (badges[i] != null)).All(x => x) && badges.Count(b => b != null) == 1, string.Join(",", st.Entries.Select(e => e.Count)));
                var tip = st.TipOf(st.Entries[0]);
                var foot = tip.Footer.Children.OfType<TextBlock>().Select(t => t.Text).ToList();
                Check("finds strip: the tooltip is the item's game tooltip with a footer 'Found by Tess · 1× from <source> · <zone> · <time ago> · <game version>' (2.77; this fixture's find has no build: 'unknown version')", tip.Title == "Ruby Ring" && foot.Count == 1 && foot[0].StartsWith("Found by Tess · 1× from ") && foot[0].EndsWith(" · " + AtlasFormat.Ago(AtNow - 600, AtUtc(AtNow)) + " · " + FindVersions.Unknown) && foot[0].Split('·').Length == 5, string.Join(" | ", foot));
                Check("finds strip: ... a find bought from a vendor reads 'bought from'", st.TipOf(st.Entries[3]).Footer.Children.OfType<TextBlock>().Any(t => t.Text.Contains("Found by Bob") && t.Text.Contains("bought from ")), st.Entries[3].Foot);
                st.OpenForTest(0); await AtlasView.WaitForTest(); await Task.Delay(100);
                Check("finds strip: a click on an icon opens the item's Atlas page", AtlasView.CurrentId == "item:105", AtlasView.CurrentId);
                await AtlasView.Back(); await Task.Delay(200); UpdateLayout();
                st = AtlasView.FindsStripForTest;
                Check("finds strip: with room for all of them there are no arrows and nothing to scroll", st.LeftArrow.Visibility != Visibility.Visible && st.RightArrow.Visibility != Visibility.Visible && st.ScrollerForTest.ScrollableWidth <= 0, null);

                // ---- guests: the server sends no names
                AtlasQuery.Context.Lodge = () => AtlasFindsLodge(4, true);
                await AtlasView.Open(AtlasNav.FindsId); await AtlasView.Redraw(); UpdateLayout();
                st = AtlasView.FindsStripForTest;
                Check("finds strip, guest: 'Found by a member' and no name anywhere in the footer", st != null && st.Entries.All(e => e.Foot.StartsWith("Found by a member · ")) && !st.Entries.Any(e => e.Foot.Contains("Tess")), st == null ? "no strip" : st.Entries[0].Foot);

                // ---- many finds: capped, scrolls
                var lodge = AtlasFindsLodge(50, false);
                AtlasQuery.Context.Lodge = () => lodge;
                await AtlasView.Open(AtlasNav.FindsId); await AtlasView.Redraw(); await Task.Delay(100); UpdateLayout();
                st = AtlasView.FindsStripForTest;
                Check("finds strip: 50 finds show the newest 40, newest first", st != null && st.Entries.Count == ElansAddonHub.AtlasPage.FindsStripMax && st.Entries[0].Seq == 50 && st.Entries[39].Seq == 11 && st.TilesForTest.Count == 40, st == null ? "no strip" : st.Entries.Count + " " + st.Entries[0].Seq);
                Check("finds strip: ... seq 45 was three of them: its tile has the count badge 3", AtlasWalk(st.TilesForTest[5]).OfType<TextBlock>().Any(t => t.Text == "3"), null);
                Check("finds strip: at the start the left arrow is hidden and the right one shown, and no scrollbar is visible", st.ScrollerForTest.ScrollableWidth > 100 && st.AtStart && st.LeftArrow.Visibility != Visibility.Visible && st.RightArrow.Visibility == Visibility.Visible && st.ScrollerForTest.ComputedHorizontalScrollBarVisibility != Visibility.Visible, st.ScrollerForTest.ScrollableWidth + " " + st.LeftArrow.Visibility + " " + st.RightArrow.Visibility);
                double pageY = AtlasView.Scroller.VerticalOffset;
                WheelOn(st, -240); await Task.Delay(500);
                Check("finds strip: the mouse wheel scrolls it sideways (not the page), smoothly to the target", st.Offset > 100 && Math.Abs(st.Offset - st.TargetOffset) < 2 && AtlasView.Scroller.VerticalOffset == pageY, st.Offset + " / " + st.TargetOffset);
                Check("finds strip: ... once scrolled both arrows show", st.LeftArrow.Visibility == Visibility.Visible && st.RightArrow.Visibility == Visibility.Visible, null);
                double drag0 = st.Offset; st.SimulateDragForTest(-80); UpdateLayout();
                Check("finds strip: dragging with the mouse moves it with the pointer", Math.Abs(st.Offset - (drag0 + 80)) < 2, st.Offset + " from " + drag0);
                ClickButton(st.RightArrow); await Task.Delay(500);
                Check("finds strip: the right arrow scrolls on by most of a screen", st.Offset > drag0 + 300, st.Offset + " from " + drag0);
                st.To(1e6); await Task.Delay(500);
                Check("finds strip: at the far end the right arrow is hidden and the left one shown", st.RightArrow.Visibility != Visibility.Visible && st.LeftArrow.Visibility == Visibility.Visible && st.Offset >= st.ScrollerForTest.ScrollableWidth - 1, null);
                ClickButton(st.LeftArrow); await Task.Delay(500);
                Check("finds strip: the left arrow scrolls back", st.Offset < st.ScrollerForTest.ScrollableWidth - 300, null);
                Check("finds strip: swiping is set up (touch panning, horizontal)", st.ScrollerForTest.PanningMode == PanningMode.HorizontalOnly, null);
                st.To(0); await Task.Delay(400);
                st.TilesForTest[0].Focus(); UpdateLayout();
                bool movedRight = st.MoveFocus(1);
                Check("finds strip: Right moves the focus to the next icon and Left back", movedRight && st.TilesForTest[1].IsKeyboardFocused && st.MoveFocus(-1) && st.TilesForTest[0].IsKeyboardFocused, null);
                Check("finds strip: ... but not past the ends", st.MoveFocus(-1) && st.TilesForTest[0].IsKeyboardFocused, null);

                // ---- live: new finds arrive
                st.To(0); await Task.Delay(400);
                var l2 = AtlasFindsLodge(51, false);
                AtlasQuery.Context.Lodge = () => l2;
                AtlasView.PoolChanged(); await Task.Delay(100); UpdateLayout();
                Check("finds strip, live: at the start, a new find is put at the front and the strip stays at the start", st.Entries[0].Seq == 51 && st.AtStart && st.TilesForTest.Count == 40 && AtlasView.FindsStripForTest == st, null);
                st.To(300); await Task.Delay(400);
                double was = st.Offset;
                var l3 = AtlasFindsLodge(53, false);
                AtlasQuery.Context.Lodge = () => l3;
                AtlasView.PoolChanged(); await Task.Delay(100); UpdateLayout();
                Check("finds strip, live: scrolled away, two new finds do not move what you were looking at (the offset grows by their width)", st.Entries[0].Seq == 53 && Math.Abs(st.Offset - (was + 2 * FindsStrip.Pitch)) < 2, st.Offset + " from " + was);
                Snapshot(Path.Combine(dir, "atlas-finds-strip.png"));
                st.To(0); await Task.Delay(400);
                SnapshotWithTip(st.TilesForTest[1], st.TipOf(st.Entries[1]), Path.Combine(dir, "atlas-finds-tooltip.png"));
                int before = st.Entries.Count;
                AtlasView.PoolChanged();
                Check("finds strip, live: the same data again changes nothing", st.Entries.Count == before && AtlasView.FindsStripForTest == st, null);

                // ---- nothing found
                AtlasQuery.Context.Lodge = () => new LodgeAtlas();
                await AtlasView.Open(AtlasNav.FindsId); await AtlasView.Redraw(); await Task.Delay(100); UpdateLayout();
                Check("finds strip: no finds, no strip (and no 'Latest finds' heading)", AtlasView.FindsStripForTest == null && !AtlasTexts(AtlasView.BodyForTest).Contains("Latest finds"), null);
                AtlasQuery.Context.Lodge = () => null;
                await AtlasView.Open(AtlasNav.FindsId); await AtlasView.Redraw(); await Task.Delay(100);
                Check("finds strip: no Lodge, no strip", AtlasView.FindsStripForTest == null, null);
                await AtlasView.Open("browse:items"); await Task.Delay(100);
                Check("finds strip: the other browse pages have none", AtlasView.FindsStripForTest == null, null);
            }
            finally { AtlasQuery.Context.Lodge = saved; Width = wasW; Height = wasH; await Task.Delay(50); }
        }
    }
}
