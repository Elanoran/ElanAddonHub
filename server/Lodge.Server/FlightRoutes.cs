using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Elans.Shared;

namespace Lodge;

// Learned flight paths (feature "flightroutes"; the server version is set at release). WoW Forever flies some flight paths differently from the routes in the game files; the Outpost
// records the line its member's flights really took and, once two of their flights agree on a line that differs from the game's, shares it here.
// A contribution is one leg "<fromNode>><toNode>" (taxi node ids) on a continent: a simplified line [x, y, z|null, ...] (2-64 points, world yards),
// the average duration and n (how many of the member's flights agree, 1-50) - or { g: 1 }: "my newest flight flew the game's route again".
// The pool publishes a leg's route when contributions that agree with each other (both lines resampled to 32 points, mean distance <= 25 and
// largest <= 75 yards, both ways) add up to n >= 2 (one member's two agreeing flights, or two members), and no member said "the game's route
// again" after the newest of them. The published line is the average of the agreeing ones.
// The server does not know where the flight masters stand, so it checks what it can (sizes, numbers, a plausible length for the straight
// distance and the duration); the Outpost checks the ends against its own game files before it uses a route.
// TODO(Cairn): learned routes are not offered to the Cairn yet. When they are, the Cairn would take the published lines only (no identities),
// keyed like here, behind its own record kind - a follow-up in Cairn.cs, deliberately not done here.
public static partial class FlightRoutes
{
    public const int MaxRoutesPerShare = 100;
    public const int MaxLegs = 3000;               // distinct keys kept
    public const int MaxLegsPerIdentity = 500;
    public const int MaxContributorsPerLeg = 20;
    public const int MaxPoints = 64, MinPoints = 2;
    public const int MaxSamples = 50;
    public const int ExpireDays = 120;
    public const double MaxCoord = 20000, MaxZ = 5000, MinStraight = 50, MaxLength = 30000;
    public const double MinSeconds = 5, MaxSeconds = 1800, MinSpeed = 3, MaxSpeed = 100;
    public const double AgreeMean = 25, AgreeMax = 75;
    public const int Resample = 32;
    public static readonly TimeSpan ShareEvery = TimeSpan.FromMinutes(10);

    [GeneratedRegex(@"^([1-9][0-9]{0,6})>([1-9][0-9]{0,6})\z", RegexOptions.CultureInvariant)]
    private static partial Regex KeyRx();

    public static bool ValidKey(string key)
    {
        if (key == null || key.Length > 20) return false;
        var m = KeyRx().Match(key);
        return m.Success && m.Groups[1].Value != m.Groups[2].Value;
    }

    public struct P { public double X, Y, Z; public bool HasZ; }

    public sealed class Contribution
    {
        public int Map; public P[] Pts; public double T; public int N; public bool Game; public long At;
    }

    static bool Num(JsonNode n, out double d)
    {
        d = 0;
        if (n is JsonValue v && !v.TryGetValue<string>(out _) && !v.TryGetValue<bool>(out _) && v.TryGetValue<double>(out d)) return !double.IsNaN(d) && !double.IsInfinity(d);
        return false;
    }

    static double Dist(P a, P b) { double dx = a.X - b.X, dy = a.Y - b.Y; return Math.Sqrt(dx * dx + dy * dy); }
    public static double Length(P[] p) { double s = 0; for (int i = 1; i < p.Length; i++) s += Dist(p[i - 1], p[i]); return s; }

    // one contribution from the wire (or the file): null = refused. `why` says which rule (never logged with the content).
    public static Contribution Parse(JsonNode node, out string why)
    {
        why = "shape";
        if (node is not JsonObject o) return null;
        if (!Num(o["m"], out var md) || md < 0 || md > 100000 || md != Math.Floor(md)) { why = "map"; return null; }
        if (o["g"] is JsonNode gn)
        {
            if (!Num(gn, out var g) || g != 1 || o.Count != 2) return null;
            return new Contribution { Map = (int)md, Game = true, Pts = Array.Empty<P>() };
        }
        if (o["p"] is not JsonArray pa || pa.Count % 3 != 0 || pa.Count < MinPoints * 3 || pa.Count > MaxPoints * 3) { why = "points"; return null; }
        var pts = new P[pa.Count / 3];
        for (int i = 0; i < pts.Length; i++)
        {
            if (!Num(pa[i * 3], out var x) || !Num(pa[i * 3 + 1], out var y) || Math.Abs(x) > MaxCoord || Math.Abs(y) > MaxCoord) { why = "points"; return null; }
            var zn = pa[i * 3 + 2];
            double z = 0; bool hz = false;
            if (zn != null) { if (!Num(zn, out z) || Math.Abs(z) > MaxZ) { why = "points"; return null; } hz = true; }
            pts[i] = new P { X = Math.Round(x, 1), Y = Math.Round(y, 1), Z = hz ? Math.Round(z, 1) : 0, HasZ = hz };
        }
        if (!Num(o["n"], out var nd) || nd < 1 || nd > MaxSamples || nd != Math.Floor(nd)) { why = "n"; return null; }
        if (!Num(o["t"], out var t) || !(t == 0 || (t >= MinSeconds && t <= MaxSeconds))) { why = "time"; return null; }
        double len = Length(pts), straight = Dist(pts[0], pts[^1]);
        if (straight < MinStraight || len > MaxLength || len > straight * 3 + 2000 || len < straight) { why = "length"; return null; }
        if (t > 0) { var sp = len / t; if (sp < MinSpeed || sp > MaxSpeed) { why = "speed"; return null; } }
        return new Contribution { Map = (int)md, Pts = pts, T = Math.Round(t, 1, MidpointRounding.AwayFromZero), N = (int)nd };
    }

    public static P[] ResampleLine(P[] pts, int n)
    {
        var r = new P[n];
        double total = Length(pts);
        if (total <= 1e-9) { for (int k = 0; k < n; k++) r[k] = pts[0]; return r; }
        int i = 0; double acc = 0;
        for (int k = 0; k < n; k++)
        {
            double want = total * k / (n - 1);
            while (i + 1 < pts.Length - 1 && acc + Dist(pts[i], pts[i + 1]) < want) { acc += Dist(pts[i], pts[i + 1]); i++; }
            double seg = Dist(pts[i], pts[i + 1]), f = seg <= 1e-9 ? 0 : Math.Clamp((want - acc) / seg, 0, 1);
            var a = pts[i]; var b = pts[i + 1];
            r[k] = new P { X = a.X + (b.X - a.X) * f, Y = a.Y + (b.Y - a.Y) * f, HasZ = a.HasZ && b.HasZ, Z = a.HasZ && b.HasZ ? a.Z + (b.Z - a.Z) * f : 0 };
        }
        r[n - 1] = pts[^1];
        return r;
    }

    static double SegDist(P p, P a, P b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, l2 = dx * dx + dy * dy;
        double t = l2 <= 1e-12 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / l2, 0, 1);
        double qx = a.X + dx * t - p.X, qy = a.Y + dy * t - p.Y;
        return Math.Sqrt(qx * qx + qy * qy);
    }

    static double ToLine(P p, P[] line) { double best = double.MaxValue; for (int i = 0; i + 1 < line.Length; i++) best = Math.Min(best, SegDist(p, line[i], line[i + 1])); return best; }

    // both ways: each line resampled, each point's distance to the other line
    public static (double Mean, double Max) Deviation(P[] a, P[] b)
    {
        double sum = 0, max = 0; int n = 0;
        foreach (var p in ResampleLine(a, Resample)) { var d = ToLine(p, b); sum += d; n++; max = Math.Max(max, d); }
        foreach (var p in ResampleLine(b, Resample)) { var d = ToLine(p, a); sum += d; n++; max = Math.Max(max, d); }
        return (sum / Math.Max(1, n), max);
    }

    public static bool Agree(P[] a, P[] b) { var (mean, max) = Deviation(a, b); return mean <= AgreeMean && max <= AgreeMax; }

    public sealed class Published { public int Map; public P[] Pts; public double T; public int N; public int K; }

    // The pooled route of one leg from every identity's latest contribution (deterministic from the stored data). Null = nothing to publish.
    public static Published Merge(IReadOnlyDictionary<string, Contribution> byIdentity)
    {
        var routes = byIdentity.Where(kv => !kv.Value.Game && kv.Value.Pts.Length >= MinPoints).OrderByDescending(kv => kv.Value.At).ThenBy(kv => kv.Key, StringComparer.Ordinal).ToList();
        if (routes.Count == 0) return null;
        List<KeyValuePair<string, Contribution>> best = null; int bestN = 0;
        foreach (var anchor in routes)
        {
            var cluster = routes.Where(o => o.Value.Map == anchor.Value.Map && (o.Key == anchor.Key || Agree(o.Value.Pts, anchor.Value.Pts))).ToList();
            int n = cluster.Sum(c => c.Value.N);
            if (n < 2) continue;
            if (best == null || cluster.Count > best.Count || (cluster.Count == best.Count && n > bestN)) { best = cluster; bestN = n; }
        }
        if (best == null) return null;
        // members whose newest word is "the game's route again", after the newest report of the route: as many as the route's reporters = not published
        long newest = best.Max(c => c.Value.At);
        int map = best[0].Value.Map;
        int reverts = byIdentity.Count(kv => kv.Value.Game && kv.Value.Map == map && kv.Value.At > newest);
        if (reverts >= best.Count) return null;
        var rs = best.Select(c => ResampleLine(c.Value.Pts, 48)).ToList();
        var avg = new P[48];
        for (int k = 0; k < 48; k++)
        {
            double x = 0, y = 0, z = 0; int nz = 0;
            foreach (var l in rs) { x += l[k].X; y += l[k].Y; if (l[k].HasZ) { z += l[k].Z; nz++; } }
            avg[k] = new P { X = Math.Round(x / rs.Count, 1), Y = Math.Round(y / rs.Count, 1), HasZ = nz > 0, Z = nz > 0 ? Math.Round(z / nz, 1) : 0 };
        }
        var ts = best.Where(c => c.Value.T > 0).ToList();
        double t = ts.Count == 0 ? 0 : Math.Round(ts.Sum(c => c.Value.T * c.Value.N) / ts.Sum(c => c.Value.N), 1, MidpointRounding.AwayFromZero);
        return new Published { Map = map, Pts = avg, T = t, N = Math.Min(MaxSamples, bestN), K = best.Count };
    }

    // the same flat list for a typed FlightRoute (FlightsRoutesDataMsg)
    public static List<double?> FlatNums(P[] pts)
    {
        var l = new List<double?>(pts.Length * 3);
        foreach (var p in pts) { l.Add(p.X); l.Add(p.Y); l.Add(p.HasZ ? p.Z : null); }
        return l;
    }

    public static JsonArray Flat(P[] pts)
    {
        var a = new JsonArray();
        foreach (var p in pts) { a.Add(p.X); a.Add(p.Y); a.Add(p.HasZ ? JsonValue.Create(p.Z) : null); }
        return a;
    }
}

// data/flightroutes.json = { "v":1, "legs": { key: { identity: { m, p | g, t, n, at } } } }. Atomic writes, size-capped load, every loaded value
// re-validated, contributions older than 120 days dropped. Never logs any of its content.
public sealed class FlightRouteStore
{
    const long MaxFileBytes = 24 * 1024 * 1024;
    readonly string path, tmp;
    readonly ILogger log;
    readonly IClock clock;
    public readonly object Gate = new();
    readonly Dictionary<string, Dictionary<string, FlightRoutes.Contribution>> legs = new(StringComparer.Ordinal);
    readonly Dictionary<string, DateTime> lastShare = new(StringComparer.Ordinal);
    string cached; long cachedVersion = -1, version;

    public FlightRouteStore(string dataDir, ILogger log, IClock clock)
    {
        this.log = log; this.clock = clock;
        path = Path.Combine(dataDir, "flightroutes.json");
        tmp = path + ".tmp";
        try
        {
            if (File.Exists(tmp)) { if (!File.Exists(path)) File.Move(tmp, path); else File.Delete(tmp); }
            if (File.Exists(path) && new FileInfo(path).Length <= MaxFileBytes)
            {
                var root = JsonNode.Parse(File.ReadAllText(path))?.AsObject();
                if (root?["legs"] is JsonObject lo)
                    foreach (var (key, v) in lo)
                    {
                        if (legs.Count >= FlightRoutes.MaxLegs) break;
                        if (!FlightRoutes.ValidKey(key) || v is not JsonObject cs) continue;
                        var d = new Dictionary<string, FlightRoutes.Contribution>(StringComparer.Ordinal);
                        foreach (var (who, c) in cs)
                        {
                            if (d.Count >= FlightRoutes.MaxContributorsPerLeg || who.Length == 0 || who.Length > 64 || c is not JsonObject co) continue;
                            long at = co["at"] is JsonValue av && av.TryGetValue<long>(out var l) ? l : 0;
                            var copy = co.DeepClone().AsObject(); copy.Remove("at");
                            var parsed = FlightRoutes.Parse(copy, out _);
                            if (parsed == null) continue;
                            parsed.At = at;
                            d[who] = parsed;
                        }
                        if (d.Count > 0) legs[key] = d;
                    }
            }
        }
        catch (Exception e) { log?.LogWarning("Flight routes: load failed: {Error}", e.GetType().Name); }
        lock (Gate) Prune();
    }

    public long NowMs => new DateTimeOffset(clock.UtcNow).ToUnixTimeMilliseconds();
    public int LegCount { get { lock (Gate) return legs.Count; } }
    public int ContributionCount { get { lock (Gate) return legs.Values.Sum(d => d.Count); } }

    // one share per identity per 10 minutes (kept here, not in the Budget, so the flight-time slot is not shared). Caller holds Gate.
    public bool TakeSlot(string identity)
    {
        var now = clock.UtcNow;
        if (lastShare.TryGetValue(identity, out var t) && now - t < FlightRoutes.ShareEvery) return false;
        lastShare[identity] = now;
        if (lastShare.Count > 5000) foreach (var k in lastShare.Where(kv => now - kv.Value > FlightRoutes.ShareEvery).Select(kv => kv.Key).ToList()) lastShare.Remove(k);
        return true;
    }

    public int Prune()
    {
        var cutoff = NowMs - FlightRoutes.ExpireDays * 86_400_000L;
        int n = 0;
        foreach (var key in legs.Keys.ToList())
        {
            var d = legs[key];
            foreach (var who in d.Where(kv => kv.Value.At < cutoff).Select(kv => kv.Key).ToList()) { d.Remove(who); n++; }
            if (d.Count == 0) legs.Remove(key);
        }
        if (n > 0) version++;
        return n;
    }

    public int RemoveIdentity(string identity)
    {
        int n = 0;
        foreach (var key in legs.Keys.ToList())
        {
            if (legs[key].Remove(identity)) n++;
            if (legs[key].Count == 0) legs.Remove(key);
        }
        if (n > 0) version++;
        return n;
    }

    public sealed class ShareResult { public int Accepted, Rejected, Unchanged; public bool Changed; }

    static bool Same(FlightRoutes.Contribution a, FlightRoutes.Contribution b)
    {
        if (a.Game != b.Game || a.Map != b.Map || a.N != b.N || a.T != b.T || a.Pts.Length != b.Pts.Length) return false;
        for (int i = 0; i < a.Pts.Length; i++) if (a.Pts[i].X != b.Pts[i].X || a.Pts[i].Y != b.Pts[i].Y || a.Pts[i].HasZ != b.Pts[i].HasZ || a.Pts[i].Z != b.Pts[i].Z) return false;
        return true;
    }

    // Stores one identity's routes. Invalid entries count as rejected; a "game again" report for a leg nobody reported is not kept. Caller holds Gate.
    public ShareResult Share(string identity, JsonObject routesIn)
    {
        var r = new ShareResult();
        var now = NowMs;
        int mine = legs.Values.Count(d => d.ContainsKey(identity));
        int seen = 0;
        foreach (var (key, node) in routesIn)
        {
            if (++seen > FlightRoutes.MaxRoutesPerShare) { r.Rejected++; continue; }
            var c = FlightRoutes.ValidKey(key) ? FlightRoutes.Parse(node, out _) : null;
            if (c == null) { r.Rejected++; continue; }
            c.At = now;
            if (!legs.TryGetValue(key, out var d))
            {
                if (c.Game) { r.Unchanged++; continue; }                       // nothing to take back
                if (legs.Count >= FlightRoutes.MaxLegs || mine >= FlightRoutes.MaxLegsPerIdentity) { r.Rejected++; continue; }
                d = legs[key] = new Dictionary<string, FlightRoutes.Contribution>(StringComparer.Ordinal);
            }
            if (d.TryGetValue(identity, out var old))
            {
                if (Same(old, c))
                {
                    r.Unchanged++;
                    if (now - old.At > 30 * 86_400_000L) { old.At = now; r.Changed = true; version++; }
                    continue;
                }
                d[identity] = c;
            }
            else
            {
                if (mine >= FlightRoutes.MaxLegsPerIdentity) { r.Rejected++; if (d.Count == 0) legs.Remove(key); continue; }
                if (d.Count >= FlightRoutes.MaxContributorsPerLeg) d.Remove(d.OrderBy(kv => kv.Value.At).First().Key);
                d[identity] = c; mine++;
            }
            r.Accepted++; r.Changed = true; version++;
        }
        return r;
    }

    // { t:"flights.routes.data", at, routes: { key: { m, p, t, n, k } } } (typed: FlightsRoutesDataMsg). Caller holds Gate.
    public string MergedJson()
    {
        if (cached != null && cachedVersion == version) return cached;
        var msg = new FlightsRoutesDataMsg { At = NowMs / 1000 };
        foreach (var (key, d) in legs.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var p = FlightRoutes.Merge(d);
            if (p != null) msg.Routes.Add(new(key, new FlightRoute { M = p.Map, P = FlightRoutes.FlatNums(p.Pts), T = p.T, N = p.N, K = p.K }));
        }
        cached = LodgeJson.Build(msg.Write).ToJsonString();
        cachedVersion = version;
        return cached;
    }

    public bool Save()
    {
        try
        {
            var lo = new JsonObject();
            foreach (var (key, d) in legs)
            {
                var co = new JsonObject();
                foreach (var (who, c) in d)
                {
                    var o = c.Game ? new JsonObject { ["m"] = c.Map, ["g"] = 1 } : new JsonObject { ["m"] = c.Map, ["p"] = FlightRoutes.Flat(c.Pts), ["t"] = c.T, ["n"] = c.N };
                    o["at"] = c.At;
                    co[who] = o;
                }
                lo[key] = co;
            }
            var bytes = Encoding.UTF8.GetBytes(new JsonObject { ["v"] = 1, ["legs"] = lo }.ToJsonString());
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception e)
        {
            log?.LogWarning("Flight routes: save failed: {Error}", e.GetType().Name);
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            return false;
        }
    }
}
