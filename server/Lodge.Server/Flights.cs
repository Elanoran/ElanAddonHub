using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Elans.Shared;

namespace Lodge;

// Crowd-shared flight-path leg times (2.13). A leg is "<faction>|<idA>><idB>" with a duration in seconds and the number of
// flights it was measured over. Pure rules live here (validation, aggregation); FlightStore keeps them in <data>/flights.json.
public static partial class Flights
{
    public const int MaxLegsPerShare = 500;
    public const int MaxNamesPerShare = 300;
    public const int MaxLegs = 5000;               // distinct keys kept
    public const int MaxLegsPerIdentity = 1500;    // one member can't fill the table alone
    public const int MaxContributorsPerLeg = 20;
    public const int MaxNames = 3000;
    public const int MaxNameLength = 60;
    public const int MaxSamples = 50;
    public const int ExpireDays = 120;
    public const double MinSeconds = 1, MaxSeconds = 1800;
    public const double OutlierFraction = 0.40;    // more than +-40% from an established aggregate ...
    public const int EstablishedSamples = 5;       // ... of at least this many samples is held back until confirmed
    public static readonly TimeSpan ShareEvery = TimeSpan.FromMinutes(10);

    // an id is a taxi node id ("11") or, when the addon had none, a node name: letters, digits, space and ' . , - only
    const string Id = @"[\p{L}\p{N}][\p{L}\p{N} '.,\-]{0,30}[\p{L}\p{N}.]|[\p{L}\p{N}]";
    [GeneratedRegex(@"^(Horde|Alliance)\|(" + Id + @")>(" + Id + @")\z", RegexOptions.CultureInvariant)]
    private static partial Regex KeyRx();
    [GeneratedRegex("^(" + Id + @")\z", RegexOptions.CultureInvariant)]
    private static partial Regex IdRx();

    public static bool ValidKey(string key)
    {
        if (key == null || key.Length > 80) return false;
        var m = KeyRx().Match(key);
        return m.Success && m.Groups[2].Value != m.Groups[3].Value;
    }

    public static bool ValidId(string id) => id != null && id.Length <= 32 && IdRx().IsMatch(id);

    // the readable name of a node: cleaned like any text, cut at 60, without the characters a Lua/JSON string would need to escape
    public static string CleanName(string s)
    {
        var r = Names.Text(s ?? "", MaxNameLength * 2);
        r = new string(r.Where(c => c is not ('"' or '\\' or '<' or '>' or '|' or '[' or ']')).ToArray()).Trim();
        return Names.Cut(r, MaxNameLength).Trim();
    }

    public static bool TryLeg(JsonNode node, out double t, out int n)
    {
        t = 0; n = 0;
        if (node is not JsonObject o) return false;
        if (!Num(o["t"], out var td) || !Num(o["n"], out var nd)) return false;
        if (td < MinSeconds || td > MaxSeconds || nd < 1 || nd > MaxSamples || nd != Math.Floor(nd)) return false;
        t = Math.Round(td, 1, MidpointRounding.AwayFromZero); n = (int)nd;
        return true;
    }

    static bool Num(JsonNode n, out double d)
    {
        d = 0;
        if (n is JsonValue v && !v.TryGetValue<string>(out _) && !v.TryGetValue<bool>(out _) && v.TryGetValue<double>(out d)) return !double.IsNaN(d) && !double.IsInfinity(d);
        return false;
    }

    public sealed class Contribution
    {
        public double T; public int N; public long At;  // At: when the server stored this value (unix ms)
    }

    public sealed class Agg { public double T; public int N; }

    // The merged value of one leg from every identity's latest contribution. Deterministic from the stored data alone:
    // contributions are taken in the order they were stored; one that lands more than 40 % away from an aggregate that already
    // holds >= 5 samples is held back, and it joins only when ANOTHER held-back identity agrees with it within 40 % (confirmed).
    // The result is the sample-weighted mean of the accepted ones, with n summed and capped at 50. Null: nothing accepted.
    public static Agg Merge(IReadOnlyDictionary<string, Contribution> byIdentity)
    {
        if (byIdentity.Count == 0) return null;
        var ordered = byIdentity.OrderBy(kv => kv.Value.At).ThenBy(kv => kv.Key, StringComparer.Ordinal).ToList();
        var accepted = new List<KeyValuePair<string, Contribution>>();
        var held = new List<KeyValuePair<string, Contribution>>();
        foreach (var kv in ordered)
        {
            var cur = Weighted(accepted.Select(a => a.Value));
            if (cur != null && cur.N >= EstablishedSamples && Math.Abs(kv.Value.T - cur.T) > OutlierFraction * cur.T) held.Add(kv);
            else accepted.Add(kv);
        }
        // confirmed = another identity ALSO landed far from the aggregate and agrees with this one (two independent outliers that
        // match each other); an ordinary contribution nearby does not rescue a stray value
        var confirmed = held.Where(h => held.Any(o => o.Key != h.Key && Math.Abs(o.Value.T - h.Value.T) <= OutlierFraction * h.Value.T)).ToList();
        accepted.AddRange(confirmed);
        return Weighted(accepted.Select(a => a.Value));
    }

    static Agg Weighted(IEnumerable<Contribution> cs)
    {
        double sum = 0; long n = 0;
        foreach (var c in cs) { sum += c.T * c.N; n += c.N; }
        if (n == 0) return null;
        return new Agg { T = Math.Round(sum / n, 1, MidpointRounding.AwayFromZero), N = (int)Math.Min(n, MaxSamples) };
    }
}

// data/flights.json = { "v":1, "legs": { key: { identity: {t,n,at} } }, "names": { id: { identity: name } } }
// Atomic (tmp, flush, rename; a leftover tmp is used only when the main file is missing), size-capped on load, every loaded
// value re-validated, contributions older than 120 days dropped. Never logs any of its content.
public sealed class FlightStore
{
    const long MaxFileBytes = 12 * 1024 * 1024;
    readonly string path, tmp;
    readonly ILogger log;
    readonly IClock clock;
    public readonly object Gate = new();
    readonly Dictionary<string, Dictionary<string, Flights.Contribution>> legs = new(StringComparer.Ordinal);
    readonly Dictionary<string, Dictionary<string, string>> names = new(StringComparer.Ordinal);
    string cached; long cachedVersion = -1, version;

    public FlightStore(string dataDir, ILogger log, IClock clock)
    {
        this.log = log; this.clock = clock;
        path = Path.Combine(dataDir, "flights.json");
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
                        if (legs.Count >= Flights.MaxLegs) break;
                        if (!Flights.ValidKey(key) || v is not JsonObject cs) continue;
                        var d = new Dictionary<string, Flights.Contribution>(StringComparer.Ordinal);
                        foreach (var (who, c) in cs)
                        {
                            if (d.Count >= Flights.MaxContributorsPerLeg || who.Length == 0 || who.Length > 64) continue;
                            if (!Flights.TryLeg(c, out var t, out var n)) continue;
                            long at = c is JsonObject co && co["at"] is JsonValue av && av.TryGetValue<long>(out var l) ? l : 0;
                            d[who] = new Flights.Contribution { T = t, N = n, At = at };
                        }
                        if (d.Count > 0) legs[key] = d;
                    }
                if (root?["names"] is JsonObject no)
                    foreach (var (id, v) in no)
                    {
                        if (names.Count >= Flights.MaxNames) break;
                        if (!Flights.ValidId(id) || v is not JsonObject ws) continue;
                        var d = new Dictionary<string, string>(StringComparer.Ordinal);
                        foreach (var (who, nm) in ws)
                        {
                            var s = nm is JsonValue sv && sv.TryGetValue<string>(out var str) ? Flights.CleanName(str) : "";
                            if (s.Length > 0 && who.Length <= 64 && d.Count < Flights.MaxContributorsPerLeg) d[who] = s;
                        }
                        if (d.Count > 0) names[id] = d;
                    }
            }
        }
        catch (Exception e) { log?.LogWarning("Flights: load failed: {Error}", e.GetType().Name); }
        lock (Gate) Prune();
    }

    public long NowMs => new DateTimeOffset(clock.UtcNow).ToUnixTimeMilliseconds();
    public int LegCount { get { lock (Gate) return legs.Count; } }
    public int ContributionCount { get { lock (Gate) return legs.Values.Sum(d => d.Count); } }
    public int CountOf(string identity) { lock (Gate) return legs.Values.Count(d => d.ContainsKey(identity)); }
    public Dictionary<string, int> CountsByIdentity() { lock (Gate) { var r = new Dictionary<string, int>(StringComparer.Ordinal); foreach (var d in legs.Values) foreach (var k in d.Keys) r[k] = r.GetValueOrDefault(k) + 1; return r; } }

    // Drops contributions older than 120 days (and legs / names left empty). Caller holds Gate. Returns how many contributions went.
    public int Prune()
    {
        var cutoff = NowMs - Flights.ExpireDays * 86_400_000L;
        int n = 0;
        foreach (var key in legs.Keys.ToList())
        {
            var d = legs[key];
            foreach (var who in d.Where(kv => kv.Value.At < cutoff).Select(kv => kv.Key).ToList()) { d.Remove(who); n++; }
            if (d.Count == 0) legs.Remove(key);
        }
        // names of an identity with no leg left are not kept; names nobody uses are not kept either
        var used = legs.Keys.SelectMany(IdsOf).ToHashSet();
        var live = legs.Values.SelectMany(d => d.Keys).ToHashSet();
        foreach (var id in names.Keys.ToList())
        {
            if (!used.Contains(id)) { names.Remove(id); continue; }
            var d = names[id];
            foreach (var who in d.Keys.Where(w => !live.Contains(w)).ToList()) d.Remove(who);
            if (d.Count == 0) names.Remove(id);
        }
        if (n > 0) version++;
        return n;
    }

    // 2.22: a member left or was removed: every leg value and name they contributed goes (the merged table is rebuilt from the others). Caller holds Gate; returns how many
    // contributions went (the caller saves).
    public int RemoveIdentity(string identity)
    {
        int n = 0;
        foreach (var key in legs.Keys.ToList())
        {
            if (legs[key].Remove(identity)) n++;
            if (legs[key].Count == 0) legs.Remove(key);
        }
        foreach (var id in names.Keys.ToList())
        {
            if (names[id].Remove(identity)) n++;
            if (names[id].Count == 0) names.Remove(id);
        }
        if (n > 0) { Prune(); version++; }
        return n;
    }

    static IEnumerable<string> IdsOf(string key)
    {
        var rest = key[(key.IndexOf('|') + 1)..];
        var i = rest.IndexOf('>');
        yield return rest[..i]; yield return rest[(i + 1)..];
    }

    public sealed class ShareResult { public int Accepted, Rejected, Unchanged; public bool Changed; }

    // Stores one identity's legs. Invalid entries count as rejected; the table bounds are enforced. Caller holds Gate; saving is separate.
    public ShareResult Share(string identity, JsonObject legsIn, JsonObject namesIn)
    {
        var r = new ShareResult();
        var now = NowMs;
        int mine = legs.Values.Count(d => d.ContainsKey(identity));
        int seen = 0;
        foreach (var (key, node) in legsIn)
        {
            if (++seen > Flights.MaxLegsPerShare) { r.Rejected++; continue; }
            if (!Flights.ValidKey(key) || !Flights.TryLeg(node, out var t, out var n)) { r.Rejected++; continue; }
            if (!legs.TryGetValue(key, out var d))
            {
                if (legs.Count >= Flights.MaxLegs || mine >= Flights.MaxLegsPerIdentity) { r.Rejected++; continue; }
                d = legs[key] = new Dictionary<string, Flights.Contribution>(StringComparer.Ordinal);
            }
            if (d.TryGetValue(identity, out var old))
            {
                if (old.T == t && old.N == n)
                {
                    r.Unchanged++;
                    if (now - old.At > 30 * 86_400_000L) { old.At = now; r.Changed = true; version++; } // still true: keeps it from expiring
                    continue;
                }
                old.T = t; old.N = n; old.At = now;
            }
            else
            {
                if (mine >= Flights.MaxLegsPerIdentity) { r.Rejected++; if (d.Count == 0) legs.Remove(key); continue; }
                if (d.Count >= Flights.MaxContributorsPerLeg) d.Remove(d.OrderBy(kv => kv.Value.At).First().Key);
                d[identity] = new Flights.Contribution { T = t, N = n, At = now };
                mine++;
            }
            r.Accepted++; r.Changed = true; version++;
        }
        if (namesIn != null)
        {
            int k = 0;
            foreach (var (id, node) in namesIn)
            {
                if (++k > Flights.MaxNamesPerShare) break;
                if (!Flights.ValidId(id) || node is not JsonValue v || !v.TryGetValue<string>(out var raw)) continue;
                var nm = Flights.CleanName(raw);
                if (nm.Length == 0) continue;
                if (!names.TryGetValue(id, out var nd))
                {
                    if (names.Count >= Flights.MaxNames) continue;
                    nd = names[id] = new Dictionary<string, string>(StringComparer.Ordinal);
                }
                if (nd.TryGetValue(identity, out var prev) && prev == nm) continue;
                if (nd.Count >= Flights.MaxContributorsPerLeg && !nd.ContainsKey(identity)) continue;
                nd[identity] = nm; r.Changed = true; version++;
            }
        }
        return r;
    }

    // { t:"flights.data", at, legs: { key: {t,n} }, names: { id: name } } - the merged table. Caller holds Gate.
    public string MergedJson()
    {
        if (cached != null && cachedVersion == version) return cached;
        var lo = new JsonObject();
        foreach (var (key, d) in legs.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var a = Flights.Merge(d);
            if (a != null) lo[key] = new JsonObject { ["t"] = a.T, ["n"] = a.N };
        }
        var used = lo.Select(kv => kv.Key).SelectMany(IdsOf).ToHashSet();
        var no = new JsonObject();
        foreach (var (id, d) in names.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!used.Contains(id)) continue;
            // the name most contributors agree on; ties go to the alphabetically first so the answer is stable
            var pick = d.Values.GroupBy(x => x).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).First().Key;
            no[id] = pick;
        }
        cached = new JsonObject { ["t"] = LodgeMsg.FlightsData, ["at"] = NowMs / 1000, ["legs"] = lo, ["names"] = no }.ToJsonString();
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
                foreach (var (who, c) in d) co[who] = new JsonObject { ["t"] = c.T, ["n"] = c.N, ["at"] = c.At };
                lo[key] = co;
            }
            var no = new JsonObject();
            foreach (var (id, d) in names)
            {
                var co = new JsonObject();
                foreach (var (who, nm) in d) co[who] = nm;
                no[id] = co;
            }
            var bytes = Encoding.UTF8.GetBytes(new JsonObject { ["v"] = 1, ["legs"] = lo, ["names"] = no }.ToJsonString());
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
            log?.LogWarning("Flights: save failed: {Error}", e.GetType().Name);
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            return false;
        }
    }
}
