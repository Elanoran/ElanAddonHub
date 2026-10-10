using System;
using System.IO;

namespace ElansAddonHub.Services
{
    // BLP2 texture decoder (first mip level only): DXT1/3/5, palettized (0/1/4/8 bit alpha) and raw BGRA.
    public static class Blp
    {
        public static byte[] Decode(byte[] d, out int width, out int height)
        {
            if (d == null || d.Length < 148 || d[0] != 'B' || d[1] != 'L' || d[2] != 'P' || d[3] != '2') throw new InvalidDataException("not a BLP2 file");
            uint type = BitConverter.ToUInt32(d, 4);
            if (type != 1) throw new NotSupportedException("BLP type " + type + " (JPEG) is not supported");
            int enc = d[8], alphaDepth = d[9], alphaType = d[10];
            width = (int)BitConverter.ToUInt32(d, 12);
            height = (int)BitConverter.ToUInt32(d, 16);
            int off = (int)BitConverter.ToUInt32(d, 20), size = (int)BitConverter.ToUInt32(d, 20 + 64);
            if (width <= 0 || height <= 0 || width > 4096 || height > 4096) throw new InvalidDataException("odd BLP size");
            if (off <= 0 || off + size > d.Length) throw new InvalidDataException("BLP mip 0 is outside the file");
            var px = new byte[width * height * 4];
            if (enc == 1)
            {
                int n = width * height;
                int aBytes = alphaDepth == 8 ? n : alphaDepth == 4 ? (n + 1) / 2 : alphaDepth == 1 ? (n + 7) / 8 : 0;
                if (off + n + aBytes > d.Length) throw new InvalidDataException("palettized BLP is truncated");
                for (int i = 0; i < n; i++)
                {
                    int pi = 148 + d[off + i] * 4;
                    px[i * 4] = d[pi]; px[i * 4 + 1] = d[pi + 1]; px[i * 4 + 2] = d[pi + 2];
                    int a = 255;
                    if (alphaDepth == 8) a = d[off + n + i];
                    else if (alphaDepth == 4) { int b = d[off + n + i / 2]; a = ((i & 1) == 0 ? b & 0xF : b >> 4) * 17; }
                    else if (alphaDepth == 1) a = ((d[off + n + i / 8] >> (i & 7)) & 1) * 255;
                    px[i * 4 + 3] = (byte)a;
                }
            }
            else if (enc == 2)
            {
                int mode = alphaDepth == 0 ? 1 : alphaType == 7 ? 5 : alphaType == 1 ? 3 : 1;
                int bw = (width + 3) / 4, bh = (height + 3) / 4, bs = mode == 1 ? 8 : 16;
                if (off + bw * bh * bs > d.Length) throw new InvalidDataException("DXT data is truncated");
                var block = new byte[64];
                for (int by = 0; by < bh; by++)
                    for (int bx = 0; bx < bw; bx++)
                    {
                        int p = off + (by * bw + bx) * bs;
                        Dxt(d, p, mode, block);
                        for (int y = 0; y < 4 && by * 4 + y < height; y++)
                            for (int x = 0; x < 4 && bx * 4 + x < width; x++)
                                Buffer.BlockCopy(block, (y * 4 + x) * 4, px, ((by * 4 + y) * width + bx * 4 + x) * 4, 4);
                    }
            }
            else if (enc == 3)
            {
                if (off + width * height * 4 > d.Length) throw new InvalidDataException("raw BLP is truncated");
                Buffer.BlockCopy(d, off, px, 0, width * height * 4);
            }
            else throw new NotSupportedException("BLP encoding " + enc);
            return px;
        }

        static void Dxt(byte[] d, int p, int mode, byte[] outBgra)
        {
            int cp = mode == 1 ? p : p + 8;
            int c0 = d[cp] | d[cp + 1] << 8, c1 = d[cp + 2] | d[cp + 3] << 8;
            var pal = new int[4][];
            pal[0] = Rgb565(c0); pal[1] = Rgb565(c1);
            if (c0 > c1 || mode != 1)
            {
                pal[2] = new[] { (2 * pal[0][0] + pal[1][0]) / 3, (2 * pal[0][1] + pal[1][1]) / 3, (2 * pal[0][2] + pal[1][2]) / 3, 255 };
                pal[3] = new[] { (pal[0][0] + 2 * pal[1][0]) / 3, (pal[0][1] + 2 * pal[1][1]) / 3, (pal[0][2] + 2 * pal[1][2]) / 3, 255 };
            }
            else
            {
                pal[2] = new[] { (pal[0][0] + pal[1][0]) / 2, (pal[0][1] + pal[1][1]) / 2, (pal[0][2] + pal[1][2]) / 2, 255 };
                pal[3] = new[] { 0, 0, 0, 0 };
            }
            uint bits = BitConverter.ToUInt32(d, cp + 4);
            var alpha = new int[16];
            for (int i = 0; i < 16; i++) alpha[i] = 255;
            if (mode == 3)
            {
                for (int i = 0; i < 16; i++) { int b = d[p + i / 2]; alpha[i] = ((i & 1) == 0 ? b & 0xF : b >> 4) * 17; }
            }
            else if (mode == 5)
            {
                int a0 = d[p], a1 = d[p + 1];
                var av = new int[8];
                av[0] = a0; av[1] = a1;
                if (a0 > a1) for (int i = 1; i < 7; i++) av[i + 1] = ((7 - i) * a0 + i * a1) / 7;
                else { for (int i = 1; i < 5; i++) av[i + 1] = ((5 - i) * a0 + i * a1) / 5; av[6] = 0; av[7] = 255; }
                ulong ab = 0;
                for (int i = 0; i < 6; i++) ab |= (ulong)d[p + 2 + i] << (8 * i);
                for (int i = 0; i < 16; i++) alpha[i] = av[(int)((ab >> (3 * i)) & 7)];
            }
            for (int i = 0; i < 16; i++)
            {
                var c = pal[(bits >> (2 * i)) & 3];
                outBgra[i * 4] = (byte)c[2]; outBgra[i * 4 + 1] = (byte)c[1]; outBgra[i * 4 + 2] = (byte)c[0];
                outBgra[i * 4 + 3] = (byte)(mode == 1 ? c[3] : alpha[i]);
            }
        }

        static int[] Rgb565(int c)
        {
            int r = (c >> 11) & 31, g = (c >> 5) & 63, b = c & 31;
            return new[] { (r << 3) | (r >> 2), (g << 2) | (g >> 4), (b << 3) | (b >> 2), 255 };
        }
    }
}
