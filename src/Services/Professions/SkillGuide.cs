using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ElansAddonHub.Services.Professions
{
    // ============================================================================================================================
    // The leveling guide of a crafting profession, generated from the game's own recipe table (no text from any website).
    //
    // HOW IT PICKS (simple on purpose; the page shows the same one-line reason per step):
    //   1. For every skill point from the start to the target, look at the recipes of the profession that the character could be making there:
    //      not grey, learnable (skill >= the recipe's estimated start), and gettable: taught by the profession (starter), by the trainer (no recipe
    //      item in the game data), or by a recipe item that a known vendor sells. Recipes whose only way in is a drop / quest are left out, unless
    //      a skill range would otherwise have nothing at all (then they are used and the step is marked "hard to get").
    //   2. Orange and yellow recipes beat green ones. Among them the winner is the cheapest per skill point:
    //          cost per point = crafts per point (orange 1.0, yellow 1.25, green 2.5) x the reagents' price.
    //      A reagent is priced by the vendor that sells it (Classic baseline or the Lodge's observed price); a reagent no vendor is known to sell
    //      is ESTIMATED from the client's own item value (marked ~), because it has to be farmed or bought from players.
    //   3. To avoid hopping between recipes every point, the recipe in use is kept while it is still orange or yellow and costs at most 25 % more
    //      than the best choice.
    //   4. Points with the same recipe become one step, split where a rank cap is reached (75 / 150 / 225 / 300 as the game's rank spells say):
    //      there the guide adds a "train the next rank" milestone, because the skill cannot rise past the cap.
    //   5. Crafts per step = the sum over its points of the crafts-per-point of that point's colour, rounded up. Materials = crafts x reagents.
    //   6. The shopping list is all steps' materials added up, minus what the characters already hold, each with the cheapest known place to get it.
    // Camp objects are left out unless "include camp objects" is on (they are made for their buff, not for skill: the first ones are cheap and orange at the start, though).
    // ============================================================================================================================
    public sealed class GuideStep
    {
        public int From, To;                            // skill before / after the step
        public ProfRecipe Recipe;                       // null for a trainer milestone
        public RankInfo Train;                          // milestone: the rank to train at skill `From`
        public string TrainText;                        // milestone text
        public int Crafts;
        public SkillColor StartColor, EndColor;
        public List<Reagent> Materials = new List<Reagent>();
        public long Cost;                               // copper for the whole step (reagents at full price)
        public bool CostApprox;                         // some price in it is an estimate or unknown
        public long RunningCost;
        public string Why = "";
        public string Learn = "";                       // how the recipe is learned
        public List<SourceLine> LearnSources = new List<SourceLine>();
        public int LearnItemId;                         // the recipe item to buy / find (0 = trainer / starter)
        public bool Hard;                               // only reachable through a drop / quest recipe item
        public bool Known;                              // the character's window read says the recipe is learned: no learn step
        public bool Unlearned;                          // the read says it is not learned (yet): "Learn first"
        public bool IsMilestone => Recipe == null;
    }

    public sealed class ShoppingLine
    {
        public int ItemId, Need, Have, ToGet;
        public ItemPrice Price;
        public long Total => Price.Copper * ToGet;
        public string Where = "";
        public string Label = "";
        public string Link = "";
        public bool IsRecipe;                           // a recipe item to buy (not a reagent)
    }

    public sealed class SkillGuide
    {
        public Profession Profession;
        public int Start, Target;
        public string Character;                        // whose skill / holdings the guide started from (null = from scratch)
        public readonly List<GuideStep> Steps = new List<GuideStep>();
        public readonly List<ShoppingLine> Shopping = new List<ShoppingLine>();
        public readonly List<string> Notes = new List<string>();
        public long TotalCost;                          // copper still to buy (shopping list)
        public bool TotalApprox;
        public int UnknownPrices;                       // shopping lines with no price at all
        public int Reached;                             // the skill the guide actually gets to (less than Target when no recipe is left)

        public IEnumerable<GuideStep> Crafting => Steps.Where(s => !s.IsMilestone);

        // "1-40" like the guides write it
        public static string Range(int from, int to) => from.ToString(CultureInfo.InvariantCulture) + "–" + to.ToString(CultureInfo.InvariantCulture);

        // plain text, the same on screen and in the clipboard / file
        public string ToText()
        {
            var sb = new StringBuilder();
            string Money(long c, bool ap) => (ap ? "~" : "") + ProfMoney.Format(c);
            sb.AppendLine(Profession.Name + " " + Range(Start, Target) + (Character != null ? " (" + Character + ")" : ""));
            sb.AppendLine("Generated by Elan's Outpost from the game's own recipe table. Orange = every craft is a skill-up, yellow ~ 4 in 5, green ~ 2 in 5.");
            sb.AppendLine();
            sb.AppendLine("Shopping list (still to get" + (Character != null ? "" : "") + ")");
            if (Shopping.Count == 0) sb.AppendLine("  nothing");
            foreach (var l in Shopping)
            {
                var it = l.ItemId;
                sb.Append("  ").Append(l.ToGet > 0 ? l.ToGet + " x " : "have all ").Append(NameOf(it, l)).Append(l.Have > 0 ? "  (need " + l.Need + ", have " + l.Have + ")" : "");
                if (l.ToGet > 0) sb.Append(l.Price.Known ? "  " + (l.Price.Kind == PriceKind.Estimated ? "~" : "") + ProfMoney.Format(l.Total) : "  price unknown");
                if (!string.IsNullOrEmpty(l.Where) && l.ToGet > 0) sb.Append("  [" + l.Where + (string.IsNullOrEmpty(l.Label) ? "" : ", " + l.Label) + "]");
                sb.AppendLine();
            }
            sb.AppendLine("  Total about " + Money(TotalCost, TotalApprox) + (UnknownPrices > 0 ? " + " + UnknownPrices + " item(s) with no known price" : ""));
            sb.AppendLine();
            foreach (var s in Steps)
            {
                if (s.IsMilestone) { sb.AppendLine(s.From.ToString(CultureInfo.InvariantCulture) + ": " + s.TrainText); continue; }
                sb.AppendLine(Range(s.From, s.To) + ": " + s.Recipe.Name + " x" + s.Crafts + "   (" + RecipeBands.Word(s.StartColor) + (s.EndColor != s.StartColor ? " to " + RecipeBands.Word(s.EndColor) : "") + ")");
                sb.AppendLine("    Learn: " + s.Learn);
                sb.AppendLine("    Materials: " + string.Join(", ", s.Materials.Select(m => m.Count + " x " + NameOf(m.ItemId, null))) + "   Step " + Money(s.Cost, s.CostApprox) + ", running " + Money(s.RunningCost, true));
                sb.AppendLine("    Why: " + s.Why);
            }
            foreach (var n in Notes) sb.AppendLine().AppendLine(n);
            return sb.ToString();
        }

        // set by the builder so the text can name items without the book
        public Func<int, string> ItemName = id => "item " + id;
        string NameOf(int id, ShoppingLine l) => ItemName(id);
    }

    public static class SkillGuideBuilder
    {
        // `have` = the items the user already holds (all characters or one), `start` = the character's current skill (1 for "from scratch")
        public static SkillGuide Build(ProfessionBook book, Profession prof, int start, int target, IProfessionSources src, IDictionary<int, int> have = null, string character = null, bool allowHard = false, bool includeCamp = false, ProfChar learnedBy = null)
        {
            start = Math.Max(1, start);
            var g = new SkillGuide { Profession = prof, Start = start, Target = Math.Max(start, target), Character = character, Reached = start };
            g.ItemName = id => book.Item(id)?.Name ?? "item " + id;
            have = have ?? new Dictionary<int, int>();
            src = src ?? new FixedSources();
            if (prof.Gathering) { g.Notes.Add("Gathering professions have a zone guide instead (no recipes to craft)."); return g; }

            var priceCache = new Dictionary<int, ItemPrice>();
            ItemPrice Price(int id) { if (!priceCache.TryGetValue(id, out var p)) priceCache[id] = p = src.PriceOf(id); return p; }
            double CraftCost(ProfRecipe r) => r.Reagents.Sum(x => (double)Price(x.ItemId).Copper * x.Count);
            bool Approx(ProfRecipe r) => r.Reagents.Any(x => Price(x.ItemId).Kind != PriceKind.Vendor);

            var scan = learnedBy?.ScanOf(prof.Name, prof.Id);      // what the character's profession window said (null = never read)
            bool KnownR(ProfRecipe r) => scan != null && scan.Ids.Contains(r.SpellId);
            var recipes = (includeCamp ? prof.Recipes : prof.Craftable).ToList();
            // gettable: starter / trainer / special passives are free to learn; a recipe item that a vendor sells is gettable; the rest is hard
            var hard = new Dictionary<int, bool>();
            var extra = new Dictionary<int, double>();         // the vendor price of the recipe item, paid once
            foreach (var r in recipes)
            {
                bool h = false; double x = 0;
                if (KnownR(r)) { }                                        // already learned: nothing to get
                else if (r.Learn == LearnKind.RecipeItem)
                {
                    var sold = r.TeachingItems.Where(i => src.SoldByVendor(i)).ToList();
                    if (sold.Count > 0) x = sold.Min(i => (double)Price(i).Copper); else h = true;
                }
                else if (r.Learn == LearnKind.Special) h = true;
                hard[r.SpellId] = h; extra[r.SpellId] = x;
            }

            // the skill points
            var pick = new List<ProfRecipe>(); var pickColor = new List<SkillColor>(); var pickCrafts = new List<double>(); var pickWhy = new List<string>();
            ProfRecipe current = null;
            int s0 = start;
            var caps = prof.Ranks.Select(r => r.Cap).OrderBy(c => c).ToList();
            for (int s = start; s < g.Target; s++)
            {
                var best = Choose(s, false, recipes, hard, CraftCost, current, out var why, out bool usedHard);
                if (best == null) { best = Choose(s, true, recipes, hard, CraftCost, current, out why, out usedHard); if (best != null) usedHard = true; }
                if (best == null) { g.Notes.Add("No recipe of " + prof.Name + " gives a skill-up at " + s + ": the guide stops there (grey recipes only, or the recipes need a drop the game data cannot place)."); break; }
                current = best;
                pick.Add(best); pickColor.Add(best.ColorAt(s)); pickCrafts.Add(RecipeBands.CraftsPerPoint(best.ColorAt(s))); pickWhy.Add(why);
                g.Reached = s + 1;
            }

            // compress into steps, split at rank caps
            int i2 = 0;
            long running = 0;
                        if (start <= 1 && prof.Ranks.Count > 0)
            {
                var first = prof.Ranks.OrderBy(r => r.Cap).First();
                g.Steps.Add(new GuideStep { From = 1, To = 1, Train = first, TrainText = "Learn " + prof.Name + " (" + first.Name + ", skill up to " + first.Cap + ") from a trainer first." });
            }
            while (i2 < pick.Count)
            {
                int skillNow = start + i2;
                // a cap reached exactly: train the next rank before going on
                var capHere = caps.FirstOrDefault(c => c == skillNow);
                if (capHere != 0)
                {
                    var next = prof.Ranks.OrderBy(r => r.Cap).FirstOrDefault(r => r.Cap > capHere);
                    if (next != null) g.Steps.Add(new GuideStep { From = skillNow, To = skillNow, Train = next, TrainText = "Skill " + skillNow + " is the cap: train " + next.Name + " " + prof.Name + " at the trainer (skill up to " + next.Cap + ")." });
                }
                var r = pick[i2];
                int j = i2; double crafts = 0;
                do { crafts += pickCrafts[j]; j++; } while (j < pick.Count && pick[j] == r && !caps.Contains(start + j));
                var st = new GuideStep { From = start + i2, To = start + j, Recipe = r, Crafts = (int)Math.Ceiling(crafts - 1e-9), StartColor = pickColor[i2], EndColor = pickColor[j - 1], Hard = hard[r.SpellId] };
                foreach (var m in r.Reagents) st.Materials.Add(new Reagent { ItemId = m.ItemId, Count = m.Count * st.Crafts });
                st.Cost = (long)Math.Round(CraftCost(r) * st.Crafts);
                st.CostApprox = Approx(r);
                // the recipe item is bought once with the first step that uses it
                bool isKnown = KnownR(r);
                st.Known = isKnown; st.Unlearned = scan != null && !isKnown;
                var learnItem = r.Learn == LearnKind.RecipeItem && !isKnown ? (r.TeachingItems.FirstOrDefault(x => src.SoldByVendor(x)) is int vi && vi > 0 ? vi : r.TeachingItems[0]) : 0;
                st.LearnItemId = learnItem;
                if (isKnown) st.Learn = "already learned";
                else switch (r.Learn)
                {
                    case LearnKind.Starter: st.Learn = "known from the start (taught with the profession)"; break;
                    case LearnKind.Trainer: st.Learn = "trainer"; break;
                    case LearnKind.RecipeItem:
                        st.LearnSources = src.SourcesOf(learnItem, 2);
                        st.Learn = "recipe item " + g.ItemName(learnItem) + (st.LearnSources.Count > 0 ? " - " + st.LearnSources[0].Text + " (" + st.LearnSources[0].Label + ")" : " - no source known");
                        break;
                    default: st.Learn = "special (no trainer or recipe item in the game data)"; break;
                }
                if (st.Unlearned)
                {
                    var src0 = st.LearnSources.FirstOrDefault();
                    st.Learn = "Learn first: " + (r.Learn == LearnKind.RecipeItem ? g.ItemName(learnItem) + (src0 != null ? " - " + src0.Text + " (" + src0.Label + ")" : " - no source known")
                        : r.Learn == LearnKind.Trainer ? r.Name + " - at the trainer" : r.Learn == LearnKind.Starter ? r.Name + " - taught with the profession" : r.Name + " - special");
                }
                st.Why = pickWhy[i2];
                running += st.Cost + (isKnown ? 0 : (long)extra[r.SpellId]);
                st.RunningCost = running;
                g.Steps.Add(st);
                i2 = j;
            }

            // shopping list
            var need = new Dictionary<int, int>();
            foreach (var st in g.Steps.Where(x => !x.IsMilestone)) foreach (var m in st.Materials) need[m.ItemId] = (need.TryGetValue(m.ItemId, out var n) ? n : 0) + m.Count;
            foreach (var kv in need.OrderByDescending(k => Price(k.Key).Copper * (long)k.Value).ThenBy(k => k.Key))
            {
                have.TryGetValue(kv.Key, out var hv);
                var line = new ShoppingLine { ItemId = kv.Key, Need = kv.Value, Have = Math.Min(hv, kv.Value), Price = Price(kv.Key) };
                line.ToGet = line.Need - line.Have;
                var how = src.SourcesOf(kv.Key, 1).FirstOrDefault();
                if (how != null) { line.Where = how.Text; line.Label = how.Label; line.Link = how.Link; }
                else line.Where = line.Price.Kind == PriceKind.Unknown ? "no known source" : "farm or buy from players (price estimated)";
                g.Shopping.Add(line);
            }
            var recipeBuys = g.Steps.Where(x => !x.IsMilestone && x.LearnItemId > 0 && src.SoldByVendor(x.LearnItemId)).Select(x => x.LearnItemId).Distinct();
            foreach (var id in recipeBuys)
            {
                have.TryGetValue(id, out var hv);
                var how = src.SourcesOf(id, 1).FirstOrDefault();
                g.Shopping.Add(new ShoppingLine { ItemId = id, Need = 1, Have = Math.Min(1, hv), ToGet = Math.Max(0, 1 - hv), Price = Price(id), Where = how?.Text ?? "vendor", Label = how?.Label ?? "", Link = how?.Link ?? "", IsRecipe = true });
            }
            foreach (var l in g.Shopping.Where(x => x.ToGet > 0))
            {
                if (!l.Price.Known) g.UnknownPrices++; else { g.TotalCost += l.Total; if (l.Price.Kind != PriceKind.Vendor) g.TotalApprox = true; }
            }
            if (g.UnknownPrices > 0) g.TotalApprox = true;
            if (g.Reached < g.Target && g.Notes.Count == 0) g.Notes.Add("The guide reaches skill " + g.Reached + " of " + g.Target + ".");
            g.Notes.Add("A skill-up is not certain on yellow and green recipes, so the craft counts are averages: expect to make a few more or fewer. Prices are copper per item from a known vendor (Classic baseline or Lodge); ~ marks an estimate from the item's own value.");
            if (scan != null)
                g.Notes.Add("The game data holds no 'required skill' for recipes, so the earliest a recipe can be learned is estimated from its colour band. Which recipes " + learnedBy.Name + " has learned comes from the " + prof.Name + " window the Elan's Hub addon read" + (scan.At > 0 ? " on " + DateTimeOffset.FromUnixTimeSeconds(scan.At).LocalDateTime.ToString("d MMM yyyy", CultureInfo.InvariantCulture) : "") + (scan.Partial ? " (a partial read: open the window with every group expanded to read all of it)" : "") + "; steps with a recipe already learned have no learn step.");
            else
                g.Notes.Add("The game data holds no 'required skill' for recipes, so the earliest a recipe can be learned is estimated from its colour band. Open the profession window in game once (with Elan's Hub 1.14.0 or newer) and the guide knows which recipes are already learned.");
            return g;
        }

        // the best recipe at skill `s` (null: none); `allowHard` also lets in recipes that only a drop / quest recipe item teaches
        static ProfRecipe Choose(int s, bool allowHard, List<ProfRecipe> recipes, Dictionary<int, bool> hard, Func<ProfRecipe, double> cost, ProfRecipe current, out string why, out bool usedHard)
        {
            usedHard = false; why = "";
            var cands = recipes.Where(r => r.Start <= s && (allowHard || !hard[r.SpellId])).Select(r => new { r, c = r.ColorAt(s) })
                .Where(x => x.c == SkillColor.Orange || x.c == SkillColor.Yellow || x.c == SkillColor.Green).ToList();
            if (cands.Count == 0) return null;
            var good = cands.Where(x => x.c != SkillColor.Green).ToList();
            var pool = good.Count > 0 ? good : cands;
            double Score(ProfRecipe r) => RecipeBands.CraftsPerPoint(r.ColorAt(s)) * cost(r);
            var best = pool.OrderBy(x => Score(x.r)).ThenBy(x => x.c).ThenBy(x => x.r.Low).ThenBy(x => x.r.SpellId).First();
            var chosen = best.r;
            // keep the recipe in use while it is still orange / yellow (or the best is green too) and not much dearer
            if (current != null)
            {
                var cur = pool.FirstOrDefault(x => x.r == current);
                if (cur != null && Score(current) <= Score(best.r) * 1.25 + 1e-9) chosen = current;
            }
            var col = chosen.ColorAt(s);
            usedHard = hard[chosen.SpellId];
            double sc = Score(chosen);
            if (pool.Count == 1) why = (col == SkillColor.Green ? "the only option that still gives skill-ups here (green: ~2.5 crafts per point)" : "the only " + RecipeBands.Word(col).ToLowerInvariant() + " recipe you can make at this skill");
            else why = "cheapest of " + pool.Count + " " + (good.Count > 0 ? "orange/yellow" : "green") + " options at " + s + (sc > 0 ? " (about " + ProfMoney.Format((long)Math.Round(sc)) + " per skill point)" : " (reagents cost nothing at a vendor, or no price is known)");
            if (usedHard) why += "; hard to get: its recipe item is a drop or quest reward";
            return chosen;
        }
    }
}
