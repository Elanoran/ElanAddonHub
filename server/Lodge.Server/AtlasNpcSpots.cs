using System.Globalization;
using System.Text.Json.Nodes;

namespace Lodge;

// Where NPCs really stand (2.26, sub-feature "atlas8"). Forever keeps its spawn points on the server, so Elan's Toolkit notes them from play, per npc id: spots
// { z (the zone's uiMapId), x, y (fractions of that map, 4 decimals), k = the kind (1 = a window opened for it: gossip, quest, vendor, trainer, flight ...; 2 = a
// looted corpse; 3 = a minimap vignette, a rare), n = times seen there }. A share carries a member's CUMULATIVE spots for the npcs that changed:
// { "<npcId>": [ { z, x, y, k, n }, ... ] }. The server keeps them per member and shows everybody the merge:
//   * two spots of one npc of the same kind on the same zone map within NpcSpotMerge (Euclidean, map fraction) are ONE spot;
//   * its position is the mean of the members' points weighted by their n; per member the larger n of its points in that spot; n = the sum over members,
//     m = the members who saw it there;
//   * at most MaxNpcSpots (12) per npc: the best spot of every kind first (so many kill spots never push out the one place a vendor stands), then the
//     rest by most members, then most sightings.
// Idempotency: a member's own spot within NpcSpotMerge of a stored one of the same kind is the same spot and keeps the max of stored and sent: a resend never
// counts twice. Only numbers leave the server: no names (guests see the same merge).
public static partial class Atlas
{
    public const double NpcSpotMerge = 0.01;
    public const int MaxNpcSpotKind = 3;
    public const int MaxNpcSpotNpcsPerMember = 3000, MaxNpcSpotsPerNpcPerMember = 10, MaxNpcSpots = 12, MaxNpcSpotNpcs = 30000, MaxNpcSpotNpcsPerShare = 400, MaxNpcSpotsPerShare = 2000;

    public sealed class NpcSpot
    {
        public int Z, K; public double X, Y; public long N; public int M;
        public NpcSpot Clone() => new() { Z = Z, K = K, X = X, Y = Y, N = N, M = M };
    }

    public static bool SameNpcSpot(NpcSpot a, int z, int k, double x, double y) => a.Z == z && a.K == k && (a.X - x) * (a.X - x) + (a.Y - y) * (a.Y - y) <= NpcSpotMerge * NpcSpotMerge + 1e-12;

    // one npc's spots (a JSON array); all-or-nothing
    public static bool ParseNpcSpotList(JsonNode node, out List<NpcSpot> spots)
    {
        spots = null;
        if (node is not JsonArray a || a.Count == 0 || a.Count > MaxNpcSpotsPerNpcPerMember) return false;
        var l = new List<NpcSpot>();
        foreach (var e in a)
        {
            if (e is not JsonObject o || !Whole(o["z"], 1, MaxMapId, out var z) || !Whole(o["k"], 1, MaxNpcSpotKind, out var k) || !Coord4(o["x"], out var x) || !Coord4(o["y"], out var y)
                || !Whole(o["n"], 1, MaxNodeCount, out var n)) return false;
            var same = l.FirstOrDefault(p => SameNpcSpot(p, (int)z, (int)k, x, y));
            if (same != null) same.N = Math.Max(same.N, n); else l.Add(new NpcSpot { Z = (int)z, K = (int)k, X = x, Y = y, N = n });
        }
        spots = l;
        return true;
    }

    // merge incoming spots into one member's list for one npc; true when anything changed, `full` when a new spot did not fit
    public static bool MergeNpcSpots(List<NpcSpot> have, List<NpcSpot> incoming, out bool full)
    {
        bool changed = false; full = false;
        foreach (var p in incoming)
        {
            var same = have.FirstOrDefault(h => SameNpcSpot(h, p.Z, p.K, p.X, p.Y));
            if (same != null) { if (p.N > same.N) { same.N = p.N; changed = true; } continue; }
            if (have.Count >= MaxNpcSpotsPerNpcPerMember) { full = true; continue; }
            have.Add(p.Clone()); changed = true;
        }
        return changed;
    }

    // the merged spots of one npc over all members' lists; deterministic (most sightings first, then member, kind, map, x, y)
    public static List<NpcSpot> AggregateNpcSpots(IEnumerable<(string who, List<NpcSpot> spots)> lists)
    {
        var all = new List<(string who, NpcSpot p)>();
        foreach (var (who, spots) in lists) foreach (var p in spots) all.Add((who, p));
        all.Sort((a, b) =>
        {
            int c = b.p.N.CompareTo(a.p.N); if (c != 0) return c;
            c = string.CompareOrdinal(a.who, b.who); if (c != 0) return c;
            c = a.p.K.CompareTo(b.p.K); if (c != 0) return c;
            c = a.p.Z.CompareTo(b.p.Z); if (c != 0) return c;
            c = a.p.X.CompareTo(b.p.X); return c != 0 ? c : a.p.Y.CompareTo(b.p.Y);
        });
        var seeds = new List<NpcSpot>(); var perMember = new List<Dictionary<string, long>>(); var sums = new List<double[]>();
        foreach (var (who, p) in all)
        {
            int idx = seeds.FindIndex(s => SameNpcSpot(s, p.Z, p.K, p.X, p.Y));
            if (idx < 0) { seeds.Add(new NpcSpot { Z = p.Z, K = p.K, X = p.X, Y = p.Y }); perMember.Add(new(StringComparer.Ordinal)); sums.Add(new double[3]); idx = seeds.Count - 1; }
            var d = perMember[idx];
            d[who] = d.TryGetValue(who, out var old) ? Math.Max(old, p.N) : p.N;
            sums[idx][0] += p.X * p.N; sums[idx][1] += p.Y * p.N; sums[idx][2] += p.N;
        }
        for (int i = 0; i < seeds.Count; i++)
        {
            seeds[i].N = Math.Min(MaxNodeCount * 2000, perMember[i].Values.Sum()); seeds[i].M = perMember[i].Count;
            if (sums[i][2] > 0) { seeds[i].X = Math.Round(sums[i][0] / sums[i][2], 4); seeds[i].Y = Math.Round(sums[i][1] / sums[i][2], 4); }
        }
        var ranked = seeds.OrderByDescending(s => s.M).ThenByDescending(s => s.N).ThenBy(s => s.K).ThenBy(s => s.Z).ThenBy(s => s.X).ThenBy(s => s.Y).ToList();
        // every kind's best spot first, then the rest by rank
        var keep = ranked.GroupBy(s => s.K).Select(g => g.First()).ToList();
        foreach (var s in ranked) { if (keep.Count >= MaxNpcSpots) break; if (!keep.Contains(s)) keep.Add(s); }
        return keep.OrderByDescending(s => s.M).ThenByDescending(s => s.N).ThenBy(s => s.K).ThenBy(s => s.Z).ThenBy(s => s.X).ThenBy(s => s.Y).Take(MaxNpcSpots).ToList();
    }

    public static JsonArray NpcSpotJson(List<NpcSpot> spots, bool withMembers) => new(spots.Select(p =>
    {
        var o = new JsonObject { ["z"] = p.Z, ["x"] = p.X, ["y"] = p.Y, ["k"] = p.K, ["n"] = p.N };
        if (withMembers) o["m"] = p.M;
        return (JsonNode)o;
    }).ToArray());
}

public sealed partial class AtlasStore
{
    // the merged spots of one npc (most members first)
    public sealed class NpcSpotAgg { public List<Atlas.NpcSpot> Spots = new(); public long Rev; }

    readonly Dictionary<int, NpcSpotAgg> npcSpotAggs = new();

    public int NpcSpotNpcCount { get { lock (Gate) return npcSpotAggs.Count; } }

    static void LoadNpcSpots(MemberData md, JsonObject so)
    {
        foreach (var (key, arr) in so)
        {
            if (md.NpcSpots.Count >= Atlas.MaxNpcSpotNpcsPerMember || !Atlas.Key(key, Atlas.MaxId, out var npc) || !Atlas.ParseNpcSpotList(arr, out var spots)) continue;
            md.NpcSpots[npc] = spots;
        }
    }

    List<Atlas.NpcSpot> SumNpcSpots(int npc) =>
        Atlas.AggregateNpcSpots(members.Where(kv => kv.Value.NpcSpots.ContainsKey(npc)).Select(kv => (kv.Key, kv.Value.NpcSpots[npc])));

    void RebuildNpcSpots(JsonObject revs, ref long maxRev)
    {
        foreach (var npc in members.Values.SelectMany(m => m.NpcSpots.Keys).Distinct().ToList())
        {
            if (npcSpotAggs.Count >= Atlas.MaxNpcSpotNpcs) break;
            long rr = 1;
            if (revs?[npc.ToString(CultureInfo.InvariantCulture)] is JsonValue v && v.TryGetValue<long>(out var l) && l > 0) rr = l;
            maxRev = Math.Max(maxRev, rr);
            npcSpotAggs[npc] = new NpcSpotAgg { Spots = SumNpcSpots(npc), Rev = rr };
        }
    }

    void NpcSpots(ShareResult r, MemberData md, JsonNode sect)
    {
        if (sect == null) return;
        if (sect is not JsonObject so) { r.Rejected++; return; }
        int seen = 0, points = 0;
        foreach (var (key, node) in so)
        {
            if (++seen > Atlas.MaxNpcSpotNpcsPerShare) { r.Rejected++; continue; }
            if (!Atlas.Key(key, Atlas.MaxId, out var npc) || !Atlas.ParseNpcSpotList(node, out var inc) || points + inc.Count > Atlas.MaxNpcSpotsPerShare) { r.Rejected++; continue; }
            points += inc.Count;
            bool isNew = !md.NpcSpots.TryGetValue(npc, out var mine);
            if (isNew && (md.NpcSpots.Count >= Atlas.MaxNpcSpotNpcsPerMember || (!npcSpotAggs.ContainsKey(npc) && npcSpotAggs.Count >= Atlas.MaxNpcSpotNpcs))) { r.Rejected++; continue; }
            var have = isNew ? new List<Atlas.NpcSpot>() : mine.Select(p => p.Clone()).ToList();
            bool changed = Atlas.MergeNpcSpots(have, inc, out var full);
            if (!changed) { if (full) r.Rejected++; else r.Unchanged++; continue; }
            md.NpcSpots[npc] = have;
            npcSpotAggs[npc] = new NpcSpotAgg { Spots = SumNpcSpots(npc), Rev = ++rev };
            r.Accepted++; r.Changed = true;
        }
    }

    JsonObject NpcSpotsStoreJson(MemberData md)
    {
        var o = new JsonObject();
        foreach (var (npc, l) in md.NpcSpots) o[npc.ToString(CultureInfo.InvariantCulture)] = Atlas.NpcSpotJson(l, false);
        return o;
    }

    JsonObject NpcSpotRevJson()
    {
        var o = new JsonObject();
        foreach (var (npc, a) in npcSpotAggs) o[npc.ToString(CultureInfo.InvariantCulture)] = a.Rev;
        return o;
    }

    // the purge (member left / removed / reverted): rebuild the npcs the member had spots for; returns the number of spots taken out
    int PurgeNpcSpots(MemberData md)
    {
        int n = 0;
        foreach (var (npc, l) in md.NpcSpots)
        {
            n += l.Count;
            var spots = SumNpcSpots(npc);
            if (spots.Count == 0) npcSpotAggs.Remove(npc);
            else npcSpotAggs[npc] = new NpcSpotAgg { Spots = spots, Rev = ++rev };
        }
        return n;
    }
}
