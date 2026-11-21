using System.IO.Compression;
using System.Text.Json.Nodes;
using Xunit;

namespace Lodge.Tests;

// 2.40: nothing the members found is lost. The finds the caps remove go to data/atlas-archive (never to the Outposts), the member forget
// cleans it, finds keep their new optional fields (instance, suffix, loot context), and each source's growth is noted per day.
public partial class AtlasTests
{
    static string ArchiveDir(FunRig r) => Path.Combine(r.Dir.Path, "atlas-archive");

    static List<JsonObject> Archived(FunRig r)
    {
        var s = r.Hub.AtlasPool.Store;
        lock (s.Gate) return s.ArchivedFinds().ToList();
    }

    static Dictionary<string, object> Items1(string item, long n) => new() { [item] = new { n, c = n } };

    [Fact]
    public async Task Finds_the_member_cap_removes_go_to_the_archive_with_their_fields()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member");
        var t = Unix(r) - 200000;
        var first = Find(1, t); first["ii"] = 36; first["sx"] = -6; first["cx"] = new Atlas.FindCtx(2, 1, 12, 1).Pack();
        await Share(r, amy, new { finds = new[] { first } });
        await ShareMany(r, amy, 2, Atlas.MaxFindsPerMember, t + 1);           // 5001 finds: the first one is pushed out
        r.Hub.AtlasPool.Flush();
        var a = Assert.Single(Archived(r));
        Assert.Equal(1, (int)a["i"]!);
        Assert.Equal("amy", (string)a["b"]!);
        Assert.Equal(36, (int)a["ii"]!); Assert.Equal(-6, (int)a["sx"]!);
        var cx = Atlas.FindCtx.Of((int)a["cx"]!);
        Assert.Equal((2, 1, 12, 1), (cx.Group, cx.Faction, cx.LevelBucket, cx.Difficulty));
        Assert.True(Directory.GetFiles(ArchiveDir(r), "finds-*.jsonl.gz").Length >= 1);
        // the archived finds are not in what the Outposts get
        Assert.DoesNotContain(await AllFinds(r, amy), f => (int)f!["i"]! == 1);
    }

    [Fact]
    public async Task New_find_fields_survive_a_restart_and_bad_ones_only_lose_the_field()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member");
        var t = Unix(r);
        var good = Find(5, t - 10); good["ii"] = 289; good["cx"] = 2048 * 2 + 3;
        var bad = Find(6, t - 9); bad["ii"] = "x"; bad["sx"] = 0; bad["cx"] = -1;
        var rep = await Share(r, amy, new { finds = new[] { good, bad } });
        Assert.Equal(2, (int)rep["accepted"]!);
        r.Hub.AtlasPool.Flush();
        var root = JsonNode.Parse(File.ReadAllText(Path.Combine(r.Dir.Path, "atlas.json")))!.AsObject();
        var fs = ((JsonArray)root["finds"]!).Select(f => f!.AsObject()).ToList();
        var g = fs.Single(f => (int)f["i"]! == 5);
        Assert.Equal(289, (int)g["ii"]!); Assert.Equal(2048 * 2 + 3, (int)g["cx"]!);
        var b = fs.Single(f => (int)f["i"]! == 6);
        Assert.Null(b["ii"]); Assert.Null(b["sx"]); Assert.Null(b["cx"]);
        // the Outposts don't get them yet (2.40 keeps them for the item records)
        var sent = (await AllFinds(r, amy)).Single(f => (int)f!["i"]! == 5)!;
        Assert.Null(sent["ii"]); Assert.Null(sent["cx"]);
        // a restart keeps them
        var again = new AtlasStore(r.Dir.Path, T.Log, r.Clock);
        lock (again.Gate) again.Save();
        var root2 = JsonNode.Parse(File.ReadAllText(Path.Combine(r.Dir.Path, "atlas.json")))!.AsObject();
        Assert.Equal(289, (int)((JsonArray)root2["finds"]!).Single(f => (int)f!["i"]! == 5)!["ii"]!);
    }

    [Fact]
    public async Task The_member_forget_removes_their_archived_finds_and_keeps_the_others()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member"); var bob = r.Join("Bob", "member");
        var t = Unix(r) - 300000;
        await ShareMany(r, amy, 1, Atlas.MaxFindsPerMember + 3, t);
        await ShareMany(r, bob, 50000, Atlas.MaxFindsPerMember + 2, t);
        r.Hub.AtlasPool.Flush();
        Assert.Equal(5, Archived(r).Count);
        var res = r.Hub.AtlasPool.Purge("Amy");
        Assert.Equal(3, res.Archived);
        var left = Archived(r);
        Assert.Equal(2, left.Count);
        Assert.All(left, x => Assert.Equal("bob", (string)x["b"]!));
    }

    [Fact]
    public async Task The_archive_can_be_switched_off_and_is_capped_oldest_month_first()
    {
        using (var r = new FunRig())
        {
            r.Hub.AtlasPool.Store.ArchiveEnabled = false;
            var amy = r.Join("Amy", "member");
            await ShareMany(r, amy, 1, Atlas.MaxFindsPerMember + 2, Unix(r) - 200000);
            r.Hub.AtlasPool.Flush();
            Assert.Empty(Archived(r));
            Assert.False(Directory.Exists(ArchiveDir(r)));
        }
        using (var r = new FunRig())
        {
            var store = r.Hub.AtlasPool.Store;
            var amy = r.Join("Amy", "member");
            // two months of finds, the older month pushed out first
            var t = Unix(r) - 70L * 86400;
            await ShareMany(r, amy, 1, Atlas.MaxFindsPerMember + 400, t);
            r.Hub.AtlasPool.Flush();
            var before = Directory.GetFiles(ArchiveDir(r), "finds-*.jsonl.gz").OrderBy(x => x).ToList();
            Assert.Single(before);
            // a find from a newer month is archived later; a cap below the total keeps only the newest month
            store.ArchiveCapBytes = 1;
            await Share(r, amy, new { finds = Enumerable.Range(0, 300).Select(i => Find(900000 + i, Unix(r) - 1000 + i)).ToArray() });
            r.Hub.AtlasPool.Flush();
            var after = Directory.GetFiles(ArchiveDir(r), "finds-*.jsonl.gz");
            Assert.Single(after);
        }
    }

    [Fact]
    public void An_archive_of_several_appends_reads_back_whole()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, "x.gz");
        foreach (var part in new[] { "{\"a\":1}\n", "{\"a\":2}\n{\"a\":3}\n" })
        {
            using var fs = new FileStream(file, FileMode.Append);
            using var gz = new GZipStream(fs, CompressionLevel.SmallestSize);
            gz.Write(System.Text.Encoding.UTF8.GetBytes(part));
        }
        using var rd = new StreamReader(new GZipStream(File.OpenRead(file), CompressionMode.Decompress));
        Assert.Equal("{\"a\":1}\n{\"a\":2}\n{\"a\":3}\n", rd.ReadToEnd());
    }

    [Fact]
    public async Task Each_sources_growth_is_noted_on_the_day_of_the_share()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member");
        var store = r.Hub.AtlasPool.Store;
        await ShareKills(r, amy, "100", Ctr(3, Items1("7", 1)));                // first share of a source, small: counted
        int d1 = (int)(Unix(r) / 86400);
        await ShareKills(r, amy, "100", Ctr(5, Items1("7", 1)));                // two more kills, no new drop, same day
        r.Clock.Advance(TimeSpan.FromDays(1));
        await ShareKills(r, amy, "100", Ctr(9, Items1("7", 3)));                // next day: four kills, two drops
        int d2 = (int)(Unix(r) / 86400);
        var t1 = store.DayOfSource("amy", AtlasStore.Kills, 100, d1)!;
        Assert.Equal(5, t1.N); Assert.Equal(1, t1.I[7]);
        var t2 = store.DayOfSource("amy", AtlasStore.Kills, 100, d2)!;
        Assert.Equal(4, t2.N); Assert.Equal(2, t2.I[7]);
        // a resend changes nothing
        await ShareKills(r, amy, "100", Ctr(9, Items1("7", 3)));
        Assert.Equal(4, store.DayOfSource("amy", AtlasStore.Kills, 100, d2)!.N);

        // a source first shared with a backlog is not put on today
        await ShareKills(r, amy, "200", Ctr(400, Items1("8", 20)));
        Assert.Null(store.DayOfSource("amy", AtlasStore.Kills, 200, d2));
        // but its growth from then on is
        await ShareKills(r, amy, "200", Ctr(402, Items1("8", 20)));
        Assert.Equal(2, store.DayOfSource("amy", AtlasStore.Kills, 200, d2)!.N);

        // it survives a restart and goes with the member
        r.Hub.AtlasPool.Flush();
        var again = new AtlasStore(r.Dir.Path, T.Log, r.Clock);
        Assert.Equal(4, again.DayOfSource("amy", AtlasStore.Kills, 100, d2)!.N);
        Assert.Equal(1, again.DayOfSource("amy", AtlasStore.Kills, 100, d1)!.I[7]);
        r.Hub.AtlasPool.Purge("Amy");
        Assert.Equal(0, store.DayEntryCount);
    }

    [Fact]
    public async Task Only_the_newest_days_of_a_source_are_kept()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member");
        var store = r.Hub.AtlasPool.Store;
        int firstDay = 0;
        for (int i = 1; i <= AtlasStore.MaxDaysPerSource + 5; i++)
        {
            await ShareKills(r, amy, "100", Ctr(i));
            if (i == 1) firstDay = (int)(Unix(r) / 86400);
            r.Clock.Advance(TimeSpan.FromDays(1));
        }
        Assert.Equal(AtlasStore.MaxDaysPerSource, store.DayEntryCount);
        Assert.Null(store.DayOfSource("amy", AtlasStore.Kills, 100, firstDay));
    }
}
