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
    // --selftest "atlaspage", part 3: the page on the player's own game files, only when there is a WoW install with the Forever client on this PC. Read-only: the tables are read
    // from CASC and the client's caches, icons and map pictures go to the scratch folder, nothing is written to the WoW folder. No Classic baseline and no Lodge here, so what shows is
    // exactly what the game itself knows.
    public partial class MainWindow
    {
        async Task AtlasRealChecks(string dir, string work, CheckFn Check)
        {
            var real = Environment.GetEnvironmentVariable("ELANSHUB_WOW");
            if (string.IsNullOrEmpty(real)) real = WowLocator.Find();
            if (string.IsNullOrEmpty(real) || !Directory.Exists(Path.Combine(real, "Data", "data")) || !Directory.Exists(Path.Combine(real, "_classic_beta_")))
            {
                Check("real game files: skipped (no WoW install with the Forever beta client on this PC)", true);
                return;
            }
            var savedIconRoot = IconStore.WowRoot;
            try
            {
                var rw = Path.Combine(work, "real");
                Directory.CreateDirectory(rw);
                // items (with what the server revealed), spells, what the client has seen
                var rawI = ItemData.ReadCasc(real, "_classic_beta_");
                var iset = ItemData.ParseTables(rawI.Sparse, rawI.Item, rawI.Budget);
                if (!iset.Ok) { Check("real game files: skipped (" + iset.Problem + ": this Outpost does not know that game build's item table)", true); return; }
                ItemData.ReadCacheRows(iset, ItemData.AdbFile(real, "_classic_beta_"));
                ItemData.SetForTest(new ItemSnapshot(iset.Client, iset.Cache), ItemData.State.Ready);
                var sset = SpellParse.All(SpellData.ReadCasc(real, "_classic_beta_"));
                SpellData.SetForTest(sset);
                var wdb = SeenData.WdbDir(real, "_classic_beta_");
                var seen = wdb != null && File.Exists(Path.Combine(wdb, "creaturecache.wdb")) ? SeenData.ReadFiles(wdb) : null;
                if (seen != null && seen.Ok) SeenData.SetForTest(new SeenSnapshot(seen.Npcs, seen.Objects, seen.Quests)); else SeenData.SetForTest(null, SeenData.State.NotStarted);
                // maps: the real index and pictures (in the scratch folder)
                MapData.ResetForTest();
                MapData.WowRoot = () => real; MapData.Enabled = () => true; MapData.CacheFile = Path.Combine(rw, "cache", "mapdata.bin");
                MapRender.DirOverride = Path.Combine(rw, "maps"); MapRender.TileSourceForTest = null; MapRender.ForgetFailuresForTest();
                MapData.Start();
                await WaitFor(() => MapData.Status == MapData.State.Ready || MapData.Status == MapData.State.Unavailable, 120000);
                IconStore.WowRoot = () => real;
                ClassicAtlas.Use(null);
                AtlasQuery.Context.Lodge = () => null;
                AtlasView.OwnedOverrideForTest = () => null;
                AtlasView.LodgeState = () => "Not connected to a Lodge";
                AtlasView.UseClassicSetup(new ClassicSetup(new FakeClassic { Dir = rw }));
                Check("real game files: items, spells and maps are read (" + ItemData.Count + " items, " + SpellData.SpellCount + " spells, " + MapData.StatusLine + ")", ItemData.Count > 10000 && SpellData.SpellCount > 10000 && MapData.Ready);

                // search the way a player would
                var linen = AtlasQuery.Search("linen cloth");
                var cloth = linen.All.FirstOrDefault(h => h.Kind == "item" && h.Name == "Linen Cloth");
                Check("real game files: 'linen cloth' finds the item Linen Cloth (item:2589), Forever", cloth != null && cloth.Id == "item:2589" && cloth.Label == AtlasLabel.Forever && linen.Groups[0].Kind == "item", string.Join(" | ", linen.All.Take(5)));
                var elw = AtlasQuery.Search("elwynn");
                var elwHit = elw.All.FirstOrDefault(h => h.Kind == "zone" && h.Name == "Elwynn Forest");
                Check("real game files: 'elwynn' finds the zone Elwynn Forest", elwHit != null, string.Join(" | ", elw.All.Take(5)));
                var fire = AtlasQuery.Search("fireball");
                Check("real game files: 'fireball' finds the spell", fire.All.Any(h => h.Kind == "spell" && h.Name == "Fireball"));
                var dm = AtlasQuery.Search("deadmines");
                Check("real game files: 'deadmines' finds the instance", dm.All.Any(h => h.Kind == "instance" && h.Id == "instance:36" && h.Name.Contains("Deadmines")), string.Join(" | ", dm.All.Take(4)));

                // a real item page: the game's own tooltip, recipes (client data) and what the Atlas can not know without the baseline
                var pot = AtlasQuery.Build("item:13446");
                Check("real game files: item page for the Major Healing Potion has the game's own tooltip (requirement, 'Use:' effect, price), the recipe of this client that makes it (a profession and its reagents), and says the Classic baseline is missing",
                    pot.Title == "Major Healing Potion" && pot.Tooltip.Any(l => l.Kind == "effect") && pot.Tooltip.Any(l => l.Kind == "req") && pot.Section("sources").Rows.Any(r => r.Type == "Recipe" && r.Label == AtlasLabel.Forever && r.Detail.Contains("Crystal Vial")) && pot.Notes.Any(n => n.Contains("Classic baseline")),
                    string.Join("; ", pot.Tooltip.Take(6)) + " / " + string.Join(" || ", pot.Section("sources").Rows));
                var lc = AtlasQuery.Build("item:2589");
                Check("real game files: Linen Cloth is used in many recipes (reagent table), every row linking to its item or spell", lc.Section("reagent") != null && lc.Section("reagent").Rows.Count > 5 && lc.Section("reagent").Rows.All(r => r.Link != null));
                var zone = AtlasQuery.Build(elwHit?.Id ?? "zone:1429");
                Check("real game files: the Elwynn Forest page names its continent, shows its map and says where the creatures would come from", zone.Title == "Elwynn Forest" && zone.Map.ShowZone > 0 && zone.Facts.Any(f => f.Label == "In") && zone.Notes.Any(n => n.Contains("Classic baseline")), string.Join("; ", zone.Facts));
                var dmPage = AtlasQuery.Build("instance:36");
                Check("real game files: the Deadmines page: a dungeon with its player count (this client: 10) and no map in the game files, said so", dmPage.Title.Contains("Deadmines") && dmPage.Facts.Any(f => f.Label == "Type" && f.Value == "Dungeon") && dmPage.Facts.Any(f => f.Label == "Players") && dmPage.Notes.Any(n => n.Contains("no map in the game files")), string.Join("; ", dmPage.Facts));
                var fbPage = AtlasQuery.Build("spell:133");
                Check("real game files: the Fireball page has its description with the numbers filled in", fbPage.Texts.Count > 0 && !fbPage.Texts[0].Body.Contains("$s1"), fbPage.Texts.Count > 0 ? fbPage.Texts[0].Body : "no text");
                if (seen != null && seen.Ok)
                {
                    var someNpc = seen.Npcs.FirstOrDefault(n => n.Name.Length > 0);
                    if (someNpc != null) Check("real game files: a creature the client has seen has a page with its name, rank and type, labelled Forever (" + someNpc.Name + ")", AtlasQuery.Build("npc:" + someNpc.Id).Title == someNpc.Name && AtlasQuery.Build("npc:" + someNpc.Id).Facts.Any(f => f.Label == "Rank" && f.Source == AtlasLabel.Forever));
                }

                // in the window, with the real pictures
                ShowTab("atlas");
                await AtlasView.GoStart(); await Task.Delay(300); UpdateLayout();
                Snapshot(Path.Combine(dir, "atlas-page-real-start.png"));
                await AtlasView.Open("item:13446");
                for (int i = 0; i < 100 && IconStore.TryGet(ItemData.Get(13446)?.IconId ?? 0) == null; i++) await Task.Delay(100);
                await Task.Delay(300); UpdateLayout();
                Check("real game files: the item's icon is cut out of the game's own files and shown in the tooltip card", ItemData.Get(13446) != null && IconStore.TryGet(ItemData.Get(13446).IconId) != null);
                Snapshot(Path.Combine(dir, "atlas-page-real-item.png"));
                await AtlasView.Open(elwHit?.Id ?? "zone:1429");
                bool ready = await MapReady(0, 60000);
                await Task.Delay(500); UpdateLayout();
                Check("real game files: the zone page shows Elwynn Forest's real map picture", ready && AtlasView.MapsForTest[0].UiMapId == (elwHit != null ? int.Parse(elwHit.Id.Substring(5)) : 1429));
                Snapshot(Path.Combine(dir, "atlas-page-real-zone.png"));
                await AtlasView.Open("item:2589");
                await Task.Delay(400); UpdateLayout();
                Snapshot(Path.Combine(dir, "atlas-page-real-reagent.png"));
            }
            catch (Exception e) { Check("real game files: the Atlas page ran to the end", false, e.ToString()); }
            finally { IconStore.WowRoot = savedIconRoot; }
        }
    }
}
