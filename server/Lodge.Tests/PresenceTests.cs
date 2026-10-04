using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Lodge.Tests;

// 2.5.0: rich-presence sanitizing, and "Appear offline" (invisible members).
public class PresenceTests
{
    // a socket the test drives: Say() queues a client frame, Sent holds what the server sent
    sealed class ScriptSocket : WebSocket
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
    }

    const string MemCode = "synthetic0mem00code0000000000003";
    const string Mem2Code = "synthetic0mem20code000000000004";

    sealed class Lodge2 : IDisposable
    {
        public LodgeHub Hub = null!; public Auth Auth = null!; public TempDir Dir = new();
        public CancellationTokenSource Cts = new();
        public List<Task> Runs = new();
        public Lodge2()
        {
            var cfg = LodgeConfig.ForTests(Dir.Path);
            T.WriteCodes(cfg, $"Elan:{T.ElanCode}:owner", $"Bob:{T.BobCode}:officer", $"Mia:{MemCode}:member", $"Zed:{Mem2Code}:member");
            Auth = new Auth(cfg, T.Log);
            Hub = new LodgeHub(cfg, Auth, new FileStore(cfg), T.Log, new FakeClock());
        }
        public async Task<ScriptSocket> Join(string code, bool invisible = false)
        {
            var s = new ScriptSocket();
            Runs.Add(Hub.Run(s, Auth.Check(code)!, code, null, "hub/test", "1.1.1." + (Runs.Count + 1), Cts.Token, invisible));
            await Settle();
            return s;
        }
        public static Task Settle() => Task.Delay(120);
        public void Dispose() { Cts.Cancel(); try { Task.WhenAll(Runs).Wait(2000); } catch { } Dir.Dispose(); }
    }

    static JsonObject Obj(string json) => JsonNode.Parse(json)!.AsObject();
    static IEnumerable<string?> NamesOf(JsonObject welcome) => ((JsonArray)welcome["users"]!).Select(u => (string?)u!["name"]);

    // ---------------------------------------------------------------- game presence sanitizing

    [Fact]
    public void Rich_presence_fields_are_bounded_and_whitelisted()
    {
        var g = PresenceModule.BuildGame(Obj("""
            {"playing":true,"name":"Elan","level":60,"zone":"The Barrens","flags":65535,"xpPct":62,"rested":true,
             "groupSize":5,"inInstance":true,"instanceName":"Wailing Caverns"}
            """));
        Assert.True(((int)g["flags"]! & ~PresenceModule.FlagMask) == 0);
        Assert.Equal(62, (int)g["xpPct"]!);
        Assert.True((bool)g["rested"]!);
        Assert.Equal(5, (int)g["groupSize"]!);
        Assert.Equal("Wailing Caverns", (string?)g["instanceName"]);
        Assert.NotEqual(0, (int)g["flags"]! & 128);                                    // group bit follows groupSize
        Assert.NotEqual(0, (int)g["flags"]! & 16);                                     // instance bit follows inInstance
    }

    [Fact]
    public void Rich_presence_garbage_is_clamped_or_dropped()
    {
        var g = PresenceModule.BuildGame(Obj("""
            {"xpPct":9999,"groupSize":-4,"flags":-1,"inInstance":false,"instanceName":"Leaky","level":"x","rested":"yes","zone":123}
            """));
        Assert.Equal(100, (int)g["xpPct"]!);
        Assert.Equal(0, (int)g["groupSize"]!);
        Assert.Equal(0, (int)g["flags"]! & 128);                                       // no group -> no group bit
        Assert.Null(g["instanceName"]);                                                // no instance -> no instance name
        Assert.False((bool)g["rested"]!);
        Assert.Equal(0, (int)g["level"]!);
        Assert.Null(g["zone"]);
        var none = PresenceModule.BuildGame(Obj("{\"playing\":true}"));                // an old companion: nothing new, no xp
        Assert.Null(none["xpPct"]);
        Assert.Equal(0, (int)none["flags"]!);
        Assert.False((bool)none["inInstance"]!);
    }

    [Fact]
    public void Rich_presence_strings_lose_control_characters_and_length()
    {
        var long80 = new string('W', 80);
        var g = PresenceModule.BuildGame(Obj($"{{\"inInstance\":true,\"instanceName\":\"Dead\\u0001mines\\u200b {long80}\",\"zone\":\"Bar\\u0007rens{new string('z', 90)}\"}}"));
        var inst = (string)g["instanceName"]!;
        Assert.True(inst.Length <= 41);                                                // 40 + ellipsis
        Assert.DoesNotContain("\u0001", inst, StringComparison.Ordinal);
        Assert.DoesNotContain("\u200b", inst, StringComparison.Ordinal);
        Assert.True(((string)g["zone"]!).Length <= 41);
        Assert.DoesNotContain("\u0007", (string)g["zone"]!, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- Appear offline

    [Fact]
    public async Task An_invisible_member_is_absent_from_rosters_joins_and_leaves_of_others()
    {
        using var l = new Lodge2();
        var bob = await l.Join(T.BobCode);                                   // officer: can't see invisible
        var elan = await l.Join(T.ElanCode);                                 // owner
        bob.Clear(); elan.Clear();
        var mia = await l.Join(MemCode, invisible: true);
        Assert.Empty(bob.Of("join"));                                        // no join announced to others...
        Assert.Single(elan.Of("join"));                                      // ...except the owner, with the marker
        Assert.True((bool?)elan.Of("join")[0]["user"]!["invisible"]);
        var w = mia.Of("welcome")[0];
        Assert.Contains("Mia", NamesOf(w));                                    // she sees herself, flagged
        Assert.True((bool?)((JsonArray)w["users"]!).First(u => (string?)u!["name"] == "Mia")!["invisible"]);
        Assert.Contains("Bob", NamesOf(w));                                    // and receives everything else

        var zed = await l.Join(Mem2Code);                                    // a later joiner's roster
        Assert.DoesNotContain("Mia", NamesOf(zed.Of("welcome")[0]));
        var owner2 = await l.Join(T.ElanCode);                               // owner signing in again sees Mia
        Assert.Contains("Mia", NamesOf(owner2.Of("welcome")[0]));
        Assert.Equal(l.Hub.Online - 1, l.Hub.VisibleOnline);                 // Mia is not counted

        bob.Clear(); elan.Clear();
        mia.Hangup(); await Lodge2.Settle();
        Assert.Empty(bob.Of("leave"));                                       // leaving invisibly announces nothing to Bob
    }

    [Fact]
    public async Task Typing_status_and_game_of_an_invisible_member_are_not_forwarded()
    {
        using var l = new Lodge2();
        var bob = await l.Join(T.BobCode);
        var elan = await l.Join(T.ElanCode);
        var mia = await l.Join(MemCode, invisible: true);
        bob.Clear(); elan.Clear(); mia.Clear();
        var ch = l.Hub.Channels.DefaultText.Id;
        mia.Say($"{{\"t\":\"typing\",\"channel\":\"{ch}\"}}");
        mia.Say("{\"t\":\"status\",\"status\":\"busy\",\"note\":\"secret\"}");
        mia.Say("{\"t\":\"game\",\"playing\":true,\"name\":\"Mia\",\"zone\":\"Hidden Zone\",\"level\":30}");
        await Lodge2.Settle();
        Assert.Empty(bob.Of("typing"));
        Assert.Empty(bob.Of("user"));                                        // nothing at all, not even a masked update
        Assert.Empty(elan.Of("typing"));                                     // typing is never shown, even to the owner
        Assert.Contains(elan.Of("user"), u => (string?)u["user"]!["note"] == "secret");   // the owner does get presence (moderation view)
        Assert.Contains(mia.Of("user"), u => (string?)u["user"]!["note"] == "secret");    // and so does she
        Assert.DoesNotContain("Hidden Zone", string.Join("", bob.Sent.Select(x => x.ToJsonString())), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Going_invisible_sends_a_leave_and_going_visible_a_join_with_current_presence()
    {
        using var l = new Lodge2();
        var bob = await l.Join(T.BobCode);
        var elan = await l.Join(T.ElanCode);
        var mia = await l.Join(MemCode);
        mia.Say("{\"t\":\"game\",\"playing\":true,\"name\":\"Mia\",\"zone\":\"Elwynn Forest\",\"level\":12,\"xpPct\":40}");
        await Lodge2.Settle();
        bob.Clear(); elan.Clear();

        mia.Say("{\"t\":\"status\",\"status\":\"online\",\"note\":\"\",\"visibility\":\"invisible\"}");
        await Lodge2.Settle();
        Assert.Single(bob.Of("leave"));                                      // Bob sees her leave
        Assert.Empty(elan.Of("leave"));                                      // the owner keeps her, now flagged
        Assert.Contains(elan.Of("user"), u => (bool?)u["user"]!["invisible"] == true);

        bob.Clear(); elan.Clear();
        mia.Say("{\"t\":\"status\",\"status\":\"online\",\"note\":\"\",\"visibility\":\"visible\"}");
        await Lodge2.Settle();
        var join = Assert.Single(bob.Of("join"));                            // a normal join, current presence included
        Assert.Equal("Elwynn Forest", (string?)join["user"]!["game"]!["zone"]);
        Assert.Null(join["user"]!["invisible"]);
        Assert.Empty(elan.Of("join"));                                       // the owner just gets an update
        Assert.Contains(elan.Of("user"), u => (string?)u["user"]!["name"] == "Mia" && u["user"]!["invisible"] == null);

        // old hubs never send "visibility": nothing changes
        bob.Clear();
        mia.Say("{\"t\":\"status\",\"status\":\"away\",\"note\":\"\"}");
        await Lodge2.Settle();
        Assert.Empty(bob.Of("leave"));
        Assert.Single(bob.Of("user"));
    }

    [Fact]
    public async Task Messages_of_an_invisible_member_are_still_delivered_with_the_author()
    {
        using var l = new Lodge2();
        var bob = await l.Join(T.BobCode);
        var mia = await l.Join(MemCode, invisible: true);
        bob.Clear();
        var ch = l.Hub.Channels.DefaultText.Id;
        mia.Say($"{{\"t\":\"msg\",\"channel\":\"{ch}\",\"text\":\"hello from the shadows\"}}");
        await Lodge2.Settle();
        var msg = Assert.Single(bob.Of("msg"));
        Assert.Equal("Mia", (string?)msg["from"]);
        Assert.Equal("hello from the shadows", (string?)msg["text"]);
    }

    [Fact]
    public async Task Joining_voice_while_invisible_shows_a_masked_entry_to_that_rooms_viewers_only()
    {
        using var l = new Lodge2();
        var bob = await l.Join(T.BobCode);
        var zed = await l.Join(Mem2Code);
        var mia = await l.Join(MemCode, invisible: true);
        mia.Say("{\"t\":\"status\",\"status\":\"busy\",\"note\":\"private note\"}");
        await Lodge2.Settle();
        bob.Clear(); zed.Clear();
        var room = l.Hub.Channels.DefaultVoice("member")!.Id;
        mia.Say($"{{\"t\":\"voice\",\"room\":\"{room}\"}}");
        await Lodge2.Settle();
        var join = Assert.Single(bob.Of("join"));                            // shown in the room list like Discord
        Assert.Equal(room, (string?)join["user"]!["room"]);
        Assert.Equal("online", (string?)join["user"]!["status"]);            // but nothing else: no status, note or game
        Assert.Equal("", (string?)join["user"]!["note"]);
        Assert.Null(join["user"]!["game"]);
        Assert.Null(join["user"]!["invisible"]);
        mia.Say("{\"t\":\"voice\",\"room\":null}");
        await Lodge2.Settle();
        Assert.Single(bob.Of("leave"));                                      // hidden again when she leaves the room
    }

    [Fact]
    public async Task Only_the_owner_rank_may_see_invisible_members_and_officers_cant_moderate_them()
    {
        Assert.True(Roles.Can("owner", Perm.SeeInvisible));
        Assert.False(Roles.Can("officer", Perm.SeeInvisible));
        Assert.False(Roles.Can("member", Perm.SeeInvisible));
        using var l = new Lodge2();
        var bob = await l.Join(T.BobCode);
        var mia = await l.Join(MemCode, invisible: true);
        var miaId = l.Hub.Members.First(m => m.Name == "Mia").Id;
        bob.Clear();
        bob.Say($"{{\"t\":\"mod.kick\",\"id\":{miaId}}}");
        await Lodge2.Settle();
        Assert.NotEmpty(bob.Of("error"));                                    // "They're not online" - no confirmation she exists
        Assert.Equal(WebSocketState.Open, mia.State);                        // still connected
        var elan = await l.Join(T.ElanCode);
        elan.Say($"{{\"t\":\"mod.kick\",\"id\":{miaId}}}");
        await Lodge2.Settle();
        Assert.NotEqual(WebSocketState.Open, mia.State);                     // the owner can
    }

    [Fact]
    public async Task Health_online_count_leaves_out_invisible_members()
    {
        using var l = new Lodge2();
        await l.Join(T.BobCode);
        await l.Join(MemCode, invisible: true);
        Assert.Equal(2, l.Hub.Online);
        Assert.Equal(1, l.Hub.VisibleOnline);
    }
}
