using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Professions;
using ElansAddonHub.Services.Upgrades;

namespace ElansAddonHub
{
    // --selftest "upgrades", part 1b: enchants (applicability, the enchant worn now from the item link, score and words, who can apply it, how it is learned, reagents with what
    // I own, the filters, missing data) and food & drink, on the invented world.
    public partial class MainWindow
    {
        void UpgradesExtrasChecks(CheckFn Check, List<InvChar> chars, Func<string, InvChar> C, HubModel hub, long now)
        {
            var items = UpItems();
            ClientItem It(int id) => items.Get(id);
            var f0 = new UpgradeFilters();
            var es = EnchantData.Set;
            UpgradeSlotResult Slot(List<UpgradeSlotResult> l, int s) => l.First(x => x.Slot == s);
            var w = UpgradeWeights.Effective("HUNTER", 0);

            // ---------------------------------------------------------------------------------------------------------------- the enchant of a link
            Check("link: the enchant is the second number of the item link ('Hitem:110:1002:...' = 1002); none, a gem only, an empty field, garbage and null = 0",
                UpgradeChar.EnchantOfLink("|cffffffff|Hitem:110:1002::::::::|h[Padded Vest]|h|r") == 1002 && UpgradeChar.EnchantOfLink("|cffffffff|Hitem:110::::::::|h[Padded Vest]|h|r") == 0 && UpgradeChar.EnchantOfLink("|Hitem:110:0:5:::::::|h") == 0
                && UpgradeChar.EnchantOfLink("|Hitem:110:abc::|h") == 0 && UpgradeChar.EnchantOfLink("garbage") == 0 && UpgradeChar.EnchantOfLink(null) == 0 && UpgradeChar.EnchantOfLink("") == 0);
            var enyaLua = "ElansHubData = {\n['schema']=1,\n['chars']={\n['Forever-Enya']={at=" + (now - 60) + ",mods={\n"
                + "['core']={v=1,at=" + (now - 60) + ",by='ElansHub',ver='1.14.0',data={name='Enya',realm='Forever',class='Hunter',classFile='HUNTER',race='Night Elf',raceFile='NightElf',sex=3,faction='Alliance',level=30,xp=1,xpMax=2,rested=0,money=1,guild='',zone='Z',subzone='S',updated=" + (now - 60) + "}},\n"
                + "['gear']={v=1,at=" + (now - 60) + ",by='ElansHub',ver='1.14.0',data={at=" + (now - 60) + ",slots={[1]={i=100,c=1,q=1,ic=0,il=10},[5]={i=110,c=1,q=1,ic=0,il=15},[8]={i=114,c=1,q=1,ic=0,il=12},[15]={i=125,c=1,q=2,ic=0,il=18}},"
                + "links={[100]=\"|cffffffff|Hitem:100::::::::|h[Frayed Cap]|h|r\",[110]=\"|cffffffff|Hitem:110:1002::::::::|h[Padded Vest]|h|r\",[114]=\"|cffffffff|Hitem:114::::::::|h[Soft Boots]|h|r\",[125]=\"|cff1eff00|Hitem:125:1003::::::::|h[Cloak of Shadows]|h|r\"}}},\n"
                + "}}},\n['account']={['mods']={}}}\n";
            var enyaHub = HubData.Parse(enyaLua);
            var enyaInv = UpInvChars(enyaHub).Single();
            var enya = UpgradeChar.From(enyaInv);
            Check("link: the Hub's gear links give the worn enchants by slot - Strength +3 on the chest (1002), Stamina +3 on the cloak (1003), the boots and the cap none; they are part of the cache key",
                enya.Enchants.Count == 2 && enya.Enchants[5] == 1002 && enya.Enchants[15] == 1003 && !enya.Enchants.ContainsKey(8) && !enya.Enchants.ContainsKey(1) && enya.Worn.Count == 4 && enya.Fingerprint.Contains("5e1002"), string.Join(",", enya.Enchants));

            // ---------------------------------------------------------------------------------------------------------------- what applies where
            Check("applicability: a chest enchant fits a chest piece and a robe, not a head piece; a boots enchant only boots; a weapon enchant melee weapons (a dagger) and not a bow; the scope a bow and not a dagger; the armor kit head, chest, legs, feet and not shoulders",
                es.Applies(7001, It(110)) && es.Applies(7001, new ClientItem { ClassId = 4, SubclassId = 1, InventoryType = 20 }) && !es.Applies(7001, It(100)) && es.Applies(7003, It(114)) && !es.Applies(7003, It(110))
                && es.Applies(7004, It(140)) && !es.Applies(7004, It(150)) && es.Applies(7102, It(150)) && !es.Applies(7102, It(140)) && es.Applies(7101, It(100)) && es.Applies(7101, It(110)) && es.Applies(7101, It(114)) && !es.Applies(7101, new ClientItem { ClassId = 4, SubclassId = 1, InventoryType = 3 }));
            Check("applicability: a spell with no row in the equipped-items table applies to nothing (never guessed); an item class mismatch (armor enchant on a weapon) fails; a mask of -1 or 0 means any",
                !es.Applies(9999, It(110)) && !es.Applies(7001, It(140)) && new EquipReq { Class = -1, InvMask = -1, SubMask = 0 }.Fits(It(150)) && new EquipReq { Class = 2, InvMask = 0, SubMask = -1 }.Fits(It(150)) && !new EquipReq { Class = 2 }.Fits(null));
            var opts = UpgradeExtras.Options(es);
            Check("options: seven formulas (Enchanting) and two kinds of item - the Rough Armor Kit is an 'Armor kit', the Crude Scope a 'Scope' - from the spells whose effect enchants an item; a spell no profession teaches is not a formula",
                opts.Count == 9 && opts.Count(o => o.Kind == "Enchanting") == 7 && opts.Any(o => o.KitItemId == 600 && o.Kind == "Armor kit") && opts.Any(o => o.KitItemId == 601 && o.Kind == "Scope") && !opts.Any(o => o.Kind == "Enchanting" && (o.SpellId == 7101 || o.SpellId == 7102)), string.Join(",", opts.Select(o => o.Kind + o.SpellId)));

            // ---------------------------------------------------------------------------------------------------------------- scoring and words
            var agi4 = UpgradeExtras.PartsOf(es.Get(1001), 5, It(110), w, out bool u1);
            var dmg = UpgradeExtras.PartsOf(es.Get(1006), 16, It(140), w, out _);
            var scope = UpgradeExtras.PartsOf(es.Get(1008), 18, It(150), w, out _);
            var proc = UpgradeExtras.PartsOf(es.Get(1005), 5, It(110), w, out bool u2);
            var part = UpgradeExtras.PartsOf(es.Get(1009), 1, It(100), w, out bool u3);
            var arm = UpgradeExtras.PartsOf(es.Get(1007), 5, It(110), w, out _);
            Check("score: +4 Agility is worth 4 to a Hunter; weapon damage is bonus damage per swing divided by the swing time and weighted as damage per second (+3 on a 2.5 s dagger = 1.2 dps x 1.5 = 1.8; +7 on a bow = 2.8 dps x 14 = 39.2); armor counts as armor",
                Math.Abs(UpgradeExtras.ScoreOf(agi4) - 4) < 1e-9 && !u1 && Math.Abs(UpgradeExtras.ScoreOf(dmg) - 1.8) < 1e-9 && Math.Abs(UpgradeExtras.ScoreOf(scope) - 39.2) < 1e-9 && Math.Abs(UpgradeExtras.ScoreOf(arm) - 0.6) < 1e-9 && arm[0].Label == "armor", UpgradeExtras.ScoreOf(dmg) + " " + UpgradeExtras.ScoreOf(scope));
            Check("score: a proc or a spell on equip is not a number - it is left out, nothing is guessed (Crusader has no parts and is flagged); one with a stat and a spell scores only the stat and is flagged partial",
                proc.Count == 0 && u2 && part.Count == 1 && part[0].Value == 8 && u3);
            Check("words: '+4 Agility, -3 Strength' - the change from the current enchant to the new one; the same enchant is no change; a text for the enchant alone", UpgradeExtras.DiffText(agi4, UpgradeExtras.PartsOf(es.Get(1002), 5, It(110), w, out _)) == "+4 Agility, -3 Strength"
                && UpgradeExtras.DiffText(agi4, agi4) == "" && UpgradeExtras.PartsText(agi4) == "+4 Agility");

            // ---------------------------------------------------------------------------------------------------------------- Enya: what she wears is enchanted, or not
            UpgradeEngine.ClearCache();
            var set = UpgradeEngine.Compute(enya);
            Check("enchants: the engine read the enchant tables (no problem) and has an entry for each worn slot - cap, chest, boots, cloak - and none for an empty one", set.EnchantProblem == "" && set.EnchantBySlot.Keys.OrderBy(k => k).SequenceEqual(new[] { 1, 5, 8, 15 }), string.Join(",", set.EnchantBySlot.Keys));
            var chest = set.EnchantBySlot[5];
            Check("current enchant: the chest wears Strength +3 (1002), shown as '+3 Strength (Strength +3)'; the cloak Stamina +3; the boots and the cap nothing", chest.CurrentId == 1002 && chest.CurrentText == "+3 Strength (Strength +3)" && set.EnchantBySlot[15].CurrentText == "+3 Stamina (Stamina +3)" && set.EnchantBySlot[8].CurrentId == 0 && set.EnchantBySlot[8].CurrentText == "");
            Check("suggestion: only an enchant that beats the current one under the weights - Agility +4 over Strength +3 on the chest (+567 %); the same enchant (Strength), the Crusader proc and an armor kit that is worth no more than the current one are not offered",
                chest.Options.Count == 1 && chest.Options[0].SpellId == 7001 && chest.Options[0].Name == "Enchant Chest - Agility" && chest.Options[0].Delta == "+4 Agility, -3 Strength (score +567%)" && Math.Round(chest.Options[0].Pct.Value) == 567, string.Join(" | ", chest.Options.Select(o => o.Name + " " + o.Delta)));
            Check("suggestion: the cloak already wears the best Stamina enchant - nothing is suggested and it is not marked 'no enchant'", set.EnchantBySlot[15].Options.Count == 0 && set.EnchantBySlot[15].Enchantable);
            var boots = set.EnchantBySlot[8];
            Check("no enchant: the boots have none - Agility +5 (a formula) and the armor kit (+30 armor) are offered, the larger gain first; the words say 'no enchant'",
                Joined(boots.Options.Select(o => o.SpellId.ToString())) == "7003,7101" && boots.Options[0].Delta == "+5 Agility (no enchant)" && boots.Options[0].Pct == null && boots.Options[1].Delta == "+30 armor (no enchant)" && boots.Options[1].Kind == "Armor kit" && boots.Options[1].ItemId == 600, Joined(boots.Options.Select(o => o.SpellId + ":" + o.Delta)));
            var head = set.EnchantBySlot[1];
            Check("a partial enchant: Swiftness (+8 Agility and a spell) is offered for the cap with the stat scored and the rest said - 'also has an effect that is not scored'", head.Options.Count == 2 && head.Options[0].SpellId == 7007 && head.Options[0].Partial && head.Options[0].Delta == "+8 Agility (no enchant, also has an effect that is not scored)", Joined(head.Options.Select(o => o.Delta)));

            // ---------------------------------------------------------------------------------------------------------------- how to get it and who can apply it
            var e7001 = chest.Options[0];
            Check("how: the formula says what it needs - 'Crafted · Enchanting 100 · Aela can make it' (Aela knows the recipe and is one of my characters), who can apply it is marked, the reagent with what I own ('Linen Cloth x2 (you have 6)'), and how the formula is learned (the trainer)",
                e7001.Sources.Count == 1 && e7001.Best.Line == "Crafted · Enchanting 100 · Aela can make it" && e7001.Best.OwnMaker && e7001.Applicable && Joined(e7001.Reagents) == "Linen Cloth x2 (you have 6)" && Joined(e7001.Learn) == "Learn it at the Enchanting trainer", e7001.Best.Line + " | " + Joined(e7001.Learn));
            var e7003 = boots.Options[0];
            Check("how: a formula nobody of mine knows says 'learn it first' and is not applicable; the armor kit is applicable because Borg already owns two (you have 2 across my characters), has the Leatherworking recipe's reagents and the vendor to buy it from",
                e7003.Best.Line == "Crafted · Enchanting 100 · learn it first" && !e7003.Applicable && boots.Options[1].Applicable && boots.Options[1].Owned == 2 && Joined(boots.Options[1].Reagents) == "Linen Cloth x4 (you have 6)" && boots.Options[1].Sources.Any(s => s.Kind == "vendor" && s.What == "Peddler Nim"), e7003.Best.Line);
            AtlasQuery.Context.Crafters = spells =>
            {
                var r = new LodgeCrafters { Available = true };
                r.Rows.Add(new LodgeCrafter { Spell = 7003, Owner = "u1", By = "Tess", Key = "k1", Name = "Bob", Class = "MAGE", Profession = "Enchanting", Skill = 225, Max = 225 });
                return r;
            };
            UpgradeEngine.ClearCache();
            var setL = UpgradeEngine.Compute(enya);
            var l7003 = setL.EnchantBySlot[8].Options.First(o => o.SpellId == 7003);
            Check("who can apply it: a Lodge member who knows the formula (the Armory's answer) makes it applicable - 'Crafted · Enchanting 100 · Bob (Tess) can make it'", l7003.Applicable && l7003.Best.LodgeMaker && l7003.Best.Line == "Crafted · Enchanting 100 · Bob (Tess) can make it", l7003.Best.Line);
            AtlasQuery.Context.Crafters = null;
            UpgradeEngine.ClearCache();
            set = UpgradeEngine.Compute(enya);

            // ---------------------------------------------------------------------------------------------------------------- Pick and the filters
            var picks = UpgradeEngine.Pick(set, f0);
            Check("panel data: each slot says what is on it - 'Enchanted: +3 Strength (Strength +3)', 'No enchant' (flagged) where something could be put, nothing where nothing can; at most two suggestions a slot",
                Slot(picks, 5).EnchantCurrent == "Enchanted: +3 Strength (Strength +3)" && !Slot(picks, 5).NoEnchant && Slot(picks, 8).EnchantCurrent == "No enchant" && Slot(picks, 8).NoEnchant && Slot(picks, 2).EnchantCurrent == "" && !Slot(picks, 2).NoEnchant && picks.All(p => p.Enchants.Count <= 2));
            var onlyMine = UpgradeEngine.Pick(set, new UpgradeFilters { OnlyApplicable = true });
            Check("filter: 'only enchants I or my Lodge can apply' keeps the chest formula (Aela knows it) and the armor kit I own and drops the boots formula nobody knows and the cap's Swiftness",
                Joined(Slot(onlyMine, 5).Enchants.Select(o => o.SpellId.ToString())) == "7001" && Joined(Slot(onlyMine, 8).Enchants.Select(o => o.SpellId.ToString())) == "7101" && Joined(Slot(onlyMine, 1).Enchants.Select(o => o.SpellId.ToString())) == "7101");
            var noEn = UpgradeEngine.Pick(set, new UpgradeFilters { Enchants = false });
            Check("filter: enchants off - no enchant rows and no 'No enchant' line", noEn.All(p => p.Enchants.Count == 0 && p.EnchantCurrent == "" && !p.NoEnchant));
            var noClassic = UpgradeEngine.Pick(set, new UpgradeFilters { Classic = false });
            Check("filter: without the Classic baseline's sources the kit is still offered through the Leatherworking recipe (the game's own data); the Crude Scope's vendor row (Classic only) is gone", Slot(noClassic, 8).Enchants.Any(o => o.SpellId == 7101 && o.Sources.All(s => s.Kind == "craft")));
            Check("filters: the new switches are saved with the others (enchants, only applicable, food) and read back; an old saved text keeps them at the defaults", new UpgradeFilters { Enchants = false, OnlyApplicable = true, Food = false }.Serialize() == "D1R0C1T0L0N0A1F0" && !UpgradeFilters.Parse("D1R0C1T0L0N0A1F0").Enchants && UpgradeFilters.Parse("D1R0C1T0L0N0A1F0").OnlyApplicable && !UpgradeFilters.Parse("D1R0C1T0L0N0A1F0").Food
                && UpgradeFilters.Parse("D1R0C1T0L3").Enchants && !UpgradeFilters.Parse("D1R0C1T0L3").OnlyApplicable && UpgradeFilters.Parse("D1R0C1T0L3").Food);

            // ---------------------------------------------------------------------------------------------------------------- enchants for the items I could get instead
            var aela = UpgradeChar.From(C("Aela"));
            var setA = UpgradeEngine.Compute(aela);
            var cowl = setA.BySlot[1].First(c => c.Item.Id == 101);
            var axe = setA.BySlot[16].First(c => c.Item.Id == 141);
            Check("upgrade items: each suggested item says its best enchant - the Ranger's Cowl 'Best enchant: Enchant Head - Swiftness (+8 Agility)', the Twin Axe 'Best enchant: Enchant Weapon - Impact (+3 weapon damage)'",
                cowl.EnchantNote == "Best enchant: Enchant Head - Swiftness (+8 Agility)" && axe.EnchantNote == "Best enchant: Enchant Weapon - Impact (+3 weapon damage)", cowl.EnchantNote + " | " + axe.EnchantNote);
            var picksA = UpgradeEngine.Pick(setA, f0);
            Check("Aela: her ranged slot offers the Crude Scope (+7 damage x 14 = a huge gain for a Hunter), her main hand the Impact formula, her cap flagged 'No enchant'; her bow is not offered a melee enchant",
                Slot(picksA, 18).Enchants.Any(o => o.ItemId == 601 && o.Kind == "Scope") && !Slot(picksA, 18).Enchants.Any(o => o.SpellId == 7004) && Slot(picksA, 16).Enchants.Any(o => o.SpellId == 7004) && Slot(picksA, 1).NoEnchant, Joined(Slot(picksA, 18).Enchants.Select(o => o.Name)));

            // ---------------------------------------------------------------------------------------------------------------- missing data
            EnchantData.SetForTest(null, EnchantData.State.Unavailable, "layout"); UpgradeEngine.ClearCache();
            var sNo = UpgradeEngine.Compute(enya);
            Check("missing data: enchant tables the game does not have (or of a layout this version does not know) = 'unavailable', no enchant row, the item upgrades still work", sNo.EnchantProblem == "unavailable" && sNo.EnchantBySlot.Count == 0 && UpgradeEngine.Pick(sNo, f0).All(p => p.Enchants.Count == 0 && p.EnchantCurrent == "") && sNo.BySlot.Count > 0);
            EnchantData.SetForTest(null, EnchantData.State.Disabled, "off"); UpgradeEngine.ClearCache();
            Check("missing data: game data switched off = 'off'", UpgradeEngine.Compute(enya).EnchantProblem == "off");
            EnchantData.SetForTest(new EnchantSet()); UpgradeEngine.ClearCache();
            Check("missing data: tables without any enchant spell = 'none'", UpgradeEngine.Compute(enya).EnchantProblem == "none");
            Check("tables: bytes that are not the table (garbage, nothing, a foreign layout) give a calm problem text and never an exception", EnchantParse.All(new byte[] { 1, 2, 3, 4 }, null).Problem.Length > 0 && EnchantParse.All(null, null).Problem.Contains("SpellItemEnchantment") && EnchantParse.All(new byte[0], new byte[0]).Problem.Length > 0);
            EnchantData.SetForTest(UpEnchants()); UpgradeEngine.ClearCache(); UpgradeExtras.ForgetForTest();

            // ---------------------------------------------------------------------------------------------------------------- food and drink
            set = UpgradeEngine.Compute(aela);
            var food = UpgradeEngine.PickFood(set, f0);
            List<ExtraEntry> Kind(string k) => food.Where(e => e.Kind == k).ToList();
            Check("food: for a level 30 Hunter the well-fed foods that match her weights, the best first - Grilled Squid (+30 Attack Power, 30 min), Spiced Wolf Ribs (+8 Agility, +8 Stamina, 15 min), Cactus Apple Surprise (+6 Intellect); nothing that needs level 40",
                Joined(Kind("Well fed").Select(e => e.ItemId.ToString())) == "310,301,309" && Kind("Well fed")[0].Buff == "+30 Attack Power for 30 min" && Kind("Well fed")[1].Buff == "+8 Agility, +8 Stamina for 15 min" && !food.Any(e => e.ItemId == 302), Joined(Kind("Well fed").Select(e => e.ItemId + ":" + e.Buff)));
            Check("food: plain healing food for the level as the fallback (the game's own text of the spell) - top three by amount: Roast Raptor 1200, Smoked Sagefish 874, Roasted Boar Meat 552; Dried Bread (150) is fourth and left out",
                Joined(Kind("Food").Select(e => e.ItemId.ToString())) == "305,303,300" && Kind("Food")[0].Buff == "Restores 1200 health over 30 sec" && Joined(Kind("Drink").Select(e => e.ItemId.ToString())) == "307,308,306" && Kind("Drink")[0].Buff == "Restores 1200 mana over 30 sec");
            var ribs = Kind("Well fed")[1];
            Check("food: how to get it - the vendor first ('Vendor · Peddler Nim · Greenvale (Classic)'), then the Cooking recipe 'Crafted · Cooking 25 · You can make it' (Aela knows it), the reagents with what I own, how the recipe is learned (a recipe item and where it is sold)",
                ribs.Sources.Count == 2 && ribs.Sources[0].Line == "Vendor · Peddler Nim · Greenvale (Classic)" && ribs.Sources[1].Line == "Crafted · Cooking 25 · You can make it" && Joined(ribs.Reagents) == "Linen Cloth x2 (you have 6)" && ribs.Learn.Count >= 1 && ribs.Learn[0] == "Taught by Recipe: Spiced Wolf Ribs" && ribs.Applicable, Joined(ribs.Sources.Select(s => s.Line)) + " | " + Joined(ribs.Learn) + " | " + Joined(ribs.Reagents) + " | " + ribs.Applicable + " | " + (ProfessionBook.Build(SpellData.Spells, ItemData.Get, ItemDetails.Current.Effects).BySpell.TryGetValue(7401, out var dbgPr) ? dbgPr.Learn + "/" + dbgPr.TeachingItems.Count : "none"));
            Check("food: what only the Classic baseline knows is labelled - Roasted Boar Meat is sold in the baseline only (classic), the ribs have a recipe of the game's own data (not classic only)", Kind("Food").First(e => e.ItemId == 300).ClassicOnly && !ribs.ClassicOnly && Kind("Food").First(e => e.ItemId == 300).Best.Line.EndsWith("(Classic)"));
            Check("food: a class without mana (Warrior) gets no drinks; the well-fed list uses her own weights", UpgradeEngine.PickFood(UpgradeEngine.Compute(UpgradeChar.From(C("Wren"))), f0).All(e => e.Kind != "Drink") && UpgradeEngine.PickFood(UpgradeEngine.Compute(UpgradeChar.From(C("Wren"))), f0).Any(e => e.Kind == "Well fed"));
            Check("food: the filters - hidden by its switch; without the Classic baseline only what the game's recipes give remains (the ribs through Cooking)", UpgradeEngine.PickFood(set, new UpgradeFilters { Food = false }).Count == 0 && Joined(UpgradeEngine.PickFood(set, new UpgradeFilters { Classic = false }).Select(e => e.ItemId.ToString())) == "301,361");
            var low = UpgradeChar.From(C("Dru")); low.Level = 12;
            Check("food: only what the character can use at the level (a level 12 Rogue: nothing above 12 - not the ribs (20) or the squid (26); the juice (12) is a drink only for classes with mana)", UpgradeEngine.PickFood(UpgradeEngine.Compute(low), f0).All(e => (ItemData.Get(e.ItemId)?.RequiredLevel ?? 99) <= 12));
            UpgradesConsumableChecks(Check, chars, C);
        }
    }
}
