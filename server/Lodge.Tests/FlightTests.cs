using System.Text.Json.Nodes;
using Xunit;

namespace Lodge.Tests;

// Lodge 2.13: crowd-shared flight times
public class FlightTests
{
    static object Leg(double t, int n) => new { t, n };

    static async Task<JsonObject> Share(FunRig r, Member m, Dictionary<string, object> legs, Dictionary<string, string>? names = null, bool advance = true)
    {
        if (advance) r.Clock.Advance(TimeSpan.FromMinutes(11));
        r.Sock(m.Name).Clear();
        await r.Send(m, new { t = "flights.share", legs, names });
        return r.Sock(m.Name).Of("flights.shared").Last();
    }

    static async Task<JsonObject> Get(FunRig r, Member m)
    {
        r.Clock.Advance(TimeSpan.FromSeconds(2));
        r.Sock(m.Name).Clear();
        await r.Send(m, new { t = "flights.get" });
        return r.Sock(m.Name).Of("flights.data").Last();
    }

    static (double t, int n) At(JsonObject data, string key) => ((double)data["legs"]![key]!["t"]!, (int)data["legs"]![key]!["n"]!);

    const string K = "Horde|11>22";

    // ---------------------------------------------------------------- validation

    [Theory]
    [InlineData("Horde|11>22", true)]
    [InlineData("Alliance|2>3", true)]
    [InlineData("Alliance|Stormwind, Elwynn>Ironforge", true)]
    [InlineData("Horde|11>11", false)]            // same node
    [InlineData("Neutral|11>22", false)]
    [InlineData("horde|11>22", false)]
    [InlineData("Horde|11-22", false)]
    [InlineData("Horde|>22", false)]
    [InlineData("Horde|11>", false)]
    [InlineData("Horde|11>22>33", false)]
    [InlineData("Horde|a\"b>22", false)]
    [InlineData("Horde|a\\b>22", false)]
    [InlineData("Horde|a]]..os.execute('x')--[[>22", false)]
    [InlineData("Horde|11>22\n", false)]
    [InlineData("Horde| 11>22", false)]
    [InlineData("Horde|11 >22", false)]
    [InlineData("", false)]
    public void Keys_are_checked_against_a_strict_format(string key, bool ok) => Assert.Equal(ok, Flights.ValidKey(key));

    [Fact]
    public void A_key_with_a_very_long_id_is_refused() => Assert.False(Flights.ValidKey("Horde|" + new string('a', 33) + ">22"));

    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(1800, 50, true)]
    [InlineData(45.55, 3, true)]
    [InlineData(0.9, 1, false)]
    [InlineData(1801, 1, false)]
    [InlineData(-5, 1, false)]
    [InlineData(100, 0, false)]
    [InlineData(100, 51, false)]
    [InlineData(100, 2.5, false)]
    public void Times_and_sample_counts_have_bounds(double t, double n, bool ok)
    {
        var node = JsonNode.Parse($"{{\"t\":{t.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"n\":{n.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}")!;
        Assert.Equal(ok, Flights.TryLeg(node, out _, out _));
    }

    [Fact]
    public void Non_numbers_are_refused_not_thrown()
    {
        foreach (var s in new[] { "{\"t\":\"100\",\"n\":1}", "{\"t\":100,\"n\":true}", "{\"t\":null,\"n\":1}", "{\"t\":[1],\"n\":1}", "5", "\"x\"", "{}" })
            Assert.False(Flights.TryLeg(JsonNode.Parse(s)!, out _, out _), s);
    }

    [Fact]
    public async Task A_share_stores_valid_legs_refuses_bad_ones_and_reports_counts()
    {
        using var r = new FunRig();
        var bob = r.Join("Bob", "member");
        var rep = await Share(r, bob, new() { [K] = Leg(100, 3), ["Horde|bad"] = Leg(100, 3), ["Alliance|1>2"] = Leg(5000, 3), ["Alliance|3>4"] = Leg(60, 0) });
        Assert.True((bool)rep["ok"]!);
        Assert.Equal(1, (int)rep["accepted"]!);
        Assert.Equal(3, (int)rep["rejected"]!);
        var d = await Get(r, bob);
        Assert.Single((JsonObject)d["legs"]!);
        Assert.Equal((100.0, 3), At(d, K));
    }

    [Fact]
    public async Task Names_are_capped_cleaned_and_only_kept_for_known_ids()
    {
        using var r = new FunRig();
        var bob = r.Join("Bob", "member");
        await Share(r, bob, new() { [K] = Leg(100, 3) }, new()
        {
            ["11"] = new string('A', 200) + "\"; os.execute('x') --",
            ["22"] = "Gry\\phon \"Roost\"\r\n[[x]]",
            ["999"] = "Not in any leg",
            ["bad id!"] = "x",
        });
        var d = await Get(r, bob);
        var names = (JsonObject)d["names"]!;
        Assert.Equal(2, names.Count);
        Assert.True(((string)names["11"]!).Length <= 60);
        var n22 = (string)names["22"]!;
        Assert.DoesNotContain("\"", n22); Assert.DoesNotContain("\\", n22); Assert.DoesNotContain("\n", n22); Assert.DoesNotContain("[", n22);
        Assert.Equal("Gryphon Roost x", n22);
    }

    // ---------------------------------------------------------------- rate limit, guests, bounds

    [Fact]
    public async Task One_share_per_ten_minutes_per_identity()
    {
        using var r = new FunRig();
        var bob = r.Join("Bob", "member"); var amy = r.Join("Amy", "member");
        var first = await Share(r, bob, new() { [K] = Leg(100, 3) });
        Assert.True((bool)first["ok"]!);
        r.Clock.Advance(TimeSpan.FromMinutes(9));
        var again = await Share(r, bob, new() { [K] = Leg(101, 4) }, advance: false);
        Assert.False((bool)again["ok"]!); Assert.Equal("rate", (string)again["why"]!);
        Assert.Equal((100.0, 3), At(await Get(r, bob), K));   // the refused one changed nothing
        var other = await Share(r, amy, new() { [K] = Leg(100, 3) }, advance: false);   // another identity has its own budget
        Assert.True((bool)other["ok"]!);
        r.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.True((bool)(await Share(r, bob, new() { [K] = Leg(101, 4) }, advance: false))["ok"]!);
    }

    [Fact]
    public async Task Guests_can_read_but_not_contribute()
    {
        using var r = new FunRig();
        var bob = r.Join("Bob", "member"); var pat = r.Join("Pat", "guest");
        await Share(r, bob, new() { [K] = Leg(100, 3) });
        var rep = await Share(r, pat, new() { ["Horde|1>2"] = Leg(50, 5) });
        Assert.False((bool)rep["ok"]!); Assert.Equal("guest", (string)rep["why"]!);
        var d = await Get(r, pat);
        Assert.Equal((100.0, 3), At(d, K));
        Assert.Single((JsonObject)d["legs"]!);
        Assert.Equal(1, r.Hub.Flight.Store.LegCount);
    }

    [Fact]
    public async Task One_share_takes_at_most_500_legs()
    {
        using var r = new FunRig();
        var bob = r.Join("Bob", "member");
        var legs = new Dictionary<string, object>();
        for (int i = 0; i < 520; i++) legs[$"Horde|{i + 1}>{i + 2000}"] = Leg(60, 1);
        var rep = await Share(r, bob, legs);
        Assert.Equal(500, (int)rep["accepted"]!); Assert.Equal(20, (int)rep["rejected"]!);
        Assert.Equal(500, r.Hub.Flight.Store.LegCount);
    }

    [Fact]
    public async Task One_member_cannot_fill_the_table_and_the_table_is_bounded()
    {
        using var r = new FunRig();
        var bob = r.Join("Bob", "member");
        int accepted = 0;
        for (int b = 0; b < 4; b++)
        {
            var legs = new Dictionary<string, object>();
            for (int i = 0; i < 500; i++) legs[$"Horde|{b * 500 + i + 1}>{b * 500 + i + 9000}"] = Leg(60, 1);
            accepted += (int)(await Share(r, bob, legs))["accepted"]!;
        }
        Assert.Equal(Flights.MaxLegsPerIdentity, accepted);
        Assert.Equal(Flights.MaxLegsPerIdentity, r.Hub.Flight.Store.LegCount);

        // the table as a whole: other people stop at 5000 distinct legs
        int total = 0, k = 0;
        for (int who = 0; who < 5; who++)
        {
            var m = r.Join("Mem" + who, "member");
            for (int b = 0; b < 3; b++)
            {
                var legs = new Dictionary<string, object>();
                for (int i = 0; i < 500; i++, k++) legs[$"Alliance|{k + 1}>{k + 20000}"] = Leg(60, 1);
                total += (int)(await Share(r, m, legs))["accepted"]!;
            }
        }
        Assert.Equal(Flights.MaxLegs, r.Hub.Flight.Store.LegCount);
        Assert.Equal(Flights.MaxLegs - Flights.MaxLegsPerIdentity, total);
    }

    // ---------------------------------------------------------------- aggregation

    [Fact]
    public async Task The_merge_is_the_sample_weighted_average_with_n_summed()
    {
        using var r = new FunRig();
        var a = r.Join("Amy", "member"); var b = r.Join("Bob", "member");
        await Share(r, a, new() { [K] = Leg(100, 10) });
        await Share(r, b, new() { [K] = Leg(120, 30) });
        Assert.Equal((115.0, 40), At(await Get(r, a), K));
    }

    [Fact]
    public async Task Each_person_counts_once_their_latest_value_replaces_the_old_one()
    {
        using var r = new FunRig();
        var a = r.Join("Amy", "member"); var b = r.Join("Bob", "member");
        await Share(r, a, new() { [K] = Leg(100, 10) });
        await Share(r, b, new() { [K] = Leg(110, 10) });
        Assert.Equal((105.0, 20), At(await Get(r, a), K));
        await Share(r, a, new() { [K] = Leg(100, 20) });          // Amy measured more: replaces, never adds
        Assert.Equal((103.3, 30), At(await Get(r, a), K));
        await Share(r, a, new() { [K] = Leg(100, 20) }, advance: true);    // the same again changes nothing
        Assert.Equal((103.3, 30), At(await Get(r, a), K));
        Assert.Equal(2, r.Hub.Flight.Store.ContributionCount);
    }

    [Fact]
    public async Task N_is_capped_at_50()
    {
        using var r = new FunRig();
        var a = r.Join("Amy", "member"); var b = r.Join("Bob", "member"); var c = r.Join("Cy", "member");
        foreach (var m in new[] { a, b, c }) await Share(r, m, new() { [K] = Leg(100, 40) });
        Assert.Equal((100.0, 50), At(await Get(r, a), K));
    }

    [Fact]
    public async Task A_far_off_value_is_stored_but_held_back_once_the_aggregate_is_established()
    {
        using var r = new FunRig();
        var a = r.Join("Amy", "member"); var b = r.Join("Bob", "member"); var c = r.Join("Cy", "member"); var d = r.Join("Dee", "member");
        await Share(r, a, new() { [K] = Leg(100, 6) });
        await Share(r, b, new() { [K] = Leg(300, 2) });                       // +200 %: held back
        Assert.Equal((100.0, 6), At(await Get(r, a), K));
        Assert.Equal(2, r.Hub.Flight.Store.ContributionCount);                // ... but stored
        await Share(r, c, new() { [K] = Leg(105, 2) });                       // an ordinary value is taken
        Assert.Equal((101.3, 8), At(await Get(r, a), K));
        await Share(r, d, new() { [K] = Leg(290, 2) });                       // a second identity confirms Bob
        var (t, n) = At(await Get(r, a), K);
        Assert.Equal(12, n);
        Assert.InRange(t, 165, 166);
    }

    [Fact]
    public async Task Within_forty_percent_is_not_an_outlier_and_a_young_aggregate_takes_anything()
    {
        using var r = new FunRig();
        var a = r.Join("Amy", "member"); var b = r.Join("Bob", "member"); var c = r.Join("Cy", "member");
        await Share(r, a, new() { [K] = Leg(100, 2) });
        await Share(r, b, new() { [K] = Leg(300, 2) });                       // n = 2 < 5: no established aggregate yet
        Assert.Equal((200.0, 4), At(await Get(r, a), K));
        await Share(r, a, new() { ["Horde|1>2"] = Leg(100, 10) });
        await Share(r, c, new() { ["Horde|1>2"] = Leg(140, 1) });             // exactly +40 %: taken
        Assert.Equal(11, At(await Get(r, a), "Horde|1>2").n);
        await Share(r, b, new() { ["Horde|1>2"] = Leg(160, 1) });             // beyond +40 % of the aggregate: held
        Assert.Equal(11, At(await Get(r, a), "Horde|1>2").n);
    }

    [Fact]
    public async Task A_held_back_value_survives_a_restart_and_the_result_is_the_same()
    {
        using var r = new FunRig();
        var a = r.Join("Amy", "member"); var b = r.Join("Bob", "member");
        await Share(r, a, new() { [K] = Leg(100, 6) });
        await Share(r, b, new() { [K] = Leg(300, 2) });
        var before = At(await Get(r, a), K);
        var again = new FlightStore(r.Dir.Path, T.Log, r.Clock);
        lock (again.Gate) Assert.Contains("\"t\":100", again.MergedJson());
        Assert.Equal(2, again.ContributionCount);
        Assert.Equal((100.0, 6), before);
    }

    // ---------------------------------------------------------------- storage, expiry

    [Fact]
    public async Task Contributions_older_than_120_days_are_dropped_and_the_file_follows()
    {
        using var r = new FunRig();
        var a = r.Join("Amy", "member"); var b = r.Join("Bob", "member");
        await Share(r, a, new() { [K] = Leg(100, 5), ["Horde|1>2"] = Leg(50, 2) }, new() { ["11"] = "Old Place" });
        r.Clock.Advance(TimeSpan.FromDays(100));
        await Share(r, b, new() { [K] = Leg(110, 5) });
        r.Clock.Advance(TimeSpan.FromDays(30));                               // Amy's are 130 days old, Bob's 30
        r.Hub.Flight.Tick();
        var d = await Get(r, b);
        Assert.Equal((110.0, 5), At(d, K));
        Assert.Null(d["legs"]!["Horde|1>2"]);
        Assert.Equal(1, r.Hub.Flight.Store.ContributionCount);
        var again = new FlightStore(r.Dir.Path, T.Log, r.Clock);
        Assert.Equal(1, again.LegCount);
        r.Clock.Advance(TimeSpan.FromDays(100));
        r.Hub.Flight.Tick();
        Assert.Equal(0, r.Hub.Flight.Store.LegCount);
        Assert.Empty((JsonObject)(await Get(r, b))["names"]!);
    }

    [Fact]
    public async Task The_file_is_written_atomically_and_reloads_with_the_same_data()
    {
        using var r = new FunRig();
        var a = r.Join("Amy", "member");
        await Share(r, a, new() { [K] = Leg(100, 5) }, new() { ["11"] = "Orgrimmar", ["22"] = "Crossroads" });
        Assert.True(File.Exists(Path.Combine(r.Dir.Path, "flights.json")));
        Assert.False(File.Exists(Path.Combine(r.Dir.Path, "flights.json.tmp")));
        var again = new FlightStore(r.Dir.Path, T.Log, r.Clock);
        lock (again.Gate) { var j = JsonNode.Parse(again.MergedJson())!; Assert.Equal("Orgrimmar", (string)j["names"]!["11"]!); Assert.Equal(100.0, (double)j["legs"]![K]!["t"]!); }
    }

    [Fact]
    public void A_corrupt_hand_edited_or_huge_file_loads_only_what_is_valid()
    {
        using var dir = new TempDir();
        var now = new DateTimeOffset(new FakeClock().UtcNow).ToUnixTimeMilliseconds();
        File.WriteAllText(Path.Combine(dir.Path, "flights.json"),
            "{\"v\":1,\"legs\":{\"Horde|1>2\":{\"amy\":{\"t\":50,\"n\":2,\"at\":" + now + "},\"bob\":{\"t\":\"x\",\"n\":2,\"at\":1}},\"evil\":{\"amy\":{\"t\":5,\"n\":1,\"at\":" + now + "}},\"Horde|3>4\":{\"amy\":{\"t\":9999,\"n\":1,\"at\":" + now + "}}},\"names\":{\"1\":{\"amy\":\"Fine\"},\"x\\\"y\":{\"amy\":\"no\"}}}");
        var s = new FlightStore(dir.Path, T.Log, new FakeClock());
        Assert.Equal(1, s.LegCount); Assert.Equal(1, s.ContributionCount);
        File.WriteAllText(Path.Combine(dir.Path, "flights.json"), "not json at all {");
        Assert.Equal(0, new FlightStore(dir.Path, T.Log, new FakeClock()).LegCount);
    }

    // ---------------------------------------------------------------- feature flag, logs

    [Fact]
    public void The_welcome_announces_flights()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner");
        var features = ((JsonArray)r.Hub.Welcome(elan)["features"]!).Select(x => (string)x!).ToList();
        Assert.Contains("flights", features);
        Assert.Contains("calendar", features);
        Assert.Equal("2.13.0", LodgeHub.Version);
    }

    [Fact]
    public async Task Nothing_that_was_shared_reaches_the_logs()
    {
        var log = new CaptureLogger();
        using var r = new FunRig(log: log);
        var a = r.Join("Amy", "member");
        await Share(r, a, new() { ["Horde|ZZSECRETNODE>QQSECRETNODE"] = Leg(123.4, 7), ["Horde|bad\"SECRETKEY>2"] = Leg(1, 1) }, new() { ["ZZSECRETNODE"] = "SECRETNAMETEXT" });
        await Share(r, a, new() { ["Horde|ZZSECRETNODE>QQSECRETNODE"] = Leg(124.4, 7) }, advance: false);   // rate limited
        r.Clock.Advance(TimeSpan.FromDays(200)); r.Hub.Flight.Tick();                                         // expiry
        string all; lock (log.Lines) all = string.Join("\n", log.Lines);
        Assert.Contains("shared flight times", all);                          // counts are logged ...
        Assert.DoesNotContain("SECRET", all);                                 // ... never the data
        Assert.DoesNotContain("123.4", all);
    }

    [Fact]
    public async Task Garbage_messages_cost_nothing_and_never_throw()
    {
        using var r = new FunRig();
        var a = r.Join("Amy", "member");
        r.Clock.Advance(TimeSpan.FromMinutes(11));
        foreach (var o in new object[] { new { t = "flights.share" }, new { t = "flights.share", legs = "x" }, new { t = "flights.share", legs = new object[] { 1 } }, new { t = "flights.share", legs = new { } } })
            await r.Send(a, o);
        // none of those spent the rate-limit token
        Assert.True((bool)(await Share(r, a, new() { [K] = Leg(100, 3) }, advance: false))["ok"]!);
    }
}
