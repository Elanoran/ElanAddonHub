using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Models3d;

namespace ElansAddonHub
{
    // --selftest "models", 3.7: item models (weapons, shields, held in the off hand). The five item tables written here in their 1.60.1.70707 layouts
    // (ItemModifiedAppearance, ItemAppearance, ItemDisplayInfo, ModelFileData, TextureFileData), the chain item -> default appearance -> display -> model
    // file + skin (the default appearance's rule, the first ModelFileData row of a resource, the first UsageType-0 texture, a second model noted, every
    // missing link a reason), the inventory-type filter, an unknown layout switching only its table off, the saved index; a small item model (a still
    // "sword" without bones, and one with a bone and a Stand) built as "M2B2" kind "item" (texture type 2 = the item's skin), its cache file
    // m\item-<display>.m2b2 beside a creature display of the same number; and the item page's Model card with the viewer (the still mesh drawn, framed by
    // its longest side at every turn, no look switch, no card for armour or a Hearthstone).
    public partial class MainWindow
    {
        // ---- the item tables (3.7), in the layouts of 1.60.1.70707 (ModelTables)
        // ItemModifiedAppearance 03A6C979: 7 fields, the id inline (0): ItemID(1) ItemAppearanceModifierID(2) ItemAppearanceID(3) OrderIndex(4) TransmogSourceTypeEnum(5) Flags(6)
        static byte[] MiLooks(uint layout = 0x03A6C979)
        {
            var fs = new[] { SdI(), SdI(), SdI(), SdI(), SdI(), SdI(1, 8), SdI() };
            object[] R(int id, int item, int mod, int app, int order) => new object[] { id, item, mod, app, order, 0, 0 };
            var rows = new[]
            {
                R(1, 19019, 0, 9001, 0), R(2, 19019, 1, 9002, 0),                          // the default (modifier 0) and another look
                R(3, 2000, 0, 9003, 0),                                                    // a display with two models
                R(4, 2001, 3, 9010, 2), R(5, 2001, 3, 9011, 1), R(6, 2001, 5, 9001, 0),    // no modifier 0: the lowest modifier (3), its lowest OrderIndex (9011)
                R(7, 2002, 0, 9004, 1), R(8, 2002, 0, 9005, 0),                            // two default rows, two displays: the lowest OrderIndex (9005), noted
                R(9, 2004, 0, 9001, 0),                                                    // a chest piece: never a card
                R(10, 2005, 0, 9006, 0),                                                   // its model resource has no ModelFileData row
                R(11, 2006, 0, 9007, 0),                                                   // its display has no model (armour: textures only)
                R(12, 2007, 0, 9099, 0),                                                   // its appearance is not in ItemAppearance
                R(13, 2008, 0, 9008, 0),                                                   // its material has no UsageType-0 texture
                R(14, 2009, 0, 9012, 0),                                                   // its display's first model slot is empty
            };
            return SdBuild(ModelTables.ItemLooks.TableHash, layout, fs, rows, rows.Select(r => (int)r[0]).ToArray(), idIndex: 0, idList: false);
        }

        // ItemAppearance 481C4281: 5 fields, the id in the id list: DisplayType(0) ItemDisplayInfoID(1) DefaultIconFileDataID(2) UiOrder(3) TransmogPlayerConditionID(4)
        static byte[] MiAppearances()
        {
            var fs = new[] { SdI(1, 8), SdI(), SdI(), SdI(), SdI() };
            var map = new (int app, int disp)[] { (9001, 7001), (9002, 7009), (9003, 7002), (9010, 7003), (9011, 604), (9004, 7001), (9005, 7002), (9006, 7005), (9007, 7050), (9008, 7008), (9012, 7012) };
            return SdBuild(ModelTables.Appearance.TableHash, 0x481C4281, fs, map.Select(x => new object[] { 0, x.disp, 135274, 0, 0 }).ToArray(), map.Select(x => x.app).ToArray());
        }

        // ItemDisplayInfo 9F3AB8A9: 16 fields, the id in the id list; ModelResourcesID[2](10) ModelMaterialResourcesID[2](11) ModelType[2](12) GeosetGroup[6](13)
        // AttachmentGeosetGroup[6](14) HelmetGeosetVis[2](15)
        static byte[] MiDisplays(uint layout = 0x9F3AB8A9)
        {
            var fs = Enumerable.Range(0, 16).Select(i => i == 10 || i == 11 || i == 12 || i == 15 ? SdI(2) : i == 13 || i == 14 ? SdI(6) : SdI()).ToArray();
            object[] R(int[] models, int[] mats) { var r = new object[16]; for (int i = 0; i < 16; i++) r[i] = 0; r[9] = 0; r[10] = models; r[11] = mats; r[12] = new[] { 0, 0 }; r[13] = new int[6]; r[14] = new int[6]; r[15] = new int[2]; return r; }
            var rows = new (int id, object[] r)[]
            {
                (7001, R(new[] { 501, 0 }, new[] { 601, 0 })), (7002, R(new[] { 502, 503 }, new[] { 602, 603 })), (7003, R(new[] { 504, 0 }, new[] { 604, 0 })),
                (604, R(new[] { 501, 0 }, new[] { 601, 0 })), (7005, R(new[] { 505, 0 }, new[] { 605, 0 })), (7008, R(new[] { 501, 0 }, new[] { 608, 0 })),
                (7009, R(new[] { 501, 0 }, new[] { 601, 0 })), (7050, R(new[] { 0, 0 }, new[] { 650, 0 })), (7012, R(new[] { 0, 504 }, new[] { 0, 604 })),
            };
            return SdBuild(ModelTables.ItemDisplay.TableHash, layout, fs, rows.Select(x => x.r).ToArray(), rows.Select(x => x.id).ToArray());
        }

        // ModelFileData 2AE4E788: 5 fields, the id (the FileDataID) inline as field 1: GeoBox[6](0) FileDataID(1) Flags(2) LodCount(3) ModelResourcesID(4).
        // Resource 501 has two rows: the first in the table (80009) is taken although 80001 is lower; 999 is named by no display (left out of the set)
        static byte[] MiModelFiles()
        {
            var fs = new[] { SdFl(6), SdI(), SdI(), SdI(1, 8), SdI() };
            object[] R(int fdid, int res, int lods = 1) => new object[] { new float[6], fdid, 0, lods, res };
            var rows = new[] { R(80009, 501), R(80001, 501, 2), R(80020, 502), R(80030, 503), R(80040, 504), R(80999, 999) };
            return SdBuild(ModelTables.ModelFiles.TableHash, 0x2AE4E788, fs, rows, rows.Select(r => (int)r[1]).ToArray(), idIndex: 1, idList: false);
        }

        // TextureFileData BD7C74C2: 3 fields, the id (the FileDataID) inline as field 0: FileDataID(0) UsageType(1) MaterialResourcesID(2). Material 601: a
        // UsageType-1 row first, then the UsageType-0 one (90001) that is taken; 608 has only a UsageType-1 row
        static byte[] MiTextureFiles()
        {
            var fs = new[] { SdI(), SdI(1, 8), SdI() };
            object[] R(int fdid, int usage, int mat) => new object[] { fdid, usage, mat };
            var rows = new[] { R(90002, 1, 601), R(90001, 0, 601), R(90020, 0, 602), R(90030, 0, 603), R(90040, 0, 604), R(90080, 1, 608), R(90999, 0, 999) };
            return SdBuild(ModelTables.TextureFiles.TableHash, 0xBD7C74C2, fs, rows, rows.Select(r => (int)r[0]).ToArray(), idIndex: 0, idList: false);
        }

        static void MiAdd(SpellRaw raw, byte[] displays = null)
        {
            raw.Tables[ModelTables.ItemLooks.Name] = MiLooks();
            raw.Tables[ModelTables.Appearance.Name] = MiAppearances();
            raw.Tables[ModelTables.ItemDisplay.Name] = displays ?? MiDisplays();
            raw.Tables[ModelTables.ModelFiles.Name] = MiModelFiles();
            raw.Tables[ModelTables.TextureFiles.Name] = MiTextureFiles();
        }

        // ============================================================================================================================ the data
        void ModelItemDataChecks(string work, ModelSet set, Action<string, bool, string> Check)
        {
            Check("items: the five item tables parse in their 1.60.1.70707 layouts (10 items, 11 appearances, 8 displays with a model - the armour one left out, 4 model resources with files - 999 left out, 5 materials with textures)",
                set.ItemLooks?.Count == 10 && set.AppearanceDisplay?.Count == 11 && set.ItemDisplays?.Count == 8 && !set.ItemDisplays.ContainsKey(7050) && set.ModelFiles?.Count == 4 && !set.ModelFiles.ContainsKey(999)
                && set.TextureFiles?.Count == 5 && ModelTables.ItemTables.All(d => set.LayoutsSeen.GetValueOrDefault(d.Name) == d.Layouts[0]) && ModelTables.ItemTables.All(d => !set.Problems.ContainsKey(d.Name)),
                "looks " + set.ItemLooks?.Count + " app " + set.AppearanceDisplay?.Count + " displays " + set.ItemDisplays?.Count + " files " + set.ModelFiles?.Count + " tex " + set.TextureFiles?.Count + " | " + string.Join("; ", set.Problems.Values));
            Check("items: the fields - ItemID / modifier / appearance / OrderIndex, the display's ModelResourcesID[2] and ModelMaterialResourcesID[2], a model resource's rows in the table's order (80009 then 80001, LodCount 1 / 2), a texture's UsageType",
                set.ItemLooks[2001].Select(l => l.Modifier + "/" + l.Appearance + "/" + l.Order).SequenceEqual(new[] { "3/9010/2", "3/9011/1", "5/9001/0" }) && set.ItemDisplays[7002].Models.SequenceEqual(new[] { 502, 503 })
                && set.ItemDisplays[7002].Materials.SequenceEqual(new[] { 602, 603 }) && set.ModelFiles[501].Select(f => f.FileId + "/" + f.Lods).SequenceEqual(new[] { "80009/1", "80001/2" })
                && set.TextureFiles[601].Select(f => f.FileId + "/" + f.Usage).SequenceEqual(new[] { "90002/1", "90001/0" }), null);

            Check("items: the inventory types with a card - 13 one-hand, 14 shield, 15 bow, 17 two-hand, 21 main hand, 22 off hand, 23 held in off-hand, 25 thrown, 26 gun / wand; not 0 (a Hearthstone), armour, a cloak (16), ammo (24), a quiver (27), a relic (28)",
                new[] { 13, 14, 15, 17, 21, 22, 23, 25, 26 }.All(ModelTables.ItemSlotShown) && !new[] { -1, 0, 1, 3, 5, 7, 16, 18, 19, 20, 24, 27, 28 }.Any(ModelTables.ItemSlotShown)
                && ItemNames.Slot(14) == "Off Hand" && ItemNames.Slot(23) == "Held In Off-hand" && ItemNames.Slot(26) == "Ranged", null);

            var tf = ModelData.ResolveItem(set, 19019, 13);
            Check("items: item 19019 -> its modifier-0 appearance 9001 (not modifier 1's) -> display 7001 -> resource 501 -> file 80009 (the first of its 2 rows, as wow.export takes) and material 601 -> texture 90001 (UsageType 0, not the UsageType-1 row before it)",
                tf.Ok && tf.Modifier == 0 && tf.AppearanceId == 9001 && tf.DisplayInfoId == 7001 && tf.ModelResId == 501 && tf.ModelFileId == 80009 && tf.FileChoices == 2 && tf.MaterialResId == 601 && tf.TextureFileId == 90001
                && tf.SecondModelFileId == 0 && tf.Notes.Any(n => n.Contains("2 files")), tf.ToString());
            var two = ModelData.ResolveItem(set, 2000, 17);
            var low = ModelData.ResolveItem(set, 2001, 21);
            var dup = ModelData.ResolveItem(set, 2002, 14);
            var slot1 = ModelData.ResolveItem(set, 2009, 23);
            Check("items: a display with two models shows the first (80020, skin 90020) and notes the second (80030); no modifier 0 = the lowest modifier (3) and its lowest OrderIndex (9011 -> display 604); two default rows = the lowest OrderIndex, noted; only the second model slot = that one, noted",
                two.Ok && two.ModelFileId == 80020 && two.TextureFileId == 90020 && two.SecondModelFileId == 80030 && two.Notes.Any(n => n.Contains("second model")) && low.Ok && low.Modifier == 3 && low.AppearanceId == 9011 && low.DisplayInfoId == 604
                && dup.Ok && dup.AppearanceId == 9005 && dup.DisplayInfoId == 7002 && dup.DefaultLooks == 2 && dup.Notes.Any(n => n.Contains("2 displays")) && slot1.Ok && slot1.ModelIndex == 1 && slot1.ModelFileId == 80040 && slot1.TextureFileId == 90040,
                two + " | " + low + " | " + dup + " | " + slot1);
            var chest = ModelData.ResolveItem(set, 2004, 5); var hearth = ModelData.ResolveItem(set, 6948, 0); var unknown = ModelData.ResolveItem(set, 19019, -1);
            var noFile = ModelData.ResolveItem(set, 2005, 13); var noModel = ModelData.ResolveItem(set, 2006, 13); var noApp = ModelData.ResolveItem(set, 2007, 13);
            var noLooks = ModelData.ResolveItem(set, 3333, 13); var noSkin = ModelData.ResolveItem(set, 2008, 13);
            Check("items: armour and a Hearthstone are not a card's; an item the item data lacks, one with no appearance row, an appearance or display the game lacks, a model resource without a file - each a reason, nothing guessed",
                chest.Reason == ModelData.NoItemCard && hearth.Reason == ModelData.NoItemCard && unknown.Reason.Contains("item data") && noFile.Reason.Contains("not in the game's ModelFileData") && noModel.Reason.Contains("has no model")
                && noApp.Reason.Contains("not in the game's ItemAppearance") && noLooks.Reason.Contains("no appearance") && !noFile.Ok && !noModel.Ok && !noApp.Ok && !noLooks.Ok,
                chest.Reason + " | " + hearth.Reason + " | " + unknown.Reason + " | " + noFile.Reason + " | " + noModel.Reason + " | " + noApp.Reason + " | " + noLooks.Reason);
            Check("items: a material with no UsageType-0 texture still gives the model (a stand-in skin, noted)", noSkin.Ok && noSkin.TextureFileId == 0 && noSkin.Notes.Any(n => n.Contains("no texture")), noSkin.ToString());

            // ---- an unknown layout: only that table off, the creatures untouched
            var rawNew = MdRaw(); MiAdd(rawNew, MiDisplays(0x12345678));
            var setNew = ModelSet.Parse(rawNew);
            var rn = ModelData.ResolveItem(setNew, 19019, 13);
            Check("items: an unknown ItemDisplayInfo layout switches that table off ('game updated'), the other item tables and the creature tables still read, and the item's reason says so",
                setNew.ItemDisplays == null && setNew.Problems.TryGetValue("ItemDisplayInfo", out var pi) && pi.StartsWith("game updated") && setNew.ItemLooks?.Count == 10 && setNew.Displays?.Count == 5 && !rn.Ok && rn.Reason.StartsWith("game updated")
                && rn.AppearanceId == 9001, rn.Reason);
            var noItems = ModelSet.Parse(MdRaw(noItems: true));
            Check("items: an install without the item tables keeps the creatures (no 'all' problem) and each item table says it is not there",
                noItems.Displays?.Count == 5 && !noItems.Problems.ContainsKey("all") && ModelTables.ItemTables.All(d => noItems.Problems.TryGetValue(d.Name, out var w) && w.Contains("not in this game install")), string.Join("; ", noItems.Problems.Values));

            // ---- the saved index (format 2)
            var cf = Path.Combine(work, "cache", "modeldata-items.bin");
            bool wrote = ModelIndexFile.TryWrite(cf, "wowf|1.60.1.70707|abc", set);
            var back = ModelIndexFile.TryRead(cf);
            var tb = back == null ? null : ModelData.ResolveItem(back.Set, 19019, 13);
            var lb = back == null ? null : ModelData.ResolveItem(back.Set, 2001, 21);
            Check("items: the item tables survive a save (format 2) - the same chain, the rows' order kept (the first model file, the lowest OrderIndex)",
                wrote && back != null && ModelIndexFile.FormatVersion == 2 && back.Set.ItemLooks.Count == 10 && back.Set.ItemDisplays.Count == 8 && tb.Ok && tb.ModelFileId == 80009 && tb.TextureFileId == 90001 && lb.AppearanceId == 9011
                && back.Set.ModelFiles[501].Select(f => f.FileId).SequenceEqual(new[] { 80009, 80001 }) && back.Set.LayoutsSeen.Count == 7, tb?.ToString());
        }

        // ============================================================================================================================ the model
        // A small item: a "sword" 3 long along WoW's x (-0.4 .. 2.6: the grip, the model's origin, near one end), 0.3 across y, 0.1 thick in z - two crossed
        // quads (8 vertices) in two sub-meshes (both id 0), 3 batches: the blade the item's skin (texture type 2), the cross quad TXID 5001 (type 0) and over
        // it a character skin (type 1). bones: none (a still model), or one bone turning in a Stand sequence (2 keys, 1000 ms).
        static (byte[] root, byte[] skin) CmItem(bool bones, int skinFdid = 3101, bool shield = false)
        {
            var md = new CmW(0x130);
            md.Set(0, BitConverter.ToInt32(Encoding.ASCII.GetBytes("MD20"), 0)); md.Set(4, 274);
            int no = md.Here; md.W.Write(Encoding.ASCII.GetBytes("TestSword\0")); md.Arr(0x08, 10, no);
            if (bones)
            {
                int so = md.Here; var sq = new byte[64]; BitConverter.GetBytes(1000).CopyTo(sq, 4); BitConverter.GetBytes(0x20).CopyTo(sq, 12); BitConverter.GetBytes((short)-1).CopyTo(sq, 60); md.W.Write(sq);
                md.Arr(0x1C, 1, so);
                int bo = md.Here; md.W.Write(new byte[88]); md.Arr(0x2C, 1, bo);
                md.Set(bo, -1); md.Set16(bo + 8, -1);
                CmTrackW(md, bo + 16, 0, -1, new (uint[], byte[])[] { (null, null) });
                CmTrackW(md, bo + 36, 1, -1, new[] { (new uint[] { 0, 500 }, CmQuat(32767, 32767, 32767, -1, -13108, 32767, 32767, -6554)) });
                CmTrackW(md, bo + 56, 0, -1, new (uint[], byte[])[] { (null, null) });
                int blo = md.Here; md.W.Write((ushort)0); md.W.Write((ushort)0); md.Arr(0x78, 1, blo);
            }
            var pos = shield ? new[]
            {
                // 3.7: a "shield" - a square 1 x 1 face 0.3 in front of the arm (the origin) along WoW's x, a smaller back plate at 0.25
                (0.3f, -0.5f, -0.5f), (0.3f, 0.5f, -0.5f), (0.3f, 0.5f, 0.5f), (0.3f, -0.5f, 0.5f),
                (0.25f, -0.4f, -0.4f), (0.25f, 0.4f, -0.4f), (0.25f, 0.4f, 0.4f), (0.25f, -0.4f, 0.4f),
            } : new[]
            {
                (-0.4f, -0.15f, 0f), (2.6f, -0.15f, 0f), (2.6f, 0.15f, 0f), (-0.4f, 0.15f, 0f),
                (-0.4f, 0f, -0.05f), (2.6f, 0f, -0.05f), (2.6f, 0f, 0.05f), (-0.4f, 0f, 0.05f),
            };
            int vo = md.Here;
            for (int i = 0; i < pos.Length; i++)
            {
                md.W.Write(pos[i].Item1); md.W.Write(pos[i].Item2); md.W.Write(pos[i].Item3);
                md.W.Write(new byte[] { 255, 0, 0, 0 }); md.W.Write(new byte[4]);
                md.W.Write(0f); md.W.Write(0f); md.W.Write(1f); md.W.Write((i & 1) * 1f); md.W.Write((i >> 1 & 1) * 1f); md.W.Write(0f); md.W.Write(0f);
            }
            md.Arr(0x3C, pos.Length, vo); md.Set(0x44, 1);
            int to = md.Here; foreach (var (type, flags) in new[] { (2, 0), (0, 3), (1, 0) }) { md.W.Write(type); md.W.Write(flags); md.W.Write(0); md.W.Write(0); }
            md.Arr(0x50, 3, to);
            int mo = md.Here; md.W.Write((ushort)0); md.W.Write((ushort)0); md.W.Write((ushort)0x4); md.W.Write((ushort)1); md.Arr(0x70, 2, mo);
            int lo = md.Here; md.W.Write((ushort)0); md.W.Write((ushort)1); md.W.Write((ushort)2); md.Arr(0x80, 3, lo);
            md.SetF(0xA0, -0.4f); md.SetF(0xA4, -0.15f); md.SetF(0xA8, -0.05f); md.SetF(0xAC, 2.6f); md.SetF(0xB0, 0.15f); md.SetF(0xB4, 0.05f); md.SetF(0xB8, 1.6f);
            var mdb = md.Bytes;

            var sk = new CmW(48);
            sk.Set(0, BitConverter.ToInt32(Encoding.ASCII.GetBytes("SKIN"), 0));
            int sv = sk.Here; for (int i = 0; i < 8; i++) sk.W.Write((ushort)i); sk.Arr(4, 8, sv);
            int si = sk.Here; foreach (var x in new ushort[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 }) sk.W.Write(x); sk.Arr(12, 12, si);
            int sb = sk.Here; for (int i = 0; i < 8; i++) sk.W.Write(new byte[4]); sk.Arr(20, 8, sb);
            int ss = sk.Here;
            for (int k = 0; k < 2; k++)
            {
                var s = new byte[48];
                BitConverter.GetBytes((ushort)(k * 4)).CopyTo(s, 4); BitConverter.GetBytes((ushort)4).CopyTo(s, 6);
                BitConverter.GetBytes((ushort)(k * 6)).CopyTo(s, 8); BitConverter.GetBytes((ushort)6).CopyTo(s, 10); BitConverter.GetBytes((ushort)(bones ? 1 : 0)).CopyTo(s, 12);
                sk.W.Write(s);
            }
            sk.Arr(28, 2, ss);
            int sbt = sk.Here;
            foreach (var (sub, mat, combo) in new[] { (0, 0, 0), (1, 0, 1), (1, 1, 2) })
            {
                var b = new byte[24];
                BitConverter.GetBytes((ushort)sub).CopyTo(b, 4); BitConverter.GetBytes((ushort)0xFFFF).CopyTo(b, 8); BitConverter.GetBytes((ushort)mat).CopyTo(b, 10);
                BitConverter.GetBytes((ushort)1).CopyTo(b, 14); BitConverter.GetBytes((ushort)combo).CopyTo(b, 16); BitConverter.GetBytes((ushort)0xFFFF).CopyTo(b, 20);
                sk.W.Write(b);
            }
            sk.Arr(36, 3, sbt);

            var rs = new MemoryStream(); var rw = new BinaryWriter(rs);
            void Chunk(string tag, byte[] data) { rw.Write(Encoding.ASCII.GetBytes(tag)); rw.Write(data.Length); rw.Write(data); }
            Chunk("MD21", mdb);
            Chunk("SFID", BitConverter.GetBytes(skinFdid));
            Chunk("TXID", BitConverter.GetBytes(0).Concat(BitConverter.GetBytes(5001)).Concat(BitConverter.GetBytes(0)).ToArray());
            rw.Flush();
            return (rs.ToArray(), sk.Bytes);
        }

        // the files of the item fixture: the still sword (80009, its skin 3101), the turning one (80020 / 3102), the textures; null = not in the "install"
        static Func<int, byte[]> MiFiles(byte[] blp, Func<int, byte[]> also = null)
        {
            var (still, stillSkin) = CmItem(false);
            var (turningRoot, turningSkin) = CmItem(true, 3102);      // the turning sword names its own skin file
            var (shieldRoot, shieldSkin) = CmItem(false, 3103, shield: true);
            return f => f == 80009 ? still : f == 80040 ? shieldRoot : f == 3103 ? shieldSkin : f == 3101 ? stillSkin : f == 80020 ? turningRoot : f == 3102 ? turningSkin : f == 90001 || f == 90020 || f == 90040 || f == 5001 ? blp : also?.Invoke(f);
        }

        async Task ModelItemBuildChecks(string work, ModelSet itemSet, Action<string, bool, string> Check)
        {
            bool Near(float a, float b, float e = 1e-3f) => Math.Abs(a - b) <= e;
            Check("item build rules: texture type 2 (object skin) = the item's skin when given, else none; a creature model leaves type 2 a stand-in (no skin given)",
                CreatureModel.TextureFor(2, 0, new[] { 0 }, null, 90001) == 90001 && CreatureModel.TextureFor(2, 0, new[] { 0 }, null) == 0 && CreatureModel.KnownTextureType(2, true) && !CreatureModel.KnownTextureType(2, false)
                && CreatureModel.KnownTextureType(11, false) && !CreatureModel.KnownTextureType(11, true) && CreatureModel.KnownTextureType(0, true), null);
            var (root, skin) = CmItem(false);
            var m = CreatureModel.ParseItem(root, skin, 90001, _ => throw new InvalidOperationException("no .anim is needed"), 7001);
            var b0 = m.Batches.ElementAtOrDefault(0); var b1 = m.Batches.ElementAtOrDefault(1); var b2 = m.Batches.ElementAtOrDefault(2);
            Check("item build: the still sword - kind item, no bones, 8 vertices / 12 indices in 2 sub-meshes (both id 0: every geoset shown), the blade's skin is the item's (type 2 -> 90001), the cross quad TXID 5001, the character skin a stand-in noted as not the item's",
                m.Ok && m.Drawable && m.Kind == "item" && m.Bones.Count == 0 && m.Nv == 8 && m.Idx.Count == 12 && m.Subs.Count == 2 && m.ShownSubs == 2 && b0?.TexType == 2 && b0.Tex == 90001 && b1?.Tex == 5001 && b2?.TexType == 1 && b2.Tex == 0
                && m.Textures.SequenceEqual(new[] { 90001, 5001 }) && m.Notes.Any(n => n.Contains("not the item's skin")) && m.Notes.Any(n => n.Contains("still model")), m.Problem + " " + string.Join("; ", m.Notes));
            Check("item build: a model without bones - every vertex weighs 255 on the one stand-in bone, nothing counted as put right, no animation problem; the axes as for creatures (WoW x -> z)",
                Enumerable.Range(0, m.Nv).All(i => m.Bw[i * 4] == 255 && m.Bw[i * 4 + 1] == 0 && m.Bi[i * 4] == 0) && m.FixedWeights == 0 && m.AnimProblem == null && m.AnimIndex == -1
                && Near(m.Min[2], -0.4f) && Near(m.Max[2], 2.6f) && Near(m.Max[0], 0.15f) && Near(m.Max[1], 0.05f), "fixed " + m.FixedWeights + " " + m.AnimProblem + " min " + string.Join(",", m.Min) + " max " + string.Join(",", m.Max));
            var bytes = m.ToBytes(); var meta = CreatureModel.ReadMeta(bytes, out _);
            Check("item build: the file says kind \"item\", static true, its display 7001, no bones, the format the viewer reads (v 1)",
                meta != null && meta.Str("kind") == "item" && meta.Bool("static") && meta.Int("display") == 7001 && meta.List("bones").Count == 0 && meta.Int("v") == CreatureModel.FormatVersion, meta == null ? "no meta" : meta.Str("kind"));
            var (rootB, skinB) = CmItem(true);
            var mb = CreatureModel.ParseItem(rootB, skinB, 0, _ => null, 7002);
            Check("item build: the turning sword - one bone, Stand (sequence 0, 1000 ms) with its 2 rotation keys, not static; no skin given = the blade a stand-in, noted",
                mb.Ok && mb.Bones.Count == 1 && mb.AnimIndex == 0 && mb.AnimDuration == 1000 && mb.Bones[0].R.Keys == 2 && mb.AnimProblem == null && mb.Batches[0].Tex == 0 && mb.Notes.Any(n => n.Contains("no texture"))
                && CreatureModel.ReadMeta(mb.ToBytes(), out _)?.Bool("static") == false, mb.Problem + " " + string.Join("; ", mb.Notes));
            var creatureSame = CreatureModel.Parse(root, skin, new[] { 90001, 0, 0, 0 }, 1, null, 7001);
            Check("item build: the same file read as a creature leaves texture type 2 a stand-in (the creature rule is unchanged)", creatureSame.Ok && creatureSame.Kind == "creature" && creatureSame.Batches[0].Tex == 0 && creatureSame.Notes.Any(n => n.Contains("not a creature skin")), null);

            // ---- the cache: m\item-<display>.m2b2 beside the creature files
            var savedRoot = CreatureModels.RootOverride; var savedSrc = CreatureModels.SourceForTest; var savedBuild = CreatureModels.BuildForTest;
            try
            {
                CreatureModels.ResetForTest();
                CreatureModels.RootOverride = Path.Combine(work, "items", "atlas", "models");
                var blp = ObjBlp();
                var (croot, cskin, _) = CmModel();
                var files = MiFiles(blp, f => f == 126487 ? croot : f == 3001 ? cskin : f == 6001 ? blp : null);
                var src = new FuncModelSource(files);
                CreatureModels.SourceForTest = () => src;
                CreatureModels.BuildForTest = () => "wowf|1.60.1.70707|2417084bcafe";
                itemSet.Displays = new Dictionary<int, CreatureDisplay> { [604] = new CreatureDisplay { Id = 604, ModelId = 43, Scale = 0.71f, Textures = new[] { 6001, 0, 0, 0 } } };
                itemSet.Models = new Dictionary<int, CreatureModelInfo> { [43] = new CreatureModelInfo { Id = 43, FileId = 126487, Scale = 1 } };
                ModelData.SetForTest(itemSet);
                var key = CreatureModels.KeyFor("wowf|1.60.1.70707|2417084bcafe");
                var t1 = CreatureModels.EnsureItemAsync(7001); var t2 = CreatureModels.EnsureItemAsync(7001);
                var r1 = await t1; await t2;
                Check("item cache: two calls at once share one build; the file is <root>\\<build>_m2\\m\\item-7001.m2b2, its textures (the item's skin 90001, TXID 5001) packed",
                    ReferenceEquals(t1, t2) && r1.Ok && r1.Built && r1.Path == CreatureModels.ItemModelPath(key, 7001) && r1.Path.EndsWith(Path.Combine("m", "item-7001.m2b2")) && File.Exists(r1.Path)
                    && File.Exists(CreatureModels.TexturePath(key, 90001)) && File.Exists(CreatureModels.TexturePath(key, 5001)) && r1.MissingTextures.Count == 0 && CreatureModels.BuildCountForTest == 1, r1.ToString());
                var ri = await CreatureModels.EnsureItemAsync(604); var rc = await CreatureModels.EnsureAsync(604);
                var mi = ri.Ok ? CreatureModel.ReadMeta(File.ReadAllBytes(ri.Path), out _) : null; var mc = rc.Ok ? CreatureModel.ReadMeta(File.ReadAllBytes(rc.Path), out _) : null;
                Check("item cache: item display 604 and creature display 604 are two files (item-604.m2b2: an item, 604.m2b2: the creature) - they never clash",
                    ri.Ok && rc.Ok && ri.Path != rc.Path && mi?.Str("kind") == "item" && mc?.Str("kind") == "creature" && mc.Int("fdid") == 126487 && mi.Int("fdid") == 80009, ri + " | " + rc);
                int reads = src.Reads;
                var again = await CreatureModels.EnsureItemAsync(7001);
                Check("item cache: asked again = the file at once (no build, nothing read)", again.Ok && !again.Built && again.Path == r1.Path && src.Reads == reads, again.ToString());
                var r2 = await CreatureModels.EnsureItemAsync(7002);
                var m2 = r2.Ok ? CreatureModel.ReadMeta(File.ReadAllBytes(r2.Path), out _) : null;
                Check("item cache: a display with two models is built from the first, the second named in the file (80030), the turning one keeps its bone",
                    r2.Ok && m2 != null && m2.Int("fdid") == 80020 && m2.Int("second") == 80030 && m2.List("bones").Count == 1 && !m2.Bool("static"), r2.ToString());
                int builds = CreatureModels.BuildCountForTest;
                var bad = await CreatureModels.EnsureItemAsync(7005); var none = await CreatureModels.EnsureItemAsync(7050); var bad2 = await CreatureModels.EnsureItemAsync(7005);
                Check("item cache: a display without a model file and one without a model give their reasons and no file; asked again, the kept reason (no new build)",
                    !bad.Ok && bad.Reason.Contains("ModelFileData") && !none.Ok && none.Reason.Contains("has no model") && !bad2.Ok && bad2.Reason == bad.Reason && CreatureModels.BuildCountForTest == builds
                    && !File.Exists(CreatureModels.ItemModelPath(key, 7005)), bad.Reason + " | " + none.Reason);
            }
            finally
            {
                CreatureModels.RootOverride = savedRoot; CreatureModels.SourceForTest = savedSrc; CreatureModels.BuildForTest = savedBuild; CreatureModels.ResetForTest();
                ModelData.ResetForTest();
            }
        }

        // ============================================================================================================================ the page
        // the item pages: a sword (19019, one-hand, the still sword), a turning two-hand (2000), a chest piece (2004) and a Hearthstone (6948)
        async Task MvItemChecks(string work, ModelSet creatures, Action<string, bool, string> Check, List<string> notes)
        {
            var savedSrc = CreatureModels.SourceForTest; var savedItems = ItemData.Snapshot; var savedItemSt = ItemData.Status;
            try
            {
                var raw = MdRaw(); var set = ModelSet.Parse(raw);
                set.Displays = creatures.Displays; set.Models = creatures.Models;
                ModelData.SetForTest(set);
                var blp = ObjBlp();
                var (croot, cskin, _) = CmModel();
                CreatureModels.SourceForTest = () => new FuncModelSource(MiFiles(blp, f => f == 126487 ? croot : f == 3001 ? cskin : f == 6001 ? blp : null));
                ClientItem It(int id, string name, int inv, int cls, int sub, int q = 4) => new ClientItem { Id = id, Name = name, Quality = q, ItemLevel = 30, RequiredLevel = 20, InventoryType = inv, ClassId = cls, SubclassId = sub };
                ItemData.SetForTest(new ItemSnapshot(new[] { It(19019, "Test Thunderblade", 13, 2, 7, 5), It(2000, "Test Greatstaff", 17, 2, 10), It(2004, "Test Robe", 5, 4, 1), It(6948, "Test Hearthstone", 0, 15, 0, 1), It(2009, "Test Ward", 23, 4, 0, 3) }, new ClientItem[0]), ItemData.State.Ready);
                Width = 1800; Height = 1000;
                await AtlasView.Open("item:19019"); UpdateLayout();
                await Until(() => AtlasView.WideForTest);
                Check("item card: a one-hand weapon's page (19019) has the Model card at the top of the rail (wide), 260 high, for that item",
                    AtlasView.ModelCardForTest != null && AtlasView.ModelCardInRailForTest && AtlasView.ModelItemForTest == 19019 && AtlasView.ModelEntryForTest == 0 && Math.Abs(AtlasView.ModelCardForTest.Height - ElansAddonHub.AtlasPage.ModelCardHeight) < 0.5,
                    "card " + (AtlasView.ModelCardForTest != null) + " rail " + AtlasView.ModelCardInRailForTest + " status " + AtlasView.ModelStatusForTest);
                await AtlasView.ModelResolveForTest;
                bool shown = await MvShown(() => AtlasView.ModelViewForTest);
                var v = AtlasView.ModelViewForTest;
                Dictionary<string, object> st = null;
                var tw = System.Diagnostics.Stopwatch.StartNew();
                while (shown && tw.ElapsedMilliseconds < 8000) { st = await MvState(v); if (D(st, "texLoaded") + D(st, "texFailed") >= D(st, "texWanted")) break; await Task.Delay(100); }
                Check("item viewer: the still sword is shown (item-7001.m2b2) - kind item, a plain mesh (no bones, no skeleton), 8 vertices, 3 batches, both textures read; no look switch",
                    shown && st?.Str("state") == "shown" && st.Str("kind") == "item" && st.Bool("still") && D(st, "bones") == 0 && D(st, "nv") == 8 && D(st, "batches") == 3 && D(st, "texLoaded") == 2 && D(st, "texFailed") == 0
                    && v.ModelForTest.EndsWith("/m/item-7001.m2b2") && AtlasView.LookCountForTest == 0 && AtlasView.ModelStatusForTest == "",
                    "shown " + shown + " " + v?.State + " " + v?.Reason + " status '" + AtlasView.ModelStatusForTest + "' " + Json.Write(st));
                if (!shown || v == null) return;
                // framing: the longest side up the card, leaned, the whole of it in view at every turn and filling it
                var boxes = new List<Dictionary<string, object>>();
                foreach (var yaw in new[] { 0.0, 0.7, 1.6, 2.4, 3.3, 4.7 }) boxes.Add(Json.Obj(await v.EvalForTest("mv.screenBox(" + yaw.ToString(CultureInfo.InvariantCulture) + ")") ?? "null"));
                await v.EvalForTest("mv.screenBox(0.7)");
                var vw = Json.Obj(await v.EvalForTest("mv.viewOf()") ?? "null");
                bool inView = boxes.All(b => b != null && D(b, "x0") >= -1 && D(b, "x1") <= 1 && D(b, "y0") >= -1 && D(b, "y1") <= 1);
                double fill = boxes.Min(b => b == null ? 0 : Math.Max(D(b, "x1") - D(b, "x0"), D(b, "y1") - D(b, "y0")));
                var ends = Json.Obj(await v.EvalForTest("(function(){ mv.opening(); return mv.ends(); })()") ?? "null");
                Check("item viewer: a sword (one-hand, not a staff) keeps the grip rule - the far end from the grip (the blade's tip, 2.6 from the origin against 0.4) is the upper end on the screen",
                    st.Str("up") == "grip" && ends != null && D(ends.Child("hi"), "y") > D(ends.Child("lo"), "y") + 0.5, Json.Write(ends));
                Check("item viewer: a long thin item is framed by its longest side - leaned (" + (D(vw, "tilt") * 180 / Math.PI).ToString("0", CultureInfo.InvariantCulture) + " degrees), seen straight on, inside the view at every turn and filling it (the smallest span " + fill.ToString("0.00", CultureInfo.InvariantCulture) + " of 2)",
                    st.Bool("thin") && D(vw, "tilt") > 0.15 && D(vw, "tilt") < 0.8 && D(vw, "yaw0") == 0 && inView && fill > 1.0,
                    string.Join(" | ", boxes.Select(b => Json.Write(b))) + " view " + Json.Write(vw));
                var pr = Json.Obj(await v.EvalForTest("mv.probe()"));
                Check("item viewer: a drawn frame is not blank (" + (D(pr, "frac") * 100).ToString("0.0", CultureInfo.InvariantCulture) + " % lit)", D(pr, "frac") > 0.005 && D(pr, "frac") < 0.9, Json.Write(pr));
                if (SelfTestClock.ShotsOn) { await Task.Delay(300); var png = await v.CaptureForTest(); SnapshotWithViewer(Path.Combine(ModelShots(work), "models-item-card.png"), v, png); }

                Width = 1100;
                await Until(() => !AtlasView.WideForTest && AtlasView.ModelCardOverviewIndexForTest >= 0);
                Check("item card: narrow (1100) - in the Overview right after the tooltip card (index 1), the same viewer", AtlasView.ModelCardOverviewIndexForTest == 1 && ReferenceEquals(AtlasView.ModelViewForTest, v),
                    "index " + AtlasView.ModelCardOverviewIndexForTest);
                Width = 1800; await Until(() => AtlasView.ModelCardInRailForTest);

                await AtlasView.Open("item:2000"); UpdateLayout(); await AtlasView.ModelResolveForTest;
                bool turning = await MvShown(() => AtlasView.ModelViewForTest);
                var v2 = AtlasView.ModelViewForTest; var s2 = await MvState(v2);
                double changed = turning ? await MvNum(v2, "(function(){ mv.snap(); mv.step(250); return mv.changed(); })()") : -1;
                Check("item viewer: a two-hand item with a bone and a Stand (2000, display 7002: the first of its two models) is skinned and turns (1 bone, its matrix moves); a staff (subclass 10) is sent with up = head",
                    turning && s2?.Str("kind") == "item" && !s2.Bool("still") && D(s2, "bones") == 1 && changed == 1 && v2.ModelForTest.EndsWith("/m/item-7002.m2b2") && s2.Str("up") == "head" && s2.Child("head") != null
                    && ModelData.ItemUpRule(2000) == "head" && ModelData.ItemUpRule(19019) == null, Json.Write(s2) + " changed " + changed);

                // a held-in-off-hand item (2009, a flat "ward" whose face is 0.3 in front of the arm): it opens facing the camera and sways +-35 degrees
                await AtlasView.Open("item:2009"); UpdateLayout(); await AtlasView.ModelResolveForTest;
                bool ward = await MvShown(() => AtlasView.ModelViewForTest);
                var v3 = AtlasView.ModelViewForTest; var s3 = await MvState(v3);
                double face0 = ward ? await MvNum(v3, "(function(){ mv.opening(); return mv.facing(); })()") : double.NaN;
                var offs = new List<double>(); var faces = new List<double>();
                for (int k = 0; ward && k < 40; k++) { offs.Add(await MvNum(v3, "mv.sway(250)")); faces.Add(await MvNum(v3, "mv.facing()")); }
                Check("item viewer: a flat held-in-off-hand item opens with its front (away from the arm) facing the camera (" + face0.ToString("0.000", CultureInfo.InvariantCulture) + ") and sways +-35 degrees instead of turning round (offsets "
                    + (offs.Count > 0 ? offs.Min().ToString("0.00", CultureInfo.InvariantCulture) + " .. " + offs.Max().ToString("0.00", CultureInfo.InvariantCulture) : "-") + ", facing never under " + (faces.Count > 0 ? faces.Min().ToString("0.00", CultureInfo.InvariantCulture) : "-") + ")",
                    ward && s3.Bool("sway") && !s3.Bool("thin") && face0 > 0.97 && offs.Max() <= 0.611 && offs.Min() >= -0.611 && offs.Max() > 0.55 && offs.Min() < -0.55 && faces.Min() > 0.78,
                    Json.Write(s3) + " faces " + string.Join(",", faces.Select(f => f.ToString("0.00", CultureInfo.InvariantCulture))));

                await AtlasView.Open("item:2004"); UpdateLayout();
                bool chest = AtlasView.ModelCardForTest == null && AtlasView.ModelViewForTest == null;
                await AtlasView.Open("item:6948"); UpdateLayout();
                bool hearth = AtlasView.ModelCardForTest == null;
                await AtlasView.Open("item:424242"); UpdateLayout();
                bool unknownItem = AtlasView.ModelCardForTest == null;
                Check("item card: armour (a robe), a Hearthstone (inventory type 0) and an item the game's data does not know get no card", chest && hearth && unknownItem, "chest " + chest + " hearth " + hearth + " unknown " + unknownItem);

                ModelData.SetForTest(null, ModelData.State.Unavailable, "game updated (test)");
                await AtlasView.Open("item:19019"); UpdateLayout(); await AtlasView.ModelResolveForTest;
                Check("item card: model data unavailable - the card says why in one calm line", AtlasView.ModelStatusForTest.StartsWith("The game's model data is not available") && AtlasView.ModelStatusForTest.Contains("game updated (test)"), AtlasView.ModelStatusForTest);
                var noFile = ModelSet.Parse(MdRaw()); noFile.ModelFiles = new Dictionary<int, List<ModelFileRow>>();
                ModelData.SetForTest(noFile);
                await AtlasView.Open("item:2000"); await AtlasView.Open("item:19019"); UpdateLayout(); await AtlasView.ModelResolveForTest;
                Check("item card: an item whose model is not in this game build - one calm line, no viewer started", AtlasView.ModelStatusForTest == "There is no model for this item in this game build." && AtlasView.ModelViewForTest?.HasWebForTest == false, AtlasView.ModelStatusForTest);
            }
            finally
            {
                CreatureModels.SourceForTest = savedSrc;
                ItemData.SetForTest(savedItems, savedItemSt);
                ModelData.SetForTest(creatures);
            }
        }
    }
}
