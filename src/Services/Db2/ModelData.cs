using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Atlas.Classic;

namespace ElansAddonHub.Services.Db2
{
    // ============================================================================================================================
    // 3.6: Atlas 3D models, step 1 - the data. Which model a creature wears, read from the player's OWN WoW install, read-only, in the background,
    // exactly like SpellData / TalentData:
    //   * CreatureDisplayInfo (display id -> model row, scale, the three monster skins, the "character model" link) and CreatureModelData (model row ->
    //     the .m2 FileDataID, its own scale) come out of the local CASC storage (ModelTables lists them and the layouts this version knows)
    //   * the parsed result is kept in the Outpost's data folder (cache\modeldata.bin), keyed by the game build
    //   * a table with a layout this version does not know is unavailable on its own (a reason, never a guess)
    // The creature's display ids come from what the client saw (SeenData: creaturecache.wdb keeps the FIRST display only), else the Classic baseline
    // (creature_template DisplayId1..4 with their chances). Nothing here touches the network. Nothing starts the service yet: the viewer (step 3) will,
    // on the same off switch as the item data.
    // 3.7: the item models too (weapons, shields, held in the off hand): ItemModifiedAppearance -> ItemAppearance -> ItemDisplayInfo -> ModelFileData /
    // TextureFileData, read with the creature tables into the same index (format 2). ForItem gives an item's model file and skin.
    // ============================================================================================================================
    public static class ModelTables
    {
        static SpellTables.Def D(string name, int fileId, uint table, uint layout) => new SpellTables.Def { Name = name, FileId = fileId, TableHash = table, Layouts = new[] { layout }, Build = "1.60.1.70707" };

        // Source: wowdev/WoWDBDefs definitions/*.dbd, the LAYOUT blocks that list 1.60.1.70707 (WoW Forever), checked against the real files of that build
        // (table hash, layout hash and field count read from the WDC5 headers: 28 and 34 fields). Fields counted as Wdc5 does: a $noninline$ column is
        // not a field, an array is one field.
        // CreatureDisplayInfo (layout 7275F5F6, the id is stored): ID(0) ModelID(1) SoundID(2) SizeClass(3) CreatureModelScale(4) CreatureModelAlpha(5) BloodID(6)
        // ExtendedDisplayInfoID(7) NPCSoundID(8) ... ConditionalCreatureModelID(24) MountMaxBankingAngle(25) Field_11_0_0_54210_026(26) TextureVariationFileDataID[4](27)
        public static readonly SpellTables.Def DisplayInfo = D("CreatureDisplayInfo", 1108759, 0xBFDAF9F1, 0x7275F5F6);
        public const int DiModel = 1, DiScale = 4, DiExtended = 7, DiTextures = 27, TextureSlots = 4;
        // CreatureModelData (layout 0F5449F8, the id is not stored): GeoBox[6](0) Flags(1) FileDataID(2) WalkSpeed(3) ... CreatureGeosetDataID(18) HoverHeight(19)
        // AttachedEffectScale(20) ModelScale(21) ... MountScaleOther[2](33)
        public static readonly SpellTables.Def ModelInfo = D("CreatureModelData", 1365368, 0x7F695FA5, 0x0F5449F8);
        public const int CmFlags = 1, CmFile = 2, CmScale = 21;

        // 3.7: item models (weapons, shields, held in the off hand). Same source and checks (the WDC5 headers of 1.60.1.70707: the field counts 7, 5, 16, 5
        // and 3). Table hashes and FileDataIDs from WoWDBDefs' manifest.json.
        // ItemModifiedAppearance (layout 03A6C979, the id stored): ID(0) ItemID(1) ItemAppearanceModifierID(2) ItemAppearanceID(3) OrderIndex(4)
        // TransmogSourceTypeEnum(5) Flags(6)
        public static readonly SpellTables.Def ItemLooks = D("ItemModifiedAppearance", 982457, 0xE491AC55, 0x03A6C979);
        public const int ImaItem = 1, ImaModifier = 2, ImaAppearance = 3, ImaOrder = 4, ImaFields = 7;
        // ItemAppearance (layout 481C4281, the id not stored): DisplayType(0) ItemDisplayInfoID(1) DefaultIconFileDataID(2) UiOrder(3) TransmogPlayerConditionID(4)
        public static readonly SpellTables.Def Appearance = D("ItemAppearance", 982462, 0x42261B89, 0x481C4281);
        public const int IaDisplay = 1, IaIcon = 2, IaFields = 5;
        // ItemDisplayInfo (layout 9F3AB8A9, the id not stored): GeosetGroupOverride(0) ItemVisual(1) ParticleColorID(2) ItemRangedDisplayInfoID(3)
        // OverrideSwooshSoundKitID(4) SheatheTransformMatrixID(5) StateSpellVisualKitID(6) SheathedSpellVisualKitID(7) UnsheathedSpellVisualKitID(8) Flags(9)
        // ModelResourcesID[2](10) ModelMaterialResourcesID[2](11) ModelType[2](12) GeosetGroup[6](13) AttachmentGeosetGroup[6](14) HelmetGeosetVis[2](15)
        public static readonly SpellTables.Def ItemDisplay = D("ItemDisplayInfo", 1266429, 0x986F8CD0, 0x9F3AB8A9);
        public const int IdFlags = 9, IdModels = 10, IdMaterials = 11, IdModelType = 12, IdFields = 16;
        // ModelFileData (layout 2AE4E788, the id = the FileDataID, stored as field 1): GeoBox[6](0) FileDataID(1) Flags(2) LodCount(3) ModelResourcesID(4)
        public static readonly SpellTables.Def ModelFiles = D("ModelFileData", 1337833, 0xA161E42C, 0x2AE4E788);
        public const int MfFlags = 2, MfLods = 3, MfResource = 4, MfFields = 5;
        // TextureFileData (layout BD7C74C2, the id = the FileDataID, stored as field 0): FileDataID(0) UsageType(1) MaterialResourcesID(2)
        public static readonly SpellTables.Def TextureFiles = D("TextureFileData", 982459, 0x1A473014, 0xBD7C74C2);
        public const int TfUsage = 1, TfMaterial = 2, TfFields = 3;

        public static readonly SpellTables.Def[] All = { DisplayInfo, ModelInfo, ItemLooks, Appearance, ItemDisplay, ModelFiles, TextureFiles };
        public static readonly SpellTables.Def[] ItemTables = { ItemLooks, Appearance, ItemDisplay, ModelFiles, TextureFiles };

        // 3.7: the inventory types (Item.db2's InventoryType, ItemNames.Slot) whose items get a model card: 13 one-hand, 14 shield, 15 ranged (bow),
        // 17 two-hand, 21 main hand, 22 off hand, 23 held in the off hand, 25 thrown, 26 ranged right (gun, crossbow, wand). Armour, ammo, relics: none.
        public static readonly int[] ItemSlotsShown = { 13, 14, 15, 17, 21, 22, 23, 25, 26 };
        public static bool ItemSlotShown(int inventoryType) => Array.IndexOf(ItemSlotsShown, inventoryType) >= 0;
    }

    // 3.7: the item tables' rows the model card needs
    public struct ItemLook { public int Modifier, Appearance, Order; }        // one ItemModifiedAppearance row of an item
    public sealed class ItemDisplayRow { public int Id, Flags; public int[] Models = new int[2], Materials = new int[2], ModelType = new int[2]; }
    public struct ModelFileRow { public int FileId, Flags, Lods; }             // one ModelFileData row of a model resource (file order)
    public struct TextureFileRow { public int FileId, Usage; }                 // one TextureFileData row of a material resource (file order)

    // one CreatureDisplayInfo row: Textures = TextureVariationFileDataID[0..3], the skins a model's replaceable textures 11 / 12 / 13 take (0 = none)
    public sealed class CreatureDisplay { public int Id, ModelId, ExtendedId; public float Scale; public int[] Textures = new int[ModelTables.TextureSlots]; }
    // one CreatureModelData row
    public sealed class CreatureModelInfo { public int Id, FileId, Flags; public float Scale; }

    public sealed class ModelSet
    {
        public Dictionary<int, CreatureDisplay> Displays;          // null = the table is unavailable (Problems says why)
        public Dictionary<int, CreatureModelInfo> Models;
        // 3.7: items. Each null = that table is unavailable (Problems says why). Kept small: ModelFiles / TextureFiles only for the resources an
        // ItemDisplayInfo row names (the rest of the game's models and textures are not needed), each list in the table's own order.
        public Dictionary<int, List<ItemLook>> ItemLooks;           // ItemID -> its ItemModifiedAppearance rows
        public Dictionary<int, int> AppearanceDisplay;              // ItemAppearanceID -> ItemDisplayInfoID
        public Dictionary<int, ItemDisplayRow> ItemDisplays;
        public Dictionary<int, List<ModelFileRow>> ModelFiles;      // ModelResourcesID -> its ModelFileData rows
        public Dictionary<int, List<TextureFileRow>> TextureFiles;  // MaterialResourcesID -> its TextureFileData rows
        public readonly Dictionary<string, string> Problems = new Dictionary<string, string>();     // table name (or "all") -> why it is unavailable
        public readonly List<string> Notes = new List<string>();
        public readonly Dictionary<string, uint> LayoutsSeen = new Dictionary<string, uint>();

        public static ModelSet Parse(SpellRaw raw)
        {
            var s = new ModelSet();
            var di = SpellTables.Open(raw.Get(ModelTables.DisplayInfo), ModelTables.DisplayInfo);
            if (di.Layout != 0) s.LayoutsSeen[ModelTables.DisplayInfo.Name] = di.Layout;
            if (!di.Ok) s.Problems[ModelTables.DisplayInfo.Name] = di.Problem;
            else
            {
                var t = di.Table; s.Displays = new Dictionary<int, CreatureDisplay>(t.Count);
                for (int i = 0; i < t.Count; i++)
                {
                    var d = new CreatureDisplay { Id = t.IdAt(i), ModelId = SpellTables.Int(t, i, ModelTables.DiModel), Scale = SpellTables.Flt(t, i, ModelTables.DiScale), ExtendedId = SpellTables.Int(t, i, ModelTables.DiExtended) };
                    for (int k = 0; k < ModelTables.TextureSlots; k++) d.Textures[k] = SpellTables.IntArr(t, i, ModelTables.DiTextures, k, ModelTables.TextureSlots);
                    s.Displays[d.Id] = d;
                }
                if (t.EncryptedRecords > 0) s.Notes.Add(ModelTables.DisplayInfo.Name + ": " + t.EncryptedRecords + " rows are key-locked and not there");
            }
            var mi = SpellTables.Open(raw.Get(ModelTables.ModelInfo), ModelTables.ModelInfo);
            if (mi.Layout != 0) s.LayoutsSeen[ModelTables.ModelInfo.Name] = mi.Layout;
            if (!mi.Ok) s.Problems[ModelTables.ModelInfo.Name] = mi.Problem;
            else
            {
                var t = mi.Table; s.Models = new Dictionary<int, CreatureModelInfo>(t.Count);
                for (int i = 0; i < t.Count; i++)
                    s.Models[t.IdAt(i)] = new CreatureModelInfo { Id = t.IdAt(i), Flags = SpellTables.Int(t, i, ModelTables.CmFlags), FileId = SpellTables.Int(t, i, ModelTables.CmFile), Scale = SpellTables.Flt(t, i, ModelTables.CmScale) };
                if (t.EncryptedRecords > 0) s.Notes.Add(ModelTables.ModelInfo.Name + ": " + t.EncryptedRecords + " rows are key-locked and not there");
            }
            ParseItems(raw, s);
            if (s.Displays == null && s.Models == null && s.ItemLooks == null && s.AppearanceDisplay == null && s.ItemDisplays == null && s.ModelFiles == null && s.TextureFiles == null)
                s.Problems["all"] = s.Problems.Values.FirstOrDefault() ?? "no model table could be read";
            return s;
        }

        // 3.7: the item tables
        static Wdc5 OpenOne(SpellRaw raw, SpellTables.Def def, ModelSet s)
        {
            var o = SpellTables.Open(raw.Get(def), def);
            if (o.Layout != 0) s.LayoutsSeen[def.Name] = o.Layout;
            if (!o.Ok) { s.Problems[def.Name] = o.Problem; return null; }
            if (o.Table.EncryptedRecords > 0) s.Notes.Add(def.Name + ": " + o.Table.EncryptedRecords + " rows are key-locked and not there");
            return o.Table;
        }

        static void ParseItems(SpellRaw raw, ModelSet s)
        {
            var t = OpenOne(raw, ModelTables.ItemLooks, s);
            if (t != null)
            {
                s.ItemLooks = new Dictionary<int, List<ItemLook>>();
                for (int i = 0; i < t.Count; i++)
                {
                    int item = SpellTables.Int(t, i, ModelTables.ImaItem);
                    if (item <= 0) continue;
                    if (!s.ItemLooks.TryGetValue(item, out var l)) s.ItemLooks[item] = l = new List<ItemLook>(1);
                    l.Add(new ItemLook { Modifier = SpellTables.Int(t, i, ModelTables.ImaModifier), Appearance = SpellTables.Int(t, i, ModelTables.ImaAppearance), Order = SpellTables.Int(t, i, ModelTables.ImaOrder) });
                }
            }
            t = OpenOne(raw, ModelTables.Appearance, s);
            if (t != null)
            {
                s.AppearanceDisplay = new Dictionary<int, int>(t.Count);
                for (int i = 0; i < t.Count; i++) s.AppearanceDisplay[t.IdAt(i)] = SpellTables.Int(t, i, ModelTables.IaDisplay);
            }
            var models = new HashSet<int>(); var materials = new HashSet<int>();
            t = OpenOne(raw, ModelTables.ItemDisplay, s);
            if (t != null)
            {
                s.ItemDisplays = new Dictionary<int, ItemDisplayRow>(t.Count);
                for (int i = 0; i < t.Count; i++)
                {
                    var d = new ItemDisplayRow { Id = t.IdAt(i), Flags = SpellTables.Int(t, i, ModelTables.IdFlags) };
                    for (int k = 0; k < 2; k++)
                    {
                        d.Models[k] = SpellTables.IntArr(t, i, ModelTables.IdModels, k, 2); d.Materials[k] = SpellTables.IntArr(t, i, ModelTables.IdMaterials, k, 2);
                        d.ModelType[k] = SpellTables.IntArr(t, i, ModelTables.IdModelType, k, 2);
                        if (d.Models[k] > 0) models.Add(d.Models[k]);
                        if (d.Materials[k] > 0) materials.Add(d.Materials[k]);
                    }
                    if (d.Models[0] > 0 || d.Models[1] > 0) s.ItemDisplays[d.Id] = d;      // a display with no model of its own (armour worn as textures) is of no use here
                }
            }
            t = OpenOne(raw, ModelTables.ModelFiles, s);
            if (t != null)
            {
                s.ModelFiles = new Dictionary<int, List<ModelFileRow>>();
                for (int i = 0; i < t.Count; i++)
                {
                    int res = SpellTables.Int(t, i, ModelTables.MfResource);
                    if (res <= 0 || (s.ItemDisplays != null && !models.Contains(res))) continue;
                    if (!s.ModelFiles.TryGetValue(res, out var l)) s.ModelFiles[res] = l = new List<ModelFileRow>(1);
                    l.Add(new ModelFileRow { FileId = t.IdAt(i), Flags = SpellTables.Int(t, i, ModelTables.MfFlags), Lods = SpellTables.Int(t, i, ModelTables.MfLods) });
                }
            }
            t = OpenOne(raw, ModelTables.TextureFiles, s);
            if (t != null)
            {
                s.TextureFiles = new Dictionary<int, List<TextureFileRow>>();
                for (int i = 0; i < t.Count; i++)
                {
                    int mat = SpellTables.Int(t, i, ModelTables.TfMaterial);
                    if (mat <= 0 || (s.ItemDisplays != null && !materials.Contains(mat))) continue;
                    if (!s.TextureFiles.TryGetValue(mat, out var l)) s.TextureFiles[mat] = l = new List<TextureFileRow>(1);
                    l.Add(new TextureFileRow { FileId = t.IdAt(i), Usage = SpellTables.Int(t, i, ModelTables.TfUsage) });
                }
            }
        }
    }

    // one display id a creature may wear: From = "seen" (the client's creaturecache.wdb) or "classic" (the Classic baseline); Chance in percent (0 = none given)
    public sealed class NpcDisplay { public int DisplayId, Chance; public string From; public override string ToString() => DisplayId + " (" + From + (Chance > 0 ? ", " + Chance + "%" : "") + ")"; }

    // what ModelData.ForNpc found: the model to show, or why there is none
    public sealed class NpcModel
    {
        public int Entry;
        public List<NpcDisplay> Considered = new List<NpcDisplay>();
        public int DisplayId, ModelId, ModelFileId;
        public int[] Textures = new int[ModelTables.TextureSlots];
        public float Scale;                   // CreatureDisplayInfo.CreatureModelScale x CreatureModelData.ModelScale
        public bool Humanoid;                 // ExtendedDisplayInfoID != 0: a character model (HD body + bake + items), not shown yet
        public string Reason;                 // why nothing can be shown (null = Ok)
        public string From;                   // where the chosen display id came from
        public bool Ok => Reason == null && ModelFileId > 0;
        public int TextureCount => Textures.Count(x => x > 0);
    }

    // 3.6 step 5: one look of a creature the model card can switch to - a display that resolves to a model that can be shown (not a character model)
    public sealed class NpcLook
    {
        public int DisplayId, Chance;         // Chance in percent; 0 = none given (then no look of the creature has one)
        public NpcModel Model;
        public override string ToString() => DisplayId + " (" + Chance + "%, file " + (Model?.ModelFileId ?? 0) + ")";
    }

    // 3.7: what ModelData.ForItem found: the item's model and its skin, or why there is none
    public sealed class ItemModel
    {
        public int ItemId, InventoryType, Modifier = -1, AppearanceId, DisplayInfoId, ModelResId, MaterialResId, ModelFileId, TextureFileId;
        public int ModelIndex;                // 0 = ItemDisplayInfo.ModelResourcesID[0] (always for a weapon or shield unless only [1] is set)
        public int SecondModelFileId;         // a second model of the display (ModelResourcesID[1]): not shown, noted
        public int FileChoices;               // how many ModelFileData rows the model resource has (the first is taken, as wow.export does)
        public int DefaultLooks;              // how many different displays the item's default appearance rows (modifier 0) name; > 1 is only noted
        public string Reason;                 // null = Ok
        public readonly List<string> Notes = new List<string>();
        public bool Ok => Reason == null && ModelFileId > 0;
        public override string ToString() => "item " + ItemId + " slot " + InventoryType + " appearance " + AppearanceId + " (modifier " + Modifier + ") display " + DisplayInfoId + " model res " + ModelResId + " -> file " + ModelFileId
            + " (" + FileChoices + " rows) material " + MaterialResId + " -> texture " + TextureFileId + (SecondModelFileId > 0 ? " second model " + SecondModelFileId : "") + (Reason == null ? "" : " (" + Reason + ")");
    }

    public static class ModelData
    {
        public enum State { NotStarted, Disabled, Loading, Ready, Unavailable }

        public const int FormatVersion = ModelIndexFile.FormatVersion;
        public const string HumanoidReason = "character model, not yet shown";

        public static bool AutoStart = true;         // off in the self-test (it must not read the real game)
        public static string CacheFile = Path.Combine(Util.DataDir, "cache", "modeldata.bin");
        public static event Action Changed;          // on a worker thread, whenever the data or the state changed

        static readonly object gate = new object();
        static volatile ModelSet data;
        static volatile State state = State.NotStarted;
        static string reason, builtFor;
        static bool running, again;
        public static long LastLoadMs { get; private set; }
        public static string LastLoadFrom { get; private set; }
        public static string Build { get; private set; }
        public static bool Stale { get; private set; }

        public static State Status => state;
        public static string Reason => reason;
        public static ModelSet Data => data;
        public static bool Ready => state == State.Ready && data != null;

        // ---------------------------------------------------------------------------------------------------------------- the lookups
        // the display ids an npc may wear: the client's own first (it saw this creature), else the Classic baseline's (empty = none known)
        public static List<NpcDisplay> DisplaysOf(int entry)
        {
            var l = new List<NpcDisplay>();
            int seen = SeenData.Npc(entry)?.DisplayId ?? 0;
            if (seen > 0) { l.Add(new NpcDisplay { DisplayId = seen, Chance = 0, From = "seen" }); return l; }
            foreach (var kv in ClassicAtlas.Displays(entry)) l.Add(new NpcDisplay { DisplayId = kv.Key, Chance = kv.Value, From = "classic" });
            return l;
        }

        public static NpcModel ForNpc(int entry)
        {
            var considered = DisplaysOf(entry);
            var m = Resolve(data, considered, entry);
            if (considered.Count == 0)
            {
                int fmt = ClassicAtlas.Current?.Provenance?.FormatVersion ?? 0;
                m.Reason = "no display id is known for this creature (not met in the game" + (fmt > 0 && fmt < 3 ? ", and the Classic baseline index is older than format 3" : ClassicAtlas.Current == null ? ", and no Classic baseline" : ", and not in the Classic baseline") + ")";
            }
            return m;
        }

        // 3.6 step 5: the looks of a creature, in the order of its display ids (DisplayId1..4): each display that resolves to a model that can be shown.
        // Left out: a character model (humanoid), a display or model the game lacks, and - when any display has a chance - one with a chance of 0 (the
        // game never picks it). The same display twice is one look with the chances added (the game's roll counts both).
        public static List<NpcLook> LooksOf(int entry) => Looks(data, DisplaysOf(entry), entry);

        public static List<NpcLook> Looks(ModelSet s, List<NpcDisplay> considered, int entry)
        {
            var l = new List<NpcLook>();
            if (considered == null || considered.Count == 0) return l;
            bool anyChance = considered.Any(d => d.Chance > 0);
            foreach (var d in considered)
            {
                if (anyChance && d.Chance <= 0) continue;
                var same = l.FirstOrDefault(x => x.DisplayId == d.DisplayId);
                if (same != null) { same.Chance += d.Chance; continue; }
                var m = Resolve(s, new List<NpcDisplay> { d }, entry);
                if (!m.Ok) continue;
                l.Add(new NpcLook { DisplayId = d.DisplayId, Chance = d.Chance, Model = m });
            }
            return l;
        }

        // the look shown first: the highest chance (equal chances keep the order), as Resolve picks; -1 = none
        public static int DefaultLook(List<NpcLook> looks)
        {
            int best = -1;
            for (int i = 0; i < (looks?.Count ?? 0); i++) if (best < 0 || looks[i].Chance > looks[best].Chance) best = i;
            return best;
        }

        // one display id alone (the real-file checks, a later item / pet viewer)
        public static NpcModel ForDisplay(int displayId) => Resolve(data, new List<NpcDisplay> { new NpcDisplay { DisplayId = displayId, From = "asked" } }, 0);

        // The choice: the highest chance first (equal chances keep the order given), the first display the game's CreatureDisplayInfo knows wins.
        // A table that is unavailable gives its reason; nothing is guessed.
        public static NpcModel Resolve(ModelSet s, List<NpcDisplay> considered, int entry)
        {
            var m = new NpcModel { Entry = entry, Considered = considered ?? new List<NpcDisplay>() };
            if (m.Considered.Count == 0) { m.Reason = "no display id is known for this creature"; return m; }
            if (s == null) { m.Reason = "the model tables are not read" + (string.IsNullOrEmpty(reason) ? "" : " (" + reason + ")"); return m; }
            if (s.Displays == null) { m.Reason = s.Problems.TryGetValue(ModelTables.DisplayInfo.Name, out var w) ? w : "CreatureDisplayInfo is unavailable"; return m; }
            var order = m.Considered.Select((d, i) => (d, i)).OrderByDescending(x => x.d.Chance).ThenBy(x => x.i).Select(x => x.d).ToList();
            CreatureDisplay cd = null; NpcDisplay pick = null;
            foreach (var d in order) if (s.Displays.TryGetValue(d.DisplayId, out cd)) { pick = d; break; }
            if (pick == null) { m.DisplayId = order[0].DisplayId; m.Reason = "display " + string.Join(", ", order.Select(d => d.DisplayId)) + (order.Count == 1 ? " is" : " are") + " not in the game's CreatureDisplayInfo"; return m; }
            m.DisplayId = pick.DisplayId; m.From = pick.From; m.ModelId = cd.ModelId; m.Humanoid = cd.ExtendedId != 0;
            Array.Copy(cd.Textures, m.Textures, ModelTables.TextureSlots);
            if (s.Models == null) { m.Reason = s.Problems.TryGetValue(ModelTables.ModelInfo.Name, out var w) ? w : "CreatureModelData is unavailable"; return m; }
            if (!s.Models.TryGetValue(cd.ModelId, out var md)) { m.Reason = "model " + cd.ModelId + " of display " + cd.Id + " is not in the game's CreatureModelData"; return m; }
            m.ModelFileId = md.FileId; m.Scale = cd.Scale * md.Scale;
            if (md.FileId <= 0) { m.Reason = "model " + cd.ModelId + " has no model file"; return m; }
            if (m.Humanoid) m.Reason = HumanoidReason;
            return m;
        }

        // ---------------------------------------------------------------------------------------------------------------- 3.7: items
        public const string NoItemCard = "not a weapon, shield or off-hand item";

        // the item's inventory type from the game's item data (ItemData: Item.db2 / ItemSparse; 0 = not worn); -1 = the item data does not know the item
        public static int InventoryTypeOf(int itemId) { try { return ItemData.Get(itemId)?.InventoryType ?? -1; } catch (Exception) { return -1; } }

        // whether an item page gets a model card: a weapon, a shield or a held-in-off-hand item the game's item data knows
        public static bool ItemWanted(int itemId) => itemId > 0 && ModelTables.ItemSlotShown(InventoryTypeOf(itemId));

        public static ItemModel ForItem(int itemId) => ResolveItem(data, itemId, InventoryTypeOf(itemId));

        // 3.7: which end of a long item the viewer puts on top: "head" (the heavier end) for a staff, polearm or spear (weapon subclasses 10, 6, 17: held in
        // the middle), null (the far end from the grip) for everything else
        public static string ItemUpRule(int itemId)
        {
            ClientItem c = null; try { c = ItemData.Get(itemId); } catch (Exception) { }
            return c != null && c.ClassId == 2 && (c.SubclassId == 10 || c.SubclassId == 6 || c.SubclassId == 17) ? "head" : null;
        }

        // The chain, after wow.export's DBItemModels (MIT, Kruithne): ItemModifiedAppearance (the item's rows; the default appearance = modifier 0, else the
        // lowest modifier; several rows of that modifier: the lowest OrderIndex, then the first) -> ItemAppearance.ItemDisplayInfoID -> ItemDisplayInfo
        // ModelResourcesID[0] (a weapon or shield is one model; [1] only when [0] is empty; a second one is noted) -> ModelFileData (the resource's first row
        // in the table's order, as wow.export takes its first option when no race is asked for) and ModelMaterialResourcesID[same index] -> TextureFileData
        // (the first row of UsageType 0, as wow.export's DBTextureFileData keeps only those): the skin the model's texture type 2 ("object skin") takes.
        public static ItemModel ResolveItem(ModelSet s, int itemId, int inventoryType)
        {
            var m = new ItemModel { ItemId = itemId, InventoryType = inventoryType };
            if (itemId <= 0) { m.Reason = "no item"; return m; }
            if (!ModelTables.ItemSlotShown(inventoryType)) { m.Reason = inventoryType < 0 ? "the item is not in the game's item data" : NoItemCard; return m; }
            if (s == null) { m.Reason = "the model tables are not read" + (string.IsNullOrEmpty(reason) ? "" : " (" + reason + ")"); return m; }
            string Off(SpellTables.Def d) => s.Problems.TryGetValue(d.Name, out var w) ? w : d.Name + " is unavailable";
            if (s.ItemLooks == null) { m.Reason = Off(ModelTables.ItemLooks); return m; }
            if (!s.ItemLooks.TryGetValue(itemId, out var rows) || rows.Count == 0) { m.Reason = "the item has no appearance in the game's ItemModifiedAppearance"; return m; }
            int mod = rows.Any(r => r.Modifier == 0) ? 0 : rows.Min(r => r.Modifier);
            var pick = rows.Select((r, i) => (r, i)).Where(x => x.r.Modifier == mod).OrderBy(x => x.r.Order).ThenBy(x => x.i).First().r;
            m.Modifier = mod; m.AppearanceId = pick.Appearance;
            if (s.AppearanceDisplay == null) { m.Reason = Off(ModelTables.Appearance); return m; }
            m.DefaultLooks = rows.Where(r => r.Modifier == mod).Select(r => s.AppearanceDisplay.TryGetValue(r.Appearance, out var dd) ? dd : 0).Where(x => x > 0).Distinct().Count();
            if (m.DefaultLooks > 1) m.Notes.Add("the default appearance names " + m.DefaultLooks + " displays; the first is shown");
            if (!s.AppearanceDisplay.TryGetValue(pick.Appearance, out var disp)) { m.Reason = "appearance " + pick.Appearance + " is not in the game's ItemAppearance"; return m; }
            if (disp <= 0) { m.Reason = "appearance " + pick.Appearance + " has no display"; return m; }
            return FillDisplay(s, m, disp);
        }

        // one ItemDisplayInfo row alone (the model build is per display: items that look the same share one file)
        public static ItemModel ForItemDisplay(int displayInfoId)
        {
            var m = new ItemModel();
            if (data == null) { m.Reason = "the model tables are not read" + (string.IsNullOrEmpty(reason) ? "" : " (" + reason + ")"); return m; }
            return FillDisplay(data, m, displayInfoId);
        }

        static ItemModel FillDisplay(ModelSet s, ItemModel m, int disp)
        {
            string Off(SpellTables.Def d) => s.Problems.TryGetValue(d.Name, out var w) ? w : d.Name + " is unavailable";
            m.DisplayInfoId = disp;
            if (disp <= 0) { m.Reason = "no item display"; return m; }
            if (s.ItemDisplays == null) { m.Reason = Off(ModelTables.ItemDisplay); return m; }
            if (!s.ItemDisplays.TryGetValue(disp, out var d)) { m.Reason = "display " + disp + " has no model in the game's ItemDisplayInfo"; return m; }
            m.ModelIndex = d.Models[0] > 0 ? 0 : 1;
            m.ModelResId = d.Models[m.ModelIndex]; m.MaterialResId = d.Materials[m.ModelIndex];
            if (s.ModelFiles == null) { m.Reason = Off(ModelTables.ModelFiles); return m; }
            if (!s.ModelFiles.TryGetValue(m.ModelResId, out var files) || files.Count == 0 || files[0].FileId <= 0) { m.Reason = "model resource " + m.ModelResId + " of display " + disp + " is not in the game's ModelFileData"; return m; }
            m.ModelFileId = files[0].FileId; m.FileChoices = files.Count;
            if (files.Count > 1) m.Notes.Add("model resource " + m.ModelResId + " has " + files.Count + " files; the first (" + files[0].FileId + ") is shown");
            if (m.ModelIndex == 0 && d.Models[1] > 0)
            {
                if (s.ModelFiles.TryGetValue(d.Models[1], out var f2) && f2.Count > 0) m.SecondModelFileId = f2[0].FileId;
                m.Notes.Add("the display has a second model (resource " + d.Models[1] + (m.SecondModelFileId > 0 ? ", file " + m.SecondModelFileId : "") + "); it is not shown");
            }
            if (m.ModelIndex == 1) m.Notes.Add("the display's first model slot is empty; its second model is shown");
            if (s.TextureFiles == null) m.Notes.Add(Off(ModelTables.TextureFiles) + "; a stand-in skin is shown");
            else if (m.MaterialResId <= 0) m.Notes.Add("the display names no skin for its model");
            else if (s.TextureFiles.TryGetValue(m.MaterialResId, out var tex) && tex.Any(x => x.Usage == 0 && x.FileId > 0)) m.TextureFileId = tex.First(x => x.Usage == 0 && x.FileId > 0).FileId;
            else m.Notes.Add("material resource " + m.MaterialResId + " has no texture in the game's TextureFileData");
            return m;
        }

        // ---------------------------------------------------------------------------------------------------------------- the service
        static bool SafeEnabled() { try { return ItemData.Enabled(); } catch { return true; } }

        public static void Start()
        {
            if (!AutoStart) return;
            if (!SafeEnabled()) { Disable(); return; }
            lock (gate)
            {
                again = true;
                if (running) return;
                running = true;
            }
            if (data == null) SetState(State.Loading, null);
            Task.Factory.StartNew(Worker, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        public static void Disable()
        {
            data = null; Build = null; builtFor = null;
            SetState(State.Disabled, null);
        }

        static void Worker()
        {
            try { Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; } catch { }
            while (true)
            {
                lock (gate) { if (!again) { running = false; return; } again = false; }
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    if (!SafeEnabled()) { Disable(); continue; }
                    GameDataProgress.Begin(JobId, "3D models", "Loading the creature and item models", 0.1);
                    LoadOnce();
                }
                catch (Exception e)
                {
                    Util.Log("model data: " + e);
                    Publish(null, State.Unavailable, "could not be read: " + e.Message, null);
                }
                LastLoadMs = sw.ElapsedMilliseconds;
                GameDataProgress.End(JobId, state == State.Ready, state == State.Ready ? null : reason);
            }
        }

        static void Publish(ModelSet s, State st, string why, string build)
        {
            lock (gate) { data = st == State.Ready ? s : null; reason = why; state = st; Build = st == State.Ready ? build : null; builtFor = Build; }
            try { Changed?.Invoke(); } catch { }
        }

        static void SetState(State st, string why)
        {
            lock (gate) { state = st; reason = why; }
            try { Changed?.Invoke(); } catch { }
        }

        public static void SetForTest(ModelSet s, State st = State.Ready, string why = null)
        {
            lock (gate) { data = st == State.Ready ? s : null; state = st; reason = why; Build = st == State.Ready ? "test" : null; builtFor = Build; }
        }
        // 3.6 step 3: as the worker does when it finishes (the NPC page's model card waits for it)
        public static void RaiseChangedForTest() { try { Changed?.Invoke(); } catch { } }

        public static void ResetForTest()
        {
            lock (gate) { data = null; state = State.NotStarted; reason = null; running = false; again = false; Build = null; builtFor = null; Stale = false; }
        }

        static void LoadOnce()
        {
            var root = ItemData.WowRoot();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) { Publish(null, State.Unavailable, "the WoW folder is not set", null); return; }
            var flavor = ItemData.Flavor() ?? "_classic_beta_";
            var build = ItemData.GameBuild(root, flavor);
            if (build == null) { Publish(null, State.Unavailable, "no .build.info in the WoW folder", null); return; }
            if (state == State.Ready && data != null && builtFor == build) return;
            var saved = ModelIndexFile.TryRead(CacheFile);
            // the game was patched: the models of the previous build are used at once while the new game files are read in the background
            bool old = data == null && saved != null && saved.Build != build && !saved.Set.Problems.ContainsKey("all");
            if (old) { LastLoadFrom = "cache file (previous game build)"; Stale = true; Publish(saved.Set, State.Ready, null, saved.Build); }
            var set = Obtain(saved, build, CacheFile, CascShared.OneAtATime(JobId, () =>
            {
                GameDataProgress.Rebuilding(JobId, GameDataProgress.ReasonFor(CacheFile, saved?.Build, build), "Reading creature and item models", 0.3, "tables", old);
                try { return ModelSet.Parse(ReadCasc(root, flavor, (n, t) => GameDataProgress.Report(JobId, n, t, 0, 0.8))); }
                catch (Exception e) { Util.Log("model data: game files: " + e.Message); var f = new ModelSet(); f.Problems["all"] = "the game files could not be read: " + e.Message; return f; }
            }), out var from);
            if (set.Problems.ContainsKey("all") && old) { Util.Log("model data: the new game files could not be read (" + set.Problems["all"] + "); the models of the previous game build stay"); return; }
            if (set.Problems.ContainsKey("all")) { Util.Log("model data: unavailable: " + set.Problems["all"]); Publish(null, State.Unavailable, set.Problems["all"], null); return; }
            foreach (var kv in set.Problems) Util.Log("model data: " + kv.Key + ": " + kv.Value);
            foreach (var n in set.Notes) Util.Log("model data: " + n);
            LastLoadFrom = from; Stale = false;
            Publish(set, State.Ready, null, build);
        }

        // the saved index when it is of this game build, otherwise the game files (which are then saved)
        public static ModelSet Obtain(ModelIndexFile.Saved saved, string build, string cacheFile, Func<ModelSet> readGame, out string from)
        {
            if (saved != null && saved.Build == build && saved.Format == FormatVersion) { from = "cache file"; return saved.Set; }
            var fresh = readGame();
            if (!fresh.Problems.ContainsKey("all")) ModelIndexFile.TryWrite(cacheFile, build, fresh);
            from = "game files";
            return fresh;
        }

        public const string JobId = "models";

        public static SpellRaw ReadCasc(string root, string flavor, Action<int, int> progress = null)
        {
            var raw = new SpellRaw();
            using (var lease = CascShared.Open(root, flavor))     // shared with the other readers; key-locked parts read as zeros (the table reader skips those sections)
            {
                var casc = lease.Casc;
                var all = ModelTables.All;
                var keys = casc.ResolveFileIds(new HashSet<int>(all.Select(d => d.FileId)));
                int i = 0;
                foreach (var def in all)
                {
                    progress?.Invoke(i++, all.Length);
                    try { if (keys.TryGetValue(def.FileId, out var ck)) raw.Tables[def.Name] = casc.ReadByContentKey(ck); }
                    catch (Exception e) { Util.Log("model data: " + def.Name + ".db2: " + e.Message); }
                }
            }
            return raw;
        }
    }

    // ============================================================================================================================
    // the compact copy of the two tables in the Outpost's data folder (gzip of a small binary format), keyed by the game build
    // ============================================================================================================================
    public static class ModelIndexFile
    {
        public sealed class Saved { public int Format; public string Build; public ModelSet Set; }
        public const int FormatVersion = 2;       // bump when the parsing, the table layouts or the cache file change meaning (3.7: 2, the item tables)
        const int Magic = 0x4C444D45;       // "EMDL"

        public static bool TryWrite(string path, string build, ModelSet set)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var tmp = path + ".tmp";
                var mem = new MemoryStream(1 << 20);
                using (var w = new BinaryWriter(mem, Encoding.UTF8, true))
                {
                    w.Write(Magic); w.Write(FormatVersion); w.Write(build ?? "");
                    w.Write(set.Problems.Count); foreach (var kv in set.Problems) { w.Write(kv.Key); w.Write(kv.Value ?? ""); }
                    w.Write(set.Notes.Count); foreach (var n in set.Notes) w.Write(n);
                    w.Write(set.LayoutsSeen.Count); foreach (var kv in set.LayoutsSeen) { w.Write(kv.Key); w.Write(kv.Value); }
                    w.Write(set.Displays != null);
                    if (set.Displays != null)
                    {
                        w.Write(set.Displays.Count);
                        foreach (var d in set.Displays.Values) { w.Write(d.Id); w.Write(d.ModelId); w.Write(d.ExtendedId); w.Write(d.Scale); foreach (var t in d.Textures) w.Write(t); }
                    }
                    w.Write(set.Models != null);
                    if (set.Models != null)
                    {
                        w.Write(set.Models.Count);
                        foreach (var m in set.Models.Values) { w.Write(m.Id); w.Write(m.FileId); w.Write(m.Flags); w.Write(m.Scale); }
                    }
                    // 3.7 (format 2): the item tables, each list in its table's order
                    w.Write(set.ItemLooks != null);
                    if (set.ItemLooks != null)
                    {
                        w.Write(set.ItemLooks.Count);
                        foreach (var kv in set.ItemLooks) { w.Write(kv.Key); w.Write(kv.Value.Count); foreach (var l in kv.Value) { w.Write(l.Modifier); w.Write(l.Appearance); w.Write(l.Order); } }
                    }
                    w.Write(set.AppearanceDisplay != null);
                    if (set.AppearanceDisplay != null) { w.Write(set.AppearanceDisplay.Count); foreach (var kv in set.AppearanceDisplay) { w.Write(kv.Key); w.Write(kv.Value); } }
                    w.Write(set.ItemDisplays != null);
                    if (set.ItemDisplays != null)
                    {
                        w.Write(set.ItemDisplays.Count);
                        foreach (var d in set.ItemDisplays.Values) { w.Write(d.Id); w.Write(d.Flags); for (int k = 0; k < 2; k++) { w.Write(d.Models[k]); w.Write(d.Materials[k]); w.Write(d.ModelType[k]); } }
                    }
                    w.Write(set.ModelFiles != null);
                    if (set.ModelFiles != null)
                    {
                        w.Write(set.ModelFiles.Count);
                        foreach (var kv in set.ModelFiles) { w.Write(kv.Key); w.Write(kv.Value.Count); foreach (var f in kv.Value) { w.Write(f.FileId); w.Write(f.Flags); w.Write(f.Lods); } }
                    }
                    w.Write(set.TextureFiles != null);
                    if (set.TextureFiles != null)
                    {
                        w.Write(set.TextureFiles.Count);
                        foreach (var kv in set.TextureFiles) { w.Write(kv.Key); w.Write(kv.Value.Count); foreach (var f in kv.Value) { w.Write(f.FileId); w.Write(f.Usage); } }
                    }
                }
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var gz = new GZipStream(fs, CompressionMode.Compress))
                    gz.Write(mem.GetBuffer(), 0, (int)mem.Length);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                return true;
            }
            catch (Exception e) { Util.Log("model data: cannot save the index: " + e.Message); return false; }
        }

        // null for a missing, damaged, foreign or older file (the caller then reads the game files)
        public static Saved TryRead(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var mem = new MemoryStream(1 << 20);
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var gz = new GZipStream(fs, CompressionMode.Decompress))
                    gz.CopyTo(mem);
                mem.Position = 0;
                using (var r = new BinaryReader(mem, Encoding.UTF8))
                {
                    if (r.ReadInt32() != Magic) return null;
                    var s = new Saved { Format = r.ReadInt32(), Build = r.ReadString(), Set = new ModelSet() };
                    if (s.Format != FormatVersion) return null;
                    int n = r.ReadInt32(); Count(n, 1000);
                    for (int i = 0; i < n; i++) s.Set.Problems[r.ReadString()] = r.ReadString();
                    n = r.ReadInt32(); Count(n, 100000);
                    for (int i = 0; i < n; i++) s.Set.Notes.Add(r.ReadString());
                    n = r.ReadInt32(); Count(n, 1000);
                    for (int i = 0; i < n; i++) s.Set.LayoutsSeen[r.ReadString()] = r.ReadUInt32();
                    if (r.ReadBoolean())
                    {
                        n = r.ReadInt32(); Count(n, 5000000);
                        s.Set.Displays = new Dictionary<int, CreatureDisplay>(n);
                        for (int i = 0; i < n; i++)
                        {
                            var d = new CreatureDisplay { Id = r.ReadInt32(), ModelId = r.ReadInt32(), ExtendedId = r.ReadInt32(), Scale = r.ReadSingle() };
                            for (int k = 0; k < ModelTables.TextureSlots; k++) d.Textures[k] = r.ReadInt32();
                            s.Set.Displays[d.Id] = d;
                        }
                    }
                    if (r.ReadBoolean())
                    {
                        n = r.ReadInt32(); Count(n, 5000000);
                        s.Set.Models = new Dictionary<int, CreatureModelInfo>(n);
                        for (int i = 0; i < n; i++) { var m = new CreatureModelInfo { Id = r.ReadInt32(), FileId = r.ReadInt32(), Flags = r.ReadInt32(), Scale = r.ReadSingle() }; s.Set.Models[m.Id] = m; }
                    }
                    if (r.ReadBoolean())
                    {
                        n = r.ReadInt32(); Count(n, 5000000);
                        s.Set.ItemLooks = new Dictionary<int, List<ItemLook>>(n);
                        for (int i = 0; i < n; i++)
                        {
                            int id = r.ReadInt32(), c = r.ReadInt32(); Count(c, 10000);
                            var l = new List<ItemLook>(c);
                            for (int k = 0; k < c; k++) l.Add(new ItemLook { Modifier = r.ReadInt32(), Appearance = r.ReadInt32(), Order = r.ReadInt32() });
                            s.Set.ItemLooks[id] = l;
                        }
                    }
                    if (r.ReadBoolean())
                    {
                        n = r.ReadInt32(); Count(n, 5000000);
                        s.Set.AppearanceDisplay = new Dictionary<int, int>(n);
                        for (int i = 0; i < n; i++) { int k = r.ReadInt32(); s.Set.AppearanceDisplay[k] = r.ReadInt32(); }
                    }
                    if (r.ReadBoolean())
                    {
                        n = r.ReadInt32(); Count(n, 5000000);
                        s.Set.ItemDisplays = new Dictionary<int, ItemDisplayRow>(n);
                        for (int i = 0; i < n; i++)
                        {
                            var d = new ItemDisplayRow { Id = r.ReadInt32(), Flags = r.ReadInt32() };
                            for (int k = 0; k < 2; k++) { d.Models[k] = r.ReadInt32(); d.Materials[k] = r.ReadInt32(); d.ModelType[k] = r.ReadInt32(); }
                            s.Set.ItemDisplays[d.Id] = d;
                        }
                    }
                    if (r.ReadBoolean())
                    {
                        n = r.ReadInt32(); Count(n, 5000000);
                        s.Set.ModelFiles = new Dictionary<int, List<ModelFileRow>>(n);
                        for (int i = 0; i < n; i++)
                        {
                            int id = r.ReadInt32(), c = r.ReadInt32(); Count(c, 10000);
                            var l = new List<ModelFileRow>(c);
                            for (int k = 0; k < c; k++) l.Add(new ModelFileRow { FileId = r.ReadInt32(), Flags = r.ReadInt32(), Lods = r.ReadInt32() });
                            s.Set.ModelFiles[id] = l;
                        }
                    }
                    if (r.ReadBoolean())
                    {
                        n = r.ReadInt32(); Count(n, 5000000);
                        s.Set.TextureFiles = new Dictionary<int, List<TextureFileRow>>(n);
                        for (int i = 0; i < n; i++)
                        {
                            int id = r.ReadInt32(), c = r.ReadInt32(); Count(c, 10000);
                            var l = new List<TextureFileRow>(c);
                            for (int k = 0; k < c; k++) l.Add(new TextureFileRow { FileId = r.ReadInt32(), Usage = r.ReadInt32() });
                            s.Set.TextureFiles[id] = l;
                        }
                    }
                    return s;
                }
            }
            catch (Exception) { return null; }
        }

        static void Count(int n, int max) { if (n < 0 || n > max) throw new InvalidDataException("count"); }
    }
}
