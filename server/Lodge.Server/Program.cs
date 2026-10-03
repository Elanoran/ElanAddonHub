// Lodge server: text chat, voice relay and file sharing for Elan's Addon Hub.
// One small process behind your existing web server (reverse proxy). Protocol: ../PROTOCOL.md
//
// Configuration (environment variables):
//   LODGE_CODES_FILE       personal invite codes, "Name:code" per line (default /etc/lodge/codes,
//                          manage with `sudo lodge-admin`)
//   LODGE_CODE             optional shared invite code without a fixed name
//   LODGE_URLS             where to listen              (default http://127.0.0.1:5280 - keep it local)
//   LODGE_DATA             data folder (history, files) (default ./data)
//   LODGE_PATHBASE         URL prefix behind the proxy  (default /lodge; "" for a subdomain)
//   LODGE_MAX_FILE_MB      upload limit                 (default 25)
//   LODGE_FILE_DAYS        delete uploads after N days  (default 30)
//   LODGE_MAX_USERS        max people online            (default 25)
//   LODGE_TRUSTED_PROXIES  extra proxy networks, e.g. 172.16.0.0/12 for a proxy in Docker (localhost is always trusted)

using System.Net;
using Lodge;
using Microsoft.AspNetCore.HttpOverrides;

var cfg = LodgeConfig.FromEnv();
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(cfg.Urls);
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = cfg.MaxFileBytes + (1 << 20));
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
var app = builder.Build();

app.UseForwardedHeaders();
if (!string.IsNullOrEmpty(cfg.PathBase)) app.UsePathBase(cfg.PathBase);
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

var auth = new Auth(cfg, app.Logger);
var files = new FileStore(cfg);
var lodge = new LodgeHub(cfg, auth, files, app.Logger);

// null = allowed (identity set); otherwise the error to return
IResult Gate(HttpContext ctx, string code, out Identity who)
{
    who = null;
    var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
    if (auth.Blocked(ip))
        return Results.Json(new { error = "Too many wrong invite codes - try again in 15 minutes" }, statusCode: 429);
    who = auth.Check(code);
    if (who == null) { auth.Fail(ip); return Results.Json(new { error = "Wrong invite code" }, statusCode: 401); }
    auth.Success(ip);
    return null;
}

static string CodeFrom(HttpContext ctx)
{
    var code = ctx.Request.Headers["X-Lodge-Code"].ToString();
    return string.IsNullOrEmpty(code) ? ctx.Request.Query["code"].ToString() : code;
}

// public: just "ok"; with a valid code also the version and who's online
app.MapGet("/health", (HttpContext ctx) =>
{
    var code = CodeFrom(ctx);
    if (!string.IsNullOrEmpty(code) && auth.Check(code) != null)
        return Results.Json(new { ok = true, version = LodgeHub.Version, online = lodge.Online });
    return Results.Json(new { ok = true });
});

app.Map("/ws", async (HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    var code = CodeFrom(ctx);
    var deny = Gate(ctx, code, out var who);
    if (deny != null) { await deny.ExecuteAsync(ctx); return; }
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    await lodge.Run(ws, who, code, ctx.Request.Query["name"], ctx.Request.Query["client"], ctx.RequestAborted);
});

// upload: raw body, headers X-Lodge-Code + X-File-Name (URL-encoded)
app.MapPost("/files", async (HttpContext ctx) =>
{
    var deny = Gate(ctx, CodeFrom(ctx), out var who);
    if (deny != null) return deny;
    if (!Roles.Can(who.Role, Perm.ShareFiles))
        return Results.Json(new { error = "Initiates can't share files - ask the lodge owner for a personal invite" }, statusCode: 403);
    var name = Uri.UnescapeDataString(ctx.Request.Headers["X-File-Name"].ToString());
    var by = who.Name ?? Uri.UnescapeDataString(ctx.Request.Headers["X-Lodge-Name"].ToString());
    try { return Results.Json(await files.Save(ctx.Request.Body, name, by, ctx.RequestAborted)); }
    catch (FileTooBigException) { return Results.Json(new { error = $"File is larger than {cfg.MaxFileMb} MB" }, statusCode: 413); }
});

// download: code in X-Lodge-Code header or ?code=
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
app.Logger.LogInformation("Lodge {Version} listening on {Urls}, path base '{Base}', data {Data}, codes {Codes}",
    LodgeHub.Version, cfg.Urls, cfg.PathBase, cfg.DataDir, cfg.CodesFile);
app.Run();
