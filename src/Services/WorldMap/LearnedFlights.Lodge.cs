using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using ElansAddonHub.Services.Maps;
using Elans.Shared;
using ElansAddonHub.Lodge;

namespace ElansAddonHub.Services.WorldMap
{
    // The Lodge's side of learned flight paths (server feature "flightroutes"; LodgeSession.FlightRoutes.cs sends and receives the frames).
    //   upload    per leg what my flights say: my learned route (n = how many flights agree), else my newest deviating flight alone (n = 1), else - when my newest
    //             flight flew the game's route again and I said otherwise before - { g: 1 } ("the game's route is right again"). Only the leg (two taxi node
    //             ids), the continent, the simplified line, the average duration and n: no names, no times of day, no character.
    //   download  the Lodge's learned routes (reports of >= 2 members, or one member's >= 2 agreeing flights) are re-checked here: the key, the size, the numbers,
    //             the ends near the two flight masters, a sane length, and that it really differs from the game's route. Kept in memory only.
    public sealed partial class LearnedFlights
    {
        public const int MaxShareRoutes = 100, MaxLodgeRoutes = 3000;
        public const int MaxShareBytes = 56 * 1024;

        readonly Dictionary<string, LearnedRoute> lodge = new Dictionary<string, LearnedRoute>(StringComparer.Ordinal);       // "<from>><to>" -> route
        readonly Dictionary<string, LearnedRoute> lodgeCache = new Dictionary<string, LearnedRoute>(StringComparer.Ordinal);
        public int LodgeCount { get { lock (gate) return lodge.Count; } }

        // a Lodge route for a leg, if it passes the checks against this game's flight masters and routes (cached until the next download / change)
        LearnedRoute LodgeRoute(int map, int from, int to)
        {
            var k = KeyOf(from, to);
            if (!lodge.TryGetValue(k, out var r) || r.MapId != map) return null;
            var ck = map + ":" + k;
            if (lodgeCache.TryGetValue(ck, out var c)) return c;
            c = UsableLodge(r) ? r : null;
            lodgeCache[ck] = c;
            return c;
        }

        bool UsableLodge(LearnedRoute r)
        {
            if (r.Pts.Count < 2) return false;
            var a = NodeOf(r.MapId, r.From); var b = NodeOf(r.MapId, r.To);
            if (a == null || b == null) return false;                                  // masters this game does not have on that continent
            var p0 = r.Pts[0]; var p1 = r.Pts[r.Pts.Count - 1];
            if (FlightGeo.Dist(p0.X, p0.Y, a.X, a.Y) > FlightRecorder.EndYards || FlightGeo.Dist(p1.X, p1.Y, b.X, b.Y) > FlightRecorder.EndYards) return false;
            double straight = FlightGeo.Dist(a.X, a.Y, b.X, b.Y), len = FlightGeo.Length(r.Pts);
            if (len < straight * 0.8 || len > straight * 3 + 2000) return false;
            var game = GameRoute(r.MapId, r.From, r.To);
            r.GameHasRoute = game != null;
            return Classify(r.Pts, game, out r.VsGame) == FlightFit.Deviates;          // the Lodge's line must really differ from the game's
        }

        // flights.routes.data -> validated routes (whatever the server says); Dropped counts what failed
        public sealed class LodgeData { public Dictionary<string, LearnedRoute> Routes = new Dictionary<string, LearnedRoute>(StringComparer.Ordinal); public int Dropped; public long At; }

        // typed: FlightsRoutesDataMsg, whose numbers are strict (missing or not a JSON number = null) and whose points are null unless a clean list
        public static LodgeData ParseServer(Dictionary<string, object> m)
        {
            var r = new LodgeData();
            if (m == null) return r;
            var msg = FlightsRoutesDataMsg.Read(new LodgeFieldsDict(m));
            r.At = msg.At; if (r.At < 0 || r.At > 4_000_000_000L) r.At = 0;
            foreach (var kv in msg.Routes)
            {
                if (r.Routes.Count >= MaxLodgeRoutes) { r.Dropped++; continue; }
                var d = kv.Value;
                if (!TryKey(kv.Key, out var a, out var b) || d == null) { r.Dropped++; continue; }
                var pts = d.P == null ? null : ReadPts(d.P);
                double t = d.T ?? -1, n = d.N ?? -1, k = d.K ?? 0;
                double map = d.M ?? -1;
                var lr = new LearnedRoute { MapId = (int)map, From = a, To = b, Pts = pts, Seconds = t, Flights = (int)n, Members = (int)k, FromLodge = true };
                if (pts == null || map < 0 || map > 100000 || map != Math.Floor(map) || n < 2 || n > 50 || n != Math.Floor(n) || k < 0 || k > 50 || !(t == 0 || (t >= 5 && t <= 1800))
                    || !Valid(new ObservedFlight { MapId = lr.MapId, From = a, To = b, Pts = pts, Seconds = Math.Max(0, t) })) { r.Dropped++; continue; }
                r.Routes[kv.Key] = lr;
            }
            return r;
        }

        public void SetLodge(LodgeData data)
        {
            lock (gate)
            {
                lodge.Clear(); lodgeCache.Clear();
                if (data != null) foreach (var kv in data.Routes) lodge[kv.Key] = kv.Value;
                version++;
            }
            Fire();
        }

        // ---------------------------------------------------------------- the upload
        public sealed class ShareBatch
        {
            public Dictionary<string, object> Frame;                               // flights.routes.share
            public Dictionary<string, string> Sigs = new Dictionary<string, string>(StringComparer.Ordinal);
            public int Remaining;
        }

        // what I would tell the Lodge per leg (key -> entry), with a signature to send only what changed. `sent` = what the Lodge already has from me:
        // a { g: 1 } is only sent for a leg I reported a route for before.
        public Dictionary<string, Tuple<FlightRoute, string>> ShareEntries(IDictionary<string, string> sent)
        {
            var res = new Dictionary<string, Tuple<FlightRoute, string>>(StringComparer.Ordinal);
            foreach (var st in States().OrderBy(s => s.MapId).ThenBy(s => s.From).ThenBy(s => s.To))
            {
                var k = KeyOf(st.From, st.To);
                if (res.ContainsKey(k)) continue;                                       // the same two ids on two continents: one is enough
                FlightRoute e = null; string sig = null;
                if (st.Learned != null) { e = Entry(st.MapId, st.Learned.Pts, st.Learned.Seconds, st.Learned.Flights); }
                else if (st.Single != null) { e = Entry(st.MapId, st.Single.Pts, st.Single.Seconds, 1); }
                else if (st.NewestMatchesGame && sent != null && sent.TryGetValue(k, out var was) && !was.StartsWith("g")) { e = new FlightRoute { M = st.MapId, Game = true }; sig = "g"; }
                if (e == null) continue;
                sig = sig ?? Sig(e);
                res[k] = Tuple.Create(e, sig);
            }
            return res;
        }

        static FlightRoute Entry(int map, List<FlightGeo.P> pts, double secs, int n) => new FlightRoute
        {
            M = map, P = WritePts(pts).Select(o => o is double d ? d : (double?)null).ToList(), T = secs >= 5 && secs <= 1800 ? Math.Round(secs, 1) : 0.0, N = Math.Max(1, Math.Min(50, n)),
        };

        // the signature of what was sent (kept in flightroutes-sent.txt): "r<n>|<t>|<hash of the points>", as it was computed from the old dictionary
        static string Sig(FlightRoute e)
        {
            var sb = new StringBuilder("r").Append(((int)e.N.Value).ToString(CultureInfo.InvariantCulture)).Append('|').Append(e.T.Value.ToString(CultureInfo.InvariantCulture)).Append('|');
            long h = 17; foreach (var o in e.P) h = unchecked(h * 31 + (o is double d ? (long)Math.Round(d * 10) : -7));
            return sb.Append(h.ToString("x", CultureInfo.InvariantCulture)).ToString();
        }

        // the changed entries as one frame: at most 100 routes and 56 KB. Null when nothing changed.
        public ShareBatch MakeBatch(IDictionary<string, string> sent)
        {
            var all = ShareEntries(sent);
            var changed = all.Where(kv => sent == null || !sent.TryGetValue(kv.Key, out var s) || s != kv.Value.Item2).OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();
            if (changed.Count == 0) return null;
            var b = new ShareBatch();
            var msg = new FlightsRoutesShareMsg();
            int bytes = 64;
            foreach (var kv in changed)
            {
                int cost = Json.Write(LodgeFieldsDict.Build(kv.Value.Item1.Write)).Length + kv.Key.Length + 8;
                if (msg.Routes.Count >= MaxShareRoutes || bytes + cost > MaxShareBytes) break;
                msg.Routes.Add(new KeyValuePair<string, FlightRoute>(kv.Key, kv.Value.Item1)); b.Sigs[kv.Key] = kv.Value.Item2; bytes += cost;
            }
            b.Remaining = changed.Count - msg.Routes.Count;
            b.Frame = LodgeFieldsDict.Build(msg.Write);
            return b;
        }
    }
}
