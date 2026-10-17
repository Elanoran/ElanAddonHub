using System;
using System.Collections.Generic;
using System.Linq;

namespace ElansAddonHub.Services.WorldMap
{
    // ============================================================================================================================
    // Plane geometry of flight lines (2.88, learned flight paths): world x north, y west, z up, yards; z NaN = no height known for that point.
    // Length, resampling by arc length, Douglas-Peucker simplification, the distance of a point to a line, the deviation of two lines (both ways) and the
    // point-wise average of several lines. Pure functions; the self-test "flightlearn" checks them on invented lines.
    // ============================================================================================================================
    public static class FlightGeo
    {
        public struct P
        {
            public double X, Y, Z;
            public P(double x, double y, double z = double.NaN) { X = x; Y = y; Z = z; }
            public bool HasZ => !double.IsNaN(Z) && !double.IsInfinity(Z);
        }

        // how far apart two lines are: the mean and the largest distance of the points of each (resampled) line to the other line
        public struct Dev
        {
            public double Mean, Max;
            public override string ToString() => "mean " + Math.Round(Mean, 1) + " / max " + Math.Round(Max, 1);
        }

        public static double Dist(double x1, double y1, double x2, double y2) { double dx = x1 - x2, dy = y1 - y2; return Math.Sqrt(dx * dx + dy * dy); }
        public static double Dist(P a, P b) => Dist(a.X, a.Y, b.X, b.Y);

        public static double Length(IList<P> pts)
        {
            double s = 0;
            if (pts == null) return 0;
            for (int i = 1; i < pts.Count; i++) s += Dist(pts[i - 1], pts[i]);
            return s;
        }

        // n points at equal distances along the line (the first and the last stay); the height is interpolated (NaN where either end has none)
        public static List<P> Resample(IList<P> pts, int n)
        {
            var r = new List<P>();
            if (pts == null || pts.Count == 0 || n < 2) return r;
            if (pts.Count == 1) { for (int k = 0; k < n; k++) r.Add(pts[0]); return r; }
            double total = Length(pts);
            if (total <= 1e-9) { for (int k = 0; k < n; k++) r.Add(pts[0]); return r; }
            int i = 0; double acc = 0;
            for (int k = 0; k < n; k++)
            {
                double want = total * k / (n - 1);
                while (i + 1 < pts.Count - 1 && acc + Dist(pts[i], pts[i + 1]) < want) { acc += Dist(pts[i], pts[i + 1]); i++; }
                double seg = Dist(pts[i], pts[i + 1]);
                double t = seg <= 1e-9 ? 0 : Math.Max(0, Math.Min(1, (want - acc) / seg));
                var a = pts[i]; var b = pts[i + 1];
                double z = a.HasZ && b.HasZ ? a.Z + (b.Z - a.Z) * t : (t < 0.5 ? (a.HasZ ? a.Z : double.NaN) : (b.HasZ ? b.Z : double.NaN));
                r.Add(new P(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, z));
            }
            r[n - 1] = pts[pts.Count - 1];
            return r;
        }

        // Douglas-Peucker on the plane: every left-out point lies within `tol` yards of the line that is kept; the ends always stay
        public static List<P> Simplify(IList<P> pts, double tol)
        {
            if (pts == null) return new List<P>();
            if (pts.Count <= 2) return pts.ToList();
            var keep = new bool[pts.Count]; keep[0] = keep[pts.Count - 1] = true;
            var stack = new Stack<Tuple<int, int>>(); stack.Push(Tuple.Create(0, pts.Count - 1));
            while (stack.Count > 0)
            {
                var s = stack.Pop(); int a = s.Item1, b = s.Item2;
                if (b - a < 2) continue;
                double best = -1; int bi = -1;
                for (int i = a + 1; i < b; i++) { double d = SegDist(pts[i].X, pts[i].Y, pts[a], pts[b]); if (d > best) { best = d; bi = i; } }
                if (best > tol) { keep[bi] = true; stack.Push(Tuple.Create(a, bi)); stack.Push(Tuple.Create(bi, b)); }
            }
            var r = new List<P>();
            for (int i = 0; i < pts.Count; i++) if (keep[i]) r.Add(pts[i]);
            return r;
        }

        // simplified until it has at most `maxPts` points (the tolerance grows by half each round)
        public static List<P> SimplifyCapped(IList<P> pts, double tol, int maxPts)
        {
            var r = Simplify(pts, tol);
            for (int k = 0; k < 30 && r.Count > maxPts; k++) { tol *= 1.5; r = Simplify(pts, tol); }
            return r;
        }

        public static double SegDist(double x, double y, P a, P b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y, l2 = dx * dx + dy * dy;
            double t = l2 <= 1e-12 ? 0 : Math.Max(0, Math.Min(1, ((x - a.X) * dx + (y - a.Y) * dy) / l2));
            return Dist(x, y, a.X + dx * t, a.Y + dy * t);
        }

        public static double DistToLine(double x, double y, IList<P> line)
        {
            if (line == null || line.Count == 0) return double.MaxValue;
            if (line.Count == 1) return Dist(x, y, line[0].X, line[0].Y);
            double best = double.MaxValue;
            for (int i = 0; i + 1 < line.Count; i++) { double d = SegDist(x, y, line[i], line[i + 1]); if (d < best) best = d; }
            return best;
        }

        // both ways, so a shortcut counts as much as a detour: each line resampled to n points, each point's distance to the other line
        public static Dev Deviation(IList<P> a, IList<P> b, int n = 64)
        {
            if (a == null || b == null || a.Count < 2 || b.Count < 2) return new Dev { Mean = double.MaxValue, Max = double.MaxValue };
            var ra = Resample(a, n); var rb = Resample(b, n);
            double sum = 0, max = 0; int cnt = 0;
            foreach (var p in ra) { double d = DistToLine(p.X, p.Y, b); sum += d; cnt++; if (d > max) max = d; }
            foreach (var p in rb) { double d = DistToLine(p.X, p.Y, a); sum += d; cnt++; if (d > max) max = d; }
            return new Dev { Mean = sum / Math.Max(1, cnt), Max = max };
        }

        // the point-wise average of lines that run the same way (each resampled to n points); the height averages the lines that have one there
        public static List<P> Average(IList<IList<P>> lines, int n = 64)
        {
            var rs = lines.Where(l => l != null && l.Count >= 2).Select(l => Resample(l, n)).ToList();
            var r = new List<P>();
            if (rs.Count == 0) return r;
            for (int k = 0; k < n; k++)
            {
                double x = 0, y = 0, z = 0; int nz = 0;
                foreach (var l in rs) { x += l[k].X; y += l[k].Y; if (l[k].HasZ) { z += l[k].Z; nz++; } }
                r.Add(new P(x / rs.Count, y / rs.Count, nz > 0 ? z / nz : double.NaN));
            }
            return r;
        }

        public static List<P> Reversed(IList<P> pts) { var r = pts.ToList(); r.Reverse(); return r; }

        public static List<P> OfRoute(TaxiRoute r)
        {
            var l = new List<P>();
            if (r == null) return l;
            for (int i = 0; i < r.X.Length; i++) l.Add(new P(r.X[i], r.Y[i], r.Z[i]));
            return l;
        }
    }
}
