using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Lodge.Tests;

// Give to get (feature "cairn2"): a contributing Lodge downloads the Cairn's dataset with its key and hands it ONLY to members whose Atlas sharing
// is on; guests never; when the Lodge's own standing at the Cairn lapses it stops serving and drops its copy. A fake Cairn here - no network.
public class CairnReceiverTests
{
    const string Key = "cairn_0a1b2c3d_synthetic0key0for0tests0only0000000000000";
    const string Url = "https://cairn.example.test/cairn";

    static string Hex(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    static byte[] Gz(byte[] b)
    {
        using var ms = new MemoryStream();
        using (var z = new GZipStream(ms, CompressionLevel.Optimal, true)) z.Write(b);
        return ms.ToArray();
    }

    // a Cairn with standing: status / contribute say the standing; manifest / datasets answer only "good"
    sealed class FakeCairn : HttpMessageHandler
    {
        public string Standing = "good";             // good, new, lapsed, revoked
        public byte[] Gz = Array.Empty<byte>();
        public string File = "", Day = "2026-10-17", ContentSha = "", Sha = "", ETag = "\"m1\"";
        public long Seq;
        public bool CorruptFile;
        public bool NoDataset;
        public readonly List<string> Paths = new();
        public readonly List<string> IfNoneMatch = new();
        public readonly List<string> Auth = new();
        public long LastSeq;

        public void Publish(long seq, string json)
        {
            var raw = Encoding.UTF8.GetBytes(json);
            Seq = seq; Gz = CairnReceiverTests.Gz(raw); ContentSha = Hex(raw); Sha = Hex(Gz);
            File = "cairn-" + Day + "-" + seq + ".json.gz"; ETag = "\"m" + seq + "\"";
        }

        static HttpResponseMessage J(HttpStatusCode c, JsonObject o) => new(c) { Content = new StringContent(o.ToJsonString(), Encoding.UTF8, "application/json") };

        HttpResponseMessage Refusal()
        {
            if (Standing == "revoked") return J(HttpStatusCode.Unauthorized, new JsonObject { ["ok"] = false, ["reason"] = "revoked" });
            return J(HttpStatusCode.Forbidden, new JsonObject { ["ok"] = false, ["reason"] = "contribute", ["standing"] = Standing, ["error"] = "Contribute to receive" });
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            lock (Paths) { Paths.Add(path); Auth.Add(req.Headers.Authorization?.ToString() ?? ""); IfNoneMatch.Add(req.Headers.IfNoneMatch.ToString()); }
            if (path.EndsWith("/v1/status"))
            {
                if (Standing == "revoked") return Refusal();
                return J(HttpStatusCode.OK, new JsonObject { ["ok"] = true, ["epoch"] = "e1", ["lastSeq"] = LastSeq, ["standing"] = Standing, ["receiveUntil"] = Standing == "good" ? 1_800_000_000L : null });
            }
            if (path.EndsWith("/v1/contribute"))
            {
                if (Standing == "revoked") return Refusal();
                var raw = await req.Content!.ReadAsByteArrayAsync(ct);
                using var ms = new MemoryStream();
                using (var g = new GZipStream(new MemoryStream(raw), CompressionMode.Decompress)) g.CopyTo(ms);
                LastSeq = (long)JsonNode.Parse(ms.ToArray())!["seq"]!;
                Standing = "good";                    // an accepted contribution puts the Lodge (back) in good standing
                return J(HttpStatusCode.OK, new JsonObject { ["ok"] = true, ["seq"] = LastSeq, ["accepted"] = 1, ["rejected"] = 0, ["epoch"] = "e1", ["contribution"] = true, ["standing"] = "good", ["receiveUntil"] = 1_800_000_000L });
            }
            if (Standing != "good") return Refusal();
            if (path.EndsWith("/v1/manifest.json"))
            {
                if (NoDataset) return J(HttpStatusCode.NotFound, new JsonObject { ["ok"] = false, ["reason"] = "none" });
                if (req.Headers.IfNoneMatch.ToString() == ETag) return new HttpResponseMessage(HttpStatusCode.NotModified);
                var m = new JsonObject
                {
                    ["format"] = "elans-cairn-manifest", ["v"] = 1, ["license"] = "contributors", ["kid"] = "k1",
                    ["latest"] = new JsonObject { ["day"] = Day, ["seq"] = Seq, ["file"] = File, ["bytes"] = Gz.Length, ["contentBytes"] = 10, ["sha256"] = Sha, ["contentSha256"] = ContentSha, ["sig"] = "c2ln", ["kid"] = "k1", ["built"] = 1 },
                };
                var r = J(HttpStatusCode.OK, m);
                r.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(ETag);
                return r;
            }
            if (path.EndsWith("/v1/datasets/" + File))
            {
                var data = (byte[])Gz.Clone();
                if (CorruptFile) data[data.Length / 2] ^= 0x55;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    const string DatasetJson = "{\"format\":\"elans-cairn\",\"v\":1,\"license\":\"contributors\",\"day\":\"2026-10-17\",\"seq\":1,\"lodges\":2,\"drops\":{},\"vendors\":{},\"spawns\":{},\"nodes\":{},\"flights\":{}}";

    sealed class Rig : IDisposable
    {
        public readonly FunRig Fun;
        public readonly FakeCairn Cairn = new();
        public readonly CaptureLogger Log = new();
        public readonly CairnContributor Contributor;
        public CairnReceiver Rx => Fun.Hub.CairnIn.Receiver;
        public CairnModule Mod => Fun.Hub.CairnIn;

        public Rig(bool contributing = true)
        {
            Fun = new FunRig(c => { if (contributing) { c.CairnEnabled = true; c.CairnUrl = Url; c.CairnKey = Key; } }, Log);
            Rx.UseHandlerForTests(Cairn);
            // the hub's contributor posts through the network; this one shares the hub's stores and the fake, and feeds the same receiver
            Contributor = new CairnContributor(Fun.Hub.Cfg, Fun.Hub.AtlasPool.Store, Fun.Hub.Flight.Store, Log, Fun.Clock, Cairn);
            Contributor.Standing += Rx.AfterContribution;
            Cairn.Publish(1, DatasetJson);
        }

        public string Fetch() => Rx.RunOnce(CancellationToken.None).GetAwaiter().GetResult();

        public void Share(string who, string json) { lock (Fun.Hub.AtlasPool.Store.Gate) Fun.Hub.AtlasPool.Store.Share(who.ToLowerInvariant(), who, JsonNode.Parse(json)!.AsObject()); }
        public CairnContributor.RunResult Contribute() => Contributor.RunOnce(CancellationToken.None).GetAwaiter().GetResult();

        public Task Do(Member m, object o)
        {
            var j = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(o))!.AsObject();
            return Mod.Handle(m, (string)j["t"]!, j);
        }

        public JsonObject LastStatus(string name) => Fun.Sock(name).Of("cairn.status").Last();

        public void Dispose() => Fun.Dispose();
    }

    // ---------------------------------------------------------------- the Lodge <- Cairn

    [Fact]
    public void Off_unless_the_Lodge_contributes_and_the_feature_follows()
    {
        using var off = new Rig(contributing: false);
        Assert.False(off.Rx.Enabled);
        Assert.DoesNotContain("cairn2", off.Fun.Hub.FeatureList());
        Assert.Equal("disabled", off.Fetch());
        Assert.Empty(off.Cairn.Paths);                                         // nothing is ever asked
        var m = off.Fun.Join("Mia", "member");
        Assert.Equal("disabled", off.Mod.Check("Mia", true, "member")!.Value.reason);

        using var on = new Rig();
        Assert.Contains("cairn2", on.Fun.Hub.FeatureList());
        Assert.Contains("cairn", on.Fun.Hub.FeatureList());
        Assert.Equal(6, on.Fun.Hub.Cfg.CairnFetchHours);
    }

    [Fact]
    public void Downloads_with_the_key_checks_both_checksums_and_uses_the_ETag()
    {
        using var r = new Rig();
        Assert.Equal("updated", r.Fetch());
        Assert.Equal(new[] { "/cairn/v1/manifest.json", "/cairn/v1/datasets/" + r.Cairn.File }, r.Cairn.Paths);
        Assert.All(r.Cairn.Auth, a => Assert.Equal("Bearer " + Key, a));
        Assert.True(r.Rx.Serving);
        Assert.Equal(r.Cairn.Gz, File.ReadAllBytes(r.Rx.GzPath));
        Assert.Equal(1, r.Rx.Current!.Seq);
        Assert.Contains(r.Log.Lines, l => l.Contains("dataset 2026-10-17 #1 received"));
        Assert.DoesNotContain(r.Log.Lines, l => l.Contains("synthetic0key"));

        // nothing new: the manifest is asked with its ETag, no download
        r.Cairn.Paths.Clear();
        Assert.Equal("unchanged", r.Fetch());
        Assert.Equal("\"m1\"", r.Cairn.IfNoneMatch.Last());
        Assert.Single(r.Cairn.Paths);

        // a damaged file is refused and the copy on hand stays
        r.Cairn.Publish(2, DatasetJson.Replace("\"seq\":1", "\"seq\":2"));
        r.Cairn.CorruptFile = true;
        Assert.StartsWith("failed", r.Fetch());
        Assert.Equal(1, r.Rx.Current!.Seq);
        Assert.True(r.Rx.Serving);
        r.Cairn.CorruptFile = false;
        Assert.Equal("updated", r.Fetch());
        Assert.Equal(2, r.Rx.Current!.Seq);

        // the copy survives a restart of the Lodge
        var again = new CairnReceiver(r.Fun.Hub.Cfg, r.Contributor, r.Log, r.Fun.Clock);
        Assert.Equal(2, again.Current!.Seq);
        Assert.True(again.Serving);
    }

    [Fact]
    public void A_manifest_whose_content_checksum_does_not_match_is_refused()
    {
        using var r = new Rig();
        r.Cairn.ContentSha = new string('a', 64);                              // the file is fine, but it is not what the manifest says
        Assert.StartsWith("failed", r.Fetch());
        Assert.Null(r.Rx.Current);
        Assert.False(r.Rx.Serving);
    }

    [Fact]
    public void Lapsed_standing_stops_serving_drops_the_copy_and_says_why_then_the_next_contribution_brings_it_back()
    {
        using var r = new Rig();
        Assert.Equal("updated", r.Fetch());
        bool changed = false;
        r.Rx.Changed += () => changed = true;

        r.Cairn.Standing = "lapsed";
        Assert.Equal("refused: lapsed", r.Fetch());
        Assert.True(changed);
        Assert.False(r.Rx.Serving);
        Assert.Null(r.Rx.Current);
        Assert.False(File.Exists(r.Rx.GzPath));                                 // the data is only for a Lodge in good standing
        Assert.Contains(r.Log.Lines, l => l.Contains("not receiving the Cairn's data") && l.Contains("contribute to receive"));
        r.Mod.Sharing.Set(ProfileRules.Key("Mia"), true, 1);
        Assert.Equal("lodge", r.Mod.Check("Mia", true, "member")!.Value.reason);   // even a member who shares gets nothing now

        // the status of the next contribution run already says lapsed (nothing to post): no fetch is asked for
        r.Cairn.Paths.Clear();
        Assert.Equal("nothing-new", r.Contribute().Stopped);
        Assert.Equal("lapsed", r.Rx.Standing);

        // a new accepted contribution: good again, and the fetch is due at once
        r.Share("Samhain", @"{""kills"":{""448"":{""n"":12}}}");
        var res = r.Contribute();
        Assert.Equal(1, res.Posts);
        Assert.Equal("good", r.Rx.Standing);
        r.Rx.Tick();                                                            // the housekeeping tick starts the fetch
        for (int i = 0; i < 100 && !r.Rx.Serving; i++) Thread.Sleep(20);
        Assert.True(r.Rx.Serving);
    }

    [Fact]
    public void A_new_key_receives_nothing_and_a_revoked_key_drops_everything()
    {
        using var r = new Rig();
        r.Cairn.Standing = "new";
        Assert.Equal("refused: new", r.Fetch());
        Assert.False(r.Rx.Serving);
        Assert.Contains(r.Log.Lines, l => l.Contains("no accepted contribution yet"));

        r.Cairn.Standing = "good";
        Assert.Equal("updated", r.Fetch());
        r.Cairn.Standing = "revoked";
        Assert.Equal("revoked", r.Contribute().Standing);                      // the status run sees it
        Assert.False(r.Rx.Serving);
        Assert.False(File.Exists(r.Rx.GzPath));
        Assert.Equal("refused: revoked", r.Fetch());
    }

    [Fact]
    public void The_manifest_is_checked_at_most_every_few_hours()
    {
        using var r = new Rig();
        r.Fun.Clock.Advance(TimeSpan.FromMinutes(5));
        r.Rx.Tick();
        for (int i = 0; i < 100 && !r.Rx.Serving; i++) Thread.Sleep(20);
        Assert.True(r.Rx.Serving);
        int asked = r.Cairn.Paths.Count;
        r.Fun.Clock.Advance(TimeSpan.FromHours(1));
        r.Rx.Tick();
        Thread.Sleep(100);
        Assert.Equal(asked, r.Cairn.Paths.Count);                              // not due yet
        r.Fun.Clock.Advance(TimeSpan.FromHours(6));
        r.Rx.Tick();
        for (int i = 0; i < 100 && r.Cairn.Paths.Count == asked; i++) Thread.Sleep(20);
        Assert.Equal(asked + 1, r.Cairn.Paths.Count);                          // one manifest check (304), no download
    }

    // ---------------------------------------------------------------- the Lodge -> its members

    [Fact]
    public async Task Only_members_who_share_receive_guests_never()
    {
        using var r = new Rig();
        Assert.Equal("updated", r.Fetch());
        var mia = r.Fun.Join("Mia", "member");
        var guest = r.Fun.Join("Wanderer", "guest");

        // not said yet = not sharing
        Assert.Equal("share", r.Mod.Check("Mia", true, "member")!.Value.reason);
        await r.Do(mia, new { t = "cairn.status" });
        var st = r.LastStatus("Mia");
        Assert.False((bool)st["entitled"]!);
        Assert.Equal("share", (string)st["why"]!);
        Assert.Contains("Share to receive", (string)st["text"]!);

        await r.Do(mia, new { t = "atlas.sharing", on = true });
        st = r.LastStatus("Mia");
        Assert.True((bool)st["entitled"]!);
        Assert.Equal(1, (long)st["seq"]!);
        Assert.Null(st["lodge"]);                                               // only the owner sees the Lodge's standing
        Assert.Null(r.Mod.Check("Mia", true, "member"));

        // guests: never, whatever they say
        await r.Do(guest, new { t = "atlas.sharing", on = true });
        st = r.LastStatus("Wanderer");
        Assert.False((bool)st["entitled"]!);
        Assert.Equal("guest", (string)st["why"]!);
        Assert.Equal("guest", r.Mod.Check("Wanderer", false, "guest")!.Value.reason);

        // sharing off again: refused with the plain reason, remembered across a restart
        await r.Do(mia, new { t = "atlas.sharing", on = false });
        Assert.Equal("share", (string)r.LastStatus("Mia")["why"]!);
        Assert.False(new CairnSharing(r.Fun.Hub.Cfg.DataDir, T.Log).IsOn(ProfileRules.Key("Mia")));
        await r.Do(mia, new { t = "atlas.sharing", on = true });
        Assert.True(new CairnSharing(r.Fun.Hub.Cfg.DataDir, T.Log).IsOn(ProfileRules.Key("Mia")));
    }

    [Fact]
    public async Task Sharers_are_told_when_new_data_arrives_and_when_the_Lodge_stops_serving()
    {
        using var r = new Rig();
        var mia = r.Fun.Join("Mia", "member");
        var bob = r.Fun.Join("Bob", "member");
        await r.Do(mia, new { t = "atlas.sharing", on = true });
        Assert.Equal("not-yet", (string)r.LastStatus("Mia")["why"]!);           // nothing on hand yet: a passing state, the Outpost keeps its copy
        r.Fun.Sock("Mia").Clear(); r.Fun.Sock("Bob").Clear();

        Assert.Equal("updated", r.Fetch());
        await Task.Delay(50);
        var told = r.LastStatus("Mia");
        Assert.True((bool)told["entitled"]!);
        Assert.Equal(1, (long)told["seq"]!);
        Assert.Empty(r.Fun.Sock("Bob").Of("cairn.status"));                     // Bob never said he shares: he hears nothing

        r.Cairn.Standing = "lapsed";
        r.Fetch();
        await Task.Delay(50);
        told = r.LastStatus("Mia");
        Assert.False((bool)told["entitled"]!);
        Assert.Equal("lodge", (string)told["why"]!);
    }

    [Fact]
    public async Task The_owner_sees_the_Lodges_standing()
    {
        using var r = new Rig();
        var elan = r.Fun.Join("Elan", "owner");
        r.Cairn.Standing = "lapsed";
        r.Fetch();
        await r.Do(elan, new { t = "cairn.status" });
        var l = r.LastStatus("Elan")["lodge"]!;
        Assert.Equal("lapsed", (string)l["standing"]!);
        Assert.False((bool)l["serving"]!);
        Assert.Contains("contribute to receive", (string)l["why"]!);
    }

    [Fact]
    public void Member_downloads_are_rate_limited_and_a_purge_forgets_the_sharing_note()
    {
        using var r = new Rig();
        for (int i = 0; i < CairnModule.DownloadsPerHour; i++) r.Mod.CheckDownloadRate("Mia");
        var e = Assert.Throws<DatasetException>(() => r.Mod.CheckDownloadRate("Mia"));
        Assert.Equal(429, e.Status);
        r.Fun.Clock.Advance(TimeSpan.FromMinutes(61));
        r.Mod.CheckDownloadRate("Mia");

        r.Mod.Sharing.Set(ProfileRules.Key("Gone"), true, 1);
        r.Fun.Hub.PurgeMember("Gone").GetAwaiter().GetResult();
        Assert.False(r.Mod.Sharing.IsOn(ProfileRules.Key("Gone")));
    }

    [Fact]
    public void The_manifest_handed_on_carries_the_checksums_and_the_signature_but_no_Cairn_address()
    {
        using var r = new Rig();
        r.Fetch();
        var h = r.Rx.Current!;
        var m = r.Rx.ManifestJson(h);
        Assert.Equal("elans-cairn-manifest", (string)m["format"]!);
        Assert.Equal(r.Cairn.Sha, (string)m["latest"]!["sha256"]!);
        Assert.Equal(r.Cairn.ContentSha, (string)m["latest"]!["contentSha256"]!);
        Assert.Equal("c2ln", (string)m["latest"]!["sig"]!);
        Assert.DoesNotContain("example.test", m.ToJsonString());
        Assert.Equal("\"" + r.Cairn.ContentSha[..32] + "\"", CairnReceiver.ETagOf(h));
        using var fs = r.Rx.OpenDataset(out var h2)!;
        using var ms = new MemoryStream();
        fs.CopyTo(ms);
        Assert.Equal(r.Cairn.Gz, ms.ToArray());
    }
}
