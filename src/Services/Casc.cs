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
        // false (icons): an encrypted (key-locked) BLTE block throws. true (item tables): such a block reads as zeros, so the caller can skip the part that is locked.
        public bool ZeroFillEncrypted;
        byte[] rootCKey, encodingEKey;
        // encoding file: page index (first ckey of every page) and a random access view
        BlteFile encoding;
        int encPageKb, encPages;
        byte[] encFirstKeys;
        long encPagesStart;

        CascStorage(string dir) { dataDir = dir; }

        static FileStream OpenShared(string path) => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);

        internal static string ProductFor(string flavor)
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
            lock (archives)
            {
                if (!archives.TryGetValue(n, out var fs))
                {
                    fs = OpenShared(Path.Combine(dataDir, "data", "data." + n.ToString("D3")));
                    archives[n] = fs;
                }
                return fs;
            }
        }

        // the BLTE file behind an encoding key
        BlteFile OpenEKey(byte[] ekey)
        {
            if (disposed) throw new ObjectDisposedException("CascStorage", "the game storage is closed");
            if (!TryFind(ekey, out var loc)) throw new FileNotFoundException("file is not in the local storage (ekey " + BitConverter.ToString(ekey, 0, 9).Replace("-", "") + ")");
            return new BlteFile(Archive(loc.Archive), loc.Offset + 30, loc.Size - 30, ZeroFillEncrypted);
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

        // ---- the root as a sorted table (opt-in: UseRootIndex). Without it every ResolveFileIds walks (and decodes) the whole root file again, about
        // 0.1 s and 50+ MB of short-lived arrays each time; a reader that asks for files in several rounds (the map data: a WDT, then its tiles, per
        // instance; the area probe: one tile at a time) paid that dozens or hundreds of times. With it the root is walked once per opened storage and
        // every later lookup is a binary search. Same answers as the walk: per FileDataID the entry with the best score, the first one on a tie.
        public bool UseRootIndex;
        readonly object rootGate = new object();
        sealed class RootTable { public int[] Ids; public byte[] Keys; }     // Ids sorted, unique; Keys 16 bytes per id, same order
        RootTable rootTable;    // under rootGate (set once)
        volatile bool disposed;
        public int RootIndexCount { get { lock (rootGate) return rootTable?.Ids.Length ?? 0; } }
        public long RootIndexMs { get; private set; }

        // FileDataID -> content key, for all wanted ids in one pass over the root file (TSFM, versions 1 and 2)
        public Dictionary<int, byte[]> ResolveFileIds(HashSet<int> wanted)
        {
            if (disposed) throw new ObjectDisposedException("CascStorage", "the game storage is closed");
            if (UseRootIndex)
            {
                // built once, by the first caller; the others wait for it here. ids and keys are taken together (one object), so a reader never sees
                // the ids of the new table with the keys of none
                RootTable t;
                lock (rootGate) { if (rootTable == null) BuildRootIndex(); t = rootTable; }
                var hit = new Dictionary<int, byte[]>();
                foreach (var fid in wanted)
                {
                    int i = Array.BinarySearch(t.Ids, fid);
                    if (i < 0) continue;
                    var k = new byte[16];
                    Buffer.BlockCopy(t.Keys, i * 16, k, 0, 16);
                    hit[fid] = k;
                }
                return hit;
            }
            return WalkRoot(wanted);
        }

        void BuildRootIndex()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int n = 0;
            int cap0 = Math.Max(1 << 16, Math.Min(1 << 26, RootEntryCount() + 1024));     // the header says how many entries there are: no growing arrays
            var ids = new int[cap0]; var scores = new byte[cap0]; var keys = new byte[cap0 * 16];
            ForEachRootBlock((nr, idBuf, keyBuf, sc) =>
            {
                if (n + nr > ids.Length)
                {
                    int cap = Math.Max(ids.Length * 2, n + nr);
                    Array.Resize(ref ids, cap); Array.Resize(ref scores, cap); Array.Resize(ref keys, cap * 16);
                }
                Buffer.BlockCopy(idBuf, 0, ids, n * 4, nr * 4);
                Buffer.BlockCopy(keyBuf, 0, keys, n * 16, nr * 16);
                for (int i = 0; i < nr; i++) scores[n + i] = (byte)sc;
                n += nr;
            });
            if (n >= 1 << 28) throw new IOException("root file too large");
            // order: id, then the better score, then the earlier entry
            var order = new long[n];
            for (int i = 0; i < n; i++) order[i] = ((long)ids[i] << 32) | ((long)(7 - scores[i]) << 28) | (uint)i;
            Array.Sort(order);
            int u = 0;
            for (int i = 0; i < n; i++) if (i == 0 || (int)(order[i] >> 32) != (int)(order[i - 1] >> 32)) u++;
            var uIds = new int[u]; var uKeys = new byte[u * 16];
            int w = 0;
            for (int i = 0; i < n; i++)
            {
                int id = (int)(order[i] >> 32);
                if (i > 0 && id == (int)(order[i - 1] >> 32)) continue;
                uIds[w] = id;
                Buffer.BlockCopy(keys, (int)(order[i] & 0x0FFFFFFF) * 16, uKeys, w * 16, 16);
                w++;
            }
            rootTable = new RootTable { Ids = uIds, Keys = uKeys };
            RootIndexMs = sw.ElapsedMilliseconds;
        }

        // the number of entries the root header announces (0 when it does not say)
        int RootEntryCount()
        {
            try
            {
                var ek = EncodingKeyFor(rootCKey);
                if (ek == null) return 0;
                using (var root = OpenEKey(ek))
                {
                    var hd = new byte[24];
                    root.ReadAt(0, hd, 0, 24);
                    uint hdrSize = BitConverter.ToUInt32(hd, 4), ver = BitConverter.ToUInt32(hd, 8);
                    bool v2 = hdrSize == 0x18 && (ver == 1 || ver == 2);
                    long n = v2 ? BitConverter.ToUInt32(hd, 12) : BitConverter.ToUInt32(hd, 4);
                    return n > 0 && n < (1 << 26) ? (int)n : 0;
                }
            }
            catch (Exception) { return 0; }
        }

        // every block of the root: (entries, their FileDataIDs (absolute, as int32 little endian), their content keys, the block's score)
        void ForEachRootBlock(Action<int, byte[], byte[], int> block)
        {
            var ek = EncodingKeyFor(rootCKey);
            if (ek == null) throw new IOException("root file is not in the encoding table");
            using (var root = OpenEKey(ek))
            {
                var hd = new byte[24];
                root.ReadAt(0, hd, 0, 24);
                if (hd[0] != 'T' || hd[1] != 'S' || hd[2] != 'F' || hd[3] != 'M') throw new IOException("unknown root format");
                uint hdrSize = BitConverter.ToUInt32(hd, 4), ver = BitConverter.ToUInt32(hd, 8);
                bool v2 = hdrSize == 0x18 && (ver == 1 || ver == 2);
                long pos = v2 ? hdrSize : 12;
                ver = v2 ? ver : 0;
                long len = root.Length;
                var bh = new byte[17];
                byte[] ids = new byte[0], keys = new byte[0];
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
                    if (ids.Length < nr * 4) { ids = new byte[nr * 4]; keys = new byte[nr * 16]; }
                    root.ReadAt(pos, ids, 0, nr * 4);
                    long ckPos = pos + (long)nr * 4;
                    root.ReadAt(ckPos, keys, 0, nr * 16);
                    // deltas -> absolute ids, in place
                    int cur = -1;
                    for (int i = 0; i < nr; i++)
                    {
                        cur = i == 0 ? BitConverter.ToInt32(ids, 0) : cur + 1 + BitConverter.ToInt32(ids, i * 4);
                        ids[i * 4] = (byte)cur; ids[i * 4 + 1] = (byte)(cur >> 8); ids[i * 4 + 2] = (byte)(cur >> 16); ids[i * 4 + 3] = (byte)(cur >> 24);
                    }
                    int sc = ((cf & 0x80) == 0 ? 4 : 0) + ((cf & 0x100) == 0 ? 2 : 0) + ((locale & 0x2) != 0 ? 1 : 0);
                    block(nr, ids, keys, sc);
                    pos = ckPos + (long)nr * 16 + ((cf & 0x10000000) != 0 ? 0 : (long)nr * 8);
                }
            }
        }

        Dictionary<int, byte[]> WalkRoot(HashSet<int> wanted)
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

        // ---- files by NAME (e.g. "Interface\Icons\Trade_Mining.blp", what an addon's toc IconTexture can point at). The root keeps, per entry
        // and unless the block's flag 0x10000000 says otherwise, an 8-byte name hash after the content keys: Jenkins lookup3 hashlittle2 over the
        // upper-case path with backslashes, pc = 0 and pb = 0 in, stored as (pc << 32) | pb (the same as CascLib's CalcFileNameHash).
        public static ulong NameHash(string path)
        {
            var norm = (path ?? "").Replace('/', '\\').ToUpperInvariant();
            var k = System.Text.Encoding.ASCII.GetBytes(norm);
            uint c = 0, b = 0;
            HashLittle2(k, ref c, ref b);
            return ((ulong)c << 32) | b;
        }

        static uint Rot(uint x, int r) => (x << r) | (x >> (32 - r));

        // Bob Jenkins' lookup3.c hashlittle2 (public domain), byte-wise reading of the little-endian words
        public static void HashLittle2(byte[] k, ref uint pc, ref uint pb)
        {
            int length = k.Length, o = 0;
            uint a, b, c;
            a = b = c = 0xdeadbeef + (uint)length + pc;
            c += pb;
            while (length > 12)
            {
                a += BitConverter.ToUInt32(k, o); b += BitConverter.ToUInt32(k, o + 4); c += BitConverter.ToUInt32(k, o + 8);
                a -= c; a ^= Rot(c, 4); c += b; b -= a; b ^= Rot(a, 6); a += c; c -= b; c ^= Rot(b, 8); b += a;
                a -= c; a ^= Rot(c, 16); c += b; b -= a; b ^= Rot(a, 19); a += c; c -= b; c ^= Rot(b, 4); b += a;
                length -= 12; o += 12;
            }
            if (length == 0) { pc = c; pb = b; return; }
            uint K(int i) => i < length ? k[o + i] : 0u;
            c += K(8) | K(9) << 8 | K(10) << 16 | K(11) << 24;
            b += K(4) | K(5) << 8 | K(6) << 16 | K(7) << 24;
            a += K(0) | K(1) << 8 | K(2) << 16 | K(3) << 24;
            c ^= b; c -= Rot(b, 14); a ^= c; a -= Rot(c, 11); b ^= a; b -= Rot(a, 25); c ^= b; c -= Rot(b, 16);
            a ^= c; a -= Rot(c, 4); b ^= a; b -= Rot(a, 14); c ^= b; c -= Rot(b, 24);
            pc = c; pb = b;
        }

        // FileDataIDs for the wanted name hashes (one pass over the root): a file named in the root is found by its path, and the icon pipeline (IconStore) takes it by id.
        // A name that is not in the root (or only in blocks without names) is simply absent.
        public Dictionary<ulong, int> ResolveNameHashesToFileIds(HashSet<ulong> wanted)
        {
            if (disposed) throw new ObjectDisposedException("CascStorage", "the game storage is closed");
            var res = new Dictionary<ulong, int>();
            var ek = EncodingKeyFor(rootCKey);
            if (ek == null) throw new IOException("root file is not in the encoding table");
            using (var root = OpenEKey(ek))
            {
                var hd = new byte[24];
                root.ReadAt(0, hd, 0, 24);
                if (hd[0] != 'T' || hd[1] != 'S' || hd[2] != 'F' || hd[3] != 'M') throw new IOException("unknown root format");
                uint hdrSize = BitConverter.ToUInt32(hd, 4), ver = BitConverter.ToUInt32(hd, 8);
                bool v2 = hdrSize == 0x18 && (ver == 1 || ver == 2);
                long pos = v2 ? hdrSize : 12;
                ver = v2 ? ver : 0;
                long len = root.Length;
                var bh = new byte[17];
                while (pos + 12 < len)
                {
                    root.ReadAt(pos, bh, 0, 12);
                    int nr = (int)BitConverter.ToUInt32(bh, 0);
                    uint cf;
                    if (ver == 2) { root.ReadAt(pos + 12, bh, 12, 5); cf = BitConverter.ToUInt32(bh, 8) | BitConverter.ToUInt32(bh, 12) | ((uint)bh[16] << 17); pos += 17; }
                    else { cf = BitConverter.ToUInt32(bh, 4); pos += 12; }
                    long ckPos = pos + (long)nr * 4, nhPos = ckPos + (long)nr * 16;
                    bool hasNames = (cf & 0x10000000) == 0;
                    if (hasNames && nr > 0)
                    {
                        var ids = new byte[nr * 4];
                        root.ReadAt(pos, ids, 0, ids.Length);
                        var names = new byte[nr * 8];
                        root.ReadAt(nhPos, names, 0, names.Length);
                        int cur = -1;
                        for (int i = 0; i < nr; i++)
                        {
                            cur = i == 0 ? BitConverter.ToInt32(ids, 0) : cur + 1 + BitConverter.ToInt32(ids, i * 4);
                            var h = BitConverter.ToUInt64(names, i * 8);
                            if (wanted.Contains(h) && !res.ContainsKey(h)) res[h] = cur;
                        }
                    }
                    pos = nhPos + (hasNames ? (long)nr * 8 : 0);
                }
            }
            return res;
        }

        // content keys for the wanted name hashes (one pass over the root; the best-scored entry per name, like ResolveFileIds)
        public Dictionary<ulong, byte[]> ResolveNameHashes(HashSet<ulong> wanted)
        {
            if (disposed) throw new ObjectDisposedException("CascStorage", "the game storage is closed");
            var res = new Dictionary<ulong, byte[]>();
            var score = new Dictionary<ulong, int>();
            var ek = EncodingKeyFor(rootCKey);
            if (ek == null) throw new IOException("root file is not in the encoding table");
            using (var root = OpenEKey(ek))
            {
                var hd = new byte[24];
                root.ReadAt(0, hd, 0, 24);
                if (hd[0] != 'T' || hd[1] != 'S' || hd[2] != 'F' || hd[3] != 'M') throw new IOException("unknown root format");
                uint hdrSize = BitConverter.ToUInt32(hd, 4), ver = BitConverter.ToUInt32(hd, 8);
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
                    long ckPos = pos + (long)nr * 4, nhPos = ckPos + (long)nr * 16;
                    bool hasNames = (cf & 0x10000000) == 0;
                    if (hasNames && nr > 0)
                    {
                        var names = new byte[nr * 8];
                        root.ReadAt(nhPos, names, 0, names.Length);
                        int sc = ((cf & 0x80) == 0 ? 4 : 0) + ((cf & 0x100) == 0 ? 2 : 0) + ((locale & 0x2) != 0 ? 1 : 0);
                        for (int i = 0; i < nr; i++)
                        {
                            var h = BitConverter.ToUInt64(names, i * 8);
                            if (!wanted.Contains(h) || (score.TryGetValue(h, out var old) && old >= sc)) continue;
                            var one = new byte[16];
                            root.ReadAt(ckPos + (long)i * 16, one, 0, 16);
                            res[h] = one; score[h] = sc;
                        }
                    }
                    pos = nhPos + (hasNames ? (long)nr * 8 : 0);
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

        // the same into a buffer the caller keeps (grown when too small): for many files read one after the other (the area probe's terrain tiles), so
        // each one does not become another large array for the garbage collector. Returns the file's length, -1 when the storage does not hold it.
        public int ReadByContentKey(byte[] ckey, ref byte[] buffer)
        {
            var ek = EncodingKeyFor(ckey);
            if (ek == null || !TryFind(ek, out _)) return -1;
            using (var f = OpenEKey(ek))
            {
                if (f.Length > int.MaxValue) throw new IOException("file too large");
                int n = (int)f.Length;
                if (buffer == null || buffer.Length < n) buffer = new byte[Math.Max(n, (buffer?.Length ?? 0) * 3 / 2)];
                f.ReadAt(0, buffer, 0, n);
                return n;
            }
        }

        // a part of the decoded file (only the blocks that hold it are decoded): the header and a small mip level of a texture, say. Null = not in the local storage.
        public byte[] ReadRangeByContentKey(byte[] ckey, long offset, int count)
        {
            var ek = EncodingKeyFor(ckey);
            if (ek == null || !TryFind(ek, out _)) return null;
            using (var f = OpenEKey(ek))
            {
                if (offset < 0 || offset >= f.Length) return null;
                int n = (int)Math.Min((long)count, f.Length - offset);
                var buf = new byte[n];
                f.ReadAt(offset, buf, 0, n);
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
            disposed = true;
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

        readonly bool zeroFill;

        public BlteFile(FileStream src, long start, int size, bool zeroFillEncrypted = false)
        {
            this.src = src; this.start = start; zeroFill = zeroFillEncrypted;
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
            lock (src) { src.Position = pos; CascStorage.ReadFull(src, buf, off, count); }   // the archive stream is shared by every file read from it (and by threads using one storage)
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
                    if (decSize[i] > 0) { dec = new byte[decSize[i]]; Inflate(raw, dec, 0, dec.Length); break; }
                    using (var ms = new MemoryStream(raw, 3, raw.Length - 3)) // 1 type byte + 2 byte zlib header
                    using (var ds = new DeflateStream(ms, CompressionMode.Decompress))
                    using (var o = new MemoryStream(4096))
                    {
                        ds.CopyTo(o);
                        dec = o.ToArray();
                    }
                    break;
                case 'E' when zeroFill: dec = new byte[decSize[i] > 0 ? decSize[i] : 0]; break;
                default: throw new NotSupportedException("BLTE block type '" + (char)raw[0] + "' (encrypted or unknown) is not supported");
            }
            cache[i] = dec; order.Enqueue(i);
            while (order.Count > 6) cache.Remove(order.Dequeue());
            return dec;
        }

        static void Inflate(byte[] raw, byte[] dst, int off, int count)
        {
            using (var ms = new MemoryStream(raw, 3, raw.Length - 3)) // 1 type byte + 2 byte zlib header
            using (var ds = new DeflateStream(ms, CompressionMode.Decompress))
            {
                int got = 0;
                while (got < count) { int r = ds.Read(dst, off + got, count - got); if (r <= 0) break; got += r; }
                if (got < count) throw new EndOfStreamException("BLTE block decoded short");
            }
        }

        // a whole block that is wanted whole (reading a file from start to end): decoded straight into the caller's buffer, not kept
        bool TryDecodeInto(int i, byte[] buf, int off)
        {
            if (decSize[i] <= 0 || cache.ContainsKey(i)) return false;
            var raw = new byte[rawSize[i]];
            Read(rawPos[i], raw, 0, raw.Length);
            switch ((char)raw[0])
            {
                case 'N': if (raw.Length - 1 != decSize[i]) return false; Buffer.BlockCopy(raw, 1, buf, off, decSize[i]); return true;
                case 'Z': Inflate(raw, buf, off, decSize[i]); return true;
                case 'E' when zeroFill: Array.Clear(buf, off, decSize[i]); return true;
                default: return false;
            }
        }

        public void ReadAt(long pos, byte[] buf, int off, int count)
        {
            lock (gate) ReadAtLocked(pos, buf, off, count);   // the block cache is not shared between threads (the encoding file of a shared storage is)
        }

        void ReadAtLocked(long pos, byte[] buf, int off, int count)
        {
            if (pos < 0 || pos + count > Length) throw new EndOfStreamException();
            // first block that holds pos
            int lo = 0, hi = decStart.Length - 1, bi = 0;
            while (lo <= hi) { int mid = (lo + hi) >> 1; if (decStart[mid] <= pos) { bi = mid; lo = mid + 1; } else hi = mid - 1; }
            while (count > 0)
            {
                if (pos == decStart[bi] && count >= decSize[bi] && decSize[bi] > 0 && TryDecodeInto(bi, buf, off))
                {
                    int whole = decSize[bi];
                    pos += whole; off += whole; count -= whole; bi++;
                    continue;
                }
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
