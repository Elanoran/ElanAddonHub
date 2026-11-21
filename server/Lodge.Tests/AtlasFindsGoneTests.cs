using System.Text.Json.Nodes;
using Xunit;

namespace Lodge.Tests;

// 2.39 (feature atlas10): the caps went up to 10000 finds / 5000 per member, and the finds the caps remove are told to the Outposts
// (atlas.data `findsGone`, the removed finds' revisions `s`), so an Outpost drops them too instead of showing finds the Lodge no longer has.
public partial class AtlasTests
{
    // one member shares n finds (items from `first`, a second apart), in shares of the per-share maximum
    static async Task ShareMany(FunRig r, Member m, int first, int n, long t0)
    {
        for (int done = 0; done < n; done += Atlas.MaxFindsPerShare)
        {
            int k = Math.Min(Atlas.MaxFindsPerShare, n - done);
            await Share(r, m, new { finds = Enumerable.Range(done, k).Select(i => Find(first + i, t0 + i)).ToArray() });
        }
    }

    // where an Outpost stands after downloading every page: (rev, epoch)
    static async Task<(long Rev, string Epoch)> Caught(FunRig r, Member m)
    {
        long since = 0; string? epoch = null;
        for (int guard = 0; guard < 50; guard++)
        {
            var d = await Get(r, m, since, epoch);
            since = (long)d["rev"]!; epoch = (string)d["epoch"]!;
            if (!d.ContainsKey("more")) break;
        }
        return (since, epoch!);
    }

    [Fact]
    public void The_features_announce_atlas10()
    {
        using var r = new FunRig();
        Assert.Contains("atlas10", r.Hub.FeatureList());
    }

    [Fact]
    public async Task Finds_the_member_cap_removes_are_told_to_an_Outpost_that_continues_and_only_once()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member");
        var t = Unix(r) - 200000;
        await ShareMany(r, amy, 1, Atlas.MaxFindsPerMember, t);
        var all = await AllFinds(r, amy);
        Assert.Equal(Atlas.MaxFindsPerMember, all.Count);
        var (rev0, ep) = await Caught(r, amy);            // where an Outpost stands after its download
        var oldest = all.OrderBy(f => (long)f!["s"]!).Take(300).Select(f => (long)f!["s"]!).ToHashSet();

        // 300 newer finds push the 300 oldest out
        await Share(r, amy, new { finds = Enumerable.Range(0, 300).Select(i => Find(100000 + i, t + 100000 + i)).ToArray() });
        var d1 = await Get(r, amy, rev0, ep);
        Assert.False(d1.ContainsKey("full"));
        Assert.Equal(300, ((JsonArray)d1["finds"]!).Count);
        var gone = ((JsonArray)d1["findsGone"]!).Select(x => (long)x!).ToHashSet();
        Assert.Equal(oldest, gone);                       // exactly the removed ones

        // asked again from there: nothing removed since, so no findsGone
        var d2 = await Get(r, amy, (long)d1["rev"]!, ep);
        Assert.Null(d2["findsGone"]);
        // a full download never carries findsGone (the Outpost replaces everything)
        Assert.Null((await Get(r, amy))["findsGone"]);
    }

    [Fact]
    public async Task Finds_the_lodge_cap_removes_are_told_too()
    {
        using var r = new FunRig();
        var t = Unix(r) - 300000;
        var ms = Enumerable.Range(0, 3).Select(i => r.Join("M" + i, "member")).ToList();
        await ShareMany(r, ms[0], 1, Atlas.MaxFindsPerMember, t);
        await ShareMany(r, ms[1], 20000, Atlas.MaxFindsPerMember, t + 10000);
        var (rev0, ep) = await Caught(r, ms[0]);
        await ShareMany(r, ms[2], 40000, 1000, t + 20000);  // 11000 > 10000: the 1000 oldest of the lodge go
        var gone = new HashSet<long>();
        long since = rev0;
        for (int guard = 0; guard < 50; guard++)
        {
            var d = await Get(r, ms[0], since, ep);
            Assert.False(d.ContainsKey("full"));
            if (d["findsGone"] is JsonArray g) foreach (var x in g) gone.Add((long)x!);
            since = (long)d["rev"]!;
            if (!d.ContainsKey("more")) break;
        }
        Assert.Equal(1000, gone.Count);
        Assert.Equal(Atlas.MaxFinds, (await AllFinds(r, ms[0])).Count);
    }

    [Fact]
    public async Task Removed_finds_are_remembered_across_a_restart()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member");
        var t = Unix(r) - 200000;
        await ShareMany(r, amy, 1, Atlas.MaxFindsPerMember, t);
        var (rev0, ep) = await Caught(r, amy);
        await Share(r, amy, new { finds = Enumerable.Range(0, 10).Select(i => Find(100000 + i, t + 100000 + i)).ToArray() });
        r.Hub.AtlasPool.Flush();
        var store = new AtlasStore(r.Dir.Path, T.Log, r.Clock);
        Assert.Equal(ep, store.Epoch);                    // a 2.39 file keeps its epoch: no new download for everybody
        Assert.Equal(10, store.FindsGoneCount);
        JsonObject after;
        lock (store.Gate) after = store.Get(rev0, ep, x => x);
        Assert.False(after.ContainsKey("full"));
        Assert.Equal(10, ((JsonArray)after["findsGone"]!).Count);
    }

    [Fact]
    public void A_file_from_before_2_39_gets_a_new_epoch_once_so_every_Outpost_fetches_the_pool_again()
    {
        using var dir = new TempDir();
        var clock = new FakeClock { UtcNow = new DateTime(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc) };
        long at = new DateTimeOffset(clock.UtcNow).ToUnixTimeSeconds() - 1000;
        File.WriteAllText(Path.Combine(dir.Path, "atlas.json"), new JsonObject
        {
            ["v"] = 1, ["epoch"] = "old123", ["rev"] = 5,
            ["finds"] = new JsonArray(new JsonObject { ["i"] = 7, ["c"] = 1, ["src"] = "npc:3", ["at"] = at, ["b"] = "amy", ["w"] = "Amy", ["r"] = 5 }),
        }.ToJsonString());
        var s1 = new AtlasStore(dir.Path, T.Log, clock);
        Assert.NotEqual("old123", s1.Epoch);
        Assert.Equal(1, s1.FindCount);                    // nothing is lost, only fetched again
        lock (s1.Gate) s1.SaveIfDirty(TimeSpan.Zero);
        var s2 = new AtlasStore(dir.Path, T.Log, clock);
        Assert.Equal(s1.Epoch, s2.Epoch);                 // once: the saved file now has findsGone
    }

    [Fact]
    public void An_Outpost_behind_what_the_store_still_remembers_gets_a_full_answer()
    {
        using var dir = new TempDir();
        var clock = new FakeClock { UtcNow = new DateTime(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc) };
        File.WriteAllText(Path.Combine(dir.Path, "atlas.json"), new JsonObject
        {
            ["v"] = 1, ["epoch"] = "ep2390", ["rev"] = 1000, ["findsGone"] = new JsonArray(new JsonArray(900, 40)), ["goneFloor"] = 500,
        }.ToJsonString());
        var s = new AtlasStore(dir.Path, T.Log, clock);
        Assert.Equal("ep2390", s.Epoch);
        JsonObject behind, after;
        lock (s.Gate) { behind = s.Get(100, "ep2390", x => x); after = s.Get(600, "ep2390", x => x); }
        Assert.True((bool)behind["full"]!);               // removals before 500 were forgotten: start over
        Assert.False(after.ContainsKey("full"));
        Assert.Equal(new long[] { 40 }, ((JsonArray)after["findsGone"]!).Select(x => (long)x!).ToArray());
    }
}
