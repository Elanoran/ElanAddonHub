using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Atlas.Classic;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Maps;

namespace ElansAddonHub
{
    // --selftest "datahonesty" (2.91): where the Atlas's data comes from, said honestly. On the invented world of the Atlas page test with the
    // fixture Lodge (AtlasTestLodge) and the fixture Cairn (CairnDatasetJson): the explainer on the Data sources page and the Atlas Home card
    // (dismissed once, remembered, back only when the setting is cleared; "Data sources" opens the page); the one coverage line of a loot table,
    // a vendor, an item's sources and a map - "Not seen on Forever yet" / "Nobody has seen ..." ONLY where no Forever data exists (each source:
    // Lodge, Cairn, both, neither), the "based on" line ONLY where it does; the browse lists' coverage numbers; the README's section.
    public partial class MainWindow
    {
        async Task<string> DataHonestyTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            void Check(string what, bool cond, string detail = null) { SelfTestClock.Mark(what); notes.Add((cond ? "ok   " : "FAIL ") + what + (!cond && detail != null ? " [" + detail + "]" : "")); ok &= cond; }
            var savedLodge = AtlasQuery.Context.Lodge; var savedNow = AtlasQuery.Context.NowUtc; var savedCairn = CairnAtlas.Current;
            var savedDismissed = settings.AtlasSourcesCardDismissed;
            var savedTiles = MapRender.TileSourceForTest; var savedMapDir = MapRender.DirOverride;
            var work = Path.Combine(dir, "datahonesty");
            try
            {
                if (Directory.Exists(work)) Util.DeleteDir(work);
                Directory.CreateDirectory(work);
                MapRender.DirOverride = Path.Combine(work, "maps");
                MapRender.TileSourceForTest = fdid => Solid(256, 256, 70, 96, 60);
                ItemData.SetForTest(AtlasTestItems(), ItemData.State.Ready);
                SpellData.SetForTest(AtlasTestSpells());
                SeenData.SetForTest(AtlasTestSeen());
                MapData.SetForTest(new MapIndex(AtlasTestMapSet()));
                ClassicAtlas.Use(AtlasTestClassic());
                var lodge = AtlasTestLodge();
                var cairn = CairnSet.Parse(CairnDatasetJson(1, 200));
                AtlasQuery.Context.NowUtc = () => AtUtc(AtNow);
                void Use(bool withLodge, bool withCairn)
                {
                    AtlasQuery.Context.Lodge = () => withLodge ? lodge : null;
                    CairnAtlas.SetForTest(withCairn ? cairn : null);
                    AtlasBrowse.ForgetForTest();
                }
                AtlasSection Sec(string id, string key) => AtlasQuery.Build(id).Section(key);
                string Cov(string id, string key) => Sec(id, key)?.Coverage ?? "(no section)";

                // ============================================================================================================ the words, defined once
                Check("words: three kinds and how to help, plain and short (each one sentence or two, no blame words)",
                    DataHonesty.Explainer.Length == 3 && DataHonesty.Explainer.All(t => t.Length < 260) && DataHonesty.HelpText.Contains("share what you see with your Lodge")
                    && !DataHonesty.Explainer.Concat(new[] { DataHonesty.NotSeenYet, DataHonesty.NobodyDrop, DataHonesty.NobodyLoot, DataHonesty.HelpText }).Any(t => t.Contains("you must") || t.Contains("your fault") || t.Contains("!")));
                Check("words: the based-on line names who saw it, members and lodges counted",
                    DataHonesty.LootLine(340, 3, 200, 3, "kill", true) == "Seen on Forever: 340 looted kills by 3 members of your Lodge and 200 looted kills by 3 of the Cairn's Lodges."
                    && DataHonesty.LootLine(12, 1, 0, 0, "open", false) == "Seen on Forever: 12 looted opens by your Lodge."
                    && DataHonesty.LootLine(0, 0, 0, 0, "kill", true) == DataHonesty.NotSeenYet && DataHonesty.LootLine(0, 0, 0, 0, "kill", false) == DataHonesty.NobodyLoot,
                    DataHonesty.LootLine(340, 3, 200, 3, "kill", true));

                // ============================================================================================================ creature pages: each source
                Use(true, true);
                Check("creature (Lodge + Cairn): the loot table says what it is based on - the Lodge's kills and members and the Cairn's lodges",
                    Cov("npc:1", "loot") == "Seen on Forever: 340 looted kills by 3 members of your Lodge and 200 looted kills by 3 of the Cairn's Lodges.", Cov("npc:1", "loot"));
                Check("creature (Cairn only): based on the Cairn's lodges", Cov("npc:449", "loot") == "Seen on Forever: 500 looted kills by 2 of the Cairn's Lodges.", Cov("npc:449", "loot"));
                Check("creature (Classic only): \"Not seen on Forever yet - showing the Classic baseline, which may differ\", rows still there",
                    Cov("npc:4", "loot") == DataHonesty.NotSeenYet && Sec("npc:4", "loot").Rows.Count > 0, Cov("npc:4", "loot"));
                var inn = Sec("npc:2", "loot");
                Check("creature (nothing anywhere): the empty table says nobody has seen it on Forever (one line, not two)",
                    inn != null && !inn.Open && inn.Empty == DataHonesty.NobodyLoot, inn?.Empty);
                Check("creature: no \"not seen\" line where the Forever rows are (Lodge or Cairn)",
                    !new[] { Cov("npc:1", "loot"), Cov("npc:449", "loot"), Cov("npc:449", "skin") }.Any(c => c.Contains("Not seen") || c.Contains("Nobody")) && Cov("npc:449", "skin").StartsWith(DataHonesty.BasedOnLead), Cov("npc:449", "skin"));
                var sells = Sec("npc:2", "vendor");
                Check("vendor (Lodge): the prices say they come from a Lodge member's game and when",
                    sells != null && sells.Coverage.StartsWith("Seen on Forever: prices from a Lodge member's game, ") && sells.Coverage.Contains("ago"), sells?.Coverage);
                var npc1 = AtlasQuery.Build("npc:1"); var npc4 = AtlasQuery.Build("npc:4"); var npc3 = AtlasQuery.Build("npc:3");
                Check("map: places seen on Forever counted (npc 1: the Lodge's 2 spawn points); the Classic baseline's only = not seen yet (npc 3 in the open, npc 4 inside an instance)",
                    npc1.Map.Coverage == "Seen on Forever: 2 places by you and your Lodge." && npc3.Map.Coverage == DataHonesty.NotSeenYet && npc4.Map.Coverage == DataHonesty.NotSeenYet,
                    npc1.Map.Coverage + " | " + npc3.Map.Coverage + " | " + npc4.Map.Coverage);

                Use(true, false);
                Check("creature (Lodge only, no Cairn): only the Lodge's part", Cov("npc:1", "loot") == "Seen on Forever: 340 looted kills by 3 members of your Lodge.", Cov("npc:1", "loot"));
                Use(false, false);
                var noF = Sec("npc:1", "loot");
                Check("creature (neither Lodge nor Cairn): the same creature now says it is not seen on Forever yet, over the Classic rows",
                    noF != null && noF.Coverage == DataHonesty.NotSeenYet && noF.Rows.All(r => r.Label == AtlasLabel.Classic), noF?.Coverage);
                Use(true, false);
                Check("object (Lodge): based on the opens the Lodge saw", Cov("obj:50", "loot") == "Seen on Forever: 50 looted opens by 2 members of your Lodge.", Cov("obj:50", "loot"));
                Use(false, false);
                Check("object (Classic only): not seen yet", Cov("obj:50", "loot") == DataHonesty.NotSeenYet, Cov("obj:50", "loot"));

                // ============================================================================================================ item pages
                Use(true, true);
                Check("item (Lodge + Cairn): the sources say how many each saw (drops and vendors; quests and recipes are the game's own)",
                    Cov("item:100", "sources") == "Seen on Forever: 2 sources by your Lodge and 2 sources by the Cairn's Lodges.", Cov("item:100", "sources"));
                Check("item (Classic only): not seen yet", Cov("item:104", "sources") == DataHonesty.NotSeenYet, Cov("item:104", "sources"));
                Check("item (crafted only): no line - there is nothing that could drop", Cov("item:108", "sources") == "", Cov("item:108", "sources"));
                var coin = Sec("item:107", "sources");
                Check("item (nothing known): the empty table says nobody has seen this drop on Forever yet", coin != null && !coin.Open && coin.Empty.EndsWith(DataHonesty.NobodyDrop), coin?.Empty);
                Use(true, false);
                Check("item (Lodge only)", Cov("item:100", "sources") == "Seen on Forever: 2 sources by your Lodge.", Cov("item:100", "sources"));
                Use(false, true);
                Check("item (Cairn only)", Cov("item:100", "sources") == "Seen on Forever: 4 sources by the Cairn's Lodges.", Cov("item:100", "sources"));
                Use(false, false);
                Check("item (neither): the same item now says not seen yet", Cov("item:100", "sources") == DataHonesty.NotSeenYet, Cov("item:100", "sources"));

                // ============================================================================================================ browse lists
                Use(true, true);
                var cr = AtlasBrowse.Run("browse:creatures");
                Check("browse creatures: one of the listed creatures has Forever drop data (Gnarlfang; the Cairn's 449 is not in the list)",
                    cr.Total > 10 && cr.Coverage == "1 of these has drop data from Forever players so far.", cr.Total + ": " + cr.Coverage);
                var it = AtlasBrowse.Run("browse:items");
                Check("browse items: 4 have a drop or vendor seen on Forever (Gnarlfang's Claw, Wolf Pelt by fishing, Linen Cloth, Ruby Ring from the chest)",
                    it.Coverage == "4 of these have a drop or vendor seen on Forever so far.", it.Total + ": " + it.Coverage);
                var ob = AtlasBrowse.Run("browse:objects");
                Check("browse objects: the chest has loot seen on Forever", ob.Total >= 1 && ob.Coverage == (ob.Total == 1 ? "This one has loot or places seen on Forever." : "1 of these has loot or places seen on Forever so far."), ob.Total + ": " + ob.Coverage);
                var one = AtlasBrowse.Run("browse:creatures?name=gnarl");
                var wolf = AtlasBrowse.Run("browse:creatures?name=plains");
                Check("browse: the line follows the filters (one creature with data / one without)",
                    one.Total == 1 && one.Coverage == "This one has drop data from Forever players." && wolf.Total == 1 && wolf.Coverage == "This one has no drop data from Forever players yet.", one.Id + " " + one.Coverage + " | " + wolf.Id + " " + wolf.Coverage);
                Check("browse: no line where it means nothing (quests, zones, spells)", AtlasBrowse.Run("browse:quests").Coverage == "" && AtlasBrowse.Run("browse:spells").Coverage == "");
                Use(false, false);
                var cr0 = AtlasBrowse.Run("browse:creatures");
                Check("browse without Lodge and Cairn: none of these has Forever drop data yet", cr0.Coverage == "None of these has drop data from Forever players yet.", cr0.Coverage);
                var before = AtlasBrowse.Run("browse:items").Coverage;
                CairnAtlas.SetForTest(cairn);                  // no ForgetForTest: the index must notice the Cairn's data by itself
                var after = AtlasBrowse.Run("browse:items").Coverage;
                Check("browse: the Cairn alone counts too, and the list follows when the Cairn's data arrives (Gnarlfang's Claw and Linen Cloth)",
                    before.StartsWith("None of these") && after == "2 of these have a drop or vendor seen on Forever so far.", before + " -> " + after);

                // ============================================================================================================ the window
                Use(true, true);
                ShowTab("atlas");
                settings.AtlasSourcesCardDismissed = false;
                async Task Home() { AtlasView.History.Clear(); await AtlasView.GoStart(); await (AtlasView.StartFillForTest ?? Task.CompletedTask); await Task.Delay(80); }
                await Home();
                var homeTexts = AtlasView.ExplainerTextsForTest;
                Check("Home: the card \"Where this comes from\" with the three kinds and how to help",
                    AtlasView.SourcesCardShownForTest && homeTexts.Contains(DataHonesty.Title) && DataHonesty.Explainer.All(t => homeTexts.Any(x => x.Contains(t))) && homeTexts.Contains(DataHonesty.HelpText), string.Join(" | ", homeTexts));
                await ShotWait(300); if (SelfTestClock.ShotsOn) Snapshot(Path.Combine(dir, "datahonesty-home.png"));
                Press(AtlasView.SourcesCardMoreForTest);
                await AtlasView.WaitForTest(); await Task.Delay(120);
                Check("Home: \"Data sources\" opens the Data sources page", AtlasView.History.Current == AtlasNav.DataId, AtlasView.History.Current);
                var dataTexts = AtlasView.ExplainerTextsForTest;
                Check("Data sources: the explainer is at the top with the same words", dataTexts.Contains(DataHonesty.Title) && DataHonesty.Explainer.All(t => dataTexts.Any(x => x.Contains(t))) && dataTexts.Contains(DataHonesty.HelpText), string.Join(" | ", dataTexts));
                await ShotWait(300); if (SelfTestClock.ShotsOn) Snapshot(Path.Combine(dir, "datahonesty-data.png"));
                await Home();
                Press(AtlasView.SourcesCardDismissForTest);
                Check("Home: \"Got it\" hides the card and remembers it", !AtlasView.SourcesCardShownForTest && settings.AtlasSourcesCardDismissed);
                await Home();
                Check("Home: not again on the next visit", !AtlasView.SourcesCardShownForTest && AtlasView.ExplainerTextsForTest.Count == 0);
                SettingsStore.Save(settings);
                Check("Home: the answer is saved with the settings", SettingsStore.Load().AtlasSourcesCardDismissed);
                await AtlasView.Open(AtlasNav.DataId); await AtlasView.WaitForTest(); await Task.Delay(80);
                Check("Data sources: the explainer stays there after the card was dismissed", AtlasView.ExplainerTextsForTest.Contains(DataHonesty.Title));
                settings.AtlasSourcesCardDismissed = false;
                await Home();
                Check("Home: the card comes back only when the setting is cleared", AtlasView.SourcesCardShownForTest);

                await AtlasView.Open("npc:4?tab=related"); await AtlasView.WaitForTest(); await Task.Delay(120);
                var l4 = AtlasView.CoverageLinesForTest;
                Check("page in the window: a creature with only Classic loot shows the not-seen line over its table", l4.Contains(DataHonesty.NotSeenYet), string.Join(" | ", l4));
                await ShotWait(300); if (SelfTestClock.ShotsOn) Snapshot(Path.Combine(dir, "datahonesty-npc-classic.png"));
                await AtlasView.Open("npc:1?tab=related"); await AtlasView.WaitForTest(); await Task.Delay(120);
                var l1 = AtlasView.CoverageLinesForTest;
                Check("page in the window: a creature the Lodge and the Cairn saw shows what its loot is based on, and no not-seen line",
                    l1.Any(t => t.StartsWith("Seen on Forever: 340 looted kills")) && !l1.Contains(DataHonesty.NotSeenYet), string.Join(" | ", l1));
                await ShotWait(300); if (SelfTestClock.ShotsOn) Snapshot(Path.Combine(dir, "datahonesty-npc-forever.png"));
                await AtlasView.Open("npc:3?tab=map"); await AtlasView.WaitForTest(); await Task.Delay(150);
                var l3 = AtlasView.CoverageLinesForTest;
                Check("page in the window: the map of a creature with only the Classic baseline's spawn points says so under its title", l3.Contains(DataHonesty.NotSeenYet), string.Join(" | ", l3));
                await ShotWait(300); if (SelfTestClock.ShotsOn) Snapshot(Path.Combine(dir, "datahonesty-npc-map.png"));
                await AtlasView.Open("item:100?tab=sources"); await AtlasView.WaitForTest(); await Task.Delay(120);
                await ShotWait(300); if (SelfTestClock.ShotsOn) Snapshot(Path.Combine(dir, "datahonesty-item.png"));
                await AtlasView.Open("browse:creatures"); await AtlasView.WaitForTest(); await AtlasView.BrowseWaitForTest(); await Task.Delay(150);
                Check("browse in the window: the quiet line under the count", AtlasView.BrowseCoverageTextForTest == "1 of these has drop data from Forever players so far.", AtlasView.BrowseCoverageTextForTest);
                await ShotWait(300); if (SelfTestClock.ShotsOn) Snapshot(Path.Combine(dir, "datahonesty-browse.png"));

                // ============================================================================================================ README
                string readme = null;
                for (var up = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory); up != null && readme == null; up = up.Parent)
                    if (File.Exists(Path.Combine(up.FullName, "README.md")) && File.Exists(Path.Combine(up.FullName, "ARTWORK.md"))) readme = File.ReadAllText(Path.Combine(up.FullName, "README.md")).Replace("\r\n", "\n");
                int at = readme?.IndexOf("\n## Where the data comes from\n", StringComparison.Ordinal) ?? -1;
                var part = at >= 0 ? readme.Substring(at, Math.Max(0, (readme.IndexOf("\n## ", at + 5, StringComparison.Ordinal) is int e && e > at ? e : readme.Length) - at)) : "";
                Check("README: \"Where the data comes from\" after the tour, with the three kinds, honest that the Forever part grows and the baseline is a fallback (skipped when the source folder is not found)",
                    readme == null || (at > readme.IndexOf("## A short tour", StringComparison.Ordinal) && part.Contains("**" + DataHonesty.GameLead + ".**") && part.Contains("**" + DataHonesty.ForeverLead + ".**")
                        && part.Contains("**" + DataHonesty.ClassicLead + ".**") && part.Contains("still growing") && part.Contains("fallback") && part.Contains("may") && part.Contains("differ") && part.Contains(DataHonesty.NotSeenYet.Substring(0, 23))),
                    at < 0 ? "section not found" : part.Substring(0, Math.Min(200, part.Length)));
            }
            catch (Exception e) { Check("datahonesty: no exception", false, e.ToString()); }
            finally
            {
                try
                {
                    settings.AtlasSourcesCardDismissed = savedDismissed; SettingsStore.Save(settings);
                    AtlasQuery.Context.Lodge = savedLodge; AtlasQuery.Context.NowUtc = savedNow; CairnAtlas.SetForTest(savedCairn);
                    ClassicAtlas.Use(null); ItemData.ResetForTest(); SpellData.ResetForTest(); SeenData.ResetForTest(); MapData.ResetForTest(); AtlasBrowse.ForgetForTest();
                    MapRender.TileSourceForTest = savedTiles; MapRender.DirOverride = savedMapDir; MapRender.ForgetFailuresForTest();
                    ResetAtlasForTest();
                    ShowTab("addons");
                }
                catch { }
            }
            return string.Join("\r\n", notes) + "\r\ndatahonesty: " + (ok ? "ok" : "FAILED");
        }
    }
}
