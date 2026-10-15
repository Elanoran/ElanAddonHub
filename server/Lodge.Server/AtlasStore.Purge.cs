namespace Lodge;

// Take one member's contributions out of the pool (2.22): used when a member leaves or is removed, and by the owner's "revert member" action
// (a member who sent bad data stays a member). Everything the member authored goes: their counters, nodes, fishing spots and entrance spots (the merged totals are
// rebuilt from the other members), the vendors, quests, revealed items, seen npc / object / quest rows and spawn points that carry their name, and
// their finds. First-writer-wins rows are simply removed, so the next member to share the same id puts their version in.
// Rows saved before 2.22 carry no author for quests and seen rows; those cannot be attributed and stay.
public sealed partial class AtlasStore
{
    public sealed class PurgeResult
    {
        public int Counters, Nodes, FishSpots, FieldNotes, Entrances, Vendors, Quests, Revealed, Seen, Spawns, Finds;
        public int Total => Counters + Nodes + FishSpots + FieldNotes + Entrances + Vendors + Quests + Revealed + Seen + Spawns + Finds;
    }

    // Caller holds Gate and saves afterwards (Dirty is set). The epoch changes, so every client fetches the whole pool again (a delta cannot express a removal).
    public PurgeResult Purge(string identity)
    {
        var r = new PurgeResult();
        if (string.IsNullOrEmpty(identity)) return r;
        var srcs = new List<(int k, int id)>();
        var objs = new List<int>();
        var maps = new List<int>();
        bool wasMember = false;
        if (members.Remove(identity, out var md))
        {
            for (int k = 0; k < 3; k++) foreach (var id in md.Src[k].Keys) { srcs.Add((k, id)); r.Counters++; }
            foreach (var id in md.Nodes.Keys) objs.Add(id);
            wasMember = true;
            r.Nodes = md.NodeTotal;
            foreach (var m in md.Fish.Keys) maps.Add(m);
            r.FishSpots = md.FishTotal;
            r.FieldNotes = PurgeNotes(md);
            r.Entrances = PurgeEntrances(md);
        }
        foreach (var (k, id) in srcs)
        {
            var a = Sum(k, id);
            if (a.Members == 0) aggs[k].Remove(id);
            else { a.Rev = ++rev; aggs[k][id] = a; }
        }
        foreach (var id in objs)
        {
            var na = SumNodes(id);
            if (na.Maps.Count == 0) nodeAggs.Remove(id);
            else { na.Rev = ++rev; nodeAggs[id] = na; }
        }
        foreach (var map in maps)
        {
            var spots = SumFish(map);
            if (spots.Count == 0) fishAggs.Remove(map);
            else fishAggs[map] = new FishAgg { Spots = spots, Rev = ++rev };
        }
        r.Vendors = Drop(vendors, v => v.By == identity);
        r.Quests = Drop(quests, q => q.By == identity);
        r.Revealed = Drop(revealed, x => x.By == identity);
        r.Seen = Drop(seenNpcs, x => x.By == identity) + Drop(seenObjects, x => x.By == identity) + Drop(seenQuests, x => x.By == identity);
        foreach (var (id, set) in spawns.ToList())
        {
            int n = set.Pts.RemoveAll(p => p.By == identity);
            if (n == 0) continue;
            r.Spawns += n;
            if (set.Pts.Count == 0) spawns.Remove(id); else set.Rev = ++rev;
        }
        foreach (var f in finds.Where(f => f.Identity == identity).ToList()) findKeys.Remove(f.Key);
        r.Finds = finds.RemoveAll(f => f.Identity == identity);
        if (r.Total > 0 || wasMember)
        {
            Epoch = Guid.NewGuid().ToString("N")[..12];
            rev++;
            Dirty = true;
            capLogged = false;
        }
        return r;
    }

    static int Drop<T>(Dictionary<int, T> table, Func<T, bool> mine)
    {
        var gone = table.Where(kv => mine(kv.Value)).Select(kv => kv.Key).ToList();
        foreach (var id in gone) table.Remove(id);
        return gone.Count;
    }
}
