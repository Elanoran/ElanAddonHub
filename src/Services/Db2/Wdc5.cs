using System;
using System.Collections.Generic;
using System.Text;

namespace ElansAddonHub.Services.Db2
{
    public sealed class Db2Exception : Exception
    {
        public Db2Exception(string message) : base(message) { }
    }

    // A minimal, read-only reader for Blizzard's WDC5 table files (.db2), written from the public format description on wowdev.wiki.
    // Only what the item tables need: header, section headers, field storage info (plain, bit-packed, pallet, pallet array, common data),
    // the id list, the copy table, the offset map of "sparse" tables (records with their strings inline) and the string block.
    // Sections that are encrypted with a key (tact key hash != 0) are skipped cleanly: their records are simply not there.
    // It works on the bytes of the whole file and never touches the disk.
    //
    // Layout (all little endian):
    //   header (204 bytes): "WDC5", version, schema[128], record_count, field_count, record_size, string_table_size, table_hash, layout_hash,
    //                       min_id, max_id, locale, flags(u16), id_index(u16), total_field_count, bitpacked_data_offset, lookup_column_count,
    //                       field_storage_info_size, common_data_size, pallet_data_size, section_count
    //   section headers (40 bytes each): tact_key_hash(u64), file_offset, record_count, string_table_size, offset_records_end, id_list_size,
    //                       relationship_data_size, offset_map_id_count, copy_table_count
    //   field_structure[field_count] (4 bytes each, not needed), field_storage_info (24 bytes each), pallet data, common data,
    //   then every section: records, string block, id list, copy table (new id, source id), offset map (offset u32, size u16), ...
    //   A "sparse" table (flags & 1) has no fixed record size: its records are found through the offset map and hold their strings inline.
    public sealed class Wdc5
    {
        public const uint Magic = 0x35434457;   // "WDC5"

        public struct FieldInfo
        {
            public int OffsetBits, SizeBits, AdditionalSize, StorageType;
            public uint C0, C1, C2;   // compression data: bit-packed/pallet = (offset bits, size bits, flags or array count); common = (default value)
        }

        public const int StorageNone = 0, StorageBitpacked = 1, StorageCommon = 2, StoragePallet = 3, StoragePalletArray = 4, StorageBitpackedSigned = 5;

        public string Schema { get; private set; }
        public uint TableHash { get; private set; }
        public uint LayoutHash { get; private set; }
        public int RecordSize { get; private set; }
        public int FieldCount { get; private set; }
        public ushort Flags { get; private set; }
        public int IdIndex { get; private set; }
        public int MinId { get; private set; }
        public int MaxId { get; private set; }
        public bool Sparse => (Flags & 1) != 0;
        public FieldInfo[] Fields { get; private set; }
        // sections that could not be read because they are locked with a key, and the records they hold
        public int EncryptedSections { get; private set; }
        public int EncryptedRecords { get; private set; }

        struct Rec { public int Id; public int Pos; public int Size; public int Sec; }
        // per section (in file order, locked ones too): file offset of the records, record count, string block size, 1 = locked
        readonly List<int[]> secs = new List<int[]>();

        byte[] d;
        Rec[] recs = new Rec[0];
        int count;
        readonly Dictionary<int, int> byId = new Dictionary<int, int>();
        uint[][] pallet;                       // per field (pallet types)
        Dictionary<int, uint>[] common;        // per field (common data)

        // the foreign key stored in the relationship section ("$noninline,relation$" columns, e.g. UiMapArtTile.UiMapArtID); 0 = none
        readonly Dictionary<int, int> parents = new Dictionary<int, int>();
        public int ParentOf(int index) => parents.TryGetValue(index, out int p) ? p : 0;

        public int Count => count;
        public int IdAt(int index) => recs[index].Id;
        public bool TryIndex(int id, out int index) => byId.TryGetValue(id, out index);

        // the bytes of record `index` of a sparse table (strings inline, then the fixed fields)
        public ArraySegment<byte> Raw(int index) => new ArraySegment<byte>(d, recs[index].Pos, recs[index].Size);

        static uint U32(byte[] b, int p) => BitConverter.ToUInt32(b, p);
        static int I32(byte[] b, int p) => BitConverter.ToInt32(b, p);

        public static Wdc5 Parse(byte[] data)
        {
            if (data == null || data.Length < 204) throw new Db2Exception("the table is too short to be a WDC5 file");
            if (U32(data, 0) != Magic) throw new Db2Exception("not a WDC5 table (magic " + Encoding.ASCII.GetString(data, 0, 4) + ")");
            var w = new Wdc5 { d = data };
            w.ReadHeader();
            return w;
        }

        void ReadHeader()
        {
            int end = Array.IndexOf(d, (byte)0, 8, 128);
            Schema = Encoding.ASCII.GetString(d, 8, (end < 0 ? 136 : end) - 8);
            int p = 136;
            int recordCount = I32(d, p); FieldCount = I32(d, p + 4); RecordSize = I32(d, p + 8);
            TableHash = U32(d, p + 16); LayoutHash = U32(d, p + 20); MinId = I32(d, p + 24); MaxId = I32(d, p + 28);
            Flags = BitConverter.ToUInt16(d, p + 36); IdIndex = BitConverter.ToUInt16(d, p + 38);
            int fsi = I32(d, p + 52), cds = I32(d, p + 56), pds = I32(d, p + 60), sectionCount = I32(d, p + 64);
            if (recordCount < 0 || FieldCount < 0 || FieldCount > 4096 || sectionCount < 0 || sectionCount > 256 || fsi < 0 || cds < 0 || pds < 0)
                throw new Db2Exception("the WDC5 header holds impossible numbers");
            p = 204;
            long need = p + 40L * sectionCount + 4L * FieldCount + fsi + pds + cds;
            if (need > d.Length) throw new Db2Exception("the WDC5 header runs past the end of the file");
            int secAt = p;
            p += 40 * sectionCount + 4 * FieldCount;                 // section headers, field_structure (not needed)
            int nInfo = fsi / 24;
            Fields = new FieldInfo[nInfo];
            for (int i = 0; i < nInfo; i++)
            {
                int q = p + i * 24;
                Fields[i] = new FieldInfo
                {
                    OffsetBits = BitConverter.ToUInt16(d, q), SizeBits = BitConverter.ToUInt16(d, q + 2),
                    AdditionalSize = I32(d, q + 4), StorageType = I32(d, q + 8), C0 = U32(d, q + 12), C1 = U32(d, q + 16), C2 = U32(d, q + 20),
                };
            }
            p += fsi;
            int palletAt = p, commonAt = p + pds;
            pallet = new uint[nInfo][]; common = new Dictionary<int, uint>[nInfo];
            int pp = palletAt, cp = commonAt;
            for (int i = 0; i < nInfo; i++)
            {
                var f = Fields[i];
                if (f.StorageType == StoragePallet || f.StorageType == StoragePalletArray)
                {
                    int n = Math.Max(0, f.AdditionalSize) / 4;
                    if (pp + 4L * n > palletAt + pds) throw new Db2Exception("pallet data is shorter than the field table says");
                    var arr = new uint[n];
                    for (int k = 0; k < n; k++) arr[k] = U32(d, pp + k * 4);
                    pallet[i] = arr; pp += n * 4;
                }
                else if (f.StorageType == StorageCommon)
                {
                    int n = Math.Max(0, f.AdditionalSize) / 8;
                    if (cp + 8L * n > commonAt + cds) throw new Db2Exception("common data is shorter than the field table says");
                    var map = new Dictionary<int, uint>(n);
                    for (int k = 0; k < n; k++) map[I32(d, cp + k * 8)] = U32(d, cp + k * 8 + 4);
                    common[i] = map; cp += n * 8;
                }
            }

            var list = new List<Rec>(Math.Max(0, Math.Min(recordCount, 1 << 22)));
            for (int s = 0; s < sectionCount; s++)
            {
                int h = secAt + s * 40;
                ulong key = BitConverter.ToUInt64(d, h);
                int fileOffset = I32(d, h + 8), recCount = I32(d, h + 12), strSize = I32(d, h + 16), recEnd = I32(d, h + 20), idSize = I32(d, h + 24),
                    omCount = I32(d, h + 32), copyCount = I32(d, h + 36);
                secs.Add(new[] { fileOffset, recCount, strSize, key != 0 ? 1 : 0 });
                if (key != 0) { EncryptedSections++; EncryptedRecords += Math.Max(0, recCount); continue; }
                if (recCount < 0 || omCount < 0 || copyCount < 0 || fileOffset < 0 || fileOffset > d.Length) throw new Db2Exception("section " + s + " is out of range");
                long pos = Sparse ? recEnd : (long)fileOffset + (long)recCount * RecordSize + strSize;
                if (pos < fileOffset || pos > d.Length) throw new Db2Exception("section " + s + " is out of range");
                var ids = new int[recCount];
                bool haveIds = idSize >= 4L * recCount && recCount > 0;
                if (haveIds)
                {
                    if (pos + 4L * recCount > d.Length) throw new Db2Exception("section " + s + ": id list runs past the end");
                    for (int k = 0; k < recCount; k++) ids[k] = I32(d, (int)pos + k * 4);
                }
                pos += idSize;
                int copyAt = (int)pos;
                if (pos + 8L * copyCount > d.Length) throw new Db2Exception("section " + s + ": copy table runs past the end");
                pos += 8L * copyCount;
                int omAt = (int)pos;
                int first = list.Count;
                int relSize = I32(d, h + 28);
                if (!Sparse && relSize >= 12 && omAt + (long)relSize <= d.Length)
                {
                    // relationship data ("noninline" foreign key): count, min, max, then (foreign id, record index) pairs
                    int rn = I32(d, omAt);
                    if (rn >= 0 && 12L + 8L * rn <= relSize)
                        for (int k = 0; k < rn; k++) { int ri = I32(d, omAt + 16 + k * 8); if (ri >= 0 && ri < recCount) parents[first + ri] = I32(d, omAt + 12 + k * 8); }
                }
                if (Sparse)
                {
                    if (pos + 6L * omCount > d.Length) throw new Db2Exception("section " + s + ": offset map runs past the end");
                    int n = Math.Min(recCount, omCount);
                    for (int k = 0; k < n; k++)
                    {
                        int off = I32(d, omAt + k * 6); int sz = BitConverter.ToUInt16(d, omAt + k * 6 + 4);
                        if (sz == 0) continue;
                        if (off < 0 || (long)off + sz > d.Length) continue;
                        if (!haveIds) throw new Db2Exception("a sparse section without an id list");
                        list.Add(new Rec { Id = ids[k], Pos = off, Size = sz });
                    }
                }
                else
                {
                    if ((long)fileOffset + (long)recCount * RecordSize > d.Length) throw new Db2Exception("section " + s + ": records run past the end");
                    for (int k = 0; k < recCount; k++)
                    {
                        var r = new Rec { Pos = fileOffset + k * RecordSize, Size = RecordSize, Sec = s };
                        r.Id = haveIds ? ids[k] : (int)ReadField(r, 0, IdIndex, 0);
                        list.Add(r);
                    }
                }
                // the copy table: new id = a copy of an existing record
                var local = new Dictionary<int, int>();
                for (int k = first; k < list.Count; k++) local[list[k].Id] = k;
                for (int k = 0; k < copyCount; k++)
                {
                    int newId = I32(d, copyAt + k * 8), srcId = I32(d, copyAt + k * 8 + 4);
                    if (local.TryGetValue(srcId, out int si)) { if (parents.TryGetValue(si, out int sp)) parents[list.Count] = sp; list.Add(new Rec { Id = newId, Pos = list[si].Pos, Size = list[si].Size, Sec = list[si].Sec }); }
                    else if (byId.TryGetValue(srcId, out int gi) && gi < list.Count) { if (parents.TryGetValue(gi, out int gp)) parents[list.Count] = gp; list.Add(new Rec { Id = newId, Pos = list[gi].Pos, Size = list[gi].Size, Sec = list[gi].Sec }); }
                }
                for (int k = first; k < list.Count; k++) byId[list[k].Id] = k;
            }
            recs = list.ToArray(); count = recs.Length;
            if (recs.Length != list.Count) throw new Db2Exception("internal: record count");
        }

        // ---- fields of tables with fixed records

        long ReadBits(long bitPos, int bits)
        {
            if (bits <= 0) return 0;
            if (bits > 64) bits = 64;
            long end = (bitPos + bits + 7) >> 3;
            if (bitPos < 0 || end > d.Length) throw new Db2Exception("a field lies outside the file");
            ulong v = 0; int got = 0;
            long b = bitPos >> 3; int shift = (int)(bitPos & 7);
            while (got < bits)
            {
                ulong cur = (ulong)(d[b] >> shift);
                int take = Math.Min(8 - shift, bits - got);
                cur &= (1UL << take) - 1;
                v |= cur << got;
                got += take; b++; shift = 0;
            }
            return (long)v;
        }

        long ReadField(Rec r, int index, int field, int element)
        {
            if (field < 0 || field >= Fields.Length) throw new Db2Exception("field " + field + " does not exist");
            var f = Fields[field];
            long basePos = (long)r.Pos * 8;
            switch (f.StorageType)
            {
                case StorageNone:
                    return ReadBits(basePos + f.OffsetBits + (long)element * f.SizeBits, f.SizeBits);
                case StorageBitpacked:
                    return ReadBits(basePos + f.OffsetBits, (int)f.C1);
                case StorageBitpackedSigned:
                    {
                        int n = (int)f.C1;
                        long v = ReadBits(basePos + f.OffsetBits, n);
                        if (n > 0 && n < 64 && (v & (1L << (n - 1))) != 0) v |= -1L << n;
                        return v;
                    }
                case StorageCommon:
                    {
                        if (common[field] != null && common[field].TryGetValue(IdOf(r, index), out uint cv)) return cv;
                        return f.C0;
                    }
                case StoragePallet:
                    {
                        long i = ReadBits(basePos + f.OffsetBits, (int)f.C1);
                        var arr = pallet[field];
                        return arr != null && i >= 0 && i < arr.Length ? arr[i] : 0;
                    }
                case StoragePalletArray:
                    {
                        long i = ReadBits(basePos + f.OffsetBits, (int)f.C1);
                        var arr = pallet[field];
                        long at = i * (long)f.C2 + element;
                        return arr != null && at >= 0 && at < arr.Length ? arr[at] : 0;
                    }
                default: throw new Db2Exception("unknown field storage type " + f.StorageType);
            }
        }

        int IdOf(Rec r, int index) => index >= 0 && index < recs.Length ? recs[index].Id : r.Id;

        // element of an array field stored plainly (a float[6] is one field of 6 x 32 bits): the element width is the field width / count
        public long Element(int index, int field, int element, int count)
        {
            var f = Fields[field];
            if (f.StorageType != StorageNone || count <= 1) return Field(index, field, element);
            int w = f.SizeBits / count;
            return ReadBits((long)recs[index].Pos * 8 + f.OffsetBits + (long)element * w, w);
        }

        // value of a field as an unsigned number (bit-packed signed fields are sign extended); element = array index
        public long Field(int index, int field, int element = 0) => ReadField(recs[index], index, field, element);

        // a string field of a table with fixed records: the value is an offset from the field's own position into the string block
        public string String(int index, int field)
        {
            var r = recs[index];
            var f = Fields[field];
            long v = ReadField(r, index, field, 0);
            long at = (long)r.Pos + (f.OffsetBits >> 3) + (int)v;
            if (secs.Count > 1 && !Sparse)
            {
                // several sections: the offset counts from the field to a string block laid out as if ALL records (locked sections too) came
                // first and the string blocks of all sections followed one after the other
                long cumRecs = 0, total = 0;
                for (int s = 0; s < secs.Count; s++) { if (s < r.Sec) cumRecs += secs[s][1]; total += secs[s][1]; }
                long k = (r.Pos - secs[r.Sec][0]) / Math.Max(1, RecordSize);
                long idx = (cumRecs + k) * RecordSize + (f.OffsetBits >> 3) + (int)v - total * RecordSize, cs = 0;
                at = -1;
                for (int s = 0; s < secs.Count; s++)
                {
                    if (idx >= cs && idx < cs + secs[s][2]) { if (secs[s][3] == 0) at = secs[s][0] + (long)secs[s][1] * RecordSize + (idx - cs); break; }
                    cs += secs[s][2];
                }
            }
            if (at < 0 || at >= d.Length) return "";
            return CString(d, (int)at, d.Length);
        }

        public static string CString(byte[] b, int at, int limit)
        {
            int e = at;
            while (e < limit && b[e] != 0) e++;
            return e == at ? "" : Encoding.UTF8.GetString(b, at, e - at);
        }
    }
}
