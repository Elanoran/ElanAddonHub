using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Controls;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Atlas.Classic;
using ElansAddonHub.Services.Maps;
using ElansAddonHub.Services.WorldMap;

namespace ElansAddonHub
{
    // --selftest "instances": dungeon and raid entrances on the maps. The Map.db2 ghost spot (CorpseMapID + Corpse[2]) as the entrance, the terrain area under it for the exact
    // zone, LFGDungeons + ContentTuning for the level, the "Dungeons & raids" world map layer (messages, protocol, catalog, kinds), the instance page's Entrance card, the zone
    // page's list, the browse list's zone / level columns; and, when this PC has the game, the real entrances (read-only). The 2D / 3D pages with this layer are exercised in
    // the "worldmap" section (they need its invented continent and its WebView2 session).
    public partial class MainWindow
    {
        // ---- synthetic WDC5 tables in the real layouts (the writer is in MainWindow.ItemDataTest.cs)
        // Map (D43AFAC3): Directory 0 (string), MapName_lang 1 (string), ..., Corpse[2] 6 (two floats), MapType 7, InstanceType 8, ExpansionID 9, AreaTableID 10, ..., CorpseMapID 17, MaxPlayers 18, ..., WdtFileDataID 21
        static byte[] InsMapTable(uint layout = 0xD43AFAC3)
        {
            var fields = new List<W5Field>();
            for (int i = 0; i < 6; i++) fields.Add(F32(i));
            fields.Add(new W5Field { OffsetBits = 192, SizeBits = 64 });
            for (int i = 0; i < 19; i++) fields.Add(new W5Field { OffsetBits = 256 + i * 32, SizeBits = 32 });
            const int S = 108;
            var rows = new[]
            {
                new { id = 13, dir = "test", name = "Test Dungeon", type = 1, cmap = -1, cx = 0f, cy = 0f, pl = 0 },
                new { id = 36, dir = "DeadminesInstance", name = "Deadmines", type = 1, cmap = 0, cx = -11207.8f, cy = 1681.2f, pl = 10 },
                new { id = 43, dir = "WailingCaverns", name = "Wailing Caverns", type = 1, cmap = 1, cx = -751.4f, cy = -2209.3f, pl = 10 },
                new { id = 249, dir = "OnyxiaLairInstance", name = "Onyxia's Lair", type = 2, cmap = 1, cx = -4753.2f, cy = -3752.4f, pl = 40 },
                new { id = 429, dir = "DireMaul", name = "Dire Maul", type = 1, cmap = 1, cx = -3908.1f, cy = 1130.4f, pl = 5 },
                new { id = 533, dir = "Stratholme Raid", name = "Naxxramas", type = 2, cmap = 0, cx = 0f, cy = 0f, pl = 40 },
            };
            var str = new MemoryStream(); str.WriteByte(0);
            var offs = new List<int[]>();
            foreach (var r in rows) { int a = (int)str.Length; var d = Encoding.UTF8.GetBytes(r.dir + "\0"); str.Write(d, 0, d.Length); int b = (int)str.Length; var n = Encoding.UTF8.GetBytes(r.name + "\0"); str.Write(n, 0, n.Length); offs.Add(new[] { a, b }); }
            var recs = new List<byte[]>();
            for (int i = 0; i < rows.Length; i++)
            {
                var r = rows[i]; var rec = new byte[S];
                void I(int at, int v) => Array.Copy(BitConverter.GetBytes(v), 0, rec, at, 4);
                void Fl(int at, float v) => Array.Copy(BitConverter.GetBytes(v), 0, rec, at, 4);
                I(0, rows.Length * S - i * S - 0 + offs[i][0]); I(4, rows.Length * S - i * S - 4 + offs[i][1]);
                Fl(24, r.cx); Fl(28, r.cy);
                I(32, 1); I(36, r.type); I(40, 0); I(44, 0); I(72, r.cmap); I(76, r.pl); I(88, 0);
                recs.Add(rec);
            }
            var sec = new W5Section { Records = recs.ToArray(), Strings = str.ToArray(), Ids = rows.Select(r => r.id).ToArray() };
            return BuildWdc5(MapTables.Map.TableHash, layout, false, S, fields.ToArray(), new[] { sec });
        }

        // LFGDungeons (34B02DE8): Name_lang 0 (string) ... TypeID 2 ... ContentTuningID 31 (33 stored fields; the ID is in the id list)
        static byte[] InsLfgTable(uint layout = 0x34B02DE8)
        {
            var fields = Enumerable.Range(0, 33).Select(F32).ToArray();
            const int S = 132;
            var rows = new[] { new { id = 1, name = "Wailing Caverns", tuning = 100 }, new { id = 3, name = "Dire Maul - East", tuning = 101 }, new { id = 5, name = "Dire Maul - West", tuning = 101 }, new { id = 7, name = "Orphan Hall", tuning = 999 }, new { id = 9, name = "Onyxia", tuning = 102 }, new { id = 11, name = "No Level Keep", tuning = 103 } };
            var str = new MemoryStream(); str.WriteByte(0); var offs = new List<int>();
            foreach (var r in rows) { offs.Add((int)str.Length); var d = Encoding.UTF8.GetBytes(r.name + "\0"); str.Write(d, 0, d.Length); }
            var recs = new List<byte[]>();
            for (int i = 0; i < rows.Length; i++)
            {
                var rec = new byte[S];
                Array.Copy(BitConverter.GetBytes(rows.Length * S - i * S + offs[i]), 0, rec, 0, 4);
                Array.Copy(BitConverter.GetBytes(rows[i].tuning), 0, rec, 124, 4);
                recs.Add(rec);
            }
            var sec = new W5Section { Records = recs.ToArray(), Strings = str.ToArray(), Ids = rows.Select(r => r.id).ToArray() };
            return BuildWdc5(MapTables.LfgDungeons.TableHash, layout, false, S, fields, new[] { sec });
        }

        // ContentTuning (A3E13004): ID (inline, in the id list), Flags 1, ExpansionID 2 ... MinLevelSquish 9, MaxLevelSquish 10 ... (19 fields)
        static byte[] InsTuningTable(uint layout = 0xA3E13004)
        {
            var fields = Enumerable.Range(0, 19).Select(F32).ToArray();
            const int S = 76;
            byte[] Rec(int min, int max) { var r = new byte[S]; Array.Copy(BitConverter.GetBytes(min), 0, r, 36, 4); Array.Copy(BitConverter.GetBytes(max), 0, r, 40, 4); return r; }
            var sec = new W5Section { Records = new[] { Rec(17, 17), Rec(54, 54), Rec(60, 60), Rec(0, 0) }, Ids = new[] { 100, 101, 102, 103 } };
            return BuildWdc5(MapTables.ContentTuning.TableHash, layout, false, S, fields, new[] { sec });
        }

        // the invented continent with entrances: Crypt of Bones (dungeon, level 17 from the game) in Greenvale, Deep Hall (raid) at the city's gate where the rectangles of Highkeep
        // and Greenvale overlap (the terrain's area decides), a dungeon with two wings on another continent, a battleground and an unused leftover that have none
        static MapSet InsSet(int deepHallArea = 78)
        {
            var s = AtlasTestMapSet();
            var crypt = s.Maps.First(m => m.Id == 99); crypt.CorpseMapId = 0; crypt.CorpseX = 300; crypt.CorpseY = 800;
            var hall = s.Maps.First(m => m.Id == 98); hall.CorpseMapId = 0; hall.CorpseX = 150; hall.CorpseY = 200; hall.CorpseAreaId = deepHallArea;
            var mine = s.Maps.First(m => m.Id == 97); mine.CorpseMapId = 0; mine.CorpseX = 50; mine.CorpseY = 50;       // "<unused> Mine": a leftover, no entrance
            var frost = s.Maps.First(m => m.Id == 96); frost.CorpseMapId = 0; frost.CorpseX = 60; frost.CorpseY = 60;   // a battleground
            s.Maps.Add(new GameMapInfo { Id = 94, Name = "Far Isle Cave", InstanceType = 1, MaxPlayers = 5, CorpseMapId = 5, CorpseX = 400, CorpseY = 300 });
            s.Lfg.Add(new LfgDungeonInfo { Id = 1, Name = "Crypt of Bones", MinLevel = 17, MaxLevel = 17 });
            s.Lfg.Add(new LfgDungeonInfo { Id = 2, Name = "Far Isle Cave - North", MinLevel = 30, MaxLevel = 30 });
            s.Lfg.Add(new LfgDungeonInfo { Id = 3, Name = "Far Isle Cave - South", MinLevel = 32, MaxLevel = 32 });
            s.HasLfg = true;
            return s;
        }

        async Task<string> InstancesTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            void Check(string what, bool cond, string detail = null) { SelfTestClock.Mark(what); notes.Add((cond ? "ok   " : "FAIL ") + what + (!cond && detail != null ? " [" + detail + "]" : "")); ok &= cond; Util.Log("instances test " + (cond ? "ok " : "FAIL ") + what.Substring(0, Math.Min(80, what.Length))); }
            string P(string n) => Path.Combine(dir, n);
            var work = Path.Combine(dir, "instances");
            var savedCache = MapData.CacheFile; var savedRoot = MapData.WowRoot; var savedEnabled = MapData.Enabled; var savedLodge = AtlasQuery.Context.Lodge;
            var savedLayers = settings.WorldMapLayers; var savedLayersV = settings.WorldMapLayersVersion;
            try
            {
                if (Directory.Exists(work)) Util.DeleteDir(work);
                Directory.CreateDirectory(work);
                Check("fixture: everything of this test is under the scratch folder (never the real game, never the real data folder)", Path.GetFullPath(work).StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase));
                AtlasQuery.Context.Lodge = () => null;

                // ---------------------------------------------------------------- the tables (synthetic WDC5, the real layouts)
                var raw = RawWith(Raw("UiMap", UiMapTable()), Raw("Map", InsMapTable()), Raw("LFGDungeons", InsLfgTable()), Raw("ContentTuning", InsTuningTable()));
                var set = MapTables.Parse(raw);
                var dm = set.Maps.FirstOrDefault(m => m.Id == 36); var wc = set.Maps.FirstOrDefault(m => m.Id == 43); var nx = set.Maps.FirstOrDefault(m => m.Id == 533); var tst = set.Maps.FirstOrDefault(m => m.Id == 13);
                Check("Map.db2: the ghost spot is read for every row - the continent (CorpseMapID, field 17) and the two floats of Corpse[2] (field 6): Deadmines on map 0 at (-11207.8, 1681.2), Wailing Caverns on map 1 at (-751.4, -2209.3), a row with none says -1 / 0, 0",
                    set.Ok && set.HasMaps && set.Maps.Count == 6 && dm != null && dm.CorpseMapId == 0 && Math.Abs(dm.CorpseX + 11207.8) < 0.01 && Math.Abs(dm.CorpseY - 1681.2) < 0.01 && wc != null && wc.CorpseMapId == 1 && Math.Abs(wc.CorpseX + 751.4) < 0.01 && Math.Abs(wc.CorpseY + 2209.3) < 0.01
                    && tst != null && tst.CorpseMapId == -1 && nx != null && nx.CorpseMapId == 0 && nx.CorpseX == 0 && nx.CorpseY == 0, string.Join(" | ", set.Maps.Select(m => m.Name + " " + m.CorpseMapId + " " + m.CorpseX + "," + m.CorpseY)));
                var lw = set.Lfg.FirstOrDefault(d => d.Name == "Wailing Caverns"); var lde = set.Lfg.FirstOrDefault(d => d.Name == "Dire Maul - East"); var lorph = set.Lfg.FirstOrDefault(d => d.Name == "Orphan Hall"); var lzero = set.Lfg.FirstOrDefault(d => d.Name == "No Level Keep");
                Check("LFGDungeons + ContentTuning: six rows with their names; the suggested level comes from the row's ContentTuningID (Wailing Caverns 17, Dire Maul 54, Onyxia 60); a tuning row that is missing or says 0 gives no level",
                    set.HasLfg && set.Lfg.Count == 6 && lw != null && lw.MinLevel == 17 && lw.MaxLevel == 17 && lde != null && lde.MinLevel == 54 && set.Lfg.First(d => d.Name == "Onyxia").MinLevel == 60 && lorph != null && lorph.MinLevel == 0 && lzero != null && lzero.MinLevel == 0, string.Join(" | ", set.Lfg.Select(d => d.Name + " " + d.MinLevel)));
                var unkLfg = MapTables.Parse(RawWith(Raw("UiMap", UiMapTable()), Raw("Map", InsMapTable()), Raw("LFGDungeons", InsLfgTable(0xDEADBEEF)), Raw("ContentTuning", InsTuningTable())));
                var unkMap = MapTables.Parse(RawWith(Raw("UiMap", UiMapTable()), Raw("Map", InsMapTable(0xDEADBEEF)), Raw("LFGDungeons", InsLfgTable()), Raw("ContentTuning", InsTuningTable())));
                var noTables = MapTables.Parse(RawWith(Raw("UiMap", UiMapTable())));
                var brokenLfg = MapTables.Parse(RawWith(Raw("UiMap", UiMapTable()), Raw("LFGDungeons", new byte[] { 1, 2, 3 }), Raw("ContentTuning", InsTuningTable())));
                Check("layouts: an unknown LFGDungeons layout leaves the dungeons without levels (and says why), the entrances still come from Map.db2; an unknown Map layout leaves no Map rows; tables the file list does not mention or a damaged file change nothing and break nothing",
                    unkLfg.Ok && !unkLfg.HasLfg && unkLfg.Lfg.Count == 0 && unkLfg.Notes.Any(n => n.Contains("LFGDungeons")) && unkLfg.Maps.Count == 6 && unkLfg.Maps.First(m => m.Id == 36).CorpseMapId == 0
                    && unkMap.Ok && !unkMap.HasMaps && unkMap.Maps.Count == 0 && unkMap.HasLfg && noTables.Ok && !noTables.HasLfg && noTables.Lfg.Count == 0 && brokenLfg.Ok && !brokenLfg.HasLfg, string.Join(" ; ", unkLfg.Notes.Concat(unkMap.Notes)));

                // ---------------------------------------------------------------- entrance selection (nothing guessed)
                GameMapInfo G(int cm, double x, double y) => new GameMapInfo { Id = 1, CorpseMapId = cm, CorpseX = x, CorpseY = y };
                var e1 = MapIndex.EntranceOf(G(1, 1816.8, -4423.1));
                Check("entrance choice: a continent and a spot make an entrance; no continent (-1), the spot (0, 0), a spot outside the world, NaN and a missing row give none - an instance without a ghost spot has no entrance rather than a guessed one",
                    e1 != null && e1.MapId == 1 && Math.Abs(e1.X - 1816.8) < 1e-9 && e1.Source == MapIndex.EntranceSourceName && MapIndex.EntranceOf(G(-1, 100, 100)) == null && MapIndex.EntranceOf(G(0, 0, 0)) == null && MapIndex.EntranceOf(G(0, 20000, 5)) == null
                    && MapIndex.EntranceOf(G(0, double.NaN, 5)) == null && MapIndex.EntranceOf(G(0, 5, double.PositiveInfinity)) == null && MapIndex.EntranceOf(null) == null && MapIndex.EntranceOf(G(0, 0.3, 0.3)) == null);
                var same = new[] { new[] { "Stormwind Stockade", "Stormwind Stockades" }, new[] { "Zul'Farrak", "Zul'Farak" }, new[] { "Onyxia's Lair", "Onyxia" }, new[] { "Blackrock Spire", "Lower Blackrock Spire" }, new[] { "Dire Maul", "Dire Maul - East" }, new[] { "Scarlet Monastery", "Scarlet Monastery" } };
                var diff = new[] { new[] { "Blackrock Depths", "Blackrock Spire" }, new[] { "Molten Core", "Blackwing Lair" }, new[] { "Naxxramas", "Stratholme" }, new[] { "Ragefire Chasm", "Dire Maul - East" } };
                Check("dungeon names: the game spells the same dungeon differently in its two tables (an s more, a letter off, one inside the other); different dungeons are never joined, and a short or empty name never matches",
                    same.All(n => MapIndex.SameDungeonName(n[0], n[1])) && diff.All(n => !MapIndex.SameDungeonName(n[0], n[1])) && !MapIndex.SameDungeonName("Zul", "Zul'Gurub") && !MapIndex.SameDungeonName("", "Anything long") && !MapIndex.SameDungeonName(null, null));

                var idx0 = new MapIndex(set);
                var iDm = idx0.Instance(36); var iWc = idx0.Instance(43); var iOn = idx0.Instance(249); var iDM = idx0.Instance(429); var iNx = idx0.Instance(533); var iTest = idx0.Instance(13);
                Check("instances: Deadmines, Wailing Caverns, Onyxia's Lair and Dire Maul get their entrance; Naxxramas (spot 0, 0) and the test dungeon have none; the test row is a placeholder",
                    iDm?.Entrance != null && iDm.Entrance.MapId == 0 && iWc?.Entrance != null && iWc.Entrance.MapId == 1 && iOn?.Entrance != null && iDM?.Entrance != null && iNx != null && iNx.Entrance == null && iTest != null && iTest.Placeholder && iTest.Entrance == null);
                Check("levels: the level the game suggests is joined by name - Wailing Caverns 17, Onyxia's Lair 60 (the table calls it \"Onyxia\"), Dire Maul 54 with its two wings listed, Deadmines (no row) none; a row whose tuning is missing or 0 does not count",
                    iWc.MinLevel == 17 && iWc.MaxLevel == 17 && iWc.Parts.Count == 0 && iOn.MinLevel == 60 && iDM.MinLevel == 54 && iDM.MaxLevel == 54 && iDM.Parts.SequenceEqual(new[] { "Dire Maul - East", "Dire Maul - West" }) && iDm.MinLevel == 0 && iNx.MinLevel == 0);

                // ---------------------------------------------------------------- the zone an entrance is in (exact when the terrain's area was read)
                var sA = InsSet(78); var sB = InsSet(0);
                var idxA = new MapIndex(sA); var idxB = new MapIndex(sB);
                var hallA = idxA.Instance(98).Entrance; var hallB = idxB.Instance(98).Entrance; var cryptA = idxA.Instance(99).Entrance;
                Check("zone of an entrance: with the terrain's area (Greenvale's area 78) the entrance is on Greenvale although the city's smaller rectangle also holds the spot; without the area the most specific rectangle (Highkeep) answers, as the rest of the maps do; the label says which",
                    hallA.UiMapId == 11 && hallA.ZoneName == "Greenvale" && hallA.AreaId == 78 && hallA.Source.Contains("terrain") && hallB.UiMapId == 13 && hallB.ZoneName == "Highkeep" && !hallB.Source.Contains("terrain") && hallA.Fx > 0 && hallA.Fx < 1 && hallA.Fy > 0 && hallA.Fy < 1, hallA.UiMapId + " " + hallB.UiMapId);
                Check("zone of an entrance: Crypt of Bones at (300, 800) is 60 % / 70 % on Greenvale's map (x north: (1000 - 300) / 1000 from the top, y west: (2000 - 800) / 2000 from the left); the dungeon on another continent has no map there, so no zone, but keeps its spot",
                    cryptA.UiMapId == 11 && Math.Abs(cryptA.Fx - 0.6) < 1e-6 && Math.Abs(cryptA.Fy - 0.7) < 1e-6 && idxA.Instance(94).Entrance != null && idxA.Instance(94).Entrance.UiMapId == 0 && idxA.Instance(94).Entrance.MapId == 5);

                // ---------------------------------------------------------------- the zone a position is in: the terrain's area beats the rectangles (a city's box must not win outside the city)
                {
                    var savedProbe = MapIndex.AreaAt; var savedSrc = AreaProbe.TileSourceForTest; var savedDir = AreaProbe.CacheDirOverride;
                    try
                    {
                        MapData.SetForTest(idxA);
                        AreaProbe.ResetForTest(); AreaProbe.CacheDirOverride = Path.Combine(work, "areas"); MapIndex.AreaAt = null;
                        var rectOnly = idxA.WorldToMap(0, 150, 200);               // inside Highkeep's small box and Greenvale's large one
                        int answer = 78;
                        AreaProbe.TileSourceForTest = (m, c, r) => m == 0 && c == 31 && r == 31 ? Enumerable.Repeat(answer, 256).ToArray() : null;
                        MapIndex.AreaAt = AreaProbe.AreaAt;
                        var byArea = idxA.WorldToMap(0, 150, 200);
                        answer = 79; AreaProbe.ResetForTest(); var byCity = idxA.WorldToMap(0, 150, 200);
                        answer = 0; AreaProbe.ResetForTest(); var byNothing = idxA.WorldToMap(0, 150, 200);
                        answer = 4242; AreaProbe.ResetForTest(); var byUnknownArea = idxA.WorldToMap(0, 150, 200);
                        var noTile = idxA.WorldToMap(0, 500, 900);                  // a tile the source does not have: the rectangles answer
                        int reads0 = AreaProbe.ReadsForTest; idxA.WorldToMap(0, 500, 900); idxA.WorldToMap(0, 500, 900);
                        Check("zone rule: without the terrain the smaller city box wins (Highkeep); with the terrain's area it is the zone the chunk is in - Greenvale for area 78 although Highkeep's box also holds the spot, Highkeep for its own area 79; an unknown area, no area and a tile the game does not have fall back to the rectangles; a tile is read once",
                            rectOnly?.UiMapId == 13 && byArea?.UiMapId == 11 && byCity?.UiMapId == 13 && byNothing?.UiMapId == 13 && byUnknownArea?.UiMapId == 13 && noTile?.UiMapId == 11 && AreaProbe.ReadsForTest == reads0, rectOnly + " " + byArea + " " + byCity + " " + byNothing + " " + noTile);
                        var set2 = InsSet(0); var idxP = new MapIndex(set2);
                        MapData.SetForTest(idxP);
                        answer = 78; AreaProbe.ResetForTest();
                        var layerFm = WorldMapLayers.Instances(0);
                        Check("zone rule: the layers and pages that ask WorldToMap (flight master hover text, instance zones, spawn markers) get the same answer, not their own", idxP.WorldToMap(0, 150, 200)?.UiMapId == 11 && layerFm.Count == 2);
                        AreaProbe.Flush();
                        Check("zone rule: what was read is saved (1 KB a tile) next to the map cache and read back by a new session without touching the game", Directory.Exists(Path.Combine(work, "areas")) && Directory.GetFiles(Path.Combine(work, "areas"), "areas-*-0.bin").Length == 1 && ReadBackAreas());
                        bool ReadBackAreas()
                        {
                            AreaProbe.ResetForTest(); int src = 0;
                            AreaProbe.TileSourceForTest = (m, c, r) => { src++; return null; };
                            int a = AreaProbe.AreaAt(0, 150, 200);
                            return a == 78 && src == 0;
                        }
                        MapData.SetForTest(idxA);
                    }
                    finally { MapIndex.AreaAt = savedProbe; AreaProbe.TileSourceForTest = savedSrc; AreaProbe.CacheDirOverride = savedDir; AreaProbe.ResetForTest(); }
                }

                // ---------------------------------------------------------------- the saved index keeps all of it
                var idxFile = Path.Combine(work, "mapdata.bin");
                bool wrote = MapIndexFile.TryWrite(idxFile, "wow_classic_beta|1.60.1.70338|abc", sA);
                var back = MapIndexFile.TryRead(idxFile);
                var hb = back?.Set.Maps.FirstOrDefault(m => m.Id == 98);
                Check("index file: the ghost spots, the terrain areas and the dungeon levels survive the saved copy (format " + MapData.FormatVersion + "), and the entrances come out the same from the copy",
                    wrote && back != null && back.Set.HasLfg && back.Set.Lfg.Count == 3 && back.Set.Lfg[2].Name == "Far Isle Cave - South" && back.Set.Lfg[0].MinLevel == 17 && hb != null && hb.CorpseMapId == 0 && hb.CorpseX == 150 && hb.CorpseAreaId == 78
                    && new MapIndex(back.Set).Instance(98).Entrance.ZoneName == "Greenvale" && new MapIndex(back.Set).Instance(94).Entrance.MapId == 5);

                // ---------------------------------------------------------------- the world map layer
                MapData.SetForTest(idxA);
                var lr = WorldMapLayers.Instances(0); var lr5 = WorldMapLayers.Instances(5); var lr9 = WorldMapLayers.Instances(9);
                var lj = Json.Obj(lr.Json);
                var itemList = lj == null ? new List<Dictionary<string, object>>() : lj.List("items").OfType<Dictionary<string, object>>().ToList();
                var crypt = itemList.FirstOrDefault(i => (string)i["name"] == "Crypt of Bones"); var hall = itemList.FirstOrDefault(i => (string)i["name"] == "Deep Hall");
                Check("layer: Dungeons & raids on the continent holds the two entrances (not the unused leftover, the battleground or the dungeon on another continent), kind \"markers\", every item with a position on the plane, a link to its instance page, a hover text with kind / size / level and the zone",
                    lr.Count == 2 && lj != null && (string)lj["id"] == "instances" && (string)lj["kind"] == "markers" && crypt != null && hall != null && (string)crypt["link"] == "instance:99" && (string)hall["link"] == "instance:98"
                    && (string)crypt["tip"] == "Dungeon · 5 players · level 17" && ((string)crypt["extra"]).StartsWith("Greenvale · 60 / 70") && (double)crypt["lat"] < 0 && (double)crypt["lng"] > 0 && (string)hall["tip"] == "Raid · 40 players" && ((string)hall["extra"]).StartsWith("Greenvale"), lr.Json);
                Check("layer: a door icon (dun) and group \"instances.dungeons\" for a dungeon, a skull badge (raid) and \"instances.raids\" for a raid; the label is the name and the suggested level (\"Crypt of Bones · 17\"), just the name when the game gives none",
                    crypt != null && hall != null && (string)crypt["ic"] == "dun" && (string)crypt["g"] == "instances.dungeons" && (string)hall["ic"] == "raid" && (string)hall["g"] == "instances.raids" && (string)crypt["lab"] == "Crypt of Bones · 17" && (string)hall["lab"] == "Deep Hall");
                WorldMapMath.WorldToLatLng(300, 800, out var clat, out var clng);
                Check("layer: the plane position is the entrance's world position (300, 800) through the same math as every other layer", crypt != null && Math.Abs((double)crypt["lat"] - clat) < 0.002 && Math.Abs((double)crypt["lng"] - clng) < 0.002);
                var lj5 = Json.Obj(lr5.Json);
                Check("layer: another continent lists its own entrance (Far Isle Cave: two wings, levels 30 and 32 joined); a continent without entrances says so (an empty list, a note)",
                    lr5.Count == 1 && lj5 != null && lj5.List("items").Count == 1 && ((Dictionary<string, object>)lj5.List("items")[0])["lab"].ToString() == "Far Isle Cave · 30-32" && lr9.Count == 0 && lr9.Note.Contains("No dungeon or raid"), lr5.Json + " | " + lr9.Note);
                MapData.SetForTest(null, null, MapData.State.Disabled);
                var lrOff = WorldMapLayers.Instances(0);
                MapData.SetForTest(new MapIndex(AtlasTestMapSet()));
                var lrNone = WorldMapLayers.Instances(0);
                Check("layer: with game data off it says the maps are not ready; a game whose Map table has no ghost spots says the game files give none", lrOff.Json.Length == 0 && lrOff.Note.Contains("not ready") && lrNone.Count == 0 && lrNone.Note.Contains("give no entrance"), lrOff.Note + " | " + lrNone.Note);
                MapData.SetForTest(idxA);
                Check("layer: WorldMapLayers.Build(\"instances\") is the same builder; a kind row on its own is no layer", WorldMapLayers.Build("instances", 0).Count == 2 && WorldMapLayers.Build("instances.raids", 0).Json.Length == 0);

                // the catalog, the defaults, the protocol
                var cat = WorldMapLayers.Catalog();
                var catIds = cat.Select(c => (string)c["id"]).ToList();
                var insRow = cat.FirstOrDefault(c => (string)c["id"] == "instances");
                var kidRows = insRow != null && insRow.ContainsKey("children") ? (List<Dictionary<string, object>>)insRow["children"] : new List<Dictionary<string, object>>();
                string Why(string json) { WorldMapProtocol.Parse(json, out var why); return why; }
                var layersMsg = WorldMapProtocol.Parse("{\"t\":\"layers\",\"on\":[\"zones\",\"instances\",\"instances.raids\",\"instances.dungeons\"]}", out _);
                var needMsg = WorldMapProtocol.Parse("{\"t\":\"need\",\"layer\":\"instances\"}", out _);
                Check("catalog: \"Dungeons & raids\" is a layer with the child rows Dungeons and Raids (distinct colours), after the flight routes; it is on by default (with both kinds); every id is one the protocol accepts",
                    insRow != null && (string)insRow["title"] == "Dungeons & raids" && kidRows.Select(k => (string)k["id"]).SequenceEqual(new[] { "instances.dungeons", "instances.raids" }) && catIds.IndexOf("instances") == catIds.IndexOf("routes") + 1
                    && WorldMapLayers.DefaultOn.Contains("instances") && WorldMapLayers.DefaultOn.Contains("instances.dungeons") && WorldMapLayers.DefaultOn.Contains("instances.raids") && WorldMapLayers.DefaultOn.All(WorldMapIds.IsLayerId)
                    && kidRows.Select(k => (string)k["color"]).Distinct().Count() == 2);
                Check("protocol: \"layers\" with the new ids and \"need\" for \"instances\" are accepted; a kind row is never asked for on its own, and kinds that do not exist (battlegrounds), a glued or empty suffix are refused",
                    layersMsg != null && layersMsg.On.Contains("instances.raids") && needMsg != null && needMsg.Layer == "instances" && Why("{\"t\":\"need\",\"layer\":\"instances.raids\"}") != null && Why("{\"t\":\"layers\",\"on\":[\"instances.battlegrounds\"]}") != null
                    && Why("{\"t\":\"layers\",\"on\":[\"instances.\"]}") != null && Why("{\"t\":\"layers\",\"on\":[\"instancesx\"]}") != null && WorldMapProtocol.IsOpenId("instance:98") && !WorldMapProtocol.IsOpenId("instance:x"));
                var mig0 = WorldMapLayers.WithNewDefaults(new List<string> { "zones", "fm" }, 0); var mig2 = WorldMapLayers.WithNewDefaults(new List<string> { "zones" }, WorldMapLayers.LayersVersion);
                Check("settings: a layer list saved before this catalog gets Dungeons & raids switched on once (with both kinds); a list already at this version, and the defaults (null), are left alone",
                    mig0.SequenceEqual(new[] { "zones", "fm", "instances", "instances.dungeons", "instances.raids", "fm.horde", "fm.alliance", "fm.neutral", "seen", "seen.met", "seen.rares", "trailevents" }) && mig2.SequenceEqual(new[] { "zones" }) && WorldMapLayers.WithNewDefaults(null, 0) == null && new Settings().WorldMapLayersVersion == 0 && WorldMapLayers.LayersVersion >= 2);

                // ---------------------------------------------------------------- Atlas pages
                ClassicAtlas.Use(AtlasTestClassic());
                var ip = AtlasQuery.Build("instance:99"); var ip2 = AtlasQuery.Build("instance:98"); var ipFar = AtlasQuery.Build("instance:94"); var ipNone = AtlasQuery.Build("instance:97");
                var en = ip.Entrance;
                Check("instance page: Crypt of Bones has an Entrance block - Greenvale at 60 / 70, the continent position, the source - and a fact \"Entrance: Greenvale · 60 / 70\" that links to the zone; the suggested level 17 is a Forever fact; the unused leftover has no page",
                    en != null && en.UiMapId == 11 && en.ZoneName == "Greenvale" && en.Coords == "60 / 70" && en.MapId == 0 && Math.Abs(en.X - 300) < 1e-6 && en.Source.Length > 0 && ip.Facts.Any(f => f.Label == "Entrance" && f.Value == "Greenvale · 60 / 70" && f.Link == "zone:11" && f.Source == AtlasLabel.Forever)
                    && ip.Facts.Any(f => f.Label == "Suggested level" && f.Value == "17" && f.Source == AtlasLabel.Forever) && ip.Subtitle.Contains("level 17") && ip.Subtitle.Contains("entrance in Greenvale") && ipNone.NotFound);
                Check("instance page: \"Show on world map\" is a worldmap: id on the continent ringed at the entrance (when the world map has this continent), \"Navigate here\" is the /etk way command of the zone map that shows it with the instance's name; an entrance no zone map shows has a position and no zone",
                    en != null && en.Command == "/etk way 11 60.00 70.00 Crypt of Bones" && (en.WorldMapId == null || (en.WorldMapId.StartsWith("worldmap:") && en.WorldMapId.Contains("p=1"))) && ipFar.Entrance != null && ipFar.Entrance.UiMapId == 0 && ipFar.Entrance.ZoneName == "", en?.Command + " | " + en?.WorldMapId);
                Check("instance page: an instance with several wings names them (Far Isle Cave: North, South at levels 30-32), and the Classic baseline's creature levels stay apart from the game's suggested level",
                    ipFar.Facts.Any(f => f.Label == "Wings" && f.Value == "Far Isle Cave - North, Far Isle Cave - South") && ipFar.Facts.Any(f => f.Label == "Suggested level" && f.Value == "30-32") && ip.Facts.Any(f => f.Label == "Creature levels" && f.Source == AtlasLabel.Classic) && ip2.Entrance != null && ip2.Facts.All(f => f.Label != "Suggested level"));
                var zp = AtlasQuery.Build("zone:11"); var zsec = zp.Section("instances"); var zp13 = AtlasQuery.Build("zone:13"); var zp12 = AtlasQuery.Build("zone:12");
                Check("zone page: Greenvale lists the dungeons and raids whose entrance is in it (Crypt of Bones and, by the terrain's area, Deep Hall) right after the places in it, each linking to its page with kind / size / level and the entrance position, and shows door markers on its map; Highkeep and Redmoor list none",
                    zsec != null && zsec.Rows.Count == 2 && zsec.Rows[0].Link == "instance:99" && zsec.Rows[0].Detail == "Dungeon · 5 players · level 17" && zsec.Rows[0].Extra == "Entrance 60 / 70" && zsec.Rows[1].Link == "instance:98" && zp.Sections.FindIndex(s => s.Key == "instances") == zp.Sections.FindIndex(s => s.Key == "children") + 1
                    && zp.Map.Zones.Any(z => z.UiMapId == 11 && z.Markers.Count(k => k.Kind == MarkerKind.Entrance && k.LinkId == "instance:99") == 1) && zp13.Section("instances") == null && zp12.Section("instances") == null, zsec == null ? "none" : string.Join(" | ", zsec.Rows.Select(r => r.Title + ":" + r.Detail + ":" + r.Extra)) + " idx " + zp.Sections.FindIndex(s => s.Key == "instances") + "/" + zp.Sections.FindIndex(s => s.Key == "children") + " markers " + string.Join(",", zp.Map.Zones.Select(z => z.UiMapId + ":" + string.Join("+", z.Markers.Select(k => k.Kind + "/" + k.LinkId)))) + " 13:" + (zp13.Section("instances") != null) + " 12:" + (zp12.Section("instances") != null));
                var br = AtlasBrowse.Run("browse:instances"); var brZ = AtlasBrowse.Run("browse:instances?zone=11"); var brS = AtlasBrowse.Run("browse:instances?sort=zone"); var brC = br.Controls;
                var rCrypt = br.Rows.FirstOrDefault(r => r.Title == "Crypt of Bones"); var rFar = br.Rows.FirstOrDefault(r => r.Title == "Far Isle Cave");
                Check("browse: the list shows the zone of each entrance (Extra / Zone column) and the game's level (\"Levels 17\", 30-32), sorted by level with the unknown last; a zone filter and a zone sort exist",
                    rCrypt != null && rCrypt.Zone == "Greenvale" && rCrypt.Extra == "Greenvale" && rCrypt.Detail.Contains("Levels 17") && rCrypt.Detail.Contains("entrance in Greenvale") && rFar != null && rFar.Detail.Contains("Levels 30-32") && br.Rows.First().Title == "Crypt of Bones" && br.Sort == "level"
                    && brZ.Total == 2 && brZ.Rows.All(r => r.Zone == "Greenvale") && brC != null && brC.Zone && brC.Zones.Any(z => z.Text == "Greenvale") && brC.Sorts.Any(s2 => s2.Value == "zone") && brS.Sort == "zone" && brS.Rows.Take(2).All(r => r.Zone == "Greenvale"), string.Join(" | ", br.Rows.Select(r => r.Title + ":" + r.Detail + ":" + r.Zone)));
                ClassicAtlas.Use(null);
                var brNo = AtlasBrowse.Run("browse:instances");
                Check("browse: without the Classic baseline the game's levels stay and a note says what the baseline would add for the instances with no level", brNo.Rows.Any(r => r.Title == "Crypt of Bones" && r.Detail.Contains("Levels 17")) && brNo.Notes.Any(n => n.Contains("no level in your game files")), string.Join(" | ", brNo.Notes));
                var levelsA = AtlasInstances.Levels(idxA.Instance(98), out _, out _, out _);
                Check("levels helper: the game's level first; with neither the game's nor enough baseline creatures there is no number at all", AtlasInstances.Levels(idxA.Instance(99), out var lo99, out _, out var src99) && lo99 == 17 && src99 == AtlasLabel.Forever && !levelsA && AtlasInstances.LevelText(idxA.Instance(98)) == "");

                // ---------------------------------------------------------------- the instance page in the window
                var clipSeen = new List<string>();
                ShowTab("atlas"); AtlasView.History.Clear();
                AtlasView.ClipboardSeamForTest = t => clipSeen.Add(t);
                await AtlasView.Open("instance:99"); await AtlasView.WaitForTest(); await Task.Delay(500); UpdateLayout();
                var texts = new List<string>();
                void Walk(System.Windows.DependencyObject d) { if (d is TextBlock tb && !string.IsNullOrEmpty(tb.Text)) texts.Add(tb.Text); for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i++) Walk(System.Windows.Media.VisualTreeHelper.GetChild(d, i)); }
                Walk(AtlasView.BodyForTest);
                Check("page in the window: the Entrance card shows the zone as a button (Greenvale), the position (60 / 70), the source label, a map of the zone with the door marker, and the button \"Navigate here\" (and \"Show on world map\" when the world map has the continent)",
                    texts.Contains("Entrance") && AtlasView.EntranceButtonsForTest.Any(b => (string)b.Content == "Greenvale") && AtlasView.EntranceTextsForTest.Any(t => t.Text == "60 / 70") && AtlasView.EntranceMapsForTest.Count == 1 && AtlasView.EntranceMapsForTest[0].UiMapId == 11
                    && AtlasView.EntranceButtonsForTest.Any(b => (string)b.Content == "Navigate here"), string.Join(",", AtlasView.EntranceButtonsForTest.Select(b => b.Content)) + " maps " + AtlasView.EntranceMapsForTest.Count);
                Check("page in the window: the instance opens on its Overview - the hero names it with chips from its facts (type, players, suggested level) - and the Entrance card is part of that Overview",
                    AtlasView.TabNowForTest == "overview" && AtlasView.HeroNameForTest == AtlasView.Model.Title && AtlasView.HeroChipsForTest.Count > 0 && AtlasView.HeroChipsForTest.Any(c => c.StartsWith("Level ")) && AtlasView.TabBuiltForTest("overview"), string.Join(" | ", AtlasView.HeroChipsForTest));
                var nav = AtlasView.EntranceButtonsForTest.First(b => (string)b.Content == "Navigate here");
                nav.RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
                Check("page in the window: \"Navigate here\" copies /etk way 11 60.00 70.00 Crypt of Bones and says so beside the page link", clipSeen.Count == 1 && clipSeen[0] == "/etk way 11 60.00 70.00 Crypt of Bones" && AtlasView.LastNavigateCopiedForTest == clipSeen[0] && AtlasView.CopiedShownForTest, string.Join("|", clipSeen));
                var zbtn = AtlasView.EntranceButtonsForTest.First(b => (string)b.Content == "Greenvale");
                zbtn.RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
                bool atZone = await WaitFor(() => AtlasView.CurrentId == "zone:11", 10000);
                await AtlasView.WaitForTest(); await Task.Delay(300);
                Check("page in the window: the zone button opens the zone page, which lists the instances and has no Entrance card of its own", atZone && AtlasView.Model.Section("instances") != null && AtlasView.EntranceMapsForTest.Count == 0);
                await AtlasView.Open("instance:94"); await AtlasView.WaitForTest(); await Task.Delay(300); UpdateLayout();
                Check("page in the window: an entrance on a continent no zone map shows still has its card (position in yards, no zone, no map) and does not break the page", AtlasView.EntranceMapsForTest.Count == 0 && AtlasView.EntranceTextsForTest.Any(t => t.Text.StartsWith("x 400")) && AtlasView.Model.Entrance != null);
                await AtlasView.Open("instance:99"); await AtlasView.WaitForTest(); await Task.Delay(500); UpdateLayout();
                Snapshot(P("instances-page-synthetic.png"));
                AtlasView.ClipboardSeamForTest = null;

                // the real game files run in their own section: "instancesreal" (MainWindow.InstancesTest.Real.cs)
            }
            catch (Exception e) { Check("instances test crashed", false, e.ToString()); }
            finally
            {
                try { AtlasView.ClipboardSeamForTest = null; } catch (Exception) { }
                ClassicAtlas.Use(null);
                MapData.CacheFile = savedCache; MapData.WowRoot = savedRoot; MapData.Enabled = savedEnabled; AtlasQuery.Context.Lodge = savedLodge;
                settings.WorldMapLayers = savedLayers; settings.WorldMapLayersVersion = savedLayersV;
            }
            return (ok ? "instances: ok" : "instances: FAILED") + "\r\n" + string.Join("\r\n", notes);
        }
    }
}
