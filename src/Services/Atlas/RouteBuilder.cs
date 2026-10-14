using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ElansAddonHub.Services.Atlas
{
    // A gathering node as the route builder and the map see it: x, y = fractions of the zone map, N = times looted (0 = a Classic baseline position),
    // Object = which object (Copper Vein, Peacebloom ...) so a loop through several kinds can number and name its stops.
    public struct GNode
    {
        public double X, Y;
        public long N;
        public bool Forever;
        public int Object;
        public GNode(double x, double y, long n, bool forever, int obj = 0) { X = x; Y = y; N = n; Forever = forever; Object = obj; }
    }

    public sealed class RouteResult
    {
        public List<GNode> Points = new List<GNode>();     // the stops in loop order (the loop closes back to the first)
        public int ForeverNodes, ClassicNodes;             // raw nodes that went in (before clustering)
        public bool UsedClassic;                           // Classic baseline positions are part of the loop
        public string Src => UsedClassic ? "c" : "f";      // the hand-off label: "f" = only Forever nodes, "c" = Classic baseline positions are in it
        public double Length;                              // of the closed loop, in yards when the map's size is known, else in map fractions (see Yards)
        public bool Yards;
        public double Minutes;                             // travel time at run speed; 0 when the length is unknown
        public int Work;                                   // distance evaluations spent (2-opt is capped)
        public string Note = "";
        public int Count => Points.Count;

        // "12 nodes, about 6 minutes at run speed (1,350 yards)"
        public string Summary()
        {
            var inv = CultureInfo.InvariantCulture;
            if (Points.Count == 0) return "No nodes known for this selection.";
            string s = Points.Count + (Points.Count == 1 ? " node" : " nodes");
            if (Points.Count >= 2 && Yards) s += ", about " + Math.Max(1, (int)Math.Round(Minutes)).ToString(inv) + (Math.Round(Minutes) == 1 ? " minute" : " minutes") + " at run speed (" + ((long)Math.Round(Length)).ToString("N0", inv) + " yards)";
            else if (Points.Count >= 2) s += ", length unknown (this map has no world size)";
            return s;
        }
    }

    // ============================================================================================================================
    // Gathering loops (pure; no UI, no files). The recipe, the same as the contract (profguide part C):
    //   1. the nodes closer than ClusterDistance (map fractions) are one stop (greedy, most looted first; the stop sits at their looted-weighted mean);
    //   2. a nearest-neighbour tour starting at the stop closest to the zone's centre;
    //   3. 2-opt until no swap shortens the loop, within a cap on the distance evaluations;
    //   4. the loop is closed (it returns to its first stop).
    // Forever nodes (what players looted) come first. The Classic baseline's positions are only added when there are fewer than MinForever Forever nodes
    // for the set in the zone, and the result says so. Length uses the map's size in yards when it is known (x and y are scaled separately).
    // ============================================================================================================================
    public static class RouteBuilder
    {
        public const double ClusterDistance = 0.02;
        public const int MinForever = 8;
        public const int MaxStops = 120;                  // the hand-off cap per zone
        public const int WorkCap = 4_000_000;             // distance evaluations in 2-opt
        public const double RunYardsPerSecond = 7.0;      // a character on foot at normal speed

        // forever / classic: the raw nodes of the whole set (any number of objects) on one zone map.
        // yardsW / yardsH: the zone map's size in yards (0 = unknown). centre: where the tour starts near (default the middle of the map).
        public static RouteResult Build(IEnumerable<GNode> forever, IEnumerable<GNode> classic, double yardsW = 0, double yardsH = 0, double centreX = 0.5, double centreY = 0.5, int maxStops = MaxStops, int workCap = WorkCap)
        {
            var f = (forever ?? Enumerable.Empty<GNode>()).Where(Valid).Select(n => { n.Forever = true; return n; }).ToList();
            var c = (classic ?? Enumerable.Empty<GNode>()).Where(Valid).Select(n => { n.Forever = false; n.N = 0; return n; }).ToList();
            var r = new RouteResult { ForeverNodes = f.Count, ClassicNodes = c.Count };
            var pool = new List<GNode>(f);
            if (f.Count < MinForever && c.Count > 0)
            {
                pool.AddRange(c);
                r.UsedClassic = true;
                r.Note = f.Count == 0
                    ? "No Forever nodes known here yet: this loop uses the Classic baseline's positions."
                    : "Only " + f.Count + " Forever " + (f.Count == 1 ? "node is" : "nodes are") + " known here (fewer than " + MinForever + "): the Classic baseline's positions fill the loop.";
            }
            if (pool.Count == 0) return r;
            var stops = Cluster(pool, ClusterDistance);
            if (stops.Count > maxStops)
            {
                stops = stops.OrderByDescending(s => s.Forever ? 1 : 0).ThenByDescending(s => s.N).ThenBy(s => s.X).ThenBy(s => s.Y).Take(maxStops).ToList();
                r.Note = (r.Note.Length > 0 ? r.Note + " " : "") + "The loop keeps the " + maxStops + " most looted stops.";
            }
            if (stops.Count > 0 && !stops.Any(s => !s.Forever)) r.UsedClassic = false;
            bool scaled = yardsW > 0 && yardsH > 0;
            double sx = scaled ? yardsW : 1, sy = scaled ? yardsH : 1;
            var order = NearestNeighbour(stops, sx, sy, centreX, centreY);
            int work = 0;
            TwoOpt(stops, order, sx, sy, workCap, ref work);
            r.Work = work;
            foreach (var i in order) r.Points.Add(stops[i]);
            r.Length = LoopLength(r.Points, sx, sy);
            r.Yards = scaled;
            r.Minutes = scaled && r.Points.Count >= 2 ? r.Length / RunYardsPerSecond / 60.0 : 0;
            return r;
        }

        static bool Valid(GNode n) => !double.IsNaN(n.X) && !double.IsNaN(n.Y) && n.X >= 0 && n.X <= 1 && n.Y >= 0 && n.Y <= 1;

        // Greedy clustering: points are taken most looted first (Forever before Classic), each joins the first stop whose seed is closer than `dist`, otherwise
        // starts a new one. A stop sits at the looted-weighted mean of its members (an unlooted Classic position counts as 1), keeps the total N,
        // is Forever when any member is, and carries the object of its heaviest member.
        public static List<GNode> Cluster(IEnumerable<GNode> pts, double dist)
        {
            var sorted = pts.OrderByDescending(p => p.Forever ? 1 : 0).ThenByDescending(p => p.N).ThenBy(p => p.X).ThenBy(p => p.Y).ThenBy(p => p.Object).ToList();
            var seeds = new List<GNode>(); var members = new List<List<GNode>>();
            double d2 = dist * dist;
            foreach (var p in sorted)
            {
                int hit = -1;
                for (int i = 0; i < seeds.Count; i++)
                {
                    double dx = seeds[i].X - p.X, dy = seeds[i].Y - p.Y;
                    if (dx * dx + dy * dy < d2) { hit = i; break; }
                }
                if (hit < 0) { seeds.Add(p); members.Add(new List<GNode> { p }); }
                else members[hit].Add(p);
            }
            var res = new List<GNode>(seeds.Count);
            for (int i = 0; i < seeds.Count; i++)
            {
                var m = members[i];
                double w = 0, x = 0, y = 0; long n = 0;
                foreach (var p in m) { double wt = Math.Max(1, p.N); w += wt; x += p.X * wt; y += p.Y * wt; n += p.N; }
                res.Add(new GNode(Math.Round(x / w, 4), Math.Round(y / w, 4), n, m.Any(p => p.Forever), seeds[i].Object));
            }
            return res;
        }

        static double Dist(GNode a, GNode b, double sx, double sy)
        {
            double dx = (a.X - b.X) * sx, dy = (a.Y - b.Y) * sy;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        // indices in tour order, starting at the stop closest to (cx, cy)
        public static List<int> NearestNeighbour(IReadOnlyList<GNode> pts, double sx, double sy, double cx = 0.5, double cy = 0.5)
        {
            int n = pts.Count;
            var order = new List<int>(n);
            if (n == 0) return order;
            var centre = new GNode(cx, cy, 0, false);
            int cur = 0; double best = double.MaxValue;
            for (int i = 0; i < n; i++) { double d = Dist(pts[i], centre, sx, sy); if (d < best - 1e-12) { best = d; cur = i; } }
            var used = new bool[n];
            order.Add(cur); used[cur] = true;
            for (int step = 1; step < n; step++)
            {
                int next = -1; double bd = double.MaxValue;
                for (int i = 0; i < n; i++)
                {
                    if (used[i]) continue;
                    double d = Dist(pts[cur], pts[i], sx, sy);
                    if (d < bd - 1e-12) { bd = d; next = i; }
                }
                order.Add(next); used[next] = true; cur = next;
            }
            return order;
        }

        // 2-opt on a closed loop: reverse a stretch whenever that shortens the loop, until a full pass finds nothing (or the work cap is reached).
        // The first stop stays first. Returns the number of swaps.
        public static int TwoOpt(IReadOnlyList<GNode> pts, List<int> order, double sx, double sy, int workCap, ref int work)
        {
            int n = order.Count, swaps = 0;
            if (n < 4) return 0;
            bool improved = true;
            while (improved && work < workCap)
            {
                improved = false;
                for (int i = 0; i < n - 1 && work < workCap; i++)
                    for (int j = i + 2; j < n && work < workCap; j++)
                    {
                        if (i == 0 && j == n - 1) continue;        // the same two edges of the closed loop
                        var a = pts[order[i]]; var b = pts[order[i + 1]]; var c = pts[order[j]]; var d = pts[order[(j + 1) % n]];
                        work++;
                        double before = Dist(a, b, sx, sy) + Dist(c, d, sx, sy), after = Dist(a, c, sx, sy) + Dist(b, d, sx, sy);
                        if (after < before - 1e-9)
                        {
                            order.Reverse(i + 1, j - i);
                            swaps++; improved = true;
                        }
                    }
            }
            return swaps;
        }

        public static double LoopLength(IReadOnlyList<GNode> pts, double sx, double sy)
        {
            if (pts.Count < 2) return 0;
            double len = 0;
            for (int i = 0; i < pts.Count; i++) len += Dist(pts[i], pts[(i + 1) % pts.Count], sx, sy);
            return len;
        }

        public static double LoopLength(IReadOnlyList<int> order, IReadOnlyList<GNode> pts, double sx, double sy)
            => LoopLength(order.Select(i => pts[i]).ToList(), sx, sy);
    }
}
