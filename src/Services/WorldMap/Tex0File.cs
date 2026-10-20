using System;
using System.Collections.Generic;

namespace ElansAddonHub.Services.WorldMap
{
    public sealed class TexLayer
    {
        public int Texture;             // index into Tex0File.Diffuse
        public uint Flags;              // MCLY flags: 0x100 uses an alpha map, 0x200 the alpha map is compressed
        public int AlphaOffset;
        public byte[] Alpha;            // 64 x 64, row-major north -> south, west -> east (null for layer 0 or a layer without alpha)
    }

    public sealed class TexChunk
    {
        public List<TexLayer> Layers = new List<TexLayer>();
    }

    // ============================================================================================================================
    // The texture ADT (_tex0) of a map tile, read for the 3D world map's real ground (3.2). Ported from Elan's Lab (checked there on the real files of build
    // 1.60.1: RLE and 8-bit alpha maps on both continents, no 4-bit ones, no MTXP). Written from wowdev.wiki/ADT/v18; tags reversed on disk as in every ADT:
    //   MDID  uint32 per texture: the diffuse texture's FileDataID          MHID  height textures (not used)
    //   MCNK  x 256, no header in this file (order iy * 16 + ix), sub-chunks:
    //     MCLY  16 bytes per layer (at most 4): texture index, flags, offset into MCAL, effect id
    //     MCAL  the alpha maps of layers 1..3, 64 x 64 each: RLE (flag 0x200: a byte = fill bit 0x80 + count, then one value repeated or count values),
    //           8 bits per value ("big alpha": WDT MPHD flag 0x4 or 0x80), else 4 bits (low nibble first) where, unless the root MCNK has flag 0x8000,
    //           the last column and row repeat the ones before ("fix alpha")
    // And from the root ADT: the MCNK flags and the MCCV vertex colours (145 BGRA entries per chunk: 9 outer rows of 9 and 8 inner rows of 8, interleaved).
    // ============================================================================================================================
    public sealed class Tex0File
    {
        public List<int> Diffuse = new List<int>();
        public TexChunk[] Chunks = new TexChunk[256];
        public int AlphaRle, Alpha8, Alpha4;
        public string Problem;

        static bool Is(byte[] d, int p, string want)
        {
            bool fwd = true, rev = true;
            for (int i = 0; i < 4; i++) { if (d[p + i] != want[i]) fwd = false; if (d[p + 3 - i] != want[i]) rev = false; }
            return fwd || rev;
        }

        public static Tex0File Parse(byte[] d, bool bigAlpha, uint[] rootFlags)
        {
            var f = new Tex0File();
            if (d == null || d.Length < 16) { f.Problem = "the texture file is empty"; return f; }
            int chunk = 0;
            for (int p = 0; p + 8 <= d.Length;)
            {
                int size = BitConverter.ToInt32(d, p + 4);
                if (size < 0 || (long)p + 8 + size > d.Length) break;
                int s = p + 8;
                if (Is(d, p, "MDID")) { for (int i = 0; i + 4 <= size; i += 4) f.Diffuse.Add(BitConverter.ToInt32(d, s + i)); }
                else if (Is(d, p, "MCNK"))
                {
                    if (chunk < 256) f.Chunks[chunk] = ReadChunk(f, d, s, size, bigAlpha, rootFlags != null && (rootFlags[chunk] & 0x8000) != 0);
                    chunk++;
                }
                p += 8 + size;
            }
            if (chunk == 0) f.Problem = "the texture file has no chunks";
            return f;
        }

        static TexChunk ReadChunk(Tex0File f, byte[] d, int s, int size, bool bigAlpha, bool doNotFix)
        {
            var c = new TexChunk();
            int end = s + size, mcal = -1, mcalSize = 0;
            for (int p = s; p + 8 <= end;)
            {
                int sz = BitConverter.ToInt32(d, p + 4);
                if (sz < 0 || (long)p + 8 + sz > end) break;
                if (Is(d, p, "MCLY"))
                    for (int i = 0; i + 16 <= sz && c.Layers.Count < 4; i += 16)
                        c.Layers.Add(new TexLayer { Texture = BitConverter.ToInt32(d, p + 8 + i), Flags = BitConverter.ToUInt32(d, p + 12 + i), AlphaOffset = BitConverter.ToInt32(d, p + 16 + i) });
                else if (Is(d, p, "MCAL")) { mcal = p + 8; mcalSize = sz; }
                p += 8 + sz;
            }
            for (int li = 1; li < c.Layers.Count; li++)
            {
                var L = c.Layers[li];
                if ((L.Flags & 0x100) == 0 || mcal < 0 || L.AlphaOffset < 0 || L.AlphaOffset >= mcalSize) continue;
                int a = mcal + L.AlphaOffset, aend = mcal + mcalSize;
                var al = new byte[4096];
                if ((L.Flags & 0x200) != 0)
                {
                    int o = 0;
                    while (o < 4096 && a < aend)
                    {
                        int b = d[a++], n = b & 0x7F;
                        if ((b & 0x80) != 0) { if (a >= aend) break; byte v = d[a++]; for (int k = 0; k < n && o < 4096; k++) al[o++] = v; }
                        else for (int k = 0; k < n && o < 4096 && a < aend; k++) al[o++] = d[a++];
                    }
                    f.AlphaRle++;
                }
                else if (bigAlpha) { Buffer.BlockCopy(d, a, al, 0, Math.Min(4096, aend - a)); f.Alpha8++; }
                else
                {
                    for (int i = 0; i < 2048 && a + i < aend; i++) { int b = d[a + i]; al[i * 2] = (byte)((b & 0xF) * 17); al[i * 2 + 1] = (byte)((b >> 4) * 17); }
                    if (!doNotFix) Fix(al);
                    f.Alpha4++;
                }
                L.Alpha = al;
            }
            return c;
        }

        // the 63 x 63 quirk: the last column and row repeat the ones before them
        static void Fix(byte[] m)
        {
            for (int y = 0; y < 64; y++) m[y * 64 + 63] = m[y * 64 + 62];
            for (int x = 0; x < 64; x++) m[63 * 64 + x] = m[62 * 64 + x];
        }

        // the root ADT's MCNK flags (per chunk, iy * 16 + ix) and MCCV colours (145 x BGRA, null = none)
        public static uint[] RootChunks(byte[] d, out byte[][] mccv)
        {
            var flags = new uint[256]; mccv = new byte[256][];
            if (d == null) return flags;
            int chunk = 0;
            for (int p = 0; p + 8 <= d.Length;)
            {
                int size = BitConverter.ToInt32(d, p + 4);
                if (size < 0 || (long)p + 8 + size > d.Length) break;
                if (Is(d, p, "MCNK") && size >= 128)
                {
                    int s = p + 8, ix = BitConverter.ToInt32(d, s + 4), iy = BitConverter.ToInt32(d, s + 8);
                    int k = ix >= 0 && ix < 16 && iy >= 0 && iy < 16 ? iy * 16 + ix : chunk;
                    if (k < 256)
                    {
                        flags[k] = BitConverter.ToUInt32(d, s);
                        for (int q = s + 128; q + 8 <= s + size;)
                        {
                            int sz = BitConverter.ToInt32(d, q + 4);
                            if (sz < 0 || (long)q + 8 + sz > s + size) break;
                            if (Is(d, q, "MCCV") && sz >= 145 * 4) { var c = new byte[145 * 4]; Buffer.BlockCopy(d, q + 8, c, 0, c.Length); mccv[k] = c; }
                            q += 8 + sz;
                        }
                    }
                    chunk++;
                }
                p += 8 + size;
            }
            return flags;
        }
    }
}
