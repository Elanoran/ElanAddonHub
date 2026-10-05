using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Lodge;

// Message budgets of one identity (a personal code, or a guest's IP), shared by its connections and kept across
// reconnects for a while. Normal use - typing, 8 chat lines per 10 s, a 20 ms Opus stream - stays well inside.
public sealed class Budget
{
    public readonly TokenBucket Chat;          // 8 per 10 s
    public readonly TokenBucket Control;       // typing/state/status/voice/game/admin: burst 30, 5 per s
    public readonly TokenBucket VoicePackets;  // burst 150, 60 per s (50 per s is normal)
    public readonly TokenBucket VoiceBytes;    // burst 128 KB, 24 KB per s (~6x a 32 kbit/s stream)
    public readonly TokenBucket ProfileSet;    // profile:set: 1 per 2 s (a burst of 1)
    public readonly TokenBucket DisplayName;   // changing the display name: 1 per 60 s (a burst of 1)
    public DateTime LastUsed;

    public Budget(IClock clock)
    {
        Chat = new TokenBucket(8, 0.8, clock);
        Control = new TokenBucket(30, 5, clock);
        ProfileSet = new TokenBucket(1, 0.5, clock);
        DisplayName = new TokenBucket(1, 1.0 / 60, clock);
        VoicePackets = new TokenBucket(150, 60, clock);
        VoiceBytes = new TokenBucket(128 * 1024, 24 * 1024, clock);
        LastUsed = clock.UtcNow;
    }
}

public class Member
{
    public int Id;
    public string Name;
    public string ClientInfo;
    public string Ip;
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
    public bool Invisible;           // "Appear offline": connected, but hidden from everyone without Perm.SeeInvisible
    public Profile Profile = new();  // personal: the stored one (shared by reconnects); guests: session-only
    public Budget Budget;
    public readonly ConcurrentDictionary<string, DateTime> LastTyping = new();
    public readonly SemaphoreSlim SendLock = new(1, 1);

    // refused messages in the last minute: a client that keeps flooding gets disconnected
    readonly Queue<DateTime> drops = new();
    public int CountDrop(IClock clock)
    {
        lock (drops) { drops.Enqueue(clock.UtcNow); return Prune(clock); }
    }

    public int RecentDrops(IClock clock) { lock (drops) return Prune(clock); }

    int Prune(IClock clock)
    {
        var now = clock.UtcNow;
        while (drops.Count > 0 && now - drops.Peek() > TimeSpan.FromMinutes(1)) drops.Dequeue();
        return drops.Count;
    }

    // masked = only what a voice room needs (an invisible member who joined a room is shown there, nothing else:
    // no status, note or game)
    public JsonObject ToJson(bool masked = false) => new()
    {
        ["id"] = Id, ["name"] = Name, ["guest"] = !Personal, ["role"] = Role,
        ["room"] = Room, ["voice"] = Room != null, ["muted"] = Muted, ["deaf"] = Deaf, ["serverMuted"] = ServerMuted,
        ["avatarId"] = Profile?.AvatarId, ["accent"] = Profile?.Accent, ["displayName"] = Profile?.DisplayName ?? "",
        ["status"] = masked ? "online" : Status, ["note"] = masked ? "" : Note, ["game"] = masked ? null : Game?.DeepClone(),
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
    public const string Version = "2.7.0";
    public const int FloodDropsPerMinute = 200; // refused control/chat messages before the connection is closed
    const int MaxFrame = 64 * 1024;

    public readonly LodgeConfig Cfg;
    public readonly Auth Auth;
    public readonly FileStore Files;
    public readonly Channels Channels;
    public readonly ILogger Log;
    public readonly IClock Clock;
    public readonly ProfileStore Profiles;
    public readonly ProfileModule ProfileMod;
    public readonly ChatModule Chat;
    public readonly VoiceModule Voice;
    readonly IModule[] modules;
    readonly ConcurrentDictionary<int, Member> members = new();
    readonly ConcurrentDictionary<string, Budget> budgets = new();
    readonly object admission = new();
    int nextId;

    public int Online => members.Count;
    // what the public /health reports: invisible members are not counted
    public int VisibleOnline => members.Values.Count(m => !m.Invisible);
    public IEnumerable<Member> Members => members.Values;
    public Member Find(int id) => members.TryGetValue(id, out var m) ? m : null;

    public LodgeHub(LodgeConfig cfg, Auth auth, FileStore files, ILogger log, IClock clock = null)
    {
        Cfg = cfg; Auth = auth; Files = files; Log = log; Clock = clock ?? SystemClock.Instance;
        Channels = new Channels(cfg.DataDir);
        Profiles = new ProfileStore(cfg.DataDir, log, Clock);
        ProfileMod = new ProfileModule(this);
        Chat = new ChatModule(this);
        Voice = new VoiceModule(this);
        modules = new IModule[] { Chat, Voice, new PresenceModule(this), ProfileMod, new AdminModule(this) };
    }

    // personal codes: one budget per person; guests (shared code): one per IP, so several friends on one guest
    // code are separate unless they share an address
    public Budget BudgetFor(Identity who, string ip)
    {
        var key = who.Personal ? "p:" + Names.Normalize(who.Name).ToLowerInvariant() : "g:" + ip;
        var b = budgets.GetOrAdd(key, _ => new Budget(Clock));
        b.LastUsed = Clock.UtcNow;
        return b;
    }

    // atomic: the count check and the add happen under one lock, so simultaneous joins can't pass the cap
    public bool TryAdmit(Member me)
    {
        lock (admission)
        {
            if (members.Count >= Cfg.MaxUsers) return false;
            members[me.Id] = me;
            return true;
        }
    }

    public int NextId() => Interlocked.Increment(ref nextId);

    public async Task Run(WebSocket ws, Identity who, string code, string name, string clientInfo, string ip, CancellationToken ct, bool invisible = false)
    {
        // a personal code is one person: a new sign-in replaces the old connection first, so reconnecting at
        // capacity works and doesn't need an extra slot
        if (who.Personal)
            foreach (var old in members.Values.Where(m => m.Personal && Names.Same(m.Name, who.Name)).ToList())
            {
                Log.LogInformation("{Name} signed in again - closing the older connection", old.Name);
                await Disconnect(old, "Signed in somewhere else with this code");
            }

        var me = new Member
        {
            Id = NextId(), Ws = ws, Code = code, Personal = who.Personal, Role = who.Role, Ip = ip,
            ClientInfo = Names.ForLog(clientInfo, 32), Budget = BudgetFor(who, ip), Invisible = invisible,
        };
        lock (admission) me.Name = who.Personal ? UniqueName(Names.Normalize(who.Name)) : UniqueName(GuestName(name), Auth.PersonalNames);
        me.Profile = who.Personal ? Profiles.For(me.Name) : new Profile(); // guests: a session-only profile, never stored
        if (!TryAdmit(me))
        {
            try { await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "The lodge is full", ct); } catch { }
            return;
        }
        Log.LogInformation("{Name} joined as {Role} ({Client})", me.Name, me.Role, me.ClientInfo);
        try
        {
            await Send(me, Welcome(me));
            await BroadcastJoin(me);
            await ReceiveLoop(me, ct);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException) { }
        finally
        {
            if (members.TryRemove(me.Id, out _)) // not already removed (newer sign-in, kick, flood)
            {
                me.Budget.LastUsed = Clock.UtcNow;
                Log.LogInformation("{Name} left", me.Name);
                await BroadcastLeave(me);
            }
        }
    }

    // everything a client needs: who it is, the channels it may see with their history, who's here
    public JsonObject Welcome(Member me)
    {
        var visible = Channels.VisibleTo(me.Role).ToList();
        var history = new JsonObject();
        var pins = new JsonObject();
        foreach (var ch in visible.Where(c => c.Type == "text")) { history[ch.Id] = Chat.HistoryFor(ch.Id); pins[ch.Id] = Chat.Pins.For(ch.Id); }
        return new JsonObject
        {
            ["t"] = "welcome", ["you"] = me.Id, ["name"] = me.Name, ["role"] = me.Role, ["server"] = Version,
            ["maxFileMb"] = Cfg.MaxFileMb,
            ["canShareFiles"] = Roles.Can(me.Role, Perm.ShareFiles),
            ["canModerate"] = Roles.Can(me.Role, Perm.Moderate),
            ["canManage"] = Roles.Can(me.Role, Perm.ManageMembers),
            ["canPin"] = Roles.Can(me.Role, Perm.PinMessages),
            ["features"] = new JsonArray("reply", "react", "react2", "pin", "profile", "displayname"),
            ["reactions"] = new JsonArray(ChatModule.ReactionSet.Select(e => (JsonNode)JsonValue.Create(e)).ToArray()), // canonical ids (2.4)
            ["pins"] = pins,
            ["channels"] = new JsonArray(visible.Select(c => (JsonNode)c.ToJson()).ToArray()),
            ["users"] = new JsonArray(members.Values.Where(m => CanSee(me, m)).Select(m => (JsonNode)UserFor(m, me)).ToArray()),
            ["profile"] = ProfileMod.OwnView(me),
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
                if (msg.Length > MaxFrame) return; // nobody needs frames that big
            } while (!r.EndOfMessage);

            if (r.MessageType == WebSocketMessageType.Binary)
            {
                // voice over budget is dropped quietly (a burst after a hiccup shouldn't cost anything else)
                if (me.Budget.VoicePackets.TryTake() && me.Budget.VoiceBytes.TryTake(msg.Length))
                    await Voice.Relay(me, msg.ToArray());
                continue;
            }
            JsonObject m;
            try { m = JsonNode.Parse(Encoding.UTF8.GetString(msg.GetBuffer(), 0, (int)msg.Length))?.AsObject(); } catch { continue; }
            if (m == null) continue;
            var type = (string)m["t"] ?? "";
            if (type != "msg" && !me.Budget.Control.TryTake())
            {
                if (me.CountDrop(Clock) > FloodDropsPerMinute) { await Disconnect(me, "Too many messages - slow down"); return; }
                continue;
            }
            if (type == "ping") { await Send(me, new JsonObject { ["t"] = "pong" }); continue; }
            foreach (var mod in modules)
                if (await mod.Handle(me, type, m)) break;
            if (me.RecentDrops(Clock) > FloodDropsPerMinute) { await Disconnect(me, "Too many messages - slow down"); return; }
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

    // ---- visibility ("Appear offline"). An invisible member stays connected and receives everything; everyone else
    // is told about them only where the rules below allow: themselves, anyone with Perm.SeeInvisible (full entry plus
    // "invisible": true), and - only while they sit in a voice room - people who may see that room (masked entry:
    // no status, note or game). Every roster/join/leave/user message goes through these helpers.
    public bool CanSee(Member viewer, Member subject)
    {
        if (!subject.Invisible || viewer.Id == subject.Id) return true;
        if (Roles.Can(viewer.Role, Perm.SeeInvisible)) return true;
        return subject.Room != null && Channels.Get(subject.Room)?.VisibleTo(viewer.Role) == true;
    }

    public JsonObject UserFor(Member subject, Member viewer)
    {
        if (!subject.Invisible) return subject.ToJson();
        bool full = viewer.Id == subject.Id || Roles.Can(viewer.Role, Perm.SeeInvisible);
        var j = subject.ToJson(masked: !full);
        if (full) j["invisible"] = true;
        return j;
    }

    public Task BroadcastJoin(Member me) =>
        Task.WhenAll(members.Values.Where(v => v.Id != me.Id && CanSee(v, me))
            .Select(v => Send(v, new JsonObject { ["t"] = "join", ["user"] = UserFor(me, v) })));

    // `gone` is already out of the member table
    public Task BroadcastLeave(Member gone) =>
        Task.WhenAll(members.Values.Where(v => CanSee(v, gone))
            .Select(v => Send(v, new JsonObject { ["t"] = "leave", ["id"] = gone.Id })));

    // apply a change that may alter who can see `me` (visibility, voice room): viewers who gain sight get a join,
    // who lose it a leave, who keep it an update
    public async Task Mutate(Member me, Action change)
    {
        var before = members.Values.Where(v => v.Id != me.Id && CanSee(v, me)).Select(v => v.Id).ToHashSet();
        change();
        var sends = new List<Task>();
        foreach (var v in members.Values)
        {
            if (v.Id == me.Id) { sends.Add(Send(v, new JsonObject { ["t"] = "user", ["user"] = UserFor(me, v) })); continue; }
            bool was = before.Contains(v.Id), now = CanSee(v, me);
            if (now) sends.Add(Send(v, new JsonObject { ["t"] = was ? "user" : "join", ["user"] = UserFor(me, v) }));
            else if (was) sends.Add(Send(v, new JsonObject { ["t"] = "leave", ["id"] = me.Id }));
        }
        await Task.WhenAll(sends);
    }

    public Task BroadcastUser(Member m) => Mutate(m, () => { });

    // status / game updates: an invisible member's go to themselves and to Perm.SeeInvisible only (never to the
    // voice-room viewers, who only get the masked entry)
    public Task BroadcastPresence(Member m)
    {
        if (!m.Invisible) return BroadcastUser(m);
        return Task.WhenAll(members.Values.Where(v => v.Id == m.Id || Roles.Can(v.Role, Perm.SeeInvisible))
            .Select(v => Send(v, new JsonObject { ["t"] = "user", ["user"] = UserFor(m, v) })));
    }

    // avatar/accent changed: everyone who can see the member (and the member) gets a small "profile" update
    public Task BroadcastProfile(Member m)
    {
        var msg = new JsonObject { ["t"] = "profile", ["id"] = m.Id, ["name"] = m.Name, ["avatarId"] = m.Profile?.AvatarId, ["accent"] = m.Profile?.Accent, ["displayName"] = m.Profile?.DisplayName ?? "" };
        return Broadcast(msg, v => CanSee(v, m));
    }

    // channels changed (or someone's rank did): everyone gets their own up-to-date view
    public Task Resync(Func<Member, bool> where = null) =>
        Task.WhenAll(members.Values.Where(m => where == null || where(m)).Select(m => Send(m, Welcome(m))));

    // close with a reason the client shows (a clean close stops it reconnecting); frees the slot right away
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
        await BroadcastLeave(m);
    }

    // ---------------------------------------------------------------- housekeeping

    public void StartRevalidation(CancellationToken stop)
    {
        _ = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(15), stop); } catch { return; }
                try
                {
                    await ApplyCodeChanges();
                    Auth.Strikes.Sweep();
                    SweepBudgets();
                }
                catch (Exception e) { Log.LogWarning("Housekeeping failed: {Error}", e.GetType().Name); }
            }
        });
    }

    // budgets of identities with no connection for 10 minutes are forgotten
    public int SweepBudgets()
    {
        var active = members.Values.Select(m => m.Budget).ToHashSet();
        var cutoff = Clock.UtcNow - TimeSpan.FromMinutes(10);
        int n = 0;
        foreach (var kv in budgets)
            if (!active.Contains(kv.Value) && kv.Value.LastUsed < cutoff && budgets.TryRemove(kv.Key, out _)) n++;
        return n;
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

    // ---------------------------------------------------------------- names (all compared in Names.Normalize form)

    public static bool Same(string a, string b) => Names.Same(a, b);

    // a guest can't look like a personal member: their normalized name gets " (guest)" when it matches one,
    // shortened to fit the 24 characters
    public string GuestName(string name)
    {
        name = Names.Normalize(name);
        if (name.Length == 0) name = "Guest";
        if (Auth.PersonalNames.Any(p => Names.Same(p, name)))
        {
            const string tag = " (guest)";
            name = name.Length + tag.Length > Names.Max ? name[..(Names.Max - tag.Length)].TrimEnd() + tag : name + tag;
        }
        return name;
    }

    // reserved: for guests, every personal name - a " 2" suffix must not land on one either
    public string UniqueName(string name, IEnumerable<string> reserved = null)
    {
        name = Names.Normalize(name);
        if (name.Length == 0) name = "Guest";
        var taken = members.Values.Select(m => Names.Normalize(m.Name).ToLowerInvariant()).ToHashSet();
        if (reserved != null) foreach (var r in reserved) taken.Add(Names.Normalize(r).ToLowerInvariant());
        var candidate = name;
        for (int i = 2; taken.Contains(candidate.ToLowerInvariant()); i++)
        {
            var suffix = " " + i;
            candidate = (name.Length + suffix.Length > Names.Max ? name[..(Names.Max - suffix.Length)].TrimEnd() : name) + suffix;
        }
        return candidate;
    }
}
