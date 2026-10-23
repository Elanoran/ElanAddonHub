using System.Globalization;
using System.Text.Json.Nodes;

namespace Lodge;

// Counter extras (2.26, sub-feature "atlas9"): what Elan's Toolkit 0.5.10 adds to every counter (kills, objects, fishing and the new pickpocket / skinning /
// mining / herbalism / salvage / contained sections) so drop rates can be qualified:
//   x = { b = { "<build>": n }, q = { "<questId>": kills while that quest still wanted an item }, lv = [ lo, hi ], cl = { "<class 0..6>": n }, fp = [ corpse fingerprints ] }
//   items."<id>" += { q = the quest the item is for, sx = { "<randomSuffixId>": drops } (negative ids allowed), b = { "<build>": drops } }
// Every part is optional and validated on its own: a malformed part is left out, the counter itself stays (an older Toolkit sends none of it).
// Merge per member: per key the larger value (cumulative and idempotent like the rest of the counter), lv the wider range, fp the union (the newest
// MaxFingerprints). The aggregate sums the members and counts the fingerprints several members sent: `dup` = how many kills were the same corpse counted
// again by another member (a group looting one corpse), so a page can count each corpse once. A fingerprint is a number the Toolkit hashes from the
// corpse GUID's server and spawn part only; the server never sees a GUID or anything about a character.
public static partial class Atlas
{
    public const int MaxBuildsPerCounter = 8, MaxQuestsPerCounter = 20, MaxSuffixesPerItem = 40, MaxFingerprints = 200, MaxBuildId = 9_999_999, MaxClass = 6;
    public const long MaxFingerprint = 2_147_483_647;

    public sealed class ItemX
    {
        public int Q; public Dictionary<int, long> Sx = new(), B = new();
        public bool Empty => Q == 0 && Sx.Count == 0 && B.Count == 0;
        public ItemX Clone() => new() { Q = Q, Sx = new(Sx), B = new(B) };
    }

    public sealed class CtrX
    {
        public Dictionary<int, long> B = new(), Q = new(), Cl = new();
        public int Lo, Hi;
        public List<long> Fp = new();
        public Dictionary<int, ItemX> It = new();
        public bool Empty => B.Count == 0 && Q.Count == 0 && Cl.Count == 0 && Lo == 0 && Fp.Count == 0 && It.Values.All(i => i.Empty);
        public CtrX Clone() => new() { B = new(B), Q = new(Q), Cl = new(Cl), Lo = Lo, Hi = Hi, Fp = new(Fp), It = It.ToDictionary(kv => kv.Key, kv => kv.Value.Clone()) };
    }

    // a signed table key ("-6", "1047"): |k| <= max, no leading zero
    static bool SKey(string k, int max, out int v)
    {
        v = 0;
        if (string.IsNullOrEmpty(k) || k.Length > 8) return false;
        bool neg = k[0] == '-';
        var digits = neg ? k[1..] : k;
        if (!Key(digits, max, out var a)) return false;
        v = neg ? -a : a;
        return true;
    }

    static Dictionary<int, long> CountMap(JsonNode n, int minKey, int maxKey, long maxVal, int maxCount, bool signed = false)
    {
        var d = new Dictionary<int, long>();
        if (n is not JsonObject o || o.Count > maxCount) return d;
        foreach (var (k, v) in o)
        {
            int key;
            if (signed) { if (!SKey(k, maxKey, out key)) return new(); }
            else if (k == "0" && minKey == 0) key = 0;
            else if (!Key(k, maxKey, out key) || key < minKey) return new();
            if (!Whole(v, 1, maxVal, out var c)) return new();
            d[key] = c;
        }
        return d;
    }

    // ---- event builds: game builds whose loot is not the game's normal loot (70532 = the last days of the WoW Forever beta: epics and other unusual
    // drops). What the members' Outposts get leaves those builds' kills and drops out (x.b / items.b say how many; an item without items.b loses the
    // event kills' share of its drops); the stored member data, the
    // aggregate and the Cairn feed keep the raw numbers (the Cairn splits by build itself). LODGE_EVENT_BUILDS changes the list ("none" = none).
    public static readonly HashSet<int> DefaultEventBuilds = new() { 70532 };

    // the counter without its event builds' part (the same counter when it has none); dup scaled with the kills
    public static (Ctr C, long Dup) WithoutEventBuilds(Ctr c, long dup, ISet<int> builds, ISet<int> eventOnly = null)
    {
        if (c?.X == null || builds == null || builds.Count == 0) return (c, dup);
        long kb = c.X.B.Where(b => builds.Contains(b.Key)).Sum(b => b.Value);
        if (kb <= 0 && !c.X.It.Values.Any(i => i.B.Keys.Any(builds.Contains)) && !(eventOnly != null && c.I.Keys.Any(eventOnly.Contains))) return (c, dup);
        var r = new Ctr { N = Math.Max(0, c.N - kb), X = c.X.Clone() };
        double keep = c.N > 0 ? (double)r.N / c.N : 0;
        foreach (var z in c.Z) { long v = (long)Math.Round(z.Value * keep); if (v > 0) r.Z[z.Key] = v; }
        foreach (var b in builds) r.X.B.Remove(b);
        foreach (var it in c.I)
        {
            var ix = r.X.It.TryGetValue(it.Key, out var xx) ? xx : null;
            // the drops counted per build come off exactly; the rest (a Toolkit strips items' builds first when its copy gets too big, and several
            // members' sums mix both) loses the event kills' share; an event-only item (EventOnlyItems) goes whole
            long tagged = ix == null ? 0 : ix.B.Values.Sum(), untagged = Math.Max(0, it.Value[0] - tagged);
            long db = eventOnly != null && eventOnly.Contains(it.Key) ? it.Value[0]
                : (ix == null ? 0 : ix.B.Where(b => builds.Contains(b.Key)).Sum(b => b.Value)) + (c.N > 0 ? (long)Math.Round(untagged * (double)kb / c.N) : 0);
            long drops = Math.Max(0, it.Value[0] - db);
            if (drops <= 0) { r.X.It.Remove(it.Key); continue; }               // only ever seen in the event: left out
            long count = it.Value[0] > 0 ? Math.Max(drops, (long)Math.Round(it.Value[1] * (double)drops / it.Value[0])) : drops;
            r.I[it.Key] = new[] { drops, count };
            if (ix != null) foreach (var b in builds) ix.B.Remove(b);
        }
        return (r, Math.Min(r.N, (long)Math.Round(dup * keep)));
    }

    // event-only items: the server revealed them (not in the game's own files: the end of the beta's Molten Core sets, weapons and "Endless" elixirs
    // came as server data) and every drop counted per build is an event build's. Their drops leave every counter whole, also the ones a member sent
    // without item builds. A normal item (in the game files, or counted in a normal build) only loses the event kills' share.
    public static HashSet<int> EventOnlyItems(IEnumerable<Ctr> counters, ISet<int> builds, Func<int, bool> revealed)
    {
        var ev = new HashSet<int>(); var normal = new HashSet<int>();
        if (builds == null || builds.Count == 0 || revealed == null) return new HashSet<int>();
        foreach (var c in counters)
        {
            if (c?.X == null) continue;
            foreach (var (id, ix) in c.X.It)
                foreach (var b in ix.B) { if (builds.Contains(b.Key)) ev.Add(id); else normal.Add(id); }
        }
        ev.ExceptWith(normal);
        ev.RemoveWhere(id => !revealed(id));
        return ev;
    }

    // the extras of a counter entry (null = none); n = the counter's own count
    public static CtrX ParseX(JsonObject o, long n)
    {
        var x = new CtrX();
        if (o["x"] is JsonObject xo)
        {
            x.B = CountMap(xo["b"], 1, MaxBuildId, n, MaxBuildsPerCounter);
            x.Q = CountMap(xo["q"], 1, MaxId, n, MaxQuestsPerCounter);
            x.Cl = CountMap(xo["cl"], 0, MaxClass, n, MaxClass + 1);
            if (xo["lv"] is JsonArray lv && lv.Count == 2 && Whole(lv[0], 1, 255, out var lo) && Whole(lv[1], lo, 255, out var hi)) { x.Lo = (int)lo; x.Hi = (int)hi; }
            if (xo["fp"] is JsonArray fa && fa.Count <= MaxFingerprints)
                foreach (var e in fa) { if (!Whole(e, 1, MaxFingerprint, out var f)) { x.Fp.Clear(); break; } if (!x.Fp.Contains(f)) x.Fp.Add(f); }
        }
        if (o["items"] is JsonObject io)
            foreach (var (k, v) in io)
            {
                if (!Key(k, MaxItemId, out var id) || v is not JsonObject it || !Whole(it["n"], 1, long.MaxValue, out var dn)) continue;
                var ix = new ItemX();
                if (it["q"] != null && Whole(it["q"], 1, MaxId, out var q)) ix.Q = (int)q;
                ix.Sx = CountMap(it["sx"], 0, 9_999_999, dn, MaxSuffixesPerItem, true);
                ix.B = CountMap(it["b"], 1, MaxBuildId, dn, MaxBuildsPerCounter);
                if (!ix.Empty) x.It[id] = ix;
            }
        return x.Empty ? null : x;
    }

    static Dictionary<int, long> MaxMap(Dictionary<int, long> a, Dictionary<int, long> b)
    {
        var r = new Dictionary<int, long>(a);
        foreach (var kv in b) r[kv.Key] = r.TryGetValue(kv.Key, out var v) ? Math.Max(v, kv.Value) : kv.Value;
        return r;
    }

    public static CtrX MergeX(CtrX old, CtrX nw)
    {
        if (old == null) return nw?.Clone();
        if (nw == null) return old.Clone();
        var r = new CtrX { B = MaxMap(old.B, nw.B), Q = MaxMap(old.Q, nw.Q), Cl = MaxMap(old.Cl, nw.Cl) };
        r.Lo = old.Lo == 0 ? nw.Lo : nw.Lo == 0 ? old.Lo : Math.Min(old.Lo, nw.Lo);
        r.Hi = Math.Max(old.Hi, nw.Hi);
        r.Fp = new List<long>(old.Fp);
        foreach (var f in nw.Fp) if (!r.Fp.Contains(f)) r.Fp.Add(f);
        while (r.Fp.Count > MaxFingerprints) r.Fp.RemoveAt(0);
        foreach (var kv in old.It) r.It[kv.Key] = kv.Value.Clone();
        foreach (var kv in nw.It)
        {
            if (!r.It.TryGetValue(kv.Key, out var have)) { r.It[kv.Key] = kv.Value.Clone(); continue; }
            have.Q = kv.Value.Q != 0 ? kv.Value.Q : have.Q;
            have.Sx = MaxMap(have.Sx, kv.Value.Sx); have.B = MaxMap(have.B, kv.Value.B);
        }
        // keep the caps after the union
        if (r.B.Count > MaxBuildsPerCounter || r.Q.Count > MaxQuestsPerCounter || r.It.Values.Any(i => i.Sx.Count > MaxSuffixesPerItem || i.B.Count > MaxBuildsPerCounter))
        {
            foreach (var k in r.B.Keys.OrderBy(k => k).Take(Math.Max(0, r.B.Count - MaxBuildsPerCounter)).ToList()) r.B.Remove(k);
            foreach (var k in r.Q.OrderBy(kv => kv.Value).ThenBy(kv => kv.Key).Take(Math.Max(0, r.Q.Count - MaxQuestsPerCounter)).Select(kv => kv.Key).ToList()) r.Q.Remove(k);
            foreach (var i in r.It.Values)
            {
                foreach (var k in i.Sx.OrderBy(kv => kv.Value).ThenBy(kv => kv.Key).Take(Math.Max(0, i.Sx.Count - MaxSuffixesPerItem)).Select(kv => kv.Key).ToList()) i.Sx.Remove(k);
                foreach (var k in i.B.Keys.OrderBy(k => k).Take(Math.Max(0, i.B.Count - MaxBuildsPerCounter)).ToList()) i.B.Remove(k);
            }
        }
        return r.Empty ? null : r;
    }

    static bool SameMap(Dictionary<int, long> a, Dictionary<int, long> b) => a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    public static bool SameX(CtrX a, CtrX b)
    {
        if (a == null || b == null) return a == null && b == null;
        return SameMap(a.B, b.B) && SameMap(a.Q, b.Q) && SameMap(a.Cl, b.Cl) && a.Lo == b.Lo && a.Hi == b.Hi && a.Fp.Count == b.Fp.Count && a.Fp.All(b.Fp.Contains)
            && a.It.Count == b.It.Count && a.It.All(kv => b.It.TryGetValue(kv.Key, out var o) && o.Q == kv.Value.Q && SameMap(o.Sx, kv.Value.Sx) && SameMap(o.B, kv.Value.B));
    }

    // the members' extras summed; fpSeen counts each fingerprint over the members (for `dup`)
    public static void AddX(ref CtrX into, CtrX add, Dictionary<long, int> fpSeen)
    {
        if (add == null) return;
        into ??= new CtrX();
        void Sum(Dictionary<int, long> d, Dictionary<int, long> s) { foreach (var kv in s) d[kv.Key] = d.TryGetValue(kv.Key, out var v) ? v + kv.Value : kv.Value; }
        Sum(into.B, add.B); Sum(into.Q, add.Q); Sum(into.Cl, add.Cl);
        if (add.Lo > 0) { into.Lo = into.Lo == 0 ? add.Lo : Math.Min(into.Lo, add.Lo); into.Hi = Math.Max(into.Hi, add.Hi); }
        foreach (var f in add.Fp) fpSeen[f] = fpSeen.TryGetValue(f, out var c) ? c + 1 : 1;
        foreach (var kv in add.It)
        {
            if (!into.It.TryGetValue(kv.Key, out var have)) into.It[kv.Key] = have = new ItemX();
            if (kv.Value.Q != 0) have.Q = kv.Value.Q;
            Sum(have.Sx, kv.Value.Sx); Sum(have.B, kv.Value.B);
        }
    }

    static JsonObject MapJson(Dictionary<int, long> d)
    {
        var o = new JsonObject();
        foreach (var kv in d.OrderBy(k => k.Key)) o[kv.Key.ToString(CultureInfo.InvariantCulture)] = kv.Value;
        return o;
    }

    // writes the extras into a counter's JSON (store: with the fingerprints; aggregate: without them, with dup)
    public static void WriteX(JsonObject o, CtrX x, bool fingerprints, long dup)
    {
        if (x != null)
        {
            var xo = new JsonObject();
            if (x.B.Count > 0) xo["b"] = MapJson(x.B);
            if (x.Q.Count > 0) xo["q"] = MapJson(x.Q);
            if (x.Cl.Count > 0) xo["cl"] = MapJson(x.Cl);
            if (x.Lo > 0) xo["lv"] = new JsonArray(x.Lo, x.Hi);
            if (fingerprints && x.Fp.Count > 0) xo["fp"] = new JsonArray(x.Fp.Select(f => (JsonNode)f).ToArray());
            if (xo.Count > 0) o["x"] = xo;
            if (o["items"] is JsonObject io)
                foreach (var kv in x.It)
                {
                    if (io[kv.Key.ToString(CultureInfo.InvariantCulture)] is not JsonObject it) continue;
                    if (kv.Value.Q != 0) it["q"] = kv.Value.Q;
                    if (kv.Value.Sx.Count > 0) it["sx"] = MapJson(kv.Value.Sx);
                    if (kv.Value.B.Count > 0) it["b"] = MapJson(kv.Value.B);
                }
        }
        if (dup > 0) o["dup"] = dup;
    }
}
