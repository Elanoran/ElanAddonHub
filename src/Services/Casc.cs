using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace ElansAddonHub.Services
{
    // A small READ-ONLY reader for Blizzard's local CASC storage (the game's Data folder), written from the public format
    // description on wowdev.wiki (CASC / BLTE / Encoding / Root). No third party code and no native DLL.
    // Only what the hub needs: FileDataID -> file bytes (item icons). It never writes, and opens every file with
    // FileShare.ReadWrite so it works while the game is running.
    public sealed class CascStorage : IDisposable
    {
        public struct Loc { public int Archive; public long Offset; public int Size; }

        readonly string dataDir;
        readonly byte[][] idx = new byte[16][];
        readonly int[] idxCount = new int[16];
        readonly Dictionary<int, FileStream> archives = new Dictionary<int, FileStream>();
        public string Product, BuildName;
        byte[] rootCKey, encodingEKey;
        // encoding file: page index (first ckey of every page) and a random access view
        BlteFile encoding;
        int encPageKb, encPages;
        byte[] encFirstKeys;
        long encPagesStart;

        CascStorage(string dir) { dataDir = dir; }

        static FileStream OpenShared(string path) => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);

        static string ProductFor(string flavor)
        {
            switch (flavor)
            {
                case "_classic_beta_": return "wow_classic_beta";
                case "_classic_era_": return "wow_classic_era";
                case "_classic_": return "wow_classic";
                case "_retail_": return "wow";
                default: return null;
            }
        }

        // wowRoot = the folder with "Data" and ".build.info". flavor e.g. "_classic_beta_" (picks that product's build)
        public static CascStorage Open(string wowRoot, string flavor = "_classic_beta_")
        {
            var data = Path.Combine(wowRoot, "Data");
            var info = Path.Combine(wowRoot, ".build.info");
            if (!Directory.Exists(Path.Combine(data, "data"))) throw new IOException("no Data\\data folder in " + wowRoot);
            if (!File.Exists(info)) throw new IOException("no .build.info in " + wowRoot);
            string[] lines;
            using (var fs = OpenShared(info)) using (var sr = new StreamReader(fs)) lines = sr.ReadToEnd().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var cols = lines[0].Split('|').Select(c => c.Split('!')[0]).ToList();
            int iBuild = cols.IndexOf("Build Key"), iProd = cols.IndexOf("Product"), iVer = cols.IndexOf("Version");
            if (iBuild < 0 || iProd < 0) throw new IOException(".build.info has no Build Key/Product columns");
            var rows = lines.Skip(1).Select(l => l.Split('|')).Where(r => r.Length > Math.Max(iBuild, iProd)).ToList();
            var want = ProductFor(flavor);
            var row = rows.FirstOrDefault(r => r[iProd] == want) ?? rows.FirstOrDefault(r => r[iProd].StartsWith("wow"));
            if (row == null) throw new IOException("no product for " + flavor + " in .build.info");

            var s = new CascStorage(data) { Product = row[iProd] };
            try
            {
                var key = row[iBuild];
                var cfg = Path.Combine(data, "config", key.Substring(0, 2), key.Substring(2, 2), key);
                if (!File.Exists(cfg)) throw new IOException("build config " + key + " is not in Data\\config");
                string text;
                using (var fs = OpenShared(cfg)) using (var sr = new StreamReader(fs)) text = sr.ReadToEnd();
                foreach (var line in text.Split('\n'))
                {
                    var eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    var k = line.Substring(0, eq).Trim();
                    var parts = line.Substring(eq + 1).Trim().Split(' ');
                    if (k == "root") s.rootCKey = Hex(parts[0]);
                    else if (k == "encoding" && parts.Length > 1) s.encodingEKey = Hex(parts[1]);
                    else if (k == "build-name") s.BuildName = line.Substring(eq + 1).Trim();
                }
                if (s.rootCKey == null || s.encodingEKey == null) throw new IOException("build config has no root/encoding");
                s.LoadIndexes();
                s.OpenEncoding();
                return s;
            }
            catch { s.Dispose(); throw; }
        }

        static byte[] Hex(string h)
        {
            var b = new byte[h.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(h.Substring(i * 2, 2), 16);
            return b;
        }

        // newest .idx of each of the 16 buckets, held in memory (about 2.4 MB each); entries: ekey(9) offset(5, big endian) size(4)
        void LoadIndexes()
        {
            var best = new Dictionary<int, KeyValuePair<long, string>>();
            foreach (var f in Directory.GetFiles(Path.Combine(dataDir, "data"), "*.idx"))
            {
                var n = Path.GetFileNameWithoutExtension(f);
                if (n.Length != 10) continue;
                try
                {
                    int b = Convert.ToInt32(n.Substring(0, 2), 16);
                    long v = Convert.ToInt64(n.Substring(2), 16);
                    if (b < 16 && (!best.TryGetValue(b, out var o) || v > o.Key)) best[b] = new KeyValuePair<long, string>(v, f);
                }
                catch { }
            }
            if (best.Count == 0) throw new IOException("no index files in Data\\data");
            foreach (var kv in best)
            {
                byte[] d;
                using (var fs = OpenShared(kv.Value.Value)) { d = new byte[fs.Length]; ReadFull(fs, d, 0, d.Length); }
                if (d.Length < 0x28 || BitConverter.ToUInt16(d, 8) != 7 || d[0x0D] != 5 || d[0x0E] != 9 || d[0x0C] != 4)
                    throw new IOException("unsupported index format in " + Path.GetFileName(kv.Value.Value));
                idx[kv.Key] = d;
                int blob = (int)BitConverter.ToUInt32(d, 0x20);
                idxCount[kv.Key] = Math.Min(blob, d.Length - 0x28) / 18;
            }
        }

        public bool TryFind(byte[] ekey, out Loc loc)
        {
            for (int b = 0; b < 16; b++)
            {
                var d = idx[b];
                if (d == null) continue;
                int lo = 0, hi = idxCount[b] - 1;
                while (lo <= hi)
                {
                    int mid = (lo + hi) >> 1, o = 0x28 + mid * 18, c = 0;
                    for (int i = 0; i < 9 && c == 0; i++) c = d[o + i].CompareTo(ekey[i]);
                    if (c == 0)
                    {
                        long off = 0;
                        for (int i = 0; i < 5; i++) off = (off << 8) | d[o + 9 + i];
                        loc = new Loc { Archive = (int)(off >> 30), Offset = off & 0x3FFFFFFF, Size = (int)BitConverter.ToUInt32(d, o + 14) };
                        return true;
                    }
                    if (c < 0) lo = mid + 1; else hi = mid - 1;
                }
            }
            loc = default(Loc);
            return false;
        }

        FileStream Archive(int n)
        {
            if (!archives.TryGetValue(n, out var fs))
            {
                fs = OpenShared(Path.Combine(dataDir, "data", "data." + n.ToString("D3")));
                archives[n] = fs;
            }
            return fs;
        }

        // the BLTE file behind an encoding key
        BlteFile OpenEKey(byte[] ekey)
        {
            if (!TryFind(ekey, out var loc)) throw new FileNotFoundException("file is not in the local storage (ekey " + BitConverter.ToString(ekey, 0, 9).Replace("-", "") + ")");
            return new BlteFile(Archive(loc.Archive), loc.Offset + 30, loc.Size - 30);
        }

        void OpenEncoding()
        {
            encoding = OpenEKey(encodingEKey);
            var h = new byte[22];
            encoding.ReadAt(0, h, 0, 22);
            if (h[0] != 'E' || h[1] != 'N' || h[2] != 1 || h[3] != 16 || h[4] != 16) throw new IOException("unsupported encoding file");
            encPageKb = (h[5] << 8) | h[6];
            // h[7..8] = especPageKb (ckey->espec pages are not used)
            int ccount = (h[9] << 24) | (h[10] << 16) | (h[11] << 8) | h[12];
            int especSize = (h[18] << 24) | (h[19] << 16) | (h[20] << 8) | h[21];
            encPages = ccount;
            encFirstKeys = new byte[ccount * 16];
            // page index entries are 32 bytes: first ckey(16) + page md5(16)
            var pidx = new byte[ccount * 32];
            encoding.ReadAt(22 + especSize, pidx, 0, pidx.Length);
            for (int i = 0; i < ccount; i++) Buffer.BlockCopy(pidx, i * 32, encFirstKeys, i * 16, 16);
            encPagesStart = 22 + especSize + (long)ccount * 32;
        }

        // content key -> encoding key (first one)
        public byte[] EncodingKeyFor(byte[] ckey)
        {
            int lo = 0, hi = encPages - 1, page = 0;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (Cmp16(encFirstKeys, mid * 16, ckey) <= 0) { page = mid; lo = mid + 1; } else hi = mid - 1;
            }
            int size = encPageKb * 1024;
            var pg = new byte[size];
            encoding.ReadAt(encPagesStart + (long)page * size, pg, 0, size);
            int p = 0;
            while (p + 22 <= size)
            {
                int n = pg[p];
                if (n == 0) break;
                if (Cmp16(pg, p + 6, ckey) == 0)
                {
                    var ek = new byte[16];
                    Buffer.BlockCopy(pg, p + 22, ek, 0, 16);
                    return ek;
                }
                p += 22 + 16 * n;
            }
            return null;
        }

        static int Cmp16(byte[] a, int ao, byte[] b)
        {
            for (int i = 0; i < 16; i++) { int c = a[ao + i].CompareTo(b[i]); if (c != 0) return c; }
            return 0;
        }

        // FileDataID -> content key, for all wanted ids in one pass over the root file (TSFM, versions 1 and 2)
        public Dictionary<int, byte[]> ResolveFileIds(HashSet<int> wanted)
        {
            var res = new Dictionary<int, byte[]>();
            var score = new Dictionary<int, int>();
            var ek = EncodingKeyFor(rootCKey);
            if (ek == null) throw new IOException("root file is not in the encoding table");
            using (var root = OpenEKey(ek))
            {
                var hd = new byte[24];
                root.ReadAt(0, hd, 0, 24);
                if (hd[0] != 'T' || hd[1] != 'S' || hd[2] != 'F' || hd[3] != 'M') throw new IOException("unknown root format");
                uint hdrSize = BitConverter.ToUInt32(hd, 4), ver = BitConverter.ToUInt32(hd, 8);
                // v2: magic, header size (0x18), version (2), total files, named files, padding. Older: magic, total, named.
                bool v2 = hdrSize == 0x18 && (ver == 1 || ver == 2);
                long pos = v2 ? hdrSize : 12;
                ver = v2 ? ver : 0;
                long len = root.Length;
                var bh = new byte[17];
                while (pos + 12 < len)
                {
                    root.ReadAt(pos, bh, 0, 12);
                    int nr = (int)BitConverter.ToUInt32(bh, 0);
                    uint locale, cf;
                    if (ver == 2)
                    {
                        root.ReadAt(pos + 12, bh, 12, 5);
                        locale = BitConverter.ToUInt32(bh, 4);
                        cf = BitConverter.ToUInt32(bh, 8) | BitConverter.ToUInt32(bh, 12) | ((uint)bh[16] << 17);
                        pos += 17;
                    }
                    else { cf = BitConverter.ToUInt32(bh, 4); locale = BitConverter.ToUInt32(bh, 8); pos += 12; }
                    var ids = new byte[nr * 4];
                    root.ReadAt(pos, ids, 0, ids.Length);
                    long ckPos = pos + ids.Length;
                    int cur = -1;
                    List<int> hit = null;
                    var hitIds = new List<int>();
                    for (int i = 0; i < nr; i++)
                    {
                        cur = i == 0 ? BitConverter.ToInt32(ids, 0) : cur + 1 + BitConverter.ToInt32(ids, i * 4);
                        if (wanted.Contains(cur)) { (hit ?? (hit = new List<int>())).Add(i); hitIds.Add(cur); }
                    }
                    if (hit != null)
                    {
                        // prefer: not low-violence (0x80), not do-not-load (0x100), enUS or all locales
                        int sc = ((cf & 0x80) == 0 ? 4 : 0) + ((cf & 0x100) == 0 ? 2 : 0) + ((locale & 0x2) != 0 ? 1 : 0);
                        var one = new byte[16];
                        for (int h = 0; h < hit.Count; h++)
                        {
                            int fid = hitIds[h];
                            if (score.TryGetValue(fid, out var old) && old >= sc) continue;
                            root.ReadAt(ckPos + (long)hit[h] * 16, one, 0, 16);
                            res[fid] = (byte[])one.Clone();
                            score[fid] = sc;
                        }
                    }
                    pos = ckPos + (long)nr * 16 + ((cf & 0x10000000) != 0 ? 0 : (long)nr * 8);
                }
            }
            return res;
        }

        // the decoded bytes of a file by content key, or null when the storage does not hold it
        public byte[] ReadByContentKey(byte[] ckey)
        {
            var ek = EncodingKeyFor(ckey);
            if (ek == null) return null;
            if (!TryFind(ek, out _)) return null;
            using (var f = OpenEKey(ek))
            {
                var buf = new byte[f.Length];
                f.ReadAt(0, buf, 0, buf.Length);
                return buf;
            }
        }

        internal static void ReadFull(Stream s, byte[] buf, int off, int count)
        {
            while (count > 0)
            {
                int n = s.Read(buf, off, count);
                if (n <= 0) throw new EndOfStreamException();
                off += n; count -= n;
            }
        }

        public void Dispose()
        {
            encoding?.Dispose();
            foreach (var a in archives.Values) { try { a.Dispose(); } catch { } }
            archives.Clear();
        }
    }

    // BLTE container with random access to the decoded bytes: blocks are decoded on demand (a few kept in a small cache)
    internal sealed class BlteFile : IDisposable
    {
        readonly FileStream src;
        readonly long start;
        readonly long[] rawPos;     // position of each block in the archive file
        readonly int[] rawSize, decSize;
        readonly long[] decStart;
        public long Length { get; private set; }
        readonly Dictionary<int, byte[]> cache = new Dictionary<int, byte[]>();
        readonly Queue<int> order = new Queue<int>();
        readonly object gate = new object();

        public BlteFile(FileStream src, long start, int size)
        {
            this.src = src; this.start = start;
            var h = new byte[12];
            Read(start, h, 0, 12);
            if (h[0] != 'B' || h[1] != 'L' || h[2] != 'T' || h[3] != 'E') throw new IOException("not a BLTE block");
            int headerSize = (h[4] << 24) | (h[5] << 16) | (h[6] << 8) | h[7];
            if (headerSize == 0)
            {
                // one block, no table
                rawPos = new[] { start + 8 };
                rawSize = new[] { size - 8 };
                decSize = new[] { -1 };
                decStart = new long[] { 0 };
                var all = Block(0);
                decSize[0] = all.Length;
                Length = all.Length;
                return;
            }
            int count = (h[9] << 16) | (h[10] << 8) | h[11];
            var tab = new byte[count * 24];
            Read(start + 12, tab, 0, tab.Length);
            rawPos = new long[count]; rawSize = new int[count]; decSize = new int[count]; decStart = new long[count];
            long rp = start + headerSize, dp = 0;
            for (int i = 0; i < count; i++)
            {
                int cs = (tab[i * 24] << 24) | (tab[i * 24 + 1] << 16) | (tab[i * 24 + 2] << 8) | tab[i * 24 + 3];
                int ds = (tab[i * 24 + 4] << 24) | (tab[i * 24 + 5] << 16) | (tab[i * 24 + 6] << 8) | tab[i * 24 + 7];
                rawPos[i] = rp; rawSize[i] = cs; decSize[i] = ds; decStart[i] = dp;
                rp += cs; dp += ds;
            }
            Length = dp;
        }

        void Read(long pos, byte[] buf, int off, int count)
        {
            lock (gate) { src.Position = pos; CascStorage.ReadFull(src, buf, off, count); }
        }

        byte[] Block(int i)
        {
            if (cache.TryGetValue(i, out var c)) return c;
            var raw = new byte[rawSize[i]];
            Read(rawPos[i], raw, 0, raw.Length);
            byte[] dec;
            switch ((char)raw[0])
            {
                case 'N': dec = new byte[raw.Length - 1]; Buffer.BlockCopy(raw, 1, dec, 0, dec.Length); break;
                case 'Z':
                    using (var ms = new MemoryStream(raw, 3, raw.Length - 3)) // 1 type byte + 2 byte zlib header
                    using (var ds = new DeflateStream(ms, CompressionMode.Decompress))
                    using (var o = new MemoryStream(decSize[i] > 0 ? decSize[i] : 4096))
                    {
                        ds.CopyTo(o);
                        dec = o.ToArray();
                    }
                    break;
                default: throw new NotSupportedException("BLTE block type '" + (char)raw[0] + "' (encrypted or unknown) is not supported");
            }
            cache[i] = dec; order.Enqueue(i);
            while (order.Count > 6) cache.Remove(order.Dequeue());
            return dec;
        }

        public void ReadAt(long pos, byte[] buf, int off, int count)
        {
            if (pos < 0 || pos + count > Length) throw new EndOfStreamException();
            // first block that holds pos
            int lo = 0, hi = decStart.Length - 1, bi = 0;
            while (lo <= hi) { int mid = (lo + hi) >> 1; if (decStart[mid] <= pos) { bi = mid; lo = mid + 1; } else hi = mid - 1; }
            while (count > 0)
            {
                var b = Block(bi);
                int inBlock = (int)(pos - decStart[bi]);
                int n = Math.Min(count, b.Length - inBlock);
                Buffer.BlockCopy(b, inBlock, buf, off, n);
                pos += n; off += n; count -= n;
                bi++;
            }
        }

        public void Dispose() { cache.Clear(); }
    }
}
