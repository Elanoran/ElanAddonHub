using System.Text.Json.Nodes;
using Xunit;

namespace Lodge.Tests;

// Lodge 2.22: purging a member (leave / remove / owner "revert"), the Atlas trust fixes (time clamp, authorship, write cap, guest view) and the chat upload block
public class PurgeTests
{
    static long Unix(FunRig r) => AtlasTests.Unix(r);

    static object Everything(long t) => new
    {
        kills = new Dictionary<string, object> { ["100"] = AtlasTests.Ctr(20) },
        vendors = new Dictionary<string, object> { ["300"] = new { at = t - 10, z = 1429, x = 0.5, y = 0.5, items = new Dictionary<string, object> { ["7"] = new { p = 5, s = 1, l = -1 } } } },
        quests = new Dictionary<string, object> { ["900"] = new { at = t - 10, rewards = new[] { 1 }, choices = Array.Empty<int>() } },
        spawns = new Dictionary<string, object> { ["100"] = new[] { new { z = 1429, x = 0.5, y = 0.5 } } },
        items = new Dictionary<string, object> { ["2589"] = new Dictionary<string, object> { ["n"] = "Linen Cloth", ["q"] = 1 } },
        seenNpcs = new Dictionary<string, object> { ["100"] = new { name = "Wolf" } },
        seenObjects = new Dictionary<string, object> { ["5"] = new { name = "Chest" } },
        seenQuests = new Dictionary<string, object> { ["7"] = new { title = "A Quest" } },
        finds = new[] { new Dictionary<string, object> { ["i"] = 7, ["c"] = 1, ["src"] = "npc:100", ["z"] = 1429, ["x"] = 0.5, ["y"] = 0.5, ["at"] = t - 10 } },
        nodes = new Dictionary<string, object> { ["1731"] = new Dictionary<string, object> { ["1411"] = new[] { new Dictionary<string, object> { ["x"] = 0.4, ["y"] = 0.5, ["n"] = 3 } } } },
        fishspots = new Dictionary<string, object> { ["1456"] = new[] { new Dictionary<string, object> { ["x"] = 0.4, ["y"] = 0.5, ["n"] = 5 } } },
    };

    static int Rows(JsonObject d) =>
        ((JsonObject)d["kills"]!).Count + ((JsonObject)d["vendors"]!).Count + ((JsonObject)d["quests"]!).Count + ((JsonObject)d["spawns"]!).Count + ((JsonObject)d["items"]!).Count
        + ((JsonObject)d["seenNpcs"]!).Count + ((JsonObject)d["seenObjects"]!).Count + ((JsonObject)d["seenQuests"]!).Count + ((JsonArray)d["finds"]!).Count
        + ((JsonObject)d["nodes"]!).Count + ((JsonObject)d["fishspots"]!).Count;

    // ---------------------------------------------------------------- atlas purge / revert

    [Fact]
    public async Task Purging_a_member_removes_everything_they_authored_in_the_atlas_and_rebuilds_the_totals()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member"); var bob = r.Join("Bob", "member");
        var t = Unix(r);
        var ok = await AtlasTests.Share(r, amy, Everything(t));
        Assert.True((bool)ok["ok"]!);
        await AtlasTests.Share(r, bob, new { kills = new Dictionary<string, object> { ["100"] = AtlasTests.Ctr(5) } });
        var before = await AtlasTests.GetAll(r, bob);
        Assert.Equal(25, AtlasTests.N(AtlasTests.Kill(before, "100")));
        Assert.True(Rows(before) >= 11);

        var res = r.Hub.AtlasPool.Purge("Amy");
        Assert.True(res.Total >= 11);
        Assert.Equal(1, res.Vendors); Assert.Equal(1, res.Quests); Assert.Equal(1, res.Revealed); Assert.Equal(3, res.Seen); Assert.Equal(1, res.Spawns); Assert.Equal(1, res.Finds);

        var after = await AtlasTests.Get(r, bob, (long)before["rev"]!, (string)before["epoch"]!);
        Assert.NotEqual((string)before["epoch"]!, (string)after["epoch"]!);           // the epoch changes: clients fetch everything again
        Assert.True(after.ContainsKey("full"));
        Assert.Equal(5, AtlasTests.N(AtlasTests.Kill(after, "100")));                 // Bob's own count is what is left
        Assert.Equal(1, Rows(after));
        Assert.DoesNotContain("Amy", after.ToJsonString());
        Assert.Equal(0, r.Hub.AtlasPool.Store.Contributions().Count(kv => kv.Key == "amy"));
        // the first-writer rows are free again: Bob's version goes in on his next share
        var again = await AtlasTests.Share(r, bob, new { items = new Dictionary<string, object> { ["2589"] = new Dictionary<string, object> { ["n"] = "Bob Cloth", ["q"] = 2 } } });
        Assert.Equal(1, (int)again["accepted"]!);
        Assert.Equal("Bob Cloth", (string)(await AtlasTests.GetAll(r, bob))["items"]!["2589"]!["n"]!);
    }

    [Fact]
    public async Task The_author_of_quest_and_seen_rows_survives_a_restart_so_a_revert_still_works()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member");
        await AtlasTests.Share(r, amy, Everything(Unix(r)));
        r.Hub.AtlasPool.Flush();
        var cfg = LodgeConfig.ForTests(r.Dir.Path);
        var store = new AtlasStore(cfg.DataDir, T.Log, r.Clock);
        lock (store.Gate)
        {
            var res = store.Purge("amy");
            Assert.Equal(1, res.Quests); Assert.Equal(3, res.Seen); Assert.Equal(1, res.Revealed); Assert.Equal(1, res.Vendors);
            Assert.Equal(0, store.SeenNpcCount + store.SeenObjectCount + store.SeenQuestCount);
        }
    }

    [Fact]
    public async Task Only_the_owner_can_revert_a_member_and_the_member_stays()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner"); var bob = r.Join("Bob", "officer");
        await AtlasTests.Share(r, bob, Everything(Unix(r)));
        var admin = new AdminModule(r.Hub);
        r.Sock("Bob").Clear();
        Assert.True(await admin.Handle(bob, "admin.purge", new JsonObject { ["t"] = "admin.purge", ["name"] = "Bob" }));
        Assert.NotNull(r.Sock("Bob").LastError());                                     // an officer is refused
        Assert.True(r.Hub.AtlasPool.Store.FindCount > 0);

        r.Sock("Elan").Clear();
        await admin.Handle(elan, "admin.purge", new JsonObject { ["t"] = "admin.purge", ["name"] = "Bob" });
        var done = r.Sock("Elan").Of("admin.purged").Single();
        Assert.True((bool)done["atlasOnly"]!);
        Assert.True((int)done["atlas"]! >= 11);
        Assert.Equal(0, r.Hub.AtlasPool.Store.FindCount);
        Assert.Contains(r.Hub.Auth.ListPersonal(), p => Names.Same(p.Name, "Bob"));    // still a member

        // a full purge of someone who is still a member is refused; of someone who is gone it runs
        r.Sock("Elan").Clear();
        await admin.Handle(elan, "admin.purge", new JsonObject { ["t"] = "admin.purge", ["name"] = "Bob", ["atlasOnly"] = false });
        Assert.NotNull(r.Sock("Elan").LastError());
        r.Sock("Elan").Clear();
        await admin.Handle(elan, "admin.purge", new JsonObject { ["t"] = "admin.purge", ["name"] = "Nobody", ["atlasOnly"] = false });
        Assert.Single(r.Sock("Elan").Of("admin.purged"));
    }

    // ---------------------------------------------------------------- leave / remove fan-out

    static async Task<JsonObject> Cal(FunRig r, Member m, string title)
    {
        r.Clock.Advance(TimeSpan.FromSeconds(3));
        r.Sock(m.Name).Clear();
        await r.Send(m, new { t = "cal:create", title, start = 1_792_260_000_000L, slots = new { tank = 2, healer = 3, dps = 5 }, tz = "Europe/Berlin", off = 120, roles = true, durationMin = 180, post = false });
        return r.Sock(m.Name).Of("cal:events").Last();
    }

    [Fact]
    public async Task Leaving_forgets_every_per_member_store()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner"); var bob = r.Join("Bob", "officer");
        var t = Unix(r);
        await AtlasTests.Share(r, bob, Everything(t));
        // flights
        r.Clock.Advance(TimeSpan.FromMinutes(11));
        await r.Send(bob, new { t = "flights.share", legs = new Dictionary<string, object> { ["Horde|11>12"] = new { t = 60, n = 3 } } });
        await r.Send(elan, new { t = "flights.share", legs = new Dictionary<string, object> { ["Horde|11>13"] = new { t = 70, n = 3 } } });
        Assert.Equal(1, r.Hub.Flight.Store.CountOf("bob"));
        // calendar: Bob organises one event and signs up for Elan's
        var evFrame = await Cal(r, bob, "Bob raid");
        var bobEv = (string)((JsonArray)evFrame["events"]!)[0]!["id"]!;
        var elanFrame = await Cal(r, elan, "Elan raid");
        var elanEv = (string)((JsonArray)elanFrame["events"]!)[0]!["id"]!;
        await r.Send(bob, new { t = "cal:signup", id = elanEv, status = "going", role = "dps" });
        await r.Send(elan, new { t = "cal:signup", id = bobEv, status = "going", role = "tank" });
        // chat: a message, a reaction on Elan's message, a pin
        var bobMsg = await r.Say(bob, "hello from Bob");
        var elanMsg = await r.Say(elan, "hello from Elan");
        await r.Do(bob, new { t = "react", id = elanMsg, emoji = "ready" });
        await r.Do(elan, new { t = "pin", id = bobMsg });
        // profile and a chat file
        lock (r.Hub.Profiles.Gate) r.Hub.Profiles.For("Bob").About = "I like pie";
        r.Hub.Profiles.Save(r.Hub.Profiles.For("Bob"));
        var meta = await r.Hub.Files.Save(new T.SlowStream(10), 10, "map.png", "Bob", "p:bob", CancellationToken.None);

        var admin = new AdminModule(r.Hub);
        await admin.Handle(bob, "member.leave", new JsonObject { ["t"] = "member.leave" });

        Assert.Equal(0, r.Hub.AtlasPool.Store.FindCount);
        Assert.Equal(0, r.Hub.Flight.Store.CountOf("bob"));
        Assert.Equal(1, r.Hub.Flight.Store.CountOf("elan"));
        lock (r.Hub.Calendar.Store.Gate)
        {
            var mine = r.Hub.Calendar.Store.Find(bobEv)!;
            Assert.Equal(LodgeHub.FormerMember, (string)mine["by"]!);                  // the event stays, the organiser is anonymised
            var theirs = r.Hub.Calendar.Store.Find(elanEv)!;
            Assert.DoesNotContain("Bob", theirs.ToJsonString());                       // the sign-up is gone
            Assert.Equal("Elan", (string)theirs["by"]!);
            Assert.Contains("Elan", mine.ToJsonString());                              // others' sign-ups stay
        }
        Assert.Null(r.Hub.Profiles.Peek("Bob"));
        var hist = r.Hub.Chat.HistoryFor("general");
        var first = hist.Select(x => x!.AsObject()).Single(x => (string)x["id"]! == bobMsg);
        Assert.Equal(LodgeHub.FormerMember, (string)first["from"]!);                   // the text stays, the author does not
        Assert.Equal("hello from Bob", (string)first["text"]!);
        var second = hist.Select(x => x!.AsObject()).Single(x => (string)x["id"]! == elanMsg);
        Assert.DoesNotContain("Bob", second.ToJsonString());                           // the reaction is gone
        Assert.DoesNotContain("\"Bob\"", r.Hub.Chat.Pins.For("general").ToJsonString());
        Assert.Equal(LodgeHub.FormerMember, r.Hub.Files.Get(meta.Id)!.By);             // the file stays, the uploader does not
        // and it is on disk
        r.Hub.Chat.FlushAll();
        var raw = File.ReadAllText(Path.Combine(r.Dir.Path, "history", "general.jsonl"));
        Assert.DoesNotContain("\"Bob\"", raw);
    }

    [Fact]
    public async Task A_console_request_runs_from_the_purge_folder_and_ignores_garbage()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member");
        await AtlasTests.Share(r, amy, Everything(Unix(r)));
        var dir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(r.Hub.Cfg.CodesFile))!, "purge.d");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a.req"), "atlas\tAmy");
        File.WriteAllText(Path.Combine(dir, "b.req"), "all\t../../etc/passwd");
        File.WriteAllText(Path.Combine(dir, "c.req"), new string('x', 2000));
        Assert.Equal(1, await r.Hub.ProcessPurgeRequests());
        Assert.Empty(Directory.GetFiles(dir, "*.req"));                                // every request is consumed, run or not
        Assert.Equal(0, r.Hub.AtlasPool.Store.FindCount);
        Assert.Equal(0, await r.Hub.ProcessPurgeRequests());
    }

    // ---------------------------------------------------------------- pool poisoning

    [Fact]
    public async Task A_time_more_than_five_minutes_ahead_is_refused_everywhere()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member");
        var t = Unix(r);
        var rep = await AtlasTests.Share(r, amy, new
        {
            vendors = new Dictionary<string, object>
            {
                ["1"] = new { at = t + 4 * 60, z = 1429, x = 0.5, y = 0.5, items = new Dictionary<string, object>() },     // clock skew: fine
                ["2"] = new { at = t + 6 * 60, z = 1429, x = 0.5, y = 0.5, items = new Dictionary<string, object>() },     // pins the vendor: refused
                ["3"] = new { at = t + 23 * 3600, z = 1429, x = 0.5, y = 0.5, items = new Dictionary<string, object>() },
            },
            quests = new Dictionary<string, object> { ["1"] = new { at = t + 6 * 60 }, ["2"] = new { at = t + 60 } },
            finds = new[]
            {
                new Dictionary<string, object> { ["i"] = 7, ["c"] = 1, ["src"] = "npc:1", ["at"] = t + 10 * 60 },
                new Dictionary<string, object> { ["i"] = 8, ["c"] = 1, ["src"] = "npc:1", ["at"] = t + 2 * 60 },
            },
        }, advance: false);
        Assert.Equal(3, (int)rep["accepted"]!);
        Assert.Equal(4, (int)rep["rejected"]!);
        Assert.Equal(300, Atlas.FutureSlackSeconds);
    }

    // ---------------------------------------------------------------- write cap

    [Fact]
    public async Task A_full_atlas_refuses_new_contributions_until_room_is_made()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member"); var bob = r.Join("Bob", "member");
        var t = Unix(r);
        var store = r.Hub.AtlasPool.Store;
        Assert.True((bool)(await AtlasTests.Share(r, amy, Everything(t)))["ok"]!);
        r.Hub.AtlasPool.Flush();
        Assert.True(store.StoredBytes > 100);
        store.WriteCapBytes = store.StoredBytes;                                       // "64 MB reached"
        var rep = await AtlasTests.Share(r, bob, new { kills = new Dictionary<string, object> { ["555"] = AtlasTests.Ctr(3) } });
        Assert.False((bool)rep["ok"]!);
        Assert.Equal("full", (string)rep["why"]!);
        Assert.Null((await AtlasTests.GetAll(r, bob))["kills"]!["555"]);
        // reverting a member makes room (the file shrinks on the save the revert triggers)
        r.Hub.AtlasPool.Purge("Amy");
        Assert.True(store.StoredBytes < store.WriteCapBytes);
        var ok = await AtlasTests.Share(r, bob, new { kills = new Dictionary<string, object> { ["555"] = AtlasTests.Ctr(3) } });
        Assert.True((bool)ok["ok"]!);
    }

    [Fact]
    public void The_default_cap_leaves_headroom_under_the_load_limit()
    {
        using var d = new TempDir();
        var s = new AtlasStore(d.Path, T.Log, new FakeClock());
        Assert.True(s.WriteCapBytes < 64L * 1024 * 1024);
        Assert.True(s.WriteCapBytes > 60L * 1024 * 1024);
    }

    // ---------------------------------------------------------------- guests

    [Fact]
    public async Task Guests_see_finds_by_day_and_as_a_member()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member"); var guest = r.Join("Visitor", "guest");
        var t = Unix(r);
        await AtlasTests.Share(r, amy, new { finds = new[] { new Dictionary<string, object> { ["i"] = 7, ["c"] = 1, ["src"] = "npc:100", ["at"] = t - 3600 } } });
        var forMember = ((JsonArray)(await AtlasTests.GetAll(r, amy))["finds"]!)[0]!;
        Assert.Equal("Amy", (string)forMember["by"]!);
        Assert.Equal(t - 3600, (long)forMember["at"]!);
        var forGuest = ((JsonArray)(await AtlasTests.GetAll(r, guest))["finds"]!)[0]!;
        Assert.Equal("a member", (string)forGuest["by"]!);
        Assert.Equal((t - 3600) - (t - 3600) % 86400, (long)forGuest["at"]!);
        Assert.DoesNotContain("Amy", forGuest.ToJsonString(), StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- chat upload block

    [Theory]
    [InlineData("patch.exe")]
    [InlineData("PATCH.EXE")]
    [InlineData("setup.exe. ")]
    [InlineData("run.bat")]
    [InlineData("a.ps1")]
    [InlineData("link.lnk")]
    [InlineData("tool.jar")]
    public async Task Chat_uploads_refuse_programs_and_scripts(string name)
    {
        using var d = new TempDir();
        var s = new FileStore(LodgeConfig.ForTests(d.Path), new FakeClock());
        var e = await Assert.ThrowsAsync<UploadRefusedException>(() => s.Save(new T.SlowStream(10), 10, name, "Elan", "p:elan", CancellationToken.None));
        Assert.Equal(415, e.Status);
        Assert.Equal(0, s.FileCount);
        Assert.Equal(0, s.ReservedBytes);
    }

    [Theory]
    [InlineData("screenshot.png")]
    [InlineData("notes.txt")]
    [InlineData("pack.zip")]
    [InlineData("exe")]
    public async Task Chat_uploads_still_take_ordinary_files(string name)
    {
        using var d = new TempDir();
        var s = new FileStore(LodgeConfig.ForTests(d.Path), new FakeClock());
        var meta = await s.Save(new T.SlowStream(10), 10, name, "Elan", "p:elan", CancellationToken.None);
        Assert.Equal(1, s.FileCount);
        Assert.NotNull(s.Get(meta.Id));
    }
}
