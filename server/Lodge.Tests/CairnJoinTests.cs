using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Lodge.Tests;

// 2.28 "Join the Cairn": the Lodge's own key pair, signed requests, the Guild Master's switch, the beta's invite code, the waiting list, and the
// env path's one-time move from its bearer key to signed requests. A fake Cairn that checks every signature - no network.
public class CairnJoinTests
{
    const string Key = "cairn_0a1b2c3d_synthetic0key0for0tests0only0000000000000";
    const string Url = "https://cairn.example.test/cairn";
    const string LodgeUrl = "https://lodge.example.test/lodge";
    const string Code = "CAIRN-ABCD-EFGH";

    sealed class FakeCairn : HttpMessageHandler
    {
        public string Mode = "beta";
        public bool Keys;                                   // offers POST /v1/keys (env path)
        public bool BearerRetired;
        public string AdmitAs = "admitted";                 // what a join without a code gets in open mode: admitted / waiting
        public string RefuseCode;                           // a reason for the code (code-invalid, beta-full, ...)
        public string PublicKey;                            // learned from the join / keys body
        public readonly List<string> Paths = new();
        public readonly List<string> Auth = new();
        public readonly List<bool> SignedOk = new();
        public readonly List<JsonObject> Joins = new();
        public string State = "";
        public int Place = 2;
        public Func<string, string>? Challenge;            // the Lodge's /cairn/challenge as the Cairn would call it

        static HttpResponseMessage J(HttpStatusCode c, JsonObject o) => new(c) { Content = new StringContent(o.ToJsonString(), Encoding.UTF8, "application/json") };

        bool Verify(HttpRequestMessage req, byte[] body)
        {
            if (PublicKey == null || !req.Headers.TryGetValues(CairnSign.HSig, out var sig)) return false;
            string H(string n) => req.Headers.GetValues(n).Single();
            var spki = Convert.FromBase64String(PublicKey.Replace('-', '+').Replace('_', '/') + new string('=', (4 - PublicKey.Length % 4) % 4));
            using var k = ECDsa.Create(); k.ImportSubjectPublicKeyInfo(spki, out _);
            var path = req.RequestUri!.AbsolutePath["/cairn".Length..] + req.RequestUri.Query;
            var msg = CairnSign.RequestMessage(req.Method.Method, path, H(CairnSign.HTime), H(CairnSign.HNonce), CairnSign.Sha256Hex(body));
            var s = sig.Single().Replace('-', '+').Replace('_', '/');
            return k.VerifyData(Encoding.UTF8.GetBytes(msg), Convert.FromBase64String(s + new string('=', (4 - s.Length % 4) % 4)), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
                && H(CairnSign.HKey) == Convert.ToHexString(SHA256.HashData(spki)).ToLowerInvariant()[..16];
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            var body = req.Content == null ? Array.Empty<byte>() : await req.Content.ReadAsByteArrayAsync(ct);
            bool signedHeaders = req.Headers.Contains(CairnSign.HSig);
            lock (Paths) { Paths.Add(path); Auth.Add(req.Headers.Authorization?.ToString() ?? ""); }
            if (path.EndsWith("/v1/join/info")) return J(HttpStatusCode.OK, new JsonObject { ["ok"] = true, ["mode"] = Mode, ["betaOpenSlots"] = Mode == "beta" ? 3 : null });
            if (path.EndsWith("/v1/join"))
            {
                var o = JsonNode.Parse(body)!.AsObject();
                Joins.Add(o);
                PublicKey = (string)o["publicKey"]!;
                lock (Paths) SignedOk.Add(Verify(req, body));
                if (!SignedOk.Last()) return J(HttpStatusCode.Unauthorized, new JsonObject { ["ok"] = false, ["reason"] = "signature" });
                // the call back: the Lodge must sign the nonce for the address it gave
                var nonce = "nonce0123456789abcdef";
                var ans = JsonNode.Parse(Challenge!(nonce))!.AsObject();
                if ((string)ans["publicUrl"]! != (string)o["publicUrl"]! || (string)ans["nonce"]! != nonce) return J((HttpStatusCode)422, new JsonObject { ["ok"] = false, ["reason"] = "unverified" });
                var code = (string?)o["code"];
                if (Mode == "beta" && code == null) return J(HttpStatusCode.Forbidden, new JsonObject { ["ok"] = false, ["reason"] = "invite-needed", ["error"] = "beta: an invite code from the Cairn's operator is needed" });
                if (code != null && RefuseCode != null) return J(HttpStatusCode.Forbidden, new JsonObject { ["ok"] = false, ["reason"] = RefuseCode, ["error"] = "no: " + RefuseCode });
                State = code != null ? "admitted" : AdmitAs;
                return J(HttpStatusCode.OK, StateJson());
            }
            if (path.EndsWith("/v1/join/status"))
            {
                lock (Paths) SignedOk.Add(Verify(req, body));
                return J(SignedOk.Last() ? HttpStatusCode.OK : HttpStatusCode.Unauthorized, StateJson());
            }
            if (path.EndsWith("/v1/keys"))
            {
                PublicKey = (string)JsonNode.Parse(body)!["publicKey"]!;
                lock (Paths) SignedOk.Add(Verify(req, body));
                if (req.Headers.Authorization?.ToString() != "Bearer " + Key || !SignedOk.Last()) return J(HttpStatusCode.Unauthorized, new JsonObject { ["reason"] = "key" });
                BearerRetired = true;
                return J(HttpStatusCode.OK, new JsonObject { ["ok"] = true, ["signed"] = true });
            }
            // the Lodge routes: signed (and then verified), or the bearer key while it is not retired
            if (signedHeaders) { lock (Paths) SignedOk.Add(Verify(req, body)); if (!SignedOk.Last()) return J(HttpStatusCode.Unauthorized, new JsonObject { ["reason"] = "signature" }); }
            else if (req.Headers.Authorization?.ToString() != "Bearer " + Key) return J(HttpStatusCode.Unauthorized, new JsonObject { ["reason"] = "key" });
            else if (BearerRetired) return J(HttpStatusCode.Unauthorized, new JsonObject { ["reason"] = "retired" });
            if (path.EndsWith("/v1/status"))
                return J(HttpStatusCode.OK, new JsonObject { ["ok"] = true, ["epoch"] = "e1", ["lastSeq"] = 0, ["standing"] = "new", ["features"] = Keys ? new JsonArray("outposts", "keys") : new JsonArray("outposts"), ["probationUntil"] = 1_900_000_000L, ["settled"] = false });
            if (path.EndsWith("/v1/contribute"))
                return J(HttpStatusCode.OK, new JsonObject { ["ok"] = true, ["seq"] = 1, ["accepted"] = 1, ["epoch"] = "e1", ["contribution"] = true, ["standing"] = "good", ["receiveUntil"] = 1_900_000_000L });
            if (path.EndsWith("/v1/manifest.json")) return J(HttpStatusCode.NotFound, new JsonObject { ["reason"] = "none" });
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        JsonObject StateJson() => State == "waiting"
            ? new JsonObject { ["ok"] = true, ["state"] = "waiting", ["place"] = Place, ["of"] = 5, ["estDay"] = "2026-10-19", ["mode"] = Mode }
            : new JsonObject { ["ok"] = true, ["state"] = State, ["admitted"] = 1_800_000_000L, ["probationUntil"] = null, ["settled"] = false, ["mode"] = Mode };
    }

    sealed class Rig : IDisposable
    {
        public readonly FunRig Fun;
        public readonly FakeCairn Cairn = new();
        public readonly CaptureLogger Log = new();
        public LodgeHub Hub => Fun.Hub;
        public CairnLink Link => Fun.Hub.Cairn.Link;

        public Rig(Action<LodgeConfig>? tweak = null)
        {
            Fun = new FunRig(c => { c.CairnUrl = ""; tweak?.Invoke(c); }, Log);
            Hub.Cairn.UseHandlerForTests(Cairn);
            Hub.CairnIn.Joiner.UseHandlerForTests(Cairn);
            Hub.CairnIn.Receiver.UseHandlerForTests(Cairn);
            Cairn.Challenge = n => Link.ChallengeAnswer(n)?.ToJsonString() ?? "{}";
        }

        public Task Do(Member m, object o)
        {
            var j = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(o))!.AsObject();
            return Hub.CairnIn.Handle(m, (string)j["t"]!, j);
        }

        public string JoinRun() => Hub.CairnIn.Joiner.RunOnce(CancellationToken.None).GetAwaiter().GetResult();
        public CairnContributor.RunResult Contribute() => Hub.Cairn.RunOnce(CancellationToken.None).GetAwaiter().GetResult();
        public JsonObject Last(string name) => Fun.Sock(name).Of("cairn.join").Last();
        public void Share() { lock (Hub.AtlasPool.Store.Gate) Hub.AtlasPool.Store.Share("ana", "Ana", JsonNode.Parse(@"{""kills"":{""100"":{""n"":5,""items"":{""7"":{""n"":1,""c"":1}}}}}")!.AsObject()); }
        public void Dispose() => Fun.Dispose();
    }

    // ---------------------------------------------------------------- the key pair

    [Fact]
    public void The_key_pair_is_made_once_kept_private_and_only_the_public_half_is_shown()
    {
        using var dir = new TempDir();
        var k = new CairnKeys(dir.Path);
        Assert.False(k.Exists);
        k.Get();
        Assert.True(k.Exists);
        Assert.Matches("^[0-9a-f]{16}$", k.KeyId);
        var pem = File.ReadAllText(k.FilePath);
        Assert.StartsWith("-----BEGIN PRIVATE KEY-----", pem);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(k.FilePath));
        var again = new CairnKeys(dir.Path);
        again.Get();
        Assert.Equal(k.KeyId, again.KeyId);                    // the same key after a restart
        Assert.Equal(k.PublicKey, again.PublicKey);
        Assert.DoesNotContain("PRIVATE", k.PublicKey);
        // the key file sits in the data folder, so lodge-backup takes it along with everything else
        Assert.Equal(Path.GetFullPath(dir.Path), Path.GetDirectoryName(Path.GetFullPath(k.FilePath)));
    }

    [Fact]
    public void The_challenge_is_answered_only_while_joining_is_on_and_is_signed_for_the_given_address()
    {
        using var r = new Rig();
        Assert.Null(r.Link.ChallengeAnswer("nonce0123456789abcdef"));            // off
        lock (r.Link.Join.Gate) { r.Link.Join.On = true; r.Link.Join.PublicUrl = LodgeUrl; }
        Assert.Null(r.Link.ChallengeAnswer("nonce0123456789abcdef"));            // no key pair yet: never made by this request
        Assert.False(r.Link.Keys.Exists);
        r.Link.Keys.Get();
        Assert.Null(r.Link.ChallengeAnswer("short"));
        Assert.Null(r.Link.ChallengeAnswer("nonce0123456789abcdef\nelans-cairn-req/1"));   // no line breaks: never a request signature
        var a = r.Link.ChallengeAnswer("nonce0123456789abcdef")!;
        Assert.Equal(LodgeUrl, (string)a["publicUrl"]!);
        Assert.Equal(r.Link.Keys.KeyId, (string)a["keyId"]!);
        using var pub = ECDsa.Create();
        var spki = Convert.FromBase64String(r.Link.Keys.PublicKey.Replace('-', '+').Replace('_', '/') + new string('=', (4 - r.Link.Keys.PublicKey.Length % 4) % 4));
        pub.ImportSubjectPublicKeyInfo(spki, out _);
        var s = ((string)a["sig"]!).Replace('-', '+').Replace('_', '/');
        Assert.True(pub.VerifyData(Encoding.UTF8.GetBytes("elans-cairn-challenge/1\nnonce0123456789abcdef\n" + LodgeUrl),
            Convert.FromBase64String(s + new string('=', (4 - s.Length % 4) % 4)), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        int answered = 1;
        for (int i = 0; i < 40; i++) if (r.Link.ChallengeAnswer("nonce0123456789abcde" + (char)('a' + i % 26)) != null) answered++;
        Assert.Equal(30, answered);                                              // at most 30 a minute
    }

    [Theory]
    [InlineData("http://lodge.example.org/lodge")]
    [InlineData("https://localhost/lodge")]
    [InlineData("https://10.20.30.40:5280/lodge")]
    [InlineData("https://lodge.example.org/lodge?x=1")]
    [InlineData("https://intranet/lodge")]
    [InlineData("")]
    public void Unusable_public_addresses_are_refused_with_a_reason(string url)
    {
        Assert.False(CairnSign.TryNormalizeUrl(url, out _, out var why));
        Assert.False(string.IsNullOrEmpty(why));
    }

    [Fact]
    public void A_public_address_is_written_the_way_the_Cairn_checks_it()
    {
        Assert.True(CairnSign.TryNormalizeUrl(" https://Nasferatu.Example/Lodge/ ", out var n, out _));
        Assert.Equal("https://nasferatu.example/Lodge", n);
        Assert.True(CairnSign.TryNormalizeUrl("https://lodge.example.org:8443", out n, out _));
        Assert.Equal("https://lodge.example.org:8443", n);
    }

    // ---------------------------------------------------------------- the Guild Master's switch

    [Fact]
    public async Task Only_the_Guild_Master_switches_it_officers_see_the_status_members_see_nothing()
    {
        using var r = new Rig();
        var gm = r.Fun.Join("Elan", "owner");
        var officer = r.Fun.Join("Ana", "officer");
        var member = r.Fun.Join("Bo", "member");
        Assert.Contains("cairnjoin", r.Hub.FeatureList());
        await r.Do(member, new { t = "cairn.join.get" });
        Assert.Empty(r.Fun.Sock("Bo").Of("cairn.join"));
        Assert.NotNull(r.Fun.Sock("Bo").LastError());
        await r.Do(officer, new { t = "admin.cairn", on = true, publicUrl = LodgeUrl });
        Assert.False(r.Link.Join.On);
        await r.Do(officer, new { t = "cairn.join.get" });
        var st = r.Last("Ana");
        Assert.False((bool)st["on"]!);
        Assert.Equal("nasferatu.dk", (string)st["cairnHost"]!);                  // the default public Cairn (LODGE_CAIRN_URL not set)
        await r.Do(gm, new { t = "admin.cairn", on = true, publicUrl = "http://lodge.example.test/lodge" });
        Assert.Contains("https", (string)r.Fun.Sock("Elan").LastError()!["text"]!);
        Assert.False(r.Link.Join.On);
        await r.Do(gm, new { t = "admin.cairn", on = true, publicUrl = LodgeUrl + "/", code = "  cairn-abcd-efgh " });
        Assert.True(r.Link.Join.On);
        Assert.Equal(LodgeUrl, r.Link.Join.PublicUrl);
        Assert.Equal(Code, r.Link.Join.Code);
        var told = r.Last("Ana");                                                // officers hear the change too
        Assert.True((bool)told["on"]!);
        Assert.True((bool)told["hasCode"]!);
        Assert.Null(told["code"]);                                               // the code itself never goes to anyone
        Assert.DoesNotContain(r.Log.Lines, l => l.Contains("ABCD"));
        Assert.Empty(r.Fun.Sock("Bo").Of("cairn.join"));
    }

    [Fact]
    public async Task The_switch_is_off_by_default_and_locked_when_the_env_path_is_used()
    {
        using var r = new Rig(c => { c.CairnEnabled = true; c.CairnUrl = Url; c.CairnKey = Key; });
        var gm = r.Fun.Join("Elan", "owner");
        await r.Do(gm, new { t = "cairn.join.get" });
        var st = r.Last("Elan");
        Assert.True((bool)st["env"]!);
        Assert.True((bool)st["on"]!);
        await r.Do(gm, new { t = "admin.cairn", on = false });
        Assert.Equal("locked", (string)r.Fun.Sock("Elan").LastError()!["code"]!);

        using var off = new Rig();
        Assert.False(off.Link.Join.On);
        Assert.False(off.Hub.Cairn.Enabled);
        Assert.Equal("off", off.JoinRun());                                     // nothing is asked while the switch is off
        Assert.Empty(off.Cairn.Paths);
    }

    // ---------------------------------------------------------------- joining against the fake Cairn

    [Fact]
    public async Task Beta_without_a_code_says_so_with_a_code_it_is_admitted_contributes_signed_and_receives()
    {
        using var r = new Rig();
        var gm = r.Fun.Join("Elan", "owner");
        await r.Do(gm, new { t = "admin.cairn", on = true, publicUrl = LodgeUrl });
        Assert.Equal("error: invite-needed", r.JoinRun());
        Assert.Equal("invite-needed", r.Link.Join.Reason);
        Assert.Empty(r.Cairn.Joins);                                            // the beta needs a code: not even asked
        Assert.Equal("beta", r.Link.Join.Mode);

        await r.Do(gm, new { t = "admin.cairn", on = true, code = Code });
        Assert.Equal("admitted", r.JoinRun());
        var join = r.Cairn.Joins.Single();
        Assert.Equal(LodgeUrl, (string)join["publicUrl"]!);
        Assert.Equal(Code, (string)join["code"]!);
        Assert.Equal(r.Link.Keys.PublicKey, (string)join["publicKey"]!);
        Assert.True(r.Cairn.SignedOk.All(x => x));
        Assert.Equal("", r.Link.Join.Code);                                      // used: forgotten
        Assert.DoesNotContain("ABCD", File.ReadAllText(Path.Combine(r.Hub.Cfg.DataDir, "cairn-join.json")));
        Assert.DoesNotContain(r.Log.Lines, l => l.Contains("ABCD"));
        Assert.True(r.Hub.Cairn.Enabled);                                       // admitted: it contributes now

        r.Share();
        var res = r.Contribute();
        Assert.Equal(1, res.Posts);
        Assert.True(r.Cairn.SignedOk.All(x => x));
        var idx = r.Cairn.Paths.FindIndex(p => p.EndsWith("/v1/contribute"));
        Assert.Equal("", r.Cairn.Auth[idx]);                                    // no bearer key: signed only
        Assert.NotNull(r.Link.Join.LastPost);
        Assert.Equal(1_900_000_000L, r.Link.Join.ProbationUntil);
        Assert.Equal("good", r.Hub.CairnIn.Receiver.Standing);
        r.Hub.CairnIn.Receiver.RunOnce(CancellationToken.None).GetAwaiter().GetResult();
        Assert.Contains(r.Cairn.Paths, p => p.EndsWith("/v1/manifest.json"));
        Assert.Equal("", r.Cairn.Auth[r.Cairn.Paths.FindLastIndex(p => p.EndsWith("/v1/manifest.json"))]);

        await r.Do(gm, new { t = "cairn.join.get" });
        var st = r.Last("Elan");
        Assert.Equal("admitted", (string)st["state"]!);
        Assert.True((bool)st["contributing"]!);
        Assert.True((bool)st["signed"]!);
        Assert.Equal(1_900_000_000L, (long)st["probationUntil"]!);

        // switched off: no contribution, the key pair and the admission stay for later
        await r.Do(gm, new { t = "admin.cairn", on = false });
        Assert.False(r.Hub.Cairn.Enabled);
        Assert.True(r.Link.Keys.Exists);
        Assert.Equal("admitted", r.Link.Join.State);
    }

    [Theory]
    [InlineData("code-invalid", true)]
    [InlineData("code-expired", true)]
    [InlineData("code-used", true)]
    [InlineData("beta-full", false)]
    public async Task A_refused_code_is_dropped_but_a_full_beta_keeps_it(string reason, bool dropped)
    {
        using var r = new Rig();
        r.Cairn.RefuseCode = reason;
        var gm = r.Fun.Join("Elan", "owner");
        await r.Do(gm, new { t = "admin.cairn", on = true, publicUrl = LodgeUrl, code = Code });
        Assert.Equal("error: " + reason, r.JoinRun());
        Assert.Equal(reason, r.Link.Join.Reason);
        Assert.Equal(dropped ? "" : Code, r.Link.Join.Code);
        Assert.False(r.Hub.Cairn.Enabled);
        await r.Do(gm, new { t = "cairn.join.get" });
        Assert.Equal(reason, (string)r.Last("Elan")["reason"]!);
        Assert.Equal(!dropped, (bool)r.Last("Elan")["hasCode"]!);
    }

    [Fact]
    public async Task Open_mode_waits_in_the_list_then_follows_the_status_to_admitted()
    {
        using var r = new Rig();
        r.Cairn.Mode = "open"; r.Cairn.AdmitAs = "waiting";
        var gm = r.Fun.Join("Elan", "owner");
        await r.Do(gm, new { t = "admin.cairn", on = true, publicUrl = LodgeUrl });
        Assert.Equal("waiting", r.JoinRun());
        Assert.Equal(2, r.Link.Join.Place);
        Assert.Equal("2026-10-19", r.Link.Join.EstDay);
        Assert.False(r.Hub.Cairn.Enabled);
        await r.Do(gm, new { t = "cairn.join.get" });
        Assert.Equal(2, (int)r.Last("Elan")["place"]!);
        r.Cairn.State = "admitted";
        Assert.Equal("admitted", r.JoinRun());                                  // the status poll, not a second join
        Assert.Single(r.Cairn.Joins);
        Assert.True(r.Hub.Cairn.Enabled);
        // a restart keeps all of it
        var again = new CairnJoinState(r.Hub.Cfg.DataDir);
        Assert.True(again.On); Assert.Equal("admitted", again.State); Assert.Equal(LodgeUrl, again.PublicUrl);
    }

    [Fact]
    public async Task An_address_already_asked_with_cannot_be_swapped()
    {
        using var r = new Rig();
        r.Cairn.Mode = "open"; r.Cairn.AdmitAs = "waiting";
        var gm = r.Fun.Join("Elan", "owner");
        await r.Do(gm, new { t = "admin.cairn", on = true, publicUrl = LodgeUrl });
        r.JoinRun();
        await r.Do(gm, new { t = "admin.cairn", on = true, publicUrl = "https://other.example.test/lodge" });
        Assert.Equal("address", (string)r.Fun.Sock("Elan").LastError()!["code"]!);
        Assert.Equal(LodgeUrl, r.Link.Join.PublicUrl);
    }

    // ---------------------------------------------------------------- the env path: unchanged, and its one-time move to signed requests

    [Fact]
    public void The_env_path_still_works_with_its_bearer_key_on_a_Cairn_without_keys()
    {
        using var r = new Rig(c => { c.CairnEnabled = true; c.CairnUrl = Url; c.CairnKey = Key; });
        r.Share();
        var res = r.Contribute();
        Assert.Equal(1, res.Posts);
        Assert.All(r.Cairn.Auth, a => Assert.Equal("Bearer " + Key, a));
        Assert.False(r.Link.Keys.Exists);                                      // no key pair without a reason
        Assert.False(r.Link.Join.Migrated);
    }

    [Fact]
    public void The_env_path_registers_its_key_pair_once_then_signs_and_the_bearer_key_is_no_longer_sent()
    {
        using var r = new Rig(c => { c.CairnEnabled = true; c.CairnUrl = Url; c.CairnKey = Key; });
        r.Cairn.Keys = true;
        r.Share();
        var first = r.Contribute();
        Assert.Contains(r.Cairn.Paths, p => p.EndsWith("/v1/keys"));
        Assert.True(r.Cairn.BearerRetired);
        Assert.True(r.Link.Join.Migrated);
        Assert.True(r.Link.Signs);
        int before = r.Cairn.Paths.Count;
        r.Share();
        lock (r.Hub.AtlasPool.Store.Gate) r.Hub.AtlasPool.Store.Share("bo", "Bo", JsonNode.Parse(@"{""kills"":{""200"":{""n"":3}}}")!.AsObject());
        var second = r.Contribute();
        Assert.True(second.Posts >= 1);
        for (int i = before; i < r.Cairn.Paths.Count; i++) Assert.Equal("", r.Cairn.Auth[i]);   // signed only from now on
        Assert.True(r.Cairn.SignedOk.All(x => x));
        Assert.Single(r.Cairn.Paths, p => p.EndsWith("/v1/keys"));            // registered once
        Assert.True(new CairnJoinState(r.Hub.Cfg.DataDir).Migrated);           // survives a restart
    }

    [Fact]
    public void A_retired_bearer_key_answer_moves_the_env_path_to_signing()
    {
        using var r = new Rig(c => { c.CairnEnabled = true; c.CairnUrl = Url; c.CairnKey = Key; });
        r.Link.Keys.Get();
        r.Cairn.PublicKey = r.Link.Keys.PublicKey;                              // registered before, but the note was lost
        r.Cairn.BearerRetired = true;
        var res = r.Contribute();
        Assert.Equal("signing from the next run", res.Stopped);
        Assert.True(r.Link.Signs);
        r.Share();
        Assert.Equal(1, r.Contribute().Posts);
    }
}
