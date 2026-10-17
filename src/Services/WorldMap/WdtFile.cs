using System;
using System.Collections.Generic;
using System.Linq;

namespace ElansAddonHub.Services.WorldMap
{
    // A world table (WDT) of a continent: which of the 64 x 64 map tiles exist and the FileDataIDs of their files. Written from wowdev.wiki/WDT:
    // a sequence of chunks (4-byte tag, 4-byte size, data). The tags are stored reversed on disk ("DIAM" for MAID). The chunk that matters is
    //   MAID  = 4096 x MapFileDataIDs { rootADT, obj0ADT, obj1ADT, tex0ADT, lodADT, mapTexture, mapTextureN, minimapTexture } (8 x uint32, 32 bytes),
    //           entry index = row * 64 + col (a tile without files has all zeros)
    // MAIN (flags, 8 bytes x 4096) is read for the "has an ADT" bit only as a cross-check. Verified on the real Eastern Kingdoms (736 tiles) and
    // Kalimdor (988 tiles) tables of build 1.60.1.70338.
    // A map made of one building (most dungeons: the Stockade, Molten Core, Blackrock Depths ...) has no tiles: MPHD's flag 0x1 (uses a global map object) is set
    // and one MODF record (the 64-byte placement of the ADT's MODF, its nameId the WMO's FileDataID) names the building. Its position is (0, 0, 0) on the
    // files of build 1.60.1.70338 and the model's own axes are the world's turned half way round: world (x, y, z) = (-model x, -model y, model z) - checked
    // against where the Classic baseline puts the creatures of Molten Core, Blackfathom Deeps, Onyxia's Lair and Blackrock Depths (all inside the turned box,
    // none inside the unturned one). GlobalWmo carries it with the position moved by half the world on both ground axes, so the placement transform of the
    // ADT objects (WmoTransform.ToScene, then world x = 17066.67 - scene z, y = 17066.67 - scene x) gives exactly that.
    public sealed class WdtTile { public int Col, Row; public int RootFdid, MinimapFdid, Obj0Fdid; }

    public sealed class WdtFile
    {
        public const int MaidEntry = 32, Entries = 4096;
        public List<WdtTile> Tiles = new List<WdtTile>();
        public int MainTiles = -1;             // tiles MAIN says have an ADT (-1 = no MAIN)
        public uint MphdFlags;                 // MPHD's flags (0x1 = the map is one global building)
        // MPHD (32 bytes since 8.1): flags, lgt, occ, fogs, mpv, tex, wdl, pd4 FileDataIDs. The WDL (the low-resolution heights the client draws in the
        // distance) is at +24: verified on the real Eastern Kingdoms (775971 -> WDL 775970, 736 MARE tiles = the WDT's 736 tiles) and Kalimdor (782779 -> 782778, 988).
        public int WdlFdid;
        public WmoPlacement GlobalWmo;         // the building of a map made of one (null = a map of tiles)
        public const float HalfWorld = 17066.666f;
        public string Problem;                 // set = nothing usable
        public bool Ok => Problem == null;
        public int WithMinimap => Tiles.Count(t => t.MinimapFdid != 0);

        static string Tag(byte[] d, int p)
        {
            var a = new[] { (char)d[p], (char)d[p + 1], (char)d[p + 2], (char)d[p + 3] };
            var s = new string(a);
            if (s == "MAID" || s == "MAIN" || s == "MPHD" || s == "MODF") return s;
            return new string(Enumerable.Reverse(a).ToArray());
        }

        public static WdtFile Parse(byte[] d)
        {
            var w = new WdtFile();
            if (d == null || d.Length < 16) { w.Problem = "the world table is empty or cut off"; return w; }
            int maid = -1, main = -1, maidSize = 0, mainSize = 0, modf = -1, modfSize = 0;
            string cut = null;
            for (int p = 0; p + 8 <= d.Length;)
            {
                int size = BitConverter.ToInt32(d, p + 4);
                if (size < 0 || (long)p + 8 + size > d.Length) { cut = "the world table is cut off"; break; }
                var tag = Tag(d, p);
                if (tag == "MAID") { maid = p + 8; maidSize = size; }
                else if (tag == "MAIN") { main = p + 8; mainSize = size; }
                else if (tag == "MPHD" && size >= 4)
                {
                    w.MphdFlags = BitConverter.ToUInt32(d, p + 8);
                    if (size >= 28) { uint wdl = BitConverter.ToUInt32(d, p + 8 + 24); w.WdlFdid = wdl > 0 && wdl <= int.MaxValue ? (int)wdl : 0; }
                }
                else if (tag == "MODF") { modf = p + 8; modfSize = size; }
                p += 8 + size;
            }
            if (modf >= 0 && modfSize >= AdtObjects.ModfRecord)
            {
                var g = new WmoPlacement
                {
                    NameId = BitConverter.ToUInt32(d, modf), UniqueId = BitConverter.ToUInt32(d, modf + 4),
                    X = BitConverter.ToSingle(d, modf + 8) + HalfWorld, Y = BitConverter.ToSingle(d, modf + 12), Z = BitConverter.ToSingle(d, modf + 16) + HalfWorld,
                    RotX = BitConverter.ToSingle(d, modf + 20), RotY = BitConverter.ToSingle(d, modf + 24), RotZ = BitConverter.ToSingle(d, modf + 28),
                    Flags = BitConverter.ToUInt16(d, modf + 56), DoodadSet = BitConverter.ToUInt16(d, modf + 58), NameSet = BitConverter.ToUInt16(d, modf + 60),
                };
                int sc = BitConverter.ToUInt16(d, modf + 62);
                g.Scale = sc == 0 ? 1f : sc / 1024f;
                if (g.NameId != 0 && g.NameId <= int.MaxValue && !float.IsNaN(g.X) && !float.IsNaN(g.Z) && g.Scale > 0.01f && g.Scale < 100f) w.GlobalWmo = g;
            }
            if (maid < 0) { w.Problem = cut ?? "this world table has no MAID chunk (the file ids of its tiles)"; return w; }
            if (maidSize != Entries * MaidEntry) { w.Problem = "MAID has an unexpected size (" + maidSize + " bytes)"; return w; }
            if (main >= 0 && mainSize == Entries * 8)
            {
                int n = 0;
                for (int i = 0; i < Entries; i++) if ((BitConverter.ToUInt32(d, main + i * 8) & 1) != 0) n++;
                w.MainTiles = n;
            }
            for (int i = 0; i < Entries; i++)
            {
                int o = maid + i * MaidEntry;
                uint root = BitConverter.ToUInt32(d, o), obj0 = BitConverter.ToUInt32(d, o + 4), mm = BitConverter.ToUInt32(d, o + 28);
                if (root == 0 && mm == 0) continue;
                if (mm > int.MaxValue || root > int.MaxValue) continue;
                w.Tiles.Add(new WdtTile { Col = i % 64, Row = i / 64, RootFdid = (int)root, MinimapFdid = (int)mm, Obj0Fdid = obj0 > int.MaxValue ? 0 : (int)obj0 });
            }
            return w;
        }

        // Strays: a small group of tiles (fewer than `minCluster`, 8-connected) far (more than `farTiles` tiles) from the biggest land mass. Some continents
        // carry test leftovers like that in a far corner (Kalimdor has a 3 x 3 block at the top-left corner of the grid); they would stretch the map's
        // bounds over empty sea. Small islands NEAR the main land (Darkspear Isles ...) are kept.
        public static List<WdtTile> WithoutStrays(IList<WdtTile> tiles, int minCluster = 12, List<WdtTile> removed = null, int farTiles = 6)
        {
            var key = new Dictionary<int, WdtTile>();
            foreach (var t in tiles) key[t.Row * 64 + t.Col] = t;
            var seen = new HashSet<int>();
            var comps = new List<List<WdtTile>>();
            foreach (var t in tiles.OrderBy(t => t.Row).ThenBy(t => t.Col))
            {
                int k0 = t.Row * 64 + t.Col;
                if (seen.Contains(k0)) continue;
                var comp = new List<WdtTile>(); var q = new Queue<int>();
                q.Enqueue(k0); seen.Add(k0);
                while (q.Count > 0)
                {
                    int k = q.Dequeue(); comp.Add(key[k]);
                    int c = k % 64, r = k / 64;
                    for (int dr = -1; dr <= 1; dr++)
                        for (int dc = -1; dc <= 1; dc++)
                        {
                            int nc = c + dc, nr = r + dr;
                            if ((dc == 0 && dr == 0) || nc < 0 || nc > 63 || nr < 0 || nr > 63) continue;
                            int nk = nr * 64 + nc;
                            if (key.ContainsKey(nk) && seen.Add(nk)) q.Enqueue(nk);
                        }
                }
                comps.Add(comp);
            }
            if (comps.Count <= 1) return tiles.OrderBy(t => t.Row).ThenBy(t => t.Col).ToList();
            var main = comps.OrderByDescending(c => c.Count).First();
            var keep = new List<WdtTile>();
            foreach (var comp in comps)
            {
                bool stray = comp.Count < minCluster && !ReferenceEquals(comp, main)
                    && comp.All(t => main.All(m => Math.Max(Math.Abs(m.Col - t.Col), Math.Abs(m.Row - t.Row)) > farTiles));
                if (stray) removed?.AddRange(comp); else keep.AddRange(comp);
            }
            return keep.OrderBy(t => t.Row).ThenBy(t => t.Col).ToList();
        }
    }
}
