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
        static byte[] Render(byte[] payload, int cell, int offX, int offY, int w, int h, int noise, double tint, Random rnd, byte[] payload2 = null)
        {
            var px = new byte[w * h * 4];
            for (int i = 0; i < w * h; i++) { px[i * 4] = 40; px[i * 4 + 1] = 90; px[i * 4 + 2] = 60; px[i * 4 + 3] = 255; } // "game" background
            void Paint(byte[][] cells, int row)
            {
                for (int c = 0; c < cells.Length; c++)
                    for (int y = 0; y < cell; y++)
                        for (int x = 0; x < cell; x++)
                        {
                            int X = offX + c * cell + x, Y = offY + row * cell + y;
                            if (X >= w || Y >= h) continue;
                            for (int ch = 0; ch < 3; ch++)
                            {
                                int v = (int)(cells[c][ch] * tint) + rnd.Next(-noise, noise + 1);
                                px[(Y * w + X) * 4 + (2 - ch)] = (byte)Math.Max(0, Math.Min(255, v));
                            }
                        }
            }
            Paint(StripCodec.Cell(payload), 0);
            if (payload2 != null) Paint(StripCodec.CellV2(payload2), 1);
            return px;
        }

        static bool ReadCells(string file, out byte[] px, out int w, out int h)
        {
            // 64 lines "r g b": row 0 then row 1, 4x4 pixel cells (what the Lua test of the companion produced)
            var lines = File.ReadAllLines(file);
            w = 160; h = 20; px = new byte[w * h * 4];
            for (int i = 0; i < w * h; i++) { px[i * 4] = 40; px[i * 4 + 1] = 90; px[i * 4 + 2] = 60; px[i * 4 + 3] = 255; }
            for (int c = 0; c < 64 && c < lines.Length; c++)
            {
                var v = lines[c].Split(' ');
                int row = c / 32, col = c % 32;
                for (int y = 0; y < 4; y++) for (int x = 0; x < 4; x++)
                {
                    int X = col * 4 + x, Y = row * 4 + y;
                    for (int ch = 0; ch < 3; ch++) px[(Y * w + X) * 4 + (2 - ch)] = byte.Parse(v[ch]);
                }
            }
            return lines.Length >= 64;
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
            // ---- v2 (second row): zone/instance ids, flags, xp, group - with noise, tint and other cell sizes; v1 payloads still decode
            var v1only = StripCodec.BuildPayload(3, 4, 3, 42, "Elan");
            var v2p = StripCodec.BuildPayload(3, 4, 3, 42, "Elan", v2: true);
            var v2row = StripCodec.BuildPayloadV2(1413, 43, 1 + 16 + 64 + 128, 1 + 2 + 4, 62, 5);
            foreach (var (cell, ox, oy, noise, tint) in new[] { (4, 0, 0, 0, 1.0), (4, 0, 0, 14, 1.0), (4, 1, 1, 12, 0.85), (3, 0, 1, 10, 1.0), (6, 2, 0, 12, 0.9), (8, 0, 0, 14, 1.0) })
            {
                int w = 300, h = 40;
                var px = Render(v2p, cell, ox, oy, w, h, noise, tint, rnd, v2row);
                var info = StripCodec.Decode(px, w, h, w * 4, out var why);
                bool ok = info != null && info.Name == "Elan" && info.Level == 42 && info.HasV2 && info.MapId == 1413 && info.InstanceId == 43
                    && info.InCombat && info.InInstance && info.InGroup && info.GroupSize == 5 && info.XpKnown && info.XpPercent == 62 && info.Rested && !info.Dead && !info.Afk;
                Check($"v2 strip cell={cell} offset=({ox},{oy}) noise=+-{noise} tint={tint}", ok, info == null ? why : $"v2={info.HasV2} map={info.MapId} inst={info.InstanceId} f1={info.Flags1} xp={info.XpPercent} group={info.GroupSize}");
            }
            {
                var px = Render(v1only, 4, 0, 0, 300, 40, 10, 1.0, rnd);
                var info = StripCodec.Decode(px, 300, 40, 1200, out var why);
                Check("v1 strip (old companion) still decodes, no v2", info != null && info.Name == "Elan" && info.Level == 42 && !info.HasV2 && info.MapId == 0, why);
                // a v1-only reader (old hub) = only the first row of cells: a v2 strip cut to one row still gives the full v1 result
                px = Render(v2p, 4, 0, 0, 300, 4, 0, 1.0, rnd, v2row);
                info = StripCodec.Decode(px, 300, 4, 1200, out why);
                Check("old-hub view of a v2 strip (row 0 only): v1 fields intact", info != null && info.Name == "Elan" && info.ClassFile == "HUNTER" && !info.HasV2, why);
                var bad2 = Render(v2p, 4, 0, 0, 300, 40, 0, 1.0, rnd, v2row);
                for (int y = 4; y < 8; y++) for (int x = 4 * 7; x < 4 * 8; x++) for (int ch = 0; ch < 3; ch++) bad2[(y * 300 + x) * 4 + ch] ^= 0x80; // flip a row-1 cell
                info = StripCodec.Decode(bad2, 300, 40, 1200, out why);
                Check("corrupted row 1: v2 dropped, v1 kept", info != null && info.Name == "Elan" && !info.HasV2, why);
                var flat2 = Render(v1only, 4, 0, 0, 300, 40, 0, 1.0, rnd, v2row); // v2 row present but row 0 doesn't announce it
                info = StripCodec.Decode(flat2, 300, 40, 1200, out why);
                Check("v2 row ignored when row 0 doesn't announce it", info != null && !info.HasV2, why);
            }
            // names for the ids: open-world uiMapID, dungeon instance id, unknown -> nothing
            var ch1 = new GamePresence.Character();
            GamePresence.ApplyLive(ch1, StripCodec.Decode(Render(v2p, 4, 0, 0, 300, 40, 0, 1.0, rnd, v2row), 300, 40, 1200, out _));
            Check("zone names: map 1413 -> The Barrens, instance 43 -> Wailing Caverns", ch1.Zone == "The Barrens" && ch1.InstanceName == "Wailing Caverns" && ch1.GroupSize == 5 && ch1.XpPercent == 62 && ch1.Rested && ch1.InCombat);
            var ch2 = new GamePresence.Character { Zone = "stale" };
            GamePresence.ApplyLive(ch2, StripCodec.Decode(Render(v2p, 4, 0, 0, 300, 40, 0, 1.0, rnd, StripCodec.BuildPayloadV2(0, 43, 16 + 64, 1, 0, 0)), 300, 40, 1200, out _));
            Check("inside a dungeon (map unknown): the instance name is the zone", ch2.Zone == "Wailing Caverns" && ch2.InInstance && ch2.XpPercent == -1);
            var ch3 = new GamePresence.Character { Zone = "stale" };
            GamePresence.ApplyLive(ch3, StripCodec.Decode(Render(v2p, 4, 0, 0, 300, 40, 0, 1.0, rnd, StripCodec.BuildPayloadV2(9999, 0, 0, 1, 0, 0)), 300, 40, 1200, out _));
            Check("unknown map id: no zone text (not a stale or wrong one)", ch3.Zone == null);
            var cells = Environment.GetEnvironmentVariable("ELANSHUB_STRIP_CELLS");
            if (!string.IsNullOrEmpty(cells) && File.Exists(cells) && ReadCells(cells, out var cpx, out var cw, out var chh))
            {
                var li = StripCodec.Decode(cpx, cw, chh, cw * 4, out var lwhy);
                Check("strip drawn by the REAL Lua addon (companion 1.5 test): v1 + v2 fields", li != null && li.HasV2 && li.Name == "Elan" && li.Level == 60 && li.ClassFile == "HUNTER" && li.RaceFile == "Orc"
                    && li.MapId == 1426 && li.InstanceId == 43 && li.InCombat && li.Afk && li.InInstance && li.GroupSize == 5 && li.XpPercent == 50 && li.Rested,
                    li == null ? lwhy : $"map={li.MapId} inst={li.InstanceId} f1={li.Flags1} f2={li.Flags2} xp={li.XpPercent} group={li.GroupSize}");
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
