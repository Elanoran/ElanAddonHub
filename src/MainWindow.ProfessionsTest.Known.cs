using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Professions;

namespace ElansAddonHub
{
    // The learned recipes (Hub 1.14.0, Outpost 2.52.0) inside the "professions" section, on the same synthetic Tailoring (197) / Cooking (185) world:
    //   model    - the Hub module's `known` field (parsing, caps, junk), ProfChar.Known, CraftNow, the leveling guide ("already learned" / "Learn first: ...")
    //   page     - Known / Not learned chips, the Any / Learned / Not learned filter, the "last read" line, Craft now, the guide, "Who in the Lodge can craft this?"
    //   atlas    - "Learned by" on a recipe (spell) page and on the page of the item it makes, the "Who in the Lodge" section, the "Recipes you can learn" preset
    public partial class MainWindow
    {
        static Dictionary<string, object> KL(params object[] items)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i < items.Length; i++) d[(i + 1).ToString(CultureInfo.InvariantCulture)] = items[i];
            return d;
        }

        // a Hub character with a professions module: one Tailoring (line 197) profession and, when `ids` is given, its read
        static HubChar KnownHub(string name, int skill, int[] ids, bool partial, long at, object extraKnown = null, bool withLine = true)
        {
            var hc = new HubChar { Key = "Realm-" + name, At = at };
            var prof = D("name", "Tailoring", "skill", (double)skill, "max", 150.0, "kind", "primary");
            if (withLine) prof["line"] = 197.0;
            var data = D("at", (double)at, "list", KL(prof));
            if (ids != null)
            {
                var known = new Dictionary<string, object> { ["197"] = D("at", (double)at, "partial", partial, "n", "Tailoring", "ids", KL(ids.Select(i => (object)(double)i).ToArray())) };
                if (extraKnown is Dictionary<string, object> more) foreach (var kv in more) known[kv.Key] = kv.Value;
                data["known"] = known;
            }
            hc.Modules["professions"] = new HubModule { Name = "professions", V = 1, At = at, By = "ElansHub", Ver = "1.14.0", Data = data };
            return hc;
        }

        static InvChar KnownInv(string name, string cls, int skill, int[] ids, bool partial, long at, bool withLine = true) =>
            new InvChar { Key = "Realm-" + name, Name = name, Class = cls, Level = 40, Realm = "Realm", Hub = KnownHub(name, skill, ids, partial, at, null, withLine) };

        // ============================================================================================================================ model
        void ProfKnownModel(ProfessionBook book, Profession tail, IProfessionSources src, CheckFn Check)
        {
            long at = 1_790_000_000;
            // ---- the Hub module
            var pm = HubModules.ProfessionsOf(KnownHub("Bob", 80, new[] { 1001, 1003, 1003, 0, -4 }, false, at));
            Check("hub: `known` is read per skill line (line 197: Tailoring, a full read, 2 recipes: duplicates, 0 and negatives dropped) and the list carries the line",
                pm != null && pm.Known.Count == 1 && pm.Known[197].Name == "Tailoring" && !pm.Known[197].Partial && pm.Known[197].At == at && pm.Known[197].Ids.SetEquals(new[] { 1001, 1003 }) && pm.List[0].Line == 197);
            var junk = new Dictionary<string, object>
            {
                ["abc"] = D("ids", KL(1.0)), ["0"] = D("ids", KL(1.0)), ["-5"] = D("ids", KL(1.0)), ["99999999"] = D("ids", KL(1.0)), ["185"] = "not a table", ["186"] = D("at", 1.0),
                ["187"] = D("partial", false, "ids", KL("x", true, 1.5, 7.0, double.NaN, double.PositiveInfinity, 3e12)),
            };
            var pj = HubModules.ProfessionsOf(KnownHub("Bob", 80, new[] { 1001 }, true, at, junk));
            Check("hub: junk lines (not a number, 0, negative, huge, not a table) are dropped; a line with only valid ids stays; fractions, text, NaN and huge ids inside are dropped",
                pj.Known.Keys.OrderBy(k => k).SequenceEqual(new[] { 186, 187, 197 }) && pj.Known[187].Ids.SetEquals(new[] { 7 }) && !pj.Known[187].Partial && pj.Known[186].Ids.Count == 0, string.Join(",", pj.Known.Keys));
            Check("hub: a missing `partial` counts as partial (only an explicit false is a full read)", pj.Known[186].Partial && pj.Known[197].Partial);
            var big = Enumerable.Range(1, 5000).ToArray();
            Check("hub: at most 2,000 ids per profession are taken", HubModules.ProfessionsOf(KnownHub("Bob", 80, big, false, at)).Known[197].Ids.Count == 2000);
            var manyLines = new Dictionary<string, object>();
            for (int i = 1; i <= 40; i++) manyLines[(1000 + i).ToString()] = D("partial", false, "ids", KL(5.0));
            Check("hub: at most 16 professions are taken", HubModules.ProfessionsOf(KnownHub("Bob", 80, new[] { 1 }, false, at, manyLines)).Known.Count == 16);
            var old = HubModules.ProfessionsOf(KnownHub("Old", 50, null, false, at, null, false));
            Check("hub: a Hub older than 1.14.0 (no known, no line) reads as before: the list, no scans, line 0", old != null && old.List.Count == 1 && old.List[0].Line == 0 && old.Known.Count == 0 && old.List[0].Skill == 50);

            // ---- ProfChar
            var inv = new List<InvChar> { KnownInv("Bob", "WARRIOR", 80, new[] { 1001, 1003 }, false, at), KnownInv("Alice", "MAGE", 20, null, false, at), KnownInv("Cy", "ROGUE", 80, new[] { 1001 }, true, at, false) };
            foreach (var c in inv) c.Hub.Modules["core"] = new HubModule { Name = "core", V = 1, At = at, Data = D("name", c.Name, "level", 40.0, "class", c.Class) };
            var pcs = Holdings.From(inv);
            var pBob = pcs.First(c => c.Name == "Bob"); var pAlice = pcs.First(c => c.Name == "Alice"); var pCy = pcs.First(c => c.Name == "Cy");
            Check("ProfChar: the scan and the profession's line come from the Hub module (Bob 197: 2 recipes), Alice has none", pBob.Known.ContainsKey(197) && pBob.Lines["Tailoring"] == 197 && pAlice.Known.Count == 0 && pAlice.ScanOf("Tailoring", 197) == null);
            Check("Knows: true for a learned recipe, false for one the read lacks, null (unknown) when the window was never read", pBob.Knows("Tailoring", 197, 1001) == true && pBob.Knows("Tailoring", 197, 1004) == false && pAlice.Knows("Tailoring", 197, 1001) == null && pBob.Knows("Cooking", 185, 1301) == null);
            Check("ScanOf finds the read by the profession's name too (the skill line id of the game data differs)", pBob.ScanOf("tailoring", 0) != null && pBob.ScanOf("Tailoring", 9999) != null && pCy.ScanOf("Tailoring", 197) != null && pBob.KnowsSpell(1003) && !pBob.KnowsSpell(1004));

            // ---- craft now
            var bolt = tail.Recipes.First(r => r.Name == "Bolt of Linen");
            var alice = PChar("Alice", "MAGE", "Tailoring", 20, 75, "Cooking", 60, 75, 100, 30, 101, 2);
            var bob = PChar("Bob", "WARRIOR", "Tailoring", 80, 150, 100, 5, 102, 8, 103, 3);
            bob.Known[197] = new HubKnownScan { Line = 197, Name = "Tailoring", At = at, Partial = false, Ids = new HashSet<int> { 1001, 1003 } };
            var chars = new List<ProfChar> { alice, bob };
            var rows = CraftNow.Compute(book, chars);
            var by = rows.ToDictionary(r => r.Recipe.Name + (r.Recipe.IsCamp ? "*" : ""), r => r);
            Check("craft now with Bob's read (Bolt of Linen, Heavy Cloak learned): Robe of Testing and Rare Cape are gone (Bob has not learned them; Alice's skill 20 is too low); Plain Thing stays only through Alice, whose window was never read",
                !by.ContainsKey("Robe of Testing") && !by.ContainsKey("Rare Cape") && by.ContainsKey("Plain Thing") && by["Plain Thing"].Crafters.Select(c => c.Name).SequenceEqual(new[] { "Alice" }) && by["Plain Thing"].Unconfirmed && by.ContainsKey("Heavy Cloak") && by.ContainsKey("Bolt of Linen"), string.Join(", ", rows.Select(r => r.ToString())));
            Check("craft now: Heavy Cloak's best crafter is Bob and confirmed; Bolt of Linen lists Bob (learned) and Alice (never read: the skill estimate) and Bob is the best, confirmed",
                by["Heavy Cloak"].Best.Name == "Bob" && !by["Heavy Cloak"].Unconfirmed && by["Bolt of Linen"].Crafters.Select(c => c.Name).SequenceEqual(new[] { "Bob", "Alice" }) && !by["Bolt of Linen"].Unconfirmed);
            Check("craft now: Roast A (Cooking, only Alice has it and her window was never read) is offered but 'not confirmed'", by["Roast A"].Best.Name == "Alice" && by["Roast A"].Unconfirmed);
            var aliceKnows = PChar("Alice", "MAGE", "Tailoring", 20, 75, 100, 30);
            aliceKnows.Known[197] = new HubKnownScan { Line = 197, Name = "Tailoring", At = at, Partial = true, Ids = new HashSet<int>() };
            Check("craft now: a character whose read is EMPTY (a partial read that found nothing) offers no Tailoring recipe at all", !CraftNow.Compute(book, new List<ProfChar> { aliceKnows }).Any(r => r.Profession.Name == "Tailoring"));

            // ---- the leveling guide
            var plainGuide = SkillGuideBuilder.Build(book, tail, 1, 100, src, null);
            var plainLines = plainGuide.Steps.Select(GuideLine).ToList();
            bob.Known[197].Ids = new HashSet<int> { 1001, 1003 };
            var gu = SkillGuideBuilder.Build(book, tail, 1, 100, src, null, "Bob", false, false, bob);
            Check("guide with a read (Robe of Testing not learned): the same steps; Bolt of Linen and Heavy Cloak 'already learned' with no learn item, the Robe 'Learn first: Pattern: Robe of Testing - Sold by Tess the Weaver (Lodge)'",
                gu.Steps.Select(GuideLine).SequenceEqual(plainLines) && gu.Steps[1].Known && gu.Steps[1].Learn == "already learned" && gu.Steps[1].LearnItemId == 0 && !gu.Steps[1].Unlearned
                && gu.Steps[2].Unlearned && gu.Steps[2].LearnItemId == 3001 && gu.Steps[2].Learn.StartsWith("Learn first: Pattern: Robe of Testing") && gu.Steps[2].Learn.Contains("Tess the Weaver") && gu.Steps[4].Known && gu.Steps[4].Learn == "already learned", string.Join(" | ", gu.Steps.Select(s => s.Learn)));
            Check("guide: the recipe item is still on the shopping list (the Robe is not learned), 72,900c as before", gu.Shopping.Any(l => l.IsRecipe && l.ItemId == 3001) && gu.TotalCost == 72900, gu.TotalCost.ToString());
            bob.Known[197].Ids.Add(1004);
            var gk = SkillGuideBuilder.Build(book, tail, 1, 100, src, null, "Bob", false, false, bob);
            Check("guide with the Robe learned too: no learn step or cost for it - no recipe item on the shopping list, the total is 500c lower (72,400c), the running cost of the Robe step has no 500c",
                gk.Steps[2].Known && gk.Steps[2].LearnItemId == 0 && gk.Steps[2].Learn == "already learned" && !gk.Shopping.Any(l => l.IsRecipe) && gk.TotalCost == 72400 && gk.Steps[2].RunningCost == 6400 + 49 * 200, gk.TotalCost + " " + gk.Steps[2].RunningCost);
            Check("guide: the note says whose read it is (window of the Elan's Hub addon, a date, steps with a learned recipe have no learn step); without a read it says to open the window", gk.Notes.Any(n => n.Contains("Bob has learned comes from the Tailoring window") && n.Contains("2026")) && plainGuide.Notes.Any(n => n.Contains("Open the profession window in game once")));
            bob.Known[197].Partial = true; bob.Known[197].Ids.Remove(1004);
            var gp = SkillGuideBuilder.Build(book, tail, 1, 100, src, null, "Bob", false, false, bob);
            Check("guide with a partial read: says so in the note", gp.Notes.Any(n => n.Contains("a partial read")));
            var starterMissing = new ProfChar { Key = "Realm-Dee", Name = "Dee" };
            starterMissing.Skills["Tailoring"] = new KeyValuePair<int, int>(1, 75);
            starterMissing.Known[197] = new HubKnownScan { Line = 197, Name = "Tailoring", At = at, Partial = false, Ids = new HashSet<int> { 1003 } };
            var gd = SkillGuideBuilder.Build(book, tail, 1, 40, src, null, "Dee", false, false, starterMissing);
            Check("guide: a recipe taught with the profession that the read lacks says what to do instead of claiming it is known", gd.Steps.First(s => !s.IsMilestone).Unlearned && gd.Steps.First(s => !s.IsMilestone).Learn.StartsWith("Learn first: Bolt of Linen"), gd.Steps.First(s => !s.IsMilestone).Learn);
            var ho = GuideHandoff.FromSkillGuide(tail, gu);
            Check("in-game steps: an unlearned recipe says 'learn first: <recipe item>', a learned one has no learn text",
                ho.Steps.First(s => s.K == "craft" && s.Spell == 1004).Text == "Robe of Testing (learn first: Pattern: Robe of Testing)" && ho.Steps.First(s => s.K == "craft" && s.Spell == 1001).Text == "Bolt of Linen", string.Join(" | ", ho.Steps.Select(s => s.Text)));
        }

        // ============================================================================================================================ page
        async Task ProfKnownPage(ProfessionsPage page, CheckFn Check)
        {
            long at = 1_790_000_000;
            var alice = PChar("Alice", "MAGE", "Tailoring", 20, 75, "Cooking", 60, 75, 100, 30, 101, 2); alice.FromHub = true;
            var bob = PChar("Bob", "WARRIOR", "Tailoring", 80, 150, 100, 5, 102, 8, 103, 3); bob.FromHub = true;
            bob.Known[197] = new HubKnownScan { Line = 197, Name = "Tailoring", At = at, Partial = false, Ids = new HashSet<int> { 1001, 1003 } };
            page.SetProfCharsForTest(new[] { alice, bob });
            page.ShowForTest("recipes", "Tailoring", "Bob");
            var texts = page.BodyTextsForTest();
            Check("recipes tab with a read: 2 rows say Known, 6 say Not learned; the note says 'Learned 2 of 8 - last read <date>.'", texts.Count(t => t == "Known") == 2 && texts.Count(t => t == "Not learned") == 6 && texts.Any(t => t.StartsWith("Learned 2 of 8 - last read ") && t.Contains("2026")), string.Join(" | ", texts.Where(t => t.StartsWith("Learned"))));
            Check("recipes tab: the Any / Learned / Not learned filter is there for a character with a read", page.KnownFilterVisibleForTest);
            page.KnownFilterForTest("learned");
            Check("filter Learned: Bolt of Linen and Heavy Cloak", string.Join(",", page.RecipeRowNamesForTest()) == "Bolt of Linen,Heavy Cloak", string.Join(",", page.RecipeRowNamesForTest()));
            page.KnownFilterForTest("notlearned");
            Check("filter Not learned: the other six", page.RecipeRowNamesForTest().Count == 6 && !page.RecipeRowNamesForTest().Contains("Heavy Cloak"), string.Join(",", page.RecipeRowNamesForTest()));
            page.KnownFilterForTest("all");
            page.FilterForTest("now");
            Check("'can make now' also needs the recipe to be learned: only Heavy Cloak (Bolt is grey for Bob at 80)", string.Join(",", page.RecipeRowNamesForTest()) == "Heavy Cloak", string.Join(",", page.RecipeRowNamesForTest()));
            page.FilterForTest("all");
            var tip = page.RecipeTipSheetForTest(1003);
            Check("recipe tooltip names which of your characters learned it", tip != null && Flatten(tip).Any(t => t == "Learned by Bob"), tip == null ? "no tip" : string.Join(" | ", Flatten(tip)));

            page.ShowForTest("recipes", "Tailoring", "Alice");
            texts = page.BodyTextsForTest();
            Check("recipes tab for a character whose window was never read: no chips, no learned filter, and the note says to open the Tailoring window in game once",
                !texts.Contains("Known") && !texts.Contains("Not learned") && !page.KnownFilterVisibleForTest && texts.Any(t => t.StartsWith("Open your Tailoring window in game once to read Alice's learned recipes")), string.Join(" | ", texts.Where(t => t.Contains("window"))));
            page.ShowForTest("recipes", "Tailoring");
            Check("recipes tab with no character: the note says to pick one", page.BodyTextsForTest().Any(t => t.StartsWith("Pick a character to see which of these recipes it has learned")));

            page.ShowForTest("craft", null, "Bob");
            var cr = page.CraftRowsForTest();
            Check("craft tab for Bob: only learned recipes (Bolt of Linen and Heavy Cloak; no Robe of Testing)", cr.Any(r => r.StartsWith("Heavy Cloak")) && cr.Any(r => r.StartsWith("Bolt of Linen")) && !cr.Any(r => r.StartsWith("Robe of Testing")), string.Join(" | ", cr));
            page.ShowForTest("craft");
            Check("craft tab (all together): a row whose best crafter was never read is marked 'not confirmed'", page.BodyTextsForTest().Any(t => t.Contains("not confirmed: Alice's Cooking window was never read")), string.Join(" | ", page.BodyTextsForTest().Where(t => t.Contains("confirmed"))));

            page.ShowForTest("guide", "Tailoring", "Bob");
            page.GuideRangeForTest(1, 100, "none");
            var gt = page.BodyTextsForTest();
            Check("guide tab: the Robe step says 'Learn first:' (red-flagged) with the recipe item, the learned steps say 'already learned'", gt.Contains("Learn first: ") && gt.Count(t => t == "already learned") == 3 && gt.Contains("Pattern: Robe of Testing"), string.Join(" | ", gt.Where(t => t.Contains("earn"))));
            Check("guide tab: the text export has the learn-first line and the 'already learned' lines", page.GuideTextForTest().Contains("Learn: Learn first: Pattern: Robe of Testing") && page.GuideTextForTest().Contains("Learn: already learned"));

            // ---- who in the Lodge can craft this?
            LodgeCrafters answer = new LodgeCrafters { Available = true, Pending = true };
            IReadOnlyCollection<int> asked = null;
            page.UseCrafters(spells => { asked = spells; return answer; });
            page.ShowForTest("recipes", "Tailoring", "Bob");
            page.ToggleCraftersForTest(1004);
            Check("crafters: a click asks for that recipe only and shows 'Asking the Lodge...'", asked != null && asked.Count == 1 && asked.Contains(1004) && page.CraftersTextsForTest(1004).SequenceEqual(new[] { "Asking the Lodge..." }), string.Join("|", page.CraftersTextsForTest(1004)));
            answer = new LodgeCrafters { Available = true };
            answer.Rows.Add(new LodgeCrafter { Spell = 1004, Owner = "mia", By = "Mia", Key = "Forever-Aria", Name = "Aria", Class = "HUNTER", Level = 60, Profession = "Tailoring", Skill = 150, Max = 300 });
            answer.Rows.Add(new LodgeCrafter { Spell = 1004, Owner = "bob", By = "Bob", Key = "Forever-Bobby", Name = "Bobby", Class = "MAGE", Level = 30, Profession = "Tailoring", Skill = 290, Max = 300, Partial = true, Mine = true });
            page.CraftersChanged();
            var ct = page.CraftersTextsForTest(1004);
            Check("crafters: the answer lists member, character and skill, best skill first; a partly read list and my own character are marked", ct.Count == 4 && ct[0] == "Bobby" && ct[1].Contains("(Bob) - Tailoring 290/300 · level 30 · yours · recipe list partly read") && ct[2] == "Aria" && ct[3] == " (Mia) - Tailoring 150/300 · level 60", string.Join("|", ct));
            answer = new LodgeCrafters { Available = true };
            page.CraftersChanged();
            Check("crafters: an answer with nobody says so (and how members get on the list)", page.CraftersTextsForTest(1004).Single().StartsWith("Nobody in the Lodge has shared a character that has learned this"));
            answer = new LodgeCrafters { Available = false, Problem = "This Lodge's server is older than 2.19: it cannot say who can craft something." };
            page.CraftersChanged();
            Check("crafters: an older server shows its own plain line", page.CraftersTextsForTest(1004).Single().Contains("older than 2.19"));
            page.ToggleCraftersForTest(1004);
            Check("crafters: a second click closes it again", page.CraftersTextsForTest(1004).Count == 1 && FindText(page.BodyForTest, t => t.Text == "Who in the Lodge can craft this?") != null);
            page.UseCrafters(null);
            page.ShowForTest("recipes", "Tailoring", "Bob");
            Check("crafters: without the Lodge hook (no session) there is no link", FindText(page.BodyForTest, t => t.Text == "Who in the Lodge can craft this?") == null);
            page.SetProfCharsForTest(new[] { alice, bob });
            await Task.Delay(10);
        }

        // ============================================================================================================================ Atlas
        void ProfKnownAtlas(CheckFn Check)
        {
            long at = 1_790_000_000;
            var savedChars = AtlasQuery.Context.Characters; var savedCrafters = AtlasQuery.Context.Crafters;
            try
            {
                var bobInv = KnownInv("Bob", "WARRIOR", 80, new[] { 1001, 1003 }, false, at);
                var aliceInv = KnownInv("Alice", "MAGE", 20, null, false, at);
                var cyInv = KnownInv("Cy", "ROGUE", 60, new[] { 1004, 1002 }, true, at);
                var list = new List<InvChar> { bobInv, aliceInv, cyInv };
                AtlasQuery.Context.Characters = () => list;
                AtlasQuery.Context.Crafters = null;

                var spell = AtlasQuery.Build("spell:1004");
                var f = spell.Facts.FirstOrDefault(x => x.Label == "Learned by");
                Check("atlas: the recipe (spell) page says which of my characters learned it (Cy), with my own label", f != null && f.Value == "Cy" && f.Source == AtlasLabel.Mine, f?.ToString());
                var spell2 = AtlasQuery.Build("spell:1001");
                Check("atlas: another recipe learned by Bob only", spell2.Facts.First(x => x.Label == "Learned by").Value == "Bob");
                var spell3 = AtlasQuery.Build("spell:1005");
                Check("atlas: nobody read has learned it: 'none of your characters'", spell3.Facts.First(x => x.Label == "Learned by").Value == "none of your characters");
                var item = AtlasQuery.Build("item:2004");
                Check("atlas: the page of the item a recipe makes shows the same line (Robe of Testing: Cy)", item.Facts.Any(x => x.Label == "Learned by" && x.Value == "Cy"), string.Join(" | ", item.Facts));
                list.Clear(); list.Add(aliceInv);
                Check("atlas: a profession whose window was never read says to open it (Alice has Tailoring, no read)", AtlasQuery.Build("spell:1004").Facts.First(x => x.Label == "Learned by").Value == "not read yet: open your Tailoring window in game");
                list.Clear();
                Check("atlas: no characters at all, no line", !AtlasQuery.Build("spell:1004").Facts.Any(x => x.Label == "Learned by"));
                list.AddRange(new[] { bobInv, aliceInv, cyInv });

                // ---- who in the Lodge
                Check("atlas: without a Lodge hook there is no Lodge section", !AtlasQuery.Build("spell:1004").Sections.Any(s => s.Key == "crafters"));
                var ans = new LodgeCrafters { Available = true, Pending = true };
                IReadOnlyCollection<int> asked = null;
                AtlasQuery.Context.Crafters = sp => { asked = sp; return ans; };
                var pend = AtlasQuery.Build("spell:1004").Sections.FirstOrDefault(s => s.Key == "crafters");
                Check("atlas: the Lodge section asks for the recipe and says 'Asking the Lodge...' while it waits", pend != null && pend.Title == "Who in the Lodge can craft this?" && pend.Empty == "Asking the Lodge..." && asked != null && asked.SequenceEqual(new[] { 1004 }) && pend.Rows.Count == 0);
                ans = new LodgeCrafters { Available = true };
                ans.Rows.Add(new LodgeCrafter { Spell = 1004, Owner = "mia", By = "Mia", Key = "Forever-Aria", Name = "Aria", Class = "HUNTER", Level = 60, Profession = "Tailoring", Skill = 150, Max = 300 });
                ans.Rows.Add(new LodgeCrafter { Spell = 1004, Owner = "zed", By = "Zed", Key = "Forever-Zed", Name = "Zed", Class = "MAGE", Level = 20, Profession = "Tailoring", Skill = 290, Max = 300 });
                var sec = AtlasQuery.Build("spell:1004").Sections.First(s => s.Key == "crafters");
                Check("atlas: the answer becomes rows - character (member), profession, skill and level - best skill first", sec.Rows.Count == 2 && sec.Rows[0].Title == "Zed (Zed)" && sec.Rows[0].Detail == "Tailoring 290/300 · level 20" && sec.Rows[1].Title == "Aria (Mia)" && sec.Rows[1].Detail == "Tailoring 150/300 · level 60", string.Join(" | ", sec.Rows.Select(r => r.Title + ": " + r.Detail)));
                var itemSec = AtlasQuery.Build("item:2004").Sections.FirstOrDefault(s => s.Key == "crafters");
                Check("atlas: the item page asks for every recipe that makes it and shows the section", itemSec != null && itemSec.Rows.Count == 2 && asked.SequenceEqual(new[] { 1004 }));
                ans = new LodgeCrafters { Available = false, Problem = "older" };
                Check("atlas: an older server / a guest / offline: no section at all", !AtlasQuery.Build("spell:1004").Sections.Any(s => s.Key == "crafters"));
                AtlasQuery.Context.Crafters = sp => throw new InvalidOperationException("boom");
                Check("atlas: a failing Lodge hook never breaks the page", AtlasQuery.Build("spell:1004").Facts.Any(x => x.Label == "Profession"));

                // ---- browse: Recipes you can learn
                AtlasQuery.Context.Crafters = null;
                list.Clear(); list.Add(bobInv); list.Add(aliceInv);
                AtlasBrowse.ForgetForTest();
                var can = AtlasBrowse.Run("browse:recipes?learn=can");
                Check("browse: 'Recipes you can learn' - Bob (Tailoring 80, Bolt and Heavy Cloak learned) can learn the other five he has the skill for; the Spinning Wheel (from skill 135) is not offered; Alice was never read",
                    !can.NotFound && can.Rows.Select(r => r.Title).OrderBy(x => x).SequenceEqual(new[] { "Faction Banner", "Linen Cloak", "Plain Thing", "Rare Cape", "Robe of Testing" }), string.Join(",", can.Rows.Select(r => r.Title)));
                Check("browse: the id keeps the filter (learn=can), it counts as a filter to clear, and the control is offered", can.Query.Learn == "can" && can.Id == "browse:recipes?learn=can" && can.Query.HasFilters && can.Controls.Learn);
                Check("browse: all recipes are still listed without it (8 of Tailoring + Cooking's 7 + the class skill's, from the data)", AtlasBrowse.Run("browse:recipes").Total > can.Total);
                var pre = AtlasBrowse.Presets(bobInv);
                var pr = pre.FirstOrDefault(p => p.Title == "Recipes you can learn");
                Check("browse: the preset 'Recipes you can learn' is offered with its count, for the character it is worked out for (Bob: for=Realm-Bob)", pr != null && pr.Count == 5 && pr.Id == "browse:recipes?for=Realm-Bob&learn=can", pr == null ? "none: " + string.Join(",", pre.Select(p => p.Title)) : pr.Count + " " + pr.Id);
                Check("browse: for Alice (never read) the same shortcut is left out; the list for=Realm-Alice is empty while Bob's has his five",
                    !AtlasBrowse.Presets(aliceInv).Any(p => p.Title == "Recipes you can learn") && AtlasBrowse.Run("browse:recipes?learn=can&for=Realm-Alice").Total == 0 && AtlasBrowse.Run("browse:recipes?learn=can&for=Realm-Bob").Total == 5);
                list.Clear(); list.Add(aliceInv);
                AtlasBrowse.ForgetForTest();
                Check("browse: without any read (Alice only) the preset is left out, the list is empty and its note says to open the profession windows",
                    !AtlasBrowse.Presets().Any(p => p.Title == "Recipes you can learn") && AtlasBrowse.Run("browse:recipes?learn=can").Total == 0 && AtlasBrowse.Run("browse:recipes?learn=can").Notes.Any(n => n.Contains("open each profession window in game once")));
            }
            finally
            {
                AtlasQuery.Context.Characters = savedChars; AtlasQuery.Context.Crafters = savedCrafters;
                AtlasBrowse.ForgetForTest();
            }
        }
    }
}
