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
    }

    // ============================================================================================================================
    // The root ADT of a map tile (terrain), read for the heights and the area ids of its chunks. Written from wowdev.wiki/ADT/v18 and checked on the
    // real files of build 1.60.1.70338: a sequence of chunks (4-byte tag, 4-byte size, data), the tags stored reversed on disk ("KNCM" for MCNK, as
    // the WDT's). The ones that matter:
    //   MCNK  x 256   a 128-byte header (flags, IndexX, IndexY, ... areaid at 0x34, position float[3] at 0x68) followed by sub-chunks
    //     MCVT        145 floats: 17 rows alternately 9 outer then 8 inner vertices; z = position.z + value
    // The newer split files keep MCVT in the root file (the textures live in the _tex0 file), so the root file alone gives heights and areas.
    // ============================================================================================================================
    public sealed class AdtFile
    {
        public const int ChunksPerSide = 16, HeightCount = 145;
        public AdtChunk[] Chunks = new AdtChunk[256];       // index = IndexY * 16 + IndexX
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

        public static AdtFile Parse(byte[] d)
        {
            var f = new AdtFile();
            if (d == null || d.Length < 16) { f.Problem = "the terrain file is empty or cut off"; return f; }
            for (int p = 0; p + 8 <= d.Length;)
            {
                int size = BitConverter.ToInt32(d, p + 4);
                if (size < 0 || (long)p + 8 + size > d.Length) break;       // cut off: what was read stays
                if (Is(d, p, "MCNK")) ReadChunk(f, d, p + 8, size);
                p += 8 + size;
            }
            if (f.Found == 0) f.Problem = "this terrain file has no MCNK chunks";
            return f;
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
