using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ElansAddonHub.Services
{
    // Counter extras and the new counters (Elan's Toolkit 0.5.10; Lodge sub-feature "atlas9", server 2.26). Every counter may carry
    //   x = { b = { [build] = n }, q = { [questId] = kills while that quest still wanted an item }, lv = { lo, hi }, cl = { [0..6] = n }, fp = { corpse fingerprints } }
    // and its items q (the quest the item is for), sx = { [randomSuffixId] = drops }, b = { [build] = drops }. The Lodge's merge adds `dup` (kills that were a
    // corpse another member counted too) and never sends fingerprints back. New sections of the same counter shape: pickpocket, skinning, mining, herbalism,
    // salvage (by npc) and contained (by the opened item). All optional and validated part by part: a bad part is left out, never the counter.
    public static partial class AtlasObs
    {
        public const int MaxBuildsPerCounter = 8, MaxQuestsPerCounter = 20, MaxSuffixesPerItem = 40, MaxFingerprints = 200, MaxGatherSources = 3000, MaxContainedSources = 1000, MaxGatherPerShare = 100;
        public static readonly Version CounterExtrasServerVersion = new Version(2, 26, 0);   // sub-feature "atlas9"
        public static readonly string[] GatherSections = { "pickpocket", "skinning", "mining", "herbalism", "salvage", "contained" };
        public const int LowSampleKills = 30;                                              // below this a rate is "too few to tell"

        // ---- event builds: loot seen under a game build whose drops are not the game's normal ones is kept out of every rate.
        // 70532 = the last days of the WoW Forever beta (October 2026): mobs drop epics and other unusual items. A counter keeps its kills and drops per build
        // (x.b, it.b), so the Atlas takes that build's part off when it reads the Lodge's numbers; the raw numbers stay in the Toolkit, the cache and on the
        // Lodge, so changing this list later loses nothing. Counters from a Toolkit without builds (before 0.5.10) cannot be split and stay as they are.
        // HOOK: EventBuilds - add a build here when another event changes what drops.
        public static readonly HashSet<int> EventBuilds = new HashSet<int> { 70532 };

        // the counter without what it saw under the event builds (the same counter when it saw nothing there; null when nothing else is left)
        public static Counter WithoutEventBuilds(Counter c, ISet<int> builds = null)
        {
            builds = builds ?? EventBuilds;
            if (c?.X == null || builds.Count == 0) return c;
            long kb = c.X.B.Where(b => builds.Contains(b.Key)).Sum(b => b.Value);
            bool itemsToo = c.X.It.Values.Any(i => i.B.Keys.Any(builds.Contains));
            if (kb <= 0 && !itemsToo) return c;
            var r = new Counter { N = Math.Max(0, c.N - kb), Members = c.Members, EventKills = Math.Min(c.N, kb) };
            double keep = c.N > 0 ? (double)r.N / c.N : 0;
            foreach (var z in c.Zones) { long v = (long)Math.Round(z.Value * keep); if (v > 0) r.Zones[z.Key] = v; }
            var x = new CounterX { Q = new Dictionary<int, long>(c.X.Q), Cl = new Dictionary<int, long>(c.X.Cl), Lo = c.X.Lo, Hi = c.X.Hi, Fp = new List<long>(c.X.Fp), Dup = (long)Math.Round(c.X.Dup * keep) };
            foreach (var b in c.X.B) if (!builds.Contains(b.Key)) x.B[b.Key] = b.Value;
            foreach (var it in c.Items)
            {
                var ix = c.X.It.TryGetValue(it.Key, out var xx) ? xx : null;
                long db = ix == null ? 0 : ix.B.Where(b => builds.Contains(b.Key)).Sum(b => b.Value);
                long drops = Math.Max(0, it.Value[0] - db);
                if (drops <= 0) continue;                                          // only ever seen in the event: left out
                long count = it.Value[0] > 0 ? Math.Max(drops, (long)Math.Round(it.Value[1] * (double)drops / it.Value[0])) : drops;
                r.Items[it.Key] = new[] { drops, count };
                if (ix != null)
                {
                    var nx = new ItemX { Q = ix.Q, Sx = new Dictionary<int, long>(ix.Sx) };
                    foreach (var b in ix.B) if (!builds.Contains(b.Key)) nx.B[b.Key] = b.Value;
                    x.It[it.Key] = nx;
                }
            }
            r.X = x;
            return r.N > 0 || r.Items.Count > 0 ? r : null;
        }

        public class ItemX { public int Q; public Dictionary<int, long> Sx = new Dictionary<int, long>(), B = new Dictionary<int, long>(); public bool Empty => Q == 0 && Sx.Count == 0 && B.Count == 0; }

        public class CounterX
        {
            public Dictionary<int, long> B = new Dictionary<int, long>(), Q = new Dictionary<int, long>(), Cl = new Dictionary<int, long>();
            public int Lo, Hi;
            public List<long> Fp = new List<long>();
            public long Dup;                                                               // (lodge) kills that were a corpse another member counted too
            public Dictionary<int, ItemX> It = new Dictionary<int, ItemX>();
            public bool Empty => B.Count == 0 && Q.Count == 0 && Cl.Count == 0 && Lo == 0 && Fp.Count == 0 && Dup == 0 && It.Values.All(i => i.Empty);
            public int LatestBuild => B.Count == 0 ? 0 : B.Keys.Max();
        }

        static bool SKey(string k, int max, out int v)
        {
            v = 0;
            if (string.IsNullOrEmpty(k) || k.Length > 8) return false;
            bool neg = k[0] == '-';
            if (!Key(neg ? k.Substring(1) : k, max, out var a)) return false;
            v = neg ? -a : a;
            return true;
        }

        static Dictionary<int, long> CountMap(object node, int minKey, int maxKey, long maxVal, int maxCount, bool signed = false)
        {
            var d = new Dictionary<int, long>();
            var o = node as Dictionary<string, object>;
            if (o == null || o.Count > maxCount) return d;
            foreach (var kv in o)
            {
                int key;
                if (signed) { if (!SKey(kv.Key, maxKey, out key)) return new Dictionary<int, long>(); }
                else if (kv.Key == "0" && minKey == 0) key = 0;
                else if (!Key(kv.Key, maxKey, out key) || key < minKey) return new Dictionary<int, long>();
                if (!Whole(kv.Value, 1, maxVal, out var c)) return new Dictionary<int, long>();
                d[key] = c;
            }
            return d;
        }

        // the extras of one counter table (null = none); lodge: the merge's `dup` is read too
        internal static CounterX ParseExtras(Dictionary<string, object> o, Counter c, bool lodge)
        {
            var x = new CounterX();
            long cap = lodge ? MaxAggregateCount : MaxCount;
            if (o.TryGetValue("x", out var xv) && xv is Dictionary<string, object> xo)
            {
                x.B = CountMap(xo.TryGetValue("b", out var b) ? b : null, 1, 9_999_999, c.N, MaxBuildsPerCounter);
                x.Q = CountMap(xo.TryGetValue("q", out var q) ? q : null, 1, MaxId, c.N, MaxQuestsPerCounter);
                x.Cl = CountMap(xo.TryGetValue("cl", out var cl) ? cl : null, 0, 6, c.N, 7);
                var lv = xo.TryGetValue("lv", out var lvv) ? ArrayValues(lvv).ToList() : null;
                if (lv != null && lv.Count == 2 && Whole(lv[0], 1, 255, out var lo) && Whole(lv[1], lo, 255, out var hi)) { x.Lo = (int)lo; x.Hi = (int)hi; }
                if (!lodge && xo.TryGetValue("fp", out var fv))
                {
                    foreach (var e in ArrayValues(fv)) { if (!Whole(e, 1, 2_147_483_647, out var f)) { x.Fp.Clear(); break; } if (!x.Fp.Contains(f)) x.Fp.Add(f); }
                    while (x.Fp.Count > MaxFingerprints) x.Fp.RemoveAt(0);
                }
            }
            if (lodge && o.TryGetValue("dup", out var dv) && Whole(dv, 0, c.N, out var dup)) x.Dup = dup;
            if (o.TryGetValue("items", out var iv) && iv is Dictionary<string, object> io)
                foreach (var kv in io)
                {
                    if (!Key(kv.Key, MaxItemId, out var id) || !c.Items.TryGetValue(id, out var have) || !(kv.Value is Dictionary<string, object> it)) continue;
                    var ix = new ItemX();
                    if (it.TryGetValue("q", out var qv) && Whole(qv, 1, MaxId, out var qq)) ix.Q = (int)qq;
                    ix.Sx = CountMap(it.TryGetValue("sx", out var sx) ? sx : null, 0, 9_999_999, have[0], MaxSuffixesPerItem, true);
                    ix.B = CountMap(it.TryGetValue("b", out var ib) ? ib : null, 1, 9_999_999, have[0], MaxBuildsPerCounter);
                    if (!ix.Empty) x.It[id] = ix;
                }
            return x.Empty ? null : x;
        }

        static Dictionary<string, object> MapNode(Dictionary<int, long> d) => d.OrderBy(k => k.Key).ToDictionary(k => I(k.Key), k => (object)k.Value);

        // the extras on a counter's wire node (only to a lodge with atlas9); withDup = the lodge cache's own copy
        internal static void ExtrasNode(Dictionary<string, object> o, CounterX x, bool withDup = false)
        {
            if (x == null) return;
            var xo = new Dictionary<string, object>();
            if (x.B.Count > 0) xo["b"] = MapNode(x.B);
            if (x.Q.Count > 0) xo["q"] = MapNode(x.Q);
            if (x.Cl.Count > 0) xo["cl"] = MapNode(x.Cl);
            if (x.Lo > 0) xo["lv"] = new List<object> { x.Lo, x.Hi };
            if (x.Fp.Count > 0) xo["fp"] = x.Fp.Select(f => (object)f).ToList();
            if (xo.Count > 0) o["x"] = xo;
            if (withDup && x.Dup > 0) o["dup"] = x.Dup;
            if (o.TryGetValue("items", out var iv) && iv is Dictionary<string, object> io)
                foreach (var kv in x.It)
                {
                    if (!(io.TryGetValue(I(kv.Key), out var itv) && itv is Dictionary<string, object> it)) continue;
                    if (kv.Value.Q != 0) it["q"] = kv.Value.Q;
                    if (kv.Value.Sx.Count > 0) it["sx"] = MapNode(kv.Value.Sx);
                    if (kv.Value.B.Count > 0) it["b"] = MapNode(kv.Value.B);
                }
        }

        static string ExtrasSig(CounterX x)
        {
            if (x == null) return "";
            var sb = new StringBuilder("|x");
            void M(Dictionary<int, long> d) { foreach (var kv in d.OrderBy(k => k.Key)) sb.Append(I(kv.Key)).Append(':').Append(I(kv.Value)).Append(','); sb.Append(';'); }
            M(x.B); M(x.Q); M(x.Cl); sb.Append(x.Lo).Append('-').Append(x.Hi).Append(';');
            foreach (var f in x.Fp.OrderBy(f => f)) sb.Append(I(f)).Append(',');
            foreach (var kv in x.It.OrderBy(k => k.Key)) { sb.Append(I(kv.Key)).Append('q').Append(kv.Value.Q); M(kv.Value.Sx); M(kv.Value.B); }
            return sb.ToString();
        }

        // ---------------------------------------------------------------- what the pages say about a rate

        // the kills a rate may count: each corpse once (minus the ones another member counted too)
        public static long UniqueKills(Counter c) => c == null ? 0 : Math.Max(0, c.N - (c.X?.Dup ?? 0));

        // drops of one item scaled to the unique kills (a shared corpse's drop was seen by every member who looted it)
        public static long UniqueDrops(Counter c, long drops) => c == null || c.N <= 0 ? drops : (long)Math.Round(drops * (double)UniqueKills(c) / c.N);

        // "4 of 37 kills (10.8 %)", "2 of 12 kills (too few to tell a rate)"; what = "kill" / "open" / "cast" / "kill while on the quest"
        public static string RateText(long drops, long kills, string what, string whats)
        {
            if (kills <= 0) return "";
            var of = drops.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " of " + kills.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " " + (kills == 1 ? what : whats);
            if (kills < LowSampleKills) return of + " (too few to tell a rate)";
            return of + " (" + Math.Min(100, drops * 100.0 / kills).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " %)";
        }

        public static bool LowSample(long kills) => kills < LowSampleKills;

        // "of the Bear 60 % · #1047 40 %": the suffix ids of an item (names are not in what the Outpost reads), most first
        public static string SuffixText(ItemX x, Func<int, string> name = null)
        {
            if (x == null || x.Sx.Count == 0) return "";
            long all = x.Sx.Values.Sum();
            return "Random suffix: " + string.Join(" · ", x.Sx.OrderByDescending(k => k.Value).ThenBy(k => k.Key).Take(6).Select(k => (name?.Invoke(k.Key) ?? ("#" + I(k.Key))) + " " + Math.Round(k.Value * 100.0 / all).ToString(System.Globalization.CultureInfo.InvariantCulture) + " %"))
                + (x.Sx.Count > 6 ? " · ..." : "");
        }

        static readonly string[] ClassNames = { "Normal", "Elite", "Rare Elite", "Boss", "Rare", "Trivial", "Minor" };
        public static string ClassText(CounterX x) => x == null || x.Cl.Count == 0 ? "" : string.Join(", ", x.Cl.OrderByDescending(k => k.Value).ThenBy(k => k.Key).Select(k => ClassNames[Math.Max(0, Math.Min(6, k.Key))]));
    }
}
