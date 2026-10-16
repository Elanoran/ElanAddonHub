using System;
using System.Collections.Generic;
using System.Linq;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.WorldMap;

namespace ElansAddonHub.Services.Maps
{
    // A second, approximate source for the way into an instance whose Map row names no ghost spot (the event and newer maps of this client: Demon Fall Canyon, The Crystal
    // Vale, Shadow Hold ...). What the client tables can say, checked on build 1.60.1.70338:
    //   JournalInstanceEntrance (the modern dungeon-entrance pins: Position, continent MapID, JournalInstanceID; layout 874E7CC2) and JournalInstance (6C5ED7F2) are in the
    //     install with exactly those layouts but EMPTY (no rows, nothing key-locked, nothing in the hotfix cache): this client has no Encounter Journal. Not used.
    //   AreaPOI (FileDataID 1000630, layout A8930CBB per WoWDBDefs: Name_lang 0, Description_lang 1, ID 2, Pos[3] 3, PortLocID 4, PlayerConditionID 5, ..., ContinentID 13,
    //     AreaID 14): 372 map points; one carries an instance's exact name ("Demon Fall Canyon", in Ashenvale) - used first, labelled as a map point of that name.
    //   AreaTable: a continent area with the instance's exact name (with or without "The": The Crystal Vale and The Scarab Dais in Silithus, Shadow Hold in Felwood, The Tainted
    //     Scar in the Blasted Lands, Ruins of Lordaeron in Tirisfal Glades, Excavation Site: Wetlands, Manor Mistmantle in Duskwood, The Searing Basin in Searing Gorge) -
    //     the middle of the terrain chunks that carry that area id (read from the continent's ADTs inside its zone's map rectangle), labelled as the area's middle, not the door.
    // Once per game build, as part of the saved index; any failure leaves the instance without an entrance (nothing is guessed beyond the label).
    public static class NamedEntrances
    {
        public const int AreaPoiFileDataId = 1000630;
        public const uint AreaPoiTableHash = 0xDF3F054A, AreaPoiLayout = 0xA8930CBB;
        public const int PoiName = 0, PoiPos = 3, PoiContinent = 13, PoiArea = 14;
        public const string KindPoi = "poi", KindArea = "area";

        public sealed class Poi { public int Id, ContinentId, AreaId; public string Name = ""; public double X, Y; }

        static string Key(string s)
        {
            s = (s ?? "").Trim();
            if (s.StartsWith("The ", StringComparison.OrdinalIgnoreCase)) s = s.Substring(4);
            return s.ToLowerInvariant();
        }

        public static List<Poi> ParsePois(byte[] file, List<string> notes)
        {
            var res = new List<Poi>();
            if (file == null) return res;
            var w = Wdc5.Parse(file);
            if (w.TableHash != AreaPoiTableHash || w.Sparse) { notes?.Add("AreaPOI is not the table this version expects"); return res; }
            if (w.LayoutHash != AreaPoiLayout) { notes?.Add("game updated: AreaPOI layout " + w.LayoutHash.ToString("X8") + " is not known to this Outpost yet"); return res; }
            for (int i = 0; i < w.Count; i++)
                res.Add(new Poi
                {
                    Id = w.IdAt(i), Name = w.String(i, PoiName) ?? "", X = Wdc5.AsFloat(w.Element(i, PoiPos, 0, 3)), Y = Wdc5.AsFloat(w.Element(i, PoiPos, 1, 3)),
                    ContinentId = (int)w.Field(i, PoiContinent), AreaId = (int)w.Field(i, PoiArea),
                });
            return res;
        }

        // the instances that need it: dungeons and raids with no ghost spot
        public static IEnumerable<GameMapInfo> Wanting(MapSet set) => set.Maps.Where(m => (m.InstanceType == 1 || m.InstanceType == 2) && MapIndex.CorpseEntrance(m) == null && !MapIndex.IsPlaceholderName(m.Name));

        // the continents a door can stand on: maps with no instance type that have a world table
        static HashSet<int> Continents(MapSet set) => new HashSet<int>(set.Maps.Where(m => m.InstanceType == 0 && m.WdtFileDataId > 0).Select(m => m.Id));

        // step 1, no files: a map point of the same name
        public static void FromPois(MapSet set, IList<Poi> pois)
        {
            var conts = Continents(set);
            foreach (var m in Wanting(set).ToList())
            {
                var p = pois.FirstOrDefault(q => conts.Contains(q.ContinentId) && Key(q.Name) == Key(m.Name) && Math.Abs(q.X) + Math.Abs(q.Y) > 1);
                if (p == null) continue;
                m.AltEntranceMapId = p.ContinentId; m.AltX = p.X; m.AltY = p.Y; m.AltAreaId = p.AreaId; m.AltKind = KindPoi;
            }
        }

        // the area of the same name on a continent (and the zone it is in); null = none
        public static AreaInfo AreaNamed(MapSet set, GameMapInfo m)
        {
            var conts = Continents(set);
            return set.Areas.Where(a => conts.Contains(a.ContinentId) && Key(a.Name) == Key(m.Name)).OrderBy(a => a.Id).FirstOrDefault();
        }

        // step 2, a few ADTs: the middle of the chunks of the area of the same name
        public static void FromAreas(CascStorage casc, MapSet set)
        {
            try
            {
                var areas = set.Areas.GroupBy(a => a.Id).ToDictionary(g => g.Key, g => g.First());
                // several instances lie on the same continent and next to each other: each WDT and terrain tile is read once for all of them (same answers;
                // only the chunks' area id and corner are kept of a tile)
                var wdts = new Dictionary<int, WdtFile>();
                var adts = new Dictionary<int, Tuple<int[], float[]>>();
                byte[] tileBuf = null;                         // one buffer for every tile read here
                foreach (var m in Wanting(set).Where(x => x.AltEntranceMapId < 0).ToList())
                {
                    var a = AreaNamed(set, m);
                    if (a == null) continue;
                    // the zone: the area itself or the first ancestor that has a map rectangle on this continent
                    var rects = new List<UiAssignment>();
                    for (var z = a; z != null && rects.Count == 0; z = z.ParentId > 0 && areas.TryGetValue(z.ParentId, out var pz) ? pz : null)
                        rects = set.Assignments.Where(r => r.MapId == a.ContinentId && r.AreaId == z.Id).ToList();
                    if (rects.Count == 0) continue;
                    var cont = set.Maps.FirstOrDefault(x => x.Id == a.ContinentId);
                    if (cont == null || cont.WdtFileDataId <= 0) continue;
                    if (!wdts.TryGetValue(cont.WdtFileDataId, out var wdt))
                    {
                        var wk = casc.ResolveFileIds(new HashSet<int> { cont.WdtFileDataId });
                        wdt = wk.TryGetValue(cont.WdtFileDataId, out var wck) ? WdtFile.Parse(casc.ReadByContentKey(wck)) : null;
                        wdts[cont.WdtFileDataId] = wdt;
                    }
                    if (wdt == null || !wdt.Ok) continue;
                    var tiles = new HashSet<int>();
                    foreach (var r in rects)
                    {
                        WorldMapMath.TileOf(r.MaxX, r.MaxY, out var c0, out var r0); WorldMapMath.TileOf(r.MinX, r.MinY, out var c1, out var r1);
                        for (int c = Math.Min(c0, c1); c <= Math.Max(c0, c1); c++) for (int rr = Math.Min(r0, r1); rr <= Math.Max(r0, r1); rr++) if (WorldMapMath.InGrid(c, rr)) tiles.Add(c * 64 + rr);
                    }
                    if (tiles.Count == 0 || tiles.Count > 100) continue;
                    var files = wdt.Tiles.Where(t => t.RootFdid != 0 && tiles.Contains(t.Col * 64 + t.Row)).ToList();
                    var keys = casc.ResolveFileIds(new HashSet<int>(files.Select(t => t.RootFdid)));
                    double sx = 0, sy = 0; int n = 0;
                    foreach (var t in files)
                    {
                        if (!keys.TryGetValue(t.RootFdid, out var ck)) continue;
                        if (!adts.TryGetValue(t.RootFdid, out var f))
                        {
                            try
                            {
                                int len = casc.ReadByContentKey(ck, ref tileBuf);
                                var adt = AdtFile.Parse(len < 0 ? null : tileBuf, Math.Max(0, len));
                                if (adt.Ok)
                                {
                                    var ids = new int[256]; var pos = new float[512];
                                    for (int i = 0; i < 256; i++) { var ch = adt.Chunks[i]; if (ch == null) { ids[i] = int.MinValue; continue; } ids[i] = ch.AreaId; pos[i * 2] = ch.PosX; pos[i * 2 + 1] = ch.PosY; }
                                    f = Tuple.Create(ids, pos);
                                }
                            }
                            catch (Exception) { f = null; }
                            adts[t.RootFdid] = f;
                        }
                        if (f == null) continue;
                        for (int i = 0; i < 256; i++)
                            if (f.Item1[i] != int.MinValue && f.Item1[i] == a.Id) { sx += f.Item2[i * 2] - DungeonMaps.DungeonGeometry.ChunkSize / 2; sy += f.Item2[i * 2 + 1] - DungeonMaps.DungeonGeometry.ChunkSize / 2; n++; }
                    }
                    if (n == 0) continue;
                    m.AltEntranceMapId = a.ContinentId; m.AltX = sx / n; m.AltY = sy / n; m.AltAreaId = a.Id; m.AltKind = KindArea;
                }
            }
            catch (Exception e) { set.Notes.Add("approximate entrances: " + e.Message); }
        }

        public static void Resolve(CascStorage casc, MapSet set)
        {
            try
            {
                if (!Wanting(set).Any()) return;
                var k = casc.ResolveFileIds(new HashSet<int> { AreaPoiFileDataId });
                if (k.TryGetValue(AreaPoiFileDataId, out var ck)) FromPois(set, ParsePois(casc.ReadByContentKey(ck), set.Notes));
                else set.Notes.Add("AreaPOI.db2 not found");
            }
            catch (Exception e) { set.Notes.Add("map points: " + e.Message); }
            FromAreas(casc, set);
        }
    }
}
