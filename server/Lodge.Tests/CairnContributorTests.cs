using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Lodge.Tests;

// "Contribute to the Cairn" (opt-in): the Lodge posts its POOLED Atlas totals (ids and counts only) to a Cairn. A fake Cairn here - no network.
public class CairnContributorTests
{
    const string Key = "cairn_0a1b2c3d_synthetic0key0for0tests0only0000000000000";
    const string Url = "https://cairn.example.test/cairn";

    // an in-memory Cairn: answers /v1/status and /v1/contribute, keeps every posted body (unpacked)
    sealed class FakeCairn : HttpMessageHandler
    {
        public readonly List<JsonObject> Posts = new();
        public readonly List<string> Paths = new();
        public readonly List<string> Auth = new();
        public string Epoch = "e1";
        public long LastSeq;
        public HttpStatusCode PostStatus = HttpStatusCode.OK;
        public HttpStatusCode StatusStatus = HttpStatusCode.OK;
        public int Calls;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Calls++;
            Paths.Add(req.RequestUri!.AbsolutePath);
            Auth.Add(req.Headers.Authorization?.ToString() ?? "");
            if (req.RequestUri.AbsolutePath.EndsWith("/v1/status"))
                return Json(StatusStatus, new JsonObject { ["ok"] = true, ["epoch"] = Epoch, ["lastSeq"] = LastSeq });
            var raw = await req.Content!.ReadAsByteArrayAsync(ct);
            Assert.Equal("gzip", req.Content.Headers.ContentEncoding.Single());
            using var ms = new MemoryStream();
            using (var gz = new GZipStream(new MemoryStream(raw), CompressionMode.Decompress)) gz.CopyTo(ms);
            var body = JsonNode.Parse(ms.ToArray())!.AsObject();
            if (PostStatus != HttpStatusCode.OK) return Json(PostStatus, new JsonObject { ["ok"] = false });
            Posts.Add(body);
            LastSeq = (long)body["seq"]!;
            return Json(HttpStatusCode.OK, new JsonObject { ["ok"] = true, ["seq"] = LastSeq, ["accepted"] = 1, ["rejected"] = 0, ["epoch"] = Epoch });
        }

        static HttpResponseMessage Json(HttpStatusCode c, JsonObject o) => new(c) { Content = new StringContent(o.ToJsonString(), Encoding.UTF8, "application/json") };
    }

    sealed class Rig : IDisposable
    {
        public readonly TempDir Dir = new();
        public readonly FakeClock Clock = new();
        public readonly LodgeConfig Cfg;
        public readonly AtlasStore Atlas;
        public readonly FlightStore Flights;
        public readonly FakeCairn Cairn = new();
        public readonly CaptureLogger Log = new();
        public readonly CairnContributor C;

        public Rig(Action<LodgeConfig>? tweak = null)
        {
            Cfg = T.Cfg(Dir.Path, c => { c.CairnEnabled = true; c.CairnUrl = Url; c.CairnKey = Key; });
            tweak?.Invoke(Cfg);
            Atlas = new AtlasStore(Dir.Path, T.Log, Clock);
            Flights = new FlightStore(Dir.Path, T.Log, Clock);
            C = new CairnContributor(Cfg, Atlas, Flights, Log, Clock, Cairn);
        }

        public long Now => new DateTimeOffset(Clock.UtcNow).ToUnixTimeSeconds();

        public void Share(string who, string json)
        {
            lock (Atlas.Gate) Atlas.Share(who.ToLowerInvariant(), who, JsonNode.Parse(json)!.AsObject());
        }

        public CairnContributor.RunResult Run() => C.RunOnce(CancellationToken.None).GetAwaiter().GetResult();

        public void Dispose() => Dir.Dispose();
    }

    static JsonNode? Rec(JsonObject post, string section, string key, string? kind = null) =>
        kind != null ? post["drops"]?[kind]?[key] : post[section]?[key];

    [Fact]
    public void Off_by_default_and_off_without_a_valid_url_and_key()
    {
        using var dir = new TempDir();
        var plain = T.Cfg(dir.Path);
        Assert.False(plain.CairnEnabled);
        Assert.Equal("", plain.CairnUrl);                                  // nothing is built in
        var fake = new FakeCairn();
        var c = new CairnContributor(plain, new AtlasStore(dir.Path, T.Log, new FakeClock()), new FlightStore(dir.Path, T.Log, new FakeClock()), T.Log, new FakeClock(), fake);
        Assert.False(c.Enabled);
        Assert.Equal("disabled", c.RunOnce(default).Result.Stopped);
        Assert.Equal(0, fake.Calls);

        Assert.False(CairnRules.ValidUrl("http://cairn.example.test/cairn", out _));   // plain http to another host: never
        Assert.True(CairnRules.ValidUrl("http://127.0.0.1:5290/cairn", out _));
        Assert.True(CairnRules.ValidUrl(Url, out _));
        Assert.False(CairnRules.ValidUrl("https://user:pw@cairn.example.test/", out _));
        Assert.False(CairnRules.ValidUrl("https://cairn.example.test/?key=x", out _));
        Assert.False(CairnRules.ValidKey("not-a-key"));
        Assert.True(CairnRules.ValidKey(Key));

        using var r = new Rig(cfg => cfg.CairnUrl = "http://cairn.example.test/cairn");
        Assert.False(r.C.Enabled);
        using var r2 = new Rig(cfg => cfg.AtlasEnabled = false);
        Assert.False(r2.C.Enabled);                                         // no Atlas pool, nothing to contribute
    }

    [Fact]
    public void The_feature_is_advertised_only_while_contributing()
    {
        using var dir = new TempDir();
        var off = new LodgeHub(T.Cfg(dir.Path), new Auth(T.Cfg(dir.Path), T.Log), new FileStore(T.Cfg(dir.Path)), T.Log);
        Assert.DoesNotContain("cairn", off.FeatureList());
        using var dir2 = new TempDir();
        var cfg = T.Cfg(dir2.Path, c => { c.CairnEnabled = true; c.CairnUrl = Url; c.CairnKey = Key; });
        var on = new LodgeHub(cfg, new Auth(cfg, T.Log), new FileStore(cfg), T.Log);
        Assert.Contains("cairn", on.FeatureList());                        // members see that their Lodge contributes
    }

    [Fact]
    public void Only_pooled_ids_and_counts_are_sent_never_names()
    {
        using var r = new Rig();
        long at = r.Now - 60;
        r.Share("Samhain", $@"{{""kills"":{{""448"":{{""n"":12,""items"":{{""2589"":{{""n"":5,""c"":7}}}}}}}},
            ""vendors"":{{""1247"":{{""at"":{at},""z"":1429,""x"":0.5,""y"":0.5,""items"":{{""159"":{{""p"":25,""s"":1,""l"":-1}}}}}}}},
            ""spawns"":{{""448"":[{{""z"":1429,""x"":0.25,""y"":0.75}}]}},
            ""finds"":[{{""i"":2589,""c"":1,""at"":{at},""src"":""npc:448"",""gb"":57689}}]}}");
        r.Share("Wing", $@"{{""kills"":{{""448"":{{""n"":3}}}},""mining"":{{""1731"":{{""n"":4,""items"":{{""2770"":{{""n"":4,""c"":9}}}}}}}}}}");
        lock (r.Flights.Gate)
        {
            r.Flights.Share("samhain", JsonNode.Parse(@"{""Horde|11>23"":{""t"":95,""n"":2},""Horde|Orgrimmar>Crossroads"":{""t"":90,""n"":1}}")!.AsObject(), new JsonObject());
        }

        var res = r.Run();
        Assert.Equal("", res.Stopped);
        var post = Assert.Single(r.Cairn.Posts);
        Assert.Equal(1, (int)post["v"]!);
        Assert.Equal(57689, (int)post["gb"]!);                              // the build of the newest find
        Assert.Equal("{\"n\":15,\"i\":{\"2589\":[5,7]}}", Rec(post, "drops", "448", "kill")!.ToJsonString());   // the pool's sum over members
        Assert.Equal("[4,9]", Rec(post, "drops", "1731", "mining")!["i"]!["2770"]!.ToJsonString());
        Assert.Equal("[25,1,-1,0]", post["vendors"]!["1247"]!["159"]!.ToJsonString());
        Assert.Equal("[[1429,0.25,0.75,1]]", post["spawns"]!["448"]!.ToJsonString());
        Assert.Equal("[95,2]", post["flights"]!["Horde|11>23"]!.ToJsonString());
        Assert.Null(post["flights"]!["Horde|Orgrimmar>Crossroads"]);       // a node name is game text: never sent

        var text = post.ToJsonString();
        foreach (var bad in new[] { "Samhain", "samhain", "Wing", "wing", "Orgrimmar", "\"by\"", "finds", "quests", "seen", "fieldnotes", "\"at\"" })
            Assert.DoesNotContain(bad, text);
        Assert.All(r.Cairn.Auth, a => Assert.Equal("Bearer " + Key, a));
        Assert.DoesNotContain(r.Log.Lines, l => l.Contains(Key) || l.Contains("synthetic0key"));   // the key is never logged
    }

    [Fact]
    public void Only_what_changed_is_sent_and_a_purge_lowers_the_totals()
    {
        using var r = new Rig();
        r.Share("Samhain", @"{""kills"":{""448"":{""n"":12},""449"":{""n"":2}}}");
        r.Share("Wing", @"{""kills"":{""448"":{""n"":3}}}");
        r.Run();
        Assert.Single(r.Cairn.Posts);
        Assert.Equal("nothing-new", r.Run().Stopped);                      // nothing changed: no post
        Assert.Single(r.Cairn.Posts);

        r.Clock.Advance(TimeSpan.FromMinutes(5));
        r.Share("Wing", @"{""kills"":{""448"":{""n"":8}}}");
        r.Run();
        var p2 = r.Cairn.Posts[1];
        Assert.Equal(20, (long)Rec(p2, "drops", "448", "kill")!["n"]!);
        Assert.Null(Rec(p2, "drops", "449", "kill"));                       // unchanged: not sent again
        Assert.True((long)p2["seq"]! > (long)r.Cairn.Posts[0]["seq"]!);

        // Samhain leaves and is purged from the pool: the Cairn gets the lower total, and null for a record that is gone
        lock (r.Atlas.Gate) r.Atlas.Purge("samhain");
        r.Run();
        var p3 = r.Cairn.Posts[2];
        Assert.Equal(8, (long)Rec(p3, "drops", "448", "kill")!["n"]!);
        Assert.True(p3["drops"]!["kill"]!.AsObject().ContainsKey("449"));
        Assert.Null(p3["drops"]!["kill"]!["449"]);
    }

    [Fact]
    public void A_failed_post_is_sent_again_with_a_new_seq()
    {
        using var r = new Rig();
        r.Share("Samhain", @"{""kills"":{""448"":{""n"":12}}}");
        r.Cairn.PostStatus = HttpStatusCode.InternalServerError;
        Assert.Equal("HTTP 500", r.Run().Stopped);
        Assert.Empty(r.Cairn.Posts);
        r.Cairn.PostStatus = HttpStatusCode.OK;
        r.Run();
        var p = Assert.Single(r.Cairn.Posts);
        Assert.Equal(12, (long)Rec(p, "drops", "448", "kill")!["n"]!);
        Assert.Equal(2, (long)p["seq"]!);                                   // seq 1 was used by the failed try
        r.Cairn.PostStatus = HttpStatusCode.Unauthorized;
        r.Share("Samhain", @"{""kills"":{""448"":{""n"":13}}}");
        Assert.StartsWith("key refused", r.Run().Stopped);
        Assert.Contains(r.Log.Lines, l => l.Contains("key refused"));
    }

    [Fact]
    public void A_new_epoch_or_a_week_means_everything_again()
    {
        using var r = new Rig();
        r.Share("Samhain", @"{""kills"":{""448"":{""n"":12},""449"":{""n"":2}}}");
        r.Run();
        r.Cairn.Epoch = "e2";                                               // the Cairn forgot this Lodge (purge or restore)
        r.Cairn.LastSeq = 0;
        var res = r.Run();
        Assert.True(res.Full);
        Assert.Equal(2, r.Cairn.Posts[1]["drops"]!["kill"]!.AsObject().Count);
        Assert.True((long)r.Cairn.Posts[1]["seq"]! > (long)r.Cairn.Posts[0]["seq"]!);   // the Lodge's own seq keeps growing
        Assert.Equal("nothing-new", r.Run().Stopped);
        r.Clock.Advance(TimeSpan.FromDays(8));
        Assert.True(r.Run().Full);
        Assert.Equal(3, r.Cairn.Posts.Count);
    }

    [Fact]
    public void Big_pools_go_in_chunks_below_the_body_limit()
    {
        var changed = new SortedDictionary<string, JsonNode?>(StringComparer.Ordinal);
        for (int i = 1; i <= 30_000; i++) changed["drops/kill/" + i] = JsonNode.Parse(@"{""n"":40,""i"":{""2589"":[5,7],""2590"":[1,1]}}");
        long seq = 10;
        var chunks = CairnRules.Chunks(changed!, 1, () => ++seq);
        Assert.True(chunks.Count >= 2);
        Assert.Equal(30_000, chunks.Sum(c => c.keys.Count));
        Assert.All(chunks, c => Assert.True(c.body.ToJsonString().Length < CairnRules.ChunkBytes + 1000));
        Assert.Equal(Enumerable.Range(11, chunks.Count).Select(x => (long)x), chunks.Select(c => (long)c.body["seq"]!));
    }

    [Fact]
    public void The_state_survives_a_restart()
    {
        using var r = new Rig();
        r.Share("Samhain", @"{""kills"":{""448"":{""n"":12}}}");
        r.Run();
        var again = new CairnContributor(r.Cfg, r.Atlas, r.Flights, r.Log, r.Clock, r.Cairn);
        Assert.Equal("nothing-new", again.RunOnce(default).Result.Stopped);
        Assert.Single(r.Cairn.Posts);
        Assert.True(File.Exists(Path.Combine(r.Dir.Path, "cairn-sent.json")));
        Assert.DoesNotContain("synthetic0key", File.ReadAllText(Path.Combine(r.Dir.Path, "cairn-sent.json")));
    }
}
