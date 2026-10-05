using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Lodge.Tests;

// 2.6.0: avatars and profiles - validation, whitelists, privacy, persistence, budget, invisible members, owner reset.
public class ProfileTests
{
    internal sealed class ScriptSocket : WebSocket
    {
        readonly System.Threading.Channels.Channel<byte[]> incoming = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
        WebSocketState state = WebSocketState.Open;
        public readonly List<JsonObject> Sent = new();
        public override WebSocketCloseStatus? CloseStatus => state == WebSocketState.Open ? null : WebSocketCloseStatus.PolicyViolation;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => state;
        public override string? SubProtocol => null;
        public override void Abort() { state = WebSocketState.Aborted; incoming.Writer.TryComplete(); }
        public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken ct) { state = WebSocketState.Closed; incoming.Writer.TryComplete(); return Task.CompletedTask; }
        public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken ct) => CloseAsync(s, d, ct);
        public override void Dispose() { }
        public void Say(string json) => incoming.Writer.TryWrite(Encoding.UTF8.GetBytes(json));
        public void Hangup() => incoming.Writer.TryComplete();
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
        {
            if (!await incoming.Reader.WaitToReadAsync(ct)) return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            var d = await incoming.Reader.ReadAsync(ct);
            d.CopyTo(buffer.Array!, buffer.Offset);
            return new WebSocketReceiveResult(d.Length, WebSocketMessageType.Text, true);
        }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType t, bool end, CancellationToken ct)
        {
            if (t == WebSocketMessageType.Text)
                lock (Sent) Sent.Add(JsonNode.Parse(Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count))!.AsObject());
            return Task.CompletedTask;
        }
        public List<JsonObject> Of(string type) { lock (Sent) return Sent.Where(x => (string?)x["t"] == type).ToList(); }
        public void Clear() { lock (Sent) Sent.Clear(); }
        // the newest profile:data answer
        public JsonObject Profile() => (JsonObject)Of("profile:data").Last()["profile"]!;
    }

    internal const string MiaCode = "synthetic0mia00code0000000000003";
    internal const string ZedCode = "synthetic0zed00code0000000000004";

    internal sealed class Lodge3 : IDisposable
    {
        public LodgeHub Hub = null!; public Auth Auth = null!; public TempDir Dir = new(); public LodgeConfig Cfg = null!;
        public FakeClock Clock = new();
        public CancellationTokenSource Cts = new();
        public List<Task> Runs = new();
        public Lodge3(TempDir? dir = null)
        {
            if (dir != null) Dir = dir;
            Cfg = LodgeConfig.ForTests(Dir.Path);
            if (!File.Exists(Cfg.CodesFile))
                T.WriteCodes(Cfg, $"Elan:{T.ElanCode}:owner", $"Bob:{T.BobCode}:officer", $"Mia:{MiaCode}:member", $"Zed:{ZedCode}:member");
            Auth = new Auth(Cfg, T.Log);
            Hub = new LodgeHub(Cfg, Auth, new FileStore(Cfg), T.Log, Clock);
        }
        public async Task<ScriptSocket> Join(string code, bool invisible = false)
        {
            var s = new ScriptSocket();
            Runs.Add(Hub.Run(s, Auth.Check(code)!, code, null, "hub/test", "1.1.1." + (Runs.Count + 1), Cts.Token, invisible));
            await Settle();
            return s;
        }
        public async Task<ScriptSocket> JoinGuest(string name)
        {
            var s = new ScriptSocket();
            Runs.Add(Hub.Run(s, new Identity(null!, false, "guest"), "guest-code", name, "hub/test", "2.2.2." + (Runs.Count + 1), Cts.Token));
            await Settle();
            return s;
        }
        // set + wait; the rate limit is one per 2 s, so every helper call moves the fake clock on
        public async Task Set(ScriptSocket s, string json) { Clock.Advance(TimeSpan.FromSeconds(3)); s.Say(json); await Settle(); }
        public async Task Play(ScriptSocket s, string name, string cls = "Hunter", int level = 20)
        {
            Clock.Advance(TimeSpan.FromSeconds(1));
            s.Say($"{{\"t\":\"game\",\"playing\":true,\"name\":\"{name}\",\"class\":\"{cls}\",\"classFile\":\"{cls.ToUpperInvariant()}\",\"race\":\"Dwarf\",\"raceFile\":\"Dwarf\",\"level\":{level}}}");
            await Settle();
        }
        public async Task<JsonObject> Get(ScriptSocket asker, string name)
        {
            asker.Clear(); asker.Say($"{{\"t\":\"profile:get\",\"name\":\"{name}\"}}"); await Settle();
            return asker.Profile();
        }
        public static Task Settle() => Task.Delay(120);
        public void Dispose() { Cts.Cancel(); try { Task.WhenAll(Runs).Wait(2000); } catch { } Dir.Dispose(); }
    }

    static IEnumerable<string?> CharNames(JsonObject p) => ((JsonArray)p["chars"]!).Select(c => (string?)c!["name"]);

    // ---------------------------------------------------------------- validation

    [Fact]
    public async Task Only_whitelisted_avatars_and_accents_are_accepted_and_nothing_changes_on_a_bad_message()
    {
        using var l = new Lodge3();
        var mia = await l.Join(MiaCode);
        await l.Set(mia, "{\"t\":\"profile:set\",\"avatarId\":\"av.wolf\",\"accent\":\"azure\"}");
        Assert.Equal("av.wolf", (string?)mia.Profile()["avatarId"]);
        Assert.Equal("azure", (string?)mia.Profile()["accent"]);

        foreach (var bad in new[]
        {
            "\"avatarId\":\"av.dragon\"", "\"avatarId\":\"../../etc/passwd\"", "\"avatarId\":5", "\"avatarId\":[\"av.owl\"]",
            "\"accent\":\"#ff0000\"", "\"accent\":{}", "\"showChars\":\"everything\"", "\"visibleTo\":\"nobody\"", "\"about\":42", "\"playTimes\":true",
            "\"hidden\":\"x\"", "\"hidden\":[1,2]", "\"main\":7",
        })
        {
            mia.Clear();
            await l.Set(mia, $"{{\"t\":\"profile:set\",\"{(bad.Contains("about") ? "playTimes" : "about")}\":\"changed\",{bad}}}");
            Assert.True(mia.Of("error").Count > 0, bad);                                         // refused...
            Assert.Empty(mia.Of("profile:data"));
        }
        Assert.Equal("av.wolf", l.Hub.Members.First(m => m.Name == "Mia").Profile.AvatarId);   // ...as a whole: nothing applied
        Assert.Equal("", l.Hub.Members.First(m => m.Name == "Mia").Profile.About);

        await l.Set(mia, "{\"t\":\"profile:set\",\"avatarId\":null,\"accent\":\"\"}");          // null / empty = back to the default
        Assert.Null(mia.Profile()["avatarId"]);
        Assert.Null(mia.Profile()["accent"]);
        Assert.Equal(24, ProfileRules.Avatars.Length);
        Assert.Equal(8, ProfileRules.Accents.Length);
    }

    [Fact]
    public async Task About_and_play_times_are_normalized_and_bounded()
    {
        using var l = new Lodge3();
        var mia = await l.Join(MiaCode);
        var about = "Hello\u0001 ​world\n\n  line two " + new string('x', 300);
        await l.Set(mia, JsonNode.Parse("{}")!.AsObject().Also(o => { o["t"] = "profile:set"; o["about"] = about; o["playTimes"] = "evenings\u0007 CET " + new string('y', 80); }).ToJsonString());
        var p = mia.Profile();
        var a = (string)p["about"]!;
        Assert.True(a.Length <= ProfileRules.MaxAbout);
        Assert.StartsWith("Hello world line two", a, StringComparison.Ordinal);       // control/format gone, whitespace collapsed
        Assert.DoesNotContain("\u0001", a, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", a, StringComparison.Ordinal);
        var pt = (string)p["playTimes"]!;
        Assert.True(pt.Length <= ProfileRules.MaxPlayTimes);
        Assert.StartsWith("evenings CET", pt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Main_must_be_a_seen_character_and_is_never_hidden()
    {
        using var l = new Lodge3();
        var mia = await l.Join(MiaCode);
        await l.Play(mia, "Miahunter"); await l.Play(mia, "Miamage", "Mage", 12);
        await l.Set(mia, "{\"t\":\"profile:set\",\"main\":\"Nobody\"}");
        Assert.NotEmpty(mia.Of("error"));
        mia.Clear();
        await l.Set(mia, "{\"t\":\"profile:set\",\"main\":\"miahunter\",\"hidden\":[\"Miahunter\",\"Miamage\",\"Ghost\"]}");
        var p = mia.Profile();
        Assert.Equal("Miahunter", (string?)p["main"]);                                // matched case-insensitively, stored as seen
        var chars = ((JsonArray)p["chars"]!).ToDictionary(c => (string)c!["name"]!, c => (bool)c!["hidden"]!);
        Assert.False(chars["Miahunter"]);                                              // main can't be hidden
        Assert.True(chars["Miamage"]);
        Assert.False(chars.ContainsKey("Ghost"));                                      // unknown names are dropped
    }

    // ---------------------------------------------------------------- characters

    [Fact]
    public async Task Characters_are_collected_from_presence_bounded_to_12_keeping_the_most_recent()
    {
        using var l = new Lodge3();
        var mia = await l.Join(MiaCode);
        for (int i = 1; i <= 15; i++) await l.Play(mia, "Char" + i, level: i);
        await l.Play(mia, "char5", level: 33);                                         // an old one comes back: refreshed, not duplicated
        var p = await l.Get(mia, "Mia");
        var names = CharNames(p).ToList();
        Assert.Equal(12, names.Count);
        Assert.Contains("Char4", names);
        Assert.DoesNotContain("Char1", names);                                         // the oldest were dropped
        Assert.DoesNotContain("Char3", names);
        Assert.Equal(33, (int)((JsonArray)p["chars"]!).First(c => (string?)c!["name"] == "Char5")!["level"]!);
        Assert.Equal("Char5", names[0]);                                               // newest first, name keeps its first spelling
        // {share:false} and not-playing presence record nothing
        mia.Say("{\"t\":\"game\",\"share\":false}"); mia.Say("{\"t\":\"game\",\"playing\":false,\"name\":\"Idle\"}");
        await Lodge3.Settle();
        Assert.DoesNotContain("Idle", CharNames(await l.Get(mia, "Mia")));
    }

    // ---------------------------------------------------------------- privacy

    [Fact]
    public async Task Show_characters_modes_and_hidden_characters_are_enforced_for_others_but_not_for_the_owner_of_the_profile()
    {
        using var l = new Lodge3();
        var mia = await l.Join(MiaCode); var zed = await l.Join(ZedCode);
        await l.Play(mia, "Alpha"); await l.Play(mia, "Beta"); await l.Play(mia, "Gamma");
        await l.Set(mia, "{\"t\":\"profile:set\",\"main\":\"Beta\",\"hidden\":[\"Gamma\"],\"about\":\"hi\"}");

        var all = await l.Get(zed, "Mia");
        Assert.Equal(new[] { "Beta", "Alpha" }.OrderBy(x => x), CharNames(all).OrderBy(x => x));    // Gamma stays hidden
        Assert.Equal("Beta", (string?)all["main"]);
        Assert.Null(((JsonArray)all["chars"]!).First()!["hidden"]);                    // others never learn the flag
        Assert.Null(all["showChars"]);

        await l.Set(mia, "{\"t\":\"profile:set\",\"showChars\":\"main\"}");
        Assert.Equal(new[] { "Beta" }, CharNames(await l.Get(zed, "Mia")));
        await l.Set(mia, "{\"t\":\"profile:set\",\"showChars\":\"none\"}");
        var none = await l.Get(zed, "Mia");
        Assert.Empty(CharNames(none));
        Assert.Null(none["main"]);
        Assert.DoesNotContain("Gamma", none["chars"]!.ToJsonString(), StringComparison.Ordinal);
        var own = await l.Get(mia, "Mia");                                             // the owner sees everything, flags included
        Assert.Equal(3, CharNames(own).Count());
        Assert.Equal("none", (string?)own["showChars"]);
    }

    [Fact]
    public async Task Profile_visible_to_officers_leaves_others_only_avatar_and_accent()
    {
        using var l = new Lodge3();
        var mia = await l.Join(MiaCode); var zed = await l.Join(ZedCode); var bob = await l.Join(T.BobCode);
        await l.Play(mia, "Alpha");
        mia.Say("{\"t\":\"game\",\"playing\":true,\"name\":\"Alpha\",\"class\":\"Hunter\",\"zone\":\"Secret Cave\",\"level\":20}"); await Lodge3.Settle();
        await l.Set(mia, "{\"t\":\"profile:set\",\"avatarId\":\"av.owl\",\"about\":\"private words\",\"playTimes\":\"nights\",\"visibleTo\":\"officers\"}");

        var asZed = await l.Get(zed, "Mia");
        Assert.True((bool)asZed["restricted"]!);
        Assert.Equal("av.owl", (string?)asZed["avatarId"]);
        Assert.Null(asZed["about"]); Assert.Null(asZed["playTimes"]); Assert.Null(asZed["chars"]); Assert.Null(asZed["game"]);
        Assert.DoesNotContain("private words", asZed.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Secret Cave", asZed.ToJsonString(), StringComparison.Ordinal);

        var asBob = await l.Get(bob, "Mia");                                           // officer+
        Assert.False((bool)asBob["restricted"]!);
        Assert.Equal("private words", (string?)asBob["about"]);
        Assert.Equal("Secret Cave", (string?)asBob["game"]!["zone"]);
    }

    [Fact]
    public async Task Unknown_names_are_not_found_and_offline_members_still_have_a_profile()
    {
        using var l = new Lodge3();
        var mia = await l.Join(MiaCode);
        await l.Play(mia, "Alpha");
        await l.Set(mia, "{\"t\":\"profile:set\",\"avatarId\":\"av.bear\",\"about\":\"still here\"}");
        mia.Hangup(); await Lodge3.Settle();
        var zed = await l.Join(ZedCode);
        var p = await l.Get(zed, "mia");                                               // case-insensitive, offline
        Assert.True((bool)p["found"]!);
        Assert.False((bool)p["online"]!);
        Assert.Equal("av.bear", (string?)p["avatarId"]);
        Assert.Equal("still here", (string?)p["about"]);
        Assert.Null(p["game"]);
        Assert.False((bool)(await l.Get(zed, "Stranger"))["found"]!);
        Assert.False((bool)(await l.Get(zed, ""))["found"]!);
    }

    // ---------------------------------------------------------------- invisible

    [Fact]
    public async Task An_invisible_member_looks_offline_to_profile_get_for_everyone_who_may_not_see_them()
    {
        using var l = new Lodge3();
        var bob = await l.Join(T.BobCode);                                             // officer: can't see invisible
        var elan = await l.Join(T.ElanCode);                                           // owner
        var mia = await l.Join(MiaCode, invisible: true);
        mia.Say("{\"t\":\"game\",\"playing\":true,\"name\":\"Alpha\",\"class\":\"Hunter\",\"zone\":\"Hidden Zone\",\"level\":30}"); await Lodge3.Settle();
        await l.Set(mia, "{\"t\":\"profile:set\",\"avatarId\":\"av.skull\",\"about\":\"boo\",\"main\":\"Alpha\"}");

        var asBob = await l.Get(bob, "Mia");
        Assert.False((bool)asBob["online"]!);                                          // exactly what an offline member looks like
        Assert.Null(asBob["game"]);
        Assert.DoesNotContain("Hidden Zone", asBob.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal("boo", (string?)asBob["about"]);                                  // the stored profile is still readable
        Assert.False(asBob.ContainsKey("game"));
        Assert.False((bool)(await l.Get(bob, "Zed"))["online"]!);                      // same shape as a genuinely offline member

        var asElan = await l.Get(elan, "Mia");                                         // the owner sees them online, with presence
        Assert.True((bool)asElan["online"]!);
        Assert.Equal("Hidden Zone", (string?)asElan["game"]!["zone"]);

        bob.Clear();                                                                    // and an avatar change is never announced to Bob
        await l.Set(mia, "{\"t\":\"profile:set\",\"avatarId\":\"av.gem\"}");
        Assert.Empty(bob.Of("profile"));
        Assert.Single(elan.Of("profile"));
    }

    [Fact]
    public async Task An_invisible_guest_is_not_found()
    {
        using var l = new Lodge3();
        var bob = await l.Join(T.BobCode);
        var g = await l.JoinGuest("Wanderer");
        g.Say("{\"t\":\"status\",\"status\":\"online\",\"note\":\"\",\"visibility\":\"invisible\"}"); await Lodge3.Settle();
        Assert.False((bool)(await l.Get(bob, "Wanderer"))["found"]!);
    }

    // ---------------------------------------------------------------- live updates / welcome

    [Fact]
    public async Task Avatar_changes_are_broadcast_and_welcome_carries_avatar_and_accent_of_each_member()
    {
        using var l = new Lodge3();
        var bob = await l.Join(T.BobCode);
        var mia = await l.Join(MiaCode);
        bob.Clear();
        await l.Set(mia, "{\"t\":\"profile:set\",\"avatarId\":\"av.sword\",\"accent\":\"crimson\"}");
        var up = Assert.Single(bob.Of("profile"));
        Assert.Equal("Mia", (string?)up["name"]);
        Assert.Equal("av.sword", (string?)up["avatarId"]);
        Assert.Equal("crimson", (string?)up["accent"]);
        bob.Clear();
        await l.Set(mia, "{\"t\":\"profile:set\",\"about\":\"text only\"}");            // text isn't broadcast
        Assert.Empty(bob.Of("profile"));

        var zed = await l.Join(ZedCode);
        var miaEntry = ((JsonArray)zed.Of("welcome")[0]["users"]!).First(u => (string?)u!["name"] == "Mia")!;
        Assert.Equal("av.sword", (string?)miaEntry["avatarId"]);
        Assert.Equal("crimson", (string?)miaEntry["accent"]);
        Assert.NotNull(zed.Of("welcome")[0]["profile"]);                                // and the member's own profile
    }

    // ---------------------------------------------------------------- budget

    [Fact]
    public async Task Profile_changes_are_rate_limited_to_one_per_two_seconds_and_spend_the_control_budget()
    {
        using var l = new Lodge3();
        var mia = await l.Join(MiaCode);
        mia.Say("{\"t\":\"profile:set\",\"about\":\"one\"}");
        mia.Say("{\"t\":\"profile:set\",\"about\":\"two\"}");
        await Lodge3.Settle();
        Assert.Single(mia.Of("profile:data"));
        Assert.Contains(mia.Of("error"), e => ((string)e["text"]!).Contains("Slow down"));
        Assert.Equal("one", l.Hub.Members.First(m => m.Name == "Mia").Profile.About);
        l.Clock.Advance(TimeSpan.FromSeconds(2.5));
        mia.Say("{\"t\":\"profile:set\",\"about\":\"three\"}"); await Lodge3.Settle();
        Assert.Equal("three", l.Hub.Members.First(m => m.Name == "Mia").Profile.About);

        // get/reset are plain control messages: a flood of them is dropped by the Control bucket, not served
        mia.Clear();
        for (int i = 0; i < 80; i++) mia.Say("{\"t\":\"profile:get\",\"name\":\"Zed\"}");
        await Lodge3.Settle(); await Lodge3.Settle();
        Assert.True(mia.Of("profile:data").Count < 60);
    }

    // ---------------------------------------------------------------- persistence

    [Fact]
    public async Task Profiles_survive_a_restart_and_are_written_atomically()
    {
        var dir = new TempDir();
        using (var l = new Lodge3(dir))
        {
            var mia = await l.Join(MiaCode);
            await l.Play(mia, "Alpha"); await l.Play(mia, "Beta");
            await l.Set(mia, "{\"t\":\"profile:set\",\"avatarId\":\"av.raptor\",\"accent\":\"violet\",\"about\":\"persisted\",\"playTimes\":\"evenings CET\",\"main\":\"Beta\",\"hidden\":[\"Alpha\"],\"showChars\":\"main\",\"visibleTo\":\"officers\"}");
            Assert.True(File.Exists(Path.Combine(dir.Path, "profiles.json")));
            Assert.False(File.Exists(Path.Combine(dir.Path, "profiles.json.tmp")));    // renamed into place
            l.Cts.Cancel();
            try { await Task.WhenAll(l.Runs).WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            l.Dir = new TempDir();                                                      // keep `dir` alive past Dispose
        }
        using var again = new Lodge3(dir);                                              // "restart"
        var mia2 = await again.Join(MiaCode);
        var p = mia2.Of("welcome")[0]["profile"]!.AsObject();
        Assert.Equal("av.raptor", (string?)p["avatarId"]);
        Assert.Equal("violet", (string?)p["accent"]);
        Assert.Equal("persisted", (string?)p["about"]);
        Assert.Equal("evenings CET", (string?)p["playTimes"]);
        Assert.Equal("Beta", (string?)p["main"]);
        Assert.Equal("main", (string?)p["showChars"]);
        Assert.Equal("officers", (string?)p["visibleTo"]);
        Assert.Equal(2, ((JsonArray)p["chars"]!).Count);
        Assert.True((bool)((JsonArray)p["chars"]!).First(c => (string?)c!["name"] == "Alpha")!["hidden"]!);
        dir.Dispose();
    }

    [Fact]
    public void Loading_is_defensive_leftover_tmp_recovered_garbage_sanitized_oversize_ignored()
    {
        using var d = new TempDir();
        var path = Path.Combine(d.Path, "profiles.json");
        // crash after writing the tmp file but before the rename: it is used when the main file is missing
        File.WriteAllText(path + ".tmp", "{\"mia\":{\"avatarId\":\"av.owl\",\"about\":\"from tmp\"}}");
        var s1 = new ProfileStore(d.Path, T.Log, new FakeClock());
        Assert.Equal("from tmp", s1.Peek("Mia")!.About);
        Assert.False(File.Exists(path + ".tmp"));

        // an edited file cannot smuggle values past the whitelists and bounds
        File.WriteAllText(path, JsonNode.Parse("""
            {"mia":{"avatarId":"javascript:alert(1)","accent":"red","about":"ok\u0001ok","playTimes":42,"showChars":"x","visibleTo":"y",
                    "hidden":["Ghost",7],"main":"Nobody",
                    "chars":[{"name":"A","level":9999},{"name":"A","level":2},{"name":""},"junk",{"name":"B"}]},
             "NotNormal  Key":{"about":"dropped: key isn't in normal form"},
             "bob":"not an object"}
            """)!.ToJsonString());
        var s2 = new ProfileStore(d.Path, T.Log, new FakeClock());
        var p = s2.Peek("mia")!;
        Assert.Null(p.AvatarId); Assert.Null(p.Accent); Assert.Equal("okok", p.About); Assert.Equal("", p.PlayTimes);
        Assert.Equal("all", p.ShowChars); Assert.Equal("everyone", p.VisibleTo);
        Assert.Null(p.Main); Assert.Empty(p.Hidden);
        Assert.Equal(2, p.Chars.Count);                                                 // A (deduplicated) and B
        Assert.All(p.Chars, c => Assert.InRange(c.Level, 0, 100));
        Assert.Null(s2.Peek("NotNormal  Key")); Assert.Null(s2.Peek("bob"));

        // a file over the size cap is ignored (not read into memory)
        File.WriteAllBytes(path, new byte[9 * 1024 * 1024]);
        Assert.Equal(0, new ProfileStore(d.Path, T.Log, new FakeClock()).Count);
        File.WriteAllText(path, "{ not json");
        Assert.Equal(0, new ProfileStore(d.Path, T.Log, new FakeClock()).Count);
    }

    [Fact]
    public async Task Guests_get_a_session_only_profile_that_is_never_stored()
    {
        using var l = new Lodge3();
        var g = await l.JoinGuest("Wanderer");
        await l.Play(g, "Gchar");
        await l.Set(g, "{\"t\":\"profile:set\",\"avatarId\":\"av.fish\",\"about\":\"just passing\"}");
        Assert.Equal("av.fish", (string?)g.Profile()["avatarId"]);
        var bob = await l.Join(T.BobCode);
        Assert.Equal("av.fish", (string?)(await l.Get(bob, "Wanderer"))["avatarId"]);   // visible while they are here
        Assert.Null(l.Hub.Profiles.Peek("Wanderer"));                                   // but nothing in the store...
        l.Hub.Profiles.Flush();
        var disk = File.ReadAllText(Path.Combine(l.Dir.Path, "profiles.json"));          // ...or on disk
        Assert.DoesNotContain("av.fish", disk, StringComparison.Ordinal);
        Assert.DoesNotContain("wanderer", disk, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Gchar", disk, StringComparison.Ordinal);
        g.Hangup(); await Lodge3.Settle();
        Assert.False((bool)(await l.Get(bob, "Wanderer"))["found"]!);                   // gone with the session
        var g2 = await l.JoinGuest("Wanderer");
        Assert.Null(g2.Of("welcome")[0]["profile"]!["avatarId"]);
    }

    // ---------------------------------------------------------------- owner reset

    [Fact]
    public async Task Only_the_owner_rank_can_reset_an_avatar_and_it_persists_and_is_broadcast()
    {
        using var l = new Lodge3();
        var bob = await l.Join(T.BobCode); var elan = await l.Join(T.ElanCode); var mia = await l.Join(MiaCode);
        await l.Set(mia, "{\"t\":\"profile:set\",\"avatarId\":\"av.skull\",\"accent\":\"rose\",\"about\":\"rude text\"}");
        bob.Clear();
        bob.Say("{\"t\":\"profile:reset\",\"name\":\"Mia\"}"); await Lodge3.Settle();
        Assert.NotEmpty(bob.Of("error"));                                               // officers can't
        Assert.Equal("av.skull", l.Hub.Members.First(m => m.Name == "Mia").Profile.AvatarId);

        mia.Clear(); bob.Clear();
        elan.Say("{\"t\":\"profile:reset\",\"name\":\"mia\",\"clearText\":true}"); await Lodge3.Settle();
        var prof = l.Hub.Members.First(m => m.Name == "Mia").Profile;
        Assert.Null(prof.AvatarId); Assert.Null(prof.Accent); Assert.Equal("", prof.About);
        Assert.Null(mia.Profile()["avatarId"]);                                          // she is told
        Assert.Null(Assert.Single(bob.Of("profile"))["avatarId"]);                       // and the lodge
        Assert.Null(l.Hub.Profiles.Peek("Mia")!.AvatarId);

        // offline member: still resettable (the stored profile), unknown names are refused
        await l.Set(mia, "{\"t\":\"profile:set\",\"avatarId\":\"av.gem\"}");
        mia.Hangup(); await Lodge3.Settle();
        elan.Clear(); elan.Say("{\"t\":\"profile:reset\",\"name\":\"Mia\"}"); await Lodge3.Settle();
        Assert.Null(l.Hub.Profiles.Peek("Mia")!.AvatarId);
        elan.Clear(); elan.Say("{\"t\":\"profile:reset\",\"name\":\"Stranger\"}"); await Lodge3.Settle();
        Assert.NotEmpty(elan.Of("error"));
    }

    [Fact]
    public void Owner_is_the_only_rank_with_ResetProfiles()
    {
        Assert.True(Roles.Can("owner", Perm.ResetProfiles));
        Assert.False(Roles.Can("officer", Perm.ResetProfiles));
        Assert.False(Roles.Can("member", Perm.ResetProfiles));
    }
}

static class ObjExt
{
    public static T Also<T>(this T x, Action<T> f) { f(x); return x; }
}
