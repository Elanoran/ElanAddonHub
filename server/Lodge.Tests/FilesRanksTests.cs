using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Xunit;

namespace Lodge.Tests;

// Lodge 2.33 (feature "files2"): who sees a slot, who may update it, the description, editing without uploading, the cover picture
public class FilesRanksTests
{
    static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();
    static byte[] Png() { var b = new byte[64]; new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(b, 0); return b; }

    static Task<JsonObject> Up(FunRig r, Member who, string? slotId, string? slotName, string file, byte[] data)
        => r.Hub.SharedFiles.Upload(who.Name, who.Role, "p:" + who.Name.ToLowerInvariant(), slotId, slotName, file, "", Sha(data), data.Length, new MemoryStream(data), CancellationToken.None);

    static async Task<JsonObject> Edit(FunRig r, Member who, object msg)
    {
        await r.Send(who, msg);
        for (int i = 0; i < 100; i++) { var l = r.Sock(who.Name).Of("files.result"); if (l.Count > 0) return l.Last(); await Task.Delay(20); }
        throw new Xunit.Sdk.XunitException("no files.result");
    }

    static string[] Listed(FunRig r, Member who) => ((JsonArray)r.Hub.SharedFiles.ListJson(who)["slots"]!).Select(s => (string)s!["name"]!).ToArray();

    [Fact]
    public void The_feature_is_announced()
    {
        using var r = new FunRig();
        Assert.Contains("files2", r.Hub.FeatureList());
    }

    [Fact]
    public async Task A_slot_for_officers_does_not_exist_for_anyone_below()
    {
        using var r = new FunRig();
        var o = r.Join("Wing", "officer"); var v = r.Join("Vee", "veteran"); var m = r.Join("Mia", "member"); var g = r.Join("Gus", "guest");
        var s = await Up(r, o, null, "Loot rules", "rules.txt", "x"u8.ToArray());
        var id = (string)s["id"]!;
        Assert.Equal(new[] { "Loot rules" }, Listed(r, g));                        // everyone by default, guests too
        var res = await Edit(r, o, new { t = "files.edit", slot = id, see = "officer" });
        Assert.True((bool)res["ok"]!);
        Assert.Empty(Listed(r, v)); Assert.Empty(Listed(r, m)); Assert.Empty(Listed(r, g));
        Assert.Equal(new[] { "Loot rules" }, Listed(r, o));
        var down = Assert.Throws<FilesException>(() => r.Hub.SharedFiles.OpenVersion(id, 1, out _, "member"));
        Assert.Equal(404, down.Status);                                             // not "forbidden": it doesn't exist for them
        r.Hub.SharedFiles.OpenVersion(id, 1, out _, "officer").Dispose();
        var refused = await Assert.ThrowsAsync<FilesException>(() => Up(r, v, id, null, "rules.txt", "y"u8.ToArray()));
        Assert.Equal(404, refused.Status);
        // members and up
        await Edit(r, o, new { t = "files.edit", slot = id, see = "member" });
        Assert.Equal(new[] { "Loot rules" }, Listed(r, m));
        Assert.Empty(Listed(r, g));
    }

    [Fact]
    public async Task Notices_reach_only_who_sees_the_slot()
    {
        using var r = new FunRig();
        var o = r.Join("Wing", "officer"); var m = r.Join("Mia", "member");
        var s = await Up(r, o, null, "Plans", "p.txt", "x"u8.ToArray());
        var id = (string)s["id"]!;
        await Edit(r, o, new { t = "files.edit", slot = id, see = "officer" });
        await Task.Delay(100);
        int before = r.Sock("Mia").Of("files.changed").Count;                     // the edit that hid it: Mia heard it (the slot leaves her list)
        await Up(r, o, id, null, "p.txt", "y"u8.ToArray());
        await Task.Delay(150);
        Assert.Equal(before, r.Sock("Mia").Of("files.changed").Count);             // the new version: not to Mia
        Assert.True(r.Sock("Wing").Of("files.changed").Count >= 2);
    }

    [Fact]
    public async Task Veterans_may_update_a_slot_set_for_them_but_not_rename_or_change_ranks()
    {
        using var r = new FunRig();
        var o = r.Join("Wing", "officer"); var v = r.Join("Vee", "veteran"); var m = r.Join("Mia", "member");
        var s = await Up(r, o, null, "WA pack", "wa.txt", "x"u8.ToArray());
        var id = (string)s["id"]!;
        Assert.Equal(403, (await Assert.ThrowsAsync<FilesException>(() => Up(r, v, id, null, "wa.txt", "y"u8.ToArray()))).Status);   // officers only by default
        await Edit(r, o, new { t = "files.edit", slot = id, edit = "veteran" });
        var v2 = await Up(r, v, id, null, "wa.txt", "y"u8.ToArray());
        Assert.True((bool)v2["canUpdate"]!);
        Assert.Equal(403, (await Assert.ThrowsAsync<FilesException>(() => Up(r, m, id, null, "wa.txt", "z"u8.ToArray()))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<FilesException>(() => Up(r, v, null, "Mine", "a.txt", "z"u8.ToArray()))).Status);   // new slots: officers
        var desc = await Edit(r, v, new { t = "files.edit", slot = id, desc = "Raid WeakAuras.\r\nImport the newest one.\u0007" });
        Assert.True((bool)desc["ok"]!);
        Assert.Equal("forbidden", (string)(await Edit(r, v, new { t = "files.edit", slot = id, name = "Renamed" }))["why"]!);
        Assert.Equal("forbidden", (string)(await Edit(r, v, new { t = "files.edit", slot = id, see = "guest" }))["why"]!);
        var listed = ((JsonArray)r.Hub.SharedFiles.ListJson(m)["slots"]!)[0]!;
        Assert.Equal("Raid WeakAuras.\nImport the newest one.", (string)listed["desc"]!);
        Assert.Equal("veteran", (string)listed["edit"]!);
        Assert.Null(listed["canUpdate"]);                                           // Mia (member) may not
        var (ok, why) = r.Hub.SharedFiles.Rollback(v, id, 1, out _, out _);
        Assert.True(ok, why);
    }

    [Fact]
    public async Task Edit_renames_fixes_a_note_and_marks_an_addon_only_when_it_is_one()
    {
        using var r = new FunRig();
        var o = r.Join("Wing", "officer");
        var s = await Up(r, o, null, "Notes", "n.txt", "x"u8.ToArray());
        var id = (string)s["id"]!;
        await Up(r, o, null, "Other", "o.txt", "x"u8.ToArray());
        Assert.Equal("name-taken", (string)(await Edit(r, o, new { t = "files.edit", slot = id, name = "other" }))["why"]!);
        Assert.True((bool)(await Edit(r, o, new { t = "files.edit", slot = id, name = "Raid notes", noteV = 1, note = "typo fixed" }))["ok"]!);
        var slot = ((JsonArray)r.Hub.SharedFiles.ListJson(o)["slots"]!).First(x => (string)x!["id"]! == id)!;
        Assert.Equal(("Raid notes", "typo fixed"), ((string)slot["name"]!, (string)slot["versions"]![0]!["note"]!));
        Assert.Equal("not-addon", (string)(await Edit(r, o, new { t = "files.edit", slot = id, kind = "addon" }))["why"]!);
        Assert.Equal("bad-rank", (string)(await Edit(r, o, new { t = "files.edit", slot = id, see = "owner" }))["why"]!);
        Assert.Equal("missing", (string)(await Edit(r, o, new { t = "files.edit", slot = new string('a', 32), desc = "x" }))["why"]!);
    }

    [Fact]
    public async Task A_cover_picture_is_checked_by_its_bytes_and_hidden_with_its_slot()
    {
        using var r = new FunRig();
        var o = r.Join("Wing", "officer"); var m = r.Join("Mia", "member");
        var s = await Up(r, o, null, "UI pack", "ui.txt", "x"u8.ToArray());
        var id = (string)s["id"]!;
        var png = Png();
        var bad = "not a picture at all"u8.ToArray();
        Assert.Equal("not-image", (await Assert.ThrowsAsync<FilesException>(() => r.Hub.SharedFiles.PutCover("Wing", "officer", id, Sha(bad), bad.Length, new MemoryStream(bad), default))).Reason);
        Assert.Equal(403, (await Assert.ThrowsAsync<FilesException>(() => r.Hub.SharedFiles.PutCover("Mia", "member", id, Sha(png), png.Length, new MemoryStream(png), default))).Status);
        var v = await r.Hub.SharedFiles.PutCover("Wing", "officer", id, Sha(png), png.Length, new MemoryStream(png), default);
        Assert.Equal(1, (int)v!["cover"]!);
        Assert.Equal("png", r.Hub.SharedFiles.GetCover(id, "member")!.Value.type);
        await r.Hub.SharedFiles.PutCover("Wing", "officer", id, Sha(png), png.Length, new MemoryStream(png), default);
        Assert.Equal(2, (int)((JsonArray)r.Hub.SharedFiles.ListJson(m)["slots"]!)[0]!["cover"]!);
        await Edit(r, o, new { t = "files.edit", slot = id, see = "officer" });
        Assert.Null(r.Hub.SharedFiles.GetCover(id, "member"));
        Assert.True((bool)(await Edit(r, o, new { t = "files.edit", slot = id, cover = false }))["ok"]!);
        Assert.Null(r.Hub.SharedFiles.GetCover(id, "officer"));
        var big = new byte[3 * 1024 * 1024]; png.CopyTo(big, 0);
        Assert.Equal(413, (await Assert.ThrowsAsync<FilesException>(() => r.Hub.SharedFiles.PutCover("Wing", "officer", id, Sha(big), big.Length, new MemoryStream(big), default))).Status);
    }

    [Fact]
    public void Descriptions_keep_line_breaks_and_lose_control_characters()
    {
        Assert.Equal("a\nb", FilesModule.CleanDesc(" a\r\nb\u0001 "));
        Assert.Equal(FilesModule.MaxDescLines, FilesModule.CleanDesc(string.Join("\n", Enumerable.Range(0, 50).Select(i => "l" + i))).Split('\n').Length);
        Assert.Equal(FilesModule.MaxDescChars, FilesModule.CleanDesc(new string('x', 5000)).Length);
        Assert.Equal("gif", FilesModule.ImageType("GIF89a......"u8.ToArray()));
        Assert.Null(FilesModule.ImageType("hello world!"u8.ToArray()));
    }
}
