using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ElansAddonHub.Services.Maps
{
    // The lookups over a MapSet: the UiMap tree, the art of each map, the instances with their floors, and the world <-> map conversion.
    // Immutable once built, so any thread can read it.
    //
    // World <-> map: the game's world coordinates are (x = north, y = west). A UiMapAssignment says "this rectangle of map `MapId`
    // (x from MinX to MaxX, y from MinY to MaxY) is drawn in the part UiMin..UiMax of UiMap `UiMapId`". The picture runs west -> east and
    // north -> south, so for a point (x, y) in the rectangle
    //     fx = (MaxY - y) / (MaxY - MinY)        fy = (MaxX - x) / (MaxX - MinX)
    //     u  = UiMinX + fx * (UiMaxX - UiMinX)   v  = UiMinY + fy * (UiMaxY - UiMinY)       (u, v = the fractions of the UiMap's picture)
    // (checked: Razor Hill at x 340, y -4686 is 52 / 43 on Durotar, as the game's own map shows it).
    public sealed class MapIndex
    {
        public readonly MapSet Set;
        readonly Dictionary<int, UiMapInfo> ui = new Dictionary<int, UiMapInfo>();
        readonly Dictionary<int, List<UiMapInfo>> children = new Dictionary<int, List<UiMapInfo>>();
        readonly Dictionary<int, MapArtInfo> artOf = new Dictionary<int, MapArtInfo>();        // by UiMap id
        readonly Dictionary<int, List<UiAssignment>> byWorldMap = new Dictionary<int, List<UiAssignment>>();
        readonly Dictionary<int, List<UiAssignment>> byUiMap = new Dictionary<int, List<UiAssignment>>();
        readonly Dictionary<int, AreaInfo> areas = new Dictionary<int, AreaInfo>();
        readonly Dictionary<int, GameMapInfo> maps = new Dictionary<int, GameMapInfo>();
        readonly Dictionary<int, List<FloorMember>> groupOf = new Dictionary<int, List<FloorMember>>();   // by UiMap id -> the whole group's members
        public readonly List<InstanceInfo> Instances = new List<InstanceInfo>();

        public int UiMapCount => ui.Count;
        public IEnumerable<UiMapInfo> UiMaps => ui.Values.OrderBy(m => m.Id);
        public int MapsWithArt => artOf.Count;
        public bool HasWorldPositions => Set.HasAssignments && Set.Assignments.Count > 0;

        static readonly Regex Junk = new Regex(@"<unused>|deprecated|\bunused\b|\btest\b|\bdnt\b|\[ph\]|nothing to see here|^\W*$|^\d+$|PVP\s?Barracks", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        // a word "test" glued to another one is a leftover too ("CashTest", "TEST_01"); a real name that merely contains the letters is not ("Contest", "Testament")
        static readonly Regex GluedTest = new Regex(@"(?<=[a-z])Test(?![a-z])|(?<![A-Za-z])TEST(?![A-Za-z])|(?<![A-Za-z])test(?=_|\d)", RegexOptions.CultureInvariant);
        public static bool IsPlaceholderName(string name) => string.IsNullOrWhiteSpace(name) || Junk.IsMatch(name) || GluedTest.IsMatch(name);
        static readonly Regex TestWord = new Regex(@"\btest\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        // a map's folder name ("TEST_01", "test"); a number is a normal folder name for the newer maps ("2784")
        public static bool IsTestDirectory(string dir) => !string.IsNullOrEmpty(dir) && (TestWord.IsMatch(dir) || GluedTest.IsMatch(dir));

        public MapIndex(MapSet set)
        {
            Set = set;
            foreach (var m in set.UiMaps) ui[m.Id] = m;
            foreach (var m in ui.Values.OrderBy(m => m.Id))
            {
                if (m.ParentId == 0 || m.ParentId == m.Id || !ui.ContainsKey(m.ParentId)) continue;
                if (!children.TryGetValue(m.ParentId, out var l)) children[m.ParentId] = l = new List<UiMapInfo>();
                l.Add(m);
            }

            // the art of each map: phase 0 (always shown), else the lowest phase; only art that has tiles
            var arts = new Dictionary<int, MapArtInfo>();
            foreach (var a in set.Arts) arts[a.Id] = a;
            foreach (var g in set.ArtLinks.GroupBy(l => l.UiMapId))
                foreach (var l in g.OrderBy(x => x.Phase).ThenBy(x => x.ArtId))
                    if (arts.TryGetValue(l.ArtId, out var art) && art.BaseLayer != null && art.Tiles.Any(t => t.Layer == art.BaseLayer.Index)) { artOf[g.Key] = art; break; }

            foreach (var a in set.Assignments)
            {
                if (!byWorldMap.TryGetValue(a.MapId, out var w)) byWorldMap[a.MapId] = w = new List<UiAssignment>();
                w.Add(a);
                if (!byUiMap.TryGetValue(a.UiMapId, out var u)) byUiMap[a.UiMapId] = u = new List<UiAssignment>();
                u.Add(a);
            }
            foreach (var a in set.Areas) areas[a.Id] = a;
            foreach (var m in set.Maps) maps[m.Id] = m;

            foreach (var grp in set.Floors.Where(f => f.GroupId != 0).GroupBy(f => f.GroupId))
            {
                var members = grp.OrderBy(f => f.FloorIndex).ThenBy(f => f.RelativeHeight).ToList();
                foreach (var f in members) groupOf[f.UiMapId] = members;
            }

            BuildInstances();
        }

        void BuildInstances()
        {
            // the event rule needs the ghost spots: a Map table without them (an older layout) says nothing about a way in
            bool corpseData = Set.Maps.Any(x => (x.InstanceType == 1 || x.InstanceType == 2) && x.CorpseMapId >= 0);
            foreach (var m in Set.Maps.OrderBy(x => x.Id))
            {
                if (m.InstanceType < 1 || m.InstanceType > 4) continue;
                var inst = new InstanceInfo
                {
                    MapId = m.Id, Name = (m.Name ?? "").Trim(), InstanceType = m.InstanceType, MaxPlayers = m.MaxPlayers,
                    Kind = m.InstanceType == 1 ? "Dungeon" : m.InstanceType == 2 ? "Raid" : m.InstanceType == 3 ? "Battleground" : "Arena",
                };
                if (inst.Name.Length == 0) inst.Name = m.Directory ?? ("Map " + m.Id);
                inst.Placeholder = IsPlaceholderName(inst.Name) || IsTestDirectory(m.Directory);
                if (!inst.Placeholder && (m.InstanceType == 1 || m.InstanceType == 2))      // dungeons and raids (a battleground or arena is entered from a battlemaster, not a door)
                {
                    inst.Entrance = EntranceOf(m);
                    if (inst.Entrance != null) PlaceEntrance(inst.Entrance);
                    ApplyLevels(inst);
                }
                // the maps that show this instance: the ones the assignments point at it (alternative copies only when there is nothing else)
                var mine = (byWorldMap.TryGetValue(m.Id, out var asg) ? asg : new List<UiAssignment>()).Select(a => a.UiMapId).Distinct()
                    .Where(ui.ContainsKey).Select(id => ui[id]).ToList();
                if (mine.Any(x => x.System == 0)) mine = mine.Where(x => x.System == 0).ToList();
                // a map in a group brings its whole group (the floors of a dungeon)
                var floors = new List<UiMapInfo>();
                foreach (var f in mine.OrderBy(x => x.Id))
                {
                    if (groupOf.TryGetValue(f.Id, out var grp)) { foreach (var g in grp) if (ui.TryGetValue(g.UiMapId, out var gm) && !floors.Contains(gm)) floors.Add(gm); }
                    else if (!floors.Contains(f)) floors.Add(f);
                }
                inst.Floors = floors;
                // data, not names: a dungeon or raid the game puts no ghost of on a continent has no way in from the world; a battleground or arena with no map
                inst.Event = corpseData && !inst.Placeholder && ((m.InstanceType == 1 || m.InstanceType == 2) ? m.CorpseMapId < 0 : floors.Count == 0);
                Instances.Add(inst);
            }
        }

        // ---- instance entrances and levels
        public const string EntranceSourceName = "Game data: the spot where the game puts your ghost (Map.db2)";
        const double MaxWorld = 17066.67;

        // The way into an instance: Map.db2 names, for every instance, the continent and the spot where a ghost that dies inside is put - the spirit healer / corpse run target,
        // which is the instance portal's door (verified on the real files: Deadmines in Westfall, Wailing Caverns in the Barrens, Ragefire Chasm in Orgrimmar, Molten Core at
        // Blackrock Mountain). A row with no continent (-1), no spot (0, 0) or a spot outside the world has no entrance - nothing is guessed.
        public static InstanceEntrance EntranceOf(GameMapInfo m)
        {
            if (m == null || m.CorpseMapId < 0) return null;
            double x = m.CorpseX, y = m.CorpseY;
            if (double.IsNaN(x) || double.IsNaN(y) || double.IsInfinity(x) || double.IsInfinity(y)) return null;
            if (Math.Abs(x) > MaxWorld || Math.Abs(y) > MaxWorld || Math.Abs(x) + Math.Abs(y) < 1) return null;
            return new InstanceEntrance { MapId = m.CorpseMapId, X = x, Y = y, AreaId = m.CorpseAreaId, Source = EntranceSourceName };
        }

        // the zone map an entrance is shown on: the zone the terrain's area id names (exact) when it was read and that map shows the spot, else the most specific map whose
        // rectangle holds it (loose boxes: near a border they can name the neighbour)
        void PlaceEntrance(InstanceEntrance e)
        {
            MapPoint? p = null;
            if (e.AreaId > 0)
            {
                int ui = UiMapForArea(e.AreaId);
                if (ui > 0) p = WorldToMap(e.MapId, e.X, e.Y, ui);
                if (p != null) e.Source = EntranceSourceName + "; the zone is the terrain's own area at that spot";
            }
            if (p == null) p = WorldToMap(e.MapId, e.X, e.Y);
            // a continent shown only on the world map (or the cosmic map) is no zone: say nothing rather than "World"
            var um = p == null ? null : Get(p.Value.UiMapId);
            if (um != null && (um.Type == UiMapInfo.World || um.Type == UiMapInfo.Cosmic)) p = null;
            if (p != null) { e.UiMapId = p.Value.UiMapId; e.Fx = p.Value.Fx; e.Fy = p.Value.Fy; e.ZoneName = Name(p.Value.UiMapId); }
        }
        static string Norm(string s)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var c in (s ?? "").ToLowerInvariant()) if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.ToString();
        }
        static int Lev(string a, string b)
        {
            var d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
                for (int j = 1; j <= b.Length; j++)
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            return d[a.Length, b.Length];
        }
        // the game spells the same dungeon differently in its two tables ("Stormwind Stockades" / "Stormwind Stockade", "Zul'Farak" / "Zul'Farrak", "Onyxia" / "Onyxia's Lair",
        // "Lower Blackrock Spire" / "Blackrock Spire"): the same words, one inside the other, or one letter apart
        public static bool SameDungeonName(string a, string b)
        {
            a = Norm(a); b = Norm(b);
            if (a.Length < 6 || b.Length < 6) return false;
            if (a == b || a.Contains(b) || b.Contains(a)) return true;
            return Math.Min(a.Length, b.Length) >= 8 && Lev(a, b) <= 1;
        }

        // the level the game suggests (all the LFG rows of the same name: Dire Maul has three wings, Blackrock Spire two halves)
        void ApplyLevels(InstanceInfo inst)
        {
            if (!Set.HasLfg) return;
            var rows = Set.Lfg.Where(d => d.MinLevel > 0 && SameDungeonName(d.Name, inst.Name)).OrderBy(d => d.Id).ToList();
            if (rows.Count == 0) return;
            inst.MinLevel = rows.Min(d => d.MinLevel); inst.MaxLevel = rows.Max(d => d.MaxLevel);
            if (rows.Count > 1) inst.Parts = rows.Select(d => d.Name).Distinct().ToList();
        }

        // ---- the tree and the art
        public UiMapInfo Get(int uiMapId) => ui.TryGetValue(uiMapId, out var m) ? m : null;
        public string Name(int uiMapId) => Get(uiMapId)?.Name ?? "";
        public IReadOnlyList<UiMapInfo> Children(int uiMapId) => children.TryGetValue(uiMapId, out var l) ? l : (IReadOnlyList<UiMapInfo>)new UiMapInfo[0];
        public MapArtInfo ArtFor(int uiMapId) => artOf.TryGetValue(uiMapId, out var a) ? a : null;
        public bool HasArt(int uiMapId) => artOf.ContainsKey(uiMapId);

        // size in pixels of the finished picture of a map (0, 0 without art)
        public void PictureSize(int uiMapId, out int width, out int height)
        {
            var l = ArtFor(uiMapId)?.BaseLayer;
            width = l?.Width ?? 0; height = l?.Height ?? 0;
        }

        // the maps from the top of the tree down to this one (cosmic maps and alternative copies left out). Never loops.
        public List<UiMapInfo> Breadcrumb(int uiMapId)
        {
            var chain = new List<UiMapInfo>();
            var seen = new HashSet<int>();
            for (var m = Get(uiMapId); m != null && seen.Add(m.Id); m = m.ParentId != 0 ? Get(m.ParentId) : null) chain.Insert(0, m);
            return chain.Where(m => m.Type != UiMapInfo.Cosmic).ToList();
        }

        // the floors of the multi-floor map this one belongs to (bottom to top), or just this map
        public List<UiMapInfo> FloorsOf(int uiMapId)
        {
            if (groupOf.TryGetValue(uiMapId, out var grp)) return grp.Where(f => ui.ContainsKey(f.UiMapId)).Select(f => ui[f.UiMapId]).Distinct().ToList();
            var inst = Instances.FirstOrDefault(i => i.Floors.Any(f => f.Id == uiMapId));
            if (inst != null && inst.Floors.Count > 1) return inst.Floors;
            var m = Get(uiMapId);
            return m == null ? new List<UiMapInfo>() : new List<UiMapInfo> { m };
        }

        public string FloorName(int uiMapId) { var f = Set.Floors.FirstOrDefault(x => x.UiMapId == uiMapId); return f != null && f.Name.Length > 0 ? f.Name : Name(uiMapId); }

        public InstanceInfo Instance(int mapId) => Instances.FirstOrDefault(i => i.MapId == mapId);
        public GameMapInfo GameMap(int mapId) => maps.TryGetValue(mapId, out var m) ? m : null;
        public string AreaName(int areaId) => areas.TryGetValue(areaId, out var a) ? a.Name : null;
        public AreaInfo Area(int areaId) => areas.TryGetValue(areaId, out var a) ? a : null;

        // the map an area (zone) id belongs to: its own assignment, else the nearest parent area that has one
        public int UiMapForArea(int areaId)
        {
            var seen = new HashSet<int>();
            for (int id = areaId; id != 0 && seen.Add(id); id = areas.TryGetValue(id, out var a) ? a.ParentId : 0)
            {
                var hit = Set.Assignments.Where(x => x.AreaId == id && Get(x.UiMapId)?.System == 0).OrderBy(x => x.Area).FirstOrDefault();
                if (hit != null) return hit.UiMapId;
            }
            return 0;
        }

        // the maps that show a world map / instance, most specific first
        public List<UiMapInfo> UiMapsOfWorldMap(int mapId)
        {
            if (!byWorldMap.TryGetValue(mapId, out var l)) return new List<UiMapInfo>();
            return l.OrderBy(a => a.Area).Select(a => Get(a.UiMapId)).Where(m => m != null && m.System == 0).Distinct().ToList();
        }

        // ---- world <-> map
        // how specific a map is when it shows a point: a city, dungeon, battleground ... (a zone-like map WITHOUT the "show overlays" flag: its
        // rectangle is drawn tight around it) beats a zone (whose rectangle is a loose box that overlaps its neighbours), a zone beats the
        // continent, the continent beats the world map
        const int FlagShowOverlays = 2;
        static int Tier(UiMapInfo m)
        {
            bool zoneLike = m.Type == UiMapInfo.Zone || m.Type == UiMapInfo.Dungeon || m.Type == UiMapInfo.Micro || m.Type == UiMapInfo.Orphan;
            return zoneLike ? ((m.Flags & FlagShowOverlays) == 0 ? 3 : 2) : m.Type == UiMapInfo.Continent ? 1 : 0;
        }

        static bool Contains(UiAssignment a, double x, double y) => x >= a.MinX && x <= a.MaxX && y >= a.MinY && y <= a.MaxY;
        static MapPoint Place(UiAssignment a, double x, double y)
        {
            double fx = (a.MaxY - y) / (a.MaxY - a.MinY), fy = (a.MaxX - x) / (a.MaxX - a.MinX);
            return new MapPoint(a.UiMapId, a.UiMinX + fx * (a.UiMaxX - a.UiMinX), a.UiMinY + fy * (a.UiMaxY - a.UiMinY));
        }

        // The most specific map that shows the world position (mapId, x, y): see Tier. Of several maps in the best tier, the smallest wins in the
        // top tier (cities and dungeons do not overlap but nest), and for zones the one the point is most central in (neighbouring zones' loose
        // rectangles overlap along their borders and over each other's corners). Alternative copies (System 1) are never chosen. null = no map
        // shows that position.
        // Where the terrain says which area a position is in (AreaProbe; null in a test). The rectangles of the map tables are boxes: a city's covers the edge of the zone around it (Northshire
        // Abbey is "Stormwind City"), a zone's overlaps its neighbour's. When the area is known its zone decides; the rectangles answer for what the terrain cannot say.
        public static Func<int, double, double, int> AreaAt;
        readonly Dictionary<int, int> uiOfArea = new Dictionary<int, int>();
        int UiMapOfAreaCached(int area) { lock (uiOfArea) { if (!uiOfArea.TryGetValue(area, out var u)) uiOfArea[area] = u = UiMapForArea(area); return u; } }

        public MapPoint? WorldToMap(int mapId, double x, double y)
        {
            if (!byWorldMap.TryGetValue(mapId, out var all)) return null;
            var probe = AreaAt;
            if (probe != null)
            {
                int area = 0;
                try { area = probe(mapId, x, y); } catch (Exception) { }
                if (area > 0) { int ui = UiMapOfAreaCached(area); if (ui > 0) { var q = WorldToMap(mapId, x, y, ui); if (q != null) return q; } }
            }
            var hits = all.Where(a => Contains(a, x, y)).Select(a => new { a, m = Get(a.UiMapId) }).Where(h => h.m != null && h.m.System == 0).ToList();
            if (hits.Count == 0) return null;
            int tier = hits.Max(h => Tier(h.m));
            var top = hits.Where(h => Tier(h.m) == tier).Select(h => h.a).OrderBy(c => c.Area).ThenBy(c => c.Order).ThenBy(c => c.UiMapId).ToList();
            if (tier == 3 || top.Count == 1) return Place(top[0], x, y);
            UiAssignment best = null; double bestD = double.MaxValue;
            foreach (var c in top)
            {
                var p = Place(c, x, y);
                double u = (p.Fx - c.UiMinX) / (c.UiMaxX - c.UiMinX), v = (p.Fy - c.UiMinY) / (c.UiMaxY - c.UiMinY);
                double d = Math.Max(Math.Abs(u - 0.5), Math.Abs(v - 0.5));
                if (d < bestD - 1e-9) { bestD = d; best = c; }
            }
            return Place(best, x, y);
        }
        // the position on one given map (a continent, the zone, a floor); null when that map does not show the point
        public MapPoint? WorldToMap(int mapId, double x, double y, int uiMapId)
        {
            if (!byUiMap.TryGetValue(uiMapId, out var l)) return null;
            var hit = l.Where(a => a.MapId == mapId && Contains(a, x, y)).OrderBy(a => a.Area).ThenBy(a => a.Order).FirstOrDefault();
            return hit == null ? (MapPoint?)null : Place(hit, x, y);
        }

        // the inverse: the world position under the fractions (u, v) of a map. The smallest part of the map that holds the point is used (the
        // world map shows two continents side by side). null = nothing of the world is drawn there.
        public WorldPoint? MapToWorld(int uiMapId, double u, double v)
        {
            if (!byUiMap.TryGetValue(uiMapId, out var l)) return null;
            var a = l.Where(c => u >= c.UiMinX && u <= c.UiMaxX && v >= c.UiMinY && v <= c.UiMaxY)
                .OrderBy(c => (double)(c.UiMaxX - c.UiMinX) * (c.UiMaxY - c.UiMinY)).ThenBy(c => c.Order).FirstOrDefault();
            if (a == null) return null;
            double fx = (u - a.UiMinX) / (a.UiMaxX - a.UiMinX), fy = (v - a.UiMinY) / (a.UiMaxY - a.UiMinY);
            return new WorldPoint(a.MapId, a.MaxX - fy * (a.MaxX - a.MinX), a.MaxY - fx * (a.MaxY - a.MinY));
        }

        // a point on one map -> the same place on another (a zone position on its continent map, a continent position on the zone ...)
        public MapPoint? Convert(int fromUiMapId, double u, double v, int toUiMapId)
        {
            if (fromUiMapId == toUiMapId) return new MapPoint(toUiMapId, u, v);
            var w = MapToWorld(fromUiMapId, u, v);
            return w == null ? null : WorldToMap(w.Value.MapId, w.Value.X, w.Value.Y, toUiMapId);
        }
    }
}
