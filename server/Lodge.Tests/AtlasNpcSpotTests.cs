using System.Text.Json.Nodes;
using Xunit;

namespace Lodge.Tests;

// Lodge 2.26: where members met, killed or saw npcs (sub-feature "atlas8")
public partial class AtlasTests
{
    static Dictionary<string, object> Seen(int z, double x, double y, int k, long n) => new() { ["z"] = z, ["x"] = x, ["y"] = y, ["k"] = k, ["n"] = n };
    static object SeenAt(string npc, params Dictionary<string, object>[] spots) => new Dictionary<string, object> { [npc] = spots };
    static JsonArray SpotsOf(JsonObject d, string npc) => (JsonArray)d["npcspots"]![npc]!;

    [Fact]
    public void The_features_announce_atlas8_when_on_and_not_when_off_and_the_version_is_2_26()
    {
        using var r = new FunRig();
        Assert.Contains("atlas8", r.Hub.FeatureList());
        Assert.Equal("2.31.1", LodgeHub.Version);
        using var off = new FunRig(c => c.AtlasEnabled = false);
        Assert.DoesNotContain("atlas8", off.Hub.FeatureList());
    }

    [Fact]
    public async Task Npc_spots_are_merged_per_kind_and_spot_with_member_counts_and_a_resend_never_counts_twice()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member"); var bob = r.Join("Bob", "member"); var cat = r.Join("Cat", "member");
        var rep = await Share(r, amy, new { npcspots = SeenAt("466", Seen(1429, 0.4211, 0.6534, 1, 3)) });
        Assert.True((bool)rep["ok"]!); Assert.Equal(1, (int)rep["accepted"]!);
        var again = await Share(r, amy, new { npcspots = SeenAt("466", Seen(1429, 0.4212, 0.6534, 1, 3)) });
        Assert.Equal(0, (int)again["accepted"]!); Assert.Equal(1, (int)again["unchanged"]!);
        await Share(r, bob, new { npcspots = SeenAt("466", Seen(1429, 0.4251, 0.6534, 1, 1)) });   // a step away: the same spot
        await Share(r, cat, new { npcspots = SeenAt("466", Seen(1429, 0.4211, 0.6534, 2, 5), Seen(1453, 0.2, 0.3, 1, 1)) });   // a kill at the same place is another kind; a second place
        var l = SpotsOf(await GetAll(r, cat), "466");
        Assert.Equal(3, l.Count);
        var top = l[0]!;
        Assert.Equal(1429, (int)top["z"]!); Assert.Equal(1, (int)top["k"]!); Assert.Equal(2, (int)top["m"]!); Assert.Equal(4, (long)top["n"]!);   // amy 3 + bob 1
        Assert.Equal(0.4221, (double)top["x"]!, 4);                                                                 // weighted by n: (0.4211 * 3 + 0.4251) / 4
        Assert.Contains(l, s => (int)s!["k"]! == 2 && (int)s["m"]! == 1 && (long)s["n"]! == 5);
        Assert.Contains(l, s => (int)s!["z"]! == 1453 && (int)s["m"]! == 1);
    }

    [Fact]
    public async Task Many_kill_spots_never_push_out_the_one_place_an_npc_was_met()
    {
        using var r = new FunRig();
        var members = Enumerable.Range(0, 3).Select(i => r.Join("M" + i, "member")).ToList();
        for (int i = 0; i < members.Count; i++)
            await Share(r, members[i], new { npcspots = SeenAt("299", Enumerable.Range(0, 10).Select(j => Seen(1429, 0.05 + 0.09 * j, 0.1 + 0.2 * i, 2, 9)).ToArray()) });
        var vendor = r.Join("Vic", "member");
        await Share(r, vendor, new { npcspots = SeenAt("299", Seen(1429, 0.5, 0.95, 1, 1)) });
        var l = SpotsOf(await GetAll(r, vendor), "299");
        Assert.Equal(Atlas.MaxNpcSpots, l.Count);
        Assert.Contains(l, s => (int)s!["k"]! == 1);
    }

    [Fact]
    public async Task Bad_npc_spots_are_refused_whole_and_guests_cannot_share()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member");
        Assert.Equal(1, (int)(await Share(r, amy, new { npcspots = SeenAt("466", Seen(1429, 1.5, 0.4, 1, 1)) }))["rejected"]!);
        Assert.Equal(1, (int)(await Share(r, amy, new { npcspots = SeenAt("466", Seen(0, 0.5, 0.4, 1, 1)) }))["rejected"]!);
        Assert.Equal(1, (int)(await Share(r, amy, new { npcspots = SeenAt("0", Seen(1429, 0.5, 0.4, 1, 1)) }))["rejected"]!);
        Assert.Equal(1, (int)(await Share(r, amy, new { npcspots = SeenAt("466", Seen(1429, 0.5, 0.4, 4, 1)) }))["rejected"]!);   // no such kind
        Assert.Equal(1, (int)(await Share(r, amy, new { npcspots = SeenAt("466", Seen(1429, 0.5, 0.4, 0, 1)) }))["rejected"]!);
        Assert.Equal(1, (int)(await Share(r, amy, new { npcspots = SeenAt("466", Seen(1429, 0.5, 0.4, 1, 0)) }))["rejected"]!);
        Assert.Equal(1, (int)(await Share(r, amy, new { npcspots = SeenAt("466", Seen(1429, 0.5, 0.4, 1, 1), new() { ["z"] = 1429, ["x"] = 0.5, ["y"] = 0.4, ["k"] = "i", ["n"] = 1 }) }))["rejected"]!);
        var eleven = Enumerable.Range(0, 11).Select(i => Seen(1429, 0.05 * (i + 1), 0.5, 2, 1)).ToArray();
        Assert.Equal(1, (int)(await Share(r, amy, new { npcspots = SeenAt("466", eleven) }))["rejected"]!);
        Assert.False((bool)(await Share(r, amy, new { npcspots = 5 }))["ok"]!);                                   // not a section: nothing to share
        Assert.Null((await GetAll(r, amy))["npcspots"]!["466"]);
        var guest = r.Join("Gus", "guest");
        var g = await Share(r, guest, new { npcspots = SeenAt("466", Seen(1429, 0.5, 0.4, 1, 1)) });
        Assert.False((bool)g["ok"]!);
    }

    [Fact]
    public async Task Guests_read_the_merge_with_counts_and_no_names()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member");
        await Share(r, amy, new { npcspots = SeenAt("466", Seen(1429, 0.42, 0.65, 1, 2)) });
        var guest = r.Join("Gus", "guest");
        var d = await GetAll(r, guest);
        var s = SpotsOf(d, "466")[0]!.AsObject();
        Assert.Equal(1, (int)s["m"]!);
        Assert.DoesNotContain("Amy", s.ToJsonString());
        Assert.Equal(new[] { "k", "m", "n", "x", "y", "z" }, s.Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Npc_spots_survive_a_restart_come_in_deltas_and_go_with_the_member()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member"); var bob = r.Join("Bob", "member");
        await Share(r, amy, new { npcspots = SeenAt("466", Seen(1429, 0.42, 0.65, 1, 2)) });
        await Share(r, bob, new { npcspots = SeenAt("466", Seen(1429, 0.42, 0.65, 1, 1)) });
        await Share(r, bob, new { npcspots = SeenAt("61", Seen(1431, 0.61, 0.33, 3, 1)) });
        var full = await GetAll(r, bob);
        long rev = (long)full["rev"]!; string epoch = (string)full["epoch"]!;
        await Share(r, amy, new { npcspots = SeenAt("299", Seen(1429, 0.55, 0.25, 2, 4)) });
        var delta = await Get(r, bob, rev, epoch);
        Assert.False(delta.ContainsKey("full"));
        Assert.Single((JsonObject)delta["npcspots"]!);
        Assert.NotNull(delta["npcspots"]!["299"]);
        r.Hub.AtlasPool.Flush();
        var again = new AtlasStore(r.Dir.Path, T.Log, r.Clock);
        JsonObject d;
        lock (again.Gate) d = again.Get(0, null, s => s);
        var s466 = ((JsonArray)d["npcspots"]!["466"]!)[0]!;
        Assert.Equal(3, (long)s466["n"]!); Assert.Equal(2, (int)s466["m"]!);
        var res = r.Hub.AtlasPool.Purge("Amy");
        Assert.Equal(2, res.NpcSpots);
        var after = await GetAll(r, bob);
        Assert.Null(after["npcspots"]!["299"]);
        var left = SpotsOf(after, "466")[0]!;
        Assert.Equal(1, (long)left["n"]!); Assert.Equal(1, (int)left["m"]!);
        Assert.NotNull(after["npcspots"]!["61"]);
    }
}
