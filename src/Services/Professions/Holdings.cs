using System;
using System.Collections.Generic;
using System.Linq;

namespace ElansAddonHub.Services.Professions
{
    // One of the user's characters as the Professions page sees it: the skills the Hub saved, every item in bags + bank and, from Hub 1.14.0, the recipes
    // the Hub read from the profession windows (Known). A profession without a read is "unknown", never "not learned": the Outpost only claims what was read.
    public sealed class ProfChar
    {
        public string Key = "", Name = "", Class = "", Realm = "";
        public int Level;
        public bool FromHub;               // the Hub saved this character: its key is the one Elan's Hub (ElansHub.CharKey) and the game use, so a guide can be handed to the game
        public readonly Dictionary<string, KeyValuePair<int, int>> Skills = new Dictionary<string, KeyValuePair<int, int>>(StringComparer.OrdinalIgnoreCase);   // profession -> (skill, max)
        public readonly Dictionary<int, int> Items = new Dictionary<int, int>();
        public readonly Dictionary<string, int> Lines = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);       // profession -> skill line id (Hub 1.14.0)
        public readonly Dictionary<int, HubKnownScan> Known = new Dictionary<int, HubKnownScan>();                          // skill line id -> the recipes read from its window

        // the read of this profession's window (null = never read)
        public HubKnownScan ScanOf(string profession, int skillId)
        {
            if (skillId > 0 && Known.TryGetValue(skillId, out var s)) return s;
            if (profession != null && Lines.TryGetValue(profession, out var line) && Known.TryGetValue(line, out s)) return s;
            if (profession != null) foreach (var k in Known.Values) if (string.Equals(k.Name, profession, StringComparison.OrdinalIgnoreCase)) return k;
            return null;
        }
        // true = learned, false = not in the read, null = the window was never read (unknown)
        public bool? Knows(string profession, int skillId, int spellId)
        {
            var s = ScanOf(profession, skillId);
            return s == null ? (bool?)null : s.Ids.Contains(spellId);
        }
        public bool KnowsSpell(int spellId) { foreach (var k in Known.Values) if (k.Ids.Contains(spellId)) return true; return false; }

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
                var pc = new ProfChar { Key = c.Key ?? c.Name, Name = c.Name ?? "", Class = c.Class ?? "", Realm = c.Realm ?? "", Level = c.Level, FromHub = c.Hub != null };
                if (c.Hub != null)
                {
                    var pm = HubModules.ProfessionsOf(c.Hub);
                    if (pm != null)
                    {
                        foreach (var p in pm.List) { pc.Skills[p.Name] = new KeyValuePair<int, int>(p.Skill, p.Max); if (p.Line > 0) pc.Lines[p.Name] = p.Line; }
                        foreach (var kv in pm.Known) pc.Known[kv.Key] = kv.Value;
                    }
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
        public bool Unconfirmed;                       // the best crafter's window was never read: the recipe may not be learned
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
                    // a character whose profession window was read must know the recipe; one that was never read stays on the skill estimate
                    var cr = crafters.Where(c => c.SkillOf(p.Name) >= r.Start && c.Knows(p.Name, p.Id, r.SpellId) != false).OrderByDescending(c => c.SkillOf(p.Name)).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
                    if (requireCrafter && cr.Count == 0) continue;
                    var row = new CraftNowRow { Profession = p, Recipe = r, Times = t, Crafters = cr, Best = cr.FirstOrDefault() };
                    row.Unconfirmed = row.Best != null && row.Best.Knows(p.Name, p.Id, r.SpellId) == null;
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
