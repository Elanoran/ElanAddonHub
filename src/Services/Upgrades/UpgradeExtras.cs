using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Professions;

namespace ElansAddonHub.Services.Upgrades
{
    // One suggestion that is not an item to wear: an enchant (a formula or a kit, scope, spike ...) or a food / drink.
    public sealed class ExtraEntry
    {
        public string Kind = "", Name = "", Delta = "", Buff = "", Profession = "";   // Kind: Enchanting | Armor kit | Scope | Shield spike | Weapon chain | Enhancement | Well fed | Food | Drink
        public int IconId, ItemId, SpellId, EnchantId, Quality = 1, Slot;
        public double Score, Gain, Metric, Rank, Reach;
        public double? Pct;                          // against the current enchant; null = nothing there now (or a food)
        public bool Applicable;                      // me, one of my characters or a Lodge member can apply / make it, or I already own the item
        public bool ClassicOnly;                     // every place to get it is from the Classic baseline
        public bool Partial;                         // the enchant also has an effect that is not scored (a proc)
        public int Owned;                            // how many of the item I hold across my characters
        public List<string> Learn = new List<string>();      // how the formula is learned: trainer, taught with the profession, a recipe item and where it is from
        public List<string> Reagents = new List<string>();   // "Greater Magic Essence x4 (you have 6)"
        public List<UpgradeSource> Sources = new List<UpgradeSource>();
        public string Link => ItemId > 0 ? AtlasIds.Item(ItemId) : SpellId > 0 ? AtlasIds.Spell(SpellId) : "";
        public UpgradeSource Best => Sources.OrderByDescending(s => s.Reach).FirstOrDefault();
        public ExtraEntry Copy() => (ExtraEntry)MemberwiseClone();
    }

    public sealed class SlotEnchants
    {
        public int Slot, CurrentId;
        public string CurrentText = "";              // "+3 Strength (Enchant Bracer - Strength)" or "" when there is none
        public bool Enchantable;                     // something in the game data can be put on the worn item
        public ClientItem Item;
        public List<ExtraEntry> Options = new List<ExtraEntry>();
    }

    // ============================================================================================================================
    // Enchants and food for one character, from the game's own data: SpellItemEnchantment / SpellEquippedItems (EnchantData), the spells whose effect enchants an
    // item (effect 53: the enchant id is its first misc value), item Use effects, the profession book for how a formula is learned, the Atlas sources and the
    // Lodge's crafters for where and who. Only values the data gives are scored; an enchant that is a proc or a spell is left out (a note says so).
    // ============================================================================================================================
    public static class UpgradeExtras
    {
        public const int EffectEnchantPermanent = 53;
        public const double MinEnchantGain = 0.5, MinEnchantPct = 5;
        public const int TopEnchants = 6, TopFood = 3;

        // ---------------------------------------------------------------------------------------------------------------- the scored parts of an enchant
        public sealed class Part { public string Label; public double Value, Weighted; }

        static readonly string[] Schools = { "armor", "Holy resistance", "Fire resistance", "Nature resistance", "Frost resistance", "Shadow resistance", "Arcane resistance" };

        public static List<Part> PartsOf(EnchantInfo e, int slot, ClientItem item, IDictionary<string, double> w, out bool unscoredEffect)
        {
            var res = new List<Part>(); unscoredEffect = false;
            if (e == null) return res;
            double Wt(string k) => w.TryGetValue(k, out var v) ? v : 0;
            for (int k = 0; k < 3; k++)
            {
                int type = e.Type[k], pts = e.Points[k], arg = e.Arg[k];
                switch (type)
                {
                    case EnchantInfo.KindStat:
                        {
                            var key = UpgradeWeights.KeyOfStat(arg);
                            if (key == null) { unscoredEffect = true; break; }
                            double wt = Wt(key); if (arg == 45) wt = Math.Max(wt, Wt("heal"));
                            res.Add(new Part { Label = ItemNames.Stat(arg), Value = pts, Weighted = pts * wt });
                            break;
                        }
                    case EnchantInfo.KindDamage:
                        if (item == null || item.DelayMs <= 0) { unscoredEffect = true; break; }
                        res.Add(new Part { Label = "weapon damage", Value = pts, Weighted = pts / (item.DelayMs / 1000.0) * Wt(slot == 18 ? "rdps" : "dps") });
                        break;
                    case EnchantInfo.KindResist:
                        res.Add(new Part { Label = arg >= 0 && arg < Schools.Length ? Schools[arg] : "resistance", Value = pts, Weighted = pts * Wt(arg == 0 ? "armor" : "res") });
                        break;
                    case 0: case 8: break;
                    default: unscoredEffect = true; break;     // a proc, a spell on equip or on use: not a number
                }
            }
            return res;
        }

        public static double ScoreOf(List<Part> parts) => parts.Sum(p => p.Weighted);
        public static string PartText(Part p) => (p.Value >= 0 ? "+" : "-") + Math.Abs(p.Value).ToString("0", CultureInfo.InvariantCulture) + " " + p.Label;
        public static string PartsText(List<Part> parts) => string.Join(", ", parts.OrderByDescending(p => p.Weighted).Select(PartText));

        // "+8 Agility, -3 Strength": what changes from the current enchant to the new one, by value to the weights
        public static string DiffText(List<Part> nu, List<Part> old, int max = 4)
        {
            var d = new Dictionary<string, KeyValuePair<double, double>>();      // label -> (delta, weight per point)
            foreach (var p in nu) d[p.Label] = new KeyValuePair<double, double>(p.Value, p.Value != 0 ? p.Weighted / p.Value : 0);
            foreach (var p in old)
            {
                d.TryGetValue(p.Label, out var cur);
                d[p.Label] = new KeyValuePair<double, double>(cur.Key - p.Value, p.Value != 0 ? p.Weighted / p.Value : cur.Value);
            }
            return string.Join(", ", d.Where(k => Math.Abs(k.Value.Key) > 1e-9).OrderByDescending(k => Math.Abs(k.Value.Key * k.Value.Value)).Take(max)
                .Select(k => (k.Value.Key > 0 ? "+" : "-") + Math.Abs(k.Value.Key).ToString("0", CultureInfo.InvariantCulture) + " " + k.Key));
        }

        // ---------------------------------------------------------------------------------------------------------------- the options in the game data
        public sealed class Option
        {
            public int SpellId, EnchantId, KitItemId;
            public EnchantInfo Info;
            public string Kind = "Enchanting";
            public Recipe Recipe;                         // formulas
            public ClientItem Kit;                        // kits, scopes, spikes: the item whose Use spell enchants
        }

        static readonly object gate = new object();
        static EnchantSet optEs; static SpellSnapshot optSp; static ItemDetails optDet; static int optCount; static List<Option> options;
        static object bookKey; static ProfessionBook book;

        static string KitKind(ClientItem it)
        {
            var n = (it.Name ?? "").ToLowerInvariant();
            if (n.Contains("armor kit")) return "Armor kit";
            if (n.Contains("scope")) return "Scope";
            if (n.Contains("spike")) return "Shield spike";
            if (n.Contains("weapon chain") || n.Contains("chain")) return "Weapon chain";
            return "Enhancement";
        }

        // every spell that puts a permanent enchant on an item (formulas) and every item whose Use spell does (kits, scopes, spikes, chains)
        public static List<Option> Options(EnchantSet es)
        {
            var sp = SpellData.Spells; var det = ItemDetails.Current;

            lock (gate)
            {
                if (options != null && ReferenceEquals(optEs, es) && ReferenceEquals(optSp, sp) && ReferenceEquals(optDet, det) && optCount == ItemData.Count) return options;
                var list = new List<Option>();
                if (es != null && sp != null)
                {
                    foreach (var s in sp.Spells.Values)
                    {
                        if (s.Effects == null) continue;
                        foreach (var e in s.Effects)
                            if (e.Effect == EffectEnchantPermanent && e.Misc > 0 && es.Get(e.Misc) != null && es.Equipped.ContainsKey(s.Id))
                            {
                                var rec = sp.RecipeOf(s.Id);
                                if (rec == null) continue;                          // a spell no profession teaches (an item's Use spell): found through the item below
                                list.Add(new Option { SpellId = s.Id, EnchantId = e.Misc, Info = es.Get(e.Misc), Recipe = rec, Kind = "Enchanting" });
                                break;
                            }
                    }
                    if (det?.Effects != null)
                        foreach (var kv in det.Effects)
                        {
                            var item = ItemData.Get(kv.Key);
                            if (item == null) continue;
                            for (int i = 0; i + 1 < kv.Value.Length; i += 2)
                            {
                                if (kv.Value[i] != 0) continue;                     // Use
                                var s = sp.Get(kv.Value[i + 1]);
                                var e = s?.Effects?.FirstOrDefault(x => x.Effect == EffectEnchantPermanent && x.Misc > 0 && es.Get(x.Misc) != null);
                                if (e == null || !es.Equipped.ContainsKey(s.Id)) continue;
                                list.Add(new Option { SpellId = s.Id, EnchantId = e.Misc, Info = es.Get(e.Misc), Kit = item, KitItemId = item.Id, Kind = KitKind(item) });
                                break;
                            }
                        }
                }
                options = list; optEs = es; optSp = sp; optDet = det; optCount = ItemData.Count;
                return list;
            }
        }

        static ProfessionBook Book()
        {
            var sp = SpellData.Spells;
            if (sp == null) return null;
            lock (gate)
            {
                if (book != null && ReferenceEquals(bookKey, sp)) return book;
                try { book = ProfessionBook.Build(sp, ItemData.Get, ItemDetails.Current?.Effects); bookKey = sp; }
                catch (Exception) { book = null; }
                return book;
            }
        }

        public static void ForgetForTest() { lock (gate) { options = null; optEs = null; book = null; bookKey = null; } }

        // ---------------------------------------------------------------------------------------------------------------- reagents and how a formula is learned
        static string ReagentLine(int item, int count, Dictionary<int, InvItemTotals> owned)
        {
            int have = 0;
            if (owned != null && owned.TryGetValue(item, out var t)) have = t.Chars.Sum(c => c.Total);
            return AtlasQuery.ItemName(item) + " x" + count + " (you have " + have + ")";
        }

        static void AddReagents(ExtraEntry e, int[] reagents, Dictionary<int, InvItemTotals> owned)
        {
            if (reagents == null) return;
            for (int i = 0; i + 1 < reagents.Length; i += 2) if (reagents[i] > 0) e.Reagents.Add(ReagentLine(reagents[i], Math.Max(1, reagents[i + 1]), owned));
        }

        static void AddLearn(ExtraEntry e, int spell, string profession, UpgradeWorld world)
        {
            var b = Book();
            if (b == null || !b.BySpell.TryGetValue(spell, out var pr)) return;
            switch (pr.Learn)
            {
                case LearnKind.Starter: e.Learn.Add("Taught with " + profession); break;
                case LearnKind.Trainer: e.Learn.Add("Learn it at the " + profession + " trainer"); break;
                case LearnKind.Special: e.Learn.Add("Learned in a special way (no trainer or recipe item in the game data)"); break;
                default:
                    {
                        var live = new LiveSources(() => world.Lodge, ItemData.Get);
                        foreach (var id in pr.TeachingItems.Take(2))
                        {
                            e.Learn.Add("Taught by " + AtlasQuery.ItemName(id));
                            foreach (var l in live.SourcesOf(id, 2)) e.Learn.Add("  " + l.Text + " (" + (l.Label == SourceLabels.Classic ? "Classic baseline" : "Lodge") + ")");
                        }
                        break;
                    }
            }
        }

        // ---------------------------------------------------------------------------------------------------------------- enchants
        public static void BuildEnchants(UpgradeSet set, UpgradeChar who, UpgradeEngine.Evaluator ev, UpgradeWorld world)
        {
            if (!EnchantData.Ensure()) { set.EnchantProblem = EnchantData.Status == EnchantData.State.Disabled ? "off" : "unavailable"; return; }
            var es = EnchantData.Set;
            var opts = Options(es);
            if (opts.Count == 0) { set.EnchantProblem = "none"; return; }
            var owned = AtlasQuery.Context.Owned?.Invoke();
            var w = ev.W;
            var craftsForLodge = new List<UpgradeSource>();
            foreach (var slot in UpgradeRules.Slots)
            {
                if (!ev.Worn.TryGetValue(slot, out var item)) continue;
                who.Enchants.TryGetValue(slot, out var curId);
                var cur = curId > 0 ? es.Get(curId) : null;
                var curParts = cur == null ? new List<Part>() : PartsOf(cur, slot, item, w, out _);
                double curScore = ScoreOf(curParts);
                var se = new SlotEnchants { Slot = slot, CurrentId = curId, Item = item };
                if (cur != null) se.CurrentText = curParts.Count > 0 ? PartsText(curParts) + (string.IsNullOrEmpty(cur.Text) ? "" : " (" + cur.Text + ")") : cur.Text;
                var found = new List<KeyValuePair<Option, double>>();
                var seen = new HashSet<int>();
                foreach (var o in opts)
                {
                    if (!es.Applies(o.SpellId, item)) continue;
                    if (o.Info.ItemLevelMax > 0 && item.ItemLevel > o.Info.ItemLevelMax) continue;
                    if (o.Info.ItemLevelMin > 0 && item.ItemLevel < o.Info.ItemLevelMin) continue;
                    if (o.Info.MinLevel > who.Level) continue;
                    if (o.Kit != null && o.Kit.RequiredLevel > who.Level) continue;
                    se.Enchantable = true;
                    if (o.EnchantId == curId) continue;
                    var parts = PartsOf(o.Info, slot, item, w, out _);
                    if (parts.Count == 0) continue;
                    double score = ScoreOf(parts), gain = score - curScore;
                    if (gain < MinEnchantGain || (curScore > 0 && gain / curScore * 100 < MinEnchantPct)) continue;
                    found.Add(new KeyValuePair<Option, double>(o, gain));
                }
                foreach (var kv in found.OrderByDescending(x => x.Value).ThenBy(x => x.Key.SpellId).Take(40))
                {
                    if (se.Options.Count >= TopEnchants) break;
                    var o = kv.Key;
                    if (!seen.Add(o.EnchantId * 10 + (o.Kit != null ? 1 : 0))) continue;
                    var e = MakeEnchant(o, slot, item, w, curParts, curScore, kv.Value, owned, world, who);
                    if (e == null) continue;
                    se.Options.Add(e);
                    craftsForLodge.AddRange(e.Sources.Where(s => s.Kind == "craft"));
                }
                set.EnchantBySlot[slot] = se;
            }
            // the best enchant for each upgrade item (it has none yet)
            foreach (var kv in set.BySlot)
                foreach (var c in kv.Value.Take(10))
                {
                    Option best = null; double bs = 0; List<Part> bp = null;
                    foreach (var o in opts)
                    {
                        if (!es.Applies(o.SpellId, c.Item) || o.Info.MinLevel > who.Level) continue;
                        if (o.Info.ItemLevelMax > 0 && c.Item.ItemLevel > o.Info.ItemLevelMax) continue;
                        if (o.Info.ItemLevelMin > 0 && c.Item.ItemLevel < o.Info.ItemLevelMin) continue;
                        if (o.Kit != null && o.Kit.RequiredLevel > who.Level) continue;
                        var parts = PartsOf(o.Info, kv.Key, c.Item, w, out _);
                        double s = ScoreOf(parts);
                        if (s > bs) { bs = s; best = o; bp = parts; }
                    }
                    if (best != null && bs >= MinEnchantGain) c.EnchantNote = "Best enchant: " + (best.Kit != null ? best.Kit.Name : AtlasQuery.SpellName(best.SpellId)) + " (" + PartsText(bp) + ")";
                }
            try { world.AddLodgeMakers(craftsForLodge); } catch (Exception) { }
            foreach (var se in set.EnchantBySlot.Values) foreach (var e in se.Options) FinishApplicable(e);
        }

        static void FinishApplicable(ExtraEntry e)
        {
            if (e.Kind == "Enchanting") e.Applicable = e.Sources.Any(s => s.Kind == "craft" && s.Makers.Count > 0);
            else e.Applicable = e.Owned > 0 || e.Sources.Any(s => s.Kind == "craft" && s.Makers.Count > 0);
            e.ClassicOnly = e.Sources.Count > 0 && e.Sources.All(s => !s.Lodge);
        }

        static ExtraEntry MakeEnchant(Option o, int slot, ClientItem item, Dictionary<string, double> w, List<Part> curParts, double curScore, double gain, Dictionary<int, InvItemTotals> owned, UpgradeWorld world, UpgradeChar who)
        {
            var parts = PartsOf(o.Info, slot, item, w, out bool partial);
            var e = new ExtraEntry
            {
                Kind = o.Kind, SpellId = o.SpellId, EnchantId = o.EnchantId, Slot = slot, Score = ScoreOf(parts), Gain = gain, Partial = partial,
                Pct = curScore > 0 ? gain / curScore * 100.0 : (double?)null, Buff = PartsText(parts), Metric = curScore > 0 ? gain / curScore * 100.0 : gain,
            };
            string diff = DiffText(parts, curParts);
            e.Delta = (diff.Length > 0 ? diff + " " : "") + "(" + (curScore > 0 ? "score " + UpgradeScore.PctText(e.Pct) : curParts.Count == 0 ? "no enchant" : "current one not scored") + (partial ? ", also has an effect that is not scored" : "") + ")";
            if (o.Kit == null)
            {
                var spell = SpellData.Spell(o.SpellId);
                e.Name = spell?.Name ?? o.Info.Text; e.IconId = spell?.IconId ?? 0; e.Quality = 1;
                var skill = SpellData.Skill(o.Recipe.SkillId);
                e.Profession = skill?.Name ?? "Enchanting";
                var src = new UpgradeSource { Kind = "craft", Lodge = true, What = e.Name, Link = AtlasIds.Spell(o.SpellId), Spell = o.SpellId, Profession = e.Profession + (o.Recipe.MinRank > 0 ? " " + o.Recipe.MinRank : ""), Skill = o.Recipe.MinRank };
                world.MakersOf(src, o.Recipe, e.Profession);
                e.Sources.Add(src);
                AddLearn(e, o.SpellId, e.Profession, world);
                AddReagents(e, o.Recipe.Reagents, owned);
            }
            else
            {
                e.Name = o.Kit.Name; e.ItemId = o.Kit.Id; e.IconId = o.Kit.IconId; e.Quality = Math.Max(0, o.Kit.Quality);
                e.Sources = world.SourcesOf(o.Kit, who.Level);
                if (owned != null && owned.TryGetValue(o.Kit.Id, out var t)) e.Owned = t.Chars.Sum(c => c.Total);
                if (e.Sources.Count == 0 && e.Owned == 0) return null;
                var rec = SpellData.RecipesProducing(o.Kit.Id).FirstOrDefault();
                if (rec != null) { AddReagents(e, rec.Reagents, owned); var skill = SpellData.Skill(rec.SkillId); e.Profession = skill?.Name ?? ""; }
            }
            return e;
        }

        // ---------------------------------------------------------------------------------------------------------------- food and drink
        static readonly Regex Restores = new Regex(@"(?i)restores\s+(?:up to\s+)?([\d,]+)\s+(health|mana)(?:\s+over\s+(\d+)\s+sec)?", RegexOptions.Compiled);

        sealed class Buff { public Dictionary<int, double> Stats = new Dictionary<int, double>(); public int DurationMs; }

        // the stat auras of a food's Use spell and of the spells it triggers (the "Well Fed" buff): strength .. spirit, attack power, spell damage, healing
        static Buff BuffOf(int spellId)
        {
            var b = new Buff();
            var seen = new HashSet<int>();
            void Walk(int id, int depth)
            {
                if (id <= 0 || depth > 2 || !seen.Add(id)) return;
                var s = SpellData.Spells?.Get(id);
                if (s?.Effects == null) return;
                bool had = false;
                foreach (var e in s.Effects)
                {
                    double v = Math.Abs(e.Points) < 0.5f ? 0 : Math.Round(e.Points, MidpointRounding.AwayFromZero);
                    int type = 0;
                    switch (e.Aura)
                    {
                        case 29:
                            switch (e.Misc) { case 0: type = 4; break; case 1: type = 3; break; case 2: type = 7; break; case 3: type = 5; break; case 4: type = 6; break; }
                            if (e.Misc == -1) { foreach (var t in new[] { 4, 3, 7, 5, 6 }) if (v > 0) b.Stats[t] = b.Stats.TryGetValue(t, out var x) ? x + v : v; had |= v > 0; continue; }
                            break;
                        case 99: type = 38; break;
                        case 124: type = 38; break;
                        case 13: if ((e.Misc & 126) != 0) type = 45; break;
                        case 135: type = 41; break;
                        case 85: if (e.Misc == 0) type = 43; break;
                    }
                    if (type != 0 && v > 0) { b.Stats[type] = b.Stats.TryGetValue(type, out var y) ? y + v : v; had = true; }
                    if (e.Trigger > 0) Walk(e.Trigger, depth + 1);
                }
                if (had && b.DurationMs == 0) b.DurationMs = s.DurationMs;
            }
            Walk(spellId, 0);
            return b;
        }

        public static void BuildFood(UpgradeSet set, UpgradeChar who, UpgradeEngine.Evaluator ev, UpgradeWorld world)
        {
            var snap = ItemData.Snapshot;
            if (snap == null || SpellData.Spells == null || ItemDetails.Current?.Effects == null) return;
            var owned = AtlasQuery.Context.Owned?.Invoke();
            var w = ev.W;
            bool mana = who.ClassFile != "WARRIOR" && who.ClassFile != "ROGUE";
            var well = new List<ExtraEntry>(); var heal = new List<ExtraEntry>(); var drink = new List<ExtraEntry>();
            foreach (var it in snap.Items.Values)
            {
                if (it.ClassId != 0 || it.SubclassId != 5 || it.RequiredLevel > who.Level || string.IsNullOrEmpty(it.Name) || ItemSnapshot.IsPlaceholder(it.Name)) continue;
                var eff = ItemDetails.EffectsOf(it.Id).Where(p => p.Key == 0 || p.Key == 4 || p.Key == 5 || p.Key == 6).ToList();
                if (eff.Count == 0) continue;
                var buff = new Buff(); double amountH = 0, amountM = 0; string text = "";
                foreach (var p in eff)
                {
                    var b = BuffOf(p.Value);
                    foreach (var kv in b.Stats) buff.Stats[kv.Key] = buff.Stats.TryGetValue(kv.Key, out var x) ? Math.Max(x, kv.Value) : kv.Value;
                    if (b.DurationMs > buff.DurationMs) buff.DurationMs = b.DurationMs;
                    var view = SpellData.Spell(p.Value);
                    var d = (view?.Description ?? "") + " " + (view?.AuraDescription ?? "");
                    foreach (Match m in Restores.Matches(d))
                    {
                        double a = double.Parse(m.Groups[1].Value.Replace(",", ""), CultureInfo.InvariantCulture);
                        if (m.Groups[2].Value.ToLowerInvariant() == "health") amountH = Math.Max(amountH, a); else amountM = Math.Max(amountM, a);
                        if (text.Length == 0) text = m.Value;
                    }
                }
                double score = 0; var words = new List<string>();
                foreach (var kv in buff.Stats.OrderByDescending(x => x.Value))
                {
                    var key = UpgradeWeights.KeyOfStat((int)kv.Key);
                    double wt = key == null ? 0 : (w.TryGetValue(key, out var ww) ? ww : 0);
                    if (kv.Key == 45) wt = Math.Max(wt, w.TryGetValue("heal", out var hh) ? hh : 0);
                    score += kv.Value * wt;
                    words.Add("+" + kv.Value.ToString("0", CultureInfo.InvariantCulture) + " " + ItemNames.Stat((int)kv.Key));
                }
                string Dur(int ms) => ms >= 60000 ? (ms / 60000) + " min" : (ms / 1000) + " sec";
                if (buff.Stats.Count > 0 && score >= MinEnchantGain)
                    well.Add(new ExtraEntry { Kind = "Well fed", Name = it.Name, ItemId = it.Id, IconId = it.IconId, Quality = Math.Max(0, it.Quality), Score = score, Metric = score, Buff = string.Join(", ", words) + (buff.DurationMs > 0 ? " for " + Dur(buff.DurationMs) : "") });
                if (amountH > 0 && buff.Stats.Count == 0) heal.Add(new ExtraEntry { Kind = "Food", Name = it.Name, ItemId = it.Id, IconId = it.IconId, Quality = Math.Max(0, it.Quality), Score = amountH, Metric = amountH, Buff = text });
                if (mana && amountM > 0 && buff.Stats.Count == 0) drink.Add(new ExtraEntry { Kind = "Drink", Name = it.Name, ItemId = it.Id, IconId = it.IconId, Quality = Math.Max(0, it.Quality), Score = amountM, Metric = amountM, Buff = text });
            }
            foreach (var l in new[] { well, heal, drink })
            {
                var kept = new List<ExtraEntry>();
                foreach (var e in l.OrderByDescending(x => x.Score).ThenBy(x => x.ItemId).Take(60))
                {
                    if (kept.Count >= TopFood * 2) break;
                    var item = ItemData.Get(e.ItemId);
                    e.Sources = world.SourcesOf(item, who.Level);
                    if (e.Sources.Count == 0) continue;
                    if (owned != null && owned.TryGetValue(e.ItemId, out var t)) e.Owned = t.Chars.Sum(c => c.Total);
                    var rec = SpellData.RecipesProducing(e.ItemId).FirstOrDefault();
                    if (rec != null) { AddReagents(e, rec.Reagents, owned); e.Profession = SpellData.Skill(rec.SkillId)?.Name ?? "Cooking"; AddLearn(e, rec.SpellId, e.Profession, world); }
                    e.Applicable = e.Owned > 0 || e.Sources.Any(s => s.Kind == "vendor" || (s.Kind == "craft" && s.Makers.Count > 0));
                    e.ClassicOnly = e.Sources.All(s => !s.Lodge);
                    kept.Add(e);
                }
                set.Food.AddRange(kept);
            }
            try { world.AddLodgeMakers(set.Food.SelectMany(e => e.Sources).Where(s => s.Kind == "craft")); } catch (Exception) { }
            foreach (var e in set.Food) if (!e.Applicable) e.Applicable = e.Sources.Any(s => s.Kind == "craft" && s.Makers.Count > 0);
            set.ShowsFood = true;
        }

        // ---------------------------------------------------------------------------------------------------------------- the filters
        static bool Allowed(UpgradeSource s, UpgradeFilters f) => !(s.Dungeon && !f.Dungeons) && !(s.Raid && !f.Raids) && (s.Lodge || f.Classic);

        // a copy with the sources the filters allow, ranked; null when nothing is left
        public static ExtraEntry Filter(ExtraEntry e, UpgradeFilters f, UpgradeChar who, bool enchant)
        {
            if (enchant && f.OnlyApplicable && !e.Applicable) return null;
            var srcs = e.Sources.Where(s => Allowed(s, f)).ToList();
            if (srcs.Count == 0) return null;
            foreach (var s in srcs) s.Reach = UpgradeEngine.ReachOf(s, who, f);
            var c = e.Copy();
            c.Sources = srcs.OrderByDescending(s => s.Reach).ThenBy(s => s.Lodge ? 0 : 1).ToList();
            c.Reach = c.Sources[0].Reach;
            c.Rank = e.Metric * (0.35 + 0.65 * c.Reach);
            c.ClassicOnly = c.Sources.All(s => !s.Lodge);
            return c;
        }
    }
}
