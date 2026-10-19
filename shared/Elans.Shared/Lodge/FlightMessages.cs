using System.Collections.Generic;

namespace Elans.Shared
{
    // Shared flight times (server/PROTOCOL.md, 2.13) and learned flight paths ("flightroutes"): the field names live only here. The classes
    // only map fields; every rule stays where it was: the server validates flights.share and flights.routes.share from the raw JSON
    // (Flights.TryLeg, FlightRoutes.Parse: a number must be a JSON number, a route's points a clean [x, y, z|null, ...] list) and the
    // Outpost re-validates flights.data and flights.routes.data whatever the server says (FlightShare.ParseServer, LearnedFlights.ParseServer).
    // So the numbers here are strict: double? is null when the field is missing or not a JSON number (ILodgeFields.Num / Nums), and a map
    // entry that is not an object is kept as a null value, so a reader can count it as refused.
    // Maps keyed by ids keep the order they were written / received in.

    // client -> server: ask for the Lodge's flight times (answered with flights.data)
    public sealed class FlightsGetMsg : LodgeEmptyMsg<FlightsGetMsg> { public const string Type = LodgeMsg.FlightsGet; protected override string MsgType => Type; }

    // client -> server: ask for the Lodge's learned flight paths (answered with flights.routes.data)
    public sealed class FlightsRoutesGetMsg : LodgeEmptyMsg<FlightsRoutesGetMsg> { public const string Type = LodgeMsg.FlightsRoutesGet; protected override string MsgType => Type; }

    // a leg's time { t: seconds, n: flights measured }
    public sealed class FlightLeg
    {
        public double? T, N;           // null = missing or not a number (left out when written)

        public void Write(ILodgeWriter w) { if (T is double t) w.Set("t", t); if (N is double n) w.Set("n", n); }
        public static FlightLeg Read(ILodgeFields f) => new FlightLeg { T = f.Num("t"), N = f.Num("n") };
    }

    // reading and writing a map { key: value } in order
    public static class LodgeMap
    {
        // a map of objects: an entry that is not an object reads as a null value
        public static List<KeyValuePair<string, T>> Read<T>(ILodgeFields map, System.Func<ILodgeFields, T> read) where T : class
        {
            var l = new List<KeyValuePair<string, T>>();
            if (map == null) return l;
            foreach (var k in map.Keys()) { var c = map.Child(k); l.Add(new KeyValuePair<string, T>(k, c == null ? null : read(c))); }
            return l;
        }

        // a map of strings: an entry that is not a string reads as null
        public static List<KeyValuePair<string, string>> ReadStrs(ILodgeFields map)
        {
            var l = new List<KeyValuePair<string, string>>();
            if (map == null) return l;
            foreach (var k in map.Keys()) l.Add(new KeyValuePair<string, string>(k, map.Str(k)));
            return l;
        }

        public static void Write<T>(ILodgeWriter w, string key, List<KeyValuePair<string, T>> map, System.Action<T, ILodgeWriter> write)
        {
            var o = w.Child(key);
            foreach (var kv in map) { var c = o.Child(kv.Key); if (kv.Value != null) write(kv.Value, c); }
        }

        public static void WriteStrs(ILodgeWriter w, string key, List<KeyValuePair<string, string>> map)
        {
            var o = w.Child(key);
            foreach (var kv in map) o.Set(kv.Key, kv.Value);
        }
    }

    // client -> server: your measured flight times (at most 500 legs) and the names of the nodes they use (at most 300; left out when none)
    public sealed class FlightsShareMsg
    {
        public const string Type = LodgeMsg.FlightsShare;
        public List<KeyValuePair<string, FlightLeg>> Legs = new List<KeyValuePair<string, FlightLeg>>();   // "<Horde|Alliance>|<id>><id>" -> leg
        public List<KeyValuePair<string, string>> Names;      // node id -> name; null = not sent

        public void Write(ILodgeWriter w)
        {
            w.Set("t", Type);
            LodgeMap.Write(w, "legs", Legs, (l, o) => l.Write(o));
            if (Names != null) LodgeMap.WriteStrs(w, "names", Names);
        }

        public static FlightsShareMsg Read(ILodgeFields f)
        {
            var n = f.Child("names");
            return new FlightsShareMsg { Legs = LodgeMap.Read(f.Child("legs"), FlightLeg.Read), Names = n == null ? null : LodgeMap.ReadStrs(n) };
        }
    }

    // server -> client: the answer to flights.share / flights.routes.share { ok, accepted, rejected, why? }
    public abstract class FlightsAnswerMsg<TSelf> where TSelf : FlightsAnswerMsg<TSelf>, new()
    {
        public bool Ok;
        public int Accepted, Rejected;
        public string Why;             // guest | empty | rate | save; null = not sent
        protected abstract string MsgType { get; }

        public void Write(ILodgeWriter w)
        {
            w.Set("t", MsgType); w.Set("ok", Ok); w.Set("accepted", Accepted); w.Set("rejected", Rejected);
            if (Why != null) w.Set("why", Why);
        }

        public static TSelf Read(ILodgeFields f) => new TSelf { Ok = f.Bool("ok"), Accepted = f.Int("accepted"), Rejected = f.Int("rejected"), Why = f.Str("why") };
    }

    public sealed class FlightsSharedMsg : FlightsAnswerMsg<FlightsSharedMsg> { public const string Type = LodgeMsg.FlightsShared; protected override string MsgType => Type; }
    public sealed class FlightsRoutesSharedMsg : FlightsAnswerMsg<FlightsRoutesSharedMsg> { public const string Type = LodgeMsg.FlightsRoutesShared; protected override string MsgType => Type; }

    // server -> client: the Lodge's merged flight times { at (unix s), legs: { key: { t, n } }, names: { id: name } }
    public sealed class FlightsDataMsg
    {
        public const string Type = LodgeMsg.FlightsData;
        public long At;
        public List<KeyValuePair<string, FlightLeg>> Legs = new List<KeyValuePair<string, FlightLeg>>();
        public List<KeyValuePair<string, string>> Names = new List<KeyValuePair<string, string>>();

        public void Write(ILodgeWriter w)
        {
            w.Set("t", Type); w.Set("at", At);
            LodgeMap.Write(w, "legs", Legs, (l, o) => l.Write(o));
            LodgeMap.WriteStrs(w, "names", Names);
        }

        public static FlightsDataMsg Read(ILodgeFields f) =>
            new FlightsDataMsg { At = f.Long("at"), Legs = LodgeMap.Read(f.Child("legs"), FlightLeg.Read), Names = LodgeMap.ReadStrs(f.Child("names")) };
    }

    // A learned flight path of one leg "<from>><to>" (taxi node ids):
    //   shared by a member:   { m (continent), p: [x, y, z|null, ...], t (seconds, 0 = unknown), n (agreeing flights) } or { m, g: 1 } ("the game's route again")
    //   published by a Lodge: { m, p, t, n, k (members) }
    public sealed class FlightRoute
    {
        public double? M;
        public List<double?> P;        // reading: null when missing or not a clean list of numbers and nulls
        public double? T, N;
        public double? K;              // published only (null = not sent)
        public bool Game;              // { m, g: 1 }

        public void Write(ILodgeWriter w)
        {
            if (M is double m) w.Set("m", m);
            if (Game) { w.Set("g", 1); return; }
            if (P != null) { var l = w.List("p"); foreach (var x in P) { if (x is double d) l.Add(d); else l.AddNull(); } }
            if (T is double t) w.Set("t", t);
            if (N is double n) w.Set("n", n);
            if (K is double k) w.Set("k", k);
        }

        public static FlightRoute Read(ILodgeFields f) => new FlightRoute
        {
            M = f.Num("m"), P = f.Nums("p") is IReadOnlyList<double?> p ? new List<double?>(p) : null, T = f.Num("t"), N = f.Num("n"), K = f.Num("k"), Game = f.Num("g") == 1,
        };
    }

    // client -> server: your learned flight paths (at most 100 per message)
    public sealed class FlightsRoutesShareMsg
    {
        public const string Type = LodgeMsg.FlightsRoutesShare;
        public List<KeyValuePair<string, FlightRoute>> Routes = new List<KeyValuePair<string, FlightRoute>>();

        public void Write(ILodgeWriter w) { w.Set("t", Type); LodgeMap.Write(w, "routes", Routes, (r, o) => r.Write(o)); }
        public static FlightsRoutesShareMsg Read(ILodgeFields f) => new FlightsRoutesShareMsg { Routes = LodgeMap.Read(f.Child("routes"), FlightRoute.Read) };
    }

    // server -> client: the Lodge's published flight paths { at (unix s), routes: { key: route } }
    public sealed class FlightsRoutesDataMsg
    {
        public const string Type = LodgeMsg.FlightsRoutesData;
        public long At;
        public List<KeyValuePair<string, FlightRoute>> Routes = new List<KeyValuePair<string, FlightRoute>>();

        public void Write(ILodgeWriter w) { w.Set("t", Type); w.Set("at", At); LodgeMap.Write(w, "routes", Routes, (r, o) => r.Write(o)); }
        public static FlightsRoutesDataMsg Read(ILodgeFields f) => new FlightsRoutesDataMsg { At = f.Long("at"), Routes = LodgeMap.Read(f.Child("routes"), FlightRoute.Read) };
    }
}
