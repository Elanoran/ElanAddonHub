using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Elans.Shared;

namespace Lodge;

// data/atlas.json = { v, epoch, rev, members: { identity: { kills, objects, fishing } }, vendors, quests, spawns, items, finds }
// Atomic (tmp, flush, rename; a leftover tmp is used only when the main file is missing), size-capped on load, every loaded value goes through the same
// validators as a share. `rev` counts changes: every stored/changed record carries the revision it last changed at, atlas.get {since} returns the records
// with a newer one. `epoch` names this data set: a client holding another epoch (data restored from a backup, deleted) gets everything again.
// Never logs any of its content.
public sealed partial class AtlasStore
{
    public const int Kills = 0, Objects = 1, Fishing = 2;
    // 2.26 (atlas9): pickpocketing, gathering a corpse per kind, and what was inside opened items (clams, lockboxes) are counters of their own
    public const int Pickpocket = 3, Skinning = 4, Mining = 5, Herbalism = 6, Salvage = 7, Contained = 8, KindCount = 9;
    static readonly int[] KindSec = { 0, 1, 2, 16, 17, 18, 19, 20, 21 };   // the kinds' places in Get's sections
    const long MaxFileBytes = 64L * 1024 * 1024;
    const int MaxMembers = 2000;

    sealed class MemberData
    {
        public readonly Dictionary<int, Atlas.Ctr>[] Src = Enumerable.Range(0, KindCount).Select(_ => new Dictionary<int, Atlas.Ctr>()).ToArray();
        public long Total;
        public int FindCount;
        public readonly Dictionary<int, Dictionary<int, List<Atlas.NodePt>>> Nodes = new();   // object -> uiMapId -> points (2.18)
        public int NodeTotal;
        public readonly Dictionary<int, List<Atlas.FishSpot>> Fish = new();   // uiMapId -> fishing spots (2.21)
        public int FishTotal;
        public readonly Dictionary<int, Atlas.FieldNote> Notes = new();   // npc -> hunter field note (2.23)
        public int NotePoints;
        public readonly Dictionary<int, List<Atlas.EntranceSpot>> Entrances = new();   // instance map id -> entrance spots (2.24)
        public readonly Dictionary<int, List<Atlas.NpcSpot>> NpcSpots = new();   // npc -> where it was met, killed, seen as a vignette (2.26)
        public string Who = "";   // the name the member was last seen under (a Lodge login, case kept): shown through the display name on field notes
    }

    // the merged fishing spots of one map (most caught first)
    public sealed class FishAgg { public List<Atlas.FishSpot> Spots = new(); public long Rev; }

    // the merged nodes of one object: map -> nodes (most looted first)
    public sealed class NodeAgg { public Dictionary<int, List<Atlas.NodePt>> Maps = new(); public long Rev; }

    public sealed class Agg { public Atlas.Ctr Sum = new(); public int Members; public long Rev; public long Dup; }   // Dup: kills that were a corpse another member counted too (2.26)

    sealed class SpawnSet { public List<Atlas.Spawn> Pts = new(); public long Rev; }

    readonly string path, tmp;
    readonly ILogger log;
    readonly IClock clock;
    public readonly object Gate = new();
    readonly Dictionary<string, MemberData> members = new(StringComparer.Ordinal);
    readonly Dictionary<int, Agg>[] aggs = Enumerable.Range(0, KindCount).Select(_ => new Dictionary<int, Agg>()).ToArray();
    readonly Dictionary<int, NodeAgg> nodeAggs = new();
    readonly Dictionary<int, FishAgg> fishAggs = new();
    readonly Dictionary<int, Atlas.Vendor> vendors = new();
    readonly Dictionary<int, Atlas.Quest> quests = new();
    readonly Dictionary<int, SpawnSet> spawns = new();
    readonly Dictionary<int, Atlas.Revealed> revealed = new();
    readonly Dictionary<int, Atlas.SeenNpc> seenNpcs = new();
    readonly Dictionary<int, Atlas.SeenObject> seenObjects = new();
    readonly Dictionary<int, Atlas.SeenQuest> seenQuests = new();
    readonly HashSet<string> seenMembers = new(StringComparer.Ordinal);   // 2.25: the members whose client caches filled the seen tables (only the count leaves the server)
    readonly List<Atlas.Find> finds = new();
    readonly HashSet<string> findKeys = new(StringComparer.Ordinal);
    long rev;
    DateTime lastSave = DateTime.MinValue;
    public bool Dirty { get; private set; }
    public string Epoch { get; private set; }

    // 2.22: the store refuses to grow once the file on disk is this big (the file is only loaded up to MaxFileBytes; growing past it would silently lose data after a restart)
    public long WriteCapBytes { get; set; } = MaxFileBytes - 2L * 1024 * 1024;
    long storedBytes;
    bool capLogged;
    public long StoredBytes { get { lock (Gate) return storedBytes; } }

    public AtlasStore(string dataDir, ILogger log, IClock clock)
    {
        this.log = log; this.clock = clock;
        path = Path.Combine(dataDir, "atlas.json");
        tmp = path + ".tmp";
        Epoch = Guid.NewGuid().ToString("N")[..12];
        lastSave = clock.UtcNow;
        try
        {
            if (File.Exists(tmp)) { if (!File.Exists(path)) File.Move(tmp, path); else File.Delete(tmp); }
            if (File.Exists(path)) storedBytes = new FileInfo(path).Length;
            if (File.Exists(path) && new FileInfo(path).Length <= MaxFileBytes) Load(JsonNode.Parse(File.ReadAllText(path))?.AsObject());
        }
        catch (Exception e) { log?.LogWarning("Atlas: load failed: {Error}", e.GetType().Name); }
    }

    public long NowMs => new DateTimeOffset(clock.UtcNow).ToUnixTimeMilliseconds();
    long NowSec => NowMs / 1000;
    public long Rev { get { lock (Gate) return rev; } }
    public int MemberCount { get { lock (Gate) return members.Count; } }
    public int FindCount { get { lock (Gate) return finds.Count; } }
    public int VendorCount { get { lock (Gate) return vendors.Count; } }
    public int SeenNpcCount { get { lock (Gate) return seenNpcs.Count; } }
    public int SeenObjectCount { get { lock (Gate) return seenObjects.Count; } }
    public int SeenQuestCount { get { lock (Gate) return seenQuests.Count; } }
    public int SeenMemberCount { get { lock (Gate) return seenMembers.Count; } }
    public int SourceCount(int kind) { lock (Gate) return aggs[kind].Count; }
    public int NodeObjectCount { get { lock (Gate) return nodeAggs.Count; } }

    // ---------------------------------------------------------------- loading

    void Load(JsonObject root)
    {
        if (root == null) return;
        var now = NowSec;
        if (root["epoch"] is JsonValue ev && ev.TryGetValue<string>(out var ep) && ep.Length is > 0 and <= 32 && ep.All(char.IsAsciiLetterOrDigit)) Epoch = ep;
        long maxRev = root["rev"] is JsonValue rv && rv.TryGetValue<long>(out var rl) && rl >= 0 ? rl : 0;
        long R(JsonNode n) { var x = n is JsonObject o && o["r"] is JsonValue v && v.TryGetValue<long>(out var l) && l > 0 ? l : 1; maxRev = Math.Max(maxRev, x); return x; }
        string By(JsonNode n) => n is JsonObject o && o["b"] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length is > 0 and <= 64 ? s : "";

        if (root["members"] is JsonObject mo)
            foreach (var (who, v) in mo)
            {
                if (members.Count >= MaxMembers) break;
                if (who.Length == 0 || who.Length > 64 || v is not JsonObject d) continue;
                var md = new MemberData();
                if (d["w"] is JsonValue wv && wv.TryGetValue<string>(out var wn)) md.Who = Names.Cut(Names.Normalize(wn), Names.Max);
                for (int k = 0; k < KindCount; k++)
                {
                    if (d[KindName(k)] is not JsonObject so) continue;
                    foreach (var (key, node) in so)
                    {
                        if (md.Src[k].Count >= MaxFor(k) || !Atlas.Key(key, KeyMax(k), out var id) || !Atlas.ParseCtr(node, k != Fishing && k != Contained, out var c)) continue;
                        if (md.Total + c.N > Atlas.MaxMemberTotal) continue;
                        md.Src[k][id] = c; md.Total += c.N;
                    }
                }
                if (d["nodes"] is JsonObject no) LoadNodes(md, no);
                if (d["fishspots"] is JsonObject fo) LoadFish(md, fo);
                if (d["fieldnotes"] is JsonObject fno) LoadNotes(md, fno, now);
                if (d["entrances"] is JsonObject eno) LoadEntrances(md, eno);
                if (d["npcspots"] is JsonObject npo) LoadNpcSpots(md, npo);
                members[who] = md;
            }
        // the aggregates are derived: rebuild them, remembering the revision of the member data
        var revs = root["srcRev"] as JsonObject;
        for (int k = 0; k < KindCount; k++)
        {
            var ids = members.Values.SelectMany(m => m.Src[k].Keys).Distinct().ToList();
            foreach (var id in ids)
            {
                var a = Sum(k, id);
                long rr = 1;
                if (revs?[KindName(k)] is JsonObject ro && ro[id.ToString(CultureInfo.InvariantCulture)] is JsonValue rvv && rvv.TryGetValue<long>(out var l2) && l2 > 0) rr = l2;
                maxRev = Math.Max(maxRev, rr); a.Rev = rr;
                aggs[k][id] = a;
            }
        }
        var nodeRevs = root["nodeRev"] as JsonObject;
        foreach (var id in members.Values.SelectMany(m => m.Nodes.Keys).Distinct().ToList())
        {
            long rr = 1;
            if (nodeRevs?[id.ToString(CultureInfo.InvariantCulture)] is JsonValue nv && nv.TryGetValue<long>(out var nl) && nl > 0) rr = nl;
            maxRev = Math.Max(maxRev, rr);
            var na = SumNodes(id); na.Rev = rr; nodeAggs[id] = na;
        }
        var fishRevs = root["fishRev"] as JsonObject;
        foreach (var map in members.Values.SelectMany(m => m.Fish.Keys).Distinct().ToList())
        {
            long rr = 1;
            if (fishRevs?[map.ToString(CultureInfo.InvariantCulture)] is JsonValue fv && fv.TryGetValue<long>(out var fl) && fl > 0) rr = fl;
            maxRev = Math.Max(maxRev, rr);
            fishAggs[map] = new FishAgg { Spots = SumFish(map), Rev = rr };
        }
        RebuildNotes(root["noteRev"] as JsonObject, ref maxRev);
        RebuildEntrances(root["entranceRev"] as JsonObject, ref maxRev);
        RebuildNpcSpots(root["npcSpotRev"] as JsonObject, ref maxRev);
        if (root["vendors"] is JsonObject vo)
            foreach (var (key, node) in vo)
                if (vendors.Count < Atlas.MaxVendors && Atlas.Key(key, Atlas.MaxId, out var id) && Atlas.ParseVendor(node, now, out var v)) { v.By = By(node); v.Rev = R(node); vendors[id] = v; }
        if (root["quests"] is JsonObject qo)
            foreach (var (key, node) in qo)
                if (quests.Count < Atlas.MaxQuests && Atlas.Key(key, Atlas.MaxId, out var id) && Atlas.ParseQuest(node, now, out var q)) { q.By = By(node); q.Rev = R(node); quests[id] = q; }
        if (root["spawns"] is JsonObject sp)
            foreach (var (key, node) in sp)
            {
                if (spawns.Count >= Atlas.MaxSpawnNpcs || !Atlas.Key(key, Atlas.MaxId, out var id) || node is not JsonObject so || so["p"] is not JsonArray pa) continue;
                var set = new SpawnSet { Rev = R(node) };
                foreach (var p in pa)
                    if (set.Pts.Count < Atlas.MaxSpawnPointsPerNpc && Atlas.ParseSpawn(p, out var z, out var x, out var y)) set.Pts.Add(new Atlas.Spawn { Z = z, X = x, Y = y, By = By(p) });
                if (set.Pts.Count > 0) spawns[id] = set;
            }
        if (root["items"] is JsonObject io)
            foreach (var (key, node) in io)
                if (revealed.Count < Atlas.MaxRevealed && Atlas.Key(key, Atlas.MaxItemId, out var id) && Atlas.ParseRevealed(node, out var x)) { x.By = By(node); x.Rev = R(node); revealed[id] = x; }
        if (root["seenNpcs"] is JsonObject sno)
            foreach (var (key, node) in sno)
                if (seenNpcs.Count < Atlas.MaxSeenNpcs && Atlas.Key(key, Atlas.MaxId, out var id) && Atlas.ParseSeenNpc(node, out var x)) { x.By = By(node); x.Rev = R(node); seenNpcs[id] = x; }
        if (root["seenObjects"] is JsonObject sob)
            foreach (var (key, node) in sob)
                if (seenObjects.Count < Atlas.MaxSeenObjects && Atlas.Key(key, Atlas.MaxId, out var id) && Atlas.ParseSeenObject(node, out var x)) { x.By = By(node); x.Rev = R(node); seenObjects[id] = x; }
        if (root["seenQuests"] is JsonObject sqo)
            foreach (var (key, node) in sqo)
                if (seenQuests.Count < Atlas.MaxSeenQuests && Atlas.Key(key, Atlas.MaxId, out var id) && Atlas.ParseSeenQuest(node, out var x)) { x.By = By(node); x.Rev = R(node); seenQuests[id] = x; }
        if (root["seenMembers"] is JsonArray sma)
            foreach (var node in sma)
                if (seenMembers.Count < Atlas.MaxSeenMembers && node is JsonValue smv && smv.TryGetValue<string>(out var smi) && smi.Length is > 0 and <= 64) seenMembers.Add(smi);
        if (root["finds"] is JsonArray fa)
            foreach (var node in fa)
            {
                if (!Atlas.ParseFind(node, now, out var f) || node is not JsonObject fo) continue;
                f.Identity = By(node); f.Who = fo["w"] is JsonValue wv && wv.TryGetValue<string>(out var w) ? Names.Cut(Names.Normalize(w), Names.Max) : f.Identity;
                f.Rev = R(node);
                if (f.Identity.Length == 0 || !findKeys.Add(f.Key)) continue;
                finds.Add(f);
            }
        finds.Sort((a, b) => a.Rev.CompareTo(b.Rev));
        while (finds.Count > Atlas.MaxFinds) { findKeys.Remove(finds[0].Key); finds.RemoveAt(0); }
        foreach (var f in finds) { if (members.TryGetValue(f.Identity, out var m)) m.FindCount++; }
        rev = maxRev;
    }

    static void LoadNodes(MemberData md, JsonObject no)
    {
        foreach (var (ok, ov) in no)
        {
            if (md.Nodes.Count >= Atlas.MaxNodeObjectsPerMember || !Atlas.Key(ok, Atlas.MaxId, out var oid) || !Atlas.ParseNodeObject(NodeObj(ov), out var maps, out var pts)) continue;
            if (md.NodeTotal + pts > Atlas.MaxNodesPerMember) continue;
            md.Nodes[oid] = maps; md.NodeTotal += pts;
        }
    }

    static void LoadFish(MemberData md, JsonObject fo)
    {
        foreach (var (mk, arr) in fo)
        {
            if (md.Fish.Count >= Atlas.MaxFishMapsPerMember || !Atlas.Key(mk, Atlas.MaxMapId, out var map) || !Atlas.ParseFishMap(arr, out var spots)) continue;
            if (md.FishTotal + spots.Count > Atlas.MaxFishSpotsPerMember) continue;
            md.Fish[map] = spots; md.FishTotal += spots.Count;
        }
    }

    List<Atlas.FishSpot> SumFish(int map) =>
        Atlas.AggregateFish(members.Where(kv => kv.Value.Fish.ContainsKey(map)).Select(kv => (kv.Key, kv.Value.Fish[map])));

    // storage shape { map: [[x, y, n], ...] } -> the wire shape { map: [ { x, y, n } ] } the parser takes
    static JsonNode NodeObj(JsonNode stored)
    {
        if (stored is not JsonObject so) return null;
        var o = new JsonObject();
        foreach (var (mk, arr) in so)
        {
            if (arr is not JsonArray a) return null;
            var na = new JsonArray();
            foreach (var e in a)
            {
                if (e is not JsonArray t || t.Count != 3) return null;
                na.Add(new JsonObject { ["x"] = t[0]!.DeepClone(), ["y"] = t[1]!.DeepClone(), ["n"] = t[2]!.DeepClone() });
            }
            o[mk] = na;
        }
        return o;
    }

    NodeAgg SumNodes(int obj)
    {
        var a = new NodeAgg();
        var maps = members.Values.Where(m => m.Nodes.ContainsKey(obj)).SelectMany(m => m.Nodes[obj].Keys).Distinct().ToList();
        foreach (var map in maps)
        {
            var lists = members.Where(kv => kv.Value.Nodes.TryGetValue(obj, out var mm) && mm.ContainsKey(map)).Select(kv => (kv.Key, kv.Value.Nodes[obj][map]));
            var l = Atlas.AggregateNodes(lists);
            if (l.Count > 0) a.Maps[map] = l;
        }
        return a;
    }

    static readonly string[] KindNames = { "kills", "objects", "fishing", "pickpocket", "skinning", "mining", "herbalism", "salvage", "contained" };
    static string KindName(int k) => KindNames[k];
    static int MaxFor(int k) => k switch { Kills => Atlas.MaxKillSources, Objects => Atlas.MaxObjectSources, Fishing => Atlas.MaxFishingZones, Contained => Atlas.MaxContainedSources, _ => Atlas.MaxGatherSources };
    static int KeyMax(int k) => k == Fishing ? Atlas.MaxMapId : k == Contained ? Atlas.MaxItemId : Atlas.MaxId;
    static int PerShare(int k) => k switch { Kills => Atlas.MaxKillsPerShare, Objects => Atlas.MaxObjectsPerShare, Fishing => Atlas.MaxFishingPerShare, _ => Atlas.MaxGatherPerShare };

    Agg Sum(int k, int id)
    {
        var a = new Agg();
        var fpSeen = new Dictionary<long, int>();
        foreach (var m in members.Values)
        {
            if (!m.Src[k].TryGetValue(id, out var c)) continue;
            a.Members++; a.Sum.N += c.N;
            Atlas.AddX(ref a.Sum.X, c.X, fpSeen);
            foreach (var kv in c.I)
            {
                if (a.Sum.I.TryGetValue(kv.Key, out var x)) { x[0] += kv.Value[0]; x[1] += kv.Value[1]; }
                else a.Sum.I[kv.Key] = new[] { kv.Value[0], kv.Value[1] };
            }
            foreach (var kv in c.Z) a.Sum.Z[kv.Key] = a.Sum.Z.TryGetValue(kv.Key, out var z) ? z + kv.Value : kv.Value;
        }
        a.Dup = Math.Min(a.Sum.N, fpSeen.Values.Sum(v => (long)(v - 1)));
        return a;
    }

    // ---------------------------------------------------------------- sharing

    public sealed class ShareResult
    {
        public int Accepted, Unchanged, Rejected;
        public bool Changed;
        public bool Full;   // 2.22: refused because the store reached its size cap
        public int Total => Accepted + Unchanged + Rejected;
    }

    // True when the message carries at least one known, non-empty section (the sections: Elans.Shared AtlasSections.All)
    public static bool HasContent(JsonObject m) =>
        AtlasSections.All.Any(s => s == AtlasSections.Finds ? m[s] is JsonArray a && a.Count > 0 : m[s] is JsonObject o && o.Count > 0);

    // One member's batch. Counters are cumulative totals; per field the stored value only ever grows (see Atlas). Caller holds Gate.
    public ShareResult Share(string identity, string who, JsonObject m)
    {
        var r = new ShareResult();
        long now = NowSec, revBefore = rev;
        if (storedBytes >= WriteCapBytes)
        {
            // 2.22 write cap: a full pool does not grow (it would become unloadable after a restart); the owner can revert a member to make room
            r.Rejected++; r.Full = true;
            if (!capLogged) { capLogged = true; log?.LogWarning("Atlas: the store is at its size cap ({Bytes} bytes) - new contributions are refused", storedBytes); }
            return r;
        }
        if (!members.TryGetValue(identity, out var md))
        {
            if (members.Count >= MaxMembers) { r.Rejected++; return r; }
            md = new MemberData();
            members[identity] = md;
        }
        md.Who = who;
        for (int k = 0; k < KindCount; k++) Counters(r, md, identity, k, m[KindName(k)], PerShare(k));
        Vendors(r, identity, m["vendors"], now);
        Quests(r, identity, m["quests"], now);
        Spawns(r, identity, m["spawns"]);
        Revealed(r, identity, m["items"]);
        Seen(r, identity, m["seenNpcs"], Atlas.MaxSeenNpcsPerShare, seenNpcs, Atlas.MaxSeenNpcs, (JsonNode n, out Atlas.SeenNpc v) => Atlas.ParseSeenNpc(n, out v), (a, b) => a.Merge(b), v => v.Build, (v, rv, by) => { v.Rev = rv; if (by != null) v.By = by; });
        Seen(r, identity, m["seenObjects"], Atlas.MaxSeenObjectsPerShare, seenObjects, Atlas.MaxSeenObjects, (JsonNode n, out Atlas.SeenObject v) => Atlas.ParseSeenObject(n, out v), (a, b) => a.Merge(b), v => v.Build, (v, rv, by) => { v.Rev = rv; if (by != null) v.By = by; });
        Seen(r, identity, m["seenQuests"], Atlas.MaxSeenQuestsPerShare, seenQuests, Atlas.MaxSeenQuests, (JsonNode n, out Atlas.SeenQuest v) => Atlas.ParseSeenQuest(n, out v), (a, b) => a.Merge(b), v => v.Build, (v, rv, by) => { v.Rev = rv; if (by != null) v.By = by; });
        Finds(r, md, identity, who, m["finds"], now);
        Nodes(r, md, m["nodes"]);
        FishSpots(r, md, m["fishspots"]);
        FieldNotes(r, md, m["fieldnotes"], now);
        Entrances(r, md, m["entrances"]);
        NpcSpots(r, md, m["npcspots"]);
        if (md.Total == 0 && md.FindCount == 0 && md.Nodes.Count == 0 && md.Fish.Count == 0 && md.Notes.Count == 0 && md.Entrances.Count == 0 && md.NpcSpots.Count == 0 && !r.Changed && md.Src.All(s => s.Count == 0)) members.Remove(identity);   // nothing of this person was kept
        if (r.Changed) { Dirty = true; NoteShare(identity, revBefore); }
        return r;
    }

    void Counters(ShareResult r, MemberData md, string identity, int k, JsonNode sect, int maxPerShare)
    {
        if (sect == null) return;
        if (sect is not JsonObject so) { r.Rejected++; return; }
        int seen = 0;
        foreach (var (key, node) in so)
        {
            if (++seen > maxPerShare) { r.Rejected++; continue; }
            if (!Atlas.Key(key, KeyMax(k), out var id) || !Atlas.ParseCtr(node, k != Fishing && k != Contained, out var nw)) { r.Rejected++; continue; }
            md.Src[k].TryGetValue(id, out var old);
            if (old == null && (md.Src[k].Count >= MaxFor(k) || nw.N < 1)) { r.Rejected++; continue; }
            var merged = Atlas.Merge(old, nw);
            if (merged == null) { r.Rejected++; continue; }
            if (old != null && merged.SameAs(old)) { r.Unchanged++; continue; }
            long total = md.Total - (old?.N ?? 0) + merged.N;
            if (total > Atlas.MaxMemberTotal) { r.Rejected++; continue; }
            md.Total = total; md.Src[k][id] = merged;
            var a = Sum(k, id); a.Rev = ++rev; aggs[k][id] = a;
            r.Accepted++; r.Changed = true;
        }
    }

    void Vendors(ShareResult r, string identity, JsonNode sect, long now)
    {
        if (sect == null) return;
        if (sect is not JsonObject so) { r.Rejected++; return; }
        int seen = 0;
        foreach (var (key, node) in so)
        {
            if (++seen > Atlas.MaxVendorsPerShare) { r.Rejected++; continue; }
            if (!Atlas.Key(key, Atlas.MaxId, out var id) || !Atlas.ParseVendor(node, now, out var v)) { r.Rejected++; continue; }
            vendors.TryGetValue(id, out var old);
            if (old == null && vendors.Count >= Atlas.MaxVendors) { r.Rejected++; continue; }
            // newest list wins; the same moment from another member does not replace the first
            if (old != null && (v.At < old.At || (v.At == old.At && (old.By != identity || v.SameContent(old))))) { r.Unchanged++; continue; }
            v.By = identity; v.Rev = ++rev; vendors[id] = v;
            r.Accepted++; r.Changed = true;
        }
    }

    void Quests(ShareResult r, string identity, JsonNode sect, long now)
    {
        if (sect == null) return;
        if (sect is not JsonObject so) { r.Rejected++; return; }
        int seen = 0;
        foreach (var (key, node) in so)
        {
            if (++seen > Atlas.MaxQuestsPerShare) { r.Rejected++; continue; }
            if (!Atlas.Key(key, Atlas.MaxId, out var id) || !Atlas.ParseQuest(node, now, out var q)) { r.Rejected++; continue; }
            quests.TryGetValue(id, out var old);
            if (old == null && quests.Count >= Atlas.MaxQuests) { r.Rejected++; continue; }
            if (old != null && q.At <= old.At) { r.Unchanged++; continue; }
            q.By = identity; q.Rev = ++rev; quests[id] = q;
            r.Accepted++; r.Changed = true;
        }
    }

    void Spawns(ShareResult r, string identity, JsonNode sect)
    {
        if (sect == null) return;
        if (sect is not JsonObject so) { r.Rejected++; return; }
        int seen = 0;
        foreach (var (key, node) in so)
        {
            if (++seen > Atlas.MaxSpawnNpcsPerShare) { r.Rejected++; continue; }
            if (!Atlas.Key(key, Atlas.MaxId, out var id) || node is not JsonArray pa || pa.Count > Atlas.MaxPointsPerNpcPerShare) { r.Rejected++; continue; }
            spawns.TryGetValue(id, out var set);
            bool added = false, bad = false;
            foreach (var p in pa)
            {
                if (!Atlas.ParseSpawn(p, out var z, out var x, out var y)) { bad = true; continue; }
                set ??= new SpawnSet();
                if (set.Pts.Any(s => Atlas.SameSpot(s, z, x, y))) continue;
                if (set.Pts.Count >= Atlas.MaxSpawnPointsPerNpc || set.Pts.Count(s => s.By == identity) >= Atlas.MaxSpawnPointsPerNpcPerMember) { bad = true; continue; }
                if (set.Pts.Count == 0 && spawns.Count >= Atlas.MaxSpawnNpcs) { bad = true; continue; }
                set.Pts.Add(new Atlas.Spawn { Z = z, X = x, Y = y, By = identity });
                added = true;
            }
            if (added) { set.Rev = ++rev; spawns[id] = set; r.Accepted++; r.Changed = true; }
            else if (bad) r.Rejected++;
            else r.Unchanged++;
        }
    }

    void Revealed(ShareResult r, string identity, JsonNode sect)
    {
        if (sect == null) return;
        if (sect is not JsonObject so) { r.Rejected++; return; }
        int seen = 0;
        foreach (var (key, node) in so)
        {
            if (++seen > Atlas.MaxRevealedPerShare) { r.Rejected++; continue; }
            if (!Atlas.Key(key, Atlas.MaxItemId, out var id) || !Atlas.ParseRevealed(node, out var x)) { r.Rejected++; continue; }
            if (revealed.ContainsKey(id)) { r.Unchanged++; continue; }          // the first revealed description stays
            if (revealed.Count >= Atlas.MaxRevealed) { r.Rejected++; continue; }
            x.By = identity; x.Rev = ++rev; revealed[id] = x;
            r.Accepted++; r.Changed = true;
        }
    }

    delegate bool SeenParse<T>(JsonNode n, out T v);

    // npc / object / quest details the clients have seen (Atlas.MergeOf): a newer game build replaces the row (and becomes its author), the same build
    // may fill fields that are still empty, an older one changes nothing. Every member who adds or changes a row is counted in seenMembers.
    void Seen<T>(ShareResult r, string identity, JsonNode sect, int maxPerShare, Dictionary<int, T> table, int cap, SeenParse<T> parse, Func<T, T, bool> fill, Func<T, int> buildOf, Action<T, long, string> setRev)
    {
        if (sect == null) return;
        if (sect is not JsonObject so) { r.Rejected++; return; }
        int seen = 0;
        foreach (var (key, node) in so)
        {
            if (++seen > maxPerShare) { r.Rejected++; continue; }
            if (!Atlas.Key(key, Atlas.MaxId, out var id) || !parse(node, out var v)) { r.Rejected++; continue; }
            if (table.TryGetValue(id, out var old))
            {
                int before = buildOf(old);
                if (!fill(old, v)) { r.Unchanged++; continue; }
                setRev(old, ++rev, buildOf(old) != before ? identity : null);
            }
            else
            {
                if (table.Count >= cap) { r.Rejected++; continue; }
                setRev(v, ++rev, identity); table[id] = v;
            }
            if (seenMembers.Count < Atlas.MaxSeenMembers) seenMembers.Add(identity);
            r.Accepted++; r.Changed = true;
        }
    }

    void Nodes(ShareResult r, MemberData md, JsonNode sect)
    {
        if (sect == null) return;
        if (sect is not JsonObject so) { r.Rejected++; return; }
        int seen = 0, points = 0;
        foreach (var (key, node) in so)
        {
            if (++seen > Atlas.MaxNodeObjectsPerShare) { r.Rejected++; continue; }
            if (!Atlas.Key(key, Atlas.MaxId, out var id) || !Atlas.ParseNodeObject(node, out var maps, out var pts) || points + pts > Atlas.MaxNodePointsPerShare) { r.Rejected++; continue; }
            points += pts;
            Dictionary<int, List<Atlas.NodePt>> mine = null;
            bool isNew = !md.Nodes.TryGetValue(id, out mine);
            if (isNew && md.Nodes.Count >= Atlas.MaxNodeObjectsPerMember) { r.Rejected++; continue; }
            bool changed = false, full = false;
            var work = new Dictionary<int, List<Atlas.NodePt>>();
            int newMaps = 0, added = 0;
            foreach (var (map, inc) in maps)
            {
                List<Atlas.NodePt> have;
                if (mine != null && mine.TryGetValue(map, out var h)) have = h.Select(p => p.Clone()).ToList();
                else
                {
                    if ((mine?.Count ?? 0) + newMaps >= Atlas.MaxNodeMapsPerObject) { full = true; continue; }
                    have = new(); newMaps++;
                }
                int before = have.Count;
                bool c = Atlas.MergeNodes(have, inc, out var f);
                if (f) full = true;
                if (md.NodeTotal + added + (have.Count - before) > Atlas.MaxNodesPerMember) { full = true; continue; }
                added += have.Count - before;
                if (c) { work[map] = have; changed = true; }
            }
            if (!changed) { if (full) r.Rejected++; else r.Unchanged++; continue; }
            if (isNew) md.Nodes[id] = mine = new();
            foreach (var (map, l) in work) { md.NodeTotal += l.Count - (mine.TryGetValue(map, out var old) ? old.Count : 0); mine[map] = l; }
            var na = SumNodes(id); na.Rev = ++rev; nodeAggs[id] = na;
            r.Accepted++; r.Changed = true;
        }
    }

    void FishSpots(ShareResult r, MemberData md, JsonNode sect)
    {
        if (sect == null) return;
        if (sect is not JsonObject so) { r.Rejected++; return; }
        int seen = 0, points = 0;
        foreach (var (key, node) in so)
        {
            if (++seen > Atlas.MaxFishMapsPerShare) { r.Rejected++; continue; }
            if (!Atlas.Key(key, Atlas.MaxMapId, out var map) || !Atlas.ParseFishMap(node, out var inc) || points + inc.Count > Atlas.MaxFishSpotsPerShare) { r.Rejected++; continue; }
            points += inc.Count;
            bool isNew = !md.Fish.TryGetValue(map, out var mine);
            if (isNew && md.Fish.Count >= Atlas.MaxFishMapsPerMember) { r.Rejected++; continue; }
            var have = isNew ? new List<Atlas.FishSpot>() : mine.Select(p => p.Clone()).ToList();
            int before = have.Count;
            bool changed = Atlas.MergeFish(have, inc, out var full);
            if (md.FishTotal + (have.Count - before) > Atlas.MaxFishSpotsPerMember) { r.Rejected++; continue; }
            if (!changed) { if (full) r.Rejected++; else r.Unchanged++; continue; }
            md.FishTotal += have.Count - before; md.Fish[map] = have;
            fishAggs[map] = new FishAgg { Spots = SumFish(map), Rev = ++rev };
            r.Accepted++; r.Changed = true;
        }
    }

    void Finds(ShareResult r, MemberData md, string identity, string who, JsonNode sect, long now)
    {
        if (sect == null) return;
        if (sect is not JsonArray fa) { r.Rejected++; return; }
        int seen = 0;
        foreach (var node in fa)
        {
            if (++seen > Atlas.MaxFindsPerShare) { r.Rejected++; continue; }
            if (!Atlas.ParseFind(node, now, out var f)) { r.Rejected++; continue; }
            f.Identity = identity; f.Who = who;
            if (!findKeys.Add(f.Key)) { r.Unchanged++; continue; }
            f.Rev = ++rev;
            finds.Add(f); md.FindCount++;
            if (md.FindCount > Atlas.MaxFindsPerMember)
            {
                int i = finds.FindIndex(x => x.Identity == identity);
                findKeys.Remove(finds[i].Key); finds.RemoveAt(i); md.FindCount--;
            }
            while (finds.Count > Atlas.MaxFinds)
            {
                var old = finds[0]; findKeys.Remove(old.Key); finds.RemoveAt(0);
                if (members.TryGetValue(old.Identity, out var om)) om.FindCount--;
            }
            r.Accepted++; r.Changed = true;
        }
    }

    // ---------------------------------------------------------------- reading

    static JsonObject CtrJson(Atlas.Ctr c, int members, bool withMembers, long dup = 0)
    {
        var o = CtrJsonPlain(c, members, withMembers);
        Atlas.WriteX(o, c.X, !withMembers, withMembers ? dup : 0);
        return o;
    }

    static JsonObject CtrJsonPlain(Atlas.Ctr c, int members, bool withMembers)
    {
        var o = new JsonObject { ["n"] = c.N };
        if (withMembers) o["m"] = members;
        if (c.I.Count > 0)
        {
            var io = new JsonObject();
            foreach (var kv in c.I.OrderBy(k => k.Key)) io[kv.Key.ToString(CultureInfo.InvariantCulture)] = new JsonObject { ["n"] = kv.Value[0], ["c"] = kv.Value[1] };
            o["items"] = io;
        }
        if (c.Z.Count > 0)
        {
            var zo = new JsonObject();
            foreach (var kv in c.Z.OrderBy(k => k.Key)) zo[kv.Key.ToString(CultureInfo.InvariantCulture)] = kv.Value;
            o["z"] = zo;
        }
        return o;
    }

    static JsonArray FishJson(List<Atlas.FishSpot> spots) => new(spots.Select(p =>
    {
        var o = new JsonObject { ["x"] = p.X, ["y"] = p.Y, ["n"] = p.N };
        if (p.Items.Count > 0) { var io = new JsonObject(); foreach (var (id, c) in p.Items.OrderBy(k => k.Key)) io[id.ToString(CultureInfo.InvariantCulture)] = c; o["items"] = io; }
        return (JsonNode)o;
    }).ToArray());

    static JsonObject VendorItemJson(long[] a)
    {
        var o = new JsonObject { ["p"] = a[0], ["s"] = a[1], ["l"] = a[2] };
        if (a.Length > 3 && a[3] == 1) o["x"] = 1;
        return o;
    }

    static JsonObject VendorJson(Atlas.Vendor v)
    {
        var io = new JsonObject();
        foreach (var kv in v.Items.OrderBy(k => k.Key)) io[kv.Key.ToString(CultureInfo.InvariantCulture)] = VendorItemJson(kv.Value);
        return new JsonObject { ["at"] = v.At, ["z"] = v.Z, ["x"] = v.X, ["y"] = v.Y, ["items"] = io };
    }

    static JsonObject QuestJson(Atlas.Quest q) => new()
    {
        ["at"] = q.At,
        ["rewards"] = new JsonArray(q.Rewards.Select(i => (JsonNode)JsonValue.Create(i)).ToArray()),
        ["choices"] = new JsonArray(q.Choices.Select(i => (JsonNode)JsonValue.Create(i)).ToArray()),
    };

    static JsonObject RevealedJson(Atlas.Revealed x)
    {
        var o = new JsonObject { ["n"] = x.Name, ["q"] = x.Q, ["il"] = x.Il, ["rl"] = x.Rl, ["cls"] = x.Cls, ["sub"] = x.Sub, ["slot"] = x.Slot };
        if (x.St.Length > 0) o["st"] = new JsonArray(x.St.Select(p => (JsonNode)new JsonArray(p[0], p[1])).ToArray());
        return o;
    }

    static JsonObject SeenNpcJson(Atlas.SeenNpc x) => Atlas.SeenNpcJson(x);
    static JsonObject SeenObjectJson(Atlas.SeenObject x) => Atlas.SeenObjectJson(x);
    static JsonObject SeenQuestJson(Atlas.SeenQuest x) => Atlas.SeenQuestJson(x);

    static JsonObject NodesJson(NodeAgg a)
    {
        var o = new JsonObject();
        foreach (var (map, l) in a.Maps.OrderBy(k => k.Key))
            o[map.ToString(CultureInfo.InvariantCulture)] = new JsonArray(l.Select(p => (JsonNode)new JsonObject { ["x"] = p.X, ["y"] = p.Y, ["n"] = p.N }).ToArray());
        return o;
    }

    // 2.22 guests: a find shows only the day it happened and "a member", never who found it or at what time
    public const string GuestFindName = "a member";

    static JsonObject FindJson(Atlas.Find f, string by, bool dayOnly = false)
    {
        var o = new JsonObject { ["s"] = f.Rev, ["i"] = f.Item, ["c"] = f.Count, ["src"] = f.Src, ["at"] = dayOnly ? f.At - f.At % 86_400 : f.At, ["by"] = by };
        if (f.Z > 0) { o["z"] = f.Z; o["x"] = f.X; o["y"] = f.Y; }
        if (f.GameBuild > 0) o["gb"] = f.GameBuild;     // optional; older Outposts ignore it
        return o;
    }

    // { t:"atlas.data", epoch, rev, full?, more?, kills, objects, fishing, vendors, quests, spawns, items, finds, seenNpcs, seenObjects, seenQuests, nodes, fishspots, fieldnotes, entrances, npcspots }
    // Records changed after `since`, in revision order, cut into pages of about Atlas.PageBytes: with more = true ask again with since = rev.
    // full = the client's state cannot be continued (since 0, another epoch, or a revision this store never reached): it replaces everything it had.
    // The aggregate record of a source is replaced as a whole. nameOf turns a find's login into the name shown (display name). Caller holds Gate.
    public JsonObject Get(long since, string epoch, Func<string, string> nameOf, bool guest = false)
    {
        bool full = since <= 0 || since > rev || epoch != Epoch;
        if (full) since = 0;
        var cand = new List<(long rev, int sec, int key)>();
        for (int k = 0; k < KindCount; k++) foreach (var kv in aggs[k]) if (kv.Value.Rev > since) cand.Add((kv.Value.Rev, KindSec[k], kv.Key));
        foreach (var kv in vendors) if (kv.Value.Rev > since) cand.Add((kv.Value.Rev, 3, kv.Key));
        foreach (var kv in quests) if (kv.Value.Rev > since) cand.Add((kv.Value.Rev, 4, kv.Key));
        foreach (var kv in spawns) if (kv.Value.Rev > since) cand.Add((kv.Value.Rev, 5, kv.Key));
        foreach (var kv in revealed) if (kv.Value.Rev > since) cand.Add((kv.Value.Rev, 6, kv.Key));
        for (int i = 0; i < finds.Count; i++) if (finds[i].Rev > since) cand.Add((finds[i].Rev, 7, i));
        foreach (var kv in seenNpcs) if (kv.Value.Rev > since) cand.Add((kv.Value.Rev, 8, kv.Key));
        foreach (var kv in seenObjects) if (kv.Value.Rev > since) cand.Add((kv.Value.Rev, 9, kv.Key));
        foreach (var kv in seenQuests) if (kv.Value.Rev > since) cand.Add((kv.Value.Rev, 10, kv.Key));
        foreach (var kv in nodeAggs) if (kv.Value.Rev > since) cand.Add((kv.Value.Rev, 11, kv.Key));
        foreach (var kv in fishAggs) if (kv.Value.Rev > since) cand.Add((kv.Value.Rev, 12, kv.Key));
        foreach (var kv in noteAggs) if (kv.Value.Rev > since) cand.Add((kv.Value.Rev, 13, kv.Key));
        foreach (var kv in entranceAggs) if (kv.Value.Rev > since) cand.Add((kv.Value.Rev, 14, kv.Key));
        foreach (var kv in npcSpotAggs) if (kv.Value.Rev > since) cand.Add((kv.Value.Rev, 15, kv.Key));
        cand.Sort((a, b) => a.rev.CompareTo(b.rev));

        var secs = new JsonObject[22];
        for (int i = 0; i < 22; i++) secs[i] = new JsonObject();
        var fa = new JsonArray();
        int bytes = 0, taken = 0;
        long last = since;
        foreach (var c in cand)
        {
            JsonNode node; string key = c.key.ToString(CultureInfo.InvariantCulture);
            switch (c.sec)
            {
                case 0: case 1: case 2: case 16: case 17: case 18: case 19: case 20: case 21: { var a = aggs[Array.IndexOf(KindSec, c.sec)][c.key]; node = CtrJson(a.Sum, a.Members, true, a.Dup); break; }
                case 3: node = VendorJson(vendors[c.key]); break;
                case 4: node = QuestJson(quests[c.key]); break;
                case 5: node = new JsonArray(spawns[c.key].Pts.Select(p => (JsonNode)new JsonObject { ["z"] = p.Z, ["x"] = p.X, ["y"] = p.Y }).ToArray()); break;
                case 6: node = RevealedJson(revealed[c.key]); break;
                case 8: node = SeenNpcJson(seenNpcs[c.key]); break;
                case 9: node = SeenObjectJson(seenObjects[c.key]); break;
                case 10: node = SeenQuestJson(seenQuests[c.key]); break;
                case 11: node = NodesJson(nodeAggs[c.key]); break;
                case 12: node = FishJson(fishAggs[c.key].Spots); break;
                case 13: node = NoteJson(noteAggs[c.key], nameOf, guest); break;
                case 14: node = Atlas.EntranceJson(entranceAggs[c.key].Spots, true); break;
                case 15: node = Atlas.NpcSpotJson(npcSpotAggs[c.key].Spots, true); break;
                default: node = guest ? FindJson(finds[c.key], GuestFindName, true) : FindJson(finds[c.key], nameOf(finds[c.key].Who)); break;
            }
            int size = node.ToJsonString().Length + key.Length + 6;
            if (taken > 0 && bytes + size > Atlas.PageBytes) break;
            if (c.sec == 7) fa.Add(node); else secs[c.sec][key] = node;
            bytes += size; taken++; last = c.rev;
        }
        bool more = taken < cand.Count;
        // the envelope is typed (Elans.Shared AtlasDataMsg); the sections are the free-form objects built above
        var res = new AtlasDataMsg
        {
            Epoch = Epoch, Rev = more ? last : rev, At = NowSec, Finds = fa,
            SeenMembers = seenMembers.Count,   // 2.25: how many members' game caches the seen tables come from (a count, never who)
            Full = full, More = more,
        };
        for (int i = 0; i < 22; i++) if (i != 7) res.Sections[AtlasSections.All[i]] = secs[i];     // secs[i] is section AtlasSections.All[i] (7 = finds, the array)
        return LodgeFieldsJson.Build(res.Write);
    }

    // ---------------------------------------------------------------- saving

    // Saves when something changed and the last save is at least `minAge` ago (Zero = now). Caller holds Gate.
    public bool SaveIfDirty(TimeSpan minAge)
    {
        if (!Dirty || clock.UtcNow - lastSave < minAge) return true;
        return Save();
    }

    public bool Save()
    {
        try
        {
            var mo = new JsonObject();
            var srcRev = new JsonObject();
            foreach (var (who, md) in members)
            {
                var d = new JsonObject();
                if (md.Who.Length > 0) d["w"] = md.Who;
                for (int k = 0; k < KindCount; k++)
                {
                    if (md.Src[k].Count == 0) continue;
                    var so = new JsonObject();
                    foreach (var (id, c) in md.Src[k]) so[id.ToString(CultureInfo.InvariantCulture)] = CtrJson(c, 0, false);
                    d[KindName(k)] = so;
                }
                if (md.Nodes.Count > 0)
                {
                    var no = new JsonObject();
                    foreach (var (oid, maps) in md.Nodes)
                    {
                        var oo = new JsonObject();
                        foreach (var (map, l) in maps) oo[map.ToString(CultureInfo.InvariantCulture)] = new JsonArray(l.Select(p => (JsonNode)new JsonArray(p.X, p.Y, p.N)).ToArray());
                        no[oid.ToString(CultureInfo.InvariantCulture)] = oo;
                    }
                    d["nodes"] = no;
                }
                if (md.Fish.Count > 0)
                {
                    var fo = new JsonObject();
                    foreach (var (map, l) in md.Fish) fo[map.ToString(CultureInfo.InvariantCulture)] = FishJson(l);
                    d["fishspots"] = fo;
                }
                if (md.Notes.Count > 0)
                {
                    var fno = new JsonObject();
                    foreach (var (npc, n) in md.Notes) fno[npc.ToString(CultureInfo.InvariantCulture)] = NoteStoreJson(n);
                    d["fieldnotes"] = fno;
                }
                if (md.Entrances.Count > 0) d["entrances"] = EntrancesStoreJson(md);
                if (md.NpcSpots.Count > 0) d["npcspots"] = NpcSpotsStoreJson(md);
                mo[who] = d;
            }
            var fishRev = new JsonObject();
            foreach (var (map, a) in fishAggs) fishRev[map.ToString(CultureInfo.InvariantCulture)] = a.Rev;
            var noteRev = new JsonObject();
            foreach (var (id, a) in noteAggs) noteRev[id.ToString(CultureInfo.InvariantCulture)] = a.Rev;
            var nodeRev = new JsonObject();
            foreach (var (id, a) in nodeAggs) nodeRev[id.ToString(CultureInfo.InvariantCulture)] = a.Rev;
            for (int k = 0; k < KindCount; k++)
            {
                var ro = new JsonObject();
                foreach (var (id, a) in aggs[k]) ro[id.ToString(CultureInfo.InvariantCulture)] = a.Rev;
                srcRev[KindName(k)] = ro;
            }
            var vo = new JsonObject();
            foreach (var (id, v) in vendors) { var j = VendorJson(v); j["b"] = v.By; j["r"] = v.Rev; vo[id.ToString(CultureInfo.InvariantCulture)] = j; }
            var qo = new JsonObject();
            foreach (var (id, q) in quests) { var j = QuestJson(q); j["b"] = q.By; j["r"] = q.Rev; qo[id.ToString(CultureInfo.InvariantCulture)] = j; }
            var sp = new JsonObject();
            foreach (var (id, s) in spawns)
                sp[id.ToString(CultureInfo.InvariantCulture)] = new JsonObject
                {
                    ["r"] = s.Rev,
                    ["p"] = new JsonArray(s.Pts.Select(p => (JsonNode)new JsonObject { ["z"] = p.Z, ["x"] = p.X, ["y"] = p.Y, ["b"] = p.By }).ToArray()),
                };
            var io = new JsonObject();
            foreach (var (id, x) in revealed) { var j = RevealedJson(x); j["b"] = x.By; j["r"] = x.Rev; io[id.ToString(CultureInfo.InvariantCulture)] = j; }
            var sno = new JsonObject();
            foreach (var (id, x) in seenNpcs) { var j = SeenNpcJson(x); j["b"] = x.By; j["r"] = x.Rev; sno[id.ToString(CultureInfo.InvariantCulture)] = j; }
            var sob = new JsonObject();
            foreach (var (id, x) in seenObjects) { var j = SeenObjectJson(x); j["b"] = x.By; j["r"] = x.Rev; sob[id.ToString(CultureInfo.InvariantCulture)] = j; }
            var sqo = new JsonObject();
            foreach (var (id, x) in seenQuests) { var j = SeenQuestJson(x); j["b"] = x.By; j["r"] = x.Rev; sqo[id.ToString(CultureInfo.InvariantCulture)] = j; }
            var fa = new JsonArray();
            foreach (var f in finds) { var j = FindJson(f, ""); j.Remove("by"); j.Remove("s"); j["b"] = f.Identity; j["w"] = f.Who; j["r"] = f.Rev; fa.Add(j); }
            var root = new JsonObject
            {
                ["v"] = 1, ["epoch"] = Epoch, ["rev"] = rev, ["members"] = mo, ["srcRev"] = srcRev, ["nodeRev"] = nodeRev, ["fishRev"] = fishRev, ["noteRev"] = noteRev, ["entranceRev"] = EntranceRevJson(), ["npcSpotRev"] = NpcSpotRevJson(),
                ["vendors"] = vo, ["quests"] = qo, ["spawns"] = sp, ["items"] = io, ["finds"] = fa,
                ["seenNpcs"] = sno, ["seenObjects"] = sob, ["seenQuests"] = sqo,
                ["seenMembers"] = new JsonArray(seenMembers.OrderBy(x => x, StringComparer.Ordinal).Select(x => (JsonNode)x).ToArray()),
            };
            var bytes = Encoding.UTF8.GetBytes(root.ToJsonString());
            if (bytes.Length > MaxFileBytes)
            {
                // never replace a good file with one the next start cannot load
                storedBytes = Math.Max(storedBytes, bytes.Length);
                log?.LogWarning("Atlas: not saved - the data ({Bytes} bytes) is over the {Cap} byte limit; revert a member's contributions to make room", bytes.Length, MaxFileBytes);
                return false;
            }
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
            Dirty = false; lastSave = clock.UtcNow;
            storedBytes = bytes.Length; if (storedBytes < WriteCapBytes) capLogged = false;
            return true;
        }
        catch (Exception e)
        {
            log?.LogWarning("Atlas: save failed: {Error}", e.GetType().Name);
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            return false;
        }
    }
}
