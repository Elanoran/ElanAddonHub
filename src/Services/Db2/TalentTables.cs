using System;

namespace ElansAddonHub.Services.Db2
{
    // Where the talent (trait) tables sit in ONE client build. Same rule as SpellTables / ItemTables: the game writes each table's layout hash into the file,
    // the Outpost knows the layouts of the build it was written against, and a hash it does not know makes THAT table unavailable (nothing is guessed).
    // Required tables missing/unknown = the talent layer is unavailable ("game updated"); optional ones only drop their feature.
    //
    // Source of every number: wowdev/WoWDBDefs definitions/*.dbd, LAYOUT blocks that list build 1.60.1.70338 (WoW Forever), checked against the real files of
    // that build (table hashes read from the files). Field numbers count ALL columns of the .dbd in order, the id and the (inline) relation columns included
    // (these client files store them in the record); only a "noninline" id is not a field.
    public static class TalentTables
    {
        static SpellTables.Def D(string name, int fileId, uint table, uint layout) => new SpellTables.Def { Name = name, FileId = fileId, TableHash = table, Layouts = new[] { layout } };

        // TraitTree: TitleText_lang(0) ID(1) TraitSystemID(2) TraitTreeID(3) FirstTraitNodeID(4) PlayerConditionID(5) Flags(6) ...
        public static readonly SpellTables.Def Tree = D("TraitTree", 4420305, 0x964B11D9, 0xFBCDBA8F);
        public const int TreeId = 1, TreeSystem = 2, TreeFlags = 6;
        // TraitTreeXTraitCurrency: ID(0) Index(1) TraitTreeID(2) TraitCurrencyID(3)
        public static readonly SpellTables.Def TreeCurrency = D("TraitTreeXTraitCurrency", 4524218, 0x373BEE7B, 0xE093CD62);
        // TraitTreeXTraitCost: ID(0) TraitTreeID(1) TraitCostID(2) ; TraitCost: InternalName(0) ID(1) Amount(2) TraitCurrencyID(3) CurveID(4)
        public static readonly SpellTables.Def TreeCost = D("TraitTreeXTraitCost", 4420306, 0x53A96E5F, 0x19EEFE95);
        public static readonly SpellTables.Def Cost = D("TraitCost", 4420295, 0x266BF08E, 0x70C65F17);
        // TraitCurrency: ID(0) Type(1) CurrencyTypesID(2) Flags(3) Icon(4) PlayerDataElementAccountID(5) PlayerDataElementCharacterID(6) SourcedMax(7)
        public static readonly SpellTables.Def Currency = D("TraitCurrency", 4524216, 0x837C91E2, 0xA8B0874B);
        // TraitCurrencySource: Requirement_lang(0) ID(1) TraitCurrencyID(2) Amount(3) QuestID(4) AchievementID(5) PlayerLevel(6) TraitNodeEntryID(7) SuperDistrictSetID(8) OrderIndex(9)
        public static readonly SpellTables.Def CurrencySource = D("TraitCurrencySource", 4539393, 0x94A4ED3B, 0x4C49B6AA);
        // TraitNode: ID(0) TraitTreeID(1) PosX(2) PosY(3) Type(4) Flags(5) TraitSubTreeID(6)
        public static readonly SpellTables.Def Node = D("TraitNode", 4420297, 0xE1432D63, 0xA01A518A);
        // TraitNodeXTraitNodeEntry: ID(0) TraitNodeID(1) TraitNodeEntryID(2) Index(3)
        public static readonly SpellTables.Def NodeEntryLink = D("TraitNodeXTraitNodeEntry", 4420304, 0xA4379E4E, 0x802ED455);
        // TraitNodeEntry: ID(0) TraitDefinitionID(1) MaxRanks(2) NodeEntryType(3) TraitSubTreeID(4)
        public static readonly SpellTables.Def NodeEntry = D("TraitNodeEntry", 4420298, 0x82E8D5D6, 0x733B015D);
        // TraitDefinition: OverrideName_lang(0) OverrideSubtext_lang(1) OverrideDescription_lang(2) ID(3) SpellID(4) OverrideIcon(5) OverridesSpellID(6) VisibleSpellID(7)
        public static readonly SpellTables.Def Definition = D("TraitDefinition", 4420327, 0xB292AC80, 0x060A9F4C);
        // TraitDefinitionEffectPoints: ID(0) TraitDefinitionID(1) EffectIndex(2) OperationType(3) CurveID(4)   (rank n of a talent = the curve's value at n)
        public static readonly SpellTables.Def EffectPoints = D("TraitDefinitionEffectPoints", 4536895, 0x16D47A01, 0x7E18C372);
        // CurvePoint: Pos[2](0) PosPreSquish[2](1) ID(2) CurveID(3) OrderIndex(4)
        public static readonly SpellTables.Def CurvePoint = D("CurvePoint", 892586, 0x700ECA3A, 0x7D4954A1);
        // TraitEdge: ID(0) VisualStyle(1) LeftTraitNodeID(2) RightTraitNodeID(3) Type(4)
        public static readonly SpellTables.Def Edge = D("TraitEdge", 4420308, 0x94D0AF81, 0x907C9B77);
        // TraitNodeGroup: ID(0) TraitTreeID(1) Flags(2) ; TraitNodeGroupXTraitNode: ID(0) TraitNodeGroupID(1) TraitNodeID(2) Index(3)
        public static readonly SpellTables.Def Group = D("TraitNodeGroup", 4420300, 0x601B98C1, 0xECA626BE);
        public static readonly SpellTables.Def GroupNode = D("TraitNodeGroupXTraitNode", 4420302, 0x1AA6B3DB, 0xB9288EB8);
        // TraitCond: ID(0) CondType(1) TraitTreeID(2) GrantedRanks(3) QuestID(4) AchievementID(5) SpecSetID(6) TraitNodeGroupID(7) TraitNodeID(8) TraitNodeEntryID(9)
        // TraitCurrencyID(10) SpentAmountRequired(11) Flags(12) RequiredLevel(13) FreeSharedStringID(14) SpendMoreSharedStringID(15) TraitCondAccountElementID(16)
        public static readonly SpellTables.Def Cond = D("TraitCond", 4543085, 0x8D6A552D, 0xBFDD1994);
        // TraitNodeGroupXTraitCond: ID(0) TraitCondID(1) TraitNodeGroupID(2) ; TraitNodeXTraitCond: ID(0) TraitCondID(1) TraitNodeID(2)
        public static readonly SpellTables.Def GroupCond = D("TraitNodeGroupXTraitCond", 4543090, 0xBDC77325, 0x3F06B639);
        public static readonly SpellTables.Def NodeCond = D("TraitNodeXTraitCond", 4543092, 0x8CCCDE25, 0xE2160AA8);
        // SkillLineXTraitTree: SkillLineID(0) TraitTreeID(1) Variant(2)   (the id is not stored)
        public static readonly SpellTables.Def SkillTree = D("SkillLineXTraitTree", 4505477, 0xA6E64E40, 0x853B88D6);
        // ChrClasses: Name_lang(0) Filename(1) ... IconFileDataID(12) ... DefaultSpec(28) ID(29, stored)
        public static readonly SpellTables.Def Classes = D("ChrClasses", 1361031, 0xF5889D8C, 0xAFC9B0C2);
        public const int ClassName = 0, ClassFile = 1;

        // all of them are needed for a usable tree layer ...
        public static readonly SpellTables.Def[] Required =
        {
            Tree, Node, NodeEntryLink, NodeEntry, Definition, Edge, Group, GroupNode, Cond, GroupCond, SkillTree, Classes,
            SpellTables.SkillLine, SpellTables.SkillLineAbility,
        };
        // ... these only improve it (without them: no rank-aware text, no point sources, no per-rank costs, no node conditions)
        public static readonly SpellTables.Def[] Optional = { TreeCurrency, TreeCost, Cost, Currency, CurrencySource, EffectPoints, CurvePoint, NodeCond };

        public static SpellTables.Def[] All
        {
            get
            {
                var a = new SpellTables.Def[Required.Length + Optional.Length];
                Array.Copy(Required, a, Required.Length); Array.Copy(Optional, 0, a, Required.Length, Optional.Length);
                return a;
            }
        }
    }
}
