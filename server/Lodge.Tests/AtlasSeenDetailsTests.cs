using System.Text.Json.Nodes;
using Xunit;

namespace Lodge.Tests;

// Lodge 2.25: the verified details of the client caches with the game build (feature "atlas7"): merge by build, member count, purge, validation
public partial class AtlasTests
{
    static Dictionary<string, object> DetQuest(int build, string title = "The Rescue", int money = 1800, string done = "Return to Krusk.") => new()
    {
        ["title"] = title, ["level"] = 22, ["minLevel"] = 17, ["objectives"] = "Free them.", ["build"] = build, ["sort"] = 267, ["type"] = 2, ["flags"] = 8, ["next"] = 533, ["money"] = money,
        ["races"] = new long[] { 0x4e0ab3b2L, 0xaa2aaaaaL }, ["rewards"] = new[] { new[] { 3734, 1 } }, ["choices"] = new[] { new[] { 3752, 1 }, new[] { 3733, 1 } }, ["provided"] = new[] { new[] { 3467, 1 } },
        ["rep"] = new[] { 76 }, ["goals"] = new object[] { new object[] { 2, 1721, 1, "Rescue Drull" }, new object[] { 0, 3917, 12, "" } }, ["desc"] = "Me and some other warriors...", ["done"] = done,
    };

    [Fact]
    public void The_features_announce_atlas7_and_the_version_is_2_25()
    {
        using var r = new FunRig();
        Assert.Contains("atlas7", r.Hub.FeatureList());
        Assert.Equal("2.30.0", LodgeHub.Version);
        using var off = new FunRig(c => c.AtlasEnabled = false);
        Assert.DoesNotContain("atlas7", off.Hub.FeatureList());
    }

    [Fact]
    public async Task Quest_npc_and_object_details_are_stored_and_returned_with_the_member_count()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member"); var bob = r.Join("Bob", "member");
        var rep = await Share(r, amy, new
        {
            seenQuests = new Dictionary<string, object> { ["498"] = DetQuest(70374) },
            seenNpcs = new Dictionary<string, object> { ["2958"] = new Dictionary<string, object> { ["name"] = "Prairie Wolf", ["type"] = 1, ["family"] = 1, ["build"] = 70374, ["tf"] = 0x08000001L },
                ["253272"] = new Dictionary<string, object> { ["name"] = "Magram Necromancer", ["build"] = 70374, ["kc"] = new[] { 253664, 0 } } },
            seenObjects = new Dictionary<string, object> { ["1621"] = new Dictionary<string, object> { ["name"] = "Briarthorn", ["type"] = 3, ["build"] = 70374, ["lock"] = 31 } },
        });
        Assert.Equal(4, (int)rep["accepted"]!);
        var d = await GetAll(r, bob);
        var q = d["seenQuests"]!["498"]!;
        Assert.Equal(70374, (int)q["build"]!); Assert.Equal(267, (int)q["sort"]!); Assert.Equal(533, (int)q["next"]!); Assert.Equal(1800, (int)q["money"]!);
        Assert.Equal(0xaa2aaaaaL, (long)q["races"]![1]!); Assert.Equal(0x4e0ab3b2L, (long)q["races"]![0]!);
        Assert.Equal(2, ((JsonArray)q["choices"]!).Count); Assert.Equal(3467, (int)q["provided"]![0]![0]!); Assert.Equal(76, (int)q["rep"]![0]!);
        Assert.Equal("Rescue Drull", (string)q["goals"]![0]![3]!); Assert.Equal(12, (int)q["goals"]![1]![2]!);
        Assert.Equal("Return to Krusk.", (string)q["done"]!); Assert.Equal("Me and some other warriors...", (string)q["desc"]!);
        Assert.Equal(0x08000001L, (long)d["seenNpcs"]!["2958"]!["tf"]!);
        Assert.Equal(253664, (int)d["seenNpcs"]!["253272"]!["kc"]![0]!);
        Assert.Equal(31, (int)d["seenObjects"]!["1621"]!["lock"]!);
        Assert.Equal(1, (int)d["seenMembers"]!);
        Assert.DoesNotContain("Amy", d.ToJsonString());                                     // a count, never who
        await Share(r, bob, new { seenQuests = new Dictionary<string, object> { ["499"] = DetQuest(70374, "Other") } });
        Assert.Equal(2, (int)(await GetAll(r, bob))["seenMembers"]!);
    }

    [Fact]
    public async Task A_newer_build_replaces_the_same_build_fills_an_older_one_is_ignored_and_resends_change_nothing()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member"); var bob = r.Join("Bob", "member");
        await Share(r, amy, new { seenQuests = new Dictionary<string, object> { ["498"] = new { title = "The Rescue", level = 22 } } });   // an old client: no build
        var up = await Share(r, bob, new { seenQuests = new Dictionary<string, object> { ["498"] = DetQuest(70374) } });
        Assert.Equal(1, (int)up["accepted"]!);                                              // build 70374 beats "no build"
        Assert.Equal(1800, (int)(await GetAll(r, amy))["seenQuests"]!["498"]!["money"]!);
        var again = await Share(r, bob, new { seenQuests = new Dictionary<string, object> { ["498"] = DetQuest(70374) } });
        Assert.Equal(0, (int)again["accepted"]!); Assert.Equal(1, (int)again["unchanged"]!);  // idempotent
        var older = await Share(r, amy, new { seenQuests = new Dictionary<string, object> { ["498"] = DetQuest(70300, "Old Title", 900) } });
        Assert.Equal(1, (int)older["unchanged"]!);
        Assert.Equal("The Rescue", (string)(await GetAll(r, amy))["seenQuests"]!["498"]!["title"]!);
        var patch = await Share(r, amy, new { seenQuests = new Dictionary<string, object> { ["498"] = DetQuest(70400, "The Rescue (patched)", 2000, "") } });
        Assert.Equal(1, (int)patch["accepted"]!);
        var q = (await GetAll(r, bob))["seenQuests"]!["498"]!;
        Assert.Equal(70400, (int)q["build"]!); Assert.Equal(2000, (int)q["money"]!); Assert.Equal("The Rescue (patched)", (string)q["title"]!);
        Assert.Null(q["done"]);                                                             // replaced as a whole: the old build's text does not linger
        // the same build fills only what is empty
        var fill = await Share(r, bob, new { seenQuests = new Dictionary<string, object> { ["498"] = DetQuest(70400, "Ignored", 1, "Return to Krusk.") } });
        Assert.Equal(1, (int)fill["accepted"]!);
        q = (await GetAll(r, bob))["seenQuests"]!["498"]!;
        Assert.Equal("The Rescue (patched)", (string)q["title"]!); Assert.Equal(2000, (int)q["money"]!); Assert.Equal("Return to Krusk.", (string)q["done"]!);
    }

    [Fact]
    public async Task Broken_details_are_refused()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member");
        Dictionary<string, object> With(string k, object v) { var q = DetQuest(70374); q[k] = v; return q; }
        var rep = await Share(r, amy, new
        {
            seenQuests = new Dictionary<string, object>
            {
                ["1"] = With("races", new[] { 1 }),                                     // one half only
                ["2"] = With("races", new long[] { -1, 0 }),
                ["3"] = With("rewards", Enumerable.Range(1, 5).Select(i => new[] { i, 1 }).ToArray()),   // five rewards
                ["4"] = With("goals", new object[] { new object[] { 1, 2 } }),          // a goal of the wrong shape
                ["5"] = With("goals", new object[] { new object[] { 0, 1, 1, "Bad\u0001" } }),
                ["6"] = With("build", 99_999_999),
                ["7"] = With("desc", new string('d', 3001)),
                ["8"] = With("rep", new[] { 0 }),
                ["9"] = DetQuest(70374),
            },
            seenNpcs = new Dictionary<string, object> { ["10"] = new Dictionary<string, object> { ["name"] = "X", ["tf"] = -1 }, ["11"] = new Dictionary<string, object> { ["name"] = "Y", ["kc"] = new[] { 1 } } },
            seenObjects = new Dictionary<string, object> { ["12"] = new Dictionary<string, object> { ["name"] = "Z", ["lock"] = -3 } },
        });
        Assert.Equal(1, (int)rep["accepted"]!);
        Assert.Equal(11, (int)rep["rejected"]!);
    }

    [Fact]
    public async Task The_member_count_and_the_details_survive_a_restart_and_a_revert_takes_the_member_out()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member"); var bob = r.Join("Bob", "member");
        await Share(r, amy, new { seenQuests = new Dictionary<string, object> { ["498"] = DetQuest(70374) } });
        await Share(r, bob, new { seenObjects = new Dictionary<string, object> { ["1621"] = new Dictionary<string, object> { ["name"] = "Briarthorn", ["build"] = 70374, ["lock"] = 31 } } });
        r.Hub.AtlasPool.Flush();
        var store = new AtlasStore(r.Dir.Path, T.Log, r.Clock);
        lock (store.Gate)
        {
            Assert.Equal(2, store.SeenMemberCount);
            var d = store.Get(0, null, x => x);
            Assert.Equal(1800, (int)d["seenQuests"]!["498"]!["money"]!);
            Assert.Equal(31, (int)d["seenObjects"]!["1621"]!["lock"]!);
            var res = store.Purge("amy");
            Assert.Equal(1, res.Seen);
            Assert.Equal(1, store.SeenMemberCount);
            Assert.Equal(1, (int)store.Get(0, null, x => x)["seenMembers"]!);
        }
    }

    [Fact]
    public async Task Guests_cannot_add_details_and_see_only_the_count()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member"); var pat = r.Join("Pat", "guest");
        await Share(r, amy, new { seenQuests = new Dictionary<string, object> { ["498"] = DetQuest(70374) } });
        var rep = await Share(r, pat, new { seenQuests = new Dictionary<string, object> { ["499"] = DetQuest(70374) } });
        Assert.False((bool)rep["ok"]!);
        var d = await GetAll(r, pat);
        Assert.Equal(1, (int)d["seenMembers"]!);
        Assert.Null(d["seenQuests"]!["499"]);
        Assert.DoesNotContain("Amy", d.ToJsonString());
    }
}
