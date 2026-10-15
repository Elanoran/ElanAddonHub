using System;
using System.Collections.Generic;
using System.Linq;
using ElansAddonHub.Services.WorldMap;

namespace ElansAddonHub.Services.Maps
{
    // Which zone an instance entrance stands in. The map rectangles of UiMapAssignment are loose boxes that overlap along borders (Razorfen Kraul and Onyxia's Lair
    // fall in the wrong neighbour), the terrain is exact: every 33-yard chunk of a continent's ADT tiles carries its AreaTable id. Once per game build (the result is part of
    // the saved index) the one tile that holds each entrance is read from the player's own game files (the continent's WDT names the file) and the area of the chunk
    // under the entrance is kept. Read-only, a handful of small files; any failure just leaves the area 0 and the entrance keeps the rectangle answer.
    public static class EntranceAreas
    {
        public static void Resolve(Services.CascStorage casc, MapSet set)
        {
            try
            {
                var want = set.Maps.Where(m => MapIndex.CorpseEntrance(m) != null).ToList();
                foreach (var cont in want.GroupBy(m => m.CorpseMapId))
                {
                    var cm = set.Maps.FirstOrDefault(m => m.Id == cont.Key);
                    if (cm == null || cm.WdtFileDataId <= 0) continue;
                    var wk = casc.ResolveFileIds(new HashSet<int> { cm.WdtFileDataId });
                    if (!wk.TryGetValue(cm.WdtFileDataId, out var wck)) continue;
                    var wdt = WdtFile.Parse(casc.ReadByContentKey(wck));
                    if (!wdt.Ok) continue;
                    var tiles = wdt.Tiles.Where(t => t.RootFdid != 0).GroupBy(t => t.Col * 64 + t.Row).ToDictionary(g => g.Key, g => g.First());
                    var need = new Dictionary<int, WdtTile>();
                    foreach (var m in cont) { WorldMapMath.TileOf(m.CorpseX, m.CorpseY, out var c, out var r); if (tiles.TryGetValue(c * 64 + r, out var t)) need[m.Id] = t; }
                    var keys = casc.ResolveFileIds(new HashSet<int>(need.Values.Select(t => t.RootFdid)));
                    foreach (var m in cont)
                    {
                        if (!need.TryGetValue(m.Id, out var t) || !keys.TryGetValue(t.RootFdid, out var ck)) continue;
                        try { m.CorpseAreaId = AreaAt(casc.ReadByContentKey(ck), m.CorpseX, m.CorpseY); }
                        catch (Exception e) { set.Notes.Add("entrance area of " + m.Name + ": " + e.Message); }
                    }
                }
            }
            catch (Exception e) { set.Notes.Add("entrance areas: " + e.Message + ": zones come from the map rectangles"); }
        }

        // the AreaTable id of the chunk of a root ADT that holds the world position (0 = none)
        public static int AreaAt(byte[] adt, double x, double y)
        {
            var f = AdtFile.Parse(adt);
            if (!f.Ok) return 0;
            WorldMapMath.WorldToGrid(x, y, out var c, out var r);
            int col = (int)Math.Floor(c), row = (int)Math.Floor(r);
            int cx = Math.Max(0, Math.Min(15, (int)((c - col) * 16))), cy = Math.Max(0, Math.Min(15, (int)((r - row) * 16)));
            var ch = f.Chunks[cy * 16 + cx];
            return ch == null ? 0 : Math.Max(0, ch.AreaId);
        }
    }
}
