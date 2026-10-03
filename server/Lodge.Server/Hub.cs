using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Lodge;

public class Member
{
    public int Id;
    public string Name;
    public string ClientInfo;
    public string Code;              // re-checked every 15 s: removing a friend's code disconnects them
    public bool Personal;            // name comes from a personal invite code
    public string Role = "member";
    public WebSocket Ws;
    public string Room;              // voice room id, null = not in voice
    public bool Muted, Deaf;         // set by the person
    public bool ServerMuted;         // set by an officer
    public string Status = "online"; // online | away | busy | dungeon | lfg
    public string Note = "";
    public JsonObject Game;          // what they're playing (from the hub), null = not shared
    public readonly SemaphoreSlim SendLock = new(1, 1);
    public readonly Queue<DateTime> Recent = new(); // message rate limit

    public JsonObject ToJson() => new()
    {
        ["id"] = Id, ["name"] = Name, ["guest"] = !Personal, ["role"] = Role,
        ["room"] = Room, ["voice"] = Room != null, ["muted"] = Muted, ["deaf"] = Deaf, ["serverMuted"] = ServerMuted,
        ["status"] = Status, ["note"] = Note, ["game"] = Game?.DeepClone(),
    };
}

// A feature area of the lodge. Handle returns true when it took the message.
public interface IModule
{
    Task<bool> Handle(Member me, string type, JsonObject m);
}

// Connections, routing to the modules, sending. The features live in Chat/Voice/Presence/Admin.
public class LodgeHub
{
    public const string Version = "2.1.0";

    public readonly LodgeConfig Cfg;
    public readonly Auth Auth;
    public readonly FileStore Files;
    public readonly Channels Channels;
    public readonly ILogger Log;
    public readonly ChatModule Chat;
    public readonly VoiceModule Voice;
    readonly IModule[] modules;
    readonly ConcurrentDictionary<int, Member> members = new();
    int nextId;

    public int Online => members.Count;
    public IEnumerable<Member> Members => members.Values;
    public Member Find(int id) => members.TryGetValue(id, out var m) ? m : null;

    public LodgeHub(LodgeConfig cfg, Auth auth, FileStore files, ILogger log)
    {
        Cfg = cfg; Auth = auth; Files = files; Log = log;
        Channels = new Channels(cfg.DataDir);
        Chat = new ChatModule(this);
        Voice = new VoiceModule(this);
        modules = new IModule[] { Chat, Voice, new PresenceModule(this), new AdminModule(this) };
    }

    public async Task Run(WebSocket ws, Identity who, string code, string name, string clientInfo, CancellationToken ct)
    {
        if (members.Count >= Cfg.MaxUsers)
        {
            await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "The lodge is full", ct);
            return;
        }
        // a personal code is one person: a new sign-in replaces the old connection
        if (who.Personal)
            foreach (var old in members.Values.Where(m => m.Personal && Same(m.Name, who.Name)).ToList())
            {
                Log.LogInformation("{Name} signed in again - closing the older connection", old.Name);
                await Disconnect(old, "Signed in somewhere else with this code");
            }

        var me = new Member
        {
            Id = Interlocked.Increment(ref nextId), Name = UniqueName(who.Personal ? who.Name : GuestName(name)),
            ClientInfo = clientInfo, Ws = ws, Code = code, Personal = who.Personal, Role = who.Role,
        };
        members[me.Id] = me;
        Log.LogInformation("{Name} joined as {Role} ({Client})", me.Name, me.Role, clientInfo);
        try
        {
            await Send(me, Welcome(me));
            await Broadcast(new JsonObject { ["t"] = "join", ["user"] = me.ToJson() }, m => m.Id != me.Id);
            await ReceiveLoop(me, ct);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException) { }
        finally
        {
            if (members.TryRemove(me.Id, out _)) // not already removed (newer sign-in, kick)
            {
                Log.LogInformation("{Name} left", me.Name);
                await Broadcast(new JsonObject { ["t"] = "leave", ["id"] = me.Id });
            }
        }
    }

    // everything a client needs: who it is, the channels it may see with their history, who's here
    public JsonObject Welcome(Member me)
    {
        var visible = Channels.VisibleTo(me.Role).ToList();
        var history = new JsonObject();
        foreach (var ch in visible.Where(c => c.Type == "text")) history[ch.Id] = Chat.HistoryFor(ch.Id);
        return new JsonObject
        {
            ["t"] = "welcome", ["you"] = me.Id, ["name"] = me.Name, ["role"] = me.Role, ["server"] = Version,
            ["maxFileMb"] = Cfg.MaxFileMb,
            ["canShareFiles"] = Roles.Can(me.Role, Perm.ShareFiles),
            ["canModerate"] = Roles.Can(me.Role, Perm.Moderate),
            ["canManage"] = Roles.Can(me.Role, Perm.ManageMembers),
            ["channels"] = new JsonArray(visible.Select(c => (JsonNode)c.ToJson()).ToArray()),
            ["users"] = new JsonArray(members.Values.Select(m => (JsonNode)m.ToJson()).ToArray()),
            ["history"] = history,
        };
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

            if (r.MessageType == WebSocketMessageType.Binary) { await Voice.Relay(me, msg.ToArray()); continue; }
            JsonObject m;
            try { m = JsonNode.Parse(Encoding.UTF8.GetString(msg.GetBuffer(), 0, (int)msg.Length))?.AsObject(); } catch { continue; }
            if (m == null) continue;
            var type = (string)m["t"] ?? "";
            if (type == "ping") { await Send(me, new JsonObject { ["t"] = "pong" }); continue; }
            foreach (var mod in modules)
                if (await mod.Handle(me, type, m)) break;
        }
    }

    // ---------------------------------------------------------------- sending

    public Task Send(Member to, JsonObject obj) => SendRaw(to, Encoding.UTF8.GetBytes(obj.ToJsonString()), WebSocketMessageType.Text);

    public Task Error(Member to, string text) => Send(to, new JsonObject { ["t"] = "error", ["text"] = text });

    public async Task SendRaw(Member to, byte[] data, WebSocketMessageType type)
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

    public Task Broadcast(JsonObject obj, Func<Member, bool> where = null)
    {
        var data = Encoding.UTF8.GetBytes(obj.ToJsonString());
        return Task.WhenAll(members.Values.Where(m => where == null || where(m)).Select(m => SendRaw(m, data, WebSocketMessageType.Text)));
    }

    public Task BroadcastUser(Member m) => Broadcast(new JsonObject { ["t"] = "user", ["user"] = m.ToJson() });

    // channels changed (or someone's rank did): everyone gets their own up-to-date view
    public Task Resync(Func<Member, bool> where = null) =>
        Task.WhenAll(members.Values.Where(m => where == null || where(m)).Select(m => Send(m, Welcome(m))));

    // close with a reason the client shows (a clean close stops it reconnecting)
    public async Task Disconnect(Member m, string reason)
    {
        if (!members.TryRemove(m.Id, out _)) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await m.Ws.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, reason, cts.Token);
        }
        catch { }
        var ws = m.Ws;
        _ = Task.Delay(5000).ContinueWith(_ => { if (ws.State != WebSocketState.Closed) ws.Abort(); });
        await Broadcast(new JsonObject { ["t"] = "leave", ["id"] = m.Id });
    }

    // ---------------------------------------------------------------- codes file changes (lodge-admin or the hub)

    public void StartRevalidation(CancellationToken stop)
    {
        _ = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(15), stop); } catch { return; }
                await ApplyCodeChanges();
            }
        });
    }

    // promotions apply live, removed codes disconnect
    public async Task ApplyCodeChanges()
    {
        foreach (var m in members.Values.ToList())
        {
            var now = Auth.Check(m.Code);
            if (now == null)
            {
                Log.LogInformation("{Name}'s invite code was removed - disconnecting", m.Name);
                await Disconnect(m, "Your invite code was removed");
            }
            else if (now.Role != m.Role)
            {
                Log.LogInformation("{Name} is now {Role}", m.Name, now.Role);
                m.Role = now.Role;
                if (m.Room != null && Channels.Get(m.Room)?.VisibleTo(m.Role) != true) m.Room = null;
                await BroadcastUser(m);
                await Send(m, Welcome(m)); // channels they may see changed
            }
        }
    }

    // ---------------------------------------------------------------- names

    public static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    string GuestName(string name)
    {
        name = (name ?? "").Trim();
        if (Auth.PersonalNames.Any(p => Same(p, name))) name += " (guest)";
        return name;
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
