using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Lodge.Tests;

// Lodge 2.8: file names, text spoofing, guest look-alikes, upload abuse, malformed input, permissions, voice cap,
// server-mute, idle timeout, debounced history writes.
public class HardeningTests
{
    // a socket the test drives: Say() queues a client frame, Sent holds what the server sent
    sealed class Sock : WebSocket
    {
        readonly System.Threading.Channels.Channel<byte[]> incoming = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
        WebSocketState state = WebSocketState.Open;
        public readonly List<JsonObject> Sent = new();
        public WebSocketCloseStatus? Status; public string? Reason;
        public override WebSocketCloseStatus? CloseStatus => state == WebSocketState.Open ? null : Status ?? WebSocketCloseStatus.PolicyViolation;
        public override string? CloseStatusDescription => Reason;
        public override WebSocketState State => state;
        public override string? SubProtocol => null;
        public override void Abort() { state = WebSocketState.Aborted; incoming.Writer.TryComplete(); }
        public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken ct) { Status = s; Reason = d; state = WebSocketState.Closed; incoming.Writer.TryComplete(); return Task.CompletedTask; }
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
    }

    const string MiaCode = "synthetic0mia00code0000000000003";
    const string GuestCode = "synthetic-guest-code";

    sealed class Rig : IDisposable
    {
        public readonly TempDir Dir = new();
        public readonly FakeClock Clock = new();
        public readonly CancellationTokenSource Cts = new();
        public readonly List<Task> Runs = new();
        public LodgeHub Hub = null!;
        public Auth Auth = null!;
        int ips;

        public Rig(Action<LodgeConfig>? tweak = null)
        {
            var cfg = new LodgeConfig
            {
                Code = GuestCode, CodesFile = Path.Combine(Dir.Path, "codes"), Urls = "", DataDir = Dir.Path, PathBase = "", MaxFileMb = 25, FileDays = 30,
                MaxUsers = 25, TrustedProxies = Array.Empty<string>(), MaxStorageBytes = 1 << 30, MaxFiles = 100, MaxConcurrentUploads = 4,
                UploadsPer10Min = 30, HistoryMaxBytes = 1 << 20, MaxTrackedIps = 1000, HttpPerMinute = 240, AllowQueryCode = false,
            };
            tweak?.Invoke(cfg);
            T.WriteCodes(cfg, $"Elan:{T.ElanCode}:owner", $"Bob:{T.BobCode}:officer", $"Mia:{MiaCode}:member");
            Auth = new Auth(cfg, T.Log);
            Hub = new LodgeHub(cfg, Auth, new FileStore(cfg), T.Log, Clock);
        }

        public async Task<Sock> Join(string code, string? name = null, string? ip = null)
        {
            var s = new Sock();
            Runs.Add(Hub.Run(s, Auth.Check(code)!, code, name, "hub/test", ip ?? "9.9.9." + (++ips), Cts.Token));
            await Settle();
            return s;
        }

        public Member Me(string name) => Hub.Members.First(m => m.Name == name);

        public static Task Settle() => Task.Delay(120);
        public void Dispose() { Cts.Cancel(); try { Task.WhenAll(Runs).Wait(2000); } catch { } Dir.Dispose(); }
    }

    static string Name(JsonObject welcome) => (string)welcome["name"]!;

    // ---------------------------------------------------------------- file names

    [Theory]
    [InlineData("..\\..\\Startup\\x.bat", "x.bat")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("C:\\Users\\x\\evil.exe", "evil.exe")]
    [InlineData("C:evil.exe", "C_evil.exe")]
    [InlineData("/etc/cron.d/job", "job")]
    [InlineData("a|b<c>d\"e:f*g?h.txt", "a_b_c_d_e_f_g_h.txt")]
    [InlineData("  ...hidden.txt  ", "hidden.txt")]
    [InlineData("name.txt. . ", "name.txt")]
    [InlineData("", "file")]
    [InlineData("....", "file")]
    [InlineData("CON", "_CON")]
    [InlineData("nul.txt", "_nul.txt")]
    [InlineData("COM1.log", "_COM1.log")]
    [InlineData("lpt9", "_lpt9")]
    [InlineData("COM10.txt", "COM10.txt")]
    [InlineData("console.txt", "console.txt")]
    [InlineData("photo\u202Egnp.exe", "photognp.exe")]          // right-to-left override: gone
    [InlineData("a\u200Bb\u200Fc\uFEFF.txt", "abc.txt")]        // zero-width and format characters
    [InlineData("a\u0000b\u0007c.txt", "abc.txt")]
    [InlineData("Ｃ：＼ｘ.exe", "x.exe")]                       // fullwidth drive/backslash fold under NFKC, then the path part goes
    public void File_names_are_safe_everywhere(string asked, string expected) => Assert.Equal(expected, Names.FileName(asked));

    [Fact]
    public void Long_file_names_keep_their_extension_and_stay_bounded()
    {
        var n = Names.FileName(new string('a', 300) + ".pdf");
        Assert.True(n.Length <= 120);
        Assert.EndsWith(".pdf", n);
        Assert.Equal("report 2026.pdf", Names.FileName("report      2026.pdf"));  // space runs collapse (no fake gaps before an extension)
    }

    [Fact]
    public async Task Uploads_are_stored_under_the_clean_name()
    {
        using var d = new TempDir();
        var cfg = LodgeConfig.ForTests(d.Path);
        var s = new FileStore(cfg, new FakeClock());
        var meta = await s.Save(new T.SlowStream(10), 10, "..\\..\\Startup\\run|me.bat", "Elan", "p:elan", CancellationToken.None);
        Assert.Equal("run_me.bat", meta.Name);
        Assert.Equal("run_me.bat", s.Get(meta.Id)!.Name);
    }

    [Fact]
    public void Names_keep_emoji_and_drop_only_unpaired_surrogates()
    {
        Assert.Equal("Raid \U0001F525 night", Names.Normalize("Raid \U0001F525 night"));
        Assert.Equal("ab", Names.Normalize("a\uD83Db"));            // lone high surrogate
        Assert.Equal("ab", Names.Normalize("a\uDE00b"));            // lone low surrogate
        var cut = Names.Snippet(new string('x', 79) + "\U0001F525more", 80);
        Assert.False(char.IsHighSurrogate(cut.TrimEnd('…')[^1]));
        Assert.Equal("Søren", Names.Normalize("Søren"));
    }

    // ---------------------------------------------------------------- message text

    [Fact]
    public void Message_text_loses_bidi_controls_and_blank_line_floods()
    {
        Assert.Equal("evil.exe", Names.MessageText("\u202Eexe.live".Replace("exe.live", "evil.exe")));
        Assert.Equal("ab", Names.MessageText("a\u2066b\u2069"));
        Assert.Equal("a\u200Bb", Names.MessageText("a\u200Bb"));       // plain zero-width stays (only bidi overrides/isolates go)
        Assert.Equal("a\n\n\n\nb", Names.MessageText("a" + new string('\n', 30) + "b"));
        Assert.Equal("a\n \n\t\nb", Names.MessageText("a\n \n\t\nb"));    // 2 blank lines: kept as typed
        Assert.Equal("a\n\n\n\nb", Names.MessageText("a\r\n\r\n\r\n\r\n\r\n\r\nb"));
    }

    [Fact]
    public async Task Only_officers_can_ping_everyone()
    {
        using var r = new Rig();
        var elan = await r.Join(T.ElanCode); var bob = await r.Join(T.BobCode); var mia = await r.Join(MiaCode);
        mia.Say("{\"t\":\"msg\",\"channel\":\"general\",\"text\":\"hello @everyone and @here\"}");
        await Rig.Settle();
        var got = (string)elan.Of("msg").Last()["text"]!;
        var id = (string)elan.Of("msg").Last()["id"]!;
        Assert.DoesNotContain("@everyone", got, StringComparison.Ordinal);
        Assert.DoesNotContain("@here", got, StringComparison.Ordinal);
        Assert.Contains("everyone", got);                               // the text is still readable
        r.Clock.Advance(TimeSpan.FromSeconds(5));
        bob.Say("{\"t\":\"msg\",\"channel\":\"general\",\"text\":\"@everyone raid\"}");
        await Rig.Settle();
        Assert.Equal("@everyone raid", (string)elan.Of("msg").Last()["text"]!);
        // an edit can't smuggle the ping in either
        mia.Say("{\"t\":\"edit\",\"id\":\"" + id + "\",\"text\":\"now @EVERYONE\"}");
        await Rig.Settle();
        Assert.DoesNotContain("@EVERYONE", (string)elan.Of("edited").Last()["text"]!, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- guests

    [Fact]
    public async Task Guests_always_carry_the_tag_and_cant_look_like_anyone()
    {
        using var r = new Rig();
        // Cyrillic "Е" (U+0415) and "а" (U+0430) instead of Latin E, a
        foreach (var asked in new[] { "\u0415l\u0430n", "ELAN", "Ｅｌａｎ", "Elan (guest)", "bob", "\u0392ob" })
        {
            var g = await r.Join(GuestCode, asked);
            var n = Name(g.Of("welcome")[0]);
            Assert.EndsWith("(guest)", n);
            Assert.False(Names.Alike(n, "Elan"), n);
            Assert.False(Names.Alike(n, "Bob"), n);
            g.Hangup();
            await Rig.Settle();
        }
    }

    [Fact]
    public async Task Guests_cant_take_a_name_that_looks_like_a_display_name_or_a_present_guest()
    {
        using var r = new Rig();
        var mia = await r.Join(MiaCode);
        mia.Say("{\"t\":\"profile:set\",\"displayName\":\"Starlight (guest)x\"}"); // invalid: parentheses - refused
        await Rig.Settle();
        r.Clock.Advance(TimeSpan.FromSeconds(3));
        mia.Say("{\"t\":\"profile:set\",\"displayName\":\"Starlight\"}");
        await Rig.Settle();
        Assert.Equal("Starlight", r.Me("Mia").Profile.DisplayName);
        var reserved = r.Hub.ReservedNames().ToList();
        Assert.Contains("Starlight", reserved);
        Assert.Contains("Elan", reserved);
        // a guest named "Starlight (guest)" with a Cyrillic "а" is the same key as one without
        var g1 = await r.Join(GuestCode, "Starlight");
        var g2 = await r.Join(GuestCode, "Starlight");
        var g3 = await r.Join(GuestCode, "Starl\u0456ght");  // Cyrillic і
        var names = new[] { Name(g1.Of("welcome")[0]), Name(g2.Of("welcome")[0]), Name(g3.Of("welcome")[0]) };
        Assert.Equal(3, names.Select(Names.Key).Distinct().Count());
    }

    [Fact]
    public async Task Invite_names_may_use_unicode_letters_but_not_lookalikes_of_existing_names()
    {
        using var r = new Rig();
        var code = r.Auth.Add("Søren", "member");
        Assert.NotNull(r.Auth.Check(code));
        Assert.Equal("Søren", r.Auth.Check(code)!.Name);
        var ex = Assert.Throws<InvalidOperationException>(() => r.Auth.Add("\u0415lan", "member")); // Cyrillic Е
        Assert.Contains("already", ex.Message);
        Assert.Throws<InvalidOperationException>(() => r.Auth.Add("bad:name", "member"));
        await Task.CompletedTask;
    }

    // ---------------------------------------------------------------- uploads

    static LodgeConfig Cfg(TempDir d, Action<LodgeConfig>? tweak = null)
    {
        var c = new LodgeConfig
        {
            Code = "", CodesFile = Path.Combine(d.Path, "codes"), Urls = "", DataDir = d.Path, PathBase = "", MaxFileMb = 25, FileDays = 30,
            MaxUsers = 25, TrustedProxies = Array.Empty<string>(), MaxStorageBytes = 1 << 30, MaxFiles = 100, MaxConcurrentUploads = 4,
            UploadsPer10Min = 30, HistoryMaxBytes = 1 << 20, MaxTrackedIps = 1000, HttpPerMinute = 240, AllowQueryCode = false,
        };
        tweak?.Invoke(c);
        return c;
    }

    [Fact]
    public async Task One_identity_can_hold_two_upload_slots_not_all_four()
    {
        using var d = new TempDir();
        var s = new FileStore(Cfg(d), new FakeClock());
        Task<FileMeta> Up(string who) => s.Save(new T.SlowStream(2 << 20, delayMs: 30), 2 << 20, "a.bin", "x", who, CancellationToken.None);
        var a = Up("p:elan"); var b = Up("p:elan");
        await Task.Delay(60);
        var e = await Assert.ThrowsAsync<UploadRefusedException>(() => Up("p:elan"));
        Assert.Equal(429, e.Status);
        var other = Up("p:bob");                      // another person still gets a slot of the four
        await Task.WhenAll(a, b, other);
        await Up("p:elan");                           // and the first is free again afterwards
    }

    [Fact]
    public async Task A_stalled_upload_is_cut_off_after_the_timeout_and_frees_its_slot()
    {
        using var d = new TempDir();
        var s = new FileStore(Cfg(d, c => c.UploadTimeoutSeconds = 1), new FakeClock());
        var e = await Assert.ThrowsAsync<UploadRefusedException>(() =>
            s.Save(new T.SlowStream(5 << 20, chunk: 1024, delayMs: 200), 5 << 20, "a.bin", "x", "p:elan", CancellationToken.None));
        Assert.Equal(408, e.Status);
        Assert.Equal(0, s.ReservedBytes);
        await s.Save(new T.SlowStream(10), 10, "b.bin", "x", "p:elan", CancellationToken.None);   // slot and identity count are free
    }

    // ---------------------------------------------------------------- malformed input

    [Fact]
    public async Task Malformed_messages_never_close_the_connection()
    {
        using var r = new Rig();
        var elan = await r.Join(T.ElanCode);
        var bad = new[]
        {
            "{\"t\":\"msg\",\"text\":5,\"channel\":{}}", "{\"t\":\"msg\",\"text\":[1],\"file\":5}", "{\"t\":\"msg\",\"text\":\"x\",\"replyTo\":{\"id\":1},\"file\":{\"id\":7}}",
            "{\"t\":\"edit\",\"id\":5,\"text\":{}}", "{\"t\":\"delete\",\"id\":[]}", "{\"t\":\"react\",\"id\":{},\"emoji\":5}", "{\"t\":\"pin\",\"id\":1}",
            "{\"t\":\"unpin\",\"id\":1,\"channel\":2}", "{\"t\":\"typing\",\"channel\":9}", "{\"t\":\"voice\",\"room\":5,\"on\":\"yes\"}", "{\"t\":\"voice\",\"on\":{}}",
            "{\"t\":\"state\",\"muted\":\"x\",\"deaf\":5}", "{\"t\":\"status\",\"status\":5,\"note\":{},\"visibility\":3}", "{\"t\":\"game\",\"level\":\"x\",\"name\":7,\"share\":\"no\"}",
            "{\"t\":\"profile:set\",\"avatarId\":5,\"hidden\":\"x\",\"displayName\":[]}", "{\"t\":\"profile:get\",\"name\":5}", "{\"t\":\"profile:reset\",\"name\":{}}",
            "{\"t\":\"admin.invite\",\"name\":5,\"role\":{}}", "{\"t\":\"admin.role\",\"name\":[],\"role\":1}", "{\"t\":\"admin.remove\",\"name\":7}",
            "{\"t\":\"admin.channel.add\",\"name\":5,\"max\":\"x\"}", "{\"t\":\"admin.channel.remove\",\"id\":5}", "{\"t\":\"mod.kick\",\"id\":\"x\"}", "{\"t\":\"mod.mute\",\"id\":[],\"on\":3}",
            "[1,2,3]", "\"just a string\"", "{\"t\":5}", "{}", "null",
        };
        foreach (var b in bad) elan.Say(b);
        await Task.Delay(300);
        elan.Clear();
        elan.Say("{\"t\":\"ping\"}");
        await Rig.Settle();
        Assert.Single(elan.Of("pong"));                       // still connected and listening
        Assert.Equal(WebSocketState.Open, elan.State);
        Assert.Equal(1, r.Hub.Online);
    }

    [Fact]
    public async Task A_handler_that_throws_is_logged_and_dropped_not_fatal()
    {
        using var r = new Rig();
        var elan = await r.Join(T.ElanCode);
        // 90000-digit number: valid JSON that the accessors must treat as "not a number", not crash on
        elan.Say("{\"t\":\"mod.kick\",\"id\":" + new string('9', 400) + "}");
        elan.Say("{\"t\":\"admin.channel.add\",\"name\":\"x\",\"max\":1e999}");
        await Task.Delay(200);
        elan.Clear();
        elan.Say("{\"t\":\"ping\"}");
        await Rig.Settle();
        Assert.Single(elan.Of("pong"));
    }

    // ---------------------------------------------------------------- edit / delete permissions

    [Fact]
    public async Task Edit_and_delete_respect_channel_visibility_authors_and_guest_identity()
    {
        using var r = new Rig();
        var elan = await r.Join(T.ElanCode); var bob = await r.Join(T.BobCode); var mia = await r.Join(MiaCode);
        var g1 = await r.Join(GuestCode, "Tom"); var g2 = await r.Join(GuestCode, "Tom");

        // an officer-only channel: Bob writes there; Mia (member) can not see it
        bob.Say("{\"t\":\"msg\",\"channel\":\"officers\",\"text\":\"secret\"}");
        await Rig.Settle();
        var secret = (string)elan.Of("msg").Last()["id"]!;
        mia.Clear();
        mia.Say("{\"t\":\"edit\",\"id\":\"" + secret + "\",\"text\":\"hacked\"}");
        mia.Say("{\"t\":\"delete\",\"id\":\"" + secret + "\"}");
        mia.Say("{\"t\":\"react\",\"id\":\"" + secret + "\",\"emoji\":\"ready\"}");
        await Rig.Settle();
        Assert.Empty(mia.Of("error"));                        // not even a hint that it exists
        Assert.Empty(elan.Of("edited")); Assert.Empty(elan.Of("deleted"));

        // Mia can't edit Bob's public message; an officer may delete it but not edit it
        r.Clock.Advance(TimeSpan.FromSeconds(5));
        bob.Say("{\"t\":\"msg\",\"channel\":\"general\",\"text\":\"hi\"}");
        await Rig.Settle();
        var hi = (string)elan.Of("msg").Last()["id"]!;
        mia.Clear();
        mia.Say("{\"t\":\"edit\",\"id\":\"" + hi + "\",\"text\":\"mine now\"}");
        mia.Say("{\"t\":\"delete\",\"id\":\"" + hi + "\"}");
        await Rig.Settle();
        Assert.Equal(2, mia.Of("error").Count);
        elan.Say("{\"t\":\"edit\",\"id\":\"" + hi + "\",\"text\":\"owner edit\"}");
        await Rig.Settle();
        Assert.Equal("You can only edit your own messages", (string)elan.Of("error").Last()["text"]!);
        elan.Say("{\"t\":\"delete\",\"id\":\"" + hi + "\"}");  // the owner can moderate
        await Rig.Settle();
        Assert.Single(elan.Of("deleted"));

        // two guests with the same chosen name: only the author's own connection may edit
        var t1 = Name(g1.Of("welcome")[0]); var t2 = Name(g2.Of("welcome")[0]);
        Assert.NotEqual(t1, t2);
        r.Clock.Advance(TimeSpan.FromSeconds(5));
        g1.Say("{\"t\":\"msg\",\"channel\":\"general\",\"text\":\"from guest one\"}");
        await Rig.Settle();
        var gm = (string)elan.Of("msg").Last()["id"]!;
        g2.Clear();
        g2.Say("{\"t\":\"edit\",\"id\":\"" + gm + "\",\"text\":\"stolen\"}");
        await Rig.Settle();
        Assert.Single(g2.Of("error"));
        g1.Say("{\"t\":\"edit\",\"id\":\"" + gm + "\",\"text\":\"fixed\"}");
        await Rig.Settle();
        Assert.Equal("fixed", (string)elan.Of("edited").Last()["text"]!);

        // reconnecting as a guest does not give the old message back (a new connection is a new identity)
        g1.Hangup(); await Rig.Settle();
        var g1b = await r.Join(GuestCode, "Tom", ip: "9.9.9.1");
        g1b.Say("{\"t\":\"edit\",\"id\":\"" + gm + "\",\"text\":\"again\"}");
        await Rig.Settle();
        Assert.Single(g1b.Of("error"));
    }

    // ---------------------------------------------------------------- voice

    [Fact]
    public async Task The_voice_room_cap_holds_under_simultaneous_joins()
    {
        using var r = new Rig();
        var cap = r.Hub.Channels.Get("dungeon")!.Max;
        Assert.Equal(5, cap);
        var socks = new List<Sock>();
        for (int i = 0; i < 12; i++) socks.Add(await r.Join(GuestCode, "G" + i));
        foreach (var s in socks) s.Say("{\"t\":\"voice\",\"room\":\"dungeon\"}");
        await Task.Delay(400);
        Assert.Equal(5, r.Hub.Members.Count(m => m.Room == "dungeon"));
        var refused = socks.SelectMany(s => s.Of("error")).Where(e => (string?)e["code"] == "room-full").ToList();
        Assert.Equal(7, refused.Count);
        Assert.All(refused, e => Assert.Equal("dungeon", (string)e["room"]!));
        // somebody leaves: the place can be taken again
        var inRoom = socks.First(s => r.Hub.Members.Any(m => m.Room == "dungeon" && m.Ws == s));
        inRoom.Say("{\"t\":\"voice\",\"room\":null}");
        await Rig.Settle();
        Assert.Equal(4, r.Hub.Members.Count(m => m.Room == "dungeon"));
    }

    [Fact]
    public async Task Server_mute_survives_a_reconnect_and_is_lifted_by_unmuting()
    {
        using var r = new Rig();
        var bob = await r.Join(T.BobCode); var mia = await r.Join(MiaCode);
        var miaId = r.Me("Mia").Id;
        bob.Say("{\"t\":\"mod.mute\",\"id\":" + miaId + ",\"on\":true}");
        await Rig.Settle();
        Assert.True(r.Me("Mia").ServerMuted);
        mia.Hangup(); await Rig.Settle();
        var mia2 = await r.Join(MiaCode);
        Assert.True(r.Me("Mia").ServerMuted);                              // still muted after coming back
        Assert.True((bool)mia2.Of("welcome")[0]["users"]!.AsArray().First(u => (string?)u!["name"] == "Mia")!["serverMuted"]!);
        bob.Say("{\"t\":\"mod.mute\",\"id\":" + r.Me("Mia").Id + ",\"on\":false}");
        await Rig.Settle();
        Assert.False(r.Me("Mia").ServerMuted);
        mia2.Hangup(); await Rig.Settle();
        await r.Join(MiaCode);
        Assert.False(r.Me("Mia").ServerMuted);                             // unmute is permanent too
    }

    // ---------------------------------------------------------------- idle timeout

    [Fact]
    public async Task A_pinging_client_that_goes_silent_is_dropped_but_old_clients_are_not()
    {
        using var r = new Rig();
        var modern = await r.Join(T.ElanCode); var old = await r.Join(T.BobCode);
        modern.Say("{\"t\":\"ping\"}");
        await Rig.Settle();
        Assert.Single(modern.Of("pong"));
        r.Clock.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(0, await r.Hub.SweepIdle());                           // 60 s of silence: still fine
        modern.Say("{\"t\":\"ping\"}");                                       // a ping resets the clock
        await Rig.Settle();
        r.Clock.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(0, await r.Hub.SweepIdle());
        r.Clock.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(1, await r.Hub.SweepIdle());                           // 80 s
        Assert.Equal(WebSocketCloseStatus.EndpointUnavailable, modern.Status); // "going away": the client reconnects at once
        Assert.Equal(1, r.Hub.Online);                                      // the client that never pings is untouched
    }

    // ---------------------------------------------------------------- history flush

    [Fact]
    public async Task Reactions_and_edits_are_written_after_a_quiet_period_not_each_time()
    {
        using var r = new Rig(c => c.HistoryFlushSeconds = 1);
        var elan = await r.Join(T.ElanCode);
        elan.Say("{\"t\":\"msg\",\"channel\":\"general\",\"text\":\"hello\"}");
        await Rig.Settle();
        var id = (string)elan.Of("msg").Last()["id"]!;
        var file = HistoryStore.FileOf(Path.Combine(r.Dir.Path, "history"), "general");
        var sizeAfterAppend = new FileInfo(file).Length;
        var stamp = File.GetLastWriteTimeUtc(file);
        foreach (var e in new[] { "ready", "love", "lol", "epic" }) elan.Say("{\"t\":\"react\",\"id\":\"" + id + "\",\"emoji\":\"" + e + "\"}");
        elan.Say("{\"t\":\"edit\",\"id\":\"" + id + "\",\"text\":\"hello again\"}");
        await Task.Delay(300);
        Assert.Equal(sizeAfterAppend, new FileInfo(file).Length);           // nothing rewritten yet
        Assert.DoesNotContain("hello again", File.ReadAllText(file));
        for (int i = 0; i < 40 && !File.ReadAllText(file).Contains("hello again"); i++) await Task.Delay(100);
        var text = File.ReadAllText(file);
        Assert.Contains("hello again", text);                               // the debounce fired
        Assert.Contains("\"epic\"", text);
        Assert.Single(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));   // one atomic rewrite holds everything
        Assert.False(File.Exists(file + ".compact.tmp"));
    }

    [Fact]
    public async Task Shutdown_flushes_pending_history_and_deletes_are_not_lost()
    {
        using var r = new Rig(c => c.HistoryFlushSeconds = 600);
        var elan = await r.Join(T.ElanCode);
        elan.Say("{\"t\":\"msg\",\"channel\":\"general\",\"text\":\"one\"}");
        await Rig.Settle();
        r.Clock.Advance(TimeSpan.FromSeconds(5));
        elan.Say("{\"t\":\"msg\",\"channel\":\"general\",\"text\":\"two\"}");
        await Rig.Settle();
        var one = (string)elan.Of("msg").First()["id"]!;
        elan.Say("{\"t\":\"delete\",\"id\":\"" + one + "\"}");
        await Rig.Settle();
        var file = HistoryStore.FileOf(Path.Combine(r.Dir.Path, "history"), "general");
        Assert.Contains("\"one\"", File.ReadAllText(file));                 // still on disk: the write is pending
        Assert.Equal(1, r.Hub.Chat.FlushAll());
        var after = File.ReadAllText(file);
        Assert.DoesNotContain("\"one\"", after);
        Assert.Contains("\"two\"", after);
        Assert.Equal(0, r.Hub.Chat.FlushAll());                             // nothing left to do
    }

    // ---------------------------------------------------------------- profiles

    [Fact]
    public async Task Profile_lookups_leave_out_last_seen_for_an_invisible_member_the_viewer_cant_see()
    {
        using var r = new Rig();
        var elan = await r.Join(T.ElanCode); var bob = await r.Join(T.BobCode); var mia = await r.Join(MiaCode);
        mia.Say("{\"t\":\"game\",\"playing\":true,\"name\":\"Miachar\",\"class\":\"Mage\",\"classFile\":\"MAGE\",\"level\":20}");
        await Rig.Settle();
        mia.Say("{\"t\":\"status\",\"status\":\"online\",\"note\":\"\",\"visibility\":\"invisible\"}");
        await Rig.Settle();
        bob.Clear(); elan.Clear();
        bob.Say("{\"t\":\"profile:get\",\"name\":\"Mia\"}");
        elan.Say("{\"t\":\"profile:get\",\"name\":\"Mia\"}");
        await Rig.Settle();
        var asBob = bob.Of("profile:data").Last()["profile"]!;
        var asOwner = elan.Of("profile:data").Last()["profile"]!;
        Assert.False((bool)asBob["online"]!);
        Assert.NotEmpty(asBob["chars"]!.AsArray());
        Assert.All(asBob["chars"]!.AsArray(), c => Assert.Null(c!["seen"]));     // "seen: now" would give her away
        Assert.True((bool)asOwner["online"]!);
        Assert.All(asOwner["chars"]!.AsArray(), c => Assert.NotNull(c!["seen"]));
    }

    [Fact]
    public async Task The_welcome_carries_the_name_directory()
    {
        using var r = new Rig();
        var mia = await r.Join(MiaCode);
        r.Clock.Advance(TimeSpan.FromSeconds(3));
        mia.Say("{\"t\":\"profile:set\",\"displayName\":\"Starlight\"}");
        await Rig.Settle();
        mia.Hangup(); await Rig.Settle();                                         // offline now
        var elan = await r.Join(T.ElanCode);
        var w = elan.Of("welcome")[0];
        Assert.Equal("Starlight", (string)w["names"]!["Mia"]!);
        Assert.Equal(LodgeHub.Version, (string)w["server"]!);
        Assert.Equal("2.18.0", LodgeHub.Version);
    }

    // ---------------------------------------------------------------- downloads / config

    [Fact]
    public void Query_codes_are_off_by_default()
    {
        var old = Environment.GetEnvironmentVariable("LODGE_ALLOW_QUERY_CODE");
        try
        {
            Environment.SetEnvironmentVariable("LODGE_ALLOW_QUERY_CODE", null);
            Environment.SetEnvironmentVariable("LODGE_DATA", Path.Combine(Path.GetTempPath(), "lodge-test-" + Guid.NewGuid().ToString("N")));
            Assert.False(LodgeConfig.FromEnv().AllowQueryCode);
            Environment.SetEnvironmentVariable("LODGE_ALLOW_QUERY_CODE", "true");
            Assert.True(LodgeConfig.FromEnv().AllowQueryCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LODGE_ALLOW_QUERY_CODE", old);
            Environment.SetEnvironmentVariable("LODGE_DATA", null);
        }
    }
}
