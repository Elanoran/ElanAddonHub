using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Maps;
using ElansAddonHub.Services.WorldMap;

namespace ElansAddonHub
{
    // --selftest "worldmap": the Atlas World map (2.51). Part 1 (this file): everything that needs no browser - the tile / world math, the WDT reader, the tile
    // pyramid on a fake tile source (counts, missing tiles, cache reuse, resume after a cancel, build-key cleanup, clear), the flight tables (synthetic WDC5
    // tables, an unknown layout, the saved index), the message protocol, the world map ids, "Navigate here", the layers on an invented continent, the chat links.
    // Part 2 (MainWindow.WorldMapTest.Page.cs): the page in a real WebView2 on the same invented continent, and the real game files (read-only) when there are any.
    public partial class MainWindow
    {
        // a continent of 8 x 4 map tiles around the world's origin: x (north) -1000..1000 and y (west) -2000..2000 is where the invented Northland of the mapdata test lies
        static readonly int[] WmCols = Enumerable.Range(28, 8).ToArray(), WmRows = Enumerable.Range(30, 4).ToArray();
        static int WmFdid(int col, int row) => 70000 + row * 64 + col;

        // a WDT as the game writes it: reversed chunk tags (MVER MPHD MAIN MAID), 4096 entries of 8 FileDataIDs each
        static byte[] WmWdt(IEnumerable<Tuple<int, int>> tiles, bool reversedTags = true, int maidSize = 131072, bool withMain = true, bool maid = true, int wdl = 0)
        {
            var ms = new MemoryStream(); var w = new BinaryWriter(ms);
            void Chunk(string tag, byte[] body)
            {
                var t = reversedTags ? new string(tag.Reverse().ToArray()) : tag;
                w.Write(Encoding.ASCII.GetBytes(t)); w.Write(body.Length); w.Write(body);
            }
            Chunk("MVER", BitConverter.GetBytes(18));
            var mphd = new byte[32]; Array.Copy(BitConverter.GetBytes(wdl), 0, mphd, 24, 4);
            Chunk("MPHD", mphd);
            var list = tiles.ToList();
            if (withMain)
            {
                var main = new byte[4096 * 8];
                foreach (var t in list) Array.Copy(BitConverter.GetBytes(1u), 0, main, (t.Item2 * 64 + t.Item1) * 8, 4);
                Chunk("MAIN", main);
            }
            if (maid)
            {
                var m = new byte[maidSize];
                foreach (var t in list)
                {
                    int o = (t.Item2 * 64 + t.Item1) * 32;
                    if (o + 32 > m.Length) continue;
                    Array.Copy(BitConverter.GetBytes((uint)(WmFdid(t.Item1, t.Item2) + 1000000)), 0, m, o, 4);            // root ADT
                    Array.Copy(BitConverter.GetBytes((uint)WmFdid(t.Item1, t.Item2)), 0, m, o + 28, 4);                     // minimap texture
                }
                Chunk("MAID", m);
            }
            return ms.ToArray();
        }

        // a minimap whose colour says which map tile it is (flat, so the JPEG stays within a few levels)
        static MapRender.TileImage WmTile(int col, int row)
        {
            byte r = (byte)(40 + (col * 37) % 180), g = (byte)(60 + (row * 53) % 150), b = (byte)(90 + ((col + row) * 29) % 120);
            return Solid(512, 512, b, g, r);
        }

        static bool WmNear(byte[] bgra, int r, int g, int b, int tol = 7) => Math.Abs(bgra[0] - b) <= tol && Math.Abs(bgra[1] - g) <= tol && Math.Abs(bgra[2] - r) <= tol;
        static byte[] WmPixel(string jpg, int x, int y)
        {
            var buf = new byte[256 * 256 * 4];
            if (!WorldMapPyramid.ReadJpeg(jpg, buf)) return new byte[4];
            return new[] { buf[(y * 256 + x) * 4], buf[(y * 256 + x) * 4 + 1], buf[(y * 256 + x) * 4 + 2], buf[(y * 256 + x) * 4 + 3] };
        }

        // the flight tables of the real layouts, with a few invented rows
        static byte[] WmTaxiNodes(uint layout = 0xE7B597F0)
        {
            var fields = new List<W5Field> { F32(0), new W5Field { OffsetBits = 32, SizeBits = 96 }, new W5Field { OffsetBits = 128, SizeBits = 64 }, new W5Field { OffsetBits = 192, SizeBits = 64 } };
            for (int i = 4; i <= 13; i++) fields.Add(new W5Field { OffsetBits = 256 + (i - 4) * 32, SizeBits = 32 });
            fields.Add(new W5Field { OffsetBits = 576, SizeBits = 64 });
            const int S = 80;
            var rows = new[]
            {   // name, map, x, y, flags, mount horde, mount alliance
                new { n = "Orgrimmar, Durotar", map = 1, x = 1677.6f, y = -4315.7f, fl = 1026, mh = 2224, ma = 0 },
                new { n = "Stormwind, Elwynn", map = 0, x = -8832.8f, y = 478.6f, fl = 1025, mh = 0, ma = 541 },
                new { n = "Ratchet, The Barrens", map = 1, x = -894.6f, y = -3773.0f, fl = 1027, mh = 2224, ma = 541 },
                new { n = "Transport, Orgrimmar Zepplins", map = 1, x = 1320.1f, y = -4649.2f, fl = 1024, mh = 0, ma = 0 },
                new { n = "Powderfuse Port, Riverglades", map = 0, x = -8157.2f, y = -5724.7f, fl = 1024, mh = 2224, ma = 0 },
                new { n = "Quest Path 9571: Shaman", map = 1, x = -5167.3f, y = -2788.5f, fl = 1024, mh = 2224, ma = 2224 },
            };
            var str = new MemoryStream(); str.WriteByte(0);
            var offs = new List<int>();
            foreach (var r in rows) { offs.Add((int)str.Length); var b = Encoding.UTF8.GetBytes(r.n); str.Write(b, 0, b.Length); str.WriteByte(0); }
            var recs = new List<byte[]>();
            for (int i = 0; i < rows.Length; i++)
            {
                var r = rows[i]; var rec = new byte[S];
                void I(int at, int v) => Array.Copy(BitConverter.GetBytes(v), 0, rec, at, 4);
                void Fl(int at, float v) => Array.Copy(BitConverter.GetBytes(v), 0, rec, at, 4);
                I(0, (rows.Length - i) * S + offs[i]); Fl(4, r.x); Fl(8, r.y); Fl(12, 94.4f);
                I(36, r.map); I(48, r.fl); I(72, r.mh); I(76, r.ma);
                recs.Add(rec);
            }
            var sec = new W5Section { Records = recs.ToArray(), Strings = str.ToArray(), Ids = new[] { 23, 2, 80, 35, 3274, 2913 } };
            return BuildWdc5(MapTables.TaxiNodes.TableHash, layout, false, S, fields.ToArray(), new[] { sec });
        }

        static byte[] WmTaxiPaths(uint layout = 0xA303DE51)
        {
            var fields = Enumerable.Range(0, 4).Select(F32).ToArray();
            byte[] Rec(int from, int to, int cost) { var r = new byte[16]; Array.Copy(BitConverter.GetBytes(0), 0, r, 0, 4); Array.Copy(BitConverter.GetBytes(from), 0, r, 4, 4); Array.Copy(BitConverter.GetBytes(to), 0, r, 8, 4); Array.Copy(BitConverter.GetBytes(cost), 0, r, 12, 4); return r; }
            var sec = new W5Section { Records = new[] { Rec(23, 80, 330), Rec(80, 23, 330), Rec(23, 35, 10), Rec(2, 23, 999) }, Ids = new[] { 501, 502, 503, 504 } };
            return BuildWdc5(MapTables.TaxiPath.TableHash, layout, false, 16, fields, new[] { sec });
        }

        static byte[] WmTaxiPoints(uint layout = 0xFE362E70)
        {
            var fields = new List<W5Field> { new W5Field { OffsetBits = 0, SizeBits = 96 } };
            for (int i = 0; i < 8; i++) fields.Add(new W5Field { OffsetBits = 96 + i * 32, SizeBits = 32 });
            byte[] Rec(int path, int idx, int map, float x, float y) { var r = new byte[44]; Array.Copy(BitConverter.GetBytes(x), 0, r, 0, 4); Array.Copy(BitConverter.GetBytes(y), 0, r, 4, 4); Array.Copy(BitConverter.GetBytes(60f), 0, r, 8, 4); Array.Copy(BitConverter.GetBytes(path), 0, r, 16, 4); Array.Copy(BitConverter.GetBytes(idx), 0, r, 20, 4); Array.Copy(BitConverter.GetBytes(map), 0, r, 24, 4); return r; }
            var sec = new W5Section
            {
                Records = new[] { Rec(501, 0, 1, 1677f, -4315f), Rec(501, 1, 1, 400f, -4000f), Rec(501, 2, 1, -300f, -3800f), Rec(501, 3, 1, -894f, -3773f), Rec(503, 0, 1, 1677f, -4315f) },
                Ids = new[] { 9001, 9002, 9003, 9004, 9005 },
            };
            return BuildWdc5(MapTables.TaxiPathNode.TableHash, layout, false, 44, fields.ToArray(), new[] { sec });
        }

        async Task<string> WorldMapTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            void Check(string what, bool cond, string detail = null) { SelfTestClock.Mark(what); notes.Add((cond ? "ok   " : "FAIL ") + what + (!cond && detail != null ? " [" + detail + "]" : "")); ok &= cond; Util.Log("worldmap test " + (cond ? "ok " : "FAIL ") + what.Substring(0, Math.Min(80, what.Length))); }
            string P(string n) => Path.Combine(dir, n);
            var work = Path.Combine(dir, "worldmap");
            var savedRoot = WorldMapCache.RootOverride; var savedWdt = WorldMapService.WdtSourceForTest; var savedTile = WorldMapService.TileSourceForTest;
            var savedCache = MapData.CacheFile; var savedMapRoot = MapData.WowRoot; var savedMapEnabled = MapData.Enabled; var savedLodge = AtlasQuery.Context.Lodge;
            var savedLayers = settings.WorldMapLayers; var savedLast = settings.WorldMapLast;
            try
            {
                if (Directory.Exists(work)) Util.DeleteDir(work);
                Directory.CreateDirectory(work);
                Check("fixture: everything of this test is under the scratch folder (never the real game, never the real data folder)", Path.GetFullPath(work).StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase));
                WorldMapCache.RootOverride = Path.Combine(work, "cache");
                WorldMapService.ResetForTest();

                // ---------------------------------------------------------------- the math
                WorldMapMath.WorldToGrid(0, 0, out var c0, out var r0);
                WorldMapMath.WorldToGrid(WorldMapIds.MaxCoord, WorldMapIds.MaxCoord, out var cNW, out var rNW);
                WorldMapMath.WorldToGrid(-WorldMapIds.MaxCoord, -WorldMapIds.MaxCoord, out var cSE, out var rSE);
                Check("math: the origin is the corner of tiles 31 / 32, the north-west corner of the world (x +17066.67, y +17066.67) is grid (0, 0), the south-east corner (64, 64): west is the left, north the top",
                    c0 == 32 && r0 == 32 && Math.Abs(cNW) < 1e-9 && Math.Abs(rNW) < 1e-9 && Math.Abs(cSE - 64) < 1e-9 && Math.Abs(rSE - 64) < 1e-9, c0 + "," + r0 + " " + cNW + "," + rNW + " " + cSE + "," + rSE);
                WorldMapMath.TileOf(1630, -4373, out var oc, out var orow); WorldMapMath.TileOf(-8913, 554, out var sc, out var srow); WorldMapMath.TileOf(340, -4686, out var rc, out var rr);
                Check("math: Orgrimmar (1630, -4373) is tile (40, 28), Stormwind (-8913, 554) tile (30, 48), Razor Hill (340, -4686) tile (40, 31)", oc == 40 && orow == 28 && sc == 30 && srow == 48 && rc == 40 && rr == 31, oc + "," + orow + " " + sc + "," + srow + " " + rc + "," + rr);
                WorldMapMath.WorldToLatLng(1630, -4373, out var lat, out var lng); WorldMapMath.LatLngToWorld(lat, lng, out var bx, out var by);
                Check("math: the viewer plane is lng = col * 4, lat = -row * 4 (Orgrimmar col 40.1994 / row 28.9406 -> lng 160.80, lat -115.78) and the way back gives the same yards", Math.Abs(lng - 160.7975) < 0.001 && Math.Abs(lat + 115.775) < 0.001 && Math.Abs(bx - 1630) < 1e-6 && Math.Abs(by + 4373) < 1e-6, lat + "," + lng);
                WorldMapMath.PyramidRangeOfAdt(7, 40, 28, out var a0, out var a1, out var a2, out var a3); WorldMapMath.PyramidRangeOfAdt(6, 40, 28, out var b0, out var b1, out var b2, out var b3); WorldMapMath.PyramidRangeOfAdt(0, 40, 28, out var z0, out var z1, out var z2, out var z3);
                Check("math: one map tile is 2 x 2 pyramid tiles at the native level (80..81 / 56..57), one at level 6 (40 / 28) and a quarter-of-nothing of the single tile at level 0",
                    a0 == 80 && a1 == 56 && a2 == 81 && a3 == 57 && b0 == 40 && b1 == 28 && b2 == 40 && b3 == 28 && z0 == 0 && z1 == 0 && z2 == 0 && z3 == 0 && WorldMapMath.TilePath(7, 80, 56) == "z7/80_56.jpg");
                int outsideBad = 0; foreach (var d in new[] { double.NaN, double.PositiveInfinity, 20000, -20000 }) if (WorldMapMath.InWorld(d, 0) || WorldMapMath.InWorld(0, d)) outsideBad++;
                Check("math: NaN, infinity and positions beyond the world are not in the world", outsideBad == 0 && WorldMapMath.InWorld(0, 0) && WorldMapMath.InWorld(16000, -16000));

                // ---------------------------------------------------------------- the WDT
                var tiles = WmCols.SelectMany(c => WmRows.Select(r => Tuple.Create(c, r))).ToList();
                var wdt = WdtFile.Parse(WmWdt(tiles));
                Check("wdt: the MAID table (reversed chunk tags) gives every tile with its root and minimap FileDataID at index row * 64 + col; MAIN agrees (32 tiles)",
                    wdt.Ok && wdt.Tiles.Count == 32 && wdt.MainTiles == 32 && wdt.WithMinimap == 32 && wdt.Tiles.All(t => t.MinimapFdid == WmFdid(t.Col, t.Row) && t.RootFdid == WmFdid(t.Col, t.Row) + 1000000), wdt.Problem + " " + wdt.Tiles.Count);
                var wdtPlain = WdtFile.Parse(WmWdt(tiles, reversedTags: false));
                Check("wdt: chunk tags written the straight way read the same", wdtPlain.Ok && wdtPlain.Tiles.Count == 32);
                Check("wdt: MPHD's WDL FileDataID (+24, the low-resolution heights) is read; a WDT without one says 0", WdtFile.Parse(WmWdt(tiles, wdl: 7777)).WdlFdid == 7777 && wdt.WdlFdid == 0);
                var noMaid = WdtFile.Parse(WmWdt(tiles, maid: false));
                var badSize = WdtFile.Parse(WmWdt(tiles, maidSize: 4096));
                var cutOff = WdtFile.Parse(WmWdt(tiles).Take(5000).ToArray());
                var junk = WdtFile.Parse(new byte[] { 1, 2, 3 });
                Check("wdt: no MAID chunk, a MAID of the wrong size, a cut-off file and junk are each refused with a reason (nothing guessed)", !noMaid.Ok && !badSize.Ok && !cutOff.Ok && !junk.Ok && noMaid.Problem.Contains("MAID") && badSize.Problem.Contains("size") && cutOff.Problem.Contains("cut"), noMaid.Problem + "|" + badSize.Problem + "|" + cutOff.Problem);
                var withHole = WdtFile.Parse(WmWdt(tiles.Where(t => !(t.Item1 == 30 && t.Item2 == 31)).Concat(new[] { Tuple.Create(50, 50) })));
                Check("wdt: a tile without files is not listed and a tile far away is", withHole.Tiles.Count == 32 && !withHole.Tiles.Any(t => t.Col == 30 && t.Row == 31) && withHole.Tiles.Any(t => t.Col == 50 && t.Row == 50));
                var land = Enumerable.Range(10, 6).SelectMany(c => Enumerable.Range(10, 5).Select(r => new WdtTile { Col = c, Row = r, MinimapFdid = 1 })).ToList();
                var far = Enumerable.Range(0, 3).SelectMany(c => Enumerable.Range(0, 3).Select(r => new WdtTile { Col = c, Row = r, MinimapFdid = 1 })).ToList();
                var island = new[] { new WdtTile { Col = 18, Row = 12, MinimapFdid = 1 }, new WdtTile { Col = 19, Row = 12, MinimapFdid = 1 } };
                var removed = new List<WdtTile>();
                var kept = WdtFile.WithoutStrays(land.Concat(far).Concat(island).ToList(), 12, removed);
                Check("wdt: a 3 x 3 block in the far corner (Kalimdor's test leftovers) is a stray and left out, a small island near the land stays", removed.Count == 9 && kept.Count == 32 && kept.Any(t => t.Col == 18) && !kept.Any(t => t.Col < 3), removed.Count + "/" + kept.Count);

                // ---------------------------------------------------------------- the pyramid on a fake source
                var pdir = Path.Combine(work, "pyr");
                var four = new[] { new WdtTile { Col = 10, Row = 20, MinimapFdid = 1 }, new WdtTile { Col = 11, Row = 20, MinimapFdid = 2 }, new WdtTile { Col = 10, Row = 21, MinimapFdid = 3 }, new WdtTile { Col = 11, Row = 21, MinimapFdid = 4 } };
                int loads = 0;
                var colorOf = new Dictionary<int, Tuple<int, int, int>> { [1] = Tuple.Create(200, 40, 40), [2] = Tuple.Create(40, 200, 40), [3] = Tuple.Create(40, 40, 200), [4] = Tuple.Create(220, 220, 40) };
                MapRender.TileImage Load(WdtTile t) { Interlocked.Increment(ref loads); if (t.MinimapFdid == 99) return null; var c = colorOf[t.MinimapFdid]; return Solid(512, 512, (byte)c.Item3, (byte)c.Item2, (byte)c.Item1); }
                var progress = new List<PyramidProgress>();
                var res = WorldMapPyramid.Build(5, "Testland", "keyA", four, Load, pdir, p => progress.Add(p), CancellationToken.None);
                int n7 = Directory.GetFiles(Path.Combine(pdir, "z7")).Length, n6 = Directory.GetFiles(Path.Combine(pdir, "z6")).Length, n5 = Directory.GetFiles(Path.Combine(pdir, "z5")).Length;
                Check("pyramid: 4 map tiles of 512 px make 16 native tiles (level 7), 4 at level 6, 1 at level 5 and 1 on every level above (levels 0-4); counts, bounds and the manifest agree",
                    res.Complete && res.Decoded == 4 && res.Missing == 0 && n7 == 16 && n6 == 4 && n5 == 1 && Enumerable.Range(0, 5).All(l => Directory.GetFiles(Path.Combine(pdir, "z" + l)).Length == 1) && res.Files == 16 + 4 + 1 + 5
                    && res.MinCol == 10 && res.MaxCol == 11 && res.MinRow == 20 && res.MaxRow == 21, "z7 " + n7 + " z6 " + n6 + " z5 " + n5 + " files " + res.Files);
                var man = WorldMapPyramid.ReadManifest(pdir);
                Check("pyramid: manifest.json is written last and holds the key, the map, the tile bounds, the file count and the sea colour", man != null && man.Valid && man.BuildKey == "keyA" && man.MapId == 5 && man.Name == "Testland" && man.Adt == 4 && man.Files == res.Files && man.Bytes > 0 && man.Sea == WorldMapPyramid.SeaHex && man.MinCol == 10 && man.MaxRow == 21 && man.NativeLevel == 7);
                // pixels: level 7 tile (20,40) is the left-top quarter of map tile (10,20); level 5 tile (5,10) shows the four map tiles in its four quadrants
                Check("pyramid: native tiles carry the minimap pixels (red, green, blue, yellow tile at their places) within JPEG tolerance",
                    WmNear(WmPixel(WorldMapPyramid.TileFile(pdir, 7, 20, 40), 100, 100), 200, 40, 40) && WmNear(WmPixel(WorldMapPyramid.TileFile(pdir, 7, 23, 40), 5, 250), 40, 200, 40) && WmNear(WmPixel(WorldMapPyramid.TileFile(pdir, 7, 20, 43), 128, 128), 40, 40, 200) && WmNear(WmPixel(WorldMapPyramid.TileFile(pdir, 7, 23, 43), 250, 250), 220, 220, 40));
                Check("pyramid: level 6 has one tile per map tile and level 5 halves four of them into its quadrants (no seams, no shift)",
                    WmNear(WmPixel(WorldMapPyramid.TileFile(pdir, 6, 10, 20), 100, 100), 200, 40, 40) && WmNear(WmPixel(WorldMapPyramid.TileFile(pdir, 5, 5, 10), 64, 64), 200, 40, 40) && WmNear(WmPixel(WorldMapPyramid.TileFile(pdir, 5, 5, 10), 192, 64), 40, 200, 40)
                    && WmNear(WmPixel(WorldMapPyramid.TileFile(pdir, 5, 5, 10), 64, 192), 40, 40, 200) && WmNear(WmPixel(WorldMapPyramid.TileFile(pdir, 5, 5, 10), 192, 192), 220, 220, 40));
                Check("pyramid: progress is reported (tiles, then levels, counting up to the total)", progress.Count >= 2 && progress.First().Phase == "tiles" && progress.Last().Phase == "levels" && progress.Last().Done == progress.Last().Total && progress.Last().Total == 4 + 4 + 1 + 5 && progress.Zip(progress.Skip(1), (a, b) => b.Done >= a.Done).All(x => x));

                // cache reuse: a second build decodes nothing and writes nothing
                loads = 0;
                var again = WorldMapPyramid.Build(5, "Testland", "keyA", four, Load, pdir, null, CancellationToken.None);
                Check("pyramid: a second build finds every tile already there: nothing is read from the source, nothing is written, the manifest is back", loads == 0 && again.Decoded == 0 && again.Reused == 4 && again.Written == 0 && again.Skipped == 10 && again.Complete && WorldMapPyramid.ReadManifest(pdir) != null, loads + " " + again.Decoded + " " + again.Reused + " " + again.Written);

                // 2.70: a map tile the WDT lists but the local storage does not have yet (a Battle.net install still downloading in the background): a hatched stand-in, never
                // the near-black sea, marked as not final (a ".low" marker + the manifest's pending list) - before 2.70 it was a black hole cached for good
                var pdir2 = Path.Combine(work, "pyr2");
                var withGap = four.Take(3).Concat(new[] { new WdtTile { Col = 11, Row = 21, MinimapFdid = 99 } }).ToArray();
                var gap = WorldMapPyramid.Build(5, "Testland", "keyA", withGap, Load, pdir2, null, CancellationToken.None, null, "stamp1");
                var gman = WorldMapPyramid.ReadManifest(pdir2);
                var hatchPx = WmPixel(WorldMapPyramid.TileFile(pdir2, 7, 22, 42), 100, 10); var stripePx = WmPixel(WorldMapPyramid.TileFile(pdir2, 7, 22, 42), 1, 0);
                bool NotSea(byte[] p) => !WmNear(p, WorldMapPyramid.SeaR, WorldMapPyramid.SeaG, WorldMapPyramid.SeaB, 4);
                Check("pyramid: a map tile the local storage does not have yet gets a hatched stand-in on the theme's surfaces (Surface2 #1E2226 with Surface3 stripes), not the near-black sea; it is counted as missing and listed as pending (missing in local storage, shown as a placeholder) with a marker next to it",
                    gap.Complete && gap.Missing == 1 && gap.Decoded == 3 && gap.Pending.Count == 1 && Directory.GetFiles(Path.Combine(pdir2, "z7"), "*.jpg").Length == 16 && File.Exists(WorldMapPyramid.MarkerFile(pdir2, 11, 21))
                    && WmNear(hatchPx, 0x1E, 0x22, 0x26, 6) && stripePx[0] + stripePx[1] + stripePx[2] > hatchPx[0] + hatchPx[1] + hatchPx[2] + 6 && NotSea(WmPixel(WorldMapPyramid.TileFile(pdir2, 5, 5, 10), 200, 200))
                    && gman != null && gman.Pending.Count == 1 && gman.Pending[0].Col == 11 && gman.Pending[0].Row == 21 && gman.Pending[0].Reason == "missing" && gman.Pending[0].Shown == "plain" && gman.Stamp == "stamp1",
                    gap.Missing + " " + gap.Decoded + " px " + string.Join(",", hatchPx) + " / " + string.Join(",", stripePx));
                // still not there: the stand-in stays as it is (nothing rewritten), it is tried again each time
                loads = 0; var still = WorldMapPyramid.Build(5, "Testland", "keyA", withGap, Load, pdir2, null, CancellationToken.None);
                Check("pyramid: building again with the tile still missing reads only that tile (" + loads + " read), rewrites nothing and keeps it pending",
                    still.Complete && loads == 1 && still.Written == 0 && still.Pending.Count == 1 && still.Reused == 3 && File.Exists(WorldMapPyramid.MarkerFile(pdir2, 11, 21)), loads + " w" + still.Written);
                // the download finished: a retry reads the real minimap, the stand-in and its marker go, the zoom levels above it are made again
                loads = 0; var fixedTile = four[3];
                var retried = WorldMapPyramid.Build(5, "Testland", "keyA", four.Take(3).Concat(new[] { fixedTile }).ToArray(), Load, pdir2, null, CancellationToken.None);
                var rman = WorldMapPyramid.ReadManifest(pdir2);
                Check("pyramid: once the tile is readable the next build replaces the stand-in (1 recovered): the real minimap (yellow) at the native level and in the level-5 quadrant above it, no marker, nothing pending in the manifest",
                    retried.Complete && retried.Recovered == 1 && loads == 1 && retried.Pending.Count == 0 && !File.Exists(WorldMapPyramid.MarkerFile(pdir2, 11, 21)) && rman != null && rman.Pending.Count == 0
                    && WmNear(WmPixel(WorldMapPyramid.TileFile(pdir2, 7, 23, 43), 250, 250), 220, 220, 40) && WmNear(WmPixel(WorldMapPyramid.TileFile(pdir2, 5, 5, 10), 192, 192), 220, 220, 40), retried.Recovered + " " + loads);
                // a stand-in from the continent's own art (the Atlas world map picture of that square), under a light hatch
                var pdirArt = Path.Combine(work, "pyrArt");
                var withArt = WorldMapPyramid.Build(5, "Testland", "keyA", withGap, Load, pdirArt, null, CancellationToken.None, t => Solid(512, 512, 40, 160, 90));
                var artPx = WmPixel(WorldMapPyramid.TileFile(pdirArt, 7, 22, 42), 100, 10);
                Check("pyramid: with the continent's map art there, the stand-in is that art (a little darker, under a light hatch) and listed as \"continent map art\"",
                    withArt.Pending.Count == 1 && withArt.Pending[0].Shown == "art" && WmNear(artPx, 90 * 4 / 5, 160 * 4 / 5, 40 * 4 / 5, 6) && withArt.Pending[0].ShownText == "continent map art", string.Join(",", artPx));
                // a minimap the install lists but cannot give (a game still downloading in the background: "not a BLTE block"): a stand-in too, the build goes on
                var pdir2b = Path.Combine(work, "pyr2b");
                var broken = WorldMapPyramid.Build(5, "Testland", "keyA", four, t => t == four[1] ? throw new IOException("not a BLTE block") : Load(t), pdir2b, null, CancellationToken.None);
                var bman = WorldMapPyramid.ReadManifest(pdir2b);
                Check("pyramid: a minimap that cannot be read (not a BLTE block) gets a stand-in and is listed as a read error with the reason; the rest of the continent is built",
                    broken.Complete && broken.Missing == 1 && broken.Unreadable == 1 && broken.Decoded == 3 && bman != null && bman.Pending.Count == 1 && bman.Pending[0].Reason == "unreadable" && bman.Pending[0].Detail.Contains("BLTE") && bman.Pending[0].ReasonText.StartsWith("read error"), broken.Missing + " " + broken.Unreadable + " " + broken.Decoded);
                Check("pyramid: the page's reason for an unreadable game file says what to do (let Battle.net finish, or Scan and Repair)",
                    WorldMapService.Why(new IOException("not a BLTE block")).Contains("Scan and Repair") && WorldMapService.Why(new IOException("disk full")) == "disk full");
                var none = Assert(() => WorldMapPyramid.Build(5, "Testland", "keyA", new[] { new WdtTile { Col = 1, Row = 1, MinimapFdid = 99 } }, Load, Path.Combine(work, "pyr3"), null, CancellationToken.None));
                Check("pyramid: when none of the minimaps can be read the build fails with a reason and no manifest", none != null && none.Contains("none of the") && !File.Exists(WorldMapPyramid.ManifestPath(Path.Combine(work, "pyr3"))), none);

                // cancel: finished tiles stay, no manifest; the next build only reads the rest
                var pdir4 = Path.Combine(work, "pyr4");
                var cts = new CancellationTokenSource(); int seen = 0;
                var cancelled = Assert(() => WorldMapPyramid.Build(5, "Testland", "keyA", four, t => { if (Interlocked.Increment(ref seen) == 3) cts.Cancel(); return Load(t); }, pdir4, null, cts.Token));
                Check("pyramid: cancelling stops between map tiles (OperationCanceledException), leaves the finished files and no manifest", cancelled != null && cancelled.StartsWith("OperationCanceledException") && Directory.GetFiles(Path.Combine(pdir4, "z7")).Length == 12 && !File.Exists(WorldMapPyramid.ManifestPath(pdir4)), cancelled);
                loads = 0;
                var resumed = WorldMapPyramid.Build(5, "Testland", "keyA", four, Load, pdir4, null, CancellationToken.None);
                Check("pyramid: the build after a cancel resumes: it reads only the one map tile that was left", resumed.Complete && loads == 1 && resumed.Decoded == 1 && resumed.Reused == 3 && Directory.GetFiles(Path.Combine(pdir4, "z7")).Length == 16, loads + " " + resumed.Reused);
                var tmps = Directory.GetFiles(pdir4, "*.tmp", SearchOption.AllDirectories);
                Check("pyramid: no half-written .tmp file is left behind", tmps.Length == 0);
                // a damaged tile (a cut-off file) is not a tile: it is made again
                var dmg = WorldMapPyramid.TileFile(pdir4, 7, 21, 41); File.WriteAllBytes(dmg, new byte[0]);
                loads = 0; var fixedUp = WorldMapPyramid.Build(5, "Testland", "keyA", four, Load, pdir4, null, CancellationToken.None);
                Check("pyramid: an empty (damaged) tile file counts as missing: that map tile is read again and the file is back", fixedUp.Complete && loads == 1 && new FileInfo(dmg).Length > 100, loads + "");

                // build-key folders, size, clear
                WorldMapCache.RootOverride = Path.Combine(work, "cache2");
                Directory.CreateDirectory(Path.Combine(WorldMapCache.Root, "1.60.1.70000-aaaaaaaa_p1", "1")); File.WriteAllBytes(Path.Combine(WorldMapCache.Root, "1.60.1.70000-aaaaaaaa_p1", "1", "x.jpg"), new byte[1000]);
                Directory.CreateDirectory(Path.Combine(WorldMapCache.Root, "1.60.1.70338-bbbbbbbb_p1", "1")); File.WriteAllBytes(Path.Combine(WorldMapCache.Root, "1.60.1.70338-bbbbbbbb_p1", "1", "manifest.json"), Encoding.UTF8.GetBytes("{}"));
                Directory.CreateDirectory(Path.Combine(WorldMapCache.Root, WorldMapCache.PageFolder)); File.WriteAllBytes(Path.Combine(WorldMapCache.Root, WorldMapCache.PageFolder, "index.html"), new byte[10]);
                var key = WorldMapCache.KeyFor("1.60.1.70338-bbbbbbbb");
                int gone = WorldMapCache.RemoveOtherBuilds(key);
                Check("cache: the key is the game build + the picture version; folders of other builds are deleted, the current build's and the page's are kept",
                    key == "1.60.1.70338-bbbbbbbb_p" + WorldMapPyramid.Version && gone == 1 && !Directory.Exists(Path.Combine(WorldMapCache.Root, "1.60.1.70000-aaaaaaaa_p1")) && Directory.Exists(Path.Combine(WorldMapCache.Root, key)) && Directory.Exists(Path.Combine(WorldMapCache.Root, WorldMapCache.PageFolder)), gone + "");
                Check("cache: the size counts the pictures and not the page; the text says how much", WorldMapCache.SizeBytes() == 2 && WorldMapCache.SizeText(2) == "1 KB" && WorldMapCache.SizeText(5 * 1048576L) == "5 MB" && WorldMapCache.SizeText(0) == "empty" && WorldMapCache.SizeText(3L * 1073741824) == "3.0 GB", WorldMapCache.SizeBytes() + "");
                long freed = WorldMapCache.Clear();
                Check("cache: Clear removes every picture and keeps the page's files; the size is then 0", freed == 2 && WorldMapCache.SizeBytes() == 0 && Directory.Exists(Path.Combine(WorldMapCache.Root, WorldMapCache.PageFolder)) && WorldMapCache.StatusLine().Contains("none made yet"), freed + "");
                WorldMapCache.RootOverride = Path.Combine(work, "cache");

                // ---------------------------------------------------------------- the builder over a fake game (WDT + minimaps)
                WorldMapService.WdtSourceForTest = mapId => WmWdt(tiles);
                WorldMapService.TileSourceForTest = fdid => WmTile((fdid - 70000) % 64, (fdid - 70000) / 64);
                var fakeRes = WorldMapService.Build(0, "Northland", 7001, "keyZ", Path.Combine(work, "svc"), null, CancellationToken.None, null);
                Check("service: a continent is built from its WDT and the tiles it names (32 map tiles, 128 native files)", fakeRes.Complete && fakeRes.Adt == 32 && fakeRes.Decoded == 32 && Directory.GetFiles(Path.Combine(work, "svc", "z7")).Length == 128 && fakeRes.MinCol == 28 && fakeRes.MaxCol == 35 && fakeRes.MinRow == 30 && fakeRes.MaxRow == 33);
                var region = WorldMapService.Build(0, "Northland", 7001, "keyZ", Path.Combine(work, "svc2"), t => t.Col <= 29 && t.Row == 30, CancellationToken.None, null);
                Check("service: a build can be limited to a region (2 map tiles)", region.Complete && region.Adt == 2 && Directory.GetFiles(Path.Combine(work, "svc2", "z7")).Length == 8);

                // ---------------------------------------------------------------- the flight tables
                var raw = RawWith(Raw("UiMap", UiMapTable()), Raw("TaxiNodes", WmTaxiNodes()), Raw("TaxiPath", WmTaxiPaths()), Raw("TaxiPathNode", WmTaxiPoints()));
                var set = MapTables.Parse(raw);
                var orgr = set.TaxiNodes.FirstOrDefault(n => n.Id == 23);
                Check("taxi: TaxiNodes / TaxiPath / TaxiPathNode of the verified layouts are read: 6 nodes (names, map, world position x north / y west, flags, mounts), 4 paths, 5 path points",
                    set.Ok && set.HasTaxi && set.TaxiNodes.Count == 6 && set.TaxiPaths.Count == 4 && set.TaxiPoints.Count == 5 && orgr != null && orgr.Name == "Orgrimmar, Durotar" && orgr.MapId == 1 && Math.Abs(orgr.X - 1677.6) < 0.01 && Math.Abs(orgr.Y + 4315.7) < 0.01
                    && orgr.Flags == 1026 && orgr.MountHorde == 2224 && orgr.MountAlliance == 0 && set.TaxiPaths[0].FromNode == 23 && set.TaxiPaths[0].ToNode == 80 && set.TaxiPaths[0].Cost == 330 && set.TaxiPoints[1].PathId == 501 && set.TaxiPoints[1].Index == 1 && Math.Abs(set.TaxiPoints[1].X - 400) < 0.01,
                    set.HasTaxi + " n" + set.TaxiNodes.Count + " p" + set.TaxiPaths.Count + " q" + set.TaxiPoints.Count + " orgr " + orgr?.Name + " map " + orgr?.MapId + " x " + orgr?.X + " y " + orgr?.Y + " fl " + orgr?.Flags + " mh " + orgr?.MountHorde + " ma " + orgr?.MountAlliance + " path0 " + set.TaxiPaths.FirstOrDefault()?.FromNode + ">" + set.TaxiPaths.FirstOrDefault()?.ToNode + " c " + set.TaxiPaths.FirstOrDefault()?.Cost + " pt1 " + set.TaxiPoints.ElementAtOrDefault(1)?.PathId + "/" + set.TaxiPoints.ElementAtOrDefault(1)?.Index + "/" + set.TaxiPoints.ElementAtOrDefault(1)?.X);
                var fm = set.TaxiNodes.Where(WorldMapLayers.IsFlightMaster).Select(n => n.Id + WorldMapLayers.FactionOf(n)).ToList();
                Check("taxi: flight masters are the nodes with a faction (flag bits, or the mount it names): Orgrimmar horde, Stormwind alliance, Ratchet both, a node with only a Horde mount (Powderfuse) horde; transports, quest paths and nodes with no faction are not",
                    fm.OrderBy(x => x).SequenceEqual(new[] { "23h", "2a", "3274h", "80n" }.OrderBy(x => x)), string.Join(",", fm));
                Check("taxi: times read \"2m 10s\", \"45s\", \"3m\" and nothing for 0 / NaN", WorldMapLayers.TimeText(130) == "2m 10s" && WorldMapLayers.TimeText(45) == "45s" && WorldMapLayers.TimeText(180) == "3m" && WorldMapLayers.TimeText(0) == "" && WorldMapLayers.TimeText(double.NaN) == "");
                var rawUnk = RawWith(Raw("UiMap", UiMapTable()), Raw("TaxiNodes", WmTaxiNodes()), Raw("TaxiPath", WmTaxiPaths(0xDEADBEEF)), Raw("TaxiPathNode", WmTaxiPoints()));
                var unk = MapTables.Parse(rawUnk);
                Check("taxi: a flight table in a layout this version does not know = no flight masters or routes, with a note and the rest of the maps intact (nothing guessed)", unk.Ok && !unk.HasTaxi && unk.TaxiNodes.Count == 0 && unk.UiMaps.Count == 3 && unk.Notes.Any(x => x.Contains("flight")) && unk.Notes.Any(x => x.Contains("layout")), string.Join("; ", unk.Notes));
                var rawNull = RawWith(Raw("UiMap", UiMapTable())); rawNull["TaxiNodes"] = null; rawNull["TaxiPath"] = null; rawNull["TaxiPathNode"] = null;
                var missing = MapTables.Parse(rawNull); var silent = MapTables.Parse(RawWith(Raw("UiMap", UiMapTable())));
                Check("taxi: tables that are not in the install say so (a note); a raw set that does not mention them at all stays silent", !missing.HasTaxi && missing.Notes.Any(x => x.Contains("flight")) && !silent.HasTaxi && !silent.Notes.Any(x => x.Contains("flight")));
                var foreign = MapTables.Parse(RawWith(Raw("UiMap", UiMapTable()), Raw("TaxiNodes", WmTaxiPaths()), Raw("TaxiPath", WmTaxiPaths()), Raw("TaxiPathNode", WmTaxiPoints())));
                Check("taxi: a table that is another table (wrong table hash) is not read", !foreign.HasTaxi && foreign.Ok);
                set.Maps.Add(new GameMapInfo { Id = 1, Name = "Kalimdor", Directory = "Kalimdor", WdtFileDataId = 782779 });
                var idxFile = Path.Combine(work, "idx", "mapdata.bin");
                Check("taxi: the saved index keeps the flight tables and the maps' WDT FileDataIDs (round trip)", MapIndexFile.TryWrite(idxFile, "B1", set));
                var back = MapIndexFile.TryRead(idxFile);
                Check("taxi: ... read back the same: 6 nodes, 4 paths, 5 points, HasTaxi, Kalimdor's WDT 782779, the node's name and position", back != null && back.Set.HasTaxi && back.Set.TaxiNodes.Count == 6 && back.Set.TaxiPaths.Count == 4 && back.Set.TaxiPoints.Count == 5 && back.Set.Maps.Any(m => m.Id == 1 && m.WdtFileDataId == 782779)
                    && back.Set.TaxiNodes[0].Name == "Orgrimmar, Durotar" && Math.Abs(back.Set.TaxiNodes[0].Y + 4315.7) < 0.01 && MapData.FormatVersion >= 3);

                // ---------------------------------------------------------------- the message protocol
                string Refused(string json) { WorldMapProtocol.Parse(json, out var why); return why; }
                WorldMapMessage Ok(string json) => WorldMapProtocol.Parse(json, out _);
                var view = Ok("{\"t\":\"view\",\"lat\":-115.8,\"lng\":160.8,\"z\":5.25}"); var nav = Ok("{\"t\":\"nav\",\"lat\":-100,\"lng\":128}"); var open = Ok("{\"t\":\"open\",\"id\":\"npc:3321\"}");
                var lay = Ok("{\"t\":\"layers\",\"on\":[\"zones\",\"nodes\",\"nodes.herbs\",\"zones\"]}"); var need = Ok("{\"t\":\"need\",\"layer\":\"vendors\"}"); var ready = Ok("{\"t\":\"ready\",\"tiles\":12,\"errors\":3}");
                Check("protocol: valid messages parse (view, nav, open, layers without duplicates, need, ready, hello)", view != null && view.Type == WorldMapMessageType.View && view.Z == 5.25 && nav != null && nav.Type == WorldMapMessageType.Nav && open != null && open.Id == "npc:3321"
                    && lay != null && lay.On.Count == 3 && need != null && need.Layer == "vendors" && ready != null && ready.Tiles == 12 && ready.Errors == 3 && Ok("{\"t\":\"hello\"}") != null && Ok("{\"t\":\"inited\"}") != null);
                var bad = new[]
                {
                    "", "   ", "not json", "[1,2]", "42", "{\"t\":5}", "{}", "{\"t\":\"open\"}", "{\"t\":\"unknown\"}", "{\"t\":\"eval\",\"js\":\"alert(1)\"}",
                    "{\"t\":\"view\",\"lat\":1e9,\"lng\":5,\"z\":3}", "{\"t\":\"view\",\"lat\":-10,\"lng\":-5,\"z\":3}", "{\"t\":\"view\",\"lat\":10,\"lng\":5,\"z\":3}", "{\"t\":\"view\",\"lat\":-10,\"lng\":5,\"z\":12}", "{\"t\":\"view\",\"lat\":-10,\"lng\":5}",
                    "{\"t\":\"view\",\"lat\":\"-10\",\"lng\":5,\"z\":3}", "{\"t\":\"nav\",\"lat\":-300,\"lng\":5}", "{\"t\":\"nav\",\"lat\":-10,\"lng\":999}", "{\"t\":\"nav\",\"lat\":null,\"lng\":4}",
                    "{\"t\":\"open\",\"id\":\"../../evil\"}", "{\"t\":\"open\",\"id\":\"npc:0\"}", "{\"t\":\"open\",\"id\":\"npc:1234567890\"}", "{\"t\":\"open\",\"id\":\"talents:tree/hunter\"}", "{\"t\":\"open\",\"id\":\"npc:5 \"}", "{\"t\":\"open\",\"id\":\"http://x.example/\"}", "{\"t\":\"open\",\"id\":\"npc:7\\nnpc:8\"}",
                    "{\"t\":\"layers\",\"on\":\"zones\"}", "{\"t\":\"layers\",\"on\":[\"zones\",\"hacks\"]}", "{\"t\":\"layers\",\"on\":[\"nodes.coal\"]}", "{\"t\":\"need\",\"layer\":\"nodes.herbs\"}", "{\"t\":\"need\",\"layer\":\"evil\"}",
                    "{\"t\":\"layers\",\"on\":[" + string.Join(",", Enumerable.Repeat("\"zones\"", 40)) + "]}", "{\"t\":\"hello\",\"pad\":\"" + new string('x', 5000) + "\"}",
                };
                var notRefused = bad.Where(b => Refused(b) == null).ToList();
                Check("protocol: " + bad.Length + " bad messages are all refused with a reason (empty, not JSON, wrong types, unknown actions, eval, positions outside the plane, zoom outside 0-9, a link that is not an entity id, unknown layers, too long)", notRefused.Count == 0, string.Join(" | ", notRefused.Take(3)));
                Check("protocol: only entity links may be opened and the layer names are a fixed list", WorldMapProtocol.IsOpenId("item:2589") && WorldMapProtocol.IsOpenId("zone:1411") && WorldMapProtocol.IsOpenId("instance:36") && !WorldMapProtocol.IsOpenId("browse:items") && !WorldMapProtocol.IsOpenId("worldmap:1") && WorldMapIds.IsLayerId("nodes.fishing") && !WorldMapIds.IsLayerId("nodes.") && WorldMapIds.LayerIds.Length == 13 && WorldMapIds.IsLayerId("instances.raids") && WorldMapIds.IsLayerId("instances.dungeons") && !WorldMapIds.IsLayerId("instances.") && !WorldMapIds.IsLayerId("instances.battlegrounds") && WorldMapIds.IsLayerId("borders"));
                Check("protocol: the app's own messages are plain JSON (toast, goto with a pin)", WorldMapProtocol.Toast("Copied", true) == "{\"t\":\"toast\",\"text\":\"Copied\",\"ok\":true}" && WorldMapProtocol.Goto(-115.8, 160.8, 5, "x").Contains("\"pin\""));
                Check("protocol: a view names the app's last goto (\"s\", a whole number >= 0; anything else counts as none) and a goto can carry it",
                    WorldMapProtocol.Parse("{\"t\":\"view\",\"lat\":-10,\"lng\":5,\"z\":3,\"s\":3}", out _)?.Seq == 3 && WorldMapProtocol.Parse("{\"t\":\"view\",\"lat\":-10,\"lng\":5,\"z\":3}", out _)?.Seq == -1
                    && WorldMapProtocol.Parse("{\"t\":\"view\",\"lat\":-10,\"lng\":5,\"z\":3,\"s\":\"3\"}", out _)?.Seq == -1 && WorldMapProtocol.Parse("{\"t\":\"view\",\"lat\":-10,\"lng\":5,\"z\":3,\"s\":-2}", out _)?.Seq == -1
                    && WorldMapProtocol.Parse("{\"t\":\"view3\",\"lat\":-120,\"lng\":130,\"dist\":500,\"az\":0,\"tilt\":45,\"s\":7}", out _)?.Seq == 7 && WorldMapProtocol.Goto(-115.8, 160.8, 5, null, null, 4).Contains("\"s\":4") && !WorldMapProtocol.Goto(-115.8, 160.8, 5).Contains("\"s\""), "");

                // ---------------------------------------------------------------- ids
                int? Slug(string s) => s == "kalimdor" ? 1 : s == "eastern-kingdoms" || s == "azeroth" ? 0 : (int?)null;
                var t1 = WorldMapIds.Parse("kalimdor", Slug); var t2 = WorldMapIds.Parse("1?x=1630&y=-4373&z=5", Slug); var t3 = WorldMapIds.Parse("", Slug); var t4 = WorldMapIds.Parse("1?x=1630&y=-4373&z=5.1234&p=1", Slug); var t5 = WorldMapIds.Parse("/Eastern-Kingdoms/", Slug);
                Check("ids: \"kalimdor\" is the continent by slug, \"1?x=&y=&z=\" a view in world yards, \"\" as it was left, p=1 puts a ring on the spot, case and slashes are forgiven",
                    t1.Valid && t1.MapId == 1 && t1.Slug == "kalimdor" && !t1.HasView && t2.Valid && t2.MapId == 1 && t2.HasView && t2.X == 1630 && t2.Y == -4373 && t2.Z == 5 && t3.Valid && t3.MapId == -1 && t4.Pin && t4.Z == 5.12 && t5.Valid && t5.MapId == 0);
                var badIds = new[] { "mars", "1?x=1630", "1?y=5", "?x=1&y=2", "1?x=abc&y=2", "1?x=99999&y=2", "1?x=1&y=-99999", "1?x=1&y=2&z=10", "1?x=1&y=2&z=-1", "1?x=NaN&y=2", "kali mdor", "1?x", "123456", new string('a', 300), "1?x=1&y=2&z=1e400" };
                var accepted = badIds.Where(b => WorldMapIds.Parse(b, Slug).Valid).ToList();
                Check("ids: unknown continents, half a position, positions outside the world (|x|, |y| > 17,066.67), zoom outside 0-9, NaN, spaces and very long text are refused", accepted.Count == 0, string.Join(" | ", accepted));
                Check("ids: the canonical id keeps the slug for a bare continent and is \"<map>?x=&y=&z=\" (whole yards, zoom to two decimals) for a view, ring included",
                    WorldMapIds.Id(1, "kalimdor", false, 0, 0, 0) == "worldmap:kalimdor" && WorldMapIds.Id(1, null, true, 1630.4, -4372.6, 5) == "worldmap:1?x=1630&y=-4373&z=5" && WorldMapIds.IdAt(0, -8913, 554, 5.256) == "worldmap:0?x=-8913&y=554&z=5.26&p=1" && WorldMapIds.Id(-1, null, false, 0, 0, 0) == "worldmap:");
                Check("ids: the chat-link check (no continent list) accepts a map id or a slug with a good view and nothing else",
                    WorldMapIds.CanonicalSyntax("kalimdor") == "kalimdor" && WorldMapIds.CanonicalSyntax("1?x=1630&y=-4373&z=5") == "1?x=1630&y=-4373&z=5" && WorldMapIds.CanonicalSyntax("1?x=999999&y=0") == null && WorldMapIds.CanonicalSyntax("") == null && WorldMapIds.CanonicalSyntax("a b") == null);
                Check("ids: worldmap: is a hosted category's prefix: \"worldmap:1?x=1&y=2&z=3\" splits into the category and its sub-path (a Back / Forward step like talents: and professions:)",
                    AtlasCategories.TryHosted("worldmap:1?x=1630&y=-4373&z=5", out var wmCat, out var wmSub) && wmCat.Id == "worldmap" && wmSub == "1?x=1630&y=-4373&z=5" && AtlasCategories.IsRouted("worldmap:kalimdor") && wmCat.Hosted);

                // ---------------------------------------------------------------- Navigate here
                var synth = SynthMapSet(); var sidx = new MapIndex(synth);
                var way = WorldMapCommands.Way(sidx, 0, 250, 500, out var why1);
                var cityWay = WorldMapCommands.Way(sidx, 0, 200, 250, out var why2);
                var outWay = WorldMapCommands.Way(sidx, 0, 5000, 5000, out var why3);
                Check("navigate: the text is /etk way <zone ui map id> <x %> <y %> <zone name> with two decimals (Greenvale 75.00 / 75.00; a city inside it wins: Highkeep 50.00 / 50.00)", way == "/etk way 11 75.00 75.00 Greenvale" && cityWay == "/etk way 13 50.00 50.00 Highkeep", way + " | " + cityWay);
                Check("navigate: a spot no zone map shows gives no command and says so", outWay == null && why3 != null && why3.Contains("No zone map") && WorldMapCommands.Way(null, 0, 1, 1, out var why4) == null && why4 != null);
                Check("navigate: the title is one chat-safe line (control characters, | \\ [ ] \" < > dropped, spaces collapsed, 40 characters at most)",
                    WorldMapCommands.SafeTitle("Zone |cff00ff00 [x]\n<evil>\t\"q\"") == "Zone cff00ff00 xevilq" && WorldMapCommands.SafeTitle(new string('a', 100)).Length == 40 && WorldMapCommands.SafeTitle("  \n ") == "" && WorldMapCommands.SafeTitle(null) == "", WorldMapCommands.SafeTitle("Zone |cff00ff00 [x]\n<evil>\t\"q\""));
                var widx = new MapIndex(WmLayerSet(set));
                var cent = WorldMapIds.ShowOnMapId(widx, 11, null);
                var one = WorldMapIds.ShowOnMapId(widx, 11, new[] { new AtlasMarker(0.75, 0.75, MarkerKind.Vendor, "V") });
                var spread = WorldMapIds.ShowOnMapId(widx, 11, new[] { new AtlasMarker(0.2, 0.2, MarkerKind.Vendor), new AtlasMarker(0.8, 0.9, MarkerKind.Vendor) });
                Check("show on world map: the id centres the continent on the zone's middle (no ring), on one place (with a ring), or on the middle of several; an instance has none",
                    cent != null && !cent.Contains("p=1") && cent.StartsWith("worldmap:0?x=500&y=1000&z=") && one != null && one.Contains("x=250&y=500") && one.EndsWith("&p=1") && spread != null && !spread.Contains("p=1") && WorldMapIds.ShowOnMapId(widx, 20, null) == null && WorldMapIds.ShowOnMapId(widx, 0, null) == null && WorldMapIds.ShowOnMapId(sidx, 11, null) == null, cent + " | " + one + " | " + spread);

                // ---------------------------------------------------------------- the layers on the invented continent
                var lset = WmLayerSet(set);
                var lidx = new MapIndex(lset);
                MapData.SetForTest(lidx, "wow_classic_beta|1.60.1.70338|0123456789abcdef");
                WorldMapLayers.FlightTimes = () => new Dictionary<string, double> { ["Horde|23>80"] = 130.4 };
                WorldMapLayers.SavedRoutes = () => new[] { new SavedRoute { Id = "r1", Name = "Herb loop", Zone = 11, Points = new List<double> { 0.2, 0.2, 0.5, 0.3, 0.4, 0.7 } }, new SavedRoute { Id = "r2", Name = "Elsewhere", Zone = 20, Points = new List<double> { 0.1, 0.1, 0.2, 0.2 } } };
                var conts = WorldMapService.Continents(lidx);
                Check("layers: the continents are the continent-type UiMaps with a world map (a WDT and a world map type) and a slug each", conts.Count == 1 && conts[0].MapId == 0 && conts[0].Name == "Northland" && conts[0].Slug == "northland" && conts[0].WdtFileDataId == 7001, string.Join(",", conts.Select(c => c.Name)));
                var zonesL = WorldMapLayers.Build("zones", 0); var zd = Json.Obj(zonesL.Json);
                Check("layers: zone names are labels at the middle of each zone's map on this continent (Greenvale, Redmoor, Highkeep as a small city label); no creature data = no level range, and the note says where it would come from",
                    zonesL.Count == 3 && zd.Str("kind") == "labels" && zd.List("items").Cast<Dictionary<string, object>>().Select(i => i.Str("name")).OrderBy(x => x).SequenceEqual(new[] { "Greenvale", "Highkeep", "Redmoor" }) && zd.List("items").Cast<Dictionary<string, object>>().All(i => i.Str("link").StartsWith("zone:")) && zonesL.Note.Contains("Classic"), zonesL.Json.Length > 200 ? zonesL.Json.Substring(0, 200) : zonesL.Json);
                var green = zd.List("items").Cast<Dictionary<string, object>>().First(i => i.Str("name") == "Greenvale");
                WorldMapMath.WorldToLatLng(500, 1000, out var glat, out var glng);
                Check("layers: a label sits where the zone's middle is on the plane (Greenvale x 500, y 1000)", Math.Abs((double)green["lat"] - glat) < 0.002 && Math.Abs((double)green["lng"] - glng) < 0.002, green["lat"] + "," + green["lng"] + " vs " + glat + "," + glng);
                var fmL = WorldMapLayers.Build("fm", 0); var fd = Json.Obj(fmL.Json);
                var fmNames = fd.List("items").Cast<Dictionary<string, object>>().ToDictionary(i => i.Str("name"), i => i.Str("f"));
                Check("layers: flight masters of the continent with their faction (Orgrimmar horde, Stormwind alliance, Ratchet both); transports, quest paths and nodes on other maps are not drawn", fmL.Count == 3 && fmNames["Orgrimmar, Durotar"] == "h" && fmNames["Stormwind, Elwynn"] == "a" && fmNames["Ratchet, The Barrens"] == "n", string.Join(",", fmNames.Keys));
                var rtL = WorldMapLayers.Build("routes", 0); var rd = Json.Obj(rtL.Json);
                var rItems = rd.List("items").Cast<Dictionary<string, object>>().ToList();
                var timed = rItems.FirstOrDefault(i => i.Str("label") == "2m 10s");
                Check("layers: a route is drawn once for both directions, along the path's points, in the faction's colour (a Horde town to a neutral one is neutral), and labelled with the time the Lodge measured (\"2m 10s\"); the other pair has no time and says so; a path to a node that is not a flight master is not drawn",
                    rtL.Count == 2 && timed != null && timed.Str("c") == "#f0b429" && timed.Str("g") == "routes.neutral" && ((List<object>)timed["pts"]).Count == 8 && timed.Str("name") == "Orgrimmar ↔ Ratchet" && timed.Str("extra") == "Both factions"
                    && rItems.Any(i => i.Str("label") == "" && i.Str("tip") == "No flight time known yet" && i.Str("c") == "#f0b429" && i.Str("name") == "Stormwind ↔ Orgrimmar"), rtL.Count + " " + string.Join(",", rItems.Select(i => i.Str("name") + "=" + i.Str("label"))));
                var savedL = WorldMapLayers.Build("saved", 0); var sd = Json.Obj(savedL.Json);
                Check("layers: saved routes of the continent are loops (the first stop is repeated), a route on another map is left out", savedL.Count == 1 && ((List<object>)((Dictionary<string, object>)sd.List("items")[0])["pts"]).Count == 8 && ((Dictionary<string, object>)sd.List("items")[0]).Str("name") == "Herb loop");
                AtlasQuery.Context.Lodge = () => null;
                var nodesL = WorldMapLayers.Build("nodes", 0); var vendL = WorldMapLayers.Build("vendors", 0); var findL = WorldMapLayers.Build("finds", 0);
                Check("layers: with no Classic baseline and no Lodge, nodes / vendors / finds report \"none\" with a note that says where the data would come from (no markers invented)",
                    nodesL.Empty && vendL.Empty && findL.Empty && nodesL.Json.Length > 0 && Json.Obj(nodesL.Json).Str("kind") == "points" && vendL.Note.Contains("Classic") && findL.Note.Contains("Lodge"), nodesL.Note + " | " + vendL.Note + " | " + findL.Note);
                var bogus = WorldMapLayers.Build("coal", 0);
                Check("layers: an unknown layer builds nothing", bogus.Json.Length == 0 && bogus.Empty);
                MapData.SetForTest(null, null, MapData.State.NotStarted);
                Check("layers: with no maps loaded every layer says the maps are not ready (and never throws)", WorldMapLayers.Build("zones", 0).Note.Contains("not ready") && WorldMapLayers.Build("fm", 0).Note.Contains("not ready") && WorldMapLayers.Build("nodes", 0).Note.Contains("not ready"));
                var cat = WorldMapLayers.Catalog();
                Check("layers: the panel lists zones, borders, flight masters, routes, dungeons & raids (dungeons, raids), nodes (herbs / ore / chests / fishing), saved routes, vendors, trainers, quest givers, rares, NPCs seen by Lodge members (met / killed / rares) and Lodge finds; every id is one the protocol accepts",
                    cat.Select(c => (string)c["id"]).SequenceEqual(WorldMapIds.LayerIds) && cat.SelectMany(c => c.ContainsKey("children") ? ((List<Dictionary<string, object>>)c["children"]).Select(k => (string)k["id"]) : Enumerable.Empty<string>()).SequenceEqual(new[] { "fm.horde", "fm.alliance", "fm.neutral", "routes.horde", "routes.alliance", "routes.neutral", "instances.dungeons", "instances.raids", "nodes.herbs", "nodes.ore", "nodes.chests", "nodes.fishing", "seen.met", "seen.killed", "seen.rares" }) && WorldMapLayers.DefaultOn.All(WorldMapIds.IsLayerId));
                Check("layers: a route is classified by its two ends - both Horde = Horde, both Alliance = Alliance, a neutral end or one of each = neutral; the kind rows fm.* / routes.* (horde, alliance, neutral) are layer ids the protocol accepts, a fourth kind or a glued suffix is not",
                    WorldMapLayers.RouteFaction(WorldMapLayers.Horde, WorldMapLayers.Horde) == WorldMapLayers.Horde && WorldMapLayers.RouteFaction(WorldMapLayers.Alliance, WorldMapLayers.Alliance) == WorldMapLayers.Alliance
                    && WorldMapLayers.RouteFaction(WorldMapLayers.Horde, WorldMapLayers.Alliance) == WorldMapLayers.Neutral && WorldMapLayers.RouteFaction(WorldMapLayers.Horde, WorldMapLayers.Neutral) == WorldMapLayers.Neutral
                    && WorldMapLayers.RouteFaction(WorldMapLayers.Neutral, WorldMapLayers.Neutral) == WorldMapLayers.Neutral && WorldMapLayers.RouteFaction("", WorldMapLayers.Horde) == WorldMapLayers.Neutral
                    && WorldMapLayers.FactionKey(WorldMapLayers.Horde) == "horde" && WorldMapLayers.FactionKey(WorldMapLayers.Alliance) == "alliance" && WorldMapLayers.FactionKey(WorldMapLayers.Neutral) == "neutral"
                    && new[] { "fm.horde", "fm.alliance", "fm.neutral", "routes.horde", "routes.alliance", "routes.neutral" }.All(WorldMapIds.IsLayerId) && !WorldMapIds.IsLayerId("fm.goblin") && !WorldMapIds.IsLayerId("fm.") && !WorldMapIds.IsLayerId("routes.hordes") && !WorldMapIds.IsTopLayer("fm.horde")
                    && WorldMapProtocol.Parse("{\"t\":\"layers\",\"on\":[\"fm\",\"fm.horde\",\"routes.neutral\"]}", out _) != null && WorldMapProtocol.Parse("{\"t\":\"need\",\"layer\":\"fm.horde\"}", out _) == null);

                // ---------------------------------------------------------------- links in chat and search
                var found = ItemLinks.Find("meet at worldmap:kalimdor or worldmap:1?x=1630&y=-4373&z=5. bad worldmap:1?x=99999&y=1 and xworldmap:kalimdor");
                Check("links: worldmap:<continent> and worldmap:<map>?x=&y=&z= in chat become Atlas chips (a position outside the world and a glued prefix do not)",
                    found.Count == 2 && found.All(f => f.Kind == ItemRefKind.Atlas && f.AtlasKind == "worldmap") && found[0].AtlasId == "worldmap:kalimdor" && found[1].AtlasId == "worldmap:1?x=1630&y=-4373&z=5", string.Join(" | ", found.Select(f => f.AtlasId)));

                // ---------------------------------------------------------------- "Copy details": the report a member can paste (no personal data)
                {
                    var prof = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    var logFile = Path.Combine(work, "fake-hub.log");
                    File.WriteAllLines(logFile, new[]
                    {
                        "2026-10-15 20:01:02  Lodge: Elan joined the voice room",
                        "2026-10-15 20:01:03  world map: Kalimdor: minimap 40,28 is not in the local game storage yet; shown as a placeholder and read again later",
                        "2026-10-15 20:01:04  world map terrain: tile 40,28: not a BLTE block in " + Path.Combine(prof, "AppData", "Local", "ElansAddonHub", "x.bin"),
                        "2026-10-15 20:01:05  worldmap test ok something the self-test logged",
                        "2026-10-15 20:01:06  world map web: the page could not be started (WebView2 140.0): E:\\Users\\SomeoneElse\\Documents\\x",
                        "2026-10-15 20:01:07  inventory: Forever-Elan bags read",
                    });
                    WorldMapReport.LogPathForTest = () => logFile;
                    var rep = WorldMapReport.Build(new WorldMapReport.Info
                    {
                        Outpost = "2.70.0", Game = "1.60.1.70425-ea9c525e", Flavor = "_classic_beta_", WebView = "140.0.3485.54", Renderer = "ANGLE (NVIDIA GeForce RTX 3070 Direct3D11)", Continent = "Kalimdor", MapId = 1, Mode = "3D",
                        Problem = "the page did not start (" + Path.Combine(prof, "AppData", "Local", "ElansAddonHub", "webview2") + ")", PictureTiles = 988, TerrainTiles = 988,
                        Pending = new List<PendingTile> { new PendingTile { Col = 40, Row = 28, Part = "picture", Reason = "missing", Shown = "art" }, new PendingTile { Col = 40, Row = 28, Part = "terrain", Reason = "unreadable", Detail = "not a BLTE block", Shown = "low" } },
                        Page = "3d page: started yes, ready no", PageErrors = new List<string> { "TypeError: x is undefined (line 12)" },
                    });
                    WorldMapReport.LogPathForTest = null;
                    Check("report: Copy details names the Outpost, game build and flavour, WebView2, the WebGL renderer, the continent, the tile counts, each tile not readable yet with its reason and what is shown, the page's state and errors",
                        rep.Contains("Outpost: 2.70.0") && rep.Contains("Game: 1.60.1.70425-ea9c525e (_classic_beta_)") && rep.Contains("WebView2 runtime: 140.0.3485.54") && rep.Contains("Graphics (WebGL): ANGLE (NVIDIA") && rep.Contains("Continent: Kalimdor (map 1), 3D view")
                        && rep.Contains("picture tile 40,28: missing in local storage - shown as continent map art") && rep.Contains("terrain tile 40,28: read error (not a BLTE block) - shown as low-detail heights (WDL)") && rep.Contains("Pictures: 988 map tiles, 1 shown with a stand-in") && rep.Contains("Page error: TypeError"), rep);
                    Check("report: the recent log lines are only the world map's (not the Lodge, the inventory or the self-test's own), and no path shows the user folder (it is %USERPROFILE% / %LOCALAPPDATA%), another user's folder neither",
                        rep.Contains("minimap 40,28 is not in the local game storage") && rep.Contains("world map web:") && !rep.Contains("Lodge: Elan") && !rep.Contains("inventory:") && !rep.Contains("worldmap test")
                        && !rep.ToLowerInvariant().Contains(prof.ToLowerInvariant()) && !rep.Contains("SomeoneElse") && rep.Contains("%LOCALAPPDATA%\\ElansAddonHub") && rep.Contains("%USERPROFILE%"), rep);
                    Check("report: Redact keeps a path that is not personal (the WoW folder) and turns the user folder into %USERPROFILE%",
                        WorldMapReport.Redact("D:\\Blizzard\\World of Warcraft") == "D:\\Blizzard\\World of Warcraft" && WorldMapReport.Redact(Path.Combine(prof, "x")).StartsWith("%USERPROFILE%") && WorldMapReport.Redact(null) == "");
                }

                // ---------------------------------------------------------------- settings: what is remembered
                Check("settings: the layers and the last continent are saved fields of the settings (null = the defaults)", new Settings().WorldMapLayers == null && new Settings().WorldMapLast == null);
            }
            catch (Exception e) { Check("worldmap test crashed", false, e.ToString()); }
            finally
            {
                MapData.CacheFile = savedCache; MapData.WowRoot = savedMapRoot; MapData.Enabled = savedMapEnabled; AtlasQuery.Context.Lodge = savedLodge;
                WorldMapService.WdtSourceForTest = savedWdt; WorldMapService.TileSourceForTest = savedTile; WorldMapCache.RootOverride = savedRoot;
                settings.WorldMapLayers = savedLayers; settings.WorldMapLast = savedLast;
                WorldMapLayers.SavedRoutes = () => settings.SavedRoutes;
                WorldMapLayers.FlightTimes = FlightTimesForMap;
            }
            try { await WorldMapTerrainTest(dir, work, notes, v => ok &= v); }
            catch (Exception e) { notes.Add("FAIL worldmap terrain test crashed [" + e + "]"); ok = false; }
            try { await WorldMapWaterTest(dir, work, notes, v => ok &= v); }
            catch (Exception e) { notes.Add("FAIL worldmap water test crashed [" + e + "]"); ok = false; }
            try { await WorldMapPageTest(dir, work, notes, v => ok &= v); }
            catch (Exception e) { notes.Add("FAIL worldmap page test crashed [" + e + "]"); ok = false; }
            try { WmAdtCacheDrop(); } catch (Exception) { }
            try { var pr = System.Diagnostics.Process.GetCurrentProcess(); notes.Add("note worldmap: at the end of the section the test process holds " + GC.GetTotalMemory(true) / 1048576 + " MB of managed memory, " + pr.PrivateMemorySize64 / 1048576 + " MB private, peak working set " + pr.PeakWorkingSet64 / 1048576 + " MB (" + (Environment.Is64BitProcess ? "64" : "32") + "-bit process)"); } catch (Exception) { }
            return (ok ? "worldmap: ok" : "worldmap: FAILED") + "\r\n" + string.Join("\r\n", notes);
        }

        // runs an action that is expected to throw: the message, or null when it did not
        static string Assert(Action a) { try { a(); return null; } catch (Exception e) { return e.GetType().Name + ": " + e.Message; } }
    }
}
