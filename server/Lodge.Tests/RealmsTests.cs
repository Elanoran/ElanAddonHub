using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Lodge.Tests;

// Realm status (feature "realms"): a fake Blizzard in place of the network. Nothing here talks to the real API; the
// credentials are synthetic and the tests check that they never reach a log line or a message.
public sealed class FakeBlizzard : HttpMessageHandler
{
    public const string Secret = "synthetic-bnet-secret-0a1b2c3d4e5f";
    public const string ClientId = "synthetic-bnet-client-id-9f8e7d";

    public readonly List<string> Requests = new();                 // "POST oauth.battle.net /token" / "GET eu.api.blizzard.com /data/wow/connected-realm/index ns=dynamic-eu"
    public readonly List<string> AuthHeaders = new();
    public readonly Dictionary<string, Dictionary<int, JsonObject>> Namespaces = new();   // ns -> connected realm id -> body
    public int TokenCalls, TokenExpiresIn = 86399;
    public int FailAll;                                            // status code to answer every API call with (0 = none)
    public TimeSpan? RetryAfter;
    public HashSet<string> MissingNamespaces = new();              // answer 404 for these

    public int Count(string needle) { lock (Requests) return Requests.Count(r => r.Contains(needle)); }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        var uri = req.RequestUri!;
        string line;
        lock (Requests)
        {
            line = $"{req.Method} {uri.Host} {uri.AbsolutePath}" + (uri.Query.Contains("namespace=") ? " ns=" + System.Web.HttpUtility.ParseQueryString(uri.Query)["namespace"] : "");
            Requests.Add(line);
            AuthHeaders.Add(req.Headers.Authorization?.ToString() ?? "");
        }
        if (uri.Host == "oauth.battle.net")
        {
            TokenCalls++;
            if (FailAll == 401) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            return Task.FromResult(Json(new JsonObject { ["access_token"] = "synthetic-token-" + TokenCalls, ["token_type"] = "bearer", ["expires_in"] = TokenExpiresIn }));
        }
        if (FailAll != 0)
        {
            var r = new HttpResponseMessage((HttpStatusCode)FailAll);
            if (RetryAfter != null) r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(RetryAfter.Value);
            return Task.FromResult(r);
        }
        var ns = System.Web.HttpUtility.ParseQueryString(uri.Query)["namespace"] ?? "";
        if (MissingNamespaces.Contains(ns) || !Namespaces.TryGetValue(ns, out var realms)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        var path = uri.AbsolutePath;
        if (path.EndsWith("/connected-realm/index"))
            return Task.FromResult(Json(new JsonObject
            {
                ["_links"] = new JsonObject { ["self"] = new JsonObject { ["href"] = $"https://{uri.Host}{path}?namespace={ns}" } },
                ["connected_realms"] = new JsonArray(realms.Keys.Select(id => (JsonNode)new JsonObject { ["href"] = $"https://{uri.Host}/data/wow/connected-realm/{id}?namespace={ns}" }).ToArray()),
            }));
        var m = System.Text.RegularExpressions.Regex.Match(path, @"/connected-realm/(\d+)$");
        if (m.Success && realms.TryGetValue(int.Parse(m.Groups[1].Value), out var body)) return Task.FromResult(Json((JsonObject)body.DeepClone()));
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    static HttpResponseMessage Json(JsonObject o) => new(HttpStatusCode.OK) { Content = new StringContent(o.ToJsonString(), Encoding.UTF8, "application/json") };

    // the documented shape of GET /data/wow/connected-realm/{id}
    public static JsonObject Connected(int id, string status, string name, string type = "NORMAL", string typeName = "Normal", string category = "Europe", string population = "FULL", bool? queue = false, string? slug = null)
    {
        var o = new JsonObject
        {
            ["_links"] = new JsonObject { ["self"] = new JsonObject { ["href"] = $"https://eu.api.blizzard.com/data/wow/connected-realm/{id}?namespace=dynamic-eu" } },
            ["id"] = id,
            ["status"] = new JsonObject { ["type"] = status, ["name"] = status == "UP" ? "Up" : "Down" },
            ["population"] = new JsonObject { ["type"] = population, ["name"] = char.ToUpper(population[0]) + population[1..].ToLower() },
            ["realms"] = new JsonArray(new JsonObject
            {
                ["id"] = id, ["region"] = new JsonObject { ["name"] = "Europe", ["id"] = 1 },
                ["connected_realm"] = new JsonObject { ["href"] = "https://eu.api.blizzard.com/data/wow/connected-realm/" + id },
                ["name"] = name, ["category"] = category, ["locale"] = "enGB", ["timezone"] = "Europe/Paris",
                ["type"] = new JsonObject { ["type"] = type, ["name"] = typeName }, ["is_tournament"] = false,
                ["slug"] = slug ?? name.ToLowerInvariant().Replace(' ', '-'),
            }),
        };
        if (queue != null) o["has_queue"] = queue.Value;
        return o;
    }
}

public sealed class RealmsTests
{
    sealed class Rig : IDisposable
    {
        public readonly FunRig R;
        public readonly FakeBlizzard Net = new();
        public readonly CaptureLogger Log = new();
        public Rig(bool configured = true, Action<LodgeConfig>? tweak = null)
        {
            R = new FunRig(c =>
            {
                if (configured) { c.BnetClientId = FakeBlizzard.ClientId; c.BnetClientSecret = FakeBlizzard.Secret; }
                tweak?.Invoke(c);
            }, Log);
            R.Hub.Realms.UseHandler(Net);
        }
        public RealmsModule M => R.Hub.Realms;
        public void Forever(string ns = "dynamic-classic1x-eu", string status = "UP")
        {
            Net.Namespaces["dynamic-eu"] = new() { [100] = FakeBlizzard.Connected(100, "UP", "Tarren Mill") };
            Net.Namespaces[ns] = new()
            {
                [2001] = FakeBlizzard.Connected(2001, status, "WoW Forever", "NORMAL", "Normal", "Forever"),
                [2002] = FakeBlizzard.Connected(2002, "UP", "Forever PvP", "PVP", "PvP", "Forever", "MEDIUM", true),
                [2003] = FakeBlizzard.Connected(2003, "UP", "Forever RP", "RP", "RP", "Forever", "LOW"),
                [2004] = FakeBlizzard.Connected(2004, "UP", "Forever Hardcore", "NORMAL", "Normal", "Forever Hardcore", "HIGH"),
                [2999] = FakeBlizzard.Connected(2999, "UP", "Some Classic Realm", "NORMAL", "Normal", "Classic"),
            };
        }
        public async Task Poll() => await M.PollOnce();
        public async Task<(Member m, FunSock s)> Join(string name, string role = "member") { var m = R.Join(name, role); await M.SendTo(m); return (m, R.Sock(name)); }
        public void Dispose() => R.Dispose();
    }

    static JsonObject Status(FunSock s) => s.Of("realms.status").Last();
    static string[] Names(JsonObject st) => ((JsonArray)st["realms"]!).Select(r => (string)r!["name"]!).ToArray();

    [Fact]
    public async Task Probes_the_namespaces_in_order_and_keeps_the_forever_realms()
    {
        using var t = new Rig();
        t.Forever();
        await t.Poll();
        var idx = t.Net.Requests.Where(r => r.Contains("connected-realm/index")).ToList();
        Assert.Equal(new[] { "dynamic-eu", "dynamic-classic-eu", "dynamic-classic1x-eu", "dynamic-classicann-eu" }, idx.Select(r => r[(r.IndexOf("ns=") + 3)..]).ToArray());
        Assert.All(idx, r => Assert.Contains("eu.api.blizzard.com", r));
        Assert.True(t.M.Available);
        var rs = t.M.Current;
        Assert.Equal(new[] { "WoW Forever", "Forever PvP", "Forever RP", "Forever Hardcore" }, rs.Select(r => r.Name).ToArray());
        Assert.Equal(new[] { "Normal", "PvP", "RP", "Hardcore" }, rs.Select(r => r.Ruleset).ToArray());
        Assert.All(rs, r => Assert.Equal("dynamic-classic1x-eu", r.Namespace));
        Assert.Contains(t.Log.Lines, l => l.Contains("dynamic-classic1x-eu: 5 connected realms, 4 Forever") && l.Contains("dynamic-eu: 1 connected realms, 0 Forever"));
    }

    [Fact]
    public async Task Missing_namespaces_are_skipped_and_the_override_replaces_the_list()
    {
        using var t = new Rig();
        t.Forever("dynamic-classic-eu");
        t.Net.MissingNamespaces.Add("dynamic-eu");
        await t.Poll();
        Assert.Equal(4, t.M.Current.Count);
        Assert.Contains(t.Log.Lines, l => l.Contains("dynamic-eu: not available (HTTP 404)"));

        using var o = new Rig(tweak: c => c.RealmsNamespaces = new[] { "dynamic-custom-eu", "bad namespace!" });
        o.Net.Namespaces["dynamic-custom-eu"] = new() { [7] = FakeBlizzard.Connected(7, "UP", "Forever Custom") };
        await o.Poll();
        Assert.Equal(new[] { "dynamic-custom-eu" }, o.Net.Requests.Where(r => r.Contains("connected-realm/index")).Select(r => r[(r.IndexOf("ns=") + 3)..]).ToArray());
        Assert.Single(o.M.Current);
    }

    [Fact]
    public async Task Region_is_used_for_the_host_and_the_namespaces()
    {
        using var t = new Rig(tweak: c => c.BnetRegion = "us");
        t.Net.Namespaces["dynamic-us"] = new();
        await t.Poll();
        Assert.Contains(t.Net.Requests, r => r.Contains("us.api.blizzard.com") && r.Contains("ns=dynamic-us"));
        Assert.Equal("eu", LodgeConfig.CleanRegion("not a region"));
        Assert.Equal("kr", LodgeConfig.CleanRegion(" KR "));
    }

    [Fact]
    public async Task Nothing_forever_is_available_false_with_a_reason_and_a_slow_retry()
    {
        using var t = new Rig();
        t.Net.Namespaces["dynamic-eu"] = new() { [100] = FakeBlizzard.Connected(100, "UP", "Tarren Mill") };
        var (_, s) = await t.Join("Mia");
        Assert.Empty(s.Of("realms.status"));            // nothing known yet: nothing sent
        await t.Poll();
        Assert.False(t.M.Available);
        var st = Status(s);
        Assert.False((bool)st["available"]!);
        Assert.Equal("Forever realms not found in the Blizzard API", (string)st["why"]!);
        Assert.Empty((JsonArray)st["realms"]!);
        Assert.Equal(TimeSpan.FromMinutes(15), t.M.NextPoll - t.R.Clock.UtcNow);
        // a connection after that gets the same answer
        var (_, s2) = await t.Join("Bob");
        Assert.False((bool)Status(s2)["available"]!);
        // not due yet = no new calls from Tick
        var calls = t.Net.Requests.Count;
        t.M.Tick();
        await Task.Delay(50);
        Assert.Equal(calls, t.Net.Requests.Count);
    }

    [Fact]
    public void Match_words_pick_the_realms()
    {
        var old = RealmsParse.Match;
        try
        {
            var r = new RealmsParse.RealmInfo { Name = "Skyborne", Slug = "skyborne", Category = "Europe" };
            RealmsParse.Match = new[] { "forever" };
            Assert.False(RealmsParse.IsForever(r));
            RealmsParse.Match = new[] { "nothing", " SKYborne " };
            Assert.True(RealmsParse.IsForever(r));
            RealmsParse.Match = new[] { "", "  " };
            Assert.False(RealmsParse.IsForever(r));
        }
        finally { RealmsParse.Match = old; }
    }

    [Fact]
    public async Task Parses_the_documented_shapes()
    {
        var c = RealmsParse.ConnectedRealm(FakeBlizzard.Connected(1305, "UP", "WoW Forever", "PVP", "PvP", "Forever", "FULL", true));
        Assert.Equal(1305, c!.Id); Assert.Equal("UP", c.Status); Assert.Equal("Full", c.Population); Assert.True(c.HasQueue);
        Assert.Equal("PvP", RealmsParse.Ruleset(c.Realms[0]));
        Assert.True(RealmsParse.IsForever(c.Realms[0]));
        // names as {locale: text} when the locale parameter is left out; a status that is neither up nor down
        var j = JsonNode.Parse("""{"id":9,"status":{"type":"MAINTENANCE"},"realms":[{"name":{"de_DE":"Ewig","en_US":"Forever Hardcore"},"slug":"forever-hardcore","category":{"en_US":"Forever"},"type":{"type":"NORMAL","name":{"en_US":"Normal"}}}]}""")!.AsObject();
        var c2 = RealmsParse.ConnectedRealm(j)!;
        Assert.Equal("UNKNOWN", c2.Status); Assert.Null(c2.HasQueue);
        Assert.Equal("Forever Hardcore", c2.Realms[0].Name);
        Assert.Equal("Hardcore", RealmsParse.Ruleset(c2.Realms[0]));
        Assert.Equal(new[] { 3, 4 }, RealmsParse.ConnectedIndex(JsonNode.Parse("""{"connected_realms":[{"href":"https://eu.api.blizzard.com/data/wow/connected-realm/3?namespace=dynamic-eu"},{"href":"https://eu.api.blizzard.com/data/wow/connected-realm/4?namespace=dynamic-eu"},{"href":"junk"},{}]}""")!.AsObject()).ToArray());
        Assert.Empty(RealmsParse.ConnectedIndex(new JsonObject()));
        Assert.False(RealmsParse.Token(new JsonObject(), out _, out _));
        Assert.True(RealmsParse.Token(JsonNode.Parse("""{"access_token":"x","expires_in":100}""")!.AsObject(), out var tok, out var secs));
        Assert.Equal("x", tok); Assert.Equal(100, secs);
        Assert.Equal("RP-PvP", RealmsParse.Ruleset(new RealmsParse.RealmInfo { Name = "Forever", TypeType = "RPPVP" }));
        Assert.Equal("", RealmsParse.Ruleset(new RealmsParse.RealmInfo { Name = "Forever" }));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task The_token_is_cached_until_it_expires()
    {
        using var t = new Rig();
        t.Forever();
        t.Net.TokenExpiresIn = 3600;
        await t.Poll();
        Assert.Equal(1, t.Net.TokenCalls);
        t.R.Clock.Advance(TimeSpan.FromMinutes(3));
        await t.Poll();
        await t.Poll();
        Assert.Equal(1, t.Net.TokenCalls);
        Assert.Contains("Bearer synthetic-token-1", t.Net.AuthHeaders.Last());
        t.R.Clock.Advance(TimeSpan.FromMinutes(59));      // 3600 s minus the 60 s margin has passed
        await t.Poll();
        Assert.Equal(2, t.Net.TokenCalls);
        Assert.Contains("Bearer synthetic-token-2", t.Net.AuthHeaders.Last());
        // the token request itself is a POST with Basic credentials and goes to the OAuth host only
        Assert.Equal("POST oauth.battle.net /token", t.Net.Requests[0]);
        Assert.StartsWith("Basic ", t.Net.AuthHeaders[0]);
    }

    [Fact]
    public async Task Later_polls_only_fetch_the_known_realms()
    {
        using var t = new Rig();
        t.Forever();
        await t.Poll();
        var before = t.Net.Requests.Count;
        t.R.Clock.Advance(TimeSpan.FromMinutes(3));
        await t.Poll();
        var added = t.Net.Requests.Skip(before).ToList();
        Assert.Equal(4, added.Count);
        Assert.DoesNotContain(added, r => r.Contains("index"));
        Assert.All(added, r => Assert.Contains("ns=dynamic-classic1x-eu", r));
        Assert.Equal(TimeSpan.FromSeconds(120), t.M.NextPoll - t.R.Clock.UtcNow);
    }

    [Fact]
    public async Task Poll_interval_is_clamped_and_tick_waits_for_it()
    {
        using var t = new Rig(tweak: c => c.RealmsPollSeconds = 5);
        t.Forever();
        await t.Poll();
        Assert.Equal(TimeSpan.FromSeconds(60), t.M.NextPoll - t.R.Clock.UtcNow);
        var polls = t.M.PollCount;
        t.M.Tick();
        await Task.Delay(50);
        Assert.Equal(polls, t.M.PollCount);
        t.R.Clock.Advance(TimeSpan.FromSeconds(61));
        t.M.Tick();
        for (int i = 0; i < 50 && t.M.PollCount == polls; i++) await Task.Delay(20);
        Assert.Equal(polls + 1, t.M.PollCount);
    }

    [Fact]
    public async Task A_change_is_broadcast_to_members_and_guests_and_new_connections_get_the_state()
    {
        using var t = new Rig();
        t.Forever(status: "DOWN");
        var (_, mia) = await t.Join("Mia");
        var (_, gus) = await t.Join("Gus", "guest");
        await t.Poll();
        foreach (var s in new[] { mia, gus })
        {
            var st = Status(s);
            Assert.True((bool)st["available"]!);
            Assert.Equal("eu", (string)st["region"]!);
            Assert.Equal(new[] { "WoW Forever", "Forever PvP", "Forever RP", "Forever Hardcore" }, Names(st));
            var first = (JsonObject)((JsonArray)st["realms"]!)[0]!;
            Assert.Equal("DOWN", (string)first["status"]!);
            Assert.Equal("Normal", (string)first["ruleset"]!);
            Assert.Equal("Full", (string)first["population"]!);
            Assert.False((bool)first["hasQueue"]!);
            Assert.True((long)st["checkedAt"]! > 0);
            Assert.Null(st["why"]);
        }
        Assert.True((bool)((JsonObject)((JsonArray)Status(mia)["realms"]!)[1]!)["hasQueue"]!);

        // unchanged: quiet
        mia.Clear();
        t.R.Clock.Advance(TimeSpan.FromMinutes(1));
        await t.Poll();
        Assert.Empty(mia.Of("realms.status"));
        // ... apart from a heartbeat so "checked N min ago" stays honest
        t.R.Clock.Advance(RealmsModule.Heartbeat);
        await t.Poll();
        Assert.Single(mia.Of("realms.status"));

        // changed: sent at once, and a connection after that gets the new state
        t.Net.Namespaces["dynamic-classic1x-eu"][2002] = FakeBlizzard.Connected(2002, "DOWN", "Forever PvP", "PVP", "PvP", "Forever", "MEDIUM", true);
        mia.Clear();
        t.R.Clock.Advance(TimeSpan.FromMinutes(1));
        await t.Poll();
        Assert.Equal("DOWN", (string)((JsonObject)((JsonArray)Status(mia)["realms"]!)[1]!)["status"]!);
        var (_, late) = await t.Join("Late");
        Assert.Equal("DOWN", (string)((JsonObject)((JsonArray)Status(late)["realms"]!)[1]!)["status"]!);
    }

    [Fact]
    public async Task Realms_back_is_sent_once_when_everything_that_was_down_is_up()
    {
        using var t = new Rig();
        t.Forever(status: "DOWN");
        var (_, mia) = await t.Join("Mia");
        await t.Poll();
        Assert.Empty(mia.Of("realms.back"));            // first state: nothing "came back"
        t.Net.Namespaces["dynamic-classic1x-eu"][2001] = FakeBlizzard.Connected(2001, "UP", "WoW Forever", "NORMAL", "Normal", "Forever");
        t.R.Clock.Advance(TimeSpan.FromMinutes(3));
        await t.Poll();
        var back = mia.Of("realms.back").Single();
        Assert.Equal(new[] { "WoW Forever" }, ((JsonArray)back["realms"]!).Select(x => (string)x!).ToArray());
        t.R.Clock.Advance(TimeSpan.FromMinutes(3));
        await t.Poll();
        Assert.Single(mia.Of("realms.back"));
    }

    [Fact]
    public async Task Errors_back_off_then_show_unknown_and_recover()
    {
        using var t = new Rig();
        t.Forever();
        var (_, mia) = await t.Join("Mia");
        await t.Poll();
        Assert.Equal("UP", (string)((JsonObject)((JsonArray)Status(mia)["realms"]!)[0]!)["status"]!);

        t.Net.FailAll = 500;
        var gaps = new List<double>();
        for (int i = 0; i < 3; i++)
        {
            t.R.Clock.Advance(TimeSpan.FromMinutes(20));
            await t.Poll();
            gaps.Add((t.M.NextPoll - t.R.Clock.UtcNow).TotalSeconds);
        }
        Assert.True(gaps[0] < gaps[1] && gaps[1] <= gaps[2], string.Join(",", gaps));
        Assert.All(gaps, g => Assert.InRange(g, 120, 900));
        // two failures: last good state still shown; the third marks everything unknown
        var last = Status(mia);
        Assert.Equal("UNKNOWN", (string)((JsonObject)((JsonArray)last["realms"]!)[0]!)["status"]!);
        Assert.True((bool)last["available"]!);

        t.Net.FailAll = 0;
        t.R.Clock.Advance(TimeSpan.FromMinutes(20));
        await t.Poll();
        Assert.Equal("UP", (string)((JsonObject)((JsonArray)Status(mia)["realms"]!)[0]!)["status"]!);
        Assert.Equal(TimeSpan.FromSeconds(120), t.M.NextPoll - t.R.Clock.UtcNow);
    }

    [Fact]
    public async Task Too_many_requests_honours_retry_after_up_to_the_cap()
    {
        using var t = new Rig();
        t.Forever();
        await t.Poll();
        t.Net.FailAll = 429; t.Net.RetryAfter = TimeSpan.FromMinutes(7);
        t.R.Clock.Advance(TimeSpan.FromMinutes(3));
        await t.Poll();
        Assert.Equal(TimeSpan.FromMinutes(7), t.M.NextPoll - t.R.Clock.UtcNow);
        t.Net.RetryAfter = TimeSpan.FromHours(3);
        t.R.Clock.Advance(TimeSpan.FromMinutes(8));
        await t.Poll();
        Assert.Equal(RealmsModule.MaxBackoff, t.M.NextPoll - t.R.Clock.UtcNow);
    }

    [Fact]
    public async Task Refused_credentials_on_a_first_poll_say_so_without_the_secret()
    {
        using var t = new Rig();
        t.Net.FailAll = 401;
        var (_, mia) = await t.Join("Mia");
        for (int i = 0; i < 3; i++) { t.R.Clock.Advance(TimeSpan.FromMinutes(20)); await t.Poll(); }
        var st = Status(mia);
        Assert.False((bool)st["available"]!);
        Assert.Contains("refused", (string)st["why"]!);
        Assert.DoesNotContain(FakeBlizzard.Secret, st.ToJsonString());
    }

    [Fact]
    public async Task The_secret_never_appears_in_logs_or_messages()
    {
        using var t = new Rig();
        t.Forever();
        var (m, mia) = await t.Join("Mia");
        await t.Poll();
        t.Net.FailAll = 500;
        for (int i = 0; i < 4; i++) { t.R.Clock.Advance(TimeSpan.FromMinutes(20)); await t.Poll(); }
        t.Net.FailAll = 401;
        t.R.Clock.Advance(TimeSpan.FromMinutes(20)); await t.Poll();
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes(FakeBlizzard.ClientId + ":" + FakeBlizzard.Secret));
        var everything = string.Join("\n", t.Log.Lines) + string.Join("\n", mia.Sent.Select(x => x.ToJsonString())) + t.R.Hub.Welcome(m).ToJsonString()
                         + string.Join(",", t.R.Hub.FeatureList());
        foreach (var bad in new[] { FakeBlizzard.Secret, basic, FakeBlizzard.ClientId, "synthetic-token-" })
            Assert.DoesNotContain(bad, everything);
        Assert.NotEmpty(t.Log.Lines);
    }

    [Fact]
    public void The_feature_is_advertised_only_with_credentials()
    {
        using (var off = new Rig(configured: false))
        {
            Assert.DoesNotContain("realms", off.R.Hub.FeatureList());
            Assert.False(off.M.Configured);
        }
        using (var half = new Rig(configured: false, tweak: c => c.BnetClientId = "only-an-id"))
            Assert.DoesNotContain("realms", half.R.Hub.FeatureList());
        using var on = new Rig();
        Assert.Contains("realms", on.R.Hub.FeatureList());
        var (m, _) = (on.R.Join("Mia", "member"), 0);
        Assert.Contains("realms", ((JsonArray)on.R.Hub.Welcome(m)["features"]!).Select(x => (string)x!));
        Assert.Equal("2.25.0", LodgeHub.Version);
    }

    [Fact]
    public async Task Without_credentials_nothing_is_polled_or_sent()
    {
        using var t = new Rig(configured: false);
        t.Forever();
        var (_, mia) = await t.Join("Mia");
        await t.Poll();
        t.M.Tick();
        Assert.Empty(t.Net.Requests);
        Assert.Empty(mia.Of("realms.status"));
    }
}
