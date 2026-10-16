using System.Text.Json.Nodes;
using Xunit;

namespace Lodge.Tests;

// A find's game build ("gb", Outpost 2.77): optional, validated, stored, sent back, kept across a restart; never part of the find's identity.
public partial class AtlasTests
{
    static Dictionary<string, object> FindB(int item, long at, object gb)
    {
        var f = Find(item, at);
        f["gb"] = gb;
        return f;
    }

    [Fact]
    public async Task A_find_keeps_its_game_build_and_one_without_stays_without()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member");
        var guest = r.Join("Gus", "guest");
        var t = Unix(r);
        var rep = await Share(r, amy, new { finds = new object[] { FindB(7, t - 30, 70374), Find(8, t - 20), FindB(9, t - 10, "x"), FindB(10, t - 5, 99_999_999) } });
        Assert.Equal(4, (int)rep["accepted"]!);           // a bad build only loses the build
        var fa = (JsonArray)(await GetAll(r, amy))["finds"]!;
        Assert.Equal(70374, (int)fa.First(f => (int)f!["i"]! == 7)!["gb"]!);
        Assert.Null(fa.First(f => (int)f!["i"]! == 8)!["gb"]);
        Assert.Null(fa.First(f => (int)f!["i"]! == 9)!["gb"]);
        Assert.Null(fa.First(f => (int)f!["i"]! == 10)!["gb"]);
        // a guest sees the build too (it says nothing about who found it)
        var ga = (JsonArray)(await GetAll(r, guest))["finds"]!;
        Assert.Equal(70374, (int)ga.First(f => (int)f!["i"]! == 7)!["gb"]!);

        // the same find again with a build is the same find (an older Outpost sent it without one)
        var again = await Share(r, amy, new { finds = new object[] { FindB(8, t - 20, 70374) } });
        Assert.Equal(1, (int)again["unchanged"]!);

        // kept across a restart
        r.Hub.AtlasPool.Flush();
        var store = new AtlasStore(r.Dir.Path, T.Log, r.Clock);
        JsonObject after;
        lock (store.Gate) after = store.Get(0, null, x => x);
        var fb = (JsonArray)after["finds"]!;
        Assert.Equal(70374, (int)fb.First(f => (int)f!["i"]! == 7)!["gb"]!);
        Assert.Null(fb.First(f => (int)f!["i"]! == 8)!["gb"]);
    }
}
