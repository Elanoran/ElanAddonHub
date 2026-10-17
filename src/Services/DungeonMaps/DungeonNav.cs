using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace ElansAddonHub.Services.DungeonMaps
{
    // ============================================================================================================================
    // Where a character can walk in a drawn instance, for the suggested route (DungeonRoute) and for putting creatures on the right floor.
    //   - a 1-yard grid of the walkable surfaces of each drawn floor (the triangles DungeonFloors gave that floor); a cell can hold several heights
    //     (a bridge over a hall), each its own node, so a node is (cell, height, floor);
    //   - a node is blocked when a wall, pillar or steep rock of the buildings (PlanGeometry.Walls) passes through the space a character fills there
    //     (Foot to Body yards above the ground): floors usually run on under the walls, so the walls are what keeps a route in the corridors;
    //   - neighbouring nodes (8 directions, on any floor) join when the height steps by at most Step yards per cell (stairs, ramps; where two floors meet);
    //   - parts the grid leaves apart are joined by a passage (Links) where they come within PassageYards of each other at a slope a character can climb
    //     (rise at most PassageSlope x the distance): the plan reads the buildings' own surfaces, not the ramps, planks and bridges placed in them as
    //     decoration, nor water to swim through. A cliff (straight up) stays apart. A route over a passage is drawn dashed.
    // Saved next to the plan as nav.bin ("EDNV", deflated): origin, then per node its cell offset, height and floor.
    // ============================================================================================================================
    public sealed class DungeonNav
    {
        public const double Cell = 1, Step = 1.6, Foot = 0.7, Body = 2.2, Layer = 1.5, WallSample = 0.4;
        public const double PassageYards = 30, PassageSlope = 1.3, PassageCost = 1.5;
        public const int Format = 2, MinPart = 30;
        public int[] LinkA = new int[0], LinkB = new int[0];     // passages: node pairs
        public bool IsLink(int a, int b) { for (int i = 0; i < LinkA.Length; i++) if ((LinkA[i] == a && LinkB[i] == b) || (LinkA[i] == b && LinkB[i] == a)) return true; return false; }
        public int X0, Y0;                      // cell of the origin (world x / Cell, y / Cell)
        public int[] Cx = new int[0], Cy = new int[0];
        public float[] Z = new float[0];
        public byte[] Floor = new byte[0];      // the plan's floor index
        public int Count => Z.Length;
        Dictionary<long, int[]> byCell;

        static long Key(long cx, long cy) => (cx + 1000000L) * 4000000L + (cy + 1000000L);
        public double WorldX(int n) => (Cx[n] + 0.5) * Cell;
        public double WorldY(int n) => (Cy[n] + 0.5) * Cell;

        Dictionary<long, int[]> Cells()
        {
            if (byCell != null) return byCell;
            var d = new Dictionary<long, List<int>>();
            for (int n = 0; n < Count; n++) { var k = Key(Cx[n], Cy[n]); if (!d.TryGetValue(k, out var l)) d[k] = l = new List<int>(); l.Add(n); }
            return byCell = d.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
        }

        // ---------------------------------------------------------------- building
        // floors: (plan floor index, its units); walls: 9 floats per triangle
        public static DungeonNav Build(IList<KeyValuePair<int, IList<PlanUnit>>> floors, IList<float> walls)
        {
            // the walkable heights per cell and floor
            var cells = new Dictionary<long, List<float[]>>();      // key -> { z lo, z hi, floor }
            foreach (var f in floors)
                foreach (var u in f.Value)
                {
                    var t = u.Tri;
                    for (int i = 0; i < u.TriCount; i++)
                    {
                        int o = i * 9;
                        Raster(t[o], t[o + 1], t[o + 2], t[o + 3], t[o + 4], t[o + 5], t[o + 6], t[o + 7], t[o + 8], (key, z) =>
                        {
                            if (!cells.TryGetValue(key, out var ls)) cells[key] = ls = new List<float[]>();
                            foreach (var l in ls)
                                if (l[2] == f.Key && z >= l[0] - Layer && z <= l[1] + Layer) { if (z < l[0]) l[0] = (float)z; if (z > l[1]) l[1] = (float)z; return; }
                            ls.Add(new[] { (float)z, (float)z, f.Key });
                        });
                    }
                }
            // the walls, as height ranges per cell
            var wall = new Dictionary<long, List<float>>();          // key -> z samples
            for (int o = 0; o + 9 <= walls.Count; o += 9) SampleWall(walls, o, wall);

            var nav = new DungeonNav();
            var cx = new List<int>(); var cy = new List<int>(); var zs = new List<float>(); var fl = new List<byte>();
            foreach (var kv in cells)
            {
                Unkey(kv.Key, out var x, out var y);
                wall.TryGetValue(kv.Key, out var wz);
                foreach (var l in kv.Value)
                {
                    float z = l[1];                                   // the top of the layer: what a character stands on
                    if (wz != null && wz.Any(h => h > z + Foot && h < z + Body)) continue;
                    cx.Add((int)x); cy.Add((int)y); zs.Add(z); fl.Add((byte)Math.Min(255, Math.Max(0, (int)l[2])));
                }
            }
            nav.Cx = cx.ToArray(); nav.Cy = cy.ToArray(); nav.Z = zs.ToArray(); nav.Floor = fl.ToArray();
            if (nav.Count > 0) { nav.X0 = nav.Cx.Min(); nav.Y0 = nav.Cy.Min(); }
            nav.JoinParts();
            return nav;
        }

        // the connected parts of the grid (without passages): part index per node
        public int[] Parts(out int count)
        {
            var part = new int[Count]; for (int i = 0; i < Count; i++) part[i] = -1;
            count = 0;
            var q = new Queue<int>();
            for (int i = 0; i < Count; i++)
            {
                if (part[i] >= 0) continue;
                part[i] = count; q.Enqueue(i);
                while (q.Count > 0) { int u = q.Dequeue(); foreach (var v in Steps(u)) if (part[v] < 0) { part[v] = count; q.Enqueue(v); } }
                count++;
            }
            return part;
        }

        // passages between parts: the closest pair of edge nodes of two parts (each of at least MinPart nodes), shortest first, each joining two groups that
        // were apart (a spanning forest), so a part is reached the shortest way and no passage is added where the grid already walks
        void JoinParts()
        {
            var part = Parts(out var parts);
            if (parts < 2) return;
            var size = new int[parts]; foreach (var p in part) size[p]++;
            const int cell = 8;
            var grid = new Dictionary<long, List<int>>();
            for (int n = 0; n < Count; n++)
            {
                if (size[part[n]] < MinPart || Steps(n).Count() >= 8) continue;      // edge nodes of big enough parts
                var k = Key(FloorDiv(Cx[n], cell), FloorDiv(Cy[n], cell));
                if (!grid.TryGetValue(k, out var l)) grid[k] = l = new List<int>();
                l.Add(n);
            }
            var best = new Dictionary<long, Tuple<double, int, int>>();
            int r = (int)Math.Ceiling(PassageYards / cell);
            foreach (var kv in grid)
            {
                Unkey(kv.Key, out var gx, out var gy);
                for (int dx = -r; dx <= r; dx++)
                    for (int dy = -r; dy <= r; dy++)
                    {
                        if (!grid.TryGetValue(Key(gx + dx, gy + dy), out var other)) continue;
                        foreach (var a in kv.Value)
                            foreach (var b in other)
                            {
                                int pa = part[a], pb = part[b];
                                if (pa >= pb) continue;
                                double h = Math.Sqrt(Sq(Cx[a] - Cx[b]) + Sq(Cy[a] - Cy[b])) * Cell, v = Math.Abs(Z[a] - Z[b]);
                                if (h > PassageYards || v > PassageSlope * h + Step) continue;
                                double d = Math.Sqrt(h * h + v * v);
                                long key = (long)pa * parts + pb;
                                if (!best.TryGetValue(key, out var have) || d < have.Item1) best[key] = Tuple.Create(d, a, b);
                            }
                    }
            }
            var group = Enumerable.Range(0, parts).ToArray();
            int Find(int x) { while (group[x] != x) { group[x] = group[group[x]]; x = group[x]; } return x; }
            var la = new List<int>(); var lb = new List<int>();
            foreach (var c in best.Values.OrderBy(t => t.Item1))
            {
                int ga = Find(part[c.Item2]), gb = Find(part[c.Item3]);
                if (ga == gb) continue;
                group[Math.Max(ga, gb)] = Math.Min(ga, gb);
                la.Add(c.Item2); lb.Add(c.Item3);
            }
            LinkA = la.ToArray(); LinkB = lb.ToArray();
        }

        static int FloorDiv(int a, int b) => (int)Math.Floor((double)a / b);
        static double Sq(double v) => v * v;

        // the grid's own steps from a node (no passages)
        IEnumerable<int> Steps(int n)
        {
            var cells = Cells();
            for (int k = 0; k < 8; k++)
            {
                if (!cells.TryGetValue(Key(Cx[n] + DX[k], Cy[n] + DY[k]), out var ns)) continue;
                double step = k < 4 ? Step : Step * 1.42;
                foreach (var m in ns) if (Math.Abs(Z[m] - Z[n]) <= step) yield return m;
            }
        }

        static void Unkey(long key, out long cx, out long cy) { cx = key / 4000000L - 1000000L; cy = key % 4000000L - 1000000L; }

        // points of a wall triangle every WallSample yards, as heights in their cells
        static void SampleWall(IList<float> w, int o, Dictionary<long, List<float>> into)
        {
            double ax = w[o], ay = w[o + 1], az = w[o + 2], bx = w[o + 3], by = w[o + 4], bz = w[o + 5], cx = w[o + 6], cy = w[o + 7], cz = w[o + 8];
            double ab = Dist(ax, ay, az, bx, by, bz), ac = Dist(ax, ay, az, cx, cy, cz);
            int nu = Math.Max(1, (int)Math.Ceiling(ab / WallSample)), nv = Math.Max(1, (int)Math.Ceiling(ac / WallSample));
            if ((long)nu * nv > 40000) return;                          // a huge sheet: never a wall a corridor runs along
            long last = long.MinValue;
            for (int i = 0; i <= nu; i++)
                for (int j = 0; j <= nv - (int)((long)i * nv / nu); j++)
                {
                    double s = (double)i / nu, t = (double)j / nv;
                    if (s + t > 1) continue;
                    double x = ax + (bx - ax) * s + (cx - ax) * t, y = ay + (by - ay) * s + (cy - ay) * t, z = az + (bz - az) * s + (cz - az) * t;
                    long key = Key((long)Math.Floor(x / Cell), (long)Math.Floor(y / Cell));
                    if (!into.TryGetValue(key, out var l)) into[key] = l = new List<float>();
                    if (key != last || l.Count == 0 || Math.Abs(l[l.Count - 1] - z) > 0.3) l.Add((float)z);
                    last = key;
                }
        }

        static double Dist(double ax, double ay, double az, double bx, double by, double bz) => Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by) + (az - bz) * (az - bz));

        // the 1-yard cells a triangle covers (centres inside it; a sliver adds its corners), with the height there
        internal static void Raster(double ax, double ay, double az, double bx, double by, double bz, double cx, double cy, double cz, Action<long, double> cell)
        {
            double minX = Math.Min(ax, Math.Min(bx, cx)), maxX = Math.Max(ax, Math.Max(bx, cx)), minY = Math.Min(ay, Math.Min(by, cy)), maxY = Math.Max(ay, Math.Max(by, cy));
            long i0 = (long)Math.Floor(minX / Cell), i1 = (long)Math.Floor(maxX / Cell), j0 = (long)Math.Floor(minY / Cell), j1 = (long)Math.Floor(maxY / Cell);
            double den = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy);
            if (Math.Abs(den) < 1e-9 || (i1 - i0 + 1) * (j1 - j0 + 1) > 4000000) return;
            int hits = 0;
            for (long i = i0; i <= i1; i++)
                for (long j = j0; j <= j1; j++)
                {
                    double px = (i + 0.5) * Cell, py = (j + 0.5) * Cell;
                    double w1 = ((by - cy) * (px - cx) + (cx - bx) * (py - cy)) / den, w2 = ((cy - ay) * (px - cx) + (ax - cx) * (py - cy)) / den, w3 = 1 - w1 - w2;
                    if (w1 < -1e-6 || w2 < -1e-6 || w3 < -1e-6) continue;
                    cell(Key(i, j), w1 * az + w2 * bz + w3 * cz); hits++;
                }
            if (hits == 0) cell(Key((long)Math.Floor((ax + bx + cx) / 3 / Cell), (long)Math.Floor((ay + by + cy) / 3 / Cell)), (az + bz + cz) / 3);
        }

        // ---------------------------------------------------------------- queries
        // the node nearest a world point: same height band first (|dz| <= dz), within `yards`; -1 = none
        public int Nearest(double x, double y, double z, double yards = 6, double dz = 4)
        {
            var cells = Cells();
            long ccx = (long)Math.Floor(x / Cell), ccy = (long)Math.Floor(y / Cell);
            int r = (int)Math.Ceiling(yards / Cell), best = -1; double bd = double.MaxValue;
            for (int dx = -r; dx <= r; dx++)
                for (int dy = -r; dy <= r; dy++)
                {
                    if (!cells.TryGetValue(Key(ccx + dx, ccy + dy), out var ns)) continue;
                    foreach (var n in ns)
                    {
                        double h = Math.Abs(Z[n] - z);
                        if (h > dz) continue;
                        double flat = Math.Sqrt((WorldX(n) - x) * (WorldX(n) - x) + (WorldY(n) - y) * (WorldY(n) - y));
                        if (flat > yards) continue;
                        double d = flat + h * 0.5;
                        if (d < bd) { bd = d; best = n; }
                    }
                }
            return best;
        }

        static readonly int[] DX = { 1, -1, 0, 0, 1, 1, -1, -1 }, DY = { 0, 0, 1, -1, 1, -1, 1, -1 };

        public IEnumerable<int> Neighbours(int n) => Steps(n);

        // the walkable steps, built once: per node its neighbours and the length of the step (yards, height included)
        int[] adjStart, adjTo; float[] adjLen;
        void Adjacency()
        {
            if (adjStart != null) return;
            var cells = Cells();
            var start = new int[Count + 1]; var to = new List<int>(Count * 6); var len = new List<float>(Count * 6);
            for (int u = 0; u < Count; u++)
            {
                start[u] = to.Count;
                for (int k = 0; k < 8; k++)
                {
                    if (!cells.TryGetValue(Key(Cx[u] + DX[k], Cy[u] + DY[k]), out var ns)) continue;
                    double l = k < 4 ? Cell : Cell * 1.4142, step = k < 4 ? Step : Step * 1.42;
                    foreach (var v in ns)
                    {
                        double dz = Math.Abs(Z[v] - Z[u]);
                        if (dz <= step) { to.Add(v); len.Add((float)Math.Sqrt(l * l + dz * dz)); }
                    }
                }
            }
            start[Count] = to.Count;
            // the passages, after the grid's own steps
            var extra = new List<int>[Count];
            for (int i = 0; i < LinkA.Length; i++)
            {
                int a = LinkA[i], b = LinkB[i];
                (extra[a] ?? (extra[a] = new List<int>())).Add(b);
                (extra[b] ?? (extra[b] = new List<int>())).Add(a);
            }
            if (LinkA.Length > 0)
            {
                var to2 = new List<int>(to.Count + LinkA.Length * 2); var len2 = new List<float>(to.Count + LinkA.Length * 2); var start2 = new int[Count + 1];
                for (int u = 0; u < Count; u++)
                {
                    start2[u] = to2.Count;
                    for (int e = start[u]; e < start[u + 1]; e++) { to2.Add(to[e]); len2.Add(len[e]); }
                    if (extra[u] != null) foreach (var v in extra[u]) { to2.Add(v); len2.Add((float)(Math.Sqrt(Sq(WorldX(u) - WorldX(v)) + Sq(WorldY(u) - WorldY(v)) + Sq(Z[u] - Z[v])) * PassageCost)); }
                }
                start2[Count] = to2.Count;
                to = to2; len = len2; start = start2;
            }
            adjTo = to.ToArray(); adjLen = len.ToArray(); adjStart = start;
        }

        // shortest walks from a node: distance (yards x the cost of the nodes walked into) and the node before, per node
        public void Dijkstra(int from, Func<int, double> cost, out double[] dist, out int[] prev) => Dijkstra(from, cost == null ? null : Enumerable.Range(0, Count).Select(i => (float)cost(i)).ToArray(), out dist, out prev);

        public void Dijkstra(int from, float[] cost, out double[] dist, out int[] prev)
        {
            int n = Count;
            dist = new double[n]; prev = new int[n];
            for (int i = 0; i < n; i++) { dist[i] = double.PositiveInfinity; prev[i] = -1; }
            if (from < 0 || from >= n) return;
            Adjacency();
            var heap = new MinHeap(); dist[from] = 0; heap.Push(from, 0);
            while (heap.Count > 0)
            {
                heap.Pop(out var u, out var du);
                if (du > dist[u]) continue;
                for (int e = adjStart[u]; e < adjStart[u + 1]; e++)
                {
                    int v = adjTo[e];
                    double nd = du + adjLen[e] * (cost == null ? 1 : cost[v]);
                    if (nd < dist[v]) { dist[v] = nd; prev[v] = u; heap.Push(v, nd); }
                }
            }
        }

        public List<int> PathTo(int[] prev, int to)
        {
            var res = new List<int>();
            for (int n = to; n >= 0; n = prev[n]) { res.Add(n); if (res.Count > Count) break; }
            res.Reverse();
            return res;
        }

        sealed class MinHeap
        {
            readonly List<int> ids = new List<int>(); readonly List<double> keys = new List<double>();
            public int Count => ids.Count;
            public void Push(int id, double k)
            {
                ids.Add(id); keys.Add(k);
                for (int i = ids.Count - 1; i > 0;) { int p = (i - 1) / 2; if (keys[p] <= keys[i]) break; Swap(i, p); i = p; }
            }
            public void Pop(out int id, out double k)
            {
                id = ids[0]; k = keys[0];
                int last = ids.Count - 1;
                ids[0] = ids[last]; keys[0] = keys[last]; ids.RemoveAt(last); keys.RemoveAt(last);
                for (int i = 0; ;)
                {
                    int l = i * 2 + 1, r = l + 1, m = i;
                    if (l < ids.Count && keys[l] < keys[m]) m = l;
                    if (r < ids.Count && keys[r] < keys[m]) m = r;
                    if (m == i) break;
                    Swap(i, m); i = m;
                }
            }
            void Swap(int a, int b) { var t = ids[a]; ids[a] = ids[b]; ids[b] = t; var u = keys[a]; keys[a] = keys[b]; keys[b] = u; }
        }

        // ---------------------------------------------------------------- the file
        static readonly byte[] Magic = { (byte)'E', (byte)'D', (byte)'N', (byte)'V' };

        public void Save(string path)
        {
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(Magic, 0, 4);
                using (var z = new DeflateStream(fs, CompressionLevel.Optimal))
                using (var w = new BinaryWriter(z, Encoding.UTF8))
                {
                    w.Write(Format); w.Write(Cell); w.Write(X0); w.Write(Y0); w.Write(Count);
                    for (int i = 0; i < Count; i++) { w.Write(Cx[i] - X0); w.Write(Cy[i] - Y0); w.Write(Z[i]); w.Write(Floor[i]); }
                    w.Write(LinkA.Length); for (int i = 0; i < LinkA.Length; i++) { w.Write(LinkA[i]); w.Write(LinkB[i]); }
                    w.Write(0x564E4445);
                }
            }
        }

        // null = missing, damaged or another format
        public static DungeonNav Load(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var m = new byte[4];
                    if (fs.Read(m, 0, 4) != 4 || !m.SequenceEqual(Magic)) return null;
                    using (var z = new DeflateStream(fs, CompressionMode.Decompress))
                    using (var r = new BinaryReader(z, Encoding.UTF8))
                    {
                        if (r.ReadInt32() != Format || r.ReadDouble() != Cell) return null;
                        var nav = new DungeonNav { X0 = r.ReadInt32(), Y0 = r.ReadInt32() };
                        int n = r.ReadInt32();
                        if (n < 0 || n > 50000000) return null;
                        nav.Cx = new int[n]; nav.Cy = new int[n]; nav.Z = new float[n]; nav.Floor = new byte[n];
                        for (int i = 0; i < n; i++) { nav.Cx[i] = r.ReadInt32() + nav.X0; nav.Cy[i] = r.ReadInt32() + nav.Y0; nav.Z[i] = r.ReadSingle(); nav.Floor[i] = r.ReadByte(); }
                        int ln = r.ReadInt32(); if (ln < 0 || ln > n) return null;
                        nav.LinkA = new int[ln]; nav.LinkB = new int[ln];
                        for (int i = 0; i < ln; i++) { nav.LinkA[i] = r.ReadInt32(); nav.LinkB[i] = r.ReadInt32(); if (nav.LinkA[i] < 0 || nav.LinkA[i] >= n || nav.LinkB[i] < 0 || nav.LinkB[i] >= n) return null; }
                        return r.ReadInt32() == 0x564E4445 ? nav : null;
                    }
                }
            }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is EndOfStreamException || e is UnauthorizedAccessException) { return null; }
        }
    }
}
