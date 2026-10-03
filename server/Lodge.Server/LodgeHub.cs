using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lodge;

class Member
{
    public int Id;
    public string Name;
    public string ClientInfo;
    public string Code;     // re-checked every 15 s: removing a friend's code disconnects them
    public bool Personal;   // name comes from a personal invite code
    public string Role = "member";
    public WebSocket Ws;
    public bool Voice, Muted, Deaf;
    public string Status = "online"; // online | away | busy | dungeon | lfg
    public string Note = "";         // short "what I'm up to"
    public readonly SemaphoreSlim SendLock = new(1, 1);
    public readonly Queue<DateTime> Recent = new(); // message rate limit

    public JsonObject ToJson() => new()
    {
        ["id"] = Id, ["name"] = Name, ["guest"] = !Personal, ["voice"] = Voice, ["muted"] = Muted, ["deaf"] = Deaf,
        ["status"] = Status, ["note"] = Note, ["role"] = Role,
    };
}

// Everyone online, the chat history and the voice relay.
public class LodgeHub
{
    public const string Version = "1.4.0";
    const int HistoryKeep = 200;
    const int MaxVoicePacket = 4000;

    readonly LodgeConfig cfg;
    readonly Auth auth;
    readonly FileStore files;
    readonly ILogger log;
    readonly ConcurrentDictionary<int, Member> members = new();
    readonly List<JsonObject> history = new();
    readonly string historyFile;
    int nextId;

    public int Online => members.Count;

    public LodgeHub(LodgeConfig cfg, Auth auth, FileStore files, ILogger log)
    {
        this.cfg = cfg;
        this.auth = auth;
        this.files = files;
        this.log = log;
        historyFile = Path.Combine(cfg.DataDir, "history.jsonl");
        LoadHistory();
    }

    void LoadHistory()
    {
        if (!File.Exists(historyFile)) return;
        var lines = File.ReadAllLines(historyFile).Where(l => l.Length > 0).TakeLast(HistoryKeep).ToList();
        foreach (var l in lines)
            try { history.Add(JsonNode.Parse(l)!.AsObject()); } catch { }
        File.WriteAllLines(historyFile, lines); // compact
    }

    public async Task Run(WebSocket ws, Identity who, string code, string name, string clientInfo, CancellationToken ct)
    {
        if (members.Count >= cfg.MaxUsers)
        {
            await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "The lodge is full", ct);
            return;
        }
        // a personal code is one person: a new sign-in replaces the old connection (a shared code,
        // or a ghost after a network drop) instead of showing up as "Elan 2"
        if (who.Personal)
        {
            foreach (var old in members.Values.Where(m => m.Personal && string.Equals(m.Name, who.Name, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                log.LogInformation("{Name} signed in again - closing the older connection", old.Name);
                members.TryRemove(old.Id, out _);
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await old.Ws.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "Signed in somewhere else with this code", cts.Token);
                }
                catch { }
                // let the client read the reason (a clean close stops it reconnecting); cut it off only if it doesn't answer
                var ws0 = old.Ws;
                _ = Task.Delay(5000).ContinueWith(_ => { if (ws0.State != WebSocketState.Closed) ws0.Abort(); });
                await Broadcast(new JsonObject { ["t"] = "leave", ["id"] = old.Id });
            }
        }
        var me = new Member
        {
            Id = Interlocked.Increment(ref nextId), Name = UniqueName(who.Personal ? who.Name : GuestName(name)),
            ClientInfo = clientInfo, Ws = ws, Code = code, Personal = who.Personal, Role = who.Role,
        };
        members[me.Id] = me;
        log.LogInformation("{Name} joined ({Client})", me.Name, clientInfo);
        try
        {
            JsonArray hist;
            lock (history) hist = new JsonArray(history.Select(h => (JsonNode)h.DeepClone()).ToArray());
            await Send(me, new JsonObject
            {
                ["t"] = "welcome", ["you"] = me.Id, ["name"] = me.Name, ["server"] = Version,
                ["maxFileMb"] = cfg.MaxFileMb,
                ["users"] = new JsonArray(members.Values.Select(m => (JsonNode)m.ToJson()).ToArray()),
                ["history"] = hist,
            });
            await Broadcast(new JsonObject { ["t"] = "join", ["user"] = me.ToJson() }, except: me.Id);
            await ReceiveLoop(me, ct);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException) { }
        finally
        {
            if (members.TryRemove(me.Id, out _)) // not already removed by a newer sign-in
            {
                log.LogInformation("{Name} left", me.Name);
                await Broadcast(new JsonObject { ["t"] = "leave", ["id"] = me.Id });
            }
        }
    }

    async Task ReceiveLoop(Member me, CancellationToken ct)
    {
        var buf = new byte[16 * 1024];
        using var msg = new MemoryStream();
        while (me.Ws.State == WebSocketState.Open)
        {
            msg.SetLength(0);
            WebSocketReceiveResult r;
            do
            {
                r = await me.Ws.ReceiveAsync(buf, ct);
                if (r.MessageType == WebSocketMessageType.Close) return;
                msg.Write(buf, 0, r.Count);
                if (msg.Length > 64 * 1024) return; // nobody needs frames that big
            } while (!r.EndOfMessage);

            if (r.MessageType == WebSocketMessageType.Binary) await RelayVoice(me, msg.ToArray());
            else await Handle(me, Encoding.UTF8.GetString(msg.GetBuffer(), 0, (int)msg.Length));
        }
    }

    async Task Handle(Member me, string text)
    {
        JsonObject m;
        try { m = JsonNode.Parse(text)?.AsObject(); } catch { return; }
        if (m == null) return;
        switch ((string)m["t"])
        {
            case "msg":
                await Chat(me, m);
                break;
            case "typing":
                await Broadcast(new JsonObject { ["t"] = "typing", ["id"] = me.Id }, except: me.Id);
                break;
            case "voice":
                me.Voice = m["on"]?.GetValue<bool>() == true;
                await Broadcast(new JsonObject { ["t"] = "user", ["user"] = me.ToJson() });
                break;
            case "state":
                me.Muted = m["muted"]?.GetValue<bool>() == true;
                me.Deaf = m["deaf"]?.GetValue<bool>() == true;
                await Broadcast(new JsonObject { ["t"] = "user", ["user"] = me.ToJson() });
                break;
            case "status":
                var st = (string)m["status"];
                me.Status = st is "online" or "away" or "busy" or "dungeon" or "lfg" ? st : "online";
                var note = new string(((string)m["note"] ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
                me.Note = note.Length > 40 ? note[..40] : note;
                await Broadcast(new JsonObject { ["t"] = "user", ["user"] = me.ToJson() });
                break;
            case "ping":
                await Send(me, new JsonObject { ["t"] = "pong" });
                break;
        }
    }

    async Task Chat(Member me, JsonObject m)
    {
        // max 8 messages per 10 seconds
        var now = DateTime.UtcNow;
        while (me.Recent.Count > 0 && now - me.Recent.Peek() > TimeSpan.FromSeconds(10)) me.Recent.Dequeue();
        if (me.Recent.Count >= 8)
        {
            await Send(me, new JsonObject { ["t"] = "error", ["text"] = "Slow down a little" });
            return;
        }
        me.Recent.Enqueue(now);

        var text = ((string)m["text"] ?? "").Trim();
        if (text.Length > 2000) text = text[..2000];
        JsonObject file = null;
        var fileId = (string)m["file"]?["id"];
        if (fileId != null)
        {
            var meta = files.Get(fileId);
            if (meta != null)
                file = new JsonObject { ["id"] = meta.Id, ["name"] = meta.Name, ["size"] = meta.Size, ["mime"] = meta.Mime };
        }
        if (text.Length == 0 && file == null) return;

        var msg = new JsonObject
        {
            ["t"] = "msg",
            ["id"] = Guid.NewGuid().ToString("N")[..12],
            ["at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["from"] = me.Name,
            ["fromId"] = me.Id,
            ["text"] = text,
        };
        if (file != null) msg["file"] = file;
        lock (history)
        {
            history.Add(msg);
            if (history.Count > HistoryKeep) history.RemoveRange(0, history.Count - HistoryKeep);
            try { File.AppendAllText(historyFile, msg.ToJsonString() + "\n"); } catch { }
        }
        await Broadcast(msg);
    }

    // Voice: client sends [opus packet]; everyone else in voice (not deafened) gets [sender id: int32 LE][opus packet].
    async Task RelayVoice(Member me, byte[] packet)
    {
        if (!me.Voice || me.Muted || packet.Length == 0 || packet.Length > MaxVoicePacket) return;
        var outBuf = new byte[4 + packet.Length];
        BitConverter.TryWriteBytes(outBuf.AsSpan(0, 4), me.Id);
        packet.CopyTo(outBuf, 4);
        var targets = members.Values.Where(x => x.Id != me.Id && x.Voice && !x.Deaf);
        await Task.WhenAll(targets.Select(t => SendRaw(t, outBuf, WebSocketMessageType.Binary)));
    }

    Task Send(Member to, JsonObject obj) =>
        SendRaw(to, Encoding.UTF8.GetBytes(obj.ToJsonString()), WebSocketMessageType.Text);

    async Task SendRaw(Member to, byte[] data, WebSocketMessageType type)
    {
        if (to.Ws.State != WebSocketState.Open) return;
        // a stuck connection must never hold up everyone else
        if (!await to.SendLock.WaitAsync(TimeSpan.FromSeconds(2))) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await to.Ws.SendAsync(data, type, true, cts.Token);
        }
        catch { to.Ws.Abort(); }
        finally { to.SendLock.Release(); }
    }

    Task Broadcast(JsonObject obj, int except = 0)
    {
        var data = Encoding.UTF8.GetBytes(obj.ToJsonString());
        return Task.WhenAll(members.Values.Where(m => m.Id != except).Select(m => SendRaw(m, data, WebSocketMessageType.Text)));
    }

    // a shared-code guest can't take a name that belongs to a personal code
    string GuestName(string name)
    {
        name = (name ?? "").Trim();
        if (auth.PersonalNames.Any(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase))) name += " (guest)";
        return name;
    }

    public void StartRevalidation(CancellationToken stop)
    {
        _ = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(15), stop); } catch { return; }
                // promotions / demotions in the codes file apply live
                foreach (var m in members.Values.ToList())
                {
                    var now = auth.Check(m.Code);
                    if (now == null || now.Role == m.Role) continue;
                    log.LogInformation("{Name} is now {Role}", m.Name, now.Role);
                    m.Role = now.Role;
                    await Broadcast(new JsonObject { ["t"] = "user", ["user"] = m.ToJson() });
                }
                foreach (var m in members.Values.Where(m => auth.Check(m.Code) == null).ToList())
                {
                    log.LogInformation("{Name}'s invite code was removed - disconnecting", m.Name);
                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                        await m.Ws.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "Your invite code was removed", cts.Token);
                    }
                    catch { }
                    m.Ws.Abort();
                }
            }
        });
    }

    string UniqueName(string name)
    {
        name = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (name.Length > 24) name = name[..24];
        if (name.Length == 0) name = "Guest";
        var taken = members.Values.Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidate = name;
        for (int i = 2; taken.Contains(candidate); i++) candidate = $"{name} {i}";
        return candidate;
    }
}
