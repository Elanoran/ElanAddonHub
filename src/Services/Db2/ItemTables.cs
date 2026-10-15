using System;
using System.Collections.Generic;
using System.Linq;

namespace ElansAddonHub.Services.Db2
{
    // Where each field we use sits in the item tables of ONE client build. Keyed by the table's layout hash: the game writes the hash into the
    // file, so a patch that changes a table shows up as an unknown hash and nothing is guessed (ItemData then says "unavailable: game updated").
    //
    // Source of every number below: wowdev/WoWDBDefs, definitions/ItemSparse.dbd, Item.dbd and RandPropPoints.dbd, the LAYOUT blocks that list
    // build 1.60.1.70338 (WoW Forever): ItemSparse 6FCC3191, Item 9A2A4834, RandPropPoints 4FD22743. Checked against the real files of that build
    // (Thunderfury: level 80, epic, requires 60, +5 Agility +8 Stamina; Rough Arrow, Hearthstone, Linen Cloth, 7 hidden items from DBCache.bin).
    public static class ItemTables
    {
        public const uint ItemSparseHash = 0x919BE54E, ItemHash = 0x50238EC2, RandPropPointsHash = 0x5D8CEFA6;
        public const int ItemSparseFileDataId = 1572924, ItemFileDataId = 841626, RandPropPointsFileDataId = 1310245;

        // ItemSparse is a "sparse" table: every record holds five strings (Description, Display3, Display2, Display1, Display = the name) inline,
        // then the fixed fields. Offsets are counted from the first byte AFTER the fifth string. ID is not stored in the record.
        // Field order and widths from the .dbd: ExpansionID@0 DmgVariance@4 LimitCategory@8 DurationInInventory@12 QualityModifier@16 BagFamily@20
        // StartQuestID@24 LanguageID@28 ItemRange@32 StatPercentageOfSocket[10]@36 StatPercentEditor[10]@76 StatModifier_bonusStat[10]@116
        // Stackable@156 MaxCount@160 MinReputation@164 RequiredAbility@168 AllowableRace[2]@172 SellPrice@180 BuyPrice@184 VendorStackCount@188
        // PriceVariance@192 PriceRandomValue@196 Flags[5]@200 (then 7 int32 up to 248) u16 block from 248: ItemNameDescriptionID ...
        // ItemDelay@272 MinFactionID@274 RequiredSkillRank@276 RequiredSkill@278 ItemLevel@280 AllowableClass@282, u8 block from 284:
        // ArtifactID SpellWeight SpellWeightCategory SocketType[3] SheatheType Material PageMaterialID Bonding@293 DamageType ContainerSlots@295
        // RequiredPVPMedal RequiredPVPRank RequiredLevel@298 InventoryType@299 OverallQualityID@300 AmmunitionType@301
        public sealed class SparseLayout
        {
            public uint LayoutHash;
            public string Build;
            public int Strings = 5, NameString = 4, DescriptionString = 0;
            public int MinFixed;                                       // bytes the fixed part must have (record shorter than this = skipped)
            public int StatValue, StatType, StatSlots;                 // StatPercentEditor[], StatModifier_bonusStat[]
            public int Stackable, SellPrice, BuyPrice, ItemDelay, ItemLevel, Bonding, ContainerSlots, RequiredLevel, InventoryType, Quality;
            public int DmgVariance = -1, Flags2 = -1, ItemSet = -1;   // float damage spread, Flags[1] (0x200 = caster weapon), item set id (optional: -1 = this layout does not say)
            // who may use the item (optional like the three above): MinReputation int32, AllowableRace int64, MinFactionID u16, RequiredSkillRank u16, RequiredSkill u16, AllowableClass int16, RequiredPVPRank u8
            public int MinReputation = -1, AllowableRace = -1, MinFaction = -1, RequiredSkillRank = -1, RequiredSkill = -1, AllowableClass = -1, RequiredPvpRank = -1;
        }

        public static readonly SparseLayout[] SparseLayouts =
        {
            new SparseLayout
            {
                LayoutHash = 0x6FCC3191, Build = "1.60.1.70338", MinFixed = 302,
                StatValue = 76, StatType = 116, StatSlots = 10, Stackable = 156, SellPrice = 180, BuyPrice = 184, ItemDelay = 272, ItemLevel = 280,
                Bonding = 293, ContainerSlots = 295, RequiredLevel = 298, InventoryType = 299, Quality = 300,
                DmgVariance = 4, Flags2 = 204, ItemSet = 266,
                MinReputation = 164, AllowableRace = 172, MinFaction = 274, RequiredSkillRank = 276, RequiredSkill = 278, AllowableClass = 282, RequiredPvpRank = 297,
            },
        };

        public static SparseLayout SparseFor(uint layoutHash) => SparseLayouts.FirstOrDefault(l => l.LayoutHash == layoutHash);

        // Item.db2 (class, subclass, inventory type, icon) as a table with fields; the field numbers count the stored columns (ID is the id list).
        // Layout 9A2A4834: ClassID(0) SubclassID(1) Material(2) InventoryType(3) SheatheType(4) ItemPetFoodID(5) Sound_override_subclassID(6)
        // IconFileDataID(7) ... The server's hotfix rows for this table (DBCache.bin) are the same fields at their natural widths:
        // ClassID int32@0, SubclassID u8@4, InventoryType u8@6, IconFileDataID int32@13 (row length 43).
        public sealed class ItemLayout { public uint LayoutHash; public int ClassField, SubclassField, InventoryField, IconField, CacheRowLength; }
        public static readonly ItemLayout[] ItemLayouts = { new ItemLayout { LayoutHash = 0x9A2A4834, ClassField = 0, SubclassField = 1, InventoryField = 3, IconField = 7, CacheRowLength = 43 } };
        public static ItemLayout ItemFor(uint layoutHash) => ItemLayouts.FirstOrDefault(l => l.LayoutHash == layoutHash);

        // RandPropPoints 4FD22743: the item-level budgets. One record per item level (id = level), fields 7/8/9 = Epic[5], Superior[5], Good[5]
        // (whole numbers; fields 4-6 are the same as floats). The stat amounts of an item are (budget x StatPercentEditor / 10000).
        public sealed class BudgetLayout { public uint LayoutHash; public int Epic, Superior, Good, Count; }
        public static readonly BudgetLayout[] BudgetLayouts = { new BudgetLayout { LayoutHash = 0x4FD22743, Epic = 7, Superior = 8, Good = 9, Count = 5 } };
        public static BudgetLayout BudgetFor(uint layoutHash) => BudgetLayouts.FirstOrDefault(l => l.LayoutHash == layoutHash);

        public sealed class Budgets
        {
            public readonly Dictionary<int, int[]> ByLevel = new Dictionary<int, int[]>();   // item level -> Epic[5], Superior[5], Good[5]

            public static Budgets From(Wdc5 t, BudgetLayout l)
            {
                var b = new Budgets();
                for (int i = 0; i < t.Count; i++)
                {
                    var a = new int[15];
                    for (int k = 0; k < l.Count; k++)
                    {
                        a[k] = (int)t.Field(i, l.Epic, k);
                        a[5 + k] = (int)t.Field(i, l.Superior, k);
                        a[10 + k] = (int)t.Field(i, l.Good, k);
                    }
                    b.ByLevel[t.IdAt(i)] = a;
                }
                return b;
            }

            // which of the five budget columns an item uses (the slot group)
            public static int Column(int inventoryType, int classId, int subclassId)
            {
                switch (inventoryType)
                {
                    case 1: case 4: case 5: case 7: case 15: case 17: case 20: case 25: return 0;     // head body chest legs ranged two-hand robe thrown
                    case 26: return classId == 2 && subclassId == 19 ? 3 : 0;                          // ranged: a wand uses the one-hand budget
                    case 13: case 21: case 22: return 3;                                               // one-hand main hand off hand
                    case 3: case 6: case 8: case 10: case 12: return 1;                                // shoulders waist feet hands trinket
                    case 2: case 9: case 11: case 14: case 16: case 23: return 2;                      // neck wrist finger shield back held in off-hand
                    case 28: return 4;                                                                 // relic
                    default: return -1;
                }
            }

            public int Budget(int itemLevel, int quality, int inventoryType, int classId, int subclassId)
            {
                int col = Column(inventoryType, classId, subclassId);
                if (col < 0 || !ByLevel.TryGetValue(itemLevel, out var a)) return -1;
                int row = quality >= 4 ? 0 : quality == 3 ? 5 : 10;
                return a[row + col];
            }
        }

        // ---- reading one ItemSparse record (the same bytes in the shipped table and in the hotfix cache)
        public static ClientItem ParseSparse(int id, byte[] a, int offset, int count, SparseLayout L, Budgets budgets, string source)
        {
            int end = offset + count, q = offset;
            string name = null, desc = null;
            for (int s = 0; s < L.Strings; s++)
            {
                int e = q;
                while (e < end && a[e] != 0) e++;
                if (e >= end) return null;                       // a string without its end: not a record we understand
                var t = e == q ? "" : System.Text.Encoding.UTF8.GetString(a, q, e - q);
                if (s == L.NameString) name = t;
                if (s == L.DescriptionString) desc = t;
                q = e + 1;
            }
            int f = q;
            if (end - f < L.MinFixed) return null;
            var it = new ClientItem
            {
                Id = id, Name = (name ?? "").Trim(), Description = (desc ?? "").Trim(), Source = source,
                Quality = a[f + L.Quality], ItemLevel = BitConverter.ToUInt16(a, f + L.ItemLevel), RequiredLevel = (sbyte)a[f + L.RequiredLevel],
                InventoryType = a[f + L.InventoryType], Bonding = a[f + L.Bonding], ContainerSlots = a[f + L.ContainerSlots],
                Stackable = BitConverter.ToInt32(a, f + L.Stackable), DelayMs = BitConverter.ToUInt16(a, f + L.ItemDelay),
                SellPrice = BitConverter.ToUInt32(a, f + L.SellPrice), BuyPrice = BitConverter.ToUInt32(a, f + L.BuyPrice),
            };
            if (it.RequiredLevel < 0) it.RequiredLevel = 0;
            if (L.DmgVariance >= 0) it.DmgVariance = BitConverter.ToSingle(a, f + L.DmgVariance);
            if (L.Flags2 >= 0) it.Flags2 = BitConverter.ToInt32(a, f + L.Flags2);
            if (L.ItemSet >= 0) it.ItemSetId = BitConverter.ToUInt16(a, f + L.ItemSet);
            if (L.AllowableClass >= 0) it.AllowableClass = BitConverter.ToInt16(a, f + L.AllowableClass);
            if (L.AllowableRace >= 0) it.AllowableRace = BitConverter.ToInt64(a, f + L.AllowableRace);
            if (L.RequiredSkill >= 0) it.RequiredSkill = BitConverter.ToUInt16(a, f + L.RequiredSkill);
            if (L.RequiredSkillRank >= 0) it.RequiredSkillRank = BitConverter.ToUInt16(a, f + L.RequiredSkillRank);
            if (L.MinFaction >= 0) it.MinFactionId = BitConverter.ToUInt16(a, f + L.MinFaction);
            if (L.MinReputation >= 0) it.MinReputation = BitConverter.ToInt32(a, f + L.MinReputation);
            if (L.RequiredPvpRank >= 0) it.RequiredPvpRank = a[f + L.RequiredPvpRank];
            var raw = new List<int>();
            for (int i = 0; i < L.StatSlots; i++)
            {
                int pct = BitConverter.ToInt32(a, f + L.StatValue + i * 4);
                int type = BitConverter.ToInt32(a, f + L.StatType + i * 4);
                if (pct == 0 || type < 0) continue;
                raw.Add(type); raw.Add(pct);
            }

            // classes are filled in later (they come from another table), so the amounts are worked out by ResolveStats
            it.StatsRaw = raw.Count == 0 ? null : raw.ToArray();
            return it;
        }

        // the stat amounts of an item: budget(item level, quality, slot group) x share / 10000, rounded. Without a budget the amounts stay unknown (null)
        public static void ResolveStats(ClientItem it, Budgets budgets)
        {
            var raw = it.StatsRaw;
            it.StatsRaw = null;
            if (raw == null) { it.Stats = ClientItem.NoStats; return; }
            int budget = budgets == null ? -1 : budgets.Budget(it.ItemLevel, it.Quality, it.InventoryType, it.ClassId, it.SubclassId);
            if (budget < 0) { it.Stats = null; return; }
            var st = new int[raw.Length];
            for (int i = 0; i + 1 < raw.Length; i += 2)
            {
                st[i] = raw[i];
                st[i + 1] = (int)Math.Round(budget * (double)raw[i + 1] / 10000.0, MidpointRounding.AwayFromZero);
            }
            it.Stats = st;
        }
    }
}
