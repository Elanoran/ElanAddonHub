using System.Text.Json.Nodes;

namespace Lodge;

// Fishing spots (2.21, sub-feature "atlas4"). Elan's Toolkit records where a member stood when a catch was looted: per map a list of spots
// { x, y (fractions of the map, 4 decimals), n = catches there, items = { itemId: count caught } }. A share carries a member's CUMULATIVE spots
// for the maps that changed: { "<uiMapId>": [ { x, y, n, items? }, ... ] }. The server keeps them per member and shows everybody the merge:
//   * two spots on one map within FishMerge (Euclidean, map fraction) are ONE spot;
//   * per member the larger n (and the larger count per item) of its points in that spot, summed over members;
//   * at most MaxFishSpotsPerMap (300) per map, the most caught first.
// Idempotency: a member's own spot within FishMerge of a stored one is the same spot and keeps the max of stored and sent, so a resend never counts twice.
public static partial class Atlas
{
    public const double FishMerge = 0.01;
    public const int MaxFishMapsPerMember = 60, MaxFishSpotsPerMapPerMember = 150, MaxFishSpotsPerMap = 300, MaxFishSpotsPerMember = 5000, MaxFishItemsPerSpot = 50;
    public const int MaxFishMapsPerShare = 20, MaxFishSpotsPerShare = 600;

    public sealed class FishSpot
    {
        public double X, Y; public long N;
        public Dictionary<int, long> Items = new();
        public FishSpot Clone() => new() { X = X, Y = Y, N = N, Items = new Dictionary<int, long>(Items) };
    }

    public static bool SameFish(FishSpot a, double x, double y) => (a.X - x) * (a.X - x) + (a.Y - y) * (a.Y - y) <= FishMerge * FishMerge + 1e-12;

    // one map's spots (a JSON array); all-or-nothing
    public static bool ParseFishMap(JsonNode node, out List<FishSpot> spots)
    {
        spots = null;
        if (node is not JsonArray a || a.Count == 0 || a.Count > MaxFishSpotsPerMapPerMember) return false;
        var l = new List<FishSpot>();
        foreach (var e in a)
        {
            if (e is not JsonObject o || !Coord4(o["x"], out var x) || !Coord4(o["y"], out var y) || !Whole(o["n"], 1, MaxNodeCount, out var n)) return false;
            var sp = new FishSpot { X = x, Y = y, N = n };
            if (o["items"] != null)
            {
                if (o["items"] is not JsonObject io || io.Count > MaxFishItemsPerSpot) return false;
                foreach (var (k, v) in io)
                {
                    if (!Key(k, MaxItemId, out var id) || !Whole(v, 1, MaxNodeCount, out var c)) return false;
                    sp.Items[id] = c;
                }
            }
            var same = l.FirstOrDefault(p => SameFish(p, x, y));
            if (same != null) MergeSpot(same, sp); else l.Add(sp);
        }
        spots = l;
        return true;
    }

    static bool MergeSpot(FishSpot into, FishSpot from)
    {
        bool changed = false;
        if (from.N > into.N) { into.N = from.N; changed = true; }
        foreach (var (id, c) in from.Items)
            if (!into.Items.TryGetValue(id, out var have) ? into.Items.Count < MaxFishItemsPerSpot : c > have) { into.Items[id] = c; changed = true; }
        return changed;
    }

    // merge incoming spots into one member's list for one map; true when anything changed, `full` when a new spot did not fit
    public static bool MergeFish(List<FishSpot> have, List<FishSpot> incoming, out bool full)
    {
        bool changed = false; full = false;
        foreach (var p in incoming)
        {
            var same = have.FirstOrDefault(h => SameFish(h, p.X, p.Y));
            if (same != null) { if (MergeSpot(same, p)) changed = true; continue; }
            if (have.Count >= MaxFishSpotsPerMapPerMember) { full = true; continue; }
            have.Add(p.Clone()); changed = true;
        }
        return changed;
    }

    // the merged spots of one map over all members' lists; deterministic (most caught first, then member, x, y)
    public static List<FishSpot> AggregateFish(IEnumerable<(string who, List<FishSpot> spots)> lists)
    {
        var all = new List<(string who, FishSpot p)>();
        foreach (var (who, spots) in lists) foreach (var p in spots) all.Add((who, p));
        all.Sort((a, b) =>
        {
            int c = b.p.N.CompareTo(a.p.N); if (c != 0) return c;
            c = string.CompareOrdinal(a.who, b.who); if (c != 0) return c;
            c = a.p.X.CompareTo(b.p.X); return c != 0 ? c : a.p.Y.CompareTo(b.p.Y);
        });
        var seeds = new List<FishSpot>(); var perMember = new List<Dictionary<string, long>>(); var perItem = new List<Dictionary<int, Dictionary<string, long>>>();
        foreach (var (who, p) in all)
        {
            int idx = seeds.FindIndex(s => SameFish(s, p.X, p.Y));
            if (idx < 0) { seeds.Add(new FishSpot { X = p.X, Y = p.Y }); perMember.Add(new(StringComparer.Ordinal)); perItem.Add(new()); idx = seeds.Count - 1; }
            var d = perMember[idx];
            d[who] = d.TryGetValue(who, out var old) ? Math.Max(old, p.N) : p.N;
            foreach (var (id, c) in p.Items)
            {
                if (!perItem[idx].TryGetValue(id, out var byWho)) perItem[idx][id] = byWho = new(StringComparer.Ordinal);
                byWho[who] = byWho.TryGetValue(who, out var o2) ? Math.Max(o2, c) : c;
            }
        }
        for (int i = 0; i < seeds.Count; i++)
        {
            seeds[i].N = Math.Min(MaxNodeCount * 2000, perMember[i].Values.Sum());
            foreach (var (id, m) in perItem[i].OrderByDescending(k => k.Value.Values.Sum()).ThenBy(k => k.Key).Take(MaxFishItemsPerSpot))
                seeds[i].Items[id] = Math.Min(MaxNodeCount * 2000, m.Values.Sum());
        }
        return seeds.OrderByDescending(s => s.N).ThenBy(s => s.X).ThenBy(s => s.Y).Take(MaxFishSpotsPerMap).ToList();
    }
}
