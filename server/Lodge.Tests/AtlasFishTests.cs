using System.Text.Json.Nodes;
using Xunit;

namespace Lodge.Tests;

// Lodge 2.21: fishing spots (sub-feature "atlas4") and the extended-cost flag `x` of vendor items
public partial class AtlasTests
{
    static Dictionary<string, object> Spot(double x, double y, long n, params (int id, long c)[] items)
    {
        var o = new Dictionary<string, object> { ["x"] = x, ["y"] = y, ["n"] = n };
        if (items.Length > 0) o["items"] = items.ToDictionary(i => i.id.ToString(), i => (object)i.c);
        return o;
    }
    static object Fish(string map, params Dictionary<string, object>[] spots) => new Dictionary<string, object> { [map] = spots };
    static JsonArray FishOf(JsonObject d, string map) => (JsonArray)d["fishspots"]![map]!;

    [Fact]
    public void The_features_announce_atlas4_when_on_and_not_when_off()
    {
        using var r = new FunRig();
        Assert.Contains("atlas4", r.Hub.FeatureList());
        using var off = new FunRig(c => c.AtlasEnabled = false);
        Assert.DoesNotContain("atlas4", off.Hub.FeatureList());
    }

    [Fact]
    public async Task Fishing_spots_are_stored_with_their_items_and_returned_to_everybody()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member"); var bob = r.Join("Bob", "member");
        var rep = await Share(r, amy, new { fishspots = Fish("1456", Spot(0.41234567, 0.594, 5, (6289, 3), (6291, 2))) });
        Assert.True((bool)rep["ok"]!); Assert.Equal(1, (int)rep["accepted"]!);
        var l = FishOf(await GetAll(r, bob), "1456");
        Assert.Single(l);
        Assert.Equal(0.4123, (double)l[0]!["x"]!, 6); Assert.Equal(5, (long)l[0]!["n"]!);
        Assert.Equal(3, (long)l[0]!["items"]!["6289"]!); Assert.Equal(2, (long)l[0]!["items"]!["6291"]!);
    }

    [Fact]
    public async Task Fishing_spots_merge_within_the_radius_a_resend_never_counts_twice_and_members_add_up()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member"); var bob = r.Join("Bob", "member");
        await Share(r, amy, new { fishspots = Fish("1456", Spot(0.4, 0.5, 5, (1, 3)), Spot(0.8, 0.8, 1)) });
        var again = await Share(r, amy, new { fishspots = Fish("1456", Spot(0.4, 0.5, 5, (1, 3)), Spot(0.8, 0.8, 1)) });
        Assert.Equal(0, (int)again["accepted"]!); Assert.Equal(1, (int)again["unchanged"]!);
        await Share(r, bob, new { fishspots = Fish("1456", Spot(0.405, 0.505, 2, (1, 1), (2, 1))) });
        var l = FishOf(await GetAll(r, bob), "1456");
        Assert.Equal(2, l.Count);
        Assert.Equal(7, (long)l[0]!["n"]!);                       // 5 (amy) + 2 (bob)
        Assert.Equal(4, (long)l[0]!["items"]!["1"]!);             // 3 + 1
        Assert.Equal(1, (long)l[0]!["items"]!["2"]!);
    }

    [Fact]
    public async Task Bad_fishing_spots_are_refused_whole()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member");
        var bad = await Share(r, amy, new { fishspots = Fish("1456", Spot(0.4, 0.5, 5), Spot(1.5, 0.5, 1)) });
        Assert.Equal(1, (int)bad["rejected"]!); Assert.Equal(0, (int)bad["accepted"]!);
        var badItem = await Share(r, amy, new { fishspots = Fish("1456", Spot(0.4, 0.5, 5, (0, 1))) });
        Assert.Equal(1, (int)badItem["rejected"]!);
        var badMap = await Share(r, amy, new { fishspots = Fish("0", Spot(0.4, 0.5, 5)) });
        Assert.Equal(1, (int)badMap["rejected"]!);
        var badN = await Share(r, amy, new { fishspots = Fish("1456", Spot(0.4, 0.5, 0)) });
        Assert.Equal(1, (int)badN["rejected"]!);
    }

    [Fact]
    public async Task Fishing_spots_survive_a_restart()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member");
        await Share(r, amy, new { fishspots = Fish("1456", Spot(0.4, 0.5, 5, (1, 3))) });
        r.Hub.AtlasPool.Flush();
        var again = new AtlasStore(r.Dir.Path, T.Log, r.Clock);
        JsonObject d;
        lock (again.Gate) d = again.Get(0, null, s => s);
        var l = (JsonArray)d["fishspots"]!["1456"]!;
        Assert.Equal(5, (long)l[0]!["n"]!); Assert.Equal(3, (long)l[0]!["items"]!["1"]!);
    }
    [Fact]
    public async Task A_vendor_item_keeps_its_extended_cost_flag_and_a_corrected_price_replaces_the_zero_rows()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member"); var bob = r.Join("Bob", "member");
        long t = 1_790_000_000;
        object Vendor(long at, long p, int x) => new Dictionary<string, object>
        {
            ["300"] = new Dictionary<string, object>
            {
                ["at"] = at, ["z"] = 1429, ["x"] = 0.5, ["y"] = 0.5,
                ["items"] = new Dictionary<string, object>
                {
                    ["4540"] = new Dictionary<string, object> { ["p"] = p, ["s"] = 1, ["l"] = -1 },
                    ["4541"] = x == 1 ? new Dictionary<string, object> { ["p"] = 0, ["s"] = 1, ["l"] = -1, ["x"] = 1 } : new Dictionary<string, object> { ["p"] = 7, ["s"] = 1, ["l"] = -1 },
                },
            },
        };
        await Share(r, amy, new { vendors = Vendor(t, 0, 0) });                    // Toolkit 0.5.1: every price 0
        var rep = await Share(r, amy, new { vendors = Vendor(t + 60, 250, 1) });  // 0.5.2: real prices + flag, scanned later
        Assert.Equal(1, (int)rep["accepted"]!);
        var v = (JsonObject)(await GetAll(r, bob))["vendors"]!["300"]!;
        Assert.Equal(250, (long)v["items"]!["4540"]!["p"]!);
        Assert.Null(v["items"]!["4540"]!["x"]);
        Assert.Equal(1, (long)v["items"]!["4541"]!["x"]!);
        var bad = await Share(r, amy, new { vendors = new Dictionary<string, object> { ["301"] = new Dictionary<string, object> { ["at"] = t, ["z"] = 1, ["x"] = 0.5, ["y"] = 0.5, ["items"] = new Dictionary<string, object> { ["5"] = new Dictionary<string, object> { ["p"] = 1, ["s"] = 1, ["l"] = -1, ["x"] = 7 } } } } });
        Assert.Equal(1, (int)bad["rejected"]!);
    }
}
