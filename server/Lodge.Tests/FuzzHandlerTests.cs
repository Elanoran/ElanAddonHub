using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Channel = System.Threading.Channels.Channel;
using Elans.Shared;
using Elans.Shared.Tests;
using Xunit;
using Xunit.Abstractions;
using static Elans.Shared.Tests.FuzzGen;

namespace Lodge.Tests;

// A WebSocket driven by the test, for the real receive loop (LodgeHub.Run): frames are queued, ReceiveAsync hands them out in pieces of
// the server's buffer (or smaller, Chunk: a fragmented frame), and asking for the next frame tells the test the previous one was handled.
// What the server sends is kept as text until the test drains it.
public sealed class ScriptSock : WebSocket
{
    readonly Channel<(byte[] Data, WebSocketMessageType Type)> queue = System.Threading.Channels.Channel.CreateUnbounded<(byte[], WebSocketMessageType)>();
    byte[] cur; int pos; WebSocketMessageType curType;
    volatile WebSocketState state = WebSocketState.Open;
    volatile TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly List<string> sent = new();
    public string CloseReason;
    public int Chunk;                  // > 0: hand frames out in pieces of this size
    public int BinarySent;

    public override WebSocketCloseStatus? CloseStatus => state == WebSocketState.Open ? null : WebSocketCloseStatus.NormalClosure;
    public override string CloseStatusDescription => CloseReason;
    public override WebSocketState State => state;
    public override string SubProtocol => null;
    public override void Abort() { state = WebSocketState.Aborted; queue.Writer.TryComplete(); }
    public override Task CloseAsync(WebSocketCloseStatus s, string d, CancellationToken ct) { CloseReason ??= d; state = WebSocketState.Closed; queue.Writer.TryComplete(); return Task.CompletedTask; }
    public override Task CloseOutputAsync(WebSocketCloseStatus s, string d, CancellationToken ct) => CloseAsync(s, d, ct);
    public override void Dispose() { }

    // the server is waiting for the next frame: everything before it was handled
    public Task Ready => ready.Task;

    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
    {
        if (cur == null)
        {
            ready.TrySetResult();
            (byte[] Data, WebSocketMessageType Type) next;
            try { next = await queue.Reader.ReadAsync(ct); }
            catch (ChannelClosedException) { return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true); }
            if (next.Type == WebSocketMessageType.Close) { state = WebSocketState.CloseReceived; return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true); }
            cur = next.Data; pos = 0; curType = next.Type;
        }
        int n = Math.Min(buffer.Count, cur.Length - pos);
        if (Chunk > 0) n = Math.Min(n, Chunk);
        Array.Copy(cur, pos, buffer.Array!, buffer.Offset, n);
        pos += n;
        bool end = pos == cur.Length;
        var type = curType;
        if (end) cur = null;
        return new WebSocketReceiveResult(n, type, end);
    }

    // queue a frame; the returned task completes when the server asks for the next one (or the connection ends)
    public Task Feed(byte[] data, WebSocketMessageType type = WebSocketMessageType.Text)
    {
        ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Writer.TryWrite((data, type));
        return ready.Task;
    }

    public Task Feed(string text) => Feed(Encoding.UTF8.GetBytes(text));

    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType t, bool end, CancellationToken ct)
    {
        if (state != WebSocketState.Open) throw new WebSocketException("closed");
        if (t == WebSocketMessageType.Binary) { Interlocked.Increment(ref BinarySent); return Task.CompletedTask; }
        lock (sent) sent.Add(Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count));
        return Task.CompletedTask;
    }

    public List<string> Drain() { lock (sent) { var l = sent.ToList(); sent.Clear(); return l; } }
}

// Fuzzes the real Lodge hub through its receive loop: thousands of random and mutated frames of every LodgeMsg type, from every rank, with
// oversized, fragmented, deeply nested, invalid UTF-8 and binary frames mixed with normal traffic. Watched all the way: no handler throws
// (the hub logs a dropped frame), no connection loop crashes (a Run task that faults), no frame reaches someone the rank rules forbid
// (admin answers to lower ranks, a code / token / invite secret anywhere but its one answer, an invisible member to anyone below the
// Guild Master), memory stays bounded, and afterwards a ping and a chat line still work. Deterministic per seed; ELANS_FUZZ=deep for more.
// alone, not next to other test classes: the memory check reads the whole process (GC.GetTotalMemory)
[CollectionDefinition("FuzzHandlerTests", DisableParallelization = true)]
public sealed class FuzzHandlerCollection { }

[Collection("FuzzHandlerTests")]
public class FuzzHandlerTests
{
    readonly ITestOutputHelper output;
    public FuzzHandlerTests(ITestOutputHelper output) { this.output = output; }

    const string GuestCode = "synthetic-guest-code-0000000000x";
    const string MiaCode = "synthetic0mia00code0000000000003";
    const string ObsCode = "synthetic0obs00code0000000000004";
    const string IvyCode = "synthetic0ivy00code0000000000005";
    const int MaxFrame = 64 * 1024;                    // LodgeHub.MaxFrame
    static readonly TimeSpan FrameLimit = TimeSpan.FromSeconds(20);

    sealed class Person
    {
        public string Name, Role, Cred, GuestName; public bool Token, Invisible, Observer;
        public ScriptSock Sock; public Task Run; public int Id;
        public int Kicked;
    }

    sealed class Rig : IDisposable
    {
        public readonly TempDir Dir = new();
        public readonly FakeClock Clock = new();
        public readonly CaptureLogger Log = new();
        public readonly LodgeHub Hub;
        public readonly Auth Auth;
        public readonly CancellationTokenSource Cts = new();
        public readonly Dictionary<string, Person> People = new();
        public readonly List<string> Problems = new();
        public readonly HashSet<string> Secrets = new(StringComparer.Ordinal);
        public int Frames, LogSeen, CodesRenewed, Refused;

        public Rig()
        {
            var t = LodgeConfig.ForTests(Dir.Path);
            var cfg = new LodgeConfig
            {
                Code = GuestCode, CodesFile = t.CodesFile, Urls = "", DataDir = Dir.Path, PathBase = "", MaxFileMb = 25, FileDays = 30, MaxUsers = 25,
                TrustedProxies = Array.Empty<string>(), MaxStorageBytes = 1 << 30, MaxFiles = 100, MaxConcurrentUploads = 4, UploadsPer10Min = 30,
                HistoryMaxBytes = 1 << 20, HistoryMax = 200, MaxTrackedIps = 1000, HttpPerMinute = 240, AllowQueryCode = true,
            };
            T.WriteCodes(cfg, $"Elan:{T.ElanCode}:owner", $"Bob:{T.BobCode}:officer", $"Mia:{MiaCode}:member", $"Obs:{ObsCode}:member", $"Ivy:{IvyCode}:member");
            Auth = new Auth(cfg, Log, Clock);
            Hub = new LodgeHub(cfg, Auth, new FileStore(cfg), Log, Clock);
            foreach (var s in new[] { T.ElanCode, T.BobCode, MiaCode, ObsCode, IvyCode, GuestCode }) Secrets.Add(s);
        }

        public void Add(Person p) => People[p.Name] = p;

        public async Task Connect(Person p)
        {
            var who = p.Token ? Auth.CheckToken(p.Cred) : Auth.Check(p.Cred);
            if (who == null && !p.Token && p.Role != "guest")
            {
                // the fuzz took the code away (member.leave, admin.remove / admin.retire of a guessed name): a new one, as the Guild Master would
                try { p.Cred = Auth.Add(p.Name, p.Role); } catch (InvalidOperationException) { }
                Secrets.Add(p.Cred);
                CodesRenewed++;
                who = Auth.Check(p.Cred);
            }
            if (who == null) { p.Run = Task.CompletedTask; return; }      // still no way in: out of the run (reported as skipped)
            p.Sock = new ScriptSock();
            p.Run = Hub.Run(p.Sock, who, p.Cred, p.GuestName, "fuzz/1", "10.9.0." + (1 + People.Values.ToList().IndexOf(p)), Cts.Token, p.Invisible);
            await Wait(p, p.Sock.Ready, "connecting " + p.Name);
            Scan();
        }

        public bool Alive(Person p) => p.Run != null && !p.Run.IsCompleted;

        public async Task Wait(Person p, Task t, string what)
        {
            var done = await Task.WhenAny(t, p.Run, Task.Delay(FrameLimit));
            if (done != t && done != p.Run) Problems.Add(what + ": no answer within " + FrameLimit.TotalSeconds + " s (a hang?)");
            if (p.Run.IsFaulted) { Problems.Add(what + ": the connection loop of " + p.Name + " crashed: " + p.Run.Exception!.InnerException); p.Run = Task.CompletedTask; }
        }

        public async Task Send(Person p, byte[] data, WebSocketMessageType type, string what)
        {
            if (!Alive(p)) await Connect(p);
            if (!Alive(p)) return;
            Frames++;
            if (type == WebSocketMessageType.Text && LodgeJson.Parse(data) == null) Refused++;
            await Wait(p, p.Sock.Feed(data, type), what);
            Scan(what);
            CheckLog(what, data, type);
        }

        public Task Send(Person p, string text, string what = null) => Send(p, Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, what ?? Clip(text));

        // ---------------------------------------------------------------- what the server sent

        public readonly Dictionary<string, int> Received = new();
        public readonly List<(Person P, JsonObject F)> Kept = new();
        public bool Keep;                 // keep the frames (the targeted tests look at them)

        static readonly HashSet<string> OwnerOnly = new() { LodgeMsg.AdminMembers, LodgeMsg.AdminInvited, LodgeMsg.AdminPurged };
        static readonly HashSet<string> OfficerUp = new() { LodgeMsg.AdminInviteCreated, LodgeMsg.AdminInvites, LodgeMsg.AdminDevices };
        static readonly HashSet<string> Presence = new() { LodgeMsg.Join, LodgeMsg.User, LodgeMsg.Leave, LodgeMsg.MemberPos };

        public void Scan(string what = "")
        {
            var batch = People.Values.Where(p => p.Sock != null).SelectMany(p => p.Sock.Drain().Select(text => (p, text))).ToList();
            // a secret made just now goes to whoever asked, in its one answer: learn it first
            var parsed = new List<(Person P, string Text, JsonObject F)>();
            foreach (var (p, text) in batch)
            {
                JsonObject f = null;
                try { f = JsonNode.Parse(text) as JsonObject; } catch (JsonException) { }
                if (f == null) { Problems.Add(what + ": the server sent " + p.Name + " no JSON object: " + Clip(text)); continue; }
                parsed.Add((p, text, f));
                var t = (string)f["t"];
                if (t == LodgeMsg.AdminInvited && f["code"] is JsonValue c && c.TryGetValue<string>(out var code) && code.Length > 0) Secrets.Add(code);
                if (t == LodgeMsg.AdminInviteCreated && f["secret"] is JsonValue s && s.TryGetValue<string>(out var secret) && secret.Length > 0) Secrets.Add(secret);
                if (t == LodgeMsg.Welcome && Num(f, "you") is double you) p.Id = (int)you;
            }
            var ivy = People.TryGetValue("Ivy", out var iv) ? iv.Id : -1;
            foreach (var (p, text, f) in parsed)
            {
                var t = (string)f["t"] ?? "";
                lock (Received) Received[t] = Received.GetValueOrDefault(t) + 1;
                if (Keep) Kept.Add((p, f));
                if (OwnerOnly.Contains(t) && p.Role != "owner") Problems.Add(what + ": " + p.Name + " (" + p.Role + ") received " + t);
                if (OfficerUp.Contains(t) && p.Role is not ("owner" or "officer")) Problems.Add(what + ": " + p.Name + " (" + p.Role + ") received " + t);
                // a secret: only in the answer that hands it to the one who asked (admin.invited / admin.invite.created), never anywhere else
                bool carrier = (t == LodgeMsg.AdminInvited || t == LodgeMsg.AdminInviteCreated) && p.Role is "owner" or "officer";
                foreach (var secret in Secrets)
                    if (text.Contains(secret, StringComparison.Ordinal) && !(carrier && (Str(f, "code") == secret || Str(f, "secret") == secret)))
                        Problems.Add(what + ": a secret reached " + p.Name + " in " + t + ": " + Clip(text));
                // the invisible member: never shown to anyone below the Guild Master (she never joins a voice room, so not even masked)
                if (ivy > 0 && p.Name != "Ivy" && p.Role != "owner")
                {
                    if (Presence.Contains(t) && (int?)Num(f, "id") == ivy) Problems.Add(what + ": " + p.Name + " (" + p.Role + ") was told about the invisible member: " + Clip(text));
                    if (t == LodgeMsg.Join && f["user"] is JsonObject u && (int?)Num(u, "id") == ivy) Problems.Add(what + ": " + p.Name + " got the invisible member's join");
                    if (t == LodgeMsg.User && f["user"] is JsonObject u2 && (int?)Num(u2, "id") == ivy) Problems.Add(what + ": " + p.Name + " got the invisible member's user entry");
                    if (t == LodgeMsg.Welcome && f["users"] is JsonArray us && us.OfType<JsonObject>().Any(x => (int?)Num(x, "id") == ivy))
                        Problems.Add(what + ": " + p.Name + " (" + p.Role + ") saw the invisible member in welcome.users");
                }
                // an error tells the person what to do, never what broke inside (AdminModule sends e.Message of whatever it caught)
                if (t == LodgeMsg.Error && (text.Contains("Object reference", StringComparison.Ordinal) || text.Contains("Exception", StringComparison.Ordinal)
                    || text.Contains("   at ", StringComparison.Ordinal) || text.Contains("Index was outside", StringComparison.Ordinal)))
                    Problems.Add(what + ": an error frame shows an internal failure: " + Clip(text));
            }
        }

        static string Str(JsonObject f, string k) => f[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        static double? Num(JsonObject f, string k) => f[k] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

        // ---------------------------------------------------------------- the log: a handler that threw

        public void CheckLog(string what, byte[] data, WebSocketMessageType type)
        {
            List<string> fresh;
            lock (Log.Lines) { fresh = Log.Lines.Skip(LogSeen).ToList(); LogSeen = Log.Lines.Count; }
            foreach (var line in fresh)
            {
                // the hub's safety net: a handler threw (ReceiveLoop logs it and drops the frame); AdminModule's own catch logs "<type> failed"
                bool dropped = line.Contains("dropped a malformed", StringComparison.Ordinal);
                bool adminFailed = line.Contains(" failed System.", StringComparison.Ordinal) && !line.Contains("System.InvalidOperationException", StringComparison.Ordinal);
                if (!dropped && !adminFailed) continue;
                Problems.Add(what + ": a handler threw - " + Clip(line, 600));
            }
        }

        public void Dispose() { Cts.Cancel(); Dir.Dispose(); }
    }

    static string Clip(string s, int n = 240) => s.Length > n ? s[..n] + "... (" + s.Length + " chars)" : s;

    // the members: four who fuzz (one per rank), three who only watch (a member signed in with a device token, a guest, and an invisible
    // member - "Appear offline")
    static async Task<Rig> Lodge()
    {
        var r = new Rig();
        var obsToken = r.Auth.ExchangeCode("10.9.9.9", ObsCode, "Obs's laptop", "fuzz-device-0001").Token;
        Assert.NotNull(obsToken);
        r.Secrets.Add(obsToken);
        r.Add(new Person { Name = "Elan", Role = "owner", Cred = T.ElanCode });
        r.Add(new Person { Name = "Bob", Role = "officer", Cred = T.BobCode });
        r.Add(new Person { Name = "Mia", Role = "member", Cred = MiaCode });
        r.Add(new Person { Name = "Gus", Role = "guest", Cred = GuestCode, GuestName = "Gus" });
        r.Add(new Person { Name = "Obs", Role = "member", Cred = obsToken, Token = true, Observer = true });
        r.Add(new Person { Name = "Wat", Role = "guest", Cred = GuestCode, GuestName = "Wat", Observer = true });
        r.Add(new Person { Name = "Ivy", Role = "member", Cred = IvyCode, Invisible = true, Observer = true });
        foreach (var p in r.People.Values) await r.Connect(p);
        Assert.All(r.People.Values, p => Assert.True(r.Alive(p), p.Name + " did not get in"));
        return r;
    }

    // ================================================================ frames

    static readonly List<Msg> All = Messages();
    static readonly Dictionary<Type, JsonObject> Samples = All.ToDictionary(m => m.Class, m => Sample(m));
    static readonly Dictionary<Type, List<string>> KeysByClass = All.ToDictionary(m => m.Class, m => KeysOf(m, Samples[m.Class]));
    static readonly string[] Types = typeof(LodgeMsg).GetFields().Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()).OrderBy(x => x, StringComparer.Ordinal).ToArray();

    // a sample frame made to look real: the channel "general", ids of people who are here
    static JsonObject Realistic(Msg m, Random r)
    {
        var o = (JsonObject)Samples[m.Class].DeepClone();
        foreach (var k in new[] { "channel", "room" }) if (o.ContainsKey(k) && r.Next(2) == 0) o[k] = "general";
        if (o.ContainsKey("text") && r.Next(2) == 0) o["text"] = "hello " + r.Next(100);
        if (o.ContainsKey("id") && r.Next(3) == 0) o["id"] = o["id"] is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? r.Next(1, 12) : (JsonNode)("m" + r.Next(5));
        return o;
    }

    // real-looking frames for the parts that travel as free-form children (validated field by field on the server): mutated like the samples,
    // so the fuzz gets past the first check of Armory.Clean, PresenceModule.BuildGame / BuildPosition, Cal.Parse, Lfg.Create, AtlasStore.Share
    static readonly JsonObject[] Seeds = new[]
    {
        """{"t":"game","playing":true,"name":"Mia","realm":"Forever","class":"Hunter","classFile":"HUNTER","race":"Night Elf","raceFile":"NightElf","level":30,"sex":3,"zone":"The Barrens","guild":"Friends","xpPct":50,"rested":true,"groupSize":3,"inInstance":false,"instanceName":"","flags":["afk","pvp"]}""",
        """{"t":"position","share":true,"valid":true,"mapId":1411,"fx":0.5123,"fy":0.25,"move":1,"scope":"everyone"}""",
        """{"t":"armory.put","key":"Forever-Fang","char":{"name":"Fang","realm":"Forever","class":"HUNTER","classText":"Hunter","race":"Night Elf","raceFile":"NightElf","sex":3,"faction":"Alliance","guild":"Friends","level":40,"ilvl":41.5,"at":1790000000,"gear":{"at":1790000000,"ilvl":41.5,"slots":{"1":{"i":3000,"q":2,"ic":132,"il":40},"16":{"i":3001,"q":3,"ic":133,"il":45}},"links":{"3000":"|cff1eff00|Hitem:3000::::::::40:::::|h[Ring]|h|r"},"stats":{"health":3000,"power":1200,"str":50,"agi":160,"crit":6.5,"powerType":"MANA","melee":{"min":40,"max":70,"speed":2.1}}},"talents":{"at":1790000000,"api":"traits","spent":40,"unspent":2,"build":"ETB1-AAECAwQFBgcICQoL","nodes":[{"id":11,"r":3,"m":5,"s":900,"ic":100,"x":1.0,"y":2.0,"n":"Deadly Aspects"},{"id":12,"r":0,"m":1}]},"professions":{"at":1790000000,"list":[{"name":"Alchemy","skill":150,"max":300,"ic":5,"kind":"primary"}],"known":{"2329":1}},"pets":{"at":1790000000,"active":"p1","pets":[{"key":"p1","name":"Wolfie","family":"Wolf","level":40,"loyalty":4,"abilities":[{"id":"bite","rank":5}]}],"known":{"bite":6}},"reps":{"at":1790000000,"list":[{"id":72,"s":5,"v":3000,"min":0,"max":6000}]}}}""",
        """{"t":"cal:create","title":"Molten Core","start":1792000000000,"slots":{"tank":2,"healer":3,"dps":5},"tz":"Europe/Berlin","off":120,"roles":true,"note":"bring fire resist","durationMin":180,"post":true,"repeat":{"every":"week","count":4}}""",
        """{"t":"msg","channel":"general","text":"[Looking for group] Deadmines","kind":"lfg","lfg":{"title":"The Deadmines","start":1792000000000,"now":false,"note":"bring water","slots":{"tank":1,"healer":1,"dps":3}}}""",
        """{"t":"msg","channel":"general","text":"see this","replyTo":"m1","file":{"id":"f1","name":"map.png","size":1234,"mime":"image/png"}}""",
        """{"t":"profile:set","avatarId":"wolf","accent":"#aabbcc","about":"hi there","playTimes":"evenings","displayName":"Mimi","main":"Fang","showChars":true,"visibleTo":"members","hidden":["Alt"]}""",
        """{"t":"atlas.share","kills":{"1234":{"n":5,"items":{"2589":[3,1]}}},"vendors":{"5678":{"items":[[159,0,1]]}},"nodes":{"1411":[[0.5,0.5,1731]]},"finds":[{"k":"node","m":1411,"x":0.5,"y":0.5}],"build":11508}""",
        """{"t":"flights.share","legs":{"1-2":{"s":60,"n":3},"2-1":{"s":61,"n":1}},"names":{"1":"Stormwind","2":"Ironforge"}}""",
        """{"t":"flights.routes.share","routes":{"1-2":[0.1,0.2,null,0.3,0.4,12.5]}}""",
        """{"t":"search","q":"molten","channel":"general"}""",
        """{"t":"react","channel":"general","id":"m1","emoji":"thumbsup"}""",
        """{"t":"voice","room":"general-voice"}""",
        """{"t":"admin.invite.create","kind":"person","name":"Newbie","role":"member","hours":24,"uses":1}""",
        """{"t":"admin.cairn","on":true,"publicUrl":"https://lodge.example.com/lodge"}""",
    }.Select(s => JsonNode.Parse(s)!.AsObject()).ToArray();

    static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    // one frame for the fuzz run (text unless said otherwise); what: a short description for a failure message
    static (byte[] Data, WebSocketMessageType Type, string What, int Chunk) NextFrame(Gen g)
    {
        var r = g.R;
        int k = r.Next(100);
        var m = All[r.Next(All.Count)];
        if (k < 25) return (Utf8(Mutate(Realistic(m, r), g)), WebSocketMessageType.Text, "mutated " + m.Type, 0);
        if (k < 35)
        {
            var seed = Seeds[r.Next(Seeds.Length)];
            return (Utf8(r.Next(4) == 0 ? seed.ToJsonString() : Mutate(seed, g)), WebSocketMessageType.Text, "seed " + (string)seed["t"], 0);
        }
        if (k < 50)
        {
            var fields = new List<(string, string)> { ("t", r.Next(10) < 8 ? JsonSerializer.Serialize(m.Type) : g.Value()) };
            foreach (var key in KeysByClass[m.Class]) if (key != "t" && r.Next(10) < 7) fields.Add((JsonSerializer.Serialize(key), g.Value()));
            return (Utf8(g.Obj(fields)), WebSocketMessageType.Text, "random fields " + m.Type, 0);
        }
        if (k < 62) return (Utf8(Realistic(m, r).ToJsonString()), WebSocketMessageType.Text, "sample " + m.Type, 0);
        if (k < 72)
        {
            // normal traffic in between
            var t = r.Next(5) switch
            {
                0 => "{\"t\":\"ping\"}",
                1 => "{\"t\":\"msg\",\"channel\":\"general\",\"text\":\"normal line " + r.Next(1000) + "\"}",
                2 => "{\"t\":\"typing\",\"channel\":\"general\"}",
                3 => "{\"t\":\"status\",\"status\":\"away\",\"note\":\"brb\"}",
                _ => "{\"t\":\"history:before\",\"channel\":\"general\",\"before\":\"x\"}",
            };
            return (Utf8(t), WebSocketMessageType.Text, "normal " + t, 0);
        }
        if (k < 76) return (Utf8("{\"t\":" + JsonSerializer.Serialize(Types[r.Next(Types.Length)]) + "}"), WebSocketMessageType.Text, "type only", 0);
        if (k < 80) return (Utf8(g.Value()), WebSocketMessageType.Text, "a random value", 0);
        if (k < 84)
        {
            var b = new byte[r.Next(0, 3000)]; r.NextBytes(b);
            if (r.Next(2) == 0) b = Utf8("{\"t\":\"ping\"}");
            return (b, WebSocketMessageType.Binary, "binary", 0);
        }
        if (k < 88)
        {
            // invalid UTF-8 inside an otherwise good frame: lone continuation bytes, an overlong NUL, a cut sequence, 0xff
            var bad = new[] { new byte[] { 0x80 }, new byte[] { 0xc0, 0x80 }, new byte[] { 0xe2, 0x82 }, new byte[] { 0xff, 0xfe }, new byte[] { 0xed, 0xa0, 0x80 } }[r.Next(5)];
            var head = Utf8("{\"t\":" + JsonSerializer.Serialize(m.Type) + ",\"text\":\"a");
            var tail = Utf8("b\",\"channel\":\"general\"}");
            return (head.Concat(bad).Concat(tail).ToArray(), WebSocketMessageType.Text, "invalid UTF-8 in " + m.Type, 0);
        }
        if (k < 92) return (Utf8("{\"t\":" + JsonSerializer.Serialize(m.Type) + ",\"x\":" + g.Nest(r.Next(55, 70)) + "}"), WebSocketMessageType.Text, "deep " + m.Type, 0);
        if (k < 96)
        {
            // around the frame cap, sometimes fragmented
            int size = MaxFrame + r.Next(-3, 2);
            var head = "{\"t\":" + JsonSerializer.Serialize(m.Type) + ",\"text\":\"";
            var body = head + new string('x', Math.Max(0, size - head.Length - 2)) + "\"}";
            return (Utf8(body), WebSocketMessageType.Text, "a " + Encoding.UTF8.GetByteCount(body) + "-byte " + m.Type, r.Next(2) == 0 ? 1000 : 0);
        }
        return (Utf8(Mutate(Realistic(m, r), g)), WebSocketMessageType.Text, "fragmented mutated " + m.Type, r.Next(1, 500));
    }

    // ================================================================ the fuzz run

    [Fact]
    public async Task Thousands_of_random_frames_from_every_rank_break_nothing()
    {
        var sw = Stopwatch.StartNew();
        using var r = await Lodge();
        var g = new Gen(0x10d6e);
        var senders = r.People.Values.Where(p => !p.Observer).ToList();
        int n = N(8000, 150_000);
        long memStart = 0;
        int skipped = 0;
        var lastWhat = senders.ToDictionary(p => p.Name, p => "");
        for (int i = 0; i < n; i++)
        {
            if (i == n / 5) memStart = GC.GetTotalMemory(true);
            var p = senders[g.R.Next(senders.Count)];
            var (data, type, what, chunk) = NextFrame(g);
            // kept out: removing the channel the final check talks in
            if (type == WebSocketMessageType.Text && what.Contains(LodgeMsg.AdminChannelRemove, StringComparison.Ordinal)) continue;
            r.Clock.Advance(TimeSpan.FromMilliseconds(g.R.Next(50, 1500)));
            if (!r.Alive(p)) await r.Connect(p);
            if (!r.Alive(p)) { skipped++; if (skipped < 5) output.WriteLine("#" + i + " " + p.Name + " can't come back: " + (p.Sock?.CloseReason ?? "no sign-in") + " after " + lastWhat[p.Name]); continue; }
            lastWhat[p.Name] = what;
            p.Sock.Chunk = chunk;
            await r.Send(p, data, type, "#" + i + " " + p.Name + " (" + p.Role + "): " + what);
            if (p.Sock != null) p.Sock.Chunk = 0;
            if (r.Problems.Count > 30) break;
            // a watcher that was kicked (mod.kick with a random id can hit anyone below the sender) comes back
            foreach (var o in r.People.Values.Where(o => o.Observer && !r.Alive(o)))
            {
                if (o.Sock?.CloseReason?.StartsWith("Kicked by", StringComparison.Ordinal) != true)
                    r.Problems.Add("#" + i + ": the watcher " + o.Name + " lost its connection: " + (o.Sock?.CloseReason ?? "(no reason)"));
                o.Kicked++;
                await r.Connect(o);
            }
        }
        GC.Collect(); GC.WaitForPendingFinalizers();
        long memEnd = GC.GetTotalMemory(true);

        // still working afterwards: every fuzzer gets its pong, a chat line from the Guild Master reaches a watcher
        r.Clock.Advance(TimeSpan.FromMinutes(2));
        foreach (var p in senders)
        {
            if (!r.Alive(p)) await r.Connect(p);
            if (!r.Alive(p)) continue;
            r.Keep = true;
            await r.Send(p, "{\"t\":\"ping\"}", "final ping");
            Assert.True(r.Kept.Any(x => x.P == p && (string)x.F["t"] == LodgeMsg.Pong), p.Name + " got no pong after the run");
            r.Kept.Clear();
        }
        await r.Send(r.People["Elan"], "{\"t\":\"msg\",\"channel\":\"general\",\"text\":\"still here\"}", "final msg");
        Assert.True(r.Kept.Any(x => x.P.Name == "Obs" && (string)x.F["t"] == LodgeMsg.Msg && (string)x.F["text"] == "still here"), "the final chat line did not reach a watcher");

        var growth = (memEnd - memStart) / (1024 * 1024);
        output.WriteLine(r.Frames + " frames (" + skipped + " skipped: sender could not sign in again) in " + sw.Elapsed.TotalSeconds.ToString("0.0") + " s; memory " + memStart / (1024 * 1024) + " -> " + memEnd / (1024 * 1024) + " MB; "
            + "codes given again: " + r.CodesRenewed + "; watchers kicked and back: " + string.Join(", ", r.People.Values.Where(o => o.Observer).Select(o => o.Name + " " + o.Kicked)) + "; frames the parser refused: " + r.Refused);
        output.WriteLine("answers seen: " + string.Join(", ", r.Received.OrderByDescending(kv => kv.Value).Select(kv => kv.Key + " " + kv.Value)));
        Assert.True(r.Problems.Count == 0, r.Problems.Count + " problems:\n" + string.Join("\n", r.Problems.Take(30)));
        Assert.True(growth < (FuzzGen.Deep ? 256 : 96), "memory grew by " + growth + " MB over the run");
        Assert.True(r.Received.Count > 25, "only " + r.Received.Count + " kinds of answers - did the frames reach the handlers?");
    }

    // ================================================================ budgets, the frame cap, odd frames

    [Fact]
    public async Task A_control_flood_is_rate_limited_and_then_cut_off_without_touching_anyone_else()
    {
        using var r = await Lodge();
        var mia = r.People["Mia"];
        r.Keep = true;
        int sent = 0;
        for (int i = 0; i < 1000 && r.Alive(mia); i++) { await r.Send(mia, "{\"t\":\"status\",\"status\":\"away\",\"note\":\"n" + i + "\"}", "flood"); sent++; }
        var relayed = r.Kept.Count(x => x.P.Name == "Obs" && (string)x.F["t"] == LodgeMsg.User);
        Assert.True(relayed <= 30, relayed + " status changes reached the others (the burst is 30)");
        Assert.False(r.Alive(mia), "the flood was never cut off");
        Assert.Equal("Too many messages - slow down", mia.Sock.CloseReason);
        Assert.True(sent < 300, "cut off only after " + sent + " frames");
        // everyone else is fine
        r.Kept.Clear();
        await r.Send(r.People["Obs"], "{\"t\":\"ping\"}", "ping after the flood");
        Assert.Contains(r.Kept, x => x.P.Name == "Obs" && (string)x.F["t"] == LodgeMsg.Pong);
        Assert.Empty(r.Problems);
    }

    [Fact]
    public async Task A_chat_flood_gets_rate_errors_not_relays()
    {
        using var r = await Lodge();
        var bob = r.People["Bob"];
        r.Keep = true;
        for (int i = 0; i < 40; i++) await r.Send(bob, "{\"t\":\"msg\",\"channel\":\"general\",\"text\":\"spam " + i + "\"}", "chat flood");
        var relayed = r.Kept.Count(x => x.P.Name == "Obs" && (string)x.F["t"] == LodgeMsg.Msg);
        var rate = r.Kept.Count(x => x.P.Name == "Bob" && (string)x.F["t"] == LodgeMsg.Error && (string)x.F["code"] == "rate");
        Assert.True(relayed <= 8, relayed + " lines relayed (the burst is 8)");
        Assert.True(rate >= 30, "only " + rate + " rate errors for 40 lines");
        Assert.True(r.Alive(bob));
        Assert.Empty(r.Problems);
    }

    static string PaddedPing(int bytes)
    {
        const string head = "{\"t\":\"ping\",\"pad\":\"";
        return head + new string('x', bytes - head.Length - 2) + "\"}";
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1000)]
    [InlineData(7)]
    public async Task The_frame_cap_takes_64_KB_and_closes_on_more(int chunk)
    {
        using var r = await Lodge();
        var mia = r.People["Mia"];
        r.Keep = true;
        foreach (var size in new[] { MaxFrame - 1, MaxFrame })
        {
            r.Kept.Clear();
            mia.Sock.Chunk = chunk;
            r.Clock.Advance(TimeSpan.FromSeconds(1));
            await r.Send(mia, PaddedPing(size), size + " bytes");
            Assert.Equal(size, Encoding.UTF8.GetByteCount(PaddedPing(size)));
            Assert.Contains(r.Kept, x => x.P == mia && (string)x.F["t"] == LodgeMsg.Pong);
        }
        await r.Send(mia, PaddedPing(MaxFrame + 1), "one byte over");
        Assert.False(r.Alive(mia), "a frame over the cap was taken");
        // the others are untouched
        r.Kept.Clear();
        await r.Send(r.People["Obs"], "{\"t\":\"ping\"}", "ping");
        Assert.Contains(r.Kept, x => x.P.Name == "Obs" && (string)x.F["t"] == LodgeMsg.Pong);
        Assert.Empty(r.Problems);
    }

    [Fact]
    public async Task Binary_invalid_UTF8_and_too_deep_frames_cost_only_themselves()
    {
        using var r = await Lodge();
        var mia = r.People["Mia"];
        r.Keep = true;
        async Task<bool> Pong(string what)
        {
            r.Kept.Clear();
            r.Clock.Advance(TimeSpan.FromSeconds(1));
            await r.Send(mia, "{\"t\":\"ping\"}", what);
            return r.Kept.Any(x => x.P == mia && (string)x.F["t"] == LodgeMsg.Pong);
        }
        // a ping as a binary frame is voice, not a ping
        r.Kept.Clear();
        await r.Send(mia, Utf8("{\"t\":\"ping\"}"), WebSocketMessageType.Binary, "binary ping");
        Assert.DoesNotContain(r.Kept, x => (string)x.F["t"] == LodgeMsg.Pong);
        Assert.True(await Pong("after binary"));
        // invalid UTF-8 refuses the frame (LodgeJson.Rules: no U+FFFD in its place) - and costs only that frame
        foreach (var bad in new[] { new byte[] { 0xff }, new byte[] { 0xc0, 0x80 }, new byte[] { 0xed, 0xa0, 0x80 }, new byte[] { 0xe2, 0x82 } })
        {
            r.Kept.Clear();
            await r.Send(mia, Utf8("{\"t\":\"ping\",\"x\":\"").Concat(bad).Concat(Utf8("\"}")).ToArray(), WebSocketMessageType.Text, "invalid UTF-8");
            Assert.DoesNotContain(r.Kept, x => (string)x.F["t"] == LodgeMsg.Pong);
            Assert.True(await Pong("after invalid UTF-8"));
        }
        await r.Send(mia, new byte[] { 0xff, 0xfe, 0xfd, 0x00, 0x80 }, WebSocketMessageType.Text, "garbage");
        Assert.True(await Pong("after garbage"));
        // nesting: 64 levels parse, deeper is no frame
        r.Kept.Clear();
        await r.Send(mia, "{\"t\":\"ping\",\"x\":" + string.Concat(Enumerable.Repeat("[", 62)) + string.Concat(Enumerable.Repeat("]", 62)) + "}", "depth 63");
        Assert.Contains(r.Kept, x => x.P == mia && (string)x.F["t"] == LodgeMsg.Pong);
        r.Kept.Clear();
        await r.Send(mia, "{\"t\":\"ping\",\"x\":" + string.Concat(Enumerable.Repeat("[", 5000)) + string.Concat(Enumerable.Repeat("]", 5000)) + "}", "depth 5001");
        Assert.DoesNotContain(r.Kept, x => (string)x.F["t"] == LodgeMsg.Pong);
        Assert.True(await Pong("after deep"));
        Assert.Empty(r.Problems);
    }

    // found by the fuzzer: the frame type was read outside the hub's safety net (ReceiveLoop: J.Str(m, "t")), so a frame whose JSON the
    // shared adapter can't read lazily - a key twice, a lone surrogate escape - threw out of the receive loop: LodgeHub.Run faulted and the
    // exception reached ASP.NET instead of costing only that frame. Since step 7 LodgeJson.Parse refuses such a frame outright.
    [Theory]
    [InlineData("{\"t\":\"ping\",\"t\":\"ping\"}")]
    [InlineData("{\"t\":\"msg\",\"channel\":\"general\",\"text\":\"a\",\"t\":\"msg\"}")]
    [InlineData("{\"t\":\"LONE\"}")]
    [InlineData("{\"LONE\":1,\"t\":\"ping\"}")]
    public async Task Regression_a_frame_the_adapter_cannot_read_costs_only_that_frame(string frame)
    {
        frame = frame.Replace("LONE", (char)92 + "ud800");
        using var r = await Lodge();
        var mia = r.People["Mia"];
        r.Keep = true;
        await r.Send(mia, frame, "unreadable frame");
        Assert.True(r.Alive(mia), "the connection ended");
        r.Kept.Clear();
        r.Clock.Advance(TimeSpan.FromSeconds(1));
        await r.Send(mia, "{\"t\":\"ping\"}", "ping after");
        Assert.Contains(r.Kept, x => x.P == mia && (string)x.F["t"] == LodgeMsg.Pong);
        Assert.Empty(r.Problems);
    }
}
