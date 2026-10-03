// Lodge server: text chat, voice relay and file sharing for Elan's Addon Hub.
// One small process behind your existing web server (reverse proxy). Protocol: ../PROTOCOL.md
//
// Configuration (environment variables):
//   LODGE_CODE         invite code friends must enter (required)
//   LODGE_URLS         where to listen              (default http://127.0.0.1:5280 - keep it local)
//   LODGE_DATA         data folder (history, files) (default ./data)
//   LODGE_PATHBASE     URL prefix behind the proxy  (default /lodge; "" for a subdomain)
//   LODGE_MAX_FILE_MB  upload limit                 (default 25)
//   LODGE_FILE_DAYS    delete uploads after N days  (default 30)
//   LODGE_MAX_USERS    max people online            (default 25)

using Lodge;
using Microsoft.AspNetCore.StaticFiles;

var cfg = LodgeConfig.FromEnv();
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(cfg.Urls);
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = cfg.MaxFileBytes + (1 << 20));
var app = builder.Build();

if (!string.IsNullOrEmpty(cfg.PathBase)) app.UsePathBase(cfg.PathBase);
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

var files = new FileStore(cfg);
var lodge = new LodgeHub(cfg, files, app.Logger);

app.MapGet("/health", () => Results.Json(new { ok = true, version = LodgeHub.Version, online = lodge.Online }));

app.Map("/ws", async (HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    if (!cfg.CodeOk(ctx.Request.Query["code"])) { ctx.Response.StatusCode = 401; return; }
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    await lodge.Run(ws, ctx.Request.Query["name"], ctx.Request.Query["client"], ctx.RequestAborted);
});

// upload: raw body, headers X-Lodge-Code + X-File-Name (URL-encoded)
app.MapPost("/files", async (HttpContext ctx) =>
{
    if (!cfg.CodeOk(ctx.Request.Headers["X-Lodge-Code"])) return Results.StatusCode(401);
    var name = Uri.UnescapeDataString(ctx.Request.Headers["X-File-Name"].ToString());
    var by = Uri.UnescapeDataString(ctx.Request.Headers["X-Lodge-Name"].ToString());
    try { return Results.Json(await files.Save(ctx.Request.Body, name, by, ctx.RequestAborted)); }
    catch (FileTooBigException) { return Results.Json(new { error = $"File is larger than {cfg.MaxFileMb} MB" }, statusCode: 413); }
});

// download: code in X-Lodge-Code header or ?code=
app.MapGet("/files/{id}", (HttpContext ctx, string id) =>
{
    var code = ctx.Request.Headers["X-Lodge-Code"].ToString();
    if (string.IsNullOrEmpty(code)) code = ctx.Request.Query["code"];
    if (!cfg.CodeOk(code)) return Results.StatusCode(401);
    var meta = files.Get(id);
    if (meta == null) return Results.NotFound();
    return Results.File(files.PathOf(id), meta.Mime, meta.Name, enableRangeProcessing: true);
});

files.StartCleanup(app.Lifetime.ApplicationStopping);
app.Logger.LogInformation("Lodge {Version} listening on {Urls}, path base '{Base}', data {Data}",
    LodgeHub.Version, cfg.Urls, cfg.PathBase, cfg.DataDir);
app.Run();
