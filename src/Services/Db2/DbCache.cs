using System;
using System.Collections.Generic;
using System.IO;

namespace ElansAddonHub.Services.Db2
{
    // The client's own hotfix cache (Cache\ADB\<locale>\DBCache.bin, "XFTH" version 9): rows the server pushed to the client, among them the
    // item rows of items that were "hidden" until somebody found them. Read-only, opened with FileShare.ReadWrite|Delete (the game rewrites it).
    //   file:  "XFTH", version(u32), build(u32), verification hash(32)
    //   entry: "XFTH", region(u32), push id(u32), unique id(u32), table hash(u32), record id(u32), data size(u32), status(u8) + 3 pad, data
    //   status 1 = a valid row; the row bytes are the table's own record (for ItemSparse the "sparse" record: five inline strings, then the fixed fields).
    public static class DbCache
    {
        public const uint ItemSparseHash = 0x919BE54E;
        public const int SupportedVersion = 9;
        public const int HeaderSize = 44, EntryHeader = 32;

        public sealed class Result
        {
            public int Version, Build;
            public bool Truncated;                       // the file ended in the middle of an entry (the game was writing it): what came before is used
            public Dictionary<int, byte[]> Rows = new Dictionary<int, byte[]>();
            public int Entries;
        }

        public static byte[] ReadAllShared(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16))
            {
                var buf = new byte[fs.Length];
                int got = 0;
                while (got < buf.Length) { int n = fs.Read(buf, got, buf.Length - got); if (n <= 0) break; got += n; }
                if (got < buf.Length) Array.Resize(ref buf, got);
                return buf;
            }
        }

        // the valid rows of one table (the last row of an id wins); throws Db2Exception for a file that is not a version 9 cache
        public static Result Read(byte[] file, uint tableHash)
        {
            if (file == null || file.Length < HeaderSize) throw new Db2Exception("DBCache.bin is too short");
            if (BitConverter.ToUInt32(file, 0) != 0x48544658) throw new Db2Exception("DBCache.bin: not an XFTH file");
            var r = new Result { Version = BitConverter.ToInt32(file, 4), Build = BitConverter.ToInt32(file, 8) };
            if (r.Version != SupportedVersion) throw new Db2Exception("DBCache.bin version " + r.Version + " (this reader knows " + SupportedVersion + ")");
            long p = HeaderSize;
            while (p + EntryHeader <= file.Length)
            {
                if (BitConverter.ToUInt32(file, (int)p) != 0x48544658) { r.Truncated = true; break; }
                uint table = BitConverter.ToUInt32(file, (int)p + 16);
                int id = BitConverter.ToInt32(file, (int)p + 20);
                uint size = BitConverter.ToUInt32(file, (int)p + 24);
                byte status = file[p + 28];
                if (size > 1 << 20 || p + EntryHeader + size > file.Length) { r.Truncated = true; break; }
                r.Entries++;
                if (table == tableHash && status == 1 && size > 0)
                {
                    var row = new byte[size];
                    Buffer.BlockCopy(file, (int)p + EntryHeader, row, 0, (int)size);
                    r.Rows[id] = row;
                }
                else if (table == tableHash && status != 1) r.Rows.Remove(id);   // a later "removed / invalid" entry takes the row away again
                p += EntryHeader + size;
            }
            return r;
        }

        // ---- several tables at once, with the removals kept (the item reader above only needs the rows)
        // Status, as the client treats it: 1 = the row (it replaces the shipped row of the same id), 2 = "record removed" (the shipped row is gone too),
        // 3 = invalid and 4 = not public (no data: an earlier hotfix row of that id no longer counts, the shipped row stands). The last entry of an id wins.
        public const byte StatusValid = 1, StatusRemoved = 2;

        public sealed class TableRows
        {
            public readonly Dictionary<int, byte[]> Rows = new Dictionary<int, byte[]>();
            public readonly HashSet<int> Removed = new HashSet<int>();
            public int Entries;
        }

        public sealed class Tables
        {
            public int Version, Build, Entries;
            public bool Truncated;
            public readonly Dictionary<uint, TableRows> ByHash = new Dictionary<uint, TableRows>();
            public TableRows Get(uint hash) => ByHash.TryGetValue(hash, out var t) ? t : null;
        }

        public static Tables ReadTables(byte[] file, ICollection<uint> hashes)
        {
            if (file == null || file.Length < HeaderSize) throw new Db2Exception("DBCache.bin is too short");
            if (BitConverter.ToUInt32(file, 0) != 0x48544658) throw new Db2Exception("DBCache.bin: not an XFTH file");
            var r = new Tables { Version = BitConverter.ToInt32(file, 4), Build = BitConverter.ToInt32(file, 8) };
            if (r.Version != SupportedVersion) throw new Db2Exception("DBCache.bin version " + r.Version + " (this reader knows " + SupportedVersion + ")");
            long p = HeaderSize;
            while (p + EntryHeader <= file.Length)
            {
                if (BitConverter.ToUInt32(file, (int)p) != 0x48544658) { r.Truncated = true; break; }
                uint table = BitConverter.ToUInt32(file, (int)p + 16);
                int id = BitConverter.ToInt32(file, (int)p + 20);
                uint size = BitConverter.ToUInt32(file, (int)p + 24);
                byte status = file[p + 28];
                if (size > 1 << 20 || p + EntryHeader + size > file.Length) { r.Truncated = true; break; }
                r.Entries++;
                if (hashes.Contains(table))
                {
                    if (!r.ByHash.TryGetValue(table, out var t)) r.ByHash[table] = t = new TableRows();
                    t.Entries++;
                    t.Rows.Remove(id); t.Removed.Remove(id);
                    if (status == StatusValid && size > 0)
                    {
                        var row = new byte[size];
                        Buffer.BlockCopy(file, (int)p + EntryHeader, row, 0, (int)size);
                        t.Rows[id] = row;
                    }
                    else if (status == StatusRemoved) t.Removed.Add(id);
                }
                p += EntryHeader + size;
            }
            return r;
        }
    }
}
