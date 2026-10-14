using System.Text.Json.Nodes;

namespace Lodge;

// Gathering / treasure node positions (2.18, sub-feature "atlas3"). Elan's Toolkit records where a member looted a herb, vein, chest or fishing pool
// (object id, uiMapId, x and y as fractions of the map, n = times looted there). A share carries a member's CUMULATIVE nodes for the objects that
// changed: { "<objectId>": { "<uiMapId>": [ { x, y, n }, ... ] } }. The server keeps them per member and shows everybody the merge:
//   * two nodes of one object on one map within NodeMerge (Euclidean, map fraction) are ONE node;
//   * per member the larger n of its points in that node, summed over members;
//   * at most MaxNodesPerObjectMap (300) per object per map, the most looted first.
// Idempotency: a member's own point within NodeMerge of a stored one is the same node and keeps max(stored n, sent n), so a resend never counts twice.
public static partial class Atlas
{
    public const double NodeMerge = 0.008;
    public const int MaxNodeObjectsPerMember = 400, MaxNodeMapsPerObject = 40, MaxNodesPerObjectMapPerMember = 150, MaxNodesPerObjectMap = 300;
    public const int MaxNodesPerMember = 20_000;
    public const long MaxNodeCount = 100_000;
    public const int MaxNodeObjectsPerShare = 50, MaxNodePointsPerShare = 1000;

    public sealed class NodePt
    {
        public double X, Y; public long N;
        public NodePt Clone() => new() { X = X, Y = Y, N = N };
    }

    // a coordinate with 4 decimals (nodes are finer than the other positions)
    public static bool Coord4(JsonNode n, out double v)
    {
        if (!Dbl(n, out v) || v < 0 || v > 1) return false;
        v = Math.Round(v, 4, MidpointRounding.AwayFromZero); return true;
    }

    public static bool SameNode(NodePt a, double x, double y) => (a.X - x) * (a.X - x) + (a.Y - y) * (a.Y - y) <= NodeMerge * NodeMerge + 1e-12;

    // one object: { map: [points] }; all-or-nothing
    public static bool ParseNodeObject(JsonNode node, out Dictionary<int, List<NodePt>> maps, out int points)
    {
        maps = null; points = 0;
        if (node is not JsonObject mo || mo.Count == 0 || mo.Count > MaxNodeMapsPerObject) return false;
        var res = new Dictionary<int, List<NodePt>>();
        foreach (var (mk, arr) in mo)
        {
            if (!Key(mk, MaxMapId, out var map) || arr is not JsonArray a || a.Count == 0 || a.Count > MaxNodesPerObjectMapPerMember) return false;
            var l = new List<NodePt>();
            foreach (var e in a)
            {
                if (e is not JsonObject o || !Coord4(o["x"], out var x) || !Coord4(o["y"], out var y) || !Whole(o["n"], 1, MaxNodeCount, out var n)) return false;
                var same = l.FirstOrDefault(p => SameNode(p, x, y));
                if (same != null) same.N = Math.Max(same.N, n); else l.Add(new NodePt { X = x, Y = y, N = n });
            }
            res[map] = l; points += l.Count;
        }
        maps = res;
        return true;
    }

    // merge incoming points into one member's list for one object / map; returns true when anything changed. False in `full` when a new point did not fit.
    public static bool MergeNodes(List<NodePt> have, List<NodePt> incoming, out bool full)
    {
        bool changed = false; full = false;
        foreach (var p in incoming)
        {
            var same = have.FirstOrDefault(h => SameNode(h, p.X, p.Y));
            if (same != null) { if (p.N > same.N) { same.N = p.N; changed = true; } continue; }
            if (have.Count >= MaxNodesPerObjectMapPerMember) { full = true; continue; }
            have.Add(p.Clone()); changed = true;
        }
        return changed;
    }

    // The merged nodes of one object on one map over all members' lists. Deterministic: points are taken most looted first (then member, x, y), a point joins the first
    // node whose seed is within NodeMerge; per node the max n per member, summed.
    public static List<NodePt> AggregateNodes(IEnumerable<(string who, List<NodePt> pts)> lists)
    {
        var all = new List<(string who, NodePt p)>();
        foreach (var (who, pts) in lists) foreach (var p in pts) all.Add((who, p));
        all.Sort((a, b) =>
        {
            int c = b.p.N.CompareTo(a.p.N); if (c != 0) return c;
            c = string.CompareOrdinal(a.who, b.who); if (c != 0) return c;
            c = a.p.X.CompareTo(b.p.X); return c != 0 ? c : a.p.Y.CompareTo(b.p.Y);
        });
        var seeds = new List<NodePt>(); var perMember = new List<Dictionary<string, long>>();
        foreach (var (who, p) in all)
        {
            int idx = seeds.FindIndex(s => SameNode(s, p.X, p.Y));
            if (idx < 0) { seeds.Add(new NodePt { X = p.X, Y = p.Y }); perMember.Add(new Dictionary<string, long>(StringComparer.Ordinal)); idx = seeds.Count - 1; }
            var d = perMember[idx];
            d[who] = d.TryGetValue(who, out var old) ? Math.Max(old, p.N) : p.N;
        }
        for (int i = 0; i < seeds.Count; i++) seeds[i].N = Math.Min(MaxNodeCount * 2000, perMember[i].Values.Sum());
        return seeds.OrderByDescending(s => s.N).ThenBy(s => s.X).ThenBy(s => s.Y).Take(MaxNodesPerObjectMap).ToList();
    }
}
