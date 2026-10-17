using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ElansAddonHub.Services.DungeonMaps
{
    // ============================================================================================================================
    // The picture of one floor, drawn top-down (north up, east right; the same orientation as the game's maps: px = (Ymax - y) * scale, py = (Xmax - x) * scale).
    // It is stored WITHOUT colours: an 8-bit PNG whose pixel values are roles, and the Outpost's theme gives each role its colour when the picture is shown,
    // so a plan follows the light or dark theme without being drawn again:
    //   0              nothing (transparent: the map stage's own background shows)
    //   1 .. 32        the floor, low to high on that floor (a slight lift in lightness with height, so ramps and terraces read)
    //   33             another floor of the same instance under or over this one (a faint shape for orientation)
    //   34 .. 37       the outline at the floor's edge, 25 / 50 / 75 / 100 % of the pixel covered (soft edges)
    //   38             a ledge inside the floor: neighbours more than LedgeYards apart in height (a drop, a stair top)
    // The floor is drawn at twice the size and folded down for the soft edges; the outline is about 1/700 of the picture's long side thick so it stays visible
    // when the picture is shown whole.
    // ============================================================================================================================
    public static class DungeonPlanImage
    {
        public const byte Empty = 0, FloorLow = 1, FloorHigh = 32, Ghost = 33, Edge25 = 34, Edge100 = 37, Ledge = 38, Roles = 39;
        public const double LedgeYards = 2.5;
        public const int MaxSide = 2048;
        public const double MaxScale = 3;               // pixels per yard at most (small rooms are not blown up)

        // the frame every floor of a plan shares
        public sealed class Frame
        {
            public double MaxX, MaxY, Scale;            // world x (north) and y (west) of the picture's top-left corner; pixels per yard
            public int Width, Height;
            public double Px(double y) => (MaxY - y) * Scale;
            public double Py(double x) => (MaxX - x) * Scale;
        }

        public static Frame FrameOf(IEnumerable<PlanFloor> floors, double margin = 8)
        {
            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
            foreach (var f in floors)
                foreach (var u in f.Units)
                    for (int i = 0; i < u.Tri.Count; i += 3)
                    {
                        double x = u.Tri[i], y = u.Tri[i + 1];
                        if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y;
                    }
            if (minX > maxX) { minX = maxX = minY = maxY = 0; }
            minX -= margin; maxX += margin; minY -= margin; maxY += margin;
            double span = Math.Max(maxX - minX, maxY - minY);
            double s = Math.Min(MaxScale, MaxSide / Math.Max(1, span));
            return new Frame { MaxX = maxX, MaxY = maxY, Scale = s, Width = Math.Max(1, (int)Math.Ceiling((maxY - minY) * s)), Height = Math.Max(1, (int)Math.Ceiling((maxX - minX) * s)) };
        }

        // ---------------------------------------------------------------- drawing
        // the floor's highest surface per pixel at twice the size (NaN = none)
        static float[] Depth(PlanFloor f, Frame fr, int w2, int h2, bool close = true)
        {
            var z = new float[w2 * h2];
            for (int i = 0; i < z.Length; i++) z[i] = float.NaN;
            double s2 = fr.Scale * 2;
            foreach (var u in f.Units)
            {
                var t = u.Tri;
                for (int k = 0; k + 9 <= t.Count; k += 9)
                {
                    double ax = (fr.MaxY - t[k + 1]) * s2, ay = (fr.MaxX - t[k]) * s2, bx = (fr.MaxY - t[k + 4]) * s2, by = (fr.MaxX - t[k + 3]) * s2, cx = (fr.MaxY - t[k + 7]) * s2, cy = (fr.MaxX - t[k + 6]) * s2;
                    Fill(z, w2, h2, ax, ay, t[k + 2], bx, by, t[k + 5], cx, cy, t[k + 8]);
                }
            }
            if (close) Close(z, w2, h2, (int)Math.Round(CloseYards * s2));
            return z;
        }

        // Cracks and pinholes between a floor's triangles (a bumpy cave floor has steep facets the slope rule leaves out) are closed: grown by CloseYards and shrunk
        // back, the filled pixels taking the highest height around them. Real gaps wider than twice that stay open.
        public const double CloseYards = 1.5;

        internal static void Close(float[] z, int w, int h, int r)
        {
            if (r <= 0) return;
            var mask = new bool[w * h];
            for (int i = 0; i < mask.Length; i++) mask[i] = !float.IsNaN(z[i]);
            var grown = Grow(mask, w, h, r);
            var outside = new bool[w * h];
            for (int i = 0; i < outside.Length; i++) outside[i] = !grown[i];
            var shrunkOut = Grow(outside, w, h, r);
            // the pixels to fill take the highest height of a neighbour that has one, ring by ring from the edge of the hole
            var todo = new List<int>();
            for (int i = 0; i < mask.Length; i++) if (!mask[i] && !shrunkOut[i]) todo.Add(i);
            for (int round = 0; round < 2 * r + 2 && todo.Count > 0; round++)
            {
                var next = new List<int>(); var set = new List<KeyValuePair<int, float>>();
                foreach (var i in todo)
                {
                    int x = i % w, y = i / w; float m = float.NaN;
                    if (x > 0) m = Hi(m, z[i - 1]); if (x + 1 < w) m = Hi(m, z[i + 1]); if (y > 0) m = Hi(m, z[i - w]); if (y + 1 < h) m = Hi(m, z[i + w]);
                    if (float.IsNaN(m)) next.Add(i); else set.Add(new KeyValuePair<int, float>(i, m));
                }
                foreach (var kv in set) z[kv.Key] = kv.Value;
                todo = next;
            }
        }

        static float Hi(float a, float b) => float.IsNaN(a) ? b : float.IsNaN(b) ? a : Math.Max(a, b);

        // a square of (2r+1) around every set pixel, rows then columns, by running counts
        internal static bool[] Grow(bool[] m, int w, int h, int r)
        {
            var a = new bool[w * h]; var b = new bool[w * h];
            for (int y = 0; y < h; y++)
            {
                int run = 0, o = y * w;
                for (int x = 0; x < Math.Min(w, r); x++) if (m[o + x]) run++;
                for (int x = 0; x < w; x++)
                {
                    if (x + r < w && m[o + x + r]) run++;
                    if (x - r - 1 >= 0 && m[o + x - r - 1]) run--;
                    a[o + x] = run > 0;
                }
            }
            for (int x = 0; x < w; x++)
            {
                int run = 0;
                for (int y = 0; y < Math.Min(h, r); y++) if (a[y * w + x]) run++;
                for (int y = 0; y < h; y++)
                {
                    if (y + r < h && a[(y + r) * w + x]) run++;
                    if (y - r - 1 >= 0 && a[(y - r - 1) * w + x]) run--;
                    b[y * w + x] = run > 0;
                }
            }
            return b;
        }

        // pixel centres inside the triangle take its interpolated height when higher; a triangle thinner than a pixel still marks the pixel of its middle
        static void Fill(float[] z, int w, int h, double ax, double ay, double az, double bx, double by, double bz, double cx, double cy, double cz)
        {
            int x0 = Math.Max(0, (int)Math.Floor(Math.Min(ax, Math.Min(bx, cx)))), x1 = Math.Min(w - 1, (int)Math.Ceiling(Math.Max(ax, Math.Max(bx, cx))));
            int y0 = Math.Max(0, (int)Math.Floor(Math.Min(ay, Math.Min(by, cy)))), y1 = Math.Min(h - 1, (int)Math.Ceiling(Math.Max(ay, Math.Max(by, cy))));
            double den = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy);
            bool any = false;
            if (Math.Abs(den) > 1e-9)
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        double px = x + 0.5, py = y + 0.5;
                        double w1 = ((by - cy) * (px - cx) + (cx - bx) * (py - cy)) / den, w2 = ((cy - ay) * (px - cx) + (ax - cx) * (py - cy)) / den, w3 = 1 - w1 - w2;
                        if (w1 < -1e-4 || w2 < -1e-4 || w3 < -1e-4) continue;
                        float v = (float)(w1 * az + w2 * bz + w3 * cz);
                        int i = y * w + x;
                        if (float.IsNaN(z[i]) || v > z[i]) z[i] = v;
                        any = true;
                    }
            if (!any)
            {
                int mx = (int)((ax + bx + cx) / 3), my = (int)((ay + by + cy) / 3);
                if (mx >= 0 && my >= 0 && mx < w && my < h) { float v = (float)((az + bz + cz) / 3); int i = my * w + mx; if (float.IsNaN(z[i]) || v > z[i]) z[i] = v; }
            }
        }

        // the role pictures of every floor (Width x Height bytes each, row by row)
        public static List<byte[]> DrawAll(IList<PlanFloor> floors, Frame fr, Action<int> drawn = null)
        {
            int w = fr.Width, h = fr.Height, w2 = w * 2, h2 = h * 2;
            // each floor's shape at the final size first (the other floors are drawn faintly under each floor)
            var shapes = new List<bool[]>();
            foreach (var f in floors)
            {
                var gz = Depth(f, fr, w2, h2, false);
                var cover = new bool[w * h];
                for (int y = 0; y < h2; y++) for (int x = 0; x < w2; x++) if (!float.IsNaN(gz[y * w2 + x])) cover[(y / 2) * w + x / 2] = true;
                shapes.Add(cover);
            }
            var res = new List<byte[]>();
            for (int k = 0; k < floors.Count; k++)
            {
                var ghost = new bool[w * h];
                for (int o = 0; o < floors.Count; o++) if (o != k) { var sh = shapes[o]; for (int i = 0; i < ghost.Length; i++) if (sh[i]) ghost[i] = true; }
                res.Add(Draw(floors[k], fr, ghost));
                drawn?.Invoke(k);
            }
            return res;
        }

        // the role picture of one floor; ghost = where the other floors are (null = none)
        public static byte[] Draw(PlanFloor f, Frame fr, bool[] ghost)
        {
            int w = fr.Width, h = fr.Height, w2 = w * 2, h2 = h * 2;
            var z = Depth(f, fr, w2, h2);
            ghost = ghost ?? new bool[w * h];
            // coverage (0..4) and the highest height per pixel
            var cov = new byte[w * h]; var hz = new float[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int c = 0; float m = float.NaN;
                    for (int dy = 0; dy < 2; dy++) for (int dx = 0; dx < 2; dx++) { float v = z[(y * 2 + dy) * w2 + x * 2 + dx]; if (!float.IsNaN(v)) { c++; if (float.IsNaN(m) || v > m) m = v; } }
                    cov[y * w + x] = (byte)c; hz[y * w + x] = m;
                }
            double lo = f.MinZ, hi = f.MaxZ;
            if (!(hi > lo)) { lo = 0; hi = 1; }
            int r = Math.Max(1, (int)Math.Round(Math.Max(w, h) / 700.0));
            var thin = new bool[w * h];
            for (int i = 0; i < thin.Length; i++) thin[i] = cov[i] < 2;
            var nearEdge = Grow(thin, w, h, r);
            var px = new byte[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    int c = cov[i];
                    if (c == 0) { px[i] = ghost[i] ? Ghost : Empty; continue; }
                    if (c < 4) { px[i] = (byte)(Edge25 + c - 1); continue; }
                    bool edge = nearEdge[i] || x < r || y < r || x >= w - r || y >= h - r;
                    if (edge) { px[i] = Edge100; continue; }
                    float zz = hz[i];
                    bool ledge = (x + 1 < w && cov[i + 1] == 4 && Math.Abs(hz[i + 1] - zz) > LedgeYards) || (y + 1 < h && cov[i + w] == 4 && Math.Abs(hz[i + w] - zz) > LedgeYards);
                    if (ledge) { px[i] = Ledge; continue; }
                    double t = (zz - lo) / (hi - lo);
                    px[i] = (byte)(FloorLow + Math.Max(0, Math.Min(31, (int)Math.Round(t * 31))));
                }
            return px;
        }

        // ---------------------------------------------------------------- colours (the theme's, given by the caller)
        public sealed class Colors
        {
            public Color FloorLow, FloorHigh, Ghost, Edge, Ledge;
        }

        static Color Mix(Color a, Color b, double t) => Color.FromArgb((byte)Math.Round(a.A + (b.A - a.A) * t), (byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));

        public static BitmapPalette Palette(Colors c)
        {
            var list = new List<Color>(256) { Color.FromArgb(0, 0, 0, 0) };
            for (int i = 0; i < 32; i++) list.Add(Mix(c.FloorLow, c.FloorHigh, i / 31.0));
            list.Add(c.Ghost);
            for (int k = 1; k <= 4; k++) list.Add(Color.FromArgb((byte)(c.Edge.A * k / 4), c.Edge.R, c.Edge.G, c.Edge.B));
            list.Add(c.Ledge);
            while (list.Count < 256) list.Add(Color.FromArgb(0, 0, 0, 0));
            return new BitmapPalette(list);
        }

        // the stored palette: grey levels (any palette would do, the values are what counts; distinct entries keep an encoder from merging them)
        static BitmapPalette RolePalette()
        {
            var list = new List<Color>(256);
            for (int i = 0; i < 256; i++) list.Add(Color.FromRgb((byte)i, (byte)i, (byte)i));
            return new BitmapPalette(list);
        }

        public static void WritePng(string path, byte[] px, int w, int h)
        {
            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Indexed8, RolePalette(), px, w);
            bmp.Freeze();
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            var tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None)) enc.Save(fs);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        // the role values back (null = missing or damaged)
        public static byte[] ReadPng(string path, out int w, out int h)
        {
            w = h = 0;
            try
            {
                BitmapSource src;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var dec = new PngBitmapDecoder(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    src = dec.Frames[0];
                }
                w = src.PixelWidth; h = src.PixelHeight;
                if (w <= 0 || h <= 0 || w > 8192 || h > 8192) return null;
                var px = new byte[w * h];
                if (src.Format == PixelFormats.Indexed8) src.CopyPixels(px, w, 0);
                else
                {
                    // a re-encoded copy: read the grey level
                    var g = new FormatConvertedBitmap(src, PixelFormats.Gray8, null, 0);
                    g.CopyPixels(px, w, 0);
                }
                for (int i = 0; i < px.Length; i++) if (px[i] >= Roles) return null;
                return px;
            }
            catch (Exception) { return null; }
        }

        // the picture in the theme's colours, frozen (any thread)
        public static BitmapSource Colour(byte[] px, int w, int h, Colors c)
        {
            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Indexed8, Palette(c), px, w);
            var conv = new FormatConvertedBitmap(bmp, PixelFormats.Pbgra32, null, 0);
            var wb = new WriteableBitmap(conv);
            wb.Freeze();
            return wb;
        }

        public static void SaveColoured(string path, byte[] px, int w, int h, Colors c, Color? background = null)
        {
            BitmapSource bmp = Colour(px, w, h, c);
            if (background != null)
            {
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen()) { dc.DrawRectangle(new SolidColorBrush(background.Value), null, new System.Windows.Rect(0, 0, w, h)); dc.DrawImage(bmp, new System.Windows.Rect(0, 0, w, h)); }
                var rt = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                rt.Render(dv); rt.Freeze(); bmp = rt;
            }
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None)) enc.Save(fs);
        }
    }
}
