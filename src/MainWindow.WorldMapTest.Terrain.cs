using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Maps;
using ElansAddonHub.Services.WorldMap;

namespace ElansAddonHub
{
    // --selftest "worldmap", part 3 (2.53 phase 2): terrain. The root ADT reader (MCNK / MCVT / area ids) on a synthetic file as the game writes it and on real
    // tiles (read-only), the height grid (down-sampling, seams between neighbouring tiles), the terrain cache (build, resume, damaged and missing files,
    // clean-up), zone outlines on invented area grids (two zones, a hole, islands, corners that touch) and on the real continents, "which zone is this point in".
    public partial class MainWindow
    {
        // an invented landscape that is a function of the world position, so neighbouring tiles meet exactly like the game's
        static float WmZ(double x, double y) => (float)(120 + 0.02 * x - 0.015 * y + 35 * Math.Sin(x / 310.0) + 25 * Math.Cos(y / 270.0));
        static int WmChunkArea(int gx, int gy)
        {
            if (gy >= 56) return 0;                                            // open sea along the south edge
            if (gx >= 20 && gx <= 25 && gy >= 20 && gy <= 25) return 300;      // a hole in zone 100
            if (gx >= 40 && gx <= 43 && gy >= 40 && gy <= 43) return 200;      // an island of zone 200 inside zone 100
            if (gx >= 10 && gx <= 11 && gy >= 5 && gy <= 6) return 200;        // two 2 x 2 islands that only touch at a corner
            if (gx >= 12 && gx <= 13 && gy >= 7 && gy <= 8) return 200;
            if (gx == 30 && gy == 30) return 200;                              // one chunk of speckle
            return gx < 64 ? 100 + (gx / 8) % 3 : 200 + (gx / 8) % 3;          // zone 100 (areas 100-102) west, zone 200 (areas 200-202) east
        }

        // a root ADT as the game writes it: reversed chunk tags, 256 MCNK of 128 bytes of header + MCNR + MCVT, a trailer chunk
        static byte[] WmAdt(int col, int row, bool reversedTags = true, int dropChunks = 0, bool truncate = false)
        {
            var ms = new MemoryStream(); var w = new BinaryWriter(ms);
            string T(string t) => reversedTags ? new string(t.Reverse().ToArray()) : t;
            void Chunk(string tag, byte[] body) { w.Write(Encoding.ASCII.GetBytes(T(tag))); w.Write(body.Length); w.Write(body); }
            Chunk("MVER", BitConverter.GetBytes(18));
            Chunk("MHDR", new byte[64]);
            Chunk("MH2O", WmMh2o(col, row));
            double ty = WorldMapMath.TileYards, cs = ty / 16, unit = ty / 128;
            for (int iy = 0; iy < 16; iy++)
                for (int ix = 0; ix < 16; ix++)
                {
                    if (iy * 16 + ix >= 256 - dropChunks) continue;
                    double px = (32 - row) * ty - iy * cs, py = (32 - col) * ty - ix * cs;
                    float pz = WmZ(px, py);
                    var body = new MemoryStream(); var b = new BinaryWriter(body);
                    b.Write(0x18040u); b.Write(ix); b.Write(iy); b.Write(0); b.Write(0);
                    b.Write(136 + 8 + 435); b.Write(136); b.Write(0); b.Write(0); b.Write(0); b.Write(0); b.Write(0); b.Write(0);        // offsets (0x14..0x30)
                    b.Write(WmChunkArea((col - 28) * 16 + ix, (row - 30) * 16 + iy));                                                     // 0x34 areaid
                    while (body.Length < 0x68) b.Write(0);
                    b.Write((float)px); b.Write((float)py); b.Write(pz);                                                                   // 0x68 position
                    while (body.Length < 128) b.Write(0);
                    // MCNR before MCVT (the reader scans sub-chunks), then MCVT
                    b.Write(Encoding.ASCII.GetBytes(T("MCNR"))); b.Write(435); b.Write(new byte[435]);
                    b.Write(Encoding.ASCII.GetBytes(T("MCVT"))); b.Write(145 * 4);
                    for (int k = 0; k < 9; k++)
                    {
                        for (int i = 0; i < 9; i++) b.Write(WmZ(px - k * unit, py - i * unit) - pz);
                        if (k < 8) for (int i = 0; i < 8; i++) b.Write(WmZ(px - (k + 0.5) * unit, py - (i + 0.5) * unit) - pz);
                    }
                    Chunk("MCNK", body.ToArray());
                }
            Chunk("MFBO", new byte[36]);
            var all = ms.ToArray();
            return truncate ? all.Take(all.Length / 2).ToArray() : all;
        }

        // the synthetic ADTs are 450 KB each and the test builds the terrain a dozen times: made once per tile (the process is 32-bit and big short-lived arrays fragment its heap)
        static Dictionary<int, byte[]> wmAdtCache;
        static byte[] WmAdtCached(int col, int row)
        {
            if (wmAdtCache == null) wmAdtCache = new Dictionary<int, byte[]>();
            if (!wmAdtCache.TryGetValue(col * 64 + row, out var b)) wmAdtCache[col * 64 + row] = b = WmAdt(col, row);
            return b;
        }
        static void WmAdtCacheDrop()
        {
            wmAdtCache = null;
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect();
        }

        static (int col, int row) WmAdtOf(int fdid) { int i = fdid - 1070000; return (i % 64, i / 64); }

        async Task WorldMapTerrainTest(string dir, string work, List<string> notes, Action<bool> record)
        {
            void Check(string what, bool cond, string detail = null) { SelfTestClock.Mark(what); notes.Add((cond ? "ok   " : "FAIL ") + what + (!cond && detail != null ? " [" + detail + "]" : "")); record(cond); Util.Log("worldmap test " + (cond ? "ok " : "FAIL ") + what.Substring(0, Math.Min(80, what.Length))); }
            var I = CultureInfo.InvariantCulture;
            var savedRoot = WorldMapCache.RootOverride; var savedWdt = WorldMapService.WdtSourceForTest; var savedAdt = WorldMapService.AdtSourceForTest; var savedTile = WorldMapService.TileSourceForTest;
            try
            {
                var tw = Path.Combine(work, "terrain");
                if (Directory.Exists(tw)) Util.DeleteDir(tw);
                Directory.CreateDirectory(tw);
                WorldMapCache.RootOverride = Path.Combine(tw, "cache");
                WorldMapService.ResetForTest();

                // ---------------------------------------------------------------- the root ADT reader
                var adt = AdtFile.Parse(WmAdt(30, 31));
                bool heightsOk = true, areasOk = true; double maxErr = 0;
                double ty = WorldMapMath.TileYards, unit = ty / 128;
                for (int iy = 0; iy < 16 && heightsOk; iy++)
                    for (int ix = 0; ix < 16; ix++)
                    {
                        var c = adt.Chunks[iy * 16 + ix];
                        if (c == null || c.Heights == null || c.IndexX != ix || c.IndexY != iy) { heightsOk = false; break; }
                        if (c.AreaId != WmChunkArea((30 - 28) * 16 + ix, (31 - 30) * 16 + iy)) areasOk = false;
                        for (int k = 0; k < 9; k++) for (int i = 0; i < 9; i++)
                            maxErr = Math.Max(maxErr, Math.Abs(c.PosZ + c.Heights[k * 17 + i] - WmZ(c.PosX - k * unit, c.PosY - i * unit)));
                    }
                Check("ADT: a synthetic root ADT (reversed chunk tags, MCNR before MCVT, trailer chunks) gives 256 chunks with their index, 145 heights each (z = position.z + value, max error " + maxErr.ToString("0.0000", I) + ") and the area id at 0x34",
                    adt.Ok && adt.Found == 256 && adt.WithHeights == 256 && heightsOk && areasOk && maxErr < 0.01, adt.Problem + " " + adt.Found + " " + maxErr);
                var plain = AdtFile.Parse(WmAdt(30, 31, reversedTags: false));
                var cut = AdtFile.Parse(WmAdt(30, 31, truncate: true));
                Check("ADT: tags written the right way round are read too; a file cut in half keeps the chunks it has (" + cut.Found + "); one without MCNK, or empty, says so",
                    plain.Ok && plain.Found == 256 && cut.Found > 60 && cut.Found < 256 && !AdtFile.Parse(new byte[40]).Ok && !AdtFile.Parse(null).Ok && !AdtFile.Parse(Encoding.ASCII.GetBytes("REVM\u0004\0\0\0\u0012\0\0\0xxxxxxxxxxxx")).Ok);

                // ---------------------------------------------------------------- the height grid
                var tt = TerrainTile.FromAdt(30, 31, adt);
                double gerr = 0; double x0 = (32 - 31) * ty, y0 = (32 - 30) * ty;
                for (int r = 0; r < 65; r++) for (int c = 0; c < 65; c++) gerr = Math.Max(gerr, Math.Abs(tt.Z(r, c) - WmZ(x0 - r * ty / 64, y0 - c * ty / 64)));
                var rt = TerrainTile.FromBytes(tt.ToBytes());
                Check("terrain: a tile becomes a 65 x 65 grid (every second outer vertex: sample (r, c) lies r x 8.33 yards south and c x 8.33 yards east of the tile's north-west corner) as 16-bit heights between the tile's min and max (max error " + gerr.ToString("0.0000", I) + " yd of a " + (tt.MaxZ - tt.MinZ).ToString("0") + " yd range); the file is " + TerrainTile.FileBytes + " bytes and reads back identically",
                    gerr < (tt.MaxZ - tt.MinZ) / 65535.0 + 0.01 && tt.ToBytes().Length == TerrainTile.FileBytes && rt != null && rt.Col == 30 && rt.Row == 31 && rt.H.SequenceEqual(tt.H) && rt.Area.SequenceEqual(tt.Area) && Math.Abs(rt.MinZ - tt.MinZ) < 1e-6, gerr + "");
                var bad = tt.ToBytes(); bad[0] = (byte)'X';
                Check("terrain: a damaged or foreign tile file (wrong magic, wrong size, empty) is refused", TerrainTile.FromBytes(bad) == null && TerrainTile.FromBytes(tt.ToBytes().Take(100).ToArray()) == null && TerrainTile.FromBytes(null) == null);
                var gap = AdtFile.Parse(WmAdt(30, 31, dropChunks: 3));
                var gtile = TerrainTile.FromAdt(30, 31, gap);
                Check("terrain: a tile with missing chunks still gives a grid (the gaps sit at the lowest height) and counts them", gtile.MissingChunks == 3 && gtile.Area[255] == 0 && gtile.Area[253] == 0 && gtile.Area[252] != 0);

                // ---------------------------------------------------------------- the cache: build, seams, resume, damaged / missing files
                var tiles = WmCols.SelectMany(c => WmRows.Select(r => Tuple.Create(c, r))).ToList();
                int adtLoads = 0;
                WorldMapService.WdtSourceForTest = mapId => WmWdt(tiles);
                int missingFdid = WmFdid(35, 33) + 1000000, garbageFdid = WmFdid(34, 33) + 1000000;
                WorldMapService.AdtSourceForTest = fdid =>
                {
                    Interlocked.Increment(ref adtLoads);
                    if (fdid == missingFdid) return null;
                    if (fdid == garbageFdid) return new byte[500];
                    var (c, r) = WmAdtOf(fdid); return WmAdtCached(c, r);
                };
                var zr = new ZoneResolver { ZoneOf = a => a <= 0 ? 0 : a / 100 * 100, NameOf = a => a == 100 ? "Westmark" : a == 200 ? "Eastmark" : a == 300 ? "Lakeland" : a == 0 ? "" : "Area " + a, UiMapOf = a => a / 100 };
                var key = "9.9.9.9-test_p1"; var mapDir = WorldMapCache.MapDir(key, 5);
                var prog = new List<string>();
                var res = WorldMapService.BuildTerrain(5, "Testland", 7001, key, mapDir, null, zr, CancellationToken.None, p => prog.Add(p.Phase + ":" + p.Done + "/" + p.Total));
                var tdir = WorldMapTerrain.Dir(mapDir); var man = WorldMapTerrain.ReadManifest(tdir);
                Check("terrain cache: 32 tiles are read (one is absent in the game and one is damaged: counted, not fatal), " + res.Written + " files + manifest + borders in <map>\\terrain\\, progress " + prog.Count + " reports ending " + prog.LastOrDefault(),
                    res.Complete && res.Tiles == 32 && res.Written == 30 && res.Missing == 1 && res.Failed == 1 && res.Files == 30 && man != null && man.Valid && man.Tiles == 30 && man.Zones == res.Zones && res.Zones >= 3 && File.Exists(WorldMapTerrain.BordersPath(tdir))
                    && !Directory.GetFiles(tdir, "*.tmp").Any() && prog.Count > 3 && prog.Last().StartsWith("borders:") && adtLoads == 32, res.Written + "/" + res.Missing + "/" + res.Failed + " loads " + adtLoads);
                // seams: the east column of a tile and the west column of its eastern neighbour are the same place
                double maxSeam = 0; int seams = 0;
                foreach (var t in tiles)
                {
                    var a = WorldMapTerrain.ReadTile(tdir, t.Item1, t.Item2); var e = WorldMapTerrain.ReadTile(tdir, t.Item1 + 1, t.Item2); var s = WorldMapTerrain.ReadTile(tdir, t.Item1, t.Item2 + 1);
                    if (a != null && e != null) { seams++; for (int r = 0; r < 65; r++) maxSeam = Math.Max(maxSeam, Math.Abs(a.Z(r, 64) - e.Z(r, 0))); }
                    if (a != null && s != null) { seams++; for (int c = 0; c < 65; c++) maxSeam = Math.Max(maxSeam, Math.Abs(a.Z(64, c) - s.Z(0, c))); }
                }
                Check("terrain: neighbouring tiles meet without a step (" + seams + " shared edges, the largest difference is " + maxSeam.ToString("0.000", I) + " yd, which is the 16-bit quantisation)", seams > 40 && maxSeam < 0.05, maxSeam + "");
                int loads1 = adtLoads;
                var again = WorldMapService.BuildTerrain(5, "Testland", 7001, key, mapDir, null, zr, CancellationToken.None, null);
                Check("terrain cache: building again reads the 30 finished tiles back from disk (nothing written; the two bad ones are tried once more)", again.Complete && again.Reused == 30 && again.Written == 0 && adtLoads - loads1 == 2, again.Reused + " " + (adtLoads - loads1));

                // a cancelled build leaves its finished tiles and no manifest; the next one only reads the rest
                var mapDir2 = WorldMapCache.MapDir(key, 6); adtLoads = 0;
                Func<int, byte[]> good = fdid => { Interlocked.Increment(ref adtLoads); var (c, r) = WmAdtOf(fdid); return WmAdtCached(c, r); };
                var cts = new CancellationTokenSource(); int seen = 0;
                WorldMapService.AdtSourceForTest = fdid => { if (Interlocked.Increment(ref seen) == 11) cts.Cancel(); return good(fdid); };
                var cancelled = Assert(() => WorldMapService.BuildTerrain(6, "Testland", 7001, key, mapDir2, null, zr, cts.Token, null));
                var tdir2 = WorldMapTerrain.Dir(mapDir2);
                int partial = Directory.GetFiles(tdir2, "*.wmt").Length;
                Check("terrain cache: a cancelled build stops between tiles (" + partial + " finished), writes no manifest, leaves no half-written file", cancelled != null && cancelled.StartsWith("OperationCanceledException") && partial >= 10 && partial < 32 && !File.Exists(WorldMapTerrain.ManifestPath(tdir2)) && !Directory.GetFiles(tdir2, "*.tmp").Any(), cancelled + " " + partial);
                WorldMapService.AdtSourceForTest = good; adtLoads = 0;
                // a damaged file (cut short) is made again
                var victim = Directory.GetFiles(tdir2, "*.wmt").OrderBy(f => f).First();
                File.WriteAllBytes(victim, new byte[100]);
                var resumed = WorldMapService.BuildTerrain(6, "Testland", 7001, key, mapDir2, null, zr, CancellationToken.None, null);
                Check("terrain cache: the next build resumes - only the tiles that were missing or damaged are read (" + adtLoads + " of 32), then the manifest is written", resumed.Complete && resumed.Reused == partial - 1 && adtLoads == 32 - partial + 1 && resumed.Files == 32 && WorldMapTerrain.ReadManifest(tdir2) != null, adtLoads + " " + resumed.Reused);
                // build keys: another game build's folder is removed, this one's stays; Clear removes everything
                long bytes = WorldMapCache.SizeBytes();
                int removed = WorldMapCache.RemoveOtherBuilds("other_p1");
                Check("terrain cache: the terrain lives under the build key's folder - a new game build removes it with the pictures (" + removed + " folder removed), Settings' size / Clear cover it (" + WorldMapCache.SizeText(bytes) + ")", bytes > 100000 && removed == 1 && WorldMapCache.SizeBytes() == 0);
                var res3 = WorldMapService.BuildTerrain(7, "Testland", 7001, key, WorldMapCache.MapDir(key, 7), null, zr, CancellationToken.None, null);
                long freed = WorldMapCache.Clear();
                Check("terrain cache: Clear world map cache frees the terrain too", res3.Complete && freed > 100000 && WorldMapCache.SizeBytes() == 0 && WorldMapCache.TerrainBuilt(7, key) == null);

                // ---------------------------------------------------------------- zone outlines on an invented grid
                const int GW = 128, GH = 64;
                var grid = new int[GW * GH];
                for (int y = 0; y < GH; y++) for (int x = 0; x < GW; x++) { int a = WmChunkArea(x, y); grid[y * GW + x] = a <= 0 ? 0 : a / 100 * 100; }
                var exact = WorldMapZones.Trace(grid, GW, GH, 0, 1);
                bool allExact = true; int wrong = 0;
                foreach (var z in new[] { 100, 200, 300 })
                    for (int y = 0; y < GH; y++) for (int x = 0; x < GW; x++)
                        if (WorldMapZones.Contains(exact[z], x + 0.5, y + 0.5) != (grid[y * GW + x] == z)) { allExact = false; wrong++; }
                Check("zone borders: on an invented 128 x 64 area grid (two zones, a lake inside one, an island of the other inside it, two islands that touch at a corner, open sea, a speck) every chunk is inside the rings of its own zone and of no other when nothing is simplified",
                    allExact && exact.Count == 3, wrong + " wrong, " + exact.Count + " zones");
                // 100: outer + the lake + the island + 2 corner islands (4 holes); 200: outer + island + 2 corner islands + the speck; 300: the lake
                var dropped = WorldMapZones.Trace(grid, GW, GH, 0);
                Check("zone borders: rings: Westmark has an outer ring, the lake, the island, the speck and ONE hole in the shape of an 8 for the two islands that touch at a corner (a zone is 4-connected, so its holes are 8-connected), Eastmark an outer ring, the island, the two corner islands (separate rings) and the speck, Lakeland 1; with rings under 3 chunks dropped the specks go (" + dropped[100].Count + " and " + dropped[200].Count + " rings)",
                    exact[100].Count == 5 && exact[200].Count == 5 && exact[300].Count == 1 && dropped[100].Count == 4 && dropped[200].Count == 4, exact[100].Count + " " + exact[200].Count + " " + exact[300].Count + " " + dropped[100].Count + " " + dropped[200].Count);
                var simp = WorldMapZones.Trace(grid, GW, GH);
                int near = 0, farWrong = 0;
                foreach (var z in new[] { 100, 200, 300 })
                    for (int y = 0; y < GH; y++) for (int x = 0; x < GW; x++)
                    {
                        bool inside = WorldMapZones.Contains(simp[z], x + 0.5, y + 0.5), want = grid[y * GW + x] == z;
                        if (inside == want) continue;
                        bool nearBorder = false;
                        for (int dy = -1; dy <= 1 && !nearBorder; dy++) for (int dx = -1; dx <= 1; dx++) { int nx = x + dx, ny = y + dy; if (nx < 0 || ny < 0 || nx >= GW || ny >= GH) { nearBorder = true; break; } if ((grid[ny * GW + nx] == z) != want) { nearBorder = true; break; } }
                        near++; if (!nearBorder) farWrong++;
                    }
                Check("zone borders: simplified (Douglas-Peucker, 0.8 chunk), the rings still agree with the grid everywhere except within one chunk of a border (" + near + " chunks differ, " + farWrong + " further away), and have no more points than the exact ones (" + simp[100].Sum(r => r.Length / 2) + " against " + exact[100].Sum(r => r.Length / 2) + ")",
                    farWrong == 0 && simp[100].Sum(r => r.Length) <= exact[100].Sum(r => r.Length), near + " " + farWrong);
                var emptyTrace = WorldMapZones.Trace(new int[16], 4, 4);
                var single = WorldMapZones.Trace(new[] { 7, 7, 0, 7, 7, 0, 0, 0, 0 }, 3, 3, 0, 1);
                Check("zone borders: an empty grid has no rings, a 2 x 2 block has one square ring of four corners", emptyTrace.Count == 0 && single.Count == 1 && single[7].Count == 1 && single[7][0].Length == 8, single.Count + "");

                // ---------------------------------------------------------------- zones from the terrain files
                var key2 = "9.9.9.9-test2_p1"; var mapDirZ = WorldMapCache.MapDir(key2, 5);
                WorldMapService.AdtSourceForTest = good;
                var res4 = WorldMapService.BuildTerrain(5, "Testland", 7001, key2, mapDirZ, null, zr, CancellationToken.None, null);
                var tdir3 = WorldMapTerrain.Dir(mapDirZ);
                var borders = WorldMapZones.ReadBorders(tdir3);
                var west = borders?.Zones.FirstOrDefault(z => z.Id == 100);
                Check("zone borders: borders.json holds each zone with its name and rings on the chunk lattice (" + (borders?.Zones.Count ?? 0) + " zones: " + string.Join(", ", borders?.Zones.Select(z => z.Name + " " + z.Rings.Count) ?? new string[0]) + ")",
                    borders != null && borders.MapId == 5 && borders.Zones.Count == 3 && west != null && west.Name == "Westmark" && west.Rings.Count == 4 && west.Chunks > 3000 && res4.Complete, borders?.Zones.Count + "");
                // point -> zone from the tile files: a spot in Westmark's area 101, one in the lake, one in the sea, one in a tile that does not exist
                WorldMapMath.GridToWorld(28 + 8.8 / 16.0, 30 + 5.6 / 16.0, out var wx, out var wy);
                var h1 = WorldMapZones.ZoneAt(tdir3, wx, wy, zr);
                WorldMapMath.GridToWorld(28 + 20.5 / 16.0, 30 + 20.5 / 16.0, out var lx, out var ly);
                var h2 = WorldMapZones.ZoneAt(tdir3, lx, ly, zr);
                WorldMapMath.GridToWorld(28 + 5, 30 + 3.9, out var sx, out var sy);
                var h3 = WorldMapZones.ZoneAt(tdir3, sx, sy, zr);
                WorldMapMath.GridToWorld(5, 5, out var nx2, out var ny2);
                Check("zone borders: which zone a point is in comes from the tile's chunk grid: area 101 -> zone Westmark, the lake -> Lakeland, open sea and a missing tile -> none",
                    h1 != null && h1.AreaId == 101 && h1.ZoneName == "Westmark" && h1.AreaName == "Area 101" && h2 != null && h2.ZoneName == "Lakeland" && h3 == null && WorldMapZones.ZoneAt(tdir3, nx2, ny2, zr) == null, h1?.AreaId + " " + h2?.ZoneName);
                WorldMapCache.Clear();

            }
            finally
            {
                WmAdtCacheDrop();
                WorldMapService.AdtSourceForTest = savedAdt; WorldMapService.WdtSourceForTest = savedWdt; WorldMapService.TileSourceForTest = savedTile; WorldMapCache.RootOverride = savedRoot;
                WorldMapService.ResetForTest();
            }
        }

        // the real terrain (read-only game files; optional)
        async Task WorldMapTerrainRealChecks(MapIndex idx, string real, string rw, List<string> notes, CheckFn Check)
        {
            var I = CultureInfo.InvariantCulture;
            var savedRootOverride = WorldMapCache.RootOverride;
            try
            {
                var kal = WorldMapService.Continents(idx).FirstOrDefault(c => c.MapId == 1);
                if (kal == null) { notes.Add("skip real terrain: no Kalimdor"); return; }
                WorldMapCache.RootOverride = Path.Combine(rw, "terrain-cache");
                var zr = ZoneResolver.FromIndex(idx);
                var key = WorldMapCache.BuildKey; var mapDir = WorldMapCache.MapDir(key, 1);
                // a real region: Durotar and the Orgrimmar coast (map tiles col 38..42, row 28..33)
                var sw = System.Diagnostics.Stopwatch.StartNew();
                Func<WdtTile, bool> region = t => t.Col >= 38 && t.Col <= 42 && t.Row >= 28 && t.Row <= 33;
                var res = WorldMapService.BuildTerrain(1, kal.Name, kal.WdtFileDataId, key, mapDir, region, zr, CancellationToken.None, null);
                long ms = sw.ElapsedMilliseconds;
                var tdir = WorldMapTerrain.Dir(mapDir);
                var set = new Dictionary<int, TerrainTile>();
                foreach (var f in Directory.GetFiles(tdir, "*.wmt")) { var p = Path.GetFileNameWithoutExtension(f).Split('_'); var t = WorldMapTerrain.ReadTile(tdir, int.Parse(p[0]), int.Parse(p[1])); if (t != null) set[t.Col * 100 + t.Row] = t; }
                float mn = set.Values.Min(t => t.MinZ), mx = set.Values.Max(t => t.MaxZ);
                Check("real terrain: a region of Kalimdor (" + res.Tiles + " map tiles) is read from the game's root ADT files (MCNK / MCVT): " + res.Written + " height grids + borders in " + ms + " ms, " + (res.Bytes / 1024) + " KB; heights " + mn.ToString("0", I) + " .. " + mx.ToString("0", I) + " yards",
                    res.Complete && res.Failed == 0 && res.Missing == 0 && res.Written == res.Tiles && mn > -1500 && mx < 3500 && mx - mn > 100 && res.Bytes < res.Tiles * 12000L + 4000000, res.Failed + " " + res.Missing + " " + mn + " " + mx);
                // seams between real neighbouring tiles
                double worst = 0, sum = 0; long n = 0; long over = 0;
                foreach (var kv in set)
                {
                    var a = kv.Value;
                    if (set.TryGetValue((a.Col + 1) * 100 + a.Row, out var e)) for (int r = 0; r < 65; r++) { var d = Math.Abs(a.Z(r, 64) - e.Z(r, 0)); worst = Math.Max(worst, d); sum += d; n++; if (d > 0.1) over++; }
                    if (set.TryGetValue(a.Col * 100 + a.Row + 1, out var s)) for (int c = 0; c < 65; c++) { var d = Math.Abs(a.Z(64, c) - s.Z(0, c)); worst = Math.Max(worst, d); sum += d; n++; if (d > 0.1) over++; }
                }
                Check("real terrain: real neighbouring tiles meet: over " + n + " shared edge samples the mean difference is " + (sum / Math.Max(1, n)).ToString("0.0000", I) + " yd, the largest " + worst.ToString("0.00", I) + " yd, " + over + " samples differ by more than 0.1 yd",
                    n > 1000 && over < n / 100, over + "/" + n + " worst " + worst);
                var orgr = idx.Set.TaxiNodes.FirstOrDefault(nd => nd.Name == "Orgrimmar, Durotar");
                var borders = WorldMapZones.ReadBorders(tdir);
                var durotar = borders?.Zones.FirstOrDefault(z => z.Name == "Durotar");
                // the game's own flight masters on the region: the node hovers a few yards above the ground (cities are buildings, whose floors are not terrain: those are the outliers)
                var diffs = new List<double>();
                foreach (var nd in idx.Set.TaxiNodes.Where(nd => nd.MapId == 1))
                {
                    WorldMapMath.WorldToGrid(nd.X, nd.Y, out var oc, out var orr);
                    if (!set.TryGetValue((int)oc * 100 + (int)orr, out var ot)) continue;
                    diffs.Add(nd.Z - ot.Z((int)Math.Round((orr - Math.Floor(orr)) * 64), (int)Math.Round((oc - Math.Floor(oc)) * 64)));
                }
                diffs.Sort();
                double med = diffs.Count == 0 ? 999 : diffs[diffs.Count / 2];
                Check("real terrain: the height grid agrees with the game's flight masters in the region: " + diffs.Count + " nodes sit a median of " + med.ToString("0.0", I) + " yards above the grid (" + string.Join(", ", diffs.Select(d => d.ToString("0"))) + ")", diffs.Count >= 3 && Math.Abs(med) < 12, med + "");
                Check("real terrain: the region's borders list Durotar with an outline (" + (durotar?.Rings.Count ?? 0) + " rings, " + (durotar?.Rings.Sum(r => r.Length / 2) ?? 0) + " points, " + (durotar?.Chunks ?? 0) + " chunks) and " + ((borders?.Zones.Count ?? 0)) + " zones in all: " + string.Join(", ", borders?.Zones.Select(z => z.Name) ?? new string[0]),
                    borders != null && durotar != null && durotar.Rings.Count >= 1 && durotar.Chunks > 200, durotar?.Chunks + "");
                if (orgr != null && borders != null && durotar != null)
                {
                    var hit = WorldMapZones.ZoneAt(tdir, orgr.X, orgr.Y, zr);
                    var hitD = WorldMapZones.ZoneAt(tdir, 340, -4686, zr);       // Razor Hill
                    Check("real terrain: the zone of Orgrimmar's flight master is " + hit?.AreaName + " (zone " + hit?.ZoneName + "), Razor Hill's is " + hitD?.ZoneName + " / " + hitD?.AreaName,
                        hit != null && (hit.ZoneName == "Orgrimmar" || hit.ZoneName == "Durotar") && hit.AreaName == "Orgrimmar" && hitD != null && hitD.ZoneName == "Durotar", hit?.ZoneName + "/" + hit?.AreaName);
                    var zoneRings = borders.Zones.FirstOrDefault(z => z.Id == hit.ZoneId);
                    WorldMapMath.WorldToGrid(orgr.X, orgr.Y, out var gc, out var gr);
                    Check("real terrain: the flight master lies inside its own zone's outline", zoneRings != null && WorldMapZones.Contains(zoneRings.Rings, gc * 16, gr * 16));
                    Check("real terrain: the area maps to a zone map of the game (UiMap " + hit.UiMapId + " = " + idx.Name(hit.UiMapId) + ")", hit.UiMapId > 0 && idx.Get(hit.UiMapId) != null);
                }
                notes.Add("note real terrain: region " + res.Tiles + " tiles -> " + res.Files + " files " + (res.Bytes / 1024) + " KB in " + ms + " ms; managed memory " + GC.GetTotalMemory(true) / 1048576 + " MB");

                // the whole continents (slow): ELANSHUB_WORLDMAP_FULL=1
                if (Environment.GetEnvironmentVariable("ELANSHUB_WORLDMAP_FULL") == "1")
                {
                    WorldMapCache.RootOverride = Path.Combine(rw, "full");
                    WorldMapService.ResetForTest();
                    foreach (var c in WorldMapService.Continents(idx).Where(c => c.MapId == 0 || c.MapId == 1))
                    {
                        var s2 = System.Diagnostics.Stopwatch.StartNew(); long m0 = GC.GetTotalMemory(true);
                        bool built = await WorldMapService.EnsureTerrain(c.MapId);
                        var man = WorldMapCache.TerrainBuilt(c.MapId);
                        Check("real terrain (full): " + c.Name + " terrain is built: " + (man?.Tiles ?? 0) + " tiles, " + (man?.Files ?? 0) + " files, " + WorldMapCache.SizeText(man?.Bytes ?? 0) + ", " + (man?.Zones ?? 0) + " zones with borders, in " + s2.ElapsedMilliseconds / 1000.0 + " s", built && man != null && man.Tiles > 700 && man.Zones > 10);
                        var td = WorldMapCache.TerrainDir(WorldMapCache.BuildKey, c.MapId);
                        Check("real water (full): " + c.Name + " has a water file for every height grid: " + (man?.WaterFiles ?? 0) + " files, " + (man?.WaterLayers ?? 0) + " layers, " + (man?.WaterCells ?? 0) + " cells, " + WorldMapCache.SizeText(man?.WaterBytes ?? 0), man != null && man.WaterFiles == man.Files && man.WaterLayers > 20000 && man.WaterBytes < 8000000, man == null ? "?" : man.WaterFiles + "/" + man.Files + " " + man.WaterBytes);
                        notes.Add("note real water (full): " + c.Name + " " + (man?.WaterFiles ?? 0) + " water files = " + WorldMapCache.SizeText(man?.WaterBytes ?? 0) + " (" + (man?.WaterLayers ?? 0) + " layers, " + (man?.WaterCells ?? 0) + " cells) of the " + WorldMapCache.SizeText(man?.Bytes ?? 0) + " terrain folder; the whole terrain build took " + s2.ElapsedMilliseconds + " ms");
                        var b2 = WorldMapZones.ReadBorders(td);
                        notes.Add("note real terrain (full): " + c.Name + " " + (man?.Tiles ?? 0) + " tiles (missing " + man?.Missing + ") -> " + (man?.Files ?? 0) + " files, " + WorldMapCache.SizeText(man?.Bytes ?? 0) + ", " + s2.ElapsedMilliseconds + " ms (builder " + man?.Ms + " ms), z " + man?.MinZ.ToString("0", I) + " .. " + man?.MaxZ.ToString("0", I) + ", borders " + (b2?.Zones.Count ?? 0) + " zones / " + (b2?.Zones.Sum(z => z.Rings.Count) ?? 0) + " rings / " + (b2?.Zones.Sum(z => z.Rings.Sum(r => r.Length / 2)) ?? 0) + " points, borders.json " + (File.Exists(WorldMapTerrain.BordersPath(td)) ? new FileInfo(WorldMapTerrain.BordersPath(td)).Length / 1024 : 0) + " KB, managed memory +" + (GC.GetTotalMemory(true) - m0) / 1024 + " KB afterwards");
                        if (b2 != null)
                        {
                            // accuracy: every 5th chunk (of both directions) against the outlines: inside its own zone's rings and no other zone's
                            var wl = new List<WdtTile>(); foreach (var f in Directory.GetFiles(td, "*.wmt")) { var pp = Path.GetFileNameWithoutExtension(f).Split('_'); wl.Add(new WdtTile { Col = int.Parse(pp[0]), Row = int.Parse(pp[1]) }); }
                            var grid = WorldMapZones.ZoneGrid(td, wl, ZoneResolver.FromIndex(idx), out _);
                            var boxes = b2.Zones.Select(z => { double x0 = 1e9, y0 = 1e9, x1 = -1e9, y1 = -1e9; foreach (var r in z.Rings) for (int i = 0; i < r.Length; i += 2) { x0 = Math.Min(x0, r[i]); x1 = Math.Max(x1, r[i]); y0 = Math.Min(y0, r[i + 1]); y1 = Math.Max(y1, r[i + 1]); } return new double[] { x0, y0, x1, y1 }; }).ToList();
                            long total = 0, exact = 0, overlap = 0, missed = 0, other = 0, edge = 0;
                            for (int gy = 0; gy < WorldMapZones.Size; gy += 5) for (int gx = 0; gx < WorldMapZones.Size; gx += 5)
                            {
                                int zid = grid[gy * WorldMapZones.Size + gx]; if (zid == 0) continue;
                                total++; bool own = false; int others = 0;
                                for (int zi = 0; zi < b2.Zones.Count; zi++)
                                {
                                    var bx = boxes[zi]; double px = gx + 0.5, py = gy + 0.5; if (px < bx[0] || px > bx[2] || py < bx[1] || py > bx[3]) continue;
                                    if (WorldMapZones.Contains(b2.Zones[zi].Rings, px, py)) { if (b2.Zones[zi].Id == zid) own = true; else others++; }
                                }
                                bool nearBorder = false; for (int dy = -1; dy <= 1 && !nearBorder; dy++) for (int dx = -1; dx <= 1; dx++) { int nx = gx + dx, ny = gy + dy; if (nx < 0 || ny < 0 || nx >= WorldMapZones.Size || ny >= WorldMapZones.Size || grid[ny * WorldMapZones.Size + nx] != zid) { nearBorder = true; break; } }
                                if (own && others == 0) exact++; else if (own) { overlap++; if (nearBorder) edge++; } else if (others > 0) { other++; if (nearBorder) edge++; } else { missed++; if (nearBorder) edge++; }
                            }
                            Check("real terrain (full): " + c.Name + " zone outlines against the chunk grid (" + total + " sampled chunks): " + (100.0 * exact / Math.Max(1, total)).ToString("0.00", I) + " % exactly inside their own zone only; the rest are within one chunk of a border (" + edge + " of " + (total - exact) + ")", total > 5000 && exact / (double)total > 0.97 && edge == overlap + other + missed, exact + " " + overlap + " " + other + " " + missed + " edge " + edge);
                            notes.Add("note real terrain (full): " + c.Name + " outline accuracy over " + total + " sampled chunks: exact " + exact + ", own+neighbour " + overlap + ", only a neighbour " + other + ", in no zone " + missed + "; of the " + (total - exact) + " that differ " + edge + " lie next to a border (the simplification moves a border by up to 0.8 chunk = 27 yards)");
                        }
                        if (b2 != null) notes.Add("note real terrain (full): " + c.Name + " zones: " + string.Join(", ", b2.Zones.OrderByDescending(z => z.Chunks).Take(40).Select(z => z.Name + " " + z.Chunks)));
                    }
                }
            }
            finally
            {
                WorldMapCache.RootOverride = savedRootOverride; WorldMapService.ResetForTest();
            }
        }
    }
}
