using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Lodge.Tests;

// Lodge 2.34 (feature "filestext"): a text slot - an import string (a WeakAura, a profile, a macro) shared as a .txt of UTF-8 text, at most 256 KB
public class FilesTextTests
{
    static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    static Task<JsonObject> Up(FunRig r, Member who, string? slotId, string? slotName, string file, byte[] data, string? kind)
        => r.Hub.SharedFiles.Upload(who.Name, who.Role, "p:" + who.Name.ToLowerInvariant(), slotId, slotName, file, "", Sha(data), data.Length, new MemoryStream(data), CancellationToken.None, kind);

    [Fact]
    public void The_feature_is_announced() { using var r = new FunRig(); Assert.Contains("filestext", r.Hub.FeatureList()); }

    [Fact]
    public async Task A_text_slot_says_so_and_stays_text()
    {
        using var r = new FunRig();
        var o = r.Join("Wing", "officer");
        var wa = Encoding.UTF8.GetBytes("!WA:2!S3xtrjoe... raid pack ✓");
        var s = await Up(r, o, null, "Raid WeakAura", "import.txt", wa, "text");
        Assert.Equal("text", (string?)s["kind"]);
        var id = (string)s["id"]!;
        var v2 = await Up(r, o, id, null, "import.txt", Encoding.UTF8.GetBytes("!WA:2!newer"), null);   // an older Outpost: no kind, still text
        Assert.Equal("text", (string?)v2["kind"]);
        var e = await Assert.ThrowsAsync<FilesException>(() => Up(r, o, id, null, "data.bin", new byte[] { 0x50, 0x4B, 3, 4 }, null));
        Assert.Equal("not-text", e.Reason);                                                         // ... so it has to stay text
    }

    [Fact]
    public async Task Text_must_be_a_small_txt_of_UTF8()
    {
        using var r = new FunRig();
        var o = r.Join("Wing", "officer");
        async Task<string> Why(string file, byte[] data) => (await Assert.ThrowsAsync<FilesException>(() => Up(r, o, null, "T" + file, file, data, "text"))).Reason;
        Assert.Equal("not-text", await Why("a.lua", "x"u8.ToArray()));
        Assert.Equal("not-text", await Why("b.txt", new byte[] { 0xC3, 0x28 }));                        // invalid UTF-8
        Assert.Equal("not-text", await Why("c.txt", new byte[] { 0x41, 0, 0x42 }));                     // a NUL: binary
        Assert.Equal("too-big", await Why("d.txt", new byte[256 * 1024 + 1]));
        Assert.Empty((JsonArray)r.Hub.SharedFiles.ListJson(o)["slots"]!);
    }
}
