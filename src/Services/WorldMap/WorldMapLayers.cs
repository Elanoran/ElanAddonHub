using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Atlas.Classic;
using ElansAddonHub.Services.Maps;

namespace ElansAddonHub.Services.WorldMap
{
    // What the layers of the world map hold. Each layer is built on a worker thread, on request (the first time it is switched on for a continent), from
    // the data the Atlas already has, and sent to the page as one message in the page's own plane (lat / lng, see WorldMapMath). Nothing is drawn from
    // data that is not there: a layer with no data reports "none" and says why.
    //   zones      the zone names with their creature level range (UiMap rectangles + the Classic baseline's creature levels)
    //   fm         flight masters (TaxiNodes): Alliance, Horde, both
    //   routes     flight paths (TaxiPath + TaxiPathNode) with the time the Lodge's members measured ("2m 10s")
    //   instances  dungeon and raid entrances (Map.db2 corpse spot) with the level the game suggests; kinds dungeons / raids
    //   nodes      herbs / ore / chests / fishing holes: what players looted (this PC's own and the Lodge's) and the Classic baseline's spawn points
    //   saved      the gathering routes saved in the Atlas
    //   vendors, trainers, quests, rares: Classic baseline creatures by their flags, vendors and rares the Lodge saw as well
    //   finds      what the Lodge's members found (items of green quality and better, with where)
    public sealed class LayerResult
    {
        public string Json = "";
        public int Count;
        public string Note = "";
        public bool Empty => Count == 0;
    }

    public static class WorldMapLayers
    {
        // hooks (the main window sets them; tests set their own)
        public static Func<IEnumerable<SavedRoute>> SavedRoutes = () => null;
        public static Func<IDictionary<string, double>> FlightTimes = () => null;     // "Horde|11>22" -> seconds
        public const int MaxPointsPerLayer = 60000;

        public const string Alliance = "a", Horde = "h", Neutral = "n";

        // the layer panel's rows
        public static List<Dictionary<string, object>> Catalog()
        {
            Dictionary<string, object> L(string id, string title, string color, string tip, params Dictionary<string, object>[] kids)
            {
                var d = new Dictionary<string, object> { ["id"] = id, ["title"] = title, ["color"] = color, ["tip"] = tip };
                if (kids.Length > 0) d["children"] = kids.ToList();
                return d;
            }
            return new List<Dictionary<string, object>>
            {
                L("zones", "Zone names", "#f5d97a", "Zone names with the level range of the creatures that live there"),
                L("borders", "Zone borders", "#7dd3fc", "Outlines of the zones, from the game's terrain data (the first time it is switched on the terrain is read from your game files, once per game version); point at a zone to tint it"),
                L("fm", "Flight masters", "#5aa9ff", "Flight masters of the Alliance (blue), the Horde (red) and both (gold)",
                    L("fm.horde", "Horde", "#ff6b5e", ""), L("fm.alliance", "Alliance", "#5aa9ff", ""), L("fm.neutral", "Neutral", "#f0b429", "Both factions")),
                L("routes", "Flight routes", "#ff9f43", "Flight paths between flight masters, by faction (a route is Horde or Alliance when both its ends are, else neutral); the flight time is shown when you or the Lodge have flown it",
                    L("routes.horde", "Horde", "#ff6b5e", ""), L("routes.alliance", "Alliance", "#5aa9ff", ""), L("routes.neutral", "Neutral", "#f0b429", "")),
                L("instances", "Dungeons & raids", "#e879f9", "Dungeon and raid entrances with their level and group size; click one for its page, right-click to navigate there",
                    L("instances.dungeons", "Dungeons", "#e879f9", ""), L("instances.raids", "Raids", "#f97316", "")),
                L("nodes", "Gathering nodes", "#4ade80", "Herbs, ore, chests and fishing holes: what players looted (Forever) and the Classic baseline's spawn points",
                    L("nodes.herbs", "Herbs", "#4ade80", ""), L("nodes.ore", "Ore", "#d08a4e", ""), L("nodes.chests", "Chests", "#f0b429", ""), L("nodes.fishing", "Fishing", "#5aa9ff", "")),
                L("saved", "Saved routes", "#22d3ee", "The gathering loops you saved in the Atlas"),
                L("vendors", "Vendors", "#f0b429", "Merchants (Classic baseline and what the Lodge saw)"),
                L("trainers", "Trainers", "#a78bfa", "Class and profession trainers (Classic baseline)"),
                L("quests", "Quest givers", "#fde047", "Creatures that give quests (Classic baseline)"),
                L("rares", "Rare creatures", "#e5e7eb", "Rare and rare elite creatures (Classic baseline and what the Lodge saw)"),
                L("finds", "Lodge finds", "#f472b6", "Items of green quality and better that Lodge members found, and where"),
            };
        }

        public static readonly string[] DefaultOn = { "zones", "fm", "fm.horde", "fm.alliance", "fm.neutral", "routes", "routes.horde", "routes.alliance", "routes.neutral", "instances", "instances.dungeons", "instances.raids", "nodes.herbs", "nodes.ore", "nodes.chests", "nodes.fishing" };

        // The catalog version a saved layer list belongs to. 2 added the dungeon and raid entrances (on by default): a list saved before that gets them switched on once, so a person who
        // updates sees the new layer; after that their own choice stands (the page saves the version with every change).
        public const int LayersVersion = 3;
        public static List<string> WithNewDefaults(List<string> saved, int version)
        {
            if (saved == null) return null;
            var l = saved.ToList();
            if (version < 2) foreach (var id in new[] { "instances", "instances.dungeons", "instances.raids" }) if (!l.Contains(id)) l.Add(id);
    // 3 added the faction kinds of the flight masters and routes: a list that has the layer on gets its kinds on too
    if (version < 3) foreach (var top in new[] { "fm", "routes" }) if (l.Contains(top)) foreach (var k in new[] { "horde", "alliance", "neutral" }) if (!l.Contains(top + "." + k)) l.Add(top + "." + k);
            return l;
        }

        static double R3(double v) => Math.Round(v, 3);
        static void LL(double x, double y, out double lat, out double lng) { WorldMapMath.WorldToLatLng(x, y, out lat, out lng); lat = R3(lat); lng = R3(lng); }

        public static LayerResult Build(string id, int mapId)
        {
            try
            {
                switch (id)
                {
                    case "zones": return Zones(mapId);
                    case "borders": return Borders(mapId);
                    case "fm": return FlightMasters(mapId);
                    case "routes": return Routes(mapId);
                    case "instances": return Instances(mapId);
                    case "nodes": return Nodes(mapId);
                    case "saved": return Saved(mapId);
                    case "vendors": return Npcs(mapId, "vendors");
                    case "trainers": return Npcs(mapId, "trainers");
                    case "quests": return Npcs(mapId, "quests");
                    case "rares": return Npcs(mapId, "rares");
                    case "finds": return Finds(mapId);
                    default: return new LayerResult { Note = "unknown layer" };
                }
            }
            catch (Exception e)
            {
                Util.Log("world map layer " + id + ": " + e);
                return new LayerResult { Note = "This layer could not be built." };
            }
        }

        static LayerResult Message(string id, string kind, Dictionary<string, object> body, int count, string note)
        {
            body["t"] = "layer"; body["id"] = id; body["kind"] = kind; body["count"] = count; body["note"] = note ?? "";
            return new LayerResult { Json = Json.Write(body), Count = count, Note = note ?? "" };
        }

        // ---------------------------------------------------------------- zones
        public static LayerResult Zones(int mapId)
        {
            var idx = MapData.Index;
            if (idx == null) return new LayerResult { Note = "The maps are not ready yet." };
            var dv = AtlasDerived.For(ClassicAtlas.Current);
            var items = new List<object>();
            foreach (var z in idx.UiMaps.Where(m => m.System == 0 && m.Type == UiMapInfo.Zone && !MapIndex.IsPlaceholderName(m.Name)))
            {
                if (!idx.Set.Assignments.Any(a => a.UiMapId == z.Id && a.MapId == mapId)) continue;
                var c = idx.MapToWorld(z.Id, 0.5, 0.5);
                if (c == null || c.Value.MapId != mapId) continue;
                LL(c.Value.X, c.Value.Y, out var lat, out var lng);
                bool city = (z.Flags & 2) == 0;       // no "show overlays" flag: a city or a small place (its guards would make its level range 2-60: no range for those)
                string levels = "";
                try { var zc = city ? null : dv?.Zone(idx, z.Id); if (zc != null && zc.MinLevel > 0) levels = AtlasFormat.Levels(zc.MinLevel, zc.MaxLevel); } catch (Exception) { }
                items.Add(new Dictionary<string, object>
                {
                    ["lat"] = lat, ["lng"] = lng, ["name"] = z.Name, ["sub"] = levels, ["link"] = "zone:" + z.Id,
                    ["sm"] = city, ["minZ"] = city ? 3.5 : 1.5, ["maxZ"] = city ? 8.0 : 5.75,
                });
            }
            var note = dv == null ? "Level ranges come from the Classic baseline (Atlas start page)." : "";
            return Message("zones", "labels", new Dictionary<string, object> { ["items"] = items }, items.Count, items.Count == 0 ? "The game's map tables list no zones on this continent." : note);
        }

        // -------------------------------------------------------------- zone borders (rings on the plane, from the terrain's per-chunk area ids)
        // null Note + empty Json when the terrain of the continent is not built yet (the host builds it, then asks again)
        public static LayerResult Borders(int mapId)
        {
            var idx = MapData.Index;
            var dir = WorldMapCache.TerrainDir(WorldMapCache.BuildKey, mapId);
            if (WorldMapCache.TerrainBuilt(mapId) == null) return new LayerResult { Note = "The terrain of this continent is not read yet." };
            var set = WorldMapZones.ReadBorders(dir);
            if (set == null || set.Zones.Count == 0) return new LayerResult { Note = "The game's terrain has no zone areas on this continent." };
            var zr = idx == null ? null : ZoneResolver.FromIndex(idx);
            var items = new List<object>();
            foreach (var z in set.Zones)
            {
                int ui = zr == null ? 0 : zr.UiMapOf(z.Id);
                var rings = new List<object>();
                foreach (var r in z.Rings)
                {
                    var a = new double[r.Length];
                    for (int i = 0; i < r.Length; i += 2) { a[i] = -r[i + 1] / 4.0; a[i + 1] = r[i] / 4.0; }       // lattice (x east, y south, 16 per tile) -> plane (lat = -row * 4, lng = col * 4)
                    rings.Add(a);
                }
                var item = new Dictionary<string, object> { ["name"] = z.Name, ["id"] = z.Id, ["rings"] = rings, ["chunks"] = z.Chunks };
                if (ui > 0 && idx != null && idx.Get(ui) != null) item["link"] = "zone:" + ui;
                items.Add(item);
            }
            return Message("borders", "rings", new Dictionary<string, object> { ["items"] = items }, items.Count, "");
        }

        // ---------------------------------------------------------------- flight masters and routes
        // The faction of a node, verified on the real TaxiNodes of 1.60.1.70338 (100 rows): Flags bit 1 = Alliance, bit 2 = Horde (1025 Stormwind, 1026 Orgrimmar, 1027 Ratchet / Marshal's Refuge = both);
        // a node with no faction bit (a Forever addition such as Powderfuse Port, Fishing Village, the Eastern Plaguelands node) is told by its mounts, MountCreatureID[0] = the Horde mount
        // (2224 wind rider, 3574 bat) and [1] = the Alliance mount (541 gryphon, 3837 hippogryph); both mounts = both factions. Mount 308 is the developers' placeholder (the test island and Northshire
        // Abbey carry it): it says nothing about a faction, so such a node is neutral rather than "Horde". "" = names no faction at all (a transport, a quest flight).
        public const int PlaceholderMount = 308;
        public static string FactionOf(TaxiNodeInfo n)
        {
            bool a = (n.Flags & 1) != 0, h = (n.Flags & 2) != 0;
            if (!a && !h)
            {
                a = n.MountAlliance != 0 && n.MountAlliance != PlaceholderMount; h = n.MountHorde != 0 && n.MountHorde != PlaceholderMount;
                if (!a && !h && (n.MountHorde == PlaceholderMount || n.MountAlliance == PlaceholderMount)) return Neutral;
            }
            return a && h ? Neutral : a ? Alliance : h ? Horde : "";
        }

        static readonly string[] NotFlightMasters = { "Transport,", "Generic,", "Quest Path", "zz", "Test", "Programmer Isle" };      // transports, quest flights, retired nodes and the developers' test island
        public static bool IsFlightMaster(TaxiNodeInfo n)
        {
            if (n == null || string.IsNullOrWhiteSpace(n.Name) || FactionOf(n) == "") return false;
            if (NotFlightMasters.Any(p => n.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase))) return false;
            return !(n.X == 0 && n.Y == 0);
        }

        // the kind row a faction belongs to (fm.horde ...), and a route's faction from its two ends: both Horde = Horde, both Alliance = Alliance, anything else (a neutral end, mixed) = neutral
        public static string FactionKey(string f) => f == Alliance ? "alliance" : f == Horde ? "horde" : "neutral";
        public static string RouteFaction(string a, string b) => a == b && (a == Alliance || a == Horde) ? a : Neutral;

        public static string FactionName(string f) => f == Alliance ? "Alliance" : f == Horde ? "Horde" : "Both factions";

        public static LayerResult FlightMasters(int mapId)
        {
            var idx = MapData.Index;
            if (idx == null) return new LayerResult { Note = "The maps are not ready yet." };
            if (!idx.Set.HasTaxi) return new LayerResult { Note = "The game's flight tables are not read (a table is missing or this game version's layout is not known yet)." };
            var items = new List<object>();
            foreach (var n in idx.Set.TaxiNodes.Where(n => n.MapId == mapId && IsFlightMaster(n)).OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ThenBy(n => n.Id))
            {
                LL(n.X, n.Y, out var lat, out var lng);
                var f = FactionOf(n);
                var p = idx.WorldToMap(mapId, n.X, n.Y);
                items.Add(new Dictionary<string, object>
                {
                    ["lat"] = lat, ["lng"] = lng, ["name"] = n.Name, ["f"] = f, ["g"] = "fm." + FactionKey(f), ["tip"] = "Flight master", ["extra"] = FactionName(f) + (p != null ? " · " + idx.Name(p.Value.UiMapId) : ""),
                    ["link"] = p != null ? "zone:" + p.Value.UiMapId : "",
                });
            }
            return Message("fm", "markers", new Dictionary<string, object> { ["items"] = items }, items.Count, items.Count == 0 ? "No flight masters on this continent." : "");
        }

        public static string TimeText(double seconds)
        {
            if (double.IsNaN(seconds) || seconds < 1) return "";
            int s = (int)Math.Round(seconds), m = s / 60; s %= 60;
            return m == 0 ? s + "s" : s == 0 ? m + "m" : m + "m " + s + "s";
        }

        // the time of a leg A > B for a faction (the Lodge's merged value first, then this PC's own; "Horde|11>22" keys, node ids or names)
        static double LegTime(IDictionary<string, double> times, string faction, TaxiNodeInfo a, TaxiNodeInfo b)
        {
            if (times == null || times.Count == 0) return 0;
            foreach (var fac in faction == Neutral ? new[] { "Alliance", "Horde" } : new[] { FactionName(faction) })
                foreach (var ka in new[] { a.Id.ToString(CultureInfo.InvariantCulture), a.Name })
                    foreach (var kb in new[] { b.Id.ToString(CultureInfo.InvariantCulture), b.Name })
                        if (times.TryGetValue(fac + "|" + ka + ">" + kb, out var t) && t > 0) return t;
            return 0;
        }

        public static LayerResult Routes(int mapId)
        {
            var idx = MapData.Index;
            if (idx == null) return new LayerResult { Note = "The maps are not ready yet." };
            if (!idx.Set.HasTaxi) return new LayerResult { Note = "The game's flight tables are not read (a table is missing or this game version's layout is not known yet)." };
            var nodes = idx.Set.TaxiNodes.Where(n => IsFlightMaster(n)).GroupBy(n => n.Id).ToDictionary(g => g.Key, g => g.First());
            var pts = idx.Set.TaxiPoints.GroupBy(p => p.PathId).ToDictionary(g => g.Key, g => g.OrderBy(p => p.Index).ToList());
            var times = SafeTimes();
            var items = new List<object>(); var seen = new HashSet<long>();
            int timed = 0;
            foreach (var path in idx.Set.TaxiPaths.OrderBy(p => p.Id))
            {
                if (!nodes.TryGetValue(path.FromNode, out var a) || !nodes.TryGetValue(path.ToNode, out var b) || a.MapId != mapId || b.MapId != mapId) continue;
                long key = Math.Min(path.FromNode, path.ToNode) * 100000L + Math.Max(path.FromNode, path.ToNode);
                if (!seen.Add(key)) continue;
                var fa = FactionOf(a); var fb = FactionOf(b);
                var fac = RouteFaction(fa, fb);
                var line = new List<double>();
                if (pts.TryGetValue(path.Id, out var l) && l.Count > 1)
                    foreach (var p in l.Where(p => p.MapId == mapId)) { LL(p.X, p.Y, out var la, out var ln); line.Add(la); line.Add(ln); }
                if (line.Count < 4) { LL(a.X, a.Y, out var la, out var ln); line.Add(la); line.Add(ln); LL(b.X, b.Y, out la, out ln); line.Add(la); line.Add(ln); }
                double t = LegTime(times, fac, a, b); if (t <= 0) t = LegTime(times, fac, b, a);
                var label = TimeText(t); if (label.Length > 0) timed++;
                int mid = (line.Count / 4) * 2;
                items.Add(new Dictionary<string, object>
                {
                    ["pts"] = line, ["g"] = "routes." + FactionKey(fac), ["c"] = fac == Alliance ? "#5aa9ff" : fac == Horde ? "#ff6b5e" : "#f0b429", ["w"] = 2.0, ["o"] = 0.8, ["d"] = "6 5",
                    ["name"] = ShortName(a.Name) + " ↔ " + ShortName(b.Name), ["tip"] = label.Length > 0 ? "Flight time " + label : "No flight time known yet", ["extra"] = FactionName(fac),
                    ["label"] = label, ["mid"] = new List<double> { line[Math.Min(mid, line.Count - 2)], line[Math.Min(mid + 1, line.Count - 1)] },
                });
            }
            return Message("routes", "lines", new Dictionary<string, object> { ["items"] = items }, items.Count,
                items.Count == 0 ? "No flight routes on this continent." : timed == 0 ? "No flight times yet: Elan's Hunter Helper measures them as you fly, and the Lodge shares them." : "");
        }

        static string ShortName(string n) { var i = (n ?? "").IndexOf(','); return i > 0 ? n.Substring(0, i) : n; }
        static IDictionary<string, double> SafeTimes() { try { return FlightTimes?.Invoke(); } catch (Exception) { return null; } }

        // ---------------------------------------------------------------- dungeon and raid entrances
        // Every dungeon and raid whose entrance (Map.db2: where the game puts a ghost that dies inside) stands on this continent. The Classic baseline is not needed;
        // the level is the game's own suggested level (LFGDungeons + ContentTuning), the creature range of the baseline only fills in an instance the game does not list.
        public static LayerResult Instances(int mapId)
        {
            var idx = MapData.Index;
            if (idx == null) return new LayerResult { Note = "The maps are not ready yet." };
            var items = new List<object>();
            foreach (var i in idx.Instances.Where(i => !i.Placeholder && i.Entrance != null && i.Entrance.MapId == mapId && (i.InstanceType == 1 || i.InstanceType == 2))
                .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.MapId))
            {
                var e = i.Entrance;
                if (!WorldMapMath.InWorld(e.X, e.Y)) continue;
                LL(e.X, e.Y, out var lat, out var lng);
                bool raid = i.InstanceType == 2;
                var lv = AtlasInstances.LevelText(i);
                var where = AtlasInstances.Where(i);
                items.Add(new Dictionary<string, object>
                {
                    ["lat"] = lat, ["lng"] = lng, ["name"] = i.Name, ["ic"] = raid ? "raid" : "dun", ["g"] = raid ? "instances.raids" : "instances.dungeons",
                    ["lab"] = lv.Length > 0 ? i.Name + " · " + lv : i.Name,
                    ["tip"] = AtlasInstances.Summary(i), ["extra"] = (where.Length > 0 ? where + " · " : "") + "click to open its page",
                    ["link"] = "instance:" + i.MapId,
                });
            }
            string note = items.Count == 0 ? (idx.Instances.Any(i => i.Entrance != null) ? "No dungeon or raid has its entrance on this continent." : "The game files give no entrance positions.") : "";
            return Message("instances", "markers", new Dictionary<string, object> { ["items"] = items }, items.Count, note);
        }

        // ---------------------------------------------------------------- gathering nodes
        sealed class PointSet
        {
            public readonly List<double> Pts = new List<double>();
            public readonly List<string> Names = new List<string>(), Tips = new List<string>(), Links = new List<string>();
            readonly Dictionary<string, int> n = new Dictionary<string, int>(), t = new Dictionary<string, int>(), l = new Dictionary<string, int>();
            public int Count => Pts.Count / 6;
            static int Idx(List<string> list, Dictionary<string, int> map, string s) { if (!map.TryGetValue(s, out var i)) { i = list.Count; map[s] = i; list.Add(s); } return i; }
            public bool Full => Count >= MaxPointsPerLayer;
            public void Add(double x, double y, string name, string tip, int style, string link)
            {
                if (Full || !WorldMapMath.InWorld(x, y)) return;
                LL(x, y, out var lat, out var lng);
                Pts.Add(lat); Pts.Add(lng); Pts.Add(Idx(Names, n, name ?? "")); Pts.Add(string.IsNullOrEmpty(tip) ? -1 : Idx(Tips, t, tip)); Pts.Add(style); Pts.Add(string.IsNullOrEmpty(link) ? -1 : Idx(Links, l, link));
            }
        }

        static Dictionary<string, object> Style(string color, string shape, bool forever, string label, string group = null)
        {
            var d = new Dictionary<string, object> { ["c"] = color, ["s"] = shape, ["f"] = forever, ["l"] = label };
            if (group != null) d["g"] = group;
            return d;
        }

        static LayerResult PointMessage(string id, PointSet ps, List<Dictionary<string, object>> styles, string note)
            => Message(id, "points", new Dictionary<string, object> { ["pts"] = ps.Pts, ["names"] = ps.Names, ["tips"] = ps.Tips, ["links"] = ps.Links, ["styles"] = styles }, ps.Count, note);

        public static LayerResult Nodes(int mapId)
        {
            var idx = MapData.Index;
            if (idx == null) return new LayerResult { Note = "The maps are not ready yet." };
            string[] groups = { GatherNodes.Herbs, GatherNodes.Ore, GatherNodes.Chests, GatherNodes.Fishing };
            string[] colors = { "#4ade80", "#d08a4e", "#f0b429", "#5aa9ff" }, shapes = { "circle", "square", "diamond", "triangle" };
            var styles = new List<Dictionary<string, object>>();
            for (int g = 0; g < groups.Length; g++)
            {
                styles.Add(Style(colors[g], shapes[g], true, GatherNodes.GroupTitle(groups[g]) + " · " + AtlasLabel.Forever, "nodes." + groups[g]));
                styles.Add(Style(colors[g], shapes[g], false, GatherNodes.GroupTitle(groups[g]) + " · " + AtlasLabel.Classic, "nodes." + groups[g]));
            }
            styles.Add(Style("#38bdf8", "circle", true, "Fishing spots · " + AtlasLabel.Forever, "nodes.fishing"));
            var ps = new PointSet();
            int forever = 0, classic = 0;
            var src = new LiveNodeSource();
            // where players stood when a catch was looted (the Lodge's merged spots and this PC's own)
            foreach (var ui in GatherNodes.FishSpotMaps())
                foreach (var p in GatherNodes.FishSpotsOn(ui).Take(AtlasQuery.MaxNodeMarkersPerObject))
                {
                    var w = idx.MapToWorld(ui, p.X, p.Y);
                    if (w == null || w.Value.MapId != mapId) continue;
                    ps.Add(w.Value.X, w.Value.Y, "Fishing spot", AtlasQuery.FishSpotTip(p), groups.Length * 2, null); forever++;
                }
            var groupOf = new Dictionary<int, string>();
            string GroupOf(int id) { if (!groupOf.TryGetValue(id, out var g)) groupOf[id] = g = AtlasBrowse.ObjectGroupOf(id) ?? ""; return g; }
            // what players looted: the Lodge's merged nodes and this PC's own (positions are fractions of a zone map)
            foreach (var oid in GatherNodes.ObjectsWithNodes().ToList())
            {
                int gi = Array.IndexOf(groups, GroupOf(oid)); if (gi < 0) continue;
                var name = AtlasQuery.ObjectName(oid);
                foreach (var ui in src.ForeverMaps(oid))
                    foreach (var nd in src.Forever(oid, ui))
                    {
                        var w = idx.MapToWorld(ui, nd.X, nd.Y);
                        if (w == null || w.Value.MapId != mapId) continue;
                        ps.Add(w.Value.X, w.Value.Y, name, AtlasQuery.NodeTip(nd.N), gi * 2, "obj:" + oid); forever++;
                    }
            }
            // the Classic baseline's spawn points (world positions as they are)
            var cl = ClassicAtlas.Current;
            if (cl != null)
                foreach (var o in cl.Objects.Values.Where(o => o.Type == 3 || o.Type == 25).OrderBy(o => o.Id))
                {
                    if (ps.Full) break;
                    var sp = o.Spawns; if (sp == null || sp.Length == 0 || !sp.Any(s => s.Map == mapId)) continue;
                    int gi = Array.IndexOf(groups, GroupOf(o.Id)); if (gi < 0) continue;
                    foreach (var s in sp) { if (s.Map != mapId) continue; ps.Add(s.X, s.Y, o.Name, AtlasLabel.Classic, gi * 2 + 1, "obj:" + o.Id); classic++; }
                }
            string note = ps.Full ? "Showing the first " + MaxPointsPerLayer.ToString("N0", CultureInfo.InvariantCulture) + " places." : "";
            if (ps.Count == 0) note = cl == null ? "No nodes yet: looted nodes come from this PC and the Lodge, the other spawn points from the Classic baseline (Atlas start page)." : "No nodes known on this continent.";
            else if (cl == null) note = "Looted nodes only; the Classic baseline's spawn points need the Classic data (Atlas start page).";
            return PointMessage("nodes", ps, styles, note);
        }

        // ---------------------------------------------------------------- saved routes
        public static LayerResult Saved(int mapId)
        {
            var idx = MapData.Index;
            if (idx == null) return new LayerResult { Note = "The maps are not ready yet." };
            var items = new List<object>();
            IEnumerable<SavedRoute> all = null;
            try { all = SavedRoutes?.Invoke(); } catch (Exception) { }
            foreach (var r in (all ?? Enumerable.Empty<SavedRoute>()).Where(r => r != null))
            {
                var nodes = r.ToNodes(); if (nodes.Count < 2) continue;
                var line = new List<double>(); bool onMap = true;
                foreach (var nd in nodes)
                {
                    var w = idx.MapToWorld(r.Zone, nd.X, nd.Y);
                    if (w == null || w.Value.MapId != mapId) { onMap = false; break; }
                    LL(w.Value.X, w.Value.Y, out var lat, out var lng); line.Add(lat); line.Add(lng);
                }
                if (!onMap || line.Count < 4) continue;
                line.Add(line[0]); line.Add(line[1]);        // a loop
                items.Add(new Dictionary<string, object>
                {
                    ["pts"] = line, ["c"] = "#22d3ee", ["w"] = 3.0, ["o"] = 0.9,
                    ["name"] = r.Name ?? "Route", ["tip"] = nodes.Count + " stops · " + idx.Name(r.Zone), ["extra"] = "Saved route",
                    ["label"] = r.Name ?? "", ["mid"] = new List<double> { line[0], line[1] }, ["labelZ"] = 4.5,
                });
            }
            return Message("saved", "lines", new Dictionary<string, object> { ["items"] = items }, items.Count, items.Count == 0 ? "You have no saved gathering routes on this continent. Build one on a zone's page in the Atlas." : "");
        }

        // ---------------------------------------------------------------- creatures
        public static LayerResult Npcs(int mapId, string kind)
        {
            var idx = MapData.Index;
            if (idx == null) return new LayerResult { Note = "The maps are not ready yet." };
            string label, color, shape;
            switch (kind)
            {
                case "vendors": label = "Vendor"; color = "#f0b429"; shape = "square"; break;
                case "trainers": label = "Trainer"; color = "#a78bfa"; shape = "triangle"; break;
                case "quests": label = "Quest giver"; color = "#fde047"; shape = "diamond"; break;
                default: label = "Rare"; color = "#e5e7eb"; shape = "star"; break;
            }
            var styles = new List<Dictionary<string, object>> { Style(color, shape, true, label + " · " + AtlasLabel.Forever), Style(color, shape, false, label + " · " + AtlasLabel.Classic) };
            var ps = new PointSet();
            var cl = ClassicAtlas.Current;
            var dv = AtlasDerived.For(cl);
            var lodge = AtlasQuery.LodgeOrNull();
            var have = new HashSet<int>();
            // what the Lodge saw comes first (drawn under, but counted for the "also in the baseline" skip)
            if (lodge != null)
            {
                if (kind == "vendors")
                    foreach (var kv in lodge.AllVendors)
                    {
                        var w = idx.MapToWorld(kv.Value.Zone, kv.Value.X, kv.Value.Y);
                        if (w == null || w.Value.MapId != mapId) continue;
                        ps.Add(w.Value.X, w.Value.Y, AtlasQuery.NpcName(kv.Key), "Vendor", 0, "npc:" + kv.Key); have.Add(kv.Key);
                    }
                else if (kind == "rares")
                    foreach (var kv in lodge.AllSpawns)
                    {
                        var sn = SeenData.Npc(kv.Key); int rank = sn?.Rank ?? lodge.Npc(kv.Key)?.Rank ?? 0;
                        if (rank != 2 && rank != 4) continue;
                        foreach (var sp in kv.Value)
                        {
                            var w = idx.MapToWorld(sp.Zone, sp.X, sp.Y);
                            if (w == null || w.Value.MapId != mapId) continue;
                            ps.Add(w.Value.X, w.Value.Y, AtlasQuery.NpcName(kv.Key), JoinTip(rank == 2 ? "Rare elite" : "Rare", AtlasQuery.NoteTip(kv.Key)), 0, "npc:" + kv.Key); have.Add(kv.Key);
                        }
                    }
                if (kind == "rares")
                    foreach (var nt in AtlasQuery.RareNotes())          // Hunter Helper's field notes: rares and where hunters saw them
                    {
                        if (have.Contains(nt.Id)) continue;
                        var ntip = JoinTip(nt.Class == "rareelite" ? "Rare elite" : "Rare", AtlasQuery.NoteTip(nt.Id));
                        foreach (var kv in nt.Pts)
                            foreach (var p in kv.Value)
                            {
                                var w = idx.MapToWorld(kv.Key, p.X, p.Y);
                                if (w == null || w.Value.MapId != mapId) continue;
                                ps.Add(w.Value.X, w.Value.Y, AtlasQuery.NpcName(nt.Id), ntip, 0, "npc:" + nt.Id); have.Add(nt.Id);
                            }
                    }
            }
            if (cl != null)
                foreach (var n in cl.Npcs.Values.OrderBy(n => n.Id))
                {
                    if (ps.Full) break;
                    if (n.Spawns == null || n.Spawns.Length == 0) continue;
                    bool match;
                    switch (kind)
                    {
                        case "vendors": match = n.IsVendor; break;
                        case "trainers": match = (n.NpcFlags & 0x10) != 0; break;
                        case "quests": match = n.IsQuestGiver && dv != null && dv.QuestsGivenBy(false, n.Id).Count > 0; break;
                        default: match = n.Rank == 2 || n.Rank == 4; break;
                    }
                    if (!match || have.Contains(n.Id)) continue;
                    var lv = AtlasFormat.Levels(n.MinLevel, n.MaxLevel);
                    var tip = string.Join(" · ", new[] { lv.Length > 0 ? "Level " + lv : "", kind == "rares" ? n.RankName : (n.SubName ?? ""), kind == "rares" ? AtlasQuery.NoteTip(n.Id) : "" }.Where(x => x.Length > 0));
                    foreach (var s in n.Spawns) if (s.Map == mapId) ps.Add(s.X, s.Y, n.Name, tip.Length > 0 ? tip : label, 1, "npc:" + n.Id);
                }
            string note = ps.Full ? "Showing the first " + MaxPointsPerLayer.ToString("N0", CultureInfo.InvariantCulture) + " places." : "";
            if (ps.Count == 0) note = cl == null ? "This layer needs the Classic baseline (Atlas start page)." : "None known on this continent.";
            else if (cl == null) note = "Only what the Lodge saw; the rest needs the Classic baseline (Atlas start page).";
            return PointMessage(kind, ps, styles, note);
        }

        static string JoinTip(string a, string b) => string.IsNullOrEmpty(b) ? a : a + " · " + b;

        // ---------------------------------------------------------------- Lodge finds
        public static LayerResult Finds(int mapId)
        {
            var idx = MapData.Index;
            if (idx == null) return new LayerResult { Note = "The maps are not ready yet." };
            var lodge = AtlasQuery.LodgeOrNull();
            var styles = new List<Dictionary<string, object>> { Style("#f472b6", "diamond", true, "Find · " + AtlasLabel.Forever) };
            var ps = new PointSet();
            if (lodge != null)
                foreach (var f in lodge.AllFinds.OrderByDescending(f => f.Seq))
                {
                    if (AtlasQuery.ItemQuality(f.Item) < 2) continue;
                    var w = idx.MapToWorld(f.Zone, f.X, f.Y);
                    if (w == null || w.Value.MapId != mapId) continue;
                    ps.Add(w.Value.X, w.Value.Y, AtlasQuery.ItemName(f.Item), "Found by " + (string.IsNullOrEmpty(f.By) ? "a member" : f.By), 0, "item:" + f.Item);
                }
            return PointMessage("finds", ps, styles, ps.Count == 0 ? (lodge == null ? "You are not connected to a Lodge." : "No finds on this continent yet.") : "");
        }
    }
}
