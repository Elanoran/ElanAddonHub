using System;
using System.Collections.Generic;

namespace ElansAddonHub.Services.Atlas.Classic
{
    // Loot tables -> "per source, chance % of each item". cMaNGOS loot semantics (as the server rolls them):
    //  - group 0: every row is rolled on its own with |ChanceOrQuestChance| percent;
    //  - group N > 0: ONE row of the group is chosen. Rows with an explicit chance are tried in order (cumulative, capped at 100 %); when none hits, the
    //    rows with chance 0 share the remainder equally: each gets (100 - sum of explicit chances) / count percent (0 if the explicit chances reach 100);
    //  - a negative chance marks quest-only loot (it drops only while the player has the quest);
    //  - MinCountOrRef < 0 = a reference to reference_loot_template(-MinCountOrRef): the row's chance gates it ONCE, then the reference is processed
    //    MaxCount times (each pass is a full roll of that reference table), so an item with pass chance q ends up with chance * (1 - (1 - q)^MaxCount);
    //  - the same item from several rows combines as independent rolls: 1 - (1 - a)(1 - b).
    // Loot conditions (condition_id: holiday loot ...) are ignored; money is not loot here.
    static class ClassicLoot
    {
        public sealed class Row { public int Item; public double Chance; public int Group; public int MinOrRef; public int Max; }

        public sealed class Result
        {
            public double P;            // 0..1
            public int Min = int.MaxValue, Max;
            public bool QuestOnly = true;
        }

        public sealed class Store
        {
            public readonly Dictionary<int, List<Row>> ByEntry = new Dictionary<int, List<Row>>();

            public static Store From(ClassicSqlTable t)
            {
                var s = new Store();
                if (t == null) return s;
                int ie = t.Idx["entry"], ii = t.Idx["item"], ic = t.Idx["chanceorquestchance"], ig = t.Idx["groupid"], im = t.Idx["mincountorref"], ix = t.Idx["maxcount"];
                foreach (var r in t.Rows)
                {
                    int entry = (int)SqlVal.ToLong(r[ie]);
                    if (!s.ByEntry.TryGetValue(entry, out var l)) s.ByEntry[entry] = l = new List<Row>();
                    l.Add(new Row { Item = (int)SqlVal.ToLong(r[ii]), Chance = SqlVal.ToDouble(r[ic]), Group = (int)SqlVal.ToLong(r[ig]), MinOrRef = (int)SqlVal.ToLong(r[im]), Max = (int)SqlVal.ToLong(r[ix]) });
                }
                return s;
            }
        }

        public sealed class Resolver
        {
            readonly Store refs;
            readonly Dictionary<int, Dictionary<int, Result>> refCache = new Dictionary<int, Dictionary<int, Result>>();
            readonly HashSet<int> busy = new HashSet<int>();
            public int ReferenceCycles;

            public Resolver(Store referenceStore) { refs = referenceStore; }

            // item id -> result for one loot id of `store`; empty when the id does not exist
            public Dictionary<int, Result> Resolve(Store store, int entry)
            {
                return store.ByEntry.TryGetValue(entry, out var rows) ? Compute(rows) : new Dictionary<int, Result>();
            }

            Dictionary<int, Result> ResolveRef(int entry)
            {
                if (refCache.TryGetValue(entry, out var c)) return c;
                if (!busy.Add(entry)) { ReferenceCycles++; return new Dictionary<int, Result>(); }
                var res = refs.ByEntry.TryGetValue(entry, out var rows) ? Compute(rows) : new Dictionary<int, Result>();
                busy.Remove(entry);
                refCache[entry] = res;
                return res;
            }

            Dictionary<int, Result> Compute(List<Row> rows)
            {
                var prob = new double[rows.Count];
                Dictionary<int, List<int>> groups = null;
                for (int i = 0; i < rows.Count; i++)
                {
                    var r = rows[i];
                    if (r.Group == 0) { prob[i] = Math.Min(1.0, Math.Abs(r.Chance) / 100.0); continue; }
                    if (groups == null) groups = new Dictionary<int, List<int>>();
                    if (!groups.TryGetValue(r.Group, out var g)) groups[r.Group] = g = new List<int>();
                    g.Add(i);
                }
                if (groups != null)
                    foreach (var g in groups.Values)
                    {
                        double cum = 0; int equal = 0;
                        foreach (var i in g)
                        {
                            double c = Math.Abs(rows[i].Chance);
                            if (c == 0) { equal++; continue; }
                            double take = Math.Min(c, Math.Max(0, 100 - cum));
                            prob[i] = take / 100.0;
                            cum += c;
                        }
                        if (equal > 0 && cum < 100)
                        {
                            double each = (100 - cum) / 100.0 / equal;
                            foreach (var i in g) if (rows[i].Chance == 0) prob[i] = each;
                        }
                    }

                var acc = new Dictionary<int, Result>();
                for (int i = 0; i < rows.Count; i++)
                {
                    var r = rows[i];
                    double p = prob[i];
                    if (p <= 0) continue;
                    if (r.MinOrRef < 0)
                    {
                        var sub = ResolveRef(-r.MinOrRef);
                        int rolls = Math.Max(1, r.Max);
                        foreach (var kv in sub)
                        {
                            double q = Math.Min(1.0, kv.Value.P);
                            double pass = rolls == 1 ? q : 1.0 - Math.Pow(1.0 - q, rolls);
                            Add(acc, kv.Key, p * pass, kv.Value.Min, kv.Value.Max, r.Chance < 0 || kv.Value.QuestOnly);
                        }
                    }
                    else if (r.Item > 0)
                    {
                        int min = Math.Max(1, r.MinOrRef);
                        Add(acc, r.Item, p, min, Math.Max(min, r.Max), r.Chance < 0);
                    }
                }
                return acc;
            }

            static void Add(Dictionary<int, Result> acc, int item, double p, int min, int max, bool questOnly)
            {
                if (!acc.TryGetValue(item, out var a)) acc[item] = a = new Result();
                a.P = 1.0 - (1.0 - a.P) * (1.0 - p);
                if (min < a.Min) a.Min = min;
                if (max > a.Max) a.Max = max;
                a.QuestOnly &= questOnly;
            }
        }
    }
}
