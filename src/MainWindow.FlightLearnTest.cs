using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Maps;
using ElansAddonHub.Services.WorldMap;

namespace ElansAddonHub
{
    // --selftest "flightlearn" (2.88): learned flight paths. Everything on an invented continent and synthetic Lodge frames (no game, no server):
    //   the line geometry (resampling, Douglas-Peucker, the deviation both ways), the recorder (a synthetic flight from take-off to landing, a multi-hop flight cut
    //   at the master it passes, a direct route kept whole, glitches: a pause, a jump, a start in mid-air, another continent, a broken flight), the rules (one flight
    //   is not enough, two agreeing deviating flights are, disagreeing or unclear ones are not, a newer flight on the game's route reverts it, a leg the files lack),
    //   the file (round trip, what is in it, a damaged one, Clear), the Flight routes layer (learned line drawn instead of the game's, styled and labelled, the
    //   Lodge's), the elevation solver flying the learned line, the live position recording a flight end to end, and the Lodge round trip (feature gating, the
    //   upload only with the flight-time switch on, guests, the answer validated). The world map page parts (2D / 3D pictures) run in "worldmap", the real
    //   Crossroads -> Ratchet check in "worldmapreal".
    public partial class MainWindow
    {
        // the invented continent 1: A (0, 0), B (3000, 0) north of it, C (3000, 3000) west of B, D far away
        static TaxiNodeInfo FlNode(int id, string name, double x, double y) => new TaxiNodeInfo { Id = id, MapId = 1, Name = name, X = x, Y = y, Flags = 3 };
        static readonly TaxiNodeInfo[] FlMasters = { FlNode(101, "Alpha, Testland", 0, 0), FlNode(102, "Bravo, Testland", 3000, 0), FlNode(103, "Charlie, Testland", 3000, 3000), FlNode(104, "Delta, Testland", 9000, 9000) };
        static TaxiRoute FlRoute(int id, int from, int to, params double[] xyz)
        {
            var pts = new List<double[]>(); for (int i = 0; i + 2 < xyz.Length; i += 3) pts.Add(new[] { xyz[i], xyz[i + 1], xyz[i + 2] });
            return TaxiRoute.Make(id, from, to, 1, pts);
        }
        static readonly List<TaxiRoute> FlGame = new List<TaxiRoute>
        {
            FlRoute(901, 101, 102, 0, 0, 40, 1500, 0, 120, 3000, 0, 40),
            FlRoute(902, 102, 103, 3000, 0, 40, 3000, 1500, 110, 3000, 3000, 40),
            FlRoute(903, 102, 101, 3000, 0, 40, 1500, 0, 120, 0, 0, 40),
        };

        // points along a polyline at `speed` yards a second, one a second, with a little jitter (the strip's map fractions are not exact)
        static List<double[]> FlPath(double speed, double jitter, int seed, params double[] xy)
        {
            var line = new List<FlightGeo.P>(); for (int i = 0; i + 1 < xy.Length; i += 2) line.Add(new FlightGeo.P(xy[i], xy[i + 1]));
            double len = FlightGeo.Length(line); int n = Math.Max(2, (int)Math.Ceiling(len / speed) + 1);
            var rs = FlightGeo.Resample(line, n); var rnd = new Random(seed);
            return rs.Select((p, i) => new[] { p.X + (i == 0 || i == n - 1 ? 0 : (rnd.NextDouble() - 0.5) * 2 * jitter), p.Y + (i == 0 || i == n - 1 ? 0 : (rnd.NextDouble() - 0.5) * 2 * jitter) }).ToList();
        }

        // flies a path with a recorder: a moment on the ground at the start, the taxi samples, a sample on the ground at the end. Returns the legs (or null).
        static List<ObservedFlight> FlFly(FlightRecorder rec, List<double[]> path, DateTime t0, double z = double.NaN, bool groundStart = true, Func<int, double[], double[]> tweak = null, int map = 1)
        {
            var at = t0;
            if (groundStart) rec.Sample(map, path[0][0] + 3, path[0][1] + 2, double.NaN, false, at);
            List<ObservedFlight> res = null;
            for (int i = 0; i < path.Count; i++)
            {
                at = at.AddSeconds(1);
                var p = tweak != null ? tweak(i, path[i]) : path[i];
                if (p == null) continue;
                int m = p.Length > 2 ? (int)p[2] : map;
                res = rec.Sample(m, p[0], p[1], z, true, at) ?? res;
            }
            at = at.AddSeconds(1);
            return rec.Sample(map, path[path.Count - 1][0] + 2, path[path.Count - 1][1] - 2, double.NaN, false, at) ?? res;
        }

        static FlightRecorder FlRecorder(List<TaxiRoute> routes = null) => new FlightRecorder { Masters = m => m == 1 ? FlMasters : new TaxiNodeInfo[0], Routes = m => m == 1 ? (IReadOnlyList<TaxiRoute>)(routes ?? FlGame) : new TaxiRoute[0], Build = () => "1.60.1.70374" };

        static LearnedFlights FlStore(string file, List<TaxiRoute> routes = null) => new LearnedFlights(file)
        {
            GameRoutes = m => m == 1 ? (IReadOnlyList<TaxiRoute>)(routes ?? FlGame) : new TaxiRoute[0],
            Node = (m, id) => m == 1 ? FlMasters.FirstOrDefault(n => n.Id == id) : null,
        };

        // an A -> B flight that bends `bend` yards west of the straight line in the middle (Forever's line), as legs the recorder made
        static ObservedFlight FlBent(FlightRecorder rec, double bend, int seed, DateTime t0, double z = double.NaN) =>
            FlFly(rec, FlPath(32, 4, seed, 0, 0, 1500, bend, 3000, 0), t0, z)?.FirstOrDefault();

        async Task<string> FlightLearnTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            void Check(string what, bool cond, string detail = null) { SelfTestClock.Mark(what); notes.Add((cond ? "ok   " : "FAIL ") + what + (!cond && detail != null ? " [" + detail + "]" : "")); ok &= cond; Util.Log("flightlearn test " + (cond ? "ok " : "FAIL ") + what.Substring(0, Math.Min(80, what.Length))); }
            var work = Path.Combine(dir, "flightlearn");
            var savedShared = LearnedFlights.Shared; var savedHook = WorldMapLayers.Learned; var savedIdx = MapData.Index; var savedState = MapData.Status;
            try
            {
                if (Directory.Exists(work)) Util.DeleteDir(work);
                Directory.CreateDirectory(work);
                Check("fixture: the learned-flights files of this test are under the scratch folder", Path.GetFullPath(work).StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase));
                LearnedFlights.Shared = FlStore(Path.Combine(work, "shared.json"));     // nothing another section does reaches the app's own file
                FlGeoChecks(Check);
                FlRecorderChecks(Check);
                FlRuleChecks(Check, work);
                FlFileChecks(Check, work);
                FlLayerChecks(Check, work);
                FlElevationChecks(Check, work);
                FlLiveChecks(Check, work);
                await FlLodgeChecks(Check, work);
            }
            catch (Exception e) { ok = false; notes.Add("FAIL flightlearn crashed [" + e + "]"); }
            finally
            {
                LearnedFlights.Shared = savedShared; WorldMapLayers.Learned = savedHook;
                if (savedIdx != null) MapData.SetForTest(savedIdx, "wow_classic_beta|1.60.1.70338|0123456789abcdef", savedState); else MapData.ResetForTest();
            }
            return "flightlearn selftest: " + (ok ? "PASSED" : "FAILED") + "\r\n  " + string.Join("\r\n  ", notes);
        }

        // ---------------------------------------------------------------- geometry
        void FlGeoChecks(CheckFn Check)
        {
            var straight = new List<FlightGeo.P>(); for (int i = 0; i <= 100; i++) straight.Add(new FlightGeo.P(i * 30, (i % 2 == 0 ? 1 : -1) * 2));
            var s1 = FlightGeo.Simplify(straight, 12);
            Check("simplify: a straight flight with 2 yards of jitter (101 points) becomes its two ends", s1.Count == 2 && s1[0].X == 0 && s1[1].X == 3000, s1.Count.ToString());
            var bent = FlPath(32, 0, 1, 0, 0, 1500, 400, 3000, 0).Select(p => new FlightGeo.P(p[0], p[1])).ToList();
            var s2 = FlightGeo.Simplify(bent, 12);
            double worst = bent.Max(p => FlightGeo.DistToLine(p.X, p.Y, s2));
            Check("simplify: a bent flight keeps its corner, every left-out point within 12 yards of the line kept (" + bent.Count + " -> " + s2.Count + " points, worst " + Math.Round(worst, 1) + ")",
                s2.Count >= 3 && s2.Count <= 6 && worst <= 12.0001 && s2.Any(p => Math.Abs(p.X - 1500) < 40 && Math.Abs(p.Y - 400) < 40));
            var curve = new List<FlightGeo.P>(); for (int i = 0; i <= 400; i++) { double a = i / 400.0 * Math.PI * 6; curve.Add(new FlightGeo.P(i * 10, Math.Sin(a) * 500)); }
            var capped = FlightGeo.SimplifyCapped(curve, 2, 64);
            Check("simplify: a long winding flight is capped at 64 points (" + capped.Count + ")", capped.Count <= 64 && capped.Count >= 10);
            var rs = FlightGeo.Resample(new List<FlightGeo.P> { new FlightGeo.P(0, 0, 10), new FlightGeo.P(100, 0, 30), new FlightGeo.P(100, 100) }, 5);
            Check("resample: 5 points at equal distances along the line (0, 50, 100 | 100, 50 | 100, 100), the height interpolated where both ends have one",
                rs.Count == 5 && Math.Abs(rs[1].X - 50) < 1e-6 && Math.Abs(rs[1].Z - 20) < 1e-6 && Math.Abs(rs[2].X - 100) < 1e-6 && Math.Abs(rs[3].Y - 50) < 1e-6 && !rs[3].HasZ && rs[4].Y == 100, string.Join(" ", rs.Select(p => p.X + "," + p.Y)));
            var line = new List<FlightGeo.P> { new FlightGeo.P(0, 0), new FlightGeo.P(3000, 0) };
            var off = new List<FlightGeo.P> { new FlightGeo.P(0, 50), new FlightGeo.P(3000, 50) };
            var d0 = FlightGeo.Deviation(line, line); var d1 = FlightGeo.Deviation(line, off);
            Check("deviation: a line against itself is 0, against a copy 50 yards to the side mean 50 / max 50 (" + d0 + "; " + d1 + ")", d0.Max < 1e-6 && Math.Abs(d1.Mean - 50) < 1e-6 && Math.Abs(d1.Max - 50) < 1e-6);
            // both ways: a line that stops half way is close to the long one one way only - the other way it is 1500 yards short
            var half = new List<FlightGeo.P> { new FlightGeo.P(0, 0), new FlightGeo.P(1500, 0) };
            var dh = FlightGeo.Deviation(half, line);
            Check("deviation: measured both ways, so a line that stops short (or cuts a corner) counts (half a line: max " + Math.Round(dh.Max) + ")", dh.Max > 1400);
            var bentLine = new List<FlightGeo.P> { new FlightGeo.P(0, 0), new FlightGeo.P(1500, 400), new FlightGeo.P(3000, 0) };
            var db = FlightGeo.Deviation(bentLine, line);
            Check("deviation: the 400-yard bend against the straight route (" + db + ") is far over the 'differs' limits (mean 40 or max 100)", db.Max > 390 && db.Mean > LearnedFlights.DevMean);
            var avg = FlightGeo.Average(new List<IList<FlightGeo.P>> { bentLine, new List<FlightGeo.P> { new FlightGeo.P(0, 0, 100), new FlightGeo.P(1500, 300, 100), new FlightGeo.P(3000, 0, 100) } }, 3);
            Check("average: two lines averaged point by point (the middle at y 350), the height from the line that has one", avg.Count == 3 && Math.Abs(avg[1].Y - 350) < 1e-6 && Math.Abs(avg[1].Z - 100) < 1e-6, string.Join(" ", avg.Select(p => p.X + "," + p.Y + "," + p.Z)));
        }

        // ---------------------------------------------------------------- the recorder
        void FlRecorderChecks(CheckFn Check)
        {
            var t0 = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
            var rec = FlRecorder();
            var legs = FlFly(rec, FlPath(32, 4, 1, 0, 0, 1500, 400, 3000, 0), t0, 150);
            var f = legs?.FirstOrDefault();
            Check("record: a flight from A's master to B's (taxi bit on in between) is one leg A > B: " + (f == null ? rec.LastResult : f.Key + ", " + f.Pts.Count + " points, " + f.Seconds + " s, build " + f.Build),
                legs != null && legs.Count == 1 && f.From == 101 && f.To == 102 && f.MapId == 1 && f.Pts.Count >= 3 && f.Pts.Count <= 12 && f.Seconds > 90 && f.Seconds < 110 && f.Build == "1.60.1.70374", rec.LastResult);
            Check("record: it starts where you stood at A's master and ends at B's (the first ground point after landing)", f != null && FlightGeo.Dist(f.Pts[0].X, f.Pts[0].Y, 0, 0) < 10 && FlightGeo.Dist(f.Pts[f.Pts.Count - 1].X, f.Pts[f.Pts.Count - 1].Y, 3000, 0) < 10);
            Check("record: the line keeps the heights the solver had on the flight (150), none for the ground points", f != null && f.Pts.Skip(1).Take(f.Pts.Count - 2).All(p => Math.Abs(p.Z - 150) < 0.01) && !f.Pts[0].HasZ);
            // multi-hop: A > B > C with no direct route A > C: cut at B
            var hop = FlFly(rec, FlPath(32, 4, 2, 0, 0, 3000, 0, 3000, 3000), t0.AddMinutes(10));
            Check("record: a multi-hop flight A > C (no direct route in the game files) is cut at B, the master it passes, into A > B and B > C (" + (hop == null ? rec.LastResult : string.Join(", ", hop.Select(h => h.Key + " " + h.Seconds + " s"))) + ")",
                hop != null && hop.Count == 2 && hop[0].Key == "101>102" && hop[1].Key == "102>103" && Math.Abs(hop[0].Seconds - 94) < 6 && Math.Abs(hop[1].Seconds - 94) < 6
                && FlightGeo.Dist(hop[0].Pts.Last().X, hop[0].Pts.Last().Y, 3000, 0) < 40 && FlightGeo.Dist(hop[1].Pts[0].X, hop[1].Pts[0].Y, 3000, 0) < 40);
            // the same flight with a direct A > C route in the files: one leg (the game flies it in one go)
            var direct = new List<TaxiRoute>(FlGame) { FlRoute(904, 101, 103, 0, 0, 40, 3000, 3000, 40) };
            var one = FlFly(FlRecorder(direct), FlPath(32, 4, 2, 0, 0, 3000, 0, 3000, 3000), t0.AddMinutes(20));
            Check("record: with a direct route A > C in the files the same flight stays one leg A > C", one != null && one.Count == 1 && one[0].Key == "101>103");
            // a flight that passes B but whose chain says nothing about B (no game route touches it): one leg
            var noChain = FlFly(FlRecorder(new List<TaxiRoute>()), FlPath(32, 4, 2, 0, 0, 3000, 0, 3000, 3000), t0.AddMinutes(25));
            Check("record: passing over a master the game's routes do not chain through does not cut the flight (one leg A > C)", noChain != null && noChain.Count == 1 && noChain[0].Key == "101>103");
            // glitches: the whole flight is dropped
            int rej = rec.Rejected;
            var gap = FlFly(rec, FlPath(32, 4, 3, 0, 0, 1500, 400, 3000, 0), t0.AddMinutes(30), tweak: (i, p) => i > 30 && i < 45 ? null : p);
            Check("glitch: a pause of 15 s in the samples (a /reload mid-air) drops the flight (" + rec.LastResult + ")", gap == null && rec.Rejected == rej + 1 && rec.LastResult.Contains("gap"));
            var jump = FlFly(rec, FlPath(32, 4, 4, 0, 0, 1500, 400, 3000, 0), t0.AddMinutes(40), tweak: (i, p) => i == 40 ? new[] { p[0] + 900, p[1] - 900 } : p);
            Check("glitch: a jump of 1200 yards in a second (a teleport, a wrong read) drops the flight (" + rec.LastResult + ")", jump == null && rec.LastResult.Contains("jump"));
            var air = FlFly(rec, FlPath(32, 4, 5, 1500, 400, 3000, 0), t0.AddMinutes(50), groundStart: false);
            Check("glitch: a flight that starts in mid-air (the Outpost started during it) is not used (" + rec.LastResult + ")", air == null && rec.LastResult.Contains("take-off"));
            var cont = FlFly(rec, FlPath(32, 4, 6, 0, 0, 1500, 400, 3000, 0), t0.AddMinutes(60), tweak: (i, p) => i == 50 ? new[] { p[0], p[1], 0 } : p);
            Check("glitch: a sample on another continent drops the flight (" + rec.LastResult + ")", cont == null && rec.LastResult.Contains("continent"));
            var at = t0.AddMinutes(70); rec.Sample(1, 3, 2, double.NaN, false, at);
            foreach (var p in FlPath(32, 4, 7, 0, 0, 1500, 400).Take(30)) { at = at.AddSeconds(1); rec.Sample(1, p[0], p[1], double.NaN, true, at); }
            rec.Break("position unavailable");
            at = at.AddSeconds(1); var after = rec.Sample(1, 3000, 0, double.NaN, false, at);
            Check("glitch: a flight whose position went away on the way (Break) is not used, and landing afterwards records nothing", after == null && !rec.Flying && rec.LastResult.Contains("unavailable"));
            var tiny = FlFly(rec, FlPath(32, 0, 8, 0, 0, 60, 0), t0.AddMinutes(80));
            Check("glitch: a hop of a few seconds is too short to be a flight", tiny == null);
        }

        // ---------------------------------------------------------------- the rules
        void FlRuleChecks(CheckFn Check, string work)
        {
            var t0 = new DateTime(2026, 10, 17, 13, 0, 0, DateTimeKind.Utc);
            var rec = FlRecorder();
            var s = FlStore(Path.Combine(work, "rules.json"));
            int changes = 0; s.Changed += () => changes++;
            s.Add(new[] { FlBent(rec, 400, 11, t0) });
            var st1 = s.State(1, 101, 102);
            Check("rules: one flight that differs from the game's route is not enough (a glitch?): no learned route, the game's line stays (" + string.Join(",", st1?.Fits ?? new List<FlightFit>()) + ")",
                st1 != null && st1.Learned == null && st1.Single != null && s.Get(1, 101, 102) == null && st1.Fits.SequenceEqual(new[] { FlightFit.Deviates }));
            s.Add(new[] { FlBent(rec, 380, 12, t0.AddMinutes(10)) });
            var lr = s.Get(1, 101, 102);
            var mid = lr == null ? new FlightGeo.P() : FlightGeo.Resample(lr.Pts, 65)[32];
            Check("rules: a second flight that agrees with the first (both 380-400 yards off the game's line) makes it a learned route: the average of the two (middle at y " + Math.Round(mid.Y) + "), " + (lr?.Flights ?? 0) + " flights, " + (lr?.Seconds ?? 0) + " s",
                lr != null && !lr.FromLodge && lr.Flights == 2 && Math.Abs(mid.Y - 390) < 25 && lr.Seconds > 90 && lr.GameHasRoute && lr.VsGame.Max > 300 && lr.Pts.Count <= LearnedFlights.MaxPoints);
            Check("rules: the store says it changed (" + changes + " times) and counts what it has (1 leg, 2 flights, 1 learned)", changes == 2 && s.LegCount == 1 && s.FlightCount == 2 && s.LearnedCount == 1);
            // Forever changes it back: a flight on the game's line wins
            var back = FlFly(rec, FlPath(32, 4, 13, 0, 0, 1500, 0, 3000, 0), t0.AddMinutes(20)).First();
            s.Add(new[] { back });
            var st3 = s.State(1, 101, 102);
            Check("revert: a newer flight on the game's route drops the learned one (it matches: " + st3.Fits.Last() + ") - the game's line is drawn again", s.Get(1, 101, 102) == null && st3.NewestMatchesGame && st3.Fits.Last() == FlightFit.Matches);
            s.Add(new[] { FlBent(rec, 400, 14, t0.AddMinutes(30)) });
            Check("revert: after that, one flight on the bent line again is not enough (only flights after the last game match count)", s.Get(1, 101, 102) == null && s.State(1, 101, 102).Single != null);
            s.Add(new[] { FlBent(rec, 395, 15, t0.AddMinutes(40)) });
            Check("revert: a second agreeing one learns it again", s.Get(1, 101, 102)?.Flights == 2);
            // disagreeing flights: B > A once 400 west, once 400 east
            var ba1 = FlFly(rec, FlPath(32, 4, 16, 3000, 0, 1500, 400, 0, 0), t0.AddMinutes(50)).First();
            var ba2 = FlFly(rec, FlPath(32, 4, 17, 3000, 0, 1500, -400, 0, 0), t0.AddMinutes(60)).First();
            s.Add(new[] { ba1, ba2 });
            Check("rules: two flights that both differ from the game but not in the same way (one west, one east) teach nothing", ba1.Key == "102>101" && s.Get(1, 102, 101) == null && s.State(1, 102, 101).Fits.All(x => x == FlightFit.Deviates));
            // unclear: 50 yards off - neither the game's line nor clearly another
            var u1 = FlBent(rec, 50, 18, t0.AddMinutes(70)); var u2 = FlBent(rec, 55, 19, t0.AddMinutes(80));
            var su = FlStore(null); su.Add(new[] { u1, u2 });
            Check("rules: flights only 50 yards off the game's line are unclear (" + string.Join(",", su.State(1, 101, 102).Fits) + "): they never learn a route", su.Get(1, 101, 102) == null && su.State(1, 101, 102).Fits.All(x => x == FlightFit.Unclear));
            // a leg the game files do not have (C > A): two agreeing flights learn it
            var ca = new[] { FlFly(rec, FlPath(32, 4, 20, 3000, 3000, 1500, 1200, 0, 0), t0.AddMinutes(90)).First(), FlFly(rec, FlPath(32, 4, 21, 3000, 3000, 1500, 1210, 0, 0), t0.AddMinutes(100)).First() };
            s.Add(ca);
            var lca = s.Get(1, 103, 101);
            Check("rules: a leg the game files have no route for (C > A) is learned from two agreeing flights, marked as having no game route", lca != null && !lca.GameHasRoute && lca.Flights == 2);
            Check("rules: All lists one learned route per leg on the continent (A > B, C > A) and none for another continent", s.All(1).Select(x => x.Key).OrderBy(x => x).SequenceEqual(new[] { "101>102", "103>101" }) && s.All(0).Count == 0, string.Join(",", s.All(1).Select(x => x.Key)));
            // the store keeps at most 8 flights a leg
            var cap = FlStore(null); for (int i = 0; i < 11; i++) cap.Add(new[] { FlBent(rec, 400 + i, 30 + i, t0.AddMinutes(200 + i * 5)) });
            Check("rules: at most 8 flights a leg are kept (11 flown)", cap.FlightCount == LearnedFlights.MaxFlightsPerLeg);
            Check("rules: an invalid leg (same node twice, a NaN, a huge coordinate, too many points) is never stored",
                !LearnedFlights.Valid(new ObservedFlight { MapId = 1, From = 5, To = 5, Pts = new List<FlightGeo.P> { new FlightGeo.P(0, 0), new FlightGeo.P(1, 1) } })
                && !LearnedFlights.Valid(new ObservedFlight { MapId = 1, From = 5, To = 6, Pts = new List<FlightGeo.P> { new FlightGeo.P(0, double.NaN), new FlightGeo.P(1, 1) } })
                && !LearnedFlights.Valid(new ObservedFlight { MapId = 1, From = 5, To = 6, Pts = new List<FlightGeo.P> { new FlightGeo.P(0, 0), new FlightGeo.P(99999, 1) } })
                && !LearnedFlights.Valid(new ObservedFlight { MapId = 1, From = 5, To = 6, Pts = Enumerable.Range(0, 65).Select(i => new FlightGeo.P(i, i)).ToList() }));
            // the flight time of a leg from recorded flights
            Check("time: the leg's flight time is the average of its recorded flights (" + s.Seconds(1, 101, 102) + " s)", s.Seconds(1, 101, 102) > 90 && s.Seconds(1, 101, 102) < 110 && s.Seconds(1, 104, 101) == 0);
        }

        // ---------------------------------------------------------------- the file
        void FlFileChecks(CheckFn Check, string work)
        {
            var t0 = new DateTime(2026, 10, 17, 14, 0, 0, DateTimeKind.Utc);
            var rec = FlRecorder();
            var file = Path.Combine(work, "persist.json");
            var s = FlStore(file);
            s.Add(new[] { FlBent(rec, 400, 41, t0, 160) }); s.Add(new[] { FlBent(rec, 390, 42, t0.AddMinutes(5), 160) });
            var a = s.Get(1, 101, 102);
            var s2 = FlStore(file); var b = s2.Get(1, 101, 102);
            Check("file: the learned route is there again after a restart (same line, same flights)", File.Exists(file) && a != null && b != null && b.Flights == 2 && a.Pts.Count == b.Pts.Count && a.Pts.Zip(b.Pts, (p, q) => FlightGeo.Dist(p, q)).Max() < 0.2);
            var text = File.ReadAllText(file);
            var root = Json.Obj(text); var keys = new HashSet<string>();
            void Walk(object o) { if (o is Dictionary<string, object> d) foreach (var kv in d) { keys.Add(kv.Key); Walk(kv.Value); } else if (o is List<object> l) foreach (var x in l) Walk(x); }
            Walk(root);
            Check("file: it holds only the leg (map, two node ids), the order, the duration, the game build and the simplified line - no clock time, no names (" + string.Join(",", keys.OrderBy(k => k)) + ", " + text.Length + " bytes)",
                keys.SetEquals(new[] { "v", "legs", "m", "a", "b", "f", "s", "t", "p" }) && text.Contains("1.60.1.70374") && !text.Contains("Alpha") && !text.Contains("2026") && text.Length < 4000);
            Check("file: heights the solver had are kept, unknown ones are null", text.Contains("160") && text.Contains("null"));
            File.WriteAllText(file, "{\"v\":1,\"legs\":[{\"m\":1,\"a\":101,\"b\":102,\"f\":[{\"s\":1,\"t\":90,\"b\":\"x\",\"p\":[0,0,null,1e9,0,null]}]}]} trailing {{{");
            var s3 = FlStore(file);
            Check("file: a damaged file (bad numbers, garbage) is read as nothing and never throws", s3.FlightCount == 0);
            File.WriteAllText(file, "{{{{ no json");
            Check("file: a file that is not JSON at all is read as nothing", FlStore(file).FlightCount == 0);
            s.Add(new[] { FlBent(rec, 400, 43, t0.AddMinutes(10)) });
            int fired = 0; s.Changed += () => fired++;
            s.Clear();
            Check("clear: Clear learned routes forgets every flight and deletes the file (the store says it changed)", !File.Exists(file) && s.FlightCount == 0 && s.LegCount == 0 && s.Get(1, 101, 102) == null && fired == 1);
            Check("settings: the Settings line counts what was recorded and says what is kept",
                SettingsPage.LearnedFlightsText(0, 0, 0, 0).EndsWith("Nothing recorded yet.") && SettingsPage.LearnedFlightsText(2, 5, 1, 3).Contains("5 flights on 2 legs recorded, 1 route learned. Your Lodge shares 3 learned routes.")
                && SettingsPage.LearnedFlightsText(1, 1, 0, 0).Contains("Only the simplified line, its flight time and the game version are kept"));
        }

        // ---------------------------------------------------------------- the Flight routes layer
        static ObservedFlight FlOn(int map, int from, int to, List<double[]> path, double secs, long seq = 0) =>
            new ObservedFlight { MapId = map, From = from, To = to, Seconds = secs, Build = "1.60.1.70374", Pts = FlightGeo.Simplify(path.Select(p => new FlightGeo.P(p[0], p[1])).ToList(), 12) };

        // the world map fixture's route Orgrimmar (23, at 300 / 400) <-> Ratchet (80, at -400 / 1200) on continent 0, bent `bend` yards off the straight line
        static List<double[]> FlFixtureBent(double bend, int seed)
        {
            double ax = 300, ay = 400, bx = -400, by = 1200, mx = (ax + bx) / 2, my = (ay + by) / 2, dx = bx - ax, dy = by - ay, l = Math.Sqrt(dx * dx + dy * dy);
            return FlPath(32, 3, seed, ax, ay, mx + dy / l * bend, my - dx / l * bend, bx, by);
        }

        static MapSet FlLayerSet() => WmLayerSet(MapTables.Parse(RawWith(Raw("UiMap", UiMapTable()), Raw("TaxiNodes", WmTaxiNodes()), Raw("TaxiPath", WmTaxiPaths()), Raw("TaxiPathNode", WmTaxiPoints()))));

        void FlLayerChecks(CheckFn Check, string work)
        {
            MapData.SetForTest(new MapIndex(FlLayerSet()));
            var s = new LearnedFlights(Path.Combine(work, "layer.json"));            // the game's routes and masters from MapData, like the app
            WorldMapLayers.Learned = () => s;
            var savedTimes = WorldMapLayers.FlightTimes; WorldMapLayers.FlightTimes = () => null;
            try { FlLayerChecksIn(Check, s); } finally { WorldMapLayers.FlightTimes = savedTimes; }
        }

        void FlLayerChecksIn(CheckFn Check, LearnedFlights s)
        {
            List<Dictionary<string, object>> Items()
            {
                var r = WorldMapLayers.Routes(0);
                var m = Json.Obj(r.Json);
                return m == null ? new List<Dictionary<string, object>>() : m.List("items").OfType<Dictionary<string, object>>().ToList();
            }
            var before = Items();
            var game = before.FirstOrDefault(i => (i.Str("name") ?? "").Contains("Orgrimmar"));
            Check("layer: without learned routes the route Orgrimmar - Ratchet is drawn from the game files, dashed in its faction's colour", game != null && game.Str("d") == "6 5" && game.Str("c") != WorldMapLayers.LearnedColor && !game.ContainsKey("learned"), Json.Write(game));
            s.Add(new[] { FlOn(0, 23, 80, FlFixtureBent(250, 51), 95) }); s.Add(new[] { FlOn(0, 23, 80, FlFixtureBent(240, 52), 99) });
            var after = Items();
            var lr = after.FirstOrDefault(i => (i.Str("name") ?? "").Contains("Orgrimmar"));
            var pts = lr == null ? new List<object>() : lr.List("pts");
            // the plane point of the bend
            var bend = FlFixtureBent(245, 0); var mid = bend[bend.Count / 2]; WorldMapMath.WorldToLatLng(mid[0], mid[1], out var mlat, out var mlng);
            double near = double.MaxValue; for (int i = 0; i + 1 < pts.Count; i += 2) near = Math.Min(near, Math.Sqrt(Math.Pow((double)pts[i] - mlat, 2) + Math.Pow((double)pts[i + 1] - mlng, 2)));
            Check("layer: with two agreeing flights the route is drawn along the learned line instead (it passes the bend; " + pts.Count / 2 + " points), solid, teal, a little wider, marked 'mine'",
                lr != null && lr.Str("learned") == "mine" && lr["d"] == null && lr.Str("c") == WorldMapLayers.LearnedColor && Convert.ToDouble(lr["w"], CultureInfo.InvariantCulture) > 2 && near < 0.2 && after.Count == before.Count, Json.Write(lr));
            Check("layer: its tooltip says where it comes from (\"" + lr?.Str("tip") + "\") and how many flights agree (\"" + lr?.Str("extra") + "\")",
                lr != null && lr.Str("tip").StartsWith(WorldMapLayers.LearnedMineTip) && lr.Str("extra").Contains("2 of your flights agree"));
            Check("layer: with no flight time from the Lodge or Hunter Helper the leg's time comes from the recorded flights (\"" + lr?.Str("label") + "\")", lr != null && lr.Str("label") == "1m 37s");
            Check("layer: the other routes keep the game's style", after.Where(i => i != lr).All(i => !i.ContainsKey("learned") && i.Str("d") == "6 5"));
            var cat = WorldMapLayers.Catalog().First(c => (string)c["id"] == "routes");
            Check("layer: the Flight routes row explains the teal line (learned from real flights, differs from the game files)", ((string)cat["tip"]).Contains("learned route") && ((string)cat["tip"]).Contains("differently from the game files"));
            // the Lodge's, when I have none of my own
            s.Clear();
            var bentL = FlFixtureBent(260, 53);
            s.SetLodge(LearnedFlights.ParseServer(Json.Obj("{\"t\":\"flights.routes.data\",\"at\":1,\"routes\":{\"23>80\":{\"m\":0,\"p\":" + Json.Write(LearnedFlights.WritePts(bentL.Select(p => new FlightGeo.P(p[0], p[1])).Where((p, i) => i % 4 == 0 || i == bentL.Count - 1))) + ",\"t\":97,\"n\":3,\"k\":2}}}")));
            var lod = Items().FirstOrDefault(i => (i.Str("name") ?? "").Contains("Orgrimmar"));
            Check("layer: a route the Lodge learned is drawn the same way, labelled \"learned by your Lodge\" with how many members (\"" + lod?.Str("extra") + "\")",
                lod != null && lod.Str("learned") == "lodge" && lod.Str("tip").StartsWith(WorldMapLayers.LearnedLodgeTip) && lod.Str("extra").Contains("3 flights by 2 members"), Json.Write(lod));
            // my own newest flight on the game's line beats the Lodge's
            var gameLine = new List<double[]> { new[] { 300.0, 400 }, new[] { -50.0, 800 }, new[] { -400.0, 1200 } };
            var g = new LearnedFlights(null) { GameRoutes = m => new[] { TaxiRoute.Make(501, 23, 80, 0, gameLine.Select(p => new[] { p[0], p[1], 50.0 }).ToList()) }, Node = (m, id) => MapData.Index.Set.TaxiNodes.FirstOrDefault(n => n.Id == id) };
            g.SetLodge(LearnedFlights.ParseServer(Json.Obj("{\"routes\":{\"23>80\":{\"m\":0,\"p\":" + Json.Write(LearnedFlights.WritePts(bentL.Select(p => new FlightGeo.P(p[0], p[1])).Where((p, i) => i % 4 == 0 || i == bentL.Count - 1))) + ",\"t\":97,\"n\":3,\"k\":2}}}")));
            bool lodgeFirst = g.Get(0, 23, 80)?.FromLodge == true;
            g.Add(new[] { FlOn(0, 23, 80, FlPath(32, 3, 54, 300, 400, -50, 800, -400, 1200), 95) });
            Check("layer: the Lodge's route is used until my own newest flight of that leg flies the game's line - then the game's line is drawn", lodgeFirst && g.Get(0, 23, 80) == null);
            s.Clear(); WorldMapLayers.Learned = () => null;
            Check("layer: with no store at all the layer still builds (the game's routes)", Items().Count == before.Count);
        }

        // ---------------------------------------------------------------- the elevation solver flies the learned line
        void FlElevationChecks(CheckFn Check, string work)
        {
            var t0 = new DateTime(2026, 10, 17, 15, 0, 0, DateTimeKind.Utc);
            var rec = FlRecorder();
            var s = FlStore(Path.Combine(work, "elev.json"));
            s.Add(new[] { FlBent(rec, 400, 61, t0, 210) }); s.Add(new[] { FlBent(rec, 390, 62, t0.AddMinutes(5), 210) });
            var merged = s.MergeRoutes(1, FlGame);
            var ab = merged.FirstOrDefault(r => r.From == 101 && r.To == 102);
            Check("elevation: the solver's routes have the learned line in place of the game's A > B (same id 901, marked learned), the others unchanged",
                ab != null && ab.Learned && ab.Id == 901 && merged.Count == FlGame.Count && merged.Where(r => r.Id != 901).All(r => FlGame.Contains(r)));
            Check("elevation: the merged list is cached until the store changes", ReferenceEquals(merged, s.MergeRoutes(1, FlGame)));
            ElevResult Fly(IReadOnlyList<TaxiRoute> routes, out int onRoute, out int n, out double zErr)
            {
                var w = new FixtureWorld { G = (x, y) => 10, R = routes.ToList() };
                var e = new LiveElevation(w); var at = t0;
                e.Solve(1, 4, 3, 0, at);
                onRoute = 0; n = 0; zErr = 0; ElevResult last = new ElevResult();
                foreach (var p in FlPath(32, 5, 63, 0, 0, 1500, 395, 3000, 0).Skip(2).Take(80))
                {
                    at = at.AddSeconds(1); last = e.Solve(1, p[0], p[1], LiveMove.Taxi, at); n++;
                    if (last.RouteId == 901) onRoute++;
                    var lr901 = routes.First(r => r.Id == 901); lr901.Project(p[0], p[1], out _, out var want);
                    if (p[0] > 200 && p[0] < 2800 && !double.IsNaN(want)) zErr = Math.Max(zErr, Math.Abs(last.Z - Math.Max(10, want)));
                }
                return last;
            }
            var lastL = Fly(merged, out var onL, out var nL, out var errL);
            var lastG = Fly(FlGame, out var onG, out var nG, out var errG);
            var topL = merged.First(r => r.Id == 901).Z.Where(z => !double.IsNaN(z)).DefaultIfEmpty(0).Max();
            Check("elevation: flying Forever's bent line, the dot is matched to the learned route on " + onL + " of " + nL + " points and flies at its heights (up to " + Math.Round(topL) + ", worst " + Math.Round(errL, 1) + " off); with the game files only it fits on " + onG + " and falls back to 'no route fits' (" + lastG.Why + ")",
                onL >= nL - 2 && errL < 3 && Math.Abs(topL - 210) < 1 && onG < nG / 2 && lastL.Src == ElevSrc.Air);
            // a learned line without heights: the solver lifts over the ground there instead of a NaN
            var noZ = new LearnedFlights(null) { GameRoutes = m => new TaxiRoute[0] };
            noZ.Add(new[] { FlBent(rec, 400, 64, t0.AddMinutes(10)) }); noZ.Add(new[] { FlBent(rec, 400, 65, t0.AddMinutes(15)) });
            var mz = noZ.MergeRoutes(1, new TaxiRoute[0]);
            var w2 = new FixtureWorld { G = (x, y) => 10, R = mz.ToList() }; var e2 = new LiveElevation(w2); var at2 = t0; e2.Solve(1, 4, 3, 0, at2);
            ElevResult r2 = new ElevResult(); foreach (var p in FlPath(32, 0, 66, 0, 0, 1500, 400).Skip(1).Take(20)) { at2 = at2.AddSeconds(1); r2 = e2.Solve(1, p[0], p[1], LiveMove.Taxi, at2); }
            Check("elevation: a learned leg the files lack (and with no heights) is still matched (id " + r2.RouteId + "), the dot " + Math.Round(r2.Z) + " yards up = a little over the ground, never NaN",
                mz.Count == 1 && mz[0].Id < 0 && r2.RouteId == mz[0].Id && Math.Abs(r2.Z - (10 + LiveElevation.NoRouteLift)) < 0.01);
            // the app's solver world reads the shared store
            MapData.SetForTest(new MapIndex(FlLayerSet()));
            var shared = new LearnedFlights(Path.Combine(work, "elev-shared.json"));
            LearnedFlights.Shared = shared;
            shared.Add(new[] { FlOn(0, 23, 80, FlFixtureBent(250, 67), 95) }); shared.Add(new[] { FlOn(0, 23, 80, FlFixtureBent(245, 68), 95) });
            var gw = GameElevationWorld.Shared.Routes(0);
            Check("elevation: the app's own solver world (GameElevationWorld) hands out the learned line for the fixture's Orgrimmar > Ratchet (" + gw.Count + " routes, learned " + gw.Count(r => r.Learned) + ")", gw.Any(r => r.Learned && r.From == 23 && r.To == 80));
            LearnedFlights.Shared = FlStore(Path.Combine(work, "shared2.json"));
        }

        // ---------------------------------------------------------------- the live position records a flight end to end
        void FlLiveChecks(CheckFn Check, string work)
        {
            var s = FlStore(Path.Combine(work, "live.json"));
            // zone 11 = the whole invented continent 1: fx -> y (west, 0..4000), fy -> x (north, 0..4000)
            var lp = new LivePosition { ToWorld = (ui, fx, fy) => ui == 11 ? new WorldPoint(1, fy * 4000, fx * 4000) : (WorldPoint?)null, ElevationWorld = new FixtureWorld { G = (x, y) => 10, R = FlGame.ToList() }, Learned = s };
            lp.Recorder.Masters = m => m == 1 ? FlMasters : new TaxiNodeInfo[0]; lp.Recorder.Routes = m => FlGame; lp.Recorder.Build = () => "1.60.1.70374";
            GamePresence.Character Ch(double x, double y, int move, bool known = true) { var c = LiveChar(fx: y / 4000, fy: x / 4000); c.PosMove = move; c.PosMoveKnown = known; return c; }
            var t = DateTime.UtcNow.AddMinutes(-30);
            void FlyLive(double bend, int seed, bool known = true)
            {
                lp.Update(true, true, Ch(2, 1, 0, known), t);
                foreach (var p in FlPath(32, 3, seed, 0, 0, 1500, bend, 3000, 0)) { t = t.AddSeconds(1); lp.Update(true, true, Ch(p[0], p[1], LiveMove.Taxi, known), t); }
                t = t.AddSeconds(1); lp.Update(true, true, Ch(2999, 1, 0, known), t);
                t = t.AddSeconds(30);
            }
            FlyLive(400, 71);
            Check("live: a flight with the live position on is recorded when you land (" + lp.Recorder.LastResult + ")", s.FlightCount == 1 && s.State(1, 101, 102) != null);
            FlyLive(390, 72);
            Check("live: after a second flight on the same line the leg is learned", s.Get(1, 101, 102)?.Flights == 2);
            var s2 = FlStore(null); lp.Learned = s2;
            FlyLive(400, 73, known: false);
            Check("live: from a Hub that does not send the flight bit nothing is recorded", s2.FlightCount == 0);
            // the setting switched off during a flight: that flight is not used
            lp.Update(true, true, Ch(2, 1, 0), t);
            foreach (var p in FlPath(32, 3, 74, 0, 0, 1500, 400, 3000, 0).Take(40)) { t = t.AddSeconds(1); lp.Update(true, true, Ch(p[0], p[1], LiveMove.Taxi), t); }
            lp.Update(false, true, Ch(1500, 400, LiveMove.Taxi), t.AddSeconds(1));
            t = t.AddSeconds(2); lp.Update(true, true, Ch(2999, 1, 0), t);
            Check("live: switching the live position off mid-flight drops that flight", s2.FlightCount == 0 && !lp.Recorder.Flying);
        }

        // ---------------------------------------------------------------- the Lodge round trip (synthetic frames, no server)
        async Task FlLodgeChecks(CheckFn Check, string work)
        {
            var t0 = new DateTime(2026, 10, 17, 16, 0, 0, DateTimeKind.Utc);
            var rec = FlRecorder();
            var s = FlStore(Path.Combine(work, "lodge.json"));
            s.Add(new[] { FlBent(rec, 400, 81, t0, 200) }); s.Add(new[] { FlBent(rec, 390, 82, t0.AddMinutes(5), 200) });
            s.Add(new[] { FlFly(rec, FlPath(32, 4, 83, 3000, 0, 3000, 1500, 3000, 3000), t0.AddMinutes(10)).First() });     // B > C on the game's line: nothing to say
            s.Add(new[] { FlFly(rec, FlPath(32, 4, 84, 3000, 3000, 1500, 1200, 0, 0), t0.AddMinutes(15)).First() });        // C > A once: n = 1
            var hadOff = settings.FlightShareOff; var hadClock = Session.Clock;
            var sent = new List<Dictionary<string, object>>(); var now = new DateTime(2026, 10, 17, 18, 0, 0, DateTimeKind.Utc);
            var sentFile = Path.Combine(Util.DataDir, "flightroutes-sent.txt");
            // the frames as the server would read them (numbers as JSON numbers)
            List<Dictionary<string, object>> Of(string ty) => sent.Where(o => o.Str("t") == ty).Select(o => Json.Obj(Json.Write(o))).ToList();
            string Welcome(string role, bool feature) => FunWelcome(role, FunPeople(false), "", "\"reply\",\"react\",\"profile\",\"flights\"" + (feature ? ",\"flightroutes\"" : ""), "2.26.0");
            try
            {
                Session.Disconnect();
                Session.Clock = () => now; Session.SentForTest = o => sent.Add(o);
                Session.RouteStoreForTest = s; settings.FlightShareOff = false;
                try { File.Delete(sentFile); } catch (Exception) { }
                Session.FlightRoutesResetForTest();
                // a server without the feature: nothing about routes
                Session.FeedForTest(Welcome("member", false));
                Check("lodge: a server without \"flightroutes\" gets no route frames at all", !Session.SupportsFlightRoutes && Of("flights.routes.share").Count == 0 && Of("flights.routes.get").Count == 0);
                sent.Clear();
                Session.FeedForTest(Welcome("member", true));
                var shares = Of("flights.routes.share");
                var routes = shares.Count == 1 ? shares[0].Child("routes") : null;
                Check("lodge: right after the welcome one flights.routes.share goes out: the learned A > B (n 2) and the single C > A (n 1), not B > C (it flew the game's line)",
                    Session.SupportsFlightRoutes && routes != null && routes.Count == 2 && routes.Child("101>102").Int("n") == 2 && routes.Child("103>101").Int("n") == 1 && !routes.ContainsKey("102>103"), Json.Write(shares));
                var e = routes?.Child("101>102");
                Check("lodge: an entry holds only the continent, the line, the duration and n (" + string.Join(",", e?.Keys ?? new Dictionary<string, object>().Keys) + "), the line at most 64 points; nothing else in the frame",
                    e != null && e.Keys.OrderBy(k => k).SequenceEqual(new[] { "m", "n", "p", "t" }) && e.List("p").Count % 3 == 0 && e.List("p").Count <= 64 * 3 && shares[0].Keys.OrderBy(k => k).SequenceEqual(new[] { "routes", "t" }) && !Json.Write(shares[0]).Contains("Alpha"));
                Session.FeedForTest("{\"t\":\"flights.routes.shared\",\"ok\":true,\"accepted\":2,\"rejected\":0}");
                Check("lodge: after the answer the pool is asked for, and what was sent is remembered (on disk too)", Of("flights.routes.get").Count == 1 && Session.RouteSentForTest.Count == 2 && File.Exists(sentFile));
                // the round trip: the pool answers with what was shared (as if another member agreed) plus junk that must not get in
                var line = e.List("p");
                var far = Json.Write(LearnedFlights.WritePts(new[] { new FlightGeo.P(800, 800), new FlightGeo.P(1500, 400), new FlightGeo.P(3000, 0) }));
                var tooMany = Json.Write(LearnedFlights.WritePts(Enumerable.Range(0, 70).Select(i => new FlightGeo.P(i * 40, 0))));
                var gameLike = Json.Write(LearnedFlights.WritePts(new[] { new FlightGeo.P(0, 0), new FlightGeo.P(1500, 0), new FlightGeo.P(3000, 0) }));
                Session.FeedForTest("{\"t\":\"flights.routes.data\",\"at\":1790000000,\"routes\":{\"101>102\":{\"m\":1,\"p\":" + Json.Write(line) + ",\"t\":98,\"n\":3,\"k\":2},"
                    + "\"Horde|1>2\":{\"m\":1,\"p\":" + Json.Write(line) + ",\"t\":98,\"n\":3,\"k\":2},\"102>101\":{\"m\":1,\"p\":" + far + ",\"t\":98,\"n\":2,\"k\":1},"
                    + "\"103>102\":{\"m\":1,\"p\":" + tooMany + ",\"t\":98,\"n\":2,\"k\":1},\"103>104\":{\"m\":1,\"p\":" + Json.Write(line) + ",\"t\":98,\"n\":1,\"k\":1},"
                    + "\"102>103\":{\"m\":1,\"p\":[3000,0,null,3000,1500,null,3000,3000,null],\"t\":98,\"n\":2,\"k\":2},\"101>103\":{\"m\":1,\"p\":" + gameLike + ",\"t\":\"x\",\"n\":2,\"k\":2}}}");
                Check("lodge: the answer is checked again: a bad key, too many points, n under 2 and a string for the time are refused (" + Session.FlightRoutesReceived + " kept)", Session.FlightRoutesReceived == 3 && s.LodgeCount == 3);
                s.Clear();     // my own go: the Lodge's alone (Clear also drops the Lodge's copy; it comes back with the next answer)
                Session.FeedForTest("{\"t\":\"flights.routes.data\",\"at\":1790000000,\"routes\":{\"101>102\":{\"m\":1,\"p\":" + Json.Write(line) + ",\"t\":98,\"n\":3,\"k\":2},\"102>101\":{\"m\":1,\"p\":" + far + ",\"t\":98,\"n\":2,\"k\":1},\"102>103\":{\"m\":1,\"p\":[3000,0,null,3000,1500,null,3000,3000,null],\"t\":98,\"n\":2,\"k\":2}}}");
                var lodgeAB = s.Get(1, 101, 102);
                Check("lodge: the round trip comes back as a learned route from the Lodge (A > B, 3 flights by 2 members) the map and the solver use", lodgeAB != null && lodgeAB.FromLodge && lodgeAB.Flights == 3 && lodgeAB.Members == 2 && s.MergeRoutes(1, FlGame).Any(r => r.Learned && r.From == 101));
                Check("lodge: a Lodge route whose start is nowhere near the flight master (B > A starting 800 yards off) is not used", s.Get(1, 102, 101) == null);
                Check("lodge: a Lodge route that is just the game's line (B > C) is not used either - nothing to learn", s.Get(1, 102, 103) == null);
                // sharing off: nothing goes up, the pool still comes down
                sent.Clear(); settings.FlightShareOff = true; now = now.AddMinutes(30);
                s.Add(new[] { FlBent(rec, 400, 85, t0.AddMinutes(20)) }); s.Add(new[] { FlBent(rec, 395, 86, t0.AddMinutes(25)) });
                Session.FlightSettingsChanged();
                Check("lodge: with \"Share flight times with the lodge\" off nothing is uploaded (a new learned route waits), the pool is still asked for", Of("flights.routes.share").Count == 0 && Of("flights.routes.get").Count >= 1, Json.Write(sent));
                // back on: once the 10 minutes are over the change goes; a newer flight on the game's line takes my route back with { g: 1 }
                settings.FlightShareOff = false; sent.Clear(); now = now.AddMinutes(11);
                Session.FlightRouteSync();
                var s2 = Of("flights.routes.share");
                Check("lodge: switched on again, the changed route goes up", s2.Count == 1 && s2[0].Child("routes").ContainsKey("101>102"));
                Session.FeedForTest("{\"t\":\"flights.routes.shared\",\"ok\":true,\"accepted\":1,\"rejected\":0}");
                s.Add(new[] { FlFly(rec, FlPath(32, 4, 87, 0, 0, 1500, 0, 3000, 0), t0.AddMinutes(30)).First() });
                sent.Clear(); now = now.AddMinutes(5); Session.FlightRouteSync();
                Check("lodge: a route reverted by a flight on the game's line waits for the 10-minute slot", Of("flights.routes.share").Count == 0 && Session.RouteBacklogForTest);
                now = now.AddMinutes(6); Session.FlightRouteSync();
                var s3 = Of("flights.routes.share");
                Check("lodge: then it tells the Lodge \"the game's route again\" ({ m, g: 1 }) for that leg", s3.Count == 1 && s3[0].Child("routes").Child("101>102")?.Int("g") == 1 && s3[0].Child("routes").Child("101>102").Count == 2, Json.Write(s3));
                Session.FeedForTest("{\"t\":\"flights.routes.shared\",\"ok\":false,\"accepted\":0,\"rejected\":0,\"why\":\"guest\"}");
                sent.Clear(); now = now.AddMinutes(30);
                s.Add(new[] { FlBent(rec, 400, 88, t0.AddMinutes(40)) }); Session.FlightRouteSync();
                Check("lodge: after a 'guest' answer nothing more is sent", Of("flights.routes.share").Count == 0);
                // a guest from the welcome on
                Session.FlightRoutesResetForTest(); sent.Clear();
                Session.FeedForTest(Welcome("guest", true));
                Check("lodge: a guest never uploads routes (but reads the pool)", Of("flights.routes.share").Count == 0 && Of("flights.routes.get").Count == 1);
                await Task.Delay(1);
            }
            finally
            {
                Session.Disconnect();
                Session.SentForTest = null; Session.Clock = hadClock; settings.FlightShareOff = hadOff; Session.RouteStoreForTest = null; Session.FlightRoutesResetForTest();
                try { File.Delete(sentFile); } catch (Exception) { }
                try { File.Delete(sentFile + ".tmp"); } catch (Exception) { }
            }
        }
    }
}
