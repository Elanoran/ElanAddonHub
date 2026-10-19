using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Xunit;

namespace Lodge.Tests;

// Lodge 2.14: shared files in named slots
public class FilesTests
{
    static byte[] Bytes(int n, byte fill = 7) { var b = new byte[n]; Array.Fill(b, fill); return b; }
    static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    static byte[] Zip(params (string name, string text)[] entries)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var (name, text) in entries)
            {
                var e = z.CreateEntry(name);
                using var w = new StreamWriter(e.Open());
                w.Write(text);
            }
        return ms.ToArray();
    }

    static Task<JsonObject> Up(FunRig r, Member who, string? slotId, string? slotName, string file, byte[] data, string note = "", string? sha = null, long? len = null)
        => r.Hub.SharedFiles.Upload(who.Name, who.Role, "p:" + who.Name.ToLowerInvariant(), slotId, slotName, file, note, sha ?? Sha(data), len ?? data.Length,
            new MemoryStream(data), CancellationToken.None);

    static async Task<FilesException> Refused(Task<JsonObject> t) => await Assert.ThrowsAsync<FilesException>(() => t);

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

    // ---------------------------------------------------------------- rules

    [Theory]
    [InlineData("a.exe", true)] [InlineData("A.EXE", true)] [InlineData("x.dll", true)] [InlineData("x.ps1", true)] [InlineData("x.psm1", true)]
    [InlineData("x.js", true)] [InlineData("x.jse", true)] [InlineData("x.lnk", true)] [InlineData("x.reg", true)] [InlineData("x.hta", true)]
    [InlineData("x.application", true)] [InlineData("x.appref-ms", true)] [InlineData("x.jar", true)] [InlineData("x.msi", true)]
    [InlineData("setup.exe.", true)] [InlineData("setup.exe ", true)] [InlineData("a.txt.exe", true)]
    [InlineData("a.txt", false)] [InlineData("a.zip", false)] [InlineData("exe", false)] [InlineData("a.exe.txt", false)] [InlineData("a.json", false)]
    [InlineData("UI pack.7z", false)] [InlineData("x.lua", false)]
    public void Blocked_types_are_recognised(string name, bool blocked) => Assert.Equal(blocked, FileRules.IsBlockedName(name));

    [Theory]
    [InlineData("/etc/passwd", true)] [InlineData("\\windows\\x.txt", true)] [InlineData("C:\\x.txt", true)] [InlineData("c:/x", true)]
    [InlineData("../x.txt", true)] [InlineData("a/../../x", true)] [InlineData("a\\..\\x", true)] [InlineData("a.txt:stream", true)] [InlineData("", true)]
    [InlineData("a/b/c.txt", false)] [InlineData("a/", false)] [InlineData("..hidden/x", false)] [InlineData("a..b/x", false)]
    public void Unsafe_zip_paths_are_recognised(string path, bool bad) => Assert.Equal(bad, FileRules.UnsafeEntryPath(path));

    [Fact]
    public void Zip_check_lists_entries_without_extracting()
    {
        Assert.Null(FileRules.CheckZip(new MemoryStream(Zip(("Interface/AddOns/Foo/Foo.lua", "x"), ("Foo.toc", "y")))));
        Assert.NotNull(FileRules.CheckZip(new MemoryStream(Zip(("docs/readme.txt", "x"), ("tools/run.exe", "MZ")))));
        Assert.NotNull(FileRules.CheckZip(new MemoryStream(Zip(("a/RUN.CMD", "x")))));
        Assert.NotNull(FileRules.CheckZip(new MemoryStream(Zip(("../evil.txt", "x")))));
        Assert.NotNull(FileRules.CheckZip(new MemoryStream(Zip(("/abs.txt", "x")))));
        Assert.NotNull(FileRules.CheckZip(new MemoryStream(Zip(("C:/x.txt", "x")))));
        Assert.NotNull(FileRules.CheckZip(new MemoryStream(Bytes(500))));          // not a zip at all
    }

    // ---------------------------------------------------------------- permissions

    [Fact]
    public void Only_officers_and_up_manage_everybody_reads()
    {
        foreach (var role in new[] { "guest", "member", "veteran" })
        {
            Assert.False(Roles.Can(role, Perm.FilesManage), role);
            Assert.True(Roles.Can(role, Perm.FilesRead), role);
        }
        foreach (var role in new[] { "officer", "owner" }) Assert.True(Roles.Can(role, Perm.FilesManage), role);
    }

    [Theory]
    [InlineData("guest")] [InlineData("member")] [InlineData("veteran")]
    public async Task Members_below_officer_cannot_upload(string role)
    {
        using var r = new FunRig();
        var m = r.Join("Mia", role);
        var e = await Refused(Up(r, m, null, "Pack", "a.txt", Bytes(10)));
        Assert.Equal(403, e.Status);
        Assert.Equal(0, r.Hub.SharedFiles.SlotCount);
    }

    [Theory]
    [InlineData("officer")] [InlineData("owner")]
    public async Task Officers_and_owner_can_upload_and_replace(string role)
    {
        using var r = new FunRig();
        var m = r.Join("Wing", role);
        var s = await Up(r, m, null, "Raid UI pack", "pack.zip", Zip(("a.lua", "x")));
        Assert.Equal(1, (int)s["versions"]![0]!["v"]!);
        var s2 = await Up(r, m, (string)s["id"]!, null, "pack.zip", Zip(("a.lua", "y")));
        Assert.Equal(2, ((JsonArray)s2["versions"]!).Count);
    }

    [Fact]
    public async Task Member_cannot_replace_delete_or_roll_back_but_can_list()
    {
        using var r = new FunRig();
        var boss = r.Join("Boss", "officer"); var mem = r.Join("Mem", "member"); r.Join("Gus", "guest");
        var s = await Up(r, boss, null, "Pack", "a.txt", Bytes(10));
        var id = (string)s["id"]!;
        await Up(r, boss, id, null, "a.txt", Bytes(11));
        Assert.Equal(403, (await Refused(Up(r, mem, id, null, "a.txt", Bytes(12)))).Status);
        await r.Send(mem, new { t = "files.delete", slot = id });
        Assert.Equal("forbidden", (string)r.Sock("Mem").Of("files.result").Last()["why"]!);
        await r.Send(mem, new { t = "files.rollback", slot = id, v = 1 });
        Assert.Equal("forbidden", (string)r.Sock("Mem").Of("files.result").Last()["why"]!);
        Assert.Equal(2, ((JsonArray)r.Hub.SharedFiles.SlotView(id)!["versions"]!).Count);
        foreach (var n in new[] { "Mem", "Gus" })
        {
            r.Clock.Advance(TimeSpan.FromSeconds(3));
            await r.Send(r.People[n].m, new { t = "files.list" });
            var d = r.Sock(n).Of("files.data").Last();
            Assert.Single((JsonArray)d["slots"]!);
            Assert.False((bool)d["canManage"]!);
        }
    }

    // ---------------------------------------------------------------- versions

    [Fact]
    public async Task New_versions_number_up_and_only_the_newest_three_stay_on_disk()
    {
        using var r = new FunRig();
        var o = r.Join("Wing", "officer");
        var s = await Up(r, o, null, "Pack", "p.txt", Bytes(10, 1), note: "first");
        var id = (string)s["id"]!;
        for (int i = 2; i <= 5; i++) s = await Up(r, o, id, null, "p.txt", Bytes(10, (byte)i), note: "n" + i);
        var vs = ((JsonArray)s["versions"]!).Select(x => (int)x!["v"]!).ToArray();
        Assert.Equal(new[] { 3, 4, 5 }, vs);
        Assert.Equal(3, r.Hub.SharedFiles.DiskFiles().Count(f => f.EndsWith(".bin")));
        Assert.Equal(30, r.Hub.SharedFiles.UsedBytes);
        Assert.Equal("n5", (string)s["versions"]![2]!["note"]!);
    }

    [Fact]
    public async Task Rollback_makes_an_older_version_newest_and_numbers_are_not_reused()
    {
        using var r = new FunRig();
        var o = r.Join("Wing", "officer");
        var s = await Up(r, o, null, "Pack", "p.txt", Bytes(10, 1));
        var id = (string)s["id"]!;
        await Up(r, o, id, null, "p.txt", Bytes(10, 2));
        await Up(r, o, id, null, "p.txt", Bytes(10, 3));
        await r.Send(o, new { t = "files.rollback", slot = id, v = 2 });
        Assert.True((bool)r.Sock("Wing").Of("files.result").Last()["ok"]!);
        var view = r.Hub.SharedFiles.SlotView(id)!;
        Assert.Equal(new[] { 1, 2 }, ((JsonArray)view["versions"]!).Select(x => (int)x!["v"]!).ToArray());
        Assert.Equal(2, r.Hub.SharedFiles.DiskFiles().Count(f => f.EndsWith(".bin")));
        var again = await Up(r, o, id, null, "p.txt", Bytes(10, 9));
        Assert.Equal(4, (int)again["versions"]![2]!["v"]!);
        await r.Send(o, new { t = "files.rollback", slot = id, v = 99 });
        Assert.Equal("missing", (string)r.Sock("Wing").Of("files.result").Last()["why"]!);
    }

    [Fact]
    public async Task Delete_removes_the_slot_and_every_file()
    {
        using var r = new FunRig();
        var o = r.Join("Wing", "owner");
        var s = await Up(r, o, null, "Pack", "p.txt", Bytes(10));
        await Up(r, o, (string)s["id"]!, null, "p.txt", Bytes(11));
        await r.Send(o, new { t = "files.delete", slot = (string)s["id"]! });
        Assert.Equal(0, r.Hub.SharedFiles.SlotCount);
        Assert.Empty(r.Hub.SharedFiles.DiskFiles().Where(f => f.EndsWith(".bin")));
        Assert.Equal(0, r.Hub.SharedFiles.UsedBytes);
    }

    [Fact]
    public async Task Slot_names_are_unique_bounded_and_required()
    {
        using var r = new FunRig();
        var o = r.Join("Wing", "officer");
        await Up(r, o, null, "Pack", "p.txt", Bytes(10));
        Assert.Equal(409, (await Refused(Up(r, o, null, "  pack ", "p.txt", Bytes(10)))).Status);
        Assert.Equal(400, (await Refused(Up(r, o, null, "   ", "p.txt", Bytes(10)))).Status);
        var s = await Up(r, o, null, new string('x', 100), "p.txt", Bytes(10), note: new string('n', 500));
        Assert.Equal(60, ((string)s["name"]!).Length);
        Assert.Equal(200, ((string)s["versions"]![0]!["note"]!).Length);
        Assert.Equal(404, (await Refused(Up(r, o, "00000000000000000000000000000000", null, "p.txt", Bytes(10)))).Status);
    }

    // ---------------------------------------------------------------- safety

    [Theory]
    [InlineData("tool.exe")] [InlineData("run.ps1")] [InlineData("x.dll")] [InlineData("a.js")] [InlineData("a.exe.")]
    public async Task Programs_and_scripts_are_refused(string name)
    {
        using var r = new FunRig();
        var o = r.Join("Wing", "officer");
        var e = await Refused(Up(r, o, null, "Pack", name, Bytes(10)));
        Assert.Equal(415, e.Status);
        Assert.Equal("blocked-type", e.Reason);
        Assert.Empty(r.Hub.SharedFiles.DiskFiles());
    }

    [Fact]
    public async Task A_zip_holding_a_program_or_unsafe_path_is_refused_and_leaves_nothing_behind()
    {
        using var r = new FunRig();
        var o = r.Join("Wing", "officer");
        Assert.Equal("blocked-zip", (await Refused(Up(r, o, null, "A", "p.zip", Zip(("ok.txt", "x"), ("sub/evil.exe", "MZ"))))).Reason);
        Assert.Equal("blocked-zip", (await Refused(Up(r, o, null, "B", "p.zip", Zip(("..\\x.txt", "x"))))).Reason);
        Assert.Equal("blocked-zip", (await Refused(Up(r, o, null, "C", "p.zip", Bytes(100)))).Reason);
        Assert.Empty(r.Hub.SharedFiles.DiskFiles().Where(f => f != "index.json"));
        Assert.Equal(0, r.Hub.SharedFiles.SlotCount);
        var ok = await Up(r, o, null, "D", "p.zip", Zip(("ok.txt", "x")));
        Assert.Equal("p.zip", (string)ok["versions"]![0]!["fileName"]!);
    }

    [Fact]
    public async Task A_wrong_checksum_or_length_is_refused_and_the_partial_file_removed()
    {
        using var r = new FunRig();
        var o = r.Join("Wing", "officer");
        var data = Bytes(1000);
        Assert.Equal("sha-mismatch", (await Refused(Up(r, o, null, "A", "p.txt", data, sha: Sha(Bytes(1000, 9))))).Reason);
        Assert.Equal("bad-sha", (await Refused(Up(r, o, null, "A", "p.txt", data, sha: "zz"))).Reason);
        Assert.Equal("length", (await Refused(Up(r, o, null, "A", "p.txt", data, len: 2000))).Reason);   // shorter than announced
        Assert.Equal("length", (await Refused(Up(r, o, null, "A", "p.txt", data, len: 500))).Reason);    // longer than announced
        Assert.Equal(411, (await Refused(r.Hub.SharedFiles.Upload("Wing", "officer", "k", null, "A", "p.txt", "", Sha(data), null, new MemoryStream(data), default))).Status);
        Assert.Empty(r.Hub.SharedFiles.DiskFiles().Where(f => f != "index.json"));
        Assert.Equal(0, r.Hub.SharedFiles.SlotCount);
    }

    [Fact]
    public async Task Files_are_stored_under_generated_names_and_display_names_are_cleaned()
    {
        using var r = new FunRig();
        var o = r.Join("Wing", "officer");
        var s = await Up(r, o, null, "Pack", "..\\..\\etc/pass:wd*?.txt", Bytes(10));
        var shown = (string)s["versions"]![0]!["fileName"]!;
        Assert.DoesNotContain('/', shown); Assert.DoesNotContain('\\', shown); Assert.DoesNotContain(':', shown); Assert.DoesNotContain('*', shown);
        foreach (var f in r.Hub.SharedFiles.DiskFiles().Where(f => f != "index.json"))
            Assert.Matches("^[0-9a-f]{32}\\.bin$", f);
        Assert.False(File.Exists(Path.Combine(r.Dir.Path, "pass_wd__.txt")));
    }

    // ---------------------------------------------------------------- limits

    [Fact]
    public async Task The_per_file_limit_is_enforced()
    {
        using var r = new FunRig(c => c.FilesMaxMb = 1);
        var o = r.Join("Wing", "officer");
        var big = Bytes(1024 * 1024 + 1);
        Assert.Equal(413, (await Refused(Up(r, o, null, "Big", "b.bin", big))).Status);
        Assert.Equal("length", (await Refused(Up(r, o, null, "Big", "b.bin", big, len: 100))).Reason);   // announces small, sends more
        await Up(r, o, null, "Fits", "b.bin", Bytes(1024 * 1024));
        Assert.Empty(r.Hub.SharedFiles.DiskFiles().Where(f => f.EndsWith(".part")));
    }

    [Fact]
    public async Task The_lodge_total_counts_all_kept_versions_and_pruning_frees_space()
    {
        using var r = new FunRig(c => { c.FilesMaxMb = 1; c.FilesTotalMb = 2; });
        var o = r.Join("Wing", "officer");
        var mb = 1024 * 1024;
        var a = await Up(r, o, null, "A", "a.bin", Bytes(mb, 1));
        var b = await Up(r, o, null, "B", "b.bin", Bytes(mb, 2));
        Assert.Equal(507, (await Refused(Up(r, o, null, "C", "c.bin", Bytes(10)))).Status);
        Assert.Equal(507, (await Refused(Up(r, o, (string)a["id"]!, null, "a.bin", Bytes(10)))).Status);   // a 2nd version of A: no room
        await r.Send(o, new { t = "files.delete", slot = (string)b["id"]! });
        await Up(r, o, null, "C", "c.bin", Bytes(10));
        Assert.Empty(r.Hub.SharedFiles.DiskFiles().Where(f => f.EndsWith(".part")));
    }

    [Fact]
    public async Task Uploads_are_rate_limited_per_person()
    {
        using var r = new FunRig(c => c.FilesUploadsPer10Min = 2);
        var o = r.Join("Wing", "officer");
        await Up(r, o, null, "A", "a.txt", Bytes(10));
        await Up(r, o, null, "B", "a.txt", Bytes(10));
        Assert.Equal(429, (await Refused(Up(r, o, null, "C", "a.txt", Bytes(10)))).Status);
        r.Clock.Advance(TimeSpan.FromMinutes(11));
        await Up(r, o, null, "C", "a.txt", Bytes(10));
    }

    [Fact]
    public async Task Switched_off_it_refuses_everything_and_is_not_advertised()
    {
        using var r = new FunRig(c => c.FilesEnabled = false);
        var o = r.Join("Wing", "owner");
        Assert.Equal("disabled", (await Refused(Up(r, o, null, "A", "a.txt", Bytes(10)))).Reason);
        Assert.DoesNotContain("files", r.Hub.FeatureList());
        await r.Send(o, new { t = "files.list" });
        Assert.Equal("disabled", (string)r.Sock("Wing").Of("files.result").Last()["why"]!);
        var w = r.Hub.Welcome(o);
        Assert.DoesNotContain("files", ((JsonArray)w["features"]!).Select(x => (string)x!));
        Assert.False((bool)w["canManageFiles"]!);
    }

    [Fact]
    public void Switched_on_it_is_advertised_with_the_permission()
    {
        using var r = new FunRig();
        var o = r.Join("Wing", "officer"); var m = r.Join("Mia", "member");
        Assert.Contains("files", r.Hub.FeatureList());
        Assert.True((bool)r.Hub.Welcome(o)["canManageFiles"]!);
        Assert.False((bool)r.Hub.Welcome(m)["canManageFiles"]!);
        Assert.Equal("2.31.1", LodgeHub.Version);
    }

    // ---------------------------------------------------------------- broadcast, download, restart

    [Fact]
    public async Task Every_change_is_announced_to_everybody()
    {
        using var r = new FunRig();
        var o = r.Join("Wing", "officer"); r.Join("Mia", "member"); r.Join("Gus", "guest");
        var s = await Up(r, o, null, "Raid UI pack", "p.txt", Bytes(10));
        var id = (string)s["id"]!;
        var c1 = await Eventually(r.Sock("Mia"), "files.changed", 1);
        Assert.Equal("new", (string)c1["action"]!); Assert.Equal("Raid UI pack", (string)c1["name"]!); Assert.Equal("Wing", (string)c1["by"]!); Assert.Equal(1, (int)c1["v"]!);
        await Up(r, o, id, null, "p.txt", Bytes(11));
        var c2 = await Eventually(r.Sock("Gus"), "files.changed", 2);
        Assert.Equal("version", (string)c2["action"]!); Assert.Equal(2, (int)c2["v"]!);
        await r.Send(o, new { t = "files.rollback", slot = id, v = 1 });
        Assert.Equal("rollback", (string)(await Eventually(r.Sock("Mia"), "files.changed", 3))["action"]!);
        await r.Send(o, new { t = "files.delete", slot = id });
        Assert.Equal("delete", (string)(await Eventually(r.Sock("Mia"), "files.changed", 4))["action"]!);
    }

    [Fact]
    public async Task Download_returns_the_exact_bytes_and_a_missing_version_is_404()
    {
        using var r = new FunRig();
        var o = r.Join("Wing", "officer");
        var data = Enumerable.Range(0, 5000).Select(i => (byte)(i * 7)).ToArray();
        var s = await Up(r, o, null, "Pack", "p.dat", data);
        SlotVersion? ver;
        using (var fs = r.Hub.SharedFiles.OpenVersion((string)s["id"]!, 1, out ver))
        {
            var ms = new MemoryStream(); fs.CopyTo(ms);
            Assert.Equal(data, ms.ToArray());
            Assert.Equal(Sha(data), ver.Sha256);
        }
        Assert.Equal(404, Assert.Throws<FilesException>(() => r.Hub.SharedFiles.OpenVersion((string)s["id"]!, 7, out _)).Status);
        Assert.Equal(404, Assert.Throws<FilesException>(() => r.Hub.SharedFiles.OpenVersion("nope", 1, out _)).Status);
    }

    [Fact]
    public async Task The_index_survives_a_restart_and_leftovers_are_cleaned()
    {
        using var r = new FunRig();
        var o = r.Join("Wing", "officer");
        var s = await Up(r, o, null, "Pack", "p.txt", Bytes(10));
        var dir = Path.Combine(r.Dir.Path, "slots");
        File.WriteAllText(Path.Combine(dir, "deadbeefdeadbeefdeadbeefdeadbeef.part"), "x");
        File.WriteAllText(Path.Combine(dir, "cafebabecafebabecafebabecafebabe.bin"), "orphan");
        var again = new FilesModule(r.Hub);
        Assert.Equal(1, again.SlotCount);
        Assert.Equal(10, again.UsedBytes);
        Assert.DoesNotContain(again.DiskFiles(), f => f.EndsWith(".part") || f.StartsWith("cafebabe"));
        Assert.Equal("Pack", (string)again.SlotView((string)s["id"]!)!["name"]!);
        // a lost content file drops that version, and with it the empty slot
        File.Delete(Directory.GetFiles(dir, "*.bin").Single());
        Assert.Equal(0, new FilesModule(r.Hub).SlotCount);
    }
}
