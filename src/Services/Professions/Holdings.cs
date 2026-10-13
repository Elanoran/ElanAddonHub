using System;
using System.Collections.Generic;
using System.Linq;

namespace ElansAddonHub.Services.Professions
{
    // One of the user's characters as the Professions page sees it: the skills the Hub saved and every item in bags + bank (the Hub does not save
    // WHICH recipes a character has learned, so the Outpost never claims to know that).
    public sealed class ProfChar
    {
        public string Key = "", Name = "", Class = "", Realm = "";
        public int Level;
        public readonly Dictionary<string, KeyValuePair<int, int>> Skills = new Dictionary<string, KeyValuePair<int, int>>(StringComparer.OrdinalIgnoreCase);   // profession -> (skill, max)
        public readonly Dictionary<int, int> Items = new Dictionary<int, int>();

        public bool Has(string profession) => Skills.ContainsKey(profession ?? "");
        public int SkillOf(string profession) => Skills.TryGetValue(profession ?? "", out var s) ? s.Key : 0;
        public int MaxOf(string profession) => Skills.TryGetValue(profession ?? "", out var s) ? s.Value : 0;
        public int Count(int itemId) => Items.TryGetValue(itemId, out var c) ? c : 0;
        public void Add(int itemId, int count) { if (itemId <= 0 || count <= 0) return; Items[itemId] = Count(itemId) + count; }
        public override string ToString() => Name;
    }

    public static class Holdings
    {
        // from the Characters page's merged model (bags + bank; worn gear and guild banks are not craft stock)
        public static List<ProfChar> From(IEnumerable<InvChar> chars)
        {
            var res = new List<ProfChar>();
            foreach (var c in chars ?? Enumerable.Empty<InvChar>())
            {
                if (c.Hidden) continue;
                var pc = new ProfChar { Key = c.Key ?? c.Name, Name = c.Name ?? "", Class = c.Class ?? "", Realm = c.Realm ?? "", Level = c.Level };
                if (c.Hub != null)
                {
                    var pm = HubModules.ProfessionsOf(c.Hub);
                    if (pm != null) foreach (var p in pm.List) pc.Skills[p.Name] = new KeyValuePair<int, int>(p.Skill, p.Max);
                }
                foreach (var it in c.Bags ?? new List<InvItem>()) pc.Add(it.Id, it.Count);
                foreach (var it in c.Bank ?? new List<InvItem>()) pc.Add(it.Id, it.Count);
                res.Add(pc);
            }
            return res;
        }

        // all characters' items added up
        public static Dictionary<int, int> Pool(IEnumerable<ProfChar> chars)
        {
            var d = new Dictionary<int, int>();
            foreach (var c in chars ?? Enumerable.Empty<ProfChar>()) foreach (var kv in c.Items) d[kv.Key] = (d.TryGetValue(kv.Key, out var n) ? n : 0) + kv.Value;
            return d;
        }

        // who holds an item and how many (most first)
        public static List<KeyValuePair<ProfChar, int>> Holders(IEnumerable<ProfChar> chars, int itemId)
            => (chars ?? Enumerable.Empty<ProfChar>()).Select(c => new KeyValuePair<ProfChar, int>(c, c.Count(itemId))).Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value).ToList();
    }

    public sealed class CraftNowRow
    {
        public Profession Profession;
        public ProfRecipe Recipe;
        public int Times;                              // how often the reagents in stock cover it
        public List<ProfChar> Crafters = new List<ProfChar>();      // characters with the profession (skill from the recipe's estimated start)
        public ProfChar Best;                          // the crafter with the highest skill
        public SkillColor Color;                       // for the best crafter's skill
        public ProfChar SingleHolder;                  // set when ONE character has every reagent for at least one craft
        public override string ToString() => Recipe.Name + " x" + Times;
    }

    // "What can I craft now": the recipes whose reagents the characters' bags and bank cover, and how many times.
    public static class CraftNow
    {
        // times a recipe can be made from `stock`
        public static int Times(ProfRecipe r, IDictionary<int, int> stock)
        {
            if (r.Reagents.Length == 0) return 0;
            int times = int.MaxValue;
            foreach (var g in r.Reagents)
            {
                int have = stock.TryGetValue(g.ItemId, out var n) ? n : 0;
                times = Math.Min(times, have / Math.Max(1, g.Count));
                if (times == 0) return 0;
            }
            return times == int.MaxValue ? 0 : times;
        }

        // scope = null: all characters' items together (the reagents may be on another character than the profession: the row says so);
        // scope = one character: only that character's items and only that character as crafter.
        // requireCrafter: only recipes of a profession some character has (skill >= the recipe's estimated start)
        public static List<CraftNowRow> Compute(ProfessionBook book, IList<ProfChar> chars, ProfChar scope = null, bool requireCrafter = true, bool includeCamp = true)
        {
            var res = new List<CraftNowRow>();
            if (book == null) return res;
            var stock = scope != null ? scope.Items : Holdings.Pool(chars);
            foreach (var p in book.Professions)
            {
                if (p.Gathering && p.Recipes.Count == 0) continue;
                var crafters = (scope != null ? new[] { scope } : chars.ToArray()).Where(c => c.Has(p.Name)).ToList();
                foreach (var r in p.Recipes)
                {
                    if (r.IsCamp && !includeCamp) continue;
                    int t = Times(r, stock);
                    if (t < 1) continue;
                    var cr = crafters.Where(c => c.SkillOf(p.Name) >= r.Start).OrderByDescending(c => c.SkillOf(p.Name)).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
                    if (requireCrafter && cr.Count == 0) continue;
                    var row = new CraftNowRow { Profession = p, Recipe = r, Times = t, Crafters = cr, Best = cr.FirstOrDefault() };
                    row.Color = row.Best != null ? r.ColorAt(row.Best.SkillOf(p.Name)) : SkillColor.None;
                    row.SingleHolder = (scope != null ? new[] { scope } : chars.ToArray()).FirstOrDefault(c => Times(r, c.Items) >= 1);
                    res.Add(row);
                }
            }
            // useful first: skill-ups (orange, yellow, green), then the rest; within a colour the biggest batches
            return res.OrderBy(x => x.Color == SkillColor.Orange ? 0 : x.Color == SkillColor.Yellow ? 1 : x.Color == SkillColor.Green ? 2 : 3)
                .ThenBy(x => x.Profession.Name, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Recipe.Low).ThenBy(x => x.Recipe.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
