#if ELANS_DEVPIPE
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using ElansAddonHub.Services.WorldMap;

namespace ElansAddonHub.Services
{
    // ============================================================================================================================
    // The developer's pipe (3.2): ONLY in a build made with  -p:ElansDevPipe=true  (Tools/devoutpost.ps1); release.py and the self-test never set it, and the
    // self-test checks that a normal build has no such type. Tools/OutpostDevMcp talks to it so a developer's assistant can look at and steer the 3D world map
    // while it is being made: run script in the map page, take a picture of it, move the camera, reload the page's files from the source folder, read the log.
    //   pipe   \\.\pipe\ElansAddonHub.Dev.<user SID>.<session>   only this Windows user can open it (LinkPipe.Security: the network is refused outright)
    //   lines  one JSON request per line {id, cmd, args}; one JSON answer per line {id, ok, result | error, image (base64 JPEG) + mime}; at most 256 KB a request
    // It reaches nothing but the world map page and the log: no files, no settings beyond the map's own switches, no Lodge.
    // A dev build also never offers an update (SelfUpdater.IsNewer) and does not register the elanshub:// links, so it cannot replace or redirect the real Outpost.
    // ============================================================================================================================
    public static class DevPipe
    {
        public const int MaxRequest = 256 * 1024;
        public static string Name()
        {
            var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? "user";
            int session = 0; try { session = System.Diagnostics.Process.GetCurrentProcess().SessionId; } catch { }
            return "ElansAddonHub.Dev." + sid + "." + session;
        }

        static MainWindow main;
        static CancellationTokenSource cts;
        public static string PageSource;          // the source folder of the page files (src\WorldMap): "reload" copies them over the page folder

        public static void Start(MainWindow w)
        {
            main = w; cts = new CancellationTokenSource();
            PageSource = FindPageSource();
            Util.Log("dev pipe: listening on " + Name() + (PageSource != null ? "; page files from " + PageSource : "; no page source folder found"));
            Task.Run(() => Loop(cts.Token));
        }
        public static void Stop() { try { cts?.Cancel(); } catch { } }

        // the exe sits in <repo>\src\bin\<config>\<tfm>\...: the first parent folder with WorldMap\view3d.js is the source
        static string FindPageSource()
        {
            var env = Environment.GetEnvironmentVariable("ELANS_DEV_PAGE");
            if (!string.IsNullOrEmpty(env) && File.Exists(Path.Combine(env, "view3d.js"))) return env;
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 8 && d != null; i++, d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "WorldMap", "view3d.js")) && File.Exists(Path.Combine(d.FullName, "ElansAddonHub.csproj"))) return Path.Combine(d.FullName, "WorldMap");
            return null;
        }

        static async Task Loop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                NamedPipeServerStream pipe = null;
                try
                {
                    pipe = NamedPipeServerStreamAcl.Create(Name(), PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 1 << 16, 1 << 16, LinkPipe.Security());
                    await pipe.WaitForConnectionAsync(ct);
                    await Serve(pipe, ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception e) { Util.Log("dev pipe: " + e.Message); try { await Task.Delay(500, ct); } catch { break; } }
                finally { try { pipe?.Dispose(); } catch { } }
            }
        }

        static async Task Serve(NamedPipeServerStream pipe, CancellationToken ct)
        {
            var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1 << 16, true);
            var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1 << 16, true) { AutoFlush = true, NewLine = "\n" };
            while (pipe.IsConnected && !ct.IsCancellationRequested)
            {
                var line = await ReadLine(reader, ct);
                if (line == null) break;
                Dictionary<string, object> answer;
                object id = null;
                try
                {
                    var req = Json.Obj(line) ?? throw new InvalidDataException("not a JSON object");
                    req.TryGetValue("id", out id);
                    var args = req.TryGetValue("args", out var a) && a is Dictionary<string, object> ad ? ad : new Dictionary<string, object>();
                    answer = await OnUi(() => Run(req.Str("cmd") ?? "", args));
                }
                catch (Exception e) { answer = new Dictionary<string, object> { ["ok"] = false, ["error"] = e.Message }; }
                answer["id"] = id;
                await writer.WriteLineAsync(Json.Write(answer));
            }
        }

        // a line of at most MaxRequest characters (a longer one ends the connection)
        static async Task<string> ReadLine(StreamReader r, CancellationToken ct)
        {
            var sb = new StringBuilder(); var buf = new char[1];
            while (true)
            {
                int n = await r.ReadAsync(buf.AsMemory(0, 1), ct);
                if (n == 0) return sb.Length > 0 ? sb.ToString() : null;
                if (buf[0] == '\n') return sb.ToString().TrimEnd('\r');
                sb.Append(buf[0]);
                if (sb.Length > MaxRequest) return null;
            }
        }

        static Task<Dictionary<string, object>> OnUi(Func<Task<Dictionary<string, object>>> f)
        {
            var tcs = new TaskCompletionSource<Dictionary<string, object>>();
            Application.Current.Dispatcher.BeginInvoke(new Action(async () =>
            {
                try { tcs.SetResult(await f()); } catch (Exception e) { tcs.SetException(e); }
            }));
            return tcs.Task;
        }

        static Dictionary<string, object> Ok(object result) => new Dictionary<string, object> { ["ok"] = true, ["result"] = result };
        static double Num(Dictionary<string, object> a, string k, double def) => a.TryGetValue(k, out var o) && o is double d && !double.IsNaN(d) && !double.IsInfinity(d) ? d : def;
        static string I(double d) => d.ToString("R", CultureInfo.InvariantCulture);

        static async Task<string> Eval(string js)
        {
            var r = await main.WorldMapView.EvalForTest(js);
            // ExecuteScriptAsync answers JSON; a page hook that returns a JSON string comes back as a JSON string of it
            if (r != null && r.Length >= 2 && r[0] == '"') { var s = Json.Parse(r) as string; if (s != null) return s; }
            return r;
        }

        static async Task<bool> WaitFor(Func<bool> cond, int ms)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms) { if (cond()) return true; await Task.Delay(100); }
            return cond();
        }

        static async Task<Dictionary<string, object>> Run(string cmd, Dictionary<string, object> a)
        {
            var wm = main.WorldMapView;
            switch (cmd)
            {
                case "status":
                    return Ok(new Dictionary<string, object>
                    {
                        ["version"] = App.Version, ["page"] = wm.LoadedPage ?? "", ["map"] = wm.MapId, ["ready"] = wm.PageReady, ["is3d"] = wm.Is3D, ["gl"] = wm.GlOk,
                        ["pageSource"] = PageSource ?? "", ["data"] = Util.DataDir,
                    });
                case "open":
                    {
                        int map = (int)Num(a, "map", 0); double x = Num(a, "x", double.NaN), y = Num(a, "y", double.NaN);
                        main.ShowTab("atlas");
                        var sub = "worldmap:" + map.ToString(CultureInfo.InvariantCulture) + (double.IsNaN(x) || double.IsNaN(y) ? "?" : "?x=" + I(x) + "&y=" + I(y) + "&") + "z=6&m=3";
                        await main.AtlasView.Open(sub);
                        bool ok = await WaitFor(() => wm.PageReady && wm.LoadedPage == "3d" && wm.MapId == map, 60000);
                        return Ok(new Dictionary<string, object> { ["ready"] = ok, ["gl"] = wm.GlOk, ["fallback"] = wm.GlFallbackReason ?? "" });
                    }
                case "view":
                    {
                        double x = Num(a, "x", double.NaN), y = Num(a, "y", double.NaN);
                        if (double.IsNaN(x) || double.IsNaN(y)) throw new ArgumentException("view needs world x and y (yards: x north, y west)");
                        WorldMapMath.WorldToLatLng(x, y, out var lat, out var lng);
                        await Eval("window.wm3.setView(" + I(lat) + "," + I(lng) + "," + I(Num(a, "dist", 400)) + "," + I(Num(a, "az", 0)) + "," + I(Num(a, "tilt", 40)) + ")");
                        await Eval("window.wm3.step(3,130)");
                        return Ok(true);
                    }
                case "eval":
                    {
                        var js = a.Str("js") ?? throw new ArgumentException("eval needs js");
                        return Ok(await Eval(js));
                    }
                case "level":
                    {
                        var lv = WorldMapObjects.Normalize(a.Str("level"));
                        return Ok(await Eval("window.wm3.objectsLevel('" + lv + "', true)"));
                    }
                case "objects": return Ok(await Eval("window.wm3.objects()"));
                case "settle":
                    {
                        // waits until the page has nothing in flight and the trees stop changing (or the time is up)
                        int ms = (int)Math.Min(60000, Num(a, "ms", 15000)); var sw = System.Diagnostics.Stopwatch.StartNew(); int calm = 0; string last = null;
                        while (sw.ElapsedMilliseconds < ms)
                        {
                            await Task.Delay(400); await Eval("window.wm3.step(2,130)");
                            var o = Json.Obj(await Eval("window.wm3.objects()") ?? "{}"); var st = Json.Obj(await Eval("window.wm3.state ? window.wm3.state() : '{}'") ?? "{}");
                            var key = o?.Int("drawn") + "/" + o?.Int("modelsLoading") + "/" + st?.Int("inFlight");
                            if (key == last && o?.Int("modelsLoading") == 0 && st?.Int("inFlight") == 0) { if (++calm >= 2) break; } else calm = 0;
                            last = key;
                        }
                        return Ok(new Dictionary<string, object> { ["ms"] = sw.ElapsedMilliseconds });
                    }
                case "bench":
                    {
                        int ms = (int)Math.Min(10000, Math.Max(500, Num(a, "ms", 2500)));
                        await Eval("window.__bench = null; window.wm3.bench(" + ms + ").then(function (r) { window.__bench = r; }); 1");
                        await Task.Delay(ms + 300);
                        for (int i = 0; i < 40; i++) { var r = await Eval("window.__bench ? JSON.stringify(window.__bench) : null"); if (!string.IsNullOrEmpty(r) && r != "null") return Ok(r); await Task.Delay(250); }
                        return Ok(null);
                    }
                case "screenshot":
                    {
                        var png = await wm.CaptureForTest() ?? throw new InvalidOperationException("the world map page is not open");
                        int w = (int)Math.Max(200, Math.Min(1920, Num(a, "width", 960)));
                        return new Dictionary<string, object> { ["ok"] = true, ["result"] = "screenshot", ["image"] = Convert.ToBase64String(Scale(png, w)), ["mime"] = "image/jpeg" };
                    }
                case "reload":
                    {
                        // the page files from the source folder, then the map again from scratch (where the camera was is kept; turn and tilt are not)
                        if (PageSource == null) throw new InvalidOperationException("no page source folder (set ELANS_DEV_PAGE to src\\WorldMap)");
                        int n = 0;
                        foreach (var f in WorldMapPage.Files) { var s = Path.Combine(PageSource, f); if (File.Exists(s)) { File.Copy(s, Path.Combine(WorldMapPage.Dir, f), true); n++; } }
                        wm.DevReload();
                        bool ok = await WaitFor(() => wm.PageReady, 60000);
                        return Ok(new Dictionary<string, object> { ["files"] = n, ["ready"] = ok });
                    }
                case "log":
                    {
                        int lines = (int)Math.Max(1, Math.Min(400, Num(a, "lines", 40)));
                        var p = Path.Combine(Util.DataDir, "hub.log");
                        var all = File.Exists(p) ? File.ReadAllLines(p) : new string[0];
                        var filter = a.Str("filter");
                        var sel = string.IsNullOrEmpty(filter) ? all : all.Where(l => l.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
                        return Ok(string.Join("\n", sel.Skip(Math.Max(0, sel.Length - lines))));
                    }
                default: throw new ArgumentException("unknown command '" + cmd + "' (status, open, view, eval, level, objects, settle, bench, screenshot, reload, log)");
            }
        }

        // the picture made smaller (fewer pixels: what the assistant reads costs by the pixel), as JPEG
        static byte[] Scale(byte[] png, int width)
        {
            var src = BitmapFrame.Create(new MemoryStream(png), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            BitmapSource img = src;
            if (src.PixelWidth > width) img = new TransformedBitmap(src, new System.Windows.Media.ScaleTransform((double)width / src.PixelWidth, (double)width / src.PixelWidth));
            var enc = new JpegBitmapEncoder { QualityLevel = 85 };
            enc.Frames.Add(BitmapFrame.Create(img));
            var ms = new MemoryStream(); enc.Save(ms); return ms.ToArray();
        }
    }
}
#endif
