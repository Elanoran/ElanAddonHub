using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.WorldMap;

namespace ElansAddonHub.Services.DungeonMaps
{
    // ============================================================================================================================
    // The game's own minimap pictures of a building's inside (what the in-game minimap shows in a dungeon), laid onto the drawn floors of a plan.
    //
    // Where they are (checked on build 1.60.1.70338): the client no longer finds them by path; the table WMOMinimapTexture (FileDataID 1323241, layout 01230A50
    // per WoWDBDefs: ID (id list), GroupNum u16, BlockX u8, BlockY u8, FileDataID, WMOID (relationship)) lists, per model (MOHD's wmoID) and per group, the
    // pictures of the group as a grid of blocks. 97,172 blocks of 3,102 models on this build; 45 of the 47 dungeons and raids with no map art have them (not the
    // Emerald Dream, which has no building, nor Manor Mistmantle, whose world table is not in the install). The BlockY column reads with 0x100 added on this build
    // (every row: 256, 257, 258 ...); the block index is its low byte.
    //
    // How a block lies (measured on Molten Core and the Deadmines against each group's own walkable floor: 94 - 99.8 % of the floor covered, the best offset 0
    // within a pixel, every other flip or turn clearly worse): 2 pixels per yard, a block is 128 x 128 yards from the group's box (MOGI) corner: block (bx, by)
    // starts at model (minX + 128 bx, minY + 128 by); the picture's columns run along model x, its rows along model y with the first row at the block's HIGH y
    // end (the picture is upside down against model y). A block at the group's edge is a smaller picture (a power of two that holds the rest of the group), its
    // first row still at its own high y end.
    //
    // Laid onto a plan: a drawn floor keeps its frame (DungeonPlanImage.Frame), so everything placed by world position lands on the same spot in both looks. For
    // each building group with triangles on the floor (low groups first), every texel is carried through the placement (the model's axes to the world's, the
    // world's to the picture's) and blended over; only where that group itself stands on this floor (its triangles here, grown by GrowYards) and the floor is
    // - a room whose gallery is on another floor does not paint the gallery here, nor the hall under the gallery on the gallery's floor. The result is a BGRA picture with alpha (transparent off the floor); the map card shows it over the plan, so where the
    // game has no picture the plan shows through.
    // ============================================================================================================================
    public sealed class WmoMinimapIndex
    {
        public const int FileDataId = 1323241;
        public const uint TableHash = 0xB1C5A0F8, Layout = 0x01230A50;
        public const int FGroup = 0, FBlockX = 1, FBlockY = 2, FFile = 3;

        public sealed class Block { public int X, Y, Fdid; }

        readonly Dictionary<int, Dictionary<int, List<Block>>> byWmo = new Dictionary<int, Dictionary<int, List<Block>>>();
        public int Rows { get; private set; }
        public int Wmos => byWmo.Count;
        public string Problem;

        public static WmoMinimapIndex FromRows(IEnumerable<(int wmoId, int group, int x, int y, int fdid)> rows)
        {
            var ix = new WmoMinimapIndex();
            foreach (var r in rows) ix.Add(r.wmoId, r.group, r.x, r.y, r.fdid);
            return ix;
        }

        void Add(int wmoId, int group, int x, int y, int fdid)
        {
            if (wmoId <= 0 || group < 0 || x < 0 || y < 0 || x > 63 || y > 63 || fdid <= 0) return;
            if (!byWmo.TryGetValue(wmoId, out var g)) byWmo[wmoId] = g = new Dictionary<int, List<Block>>();
            if (!g.TryGetValue(group, out var l)) g[group] = l = new List<Block>();
            if (l.Any(b => b.X == x && b.Y == y)) return;
            l.Add(new Block { X = x, Y = y, Fdid = fdid });
            Rows++;
        }

        // the table file; an unknown layout or another table = an empty index with the reason (nothing guessed)
        public static WmoMinimapIndex Parse(byte[] file)
        {
            var ix = new WmoMinimapIndex();
            if (file == null) { ix.Problem = "WMOMinimapTexture.db2 is not in this game install"; return ix; }
            Wdc5 w;
            try { w = Wdc5.Parse(file); } catch (Exception e) { ix.Problem = "WMOMinimapTexture.db2 could not be read: " + e.Message; return ix; }
            if (w.TableHash != TableHash || w.Sparse) { ix.Problem = "WMOMinimapTexture is not the table this version expects"; return ix; }
            if (w.LayoutHash != Layout) { ix.Problem = "game updated: WMOMinimapTexture layout " + w.LayoutHash.ToString("X8") + " is not known to this Outpost yet"; return ix; }
            for (int i = 0; i < w.Count; i++)
                ix.Add(w.Foreign(i), (int)w.Field(i, FGroup), (int)(w.Field(i, FBlockX) & 0xFF), (int)(w.Field(i, FBlockY) & 0xFF), (int)w.Field(i, FFile));
            return ix;
        }

        public IReadOnlyList<Block> BlocksOf(int wmoId, int group) => byWmo.TryGetValue(wmoId, out var g) && g.TryGetValue(group, out var l) ? (IReadOnlyList<Block>)l : NoBlocks;
        public bool Has(int wmoId) => byWmo.ContainsKey(wmoId);
        static readonly Block[] NoBlocks = new Block[0];
    }

    public static class DungeonMinimap
    {
        public const double BlockYards = 128, PixelsPerYard = 2, GrowYards = 2.5, MinCoverage = 0.3;
        public static Func<WmoMinimapIndex> IndexForTest;

        public sealed class Picture { public byte[] Bgra; public int Width, Height; }

        public sealed class GameFloor
        {
            public byte[] Bgra; public int Width, Height;
            public double Coverage;          // the floor's own pixels the game's pictures cover (0..1)
            public int Groups, Blocks, MissingBlocks;
        }

        // the texel (column, row; centres at .5) of a block picture `h` pixels tall that shows model point (mx, my); see the header for the rule
        public static void TexelOf(double mx, double my, double minX, double minY, int bx, int by, int h, out double c, out double r)
        {
            c = (mx - (minX + bx * BlockYards)) * PixelsPerYard;
            r = h - (my - (minY + by * BlockYards)) * PixelsPerYard;
        }

        // the index of the install (null = none)
        public static WmoMinimapIndex Index(IBuildingSource src)
        {
            if (IndexForTest != null) return IndexForTest();
            try { var ix = WmoMinimapIndex.Parse(src.Read(WmoMinimapIndex.FileDataId)); if (ix.Problem != null) Util.Log("dungeon plans: " + ix.Problem); return ix.Rows > 0 ? ix : null; }
            catch (Exception e) { Util.Log("dungeon plans: minimap table: " + e.Message); return null; }
        }

        // does any building of this geometry have pictures
        public static bool Any(PlanGeometry geo, WmoMinimapIndex ix) => ix != null && geo != null && geo.Roots.Values.Any(r => ix.Has(r.WmoId));

        // the game's pictures of one drawn floor in its frame; roles = the floor's role picture (DungeonPlanImage.Draw); texture(fdid) = a decoded block (null = missing)
        public static GameFloor Compose(PlanFloor floor, DungeonPlanImage.Frame fr, byte[] roles, PlanGeometry geo, WmoMinimapIndex ix, Func<int, Picture> texture)
        {
            int W = fr.Width, H = fr.Height;
            var res = new GameFloor { Width = W, Height = H, Bgra = new byte[W * H * 4] };
            if (ix == null || geo == null || roles == null || roles.Length != W * H) return res;
            var mask = new bool[W * H]; int floorPx = 0;
            for (int i = 0; i < mask.Length; i++) { byte v = roles[i]; mask[i] = v != DungeonPlanImage.Empty && v != DungeonPlanImage.Ghost; if (mask[i]) floorPx++; }
            if (floorPx == 0) return res;
            var paint = DungeonPlanImage.Grow(mask, W, H, Math.Max(1, (int)Math.Round(GrowYards * fr.Scale)));
            // the groups with triangles on this floor, low ones first
            var groups = floor.Units.Where(u => u.Kind == PlanUnitKind.Building && u.Place >= 0 && u.Group >= 0)
                .GroupBy(u => (u.Place, u.Group))
                .Select(g => new { g.Key.Place, g.Key.Group, Units = g.ToList(), Z = g.Sum(u => u.Area) > 0 ? g.Sum(u => MeanZ(u) * u.Area) / g.Sum(u => u.Area) : MeanZ(g.First()) })
                .OrderBy(g => g.Z).ToList();
            int grow = Math.Max(1, (int)Math.Round(GrowYards * fr.Scale));
            foreach (var gk in groups)
            {
                if (gk.Place >= geo.Places.Count) continue;
                var p = geo.Places[gk.Place];
                if (!geo.Roots.TryGetValue((int)p.NameId, out var root) || gk.Group >= root.GroupInfos.Count) continue;
                var gi = root.GroupInfos[gk.Group];
                var blocks = ix.BlocksOf(root.WmoId, gk.Group);
                if (blocks.Count == 0) continue;
                res.Groups++;
                // where this group stands on this floor (a group that also holds a gallery on another floor paints only its part here)
                var own = Footprint(gk.Units, fr);
                own = DungeonPlanImage.Grow(own, W, H, grow);
                for (int i = 0; i < own.Length; i++) own[i] &= paint[i];
                // model (x, y) -> picture (u, v): affine through the placement at the group's middle height
                double z = (gi.MinZ + gi.MaxZ) / 2;
                DungeonGeometry.ToWorld(p, 0, 0, z, out var ox, out var oy, out _);
                DungeonGeometry.ToWorld(p, 1, 0, z, out var ax, out var ay, out _);
                DungeonGeometry.ToWorld(p, 0, 1, z, out var bx2, out var by2, out _);
                double u0 = (fr.MaxY - oy) * fr.Scale, v0 = (fr.MaxX - ox) * fr.Scale;
                double uX = -(ay - oy) * fr.Scale, vX = -(ax - ox) * fr.Scale;     // d(u, v) / d model x
                double uY = -(by2 - oy) * fr.Scale, vY = -(bx2 - ox) * fr.Scale;   // d(u, v) / d model y
                double det = uX * vY - uY * vX;
                if (Math.Abs(det) < 1e-12) continue;
                foreach (var b in blocks)
                {
                    var t = texture(b.Fdid);
                    if (t == null || t.Bgra == null || t.Width <= 0 || t.Height <= 0) { res.MissingBlocks++; continue; }
                    res.Blocks++;
                    double x0 = gi.MinX + b.X * BlockYards, y0 = gi.MinY + b.Y * BlockYards, x1 = x0 + t.Width / PixelsPerYard, y1 = y0 + t.Height / PixelsPerYard;
                    double umin = double.MaxValue, umax = double.MinValue, vmin = double.MaxValue, vmax = double.MinValue;
                    foreach (var (mx, my) in new[] { (x0, y0), (x1, y0), (x0, y1), (x1, y1) })
                    {
                        double u = u0 + mx * uX + my * uY, v = v0 + mx * vX + my * vY;
                        umin = Math.Min(umin, u); umax = Math.Max(umax, u); vmin = Math.Min(vmin, v); vmax = Math.Max(vmax, v);
                    }
                    int pu0 = Math.Max(0, (int)Math.Floor(umin)), pu1 = Math.Min(W - 1, (int)Math.Ceiling(umax)), pv0 = Math.Max(0, (int)Math.Floor(vmin)), pv1 = Math.Min(H - 1, (int)Math.Ceiling(vmax));
                    for (int pv = pv0; pv <= pv1; pv++)
                        for (int pu = pu0; pu <= pu1; pu++)
                        {
                            int i = pv * W + pu;
                            if (!own[i]) continue;
                            double du = pu + 0.5 - u0, dv = pv + 0.5 - v0;
                            double mx = (du * vY - dv * uY) / det, my = (dv * uX - du * vX) / det;
                            if (mx < x0 || mx >= x1 || my < y0 || my >= y1) continue;
                            TexelOf(mx, my, gi.MinX, gi.MinY, b.X, b.Y, t.Height, out var c, out var r);
                            Sample(t, c - 0.5, r - 0.5, out var sb, out var sg, out var sr, out var sa);
                            if (sa <= 0) continue;
                            Over(res.Bgra, i * 4, sb, sg, sr, sa);
                        }
                }
            }
            int covered = 0;
            for (int i = 0; i < mask.Length; i++) if (mask[i] && res.Bgra[i * 4 + 3] > 0) covered++;
            res.Coverage = (double)covered / floorPx;
            return res;
        }

        // the pixels of the frame the units' triangles cover (centres inside, plus the pixel of each triangle's middle)
        internal static bool[] Footprint(IEnumerable<PlanUnit> units, DungeonPlanImage.Frame fr)
        {
            int W = fr.Width, H = fr.Height;
            var m = new bool[W * H];
            foreach (var u in units)
            {
                var t = u.Tri;
                for (int k = 0; k + 9 <= t.Count; k += 9)
                {
                    double ax = fr.Px(t[k + 1]), ay = fr.Py(t[k]), bx = fr.Px(t[k + 4]), by = fr.Py(t[k + 3]), cx = fr.Px(t[k + 7]), cy = fr.Py(t[k + 6]);
                    int mx = (int)((ax + bx + cx) / 3), my = (int)((ay + by + cy) / 3);
                    if (mx >= 0 && my >= 0 && mx < W && my < H) m[my * W + mx] = true;
                    int x0 = Math.Max(0, (int)Math.Floor(Math.Min(ax, Math.Min(bx, cx)))), x1 = Math.Min(W - 1, (int)Math.Ceiling(Math.Max(ax, Math.Max(bx, cx))));
                    int y0 = Math.Max(0, (int)Math.Floor(Math.Min(ay, Math.Min(by, cy)))), y1 = Math.Min(H - 1, (int)Math.Ceiling(Math.Max(ay, Math.Max(by, cy))));
                    double den = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy);
                    if (Math.Abs(den) < 1e-9) continue;
                    for (int y = y0; y <= y1; y++)
                        for (int x = x0; x <= x1; x++)
                        {
                            double px = x + 0.5, py = y + 0.5;
                            double w1 = ((by - cy) * (px - cx) + (cx - bx) * (py - cy)) / den, w2 = ((cy - ay) * (px - cx) + (ax - cx) * (py - cy)) / den;
                            if (w1 >= -1e-4 && w2 >= -1e-4 && 1 - w1 - w2 >= -1e-4) m[y * W + x] = true;
                        }
                }
            }
            return m;
        }

        static double MeanZ(PlanUnit u)
        {
            double s = 0; int n = 0;
            for (int i = 2; i < u.Tri.Count; i += 3) { s += u.Tri[i]; n++; }
            return n == 0 ? 0 : s / n;
        }

        // bilinear, BGRA with straight alpha; outside the picture = the nearest edge texel
        static void Sample(Picture t, double c, double r, out double b, out double g, out double rr, out double a)
        {
            c = Math.Max(0, Math.Min(t.Width - 1, c)); r = Math.Max(0, Math.Min(t.Height - 1, r));
            int c0 = (int)c, r0 = (int)r, c1 = Math.Min(t.Width - 1, c0 + 1), r1 = Math.Min(t.Height - 1, r0 + 1);
            double fc = c - c0, fr = r - r0;
            double sb = 0, sg = 0, sr = 0, sa = 0;
            var cs = new[] { c0, c1, c0, c1 }; var rs = new[] { r0, r0, r1, r1 };
            var ws = new[] { (1 - fc) * (1 - fr), fc * (1 - fr), (1 - fc) * fr, fc * fr };
            for (int k = 0; k < 4; k++)
            {
                int o = (rs[k] * t.Width + cs[k]) * 4; double al = t.Bgra[o + 3] / 255.0 * ws[k];
                sb += t.Bgra[o] * al; sg += t.Bgra[o + 1] * al; sr += t.Bgra[o + 2] * al; sa += al;
            }
            if (sa > 1e-6) { sb /= sa; sg /= sa; sr /= sa; }
            b = sb; g = sg; rr = sr; a = sa;
        }

        static void Over(byte[] d, int o, double b, double g, double r, double a)
        {
            double da = d[o + 3] / 255.0, oa = a + da * (1 - a);
            if (oa <= 0) return;
            d[o] = (byte)Math.Round((b * a + d[o] * da * (1 - a)) / oa);
            d[o + 1] = (byte)Math.Round((g * a + d[o + 1] * da * (1 - a)) / oa);
            d[o + 2] = (byte)Math.Round((r * a + d[o + 2] * da * (1 - a)) / oa);
            d[o + 3] = (byte)Math.Round(oa * 255);
        }

        // a block picture from the install (decoded BLP; null = missing or unreadable)
        public static Picture Decode(byte[] blp)
        {
            if (blp == null) return null;
            try { var px = Blp.Decode(blp, out var w, out var h); return px == null || w <= 0 || h <= 0 ? null : new Picture { Bgra = px, Width = w, Height = h }; }
            catch (Exception) { return null; }
        }

        // ---------------------------------------------------------------- files
        public static void WritePng(string path, GameFloor g)
        {
            var bmp = BitmapSource.Create(g.Width, g.Height, 96, 96, PixelFormats.Bgra32, null, g.Bgra, g.Width * 4);
            bmp.Freeze();
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            var tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None)) enc.Save(fs);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        // straight-alpha BGRA back (null = missing, damaged, another size)
        public static byte[] ReadPng(string path, int w, int h)
        {
            try
            {
                BitmapSource src;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    src = new PngBitmapDecoder(fs, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad).Frames[0];
                if (src.PixelWidth != w || src.PixelHeight != h) return null;
                if (src.Format != PixelFormats.Bgra32) src = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
                var px = new byte[w * h * 4];
                src.CopyPixels(px, w * 4, 0);
                return px;
            }
            catch (Exception) { return null; }
        }

        // the look the map card shows: the plan in the theme's colours with the game's pictures over it (any thread)
        public static BitmapSource Blend(byte[] roles, byte[] game, int w, int h, DungeonPlanImage.Colors colors)
        {
            var plan = DungeonPlanImage.Colour(roles, w, h, colors);
            var px = new byte[w * h * 4];
            plan.CopyPixels(px, w * 4, 0);                     // premultiplied BGRA
            for (int i = 0; i < w * h; i++)
            {
                int o = i * 4; double a = game[o + 3] / 255.0;
                if (a <= 0) continue;
                double da = px[o + 3] / 255.0, oa = a + da * (1 - a);
                px[o] = (byte)Math.Round(game[o] * a + px[o] * (1 - a));
                px[o + 1] = (byte)Math.Round(game[o + 1] * a + px[o + 1] * (1 - a));
                px[o + 2] = (byte)Math.Round(game[o + 2] * a + px[o + 2] * (1 - a));
                px[o + 3] = (byte)Math.Round(oa * 255);
            }
            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, px, w * 4);
            bmp.Freeze();
            return bmp;
        }
    }
}
