using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Maps;
using ElansAddonHub.Services.WorldMap;

namespace ElansAddonHub
{
    // --selftest "worldmap", part 5 (2.53 phase 2b): liquids. The MH2O reader on invented chunks in every vertex layout (height + depth, height + uv, depth only,
    // height + uv + depth; the LiquidObject id the real files carry in the format field; exists masks; several layers in one chunk; no vertex data), the liquid kinds,
    // the .wmw water files and their cache (build, damaged / missing files, old caches, cancel and resume), and on the real game files (read-only): Orgrimmar has no
    // water over its canyon floor, Durotar's sea is ocean, Loch Modan has a lake, Searing Gorge and the Burning Steppes have lava.
    public partial class MainWindow
    {
        // ---------------------------------------------------------------- an invented sea, lake, lava and slime, as MH2O writes them
        sealed class WmLiq
        {
            public int Gx, Gy, Type, Fmt, Layout, X, Y, W, H;
            public bool HasExists, HasVerts;
            public Func<int, int, float> Ht;        // vertex (vx, vy) of the rectangle -> height; null for the depth-only layout
            public float Min, Max;
            public Func<int, bool> Bit;             // exists bit i of the rectangle
            public bool Has(int cx, int cy)
            {
                if (cx < X || cx >= X + W || cy < Y || cy >= Y + H) return false;
                return !HasExists || Bit((cy - Y) * W + (cx - X));
            }
            public float VertexH(int vx, int vy) => Ht == null ? Min : Ht(Math.Min(Math.Max(vx - X, 0), W), Math.Min(Math.Max(vy - Y, 0), H));
        }

        static WmLiq WmMk(int gx, int gy, int type, int fmt, int layout, int x, int y, int w, int h, Func<int, int, float> ht, Func<int, bool> bit, bool verts = true)
        {
            var l = new WmLiq { Gx = gx, Gy = gy, Type = type, Fmt = fmt, Layout = layout, X = x, Y = y, W = w, H = h, Ht = ht, Bit = bit, HasExists = bit != null, HasVerts = verts };
            l.Min = float.MaxValue; l.Max = float.MinValue;
            if (ht != null) for (int vy = 0; vy <= h; vy++) for (int vx = 0; vx <= w; vx++) { var v = ht(vx, vy); l.Min = Math.Min(l.Min, v); l.Max = Math.Max(l.Max, v); }
            return l;
        }

        static List<WmLiq> WmLiquidsAt(int gx, int gy)
        {
            var l = new List<WmLiq>();
            if (gy >= 56) l.Add(WmMk(gx, gy, 1250, 42, 0, 0, 0, 8, 8, (vx, vy) => 0f, null));                                                      // open sea (what the real tiles carry)
            if (gx >= 20 && gx <= 25 && gy >= 20 && gy <= 25)                                                                                         // a lake: a LiquidObject id, height + depth, holes
            {
                int g0 = gx - 20;
                l.Add(WmMk(gx, gy, 1240, 6001, 0, 0, 0, 8, 8, (vx, vy) => 165f + 0.5f * vx + 0.25f * vy + g0, i => (i / 8 + i % 8) % 5 != 0));
            }
            if (gx == 22 && gy == 22) l.Add(WmMk(gx, gy, 3, 3, 3, 2, 1, 4, 3, (vx, vy) => 170f + 0.1f * vx + 0.05f * vy, null));                    // a second layer in the same chunk: lava, classic format 3, a 4 x 3 rectangle
            if (gx >= 12 && gx <= 13 && gy >= 14 && gy <= 15) l.Add(WmMk(gx, gy, 4, 1, 1, 1, 2, 5, 4, (vx, vy) => 40f + 0.2f * vx + 0.4f * vy, i => i % 3 != 1));   // slime, height + uv
            if (gx == 40 && gy == 48) l.Add(WmMk(gx, gy, 1240, 2, 2, 0, 0, 8, 8, null, null)); // depth only: flat at min
            if (gx == 40 && gy == 48) { var d = l[l.Count - 1]; d.Min = 70f; d.Max = 72f; }
            if (gx == 30 && gy == 10) { var f = WmMk(gx, gy, 1250, 42, 0, 0, 0, 8, 8, null, null, false); f.Min = f.Max = 33f; l.Add(f); }         // no exists bitmap, no vertex data: flat at min
            return l;
        }

        // the MH2O body of a tile: 256 headers, the instances, then per instance its attributes, exists bitmap and vertex data (offsets from the start of the body)
        static byte[] WmMh2o(int col, int row)
        {
            var per = new List<WmLiq>[256];
            for (int c = 0; c < 256; c++) per[c] = WmLiquidsAt((col - 28) * 16 + c % 16, (row - 30) * 16 + c / 16);
            int pos = 3072, total = 0;
            var instOff = new int[256];
            for (int c = 0; c < 256; c++) { if (per[c].Count == 0) continue; instOff[c] = pos; pos += 24 * per[c].Count; total += per[c].Count; }
            if (total == 0) return new byte[256 * 12];
            var data = new MemoryStream(); var dw = new BinaryWriter(data);
            var inst = new MemoryStream(); var iw = new BinaryWriter(inst);
            var attrOf = new int[256];
            for (int c = 0; c < 256; c++)
            {
                if (per[c].Count == 0) continue;
                attrOf[c] = -1;
                foreach (var q in per[c])
                {
                    int attr = pos + (int)data.Length; if (attrOf[c] < 0) attrOf[c] = attr;
                    dw.Write(new byte[16]);
                    int eo = 0, vo = 0, n = (q.W + 1) * (q.H + 1);
                    if (q.HasExists)
                    {
                        eo = pos + (int)data.Length;
                        var bits = new byte[(q.W * q.H + 7) / 8];
                        for (int i = 0; i < q.W * q.H; i++) if (q.Bit(i)) bits[i / 8] |= (byte)(1 << (i % 8));
                        dw.Write(bits);
                    }
                    if (q.HasVerts)
                    {
                        vo = pos + (int)data.Length;
                        if (q.Layout != 2) for (int vy = 0; vy <= q.H; vy++) for (int vx = 0; vx <= q.W; vx++) dw.Write(q.Ht(vx, vy));
                        if (q.Layout == 1 || q.Layout == 3) for (int i = 0; i < n; i++) { dw.Write((ushort)(i * 3)); dw.Write((ushort)(i * 5)); }
                        if (q.Layout == 0 || q.Layout == 2 || q.Layout == 3) for (int i = 0; i < n; i++) dw.Write((byte)(i % 200));
                    }
                    iw.Write((ushort)q.Type); iw.Write((ushort)q.Fmt); iw.Write(q.Min); iw.Write(q.Max);
                    iw.Write((byte)q.X); iw.Write((byte)q.Y); iw.Write((byte)q.W); iw.Write((byte)q.H); iw.Write(eo); iw.Write(vo);
                }
            }
            var ms = new MemoryStream(); var w = new BinaryWriter(ms);
            for (int c = 0; c < 256; c++)
            {
                if (per[c].Count == 0) { w.Write(0); w.Write(0); w.Write(0); }
                else { w.Write(instOff[c]); w.Write(per[c].Count); w.Write(attrOf[c]); }
            }
            w.Write(inst.ToArray()); w.Write(data.ToArray());
            return ms.ToArray();
        }

        static ulong WmMask(WmLiq q) { ulong m = 0; for (int cy = 0; cy < 8; cy++) for (int cx = 0; cx < 8; cx++) if (q.Has(cx, cy)) m |= 1UL << (cy * 8 + cx); return m; }

        async Task WorldMapWaterTest(string dir, string work, List<string> notes, Action<bool> record)
        {
            void Check(string what, bool cond, string detail = null) { SelfTestClock.Mark(what); notes.Add((cond ? "ok   " : "FAIL ") + what + (!cond && detail != null ? " [" + detail + "]" : "")); record(cond); Util.Log("worldmap test " + (cond ? "ok " : "FAIL ") + what.Substring(0, Math.Min(80, what.Length))); }
            var I = CultureInfo.InvariantCulture;
            var savedRoot = WorldMapCache.RootOverride; var savedWdt = WorldMapService.WdtSourceForTest; var savedAdt = WorldMapService.AdtSourceForTest;
            try
            {
                var tw = Path.Combine(work, "water");
                if (Directory.Exists(tw)) Util.DeleteDir(tw);
                Directory.CreateDirectory(tw);
                WorldMapCache.RootOverride = Path.Combine(tw, "cache");
                WorldMapService.ResetForTest();
                var kinds = LiquidKinds.Fallbacks();

                // ---------------------------------------------------------------- the MH2O reader: every layout, masks, several layers
                var lakeTile = AdtFile.Parse(WmAdt(29, 31));
                var lakes = lakeTile.Liquids.Where(l => l.LiquidType == 1240).ToList();
                bool maskOk = true, hOk = true; double hErr = 0; int formatOk = 0;
                foreach (var l in lakes)
                {
                    int gx = (29 - 28) * 16 + l.Chunk % 16, gy = (31 - 30) * 16 + l.Chunk / 16;
                    var spec = WmLiquidsAt(gx, gy).First(q => q.Type == 1240);
                    if (l.Mask != WmMask(spec)) maskOk = false;
                    if (l.Format == 0) formatOk++;
                    if (l.Heights == null) { hOk = false; continue; }
                    for (int vy = 0; vy < 9; vy++) for (int vx = 0; vx < 9; vx++) hErr = Math.Max(hErr, Math.Abs(l.Heights[vy * 9 + vx] - spec.VertexH(vx, vy)));
                }
                Check("MH2O: a tile with a lake (36 chunks, the LiquidObject id 6001 in the format field, height + depth vertices, an exists bitmap with holes) gives 36 lake layers with their masks (" + (lakes.Count > 0 ? Convert.ToString((long)lakes[0].Mask, 2).Count(ch => ch == '1') : 0) + " of 64 cells on) and their 9 x 9 heights to " + hErr.ToString("0.0000", I) + " yd; the layout is told from the size of the vertex block (5 bytes a vertex)",
                    lakes.Count == 36 && maskOk && hOk && hErr < 1e-4 && formatOk == 36, lakes.Count + " " + maskOk + " " + hErr + " " + formatOk);
                var magma = lakeTile.Liquids.FirstOrDefault(l => l.LiquidType == 3);
                var mspec = WmLiquidsAt(22, 22).First(q => q.Type == 3);
                double mErr = 0; if (magma?.Heights != null) for (int vy = 0; vy < 9; vy++) for (int vx = 0; vx < 9; vx++) mErr = Math.Max(mErr, Math.Abs(magma.Heights[vy * 9 + vx] - mspec.VertexH(vx, vy)));
                var sameChunk = lakeTile.Liquids.Where(l => l.Chunk == 6 * 16 + 6).ToList();
                Check("MH2O: two layers in one chunk are both read (a lake and lava), the lava is a 4 x 3 rectangle at (2, 1) with no exists bitmap (12 cells, mask " + (magma?.Mask.ToString("X") ?? "?") +"), classic format 3 (height + uv + depth, 9 bytes a vertex): heights to " + mErr.ToString("0.0000", I) + " yd, clamped to the nearest vertex outside the rectangle",
                    sameChunk.Count == 2 && magma != null && magma.Chunk == 6 * 16 + 6 && magma.Mask == WmMask(mspec) && magma.Format == 3 && magma.Heights != null && mErr < 1e-4 && magma.X == 2 && magma.W == 4 && magma.H == 3, sameChunk.Count + " " + mErr);
                var slimeTile = AdtFile.Parse(WmAdt(28, 30));
                var slimes = slimeTile.Liquids.Where(l => l.LiquidType == 4).ToList();
                bool slOk = slimes.Count == 4; double slErr = 0;
                foreach (var l in slimes)
                {
                    var spec = WmLiquidsAt(12 + l.Chunk % 16 - 12, l.Chunk / 16).First(q => q.Type == 4);
                    if (l.Mask != WmMask(spec) || l.Format != 1 || l.Heights == null) { slOk = false; continue; }
                    for (int vy = 0; vy < 9; vy++) for (int vx = 0; vx < 9; vx++) slErr = Math.Max(slErr, Math.Abs(l.Heights[vy * 9 + vx] - spec.VertexH(vx, vy)));
                }
                Check("MH2O: height + uv vertices (8 bytes a vertex, classic format 1) with an exists bitmap over a 5 x 4 rectangle: " + slimes.Count + " slime layers, masks and heights (error " + slErr.ToString("0.0000", I) + " yd) as written", slOk && slErr < 1e-4, slimes.Count + " " + slErr);
                var depthTile = AdtFile.Parse(WmAdt(30, 33)); var flatTile = AdtFile.Parse(WmAdt(29, 30));
                var dep = depthTile.Liquids.FirstOrDefault(l => l.LiquidType == 1240); var nov = flatTile.Liquids.FirstOrDefault(l => l.LiquidType == 1250);
                Check("MH2O: a depth-only block (1 byte a vertex) has no heights - the layer is flat at its minimum (70) and fully on; a layer with no vertex data and no bitmap is flat at its minimum (33), all 64 cells on",
                    dep != null && dep.Format == 2 && dep.Heights == null && Math.Abs(dep.MinHeight - 70) < 1e-4 && dep.Mask == ulong.MaxValue && nov != null && nov.Format == -1 && nov.Heights == null && Math.Abs(nov.MinHeight - 33) < 1e-4 && nov.Mask == ulong.MaxValue, dep?.Format + " " + nov?.Format);
                var seaTile = AdtFile.Parse(WmAdt(28, 33));
                Check("MH2O: the open sea along the south edge is 128 ocean layers (8 rows of 16 chunks, type 1250, a LiquidObject id of 42, height 0, all cells) - what the real ocean tiles look like",
                    seaTile.Liquids.Count == 128 && seaTile.Liquids.All(l => l.LiquidType == 1250 && l.Mask == ulong.MaxValue && l.Heights != null && l.Heights.All(h => h == 0) && l.Chunk >= 128) && AdtFile.Parse(WmAdt(28, 31)).Liquids.Count == 0, seaTile.Liquids.Count + "");
                var plainTags = AdtFile.Parse(WmAdt(29, 31, reversedTags: false));
                var cutTile = AdtFile.Parse(WmAdt(29, 31, truncate: true));
                var shortMh2o = new AdtFile(); AdtFile.ReadLiquids(shortMh2o, new byte[100], 0, 100);
                var wildMh2o = new byte[3072 + 24]; BitConverter.GetBytes(3072).CopyTo(wildMh2o, 0); BitConverter.GetBytes(1).CopyTo(wildMh2o, 4); wildMh2o[3072 + 12] = 7; wildMh2o[3072 + 14] = 9;     // x 7 + width 9: outside the chunk
                var wild = new AdtFile(); AdtFile.ReadLiquids(wild, wildMh2o, 0, wildMh2o.Length);
                Check("MH2O: tags the right way round are read too, a file cut in half keeps what it has, a body too short or an instance outside the chunk's 8 x 8 cells is skipped without an error",
                    plainTags.Liquids.Count == 37 && cutTile.Ok && shortMh2o.Liquids.Count == 0 && wild.Liquids.Count == 0, plainTags.Liquids.Count + "");

                // ---------------------------------------------------------------- the kinds
                var named = new[]
                {
                    LiquidKinds.ByName("PBRWater - Generic - Lake", 0), LiquidKinds.ByName("PBRWater - Generic - Ocean", 0), LiquidKinds.ByName("Magma", 2), LiquidKinds.ByName("10FX - Generic - Lava - Molten - Exterior - (0.5 Depth)", 2),
                    LiquidKinds.ByName("1.60.0 FX - Fel - Generic - Bright - Lake", 2), LiquidKinds.ByName("Naxxramas - Slime", 3), LiquidKinds.ByName("Something new", 1), LiquidKinds.ByName(null, 0), LiquidKinds.ByName("Something new", 3),
                };
                Check("liquid kinds: a LiquidType row becomes a kind by its name first (ocean, lava / magma / molten, slime / fel) and by its sound bank otherwise (0 water, 1 ocean, 2 magma, 3 slime)",
                    string.Join(",", named) == "Water,Ocean,Magma,Magma,Slime,Slime,Ocean,Water,Slime", string.Join(",", named));
                Check("liquid kinds: without the table the well-known ids are used (1 / 5 / 1240 / 1350 water, 2 / 1250 ocean, 3 / 19 / 1267 magma, 4 / 1269 slime, anything unknown water); the table is read through its verified layout (table hash 6613BED3, layout D1ECEEC9) and an unknown layout or a missing file says so and falls back",
                    LiquidKinds.Fallback(1) == LiquidKind.Water && LiquidKinds.Fallback(5) == LiquidKind.Water && LiquidKinds.Fallback(1240) == LiquidKind.Water && LiquidKinds.Fallback(1350) == LiquidKind.Water && LiquidKinds.Fallback(2) == LiquidKind.Ocean && LiquidKinds.Fallback(1250) == LiquidKind.Ocean
                    && LiquidKinds.Fallback(3) == LiquidKind.Magma && LiquidKinds.Fallback(19) == LiquidKind.Magma && LiquidKinds.Fallback(1267) == LiquidKind.Magma && LiquidKinds.Fallback(4) == LiquidKind.Slime && LiquidKinds.Fallback(1269) == LiquidKind.Slime && LiquidKinds.Fallback(99999) == LiquidKind.Water
                    && MapTables.LiquidType.Knows(0xD1ECEEC9) && MapTables.LiquidType.TableHash == 0x6613BED3 && !MapTables.LiquidType.Knows(1) && LiquidKinds.FromDb2(null).Problem != null && LiquidKinds.FromDb2(new byte[64]).Problem != null && !LiquidKinds.FromDb2(null).FromTable);

                // ---------------------------------------------------------------- the water file
                var wt = WaterTile.FromAdt(29, 31, lakeTile, kinds);
                var rt = WaterTile.FromBytes(wt.ToBytes());
                var seaWt = WaterTile.FromAdt(28, 33, seaTile, kinds);
                var flatWt = WaterTile.FromAdt(29, 30, flatTile, kinds);
                bool same = rt != null && rt.Col == 29 && rt.Row == 31 && rt.Layers.Count == wt.Layers.Count;
                if (same) for (int i = 0; i < wt.Layers.Count; i++) { var a = wt.Layers[i]; var b = rt.Layers[i]; if (a.Chunk != b.Chunk || a.Kind != b.Kind || a.Mask != b.Mask || a.TypeId != b.TypeId || a.Flat != b.Flat || (a.Flat && a.Height != b.Height) || (!a.Flat && !a.Heights.SequenceEqual(b.Heights))) same = false; }
                var bytes = wt.ToBytes();
                Check("water file: a tile becomes layers of {chunk, kind, type, 64-bit cell mask, one height or a 9 x 9 grid} (" + wt.Layers.Count + " layers: " + wt.Layers.Count(l => l.Kind == LiquidKind.Water) + " water, " + wt.Layers.Count(l => l.Kind == LiquidKind.Magma) + " magma, " + bytes.Length + " bytes) and reads back identically; a flat ocean chunk is 20 bytes (the 128-chunk sea tile is " + seaWt.ToBytes().Length + " bytes), an empty tile 16",
                    same && wt.Layers.Count(l => l.Kind == LiquidKind.Water) == 36 && wt.Layers.Count(l => l.Kind == LiquidKind.Magma) == 1 && seaWt.Layers.All(l => l.Flat && l.Kind == LiquidKind.Ocean) && seaWt.ToBytes().Length == 16 + 128 * 20
                    && new WaterTile { Col = 3, Row = 4 }.ToBytes().Length == 16 && flatWt.Layers.Count == 1 && flatWt.Layers[0].Flat && Math.Abs(flatWt.Layers[0].Height - 33) < 1e-4, same + " " + bytes.Length);
                var badMagic = (byte[])bytes.Clone(); badMagic[0] = (byte)'X';
                var badKind = (byte[])bytes.Clone(); badKind[16 + 1] = 9;
                var extra = bytes.Concat(new byte[] { 1 }).ToArray();
                Check("water file: a damaged one (wrong magic, cut short, an extra byte, an unknown kind, empty) is refused", WaterTile.FromBytes(badMagic) == null && WaterTile.FromBytes(bytes.Take(bytes.Length - 3).ToArray()) == null && WaterTile.FromBytes(extra) == null && WaterTile.FromBytes(badKind) == null && WaterTile.FromBytes(new byte[0]) == null && WaterTile.FromBytes(null) == null);
                // a point query on the file: the lake's holes are dry, its wet cells have the height of their four corners, the lava is above the lake
                var cell = wt.SurfaceAt(6 * 16 + 6, 4, 4); var hole = wt.SurfaceAt(6 * 16 + 6, 0, 0); var lava = wt.SurfaceAt(6 * 16 + 6, 3, 3); var dry = wt.SurfaceAt(0, 3, 3);
                var lspec = WmLiquidsAt(22, 22).First(q => q.Type == 1240);
                float expectCell = (lspec.VertexH(4, 4) + lspec.VertexH(5, 4) + lspec.VertexH(4, 5) + lspec.VertexH(5, 5)) / 4;
                Check("water file: the surface over a cell is the mean of its four corners (" + cell.ToString("0.00", I) + " expected " + expectCell.ToString("0.00", I) + "), a hole in the mask and a chunk without liquid are dry, where lava and lake overlap the higher surface counts (" + lava.ToString("0.00", I) + ")",
                    Math.Abs(cell - expectCell) < 1e-3 && float.IsNaN(hole) && float.IsNaN(dry) && lava > 170 && lava < 171, cell + " " + hole + " " + lava);

                // ---------------------------------------------------------------- the cache: files, manifest, old caches, damaged and missing files, cancel and resume
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
                var key = "9.9.9.9-water_p1"; var mapDir = WorldMapCache.MapDir(key, 5);
                var res = WorldMapService.BuildTerrain(5, "Testland", 7001, key, mapDir, null, zr, CancellationToken.None, null);
                var tdir = WorldMapTerrain.Dir(mapDir); var man = WorldMapTerrain.ReadManifest(tdir);
                int wmw = Directory.GetFiles(tdir, "*.wmw").Length, wmt = Directory.GetFiles(tdir, "*.wmt").Length;
                var oneWater = WorldMapTerrain.ReadWater(tdir, 29, 31);
                Check("water cache: the terrain build writes a .wmw next to every .wmt (" + wmw + " water files for " + wmt + " height grids; " + res.WaterLayers + " layers, " + res.WaterCells + " cells, " + res.WaterBytes + " bytes), the manifest says " + (man?.WaterFiles ?? 0) + " files / " + (man?.WaterLayers ?? 0) + " layers and is version " + (man?.Version ?? 0) + ", no .tmp is left",
                    res.Complete && wmw == 30 && wmt == 30 && res.WaterFiles == 30 && res.WaterWritten == 30 && man != null && man.Valid && man.Version == WorldMapTerrain.Version && man.WaterFiles == 30 && man.WaterLayers == res.WaterLayers && res.WaterLayers > 500 && man.WaterBytes == res.WaterBytes
                    && oneWater != null && oneWater.Layers.Count == 37 && !Directory.GetFiles(tdir, "*.tmp").Any(), wmw + " " + wmt + " " + res.WaterWritten);
                // an old (version 1) cache: no water files, a manifest of the old version
                File.WriteAllText(WorldMapTerrain.ManifestPath(tdir), "{\"v\":1,\"mapId\":5,\"build\":\"x\",\"tiles\":30,\"missing\":2,\"files\":30,\"zones\":3,\"bytes\":1000,\"ms\":5}");
                Check("water cache: a manifest of the old format (version 1, no liquids) is not valid, so the terrain is rebuilt", WorldMapTerrain.ReadManifest(tdir) != null && !WorldMapTerrain.ReadManifest(tdir).Valid && WorldMapTerrain.Version >= 2 && WorldMapCache.TerrainBuilt(5, key) == null);
                foreach (var f in Directory.GetFiles(tdir, "*.wmw")) File.Delete(f);
                var wmtBefore = Directory.GetFiles(tdir, "*.wmt").ToDictionary(f => f, f => File.GetLastWriteTimeUtc(f));
                int loadsBefore = adtLoads;
                var upgraded = WorldMapService.BuildTerrain(5, "Testland", 7001, key, mapDir, null, zr, CancellationToken.None, null);
                bool wmtUntouched = Directory.GetFiles(tdir, "*.wmt").All(f => wmtBefore.TryGetValue(f, out var t0) && t0 == File.GetLastWriteTimeUtc(f));
                Check("water cache: a build over an old cache (height grids there, water files not) reads each tile's ADT once more, writes only the water files (" + upgraded.WaterWritten + ") and leaves the height grids alone",
                    upgraded.Complete && upgraded.WaterWritten == 30 && upgraded.Written == 0 && upgraded.Files == 30 && wmtUntouched && adtLoads - loadsBefore == 32 && WorldMapCache.TerrainBuilt(5, key) != null, upgraded.WaterWritten + " " + upgraded.Written + " " + (adtLoads - loadsBefore));
                // a damaged and a missing water file are made again; everything else is reused
                var wfiles = Directory.GetFiles(tdir, "*.wmw").OrderBy(f => f).ToList();
                File.WriteAllBytes(wfiles[0], new byte[10]); File.Delete(wfiles[1]);
                loadsBefore = adtLoads;
                var healed = WorldMapService.BuildTerrain(5, "Testland", 7001, key, mapDir, null, zr, CancellationToken.None, null);
                Check("water cache: a damaged and a missing water file are made again from their tile (" + healed.WaterWritten + "), the other 28 tiles are reused from disk", healed.Complete && healed.WaterWritten == 2 && healed.Reused == 28 && healed.Written == 0 && adtLoads - loadsBefore == 4 && Directory.GetFiles(tdir, "*.wmw").Length == 30
                    && WorldMapTerrain.ReadWater(tdir, 29, 31) != null, healed.WaterWritten + " " + healed.Reused + " " + (adtLoads - loadsBefore));
                var again = WorldMapService.BuildTerrain(5, "Testland", 7001, key, mapDir, null, zr, CancellationToken.None, null);
                Check("water cache: with every file there a build reads 30 tiles back and writes nothing", again.Complete && again.Reused == 30 && again.Written == 0 && again.WaterWritten == 0 && again.WaterFiles == 30, again.Reused + " " + again.WaterWritten);
                // cancel and resume
                var mapDir2 = WorldMapCache.MapDir(key, 6); adtLoads = 0;
                var cts = new CancellationTokenSource(); int seen = 0;
                WorldMapService.AdtSourceForTest = fdid => { if (Interlocked.Increment(ref seen) == 9) cts.Cancel(); var (c, r) = WmAdtOf(fdid); return WmAdtCached(c, r); };
                var cancelled = Assert(() => WorldMapService.BuildTerrain(6, "Testland", 7001, key, mapDir2, null, zr, cts.Token, null));
                var tdir2 = WorldMapTerrain.Dir(mapDir2);
                int pw = Directory.GetFiles(tdir2, "*.wmw").Length, pt = Directory.GetFiles(tdir2, "*.wmt").Length;
                WorldMapService.AdtSourceForTest = fdid => { Interlocked.Increment(ref adtLoads); var (c, r) = WmAdtOf(fdid); return WmAdtCached(c, r); };
                var resumed = WorldMapService.BuildTerrain(6, "Testland", 7001, key, mapDir2, null, zr, CancellationToken.None, null);
                Check("water cache: a cancelled build stops between tiles (" + pw + " water files and " + pt + " height grids written, no manifest, no .tmp); the next one reads only the tiles not finished (" + adtLoads + " of 32) and completes",
                    cancelled != null && cancelled.StartsWith("OperationCanceledException") && pw == pt && pw >= 7 && pw < 32 && !Directory.GetFiles(tdir2, "*.tmp").Any() && resumed.Complete && resumed.Reused == pw && adtLoads == 32 - pw && resumed.WaterFiles == 32 && WorldMapCache.TerrainBuilt(6, key) != null, cancelled + " " + pw + " " + pt + " loads " + adtLoads);
                // a LiquidType source for the test: an unreadable table falls back and still builds
                WorldMapService.LiquidTypeSourceForTest = () => new byte[40];
                var mapDir3 = WorldMapCache.MapDir(key, 8);
                var fb = WorldMapService.BuildTerrain(8, "Testland", 7001, key, mapDir3, t => t.Col == 29 && t.Row == 31, zr, CancellationToken.None, null);
                var fbw = WorldMapTerrain.ReadWater(WorldMapTerrain.Dir(mapDir3), 29, 31);
                WorldMapService.LiquidTypeSourceForTest = null;
                Check("water cache: with a LiquidType table that cannot be read the well-known ids are used and the build still finishes (" + (fbw?.Layers.Count(l => l.Kind == LiquidKind.Water) ?? 0) + " water layers, " + (fbw?.Layers.Count(l => l.Kind == LiquidKind.Magma) ?? 0) + " magma)", fb.Complete && fbw != null && fbw.Layers.Count(l => l.Kind == LiquidKind.Water) == 36 && fbw.Layers.Count(l => l.Kind == LiquidKind.Magma) == 1);
                notes.Add("note water: synthetic continent 32 tiles -> " + res.WaterFiles + " water files, " + res.WaterLayers + " layers, " + res.WaterCells + " cells, " + res.WaterBytes + " bytes (" + res.WaterBytes / Math.Max(1, res.WaterFiles) + " bytes a tile)");
                WorldMapCache.Clear();
            }
            finally
            {
                WorldMapService.LiquidTypeSourceForTest = null;
                WorldMapService.AdtSourceForTest = savedAdt; WorldMapService.WdtSourceForTest = savedWdt; WorldMapCache.RootOverride = savedRoot;
                WorldMapService.ResetForTest();
            }
            await Task.CompletedTask;
        }

        // ---------------------------------------------------------------- the real game files (read-only; optional)
        static string WmPointKinds(WaterTile t) => t == null ? "?" : string.Join(", ", t.Layers.GroupBy(l => l.Kind).Select(g => g.Key + " " + g.Sum(l => l.Cells)));

        // the world position (x north, y west) of the middle of a chunk
        static void WmChunkWorld(int col, int row, int chunk, out double x, out double y)
        {
            WorldMapMath.GridToWorld(col + (chunk % 16 + 0.5) / 16.0, row + (chunk / 16 + 0.5) / 16.0, out x, out y);
        }

        async Task WorldMapWaterRealChecks(MapIndex idx, string real, string rw, List<string> notes, CheckFn Check)
        {
            var I = CultureInfo.InvariantCulture;
            var savedRootOverride = WorldMapCache.RootOverride;
            try
            {
                var conts = WorldMapService.Continents(idx);
                var kal = conts.FirstOrDefault(c => c.MapId == 1); var ek = conts.FirstOrDefault(c => c.MapId == 0);
                if (kal == null || ek == null) { notes.Add("skip real water: no Kalimdor / Eastern Kingdoms"); return; }
                WorldMapCache.RootOverride = Path.Combine(rw, "water-cache");
                WorldMapService.ResetForTest();
                var zr = ZoneResolver.FromIndex(idx); var key = WorldMapCache.BuildKey;

                // the real LiquidType.db2 through its verified layout
                LiquidKinds kinds; string ltInfo;
                using (var casc = CascStorage.Open(real, "_classic_beta_"))
                {
                    casc.ZeroFillEncrypted = true;
                    var k = casc.ResolveFileIds(new HashSet<int> { MapTables.LiquidType.FileDataId });
                    var raw = k.TryGetValue(MapTables.LiquidType.FileDataId, out var ck) ? casc.ReadByContentKey(ck) : null;
                    var w = raw == null ? null : Wdc5.Parse(raw);
                    ltInfo = w == null ? "missing" : "table " + w.TableHash.ToString("X8") + " layout " + w.LayoutHash.ToString("X8") + " rows " + w.Count;
                    kinds = LiquidKinds.FromDb2(raw);
                }
                Check("real water: LiquidType.db2 is the table the Outpost expects (" + ltInfo + "): 57 rows read, 1 Water, 2 Ocean, 3 Magma, 4 Slime, 1240 \"PBRWater - Generic - Lake\" = water, 1250 \"... Ocean\" = ocean, 1269 \"Fel ... Lake\" = slime, 1267 \"Lava\" = magma, 1350 \"... Orgrimmar\" = water",
                    kinds.FromTable && kinds.Rows == 57 && kinds.Of(1) == LiquidKind.Water && kinds.Of(2) == LiquidKind.Ocean && kinds.Of(3) == LiquidKind.Magma && kinds.Of(4) == LiquidKind.Slime && kinds.Of(1240) == LiquidKind.Water && kinds.Of(1250) == LiquidKind.Ocean
                    && kinds.Of(1269) == LiquidKind.Slime && kinds.Of(1267) == LiquidKind.Magma && kinds.Of(1350) == LiquidKind.Water, ltInfo + " " + kinds.Problem);

                // Durotar, Orgrimmar and the sea east of it (map tiles col 38..42, row 27..33)
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var res = WorldMapService.BuildTerrain(1, kal.Name, kal.WdtFileDataId, key, WorldMapCache.MapDir(key, 1), t => t.Col >= 38 && t.Col <= 42 && t.Row >= 27 && t.Row <= 33, zr, CancellationToken.None, null);
                var td = WorldMapCache.TerrainDir(key, 1);
                Check("real water: the region (" + res.Tiles + " tiles of Kalimdor) gets a water file per tile: " + res.WaterFiles + " files, " + res.WaterLayers + " layers, " + res.WaterCells + " cells, " + res.WaterBytes / 1024 + " KB, in " + sw.ElapsedMilliseconds + " ms with the terrain", res.Complete && res.WaterFiles == res.Files && res.WaterLayers > 1000 && res.WaterBytes < 3000000, res.WaterFiles + " " + res.Files);
                // Orgrimmar's canyon floor lies below sea level, and has no water: the tile of the city has terrain below z 0.4 and not one liquid cell
                var orgr = idx.Set.TaxiNodes.FirstOrDefault(nd => nd.Name == "Orgrimmar, Durotar");
                if (orgr != null)
                {
                    WorldMapMath.WorldToGrid(orgr.X, orgr.Y, out var gcol, out var grow);
                    int oc = (int)gcol, orow = (int)grow;
                    var wOrg = WorldMapTerrain.ReadWater(td, oc, orow); var tOrg = WorldMapTerrain.ReadTile(td, oc, orow);
                    int below = 0; if (tOrg != null) for (int r = 0; r < 65; r++) for (int c = 0; c < 65; c++) if (tOrg.Z(r, c) < 0.4f) below++;
                    Check("real water: Orgrimmar's map tile (" + oc + "," + orow + ") has " + below + " of its 4225 height samples below z 0.4 - where the old flat sea plane flooded the city - and " + (wOrg?.Cells ?? -1) + " liquid cells: the canyon floor is dry", wOrg != null && tOrg != null && below > 500 && wOrg.Cells == 0, below + " " + wOrg?.Cells);
                    int ix = (int)((gcol - oc) * 16), iy = (int)((grow - orow) * 16), cx = (int)(((gcol - oc) * 16 - ix) * 8), cy = (int)(((grow - orow) * 16 - iy) * 8);
                    Check("real water: right under Orgrimmar's flight master there is no liquid (chunk " + ix + "," + iy + " cell " + cx + "," + cy + ")", wOrg != null && float.IsNaN(wOrg.SurfaceAt(iy * 16 + ix, cx, cy)));
                    // and in the neighbouring tile, the city's pond: whatever liquid there is lies in a few chunks
                    var wSouth = WorldMapTerrain.ReadWater(td, oc, orow + 1);
                    notes.Add("note real water: Orgrimmar tile " + oc + "," + orow + " " + (wOrg?.Cells ?? 0) + " cells, the next tile south " + (wSouth?.Cells ?? 0) + " cells (" + WmPointKinds(wSouth) + ")");
                }
                // the sea: the tile east of Durotar is open ocean, the tile of the coast is part land part ocean
                var wSea = WorldMapTerrain.ReadWater(td, 42, 30); var tSea = WorldMapTerrain.ReadTile(td, 42, 30);
                Check("real water: the tile east of Durotar (42,30) is open sea: " + (wSea?.Layers.Count ?? 0) + " ocean layers, all flat at z 0, 16384 cells, while its sea floor lies at z " + (tSea?.MinZ.ToString("0", I) ?? "?") + " .. " + (tSea?.MaxZ.ToString("0", I) ?? "?"),
                    wSea != null && tSea != null && wSea.Layers.Count == 256 && wSea.Layers.All(l => l.Kind == LiquidKind.Ocean && l.Flat && Math.Abs(l.Height) < 0.01f) && wSea.Cells == 16384 && tSea.MaxZ < 0);
                var wCoast = WorldMapTerrain.ReadWater(td, 41, 29); var tCoast = WorldMapTerrain.ReadTile(td, 41, 29);
                int coastOcean = wCoast?.Layers.Where(l => l.Kind == LiquidKind.Ocean).Sum(l => l.Cells) ?? 0;
                Check("real water: the coast tile (41,29) has " + coastOcean + " ocean cells next to dry land (" + (16384 - (wCoast?.Cells ?? 0)) + " cells without liquid)", wCoast != null && coastOcean > 1500 && coastOcean < 15000 && wCoast.Cells < 16384);
                // consistency over the region: a liquid cell lies over lower ground (the sea floor, the lake bed), judged on the 65 x 65 grid
                long wet = 0, wetUnder = 0;
                foreach (var f in Directory.GetFiles(td, "*.wmw"))
                {
                    var p = Path.GetFileNameWithoutExtension(f).Split('_'); int c0 = int.Parse(p[0]), r0 = int.Parse(p[1]);
                    var wtl = WorldMapTerrain.ReadWater(td, c0, r0); var ttl = WorldMapTerrain.ReadTile(td, c0, r0);
                    if (wtl == null || ttl == null) continue;
                    foreach (var l in wtl.Layers)
                        for (int cy2 = 0; cy2 < 8; cy2++) for (int cx2 = 0; cx2 < 8; cx2++)
                        {
                            if (!l.Has(cx2, cy2)) continue;
                            int rr = Math.Min(64, (int)Math.Round(((l.Chunk / 16) * 8 + cy2 + 0.5) / 2.0)), cc = Math.Min(64, (int)Math.Round(((l.Chunk % 16) * 8 + cx2 + 0.5) / 2.0));
                            wet++; if (ttl.Z(rr, cc) <= l.HeightAt(cy2, cx2) + 0.5f) wetUnder++;
                        }
                }
                Check("real water: over the region " + wet + " liquid cells were checked against the terrain grid and " + (100.0 * wetUnder / Math.Max(1, wet)).ToString("0.0", I) + " % of them lie over lower ground (the grid is coarse near shores)", wet > 100000 && wetUnder / (double)wet > 0.9, wetUnder + "/" + wet);

                // Eastern Kingdoms: Loch Modan's lake, Searing Gorge and the Burning Steppes' lava
                var res2 = WorldMapService.BuildTerrain(0, ek.Name, ek.WdtFileDataId, key, WorldMapCache.MapDir(key, 0), t => (t.Col >= 37 && t.Col <= 39 && t.Row >= 41 && t.Row <= 43) || (t.Col >= 32 && t.Col <= 36 && t.Row >= 43 && t.Row <= 48), zr, CancellationToken.None, null);
                var td0 = WorldMapCache.TerrainDir(key, 0);
                var lakeCells = new Dictionary<string, int>(); var magmaZones = new Dictionary<string, int>(); int lavaCells = 0, lakeAll = 0;
                foreach (var f in Directory.GetFiles(td0, "*.wmw"))
                {
                    var p = Path.GetFileNameWithoutExtension(f).Split('_'); int c0 = int.Parse(p[0]), r0 = int.Parse(p[1]);
                    var wtl = WorldMapTerrain.ReadWater(td0, c0, r0); var ttl = WorldMapTerrain.ReadTile(td0, c0, r0);
                    if (wtl == null || ttl == null) continue;
                    foreach (var l in wtl.Layers)
                    {
                        string zn = zr.NameOf(zr.ZoneOf(ttl.Area[l.Chunk]));
                        if (l.Kind == LiquidKind.Water) { lakeAll += l.Cells; lakeCells[zn] = (lakeCells.TryGetValue(zn, out var a) ? a : 0) + l.Cells; }
                        if (l.Kind == LiquidKind.Magma) { lavaCells += l.Cells; magmaZones[zn] = (magmaZones.TryGetValue(zn, out var a) ? a : 0) + l.Cells; }
                    }
                }
                int loch = lakeCells.TryGetValue("Loch Modan", out var lm) ? lm : 0;
                Check("real water: Loch Modan has its lake: " + loch + " water cells in chunks of the zone Loch Modan (" + lakeAll + " water cells in the nine tiles around it)", res2.Complete && loch > 200, string.Join(", ", lakeCells.OrderByDescending(x => x.Value).Take(6).Select(x => x.Key + " " + x.Value)));
                int sg = magmaZones.TryGetValue("Searing Gorge", out var s1) ? s1 : 0, bs = magmaZones.TryGetValue("Burning Steppes", out var s2) ? s2 : 0;
                Check("real water: Searing Gorge (" + sg + " cells) and the Burning Steppes (" + bs + " cells) have lava (Searing Gorge has only a little) - " + lavaCells + " magma cells in all, by zone: " + string.Join(", ", magmaZones.OrderByDescending(x => x.Value).Select(x => x.Key + " " + x.Value)), sg > 0 && bs > 1000, string.Join(", ", magmaZones.Select(x => x.Key + " " + x.Value)));
                notes.Add("note real water: Durotar region " + res.WaterFiles + " files " + res.WaterBytes / 1024 + " KB; Eastern Kingdoms sample " + res2.WaterFiles + " files " + res2.WaterBytes / 1024 + " KB");
                WorldMapCache.Clear();
            }
            finally
            {
                WorldMapCache.RootOverride = savedRootOverride; WorldMapService.ResetForTest();
            }
            await Task.CompletedTask;
        }
    }
}
