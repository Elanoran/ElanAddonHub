using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Lodge;

// "Contribute to the Cairn" (opt-in, off by default; PROTOCOL.md "The Cairn"). The Cairn is a commons that merges the pooled Atlas
// numbers of many Lodges into one daily dataset - give to get: only contributing Lodges receive it (CairnReceive.cs). When the Lodge owner switches it on (LODGE_CAIRN_ENABLED=1 plus the Cairn's
// URL and this Lodge's Cairn key), the server posts the Lodge's POOLED totals about once an hour: ids and counts only - drops per creature /
// object / fishing zone / gathering kind (kills and item counts), vendor items (price, stack, limit), spawn and npc spots, gathering
// nodes, flight times between numbered taxi nodes, and the game build. Never names, characters, player ids, members, finds, notes, quests
// or game text. Only what members already shared with the pool is in it (a member who switched Atlas sharing off shares nothing; a purge
// takes a member's numbers out of the pool, and the next post lowers the totals at the Cairn too).
// Each record carries the CURRENT total; only records that changed since the last confirmed post are sent (and null for records that are
// gone). <data>/cairn-sent.json remembers a hash per confirmed record, the next seq and the Cairn's epoch. Every 7 days everything is sent
// again (unchanged numbers cost nothing at the Cairn; it tells it the records are still true in the current game build).
public static partial class CairnRules
{
    public static readonly string[] Kinds = { "kill", "object", "fishing", "pickpocket", "skinning", "mining", "herbalism", "salvage", "contained" };
    static readonly string[] Sections = { "kills", "objects", "fishing", "pickpocket", "skinning", "mining", "herbalism", "salvage", "contained" };
    public const int MaxSpawnPoints = 60, MaxNodePoints = 600, MaxItems = 200;
    public const int ChunkBytes = 1_500_000;          // JSON per post before gzip (keeps every post far below a proxy's 10 MB body limit)
    public const int ChunkRecords = 20_000;

    [GeneratedRegex(@"^(Horde|Alliance)\|([1-9][0-9]{0,5})>([1-9][0-9]{0,5})\z", RegexOptions.CultureInvariant)]
    private static partial Regex LegRx();

    // only numeric taxi node ids: a leg keyed by a node NAME (game text) is never sent
    public static bool ValidLeg(string k) { var m = LegRx().Match(k ?? ""); return m.Success && m.Groups[2].Value != m.Groups[3].Value; }

    // https, or http to this machine (tests, a Cairn on the same host); anything else is refused
    public static bool ValidUrl(string url, out Uri uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim().TrimEnd('/'), UriKind.Absolute, out var u)) return false;
        if (!string.IsNullOrEmpty(u.UserInfo) || !string.IsNullOrEmpty(u.Query) || !string.IsNullOrEmpty(u.Fragment)) return false;
        bool local = u.IsLoopback;
        if (u.Scheme != Uri.UriSchemeHttps && !(u.Scheme == Uri.UriSchemeHttp && local)) return false;
        uri = u;
        return true;
    }

    public static bool ValidKey(string key) => key != null && key.Length is >= 40 and <= 120 && key.StartsWith("cairn_", StringComparison.Ordinal) && key.All(c => c is >= '!' and <= '~');

    static double R4(double v) => Math.Round(v, 4, MidpointRounding.AwayFromZero);
    // numbers from atlas.data: built in code (int / long / double inside) or parsed (JsonElement) - read them all the same way
    static double D(JsonNode n)
    {
        if (n is not JsonValue v) return 0;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<float>(out var f)) return f;
        return double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : 0;
    }
    static long L(JsonNode n)
    {
        var d = D(n);
        return double.IsNaN(d) || double.IsInfinity(d) ? 0 : (long)Math.Clamp(d, long.MinValue / 2, long.MaxValue / 2);
    }

    // The records the Cairn gets, from one full atlas.data (all pages merged) and the merged flights. Key -> value (sorted keys).
    public static SortedDictionary<string, JsonNode> Records(JsonObject atlas, JsonObject flights)
    {
        var r = new SortedDictionary<string, JsonNode>(StringComparer.Ordinal);
        for (int k = 0; k < Kinds.Length; k++)
        {
            if (atlas?[Sections[k]] is not JsonObject sec) continue;
            foreach (var (src, node) in sec)
            {
                if (node is not JsonObject o) continue;
                long n = L(o["n"]);
                if (n <= 0) continue;
                var io = new JsonObject();
                if (o["items"] is JsonObject items)
                    foreach (var (item, iv) in items.OrderBy(kv => int.TryParse(kv.Key, out var i) ? i : int.MaxValue).Take(MaxItems))
                    {
                        if (iv is not JsonObject it) continue;
                        long t = L(it["n"]), c = L(it["c"]);
                        if (t <= 0) continue;
                        io[item] = new JsonArray(t, Math.Max(c, t));
                    }
                var rec = new JsonObject { ["n"] = n };
                if (io.Count > 0) rec["i"] = io;
                r["drops/" + Kinds[k] + "/" + src] = rec;
            }
        }
        if (atlas?["vendors"] is JsonObject vendors)
            foreach (var (npc, node) in vendors)
            {
                if (node?["items"] is not JsonObject items || items.Count == 0) continue;
                var vo = new JsonObject();
                foreach (var (item, iv) in items.OrderBy(kv => int.TryParse(kv.Key, out var i) ? i : int.MaxValue))
                    if (iv is JsonObject it) vo[item] = new JsonArray(L(it["p"]), Math.Max(1, L(it["s"])), it["l"] == null ? -1 : L(it["l"]), L(it["x"]) == 1 ? 1 : 0);
                if (vo.Count > 0) r["vendors/" + npc] = vo;
            }
        // where creatures stand: the pooled spawn points (n = 1 each) and the npc spots (n = times seen there)
        var spots = new SortedDictionary<string, List<(long z, double x, double y, long n)>>(StringComparer.Ordinal);
        void Spot(string npc, JsonNode p, long n)
        {
            long z = L(p?["z"]);
            if (z <= 0) return;
            if (!spots.TryGetValue(npc, out var l)) spots[npc] = l = new();
            l.Add((z, R4(D(p["x"])), R4(D(p["y"])), Math.Max(1, n)));
        }
        if (atlas?["spawns"] is JsonObject spawns) foreach (var (npc, list) in spawns) if (list is JsonArray a) foreach (var p in a) Spot(npc, p, 1);
        if (atlas?["npcspots"] is JsonObject ns) foreach (var (npc, list) in ns) if (list is JsonArray a) foreach (var p in a) Spot(npc, p, L(p?["n"]));
        foreach (var (npc, l) in spots)
            r["spawns/" + npc] = new JsonArray(l.OrderByDescending(p => p.n).ThenBy(p => p.z).ThenBy(p => p.x).ThenBy(p => p.y).Take(MaxSpawnPoints)
                .Select(p => (JsonNode)new JsonArray(p.z, p.x, p.y, p.n)).ToArray());
        if (atlas?["nodes"] is JsonObject nodes)
            foreach (var (obj, maps) in nodes)
            {
                if (maps is not JsonObject mo) continue;
                var pts = new List<(long z, double x, double y, long n)>();
                foreach (var (map, list) in mo)
                    if (long.TryParse(map, NumberStyles.None, CultureInfo.InvariantCulture, out var z) && list is JsonArray a)
                        foreach (var p in a) pts.Add((z, R4(D(p?["x"])), R4(D(p?["y"])), Math.Max(1, L(p?["n"]))));
                if (pts.Count > 0)
                    r["nodes/" + obj] = new JsonArray(pts.OrderByDescending(p => p.n).ThenBy(p => p.z).ThenBy(p => p.x).ThenBy(p => p.y).Take(MaxNodePoints)
                        .Select(p => (JsonNode)new JsonArray(p.z, p.x, p.y, p.n)).ToArray());
            }
        if (flights?["legs"] is JsonObject legs)
            foreach (var (leg, v) in legs)
                if (ValidLeg(leg) && v is JsonObject lo && D(lo["t"]) >= 1 && L(lo["n"]) >= 1)
                    r["flights/" + leg] = new JsonArray(Math.Round(D(lo["t"]), 1, MidpointRounding.AwayFromZero), L(lo["n"]));
        return r;
    }

    // the newest game build the members' finds were made in (0 = unknown)
    public static int GameBuild(JsonObject atlas)
    {
        long best = -1; int gb = 0;
        if (atlas?["finds"] is JsonArray fa)
            foreach (var f in fa)
            {
                long s = L(f?["s"]), b = L(f?["gb"]);
                if (b > 0 && s > best) { best = s; gb = (int)Math.Min(b, 9_999_999); }
            }
        return gb;
    }

    public static string Hash(JsonNode n) => n == null ? "" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(n.ToJsonString())))[..16];

    // the post bodies: changed records (and null for gone ones), cut into chunks
    public static List<(JsonObject body, List<string> keys)> Chunks(SortedDictionary<string, JsonNode> changed, int gb, Func<long> nextSeq)
    {
        var res = new List<(JsonObject, List<string>)>();
        JsonObject cur = null; List<string> keys = null; int bytes = 0;
        foreach (var (key, val) in changed)
        {
            int size = key.Length + (val?.ToJsonString().Length ?? 4) + 8;
            if (cur == null || keys.Count >= ChunkRecords || (keys.Count > 0 && bytes + size > ChunkBytes))
            {
                cur = new JsonObject { ["v"] = 1, ["seq"] = 0, ["gb"] = gb };
                keys = new List<string>(); bytes = 0;
                res.Add((cur, keys));
            }
            var parts = key.Split('/', 3);
            JsonObject sec;
            if (parts[0] == "drops")
            {
                var drops = (JsonObject)(cur["drops"] ??= new JsonObject());
                sec = (JsonObject)(drops[parts[1]] ??= new JsonObject());
                sec[parts[2]] = val?.DeepClone();
            }
            else
            {
                sec = (JsonObject)(cur[parts[0]] ??= new JsonObject());
                sec[parts[1] + (parts.Length > 2 ? "/" + parts[2] : "")] = val?.DeepClone();
            }
            keys.Add(key); bytes += size;
        }
        foreach (var (body, _) in res) body["seq"] = nextSeq();
        return res;
    }
}

public sealed class CairnContributor
{
    readonly LodgeConfig cfg;
    readonly AtlasStore atlas;
    readonly FlightStore flights;
    readonly ILogger log;
    readonly IClock clock;
    HttpMessageHandler handler;
    readonly string statePath;
    readonly object gate = new();
    HttpClient http;
    int running;
    DateTime nextRun;

    public CairnContributor(LodgeConfig cfg, AtlasStore atlas, FlightStore flights, ILogger log, IClock clock, HttpMessageHandler handler = null)
    {
        this.cfg = cfg; this.atlas = atlas; this.flights = flights; this.log = log; this.clock = clock; this.handler = handler;
        statePath = Path.Combine(cfg.DataDir, "cairn-sent.json");
        nextRun = clock.UtcNow + TimeSpan.FromMinutes(2);   // not right at start-up
        Link = new CairnLink(cfg);
    }

    // 2.28: how this Lodge reaches the Cairn (env key, or the Guild Master's switch and its own key pair; CairnLink.cs)
    public readonly CairnLink Link;

    // tests: a fake Cairn (no network)
    public void UseHandlerForTests(HttpMessageHandler h) { handler = h; http = null; }

    // on when the Atlas is on AND either the owner's env (LODGE_CAIRN_ENABLED + a valid URL and key) or the Guild Master's switch with the
    // Cairn's admission (2.28)
    public bool Enabled => Link.Contributing;

    // the next run as soon as possible (the Guild Master's switch, an admission)
    public void Soon() => nextRun = clock.UtcNow;

    public sealed class State
    {
        public string Epoch = ""; public long Seq; public long FullAt; public Dictionary<string, string> Sent = new(StringComparer.Ordinal);
        public string OutpostsSent = "";   // connected outposts: hash of the last weekly numbers the Cairn took
    }

    public State Load()
    {
        var s = new State();
        try
        {
            if (!File.Exists(statePath) || new FileInfo(statePath).Length > 64L * 1024 * 1024) return s;
            if (JsonNode.Parse(File.ReadAllText(statePath)) is not JsonObject o) return s;
            s.Epoch = (string)o["epoch"] ?? ""; s.Seq = (long?)o["seq"] ?? 0; s.FullAt = (long?)o["fullAt"] ?? 0; s.OutpostsSent = (string)o["outposts"] ?? "";
            if (o["sent"] is JsonObject so) foreach (var (k, v) in so) if (v is JsonValue jv && jv.TryGetValue<string>(out var h)) s.Sent[k] = h;
        }
        catch (Exception e) { log?.LogWarning("Cairn: state unreadable ({Error}), everything is sent again", e.GetType().Name); }
        return s;
    }

    void Save(State s)
    {
        var so = new JsonObject();
        foreach (var kv in s.Sent.OrderBy(k => k.Key, StringComparer.Ordinal)) so[kv.Key] = kv.Value;
        var o = new JsonObject { ["v"] = 1, ["epoch"] = s.Epoch, ["seq"] = s.Seq, ["fullAt"] = s.FullAt, ["outposts"] = s.OutpostsSent, ["sent"] = so };
        var tmp = statePath + ".tmp";
        File.WriteAllText(tmp, o.ToJsonString());
        File.Move(tmp, statePath, true);
    }

    // called by the hub's housekeeping every 15 s: starts a run when one is due (never two at once)
    public void Tick()
    {
        if (!Enabled || clock.UtcNow < nextRun || Interlocked.CompareExchange(ref running, 1, 0) != 0) return;
        nextRun = clock.UtcNow + TimeSpan.FromMinutes(cfg.CairnEveryMinutes);
        _ = Task.Run(async () =>
        {
            try { await RunOnce(CancellationToken.None); }
            catch (Exception e) { log?.LogWarning("Cairn: contribution failed: {Error}", e.GetType().Name); }
            finally { Interlocked.Exchange(ref running, 0); }
        });
    }

    public sealed class RunResult { public int Posts, Records, Accepted, Rejected; public string Stopped = ""; public bool Full; public string Standing = ""; public bool OutpostsSent; }

    // connected outposts: the weekly numbers (CairnWeekly.Report), sent only to a Cairn whose /v1/status lists "outposts" in "features"
    public Func<JsonArray> Outposts;

    // give to get: what the Cairn said about this Lodge's standing (good / new / lapsed / revoked / key) and until when it receives; the
    // receiver (CairnReceive.cs) fetches or stops on it
    public event Action<string, long?> Standing;

    static long? LongOf(JsonNode n) { try { return n == null ? null : (long?)n; } catch (Exception e) when (e is FormatException or InvalidOperationException) { return null; } }

    HttpClient Http() => http ??= handler != null ? new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(60) } : new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

    HttpRequestMessage Req(HttpMethod m, string path) => Link.Request(m, path);

    // 2.28, env path: the Cairn offers "keys" - register this Lodge's own key pair once (bearer + signed by the new key). From then on every
    // request is signed and the bearer key is retired at the Cairn (nothing to change in lodge.env; the key there is simply no longer used).
    async Task<bool> RegisterKeys(CancellationToken ct)
    {
        Link.Keys.Get();
        var body = Encoding.UTF8.GetBytes(new JsonObject { ["publicKey"] = Link.Keys.PublicKey }.ToJsonString());
        var req = Link.Request(HttpMethod.Post, "/v1/keys", body, forceSign: true);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.CairnKey);
        using var r = await Http().SendAsync(req, ct);
        if (!r.IsSuccessStatusCode) { log?.LogWarning("Cairn: registering this Lodge's key pair was refused (HTTP {Code}) - the bearer key stays in use", (int)r.StatusCode); return false; }
        lock (Link.Join.Gate) Link.Join.Migrated = true;
        Link.Join.Save();
        log?.LogInformation("Cairn: this Lodge signs its requests with its own key pair now; the Cairn retired its bearer key (key id {Kid})", Link.Keys.KeyId);
        return true;
    }

    // what the pool holds now, as Cairn records (all atlas.data pages merged; guest view: no names)
    public (SortedDictionary<string, JsonNode> records, int gb) Current()
    {
        var all = new JsonObject();
        lock (atlas.Gate)
        {
            long since = 0; string epoch = null;
            for (int page = 0; page < 10_000; page++)
            {
                var d = atlas.Get(since, epoch, _ => "", guest: true);
                foreach (var (k, v) in d.ToList())
                {
                    if (v is JsonObject vo)
                    {
                        var into = (JsonObject)(all[k] ??= new JsonObject());
                        foreach (var (rk, rv) in vo.ToList()) { vo.Remove(rk); into[rk] = rv; }
                    }
                    else if (v is JsonArray va)
                    {
                        var into = (JsonArray)(all[k] ??= new JsonArray());
                        foreach (var e in va.ToList()) { va.Remove(e); into.Add(e); }
                    }
                }
                if (d["more"] == null) break;
                since = (long)d["rev"]; epoch = (string)d["epoch"];
            }
        }
        JsonObject fl;
        lock (flights.Gate) fl = JsonNode.Parse(flights.MergedJson()) as JsonObject;
        var gb = cfg.CairnGameBuild > 0 ? cfg.CairnGameBuild : CairnRules.GameBuild(all);
        return (CairnRules.Records(all, fl), gb);
    }

    public async Task<RunResult> RunOnce(CancellationToken ct)
    {
        var res = await RunInner(ct);
        if (res.Standing.Length > 0) { try { Standing?.Invoke(res.Standing, receiveUntil); } catch (Exception e) { log?.LogWarning("Cairn: {Error} after the contribution", e.GetType().Name); } }
        return res;
    }

    long? receiveUntil;

    async Task<RunResult> RunInner(CancellationToken ct)
    {
        var res = new RunResult();
        if (!Enabled) { res.Stopped = "disabled"; return res; }
        bool sendOutposts = false;
        var st = Load();
        // 1. where the Cairn stands: its epoch for us (another one = it forgot our data) and the last seq it took
        using (var sr = await Http().SendAsync(Req(HttpMethod.Get, "/v1/status"), ct))
        {
            if (!sr.IsSuccessStatusCode)
            {
                if (sr.StatusCode == HttpStatusCode.Unauthorized)
                {
                    string reason = "";
                    try { reason = (string)(JsonNode.Parse(await sr.Content.ReadAsStringAsync(ct)) as JsonObject)?["reason"] ?? ""; } catch { }
                    // 2.28: the bearer key was retired (the key pair is registered): sign from now on. A signed env-path Lodge the Cairn does not
                    // know (its database restored from before the move): back to the bearer key, which registers the key pair again.
                    if (reason == "retired" && Link.EnvPath && Link.Keys.Exists && !Link.Join.Migrated)
                    { lock (Link.Join.Gate) Link.Join.Migrated = true; Link.Join.Save(); res.Stopped = "signing from the next run"; nextRun = clock.UtcNow; return res; }
                    if (reason == "key" && Link.EnvPath && Link.Join.Migrated)
                    { lock (Link.Join.Gate) Link.Join.Migrated = false; Link.Join.Save(); res.Stopped = "the Cairn does not know this key pair - back to the bearer key"; nextRun = clock.UtcNow; return res; }
                    if (reason == "revoked" && Link.JoinPath) { lock (Link.Join.Gate) Link.Join.State = "revoked"; Link.Join.Save(); }
                    res.Standing = reason == "revoked" ? "revoked" : "key"; receiveUntil = null;
                }
                res.Stopped = Refused(sr.StatusCode, "status"); return res;
            }
            var so = JsonNode.Parse(await sr.Content.ReadAsStringAsync(ct)) as JsonObject;
            if (Link.EnvPath && !Link.Join.Migrated && so?["features"] is JsonArray kf && kf.Any(f => f is JsonValue fv && fv.TryGetValue<string>(out var fs) && fs == "keys"))
            {
                try { await RegisterKeys(ct); }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { log?.LogWarning("Cairn: registering the key pair failed ({Error}) - tried again next run", e.GetType().Name); }
            }
            if (Link.JoinPath)
                lock (Link.Join.Gate) { Link.Join.ProbationUntil = LongOf(so?["probationUntil"]); Link.Join.Settled = (bool?)so?["settled"] ?? Link.Join.Settled; }
            if ((string)so?["standing"] is { Length: > 0 } sst) { res.Standing = sst; receiveUntil = LongOf(so["receiveUntil"]); }
            sendOutposts = Outposts != null && so?["features"] is JsonArray fa && fa.Any(f => f is JsonValue fv && fv.TryGetValue<string>(out var fs) && fs == "outposts");
            var epoch = (string)so?["epoch"] ?? "";
            long lastSeq = (long?)so?["lastSeq"] ?? 0;
            if (epoch != st.Epoch) { st.Sent.Clear(); st.Epoch = epoch; res.Full = true; }
            st.Seq = Math.Max(st.Seq, lastSeq);
        }
        var nowS = new DateTimeOffset(clock.UtcNow).ToUnixTimeSeconds();
        if (nowS - st.FullAt > 7 * 86_400L) { st.Sent.Clear(); res.Full = true; }
        if (res.Full) st.FullAt = nowS;

        // 2. what changed since the last confirmed post
        var (cur, gb) = Current();
        var changed = new SortedDictionary<string, JsonNode>(StringComparer.Ordinal);
        foreach (var (k, v) in cur) if (!st.Sent.TryGetValue(k, out var h) || h != CairnRules.Hash(v)) changed[k] = v;
        foreach (var k in st.Sent.Keys) if (!cur.ContainsKey(k)) changed[k] = null;
        // the weekly numbers ride along when they changed since the Cairn last took them (alone, in a post of their own, when no record changed)
        JsonArray outposts = null; string outpostsHash = "";
        if (sendOutposts) { outposts = Outposts(); outpostsHash = CairnRules.Hash(outposts); if (outpostsHash == st.OutpostsSent) outposts = null; }
        if (changed.Count == 0 && outposts == null) { Save(st); res.Stopped = "nothing-new"; return res; }

        // 3. post in chunks; a chunk counts as sent only after the Cairn took it
        var chunks = CairnRules.Chunks(changed, gb, () => ++st.Seq);
        if (outposts != null)
        {
            if (chunks.Count == 0) chunks.Add((new JsonObject { ["v"] = 1, ["seq"] = ++st.Seq, ["gb"] = gb }, new List<string>()));
            chunks[0].body["outposts"] = outposts;
        }
        Save(st);   // the seq numbers are used up now, whatever happens next
        foreach (var (body, keys) in chunks)
        {
            var bytes = Encoding.UTF8.GetBytes(body.ToJsonString());
            using var ms = new MemoryStream();
            using (var gz = new GZipStream(ms, CompressionLevel.Optimal, true)) gz.Write(bytes);
            var req = Link.Request(HttpMethod.Post, "/v1/contribute", ms.ToArray(), "application/json", "gzip");
            using var pr = await Http().SendAsync(req, ct);
            if (!pr.IsSuccessStatusCode) { res.Stopped = Refused(pr.StatusCode, "contribute"); break; }
            lock (Link.Join.Gate) Link.Join.LastPost = new DateTimeOffset(clock.UtcNow).ToUnixTimeSeconds();
            var po = JsonNode.Parse(await pr.Content.ReadAsStringAsync(ct)) as JsonObject;
            res.Posts++; res.Records += keys.Count;
            if (body["outposts"] != null) { st.OutpostsSent = outpostsHash; res.OutpostsSent = true; }
            res.Accepted += (int?)po?["accepted"] ?? 0; res.Rejected += (int?)po?["rejected"] ?? 0;
            if ((string)po?["standing"] is { Length: > 0 } pst) { res.Standing = pst; receiveUntil = LongOf(po["receiveUntil"]); }
            if ((string)po?["epoch"] is { } ep && ep != st.Epoch) { st.Sent.Clear(); st.Epoch = ep; Save(st); res.Stopped = "epoch-changed"; break; }
            foreach (var k in keys)
                if (changed[k] == null) st.Sent.Remove(k); else st.Sent[k] = CairnRules.Hash(changed[k]);
            Save(st);
        }
        if (res.Posts > 0) Link.Join.Save();
        // counts only - never content, never the key
        log?.LogInformation("Cairn: {Posts} post(s), {Records} record(s) sent, {Accepted} accepted, {Rejected} rejected{Full}{Stop}",
            res.Posts, res.Records, res.Accepted, res.Rejected, res.Full ? " (full resend)" : "", res.Stopped.Length > 0 ? ", stopped: " + res.Stopped : "");
        return res;
    }

    string Refused(HttpStatusCode code, string what)
    {
        var why = code switch
        {
            HttpStatusCode.Unauthorized => "key refused (revoked or wrong - ask the Cairn's admin)",
            HttpStatusCode.TooManyRequests => "rate limited (tries again at the next run)",
            HttpStatusCode.RequestEntityTooLarge => "too large",
            _ => "HTTP " + (int)code,
        };
        log?.LogWarning("Cairn: {What} refused: {Why}", what, why);
        return why;
    }
}
