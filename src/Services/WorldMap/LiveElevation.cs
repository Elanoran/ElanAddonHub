using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ElansAddonHub.Services.Maps;

namespace ElansAddonHub.Services.WorldMap
{
    // ============================================================================================================================
    // How high you are, for the live dot and trail in the 3D view (2.82). The game gives an addon only x, y on the zone map (UnitPosition's z is always 0 here),
    // so the height is worked out from what the Outpost already has and three yes / no answers the Hub sends (strip row 1 version 5: on a flight, indoors, swimming):
    //   on a flight   the flight route's own 3D points (TaxiPathNode.db2, the same table the world map's Flight paths layer reads): the route that fits the points since
    //                 take-off best (it starts at the flight master nearest to where you took off, and multi-stop flights continue on the routes from where the last
    //                 one ended), and the height interpolated along it at the point nearest to you                                                   -> Air
    //   swimming      the liquid surface of the terrain (MH2O, the .wmw files the 3D view draws), when it is above the ground                      -> Water
    //   a building    walkable surfaces of the 3D view's buildings (the outside shells of the WMOs, <map>\buildings) under you: the one closest to where you were
    //                 a moment ago (you cannot climb more than a step or a slope in one stride); indoors from nowhere (after a teleport) the lowest floor above the
    //                 ground. Outdoors the ground wins unless continuity says you walked onto a bridge or deck (the floor is nearer than the ground)    -> Floor
    //   otherwise     the terrain (what the page drew before), and the page drapes those parts of the trail over its own ground                     -> Terrain
    // Floors, water and the flight's fallback are smoothed a little over time; a teleport (a big jump, another continent, a long pause) snaps.
    // Memory only, like the trail: nothing here is saved.
    // Coordinates are WoW world ones: x north, y west, z up, yards (the same as TaxiPathNode and LiveTrail.Point).
    // ============================================================================================================================
    public enum ElevSrc : byte { Terrain = 0, Floor = 1, Water = 2, Air = 3 }

    public static class LiveMove
    {
        public const int Taxi = 4, Indoors = 8, Swimming = 16;      // the bits of the strip's posFlags (row 1 version 5)
    }

    public struct ElevResult
    {
        public double Z;
        public ElevSrc Src;
        public bool Snapped;          // a teleport / first point / source change: no smoothing
        public int RouteId;           // on a flight: the TaxiPath leg it was matched to (0 = none fitted)
        public string Why;            // a short reason (tests, the log)
    }

    // what the solver can ask about a continent (null / NaN = not known)
    public interface IElevationWorld
    {
        double Ground(int mapId, double x, double y);               // terrain height; NaN = no terrain known there
        double Water(int mapId, double x, double y);                // liquid surface; NaN = none
        void Floors(int mapId, double x, double y, List<double> into);   // heights of walkable building surfaces right under / over the point
        IReadOnlyList<TaxiRoute> Routes(int mapId);                // the flight routes on this continent
    }

    // one TaxiPath with its points in order (world x, y, z) and the 2D length along it
    public sealed class TaxiRoute
    {
        public int Id, From, To, MapId;
        public double[] X, Y, Z, S;
        public double Length => S.Length > 0 ? S[S.Length - 1] : 0;

        public static TaxiRoute Make(int id, int from, int to, int mapId, IList<double[]> pts)
        {
            var r = new TaxiRoute { Id = id, From = from, To = to, MapId = mapId, X = new double[pts.Count], Y = new double[pts.Count], Z = new double[pts.Count], S = new double[pts.Count] };
            for (int i = 0; i < pts.Count; i++)
            {
                r.X[i] = pts[i][0]; r.Y[i] = pts[i][1]; r.Z[i] = pts[i][2];
                r.S[i] = i == 0 ? 0 : r.S[i - 1] + LiveTrail.Dist(r.X[i - 1], r.Y[i - 1], r.X[i], r.Y[i]);
            }
            return r;
        }

        // the nearest point of the route to (x, y): 2D distance, the length along the route there and the interpolated height
        public double Project(double x, double y, out double s, out double z)
        {
            s = 0; z = Z.Length > 0 ? Z[0] : 0;
            if (X.Length == 0) return double.MaxValue;
            if (X.Length == 1) return LiveTrail.Dist(X[0], Y[0], x, y);
            double best = double.MaxValue;
            for (int i = 0; i + 1 < X.Length; i++)
            {
                double ax = X[i], ay = Y[i], dx = X[i + 1] - ax, dy = Y[i + 1] - ay, l2 = dx * dx + dy * dy;
                double t = l2 <= 1e-9 ? 0 : Math.Max(0, Math.Min(1, ((x - ax) * dx + (y - ay) * dy) / l2));
                double px = ax + dx * t, py = ay + dy * t, d = LiveTrail.Dist(px, py, x, y);
                if (d < best) { best = d; s = S[i] + (S[i + 1] - S[i]) * t; z = Z[i] + (Z[i + 1] - Z[i]) * t; }
            }
            return best;
        }

        // every route of a map set, by continent (points sorted by their index; a route with fewer than two points is left out)
        public static Dictionary<int, List<TaxiRoute>> FromSet(MapSet set)
        {
            var res = new Dictionary<int, List<TaxiRoute>>();
            if (set == null || !set.HasTaxi) return res;
            var byPath = set.TaxiPoints.GroupBy(p => p.PathId).ToDictionary(g => g.Key, g => g.OrderBy(p => p.Index).ToList());
            foreach (var p in set.TaxiPaths)
            {
                if (!byPath.TryGetValue(p.Id, out var pts) || pts.Count < 2) continue;
                // a route that changes continent on the way keeps only the part on its first continent
                int map = pts[0].MapId;
                var list = pts.TakeWhile(q => q.MapId == map).Select(q => new[] { q.X, q.Y, q.Z }).ToList();
                if (list.Count < 2) continue;
                if (!res.TryGetValue(map, out var l)) res[map] = l = new List<TaxiRoute>();
                l.Add(Make(p.Id, p.FromNode, p.ToNode, map, list));
            }
            return res;
        }
    }

    public sealed class LiveElevation
    {
        // continuity: one stride may climb a step plus a slope; a teleport is a long jump at an impossible speed, another continent or a long pause
        public const double StepUp = 1.6, ClimbPerYard = 0.9, FallMax = 60, TeleportYards = 250, TeleportSpeed = 70, BreakSeconds = 30;
        public const double SmoothSeconds = 0.8, SnapZ = 12;
        // flights: where the take-off is looked for, how far a point may lie from a fitting route, how many candidates are kept, the lift without a route
        public const double TaxiStartYards = 150, TaxiFitYards = 70, TaxiNextYards = 150, NoRouteLift = 25;
        public const int MaxChains = 40, FlightPoints = 60;

        readonly IElevationWorld world;
        public LiveElevation(IElevationWorld w) { world = w; }

        bool has, pTaxi; int pMap; double pX, pY, pZ, pRaw; ElevSrc pSrc; DateTime pAt;
        // the flight under way: candidate chains of routes, the points flown so far
        sealed class Chain { public List<TaxiRoute> Legs = new List<TaxiRoute>(); public double Score; public string Key; }
        List<Chain> chains; readonly List<double[]> fpts = new List<double[]>(); int fMap;
        public int ChainsForTest => chains?.Count ?? 0;
        readonly List<double> floorBuf = new List<double>();

        public void Reset() { has = false; pTaxi = false; chains = null; fpts.Clear(); }

        public ElevResult Solve(int mapId, double x, double y, int move, DateTime at)
        {
            bool taxi = (move & LiveMove.Taxi) != 0, indoors = (move & LiveMove.Indoors) != 0, swim = (move & LiveMove.Swimming) != 0;
            double dt = has ? (at - pAt).TotalSeconds : 0, d = has ? LiveTrail.Dist(pX, pY, x, y) : 0;
            bool jump = !has || mapId != pMap || dt > BreakSeconds || dt < -1 || (d > TeleportYards && d / Math.Max(dt, 0.5) > TeleportSpeed);
            double g = SafeGround(mapId, x, y); bool gOk = !double.IsNaN(g); if (!gOk) g = 0;   // no terrain: the sea (the page draws 0 there too)
            double? refZ = jump ? (double?)null : pRaw;
            var r = new ElevResult();
            if (taxi) r = Taxi(mapId, x, y, g, jump);
            else
            {
                chains = null; fpts.Clear();
                double w = swim ? Safe(() => world.Water(mapId, x, y)) : double.NaN;
                if (swim && !double.IsNaN(w) && w >= g - 0.5) { r.Z = w; r.Src = ElevSrc.Water; r.Why = "water surface"; }
                else Ground(mapId, x, y, g, gOk, indoors, refZ, d, ref r);
            }
            double raw = r.Z;
            // a little smoothing for floors and water (a choice between two close floors, a sloped deck); terrain is exact (the page drapes it), the flight follows its route
            bool snap = jump || r.Src != pSrc || Math.Abs(raw - pZ) > SnapZ || r.Src == ElevSrc.Terrain || r.Src == ElevSrc.Air;
            if (!snap) { double a = 1 - Math.Exp(-Math.Max(0, dt) / SmoothSeconds); r.Z = pZ + (raw - pZ) * a; }
            r.Snapped = snap;
            has = true; pMap = mapId; pX = x; pY = y; pAt = at; pZ = r.Z; pRaw = raw; pSrc = r.Src; pTaxi = taxi;
            return r;
        }

        double Safe(Func<double> f) { try { return f(); } catch (Exception) { return double.NaN; } }
        double SafeGround(int m, double x, double y) => Safe(() => world.Ground(m, x, y));

        // ---- on foot: the terrain, or a building's floor by continuity
        void Ground(int mapId, double x, double y, double g, bool gOk, bool indoors, double? refZ, double d, ref ElevResult r)
        {
            floorBuf.Clear();
            try { world.Floors(mapId, x, y, floorBuf); } catch (Exception) { floorBuf.Clear(); }
            var floors = floorBuf.Where(f => !double.IsNaN(f) && !double.IsInfinity(f)).ToList();
            r.Z = g; r.Src = ElevSrc.Terrain; r.Why = "terrain";
            if (floors.Count == 0) return;
            if (refZ.HasValue)
            {
                double rz = refZ.Value, up = StepUp + ClimbPerYard * d;
                bool Ok(double c) => c - rz <= up && rz - c <= FallMax;
                double best = double.NaN, bd = double.MaxValue;
                foreach (var f in floors) if (Ok(f) && Math.Abs(f - rz) < bd) { bd = Math.Abs(f - rz); best = f; }
                double gd = gOk && Ok(g) ? Math.Abs(g - rz) : double.MaxValue;
                if (!double.IsNaN(best))
                {
                    // outdoors the ground wins a tie (under a bridge you stay on the ground); indoors a floor in reach wins unless the ground is clearly nearer
                    // (a floor a little above the ground you walked in on is the floor you stand on; a roof or an upper floor is out of reach)
                    if (indoors ? bd <= gd + StepUp : bd + 0.25 < gd) { r.Z = best; r.Src = ElevSrc.Floor; r.Why = indoors ? "floor by continuity" : "deck by continuity"; }
                    else r.Why = indoors ? "terrain by continuity" : "terrain (outdoors)";
                    return;
                }
                if (gd < double.MaxValue) { r.Why = "terrain (no floor in reach)"; return; }
                // nothing within reach (a fall, an elevator): as if entering
            }
            if (!indoors) { r.Why = "terrain (outdoors)"; return; }
            // entering (or after a teleport): the lowest floor above the ground, else the highest one below it (a cellar, a city under the ground)
            var above = floors.Where(f => f >= g - 2).ToList();
            double pick = above.Count > 0 ? above.Min() : floors.Max();
            r.Z = pick; r.Src = ElevSrc.Floor; r.Why = above.Count > 0 ? "lowest floor above the ground" : "highest floor below the ground";
        }

        // ---- on a flight
        ElevResult Taxi(int mapId, double x, double y, double g, bool jump)
        {
            var r = new ElevResult { Src = ElevSrc.Air };
            IReadOnlyList<TaxiRoute> routes = null;
            try { routes = world.Routes(mapId); } catch (Exception) { }
            routes = routes ?? new List<TaxiRoute>();
            if (chains == null || !pTaxi || jump || fMap != mapId)
            {
                // take-off: where you stood just before (when that was a moment ago), else here (the Outpost started during the flight)
                bool fromPrev = has && !jump && pMap == mapId && !pTaxi;
                double sx = fromPrev ? pX : x, sy = fromPrev ? pY : y;
                fpts.Clear(); fMap = mapId;
                if (fromPrev) fpts.Add(new[] { sx, sy });
                var start = routes.Where(t => LiveTrail.Dist(t.X[0], t.Y[0], sx, sy) <= TaxiStartYards).ToList();
                if (start.Count == 0) start = routes.Where(t => t.Project(x, y, out _, out _) <= TaxiFitYards * 1.5).ToList();
                chains = start.Select(t => new Chain { Legs = { t }, Key = t.Id.ToString() }).ToList();
            }
            fpts.Add(new[] { x, y });
            while (fpts.Count > FlightPoints) fpts.RemoveAt(0);
            // a chain whose last route is nearly flown continues on the routes that start where it ends (not straight back)
            var add = new List<Chain>();
            var keys = new HashSet<string>(chains.Select(c => c.Key));
            foreach (var c in chains)
            {
                var last = c.Legs[c.Legs.Count - 1];
                double dist = last.Project(x, y, out var s, out _);
                if (dist > TaxiFitYards * 2 || last.Length - s > TaxiNextYards) continue;
                foreach (var n in routes)
                {
                    if (n.From != last.To || n.To == last.From || c.Legs.Contains(n)) continue;
                    var k = c.Key + ">" + n.Id; if (keys.Contains(k)) continue;
                    var nc = new Chain { Key = k }; nc.Legs.AddRange(c.Legs); nc.Legs.Add(n); add.Add(nc); keys.Add(k);
                }
            }
            chains.AddRange(add);
            foreach (var c in chains)
            {
                double sum = 0;
                foreach (var p in fpts) { double dm = c.Legs.Min(l => l.Project(p[0], p[1], out _, out _)); sum += dm * dm; }
                c.Score = Math.Sqrt(sum / Math.Max(1, fpts.Count));
            }
            chains = chains.OrderBy(c => c.Score).ThenBy(c => c.Legs.Count).Take(MaxChains).ToList();
            var best = chains.FirstOrDefault();
            if (best != null && best.Score <= TaxiFitYards)
            {
                double bd = double.MaxValue, bz = g; int leg = 0;
                foreach (var l in best.Legs) { double dd = l.Project(x, y, out _, out var z); if (dd < bd) { bd = dd; bz = z; leg = l.Id; } }
                if (bd <= TaxiFitYards * 1.5)
                {
                    r.Z = Math.Max(bz, g); r.RouteId = leg; r.Why = "flight route " + string.Join(">", best.Legs.Select(l => l.Id));
                    return r;
                }
            }
            // no route fits (an unknown flight, no flight tables): above the ground, a little lifted, like the flat map's flight paths layer
            r.Z = g + NoRouteLift; r.Why = "flight, no route fits";
            return r;
        }
    }

    // ============================================================================================================================
    // Walkable surfaces of the 3D view's buildings (the outside shells of the WMOs in <map>\buildings, as the page draws them). A placement's triangles are put into
    // the scene once (its rotation YXZ, scale and position, like the page's InstancedMesh) and kept for a while; only triangles that face up or down by at least
    // 53 degrees count (a floor, a deck, a roof, a ramp - not a wall). Scene axes: x east, y up, z south.
    // ============================================================================================================================
    public sealed class BuildingFloors
    {
        public const double MinNormalY = 0.6;
        const double Cell = 64;
        readonly IList<WmoPlacement> pl;
        readonly Func<int, WmoModel> model;
        readonly Dictionary<long, List<int>> grid = new Dictionary<long, List<int>>();
        readonly Dictionary<int, float[]> tris = new Dictionary<int, float[]>();      // placement -> walkable triangles (9 floats each)
        readonly LinkedList<int> lru = new LinkedList<int>();
        readonly Dictionary<int, WmoModel> models = new Dictionary<int, WmoModel>();
        public int MaxCached = 64;
        public int Placements => pl.Count;

        public BuildingFloors(IList<WmoPlacement> placements, Func<int, WmoModel> modelOf)
        {
            pl = placements ?? new List<WmoPlacement>(); model = modelOf;
            for (int i = 0; i < pl.Count; i++)
            {
                var p = pl[i];
                int c0 = (int)Math.Floor(p.MinX / Cell), c1 = (int)Math.Floor(p.MaxX / Cell), r0 = (int)Math.Floor(p.MinZ / Cell), r1 = (int)Math.Floor(p.MaxZ / Cell);
                if (c1 - c0 > 40 || r1 - r0 > 40) continue;     // a broken box
                for (int c = c0; c <= c1; c++) for (int r = r0; r <= r1; r++)
                {
                    long k = ((long)c << 32) ^ (uint)r;
                    if (!grid.TryGetValue(k, out var l)) grid[k] = l = new List<int>();
                    l.Add(i);
                }
            }
        }

        public static BuildingFloors FromDir(string buildingsDir)
        {
            try
            {
                var f = WorldMapBuildings.PlacementsPath(buildingsDir);
                if (!File.Exists(f)) return null;
                var ps = WorldMapBuildings.PlacementsFromBytes(File.ReadAllBytes(f), out _);
                if (ps == null) return null;
                return new BuildingFloors(ps, fdid =>
                {
                    var mf = WorldMapBuildings.ModelFile(buildingsDir, fdid);
                    return File.Exists(mf) ? WmoModel.FromBytes(File.ReadAllBytes(mf)) : null;
                });
            }
            catch (Exception) { return null; }
        }

        // the heights (scene y) of the walkable surfaces at scene x, z
        public void At(double x, double z, List<double> into)
        {
            long k = ((long)(int)Math.Floor(x / Cell) << 32) ^ (uint)(int)Math.Floor(z / Cell);
            if (!grid.TryGetValue(k, out var cand)) return;
            foreach (var i in cand)
            {
                var p = pl[i];
                if (x < p.MinX || x > p.MaxX || z < p.MinZ || z > p.MaxZ) continue;
                var t = Tris(i); if (t == null) continue;
                for (int o = 0; o + 8 < t.Length; o += 9)
                {
                    double ax = t[o], ay = t[o + 1], az = t[o + 2], bx = t[o + 3], by = t[o + 4], bz = t[o + 5], cx = t[o + 6], cy = t[o + 7], cz = t[o + 8];
                    if (x < Math.Min(ax, Math.Min(bx, cx)) || x > Math.Max(ax, Math.Max(bx, cx)) || z < Math.Min(az, Math.Min(bz, cz)) || z > Math.Max(az, Math.Max(bz, cz))) continue;
                    double det = (bz - cz) * (ax - cx) + (cx - bx) * (az - cz); if (Math.Abs(det) < 1e-9) continue;
                    double l1 = ((bz - cz) * (x - cx) + (cx - bx) * (z - cz)) / det, l2 = ((cz - az) * (x - cx) + (ax - cx) * (z - cz)) / det, l3 = 1 - l1 - l2;
                    if (l1 < -1e-6 || l2 < -1e-6 || l3 < -1e-6) continue;
                    double h = l1 * ay + l2 * by + l3 * cy;
                    bool dup = false; foreach (var e in into) if (Math.Abs(e - h) < 0.3) { dup = true; break; }
                    if (!dup) into.Add(h);
                }
            }
        }

        float[] Tris(int i)
        {
            if (tris.TryGetValue(i, out var t)) { lru.Remove(i); lru.AddFirst(i); return t; }
            var p = pl[i]; int fdid = (int)p.NameId;
            if (!models.TryGetValue(fdid, out var m)) { try { m = model?.Invoke(fdid); } catch (Exception) { m = null; } models[fdid] = m; if (models.Count > MaxCached * 2) models.Clear(); }
            var list = new List<float>();
            if (m != null && !m.Stub)
            {
                var l = m.Lods[0]; int nv = l.VertexCount;
                var v = new double[nv * 3];
                for (int j = 0; j < nv; j++)
                {
                    m.Vertex(0, j, out var lx, out var ly, out var lz);
                    // WmoTransform takes the model's own WoW axes (x north, y west, z up) = scene (z, x, y)
                    WmoTransform.ToScene(p, lz, lx, ly, out v[j * 3], out v[j * 3 + 1], out v[j * 3 + 2]);
                }
                for (int o = 0; o + 2 < l.Indices.Length; o += 3)
                {
                    int a = l.Indices[o] * 3, b = l.Indices[o + 1] * 3, c = l.Indices[o + 2] * 3;
                    double ux = v[b] - v[a], uy = v[b + 1] - v[a + 1], uz = v[b + 2] - v[a + 2], wx = v[c] - v[a], wy = v[c + 1] - v[a + 1], wz = v[c + 2] - v[a + 2];
                    double nx = uy * wz - uz * wy, ny = uz * wx - ux * wz, nz = ux * wy - uy * wx, nl = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                    if (nl < 1e-9 || Math.Abs(ny) / nl < MinNormalY) continue;
                    foreach (var q in new[] { a, b, c }) { list.Add((float)v[q]); list.Add((float)v[q + 1]); list.Add((float)v[q + 2]); }
                }
            }
            t = list.ToArray();
            tris[i] = t; lru.AddFirst(i);
            while (lru.Count > MaxCached) { tris.Remove(lru.Last.Value); lru.RemoveLast(); }
            return t;
        }
    }

    // ============================================================================================================================
    // The real world for the solver: the terrain cache (.wmt / .wmw) of this game build, the 3D view's buildings (whatever was made: bridges only or all), the flight
    // routes of the map data. Read lazily from the Outpost's own data folder; a continent without terrain answers NaN (the page then also draws nothing there).
    // ============================================================================================================================
    public sealed class GameElevationWorld : IElevationWorld
    {
        const double Ty = 1600.0 / 3, Mid = 32 * Ty;
        public static readonly GameElevationWorld Shared = new GameElevationWorld();
        readonly object gate = new object();
        readonly Dictionary<int, TerrainSampler> terrain = new Dictionary<int, TerrainSampler>();
        readonly Dictionary<int, BuildingFloors> floors = new Dictionary<int, BuildingFloors>();
        readonly Dictionary<int, DateTime> floorsAt = new Dictionary<int, DateTime>();
        Dictionary<int, List<TaxiRoute>> routes; MapSet routesOf;
        string key;

        public static void ToScene(double wx, double wy, out double sx, out double sz) { sx = Mid - wy; sz = Mid - wx; }

        void Fresh()
        {
            var k = WorldMapCache.BuildKey;
            if (k != key) { key = k; terrain.Clear(); terrainAt.Clear(); floors.Clear(); floorsAt.Clear(); }
        }
        readonly Dictionary<int, DateTime> terrainAt = new Dictionary<int, DateTime>();
        TerrainSampler T(int mapId)
        {
            Fresh();
            // the terrain can be made while you play: a continent without it is looked at again every minute
            if (!terrain.TryGetValue(mapId, out var t) || (t == null && (!terrainAt.TryGetValue(mapId, out var at) || DateTime.UtcNow - at > TimeSpan.FromMinutes(1))))
            {
                t = WorldMapCache.TerrainBuilt(mapId) != null ? new TerrainSampler(WorldMapCache.TerrainDir(key, mapId)) : null;
                terrain[mapId] = t; terrainAt[mapId] = DateTime.UtcNow;
            }
            return t;
        }
        public double Ground(int mapId, double x, double y)
        {
            lock (gate) { var t = T(mapId); if (t == null) return double.NaN; ToScene(x, y, out var sx, out var sz); return t.Ground(sx, sz); }
        }
        public double Water(int mapId, double x, double y)
        {
            lock (gate) { var t = T(mapId); if (t == null) return double.NaN; ToScene(x, y, out var sx, out var sz); return t.Water(sx, sz); }
        }
        public void Floors(int mapId, double x, double y, List<double> into)
        {
            lock (gate)
            {
                Fresh();
                // the buildings can be made while you play: looked for again every minute until they are there
                if (!floors.TryGetValue(mapId, out var f) || (f == null && (!floorsAt.TryGetValue(mapId, out var at) || DateTime.UtcNow - at > TimeSpan.FromMinutes(1))))
                {
                    f = BuildingFloors.FromDir(WorldMapBuildings.Dir(WorldMapCache.MapDir(key, mapId)));
                    floors[mapId] = f; floorsAt[mapId] = DateTime.UtcNow;
                }
                if (f == null) return;
                ToScene(x, y, out var sx, out var sz);
                f.At(sx, sz, into);
            }
        }
        public IReadOnlyList<TaxiRoute> Routes(int mapId)
        {
            lock (gate)
            {
                var set = MapData.Index?.Set;
                if (set != routesOf) { routesOf = set; routes = TaxiRoute.FromSet(set); }
                return routes != null && routes.TryGetValue(mapId, out var l) ? l : (IReadOnlyList<TaxiRoute>)new TaxiRoute[0];
            }
        }
    }
}
