using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Lodge.Tests;

// Operator stats (GET /admin/stats, `lodge-admin stats`) and the Cairn's "connected outposts" weekly numbers.
public class OpsStatsTests
{
    const string Token = "synthetic-admin-token-0123456789abcdef";
    const string Key = "cairn_0a1b2c3d_synthetic0key0for0tests0only0000000000000";
    const string Url = "https://cairn.example.test/cairn";

    // ---------------------------------------------------------------- the gate

    [Fact]
    public void Admin_needs_a_long_token_a_direct_loopback_connection_and_no_proxy_header()
    {
        var lo = IPAddress.Loopback;
        Assert.True(LodgeAdminApi.Allowed(Token, lo, false, Token));
        Assert.True(LodgeAdminApi.Allowed(Token, IPAddress.IPv6Loopback, false, Token));
        Assert.False(LodgeAdminApi.Allowed("", lo, false, ""));                          // off by default
        Assert.False(LodgeAdminApi.Allowed("short-token", lo, false, "short-token"));    // < 24 characters: off
        Assert.False(LodgeAdminApi.Allowed(Token, lo, false, Token + "x"));
        Assert.False(LodgeAdminApi.Allowed(Token, lo, false, ""));
        Assert.False(LodgeAdminApi.Allowed(Token, lo, false, null!));
        Assert.False(LodgeAdminApi.Allowed(Token, lo, true, Token));                     // came through a reverse proxy
        Assert.False(LodgeAdminApi.Allowed(Token, IPAddress.Parse("203.0.113.5"), false, Token));
        Assert.False(LodgeAdminApi.Allowed(Token, null!, false, Token));

        var h = new Microsoft.AspNetCore.Http.HeaderDictionary();
        Assert.False(LodgeAdminApi.HasProxyHeaders(h));
        h["X-Real-IP"] = "203.0.113.9";
        Assert.True(LodgeAdminApi.HasProxyHeaders(h));
    }

    // ---------------------------------------------------------------- the numbers

    [Fact]
    public void Stats_are_counts_only()
    {
        using var r = new FunRig();
        r.Join("Elan", "owner");
        r.Join("Guesty", "guest");
        var o = LodgeAdminApi.Stats(r.Hub);
        Assert.Equal(LodgeHub.Version, (string)o["server"]!);
        Assert.Equal(2, (int)o["members"]!["total"]!);
        Assert.Equal(1, (int)o["members"]!["roles"]!["owner"]!);
        Assert.Equal(1, (int)o["members"]!["roles"]!["officer"]!);
        Assert.Equal(1, (int)o["members"]!["online"]!);
        Assert.Equal(1, (int)o["members"]!["guestsOnline"]!);
        Assert.Equal(1, (int)o["members"]!["active7d"]!);          // Elan is online; Bob has no device seen and is offline
        Assert.True((int)o["messages"]!["channels"]! >= 1);
        Assert.Equal("off", (string)o["cairn"]!["standing"]!);
        Assert.False((bool)o["cairn"]!["enabled"]!);
        var text = o.ToJsonString();
        foreach (var secret in new[] { "Elan", "Bob", "Guesty", T.ElanCode, T.BobCode, "127.0.0.1" }) Assert.DoesNotContain(secret, text);
    }

    // ---------------------------------------------------------------- connected outposts: the weekly numbers

    [Fact]
    public void Weekly_counts_distinct_members_and_keeps_only_numbers_of_a_finished_week()
    {
        using var dir = new TempDir();
        var clock = new FakeClock { UtcNow = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc) };   // Wednesday of 2026-W42
        var w = new CairnWeekly(dir.Path, clock, T.Log);
        w.NoteSharing("elan"); w.NoteSharing("elan"); w.NoteSharing("bob");
        w.NoteReceived("elan"); w.NoteReceived("elan");
        w.NoteReceived("carl");                                       // receiving implies sharing
        var rep = w.Report();
        Assert.Single(rep);
        Assert.Equal("2026-W42", (string)rep[0]!["w"]!);
        Assert.Equal((3, 2), ((int)rep[0]!["s"]!, (int)rep[0]!["r"]!));

        var file = File.ReadAllText(Path.Combine(dir.Path, "cairn-week.json"));
        foreach (var name in new[] { "elan", "bob", "carl" }) Assert.DoesNotContain(name, file);   // salted hashes only

        // reloaded: the same people are not counted twice
        var again = new CairnWeekly(dir.Path, clock, T.Log);
        again.NoteSharing("bob");
        Assert.Equal(3, (int)again.Report()[0]!["s"]!);

        // a purge takes them out
        again.Forget("carl");
        Assert.Equal((2, 1), ((int)again.Report()[0]!["s"]!, (int)again.Report()[0]!["r"]!));

        // next week: last week is reported once more as numbers, the hashes and the salt are gone
        clock.Advance(TimeSpan.FromDays(7));
        again.NoteSharing("bob");
        var next = again.Report();
        Assert.Equal(2, next.Count);
        Assert.Equal(("2026-W42", 2, 1), ((string)next[0]!["w"]!, (int)next[0]!["s"]!, (int)next[0]!["r"]!));
        Assert.Equal(("2026-W43", 1, 0), ((string)next[1]!["w"]!, (int)next[1]!["s"]!, (int)next[1]!["r"]!));
        Assert.DoesNotContain(JsonNode.Parse(file)!["salt"]!.ToString(), File.ReadAllText(Path.Combine(dir.Path, "cairn-week.json")));

        // two weeks later: nothing of 2026-W42 is sent any more
        clock.Advance(TimeSpan.FromDays(14));
        var later = again.Report();
        Assert.Single(later);
        Assert.Equal("2026-W45", (string)later[0]!["w"]!);
        Assert.Equal(0, (int)later[0]!["s"]!);
    }

    [Fact]
    public void Weeks_are_iso_weeks()
    {
        Assert.Equal("2026-W53", CairnWeekly.WeekOf(new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        Assert.Equal("2026-W01", CairnWeekly.WeekOf(new DateTime(2025, 12, 29, 0, 0, 0, DateTimeKind.Utc)));
    }

    // a Cairn that may or may not understand "outposts"
    sealed class Fake : HttpMessageHandler
    {
        public bool Understands = true;
        public readonly List<JsonObject> Bodies = new();
        long lastSeq;
        static HttpResponseMessage J(JsonObject o) => new(HttpStatusCode.OK) { Content = new StringContent(o.ToJsonString(), Encoding.UTF8, "application/json") };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/v1/status"))
            {
                var o = new JsonObject { ["ok"] = true, ["epoch"] = "e1", ["lastSeq"] = lastSeq, ["standing"] = "good" };
                if (Understands) o["features"] = new JsonArray("outposts");
                return J(o);
            }
            if (path.EndsWith("/v1/contribute"))
            {
                var raw = await req.Content!.ReadAsByteArrayAsync(ct);
                using var ms = new MemoryStream();
                using (var g = new GZipStream(new MemoryStream(raw), CompressionMode.Decompress)) g.CopyTo(ms);
                var body = JsonNode.Parse(ms.ToArray())!.AsObject();
                lock (Bodies) Bodies.Add(body);
                lastSeq = (long)body["seq"]!;
                return J(new JsonObject { ["ok"] = true, ["seq"] = lastSeq, ["accepted"] = 0, ["rejected"] = 0, ["epoch"] = "e1", ["standing"] = "good" });
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    [Fact]
    public void The_numbers_ride_along_only_when_the_Cairn_understands_them_and_only_when_they_changed()
    {
        var fake = new Fake { Understands = false };
        using var r = new FunRig(c => { c.CairnEnabled = true; c.CairnUrl = Url; c.CairnKey = Key; });
        var c = new CairnContributor(r.Hub.Cfg, r.Hub.AtlasPool.Store, r.Hub.Flight.Store, T.Log, r.Clock, fake);
        var weekly = r.Hub.CairnIn.Weekly;
        c.Outposts = () => weekly.Report();
        weekly.NoteSharing("elan");

        // an older Cairn: never sent (it would refuse the unknown field), and with no records nothing is posted at all
        Assert.Equal("nothing-new", c.RunOnce(CancellationToken.None).GetAwaiter().GetResult().Stopped);
        Assert.Empty(fake.Bodies);

        // a Cairn that lists the feature: one post with only the numbers (no records changed)
        fake.Understands = true;
        var res = c.RunOnce(CancellationToken.None).GetAwaiter().GetResult();
        Assert.True(res.OutpostsSent);
        var body = Assert.Single(fake.Bodies);
        var week = CairnWeekly.WeekOf(r.Clock.UtcNow);
        Assert.Equal(week, (string)body["outposts"]![0]!["w"]!);
        Assert.Equal(1, (int)body["outposts"]![0]!["s"]!);
        Assert.Null(body["drops"]);
        Assert.DoesNotContain("elan", body.ToJsonString(), StringComparison.OrdinalIgnoreCase);

        // unchanged: nothing again; changed: sent again
        Assert.Equal("nothing-new", c.RunOnce(CancellationToken.None).GetAwaiter().GetResult().Stopped);
        Assert.Single(fake.Bodies);
        weekly.NoteReceived("bob");
        Assert.True(c.RunOnce(CancellationToken.None).GetAwaiter().GetResult().OutpostsSent);
        Assert.Equal((2, 1), ((int)fake.Bodies[1]["outposts"]![0]!["s"]!, (int)fake.Bodies[1]["outposts"]![0]!["r"]!));
        Assert.True((long)fake.Bodies[1]["seq"]! > (long)fake.Bodies[0]["seq"]!);
    }

    [Fact]
    public void The_hub_wires_the_numbers_into_its_contributor_and_a_purge_forgets_the_member()
    {
        using var r = new FunRig(c => { c.CairnEnabled = true; c.CairnUrl = Url; c.CairnKey = Key; });
        Assert.NotNull(r.Hub.Cairn.Outposts);
        r.Hub.CairnIn.Weekly.NoteSharing(ProfileRules.Key("Bob"));
        Assert.Equal(1, (int)r.Hub.Cairn.Outposts!()[0]!["s"]!);
        r.Hub.PurgeMember("Bob").GetAwaiter().GetResult();
        Assert.Equal(0, (int)r.Hub.Cairn.Outposts!()[0]!["s"]!);
    }
}
