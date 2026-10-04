using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Rectangle = System.Windows.Shapes.Rectangle;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ElansAddonHub.Services
{
    // --selftest: the pixel strip codec on synthetic bitmaps (with colour noise, tint, other cell sizes), a strip
    // rendered by the real Lua addon (ELANSHUB_STRIP_PNG, optional), and the TGA avatar loader.
    public static class StripSelfTest
    {
        static byte[] Render(byte[] payload, int cell, int offX, int offY, int w, int h, int noise, double tint, Random rnd)
        {
            var px = new byte[w * h * 4];
            for (int i = 0; i < w * h; i++) { px[i * 4] = 40; px[i * 4 + 1] = 90; px[i * 4 + 2] = 60; px[i * 4 + 3] = 255; } // "game" background
            var cells = StripCodec.Cell(payload);
            for (int c = 0; c < cells.Length; c++)
                for (int y = 0; y < cell; y++)
                    for (int x = 0; x < cell; x++)
                    {
                        int X = offX + c * cell + x, Y = offY + y;
                        if (X >= w || Y >= h) continue;
                        for (int ch = 0; ch < 3; ch++)
                        {
                            int v = (int)(cells[c][ch] * tint) + rnd.Next(-noise, noise + 1);
                            px[(Y * w + X) * 4 + (2 - ch)] = (byte)Math.Max(0, Math.Min(255, v));
                        }
                    }
            return px;
        }

        public static string Run(string dir)
        {
            var sb = new StringBuilder();
            int fail = 0;
            void Check(string what, bool ok, string extra = "") { sb.AppendLine($"strip: {what}: {(ok ? "OK" : "FAIL")} {extra}"); if (!ok) fail++; }
            var rnd = new Random(7);
            var payload = StripCodec.BuildPayload(3, 4, 3, 42, "Elanøran-Longname");
            foreach (var (cell, ox, oy, noise, tint) in new[] { (4, 0, 0, 0, 1.0), (4, 0, 0, 14, 1.0), (4, 1, 0, 12, 0.85), (3, 0, 1, 10, 1.0), (6, 2, 0, 12, 0.9), (8, 0, 0, 14, 1.0) })
            {
                int w = 300, h = 12;
                var px = Render(payload, cell, ox, oy, w, h, noise, tint, rnd);
                var info = StripCodec.Decode(px, w, h, w * 4, out var why);
                bool ok = info != null && info.ClassFile == "HUNTER" && info.RaceFile == "NightElf" && info.Sex == 3 && info.Level == 42 && info.Name == "Elanøran-Lo";
                Check($"cell={cell} offset=({ox},{oy}) noise=+-{noise} tint={tint}", ok, info == null ? why : $"{info.Name} {info.ClassName} {info.RaceName} {info.Level}");
            }
            // garbage / no strip must be rejected
            var bad = new byte[300 * 12 * 4];
            rnd.NextBytes(bad);
            Check("random noise rejected", StripCodec.Decode(bad, 300, 12, 1200, out _) == null);
            var flat = new byte[300 * 12 * 4];
            Check("black screen rejected", StripCodec.Decode(flat, 300, 12, 1200, out _) == null);
            var broken = Render(payload, 4, 0, 0, 300, 12, 0, 1, rnd);
            for (int y = 0; y < 4; y++) for (int x = 0; x < 4; x++) for (int ch = 0; ch < 3; ch++) broken[(y * 300 + 4 * 12 + x) * 4 + ch] ^= 0x80; // flip one data cell
            Check("corrupted cell rejected by checksum", StripCodec.Decode(broken, 300, 12, 1200, out var w2) == null, w2);

            // the real addon (run in a Lua mock by the dev tests) saved its cells as a PNG
            var png = Environment.GetEnvironmentVariable("ELANSHUB_STRIP_PNG");
            if (!string.IsNullOrEmpty(png) && File.Exists(png))
            {
                var dec = new FormatConvertedBitmap(BitmapFrame.Create(new Uri(png)), PixelFormats.Bgra32, null, 0);
                int w = dec.PixelWidth, h = dec.PixelHeight; var buf = new byte[w * h * 4];
                dec.CopyPixels(buf, w * 4, 0);
                var info = StripCodec.Decode(buf, w, h, w * 4, out var why);
                Check("strip drawn by the Lua addon", info != null, info == null ? why : $"{info.Name} {info.ClassName} {info.RaceName} L{info.Level} sex{info.Sex}");
            }

            // TGA avatar loader: RLE + raw, bottom-up + top-down, then the lookup in a fake WoW root
            var raw = Tga(2, 3, 2, false, bottomUp: true, rle: false);
            var rle = Tga(2, 3, 2, false, bottomUp: true, rle: true);
            var px1 = AvatarArt.DecodeTga(raw, out int tw, out int th);
            var px2 = AvatarArt.DecodeTga(rle, out _, out _);
            Check("TGA raw and RLE decode identically", px1 != null && px2 != null && tw == 3 && th == 2 && Convert.ToBase64String(px1) == Convert.ToBase64String(px2));
            Check("TGA bottom-up flipped (top-left pixel = row 1 data)", px1 != null && px1[0] == 13 && px1[3] == 255);
            var fake = Path.Combine(dir, "fakewow");
            var hb = Path.Combine(fake, "_classic_", "Interface", "AddOns", "HealBot", "Images", "class", "1");
            Directory.CreateDirectory(hb);
            File.WriteAllBytes(Path.Combine(hb, "Hunter.tga"), rle);
            var old = AvatarArt.Root;
            AvatarArt.Root = () => fake;
            Check("class icon found in a fake WoW root", AvatarArt.ClassBrush("HUNTER") != null);
            Check("missing class icon -> null (falls back to colour + race code)", AvatarArt.ClassBrush("MAGE") == null);
            AvatarArt.Root = old;
            sb.AppendLine(fail == 0 ? "strip selftest: ALL OK" : $"strip selftest: {fail} FAILED");
            return sb.ToString().TrimEnd();
        }

        // a real window in the top-left corner of the screen showing the strip, read back through the screen capture path
        public static async Task<string> Live()
        {
            var payload = StripCodec.BuildPayload(8, 1, 2, 60, "Livetest");
            var win = new Window { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Topmost = true, ShowActivated = false, ShowInTaskbar = false, Left = 0, Top = 0, Background = Brushes.DarkSlateGray };
            var canvas = new Canvas(); win.Content = canvas;
            win.Show();
            var dpi = VisualTreeHelper.GetDpi(win).DpiScaleX;
            double cell = 4 / dpi; // 4 physical pixels
            win.Width = 200 / dpi; win.Height = 60 / dpi; win.Left = 0; win.Top = 0;
            var cells = StripCodec.Cell(payload);
            for (int i = 0; i < cells.Length; i++)
            {
                var r = new Rectangle { Width = cell, Height = cell, Fill = new SolidColorBrush(Color.FromRgb(cells[i][0], cells[i][1], cells[i][2])) };
                Canvas.SetLeft(r, i * cell); canvas.Children.Add(r);
            }
            await Task.Delay(700);
            var strip = new PixelStrip(() => true) { TestHwnd = new WindowInteropHelper(win).Handle };
            var info = strip.Probe(out var why);
            win.Close();
            return "strip live screen capture (dpi " + dpi + "): " + (info != null && info.Name == "Livetest" && info.Level == 60 && info.ClassFile == "MAGE"
                ? "OK " + info.Name + " " + info.ClassName + " " + info.RaceName : "FAIL " + why);
        }

        // 3x2 test image: pixel i has B = 10 + i
        static byte[] Tga(int type, int w, int h, bool alpha, bool bottomUp, bool rle)
        {
            var ms = new MemoryStream();
            var hd = new byte[18];
            hd[2] = (byte)(rle ? 10 : 2); hd[12] = (byte)w; hd[14] = (byte)h; hd[16] = 32; hd[17] = (byte)(bottomUp ? 8 : 0x28);
            ms.Write(hd, 0, 18);
            var pix = new byte[w * h][];
            for (int i = 0; i < pix.Length; i++) pix[i] = new byte[] { (byte)(10 + i), 20, 30, 255 };
            if (!rle) foreach (var p in pix) ms.Write(p, 0, 4);
            else
            {
                // first row stored as a raw packet, the rest as a repeat packet when equal, else raw
                for (int row = 0; row < h; row++)
                {
                    ms.WriteByte((byte)(w - 1));
                    for (int x = 0; x < w; x++) ms.Write(pix[row * w + x], 0, 4);
                }
            }
            return ms.ToArray();
        }
    }
}
