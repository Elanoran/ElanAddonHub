using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ElansAddonHub.Services
{
    // Class portraits for the round avatars. We do NOT ship any Blizzard art: the icons are loaded at run time from
    // the user's own WoW folder, from an installed addon that happens to include them (HealBot's round class icons,
    // plain TGA files). If that addon isn't there, the avatar falls back to the class colour + race code.
    public static class AvatarArt
    {
        public static Func<string> Root = () => null;
        static readonly Dictionary<string, ImageBrush> cache = new Dictionary<string, ImageBrush>();
        static string cachedRoot;

        // addon-relative locations tried, in order ({0} = "Hunter", "Deathknight" ...)
        static readonly string[] Candidates = { @"HealBot\Images\class\1\{0}.tga" };

        public static ImageBrush ClassBrush(string classFile)
        {
            if (string.IsNullOrEmpty(classFile)) return null;
            var root = Root();
            lock (cache)
            {
                if (root != cachedRoot) { cache.Clear(); cachedRoot = root; }
                if (cache.TryGetValue(classFile, out var hit)) return hit;
                ImageBrush brush = null;
                try
                {
                    var file = Find(root, classFile);
                    if (file != null)
                    {
                        var src = Decode(File.ReadAllBytes(file));
                        if (src != null) { brush = new ImageBrush(src) { Stretch = Stretch.UniformToFill }; brush.Freeze(); }
                    }
                }
                catch (Exception e) { Util.Log("avatar art: " + e.Message); }
                cache[classFile] = brush;
                return brush;
            }
        }

        static string Find(string root, string classFile)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return null;
            var name = char.ToUpperInvariant(classFile[0]) + classFile.Substring(1).ToLowerInvariant();
            foreach (var flavor in Directory.GetDirectories(root, "_*_"))
                foreach (var c in Candidates)
                {
                    var f = Path.Combine(flavor, "Interface", "AddOns", string.Format(c, name));
                    if (File.Exists(f)) return f;
                }
            return null;
        }

        public static BitmapSource Decode(byte[] tga)
        {
            var px = DecodeTga(tga, out int w, out int h);
            if (px == null) return null;
            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
            bmp.Freeze();
            return bmp;
        }

        // TGA, types 2 (raw) and 10 (RLE), 24/32 bit, no palette. Returns top-down BGRA.
        public static byte[] DecodeTga(byte[] d, out int w, out int h)
        {
            w = h = 0;
            if (d == null || d.Length < 18) return null;
            int idLen = d[0], cmap = d[1], type = d[2];
            w = d[12] | d[13] << 8; h = d[14] | d[15] << 8;
            int bpp = d[16], desc = d[17];
            if (cmap != 0 || (type != 2 && type != 10) || (bpp != 24 && bpp != 32) || w <= 0 || h <= 0 || w > 4096 || h > 4096) return null;
            int bytes = bpp / 8, pos = 18 + idLen;
            var raw = new byte[w * h * 4];
            int n = 0, total = w * h;
            while (n < total)
            {
                if (type == 2)
                {
                    if (pos + bytes > d.Length) return null;
                    Put(raw, n++, d, pos, bytes); pos += bytes;
                }
                else
                {
                    if (pos >= d.Length) return null;
                    int hd = d[pos++], cnt = (hd & 0x7F) + 1;
                    if ((hd & 0x80) != 0)
                    {
                        if (pos + bytes > d.Length) return null;
                        for (int i = 0; i < cnt && n < total; i++) Put(raw, n++, d, pos, bytes);
                        pos += bytes;
                    }
                    else
                        for (int i = 0; i < cnt && n < total; i++)
                        {
                            if (pos + bytes > d.Length) return null;
                            Put(raw, n++, d, pos, bytes); pos += bytes;
                        }
                }
            }
            if ((desc & 0x20) != 0) return raw; // already top-down
            var flipped = new byte[raw.Length];
            for (int y = 0; y < h; y++) Buffer.BlockCopy(raw, (h - 1 - y) * w * 4, flipped, y * w * 4, w * 4);
            return flipped;
        }

        static void Put(byte[] dst, int i, byte[] src, int p, int bytes)
        {
            dst[i * 4] = src[p]; dst[i * 4 + 1] = src[p + 1]; dst[i * 4 + 2] = src[p + 2];
            dst[i * 4 + 3] = bytes == 4 ? src[p + 3] : (byte)255;
        }
    }
}
