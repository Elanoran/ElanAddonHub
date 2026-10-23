using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Maps;
using ElansAddonHub.Services.Talents;

namespace ElansAddonHub
{
    // --selftest "gamepatches": Settings > About > Game patches (3.3). The comparisons of two game builds' data (items, spells, talents, maps, zones,
    // flight points: new / changed with the facts that changed / removed, unchanged things not counted, the example cap with complete counts), the
    // server hotfixes of a build, the history file (newest first, kept across starts, the hotfixes only rewritten when they change, a damaged file
    // = a new list, a failing comparison never stops a reader) and the tab (the build picker, the build's head, Blizzard's notes, the found card with
    // its counts, examples, "Show more", links into the Atlas, "the same as" and "nothing to compare with"). The real game files: coldstart.
    public partial class MainWindow
    {
        async Task<string> GamePatchesTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            void Check(string what, bool cond, string detail = null) { SelfTestClock.Mark(what); notes.Add((cond ? "ok   " : "FAIL ") + what + (!cond && detail != null ? " [" + detail + "]" : "")); ok &= cond; }
            string P(string n) => Path.Combine(dir, n);
            var work = Path.Combine(dir, "gamepatches");
            var savedFile = GameBuilds.FilePath; var savedNow = GameBuilds.Now; var savedCurrent = SettingsPage.CurrentGameBuild; var savedName = GameBuilds.TalentName;
            var savedAtlas = SettingsPage.OpenAtlas; var savedW = Width; var savedH = Height; var savedGet = PatchNotes.Get; var savedLoad = settings.PatchNotesLoad;
            const string A = "wow_classic_beta|1.60.1.70450|84bea4233331e390fda8724c6f91ffa5", B = "wow_classic_beta|1.60.1.70532|ad1802f4455e427ed8b7769aab25b0c0", C = "wow_classic_beta|1.60.1.70600|0123456789abcdef0123456789abcdef";
            try
            {
                if (Directory.Exists(work)) Util.DeleteDir(work);
                Directory.CreateDirectory(work);
                GameBuilds.FilePath = Path.Combine(work, "gamebuilds.json"); GameBuilds.ResetForTest();
                GameBuilds.Now = () => new DateTime(2026, 10, 23);
                PatchNotes.Get = (u, c) => throw new InvalidOperationException("no network in the self-test: " + u);     // Blizzard's notes come from a fixture (GamePatchesNotesTest)
                settings.PatchNotesLoad = false;
                Check("fixture: the history file is under the scratch folder (never the real data folder)", Path.GetFullPath(GameBuilds.FilePath).StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase));

                // ============================================================================================ the comparisons
                ClientItem Item(int id, string name, int ilvl, params int[] stats) => new ClientItem { Id = id, Name = name, ItemLevel = ilvl, Quality = 2, Stats = stats };
                var i1 = new ItemSet(); i1.Client.AddRange(new[] { Item(1, "Test Sword", 20, 3, 3), Item(2, "Test Shield", 15), Item(3, "Old Cap", 5) });
                var i2 = new ItemSet(); i2.Client.AddRange(new[] { Item(1, "Test Sword", 22, 3, 4), Item(2, "Test Shield", 15), Item(4, "New Axe", 25) });
                i2.Cache.Add(Item(9, "Revealed Ring", 30));
                var di = GameBuilds.DiffItems(i1, i2);
                Check("items: 1 new, 1 changed, 1 removed; the unchanged shield is not counted; the items this client revealed are left out",
                    di.Added == 1 && di.Modified == 1 && di.Removed == 1 && di.Rows.Count == 3 && di.Rows.All(r => r.Id != 2 && r.Id != 9), di.Added + "/" + di.Modified + "/" + di.Removed);
                Check("items: new first, then changed, then removed; new and changed link to the Atlas, removed does not",
                    di.Rows.Select(r => r.Op).SequenceEqual(new[] { GameBuilds.Added, GameBuilds.Modified, GameBuilds.Removed }) && di.Rows[0].Link == "item:4" && di.Rows[1].Link == "item:1" && di.Rows[2].Link == null);
                Check("items: the changed facts are named with their values ('item level 20 → 22, stats ...')", di.Rows[1].Detail.StartsWith("item level 20 → 22") && di.Rows[1].Detail.Contains("stats 3,3 → 3,4"), di.Rows[1].Detail);
                var ren = new ItemSet(); ren.Client.Add(Item(2, "Shiny Shield", 15));
                var dr = GameBuilds.DiffItems(i1, ren);
                Check("items: a rename says what it was called", dr.Modified == 1 && dr.Rows.First(r => r.Op == GameBuilds.Modified).Detail == "renamed from Test Shield", dr.Rows.FirstOrDefault()?.Detail);

                var big1 = new ItemSet(); var big2 = new ItemSet();
                big2.Client.AddRange(Enumerable.Range(1000, GameBuilds.RowCap + 100).Select(i => Item(i, "Thing " + i, 10)));
                var db = GameBuilds.DiffItems(big1, big2);
                Check("items: the counts are complete (" + (GameBuilds.RowCap + 100) + " new) while the examples stop at " + GameBuilds.RowCap, db.Added == GameBuilds.RowCap + 100 && db.Rows.Count == GameBuilds.RowCap);

                SpellInfo Spell(int id, string name, string desc, int cast, float points) => new SpellInfo { Id = id, Name = name, Desc = desc, CastMs = cast, Effects = new[] { new EffectInfo { Index = 0, Effect = 2, Points = points } } };
                AtlasSet Spells(params SpellInfo[] s) => new AtlasSet { Spells = new SpellSnapshot(s, new SkillInfo[0], new SkillAbility[0], new Dictionary<int, int[]>()) };
                var ds = GameBuilds.DiffSpells(Spells(Spell(10, "Fireball", "Hurls fire.", 3500, 12), Spell(11, "Frostbolt", "Cold.", 3000, 10)),
                                               Spells(Spell(10, "Fireball", "Hurls fire.", 3000, 14), Spell(11, "Frostbolt", "Cold.", 3000, 10), Spell(12, "Arcane Shot", "Pew.", 0, 5)));
                Check("spells: a new spell, a changed one (cast time and the effect amount); the unchanged Frostbolt is not counted",
                    ds != null && ds.Added == 1 && ds.Modified == 1 && ds.Removed == 0 && ds.Rows.Any(r => r.Id == 10 && r.Detail.Contains("cast time 3500 → 3000") && r.Detail.Contains("effect 1")) && ds.Rows.All(r => r.Id != 11),
                    ds == null ? "null" : string.Join(" | ", ds.Rows.Select(r => r.Id + " " + r.Detail)));
                Check("spells: without spell tables on either side there is nothing to compare (null, not 'everything removed')", GameBuilds.DiffSpells(new AtlasSet(), Spells(Spell(1, "x", "", 0, 0))) == null);

                GameBuilds.TalentName = n => n.SpellId == 500 ? "Improved Aspect" : n.SpellId == 501 ? "Lightning Reflexes" : null;
                TalentSet Trees(int ranks, bool extra)
                {
                    var t = new TalentTreeDef { Id = 1, ClassId = 3, ClassName = "Hunter", SpecNames = new[] { "Beast Mastery", "Marksmanship", "Survival" } };
                    t.Nodes.Add(new TalentNode { Id = 100, Spec = 0, Row = 0, Col = 1, Entries = { new TalentEntry { SpellId = 500, MaxRanks = ranks } } });
                    if (extra) t.Nodes.Add(new TalentNode { Id = 101, Spec = 2, Row = 3, Col = 2, Entries = { new TalentEntry { SpellId = 501, MaxRanks = 5 } } });
                    var set = new TalentSet(); set.Trees.Add(t); return set;
                }
                var dt = GameBuilds.DiffTalents(Trees(5, false), Trees(3, true));
                Check("talents: a new talent and one with fewer ranks, named '<class>: <talent>' and linked to its spell",
                    dt.Added == 1 && dt.Modified == 1 && dt.Rows.Any(r => r.Name == "Hunter: Lightning Reflexes" && r.Op == GameBuilds.Added && r.Link == "spell:501")
                    && dt.Rows.Any(r => r.Name == "Hunter: Improved Aspect" && r.Detail == "ranks 5 → 3"), string.Join(" | ", dt.Rows.Select(r => r.Name + ": " + r.Detail)));
                Check("talents: the same trees = nothing", GameBuilds.DiffTalents(Trees(5, true), Trees(5, true)).Total == 0);

                MapSet Maps(bool dalaran)
                {
                    var m = new MapSet { HasMaps = true, HasAreas = true, HasTaxi = true };
                    m.Maps.Add(new GameMapInfo { Id = 36, Name = "The Deadmines", InstanceType = 1, MaxPlayers = 5 });
                    if (dalaran) m.Maps.Add(new GameMapInfo { Id = 3000, Name = "City of Dalaran", InstanceType = 1, MaxPlayers = 5 });
                    m.Areas.Add(new AreaInfo { Id = 40, Name = dalaran ? "Westfall Hills" : "Westfall" });
                    m.TaxiNodes.Add(new TaxiNodeInfo { Id = 2, Name = "Sentinel Hill", MapId = 0, X = 100, Y = 200 });
                    if (!dalaran) m.TaxiNodes.Add(new TaxiNodeInfo { Id = 3, Name = "Old Perch" });
                    return m;
                }
                var dm = GameBuilds.DiffMaps(Maps(false), Maps(true)); var da = GameBuilds.DiffAreas(Maps(false), Maps(true)); var df = GameBuilds.DiffFlights(Maps(false), Maps(true));
                Check("maps: a new dungeon (linked as an instance), a renamed area, a flight point gone",
                    dm.Added == 1 && dm.Rows[0].Name == "City of Dalaran" && dm.Rows[0].Link == "instance:3000" && da.Modified == 1 && da.Rows[0].Detail == "renamed from Westfall" && df.Removed == 1 && df.Rows[0].Name == "Old Perch");
                Check("maps: a side that did not read the table compares nothing (null)", GameBuilds.DiffMaps(new MapSet(), Maps(true)) == null && GameBuilds.DiffFlights(Maps(false), new MapSet { HasMaps = true }) == null);

                var hs = Spells(Spell(10, "Fireball", "", 0, 0), Spell(12, "Arcane Shot", "", 0, 0));
                hs.Hotfixes = new HotfixInfo();
                hs.Hotfixes.Spells[10] = HotfixParts.Numbers | HotfixParts.Effects; hs.Hotfixes.Spells[12] = HotfixParts.New; hs.Hotfixes.Spells[13] = HotfixParts.Removed;
                var dh = GameBuilds.HotfixChanges(hs);
                Check("hotfixes: the spells the server's rows change, new / changed (which parts) / removed",
                    dh != null && dh.Added == 1 && dh.Modified == 1 && dh.Removed == 1 && dh.Rows.First(r => r.Id == 10).Detail == "changes the cast time, duration or range, effects" && dh.Rows.First(r => r.Id == 13).Link == null,
                    dh == null ? "null" : string.Join(" | ", dh.Rows.Select(r => r.Id + " " + r.Op + " " + r.Detail)));
                Check("hotfixes: none read = nothing to note", GameBuilds.HotfixChanges(Spells(Spell(1, "x", "", 0, 0))) == null);

                // ============================================================================================ the history
                GameBuilds.Seen(A);
                GameBuilds.Seen(A);
                GameBuilds.Compare("items", A, B, i1, i2, GameBuilds.DiffItems);
                GameBuilds.Compare("talents", A, B, Trees(5, false), Trees(3, true), GameBuilds.DiffTalents);
                GameBuilds.Compare<ItemSet>("spells", A, B, i1, i2, (x, y) => throw new InvalidOperationException("boom"));
                GameBuilds.Compare("items", B, B, i1, i2, GameBuilds.DiffItems);
                GameBuilds.NoteHotfixes(hs, B);
                var all = GameBuilds.All();
                Check("history: two builds (seen twice = once), newest first, the new one compared with the old",
                    all.Count == 2 && all[0].Build == B && all[1].Build == A && all[0].From == A && all[1].From == null && all[0].Seen == "2026-10-23", string.Join(", ", all.Select(e => e.Version + "<-" + e.From)));
                Check("history: a comparison that throws is logged and left out (the reader goes on); comparing a build with itself does nothing",
                    all[0].Of("spells") == null && all[0].Of("items").Total == 3 && all[0].Of("talents").Total == 2 && all[0].Of("hotfixes").Total == 3);
                var stamp = File.GetLastWriteTimeUtc(GameBuilds.FilePath); var size = new FileInfo(GameBuilds.FilePath).Length;
                await Task.Delay(30);
                GameBuilds.NoteHotfixes(hs, B);
                Check("history: the same hotfixes again are not written again (the spell reader notes them on every load)", File.GetLastWriteTimeUtc(GameBuilds.FilePath) == stamp && new FileInfo(GameBuilds.FilePath).Length == size);
                GameBuilds.ResetForTest();
                var again = GameBuilds.All();
                var row = again[0].Of("items").Rows[1];
                Check("history: kept across starts (read back from the file: builds, counts, rows with their detail and link)",
                    again.Count == 2 && again[0].Of("items").Modified == 1 && row.Detail == di.Rows[1].Detail && row.Link == "item:1" && again[0].Of("hotfixes").Rows.Count == 3);
                Check("history: version and build number from the build string", again[0].Version == "1.60.1.70532" && again[0].Number == 70532 && GameBuilds.NumberOf("garbage") == 0);
                var text = File.ReadAllText(GameBuilds.FilePath);
                Check("history: the file holds game data only (no path, account or user name)", !text.Contains(":\\\\") && !text.Contains(Environment.UserName) && !text.Contains("WTF"));
                File.WriteAllText(GameBuilds.FilePath, "{ \"builds\": [ { \"build\": ");
                GameBuilds.ResetForTest();
                Check("history: a damaged file = a new empty list, no exception", GameBuilds.All().Count == 0);

                // ============================================================================================ the tab
                File.Delete(GameBuilds.FilePath); GameBuilds.ResetForTest();
                GameBuilds.Seen(A);
                foreach (var k in new[] { "talents", "spells", "items", "maps", "areas", "flights" }) GameBuilds.Record(A, B, new GameBuilds.KindChanges { Kind = k });
                GameBuilds.NoteHotfixes(hs, B);
                GameBuilds.Compare("items", B, C, big1, big2, GameBuilds.DiffItems);
                GameBuilds.Compare("talents", B, C, Trees(5, false), Trees(3, true), GameBuilds.DiffTalents);
                GameBuilds.Compare("maps", B, C, Maps(false), Maps(true), GameBuilds.DiffMaps);
                SettingsPage.CurrentGameBuild = () => C;
                var opened = new List<string>();
                SettingsPage.OpenAtlas = id => opened.Add(id);
                Width = 1400; Height = 1000;
                ShowTab("settings"); SettingsPage.Show("about"); SettingsPage.ShowAboutTab("patches");
                UpdateLayout();
                Check("tab: Game patches opens (About and What's new hidden)", await Until(() => SettingsPage.AboutPatchesVisibleForTest && !SettingsPage.AboutNewsVisibleForTest && !SettingsPage.AboutPaneForTest.IsVisible));
                var pick = SettingsPage.PatchPickForTest.Children.OfType<RadioButton>().ToList();
                Check("tab: the picker lists the three builds newest first, the newest picked", pick.Count == 3 && pick.Select(r => r.Content as string).SequenceEqual(new[] { "Build 70600", "Build 70532", "Build 70450" }) && pick[0].IsChecked == true,
                    string.Join(", ", pick.Select(r => r.Content)));
                var bodyPanel = SettingsPage.PatchBodyForTest;
                string AllText(DependencyObject o) => string.Join(" ", Walk(o).OfType<TextBlock>().Select(t => new TextRange(t.ContentStart, t.ContentEnd).Text));
                var cards = bodyPanel.Children.OfType<ContentControl>().ToList();
                Check("tab: the build's head ('1.60.1 build 70600', 'Your game', first seen, compared with build 70532), Blizzard's notes, what was found",
                    cards.Count == 3 && AllText(cards[0]).Contains("1.60.1 build 70600") && AllText(cards[0]).Contains("Your game") && AllText(cards[0]).Contains("compared with 1.60.1 build 70532")
                    && (string)cards[1].Tag == "notes" && (string)cards[2].Tag == "found", cards.Count + ": " + AllText(cards[0]));
                var found = cards[2];
                var blocks = Walk(found).OfType<StackPanel>().Where(s => s.Tag is string).ToList();
                Check("found: one block per compared kind (talents, items, maps) in that order", blocks.Select(b => (string)b.Tag).SequenceEqual(new[] { "talents", "items", "maps" }), string.Join(",", blocks.Select(b => b.Tag)));
                var itemsBlock = blocks.First(b => (string)b.Tag == "items");
                Check("found: the counts as pills ('" + (GameBuilds.RowCap + 100) + " new'), " + 8 + " examples first and 'Show " + (GameBuilds.RowCap - 8) + " more'",
                    AllText(itemsBlock).Contains((GameBuilds.RowCap + 100).ToString("N0") + " new") && itemsBlock.Children.OfType<Grid>().Count() == 8
                    && itemsBlock.Children.OfType<Button>().Single().Content as string == "Show " + (GameBuilds.RowCap - 8).ToString("N0") + " more");
                itemsBlock.Children.OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                UpdateLayout();
                itemsBlock = Walk(SettingsPage.PatchBodyForTest).OfType<StackPanel>().First(s => (string)s.Tag == "items");
                Check("found: Show more lists all kept examples and says the counts are complete", itemsBlock.Children.OfType<Grid>().Count() == GameBuilds.RowCap && AllText(itemsBlock).Contains("the counts above are complete"));
                var link = Walk(Walk(SettingsPage.PatchBodyForTest).OfType<StackPanel>().First(s => (string)s.Tag == "maps")).OfType<TextBlock>().SelectMany(t => t.Inlines.OfType<Hyperlink>()).FirstOrDefault();
                link?.DoClick();
                Check("found: an example opens its Atlas page (the new dungeon -> instance:3000)", link != null && opened.SequenceEqual(new[] { "instance:3000" }), string.Join(",", opened));
                if (SelfTestClock.ShotsOn) { await ShotWait(300); Snapshot(P("gamepatches-changes.png")); }

                SettingsPage.PickPatchForTest(B); UpdateLayout();
                var foundB = SettingsPage.PatchBodyForTest.Children.OfType<ContentControl>().Last();
                Check("tab: a build whose game files did not change says so in one line ('the same as in build 70450'), its server hotfixes listed under it",
                    AllText(foundB).Contains("The game files are the same as in build 70450") && Walk(foundB).OfType<StackPanel>().Any(s => (string)s.Tag == "hotfixes") && AllText(foundB).Contains("1 new") && !AllText(foundB).Contains("Your game"),
                    AllText(foundB));
                if (SelfTestClock.ShotsOn) { await ShotWait(300); Snapshot(P("gamepatches-same.png")); }
                SettingsPage.PickPatchForTest(A); UpdateLayout();
                Check("tab: the oldest build has nothing to compare with and says what will show", AllText(SettingsPage.PatchBodyForTest.Children.OfType<ContentControl>().Last()).Contains("Nothing to compare with yet"));

                File.Delete(GameBuilds.FilePath); GameBuilds.ResetForTest();
                SettingsPage.CurrentGameBuild = () => null;
                SettingsPage.PickPatchForTest(null); UpdateLayout();
                Check("tab: without a WoW folder (no build) it says how to start, no picker", SettingsPage.PatchPickForTest.Children.Count == 0 && AllText(SettingsPage.PatchBodyForTest).Contains("Set your WoW folder"));
                // ============================================================================================ Blizzard's notes (a fixture of the forum)
                await GamePatchesNotesTest(work, (w, c, d) => Check(w, c, d));

                SettingsPage.ShowAboutTab("about");
                Check("tab: back to About hides Game patches", await Until(() => SettingsPage.AboutPaneForTest.IsVisible && !SettingsPage.AboutPatchesVisibleForTest));
            }
            catch (Exception e) { Check("no exception", false, e.ToString()); }
            finally
            {
                GameBuilds.FilePath = savedFile; GameBuilds.Now = savedNow; GameBuilds.TalentName = savedName; GameBuilds.ResetForTest();
                PatchNotes.Get = savedGet; settings.PatchNotesLoad = savedLoad;
                SettingsPage.CurrentGameBuild = savedCurrent; SettingsPage.OpenAtlas = savedAtlas; SettingsPage.PickPatchForTest(null);
                Width = savedW; Height = savedH;
            }
            return (ok ? "PASS" : "FAIL") + " gamepatches\r\n" + string.Join("\r\n", notes);
        }

        static IEnumerable<DependencyObject> Walk(DependencyObject root)
        {
            if (root == null) yield break;
            yield return root;
            foreach (var c in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
                foreach (var d in Walk(c)) yield return d;
        }
    }

    static class HyperlinkTestExt
    {
        public static void DoClick(this Hyperlink h) => h.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
    }
}
