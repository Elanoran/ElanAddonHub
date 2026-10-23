using System.Text.Json.Nodes;
using Xunit;

namespace Lodge.Tests;

// Lodge 2.26: counter extras, pickpocket / gathering / contained counters and the corpse dedupe across members (sub-feature "atlas9")
public partial class AtlasTests
{
    static Dictionary<string, object> XCtr(long n, object? items = null, object? x = null, object? z = null)
    {
        var d = new Dictionary<string, object> { ["n"] = n };
        if (items != null) d["items"] = items;
        if (x != null) d["x"] = x;
        if (z != null) d["z"] = z;
        return d;
    }

    [Fact]
    public void The_features_announce_atlas9_when_on_and_not_when_off()
    {
        using var r = new FunRig();
        Assert.Contains("atlas9", r.Hub.FeatureList());
        using var off = new FunRig(c => c.AtlasEnabled = false);
        Assert.DoesNotContain("atlas9", off.Hub.FeatureList());
    }

    [Fact]
    public async Task Pickpocket_gathering_and_contained_are_counters_of_their_own()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member");
        var rep = await Share(r, amy, new
        {
            pickpocket = new Dictionary<string, object> { ["3"] = XCtr(2, new Dictionary<string, object> { ["5428"] = new { n = 2, c = 2 } }) },
            skinning = new Dictionary<string, object> { ["4"] = XCtr(1, new Dictionary<string, object> { ["2318"] = new { n = 1, c = 2 } }, null, new Dictionary<string, object> { ["1429"] = 1 }) },
            mining = new Dictionary<string, object> { ["5"] = XCtr(1) },
            contained = new Dictionary<string, object> { ["5523"] = XCtr(3, new Dictionary<string, object> { ["5498"] = new { n = 1, c = 1 } }) },
        });
        Assert.Equal(4, (int)rep["accepted"]!);
        var d = await GetAll(r, amy);
        Assert.Equal(2, (long)d["pickpocket"]!["3"]!["n"]!);
        Assert.Equal(2, (long)d["skinning"]!["4"]!["items"]!["2318"]!["c"]!);
        Assert.Equal(3, (long)d["contained"]!["5523"]!["n"]!);
        Assert.Empty((JsonObject)d["kills"]!);                                                  // none of it is a kill
        var bad = await Share(r, amy, new { contained = new Dictionary<string, object> { ["99999999"] = XCtr(1) } });
        Assert.Equal(1, (int)bad["rejected"]!);
    }

    [Fact]
    public async Task An_event_builds_loot_is_left_out_of_what_members_get_but_kept()
    {
        // the end of the beta (70532) drops epics: members' Outposts get the rates without that build, the store and the Cairn feed keep it
        using var r = new FunRig();
        var amy = r.Join("Amy", "member");
        var items = new Dictionary<string, object>
        {
            ["3000"] = new { n = 8, c = 8, b = new Dictionary<string, object> { ["70450"] = 6, ["70532"] = 2 } },
            ["19019"] = new { n = 3, c = 3, b = new Dictionary<string, object> { ["70532"] = 3 } },
        };
        await Share(r, amy, new { kills = new Dictionary<string, object> { ["9"] = XCtr(50, items, new { b = new Dictionary<string, object> { ["70450"] = 40, ["70532"] = 10 } }, new Dictionary<string, object> { ["1429"] = 50 }) } });
        var k = (await GetAll(r, amy))["kills"]!["9"]!;
        Assert.Equal(40, (long)k["n"]!);
        Assert.Equal(6, (long)k["items"]!["3000"]!["n"]!);
        Assert.Null(k["items"]!["19019"]);                                                       // only ever seen in the event
        Assert.Null(k["x"]!["b"]!["70532"]);
        Assert.Equal(40, (long)k["z"]!["1429"]!);
        JsonNode raw;
        lock (r.Hub.AtlasPool.Store.Gate) raw = r.Hub.AtlasPool.Store.Get(0, "", _ => "", guest: true, raw: true)["kills"]!["9"]!;
        Assert.Equal(50, (long)raw["n"]!);                                                      // the Cairn feed: as stored
        Assert.Equal(3, (long)raw["items"]!["19019"]!["n"]!);
    }

    [Fact]
    public async Task An_item_without_builds_loses_the_event_kills_share()
    {
        // the Toolkit strips items' builds when its copy is too big: a boar killed 45 of 46 times in the event, its belt (3 drops, no builds) goes;
        // a normal drop on a mostly normal counter keeps nearly all of it
        using var r = new FunRig();
        var amy = r.Join("Amy", "member");
        await Share(r, amy, new { kills = new Dictionary<string, object>
        {
            ["3099"] = XCtr(46, new Dictionary<string, object> { ["288830"] = new { n = 3, c = 3 } }, new { b = new Dictionary<string, object> { ["70532"] = 45 } }),
            ["3100"] = XCtr(100, new Dictionary<string, object> { ["4865"] = new { n = 20, c = 20 } }, new { b = new Dictionary<string, object> { ["70450"] = 90, ["70532"] = 10 } }),
        } });
        var d = await GetAll(r, amy);
        Assert.Equal(1, (long)d["kills"]!["3099"]!["n"]!);
        Assert.Null(d["kills"]!["3099"]!["items"]?["288830"]);
        Assert.Equal(90, (long)d["kills"]!["3100"]!["n"]!);
        Assert.Equal(18, (long)d["kills"]!["3100"]!["items"]!["4865"]!["n"]!);
    }

    [Fact]
    public void Event_builds_come_from_the_environment()
    {
        Assert.Contains(70532, LodgeConfig.EventBuilds(""));
        Assert.Equal(new HashSet<int> { 1, 2 }, LodgeConfig.EventBuilds("1, 2,x"));
        Assert.Empty(LodgeConfig.EventBuilds("none"));
    }

    [Fact]
    public async Task Counter_extras_are_summed_and_a_corpse_two_members_looted_counts_once()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member"); var bob = r.Join("Bob", "member");
        object Items(long n) => new Dictionary<string, object>
        {
            ["750"] = new { n, c = n, q = 33, sx = new Dictionary<string, object> { ["1047"] = n, ["-6"] = 1 }, b = new Dictionary<string, object> { ["70374"] = n } },
        };
        await Share(r, amy, new { kills = new Dictionary<string, object> { ["8"] = XCtr(3, Items(2), new { b = new Dictionary<string, object> { ["70374"] = 3 }, q = new Dictionary<string, object> { ["33"] = 2 }, lv = new[] { 31, 33 }, cl = new Dictionary<string, object> { ["1"] = 3 }, fp = new long[] { 11, 12, 13 } }) } });
        await Share(r, bob, new { kills = new Dictionary<string, object> { ["8"] = XCtr(2, Items(1), new { b = new Dictionary<string, object> { ["70374"] = 1, ["70338"] = 1 }, lv = new[] { 30, 32 }, cl = new Dictionary<string, object> { ["2"] = 1, ["1"] = 1 }, fp = new long[] { 12, 13 } }) } });
        var k = (await GetAll(r, bob))["kills"]!["8"]!;
        Assert.Equal(5, (long)k["n"]!);
        Assert.Equal(2, (long)k["dup"]!);                                                       // corpses 12 and 13 were counted by both
        var x = k["x"]!;
        Assert.Equal(4, (long)x["b"]!["70374"]!); Assert.Equal(1, (long)x["b"]!["70338"]!);
        Assert.Equal(2, (long)x["q"]!["33"]!);
        Assert.Equal(30, (int)x["lv"]![0]!); Assert.Equal(33, (int)x["lv"]![1]!);
        Assert.Equal(4, (long)x["cl"]!["1"]!); Assert.Equal(1, (long)x["cl"]!["2"]!);
        Assert.Null(x["fp"]);                                                                   // fingerprints never leave the server
        var it = k["items"]!["750"]!;
        Assert.Equal(33, (int)it["q"]!); Assert.Equal(3, (long)it["sx"]!["1047"]!); Assert.Equal(2, (long)it["sx"]!["-6"]!); Assert.Equal(3, (long)it["b"]!["70374"]!);

        // a resend counts nothing twice; a new corpse fingerprint is a change
        var again = await Share(r, amy, new { kills = new Dictionary<string, object> { ["8"] = XCtr(3, Items(2), new { fp = new long[] { 12, 11 } }) } });
        Assert.Equal(1, (int)again["unchanged"]!);
        var more = await Share(r, bob, new { kills = new Dictionary<string, object> { ["8"] = XCtr(3, Items(1), new { fp = new long[] { 11 } }) } });
        Assert.Equal(1, (int)more["accepted"]!);
        Assert.Equal(3, (long)(await GetAll(r, bob))["kills"]!["8"]!["dup"]!);

        // the fingerprints survive a restart (the dedupe stays right) and go with the member
        r.Hub.AtlasPool.Flush();
        var reload = new AtlasStore(r.Dir.Path, T.Log, r.Clock);
        JsonObject d;
        lock (reload.Gate) d = reload.Get(0, null, s => s);
        Assert.Equal(3, (long)d["kills"]!["8"]!["dup"]!);
        r.Hub.AtlasPool.Purge("Amy");
        var after = (await GetAll(r, bob))["kills"]!["8"]!;
        Assert.Equal(3, (long)after["n"]!); Assert.Null(after["dup"]);
    }

    [Fact]
    public async Task Old_counters_still_count_and_bad_extras_are_left_out_without_losing_the_counter()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member");
        var rep = await Share(r, amy, new
        {
            kills = new Dictionary<string, object>
            {
                ["1"] = XCtr(2, new Dictionary<string, object> { ["100"] = new { n = 1, c = 1 } }),                     // an older Toolkit: no extras
                ["2"] = XCtr(2, new Dictionary<string, object> { ["100"] = new { n = 1, c = 1, sx = "bear", b = new Dictionary<string, object> { ["x"] = 1 } } },
                    new { b = new Dictionary<string, object> { ["70374"] = 99 }, lv = new[] { 40, 30 }, cl = new Dictionary<string, object> { ["9"] = 1 }, fp = new object[] { 5, "x" } }),
            },
        });
        Assert.Equal(2, (int)rep["accepted"]!);
        var d = await GetAll(r, amy);
        Assert.Equal(2, (long)d["kills"]!["1"]!["n"]!);
        var k2 = d["kills"]!["2"]!;
        Assert.Equal(2, (long)k2["n"]!);
        Assert.Null(k2["x"]); Assert.Null(k2["items"]!["100"]!["sx"]);
        var f = await Share(r, amy, new { finds = new[] { new { i = 5498, c = 1, src = "item:5523", at = Unix(r) - 10, z = 1429, x = 0.5, y = 0.5 } } });
        Assert.Equal(1, (int)f["accepted"]!);
    }
}
