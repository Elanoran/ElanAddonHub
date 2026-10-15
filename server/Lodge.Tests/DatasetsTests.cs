using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Lodge.Tests;

// Lodge 2.17: hosted datasets (the Classic baseline bundle). Every file here is synthetic - no real Classic data.
public class DatasetsTests
{
    static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    static byte[] Gz(string text)
    {
        using var ms = new MemoryStream();
        using (var g = new GZipStream(ms, CompressionLevel.Optimal, true)) g.Write(Encoding.UTF8.GetBytes(text));
        return ms.ToArray();
    }

    static string FakeSql()
    {
        var sb = new StringBuilder();
        var rnd = new Random(7);
        for (int i = 0; i < 400; i++) sb.Append("INSERT INTO fake VALUES (").Append(i).Append(",'row ").Append(rnd.Next()).Append("');\n");
        return sb.ToString();
    }

    // a bundle shaped like the real one, with invented content; tweak() may change the entry list
    static byte[] Bundle(Action<List<(string name, byte[] data)>>? tweak = null)
    {
        var entries = new List<(string, byte[])>
        {
            ("ClassicDB_9_9_9_z0000.sql.gz", Gz(FakeSql())),
            ("LICENSE.md", Encoding.UTF8.GetBytes("synthetic licence text")),
            ("COPYRIGHT.md", Encoding.UTF8.GetBytes("synthetic copyright text")),
            ("AUTHORS", Encoding.UTF8.GetBytes("synthetic authors")),
            ("source.json", Encoding.UTF8.GetBytes("{\"repo\":\"https://example.invalid/classic-db\",\"dump\":\"ClassicDB_9_9_9_z0000.sql.gz\"}")),
            ("updates/z1_one.sql", Encoding.UTF8.GetBytes("UPDATE fake SET a=1;")),
        };
        tweak?.Invoke(entries);
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var (name, data) in entries)
            {
                var e = z.CreateEntry(name, CompressionLevel.NoCompression);
                using var s = e.Open();
                s.Write(data);
            }
        return ms.ToArray();
    }

    static Task<JsonObject> Up(FunRig r, Member who, byte[] data, string version = "2026-01-01|ClassicDB_9_9_9_z0000.sql.gz|abc", string name = "classic", string? sha = null, long? len = null)
        => r.Hub.Datasets.Upload(who.Name, who.Role, "p:" + who.Name.ToLowerInvariant(), name, version, sha ?? Sha(data), len ?? data.Length, new MemoryStream(data), CancellationToken.None);

    static async Task<DatasetException> Refused(Task<JsonObject> t) => await Assert.ThrowsAsync<DatasetException>(() => t);

    static async Task<byte[]> Down(FunRig r, Member who, string name = "classic")
    {
        await using var fs = r.Hub.Datasets.OpenForDownload(name, who.Role, "p:" + who.Name.ToLowerInvariant(), out _);
        using var ms = new MemoryStream();
        await fs.CopyToAsync(ms);
        return ms.ToArray();
    }

    static async Task<JsonObject> Eventually(FunSock s, string type, int count = 1)
    {
        for (int i = 0; i < 100; i++)
        {
            var l = s.Of(type);
            if (l.Count >= count) return l[count - 1];
            await Task.Delay(20);
        }
        throw new Xunit.Sdk.XunitException("no " + type);
    }

    // ---------------------------------------------------------------- who may do what

    [Fact]
    public void Members_and_up_read_officers_and_up_manage_guests_neither()
    {
        Assert.False(Roles.Can("guest", Perm.DatasetRead));
        Assert.False(Roles.Can("guest", Perm.DatasetManage));
        foreach (var role in new[] { "member", "veteran" }) { Assert.True(Roles.Can(role, Perm.DatasetRead), role); Assert.False(Roles.Can(role, Perm.DatasetManage), role); }
        foreach (var role in new[] { "officer", "owner" }) { Assert.True(Roles.Can(role, Perm.DatasetRead), role); Assert.True(Roles.Can(role, Perm.DatasetManage), role); }
    }

    [Theory]
    [InlineData("officer")] [InlineData("owner")]
    public async Task Officers_and_owner_upload_and_everybody_with_read_gets_the_same_bytes(string role)
    {
        using var r = new FunRig();
        var boss = r.Join("Wing", role); var mem = r.Join("Mia", "member"); var vet = r.Join("Vic", "veteran");
        var data = Bundle();
        var v = await Up(r, boss, data);
        Assert.True((bool)v["available"]!);
        Assert.Equal(Sha(data), (string)v["sha256"]!);
        Assert.Equal(data.Length, (long)v["size"]!);
        Assert.Equal("Wing", (string)v["uploadedBy"]!);
        Assert.Equal(data, await Down(r, mem));
        Assert.Equal(data, await Down(r, vet));
        Assert.Equal(data, await Down(r, boss));
    }

    [Theory]
    [InlineData("guest")] [InlineData("member")] [InlineData("veteran")]
    public async Task Others_cannot_upload(string role)
    {
        using var r = new FunRig();
        var m = r.Join("Mia", role);
        var e = await Refused(Up(r, m, Bundle()));
        Assert.Equal(403, e.Status);
        Assert.Null(r.Hub.Datasets.Peek("classic"));
        Assert.Empty(r.Hub.Datasets.DiskFiles().Where(f => f.EndsWith(".bin")));
    }

    [Fact]
    public async Task Guests_cannot_download_or_ask()
    {
        using var r = new FunRig();
        var boss = r.Join("Wing", "officer"); var g = r.Join("Gus", "guest");
        await Up(r, boss, Bundle());
        var e = Assert.Throws<DatasetException>(() => r.Hub.Datasets.OpenForDownload("classic", "guest", "g:1.2.3.4", out _));
        Assert.Equal(403, e.Status);
        await r.Send(g, new { t = "dataset.info", name = "classic" });
        Assert.Equal("guest", (string)r.Sock("Gus").LastError()!["code"]!);
        Assert.Empty(r.Sock("Gus").Of("dataset.data"));
    }

    [Fact]
    public async Task Info_says_what_the_lodge_holds_and_who_may_manage()
    {
        using var r = new FunRig();
        var boss = r.Join("Wing", "officer"); var mem = r.Join("Mia", "member");
        await r.Send(mem, new { t = "dataset.info", name = "classic" });
        var d0 = r.Sock("Mia").Of("dataset.data").Last();
        Assert.False((bool)d0["available"]!);
        Assert.False((bool)d0["canManage"]!);
        await Up(r, boss, Bundle(), "v1");
        r.Clock.Advance(TimeSpan.FromSeconds(3));
        await r.Send(boss, new { t = "dataset.info", name = "classic" });
        var d1 = r.Sock("Wing").Of("dataset.data").Last();
        Assert.True((bool)d1["available"]!);
        Assert.Equal("v1", (string)d1["version"]!);
        Assert.True((bool)d1["canManage"]!);
        Assert.Equal("classic", (string)d1["name"]!);
    }

    [Fact]
    public async Task Unknown_dataset_names_are_refused_and_never_touch_the_disk()
    {
        using var r = new FunRig();
        var boss = r.Join("Wing", "owner");
        foreach (var name in new[] { "nope", "../classic", "CLASSIC", "" })
            Assert.Equal(404, (await Refused(Up(r, boss, Bundle(), name: name))).Status);
        Assert.Throws<DatasetException>(() => r.Hub.Datasets.OpenForDownload("nope", "owner", "p:x", out _));
        Assert.Empty(r.Hub.Datasets.DiskFiles().Where(f => f.EndsWith(".bin") || f.EndsWith(".part")));
    }

    // ---------------------------------------------------------------- the upload itself

    [Fact]
    public async Task Size_cap_is_enforced_announced_and_actual()
    {
        using var r = new FunRig(c => c.ClassicMaxMb = 1);
        var boss = r.Join("Wing", "officer");
        var big = new byte[1024 * 1024 + 1];
        Assert.Equal(413, (await Refused(Up(r, boss, big))).Status);
        // announces small, sends more
        var data = Bundle();
        var e = await Refused(Up(r, boss, data, len: data.Length - 10));
        Assert.Equal("length", e.Reason);
        // announces more than it sends
        e = await Refused(Up(r, boss, data, len: data.Length + 10));
        Assert.Equal("length", e.Reason);
        Assert.Equal(411, (await Refused(r.Hub.Datasets.Upload(boss.Name, boss.Role, "p:wing", "classic", "v", Sha(data), null, new MemoryStream(data), CancellationToken.None))).Status);
        Assert.Equal(400, (await Refused(Up(r, boss, Array.Empty<byte>()))).Status);
        Assert.Null(r.Hub.Datasets.Peek("classic"));
        Assert.Empty(r.Hub.Datasets.DiskFiles().Where(f => f.EndsWith(".bin") || f.EndsWith(".part")));
    }

    [Fact]
    public async Task Hash_mismatch_is_refused_and_nothing_is_kept()
    {
        using var r = new FunRig();
        var boss = r.Join("Wing", "officer");
        var data = Bundle();
        var e = await Refused(Up(r, boss, data, sha: new string('0', 64)));
        Assert.Equal("sha-mismatch", e.Reason);
        Assert.Equal("bad-sha", (await Refused(Up(r, boss, data, sha: "xyz"))).Reason);
        Assert.Null(r.Hub.Datasets.Peek("classic"));
        Assert.Empty(r.Hub.Datasets.DiskFiles().Where(f => f.EndsWith(".bin") || f.EndsWith(".part")));
    }

    [Fact]
    public async Task A_version_is_needed()
    {
        using var r = new FunRig();
        var boss = r.Join("Wing", "officer");
        Assert.Equal("bad-version", (await Refused(Up(r, boss, Bundle(), version: "  "))).Reason);
    }

    [Fact]
    public async Task Replacing_swaps_atomically_and_the_old_bytes_are_gone()
    {
        using var r = new FunRig();
        var boss = r.Join("Wing", "officer"); var mem = r.Join("Mia", "member");
        var one = Bundle();
        var two = Bundle(e => e.Add(("updates/z2_two.sql", Encoding.UTF8.GetBytes("UPDATE fake SET a=2;"))));
        await Up(r, boss, one, "v1");
        var blobs1 = r.Hub.Datasets.DiskFiles().Where(f => f.EndsWith(".bin")).ToList();
        Assert.Single(blobs1);
        await Up(r, boss, two, "v2");
        var blobs2 = r.Hub.Datasets.DiskFiles().Where(f => f.EndsWith(".bin")).ToList();
        Assert.Single(blobs2);
        Assert.NotEqual(blobs1[0], blobs2[0]);
        Assert.Equal(two, await Down(r, mem));
        Assert.Equal("v2", r.Hub.Datasets.Peek("classic")!.Version);
    }

    [Fact]
    public async Task A_failed_replacement_leaves_the_earlier_one_untouched()
    {
        using var r = new FunRig();
        var boss = r.Join("Wing", "officer");
        var good = Bundle();
        await Up(r, boss, good, "v1");
        var before = r.Hub.Datasets.DiskFiles().OrderBy(x => x).ToList();
        Assert.Equal(400, (await Refused(Up(r, boss, Bundle(), "v2", sha: new string('a', 64)))).Status);        // damaged in transit
        Assert.Equal(415, (await Refused(Up(r, boss, new byte[] { 1, 2, 3, 4 }, "v2"))).Status);                  // not a bundle
        Assert.Equal(before, r.Hub.Datasets.DiskFiles().OrderBy(x => x).ToList());
        Assert.Equal("v1", r.Hub.Datasets.Peek("classic")!.Version);
        Assert.Equal(good, await Down(r, boss));
    }

    [Fact]
    public async Task A_restart_keeps_the_dataset_and_sweeps_half_written_files()
    {
        var dir = new TempDir();
        try
        {
            var data = Bundle();
            using (var r = new FunRig(c => { }, null)) { }   // (separate rigs have their own folders; build the second one on the first one's folder below)
            var cfg = LodgeConfig.ForTests(dir.Path);
            T.WriteCodes(cfg, $"Elan:{T.ElanCode}:owner");
            var hub1 = new LodgeHub(cfg, new Auth(cfg, T.Log), new FileStore(cfg), T.Log, new FakeClock());
            await hub1.Datasets.Upload("Elan", "owner", "p:elan", "classic", "v1", Sha(data), data.Length, new MemoryStream(data), CancellationToken.None);
            File.WriteAllText(Path.Combine(dir.Path, "datasets", "deadbeefdeadbeefdeadbeefdeadbeef.part"), "x");
            File.WriteAllText(Path.Combine(dir.Path, "datasets", "00000000000000000000000000000000.bin"), "orphan");
            var hub2 = new LodgeHub(cfg, new Auth(cfg, T.Log), new FileStore(cfg), T.Log, new FakeClock());
            Assert.Equal("v1", hub2.Datasets.Peek("classic")!.Version);
            Assert.Single(hub2.Datasets.DiskFiles().Where(f => f.EndsWith(".bin")));
            Assert.Empty(hub2.Datasets.DiskFiles().Where(f => f.EndsWith(".part")));
        }
        finally { dir.Dispose(); }
    }

    // ---------------------------------------------------------------- the bundle check

    [Fact]
    public async Task Bundle_check_accepts_the_real_shape_and_refuses_the_rest()
    {
        using var r = new FunRig();
        var boss = r.Join("Wing", "owner");
        // fine: without updates too
        await Up(r, boss, Bundle(e => e.RemoveAll(x => x.name.StartsWith("updates/"))));
        var cases = new (string what, Action<List<(string name, byte[] data)>> tweak)[]
        {
            ("no licence", e => e.RemoveAll(x => x.name == "LICENSE.md")),
            ("no authors", e => e.RemoveAll(x => x.name == "AUTHORS")),
            ("no source.json", e => e.RemoveAll(x => x.name == "source.json")),
            ("no dump", e => e.RemoveAll(x => x.name.EndsWith(".sql.gz"))),
            ("two dumps", e => e.Add(("ClassicDB_other.sql.gz", Gz(FakeSql())))),
            ("a program", e => e.Add(("setup.exe", new byte[] { 0x4D, 0x5A, 1, 2 }))),
            ("a script in updates", e => e.Add(("updates/run.ps1", new byte[] { 1 }))),
            ("a path out of the folder", e => e.Add(("../evil.sql", new byte[] { 1 }))),
            ("a nested path", e => e.Add(("updates/sub/x.sql", new byte[] { 1 }))),
            ("an absolute path", e => e.Add(("/etc/passwd", new byte[] { 1 }))),
            ("a drive path", e => e.Add(("C:/x.sql", new byte[] { 1 }))),
            ("a dump that is not gzip", e => { e.RemoveAll(x => x.name.EndsWith(".sql.gz")); e.Add(("ClassicDB_plain.sql.gz", Encoding.UTF8.GetBytes(FakeSql()))); }),
            ("a huge source.json", e => { e.RemoveAll(x => x.name == "source.json"); e.Add(("source.json", new byte[20000])); }),
            ("an empty licence", e => { e.RemoveAll(x => x.name == "LICENSE.md"); e.Add(("LICENSE.md", Array.Empty<byte>())); }),
        };
        foreach (var (what, tweak) in cases)
        {
            r.Clock.Advance(TimeSpan.FromMinutes(11));   // (the upload rate limit counts refused tries too)
            var ex = await Refused(Up(r, boss, Bundle(tweak)));
            Assert.True(ex.Status == 415, what + " -> " + ex.Status + " " + ex.Message);
        }
        r.Clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(415, (await Refused(Up(r, boss, Encoding.UTF8.GetBytes("this is not a zip at all, just text of some length")))).Status);
        Assert.Equal(1, r.Hub.Datasets.DiskFiles().Count(f => f.EndsWith(".bin")));
    }

    // ---------------------------------------------------------------- delete

    [Fact]
    public async Task Delete_by_officers_and_owner_only()
    {
        using var r = new FunRig();
        var boss = r.Join("Wing", "officer"); var mem = r.Join("Mia", "member"); var g = r.Join("Gus", "guest");
        await Up(r, boss, Bundle());
        foreach (var who in new[] { mem, g })
        {
            await r.Send(who, new { t = "dataset.delete", name = "classic" });
            Assert.Equal("forbidden", (string)r.Sock(who.Name).Of("dataset.result").Last()["why"]!);
        }
        Assert.NotNull(r.Hub.Datasets.Peek("classic"));
        await r.Send(boss, new { t = "dataset.delete", name = "classic" });
        Assert.True((bool)r.Sock("Wing").Of("dataset.result").Last()["ok"]!);
        Assert.Null(r.Hub.Datasets.Peek("classic"));
        Assert.Empty(r.Hub.Datasets.DiskFiles().Where(f => f.EndsWith(".bin")));
        Assert.Equal(404, Assert.Throws<DatasetException>(() => r.Hub.Datasets.OpenForDownload("classic", "member", "p:mia", out _)).Status);
        await r.Send(boss, new { t = "dataset.delete", name = "classic" });
        Assert.Equal("missing", (string)r.Sock("Wing").Of("dataset.result").Last()["why"]!);
    }

    // ---------------------------------------------------------------- announcements

    [Fact]
    public async Task Changes_are_announced_to_members_and_not_to_guests()
    {
        using var r = new FunRig();
        var boss = r.Join("Wing", "officer"); r.Join("Mia", "member"); r.Join("Gus", "guest");
        await Up(r, boss, Bundle(), "v1");
        var a = await Eventually(r.Sock("Mia"), "dataset.changed");
        Assert.Equal("new", (string)a["action"]!);
        Assert.Equal("v1", (string)a["version"]!);
        await Up(r, boss, Bundle(), "v2");
        Assert.Equal("replace", (string)(await Eventually(r.Sock("Mia"), "dataset.changed", 2))["action"]!);
        await r.Send(boss, new { t = "dataset.delete", name = "classic" });
        var d = await Eventually(r.Sock("Mia"), "dataset.changed", 3);
        Assert.Equal("delete", (string)d["action"]!);
        Assert.False((bool)d["available"]!);
        Assert.Empty(r.Sock("Gus").Of("dataset.changed"));
    }

    // ---------------------------------------------------------------- rate limit

    [Fact]
    public async Task Downloads_are_limited_per_member_per_hour()
    {
        using var r = new FunRig(c => c.ClassicDownloadsPerHour = 3);
        var boss = r.Join("Wing", "officer"); var mem = r.Join("Mia", "member"); var other = r.Join("Max", "member");
        await Up(r, boss, Bundle());
        for (int i = 0; i < 3; i++) await Down(r, mem);
        var e = await Assert.ThrowsAsync<DatasetException>(() => Down(r, mem));
        Assert.Equal(429, e.Status);
        await Down(r, other);                                    // another member is not affected
        r.Clock.Advance(TimeSpan.FromMinutes(61));
        await Down(r, mem);                                      // the window rolled
    }

    // ---------------------------------------------------------------- switched off

    [Fact]
    public async Task Switched_off_is_not_advertised_and_everything_answers_disabled_but_the_data_stays()
    {
        var dir = new TempDir();
        try
        {
            var data = Bundle();
            var on = LodgeConfig.ForTests(dir.Path);
            T.WriteCodes(on, $"Elan:{T.ElanCode}:owner");
            var hubOn = new LodgeHub(on, new Auth(on, T.Log), new FileStore(on), T.Log, new FakeClock());
            await hubOn.Datasets.Upload("Elan", "owner", "p:elan", "classic", "v1", Sha(data), data.Length, new MemoryStream(data), CancellationToken.None);
            Assert.Contains("classic", hubOn.FeatureList());

            var off = LodgeConfig.ForTests(dir.Path); off.ClassicEnabled = false;
            var clock = new FakeClock();
            using var r2 = new FunRig(c => c.ClassicEnabled = false);
            Assert.DoesNotContain("classic", r2.Hub.FeatureList());
            var boss = r2.Join("Wing", "owner"); var mem = r2.Join("Mia", "member");
            Assert.Equal("disabled", (await Refused(Up(r2, boss, data))).Reason);
            Assert.Equal("disabled", Assert.Throws<DatasetException>(() => r2.Hub.Datasets.OpenForDownload("classic", "owner", "p:wing", out _)).Reason);
            await r2.Send(mem, new { t = "dataset.info", name = "classic" });
            Assert.Equal("disabled", (string)r2.Sock("Mia").LastError()!["code"]!);
            await r2.Send(boss, new { t = "dataset.delete", name = "classic" });
            Assert.Equal("disabled", (string)r2.Sock("Wing").Of("dataset.result").Last()["why"]!);
            Assert.False((bool)r2.Hub.Welcome(boss)["canShareClassic"]!);

            // the stored bundle survives a restart with the switch off
            var hubOff = new LodgeHub(off, new Auth(off, T.Log), new FileStore(off), T.Log, clock);
            Assert.NotNull(hubOff.Datasets.Peek("classic"));
        }
        finally { dir.Dispose(); }
    }

    [Fact]
    public void Welcome_advertises_the_feature_and_who_may_share()
    {
        using var r = new FunRig();
        var boss = r.Join("Wing", "officer"); var mem = r.Join("Mia", "member");
        Assert.Contains("classic", ((JsonArray)r.Hub.Welcome(boss)["features"]!).Select(x => (string)x!));
        Assert.True((bool)r.Hub.Welcome(boss)["canShareClassic"]!);
        Assert.False((bool)r.Hub.Welcome(mem)["canShareClassic"]!);
        Assert.Equal("2.25.0", LodgeHub.Version);
    }

    [Fact]
    public async Task Upload_rate_limit_stops_a_loop()
    {
        using var r = new FunRig();
        var boss = r.Join("Wing", "officer");
        for (int i = 0; i < 5; i++) await Up(r, boss, Bundle(), "v" + i);
        Assert.Equal(429, (await Refused(Up(r, boss, Bundle(), "v9"))).Status);
        r.Clock.Advance(TimeSpan.FromMinutes(11));
        await Up(r, boss, Bundle(), "v10");
    }
}
