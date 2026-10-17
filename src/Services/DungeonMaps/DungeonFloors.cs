using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ElansAddonHub.Services.DungeonMaps
{
    // ============================================================================================================================
    // Splits the ground of an instance (PlanGeometry) into floors: layers that never cover each other in a top-down view. Ramps join the levels of most
    // dungeons, so height bands would cut rooms in half; instead the floors follow the ground a character walks on:
    //   1. the walkable triangles are laid on a 2-yard grid; in each cell the heights form layers (surfaces within LayerYards of each other are one). Two layers
    //      are joined when one triangle covers both, or when they are in neighbouring cells and the height steps by no more than StepYards.
    //   2. what is joined is an area a character can walk around in; small islands (rooftops, pillar tops, tables, ledges) are left out (Reachable).
    //   3. floor 1 grows from the lowest ground left, step by step along the joins, taking every layer whose cell it does not cover yet at another height
    //      (more than Gap yards apart); what it cannot take (the part of a path that passes over or under itself) waits for the next floor, which grows the same
    //      way from the lowest ground still left.
    //   4. floors that turn out not to cover each other become one; a floor under 2 % of the whole (or under TinyArea square yards) joins the floor it touches
    //      or the nearest in height; at most MaxFloors.
    //   5. floors are ordered bottom to top and named after the biggest building group on them that has a real name ("Magmadar Cavern", not "cave2_18"; not
    //      the instance's own name), else "Level 1", "Level 2" ...
    // ============================================================================================================================
    public sealed class PlanFloor
    {
        public string Name = "";
        public List<PlanUnit> Units = new List<PlanUnit>();
        public double Area, MinZ = double.MaxValue, MaxZ = double.MinValue, MeanZ;
        internal readonly Dictionary<long, float[]> Cells = new Dictionary<long, float[]>();    // cell -> { min z, max z }
        internal double SumZ;
        public int Triangles => Units.Sum(u => u.TriCount);
    }

    public static class DungeonFloors
    {
        public const double Cell = 2, Gap = 6, ClashShare = 0.03, TinyShare = 0.02, TinyArea = 400;
        public const double StepYards = 3, LayerYards = 2.5, IslandShare = 0.02, IslandArea = 1000;
        public const int MaxFloors = 8;

        // ---------------------------------------------------------------- the grid of layers
        sealed class Graph
        {
            public readonly Dictionary<long, List<int>> CellLayers = new Dictionary<long, List<int>>();
            public readonly List<long> Key = new List<long>();
            public readonly List<float> Lo = new List<float>(), Hi = new List<float>();
            public readonly List<HashSet<int>> Edges = new List<HashSet<int>>();
            public readonly List<int> Parent = new List<int>();
            public int Count => Key.Count;

            public int Find(int a) { while (Parent[a] != a) { Parent[a] = Parent[Parent[a]]; a = Parent[a]; } return a; }
            public void Join(int a, int b)
            {
                if (a == b) return;
                Edges[a].Add(b); Edges[b].Add(a);
                a = Find(a); b = Find(b); if (a != b) Parent[Math.Max(a, b)] = Math.Min(a, b);
            }

            public int Layer(long key, double z)
            {
                if (!CellLayers.TryGetValue(key, out var ls)) CellLayers[key] = ls = new List<int>();
                int hit = -1;
                foreach (var l in ls)
                    if (z >= Lo[l] - LayerYards && z <= Hi[l] + LayerYards)
                    {
                        if (z < Lo[l]) Lo[l] = (float)z; if (z > Hi[l]) Hi[l] = (float)z;
                        if (hit < 0) hit = l; else Join(hit, l);
                    }
                if (hit >= 0) return hit;
                int id = Key.Count;
                Key.Add(key); Lo.Add((float)z); Hi.Add((float)z); Edges.Add(new HashSet<int>()); Parent.Add(id);
                ls.Add(id);
                return id;
            }

            // the layer of a cell that holds z (or the nearest; -1 = the cell has none)
            public int At(long key, double z)
            {
                if (!CellLayers.TryGetValue(key, out var ls)) return -1;
                int best = -1; double bd = double.MaxValue;
                foreach (var l in ls) { double d = z < Lo[l] ? Lo[l] - z : z > Hi[l] ? z - Hi[l] : 0; if (d < bd) { bd = d; best = l; } }
                return best;
            }
        }

        static Graph Build(IList<PlanUnit> units, List<int[]> triNode)
        {
            var g = new Graph();
            foreach (var u in units)
            {
                var first = new int[u.TriCount];
                var t = u.Tri;
                for (int i = 0; i < u.TriCount; i++)
                {
                    int o = i * 9, f = -1;
                    RasterCells(t[o], t[o + 1], t[o + 2], t[o + 3], t[o + 4], t[o + 5], t[o + 6], t[o + 7], t[o + 8], (key, z) =>
                    {
                        int l = g.Layer(key, z);
                        if (f < 0) f = l; else g.Join(f, l);
                    });
                    first[i] = f;
                }
                triNode.Add(first);
            }
            foreach (var kv in g.CellLayers.ToList())
            {
                Unkey(kv.Key, out var cx, out var cy);
                foreach (var nk in new[] { DungeonGeometry.Key(cx + 1, cy), DungeonGeometry.Key(cx, cy + 1), DungeonGeometry.Key(cx + 1, cy + 1), DungeonGeometry.Key(cx + 1, cy - 1) })
                {
                    if (!g.CellLayers.TryGetValue(nk, out var nl)) continue;
                    foreach (var a in kv.Value)
                        foreach (var b in nl)
                            if (Apart(g.Lo[a], g.Hi[a], g.Lo[b], g.Hi[b]) <= StepYards) g.Join(a, b);
                }
            }
            return g;
        }

        static double Apart(double alo, double ahi, double blo, double bhi) => Math.Max(0, Math.Max(alo - bhi, blo - ahi));
        static double Apart(float[] a, float[] b) => Apart(a[0], a[1], b[0], b[1]);
        static double TriArea(List<float> t, int o) => Math.Abs((t[o + 3] - t[o]) * (t[o + 7] - t[o + 1]) - (t[o + 6] - t[o]) * (t[o + 4] - t[o + 1])) / 2;

        // the walkable areas big enough to keep (by their root in the graph)
        static HashSet<int> Kept(Graph g, IList<PlanUnit> units, List<int[]> triNode, out int islands)
        {
            var area = new Dictionary<int, double>();
            for (int k = 0; k < units.Count; k++)
                for (int i = 0; i < units[k].TriCount; i++)
                {
                    if (triNode[k][i] < 0) continue;
                    int r = g.Find(triNode[k][i]);
                    area.TryGetValue(r, out var a); area[r] = a + TriArea(units[k].Tri, i * 9);
                }
            double biggest = area.Count == 0 ? 0 : area.Values.Max();
            var keep = new HashSet<int>(area.Where(kv => kv.Value >= biggest * IslandShare || kv.Value >= IslandArea).Select(kv => kv.Key));
            islands = area.Count - keep.Count;
            return keep;
        }

        // the units without the islands nobody walks to (units left empty are dropped)
        public static List<PlanUnit> Reachable(IList<PlanUnit> units, out int islands, out double droppedArea)
        {
            var triNode = new List<int[]>();
            var g = Build(units, triNode);
            var keep = Kept(g, units, triNode, out islands);
            droppedArea = 0;
            var res = new List<PlanUnit>();
            for (int k = 0; k < units.Count; k++)
            {
                var u = units[k];
                var s = new PlanUnit { Id = u.Id, Kind = u.Kind, Name = u.Name, Wmo = u.Wmo, Place = u.Place, Group = u.Group, Interior = u.Interior };
                for (int i = 0; i < u.TriCount; i++)
                {
                    int o = i * 9;
                    if (triNode[k][i] >= 0 && keep.Contains(g.Find(triNode[k][i]))) s.Add(u.Tri[o], u.Tri[o + 1], u.Tri[o + 2], u.Tri[o + 3], u.Tri[o + 4], u.Tri[o + 5], u.Tri[o + 6], u.Tri[o + 7], u.Tri[o + 8]);
                    else droppedArea += TriArea(u.Tri, o);
                }
                if (s.TriCount > 0) res.Add(s);
            }
            return res;
        }

        // ---------------------------------------------------------------- the split
        public static List<PlanFloor> Split(PlanGeometry geo, string instanceName = null)
        {
            var units = geo.Units;
            var triNode = new List<int[]>();
            var g = Build(units, triNode);
            var keep = Kept(g, units, triNode, out var islands);
            geo.Islands = islands; geo.IslandArea = 0;

            // floors grow from the lowest ground left, along the joins
            var floorOf = new int[g.Count];
            for (int i = 0; i < floorOf.Length; i++) floorOf[i] = keep.Contains(g.Find(i)) ? -1 : -2;
            var order = Enumerable.Range(0, g.Count).Where(i => floorOf[i] == -1).OrderBy(i => g.Lo[i]).ToList();
            int floors = 0, next = 0;
            while (true)
            {
                while (next < order.Count && floorOf[order[next]] != -1) next++;
                if (next >= order.Count) break;
                int f = floors++;
                var taken = new Dictionary<long, List<int>>();       // this floor's layers per cell
                var queue = new Queue<int>();
                var refused = new HashSet<int>();
                bool Fits(int n)
                {
                    if (!taken.TryGetValue(g.Key[n], out var ls)) return true;
                    foreach (var l in ls) if (Apart(g.Lo[l], g.Hi[l], g.Lo[n], g.Hi[n]) > Gap) return false;
                    return true;
                }
                void Take(int n)
                {
                    floorOf[n] = f;
                    if (!taken.TryGetValue(g.Key[n], out var ls)) taken[g.Key[n]] = ls = new List<int>();
                    ls.Add(n);
                    queue.Enqueue(n);
                }
                Take(order[next]);
                while (queue.Count > 0)
                {
                    int n = queue.Dequeue();
                    foreach (var m in g.Edges[n])
                    {
                        if (floorOf[m] != -1 || refused.Contains(m)) continue;
                        if (Fits(m)) Take(m); else refused.Add(m);
                    }
                }
                if (floors > 64) break;    // a safety net: everything left joins the last floor below
            }
            for (int i = 0; i < floorOf.Length; i++) if (floorOf[i] == -1) floorOf[i] = floors - 1;

            // the triangles go to the floor most of their cells are on
            var parts = new Dictionary<int, Dictionary<int, PlanUnit>>();       // floor -> unit index -> part
            for (int k = 0; k < units.Count; k++)
            {
                var u = units[k]; var t = u.Tri;
                for (int i = 0; i < u.TriCount; i++)
                {
                    int o = i * 9;
                    if (triNode[k][i] < 0 || !keep.Contains(g.Find(triNode[k][i]))) { geo.IslandArea += TriArea(t, o); continue; }
                    var votes = new Dictionary<int, int>();
                    RasterCells(t[o], t[o + 1], t[o + 2], t[o + 3], t[o + 4], t[o + 5], t[o + 6], t[o + 7], t[o + 8], (key, z) =>
                    {
                        int l = g.At(key, z);
                        if (l < 0 || floorOf[l] < 0) return;
                        votes.TryGetValue(floorOf[l], out var c); votes[floorOf[l]] = c + 1;
                    });
                    if (votes.Count == 0) continue;
                    int fl = votes.OrderByDescending(v => v.Value).ThenBy(v => v.Key).First().Key;
                    if (!parts.TryGetValue(fl, out var byUnit)) parts[fl] = byUnit = new Dictionary<int, PlanUnit>();
                    if (!byUnit.TryGetValue(k, out var part)) byUnit[k] = part = new PlanUnit { Id = u.Id, Kind = u.Kind, Name = u.Name, Wmo = u.Wmo, Place = u.Place, Group = u.Group, Interior = u.Interior };
                    part.Add(t[o], t[o + 1], t[o + 2], t[o + 3], t[o + 4], t[o + 5], t[o + 6], t[o + 7], t[o + 8]);
                }
            }
            var list = new List<PlanFloor>();
            foreach (var kv in parts.OrderBy(p => p.Key))
            {
                var pf = new PlanFloor();
                foreach (var part in kv.Value.Values) Join(pf, part);
                list.Add(pf);
            }

            // floors that do not cover each other become one; tiny floors join a neighbour; at most MaxFloors
            bool merged = true;
            while (merged && list.Count > 1)
            {
                merged = false;
                foreach (var pair in Pairs(list).OrderByDescending(p => Touching(p.Item1, p.Item2) > 0).ThenBy(p => Math.Abs(p.Item1.MeanZ - p.Item2.MeanZ)))
                {
                    var small = pair.Item1.Area <= pair.Item2.Area ? pair.Item1 : pair.Item2; var big = small == pair.Item1 ? pair.Item2 : pair.Item1;
                    if (Clash(small, big) > small.Cells.Count * ClashShare) continue;
                    foreach (var u in small.Units) Join(big, u);
                    list.Remove(small); merged = true;
                    break;
                }
            }
            double total = list.Sum(f => f.Area);
            merged = true;
            while (merged && list.Count > 1)
            {
                merged = false;
                foreach (var f in list.OrderBy(f => f.Area).ToList())
                {
                    bool tiny = f.Area < total * TinyShare || f.Area < TinyArea;
                    if (!tiny && list.Count <= MaxFloors) break;
                    var into = list.Where(o => o != f).OrderByDescending(o => Touching(o, f)).ThenBy(o => Math.Abs(o.MeanZ - f.MeanZ)).First();
                    foreach (var u in f.Units) Join(into, u);
                    list.Remove(f); merged = true;
                    break;
                }
            }
            list = list.OrderBy(f => f.MeanZ).ToList();
            Name(list, instanceName);
            return list;
        }

        static IEnumerable<Tuple<PlanFloor, PlanFloor>> Pairs(List<PlanFloor> floors)
        {
            for (int i = 0; i < floors.Count; i++) for (int j = i + 1; j < floors.Count; j++) yield return Tuple.Create(floors[i], floors[j]);
        }

        static int Clash(PlanFloor a, PlanFloor b)
        {
            int n = 0;
            foreach (var kv in a.Cells) if (b.Cells.TryGetValue(kv.Key, out var bz) && Apart(kv.Value, bz) > Gap) n++;
            return n;
        }

        static bool Touches(PlanFloor f, long key, float[] z)
        {
            Unkey(key, out var cx, out var cy);
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    if (f.Cells.TryGetValue(DungeonGeometry.Key(cx + dx, cy + dy), out var fz) && Apart(z, fz) <= Gap) return true;
            return false;
        }

        static int Touching(PlanFloor a, PlanFloor b)
        {
            var small = a.Cells.Count < b.Cells.Count ? a : b; var big = small == a ? b : a;
            int n = 0;
            foreach (var kv in small.Cells) if (Touches(big, kv.Key, kv.Value)) { n++; if (n > 50) break; }
            return n;
        }

        // a unit (or part of one) onto a floor: its cells, its area, its height
        static void Join(PlanFloor f, PlanUnit u)
        {
            f.Units.Add(u);
            double sz = 0, sa = 0;
            var t = u.Tri;
            for (int i = 0; i < u.TriCount; i++)
            {
                int o = i * 9;
                double area = TriArea(t, o);
                sz += (t[o + 2] + t[o + 5] + t[o + 8]) / 3 * area; sa += area;
                RasterCells(t[o], t[o + 1], t[o + 2], t[o + 3], t[o + 4], t[o + 5], t[o + 6], t[o + 7], t[o + 8], (key, z) =>
                {
                    if (f.Cells.TryGetValue(key, out var have)) { if (z < have[0]) have[0] = (float)z; if (z > have[1]) have[1] = (float)z; }
                    else f.Cells[key] = new[] { (float)z, (float)z };
                    if (z < f.MinZ) f.MinZ = z;
                    if (z > f.MaxZ) f.MaxZ = z;
                });
            }
            f.Area += u.Area;
            f.SumZ += sz;
            f.MeanZ = f.Area <= 0 ? (u.TriCount > 0 ? t[2] : 0) : f.SumZ / f.Area;
        }

        internal static void Unkey(long key, out long cx, out long cy) { cx = key / 4000000L - 1000000L; cy = key % 4000000L - 1000000L; }

        // the grid cells a triangle covers (cell centres inside it, plus the cells of its corners and middle so a sliver is never lost), with the height there
        internal static void RasterCells(double ax, double ay, double az, double bx, double by, double bz, double cx, double cy, double cz, Action<long, double> cell)
        {
            double minX = Math.Min(ax, Math.Min(bx, cx)), maxX = Math.Max(ax, Math.Max(bx, cx)), minY = Math.Min(ay, Math.Min(by, cy)), maxY = Math.Max(ay, Math.Max(by, cy));
            long i0 = (long)Math.Floor(minX / Cell), i1 = (long)Math.Floor(maxX / Cell), j0 = (long)Math.Floor(minY / Cell), j1 = (long)Math.Floor(maxY / Cell);
            double den = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy);
            cell(DungeonGeometry.Key(ax / Cell, ay / Cell), az); cell(DungeonGeometry.Key(bx / Cell, by / Cell), bz); cell(DungeonGeometry.Key(cx / Cell, cy / Cell), cz);
            cell(DungeonGeometry.Key((ax + bx + cx) / 3 / Cell, (ay + by + cy) / 3 / Cell), (az + bz + cz) / 3);
            if (Math.Abs(den) < 1e-9 || (i1 - i0 + 1) * (j1 - j0 + 1) > 1000000) return;
            for (long i = i0; i <= i1; i++)
                for (long j = j0; j <= j1; j++)
                {
                    double px = (i + 0.5) * Cell, py = (j + 0.5) * Cell;
                    double w1 = ((by - cy) * (px - cx) + (cx - bx) * (py - cy)) / den, w2 = ((cy - ay) * (px - cx) + (ax - cx) * (py - cy)) / den, w3 = 1 - w1 - w2;
                    if (w1 < -1e-6 || w2 < -1e-6 || w3 < -1e-6) continue;
                    cell(DungeonGeometry.Key(i, j), w1 * az + w2 * bz + w3 * cz);
                }
        }

        // ---------------------------------------------------------------- names
        // a name a person would say: capitalised words of letters ("Ironclad Cove", "Halls of the Law", "Ragnaros' Lair"; never "cave2_18", "DerRing", "LAVA")
        static readonly Regex RealName = new Regex(@"^[A-Z][a-z']+([ \-][A-Za-z][a-z']*)*$", RegexOptions.CultureInvariant);

        public static bool IsRealName(string n) => n != null && n.Trim().Length >= 4 && RealName.IsMatch(n.Trim());

        static string Simple(string s) { s = (s ?? "").Trim().ToLowerInvariant(); if (s.StartsWith("the ")) s = s.Substring(4); return s; }

        static void Name(List<PlanFloor> floors, string instanceName)
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < floors.Count; i++)
            {
                var f = floors[i];
                var pick = f.Units.Where(u => u.Kind == PlanUnitKind.Building && IsRealName(u.Name) && Simple(u.Name) != Simple(instanceName))
                    .GroupBy(u => u.Name.Trim(), StringComparer.OrdinalIgnoreCase).Select(g => new { g.Key, A = g.Sum(u => u.Area) })
                    .Where(x => x.A >= f.Area * 0.15 && !used.Contains(x.Key)).OrderByDescending(x => x.A).FirstOrDefault();
                f.Name = pick?.Key ?? "";
                if (f.Name.Length > 0) used.Add(f.Name);
            }
            for (int i = 0; i < floors.Count; i++) if (floors[i].Name.Length == 0) floors[i].Name = "Level " + (i + 1);
        }
    }
}
