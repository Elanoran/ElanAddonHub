using System;
using System.Collections.Generic;
using System.Linq;
using ElansAddonHub.Services.Atlas.Classic;
using ElansAddonHub.Services.Maps;

namespace ElansAddonHub.Services.Atlas
{
    public enum ZoneLoreVerdict { Here, City, NoData, Elsewhere }

    public sealed class ZoneLoreCheckResult
    {
        public int Zone;
        public ZoneLoreMob Mob;
        public ZoneLoreVerdict Verdict;
        public string Detail = "";
        public override string ToString() => Zone + " " + Mob + ": " + Verdict + (Detail.Length > 0 ? " (" + Detail + ")" : "");
    }

    // Is a notable creature of a zone's notes really there? Its Classic baseline spawn points are placed on the maps the way the Atlas places them
    // (MapIndex.WorldToMap), twice: by the terrain's area when it is known, and by the map rectangles alone - the terrain under a city built inside a
    // mountain or under the ground (Ironforge, Undercity) is the zone around it, the rectangles know the city. Either one is enough:
    //   Here       a spawn point lies on the zone's map or on a map inside it (a cave, a sub-zone)
    //   City       none does, but one stands in a capital city next to the zone (its rectangle touches the zone's: Thrall in Orgrimmar for Durotar)
    //   NoData     the baseline cannot tell: no such creature, no spawn points, or only inside instances (their own maps or none) / off every map
    //   Elsewhere  its spawn points are all on other maps: the notes put it in the wrong zone
    public static class ZoneLoreCheck
    {
        public static int[] Capitals = { 1453, 1454, 1455, 1456, 1457, 1458 };      // Stormwind, Orgrimmar, Ironforge, Thunder Bluff, Darnassus, Undercity

        public static ZoneLoreCheckResult Check(int zone, ZoneLoreMob mob, ClassicIndex ci, MapIndex idx)
        {
            var r = new ZoneLoreCheckResult { Zone = zone, Mob = mob };
            var n = ci?.NpcInfo(mob.Id);
            if (n == null) { r.Verdict = ZoneLoreVerdict.NoData; r.Detail = "not in the Classic baseline"; return r; }
            if (n.Spawns == null || n.Spawns.Length == 0) { r.Verdict = ZoneLoreVerdict.NoData; r.Detail = "no spawn points"; return r; }
            var count = new Dictionary<int, int>();
            string city = null, inside = null;
            var probe = MapIndex.AreaAt;
            try
            {
                foreach (var s in n.Spawns)
                {
                    var places = new List<int>();
                    var p = idx.WorldToMap(s.Map, s.X, s.Y);
                    if (p != null) places.Add(p.Value.UiMapId);
                    if (probe != null)
                    {
                        MapIndex.AreaAt = null;
                        var q = idx.WorldToMap(s.Map, s.X, s.Y);
                        MapIndex.AreaAt = probe;
                        if (q != null && !places.Contains(q.Value.UiMapId)) places.Add(q.Value.UiMapId);
                    }
                    if (places.Count == 0) continue;
                    foreach (var ui in places)
                    {
                        if (ui == zone || Ancestors(idx, ui).Contains(zone)) { r.Verdict = ZoneLoreVerdict.Here; r.Detail = Name(idx, ui); return r; }
                        if (idx.Get(ui)?.Type == UiMapInfo.Dungeon) { inside = inside ?? Name(idx, ui); continue; }      // a floor of an instance: not another zone
                        count.TryGetValue(ui, out var c); count[ui] = c + 1;
                        if (city == null && Array.IndexOf(Capitals, ui) >= 0 && (idx.WorldToMap(s.Map, s.X, s.Y, zone) != null || Touch(idx, ui, zone, s.Map))) city = Name(idx, ui);
                    }
                }
            }
            finally { MapIndex.AreaAt = probe; }
            if (city != null) { r.Verdict = ZoneLoreVerdict.City; r.Detail = "stands in " + city; return r; }
            if (count.Count == 0) { r.Verdict = ZoneLoreVerdict.NoData; r.Detail = "only inside instances" + (inside != null ? " (" + inside + ")" : "") + " or off the maps (" + n.Spawns.Length + " spawn points)"; return r; }
            r.Verdict = ZoneLoreVerdict.Elsewhere;
            r.Detail = string.Join(", ", count.OrderByDescending(k => k.Value).ThenBy(k => k.Key).Take(3).Select(k => Name(idx, k.Key) + " " + k.Value));
            return r;
        }

        // the world rectangles of two maps on one continent meet (the zone's grown by 2 % for the seam between them)
        static bool Touch(MapIndex idx, int a, int b, int mapId)
        {
            var ra = idx.Set.Assignments.Where(x => x.UiMapId == a && x.MapId == mapId).ToList();
            var rb = idx.Set.Assignments.Where(x => x.UiMapId == b && x.MapId == mapId).ToList();
            foreach (var x in ra)
                foreach (var y in rb)
                {
                    double gx = (y.MaxX - y.MinX) * 0.02, gy = (y.MaxY - y.MinY) * 0.02;
                    if (x.MinX <= y.MaxX + gx && x.MaxX >= y.MinX - gx && x.MinY <= y.MaxY + gy && x.MaxY >= y.MinY - gy) return true;
                }
            return false;
        }

        // every notable creature of every entry with the source "classic"
        public static List<ZoneLoreCheckResult> CheckAll(IEnumerable<ZoneLoreEntry> all, ClassicIndex ci, MapIndex idx)
            => all.SelectMany(e => e.Mobs.Where(m => m.Source == "classic" && m.Id > 0).Select(m => Check(e.ZoneId, m, ci, idx))).ToList();

        static IEnumerable<int> Ancestors(MapIndex idx, int ui)
        {
            var seen = new HashSet<int>();
            for (var m = idx.Get(ui); m != null && m.ParentId != 0 && seen.Add(m.Id); m = idx.Get(m.ParentId)) yield return m.ParentId;
        }

        static string Name(MapIndex idx, int ui) => idx.Get(ui)?.Name ?? ("map " + ui);
    }
}
