using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Professions;

namespace ElansAddonHub
{
    // --selftest "professions": Atlas layer H. Everything runs on tables built in the test (a spell snapshot, items and item effects made by hand) so the
    // numbers can be checked by hand; only the last, optional part reads the real game files (read-only, skipped without a WoW install) and takes screenshots.
    //
    // The synthetic world:
    //   Tailoring (197): Bolt of Linen 20/40 (starter), Linen Cloak 30/60 (trainer), Heavy Cloak 60/100 (trainer, wool: no vendor),
    //   Robe of Testing 60/90 (recipe item 3001 sold by a vendor; taught through a Learn-Spell effect), Rare Cape 60/90 (recipe item 3002 is a drop; taught with trigger 6),
    //   Plain Thing 0/0 (no skill-up); ranks Apprentice 75 / Journeyman 150; camp: Faction Banner (Tier 1), Spinning Wheel (Tier 2, blueprint 3100)
    //   Cooking (185, secondary): 5 recipes + Basic / Journeyman Campfire kits;  Herbalism (182): ranks only;  Skinning (393): ranks only
    //   (a class skill with two recipes, a [DNT] test skill and a child skill line are no professions)
    public partial class MainWindow
    {
        // ---- builders
        static SpellInfo PSp(int id, string name, string rank = "", string desc = "", int dur = 0, params EffectInfo[] fx) => new SpellInfo { Id = id, Name = name, Rank = rank, Desc = desc, DurationMs = dur, Effects = fx.Length == 0 ? SpellInfo.NoEffects : fx };
        static EffectInfo PCreate(int item, float n = 1) => new EffectInfo { Index = 0, Effect = EffectInfo.CreateItem, ItemType = item, Points = n };
        static EffectInfo PLearn(int spell) => new EffectInfo { Index = 0, Effect = 36, Trigger = spell };
        static ClientItem PIt(int id, string name, int quality = 1, long buy = 0, long sell = 0, int cls = -1, int sub = -1) => new ClientItem { Id = id, Name = name, Quality = quality, BuyPrice = buy, SellPrice = sell, ClassId = cls, SubclassId = sub, IconId = 0 };
        static SkillAbility PAb(int id, int skill, int spell, int low, int high, int acquire = 0) => new SkillAbility { Id = id, Skill = skill, Spell = spell, MinRank = 1, Green = low, Gray = high, Acquire = acquire };

        sealed class ProfSynth
        {
            public SpellSnapshot Snap;
            public List<ClientItem> Items = new List<ClientItem>();
            public Dictionary<int, int[]> Effects = new Dictionary<int, int[]>();
            public AtlasSet Set;
        }

        static ProfSynth ProfBuild()
        {
            var s = new ProfSynth();
            var sp = new List<SpellInfo>(); var ab = new List<SkillAbility>(); var rg = new Dictionary<int, int[]>(); int abId = 1;
            void Recipe(int skill, int spell, string name, int item, int low, int high, int acquire, int[] reagents, string rank = "", float n = 1)
            {
                sp.Add(PSp(spell, name, rank, "", 0, PCreate(item, n)));
                ab.Add(PAb(abId++, skill, spell, low, high, acquire));
                rg[spell] = reagents;
            }
            void Rank(int skill, int spell, string name, string rank, int cap)
            {
                sp.Add(PSp(spell, name, rank, "Allows you to work up to a maximum potential skill of " + cap + "."));
                ab.Add(PAb(abId++, skill, spell, 0, 0));
            }
            // items
            s.Items.AddRange(new[]
            {
                PIt(100, "Linen Cloth", 1, 100, 25, 7, 5), PIt(101, "Coarse Thread", 1, 50, 10, 7, 11), PIt(102, "Wool Cloth", 1, 400, 100, 7, 5), PIt(103, "Simple Wood", 1, 20, 5, 7, 11),
                PIt(104, "Peacebloom", 1, 40, 10, 7, 9),
                PIt(2001, "Bolt of Linen"), PIt(2002, "Linen Cloak", 2), PIt(2003, "Heavy Cloak", 2), PIt(2004, "Robe of Testing", 3), PIt(2005, "Rare Cape", 3),
                PIt(3001, "Pattern: Robe of Testing", 2, 500, 0, 9, 2), PIt(3002, "Pattern: Rare Cape", 3, 0, 0, 9, 2),
                PIt(2100, "Faction Banner", 2), PIt(2006, "Plain Thing"), PIt(2101, "Spinning Wheel", 2), PIt(3100, "Blueprint: Spinning Wheel", 2, 0, 0, 9, 0),
                PIt(2110, "Basic Campfire Kit"), PIt(2111, "Journeyman Campfire Kit"), PIt(3101, "Blueprint: Journeyman Campfire", 2, 0, 0, 9, 5),
                PIt(4001, "Roast A"), PIt(4002, "Roast B"), PIt(4003, "Roast C"), PIt(4004, "Roast D"), PIt(4005, "Roast E"),
            });
            // Tailoring
            Rank(197, 1900, "Tailoring", "Apprentice", 75); Rank(197, 1901, "Tailoring", "Journeyman", 150);
            Recipe(197, 1001, "Bolt of Linen", 2001, 20, 40, 1, new[] { 100, 2 });
            Recipe(197, 1002, "Linen Cloak", 2002, 30, 60, 0, new[] { 100, 3, 101, 1 });
            Recipe(197, 1003, "Heavy Cloak", 2003, 60, 100, 0, new[] { 102, 4 });
            Recipe(197, 1004, "Robe of Testing", 2004, 60, 90, 0, new[] { 100, 2 });
            Recipe(197, 1005, "Rare Cape", 2005, 60, 90, 0, new[] { 103, 1 });
            Recipe(197, 1006, "Plain Thing", 2006, 0, 0, 0, new[] { 101, 1 }, "", 3);
            // camp objects of Tailoring
            Recipe(197, 1100, "Faction Banner", 2100, 20, 25, 0, new[] { 100, 1, 101, 1 }, "Tier 1");
            Recipe(197, 1101, "Spinning Wheel", 2101, 140, 145, 0, new[] { 103, 2, 100, 5 }, "Tier 2");
            sp.Add(PSp(1200, "Faction Banner", "Tier 1", "Unrolls a banner that allows you and other members of the Horde sitting nearby to gain 32 increased Spirit, mutually exclusive with Divine Spirit.\n\nRequires a Campfire nearby. All camping features share a cooldown of 1 hour.", 900000));
            sp.Add(PSp(1201, "Spinning Wheel", "Tier 2", "Places a Spinning Wheel that allows the creation of certain reagents, and provides all the benefits of a Faction Banner. Requires a Campfire nearby.\n\nMay be placed over a Faction Banner to replace it. All camping features share a cooldown of 1 hour.", 900000));
            s.Effects[2100] = new[] { 0, 1200 }; s.Effects[2101] = new[] { 0, 1201 };
            // recipe items: 3001 teaches through a "Learn Spell" effect of a helper spell, 3002 and the blueprints through trigger 6 (learn spell)
            sp.Add(PSp(5556, "Learn Robe", "", "", 0, PLearn(1004)));
            s.Effects[3001] = new[] { 0, 5556 }; s.Effects[3002] = new[] { 6, 1005 }; s.Effects[3100] = new[] { 0, 5555, 6, 1101 }; s.Effects[3101] = new[] { 6, 1311 };
            sp.Add(PSp(5555, "Learning"));
            // Cooking
            Rank(185, 1910, "Cooking", "Apprentice", 75); Rank(185, 1911, "Cooking", "Journeyman", 150);
            for (int i = 0; i < 5; i++) Recipe(185, 1301 + i, "Roast " + (char)('A' + i), 4001 + i, 20 + i * 20, 60 + i * 20, i == 0 ? 1 : 0, new[] { 103, 1 });
            Recipe(185, 1310, "Basic Campfire", 2110, 1, 5, 1, new[] { 103, 1 }, "Tier 1");
            Recipe(185, 1311, "Journeyman Campfire", 2111, 95, 105, 0, new[] { 103, 2 }, "Tier 2");
            sp.Add(PSp(1210, "Basic Campfire", "Tier 1", "Builds a basic campfire that allows cooking and the placement of up to 3 additional camp features. Players sitting or crafting nearby for 1 min will gain benefits from other camp features.", 900000));
            sp.Add(PSp(1211, "Journeyman Campfire", "Tier 2", "Builds a journeyman campfire that allows cooking and the placement of up to 5 additional camp features. Players sitting or crafting nearby for 1 min will gain benefits from other camp features.", 900000));
            s.Effects[2110] = new[] { 0, 1210 }; s.Effects[2111] = new[] { 0, 1211 };
            // Herbalism / Skinning: ranks only (gathering)
            Rank(182, 1920, "Herbalism", "Apprentice", 75); Rank(182, 1921, "Herbalism", "Journeyman", 150);
            Rank(393, 1930, "Skinning", "Apprentice", 75); Rank(393, 1931, "Skinning", "Journeyman", 150);
            // not professions: a class skill with two reagent spells, a [DNT] test skill, a child line of Tailoring
            Recipe(40, 1400, "Crude Poison", 4001, 1, 20, 0, new[] { 103, 1 }); Recipe(40, 1401, "Deadly Poison", 4002, 20, 40, 0, new[] { 103, 1 });
            Recipe(2933, 1410, "Test Thing", 4003, 1, 20, 0, new[] { 103, 1 });
            var skills = new[]
            {
                new SkillInfo { Id = 197, Name = "Tailoring", Category = 11 }, new SkillInfo { Id = 185, Name = "Cooking", Category = 9 }, new SkillInfo { Id = 182, Name = "Herbalism", Category = 11 },
                new SkillInfo { Id = 393, Name = "Skinning", Category = 11 }, new SkillInfo { Id = 40, Name = "Poisons", Category = 9 }, new SkillInfo { Id = 2933, Name = "Test Profession [DNT]", Category = 11 },
                new SkillInfo { Id = 2948, Name = "Tailoring", Category = 11, ParentId = 197 },
            };
            s.Snap = new SpellSnapshot(sp, skills, ab, rg);
            s.Set = new AtlasSet { Spells = s.Snap, Details = new ItemDetails { Effects = s.Effects } };
            return s;
        }

        static ProfChar PChar(string name, string cls, params object[] stuff)
        {
            var c = new ProfChar { Key = "Realm-" + name, Name = name, Class = cls, Realm = "Realm", Level = 40 };
            for (int i = 0; i < stuff.Length; i++)
            {
                if (stuff[i] is string prof) { c.Skills[prof] = new KeyValuePair<int, int>((int)stuff[i + 1], (int)stuff[i + 2]); i += 2; }
                else if (stuff[i] is int item) { c.Add(item, (int)stuff[i + 1]); i += 1; }
            }
            return c;
        }

        static FixedSources PSources()
        {
            var f = new FixedSources();
            f.Prices[100] = new ItemPrice(100, PriceKind.Vendor); f.Prices[101] = new ItemPrice(50, PriceKind.Vendor); f.Prices[103] = new ItemPrice(20, PriceKind.Vendor);
            f.Prices[102] = new ItemPrice(400, PriceKind.Estimated); f.Prices[3001] = new ItemPrice(500, PriceKind.Vendor);
            f.Lines[100] = new List<SourceLine> { new SourceLine { Kind = "vendor", Text = "Sold by Tess the Weaver", Link = "npc:7", Label = SourceLabels.Classic, UnitPrice = 100 } };
            f.Lines[101] = new List<SourceLine> { new SourceLine { Kind = "vendor", Text = "Sold by Tess the Weaver", Link = "npc:7", Label = SourceLabels.Classic, UnitPrice = 50 } };
            f.Lines[3001] = new List<SourceLine> { new SourceLine { Kind = "vendor", Text = "Sold by Tess the Weaver", Link = "npc:7", Label = SourceLabels.Lodge, UnitPrice = 500 } };
            f.Lines[3002] = new List<SourceLine> { new SourceLine { Kind = "drop", Text = "Dropped by Sir Hard (1 %)", Link = "npc:99", Label = SourceLabels.Classic, Chance = 1 } };
            f.Lines[102] = new List<SourceLine> { new SourceLine { Kind = "drop", Text = "Dropped by Wolf (40 %)", Link = "npc:55", Label = SourceLabels.Classic, Chance = 40 } };
            return f;
        }

        static string GuideLine(GuideStep s) => s.IsMilestone ? s.From + ":train" : SkillGuide.Range(s.From, s.To) + ":" + s.Recipe.Name + "x" + s.Crafts;

        async Task<string> ProfessionsTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            void Check(string what, bool cond, string detail = null) { notes.Add((cond ? "ok   " : "FAIL ") + what + (!cond && detail != null ? " [" + detail + "]" : "")); ok &= cond; }
            var savedSpell = SpellData.CacheFile; var savedAuto = SpellData.AutoStart; var savedLink = ProfessionLinks.Last;
            ProfessionLinks.OpenRequested -= OnProfessionLink;   // the app opens links in the Atlas; this section checks the event itself
            try
            {
                SpellData.ResetForTest(); ItemData.ResetForTest();
                SpellData.AutoStart = false;

                // ---- colours
                Check("bands: the low trivial rank is where yellow starts, high where grey starts, green in the middle (Linen Bandage 30 / 60: orange 1, yellow 30, green 45, grey 60)",
                    RecipeBands.ColorAt(30, 60, 1) == SkillColor.Orange && RecipeBands.ColorAt(30, 60, 29) == SkillColor.Orange && RecipeBands.ColorAt(30, 60, 30) == SkillColor.Yellow && RecipeBands.ColorAt(30, 60, 44) == SkillColor.Yellow
                    && RecipeBands.ColorAt(30, 60, 45) == SkillColor.Green && RecipeBands.ColorAt(30, 60, 59) == SkillColor.Green && RecipeBands.ColorAt(30, 60, 60) == SkillColor.Grey && RecipeBands.ColorAt(30, 60, 300) == SkillColor.Grey);
                Check("bands: no band (0 / 0) = never a skill-up; low = high = orange then grey", RecipeBands.ColorAt(0, 0, 1) == SkillColor.None && RecipeBands.ColorAt(300, 300, 299) == SkillColor.Orange && RecipeBands.ColorAt(300, 300, 300) == SkillColor.Grey);
                Check("bands: crafts per skill point: orange 1, yellow 1.25, green 2.5, grey / none never", RecipeBands.CraftsPerPoint(SkillColor.Orange) == 1.0 && RecipeBands.CraftsPerPoint(SkillColor.Yellow) == 1.25 && RecipeBands.CraftsPerPoint(SkillColor.Green) == 2.5 && double.IsPositiveInfinity(RecipeBands.CraftsPerPoint(SkillColor.Grey)) && double.IsPositiveInfinity(RecipeBands.CraftsPerPoint(SkillColor.None)));
                Check("bands: estimated first skill = low - (high - low), never below 1 (Coyote Steak 90 / 130 -> 50, Linen Bandage 30 / 60 -> 1)", RecipeBands.EstimatedStart(90, 130) == 50 && RecipeBands.EstimatedStart(30, 60) == 1 && RecipeBands.EstimatedStart(5, 100) == 1);

                // ---- the book
                var syn = ProfBuild();
                var book = ProfessionBook.Build(syn.Snap, id => syn.Items.FirstOrDefault(i => i.Id == id), syn.Effects, id => { var sp = syn.Snap.Get(id); return sp == null ? null : new SpellView { Id = id, Name = sp.Name, Rank = sp.Rank, Description = sp.Desc, DurationMs = sp.DurationMs }; });
                Check("professions: Tailoring, Herbalism, Skinning, Cooking only; the class skill, the [DNT] line and the child line are no professions; primary, then gathering, then secondary",
                    string.Join(",", book.Professions.Select(p => p.Name)) == "Tailoring,Herbalism,Skinning,Cooking", string.Join(",", book.Professions.Select(p => p.Name)));
                var tail = book.ByName("tailoring"); var cook = book.ByName("Cooking");
                Check("kinds: Tailoring primary, Herbalism and Skinning gathering, Cooking secondary", tail.Kind == ProfKind.Primary && book.ByName("Herbalism").Gathering && book.ByName("Skinning").Gathering && cook.Kind == ProfKind.Secondary);
                Check("ranks: read from the rank spells' text (Apprentice 75, Journeyman 150), RankFor(80) = Journeyman, max cap 150", string.Join(",", tail.Ranks.Select(r => r.Name + r.Cap)) == "Apprentice75,Journeyman150" && tail.RankFor(80).Name == "Journeyman" && tail.RankFor(10).Name == "Apprentice" && tail.MaxCap == 150);
                Check("recipes in skill order (by the low trivial rank, then the high one, then the name), the one without a band last",
                    string.Join(",", tail.Recipes.Select(r => r.Name)) == "Faction Banner,Bolt of Linen,Linen Cloak,Rare Cape,Robe of Testing,Heavy Cloak,Spinning Wheel,Plain Thing", string.Join(",", tail.Recipes.Select(r => r.Name)));
                var bolt = tail.Recipes.First(r => r.Name == "Bolt of Linen"); var robe = tail.Recipes.First(r => r.Name == "Robe of Testing"); var rare = tail.Recipes.First(r => r.Name == "Rare Cape"); var heavy = tail.Recipes.First(r => r.Name == "Heavy Cloak"); var plain = tail.Recipes.First(r => r.Name == "Plain Thing");
                Check("learn kinds: taught with the profession (acquire 1), trainer, recipe item (both through a Learn-Spell effect and through trigger 6)", bolt.Learn == LearnKind.Starter && heavy.Learn == LearnKind.Trainer && robe.Learn == LearnKind.RecipeItem && robe.TeachingItems.SequenceEqual(new[] { 3001 }) && rare.TeachingItems.SequenceEqual(new[] { 3002 }));
                Check("recipe numbers: trivial ranks, estimated start, bands, output count (3 for the thing that makes 3), reagent list", bolt.Low == 20 && bolt.High == 40 && bolt.Yellow == 20 && bolt.Green == 30 && bolt.Grey == 40 && heavy.Start == 20 && robe.Start == 30 && plain.OutCount == 3 && heavy.Reagents.Length == 1 && heavy.Reagents[0].ItemId == 102 && heavy.Reagents[0].Count == 4);
                Check("recipes by product: Linen Cloak is made by exactly one recipe", book.Producing(2002).Count == 1 && book.Producing(2002)[0].SpellId == 1002);
                Check("camp recipes: [Tier n] marks them (Faction Banner 1, Spinning Wheel 2, the kits 1 and 2), plain recipes are not", tail.Recipes.Count(r => r.IsCamp) == 2 && cook.Recipes.Count(r => r.IsCamp) == 2 && !bolt.IsCamp && book.BySpell[1101].Tier == 2);

                // ---- camping
                var camp = book.Camp;
                Check("camp: two kits found from their own text (capacity 3 and 5, 15 min fire, 1 minute to apply), sorted by capacity",
                    camp.Kits.Count == 2 && camp.Kits[0].Capacity == 3 && camp.Kits[1].Capacity == 5 && camp.Kits[0].FireMs == 900000 && camp.Kits[0].BenefitMinutes == 1 && camp.Kits[0].Name == "Basic Campfire Kit" && camp.Kits[0].Profession == "Cooking");
                Check("camp: the Journeyman kit needs its Blueprint (item 3101, taught through trigger 6) from skill 95; the Basic kit has none", camp.Kits[1].BlueprintItemId == 3101 && camp.Kits[1].Needs == 95 && camp.Kits[0].BlueprintItemId == 0 && !camp.Kits[0].NeedsBlueprint);
                Check("camp: two objects (Tailoring), the Faction Banner with side, buff, class buff it does not stack with, from skill 20; the shared cooldown is read from the text",
                    camp.Objects.Count == 2 && camp.Objects[0].Name == "Faction Banner" && camp.Objects[0].Side == "Horde" && camp.Objects[0].Buff == "32 increased Spirit" && camp.Objects[0].ExclusiveWith == "Divine Spirit" && camp.Objects[0].Needs == 20 && camp.CooldownText == "1 hour", camp.Objects[0].Buff + "|" + camp.Objects[0].ExclusiveWith + "|" + camp.CooldownText);
                Check("camp: the Spinning Wheel replaces the Faction Banner, needs Blueprint 3100 (trigger 6 after a 'Learning' trigger 0), made from skill 140, use text without the trailer",
                    camp.Objects[1].Upgrades == "Faction Banner" && camp.Objects[1].BlueprintItemId == 3100 && camp.Objects[1].Needs == 140 && !camp.Objects[1].UseText.Contains("Requires a") && camp.Objects[1].Trailer.StartsWith("Requires a"), camp.Objects[1].UseText);
                Check("camp: grouped by profession", camp.ByProfession().Count() == 1 && camp.ByProfession().First().Key == "Tailoring");

                // ---- craft now
                var alice = PChar("Alice", "MAGE", "Tailoring", 20, 75, "Cooking", 60, 75, 100, 30, 101, 2);
                var bob = PChar("Bob", "WARRIOR", "Tailoring", 80, 150, 100, 5, 102, 8, 103, 3);
                var chars = new List<ProfChar> { alice, bob };
                var pooled = Holdings.Pool(chars);
                Check("pool: items of all characters added (Linen Cloth 35, Wool 8)", pooled[100] == 35 && pooled[102] == 8 && pooled[103] == 3 && pooled[101] == 2);
                Check("times: Bolt of Linen 17 (2 each from 35), Linen Cloak 2 (limited by the thread), none for a recipe without reagents", CraftNow.Times(bolt, pooled) == 17 && CraftNow.Times(tail.Recipes.First(r => r.Name == "Linen Cloak"), pooled) == 2 && CraftNow.Times(new ProfRecipe(), pooled) == 0);
                var rows = CraftNow.Compute(book, chars);
                var byName = rows.ToDictionary(r => r.Recipe.Name + (r.Recipe.IsCamp ? "*" : ""), r => r);
                Check("craft now (all characters together): Bolt of Linen x17, Linen Cloak x2, Heavy Cloak x2, Rare Cape x3, Robe of Testing x17, camp Faction Banner x2; Plain Thing x2 (thread); the Journeyman Campfire needs 2 wood x1 per craft...",
                    byName["Bolt of Linen"].Times == 17 && byName["Linen Cloak"].Times == 2 && byName["Heavy Cloak"].Times == 2 && byName["Rare Cape"].Times == 3 && byName["Robe of Testing"].Times == 17 && byName["Faction Banner*"].Times == 2, string.Join(", ", rows.Select(r => r.ToString())));
                Check("craft now: Robe of Testing needs skill 30 (estimated): only Bob can make it (Alice has 20); the best crafter of Bolt of Linen is Bob (80), who sees it grey",
                    byName["Robe of Testing"].Crafters.Select(c => c.Name).SequenceEqual(new[] { "Bob" }) && byName["Bolt of Linen"].Best.Name == "Bob" && byName["Bolt of Linen"].Color == SkillColor.Grey && byName["Robe of Testing"].Color == SkillColor.Green);
                Check("craft now: Cooking is Alice's: Roast A..C etc. with Simple Wood from Bob (wood is on another character: nobody holds all alone for Roast A), the sort puts skill-ups first",
                    byName["Roast A"].Times == 3 && byName["Roast A"].SingleHolder?.Name == "Bob" && rows.TakeWhile(r => r.Color != SkillColor.Grey).All(r => r.Color == SkillColor.Orange || r.Color == SkillColor.Yellow || r.Color == SkillColor.Green), string.Join(", ", rows.Select(r => r.Recipe.Name + ":" + r.Color)));
                var aliceOnly = CraftNow.Compute(book, chars, alice);
                Check("craft now (Alice only): her items only (Linen Cloth 30, thread 2): Bolt of Linen x15, Linen Cloak x2; nothing needing wool; her crafters list is just her", aliceOnly.First(r => r.Recipe.Name == "Bolt of Linen").Times == 15 && aliceOnly.All(r => r.Recipe.Name != "Heavy Cloak") && aliceOnly.All(r => r.Crafters.All(c => c == alice)));
                Check("craft now: a profession nobody has gives no rows when a crafter is required, rows with 'nobody' when not", CraftNow.Compute(book, new List<ProfChar> { PChar("Cy", "ROGUE", 103, 5) }).Count == 0 && CraftNow.Compute(book, new List<ProfChar> { PChar("Cy", "ROGUE", 103, 5) }, null, false).Any(r => r.Crafters.Count == 0));

                // ---- the leveling guide
                var src = PSources();
                var g = SkillGuideBuilder.Build(book, tail, 1, 100, src, null);
                var gl = g.Steps.Select(GuideLine).ToList();
                Check("guide 1-100: trainer first, Bolt of Linen 1-30 (19 orange + 10 yellow x 1.25 = 32 crafts), Robe of Testing 30-75, then the cap, then Heavy Cloak 75-80, Robe 80-90, Heavy Cloak 90-100",
                    string.Join(" | ", gl) == "1:train | 1–30:Bolt of Linenx32 | 30–75:Robe of Testingx49 | 75:train | 75–80:Heavy Cloakx7 | 80–90:Robe of Testingx25 | 90–100:Heavy Cloakx25", string.Join(" | ", gl));
                Check("guide: the camp objects (Faction Banner is cheap and orange at the start) and the hard-to-get Rare Cape are not used; Plain Thing (no band) never", g.Steps.All(s => s.IsMilestone || (!s.Recipe.IsCamp && s.Recipe.Name != "Rare Cape" && s.Recipe.Name != "Plain Thing")) && g.Reached == 100);
                Check("guide: milestones: the first rank is learned from the trainer at 1, the next one at the 75 cap (Journeyman, up to 150)", g.Steps[0].IsMilestone && g.Steps[0].Train.Name == "Apprentice" && g.Steps[3].IsMilestone && g.Steps[3].From == 75 && g.Steps[3].Train.Name == "Journeyman" && g.Steps[3].TrainText.Contains("150"));
                Check("guide: colours per step (Bolt orange to yellow, Robe orange to yellow, Heavy Cloak yellow, then green), the Robe's recipe item is bought from a vendor, the Bolt is taught with the profession",
                    g.Steps[1].StartColor == SkillColor.Orange && g.Steps[1].EndColor == SkillColor.Yellow && g.Steps[2].EndColor == SkillColor.Yellow && g.Steps[4].StartColor == SkillColor.Yellow && g.Steps[6].StartColor == SkillColor.Green
                    && g.Steps[2].LearnItemId == 3001 && g.Steps[1].Learn.StartsWith("known from the start") && g.Steps[2].Learn.Contains("Pattern: Robe of Testing") && g.Steps[2].Learn.Contains("Tess the Weaver") && g.Steps[2].Learn.Contains(SourceLabels.Lodge));
                Check("guide: materials per step = crafts x reagents (32 x 2 Linen Cloth; 7 x 4 Wool) and cost with the prices (32 x 200 = 6400c for step 1), running total grows",
                    g.Steps[1].Materials.Single().Count == 64 && g.Steps[4].Materials.Single().Count == 28 && g.Steps[1].Cost == 6400 && g.Steps[1].RunningCost == 6400 && g.Steps[2].RunningCost == 6400 + 49 * 200 + 500 && g.Steps[6].RunningCost > g.Steps[4].RunningCost && !g.Steps[1].CostApprox && g.Steps[4].CostApprox);
                var ship = g.Shopping.ToDictionary(l => l.ItemId + (l.IsRecipe ? "r" : ""), l => l);
                Check("guide shopping list (nothing in stock): Linen Cloth 212 (64 + 98 + 50), Wool 128 (28 + 100), the recipe item once; total 7g 29s = 72,900c, approximate (the wool is an estimate)",
                    ship["100"].Need == 212 && ship["102"].Need == 128 && ship["3001r"].Need == 1 && g.TotalCost == 72900 && g.TotalApprox && g.UnknownPrices == 0, ship["100"].Need + " " + ship["102"].Need + " " + g.TotalCost);
                Check("guide shopping: each line says where (cheapest known place, labelled): Linen Cloth from Tess (Classic baseline), Wool: dropped by Wolf; the recipe item: Lodge", ship["100"].Where.Contains("Tess") && ship["100"].Label == SourceLabels.Classic && ship["102"].Where.Contains("Wolf") && ship["3001r"].Label == SourceLabels.Lodge);
                var withStock = SkillGuideBuilder.Build(book, tail, 1, 100, src, pooled, "Bob");
                var ws = withStock.Shopping.ToDictionary(l => l.ItemId + (l.IsRecipe ? "r" : ""), l => l);
                Check("guide minus stock (35 Linen Cloth, 8 Wool): 177 and 120 still to get, total 66,200c; the step list does not change", ws["100"].Have == 35 && ws["100"].ToGet == 177 && ws["102"].ToGet == 120 && withStock.TotalCost == 66200 && withStock.Steps.Select(GuideLine).SequenceEqual(gl) && withStock.Character == "Bob");
                var from75 = SkillGuideBuilder.Build(book, tail, 75, 100, src, null);
                Check("guide from the current skill 75: starts with the Journeyman training (the cap), no first-rank line, three crafting steps", from75.Steps[0].IsMilestone && from75.Steps[0].From == 75 && from75.Steps.Count(s => s.IsMilestone) == 1 && string.Join(" | ", from75.Steps.Select(GuideLine)) == "75:train | 75–80:Heavy Cloakx7 | 80–90:Robe of Testingx25 | 90–100:Heavy Cloakx25");
                var withCamp = SkillGuideBuilder.Build(book, tail, 1, 100, src, null, null, false, true);
                Check("guide with camp objects included: the Faction Banner (made from about skill 15: its 20 / 25 band; 150c a craft against the Bolt's 200c) takes over at 15; without the option no camp object is ever used", withCamp.Crafting.First().Recipe.Name == "Bolt of Linen" && withCamp.Crafting.Skip(1).First().Recipe.Name == "Faction Banner" && withCamp.Crafting.Skip(1).First().From == 15 && g.Crafting.All(s => !s.Recipe.IsCamp), string.Join(" | ", withCamp.Steps.Select(GuideLine)));
                var hardOnly = SkillGuideBuilder.Build(book, cook, 1, 20, src, null);
                Check("guide Cooking 1-20: the starter Roast A (20 / 60 band, taught with the profession) is orange from 1; the Basic Campfire kit is no skill-up recipe for the guide", hardOnly.Steps.Where(s => !s.IsMilestone).All(s => s.Recipe.Name == "Roast A") && hardOnly.Reached == 20);
                var noRecipe = SkillGuideBuilder.Build(book, tail, 140, 150, src, null);
                Check("guide stops with a note when nothing gives a skill-up any more (everything is grey at 140)", noRecipe.Reached == 140 && noRecipe.Notes.Any(n => n.StartsWith("No recipe of Tailoring gives a skill-up at 140")));
                var txt = g.ToText();
                Check("guide text: heading, shopping list with the total, the steps in the '1-30: Bolt of Linen x32' form, the trainer lines, why-lines and the notes (the copy / file text)",
                    txt.StartsWith("Tailoring 1–100") && txt.Contains("Shopping list") && txt.Contains("Total about ~7g 29s") && txt.Contains("1–30: Bolt of Linen x32") && txt.Contains("75: Skill 75 is the cap: train Journeyman Tailoring") && txt.Contains("Why: cheapest of") && txt.Contains("Learn: recipe item Pattern: Robe of Testing"), txt.Substring(0, Math.Min(400, txt.Length)));
                Check("guide for a gathering profession is a note, not a recipe path", SkillGuideBuilder.Build(book, book.ByName("Herbalism"), 1, 100, src, null).Steps.Count == 0);

                // ---- the gathering guide
                var world = new FixedGatherWorld();
                world.Zones[1] = new ZoneInfo { Id = 1, Name = "Elwynn Forest", Median = 7, MinLevel = 1, MaxLevel = 10 };
                world.Zones[2] = new ZoneInfo { Id = 2, Name = "Westfall", Median = 12, MinLevel = 8, MaxLevel = 16 };
                world.Zones[3] = new ZoneInfo { Id = 3, Name = "Duskwood", Median = 20, MinLevel = 18, MaxLevel = 24 };
                world.ByProfession["Herbalism"] = new List<GatherEntity>
                {
                    new GatherEntity { Kind = "obj", Id = 1618, Name = "Peacebloom", Items = { 104 }, ClassicZones = { [1] = 12, [2] = 3 } },
                    new GatherEntity { Kind = "obj", Id = 1617, Name = "Silverleaf", Items = { 104 }, ClassicZones = { [1] = 8 } },
                    new GatherEntity { Kind = "obj", Id = 1620, Name = "Mageroyal", Items = { 104 }, LodgeZones = { [3] = 40 } },
                };
                world.ByProfession["Skinning"] = new List<GatherEntity>
                {
                    new GatherEntity { Kind = "npc", Id = 1, Name = "Young Wolf", Level = 8, ClassicZones = { [1] = 20 } },
                    new GatherEntity { Kind = "npc", Id = 2, Name = "Mountain Lion", Level = 15, ClassicZones = { [2] = 9 } },
                    new GatherEntity { Kind = "npc", Id = 3, Name = "Dire Wolf", Level = 30, ClassicZones = { [3] = 5 }, LodgeZones = { [3] = 2 } },
                };
                var gg = GatherGuideBuilder.Build(book, book.ByName("Herbalism"), 1, 150, world);
                Check("gather: skill estimates: a node in a zone of median level 7 wants about 35, level 12 about 60, capped at the profession's cap; skinning: level 8 = 1, 15 = 50, 20 = 100, 30 = 150",
                    GatherGuideBuilder.NodeSkill(7, 150) == 35 && GatherGuideBuilder.NodeSkill(12, 150) == 60 && GatherGuideBuilder.NodeSkill(40, 150) == 150 && GatherGuideBuilder.SkinSkill(8) == 1 && GatherGuideBuilder.SkinSkill(15) == 50 && GatherGuideBuilder.SkinSkill(20) == 100 && GatherGuideBuilder.SkinSkill(30) == 150);
                Check("gather Herbalism: brackets of 50 up to the cap 150 (1-50, 51-100, 101-150)", gg.Brackets.Count == 3 && gg.Brackets[0].From == 1 && gg.Brackets[0].To == 50 && gg.Brackets[1].From == 51 && gg.Brackets[1].To == 100 && gg.Brackets[2].To == 150, string.Join(",", gg.Brackets.Select(b => b.From + "-" + b.To)));
                var b1 = gg.Brackets[0]; var b2 = gg.Brackets[1];
                Check("gather: 1-50 has Elwynn Forest (level 1-10) with Peacebloom first (12) then Silverleaf (8), labelled Classic baseline", b1.Zones.Count == 1 && b1.Zones[0].Zone.Name == "Elwynn Forest" && b1.Zones[0].Finds.Select(f => f.Name).SequenceEqual(new[] { "Peacebloom", "Silverleaf" }) && b1.Zones[0].Label == SourceLabels.Classic && b1.Zones[0].Finds[0].Count == 12 && b1.Zones[0].Finds[0].EstSkill == 35);
                Check("gather: 51-100 has Duskwood (only the Lodge saw Mageroyal, labelled) before Westfall (3 Classic spawns, Peacebloom at ~60)", b2.Zones.Select(z => z.Zone.Name).SequenceEqual(new[] { "Duskwood", "Westfall" }) && b2.Zones[0].Label == SourceLabels.Lodge && b2.Zones[1].Finds[0].Name == "Peacebloom" && b2.Zones[1].Finds[0].EstSkill == 60);
                var sk = GatherGuideBuilder.Build(book, book.ByName("Skinning"), 1, 150, world);
                Check("gather Skinning: Young Wolf (level 8, skill 1) in the first bracket, Mountain Lion (level 15, skill 50) too (50 is the last point of 1-50), Dire Wolf (skill 150) in the last, with both labels where the Lodge saw it",
                    sk.Brackets[0].Zones.Select(z => z.Zone.Name).OrderBy(x => x).SequenceEqual(new[] { "Elwynn Forest", "Westfall" }) && sk.Brackets[2].Zones.Count == 1 && sk.Brackets[2].Zones[0].Finds[0].Name == "Dire Wolf" && sk.Brackets[2].Zones[0].Label == SourceLabels.Classic + " + " + SourceLabels.Lodge, string.Join(";", sk.Brackets.Select(b => string.Join(",", b.Zones.Select(z => z.Zone.Name)))));
                var gtxt = gg.ToText();
                Check("gather text: bracket heading, zone with level range and label, finds with counts, the estimate note", gtxt.Contains("1–50:") && gtxt.Contains("Elwynn Forest (level 1–10)") && gtxt.Contains("Peacebloom") && gtxt.Contains("estimated"));
                var noWorld = GatherGuideBuilder.Build(book, book.ByName("Mining") ?? book.ByName("Herbalism"), 1, 100, new FixedGatherWorld { Available = false, Missing = "The Classic baseline is not downloaded yet." });
                Check("gather without the data: says what is missing, no brackets", noWorld.Unavailable == "The Classic baseline is not downloaded yet." && noWorld.Brackets.Count == 0 && noWorld.ToText().Contains("not downloaded"));

                // ---- sources
                Check("sources: vendors first (cheapest known price first), then the likeliest loot, the Lodge line before the Classic one at equal rank; max respected",
                    new FixedSources { Lines = { [1] = new List<SourceLine> { new SourceLine { Kind = "drop", Text = "d", Chance = 5, Label = "x" }, new SourceLine { Kind = "vendor", Text = "v2", UnitPrice = 200 }, new SourceLine { Kind = "vendor", Text = "v1", UnitPrice = 100 }, new SourceLine { Kind = "drop", Text = "d2", Chance = 50 } } } }.SourcesOf(1, 3).Select(s => s.Text).SequenceEqual(new[] { "v1", "v2", "d2" }));
                Check("money: 1g 20s 5c, 0c, 99c, 10,000c = 1g, 250,000c = 25g", ProfMoney.Format(12005) == "1g 20s 5c" && ProfMoney.Format(0) == "0c" && ProfMoney.Format(99) == "99c" && ProfMoney.Format(10000) == "1g" && ProfMoney.Format(250000) == "25g");

                // ---- links
                string got = null; Action<string> h = id => got = id;
                ProfessionLinks.OpenRequested += h;
                ProfessionLinks.Open("item:2001"); ProfessionLinks.Open("");
                ProfessionLinks.OpenRequested -= h;
                Check("links: Open raises OpenRequested with the Atlas id (item:2001), an empty id does nothing; the id scheme parses", got == "item:2001" && ProfessionLinks.Last == "item:2001" && AtlasIds.TryParse("npc:7", out var kind, out var nid) && kind == "npc" && nid == 7);

                // ---- the page
                await ProfessionsPageChecks(dir, syn, Check);

                // ---- optional: the real game files, read-only, with screenshots
                await ProfRealChecks(dir, Check, notes);
            }
            catch (Exception ex) { ok = false; notes.Add("FAIL exception: " + ex); }
            finally
            {
                ItemDetails.Current = null;
                SpellData.ResetForTest(); ItemData.ResetForTest();
                SpellData.CacheFile = savedSpell; SpellData.AutoStart = savedAuto;
                ProfessionLinks.OpenRequested -= OnProfessionLink; ProfessionLinks.OpenRequested += OnProfessionLink;
                ProfessionsView.ResetForTest();
                ProfessionLinks.Last = savedLink;
                ShowTab("addons"); ResetAtlasForTest();
            }
            notes.Insert(0, "professions (Atlas layer H): " + (ok ? "all ok" : "FAILURES"));
            return string.Join("\r\n", notes);
        }

        // the page, on the synthetic book
        async Task ProfessionsPageChecks(string dir, ProfSynth syn, CheckFn Check)
        {
            SpellData.SetForTest(syn.Set);
            ItemData.SetForTest(new ItemSnapshot(syn.Items, null), ItemData.State.Ready);
            var page = ProfessionsView;
            page.ResetForTest();
            var alice = PChar("Alice", "MAGE", "Tailoring", 20, 75, "Cooking", 60, 75, 100, 30, 101, 2);
            var bob = PChar("Bob", "WARRIOR", "Tailoring", 80, 150, 100, 5, 102, 8, 103, 3);
            page.SetProfCharsForTest(new[] { alice, bob });
            page.SetSourcesForTest(PSources(), new FixedGatherWorld());
            ShowTab("professions");
            await Task.Delay(100);
            Check("page: ShowTab opens it inside the Atlas (hosted category); the book has the 4 professions and 2 characters", TabAtlas.IsChecked == true && AtlasView.IsHosted && AtlasView.CurrentId.StartsWith("professions:") && page.IsVisible && page.BookForTest.Professions.Count == 4 && page.CharsForTest.Count == 2);

            page.ShowForTest("recipes", "Tailoring", "Bob");
            var names = page.RecipeRowNamesForTest();
            Check("recipes tab: all 8 recipes of Tailoring in skill order, the one without a band last", names.Count == 8 && names.First() == "Faction Banner" && names.Last() == "Plain Thing", string.Join(",", names));
            var texts = page.BodyTextsForTest();
            Check("recipes tab: coloured for Bob at skill 80 (Bolt of Linen Grey, Heavy Cloak Green), the learn line says trainer / recipe item with its vendor and label, the 'cannot see learned recipes' note",
                texts.Contains("Grey") && texts.Contains("Green") && texts.Any(t => t.Contains("Trainer (no recipe item")) && texts.Any(t => t.Contains("Sold by Tess the Weaver")) && texts.Contains(" (" + SourceLabels.Lodge + ")") && texts.Any(t => t.Contains("cannot see which recipes a character has already learned")));
            Check("recipes tab: the header says whose colours these are", texts.Any(t => t.Contains("colours for Bob at skill 80/150")));
            Check("recipes tab: ranks line from the data (Apprentice to 75, Journeyman to 150)", texts.Any(t => t.StartsWith("Ranks: Apprentice (to 75), Journeyman (to 150)")));
            page.SearchForTest("cape");
            Check("search: matches the name (Heavy Cloak and Rare Cape for 'cape'? only Rare Cape) and the reagent names (wool -> Heavy Cloak)", string.Join(",", page.RecipeRowNamesForTest()) == "Rare Cape");
            page.SearchForTest("wool");
            Check("search: finds recipes by a reagent's name", string.Join(",", page.RecipeRowNamesForTest()) == "Heavy Cloak");
            page.SearchForTest("");
            page.FilterForTest("now");
            Check("filter 'can make now' (Bob 80): everything whose estimated start is reached and that is not grey: Rare Cape, Robe of Testing, Heavy Cloak (green) and Plain Thing (no band, so not grey); not the grey Faction Banner, Bolt of Linen, Linen Cloak, nor the Spinning Wheel (start 135 not reached)",
                string.Join(",", page.RecipeRowNamesForTest()) == "Rare Cape,Robe of Testing,Heavy Cloak,Plain Thing", string.Join(",", page.RecipeRowNamesForTest()));
            page.FilterForTest("later");
            Check("filter 'later': only the Spinning Wheel (start 140 > 80)", string.Join(",", page.RecipeRowNamesForTest()) == "Spinning Wheel", string.Join(",", page.RecipeRowNamesForTest()));
            page.FilterForTest("all");
            var tip = page.RecipeTipSheetForTest(1004);
            Check("recipe tooltip: the made item, the reagents with 'you have' across characters and prices, bands, the estimated start, spell and item id",
                tip != null && Flatten(tip).Any(t => t == "Robe of Testing") && Flatten(tip).Any(t => t.Contains("2 x Linen Cloth") && t.Contains("you have 35 across characters")) && Flatten(tip).Any(t => t.Contains("yellow from 60, green 75, grey 90")) && Flatten(tip).Any(t => t == "Spell ID 1004 - Item ID 2004"));

            // a clickable link raises the Atlas hook
            string opened = null; Action<string> hook = id => opened = id;
            ProfessionLinks.OpenRequested += hook;
            var link = FindText(page.BodyForTest, t => t.Text == "Robe of Testing" && t.Cursor == Cursors.Hand);
            if (link != null) link.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
            ProfessionLinks.OpenRequested -= hook;
            Check("links on the page: clicking an item name raises the Atlas hook with item:2004", opened == "item:2004", opened);

            // guide tab
            page.ShowForTest("guide", "Tailoring", "Bob");
            page.GuideRangeForTest(1, 100, "all");
            var pg = page.GuideForTest;
            Check("guide tab: the guide of the page is the generated one for Bob, 1-100, minus all characters' items (total 66,200c)", pg != null && pg.Character == "Bob" && pg.Start == 1 && pg.Target == 100 && pg.TotalCost == 66200, pg?.TotalCost.ToString());
            var gt = page.BodyTextsForTest();
            Check("guide tab: summary, shopping list (Linen Cloth 177 to get, have 35), steps '1-30', the trainer line, copy text available", gt.Any(t => t.StartsWith("Still to buy or find:")) && gt.Any(t => t.StartsWith("177 x Linen Cloth")) && gt.Contains("1–30") && gt.Any(t => t.Contains("train Journeyman Tailoring")) && page.GuideTextForTest().Contains("1–30: Bolt of Linen x32"));
            page.GuideRangeForTest(null, 100, "none");
            Check("guide tab: 'subtract nothing' gives the full list (212 Linen Cloth, 72,900c); start defaults to Bob's skill 80 (so no first steps)", page.GuideForTest.Start == 80 && page.GuideForTest.Steps.First(s => !s.IsMilestone).From == 80, page.GuideForTest.Start + "");
            page.GuideRangeForTest(1, 100, "none");
            Check("guide tab: start 1 with 'subtract nothing' = 72,900c", page.GuideForTest.TotalCost == 72900);
            page.ShowForTest("guide", "Herbalism", "Bob");
            page.GuideRangeForTest(null, null, "all");
            var gat = await page.WaitGatherForTest();
            Check("guide tab for a gathering profession (Herbalism): the zone guide, built off the UI thread, 3 brackets up to the cap 150, none with a zone because this world has no places; no crafting steps", gat != null && page.GuideForTest == null && gat.Brackets.Count == 3 && gat.Brackets.All(b => b.Zones.Count == 0) && page.BodyTextsForTest().Any(t => t == "Skill 1–50"), gat?.Brackets.Count + " " + (page.GuideForTest == null) + " " + string.Join("|", page.BodyTextsForTest().Where(t => t.StartsWith("Skill")).Take(4)));

            // craft tab
            page.ShowForTest("craft");
            var cr = page.CraftRowsForTest();
            Check("craft tab: rows 'Name xN Colour' for all characters together (Roast A x3, Heavy Cloak x2 ...)", cr.Any(r => r.StartsWith("Roast A x3")) && cr.Any(r => r.StartsWith("Heavy Cloak x2")), string.Join(" | ", cr));
            page.ShowForTest("craft", null, "Alice");
            Check("craft tab for Alice only: her items only (no Heavy Cloak)", !page.CraftRowsForTest().Any(r => r.StartsWith("Heavy Cloak")) && page.CraftRowsForTest().Any(r => r.StartsWith("Bolt of Linen x15")));

            // camp tab
            page.ShowForTest("camp");
            var cards = page.CampCardsForTest();
            Check("camp tab: both kits and both objects as cards, the rules (capacities from the data, the 15 minute fire, the shared cooldown) and the 'from guides' marks",
                cards.SequenceEqual(new[] { "Basic Campfire Kit", "Journeyman Campfire Kit", "Faction Banner", "Spinning Wheel" }) && page.BodyTextsForTest().Any(t => t.StartsWith("A campfire kit takes 3, 5 camp objects")) && page.BodyTextsForTest().Any(t => t == "A fire burns 15 minutes.") && page.BodyTextsForTest().Any(t => t.Contains("cooldown of 1 hour")) && page.BodyTextsForTest().Any(t => t.StartsWith("From guides")), string.Join(",", cards));
            var ct = page.BodyTextsForTest();
            Check("camp tab: who can make what: Alice (Tailoring 20, cloth and thread in her bags) can make the Faction Banner now; Bob (80) lacks the thread; neither reaches the Spinning Wheel's skill 140",
                ct.Contains(" 20 - can make it now") && ct.Contains(" 80 - needs reagents") && ct.Count(t => t == " 80 - needs skill 140") == 1 && ct.Contains(" 20 - needs skill 140"), string.Join(" | ", ct.Where(t => t.Contains(" - "))));
            // the page when there is no game data
            SpellData.SetForTest(null, SpellData.State.Unavailable, "off");
            page.ShowForTest("recipes");
            Check("no game data: an empty state that says what to switch on", page.BodyTextsForTest().Any(t => t.StartsWith("No game data yet")) && page.BodyTextsForTest().Any(t => t.Contains("Read item data from my WoW installation")));
            SpellData.SetForTest(syn.Set);
            page.ResetForTest();
            await Task.Delay(10);
        }

        static List<string> Flatten(FrameworkElement fe)
        {
            var res = new List<string>();
            void Walk(DependencyObject o)
            {
                if (o is TextBlock tb) { res.Add(tb.Text); return; }
                foreach (var ch in LogicalTreeHelper.GetChildren(o).OfType<DependencyObject>()) Walk(ch);
            }
            Walk(fe);
            return res;
        }

        static TextBlock FindText(DependencyObject root, Func<TextBlock, bool> pred)
        {
            if (root is TextBlock tb) return pred(tb) ? tb : null;
            foreach (var ch in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()) { var r = FindText(ch, pred); if (r != null) return r; }
            return null;
        }
    }
}
