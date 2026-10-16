using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Db2;

namespace ElansAddonHub
{
    // --selftest "itemdata": Atlas layer 1 (2.48.0), item data read from the player's own WoW files.
    // Everything runs on tables built in the test (a small WDC5 writer lives here), under the scratch folder. The real game files are only touched by
    // the last, optional check (read-only, skipped when there is no WoW install).
    public partial class MainWindow
    {
        // ---- a tiny WDC5 writer (the format the reader is written against: wowdev.wiki)
        sealed class W5Field { public int OffsetBits, SizeBits, Storage; public uint C0, C1, C2; public uint[] Pallet; public KeyValuePair<int, uint>[] Common; }
        sealed class W5Section { public ulong Key; public byte[][] Records; public byte[] Strings = new byte[0]; public int[] Ids; public int[][] Copies = new int[0][]; public int RecordCount => Records.Length; }

        static void PutBits(byte[] rec, long bitPos, int bits, long value)
        {
            for (int i = 0; i < bits; i++)
            {
                if (((value >> i) & 1) == 0) continue;
                long b = bitPos + i;
                rec[b >> 3] |= (byte)(1 << (int)(b & 7));
            }
        }

        static byte[] BuildWdc5(uint tableHash, uint layoutHash, bool sparse, int recordSize, W5Field[] fields, W5Section[] sections)
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            var pallet = new MemoryStream(); var common = new MemoryStream();
            var infos = new MemoryStream();
            foreach (var f in fields)
            {
                int add = 0;
                if (f.Storage == 3 || f.Storage == 4) { foreach (var v in f.Pallet) pallet.Write(BitConverter.GetBytes(v), 0, 4); add = f.Pallet.Length * 4; }
                if (f.Storage == 2) { foreach (var kv in f.Common) { common.Write(BitConverter.GetBytes(kv.Key), 0, 4); common.Write(BitConverter.GetBytes(kv.Value), 0, 4); } add = f.Common.Length * 8; }
                var ib = new BinaryWriter(infos);
                ib.Write((ushort)f.OffsetBits); ib.Write((ushort)f.SizeBits); ib.Write(add); ib.Write(f.Storage); ib.Write(f.C0); ib.Write(f.C1); ib.Write(f.C2);
            }
            int headLen = 204 + 40 * sections.Length + 4 * fields.Length + (int)infos.Length + (int)pallet.Length + (int)common.Length;
            // section bodies: [records][strings][id list][copy table][offset map]
            var bodies = new List<byte[]>(); var hdrs = new List<int[]>();
            long at = headLen;
            int totalRecords = 0, totalStrings = 0, minId = int.MaxValue, maxId = 0;
            foreach (var s in sections)
            {
                var body = new MemoryStream(); var bw = new BinaryWriter(body);
                var om = new List<KeyValuePair<int, int>>();
                foreach (var r in s.Records) { om.Add(new KeyValuePair<int, int>((int)(at + body.Length), r.Length)); bw.Write(r); }
                int recEnd = (int)(at + body.Length);
                bw.Write(s.Strings);
                int fileOffset = (int)at;
                foreach (var id in s.Ids) { bw.Write(id); minId = Math.Min(minId, id); maxId = Math.Max(maxId, id); }
                foreach (var c in s.Copies) { bw.Write(c[0]); bw.Write(c[1]); }
                if (sparse) foreach (var o in om) { bw.Write(o.Key); bw.Write((ushort)o.Value); }
                bodies.Add(body.ToArray());
                // file_offset, record_count, string_table_size, offset_records_end, id_list_size, relationship_data_size, offset_map_id_count, copy_table_count
                hdrs.Add(new[] { fileOffset, s.RecordCount, s.Strings.Length, sparse ? recEnd : (int)(at + body.Length - s.Ids.Length * 4 - s.Copies.Length * 8 - (sparse ? om.Count * 6 : 0)), s.Ids.Length * 4, 0, sparse ? om.Count : 0, s.Copies.Length });
                at += body.Length; totalRecords += s.RecordCount; totalStrings += s.Strings.Length;
            }
            w.Write(0x35434457); w.Write(5);
            var schema = new byte[128]; var sb = Encoding.ASCII.GetBytes("wowstatic_selftest"); Array.Copy(sb, schema, sb.Length); w.Write(schema);
            w.Write(totalRecords); w.Write(fields.Length); w.Write(recordSize); w.Write(totalStrings); w.Write(tableHash); w.Write(layoutHash);
            w.Write(minId == int.MaxValue ? 0 : minId); w.Write(maxId); w.Write(0);
            w.Write((ushort)(sparse ? 1 : 0)); w.Write((ushort)0);
            w.Write(fields.Length); w.Write(0); w.Write(0); w.Write((int)infos.Length); w.Write((int)common.Length); w.Write((int)pallet.Length); w.Write(sections.Length);
            for (int i = 0; i < sections.Length; i++)
            {
                w.Write(sections[i].Key);
                foreach (var v in hdrs[i]) w.Write(v);
            }
            w.Write(new byte[4 * fields.Length]);
            w.Write(infos.ToArray()); w.Write(pallet.ToArray()); w.Write(common.ToArray());
            foreach (var b in bodies) w.Write(b);
            return ms.ToArray();
        }

        // the table of every kind of field: ids 10..13 plus a copy 14 <- 11, a locked section with two rows that must be skipped
        static byte[] FixedTable(out int[] expectCount)
        {
            var fields = new[]
            {
                new W5Field { OffsetBits = 0, SizeBits = 32, Storage = 0 },                                                                     // f0 plain int
                new W5Field { OffsetBits = 32, SizeBits = 5, Storage = 1, C0 = 32, C1 = 5 },                                                    // f1 bit-packed
                new W5Field { OffsetBits = 37, SizeBits = 6, Storage = 5, C0 = 37, C1 = 6 },                                                    // f2 bit-packed signed
                new W5Field { OffsetBits = 43, SizeBits = 3, Storage = 3, C0 = 43, C1 = 3, Pallet = new uint[] { 100, 200, 300, 400 } },       // f3 pallet
                new W5Field { Storage = 2, C0 = 7, Common = new[] { new KeyValuePair<int, uint>(11, 99), new KeyValuePair<int, uint>(12, 98) } }, // f4 common data, default 7
                new W5Field { OffsetBits = 64, SizeBits = 32, Storage = 0 },                                                                    // f5 string (offset into the string block)
                new W5Field { OffsetBits = 46, SizeBits = 3, Storage = 4, C0 = 46, C1 = 3, C2 = 2, Pallet = new uint[] { 10, 11, 20, 21, 30, 31, 40, 41 } },   // f6 pallet array of 2
            };
            var strings = Encoding.UTF8.GetBytes("\0alpha\0beta\0gamma\0delta\0");
            int[] strAt = { 1, 7, 12, 18 };
            var recs = new byte[4][];
            for (int k = 0; k < 4; k++)
            {
                var r = new byte[12];
                PutBits(r, 0, 32, (10 + k) * 3);
                PutBits(r, 32, 5, k + 1);
                PutBits(r, 37, 6, -5 - k);
                PutBits(r, 43, 3, k % 4);
                PutBits(r, 46, 3, (k + 1) % 4);
                recs[k] = r;
            }
            // the string offsets depend on where section 0 starts; build once to learn it, then again with the right offsets
            byte[] Build(int[] off)
            {
                for (int k = 0; k < 4; k++) PutBits(recs[k], 64, 32, off[k]);
                var sec0 = new W5Section { Records = recs, Strings = strings, Ids = new[] { 10, 11, 12, 13 }, Copies = new[] { new[] { 14, 11 } } };
                var locked = new W5Section { Key = 0xDEADBEEF12345678UL, Records = new[] { new byte[12], new byte[12] }, Ids = new[] { 90, 91 } };
                return BuildWdc5(0x11112222, 0x33334444, false, 12, fields, new[] { sec0, locked });
            }
            var probe = Build(new int[4]);
            int secStart = BitConverter.ToInt32(probe, 204 + 8);            // section 0 file_offset
            var offs = new int[4];
            for (int k = 0; k < 4; k++) offs[k] = 72 + strAt[k] - (12 * k + 8);   // the client counts the records of ALL sections (6 rows of 12 bytes, the locked ones too) before the string blocks
            expectCount = new[] { 5 };
            return Build(offs);
        }

        // ---- ItemSparse / Item / RandPropPoints in the layouts of build 1.60.1.70338
        static byte[] SparseRec(string name, string desc, int quality, int ilvl, int inv, int req, int bond, uint sell, int delay, int stack, int bagSlots, params int[] stats)
        {
            var ms = new MemoryStream();
            foreach (var s in new[] { desc, "", "", "", name }) { var b = Encoding.UTF8.GetBytes(s); ms.Write(b, 0, b.Length); ms.WriteByte(0); }
            var f = new byte[304];
            void I32(int at, int v) => Array.Copy(BitConverter.GetBytes(v), 0, f, at, 4);
            void U16(int at, int v) => Array.Copy(BitConverter.GetBytes((ushort)v), 0, f, at, 2);
            for (int i = 0; i < 10; i++) I32(116 + i * 4, -1);                    // unused stat slots
            for (int i = 0; i + 1 < stats.Length; i += 2) { I32(116 + (i / 2) * 4, stats[i]); I32(76 + (i / 2) * 4, stats[i + 1]); }
            I32(156, stack); I32(180, (int)sell); U16(272, delay); U16(280, ilvl);
            f[293] = (byte)bond; f[295] = (byte)bagSlots; f[298] = (byte)req; f[299] = (byte)inv; f[300] = (byte)quality;
            ms.Write(f, 0, f.Length);
            return ms.ToArray();
        }

        static byte[] SparseTable(uint layoutHash, bool withRecords = true)
        {
            var sec = new W5Section
            {
                Records = new[]
                {
                    SparseRec("Test Sword", "It cuts.", 3, 20, 13, 10, 1, 12345, 2600, 1, 0, 3, 5000, 7, 2500),
                    SparseRec("Linen Scrap", "", 1, 5, 0, 0, 0, 13, 0, 20, 0),
                    SparseRec("Deprecated Thing", "", 1, 1, 0, 0, 0, 0, 0, 1, 0),
                    SparseRec("Old Blanchy's Blanket", "", 2, 12, 16, 5, 2, 400, 0, 1, 0, 3, 10000),
                    SparseRec("Test Quiver", "", 2, 15, 27, 0, 0, 100, 0, 1, 12),
                },
                Ids = new[] { 1001, 1002, 1003, 1005, 1006 },
                Copies = new[] { new[] { 1004, 1001 } },
            };
            var locked = new W5Section { Key = 0x1122334455667788UL, Records = new[] { SparseRec("Locked Item", "", 5, 60, 13, 60, 1, 1, 1, 1, 0) }, Ids = new[] { 1099 } };
            return BuildWdc5(ItemTables.ItemSparseHash, layoutHash, true, 0, new W5Field[0], withRecords ? new[] { sec, locked } : new[] { locked });
        }

        static byte[] ItemTable()
        {
            // eight plain 32-bit columns; the reader uses ClassID(0) SubclassID(1) InventoryType(3) IconFileDataID(7)
            var fields = Enumerable.Range(0, 8).Select(i => new W5Field { OffsetBits = i * 32, SizeBits = 32, Storage = 0 }).ToArray();
            byte[] Row(int cls, int sub, int inv, int icon) { var r = new byte[32]; Array.Copy(BitConverter.GetBytes(cls), 0, r, 0, 4); Array.Copy(BitConverter.GetBytes(sub), 0, r, 4, 4); Array.Copy(BitConverter.GetBytes(inv), 0, r, 12, 4); Array.Copy(BitConverter.GetBytes(icon), 0, r, 28, 4); return r; }
            var sec = new W5Section { Records = new[] { Row(2, 7, 13, 135000), Row(7, 5, 0, 132000), Row(4, 0, 16, 133000), Row(11, 3, 27, 134000), Row(2, 7, 13, 135000) }, Ids = new[] { 1001, 1002, 1005, 1006, 1004 } };
            return BuildWdc5(ItemTables.ItemHash, 0x9A2A4834, false, 32, fields, new[] { sec });
        }

        // item level budgets for levels 20 and 80 (Epic, Superior, Good: five slot groups each) as pallet arrays
        static byte[] BudgetTable(uint layoutHash = 0x4FD22743)
        {
            var fields = new List<W5Field>();
            for (int i = 0; i < 7; i++) fields.Add(new W5Field { OffsetBits = i * 32, SizeBits = 32, Storage = 0 });
            fields.Add(new W5Field { OffsetBits = 224, SizeBits = 1, Storage = 4, C0 = 224, C1 = 1, C2 = 5, Pallet = new uint[] { 20, 15, 12, 10, 6, 54, 41, 31, 23, 17 } });    // Epic
            fields.Add(new W5Field { OffsetBits = 225, SizeBits = 1, Storage = 4, C0 = 225, C1 = 1, C2 = 5, Pallet = new uint[] { 12, 10, 8, 6, 4, 43, 32, 24, 18, 14 } });      // Superior
            fields.Add(new W5Field { OffsetBits = 226, SizeBits = 1, Storage = 4, C0 = 226, C1 = 1, C2 = 5, Pallet = new uint[] { 8, 6, 5, 4, 3, 33, 24, 18, 14, 10 } });        // Good
            byte[] Row(int idx) { var r = new byte[29]; PutBits(r, 224, 1, idx); PutBits(r, 225, 1, idx); PutBits(r, 226, 1, idx); return r; }
            var sec = new W5Section { Records = new[] { Row(0), Row(1) }, Ids = new[] { 20, 80 } };
            return BuildWdc5(ItemTables.RandPropPointsHash, layoutHash, false, 29, fields.ToArray(), new[] { sec });
        }

        // DBCache.bin: header + entries
        sealed class DbEntry { public uint Table; public int Id; public byte Status = 1; public byte[] Data; }
        static byte[] DbCacheFile(int version, int build, IEnumerable<DbEntry> entries, bool truncateLast = false)
        {
            var ms = new MemoryStream(); var w = new BinaryWriter(ms);
            w.Write(0x48544658); w.Write(version); w.Write(build); w.Write(new byte[32]);
            foreach (var e in entries)
            {
                w.Write(0x48544658); w.Write(1); w.Write(1); w.Write(1); w.Write(e.Table); w.Write(e.Id); w.Write(e.Data.Length); w.Write(e.Status); w.Write(new byte[3]); w.Write(e.Data);
            }
            if (truncateLast) { w.Write(0x48544658); w.Write(1); w.Write(1); w.Write(1); w.Write(ItemTables.ItemSparseHash); w.Write(7777); w.Write(5000); w.Write((byte)1); w.Write(new byte[3]); w.Write(new byte[10]); }   // says 5000 bytes, holds 10
            return ms.ToArray();
        }

        static byte[] ItemRow(int cls, int sub, int inv, int icon)
        {
            var r = new byte[43];
            Array.Copy(BitConverter.GetBytes(cls), 0, r, 0, 4); r[4] = (byte)sub; r[6] = (byte)inv; Array.Copy(BitConverter.GetBytes(icon), 0, r, 13, 4);
            return r;
        }

        static ItemSet SelfTestSet(string dbCachePath)
        {
            var set = ItemData.ParseTables(SparseTable(0x6FCC3191), ItemTable(), BudgetTable());
            ItemData.ReadCacheRows(set, dbCachePath);
            return set;
        }

        delegate void CheckFn(string what, bool cond, string detail = null);

        static string Join(IEnumerable<string> s) => string.Join(" | ", s);

        async Task<string> ItemDataTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            void Check(string what, bool cond, string detail = null) { SelfTestClock.Mark(what); notes.Add((cond ? "ok   " : "FAIL ") + what + (!cond && detail != null ? " [" + detail + "]" : "")); ok &= cond; }
            string P(string n) => Path.Combine(dir, n);
            var work = Path.Combine(dir, "itemdata");
            var savedCacheFile = ItemData.CacheFile; var savedRoot = ItemData.WowRoot; var savedEnabled = ItemData.Enabled; var savedDebounce = ItemData.DebounceMs;
            try
            {
                if (Directory.Exists(work)) Util.DeleteDir(work);
                Directory.CreateDirectory(work);
                Check("fixture: everything of this test is under the scratch folder (never the real game)", Path.GetFullPath(work).StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase));
                ItemData.ResetForTest();
                ItemData.CacheFile = Path.Combine(work, "cache", "itemdata.bin");

                // ---- the WDC5 reader: every kind of field, the string block, id list, copy table, a locked section
                var t = Wdc5.Parse(FixedTable(out _));
                Check("wdc5: header read (table hash, layout hash, 7 fields, 12 byte records, not sparse)", t.TableHash == 0x11112222 && t.LayoutHash == 0x33334444 && t.Fields.Length == 7 && t.RecordSize == 12 && !t.Sparse && t.Schema.StartsWith("wowstatic"));
                Check("wdc5: 4 records + 1 copy = 5, ids from the id list; the locked section is skipped cleanly (1 section, 2 rows) and its ids are not there",
                    t.Count == 5 && Enumerable.Range(0, 5).Select(t.IdAt).SequenceEqual(new[] { 10, 11, 12, 13, 14 }) && t.EncryptedSections == 1 && t.EncryptedRecords == 2 && !t.TryIndex(90, out _), t.Count + " " + t.EncryptedSections);
                int i0, i1, i3, ic; t.TryIndex(10, out i0); t.TryIndex(11, out i1); t.TryIndex(13, out i3); t.TryIndex(14, out ic);
                Check("wdc5: plain 32-bit field", t.Field(i0, 0) == 30 && t.Field(i3, 0) == 39);
                Check("wdc5: bit-packed field (5 bits)", t.Field(i0, 1) == 1 && t.Field(i1, 1) == 2 && t.Field(i3, 1) == 4);
                Check("wdc5: signed bit-packed field (6 bits, -5 and -8)", t.Field(i0, 2) == -5 && t.Field(i3, 2) == -8, t.Field(i0, 2) + " " + t.Field(i3, 2));
                Check("wdc5: pallet field (index into 4 values)", t.Field(i0, 3) == 100 && t.Field(i1, 3) == 200 && t.Field(i3, 3) == 400);
                Check("wdc5: common data (default 7; 11 -> 99, 12 -> 98)", t.Field(i0, 4) == 7 && t.Field(i1, 4) == 99 && t.Field(t.TryIndex(12, out var i2) ? i2 : 0, 4) == 98);
                Check("wdc5: pallet array field (two values per entry)", t.Field(i0, 6, 0) == 20 && t.Field(i0, 6, 1) == 21 && t.Field(i3, 6, 0) == 10 && t.Field(i3, 6, 1) == 11, t.Field(i0, 6, 0) + "," + t.Field(i0, 6, 1));
                Check("wdc5: string field through the string block", t.String(i0, 5) == "alpha" && t.String(i1, 5) == "beta" && t.String(i3, 5) == "delta" && t.String(t.TryIndex(12, out i2) ? i2 : 0, 5) == "gamma", t.String(i0, 5) + " " + t.String(i1, 5));
                Check("wdc5: copy table row 14 reads like 11 (its own id for common data: default 7)", t.Field(ic, 0) == t.Field(i1, 0) && t.String(ic, 5) == "beta" && t.Field(ic, 4) == 7 && t.Field(ic, 1) == 2);
                var good = FixedTable(out _);
                var bad = new List<Exception>();
                foreach (var cut in new[] { 0, 10, 100, 203, 204, 300, good.Length - 80 })
                {
                    try { Wdc5.Parse(good.Take(cut).ToArray()); bad.Add(new Exception("no error at " + cut)); }
                    catch (Db2Exception) { }
                    catch (Exception e) { bad.Add(e); }
                }
                Check("wdc5: a file cut short or garbage raises Db2Exception, never anything else", bad.Count == 0, string.Join("; ", bad.Select(e => e.Message)));
                var notWdc = (byte[])good.Clone(); notWdc[0] = (byte)'X';
                bool threwMagic = false; try { Wdc5.Parse(notWdc); } catch (Db2Exception) { threwMagic = true; }
                Check("wdc5: a file that is not WDC5 is refused", threwMagic);

                // ---- ItemSparse in the layout of build 1.60.1.70338
                var sparse = Wdc5.Parse(SparseTable(0x6FCC3191));
                Check("sparse table: records through the offset map with strings inline (6 rows incl. the copy), the locked section skipped", sparse.Sparse && sparse.Count == 6 && sparse.EncryptedRecords == 1 && sparse.TryIndex(1004, out _) && !sparse.TryIndex(1099, out _), sparse.Count + "");
                var set = ItemData.ParseTables(SparseTable(0x6FCC3191), ItemTable(), BudgetTable());
                Check("item set: parsed without a problem, 6 shipped items, the locked row noted", set.Ok && set.Client.Count == 6 && set.Notes.Any(n => n.Contains("key-locked")), set.Problem + " / " + set.Client.Count);
                var sword = set.Client.First(i => i.Id == 1001);
                Check("fields from the layout table: name, description, quality 3, item level 20, requires 10, slot 13, binds when picked up, sell 12345, speed 2.6 s",
                    sword.Name == "Test Sword" && sword.Description == "It cuts." && sword.Quality == 3 && sword.ItemLevel == 20 && sword.RequiredLevel == 10 && sword.InventoryType == 13 && sword.Bonding == 1 && sword.SellPrice == 12345 && sword.DelayMs == 2600 && sword.Stackable == 1);
                Check("class, subclass and icon come from Item.db2 (2 / 7 / 135000)", sword.ClassId == 2 && sword.SubclassId == 7 && sword.IconId == 135000);
                Check("stat amounts = budget x share: rare one-hand at level 20 has 6 points: 50 % -> +3 Agility, 25 % -> 1.5 -> +2 Stamina", sword.Stats != null && sword.Stats.SequenceEqual(new[] { 3, 3, 7, 2 }), sword.Stats == null ? "null" : string.Join(",", sword.Stats));
                Check("copy table: 1004 is 1001 again", set.Client.First(i => i.Id == 1004).Name == "Test Sword");
                Check("an item without stats has none (empty, not unknown)", set.Client.First(i => i.Id == 1002).Stats != null && set.Client.First(i => i.Id == 1002).Stats.Length == 0);
                var blanket = set.Client.First(i => i.Id == 1005);
                Check("an item level the budget table does not have = amounts unknown (null), never guessed (blanket, level 12)", blanket.Stats == null);
                Check("bag size read (12 slot quiver)", set.Client.First(i => i.Id == 1006).ContainerSlots == 12);

                // unknown layouts: nothing is guessed
                var unk = ItemData.ParseTables(SparseTable(0xDEADBEEF), ItemTable(), BudgetTable());
                Check("unknown ItemSparse layout hash = unavailable: 'game updated', no items, no exception", !unk.Ok && unk.Problem.Contains("game updated") && unk.Problem.Contains("DEADBEEF") && unk.Client.Count == 0, unk.Problem);
                var garbage = ItemData.ParseTables(new byte[] { 1, 2, 3, 4, 5 }, null, null);
                var cutSparse = ItemData.ParseTables(SparseTable(0x6FCC3191).Take(500).ToArray(), null, null);
                Check("garbage or a cut file = unavailable with a reason, no exception", !garbage.Ok && !cutSparse.Ok && garbage.Problem.Length > 0 && cutSparse.Problem.Length > 0, garbage.Problem + " / " + cutSparse.Problem);
                var noBudget = ItemData.ParseTables(SparseTable(0x6FCC3191), ItemTable(), BudgetTable(0x0BADF00D));
                Check("an unknown budget layout only costs the stat amounts (null), the items are still there", noBudget.Ok && noBudget.Client.Count == 6 && noBudget.Client.First(i => i.Id == 1001).Stats == null && noBudget.Notes.Any(n => n.Contains("not known")));
                var noItem = ItemData.ParseTables(SparseTable(0x6FCC3191), null, BudgetTable());
                Check("without Item.db2 the classes are unknown (-1) but names and stats stay", noItem.Ok && noItem.Client.First(i => i.Id == 1001).ClassId == -1 && noItem.Client.First(i => i.Id == 1001).Stats != null);

                // ---- DBCache.bin
                var cacheFile = Path.Combine(work, "DBCache.bin");
                var entries = new List<DbEntry>
                {
                    new DbEntry { Table = ItemTables.ItemSparseHash, Id = 1001, Data = SparseRec("Test Sword (hotfix)", "", 3, 21, 13, 10, 1, 12345, 2600, 1, 0) },
                    new DbEntry { Table = ItemTables.ItemSparseHash, Id = 2001, Data = SparseRec("Hidden Axe", "Found by somebody.", 4, 40, 17, 30, 2, 5000, 3300, 1, 0, 4, 5000) },
                    new DbEntry { Table = ItemTables.ItemHash, Id = 2001, Data = ItemRow(2, 1, 17, 777) },
                    new DbEntry { Table = ItemTables.ItemSparseHash, Id = 3001, Status = 3, Data = SparseRec("Invalid Row", "", 1, 1, 0, 0, 0, 0, 0, 1, 0) },
                    new DbEntry { Table = 0x0A0B0C0D, Id = 5, Data = new byte[20] },
                };
                File.WriteAllBytes(cacheFile, DbCacheFile(9, 70338, entries, truncateLast: true));
                var rows = DbCache.Read(DbCache.ReadAllShared(cacheFile), ItemTables.ItemSparseHash);
                Check("dbcache: version 9, build 70338, only the valid ItemSparse rows (2), other tables and invalid rows left out", rows.Version == 9 && rows.Build == 70338 && rows.Rows.Keys.OrderBy(k => k).SequenceEqual(new[] { 1001, 2001 }), string.Join(",", rows.Rows.Keys));
                Check("dbcache: an entry that runs past the end of the file (the game is writing it) stops the read, the rows before it are kept", rows.Truncated && rows.Entries == 5);
                bool v8 = false, magic = false, tiny = false;
                try { DbCache.Read(DbCacheFile(8, 1, entries), ItemTables.ItemSparseHash); } catch (Db2Exception) { v8 = true; }
                try { DbCache.Read(new byte[100], ItemTables.ItemSparseHash); } catch (Db2Exception) { magic = true; }
                try { DbCache.Read(new byte[10], ItemTables.ItemSparseHash); } catch (Db2Exception) { tiny = true; }
                Check("dbcache: another version, a wrong header or a stub is refused with Db2Exception", v8 && magic && tiny);
                // the game rewrites the file while we read it: opened shared, so a writer holding it open does not stop us
                byte[] whileWriting;
                using (var writer = new FileStream(cacheFile, FileMode.Open, FileAccess.ReadWrite, FileShare.Read)) { whileWriting = DbCache.ReadAllShared(cacheFile); }
                Check("dbcache: readable while another process has it open for writing (FileShare.ReadWrite | Delete)", whileWriting.Length == new FileInfo(cacheFile).Length);

                set = SelfTestSet(cacheFile);
                var snap = new ItemSnapshot(set.Client, set.Cache);
                Check("merge: shipped 6 + cache 2 = 7 distinct items (1001 is in both: the cache wins), 2 revealed, 5 from the client", snap.Count == 7 && snap.Revealed == 2 && snap.ClientCount == 5, snap.Count + "/" + snap.Revealed + "/" + snap.ClientCount);
                Check("merge: 1001 now says 'hotfix' and comes from the cache; the copy 1004 stays the shipped one", snap.Get(1001).Name == "Test Sword (hotfix)" && snap.Get(1001).Source == "cache" && snap.Get(1001).Revealed && snap.Get(1004).Source == "client");
                var axe = snap.Get(2001);
                Check("a revealed item: from the cache, class / subclass / icon from the cache's Item row, stat amounts unknown (no budget for level 40)", axe != null && axe.Source == "cache" && axe.ClassId == 2 && axe.SubclassId == 1 && axe.IconId == 777 && axe.Stats == null && axe.RequiredLevel == 30);
                Check("an invalid cache row (status 3) is not an item", snap.Get(3001) == null);
                var noCache = ItemData.ParseTables(SparseTable(0x6FCC3191), ItemTable(), BudgetTable());
                ItemData.ReadCacheRows(noCache, Path.Combine(work, "nope", "DBCache.bin"));
                Check("no DBCache.bin = no revealed items, no error", noCache.Ok && noCache.Cache.Count == 0);
                var brokenCache = Path.Combine(work, "broken.bin"); File.WriteAllBytes(brokenCache, new byte[200]);
                var withBroken = ItemData.ParseTables(SparseTable(0x6FCC3191), ItemTable(), BudgetTable());
                ItemData.ReadCacheRows(withBroken, brokenCache);
                Check("a broken DBCache.bin costs only the revealed items", withBroken.Ok && withBroken.Cache.Count == 0 && withBroken.Notes.Any(n => n.StartsWith("DBCache.bin")));

                // ---- tooltip lines
                var lines = ItemText.Lines(snap.Get(1004)).Select(l => l.ToString()).ToList();
                Check("tooltip lines of the sword (the game's order): level, binding, slot + type, speed, stats, requirement, flavour text, price",
                    lines.SequenceEqual(new[] { "ilvl: Item Level 20", "bind: Binds when picked up", "slot: One-Hand | Sword", "speed: Speed 2.60", "stat: +3 Agility", "stat: +2 Stamina", "req: Requires Level 10", "desc: \"It cuts.\"", "price: Sell Price: | 12345" }), Join(lines));
                var axeLines = ItemText.Lines(axe).Select(l => l.ToString()).ToList();
                Check("tooltip lines of an item with unknown stat amounts: no stat lines at all", axeLines.All(l => !l.StartsWith("stat:")) && axeLines.Contains("req: Requires Level 30") && axeLines.Contains("bind: Binds when equipped"), Join(axeLines));
                var scrap = ItemText.Lines(snap.Get(1002)).Select(l => l.ToString()).ToList();
                Check("tooltip lines of trade goods: the class, the price, no level", scrap.SequenceEqual(new[] { "slot: Trade Goods", "price: Sell Price: | 13" }), Join(scrap));
                Check("a 12 slot quiver names its size", ItemText.Lines(snap.Get(1006)).Any(l => l.Kind == "bag" && l.Text == "12 Slot Quiver"));
                Check("coins: 12345 copper = 1g 23s 45c, 100 = 1s, 5 = 5c, 0 = 0c", string.Join(" ", ItemText.Coins(12345).Select(c => c.Key + "" + c.Value)) == "1g 23s 45c" && string.Join(" ", ItemText.Coins(100).Select(c => c.Key + "" + c.Value)) == "1s" && string.Join(" ", ItemText.Coins(5).Select(c => c.Key + "" + c.Value)) == "5c" && string.Join(" ", ItemText.Coins(0).Select(c => c.Key + "" + c.Value)) == "0c");
                Check("stat names: known numbers named, an unknown one is shown as such", ItemNames.Stat(3) == "Agility" && ItemNames.Stat(51) == "Fire Resistance" && ItemNames.Stat(45) == "Spell Power" && ItemNames.Stat(999).Contains("999"));

                // ---- search
                string Names(List<ClientItem> l) => string.Join(",", l.Select(i => i.Name));
                Check("search: 'sw' finds both swords (a prefix of a word), not the leftovers", Names(snap.Search("sw", 10)) == "Test Sword,Test Sword (hotfix)", Names(snap.Search("sw", 10)));
                var rankSnap = new ItemSnapshot(new[] { new ClientItem { Id = 1, Name = "Sword of Fire", Quality = 1 }, new ClientItem { Id = 2, Name = "Firesword", Quality = 5 }, new ClientItem { Id = 3, Name = "Fire Blade", Quality = 2 }, new ClientItem { Id = 4, Name = "Campfire", Quality = 6 } }, null);
                Check("search: starts-with first (best quality first), then a word that starts with it, then anywhere inside", Names(rankSnap.Search("fire", 10)) == "Firesword,Fire Blade,Sword of Fire,Campfire", Names(rankSnap.Search("fire", 10)));
                Check("search: names that START with the words come before names that only contain them ('lin' = Linen Scrap)", snap.Search("lin", 10).First().Name == "Linen Scrap");
                Check("search: all words must match, in any order ('hotfix sword')", Names(snap.Search("hotfix sword", 10)) == "Test Sword (hotfix)" && snap.Search("sword nothing", 10).Count == 0);
                Check("search: 'Deprecated ...' leftovers are never offered, 'Old Blanchy's Blanket' (a real item) is", snap.Search("deprecated", 10).Count == 0 && snap.Search("blanchy", 10).Count == 1 && ItemSnapshot.IsPlaceholder("OLD Sword") && ItemSnapshot.IsPlaceholder("Sword DEPRECATED") && !ItemSnapshot.IsPlaceholder("Old Blanchy's Blanket"));
                Check("search: an item id finds that item", snap.Search("2001", 10).FirstOrDefault()?.Name == "Hidden Axe");
                Check("search: the 'skip' filter hides what the caller already lists; the cap is kept; empty / blank = nothing", snap.Search("test", 10, i => i.Id == 1001).All(i => i.Id != 1001) && snap.Search("e", 2).Count == 2 && snap.Search("", 5).Count == 0 && snap.Search("   ", 5).Count == 0);
                var big = new ItemSnapshot(Enumerable.Range(1, 25000).Select(i => new ClientItem { Id = i, Name = "Item number " + i + " of the test set", Quality = i % 6 }), null);
                var swSearch = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < 20; i++) big.Search("number 12", 40);
                Check("search over 25,000 items stays fast (under 30 ms each)", swSearch.ElapsedMilliseconds / 20.0 < 30, (swSearch.ElapsedMilliseconds / 20.0).ToString("0.0", CultureInfo.InvariantCulture) + " ms");

                // ---- the saved index
                var idxPath = Path.Combine(work, "idx", "itemdata.bin");
                Check("index file: written", ItemIndexFile.TryWrite(idxPath, "wow_classic_beta|1.60.1.70338|abc", 4096, 12345, set) && File.Exists(idxPath) && !File.Exists(idxPath + ".tmp"));
                var back = ItemIndexFile.TryRead(idxPath);
                Check("index file round trip: build, DBCache size and time, layout hashes, budgets, all items with every field (null / empty / filled stats, descriptions, sources)",
                    back != null && back.Build == "wow_classic_beta|1.60.1.70338|abc" && back.DbSize == 4096 && back.DbTicks == 12345 && back.Set.SparseHash == 0x6FCC3191 && back.Set.ItemHash == 0x9A2A4834 && back.Set.Budgets != null && back.Set.Budgets.ByLevel.Count == 2
                    && back.Set.Client.Count == set.Client.Count && back.Set.Cache.Count == set.Cache.Count
                    && back.Set.Client.Zip(set.Client, (a, b) => a.Id == b.Id && a.Name == b.Name && a.Description == b.Description && a.Quality == b.Quality && a.ItemLevel == b.ItemLevel && a.RequiredLevel == b.RequiredLevel && a.InventoryType == b.InventoryType
                        && a.ClassId == b.ClassId && a.SubclassId == b.SubclassId && a.IconId == b.IconId && a.Bonding == b.Bonding && a.Stackable == b.Stackable && a.DelayMs == b.DelayMs && a.ContainerSlots == b.ContainerSlots && a.SellPrice == b.SellPrice && a.BuyPrice == b.BuyPrice
                        && (a.Stats == null) == (b.Stats == null) && (a.Stats == null || a.Stats.SequenceEqual(b.Stats))).All(x => x)
                    && back.Set.Cache.First(i => i.Id == 2001).Stats == null && back.Set.Cache.All(i => i.Source == "cache") && back.Set.Client.All(i => i.Source == "client"));
                var fileSize = new FileInfo(idxPath).Length;
                Check("index file is compact (zipped, a few hundred bytes for 8 items)", fileSize < 4000, fileSize + " bytes");
                var corrupt = Path.Combine(work, "idx", "corrupt.bin");
                var raw = File.ReadAllBytes(idxPath);
                File.WriteAllBytes(corrupt, raw.Take(raw.Length / 2).ToArray());
                var junk = Path.Combine(work, "idx", "junk.bin"); File.WriteAllText(junk, "this is not an index");
                Check("index file: a cut, a foreign or a missing file reads as null (the game files are read instead), no exception", ItemIndexFile.TryRead(corrupt) == null && ItemIndexFile.TryRead(junk) == null && ItemIndexFile.TryRead(Path.Combine(work, "idx", "missing.bin")) == null);

                // the decision: saved index vs game files vs only the hotfix cache
                int gameReads = 0;
                Func<ItemSet> game = () => { gameReads++; return ItemData.ParseTables(SparseTable(0x6FCC3191), ItemTable(), BudgetTable()); };
                var obtPath = Path.Combine(work, "obt", "itemdata.bin");
                var dbInfo = new FileInfo(cacheFile);
                var s1 = ItemData.Obtain(ItemIndexFile.TryRead(obtPath), "B1", dbInfo.Length, dbInfo.LastWriteTimeUtc.Ticks, cacheFile, obtPath, game, out var from1);
                Check("first start (no saved index): the game files are read once, 7 items, the index is saved", gameReads == 1 && from1 == "game files" && s1.Ok && new ItemSnapshot(s1.Client, s1.Cache).Count == 7 && File.Exists(obtPath), from1);
                var s2 = ItemData.Obtain(ItemIndexFile.TryRead(obtPath), "B1", dbInfo.Length, dbInfo.LastWriteTimeUtc.Ticks, cacheFile, obtPath, game, out var from2);
                Check("second start, same build and same DBCache.bin: from the saved index, the game files are NOT read", gameReads == 1 && from2 == "cache file" && new ItemSnapshot(s2.Client, s2.Cache).Count == 7 && new ItemSnapshot(s2.Client, s2.Cache).Revealed == 2, from2 + " " + gameReads);
                // the game learns a new item: DBCache.bin grows
                entries.Add(new DbEntry { Table = ItemTables.ItemSparseHash, Id = 2002, Data = SparseRec("Another Hidden Thing", "", 3, 20, 13, 0, 0, 1, 0, 1, 0) });
                File.WriteAllBytes(cacheFile, DbCacheFile(9, 70338, entries));
                dbInfo = new FileInfo(cacheFile);
                var s3 = ItemData.Obtain(ItemIndexFile.TryRead(obtPath), "B1", dbInfo.Length, dbInfo.LastWriteTimeUtc.Ticks, cacheFile, obtPath, game, out var from3);
                var snap3 = new ItemSnapshot(s3.Client, s3.Cache);
                Check("DBCache.bin changed: only the hotfix cache is read again (no game files), the new item is there, 3 revealed", gameReads == 1 && from3 == "cache file + DBCache.bin" && snap3.Get(2002) != null && snap3.Revealed == 3 && snap3.Count == 8, from3 + " " + gameReads + " " + snap3.Count);
                var s4 = ItemData.Obtain(ItemIndexFile.TryRead(obtPath), "B1", dbInfo.Length, dbInfo.LastWriteTimeUtc.Ticks, cacheFile, obtPath, game, out var from4);
                Check("the new signature was saved: the next start is instant again", gameReads == 1 && from4 == "cache file" && new ItemSnapshot(s4.Client, s4.Cache).Count == 8, from4);
                var s5 = ItemData.Obtain(ItemIndexFile.TryRead(obtPath), "B2", dbInfo.Length, dbInfo.LastWriteTimeUtc.Ticks, cacheFile, obtPath, game, out var from5);
                Check("the game was patched (other build key): the index is not trusted, the game files are read again", gameReads == 2 && from5 == "game files", from5 + " " + gameReads);
                File.WriteAllBytes(obtPath, new byte[] { 1, 2, 3 });
                var s6 = ItemData.Obtain(ItemIndexFile.TryRead(obtPath), "B2", dbInfo.Length, dbInfo.LastWriteTimeUtc.Ticks, cacheFile, obtPath, game, out var from6);
                Check("a damaged index file: the game files are read again and the file is replaced", gameReads == 3 && from6 == "game files" && ItemIndexFile.TryRead(obtPath) != null);
                int badReads = 0;
                var s7 = ItemData.Obtain(null, "B3", 0, 0, null, Path.Combine(work, "obt", "unk.bin"), () => { badReads++; return ItemData.ParseTables(SparseTable(0xDEADBEEF), null, null); }, out var from7);
                Check("a game that was updated (unknown layout): unavailable, nothing saved", !s7.Ok && !File.Exists(Path.Combine(work, "obt", "unk.bin")) && badReads == 1);

                // the refresh is debounced: three writes in a row = one refresh
                ItemData.WowRoot = () => null; ItemData.Enabled = () => true;
                ItemData.DebounceMs = 200;
                var watchDir = Path.Combine(work, "watch", "_classic_beta_", "Cache", "ADB", "enUS");
                Directory.CreateDirectory(watchDir);
                var wf = Path.Combine(watchDir, "DBCache.bin");
                File.WriteAllBytes(wf, DbCacheFile(9, 1, new DbEntry[0]));
                ItemData.WatchForTest(wf);
                int before = ItemData.StartCountForTest;
                for (int i = 0; i < 3; i++) { File.AppendAllText(wf, "x"); await Task.Delay(40); }
                await Task.Delay(1200);
                Check("DBCache.bin written three times in a row = one refresh after the quiet moment", ItemData.StartCountForTest - before == 1, (ItemData.StartCountForTest - before) + " refreshes");
                ItemData.StopWatching();
                File.AppendAllText(wf, "y");
                await Task.Delay(500);
                Check("after StopWatching nothing is refreshed any more", ItemData.StartCountForTest - before == 1);
                ItemData.ResetForTest();
                ItemData.DebounceMs = savedDebounce;
                ItemData.CacheFile = Path.Combine(work, "cache", "itemdata.bin");

                // ---- states and the Settings line
                ItemData.SetForTest(null, ItemData.State.NotStarted);
                Check("status line: not read yet", ItemData.StatusLine == "Item data: not read yet");
                ItemData.SetForTest(snap, ItemData.State.Ready);
                Check("status line: 'Item data: read from your WoW installation (7 items, 2 revealed)'", ItemData.StatusLine == "Item data: read from your WoW installation (7 items, 2 revealed)", ItemData.StatusLine);
                ItemData.SetForTest(null, ItemData.State.Unavailable, "game updated: item table layout DEADBEEF is not known to this Outpost yet");
                Check("status line: unavailable names the reason; no snapshot, Get() is null, Ready is false", ItemData.StatusLine.StartsWith("Item data: unavailable (game updated") && ItemData.Get(1001) == null && !ItemData.Ready && ItemData.Search("sword", 5).Count == 0);
                ItemData.Enabled = () => false;
                ItemData.Start();
                Check("switched off: Start() does not read anything and says 'off'; the index is dropped", ItemData.Status == ItemData.State.Disabled && ItemData.StatusLine == "Item data: off" && ItemData.Snapshot == null);
                ItemData.Enabled = () => true;
                ItemData.ResetForTest();

                // ---- the Characters page
                await PageChecks(dir, work, snap, Check, P);

                // ---- optional: the real game files, read-only
                SmokeReal(Check, notes);
                CascSharedStress(work, Check, notes);
            }
            catch (Exception ex) { ok = false; notes.Add("FAIL exception: " + ex); }
            finally
            {
                ItemData.StopWatching();
                ItemData.ResetForTest();
                ItemData.CacheFile = savedCacheFile; ItemData.WowRoot = savedRoot; ItemData.Enabled = savedEnabled; ItemData.DebounceMs = savedDebounce;
                HubData.ResetCache();
                IconStore.ForgetMemory();
                InventoryView.ResetViewForTest();
                InventoryView.Init(() => settings.WowRoot);
                ShowTab("addons");
            }
            notes.Insert(0, "itemdata (2.48.0): " + (ok ? "all ok" : "FAILURES"));
            return string.Join("\r\n", notes);
        }

        // the Characters page with a snapshot of the game data: tooltips, search, gear level
        async Task PageChecks(string dir, string work, ItemSnapshot snapBase, CheckFn Check, Func<string, string> P)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var root = Path.Combine(work, "pagewow");
            var sv = Path.Combine(root, "_classic_beta_", "WTF", "Account", "ITEMS", "SavedVariables");
            Directory.CreateDirectory(sv);
            Directory.CreateDirectory(Path.Combine(root, "_classic_beta_", "Interface", "AddOns"));
            File.WriteAllText(Path.Combine(sv, "ElansBags.lua"), BagsLua(3), new UTF8Encoding(false));
            // the Hub's gear without item levels and without the average: the game data fills them in
            var hubLua = HubCharsLua(now, now - 60).Replace(",il=62", "").Replace(",il=55", "").Replace(",il=10", "").Replace(",il=70", "").Replace("ilvl=58.5,", "");
            File.WriteAllText(Path.Combine(sv, "ElansHub.lua"), hubLua, new UTF8Encoding(false));
            HubData.ResetCache();

            // the game data for some of the fixture's items (names there are the fixture's; the game data has its own words)
            ClientItem Mk(int id, string name, int q, int ilvl, int inv, int cls, int sub, int req, int bond, long sell, string desc, params int[] stats) =>
                new ClientItem { Id = id, Name = name, Quality = q, ItemLevel = ilvl, InventoryType = inv, ClassId = cls, SubclassId = sub, RequiredLevel = req, Bonding = bond, SellPrice = sell, Description = desc, Stats = stats };
            var client = new List<ClientItem>
            {
                Mk(102, "Iron Sword", 3, 28, 13, 2, 7, 20, 2, 4025, "", 3, 4, 7, 6),
                Mk(109, "White Cloak", 1, 10, 16, 4, 1, 1, 0, 33, ""),
                Mk(200, "Helm of Testing", 3, 62, 1, 4, 2, 55, 1, 98000, "", 7, 11),
                Mk(201, "Plain Chestguard", 2, 55, 5, 4, 3, 50, 0, 5000, ""),
                Mk(202, "Epic Blade", 4, 70, 17, 2, 8, 60, 1, 250000, "Forged in the fires.", 4, 20, 7, 14),
                Mk(100, "Linen Cloth", 1, 5, 0, 7, 5, 0, 0, 13, ""),
                Mk(5001, "Windseeker Greatsword", 4, 66, 17, 2, 8, 58, 1, 204500, "A blade from the game files.", 4, 18, 3, 12),
                Mk(5002, "Swordbreaker's Bulwark", 3, 40, 14, 4, 6, 35, 2, 7777, ""),
            };
            var snap = new ItemSnapshot(client, new[] { new ClientItem { Id = 5003, Name = "Revealed Sword of Yesterday", Quality = 3, ItemLevel = 33, InventoryType = 13, ClassId = 2, SubclassId = 7, RequiredLevel = 25, Source = "cache", SellPrice = 910 } });
            ItemData.SetForTest(snap, ItemData.State.Ready);
            InventoryView.Init(() => root);
            TabInventory.IsChecked = true;
            for (int i = 0; i < 40 && InventoryView.CharCountForTest != 6; i++) await Task.Delay(200);
            Check("page: the characters are listed (6) with the game data loaded", InventoryView.CharCountForTest == 6, InventoryView.CharCountForTest.ToString());

            // tooltip of a bag slot: name, facts from the game data, then the counts
            InventoryView.FilterForTest("Forever-Elan");
            InventoryView.SelectTabForTest("bags");
            var tip = InventoryView.SlotTipLinesForTest(102, "Forever-Elan");
            Check("slot tooltip: name, Item Level, slot + type, binding, stats (+4 Agility, +6 Stamina), requirement, sell price, then the character counts and the item id",
                tip.Take(11).SequenceEqual(new[] { "Iron Sword", "Item Level 28", "Binds when equipped", "One-Hand", "Sword", "+4 Agility", "+6 Stamina", "Requires Level 20", "Sell Price:", "40", "25" }), Join(tip));
            Check("slot tooltip: the counts and the item id follow the facts (the existing lines are all still there)", tip.Contains("Elan") && tip.Last() == "Item ID 102" && tip.Any(l => l.StartsWith("Bags ") || l.Contains("Backpack")), Join(tip));
            var unknown = InventoryView.SlotTipLinesForTest(101, "Forever-Elan");
            Check("slot tooltip of an item the game data does not know (still hidden): 'Not revealed yet on WoW Forever' + what the link told (name, id, counts)",
                unknown[0] == "Minor Healing Potion" && unknown[1] == "Not revealed yet on WoW Forever" && unknown.Last() == "Item ID 101" && unknown.Contains("Elan"), Join(unknown));

            // gear: the item level when the Hub did not save one
            InventoryView.SelectTabForTest("gear");
            var gl = InventoryView.CharTabLinesForTest();
            Check("gear tab: worn item levels from the game data when the Hub has none (Head 62, Chest 55, Back 10, Main hand 70), average 49.3 shown", gl.Contains("slot|1|Head|Helm of Testing|62") && gl.Contains("slot|5|Chest|Plain Chestguard|55") && gl.Contains("slot|16|Main hand|Epic Blade|70") && gl.Any(x => x.StartsWith("gearinfo|49.")) && InventoryView.ViewInfoForTest.StartsWith("Average item level 49."), Join(gl.Where(x => x.StartsWith("slot|") || x.StartsWith("gearinfo"))));
            var gearTip = InventoryView.DollTipForTest(1);
            Check("gear slot tooltip: name, 'Head · Item level 62' from the game data, then Item Level, Head + Leather, binding, +11 Stamina, requirement, price",
                gearTip.Count > 8 && gearTip[0] == "Helm of Testing" && gearTip.Contains("Head · Item level 62") && gearTip.Contains("Item Level 62") && gearTip.Contains("Leather") && gearTip.Contains("+11 Stamina") && gearTip.Contains("Requires Level 55"), Join(gearTip));
            ItemData.SetForTest(null, ItemData.State.Unavailable, "off for the test");
            InventoryView.SelectTabForTest("overview"); InventoryView.SelectTabForTest("gear");
            gl = InventoryView.CharTabLinesForTest();
            Check("gear tab without game data: as before (no item levels, no average)", gl.Contains("slot|1|Head|Helm of Testing|0") && gl.Contains("gearinfo|-|4"), Join(gl.Where(x => x.StartsWith("slot|1|") || x.StartsWith("gearinfo"))));
            var noFacts = InventoryView.SlotTipLinesForTest(101, "Forever-Elan");
            Check("without game data the tooltips are exactly what they were (no 'not revealed' line)", noFacts[0] == "Minor Healing Potion" && !noFacts.Contains("Not revealed yet on WoW Forever") && noFacts.Count == unknown.Count - 1, Join(noFacts));
            ItemData.SetForTest(snap, ItemData.State.Ready);

            // search: what you own, plus items from the game data that you do not
            InventoryView.FilterForTest(null);
            InventoryView.SetSearchForTest("sword");
            var res = InventoryView.ResultTextsForTest();
            Check("search 'sword': your Iron Sword first, then the game data's items you do not own, marked 'Not on your characters': names starting with it, then words, then the rest (Swordbreaker's Bulwark, Revealed Sword of Yesterday, Windseeker Greatsword)",
                res.Count == 4 && res[0].StartsWith("Iron Sword ×") && res.Skip(1).All(r => r.Contains("[Not on your characters ]")) && res[1].StartsWith("Swordbreaker's Bulwark ") && res[2].StartsWith("Revealed Sword of Yesterday ") && res[3].StartsWith("Windseeker Greatsword "), Join(res));
            Check("search: the game data does not list again what you own (Iron Sword once)", res.Count(r => r.StartsWith("Iron Sword")) == 1);
            var tipIdx = res.FindIndex(r => r.StartsWith("Swordbreaker"));
            var rt = InventoryView.ResultTipForTest(tipIdx);
            Check("search result tooltip of an item you do not own: facts from the game data, 'Not on your characters', the id", rt.SequenceEqual(new[] { "Swordbreaker's Bulwark", "Item Level 40", "Binds when equipped", "Off Hand", "Shield", "Requires Level 35", "Sell Price:", "77", "77", "Not on your characters", "Item ID 5002" }), Join(rt));
            var ownedIdx = res.FindIndex(r => r.StartsWith("Iron Sword"));
            var ot = InventoryView.ResultTipForTest(ownedIdx);
            Check("search result tooltip of an item you own: the same facts, then who has how many", ot[0] == "Iron Sword" && ot.Contains("Item Level 28") && ot.Any(l => l == "Elan" || l.StartsWith("Elan")) && !ot.Contains("Not on your characters"), Join(ot));
            InventoryView.SetSearchForTest("windseeker");
            Check("search 'windseeker' (nothing owned) lists the game data's item, and the caption says so", InventoryView.ResultTextsForTest().Count == 1 && InventoryView.ResultTextsForTest()[0].StartsWith("Windseeker Greatsword ") && InventoryView.ResultCaptionForTest.Contains("0 matches") && InventoryView.ResultCaptionForTest.Contains("plus 1 from the game data"), InventoryView.ResultCaptionForTest);
            InventoryView.SetSearchForTest("s");
            Check("a one-letter search does not pull in the game data's items", InventoryView.ResultTextsForTest().All(r => !r.Contains("Not on your characters")));
            InventoryView.SetSearchForTest("zzzzqq");
            Check("a search that matches nothing anywhere says so", InventoryView.ResultCountForTest == 0 && InventoryView.NoMatchVisibleForTest);
            InventoryView.SetSearchForTest("windseeker");
            ItemData.SetForTest(null, ItemData.State.Unavailable, "off for the test");
            InventoryView.SetSearchForTest("windseekerx"); InventoryView.SetSearchForTest("windseeker");
            Check("without game data the search only knows what you own (as before)", InventoryView.ResultCountForTest == 0 && InventoryView.NoMatchVisibleForTest);
            ItemData.SetForTest(snap, ItemData.State.Ready);

            // pictures
            InventoryView.SetSearchForTest("sword");
            await ShotWait(300);
            Snapshot(P("22-itemdata-search.png"));
            SnapshotSheet(InventoryView.ResultTipSheetForTest(tipIdx), P("22-itemdata-tooltip-not-owned.png"));
            SnapshotSheet(InventoryView.ResultTipSheetForTest(ownedIdx), P("22-itemdata-tooltip-owned.png"));
            InventoryView.SetSearchForTest("");
            SnapshotSheet(InventoryView.SlotTipSheetForTest(101, "Forever-Elan"), P("22-itemdata-tooltip-not-revealed.png"));
            SnapshotSheet(InventoryView.SlotTipSheetForTest(200, "Forever-Elan"), P("22-itemdata-tooltip-gear.png"));
            InventoryView.FilterForTest("Forever-Elan");
            InventoryView.SelectTabForTest("gear");
            await ShotWait(300);
            Snapshot(P("22-itemdata-gear.png"));
            Check("the Outpost wrote nothing into the WoW folder (only the two test files are left)", Directory.GetFiles(root, "*", SearchOption.AllDirectories).Select(f => Path.GetFileName(f)).OrderBy(x => x).SequenceEqual(new[] { "ElansBags.lua", "ElansHub.lua" }));
        }

        // read-only look at the real game files, only when there is a WoW install on this PC
        void SmokeReal(CheckFn Check, List<string> notes)
        {
            var real = Environment.GetEnvironmentVariable("ELANSHUB_WOW");
            if (string.IsNullOrEmpty(real)) real = WowLocator.Find();
            if (string.IsNullOrEmpty(real) || !Directory.Exists(Path.Combine(real, "Data", "data")) || !Directory.Exists(Path.Combine(real, "_classic_beta_")))
            {
                notes.Add("skip real game files: no WoW install with the Forever beta client on this PC");
                return;
            }
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                long m0 = GC.GetTotalMemory(true);
                var raw = ItemData.ReadCasc(real, "_classic_beta_");
                long readMs = sw.ElapsedMilliseconds;
                var set = ItemData.ParseTables(raw.Sparse, raw.Item, raw.Budget);
                long parseMs = sw.ElapsedMilliseconds - readMs;
                if (!set.Ok)
                {
                    notes.Add("skip real game files: " + set.Problem + " (this Outpost does not know that game build's item table; nothing was guessed)");
                    return;
                }
                var adb = ItemData.AdbFile(real, "_classic_beta_");
                ItemData.ReadCacheRows(set, adb);
                var snap = new ItemSnapshot(set.Client, set.Cache);
                raw = null;
                long mem = GC.GetTotalMemory(true) - m0;
                long total = sw.ElapsedMilliseconds;
                Check("real game files: more than 19,000 items read", snap.Count > 19000, snap.Count.ToString());
                var arrow = snap.Get(2512);
                Check("real game files: item 2512 is Rough Arrow (ammo, common)", arrow != null && arrow.Name == "Rough Arrow" && arrow.Quality == 1, arrow?.Name);
                var tf = snap.Get(19019);
                Check("real game files: item 19019 is Thunderfury (epic, item level 80, requires 60, +5 Agility +8 Stamina)", tf != null && tf.Name.StartsWith("Thunderfury, Blessed Blade") && tf.Quality == 5 && tf.ItemLevel == 80 && tf.RequiredLevel == 60 && tf.Stats != null && tf.Stats.Take(4).SequenceEqual(new[] { 3, 5, 7, 8 }), tf == null ? "null" : tf.Name + " " + (tf.Stats == null ? "null" : string.Join(",", tf.Stats)));
                var hs = snap.Get(6948);
                Check("real game files: item 6948 is Hearthstone", hs != null && hs.Name == "Hearthstone");
                if (adb != null) notes.Add("note real game files: " + snap.Revealed + " revealed items from " + adb);
                notes.Add("note real game files: " + snap.Count + " items (" + snap.Revealed + " revealed), read " + readMs + " ms + parse " + parseMs + " ms + cache " + (total - readMs - parseMs) + " ms = " + total + " ms, about " + (mem / 1024.0 / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB in memory");
                if (set.Notes.Count > 0) notes.Add("note real game files: " + string.Join("; ", set.Notes));
            }
            catch (Exception e)
            {
                notes.Add("skip real game files: " + e.Message);
            }
        }

        // The shared game storage (CascShared) under many readers at once, against the real game files: leases, reads, idle closes, a folder that is no
        // game, a game patch (another build key) in the middle, and the real spell and item readers alongside. No exception may reach a reader and every
        // read returns data. 2.70.0 failed now and then (a NullReferenceException in SpellData.ReadCasc): an open of another folder that failed left the
        // closed storage as the current one, and the next reader of the game folder got a lease without a storage.
        void CascSharedStress(string work, CheckFn Check, List<string> notes)
        {
            var real = Environment.GetEnvironmentVariable("ELANSHUB_WOW");
            if (string.IsNullOrEmpty(real)) real = WowLocator.Find();
            if (string.IsNullOrEmpty(real) || !Directory.Exists(Path.Combine(real, "Data", "data")) || !Directory.Exists(Path.Combine(real, "_classic_beta_")))
            {
                notes.Add("skip shared storage stress: no WoW install with the Forever beta client on this PC");
                return;
            }
            const string F = "_classic_beta_";
            var bogus = Path.Combine(work, "not-a-game");
            Directory.CreateDirectory(bogus);
            File.WriteAllText(Path.Combine(bogus, ".build.info"), "Branch!STRING:0|Active!DEC:1|Build Key!HEX:16|Version!STRING:0|Product!STRING:0\r\nus|1|00000000000000000000000000000000|1.0.0.1|wow_classic_beta\r\n");
            var probe = SpellTables.SpellRange.FileId;
            bool ReadOne(CascStorage c)
            {
                var k = c.ResolveFileIds(new HashSet<int> { probe });
                if (!k.TryGetValue(probe, out var ck)) return false;
                var b = c.ReadByContentKey(ck);
                return b != null && b.Length > 4 && b[0] == 'W' && b[1] == 'D' && b[2] == 'C';
            }
            int savedIdle = CascShared.IdleMs; var savedBuild = ItemData.BuildForTest;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int opens0 = CascShared.OpensForTest;
            try
            {
                CascShared.CloseIdle();
                // the 2.70.0 failure, step by step
                using (var l = CascShared.Open(real, F)) { }
                bool threw = false;
                try { using (CascShared.Open(bogus, F)) { } } catch (Exception) { threw = true; }
                string after;
                try { using (var l = CascShared.Open(real, F)) after = l.Casc == null ? "lease without a storage" : ReadOne(l.Casc) ? "ok" : "no data"; }
                catch (Exception e) { after = e.GetType().Name + ": " + e.Message; }
                Check("cascshared: an open of a folder that is no game fails, and the next reader of the game folder still gets an open storage (2.70.0: a lease without one, NullReferenceException)", threw && after == "ok", threw + " " + after);
                using (var l = CascShared.Open(real, F))
                {
                    bool refused = !CascShared.CloseIdle();
                    Check("cascshared: closing it is refused while a reader holds it, and that reader keeps reading", refused && CascShared.IsOpenForTest && ReadOne(l.Casc));
                }
                bool disposedThrows = false;
                var gone = CascShared.Open(real, F); gone.Dispose();
                try { var x = gone.Casc; } catch (ObjectDisposedException) { disposedThrows = true; }
                Check("cascshared: a lease given back says so (ObjectDisposedException), it never hands out a null storage", disposedThrows);

                // many at once
                CascShared.IdleMs = 40;
                var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
                int reads = 0, empty = 0, bogusOpened = 0;
                var realBuild = ItemData.GameBuild(real, F);
                var threads = new List<System.Threading.Thread>();
                for (int ti = 0; ti < 8; ti++)
                {
                    int seed = ti;
                    threads.Add(new System.Threading.Thread(() =>
                    {
                        var rnd = new Random(seed * 7919 + 1);
                        for (int i = 0; i < 30; i++)
                        {
                            try
                            {
                                if (seed == 0 && i % 10 == 5) { try { using (CascShared.Open(bogus, F)) System.Threading.Interlocked.Increment(ref bogusOpened); } catch (IOException) { } }
                                else if (seed == 1 && i % 7 == 3) CascShared.CloseIdle();
                                else if (seed == 2 && i == 15) { ItemData.BuildForTest = (r, f) => r == real ? realBuild + "-patched" : null; System.Threading.Thread.Sleep(80); ItemData.BuildForTest = savedBuild; }
                                else using (var l = CascShared.Open(real, F)) { if (ReadOne(l.Casc)) System.Threading.Interlocked.Increment(ref reads); else System.Threading.Interlocked.Increment(ref empty); }
                                if (rnd.Next(5) == 0) System.Threading.Thread.Sleep(rnd.Next(70));
                            }
                            catch (Exception e) { errors.Enqueue(e.GetType().Name + ": " + e.Message); }
                        }
                    }) { IsBackground = true });
                }
                int spellTables = -1; bool itemTables = false;
                threads.Add(new System.Threading.Thread(() => { try { spellTables = SpellData.ReadCasc(real, F).Tables.Count(t => t.Value != null); } catch (Exception e) { errors.Enqueue("SpellData.ReadCasc " + e); } }) { IsBackground = true });
                threads.Add(new System.Threading.Thread(() => { try { var r = ItemData.ReadCasc(real, F); itemTables = r.Sparse != null && r.Item != null; } catch (Exception e) { errors.Enqueue("ItemData.ReadCasc " + e); } }) { IsBackground = true });
                foreach (var t in threads) t.Start();
                bool done = threads.All(t => t.Join(120000));
                ItemData.BuildForTest = savedBuild;
                int allTables = SpellTables.All.Length + WorldTables.All.Length;
                Check("cascshared stress: 8 readers x 30 leases + idle closes (40 ms) + a folder that is no game + a game patch in the middle + the spell and item readers: no exception, every read returned data",
                    done && errors.IsEmpty && empty == 0 && reads > 150 && bogusOpened == 0 && spellTables == allTables && itemTables,
                    "done " + done + ", reads " + reads + ", empty " + empty + ", spell tables " + spellTables + "/" + allTables + ", items " + itemTables + ", errors: " + string.Join(" | ", errors.Take(3)));
                CascShared.IdleMs = savedIdle;
                bool closed = CascShared.CloseIdle();
                Check("cascshared: afterwards no lease is left and it closes", closed && !CascShared.IsOpenForTest && CascShared.LeasesForTest == 0);
                notes.Add("note cascshared stress: " + reads + " reads, " + (CascShared.OpensForTest - opens0) + " storage opens, " + sw.ElapsedMilliseconds + " ms");
            }
            catch (Exception e) { Check("cascshared: no exception", false, e.ToString()); }
            finally { CascShared.IdleMs = savedIdle; ItemData.BuildForTest = savedBuild; CascShared.CloseIdle(); }
        }
    }
}
