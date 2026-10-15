using System.Text.Json.Nodes;
using Xunit;

namespace Lodge.Tests;

// Lodge 2.19: learned recipes in the Armory snapshot and "who can craft this" (sub-feature "armory2")
public class ArmoryCraftersTests
{
    static Dictionary<string, object> WithKnown(string name, object known, object? list = null)
    {
        var s = ArmoryTests.Snap(name);
        var pro = (Dictionary<string, object>)s["professions"];
        pro["list"] = list ?? new object[] { new { name = "Alchemy", skill = 150, max = 300, ic = 5, kind = "primary", line = 171 }, new { name = "Cooking", skill = 80, max = 300, kind = "secondary", line = 185 } };
        pro["known"] = known;
        return s;
    }

    static async Task<JsonObject> Put(FunRig r, Member m, string key, object ch)
    {
        r.Clock.Advance(TimeSpan.FromSeconds(9));
        r.Sock(m.Name).Clear();
        await r.Send(m, new { t = "armory.put", key, @char = ch });
        return r.Sock(m.Name).Of("armory.saved").Last();
    }

    static async Task<JsonObject?> Crafters(FunRig r, Member m, object frame)
    {
        r.Clock.Advance(TimeSpan.FromSeconds(2));
        r.Sock(m.Name).Clear();
        var j = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(frame))!.AsObject();
        j["t"] = "armory.crafters";
        await r.Send(m, j);
        return r.Sock(m.Name).Of("armory.crafters").LastOrDefault();
    }

    static async Task<JsonObject> Get(FunRig r, Member m, string owner, string key)
    {
        r.Clock.Advance(TimeSpan.FromSeconds(2));
        r.Sock(m.Name).Clear();
        await r.Send(m, new { t = "armory.get", owner, key });
        return r.Sock(m.Name).Of("armory.char").Last()["char"]!.AsObject();
    }

    static object Known171(params int[] ids) => new Dictionary<string, object> { ["171"] = new { at = 1_790_000_100, partial = false, n = "Alchemy", ids } };

    [Fact]
    public void The_features_announce_armory2_with_the_armory_and_not_when_it_is_off()
    {
        using var r = new FunRig();
        Assert.Contains("armory2", r.Hub.FeatureList());
        using var off = new FunRig(c => c.ArmoryEnabled = false);
        Assert.DoesNotContain("armory2", off.Hub.FeatureList());
        Assert.Equal("2.24.0", LodgeHub.Version);
    }

    [Fact]
    public async Task Known_recipes_are_stored_cleaned_sorted_deduplicated_and_the_line_is_kept()
    {
        using var r = new FunRig();
        var mia = r.Join("Mia", "member");
        var res = await Put(r, mia, "Forever-Aria", WithKnown("Aria", Known171(300, 100, 100, 200)));
        Assert.True((bool)res["ok"]!);
        var ch = await Get(r, mia, "mia", "Forever-Aria");
        var k = ch["professions"]!["known"]!["171"]!;
        Assert.Equal(new[] { 100, 200, 300 }, k["ids"]!.AsArray().Select(x => (int)x!).ToArray());
        Assert.False((bool)k["partial"]!);
        Assert.Equal("Alchemy", (string)k["n"]!);
        Assert.Equal(171, (int)ch["professions"]!["list"]![0]!["line"]!);
    }

    [Fact]
    public async Task Bad_ids_and_lines_are_dropped_only_positive_whole_numbers_survive()
    {
        using var r = new FunRig();
        var mia = r.Join("Mia", "member");
        var known = new Dictionary<string, object>
        {
            ["171"] = new { at = 5, partial = true, ids = new object[] { -5, 0, 1.5, "7", true, null!, 9, 9_999_999_999.0, 42, new { }, new[] { 1 } } },
            ["0"] = new { at = 1, ids = new[] { 5 } },
            ["-3"] = new { at = 1, ids = new[] { 5 } },
            ["abc"] = new { at = 1, ids = new[] { 5 } },
            ["2000000"] = new { at = 1, ids = new[] { 5 } },
            ["185"] = new { at = 1, ids = new int[0] },
            ["186"] = "not an object",
        };
        var res = await Put(r, mia, "Forever-Aria", WithKnown("Aria", known));
        Assert.True((bool)res["ok"]!);
        var kn = (await Get(r, mia, "mia", "Forever-Aria"))["professions"]!["known"]!.AsObject();
        Assert.Equal(new[] { "171" }, kn.Select(x => x.Key).ToArray());
        Assert.Equal(new[] { 9, 42 }, kn["171"]!["ids"]!.AsArray().Select(x => (int)x!).ToArray());
        Assert.True((bool)kn["171"]!["partial"]!);
    }

    [Fact]
    public async Task A_missing_partial_flag_means_partial_and_garbage_known_never_fails_the_put()
    {
        using var r = new FunRig();
        var mia = r.Join("Mia", "member");
        var res = await Put(r, mia, "Forever-Aria", WithKnown("Aria", new Dictionary<string, object> { ["171"] = new { at = 1, ids = new[] { 5 } } }));
        Assert.True((bool)res["ok"]!);
        Assert.True((bool)(await Get(r, mia, "mia", "Forever-Aria"))["professions"]!["known"]!["171"]!["partial"]!);
        foreach (var bad in new object[] { 5, "x", new object[] { 1, 2 }, new Dictionary<string, object> { ["171"] = 7 } })
        {
            res = await Put(r, mia, "Forever-Aria", WithKnown("Aria", bad));
            Assert.True((bool)res["ok"]!);
            Assert.Null((await Get(r, mia, "mia", "Forever-Aria"))["professions"]!["known"]);
        }
    }

    [Fact]
    public async Task At_most_2000_recipe_ids_are_kept_per_character_and_12_professions()
    {
        using var r = new FunRig();
        var mia = r.Join("Mia", "member");
        var known = new Dictionary<string, object>();
        for (int line = 1; line <= 20; line++) known[line.ToString()] = new { at = 1, partial = false, ids = Enumerable.Range(line * 1000, 400).ToArray() };
        var res = await Put(r, mia, "Forever-Aria", WithKnown("Aria", known));
        Assert.True((bool)res["ok"]!);
        var kn = (await Get(r, mia, "mia", "Forever-Aria"))["professions"]!["known"]!.AsObject();
        Assert.Equal(2000, kn.Sum(x => x.Value!["ids"]!.AsArray().Count));
        Assert.True(kn.Count <= 12);
        Assert.Equal(5, kn.Count);                                  // 5 x 400 = 2000
        Assert.Contains("1", kn.Select(x => x.Key));                // the lowest lines are kept
    }

    [Fact]
    public async Task A_huge_known_field_never_makes_the_character_too_big_it_is_dropped_instead()
    {
        using var r = new FunRig();
        var mia = r.Join("Mia", "member");
        var s = WithKnown("Aria", Known171(Enumerable.Range(1_000_000_000, 2000).ToArray()));
        // pad the snapshot so that only the recipes push it over the limit
        var nodes = new List<object>();
        for (int i = 0; i < 250; i++) nodes.Add(new { id = i + 1, r = 1, m = 5, s = 2_000_000_000 + i, ic = 2_000_000_000 + i, x = 1234.5, y = 2345.5, n = new string('N', 48) });
        ((Dictionary<string, object>)s["talents"])["nodes"] = nodes.ToArray();
        var res = await Put(r, mia, "Forever-Aria", s);
        Assert.True((bool)res["ok"]!, res.ToJsonString());
        var ch = await Get(r, mia, "mia", "Forever-Aria");
        Assert.True(ch.ToJsonString().Length <= Armory.MaxSnapshotBytes);
        Assert.Equal(250, ch["talents"]!["nodes"]!.AsArray().Count);
        Assert.Null(ch["professions"]!["known"]);
        Assert.NotNull(ch["professions"]!["list"]);
    }

    [Fact]
    public async Task Who_can_craft_lists_every_shared_character_that_learned_the_recipe_with_member_and_skill()
    {
        using var r = new FunRig();
        var mia = r.Join("Mia", "member"); var bob = r.Join("Bob", "officer"); var elan = r.Join("Elan", "owner");
        await Put(r, mia, "Forever-Aria", WithKnown("Aria", Known171(100, 200)));
        await Put(r, bob, "Forever-Bobby", WithKnown("Bobby", Known171(200, 300), new object[] { new { name = "Alchemy", skill = 290, max = 300, kind = "primary", line = 171 } }));
        await Put(r, elan, "Forever-Nope", WithKnown("Nope", Known171(999)));

        var a = await Crafters(r, bob, new { spell = 200 });
        Assert.NotNull(a);
        var who = a!["who"]!.AsArray().Select(x => x!.AsObject()).ToList();
        Assert.Equal(2, who.Count);
        Assert.Equal("Bobby", (string)who[0]["name"]!);             // the better skill first
        Assert.Equal("Bob", (string)who[0]["by"]!);
        Assert.Equal("Alchemy", (string)who[0]["profession"]!);
        Assert.Equal(290, (int)who[0]["skill"]!);
        Assert.True(who[0]["mine"] != null);
        Assert.Equal("Aria", (string)who[1]["name"]!);
        Assert.Equal("Mia", (string)who[1]["by"]!);
        Assert.Equal(150, (int)who[1]["skill"]!);
        Assert.Equal(200, (int)who[1]["spell"]!);
        Assert.False((bool)who[1]["partial"]!);
        Assert.Equal(new[] { 200 }, a["spells"]!.AsArray().Select(x => (int)x!).ToArray());

        var several = await Crafters(r, elan, new { spells = new[] { 100, 300, 5 } });
        Assert.Equal(2, several!["who"]!.AsArray().Count);
        var none = await Crafters(r, elan, new { spell = 5 });
        Assert.Empty(none!["who"]!.AsArray());
    }

    [Fact]
    public async Task Who_can_craft_answers_only_with_shared_characters_unticked_ones_vanish_and_members_who_leave_take_theirs()
    {
        using var r = new FunRig();
        var mia = r.Join("Mia", "member"); var bob = r.Join("Bob", "officer");
        await Put(r, mia, "Forever-Aria", WithKnown("Aria", Known171(100)));
        Assert.Single((await Crafters(r, bob, new { spell = 100 }))!["who"]!.AsArray());
        r.Clock.Advance(TimeSpan.FromSeconds(2));
        r.Sock("Mia").Clear();
        await r.Send(mia, new { t = "armory.delete", key = "Forever-Aria" });
        Assert.Empty((await Crafters(r, bob, new { spell = 100 }))!["who"]!.AsArray());
    }

    [Fact]
    public async Task A_character_without_a_profession_line_still_answers_by_the_name_the_hub_saw()
    {
        using var r = new FunRig();
        var mia = r.Join("Mia", "member"); var bob = r.Join("Bob", "member");
        var list = new object[] { new { name = "Cooking", skill = 80, max = 300, kind = "secondary" } };           // an older Hub: no line
        await Put(r, mia, "Forever-Aria", WithKnown("Aria", Known171(100), list));
        var row = (await Crafters(r, bob, new { spell = 100 }))!["who"]![0]!;
        Assert.Equal("Alchemy", (string)row["profession"]!);        // the scan's own name
        Assert.Equal(0, (int)row["skill"]!);
    }

    [Fact]
    public async Task Crafters_refuses_guests_garbage_and_a_switched_off_armory_and_is_rate_limited()
    {
        using var r = new FunRig();
        var mia = r.Join("Mia", "member"); var zed = r.Join("Zed", "guest");
        await Put(r, mia, "Forever-Aria", WithKnown("Aria", Known171(100)));
        Assert.Null(await Crafters(r, zed, new { spell = 100 }));
        Assert.Equal("guest", (string)r.Sock("Zed").LastError()!["code"]!);
        foreach (var bad in new object[] { new { }, new { spell = -1 }, new { spell = "x" }, new { spell = 1.5 }, new { spells = new object[] { "a", null!, -3 } }, new { spells = 5 } })
        {
            Assert.Null(await Crafters(r, mia, bad));
            Assert.Equal("invalid", (string)r.Sock("Mia").LastError()!["code"]!);
        }
        // more than 16 ids: the first 16 (sorted) count
        var many = await Crafters(r, mia, new { spells = Enumerable.Range(1, 40).ToArray() });
        Assert.Equal(16, many!["spells"]!.AsArray().Count);
        // a burst of requests is slowed down
        int refused = 0;
        r.Sock("Mia").Clear();
        for (int i = 0; i < 80; i++) await r.Send(mia, new { t = "armory.crafters", spell = 100 });
        refused = r.Sock("Mia").Of("error").Count(f => (string?)f["code"] == "rate");
        Assert.True(refused > 0);

        using var off = new FunRig(c => c.ArmoryEnabled = false);
        var m2 = off.Join("Mia", "member");
        off.Sock("Mia").Clear();
        await off.Send(m2, new { t = "armory.crafters", spell = 100 });
        Assert.Equal("disabled", (string)off.Sock("Mia").LastError()!["code"]!);
    }

    [Fact]
    public async Task An_old_client_without_known_works_unchanged_and_the_list_does_not_grow()
    {
        using var r = new FunRig();
        var mia = r.Join("Mia", "member"); var bob = r.Join("Bob", "member");
        await Put(r, mia, "Forever-Aria", ArmoryTests.Snap());                         // no known, no line (a Hub older than 1.14.0 and an Outpost older than 2.52)
        r.Clock.Advance(TimeSpan.FromSeconds(2));
        r.Sock("Bob").Clear();
        await r.Send(bob, new { t = "armory.list" });
        var c = r.Sock("Bob").Of("armory.chars").Last()["chars"]![0]!.AsObject();
        Assert.Null(c["known"]);
        Assert.Null(c["professions"]![0]!["line"]);                                    // the list view is name, skill, max only
        Assert.Empty((await Crafters(r, bob, new { spell = 100 }))!["who"]!.AsArray());
        Assert.Null((await Get(r, bob, "mia", "Forever-Aria"))["professions"]!["known"]);
    }

    [Fact]
    public async Task A_new_snapshot_with_known_stored_by_a_new_server_loads_again_from_disk()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lodge-armory2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new ArmoryStore(dir, null!, new SystemClockForTests());
            var clean = Armory.Clean(JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(WithKnown("Aria", Known171(300, 100))))!.AsObject(), out var why);
            Assert.NotNull(clean);
            lock (store.Gate) { store.Put("mia", "Mia", "Forever-Aria", clean!); Assert.True(store.Save()); }
            var again = new ArmoryStore(dir, null!, new SystemClockForTests());
            JsonArray who;
            lock (again.Gate) who = again.Crafters(new HashSet<int> { 100 }, (id, login) => login, "mia");
            Assert.Single(who);
            Assert.Equal("Mia", (string)who[0]!["by"]!);
            await Task.CompletedTask;
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    sealed class SystemClockForTests : IClock { public DateTime UtcNow => DateTime.UtcNow; }
}
