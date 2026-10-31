using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Xunit;

namespace Lodge.Tests;

// Lodge 2.36 (feature "fileskits"): kits - named sets of slots ("Raid night kit") made by officers, shown with only the slots a viewer sees
public class FilesKitsTests
{
    static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    static async Task<string> Up(FunRig r, Member who, string name)
    {
        var data = System.Text.Encoding.UTF8.GetBytes(name);
        var s = await r.Hub.SharedFiles.Upload(who.Name, who.Role, "p:" + who.Name.ToLowerInvariant(), null, name, name + ".txt", "", Sha(data), data.Length, new MemoryStream(data), CancellationToken.None);
        return (string)s["id"]!;
    }

    static async Task<JsonObject> Do(FunRig r, Member who, object msg)
    {
        int before = r.Sock(who.Name).Of("files.result").Count;
        await r.Send(who, msg);
        for (int i = 0; i < 100; i++) { var l = r.Sock(who.Name).Of("files.result"); if (l.Count > before) return l.Last(); await Task.Delay(20); }
        throw new Xunit.Sdk.XunitException("no files.result");
    }

    static JsonArray Kits(FunRig r, Member who) => r.Hub.SharedFiles.ListJson(who)["kits"] as JsonArray ?? new JsonArray();

    [Fact]
    public void The_feature_is_announced() { using var r = new FunRig(); Assert.Contains("fileskits", r.Hub.FeatureList()); }

    [Fact]
    public async Task Officers_make_kits_and_members_see_them_with_only_the_files_they_see()
    {
        using var r = new FunRig();
        var o = r.Join("Wing", "officer"); var m = r.Join("Mia", "member"); var g = r.Join("Gus", "guest");
        var dbm = await Up(r, o, "DBM"); var wa = await Up(r, o, "WeakAuras pack"); var plans = await Up(r, o, "Officer plans");
        await Do(r, o, new { t = "files.edit", slot = plans, see = "officer" });
        Assert.Equal("forbidden", (string)(await Do(r, m, new { t = "files.kit", name = "Mine", slots = dbm }))["why"]!);
        var res = await Do(r, o, new { t = "files.kit", name = "Raid night kit", desc = "Everything for raid night", slots = dbm + "," + wa + "," + plans + ",nonsense" });
        Assert.True((bool)res["ok"]!);
        var kid = (string)res["slot"]!;
        var mk = Kits(r, m).Single()!;
        Assert.Equal(("Raid night kit", "Everything for raid night", dbm + "," + wa), ((string)mk["name"]!, (string)mk["desc"]!, (string)mk["slots"]!));   // the officers' file isn't in Mia's kit
        Assert.Equal(dbm + "," + wa + "," + plans, (string)Kits(r, o).Single()!["slots"]!);
        Assert.Equal("name-taken", (string)(await Do(r, o, new { t = "files.kit", name = "raid NIGHT kit", slots = dbm }))["why"]!);
        // members only: not for guests
        Assert.True((bool)(await Do(r, o, new { t = "files.kit", id = kid, name = "Raid night kit", see = "member", slots = dbm + "," + wa }))["ok"]!);
        Assert.Empty(Kits(r, g));
        Assert.Single(Kits(r, m));
        Assert.True(r.Sock("Mia").Of("files.changed").Any(c => (string)c["action"]! == "kit"));
        // gone
        Assert.True((bool)(await Do(r, o, new { t = "files.kit.delete", id = kid }))["ok"]!);
        Assert.Empty(Kits(r, o));
    }

    [Fact]
    public async Task Kits_survive_a_restart_of_the_Lodge()
    {
        using var r = new FunRig();
        var o = r.Join("Wing", "officer");
        var dbm = await Up(r, o, "DBM");
        Assert.True((bool)(await Do(r, o, new { t = "files.kit", name = "Kit", slots = dbm }))["ok"]!);
        var reopened = new FilesModule(r.Hub);                       // what a restarted Lodge reads from the data folder
        Assert.Equal(dbm, (string)((JsonArray)reopened.ListJson(o)["kits"]!).Single()!["slots"]!);
    }
}
