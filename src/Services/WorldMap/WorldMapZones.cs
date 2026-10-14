using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ElansAddonHub.Services.Maps;

namespace ElansAddonHub.Services.WorldMap
{
    // How an AreaTable id turns into a zone (the top-level area: Razor Hill -> Durotar) and the zone's name.
    public sealed class ZoneResolver
    {
        public Func<int, int> ZoneOf = a => a;
        public Func<int, string> NameOf = a => "Zone " + a;
        public Func<int, int> UiMapOf = a => 0;

        public static ZoneResolver FromIndex(MapIndex idx)
        {
            var cache = new Dictionary<int, int>();
            return new ZoneResolver
            {
                ZoneOf = a =>
                {
                    if (a <= 0) return 0;
                    if (cache.TryGetValue(a, out var z)) return z;
                    if (idx.Area(a) == null) return cache[a] = 0;
                    int id = a; var seen = new HashSet<int> { a };
                    while (true) { var cur = idx.Area(id); if (cur == null || cur.ParentId == 0 || idx.Area(cur.ParentId) == null || !seen.Add(cur.ParentId)) break; id = cur.ParentId; }
                    return cache[a] = id;
                },
                NameOf = a => idx.AreaName(a) ?? ("Area " + a),
                UiMapOf = a => idx.UiMapForArea(a),
            };
        }
    }

    public sealed class BorderZone
    {
        public int Id, Chunks;
        public string Name = "";
        public List<int[]> Rings = new List<int[]>();       // flat x, y pairs on the continent's chunk lattice (0..1024)
    }

    public sealed class BorderSet
    {
        public int MapId;
        public List<BorderZone> Zones = new List<BorderZone>();
    }

    // ============================================================================================================================
    // Zone borders from the per-chunk area ids of the terrain: the 64 x 16 = 1024 x 1024 chunk grid of a continent holds a zone id per chunk; the
    // outline of each zone is traced along the chunk edges (boundary tracing: every edge between a chunk of the zone and a chunk of another zone
    // is directed with the zone on its right, edges are chained into closed rings; where two corners touch the ring turns right, so diagonal
    // neighbours stay separate), simplified (Douglas-Peucker) and saved as JSON. The data is per chunk, so a border is as exact as 33 yards; the
    // simplification adds at most `Eps` chunk units to that. Rings with the zone on the right: outer rings run clockwise on the map (x east,
    // y south), holes anticlockwise, so the even-odd fill of a polygon with all its rings is the zone.
    // ============================================================================================================================
    public static class WorldMapZones
    {
        public const int Size = 1024;                 // chunks per side of the 64 x 64 tile grid
        public const double Eps = 0.8;                // simplification tolerance in chunks (about 27 yards)
        public const int MinRingCells = 3;            // rings enclosing less than this many chunks are speckle and are left out

        // ---- tracing
        // `grid` holds a zone id per cell (0 = none). Returns the simplified rings of every zone (flat x, y pairs of lattice points).
        public static Dictionary<int, List<int[]>> Trace(int[] grid, int w, int h, double eps = Eps, int minCells = MinRingCells)
        {
            var edgesOf = new Dictionary<int, List<long>>();
            int W1 = w + 1;
            long Key(int vx, int vy, int d) => ((long)vy * W1 + vx) * 4 + d;
            void Add(int z, int vx, int vy, int d) { if (!edgesOf.TryGetValue(z, out var l)) edgesOf[z] = l = new List<long>(); l.Add(Key(vx, vy, d)); }
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int z = grid[y * w + x]; if (z == 0) continue;
                    if (y == 0 || grid[(y - 1) * w + x] != z) Add(z, x, y, 0);                 // top: east
                    if (x == w - 1 || grid[y * w + x + 1] != z) Add(z, x + 1, y, 1);           // right: south
                    if (y == h - 1 || grid[(y + 1) * w + x] != z) Add(z, x + 1, y + 1, 2);     // bottom: west
                    if (x == 0 || grid[y * w + x - 1] != z) Add(z, x, y + 1, 3);               // left: north
                }
            int[] dx = { 1, 0, -1, 0 }, dy = { 0, 1, 0, -1 };
            var res = new Dictionary<int, List<int[]>>();
            foreach (var kv in edgesOf)
            {
                var all = new HashSet<long>(kv.Value); var alive = new HashSet<long>(kv.Value);
                var rings = new List<int[]>();
                foreach (var start in kv.Value)
                {
                    if (!alive.Contains(start)) continue;
                    var pts = new List<int>();
                    long e = start; int prevDir = -1;
                    for (int guard = 0; guard < 8 * 1000 * 1000; guard++)
                    {
                        alive.Remove(e);
                        int d = (int)(e % 4); long v = e / 4; int vx = (int)(v % W1), vy = (int)(v / W1);
                        if (d != prevDir) { pts.Add(vx); pts.Add(vy); }
                        prevDir = d;
                        int nx = vx + dx[d], ny = vy + dy[d];
                        long next = -1;
                        foreach (var nd in new[] { (d + 1) & 3, d, (d + 3) & 3 })      // right, straight, left
                        {
                            var k = Key(nx, ny, nd);
                            if (all.Contains(k)) { next = k; break; }
                        }
                        if (next < 0 || next == start) break;
                        e = next;
                    }
                    if (pts.Count < 8) continue;
                    // the heading of the closing edge may equal the first one: then the first point is on a straight run, drop it
                    if (Math.Abs(Area2(pts)) / 2 < minCells) continue;
                    var simp = Simplify(pts, eps);
                    if (simp.Length >= 6) rings.Add(simp);
                }
                if (rings.Count > 0) res[kv.Key] = rings;
            }
            return res;
        }

        // twice the signed area of a flat x, y ring (shoelace)
        public static double Area2(IList<int> p)
        {
            double a = 0; int n = p.Count / 2;
            for (int i = 0; i < n; i++) { int j = (i + 1) % n; a += (double)p[2 * i] * p[2 * j + 1] - (double)p[2 * j] * p[2 * i + 1]; }
            return a;
        }

        // Douglas-Peucker on a closed ring: split at the first point and the one farthest from it
        public static int[] Simplify(IList<int> p, double eps)
        {
            int n = p.Count / 2;
            if (n <= 4 || eps <= 0) return p.ToArray();
            int far = 0; long best = -1;
            for (int i = 1; i < n; i++) { long ddx = p[2 * i] - p[0], ddy = p[2 * i + 1] - p[1]; long d = ddx * ddx + ddy * ddy; if (d > best) { best = d; far = i; } }
            var keep = new bool[n];
            keep[0] = keep[far] = true;
            Dp(p, n, 0, far, eps, keep);
            Dp(p, n, far, n, eps, keep);        // from `far` around to point n == point 0
            var o = new List<int>();
            for (int i = 0; i < n; i++) if (keep[i]) { o.Add(p[2 * i]); o.Add(p[2 * i + 1]); }
            return o.ToArray();
        }

        static void Dp(IList<int> p, int n, int a, int b, double eps, bool[] keep)
        {
            var stack = new Stack<(int, int)>(); stack.Push((a, b));
            while (stack.Count > 0)
            {
                var (s, e) = stack.Pop();
                if (e - s < 2) continue;
                int i0 = s % n, i1 = e % n;
                double x0 = p[2 * i0], y0 = p[2 * i0 + 1], x1 = p[2 * i1], y1 = p[2 * i1 + 1];
                double vx = x1 - x0, vy = y1 - y0, len = Math.Sqrt(vx * vx + vy * vy);
                int at = -1; double dmax = eps;
                for (int i = s + 1; i < e; i++)
                {
                    int ii = i % n; double px = p[2 * ii], py = p[2 * ii + 1];
                    double d = len < 1e-9 ? Math.Sqrt((px - x0) * (px - x0) + (py - y0) * (py - y0)) : Math.Abs(vx * (py - y0) - vy * (px - x0)) / len;
                    if (d > dmax) { dmax = d; at = i; }
                }
                if (at < 0) continue;
                keep[at % n] = true;
                stack.Push((s, at)); stack.Push((at, e));
            }
        }

        // even-odd point test against all rings of a zone (chunk lattice coordinates)
        public static bool Contains(IList<int[]> rings, double x, double y)
        {
            bool inside = false;
            foreach (var r in rings)
            {
                int n = r.Length / 2;
                for (int i = 0, j = n - 1; i < n; j = i++)
                {
                    double xi = r[2 * i], yi = r[2 * i + 1], xj = r[2 * j], yj = r[2 * j + 1];
                    if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi) inside = !inside;
                }
            }
            return inside;
        }

        // ---- the zone grid of a continent from its terrain files
        public static int[] ZoneGrid(string terrainDir, IEnumerable<WdtTile> tiles, ZoneResolver zr, out Dictionary<int, int> chunks)
        {
            var grid = new int[Size * Size];
            chunks = new Dictionary<int, int>();
            var cache = new Dictionary<int, int>();
            foreach (var t in tiles)
            {
                var tt = WorldMapTerrain.ReadTile(terrainDir, t.Col, t.Row);
                if (tt == null) continue;
                for (int cy = 0; cy < 16; cy++)
                    for (int cx = 0; cx < 16; cx++)
                    {
                        int a = tt.Area[cy * 16 + cx];
                        if (!cache.TryGetValue(a, out var z)) cache[a] = z = a <= 0 ? 0 : zr.ZoneOf(a);
                        grid[(t.Row * 16 + cy) * Size + t.Col * 16 + cx] = z;
                        if (z != 0) { chunks.TryGetValue(z, out var n); chunks[z] = n + 1; }
                    }
            }
            return grid;
        }

        public static int WriteBorders(string terrainDir, int mapId, IEnumerable<WdtTile> tiles, ZoneResolver zr)
        {
            var grid = ZoneGrid(terrainDir, tiles, zr, out var chunks);
            var rings = Trace(grid, Size, Size);
            var sb = new StringBuilder();
            sb.Append("{\"v\":1,\"mapId\":").Append(mapId).Append(",\"size\":").Append(Size).Append(",\"zones\":[");
            bool first = true;
            foreach (var z in rings.Keys.OrderBy(k => k))
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"id\":").Append(z).Append(",\"name\":").Append(Json.Write(zr.NameOf(z) ?? "")).Append(",\"chunks\":").Append(chunks.TryGetValue(z, out var c) ? c : 0).Append(",\"rings\":[");
                for (int i = 0; i < rings[z].Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('[').Append(string.Join(",", rings[z][i])).Append(']');
                }
                sb.Append("]}");
            }
            sb.Append("]}");
            var path = WorldMapTerrain.BordersPath(terrainDir);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
            return rings.Count;
        }

        public static BorderSet ReadBorders(string terrainDir)
        {
            try
            {
                var path = WorldMapTerrain.BordersPath(terrainDir);
                if (!File.Exists(path)) return null;
                var d = Json.Obj(File.ReadAllText(path));
                if (d == null || d.Int("v") != 1) return null;
                var set = new BorderSet { MapId = d.Int("mapId") };
                foreach (var zo in d.List("zones") ?? new List<object>())
                {
                    var z = zo as Dictionary<string, object>; if (z == null) continue;
                    var bz = new BorderZone { Id = z.Int("id"), Name = z.Str("name") ?? "", Chunks = z.Int("chunks") };
                    foreach (var ro in z.List("rings") ?? new List<object>())
                    {
                        var l = ro as List<object>; if (l == null || l.Count < 6 || l.Count % 2 != 0) continue;
                        bz.Rings.Add(l.Select(v => (int)(double)v).ToArray());
                    }
                    if (bz.Rings.Count > 0) set.Zones.Add(bz);
                }
                return set;
            }
            catch (Exception) { return null; }
        }

        // ---- which zone is this point in
        public sealed class ZoneHit { public int AreaId, ZoneId; public string ZoneName = "", AreaName = ""; public int UiMapId; }

        static readonly object cacheGate = new object();
        static readonly Dictionary<string, TerrainTile> tileCache = new Dictionary<string, TerrainTile>();

        // the area / zone of a world position, from the terrain files of the continent in `terrainDir`; null when the tile is not there
        public static ZoneHit ZoneAt(string terrainDir, double x, double y, ZoneResolver zr)
        {
            WorldMapMath.WorldToGrid(x, y, out var c, out var r);
            if (c < 0 || r < 0 || c >= WorldMapMath.Grid || r >= WorldMapMath.Grid) return null;
            int col = (int)Math.Floor(c), row = (int)Math.Floor(r);
            var key = terrainDir + "|" + col + "_" + row;
            TerrainTile tt;
            lock (cacheGate)
            {
                if (!tileCache.TryGetValue(key, out tt))
                {
                    tt = WorldMapTerrain.ReadTile(terrainDir, col, row);
                    if (tileCache.Count > 64) tileCache.Clear();
                    tileCache[key] = tt;
                }
            }
            if (tt == null) return null;
            int cx = Math.Max(0, Math.Min(15, (int)((c - col) * 16))), cy = Math.Max(0, Math.Min(15, (int)((r - row) * 16)));
            int a = tt.Area[cy * 16 + cx];
            if (a <= 0) return null;
            int z = zr.ZoneOf(a);
            return new ZoneHit { AreaId = a, ZoneId = z, ZoneName = zr.NameOf(z) ?? "", AreaName = zr.NameOf(a) ?? "", UiMapId = zr.UiMapOf(a) };
        }

        public static void ClearCache() { lock (cacheGate) tileCache.Clear(); }
    }
}
