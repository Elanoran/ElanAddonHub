using System;
using System.Collections.Generic;
using System.Linq;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services.Atlas.Classic;
using ElansAddonHub.Services.Maps;

namespace ElansAddonHub.Services.Atlas
{
    // Where the gathering nodes of an object are, per zone map. Two layers, never mixed up:
    //   Forever  = positions players looted (the Lodge's merged nodes and this PC's own Hub observations, labelled "Forever"),
    //   Classic  = the Classic baseline's spawn positions (labelled "Classic baseline").
    public interface INodeSource
    {
        List<GNode> Forever(int objectId, int uiMapId);
        List<GNode> Classic(int objectId, int uiMapId);
        List<int> ForeverMaps(int objectId);      // maps with Forever nodes, most nodes first
        List<int> ClassicMaps(int objectId);
        string ObjectName(int objectId);
        string GroupOf(int objectId);             // herbs | ore | chests | fishing | other
        (double W, double H) MapYards(int uiMapId);   // the map's size in yards; (0, 0) = unknown
    }

    public static class GatherNodes
    {
        // "Show route" from the gathering guide: the zone page opens with the Routes panel showing a loop through these objects (consumed once)
        public sealed class RouteRequest { public int Zone; public List<int> Objects = new List<int>(); }
        public static RouteRequest Pending;
        public static RouteRequest TakePending(int zone)
        {
            var p = Pending;
            if (p == null || p.Zone != zone) return null;
            Pending = null;
            return p;
        }

        public const string Herbs = "herbs", Ore = "ore", Chests = "chests", Fishing = "fishing", Other = "other";
        public static readonly string[] Groups = { Herbs, Ore, Chests, Fishing, Other };
        public static string GroupTitle(string g)
        {
            switch (g) { case Herbs: return "Herbs"; case Ore: return "Ore"; case Chests: return "Chests"; case Fishing: return "Fishing pools"; default: return "Other"; }
        }

        // Lodge + own: the Lodge's merged list already holds what this PC shared, so a node of mine that sits on a Lodge node is the same node
        // (the larger count of the two counts), one that sits on none is added.
        public static List<AtlasObs.NodePt> Merge(IReadOnlyList<AtlasObs.NodePt> own, IReadOnlyList<AtlasObs.NodePt> lodge)
        {
            var res = new List<AtlasObs.NodePt>(lodge ?? new AtlasObs.NodePt[0]);
            if (own != null)
                foreach (var o in own)
                {
                    int i = res.FindIndex(p => AtlasObs.SameNode(p, o.X, o.Y));
                    if (i >= 0) { var p = res[i]; if (o.N > p.N) { p.N = o.N; res[i] = p; } }
                    else res.Add(o);
                }
            return res;
        }

        // what the map's size in yards is: the world span of half the picture, doubled (x runs north, y runs west in the game's coordinates)
        public static (double W, double H) YardsOf(MapIndex idx, int uiMapId)
        {
            try
            {
                var a = idx?.MapToWorld(uiMapId, 0.25, 0.5); var b = idx?.MapToWorld(uiMapId, 0.75, 0.5);
                var c = idx?.MapToWorld(uiMapId, 0.5, 0.25); var d = idx?.MapToWorld(uiMapId, 0.5, 0.75);
                if (a == null || b == null || c == null || d == null || a.Value.MapId != b.Value.MapId || c.Value.MapId != d.Value.MapId) return (0, 0);
                double w = Math.Abs(a.Value.Y - b.Value.Y) * 2, h = Math.Abs(c.Value.X - d.Value.X) * 2;
                return w > 1 && h > 1 && !double.IsNaN(w) && !double.IsNaN(h) ? (w, h) : (0, 0);
            }
            catch (Exception) { return (0, 0); }
        }

        // a stable text that changes when this PC's own observed nodes change (for caches that depend on them)
        public static string OwnToken()
        {
            var o = Own();
            if (o == null) return "-";
            return o.Count + ":" + o.Values.Sum(m => m.Values.Sum(l => l.Count)) + ":" + o.Values.Sum(m => m.Values.Sum(l => l.Sum(p => p.N)));
        }

        public static Dictionary<int, Dictionary<int, List<AtlasObs.NodePt>>> Own()
        {
            try { return AtlasQuery.Context.OwnNodes?.Invoke(); } catch (Exception) { return null; }
        }

        // object ids that have Forever nodes anywhere (own or Lodge)
        public static IEnumerable<int> ObjectsWithNodes()
        {
            var ids = new HashSet<int>();
            var own = Own(); if (own != null) foreach (var k in own.Keys) ids.Add(k);
            var l = AtlasQuery.LodgeOrNull(); if (l != null) foreach (var k in l.NodeObjectIds) ids.Add(k);
            return ids.OrderBy(i => i);
        }

        // the objects that have Forever nodes on one map, with how many (most first)
        public static List<KeyValuePair<int, int>> ObjectsOnMap(int uiMapId)
        {
            var res = new List<KeyValuePair<int, int>>();
            var src = new LiveNodeSource();
            foreach (var id in ObjectsWithNodes()) { int n = src.Forever(id, uiMapId).Count; if (n > 0) res.Add(new KeyValuePair<int, int>(id, n)); }
            return res.OrderByDescending(k => k.Value).ThenBy(k => k.Key).ToList();
        }
    }

    public sealed class LiveNodeSource : INodeSource
    {
        readonly Dictionary<int, Dictionary<int, List<GNode>>> classic = new Dictionary<int, Dictionary<int, List<GNode>>>();

        static List<GNode> Conv(IEnumerable<AtlasObs.NodePt> l, int obj) => l.Select(p => new GNode(p.X, p.Y, p.N, true, obj)).ToList();

        public List<GNode> Forever(int objectId, int uiMapId)
        {
            var own = GatherNodes.Own();
            IReadOnlyList<AtlasObs.NodePt> mine = null;
            if (own != null && own.TryGetValue(objectId, out var om) && om.TryGetValue(uiMapId, out var ol)) mine = ol;
            var lodge = AtlasQuery.LodgeOrNull()?.Nodes(objectId, uiMapId);
            return Conv(GatherNodes.Merge(mine, lodge), objectId);
        }

        public List<int> ForeverMaps(int objectId)
        {
            var maps = new Dictionary<int, int>();
            var own = GatherNodes.Own();
            if (own != null && own.TryGetValue(objectId, out var om)) foreach (var kv in om) maps[kv.Key] = kv.Value.Count;
            var l = AtlasQuery.LodgeOrNull();
            if (l != null) foreach (var kv in l.NodesOf(objectId)) maps[kv.Key] = Math.Max(maps.TryGetValue(kv.Key, out var c) ? c : 0, kv.Value.Count);
            return maps.OrderByDescending(k => k.Value).ThenBy(k => k.Key).Select(k => k.Key).ToList();
        }

        Dictionary<int, List<GNode>> ClassicOf(int objectId)
        {
            lock (classic)
            {
                if (classic.TryGetValue(objectId, out var d)) return d;
                d = new Dictionary<int, List<GNode>>();
                var idx = MapData.Index; var spawns = ClassicAtlas.Current?.ObjectSpawns(objectId);
                if (idx != null && spawns != null)
                    foreach (var s in spawns)
                    {
                        var p = idx.WorldToMap(s.Map, s.X, s.Y);
                        if (p == null) continue;
                        if (!d.TryGetValue(p.Value.UiMapId, out var l)) d[p.Value.UiMapId] = l = new List<GNode>();
                        l.Add(new GNode(Math.Round(p.Value.Fx, 4), Math.Round(p.Value.Fy, 4), 0, false, objectId));
                    }
                classic[objectId] = d;
                return d;
            }
        }

        public List<GNode> Classic(int objectId, int uiMapId) => ClassicOf(objectId).TryGetValue(uiMapId, out var l) ? new List<GNode>(l) : new List<GNode>();
        public List<int> ClassicMaps(int objectId) => ClassicOf(objectId).OrderByDescending(k => k.Value.Count).ThenBy(k => k.Key).Select(k => k.Key).ToList();
        public string ObjectName(int objectId) => AtlasQuery.ObjectName(objectId);
        public string GroupOf(int objectId) => AtlasBrowse.ObjectGroupOf(objectId);
        public (double W, double H) MapYards(int uiMapId) => GatherNodes.YardsOf(MapData.Index, uiMapId);
    }

    // a hand-made source for the self-test (and for a PC without any data)
    public sealed class FixedNodeSource : INodeSource
    {
        public readonly Dictionary<int, Dictionary<int, List<GNode>>> ForeverNodes = new Dictionary<int, Dictionary<int, List<GNode>>>();
        public readonly Dictionary<int, Dictionary<int, List<GNode>>> ClassicNodes = new Dictionary<int, Dictionary<int, List<GNode>>>();
        public readonly Dictionary<int, string> Names = new Dictionary<int, string>(), GroupsOf = new Dictionary<int, string>();
        public readonly Dictionary<int, (double, double)> Yards = new Dictionary<int, (double, double)>();

        public FixedNodeSource AddForever(int obj, int map, params GNode[] nodes) { Put(ForeverNodes, obj, map, nodes, true); return this; }
        public FixedNodeSource AddClassic(int obj, int map, params GNode[] nodes) { Put(ClassicNodes, obj, map, nodes, false); return this; }
        static void Put(Dictionary<int, Dictionary<int, List<GNode>>> d, int obj, int map, GNode[] nodes, bool forever)
        {
            if (!d.TryGetValue(obj, out var m)) d[obj] = m = new Dictionary<int, List<GNode>>();
            if (!m.TryGetValue(map, out var l)) m[map] = l = new List<GNode>();
            foreach (var n in nodes) { var x = n; x.Object = obj; x.Forever = forever; if (!forever) x.N = 0; l.Add(x); }
        }

        public List<GNode> Forever(int objectId, int uiMapId) => ForeverNodes.TryGetValue(objectId, out var m) && m.TryGetValue(uiMapId, out var l) ? new List<GNode>(l) : new List<GNode>();
        public List<GNode> Classic(int objectId, int uiMapId) => ClassicNodes.TryGetValue(objectId, out var m) && m.TryGetValue(uiMapId, out var l) ? new List<GNode>(l) : new List<GNode>();
        public List<int> ForeverMaps(int objectId) => ForeverNodes.TryGetValue(objectId, out var m) ? m.OrderByDescending(k => k.Value.Count).ThenBy(k => k.Key).Select(k => k.Key).ToList() : new List<int>();
        public List<int> ClassicMaps(int objectId) => ClassicNodes.TryGetValue(objectId, out var m) ? m.OrderByDescending(k => k.Value.Count).ThenBy(k => k.Key).Select(k => k.Key).ToList() : new List<int>();
        public string ObjectName(int objectId) => Names.TryGetValue(objectId, out var n) ? n : "Object " + objectId;
        public string GroupOf(int objectId) => GroupsOf.TryGetValue(objectId, out var g) ? g : GatherNodes.Other;
        public (double W, double H) MapYards(int uiMapId) => Yards.TryGetValue(uiMapId, out var y) ? y : (0, 0);
    }

    // a route for a set of objects in one zone
    public static class GatherRoutes
    {
        public static RouteResult Build(INodeSource src, IEnumerable<int> objectIds, int uiMapId)
        {
            var forever = new List<GNode>(); var classic = new List<GNode>();
            foreach (var id in (objectIds ?? Enumerable.Empty<int>()).Distinct())
            {
                forever.AddRange(src.Forever(id, uiMapId));
                classic.AddRange(src.Classic(id, uiMapId));
            }
            var y = src.MapYards(uiMapId);
            return RouteBuilder.Build(forever, classic, y.W, y.H);
        }

        // the zones where a set of objects has nodes (Forever first, most nodes first)
        public static List<int> ZonesOf(INodeSource src, IEnumerable<int> objectIds)
        {
            var score = new Dictionary<int, long>();
            foreach (var id in objectIds.Distinct())
            {
                foreach (var z in src.ForeverMaps(id)) score[z] = (score.TryGetValue(z, out var s) ? s : 0) + 1_000_000L + src.Forever(id, z).Count;
                foreach (var z in src.ClassicMaps(id)) score[z] = (score.TryGetValue(z, out var s) ? s : 0) + src.Classic(id, z).Count;
            }
            return score.OrderByDescending(k => k.Value).ThenBy(k => k.Key).Select(k => k.Key).ToList();
        }
    }

    // ---- saved routes (Settings): named loops per zone, and which one the in-game guide uses
    [System.Runtime.Serialization.DataContract]
    public sealed class SavedRoute
    {
        [System.Runtime.Serialization.DataMember(Name = "id")] public string Id { get; set; }
        [System.Runtime.Serialization.DataMember(Name = "name")] public string Name { get; set; }
        [System.Runtime.Serialization.DataMember(Name = "zone")] public int Zone { get; set; }               // uiMapId
        [System.Runtime.Serialization.DataMember(Name = "objects")] public List<int> Objects { get; set; }   // what it was built for
        [System.Runtime.Serialization.DataMember(Name = "src")] public string Src { get; set; }              // f | c
        [System.Runtime.Serialization.DataMember(Name = "pts")] public List<double> Points { get; set; }     // x0, y0, x1, y1 ... in loop order (frozen when saved)
        [System.Runtime.Serialization.DataMember(Name = "at")] public long At { get; set; }

        public const int MaxSaved = 60, MaxNameLength = 40;

        public List<GNode> ToNodes()
        {
            var res = new List<GNode>();
            if (Points == null) return res;
            for (int i = 0; i + 1 < Points.Count && res.Count < RouteBuilder.MaxStops; i += 2)
            {
                double x = Points[i], y = Points[i + 1];
                if (double.IsNaN(x) || double.IsNaN(y) || x < 0 || x > 1 || y < 0 || y > 1) continue;
                res.Add(new GNode(Math.Round(x, 4), Math.Round(y, 4), 0, Src != "c"));
            }
            return res;
        }

        public static SavedRoute From(string name, int zone, IEnumerable<int> objects, RouteResult r, long at)
        {
            var pts = new List<double>();
            foreach (var p in r.Points.Take(RouteBuilder.MaxStops)) { pts.Add(Math.Round(p.X, 4)); pts.Add(Math.Round(p.Y, 4)); }
            return new SavedRoute
            {
                Id = Guid.NewGuid().ToString("N").Substring(0, 10), Name = CleanName(name), Zone = zone, Objects = objects.Distinct().OrderBy(i => i).ToList(),
                Src = r.Src, Points = pts, At = at,
            };
        }

        public static string CleanName(string s)
        {
            var t = new string((s ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
            if (t.Length > MaxNameLength) t = t.Substring(0, MaxNameLength).TrimEnd();
            return t.Length == 0 ? "Route" : t;
        }
    }
}
