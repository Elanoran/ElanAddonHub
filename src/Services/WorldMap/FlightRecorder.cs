using System;
using System.Collections.Generic;
using System.Linq;
using ElansAddonHub.Services.Maps;

namespace ElansAddonHub.Services.WorldMap
{
    // ============================================================================================================================
    // Records the line a flight really takes (2.88, learned flight paths). WoW Forever flies some flight paths differently from the routes in the game files
    // (TaxiPathNode), so while the live position is on and the strip says "on a flight" (bit 4 of posFlags) the points of that flight are collected here, in memory.
    // When the flight ends it is checked and cut into legs:
    //   take-off and landing   the flight masters nearest to the first and the last point (within EndYards), like the "new flight path" mark of the trail
    //   glitches               a pause of the samples (a /reload mid-air, the strip gone), a jump at an impossible speed (a teleport), another continent, a start
    //                          in mid-air (the Outpost started during the flight) or too few points: the whole flight is dropped
    //   multi-hop flights      when the game files have no direct route between the two masters, the chain of game routes whose in-between masters the flight
    //                          passes within SplitReach yards (in that order) cuts it at those masters; the shortest such chain wins. No chain fits = one leg.
    // Each leg keeps only a simplified line (Douglas-Peucker, about 12 yards, at most 64 points) with the heights the elevation solver had, its duration and
    // the game build (LearnedFlights does the rest). Nothing else is kept: no time of day, no character.
    // ============================================================================================================================
    public sealed class ObservedFlight
    {
        public int MapId, From, To;
        public List<FlightGeo.P> Pts = new List<FlightGeo.P>();
        public double Seconds;
        public string Build = "";
        public long Seq;              // the order it was recorded in (LearnedFlights numbers them); no clock time is kept
        public string Key => LearnedFlights.KeyOf(From, To);
    }

    public sealed class FlightRecorder
    {
        public const double EndYards = 150, SplitReach = 100, MaxGapSeconds = 8, MaxSpeed = 80, MinSpeed = 6, MinLegYards = 150, SimplifyYards = 12, TakeOffSeconds = 6;
        public const int MinPoints = 6, MaxPoints = 64, MaxSamples = 4000, MaxChainLegs = 6;

        // the flight masters and the game's routes of a continent (null = MapData's flight tables); tests set their own
        public Func<int, IEnumerable<TaxiNodeInfo>> Masters;
        public Func<int, IReadOnlyList<TaxiRoute>> Routes;
        public Func<string> Build;

        struct S { public DateTime At; public int Map; public double X, Y, Z; }
        List<S> cur;
        S? lastGround;
        public bool Flying => cur != null;
        public string LastResult { get; private set; } = "";   // the last flight's outcome, for the log and the self-test
        public int Rejected { get; private set; }

        // one strip sample. Returns the legs of a flight that has just ended (and passed the checks), else null.
        public List<ObservedFlight> Sample(int mapId, double x, double y, double z, bool taxi, DateTime at)
        {
            if (double.IsNaN(x + y) || double.IsInfinity(x + y)) return null;
            var s = new S { At = at, Map = mapId, X = x, Y = y, Z = z };
            if (taxi)
            {
                if (cur == null)
                {
                    cur = new List<S>();
                    // where you stood a moment ago is the take-off (the strip says "on a flight" a beat after the take-off)
                    if (lastGround.HasValue && lastGround.Value.Map == mapId && (at - lastGround.Value.At).TotalSeconds <= TakeOffSeconds && (at - lastGround.Value.At).TotalSeconds >= 0)
                        cur.Add(new S { At = lastGround.Value.At, Map = mapId, X = lastGround.Value.X, Y = lastGround.Value.Y, Z = double.NaN });
                }
                if (cur.Count < MaxSamples) cur.Add(s); else { Drop("too long"); }
                lastGround = null;
                return null;
            }
            lastGround = s;
            if (cur == null) return null;
            // landed: this first point on the ground is at the landing flight master
            var pts = cur; cur = null;
            if (pts.Count > 0 && pts[pts.Count - 1].Map == mapId && (at - pts[pts.Count - 1].At).TotalSeconds <= MaxGapSeconds) pts.Add(new S { At = at, Map = mapId, X = x, Y = y, Z = double.NaN });
            return Finish(pts);
        }

        // the position went away during a flight (another state than a position, the setting off, another character): that flight is not used
        public void Break(string why)
        {
            if (cur != null) Drop(why ?? "position lost");
            lastGround = null;
        }

        void Drop(string why) { cur = null; Rejected++; LastResult = "dropped: " + why; Util.Log("learned flights: a flight was not used (" + why + ")"); }

        List<ObservedFlight> Reject(string why) { Rejected++; LastResult = "dropped: " + why; Util.Log("learned flights: a flight was not used (" + why + ")"); return null; }

        IEnumerable<TaxiNodeInfo> MastersOf(int map)
        {
            if (Masters != null) return Masters(map) ?? Enumerable.Empty<TaxiNodeInfo>();
            var set = MapData.Index?.Set;
            if (set == null || !set.HasTaxi) return Enumerable.Empty<TaxiNodeInfo>();
            return set.TaxiNodes.Where(n => n.MapId == map && WorldMapLayers.IsFlightMaster(n));
        }

        IReadOnlyList<TaxiRoute> RoutesOf(int map)
        {
            if (Routes != null) return Routes(map) ?? new TaxiRoute[0];
            return LearnedFlights.DefaultGameRoutes(map);
        }

        static TaxiNodeInfo Nearest(IEnumerable<TaxiNodeInfo> ms, double x, double y, double within, int not = 0)
        {
            TaxiNodeInfo best = null; double bd = within;
            foreach (var n in ms) { if (n.Id == not) continue; double d = FlightGeo.Dist(n.X, n.Y, x, y); if (d <= bd) { bd = d; best = n; } }
            return best;
        }

        List<ObservedFlight> Finish(List<S> pts)
        {
            if (pts.Count < MinPoints) return Reject("too few points (" + pts.Count + ")");
            int map = pts[0].Map;
            for (int i = 1; i < pts.Count; i++)
            {
                if (pts[i].Map != map) return Reject("another continent on the way");
                double dt = (pts[i].At - pts[i - 1].At).TotalSeconds, d = FlightGeo.Dist(pts[i].X, pts[i].Y, pts[i - 1].X, pts[i - 1].Y);
                if (dt > MaxGapSeconds || dt < 0) return Reject("a gap of " + Math.Round(dt) + " s in the samples");
                if (d > 40 && d / Math.Max(dt, 0.25) > MaxSpeed) return Reject("a jump of " + Math.Round(d) + " yards");
            }
            var ms = MastersOf(map).ToList();
            var a = Nearest(ms, pts[0].X, pts[0].Y, EndYards);
            if (a == null) return Reject("no flight master at the take-off (started in mid-air?)");
            var b = Nearest(ms, pts[pts.Count - 1].X, pts[pts.Count - 1].Y, EndYards, a.Id);
            if (b == null) return Reject("no flight master at the landing");
            var splits = Split(map, a, b, pts, ms);
            var res = new List<ObservedFlight>();
            var build = "";
            try { build = Build != null ? Build() ?? "" : LearnedFlights.CurrentBuild(); } catch (Exception) { }
            for (int k = 0; k + 1 < splits.Count; k++)
            {
                int i0 = splits[k].Item2, i1 = splits[k + 1].Item2;
                var seg = pts.Skip(i0).Take(i1 - i0 + 1).ToList();
                var line = seg.Select(p => new FlightGeo.P(p.X, p.Y, p.Z)).ToList();
                double len = FlightGeo.Length(line), secs = (seg[seg.Count - 1].At - seg[0].At).TotalSeconds;
                if (seg.Count < 3 || len < MinLegYards) return Reject("a leg of only " + Math.Round(len) + " yards");
                double speed = len / Math.Max(secs, 0.5);
                if (speed < MinSpeed || speed > MaxSpeed) return Reject("an odd speed of " + Math.Round(speed) + " yards a second");
                res.Add(new ObservedFlight
                {
                    MapId = map, From = splits[k].Item1, To = splits[k + 1].Item1, Seconds = Math.Round(secs, 1), Build = build,
                    Pts = FlightGeo.SimplifyCapped(line, SimplifyYards, MaxPoints).Select(p => new FlightGeo.P(Math.Round(p.X, 1), Math.Round(p.Y, 1), p.HasZ ? Math.Round(p.Z, 1) : double.NaN)).ToList(),
                });
            }
            LastResult = "recorded " + string.Join(", ", res.Select(r => r.Key)) + " (" + pts.Count + " points)";
            Util.Log("learned flights: " + LastResult);
            return res;
        }

        // the masters the flight is cut at, with the index of the point at each (first = take-off, last = landing)
        List<Tuple<int, int>> Split(int map, TaxiNodeInfo a, TaxiNodeInfo b, List<S> pts, List<TaxiNodeInfo> ms)
        {
            var whole = new List<Tuple<int, int>> { Tuple.Create(a.Id, 0), Tuple.Create(b.Id, pts.Count - 1) };
            IReadOnlyList<TaxiRoute> routes;
            try { routes = RoutesOf(map); } catch (Exception) { return whole; }
            if (routes.Any(r => r.From == a.Id && r.To == b.Id)) return whole;           // a direct route: the game flies it in one go
            var byFrom = routes.GroupBy(r => r.From).ToDictionary(g => g.Key, g => g.ToList());
            var nodes = ms.GroupBy(n => n.Id).ToDictionary(g => g.Key, g => g.First());
            List<Tuple<int, int>> best = null; double bestLen = double.MaxValue; int found = 0;
            // depth-first over the game's routes from A to B, no master twice, at most MaxChainLegs legs
            var path = new List<TaxiRoute>(); var seen = new HashSet<int> { a.Id };
            void Walk(int at)
            {
                if (found > 400 || path.Count >= MaxChainLegs || !byFrom.TryGetValue(at, out var outs)) return;
                foreach (var r in outs)
                {
                    if (seen.Contains(r.To)) continue;
                    path.Add(r);
                    if (r.To == b.Id)
                    {
                        found++;
                        var cut = Fit(path, pts, nodes);
                        double len = path.Sum(p => p.Length);
                        if (cut != null && (len < bestLen - 1e-6 || (Math.Abs(len - bestLen) < 1e-6 && cut.Count < best.Count))) { best = cut; bestLen = len; }
                    }
                    else { seen.Add(r.To); Walk(r.To); seen.Remove(r.To); }
                    path.RemoveAt(path.Count - 1);
                }
            }
            Walk(a.Id);
            return best ?? whole;
        }

        // the chain's in-between masters, each passed within SplitReach in order: the cut is the nearest point of that pass. Null = the flight did not pass one.
        static List<Tuple<int, int>> Fit(List<TaxiRoute> chain, List<S> pts, Dictionary<int, TaxiNodeInfo> nodes)
        {
            var cut = new List<Tuple<int, int>> { Tuple.Create(chain[0].From, 0) };
            int from = 1;
            for (int k = 0; k + 1 < chain.Count; k++)
            {
                if (!nodes.TryGetValue(chain[k].To, out var n)) return null;
                int i = from; while (i < pts.Count - 1 && FlightGeo.Dist(pts[i].X, pts[i].Y, n.X, n.Y) > SplitReach) i++;
                if (i >= pts.Count - 1) return null;
                int bi = i; double bd = FlightGeo.Dist(pts[i].X, pts[i].Y, n.X, n.Y);
                while (i < pts.Count - 1 && FlightGeo.Dist(pts[i].X, pts[i].Y, n.X, n.Y) <= SplitReach) { double d = FlightGeo.Dist(pts[i].X, pts[i].Y, n.X, n.Y); if (d < bd) { bd = d; bi = i; } i++; }
                cut.Add(Tuple.Create(n.Id, bi));
                from = bi + 1;
            }
            cut.Add(Tuple.Create(chain[chain.Count - 1].To, pts.Count - 1));
            return cut;
        }
    }
}
