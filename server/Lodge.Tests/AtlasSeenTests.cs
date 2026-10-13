using System.Text.Json.Nodes;
using Xunit;

namespace Lodge.Tests;

// Lodge 2.15: seen npc / object / quest details in the Atlas pool (feature "atlas2")
public partial class AtlasTests
{
    static Dictionary<string, object> Npc(string name, string title = "", int type = 0, int family = 0, int rank = 0) =>
        new() { ["name"] = name, ["title"] = title, ["type"] = type, ["family"] = family, ["rank"] = rank };

    [Fact]
    public void The_features_announce_atlas2_when_on_and_not_when_off()
    {
        using var r = new FunRig();
        Assert.Contains("atlas2", r.Hub.FeatureList());
        using var off = new FunRig(c => c.AtlasEnabled = false);
        Assert.DoesNotContain("atlas2", off.Hub.FeatureList());
    }

    [Fact]
    public async Task Seen_details_are_stored_returned_and_validated()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member"); var bob = r.Join("Bob", "member");
        var rep = await Share(r, amy, new
        {
            seenNpcs = new Dictionary<string, object>
            {
                ["1"] = Npc("Hogger", "", 7, 0, 1),
                ["2"] = Npc("Bad\u0001Name"),                                   // control character
                ["3"] = Npc(new string('N', 81)),                               // too long
                ["4"] = Npc(""),                                                // no name
                ["5"] = Npc("Fine", new string('T', 81)),                       // title too long
                ["6"] = new Dictionary<string, object> { ["name"] = 5 },        // not text
                ["7"] = Npc("X", "", -1),                                       // negative type
                ["abc"] = Npc("Key"),
                ["8"] = Npc("Brann", "Explorer", 7, 0, 0),
            },
            seenObjects = new Dictionary<string, object>
            {
                ["10"] = new { name = "Copper Vein", type = 25 },
                ["11"] = new { name = "", type = 1 },
                ["12"] = new { name = "A\nB" },
            },
            seenQuests = new Dictionary<string, object>
            {
                ["20"] = new { title = "Wolves", level = 5, minLevel = 3, objectives = "Kill 8 wolves.\nReturn." },
                ["21"] = new { title = "Long", level = 5, objectives = new string('o', 1001) },
                ["22"] = new { title = "Bad level", level = 9999 },
                ["23"] = new { title = "Any level", level = -1 },
            },
        });
        Assert.True((bool)rep["ok"]!);
        Assert.Equal(2 + 1 + 2, (int)rep["accepted"]!);
        Assert.Equal(7 + 2 + 2, (int)rep["rejected"]!);
        var d = await GetAll(r, bob);
        Assert.Equal("Hogger", (string)d["seenNpcs"]!["1"]!["name"]!);
        Assert.Equal(7, (int)d["seenNpcs"]!["1"]!["type"]!);
        Assert.Equal(1, (int)d["seenNpcs"]!["1"]!["rank"]!);
        Assert.Equal("Explorer", (string)d["seenNpcs"]!["8"]!["title"]!);
        Assert.Equal(2, ((JsonObject)d["seenNpcs"]!).Count);
        Assert.Equal("Copper Vein", (string)d["seenObjects"]!["10"]!["name"]!);
        Assert.Equal(25, (int)d["seenObjects"]!["10"]!["type"]!);
        Assert.Single((JsonObject)d["seenObjects"]!);
        Assert.Equal("Kill 8 wolves.\nReturn.", (string)d["seenQuests"]!["20"]!["objectives"]!);
        Assert.Equal(3, (int)d["seenQuests"]!["20"]!["minLevel"]!);
        Assert.Equal(-1, (int)d["seenQuests"]!["23"]!["level"]!);
        Assert.Equal(2, ((JsonObject)d["seenQuests"]!).Count);
        // the loot-counter and quest-reward sections are not mixed up with them
        Assert.Empty((JsonObject)d["objects"]!); Assert.Empty((JsonObject)d["quests"]!);
    }

    [Fact]
    public async Task The_first_seen_description_stays_and_a_later_one_only_fills_empty_fields()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member"); var bob = r.Join("Bob", "member");
        await Share(r, amy, new
        {
            seenNpcs = new Dictionary<string, object> { ["1"] = Npc("Hogger"), ["2"] = Npc("Brann", "Explorer", 7, 3, 1) },
            seenObjects = new Dictionary<string, object> { ["10"] = new { name = "Vein" } },
            seenQuests = new Dictionary<string, object> { ["20"] = new { title = "Wolves" } },
        });
        var first = await GetAll(r, bob);
        var rev1 = (long)first["rev"]!; var epoch = (string)first["epoch"]!;
        var rep = await Share(r, bob, new
        {
            seenNpcs = new Dictionary<string, object> { ["1"] = Npc("Renamed", "Gnoll", 7, 0, 1), ["2"] = Npc("Other", "Other", 1, 1, 2) },
            seenObjects = new Dictionary<string, object> { ["10"] = new { name = "Renamed", type = 25 } },
            seenQuests = new Dictionary<string, object> { ["20"] = new { title = "Renamed", level = 5, minLevel = 2, objectives = "Kill." } },
        });
        Assert.Equal(3, (int)rep["accepted"]!);
        Assert.Equal(1, (int)rep["unchanged"]!);                                // npc 2 had nothing empty
        var d = await Get(r, bob, rev1, epoch);
        Assert.Equal("Hogger", (string)d["seenNpcs"]!["1"]!["name"]!);          // the name stays
        Assert.Equal("Gnoll", (string)d["seenNpcs"]!["1"]!["title"]!);          // the empty title is filled
        Assert.Equal(1, (int)d["seenNpcs"]!["1"]!["rank"]!);
        Assert.Null(d["seenNpcs"]!["2"]);                                       // unchanged: not sent again
        Assert.Equal("Vein", (string)d["seenObjects"]!["10"]!["name"]!);
        Assert.Equal(25, (int)d["seenObjects"]!["10"]!["type"]!);
        Assert.Equal("Wolves", (string)d["seenQuests"]!["20"]!["title"]!);
        Assert.Equal("Kill.", (string)d["seenQuests"]!["20"]!["objectives"]!);
        Assert.Equal(5, (int)d["seenQuests"]!["20"]!["level"]!);
        var again = await Share(r, bob, new { seenNpcs = new Dictionary<string, object> { ["1"] = Npc("Renamed", "Gnoll", 7, 0, 1) } });
        Assert.Equal(0, (int)again["accepted"]!); Assert.Equal(1, (int)again["unchanged"]!);
    }

    [Fact]
    public async Task Seen_sections_have_per_message_limits_and_table_caps()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member");
        var many = new Dictionary<string, object>();
        for (int i = 1; i <= Atlas.MaxSeenNpcsPerShare + 5; i++) many[i.ToString()] = Npc("N" + i);
        var rep = await Share(r, amy, new { seenNpcs = many });
        Assert.Equal(Atlas.MaxSeenNpcsPerShare, (int)rep["accepted"]!); Assert.Equal(5, (int)rep["rejected"]!);
        var q = new Dictionary<string, object>();
        for (int i = 1; i <= Atlas.MaxSeenQuestsPerShare + 1; i++) q[i.ToString()] = new { title = "Q" + i };
        rep = await Share(r, amy, new { seenQuests = q });
        Assert.Equal(Atlas.MaxSeenQuestsPerShare, (int)rep["accepted"]!); Assert.Equal(1, (int)rep["rejected"]!);

        // the table cap, straight against a store
        var dir = r.Dir.Path + "-caps";
        Directory.CreateDirectory(dir);
        try
        {
            var store = new AtlasStore(dir, T.Log, r.Clock);
            int next = 1;
            while (store.SeenObjectCount < Atlas.MaxSeenObjects)
            {
                var so = new JsonObject();
                for (int k = 0; k < Atlas.MaxSeenObjectsPerShare && store.SeenObjectCount + k < Atlas.MaxSeenObjects; k++) so[(next++).ToString()] = new JsonObject { ["name"] = "O" };
                lock (store.Gate) store.Share("amy", "Amy", new JsonObject { ["seenObjects"] = so });
            }
            Assert.Equal(Atlas.MaxSeenObjects, store.SeenObjectCount);
            AtlasStore.ShareResult sr;
            lock (store.Gate) sr = store.Share("amy", "Amy", new JsonObject { ["seenObjects"] = new JsonObject { [(next + 1).ToString()] = new JsonObject { ["name"] = "Over" } } });
            Assert.Equal(1, sr.Rejected); Assert.Equal(Atlas.MaxSeenObjects, store.SeenObjectCount);
            // an id that is already there can still be filled when the table is full
            lock (store.Gate) sr = store.Share("amy", "Amy", JsonNode.Parse("{\"seenObjects\":{\"1\":{\"name\":\"x\",\"type\":5}}}")!.AsObject());
            Assert.True(sr.Accepted == 1, $"acc={sr.Accepted} unch={sr.Unchanged} rej={sr.Rejected}");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task Seen_details_survive_a_restart_and_come_back_in_incremental_and_paged_answers()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member"); var bob = r.Join("Bob", "member");
        await Share(r, amy, new
        {
            seenNpcs = new Dictionary<string, object> { ["1"] = Npc("Hogger", "", 7, 0, 1) },
            seenObjects = new Dictionary<string, object> { ["10"] = new { name = "Vein", type = 25 } },
            seenQuests = new Dictionary<string, object> { ["20"] = new { title = "Wolves", level = 5, minLevel = 3, objectives = "Kill." } },
        });
        var before = await GetAll(r, bob);
        r.Hub.AtlasPool.Flush();
        var store = new AtlasStore(r.Dir.Path, T.Log, r.Clock);
        JsonObject after;
        lock (store.Gate) after = store.Get(0, null, x => x);
        Assert.Equal(before["rev"]!.ToString(), after["rev"]!.ToString());
        foreach (var s in new[] { "seenNpcs", "seenObjects", "seenQuests" }) Assert.Equal(before[s]!.ToJsonString(), after[s]!.ToJsonString());
        Assert.Equal(1, store.SeenNpcCount); Assert.Equal(1, store.SeenObjectCount); Assert.Equal(1, store.SeenQuestCount);

        // incremental: only what changed after a revision
        var rev = (long)after["rev"]!;
        await Share(r, amy, new { seenNpcs = new Dictionary<string, object> { ["2"] = Npc("Brann") } });
        var d = await Get(r, bob, rev, (string)after["epoch"]!);
        Assert.Single((JsonObject)d["seenNpcs"]!); Assert.Empty((JsonObject)d["seenObjects"]!); Assert.Empty((JsonObject)d["seenQuests"]!);

        // paging: many long quests are cut into pages that add up
        int expected = 1;
        for (int round = 0; round < 4; round++)
        {
            var big = new Dictionary<string, object>();
            for (int i = 0; i < 90; i++) big[(1000 + round * 90 + i).ToString()] = new { title = "Quest " + i, objectives = new string('x', 700) };
            Assert.True((bool)(await Share(r, amy, new { seenQuests = big }))["ok"]!);
            expected += 90;
        }
        int quests = 0, pages = 0; long since = 0; string? epoch = null;
        for (int guard = 0; guard < 50; guard++)
        {
            var p = await Get(r, bob, since, epoch);
            quests += ((JsonObject)p["seenQuests"]!).Count; pages++;
            if (!p.ContainsKey("more")) break;
            since = (long)p["rev"]!; epoch = (string)p["epoch"]!;
        }
        Assert.Equal(expected, quests);
        Assert.True(pages > 1);
    }

    [Fact]
    public async Task An_old_client_without_the_new_sections_is_unaffected_and_garbage_is_refused()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member");
        var rep = await ShareKills(r, amy, "100", Ctr(5));
        Assert.True((bool)rep["ok"]!); Assert.Equal(1, (int)rep["accepted"]!);
        var d = await GetAll(r, amy);
        Assert.Equal(5, N(Kill(d, "100")));
        Assert.Empty((JsonObject)d["seenNpcs"]!);
        var bad = await Share(r, amy, new { seenNpcs = new[] { 1, 2 }, seenObjects = new Dictionary<string, object> { ["5"] = 7 } });
        Assert.Equal(2, (int)bad["rejected"]!);   // a section of the wrong shape and a bad entry are both refused, nothing throws
        var empty = await Share(r, amy, new { seenNpcs = new Dictionary<string, object>() });
        Assert.Equal("empty", (string)empty["why"]!);
    }
}
