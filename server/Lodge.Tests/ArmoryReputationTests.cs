using System.Text.Json.Nodes;
using Xunit;

namespace Lodge.Tests;

// Reputations in the Armory (sub-feature "reputations"): a shared character may carry its reputations (only when its member switched that on in the Outpost),
// and "armory.standing { faction }" tells where the lodge's shared characters stand with one faction.
public class ArmoryReputationTests
{
    static Dictionary<string, object> WithReps(string name, object reps)
    {
        var s = ArmoryTests.Snap(name);
        s["reps"] = reps;
        return s;
    }

    static object Reps(params object[] list) => new { at = 1_790_000_200, list };

    static async Task<JsonObject> Put(FunRig r, Member m, string key, object ch)
    {
        r.Clock.Advance(TimeSpan.FromSeconds(9));
        r.Sock(m.Name).Clear();
        await r.Send(m, new { t = "armory.put", key, @char = ch });
        return r.Sock(m.Name).Of("armory.saved").Last();
    }

    static async Task<JsonObject?> Standing(FunRig r, Member m, object frame)
    {
        r.Clock.Advance(TimeSpan.FromSeconds(2));
        r.Sock(m.Name).Clear();
        var j = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(frame))!.AsObject();
        j["t"] = "armory.standing";
        await r.Send(m, j);
        return r.Sock(m.Name).Of("armory.standing").LastOrDefault();
    }

    static async Task<JsonObject> Get(FunRig r, Member m, string owner, string key)
    {
        r.Clock.Advance(TimeSpan.FromSeconds(2));
        r.Sock(m.Name).Clear();
        await r.Send(m, new { t = "armory.get", owner, key });
        return r.Sock(m.Name).Of("armory.char").Last()["char"]!.AsObject();
    }

    [Fact]
    public void The_feature_is_announced_with_the_armory_and_not_when_either_switch_is_off()
    {
        using var r = new FunRig();
        Assert.Contains("reputations", r.Hub.FeatureList());
        using var off = new FunRig(c => c.ArmoryRepsEnabled = false);
        Assert.DoesNotContain("reputations", off.Hub.FeatureList());
        Assert.Contains("armory", off.Hub.FeatureList());
        using var noArmory = new FunRig(c => c.ArmoryEnabled = false);
        Assert.DoesNotContain("reputations", noArmory.Hub.FeatureList());
    }

    [Fact]
    public async Task Reputations_are_stored_cleaned_sorted_and_without_names()
    {
        using var r = new FunRig();
        var mia = r.Join("Mia", "member");
        var res = await Put(r, mia, "Forever-Aria", WithReps("Aria", Reps(
            new { id = 72, s = 6, v = 12500, min = 9000, max = 21000, n = "Stormwind" },
            new { id = 47, s = 5, v = 4200, min = 3000, max = 9000 },
            new { id = 72, s = 1, v = 0 },                      // a second entry for the same faction: the first wins
            new { id = 0, s = 5, v = 1 }, new { id = -4, s = 5, v = 1 }, new { id = 1.5, s = 5, v = 1 }, new { id = 2_000_000, s = 5, v = 1 },
            new { id = 69, s = 9, v = 1 }, new { id = 68, s = 0, v = 1 }, new { id = 67, s = 5 }, new { id = 66, s = "5", v = 1 },
            "junk", new { id = 54, s = 8, v = 999_999_999_999.0, min = -999_999_999_999.0 })));
        Assert.True((bool)res["ok"]!);
        var ch = await Get(r, mia, "mia", "Forever-Aria");
        var list = ch["reps"]!["list"]!.AsArray().Select(x => x!.AsObject()).ToList();
        Assert.Equal(new[] { 47, 54, 72 }, list.Select(x => (int)x["id"]!).ToArray());
        Assert.Equal(12500, (int)list[2]["v"]!);
        Assert.Equal(21000, (int)list[2]["max"]!);
        Assert.Null(list[2]["n"]);                                        // names are never kept
        Assert.Equal(100_000_000, (int)list[1]["v"]!);                    // bounded
        Assert.Equal(-100_000_000, (int)list[1]["min"]!);
        Assert.Equal(1_790_000_200L, (long)ch["reps"]!["at"]!);
    }

    [Fact]
    public async Task A_character_without_reputations_is_shared_as_before_and_garbage_never_fails_the_put()
    {
        using var r = new FunRig();
        var mia = r.Join("Mia", "member");
        foreach (var bad in new object[] { "x", 5, new { list = "nope" }, new { list = new object[] { 1, 2 } }, new { } })
        {
            var res = await Put(r, mia, "Forever-Aria", WithReps("Aria", bad));
            Assert.True((bool)res["ok"]!);
            Assert.Null((await Get(r, mia, "mia", "Forever-Aria"))["reps"]);
        }
        Assert.True((bool)(await Put(r, mia, "Forever-Bea", ArmoryTests.Snap("Bea")))["ok"]!);
        Assert.Null((await Get(r, mia, "mia", "Forever-Bea"))["reps"]);
    }

    [Fact]
    public async Task At_most_300_reputations_are_kept()
    {
        using var r = new FunRig();
        var mia = r.Join("Mia", "member");
        var many = Enumerable.Range(1, 500).Select(i => (object)new { id = i, s = 4, v = i, min = 0, max = 3000 }).ToArray();
        Assert.True((bool)(await Put(r, mia, "Forever-Aria", WithReps("Aria", Reps(many))))["ok"]!);
        Assert.Equal(300, (await Get(r, mia, "mia", "Forever-Aria"))["reps"]!["list"]!.AsArray().Count);
    }

    [Fact]
    public async Task Standing_lists_the_shared_characters_with_a_reputation_best_first_and_marks_mine()
    {
        using var r = new FunRig();
        var mia = r.Join("Mia", "member"); var bob = r.Join("Bob", "officer"); var cid = r.Join("Cid", "member");
        await Put(r, mia, "Forever-Aria", WithReps("Aria", Reps(new { id = 72, s = 6, v = 12500, min = 9000, max = 21000 })));
        await Put(r, mia, "Forever-Bea", WithReps("Bea", Reps(new { id = 72, s = 8, v = 42999, min = 42000, max = 43000 })));
        await Put(r, bob, "Forever-Bo", WithReps("Bo", Reps(new { id = 72, s = 6, v = 20000, min = 9000, max = 21000 }, new { id = 47, s = 5, v = 4000 })));
        await Put(r, cid, "Forever-Cy", ArmoryTests.Snap("Cy"));                // shares the character but not its reputations
        var ans = await Standing(r, bob, new { faction = 72 });
        Assert.NotNull(ans);
        Assert.Equal(72, (int)ans!["faction"]!);
        var who = ans["who"]!.AsArray().Select(x => x!.AsObject()).ToList();
        Assert.Equal(new[] { "Bea", "Bo", "Aria" }, who.Select(x => (string)x["name"]!).ToArray());      // Exalted, then Honored with more, then Honored
        Assert.Equal("mia", (string)who[0]["owner"]!);
        Assert.Equal("Mia", (string)who[0]["by"]!);
        Assert.Equal("Forever-Bea", (string)who[0]["key"]!);
        Assert.Equal(8, (int)who[0]["s"]!);
        Assert.Equal(42000, (int)who[0]["min"]!);
        Assert.Equal("HUNTER", (string)who[0]["class"]!);
        Assert.True((bool)who[1]["mine"]!);
        Assert.Null(who[0]["mine"]);
        Assert.DoesNotContain(who, x => (string)x["name"]! == "Cy");
        // nothing else of the character rides along
        Assert.Null(who[0]["gear"]); Assert.Null(who[0]["reps"]); Assert.Null(who[0]["guild"]);
        // another faction
        Assert.Single((await Standing(r, cid, new { faction = 47 }))!["who"]!.AsArray());
        Assert.Empty((await Standing(r, cid, new { faction = 999 }))!["who"]!.AsArray());
    }

    [Fact]
    public async Task Taking_a_character_out_or_leaving_takes_its_reputations_with_it()
    {
        using var r = new FunRig();
        var mia = r.Join("Mia", "member"); var bob = r.Join("Bob", "officer");
        await Put(r, mia, "Forever-Aria", WithReps("Aria", Reps(new { id = 72, s = 6, v = 12500 })));
        Assert.Single((await Standing(r, bob, new { faction = 72 }))!["who"]!.AsArray());
        // the member switched reputation sharing off: the next snapshot comes without them
        await Put(r, mia, "Forever-Aria", ArmoryTests.Snap("Aria"));
        Assert.Empty((await Standing(r, bob, new { faction = 72 }))!["who"]!.AsArray());
        await Put(r, mia, "Forever-Aria", WithReps("Aria", Reps(new { id = 72, s = 6, v = 12500 })));
        r.Clock.Advance(TimeSpan.FromSeconds(2));
        await r.Send(mia, new { t = "armory.delete", all = true });
        Assert.Empty((await Standing(r, bob, new { faction = 72 }))!["who"]!.AsArray());
        await Put(r, mia, "Forever-Aria", WithReps("Aria", Reps(new { id = 72, s = 6, v = 12500 })));
        r.Hub.Armory.RemoveMember("Mia");
        Assert.Empty((await Standing(r, bob, new { faction = 72 }))!["who"]!.AsArray());
    }

    [Fact]
    public async Task Standing_refuses_guests_garbage_a_switched_off_feature_and_is_rate_limited()
    {
        using var r = new FunRig();
        var mia = r.Join("Mia", "member"); var zed = r.Join("Zed", "guest");
        await Put(r, mia, "Forever-Aria", WithReps("Aria", Reps(new { id = 72, s = 6, v = 12500 })));
        Assert.Null(await Standing(r, zed, new { faction = 72 }));
        Assert.Equal("guest", (string)r.Sock("Zed").LastError()!["code"]!);
        foreach (var bad in new object[] { new { }, new { faction = -1 }, new { faction = 0 }, new { faction = "72" }, new { faction = 1.5 }, new { faction = 2_000_000 } })
        {
            Assert.Null(await Standing(r, mia, bad));
            Assert.Equal("invalid", (string)r.Sock("Mia").LastError()!["code"]!);
        }
        r.Sock("Mia").Clear();
        for (int i = 0; i < 80; i++) await r.Send(mia, new { t = "armory.standing", faction = 72 });
        Assert.True(r.Sock("Mia").Of("error").Count(f => (string?)f["code"] == "rate") > 0);

        using var off = new FunRig(c => c.ArmoryRepsEnabled = false);
        var m2 = off.Join("Mia", "member");
        // a client that sends reputations anyway: they are dropped, the character is still shared
        var res = await Put(off, m2, "Forever-Aria", WithReps("Aria", Reps(new { id = 72, s = 6, v = 12500 })));
        Assert.True((bool)res["ok"]!);
        Assert.Null((await Get(off, m2, "mia", "Forever-Aria"))["reps"]);
        off.Sock("Mia").Clear();
        await off.Send(m2, new { t = "armory.standing", faction = 72 });
        Assert.Equal("noreps", (string)off.Sock("Mia").LastError()!["code"]!);

        using var noArmory = new FunRig(c => c.ArmoryEnabled = false);
        var m3 = noArmory.Join("Mia", "member");
        noArmory.Sock("Mia").Clear();
        await noArmory.Send(m3, new { t = "armory.standing", faction = 72 });
        Assert.Equal("disabled", (string)noArmory.Sock("Mia").LastError()!["code"]!);
    }

    [Fact]
    public void Stored_reputations_are_not_served_after_the_owner_switches_the_feature_off()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lodge-reps-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var clean = Armory.Clean(JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(WithReps("Aria", Reps(new { id = 72, s = 6, v = 12500 }))))!.AsObject(), out _, true);
            Assert.NotNull(clean!["reps"]);
            var without = Armory.WithoutReps(clean);
            Assert.Null(without["reps"]);
            Assert.NotNull(clean["reps"]);                                  // the stored snapshot itself is untouched
            Assert.Null(Armory.Clean(JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(WithReps("Aria", Reps(new { id = 72, s = 6, v = 12500 }))))!.AsObject(), out _)!["reps"]);
            var store = new ArmoryStore(dir, null!, new SystemClockForTests());
            lock (store.Gate) { store.Put("mia", "Mia", "Forever-Aria", clean); Assert.True(store.Save()); }
            var again = new ArmoryStore(dir, null!, new SystemClockForTests());
            lock (again.Gate) Assert.Single(again.Standings(72, (id, login) => login, "bob"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    sealed class SystemClockForTests : IClock { public DateTime UtcNow => DateTime.UtcNow; }
}
