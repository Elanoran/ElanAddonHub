using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Db2;

namespace ElansAddonHub
{
    // --selftest "atlasmenu" (2.77): the Atlas side menu's numbers (AtlasNavCounts: remembered in memory and on disk, shown at once when you come back
    // and on the next start, worked out again in the background when the data changes, put into the rows in place; Zones, World map and Professions
    // have one now), the one width of every side menu (Atlas, Settings, Characters, Lodge: Size.SideMenu) with the title bar search box ending where
    // they end, and the one rule of spells and skills (AtlasSkillModel: nothing listed in both, weapon skills / languages / riding under Skills,
    // old ids redirected).
    public partial class MainWindow
    {
        async Task<string> AtlasMenuTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            void Check(string what, bool cond, string detail = null) { SelfTestClock.Mark(what); notes.Add((cond ? "ok   " : "FAIL ") + what + (!cond && detail != null ? " [" + detail + "]" : "")); ok &= cond; }
            string P(string n) => Path.Combine(dir, n);
            var w0 = Width; var h0 = Height;
            var realSettings = settings;
            var countsFile = P("atlas-nav-counts.txt");
            var realCountsFile = AtlasNavCounts.FilePath;
            var savedLodge = AtlasQuery.Context.Lodge;
            string widthsNote = "";
            try
            {
                Width = 1300; Height = 900;
                // the invented world of the Atlas page's section (items, spells, seen data, the maps, the Classic baseline, a Lodge pool with finds)
                ItemData.SetForTest(AtlasTestItems(), ItemData.State.Ready);
                SpellData.SetForTest(AtlasTestSpells());
                SeenData.SetForTest(AtlasTestSeen());
                var mapSet = AtlasTestMapSet();
                mapSet.Maps.First(m => m.Id == 0).WdtFileDataId = 7001; mapSet.Maps.First(m => m.Id == 0).MapType = 1;     // the continent has its terrain file: the world map shows it
                Services.Maps.MapData.SetForTest(new Services.Maps.MapIndex(mapSet));
                Services.Atlas.Classic.ClassicAtlas.Use(AtlasTestClassic());
                var lodge = AtlasTestLodge();
                AtlasQuery.Context.Lodge = () => lodge;
                AtlasBrowse.ForgetForTest();
                // ============================================================================================================ the numbers
                if (File.Exists(countsFile)) File.Delete(countsFile);
                AtlasNavCounts.ResetForTest(countsFile);
                AtlasNavCounts.ComputeOverrideForTest = null;
                AtlasView.UseNavSettings(new Settings(), () => { });
                ShowTab("atlas");
                await AtlasView.GoStart(); await Settle();
                await AtlasNavCounts.RefreshAsync(true);
                await Until(() => !string.IsNullOrEmpty(AtlasView.NavCountForTest("items")), 6000);
                var snap = AtlasNavCounts.Snapshot();
                string Nums() => string.Join(" ", AtlasView.NavRowsForTest.Select(r => r)) + " :: " + string.Join(" ", snap.Select(k => k.Key + "=" + k.Value));
                bool Digits(string s) => !string.IsNullOrEmpty(s) && s.Replace(",", "").All(char.IsDigit);
                Check("numbers: Items, Zones, World map and Professions show a number (Zones, World map and Professions had none before 2.77)",
                    new[] { "items", "zones", "worldmap", "professions" }.All(k => Digits(AtlasView.NavCountForTest(k))), Nums() + " | zones='" + AtlasView.NavCountForTest("zones") + "' worldmap='" + AtlasView.NavCountForTest("worldmap") + "' professions='" + AtlasView.NavCountForTest("professions") + "'");
                widthsNote = "continents=" + ElansAddonHub.Services.WorldMap.WorldMapService.Continents(Services.Maps.MapData.Index).Count;
                Check("numbers: World map counts its continents, Professions its professions (the twelve by name, as many as the game data has)",
                    snap.TryGetValue("worldmap", out var wm) && wm == ElansAddonHub.Services.WorldMap.WorldMapService.Continents(Services.Maps.MapData.Index).Count
                    && snap.TryGetValue("professions", out var pf) && pf == AtlasHosts.ProfessionCount() && pf > 0, widthsNote + " " + Nums());
                Check("numbers: every list row has its list's number (the same as the list says), Talents / Home / Data sources none",
                    AtlasNav.Entries().Where(e => !e.Extra && e.Members.Count == 1 && !e.Members[0].Hosted).All(e => AtlasNavCounts.Get(e.Key) == AtlasBrowse.Count(e.Members[0]) || AtlasBrowse.Count(e.Members[0]) <= 0)
                    && string.IsNullOrEmpty(AtlasView.NavCountForTest("talents")) && AtlasView.NavCountForTest("home") == null, Nums());
                await ShotWait(300); Snapshot(P("atlasmenu-nav.png"));
                var lines = File.Exists(countsFile) ? File.ReadAllLines(countsFile) : new string[0];
                Check("numbers: written to the data folder's atlas-nav-counts.txt (key=number per row)", lines.Any(l => l.StartsWith("zones=")) && lines.Any(l => l.StartsWith("professions=")) && lines.Any(l => l.StartsWith("items=")), string.Join(";", lines));

                // a slow pass (the data is being worked out again): the numbers are there at once anyway, from memory and from the file
                var gate = new ManualResetEventSlim(false);
                AtlasNavCounts.ComputeOverrideForTest = e => { gate.Wait(8000); return AtlasNavCounts.Compute(e); };
                var zonesBefore = AtlasView.NavCountForTest("zones");
                ShowTab("addons"); await Settle();
                var slow = AtlasNavCounts.RefreshAsync(true);
                ShowTab("atlas"); await Settle();
                Check("numbers: leaving the Atlas and coming back shows the numbers at once (while a new pass is still working)", !slow.IsCompleted && AtlasView.NavCountForTest("zones") == zonesBefore && Digits(zonesBefore) && Digits(AtlasView.NavCountForTest("items")));
                AtlasNavCounts.ResetForTest();              // the next start: nothing in memory, the file stays
                AtlasView.UpdateNavForTest(); await Settle();
                Check("numbers: the next start shows the last numbers from the file before anything is worked out", Digits(AtlasView.NavCountForTest("zones")) && Digits(AtlasView.NavCountForTest("professions")) && AtlasView.NavCountForTest("zones") == zonesBefore,
                    "zones='" + AtlasView.NavCountForTest("zones") + "'");
                gate.Set(); await slow;

                // the data changes: one background pass, the number changes in its row in place (same row, same text block, nothing rebuilt)
                AtlasNavCounts.Enable();
                var zoneRow = AtlasView.NavRowForTest("zones"); var zoneNum = AtlasView.NavCountBlockForTest("zones");
                AtlasNavCounts.ComputeOverrideForTest = e => e.Key == "zones" ? 777 : AtlasNavCounts.Compute(e);
                int passes = AtlasNavCounts.PassesForTest;
                AtlasNavCounts.Invalidate();
                await Task.Delay(AtlasNavCounts.DebounceMs + 400); await Settle();
                Check("numbers: nothing is worked out again when the data did not change (one cheap token check)", AtlasNavCounts.PassesForTest == passes && AtlasView.NavCountForTest("zones") == zonesBefore, AtlasNavCounts.PassesForTest + " vs " + passes);
                Interlocked.Increment(ref ZoneLevels.Revision);       // the zone levels arrived: what the Zones list counts changed
                AtlasNavCounts.Invalidate();
                bool moved = await Until(() => AtlasView.NavCountForTest("zones") == "777", 6000);
                Check("numbers: a change of the data is counted again in the background and the row shows the new number in place (same row and text block)", moved && ReferenceEquals(zoneRow, AtlasView.NavRowForTest("zones")) && ReferenceEquals(zoneNum, AtlasView.NavCountBlockForTest("zones")),
                    "zones='" + AtlasView.NavCountForTest("zones") + "'");
                Check("numbers: ... and the file has it for the next start", File.ReadAllLines(countsFile).Contains("zones=777"));
                AtlasNavCounts.ComputeOverrideForTest = e => -1;      // the data is gone / not loaded yet
                await AtlasNavCounts.RefreshAsync(true); await Settle();
                Check("numbers: a pass without the data (counts unknown) keeps the remembered numbers", AtlasView.NavCountForTest("zones") == "777" && Digits(AtlasView.NavCountForTest("items")));
                AtlasNavCounts.ComputeOverrideForTest = null;
                await AtlasNavCounts.RefreshAsync(true);
                await Until(() => AtlasView.NavCountForTest("zones") == zonesBefore, 6000);
                Check("numbers: back to the real data, back to the real number", AtlasView.NavCountForTest("zones") == zonesBefore, AtlasView.NavCountForTest("zones"));

                // ============================================================================================================ spells and skills
                var prevSpells = SpellData.Data; var prevState = SpellData.Status;
                try
                {
                    SpellData.SetForTest(MenuSkillSet());
                    AtlasBrowse.ForgetForTest();
                    var sp = AtlasBrowse.Run("browse:spells"); var sk = AtlasBrowse.Run("browse:skills");
                    var spT = sp.Rows.Select(r => r.Title).ToList(); var skT = sk.Rows.Select(r => r.Title).ToList();
                    Check("spells/skills: Spells holds what you cast - Fireball (class), Blood Fury (racial), Cooked Fish (a profession's spell), Lightning Strike (no skill line) - grouped Class abilities / Racial abilities / Professions",
                        new[] { "Fireball", "Blood Fury", "Cooked Fish", "Lightning Strike" }.All(spT.Contains) && sp.Children.Select(c => c.Title).OrderBy(t => t).SequenceEqual(new[] { "Class abilities", "Professions", "Racial abilities" }),
                        string.Join(",", spT) + " | " + string.Join(",", sp.Children.Select(c => c.Title)));
                    Check("spells/skills: no weapon skill, armor skill, language, riding or lockpicking spell in Spells, no Weapon skills / Languages / Secondary skills group there",
                        !new[] { "Bows", "Shoot Bow", "Leather", "Language Common", "Riding", "Pick Lock" }.Any(spT.Contains) && !sp.Children.Any(c => c.Title == "Weapon skills" || c.Title == "Languages" || c.Title == "Secondary skills" || c.Title == "Armor skills"), string.Join(",", spT));
                    Check("spells/skills: Skills holds the lines - Weapon skills (Bows), Armor skills (Leather), Languages, Riding, Other skills (Lockpicking) - and no class line, racial line or profession",
                        sk.Children.Select(c => c.Title).OrderBy(t => t).SequenceEqual(new[] { "Armor skills", "Languages", "Other skills", "Riding", "Weapon skills" })
                        && new[] { "Bows", "Leather", "Language: Common", "Horse Riding", "Lockpicking" }.All(skT.Contains) && !new[] { "Fire", "Orc Racial", "Cooking", "Alchemy" }.Any(skT.Contains),
                        string.Join(",", skT) + " | " + string.Join(",", sk.Children.Select(c => c.Title)));
                    // nothing in both: a spell row is never part of a listed skill line, a skill row never a line whose spells are listed
                    var listedLines = new HashSet<int>(sk.Rows.Select(r => AtlasIds.TryParse(r.Link, out _, out var n) ? n : 0));
                    var both = sp.Rows.Where(r => AtlasIds.TryParse(r.Link, out _, out var n) && AtlasSkillModel.LineOfSpell(n) is SkillInfo l && listedLines.Contains(l.Id)).Select(r => r.Title).ToList();
                    Check("spells/skills: nothing is in both lists (no spell of a skill line Skills lists)", both.Count == 0, string.Join(",", both));
                    var ws = AtlasBrowse.Run("browse:skills/weapon-skills");
                    Check("spells/skills: weapon skills are reachable - browse:skills/weapon-skills lists Bows", !ws.NotFound && ws.Rows.Any(r => r.Title == "Bows") && ws.Rows.All(r => r.Link.StartsWith("skill:")), string.Join(",", ws.Rows.Select(r => r.Title)));
                    Check("spells/skills: old ids open where the group is now (browse:spells/weapon-skills -> skills, browse:skills/class-abilities -> spells, secondary skills -> Other skills, filters kept)",
                        AtlasNav.Upgrade("browse:spells/weapon-skills") == "browse:skills/weapon-skills" && AtlasNav.Upgrade("browse:spells/languages") == "browse:skills/languages"
                        && AtlasNav.Upgrade("browse:skills/class-abilities") == "browse:spells/class-abilities" && AtlasNav.Upgrade("browse:skills/secondary-skills") == "browse:skills/other-skills"
                        && AtlasNav.Upgrade("browse:spells/weapon-skills?name=bow").StartsWith("browse:skills/weapon-skills?") && AtlasNav.Upgrade("browse:spells/weapon-skills?name=bow").Contains("bow")
                        && AtlasNav.KeyFor("browse:spells/weapon-skills") == "skills" && AtlasNav.Upgrade("browse:spells/class-abilities") == "browse:spells/class-abilities",
                        AtlasNav.Upgrade("browse:spells/weapon-skills?name=bow"));
                    Check("spells/skills: spell:<id> and skill:<id> keep opening; a weapon skill's spell (Shoot Bow) belongs to Skills, a class line (skill:Fire) to Spells, Fireball to Spells",
                        AtlasNav.KeyFor("spell:9001") == "skills" && AtlasNav.KeyFor("skill:9237") == "spells" && AtlasNav.KeyFor("spell:9010") == "spells" && AtlasNav.KeyFor("skill:9045") == "skills"
                        && AtlasQuery.Build("spell:9001") != null && AtlasQuery.Build("skill:9045") != null,
                        AtlasNav.KeyFor("spell:9001") + " " + AtlasNav.KeyFor("skill:9237") + " " + AtlasNav.KeyFor("spell:9010"));
                    await AtlasView.Open("spell:9001"); await Settle();
                    Check("spells/skills: the page of Shoot Bow highlights Skills and its crumb says Atlas > Skills", AtlasView.NavSelectedForTest.SequenceEqual(new[] { "skills" }) && AtlasView.CrumbsForTest.StartsWith("Atlas > Skills"), AtlasView.CrumbsForTest + " / " + string.Join(",", AtlasView.NavSelectedForTest));
                    await AtlasView.Open("browse:spells"); await Settle();
                    Check("spells/skills: the Spells page says in one line what a spell is and where the skills are", AtlasWalk(AtlasView).OfType<TextBlock>().Any(t => (t.Text ?? "").StartsWith("Spells are what you cast") && t.Text.Contains("Skills")));
                    await ShotWait(300); Snapshot(P("atlasmenu-spells.png"));
                    await AtlasView.Open("browse:skills"); await Settle();
                    await ShotWait(300); Snapshot(P("atlasmenu-skills.png"));
                    Check("spells/skills: ... and the Skills page what a skill is", AtlasWalk(AtlasView).OfType<TextBlock>().Any(t => (t.Text ?? "").StartsWith("Skills are what you get better at")));
                }
                finally { SpellData.SetForTest(prevSpells, prevState); AtlasBrowse.ForgetForTest(); }

                // ============================================================================================================ one side menu width
                var widths = new List<string>();
                double menuW = (double)FindResource("Size.SideMenu");
                var edges = new Dictionary<string, Tuple<double, double>>();
                void Edge(string name, FrameworkElement el)
                {
                    if (el == null || !el.IsVisible || el.ActualWidth <= 0) { widths.Add(name + "=not shown"); return; }
                    var p = el.TranslatePoint(new Point(0, 0), this);
                    edges[name] = Tuple.Create(p.X, p.X + el.ActualWidth);
                    widths.Add(name + "=" + p.X.ToString("0.#") + ".." + (p.X + el.ActualWidth).ToString("0.#"));
                }
                ShowTab("atlas"); await AtlasView.GoStart(); await Settle();
                Edge("atlas", AtlasView.NavBackdropForTest);
                ShowTab("settings"); await Settle();
                Edge("settings", SettingsPage.NavBox);
                await ShotWait(300); Snapshot(P("atlasmenu-settings.png"));
                var root = Path.Combine(dir, "menuwidthwow");
                if (Directory.Exists(root)) Util.DeleteDir(root);
                var sv = Path.Combine(root, "_classic_beta_", "WTF", "Account", "TESTACC", "SavedVariables");
                Directory.CreateDirectory(sv); Directory.CreateDirectory(Path.Combine(root, "_classic_beta_", "Interface", "AddOns"));
                File.WriteAllText(Path.Combine(sv, "ElansBags.lua"), BagsLua(3), new UTF8Encoding(false));
                try
                {
                    InventoryView.Init(() => root);
                    ShowTab("inventory");
                    await Until(() => InventoryView.CharCountForTest == 2, 8000); await Settle();
                    Edge("characters", InventoryView.ListBackdrop);
                    await ShotWait(300); Snapshot(P("atlasmenu-characters.png"));
                }
                finally { InventoryView.Init(() => settings.WowRoot); }
                try
                {
                    Session.Disconnect();
                    TabLodge.IsChecked = true;
                    LodgePage.ShowChatForTest();
                    Session.SentForTest = o => { };
                    Session.FeedForTest(FunWelcome("owner", FunPeople(false), "", "\"reply\",\"react\",\"react2\",\"pin\",\"profile\",\"displayname\",\"lfg\",\"history2\",\"search\",\"calendar\",\"files\",\"badges\"", "2.17.0"));
                    await Until(() => LodgePage.CalendarRowVisibleForTest && Session.Selected != null, 6000); await Settle();
                    Edge("lodge", LodgePage.SidebarForTest);
                    await ShotWait(300); Snapshot(P("atlasmenu-lodge.png"));
                }
                finally { Session.Disconnect(); Session.SentForTest = null; }
                Edge("search", TitleSearch);
                var menus = new[] { "atlas", "settings", "characters", "lodge" };
                Check("side menus: Atlas, Settings, Characters and Lodge each show their menu (measured)", menus.All(edges.ContainsKey), string.Join(" ", widths));
                if (menus.All(edges.ContainsKey))
                {
                    var a = edges["atlas"];
                    Check("side menus: all four are the same width (Size.SideMenu, " + menuW + " px) and start and end at the same x", menus.All(m => Math.Abs(edges[m].Item2 - edges[m].Item1 - menuW) < 0.6 && Math.Abs(edges[m].Item1 - a.Item1) < 0.6 && Math.Abs(edges[m].Item2 - a.Item2) < 0.6), string.Join(" ", widths));
                    Check("side menus: the title bar search box ends where the menus end", edges.ContainsKey("search") && Math.Abs(edges["search"].Item2 - a.Item2) < 0.6, string.Join(" ", widths));
                }
                Width = 1000; await Settle(); UpdateLayout();
                Check("side menus: the search box still lines up at another window width", Math.Abs(TitleSearch.TranslatePoint(new Point(TitleSearch.ActualWidth, 0), this).X - (edges.ContainsKey("atlas") ? edges["atlas"].Item2 : -1)) < 0.6);
            }
            catch (Exception e) { Check("atlasmenu checks ran to the end", false, e.ToString()); }
            finally
            {
                try
                {
                    AtlasNavCounts.ComputeOverrideForTest = null; AtlasNavCounts.ResetForTest(realCountsFile);
                    AtlasQuery.Context.Lodge = savedLodge;
                    Services.Atlas.Classic.ClassicAtlas.Use(null); ItemData.ResetForTest(); SpellData.ResetForTest(); SeenData.ResetForTest(); Services.Maps.MapData.ResetForTest(); AtlasBrowse.ForgetForTest();
                    AtlasView.UseNavSettings(realSettings, () => SettingsStore.Save(realSettings)); Width = w0; Height = h0; ShowTab("addons");
                }
                catch (Exception) { }
            }
            return string.Join("\r\n", notes) + "\r\natlasmenu: " + (ok ? "PASSED" : "FAILED");
        }

        // spells and skill lines for the spells / skills rule: one of each kind
        static AtlasSet MenuSkillSet()
        {
            var skills = new List<SkillInfo>
            {
                new SkillInfo { Id = 9045, Name = "Bows", Category = SkillInfo.CatWeapon }, new SkillInfo { Id = 9414, Name = "Leather", Category = SkillInfo.CatArmor },
                new SkillInfo { Id = 9098, Name = "Language: Common", Category = SkillInfo.CatLanguage }, new SkillInfo { Id = 9148, Name = "Horse Riding", Category = SkillInfo.CatSecondary },
                new SkillInfo { Id = 9633, Name = "Lockpicking", Category = SkillInfo.CatSecondary }, new SkillInfo { Id = 9237, Name = "Fire", Category = SkillInfo.CatClass },
                new SkillInfo { Id = 9125, Name = "Orc Racial", Category = SkillInfo.CatSecondary }, new SkillInfo { Id = 9185, Name = "Cooking", Category = SkillInfo.CatSecondary },
                new SkillInfo { Id = 9171, Name = "Alchemy", Category = SkillInfo.CatProfession },
            };
            var spells = new List<SpellInfo>
            {
                new SpellInfo { Id = 9001, Name = "Shoot Bow" }, new SpellInfo { Id = 9002, Name = "Bows" }, new SpellInfo { Id = 9003, Name = "Leather" }, new SpellInfo { Id = 9004, Name = "Language Common" },
                new SpellInfo { Id = 9005, Name = "Riding" }, new SpellInfo { Id = 9006, Name = "Pick Lock" }, new SpellInfo { Id = 9010, Name = "Fireball" }, new SpellInfo { Id = 9011, Name = "Blood Fury" },
                new SpellInfo { Id = 9012, Name = "Cooked Fish" }, new SpellInfo { Id = 9013, Name = "Lightning Strike" },
            };
            var line = new Dictionary<int, int> { [9001] = 9045, [9002] = 9045, [9003] = 9414, [9004] = 9098, [9005] = 9148, [9006] = 9633, [9010] = 9237, [9011] = 9125, [9012] = 9185 };
            int n = 1;
            var abil = line.Select(kv => new SkillAbility { Id = n++, Skill = kv.Value, Spell = kv.Key, MinRank = 1 }).ToList();
            return new AtlasSet { Spells = new SpellSnapshot(spells, skills, abil, new Dictionary<int, int[]>()), Factions = SpellData.Factions, Details = new ItemDetails() };
        }
    }
}
