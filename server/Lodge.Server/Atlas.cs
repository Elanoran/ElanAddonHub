using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Lodge;

// Crowd-shared Atlas observations (2.15, feature "atlas"). Members upload what Elan's Toolkit saw (loot per npc / object / fishing zone,
// vendors, quest rewards, finds, spawn points, revealed item details); the server keeps ONE record set per member and shows everybody
// the sum. This file holds the pure rules (validation, merging); AtlasStore keeps the state in <data>/atlas.json; AtlasModule talks to clients.
//
// Idempotency: counters are CUMULATIVE per member. A share carries a member's totals for the entries that changed (not increments), and the
// server stores, per field, max(stored, received). Sending the same batch twice, losing an answer and resending, or restoring an old upload
// record can therefore never count anything twice. The aggregate is the sum over members of their stored totals.
public static partial class Atlas
{
    // ---- ids and counts
    public const int MaxId = 9_999_999;          // npc, object, quest
    public const int MaxItemId = 999_999;
    public const int MaxMapId = 99_999;          // uiMapId
    public const long MaxCount = 100_000;        // kills / opens / casts of ONE member on ONE source
    public const long MaxMemberTotal = 1_000_000; // all of one member's counters together
    public const int MaxDropsPerKill = 10;       // dropped-item events (n) <= kills * 10
    public const int MaxStack = 200;             // items in those events (c) <= n * 200
    public const int MaxItemsPerSource = 150;
    public const int MaxZonesPerSource = 40;

    // ---- per member / per table
    public const int MaxKillSources = 6000, MaxObjectSources = 3000, MaxFishingZones = 400;   // per member
    public const int MaxVendors = 6000, MaxQuests = 20000, MaxSpawnNpcs = 8000, MaxRevealed = 40000;
    public const int MaxVendorItems = 300, MaxQuestItems = 10;
    public const int MaxFinds = 2000, MaxFindsPerMember = 700;
    public const int MaxSpawnPointsPerNpc = 50, MaxSpawnPointsPerNpcPerMember = 10;
    public const int MaxStats = 10;
    public const int MaxNameLength = 80;
    public const double SpawnMerge = 0.01;       // two points of one zone closer than this (x and y) are the same point

    // ---- per share message (the frame limit is 64 KB)
    public const int MaxKillsPerShare = 200, MaxObjectsPerShare = 100, MaxFishingPerShare = 50, MaxVendorsPerShare = 60,
        MaxQuestsPerShare = 200, MaxFindsPerShare = 500, MaxSpawnNpcsPerShare = 200, MaxPointsPerNpcPerShare = 20, MaxRevealedPerShare = 200;
    public const int PageBytes = 200_000;        // atlas.data is cut into pages of about this size
    public static readonly TimeSpan ShareEvery = TimeSpan.FromMinutes(2);   // refill of the per-member bucket
    public const int ShareBurst = 3;

    public const long MinUnix = 1_000_000_000L;  // 2001: an observation older than that is no observation
    public const long FutureSlackSeconds = 86_400;

    [GeneratedRegex(@"^(npc:[1-9][0-9]{0,6}|obj:[1-9][0-9]{0,6}|vendor:[1-9][0-9]{0,6}|quest:[1-9][0-9]{0,6}|fish)\z", RegexOptions.CultureInvariant)]
    private static partial Regex SrcRx();

    public static bool ValidSrc(string s) => s != null && s.Length <= 16 && SrcRx().IsMatch(s);

    // a table key: plain decimal digits without a leading zero or sign, 1..max
    public static bool Key(string k, int max, out int id)
    {
        id = 0;
        if (string.IsNullOrEmpty(k) || k.Length > 7 || k[0] == '0') return false;
        foreach (var ch in k) if (ch < '0' || ch > '9') return false;
        id = int.Parse(k, System.Globalization.CultureInfo.InvariantCulture);
        return id >= 1 && id <= max;
    }

    static bool Dbl(JsonNode n, out double d)
    {
        d = 0;
        if (n is JsonValue v && !v.TryGetValue<string>(out _) && !v.TryGetValue<bool>(out _) && v.TryGetValue<double>(out d)) return !double.IsNaN(d) && !double.IsInfinity(d);
        return false;
    }

    public static bool Whole(JsonNode n, long min, long max, out long v)
    {
        v = 0;
        if (!Dbl(n, out var d) || d != Math.Floor(d) || d < min || d > max) return false;
        v = (long)d; return true;
    }

    public static bool Coord(JsonNode n, out double v)
    {
        if (!Dbl(n, out v) || v < 0 || v > 1) return false;
        v = Math.Round(v, 3, MidpointRounding.AwayFromZero); return true;
    }

    public static bool Time(JsonNode n, long now, out long at) => Whole(n, MinUnix, now + FutureSlackSeconds, out at);

    // a name for display: cleaned like any text, without the characters a Lua/JSON string would need to escape
    public static string CleanName(string s)
    {
        var r = Names.Text(s ?? "", MaxNameLength * 2);
        r = new string(r.Where(c => c is not ('"' or '\\' or '<' or '>' or '|' or '[' or ']')).ToArray()).Trim();
        return Names.Cut(r, MaxNameLength).Trim();
    }

    // ---------------------------------------------------------------- counters (kills, objects, fishing)

    // one member's totals on one source: n = looted kills / opens / casts, I[item] = {times dropped, total count}, Z[uiMap] = n there
    public sealed class Ctr
    {
        public long N;
        public Dictionary<int, long[]> I = new();
        public Dictionary<int, long> Z = new();
        public Ctr Clone()
        {
            var c = new Ctr { N = N };
            foreach (var kv in I) c.I[kv.Key] = new[] { kv.Value[0], kv.Value[1] };
            foreach (var kv in Z) c.Z[kv.Key] = kv.Value;
            return c;
        }
        public bool SameAs(Ctr o)
        {
            if (o == null || N != o.N || I.Count != o.I.Count || Z.Count != o.Z.Count) return false;
            foreach (var kv in I) if (!o.I.TryGetValue(kv.Key, out var x) || x[0] != kv.Value[0] || x[1] != kv.Value[1]) return false;
            foreach (var kv in Z) if (!o.Z.TryGetValue(kv.Key, out var x) || x != kv.Value) return false;
            return true;
        }
    }

    // All-or-nothing: a malformed entry yields false. Cross-checks: dropped events <= kills * 10, count between n and n * 200, the zones add up to no more than n.
    public static bool ParseCtr(JsonNode node, bool zones, out Ctr c)
    {
        c = null;
        if (node is not JsonObject o || !Whole(o["n"], 0, MaxCount, out var n)) return false;
        var r = new Ctr { N = n };
        if (o["items"] != null)
        {
            if (o["items"] is not JsonObject io || io.Count > MaxItemsPerSource) return false;
            foreach (var (k, v) in io)
            {
                if (!Key(k, MaxItemId, out var id) || v is not JsonObject it) return false;
                if (!Whole(it["n"], 1, n * MaxDropsPerKill, out var dn) || !Whole(it["c"], dn, dn * MaxStack, out var dc)) return false;
                r.I[id] = new[] { dn, dc };
            }
        }
        if (zones && o["z"] != null)
        {
            if (o["z"] is not JsonObject zo || zo.Count > MaxZonesPerSource) return false;
            long sum = 0;
            foreach (var (k, v) in zo)
            {
                if (!Key(k, MaxMapId, out var id) || !Whole(v, 1, n, out var zn)) return false;
                r.Z[id] = zn; sum += zn;
            }
            if (sum > n) return false;
        }
        c = r;
        return true;
    }

    // per field max(old, new): idempotent and monotonic. Null when the result breaks the cross-checks (then nothing is stored).
    public static Ctr Merge(Ctr old, Ctr nw)
    {
        if (old == null) return nw.Clone();
        var r = old.Clone();
        r.N = Math.Max(old.N, nw.N);
        foreach (var kv in nw.I)
        {
            if (r.I.TryGetValue(kv.Key, out var x)) { x[0] = Math.Max(x[0], kv.Value[0]); x[1] = Math.Max(x[1], kv.Value[1]); }
            else r.I[kv.Key] = new[] { kv.Value[0], kv.Value[1] };
        }
        foreach (var kv in nw.Z) r.Z[kv.Key] = r.Z.TryGetValue(kv.Key, out var z) ? Math.Max(z, kv.Value) : kv.Value;
        if (r.I.Count > MaxItemsPerSource || r.Z.Count > MaxZonesPerSource || r.Z.Values.Sum() > r.N) return null;
        foreach (var kv in r.I) if (kv.Value[0] > r.N * MaxDropsPerKill || kv.Value[1] > kv.Value[0] * MaxStack || kv.Value[1] < kv.Value[0]) return null;
        return r;
    }

    // ---------------------------------------------------------------- vendors, quests, spawns, revealed items, finds

    public sealed class Vendor
    {
        public long At, Rev; public int Z; public double X, Y; public string By;
        public Dictionary<int, long[]> Items = new();   // item -> { price (copper), stack size, limit (-1 none) }
        public bool SameContent(Vendor o) => o != null && At == o.At && Z == o.Z && X == o.X && Y == o.Y && Items.Count == o.Items.Count
            && Items.All(kv => o.Items.TryGetValue(kv.Key, out var x) && x[0] == kv.Value[0] && x[1] == kv.Value[1] && x[2] == kv.Value[2]);
    }

    public static bool ParseVendor(JsonNode node, long now, out Vendor v)
    {
        v = null;
        if (node is not JsonObject o || !Time(o["at"], now, out var at) || !Whole(o["z"], 1, MaxMapId, out var z) || !Coord(o["x"], out var x) || !Coord(o["y"], out var y)) return false;
        if (o["items"] is not JsonObject io || io.Count > MaxVendorItems) return false;
        var r = new Vendor { At = at, Z = (int)z, X = x, Y = y };
        foreach (var (k, e) in io)
        {
            if (!Key(k, MaxItemId, out var id) || e is not JsonObject it) return false;
            if (!Whole(it["p"], 0, 100_000_000, out var p) || !Whole(it["s"], 1, 1000, out var s) || !Whole(it["l"], -1, 1000, out var l)) return false;
            r.Items[id] = new[] { p, s, l };
        }
        v = r;
        return true;
    }

    public sealed class Quest
    {
        public long At, Rev; public int[] Rewards = Array.Empty<int>(), Choices = Array.Empty<int>();
        public bool SameContent(Quest o) => o != null && At == o.At && Rewards.SequenceEqual(o.Rewards) && Choices.SequenceEqual(o.Choices);
    }

    static bool ItemList(JsonNode n, out int[] ids)
    {
        ids = Array.Empty<int>();
        if (n == null) return true;
        if (n is not JsonArray a || a.Count > MaxQuestItems) return false;
        var l = new List<int>();
        foreach (var e in a) { if (!Whole(e, 1, MaxItemId, out var id)) return false; if (!l.Contains((int)id)) l.Add((int)id); }
        ids = l.ToArray();
        return true;
    }

    public static bool ParseQuest(JsonNode node, long now, out Quest q)
    {
        q = null;
        if (node is not JsonObject o || !Time(o["at"], now, out var at) || !ItemList(o["rewards"], out var rw) || !ItemList(o["choices"], out var ch)) return false;
        q = new Quest { At = at, Rewards = rw, Choices = ch };
        return true;
    }

    public sealed class Spawn { public int Z; public double X, Y; public string By; }

    public static bool ParseSpawn(JsonNode node, out int z, out double x, out double y)
    {
        z = 0; x = y = 0;
        if (node is not JsonObject o || !Whole(o["z"], 1, MaxMapId, out var zz) || !Coord(o["x"], out x) || !Coord(o["y"], out y)) return false;
        z = (int)zz; return true;
    }

    public static bool SameSpot(Spawn s, int z, double x, double y) => s.Z == z && Math.Abs(s.X - x) <= SpawnMerge + 1e-9 && Math.Abs(s.Y - y) <= SpawnMerge + 1e-9;

    // details of an item the game client revealed (name, quality, item level, ...), from the member's DBCache
    public sealed class Revealed
    {
        public long Rev; public string By;
        public string Name; public int Q, Il, Rl, Cls, Sub, Slot;
        public int[][] St = Array.Empty<int[]>();   // { statType, value }
        public bool SameContent(Revealed o) => o != null && Name == o.Name && Q == o.Q && Il == o.Il && Rl == o.Rl && Cls == o.Cls && Sub == o.Sub && Slot == o.Slot
            && St.Length == o.St.Length && St.Zip(o.St).All(p => p.First[0] == p.Second[0] && p.First[1] == p.Second[1]);
    }

    static int Opt(JsonObject o, string k, int min, int max, out bool ok)
    {
        ok = true;
        if (o[k] == null) return 0;
        if (Whole(o[k], min, max, out var v)) return (int)v;
        ok = false; return 0;
    }

    public static bool ParseRevealed(JsonNode node, out Revealed r)
    {
        r = null;
        if (node is not JsonObject o || o["n"] is not JsonValue nv || !nv.TryGetValue<string>(out var raw)) return false;
        var name = CleanName(raw);
        if (name.Length == 0 || !Whole(o["q"], 0, 7, out var q)) return false;
        var x = new Revealed { Name = name, Q = (int)q };
        bool ok;
        x.Il = Opt(o, "il", 0, 1000, out ok); if (!ok) return false;
        x.Rl = Opt(o, "rl", 0, 100, out ok); if (!ok) return false;
        x.Cls = Opt(o, "cls", 0, 30, out ok); if (!ok) return false;
        x.Sub = Opt(o, "sub", 0, 30, out ok); if (!ok) return false;
        x.Slot = Opt(o, "slot", 0, 30, out ok); if (!ok) return false;
        if (o["st"] != null)
        {
            if (o["st"] is not JsonArray sa || sa.Count > MaxStats) return false;
            var l = new List<int[]>();
            foreach (var e in sa)
            {
                if (e is not JsonArray p || p.Count != 2 || !Whole(p[0], 0, 127, out var t) || !Whole(p[1], -32768, 32767, out var val)) return false;
                l.Add(new[] { (int)t, (int)val });
            }
            x.St = l.ToArray();
        }
        r = x;
        return true;
    }

    public sealed class Find
    {
        public long Rev, At; public int Item, Count, Z; public double X, Y; public string Src, Identity, Who;
        public string Key => Identity + "|" + Item + "|" + Src + "|" + Z + "|" + X.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + "|" + Y.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + "|" + At;
    }

    public static bool ParseFind(JsonNode node, long now, out Find f)
    {
        f = null;
        if (node is not JsonObject o || !Whole(o["i"], 1, MaxItemId, out var i) || !Whole(o["c"], 1, 1000, out var c) || !Time(o["at"], now, out var at)) return false;
        if (o["src"] is not JsonValue sv || !sv.TryGetValue<string>(out var src) || !ValidSrc(src)) return false;
        int z = 0; double x = 0, y = 0;
        if (o["z"] != null || o["x"] != null || o["y"] != null)
        {
            if (!Whole(o["z"], 1, MaxMapId, out var zz) || !Coord(o["x"], out x) || !Coord(o["y"], out y)) return false;
            z = (int)zz;
        }
        f = new Find { Item = (int)i, Count = (int)c, At = at, Src = src, Z = z, X = x, Y = y };
        return true;
    }
}
