using System;
using System.Collections.Generic;

namespace ElansAddonHub.Services.WorldMap
{
    // ============================================================================================================================
    // The low-resolution height map of a continent (WDL): what the client itself draws in the far distance. One small file per continent
    // (Eastern Kingdoms 0.9 MB, Kalimdor 1.2 MB; its FileDataID is in the WDT's MPHD, WdtFile.WdlFdid). Written from wowdev.wiki/WDL and checked
    // on the real files of build 1.60.1: chunks with reversed tags (MVER, MWMO/MWID/MODF, MAOF, then per tile MARE + MAHO)
    //   MAOF  = uint32 offsets[64 x 64] to the tile's MARE chunk, index row * 64 + col (0 = no tile; Kalimdor's stray corner block is index 0..)
    //   MARE  = int16 outer[17 x 17] then inner[16 x 16] heights in yards; outer[r][c] lies r x 33.33 yards south and c x 33.33 yards east of the
    //           tile's north-west corner (the same orientation as an ADT's MCVT and our TerrainTile grid)
    // Used when a tile's own terrain file (the root ADT) cannot be read yet - a Battle.net install that is still downloading in the background:
    // the tile is drawn at this low detail instead of being a hole, and the real file is read again later (WorldMapTerrain, the ".low" markers).
    // ============================================================================================================================
    public sealed class WdlFile
    {
        public const int Outer = 17, Inner = 16, MareBytes = (Outer * Outer + Inner * Inner) * 2;
        readonly Dictionary<int, short[]> tiles = new Dictionary<int, short[]>();
        public string Problem;
        public bool Ok => Problem == null;
        public int Count => tiles.Count;

        public bool Has(int col, int row) => tiles.ContainsKey(row * 64 + col);

        // the 17 x 17 outer heights of a tile (null = the file has none for it)
        public short[] OuterOf(int col, int row) => tiles.TryGetValue(row * 64 + col, out var h) ? h : null;

        static string Tag(byte[] d, int p) { var a = new[] { (char)d[p + 3], (char)d[p + 2], (char)d[p + 1], (char)d[p] }; return new string(a); }

        public static WdlFile Parse(byte[] d)
        {
            var w = new WdlFile();
            if (d == null || d.Length < 16) { w.Problem = "the low-resolution height map is empty"; return w; }
            int maof = -1;
            for (int p = 0; p + 8 <= d.Length;)
            {
                int size = BitConverter.ToInt32(d, p + 4);
                if (size < 0 || (long)p + 8 + size > d.Length) break;
                var tag = Tag(d, p);
                if (tag == "MAOF" || new string(new[] { (char)d[p], (char)d[p + 1], (char)d[p + 2], (char)d[p + 3] }) == "MAOF") { if (size == 64 * 64 * 4) maof = p + 8; }
                p += 8 + size;
            }
            if (maof < 0) { w.Problem = "the low-resolution height map has no MAOF table"; return w; }
            for (int i = 0; i < 4096; i++)
            {
                uint o = BitConverter.ToUInt32(d, maof + i * 4);
                if (o == 0 || o + 8L + MareBytes > d.Length) continue;
                var t = Tag(d, (int)o); var straight = new string(new[] { (char)d[o], (char)d[o + 1], (char)d[o + 2], (char)d[o + 3] });
                if (t != "MARE" && straight != "MARE") continue;
                if (BitConverter.ToInt32(d, (int)o + 4) < MareBytes) continue;
                var h = new short[Outer * Outer];
                for (int k = 0; k < h.Length; k++) h[k] = BitConverter.ToInt16(d, (int)o + 8 + k * 2);
                w.tiles[i] = h;
            }
            if (w.tiles.Count == 0) w.Problem = "the low-resolution height map has no tiles";
            return w;
        }

        // the tile as a TerrainTile grid (65 x 65, bilinear between the 17 x 17 outer heights; no area ids): null when the file has no heights for it
        public TerrainTile Tile(int col, int row)
        {
            var o = OuterOf(col, row);
            if (o == null) return null;
            var t = new TerrainTile { Col = col, Row = row };
            var z = new float[TerrainTile.Samples * TerrainTile.Samples];
            float min = float.MaxValue, max = float.MinValue;
            for (int r = 0; r < TerrainTile.Samples; r++)
                for (int c = 0; c < TerrainTile.Samples; c++)
                {
                    double fy = r / 4.0, fx = c / 4.0;
                    int y0 = Math.Min(Outer - 2, (int)fy), x0 = Math.Min(Outer - 2, (int)fx);
                    double ty = fy - y0, tx = fx - x0;
                    double a = o[y0 * Outer + x0], b = o[y0 * Outer + x0 + 1], cc = o[(y0 + 1) * Outer + x0], dd = o[(y0 + 1) * Outer + x0 + 1];
                    var v = (float)((a * (1 - tx) + b * tx) * (1 - ty) + (cc * (1 - tx) + dd * tx) * ty);
                    z[r * TerrainTile.Samples + c] = v;
                    if (v < min) min = v; if (v > max) max = v;
                }
            t.MinZ = min; t.MaxZ = max;
            float range = max - min;
            for (int i = 0; i < z.Length; i++) t.H[i] = range <= 0 ? (ushort)0 : (ushort)Math.Max(0, Math.Min(65535, Math.Round((z[i] - min) / range * 65535)));
            t.MissingChunks = TerrainTile.Chunks * TerrainTile.Chunks;      // no area ids: every chunk's area is unknown
            return t;
        }

        // a flat tile at one height (no WDL either): the 3D view draws a hatched placeholder there instead of a hole (see view3d.js "holes")
        public static TerrainTile Flat(int col, int row, float z) => new TerrainTile { Col = col, Row = row, MinZ = z, MaxZ = z, MissingChunks = TerrainTile.Chunks * TerrainTile.Chunks };
    }
}
