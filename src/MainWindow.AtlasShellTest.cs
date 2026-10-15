using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Db2;

namespace ElansAddonHub
{
    // --selftest "atlaspage", part 3: the Atlas shell (Phase 1 of the refresh) on the invented world: the sub-navigation (groups, the highlighted row for every kind of page, recents,
    // pins, saved in the settings), the breadcrumb header, Home, and the Ctrl+K palette (scopes, keyboard, preview, tolerant search, cancellation, 24,000 items).
    // The palette over the world map is checked in the worldmap section (it needs the WebView2).
    public partial class MainWindow
    {
        static void RaiseKey(FrameworkElement el, Key key, RoutedEvent ev)
            => el.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(el) ?? new HwndSourceStub(), 0, key) { RoutedEvent = ev, Source = el });

        sealed class HwndSourceStub : PresentationSource
        {
            public override Visual RootVisual { get => null; set { } }
            public override bool IsDisposed => false;
            protected override CompositionTarget GetCompositionTargetCore() => null;
        }

        static TextBlock CrumbNamed(ElansAddonHub.AtlasPage page, string text) => ((page.CrumbHostForTest as ContentControl)?.Content as Panel)?.Children.OfType<TextBlock>().FirstOrDefault(t => t.Text == text);

        async Task AtlasShellChecks(string dir, CheckFn Check)
        {
            var w0 = Width; var h0s = Height;
            var realSettings = settings;
            var ns = new Settings(); int saves = 0;
            var savedModifiers = AtlasView.ModifiersForTest;
            try
            {
                AtlasView.UseNavSettings(ns, () => saves++);
                AtlasView.History.Clear();
                Width = 1300; Height = 900; await Task.Delay(300);
                await AtlasView.GoStart(); await AtlasView.StartFillForTest; await Task.Delay(100); UpdateLayout();

                // ============================================================================================================ the sub-navigation
                var rows = AtlasView.NavRowsForTest;
                Check("nav: the 216 px sub-navigation lists Home and Search on top, the groups Database, World, Character and Lodge (the Lodge has finds here) with the registry's rows, and Data sources at the bottom",
                    !AtlasView.NavCollapsedForTest && Math.Abs(AtlasView.NavWidthForTest - 216) < 1 && AtlasView.NavGroupsForTest.SequenceEqual(new[] { "Database", "World", "Character", "Lodge" })
                    && new[] { "Home", "Search", "Items", "Creatures", "Objects", "Quests", "World map", "Zones", "Dungeons and raids", "Spells and talents", "Professions", "Factions", "Finds", "Data sources" }.All(rows.Contains),
                    string.Join(" | ", rows) + " groups=" + string.Join(",", AtlasView.NavGroupsForTest));
                Check("nav: Skills, Recipes and Talents are not rows of their own (merged into Spells and talents / Professions: nav grouping only)", !rows.Contains("Skills and professions") && !rows.Contains("Recipes") && !rows.Contains("Talents") && !rows.Contains("Spells"));
                var order = AtlasNav.Entries().Select(e => e.Group + "/" + e.Title).ToList();
                Check("nav: built from the registry - World map, Zones, Dungeons and raids in World (in that order), Spells and talents, Professions, Factions in Character", order.IndexOf("World/World map") < order.IndexOf("World/Zones") && order.IndexOf("World/Zones") < order.IndexOf("World/Dungeons and raids")
                    && order.IndexOf("Character/Spells and talents") < order.IndexOf("Character/Professions") && order.IndexOf("Character/Professions") < order.IndexOf("Character/Factions") && order.Last() == "Lodge/Finds", string.Join(" | ", order));
                Check("nav: a count on the rows whose list is already built (Items), none on a hosted page", AtlasView.NavRowForTest("items") != null && AtlasTexts(AtlasView.NavRowForTest("items")).Any(t => t.Length > 0 && char.IsDigit(t[0])) && !AtlasTexts(AtlasView.NavRowForTest("professions")).Any(t => t.Length > 0 && char.IsDigit(t[0])));
                var pro = AtlasNav.Entries().First(e => e.Key == "professions"); var spt = AtlasNav.Entries().First(e => e.Key == "spells");
                Check("nav: Professions opens the hosted page (and holds Skills and Recipes), Spells and talents opens the spell list (and holds Talents)", pro.OpenId == "professions:" && pro.Members.Select(m => m.Id).OrderBy(x => x).SequenceEqual(new[] { "professions", "recipes", "skills" }) && spt.OpenId == "browse:spells" && spt.Members.Select(m => m.Id).OrderBy(x => x).SequenceEqual(new[] { "spells", "talents" }), pro.OpenId + " " + spt.OpenId);

                // the highlighted row for every kind of page
                var selected = new List<string>();
                async Task Sel(string id, string key)
                {
                    await AtlasView.Open(id); await Task.Delay(60); UpdateLayout();
                    var now = AtlasView.NavSelectedForTest;
                    if (!(now.Count == 1 && now[0] == key)) selected.Add(id + " -> [" + string.Join(",", now) + "] wanted " + key);
                }
                await Sel("browse:items", "items"); await Sel("browse:items/weapons?how=vendor", "items"); await Sel("item:100", "items"); await Sel("npc:1", "creatures"); await Sel("obj:50", "objects"); await Sel("quest:7", "quests");
                await Sel("zone:11", "zones"); await Sel("instance:98", "instances"); await Sel("spell:4000", "spells"); await Sel("skill:129", "professions"); await Sel("faction:72", "factions");
                await Sel("browse:creatures?rank=elite", "creatures"); await Sel("browse:recipes", "professions"); await Sel(AtlasNav.FindsId, "finds"); await Sel(AtlasNav.DataId, "data"); await Sel("item:99999", "items");
                await AtlasView.GoStart(); await Task.Delay(60); UpdateLayout();
                if (!(AtlasView.NavSelectedForTest.Count == 1 && AtlasView.NavSelectedForTest[0] == "home")) selected.Add("start -> " + string.Join(",", AtlasView.NavSelectedForTest));
                Check("nav: the row of the section is highlighted for every kind of page - browse lists (with and without filters), item, creature, object, quest, zone, instance, spell, skill, faction, not found, the Lodge's finds, the data sources and the start page", selected.Count == 0, string.Join(" ; ", selected));
                Check("nav: the hosted pages and the world map belong to their rows too (talents -> Spells and talents, professions -> Professions, worldmap -> World map)",
                    AtlasNav.KeyFor("talents:tree/hunter") == "spells" && AtlasNav.KeyFor("professions:cooking/recipes") == "professions" && AtlasNav.KeyFor("worldmap:kalimdor") == "worldmap" && AtlasNav.KeyFor("atlas:data") == "data" && AtlasNav.KeyFor("") == "home",
                    AtlasNav.KeyFor("talents:tree/hunter") + " " + AtlasNav.KeyFor("professions:cooking/recipes") + " " + AtlasNav.KeyFor("worldmap:kalimdor"));
                await AtlasView.Open("skill:129"); await Task.Delay(60); UpdateLayout();
                Check("nav: inside a shared row (a skill) its members are listed under it - Skills and professions, Recipes, Professions - with the one you are in marked", AtlasView.NavSubRowsForTest.OrderBy(x => x).SequenceEqual(new[] { "professions", "recipes", "skills" }) && AtlasView.NavSubRowsForTest.Count == 3, string.Join(",", AtlasView.NavSubRowsForTest));
                // a row opens its page
                ClickRow(AtlasView.NavRowForTest("zones")); await AtlasView.WaitForTest(); await Task.Delay(100);
                Check("nav: clicking a row opens its page (Zones -> browse:zones); Home goes to the start page", AtlasView.CurrentId == "browse:zones" && AtlasView.NavSelectedForTest.SequenceEqual(new[] { "zones" }));
                ClickRow(AtlasView.NavRowForTest("home")); await AtlasView.WaitForTest();
                Check("nav: ... Home goes to the start page", AtlasView.IsStart);

                // ============================================================================================================ recents
                var st = AtlasView.NavState;
                await AtlasView.Open("item:100"); await AtlasView.Open("npc:1"); await AtlasView.Open("zone:11"); await AtlasView.Open("item:100"); UpdateLayout();
                Check("recents: the pages opened last, newest first, each page once (item:100 moved to the front), the start page is never listed", st.Recents[0] == "item:100" && st.Recents[1] == "zone:11" && st.Recents.Count(r => r == "item:100") == 1 && !st.Recents.Contains("") && !st.Recents.Contains("atlas:"), string.Join(",", st.Recents));
                Check("recents: the settings hold the same list (ids only), so they are saved", ns.AtlasRecents.SequenceEqual(st.Recents) && ns.AtlasRecents.All(r => r.IndexOf(' ') < 0));
                var shown = AtlasView.NavRecentsForTest;
                Check("recents: the nav lists them under 'Recent' (the first 5), with a Clear button", shown.Count == Math.Min(5, st.Recents.Count) && shown[0] == "item:100" && AtlasTexts(AtlasView.NavScroller_ForTest).Contains("Recent") && AtlasTexts(AtlasView.NavScroller_ForTest).Contains("Clear"), string.Join(",", shown));
                for (int i = 0; i < 12; i++) await AtlasView.Open("item:" + (200 + i));
                Check("recents: at most 8 are kept, the oldest drop out", st.Recents.Count == 8 && st.Recents[0] == "item:211" && !st.Recents.Contains("item:100"), string.Join(",", st.Recents));
                await AtlasView.Open("browse:items/weapons"); await Task.Delay(100);
                st.Follow("browse:items/weapons", "browse:items/weapons?name=swo");
                Check("recents: a list whose filters change keeps ONE entry (the newest follows the filters)", st.Recents.Count(r => r.StartsWith("browse:items/weapons")) == 1 && st.Recents.Contains("browse:items/weapons?name=swo"), string.Join(",", st.Recents));
                AtlasView.FlushNav(); saves = 0;
                st.Visit("item:300"); st.Visit("item:301");
                Check("recents: a burst of changes is saved once, a moment later (not once per click)", saves == 0 && await WaitFor(() => saves == 1, 4000), "saves " + saves);
                AtlasView.UpdateNavForTest(); UpdateLayout();
                var clearBtn = AtlasWalk(AtlasView.NavScroller_ForTest).OfType<Button>().FirstOrDefault(b => b.Content as string == "Clear");
                Check("recents: the Clear button is there", clearBtn != null, string.Join(" | ", AtlasWalk(AtlasView.NavScroller_ForTest).OfType<Button>().Select(b => b.Content + "")));
                if (clearBtn != null) ClickButton(clearBtn);
                UpdateLayout();
                Check("recents: Clear empties the list and the nav's section", st.Recents.Count == 0 && AtlasView.NavRecentsForTest.Count == 0 && ns.AtlasRecents.Count == 0);

                // ============================================================================================================ pins
                await AtlasView.Open("item:100"); UpdateLayout();
                Check("pin: the header has a Pin button on a page (not on the start page), unpinned at first", AtlasView.PinShownForTest && !AtlasView.PinnedNowForTest && AtlasView.PinButtonForTest.Width == 32 && System.Windows.Automation.AutomationProperties.GetName(AtlasView.PinButtonForTest) == "Pin page");
                ClickButton(AtlasView.PinButtonForTest); UpdateLayout();
                Check("pin: the button pins the page - it shows as pinned (Unpin page) and the nav lists it under 'Pinned'", AtlasView.PinnedNowForTest && st.Pins.SequenceEqual(new[] { "item:100" }) && AtlasView.NavPinsForTest.SequenceEqual(new[] { "item:100" }) && System.Windows.Automation.AutomationProperties.GetName(AtlasView.PinButtonForTest) == "Unpin page" && AtlasTexts(AtlasView.NavScroller_ForTest).Contains("Pinned"));
                await AtlasView.Open("npc:1"); AtlasView.TogglePinCurrent(); await AtlasView.Open("browse:items/weapons?how=vendor"); AtlasView.TogglePinCurrent(); await AtlasView.Open("zone:11"); AtlasView.TogglePinCurrent(); UpdateLayout();
                Check("pin: a list with its filters can be pinned too; pins keep the order they were made in", st.Pins.SequenceEqual(new[] { "item:100", "npc:1", "browse:items/weapons?how=vendor", "zone:11" }) && AtlasView.NavPinsForTest.SequenceEqual(st.Pins), string.Join(",", st.Pins));
                Check("pin: pinned rows say what they are (names from the data, not ids)", AtlasNav.Describe("item:100").Title == "Gnarlfang's Claw" && AtlasNav.Describe("browse:items/weapons?how=vendor").Title == "Items · Weapons (filtered)" && AtlasNav.Describe("zone:11").Title == "Greenvale" && AtlasNav.Describe("npc:1").Title == "Gnarlfang");
                st.MovePin("zone:11", -2); AtlasView.UpdateNavForTest(); UpdateLayout();
                Check("pin: reorder - moving zone:11 up twice puts it second; the nav follows", st.Pins.SequenceEqual(new[] { "item:100", "zone:11", "npc:1", "browse:items/weapons?how=vendor" }) && AtlasView.NavPinsForTest.SequenceEqual(st.Pins), string.Join(",", st.Pins));
                st.MovePinTo("item:100", 3); st.MovePin("npc:1", -9); AtlasView.UpdateNavForTest(); UpdateLayout();
                Check("pin: moving to a place (and past the ends) is clamped: item:100 last, npc:1 first", st.Pins.SequenceEqual(new[] { "npc:1", "zone:11", "browse:items/weapons?how=vendor", "item:100" }) && !st.MovePin("nothing", 1), string.Join(",", st.Pins));
                var unpinX = AtlasWalk(AtlasView.NavScroller_ForTest).OfType<Button>().FirstOrDefault(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Unpin Gnarlfang");
                Check("pin: pinned rows have an unpin button", unpinX != null, string.Join(" | ", AtlasWalk(AtlasView.NavScroller_ForTest).OfType<Button>().Select(b => System.Windows.Automation.AutomationProperties.GetName(b))));
                if (unpinX != null) ClickButton(unpinX);
                UpdateLayout();
                Check("pin: the unpin button on a pinned row removes it", !st.IsPinned("npc:1") && st.Pins.Count == 3 && AtlasView.NavPinsForTest.Count == 3);
                await AtlasView.Open("zone:11"); ClickButton(AtlasView.PinButtonForTest); UpdateLayout();
                Check("pin: the Pin button on a pinned page unpins it", !st.IsPinned("zone:11") && !AtlasView.PinnedNowForTest && st.Pins.SequenceEqual(new[] { "browse:items/weapons?how=vendor", "item:100" }));
                ClickRow(AtlasView.NavRowForTest("items")); // keep a page that is not pinned open
                await AtlasView.WaitForTest();
                // saved in the settings and read back
                AtlasView.FlushNav();
                var json = Util.ToJson(ns);
                var back = Util.FromJson<Settings>(json);
                Check("persist: recents and pins are saved in the settings file (ids only) and read back the same", back.AtlasPins.SequenceEqual(ns.AtlasPins) && back.AtlasRecents.SequenceEqual(ns.AtlasRecents) && json.Contains("atlasPins") && json.Contains("atlasRecents") && ns.AtlasPins.Count == 2, json.Length > 0 ? string.Join(",", back.AtlasPins) : "");
                AtlasView.UseNavSettings(back, () => saves++); UpdateLayout();
                Check("persist: after a restart (settings loaded again) the nav lists the same recents and pins", AtlasView.NavPinsForTest.SequenceEqual(back.AtlasPins) && AtlasView.NavRecentsForTest.SequenceEqual(back.AtlasRecents.Take(5)));
                { var junk = new Settings { AtlasPins = new List<string> { "item:1", "item:1", "", "atlas:", "item:2" }, AtlasRecents = Enumerable.Range(0, 20).Select(i => "item:" + i).ToList() }; var cl = new AtlasNavState(junk.AtlasRecents, junk.AtlasPins, null);
                  Check("persist: a damaged list is cleaned (no duplicates, no blanks, no start page, at most 8 recents)", cl.Pins.SequenceEqual(new[] { "item:1", "item:2" }) && cl.Recents.Count == 8); }
                AtlasView.UseNavSettings(ns, () => saves++); st = AtlasView.NavState;

                // ============================================================================================================ the breadcrumb header
                await AtlasView.Open("item:100"); UpdateLayout();
                Check("breadcrumb: an item page reads Atlas > Items > Gnarlfang's Claw; the last crumb is the page itself (not a link) in the item's quality colour, the hero's name below it is in the same colour",
                    AtlasView.CrumbsForTest == "Atlas > Items > Gnarlfang's Claw" && CrumbNamed(AtlasView, "Gnarlfang's Claw").Cursor != Cursors.Hand && CrumbNamed(AtlasView, "Items").Cursor == Cursors.Hand && CrumbNamed(AtlasView, "Atlas").Cursor == Cursors.Hand
                    && AtlasView.HeroNameForTest == "Gnarlfang's Claw" && AtlasView.PageTitleForTest == "Gnarlfang's Claw" && ((SolidColorBrush)AtlasView.TitleBrushForTest).Color == ((SolidColorBrush)CrumbNamed(AtlasView, "Gnarlfang's Claw").Foreground).Color && ((SolidColorBrush)AtlasView.TitleBrushForTest).Color != ((SolidColorBrush)Application.Current.FindResource("Brush.Text.Primary")).Color,
                    AtlasView.CrumbsForTest);
                ClickRow(CrumbNamed(AtlasView, "Items")); await AtlasView.WaitForTest(); await Task.Delay(100);
                Check("breadcrumb: clicking 'Items' opens the item list (browse:items); the page's crumbs now read Atlas > Items", AtlasView.CurrentId == "browse:items" && AtlasView.CrumbsForTest == "Atlas > Items", AtlasView.CurrentId + " " + AtlasView.CrumbsForTest);
                await AtlasView.Open("browse:items/weapons"); await Task.Delay(100); UpdateLayout();
                Check("breadcrumb: a list reads Atlas > Items > Weapons and the header has no second title row of crumbs inside the page", AtlasView.CrumbsForTest == "Atlas > Items > Weapons" && !AtlasWalk(AtlasView.BodyForTest).OfType<TextBlock>().Any(t => t.Text == "›"), AtlasView.CrumbsForTest);
                ClickRow(CrumbNamed(AtlasView, "Items")); await AtlasView.WaitForTest();
                Check("breadcrumb: ... Items goes up one level", AtlasView.CurrentId == "browse:items");
                var zm = AtlasQuery.Build("zone:11");
                await AtlasView.Open("zone:11"); UpdateLayout();
                Check("breadcrumb: a zone reads Atlas > Zones > (its continent) > Greenvale", AtlasView.CrumbsForTest == "Atlas > Zones > " + string.Join(" > ", zm.Crumbs.Select(c => c.Title)) + " > Greenvale" && zm.Crumbs.Count > 0, AtlasView.CrumbsForTest);
                ClickRow(CrumbNamed(AtlasView, zm.Crumbs[0].Title)); await AtlasView.WaitForTest();
                Check("breadcrumb: the continent crumb opens the continent's page", AtlasView.CurrentId == zm.Crumbs[0].Link, AtlasView.CurrentId);
                ClickRow(CrumbNamed(AtlasView, "Atlas")); await AtlasView.WaitForTest();
                Check("breadcrumb: 'Atlas' goes to the start page, where the header shows just 'Atlas' and there is no hero; back / forward / start and the Copy link chip keep working", AtlasView.IsStart && AtlasView.CrumbsForTest == "Atlas" && AtlasView.HeroForTest == null && AtlasView.LinkShown == "" && !AtlasView.PinShownForTest);
                await AtlasView.Back(); UpdateLayout();
                Check("breadcrumb: Back returns to the page before, with its crumbs and the hero back", AtlasView.CurrentId == zm.Crumbs[0].Link && AtlasView.CrumbsForTest.StartsWith("Atlas > Zones") && AtlasView.HeroForTest != null && AtlasView.LinkShown == zm.Crumbs[0].Link);
                await AtlasView.Open(AtlasNav.DataId); UpdateLayout();
                Check("breadcrumb: the data sources read Atlas > Data sources", AtlasView.CrumbsForTest == "Atlas > Data sources");
                await AtlasView.Open("item:99999"); UpdateLayout();
                Check("breadcrumb: a page that was not found still has its crumbs (Atlas > Items > Item 99999)", AtlasView.CrumbsForTest == "Atlas > Items > Item 99999", AtlasView.CrumbsForTest);

                // ============================================================================================================ Home
                await AtlasView.GoStart(); await AtlasView.StartFillForTest; await Task.Delay(100); UpdateLayout();
                var homeTx = AtlasTexts(AtlasView.BodyForTest);
                Check("home: hero search (it says what to type, Ctrl K), Continue with the pages opened last as cards with icons, Browse with the categories in their groups, the shortcuts, the Lodge's finds and one status line, in that order",
                    AtlasView.HeroSearchForTest != null && homeTx.Contains("Continue") && homeTx.Contains("Browse") && AtlasView.ContinueIdsForTest.Count > 0 && homeTx.IndexOf("Continue") < homeTx.IndexOf("Browse") && homeTx.IndexOf("Browse") < homeTx.IndexOf("Recent finds in the Lodge") && homeTx.IndexOf("Recent finds in the Lodge") < homeTx.LastIndexOf("Data sources") && AtlasView.StatusLineForTest != null && homeTx.LastIndexOf("Data sources") == homeTx.Count - 1, string.Join(" | ", homeTx.Where(t => t.Length > 1)) + " idx " + homeTx.IndexOf("Continue") + "," + homeTx.IndexOf("Browse") + "," + homeTx.IndexOf("Recent finds in the Lodge") + "," + homeTx.IndexOf("Data sources"));
                Check("home: Continue shows the recents (up to 6) as cards whose names come from the data, and a click opens the page", AtlasView.ContinueIdsForTest.SequenceEqual(st.Recents.Take(6)) && homeTx.Contains(AtlasNav.Describe(AtlasView.ContinueIdsForTest[0]).Title), string.Join(",", AtlasView.ContinueIdsForTest) + " vs " + string.Join(",", st.Recents));
                var firstCard = AtlasView.ContinueIdsForTest[0];
                ClickRow(AtlasWalk(AtlasView.BodyForTest).OfType<ContentControl>().First(c => c.Tag as string == "continue|" + firstCard)); await AtlasView.WaitForTest();
                Check("home: ... the card opened that page", AtlasView.CurrentId == firstCard, AtlasView.CurrentId + " vs " + firstCard);
                await AtlasView.GoStart(); await AtlasView.StartFillForTest; UpdateLayout();
                {
                    var origLodge = AtlasQuery.Context.Lodge; var live = AtlasTestLodge();
                    live.Apply(Json.Obj("{\"t\":\"atlas.data\",\"epoch\":\"e1\",\"rev\":8,\"at\":" + (AtNow - 5) + ",\"finds\":[{\"i\":104,\"c\":1,\"at\":" + (AtNow - 60) + ",\"src\":\"npc:1\",\"z\":11,\"x\":0.3,\"y\":0.3,\"by\":\"Tess\",\"s\":9}]}"));
                    AtlasQuery.Context.Lodge = () => live;
                    try
                    {
                        await AtlasView.GoStart(); UpdateLayout();
                        var finds = AtlasWalk(AtlasView.BodyForTest).OfType<ContentControl>().Count(c => c.Tag is string t && t.StartsWith("item:"));
                        var tx5 = AtlasTexts(AtlasView.BodyForTest);
                        Check("home: the Lodge's finds sit between the shortcuts and the status line", tx5.IndexOf("Browse") < tx5.IndexOf("Recent finds in the Lodge") && tx5.IndexOf("Recent finds in the Lodge") < tx5.LastIndexOf("Data sources"));
                        Check("home: the Lodge's finds are compact - at most 4 rows - with a link to all of them (All 5 finds, which opens Finds)", finds > 0 && finds <= 4 && tx5.Contains("All 5 finds"), finds + " " + string.Join(" | ", tx5.Where(t => t.Contains("find"))));
                        ClickButton(AtlasWalk(AtlasView.BodyForTest).OfType<Button>().First(b => b.Content as string == "All 5 finds")); await AtlasView.WaitForTest();
                        Check("home: ... the All 5 finds button opens the Lodge's finds list", AtlasView.CurrentId == AtlasNav.FindsId, AtlasView.CurrentId);
                    }
                    finally { AtlasQuery.Context.Lodge = origLodge; }
                }
                await AtlasView.GoStart(); await AtlasView.StartFillForTest; UpdateLayout();
                var states = AtlasView.SourceStates();
                Check("home: the one status line has four sources (game data, Classic, Lodge, world map), each a dot and a few words", states.Select(s => s.Name).SequenceEqual(new[] { "Game data", "Classic", "Lodge", "World map" }) && states.All(s => s.Text.Length > 0) && states[0].Level == 2 && states[2].Level == 2, string.Join(" | ", states.Select(s => s.Name + ":" + s.Text + ":" + s.Level)));
                ClickRow(AtlasView.StatusLineForTest); await AtlasView.WaitForTest(); await Task.Delay(100);
                Check("home: the status line opens the Data sources page, which holds the cards (Your game data, Classic baseline, Lodge pool, World map cache)", AtlasView.CurrentId == AtlasNav.DataId && AtlasTexts(AtlasView).Contains("Your game data") && AtlasTexts(AtlasView).Contains("World map cache"));
                await AtlasView.GoStart(); await AtlasView.StartFillForTest; UpdateLayout();
                ClickRow(AtlasView.HeroSearchForTest); await Task.Delay(50);
                Check("home: a click on the hero search opens the palette", AtlasView.PaletteOpen);
                AtlasView.ClosePalette();
                await Task.Delay(300); Snapshot(Path.Combine(dir, "atlas-shell-home.png"));
                await AtlasView.Open("browse:items/weapons"); await Task.Delay(400); UpdateLayout(); Snapshot(Path.Combine(dir, "atlas-shell-browse.png"));
                await AtlasView.Open("item:100"); await MapReady(); await Task.Delay(300); UpdateLayout(); Snapshot(Path.Combine(dir, "atlas-shell-item.png"));
                await AtlasView.Open(AtlasNav.DataId); await Task.Delay(300); UpdateLayout(); Snapshot(Path.Combine(dir, "atlas-shell-data.png"));
                await AtlasView.GoStart(); await AtlasView.StartFillForTest; UpdateLayout();

                // ============================================================================================================ the palette
                AtlasView.PaletteDebounceMsForTest = 30;
                AtlasView.History.Clear(); await AtlasView.Open("item:100"); st.Pin("item:101");
                AtlasView.ModifiersForTest = ModifierKeys.Control;
                RaiseKey(AtlasView, Key.K, Keyboard.PreviewKeyDownEvent);
                AtlasView.ModifiersForTest = null; await AtlasView.PaletteOpenWaitForTest(); await Task.Delay(100); UpdateLayout();
                Check("palette: Ctrl+K opens it over the page - the search box has the focus, the scope chips are All, Items, Creatures, Quests, Zones, Spells, Pages, and the dimmed page stays behind", AtlasView.PaletteOpen && AtlasView.PaletteBoxForTest.IsKeyboardFocused && AtlasView.PaletteScopesForTest.SequenceEqual(new[] { "all", "items", "creatures", "quests", "zones", "spells", "pages" }) && AtlasView.PaletteChipForTest("all").IsChecked == true && AtlasView.PaletteScopeForTest == "all");
                Check("palette: empty, it lists Pinned, Recent and Pages (no results yet), with a hint to paste a link", AtlasView.PaletteHeadingsForTest == "Pinned|Recent|Pages" && AtlasView.PaletteTextsForTest.Any(t => t.StartsWith("Paste a link such as item:2589")), AtlasView.PaletteHeadingsForTest);
                Check("palette: the first row is selected, with its preview; the pinned page comes first", AtlasView.PaletteSelectedForTest == 0 && AtlasView.PaletteIdsForTest[0] == st.Pins[0] && AtlasView.PalettePreviewForTest.Visibility == Visibility.Visible);
                await Task.Delay(350); Snapshot(Path.Combine(dir, "atlas-shell-palette-empty.png"));
                AtlasView.PressPaletteForTest(Key.Escape);
                Check("palette: Esc closes it and the page stays", !AtlasView.PaletteOpen && AtlasView.CurrentId == "item:100" && AtlasView.PaletteLayerForTest.Visibility == Visibility.Collapsed);
                // the header box opens it as well
                TitleSearchForTest.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseDownEvent, Source = TitleSearchForTest });
                await AtlasView.PaletteOpenWaitForTest();
                Check("palette: a click in the header search box opens it too (the box is its launcher)", AtlasView.PaletteOpen);
                AtlasView.ClosePalette();
                TitleSearchForTest.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, new TextComposition(InputManager.Current, TitleSearchForTest, "w")) { RoutedEvent = UIElement.PreviewTextInputEvent, Source = TitleSearchForTest });
                await AtlasView.PaletteOpenWaitForTest(); await AtlasView.PaletteWaitForTest();
                Check("palette: typing a letter in the header box opens it with that letter already in the search box", AtlasView.PaletteOpen && AtlasView.PaletteTextForTest == "w");
                AtlasView.ClosePalette();
                RaiseKey(AtlasView, Key.Oem2, Keyboard.PreviewKeyDownEvent); await AtlasView.PaletteOpenWaitForTest();
                Check("palette: '/' opens it as well (like the web)", AtlasView.PaletteOpen);
                AtlasView.ClosePalette();

                // scopes, keyboard, preview
                await AtlasView.OpenPalette("wolf");
                await WaitFor(() => AtlasView.PaletteResultForTest != null && AtlasView.PaletteResultForTest.Query == "wolf" && AtlasView.PaletteResultForTest.Total > 0, 5000); await AtlasView.PaletteWaitForTest(); UpdateLayout();
                var allKinds = AtlasView.PaletteResultForTest.All.Select(h => h.Kind).Distinct().ToList();
                Check("palette: scope All searches every kind (items and creatures for 'wolf'), grouped, with where each comes from", allKinds.Contains("item") && allKinds.Contains("npc") && AtlasView.PaletteHeadingsForTest.Contains("Items") && AtlasView.PaletteHeadingsForTest.Contains("NPCs"), string.Join(",", allKinds));
                AtlasView.PressPaletteForTest(Key.Tab); await AtlasView.PaletteWaitForTest(); await Task.Delay(100);
                Check("palette: Tab goes to the next scope (Items): only items are left and the chip is checked", AtlasView.PaletteScopeForTest == "items" && AtlasView.PaletteChipForTest("items").IsChecked == true && AtlasView.PaletteResultForTest.All.All(h => h.Kind == "item") && AtlasView.PaletteResultForTest.Total > 0 && AtlasView.PaletteResultForTest.Scope == "items", AtlasView.PaletteScopeForTest);
                AtlasView.CycleScope(1); await AtlasView.PaletteWaitForTest();
                Check("palette: ... and on to Creatures (only creatures); Shift+Tab goes back", AtlasView.PaletteScopeForTest == "creatures" && AtlasView.PaletteResultForTest.All.All(h => h.Kind == "npc") && AtlasView.PaletteResultForTest.Total > 0);
                AtlasView.CycleScope(-1); AtlasView.CycleScope(-1); await AtlasView.PaletteWaitForTest();
                Check("palette: ... and back to All", AtlasView.PaletteScopeForTest == "all" && AtlasView.PaletteChipForTest("all").IsChecked == true);
                ClickRadio(AtlasView.PaletteChipForTest("zones")); await AtlasView.PaletteWaitForTest();
                Check("palette: clicking a chip picks that scope (Zones: nothing called wolf, and it says so with a button to search all kinds)", AtlasView.PaletteScopeForTest == "zones" && AtlasView.PaletteResultForTest.Total == 0 && AtlasView.PaletteTextsForTest.Any(t => t.StartsWith("Nothing matches \"wolf\"")) && AtlasView.PaletteTextsForTest.Contains("Search all kinds"));
                ClickButton(AtlasWalk(AtlasView.PaletteStackForTest).OfType<Button>().First(b => b.Content as string == "Search all kinds")); await AtlasView.PaletteWaitForTest();
                Check("palette: 'Search all kinds' goes back to All and finds them", AtlasView.PaletteScopeForTest == "all" && AtlasView.PaletteResultForTest.Total > 0);
                AtlasView.PaletteTextForTest = "n:wolf"; await WaitFor(() => AtlasView.PaletteScopeForTest == "creatures" && AtlasView.PaletteResultForTest != null && AtlasView.PaletteResultForTest.Scope == "creatures" && AtlasView.PaletteResultForTest.Query == "wolf", 5000); await AtlasView.PaletteWaitForTest();
                Check("palette: a prefix picks a scope - 'n:wolf' is Creatures and 'wolf' (the prefix leaves the box)", AtlasView.PaletteScopeForTest == "creatures" && AtlasView.PaletteTextForTest == "wolf" && AtlasView.PaletteResultForTest.All.All(h => h.Kind == "npc") && AtlasView.PaletteResultForTest.Total > 0, AtlasView.PaletteTextForTest);
                Check("palette: the prefixes i: n: z: q: s: p: (and an id such as item:100 is not one)", AtlasPaletteSearch.ParsePrefix("i:sword", out var ps1, out var pr1) && ps1 == "items" && pr1 == "sword" && AtlasPaletteSearch.ParsePrefix("z: Green", out var ps2, out var pr2) && ps2 == "zones" && pr2 == "Green" && AtlasPaletteSearch.ParsePrefix("q:x", out var ps3, out _) && ps3 == "quests" && AtlasPaletteSearch.ParsePrefix("s:x", out var ps4, out _) && ps4 == "spells" && AtlasPaletteSearch.ParsePrefix("p:x", out var ps5, out _) && ps5 == "pages" && !AtlasPaletteSearch.ParsePrefix("item:100", out _, out _) && !AtlasPaletteSearch.ParsePrefix("x:y", out _, out _));
                ClickRadio(AtlasView.PaletteChipForTest("all"));
                AtlasView.PaletteTextForTest = "gnarlfang";
                await WaitFor(() => AtlasView.PaletteScopeForTest == "all" && AtlasView.PaletteResultForTest != null && AtlasView.PaletteResultForTest.Query == "gnarlfang" && AtlasView.PaletteResultForTest.Scope == "all", 5000); await AtlasView.PaletteWaitForTest(); UpdateLayout();
                Check("palette: the preview of an item is its game tooltip (the card the hover shows), of a creature a few facts", AtlasView.PaletteResultForTest.Total >= 2, AtlasView.PaletteScopeForTest);
                int itemRow = AtlasView.PaletteIdsForTest.ToList().FindIndex(i => i == "item:100"); int npcRow = AtlasView.PaletteIdsForTest.ToList().FindIndex(i => i == "npc:1");
                ClickHover(AtlasView.PaletteRowForTest(itemRow)); await Task.Delay(100); UpdateLayout();
                Check("palette: selecting an item shows its GameTooltip (name, Item Level 14, Requires Level 10) in the preview", AtlasView.PaletteSelectedForTest == itemRow && GtAll<GameTooltip>(AtlasView.PalettePreviewForTest).Any(g => g.Title == "Gnarlfang's Claw" && g.Texts().Contains("Item Level 14")), string.Join(" | ", AtlasView.PalettePreviewTextsForTest));
                ClickHover(AtlasView.PaletteRowForTest(npcRow));
                bool previewed = await WaitFor(() => !AtlasView.PalettePreviewTextsForTest.Contains("Loading..."), 5000); await Task.Delay(50); UpdateLayout();
                Check("palette: selecting a creature shows what it is (name, kind, a few facts) after a short moment, built off the UI thread", previewed && AtlasView.PalettePreviewTextsForTest.Contains("Gnarlfang") && AtlasView.PalettePreviewTextsForTest.Contains("NPC") && AtlasView.PalettePreviewTextsForTest.Count >= 4, string.Join(" | ", AtlasView.PalettePreviewTextsForTest));
                await Task.Delay(350); Snapshot(Path.Combine(dir, "atlas-shell-palette-results.png"));
                AtlasView.PressPaletteForTest(Key.Down); AtlasView.PressPaletteForTest(Key.Down);
                int sel = AtlasView.PaletteSelectedForTest;
                AtlasView.PressPaletteForTest(Key.Up);
                Check("palette: Down and Up move through the results (and wrap around at the ends)", AtlasView.PaletteSelectedForTest == (sel - 1 + AtlasView.PaletteIdsForTest.Count) % AtlasView.PaletteIdsForTest.Count);
                for (int i = 0; i < AtlasView.PaletteIdsForTest.Count + 1; i++) AtlasView.PressPaletteForTest(Key.Down);
                AtlasView.PalSelectForTest(npcRow);
                var chosen = AtlasView.PaletteIdsForTest[AtlasView.PaletteSelectedForTest];
                AtlasView.PressPaletteForTest(Key.Enter); await AtlasView.WaitForTest(); await Task.Delay(100);
                Check("palette: Enter opens the selected result and closes the palette; the page is recorded as recent", AtlasView.CurrentId == chosen && chosen == "npc:1" && !AtlasView.PaletteOpen && st.Recents[0] == "npc:1", AtlasView.CurrentId);
                await AtlasView.OpenPalette("zzzzzz");
                await WaitFor(() => AtlasView.PaletteResultForTest != null && AtlasView.PaletteResultForTest.Query == "zzzzzz", 5000);
                AtlasView.PressPaletteForTest(Key.Enter); await Task.Delay(100);
                Check("palette: Enter with no result does nothing (the palette stays, the page stays)", AtlasView.PaletteOpen && AtlasView.CurrentId == "npc:1");
                AtlasView.PaletteDimForTest.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent, Source = AtlasView.PaletteDimForTest });
                Check("palette: a click on the dimmed page behind closes it", !AtlasView.PaletteOpen);
                await AtlasView.OpenPalette("item:105");
                await WaitFor(() => AtlasView.PaletteResultForTest != null && AtlasView.PaletteResultForTest.Query == "item:105", 5000);
                Check("palette: a pasted link (item:105) is the one result, opened with Enter; a link to a list or hosted page opens that (browse:zones)", AtlasView.PaletteResultForTest.ById && AtlasView.PaletteIdsForTest.SequenceEqual(new[] { "item:105" }));
                AtlasView.PressPaletteForTest(Key.Enter); await AtlasView.WaitForTest(); await Task.Delay(50);
                Check("palette: ... item:105 opened", AtlasView.CurrentId == "item:105");
                await AtlasView.OpenPalette("browse:zones"); await WaitFor(() => AtlasView.PaletteResultForTest != null && AtlasView.PaletteResultForTest.Query == "browse:zones", 5000);
                Check("palette: browse:zones is one 'Pages' result", AtlasView.PaletteResultForTest.OpenId == "browse:zones" && AtlasView.PaletteIdsForTest.SequenceEqual(new[] { "browse:zones" }));
                AtlasView.PressPaletteForTest(Key.Enter); await AtlasView.WaitForTest(); await Task.Delay(50);
                Check("palette: ... and it opens the list", AtlasView.CurrentId == "browse:zones" && !AtlasView.PaletteOpen);

                // pages scope
                await AtlasView.OpenPalette("", "pages"); await AtlasView.PaletteWaitForTest(); UpdateLayout();
                Check("palette: the Pages scope with an empty box lists the pages of the Atlas (every category, the data sources)", AtlasView.PaletteIdsForTest.Contains("browse:items") && AtlasView.PaletteIdsForTest.Contains("browse:zones") && AtlasView.PaletteIdsForTest.Contains("professions:") && AtlasView.PaletteIdsForTest.Contains("talents:") && AtlasView.PaletteIdsForTest.Contains(AtlasNav.DataId) && AtlasView.PaletteIdsForTest.Contains("worldmap:"), string.Join(",", AtlasView.PaletteIdsForTest));
                AtlasView.PaletteTextForTest = "herb"; await WaitFor(() => AtlasView.PaletteResultForTest != null && AtlasView.PaletteResultForTest.Query == "herb", 5000); await AtlasView.PaletteWaitForTest();
                Check("palette: Pages by a keyword ('herb' finds Objects, which holds herbs)", AtlasView.PaletteIdsForTest.Contains("browse:objects"), string.Join(",", AtlasView.PaletteIdsForTest));
                AtlasView.PaletteTextForTest = "data"; await WaitFor(() => AtlasView.PaletteResultForTest != null && AtlasView.PaletteResultForTest.Query == "data", 5000); await AtlasView.PaletteWaitForTest();
                Check("palette: ... and 'data' finds the Data sources page", AtlasView.PaletteIdsForTest.Contains(AtlasNav.DataId));
                AtlasView.ClosePalette();

                // tolerant search and cancellation
                var fz1 = AtlasPaletteSearch.Run("bandage heavy", "all", CancellationToken.None);
                var fz2 = AtlasPaletteSearch.Run("gnarlfangx", "all", CancellationToken.None);
                var fz3 = AtlasPaletteSearch.Run("qqqzzz", "all", CancellationToken.None);
                var fz4 = AtlasPaletteSearch.Run("boss gnarlfang", "all", CancellationToken.None);
                Check("palette: a tolerant second look - the words in another order ('bandage heavy' finds Heavy Linen Bandage; 'boss gnarlfang' finds the creature by two words from its name and its detail), a slip at the end ('gnarlfangx' shows Gnarlfang and says so); nonsense finds nothing",
                    fz1.All.Any(h => h.Name == "Heavy Linen Bandage") && fz4.Fuzzy && fz4.All.Any(h => h.Id == "npc:1") && !fz4.All.Any(h => h.Kind == "item") && fz2.Fuzzy && fz2.Note.Contains("Nothing is called \"gnarlfangx\"") && fz2.All.Any(h => h.Name == "Gnarlfang") && fz3.Total == 0 && !fz3.Fuzzy, fz1.Total + " " + fz2.Total + " " + fz2.Note);
                var cts = new CancellationTokenSource(); cts.Cancel();
                bool cancelled = false; try { AtlasPaletteSearch.Run("wolf", "all", cts.Token); } catch (OperationCanceledException) { cancelled = true; }
                bool cancelled2 = false; try { AtlasQuery.Search("wolf", 6, cts.Token); } catch (OperationCanceledException) { cancelled2 = true; }
                Check("palette: a search can be cancelled (a cancelled token stops AtlasPaletteSearch and AtlasQuery.Search)", cancelled && cancelled2);
                int uiThread = Thread.CurrentThread.ManagedThreadId; int searchThread = -1;
                await AtlasView.OpenPalette("wolf"); await AtlasView.PaletteWaitForTest();
                AtlasView.PaletteSearchHookForTest = () => { searchThread = Thread.CurrentThread.ManagedThreadId; Thread.Sleep(250); };
                AtlasView.PaletteTextForTest = "linen"; await Task.Delay(80);
                int ticks = 0; var tm = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) }; tm.Tick += (s, e) => ticks++; tm.Start();
                AtlasView.PaletteTextForTest = "gnarlfang";           // a newer keystroke while the slow search for 'linen' runs
                await WaitFor(() => AtlasView.PaletteResultForTest != null && AtlasView.PaletteResultForTest.Query == "gnarlfang", 5000); await AtlasView.PaletteWaitForTest(); tm.Stop();
                AtlasView.PaletteSearchHookForTest = null;
                await Task.Delay(400);
                Check("palette: the search runs on a worker thread (the window keeps ticking) and a slower, older answer never replaces a newer one ('linen' was typed, then 'gnarlfang')", searchThread > 0 && searchThread != uiThread && ticks >= 10 && AtlasView.PaletteResultForTest.Query == "gnarlfang" && AtlasView.PaletteIdsForTest.Contains("npc:1"), "search thread " + searchThread + " ui " + uiThread + " ticks " + ticks + " q=" + AtlasView.PaletteResultForTest?.Query);
                AtlasView.ClosePalette();

                // 24,000 items
                var savedItems = ItemData.Count;
                var bigItems = new List<ClientItem>(); for (int i = 0; i < 24000; i++) bigItems.Add(CItem(5000 + i, (i % 7 == 0 ? "Sword of " : "Staff of ") + "Test " + i, i % 6, 10 + i % 50, 1 + i % 60, 13, 2, 7));
                bigItems.Add(CItem(99000, "Gnarlfang's Claw", 3, 14, 10, 13, 2, 7, 12050));
                ItemData.SetForTest(new ItemSnapshot(bigItems.ToArray(), new ClientItem[0]), ItemData.State.Ready);
                var swBig = Stopwatch.StartNew();
                var big = await Task.Run(() => AtlasPaletteSearch.Run("sword of test 12", "all", CancellationToken.None));
                swBig.Stop();
                var swNav = Stopwatch.StartNew(); await AtlasView.GoStart(); UpdateLayout(); swNav.Stop();
                await AtlasView.StartFillForTest;
                Check("performance: with 24,000 items the palette searches in well under a second (" + swBig.ElapsedMilliseconds + " ms, " + big.Total + " hits) and Home draws from the caches without waiting (" + swNav.ElapsedMilliseconds + " ms)", swBig.ElapsedMilliseconds < 1500 && big.Total > 0 && big.All.First().Name.StartsWith("Sword of Test 12") && swNav.ElapsedMilliseconds < 2000, swBig.ElapsedMilliseconds + " ms / " + swNav.ElapsedMilliseconds + " ms");
                var swNav2 = Stopwatch.StartNew(); AtlasView.UpdateNavForTest(); swNav2.Stop();
                Check("performance: building the nav again takes a few milliseconds (" + swNav2.ElapsedMilliseconds + " ms): it only reads the registry, the counts of finished lists and the two id lists", swNav2.ElapsedMilliseconds < 100);
                ItemData.SetForTest(AtlasTestItems(), ItemData.State.Ready);

                // a narrow window: the nav is an icon strip with tooltips
                Width = 760; await Task.Delay(300); UpdateLayout();
                await AtlasView.Open("item:100"); await Task.Delay(100); UpdateLayout();
                var rowsNarrow = AtlasView.NavRowsForTest;
                Check("nav: in a narrow window (under 1000 px) it is a 56 px strip of icons - the rows keep their names as tooltips and names for assistive tools, the selected row is still marked", AtlasView.NavCollapsedForTest && Math.Abs(AtlasView.NavWidthForTest - 56) < 1 && rowsNarrow.Contains("Items") && AtlasView.NavRowForTest("items").ToolTip as string == "Items" && AtlasView.NavSelectedForTest.SequenceEqual(new[] { "items" }) && !AtlasTexts(AtlasView.NavScroller_ForTest).Contains("Creatures"));
                Snapshot(Path.Combine(dir, "atlas-shell-narrow.png"));
                Width = 1300; await Task.Delay(300); UpdateLayout();
                Check("nav: ... and the full nav is back when the window is wide again", !AtlasView.NavCollapsedForTest && AtlasTexts(AtlasView.NavScroller_ForTest).Contains("Creatures"));
            }
            catch (Exception e) { Check("atlas shell checks ran to the end", false, e.ToString()); }
            finally
            {
                try
                {
                    AtlasView.ModifiersForTest = savedModifiers; AtlasView.PaletteSearchHookForTest = null; AtlasView.ClosePalette();
                    AtlasView.UseNavSettings(realSettings, () => SettingsStore.Save(realSettings));
                    Width = w0; Height = h0s; await Task.Delay(200);
                }
                catch (Exception) { }
            }
        }

        static void ClickRadio(RadioButton rb) { rb.IsChecked = true; }
        static void ClickHover(FrameworkElement row) => row.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = UIElement.MouseEnterEvent, Source = row });
    }
}
