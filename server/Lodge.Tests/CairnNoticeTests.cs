using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Lodge.Tests;

// 2.31: notices from the Cairn (feature "cairnnotices") and the Lodge's security log. A fake Cairn here - no network.
public class CairnNoticeTests
{
    const string Key = "cairn_0a1b2c3d_synthetic0key0for0tests0only0000000000000";
    const string Url = "https://cairn.example.test/cairn";

    sealed class FakeCairn : HttpMessageHandler
    {
        public JsonArray Notices = new();
        public readonly List<string> Seen = new();
        public int StatusCalls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/v1/status"))
            {
                Interlocked.Increment(ref StatusCalls);
                var o = new JsonObject { ["ok"] = true, ["epoch"] = "e1", ["lastSeq"] = 0, ["standing"] = "good", ["notices"] = Notices.DeepClone() };
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(o.ToJsonString(), Encoding.UTF8, "application/json") };
            }
            if (path.EndsWith("/v1/notices/seen"))
            {
                var b = await req.Content!.ReadAsStringAsync(ct); lock (Seen) Seen.Add(b);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true,\"marked\":1}", Encoding.UTF8, "application/json") };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    static JsonObject N(long id, string level, string text, long expires = 1_900_000_000, string sig = "c2lnbmF0dXJl", string kid = "17140e4dd7ef6d82") => new()
    {
        ["id"] = id, ["created"] = 1_790_000_000, ["expires"] = expires, ["level"] = level, ["to"] = "*", ["text"] = text, ["sig"] = sig, ["kid"] = kid,
    };

    sealed class Rig : IDisposable
    {
        public readonly FunRig Fun;
        public readonly FakeCairn Cairn = new();
        public readonly CaptureLogger Log = new();
        public Rig()
        {
            Fun = new FunRig(c => { c.CairnEnabled = true; c.CairnUrl = Url; c.CairnKey = Key; }, Log);
            Fun.Hub.Cairn.UseHandlerForTests(Cairn);
        }
        public void Check() => Fun.Hub.Cairn.CheckNotices(CancellationToken.None).GetAwaiter().GetResult();
        public Task Do(Member m, object o)
        {
            var j = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(o))!.AsObject();
            return Fun.Hub.CairnIn.Handle(m, (string)j["t"]!, j);
        }
        public void Dispose() => Fun.Dispose();
    }

    [Fact]
    public void Officers_get_every_notice_members_only_critical_guests_none()
    {
        using var r = new Rig();
        Assert.Contains("cairnnotices", r.Fun.Hub.FeatureList());
        var gm = r.Fun.Join("Elan", "owner");
        var off = r.Fun.Join("Bob", "officer");
        var mem = r.Fun.Join("Mia", "member");
        var guest = r.Fun.Join("Rover", "guest");
        r.Cairn.Notices = new JsonArray(N(1, "info", "Maintenance tonight"), N(2, "critical", "Update your Lodge now - security fix"));
        r.Check();
        var g = r.Fun.Sock("Elan").Of("cairn.notices").Last()["list"]!.AsArray();
        Assert.Equal(2, g.Count);
        Assert.Equal("c2lnbmF0dXJl", (string)g[0]!["sig"]!);                      // passed on unchanged, signature and all
        Assert.Equal(2, r.Fun.Sock("Bob").Of("cairn.notices").Last()["list"]!.AsArray().Count);
        var m = r.Fun.Sock("Mia").Of("cairn.notices").Last()["list"]!.AsArray();
        Assert.Single(m);
        Assert.Equal(2, (long)m[0]!["id"]!);
        Assert.Empty(r.Fun.Sock("Rover").Of("cairn.notices"));
        Assert.Contains(r.Log.Lines, l => l.Contains("Cairn notice #2 (critical)"));
        // the same list again: nothing new is sent
        int before = r.Fun.Sock("Elan").Of("cairn.notices").Count;
        r.Check();
        Assert.Equal(before, r.Fun.Sock("Elan").Of("cairn.notices").Count);
        // withdrawn at the Cairn: gone for everybody
        r.Cairn.Notices = new JsonArray();
        r.Check();
        Assert.Empty(r.Fun.Sock("Elan").Of("cairn.notices").Last()["list"]!.AsArray());
    }

    [Fact]
    public async Task Asking_gets_the_list_and_odd_entries_are_dropped()
    {
        using var r = new Rig();
        var gm = r.Fun.Join("Elan", "owner");
        r.Cairn.Notices = new JsonArray(N(1, "info", "ok"), N(2, "panic", "bad level"), N(3, "info", new string('x', 501)), N(4, "info", "expired", expires: 1_000),
            new JsonObject { ["id"] = "nope" }, N(5, "warning", "no signature", sig: ""));
        r.Check();
        await r.Do(gm, new { t = "cairn.notices.get" });
        var list = r.Fun.Sock("Elan").Of("cairn.notices").Last()["list"]!.AsArray();
        Assert.Single(list);
        Assert.Equal(1, (long)list[0]!["id"]!);
        // kept across a restart of the Lodge
        var again = new CairnNotices(r.Fun.Hub.Cfg.DataDir, r.Log);
        Assert.Single(again.Current(1_800_000_000));
    }

    [Fact]
    public async Task An_officers_look_is_reported_to_the_Cairn_once_a_members_is_not()
    {
        using var r = new Rig();
        var gm = r.Fun.Join("Elan", "owner");
        var mem = r.Fun.Join("Mia", "member");
        r.Cairn.Notices = new JsonArray(N(7, "critical", "Update now"));
        r.Check();
        await r.Do(mem, new { t = "cairn.notice.seen", ids = new[] { 7 } });
        Assert.Empty(r.Cairn.Seen);                                                   // a member's look is not "seen by the Lodge"
        await r.Do(gm, new { t = "cairn.notice.seen", ids = new[] { 7, 99 } });       // 99: not a notice the Cairn sent us
        Assert.Single(r.Cairn.Seen);
        Assert.Contains("[7]", r.Cairn.Seen[0].Replace(" ", ""));
        await r.Do(gm, new { t = "cairn.notice.seen", ids = new[] { 7 } });
        Assert.Single(r.Cairn.Seen);                                                  // told once
    }

    [Fact]
    public void The_security_log_writes_one_line_then_sums_up_and_never_the_code()
    {
        var log = new CaptureLogger();
        var clock = new FakeClock();
        var s = new LodgeSecurity(log, clock);
        for (int i = 0; i < 5; i++) s.Note("wrong-code", "203.0.113.5");
        s.Note("lockout", "203.0.113.5", "15 minutes after too many wrong invite codes");
        Assert.Single(log.Lines, l => l.Contains("security: wrong-code from 203.0.113.5"));
        clock.Advance(TimeSpan.FromMinutes(2));
        s.Tick();
        Assert.Contains(log.Lines, l => l.Contains("4 more wrong-code events from 1 addresses"));
        var today = s.Today();
        Assert.Equal(5, (long)today["counts"]!["wrong-code"]!);
        Assert.DoesNotContain("203.0.113.5", today.ToJsonString());                   // the admin stats never carry an address
        clock.Advance(TimeSpan.FromDays(1));
        s.Tick();
        Assert.Contains(log.Lines, l => l.Contains("lockout 1") && l.Contains("wrong-code 5"));
    }

    [Fact]
    public void Wrong_invite_codes_reach_the_security_log()
    {
        var log = new CaptureLogger();
        using var fun = new FunRig(null, log);
        for (int i = 0; i < 12; i++) fun.Hub.Auth.ExchangeCode("198.51.100.7", "wrong-code-" + i, "PC", null);
        Assert.Contains(log.Lines, l => l.Contains("security: wrong-code from 198.51.100.7"));
        Assert.Contains(log.Lines, l => l.Contains("security: lockout from 198.51.100.7"));
        Assert.Contains(log.Lines, l => l.Contains("security: blocked from 198.51.100.7"));
        Assert.DoesNotContain(log.Lines, l => l.Contains("wrong-code-1"));            // never the code itself
    }
}
