using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Elans.Shared;
using Elans.Shared.Tests.Frozen;
using Xunit.Abstractions;

namespace Elans.Shared.Tests;

// Old vs new reading of realistic frames (parse + typed Read): the old Outpost (hand-written reader + LodgeFieldsDict), the old server
// (JsonNode.Parse of the decoded text + LodgeFieldsJson) and the shared engine (LodgeJson.Parse with its checks + LodgeJsonFields).
// The numbers go to the test output (set ELANS_PERF=1, then dotnet test -c Release --logger "console;verbosity=detailed" --filter
// LodgeJsonPerfTests); what is
// asserted is only what must hold: the realistic welcome is far below the frame cap, and every side reads the same thing.
public class LodgeJsonPerfTests
{
    readonly ITestOutputHelper output;
    public LodgeJsonPerfTests(ITestOutputHelper output) { this.output = output; }

    static string Q(string s) => LodgeJson.WritePlain(s);

    static string Msg(string ch, int i) =>
        "{\"t\":\"msg\",\"channel\":" + Q(ch) + ",\"id\":\"m" + ch + i + "\",\"at\":" + (1760900000000L + i * 60000) + ",\"from\":\"Member" + (i % 20) + "\",\"fromId\":" + (i % 20 + 1)
        + ",\"text\":" + Q("Message " + i + " about the raid on Thursday - bring flasks, food and énough patience \U0001F600") + ",\"kind\":\"text\""
        + (i % 5 == 0 ? ",\"reactions\":{\"heart\":[\"Member1\",\"Member2\"],\"lol\":[\"Member3\"]}" : "")
        + (i % 7 == 0 ? ",\"replyTo\":{\"id\":\"m" + ch + (i - 1) + "\",\"by\":\"Member2\",\"text\":\"the message before\"}" : "")
        + (i % 11 == 0 ? ",\"edited\":true" : "") + "}";

    static string User(int id) =>
        "{\"id\":" + id + ",\"name\":\"Member" + id + "\",\"guest\":false,\"role\":\"member\",\"room\":" + (id % 4 == 0 ? "\"v1\"" : "null") + ",\"voice\":" + (id % 4 == 0 ? "true" : "false")
        + ",\"muted\":false,\"deaf\":false,\"serverMuted\":false,\"avatarId\":\"a" + id % 9 + "\",\"accent\":\"#c08040\",\"displayName\":\"Member " + id + "\",\"status\":\"online\",\"note\":\"\""
        + ",\"game\":{\"playing\":true,\"name\":\"Char" + id + "\",\"realm\":\"Realm\",\"class\":\"Hunter\",\"classFile\":\"HUNTER\",\"level\":60,\"guild\":\"G\",\"race\":\"Orc\",\"raceFile\":\"Orc\",\"sex\":2,\"zone\":\"Orgrimmar\",\"flags\":1,\"groupSize\":5,\"inInstance\":false,\"xpPct\":41,\"rested\":true}}";

    // the welcome the size estimate used: 3 text channels of 100 messages, 5 pins, 20 members, 8 events, 60 names, 40 features
    public static string Welcome()
    {
        var sb = new StringBuilder("{\"t\":\"welcome\",\"you\":1,\"name\":\"Member1\",\"role\":\"member\",\"server\":\"2.31.1\",\"maxFileMb\":25,\"canShareFiles\":true,\"canModerate\":false,\"canManage\":false,\"canPin\":false,");
        sb.Append("\"features\":[").Append(string.Join(",", Enumerable.Range(0, 40).Select(i => Q("feature" + i)))).Append("],");
        sb.Append("\"lodge\":{\"name\":\"The Silver Lodge\",\"welcome\":\"Welcome in. Be kind.\"},");
        sb.Append("\"calendar\":{\"now\":1760900000000,\"events\":[").Append(string.Join(",", Enumerable.Range(0, 8).Select(i =>
            "{\"id\":\"e" + i + "\",\"title\":\"Raid " + i + "\",\"start\":" + (1761000000000L + i * 86400000L) + ",\"end\":" + (1761007200000L + i * 86400000L)
            + ",\"by\":\"Member1\",\"status\":\"open\",\"roles\":{\"tank\":2,\"healer\":3,\"dps\":10},\"signups\":[{\"name\":\"Member2\",\"role\":\"tank\"},{\"name\":\"Member3\",\"role\":\"healer\"}]}"))).Append("]},");
        sb.Append("\"reactions\":[\"ready\",\"notready\",\"lol\",\"love\",\"fight\",\"loot\",\"wipe\",\"epic\"],");
        sb.Append("\"pins\":{\"c1\":[").Append(string.Join(",", Enumerable.Range(0, 5).Select(i => "{\"id\":\"mc1" + i + "\",\"text\":\"Pinned " + i + "\",\"by\":\"Member1\",\"at\":1760900000000,\"pinnedBy\":\"Member2\",\"pinnedAt\":1760900100000}"))).Append("]},");
        sb.Append("\"channels\":[").Append(string.Join(",", new[] { "c1", "c2", "c3" }.Select(c => "{\"id\":\"" + c + "\",\"name\":\"" + c + "\",\"type\":\"text\",\"minRole\":\"guest\"}"))).Append(",{\"id\":\"v1\",\"name\":\"Voice\",\"type\":\"voice\",\"minRole\":\"member\",\"max\":10}],");
        sb.Append("\"users\":[").Append(string.Join(",", Enumerable.Range(1, 20).Select(User))).Append("],");
        sb.Append("\"names\":{").Append(string.Join(",", Enumerable.Range(1, 60).Select(i => "\"member" + i + "\":\"Member " + i + "\""))).Append("},");
        sb.Append("\"history\":{").Append(string.Join(",", new[] { "c1", "c2", "c3" }.Select(c => "\"" + c + "\":[" + string.Join(",", Enumerable.Range(1, 100).Select(i => Msg(c, i))) + "]"))).Append("}}");
        return sb.ToString();
    }

    public static string HistoryPage() =>
        "{\"t\":\"history:page\",\"channel\":\"c1\",\"before\":\"mc1101\",\"hasMore\":true,\"messages\":[" + string.Join(",", Enumerable.Range(1, 100).Select(i => Msg("c1", i))) + "]}";

    static readonly (string Name, Func<byte[], ILodgeFields> Read)[] Sides =
    {
        ("old Outpost", b => OldFrames.Outpost(b) is Dictionary<string, object> d ? new OldOutpostFields(d) : null),
        ("old server", b => OldFrames.Server(b) is JsonObject o ? new OldServerFields(o) : null),
        ("shared engine", b => LodgeJson.Parse(b) is JsonObject o ? new LodgeJsonFields(o) : null),
    };

    double Time(int n, Action a)
    {
        for (int i = 0; i < Math.Min(n, 50); i++) a();                              // warm up
        var best = double.MaxValue;
        for (int round = 0; round < 5; round++)
        {
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < n; i++) a();
            best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
        }
        return best;
    }

    [Fact]
    public void Realistic_frames_old_vs_new()
    {
        var welcome = Encoding.UTF8.GetBytes(Welcome());
        var page = Encoding.UTF8.GetBytes(HistoryPage());
        var status = Encoding.UTF8.GetBytes("{\"t\":\"status\",\"status\":\"busy\",\"note\":\"in a raid\",\"visibility\":\"visible\"}");
        var state = Encoding.UTF8.GetBytes("{\"t\":\"state\",\"muted\":true,\"deaf\":false}");
        var user = Encoding.UTF8.GetBytes("{\"t\":\"user\",\"user\":" + User(7) + "}");
        output.WriteLine("welcome: " + welcome.Length + " bytes (cap " + LodgeJson.MaxFrameFromLodge + "), history page: " + page.Length + " bytes");
        Assert.True(welcome.Length < LodgeJson.MaxFrameFromLodge / 8, "the realistic welcome is " + welcome.Length + " bytes");

        foreach (var (name, read) in Sides)
        {
            // every side reads the same welcome
            var w = WelcomeMsg.Read(read(welcome));
            Assert.Equal(3, w.History.Count); Assert.Equal(300, w.History.Sum(h => h.Items.Count)); Assert.Equal(20, w.Users.Count); Assert.Equal(40, w.Features.Count);
            Assert.Equal(8, w.Calendar.Events.Count);
            Assert.Equal(100, HistoryPageMsg.Read(read(page)).Items.Count);

            if (Environment.GetEnvironmentVariable("ELANS_PERF") != "1") continue;   // the timing only on request (it takes seconds)
            double tw = Time(200, () => WelcomeMsg.Read(read(welcome))) / 200;
            double tp = Time(1000, () => HistoryPageMsg.Read(read(page))) / 1000;
            double ths = Time(50_000, () => StatusMsg.Read(read(status))) + Time(50_000, () => VoiceStateMsg.Read(read(state)));
            double thu = Time(100_000, () => UserMsg.Read(read(user)));
            output.WriteLine($"{name,-14} welcome {tw:0.000} ms   history page {tp:0.000} ms   100k status/state {ths:0} ms   100k user {thu:0} ms");
        }
    }
}
