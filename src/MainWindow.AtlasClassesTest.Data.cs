using System;
using System.Collections.Generic;
using System.Linq;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Talents;

namespace ElansAddonHub
{
    // --selftest "atlasclasses", part 1: the invented world. Class and race tables in the layouts of build 1.60.1.70338 (ClassTables), written with the small WDC5 writer
    // of the spelldata test, and the spell layer (SpellSnapshot) the class pages are built on: a Hunter, a Warrior and a Mage with a few spells each, ranks, a trap with
    // its helper "Effect" spell, a talent, a pet ability, a level-less variant; two playable races (Human, Orc) and the Night Elf, and a Goblin that cannot be created.
    public partial class MainWindow
    {
        const uint CcLayoutClasses = 0xAFC9B0C2, CcLayoutRaces = 0x4F44C796, CcLayoutBase = 0xFEC46FB7, CcLayoutPower = 0x70DA1F8C, CcLayoutSkillRace = 0x24277B48, CcLayoutLevels = 0x7EB86FDC, CcLayoutCost = 0x61AD223F, CcLayoutCd = 0xDC945B8C;

        static SdF[] CcClassCols() { var l = new List<SdF>(); for (int i = 0; i < 43; i++) l.Add(i <= 9 ? SdS() : SdI()); return l.ToArray(); }
        static object[] CcClass(int id, string name, string file, string desc, int icon, int r, int g, int b, int roles, int displayPower = 0)
        {
            var cols = CcClassCols(); var row = new object[43];
            for (int i = 0; i < 43; i++) row[i] = cols[i].Kind == 's' ? (object)"" : 0;
            row[0] = name; row[1] = file; row[5] = desc; row[12] = icon; row[15] = 1; row[29] = id; row[32] = displayPower; row[37] = r; row[38] = g; row[39] = b; row[40] = roles;
            return row;
        }

        static byte[] CcClasses(uint layout = CcLayoutClasses, uint table = 0xF5889D8C) => SdBuild(table, layout, CcClassCols(),
            new[]
            {
                CcClass(1, "Warrior", "WARRIOR", "Warriors train constantly.", 626008, 198, 155, 109, 10, 1),
                CcClass(3, "Hunter", "HUNTER", "Hunters know the uncharted places of the world.", 626000, 170, 211, 114, 8),
                CcClass(8, "Mage", "MAGE", "Magi are wizards of immense knowledge.", 626001, 63, 199, 235, 8),
            }, new[] { 1, 3, 8 }, idList: false, idIndex: 29);

        static SdF[] CcRaceCols()
        {
            var l = new List<SdF>();
            for (int i = 0; i < 51; i++)
                l.Add(i <= 14 ? SdS() : i == 23 || i == 24 ? SdI(3) : i == 30 || i == 32 || i == 33 ? SdFl(3) : i == 31 ? SdFl() : SdI());
            return l.ToArray();
        }
        static object[] CcRace(string prefix, string file, string name, string lore, string desc, int faction, int startLevel, int order, int bit, int side, int defaultClass)
        {
            var cols = CcRaceCols(); var row = new object[51];
            for (int i = 0; i < 51; i++) row[i] = cols[i].Len > 1 ? (object)new int[cols[i].Len] : cols[i].Kind == 's' ? (object)"" : cols[i].Kind == 'f' ? (object)0f : 0;
            row[0] = prefix; row[1] = file; row[2] = name; row[6] = lore; row[10] = desc; row[16] = faction; row[26] = startLevel; row[27] = order; row[28] = bit; row[34] = 7; row[36] = side; row[39] = defaultClass;
            return row;
        }

        static byte[] CcRaces(uint layout = CcLayoutRaces) => SdBuild(ClassTables.Races.TableHash, layout, CcRaceCols(),
            new[]
            {
                CcRace("Hu", "Human", "Human", "Human", "Humans are a young race.", 1, 1, 1, 0, 0, 1),
                CcRace("Or", "Orc", "Orc", "Orc", "The orc race originated on Draenor.", 2, 1, 5, 1, 1, 1),
                CcRace("Ni", "NightElf", "Night Elf", "Kaldorei", "Ten thousand years ago.", 4, 1, 3, 3, 0, 11),
                CcRace("Go", "Goblin", "Goblin", "Bilgewater", "", 1, 1, 0, 8, 1, 4),
            }, new[] { 1, 2, 4, 9 });

        // race x class: Human can be a Warrior, Hunter or Mage, Orc a Warrior or Hunter, the Night Elf a Hunter; the Goblin is in no pair (not playable)
        static byte[] CcCharBase(uint layout = CcLayoutBase) => SdBuild(ClassTables.CharBase.TableHash, layout, new[] { SdI(), SdI(), SdI() },
            new[] { new object[] { 1, 1, 0 }, new object[] { 1, 3, 0 }, new object[] { 1, 8, 0 }, new object[] { 2, 1, 0 }, new object[] { 2, 3, 0 }, new object[] { 4, 3, 0 } }, new[] { 1, 2, 3, 4, 5, 6 });

        static byte[] CcClassPower(uint layout = CcLayoutPower) => SdBuild(ClassTables.ClassPower.TableHash, layout, new[] { SdI() },
            new[] { new object[] { 1 }, new object[] { 0 }, new object[] { 0 }, new object[] { 2 } }, new[] { 1, 3, 6, 9 }, foreign: new[] { 1, 3, 8, 3 });

        // SkillRaceClassInfo: SkillID, ClassMask, Flags, Availability, MinLevel, SkillTierID, RaceMasks[2]
        static byte[] CcSkillRace(uint layout = CcLayoutSkillRace)
        {
            object[] R(int skill, int cls, int avail, int minLevel, uint lo, uint hi) => new object[] { skill, cls, 0, avail, minLevel, 0, new[] { unchecked((int)lo), unchecked((int)hi) } };
            const uint All = 0xFFFFFFFF;
            var rows = new List<object[]>
            {
                R(50, 4, 1, 0, All, All), R(163, 4, 1, 0, All, All), R(51, 4, 1, 0, All, All), R(261, 4, 1, 0, All, All),            // Hunter: Beast Mastery, Marksmanship, Survival (empty here), Beast Training
                R(26, 1, 1, 0, All, All), R(237, 128, 1, 0, All, All), R(40, 8, 1, 0, All, All),                                     // Warrior Arms, Mage Arcane, a Rogue-only secondary skill
                R(414, 5, 1, 0, All, All), R(413, 4, 0, 40, All, All), R(293, 1, 0, 40, All, All), R(415, 1503, 1, 0, All, All),        // Leather (warrior, hunter), Mail from 40 (hunter), Plate from 40 (warrior), Cloth
                R(45, 4, 1, 0, All, All), R(44, 5, 1, 0, All, All), R(136, 4, 1, 0, All, All),                                      // Bows, Axes, Staves
                R(754, 1503, 1, 0, 1, 0), R(125, 1503, 1, 0, 2, 0),                                                                  // Racial - Human (race bit 0), Orc Racial (bit 1)
                R(98, 1503, 1, 0, 1, 0), R(109, 1503, 1, 0, 2, 0), R(148, 1503, 0, 0, 1, 0), R(148, 1503, 0, 0, 0xFE, 3), R(149, 1503, 0, 0, 2, 0),   // Common, Orcish, Horse Riding (Human; and the others), Wolf Riding (Orc)
            };
            return SdBuild(ClassTables.SkillRaceClass.TableHash, layout, new[] { SdI(), SdI(), SdI(), SdI(), SdI(), SdI(), SdI(2) }, rows.ToArray(), Enumerable.Range(1, rows.Count).ToArray());
        }

        // spell id, base level, spell level, cost (type, amount, pct), cooldown ms (own, category)
        sealed class CcSpell { public int Id, Base, Level, Type, Cost, Cd, CatCd; public float Pct; public string Name, Rank, Desc; public int Skill, Mask = 4, Icon, Cast; }

        static List<CcSpell> CcSpells()
        {
            CcSpell S(int id, string name, string rank, int skill, int bse, int lvl, string desc, int type = 0, int cost = 0, int cd = 0, float pct = 0, int mask = 4, int cast = 0) =>
                new CcSpell { Id = id, Name = name, Rank = rank, Skill = skill, Base = bse, Level = lvl, Desc = desc, Type = type, Cost = cost, Cd = cd, Pct = pct, Mask = mask, Icon = 135000 + id % 997, Cast = cast };
            return new List<CcSpell>
            {
                // Hunter: Beast Mastery (50), Marksmanship (163), Beast Training (261, the pet)
                S(13165, "Aspect of the Hawk", "Rank 1", 50, 10, 10, "Increases ranged attack power by 20.", 0, 20), S(14318, "Aspect of the Hawk", "Rank 2", 50, 18, 18, "Increases ranged attack power by 35.", 0, 35),
                S(14319, "Aspect of the Hawk", "Rank 3", 50, 28, 28, "Increases ranged attack power by 50.", 0, 50),
                S(1515, "Tame Beast", "", 50, 10, 10, "Tames a beast to be your companion.", 0, 0, 0, 0, 4, 20000), S(13161, "Aspect of the Beast", "Rank 1", 50, 0, 30, "Beast aspect.", 0, 50),
                S(2973, "Raptor Strike", "Rank 1", 163, 1, 1, "A strong attack.", 0, 10, 6000), S(415335, "Raptor Strike", "Rank 1", 163, 0, 1, "A strong attack that does more.", 0, 10, 6000),
                S(1978, "Serpent Sting", "Rank 1", 163, 4, 4, "Stings the target for 20 damage over 15 sec.", 0, 15), S(13549, "Serpent Sting", "Rank 2", 163, 10, 10, "Stings the target for 40 damage.", 0, 30),
                S(3044, "Arcane Shot", "Rank 1", 163, 6, 6, "An instant shot that causes 15 Arcane damage.", 0, 25, 6000), S(14281, "Arcane Shot", "Rank 2", 163, 12, 12, "An instant shot that causes 23 Arcane damage.", 0, 35, 6000),
                S(14282, "Arcane Shot", "Rank 3", 163, 20, 20, "An instant shot that causes 33 Arcane damage.", 0, 50, 6000),
                S(1130, "Hunter's Mark", "Rank 1", 163, 6, 6, "Marks the target.", 0, 15),
                S(13795, "Immolation Trap", "Rank 1", 163, 16, 16, "Place a fire trap that burns the first enemy.", 0, 50, 30000), S(13797, "Immolation Trap", "Rank 1", 163, 16, 16, "Inflict fire damage every few seconds."),
                S(13813, "Explosive Trap", "Rank 1", 163, 34, 34, "Place a trap that explodes.", 0, 275, 30000), S(13812, "Explosive Trap Effect", "Rank 1", 163, 34, 34, "", 0),
                S(19434, "Aimed Shot", "Rank 1", 163, 20, 20, "A carefully aimed shot.", 0, 75, 6000, 0, 0),            // a talent (the tree below has it), class mask 0 in the skill line
                S(409433, "Chimera Shot", "", 163, 0, 1, "A shot with a special effect.", 0, 0, 6000),                                // no learn level, spell level 1
                S(1853, "Growl", "Rank 1", 261, 0, 1, "The pet taunts."), S(14922, "Growl", "Rank 2", 261, 0, 10, "The pet taunts better."),
                S(2980, "Claw", "Rank 1", 261, 0, 1, "The pet claws."), S(2981, "Claw", "Rank 2", 261, 0, 8, "The pet claws harder."),
                // Warrior: Arms (26), the rage is stored x10
                S(78, "Heroic Strike", "Rank 1", 26, 1, 1, "A strong attack.", 1, 150, 0, 0, 1), S(100, "Charge", "Rank 1", 26, 4, 4, "Charge an enemy.", 1, 0, 0, 0, 1), S(772, "Rend", "Rank 1", 26, 4, 4, "Wounds the target.", 1, 100, 0, 0, 1),
                // Mage: Arcane (237): a cost in percent of base mana
                S(133, "Fireball", "Rank 1", 237, 1, 1, "Hurls a fiery ball.", 0, 30, 0, 0, 128, 3500), S(1459, "Arcane Intellect", "Rank 1", 237, 1, 1, "Increases intellect.", 0, 0, 0, 4.5f, 128),
                S(12051, "Evocation", "", 237, 20, 20, "Restores mana.", 0, 0, 480000, 0, 128, 8000),
            };
        }

        static byte[] CcLevels(List<CcSpell> spells, uint layout = CcLayoutLevels)
        {
            var rows = spells.Where(s => s.Base > 0 || s.Level > 0).Select(s => new object[] { 0, 0, 0, s.Base, s.Level }).ToArray();
            var sp = spells.Where(s => s.Base > 0 || s.Level > 0).ToList();
            return SdBuild(ClassTables.SpellLevels.TableHash, layout, new[] { SdI(), SdI(), SdI(), SdI(), SdI() }, rows, Enumerable.Range(1, rows.Length).ToArray(), foreign: sp.Select(s => s.Id).ToArray());
        }

        static byte[] CcPower(List<CcSpell> spells, uint layout = CcLayoutCost)
        {
            var fs = new List<SdF>(); for (int i = 0; i < 14; i++) fs.Add(i >= 7 && i <= 10 ? SdFl() : SdI());
            var sp = spells.Where(s => s.Cost > 0 || s.Pct > 0).ToList();
            var rows = sp.Select((s, k) =>
            {
                var r = new object[14]; for (int i = 0; i < 14; i++) r[i] = i >= 7 && i <= 10 ? (object)0f : 0;
                r[0] = 1000 + k; r[2] = s.Cost; r[7] = s.Pct; r[11] = s.Type;
                return r;
            }).ToArray();
            return SdBuild(ClassTables.SpellPower.TableHash, layout, fs.ToArray(), rows, Enumerable.Range(1000, rows.Length).ToArray(), foreign: sp.Select(s => s.Id).ToArray(), idList: false, idIndex: 0);
        }

        static byte[] CcCooldowns(List<CcSpell> spells, uint layout = CcLayoutCd)
        {
            var sp = spells.Where(s => s.Cd > 0 || s.CatCd > 0).ToList();
            var rows = sp.Select(s => new object[] { 0, s.CatCd, s.Cd, 1500, 0 }).ToArray();
            return SdBuild(ClassTables.SpellCooldowns.TableHash, layout, new[] { SdI(), SdI(), SdI(), SdI(), SdI() }, rows, Enumerable.Range(1, rows.Length).ToArray(), foreign: sp.Select(s => s.Id).ToArray());
        }

        static ClassRaw CcRaw(List<CcSpell> spells = null, Func<string, uint?> layoutOf = null, params string[] without)
        {
            spells = spells ?? CcSpells();
            var raw = new ClassRaw();
            uint L(string name, uint def) => layoutOf?.Invoke(name) ?? def;
            raw.Tables[ClassTables.Classes.Name] = CcClasses(L("ChrClasses", CcLayoutClasses));
            raw.Tables[ClassTables.Races.Name] = CcRaces(L("ChrRaces", CcLayoutRaces));
            raw.Tables[ClassTables.CharBase.Name] = CcCharBase(L("CharBaseInfo", CcLayoutBase));
            raw.Tables[ClassTables.ClassPower.Name] = CcClassPower(L("ChrClassesXPowerTypes", CcLayoutPower));
            raw.Tables[ClassTables.SkillRaceClass.Name] = CcSkillRace(L("SkillRaceClassInfo", CcLayoutSkillRace));
            raw.Tables[ClassTables.SpellLevels.Name] = CcLevels(spells, L("SpellLevels", CcLayoutLevels));
            raw.Tables[ClassTables.SpellPower.Name] = CcPower(spells, L("SpellPower", CcLayoutCost));
            raw.Tables[ClassTables.SpellCooldowns.Name] = CcCooldowns(spells, L("SpellCooldowns", CcLayoutCd));
            foreach (var w in without) raw.Tables.Remove(w);
            return raw;
        }

        // the spell layer the pages are built on
        static AtlasSet CcSpellSet(List<CcSpell> spells = null)
        {
            spells = spells ?? CcSpells();
            var infos = spells.Select(s => new SpellInfo { Id = s.Id, Name = s.Name, Rank = s.Rank, Desc = s.Desc, IconId = s.Icon, CastMs = s.Cast }).ToList();
            // racial abilities
            infos.Add(new SpellInfo { Id = 20572, Name = "Blood Fury", Desc = "Increases attack power.", IconId = 135726 });
            infos.Add(new SpellInfo { Id = 20573, Name = "Hardiness", Desc = "Reduces the duration of stuns.", IconId = 135727 });
            infos.Add(new SpellInfo { Id = 20600, Name = "Perception", Desc = "Increases your stealth detection.", IconId = 135728 });
            var skills = new List<SkillInfo>
            {
                new SkillInfo { Id = 50, Name = "Beast Mastery", Category = 7, IconId = 132164 }, new SkillInfo { Id = 163, Name = "Marksmanship", Category = 7, IconId = 132222 },
                new SkillInfo { Id = 51, Name = "Survival", Category = 7, IconId = 132215 }, new SkillInfo { Id = 261, Name = "Beast Training", Category = 7, IconId = 132164 },
                new SkillInfo { Id = 26, Name = "Arms", Category = 7 }, new SkillInfo { Id = 237, Name = "Arcane", Category = 7 }, new SkillInfo { Id = 40, Name = "Poisons", Category = 9 },
                new SkillInfo { Id = 414, Name = "Leather", Category = 8 }, new SkillInfo { Id = 413, Name = "Mail", Category = 8 }, new SkillInfo { Id = 293, Name = "Plate Mail", Category = 8 }, new SkillInfo { Id = 415, Name = "Cloth", Category = 8 },
                new SkillInfo { Id = 45, Name = "Bows", Category = 6 }, new SkillInfo { Id = 44, Name = "Axes", Category = 6 }, new SkillInfo { Id = 136, Name = "Staves", Category = 6 },
                new SkillInfo { Id = 754, Name = "Racial - Human", Category = 9 }, new SkillInfo { Id = 125, Name = "Orc Racial", Category = 9 },
                new SkillInfo { Id = 98, Name = "Language: Common", Category = 10 }, new SkillInfo { Id = 109, Name = "Language: Orcish", Category = 10 },
                new SkillInfo { Id = 148, Name = "Horse Riding", Category = 9 }, new SkillInfo { Id = 149, Name = "Wolf Riding", Category = 9 },
            };
            var abil = new List<SkillAbility>(); int n = 1;
            foreach (var s in spells) abil.Add(new SkillAbility { Id = n++, Skill = s.Skill, Spell = s.Id, MinRank = 1, ClassMask = s.Mask });
            abil.Add(new SkillAbility { Id = n++, Skill = 125, Spell = 20572 }); abil.Add(new SkillAbility { Id = n++, Skill = 125, Spell = 20573 }); abil.Add(new SkillAbility { Id = n++, Skill = 754, Spell = 20600 });
            return new AtlasSet
            {
                Spells = new SpellSnapshot(infos, skills, abil, new Dictionary<int, int[]>()),
                Factions = new FactionSnapshot(new[] { new FactionInfo { Id = 72, Name = "Stormwind" }, new FactionInfo { Id = 76, Name = "Orgrimmar" } },
                    new[] { new FactionTemplateInfo { Id = 1, FactionId = 72, Group = 3 }, new FactionTemplateInfo { Id = 2, FactionId = 76, Group = 5 } }),
                Details = new ItemDetails(),
            };
        }

        // a Hunter talent tree with Aimed Shot in it
        static TalentSet CcTalents()
        {
            var set = new TalentSet();
            var tree = new TalentTreeDef { Id = 1, ClassId = 3, ClassName = "Hunter", ClassFile = "HUNTER" };
            tree.Nodes.Add(new TalentNode { Id = 1, Entries = { new TalentEntry { Id = 1, SpellId = 19434 } } });
            set.Trees.Add(tree);
            return set;
        }
    }
}
