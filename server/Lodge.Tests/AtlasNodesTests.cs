using System.Text.Json.Nodes;
using Xunit;

namespace Lodge.Tests;

// Lodge 2.18: gathering node positions in the Atlas pool (sub-feature "atlas3")
public partial class AtlasTests
{
    static Dictionary<string, object> Pt(double x, double y, long n) => new() { ["x"] = x, ["y"] = y, ["n"] = n };
    static Dictionary<string, object> Nodes(string obj, string map, params Dictionary<string, object>[] pts) =>
        new() { [obj] = new Dictionary<string, object> { [map] = pts } };

    static JsonArray NodesOf(JsonObject d, string obj, string map) => (JsonArray)d["nodes"]![obj]![map]!;

    [Fact]
    public void The_features_announce_atlas3_when_on_and_not_when_off()
    {
        using var r = new FunRig();
        Assert.Contains("atlas3", r.Hub.FeatureList());
        using var off = new FunRig(c => c.AtlasEnabled = false);
        Assert.DoesNotContain("atlas3", off.Hub.FeatureList());
        Assert.Equal("2.31.4", LodgeHub.Version);
    }

    [Fact]
    public async Task Nodes_are_stored_returned_rounded_to_four_decimals()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member"); var bob = r.Join("Bob", "member");
        var rep = await Share(r, amy, new { nodes = Nodes("1731", "1411", Pt(0.41234567, 0.5531, 3)) });
        Assert.True((bool)rep["ok"]!); Assert.Equal(1, (int)rep["accepted"]!);
        var d = await GetAll(r, bob);
        var l = NodesOf(d, "1731", "1411");
        Assert.Single(l);
        Assert.Equal(0.4123, (double)l[0]!["x"]!, 6); Assert.Equal(0.5531, (double)l[0]!["y"]!, 6); Assert.Equal(3, (long)l[0]!["n"]!);
    }

    [Fact]
    public async Task Points_within_the_merge_radius_are_one_node_and_a_resend_never_counts_twice()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member"); var bob = r.Join("Bob", "member");
        await Share(r, amy, new { nodes = Nodes("1731", "1411", Pt(0.4, 0.5, 3), Pt(0.7, 0.7, 1)) });
        var again = await Share(r, amy, new { nodes = Nodes("1731", "1411", Pt(0.4, 0.5, 3), Pt(0.7, 0.7, 1)) });
        Assert.Equal(0, (int)again["accepted"]!); Assert.Equal(1, (int)again["unchanged"]!);
        // a nearby point of the SAME member is its node: max, not sum
        await Share(r, amy, new { nodes = Nodes("1731", "1411", Pt(0.405, 0.503, 5)) });
        var d = await GetAll(r, bob);
        var l = NodesOf(d, "1731", "1411");
        Assert.Equal(2, l.Count);
        Assert.Equal(5, (long)l[0]!["n"]!);
        // another member's point inside the radius is the same node, summed with the first member's max; outside (0.009) it is a node of its own
        await Share(r, bob, new { nodes = Nodes("1731", "1411", Pt(0.4079, 0.5, 2), Pt(0.4, 0.509, 4)) });
        d = await GetAll(r, bob);
        l = NodesOf(d, "1731", "1411");
        Assert.Equal(3, l.Count);
        Assert.Contains(l, n => (long)n!["n"]! == 7);   // 5 (amy) + 2 (bob)
        Assert.Contains(l, n => (long)n!["n"]! == 4);
        Assert.Contains(l, n => (long)n!["n"]! == 1);
    }

    [Fact]
    public async Task The_largest_n_of_a_member_never_goes_down_and_growth_replaces_it()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member"); var bob = r.Join("Bob", "member");
        await Share(r, amy, new { nodes = Nodes("5", "1", Pt(0.1, 0.1, 9)) });
        var low = await Share(r, amy, new { nodes = Nodes("5", "1", Pt(0.1, 0.1, 2)) });
        Assert.Equal(1, (int)low["unchanged"]!);
        await Share(r, amy, new { nodes = Nodes("5", "1", Pt(0.1, 0.1, 12)) });
        Assert.Equal(12, (long)NodesOf(await GetAll(r, bob), "5", "1")[0]!["n"]!);
    }

    [Fact]
    public async Task Invalid_node_entries_are_refused_whole()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member"); var bob = r.Join("Bob", "member");
        var rep = await Share(r, amy, new
        {
            nodes = new Dictionary<string, object>
            {
                ["abc"] = new Dictionary<string, object> { ["1"] = new[] { Pt(0.1, 0.1, 1) } },
                ["0"] = new Dictionary<string, object> { ["1"] = new[] { Pt(0.1, 0.1, 1) } },
                ["10"] = new Dictionary<string, object> { ["1"] = new[] { Pt(1.5, 0.1, 1) } },       // x out of range
                ["11"] = new Dictionary<string, object> { ["1"] = new[] { Pt(0.1, 0.1, 0) } },       // n < 1
                ["12"] = new Dictionary<string, object> { ["1"] = new[] { Pt(0.1, 0.1, 100001) } },   // n too big
                ["13"] = new Dictionary<string, object> { ["1"] = new[] { Pt(0.1, 0.1, 1), Pt(0.2, -0.5, 1) } },   // one bad point spoils the entry
                ["14"] = new Dictionary<string, object> { ["x"] = new[] { Pt(0.1, 0.1, 1) } },        // map key not a number
                ["15"] = new Dictionary<string, object> { ["100000"] = new[] { Pt(0.1, 0.1, 1) } },   // map id too big
                ["16"] = 5,
                ["17"] = new Dictionary<string, object> { ["1"] = new object[] { new { x = "0.1", y = 0.1, n = 1 } } },
                ["20"] = new Dictionary<string, object> { ["1"] = new[] { Pt(0.1, 0.1, 1) } },        // the only good one
            },
        });
        Assert.Equal(1, (int)rep["accepted"]!); Assert.Equal(10, (int)rep["rejected"]!);
        var d = await GetAll(r, bob);
        Assert.Single((JsonObject)d["nodes"]!);
        // a section of the wrong shape is no content at all
        rep = await Share(r, amy, new { nodes = new[] { 1, 2 } });
        Assert.Equal("empty", (string)rep["why"]!);
    }

    [Fact]
    public async Task Caps_per_member_per_object_per_share_and_per_merged_object_map()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member"); var bob = r.Join("Bob", "member"); var cat = r.Join("Cat", "member");
        Dictionary<string, object>[] Grid(double y0, long n, int count) =>
            Enumerable.Range(0, count).Select(i => Pt(0.001 + (i % 50) * 0.02, y0 + (i / 50) * 0.02, n)).ToArray();
        // 151 points of one member on one map: refused whole (150 is the limit)
        var rep = await Share(r, amy, new { nodes = Nodes("1", "1", Grid(0.0, 1, 151)) });
        Assert.Equal(1, (int)rep["rejected"]!);
        rep = await Share(r, amy, new { nodes = Nodes("1", "1", Grid(0.0, 1, 150)) });
        Assert.Equal(1, (int)rep["accepted"]!);
        // three members with 150 distinct points each: the merged list of the map is capped at 300, the most looted first
        await Share(r, bob, new { nodes = Nodes("1", "1", Grid(0.3, 10, 150)) });
        await Share(r, cat, new { nodes = Nodes("1", "1", Grid(0.6, 20, 150)) });
        var l = NodesOf(await GetAll(r, amy), "1", "1");
        Assert.Equal(300, l.Count);
        Assert.Equal(20, (long)l[0]!["n"]!);
        Assert.DoesNotContain(l, n => (long)n!["n"]! == 1);
        // too many objects in one share: only the first 50 are looked at
        var objs = new Dictionary<string, object>();
        for (int i = 0; i < 60; i++) objs[(1000 + i).ToString()] = new Dictionary<string, object> { ["2"] = new[] { Pt(0.5, 0.5, 1) } };
        rep = await Share(r, amy, new { nodes = objs });
        Assert.Equal(50, (int)rep["accepted"]!); Assert.Equal(10, (int)rep["rejected"]!);
    }

    [Fact]
    public async Task Nodes_follow_the_revision_paging_and_survive_a_restart()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member"); var bob = r.Join("Bob", "member");
        await Share(r, amy, new { nodes = Nodes("7", "10", Pt(0.1, 0.2, 3)) });
        var first = await GetAll(r, bob);
        long rev = (long)first["rev"]!; var epoch = (string)first["epoch"]!;
        var none = await Get(r, bob, rev, epoch);
        Assert.Empty((JsonObject)none["nodes"]!);
        await Share(r, amy, new { nodes = Nodes("8", "10", Pt(0.3, 0.3, 1)) });
        var inc = await Get(r, bob, rev, epoch);
        Assert.Single((JsonObject)inc["nodes"]!); Assert.NotNull(inc["nodes"]!["8"]);
        r.Hub.AtlasPool.Flush();
        var store = new AtlasStore(r.Dir.Path, T.Log, r.Clock);
        JsonObject after;
        lock (store.Gate) after = store.Get(0, null, x => x);
        Assert.Equal(2, ((JsonObject)after["nodes"]!).Count);
        Assert.Equal(3, (long)after["nodes"]!["7"]!["10"]![0]!["n"]!);
        AtlasStore.ShareResult sr;
        lock (store.Gate) sr = store.Share("amy", "Amy", JsonNode.Parse("{\"nodes\":{\"7\":{\"10\":[{\"x\":0.1,\"y\":0.2,\"n\":3}]}}}")!.AsObject());
        Assert.Equal(1, sr.Unchanged); Assert.False(sr.Changed);
    }

    [Fact]
    public async Task A_guest_cannot_share_nodes_and_everybody_can_read_them()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member"); var guest = r.Join("Gus", "guest");
        var rep = await Share(r, guest, new { nodes = Nodes("1", "1", Pt(0.1, 0.1, 1)) });
        Assert.False((bool)rep["ok"]!); Assert.Equal("guest", (string)rep["why"]!);
        await Share(r, amy, new { nodes = Nodes("1", "1", Pt(0.1, 0.1, 1)) });
        Assert.Single(NodesOf(await GetAll(r, guest), "1", "1"));
        using var r2 = new FunRig();
        var bob = r2.Join("Bob", "member");
        var empty = await Share(r2, bob, new { nodes = new Dictionary<string, object>() });
        Assert.Equal("empty", (string)empty["why"]!);
    }
}
