using System.Net.WebSockets;
using Xunit;

namespace Lodge.Tests;

// Findings 4 and 6: budgets, admission, reconnect at capacity, guest names.
public class HubTests
{
    sealed class FakeSocket : WebSocket
    {
        WebSocketState state = WebSocketState.Open;
        readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? CloseReason;
        public override WebSocketCloseStatus? CloseStatus => state == WebSocketState.Open ? null : WebSocketCloseStatus.PolicyViolation;
        public override string? CloseStatusDescription => CloseReason;
        public override WebSocketState State => state;
        public override string? SubProtocol => null;
        public override void Abort() { state = WebSocketState.Aborted; closed.TrySetResult(); }
        public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken ct) { CloseReason = d; state = WebSocketState.Closed; closed.TrySetResult(); return Task.CompletedTask; }
        public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken ct) => CloseAsync(s, d, ct);
        public override void Dispose() { }
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
        {
            await closed.Task.WaitAsync(ct);
            return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
        }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType t, bool end, CancellationToken ct) => Task.CompletedTask;
    }

    static (LodgeHub hub, Auth auth, TempDir dir) Hub(int maxUsers = 25, FakeClock? clock = null)
    {
        var d = new TempDir();
        var cfg = LodgeConfig.ForTests(d.Path);
        cfg = new LodgeConfig
        {
            Code = "synthetic-guest-code", CodesFile = cfg.CodesFile, Urls = "", DataDir = d.Path, PathBase = "", MaxFileMb = 25, FileDays = 30,
            MaxUsers = maxUsers, TrustedProxies = Array.Empty<string>(), MaxStorageBytes = 1 << 30, MaxFiles = 100, MaxConcurrentUploads = 4,
            UploadsPer10Min = 30, HistoryMaxBytes = 1 << 20, MaxTrackedIps = 1000, HttpPerMinute = 240, AllowQueryCode = true,
        };
        T.WriteCodes(cfg, $"Elan:{T.ElanCode}:owner", $"Bob:{T.BobCode}:officer");
        var auth = new Auth(cfg, T.Log);
        return (new LodgeHub(cfg, auth, new FileStore(cfg), T.Log, clock ?? new FakeClock()), auth, d);
    }

    [Fact]
    public void Normal_voice_and_chat_fit_the_budget_and_floods_dont()
    {
        var clock = new FakeClock();
        var b = new Budget(clock);
        int voice = 0;
        for (int i = 0; i < 50 * 30; i++) // 30 s of a 20 ms Opus stream, 80-byte packets
        {
            if (b.VoicePackets.TryTake() && b.VoiceBytes.TryTake(80)) voice++;
            clock.Advance(TimeSpan.FromMilliseconds(20));
        }
        Assert.Equal(1500, voice);
        int chat = 0;
        for (int i = 0; i < 100; i++) if (b.Chat.TryTake()) chat++;
        Assert.Equal(8, chat);                                  // 8 per 10 s burst
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.True(b.Chat.TryTake());
        int control = 0;
        for (int i = 0; i < 10_000; i++) if (b.Control.TryTake()) control++;
        Assert.Equal(30, control);                              // a control flood stops at the burst
        int big = 0;
        for (int i = 0; i < 500; i++) if (b.VoicePackets.TryTake() && b.VoiceBytes.TryTake(4000)) big++;
        Assert.True(big < 40, $"{big} oversized packets passed");
    }

    [Fact]
    public void Budgets_follow_the_person_across_reconnects_and_guests_stay_separate()
    {
        var (hub, _, d) = Hub();
        using var _ = d;
        var elan1 = hub.BudgetFor(new Identity("Elan", true, "owner"), "1.1.1.1");
        var elan2 = hub.BudgetFor(new Identity("elan", true, "owner"), "9.9.9.9");      // reconnect from elsewhere
        Assert.Same(elan1, elan2);
        var guestA = hub.BudgetFor(new Identity(null, false, "guest"), "1.1.1.1");
        var guestB = hub.BudgetFor(new Identity(null, false, "guest"), "2.2.2.2");
        Assert.NotSame(guestA, guestB);                                                   // friends on the guest code
        Assert.NotSame(elan1, guestA);
    }

    [Fact]
    public void Concurrent_joins_never_pass_the_cap()
    {
        var (hub, _, d) = Hub(maxUsers: 25);
        using var _ = d;
        int admitted = 0;
        Parallel.For(0, 200, i =>
        {
            var m = new Member { Id = hub.NextId(), Name = "g" + i, Ws = new FakeSocket(), Budget = new Budget(new FakeClock()) };
            if (hub.TryAdmit(m)) Interlocked.Increment(ref admitted);
        });
        Assert.Equal(25, admitted);
        Assert.Equal(25, hub.Online);
    }

    [Fact]
    public async Task Reconnecting_at_capacity_replaces_the_old_connection()
    {
        var (hub, auth, d) = Hub(maxUsers: 2);
        using var _ = d;
        using var cts = new CancellationTokenSource();
        var oldSock = new FakeSocket();
        var elan = auth.Check(T.ElanCode)!;
        var run1 = hub.Run(oldSock, elan, T.ElanCode, null, "hub/test", "1.1.1.1", cts.Token);
        var run2 = hub.Run(new FakeSocket(), auth.Check(T.BobCode)!, T.BobCode, null, "hub/test", "2.2.2.2", cts.Token);
        await Task.Delay(50);
        Assert.Equal(2, hub.Online);                             // full
        var guestSock = new FakeSocket();
        await hub.Run(guestSock, auth.Check("synthetic-guest-code")!, "synthetic-guest-code", "Tom", "hub/test", "3.3.3.3", cts.Token);
        Assert.Equal("The lodge is full", guestSock.CloseReason);
        var newSock = new FakeSocket();
        var run3 = hub.Run(newSock, elan, T.ElanCode, null, "hub/test", "4.4.4.4", cts.Token);
        await Task.Delay(50);
        Assert.Equal("Signed in somewhere else with this code", oldSock.CloseReason);
        Assert.Null(newSock.CloseReason);                        // admitted although the lodge was full
        Assert.Equal(2, hub.Online);
        cts.Cancel();
        await Task.WhenAll(run1, run2, run3).ContinueWith(_ => { });
        Assert.Equal(0, hub.Online);                             // every slot released
    }

    [Theory]
    [InlineData("E\u0001lan")]
    [InlineData("  elan  ")]
    [InlineData("E\tlan")]
    [InlineData("ELAN")]
    [InlineData("Ｅｌａｎ")]                  // fullwidth letters fold under NFKC
    [InlineData("El​an")]               // zero-width space
    [InlineData("Elan                                      ")]
    public void Guests_cant_look_like_a_personal_member(string asked)
    {
        var (hub, _, d) = Hub();
        using var _ = d;
        var name = hub.UniqueName(hub.GuestName(asked), hub.Auth.PersonalNames);
        Assert.False(Names.Same(name, "Elan"), $"'{asked}' became '{name}'");
        // whatever normalizes to the reserved name gets the guest tag; anything else must look different anyway
        if (Names.Same(Names.Normalize(asked), "Elan")) Assert.Contains("(guest)", name);
        Assert.True(name.Length <= Names.Max);
    }

    [Fact]
    public void Guest_suffixes_and_truncation_stay_distinct_and_short()
    {
        var (hub, _, d) = Hub();
        using var _ = d;
        var longName = new string('A', 40);
        var n = hub.UniqueName(hub.GuestName(longName), hub.Auth.PersonalNames);
        Assert.Equal(24, n.Length);
        Assert.Equal("Tom", hub.UniqueName(hub.GuestName("Tom"), hub.Auth.PersonalNames));
        Assert.Equal("Guest", hub.UniqueName(hub.GuestName("\u0001\u0002"), hub.Auth.PersonalNames));
        // a guest asking for "Bob" gets the guest tag even before Bob is online
        Assert.Equal("Bob (guest)", hub.UniqueName(hub.GuestName("bob"), hub.Auth.PersonalNames), ignoreCase: true);
    }

    [Fact]
    public void Names_normalize_consistently()
    {
        Assert.Equal("Elan the Bold", Names.Normalize("  Elan \t the\n Bold "));
        Assert.Equal("Elan", Names.Normalize("E\u0000l\u0007an"));
        Assert.True(Names.Same("ELAN", "elan"));
        Assert.Equal(24, Names.Normalize(new string('x', 99)).Length);
        Assert.Equal("", Names.Normalize(null!));
        Assert.Equal("a b[31mc", Names.ForLog("a\r\nb\u001b[31mc"));         // no line breaks or escape codes in logs
    }

    [Fact]
    public void Old_budgets_are_swept_but_connected_ones_stay()
    {
        var clock = new FakeClock();
        var (hub, _, d) = Hub(clock: clock);
        using var _ = d;
        hub.BudgetFor(new Identity(null, false, "guest"), "1.1.1.1");
        hub.BudgetFor(new Identity("Elan", true, "owner"), "1.1.1.1");
        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(2, hub.SweepBudgets());
    }

    [Fact]
    public void Game_presence_passes_race_and_sex_through_sanitized()
    {
        var g = PresenceModule.BuildGame(System.Text.Json.Nodes.JsonNode.Parse(
            "{\"playing\":true,\"name\":\"Elan\",\"level\":60,\"raceFile\":\"NightElf\",\"race\":\"Night Elf\",\"sex\":3}")!.AsObject());
        Assert.Equal("NightElf", (string?)g["raceFile"]);
        Assert.Equal(3, (int)g["sex"]!);
        var bad = PresenceModule.BuildGame(System.Text.Json.Nodes.JsonNode.Parse("{\"sex\":99}")!.AsObject());
        Assert.Equal(3, (int)bad["sex"]!);
        var old = PresenceModule.BuildGame(System.Text.Json.Nodes.JsonNode.Parse("{\"playing\":false}")!.AsObject());
        Assert.Equal(0, (int)old["sex"]!); // older clients: no fields
    }
}
