using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Lodge.Tests;

// a fake socket + a hub with a few people, shared by the 2.9 tests (sign-up cards, paging, search)
public sealed class FunSock : WebSocket
{
    public readonly List<JsonObject> Sent = new();
    public override WebSocketCloseStatus? CloseStatus => null;
    public override string? CloseStatusDescription => null;
    public override WebSocketState State => WebSocketState.Open;
    public override string? SubProtocol => null;
    public override void Abort() { }
    public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken ct) => Task.CompletedTask;
    public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken ct) => Task.CompletedTask;
    public override void Dispose() { }
    public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct) => Task.Delay(Timeout.Infinite, ct).ContinueWith<WebSocketReceiveResult>(_ => null!);
    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType t, bool end, CancellationToken ct)
    {
        var o = JsonNode.Parse(Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count))!.AsObject();
        lock (Sent) Sent.Add(o);
        return Task.CompletedTask;
    }
    public List<JsonObject> Of(string t) { lock (Sent) return Sent.Where(x => (string)x["t"]! == t).ToList(); }
    public JsonObject? LastError() => Of("error").LastOrDefault();
    public void Clear() { lock (Sent) Sent.Clear(); }
}

public sealed class FunRig : IDisposable
{
    public readonly TempDir Dir = new();
    public readonly FakeClock Clock = new();
    public readonly LodgeHub Hub;
    public readonly Dictionary<string, (Member m, FunSock s)> People = new();

    public FunRig(Action<LodgeConfig>? tweak = null, Microsoft.Extensions.Logging.ILogger? log = null)
    {
        var cfg = LodgeConfig.ForTests(Dir.Path);
        tweak?.Invoke(cfg);
        T.WriteCodes(cfg, $"Elan:{T.ElanCode}:owner", $"Bob:{T.BobCode}:officer");
        Hub = new LodgeHub(cfg, new Auth(cfg, T.Log), new FileStore(cfg), log ?? T.Log, Clock);
    }

    public Member Join(string name, string role)
    {
        var s = new FunSock();
        var m = new Member { Id = Hub.NextId(), Name = name, Role = role, Ws = s, Personal = role != "guest", Budget = new Budget(Clock), Ip = "127.0.0.1" };
        Assert.True(Hub.TryAdmit(m));
        People[name] = (m, s);
        return m;
    }

    public FunSock Sock(string name) => People[name].s;

    public Task Do(Member m, object o)
    {
        var j = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(o))!.AsObject();
        return Hub.Chat.Handle(m, (string)j["t"]!, j);
    }

    // any module (chat, calendar...) - the hub's own dispatch order
    public async Task Send(Member m, object o)
    {
        var j = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(o))!.AsObject();
        var type = (string)j["t"]!;
        if (!await Hub.Chat.Handle(m, type, j) && !await Hub.Calendar.Handle(m, type, j)) await Hub.Flight.Handle(m, type, j);
    }

    public async Task<string> Say(Member m, string text, string channel = "general")
    {
        Clock.Advance(TimeSpan.FromSeconds(3));
        await Do(m, new { t = "msg", channel, text });
        return (string)Sock(m.Name).Of("msg").Last()["id"]!;
    }

    public long NowMs => new DateTimeOffset(Clock.UtcNow).ToUnixTimeMilliseconds();

    public void Dispose() => Dir.Dispose();
}
