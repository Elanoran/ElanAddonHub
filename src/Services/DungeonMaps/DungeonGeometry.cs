using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ElansAddonHub.Services.WorldMap;

namespace ElansAddonHub.Services.DungeonMaps
{
    // ============================================================================================================================
    // The ground of an instance, read from the player's own game files for a drawn plan (DungeonPlans): every surface a character can stand on, in WoW world
    // axes (x north, y west, z up), cut into units - one per building group and one per terrain chunk - that the floor split (DungeonFloors) keeps together.
    //
    // Where it comes from (Map.db2's WDT; checked on build 1.60.1.70338):
    //   a map of one building (MPHD flag 0x1, one MODF in the WDT: the Stockade, Molten Core, Blackrock Depths, Onyxia's Lair ...) -> that WMO's groups
    //   a map of tiles (the Deadmines, Scholomance, Zul'Gurub ...) -> the WMOs placed in the tiles' obj0 files (MODF, one record per unique id - a building
    //     that crosses a tile edge is listed by every tile it touches) and the terrain of the root ADTs, when the terrain is where the instance is (see below)
    // A building group (WmoFile / wowdev.wiki/WMO) gives its triangles from MOVT / MOVI with MOPY's flags per triangle; a triangle counts when a character
    // collides with it - flag 0x08 (collision), or 0x20 (rendered) without 0x04 (detail: rubble and trim the game does not collide with) - and faces up:
    // its normal (turned to the side MONR's vertex normals point to) is at most ~53 degrees from straight up (the steepest slope a character walks up).
    // Groups flagged unreachable (0x80) or antiportal (0x4000000) are left out. A surface more than DeepYards under the group's liquid (MLIQ: the bed of a lava lake or a
    // pool) is not ground either. MLIQ (wowdev.wiki/WMO#MLIQ): int xVerts, yVerts, xTiles, yTiles, float corner[3], uint16 material (30 bytes); then xVerts * yVerts
    // vertices of 8 bytes (4 bytes of flow / texture data, float height), x fastest; then one byte per tile (low 4 bits 0xF = no liquid in that tile). A tile is
    // 4.1667 yards (533.33 / 128) from the corner along the model's x and y.
    // Terrain (AdtFile): each MCNK's 8 x 8 cells as four triangles around the cell's middle vertex, holes left out, the same slope rule.
    //
    // The terrain rule (data, not names): many dungeons of tiles keep a filler landscape far above or below the building (the Deadmines' hills over the mine,
    // the flat 0-height sheet of Scarlet Monastery). The terrain is drawn only when the buildings stand ON it: at least a quarter of their walkable area is within
    // 8 yards of the terrain under it (on build 1.60.1: Zul'Gurub 19 %, the Ruins of Ahn'Qiraj 19 %, Shadowfang Keep 30 %, Razorfen Kraul 12 %; the Deadmines 3 %, Scholomance 4 %, Stratholme 4 %, Blackwing Lair 8 %), and then only
    // the terrain within TerrainMargin yards of the buildings' overall extent (Shadowfang Keep's castle stands in a whole landscape of Silverpine filler).
    // With no building at all the terrain is the instance.
    // ============================================================================================================================
    public enum PlanUnitKind { Building, Terrain }

    public sealed class PlanUnit
    {
        public int Id;
        public PlanUnitKind Kind;
        public string Name = "";                 // the WMO group's name (MOGN), "" for terrain
        public int Wmo;                          // the WMO's FileDataID (0 = terrain)
        public int Place = -1, Group = -1;       // which placement (PlanGeometry.Places) and which of its groups (WmoRoot.GroupInfos) the triangles are from; -1 = terrain / unknown
        public bool Interior;                    // a building group flagged interior (MOGP 0x2000): inside walls and under a roof
        public List<float> Tri = new List<float>();   // 9 floats per triangle: x, y, z of its three corners (world axes)
        public int TriCount => Tri.Count / 9;
        public double Area;                      // walkable area, square yards (top-down)

        public void Add(double ax, double ay, double az, double bx, double by, double bz, double cx, double cy, double cz)
        {
            Tri.Add((float)ax); Tri.Add((float)ay); Tri.Add((float)az); Tri.Add((float)bx); Tri.Add((float)by); Tri.Add((float)bz); Tri.Add((float)cx); Tri.Add((float)cy); Tri.Add((float)cz);
            Area += Math.Abs((bx - ax) * (cy - ay) - (cx - ax) * (by - ay)) / 2;
        }
    }

    public sealed class PlanGeometry
    {
        public List<PlanUnit> Units = new List<PlanUnit>();
        public List<WmoPlacement> Places = new List<WmoPlacement>();          // the buildings, as PlanUnit.Place indexes them
        public Dictionary<int, WmoRoot> Roots = new Dictionary<int, WmoRoot>(); // their roots by FileDataID (group boxes, MOHD's wmoID: the game's minimap pictures, DungeonMinimap)
        public bool GlobalWmo;                   // the map is one building
        public int Buildings, Groups, Tiles, TerrainChunks, MissingFiles;
        public bool TerrainUsed;
        public int Islands; public double IslandArea;        // walkable islands left out by DungeonFloors.Reachable (rooftops, ledges)
        public string TerrainRule = "";          // why the terrain was or was not drawn (a note for the self-test and the log)
        public string Problem;
        public bool Ok => Problem == null && Units.Count > 0;
        public double Area => Units.Sum(u => u.Area);
    }

    // the walkable triangles of one WMO group file, in the model's own axes
    public sealed class WmoWalkGroup
    {
        public uint Flags;                       // MOGP flags
        public float[] Pos = new float[0];       // 3 per vertex (model axes: x, y, z up)
        public List<int> Tris = new List<int>(); // walkable triangles, 3 vertex indices each, wound so the normal faces up
        public int Triangles, Collidable;        // all the group's triangles, those a character collides with
        public int UnderLiquid;                  // walkable-looking triangles left out because they lie deep under the group's liquid
        public string Problem;
        public bool Ok => Problem == null;
    }

    public static class DungeonGeometry
    {
        public const double MinUpNormal = 0.6;           // cos(53 degrees)
        public const uint GroupUnreachable = 0x80, GroupAntiportal = 0x4000000;
        public const double OnGroundYards = 8, OnGroundShare = 0.12, TerrainMargin = 40, DeepYards = 2.5, LiquidTile = 1600.0 / 3 / 128;

        public static bool Collidable(byte flags) => (flags & 0x08) != 0 || (flags & 0x24) == 0x20;

        // ---------------------------------------------------------------- one WMO group file
        public static WmoWalkGroup ParseGroup(byte[] d)
        {
            var g = new WmoWalkGroup();
            if (d == null || d.Length < 16) { g.Problem = "the group file is empty or cut off"; return g; }
            int mogp = -1, mogpSize = 0;
            for (int p = 0; p + 8 <= d.Length;)
            {
                int size = BitConverter.ToInt32(d, p + 4);
                if (size < 0) break;
                if (WmoRoot.Is(d, p, "MOGP")) { mogp = p + 8; mogpSize = (int)Math.Min((long)size, d.Length - (p + 8)); break; }
                p += 8 + size;
            }
            if (mogp < 0 || mogpSize < 68) { g.Problem = "this group has no MOGP chunk"; return g; }
            g.Flags = BitConverter.ToUInt32(d, mogp + 8);
            int movt = -1, movi = -1, monr = -1, mopy = -1, mliq = -1, mliqSize = 0, nVt = 0, nIdx = 0, nNor = 0, nPy = 0;
            int end = mogp + mogpSize;
            for (int p = mogp + 68; p + 8 <= end;)
            {
                int size = BitConverter.ToInt32(d, p + 4);
                if (size < 0 || (long)p + 8 + size > end) break;
                if (WmoRoot.Is(d, p, "MOVT")) { movt = p + 8; nVt = size / 12; }
                else if (WmoRoot.Is(d, p, "MOVI")) { movi = p + 8; nIdx = size / 2; }
                else if (WmoRoot.Is(d, p, "MONR")) { monr = p + 8; nNor = size / 12; }
                else if (WmoRoot.Is(d, p, "MOPY")) { mopy = p + 8; nPy = size / 2; }
                else if (WmoRoot.Is(d, p, "MLIQ")) { mliq = p + 8; mliqSize = size; }
                p += 8 + size;
            }
            if (movt < 0 || movi < 0 || nVt == 0 || nIdx < 3) { g.Problem = "this group has no vertices or triangles"; return g; }
            var liquid = Liquid(d, mliq, mliqSize);
            g.Pos = new float[nVt * 3];
            for (int i = 0; i < nVt * 3; i++) g.Pos[i] = BitConverter.ToSingle(d, movt + i * 4);
            bool normals = monr >= 0 && nNor >= nVt;
            g.Triangles = nIdx / 3;
            for (int t = 0; t < nIdx / 3; t++)
            {
                byte flags = mopy >= 0 && t < nPy ? d[mopy + t * 2] : (byte)0x20;
                if (!Collidable(flags)) continue;
                g.Collidable++;
                int a = BitConverter.ToUInt16(d, movi + t * 6), b = BitConverter.ToUInt16(d, movi + t * 6 + 2), c = BitConverter.ToUInt16(d, movi + t * 6 + 4);
                if (a >= nVt || b >= nVt || c >= nVt || a == b || b == c || a == c) continue;
                Normal(g.Pos, a, b, c, out var nx, out var ny, out var nz, out var len);
                if (len < 1e-6) continue;
                nx /= len; ny /= len; nz /= len;
                if (normals)
                {
                    // the side the artist's vertex normals point to is the front
                    double vx = 0, vy = 0, vz = 0;
                    foreach (var v in new[] { a, b, c }) { vx += BitConverter.ToSingle(d, monr + v * 12); vy += BitConverter.ToSingle(d, monr + v * 12 + 4); vz += BitConverter.ToSingle(d, monr + v * 12 + 8); }
                    if (nx * vx + ny * vy + nz * vz < 0) { nz = -nz; int s = b; b = c; c = s; }
                }
                else if (nz < 0) { nz = -nz; int s = b; b = c; c = s; }
                if (nz < MinUpNormal) continue;
                if (liquid != null && liquid.Under(g.Pos, a, b, c)) { g.UnderLiquid++; continue; }
                g.Tris.Add(a); g.Tris.Add(b); g.Tris.Add(c);
            }
            return g;
        }

        // the liquid surface of a group per tile (NaN = no liquid there)
        public sealed class LiquidGrid
        {
            public float X0, Y0; public int W, H; public float[] Top;
            public double At(double x, double y)
            {
                int tx = (int)Math.Floor((x - X0) / LiquidTile), ty = (int)Math.Floor((y - Y0) / LiquidTile);
                return tx < 0 || ty < 0 || tx >= W || ty >= H ? double.NaN : Top[ty * W + tx];
            }
            // all three corners more than DeepYards under the surface above them
            public bool Under(float[] p, int a, int b, int c)
            {
                foreach (var v in new[] { a, b, c })
                {
                    double top = At(p[v * 3], p[v * 3 + 1]);
                    if (double.IsNaN(top) || p[v * 3 + 2] > top - DeepYards) return false;
                }
                return true;
            }
        }

        public static LiquidGrid Liquid(byte[] d, int s, int size)
        {
            if (s < 0 || size < 30) return null;
            int xv = BitConverter.ToInt32(d, s), yv = BitConverter.ToInt32(d, s + 4), xt = BitConverter.ToInt32(d, s + 8), yt = BitConverter.ToInt32(d, s + 12);
            if (xv <= 0 || yv <= 0 || xt <= 0 || yt <= 0 || xv > 1024 || yv > 1024 || xt != xv - 1 || yt != yv - 1) return null;
            long need = 30L + (long)xv * yv * 8 + (long)xt * yt;
            if (need > size) return null;
            var g = new LiquidGrid { X0 = BitConverter.ToSingle(d, s + 16), Y0 = BitConverter.ToSingle(d, s + 20), W = xt, H = yt, Top = new float[xt * yt] };
            int verts = s + 30, tiles = verts + xv * yv * 8;
            bool any = false;
            for (int ty = 0; ty < yt; ty++)
                for (int tx = 0; tx < xt; tx++)
                {
                    if ((d[tiles + ty * xt + tx] & 0x0F) == 0x0F) { g.Top[ty * xt + tx] = float.NaN; continue; }
                    float h = float.MinValue;
                    foreach (var v in new[] { ty * xv + tx, ty * xv + tx + 1, (ty + 1) * xv + tx, (ty + 1) * xv + tx + 1 }) h = Math.Max(h, BitConverter.ToSingle(d, verts + v * 8 + 4));
                    g.Top[ty * xt + tx] = h; any = true;
                }
            return any ? g : null;
        }

        static void Normal(float[] p, int a, int b, int c, out double nx, out double ny, out double nz, out double len)
        {
            double ux = p[b * 3] - p[a * 3], uy = p[b * 3 + 1] - p[a * 3 + 1], uz = p[b * 3 + 2] - p[a * 3 + 2];
            double vx = p[c * 3] - p[a * 3], vy = p[c * 3 + 1] - p[a * 3 + 1], vz = p[c * 3 + 2] - p[a * 3 + 2];
            nx = uy * vz - uz * vy; ny = uz * vx - ux * vz; nz = ux * vy - uy * vx;
            len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        }

        // a point of a placed model in world axes (x north, y west, z up)
        public static void ToWorld(WmoPlacement p, double mx, double my, double mz, out double x, out double y, out double z)
        {
            WmoTransform.ToScene(p, mx, my, mz, out var sx, out var sy, out var sz);
            x = WdtFile.HalfWorld - sz; y = WdtFile.HalfWorld - sx; z = sy;
        }

        // ---------------------------------------------------------------- the whole map
        public static List<WmoPlacement> Buildings(WdtFile wdt, IBuildingSource src, out int missing)
        {
            missing = 0;
            var res = new List<WmoPlacement>();
            if (wdt.GlobalWmo != null) { res.Add(wdt.GlobalWmo); return res; }
            var seen = new HashSet<uint>();
            src.Prefetch(wdt.Tiles.Where(t => t.Obj0Fdid != 0).Select(t => t.Obj0Fdid));
            foreach (var t in wdt.Tiles.OrderBy(t => t.Row).ThenBy(t => t.Col))
            {
                if (t.Obj0Fdid == 0) continue;
                var b = src.Read(t.Obj0Fdid);
                if (b == null) { missing++; continue; }
                foreach (var p in AdtObjects.ParsePlacements(b, out _))
                    if (!p.HasNameIndex && seen.Add(p.UniqueId)) res.Add(p);
            }
            return res;
        }

        // Everything walkable of a map. progress(done, total) after each file.
        public static PlanGeometry Collect(WdtFile wdt, IBuildingSource src, CancellationToken ct, Action<int, int> progress = null)
        {
            var geo = new PlanGeometry { GlobalWmo = wdt.GlobalWmo != null };
            var places = Buildings(wdt, src, out var missing);
            geo.MissingFiles += missing;
            var roots = new Dictionary<uint, WmoRoot>();
            src.Prefetch(places.Select(p => (int)p.NameId).Distinct());
            foreach (var p in places)
                if (!roots.ContainsKey(p.NameId)) { var rb = src.Read((int)p.NameId); if (rb == null) geo.MissingFiles++; roots[p.NameId] = rb == null ? null : WmoRoot.Parse(rb); }
            var tiles = wdt.GlobalWmo != null ? new List<WdtTile>() : wdt.Tiles.Where(t => t.RootFdid != 0).ToList();
            int total = places.Sum(p => roots[p.NameId]?.GroupInfos.Count(g => g.Fdid != 0) ?? 0) + tiles.Count, done = 0;
            src.Prefetch(places.SelectMany(p => roots[p.NameId]?.GroupInfos.Where(g => g.Fdid != 0).Select(g => g.Fdid) ?? new int[0]).Distinct());
            var groupCache = new Dictionary<int, WmoWalkGroup>();
            int id = 0;
            var buildingUnits = new List<PlanUnit>();
            geo.Places = places;
            foreach (var kv in roots) if (kv.Value != null && kv.Value.Ok) geo.Roots[(int)kv.Key] = kv.Value;
            for (int pi = 0; pi < places.Count; pi++)
            {
                var p = places[pi];
                var r = roots[p.NameId];
                if (r == null || !r.Ok) continue;
                geo.Buildings++;
                for (int gx = 0; gx < r.GroupInfos.Count; gx++)
                {
                    var gi = r.GroupInfos[gx];
                    if (gi.Fdid == 0) continue;
                    ct.ThrowIfCancellationRequested();
                    done++;
                    if ((gi.Flags & (GroupUnreachable | GroupAntiportal)) != 0) { progress?.Invoke(done, total); continue; }
                    if (!groupCache.TryGetValue(gi.Fdid, out var g))
                    {
                        var gb = src.Read(gi.Fdid);
                        if (gb == null) geo.MissingFiles++;
                        groupCache[gi.Fdid] = g = gb == null ? null : ParseGroup(gb);
                    }
                    progress?.Invoke(done, total);
                    if (g == null || !g.Ok || g.Tris.Count == 0) continue;
                    if ((g.Flags & (GroupUnreachable | GroupAntiportal)) != 0) continue;
                    geo.Groups++;
                    var u = new PlanUnit { Id = id++, Kind = PlanUnitKind.Building, Name = gi.Name ?? "", Wmo = (int)p.NameId, Place = pi, Group = gx, Interior = (g.Flags & WmoRoot.GroupInterior) != 0 };
                    int nv = g.Pos.Length / 3;
                    var wx = new double[nv]; var wy = new double[nv]; var wz = new double[nv];
                    var done1 = new bool[nv];
                    for (int k = 0; k < g.Tris.Count; k++)
                    {
                        int v = g.Tris[k];
                        if (done1[v]) continue;
                        ToWorld(p, g.Pos[v * 3], g.Pos[v * 3 + 1], g.Pos[v * 3 + 2], out wx[v], out wy[v], out wz[v]);
                        done1[v] = true;
                    }
                    for (int k = 0; k < g.Tris.Count; k += 3)
                    {
                        int a = g.Tris[k], b = g.Tris[k + 1], c = g.Tris[k + 2];
                        // a tilted placement can tip a surface past the slope limit
                        double ux = wx[b] - wx[a], uy = wy[b] - wy[a], uz = wz[b] - wz[a], vx = wx[c] - wx[a], vy = wy[c] - wy[a], vz = wz[c] - wz[a];
                        double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx, len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                        if (len < 1e-6 || Math.Abs(nz) / len < MinUpNormal) continue;
                        u.Add(wx[a], wy[a], wz[a], wx[b], wy[b], wz[b], wx[c], wy[c], wz[c]);
                    }
                    if (u.TriCount > 0) buildingUnits.Add(u);
                }
            }

            // terrain
            var terrainUnits = new List<PlanUnit>();
            src.Prefetch(tiles.Select(t => t.RootFdid));
            foreach (var t in tiles)
            {
                ct.ThrowIfCancellationRequested();
                var b = src.Read(t.RootFdid);
                done++; progress?.Invoke(done, total);
                if (b == null) { geo.MissingFiles++; continue; }
                var a = AdtFile.Parse(b);
                if (!a.Ok) continue;
                geo.Tiles++;
                foreach (var c in a.Chunks)
                {
                    if (c == null || c.Heights == null) continue;
                    var u = TerrainUnit(c, id);
                    if (u.TriCount == 0) continue;
                    id++; geo.TerrainChunks++;
                    terrainUnits.Add(u);
                }
            }
            geo.TerrainUsed = TerrainRule(buildingUnits, terrainUnits, out geo.TerrainRule);
            geo.Units.AddRange(buildingUnits);
            if (geo.TerrainUsed) geo.Units.AddRange(buildingUnits.Count == 0 ? terrainUnits : Crop(terrainUnits, buildingUnits, TerrainMargin));
            if (geo.Units.Count == 0) geo.Problem = places.Count == 0 && tiles.Count == 0 ? "the map lists no buildings and no terrain" : "nothing in the map's files is a surface to stand on";
            return geo;
        }

        public const double ChunkSize = 1600.0 / 3 / 16, CellSize = ChunkSize / 8;

        // one terrain chunk: 8 x 8 cells, four triangles each around the middle vertex (MCVT: rows of 9 outer then 8 inner heights)
        public static PlanUnit TerrainUnit(AdtChunk c, int id)
        {
            var u = new PlanUnit { Id = id, Kind = PlanUnitKind.Terrain };
            for (int row = 0; row < 8; row++)
                for (int col = 0; col < 8; col++)
                {
                    if (c.IsHole(col, row)) continue;
                    double x0 = c.PosX - row * CellSize, x1 = x0 - CellSize, y0 = c.PosY - col * CellSize, y1 = y0 - CellSize;
                    double hTL = c.PosZ + c.Heights[row * 17 + col], hTR = c.PosZ + c.Heights[row * 17 + col + 1];
                    double hBL = c.PosZ + c.Heights[(row + 1) * 17 + col], hBR = c.PosZ + c.Heights[(row + 1) * 17 + col + 1];
                    double hC = c.PosZ + c.Heights[row * 17 + 9 + col];
                    double xm = x0 - CellSize / 2, ym = y0 - CellSize / 2;
                    AddUp(u, x0, y0, hTL, x0, y1, hTR, xm, ym, hC);
                    AddUp(u, x0, y1, hTR, x1, y1, hBR, xm, ym, hC);
                    AddUp(u, x1, y1, hBR, x1, y0, hBL, xm, ym, hC);
                    AddUp(u, x1, y0, hBL, x0, y0, hTL, xm, ym, hC);
                }
            return u;
        }

        static void AddUp(PlanUnit u, double ax, double ay, double az, double bx, double by, double bz, double cx, double cy, double cz)
        {
            double ux = bx - ax, uy = by - ay, uz = bz - az, vx = cx - ax, vy = cy - ay, vz = cz - az;
            double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx, len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len < 1e-9 || Math.Abs(nz) / len < MinUpNormal) return;
            u.Add(ax, ay, az, bx, by, bz, cx, cy, cz);
        }

        // ---------------------------------------------------------------- is the terrain the instance?
        public static bool TerrainRule(List<PlanUnit> buildings, List<PlanUnit> terrain, out string why)
        {
            if (terrain.Count == 0) { why = "no terrain"; return false; }
            if (buildings.Count == 0) { why = "no buildings: the terrain is the instance"; return true; }
            // the terrain's height on a 4-yard grid (highest surface per cell)
            const double cell = 4;
            var ground = new Dictionary<long, double>();
            foreach (var u in terrain)
                for (int t = 0; t < u.TriCount; t++)
                    for (int k = 0; k < 3; k++)
                    {
                        long key = Key(u.Tri[t * 9 + k * 3] / cell, u.Tri[t * 9 + k * 3 + 1] / cell);
                        double z = u.Tri[t * 9 + k * 3 + 2];
                        if (!ground.TryGetValue(key, out var have) || z > have) ground[key] = z;
                    }
            double on = 0, all = 0;
            foreach (var u in buildings)
                for (int t = 0; t < u.TriCount; t++)
                {
                    int o = t * 9;
                    double cx = (u.Tri[o] + u.Tri[o + 3] + u.Tri[o + 6]) / 3, cy = (u.Tri[o + 1] + u.Tri[o + 4] + u.Tri[o + 7]) / 3, cz = (u.Tri[o + 2] + u.Tri[o + 5] + u.Tri[o + 8]) / 3;
                    double area = Math.Abs((u.Tri[o + 3] - u.Tri[o]) * (u.Tri[o + 7] - u.Tri[o + 1]) - (u.Tri[o + 6] - u.Tri[o]) * (u.Tri[o + 4] - u.Tri[o + 1])) / 2;
                    all += area;
                    if (ground.TryGetValue(Key(cx / cell, cy / cell), out var gz) && Math.Abs(gz - cz) <= OnGroundYards) on += area;
                }
            double share = all <= 0 ? 0 : on / all;
            bool use = share >= OnGroundShare;
            why = (use ? "the buildings stand on the terrain" : "the terrain is a filler away from the buildings") + " (" + Math.Round(share * 100) + " % of their floor within " + OnGroundYards + " yards of it)";
            return use;
        }

        // the terrain triangles inside the buildings' extent (grown by margin yards)
        public static List<PlanUnit> Crop(List<PlanUnit> terrain, List<PlanUnit> buildings, double margin)
        {
            double x0 = double.MaxValue, x1 = double.MinValue, y0 = double.MaxValue, y1 = double.MinValue;
            foreach (var u in buildings) for (int i = 0; i < u.Tri.Count; i += 3) { x0 = Math.Min(x0, u.Tri[i]); x1 = Math.Max(x1, u.Tri[i]); y0 = Math.Min(y0, u.Tri[i + 1]); y1 = Math.Max(y1, u.Tri[i + 1]); }
            x0 -= margin; x1 += margin; y0 -= margin; y1 += margin;
            var res = new List<PlanUnit>();
            foreach (var u in terrain)
            {
                var c = new PlanUnit { Id = u.Id, Kind = u.Kind };
                for (int o = 0; o + 9 <= u.Tri.Count; o += 9)
                {
                    double cx = (u.Tri[o] + u.Tri[o + 3] + u.Tri[o + 6]) / 3, cy = (u.Tri[o + 1] + u.Tri[o + 4] + u.Tri[o + 7]) / 3;
                    if (cx >= x0 && cx <= x1 && cy >= y0 && cy <= y1) c.Add(u.Tri[o], u.Tri[o + 1], u.Tri[o + 2], u.Tri[o + 3], u.Tri[o + 4], u.Tri[o + 5], u.Tri[o + 6], u.Tri[o + 7], u.Tri[o + 8]);
                }
                if (c.TriCount > 0) res.Add(c);
            }
            return res;
        }

        internal static long Key(double cx, double cy) => ((long)Math.Floor(cx) + 1000000L) * 4000000L + ((long)Math.Floor(cy) + 1000000L);
    }
}
