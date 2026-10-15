using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Atlas.Classic;
using ElansAddonHub.Services.Db2;

namespace ElansAddonHub.Services.Upgrades
{
    // ============================================================================================================================
    // The upgrade engine: for one character, every item of the game data (and the ones a Lodge member's game revealed) that the class can use at the level,
    // scored with the stat weights of the class and talent tree, compared with what is worn in that slot, with the places it can be had from.
    // Compute() does the heavy part once (cached by character, gear, level, weights and the data that was read); Pick() applies the person's filters and
    // returns the top few per slot - it is cheap, so a toggle never waits. Both are pure: no window, no files, safe on any thread.
    // ============================================================================================================================
    public static class UpgradeEngine
    {
        public const double MinGain = 0.25;                  // an upgrade is worth at least this many "points" ...
        public const double MinPct = 1.0;                    // ... and at least this many percent of what is worn
        public const int KeepPerSlot = 30, KeepSoonPerSlot = 8, TryPerSlot = 400, SoonLevels = 3;
        public const int LodgeLookups = 14;                  // candidates per slot whose crafters are asked of the Lodge

        // ---------------------------------------------------------------------------------------------------------------- the item pool
        // A Lodge member's game may have shown an item this client has not: its stats are in the Lodge data, so it can be a candidate too.
        public static ClientItem FromRevealed(AtlasObs.Revealed r)
        {
            if (r == null) return null;
            var stats = new List<int>();
            foreach (var st in r.Stats ?? new int[0][]) if (st != null && st.Length >= 2) { stats.Add(st[0]); stats.Add(st[1]); }
            return new ClientItem
            {
                Id = r.Id, Name = r.Name ?? "", Quality = r.Quality, ItemLevel = r.ItemLevel, RequiredLevel = r.RequiredLevel, InventoryType = r.Slot, ClassId = r.Class, SubclassId = r.SubClass,
                Stats = stats.ToArray(), Source = "lodge",
            };
        }

        static ClientItem Lookup(int id)
        {
            var it = ItemData.Get(id);
            if (it != null) return it;
            return FromRevealed(AtlasQuery.LodgeOrNull()?.Revealed(id));
        }

        static IEnumerable<ClientItem> Pool()
        {
            var snap = ItemData.Snapshot;
            if (snap != null) foreach (var it in snap.Items.Values) yield return it;
            var lodge = AtlasQuery.LodgeOrNull();
            if (lodge != null)
                foreach (var r in lodge.AllRevealed)
                    if (snap == null || snap.Get(r.Id) == null) { var it = FromRevealed(r); if (it != null) yield return it; }
        }

        // ---------------------------------------------------------------------------------------------------------------- one character's evaluator
        public sealed class Evaluator
        {
            public readonly UpgradeChar Who;
            public readonly Dictionary<string, double> W;
            public readonly Dictionary<int, ClientItem> Worn = new Dictionary<int, ClientItem>();
            public readonly Dictionary<int, double> OldScore = new Dictionary<int, double>();
            public readonly bool TwoHandWorn;
            public int SpecKey => UpgradeWeights.SpecKeyOf(Who.ClassFile, Who.Spec);

            public Evaluator(UpgradeChar who)
            {
                Who = who;
                W = UpgradeWeights.Effective(who.ClassFile, who.Spec);
                foreach (var kv in who.Worn) { var it = Lookup(kv.Value); if (it != null) Worn[kv.Key] = it; }
                foreach (var slot in UpgradeRules.Slots) OldScore[slot] = Worn.TryGetValue(slot, out var w) ? UpgradeScore.Score(w, slot, W) : 0;
                TwoHandWorn = Worn.TryGetValue(16, out var mh) && UpgradeRules.IsTwoHand(mh);
            }

            // Is `it` an upgrade in `slot` for a character who is `level` (the level it needs when that is higher: a "soon" item)? null = no.
            public UpgradeCandidate Evaluate(ClientItem it, int slot)
            {
                int level = Who.Level;
                if (it.RequiredLevel > level + SoonLevels) return null;
                bool soon = it.RequiredLevel > level;
                int eff = soon ? it.RequiredLevel : level;
                if (!UpgradeRules.CanUse(Who, it, slot, eff)) return null;
                if (slot == 17 && TwoHandWorn) return null;
                if (who_WearsAlready(it.Id, slot)) return null;

                bool hasOld = Who.Worn.TryGetValue(slot, out var oldId) && oldId > 0;
                double nu, basis;
                bool replacesOff = false;
                if (slot == 16)
                {
                    double off = OldScore[17];
                    basis = OldScore[16] + off;
                    if (UpgradeRules.IsTwoHand(it)) { nu = UpgradeScore.Score(it, 16, W); replacesOff = Who.Worn.ContainsKey(17); }
                    else nu = UpgradeScore.Score(it, 16, W) + off;
                }
                else { basis = OldScore[slot]; nu = UpgradeScore.Score(it, slot, W); }
                double gain = nu - basis;
                if (gain < MinGain) return null;
                double? pct = basis > 0.0001 ? gain / basis * 100.0 : (double?)null;
                if (pct != null && pct.Value < MinPct) return null;

                Worn.TryGetValue(slot, out var old);
                var c = new UpgradeCandidate
                {
                    Item = it, Slot = slot, Score = nu, OldScore = basis, Pct = pct, Soon = soon, NeedsLevel = soon ? it.RequiredLevel : 0, ReplacesOffhand = replacesOff,
                    Revealed = it.Source == "lodge", OldItemId = hasOld ? oldId : 0, Metric = pct ?? gain,
                };
                string stats = old != null ? UpgradeScore.DiffText(it, old, slot, W) : UpgradeScore.StatsText(it, slot, W);
                c.DeltaStats = stats;
                string tail = pct == null ? (hasOld ? "worn item not known" : "empty slot") : "score " + UpgradeScore.PctText(pct);
                if (replacesOff) tail += ", replaces your off hand";
                c.Delta = (stats.Length > 0 ? stats + " " : "") + "(" + tail + ")";
                return c;
            }

            bool who_WearsAlready(int id, int slot)
            {
                if (Who.Worn.TryGetValue(slot, out var cur) && cur == id) return true;
                int other = UpgradeRules.OtherOfPair(slot);
                return other != 0 && Who.Worn.TryGetValue(other, out var oth) && oth == id;        // a ring or trinket already worn in the other slot (maybe unique)
            }

            // a cheap check before the slot loop
            public static bool Candidate(ClientItem it, int level)
                => it != null && it.Equippable && (it.ClassId == 2 || it.ClassId == 4) && it.Quality >= 1 && it.ItemLevel > 0 && it.RequiredLevel <= level + SoonLevels
                   && (it.Stats != null) && !string.IsNullOrEmpty(it.Name) && !ItemSnapshot.IsPlaceholder(it.Name);
        }

        // ---------------------------------------------------------------------------------------------------------------- the cache
        static readonly object gate = new object();
        static readonly Dictionary<string, UpgradeSet> cache = new Dictionary<string, UpgradeSet>();
        static readonly List<string> order = new List<string>();
        public static int ComputeCount;                      // how often the heavy part ran (self-test)
        public static int CacheLimit = 12;
        public static int OwnedStamp;                        // bumped whenever the Characters page reads new bags / bank data (reagent and "you have N" counts are part of the answer)

        public static void ClearCache() { lock (gate) { cache.Clear(); order.Clear(); } }

        // what the answer depends on besides the character: the item data that was read, the Classic baseline, the Lodge's pool
        public static string DataSignature()
        {
            var lodge = AtlasQuery.LodgeOrNull();
            var snap = ItemData.Snapshot;
            return "i" + (snap?.Count ?? 0) + "/" + snap?.Revealed + "/" + ItemData.Status + "|c" + (ClassicAtlas.IsReady ? ClassicAtlas.Current?.Provenance?.CommitSha + ClassicAtlas.Current?.Items.Count : "-")
                   + "|l" + (lodge == null ? "-" : lodge.Epoch + ":" + lodge.Rev + ":" + lodge.RevealedCount) + "|k" + ClassData.Status + "|m" + (ElansAddonHub.Services.Maps.MapData.Index == null ? 0 : 1) + "|s" + SpellData.Status
                   + "|a" + AtlasQuery.Characters().Count + "|n" + EnchantData.Revision + EnchantData.Status + "|o" + OwnedStamp;
        }

        public static string CacheKey(UpgradeChar who) => who.Fingerprint + "|" + UpgradeWeights.Fingerprint(who.ClassFile, who.Spec) + "|" + DataSignature();

        public static UpgradeSet Compute(UpgradeChar who)
        {
            if (who == null) return new UpgradeSet { Problem = "none" };
            var key = CacheKey(who);
            lock (gate) { if (cache.TryGetValue(key, out var hit)) return hit; }
            var set = Build(who);
            lock (gate)
            {
                if (!cache.ContainsKey(key)) { order.Add(key); while (order.Count > CacheLimit) { cache.Remove(order[0]); order.RemoveAt(0); } }
                cache[key] = set;
            }
            return set;
        }

        static UpgradeSet Build(UpgradeChar who)
        {
            var sw = Stopwatch.StartNew();
            System.Threading.Interlocked.Increment(ref ComputeCount);
            var lodge = AtlasQuery.LodgeOrNull();
            var set = new UpgradeSet { Who = who, GameData = ItemData.Ready, ClassicData = ClassicAtlas.IsReady, LodgeData = lodge != null && lodge.HasData };
            if (!UpgradeWeights.Known(who.ClassFile) || who.Level <= 0) { set.Problem = "class"; return set; }
            if (!ItemData.Ready && !set.LodgeData) { set.Problem = "items"; return set; }
            var ev = new Evaluator(who);
            set.WeightsText = UpgradeWeights.Fingerprint(who.ClassFile, who.Spec);
            set.SpecName = UpgradeWeights.SpecName(who.ClassFile, ev.SpecKey) ?? "";
            set.TwoHandWorn = ev.TwoHandWorn;

            foreach (var it in Pool())
            {
                set.Considered++;
                if (!Evaluator.Candidate(it, who.Level)) continue;
                foreach (var slot in UpgradeRules.SlotsFor(it.InventoryType))
                {
                    var c = ev.Evaluate(it, slot);
                    if (c == null) continue;
                    if (!set.BySlot.TryGetValue(slot, out var l)) set.BySlot[slot] = l = new List<UpgradeCandidate>();
                    l.Add(c);
                }
            }

            // the places to get them: the best of each slot first, until there are enough with a place
            var world = UpgradeWorld.Create(who);
            var withoutSource = new HashSet<int>();
            foreach (var slot in set.BySlot.Keys.ToList())
            {
                var all = set.BySlot[slot];
                double max = all.Max(c => c.Score);
                foreach (var c in all) if (c.Pct == null) c.Metric = max > 0 ? c.Score / max * 100.0 : 0;
                all.Sort((a, b) => { int r = b.Metric.CompareTo(a.Metric); return r != 0 ? r : a.Item.Id.CompareTo(b.Item.Id); });
                var kept = new List<UpgradeCandidate>();
                int keptNow = 0, keptSoon = 0, tried = 0;
                foreach (var c in all)
                {
                    if (keptNow >= KeepPerSlot && keptSoon >= KeepSoonPerSlot) break;
                    if (c.Soon ? keptSoon >= KeepSoonPerSlot : keptNow >= KeepPerSlot) continue;
                    if (++tried > TryPerSlot) break;
                    c.Sources = world.SourcesOf(c.Item, who.Level);
                    if (c.Sources.Count == 0) { withoutSource.Add(c.Item.Id); continue; }
                    kept.Add(c);
                    if (c.Soon) keptSoon++; else keptNow++;
                }
                set.BySlot[slot] = kept;
            }
            set.WithoutSource = withoutSource.Count;
            // the Lodge's crafters, for the few that matter (one question for all of them; an answer still on its way adds nothing yet)
            try { world.AddLodgeMakers(set.BySlot.Values.SelectMany(l => l.Take(LodgeLookups)).SelectMany(c => c.Sources).Where(s => s.Kind == "craft")); } catch (Exception) { }
            try { UpgradeExtras.BuildEnchants(set, who, ev, world); } catch (Exception ex) { Util.Log("upgrades: enchants: " + ex); set.EnchantProblem = "error"; }
            try { UpgradeExtras.BuildFood(set, who, ev, world); } catch (Exception ex) { Util.Log("upgrades: food: " + ex); }
            set.BuiltMs = sw.ElapsedMilliseconds;
            return set;
        }

        // ---------------------------------------------------------------------------------------------------------------- reach and the filters
        // How reachable a source is for the character, 0..1: vendors and quests are sure things, crafts depend on who can make it, a drop on its chance.
        public static double ReachOf(UpgradeSource s, UpgradeChar who, UpgradeFilters f)
        {
            double r;
            switch (s.Kind)
            {
                case "vendor": r = 1.0; break;
                case "quest": r = 0.9; break;
                case "craft":
                    if (s.CanCraftNow) r = 0.95;
                    else if (s.OwnMaker) r = 0.9;
                    else if (s.LodgeMaker || s.Makers.Count > 0) r = 0.8;
                    else if (s.ProfessionSkill >= 0) r = s.ProfessionSkill >= s.Skill ? 0.6 : 0.45;
                    else r = 0.35;
                    break;
                case "container": r = 0.3 + 0.3 * Math.Min(1, s.Chance / 20.0); break;
                case "fishing": r = 0.3; break;
                default:
                    r = 0.2 + 0.6 * Math.Min(1, s.Chance / 20.0);
                    if (s.Chance < f.RareChance) r = Math.Min(r, 0.2);
                    break;
            }
            if ((s.Kind == "drop" || s.Kind == "quest") && s.Level > 0 && who != null)
            {
                int diff = s.Level - who.Level;
                r *= diff <= 3 ? 1.0 : diff <= 6 ? 0.8 : 0.55;
            }
            if (s.NeedsLevel > 0 && who != null && s.NeedsLevel > who.Level) r *= 0.4;
            if (s.Dungeon) r *= 0.9;
            if (s.Raid) r *= 0.55;
            if (s.Lodge && (s.Kind == "drop" || s.Kind == "vendor" || s.Kind == "quest")) r += 0.05;
            return Math.Max(0, Math.Min(1, r));
        }

        static bool Allowed(UpgradeSource s, UpgradeFilters f)
        {
            if (s.Dungeon && !f.Dungeons) return false;
            if (s.Raid && !f.Raids) return false;
            if (!s.Lodge && !f.Classic) return false;
            return true;
        }

        // The top few per slot for these filters. The result shares nothing with the cached set (it can be kept by the window).
        public static List<UpgradeSlotResult> Pick(UpgradeSet set, UpgradeFilters f)
        {
            f = f ?? new UpgradeFilters();
            var res = new List<UpgradeSlotResult>();
            if (set == null || set.Who == null) return res;
            foreach (var slot in UpgradeRules.Slots)
            {
                var r = new UpgradeSlotResult { Slot = slot, SlotName = UpgradeRules.SlotTitle(slot), WornId = set.Who.Worn.TryGetValue(slot, out var wid) ? wid : 0 };
                if (slot == 17 && set.TwoHandWorn) r.Note = "You wield a two-hand weapon, so there is no off hand to fill.";
                var now = new List<UpgradeCandidate>(); var soon = new List<UpgradeCandidate>();
                if (set.BySlot.TryGetValue(slot, out var cands))
                    foreach (var c in cands)
                    {
                        if (c.Soon && f.LevelOffset < c.NeedsLevel - set.Who.Level) continue;
                        if (f.TradeableOnly && c.Item.Bonding != 0 && c.Item.Bonding != 2) { if (!c.Soon) r.Hidden++; continue; }
                        var srcs = c.Sources.Where(s => Allowed(s, f)).ToList();
                        if (srcs.Count == 0) { if (!c.Soon) r.Hidden++; continue; }
                        foreach (var s in srcs) s.Reach = ReachOf(s, set.Who, f);
                        var copy = c.Copy();
                        copy.Sources = srcs.OrderByDescending(s => s.Reach).ThenBy(s => s.Lodge ? 0 : 1).ThenByDescending(s => s.Chance).ToList();
                        copy.Reach = copy.Sources[0].Reach;
                        copy.Rank = c.Metric * (0.35 + 0.65 * copy.Reach);
                        (c.Soon ? soon : now).Add(copy);
                    }
                int Cmp(UpgradeCandidate a, UpgradeCandidate b)
                {
                    int x = b.Rank.CompareTo(a.Rank); if (x != 0) return x;
                    x = b.Reach.CompareTo(a.Reach); if (x != 0) return x;
                    x = (b.Pct ?? 0).CompareTo(a.Pct ?? 0); if (x != 0) return x;
                    return a.Item.Id.CompareTo(b.Item.Id);
                }
                now.Sort(Cmp); soon.Sort(Cmp);
                r.Upgrades = now.Take(Math.Max(1, f.PerSlot)).ToList();
                r.Soon = soon.Take(2).ToList();
                if (f.Enchants && set.EnchantBySlot.TryGetValue(slot, out var se))
                {
                    r.NoEnchant = se.CurrentId == 0 && se.Enchantable;
                    r.EnchantCurrent = se.CurrentId > 0 ? "Enchanted: " + se.CurrentText : se.Enchantable ? "No enchant" : "";
                    r.Enchants = se.Options.Select(x => UpgradeExtras.Filter(x, f, set.Who, true)).Where(x => x != null).OrderByDescending(x => x.Rank).ThenBy(x => x.SpellId).Take(2).ToList();
                }
                res.Add(r);
            }
            return res;
        }

        // the Food & drink lists for these filters: the top 3 of each kind (well fed, food, drink)
        public static List<ExtraEntry> PickFood(UpgradeSet set, UpgradeFilters f)
        {
            var res = new List<ExtraEntry>();
            if (set == null || set.Who == null || !(f ?? new UpgradeFilters()).Food) return res;
            foreach (var kind in UpgradeExtras.KindOrder)
                res.AddRange(set.Food.Where(e => e.Kind == kind).Select(e => UpgradeExtras.Filter(e, f, set.Who, false)).Where(e => e != null).OrderByDescending(e => e.Rank).ThenBy(e => e.ItemId).Take(UpgradeExtras.TopFood));
            return res;
        }

        // ---------------------------------------------------------------------------------------------------------------- one item for a character (Atlas item page)
        public sealed class Hint { public UpgradeChar Who; public int Slot; public double? Pct; public string Delta = ""; }

        // "Upgrade for: Paehaen (Hunter) +14%": the best slot the item would improve for a character who can use it now; null when it is none
        public static Hint HintFor(UpgradeChar who, ClientItem it)
        {
            if (who == null || it == null || who.Level <= 0 || !who.WornKnown || who.Worn.Count == 0) return null;
            if (!UpgradeWeights.Known(who.ClassFile) || !Evaluator.Candidate(it, who.Level)) return null;
            var ev = new Evaluator(who);
            UpgradeCandidate best = null;
            foreach (var slot in UpgradeRules.SlotsFor(it.InventoryType))
            {
                var c = ev.Evaluate(it, slot);
                if (c == null || c.Soon) continue;
                double m = c.Pct ?? 1000 + c.Score;
                if (best == null || m > (best.Pct ?? 1000 + best.Score)) best = c;
            }
            return best == null ? null : new Hint { Who = who, Slot = best.Slot, Pct = best.Pct, Delta = best.Delta };
        }
    }
}
