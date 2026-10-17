using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Atlas.Classic;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Maps;

namespace ElansAddonHub
{
    // --selftest "atlashome": Atlas Home (Outpost 2.74, AtlasPage.Home.cs, Services/Atlas/AtlasHome.cs) on the invented world of the Atlas page test: the hero with a
    // live character (zone, painting, the zone note's first sentence, the fit of the creature levels, the status chips, XP and rested), the saved character ("last seen"),
    // a character in a dungeon, no character at all; the cards built from the fixture data (quests, dungeons, rares, upgrades, recipes); the Lodge today line and "near you"
    // with the members' sharing respected; a live zone change, a combat flag and a level-up that change the hero and the cards in place (no new page, no scroll jump);
    // the cave fix (a uiMap the zone table has no name for is walked up its breadcrumb to the zone around it); screenshots.
    public partial class MainWindow
    {
        async Task<string> AtlasHomeTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            void Check(string what, bool cond, string detail = null) { SelfTestClock.Mark(what); notes.Add((cond ? "ok   " : "FAIL ") + what + (!cond && detail != null ? " [" + detail + "]" : "")); ok &= cond; }
            var work = Path.Combine(dir, "atlashome");
            var savedLodge = AtlasQuery.Context.Lodge; var savedNow = AtlasQuery.Context.NowUtc; var savedOwned = AtlasQuery.Context.Owned;
            var savedTiles = MapRender.TileSourceForTest; var savedMapDir = MapRender.DirOverride;
            var savedLodgeState = AtlasView.LodgeState; var savedWidth = Width; var h0 = Height;
            var savedLive = AtlasView.LiveSource; var savedHomeLodge = AtlasView.HomeLodgeSource; var savedDebounce = ElansAddonHub.AtlasPage.HomeDebounceMs;
            bool artAdded = false;
            try
            {
                if (Directory.Exists(work)) Util.DeleteDir(work);
                Directory.CreateDirectory(work);
                Check("fixture: everything of this test is under the scratch folder (never the real game)", Path.GetFullPath(work).StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase));

                // ---- the invented world, a cave in Greenvale and a rare
                MapRender.DirOverride = Path.Combine(work, "maps");
                MapRender.TileSourceForTest = fdid => Solid(256, 256, 70, 96, 60);
                var maps = AtlasTestMapSet();
                maps.UiMaps.Add(new UiMapInfo { Id = 60, Name = "Wolf Den", ParentId = 11, Type = UiMapInfo.Micro, Flags = 2 });          // a cave in Greenvale: no name in the strip's zone table
                maps.UiMaps.Add(new UiMapInfo { Id = 61, Name = "Wolf Den Depths", ParentId = 60, Type = UiMapInfo.Dungeon, Flags = 2 }); // a floor under the cave
                var idx = new MapIndex(maps);
                var classic = AtlasTestClassic();
                classic.Npcs[70] = new ClassicNpc { Id = 70, Name = "Old Greyjaw", MinLevel = 10, MaxLevel = 10, Rank = 4, Type = 1, FactionId = 14, Spawns = new[] { Sp(0, 700, 1500) } };
                var lodge = AtlasTestLodge();
                var elanCore = new HubCore { Name = "Elan", Realm = "Forever", Class = "Hunter", ClassFile = "HUNTER", RaceFile = "Orc", Race = "Orc", Zone = "Greenvale", Level = 9, Xp = 600, XpMax = 1000, Rested = 200, Updated = AtNow - 3 * 3600 };
                var elan = new InvChar { Key = "Forever-Elan", Name = "Elan", Realm = "Forever", Class = "HUNTER", RaceFile = "Orc", Sex = 2, Level = 9, Updated = AtNow - 3 * 3600, Core = elanCore };
                var mira = new InvChar { Key = "Forever-Mira", Name = "Mira", Realm = "Forever", Class = "MAGE", Level = 22, Updated = AtNow - 9 * 86400 };
                var chars = new List<InvChar> { elan, mira };
                ItemData.SetForTest(AtlasTestItems(), ItemData.State.Ready);
                SpellData.SetForTest(AtlasTestSpells());
                SeenData.SetForTest(AtlasTestSeen());
                MapData.SetForTest(idx);
                ClassicAtlas.Use(classic);
                AtlasQuery.Context.Lodge = () => lodge;
                AtlasQuery.Context.NowUtc = () => AtUtc(AtNow);
                AtlasView.OwnedOverrideForTest = AtlasTestOwned;
                AtlasView.CharsOverrideForTest = () => chars;
                AtlasBrowse.ForgetForTest();
                AtlasView.LodgeState = () => "Connected · sharing your observations and receiving the Lodge's";
                ZoneLore.SetForTest("{\"zones\":[{\"zoneId\":11,\"name\":\"Greenvale\",\"summary\":\"A green vale of wolves and old mills. The river runs down to Highkeep.\"}]}");
                if (!ArtLibrary.Zones.ContainsKey(11)) { ArtLibrary.Zones[11] = ArtLibrary.Zones[1411]; artAdded = true; }          // Greenvale borrows Durotar's painting for the test
                AtlasHome.UpgradesOverrideForTest = (h, me) => me == null || h.ZoneId != 11 ? null : new HomeCard
                {
                    Key = "upgrades", Title = "Upgrades from here", Total = 1, Note = "Better than what " + me.Name + " wears, from " + h.ZoneName + ".",
                    Rows = { new AtlasRow { Link = "item:100", Title = "Gnarlfang's Claw", Quality = 3, Detail = "+14% · Drop · Gnarlfang · Greenvale · 10%" } },
                };
                AtlasHome.RecipesOverrideForTest = me => me?.Key == "Forever-Elan" ? 4 : 0;
                ElansAddonHub.AtlasPage.HomeDebounceMs = 120;

                // ============================================================================================================ the cave fix (unit level)
                Check("cave: the zone of a map is the nearest zone up its breadcrumb (Wolf Den -> Greenvale, a floor under it too); a zone is itself; a continent or a dungeon with no zone above has none",
                    AtlasHome.ZoneOf(idx, 60) == 11 && AtlasHome.ZoneOf(idx, 61) == 11 && AtlasHome.ZoneOf(idx, 11) == 11 && AtlasHome.ZoneOf(idx, 10) == 0 && AtlasHome.ZoneOf(idx, 20) == 0 && AtlasHome.ZoneOf(idx, 9999) == 0 && AtlasHome.ZoneOf(null, 60) == 0);
                var caveChar = new GamePresence.Character { Name = "Elan", ClassFile = "HUNTER", Level = 9, Online = true };
                var caveInfo = new StripCodec.Info { Name = "Elan", ClassId = 3, Level = 9, HasV2 = true, MapId = 60, Flags1 = 0, Flags2 = 2, XpPercent = 40 };
                GamePresence.ApplyLive(caveChar, caveInfo);
                var caveHere = AtlasHome.Resolve(new HomeLive { Char = caveChar, Strip = caveInfo, Playing = true }, chars, idx);
                Check("cave: the status strip says uiMap 60, which the zone table cannot name, so the presence has no zone (as before) - Home walks the breadcrumb and says Greenvale (zone 11)",
                    caveChar.Zone == null && caveHere.ZoneId == 11 && caveHere.ZoneName == "Greenvale" && caveHere.Live && caveHere.CharKey == "Forever-Elan", caveChar.Zone + " / " + caveHere.ZoneId + " " + caveHere.ZoneName);
                var named = AtlasHome.Resolve(new HomeLive { Char = new GamePresence.Character { Name = "Elan", Level = 9, Zone = "Greenvale" }, Playing = true }, chars, idx);
                Check("zone by name: without a strip map the presence's zone name is looked up (Greenvale -> 11); an unknown name keeps the name with no id", named.ZoneId == 11
                    && AtlasHome.Resolve(new HomeLive { Char = new GamePresence.Character { Name = "Elan", Level = 9, Zone = "Nowhere Land" }, Playing = true }, chars, idx).ZoneName == "Nowhere Land");
                Check("fit: the zone's creature range against the level - good inside (and up to 3 over), a bit high up to 5 under, too high further down, low (little XP) far over",
                    AtlasHome.Fit(9, 5, 7) == HomeFit.Good && AtlasHome.Fit(10, 5, 7) == HomeFit.Good && AtlasHome.Fit(11, 5, 7) == HomeFit.Low && AtlasHome.Fit(3, 5, 7) == HomeFit.High && AtlasHome.Fit(1, 8, 10) == HomeFit.TooHigh && AtlasHome.Fit(5, 0, 0) == HomeFit.Unknown
                    && AtlasHome.FitText(HomeFit.Good) == "Good for your level" && AtlasHome.FitText(HomeFit.Low) == "Low for you, little XP");
                Check("one-liner: the first sentence of the zone note", AtlasHome.OneLiner("A green vale. The river runs.") == "A green vale." && AtlasHome.OneLiner("Just one") == "Just one" && AtlasHome.OneLiner(null) == "");

                // ============================================================================================================ the hero, live
                GamePresence.Character Live(string zone, int level = 9, int flags = 1 + 128, int group = 3, int xp = 62, bool rested = true, string instance = null)
                    => new GamePresence.Character { Name = "Elan", Realm = "Forever", Class = "Hunter", ClassFile = "HUNTER", RaceFile = "Orc", Sex = 2, Level = level, Online = true, HasLive = true, CombatKnown = true, Flags = flags, GroupSize = group, XpPercent = xp, Rested = rested, Zone = zone, InInstance = instance != null, InstanceName = instance };
                HomeLive live = new HomeLive { Char = Live("Greenvale"), Playing = true };
                AtlasView.LiveSource = () => live;
                var members = new List<HomeMember>
                {
                    new HomeMember { Name = "Tess", CharName = "Tessa", Place = "Greenvale", Level = 10, Playing = true, Status = "In combat" },
                    new HomeMember { Name = "Bob", CharName = "Bobby", Place = "Greenvale", Level = 12, Playing = true, Invisible = true },          // appears offline: never shown
                    new HomeMember { Name = "Kim", CharName = "Kimi", Place = "", Level = 14, Playing = true },                                     // keeps the zone to herself
                    new HomeMember { Name = "Zed", CharName = "Zedd", Place = "Redmoor", Level = 30, Playing = true },
                    new HomeMember { Name = "Me", CharName = "Elan", Place = "Greenvale", Level = 9, Playing = true, IsMe = true },
                };
                var homeLodge = new HomeLodge { Online = 4, Members = members, NextEventTitle = "Raid night", NextEventUtc = AtUtc(AtNow + 86400 + 3600), NextResetTitle = "Raid reset", NextResetUtc = AtUtc(AtNow + 3 * 86400) };
                AtlasView.HomeLodgeSource = () => homeLodge;
                ShowTab("atlas");
                Width = 1300; Height = 900; await Task.Delay(200);
                async Task Home() { AtlasView.History.Clear(); await AtlasView.GoStart(); await AtlasView.StartFillForTest; await AtlasView.HomeCardsFillForTest; await Task.Delay(60); UpdateLayout(); }
                await Home();
                var hero = AtlasView.HomeHeroForTest;
                var htx = AtlasTexts(hero);
                var here = AtlasView.HomeHereForTest;
                FrameworkElement HeroGrid() => (hero as Border)?.Child as FrameworkElement;
                Check("hero, live: 'You are in Greenvale' with Elan (class colour), level 9 Hunter, as the first block of Home", here != null && here.Live && here.ZoneId == 11 && htx.Contains("You are in Greenvale")
                    && AtlasWalk(hero).OfType<TextBlock>().Any(t => t.Tag as string == "home-who" && TextOf(t).StartsWith("Elan · Level 9 Hunter")), string.Join(" | ", htx));
                Check("hero, live: the zone's painting behind a see-through card (home-hero|zone|zone:11), 176 of picture above the card", HeroGrid()?.Tag as string == "home-hero|zone|zone:11"
                    && AtlasWalk(hero).OfType<FrameworkElement>().Any(e => e.Tag as string == "home-hero-art|zone") && hero.ActualHeight > ElansAddonHub.AtlasPage.BannerHeight, HeroGrid()?.Tag as string);
                Check("hero, live: the zone note's first sentence under the place", htx.Contains("A green vale of wolves and old mills.") && !htx.Any(t => t.Contains("The river runs")));
                AtlasHome.ZoneLevels(idx, 11, out var zlo, out var zhi);
                var fitWant = AtlasHome.FitText(AtlasHome.Fit(9, zlo, zhi));
                Check("hero, live: the creature-level fit for level 9 in Greenvale (creatures " + AtlasFormat.Levels(zlo, zhi) + ": '" + fitWant + "') and the range as a chip", zlo > 0 && fitWant.Length > 0 && htx.Contains(fitWant) && htx.Contains("Creatures " + AtlasFormat.Levels(zlo, zhi)), string.Join(" | ", htx));
                Check("hero, live: the status chips from the strip (In combat, Grouped (3)), XP 62% and the Rested badge", htx.Contains("In combat") && htx.Contains("Grouped (3)") && AtlasView.HomeXpForTest == 62 && htx.Contains("62% XP") && htx.Contains("Rested"), string.Join(" | ", htx));
                Check("home: no category tiles, no 'Browse' (the side menu has the categories)", !AtlasTexts(AtlasView.BodyForTest).Contains("Browse") && !AtlasWalk(AtlasView.BodyForTest).OfType<ContentControl>().Any(c => c.Tag is string t && t == "browse:items"));
                await ShotWait(500); Snapshot(Path.Combine(dir, "atlashome-live.png"));

                // ---- opening Home: once it shows, the page never shifts sideways and the scroll bar does not pop up or away while its parts fill in (frame by frame,
                // from another Atlas page and from the Addons tab, at heights around the one where the page just fits; on 2.73 the bar came up a frame after Home
                // was shown, when the shortcuts and the cards arrived)
                {
                    async Task<string> Frames(Func<Task> open)
                    {
                        var seen = new List<string>();
                        var sv = AtlasView.ScrollerForTest;
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        EventHandler tick = (s, e) =>
                        {
                            var body = AtlasView.BodyForTest;
                            if (!body.IsVisible || body.Opacity <= 0.01 || !AtlasView.IsStart || AtlasView.HomeHeroForTest == null) return;   // Home not shown yet
                            double x = Math.Round(body.TranslatePoint(new Point(0, 0), this).X, 1), w = Math.Round(body.ActualWidth, 1);
                            var f = x + "/" + w + "/" + sv.ComputedVerticalScrollBarVisibility;
                            if (seen.Count == 0 || !seen[seen.Count - 1].StartsWith(f)) seen.Add(f + " (" + sw.ElapsedMilliseconds + " ms, " + Math.Round(sv.ExtentHeight) + " of " + Math.Round(sv.ViewportHeight) + ")");
                        };
                        System.Windows.Media.CompositionTarget.Rendering += tick;
                        try { await open(); await AtlasView.StartFillForTest; await AtlasView.HomeCardsFillForTest; await Task.Delay(400); }
                        finally { System.Windows.Media.CompositionTarget.Rendering -= tick; }
                        return string.Join(" ; ", seen);
                    }
                    async Task<List<string>> Sweep()
                    {
                        var moved = new List<string>();
                        foreach (var hh in new[] { 700, 900, 1100, 1300, 1500 })
                        {
                            Height = hh; await Task.Delay(120);
                            await AtlasView.Open("quest:7"); await AtlasView.WaitForTest();
                            AtlasBrowse.ForgetForTest();                                   // the shortcuts and the cards take a moment, as on a first visit
                            var fp = await Frames(() => AtlasView.GoStart());
                            if (fp.Contains(" ; ") || fp.Length == 0) moved.Add(hh + " from a page: " + fp);
                            ShowTab("addons"); await Task.Delay(80);
                            AtlasView.History.Clear(); await AtlasView.Open("quest:7"); await AtlasView.WaitForTest(); _ = AtlasView.GoStart();   // Home drawn while the tab is away
                            AtlasBrowse.ForgetForTest();
                            var ft = await Frames(async () => { ShowTab("atlas"); await Task.Delay(1); });
                            if (ft.Contains(" ; ") || ft.Length == 0) moved.Add(hh + " from the Addons tab: " + ft);
                        }
                        return moved;
                    }
                    Width = 1300;
                    // the lists behind the shortcuts and the cards take 100 ms each, as on a first visit with a busy disk (on a fast PC they could land before the first frame)
                    var savedHook = AtlasQuery.BuildHookForTest;
                    AtlasQuery.BuildHookForTest = id => { if (id != null && id.StartsWith("browse:", StringComparison.Ordinal)) System.Threading.Thread.Sleep(100); };
                    List<string> before, now;
                    var savedCard = settings.AtlasSourcesCardDismissed;
                    settings.AtlasSourcesCardDismissed = true;              // the heights below are tuned to the page without the "Where this comes from" card (2.91; a fixed block drawn with the page, it cannot pop a bar up)
                    try
                    {
                        ElansAddonHub.AtlasPage.HomeHoldOffForTest = true;
                        try { before = await Sweep(); } finally { ElansAddonHub.AtlasPage.HomeHoldOffForTest = false; }
                        now = await Sweep();
                    }
                    finally { AtlasQuery.BuildHookForTest = savedHook; settings.AtlasSourcesCardDismissed = savedCard; }
                    Check("opening Home: the check sees the 2.73 behaviour (shown at once, then the parts fill in): the scroll bar popped up after the page showed in " + before.Count + " of 10 openings", before.Count > 0, string.Join(" || ", before));
                    Check("opening Home: once it shows, the page keeps its left edge and width and the scroll bar neither pops up nor away while the parts fill in (10 openings, 700-1500 high, from a page and from the Addons tab)", now.Count == 0, string.Join(" || ", now));
                    Check("opening Home: the page is shown (not held back) once its parts are in", !AtlasView.HomeHeldForTest && AtlasView.BodyForTest.Opacity > 0.99 && AtlasView.ScrollerForTest.VerticalScrollBarVisibility == ScrollBarVisibility.Auto);
                    Width = 1300; Height = 900; await Task.Delay(150);
                    await Home();
                }

                // ---- the cards
                var keys = AtlasView.HomeCardKeysForTest;
                FrameworkElement Card(string k) => AtlasView.HomeCardForTest(k);
                List<string> CardTexts(string k) => Card(k) == null ? new List<string>() : AtlasTexts(Card(k));
                Check("cards: for the live character (selected chip = Elan), only the cards with data - quests, rares, upgrades, recipes (no dungeon fits level 9)",
                    AtlasView.HomeCharForTest == "Forever-Elan" && AtlasView.HomeCharChipSelectedForTest("Forever-Elan") && keys.SequenceEqual(new[] { "quests", "rares", "upgrades", "recipes" }), string.Join(",", keys));
                var qt = CardTexts("quests");
                Check("cards: Quests here - Greenvale's quests near level 9 (The Lost Pup 6, A Forever Only Errand 9; not the level 3 errand), a note that finished quests still show, a link to the list",
                    qt.Contains("Quests here") && qt.Contains("The Lost Pup") && qt.Contains("A Forever Only Errand") && !qt.Contains("Classic Only Errand") && qt.Any(t => t.Contains("cannot tell yet"))
                    && AtlasWalk(Card("quests")).OfType<Button>().Any(b => (b.Tag as string ?? "").StartsWith("home-more|browse:quests?zone=11")), string.Join(" | ", qt));
                var rt = CardTexts("rares");
                Check("cards: Rares here - Old Greyjaw (rare, level 10) with where it spawns in Greenvale", rt.Contains("Old Greyjaw") && rt.Any(t => t.StartsWith("Level 10") && t.Contains("spawns at")), string.Join(" | ", rt));
                Check("cards: Upgrades from here (worked out off the UI thread) and Recipes to learn (4, with a link)", CardTexts("upgrades").Contains("Gnarlfang's Claw") && CardTexts("recipes").Contains("4")
                    && AtlasWalk(Card("recipes")).OfType<Button>().Any(b => b.Tag as string == "home-more|" + AtlasHome.RecipesId("Forever-Elan")), string.Join(" | ", CardTexts("recipes")));
                var qRow = AtlasWalk(Card("quests")).OfType<ContentControl>().FirstOrDefault(c => c.Tag as string == "home-row|quest:7");
                ClickRow(qRow); await AtlasView.WaitForTest();
                Check("cards: a row opens its page (quest:7)", qRow != null && AtlasView.CurrentId == "quest:7", AtlasView.CurrentId);
                await Home();
                ClickRow(AtlasWalk(AtlasView.HomeHeroForTest).OfType<ContentControl>().First(c => c.Tag as string == "home-hero-card")); await AtlasView.WaitForTest();
                Check("hero: a click opens the zone page (zone:11)", AtlasView.CurrentId == "zone:11", AtlasView.CurrentId);
                await Home();
                await AtlasView.SelectHomeCharForTest("Forever-Mira"); await AtlasView.HomeCardsFillForTest; UpdateLayout();
                var mk = AtlasView.HomeCardKeysForTest;
                Check("cards: picking another character (Mira, level 22, no saved zone) drives the cards - no zone cards, the dungeons for her level", AtlasView.HomeCharForTest == "Forever-Mira" && !mk.Contains("quests") && !mk.Contains("rares") && mk.Contains("dungeons") == (AtlasBrowse.Run(AtlasHome.DungeonsId(22)).Total > 0), string.Join(",", mk));
                await AtlasView.SelectHomeCharForTest("Forever-Elan"); await AtlasView.HomeCardsFillForTest; UpdateLayout();

                // ---- the Lodge today and near you
                var lodgeBlock = AtlasView.HomeLodgeForTest;
                var ltx = lodgeBlock == null ? new List<string>() : AtlasTexts(lodgeBlock);
                var line = ltx.FirstOrDefault(t => t.Contains("online")) ?? "";
                int today = AtlasHome.FindsToday(lodge, AtUtc(AtNow));
                Check("lodge today: one line - members online, the finds of today, the next event and raid reset", line.StartsWith("4 members online") && line.Contains(today == 0 ? "no finds today" : AtlasFormat.Count(today, "find", "finds") + " today") && line.Contains("Raid night") && line.Contains("Raid reset"), line);
                var near = lodgeBlock == null ? new List<string>() : AtlasWalk(lodgeBlock).OfType<Border>().Select(b => b.Tag as string).Where(t => t != null && t.StartsWith("member|")).ToList();
                Check("lodge today: near you in Greenvale - only Tessa (not Bobby who appears offline, not Kimi who keeps her zone to herself, not Zedd in Redmoor, not you)", near.SequenceEqual(new[] { "member|Tessa" }) && ltx.Contains("Near you in Greenvale"), string.Join(",", near));
                // the main window's mapping of the Lodge's members: what they share, nothing more
                var tess = new MemberVM { Id = 1, Name = "tess" }; tess.SetGame(true, "Tessa", "Hunter", "HUNTER", 10, "Greenvale", null);
                var bob = new MemberVM { Id = 2, Name = "bob" }; bob.SetGame(true, "Bobby", "Rogue", "ROGUE", 12, "Greenvale", null); bob.Invisible = true;
                var kim = new MemberVM { Id = 3, Name = "kim" }; kim.SetGame(true, "Kimi", "Mage", "MAGE", 14, null, null);
                var ann = new MemberVM { Id = 4, Name = "ann" }; ann.SetGame(false, null, null, null, 0, null, null);
                var mapped = HomeMembersOf(new[] { tess, bob, kim, ann });
                var near2 = AtlasHome.NearYou(new HomeLodge { Online = 3, Members = mapped }, AtlasView.HomeHereForTest);
                Check("lodge today: the Lodge's members map with their sharing respected - level from the game line, invisible kept as invisible, no zone when it is not shared, no place when the game is not shared; near you is Tessa only",
                    mapped.Count == 4 && mapped[0].Level == 10 && mapped[0].Place == "Greenvale" && mapped[1].Invisible && mapped[2].Place == "" && !mapped[3].Playing && mapped[3].Place == "" && near2.Select(m => m.CharName).SequenceEqual(new[] { "Tessa" }),
                    string.Join(" | ", mapped.Select(m => m.CharName + ":" + m.Place + ":" + m.Level + ":" + m.Invisible)));

                // ---- the recent finds: this zone, or all
                var ftx = AtlasTexts(AtlasView.HomeFindsForTest);
                Check("finds: the Lodge's finds in Greenvale by default (Ruby Ring, Gnarlfang's Claw; not the one bought in Highkeep), with the toggle Greenvale | All", !AtlasView.HomeFindsAllForTest && ftx.Contains("Ruby Ring") && !ftx.Any(t => t.Contains("bought from")) && ftx.Contains("Greenvale") && ftx.Contains("All"), string.Join(" | ", ftx));
                AtlasView.HomeFindsToggleForTest(true); UpdateLayout();
                ftx = AtlasTexts(AtlasView.HomeFindsForTest);
                Check("finds: All shows the others too (bought from Innkeeper Hale in Highkeep)", AtlasView.HomeFindsAllForTest && ftx.Any(t => t.Contains("bought from Innkeeper Hale")), string.Join(" | ", ftx));
                AtlasView.HomeFindsToggleForTest(false); UpdateLayout();

                // ============================================================================================================ live updates without a new page
                Width = 1000; Height = 700; await Task.Delay(250); UpdateLayout();
                var page = ((Panel)AtlasView.BodyForTest).Children[0];
                int builds = AtlasView.HomeHeroBuildsForTest, chipUpdates = AtlasView.HomeChipUpdatesForTest;
                // a flag only: the chips change, the hero is not built again
                live = new HomeLive { Char = Live("Greenvale", flags: 128), Playing = true };
                AtlasView.LiveChanged(); UpdateLayout();
                htx = AtlasTexts(AtlasView.HomeHeroForTest);
                Check("live: leaving combat changes the chips at once, in place (no new hero, no new page)", !htx.Contains("In combat") && htx.Contains("Grouped (3)") && AtlasView.HomeChipUpdatesForTest == chipUpdates + 1 && AtlasView.HomeHeroBuildsForTest == builds && ReferenceEquals(page, ((Panel)AtlasView.BodyForTest).Children[0]), string.Join(" | ", htx));
                // a new zone: the hero, the cards, near you and the finds change in place
                live = new HomeLive { Char = Live("Redmoor", flags: 128, xp: 63), Playing = true };
                AtlasView.LiveChanged(); UpdateLayout();
                bool debounced = AtlasTexts(AtlasView.HomeHeroForTest).Contains("You are in Greenvale");
                await Until(() => AtlasView.HomeHereForTest.ZoneId == 12, 3000);
                await AtlasView.HomeCardsFillForTest; await Task.Delay(60); UpdateLayout();
                htx = AtlasTexts(AtlasView.HomeHeroForTest);
                Check("live: a new zone waits a moment (debounced), then the hero says 'You are in Redmoor' (the Atlas painting: Redmoor has none) - the same page, not drawn again",
                    debounced && htx.Contains("You are in Redmoor") && (((Border)AtlasView.HomeHeroForTest).Child as FrameworkElement)?.Tag as string == "home-hero|atlas|zone:12" && ReferenceEquals(page, ((Panel)AtlasView.BodyForTest).Children[0]) && AtlasView.HomeHeroBuildsForTest == builds + 1, string.Join(" | ", htx));
                Check("live: the cards follow the new zone (no Greenvale quests or rares in Redmoor) and the finds follow it too", !AtlasView.HomeCardKeysForTest.Contains("quests") && !AtlasView.HomeCardKeysForTest.Contains("rares") && AtlasTexts(AtlasView.HomeFindsForTest).Any(t => t.Contains("Nothing found in Redmoor")), string.Join(",", AtlasView.HomeCardKeysForTest));
                var nearNow = AtlasWalk(AtlasView.HomeLodgeForTest).OfType<Border>().Select(b => b.Tag as string).Where(t => t != null && t.StartsWith("member|")).ToList();
                Check("live: near you follows the zone (Zedd in Redmoor now)", nearNow.SequenceEqual(new[] { "member|Zedd" }), string.Join(",", nearNow));
                // back to Greenvale while the reader is further down: the hero and the cards above grow, what is read stays where it is on the screen
                var finds = AtlasView.HomeFindsForTest;
                Scroller_ScrollTo(finds);
                double off0 = AtlasView.ScrollerForTest.VerticalOffset;
                double y0 = finds.TranslatePoint(new Point(0, 0), AtlasView.ScrollerForTest).Y;
                live = new HomeLive { Char = Live("Greenvale", flags: 128, xp: 64), Playing = true };
                AtlasView.LiveChanged();
                await Until(() => AtlasView.HomeHereForTest.ZoneId == 11, 3000);
                await AtlasView.HomeCardsFillForTest; await Task.Delay(60); UpdateLayout();
                double y1 = AtlasView.HomeFindsForTest.TranslatePoint(new Point(0, 0), AtlasView.ScrollerForTest).Y;
                Check("live: what you read does not jump - the blocks above grew (Greenvale's note, quests and rares), the finds block stays where it was on the screen (" + Math.Round(y0) + " -> " + Math.Round(y1) + ", scrolled " + Math.Round(off0) + " -> " + Math.Round(AtlasView.ScrollerForTest.VerticalOffset) + ")",
                    off0 > 0 && AtlasView.ScrollerForTest.VerticalOffset > off0 + 20 && Math.Abs(y1 - y0) < 1.5 && ReferenceEquals(page, ((Panel)AtlasView.BodyForTest).Children[0]) && AtlasView.HomeCardKeysForTest.Contains("quests"));
                // a level-up: the fit and the cards change
                live = new HomeLive { Char = Live("Greenvale", level: 21, flags: 128), Playing = true };
                AtlasView.LiveChanged(); await Until(() => AtlasView.HomeHereForTest.Level == 21 && AtlasView.HomeHereForTest.ZoneId == 11, 3000);
                await AtlasView.HomeCardsFillForTest; await Task.Delay(60); UpdateLayout();
                htx = AtlasTexts(AtlasView.HomeHeroForTest);
                var fit21 = AtlasHome.FitText(AtlasHome.Fit(21, zlo, zhi));
                bool dungeons21 = AtlasBrowse.Run(AtlasHome.DungeonsId(21)).Total > 0;
                Check("live: a level-up (9 -> 21 in Greenvale) changes the fit ('" + fit21 + "') and the cards (dungeons for level 21: " + dungeons21 + ", for the group of 3)", htx.Contains(fit21) && fit21 != fitWant
                    && dungeons21 && AtlasView.HomeCardKeysForTest.Contains("dungeons") && CardTexts("dungeons").Any(t => t.StartsWith("Dungeons for your group (3)")) && CardTexts("dungeons").Contains("Crypt of Bones"), string.Join(" | ", htx) + " / " + string.Join(" | ", CardTexts("dungeons")));
                Width = 1300; Height = 900; await Task.Delay(200);

                // ============================================================================================================ the other heroes
                // in a dungeon
                live = new HomeLive { Char = Live(null, level: 22, flags: 16 + 64 + 128, group: 5, instance: "Crypt of Bones"), Strip = new StripCodec.Info { Name = "Elan", HasV2 = true, InstanceId = 99, Flags1 = 16 + 64 + 128, GroupSize = 5 }, Playing = true };
                await Home();
                htx = AtlasTexts(AtlasView.HomeHeroForTest);
                Check("instance: 'You are in Crypt of Bones' on the dungeons painting, the chips say grouped (5) and in the dungeon, a click opens the dungeon's page",
                    htx.Contains("You are in Crypt of Bones") && htx.Contains("Grouped (5)") && htx.Contains("In Crypt of Bones") && (((Border)AtlasView.HomeHeroForTest).Child as FrameworkElement)?.Tag as string == "home-hero|instance|instance:99"
                    && AtlasWalk(AtlasView.HomeHeroForTest).OfType<FrameworkElement>().Any(e => e.Tag as string == "home-hero-art|instance"), string.Join(" | ", htx));
                await ShotWait(500); Snapshot(Path.Combine(dir, "atlashome-instance.png"));
                ClickRow(AtlasWalk(AtlasView.HomeHeroForTest).OfType<ContentControl>().First(c => c.Tag as string == "home-hero-card")); await AtlasView.WaitForTest();
                Check("instance: ... the click opened instance:99", AtlasView.CurrentId == "instance:99", AtlasView.CurrentId);
                // not playing: the last saved character, "last seen"
                live = new HomeLive { Playing = false };
                await Home();
                here = AtlasView.HomeHereForTest; htx = AtlasTexts(AtlasView.HomeHeroForTest);
                Check("last seen: WoW is not running - Elan (saved last) 'Last seen in Greenvale', saved 3 h ago, the saved XP (60% when saved) and rested, no live chips",
                    here != null && !here.Live && here.Name == "Elan" && htx.Contains("Last seen in Greenvale") && htx.Contains("Last seen 3 h ago") && htx.Contains("60% XP when saved") && htx.Contains("Rested") && !htx.Contains("In combat") && !htx.Any(t => t.StartsWith("Grouped")), string.Join(" | ", htx));
                Check("last seen: the cards are Elan's in Greenvale (quests, rares)", AtlasView.HomeCardKeysForTest.Contains("quests") && AtlasView.HomeCardKeysForTest.Contains("rares"), string.Join(",", AtlasView.HomeCardKeysForTest));
                await ShotWait(500); Snapshot(Path.Combine(dir, "atlashome-lastseen.png"));
                // no character at all, no Lodge
                var savedChars = chars.ToList(); chars.Clear();
                AtlasView.HomeLodgeSource = () => null;
                await Home();
                htx = AtlasTexts(AtlasView.HomeHeroForTest);
                Check("no character: a calm Atlas banner and the hint to install Elan's Hub; no cards, no Lodge line", (((Border)AtlasView.HomeHeroForTest).Child as FrameworkElement)?.Tag as string == "home-hero|none" && htx.Contains("Atlas") && htx.Any(t => t.Contains("Install Elan's Hub"))
                    && AtlasView.HomeCardKeysForTest.Count == 0 && AtlasView.HomeLodgeForTest == null, string.Join(" | ", htx));
                await ShotWait(500); Snapshot(Path.Combine(dir, "atlashome-none.png"));
                chars.AddRange(savedChars);
                AtlasView.HomeLodgeSource = () => homeLodge;

                // ============================================================================================================ the window's wiring
                // the main window's own source (GamePresence) and its Changed event: SetForTest raises it as the strip would
                AtlasView.LiveSource = savedLive;
                await Home();
                Session.Presence.SetForTest(Live("Greenvale", level: 9, flags: 8, group: 0), true);
                bool wired = await Until(() => AtlasView.HomeHereForTest != null && AtlasView.HomeHereForTest.Live && AtlasView.HomeHereForTest.Name == "Elan" && AtlasView.HomeHereForTest.ZoneId == 11, 4000);
                Check("wiring: the main window feeds Home from the Hub status strip (GamePresence.Changed) - Elan in Greenvale, resting", wired && AtlasTexts(AtlasView.HomeHeroForTest).Contains("Resting"), AtlasView.HomeHereForTest?.Name + " " + AtlasView.HomeHereForTest?.ZoneName);
                await ShotWait(400); Snapshot(Path.Combine(dir, "atlashome-wired.png"));
                Session.Presence.SetForTest(null, false);
            }
            catch (Exception e) { Check("atlashome ran without an exception", false, e.ToString()); }
            finally
            {
                try
                {
                    Session?.Presence.SetForTest(null, false);
                    AtlasHome.UpgradesOverrideForTest = null; AtlasHome.RecipesOverrideForTest = null;
                    ElansAddonHub.AtlasPage.HomeDebounceMs = savedDebounce;
                    AtlasView.LiveSource = savedLive; AtlasView.HomeLodgeSource = savedHomeLodge;
                    if (artAdded) ArtLibrary.Zones.Remove(11);
                    ZoneLore.ResetForTest();
                    AtlasQuery.Context.Lodge = savedLodge; AtlasQuery.Context.NowUtc = savedNow; AtlasQuery.Context.Owned = savedOwned;
                    AtlasView.OwnedOverrideForTest = null; AtlasView.CharsOverrideForTest = null; AtlasView.LodgeState = savedLodgeState;
                    ClassicAtlas.Use(null); ItemData.ResetForTest(); SpellData.ResetForTest(); SeenData.ResetForTest(); MapData.ResetForTest(); AtlasBrowse.ForgetForTest();
                    MapRender.TileSourceForTest = savedTiles; MapRender.DirOverride = savedMapDir; MapRender.ForgetFailuresForTest();
                    Width = savedWidth; Height = h0;
                    ResetAtlasForTest();
                    ShowTab("addons");
                }
                catch { }
            }
            return string.Join("\r\n", notes) + "\r\natlashome: " + (ok ? "ok" : "FAILED");
        }

        static string TextOf(TextBlock t) => ElansAddonHub.AtlasPage.TextOf(t);
        void Scroller_ScrollTo(FrameworkElement el) { AtlasView.Scroller_ScrollToForTest(el); UpdateLayout(); }
    }
}
