using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Atlas.Classic;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Maps;

namespace ElansAddonHub
{
    // --selftest "atlaspage": the Atlas page (layer F) on an invented world (MainWindow.AtlasPageTest.Data.cs): search (grouping, ranking, ids), the pages of every kind with
    // every layer and with each layer missing, the item sources table (Lodge above Classic, merged rows, folded world drops, labels), markers from Classic world coordinates,
    // the Classic baseline card with a fake downloader, links and history, the page in the window (never blocking), screenshots.
    public partial class MainWindow
    {
        static DateTime AtUtc(long unix) => new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(unix);

        static IEnumerable<DependencyObject> AtlasWalk(DependencyObject d)
        {
            if (d == null) yield break;
            yield return d;
            int n = d is System.Windows.Media.Visual || d is System.Windows.Media.Media3D.Visual3D ? System.Windows.Media.VisualTreeHelper.GetChildrenCount(d) : 0;
            for (int i = 0; i < n; i++)
                foreach (var c in AtlasWalk(System.Windows.Media.VisualTreeHelper.GetChild(d, i))) yield return c;
        }

        static List<string> AtlasTexts(DependencyObject root)
        {
            var l = new List<string>();
            foreach (var d in AtlasWalk(root))
            {
                if (d is TextBlock tb && tb.Visibility == Visibility.Visible && ElansAddonHub.AtlasPage.TextOf(tb).Length > 0) l.Add(ElansAddonHub.AtlasPage.TextOf(tb));
                else if (d is Button b && b.Content is string s && s.Length > 0) l.Add(s);
            }
            return l;
        }

        static AtlasRow RowOf(AtlasPageModel m, string key, string rowKey) => m.Section(key)?.Rows.Concat(m.Section(key)?.Hidden ?? new List<AtlasRow>()).FirstOrDefault(r => r.Key == rowKey);

        async Task<string> AtlasPageTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            void Check(string what, bool cond, string detail = null) { notes.Add((cond ? "ok   " : "FAIL ") + what + (!cond && detail != null ? " [" + detail + "]" : "")); ok &= cond; }
            var work = Path.Combine(dir, "atlaspage");
            var savedLodge = AtlasQuery.Context.Lodge; var savedNow = AtlasQuery.Context.NowUtc; var savedOwned = AtlasQuery.Context.Owned;
            var savedTiles = MapRender.TileSourceForTest; var savedMapDir = MapRender.DirOverride; var savedHook = Dialog.TestHook;
            var savedLodgeState = AtlasView.LodgeState; var savedWidth = Width;
            try
            {
                if (Directory.Exists(work)) Util.DeleteDir(work);
                Directory.CreateDirectory(work);
                Check("fixture: everything of this test is under the scratch folder (never the real game)", Path.GetFullPath(work).StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase));

                // ---- the invented world
                MapRender.DirOverride = Path.Combine(work, "maps");
                MapRender.TileSourceForTest = fdid => Solid(256, 256, (byte)(70 + fdid % 40), (byte)(96 + fdid % 30), (byte)(60 + fdid % 25));
                var lodge = AtlasTestLodge();
                var classic = AtlasTestClassic();
                void AllLayers()
                {
                    ItemData.SetForTest(AtlasTestItems(), ItemData.State.Ready);
                    SpellData.SetForTest(AtlasTestSpells());
                    SeenData.SetForTest(AtlasTestSeen());
                    MapData.SetForTest(new MapIndex(AtlasTestMapSet()));
                    MapRender.ForgetFailuresForTest();
                    ClassicAtlas.Use(classic);
                    AtlasQuery.Context.Lodge = () => lodge;
                    AtlasView.OwnedOverrideForTest = AtlasTestOwned;
                }
                AtlasQuery.Context.NowUtc = () => AtUtc(AtNow);
                AllLayers();
                AtlasView.LodgeState = () => "Connected · sharing your observations and receiving the Lodge's";

                // ============================================================================================================ search
                var s1 = AtlasQuery.Search("gnarlfang");
                Check("search: an exact name comes first - the NPC group (exact) before the item group (prefix) - and each hit says where it comes from",
                    s1.Groups.Count >= 2 && s1.Groups[0].Kind == "npc" && s1.Groups[0].Hits[0].Name == "Gnarlfang" && s1.Groups[0].Hits[0].Rank == 0 && s1.Groups[0].Hits[0].Label == AtlasLabel.Forever
                    && s1.Groups[1].Kind == "item" && s1.Groups[1].Hits[0].Name == "Gnarlfang's Claw" && s1.Groups[1].Hits[0].Rank == 1, string.Join(" | ", s1.Groups.Select(g => g.Kind + ":" + g.Hits[0])));
                var s2 = AtlasQuery.Search("wolf");
                var wolfNpc = s2.All.FirstOrDefault(h => h.Kind == "npc");
                Check("search: 'wolf' finds the item (prefix) before the creature (second word); the creature only the Classic baseline knows is labelled so",
                    s2.Groups[0].Kind == "item" && s2.Groups[0].Hits[0].Name == "Wolf Pelt" && wolfNpc != null && wolfNpc.Name == "Plains Wolf" && wolfNpc.Label == AtlasLabel.Classic && wolfNpc.Tier == 1);
                var s3 = AtlasQuery.Search("innkeeper");
                var inn = s3.All.Where(h => h.Kind == "npc").ToList();
                Check("search: a creature the client has seen and the Classic baseline has is ONE hit, labelled Forever, with the baseline's level added", inn.Count == 1 && inn[0].Label == AtlasLabel.Forever && inn[0].Detail.Contains("Innkeeper") && inn[0].Detail.Contains("level 20"), string.Join("|", inn.Select(h => h.ToString())));
                var s4 = AtlasQuery.Search("linen");
                var linenItems = s4.Groups.First(g => g.Kind == "item").Hits;
                Check("search: within a group the better match first (Linen Cloth = prefix before Heavy Linen Bandage = second word)", linenItems.Count == 2 && linenItems[0].Name == "Linen Cloth" && linenItems[1].Name == "Heavy Linen Bandage" && s4.Groups.Any(g => g.Kind == "spell"), string.Join("|", linenItems.Select(h => h.Name)));
                var s5 = AtlasQuery.Search("item:100"); var s6 = AtlasQuery.Search("NPC 2"); var s7 = AtlasQuery.Search("item:999999"); var s8 = AtlasQuery.Search("npc=1");
                Check("search: id links in three spellings go straight to the thing (item:100, 'NPC 2', npc=1)", s5.ById && s5.Total == 1 && s5.Groups[0].Hits[0].Id == "item:100" && s5.Groups[0].Hits[0].Name == "Gnarlfang's Claw"
                    && s6.ById && s6.Groups[0].Hits[0].Id == "npc:2" && s6.Groups[0].Hits[0].Name == "Innkeeper Hale" && s8.Groups[0].Hits[0].Id == "npc:1");
                Check("search: an id nothing knows still opens a page (it will say so): 'Item 999999', ranked as an id", s7.ById && s7.Groups[0].Hits[0].Id == "item:999999" && s7.Groups[0].Hits[0].Name == "Item 999999" && s7.Groups[0].Hits[0].Rank == 4);
                var s9 = AtlasQuery.Search("green"); var s10 = AtlasQuery.Search("crypt"); var s11 = AtlasQuery.Search("mine");
                Check("search: zones and instances are found (Greenvale as a zone in Northland; Crypt of Bones as a dungeon of 5), alongside the Crypt creatures and item",
                    s9.Groups.Any(g => g.Kind == "zone" && g.Hits[0].Id == "zone:11" && g.Hits[0].Detail == "Zone in Northland") && s10.Groups.Any(g => g.Kind == "instance" && g.Hits[0].Id == "instance:99" && g.Hits[0].Detail == "Dungeon · 5 players")
                    && s10.Groups.Any(g => g.Kind == "npc") && s10.Groups.Any(g => g.Kind == "item"));
                Check("search: a placeholder map (<unused> Mine) is never offered", !s11.All.Any(h => h.Kind == "instance"));
                var s12 = AtlasQuery.Search("wanderer");
                var wg = s12.Groups.First(g => g.Kind == "npc");
                Check("search: a group shows its best 6 and says how many matched (30 creatures)", wg.Hits.Count == 6 && wg.Total == 30);
                Check("search: one letter finds nothing, blank finds nothing, case does not matter", AtlasQuery.Search("g").Total == 0 && AtlasQuery.Search("   ").Total == 0 && AtlasQuery.Search("GNARLFANG").Groups[0].Hits[0].Name == "Gnarlfang");
                Check("search: spells, skills and factions are searched too", AtlasQuery.Search("fire").Groups.Any(g => g.Kind == "spell" && g.Hits[0].Name == "Fireball") && AtlasQuery.Search("first").Groups.Any(g => g.Kind == "skill" && g.Hits[0].Id == "skill:129")
                    && AtlasQuery.Search("storm").Groups.Any(g => g.Kind == "faction" && g.Hits[0].Id == "faction:72"));
                Check("search: a recipe is a spell with 'recipe' in its detail, before a plain spell of the same match", AtlasQuery.Search("heavy").Groups.First(g => g.Kind == "spell").Hits[0].Detail.Contains("recipe"));

                // ============================================================================================================ the item page
                var it = AtlasQuery.Build("item:100");
                Check("item page: name, quality, the game's own tooltip lines (item level, slot, requirement, description, sell price) and their source", it.Title == "Gnarlfang's Claw" && it.Quality == 3 && !it.NotFound && it.TooltipSource == AtlasLabel.Forever
                    && it.Tooltip.Any(l => l.Kind == "ilvl" && l.Text == "Item Level 14") && it.Tooltip.Any(l => l.Kind == "req" && l.Text == "Requires Level 10") && it.Tooltip.Any(l => l.Kind == "desc") && it.Tooltip.Any(l => l.Kind == "price" && l.Copper == 12050), string.Join("; ", it.Tooltip));
                var own = it.Section("owned");
                Check("item page: 'Your characters' lists who has it (Elan: bags and bank) and the guild bank", own != null && own.Rows.Count == 2 && own.Rows[0].Title == "Elan" && own.Rows[0].Detail.Contains("Bags 1") && own.Rows[0].Detail.Contains("Total 2") && own.Rows[1].Title == "Nasferatu" && own.Rows[1].Right == "× 3");
                var src = it.Section("sources");
                var tiers = src.Rows.Select(r => r.Tier).ToList();
                Check("sources: Lodge-observed rows first, then the Classic baseline's (tiers never go down)", tiers.Count >= 4 && tiers.SequenceEqual(tiers.OrderBy(x => x)) && src.Rows[0].Tier == 0 && src.Rows.Last().Tier == 2, string.Join(",", tiers));
                var drop = RowOf(it, "sources", "npc:1");
                Check("sources: the Lodge's drop row says '12% · 340 kills · 3 members', is labelled Forever, links to the NPC and carries the Classic baseline's own figure on the same row (no second row)",
                    drop != null && drop.Detail == "12% · 340 kills · 3 members" && drop.Label == AtlasLabel.Forever && drop.Link == "npc:1" && drop.Type == "Drop" && drop.Extra == AtlasLabel.Classic + ": 10%" && src.Rows.Count(r => r.Key == "npc:1") == 1, drop?.ToString());
                Check("sources: the best Forever drop is the very first row (Lodge drops before vendors before quests)", src.Rows[0].Key == "npc:1" && src.Rows.Select(r => r.Type).SkipWhile(t => t == "Drop").First() == "Vendor", string.Join(",", src.Rows.Select(r => r.Key)));
                var ven = RowOf(it, "sources", "vendor:2");
                Check("sources: the vendor row has the Lodge's price ('1g 20s 50c'), the zone, and is Forever", ven != null && ven.Detail == "1g 20s 50c" && ven.Zone == "Highkeep" && ven.Label == AtlasLabel.Forever && ven.Tier == 0, ven?.ToString());
                var qr = RowOf(it, "sources", "quest:7");
                Check("sources: the quest row (Lodge-seen reward) is merged with the baseline's reward of the same quest", qr != null && qr.Tier == 0 && qr.Detail == "Reward" && qr.Extra.StartsWith(AtlasLabel.Classic) && src.Rows.Count(r => r.Key == "quest:7") == 1, qr?.ToString());
                var wolfRow = RowOf(it, "sources", "npc:3");
                Check("sources: a drop only the Classic baseline knows (Plains Wolf 0.5 %) is below the Lodge rows and labelled 'Classic baseline'", wolfRow != null && wolfRow.Tier == 2 && wolfRow.Label == AtlasLabel.Classic && wolfRow.Detail == "0.5%" && src.Rows.IndexOf(wolfRow) > src.Rows.IndexOf(qr), wolfRow?.ToString());
                Check("sources: 30 world-drop sources far under 0.1 % are folded into one line, not listed", src.Hidden.Count == 30 && src.HiddenText == "+ 30 world-drop sources under 0.1%" && src.Rows.All(r => !r.Key.StartsWith("npc:") || int.Parse(r.Key.Substring(4)) < 10) && src.Hidden.All(r => r.Sort < AtlasQuery.TinyChance), src.HiddenText);
                Check("sources: every row names its source (Forever or Classic baseline), folded ones too", src.Rows.Concat(src.Hidden).All(r => r.Label == AtlasLabel.Forever || r.Label == AtlasLabel.Classic));
                var fd = it.Section("finds");
                Check("found by the Lodge: newest first with the member's Lodge name, what it came from, the zone and how long ago; the section says '2 finds by 2 members'",
                    fd != null && fd.Rows.Count == 2 && fd.Rows[0].Title == "Tess" && fd.Rows[0].Right == "3 h ago" && fd.Rows[0].Detail == "1× from Gnarlfang · Greenvale" && fd.Rows[0].Link == "npc:1"
                    && fd.Rows[1].Title == "Bob" && fd.Rows[1].Right == "2 d ago" && fd.Rows[1].Detail.StartsWith("1× bought from Innkeeper Hale") && fd.Note.StartsWith("2 finds by 2 members"), fd == null ? "none" : string.Join(" | ", fd.Rows));
                Check("item page: the Classic quest that needs it is listed apart ('Needed for quests') for another item", AtlasQuery.Build("item:101").Section("needed")?.Rows.Count == 1 && AtlasQuery.Build("item:101").Section("needed").Rows[0].Link == "quest:7");

                // the map: zones, the Classic world coordinates turned into the right zone map, the Lodge's fractions as they are
                var plan = it.Map;
                var z11 = plan.Zones.FirstOrDefault(z => z.UiMapId == 11); var z13 = plan.Zones.FirstOrDefault(z => z.UiMapId == 13);
                var gnarl = z11?.Markers.FirstOrDefault(m => m.Kind == MarkerKind.Spawn && m.Label == "Gnarlfang" && m.Tooltip.Contains(AtlasLabel.Classic));
                Check("map: the Classic spawn of Gnarlfang (world 500, 1500 on map 0) lands in Greenvale at (0.25, 0.5) = ((MaxY - y) / width, (MaxX - x) / height), tooltip says Classic baseline",
                    gnarl != null && Math.Abs(gnarl.Fx - 0.25) < 1e-9 && Math.Abs(gnarl.Fy - 0.5) < 1e-9 && gnarl.LinkId == "npc:1", gnarl == null ? string.Join("|", plan.Zones.Select(z => z.ToString())) : "");
                var lodgeSpot = z11?.Markers.FirstOrDefault(m => m.Kind == MarkerKind.Spawn && m.Tooltip.Contains(AtlasLabel.Forever) && Math.Abs(m.Fx - 0.3) < 1e-9 && Math.Abs(m.Fy - 0.4) < 1e-9);
                var lodgeFind = z11?.Markers.FirstOrDefault(m => m.Kind == MarkerKind.Find && Math.Abs(m.Fx - 0.31) < 1e-9);
                var lodgeVend = z13?.Markers.FirstOrDefault(m => m.Kind == MarkerKind.Vendor && Math.Abs(m.Fx - 0.667) < 1e-9 && Math.Abs(m.Fy - 0.75) < 1e-9);
                Check("map: the Lodge's spawn points and finds (zone fractions) go where they are, tooltips say Forever; the Lodge vendor sits on Highkeep", lodgeSpot != null && lodgeFind != null && lodgeFind.Tooltip.Contains("Forever") && lodgeVend != null && lodgeVend.LinkId == "npc:2");
                Check("map: the zone with the most Lodge-observed places is the one shown first; the zone picker has Greenvale and Highkeep", plan.Best.UiMapId == 11 && plan.Zones.Count >= 2 && plan.Zones.Select(z => z.UiMapId).Contains(13));
                Check("map: a baseline drop under 1 % (Plains Wolf, 0.5 %) is not drawn while better places are known; at most 600 places in all", z11.Markers.All(m => m.LinkId != "npc:3") && plan.MarkerCount <= AtlasQuery.MaxMarkers);
                var wolfOnly = AtlasQuery.Build("item:101");
                Check("map: ... but with nothing better the baseline's places are all there are, 40 at most per source (61 wolf spawns, 45 % and 33 % drops)", wolfOnly.Map.Zones.SelectMany(z => z.Markers).Count(m => m.LinkId == "npc:3") <= AtlasQuery.MaxSpawnsPerSource && wolfOnly.Map.Zones.SelectMany(z => z.Markers).Count(m => m.LinkId == "npc:3") > 10 && wolfOnly.Map.MarkerCount <= AtlasQuery.MaxMarkers);
                var tinyMarkers = plan.Zones.SelectMany(z => z.Markers).Count(m => m.LinkId != null && m.LinkId.StartsWith("npc:") && int.Parse(m.LinkId.Substring(4)) >= 10);
                Check("map: folded world-drop sources put nothing on the map", tinyMarkers == 0);
                var cl2 = classic.Npcs[1].Spawns[0];
                var viaIndex = MapData.Index.WorldToMap(0, cl2.X, cl2.Y);
                Check("map: the same point through MapIndex.WorldToMap agrees (zone 11)", viaIndex != null && viaIndex.Value.UiMapId == 11 && Math.Abs(viaIndex.Value.Fx - gnarl.Fx) < 1e-9);
                var ring = AtlasQuery.Build("item:105");
                var crypt = ring.Map.Zones.FirstOrDefault(z => z.UiMapId == 20);
                var lord = crypt?.Markers.FirstOrDefault(m => m.LinkId == "npc:4");
                Check("map: a boss inside an instance that has a map (Crypt of Bones, floor 'Crypt cellar') is placed on it", lord != null && Math.Abs(lord.Fx - 0.5) < 1e-9 && Math.Abs(lord.Fy - 0.5) < 1e-9, string.Join("|", ring.Map.Zones.Select(z => z.ToString())));
                Check("map: spawn points inside an instance with no map (Deep Hall) are counted, not drawn, and offered as a link to the instance", ring.Map.InInstances.Count == 1 && ring.Map.InInstances[0].Key == 98 && ring.Map.InInstances[0].Value == 1);
                var ringSrc = ring.Section("sources");
                Check("sources: the Lodge-opened chest (10 % of 50 opens, 2 members) is a Forever row 'Object'; the baseline's 15 % rides along", RowOf(ring, "sources", "obj:50")?.Detail == "10% · 50 opens · 2 members" && RowOf(ring, "sources", "obj:50").Extra == AtlasLabel.Classic + ": 15%" && ringSrc.Rows[0].Key == "obj:50");
                var cloak = AtlasQuery.Build("item:103").Section("sources");
                Check("sources: an item with nothing but tiny sources still shows its 6 best and folds the rest ('+ 2 more world-drop sources under 0.1%')", cloak.Rows.Count == 6 && cloak.Hidden.Count == 2 && cloak.HiddenText == "+ 2 more world-drop sources under 0.1%" && cloak.Rows[0].Sort >= cloak.Rows[5].Sort);
                var cloth = AtlasQuery.Build("item:102");
                var clothVendor = RowOf(cloth, "sources", "vendor:2");
                Check("sources: a vendor both the Lodge and the baseline know is one row (Lodge price, baseline stock on its second line); the Lodge's 5.9 % drop is listed", clothVendor != null && clothVendor.Detail.StartsWith("25c") && clothVendor.Detail.Contains("stack of 20") && clothVendor.Extra == AtlasLabel.Classic + ": unlimited stock" && RowOf(cloth, "sources", "npc:1")?.Detail.StartsWith("5.9%") == true, clothVendor?.ToString());
                Check("used as a reagent: Linen Cloth is used by the Heavy Linen Bandage recipe (First Aid 40, uses 2)", cloth.Section("reagent")?.Rows.Count == 1 && cloth.Section("reagent").Rows[0].Title == "Heavy Linen Bandage" && cloth.Section("reagent").Rows[0].Detail == "First Aid 40 · uses 2" && cloth.Section("reagent").Rows[0].Link == "item:108");
                var bandage = AtlasQuery.Build("item:108");
                var rec = RowOf(bandage, "sources", "recipe:4000");
                Check("sources: a recipe that makes the item (client data, tier 1) names the skill, its rank and the reagents", rec != null && rec.Tier == 1 && rec.Label == AtlasLabel.Forever && rec.Type == "Recipe" && rec.Detail == "First Aid 40 · Linen Cloth ×2" && rec.Link == "spell:4000", rec?.ToString());
                var mystery = AtlasQuery.Build("item:555");
                Check("item page: an item only a Lodge member's game revealed has its name, quality, level, slot and stats from the pool", mystery.Title == "Mystery Ring" && mystery.Quality == 4 && mystery.Tooltip.Any(l => l.Kind == "ilvl" && l.Text == "Item Level 20") && mystery.Tooltip.Any(l => l.Kind == "stat") && mystery.Subtitle.Contains("revealed") && !mystery.NotFound);
                var classicOnly = AtlasQuery.Build("item:107");
                Check("item page: an item only the Classic baseline knows shows its few facts, labelled Classic baseline, and says the game has not shown it yet", classicOnly.Title == "Wanderer's Coin" && classicOnly.TooltipSource == AtlasLabel.Classic && classicOnly.Tooltip.Any(l => l.Kind == "unrevealed"));
                var gone = AtlasQuery.Build("item:99999");
                Check("item page: an id no layer knows says so (NotFound with a message), no exception", gone.NotFound && gone.Message.Contains("99999"));

                // ============================================================================================================ the other pages
                var npc = AtlasQuery.Build("npc:1");
                Check("NPC page: name, title (Forever: the client's cache), level (Classic baseline), rank, type, faction through the client's faction table, each fact with its source",
                    npc.Title == "Gnarlfang" && npc.Facts.Any(f => f.Label == "Title" && f.Value == "<Fanglord>" && f.Source == AtlasLabel.Forever) && npc.Facts.Any(f => f.Label == "Level" && f.Value == "12" && f.Source == AtlasLabel.Classic)
                    && npc.Facts.Any(f => f.Label == "Rank" && f.Value == "Boss" && f.Source == AtlasLabel.Forever) && npc.Facts.Any(f => f.Label == "Type" && f.Value == "Beast") && npc.Facts.Any(f => f.Label == "Faction" && f.Value == "Stormwind" && f.Link == "faction:72"), string.Join("; ", npc.Facts));
                var loot = npc.Section("loot");
                Check("NPC page: its loot table - the Lodge's observed rows first (12 %, 5.9 %), then the baseline's; merged where both know", loot.Rows.Count == 3 && loot.Rows[0].Key == "item:100" && loot.Rows[0].Detail.StartsWith("12% · 41 of 340 kills") && loot.Rows[0].Extra == AtlasLabel.Classic + ": 10%" && loot.Rows[1].Key == "item:102" && loot.Rows[1].Detail.StartsWith("5.9%")
                    && loot.Rows[2].Key == "item:101" && loot.Rows[2].Tier == 2 && loot.Rows[2].Label == AtlasLabel.Classic && loot.Rows[2].Detail == "45%", string.Join(" | ", loot.Rows));
                Check("NPC page: where it stands - Found in Greenvale (a link), the map has its Lodge spawn points and its Classic spawn", npc.MapFirst && npc.Facts.Any(f => f.Label == "Found in" && f.Value == "Greenvale" && f.Link == "zone:11") && npc.Map.Zones.Any(z => z.UiMapId == 11 && z.Markers.Count == 3));
                var innk = AtlasQuery.Build("npc:2");
                var sells = innk.Section("vendor");
                Check("NPC page: a vendor lists what it sells with the Lodge's prices (and the baseline's stock) and the quests it gives", sells != null && sells.Rows.Count == 2 && sells.Rows.Any(r => r.Key == "item:100" && r.Detail == "1g 20s 50c") && sells.Rows.Any(r => r.Key == "item:102" && r.Extra.StartsWith(AtlasLabel.Classic))
                    && innk.Section("quests")?.Rows.Count == 1 && innk.Section("quests").Rows[0].Link == "quest:7" && innk.Facts.Any(f => f.Label == "Services" && f.Value == "Quest giver, Vendor"), string.Join("; ", innk.Facts));
                var skinning = AtlasQuery.Build("npc:3");
                Check("NPC page: skinning loot is its own table, a creature known only to the Classic baseline is labelled so", skinning.Section("skin")?.Rows.Count == 1 && skinning.Section("skin").Rows[0].Key == "item:101" && skinning.Facts.All(f => f.Source != AtlasLabel.Forever) && skinning.Facts.Any(f => f.Label == "Level" && f.Value == "5-6"));
                var seenOnly = AtlasQuery.Build("npc:60");
                Check("NPC page: a creature only the client has seen (not in the baseline) still has a page, no loot, no map, a note saying what is missing", !seenOnly.NotFound && seenOnly.Title == "Forever Only Boar" && !seenOnly.Map.Any && seenOnly.Facts.All(f => f.Source == AtlasLabel.Forever) && seenOnly.Section("loot").Rows.Count == 0);
                Check("NPC page: an unknown id is NotFound with a message", AtlasQuery.Build("npc:77777").NotFound);
                var obj = AtlasQuery.Build("obj:50");
                Check("object page: name and kind (Forever), its loot (Lodge 10 %, baseline 15 % on the same row), its position on the map (Node marker)", obj.Title == "Ancient Chest" && obj.Facts.Any(f => f.Label == "Kind" && f.Value == "chest or node") && obj.Section("loot").Rows.Count == 1 && obj.Section("loot").Rows[0].Detail.StartsWith("10% · 5 of 50 opens")
                    && obj.Map.Zones.Any(z => z.UiMapId == 11 && z.Markers.Any(m => m.Kind == MarkerKind.Node && m.LinkId == "obj:50")));

                var zone = AtlasQuery.Build("zone:11");
                var cr = zone.Section("creatures");
                Check("zone page: name, type, 'In Northland' (link), creature levels (Classic baseline), the map of the zone", zone.Title == "Greenvale" && zone.MapFirst && zone.Map.ShowZone == 11 && zone.Facts.Any(f => f.Label == "In" && f.Link == "zone:10") && zone.Facts.Any(f => f.Label == "Creature levels" && f.Source == AtlasLabel.Classic) && zone.Crumbs.Count == 2, string.Join("; ", zone.Facts));
                Check("zone page: the creatures standing there, bosses first (Gnarlfang before Plains Wolf), not the innkeeper of Highkeep, with spawn counts", cr != null && cr.Rows[0].Link == "npc:1" && cr.Rows.Any(r => r.Link == "npc:3" && r.Detail.Contains("spawns")) && !cr.Rows.Any(r => r.Link == "npc:2"), cr == null ? "none" : string.Join(" | ", cr.Rows.Take(3)));
                Check("zone page: the more specific city map wins - Highkeep has the innkeeper, Greenvale does not", AtlasQuery.Build("zone:13").Section("creatures").Rows.Any(r => r.Link == "npc:2") && AtlasQuery.Build("zone:13").Facts.Any(f => f.Label == "In"));
                var zq = zone.Section("quests");
                Check("zone page: quests sorted under it - the client's cache first (Forever), then the baseline's; a quest both know once", zq != null && zq.Rows.Count == 3 && zq.Rows.Take(2).All(r => r.Label == AtlasLabel.Forever) && zq.Rows[2].Label == AtlasLabel.Classic && zq.Rows[2].Link == "quest:9" && zq.Rows.Count(r => r.Key == "quest:7") == 1, zq == null ? "none" : string.Join(" | ", zq.Rows));
                Check("zone page: objects standing there, and notable drops (what the Lodge found of rare quality first, the baseline's best after)", zone.Section("objects")?.Rows.Any(r => r.Link == "obj:50") == true && zone.Section("drops") != null && zone.Section("drops").Rows[0].Tier == 0 && zone.Section("drops").Rows.Any(r => r.Key == "item:105"));
                Check("zone page: markers on the zone's own map only - the boss and the Lodge's finds", zone.Map.Zones.Count == 1 && zone.Map.Zones[0].UiMapId == 11 && zone.Map.Zones[0].Markers.Any(m => m.LinkId == "npc:1") && zone.Map.Zones[0].Markers.Any(m => m.Kind == MarkerKind.Find));
                var cont = AtlasQuery.Build("zone:10");
                Check("zone page: a continent lists its zones as links instead of thousands of creatures", cont.Section("children")?.Rows.Count == 4 && cont.Section("children").Rows.All(r => r.Link.StartsWith("zone:")) && cont.Section("creatures") == null);
                var inst = AtlasQuery.Build("instance:99");
                Check("instance page: type and players (client), the boss of the Classic baseline with its level, the better loot of the boss, the instance map (floors)", inst.Title == "Crypt of Bones" && inst.Facts.Any(f => f.Label == "Type" && f.Value == "Dungeon") && inst.Facts.Any(f => f.Label == "Players" && f.Value == "5")
                    && inst.Section("bosses").Rows.Count == 1 && inst.Section("bosses").Rows[0].Link == "npc:4" && inst.Section("bosses").Rows[0].Detail.StartsWith("Level 24") && inst.Section("drops").Rows[0].Key == "item:105" && inst.Section("drops").Rows.Count == 2 && inst.Map.InstanceId == 99
                    && inst.Section("creatures").Rows.Any(r => r.Link == "npc:6"), string.Join("; ", inst.Facts));
                var inst2 = AtlasQuery.Build("instance:98");
                Check("instance page: a raid with no map in the game files says so (note) and still lists its boss", inst2.Title == "Deep Hall" && inst2.Map.InstanceId == 98 && inst2.Notes.Any(n => n.Contains("no map in the game files")) && inst2.Section("bosses").Rows.Count == 1 && inst2.Subtitle.Contains("no map in the game files"));
                Check("instance page: a placeholder map and an unknown map are NotFound", AtlasQuery.Build("instance:97").NotFound && AtlasQuery.Build("instance:555").NotFound);

                var q = AtlasQuery.Build("quest:7");
                Check("quest page: title and level (client cache), requires level, zone as a link, classes (Classic baseline), the cache's objective and description text", q.Title == "The Lost Pup" && q.Facts.Any(f => f.Label == "Level" && f.Value == "6" && f.Source == AtlasLabel.Forever) && q.Facts.Any(f => f.Label == "Requires level" && f.Value == "4")
                    && q.Facts.Any(f => f.Label == "Zone" && f.Value == "Greenvale" && f.Link == "zone:11") && q.Facts.Any(f => f.Label == "Classes" && f.Value == "Hunter, Rogue" && f.Source == AtlasLabel.Classic) && q.Texts.Count == 2 && q.Texts[0].Body.StartsWith("Bring four"), string.Join("; ", q.Facts));
                Check("quest page: rewards (the Lodge saw item 100, the baseline lists it too), 'Choose one of' (101 both, 102 only the baseline x5), what it needs (4 Wolf Pelts)", q.Section("rewards").Rows.Count == 1 && q.Section("rewards").Rows[0].Tier == 0 && q.Section("rewards").Rows[0].Extra.StartsWith(AtlasLabel.Classic)
                    && q.Section("choices").Rows.Count == 2 && q.Section("choices").Rows[0].Key == "item:101" && q.Section("choices").Rows[1].Detail == "×5" && q.Section("objectives").Rows[0].Detail == "×4");
                Check("quest page: given by Innkeeper Hale (zone Highkeep) and the giver's position on the map as a Quest marker", q.Section("givers").Rows[0].Link == "npc:2" && q.Section("givers").Rows[0].Zone == "Highkeep" && q.Map.Zones.Any(z => z.UiMapId == 13 && z.Markers.Any(m => m.Kind == MarkerKind.Quest)));
                Check("quest page: a quest only the Classic baseline knows, and one the client has seen without the baseline, both have pages", !AtlasQuery.Build("quest:9").NotFound && !AtlasQuery.Build("quest:8").NotFound && AtlasQuery.Build("quest:8").Title == "A Forever Only Errand" && AtlasQuery.Build("quest:4040").NotFound);
                var sp = AtlasQuery.Build("spell:4000");
                Check("spell page: a recipe - name, rank, cast time, description; the recipe with its skill, what it makes and what it needs", sp.Title == "Heavy Linen Bandage" && sp.Facts.Any(f => f.Label == "Rank" && f.Value == "Rank 2") && sp.Facts.Any(f => f.Label == "Cast time" && f.Value == "Instant") && sp.Texts[0].Body == "Heals the target."
                    && sp.Section("recipe").Rows.Any(r => r.Link == "skill:129") && sp.Section("recipe").Rows.Any(r => r.Type == "Creates" && r.Link == "item:108") && sp.Section("recipe").Rows.Any(r => r.Type == "Reagent" && r.Link == "item:102" && r.Detail == "Needs ×2"));
                var fb = AtlasQuery.Build("spell:133");
                Check("spell page: cast time 1.5 s, range 35 yards; a plain spell has no recipe", fb.Facts.Any(f => f.Label == "Cast time" && f.Value == "1.5 s") && fb.Facts.Any(f => f.Label == "Range" && f.Value == "35 yards") && fb.Section("recipe") == null);
                var sk = AtlasQuery.Build("skill:129");
                Check("skill page: the recipes of the skill, each linking to its spell", sk.Title == "First Aid" && sk.Section("recipes").Rows.Count == 1 && sk.Section("recipes").Rows[0].Link == "spell:4000" && sk.Section("recipes").Rows[0].Title == "Heavy Linen Bandage" && sk.Texts[0].Body.StartsWith("Bandages"));
                var fac = AtlasQuery.Build("faction:469");
                Check("faction page: its parts link to their pages; a part links back up", fac.Section("members").Rows[0].Link == "faction:72" && AtlasQuery.Build("faction:72").Facts.Any(f => f.Label == "Part of" && f.Link == "faction:469"));
                Check("pages: a link that is not an id says so, the way back is still a page", AtlasQuery.Build("hello").NotFound && AtlasQuery.Build("item:abc").NotFound && AtlasQuery.Build("").NotFound);

                // ============================================================================================================ each layer missing
                var ids = new[] { "item:100", "item:555", "item:101", "npc:1", "npc:2", "npc:60", "obj:50", "quest:7", "zone:11", "zone:10", "instance:99", "instance:98", "spell:4000", "skill:129", "faction:72" };
                string Sweep(string name, Func<AtlasPageModel, string> extra = null)
                {
                    var bad = new List<string>();
                    foreach (var id in ids)
                    {
                        try
                        {
                            var m = AtlasQuery.Build(id);
                            if (m == null || string.IsNullOrEmpty(m.Title) || m.Id != id) bad.Add(id + " has no title");
                            else if (m.NotFound && string.IsNullOrEmpty(m.Message)) bad.Add(id + " is NotFound without a message");
                            var e = extra?.Invoke(m); if (!string.IsNullOrEmpty(e)) bad.Add(e);
                        }
                        catch (Exception ex) { bad.Add(id + " threw " + ex.GetType().Name + ": " + ex.Message); }
                    }
                    return bad.Count == 0 ? null : name + ": " + string.Join("; ", bad);
                }
                var nothing = Sweep("all layers", m => m.NotFound && m.Id != "item:99999" ? m.Id + " NotFound with every layer" : null);
                Check("layers: every kind of page builds with all layers present (none NotFound)", nothing == null, nothing);

                ClassicAtlas.Use(null);
                var noClassic = Sweep("no Classic");
                var itNC = AtlasQuery.Build("item:100"); var npcNC = AtlasQuery.Build("npc:1"); var zoneNC = AtlasQuery.Build("zone:11"); var instNC = AtlasQuery.Build("instance:99");
                Check("layers: without the Classic download every page still builds; the item shows only the Lodge's rows (3), no folded line, and a note says how to get the baseline", noClassic == null && itNC.Section("sources").Rows.Count == 3 && itNC.Section("sources").Hidden.Count == 0 && itNC.Section("sources").Rows.All(r => r.Label == AtlasLabel.Forever)
                    && itNC.Notes.Any(n => n.Contains("Classic baseline") && n.Contains("start page")), noClassic ?? string.Join("; ", itNC.Notes));
                Check("layers: without Classic the NPC keeps the client's facts and the Lodge's loot, the zone has its map but no creature list, the instance its map and a note", npcNC.Title == "Gnarlfang" && npcNC.Section("loot").Rows.Count == 2 && npcNC.Section("loot").Rows.All(r => r.Label == AtlasLabel.Forever) && npcNC.Facts.Any(f => f.Label == "Rank")
                    && zoneNC.Section("creatures") == null && zoneNC.Map.ShowZone == 11 && zoneNC.Notes.Any(n => n.Contains("Classic baseline")) && instNC.Map.InstanceId == 99 && instNC.Section("bosses") == null && instNC.Notes.Any(n => n.Contains("Classic baseline")));
                Check("layers: without Classic the Lodge's spawn points are still on the map (3 markers of the NPC), the quest page shows the client's text", npcNC.Map.Zones.Any(z => z.Markers.Count == 2) && AtlasQuery.Build("quest:7").Texts.Count == 2);
                Check("search: without Classic only the layers that are there answer (no 'Plains Wolf', still 'Gnarlfang')", !AtlasQuery.Search("wolf").All.Any(h => h.Kind == "npc") && AtlasQuery.Search("gnarlfang").Groups[0].Hits[0].Name == "Gnarlfang");

                ClassicAtlas.Use(classic);
                AtlasQuery.Context.Lodge = () => null;
                var noLodge = Sweep("no Lodge");
                var itNL = AtlasQuery.Build("item:100");
                Check("layers: without a Lodge every page builds; the item lists the Classic baseline's rows, no 'Found by the Lodge', and a note says what the Lodge would add", noLodge == null && itNL.Section("finds") == null && itNL.Section("sources").Rows.All(r => r.Label == AtlasLabel.Classic) && itNL.Section("sources").Rows.Count >= 2
                    && itNL.Notes.Any(n => n.Contains("No Lodge observations")), noLodge ?? string.Join("; ", itNL.Notes));
                Check("layers: without a Lodge the revealed item is unknown (NotFound), the map has only Classic markers", AtlasQuery.Build("item:555").NotFound && itNL.Map.Zones.SelectMany(z => z.Markers).All(m => !m.Tooltip.Contains("Forever")));
                AtlasQuery.Context.Lodge = () => new LodgeAtlas();
                Check("layers: a Lodge pool with no data behaves like none", Sweep("empty Lodge") == null && AtlasQuery.Build("item:100").Section("finds") == null);

                AtlasQuery.Context.Lodge = () => lodge;
                ItemData.SetForTest(null, ItemData.State.Disabled, "off"); SpellData.SetForTest(null, SpellData.State.Disabled); SeenData.SetForTest(null, SeenData.State.Disabled); MapData.SetForTest(null, null, MapData.State.Disabled);
                var noGame = Sweep("game data off");
                var itNG = AtlasQuery.Build("item:100");
                Check("layers: with game data switched off every page builds; the item falls back to the baseline and the Lodge (name, sources), says item details are off", noGame == null && itNG.Title == "Gnarlfang's Claw" && itNG.TooltipSource == AtlasLabel.Classic && itNG.Section("sources").Rows.Count >= 3 && itNG.Notes.Any(n => n.Contains("switched off")), noGame ?? string.Join("; ", itNG.Notes));
                Check("layers: with game data off the maps, spells and the client's creature names are gone - zone and spell pages say so, the NPC page uses the baseline's name, markers stay empty (no map to place them)",
                    AtlasQuery.Build("zone:11").NotFound && AtlasQuery.Build("zone:11").Message.Contains("switched off") && AtlasQuery.Build("spell:4000").NotFound && AtlasQuery.Build("npc:1").Title == "Gnarlfang" && AtlasQuery.Build("npc:1").Map.Zones.Count == 1 && AtlasQuery.Build("npc:1").Map.Note.Contains("not read"), string.Join("; ", AtlasQuery.Build("npc:1").Map.Zones.Select(z => z.ToString())));
                Check("search: with game data off only the baseline answers; the page remains usable", AtlasQuery.Search("gnarlfang").Groups.All(g => g.Hits.All(h => h.Label == AtlasLabel.Classic)) && AtlasQuery.Search("green").Total == 0);

                ClassicAtlas.Use(null); AtlasQuery.Context.Lodge = () => null;
                var bare = Sweep("nothing at all");
                Check("layers: with nothing at all (no game data, no baseline, no Lodge) every page is a calm NotFound with a message, never an exception; search answers nothing", bare == null && AtlasQuery.Build("item:100").NotFound && AtlasQuery.Build("npc:1").NotFound && AtlasQuery.Search("gnarlfang").Total == 0, bare);
                AllLayers();

                // ============================================================================================================ history
                var h = new AtlasHistory();
                h.Go("item:1"); h.Go("npc:2"); h.Go("npc:2"); h.Go("zone:3");
                Check("history: visits are kept in order, visiting the page already shown changes nothing", h.Count == 3 && h.Current == "zone:3" && h.CanBack && !h.CanForward);
                Check("history: back and forward walk it; a new visit after going back drops the forward part", h.Back() == "npc:2" && h.Back() == "item:1" && !h.CanBack && h.Forward() == "npc:2" && h.Go("quest:9") && !h.CanForward && h.Count == 3 && h.Back() == "npc:2");
                var h2 = new AtlasHistory(); for (int i = 1; i <= AtlasHistory.Max + 20; i++) h2.Go("item:" + i);
                Check("history: at most " + AtlasHistory.Max + " entries (the oldest drop out)", h2.Count == AtlasHistory.Max && h2.Current == "item:" + (AtlasHistory.Max + 20));
                Check("ids: 'Item 5', 'npc=7', ' obj:9 ' and 'object 3' are ids, 'item:0', 'hello 5' and 'item:5x' are not", AtlasQuery.NormalizeId("Item 5") == "item:5" && AtlasQuery.NormalizeId("npc=7") == "npc:7" && AtlasQuery.NormalizeId(" obj:9 ") == "obj:9" && AtlasQuery.NormalizeId("object 3") == "obj:3"
                    && AtlasQuery.NormalizeId("item:0") == null && AtlasQuery.NormalizeId("hello 5") == null && AtlasQuery.NormalizeId("item:5x") == null);

                await AtlasPageUiTest(dir, work, Check);
            }
            catch (Exception e) { Check("atlaspage test ran to the end", false, e.ToString()); }
            finally
            {
                try
                {
                    AtlasQuery.BuildHookForTest = null;
                    AtlasQuery.Context.Lodge = savedLodge; AtlasQuery.Context.NowUtc = savedNow; AtlasQuery.Context.Owned = savedOwned;
                    AtlasView.OwnedOverrideForTest = null; AtlasView.LodgeState = savedLodgeState;
                    ClassicAtlas.Use(null); ItemData.ResetForTest(); SpellData.ResetForTest(); SeenData.ResetForTest(); MapData.ResetForTest();
                    MapRender.TileSourceForTest = savedTiles; MapRender.DirOverride = savedMapDir; MapRender.ForgetFailuresForTest();
                    Dialog.TestHook = savedHook;
                    Width = savedWidth;
                    ShowTab("addons");
                }
                catch { }
            }
            return string.Join("\r\n", notes) + "\r\natlaspage: " + (ok ? "ok" : "FAILED");
        }
    }
}
