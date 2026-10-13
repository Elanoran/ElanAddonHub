using System.Text.Json.Nodes;
using Xunit;

namespace Lodge.Tests;

// Lodge 2.15: crowd-shared Atlas observations (feature "atlas")
public class AtlasTests
{
    static Dictionary<string, object> Item(long n, long c) => new() { ["n"] = n, ["c"] = c };

    // an observation of one source: cumulative kills n, drops { item: (times, total) }, zones { map: n }
    static Dictionary<string, object> Ctr(long n, Dictionary<string, object>? items = null, Dictionary<string, object>? z = null)
    {
        var o = new Dictionary<string, object> { ["n"] = n };
        if (items != null) o["items"] = items;
        if (z != null) o["z"] = z;
        return o;
    }

    static Dictionary<string, object> Kills(string npc, Dictionary<string, object> ctr) => new() { [npc] = ctr };

    static async Task<JsonObject> Share(FunRig r, Member m, object payload, bool advance = true)
    {
        if (advance) r.Clock.Advance(TimeSpan.FromMinutes(3));
        r.Sock(m.Name).Clear();
        var j = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(payload))!.AsObject();
        j["t"] = "atlas.share";
        await r.Send(m, j);
        return r.Sock(m.Name).Of("atlas.shared").Last();
    }

    static async Task<JsonObject> ShareKills(FunRig r, Member m, string npc, Dictionary<string, object> ctr, bool advance = true) =>
        await Share(r, m, new { kills = Kills(npc, ctr) }, advance);

    static async Task<JsonObject> Get(FunRig r, Member m, long since = 0, string? epoch = null)
    {
        r.Clock.Advance(TimeSpan.FromSeconds(2));
        r.Sock(m.Name).Clear();
        await r.Send(m, new { t = "atlas.get", since, epoch });
        return r.Sock(m.Name).Of("atlas.data").Last();
    }

    static async Task<JsonObject> GetAll(FunRig r, Member m) => await Get(r, m);

    // every page of a full download; the finds of all pages in one array
    static async Task<List<JsonNode>> AllFinds(FunRig r, Member m)
    {
        var res = new List<JsonNode>();
        long since = 0; string? epoch = null;
        for (int guard = 0; guard < 50; guard++)
        {
            var d = await Get(r, m, since, epoch);
            foreach (var f in (JsonArray)d["finds"]!) res.Add(f!.DeepClone());
            if (!d.ContainsKey("more")) break;
            since = (long)d["rev"]!; epoch = (string)d["epoch"]!;
        }
        return res;
    }

    static JsonNode? Kill(JsonObject d, string npc) => d["kills"]![npc];
    static long N(JsonNode? o) => (long)o!["n"]!;

    static long Unix(FunRig r) => new DateTimeOffset(r.Clock.UtcNow).ToUnixTimeSeconds();

    // ---------------------------------------------------------------- validation

    [Theory]
    [InlineData("1", true)]
    [InlineData("9999999", true)]
    [InlineData("10000000", false)]
    [InlineData("0", false)]
    [InlineData("01", false)]
    [InlineData("-5", false)]
    [InlineData("1.5", false)]
    [InlineData("1e3", false)]
    [InlineData("12 ", false)]
    [InlineData("", false)]
    [InlineData("a", false)]
    public void Keys_are_plain_positive_ids(string key, bool ok) => Assert.Equal(ok, Atlas.Key(key, Atlas.MaxId, out _));

    [Theory]
    [InlineData("npc:123", true)]
    [InlineData("obj:45", true)]
    [InlineData("vendor:67", true)]
    [InlineData("quest:89", true)]
    [InlineData("fish", true)]
    [InlineData("npc:0", false)]
    [InlineData("npc:123x", false)]
    [InlineData("npc:123\n", false)]
    [InlineData("mob:5", false)]
    [InlineData("", false)]
    public void Find_sources_have_a_strict_format(string src, bool ok) => Assert.Equal(ok, Atlas.ValidSrc(src));

    [Theory]
    [InlineData("0", true)]
    [InlineData("1", true)]
    [InlineData("0.5", true)]
    [InlineData("1.0001", false)]
    [InlineData("-0.1", false)]
    [InlineData("\"0.5\"", false)]
    [InlineData("null", false)]
    [InlineData("true", false)]
    public void Coordinates_are_numbers_from_0_to_1(string json, bool ok) => Assert.Equal(ok, Atlas.Coord(JsonNode.Parse(json), out _));

    [Fact]
    public void Coordinates_are_rounded_to_three_decimals()
    {
        Assert.True(Atlas.Coord(JsonNode.Parse("0.12349"), out var v));
        Assert.Equal(0.123, v);
    }

    // ---------------------------------------------------------------- storing and the aggregate

    [Fact]
    public async Task A_share_stores_what_is_valid_and_reports_counts()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member");
        var rep = await Share(r, amy, new
        {
            kills = new Dictionary<string, object>
            {
                ["100"] = Ctr(10, new() { ["7"] = Item(3, 4) }, new() { ["1429"] = 10 }),
                ["bad"] = Ctr(5),
                ["0"] = Ctr(5),
                ["101"] = Ctr(5, new() { ["7"] = Item(51, 51) }),                  // 51 events from 5 kills
                ["102"] = Ctr(5, new() { ["7"] = Item(2, 1) }),                    // fewer items than events
                ["103"] = Ctr(-1),
                ["104"] = Ctr(100_001),
                ["105"] = "text",
            },
        });
        Assert.True((bool)rep["ok"]!);
        Assert.Equal(1, (int)rep["accepted"]!);
        Assert.Equal(7, (int)rep["rejected"]!);
        var d = await GetAll(r, amy);
        var k = (JsonObject)d["kills"]!;
        Assert.Single(k);
        Assert.Equal(10, N(Kill(d, "100")));
        Assert.Equal(3, (long)Kill(d, "100")!["items"]!["7"]!["n"]!);
        Assert.Equal(4, (long)Kill(d, "100")!["items"]!["7"]!["c"]!);
        Assert.Equal(10, (long)Kill(d, "100")!["z"]!["1429"]!);
        Assert.Equal(1, (int)Kill(d, "100")!["m"]!);
    }

    [Fact]
    public async Task Sending_the_same_batch_twice_counts_nothing_twice()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member");
        var batch = Kills("100", Ctr(10, new() { ["7"] = Item(3, 4) }, new() { ["1429"] = 10 }));
        var a = await Share(r, amy, new { kills = batch });
        var rev1 = (long)a["rev"]!;
        var b = await Share(r, amy, new { kills = batch });
        Assert.True((bool)b["ok"]!);
        Assert.Equal(0, (int)b["accepted"]!);
        Assert.Equal(1, (int)b["unchanged"]!);
        Assert.Equal(rev1, (long)b["rev"]!);                         // nothing changed, so no new revision
        var d = await GetAll(r, amy);
        Assert.Equal(10, N(Kill(d, "100")));
        Assert.Equal(3, (long)Kill(d, "100")!["items"]!["7"]!["n"]!);
    }

    [Fact]
    public async Task A_member_total_grows_with_cumulative_values_and_never_shrinks()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member");
        await ShareKills(r, amy, "100", Ctr(10, new() { ["7"] = Item(3, 4) }));
        await ShareKills(r, amy, "100", Ctr(25, new() { ["7"] = Item(6, 9), ["8"] = Item(1, 1) }));
        var d = await GetAll(r, amy);
        Assert.Equal(25, N(Kill(d, "100")));
        Assert.Equal(6, (long)Kill(d, "100")!["items"]!["7"]!["n"]!);
        Assert.Equal(1, (long)Kill(d, "100")!["items"]!["8"]!["n"]!);
        // an old, smaller upload record is resent (reinstall, restored settings): the stored totals stay
        var rep = await ShareKills(r, amy, "100", Ctr(10, new() { ["7"] = Item(3, 4) }));
        Assert.Equal(1, (int)rep["unchanged"]!);
        d = await GetAll(r, amy);
        Assert.Equal(25, N(Kill(d, "100")));
        Assert.Equal(6, (long)Kill(d, "100")!["items"]!["7"]!["n"]!);
    }

    [Fact]
    public async Task The_aggregate_is_the_sum_over_members()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member"); var bob = r.Join("Bob", "veteran"); var cat = r.Join("Cat", "officer");
        await ShareKills(r, amy, "100", Ctr(10, new() { ["7"] = Item(3, 4) }, new() { ["1429"] = 10 }));
        await ShareKills(r, bob, "100", Ctr(30, new() { ["7"] = Item(5, 5), ["9"] = Item(1, 1) }, new() { ["1429"] = 20, ["1430"] = 10 }));
        await ShareKills(r, cat, "200", Ctr(7));
        var d = await GetAll(r, cat);
        var k = Kill(d, "100")!;
        Assert.Equal(40, N(k));
        Assert.Equal(2, (int)k["m"]!);
        Assert.Equal(8, (long)k["items"]!["7"]!["n"]!);
        Assert.Equal(9, (long)k["items"]!["7"]!["c"]!);
        Assert.Equal(1, (long)k["items"]!["9"]!["n"]!);
        Assert.Equal(30, (long)k["z"]!["1429"]!);
        Assert.Equal(10, (long)k["z"]!["1430"]!);
        Assert.Equal(7, N(Kill(d, "200")));
        // growth by one member moves the sum by exactly that much, a resend by the other does not
        await ShareKills(r, amy, "100", Ctr(12, new() { ["7"] = Item(4, 5) }, new() { ["1429"] = 12 }));
        await ShareKills(r, bob, "100", Ctr(30, new() { ["7"] = Item(5, 5), ["9"] = Item(1, 1) }, new() { ["1429"] = 20, ["1430"] = 10 }));
        d = await GetAll(r, cat);
        Assert.Equal(42, N(Kill(d, "100")));
        Assert.Equal(9, (long)Kill(d, "100")!["items"]!["7"]!["n"]!);
    }

    [Fact]
    public async Task Objects_and_fishing_zones_are_counted_like_kills()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member"); var bob = r.Join("Bob", "member");
        await Share(r, amy, new { objects = new Dictionary<string, object> { ["50"] = Ctr(4, new() { ["11"] = Item(4, 9) }, new() { ["1429"] = 4 }) }, fishing = new Dictionary<string, object> { ["1429"] = Ctr(20, new() { ["13"] = Item(6, 6) }) } });
        await Share(r, bob, new { fishing = new Dictionary<string, object> { ["1429"] = Ctr(5, new() { ["13"] = Item(1, 1) }) } });
        var d = await GetAll(r, bob);
        Assert.Equal(4, N(d["objects"]!["50"]));
        Assert.Equal(9, (long)d["objects"]!["50"]!["items"]!["11"]!["c"]!);
        Assert.Equal(25, N(d["fishing"]!["1429"]));
        Assert.Equal(7, (long)d["fishing"]!["1429"]!["items"]!["13"]!["n"]!);
    }

    // ---------------------------------------------------------------- caps

    [Fact]
    public async Task Absurd_numbers_are_refused_whole()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member");
        var bad = new Dictionary<string, object>
        {
            ["1"] = Ctr(10, new() { ["7"] = Item(101, 101) }),                       // more than 10 drops per kill
            ["2"] = Ctr(10, new() { ["7"] = Item(5, 1001) }),                        // more than 200 per drop
            ["3"] = Ctr(10, new() { ["7"] = Item(5, 4) }),                           // fewer items than drops
            ["4"] = Ctr(10, null, new() { ["1429"] = 7, ["1430"] = 7 }),             // zones add up to more than the kills
            ["5"] = Ctr(10, new() { ["0"] = Item(1, 1) }),                           // item 0
            ["6"] = Ctr(10, new() { ["1000000"] = Item(1, 1) }),                     // item id out of range
            ["7"] = Ctr(10, null, new() { ["100000"] = 5 }),                         // map id out of range
            ["8"] = Ctr(0, new() { ["7"] = Item(1, 1) }),                            // drops without kills
        };
        var rep = await Share(r, amy, new { kills = bad });
        Assert.Equal(0, (int)rep["accepted"]!);
        Assert.Equal(8, (int)rep["rejected"]!);
        Assert.Empty((JsonObject)(await GetAll(r, amy))["kills"]!);
    }

    [Fact]
    public async Task Non_numbers_in_counters_are_refused_not_thrown()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member");
        foreach (var json in new[] { "{\"n\":\"10\"}", "{\"n\":true}", "{\"n\":null}", "{\"n\":[1]}", "{\"n\":10.5}", "{\"n\":1,\"items\":[1]}", "{\"n\":1,\"items\":{\"7\":5}}",
                     "{\"n\":1,\"items\":{\"7\":{\"n\":\"1\",\"c\":1}}}", "{\"n\":1,\"z\":\"x\"}", "5", "\"x\"", "[]", "null" })
        {
            var j = new JsonObject { ["t"] = "atlas.share", ["kills"] = new JsonObject { ["100"] = JsonNode.Parse(json) } };
            r.Clock.Advance(TimeSpan.FromMinutes(3));
            r.Sock("Amy").Clear();
            await r.Send(amy, j);
            Assert.Equal(1, (int)r.Sock("Amy").Of("atlas.shared").Last()["rejected"]!);
        }
        Assert.Empty((JsonObject)(await GetAll(r, amy))["kills"]!);
    }

    [Fact]
    public async Task One_member_cannot_pour_unlimited_counts_into_the_pool()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member");
        // 10 sources of 100 000 reach the 1 000 000 total, the 11th is refused
        var all = new Dictionary<string, object>();
        for (int i = 1; i <= 10; i++) all[(1000 + i).ToString()] = Ctr(100_000);
        var rep = await Share(r, amy, new { kills = all });
        Assert.Equal(10, (int)rep["accepted"]!);
        rep = await ShareKills(r, amy, "2000", Ctr(1));
        Assert.Equal(1, (int)rep["rejected"]!);
        // growing an existing source is refused too once the total is reached
        rep = await ShareKills(r, amy, "1001", Ctr(100_000));
        Assert.Equal(1, (int)rep["unchanged"]!);
    }

    [Fact]
    public async Task A_share_has_per_section_limits()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member");
        var many = new Dictionary<string, object>();
        for (int i = 1; i <= Atlas.MaxKillsPerShare + 5; i++) many[i.ToString()] = Ctr(1);
        var rep = await Share(r, amy, new { kills = many });
        Assert.Equal(Atlas.MaxKillsPerShare, (int)rep["accepted"]!);
        Assert.Equal(5, (int)rep["rejected"]!);
    }

    [Fact]
    public async Task A_sources_item_list_is_bounded()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member");
        var items = new Dictionary<string, object>();
        for (int i = 1; i <= Atlas.MaxItemsPerSource + 1; i++) items[i.ToString()] = Item(1, 1);
        var rep = await ShareKills(r, amy, "100", Ctr(10, items));
        Assert.Equal(1, (int)rep["rejected"]!);
    }

    // ---------------------------------------------------------------- vendors, quests

    static Dictionary<string, object> Vend(long at, int z = 1429, double x = 0.5, double y = 0.5, Dictionary<string, object>? items = null) =>
        new() { ["at"] = at, ["z"] = z, ["x"] = x, ["y"] = y, ["items"] = items ?? new Dictionary<string, object> { ["4540"] = new { p = 25, s = 1, l = -1 } } };

    [Fact]
    public async Task The_newest_vendor_list_wins()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member"); var bob = r.Join("Bob", "member");
        var t = Unix(r);
        await Share(r, amy, new { vendors = new Dictionary<string, object> { ["300"] = Vend(t - 1000) } });
        await Share(r, bob, new { vendors = new Dictionary<string, object> { ["300"] = Vend(t - 100, 1430, 0.4, 0.4, new() { ["4541"] = new { p = 30, s = 5, l = 10 } }) } });
        var rep = await Share(r, amy, new { vendors = new Dictionary<string, object> { ["300"] = Vend(t - 1000) } });   // an older snapshot afterwards
        Assert.Equal(1, (int)rep["unchanged"]!);
        var d = await GetAll(r, amy);
        var v = d["vendors"]!["300"]!;
        Assert.Equal(t - 100, (long)v["at"]!);
        Assert.Equal(1430, (int)v["z"]!);
        Assert.Null(v["items"]!["4540"]);
        Assert.Equal(30, (long)v["items"]!["4541"]!["p"]!);
        Assert.Equal(10, (long)v["items"]!["4541"]!["l"]!);
    }

    [Fact]
    public async Task Vendor_fields_are_checked()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member");
        var t = Unix(r);
        var rep = await Share(r, amy, new
        {
            vendors = new Dictionary<string, object>
            {
                ["1"] = Vend(t + 100_000),                                                                    // from the future
                ["2"] = Vend(5),                                                                              // before 2001
                ["3"] = Vend(t, 1429, 1.5, 0.5),                                                              // x out of range
                ["4"] = Vend(t, 0),                                                                           // map 0
                ["5"] = Vend(t, 1429, 0.5, 0.5, new() { ["7"] = new { p = -1, s = 1, l = -1 } }),             // negative price
                ["6"] = Vend(t, 1429, 0.5, 0.5, new() { ["7"] = new { p = 1, s = 0, l = -1 } }),              // stack 0
                ["7"] = Vend(t, 1429, 0.5, 0.5, new() { ["7"] = new { p = 1, s = 1, l = -2 } }),              // limit -2
                ["8"] = Vend(t, 1429, 0.5, 0.5, new() { ["x"] = new { p = 1, s = 1, l = -1 } }),              // item id not a number
                ["9"] = Vend(t),                                                                              // fine
            },
        });
        Assert.Equal(1, (int)rep["accepted"]!);
        Assert.Equal(8, (int)rep["rejected"]!);
    }

    [Fact]
    public async Task Quest_rewards_keep_the_newest_observation()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member");
        var t = Unix(r);
        await Share(r, amy, new { quests = new Dictionary<string, object> { ["900"] = new { at = t - 50, rewards = new[] { 1, 2 }, choices = new[] { 3, 4, 5 } } } });
        await Share(r, amy, new { quests = new Dictionary<string, object> { ["900"] = new { at = t - 500, rewards = new[] { 9 }, choices = Array.Empty<int>() } } });   // older: ignored
        var d = await GetAll(r, amy);
        Assert.Equal(new[] { 1, 2 }, ((JsonArray)d["quests"]!["900"]!["rewards"]!).Select(x => (int)x!).ToArray());
        Assert.Equal(3, ((JsonArray)d["quests"]!["900"]!["choices"]!).Count);
        var rep = await Share(r, amy, new { quests = new Dictionary<string, object> { ["901"] = new { at = t, rewards = new[] { 0 }, choices = Array.Empty<int>() }, ["902"] = new { at = t, rewards = "x" }, ["903"] = new { at = t, rewards = Enumerable.Range(1, 11).ToArray() } } });
        Assert.Equal(3, (int)rep["rejected"]!);
    }

    // ---------------------------------------------------------------- finds

    static Dictionary<string, object> Find(int item, long at, string src = "npc:100", int count = 1, double x = 0.5, double y = 0.5) =>
        new() { ["i"] = item, ["c"] = count, ["src"] = src, ["z"] = 1429, ["x"] = x, ["y"] = y, ["at"] = at };

    [Fact]
    public async Task Finds_carry_the_members_display_name_and_nothing_from_the_game()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member"); var bob = r.Join("Bob", "member");
        lock (r.Hub.Profiles.Gate) r.Hub.Profiles.For("Amy").DisplayName = "Amy the Bold";
        var t = Unix(r);
        var f = Find(7, t - 10);
        f["char"] = "Thrallsmite-Realm"; f["name"] = "Thrallsmite";            // anything extra is dropped
        await Share(r, amy, new { finds = new[] { f } });
        await Share(r, bob, new { finds = new[] { Find(8, t - 5, "fish", 3) } });
        var d = await GetAll(r, bob);
        var fa = (JsonArray)d["finds"]!;
        Assert.Equal(2, fa.Count);
        Assert.Equal("Amy the Bold", (string)fa[0]!["by"]!);
        Assert.Equal("Bob", (string)fa[1]!["by"]!);
        Assert.DoesNotContain("Thrallsmite", d.ToJsonString());
        Assert.Equal(7, (int)fa[0]!["i"]!);
        Assert.Equal(1429, (int)fa[0]!["z"]!);
    }

    [Fact]
    public async Task A_find_sent_twice_is_stored_once()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member");
        var t = Unix(r);
        await Share(r, amy, new { finds = new[] { Find(7, t - 10), Find(7, t - 10) } });
        var rep = await Share(r, amy, new { finds = new[] { Find(7, t - 10), Find(7, t - 9) } });
        Assert.Equal(1, (int)rep["accepted"]!);
        Assert.Equal(1, (int)rep["unchanged"]!);
        Assert.Equal(2, ((JsonArray)(await GetAll(r, amy))["finds"]!).Count);
    }

    [Fact]
    public async Task Finds_are_validated()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member");
        var t = Unix(r);
        var bad = new List<Dictionary<string, object>>
        {
            Find(0, t), Find(7, t, "npc:0"), Find(7, t, "npc:1;drop"), Find(7, t, "npc:100", 0), Find(7, t, "npc:100", 1001), Find(7, t + 200_000), Find(7, 5),
            Find(7, t, "npc:100", 1, 1.2), Find(1_000_000, t),
        };
        var ok = Find(7, t);
        bad.Add(ok);
        var rep = await Share(r, amy, new { finds = bad });
        Assert.Equal(1, (int)rep["accepted"]!);
        Assert.Equal(9, (int)rep["rejected"]!);
    }

    [Fact]
    public async Task Only_the_newest_2000_finds_are_kept_and_one_member_cannot_fill_the_list()
    {
        using var r = new FunRig();
        var t = Unix(r);
        var members = Enumerable.Range(0, 4).Select(i => r.Join("M" + i, "member")).ToList();
        // each member sends 700 (the per-member cap) in two batches of 350
        foreach (var m in members)
            for (int part = 0; part < 2; part++)
                await Share(r, m, new { finds = Enumerable.Range(0, 350).Select(i => Find(1000 + part * 350 + i, t - 100000 + i)).ToArray() });
        Assert.Equal(2000, (await AllFinds(r, members[0])).Count);   // 4 x 700 = 2800 -> the newest 2000
        // one member alone: never more than 700 in the list
        using var r2 = new FunRig();
        var solo = r2.Join("Solo", "member");
        for (int part = 0; part < 4; part++)
            await Share(r2, solo, new { finds = Enumerable.Range(0, 300).Select(i => Find(1 + part * 300 + i, t - 100000 + part * 300 + i)).ToArray() });
        var fa = await AllFinds(r2, solo);
        Assert.Equal(Atlas.MaxFindsPerMember, fa.Count);
        Assert.Equal(1 + 3 * 300 + 299, fa.Select(x => (int)x!["i"]!).Max());   // the newest stay, the oldest went
    }

    // ---------------------------------------------------------------- spawns, revealed items

    [Fact]
    public async Task Spawn_points_merge_nearby_ones_and_are_bounded()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member"); var bob = r.Join("Bob", "member");
        object P(double x, double y, int z = 1429) => new { z, x, y };
        await Share(r, amy, new { spawns = new Dictionary<string, object> { ["100"] = new[] { P(0.5, 0.5), P(0.505, 0.505), P(0.6, 0.6) } } });
        var d = await GetAll(r, amy);
        Assert.Equal(2, ((JsonArray)d["spawns"]!["100"]!).Count);                       // the second is the same spot as the first
        var rep = await Share(r, amy, new { spawns = new Dictionary<string, object> { ["100"] = new[] { P(0.5, 0.5) } } });
        Assert.Equal(1, (int)rep["unchanged"]!);
        // one member holds at most 10 points of an npc, all members together 50
        await Share(r, amy, new { spawns = new Dictionary<string, object> { ["200"] = Enumerable.Range(0, 20).Select(i => P(0.05 * i, 0.1)).ToArray() } });
        Assert.Equal(10, ((JsonArray)(await GetAll(r, amy))["spawns"]!["200"]!).Count);
        for (int b = 0; b < 6; b++)
        {
            var m = r.Join("S" + b, "member");
            await Share(r, m, new { spawns = new Dictionary<string, object> { ["200"] = Enumerable.Range(0, 10).Select(i => P(0.05 * i, 0.2 + 0.1 * b)).ToArray() } });
        }
        Assert.Equal(50, ((JsonArray)(await GetAll(r, bob))["spawns"]!["200"]!).Count);
        var bad = await Share(r, bob, new { spawns = new Dictionary<string, object> { ["300"] = new object[] { P(1.5, 0.5), new { z = 0, x = 0.5, y = 0.5 }, "x" } } });
        Assert.Equal(1, (int)bad["rejected"]!);
    }

    static Dictionary<string, object> Rev(string name = "Linen Cloth", int q = 1) => new() { ["n"] = name, ["q"] = q, ["il"] = 5, ["slot"] = 0, ["st"] = new[] { new[] { 3, 5 }, new[] { 7, -2 } } };

    [Fact]
    public async Task Revealed_items_are_validated_and_the_first_description_stays()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member"); var bob = r.Join("Bob", "member");
        var rep = await Share(r, amy, new
        {
            items = new Dictionary<string, object>
            {
                ["2589"] = Rev(),
                ["1"] = Rev(new string('N', 200) + "\"\\[x]"),
                ["2"] = Rev("X", 9),                                       // quality 9
                ["3"] = Rev(""),                                           // no name
                ["4"] = new Dictionary<string, object> { ["n"] = "X", ["q"] = 1, ["st"] = new[] { new[] { 1, 99999 } } },    // stat value out of range
                ["5"] = new Dictionary<string, object> { ["n"] = "X", ["q"] = 1, ["il"] = 5000 },
                ["6"] = new Dictionary<string, object> { ["n"] = 5, ["q"] = 1 },
                ["abc"] = Rev(),
            },
        });
        Assert.Equal(2, (int)rep["accepted"]!);
        Assert.Equal(6, (int)rep["rejected"]!);
        await Share(r, bob, new { items = new Dictionary<string, object> { ["2589"] = Rev("Something Else", 4) } });
        var d = await GetAll(r, bob);
        Assert.Equal("Linen Cloth", (string)d["items"]!["2589"]!["n"]!);
        Assert.Equal(1, (int)d["items"]!["2589"]!["q"]!);
        Assert.Equal(-2, (int)d["items"]!["2589"]!["st"]![1]![1]!);
        var longName = (string)d["items"]!["1"]!["n"]!;
        Assert.True(longName.Length <= Atlas.MaxNameLength);
        Assert.DoesNotContain("\"", longName); Assert.DoesNotContain("\\", longName); Assert.DoesNotContain("[", longName);
    }

    // ---------------------------------------------------------------- revisions, paging

    [Fact]
    public async Task Get_since_returns_only_what_changed()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member"); var bob = r.Join("Bob", "member");
        await ShareKills(r, amy, "100", Ctr(10));
        await ShareKills(r, amy, "200", Ctr(20));
        var d1 = await GetAll(r, bob);
        Assert.True((bool)d1["full"]!);
        var rev = (long)d1["rev"]!; var epoch = (string)d1["epoch"]!;
        Assert.False(d1.ContainsKey("more"));
        var none = await Get(r, bob, rev, epoch);
        Assert.False(none.ContainsKey("full")); Assert.Empty((JsonObject)none["kills"]!); Assert.Equal(rev, (long)none["rev"]!);
        await ShareKills(r, amy, "100", Ctr(15));
        var d2 = await Get(r, bob, rev, epoch);
        Assert.Single((JsonObject)d2["kills"]!);
        Assert.Equal(15, N(Kill(d2, "100")));
        Assert.True((long)d2["rev"]! > rev);
        // another member's contribution to the same source replaces the whole record with the new sum
        await ShareKills(r, bob, "100", Ctr(5));
        var d3 = await Get(r, amy, (long)d2["rev"]!, epoch);
        Assert.Equal(20, N(Kill(d3, "100")));
    }

    [Fact]
    public async Task A_client_with_another_epoch_or_a_too_new_revision_gets_everything_again()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member");
        await ShareKills(r, amy, "100", Ctr(10));
        var d = await GetAll(r, amy);
        var rev = (long)d["rev"]!;
        var other = await Get(r, amy, rev, "someotherepoch");
        Assert.True((bool)other["full"]!); Assert.Equal(10, N(Kill(other, "100")));
        var ahead = await Get(r, amy, rev + 1000, (string)d["epoch"]!);
        Assert.True((bool)ahead["full"]!); Assert.Equal(10, N(Kill(ahead, "100")));
        var noEpoch = await Get(r, amy, rev, null);
        Assert.True((bool)noEpoch["full"]!);
    }

    [Fact]
    public async Task A_big_answer_is_cut_into_pages_that_add_up()
    {
        using var r = new FunRig();
        var members = Enumerable.Range(0, 4).Select(i => r.Join("P" + i, "member")).ToList();
        int id = 1;
        foreach (var m in members)
            for (int b = 0; b < 4; b++)
            {
                var many = new Dictionary<string, object>();
                for (int i = 0; i < 100; i++)
                {
                    var items = new Dictionary<string, object>();
                    for (int k = 1; k <= 40; k++) items[(100 + k).ToString()] = Item(1, 1);
                    many[(id++).ToString()] = Ctr(50, items);
                }
                await Share(r, m, new { kills = many });
            }
        var reader = r.Join("Reader", "guest");
        long since = 0; string? epoch = null; int sources = 0, pages = 0; bool first = true;
        while (true)
        {
            var d = await Get(r, reader, since, epoch);
            pages++;
            if (first) { Assert.True((bool)d["full"]!); first = false; } else Assert.False(d.ContainsKey("full"));
            sources += ((JsonObject)d["kills"]!).Count;
            Assert.True(d.ToJsonString().Length < Atlas.PageBytes + 20_000);
            since = (long)d["rev"]!; epoch = (string)d["epoch"]!;
            if (!d.ContainsKey("more")) break;
            Assert.True(pages < 100);
        }
        Assert.Equal(1600, sources);
        Assert.True(pages > 1);
        Assert.Empty((JsonObject)(await Get(r, reader, since, epoch))["kills"]!);
    }

    // ---------------------------------------------------------------- persistence

    [Fact]
    public async Task The_pool_survives_a_restart_with_the_same_revisions()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member"); var bob = r.Join("Bob", "member");
        var t = Unix(r);
        await ShareKills(r, amy, "100", Ctr(10, new() { ["7"] = Item(3, 4) }, new() { ["1429"] = 10 }));
        await ShareKills(r, bob, "100", Ctr(20, new() { ["7"] = Item(1, 1) }));
        await Share(r, amy, new
        {
            objects = new Dictionary<string, object> { ["5"] = Ctr(2, new() { ["9"] = Item(2, 2) }) },
            fishing = new Dictionary<string, object> { ["1429"] = Ctr(3) },
            vendors = new Dictionary<string, object> { ["300"] = Vend(t - 10) },
            quests = new Dictionary<string, object> { ["900"] = new { at = t - 10, rewards = new[] { 1 }, choices = new[] { 2 } } },
            spawns = new Dictionary<string, object> { ["100"] = new[] { new { z = 1429, x = 0.1, y = 0.2 } } },
            items = new Dictionary<string, object> { ["2589"] = Rev() },
            finds = new[] { Find(7, t - 5) },
        });
        var before = await GetAll(r, amy);
        r.Hub.AtlasPool.Flush();
        Assert.True(File.Exists(Path.Combine(r.Dir.Path, "atlas.json")));
        Assert.False(File.Exists(Path.Combine(r.Dir.Path, "atlas.json.tmp")));

        var store = new AtlasStore(r.Dir.Path, T.Log, r.Clock);
        JsonObject after;
        lock (store.Gate) after = store.Get(0, null, x => x);
        Assert.Equal(before["epoch"]!.ToString(), after["epoch"]!.ToString());
        Assert.Equal(before["rev"]!.ToString(), after["rev"]!.ToString());
        foreach (var s in new[] { "kills", "objects", "fishing", "vendors", "quests", "spawns", "items" })
            Assert.Equal(before[s]!.ToJsonString(), after[s]!.ToJsonString());
        Assert.Single((JsonArray)after["finds"]!);
        // per-member data survived too: resending an old batch is still idempotent, and growth still adds to the right member
        AtlasStore.ShareResult sr;
        lock (store.Gate) sr = store.Share("amy", "Amy", JsonNode.Parse("{\"kills\":{\"100\":{\"n\":10,\"items\":{\"7\":{\"n\":3,\"c\":4}},\"z\":{\"1429\":10}}}}")!.AsObject());
        Assert.Equal(1, sr.Unchanged); Assert.False(sr.Changed);
        lock (store.Gate) sr = store.Share("amy", "Amy", JsonNode.Parse("{\"kills\":{\"100\":{\"n\":12,\"items\":{\"7\":{\"n\":3,\"c\":4}},\"z\":{\"1429\":12}}}}")!.AsObject());
        Assert.Equal(1, sr.Accepted);
        lock (store.Gate) after = store.Get(0, null, x => x);
        Assert.Equal(32, N(after["kills"]!["100"]));
    }

    [Fact]
    public async Task A_damaged_or_hostile_file_is_survived_and_its_values_re_checked()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member");
        await ShareKills(r, amy, "100", Ctr(10));
        r.Hub.AtlasPool.Flush();
        var path = Path.Combine(r.Dir.Path, "atlas.json");
        // a value that no share could have carried is dropped on load
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        root["members"]!["amy"]!["kills"]!["666"] = JsonNode.Parse("{\"n\":99999999}");
        root["members"]!["amy"]!["kills"]!["bad key"] = JsonNode.Parse("{\"n\":3}");
        File.WriteAllText(path, root.ToJsonString());
        var store = new AtlasStore(r.Dir.Path, T.Log, r.Clock);
        JsonObject d;
        lock (store.Gate) d = store.Get(0, null, x => x);
        Assert.Single((JsonObject)d["kills"]!);
        File.WriteAllText(path, "{ this is not json");
        var broken = new AtlasStore(r.Dir.Path, T.Log, r.Clock);
        lock (broken.Gate) Assert.Empty((JsonObject)broken.Get(0, null, x => x)["kills"]!);
        // the leftover tmp of an interrupted save is used when the main file is missing
        File.Delete(path);
        File.WriteAllText(path + ".tmp", root.ToJsonString());
        var recovered = new AtlasStore(r.Dir.Path, T.Log, r.Clock);
        lock (recovered.Gate) Assert.Single((JsonObject)recovered.Get(0, null, x => x)["kills"]!);
    }

    [Fact]
    public async Task A_change_reaches_the_disk_within_one_housekeeping_round()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member");
        var path = Path.Combine(r.Dir.Path, "atlas.json");
        await ShareKills(r, amy, "100", Ctr(10));
        r.Hub.AtlasPool.Flush();
        File.Delete(path);
        await ShareKills(r, amy, "100", Ctr(11), advance: false);       // inside 30 s of the last save: kept in memory ...
        Assert.False(File.Exists(path));
        r.Hub.AtlasPool.Tick();                                         // ... and written by the next tick
        Assert.True(File.Exists(path));
        Assert.False(r.Hub.AtlasPool.Store.Dirty);
    }

    // ---------------------------------------------------------------- permissions, switch, budgets

    [Fact]
    public async Task Guests_can_read_but_not_contribute()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member"); var pat = r.Join("Pat", "guest");
        await ShareKills(r, amy, "100", Ctr(10));
        var rep = await ShareKills(r, pat, "200", Ctr(5));
        Assert.False((bool)rep["ok"]!); Assert.Equal("guest", (string)rep["why"]!);
        var d = await GetAll(r, pat);
        Assert.Equal(10, N(Kill(d, "100")));
        Assert.Null(Kill(d, "200"));
        Assert.True(Roles.Can("member", Perm.AtlasShare));
        Assert.False(Roles.Can("guest", Perm.AtlasShare));
        Assert.True(Roles.Can("guest", Perm.AtlasRead));
    }

    [Fact]
    public async Task The_owner_can_switch_the_feature_off()
    {
        using var r = new FunRig(c => c.AtlasEnabled = false);
        var elan = r.Join("Elan", "owner");
        var features = ((JsonArray)r.Hub.Welcome(elan)["features"]!).Select(x => (string)x!).ToList();
        Assert.DoesNotContain("atlas", features);
        Assert.DoesNotContain("atlas", r.Hub.FeatureList());
        var rep = await ShareKills(r, elan, "100", Ctr(10));
        Assert.False((bool)rep["ok"]!); Assert.Equal("disabled", (string)rep["why"]!);
        r.Sock("Elan").Clear();
        await r.Send(elan, new { t = "atlas.get", since = 0 });
        Assert.Empty(r.Sock("Elan").Of("atlas.data"));
        Assert.Equal("disabled", (string)r.Sock("Elan").LastError()!["code"]!);
        Assert.Equal(0, r.Hub.AtlasPool.Store.MemberCount);
    }

    [Fact]
    public void The_welcome_announces_atlas_when_on()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner");
        var features = ((JsonArray)r.Hub.Welcome(elan)["features"]!).Select(x => (string)x!).ToList();
        Assert.Contains("atlas", features);
        Assert.Contains("flights", features);
    }

    [Fact]
    public async Task Shares_are_rate_limited_per_member()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member"); var bob = r.Join("Bob", "member");
        for (int i = 1; i <= Atlas.ShareBurst; i++) Assert.True((bool)(await ShareKills(r, amy, "100", Ctr(i), advance: false))["ok"]!);
        var rep = await ShareKills(r, amy, "100", Ctr(50), advance: false);
        Assert.False((bool)rep["ok"]!); Assert.Equal("rate", (string)rep["why"]!);
        Assert.Equal(Atlas.ShareBurst, N(Kill(await GetAll(r, amy), "100")));          // the refused one changed nothing
        Assert.True((bool)(await ShareKills(r, bob, "100", Ctr(1), advance: false))["ok"]!);   // another member has its own budget
        r.Clock.Advance(Atlas.ShareEvery + TimeSpan.FromSeconds(1));
        Assert.True((bool)(await ShareKills(r, amy, "100", Ctr(60), advance: false))["ok"]!);
    }

    [Fact]
    public async Task Garbage_messages_cost_nothing_and_never_throw()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member");
        r.Clock.Advance(TimeSpan.FromMinutes(3));
        foreach (var o in new object[]
                 {
                     new { t = "atlas.share" }, new { t = "atlas.share", kills = "x" }, new { t = "atlas.share", kills = new object[] { 1 } }, new { t = "atlas.share", kills = new { } },
                     new { t = "atlas.share", finds = new { a = 1 } }, new { t = "atlas.share", spawns = 5 }, new { t = "atlas.share", vendors = new object[] { new { } } },
                 })
            await r.Send(amy, o);
        // an empty or shapeless message did not spend a token: the full burst is still there
        for (int i = 1; i <= Atlas.ShareBurst; i++) Assert.True((bool)(await ShareKills(r, amy, "100", Ctr(i), advance: false))["ok"]!);
        foreach (var o in new object[] { new { t = "atlas.get", since = "x" }, new { t = "atlas.get", since = -5, epoch = 5 }, new { t = "atlas.get" }, new { t = "atlas.get", since = 1e30 } })
        {
            r.Clock.Advance(TimeSpan.FromSeconds(2));
            r.Sock("Amy").Clear();
            await r.Send(amy, o);
            Assert.Single(r.Sock("Amy").Of("atlas.data"));
        }
    }

    [Fact]
    public async Task Reading_spends_the_page_budget()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member");
        int ok = 0;
        for (int i = 0; i < 30; i++)
        {
            r.Sock("Amy").Clear();
            await r.Send(amy, new { t = "atlas.get", since = 0 });
            if (r.Sock("Amy").Of("atlas.data").Count == 1) ok++;
        }
        Assert.Equal(10, ok);   // a burst of 10 (the clock does not move here)
        Assert.NotNull(r.Sock("Amy").LastError());
    }

    [Fact]
    public async Task Nothing_that_was_shared_reaches_the_logs()
    {
        var log = new CaptureLogger();
        using var r = new FunRig(log: log);
        var amy = r.Join("Amy", "member");
        var t = Unix(r);
        await Share(r, amy, new
        {
            kills = Kills("123456", Ctr(7, new() { ["987654"] = Item(1, 1) })),
            items = new Dictionary<string, object> { ["555"] = Rev("SECRETITEMNAME") },
            finds = new[] { Find(424242, t - 10) },
        });
        r.Hub.AtlasPool.Tick();
        string all; lock (log.Lines) all = string.Join("\n", log.Lines);
        Assert.Contains("shared atlas observations", all);
        Assert.DoesNotContain("SECRET", all);
        Assert.DoesNotContain("123456", all);
        Assert.DoesNotContain("987654", all);
        Assert.DoesNotContain("424242", all);
    }
}
