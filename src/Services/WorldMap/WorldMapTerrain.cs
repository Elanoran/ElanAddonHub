using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace ElansAddonHub.Services.WorldMap
{
    // ============================================================================================================================
    // The height grid of one map tile, made from its root ADT and kept on disk:   <map folder>\terrain\{col}_{row}.wmt
    //   "WMT1" | int col | int row | float minZ | float maxZ | ushort heights[65 x 65] | int areaId[16 x 16]       (9,494 bytes)
    //   heights: row-major, r = 0 at the tile's NORTH edge (world x falls with r), c = 0 at its WEST edge (the column grows eastward, world y falls
    //   with c); sample (r, c) is at world x = (32 - row) * 533.33 - r * 8.333, y = (32 - col) * 533.33 - c * 8.333; z = minZ + h / 65535 * (maxZ - minZ).
    //   areaId: per terrain chunk (33.33 yards), cy = north -> south, cx = west -> east.
    // 65 x 65 = every second outer vertex of the tile's 129 x 129 lattice (the ADT's MCVT outer vertices, 4.17 yards apart): 8.33 yards per sample,
    // 9.3 KB per tile, so Kalimdor is about 9 MB and a mesh at any of the four LODs (stride 1, 2, 4, 8 = 65, 33, 17, 9 samples) is cut from the same data.
    // The inner vertices of the ADT are left out (they only add detail finer than 8 yards, which the minimap texture cannot show anyway).
    // ============================================================================================================================
    public sealed class TerrainTile
    {
        public const int Samples = 65, Chunks = 16;
        public int Col, Row;
        public float MinZ, MaxZ;
        public ushort[] H = new ushort[Samples * Samples];
        public int[] Area = new int[Chunks * Chunks];
        public int MissingChunks;

        public float Z(int r, int c) => MaxZ <= MinZ ? MinZ : MinZ + H[r * Samples + c] / 65535f * (MaxZ - MinZ);

        public byte[] ToBytes()
        {
            var ms = new MemoryStream(4 + 8 + 8 + H.Length * 2 + Area.Length * 4);
            var w = new BinaryWriter(ms);
            w.Write(new[] { (byte)'W', (byte)'M', (byte)'T', (byte)'1' });
            w.Write(Col); w.Write(Row); w.Write(MinZ); w.Write(MaxZ);
            foreach (var h in H) w.Write(h);
            foreach (var a in Area) w.Write(a);
            w.Flush();
            return ms.ToArray();
        }

        public const int FileBytes = 4 + 8 + 8 + Samples * Samples * 2 + Chunks * Chunks * 4;

        public static TerrainTile FromBytes(byte[] b)
        {
            if (b == null || b.Length != FileBytes || b[0] != 'W' || b[1] != 'M' || b[2] != 'T' || b[3] != '1') return null;
            var t = new TerrainTile { Col = BitConverter.ToInt32(b, 4), Row = BitConverter.ToInt32(b, 8), MinZ = BitConverter.ToSingle(b, 12), MaxZ = BitConverter.ToSingle(b, 16) };
            if (float.IsNaN(t.MinZ) || float.IsNaN(t.MaxZ) || t.MaxZ < t.MinZ) return null;
            int o = 20;
            for (int i = 0; i < t.H.Length; i++, o += 2) t.H[i] = BitConverter.ToUInt16(b, o);
            for (int i = 0; i < t.Area.Length; i++, o += 4) t.Area[i] = BitConverter.ToInt32(b, o);
            return t;
        }

        // the 129 x 129 lattice of outer vertices of the whole tile (null entry = chunk missing) and the 65 x 65 grid from it
        public static TerrainTile FromAdt(int col, int row, AdtFile f)
        {
            const int N = 129;
            var lat = new float[N * N];
            var have = new bool[N * N];
            var t = new TerrainTile { Col = col, Row = row };
            for (int iy = 0; iy < 16; iy++)
                for (int ix = 0; ix < 16; ix++)
                {
                    var c = f.Chunks[iy * 16 + ix];
                    if (c == null || c.Heights == null) { t.MissingChunks++; continue; }
                    t.Area[iy * 16 + ix] = c.AreaId;
                    for (int k = 0; k < 9; k++)
                        for (int i = 0; i < 9; i++)
                        {
                            int R = iy * 8 + k, C = ix * 8 + i;
                            lat[R * N + C] = c.PosZ + c.Heights[k * 17 + i];
                            have[R * N + C] = true;
                        }
                }
            float min = float.MaxValue, max = float.MinValue; int n = 0;
            for (int r = 0; r < Samples; r++)
                for (int c = 0; c < Samples; c++)
                {
                    int i = (r * 2) * N + c * 2;
                    if (!have[i]) continue;
                    n++; if (lat[i] < min) min = lat[i]; if (lat[i] > max) max = lat[i];
                }
            if (n == 0) { min = 0; max = 0; }
            t.MinZ = min; t.MaxZ = max;
            float range = max - min;
            for (int r = 0; r < Samples; r++)
                for (int c = 0; c < Samples; c++)
                {
                    int i = (r * 2) * N + c * 2;
                    float z = have[i] ? lat[i] : min;
                    t.H[r * Samples + c] = range <= 0 ? (ushort)0 : (ushort)Math.Max(0, Math.Min(65535, Math.Round((z - min) / range * 65535)));
                }
            return t;
        }
    }

    public sealed class TerrainResult
    {
        public int Tiles, Written, Reused, Missing, Failed, Files, Zones;
        public int WaterFiles, WaterWritten, WaterLayers, WaterCells;      // liquid files (.wmw), those made this time, layers and cells in all of them
        public long Bytes, WaterBytes, Ms;
        public float MinZ, MaxZ;
        public bool Complete;
    }

    public static class WorldMapTerrain
    {
        public const int Version = 2;          // 2: the liquids (.wmw) next to the height grids
        public const string Folder = "terrain";
        public static string Dir(string mapDir) => Path.Combine(mapDir, Folder);
        public static string TileFile(string terrainDir, int col, int row) => Path.Combine(terrainDir, col + "_" + row + ".wmt");
        public static string WaterFile(string terrainDir, int col, int row) => Path.Combine(terrainDir, col + "_" + row + ".wmw");
        public static string ManifestPath(string terrainDir) => Path.Combine(terrainDir, "manifest.json");
        public static string BordersPath(string terrainDir) => Path.Combine(terrainDir, "borders.json");

        public sealed class Manifest
        {
            public int Version, MapId, Tiles, Missing, Files, Zones, WaterFiles, WaterLayers, WaterCells;
            public string BuildKey = "", Built = "";
            public long Bytes, WaterBytes, Ms;
            public float MinZ, MaxZ;
            public bool Valid => Version == WorldMapTerrain.Version && Tiles > 0 && Files > 0;
        }

        public static Manifest ReadManifest(string terrainDir)
        {
            try
            {
                var p = ManifestPath(terrainDir);
                if (!File.Exists(p)) return null;
                var d = Json.Obj(File.ReadAllText(p));
                if (d == null) return null;
                return new Manifest
                {
                    Version = d.Int("v"), MapId = d.Int("mapId"), BuildKey = d.Str("build") ?? "", Tiles = d.Int("tiles"), Missing = d.Int("missing"), Files = d.Int("files"), Zones = d.Int("zones"),
                    Bytes = d.Long("bytes"), Ms = d.Long("ms"), WaterFiles = d.Int("waterFiles"), WaterLayers = d.Int("waterLayers"), WaterCells = d.Int("waterCells"), WaterBytes = d.Long("waterBytes"), Built = d.Str("built") ?? "",
                    MinZ = (float)Num(d, "minZ"), MaxZ = (float)Num(d, "maxZ"),
                };
            }
            catch (Exception) { return null; }
        }

        static double Num(Dictionary<string, object> d, string k) => d.TryGetValue(k, out var o) && o is double x ? x : 0;

        static bool Have(string path) { try { var f = new FileInfo(path); return f.Exists && f.Length == TerrainTile.FileBytes; } catch (Exception) { return false; } }

        public static TerrainTile ReadTile(string terrainDir, int col, int row)
        {
            try
            {
                var p = TileFile(terrainDir, col, row);
                if (!File.Exists(p)) return null;
                var t = TerrainTile.FromBytes(File.ReadAllBytes(p));
                return t != null && t.Col == col && t.Row == row ? t : null;
            }
            catch (Exception) { return null; }
        }

        public static WaterTile ReadWater(string terrainDir, int col, int row)
        {
            try
            {
                var p = WaterFile(terrainDir, col, row);
                if (!File.Exists(p)) return null;
                var t = WaterTile.FromBytes(File.ReadAllBytes(p));
                return t != null && t.Col == col && t.Row == row ? t : null;
            }
            catch (Exception) { return null; }
        }

        static void WriteAtomic(string path, byte[] data)
        {
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, data);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        // ---- build
        // `load` gives the bytes of the root ADT of a tile (null = the source does not have it). Throws OperationCanceledException when `ct` is cancelled.
        // `zones` (may be null) turns the area ids into zones for borders.json. A cancelled or crashed build leaves its finished tile files; the next
        // one skips every tile whose file is complete (the right size), so it resumes. Files are written as .tmp and renamed; the manifest comes last.
        public static TerrainResult Build(int mapId, string buildKey, IList<WdtTile> tiles, Func<WdtTile, byte[]> load, string mapDir, ZoneResolver zones,
            Action<PyramidProgress> progress, CancellationToken ct, LiquidKinds kinds = null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var res = new TerrainResult { Tiles = tiles.Count };
            var dir = Dir(mapDir);
            Directory.CreateDirectory(dir);
            try { if (File.Exists(ManifestPath(dir))) File.Delete(ManifestPath(dir)); } catch (Exception) { }
            int total = tiles.Count + 1, done = 0;
            void Report(string phase) { progress?.Invoke(new PyramidProgress { Phase = phase, Done = done, Total = total }); }
            Report("terrain");
            float minZ = float.MaxValue, maxZ = float.MinValue;
            foreach (var t in tiles.OrderBy(t => t.Row).ThenBy(t => t.Col))
            {
                ct.ThrowIfCancellationRequested();
                var file = TileFile(dir, t.Col, t.Row);
                var wfile = WaterFile(dir, t.Col, t.Row);
                TerrainTile tt = null; WaterTile wt = null;
                if (Have(file)) tt = ReadTile(dir, t.Col, t.Row);
                if (File.Exists(wfile)) wt = ReadWater(dir, t.Col, t.Row);
                if (tt != null && wt != null) res.Reused++;
                else
                {
                    byte[] bytes = null; bool failedHere = false;
                    try { bytes = t.RootFdid == 0 ? null : load(t); } catch (OperationCanceledException) { throw; } catch (Exception e) { Util.Log("world map terrain: tile " + t.Col + "," + t.Row + ": " + e.Message); res.Failed++; failedHere = true; }
                    if (bytes == null) { if (!failedHere) res.Missing++; }
                    else
                    {
                        var adt = AdtFile.Parse(bytes);
                        if (!adt.Ok || adt.WithHeights == 0) { res.Failed++; tt = null; }
                        else
                        {
                            if (tt == null) { tt = TerrainTile.FromAdt(t.Col, t.Row, adt); WriteAtomic(file, tt.ToBytes()); res.Written++; }
                            if (wt == null) { wt = WaterTile.FromAdt(t.Col, t.Row, adt, kinds); WriteAtomic(wfile, wt.ToBytes()); res.WaterWritten++; }
                        }
                    }
                }
                if (tt != null) { if (tt.MinZ < minZ) minZ = tt.MinZ; if (tt.MaxZ > maxZ) maxZ = tt.MaxZ; }
                done++;
                if ((done & 7) == 0 || done == tiles.Count) Report("terrain");
            }
            res.Files = Directory.GetFiles(dir, "*.wmt").Length;
            foreach (var wf in Directory.GetFiles(dir, "*.wmw"))
            {
                res.WaterFiles++; try { res.WaterBytes += new FileInfo(wf).Length; } catch (Exception) { }
                var p = Path.GetFileNameWithoutExtension(wf).Split('_');
                if (p.Length == 2 && int.TryParse(p[0], out var wc) && int.TryParse(p[1], out var wr)) { var w2 = ReadWater(dir, wc, wr); if (w2 != null) { res.WaterLayers += w2.Layers.Count; res.WaterCells += w2.Cells; } }
            }
            if (res.Files == 0) throw new InvalidDataException("none of the " + tiles.Count + " terrain files could be read");
            ct.ThrowIfCancellationRequested();
            Report("borders");
            if (zones != null) res.Zones = WorldMapZones.WriteBorders(dir, mapId, tiles, zones);
            done++; Report("borders");
            long bytes2 = 0; foreach (var f in Directory.GetFiles(dir)) { try { bytes2 += new FileInfo(f).Length; } catch (Exception) { } }
            res.Bytes = bytes2; res.MinZ = minZ == float.MaxValue ? 0 : minZ; res.MaxZ = maxZ == float.MinValue ? 0 : maxZ; res.Ms = sw.ElapsedMilliseconds;
            var man = new StringBuilder();
            man.Append(Json.Write(new Dictionary<string, object>
            {
                ["v"] = Version, ["mapId"] = mapId, ["build"] = buildKey, ["tiles"] = res.Tiles - res.Missing - res.Failed, ["missing"] = res.Missing + res.Failed, ["files"] = res.Files, ["zones"] = res.Zones, ["bytes"] = res.Bytes, ["waterFiles"] = res.WaterFiles, ["waterLayers"] = res.WaterLayers, ["waterCells"] = res.WaterCells, ["waterBytes"] = res.WaterBytes,
                ["ms"] = res.Ms, ["minZ"] = Math.Round(res.MinZ, 2), ["maxZ"] = Math.Round(res.MaxZ, 2), ["samples"] = TerrainTile.Samples, ["built"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            }));
            WriteAtomic(ManifestPath(dir), Encoding.UTF8.GetBytes(man.ToString()));
            res.Complete = true;
            return res;
        }
    }
}
