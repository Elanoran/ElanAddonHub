using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ElansAddonHub.Services.Db2
{
    // Where the tables of the spell / skill / faction / item-detail layers sit in ONE client build. Same rule as ItemTables: the game writes each table's
    // layout hash into the file, the Outpost knows the layouts of the builds it was written against, and a hash it does not know means that ONE table is
    // unavailable (the others still are). Nothing is guessed.
    //
    // Source of every number: wowdev/WoWDBDefs, definitions/*.dbd, the LAYOUT blocks that list build 1.60.1.70338 (WoW Forever), checked against the real
    // files of that build. Field numbers count the STORED columns of the table (the id list / relationship columns are not fields), in the .dbd's order.
    public static class SpellTables
    {
        public sealed class Def
        {
            public string Name;            // short name: also the key in the raw table set
            public int FileId;             // FileDataID in the game's CASC storage
            public uint TableHash;         // the table's own hash (written by the game)
            public uint[] Layouts;         // layout hashes this version can read
            public string Build = "1.60.1.70338";
            public override string ToString() => Name;
        }

        static Def D(string name, int fileId, uint table, params uint[] layouts) => new Def { Name = name, FileId = fileId, TableHash = table, Layouts = layouts };

        // ---- spells and skills (field order from the .dbd)
        // Spell (sparse, three strings inline and nothing else): NameSubtext, Description, AuraDescription
        public static readonly Def Spell = D("Spell", 1140089, 0xE111669E, 0xE3D134FB);
        // SpellName: Name_lang(0)
        public static readonly Def SpellName = D("SpellName", 1990283, 0x46C66698, 0x782EE721);
        // SpellMisc: Attributes[17](0) DifficultyID(1) CastingTimeIndex(2) DurationIndex(3) PvPDurationIndex(4) RangeIndex(5) SchoolMask(6) Speed(7) LaunchDelay(8)
        // MinDuration(9) SpellIconFileDataID(10) ActiveIconFileDataID(11) ... ; SpellID = the relationship column
        public static readonly Def SpellMisc = D("SpellMisc", 1003144, 0xC603EE28, 0x434B3607);
        public const int MiscDifficulty = 1, MiscCastTime = 2, MiscDuration = 3, MiscRange = 5, MiscIcon = 10;
        // SpellEffect: EffectAura(0) DifficultyID(1) EffectIndex(2) Effect(3) EffectAmplitude(4) EffectAttributes(5) EffectAuraPeriod(6) EffectBonusCoefficient(7)
        // EffectChainAmplitude(8) EffectChainTargets(9) EffectItemType(10) EffectMechanic(11) EffectPointsPerResource(12) EffectPos_facing(13)
        // EffectRealPointsPerLevel(14) EffectTriggerSpell(15) BonusCoefficientFromAP(16) PvpMultiplier(17) Coefficient(18) Variance(19) ResourceCoefficient(20)
        // GroupSizeBasePointsCoefficient(21) EffectBasePointsF(22) ScalingClass(23) Node(24) EffectMiscValue[2](25) EffectRadiusIndex[2](26) EffectSpellClassMask[4](27)
        // ImplicitTarget[2](28); SpellID = the relationship column
        public static readonly Def SpellEffect = D("SpellEffect", 1140088, 0xF04238A5, 0x5362E3D4);
        public const int EffAura = 0, EffDifficulty = 1, EffIndex = 2, EffEffect = 3, EffPeriod = 6, EffChain = 9, EffItemType = 10, EffTrigger = 15, EffCoefficient = 18,
            EffPoints = 22, EffMisc = 25, EffRadius = 26;
        // SpellReagents: SpellID(0) Reagent[8](1) ReagentCount[8](2) ReagentReCraftCount[8](3) ReagentSource[8](4)
        public static readonly Def SpellReagents = D("SpellReagents", 841946, 0xAB66C99F, 0xFF40D202);
        // SpellDuration: Duration(0) MaxDuration(1) DurationPerResource(2) ; SpellCastTimes: Base(0) Minimum(1) ; SpellRadius: Radius(0) RadiusPerLevel(1) RadiusMin(2) RadiusMax(3)
        public static readonly Def SpellDuration = D("SpellDuration", 1137828, 0xB423FA7A, 0xA931BD2B);
        public static readonly Def SpellCastTimes = D("SpellCastTimes", 1134089, 0xFDBA5A66, 0x75B6BD3A);
        public static readonly Def SpellRadius = D("SpellRadius", 1134584, 0xAB7E4841, 0xF9A913EE);
        // SpellRange: DisplayName_lang(0) DisplayNameShort_lang(1) Flags(2) RangeMin[2](3) RangeMax[2](4)
        public static readonly Def SpellRange = D("SpellRange", 1146820, 0xE051A69C, 0xADA13705);
        // SkillLine: DisplayName_lang(0) AlternateVerb_lang(1) Description_lang(2) HordeDisplayName_lang(3) NeutralDisplayName(4) ID(5) CategoryID(6) SpellIconFileID(7)
        // CanLink(8) ParentSkillLineID(9) ParentTierIndex(10) Flags(11) SpellBookSpellID(12) ...   (the id is a stored column here)
        public static readonly Def SkillLine = D("SkillLine", 1240935, 0xB53DC9D6, 0x6763217C);
        // SkillLineAbility: AbilityVerb_lang(0) AbilityAllVerb_lang(1) ID(2) SkillLine(3) Spell(4) MinSkillLineRank(5) ClassMask(6) SupercedesSpell(7) AcquireMethod(8)
        // TrivialSkillLineRankHigh(9) TrivialSkillLineRankLow(10) Flags(11) NumSkillUps(12) ... RaceMask[2](17: low and high 32 bits, 0 = every race)
        public static readonly Def SkillLineAbility = D("SkillLineAbility", 1266278, 0xFF4446F6, 0x224F7EA0);

        // ---- factions
        // Faction: Name_lang(0) Description_lang(1) ReputationIndex(2) ParentFactionID(3) Expansion(4) FriendshipRepID(5) Flags(6) ...
        public static readonly Def Faction = D("Faction", 1361972, 0x06F624D7, 0x6D443C38);
        // FactionTemplate: Faction(0) Flags(1) FactionGroup(2) FriendGroup(3) EnemyGroup(4) Enemies[8](5) Friend[8](6)
        public static readonly Def FactionTemplate = D("FactionTemplate", 1361579, 0x1018ADD5, 0x22B6DC22);

        // ---- item details
        // ItemArmorTotal: ItemLevel(0) Cloth(1) Leather(2) Mail(3) Plate(4) (floats) ; ItemArmorQuality: Qualitymod[7](0) ; ItemArmorShield: Quality[7](0) ItemLevel(1)
        // ArmorLocation: Clothmodifier(0) Leathermodifier(1) Chainmodifier(2) Platemodifier(3) Modifier(4)  (id = inventory type)
        public static readonly Def ArmorTotal = D("ItemArmorTotal", 1283022, 0x029D50FE, 0xEB155D51);
        public static readonly Def ArmorQuality = D("ItemArmorQuality", 1283021, 0xB94A5807, 0x2935AA9D);
        public static readonly Def ArmorShield = D("ItemArmorShield", 1277741, 0xFFBB8DC2, 0x7E6C94F9);
        public static readonly Def ArmorLocation = D("ArmorLocation", 1284818, 0xB1911544, 0xFB67352F);
        // ItemDamage*: ItemLevel(0) Quality[7](1) (floats = damage per second per quality); the one-hand / two-hand / ammo tables have the layout 56F30531, ranged / thrown / wand 3CD722C1
        public static readonly Def DmgOneHand = D("ItemDamageOneHand", 1277743, 0xE0A6BB08, 0x56F30531);
        public static readonly Def DmgOneHandCaster = D("ItemDamageOneHandCaster", 1277739, 0xF2972767, 0x56F30531);
        public static readonly Def DmgTwoHand = D("ItemDamageTwoHand", 1277738, 0xD8C5FD43, 0x56F30531);
        public static readonly Def DmgTwoHandCaster = D("ItemDamageTwoHandCaster", 1277742, 0x80A1A0FA, 0x56F30531);
        public static readonly Def DmgAmmo = D("ItemDamageAmmo", 1277740, 0x9790C2EE, 0x56F30531);
        public static readonly Def DmgRanged = D("ItemDamageRanged", 6156256, 0x30E32F7A, 0x3CD722C1);
        public static readonly Def DmgThrown = D("ItemDamageThrown", 6156257, 0x72FD7ADB, 0x3CD722C1);
        public static readonly Def DmgWand = D("ItemDamageWand", 6156258, 0xC2C866AF, 0x3CD722C1);
        // ItemEffect: LegacySlotIndex(0) TriggerType(1) Charges(2) CoolDownMSec(3) CategoryCoolDownMSec(4) SpellCategoryID(5) SpellID(6) ChrSpecializationID(7) PlayerConditionID(8)
        // ItemXItemEffect: ItemEffectID(0); ItemID = the relationship column
        public static readonly Def ItemEffect = D("ItemEffect", 969941, 0x4002A5B1, 0x4CA77678);
        public static readonly Def ItemXItemEffect = D("ItemXItemEffect", 3177687, 0x00CB674F, 0x96F083AD);
        // ItemSet: Name_lang(0) SetFlags(1) RequiredSkill(2) RequiredSkillRank(3) ItemID[17](4) ; ItemSetSpell: ChrSpecID(0) SpellID(1) TraitSubTreeID(2) Threshold(3); ItemSetID = the relationship column
        public static readonly Def ItemSet = D("ItemSet", 1343609, 0x8E741A98, 0xF79068A4);
        public static readonly Def ItemSetSpell = D("ItemSetSpell", 1314689, 0x8645BB79, 0x2666A73F);

        public static readonly Def[] All =
        {
            Spell, SpellName, SpellMisc, SpellEffect, SpellReagents, SpellDuration, SpellCastTimes, SpellRadius, SpellRange, SkillLine, SkillLineAbility,
            Faction, FactionTemplate, ArmorTotal, ArmorQuality, ArmorShield, ArmorLocation,
            DmgOneHand, DmgOneHandCaster, DmgTwoHand, DmgTwoHandCaster, DmgAmmo, DmgRanged, DmgThrown, DmgWand,
            ItemEffect, ItemXItemEffect, ItemSet, ItemSetSpell,
        };

        // what a table gave: a reader (or null) and the reason it is not usable
        public sealed class Opened { public Wdc5 Table; public string Problem; public uint Layout; public bool Ok => Table != null; }

        static string Hex(uint v) => v.ToString("X8", CultureInfo.InvariantCulture);

        // parse one table and check that it is the table and the layout this version was written for
        public static Opened Open(byte[] data, Def def)
        {
            if (data == null) return new Opened { Problem = def.Name + ".db2 is not in this game install" };
            Wdc5 w;
            try { w = Wdc5.Parse(data); }
            catch (Exception e) { return new Opened { Problem = def.Name + ".db2 could not be read: " + e.Message }; }
            if (w.TableHash != def.TableHash) return new Opened { Problem = def.Name + ".db2 is not the table this version expects", Layout = w.LayoutHash };
            if (!def.Layouts.Contains(w.LayoutHash)) return new Opened { Problem = "game updated: " + def.Name + " layout " + Hex(w.LayoutHash) + " is not known to this Outpost yet", Layout = w.LayoutHash };
            return new Opened { Table = w, Layout = w.LayoutHash };
        }

        // a float field (bit pattern stored as a 32-bit number)
        public static float Flt(Wdc5 t, int index, int field, int element = 0) => Wdc5.AsFloat(t.Field(index, field, element));
        public static float FltArr(Wdc5 t, int index, int field, int element, int len) => Wdc5.AsFloat(t.FieldArr(index, field, element, len));
        public static int Int(Wdc5 t, int index, int field, int element = 0) => unchecked((int)(uint)t.Field(index, field, element));
        public static int IntArr(Wdc5 t, int index, int field, int element, int len) => unchecked((int)(uint)t.FieldArr(index, field, element, len));
    }
}
