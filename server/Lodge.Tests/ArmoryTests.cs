using System.Text.Json.Nodes;
using Xunit;

namespace Lodge.Tests;

// Lodge 2.16: the Armory - characters members choose to share (feature "armory")
public class ArmoryTests
{
    const string Link = "|cff0070dd|Hitem:3000:2564:::::::60:::::|h[Linen Bag]|h|r";

    internal static Dictionary<string, object> Snap(string name = "Aria", string cls = "HUNTER", int level = 60, string realm = "Forever")
    {
        return new Dictionary<string, object>
        {
            ["name"] = name, ["realm"] = realm, ["class"] = cls, ["classText"] = "Hunter", ["race"] = "Night Elf", ["raceFile"] = "NightElf", ["sex"] = 3, ["faction"] = "Alliance",
            ["guild"] = "Friends", ["level"] = level, ["ilvl"] = 41.5, ["at"] = 1_790_000_000,
            ["gear"] = new Dictionary<string, object>
            {
                ["at"] = 1_790_000_000, ["ilvl"] = 41.5,
                ["slots"] = new Dictionary<string, object>
                {
                    ["1"] = new { i = 3000, q = 2, ic = 132, il = 40 },
                    ["16"] = new { i = 3001, q = 3, ic = 133, il = 45 },
                },
                ["links"] = new Dictionary<string, object> { ["3000"] = Link },
                ["stats"] = new { health = 3000, power = 1200, str = 50, agi = 160, sta = 120, armor = 800, ap = 400, crit = 6.5, powerType = "MANA", melee = new { min = 40, max = 70, speed = 2.1 } },
            },
            ["talents"] = new Dictionary<string, object>
            {
                ["at"] = 1_790_000_000, ["api"] = "traits", ["spent"] = 40, ["unspent"] = 2, ["build"] = "ETB1-AAECAwQFBgcICQoL",
                ["nodes"] = new object[] { new { id = 11, r = 3, m = 5, s = 900, ic = 100, x = 1.0, y = 2.0, n = "Deadly Aspects" }, new { id = 12, r = 0, m = 1 } },
            },
            ["professions"] = new Dictionary<string, object>
            {
                ["at"] = 1_790_000_000,
                ["list"] = new object[] { new { name = "Alchemy", skill = 150, max = 300, ic = 5, kind = "primary" }, new { name = "Cooking", skill = 80, max = 300, kind = "secondary" } },
            },
            ["pets"] = new Dictionary<string, object>
            {
                ["at"] = 1_790_000_000, ["active"] = "p1",
                ["pets"] = new object[] { new { key = "p1", name = "Fang", family = "Wolf", level = 40, loyalty = 4, abilities = new object[] { new { id = "bite", rank = 5 } } } },
                ["known"] = new Dictionary<string, object> { ["bite"] = 6 },
            },
        };
    }

    static async Task<JsonObject> Put(FunRig r, Member m, string key, object ch, bool advance = true)
    {
        if (advance) r.Clock.Advance(TimeSpan.FromSeconds(9));
        r.Sock(m.Name).Clear();
        await r.Send(m, new { t = "armory.put", key, @char = ch });
        return r.Sock(m.Name).Of("armory.saved").Last();
    }

    static async Task<JsonObject> List(FunRig r, Member m)
    {
        r.Clock.Advance(TimeSpan.FromSeconds(2));
        r.Sock(m.Name).Clear();
        await r.Send(m, new { t = "armory.list" });
        return r.Sock(m.Name).Of("armory.chars").Last();
    }

    static async Task<JsonObject?> Get(FunRig r, Member m, string owner, string key)
    {
        r.Clock.Advance(TimeSpan.FromSeconds(2));
        r.Sock(m.Name).Clear();
        await r.Send(m, new { t = "armory.get", owner, key });
        return r.Sock(m.Name).Of("armory.char").LastOrDefault();
    }

    static async Task<JsonObject> Delete(FunRig r, Member m, object frame)
    {
        r.Clock.Advance(TimeSpan.FromSeconds(1));
        r.Sock(m.Name).Clear();
        var j = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(frame))!.AsObject();
        j["t"] = "armory.delete";
        await r.Send(m, j);
        return r.Sock(m.Name).Of("armory.deleted").Last();
    }

    // ---------------------------------------------------------------- feature, put, list, get, delete

    [Fact]
    public void The_welcome_announces_the_armory_and_the_version()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner");
        var features = ((JsonArray)r.Hub.Welcome(elan)["features"]!).Select(x => (string)x!).ToList();
        Assert.Contains("armory", features);
        Assert.Contains("atlas", features);
        Assert.Equal("2.29.0", LodgeHub.Version);
    }

    [Fact]
    public async Task A_member_shares_a_character_and_everybody_in_the_lodge_can_list_and_open_it()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner"); var bob = r.Join("Bob", "officer"); var mia = r.Join("Mia", "member");
        var res = await Put(r, mia, "Forever-Aria", Snap());
        Assert.True((bool)res["ok"]!); Assert.True((bool)res["stored"]!); Assert.Equal("Forever-Aria", (string)res["key"]!);

        foreach (var who in new[] { elan, bob, mia })
        {
            var list = await List(r, who);
            var c = Assert.Single((JsonArray)list["chars"]!)!.AsObject();
            Assert.Equal("Aria", (string)c["name"]!);
            Assert.Equal("HUNTER", (string)c["class"]!);
            Assert.Equal(60, (int)c["level"]!);
            Assert.Equal("mia", (string)c["owner"]!);
            Assert.Equal("Mia", (string)c["by"]!);
            Assert.Equal("Forever-Aria", (string)c["key"]!);
            Assert.Equal(41.5, (double)c["ilvl"]!);
            Assert.Equal("Alchemy", (string)c["professions"]![0]!["name"]!);
            Assert.Equal(who == mia, c["mine"] != null);
            Assert.Null(c["gear"]);                       // the list is summaries only
        }

        var full = await Get(r, bob, "mia", "Forever-Aria");
        Assert.NotNull(full);
        var ch = full!["char"]!.AsObject();
        Assert.Equal("Mia", (string)full["by"]!);
        Assert.Equal(3000, (int)ch["gear"]!["slots"]!["1"]!["i"]!);
        Assert.Equal(Link, (string)ch["gear"]!["links"]!["3000"]!);
        Assert.Equal("ETB1-AAECAwQFBgcICQoL", (string)ch["talents"]!["build"]!);
        Assert.Single((JsonArray)ch["talents"]!["nodes"]!);            // the unspent node (r = 0) is not shared
        Assert.Equal(150, (int)ch["professions"]!["list"]![0]!["skill"]!);
        Assert.Equal("Fang", (string)ch["pets"]!["pets"]![0]!["name"]!);
    }

    [Fact]
    public async Task Nothing_but_the_armory_fields_is_stored_no_bags_bank_gold_mail_or_zone()
    {
        using var r = new FunRig();
        var mia = r.Join("Mia", "member"); var bob = r.Join("Bob", "officer");
        var snap = Snap();
        snap["bags"] = new { cont = new { n = 16 } }; snap["bank"] = new { cont = 1 }; snap["money"] = 123456789; snap["mail"] = new[] { "gold for you" };
        snap["zone"] = "Stormwind City"; snap["subzone"] = "Trade District"; snap["xp"] = 5; snap["inventory"] = new[] { 1, 2, 3 };
        ((Dictionary<string, object>)snap["gear"])["bags"] = 1;
        ((Dictionary<string, object>)snap["gear"])["extra"] = "x";
        var res = await Put(r, mia, "Forever-Aria", snap);
        Assert.True((bool)res["ok"]!);
        var ch = (await Get(r, bob, "mia", "Forever-Aria"))!["char"]!.AsObject();
        var text = ch.ToJsonString();
        foreach (var word in new[] { "bags", "bank", "money", "mail", "zone", "Stormwind", "xp", "inventory", "extra", "123456789" }) Assert.DoesNotContain(word, text);
        var keys = ch.Select(kv => kv.Key).OrderBy(x => x).ToList();
        Assert.Equal(new[] { "at", "class", "classText", "faction", "gear", "guild", "ilvl", "level", "name", "pets", "professions", "race", "raceFile", "realm", "sex", "talents" }, keys);
    }

    [Fact]
    public async Task Putting_the_same_character_again_replaces_it_and_an_unchanged_one_is_not_a_change()
    {
        using var r = new FunRig();
        var mia = r.Join("Mia", "member");
        Assert.True((bool)(await Put(r, mia, "Forever-Aria", Snap()))["stored"]!);
        Assert.False((bool)(await Put(r, mia, "Forever-Aria", Snap()))["stored"]!);
        Assert.True((bool)(await Put(r, mia, "Forever-Aria", Snap(level: 61)))["stored"]!);
        var list = await List(r, mia);
        var c = Assert.Single((JsonArray)list["chars"]!)!.AsObject();
        Assert.Equal(61, (int)c["level"]!);
    }

    [Fact]
    public async Task A_member_takes_one_character_or_all_of_them_out()
    {
        using var r = new FunRig();
        var mia = r.Join("Mia", "member");
        await Put(r, mia, "Forever-Aria", Snap());
        await Put(r, mia, "Forever-Bela", Snap("Bela", "MAGE", 30));
        await Put(r, mia, "Forever-Cato", Snap("Cato", "ROGUE", 20));
        Assert.Equal(3, ((JsonArray)(await List(r, mia))["chars"]!).Count);
        var one = await Delete(r, mia, new { key = "Forever-Bela" });
        Assert.True((bool)one["ok"]!); Assert.Equal(1, (int)one["removed"]!); Assert.Equal("Forever-Bela", (string)one["key"]!);
        Assert.Equal(2, ((JsonArray)(await List(r, mia))["chars"]!).Count);
        var again = await Delete(r, mia, new { key = "Forever-Bela" });
        Assert.True((bool)again["ok"]!); Assert.Equal(0, (int)again["removed"]!);
        var all = await Delete(r, mia, new { all = true });
        Assert.Equal(2, (int)all["removed"]!);
        Assert.Empty((JsonArray)(await List(r, mia))["chars"]!);
        Assert.Null(await Get(r, mia, "mia", "Forever-Aria"));
        Assert.NotNull(r.Sock("Mia").LastError());
    }

    // ---------------------------------------------------------------- auth

    [Fact]
    public async Task Nobody_can_overwrite_or_delete_another_members_character()
    {
        using var r = new FunRig();
        var mia = r.Join("Mia", "member"); var bob = r.Join("Bob", "officer"); var elan = r.Join("Elan", "owner");
        await Put(r, mia, "Forever-Aria", Snap(level: 60));

        // Bob puts a character with the same key: it is HIS (his identity), Mia's stays as it was
        await Put(r, bob, "Forever-Aria", Snap(level: 12));
        Assert.Equal(60, (int)(await Get(r, elan, "mia", "Forever-Aria"))!["char"]!["level"]!);
        Assert.Equal(12, (int)(await Get(r, elan, "bob", "Forever-Aria"))!["char"]!["level"]!);

        // delete always means "mine": a frame naming Mia's owner/key does nothing to hers, an officer or owner has no power over it either
        var d = await Delete(r, bob, new { key = "Forever-Aria", owner = "mia" });
        Assert.Equal(1, (int)d["removed"]!);                                     // that was Bob's own
        Assert.NotNull(await Get(r, elan, "mia", "Forever-Aria"));
        var d2 = await Delete(r, elan, new { key = "Forever-Aria", owner = "mia" });
        Assert.Equal(0, (int)d2["removed"]!);
        var d3 = await Delete(r, elan, new { all = true, owner = "mia" });
        Assert.Equal(0, (int)d3["removed"]!);
        Assert.Equal(60, (int)(await Get(r, elan, "mia", "Forever-Aria"))!["char"]!["level"]!);
    }

    [Fact]
    public async Task Guests_neither_share_nor_browse()
    {
        using var r = new FunRig();
        var mia = r.Join("Mia", "member"); var zed = r.Join("Zed", "guest");
        await Put(r, mia, "Forever-Aria", Snap());
        var res = await Put(r, zed, "Forever-Zed", Snap("Zed"));
        Assert.False((bool)res["ok"]!); Assert.Equal("guest", (string)res["why"]!);
        var del = await Delete(r, zed, new { all = true });
        Assert.False((bool)del["ok"]!);
        r.Clock.Advance(TimeSpan.FromSeconds(2));
        r.Sock("Zed").Clear();
        await r.Send(zed, new { t = "armory.list" });
        Assert.Empty(r.Sock("Zed").Of("armory.chars"));
        Assert.Equal("guest", (string)r.Sock("Zed").LastError()!["code"]!);
        r.Sock("Zed").Clear();
        await r.Send(zed, new { t = "armory.get", owner = "mia", key = "Forever-Aria" });
        Assert.Empty(r.Sock("Zed").Of("armory.char"));
        Assert.NotNull(r.Sock("Zed").LastError());
    }

    [Fact]
    public async Task The_armory_can_be_switched_off()
    {
        using var r = new FunRig(c => c.ArmoryEnabled = false);
        var mia = r.Join("Mia", "member");
        var features = ((JsonArray)r.Hub.Welcome(mia)["features"]!).Select(x => (string)x!).ToList();
        Assert.DoesNotContain("armory", features);
        var res = await Put(r, mia, "Forever-Aria", Snap());
        Assert.False((bool)res["ok"]!); Assert.Equal("disabled", (string)res["why"]!);
        r.Sock("Mia").Clear();
        await r.Send(mia, new { t = "armory.list" });
        Assert.Equal("disabled", (string)r.Sock("Mia").LastError()!["code"]!);
    }

    // ---------------------------------------------------------------- validation and limits

    [Fact]
    public async Task Garbage_is_refused_not_thrown()
    {
        using var r = new FunRig();
        var mia = r.Join("Mia", "member");
        foreach (var bad in new object?[] { null, 5, "x", new object[] { 1 }, new { }, new { name = "A" }, new { name = "A", @class = "hunter", level = 5 }, new { name = "A", @class = "HUNTER", level = 0, gear = new { slots = new { } } },
                     new { name = "A", @class = "HUNTER", level = 5 }, new { name = 7, @class = "HUNTER", level = 5 }, new { name = "A", @class = new[] { 1 }, level = "x" } })
        {
            var res = await Put(r, mia, "Forever-Aria", bad!);
            Assert.False((bool)res["ok"]!);
        }
        foreach (var key in new[] { "", "x", "no dash here", new string('a', 90) + "-b", "Realm-\u0001Name" })
        {
            r.Clock.Advance(TimeSpan.FromSeconds(9));
            r.Sock("Mia").Clear();
            await r.Send(mia, new { t = "armory.put", key, @char = Snap() });
            Assert.False((bool)r.Sock("Mia").Of("armory.saved").Last()["ok"]!);
        }
        r.Sock("Mia").Clear();
        await r.Send(mia, new { t = "armory.put", @char = Snap() });                  // no key at all
        Assert.False((bool)r.Sock("Mia").Of("armory.saved").Last()["ok"]!);
        r.Sock("Mia").Clear();
        await r.Send(mia, JsonNode.Parse("{\"t\":\"armory.put\",\"key\":7,\"char\":\"x\"}")!);
        Assert.False((bool)r.Sock("Mia").Of("armory.saved").Last()["ok"]!);
        Assert.Empty((JsonArray)(await List(r, mia))["chars"]!);
    }

    [Fact]
    public async Task Values_are_cleaned_and_bounded_and_an_item_link_must_be_a_real_link()
    {
        using var r = new FunRig();
        var mia = r.Join("Mia", "member"); var bob = r.Join("Bob", "officer");
        var snap = Snap(name: "Ar\u0001ia" + new string('x', 40), level: 9999);
        snap["guild"] = new string('g', 200);
        var gear = (Dictionary<string, object>)snap["gear"];
        gear["slots"] = new Dictionary<string, object>
        {
            ["1"] = new { i = 3000, q = 99, ic = -5, il = 99999999 }, ["99"] = new { i = 5, q = 1 }, ["x"] = new { i = 5 }, ["2"] = new { i = -4 }, ["3"] = "junk", ["4"] = new { i = 3002, q = 4 },
        };
        gear["links"] = new Dictionary<string, object>
        {
            ["3000"] = "|cffffffff|Hitem:3000:0|h[Evil|h|r]|h|r", ["3002"] = Link.Replace("3000", "3002"), ["9999"] = Link.Replace("3000", "9999"),     // 3000 is a bad link, 9999 is not worn
            ["x"] = Link,
        };
        var res = await Put(r, mia, "Forever-Aria", snap);
        Assert.True((bool)res["ok"]!);
        var ch = (await Get(r, bob, "mia", "Forever-Aria"))!["char"]!.AsObject();
        Assert.True(((string)ch["name"]!).Length <= 24);
        Assert.False(((string)ch["name"]!).Contains('\u0001'));
        Assert.Equal(100, (int)ch["level"]!);
        Assert.True(((string)ch["guild"]!).Length <= 40);
        var slots = (JsonObject)ch["gear"]!["slots"]!;
        Assert.Equal(new[] { "1", "4" }, slots.Select(kv => kv.Key).OrderBy(x => x).ToArray());
        Assert.Equal(7, (int)slots["1"]!["q"]!);
        Assert.Null(slots["1"]!["ic"]);
        Assert.Equal(10000, (int)slots["1"]!["il"]!);
        var links = (JsonObject)ch["gear"]!["links"]!;
        Assert.Equal(new[] { "3002" }, links.Select(kv => kv.Key).ToArray());
    }

    [Fact]
    public async Task A_snapshot_that_is_too_big_is_refused_and_lists_are_capped()
    {
        using var r = new FunRig();
        var mia = r.Join("Mia", "member");
        var snap = Snap();
        var pets = new List<object>();
        for (int i = 0; i < 40; i++)
        {
            var abilities = new List<object>();
            for (int a = 0; a < 40; a++) abilities.Add(new { id = "ability" + a, name = new string('N', 48), rank = 3 });
            pets.Add(new { key = "p" + i, name = new string('P', 48), family = new string('F', 48), level = 30, abilities });
        }
        ((Dictionary<string, object>)snap["pets"])["pets"] = pets;
        var res = await Put(r, mia, "Forever-Aria", snap);
        Assert.False((bool)res["ok"]!); Assert.Equal("size", (string)res["why"]!);

        // 300 shortish talents fit
        var small = Snap();
        var n2 = new List<object>();
        for (int i = 1; i <= 300; i++) n2.Add(new { id = i, r = 1, m = 1, n = "T" + i });
        ((Dictionary<string, object>)small["talents"])["nodes"] = n2;
        var ok = await Put(r, mia, "Forever-Aria", small);
        Assert.True((bool)ok["ok"]!);
        Assert.Equal(300, ((JsonArray)(await Get(r, mia, "mia", "Forever-Aria"))!["char"]!["talents"]!["nodes"]!).Count);

        var profs = new List<object>();
        for (int i = 0; i < 30; i++) profs.Add(new { name = "Skill " + i, skill = 1, max = 300 });
        var s3 = Snap(); ((Dictionary<string, object>)s3["professions"])["list"] = profs;
        await Put(r, mia, "Forever-Aria", s3);
        Assert.Equal(Armory.MaxProfessions, ((JsonArray)(await Get(r, mia, "mia", "Forever-Aria"))!["char"]!["professions"]!["list"]!).Count);
    }

    [Fact]
    public async Task A_member_can_share_at_most_thirty_characters_and_the_lodge_has_a_total_cap()
    {
        using var r = new FunRig();
        var mia = r.Join("Mia", "member"); var bob = r.Join("Bob", "officer");
        for (int i = 0; i < 30; i++) Assert.True((bool)(await Put(r, mia, $"Forever-C{i:00}", Snap($"C{i:00}")))["ok"]!, "character " + i);
        var res = await Put(r, mia, "Forever-C30", Snap("C30"));
        Assert.False((bool)res["ok"]!); Assert.Equal("full", (string)res["why"]!);
        // replacing one of the thirty still works, and taking one out makes room
        Assert.True((bool)(await Put(r, mia, "Forever-C05", Snap("C05", level: 55)))["ok"]!);
        await Delete(r, mia, new { key = "Forever-C00" });
        Assert.True((bool)(await Put(r, mia, "Forever-C30", Snap("C30")))["ok"]!);
        // the cap is per member
        Assert.True((bool)(await Put(r, bob, "Forever-B00", Snap("B00")))["ok"]!);
        Assert.Equal(31, ((JsonArray)(await List(r, bob))["chars"]!).Count);
    }

    [Fact]
    public async Task Puts_are_rate_limited_like_the_other_modules()
    {
        using var r = new FunRig();
        var mia = r.Join("Mia", "member");
        int ok = 0; string? why = null;
        for (int i = 0; i < 40; i++)
        {
            var res = await Put(r, mia, $"Forever-C{i:00}", Snap($"C{i:00}"), advance: false);
            if ((bool)res["ok"]!) ok++; else { why = (string)res["why"]!; break; }
        }
        Assert.Equal(Armory.PutBurst, ok);
        Assert.Equal("rate", why);
        r.Clock.Advance(TimeSpan.FromSeconds(9));
        Assert.True((bool)(await Put(r, mia, "Forever-C00", Snap("Zz"), advance: false))["ok"]!);
    }

    [Fact]
    public async Task Reading_is_rate_limited_too()
    {
        using var r = new FunRig();
        var mia = r.Join("Mia", "member");
        await Put(r, mia, "Forever-Aria", Snap());
        r.Sock("Mia").Clear();
        for (int i = 0; i < 30; i++) await r.Send(mia, new { t = "armory.list" });
        Assert.True(r.Sock("Mia").Of("armory.chars").Count is > 5 and < 30);
        Assert.Equal("rate", (string)r.Sock("Mia").LastError()!["code"]!);
    }

    // ---------------------------------------------------------------- storage, leaving

    [Fact]
    public async Task Characters_survive_a_restart_and_a_deletion_is_on_disk_at_once()
    {
        using var r = new FunRig();
        var mia = r.Join("Mia", "member");
        await Put(r, mia, "Forever-Aria", Snap());
        await Put(r, mia, "Forever-Bela", Snap("Bela"));
        r.Hub.Armory.Flush();
        var cfg2 = LodgeConfig.ForTests(r.Dir.Path);
        var again = new LodgeHub(cfg2, new Auth(cfg2, T.Log), new FileStore(cfg2), T.Log, r.Clock);
        Assert.Equal(2, again.Armory.Store.CharCount);
        Assert.Equal("Aria", (string)again.Armory.Store.Get("mia", "Forever-Aria")!["name"]!);

        await Delete(r, mia, new { key = "Forever-Aria" });                       // no Flush: the deletion must already be written
        var third = new LodgeHub(cfg2, new Auth(cfg2, T.Log), new FileStore(cfg2), T.Log, r.Clock);
        Assert.Equal(1, third.Armory.Store.CharCount);
        Assert.Null(third.Armory.Store.Get("mia", "Forever-Aria"));
    }

    [Fact]
    public async Task A_member_who_leaves_the_lodge_takes_their_characters_along()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner"); var bob = r.Join("Bob", "officer");
        await Put(r, elan, "Forever-Elana", Snap("Elana"));
        await Put(r, bob, "Forever-Bobo", Snap("Bobo"));
        Assert.Equal(2, r.Hub.Armory.Store.CharCount);
        var admin = new AdminModule(r.Hub);
        Assert.True(await admin.Handle(bob, "member.leave", JsonNode.Parse("{\"t\":\"member.leave\"}")!.AsObject()));
        Assert.Equal(1, r.Hub.Armory.Store.CharCount);
        Assert.Equal(0, r.Hub.Armory.Store.CountOf("bob"));
        Assert.Equal(1, r.Hub.Armory.Store.CountOf("elan"));
    }

    [Fact]
    public async Task A_removed_member_loses_theirs_and_the_sweep_catches_codes_removed_by_hand()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner"); var bob = r.Join("Bob", "officer");
        await Put(r, bob, "Forever-Bobo", Snap("Bobo"));
        await Put(r, elan, "Forever-Elana", Snap("Elana"));
        var admin = new AdminModule(r.Hub);
        await admin.Handle(elan, "admin.remove", JsonNode.Parse("{\"t\":\"admin.remove\",\"name\":\"Bob\"}")!.AsObject());
        Assert.Equal(0, r.Hub.Armory.Store.CountOf("bob"));
        Assert.Equal(1, r.Hub.Armory.Store.CountOf("elan"));

        // a person who was dropped from the codes file by hand is swept by the housekeeping
        await Put(r, elan, "Forever-Elanb", Snap("Elanb"));
        lock (r.Hub.Armory.Store.Gate) r.Hub.Armory.Store.Put("ghost", "Ghost", "Forever-Gone", Armory.Clean(JsonNode.Parse(JsonStr(Snap("Gone")))!.AsObject(), out _)!);
        Assert.Equal(1, r.Hub.Armory.Store.CountOf("ghost"));
        r.Hub.Armory.Tick();
        Assert.Equal(0, r.Hub.Armory.Store.CountOf("ghost"));
        Assert.Equal(2, r.Hub.Armory.Store.CountOf("elan"));
    }

    [Fact]
    public async Task An_unreadable_codes_file_never_wipes_the_armory()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner");
        await Put(r, elan, "Forever-Elana", Snap("Elana"));
        File.WriteAllText(r.Hub.Cfg.CodesFile, "");
        r.Hub.Armory.Tick();
        Assert.Equal(1, r.Hub.Armory.Store.CharCount);
    }

    static string JsonStr(object o) => System.Text.Json.JsonSerializer.Serialize(o);

    [Fact]
    public async Task Nothing_that_was_shared_reaches_the_logs()
    {
        var log = new CaptureLogger();
        using var r = new FunRig(log: log);
        var mia = r.Join("Mia", "member");
        var snap = Snap("Secretname");
        snap["guild"] = "SecretGuild";
        await Put(r, mia, "Forever-Secretname", snap);
        await Delete(r, mia, new { all = true });
        var all = string.Join("\n", log.Lines);
        Assert.DoesNotContain("Secretname", all);
        Assert.DoesNotContain("SecretGuild", all);
        Assert.DoesNotContain("Linen", all);
    }
}
