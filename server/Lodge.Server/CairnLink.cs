using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Lodge;

// 2.28: how this Lodge talks to the Cairn (PROTOCOL.md "The Cairn: joining and signed requests").
//  - CairnKeys: the Lodge's own ECDSA P-256 key pair, made on first need, in <data>/cairn-key.pem (mode 600, never logged, in lodge-backup
//    with the rest of the data folder). Only the public half ever leaves the machine. No shared secret: the Cairn keeps public keys only.
//  - CairnJoinState: the Guild Master's switch (`admin.cairn {on, publicUrl, code?}`) and what the Cairn last said, in <data>/cairn-join.json.
//  - CairnLink: which way is in use and how a request is authorized:
//      env path   LODGE_CAIRN_ENABLED + LODGE_CAIRN_URL + LODGE_CAIRN_KEY (as before 2.28). Once the Cairn offers "keys" the Lodge registers its key
//                 pair once (POST /v1/keys, bearer + signed) and from then on signs; the bearer key is retired at the Cairn.
//      join path  no env at all: the Guild Master switches "Join the Cairn" on in the Outpost. The Lodge asks to join (POST /v1/join), answers the
//                 Cairn's call back (GET /cairn/challenge on this server), waits / is admitted, and then contributes and receives, signed.
//    The Cairn's URL: LODGE_CAIRN_URL when set (a self-hosted Cairn), otherwise the default public Cairn.
//  - CairnJoiner: the join / status polling for the join path (CairnContributor does the env path's one-time key registration).
public static class CairnSign
{
    public const string HKey = "X-Cairn-Key", HTime = "X-Cairn-Time", HNonce = "X-Cairn-Nonce", HSig = "X-Cairn-Signature";

    public static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static string Sha256Hex(byte[] b) => Convert.ToHexString(SHA256.HashData(b ?? Array.Empty<byte>())).ToLowerInvariant();

    public static string RequestMessage(string method, string pathAndQuery, string time, string nonce, string bodySha) =>
        "elans-cairn-req/1\n" + method.ToUpperInvariant() + "\n" + pathAndQuery + "\n" + time + "\n" + nonce + "\n" + bodySha;

    public static string ChallengeMessage(string nonce, string publicUrl) => "elans-cairn-challenge/1\n" + nonce + "\n" + publicUrl;

    public static bool ValidNonce(string n) => n != null && n.Length is >= 16 and <= 64 && n.All(ch => ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_');

    // the same canonical form the Cairn checks: https, lowercase host, :port only when not 443, no trailing '/', no user / query / fragment.
    // Refuses addresses the Cairn could never call (localhost, private IPs, a bare host name) so the Guild Master hears it at once.
    public static bool TryNormalizeUrl(string s, out string norm, out string why)
    {
        norm = null; why = null;
        s = (s ?? "").Trim();
        if (s.Length == 0) { why = "Enter the Lodge's public address (the one members connect to)"; return false; }
        if (s.Length > 200) { why = "The address is too long"; return false; }
        if (!Uri.TryCreate(s, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) { why = "The address must start with https://"; return false; }
        if (!string.IsNullOrEmpty(u.UserInfo) || !string.IsNullOrEmpty(u.Query) || !string.IsNullOrEmpty(u.Fragment) || s.Contains('?') || s.Contains('#'))
        { why = "The address can't carry a user name, ? or #"; return false; }
        if (u.Port != 443 && u.Port < 1024) { why = "The address must use port 443 or a port from 1024"; return false; }
        var host = u.IdnHost.ToLowerInvariant();
        if (u.HostNameType == UriHostNameType.Dns && (host == "localhost" || host.EndsWith(".localhost") || host.EndsWith(".local") || host.EndsWith(".internal") || !host.Contains('.')))
        { why = "The address must be reachable from the internet (not localhost or a local name)"; return false; }
        if (u.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6 && IPAddress.TryParse(u.Host.Trim('[', ']'), out var ip) && !IsPublicIp(ip))
        { why = "The address must be reachable from the internet (not a private or local IP)"; return false; }
        var path = u.AbsolutePath.TrimEnd('/');
        norm = "https://" + (u.HostNameType == UriHostNameType.IPv6 ? "[" + host.Trim('[', ']') + "]" : host) + (u.Port == 443 ? "" : ":" + u.Port.ToString(CultureInfo.InvariantCulture)) + path;
        return true;
    }

    static bool IsPublicIp(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (IPAddress.IsLoopback(a) || a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || a.IsIPv6Multicast) return false;
        var b = a.GetAddressBytes();
        if (b.Length == 4) return !(b[0] is 0 or 10 or 127 || b[0] >= 224 || (b[0] == 169 && b[1] == 254) || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 100 && b[1] is >= 64 and <= 127));
        return !((b[0] & 0xfe) == 0xfc);
    }
}

public sealed class CairnKeys
{
    readonly string path;
    readonly object gate = new();
    ECDsa key;
    public string PublicKey { get; private set; } = "";
    public string KeyId { get; private set; } = "";

    public CairnKeys(string dataDir) { path = Path.Combine(dataDir, "cairn-key.pem"); }

    public string FilePath => path;
    public bool Exists { get { lock (gate) return key != null || File.Exists(path); } }

    // the key pair, made on first need (private key: PKCS#8 PEM, 0600 from the first byte; written once, never replaced here)
    public ECDsa Get()
    {
        lock (gate)
        {
            if (key != null) return key;
            var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            if (File.Exists(path))
            {
                k.ImportFromPem(File.ReadAllText(path));
                // a restore (lodge-backup restore sets 640 on every file) must not leave the private key group-readable
                if (!OperatingSystem.IsWindows()) try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            else
            {
                var tmp = path + ".tmp";
                var opts = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
                if (!OperatingSystem.IsWindows()) opts.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using (var fs = new FileStream(tmp, opts))
                {
                    var b = Encoding.ASCII.GetBytes(k.ExportPkcs8PrivateKeyPem());
                    fs.Write(b); fs.Flush(true);
                }
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.Move(tmp, path, false);
            }
            var spki = k.ExportSubjectPublicKeyInfo();
            PublicKey = CairnSign.B64Url(spki);
            KeyId = Convert.ToHexString(SHA256.HashData(spki)).ToLowerInvariant()[..16];
            key = k;
            return k;
        }
    }

    public string Sign(string message)
    {
        var k = Get();
        return CairnSign.B64Url(k.SignData(Encoding.UTF8.GetBytes(message), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }
}

// the Guild Master's switch and the Cairn's last answer about the join (cairn-join.json). The invite code is kept only until the Cairn
// took or refused it; it is never logged and never sent to a member.
public sealed class CairnJoinState
{
    readonly string path;
    readonly object gate = new();
    public CairnJoinState(string dataDir) { path = Path.Combine(dataDir, "cairn-join.json"); Load(); }

    public bool On; public string PublicUrl = ""; public string Code = "";
    public string State = "";          // "" (not asked), waiting, admitted, refused, revoked, error
    public string Reason = "", Error = "", Mode = "", EstDay = "";
    public int Place, Of; public int? BetaOpenSlots;
    public long? Admitted, ProbationUntil, Checked, LastPost; public bool Settled;
    public bool Migrated;              // the env path's bearer key was swapped for this Lodge's key pair (POST /v1/keys)
    public string By = "";             // who switched it last (a member name, for the log only)
    public object Gate => gate;

    void Load()
    {
        try
        {
            if (!File.Exists(path) || JsonNode.Parse(File.ReadAllText(path)) is not JsonObject o) return;
            On = (bool?)o["on"] ?? false; PublicUrl = (string)o["publicUrl"] ?? ""; Code = (string)o["code"] ?? "";
            State = (string)o["state"] ?? ""; Reason = (string)o["reason"] ?? ""; Error = (string)o["error"] ?? ""; Mode = (string)o["mode"] ?? "";
            EstDay = (string)o["estDay"] ?? ""; Place = (int?)o["place"] ?? 0; Of = (int?)o["of"] ?? 0; BetaOpenSlots = (int?)o["betaOpenSlots"];
            Admitted = (long?)o["admitted"]; ProbationUntil = (long?)o["probationUntil"]; Checked = (long?)o["checked"]; LastPost = (long?)o["lastPost"];
            Settled = (bool?)o["settled"] ?? false; Migrated = (bool?)o["migrated"] ?? false;
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or InvalidOperationException or FormatException or UnauthorizedAccessException) { }
    }

    public void Save()
    {
        lock (gate)
        {
            var o = new JsonObject
            {
                ["v"] = 1, ["on"] = On, ["publicUrl"] = PublicUrl, ["code"] = Code, ["state"] = State, ["reason"] = Reason, ["error"] = Error, ["mode"] = Mode,
                ["estDay"] = EstDay, ["place"] = Place, ["of"] = Of, ["betaOpenSlots"] = BetaOpenSlots, ["admitted"] = Admitted, ["probationUntil"] = ProbationUntil,
                ["checked"] = Checked, ["lastPost"] = LastPost, ["settled"] = Settled, ["migrated"] = Migrated,
            };
            var tmp = path + ".tmp";
            var opts = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) opts.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;   // it may hold an invite code
            using (var fs = new FileStream(tmp, opts)) { fs.Write(Encoding.UTF8.GetBytes(o.ToJsonString())); fs.Flush(true); }
            File.Move(tmp, path, true);
        }
    }

    // what the Cairn's answer (join / join status) says
    public void Take(JsonObject a, long now)
    {
        lock (gate)
        {
            State = (string)a?["state"] ?? State;
            if (State is "unknown") State = "";
            Reason = ""; Error = "";
            Place = (int?)a?["place"] ?? 0; Of = (int?)a?["of"] ?? 0; EstDay = (string)a?["estDay"] ?? "";
            Admitted = (long?)a?["admitted"] ?? Admitted; ProbationUntil = (long?)a?["probationUntil"]; Settled = (bool?)a?["settled"] ?? false;
            LastPost = (long?)a?["lastPost"] ?? LastPost;
            if ((string)a?["mode"] is { Length: > 0 } m) Mode = m;
            Checked = now;
            if (State is "admitted" or "refused" or "revoked") Code = "";      // a code is used up (or worthless) now
        }
    }
}

public sealed class CairnLink
{
    // the public Cairn this project runs; LODGE_CAIRN_URL points a Lodge at another (self-hosted) one
    public const string DefaultCairnUrl = "https://nasferatu.dk/cairn";

    readonly LodgeConfig cfg;
    public readonly CairnKeys Keys;
    public readonly CairnJoinState Join;

    public CairnLink(LodgeConfig cfg)
    {
        this.cfg = cfg;
        Keys = new CairnKeys(cfg.DataDir);
        Join = new CairnJoinState(cfg.DataDir);
    }

    public string BaseUrl => string.IsNullOrWhiteSpace(cfg.CairnUrl) ? DefaultCairnUrl : cfg.CairnUrl.Trim().TrimEnd('/');
    public string Host => CairnRules.ValidUrl(BaseUrl, out var u) ? u.Host : "";

    // the old way: the owner's env (the Guild Master's switch is then shown as "on through the server settings")
    public bool EnvPath => cfg.CairnEnabled && CairnRules.ValidUrl(cfg.CairnUrl, out _) && CairnRules.ValidKey(cfg.CairnKey);
    // the new way: the Guild Master's switch, admitted by the Cairn
    public bool JoinPath => !EnvPath && Join.On && Join.State == "admitted" && CairnRules.ValidUrl(BaseUrl, out _) && Keys.Exists;
    public bool Contributing => cfg.AtlasEnabled && (EnvPath || JoinPath);
    // signed requests: the join path always; the env path once its key pair is registered
    public bool Signs => JoinPath || (EnvPath && Join.Migrated && Keys.Exists);

    public HttpRequestMessage Request(HttpMethod m, string path, byte[] body = null, string contentType = "application/json", string contentEncoding = null, bool forceSign = false)
    {
        var r = new HttpRequestMessage(m, BaseUrl + path);
        r.Headers.UserAgent.ParseAdd("ElansLodge/" + LodgeHub.Version);
        if (body != null)
        {
            r.Content = new ByteArrayContent(body);
            r.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            if (contentEncoding != null) r.Content.Headers.ContentEncoding.Add(contentEncoding);
        }
        if (Signs || forceSign) SignInto(r, m.Method, path, body);
        else r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.CairnKey);
        return r;
    }

    public void SignInto(HttpRequestMessage r, string method, string path, byte[] body)
    {
        Keys.Get();
        var time = new DateTimeOffset(DateTime.UtcNow).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var nonce = CairnSign.B64Url(RandomNumberGenerator.GetBytes(18));
        var sig = Keys.Sign(CairnSign.RequestMessage(method, path, time, nonce, CairnSign.Sha256Hex(body)));
        r.Headers.Remove(CairnSign.HKey); r.Headers.Remove(CairnSign.HTime); r.Headers.Remove(CairnSign.HNonce); r.Headers.Remove(CairnSign.HSig);
        r.Headers.TryAddWithoutValidation(CairnSign.HKey, Keys.KeyId);
        r.Headers.TryAddWithoutValidation(CairnSign.HTime, time);
        r.Headers.TryAddWithoutValidation(CairnSign.HNonce, nonce);
        r.Headers.TryAddWithoutValidation(CairnSign.HSig, sig);
    }

    // GET /cairn/challenge?nonce=...: the Cairn checks that this Lodge holds the key it sent and lives at the address it gave. Only while the
    // Guild Master's switch is on and a key pair exists (never made by this request). The message is domain-separated
    // ("elans-cairn-challenge/1") and the nonce can't hold a line break, so an answer can never pass for a signed request.
    int challenges; DateTime challengeMinute;
    public JsonObject ChallengeAnswer(string nonce)
    {
        if (EnvPath || !Join.On || !Keys.Exists || Join.PublicUrl.Length == 0 || !CairnSign.ValidNonce(nonce)) return null;
        lock (Join.Gate)
        {
            var now = DateTime.UtcNow;
            if (now - challengeMinute > TimeSpan.FromMinutes(1)) { challengeMinute = now; challenges = 0; }
            if (++challenges > 30) return null;
        }
        Keys.Get();
        return new JsonObject
        {
            ["v"] = 1, ["keyId"] = Keys.KeyId, ["publicUrl"] = Join.PublicUrl, ["nonce"] = nonce,
            ["sig"] = Keys.Sign(CairnSign.ChallengeMessage(nonce, Join.PublicUrl)),
        };
    }
}

// The join path's runner: asks to join, follows the waiting list, keeps the status fresh. Housekeeping calls Tick every 15 s.
public sealed class CairnJoiner
{
    readonly LodgeConfig cfg;
    readonly CairnLink link;
    readonly ILogger log;
    readonly IClock clock;
    HttpMessageHandler handler;
    HttpClient http;
    int running;
    DateTime nextRun;
    public Func<string> LodgeName = () => "";
    public event Action Changed;                                  // the state the Guild Master sees changed

    public CairnJoiner(LodgeConfig cfg, CairnLink link, ILogger log, IClock clock, HttpMessageHandler handler = null)
    {
        this.cfg = cfg; this.link = link; this.log = log; this.clock = clock; this.handler = handler;
        nextRun = clock.UtcNow + TimeSpan.FromMinutes(1);
    }

    public void UseHandlerForTests(HttpMessageHandler h) { handler = h; http = null; }
    HttpClient Http() => http ??= handler != null ? new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) } : new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

    public bool Active => !link.EnvPath && link.Join.On && cfg.AtlasEnabled;

    public void Soon() => nextRun = clock.UtcNow;

    public void Tick()
    {
        if (!Active || clock.UtcNow < nextRun || Interlocked.CompareExchange(ref running, 1, 0) != 0) return;
        nextRun = clock.UtcNow + TimeSpan.FromMinutes(15);
        _ = Task.Run(async () =>
        {
            try { await RunOnce(CancellationToken.None); }
            catch (Exception e) { log?.LogWarning("Cairn: joining failed: {Error}", e.GetType().Name); }
            finally { Interlocked.Exchange(ref running, 0); }
        });
    }

    long NowS => new DateTimeOffset(clock.UtcNow).ToUnixTimeSeconds();

    static async Task<JsonObject> Body(HttpResponseMessage r, CancellationToken ct)
    {
        try
        {
            var s = await r.Content.ReadAsStreamAsync(ct);
            var buf = new byte[65536]; int total = 0, n;
            while ((n = await s.ReadAsync(buf.AsMemory(total, buf.Length - total), ct)) > 0) { total += n; if (total >= buf.Length) return null; }
            return JsonNode.Parse(Encoding.UTF8.GetString(buf, 0, total)) as JsonObject;
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or IOException) { return null; }
    }

    // one step: "waiting", "admitted", "refused", "revoked", "error: <reason>", "off"
    public async Task<string> RunOnce(CancellationToken ct)
    {
        if (!Active) return "off";
        var j = link.Join;
        if (!CairnSign.TryNormalizeUrl(j.PublicUrl, out _, out _)) { Fail("address", "The Lodge's public address is missing or not usable", TimeSpan.MaxValue); return "error: address"; }
        link.Keys.Get();
        string result;
        if (j.State is "waiting" or "admitted" or "refused" or "revoked" && !(j.State == "waiting" && j.Code.Length > 0)) result = await Status(ct);
        else result = await Ask(ct);
        Changed?.Invoke();
        return result;
    }

    async Task<string> Ask(CancellationToken ct)
    {
        var j = link.Join;
        // the mode first (public): in the beta without a code there is no point asking
        try
        {
            using var ir = await Http().SendAsync(new HttpRequestMessage(HttpMethod.Get, link.BaseUrl + "/v1/join/info"), ct);
            if (ir.StatusCode == HttpStatusCode.NotFound) { Fail("unsupported", "The Cairn at " + link.Host + " doesn't take requests to join yet", TimeSpan.FromHours(24)); return "error: unsupported"; }
            var io = await Body(ir, ct);
            lock (j.Gate) { j.Mode = (string)io?["mode"] ?? ""; j.BetaOpenSlots = (int?)io?["betaOpenSlots"]; }
            if (j.Mode == "beta" && j.Code.Length == 0) { Fail("invite-needed", "beta: an invite code from the Cairn's operator is needed", TimeSpan.MaxValue); return "error: invite-needed"; }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { Fail("network", "The Cairn could not be reached", TimeSpan.FromMinutes(15)); return "error: network"; }

        var o = new JsonObject { ["name"] = LodgeName() is { Length: > 0 } n ? n : "Lodge", ["publicUrl"] = j.PublicUrl, ["publicKey"] = link.Keys.PublicKey };
        if (j.Code.Length > 0) o["code"] = j.Code;
        var body = Encoding.UTF8.GetBytes(o.ToJsonString());
        var req = link.Request(HttpMethod.Post, "/v1/join", body, forceSign: true);
        req.Headers.Authorization = null;
        try
        {
            using var r = await Http().SendAsync(req, ct);
            var a = await Body(r, ct);
            if (r.IsSuccessStatusCode)
            {
                j.Take(a, NowS); j.Save();
                log?.LogInformation("Cairn: asked to join {Host}: {State}{Place}", link.Host, j.State, j.State == "waiting" ? " (place " + j.Place + ")" : "");
                nextRun = clock.UtcNow + (j.State == "waiting" ? TimeSpan.FromHours(1) : TimeSpan.FromHours(6));
                return j.State;
            }
            var reason = (string)a?["reason"] ?? "http-" + (int)r.StatusCode;
            var error = (string)a?["error"] ?? "The Cairn answered " + (int)r.StatusCode;
            if (reason is "refused" or "revoked") { lock (j.Gate) { j.State = reason; j.Code = ""; } }
            // a code that can't work is dropped (the Guild Master enters another); a full beta keeps it for later
            if (reason.StartsWith("code-", StringComparison.Ordinal) && reason != "code-blocked") lock (j.Gate) j.Code = "";
            var retry = reason switch
            {
                "unverified" => TimeSpan.FromMinutes(30),
                "beta-full" or "waiting-full" => TimeSpan.FromHours(6),
                "join-rate" or "code-blocked" => TimeSpan.FromSeconds(Math.Clamp(r.Headers.RetryAfter?.Delta?.TotalSeconds ?? 3600, 60, 86_400)),
                "clock" or "busy" => TimeSpan.FromMinutes(15),
                _ => TimeSpan.MaxValue,   // wait for the Guild Master (a new code, another address)
            };
            Fail(reason, error, retry);
            return "error: " + reason;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { Fail("network", "The Cairn could not be reached", TimeSpan.FromMinutes(15)); return "error: network"; }
    }

    async Task<string> Status(CancellationToken ct)
    {
        var j = link.Join;
        try
        {
            var req = link.Request(HttpMethod.Get, "/v1/join/status", forceSign: true);
            req.Headers.Authorization = null;
            using var r = await Http().SendAsync(req, ct);
            var a = await Body(r, ct);
            if (r.StatusCode == HttpStatusCode.NotFound) { lock (j.Gate) { j.State = ""; } j.Save(); Soon(); return "error: unknown"; }   // the Cairn forgot us: ask again
            if ((string)a?["reason"] == "revoked") { lock (j.Gate) { j.State = "revoked"; j.Code = ""; } }
            if (!r.IsSuccessStatusCode) { Fail((string)a?["reason"] ?? "http-" + (int)r.StatusCode, (string)a?["error"] ?? "The Cairn answered " + (int)r.StatusCode, TimeSpan.FromHours(1), keepState: true); return "error"; }
            var before = j.State;
            j.Take(a, NowS); j.Save();
            if (before != j.State) log?.LogInformation("Cairn: this Lodge is now {State} at {Host}", j.State, link.Host);
            nextRun = clock.UtcNow + (j.State == "waiting" ? TimeSpan.FromHours(1) : j.State == "admitted" ? TimeSpan.FromHours(6) : TimeSpan.FromHours(24));
            return j.State;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { Fail("network", "The Cairn could not be reached", TimeSpan.FromMinutes(15), keepState: true); return "error: network"; }
    }

    void Fail(string reason, string error, TimeSpan retry, bool keepState = false)
    {
        var j = link.Join;
        lock (j.Gate)
        {
            if (!keepState && j.State is not ("refused" or "revoked")) j.State = "error";
            j.Reason = reason; j.Error = error; j.Checked = NowS;
        }
        j.Save();
        nextRun = retry == TimeSpan.MaxValue ? DateTime.MaxValue : clock.UtcNow + retry;
        log?.LogWarning("Cairn: joining: {Reason} - {Error}", reason, error);   // never the code
    }
}
