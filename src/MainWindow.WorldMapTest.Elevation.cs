using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Maps;
using ElansAddonHub.Services.WorldMap;

namespace ElansAddonHub
{
    // --selftest "worldmap" (2.82): the height of the live dot and trail in 3D (LiveElevation). Pure checks on an invented world: a flight matched to its route
    // (take-off, a multi-stop flight, joining mid-flight, no route), floors by continuity indoors, a bridge against walking under it, swimming, a teleport that
    // snaps, the smoothing; the buildings' walkable surfaces (rotation, walls left out); the "me" message's heights; and the 3D page's dot following the solved height.
    public partial class MainWindow
    {
        // an invented continent for the solver: terrain, water and floors as functions of world x / y, and a few flight routes
        sealed class FixtureWorld : IElevationWorld
        {
            public Func<double, double, double> G = (x, y) => 10, W = (x, y) => double.NaN;
            public Func<double, double, IEnumerable<double>> F = (x, y) => new double[0];
            public List<TaxiRoute> R = new List<TaxiRoute>();
            public int FloorCalls;
            public double Ground(int m, double x, double y) => G(x, y);
            public double Water(int m, double x, double y) => W(x, y);
            public void Floors(int m, double x, double y, List<double> into) { FloorCalls++; into.AddRange(F(x, y)); }
            public IReadOnlyList<TaxiRoute> Routes(int m) => R;
        }
        static TaxiRoute Route(int id, int from, int to, params double[] xyz)
        {
            var pts = new List<double[]>(); for (int i = 0; i + 2 < xyz.Length; i += 3) pts.Add(new[] { xyz[i], xyz[i + 1], xyz[i + 2] });
            return TaxiRoute.Make(id, from, to, 1, pts);
        }
        static string F1(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);

        void WorldMapLiveElevationUnitTest(CheckFn Check)
        {
            var t0 = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
            // ---- the flight routes: A (0, 0) north to B (3000, 0) climbing over a ridge; A west to C; B on north-east to D
            var w = new FixtureWorld();
            var rAB = Route(101, 1, 2, 0, 0, 20, 1000, 0, 160, 2000, 0, 220, 3000, 0, 30);
            var rAC = Route(102, 1, 3, 0, 0, 20, 0, 1500, 90, 0, 3000, 25);
            var rBD = Route(103, 2, 4, 3000, 0, 30, 3800, 800, 140, 4600, 1600, 35);
            var rBA = Route(104, 2, 1, 3000, 0, 30, 2000, 0, 200, 1000, 0, 150, 0, 0, 20);
            w.R.AddRange(new[] { rAB, rAC, rBD, rBA });
            Check("taxi route: the height along a route is interpolated between its 3D points (half way from 160 to 220 is 190)", Math.Abs(rAB.Project(1500, 3, out var s1, out var z1) - 3) < 1e-6 && Math.Abs(z1 - 190) < 1e-6 && Math.Abs(s1 - 1500) < 1e-6, F1(z1));

            var e = new LiveElevation(w);
            var at = t0; var rnd = new Random(7);
            e.Solve(1, 5, 5, 0, at);                                            // standing at the flight master
            var errs = new List<double>(); var fails = new List<string>(); int routeOk = 0, n = 0; double minLift = 1e9;
            for (double x = 60; x <= 2950; x += 90)
            {
                at = at.AddSeconds(3); double y = (rnd.NextDouble() - 0.5) * 16;
                var r = e.Solve(1, x, y, LiveMove.Taxi, at); n++;
                rAB.Project(x, 0, out _, out var want);
                errs.Add(Math.Abs(r.Z - want)); if (r.RouteId == 101) routeOk++; else fails.Add(F1(x) + ":" + r.RouteId + " " + r.Why);
                if (r.Src != ElevSrc.Air) fails.Add(F1(x) + " src " + r.Src);
                if (x > 900 && x < 2400) minLift = Math.Min(minLift, r.Z - 10);
            }
            Check("flight: taking off at A the flight is matched to the route A -> B (not A -> C or B -> A) on " + routeOk + " of " + n + " points, the height follows its 3D points (worst " + F1(errs.Max()) + " yards off), up in the air over the ridge (at least " + F1(minLift) + " yards above the ground)",
                routeOk >= n - 1 && errs.Max() < 3 && minLift > 100, string.Join(" | ", fails.Take(4)));
            // a multi-stop flight: on past B to D
            var errs2 = new List<double>(); int onBD = 0, n2 = 0;
            for (double f = 0.05; f <= 0.95; f += 0.05)
            {
                at = at.AddSeconds(3); double x = 3000 + 1600 * f, y = 1600 * f;
                var r = e.Solve(1, x, y, LiveMove.Taxi, at); n2++;
                rBD.Project(x, y, out _, out var want); errs2.Add(Math.Abs(r.Z - want)); if (r.RouteId == 103) onBD++;
            }
            Check("flight: a flight that goes on from B continues on B's route to D (" + onBD + " of " + n2 + " points on it, worst " + F1(errs2.Max()) + " yards off its heights)", onBD >= n2 - 1 && errs2.Max() < 3, F1(errs2.Max()));
            // landing at D: back on the ground at once
            at = at.AddSeconds(3); var land = e.Solve(1, 4600, 1600, 0, at);
            Check("flight: landing puts you back on the ground at once (" + F1(land.Z) + ", " + land.Src + ")", land.Src == ElevSrc.Terrain && Math.Abs(land.Z - 10) < 1e-6 && land.Snapped);
            // the Outpost starts during a flight (no take-off seen): the route is found from the points themselves
            var e2 = new LiveElevation(w); at = t0;
            var mids = new List<ElevResult>();
            for (double y = 900; y <= 2100; y += 100) { at = at.AddSeconds(3); mids.Add(e2.Solve(1, 4, y, LiveMove.Taxi, at)); }
            rAC.Project(4, 2100, out _, out var wantC);
            Check("flight: joining in the middle of a flight (the Outpost started late) still finds the route A -> C and its height (" + F1(mids.Last().Z) + " vs " + F1(wantC) + ")", mids.Skip(2).All(m => m.RouteId == 102) && Math.Abs(mids.Last().Z - wantC) < 3, string.Join(",", mids.Select(m => m.RouteId)));
            // no route fits (an unknown flight): above the ground, lifted, not on it
            var e3 = new LiveElevation(w); var nf = e3.Solve(1, 9000, 9000, LiveMove.Taxi, t0);
            Check("flight: with no route near, the dot flies a little above the ground (" + F1(nf.Z) + ", " + nf.Why + ")", nf.Src == ElevSrc.Air && Math.Abs(nf.Z - (10 + LiveElevation.NoRouteLift)) < 1e-6 && nf.RouteId == 0);

            // ---- a two-storey house at x 100..140, y 0..40 on ground 10: ground floor 10.5, upper floor 16, roof 22; a ramp (the stairs) at x 120..130 from 10.5 to 16
            var wh = new FixtureWorld();
            wh.F = (x, y) =>
            {
                if (x < 100 || x > 140 || y < 0 || y > 40) return new double[0];
                var l = new List<double> { 22 };
                if (x >= 120 && x <= 130 && y >= 10 && y <= 20) l.Add(10.5 + (x - 120) * 0.55);
                else { l.Add(10.5); if (x > 130 || x < 120 || y > 20) l.Add(16); }
                return l;
            };
            var eh = new LiveElevation(wh); at = t0;
            eh.Solve(1, 90, 15, 0, at);
            at = at.AddSeconds(1); var inG = eh.Solve(1, 104, 15, LiveMove.Indoors, at);
            Check("indoors: walking in from the street you are on the ground floor (" + F1(inG.Z) + "), not on the upper floor or the roof", inG.Src == ElevSrc.Floor && Math.Abs(inG.Z - 10.5) < 0.3, inG.Why);
            var climb = new List<double>();
            for (double x = 108; x <= 131; x += 3) { at = at.AddSeconds(1); climb.Add(eh.Solve(1, x, 15, LiveMove.Indoors, at).Z); }
            for (int i = 0; i < 4; i++) { at = at.AddSeconds(1); climb.Add(eh.Solve(1, 136, 15 + i * 4, LiveMove.Indoors, at).Z); }
            Check("indoors: up the stairs the dot climbs with you and stays on the upper floor at the top (" + string.Join(" ", climb.Select(F1)) + ")", Math.Abs(climb.Last() - 16) < 0.6 && climb.First() < 12, string.Join(",", climb.Select(F1)));
            // the same walk, but staying downstairs (round the stairs): continuity keeps the ground floor
            var eh2 = new LiveElevation(wh); at = t0;
            eh2.Solve(1, 90, 30, 0, at); var down = new List<double>();
            for (double x = 104; x <= 138; x += 3) { at = at.AddSeconds(1); down.Add(eh2.Solve(1, x, 30, LiveMove.Indoors, at).Z); }
            Check("indoors: walking through downstairs keeps the ground floor, even where an upper floor is over you (" + string.Join(" ", down.Select(F1)) + ")", down.All(z => Math.Abs(z - 10.5) < 0.6));
            // after a teleport (a hearthstone into the inn): no continuity - the lowest floor above the ground
            var eh3 = new LiveElevation(wh); var hs = eh3.Solve(1, 136, 30, LiveMove.Indoors, t0);
            Check("indoors from nowhere (a teleport): the lowest floor above the ground (" + F1(hs.Z) + ")", hs.Src == ElevSrc.Floor && Math.Abs(hs.Z - 10.5) < 1e-6, hs.Why);
            var wu = new FixtureWorld { F = (x, y) => new double[] { -40, -28 } };
            var cel = new LiveElevation(wu).Solve(1, 0, 0, LiveMove.Indoors, t0);
            Check("indoors under the ground (a city below the surface): the highest floor below it (" + F1(cel.Z) + ")", cel.Src == ElevSrc.Floor && Math.Abs(cel.Z + 28) < 1e-6, cel.Why);

            // ---- a bridge: deck at 20 over a valley (ground 20 on the banks, down to 4 in the middle at x 120..140), along y (-5..5) from x 100 to 160
            var wb = new FixtureWorld
            {
                G = (x, y) => x < 100 || x > 160 ? 20 : x < 120 ? 20 - (x - 100) * 0.8 : x <= 140 ? 4 : 4 + (x - 140) * 0.8,
                F = (x, y) => x >= 100 && x <= 160 && Math.Abs(y) <= 5 ? new double[] { 20 } : new double[0],
                W = (x, y) => x >= 118 && x <= 142 ? 5.5 : double.NaN,
            };
            var eb = new LiveElevation(wb); at = t0; var over = new List<ElevResult>();
            for (double x = 80; x <= 180; x += 5) { at = at.AddSeconds(1); over.Add(eb.Solve(1, x, 0, 0, at)); }
            Check("bridge: walking over it you stay on the deck (" + string.Join(" ", over.Select(r => F1(r.Z))) + "), above the valley",
                over.All(r => Math.Abs(r.Z - 20) < 0.6) && over.Any(r => r.Src == ElevSrc.Floor), string.Join(",", over.Select(r => F1(r.Z) + r.Src.ToString()[0])));
            var eu = new LiveElevation(wb); at = t0; var under = new List<ElevResult>();
            for (double y = -60; y <= 60; y += 6) { at = at.AddSeconds(1); under.Add(eu.Solve(1, 130, y, 0, at)); }
            Check("bridge: walking along the valley under it you stay on the ground (" + string.Join(" ", under.Select(r => F1(r.Z))) + "), not up on the deck",
                under.All(r => Math.Abs(r.Z - 4) < 1e-6 && r.Src == ElevSrc.Terrain));
            // swimming in the river under the bridge: the water surface; swimming where no water is known: the ground
            var sw1 = eu.Solve(1, 130, 66, LiveMove.Swimming, at.AddSeconds(1));
            var sw2 = new LiveElevation(wb).Solve(1, 300, 0, LiveMove.Swimming, t0);
            Check("swimming: on the water surface where the map has one (" + F1(sw1.Z) + ", " + sw1.Src + "), on the ground where it has none (" + F1(sw2.Z) + ")",
                sw1.Src == ElevSrc.Water && Math.Abs(sw1.Z - 5.5) < 1e-6 && sw2.Src == ElevSrc.Terrain && Math.Abs(sw2.Z - 20) < 1e-6);

            // ---- smoothing and the teleport snap: two floors 0.8 apart that the continuity picks in turn (a sloped deck read at two points) are eased, a teleport is not
            var ws = new FixtureWorld { G = (x, y) => 0, F = (x, y) => new double[] { ((int)(x / 4)) % 2 == 0 ? 30.0 : 30.8 } };
            var es = new LiveElevation(ws); at = t0; es.Solve(1, 0, 0, LiveMove.Indoors, at);
            var raws = new List<double>(); var outs = new List<double>();
            for (int i = 1; i <= 12; i++) { at = at.AddSeconds(1); double x = i * 4; var r = es.Solve(1, x, 0, LiveMove.Indoors, at); outs.Add(r.Z); raws.Add(((int)(x / 4)) % 2 == 0 ? 30.0 : 30.8); }
            double rawJ = raws.Zip(raws.Skip(1), (a, b) => Math.Abs(a - b)).Max(), outJ = outs.Zip(outs.Skip(1), (a, b) => Math.Abs(a - b)).Max();
            Check("smoothing: a floor that flips 0.8 yards between two readings moves the dot less (" + F1(outJ) + " per step instead of " + F1(rawJ) + ") and stays between them",
                outJ < rawJ * 0.8 && outs.All(z => z >= 29.99 && z <= 30.81), string.Join(",", outs.Select(F1)));
            at = at.AddSeconds(1); var tp = es.Solve(1, 5000, 5000, 0, at);
            at = at.AddSeconds(1); var tp2 = es.Solve(1, 5004, 5000, 0, at);
            Check("teleport: a jump of 7000 yards in a second snaps (no easing from the old height: " + F1(tp.Z) + "), and the next step goes on from there",
                tp.Snapped && Math.Abs(tp.Z - 0) < 1e-6 && tp.Src == ElevSrc.Terrain && Math.Abs(tp2.Z) < 1e-6);
            var wf = new FixtureWorld { G = (x, y) => 0, F = (x, y) => new double[] { 30 } };
            var ef = new LiveElevation(wf); ef.Solve(1, 0, 0, LiveMove.Indoors, t0);
            var ef2 = ef.Solve(1, 0, 0, LiveMove.Indoors, t0.AddMinutes(5));
            Check("teleport: a long pause (5 minutes, a loading screen) also starts afresh", ef2.Snapped);

            // ---- the buildings' walkable surfaces: a square floor (20 x 20) and a deck 5 higher, turned 45 degrees, with a wall that never counts
            var model = new WmoModel { Center = new float[] { 0, 0, 0 }, Half = new float[] { 10, 10, 10 } };
            short Q(double v) => (short)Math.Round(v / 10 * 32767);
            var pos = new List<short>(); var idx = new List<int>();
            void Quad(double y, double s0, double s1, int k) { foreach (var (a, b) in new[] { (s0, s0), (s1, s0), (s1, s1), (s0, s1) }) { pos.Add(Q(a)); pos.Add(Q(y)); pos.Add(Q(b)); } idx.AddRange(new[] { k, k + 1, k + 2, k, k + 2, k + 3 }); }
            Quad(0, -10, 10, 0); Quad(5, -4, 4, 4);
            foreach (var (a, b, c) in new[] { (-10.0, 0.0, -10.0), (10, 0, -10), (10, 9, -10) }) { pos.Add(Q(a)); pos.Add(Q(b)); pos.Add(Q(c)); }
            idx.AddRange(new[] { 8, 9, 10 });
            model.Lods.Add(new WmoLod { Pos = pos.ToArray(), Colors = new byte[pos.Count], Indices = idx.ToArray() });
            var place = new WmoPlacement { NameId = 777, X = 1000, Y = 50, Z = 2000, RotY = 45, Scale = 1, MinX = 985, MaxX = 1015, MinY = 49, MaxY = 60, MinZ = 1985, MaxZ = 2015 };
            var bf = new BuildingFloors(new[] { place }, fd => fd == 777 ? model : null);
            var hc = new List<double>(); bf.At(1000, 2000, hc);
            var he = new List<double>(); bf.At(1013, 2000, he);
            var ho = new List<double>(); bf.At(1011, 2011, ho);
            var hw = new List<double>(); bf.At(1000, 1990.5, hw);
            Check("building floors: in the middle the floor (50) and the deck above it (55); turned 45 degrees the square reaches 14 yards along x but not its old corner; the wall is no floor (" + string.Join("/", hc.Select(F1)) + " | " + string.Join("/", he.Select(F1)) + " | " + ho.Count + " | " + string.Join("/", hw.Select(F1)) + ")",
                hc.Count == 2 && hc.Any(h => Math.Abs(h - 50) < 0.01) && hc.Any(h => Math.Abs(h - 55) < 0.01) && he.Count == 1 && Math.Abs(he[0] - 50) < 0.01 && ho.Count == 0 && hw.Count == 1 && Math.Abs(hw[0] - 50) < 0.01);
            var bfMiss = new BuildingFloors(new[] { place }, fd => null); var hm = new List<double>(); bfMiss.At(1000, 2000, hm);
            Check("building floors: a building whose shape was not made has no floors (the ground is used)", hm.Count == 0);
            double sx, sz; GameElevationWorld.ToScene(1677.6, -4315.7, out sx, out sz);
            Check("building floors: world -> scene as the page has it (x = 32 x 533.33 - world y, z = 32 x 533.33 - world x)", Math.Abs(sx - (17066.667 + 4315.7)) < 0.01 && Math.Abs(sz - (17066.667 - 1677.6)) < 0.01);

            // ---- the live layer: the strip's bits reach the solver; the trail and the message carry the heights
            var lp = new LivePosition { ToWorld = (ui, fx, fy) => ui == 11 ? new WorldPoint(1, fy * 3000, fx * 10) : (WorldPoint?)null, ElevationWorld = w };
            var t1 = DateTime.UtcNow.AddSeconds(-200);
            GamePresence.Character Ch(double fy, int move, bool known = true) { var c = LiveChar(fx: 0.5, fy: fy); c.PosMove = move; c.PosMoveKnown = known; return c; }
            lp.Update(true, true, Ch(0.001, 0), t1);
            for (int i = 1; i <= 30; i++) lp.Update(true, true, Ch(0.001 + i * 0.03, LiveMove.Taxi), t1.AddSeconds(i * 3));
            var pts = lp.Trail.Points(DateTime.UtcNow);
            var air = pts.Where(p => p.HasZ && p.Src == ElevSrc.Air).ToList();
            rAB.Project(lp.Here.Value.X, lp.Here.Value.Y, out _, out var hereWant);
            Check("live: on a flight the trail points carry the route's height (" + air.Count + " of " + pts.Count + " in the air; here " + F1(lp.Here.Value.Z) + " vs the route's " + F1(hereWant) + ")",
                air.Count >= 28 && lp.Here.Value.HasZ && lp.Here.Value.Src == ElevSrc.Air && Math.Abs(lp.Here.Value.Z - hereWant) < 3, lp.LastElevForTest?.Why);
            var lpOld = new LivePosition { ToWorld = lp.ToWorld, ElevationWorld = w };
            lpOld.Update(true, true, Ch(0.001, 0, false), t1);
            for (int i = 1; i <= 10; i++) lpOld.Update(true, true, Ch(0.001 + i * 0.03, LiveMove.Taxi, false), t1.AddSeconds(i * 3));
            Check("live: from a Hub that does not send how you move (before 1.16.1) the dot stays on the ground as before", lpOld.Trail.Points(DateTime.UtcNow).All(p => p.Src == ElevSrc.Terrain));
            var me = Json.Obj(WorldMapProtocol.Me(lp.State, lp.Name, "#ABD473", lp.Here, pts, 1, DateTime.UtcNow));
            var tr = me["trail"] as List<object>; var el = me["elev"] as List<object>; var hz = me["hz"] as List<object>;
            Check("protocol: me carries one height pair per trail point (elev), the dot's (hz), in yards with the source (3 = in the air)",
                tr != null && el != null && el.Count * 2 == tr.Count && hz != null && hz.Count == 2 && Convert.ToInt32(hz[1]) == 3 && Math.Abs(Convert.ToDouble(hz[0]) - lp.Here.Value.Z) < 0.06 && Convert.ToInt32(el[el.Count - 1]) == 3,
                (el?.Count ?? -1) + " / " + (tr?.Count ?? -1));
            var bad = new List<LiveTrail.Point> { new LiveTrail.Point { At = DateTime.UtcNow, MapId = 0, X = 500, Y = 1000 }, new LiveTrail.Point { At = DateTime.UtcNow, MapId = 0, X = 510, Y = 1000, HasZ = true, Z = double.NaN, Src = ElevSrc.Air } };
            var meBad = Json.Obj(WorldMapProtocol.Me(LiveState.Ok, "Elan", "#ABD473", bad[1], bad, 0, DateTime.UtcNow));
            var elBad = meBad["elev"] as List<object>;
            Check("protocol: a point without a height (or a NaN) says so (source -1), it never sends a broken number", elBad != null && elBad.Count == 4 && Convert.ToInt32(elBad[1]) == -1 && Convert.ToInt32(elBad[3]) == -1);
            lp.Update(false, true, Ch(0.5, 0), DateTime.UtcNow);
        }

        // a picture of the 3D page from the side: the camera aimed at world x / y from `dist` yards, bearing az, tilt degrees over the horizon
        async Task Shot3d(WorldMapView view, string file, double wx, double wy, double dist, double az, double tilt)
        {
            WorldMapMath.WorldToLatLng(wx, wy, out var la, out var ln);
            await WmEval(string.Format(CultureInfo.InvariantCulture, "window.wm3.setView({0},{1},{2},{3},{4})", la, ln, dist, az, tilt));
            await Task.Delay(1200);
            var png = await view.CaptureForTest(); if (png != null) File.WriteAllBytes(file, png);
        }

        // the 3D page: the dot and the trail follow the solved height (a flight over the invented continent); runs from WorldMapLive3dTest
        async Task WorldMapLiveElevation3dTest(CheckFn Check, Func<string, string> P, LivePosition lp, WorldMapView view)
        {
            var savedWorld = lp.ElevationWorld;
            try
            {
                // a route straight across the map the live tests walk on, 180 yards up in the middle
                var ow = lp.ToWorld;
                WorldPoint? W(double fx, double fy) { try { return MapData.Index?.MapToWorld(11, fx, fy); } catch (Exception) { return null; } }
                var a = W(0.40, 0.50); var b = W(0.70, 0.50);
                if (a == null || b == null) { Check("3D height: the invented continent has a place for the test flight", false); return; }
                var fw = new FixtureWorld { G = (x, y) => double.NaN };
                var mid = new[] { (a.Value.X + b.Value.X) / 2, (a.Value.Y + b.Value.Y) / 2 };
                // heights over the page's own ground: 15 yards at the flight masters, a climb of a fifth of the distance (at least 40 yards) in the middle
                async Task<double> PageGround(double wx, double wy) { WorldMapMath.WorldToLatLng(wx, wy, out var la, out var ln); var r = await WmEval(string.Format(CultureInfo.InvariantCulture, "String(window.wm3.ground({0},{1}))", la, ln)); return double.TryParse(Json.Parse(r ?? "null") as string ?? r, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0; }
                double ga = await PageGround(a.Value.X, a.Value.Y), gm = await PageGround(mid[0], mid[1]), gb = await PageGround(b.Value.X, b.Value.Y);
                double lift = Math.Max(40, LiveTrail.Dist(a.Value.X, a.Value.Y, b.Value.X, b.Value.Y) / 5);
                fw.R.Add(Route(901, 1, 2, a.Value.X, a.Value.Y, ga + 15, mid[0], mid[1], Math.Max(ga, Math.Max(gm, gb)) + lift, b.Value.X, b.Value.Y, gb + 15));
                lp.ElevationWorld = fw; lp.Trail.Clear();             // a fresh trail (the walk before ended a moment ago: these points are older)
                var start = DateTime.UtcNow.AddSeconds(-90);
                lp.Update(true, true, LiveChar(fx: 0.40, fy: 0.50), start);
                for (int i = 1; i <= 15; i++) { var c = LiveChar(fx: 0.40 + i * 0.01, fy: 0.50); c.PosMove = LiveMove.Taxi; c.PosMoveKnown = true; lp.Update(true, true, c, start.AddSeconds(i * 3)); }
                view.PushLive(true);
                double want = lp.Here?.Z ?? double.NaN;
                var m = await Wm3MeWait(x => x.Str("state") == "ok" && x["dotZ"] is double, 6000);
                string T(Dictionary<string, object> d) => d == null ? "null" : string.Join(" ", d.Select(kv => kv.Key + "=" + (kv.Value is List<object> l ? "[" + string.Join(",", l) + "]" : kv.Value)));
                double dotY = m != null && m["dotY"] is double dy ? dy : double.NaN, gnd = m != null && m["dotGround"] is double dg ? dg : double.NaN, ex = m != null && m["exag"] is double de ? de : 1;
                double rib = m != null && m["ribbonMaxLift"] is double rl ? rl : double.NaN;
                var th = (m?["trailH"] as List<object>)?.Select(o => o is double dd ? dd : double.NaN).ToList() ?? new List<double>();
                Check("3D height: on a flight the dot is drawn at the solved height (" + F1(dotY) + " scene yards = " + F1(want) + " x " + F1(ex) + "), high above the ground under it (" + F1(gnd) + "), and the trail climbs with it (up to " + F1(rib) + " yards over the ground)",
                    m != null && Math.Abs(dotY - want * ex) < 0.6 && dotY - gnd > lift * 0.4 && rib > lift * 0.4 && th.Count >= 10 && th.Count(h => !double.IsNaN(h)) >= th.Count - 1, T(m) + " lift " + F1(lift));
                if (SelfTestClock.ShotsOn) await Shot3d(view, P("worldmap-live-3d-flight.png"), (a.Value.X + mid[0]) / 2, (a.Value.Y + mid[1]) / 2, 650, 300, 6);
                // landing (no more flight): the dot is on the ground again
                lp.Update(true, true, LiveChar(fx: 0.56, fy: 0.50), start.AddSeconds(50)); view.PushLive(true);
                var m2 = await Wm3MeWait(x => x.Str("state") == "ok" && !(x["dotZ"] is double), 6000);
                double dotY2 = m2 != null && m2["dotY"] is double d2 ? d2 : double.NaN, gnd2 = m2 != null && m2["dotGround"] is double g2 ? g2 : double.NaN;
                Check("3D height: landed, the dot stands on the ground again (" + F1(dotY2) + " vs ground " + F1(gnd2) + ")", m2 != null && Math.Abs(dotY2 - gnd2) < 0.6, T(m2));
                // indoors (entering after a hearthstone: no step before it): the dot stands on the building's lowest floor (a raised hall 6 yards over the page's ground there)
                double gp = double.IsNaN(gnd2) ? 0 : gnd2 / ex;
                var hw = new FixtureWorld { G = (x, y) => double.NaN, F = (x, y) => new double[] { gp + 6, gp + 14 } };
                lp.ElevationWorld = hw; lp.Trail.Clear();
                var ci = LiveChar(fx: 0.56, fy: 0.50); ci.PosMove = LiveMove.Indoors; ci.PosMoveKnown = true;
                lp.Update(true, true, ci, DateTime.UtcNow);
                view.PushLive(true);
                double wantF = lp.Here?.Z ?? double.NaN;
                var m3 = await Wm3MeWait(x => x.Str("state") == "ok" && x["dotZ"] is double dz && Math.Abs(dz - wantF) < 0.06, 6000);
                double dotY3 = m3 != null && m3["dotY"] is double d3 ? d3 : double.NaN;
                Check("3D height: indoors the dot stands on the floor of the building (" + F1(wantF) + " yards, drawn at " + F1(dotY3) + "), not on the ground", m3 != null && Math.Abs(wantF - (gp + 6)) < 0.06 && Math.Abs(dotY3 - wantF * ex) < 0.6, T(m3));
                if (SelfTestClock.ShotsOn && lp.Here.HasValue)
                {
                    // the picture: walking on from there up a ramp (half a yard up per yard) to the upper floor, seen from the side
                    var p0 = lp.Here.Value; var tw = DateTime.UtcNow;
                    hw.F = (x, y) => new[] { gp + 6 + Math.Min(8, LiveTrail.Dist(p0.X, p0.Y, x, y) * 0.5), gp + 14 };
                    for (int k = 1; k <= 14; k++) { var ck = LiveChar(fx: 0.56 + k * 0.002, fy: 0.50); ck.PosMove = LiveMove.Indoors; ck.PosMoveKnown = true; lp.Update(true, true, ck, tw.AddSeconds(k * 2)); }
                    view.PushLive(true); await Task.Delay(500);
                    var ph = lp.Here.Value;
                    await Shot3d(view, P("worldmap-live-3d-indoors.png"), (p0.X + ph.X) / 2, (p0.Y + ph.Y) / 2, Math.Max(100, LiveTrail.Dist(p0.X, p0.Y, ph.X, ph.Y) * 2.4), 300, 9);
                }
                lp.ToWorld = ow;
            }
            finally { lp.ElevationWorld = savedWorld; lp.Update(true, true, LiveChar(fx: 0.5, fy: 0.5), DateTime.UtcNow); }
        }
    }
}
