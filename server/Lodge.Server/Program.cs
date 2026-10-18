// Lodge server: text chat, voice relay and file sharing for Elan's Addon Hub.
// One small process behind your existing web server (reverse proxy). Protocol: ../PROTOCOL.md
// Configuration: environment variables, see ../deploy/lodge.env.example (LodgeConfig.FromEnv).

using System.Net;
using System.Threading.RateLimiting;
using Lodge;
using Microsoft.AspNetCore.HttpOverrides;

var cfg = LodgeConfig.FromEnv();
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(cfg.Urls);
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = Math.Max(Math.Max(cfg.MaxFileBytes, cfg.FilesMaxBytes), cfg.ClassicMaxMb * 1024L * 1024L) + (1 << 20));
// sockets are closed cleanly on stop (below), so a restart needn't wait the default 30 s
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(8));
// ASP.NET's per-request logs contain full URLs (a 1.x client puts its code in ?code=): keep them out of the journal
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
// the real visitor IP comes from the reverse proxy (needed for the wrong-code lockout)
var rejectedProxies = new List<string>();
builder.Services.Configure<ForwardedHeadersOptions>(o => TrustedProxies.Configure(o, cfg.TrustedProxies, rejectedProxies));
// a general per-IP request limit, separate from the wrong-code lockout, so floods can't overwhelm the code checks
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "?",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = cfg.HttpPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var app = builder.Build();
// the options are built lazily: resolve them now so a malformed LODGE_TRUSTED_PROXIES entry is reported at start, not on the first request
_ = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ForwardedHeadersOptions>>().Value;
foreach (var bad in rejectedProxies) app.Logger.LogWarning("LODGE_TRUSTED_PROXIES: ignoring '{Entry}' (not an IP address or CIDR network)", bad);

// owner/admin stats (/admin/stats): the connection's own address, before the proxy headers replace it - admin answers only a direct
// loopback connection (the operator's ssh tunnel or lodge-admin on the server itself), never anything that came through the reverse proxy
app.Use(async (ctx, next) =>
{
    ctx.Items[LodgeAdminApi.DirectKey] = ctx.Connection.RemoteIpAddress;
    ctx.Items[LodgeAdminApi.ProxiedKey] = LodgeAdminApi.HasProxyHeaders(ctx.Request.Headers);
    await next();
});
app.UseForwardedHeaders();
if (!string.IsNullOrEmpty(cfg.PathBase)) app.UsePathBase(cfg.PathBase);
// 2.30.2: every JSON answer (successes and early errors alike) says nosniff, and is not kept by a cache unless the route set its own policy
// (the Cairn manifest's ETag revalidation, the immutable dataset files)
app.Use(async (ctx, next) =>
{
    ctx.Response.OnStarting(() =>
    {
        if (ctx.Response.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) == true)
        {
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            if (!ctx.Response.Headers.ContainsKey("Cache-Control")) ctx.Response.Headers["Cache-Control"] = "no-store";
        }
        return Task.CompletedTask;
    });
    await next();
});
app.UseRateLimiter();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

var auth = new Auth(cfg, app.Logger);
var files = new FileStore(cfg);
var lodge = new LodgeHub(cfg, auth, files, app.Logger);

// opening an invite link in a browser lands here (see InvitePage: the part after # never reaches the server; the page is the same for everyone)
app.MapGet("/", (HttpContext ctx) =>
{
    var nonce = InvitePage.NewNonce();
    foreach (var h in InvitePage.Headers(nonce)) ctx.Response.Headers[h.Key] = h.Value;
    return Results.Content(InvitePage.Render(nonce, lodge.Info.Name, lodge.Info.Welcome), "text/html; charset=utf-8");
});

string IpOf(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "?";

// null = allowed (identity set); otherwise the error to return. Every credential check goes through here -
// a device token (X-Lodge-Token, 2.10) and a code (X-Lodge-Code) share one wrong-guess counter per IP.
IResult Gate(HttpContext ctx, string credential, out Identity who, bool isToken = false)
{
    who = null;
    var ip = IpOf(ctx);
    if (auth.Blocked(ip))
        return Results.Json(new { error = "Too many wrong invite codes - try again in 15 minutes" }, statusCode: 429);
    who = auth.CheckCredential(credential, isToken);
    if (who == null)
    {
        auth.Fail(ip);
        return isToken
            ? Results.Json(new { error = "This device was signed out - join again with a new invite", reason = "device-revoked" }, statusCode: 401)
            : Results.Json(new { error = "Wrong invite code" }, statusCode: 401);
    }
    auth.Success(ip);
    return null;
}

IResult GateReq(HttpContext ctx, out Identity who)
{
    var (cred, isToken) = CredFrom(ctx);
    return Gate(ctx, cred, out who, isToken);
}

// the device token wins over a code when a client sends both
(string Value, bool Token) CredFrom(HttpContext ctx)
{
    var token = ctx.Request.Headers["X-Lodge-Token"].ToString();
    return !string.IsNullOrEmpty(token) ? (token, true) : (CodeFrom(ctx), false);
}

// the X-Lodge-Code header wins; ?code= only for 1.x clients and only while LODGE_ALLOW_QUERY_CODE=true (off by default since 2.8)
string CodeFrom(HttpContext ctx)
{
    var code = ctx.Request.Headers["X-Lodge-Code"].ToString();
    if (!string.IsNullOrEmpty(code)) return code;
    return cfg.AllowQueryCode ? ctx.Request.Query["code"].ToString() : "";
}

// LODGE_ALLOW_QUERY_CODE=false: a ?code= is refused outright with a clear message (and never checked, so it is no
// oracle and costs no strike) - instead of being silently ignored
app.Use(async (ctx, next) =>
{
    if (!cfg.AllowQueryCode && ctx.Request.Query.ContainsKey("code") && string.IsNullOrEmpty(ctx.Request.Headers["X-Lodge-Code"])
        && string.IsNullOrEmpty(ctx.Request.Headers["X-Lodge-Token"]))
    {
        await Results.Json(new { error = "Send the invite code in the X-Lodge-Code header - please update Elan's Addon Hub" },
            statusCode: 401).ExecuteAsync(ctx);
        return;
    }
    await next();
});

// 2.10: what this server can do, so a newer client knows whether to use invite links and device tokens (older servers answer 404)
app.MapGet("/features", () => Results.Json(new { server = LodgeHub.Version, features = lodge.FeatureList().Where(f => f is "tokens" or "invites" or "devices" or "calendar" or "leave" or "lodgeinfo" or "flights" or "files" or "atlas" or "atlas2" or "atlas3"), lodge = new { name = lodge.Info.Name, welcome = lodge.Info.Welcome } }));

// 2.10: trade a one-time invite (X-Lodge-Invite header, or "invite" in the body) - or, to upgrade a device, the old permanent
// code (X-Lodge-Code) - for a per-device token. Body (optional JSON, max 4 KB): {"device": "PC name", "deviceId": "random id"}.
// The secrets travel in a header/body only, never in a URL. A wrong invite or code counts towards the same lockout as a wrong code.
app.MapPost("/exchange", async (HttpContext ctx) =>
{
    if (ctx.Request.ContentLength > 4096) return Results.Json(new { error = "Request too large" }, statusCode: 413);
    string device = "", deviceId = "", bodyInvite = "", typedName = "";
    try
    {
        using var ms = new MemoryStream();
        var buf = new byte[1024];
        int n;
        while ((n = await ctx.Request.Body.ReadAsync(buf)) > 0)
        {
            ms.Write(buf, 0, n);
            if (ms.Length > 4096) return Results.Json(new { error = "Request too large" }, statusCode: 413);
        }
        if (ms.Length > 0 && System.Text.Json.Nodes.JsonNode.Parse(ms.ToArray()) is System.Text.Json.Nodes.JsonObject o)
        {
            device = J.Str(o, "device") ?? ""; deviceId = J.Str(o, "deviceId") ?? ""; bodyInvite = J.Str(o, "invite") ?? ""; typedName = J.Str(o, "name") ?? "";
        }
    }
    catch (System.Text.Json.JsonException) { return Results.Json(new { error = "Bad request" }, statusCode: 400); }
    var invite = ctx.Request.Headers["X-Lodge-Invite"].ToString();
    if (string.IsNullOrEmpty(invite)) invite = bodyInvite;
    var code = ctx.Request.Headers["X-Lodge-Code"].ToString();
    ExchangeOutcome r;
    if (!string.IsNullOrEmpty(invite)) r = auth.ExchangeInvite(IpOf(ctx), invite, device, deviceId, typedName);
    else if (!string.IsNullOrEmpty(code)) r = auth.ExchangeCode(IpOf(ctx), code, device, deviceId);
    else return Results.Json(new { error = "Send the invite in the X-Lodge-Invite header" }, statusCode: 400);
    ctx.Response.Headers["Cache-Control"] = "no-store";
    if (r.Status != 200) return Results.Json(new { error = r.Error, reason = r.Reason }, statusCode: r.Status);
    return Results.Json(new
    {
        token = r.Token, deviceId = r.Device.Id, device = r.Device.DeviceName, name = r.Who.Name, role = r.Who.Role,
        guest = !r.Who.Personal, server = LodgeHub.Version,
    });
});

// public: just "ok". With a code it is an authenticated request like any other: 401 wrong, 429 blocked,
// otherwise the version and who's online. (No unmetered way to test a code.)
app.MapGet("/health", (HttpContext ctx) =>
{
    var (cred, isToken) = CredFrom(ctx);
    if (string.IsNullOrEmpty(cred)) return Results.Json(new { ok = true });
    var deny = Gate(ctx, cred, out _, isToken);
    return deny ?? Results.Json(new { ok = true, version = LodgeHub.Version, online = lodge.VisibleOnline });
});

app.Map("/ws", async (HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    var (cred, isToken) = CredFrom(ctx);
    var deny = Gate(ctx, cred, out var who, isToken);
    if (deny != null) { await deny.ExecuteAsync(ctx); return; }
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    await lodge.Run(ws, who, cred, ctx.Request.Query["name"], ctx.Request.Query["client"], IpOf(ctx), ctx.RequestAborted,
        invisible: ctx.Request.Query["vis"] == "invisible"); // hub 2.17+: "Appear offline" from the very first moment (no join is announced)
});

// upload: raw body, headers X-Lodge-Code + X-File-Name (URL-encoded)
app.MapPost("/files", async (HttpContext ctx) =>
{
    var deny = GateReq(ctx, out var who);
    if (deny != null) return deny;
    if (!Roles.Can(who.Role, Perm.ShareFiles))
        return Results.Json(new { error = "Initiates can't share files - ask the lodge owner for a personal invite" }, statusCode: 403);
    string name;
    try { name = Uri.UnescapeDataString(ctx.Request.Headers["X-File-Name"].ToString()); } catch { name = "file"; }
    var by = who.Name ?? Names.ForLog(ctx.Request.Headers["X-Lodge-Name"].ToString(), 24);
    var rateKey = who.Personal ? "p:" + who.Name.ToLowerInvariant() : "g:" + IpOf(ctx);
    try { return Results.Json(await files.Save(ctx.Request.Body, ctx.Request.ContentLength, name, by, rateKey, ctx.RequestAborted)); }
    catch (FileTooBigException) { return Results.Json(new { error = $"File is larger than {cfg.MaxFileMb} MB" }, statusCode: 413); }
    catch (UploadRefusedException e) { return Results.Json(new { error = e.Message }, statusCode: e.Status); }
    catch (OperationCanceledException) { return Results.StatusCode(499); }
    catch (IOException e)
    {
        app.Logger.LogWarning("Upload failed: {Error}", e.GetType().Name);
        return Results.Json(new { error = "The server couldn't store the file" }, statusCode: 500);
    }
});

// download: code in X-Lodge-Code (or legacy ?code=)
app.MapGet("/files/{id}", (HttpContext ctx, string id) =>
{
    var deny = GateReq(ctx, out _);
    if (deny != null) return deny;
    var meta = files.Get(id);
    if (meta == null) return Results.NotFound();
    // the content is private (a code is needed) and never to be sniffed into something executable
    ctx.Response.Headers["Cache-Control"] = "private, no-store";
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    return Results.File(files.PathOf(id), meta.Mime, meta.Name, enableRangeProcessing: true);
});

// 2.14 shared files ("files" feature). Same credential headers as every route. The slot list, delete and roll back are WebSocket
// messages (FilesModule); only the bytes travel here, streamed to disk.
string Unesc(HttpContext ctx, string header)
{
    try { return Uri.UnescapeDataString(ctx.Request.Headers[header].ToString()); } catch { return ""; }
}

IResult FilesFail(FilesException e) => Results.Json(new { error = e.Message, reason = e.Reason }, statusCode: e.Status);

async Task<IResult> PutSlot(HttpContext ctx, string slotId)
{
    var deny = GateReq(ctx, out var who);
    if (deny != null) return deny;
    var shared = lodge.SharedFiles;
    var by = who.Name ?? Names.ForLog(ctx.Request.Headers["X-Lodge-Name"].ToString(), 24);
    var rateKey = who.Personal ? "p:" + who.Name.ToLowerInvariant() : "g:" + IpOf(ctx);
    try
    {
        var view = await shared.Upload(by, who.Role, rateKey, slotId, Unesc(ctx, "X-Slot-Name"), Unesc(ctx, "X-File-Name"), Unesc(ctx, "X-Note"),
            ctx.Request.Headers["X-Sha256"].ToString(), ctx.Request.ContentLength, ctx.Request.Body, ctx.RequestAborted);
        return Results.Json(view);
    }
    catch (FilesException e) { return FilesFail(e); }
    catch (OperationCanceledException) { return Results.StatusCode(499); }
    catch (BadHttpRequestException) { return Results.Json(new { error = "File is larger than " + cfg.FilesMaxMb + " MB", reason = "too-big" }, statusCode: 413); }
    catch (IOException e)
    {
        app.Logger.LogWarning("Shared file upload failed: {Error}", e.GetType().Name);
        return Results.Json(new { error = "The server couldn't store the file", reason = "save" }, statusCode: 500);
    }
}

app.MapPut("/slots/new", async (HttpContext ctx) => await PutSlot(ctx, null));
app.MapPut("/slots/{id}", async (HttpContext ctx, string id) => await PutSlot(ctx, id));

app.MapGet("/slots/{id}/{v:int}", (HttpContext ctx, string id, int v) =>
{
    var deny = GateReq(ctx, out var who);
    if (deny != null) return deny;
    if (!Roles.Can(who.Role, Perm.FilesRead)) return Results.Json(new { error = "Not allowed", reason = "forbidden" }, statusCode: 403);
    try
    {
        var fs = lodge.SharedFiles.OpenVersion(id, v, out var ver);
        ctx.Response.Headers["Cache-Control"] = "private, no-store";
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        ctx.Response.Headers["X-Sha256"] = ver.Sha256;
        // always a download, never rendered or run: attachment + octet-stream, under the sanitised display name
        return Results.File(fs, "application/octet-stream", ver.FileName, enableRangeProcessing: true);
    }
    catch (FilesException e) { return FilesFail(e); }
});

// 2.17 hosted datasets (feature "classic"): the Classic baseline files, stored once for the whole lodge. Same credential headers as every route.
// Info and delete are WebSocket messages (DatasetsModule); only the bytes travel here, streamed to disk.
IResult DatasetFail(DatasetException e) => Results.Json(new { error = e.Message, reason = e.Reason }, statusCode: e.Status);

app.MapPut("/datasets/{name}", async (HttpContext ctx, string name) =>
{
    var deny = GateReq(ctx, out var who);
    if (deny != null) return deny;
    var by = who.Name ?? Names.ForLog(ctx.Request.Headers["X-Lodge-Name"].ToString(), 24);
    var rateKey = who.Personal ? "p:" + who.Name.ToLowerInvariant() : "g:" + IpOf(ctx);
    try
    {
        var view = await lodge.Datasets.Upload(by, who.Personal ? who.Role : "guest", rateKey, name, Unesc(ctx, "X-Version"), ctx.Request.Headers["X-Sha256"].ToString(),
            ctx.Request.ContentLength, ctx.Request.Body, ctx.RequestAborted);
        return Results.Json(view);
    }
    catch (DatasetException e) { return DatasetFail(e); }
    catch (OperationCanceledException) { return Results.StatusCode(499); }
    catch (BadHttpRequestException) { return Results.Json(new { error = "The file is larger than " + cfg.ClassicMaxMb + " MB", reason = "too-big" }, statusCode: 413); }
    catch (IOException e)
    {
        app.Logger.LogWarning("Dataset upload failed: {Error}", e.GetType().Name);
        return Results.Json(new { error = "The server couldn't store the file", reason = "save" }, statusCode: 500);
    }
});

app.MapGet("/datasets/{name}", (HttpContext ctx, string name) =>
{
    var deny = GateReq(ctx, out var who);
    if (deny != null) return deny;
    var rateKey = who.Personal ? "p:" + who.Name.ToLowerInvariant() : "g:" + IpOf(ctx);
    try
    {
        var fs = lodge.Datasets.OpenForDownload(name, who.Personal ? who.Role : "guest", rateKey, out var meta);
        ctx.Response.Headers["Cache-Control"] = "private, no-store";
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        ctx.Response.Headers["X-Sha256"] = meta.Sha256;
        ctx.Response.Headers["X-Version"] = Uri.EscapeDataString(meta.Version);
        // always a download, never rendered or run
        return Results.File(fs, "application/octet-stream", meta.Name + ".zip", enableRangeProcessing: true);
    }
    catch (DatasetException e) { return DatasetFail(e); }
});
// 2.27 give to get (feature "cairn2"): the Cairn's dataset as this Lodge received it, for MEMBERS whose Atlas sharing is on (atlas.sharing), only
// while the Lodge itself is in good standing at the Cairn. Same credential headers as every route. The manifest revalidates with its ETag.
IResult CairnFail((int status, string reason, string error) d) => Results.Json(new { error = d.error, reason = d.reason }, statusCode: d.status);

app.MapGet("/cairn/manifest", (HttpContext ctx) =>
{
    var deny = GateReq(ctx, out var who);
    if (deny != null) return deny;
    if (lodge.CairnIn.Check(who.Name, who.Personal, who.Role) is { } no) return CairnFail(no);
    var h = lodge.CairnIn.Receiver.Current;
    if (h == null) return CairnFail((503, "not-yet", "This Lodge has no copy of the Cairn's data yet - try again later"));
    var etag = CairnReceiver.ETagOf(h);
    ctx.Response.Headers["Cache-Control"] = "private, no-cache";
    ctx.Response.Headers["ETag"] = etag;
    if (ctx.Request.Headers["If-None-Match"].ToString() == etag) return Results.StatusCode(304);
    return Results.Json(lodge.CairnIn.Receiver.ManifestJson(h));
});

app.MapGet("/cairn/dataset", (HttpContext ctx) =>
{
    var deny = GateReq(ctx, out var who);
    if (deny != null) return deny;
    if (lodge.CairnIn.Check(who.Name, who.Personal, who.Role) is { } no) return CairnFail(no);
    try { lodge.CairnIn.CheckDownloadRate(who.Name); }
    catch (DatasetException e) { return DatasetFail(e); }
    var fs = lodge.CairnIn.Receiver.OpenDataset(out var h);
    if (fs == null) return CairnFail((503, "not-yet", "This Lodge has no copy of the Cairn's data yet - try again later"));
    lodge.CairnIn.NoteReceived(who.Name);   // connected outposts: counted for this week's anonymous "received" number (see CairnWeekly)
    ctx.Response.Headers["Cache-Control"] = "private, no-store";
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["X-Sha256"] = h.Sha256;
    return Results.File(fs, "application/gzip", h.File, enableRangeProcessing: false);
});

// 2.28: the Cairn's call back while this Lodge asks to join (CairnLink.ChallengeAnswer): public (the Cairn has no credentials here), answers only
// while the Guild Master's switch is on and a key pair exists, at most 30 a minute. Signs "elans-cairn-challenge/1\n<nonce>\n<publicUrl>".
app.MapGet("/cairn/challenge", (HttpContext ctx) =>
{
    ctx.Response.Headers["Cache-Control"] = "no-store";
    var a = lodge.Cairn.Link.ChallengeAnswer(ctx.Request.Query["nonce"].ToString());
    return a == null ? Results.NotFound() : Results.Json(a);
});

// read-only numbers for the server's operator (lodge-admin stats, or an operator's tool through an ssh tunnel): members, online, active in 7 days, messages, files, Cairn.
// Only from loopback without proxy headers AND with X-Lodge-Admin = LODGE_ADMIN_TOKEN (>= 24 characters; empty = off). Anything else: 404.
app.MapGet("/admin/stats", (HttpContext ctx) =>
{
    ctx.Response.Headers["Cache-Control"] = "no-store";
    if (!LodgeAdminApi.Allowed(cfg.AdminToken, ctx.Items[LodgeAdminApi.DirectKey] as IPAddress, ctx.Items[LodgeAdminApi.ProxiedKey] is true, ctx.Request.Headers["X-Lodge-Admin"].ToString()))
        return Results.NotFound();
    return Results.Json(LodgeAdminApi.Stats(lodge));
});

files.StartCleanup(app.Lifetime.ApplicationStopping);
lodge.StartRevalidation(app.Lifetime.ApplicationStopping);
// restart/stop: say goodbye properly (clients reconnect at once, see LodgeHub.CloseAllForShutdown)
app.Lifetime.ApplicationStopping.Register(() =>
{
    try { lodge.CloseAllForShutdown(TimeSpan.FromSeconds(3)).Wait(TimeSpan.FromSeconds(4)); } catch { }
    try { lodge.Chat.FlushAll(); } catch { }          // edits/reactions still waiting for their debounced write
    try { lodge.Armory.Flush(); } catch { }                // armory changes not yet written
    try { lodge.AtlasPool.Flush(); } catch { }             // atlas changes not yet written
    try { lodge.Badges.Flush(); } catch { }                // badge tallies not yet written
});
if (cfg.CairnEnabled)
{
    // the URL's host only (never the key); a bad URL or key keeps it off
    if (lodge.Cairn.Enabled) app.Logger.LogInformation("Cairn contribution on: {Host}, every {Minutes} min; its dataset is checked every {Hours} h and handed to members who share (give to get)", new Uri(cfg.CairnUrl).Host, cfg.CairnEveryMinutes, cfg.CairnFetchHours);
    else app.Logger.LogWarning("Cairn contribution stays off: LODGE_CAIRN_URL must be https (or http to this machine) and LODGE_CAIRN_KEY a Cairn key{Atlas}", cfg.AtlasEnabled ? "" : ", and the Atlas must be on");
}
else if (lodge.Cairn.Link.Join.On)
    app.Logger.LogInformation("Cairn: joining switched on by the Guild Master ({Host}, {State})", lodge.Cairn.Link.Host, lodge.Cairn.Link.Join.State.Length > 0 ? lodge.Cairn.Link.Join.State : "not asked yet");
if (cfg.RealmsConfigured)
    app.Logger.LogInformation("Realm status on: region {Region}, every {Seconds} s", lodge.Realms.Region, cfg.RealmsPollSeconds);
app.Logger.LogInformation("Lodge {Version} listening on {Urls}, path base '{Base}', data {Data}, query codes {Query}, admin stats {Admin}",
    LodgeHub.Version, cfg.Urls, cfg.PathBase, cfg.DataDir, cfg.AllowQueryCode ? "allowed" : "off",
    string.IsNullOrEmpty(cfg.AdminToken) ? "off" : cfg.AdminToken.Length < LodgeAdminApi.MinTokenLength ? "off (token shorter than 24)" : "loopback only");
app.Run();
