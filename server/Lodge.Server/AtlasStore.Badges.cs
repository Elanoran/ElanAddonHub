namespace Lodge;

// What the Badges feature reads from the Atlas pool: per-member numbers derived from data the store already keeps (counters, vendors, revealed items,
// finds, spawn points), plus a tally of the records a share added to the tables that carry no author (quest rewards, seen creatures / objects / quests).
public sealed partial class AtlasStore
{
    public sealed class MemberContribution
    {
        public long Kills, Objects, Fishing, Vendors, Revealed, Finds, Rare;
        public readonly HashSet<int> Zones = new();
    }

    // raised under Gate after a share changed something: (identity, quest rewards added or updated, seen records added or filled)
    public event Action<string, int, int> Tallied;

    void NoteShare(string identity, long revBefore)
    {
        int q = 0, s = 0;
        foreach (var x in quests.Values) if (x.Rev > revBefore) q++;
        foreach (var x in seenNpcs.Values) if (x.Rev > revBefore) s++;
        foreach (var x in seenObjects.Values) if (x.Rev > revBefore) s++;
        foreach (var x in seenQuests.Values) if (x.Rev > revBefore) s++;
        if (q + s > 0) Tallied?.Invoke(identity, q, s);
    }

    // every member the pool knows. "Rare" = drops this member was the only one in the lodge to record, from a source with at least 200 recorded kills,
    // where the drop is rarer than one in a hundred.
    public Dictionary<string, MemberContribution> Contributions()
    {
        lock (Gate)
        {
            var res = new Dictionary<string, MemberContribution>(StringComparer.Ordinal);
            MemberContribution Of(string id) { if (!res.TryGetValue(id, out var c)) res[id] = c = new MemberContribution(); return c; }
            foreach (var (id, md) in members)
            {
                var c = Of(id);
                c.Kills = md.Src[Kills].Values.Sum(x => x.N);
                c.Objects = md.Src[Objects].Values.Sum(x => x.N);
                c.Fishing = md.Src[Fishing].Values.Sum(x => x.N);
                c.Finds = md.FindCount;
                foreach (var ctr in md.Src[Kills].Values) c.Zones.UnionWith(ctr.Z.Keys);
                foreach (var zone in md.Src[Fishing].Keys) c.Zones.Add(zone);
            }
            foreach (var v in vendors.Values) if (!string.IsNullOrEmpty(v.By) && members.ContainsKey(v.By)) Of(v.By).Vendors++;
            foreach (var x in revealed.Values) if (!string.IsNullOrEmpty(x.By) && members.ContainsKey(x.By)) Of(x.By).Revealed++;
            foreach (var set in spawns.Values)
                foreach (var p in set.Pts) if (!string.IsNullOrEmpty(p.By) && members.ContainsKey(p.By)) Of(p.By).Zones.Add(p.Z);
            foreach (var (srcId, agg) in aggs[Kills])
            {
                if (agg.Sum.N < 200) continue;
                foreach (var (item, t) in agg.Sum.I)
                {
                    if (t[0] * 100 >= agg.Sum.N) continue;
                    string only = null; int holders = 0;
                    foreach (var (id, md) in members)
                        if (md.Src[Kills].TryGetValue(srcId, out var ctr) && ctr.I.TryGetValue(item, out var mine) && mine[0] > 0) { holders++; only = id; }
                    if (holders == 1) Of(only).Rare++;
                }
            }
            return res;
        }
    }
}
