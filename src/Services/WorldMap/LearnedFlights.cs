using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ElansAddonHub.Services.Maps;

namespace ElansAddonHub.Services.WorldMap
{
    // ============================================================================================================================
    // Learned flight paths (2.88). WoW Forever changes some flight paths on its server, so the line a flight really takes can differ from the route in the game
    // files. FlightRecorder hands each recorded leg here; per leg (from > to flight master, on one continent) the last MaxFlightsPerLeg flights are kept, and:
    //   compare    each flight against the game's route for that leg: both lines resampled, the mean and the largest distance both ways (FlightGeo.Deviation).
    //              Matches = mean <= MatchMean and max <= MatchMax; Deviates = mean >= DevMean or max >= DevMax (or the game files have no such route); else unclear.
    //   learn      two flights of the leg that both deviate and agree with each other (mean <= AgreeMean and max <= AgreeMax) make it a learned route: the
    //              average of all deviating flights that agree with the newest of them. One flight is never enough (a glitch), unclear flights never count.
    //   revert     a newer flight that matches the game's route again wins: only flights after it count (Forever may change it back).
    //   use        the world map's Flight routes layer draws the learned line instead of the game's (styled as learned), the live elevation solver matches flights
    //              against it (so the 3D dot follows it), and the leg's flight time comes from the recorded durations when nobody else measured it.
    //   share      (the Lodge, feature "flightroutes"; LodgeSession.FlightRoutes.cs) with the same switch as the flight times: my learned routes (and a single
    //              deviating flight, as n = 1) go to the Lodge, which pools them; routes the Lodge's members agree on come back and are used when I have
    //              none of my own and my newest flight of that leg did not match the game. The Lodge's are checked here again (ids, sizes, ends at the masters).
    // Kept in <data>\learned-flights.json: per leg the simplified lines (with the heights the solver had), durations and game builds. Nothing else.
    // ============================================================================================================================
    public sealed class LearnedRoute
    {
        public int MapId, From, To;
        public List<FlightGeo.P> Pts = new List<FlightGeo.P>();
        public double Seconds;            // the average duration of the flights it was learned from (0 = unknown)
        public int Flights;               // how many flights agree on it (the Lodge: how many reports)
        public int Members;               // the Lodge: how many members reported it (0 = mine)
        public bool FromLodge;
        public FlightGeo.Dev VsGame;      // how far it is from the game's route (Max = MaxValue when the files have none)
        public bool GameHasRoute;
        public string Key => LearnedFlights.KeyOf(From, To);
    }

    public enum FlightFit { Unclear, Matches, Deviates }

    public sealed partial class LearnedFlights
    {
        // ---- thresholds (yards); the self-test "flightlearn" checks each rule on invented flights
        public const double MatchMean = 20, MatchMax = 60;           // a flight this close to the game's route says the game files are right
        public const double DevMean = 40, DevMax = 100;              // a flight this far from it differs clearly
        public const double AgreeMean = 25, AgreeMax = 75;           // two flights this close to each other flew the same line
        public const int MaxFlightsPerLeg = 8, MaxLegs = 600, MaxPoints = 64, Samples = 64;
        public const double AverageSimplifyYards = 8;
        public const string FileName = "learned-flights.json";
        public const long MaxFileBytes = 4L * 1024 * 1024;

        // the store the app uses (tests swap in their own)
        static LearnedFlights shared;
        static readonly object sharedGate = new object();
        public static LearnedFlights Shared
        {
            get { lock (sharedGate) return shared ?? (shared = new LearnedFlights(Path.Combine(Util.DataDir, FileName))); }
            set { lock (sharedGate) shared = value; }
        }

        // the game's routes and flight masters of a continent (null = MapData's flight tables)
        public Func<int, IReadOnlyList<TaxiRoute>> GameRoutes;
        public Func<int, int, TaxiNodeInfo> Node;

        readonly string path;
        readonly object gate = new object();
        sealed class Leg { public int Map, From, To; public List<ObservedFlight> Flights = new List<ObservedFlight>(); }
        readonly Dictionary<string, Leg> legs = new Dictionary<string, Leg>(StringComparer.Ordinal);    // "<map>:<from>><to>"
        long seq;
        int version;
        public int Version { get { lock (gate) return version; } }
        public event Action Changed;
        public string FilePath => path;

        public LearnedFlights(string file)
        {
            path = file;
            if (!string.IsNullOrEmpty(path)) Load();
        }

        public static string KeyOf(int from, int to) => from.ToString(CultureInfo.InvariantCulture) + ">" + to.ToString(CultureInfo.InvariantCulture);
        static string LegKey(int map, int from, int to) => map.ToString(CultureInfo.InvariantCulture) + ":" + KeyOf(from, to);
        static readonly Regex KeyRx = new Regex(@"^([1-9][0-9]{0,6})>([1-9][0-9]{0,6})\z", RegexOptions.CultureInvariant);
        public static bool TryKey(string key, out int from, out int to)
        {
            from = to = 0;
            var m = KeyRx.Match(key ?? "");
            if (!m.Success) return false;
            from = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture); to = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            return from != to;
        }

        // ---------------------------------------------------------------- the game's side
        static MapSet defaultSet; static Dictionary<int, List<TaxiRoute>> defaultRoutes;
        static readonly object defaultGate = new object();
        public static IReadOnlyList<TaxiRoute> DefaultGameRoutes(int mapId)
        {
            lock (defaultGate)
            {
                var set = MapData.Index?.Set;
                if (set != defaultSet) { defaultSet = set; defaultRoutes = TaxiRoute.FromSet(set); }
                return defaultRoutes != null && defaultRoutes.TryGetValue(mapId, out var l) ? l : (IReadOnlyList<TaxiRoute>)new TaxiRoute[0];
            }
        }

        public static string CurrentBuild()
        {
            var s = MapData.BuildStamp ?? "";
            var i = s.IndexOf('-');
            return Clean(i > 0 ? s.Substring(0, i) : s);
        }
        static string Clean(string b) => new string((b ?? "").Where(c => char.IsDigit(c) || c == '.').Take(24).ToArray());

        IReadOnlyList<TaxiRoute> RoutesOf(int map) { try { return (GameRoutes != null ? GameRoutes(map) : DefaultGameRoutes(map)) ?? new TaxiRoute[0]; } catch (Exception) { return new TaxiRoute[0]; } }

        // the game's route for a leg (the first TaxiPath from > to on that continent), or null
        public TaxiRoute GameRoute(int map, int from, int to) => RoutesOf(map).FirstOrDefault(r => r.From == from && r.To == to);

        TaxiNodeInfo NodeOf(int map, int id)
        {
            try
            {
                if (Node != null) return Node(map, id);
                var set = MapData.Index?.Set;
                return set == null || !set.HasTaxi ? null : set.TaxiNodes.FirstOrDefault(n => n.Id == id && n.MapId == map);
            }
            catch (Exception) { return null; }
        }

        public static FlightFit Classify(IList<FlightGeo.P> line, TaxiRoute game, out FlightGeo.Dev dev)
        {
            dev = new FlightGeo.Dev { Mean = double.MaxValue, Max = double.MaxValue };
            if (line == null || line.Count < 2) return FlightFit.Unclear;
            if (game == null || game.X.Length < 2) return FlightFit.Deviates;                 // the game files have no such route: whatever was flown is new
            dev = FlightGeo.Deviation(line, FlightGeo.OfRoute(game), Samples);
            if (dev.Mean <= MatchMean && dev.Max <= MatchMax) return FlightFit.Matches;
            if (dev.Mean >= DevMean || dev.Max >= DevMax) return FlightFit.Deviates;
            return FlightFit.Unclear;
        }

        public static bool Agree(IList<FlightGeo.P> a, IList<FlightGeo.P> b)
        {
            var d = FlightGeo.Deviation(a, b, Samples);
            return d.Mean <= AgreeMean && d.Max <= AgreeMax;
        }

        // ---------------------------------------------------------------- one leg
        public sealed class LegState
        {
            public int MapId, From, To;
            public LearnedRoute Learned;               // my learned route (two or more agreeing flights), or null
            public ObservedFlight Single;              // no learned route, but my newest flight after the last game match deviates (n = 1 for the Lodge)
            public bool NewestMatchesGame;             // my newest flight of this leg flew the game's route
            public double Seconds;                     // the average duration of my flights of this leg
            public int Flights;
            public List<FlightFit> Fits = new List<FlightFit>();
        }

        LegState Evaluate(Leg l)
        {
            var st = new LegState { MapId = l.Map, From = l.From, To = l.To, Flights = l.Flights.Count };
            if (l.Flights.Count == 0) return st;
            var game = GameRoute(l.Map, l.From, l.To);
            var fl = l.Flights.OrderBy(f => f.Seq).ToList();
            var fits = fl.Select(f => Classify(f.Pts, game, out _)).ToList();
            st.Fits = fits;
            st.Seconds = Math.Round(fl.Where(f => f.Seconds > 0).Select(f => f.Seconds).DefaultIfEmpty(0).Average(), 1);
            int lastMatch = fits.LastIndexOf(FlightFit.Matches);
            st.NewestMatchesGame = lastMatch == fl.Count - 1;
            var cand = Enumerable.Range(0, fl.Count).Where(i => i > lastMatch && fits[i] == FlightFit.Deviates).Reverse().ToList();   // newest first
            foreach (var anchor in cand)
            {
                var cluster = cand.Where(j => j == anchor || Agree(fl[j].Pts, fl[anchor].Pts)).ToList();
                if (cluster.Count < 2) continue;
                var avg = FlightGeo.Average(cluster.Select(j => (IList<FlightGeo.P>)fl[j].Pts).ToList(), Samples);
                var line = Round(FlightGeo.SimplifyCapped(avg, AverageSimplifyYards, MaxPoints));
                var r = new LearnedRoute { MapId = l.Map, From = l.From, To = l.To, Pts = line, Flights = cluster.Count, GameHasRoute = game != null,
                    Seconds = Math.Round(cluster.Select(j => fl[j].Seconds).Where(s => s > 0).DefaultIfEmpty(0).Average(), 1) };
                Classify(line, game, out r.VsGame);
                st.Learned = r;
                break;
            }
            if (st.Learned == null && cand.Count > 0) st.Single = fl[cand[0]];
            return st;
        }

        static List<FlightGeo.P> Round(IEnumerable<FlightGeo.P> pts) => pts.Select(p => new FlightGeo.P(Math.Round(p.X, 1), Math.Round(p.Y, 1), p.HasZ ? Math.Round(p.Z, 1) : double.NaN)).ToList();

        // evaluations are cached per leg until the store changes or the game's routes do (another game build)
        readonly Dictionary<string, Tuple<LegState, TaxiRoute>> evalCache = new Dictionary<string, Tuple<LegState, TaxiRoute>>(StringComparer.Ordinal);
        LegState EvalCached(string k, Leg l)
        {
            var game = GameRoute(l.Map, l.From, l.To);
            if (evalCache.TryGetValue(k, out var c) && c.Item2 == game) return c.Item1;
            var st = Evaluate(l);
            evalCache[k] = Tuple.Create(st, game);
            return st;
        }

        public LegState State(int map, int from, int to)
        {
            lock (gate) { var k = LegKey(map, from, to); return legs.TryGetValue(k, out var l) ? EvalCached(k, l) : null; }
        }

        public List<LegState> States()
        {
            lock (gate) return legs.Select(kv => EvalCached(kv.Key, kv.Value)).ToList();
        }

        public int LegCount { get { lock (gate) return legs.Count; } }
        public int FlightCount { get { lock (gate) return legs.Values.Sum(l => l.Flights.Count); } }
        public int LearnedCount { get { lock (gate) return legs.Count(kv => EvalCached(kv.Key, kv.Value).Learned != null); } }

        // ---------------------------------------------------------------- recording
        public void Add(IEnumerable<ObservedFlight> flights)
        {
            if (flights == null) return;
            bool any = false;
            lock (gate)
            {
                foreach (var f in flights)
                {
                    if (!Valid(f)) continue;
                    var k = LegKey(f.MapId, f.From, f.To);
                    if (!legs.TryGetValue(k, out var l))
                    {
                        if (legs.Count >= MaxLegs) DropOldestLeg();
                        legs[k] = l = new Leg { Map = f.MapId, From = f.From, To = f.To };
                    }
                    f.Seq = ++seq;
                    l.Flights.Add(f);
                    while (l.Flights.Count > MaxFlightsPerLeg) l.Flights.RemoveAt(0);
                    evalCache.Remove(k);
                    any = true;
                }
                if (any) { version++; Save(); }
            }
            if (any) Fire();
        }

        void DropOldestLeg()
        {
            var oldest = legs.OrderBy(kv => kv.Value.Flights.Count == 0 ? 0 : kv.Value.Flights.Max(f => f.Seq)).First().Key;
            legs.Remove(oldest); evalCache.Remove(oldest);
        }

        static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
        public static bool Valid(ObservedFlight f)
        {
            if (f == null || f.From <= 0 || f.To <= 0 || f.From == f.To || f.From > 9999999 || f.To > 9999999 || f.MapId < 0 || f.MapId > 100000) return false;
            if (f.Pts == null || f.Pts.Count < 2 || f.Pts.Count > MaxPoints) return false;
            if (!Finite(f.Seconds) || f.Seconds < 0 || f.Seconds > 3600) return false;
            foreach (var p in f.Pts) if (!Finite(p.X) || !Finite(p.Y) || Math.Abs(p.X) > 20000 || Math.Abs(p.Y) > 20000 || (p.HasZ && Math.Abs(p.Z) > 5000)) return false;
            return true;
        }

        // Settings > General > Clear learned flight routes: my flights go (and the file); the Lodge's routes come back with its next update
        public void Clear()
        {
            lock (gate)
            {
                legs.Clear(); evalCache.Clear(); lodge.Clear(); lodgeCache.Clear(); version++;
                try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); } catch (Exception e) { Util.Log("learned flights: could not delete the file (" + e.GetType().Name + ")"); }
            }
            Fire();
        }

        public static event Action AnyChanged;      // any store (the app's, or a test's): the world map rebuilds its Flight routes layer
        void Fire()
        {
            try { Changed?.Invoke(); } catch (Exception e) { Util.Log("learned flights: " + e.Message); }
            try { AnyChanged?.Invoke(); } catch (Exception e) { Util.Log("learned flights: " + e.Message); }
        }

        // ---------------------------------------------------------------- what the map and the solver use
        // the line to draw / fly for a leg: my learned route; else, unless my newest flight of it matched the game, the Lodge's; else null (the game's route)
        public LearnedRoute Get(int map, int from, int to)
        {
            lock (gate)
            {
                var k = LegKey(map, from, to);
                LegState st = legs.TryGetValue(k, out var l) ? EvalCached(k, l) : null;
                if (st?.Learned != null) return st.Learned;
                if (st != null && st.NewestMatchesGame) return null;
                return LodgeRoute(map, from, to);
            }
        }

        // the duration of my own flights of a leg (0 = none recorded)
        public double Seconds(int map, int from, int to)
        {
            lock (gate)
            {
                var k = LegKey(map, from, to);
                if (legs.TryGetValue(k, out var l)) { var s = EvalCached(k, l).Seconds; if (s > 0) return s; }
                var lr = LodgeRoute(map, from, to);
                return lr?.Seconds ?? 0;
            }
        }

        // every learned route of a continent (mine and the Lodge's), one per leg
        public List<LearnedRoute> All(int map)
        {
            lock (gate)
            {
                var keys = new HashSet<string>(legs.Values.Where(l => l.Map == map).Select(l => KeyOf(l.From, l.To)), StringComparer.Ordinal);
                foreach (var kv in lodge) if (kv.Value.MapId == map) keys.Add(kv.Key);
                var r = new List<LearnedRoute>();
                foreach (var k in keys.OrderBy(x => x, StringComparer.Ordinal))
                {
                    TryKey(k, out var a, out var b);
                    var lr = Get(map, a, b);
                    if (lr != null) r.Add(lr);
                }
                return r;
            }
        }

        // the routes the elevation solver flies on a continent: the game's, with learned lines in place of theirs (same id) and learned legs the files lack added
        // (id below zero). Heights the learned line has no value for come from the game's route nearby, else stay unknown (the solver lifts over the ground).
        readonly Dictionary<int, Tuple<object, int, IReadOnlyList<TaxiRoute>>> mergeCache = new Dictionary<int, Tuple<object, int, IReadOnlyList<TaxiRoute>>>();
        public IReadOnlyList<TaxiRoute> MergeRoutes(int map, IReadOnlyList<TaxiRoute> game)
        {
            game = game ?? new TaxiRoute[0];
            lock (gate)
            {
                if (mergeCache.TryGetValue(map, out var c) && c.Item1 == game && c.Item2 == version) return c.Item3;
                var learned = All(map);
                IReadOnlyList<TaxiRoute> res = game;
                if (learned.Count > 0)
                {
                    var list = game.ToList();
                    foreach (var lr in learned)
                    {
                        int i = list.FindIndex(r => r.From == lr.From && r.To == lr.To);
                        var g = i >= 0 ? list[i] : null;
                        var pts = lr.Pts.Select(p =>
                        {
                            double z = p.Z;
                            if (!p.HasZ && g != null) g.Project(p.X, p.Y, out _, out z);
                            return new[] { p.X, p.Y, double.IsNaN(z) ? double.NaN : z };
                        }).ToList();
                        if (pts.Count < 2) continue;
                        var tr = TaxiRoute.Make(g != null ? g.Id : -(lr.From * 10000 + lr.To % 10000), lr.From, lr.To, map, pts);
                        tr.Learned = true;
                        if (i >= 0) list[i] = tr; else list.Add(tr);
                    }
                    res = list;
                }
                mergeCache[map] = Tuple.Create((object)game, version, res);
                return res;
            }
        }

        // ---------------------------------------------------------------- the file
        void Load()
        {
            try
            {
                var tmp = path + ".tmp";
                if (!File.Exists(path) && File.Exists(tmp)) File.Move(tmp, path);
                if (!File.Exists(path) || new FileInfo(path).Length > MaxFileBytes) return;
                var root = Json.Obj(File.ReadAllText(path, Encoding.UTF8));
                if (root == null || root.Int("v") != 1) return;
                foreach (var lo in root.List("legs").OfType<Dictionary<string, object>>())
                {
                    if (legs.Count >= MaxLegs) break;
                    int map = lo.Int("m"), a = lo.Int("a"), b = lo.Int("b");
                    foreach (var fo in lo.List("f").OfType<Dictionary<string, object>>().Take(MaxFlightsPerLeg))
                    {
                        var f = new ObservedFlight { MapId = map, From = a, To = b, Seq = fo.Long("s"), Build = Clean(fo.Str("b")), Pts = ReadPts(fo.List("p")),
                            Seconds = fo.TryGetValue("t", out var tv) && tv is double t ? t : -1 };
                        if (!Valid(f) || f.Seq <= 0) continue;
                        var k = LegKey(map, a, b);
                        if (!legs.TryGetValue(k, out var l)) legs[k] = l = new Leg { Map = map, From = a, To = b };
                        l.Flights.Add(f);
                        seq = Math.Max(seq, f.Seq);
                    }
                }
            }
            catch (Exception e) { Util.Log("learned flights: the file could not be read (" + e.GetType().Name + "), starting empty"); legs.Clear(); }
        }

        // [x, y, z, x, y, z ...] with z null where the height is unknown
        public static List<FlightGeo.P> ReadPts(List<object> flat)
        {
            var r = new List<FlightGeo.P>();
            if (flat == null || flat.Count % 3 != 0 || flat.Count > MaxPoints * 3) return null;
            for (int i = 0; i + 2 < flat.Count; i += 3)
            {
                if (!(flat[i] is double x) || !(flat[i + 1] is double y)) return null;
                double z = flat[i + 2] is double zz ? zz : double.NaN;
                if (flat[i + 2] != null && !(flat[i + 2] is double)) return null;
                r.Add(new FlightGeo.P(x, y, z));
            }
            return r;
        }

        public static List<object> WritePts(IEnumerable<FlightGeo.P> pts)
        {
            var r = new List<object>();
            foreach (var p in pts) { r.Add(Math.Round(p.X, 1)); r.Add(Math.Round(p.Y, 1)); r.Add(p.HasZ ? (object)Math.Round(p.Z, 1) : null); }
            return r;
        }

        void Save()
        {
            if (string.IsNullOrEmpty(path)) return;
            var tmp = path + ".tmp";
            try
            {
                var lo = new List<object>();
                foreach (var l in legs.Values.OrderBy(l => l.Map).ThenBy(l => l.From).ThenBy(l => l.To))
                    lo.Add(new Dictionary<string, object>
                    {
                        ["m"] = l.Map, ["a"] = l.From, ["b"] = l.To,
                        ["f"] = l.Flights.Select(f => (object)new Dictionary<string, object> { ["s"] = f.Seq, ["t"] = f.Seconds, ["b"] = f.Build ?? "", ["p"] = WritePts(f.Pts) }).ToList(),
                    });
                var text = Json.Write(new Dictionary<string, object> { ["v"] = 1, ["legs"] = lo });
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                File.WriteAllText(tmp, text, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
            }
            catch (Exception e) { Util.Log("learned flights: could not save (" + e.GetType().Name + ")"); try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception) { } }
        }
    }
}
