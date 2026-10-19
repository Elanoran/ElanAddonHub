using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Elans.Shared;

namespace Elans.Shared.Tests;

// The Lodge server's one error frame: it survives Write -> Read, writes exactly what LodgeHub.Error wrote at c712015, and every code the
// server sends is in LodgeErrorMsg.Codes (and every listed code is still sent) - so a new code has to be named there first.
public class ErrorMessagesTests
{
    static TestFields Written(Action<ILodgeWriter> write) => TestFields.Written(write);
    static void SameJson(object expected, object actual) => TestFields.SameJson(expected, actual);

    [Fact]
    public void The_error_round_trips()
    {
        var e = LodgeErrorMsg.Read(Written(new LodgeErrorMsg { Text = "Voice is full (5)", Code = "room-full", Room = "v1" }.Write));
        Assert.Equal(("Voice is full (5)", "room-full", "v1"), (e.Text, e.Code, e.Room));
        var bare = LodgeErrorMsg.Read(Written(new LodgeErrorMsg { Text = "Nothing to leave" }.Write));
        Assert.Equal(("Nothing to leave", (string)null, (string)null), (bare.Text, bare.Code, bare.Room));
        Assert.Equal("error", Written(new LodgeErrorMsg().Write).Str("t"));
        // lenient: a field of the wrong type is missing
        var odd = LodgeErrorMsg.Read(new TestFields(new Dictionary<string, object> { ["text"] = 5, ["code"] = true }));
        Assert.Equal(((string)null, (string)null), (odd.Text, odd.Code));
    }

    [Fact]
    public void Golden_matches_the_old_hub_Error()
    {
        // LodgeHub.Error: { t, text, code?, room? }
        SameJson(new JsonObject { ["t"] = "error", ["text"] = "Slow down a little" }, Written(new LodgeErrorMsg { Text = "Slow down a little" }.Write).D);
        SameJson(new JsonObject { ["t"] = "error", ["text"] = "Slow down a little", ["code"] = "rate" }, Written(new LodgeErrorMsg { Text = "Slow down a little", Code = "rate" }.Write).D);
        SameJson(new JsonObject { ["t"] = "error", ["text"] = "Voice is full (5)", ["code"] = "room-full", ["room"] = "v1" },
            Written(new LodgeErrorMsg { Text = "Voice is full (5)", Code = "room-full", Room = "v1" }.Write).D);
        Assert.Equal(new[] { "t", "text", "code", "room" }, Written(new LodgeErrorMsg { Code = "x", Room = "y" }.Write).D.Keys);
    }

    static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "shared")) && Directory.Exists(Path.Combine(d.FullName, "server"))) return d.FullName;
        throw new DirectoryNotFoundException("no folder above " + AppContext.BaseDirectory + " holds both shared/ and server/");
    }

    // hub.Error(me, "text", "code"[, room]) with a literal code (every code is a literal today; one passed in a variable would be missed here)
    static readonly Regex CodeLiteral = new(@"\.Error\((?:[^;""]|""(?:[^""\\]|\\.)*"")*?,\s*""([a-z0-9-]+)""\s*(?:,[^;]*)?\)\s*;");
    static readonly Regex AnyErrorCall = new(@"\bhub\.Error\(|\bawait Error\(|\bthis\.Error\(");

    [Fact]
    public void Every_code_the_server_sends_is_listed_and_every_listed_code_is_sent()
    {
        var root = RepoRoot();
        var used = new HashSet<string>();
        int calls = 0;
        foreach (var path in Directory.EnumerateFiles(Path.Combine(root, "server", "Lodge.Server"), "*.cs", SearchOption.AllDirectories))
        {
            if (Path.GetRelativePath(root, path).Split('/', '\\').Any(s => s is "obj" or "bin")) continue;
            var text = File.ReadAllText(path);
            calls += AnyErrorCall.Matches(text).Count;
            foreach (Match m in CodeLiteral.Matches(text)) used.Add(m.Groups[1].Value);
        }
        Assert.True(calls > 50, "only " + calls + " error calls found - is the scan looking at the right files?");
        var unlisted = used.Except(LodgeErrorMsg.Codes).ToList();
        Assert.True(unlisted.Count == 0, "error codes the server sends that LodgeErrorMsg.Codes doesn't list: " + string.Join(", ", unlisted));
        var unused = LodgeErrorMsg.Codes.Except(used).ToList();
        Assert.True(unused.Count == 0, "listed error codes the server never sends: " + string.Join(", ", unused));
        Assert.Equal(LodgeErrorMsg.Codes.Length, LodgeErrorMsg.Codes.Distinct().Count());
    }
}
