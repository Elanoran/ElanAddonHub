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
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = cfg.MaxFileBytes + (1 << 20));
// sockets are closed cleanly on stop (below), so a restart needn't wait the default 30 s
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(8));
// ASP.NET's per-request logs contain full URLs (a 1.x client puts its code in ?code=): keep them out of the journal
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
// the real visitor IP comes from the reverse proxy (needed for the wrong-code lockout)
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    foreach (var net in cfg.TrustedProxies)
    {
        var parts = net.Split('/');
        if (IPAddress.TryParse(parts[0], out var ip))
            o.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(ip, parts.Length > 1 ? int.Parse(parts[1]) : 32));
    }
});
// a general per-IP request limit, separate from the wrong-code lockout, so floods can't overwhelm the code checks
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "?",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = cfg.HttpPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var app = builder.Build();

app.UseForwardedHeaders();
if (!string.IsNullOrEmpty(cfg.PathBase)) app.UsePathBase(cfg.PathBase);
app.UseRateLimiter();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

var auth = new Auth(cfg, app.Logger);
var files = new FileStore(cfg);
var lodge = new LodgeHub(cfg, auth, files, app.Logger);

// opening an invite link in a browser lands here (the code after # never reaches the server)
app.MapGet("/", () => Results.Content("""
<!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>You're invited to the lodge</title>
<style>body{margin:0;min-height:100vh;display:grid;place-items:center;background:#0f1113;color:#e8eaed;font:15px/1.6 'Segoe UI',system-ui,sans-serif}
main{max-width:430px;padding:32px;background:#16191c;border:1px solid #262b30;border-radius:14px}h1{margin:0 0 6px;font-size:21px}
p{color:#8a9199;margin:0 0 18px}ol{padding-left:20px;margin:0 0 20px}li{margin:6px 0}a.btn{display:inline-block;background:#abd473;color:#111315;
text-decoration:none;font-weight:600;padding:10px 18px;border-radius:9px}</style></head><body><main>
<h1>You're invited to the lodge</h1><p>Chat, voice and files with your friends, inside Elan's Addon Hub.</p>
<ol><li>Download <b>Elan's Addon Hub</b> and start it.</li><li>Open the <b>Lodge</b> tab.</li>
<li>Paste the <b>whole invite link</b> you were sent and press Join.</li></ol>
<a class="btn" href="https://github.com/Elanoran/ElanAddonHub/releases/latest">Download the hub</a></main></body></html>
""", "text/html"));

string IpOf(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "?";

// null = allowed (identity set); otherwise the error to return. Every credential check goes through here.
IResult Gate(HttpContext ctx, string code, out Identity who)
{
    who = null;
    var ip = IpOf(ctx);
    if (auth.Blocked(ip))
        return Results.Json(new { error = "Too many wrong invite codes - try again in 15 minutes" }, statusCode: 429);
    who = auth.Check(code);
    if (who == null) { auth.Fail(ip); return Results.Json(new { error = "Wrong invite code" }, statusCode: 401); }
    auth.Success(ip);
    return null;
}

// the X-Lodge-Code header wins; ?code= only for 1.x clients and only while LODGE_ALLOW_QUERY_CODE isn't false
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
    if (!cfg.AllowQueryCode && ctx.Request.Query.ContainsKey("code") && string.IsNullOrEmpty(ctx.Request.Headers["X-Lodge-Code"]))
    {
        await Results.Json(new { error = "Send the invite code in the X-Lodge-Code header - please update Elan's Addon Hub" },
            statusCode: 401).ExecuteAsync(ctx);
        return;
    }
    await next();
});

// public: just "ok". With a code it is an authenticated request like any other: 401 wrong, 429 blocked,
// otherwise the version and who's online. (No unmetered way to test a code.)
app.MapGet("/health", (HttpContext ctx) =>
{
    var code = CodeFrom(ctx);
    if (string.IsNullOrEmpty(code)) return Results.Json(new { ok = true });
    var deny = Gate(ctx, code, out _);
    return deny ?? Results.Json(new { ok = true, version = LodgeHub.Version, online = lodge.VisibleOnline });
});

app.Map("/ws", async (HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    var code = CodeFrom(ctx);
    var deny = Gate(ctx, code, out var who);
    if (deny != null) { await deny.ExecuteAsync(ctx); return; }
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    await lodge.Run(ws, who, code, ctx.Request.Query["name"], ctx.Request.Query["client"], IpOf(ctx), ctx.RequestAborted,
        invisible: ctx.Request.Query["vis"] == "invisible"); // hub 2.17+: "Appear offline" from the very first moment (no join is announced)
});

// upload: raw body, headers X-Lodge-Code + X-File-Name (URL-encoded)
app.MapPost("/files", async (HttpContext ctx) =>
{
    var deny = Gate(ctx, CodeFrom(ctx), out var who);
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
    var deny = Gate(ctx, CodeFrom(ctx), out _);
    if (deny != null) return deny;
    var meta = files.Get(id);
    if (meta == null) return Results.NotFound();
    return Results.File(files.PathOf(id), meta.Mime, meta.Name, enableRangeProcessing: true);
});

files.StartCleanup(app.Lifetime.ApplicationStopping);
lodge.StartRevalidation(app.Lifetime.ApplicationStopping);
// restart/stop: say goodbye properly (clients reconnect at once, see LodgeHub.CloseAllForShutdown)
app.Lifetime.ApplicationStopping.Register(() =>
{
    try { lodge.CloseAllForShutdown(TimeSpan.FromSeconds(3)).Wait(TimeSpan.FromSeconds(4)); } catch { }
});
app.Logger.LogInformation("Lodge {Version} listening on {Urls}, path base '{Base}', data {Data}, query codes {Query}",
    LodgeHub.Version, cfg.Urls, cfg.PathBase, cfg.DataDir, cfg.AllowQueryCode ? "allowed" : "off");
app.Run();
