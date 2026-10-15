using System.Text.Json.Nodes;
using Xunit;

namespace Lodge.Tests;

// Lodge 2.24: instance entrances seen by members (sub-feature "atlas6")
public partial class AtlasTests
{
    static Dictionary<string, object> Door(int z, double x, double y, long n) => new() { ["z"] = z, ["x"] = x, ["y"] = y, ["n"] = n };
    static object Doors(string instance, params Dictionary<string, object>[] spots) => new Dictionary<string, object> { [instance] = spots };
    static JsonArray DoorsOf(JsonObject d, string instance) => (JsonArray)d["entrances"]![instance]!;

    [Fact]
    public void The_features_announce_atlas6_when_on_and_not_when_off()
    {
        using var r = new FunRig();
        Assert.Contains("atlas6", r.Hub.FeatureList());
        using var off = new FunRig(c => c.AtlasEnabled = false);
        Assert.DoesNotContain("atlas6", off.Hub.FeatureList());
    }

    [Fact]
    public async Task Entrances_are_merged_by_spot_with_member_counts_and_a_resend_never_counts_twice()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member"); var bob = r.Join("Bob", "member"); var cat = r.Join("Cat", "member");
        var rep = await Share(r, amy, new { entrances = Doors("2784", Door(1440, 0.64123456, 0.41, 3)) });
        Assert.True((bool)rep["ok"]!); Assert.Equal(1, (int)rep["accepted"]!);
        var again = await Share(r, amy, new { entrances = Doors("2784", Door(1440, 0.6412, 0.41, 3)) });
        Assert.Equal(0, (int)again["accepted"]!); Assert.Equal(1, (int)again["unchanged"]!);
        await Share(r, bob, new { entrances = Doors("2784", Door(1440, 0.645, 0.412, 1)) });
        await Share(r, cat, new { entrances = Doors("2784", Door(1453, 0.2, 0.3, 9)) });   // a summon: somewhere else, one member
        var l = DoorsOf(await GetAll(r, cat), "2784");
        Assert.Equal(2, l.Count);
        Assert.Equal(1440, (int)l[0]!["z"]!); Assert.Equal(2, (int)l[0]!["m"]!); Assert.Equal(4, (long)l[0]!["n"]!);   // most members first: amy 3 + bob 1
        Assert.Equal(0.6412, (double)l[0]!["x"]!, 6);
        Assert.Equal(1, (int)l[1]!["m"]!);
    }

    [Fact]
    public async Task Bad_entrances_are_refused_whole_and_guests_cannot_share()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member");
        Assert.Equal(1, (int)(await Share(r, amy, new { entrances = Doors("2784", Door(1440, 1.5, 0.4, 1)) }))["rejected"]!);
        Assert.Equal(1, (int)(await Share(r, amy, new { entrances = Doors("2784", Door(0, 0.5, 0.4, 1)) }))["rejected"]!);
        Assert.Equal(1, (int)(await Share(r, amy, new { entrances = Doors("0", Door(1440, 0.5, 0.4, 1)) }))["rejected"]!);
        Assert.Equal(1, (int)(await Share(r, amy, new { entrances = Doors("2784", Door(1440, 0.5, 0.4, 0)) }))["rejected"]!);
        var nine = Enumerable.Range(0, 9).Select(i => Door(1440, 0.1 * i, 0.5, 1)).ToArray();
        Assert.Equal(1, (int)(await Share(r, amy, new { entrances = Doors("2784", nine) }))["rejected"]!);
        var guest = r.Join("Gus", "guest");
        var g = await Share(r, guest, new { entrances = Doors("2784", Door(1440, 0.5, 0.4, 1)) });
        Assert.False((bool)g["ok"]!);
    }

    [Fact]
    public async Task Entrances_survive_a_restart_and_go_with_the_member()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member"); var bob = r.Join("Bob", "member");
        await Share(r, amy, new { entrances = Doors("2784", Door(1440, 0.6, 0.4, 2)) });
        await Share(r, bob, new { entrances = Doors("2875", Door(1431, 0.3, 0.7, 1)) });
        r.Hub.AtlasPool.Flush();
        var again = new AtlasStore(r.Dir.Path, T.Log, r.Clock);
        JsonObject d;
        lock (again.Gate) d = again.Get(0, null, s => s);
        Assert.Equal(2, (long)((JsonArray)d["entrances"]!["2784"]!)[0]!["n"]!);
        var res = r.Hub.AtlasPool.Purge("Amy");
        Assert.Equal(1, res.Entrances);
        var after = await GetAll(r, bob);
        Assert.Null(after["entrances"]!["2784"]);
        Assert.NotNull(after["entrances"]!["2875"]);
    }
}
