using System.Text.Json.Nodes;
using Xunit;

namespace Lodge.Tests;

// Learned flight paths pooled by the Lodge (feature "flightroutes")
public class FlightRouteTests
{
    // a leg from (0, 0) north to (2000, 0) that bends `bend` yards west in the middle; z unknown on every other point
    static object?[] Line(double bend = 300, double jitter = 0, int n = 20)
    {
        var a = new List<object?>();
        for (int i = 0; i < n; i++)
        {
            double f = i / (double)(n - 1), x = 2000 * f, y = bend * Math.Sin(Math.PI * f) + (i % 2 == 0 ? jitter : -jitter);
            a.Add(x); a.Add(y); a.Add(i % 2 == 0 ? 120.0 : null);
        }
        return a.ToArray();
    }

    static object Route(object?[] p, int n = 1, double t = 70, int m = 1) => new { m, p, t, n };

    static async Task<JsonObject> Share(FunRig r, Member who, Dictionary<string, object> routes, bool advance = true)
    {
        if (advance) r.Clock.Advance(TimeSpan.FromMinutes(11));
        r.Sock(who.Name).Clear();
        await r.Send(who, new { t = "flights.routes.share", routes });
        return r.Sock(who.Name).Of("flights.routes.shared").Last();
    }

    static async Task<JsonObject> Get(FunRig r, Member who)
    {
        r.Clock.Advance(TimeSpan.FromSeconds(2));
        r.Sock(who.Name).Clear();
        await r.Send(who, new { t = "flights.routes.get" });
        return r.Sock(who.Name).Of("flights.routes.data").Last();
    }

    const string K = "23>80";

    [Theory]
    [InlineData("23>80", true)]
    [InlineData("1>9999999", true)]
    [InlineData("23>23", false)]
    [InlineData("0>80", false)]
    [InlineData("023>80", false)]
    [InlineData("Horde|23>80", false)]
    [InlineData("23>80>81", false)]
    [InlineData("23>", false)]
    [InlineData("a>b", false)]
    [InlineData("23>80\n", false)]
    [InlineData("12345678>1", false)]
    public void Keys_are_two_numeric_node_ids(string key, bool ok) => Assert.Equal(ok, FlightRoutes.ValidKey(key));

    static JsonNode J(object o) => JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(o))!;

    [Fact]
    public void A_route_is_checked_for_size_numbers_length_and_speed()
    {
        Assert.NotNull(FlightRoutes.Parse(J(Route(Line())), out _));
        Assert.NotNull(FlightRoutes.Parse(J(new { m = 1, g = 1 }), out _));
        Assert.Null(FlightRoutes.Parse(J(new { m = 1, g = 2 }), out _));
        Assert.Null(FlightRoutes.Parse(J(new { m = 1, g = 1, p = Line() }), out _));         // a revert carries nothing else
        Assert.Null(FlightRoutes.Parse(J(Route(new object?[] { 0.0, 0.0, null })), out _));   // one point
        Assert.Null(FlightRoutes.Parse(J(Route(Line(n: 65))), out _));                         // too many points
        Assert.Null(FlightRoutes.Parse(J(Route(new object?[] { 0.0, 0.0, null, 1.0 })), out _)); // not triples
        Assert.Null(FlightRoutes.Parse(J(Route(new object?[] { 0.0, 0.0, null, 30000.0, 0.0, null })), out var w1)); Assert.Equal("points", w1);
        Assert.Null(FlightRoutes.Parse(J(Route(new object?[] { 0.0, 0.0, 9000.0, 2000.0, 0.0, null })), out _));      // z out of range
        Assert.Null(FlightRoutes.Parse(J(Route(new object?[] { "0", 0.0, null, 2000.0, 0.0, null })), out _));        // a string
        Assert.Null(FlightRoutes.Parse(J(Route(new object?[] { 0.0, 0.0, null, 20.0, 0.0, null })), out var w2)); Assert.Equal("length", w2);   // 20 yards: no leg
        // a zig-zag far longer than three times the straight distance
        var zig = new List<object?>(); for (int i = 0; i < 30; i++) { zig.Add(i * 20.0); zig.Add(i % 2 == 0 ? 0.0 : 900.0); zig.Add(null); }
        Assert.Null(FlightRoutes.Parse(J(Route(zig.ToArray())), out var w3)); Assert.Equal("length", w3);
        Assert.Null(FlightRoutes.Parse(J(Route(Line(), t: 4)), out _));                         // under 5 s
        Assert.Null(FlightRoutes.Parse(J(Route(Line(), t: 2000)), out _));
        Assert.Null(FlightRoutes.Parse(J(Route(Line(), t: 10)), out var w4)); Assert.Equal("speed", w4);   // 2100 yards in 10 s
        Assert.NotNull(FlightRoutes.Parse(J(Route(Line(), t: 0)), out _));                      // no duration known
        Assert.Null(FlightRoutes.Parse(J(Route(Line(), n: 0)), out _));
        Assert.Null(FlightRoutes.Parse(J(Route(Line(), n: 51)), out _));
        Assert.Null(FlightRoutes.Parse(J(new { m = -1, p = Line(), t = 70, n = 1 }), out _));
        Assert.Null(FlightRoutes.Parse(J(new { m = 1.5, p = Line(), t = 70, n = 1 }), out _));
    }

    [Fact]
    public async Task One_report_of_one_flight_is_not_published_two_members_who_agree_are()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member"); var bob = r.Join("Bob", "member");
        var a1 = (await Share(r, amy, new() { [K] = Route(Line(300)) }));
        Assert.True((bool)a1["ok"]!); Assert.Equal(1, (int)a1["accepted"]!);
        var d0 = (await Get(r, amy));
        Assert.Empty(d0["routes"]!.AsObject());
        await Share(r, bob, new() { [K] = Route(Line(310, jitter: 6), t: 74) });
        var d1 = (await Get(r, bob));
        var leg = d1["routes"]![K]!.AsObject();
        Assert.Equal(1, (int)leg["m"]!); Assert.Equal(2, (int)leg["n"]!); Assert.Equal(2, (int)leg["k"]!);
        Assert.Equal(72.0, (double)leg["t"]!);
        var p = leg["p"]!.AsArray(); Assert.Equal(48 * 3, p.Count);
        Assert.InRange((double)p[24 * 3 + 1]!, 280, 320);                                   // the average bends ~305 yards in the middle
    }

    [Fact]
    public async Task One_member_with_two_agreeing_flights_is_enough()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member");
        await Share(r, amy, new() { [K] = Route(Line(300), n: 2) });
        var d = (await Get(r, amy));
        Assert.Equal(1, (int)d["routes"]![K]!["k"]!);
        Assert.Equal(2, (int)d["routes"]![K]!["n"]!);
    }

    [Fact]
    public async Task Members_who_disagree_publish_nothing_and_the_bigger_agreeing_group_wins()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member"); var bob = r.Join("Bob", "member"); var cat = r.Join("Cat", "member");
        await Share(r, amy, new() { [K] = Route(Line(300)) });
        await Share(r, bob, new() { [K] = Route(Line(-400)) });                  // the other side: no agreement
        Assert.Empty((await Get(r, amy))["routes"]!.AsObject());
        await Share(r, cat, new() { [K] = Route(Line(-390)) });
        var leg = (await Get(r, amy))["routes"]![K]!;
        Assert.InRange((double)leg["p"]![24 * 3 + 1]!, -420, -370);
        Assert.Equal(2, (int)leg["k"]!);
    }

    [Fact]
    public async Task A_newer_game_again_report_takes_the_route_back()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member"); var bob = r.Join("Bob", "member");
        await Share(r, amy, new() { [K] = Route(Line(300), n: 3) });
        Assert.NotNull((await Get(r, amy))["routes"]![K]);
        var g = (await Share(r, bob, new() { [K] = new { m = 1, g = 1 } }));
        Assert.True((bool)g["ok"]!);
        Assert.Null((await Get(r, amy))["routes"]![K]);
        // a revert for a leg nobody reported is not even kept
        await Share(r, bob, new() { ["5>6"] = new { m = 1, g = 1 } });
        Assert.Equal(1, r.Hub.Flight.Routes.LegCount);
    }

    [Fact]
    public async Task Guests_cannot_share_the_rate_is_one_per_ten_minutes_and_bad_entries_are_counted()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member"); var pat = r.Join("Pat", "guest");
        var gst = (await Share(r, pat, new() { [K] = Route(Line()) }));
        Assert.False((bool)gst["ok"]!); Assert.Equal("guest", (string)gst["why"]!);
        var ok = (await Share(r, amy, new() { [K] = Route(Line()), ["bad"] = Route(Line()), ["7>8"] = new { m = 1, p = new object?[] { 0.0 } } }));
        Assert.Equal(1, (int)ok["accepted"]!); Assert.Equal(2, (int)ok["rejected"]!);
        var rate = (await Share(r, amy, new() { [K] = Route(Line(310)) }, advance: false));
        Assert.False((bool)rate["ok"]!); Assert.Equal("rate", (string)rate["why"]!);
        // guests may read the pool
        r.Sock("Pat").Clear();
        Assert.NotNull((await Get(r, pat))["routes"]);
    }

    [Fact]
    public async Task A_share_over_100_routes_is_cut_and_the_pool_survives_a_restart_and_a_purge()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member"); var bob = r.Join("Bob", "member");
        var many = new Dictionary<string, object>();
        for (int i = 1; i <= 105; i++) many[i + ">" + (i + 1000)] = Route(Line(300), n: 2);
        var res = (await Share(r, amy, many));
        Assert.Equal(100, (int)res["accepted"]!); Assert.Equal(5, (int)res["rejected"]!);
        await Share(r, bob, new() { [K] = Route(Line(300), n: 2) });
        // a fresh store from the same folder reads the same pool back
        var again = new FlightRouteStore(r.Hub.Cfg.DataDir, T.Log, r.Clock);
        Assert.Equal(101, again.LegCount);
        string a, b;
        lock (again.Gate) a = JsonNode.Parse(again.MergedJson())!["routes"]!.ToJsonString();
        lock (r.Hub.Flight.Routes.Gate) b = JsonNode.Parse(r.Hub.Flight.Routes.MergedJson())!["routes"]!.ToJsonString();
        Assert.Equal(b, a);
        // a member who leaves takes their routes along (the flight module's purge)
        Assert.True(r.Hub.Flight.Purge("Amy") >= 100);
        Assert.Equal(1, r.Hub.Flight.Routes.LegCount);
    }

    [Fact]
    public void A_damaged_file_is_survived_and_loaded_values_are_checked_again()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "flightroutes.json"), "{\"v\":1,\"legs\":{\"23>80\":{\"amy\":{\"m\":1,\"p\":[0,0,null,99999,0,null],\"t\":70,\"n\":1,\"at\":1}},\"x>y\":{\"bob\":{\"m\":1,\"g\":1,\"at\":1}}}}");
        var s = new FlightRouteStore(dir.Path, T.Log, new FakeClock());
        Assert.Equal(0, s.LegCount);
        File.WriteAllText(Path.Combine(dir.Path, "flightroutes.json"), "{{{ not json");
        var s2 = new FlightRouteStore(dir.Path, T.Log, new FakeClock());
        Assert.Equal(0, s2.LegCount);
    }

    [Fact]
    public void The_server_lists_the_feature()
    {
        using var r = new FunRig();
        Assert.Contains("flightroutes", r.Hub.FeatureList());
    }

    [Fact]
    public void Agreement_uses_both_directions_so_a_shortcut_counts()
    {
        var bent = FlightRoutes.Parse(J(Route(Line(300))), out _)!.Pts;
        var near = FlightRoutes.Parse(J(Route(Line(315, jitter: 5))), out _)!.Pts;
        var straight = FlightRoutes.Parse(J(Route(Line(0))), out _)!.Pts;
        Assert.True(FlightRoutes.Agree(bent, near));
        Assert.False(FlightRoutes.Agree(bent, straight));
        Assert.False(FlightRoutes.Agree(straight, bent));
    }

    static string[] Keys(JsonObject o) => o.Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray();

    // the typed FlightsRoutesSharedMsg / FlightsRoutesDataMsg (Elans.Shared) send the same keys, and flights.routes.data the very bytes the old
    // JsonObject did (z null where it is unknown)
    [Fact]
    public async Task Typed_route_frames_keep_their_keys_and_bytes()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "member"); var bob = r.Join("Bob", "officer");
        Assert.Equal(new[] { "accepted", "ok", "rejected", "t" }, Keys(await Share(r, elan, new() { [K] = Route(Line(), t: 70) })));
        await Share(r, bob, new() { [K] = Route(Line(jitter: 3), t: 72) });
        Assert.Equal(new[] { "accepted", "ok", "rejected", "t", "why" }, Keys(await Share(r, bob, new() { [K] = Route(Line()) }, advance: false)));

        var data = await Get(r, elan);
        Assert.Equal(new[] { "at", "routes", "t" }, Keys(data));
        var route = (JsonObject)data["routes"]![K]!;
        Assert.Equal(new[] { "k", "m", "n", "p", "t" }, Keys(route));
        Assert.Contains(((JsonArray)route["p"]!), x => x == null);
        // the old construction (FlightRouteStore.MergedJson at c712015) from the same values: m, n, k ints, t and the points doubles
        var ro = new JsonObject();
        foreach (var (key, v) in (JsonObject)data["routes"]!)
        {
            var p = new JsonArray();
            foreach (var x in (JsonArray)v!["p"]!) p.Add(x == null ? null : JsonValue.Create((double)x));
            ro[key] = new JsonObject { ["m"] = (int)v["m"]!, ["p"] = p, ["t"] = (double)v["t"]!, ["n"] = (int)v["n"]!, ["k"] = (int)v["k"]! };
        }
        var old = new JsonObject { ["t"] = "flights.routes.data", ["at"] = (long)data["at"]!, ["routes"] = ro }.ToJsonString();
        string now;
        lock (r.Hub.Flight.Routes.Gate) now = r.Hub.Flight.Routes.MergedJson();
        Assert.Equal(old, now);
    }
}
