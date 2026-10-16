using System;
using System.Collections.Generic;

namespace ElansAddonHub.Services.WorldMap
{
    // One terrain chunk (MCNK) of a root ADT: 33.33 yards square, the 16 x 16 of them make one map tile.
    public sealed class AdtChunk
    {
        public int IndexX, IndexY;          // column / row of the chunk in the tile (see AdtFile for the orientation)
        public uint Flags;
        public int AreaId;                  // AreaTable id of the chunk
        public float PosX, PosY, PosZ;      // MCNK.position: the chunk's corner in world coordinates, z = the base the heights are relative to
        public float[] Heights;             // MCVT: 145 floats (9 x 9 outer + 8 x 8 inner vertices), relative to PosZ; null = the chunk has none
        public ulong Holes;                 // the chunk's 8 x 8 cells that have no ground: bit (row * 8 + col), row north to south, col west to east (see AdtFile)

        public bool IsHole(int col, int row) => (Holes & (1UL << (row * 8 + col))) != 0;
    }

    // One layer of liquid in one terrain chunk (an MH2O instance): a rectangle of the chunk's 8 x 8 cells with a liquid type, an exists mask and heights.
    public sealed class AdtLiquid
    {
        public int Chunk;                   // IndexY * 16 + IndexX of the terrain chunk it lies in
        public int LiquidType;              // LiquidType.db2 id
        public int Format;                  // the vertex layout: 0 height + depth, 1 height + uv, 2 depth only, 3 height + uv + depth; -1 = none (flat at MinHeight)
        public float MinHeight, MaxHeight;
        public int X, Y, W, H;              // the rectangle in the chunk's cell grid (x along the chunk's columns, west to east; y along its rows, north to south)
        public ulong Mask;                  // bit (cy * 8 + cx): the cell (cx, cy) of the chunk has liquid
        public float[] Heights;             // 9 x 9 vertex heights (row-major, [vy * 9 + vx]); outside the rectangle the nearest vertex's; null = flat at MinHeight
    }

    // ============================================================================================================================
    // The root ADT of a map tile (terrain), read for the heights and the area ids of its chunks. Written from wowdev.wiki/ADT/v18 and checked on the
    // real files of build 1.60.1.70338: a sequence of chunks (4-byte tag, 4-byte size, data), the tags stored reversed on disk ("KNCM" for MCNK, as
    // the WDT's). The ones that matter:
    //   MCNK  x 256   a 128-byte header (flags, IndexX, IndexY, ... areaid at 0x34, position float[3] at 0x68) followed by sub-chunks. Holes (cells with no
    //                 ground, cave mouths): with flag 0x10000 (high_res_holes) the 8 bytes at 0x14 are an 8 x 8 mask, one byte per row of cells, bit = column;
    //                 without it the uint16 at 0x3C is a 4 x 4 mask, bit (row / 2 * 4 + col / 2), each bit a 2 x 2 block of cells
    //     MCVT        145 floats: 17 rows alternately 9 outer then 8 inner vertices; z = position.z + value
    //   MH2O          the liquids of the whole tile: 256 x {offset to instances, layer count, offset to attributes} (12 bytes, chunk order = MCNK order),
    //                 then per instance (24 bytes): ushort liquid type, ushort liquid object / vertex format, float min, float max, byte x, y, width, height,
    //                 uint exists-bitmap offset (0 = every cell of the rectangle), uint vertex-data offset (0 = flat at min). All offsets are from the start of the
    //                 MH2O data. Verified on build 1.60.1.70338: the second ushort is a LiquidObject id (> 41) there, not the old 0-3 format, so the vertex layout
    //                 is told by the size of the block (5 bytes per vertex = float height + byte depth, 8 = height + uv, 1 = depth only, 9 = all three); the bitmap is
    //                 LSB first, row-major over width x height, x along MCVT's columns and y along its rows (checked against the terrain: 89 % of the cells of
    //                 partly flooded chunks agree with "terrain below the water" in this orientation, 72 % the other way round).
    // The newer split files keep MCVT in the root file (the textures live in the _tex0 file), so the root file alone gives heights and areas.
    // ============================================================================================================================
    public sealed class AdtFile
    {
        public const int ChunksPerSide = 16, HeightCount = 145;
        public AdtChunk[] Chunks = new AdtChunk[256];       // index = IndexY * 16 + IndexX
        public List<AdtLiquid> Liquids = new List<AdtLiquid>();   // MH2O layers, in chunk order
        public int Found;                                   // chunks read
        public int WithHeights;
        public string Problem;
        public bool Ok => Problem == null;

        // is the tag at p `want`? On disk the letters are reversed ("KNCM"); the right way round is accepted too
        static bool Is(byte[] d, int p, string want)
        {
            bool fwd = true, rev = true;
            for (int i = 0; i < 4; i++) { if (d[p + i] != want[i]) fwd = false; if (d[p + 3 - i] != want[i]) rev = false; }
            return fwd || rev;
        }

        public static AdtFile Parse(byte[] d) => Parse(d, d?.Length ?? 0);

        // the first length bytes of d (a buffer kept between files)
        public static AdtFile Parse(byte[] d, int length)
        {
            var f = new AdtFile();
            if (d == null || length < 16 || length > d.Length) { f.Problem = "the terrain file is empty or cut off"; return f; }
            for (int p = 0; p + 8 <= length;)
            {
                int size = BitConverter.ToInt32(d, p + 4);
                if (size < 0 || (long)p + 8 + size > length) break;       // cut off: what was read stays
                if (Is(d, p, "MCNK")) ReadChunk(f, d, p + 8, size);
                else if (Is(d, p, "MH2O")) { try { ReadLiquids(f, d, p + 8, size); } catch (Exception) { } }
                p += 8 + size;
            }
            if (f.Found == 0) f.Problem = "this terrain file has no MCNK chunks";
            return f;
        }

        // Only the area id of every chunk (what Parse(d).Chunks[i].AreaId gives, 0 for a chunk that is not there), for the first `length` bytes of d;
        // null when the file has no chunk (Parse's Problem). No heights, holes or water are read: the area probe needs nothing else.
        public static int[] AreaIds(byte[] d, int length)
        {
            if (d == null || length < 16 || length > d.Length) return null;
            var a = new int[256];
            int found = 0;
            for (int p = 0; p + 8 <= length;)
            {
                int size = BitConverter.ToInt32(d, p + 4);
                if (size < 0 || (long)p + 8 + size > length) break;
                if (Is(d, p, "MCNK") && size >= 128)
                {
                    int s = p + 8, ix = BitConverter.ToInt32(d, s + 4), iy = BitConverter.ToInt32(d, s + 8);
                    if (ix >= 0 && ix <= 15 && iy >= 0 && iy <= 15) { a[iy * 16 + ix] = Math.Max(0, BitConverter.ToInt32(d, s + 0x34)); found++; }
                }
                p += 8 + size;
            }
            return found == 0 ? null : a;
        }

        // vertex bytes per format: 0 = float + byte, 1 = float + 2 x ushort, 2 = byte, 3 = float + 2 x ushort + byte
        static readonly int[] FormatBytes = { 5, 8, 1, 9 };

        public static void ReadLiquids(AdtFile f, byte[] d, int s, int size)
        {
            if (size < 256 * 12) return;
            int end = s + size;
            // every offset the block contains, so the size of a vertex block is the gap to the next one
            var starts = new List<int> { size };
            for (int c = 0; c < 256; c++)
            {
                uint off = BitConverter.ToUInt32(d, s + c * 12), cnt = BitConverter.ToUInt32(d, s + c * 12 + 4), attr = BitConverter.ToUInt32(d, s + c * 12 + 8);
                if (cnt == 0 || cnt > 16 || off == 0 || (long)off + cnt * 24 > size) continue;
                if (attr != 0 && attr < size) starts.Add((int)attr);
                starts.Add((int)off);
                for (uint l = 0; l < cnt; l++)
                {
                    int q = s + (int)off + (int)l * 24;
                    uint eo = BitConverter.ToUInt32(d, q + 16), vo = BitConverter.ToUInt32(d, q + 20);
                    if (eo != 0 && eo < size) starts.Add((int)eo);
                    if (vo != 0 && vo < size) starts.Add((int)vo);
                }
            }
            starts.Sort();
            for (int c = 0; c < 256; c++)
            {
                uint off = BitConverter.ToUInt32(d, s + c * 12), cnt = BitConverter.ToUInt32(d, s + c * 12 + 4);
                if (cnt == 0 || cnt > 16 || off == 0 || (long)off + cnt * 24 > size) continue;
                for (uint l = 0; l < cnt; l++)
                {
                    int q = s + (int)off + (int)l * 24;
                    var L = new AdtLiquid
                    {
                        Chunk = c, LiquidType = BitConverter.ToUInt16(d, q), MinHeight = BitConverter.ToSingle(d, q + 4), MaxHeight = BitConverter.ToSingle(d, q + 8),
                        X = d[q + 12], Y = d[q + 13], W = d[q + 14], H = d[q + 15],
                    };
                    int fmt = BitConverter.ToUInt16(d, q + 2);
                    uint eo = BitConverter.ToUInt32(d, q + 16), vo = BitConverter.ToUInt32(d, q + 20);
                    if (L.W == 0 || L.H == 0 || L.X + L.W > 8 || L.Y + L.H > 8) continue;
                    if (float.IsNaN(L.MinHeight) || float.IsNaN(L.MaxHeight) || Math.Abs(L.MinHeight) > 100000 || Math.Abs(L.MaxHeight) > 100000) continue;
                    // the exists mask in the chunk's 8 x 8 grid (LSB first, row-major over the rectangle)
                    for (int y = 0; y < L.H; y++)
                        for (int x = 0; x < L.W; x++)
                        {
                            bool on = true;
                            if (eo != 0)
                            {
                                int bi = y * L.W + x; long at = s + (long)eo + bi / 8;
                                on = at < end && ((d[at] >> (bi % 8)) & 1) != 0;
                            }
                            if (on) L.Mask |= 1UL << ((L.Y + y) * 8 + L.X + x);
                        }
                    if (L.Mask == 0) continue;
                    // the vertex layout: 0..3 as the old format field says; a bigger number is a LiquidObject id, and the size of the block tells
                    int nv = (L.W + 1) * (L.H + 1), format = -1;
                    if (vo != 0 && vo < size)
                    {
                        int next = size; foreach (var st in starts) if (st > (int)vo) { next = st; break; }
                        int span = next - (int)vo;
                        if (fmt <= 3 && span >= nv * FormatBytes[fmt]) format = fmt;
                        else
                        {
                            for (int k = 0; k < 4 && format < 0; k++) if (span == nv * FormatBytes[k]) format = k;                              // the block is exactly one layout's size
                            for (int k = 0; k < 4 && format < 0; k++) if (span > nv * FormatBytes[k] && span < nv * FormatBytes[k] + 8) format = k;   // or that plus padding
                        }
                        if (format < 0 && fmt > 3 && span >= nv * 5) format = 0;      // an unknown block that is big enough: the commonest layout
                    }
                    L.Format = format;
                    if (format == 0 || format == 1 || format == 3)
                    {
                        var hts = new float[81]; bool ok = true;
                        for (int vy = 0; vy < 9 && ok; vy++)
                            for (int vx = 0; vx < 9; vx++)
                            {
                                int ry = Math.Min(Math.Max(vy - L.Y, 0), L.H), rx = Math.Min(Math.Max(vx - L.X, 0), L.W);
                                long at = s + (long)vo + (ry * (L.W + 1) + rx) * 4;
                                if (at + 4 > end) { ok = false; break; }
                                float h = BitConverter.ToSingle(d, (int)at);
                                if (float.IsNaN(h) || Math.Abs(h) > 100000) { ok = false; break; }
                                hts[vy * 9 + vx] = h;
                            }
                        if (ok) L.Heights = hts;
                    }
                    f.Liquids.Add(L);
                }
            }
        }

        // the 4 x 4 mask of the old format as the 8 x 8 one
        public static ulong LowResHoles(ushort m)
        {
            ulong r = 0;
            for (int row = 0; row < 8; row++)
                for (int col = 0; col < 8; col++)
                    if ((m & (1 << (row / 2 * 4 + col / 2))) != 0) r |= 1UL << (row * 8 + col);
            return r;
        }

        static void ReadChunk(AdtFile f, byte[] d, int s, int size)
        {
            if (size < 128) return;
            var c = new AdtChunk
            {
                Flags = BitConverter.ToUInt32(d, s), IndexX = BitConverter.ToInt32(d, s + 4), IndexY = BitConverter.ToInt32(d, s + 8),
                AreaId = BitConverter.ToInt32(d, s + 0x34),
                PosX = BitConverter.ToSingle(d, s + 0x68), PosY = BitConverter.ToSingle(d, s + 0x6C), PosZ = BitConverter.ToSingle(d, s + 0x70),
            };
            if (c.IndexX < 0 || c.IndexX > 15 || c.IndexY < 0 || c.IndexY > 15) return;
            c.Holes = (c.Flags & 0x10000) != 0 ? BitConverter.ToUInt64(d, s + 0x14) : LowResHoles(BitConverter.ToUInt16(d, s + 0x3C));
            // sub-chunks follow the 128-byte header
            int end = s + size;
            for (int p = s + 128; p + 8 <= end;)
            {
                int sz = BitConverter.ToInt32(d, p + 4);
                if (sz < 0 || (long)p + 8 + sz > end) break;
                if (Is(d, p, "MCVT") && sz >= HeightCount * 4)
                {
                    var h = new float[HeightCount];
                    for (int i = 0; i < HeightCount; i++) h[i] = BitConverter.ToSingle(d, p + 8 + i * 4);
                    c.Heights = h;
                    break;
                }
                p += 8 + sz;
            }
            int k = c.IndexY * 16 + c.IndexX;
            if (f.Chunks[k] == null) { f.Found++; if (c.Heights != null) f.WithHeights++; }
            f.Chunks[k] = c;
        }
    }
}
