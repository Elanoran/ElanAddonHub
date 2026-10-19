using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Lodge.Tests;

// 2.30.0: live map positions between members ("position" in, "memberpos" out). Opt-in, scope everyone | officers, never for guests,
// an invisible sharer only to Perm.SeeInvisible, never looser than the shared zone.
public class LivePositionTests
{
    // a socket the test drives (the same as PresenceTests): Say() queues a client frame, Sent holds what the server sent
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
        public List<JsonObject> PosOf(int id) => Of("memberpos").Where(x => (int?)x["id"] == id).ToList();
        public void Clear() { lock (Sent) Sent.Clear(); }
        public int Id;
    }

    const string MemCode = "synthetic0mem00code0000000000003";
    const string Mem2Code = "synthetic0mem20code000000000004";
    const string GuestCode = "synthetic-guest-code";

    sealed class Lodge2 : IDisposable
    {
        public LodgeHub Hub = null!; public Auth Auth = null!; public TempDir Dir = new();
        public CancellationTokenSource Cts = new();
        public List<Task> Runs = new();
        public FakeClock Clock = new();
        public Lodge2()
        {
            var cfg = new LodgeConfig
            {
                Code = GuestCode, CodesFile = Path.Combine(Dir.Path, "codes"), Urls = "", DataDir = Dir.Path, PathBase = "", MaxFileMb = 25, FileDays = 30,
                MaxUsers = 25, TrustedProxies = Array.Empty<string>(), MaxStorageBytes = 1 << 30, MaxFiles = 100, MaxConcurrentUploads = 4,
                UploadsPer10Min = 30, HistoryMaxBytes = 1 << 20, MaxTrackedIps = 1000, HttpPerMinute = 240, AllowQueryCode = false,
            };
            T.WriteCodes(cfg, $"Elan:{T.ElanCode}:owner", $"Bob:{T.BobCode}:officer", $"Mia:{MemCode}:member", $"Zed:{Mem2Code}:member");
            Auth = new Auth(cfg, T.Log);
            Hub = new LodgeHub(cfg, Auth, new FileStore(cfg), T.Log, Clock);
        }
        public async Task<ScriptSocket> Join(string code, bool invisible = false, string? name = null)
        {
            var s = new ScriptSocket();
            Runs.Add(Hub.Run(s, Auth.Check(code)!, code, name, "hub/test", "1.1.1." + (Runs.Count + 1), Cts.Token, invisible));
            await Settle();
            s.Id = (int)s.Of("welcome")[0]["you"]!;
            return s;
        }
        public static Task Settle() => Task.Delay(120);
        public void Dispose() { Cts.Cancel(); try { Task.WhenAll(Runs).Wait(2000); } catch { } Dir.Dispose(); }
    }

    static JsonObject Obj(string json) => JsonNode.Parse(json)!.AsObject();
    const string Game = """{"t":"game","playing":true,"name":"Mia","class":"Hunter","classFile":"HUNTER","level":30,"zone":"The Barrens"}""";
    static string Pos(double fx, double fy, string scope = "everyone", int mapId = 1413) =>
        $$"""{"t":"position","mapId":{{mapId}},"fx":{{fx.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"fy":{{fy.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"move":0,"valid":true,"scope":"{{scope}}"}""";

    // everyone online: owner, officer, two members, a guest; Mia shares her game (with a zone)
    static async Task<(Lodge2 l, ScriptSocket elan, ScriptSocket bob, ScriptSocket mia, ScriptSocket zed, ScriptSocket guest)> Room(bool miaInvisible = false)
    {
        var l = new Lodge2();
        var elan = await l.Join(T.ElanCode); var bob = await l.Join(T.BobCode);
        var mia = await l.Join(MemCode, miaInvisible); var zed = await l.Join(Mem2Code); var guest = await l.Join(GuestCode, name: "Wanderer");
        mia.Say(Game); await Lodge2.Settle();
        foreach (var s in new[] { elan, bob, mia, zed, guest }) s.Clear();
        return (l, elan, bob, mia, zed, guest);
    }

    [Fact]
    public void The_feature_is_always_advertised_and_the_version_is_2_30()
    {
        using var l = new Lodge2();
        Assert.Contains("liveposition", l.Hub.FeatureList());
        Assert.Equal("2.31.1", LodgeHub.Version);
    }

    [Fact]
    public void BuildPosition_clamps_rounds_and_rejects_garbage()
    {
        var p = PresenceModule.BuildPosition(Obj("""{"mapId":1413,"fx":1.7,"fy":-3,"move":65535,"valid":true,"scope":"officers","extra":"x"}"""))!;
        Assert.Equal(1.0, (double)p["fx"]!); Assert.Equal(0.0, (double)p["fy"]!);
        Assert.Equal(0, (int)p["move"]! & ~PresenceModule.MoveMask);
        Assert.Equal("officers", (string?)p["scope"]);
        Assert.Null(p["extra"]);
        Assert.Equal(0.1235, (double)PresenceModule.BuildPosition(Obj("""{"mapId":1,"fx":0.123456,"fy":0.5,"valid":true}"""))!["fx"]!);
        Assert.Equal("everyone", (string?)PresenceModule.BuildPosition(Obj("""{"mapId":1,"fx":0.1,"fy":0.5,"valid":true,"scope":"guild"}"""))!["scope"]);
        Assert.Equal(PresenceModule.MaxMapId, (int)PresenceModule.BuildPosition(Obj("""{"mapId":99999999,"fx":0.1,"fy":0.5,"valid":true}"""))!["mapId"]!);
        Assert.Null(PresenceModule.BuildPosition(Obj("""{"mapId":1413,"fx":0.4,"fy":0.5,"valid":false}""")));
        Assert.Null(PresenceModule.BuildPosition(Obj("""{"mapId":1413,"fx":0.4,"fy":0.5}""")));                       // valid must be said
        Assert.Null(PresenceModule.BuildPosition(Obj("""{"mapId":1413,"fx":0.4,"fy":0.5,"valid":true,"share":false}""")));
        Assert.Null(PresenceModule.BuildPosition(Obj("""{"mapId":0,"fx":0.4,"fy":0.5,"valid":true}""")));
        Assert.Null(PresenceModule.BuildPosition(Obj("""{"mapId":1413,"fx":"NaN","fy":0.5,"valid":true}""")));
        Assert.Null(PresenceModule.BuildPosition(Obj("""{"mapId":1413,"fy":0.5,"valid":true}""")));
        Assert.Null(PresenceModule.BuildPosition(Obj("""{"mapId":"x","fx":0.4,"fy":0.5,"valid":true}""")));
        Assert.Null(PresenceModule.BuildPosition(Obj("""{"mapId":1413,"fx":1e400,"fy":0.5,"valid":true}""")));
    }

    [Fact]
    public async Task A_shared_position_reaches_members_and_the_sharer_but_never_guests()
    {
        var (l, elan, bob, mia, zed, guest) = await Room();
        using var _ = l;
        mia.Say(Pos(0.41, 0.62)); await Lodge2.Settle();
        int id = mia.Id;
        foreach (var s in new[] { elan, bob, zed, mia })
        {
            var p = Assert.Single(s.PosOf(id));
            Assert.True((bool)p["valid"]!); Assert.Equal(1413, (int)p["mapId"]!); Assert.Equal(0.41, (double)p["fx"]!); Assert.Equal(0.62, (double)p["fy"]!);
            Assert.Null(p["scope"]);                                           // the scope is the sharer's business
        }
        Assert.Empty(guest.Of("memberpos"));
    }

    [Fact]
    public async Task Officers_scope_hides_the_position_from_plain_members_and_withdraws_it_on_a_switch()
    {
        var (l, elan, bob, mia, zed, guest) = await Room();
        using var _ = l;
        int id = mia.Id;
        mia.Say(Pos(0.4, 0.5, "officers")); await Lodge2.Settle();
        Assert.Single(elan.PosOf(id)); Assert.Single(bob.PosOf(id)); Assert.Single(mia.PosOf(id));
        Assert.Empty(zed.Of("memberpos")); Assert.Empty(guest.Of("memberpos"));

        // everyone -> officers: the plain member who had the dot is told it is gone
        mia.Say(Pos(0.4, 0.5, "everyone")); await Lodge2.Settle();
        Assert.True((bool)Assert.Single(zed.PosOf(id))["valid"]!);
        zed.Clear();
        mia.Say(Pos(0.4, 0.5, "officers")); await Lodge2.Settle();
        Assert.False((bool)Assert.Single(zed.PosOf(id))["valid"]!);
        Assert.Null(Assert.Single(zed.PosOf(id))["fx"]);
    }

    [Fact]
    public async Task An_invisible_sharer_is_seen_by_the_owner_only()
    {
        var (l, elan, bob, mia, zed, guest) = await Room(miaInvisible: true);
        using var _ = l;
        int id = mia.Id;
        mia.Say(Pos(0.3, 0.3)); await Lodge2.Settle();
        Assert.Single(elan.PosOf(id)); Assert.Single(mia.PosOf(id));
        Assert.Empty(bob.Of("memberpos")); Assert.Empty(zed.Of("memberpos")); Assert.Empty(guest.Of("memberpos"));

        // even sitting in a voice room (where CanSee shows her, masked, to the room's viewers) the position stays hidden
        mia.Say("""{"t":"voice","room":"voice"}"""); await Lodge2.Settle();
        mia.Say(Pos(0.31, 0.3)); await Lodge2.Settle();
        Assert.Empty(bob.Of("memberpos")); Assert.Empty(zed.Of("memberpos"));
        Assert.Equal(2, elan.PosOf(id).Count);
    }

    [Fact]
    public async Task Going_invisible_withdraws_the_position_and_going_visible_brings_it_back()
    {
        var (l, elan, bob, mia, zed, guest) = await Room();
        using var _ = l;
        int id = mia.Id;
        mia.Say(Pos(0.3, 0.3)); await Lodge2.Settle();
        Assert.Single(zed.PosOf(id));
        zed.Clear(); bob.Clear(); elan.Clear();
        mia.Say("""{"t":"status","status":"online","visibility":"invisible"}"""); await Lodge2.Settle();
        Assert.Contains(zed.Of("leave"), x => (int?)x["id"] == id);              // the leave drops the dot...
        Assert.Empty(zed.PosOf(id));                                               // ...and nothing about her follows
        mia.Say(Pos(0.35, 0.3)); await Lodge2.Settle();
        Assert.Empty(zed.PosOf(id)); Assert.Empty(bob.PosOf(id));
        Assert.True((bool)elan.PosOf(id).Last()["valid"]!);                        // the owner still follows her
        mia.Say("""{"t":"status","status":"online","visibility":"visible"}"""); await Lodge2.Settle();
        Assert.Equal(0.35, (double)zed.PosOf(id).Last()["fx"]!);
    }

    [Fact]
    public async Task Guests_cannot_share()
    {
        var (l, elan, bob, mia, zed, guest) = await Room();
        using var _ = l;
        guest.Say(Game.Replace("Mia", "Wanderer")); await Lodge2.Settle();
        guest.Say(Pos(0.5, 0.5)); await Lodge2.Settle();
        foreach (var s in new[] { elan, bob, mia, zed, guest }) Assert.Empty(s.Of("memberpos"));
    }

    [Fact]
    public async Task Without_a_shared_zone_there_is_no_position_and_hiding_the_zone_withdraws_it()
    {
        var (l, elan, bob, mia, zed, guest) = await Room();
        using var _ = l;
        int id = zed.Id;
        zed.Say(Pos(0.5, 0.5)); await Lodge2.Settle();                            // Zed shares no game at all
        Assert.Empty(elan.Of("memberpos")); Assert.Empty(zed.Of("memberpos"));

        int mid = mia.Id;
        mia.Say(Pos(0.5, 0.5)); await Lodge2.Settle();
        Assert.Single(zed.PosOf(mid));
        mia.Say("""{"t":"game","playing":true,"name":"Mia","level":30}"""); await Lodge2.Settle();   // zone sharing switched off
        Assert.False((bool)zed.PosOf(mid).Last()["valid"]!);
        zed.Clear();
        mia.Say(Pos(0.6, 0.5)); await Lodge2.Settle();
        Assert.Empty(zed.Of("memberpos"));
        mia.Say(Game); await Lodge2.Settle();
        mia.Say(Pos(0.6, 0.5)); await Lodge2.Settle();
        Assert.Single(zed.PosOf(mid));
        mia.Say("""{"t":"game","share":false}"""); await Lodge2.Settle();          // game sharing off altogether
        Assert.False((bool)zed.PosOf(mid).Last()["valid"]!);
    }

    [Fact]
    public async Task Opting_out_withdraws_the_dot_at_once_and_an_unchanged_position_is_not_sent_again()
    {
        var (l, elan, bob, mia, zed, guest) = await Room();
        using var _ = l;
        int id = mia.Id;
        mia.Say(Pos(0.2, 0.2)); await Lodge2.Settle();
        mia.Say(Pos(0.2, 0.2)); await Lodge2.Settle();
        mia.Say(Pos(0.2, 0.2)); await Lodge2.Settle();
        Assert.Single(zed.PosOf(id));
        mia.Say("""{"t":"position","share":false}"""); await Lodge2.Settle();
        Assert.Equal(2, zed.PosOf(id).Count);
        Assert.False((bool)zed.PosOf(id)[1]["valid"]!);
        Assert.False((bool)bob.PosOf(id)[1]["valid"]!);
        mia.Say("""{"t":"position","share":false}"""); await Lodge2.Settle();      // nothing more to withdraw
        Assert.Equal(2, zed.PosOf(id).Count);
        mia.Say("""{"t":"position","mapId":1413,"fx":0.2,"fy":0.2,"valid":false}"""); await Lodge2.Settle();
        Assert.Equal(2, zed.PosOf(id).Count);
    }

    [Fact]
    public async Task A_newcomer_gets_the_positions_it_may_see()
    {
        var (l, elan, bob, mia, zed, guest) = await Room();
        using var _ = l;
        int id = mia.Id;
        mia.Say(Pos(0.7, 0.1)); await Lodge2.Settle();
        var bob2 = await l.Join(T.BobCode);                                         // Bob signs in again (a new connection replaces the old one)
        Assert.Equal(0.7, (double)Assert.Single(bob2.PosOf(id))["fx"]!);
        var g2 = await l.Join(GuestCode, name: "Second");
        Assert.Empty(g2.Of("memberpos"));
    }

    [Fact]
    public async Task A_flood_of_positions_is_dropped_quietly_but_a_withdrawal_always_goes_through()
    {
        var (l, elan, bob, mia, zed, guest) = await Room();
        using var _ = l;
        int id = mia.Id;
        for (int i = 0; i < 25; i++) mia.Say(Pos(0.01 * (i + 1), 0.5));
        await Lodge2.Settle(); await Lodge2.Settle();
        var got = zed.PosOf(id).Count;
        Assert.InRange(got, 1, 10);                                                 // the position budget: a burst of 10 (the clock stands still)
        Assert.Empty(mia.Of("error"));                                              // no error spam
        mia.Say("""{"t":"position","share":false}"""); await Lodge2.Settle();
        Assert.False((bool)zed.PosOf(id).Last()["valid"]!);
        l.Clock.Advance(TimeSpan.FromSeconds(10));
        mia.Say(Pos(0.9, 0.5)); await Lodge2.Settle();
        Assert.True((bool)zed.PosOf(id).Last()["valid"]!);
    }

    [Fact]
    public async Task A_demotion_to_guest_ends_sharing_and_seeing()
    {
        var (l, elan, bob, mia, zed, guest) = await Room();
        using var _ = l;
        int id = mia.Id;
        mia.Say(Pos(0.4, 0.4)); await Lodge2.Settle();
        Assert.Single(zed.PosOf(id));
        T.WriteCodes(LodgeConfig.ForTests(l.Dir.Path), $"Elan:{T.ElanCode}:owner", $"Bob:{T.BobCode}:officer", $"Mia:{MemCode}:guest", $"Zed:{Mem2Code}:member");
        await l.Hub.ApplyCodeChanges(); await Lodge2.Settle();
        Assert.False((bool)zed.PosOf(id).Last()["valid"]!);
        mia.Say(Pos(0.45, 0.4)); await Lodge2.Settle();
        Assert.Single(zed.PosOf(id).Where(x => (bool)x["valid"]!));
    }
}
