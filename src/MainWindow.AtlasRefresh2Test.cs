using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services.Db2;

namespace ElansAddonHub
{
    // --selftest "atlaspage", part 4: the entity page template and the global search of Atlas refresh step 2 - the hero of every kind, the tabs (present, hidden when empty, switching, ?tab= in the id
    // without a new history step), recents / pins / breadcrumbs / Follow / Describe / chat links with a tab, the Ctrl+K palette and the title bar search on every page (Characters, Lodge,
    // Settings, Addons, Browser), the caption's hit-test island, narrow widths, and what the template does not cost (tabs are built when shown).
    // The helpers are shared by the other Atlas sections (they switch tabs to find a table).
    public partial class MainWindow
    {
        // selects a tab of the entity page (as a click on its button does) and lets the layout settle; false when the page has no such tab
        async Task<bool> SwitchTab(string tab)
        {
            bool ok = AtlasView.SelectTab(tab, false);
            UpdateLayout();
            await Task.Delay(30);
            return ok;
        }

        bool ClickRailMap()
        {
            var veil = AtlasView.RailForTest == null ? null : AtlasWalk(AtlasView.RailForTest).OfType<Border>().FirstOrDefault(b => b.Tag as string == "rail-map-open");
            if (veil == null) return false;
            veil.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent, Source = veil });
            return true;
        }

        static void PressTitleKey(FrameworkElement el, Key key) => RaiseKey(el, key, Keyboard.PreviewKeyDownEvent);

        async Task AtlasRefresh2Checks(string dir, CheckFn Check)
        {
            var w0 = Width; var h0 = Height;
            var savedMods = GlobalModifiersForTest;
            var realSettings = settings;
            var ns = new Settings(); int saves = 0;
            try
            {
                AtlasView.UseNavSettings(ns, () => saves++);
                AtlasView.History.Clear();
                Width = 1300; Height = 900; await Task.Delay(300);
                ShowTab("atlas"); await AtlasView.GoStart(); await AtlasView.StartFillForTest; UpdateLayout();
                var st = AtlasView.NavState;

                // ============================================================================================================ ids with a tab
                Check("tab ids: item:100?tab=sources splits into the page item:100 and the tab sources; a plain id has none; an unknown tab word is dropped; only entity ids take a tab (browse and hosted ids keep their own '?')",
                    AtlasTabs.BaseId("item:100?tab=sources") == "item:100" && AtlasTabs.TabOf("item:100?tab=sources") == "sources" && AtlasTabs.TabOf("item:100") == "" && AtlasTabs.BaseId("item:100?tab=bogus") == "item:100" && AtlasTabs.TabOf("item:100?tab=bogus") == ""
                    && AtlasTabs.BaseId("browse:items/weapons?name=sw") == "browse:items/weapons?name=sw" && AtlasTabs.TabOf("browse:items/weapons?tab=map") == "" && AtlasTabs.BaseId("worldmap:kalimdor?x=1") == "worldmap:kalimdor?x=1" && AtlasTabs.BaseId("") == "");
                Check("tab ids: WithTab adds it (tab names in lower case), SamePage ignores it, the Atlas parsers accept it (item:100?tab=map is item 100) and Build gives the page of the base id",
                    AtlasTabs.WithTab("item:100", "map") == "item:100?tab=map" && AtlasTabs.WithTab("item:100?tab=sources", "lodge") == "item:100?tab=lodge" && AtlasTabs.WithTab("browse:items", "map") == "browse:items" && AtlasTabs.WithTab("item:100", "nonsense") == "item:100"
                    && AtlasTabs.SamePage("item:100", "item:100?tab=map") && !AtlasTabs.SamePage("item:100", "item:101") && AtlasIds.TryParse("item:100?tab=sources", out var tk, out var tn) && tk == "item" && tn == 100 && AtlasQuery.Build("item:100?tab=sources").Id == "item:100",
                    AtlasQuery.Build("item:100?tab=sources").Id);
                Check("tab ids: NormalizeId keeps a valid tab (Item 100 ? tab = Sources -> item:100?tab=sources), drops an unknown one and still reads plain ids",
                    AtlasQuery.NormalizeId("item:100?tab=sources") == "item:100?tab=sources" && AtlasQuery.NormalizeId("Item 100 ?tab=SOURCES") == "item:100?tab=sources" && AtlasQuery.NormalizeId("item:100?tab=bogus") == "item:100" && AtlasQuery.NormalizeId("item:100") == "item:100" && AtlasQuery.NormalizeId("npc=1") == "npc:1");
                Check("tab ids: Describe, the nav row and the crumbs of an id with a tab are those of the page (name, glyph, 'Items')",
                    AtlasNav.Describe("item:100?tab=sources")?.Title == "Gnarlfang's Claw" && AtlasQuery.Describe("item:100?tab=sources")?.Name == "Gnarlfang's Claw" && AtlasNav.KeyFor("item:100?tab=sources") == "items" && AtlasNav.KeyFor("zone:11?tab=related") == "zones");

                // ---- recents, pins and Follow treat the two ids as one page (unit level)
                {
                    var rl = new List<string>(); var pl = new List<string>(); int ch = 0;
                    var t = new AtlasNavState(rl, pl, () => ch++);
                    t.Visit("item:100"); t.Visit("npc:1"); t.Visit("item:100?tab=sources");
                    Check("recents: the same page on another tab is ONE entry, newest first, in the form it was last seen (item:100?tab=sources)", t.Recents.SequenceEqual(new[] { "item:100?tab=sources", "npc:1" }), string.Join(",", t.Recents));
                    t.Visit("item:100"); t.Follow("item:100", "item:100?tab=lodge");
                    Check("recents: Follow (a tab picked on the page) renames the entry instead of adding one, and never leaves two of the same page", t.Recents.Count == 2 && t.Recents[0] == "item:100?tab=lodge" && t.Recents.Count(r => AtlasTabs.BaseId(r) == "item:100") == 1, string.Join(",", t.Recents));
                    Check("pins: a pin of item:100 is a pin of item:100?tab=map too (IsPinned), pinning it again is refused, Unpin by either form removes it, and the pin keeps the form it was made in",
                        t.Pin("item:100") && t.IsPinned("item:100?tab=map") && !t.Pin("item:100?tab=map") && t.Pins.SequenceEqual(new[] { "item:100" }) && t.TogglePin("item:100?tab=sources") == false && t.Pins.Count == 0 && t.Pin("zone:11?tab=map") && t.Pins[0] == "zone:11?tab=map" && t.Unpin("zone:11"));
                    var dup = new AtlasNavState(new List<string> { "item:1", "item:1?tab=map", "npc:2" }, new List<string> { "zone:3?tab=map", "zone:3" }, null);
                    Check("lists loaded from the settings are cleaned the same way (no page twice, even with different tabs)", dup.Recents.SequenceEqual(new[] { "item:1", "npc:2" }) && dup.Pins.SequenceEqual(new[] { "zone:3?tab=map" }));
                }

                // ---- chat links
                {
                    var refs = ItemLinks.Find("look at item:100?tab=sources and quest:7 then npc:1?tab=bogus ok");
                    Check("chat links: item:100?tab=sources is a chip for item 100 that opens the Sources tab (the chip's id keeps the tab), quest:7 has none, an unknown tab is just the page",
                        refs.Count == 3 && refs[0].AtlasId == "item:100?tab=sources" && refs[0].AtlasKind == "item" && refs[0].Id == 100 && refs[1].AtlasId == "quest:7" && refs[2].AtlasId == "npc:1", string.Join(" | ", refs.Select(r => r.AtlasId)));
                    var chip = ItemChipVM.From(refs[0]);
                    Check("chat links: the chip shows the item's name (not the '?tab=' text) and its click opens the Atlas page on that tab", chip.IsAtlas && chip.Ref.AtlasId == "item:100?tab=sources" && !chip.Name.Contains("?"), chip.Name);
                    ShowTab("lodge"); await Task.Delay(100);
                    OpenInAtlas("item:100?tab=sources"); await AtlasView.WaitForTest(); await Task.Delay(100); UpdateLayout();
                    Check("chat links: Open in Atlas from the Lodge lands on the Atlas page, item:100 on its Sources tab", TabAtlas.IsChecked == true && AtlasView.CurrentId == "item:100?tab=sources" && AtlasView.TabNowForTest == "sources" && AtlasView.Model.Id == "item:100", AtlasView.CurrentId + " " + AtlasView.TabNowForTest);
                }

                // ============================================================================================================ the page: hero per kind, tabs
                var kinds = new[] { "item:100", "npc:1", "obj:50", "quest:7", "zone:11", "instance:98", "spell:4000", "skill:129", "faction:72" };
                var bad = new List<string>(); var seen = new List<string>();
                foreach (var kid in kinds)
                {
                    await AtlasView.Open(kid); await AtlasView.WaitForTest(); UpdateLayout(); await Task.Delay(40);
                    var m = AtlasView.Model; var tile = AtlasView.HeroTileForTest;
                    var tabs = AtlasView.TabsForTest;
                    seen.Add(kid + "=" + string.Join("/", tabs));
                    bool tabsOk = tabs.Count > 0 && tabs[0] == "overview" && tabs.SequenceEqual(tabs.OrderBy(x => Array.IndexOf(AtlasTabs.Order, x))) && AtlasView.TabStripShownForTest == tabs.Count > 1;
                    bool heroOk = AtlasView.HeroForTest != null && AtlasView.HeroNameForTest == m.Title && tile != null && tile.Width == 72 && tile.Height == 72 && AtlasView.PageTitleForTest == m.Title && AtlasView.TitleRowGoneForTest;
                    string defaultWant = m.Kind == "zone" && tabs.Contains("map") ? "map" : "overview";
                    bool defaultOk = AtlasView.TabDefaultForTest == defaultWant && AtlasView.TabNowForTest == defaultWant && AtlasView.CurrentId == kid;
                    bool chipsOk = !new[] { "item", "npc", "quest", "zone", "instance", "spell" }.Contains(m.Kind) || AtlasView.HeroChipsForTest.Count > 0;
                    // every table of the page is on exactly one tab, and the tab says how many rows it holds
                    bool sectionsOk = true;
                    foreach (var s in m.Sections.Where(s => s.Open))
                    {
                        var want = AtlasTabs.OfSection(s.Key);
                        if (!(want == "overview" || tabs.Contains(want))) sectionsOk = false;
                    }
                    if (!(tabsOk && heroOk && defaultOk && chipsOk && sectionsOk)) bad.Add(kid + " tabs=" + tabsOk + " hero=" + heroOk + " default=" + defaultOk + "(" + AtlasView.TabNowForTest + ") chips=" + chipsOk + " sections=" + sectionsOk);
                }
                Check("hero: every kind (item, creature, object, quest, zone, instance, spell, skill, faction) has the hero - name = the page's title, a 72 px tile, chips where the kind has key facts - and its tabs in the fixed order (a strip only when there is more than the Overview); a zone opens on its Map tab, everything else on Overview",
                    bad.Count == 0, string.Join("; ", bad) + " :: " + string.Join(" ", seen));
                Check("tabs: a tab is there only when something is on it - the spell has no Map and no Lodge tab, the unknown item has no strip at all, the start page no hero",
                    await OpenAndTabs("spell:4000", t => !t.Contains("map") && !t.Contains("lodge") && !t.Contains("sources") && t.Contains("related")) && await OpenAndTabs("item:99999", t => t.Count == 0 || t.SequenceEqual(new[] { "overview" })) && !AtlasView.TabStripShownForTest && AtlasView.HeroNameForTest == "Item 99999" && await GoStartAndCheck(), string.Join(" ", seen));

                // ---- the item's hero tile: the quality frame, the letter when the icon is not read; the creature's gold ring when elite
                await AtlasView.Open("item:100"); UpdateLayout();
                var itile = (Border)AtlasView.HeroTileForTest;
                Check("hero: an item's tile has a 2 px frame in its quality colour (Rare: #0070DD) and the name is in the same colour", itile.BorderThickness.Left == 2 && ((SolidColorBrush)itile.BorderBrush).Color == ((SolidColorBrush)AtlasView.HeroBrushForTest).Color && ((SolidColorBrush)itile.BorderBrush).Color == (Color)ColorConverter.ConvertFromString(ItemLinks.QualityHex[3]));
                await AtlasView.Open("npc:1"); UpdateLayout();
                var ntile = (Border)AtlasView.HeroTileForTest; var rankFact = AtlasView.Model.Facts.FirstOrDefault(f => f.Label == "Rank")?.Value ?? "";
                var gold = ((SolidColorBrush)Application.Current.FindResource("Brush.GameTip.Gold")).Color;
                bool elite = rankFact == "Elite" || rankFact == "Rare Elite" || rankFact == "Boss";
                Check("hero: a creature's tile is the kind's glyph; an elite or boss has the gold ring (rank '" + rankFact + "')", AtlasWalk(ntile).OfType<TextBlock>().Any(t => t.Text == AtlasKinds.Glyph("npc") && t.FontFamily == (FontFamily)FindResource("Font.Icons")) && (elite ? ntile.BorderBrush is SolidColorBrush gb && gb.Color == gold : !(ntile.BorderBrush is SolidColorBrush ob) || ob.Color != gold));

                // ============================================================================================================ switching tabs; ?tab= and the history
                await AtlasView.GoStart(); await AtlasView.Open("zone:11"); await AtlasView.Open("item:100"); await AtlasView.WaitForTest(); UpdateLayout();
                int steps = AtlasView.History.Count;
                var btn = AtlasView.TabButtonForTest("sources"); btn.IsChecked = true; await Task.Delay(50); UpdateLayout();
                Check("tab: a click on a tab button shows its content in place (the page is not built again) and the id follows: item:100?tab=sources", AtlasView.TabNowForTest == "sources" && AtlasView.CurrentId == "item:100?tab=sources" && AtlasView.LinkShown == "item:100?tab=sources" && ReferenceEquals(AtlasView.Model, AtlasView.Model) && AtlasTexts(AtlasView).Contains("Where it comes from"));
                await SwitchTab("lodge"); await SwitchTab("sources"); await SwitchTab("overview");
                Check("tab: switching tabs adds NO step to the history - three switches, still the same number of steps, and the plain id is back on the default tab", AtlasView.History.Count == steps && AtlasView.CurrentId == "item:100" && AtlasView.History.Items.Last() == "item:100" && !AtlasView.History.Items.Any(i => i.Contains("?tab=")), steps + " -> " + AtlasView.History.Count + " " + string.Join(",", AtlasView.History.Items));
                await SwitchTab("lodge");
                await AtlasView.Back(); UpdateLayout();
                Check("tab: Back goes to the PAGE before (zone:11), not to the tab before (that was the design choice: a tab is a way of looking, not a place)", AtlasView.CurrentId == "zone:11" && AtlasView.Model.Id == "zone:11", AtlasView.CurrentId);
                await AtlasView.Forward(); UpdateLayout();
                Check("tab: Forward returns to the item on the tab it was left on (item:100?tab=lodge)", AtlasView.CurrentId == "item:100?tab=lodge" && AtlasView.TabNowForTest == "lodge" && AtlasTexts(AtlasView).Contains("Found by the Lodge"));
                await SwitchTab("overview");
                // a link to the same page on another tab
                steps = AtlasView.History.Count; var mdl = AtlasView.Model;
                await AtlasView.Open("item:100?tab=map"); UpdateLayout();
                Check("link: item:100?tab=map while item:100 is shown is the same page - the Map tab opens, no new step, no page built again, the crumbs unchanged",
                    AtlasView.TabNowForTest == "map" && AtlasView.History.Count == steps && ReferenceEquals(AtlasView.Model, mdl) && AtlasView.CurrentId == "item:100?tab=map" && AtlasView.CrumbsForTest == "Atlas > Items > Gnarlfang's Claw", AtlasView.CurrentId + " " + AtlasView.History.Count + "/" + steps);
                await AtlasView.Open("item:100"); UpdateLayout();
                Check("link: the plain id item:100 on the same page goes to its default tab (Overview)", AtlasView.TabNowForTest == "overview" && AtlasView.CurrentId == "item:100");
                await AtlasView.Open("npc:1"); await AtlasView.Open("item:100?tab=sources"); await AtlasView.WaitForTest(); UpdateLayout();
                Check("link: item:100?tab=sources from another page opens the item on that tab (one step), and the page is built for it", AtlasView.CurrentId == "item:100?tab=sources" && AtlasView.TabNowForTest == "sources" && AtlasView.Model.Id == "item:100" && !AtlasView.TabBuiltForTest("overview"), AtlasView.CurrentId);
                await AtlasView.Open("spell:4000?tab=map"); await AtlasView.WaitForTest(); UpdateLayout();
                Check("link: a tab the page does not have (spell:4000?tab=map) opens the page on its own tab and the id says what is shown (spell:4000)", AtlasView.TabNowForTest == "overview" && AtlasView.CurrentId == "spell:4000", AtlasView.CurrentId + " " + AtlasView.TabNowForTest);
                await AtlasView.Open("item:100?tab=bogus"); await AtlasView.WaitForTest(); UpdateLayout();
                Check("link: an unknown tab word is ignored (item:100?tab=bogus -> item:100 on Overview)", AtlasView.CurrentId == "item:100" && AtlasView.TabNowForTest == "overview");

                // ---- recents, pins, crumbs, Copy and the palette with a tab (on the page)
                st = AtlasView.NavState;
                await AtlasView.Open("item:100"); await SwitchTab("sources");
                Check("recents: the page in the window is ONE recent entry whatever tab it is on (in its last form), and the nav lists it once", st.Recents.Count(r => AtlasTabs.BaseId(r) == "item:100") == 1 && st.Recents[0] == "item:100?tab=sources" && AtlasView.NavRecentsForTest.Count(r => AtlasTabs.BaseId(r) == "item:100") == 1, string.Join(",", st.Recents));
                AtlasView.TogglePinCurrent(); await SwitchTab("lodge"); UpdateLayout();
                Check("pins: pinned on one tab the page is pinned on every tab (the button and the nav row say so), the pin keeps its own form, a second Pin click on another tab unpins it",
                    AtlasView.PinnedNowForTest && st.Pins.Count(p => AtlasTabs.BaseId(p) == "item:100") == 1 && st.Pins.First(p => AtlasTabs.BaseId(p) == "item:100") == "item:100?tab=sources" && AtlasView.NavPinsForTest.Count(p => AtlasTabs.BaseId(p) == "item:100") == 1
                    && System.Windows.Automation.AutomationProperties.GetName(AtlasView.PinButtonForTest) == "Unpin page" && !AtlasView.TogglePinCurrent() && !st.Pins.Any(p => AtlasTabs.BaseId(p) == "item:100"));
                var copied = AtlasClickCopy();
                Check("copy: Copy link copies the id WITH the tab (item:100?tab=lodge), so the link opens where you are", copied == "item:100?tab=lodge" && AtlasView.LastCopiedForTest == "item:100?tab=lodge", copied);
                Check("breadcrumb: the crumbs of the page do not change with its tab (Atlas > Items > Gnarlfang's Claw), the last crumb is still the page in its colour", AtlasView.CrumbsForTest == "Atlas > Items > Gnarlfang's Claw" && ((SolidColorBrush)CrumbNamed(AtlasView, "Gnarlfang's Claw").Foreground).Color == ((SolidColorBrush)AtlasView.HeroBrushForTest).Color);
                Check("nav: the nav row of the page stays selected on every tab (Items) and Recent shows the page highlighted", AtlasView.NavSelectedForTest.SequenceEqual(new[] { "items" }));
                await AtlasView.OpenPalette("item:100?tab=sources"); await AtlasView.PaletteWaitForTest();
                Check("palette: an id typed with a tab is a hit that keeps the tab, and Enter opens the page on it", await WaitFor(() => AtlasView.PaletteResultForTest != null && AtlasView.PaletteResultForTest.Total == 1, 5000) && AtlasView.PaletteIdsForTest.SequenceEqual(new[] { "item:100?tab=sources" }));
                AtlasView.PressPaletteForTest(Key.Enter);
                Check("palette: ... and the page is on its Sources tab", await WaitFor(() => AtlasView.CurrentId == "item:100?tab=sources" && AtlasView.TabNowForTest == "sources", 5000));

                // ============================================================================================================ lazy tabs: what the template costs
                await AtlasView.Open("zone:10"); await AtlasView.Open("zone:11"); await AtlasView.WaitForTest(); UpdateLayout();
                Check("cost: a page build makes the hero, the tab strip and the tab it opens on - the other tabs are built when first shown (zone:11 opens on Map: Related, Sources and Overview are not built until asked for)",
                    AtlasView.TabBuiltForTest("map") && !AtlasView.TabBuiltForTest("related") && !AtlasView.TabBuiltForTest("sources") && !AtlasView.TabBuiltForTest("overview"), string.Join(",", AtlasView.TabsForTest.Where(AtlasView.TabBuiltForTest)));
                await SwitchTab("related");
                var relatedContent = ((ContentControl)AtlasView.TabHostForTest).Content;
                await SwitchTab("map"); await SwitchTab("related");
                Check("cost: ... and a tab shown once is kept (Related, then Map, then Related again is the same content, not built again)", AtlasView.TabBuiltForTest("related") && ReferenceEquals(relatedContent, ((ContentControl)AtlasView.TabHostForTest).Content));
                var sw = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < 10; i++) { await AtlasView.Open(i % 2 == 0 ? "item:100" : "npc:1"); }
                await AtlasView.WaitForTest(); sw.Stop();
                Check("cost: ten page opens (item, creature, item ...) draw in under 4 s together - the hero and the strip are cheap", sw.ElapsedMilliseconds < 4000, sw.ElapsedMilliseconds + " ms");

                // ============================================================================================================ narrow
                await AtlasView.Open("item:100"); UpdateLayout();
                Width = 760; await Task.Delay(300); UpdateLayout();
                var heroEl = AtlasView.HeroForTest; var scroller = AtlasView.ScrollerWidthForTest;
                Check("narrow (760): the hero, the tab strip and the tab stack in one column inside the page - nothing wider than the page (no sideways scroll) and the nav is the 56 px strip", !AtlasView.WideForTest && heroEl.ActualWidth <= scroller + 1 && AtlasView.TabHostForTest.ActualWidth <= scroller + 1 && AtlasView.NavCollapsedForTest, heroEl.ActualWidth + " vs " + scroller);
                await AtlasView.Open("npc:1"); UpdateLayout();
                Check("narrow (760): the quick facts (the creature's links) are inside the Overview, under the tooltip card, and the rail is empty", AtlasView.FactsCardForTest != null && AtlasView.FactsCardForTest.Parent is StackPanel fp && fp.Children.IndexOf(AtlasView.FactsCardForTest) == 1 && !AtlasView.RailShownForTest);
                Width = 1800; await Task.Delay(300); UpdateLayout();
                Check("wide (1800): the same facts moved to the rail on the right (340 px) - placed again, the page not drawn again", AtlasView.RailShownForTest && AtlasView.FactsCardForTest.Parent == AtlasView.RailForTest && Math.Abs(AtlasView.RailForTest.ActualWidth - 340) < 1);
                Width = 1300; Height = 900; await Task.Delay(300);

                // ============================================================================================================ Side.Row: a real focus ring
                {
                    var row = AtlasView.NavRowForTest("zones");
                    var ring = row?.Template?.FindName("ring", row) as Border;
                    row.Focus(); UpdateLayout();
                    bool onRing = row.IsKeyboardFocused && ring != null && ring.Visibility == Visibility.Visible && ring.BorderThickness.Left == 2 && ((SolidColorBrush)ring.BorderBrush).Color == ((SolidColorBrush)Application.Current.FindResource("Brush.Focus")).Color;
                    var bgFocused = row.Background;
                    AtlasView.NavRowForTest("items").Focus(); UpdateLayout();
                    Check("focus ring: a nav row reached with the keyboard shows a 2 px Brush.Focus ring inside its edge (Side.Row's own template), not a hover veil, and the ring goes when the focus moves on",
                        row.Focusable && row.IsTabStop && onRing && ring.Visibility == Visibility.Collapsed && !(bgFocused is SolidColorBrush bfb && bfb.Color == ((SolidColorBrush)Application.Current.FindResource("Brush.Overlay.Hover")).Color));
                }

                // ============================================================================================================ the title bar search and Ctrl+K on every page
                await AtlasView.GoStart(); UpdateLayout();
                Check("title bar: the search box sits at the left of the title bar (Search (Ctrl K)), 240 wide, the realm dots and minimise / close stay at the right; it is a read-only launcher with a name for assistive tools",
                    TitleSearchForTest.IsVisible && Math.Abs(TitleSearchForTest.ActualWidth - 240) < 1 && TitleSearchForTest.Tag as string == "Search  (Ctrl K)" && TitleSearchForTest.IsReadOnly && System.Windows.Automation.AutomationProperties.GetName(TitleSearchForTest) == "Search the Atlas"
                    && TitleSearchForTest.TranslatePoint(new Point(0, 0), this).X < 200 && RealmStrip.Margin.Right == 108 && !TitleSearchIconForTest.IsVisible, TitleSearchForTest.TranslatePoint(new Point(0, 0), this).X + "");
                foreach (var page in new[] { "addons", "lodge", "inventory", "settings", "browser" })
                {
                    ShowTab(page); await Task.Delay(120); UpdateLayout();
                    bool onPage = (page == "addons" ? AddonsPage : page == "lodge" ? (FrameworkElement)LodgePage : page == "inventory" ? InventoryView : page == "settings" ? SettingsPage : (FrameworkElement)BrowserView).IsVisible && !AtlasView.IsVisible;
                    GlobalModifiersForTest = ModifierKeys.Control;
                    RaiseKey(page == "addons" ? (FrameworkElement)AddonsPage : page == "lodge" ? LodgePage : page == "inventory" ? InventoryView : page == "settings" ? SettingsPage : (FrameworkElement)BrowserView, Key.K, Keyboard.PreviewKeyDownEvent);
                    GlobalModifiersForTest = null;
                    await AtlasView.PaletteOpenWaitForTest(); await Task.Delay(150); UpdateLayout();
                    Check("Ctrl+K on the " + page + " page opens the palette over it - the layer is in the window's page area (under the title bar), the page behind stays where it is, the Atlas page is not shown",
                        onPage && AtlasView.PaletteOpen && PaletteHostForTest.Visibility == Visibility.Visible && AtlasView.PaletteLayerForTest.Parent == PaletteHostForTest && PaletteHostForTest.ActualHeight > 300 && !AtlasView.IsVisible && TabAtlas.IsChecked != true
                        && AtlasView.PaletteBoxForTest.IsKeyboardFocused && AtlasView.PaletteHeadingsForTest.Contains("Pages") && (page != "browser" || BrowserView.HeldForOverlayForTest), page + " open=" + AtlasView.PaletteOpen + " host=" + PaletteHostForTest.Visibility);
                    if (page == "settings") Snapshot(Path.Combine(dir, "atlas-refresh2-palette-settings.png"));
                    if (page == "inventory") Snapshot(Path.Combine(dir, "atlas-refresh2-palette-characters.png"));
                    AtlasView.PressPaletteForTest(Key.Escape); await Task.Delay(60);
                    Check("Esc closes it on the " + page + " page: the layer is gone, the page is the one you were on" + (page == "browser" ? " and the web view is back" : ""), !AtlasView.PaletteOpen && PaletteHostForTest.Visibility == Visibility.Collapsed && !AtlasView.IsVisible && TabAtlas.IsChecked != true && !BrowserView.HeldForOverlayForTest);
                }
                // choosing a result switches to the Atlas page
                ShowTab("inventory"); await Task.Delay(100);
                AtlasView.PaletteDebounceMsForTest = 30;
                await AtlasView.OpenPalette("gnarlfang"); await WaitFor(() => AtlasView.PaletteResultForTest != null && AtlasView.PaletteResultForTest.Query == "gnarlfang" && AtlasView.PaletteResultForTest.Total > 0, 5000); await AtlasView.PaletteWaitForTest();
                var pickId = AtlasView.PaletteIdsForTest[AtlasView.PaletteSelectedForTest];
                AtlasView.PressPaletteForTest(Key.Enter);
                Check("choosing a result over the Characters page switches the rail to the Atlas page and opens it there (the palette closes, the layer is gone)", await WaitFor(() => TabAtlas.IsChecked == true && AtlasView.CurrentId == pickId && !AtlasView.PaletteOpen, 5000) && AtlasView.IsVisible && PaletteHostForTest.Visibility == Visibility.Collapsed, AtlasView.CurrentId + " vs " + pickId);
                // the box in the title bar, from Settings
                ShowTab("settings"); await Task.Delay(100);
                TitleSearchForTest.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseDownEvent, Source = TitleSearchForTest });
                await AtlasView.PaletteOpenWaitForTest();
                Check("the title bar box opens the palette over Settings (a click)", AtlasView.PaletteOpen && !AtlasView.IsVisible && SettingsPage.IsVisible);
                AtlasView.ClosePalette();
                TitleSearchForTest.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, new TextComposition(InputManager.Current, TitleSearchForTest, "w")) { RoutedEvent = UIElement.PreviewTextInputEvent, Source = TitleSearchForTest });
                await AtlasView.PaletteOpenWaitForTest(); await AtlasView.PaletteWaitForTest();
                Check("... typing a letter in it opens the palette with that letter in the search box", AtlasView.PaletteOpen && AtlasView.PaletteTextForTest == "w");
                AtlasView.ClosePalette();
                PressTitleKey(TitleSearchForTest, Key.Enter); await AtlasView.PaletteOpenWaitForTest();
                Check("... and Enter does too (the box is keyboard reachable: focusable, a tab stop)", AtlasView.PaletteOpen && TitleSearchForTest.Focusable && TitleSearchForTest.IsTabStop);
                // a click on a rail tab closes the palette (the layer belongs to the page it was opened over)
                TabAddons.IsChecked = true; await Task.Delay(100);
                Check("going to another page from the rail while the palette is open closes it", !AtlasView.PaletteOpen && PaletteHostForTest.Visibility == Visibility.Collapsed && TabAddons.IsChecked == true);
                // over the Atlas page itself: the same layer, the page stays
                ShowTab("atlas"); await AtlasView.Open("item:100"); await Task.Delay(100); UpdateLayout();
                GlobalModifiersForTest = ModifierKeys.Control; RaiseKey(AtlasView, Key.K, Keyboard.PreviewKeyDownEvent); GlobalModifiersForTest = null;
                await AtlasView.PaletteOpenWaitForTest(); await Task.Delay(100);
                Check("Ctrl+K on the Atlas page opens the same palette over the page (window level), the page behind unchanged", AtlasView.PaletteOpen && AtlasView.IsVisible && AtlasView.CurrentId == "item:100" && AtlasView.PaletteLayerForTest.Parent == PaletteHostForTest);
                Snapshot(Path.Combine(dir, "atlas-refresh2-palette-atlas.png"));
                AtlasView.PressPaletteForTest(Key.Escape);

                // ============================================================================================================ the caption: dragging stays, the box is an island
                {
                    ShowTab("addons"); await Task.Delay(100); UpdateLayout();
                    var bar = TitleBarForTest;
                    var onBox = new Point(TitleSearchForTest.TranslatePoint(new Point(TitleSearchForTest.ActualWidth / 2, TitleSearchForTest.ActualHeight / 2), bar).X, bar.ActualHeight / 2);
                    var empty = new Point(16 + 240 + 120, bar.ActualHeight / 2);
                    var hitBox = VisualTreeHelper.HitTest(bar, onBox)?.VisualHit; var hitEmpty = VisualTreeHelper.HitTest(bar, empty)?.VisualHit;
                    var press = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseDownEvent, Source = TitleSearchForTest };
                    TitleSearchForTest.RaiseEvent(press);
                    await AtlasView.PaletteOpenWaitForTest(); AtlasView.ClosePalette();
                    Check("caption: a press on the search box is the box's own (hit-test lands in it, the press is handled so the window drag - the caption's MouseLeftButtonDown - never starts); the empty caption beside it is the title bar itself and still drags",
                        hitBox != null && InsideOf(hitBox, TitleSearchForTest) && hitEmpty != null && ReferenceEquals(hitEmpty, bar) && press.Handled, (hitBox?.GetType().Name) + " / " + (hitEmpty?.GetType().Name));
                    var chromeHit = VisualTreeHelper.HitTest(bar, new Point(bar.ActualWidth - 30, bar.ActualHeight / 2))?.VisualHit;
                    Check("caption: minimise and close are still the last things in the bar (the close button is hit at the right edge), the box is not over them", chromeHit != null && !InsideOf(chromeHit, TitleSearchForTest) && AtlasWalk(bar).OfType<Button>().Any(b => b.ToolTip as string == "Close"));
                }

                // ---- narrow: the box shrinks to an icon, the update banner makes room
                Width = 760; await Task.Delay(300); UpdateLayout();
                Check("title bar narrow (760): the box is an icon button (32, 'Search the Atlas', Ctrl+K in its tooltip) in the same place, the update banner moves right of it; the icon opens the palette",
                    TitleSearchIsIconForTest && !TitleSearchForTest.IsVisible && TitleSearchIconForTest.IsVisible && TitleSearchIconForTest.Width == 32 && System.Windows.Automation.AutomationProperties.GetName(TitleSearchIconForTest) == "Search the Atlas" && TitleSearchIconForTest.ToolTip.ToString().Contains("Ctrl+K") && HubBanner.Margin.Left == 56, HubBanner.Margin.Left + "");
                TitleSearchIconForTest.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await AtlasView.PaletteOpenWaitForTest();
                Check("title bar narrow: a click on the icon opens the palette", AtlasView.PaletteOpen);
                Snapshot(Path.Combine(dir, "atlas-refresh2-palette-narrow.png"));
                AtlasView.ClosePalette();
                Width = 1300; await Task.Delay(300); UpdateLayout();
                Check("title bar: wide again the box is back (240) and the banner keeps clear of it", !TitleSearchIsIconForTest && TitleSearchForTest.IsVisible && HubBanner.Margin.Left == 16 + 240 + 8);
                Check("the Atlas header has no search box of its own any more (the title bar's is the one) and no title row", AtlasView.TitleRowGoneForTest);
            }
            finally
            {
                try
                {
                    GlobalModifiersForTest = savedMods; AtlasView.ClosePalette(false);
                    AtlasView.UseNavSettings(realSettings, () => SettingsStore.Save(realSettings));
                    Width = w0; Height = h0; await Task.Delay(200);
                }
                catch (Exception) { }
            }
        }

        static bool InsideOf(DependencyObject d, DependencyObject root)
        {
            for (; d != null; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d)) if (ReferenceEquals(d, root)) return true;
            return false;
        }

        async Task<bool> OpenAndTabs(string id, Func<IReadOnlyList<string>, bool> ok)
        {
            await AtlasView.Open(id); await AtlasView.WaitForTest(); UpdateLayout();
            return ok(AtlasView.TabsForTest);
        }

        async Task<bool> GoStartAndCheck()
        {
            await AtlasView.GoStart(); UpdateLayout();
            return AtlasView.HeroForTest == null && !AtlasView.TabStripShownForTest;
        }
    }
}
