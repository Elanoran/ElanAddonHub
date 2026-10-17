using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using ElansAddonHub.Services.Db2;

namespace ElansAddonHub.Services.Professions
{
    // ============================================================================================================================
    // Atlas layer H: professions. Everything here is built from the player's OWN game files (SpellData / ItemData); nothing is shipped.
    // This file: skill-up colours, the recipe model and the ProfessionBook that is built from a spell snapshot (pure: the self-test feeds it
    // synthetic tables).
    // ============================================================================================================================

    // the colour of a recipe for a given skill: orange = every craft gives a point, yellow = usually, green = sometimes, grey = never
    public enum SkillColor { None, Orange, Yellow, Green, Grey }

    public static class RecipeBands
    {
        // The game stores two trivial ranks per skill-line ability: "low" (TrivialSkillLineRankLow, `SkillAbility.Green` in the model: the name is
        // older than this finding) and "high" (`SkillAbility.Gray`). Checked against the real table of this client (Linen Bandage: 30 / 60, shown
        // in the game as orange 1, yellow 30, green 45, grey 60): low = where YELLOW starts, high = where GREY starts, green = their middle.
        public static int YellowAt(int low, int high) => low;
        public static int GreenAt(int low, int high) => (low + high) / 2;
        public static int GreyAt(int low, int high) => high;

        // a recipe with no band (0 / 0) never gives a skill point
        public static SkillColor ColorAt(int low, int high, int skill)
        {
            if (high <= 0) return SkillColor.None;
            if (skill < low) return SkillColor.Orange;
            if (skill < GreenAt(low, high)) return SkillColor.Yellow;
            if (skill < high) return SkillColor.Green;
            return SkillColor.Grey;
        }

        // The skill from which the recipe is learnable and orange. The client's tables hold no "required skill" for a recipe (MinSkillLineRank is 1
        // everywhere), so this is an ESTIMATE from the band: the classic bands are spaced evenly, so orange starts as far below yellow as grey is above
        // it (Coyote Steak 90 / 130 -> 50, which is what the game shows). Never below 1.
        public static int EstimatedStart(int low, int high) => Math.Max(1, low - Math.Max(0, high - low));

        // How many crafts a skill point costs on average. Orange is certain; the others are the rules of thumb the game's own guides use
        // (yellow about 4 in 5, green about 2 in 5). Grey gives nothing (infinity), "none" the same.
        public const double OrangeCrafts = 1.0, YellowCrafts = 1.25, GreenCrafts = 2.5;
        public static double CraftsPerPoint(SkillColor c)
        {
            switch (c)
            {
                case SkillColor.Orange: return OrangeCrafts;
                case SkillColor.Yellow: return YellowCrafts;
                case SkillColor.Green: return GreenCrafts;
                default: return double.PositiveInfinity;
            }
        }

        public static string Word(SkillColor c)
        {
            switch (c)
            {
                case SkillColor.Orange: return "Orange";
                case SkillColor.Yellow: return "Yellow";
                case SkillColor.Green: return "Green";
                case SkillColor.Grey: return "Grey";
                default: return "No skill-up";
            }
        }

        // identity colours of the skill-up bands (the game's own colours; data, not theme - see DESIGN 4)
        public static readonly string[] SkillPalette = { "#8A9199", "#FF8040", "#FFD100", "#40C040", "#8A9199" };
        public static string Hex(SkillColor c) => SkillPalette[(int)c];
    }

    public sealed class Reagent
    {
        public int ItemId, Count;
    }

    // how a recipe is learned
    public enum LearnKind { Starter, Trainer, RecipeItem, Special }

    public sealed class ProfRecipe
    {
        public int SpellId, SkillId, ItemId, OutCount = 1;
        public int Low, High;                              // trivial ranks (see RecipeBands)
        public string Name = "", Rank = "";
        public int IconId, Quality = 1;
        public Reagent[] Reagents = new Reagent[0];
        public int Acquire;                                // SkillLineAbility.AcquireMethod: 1 = taught with the profession
        public List<int> TeachingItems = new List<int>();  // items that teach this recipe ("Pattern: ...", "Blueprint: ...")
        public int Tier;                                   // camp recipes: the "[Tier n]" of the spell, 0 for everything else
        public bool IsCamp => Tier > 0;

        public int Yellow => RecipeBands.YellowAt(Low, High);
        public int Green => RecipeBands.GreenAt(Low, High);
        public int Grey => RecipeBands.GreyAt(Low, High);
        public int Start => Learn == LearnKind.Starter ? 1 : RecipeBands.EstimatedStart(Low, High);        // a recipe taught with the profession is known from skill 1
        public SkillColor ColorAt(int skill) => RecipeBands.ColorAt(Low, High, skill);

        public LearnKind Learn => TeachingItems.Count > 0 ? LearnKind.RecipeItem : Acquire == 1 ? LearnKind.Starter : Acquire == 0 ? LearnKind.Trainer : LearnKind.Special;
        public override string ToString() => Name + " [" + Low + "/" + High + "]";
    }

    public sealed class RankInfo
    {
        public string Name;       // Apprentice, Journeyman, Expert, Artisan, Master ...
        public int Cap;           // the skill this rank allows (75, 150, 225, 300 ...)
        public int SpellId;
    }

    public enum ProfKind { Primary, Secondary, Gathering }

    public sealed class Profession
    {
        public SkillInfo Skill;
        public ProfKind Kind;
        public readonly List<ProfRecipe> Recipes = new List<ProfRecipe>();
        public readonly List<RankInfo> Ranks = new List<RankInfo>();
        public int Id => Skill.Id;
        public string Name => Skill.Name;
        public int IconId => Skill.IconId;
        public bool Gathering => Kind == ProfKind.Gathering;
        public IEnumerable<ProfRecipe> Craftable => Recipes.Where(r => !r.IsCamp);
        public int MaxCap => Ranks.Count == 0 ? 300 : Ranks.Max(r => r.Cap);
        // the rank whose cap is the first above `skill` (the one a character at that skill works under), null when no rank data
        public RankInfo RankFor(int skill) => Ranks.OrderBy(r => r.Cap).FirstOrDefault(r => r.Cap > skill);
        public override string ToString() => Name;
    }

    // Everything built from the game tables. Immutable after Build.
    public sealed class ProfessionBook
    {
        public static readonly string[] GatheringNames = { "Herbalism", "Mining", "Skinning", "Fishing" };
        static readonly string[] RankWords = { "Apprentice", "Journeyman", "Expert", "Artisan", "Master", "Grand Master" };
        static readonly Regex RankCap = new Regex(@"skill of (\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex TierRank = new Regex(@"^Tier (\d+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public readonly List<Profession> Professions = new List<Profession>();
        public readonly Dictionary<int, ProfRecipe> BySpell = new Dictionary<int, ProfRecipe>();
        public readonly Dictionary<int, List<ProfRecipe>> ByProduct = new Dictionary<int, List<ProfRecipe>>();
        public CampBook Camp = new CampBook();
        public SpellSnapshot Snapshot;
        Func<int, ClientItem> item;
        readonly Dictionary<int, int[]> effects = new Dictionary<int, int[]>();
        public int[] EffectsOf(int itemId) => effects.TryGetValue(itemId, out var a) ? a : null;

        public Profession ByName(string name) => Professions.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        public Profession ById(int id) => Professions.FirstOrDefault(p => p.Id == id);
        public ClientItem Item(int id) => item?.Invoke(id);

        // Build from a spell snapshot. `item` answers item lookups (ItemData.Get in the app), `itemEffects` lists every item's effects as
        // [trigger, spell, trigger, spell ...] (ItemDetails.Effects), which is how recipe items are found; `describe` gives a spell's text with
        // its tokens filled in (SpellData.Spell). Any of them may be null.
        public static ProfessionBook Build(SpellSnapshot sn, Func<int, ClientItem> item, IEnumerable<KeyValuePair<int, int[]>> itemEffects, Func<int, SpellView> describe = null)
        {
            var book = new ProfessionBook { Snapshot = sn, item = item };
            if (sn == null) return book;
            describe = describe ?? SpellData.Spell;

            // spell -> items that teach it: trigger 6 = "learn spell" straight; any other effect whose spell is a "Learn Spell" effect (36) teaches its trigger spell
            var teach = new Dictionary<int, List<int>>();
            foreach (var kv in itemEffects ?? Enumerable.Empty<KeyValuePair<int, int[]>>()) book.effects[kv.Key] = kv.Value;
            foreach (var kv in book.effects)
            {
                var a = kv.Value;
                for (int i = 0; i + 1 < a.Length; i += 2)
                {
                    int target = 0, spell = a[i + 1];
                    if (a[i] == 6) target = spell;
                    else if (sn.Spells.TryGetValue(spell, out var sp))
                        foreach (var e in sp.Effects) if (e.Effect == 36 && e.Trigger > 0) target = e.Trigger;
                    if (target <= 0 || sn.RecipeOf(target) == null) continue;
                    if (!teach.TryGetValue(target, out var l)) teach[target] = l = new List<int>();
                    if (!l.Contains(kv.Key)) l.Add(kv.Key);
                }
            }

            foreach (var sk in sn.Skills.Values.OrderBy(s => s.Id))
            {
                if (!sk.IsProfession || sk.ParentId != 0 || string.IsNullOrEmpty(sk.Name) || Atlas.AtlasHidden.IsHiddenSkill(sk, sn, itemEffects)) continue;     // 2.93: test / placeholder lines (AtlasHidden)
                bool gathering = GatheringNames.Contains(sk.Name, StringComparer.OrdinalIgnoreCase);
                var recipes = new List<ProfRecipe>();
                foreach (var a in sn.AbilitiesOf(sk.Id))
                {
                    var r = sn.RecipeOf(a.Spell);
                    if (r == null || r.SkillId != sk.Id) continue;
                    var sp = sn.Get(r.SpellId);
                    var it = r.ItemId > 0 ? item?.Invoke(r.ItemId) : null;
                    if (Atlas.AtlasHidden.IsHiddenName(sp?.Name) || Atlas.AtlasHidden.IsHiddenName(it?.Name)) continue;     // "Smelt Copper (Test)"
                    var pr = new ProfRecipe
                    {
                        SpellId = r.SpellId, SkillId = sk.Id, ItemId = r.ItemId, Low = r.Green, High = r.Gray, Acquire = a.Acquire, Rank = sp?.Rank ?? "",
                        Name = it != null && !string.IsNullOrEmpty(it.Name) ? it.Name : sp?.Name ?? "Spell " + r.SpellId,
                        IconId = it != null && it.IconId > 0 ? it.IconId : sp?.IconId ?? 0, Quality = it?.Quality ?? 1,
                    };
                    var e0 = sp?.Effects.FirstOrDefault(e => e.Effect == EffectInfo.CreateItem);
                    pr.OutCount = e0 == null ? 1 : Math.Max(1, (int)Math.Round(e0.Points));
                    var rs = new List<Reagent>();
                    for (int i = 0; i + 1 < r.Reagents.Length; i += 2) if (r.Reagents[i] > 0 && r.Reagents[i + 1] > 0) rs.Add(new Reagent { ItemId = r.Reagents[i], Count = r.Reagents[i + 1] });
                    pr.Reagents = rs.ToArray();
                    if (teach.TryGetValue(r.SpellId, out var tl)) pr.TeachingItems = tl.OrderBy(x => x).ToList();
                    var m = TierRank.Match(pr.Rank ?? "");
                    if (m.Success) pr.Tier = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    recipes.Add(pr);
                }
                if (recipes.Count < 1 && !gathering) continue;
                if (sk.Category != SkillInfo.CatProfession && !gathering && recipes.Count < 5) continue;       // class / racial lines with a few reagent spells are not professions
                var p = new Profession { Skill = sk, Kind = gathering ? ProfKind.Gathering : sk.Category == SkillInfo.CatProfession ? ProfKind.Primary : ProfKind.Secondary };
                p.Recipes.AddRange(recipes.OrderBy(r => r.High <= 0 ? 1 : 0).ThenBy(r => r.Low).ThenBy(r => r.High).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.SpellId));
                p.Ranks.AddRange(RanksOf(sn, sk.Id));
                book.Professions.Add(p);
                foreach (var r in p.Recipes)
                {
                    book.BySpell[r.SpellId] = r;
                    if (r.ItemId > 0) { if (!book.ByProduct.TryGetValue(r.ItemId, out var l)) book.ByProduct[r.ItemId] = l = new List<ProfRecipe>(); l.Add(r); }
                }
            }
            // primary professions first (alphabetical), then the secondary ones, the gatherers keep their kind but sort among them by name
            var sorted = book.Professions.OrderBy(p => p.Kind == ProfKind.Primary ? 0 : p.Kind == ProfKind.Gathering ? 1 : 2).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
            book.Professions.Clear(); book.Professions.AddRange(sorted);
            book.Camp = CampBook.Build(book, describe);
            return book;
        }

        // the ranks of a skill (Apprentice 75, Journeyman 150 ...), read from the rank spells' own descriptions: "... up to a maximum potential skill of 75."
        static IEnumerable<RankInfo> RanksOf(SpellSnapshot sn, int skillId)
        {
            var seen = new HashSet<int>();
            var res = new List<RankInfo>();
            foreach (var a in sn.AbilitiesOf(skillId))
            {
                var sp = sn.Get(a.Spell);
                if (sp == null || !RankWords.Contains(sp.Rank ?? "", StringComparer.OrdinalIgnoreCase)) continue;
                var m = RankCap.Match(sp.Desc ?? "");
                if (!m.Success) continue;
                int cap = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                if (!seen.Add(cap)) continue;
                res.Add(new RankInfo { Name = sp.Rank, Cap = cap, SpellId = sp.Id });
            }
            return res.OrderBy(r => r.Cap);
        }

        // the recipes that make `itemId`
        public List<ProfRecipe> Producing(int itemId) => ByProduct.TryGetValue(itemId, out var l) ? l : new List<ProfRecipe>();
    }
}
