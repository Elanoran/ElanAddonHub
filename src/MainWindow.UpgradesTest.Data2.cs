using System;
using System.Collections.Generic;
using System.Linq;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Db2;

namespace ElansAddonHub
{
    // The enchants, kits, food and recipes of the invented world (see MainWindow.UpgradesTest.Data.cs)
    public partial class MainWindow
    {
        static SpellInfo UpSp(int id, string name, string desc = "", int durationMs = 0, params EffectInfo[] effects)
            => new SpellInfo { Id = id, Name = name, Desc = desc, DurationMs = durationMs, Effects = effects, IconId = 0 };

        static EffectInfo Eff(int effect, int misc = 0, int aura = 0, float points = 0, int trigger = 0, int index = 0)
            => new EffectInfo { Index = index, Effect = effect, Misc = misc, Aura = aura, Points = points, Trigger = trigger };

        static List<SpellInfo> UpSpellList()
        {
            var l = new List<SpellInfo>
            {
                UpSp(5001, "Studded Leather Vest", "", 0, new EffectInfo { Index = 0, Effect = EffectInfo.CreateItem, ItemType = 111 }),
                // formulas: Enchanting (skill 333). Effect 53 = a permanent enchant, the first misc value is the enchant
                UpSp(7001, "Enchant Chest - Agility", "", 0, Eff(53, 1001)), UpSp(7002, "Enchant Chest - Strength", "", 0, Eff(53, 1002)), UpSp(7003, "Enchant Boots - Agility", "", 0, Eff(53, 1004)),
                UpSp(7004, "Enchant Weapon - Impact", "", 0, Eff(53, 1006)), UpSp(7005, "Enchant Chest - Crusader", "", 0, Eff(53, 1005)), UpSp(7006, "Enchant Cloak - Stamina", "", 0, Eff(53, 1003)),
                UpSp(7007, "Enchant Head - Swiftness", "", 0, Eff(53, 1009)),
                // the Use spells of the kit and the scope (no profession teaches these)
                UpSp(7101, "Armor Kit", "", 0, Eff(53, 1007)), UpSp(7102, "Scope", "", 0, Eff(53, 1008)),
                UpSp(7201, "Rough Armor Kit", "", 0, new EffectInfo { Index = 0, Effect = EffectInfo.CreateItem, ItemType = 600 }),
                // food: healing and drinks (the description carries the amount), well-fed foods (the Use spell triggers a buff spell with stat auras)
                UpSp(7301, "Food", "Restores 552 health over 18 sec."), UpSp(7311, "Food", "Restores 874 health over 24 sec."), UpSp(7312, "Food", "Restores 150 health over 18 sec."), UpSp(7313, "Food", "Restores 1200 health over 30 sec."),
                UpSp(7321, "Drink", "Restores 400 mana over 18 sec."), UpSp(7322, "Drink", "Restores 1200 mana over 30 sec."), UpSp(7323, "Drink", "Restores 700 mana over 24 sec."),
                UpSp(7302, "Food", "Restores health.", 0, Eff(64, 0, 0, 0, 7303)), UpSp(7303, "Well Fed", "", 900000, Eff(6, 1, 29, 8, 0, 0), Eff(6, 2, 29, 8, 0, 1)),
                UpSp(7330, "Food", "Restores health.", 0, Eff(64, 0, 0, 0, 7331)), UpSp(7331, "Well Fed", "", 900000, Eff(6, 3, 29, 6, 0, 0)),
                UpSp(7340, "Food", "Restores health.", 0, Eff(64, 0, 0, 0, 7341)), UpSp(7341, "Well Fed", "", 1800000, Eff(6, 0, 99, 30, 0, 0)),
                UpSp(7350, "Food", "Restores health.", 0, Eff(64, 0, 0, 0, 7351)), UpSp(7351, "Well Fed", "", 900000, Eff(6, 1, 29, 25, 0, 0)),
                UpSp(7401, "Spiced Wolf Ribs", "", 0, new EffectInfo { Index = 0, Effect = EffectInfo.CreateItem, ItemType = 301 }),
            };
            return l;
        }

        static List<SkillInfo> UpSkillList() => new List<SkillInfo>
        {
            new SkillInfo { Id = 165, Name = "Leatherworking", Category = SkillInfo.CatProfession },
            new SkillInfo { Id = 333, Name = "Enchanting", Category = SkillInfo.CatProfession },
            new SkillInfo { Id = 185, Name = "Cooking", Category = SkillInfo.CatProfession },
        };

        static List<SkillAbility> UpAbilityList() => new List<SkillAbility>
        {
            new SkillAbility { Id = 1, Skill = 165, Spell = 5001, MinRank = 110, Green = 130, Gray = 150 },
            new SkillAbility { Id = 2, Skill = 333, Spell = 7001, MinRank = 100, Acquire = 0 }, new SkillAbility { Id = 3, Skill = 333, Spell = 7002, MinRank = 100, Acquire = 0 },
            new SkillAbility { Id = 4, Skill = 333, Spell = 7003, MinRank = 100, Acquire = 0 }, new SkillAbility { Id = 5, Skill = 333, Spell = 7004, MinRank = 150, Acquire = 0 },
            new SkillAbility { Id = 6, Skill = 333, Spell = 7005, MinRank = 200, Acquire = 0 }, new SkillAbility { Id = 7, Skill = 333, Spell = 7006, MinRank = 90, Acquire = 0 },
            new SkillAbility { Id = 8, Skill = 333, Spell = 7007, MinRank = 160, Acquire = 0 },
            new SkillAbility { Id = 9, Skill = 165, Spell = 7201, MinRank = 20, Acquire = 0 },
            new SkillAbility { Id = 10, Skill = 185, Spell = 7401, MinRank = 25, Acquire = 2 },
        };

        static Dictionary<int, int[]> UpReagents() => new Dictionary<int, int[]>
        {
            [7001] = new[] { 190, 2 }, [7002] = new[] { 190, 2 }, [7003] = new[] { 190, 2 }, [7004] = new[] { 190, 3 }, [7005] = new[] { 190, 2 }, [7006] = new[] { 190, 1 }, [7007] = new[] { 190, 2 },
            [7201] = new[] { 190, 4 }, [7401] = new[] { 190, 2 },
        };

        static int MaskOf(params int[] bits) { int m = 0; foreach (var b in bits) m |= 1 << b; return m; }

        // SpellItemEnchantment rows and the SpellEquippedItems rows of the formulas, kits and the scope
        static EnchantSet UpEnchants()
        {
            var s = new EnchantSet();
            void E(int id, string name, int t0, int p0, int a0, int t1 = 0, int p1 = 0, int a1 = 0) => s.Enchants[id] = new EnchantInfo { Id = id, Name = name, Type = new[] { t0, t1, 0 }, Points = new[] { p0, p1, 0 }, Arg = new[] { a0, a1, 0 }, Icon = 0 };
            E(1001, "Agility +4", 5, 4, 3); E(1002, "Strength +3", 5, 3, 4); E(1003, "Stamina +3", 5, 3, 7); E(1004, "Agility +5", 5, 5, 3); E(1005, "Crusader", 3, 0, 20007);
            E(1006, "Impact +3", 2, 3, 0); E(1007, "Armor +30", 4, 30, 0); E(1008, "Scope +7 damage", 2, 7, 0); E(1009, "Swiftness", 5, 8, 3, 3, 0, 20008);
            void R(int spell, int cls, int inv, int sub) => s.Equipped[spell] = new List<EquipReq> { new EquipReq { Class = cls, InvMask = inv, SubMask = sub } };
            int chest = MaskOf(5, 20);
            R(7001, 4, chest, 0); R(7002, 4, chest, 0); R(7005, 4, chest, 0); R(7003, 4, MaskOf(8), 0); R(7006, 4, MaskOf(16), 0); R(7007, 4, MaskOf(1), 0);
            R(7004, 2, 0, MaskOf(0, 1, 4, 5, 6, 7, 8, 10, 13, 15));                                    // melee weapons
            R(7101, 4, MaskOf(1, 5, 7, 8), 0);                                                          // armor kit: head, chest, legs, feet
            R(7102, 2, 0, MaskOf(2, 3, 18));                                                            // scope: bows, guns, crossbows
            return s;
        }

        // what my characters hold: 6 Linen Cloth (Aela), 2 Rough Armor Kits (Borg)
        static Dictionary<int, InvItemTotals> UpOwned(List<InvChar> chars)
        {
            var aela = chars.First(c => c.Name == "Aela"); var borg = chars.First(c => c.Name == "Borg");
            return new Dictionary<int, InvItemTotals>
            {
                [190] = new InvItemTotals { Id = 190, Chars = { new InvCount { Char = aela, Bags = 6 } } },
                [600] = new InvItemTotals { Id = 600, Chars = { new InvCount { Char = borg, Bags = 2 } } },
            };
        }
    }
}
