using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // --selftest "addonsgrid": the Addons page as a grid. Columns at 760 / 1300 / 1700 (1 / 2 / 3), cards of a row equally tall and on the same
    // line, the skeleton cards exactly where the real cards come (no jump), a card opening in place without reshuffling the grid, the Grid | List
    // switch and its setting, the other addons list in the same columns, and every button of every card reachable at every width.
    // ELANSHUB_DESIGN_SHOTS=addonsgrid writes the pictures (addons-grid-*.png) from the same fixture.
    public partial class MainWindow
    {
        static Point PosIn(FrameworkElement el, FrameworkElement rel) => el.TranslatePoint(new Point(0, 0), rel);

        async Task SetWidth(double w, double h = 0)
        {
            Width = w; if (h > 0) Height = h;
            await Task.Delay(300); UpdateLayout();
        }

        async Task<string> AddonsGridTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            void Check(string what, bool cond, string detail = null) { SelfTestClock.Mark(what); notes.Add((cond ? "ok   " : "FAIL ") + what + (!cond && detail != null ? " [" + detail + "]" : "")); ok &= cond; }
            var savedWidth = Width; var savedHeight = Height; var savedView = settings.AddonsView;
            try
            {
                Height = 900;
                ShowTab("addons");
                await CheckNow();
                for (int i = 0; i < 100 && (checking || scanning || checkingOthers || suggesting); i++) await Task.Delay(100);
                SetAddonsView(false);
                foreach (var c in cards) c.SetStats(new AddonStats { Released = DateTime.UtcNow.AddDays(-3), Downloads = 1234, Releases = 7 });
                if (cards.Count < 3) { Check("fixture has at least 3 addon cards", false, cards.Count.ToString()); return string.Join("\r\n", notes) + "\r\naddonsgrid: FAILED"; }

                // ---- the pure rule
                Check("columns for a width: 1 below 852, 2 from 852, 3 from 1284, never more than 3", CardGrid.ColumnsFor(851, 420, 3, 12) == 1 && CardGrid.ColumnsFor(852, 420, 3, 12) == 2
                    && CardGrid.ColumnsFor(1283, 420, 3, 12) == 2 && CardGrid.ColumnsFor(1284, 420, 3, 12) == 3 && CardGrid.ColumnsFor(4000, 420, 3, 12) == 3 && CardGrid.ColumnsFor(0, 420, 3, 12) == 1);

                // ---- columns follow the window width
                FrameworkElement Card(int i) => CardVisual(Cards, i);
                await SetWidth(760);
                int c760 = CardColumnsForTest;
                Check("narrow (760): one column, cards stacked", c760 == 1 && Math.Abs(PosIn(Card(0), AddonsRoot).X - PosIn(Card(1), AddonsRoot).X) < 1 && PosIn(Card(1), AddonsRoot).Y > PosIn(Card(0), AddonsRoot).Y + 100, c760.ToString());
                await SetWidth(1300);
                int c1300 = CardColumnsForTest;
                var p0 = PosIn(Card(0), AddonsRoot); var p1 = PosIn(Card(1), AddonsRoot); var p2 = PosIn(Card(2), AddonsRoot);
                Check("1300: two columns - the second card to the right of the first on the same line, the third under the first", c1300 == 2 && p1.X > p0.X + 300 && Math.Abs(p1.Y - p0.Y) < 1 && Math.Abs(p2.X - p0.X) < 1 && p2.Y > p0.Y + 100,
                    c1300 + " " + p0 + " " + p1 + " " + p2);
                Check("1300: the cards of a row are equally tall, and the same height as the placeholders", Math.Abs(Card(0).ActualHeight - Card(1).ActualHeight) < 0.5 && Math.Abs(Card(0).ActualHeight - (double)FindResource("Size.Card.Addon")) < 0.5,
                    Card(0).ActualHeight + " " + Card(1).ActualHeight + " token " + FindResource("Size.Card.Addon"));
                Check("1300: the columns fill the width (right edge of the second column = right edge of the page content)", Math.Abs(PosIn(Card(1), AddonsRoot).X + Card(1).ActualWidth - AddonsRoot.ActualWidth) < 1.5, (PosIn(Card(1), AddonsRoot).X + Card(1).ActualWidth) + " vs " + AddonsRoot.ActualWidth);
                if (others.Count >= 3)
                {
                    var o0 = PosIn(CardVisual(OtherCards, 0), AddonsRoot); var o1 = PosIn(CardVisual(OtherCards, 1), AddonsRoot);
                    Check("1300: the other addons list uses the same two columns", OtherColumnsForTest == 2 && o1.X > o0.X + 300 && Math.Abs(o1.Y - o0.Y) < 1 && Math.Abs(o0.X - p0.X) < 1, OtherColumnsForTest + " " + o0 + " " + o1);
                }
                Snapshot(Path.Combine(dir, "addons-grid-1300.png"));
                await SetWidth(1700);
                int c1700 = CardColumnsForTest;
                var q0 = PosIn(Card(0), AddonsRoot); var q1 = PosIn(Card(1), AddonsRoot); var q2 = PosIn(Card(2), AddonsRoot);
                Check("1700: three columns - three cards on one line", c1700 == 3 && Math.Abs(q0.Y - q1.Y) < 1 && Math.Abs(q1.Y - q2.Y) < 1 && q2.X > q1.X + 300 && q1.X > q0.X + 300, c1700 + " " + q0 + " " + q1 + " " + q2);
                Snapshot(Path.Combine(dir, "addons-grid-1700.png"));
                await SetWidth(760);
                Check("shrinking again goes back to one column", CardColumnsForTest == 1);

                // ---- what is on a card without opening it
                await SetWidth(1300);
                var face = FindDown<Button>(Card(0));
                var faceTexts = AtlasWalkTexts(Card(0));
                Check("a card shows its status chip, the source, when it was updated, and the primary action - collapsed", face != null && face.IsVisible && cards[0].Expanded == false
                    && cards[0].StatusText.Length > 0 && faceTexts.Any(t => t == cards[0].StatusText) && faceTexts.Any(t => t == "Updated 3d ago") && faceTexts.Any(t => t == cards[0].PillText), string.Join(" | ", faceTexts));
                Check("the status chip says what the pill will do: installed version / version arrow / not installed", cards.All(c => c.State == CardState.UpdateAvailable ? c.StatusText.Contains("→") : c.State == CardState.NotInstalled ? c.StatusText == "Not installed" : c.State == CardState.UpToDate ? c.StatusText.StartsWith("Installed ") : true),
                    string.Join(" | ", cards.Select(c => c.State + ":" + c.StatusText)));

                // ---- every button of every card is on screen at every width
                foreach (var w in new[] { 760.0, 1300.0, 1700.0 })
                {
                    await SetWidth(w);
                    var bad = new List<string>();
                    var viewport = new Rect(0, 0, AddonsPage.ViewportWidth, 100000);
                    for (int i = 0; i < cards.Count; i++)
                    {
                        var b = FindDown<Button>(Card(i));
                        if (b == null || !b.IsVisible || b.ActualWidth < 20) { bad.Add("card " + i + " has no visible button"); continue; }
                        var tl = b.TranslatePoint(new Point(0, 0), AddonsPage);
                        if (tl.X < 0 || tl.X + b.ActualWidth > AddonsPage.ViewportWidth + 0.5) bad.Add("card " + i + " button leaves the page sideways: x " + tl.X + " w " + b.ActualWidth);
                        var card = Card(i);
                        var cp = PosIn(card, AddonsRoot); var bp = PosIn(b, AddonsRoot);
                        if (bp.X + b.ActualWidth > cp.X + card.ActualWidth + 0.5 || bp.Y + b.ActualHeight > cp.Y + card.ActualHeight + 0.5) bad.Add("card " + i + " button sticks out of its card");
                    }
                    Check($"{w}: the primary button of every card is visible and inside its card and the page", bad.Count == 0, string.Join("; ", bad));
                }
                Check("the view switch and the check button are reachable (visible, named)", AddonsViewGrid.IsVisible && AddonsViewList.IsVisible && CheckAllButton.IsVisible
                    && (string)AddonsViewGrid.GetValue(System.Windows.Automation.AutomationProperties.NameProperty) == "Grid view" && (string)AddonsViewList.GetValue(System.Windows.Automation.AutomationProperties.NameProperty) == "List view"
                    && AddonsViewGrid.Focusable && AddonsViewList.Focusable);

                // ---- a card opens in place
                await SetWidth(1300);
                double h0 = Card(0).ActualHeight; var before = new[] { PosIn(Card(0), AddonsRoot), PosIn(Card(1), AddonsRoot), PosIn(Card(2), AddonsRoot) }; double h1 = Card(1).ActualHeight, h2 = Card(2).ActualHeight;
                cards[0].Expanded = true;
                await Task.Delay(800); UpdateLayout();
                var after = new[] { PosIn(Card(0), AddonsRoot), PosIn(Card(1), AddonsRoot), PosIn(Card(2), AddonsRoot) };
                Check("expanded card (grid, 2 columns): it grows in place - same column, same top, taller", CardColumnsForTest == 2 && Card(0).ActualHeight > h0 + 40 && Math.Abs(after[0].X - before[0].X) < 1 && Math.Abs(after[0].Y - before[0].Y) < 1, h0 + " -> " + Card(0).ActualHeight);
                Check("expanded card: its neighbour in the row stays where it was at its own height (nothing stretched or moved sideways)", CardColumnsForTest == 2 && Math.Abs(after[1].X - before[1].X) < 24 && Math.Abs(after[1].Y - before[1].Y) < 1 && Math.Abs(Card(1).ActualHeight - h1) < 0.5, Card(1).ActualHeight + " vs " + h1);
                Check("expanded card: the row below moves down by exactly the growth, still in its columns", Math.Abs((after[2].Y - before[2].Y) - (Card(0).ActualHeight - h0)) < 1.5 && Math.Abs(after[2].X - before[2].X) < 1 && Math.Abs(Card(2).ActualHeight - h2) < 0.5, (after[2].Y - before[2].Y) + " vs " + (Card(0).ActualHeight - h0));
                Check("expanded card: its details are on screen (version history, links) and the Roll back / links buttons are inside the card",
                    AtlasWalkTexts(Card(0)).Any(t => t.StartsWith("VERSION HISTORY") || t.StartsWith("WHAT'S NEW")) && FindDown<Button>(Card(0), b => (b.Content as string) == "All releases on GitHub")?.IsVisible == true);
                Snapshot(Path.Combine(dir, "addons-grid-expanded.png"));
                cards[0].Expanded = false;
                await Task.Delay(90); UpdateLayout();
                Check("closing (mid-animation): the neighbour keeps its own height (not stretched to the closing card)", Math.Abs(Card(1).ActualHeight - h1) < 0.5, Card(1).ActualHeight + " vs " + h1 + ", closing card " + Card(0).ActualHeight);
                await Task.Delay(710); UpdateLayout();
                var back =new[] { PosIn(Card(0), AddonsRoot), PosIn(Card(1), AddonsRoot), PosIn(Card(2), AddonsRoot) };
                Check("collapsing puts the grid back exactly", Math.Abs(Card(0).ActualHeight - h0) < 0.5 && Enumerable.Range(0, 3).All(i => Math.Abs(back[i].X - before[i].X) < 1 && Math.Abs(back[i].Y - before[i].Y) < 1));
                if (others.Count >= 3)
                {
                    var ob = new[] { PosIn(CardVisual(OtherCards, 1), AddonsRoot), PosIn(CardVisual(OtherCards, 2), AddonsRoot) };
                    var first = (ThirdPartyCard)CardVisual(OtherCards, 0).DataContext;
                    double oh1 = CardVisual(OtherCards, 1).ActualHeight, oh0 = CardVisual(OtherCards, 0).ActualHeight;
                    first.Expanded = true;
                    await Task.Delay(800); UpdateLayout();
                    var oa = new[] { PosIn(CardVisual(OtherCards, 1), AddonsRoot), PosIn(CardVisual(OtherCards, 2), AddonsRoot) };
                    Check("an other addon opens in place too: its neighbour stays, the row below moves down", Math.Abs(oa[0].Y - ob[0].Y) < 1 && Math.Abs(oa[0].X - ob[0].X) < 24 && oa[1].Y > ob[1].Y + 20, oa[0] + " " + oa[1]);
                    Check("an other addon opens: only it grows - the neighbour in its row keeps its own height and stays closed",
                        CardVisual(OtherCards, 0).ActualHeight > oh0 + 20 && Math.Abs(CardVisual(OtherCards, 1).ActualHeight - oh1) < 0.5 && !((ThirdPartyCard)CardVisual(OtherCards, 1).DataContext).Expanded,
                        oh0 + "->" + CardVisual(OtherCards, 0).ActualHeight + ", neighbour " + oh1 + "->" + CardVisual(OtherCards, 1).ActualHeight);
                    first.Expanded = false;
                    await Task.Delay(90); UpdateLayout();
                    Check("an other addon closing (mid-animation): the neighbour keeps its own height", Math.Abs(CardVisual(OtherCards, 1).ActualHeight - oh1) < 0.5, CardVisual(OtherCards, 1).ActualHeight + " vs " + oh1);
                    await Task.Delay(510); UpdateLayout();
                    Check("an other addon closed: back to its own height", Math.Abs(CardVisual(OtherCards, 0).ActualHeight - oh0) < 0.5, CardVisual(OtherCards, 0).ActualHeight + " vs " + oh0);
                    // rows of different natural heights: each card keeps its own (a taller neighbour never stretches the shorter one)
                    var conts = Enumerable.Range(0, Math.Min(others.Count, 6)).Select(i => OtherCards.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement).Where(x => x != null).ToList();
                    Check("other addons: every row card is at its own measured height (top-aligned, not stretched)",
                        conts.Count > 1 && conts.All(v => Math.Abs(v.ActualHeight - v.DesiredSize.Height) < 0.5), string.Join(" ", conts.Select(v => v.ActualHeight.ToString("0.#") + "/" + v.DesiredSize.Height.ToString("0.#"))));
                }

                // ---- the skeleton cards are where the real cards come
                await SetWidth(1700);
                var realPos = Enumerable.Range(0, cards.Count).Select(i => new[] { PosIn(Card(i), AddonsRoot).X, PosIn(Card(i), AddonsRoot).Y, Card(i).ActualWidth, Card(i).ActualHeight }).ToList();
                int realOthers = others.Count;
                settings.LastAddonCount = cards.Count;
                ResetColdState();
                await Task.Delay(100); UpdateLayout();
                var skel = Enumerable.Range(0, CardsSkeleton.Children.Count).Select(i => (FrameworkElement)CardsSkeleton.Children[i]).Select(e => new[] { PosIn(e, AddonsRoot).X, PosIn(e, AddonsRoot).Y, e.ActualWidth, e.ActualHeight }).ToList();
                bool same = skel.Count == realPos.Count && Enumerable.Range(0, skel.Count).All(i => Enumerable.Range(0, 4).All(k => Math.Abs(skel[i][k] - realPos[i][k]) < 1));
                Check("1700: the placeholders have the same columns, positions and sizes as the real cards that replace them (3 columns)", same && CardsSkeleton.Columns == 3, string.Join(" ; ", skel.Select(s => string.Join(",", s.Select(v => v.ToString("0")))))
                    + " <> " + string.Join(" ; ", realPos.Select(s => string.Join(",", s.Select(v => v.ToString("0"))))));
                Snapshot(Path.Combine(dir, "addons-grid-skeleton-1700.png"));
                await SetWidth(1300);
                var skel2 = Enumerable.Range(0, CardsSkeleton.Children.Count).Select(i => PosIn((FrameworkElement)CardsSkeleton.Children[i], AddonsRoot)).ToList();
                await CheckNow();
                for (int i = 0; i < 100 && (checking || scanning || checkingOthers || suggesting); i++) await Task.Delay(100);
                await Task.Delay(500); UpdateLayout();
                bool same2 = skel2.Count == cards.Count && Enumerable.Range(0, cards.Count).All(i => Math.Abs(PosIn(Card(i), AddonsRoot).X - skel2[i].X) < 1 && Math.Abs(PosIn(Card(i), AddonsRoot).Y - skel2[i].Y) < 1);
                Check("1300: the real cards then land exactly on the placeholders (2 columns), the placeholders are gone", same2 && !CardsSkeletonForTest, string.Join(" ; ", skel2) + " <> " + string.Join(" ; ", Enumerable.Range(0, cards.Count).Select(i => PosIn(Card(i), AddonsRoot).ToString())));

                // ---- Grid | List
                await SetWidth(1700);
                AddonsViewList.IsChecked = true;
                await Task.Delay(300); UpdateLayout();
                var l0 = PosIn(Card(0), AddonsRoot); var l1 = PosIn(Card(1), AddonsRoot);
                Check("List: one column, cards stacked, no wider than the comfortable maximum", CardColumnsForTest == 1 && Math.Abs(l0.X - l1.X) < 1 && l1.Y > l0.Y + 100 && AddonsRoot.ActualWidth <= AddonsListMaxWidth + 0.5 && Card(0).ActualWidth > 600,
                    CardColumnsForTest + " " + AddonsRoot.ActualWidth + " " + Card(0).ActualWidth);
                Check("List: the other addons are one column as well", others.Count < 2 || OtherColumnsForTest == 1);
                Check("List is remembered in the settings (memory and disk)", settings.AddonsView == "list" && SettingsStore.Load().AddonsView == "list" && AddonsListMode);
                Snapshot(Path.Combine(dir, "addons-list-1700.png"));
                await SetWidth(760);
                Check("List at 760: still one column, using the whole width", CardColumnsForTest == 1 && Card(0).ActualWidth > 550, Card(0).ActualWidth.ToString());
                cards[0].Expanded = true; await Task.Delay(600); UpdateLayout();
                Check("List: a card opens in place and the one below moves down", Card(0).ActualHeight > h0 + 40 && PosIn(Card(1), AddonsRoot).Y >= PosIn(Card(0), AddonsRoot).Y + Card(0).ActualHeight - 1);
                cards[0].Expanded = false; await Task.Delay(500);
                AddonsViewGrid.IsChecked = true;
                await SetWidth(1300);
                Check("Grid again: two columns and the setting says grid", CardColumnsForTest == 2 && settings.AddonsView == "grid" && SettingsStore.Load().AddonsView == "grid" && !AddonsListMode && double.IsPositiveInfinity(AddonsRoot.MaxWidth));
                // a restart: the window picks the saved choice up (the radio buttons follow the setting)
                settings.AddonsView = "list"; InitAddonsView(); UpdateLayout();
                Check("start-up restores the saved view (the List button is the checked one)", AddonsViewList.IsChecked == true && AddonsViewGrid.IsChecked != true && CardColumnsForTest == 1);
                SetAddonsView(false);

                // ---- a keyboard user: the Tab order reaches the switch and the first card's button
                var actionable = cards.ToList().FindIndex(c => c.ButtonEnabled);
                Check("the primary button of a card that has an action takes keyboard focus", actionable >= 0 && FindDown<Button>(Card(actionable))?.Focusable == true, actionable.ToString());
            }
            catch (Exception e) { Check("addonsgrid test ran to the end", false, e.ToString()); }
            finally
            {
                try
                {
                    settings.AddonsView = savedView; SettingsStore.Save(settings); ApplyAddonsView(AddonsListMode);
                    Width = savedWidth; Height = savedHeight;
                    ShowTab("addons");
                    foreach (var c in cards) c.Expanded = false;
                }
                catch { }
            }
            return string.Join("\r\n", notes) + "\r\naddonsgrid: " + (ok ? "ok" : "FAILED");
        }

        // all the texts shown in an element
        static List<string> AtlasWalkTexts(DependencyObject root)
        {
            var l = new List<string>();
            void Walk(DependencyObject d)
            {
                if (d is TextBlock t && t.IsVisible && !string.IsNullOrEmpty(t.Text)) l.Add(t.Text);
                if (d is Button b && b.Content is string s) l.Add(s);
                for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i++) Walk(System.Windows.Media.VisualTreeHelper.GetChild(d, i));
            }
            Walk(root);
            return l;
        }

        // ELANSHUB_DESIGN_SHOTS=addonsgrid: the Addons page in the grid and list, narrow to wide, collapsed and open
        async Task AddonsGridShots(string dir)
        {
            try
            {
                Height = 960;
                ShowTab("addons");
                for (int i = 0; i < 60 && (cards.Count < 3 || others.Count < 4); i++) await Task.Delay(200);
                if (cards.Count >= 3)
                {
                    cards[0].SetPillForTest("Update to 1.23.0", PillKind.Action, "x", true);
                    cards[1].SetPillForTest("Install", PillKind.Action, "x", true);
                    cards[2].SetPillForTest("Up to date", PillKind.Quiet, "x", false);
                }
                foreach (var c in cards) c.SetStats(new AddonStats { Released = DateTime.UtcNow.AddDays(-3), Downloads = 1234, Releases = 7 });
                SetAddonsView(false, false);
                foreach (var w in new[] { 760.0, 1300.0, 1700.0 })
                {
                    await SetWidth(w);
                    AddonsPage.ScrollToTop();
                    await Task.Delay(500);
                    SnapshotScaled(Path.Combine(dir, $"addons-grid-{w:0}.png"), 1);
                }
                await SetWidth(1300);
                cards[0].Expanded = true; await Task.Delay(900);
                SnapshotScaled(Path.Combine(dir, "addons-grid-1300-expanded.png"), 1);
                cards[0].Expanded = false; await Task.Delay(500);
                var y = OtherPanel.TranslatePoint(new Point(0, 0), AddonsPage).Y + AddonsPage.VerticalOffset - 12;
                AddonsPage.ScrollToVerticalOffset(Math.Max(0, y)); await Task.Delay(400);
                SnapshotScaled(Path.Combine(dir, "addons-grid-1300-others.png"), 1);
                await SetWidth(1700);
                AddonsPage.ScrollToVerticalOffset(Math.Max(0, y)); await Task.Delay(400);
                SnapshotScaled(Path.Combine(dir, "addons-grid-1700-others.png"), 1);
                SetAddonsView(true, false);
                AddonsPage.ScrollToTop(); await Task.Delay(500);
                SnapshotScaled(Path.Combine(dir, "addons-list-1700.png"), 1);
                File.WriteAllText(Path.Combine(dir, "result.txt"), "addons grid shots done");
            }
            catch (Exception e) { File.WriteAllText(Path.Combine(dir, "result.txt"), "addons grid shots crashed: " + e); }
            Quit();
        }
    }
}
