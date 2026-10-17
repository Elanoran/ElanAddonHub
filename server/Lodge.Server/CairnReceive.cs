using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Lodge;

// Give to get (2.27, feature "cairn2"; PROTOCOL.md "The Cairn"). The Cairn's dataset is for contributing Lodges and their members who share:
//  - CairnReceiver: a Lodge that contributes (CairnContributor.Enabled) also downloads the dataset with its key - the manifest with its ETag at
//    most every LODGE_CAIRN_FETCH_HOURS (6), the file only when it changed - checks both SHA-256s and keeps one copy in <data>/cairn/.
//    The Cairn answers only while this Lodge is in good standing (an accepted contribution within its grace period). A "contribute to receive"
//    (new / lapsed) or a refused key drops the copy at once and stops serving; the owner sees why in the log and in cairn.status.
//  - CairnSharing: which members said their Atlas sharing is on (atlas.sharing { on }), by identity, in <data>/cairn-sharing.json. The Lodge
//    can't see a member's switch otherwise: sharing off simply means the Outpost sends nothing.
//  - CairnModule: atlas.sharing / cairn.status over the WebSocket, and the checks behind GET /cairn/manifest and GET /cairn/dataset (Program.cs):
//    members (never guests) whose sharing is on, while the Lodge is serving.
public sealed partial class CairnReceiver
{
    public const long MaxGzBytes = 64L * 1024 * 1024, MaxJsonBytes = 256L * 1024 * 1024, MaxManifestBytes = 1024 * 1024;

    [GeneratedRegex(@"^cairn-[0-9]{4}-[0-9]{2}-[0-9]{2}-[0-9]{1,9}\.json\.gz\z", RegexOptions.CultureInvariant)]
    private static partial Regex FileRx();

    readonly LodgeConfig cfg;
    readonly CairnContributor contributor;
    readonly ILogger log;
    readonly IClock clock;
    HttpMessageHandler handler;
    readonly string dir;
    readonly object gate = new();
    HttpClient http;
    int running;
    DateTime nextRun;

    public CairnReceiver(LodgeConfig cfg, CairnContributor contributor, ILogger log, IClock clock, HttpMessageHandler handler = null)
    {
        this.cfg = cfg; this.contributor = contributor; this.log = log; this.clock = clock; this.handler = handler;
        dir = Path.Combine(cfg.DataDir, "cairn");
        nextRun = clock.UtcNow + TimeSpan.FromMinutes(4);   // after the first contribution run (2 minutes after start)
        LoadHeld();
    }

    // give to get: only a Lodge that contributes receives
    public bool Enabled => contributor.Enabled;

    public sealed class Held
    {
        public string Day = "", File = "", Sha256 = "", ContentSha256 = "", Sig = "", Kid = "", ETag = "";
        public long Seq, Bytes, ContentBytes, Built;
    }

    // what the Cairn last said about this Lodge: "" (not asked yet), good, new, lapsed, revoked, key (unknown key)
    public string Standing { get; private set; } = "";
    public long? ReceiveUntil { get; private set; }
    public string Problem { get; private set; } = "";             // the last failure in plain words (network, checksum); the copy stays
    public DateTime LastCheck { get; private set; } = DateTime.MinValue;
    public Held Current { get { lock (gate) return current; } }
    Held current;
    public event Action Changed;                                  // a new dataset, or serving started / stopped

    public string GzPath => Path.Combine(dir, "dataset.json.gz");
    string HeldPath => Path.Combine(dir, "held.json");

    // serving = on, in good standing (the last answer was not a refusal) and a checked copy on disk
    public bool Serving { get { lock (gate) return Enabled && current != null && (Standing is "" or "good") && File.Exists(GzPath); } }

    void LoadHeld()
    {
        try
        {
            if (!File.Exists(HeldPath) || !File.Exists(GzPath)) return;
            if (JsonNode.Parse(File.ReadAllText(HeldPath)) is not JsonObject o) return;
            var h = new Held
            {
                Day = (string)o["day"] ?? "", File = (string)o["file"] ?? "", Sha256 = (string)o["sha256"] ?? "", ContentSha256 = (string)o["contentSha256"] ?? "",
                Sig = (string)o["sig"] ?? "", Kid = (string)o["kid"] ?? "", ETag = (string)o["etag"] ?? "",
                Seq = (long?)o["seq"] ?? 0, Bytes = (long?)o["bytes"] ?? 0, ContentBytes = (long?)o["contentBytes"] ?? 0, Built = (long?)o["built"] ?? 0,
            };
            if (!Sha(h.Sha256) || !Sha(h.ContentSha256) || new FileInfo(GzPath).Length != h.Bytes) { Drop(); return; }
            current = h;
        }
        catch (Exception e) { log?.LogWarning("Cairn: the stored dataset is unreadable ({Error}) - fetched again", e.GetType().Name); Drop(); }
    }

    static bool Sha(string s) => s != null && s.Length == 64 && s.All(Uri.IsHexDigit);

    void SaveHeld(Held h)
    {
        var o = new JsonObject
        {
            ["day"] = h.Day, ["seq"] = h.Seq, ["file"] = h.File, ["bytes"] = h.Bytes, ["contentBytes"] = h.ContentBytes, ["sha256"] = h.Sha256,
            ["contentSha256"] = h.ContentSha256, ["sig"] = h.Sig, ["kid"] = h.Kid, ["built"] = h.Built, ["etag"] = h.ETag,
        };
        var tmp = HeldPath + ".tmp";
        File.WriteAllText(tmp, o.ToJsonString());
        File.Move(tmp, HeldPath, true);
    }

    // the copy goes (lapsed, refused key, switched off): the data is only for a Lodge in good standing
    void Drop()
    {
        lock (gate)
        {
            current = null;
            try { if (File.Exists(GzPath)) File.Delete(GzPath); } catch { }
            try { if (File.Exists(HeldPath)) File.Delete(HeldPath); } catch { }
        }
    }

    // the manifest as members get it: the Cairn's entry for the copy this Lodge holds (no history, no Cairn URL)
    public JsonObject ManifestJson(Held h) => new()
    {
        ["format"] = "elans-cairn-manifest", ["v"] = 1, ["license"] = "contributors", ["kid"] = h.Kid,
        ["latest"] = new JsonObject
        {
            ["day"] = h.Day, ["seq"] = h.Seq, ["file"] = h.File, ["bytes"] = h.Bytes, ["contentBytes"] = h.ContentBytes, ["sha256"] = h.Sha256,
            ["contentSha256"] = h.ContentSha256, ["sig"] = h.Sig, ["kid"] = h.Kid, ["built"] = h.Built,
        },
    };

    public static string ETagOf(Held h) => "\"" + h.ContentSha256[..32] + "\"";

    // the stored file, opened under the lock so a replacement a moment later can't pull it away (the handle stays valid)
    public FileStream OpenDataset(out Held h)
    {
        lock (gate)
        {
            h = current;
            if (h == null || !File.Exists(GzPath)) return null;
            return new FileStream(GzPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true);
        }
    }

    // ---------------------------------------------------------------- fetching

    public void Tick()
    {
        if (!Enabled)
        {
            if (Current != null) { Drop(); Changed?.Invoke(); }
            return;
        }
        if (clock.UtcNow < nextRun || Interlocked.CompareExchange(ref running, 1, 0) != 0) return;
        nextRun = clock.UtcNow + TimeSpan.FromHours(cfg.CairnFetchHours);
        _ = Task.Run(async () =>
        {
            try { await RunOnce(CancellationToken.None); }
            catch (Exception e) { log?.LogWarning("Cairn: fetching the dataset failed: {Error}", e.GetType().Name); }
            finally { Interlocked.Exchange(ref running, 0); }
        });
    }

    // after a contribution: the Cairn's answer carries this Lodge's standing. Good and nothing on hand -> fetch soon; refused -> stop now.
    public void AfterContribution(string standing, long? receiveUntil)
    {
        if (string.IsNullOrEmpty(standing)) return;
        ReceiveUntil = receiveUntil;
        if (standing == "good")
        {
            bool was = Standing is "new" or "lapsed";
            if (Standing != "good") Standing = "good";
            if (Current == null || was) nextRun = clock.UtcNow;    // first access (or access regained): fetch at the next housekeeping tick
        }
        else Refused(standing);
    }

    // tests: a fake Cairn (no network)
    public void UseHandlerForTests(HttpMessageHandler h) { handler = h; http = null; }

    HttpClient Http() => http ??= handler != null ? new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(120) } : new HttpClient { Timeout = TimeSpan.FromSeconds(120) };

    HttpRequestMessage Req(string path, string etag = null)
    {
        CairnRules.ValidUrl(cfg.CairnUrl, out var baseUri);
        var r = new HttpRequestMessage(HttpMethod.Get, baseUri.ToString().TrimEnd('/') + path);
        r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.CairnKey);
        r.Headers.UserAgent.ParseAdd("ElansLodge/" + LodgeHub.Version);
        if (!string.IsNullOrEmpty(etag)) r.Headers.TryAddWithoutValidation("If-None-Match", etag);
        return r;
    }

    static async Task<byte[]> ReadCapped(Stream s, long cap, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        int n;
        while ((n = await s.ReadAsync(buf, ct)) > 0)
        {
            if (ms.Length + n > cap) throw new InvalidDataException("too large");
            ms.Write(buf, 0, n);
        }
        return ms.ToArray();
    }

    // the SHA-256 of the unpacked JSON, without keeping it (capped: a gzip bomb stops at MaxJsonBytes)
    static string ContentSha(byte[] gz)
    {
        using var z = new GZipStream(new MemoryStream(gz), CompressionMode.Decompress);
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buf = new byte[81920];
        long total = 0; int n;
        while ((n = z.Read(buf, 0, buf.Length)) > 0)
        {
            total += n;
            if (total > MaxJsonBytes) throw new InvalidDataException("too large");
            h.AppendData(buf, 0, n);
        }
        return Convert.ToHexString(h.GetHashAndReset()).ToLowerInvariant();
    }

    static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    // "unchanged", "updated", "none" (the Cairn has no dataset yet), "refused: <standing>", "failed: <why>"
    public async Task<string> RunOnce(CancellationToken ct)
    {
        if (!Enabled) return "disabled";
        var have = Current;
        using var mr = await Http().SendAsync(Req("/v1/manifest.json", have?.ETag), ct);
        LastCheck = clock.UtcNow;
        if (mr.StatusCode == HttpStatusCode.NotModified && have != null) { Good(); return "unchanged"; }
        if (await Refusal(mr, ct) is { } refusal) return refusal;
        if (mr.StatusCode == HttpStatusCode.NotFound) { Good(); return Fail("the Cairn has no dataset yet", "none"); }
        if (!mr.IsSuccessStatusCode) return Fail("the Cairn answered " + (int)mr.StatusCode);
        if (mr.Content.Headers.ContentLength > MaxManifestBytes) return Fail("the manifest is too large");
        JsonObject latest;
        try { latest = (JsonNode.Parse(await ReadCapped(await mr.Content.ReadAsStreamAsync(ct), MaxManifestBytes, ct)) as JsonObject)?["latest"] as JsonObject; }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidDataException) { return Fail("the manifest is not readable"); }
        var e2 = new Held
        {
            Day = (string)latest?["day"] ?? "", File = (string)latest?["file"] ?? "", Sha256 = ((string)latest?["sha256"] ?? "").ToLowerInvariant(),
            ContentSha256 = ((string)latest?["contentSha256"] ?? "").ToLowerInvariant(), Sig = (string)latest?["sig"] ?? "", Kid = (string)latest?["kid"] ?? "",
            ETag = mr.Headers.ETag?.ToString() ?? "",
        };
        try { e2.Seq = (long?)latest?["seq"] ?? 0; e2.Bytes = (long?)latest?["bytes"] ?? 0; e2.ContentBytes = (long?)latest?["contentBytes"] ?? 0; e2.Built = (long?)latest?["built"] ?? 0; }
        catch (Exception x) when (x is FormatException or InvalidOperationException) { return Fail("the manifest names no valid file"); }
        if (!FileRx().IsMatch(e2.File) || !Sha(e2.Sha256) || !Sha(e2.ContentSha256) || e2.Seq < 1 || e2.Day.Length != 10) return Fail("the manifest names no valid file");
        if (have != null && have.ContentSha256 == e2.ContentSha256)
        {
            lock (gate) { have.ETag = e2.ETag; try { SaveHeld(have); } catch { } }
            Good();
            return "unchanged";
        }

        using var dr = await Http().SendAsync(Req("/v1/datasets/" + e2.File), HttpCompletionOption.ResponseHeadersRead, ct);
        if (await Refusal(dr, ct) is { } refused) return refused;
        if (!dr.IsSuccessStatusCode) return Fail("the dataset answered " + (int)dr.StatusCode);
        if (dr.Content.Headers.ContentLength > MaxGzBytes) return Fail("the dataset is too large");
        byte[] gz;
        try { gz = await ReadCapped(await dr.Content.ReadAsStreamAsync(ct), MaxGzBytes, ct); }
        catch (InvalidDataException) { return Fail("the dataset is too large"); }
        if (Hex(SHA256.HashData(gz)) != e2.Sha256) return Fail("the dataset does not match its checksum");
        string csha;
        try { csha = ContentSha(gz); }
        catch (InvalidDataException) { return Fail("the dataset is damaged"); }
        if (csha != e2.ContentSha256) return Fail("the dataset does not match its checksum");
        e2.Bytes = gz.Length;
        lock (gate)
        {
            Directory.CreateDirectory(dir);
            var tmp = GzPath + ".tmp";
            File.WriteAllBytes(tmp, gz);
            File.Move(tmp, GzPath, true);
            SaveHeld(e2);
            current = e2;
        }
        Good();
        Problem = "";
        log?.LogInformation("Cairn: dataset {Day} #{Seq} received ({Bytes} bytes) - members who share get it from this Lodge", e2.Day, e2.Seq, gz.Length);
        Changed?.Invoke();
        return "updated";
    }

    void Good()
    {
        if (Standing != "good") { Standing = "good"; }
    }

    // 401 / 403 from the Cairn: this Lodge is not entitled (any more). The copy goes and serving stops; said once per change.
    async Task<string> Refusal(HttpResponseMessage r, CancellationToken ct)
    {
        if (r.StatusCode != HttpStatusCode.Unauthorized && r.StatusCode != HttpStatusCode.Forbidden) return null;
        string reason = "", standing = "";
        try
        {
            var o = JsonNode.Parse(await ReadCapped(await r.Content.ReadAsStreamAsync(ct), 64 * 1024, ct)) as JsonObject;
            reason = (string)o?["reason"] ?? ""; standing = (string)o?["standing"] ?? "";
        }
        catch { }
        var st = r.StatusCode == HttpStatusCode.Unauthorized ? (reason == "revoked" ? "revoked" : "key") : standing is "new" or "lapsed" ? standing : "lapsed";
        Refused(st);
        return "refused: " + st;
    }

    void Refused(string standing)
    {
        bool changed = Standing != standing;
        bool had = Current != null;
        Standing = standing;
        ReceiveUntil = null;
        Drop();
        if (changed)
            log?.LogWarning("Cairn: not receiving the Cairn's data - {Why}. Members who share are told; the stored copy was removed.", Why(standing));
        if (changed || had) Changed?.Invoke();
    }

    // for the owner (log and cairn.status): why this Lodge receives nothing, in plain words
    public string Why(string standing) => standing switch
    {
        "new" => "this Lodge has made no accepted contribution yet (contribute to receive: it starts right after the first one)",
        "lapsed" => "this Lodge's last accepted contribution is too old (contribute to receive: it starts again with the next one)",
        "revoked" => "the Cairn revoked this Lodge's key",
        "key" => "the Cairn does not know this Lodge's key",
        _ => "",
    };

    string Fail(string why, string word = null)
    {
        Problem = why;
        log?.LogWarning("Cairn: {Why} (the copy on hand stays)", why);
        return word ?? "failed: " + why;
    }
}

// Which members said their Atlas sharing is on (by identity). Only "on" entitles; unknown = off. Saved on every change.
public sealed class CairnSharing
{
    readonly string path;
    readonly object gate = new();
    readonly Dictionary<string, (bool on, long at)> map = new(StringComparer.Ordinal);
    public const int MaxEntries = 20_000;

    public CairnSharing(string dataDir, ILogger log)
    {
        path = Path.Combine(dataDir, "cairn-sharing.json");
        try
        {
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject o)
                foreach (var (k, v) in o) if (v is JsonObject e) map[k] = ((bool?)e["on"] ?? false, (long?)e["at"] ?? 0);
        }
        catch (Exception e) { log?.LogWarning("Cairn: the sharing list is unreadable ({Error}) - members tell it again on their next sign-in", e.GetType().Name); }
    }

    public bool IsOn(string identity) { lock (gate) return identity != null && map.TryGetValue(identity, out var e) && e.on; }

    // true = it changed
    public bool Set(string identity, bool on, long at)
    {
        lock (gate)
        {
            if (map.TryGetValue(identity, out var e) && e.on == on) return false;
            if (!map.ContainsKey(identity) && map.Count >= MaxEntries) return false;
            map[identity] = (on, at);
            var o = new JsonObject();
            foreach (var (k, v) in map.OrderBy(x => x.Key, StringComparer.Ordinal)) o[k] = new JsonObject { ["on"] = v.on, ["at"] = v.at };
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, o.ToJsonString());
            File.Move(tmp, path, true);
            return true;
        }
    }

    // a purged member (2.22) is forgotten here too
    public void Forget(string identity)
    {
        lock (gate)
        {
            if (!map.Remove(identity)) return;
            var o = new JsonObject();
            foreach (var (k, v) in map.OrderBy(x => x.Key, StringComparer.Ordinal)) o[k] = new JsonObject { ["on"] = v.on, ["at"] = v.at };
            File.WriteAllText(path, o.ToJsonString());
        }
    }
}

// atlas.sharing { on } -> cairn.status; cairn.status -> cairn.status. The HTTP routes ask Check() (Program.cs).
public sealed class CairnModule : IModule
{
    public const int DownloadsPerHour = 4;                // per person: the file is the same for hours
    readonly LodgeHub hub;
    public readonly CairnReceiver Receiver;
    public readonly CairnSharing Sharing;
    readonly ConcurrentDictionary<string, Queue<DateTime>> recent = new();

    public CairnModule(LodgeHub hub, CairnReceiver receiver)
    {
        this.hub = hub; Receiver = receiver;
        Sharing = new CairnSharing(hub.Cfg.DataDir, hub.Log);
        // a new dataset, or serving stopped: the members who share hear it at once (their Outpost fetches, or drops its copy)
        Receiver.Changed += () => _ = TellSharers();
    }

    public bool Enabled => Receiver.Enabled;

    public Task TellSharers() =>
        Task.WhenAll(hub.Members.Where(m => m.Personal && Sharing.IsOn(Identity(m.Name)) || m.Role == "owner").ToList().Select(m => hub.Send(m, StatusFor(m.Name, m.Personal, m.Role))));

    static string Identity(string login) => ProfileRules.Key(login);

    // null = may receive; otherwise (status, reason, text). Order: off, guest, sharing, the Lodge's own standing, nothing on hand yet.
    public (int status, string reason, string error)? Check(string login, bool personal, string role)
    {
        if (!Enabled) return (404, "disabled", "This Lodge doesn't hand out the Cairn's data");
        if (!personal || !Roles.Can(role, Perm.AtlasShare)) return (403, "guest", "The Cairn's data is for Lodge members who share - guests don't get it");
        if (!Sharing.IsOn(Identity(login))) return (403, "share", "Share to receive: switch on sharing your Atlas observations with this Lodge, and the Lodge gives you the Cairn's numbers too");
        if (Receiver.Standing is "new" or "lapsed" or "revoked" or "key") return (403, "lodge", "This Lodge isn't receiving the Cairn's data right now (it has to contribute to receive)");
        if (!Receiver.Serving) return (503, "not-yet", "This Lodge has no copy of the Cairn's data yet - try again later");
        return null;
    }

    public void CheckDownloadRate(string login)
    {
        var now = hub.Clock.UtcNow;
        var q = recent.GetOrAdd(Identity(login), _ => new Queue<DateTime>());
        lock (q)
        {
            while (q.Count > 0 && now - q.Peek() > TimeSpan.FromHours(1)) q.Dequeue();
            if (q.Count >= DownloadsPerHour) throw new DatasetException(429, "rate", "You already fetched this a few times - try again in a while");
            q.Enqueue(now);
        }
    }

    public JsonObject StatusFor(string login, bool personal, string role)
    {
        var deny = Check(login, personal, role);
        var o = new JsonObject { ["t"] = "cairn.status", ["entitled"] = deny == null, ["sharing"] = personal && Sharing.IsOn(Identity(login)) };
        if (deny is { } d) { o["why"] = d.reason; o["text"] = d.error; }
        var h = Receiver.Current;
        if (deny == null && h != null) { o["day"] = h.Day; o["seq"] = h.Seq; }
        // the owner also sees the Lodge's own standing at the Cairn (why it receives nothing)
        if (role == "owner" && Enabled)
        {
            var l = new JsonObject { ["standing"] = Receiver.Standing.Length > 0 ? Receiver.Standing : "unknown", ["serving"] = Receiver.Serving };
            if (Receiver.ReceiveUntil is long u) l["receiveUntil"] = u;
            if (Receiver.LastCheck > DateTime.MinValue) l["checked"] = new DateTimeOffset(Receiver.LastCheck).ToUnixTimeSeconds();
            var why = Receiver.Why(Receiver.Standing);
            if (why.Length > 0) l["why"] = why;
            if (Receiver.Problem.Length > 0) l["problem"] = Receiver.Problem;
            o["lodge"] = l;
        }
        return o;
    }

    public async Task<bool> Handle(Member me, string type, JsonObject m)
    {
        switch (type)
        {
            case "atlas.sharing":
            {
                if (!Enabled) return false;
                if (!me.Budget.Page.TryTake()) { me.CountDrop(hub.Clock); await hub.Error(me, "Slow down a little", "rate"); return true; }
                if (me.Personal && Roles.Can(me.Role, Perm.AtlasShare))
                {
                    bool on = m["on"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
                    if (Sharing.Set(Identity(me.Name), on, new DateTimeOffset(hub.Clock.UtcNow).ToUnixTimeSeconds()))
                        hub.Log.LogInformation("{Name} switched Atlas sharing {State} (Cairn data {Access})", me.Name, on ? "on" : "off", on ? "on" : "off");
                }
                await hub.Send(me, StatusFor(me.Name, me.Personal, me.Role));
                return true;
            }
            case "cairn.status":
            {
                if (!Enabled) return false;
                if (!me.Budget.Page.TryTake()) { me.CountDrop(hub.Clock); await hub.Error(me, "Slow down a little", "rate"); return true; }
                await hub.Send(me, StatusFor(me.Name, me.Personal, me.Role));
                return true;
            }
        }
        return false;
    }

    public void Tick()
    {
        Receiver.Tick();
        var now = hub.Clock.UtcNow;
        foreach (var kv in recent) lock (kv.Value) if (kv.Value.Count == 0 || now - kv.Value.Peek() > TimeSpan.FromHours(1)) recent.TryRemove(kv.Key, out _);
    }
}
