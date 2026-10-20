using System;
using System.Collections.Generic;
using System.IO;

namespace ElansAddonHub.Services.WorldMap
{
    // a texture of a model for the page: the BLP's own compressed blocks (s3tc in WebGL), or plain RGBA for the rare palettized / raw ones
    //   "MTX1" | ushort width | ushort height | byte format (0 RGBA8, 1 DXT1 without alpha, 2 DXT1 with 1-bit alpha, 3 DXT3, 5 DXT5) | byte mips | ushort 0 |
    //   per mip level: int bytes, data (padded to 4). RGBA has one level (the page makes the rest); DXT has every level the BLP has from the first one that
    //   is at most MaxSize on its longer side.
    public static class ObjTexture
    {
        public const int MaxSize = 512;

        // the BLP header's mip levels: offset and size of level k
        static int Off(byte[] h, int k) => (int)BitConverter.ToUInt32(h, 20 + k * 4);
        static int Size(byte[] h, int k) => (int)BitConverter.ToUInt32(h, 84 + k * 4);

        // a BLP2 cut down to the levels from the first one that fits MaxSize: the header (and palette) plus those levels' bytes only, read with
        // readRange(offset, length) (the CASC reads ranges, so the big levels never leave the disk). null when it is not a BLP2 this can use.
        public static byte[] Trim(Func<int, int, byte[]> readRange, int maxSize = MaxSize)
        {
            var h = readRange(0, 148 + 1024);
            if (h == null || h.Length < 148 || h[0] != 'B' || h[1] != 'L' || h[2] != 'P' || h[3] != '2' || BitConverter.ToUInt32(h, 4) != 1) return null;
            int w = BitConverter.ToInt32(h, 12), ht = BitConverter.ToInt32(h, 16);
            if (w <= 0 || ht <= 0 || w > 8192 || ht > 8192) return null;
            int hdr = h[8] == 1 ? 148 + 1024 : 148;
            if (h.Length < hdr) return null;
            int k0 = 0;
            while (k0 < 15 && Math.Max(w >> k0, ht >> k0) > maxSize && Off(h, k0 + 1) > 0 && Size(h, k0 + 1) > 0) k0++;
            int levels = 0, lo = int.MaxValue, hi = 0;
            for (int k = k0; k < 16; k++)
            {
                int o = Off(h, k), s = Size(h, k);
                if (o <= 0 || s <= 0) break;
                levels++; lo = Math.Min(lo, o); hi = Math.Max(hi, o + s);
                if (Math.Max(w >> k, ht >> k) <= 1) break;
            }
            if (levels == 0 || hi - lo > (32 << 20)) return null;
            var data = readRange(lo, hi - lo);
            if (data == null || data.Length < hi - lo) return null;
            var o2 = new byte[hdr + data.Length];
            Buffer.BlockCopy(h, 0, o2, 0, hdr); Buffer.BlockCopy(data, 0, o2, hdr, data.Length);
            for (int k = 0; k < 16; k++)
            {
                int src = k0 + k, no = 0, ns = 0;
                if (k < levels) { no = Off(h, src) - lo + hdr; ns = Size(h, src); }
                Buffer.BlockCopy(BitConverter.GetBytes(no), 0, o2, 20 + k * 4, 4); Buffer.BlockCopy(BitConverter.GetBytes(ns), 0, o2, 84 + k * 4, 4);
            }
            Buffer.BlockCopy(BitConverter.GetBytes(Math.Max(1, w >> k0)), 0, o2, 12, 4); Buffer.BlockCopy(BitConverter.GetBytes(Math.Max(1, ht >> k0)), 0, o2, 16, 4);
            return o2;
        }

        public static byte[] TrimBytes(byte[] blp, int maxSize = MaxSize)
        {
            if (blp == null) return null;
            return Trim((o, n) =>
            {
                if (o < 0 || o >= blp.Length || n <= 0) return null;
                var r = new byte[Math.Min(n, blp.Length - o)]; Buffer.BlockCopy(blp, o, r, 0, r.Length); return r;
            }, maxSize);
        }

        // DXT format of a BLP (as Blp.Decode reads it): 1 DXT1 (alpha depth 0), 2 DXT1 with 1-bit alpha, 3 DXT3 (alpha type 1), 5 DXT5 (alpha type 7); 0 = not DXT
        public static int DxtOf(byte[] blp)
        {
            if (blp[8] != 2) return 0;
            int depth = blp[9], type = blp[10];
            if (depth == 0) return 1;
            return type == 7 ? 5 : type == 1 ? 3 : 2;
        }

        // a (trimmed) BLP -> the page's texture file; null when it cannot be read
        public static byte[] Pack(byte[] blp)
        {
            try
            {
                if (blp == null || blp.Length < 148 || blp[0] != 'B' || blp[1] != 'L' || blp[2] != 'P' || blp[3] != '2') return null;
                int w = BitConverter.ToInt32(blp, 12), h = BitConverter.ToInt32(blp, 16);
                if (w <= 0 || h <= 0 || w > 4096 || h > 4096) return null;
                int fmt = DxtOf(blp);
                var levels = new List<byte[]>();
                // s3tc in WebGL wants the first level's sides in multiples of 4; anything else is decoded
                if (fmt != 0 && w % 4 == 0 && h % 4 == 0)
                {
                    int bs = fmt == 1 || fmt == 2 ? 8 : 16;
                    for (int k = 0; k < 16; k++)
                    {
                        int o = Off(blp, k), s = Size(blp, k);
                        if (o <= 0 || s <= 0) break;
                        int lw = Math.Max(1, w >> k), lh = Math.Max(1, h >> k), need = ((lw + 3) / 4) * ((lh + 3) / 4) * bs;
                        if (s < need || (long)o + need > blp.Length) break;
                        var l = new byte[need]; Buffer.BlockCopy(blp, o, l, 0, need); levels.Add(l);
                        if (lw == 1 && lh == 1) break;
                    }
                    // the page needs every level down to 1 x 1 for mipmapping; a shorter chain is decoded instead
                    if (levels.Count == 0 || (Math.Max(w, h) >> (levels.Count - 1)) > 1) levels.Clear();
                }
                if (levels.Count == 0)
                {
                    fmt = 0;
                    var bgra = Blp.Decode(blp, out w, out h);
                    var rgba = new byte[bgra.Length];
                    bool alpha = blp[9] != 0;
                    for (int i = 0; i + 3 < bgra.Length; i += 4) { rgba[i] = bgra[i + 2]; rgba[i + 1] = bgra[i + 1]; rgba[i + 2] = bgra[i]; rgba[i + 3] = alpha ? bgra[i + 3] : (byte)255; }
                    levels.Add(rgba);
                }
                var ms = new MemoryStream(); var bw = new BinaryWriter(ms);
                bw.Write(new[] { (byte)'M', (byte)'T', (byte)'X', (byte)'1' }); bw.Write((ushort)w); bw.Write((ushort)h); bw.Write((byte)fmt); bw.Write((byte)levels.Count); bw.Write((ushort)0);
                foreach (var l in levels) { bw.Write(l.Length); bw.Write(l); for (int k = l.Length; k % 4 != 0; k++) bw.Write((byte)0); }
                bw.Flush(); return ms.ToArray();
            }
            catch (Exception) { return null; }
        }
    }
}
