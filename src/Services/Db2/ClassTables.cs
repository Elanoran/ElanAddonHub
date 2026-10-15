using System.Collections.Generic;

namespace ElansAddonHub.Services.Db2
{
    // Where the class / race tables sit in ONE client build (same rule as SpellTables / TalentTables: the layout hash is written in the file by the game,
    // a hash this version does not know makes THAT table unavailable, nothing is guessed). ChrClasses is the talent layer's definition (shared).
    //
    // Source of every number: wowdev/WoWDBDefs definitions/*.dbd, the LAYOUT blocks that list build 1.60.1.70338 (WoW Forever), checked against the real files of that
    // build (table and layout hashes read from the files, field counts compared with the .dbd, values spot-checked: 9 classes, 58 race rows of which 12 playable ...).
    // Field numbers count the STORED columns in the .dbd's order (a "noninline" id and a "noninline relation" column are not fields: the id list / relationship section).
    public static class ClassTables
    {
        static SpellTables.Def D(string name, int fileId, uint table, uint layout) => new SpellTables.Def { Name = name, FileId = fileId, TableHash = table, Layouts = new[] { layout } };

        // ChrClasses (talent layer): Name_lang(0) Filename(1) Name_male(2) Name_female(3) PetNameToken(4) Description_lang(5) RoleInfoString(6) DisabledString(7) Hyphenated(8,9)
        // CreateScreenFileDataID(10) SelectScreenFileDataID(11) IconFileDataID(12) LowResScreenFileDataID(13) Flags(14) StartingLevel(15) SpellTextureBlobFileDataID(16) ArmorTypeMask(17)
        // Field_9_0_1(18) visual fallbacks(19-26) CinematicSequenceID(27) DefaultSpec(28) ID(29) HasStrengthAttackBonus(30) PrimaryStatPriority(31) DisplayPower(32)
        // RangedAttackPowerPerAgility(33) AttackPowerPerAgility(34) AttackPowerPerStrength(35) SpellClassSet(36) ClassColorR(37) G(38) B(39) RolesMask(40) DamageBonusStat(41) HasRelicSlot(42)
        public static readonly SpellTables.Def Classes = TalentTables.Classes;
        public const int ClsName = 0, ClsFile = 1, ClsDescription = 5, ClsIcon = 12, ClsFlags = 14, ClsStartLevel = 15, ClsArmorMask = 17, ClsDisplayPower = 32, ClsColorR = 37, ClsColorG = 38, ClsColorB = 39, ClsRoles = 40;

        // ChrRaces (the id is a "noninline" column): ClientPrefix(0) ClientFileString(1) Name_lang(2) Name_female(3) Name_lowercase(4) Name_female_lowercase(5) Lore_name(6..9) LoreDescription(10)
        // Short_name(11..14) Flags(15) FactionID(16) CinematicSequenceID(17) ResSicknessSpellID(18) SplashSoundID(19) CreateScreenFileDataID(20) SelectScreenFileDataID(21) LowResScreenFileDataID(22)
        // AlteredFormStartVisualKitID[3](23) AlteredFormFinishVisualKitID[3](24) HeritageArmorAchievementID(25) StartingLevel(26) UiDisplayOrder(27) PlayableRaceBit(28)
        // TransmogrifyDisabledSlotMask(29) AlteredFormCustomizeOffsetFallback[3](30) AlteredFormCustomizeRotationFallback(31) Field_9_1_0_38312_030[3](32) _031[3](33)
        // BaseLanguage(34) CreatureType(35) Alliance(36: 0 Alliance, 1 Horde, 2 neutral) Race_related(37) UnalteredVisualRaceID(38) DefaultClassID(39) ...
        public static readonly SpellTables.Def Races = D("ChrRaces", 1305311, 0x53F1783C, 0x4F44C796);
        public const int RaceClientFile = 1, RaceName = 2, RaceLoreName = 6, RaceLoreDescription = 10, RaceFlags = 15, RaceFaction = 16, RaceStartLevel = 26, RaceOrder = 27, RaceBit = 28, RaceLanguage = 34, RaceAlliance = 36, RaceDefaultClass = 39;

        // CharBaseInfo: RaceID(0) ClassID(1) OtherFactionRaceID(2): the race x class pairs the client lets a player create. The id is not a field.
        public static readonly SpellTables.Def CharBase = D("CharBaseInfo", 1343386, 0x3067A8F8, 0xFEC46FB7);

        // ChrClassesXPowerTypes: PowerType(0); ClassID is the relationship column. (PowerType 0 mana, 1 rage, 2 focus, 3 energy, 4 combo points)
        public static readonly SpellTables.Def ClassPower = D("ChrClassesXPowerTypes", 1121420, 0xC0315ACF, 0x70DA1F8C);

        // SkillRaceClassInfo (this client keeps SkillID inline): SkillID(0) ClassMask(1) Flags(2) Availability(3) MinLevel(4) SkillTierID(5) RaceMasks[2](6) (two 32-bit halves of the race bit mask)
        public static readonly SpellTables.Def SkillRaceClass = D("SkillRaceClassInfo", 1240406, 0x06ADE420, 0x24277B48);
        public const int SrSkill = 0, SrClassMask = 1, SrFlags = 2, SrAvailability = 3, SrMinLevel = 4, SrRaces = 6;

        // SpellLevels: DifficultyID(0) MaxLevel(1) MaxPassiveAuraLevel(2) BaseLevel(3) SpellLevel(4); SpellID is the relationship column
        public static readonly SpellTables.Def SpellLevels = D("SpellLevels", 1140079, 0x1DDEC5E6, 0x7EB86FDC);
        public const int LvDifficulty = 0, LvBase = 3, LvSpell = 4;

        // SpellPower: ID(0) OrderIndex(1) ManaCost(2) ManaCostPerLevel(3) ManaPerSecond(4) PowerDisplayID(5) AltPowerBarID(6) PowerCostPct(7: float) PowerCostMaxPct(8) OptionalCostPct(9)
        // PowerPctPerSecond(10) PowerType(11: signed, -2 = health) RequiredAuraSpellID(12) OptionalCost(13); SpellID is the relationship column
        public static readonly SpellTables.Def SpellPower = D("SpellPower", 982806, 0xA1ACE1DF, 0x61AD223F);
        public const int PwOrder = 1, PwCost = 2, PwPct = 7, PwType = 11;

        // SpellCooldowns: DifficultyID(0) CategoryRecoveryTime(1) RecoveryTime(2) StartRecoveryTime(3: the global cooldown) AuraSpellID(4); SpellID is the relationship column
        public static readonly SpellTables.Def SpellCooldowns = D("SpellCooldowns", 1139924, 0xF9F37C57, 0xDC945B8C);
        public const int CdDifficulty = 0, CdCategory = 1, CdRecovery = 2, CdStart = 3;

        // classes, races and the pairs are needed for the class and race pages; the spell tables only improve the spell pages (levels, cost, cooldown)
        public static readonly SpellTables.Def[] Required = { Classes, Races, CharBase };
        public static readonly SpellTables.Def[] Optional = { ClassPower, SkillRaceClass, SpellLevels, SpellPower, SpellCooldowns };

        public static SpellTables.Def[] All
        {
            get
            {
                var l = new List<SpellTables.Def>(Required);
                l.AddRange(Optional);
                return l.ToArray();
            }
        }
    }
}
