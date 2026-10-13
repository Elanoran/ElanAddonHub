using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Lodge;

// data/atlas.json = { v, epoch, rev, members: { identity: { kills, objects, fishing } }, vendors, quests, spawns, items, finds }
// Atomic (tmp, flush, rename; a leftover tmp is used only when the main file is missing), size-capped on load, every loaded value goes through the same
// validators as a share. `rev` counts changes: every stored/changed record carries the revision it last changed at, atlas.get {since} returns the records
// with a newer one. `epoch` names this data set: a client holding another epoch (data restored from a backup, deleted) gets everything again.
// Never logs any of its content.
public sealed class AtlasStore
{
    public const int Kills = 0, Objects = 1, Fishing = 2;
    const long MaxFileBytes = 64L * 1024 * 1024;
    const int MaxMembers = 2000;

    sealed class MemberData
    {
        public readonly Dictionary<int, Atlas.Ctr>[] Src = { new(), new(), new() };
        public long Total;
        public int FindCount;
    }

    public sealed class Agg { public Atlas.Ctr Sum = new(); public int Members; public long Rev; }

    sealed class SpawnSet { public List<Atlas.Spawn> Pts = new(); public long Rev; }

    readonly string path, tmp;
    readonly ILogger log;
    readonly IClock clock;
    public readonly object Gate = new();
    readonly Dictionary<string, MemberData> members = new(StringComparer.Ordinal);
    readonly Dictionary<int, Agg>[] aggs = { new(), new(), new() };
    readonly Dictionary<int, Atlas.Vendor> vendors = new();
    readonly Dictionary<int, Atlas.Quest> quests = new();
    readonly Dictionary<int, SpawnSet> spawns = new();
    readonly Dictionary<int, Atlas.Revealed> revealed = new();
    readonly Dictionary<int, Atlas.SeenNpc> seenNpcs = new();
    readonly Dictionary<int, Atlas.SeenObject> seenObjects = new();
    readonly Dictionary<int, Atlas.SeenQuest> seenQuests = new();
    readonly List<Atlas.Find> finds = new();
    readonly HashSet<string> findKeys = new(StringComparer.Ordinal);
    long rev;
    DateTime lastSave = DateTime.MinValue;
    public bool Dirty { get; private set; }
    public string Epoch { get; private set; }

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
    public int SourceCount(int kind) { lock (Gate) return aggs[kind].Count; }

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
                for (int k = 0; k < 3; k++)
                {
                    if (d[KindName(k)] is not JsonObject so) continue;
                    foreach (var (key, node) in so)
                    {
                        if (md.Src[k].Count >= MaxFor(k) || !Atlas.Key(key, k == Fishing ? Atlas.MaxMapId : Atlas.MaxId, out var id) || !Atlas.ParseCtr(node, k != Fishing, out var c)) continue;
                        if (md.Total + c.N > Atlas.MaxMemberTotal) continue;
                        md.Src[k][id] = c; md.Total += c.N;
                    }
                }
                members[who] = md;
            }
        // the aggregates are derived: rebuild them, remembering the revision of the member data
        var revs = root["srcRev"] as JsonObject;
        for (int k = 0; k < 3; k++)
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
        if (root["vendors"] is JsonObject vo)
            foreach (var (key, node) in vo)
                if (vendors.Count < Atlas.MaxVendors && Atlas.Key(key, Atlas.MaxId, out var id) && Atlas.ParseVendor(node, now, out var v)) { v.By = By(node); v.Rev = R(node); vendors[id] = v; }
        if (root["quests"] is JsonObject qo)
            foreach (var (key, node) in qo)
                if (quests.Count < Atlas.MaxQuests && Atlas.Key(key, Atlas.MaxId, out var id) && Atlas.ParseQuest(node, now, out var q)) { q.Rev = R(node); quests[id] = q; }
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
                if (seenNpcs.Count < Atlas.MaxSeenNpcs && Atlas.Key(key, Atlas.MaxId, out var id) && Atlas.ParseSeenNpc(node, out var x)) { x.Rev = R(node); seenNpcs[id] = x; }
        if (root["seenObjects"] is JsonObject sob)
            foreach (var (key, node) in sob)
                if (seenObjects.Count < Atlas.MaxSeenObjects && Atlas.Key(key, Atlas.MaxId, out var id) && Atlas.ParseSeenObject(node, out var x)) { x.Rev = R(node); seenObjects[id] = x; }
        if (root["seenQuests"] is JsonObject sqo)
            foreach (var (key, node) in sqo)
                if (seenQuests.Count < Atlas.MaxSeenQuests && Atlas.Key(key, Atlas.MaxId, out var id) && Atlas.ParseSeenQuest(node, out var x)) { x.Rev = R(node); seenQuests[id] = x; }
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

    static string KindName(int k) => k switch { Kills => "kills", Objects => "objects", _ => "fishing" };
    static int MaxFor(int k) => k switch { Kills => Atlas.MaxKillSources, Objects => Atlas.MaxObjectSources, _ => Atlas.MaxFishingZones };

    Agg Sum(int k, int id)
    {
        var a = new Agg();
        foreach (var m in members.Values)
        {
            if (!m.Src[k].TryGetValue(id, out var c)) continue;
            a.Members++; a.Sum.N += c.N;
            foreach (var kv in c.I)
            {
                if (a.Sum.I.TryGetValue(kv.Key, out var x)) { x[0] += kv.Value[0]; x[1] += kv.Value[1]; }
                else a.Sum.I[kv.Key] = new[] { kv.Value[0], kv.Value[1] };
            }
            foreach (var kv in c.Z) a.Sum.Z[kv.Key] = a.Sum.Z.TryGetValue(kv.Key, out var z) ? z + kv.Value : kv.Value;
        }
        return a;
    }

    // ---------------------------------------------------------------- sharing

    public sealed class ShareResult
    {
        public int Accepted, Unchanged, Rejected;
        public bool Changed;
        public int Total => Accepted + Unchanged + Rejected;
    }

    static readonly string[] Sections = { "kills", "objects", "fishing", "vendors", "quests", "finds", "spawns", "items", "seenNpcs", "seenObjects", "seenQuests" };

    // True when the message carries at least one known, non-empty section
    public static bool HasContent(JsonObject m) =>
        Sections.Any(s => s == "finds" ? m[s] is JsonArray a && a.Count > 0 : m[s] is JsonObject o && o.Count > 0);

    // One member's batch. Counters are cumulative totals; per field the stored value only ever grows (see Atlas). Caller holds Gate.
    public ShareResult Share(string identity, string who, JsonObject m)
    {
        var r = new ShareResult();
        long now = NowSec;
        if (!members.TryGetValue(identity, out var md))
        {
            if (members.Count >= MaxMembers) { r.Rejected++; return r; }
            md = new MemberData();
            members[identity] = md;
        }
        Counters(r, md, identity, Kills, m["kills"], Atlas.MaxKillsPerShare);
        Counters(r, md, identity, Objects, m["objects"], Atlas.MaxObjectsPerShare);
        Counters(r, md, identity, Fishing, m["fishing"], Atlas.MaxFishingPerShare);
        Vendors(r, identity, m["vendors"], now);
        Quests(r, m["quests"], now);
        Spawns(r, identity, m["spawns"]);
        Revealed(r, identity, m["items"]);
        Seen(r, m["seenNpcs"], Atlas.MaxSeenNpcsPerShare, seenNpcs, Atlas.MaxSeenNpcs, (JsonNode n, out Atlas.SeenNpc v) => Atlas.ParseSeenNpc(n, out v), (a, b) => a.Fill(b), (v, rv) => v.Rev = rv);
        Seen(r, m["seenObjects"], Atlas.MaxSeenObjectsPerShare, seenObjects, Atlas.MaxSeenObjects, (JsonNode n, out Atlas.SeenObject v) => Atlas.ParseSeenObject(n, out v), (a, b) => a.Fill(b), (v, rv) => v.Rev = rv);
        Seen(r, m["seenQuests"], Atlas.MaxSeenQuestsPerShare, seenQuests, Atlas.MaxSeenQuests, (JsonNode n, out Atlas.SeenQuest v) => Atlas.ParseSeenQuest(n, out v), (a, b) => a.Fill(b), (v, rv) => v.Rev = rv);
        Finds(r, md, identity, who, m["finds"], now);
        if (md.Total == 0 && md.FindCount == 0 && !r.Changed && md.Src.All(s => s.Count == 0)) members.Remove(identity);   // nothing of this person was kept
        if (r.Changed) Dirty = true;
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
            if (!Atlas.Key(key, k == Fishing ? Atlas.MaxMapId : Atlas.MaxId, out var id) || !Atlas.ParseCtr(node, k != Fishing, out var nw)) { r.Rejected++; continue; }
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

    void Quests(ShareResult r, JsonNode sect, long now)
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
            q.Rev = ++rev; quests[id] = q;
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

    // npc / object / quest details the clients have seen: the first good description of an id stays, a later one may fill fields that are still empty
    void Seen<T>(ShareResult r, JsonNode sect, int maxPerShare, Dictionary<int, T> table, int cap, SeenParse<T> parse, Func<T, T, bool> fill, Action<T, long> setRev)
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
                if (!fill(old, v)) { r.Unchanged++; continue; }
                setRev(old, ++rev);
            }
            else
            {
                if (table.Count >= cap) { r.Rejected++; continue; }
                setRev(v, ++rev); table[id] = v;
            }
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

    static JsonObject CtrJson(Atlas.Ctr c, int members, bool withMembers)
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

    static JsonObject VendorJson(Atlas.Vendor v)
    {
        var io = new JsonObject();
        foreach (var kv in v.Items.OrderBy(k => k.Key)) io[kv.Key.ToString(CultureInfo.InvariantCulture)] = new JsonObject { ["p"] = kv.Value[0], ["s"] = kv.Value[1], ["l"] = kv.Value[2] };
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

    static JsonObject SeenNpcJson(Atlas.SeenNpc x) => new() { ["name"] = x.Name, ["title"] = x.Title, ["type"] = x.Type, ["family"] = x.Family, ["rank"] = x.Rank };
    static JsonObject SeenObjectJson(Atlas.SeenObject x) => new() { ["name"] = x.Name, ["type"] = x.Type };
    static JsonObject SeenQuestJson(Atlas.SeenQuest x) => new() { ["title"] = x.Title, ["level"] = x.Level, ["minLevel"] = x.MinLevel, ["objectives"] = x.Objectives };

    static JsonObject FindJson(Atlas.Find f, string by)
    {
        var o = new JsonObject { ["s"] = f.Rev, ["i"] = f.Item, ["c"] = f.Count, ["src"] = f.Src, ["at"] = f.At, ["by"] = by };
        if (f.Z > 0) { o["z"] = f.Z; o["x"] = f.X; o["y"] = f.Y; }
        return o;
    }

    // { t:"atlas.data", epoch, rev, full?, more?, kills, objects, fishing, vendors, quests, spawns, items, finds, seenNpcs, seenObjects, seenQuests }
    // Records changed after `since`, in revision order, cut into pages of about Atlas.PageBytes: with more = true ask again with since = rev.
    // full = the client's state cannot be continued (since 0, another epoch, or a revision this store never reached): it replaces everything it had.
    // The aggregate record of a source is replaced as a whole. nameOf turns a find's login into the name shown (display name). Caller holds Gate.
    public JsonObject Get(long since, string epoch, Func<string, string> nameOf)
    {
        bool full = since <= 0 || since > rev || epoch != Epoch;
        if (full) since = 0;
        var cand = new List<(long rev, int sec, int key)>();
        for (int k = 0; k < 3; k++) foreach (var kv in aggs[k]) if (kv.Value.Rev > since) cand.Add((kv.Value.Rev, k, kv.Key));
        foreach (var kv in vendors) if (kv.Value.Rev > since) cand.Add((kv.Value.Rev, 3, kv.Key));
        foreach (var kv in quests) if (kv.Value.Rev > since) cand.Add((kv.Value.Rev, 4, kv.Key));
        foreach (var kv in spawns) if (kv.Value.Rev > since) cand.Add((kv.Value.Rev, 5, kv.Key));
        foreach (var kv in revealed) if (kv.Value.Rev > since) cand.Add((kv.Value.Rev, 6, kv.Key));
        for (int i = 0; i < finds.Count; i++) if (finds[i].Rev > since) cand.Add((finds[i].Rev, 7, i));
        foreach (var kv in seenNpcs) if (kv.Value.Rev > since) cand.Add((kv.Value.Rev, 8, kv.Key));
        foreach (var kv in seenObjects) if (kv.Value.Rev > since) cand.Add((kv.Value.Rev, 9, kv.Key));
        foreach (var kv in seenQuests) if (kv.Value.Rev > since) cand.Add((kv.Value.Rev, 10, kv.Key));
        cand.Sort((a, b) => a.rev.CompareTo(b.rev));

        var secs = new JsonObject[11];
        for (int i = 0; i < 11; i++) secs[i] = new JsonObject();
        var fa = new JsonArray();
        int bytes = 0, taken = 0;
        long last = since;
        foreach (var c in cand)
        {
            JsonNode node; string key = c.key.ToString(CultureInfo.InvariantCulture);
            switch (c.sec)
            {
                case 0: case 1: case 2: { var a = aggs[c.sec][c.key]; node = CtrJson(a.Sum, a.Members, true); break; }
                case 3: node = VendorJson(vendors[c.key]); break;
                case 4: node = QuestJson(quests[c.key]); break;
                case 5: node = new JsonArray(spawns[c.key].Pts.Select(p => (JsonNode)new JsonObject { ["z"] = p.Z, ["x"] = p.X, ["y"] = p.Y }).ToArray()); break;
                case 6: node = RevealedJson(revealed[c.key]); break;
                case 8: node = SeenNpcJson(seenNpcs[c.key]); break;
                case 9: node = SeenObjectJson(seenObjects[c.key]); break;
                case 10: node = SeenQuestJson(seenQuests[c.key]); break;
                default: node = FindJson(finds[c.key], nameOf(finds[c.key].Who)); break;
            }
            int size = node.ToJsonString().Length + key.Length + 6;
            if (taken > 0 && bytes + size > Atlas.PageBytes) break;
            if (c.sec == 7) fa.Add(node); else secs[c.sec][key] = node;
            bytes += size; taken++; last = c.rev;
        }
        bool more = taken < cand.Count;
        var res = new JsonObject
        {
            ["t"] = "atlas.data", ["epoch"] = Epoch, ["rev"] = more ? last : rev, ["at"] = NowSec,
            ["kills"] = secs[0], ["objects"] = secs[1], ["fishing"] = secs[2], ["vendors"] = secs[3], ["quests"] = secs[4], ["spawns"] = secs[5], ["items"] = secs[6], ["finds"] = fa,
            ["seenNpcs"] = secs[8], ["seenObjects"] = secs[9], ["seenQuests"] = secs[10],
        };
        if (full) res["full"] = true;
        if (more) res["more"] = true;
        return res;
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
                for (int k = 0; k < 3; k++)
                {
                    if (md.Src[k].Count == 0) continue;
                    var so = new JsonObject();
                    foreach (var (id, c) in md.Src[k]) so[id.ToString(CultureInfo.InvariantCulture)] = CtrJson(c, 0, false);
                    d[KindName(k)] = so;
                }
                mo[who] = d;
            }
            for (int k = 0; k < 3; k++)
            {
                var ro = new JsonObject();
                foreach (var (id, a) in aggs[k]) ro[id.ToString(CultureInfo.InvariantCulture)] = a.Rev;
                srcRev[KindName(k)] = ro;
            }
            var vo = new JsonObject();
            foreach (var (id, v) in vendors) { var j = VendorJson(v); j["b"] = v.By; j["r"] = v.Rev; vo[id.ToString(CultureInfo.InvariantCulture)] = j; }
            var qo = new JsonObject();
            foreach (var (id, q) in quests) { var j = QuestJson(q); j["r"] = q.Rev; qo[id.ToString(CultureInfo.InvariantCulture)] = j; }
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
            foreach (var (id, x) in seenNpcs) { var j = SeenNpcJson(x); j["r"] = x.Rev; sno[id.ToString(CultureInfo.InvariantCulture)] = j; }
            var sob = new JsonObject();
            foreach (var (id, x) in seenObjects) { var j = SeenObjectJson(x); j["r"] = x.Rev; sob[id.ToString(CultureInfo.InvariantCulture)] = j; }
            var sqo = new JsonObject();
            foreach (var (id, x) in seenQuests) { var j = SeenQuestJson(x); j["r"] = x.Rev; sqo[id.ToString(CultureInfo.InvariantCulture)] = j; }
            var fa = new JsonArray();
            foreach (var f in finds) { var j = FindJson(f, ""); j.Remove("by"); j.Remove("s"); j["b"] = f.Identity; j["w"] = f.Who; j["r"] = f.Rev; fa.Add(j); }
            var root = new JsonObject
            {
                ["v"] = 1, ["epoch"] = Epoch, ["rev"] = rev, ["members"] = mo, ["srcRev"] = srcRev,
                ["vendors"] = vo, ["quests"] = qo, ["spawns"] = sp, ["items"] = io, ["finds"] = fa,
                ["seenNpcs"] = sno, ["seenObjects"] = sob, ["seenQuests"] = sqo,
            };
            var bytes = Encoding.UTF8.GetBytes(root.ToJsonString());
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
            Dirty = false; lastSave = clock.UtcNow;
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
