using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Elans.Shared;

using Xunit;

namespace Lodge.Tests;

// Lodge 2.32: a slot marked "addon" (feature "filesaddons") is a zip of WoW addon folders the Outpost can install (Elans.Shared.AddonZip)
public class FilesAddonTests
{
    static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    static byte[] Zip(params (string name, string text)[] entries)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var (name, text) in entries)
            {
                var e = z.CreateEntry(name);
                if (name.EndsWith("/")) continue;
                using var w = new StreamWriter(e.Open());
                w.Write(text);
            }
        return ms.ToArray();
    }

    static byte[] Addon(string version = "1.0") => Zip(("RXPGuides/RXPGuides.toc", "## Interface: 16001\n## Version: " + version + "\n"), ("RXPGuides/core.lua", "-- x"),
        ("RXPGuides_Extra/RXPGuides_Extra_Vanilla.toc", "## Interface: 11509\n"), ("RXPGuides_Extra/x.lua", ""));

    static Task<JsonObject> Up(FunRig r, Member who, string? slotId, string? slotName, string file, byte[] data, string? kind)
        => r.Hub.SharedFiles.Upload(who.Name, who.Role, "p:" + who.Name.ToLowerInvariant(), slotId, slotName, file, "", Sha(data), data.Length, new MemoryStream(data), CancellationToken.None, kind);

    [Fact]
    public void The_feature_is_announced_with_files()
    {
        using var r = new FunRig();
        Assert.Contains("filesaddons", r.Hub.FeatureList());
        using var off = new FunRig(c => c.FilesEnabled = false);
        Assert.DoesNotContain("filesaddons", off.Hub.FeatureList());
    }

    [Fact]
    public async Task An_addon_slot_says_so_and_keeps_it_over_new_versions()
    {
        using var r = new FunRig();
        var wing = r.Join("Wing", "officer");
        var v1 = await Up(r, wing, null, "RXP Guides", "RXPGuides-v1.zip", Addon("1.0"), "addon");
        Assert.Equal("addon", (string?)v1["kind"]);
        var id = (string)v1["id"]!;
        var v2 = await Up(r, wing, id, null, "RXPGuides-v2.zip", Addon("1.1"), null);       // an older Outpost: no header, the mark stays
        Assert.Equal("addon", (string?)v2["kind"]);
        var v3 = await Up(r, wing, id, null, "notes.txt", "hello"u8.ToArray(), "file");      // unmarked on purpose
        Assert.Null(v3["kind"]);
        var plain = await Up(r, wing, null, "Notes", "n.txt", "x"u8.ToArray(), null);
        Assert.Null(plain["kind"]);
        var list = FileSlot.Read(new LodgeJsonFields((JsonObject)((JsonArray)r.Hub.SharedFiles.ListJson(wing)["slots"]!)[0]!));
        Assert.NotNull(list.Id);
    }

    [Theory]
    [InlineData("notazip.txt", "not-addon")]
    public async Task An_addon_must_be_a_zip(string file, string reason)
    {
        using var r = new FunRig();
        var wing = r.Join("Wing", "officer");
        var e = await Assert.ThrowsAsync<FilesException>(() => Up(r, wing, null, "X", file, "x"u8.ToArray(), "addon"));
        Assert.Equal(reason, e.Reason);
    }

    [Fact]
    public async Task An_addon_zip_is_checked_and_a_bad_one_is_refused_with_why()
    {
        using var r = new FunRig();
        var wing = r.Join("Wing", "officer");
        async Task<string> Why(byte[] z) => (await Assert.ThrowsAsync<FilesException>(() => Up(r, wing, null, "X", "x.zip", z, "addon"))).Message;
        Assert.Contains("outside an addon folder", await Why(Zip(("readme.txt", "x"), ("A/A.toc", ""))));
        Assert.Contains("no .toc file", await Why(Zip(("A/A.lua", ""))));
        Assert.Contains("no .toc file", await Why(Zip(("A/B.toc", ""))));                       // a toc of another name is not this folder's
        Assert.Contains("unsafe path", await Why(Zip(("A/../../x.lua", ""), ("A/A.toc", ""))));
        Assert.Contains("isn't a usable addon folder name", await Why(Zip(("CON/CON.toc", ""))));
        Assert.Equal(0, ((JsonArray)r.Hub.SharedFiles.ListJson(wing)["slots"]!).Count);       // nothing stored
        Assert.Equal(400, (await Assert.ThrowsAsync<FilesException>(() => Up(r, wing, null, "Y", "y.zip", Addon(), "plugin"))).Status);
    }

    [Fact]
    public void AddonZip_reads_the_folders_and_versions()
    {
        var res = AddonZip.Check(new MemoryStream(Addon("4.11.29")));
        Assert.True(res.Ok, res.Problem);
        Assert.Equal(new[] { "RXPGuides", "RXPGuides_Extra" }, res.Folders);
        Assert.Equal("4.11.29", res.Versions["RXPGuides"]);
        Assert.Equal("", res.Versions["RXPGuides_Extra"]);
        Assert.True(AddonZip.IsTocOf("Details", "Details_Camelot.toc") && AddonZip.IsTocOf("Details", "Details-Classic.toc") && !AddonZip.IsTocOf("Details", "DetailsX.toc"));
    }
}
