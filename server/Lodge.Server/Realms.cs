using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Lodge;

// Realm status (feature "realms"). The lodge asks the Blizzard Game Data API with its own client credentials and tells the
// members; the credentials never leave this process (not in a log line, not in a message, not in an error text).
//
// Server to client: realms.status { available, region, realms: [ { id, name, slug, ruleset, status, population, hasQueue, namespace } ], checkedAt, why? }
//   sent to everybody connected (members and guests: it is public information) when something changed, every 5 minutes as a heartbeat,
//   and to a new connection right after the welcome. status = UP | DOWN | UNKNOWN; ruleset = Normal | PvP | RP | RP-PvP | Hardcore | "".
//   population / hasQueue are only present when the API gave them. checkedAt = unix seconds of the last poll that reached the API.
//   available = false carries why (a short sentence); realms is then empty.
// Server to client: realms.back { realms: [names], checkedAt } - once, when every realm that was down is up again.
//
// Which namespace holds WoW Forever is not known up front: the first poll probes dynamic-{region}, dynamic-classic-{region},
// dynamic-classic1x-{region} (or LODGE_REALMS_NAMESPACES) and keeps the connected realms whose realm name, slug or category says
// "Forever". Later polls only fetch those. Nothing found = available false, polled every 15 minutes.
public sealed class RealmEntry
{
    public int Id;                 // connected realm id
    public string Namespace = "";
    public string Name = "";
    public string Slug = "";
    public string Ruleset = "";
    public string Status = "UNKNOWN";
    public string Population;      // e.g. "Full", "Medium"; null = not given
    public bool? HasQueue;

    public RealmEntry Clone() => (RealmEntry)MemberwiseClone();

    public JsonObject ToJson()
    {
        var o = new JsonObject { ["id"] = Id, ["name"] = Name, ["slug"] = Slug, ["ruleset"] = Ruleset, ["status"] = Status, ["namespace"] = Namespace };
        if (!string.IsNullOrEmpty(Population)) o["population"] = Population;
        if (HasQueue != null) o["hasQueue"] = HasQueue.Value;
        return o;
    }

    public string Signature => $"{Id}|{Name}|{Ruleset}|{Status}|{Population}|{HasQueue}";
}

// What the Blizzard JSON means. Static and side-effect free, so the tests feed it documented response shapes.
public static class RealmsParse
{
    public sealed class Connected
    {
        public int Id;
        public string Status = "UNKNOWN";
        public string Population;
        public bool? HasQueue;
        public List<RealmInfo> Realms = new();
    }
    public sealed class RealmInfo { public string Name = "", Slug = "", Category = "", TypeType = "", TypeName = ""; }

    // {"access_token":"...","token_type":"bearer","expires_in":86399}
    public static bool Token(JsonObject o, out string token, out int expiresIn)
    {
        token = Str(o?["access_token"]); expiresIn = o?["expires_in"] is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;
        if (expiresIn <= 0) expiresIn = 3600;
        return !string.IsNullOrEmpty(token);
    }

    // {"connected_realms":[{"href":"https://eu.api.blizzard.com/data/wow/connected-realm/1305?namespace=dynamic-eu"}, ...]}
    public static List<int> ConnectedIndex(JsonObject o)
    {
        var ids = new List<int>();
        if (o?["connected_realms"] is not JsonArray a) return ids;
        foreach (var e in a)
        {
            var href = Str(e?["href"]);
            var m = Regex.Match(href ?? "", @"/connected-realm/(\d+)");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var id) && !ids.Contains(id)) ids.Add(id);
        }
        return ids;
    }

    // {"id":1305,"has_queue":false,"status":{"type":"UP","name":"Up"},"population":{"type":"FULL","name":"Full"},
    //  "realms":[{"id":1305,"name":"...","category":"...","type":{"type":"NORMAL","name":"Normal"},"slug":"..."}]}
    public static Connected ConnectedRealm(JsonObject o)
    {
        if (o == null) return null;
        var c = new Connected();
        c.Id = o["id"] is JsonValue iv && iv.TryGetValue<int>(out var id) ? id : 0;
        c.Status = StatusOf(Str(o["status"]?["type"]));
        c.Population = Localized(o["population"]?["name"]) ?? PopulationName(Str(o["population"]?["type"]));
        c.HasQueue = o["has_queue"] is JsonValue q && q.TryGetValue<bool>(out var b) ? b : null;
        if (o["realms"] is JsonArray ra)
            foreach (var r in ra)
                c.Realms.Add(new RealmInfo
                {
                    Name = Localized(r?["name"]) ?? "", Slug = Str(r?["slug"]) ?? "", Category = Localized(r?["category"]) ?? "",
                    TypeType = Str(r?["type"]?["type"]) ?? "", TypeName = Localized(r?["type"]?["name"]) ?? "",
                });
        return c;
    }

    public static string StatusOf(string type) => (type ?? "").ToUpperInvariant() switch { "UP" => "UP", "DOWN" => "DOWN", _ => "UNKNOWN" };

    static string PopulationName(string type) => string.IsNullOrEmpty(type) ? null : char.ToUpperInvariant(type[0]) + type[1..].ToLowerInvariant();

    static string Str(JsonNode n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    // a string, or {"en_US":"...","de_DE":"..."} when the locale parameter was left out
    static string Localized(JsonNode n)
    {
        if (n is JsonValue) return Names.Text(Str(n) ?? "", 60);
        if (n is JsonObject o)
        {
            var pick = Str(o["en_US"]) ?? Str(o["en_GB"]) ?? o.Select(p => Str(p.Value)).FirstOrDefault(s => !string.IsNullOrEmpty(s));
            return pick == null ? null : Names.Text(pick, 60);
        }
        return null;
    }

    public static bool IsForever(RealmInfo r) =>
        Regex.IsMatch(r.Name + " " + r.Slug + " " + r.Category, "forever", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // the ruleset from what the realm says: hints in its name / slug / category win over the API's realm type
    public static string Ruleset(RealmInfo r)
    {
        var text = r.Name + " " + r.Slug.Replace('-', ' ') + " " + r.Category;
        if (Regex.IsMatch(text, @"\bhardcore\b", RegexOptions.IgnoreCase)) return "Hardcore";
        var type = (r.TypeType ?? "").ToUpperInvariant();
        if (type == "RPPVP") return "RP-PvP";
        if (type == "PVP" || Regex.IsMatch(text, @"\bpvp\b", RegexOptions.IgnoreCase)) return "PvP";
        if (type == "RP" || Regex.IsMatch(text, @"\brp\b", RegexOptions.IgnoreCase)) return "RP";
        if (type == "NORMAL") return "Normal";
        return Regex.IsMatch(text, @"\bnormal\b", RegexOptions.IgnoreCase) ? "Normal" : "";
    }

    static readonly string[] Order = { "Normal", "PvP", "RP", "RP-PvP", "Hardcore" };
    public static int RulesetRank(string ruleset) { var i = Array.IndexOf(Order, ruleset); return i < 0 ? Order.Length : i; }
}

public sealed class RealmsHttpException : Exception
{
    public readonly int Status;
    public readonly TimeSpan? RetryAfter;
    public RealmsHttpException(int status, TimeSpan? retryAfter = null) : base("HTTP " + status) { Status = status; RetryAfter = retryAfter; }
}

public sealed class RealmsModule : IModule
{
    public static readonly TimeSpan Heartbeat = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan NotFoundEvery = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan RediscoverEvery = TimeSpan.FromHours(6);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(15);
    public const int FailuresBeforeUnknown = 3;
    const int MaxConnectedPerNamespace = 400;
    const int MaxRealms = 16;

    readonly LodgeHub hub;
    HttpClient http;
    HttpMessageHandler handler;
    readonly object gate = new();
    readonly SemaphoreSlim polling = new(1, 1);

    string token; DateTime tokenExpires = DateTime.MinValue;
    List<(string Ns, int Id)> known = new();
    DateTime discoveredAt = DateTime.MinValue;
    List<RealmEntry> realms = new();
    bool hasState, available; string why;
    long checkedAt;
    int failures;
    string lastSignature = ""; DateTime lastBroadcast = DateTime.MinValue;
    DateTime nextPoll = DateTime.MinValue;
    public int PollCount;   // polls started (tests)

    public RealmsModule(LodgeHub hub) { this.hub = hub; }

    public bool Configured => hub.Cfg.RealmsConfigured;
    public string Region => LodgeConfig.CleanRegion(hub.Cfg.BnetRegion);
    public DateTime NextPoll { get { lock (gate) return nextPoll; } }
    public bool HasState { get { lock (gate) return hasState; } }

    // tests: a fake HTTP handler instead of the network (call before the first poll)
    public void UseHandler(HttpMessageHandler h) { handler = h; http?.Dispose(); http = null; }

    public Task<bool> Handle(Member me, string type, JsonObject m) => Task.FromResult(false);   // nothing is asked of the server: it only pushes

    public string[] Namespaces()
    {
        var over = hub.Cfg.RealmsNamespaces?.Where(n => Regex.IsMatch(n, "^[a-z0-9-]{3,40}$")).ToArray() ?? Array.Empty<string>();
        if (over.Length > 0) return over;
        var r = Region;
        return new[] { "dynamic-" + r, "dynamic-classic-" + r, "dynamic-classic1x-" + r };
    }

    // ---------------------------------------------------------------- what clients get

    public JsonObject StatusJson()
    {
        lock (gate)
        {
            var o = new JsonObject
            {
                ["t"] = "realms.status", ["available"] = available, ["region"] = Region,
                ["realms"] = new JsonArray(available ? realms.Select(r => (JsonNode)r.ToJson()).ToArray() : Array.Empty<JsonNode>()),
                ["checkedAt"] = checkedAt,
            };
            if (!available && why != null) o["why"] = why;
            return o;
        }
    }

    // a new connection: right after the welcome (nothing before the first poll has finished)
    public async Task SendTo(Member me)
    {
        if (!Configured || !HasState) return;
        await hub.Send(me, StatusJson());
    }

    // ---------------------------------------------------------------- polling

    public void Tick()
    {
        if (!Configured) return;
        lock (gate) { if (hub.Clock.UtcNow < nextPoll) return; }
        if (polling.CurrentCount == 0) return;
        _ = Task.Run(async () => { try { await PollOnce(); } catch (Exception e) { hub.Log.LogWarning("Realms poll failed unexpectedly ({Error})", e.GetType().Name); } });
    }

    // One poll, never two at once. Never throws for network trouble: that is counted, backed off and (after a few in a row) shown.
    public async Task PollOnce(CancellationToken ct = default)
    {
        if (!Configured || !await polling.WaitAsync(0)) return;
        try
        {
            PollCount++;
            var now = hub.Clock.UtcNow;
            var interval = TimeSpan.FromSeconds(Math.Clamp(hub.Cfg.RealmsPollSeconds, 60, 900));
            TimeSpan next = interval;
            try
            {
                var tok = await Token(ct);
                bool rediscover = known.Count == 0 && (!hasState || now - discoveredAt >= NotFoundEvery) || now - discoveredAt >= RediscoverEvery;
                List<RealmEntry> fresh;
                if (rediscover) { fresh = await Discover(tok, ct); discoveredAt = hub.Clock.UtcNow; }
                else if (known.Count == 0) { fresh = null; next = NotFoundEvery; }   // nothing to refresh; wait for the next discovery
                else fresh = await Refresh(tok, ct);

                if (fresh != null)
                {
                    failures = 0;
                    await Publish(fresh.Count > 0, fresh, fresh.Count > 0 ? null : "Forever realms not found in the Blizzard API", reached: true);
                    if (fresh.Count == 0) next = NotFoundEvery;
                }
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                failures++;
                var retry = e is RealmsHttpException he ? he.RetryAfter : null;
                var back = TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, interval.TotalSeconds * Math.Pow(2, Math.Min(failures, 6))));
                if (retry != null && retry.Value > back) back = retry.Value;
                next = back > MaxBackoff ? MaxBackoff : back;
                // only the kind of failure is logged: never a message, a URL or a header
                var what = e is RealmsHttpException h ? "HTTP " + h.Status : e.GetType().Name;
                hub.Log.LogWarning("Realm status: Blizzard API request failed ({What}), failure {N}, next try in {Seconds} s", what, failures, (int)next.TotalSeconds);
                if (failures >= FailuresBeforeUnknown) await MarkUnknown(e is RealmsHttpException he2 && he2.Status is 401 or 403 ? "The Blizzard API refused this lodge's credentials" : "The Blizzard API could not be reached");
            }
            lock (gate) nextPoll = hub.Clock.UtcNow + next;
        }
        finally { polling.Release(); }
    }

    async Task<string> Token(CancellationToken ct)
    {
        if (token != null && hub.Clock.UtcNow < tokenExpires) return token;
        token = null;
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://oauth.battle.net/token");
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(hub.Cfg.BnetClientId + ":" + hub.Cfg.BnetClientSecret)));
        req.Content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("grant_type", "client_credentials") });
        var o = await Send(req, ct);
        if (!RealmsParse.Token(o, out var t, out var secs)) throw new RealmsHttpException(502);
        token = t;
        tokenExpires = hub.Clock.UtcNow + TimeSpan.FromSeconds(Math.Max(60, secs - 60));
        return token;
    }

    async Task<JsonObject> Api(string ns, string path, string tok, CancellationToken ct)
    {
        var url = $"https://{Region}.api.blizzard.com/data/wow/{path}?namespace={Uri.EscapeDataString(ns)}&locale=en_US";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tok);
        try { return await Send(req, ct); }
        catch (RealmsHttpException e) when (e.Status == 401) { token = null; throw; }   // expired early: fetch a new token next time
    }

    async Task<JsonObject> Send(HttpRequestMessage req, CancellationToken ct)
    {
        http ??= handler != null ? new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(15) } : new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            TimeSpan? ra = resp.Headers.RetryAfter?.Delta ?? (resp.Headers.RetryAfter?.Date is { } d ? d - hub.Clock.UtcNow : null);
            throw new RealmsHttpException((int)resp.StatusCode, ra);
        }
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (text.Length > 2_000_000) throw new RealmsHttpException(502);
        try { return JsonNode.Parse(text) as JsonObject ?? throw new RealmsHttpException(502); }
        catch (System.Text.Json.JsonException) { throw new RealmsHttpException(502); }
    }

    // probe the namespaces in order and keep the Forever realms from all of them
    async Task<List<RealmEntry>> Discover(string tok, CancellationToken ct)
    {
        var found = new List<RealmEntry>();
        var keep = new List<(string, int)>();
        var summary = new List<string>();
        foreach (var ns in Namespaces())
        {
            JsonObject index;
            try { index = await Api(ns, "connected-realm/index", tok, ct); }
            catch (RealmsHttpException e) when (e.Status is 400 or 403 or 404) { summary.Add($"{ns}: not available (HTTP {e.Status})"); continue; }
            var ids = RealmsParse.ConnectedIndex(index).Take(MaxConnectedPerNamespace).ToList();
            int forever = 0; var names = new List<string>();
            foreach (var id in ids)
            {
                RealmsParse.Connected c;
                try { c = RealmsParse.ConnectedRealm(await Api(ns, "connected-realm/" + id, tok, ct)); }
                catch (RealmsHttpException e) when (e.Status is 404 or 400) { continue; }   // one odd realm must not stop the probe; 401 / 429 / 5xx do
                if (c == null) continue;
                var hits = c.Realms.Where(RealmsParse.IsForever).ToList();
                if (hits.Count == 0) continue;
                forever += hits.Count;
                keep.Add((ns, id));
                foreach (var r in hits) { found.Add(ToEntry(ns, c, r)); names.Add(r.Name); }
            }
            summary.Add($"{ns}: {ids.Count} connected realms, {forever} Forever" + (names.Count > 0 ? " (" + string.Join(", ", names.Take(8)) + ")" : ""));
        }
        hub.Log.LogInformation("Realm status discovery in region {Region}: {Summary}", Region, string.Join("; ", summary));
        lock (gate) known = keep;
        found = found.OrderBy(r => RealmsParse.RulesetRank(r.Ruleset)).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Take(MaxRealms).ToList();
        return found;
    }

    async Task<List<RealmEntry>> Refresh(string tok, CancellationToken ct)
    {
        var result = new List<RealmEntry>();
        int okCount = 0; RealmsHttpException last = null;
        List<(string Ns, int Id)> todo; lock (gate) todo = known.ToList();
        foreach (var (ns, id) in todo)
        {
            try
            {
                var c = RealmsParse.ConnectedRealm(await Api(ns, "connected-realm/" + id, tok, ct));
                if (c != null) { foreach (var r in c.Realms.Where(RealmsParse.IsForever)) result.Add(ToEntry(ns, c, r)); okCount++; }
            }
            catch (RealmsHttpException e) { last = e; if (e.Status is 401 or 429) throw; }
        }
        if (okCount == 0 && last != null) throw last;
        if (okCount < todo.Count)
        {
            // some failed: keep the earlier entry for those so one hiccup doesn't make a realm vanish
            List<RealmEntry> old; lock (gate) old = realms.ToList();
            foreach (var o in old) if (!result.Any(r => r.Id == o.Id && r.Name == o.Name)) result.Add(o.Clone());
        }
        return result.OrderBy(r => RealmsParse.RulesetRank(r.Ruleset)).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Take(MaxRealms).ToList();
    }

    static RealmEntry ToEntry(string ns, RealmsParse.Connected c, RealmsParse.RealmInfo r) => new()
    {
        Id = c.Id, Namespace = ns, Name = r.Name, Slug = r.Slug, Ruleset = RealmsParse.Ruleset(r),
        Status = c.Status, Population = c.Population, HasQueue = c.HasQueue,
    };

    // ---------------------------------------------------------------- state and broadcast

    Task MarkUnknown(string reason)
    {
        List<RealmEntry> copy; lock (gate) copy = realms.Select(r => { var c = r.Clone(); c.Status = "UNKNOWN"; return c; }).ToList();
        return copy.Count > 0 ? Publish(true, copy, null, reached: false) : Publish(false, copy, reason, reached: false);
    }

    async Task Publish(bool isAvailable, List<RealmEntry> fresh, string reason, bool reached)
    {
        List<string> back = null;
        string sig;
        bool send;
        lock (gate)
        {
            var wasDown = realms.Where(r => r.Status == "DOWN").Select(r => r.Name).ToHashSet();
            realms = fresh; available = isAvailable; why = reason; hasState = true;
            if (reached) checkedAt = new DateTimeOffset(hub.Clock.UtcNow).ToUnixTimeSeconds();
            else if (checkedAt == 0) checkedAt = new DateTimeOffset(hub.Clock.UtcNow).ToUnixTimeSeconds();
            if (wasDown.Count > 0 && fresh.Count > 0 && fresh.All(r => r.Status != "DOWN"))
            {
                var up = fresh.Where(r => wasDown.Contains(r.Name) && r.Status == "UP").Select(r => r.Name).ToList();
                if (up.Count > 0) back = up;
            }
            sig = (isAvailable ? "1" : "0") + "|" + reason + "|" + string.Join(";", fresh.Select(r => r.Signature));
            var now = hub.Clock.UtcNow;
            send = sig != lastSignature || now - lastBroadcast >= Heartbeat;
            if (send) { lastSignature = sig; lastBroadcast = now; }
        }
        if (!send) return;
        await hub.Broadcast(StatusJson());
        if (back != null)
        {
            long at; lock (gate) at = checkedAt;
            await hub.Broadcast(new JsonObject { ["t"] = "realms.back", ["realms"] = new JsonArray(back.Select(n => (JsonNode)JsonValue.Create(n)).ToArray()), ["checkedAt"] = at });
        }
    }

    public IReadOnlyList<RealmEntry> Current { get { lock (gate) return realms.Select(r => r.Clone()).ToList(); } }
    public bool Available { get { lock (gate) return available; } }
    public string Why { get { lock (gate) return why; } }
}
