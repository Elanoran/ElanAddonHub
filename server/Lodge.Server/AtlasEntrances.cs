using System.Globalization;
using System.Text.Json.Nodes;

namespace Lodge;

// Instance entrances (2.24, sub-feature "atlas6"). Elan's Toolkit notes, when a member enters a dungeon or raid, the last spot outdoors before the loading screen:
// per instance (its Map id) a list of spots { z (the zone's uiMapId), x, y (fractions of that map, 4 decimals), n = times entered from there }. A share carries a
// member's CUMULATIVE spots for the instances that changed: { "<instanceMapId>": [ { z, x, y, n }, ... ] }. The server keeps them per member and shows everybody the merge:
//   * two spots of one instance on the same zone map within EntranceMerge (Euclidean, map fraction) are ONE spot;
//   * per member the larger n of its points in that spot; n = the sum over members, m = the members who entered there;
//   * at most MaxEntranceSpots (8) per instance, the most members first (a summon or a teleport puts a stray spot somewhere: it stays a minority).
// Idempotency: a member's own spot within EntranceMerge of a stored one is the same spot and keeps the max of stored and sent, so a resend never counts twice.
public static partial class Atlas
{
    public const double EntranceMerge = 0.01;
    public const int MaxEntranceInstancesPerMember = 300, MaxEntranceSpotsPerInstancePerMember = 8, MaxEntranceSpots = 8, MaxEntranceInstancesPerShare = 60, MaxEntranceSpotsPerShare = 300;

    public sealed class EntranceSpot
    {
        public int Z; public double X, Y; public long N; public int M;
        public EntranceSpot Clone() => new() { Z = Z, X = X, Y = Y, N = N, M = M };
    }

    public static bool SameEntrance(EntranceSpot a, int z, double x, double y) => a.Z == z && (a.X - x) * (a.X - x) + (a.Y - y) * (a.Y - y) <= EntranceMerge * EntranceMerge + 1e-12;

    // one instance's spots (a JSON array); all-or-nothing
    public static bool ParseEntranceList(JsonNode node, out List<EntranceSpot> spots)
    {
        spots = null;
        if (node is not JsonArray a || a.Count == 0 || a.Count > MaxEntranceSpotsPerInstancePerMember) return false;
        var l = new List<EntranceSpot>();
        foreach (var e in a)
        {
            if (e is not JsonObject o || !Whole(o["z"], 1, MaxMapId, out var z) || !Coord4(o["x"], out var x) || !Coord4(o["y"], out var y) || !Whole(o["n"], 1, MaxNodeCount, out var n)) return false;
            var same = l.FirstOrDefault(p => SameEntrance(p, (int)z, x, y));
            if (same != null) same.N = Math.Max(same.N, n); else l.Add(new EntranceSpot { Z = (int)z, X = x, Y = y, N = n });
        }
        spots = l;
        return true;
    }

    // merge incoming spots into one member's list for one instance; true when anything changed, `full` when a new spot did not fit
    public static bool MergeEntrances(List<EntranceSpot> have, List<EntranceSpot> incoming, out bool full)
    {
        bool changed = false; full = false;
        foreach (var p in incoming)
        {
            var same = have.FirstOrDefault(h => SameEntrance(h, p.Z, p.X, p.Y));
            if (same != null) { if (p.N > same.N) { same.N = p.N; changed = true; } continue; }
            if (have.Count >= MaxEntranceSpotsPerInstancePerMember) { full = true; continue; }
            have.Add(p.Clone()); changed = true;
        }
        return changed;
    }

    // the merged spots of one instance over all members' lists; deterministic (most entries first, then member, map, x, y)
    public static List<EntranceSpot> AggregateEntrances(IEnumerable<(string who, List<EntranceSpot> spots)> lists)
    {
        var all = new List<(string who, EntranceSpot p)>();
        foreach (var (who, spots) in lists) foreach (var p in spots) all.Add((who, p));
        all.Sort((a, b) =>
        {
            int c = b.p.N.CompareTo(a.p.N); if (c != 0) return c;
            c = string.CompareOrdinal(a.who, b.who); if (c != 0) return c;
            c = a.p.Z.CompareTo(b.p.Z); if (c != 0) return c;
            c = a.p.X.CompareTo(b.p.X); return c != 0 ? c : a.p.Y.CompareTo(b.p.Y);
        });
        var seeds = new List<EntranceSpot>(); var perMember = new List<Dictionary<string, long>>();
        foreach (var (who, p) in all)
        {
            int idx = seeds.FindIndex(s => SameEntrance(s, p.Z, p.X, p.Y));
            if (idx < 0) { seeds.Add(new EntranceSpot { Z = p.Z, X = p.X, Y = p.Y }); perMember.Add(new(StringComparer.Ordinal)); idx = seeds.Count - 1; }
            var d = perMember[idx];
            d[who] = d.TryGetValue(who, out var old) ? Math.Max(old, p.N) : p.N;
        }
        for (int i = 0; i < seeds.Count; i++) { seeds[i].N = Math.Min(MaxNodeCount * 2000, perMember[i].Values.Sum()); seeds[i].M = perMember[i].Count; }
        return seeds.OrderByDescending(s => s.M).ThenByDescending(s => s.N).ThenBy(s => s.Z).ThenBy(s => s.X).ThenBy(s => s.Y).Take(MaxEntranceSpots).ToList();
    }

    public static JsonArray EntranceJson(List<EntranceSpot> spots, bool withMembers) => new(spots.Select(p =>
    {
        var o = new JsonObject { ["z"] = p.Z, ["x"] = p.X, ["y"] = p.Y, ["n"] = p.N };
        if (withMembers) o["m"] = p.M;
        return (JsonNode)o;
    }).ToArray());
}

public sealed partial class AtlasStore
{
    // the merged entrance spots of one instance (most members first)
    public sealed class EntranceAgg { public List<Atlas.EntranceSpot> Spots = new(); public long Rev; }

    readonly Dictionary<int, EntranceAgg> entranceAggs = new();

    public int EntranceInstanceCount { get { lock (Gate) return entranceAggs.Count; } }

    static void LoadEntrances(MemberData md, JsonObject eo)
    {
        foreach (var (mk, arr) in eo)
        {
            if (md.Entrances.Count >= Atlas.MaxEntranceInstancesPerMember || !Atlas.Key(mk, Atlas.MaxMapId, out var map) || !Atlas.ParseEntranceList(arr, out var spots)) continue;
            md.Entrances[map] = spots;
        }
    }

    List<Atlas.EntranceSpot> SumEntrances(int map) =>
        Atlas.AggregateEntrances(members.Where(kv => kv.Value.Entrances.ContainsKey(map)).Select(kv => (kv.Key, kv.Value.Entrances[map])));

    void RebuildEntrances(JsonObject revs, ref long maxRev)
    {
        foreach (var map in members.Values.SelectMany(m => m.Entrances.Keys).Distinct().ToList())
        {
            long rr = 1;
            if (revs?[map.ToString(CultureInfo.InvariantCulture)] is JsonValue v && v.TryGetValue<long>(out var l) && l > 0) rr = l;
            maxRev = Math.Max(maxRev, rr);
            entranceAggs[map] = new EntranceAgg { Spots = SumEntrances(map), Rev = rr };
        }
    }

    void Entrances(ShareResult r, MemberData md, JsonNode sect)
    {
        if (sect == null) return;
        if (sect is not JsonObject so) { r.Rejected++; return; }
        int seen = 0, points = 0;
        foreach (var (key, node) in so)
        {
            if (++seen > Atlas.MaxEntranceInstancesPerShare) { r.Rejected++; continue; }
            if (!Atlas.Key(key, Atlas.MaxMapId, out var map) || !Atlas.ParseEntranceList(node, out var inc) || points + inc.Count > Atlas.MaxEntranceSpotsPerShare) { r.Rejected++; continue; }
            points += inc.Count;
            bool isNew = !md.Entrances.TryGetValue(map, out var mine);
            if (isNew && md.Entrances.Count >= Atlas.MaxEntranceInstancesPerMember) { r.Rejected++; continue; }
            var have = isNew ? new List<Atlas.EntranceSpot>() : mine.Select(p => p.Clone()).ToList();
            bool changed = Atlas.MergeEntrances(have, inc, out var full);
            if (!changed) { if (full) r.Rejected++; else r.Unchanged++; continue; }
            md.Entrances[map] = have;
            entranceAggs[map] = new EntranceAgg { Spots = SumEntrances(map), Rev = ++rev };
            r.Accepted++; r.Changed = true;
        }
    }

    JsonObject EntrancesStoreJson(MemberData md)
    {
        var o = new JsonObject();
        foreach (var (map, l) in md.Entrances) o[map.ToString(CultureInfo.InvariantCulture)] = Atlas.EntranceJson(l, false);
        return o;
    }

    JsonObject EntranceRevJson()
    {
        var o = new JsonObject();
        foreach (var (map, a) in entranceAggs) o[map.ToString(CultureInfo.InvariantCulture)] = a.Rev;
        return o;
    }

    // the purge (member left / removed / reverted): rebuild the instances the member had spots for; returns the number of spots taken out
    int PurgeEntrances(MemberData md)
    {
        int n = 0;
        foreach (var (map, l) in md.Entrances)
        {
            n += l.Count;
            var spots = SumEntrances(map);
            if (spots.Count == 0) entranceAggs.Remove(map);
            else entranceAggs[map] = new EntranceAgg { Spots = spots, Rev = ++rev };
        }
        return n;
    }
}
