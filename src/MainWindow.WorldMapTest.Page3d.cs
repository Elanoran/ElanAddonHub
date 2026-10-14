using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Maps;
using ElansAddonHub.Services.WorldMap;

namespace ElansAddonHub
{
    // --selftest "worldmap", part 4 (2.53): the flat map's zone borders layer, "Navigate here" with the terrain's zones, the 2D | 3D switch and the 3D page in a
    // real WebView2 on the invented continent (WebGL when the machine has it, the fallback to the flat map when it has not), and the new messages of the protocol.
    public partial class MainWindow
    {
        async Task<Dictionary<string, object>> Wm3State()
        {
            var r = await WorldMapView.EvalForTest("window.wm3 ? window.wm3.state() : null");
            if (string.IsNullOrEmpty(r) || r == "null") return null;
            var s = Json.Parse(r) as string;
            return s == null ? null : Json.Obj(s);
        }

        // the invented continent with its areas: Westmark (100, areas 101 / 102) = Greenvale's map, Eastmark (200) = Redmoor's, Lakeland (300)
        static MapSet WmAreaSet(MapSet taxi)
        {
            var lset = WmLayerSet(taxi);
            lset.Areas.Add(new AreaInfo { Id = 100, Name = "Westmark", ParentId = 0 });
            lset.Areas.Add(new AreaInfo { Id = 101, Name = "Westmark Fields", ParentId = 100 });
            lset.Areas.Add(new AreaInfo { Id = 102, Name = "Westmark Hills", ParentId = 100 });
            lset.Areas.Add(new AreaInfo { Id = 200, Name = "Eastmark", ParentId = 0 });
            lset.Areas.Add(new AreaInfo { Id = 201, Name = "Eastmark Coast", ParentId = 200 });
            lset.Areas.Add(new AreaInfo { Id = 202, Name = "Eastmark Pass", ParentId = 200 });
            lset.Areas.Add(new AreaInfo { Id = 300, Name = "Lakeland", ParentId = 0 });
            lset.Assignments.First(a => a.UiMapId == 11).AreaId = 100;
            lset.Assignments.First(a => a.UiMapId == 12).AreaId = 200;
            return lset;
        }

        // the protocol messages of the 3D page, and the ids that carry the mode (no browser)
        void WorldMapProtocol3dChecks(CheckFn Check)
        {
            string Why(string json) { WorldMapProtocol.Parse(json, out var why); return why; }
            var gl = WorldMapProtocol.Parse("{\"t\":\"webgl\",\"ok\":false,\"why\":\"no \\u0007GPU\\n\",\"renderer\":\"ANGLE (x)\"}", out _);
            var v3 = WorldMapProtocol.Parse("{\"t\":\"view3\",\"lat\":-120.5,\"lng\":130.25,\"dist\":2400,\"az\":370,\"tilt\":45}", out _);
            var st = WorldMapProtocol.Parse("{\"t\":\"stats\",\"fps\":59.5,\"mem\":310,\"tris\":412345,\"tiles\":88,\"tex\":71}", out _);
            var pf = WorldMapProtocol.Parse("{\"t\":\"prefs\",\"detail\":\"high\",\"exag\":2.5}", out _);
            Check("protocol 3D: webgl {ok, why, renderer}, view3 {lat, lng, dist, az, tilt}, stats {fps, mem, tris, tiles, tex} and prefs {detail, exag} parse; control characters are removed from the page's text",
                gl != null && gl.Type == WorldMapMessageType.Webgl && !gl.Ok && gl.Why == "no GPU" && gl.Detail == "ANGLE (x)" && v3 != null && v3.Type == WorldMapMessageType.View3 && v3.Dist == 2400 && v3.Az == 370 && v3.Tilt == 45
                && st != null && st.Fps == 59.5 && st.MemMb == 310 && st.Tris == 412345 && st.Tiles == 88 && st.Textures == 71 && pf != null && pf.Detail == "high" && pf.Exag == 2.5, gl?.Why);
            var bad = new[]
            {
                "{\"t\":\"webgl\"}", "{\"t\":\"webgl\",\"ok\":\"yes\"}",
                "{\"t\":\"view3\",\"lat\":-120,\"lng\":130,\"dist\":1,\"az\":0,\"tilt\":45}", "{\"t\":\"view3\",\"lat\":-120,\"lng\":130,\"dist\":500000,\"az\":0,\"tilt\":45}",
                "{\"t\":\"view3\",\"lat\":5,\"lng\":130,\"dist\":500,\"az\":0,\"tilt\":45}", "{\"t\":\"view3\",\"lat\":-120,\"lng\":130,\"dist\":500,\"az\":0}",
                "{\"t\":\"view3\",\"lat\":-120,\"lng\":130,\"dist\":500,\"az\":9999,\"tilt\":45}", "{\"t\":\"view3\",\"lat\":-120,\"lng\":130,\"dist\":500,\"az\":0,\"tilt\":120}",
                "{\"t\":\"stats\",\"fps\":-1,\"mem\":1}", "{\"t\":\"stats\",\"fps\":99999,\"mem\":1}", "{\"t\":\"stats\",\"fps\":60}",
                "{\"t\":\"prefs\",\"detail\":\"ultra\",\"exag\":2}", "{\"t\":\"prefs\",\"detail\":\"low\",\"exag\":9}", "{\"t\":\"prefs\",\"detail\":\"low\"}",
                "{\"t\":\"need\",\"layer\":\"hack\"}", "{\"t\":\"layers\",\"on\":[\"borders\",\"nodes.x\"]}",
            };
            var notRefused = bad.Where(j => Why(j) == null).ToList();
            Check("protocol 3D: sixteen bad messages are all refused (no ok flag, a camera too close / too far / off the map / without a tilt / with a bearing or tilt out of range, stats out of range or missing, an unknown detail, a height factor outside 1-3, an unknown layer)", notRefused.Count == 0, string.Join(" | ", notRefused));
            Check("protocol 3D: the borders layer is a known layer id (switching it on is accepted), hello says which page it is", WorldMapProtocol.Parse("{\"t\":\"layers\",\"on\":[\"zones\",\"borders\"]}", out _) != null && WorldMapProtocol.Parse("{\"t\":\"need\",\"layer\":\"borders\"}", out _) != null
                && WorldMapProtocol.Parse("{\"t\":\"hello\",\"p\":\"3d\"}", out _).Detail == "3d" && WorldMapProtocol.Parse("{\"t\":\"hello\"}", out _).Detail == "");
            // ids: the mode rides in &m=
            var t3 = WorldMapIds.Parse("1?x=1630&y=-4373&z=5&m=3", s => 1);
            var t2 = WorldMapIds.Parse("1?x=1630&y=-4373&z=5&m=2", s => 1);
            var tn = WorldMapIds.Parse("1?x=1630&y=-4373&z=5", s => 1);
            var tb = WorldMapIds.Parse("1?x=1630&y=-4373&z=5&m=4", s => 1);
            Check("ids: &m=3 asks for the 3D view and &m=2 for the flat map (none = as last used); another m is refused; the canonical id keeps the mode, the flat map's ids do not change",
                t3.Valid && t3.Mode == 3 && t2.Valid && t2.Mode == 2 && tn.Valid && tn.Mode == 0 && !tb.Valid && WorldMapIds.SubOf(1, null, true, 1630, -4373, 5, false, 3) == "1?x=1630&y=-4373&z=5&m=3"
                && WorldMapIds.SubOf(1, null, true, 1630, -4373, 5) == "1?x=1630&y=-4373&z=5" && WorldMapIds.SubOf(1, "kalimdor", false, 0, 0, 0, false, 3) == "kalimdor?m=3" && WorldMapIds.CanonicalSyntax("1?x=1630&y=-4373&z=5&m=3") == "1?x=1630&y=-4373&z=5&m=3"
                && WorldMapIds.Id(1, null, true, 1, 2, 3, true, 3) == "worldmap:1?x=1&y=2&z=3&p=1&m=3");
            Check("view hand-over: the 3D camera distance and the flat map's zoom are the same rule (zoom 4.5 = " + Math.Round(106666 / Math.Pow(2, 4.5)) + " yards away) in the app and the page",
                Math.Abs(WorldMapView.DistToZoom(106666 / Math.Pow(2, 4.5)) - 4.5) < 1e-9 && WorldMapView.DistToZoom(1e9) == 0 && WorldMapView.DistToZoom(0.001) == 9);
        }

        async Task WorldMapPage3dTest(string dir, List<string> notes, CheckFn Check, Func<string, string> P, MapSet taxi, MapIndex lidx)
        {
            var I = CultureInfo.InvariantCulture;
            var idx3 = new MapIndex(WmAreaSet(taxi));
            var savedAdt = WorldMapService.AdtSourceForTest;
            double savedExag = settings.WorldMapExag; var savedDetail = settings.WorldMap3dDetail; bool saved3d = settings.WorldMap3d;
            try
            {
                MapData.SetForTest(idx3, "wow_classic_beta|1.60.1.70338|0123456789abcdef");
                WorldMapService.AdtSourceForTest = fdid => { var (c, r) = WmAdtOf(fdid); return WmAdtCached(c, r); };
                settings.WorldMap3d = false; settings.WorldMap3dDetail = null; settings.WorldMapExag = 0; settings.WorldMapLayers = null;
                WorldMapView.ClipboardSeamForTest = null;
                string copied = null; WorldMapView.ClipboardSeamForTest = t => copied = t;
                ShowTab("atlas"); AtlasView.History.Clear();

                // ---------------------------------------------------------------- the flat map: Navigate here before the terrain exists, then the zone borders layer
                await AtlasView.Open("worldmap:0?x=50&y=100&z=4.5");
                bool ready2 = await WaitFor(() => WorldMapView.PageReady && WorldMapView.LoadedPage == "2d", 90000);
                WorldMapMath.WorldToLatLng(50, 100, out var nlat, out var nlng);
                await WmEval("window.chrome.webview.postMessage({t:'nav', lat:" + nlat.ToString("R", I) + ", lng:" + nlng.ToString("R", I) + "})");
                await WaitFor(() => copied != null, 8000);
                var wayBox = copied; copied = null;
                Check("borders: the flat map is open at x 50, y 100; without terrain \"Navigate here\" uses the zone rectangles of the map tables (the most central one: Redmoor, map 12)", ready2 && wayBox != null && wayBox.StartsWith("/etk way 12 "), wayBox);
                var texts = new List<string>(); void OnB(int id) { var s = WorldMapService.RunningStateOf(id); if (s != null) lock (texts) texts.Add(s.Text); }
                WorldMapService.BuildChanged += OnB;
                var notesSeen = new List<string>();
                var noteTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) }; noteTimer.Tick += (s, e) => { var n = WorldMapView.Note; if (n.Length > 0 && !notesSeen.Contains(n)) notesSeen.Add(n); }; noteTimer.Start();
                int needs0 = WorldMapView.ReceivedForTest.Count(x => x == "need");
                await WmEval("(function(){ var r=[].slice.call(document.querySelectorAll('.wm-row')).filter(function(e){return e.textContent.indexOf('Zone borders')===0})[0]; r.querySelector('input').click(); return 1; })()");
                bool gotBorders = await WmWaitCount("borders", 3, 60000);
                WorldMapService.BuildChanged -= OnB; noteTimer.Stop();
                var bj = Json.Parse(await WmEval("window.wm.borders(0)")) as string; var bd = Json.Obj(bj);
                var hov = Json.Obj(Json.Parse(await WmEval("window.wm.borders(0)")) as string);
                Check("borders: switching Zone borders on reads the terrain of the continent first (the page's row says so; the app's note said \"" + notesSeen.FirstOrDefault(n => n.StartsWith("Zone borders")) + "\"), then the layer arrives: " + (bd?.Int("n") ?? 0) + " zones, " + (bd?.Int("rings") ?? 0) + " rings",
                    gotBorders && WorldMapService.TerrainBuilt(0) && WorldMapView.ReceivedForTest.Count(x => x == "need") == needs0 + 1 && bd != null && bd.Int("n") == 3 && bd.Int("rings") >= 6 && bd.Bool("onMap")
                    && (notesSeen.Any(n => n.StartsWith("Zone borders: reading the terrain")) || texts.Any(t => t.Contains("terrain"))) && WorldMapView.Note == "", bj + " notes " + string.Join("|", notesSeen));
                Check("borders: pointing at a zone tints it (the fill goes from " + hov.Str("before") + " to " + hov.Str("hover") + " and back to " + hov.Str("after") + ") - thin lines, fill only on hover",
                    hov != null && hov.TryGetValue("before", out var b0) && (double)b0 == 0 && (double)hov["hover"] > 0.1 && (double)hov["after"] == 0, Json.Write(hov));
                await WmEval("window.chrome.webview.postMessage({t:'nav', lat:" + nlat.ToString("R", I) + ", lng:" + nlng.ToString("R", I) + "})");
                await WaitFor(() => copied != null, 8000);
                Check("borders: with the terrain read, \"Navigate here\" at the same spot names the zone the chunk really belongs to (Westmark = Greenvale's map, 11) instead of the nearest rectangle centre", copied != null && copied.StartsWith("/etk way 11 "), copied + " (before: " + wayBox + ")");
                copied = null;
                var png2 = await WorldMapView.CaptureForTest();
                if (png2 != null) File.WriteAllBytes(P("worldmap-borders-2d.png"), png2);

                // ---------------------------------------------------------------- the switch to 3D
                await AtlasView.Open("worldmap:0?x=250&y=500&z=4.5&m=2");
                await WaitFor(() => WorldMapView.PageReady, 20000);
                await Task.Delay(400);
                int refused0 = WorldMapView.RefusedForTest.Count;
                WorldMapView.SetMode(true);
                var sw3 = System.Diagnostics.Stopwatch.StartNew();
                bool settled = await WaitFor(() => (WorldMapView.LoadedPage == "3d" && WorldMapView.PageReady && WorldMapView.Is3D) || (WorldMapView.GlFallbackReason != null && WorldMapView.LoadedPage == "2d" && WorldMapView.PageReady), 90000);
                bool gl = WorldMapView.Is3D && WorldMapView.GlOk;
                notes.Add("note 3D page: " + (gl ? "WebGL ok, renderer " + WorldMapView.GlRenderer : "no 3D: " + WorldMapView.GlFallbackReason) + " (" + sw3.ElapsedMilliseconds + " ms to settle)");
                if (!gl)
                {
                    Check("3D: this machine's WebView2 has no WebGL (" + WorldMapView.GlFallbackReason + "): the flat map is shown with a note, the choice is not kept, the segments say 2D",
                        settled && WorldMapView.LoadedPage == "2d" && !WorldMapView.Is3D && WorldMapView.Note.Contains("WebGL") && !settings.WorldMap3d && !WorldMapView.Segment3dChecked, WorldMapView.Note + " " + WorldMapView.GlFallbackReason);
                }
                else
                {
                    // the page is up
                    await WaitFor(() => WorldMapView.LastStats != null, 20000);
                    await Task.Delay(1500);
                    var s3 = await Wm3State();
                    Check("3D: the 3D page opens (the flat map's view handed over, the mode kept in the id), three.js r149 from the app's own copy renders: " + (s3?.Int("meshes") ?? 0) + " tile meshes, " + (s3?.Int("heightsLoaded") ?? 0) + "/" + (s3?.Int("tilesTotal") ?? 0) + " height grids, " + (s3?.Int("textures") ?? 0) + " textures, " + (s3?.Int("drawCalls") ?? 0) + " draw calls; no script errors",
                        s3 != null && s3.Bool("glOk") && s3.Bool("ready") && s3.Int("heightsLoaded") == 32 && s3.Int("meshes") > 4 && s3.Int("tris") > 1000 && (s3.List("errs") ?? new List<object>()).Count == 0 && WorldMapView.CurrentSub.EndsWith("&m=3") && AtlasView.CurrentId.Contains("&m=3") && settings.WorldMap3d && WorldMapView.Segment3dChecked,
                        s3 == null ? "no state" : Json.Write(s3) + " sub " + WorldMapView.CurrentSub);
                    Check("3D: no request left the app's own host (the sandbox of the flat map applies unchanged), the page's CSP is the same one, three.js and its licence are in _page",
                        WorldMapView.BlockedUrlsForTest.All(u => u.Contains("example.com")) && File.ReadAllText(Path.Combine(WorldMapCache.Root, "_page", "view3d.html")).Contains("default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; connect-src 'self'") && !File.ReadAllText(Path.Combine(WorldMapCache.Root, "_page", "view3d.html")).Contains("http://") && !File.ReadAllText(Path.Combine(WorldMapCache.Root, "_page", "view3d.html")).Contains("https://")
                        && File.Exists(Path.Combine(WorldMapCache.Root, "_page", "three.min.js")) && File.ReadAllText(Path.Combine(WorldMapCache.Root, "_page", "LICENSE-three.txt")).Contains("MIT License"), WorldMapView.BlockedForTest + " " + string.Join(", ", WorldMapView.BlockedUrlsForTest));
                    // the camera: the view of the flat map is where the 3D camera looks, at the distance the zoom rule gives
                    var cam = s3.Child("cam");
                    WorldMapMath.WorldToGrid(250, 500, out var gc, out var gr);
                    double dTarget = Math.Sqrt(Math.Pow(cam.Num("tx") - gc * WorldMapMath.TileYards, 2) + Math.Pow(cam.Num("tz") - gr * WorldMapMath.TileYards, 2));
                    Check("3D: the camera looks at the spot the flat map showed (x 250, y 500, " + dTarget.ToString("0.0", I) + " yards off) from " + cam.Num("dist").ToString("0", I) + " yards, the distance of zoom 4.5", dTarget < 5 && Math.Abs(cam.Num("dist") - 106666 / Math.Pow(2, 4.5)) < 5, Json.Write(cam));
                    // markers on the ground: the flight masters sit on the height grid (the page's own grid against the invented landscape the ADTs were made from)
                    var mk = Json.Parse(await WmEval("window.wm3.markers()")) as string; var marks = Json.Parse(mk) as List<object>;
                    double worstG = 0, worstY = 0; int nm = 0;
                    foreach (var o in marks.Cast<Dictionary<string, object>>().Where(o => !((string)o["name"]).StartsWith("point")))
                    {
                        double x = (double)o["x"], z = (double)o["z"], expect = WmZ((32 - z / WorldMapMath.TileYards) * WorldMapMath.TileYards, (32 - x / WorldMapMath.TileYards) * WorldMapMath.TileYards);
                        worstG = Math.Max(worstG, Math.Abs((double)o["ground"] / 1.5 - expect)); worstY = Math.Max(worstY, Math.Abs((double)o["y"] - (double)o["ground"])); nm++;
                    }
                    Check("3D: " + nm + " flight masters stand on the ground: their height is the grid's (x" + "1.5" + " exaggeration) to within " + worstY.ToString("0.000", I) + " yd, and the grid agrees with the landscape the ADTs were made from to " + worstG.ToString("0.00", I) + " yd", nm >= 3 && worstY < 0.01 && worstG < 0.5, worstG + " " + worstY);
                    // zone borders in 3D come from the same file: the layer is on (it was switched on in the flat map), the zone under a point is found
                    WorldMapMath.WorldToLatLng((32 - 30.5) * WorldMapMath.TileYards, (32 - 29.7) * WorldMapMath.TileYards, out var zlat, out var zlng);     // tile col 29.7 / row 30.5: area 101 / 102 (Westmark)
                    var zone = Json.Parse(await WmEval("window.wm3.zoneAt(" + zlat.ToString("R", I) + "," + zlng.ToString("R", I) + ")")) as string;
                    var area = Json.Parse(await WmEval("window.wm3.areaAt(" + zlat.ToString("R", I) + "," + zlng.ToString("R", I) + ")"));
                    int expectArea = WmChunkArea((int)((29.7 - 28) * 16), (int)((30.5 - 30) * 16));
                    var s3b = await Wm3State();
                    Check("3D: the zone borders layer arrived in the 3D page too (" + s3b.Child("counts").Int("borders") + " zones), the zone under a ground point is found (" + zone + ") and the area id read from the tile's grid is " + area + " (the invented landscape says " + expectArea + ")",
                        s3b.Child("counts").Int("borders") == 3 && zone == "Westmark" && Convert.ToInt32(area) == expectArea, zone + " " + area + " " + expectArea);
                    // the other layers reach the 3D page: flight masters, zone names, routes
                    await WmWaitState(st => { var c = st.Child("counts"); return c != null && c.Int("zones") >= 3 && c.Int("fm") >= 3 && c.Int("routes") >= 1; }, 20000);
                    var c3 = (await Wm3State())?.Child("counts");
                    Check("3D: the counts of the layers are the same as in the flat map: 3 zone names, 3 flight masters, the routes (2 pairs), 3 zones with borders", c3 != null && c3.Int("zones") == 3 && c3.Int("fm") == 3 && c3.Int("routes") >= 1 && c3.Int("borders") == 3, c3 == null ? "?" : Json.Write(c3));

                    // a click on a flight master opens its Atlas page; a ground spot is "Navigate here"
                    await WmEval("window.chrome.webview.postMessage({t:'nav', lat:" + nlat.ToString("R", I) + ", lng:" + nlng.ToString("R", I) + "})");
                    bool navd = await WaitFor(() => copied != null, 8000);
                    Check("3D: \"Navigate here\" works from the 3D page with the same message and the same command (/etk way 11 ...)", navd && copied.StartsWith("/etk way 11 "), copied);
                    copied = null;
                    int ref1 = WorldMapView.RefusedForTest.Count;
                    await WmEval("(function(){ var p=window.chrome.webview.postMessage.bind(window.chrome.webview); p({t:'view3',lat:-120,lng:130,dist:1,az:0,tilt:45}); p({t:'prefs',detail:'ultra',exag:2}); p({t:'stats',fps:-4,mem:1}); p({t:'webgl',ok:'maybe'}); p({t:'open',id:'../../x'}); return 1; })()");
                    await WaitFor(() => WorldMapView.RefusedForTest.Count >= ref1 + 5, 8000);
                    Check("3D: junk from the 3D page (a camera 1 yard away, an unknown detail, negative fps, a WebGL flag that is not a flag, a link outside the entity kinds) is refused five times over and does nothing", WorldMapView.RefusedForTest.Count == ref1 + 5 && WorldMapView.Is3D, (WorldMapView.RefusedForTest.Count - ref1) + "");
                    // the settings from the page: detail and height are remembered
                    var pj = "{\"t\":\"prefs\",\"detail\":\"high\",\"exag\":2}";
                    await WmEval("window.chrome.webview.postMessage(" + pj + ")");
                    bool prefsSaved = await WaitFor(() => settings.WorldMap3dDetail == "high" && settings.WorldMapExag == 2, 8000);
                    Check("3D: the page's detail and height buttons are remembered in the settings (high, 2.0x)", prefsSaved, settings.WorldMap3dDetail + " " + settings.WorldMapExag);
                    settings.WorldMap3dDetail = null; settings.WorldMapExag = 0;

                    // the picture: a close view of the invented hills, then FPS and memory of a continuous orbit
                    WorldMapMath.WorldToLatLng(0, 1000, out var hlat, out var hlng);
                    await WmEval("window.wm3.setView(" + hlat.ToString("R", I) + "," + hlng.ToString("R", I) + ",1500,20,35)");
                    await Task.Delay(2500);
                    var png3 = await WorldMapView.CaptureForTest();
                    if (png3 != null) File.WriteAllBytes(P("worldmap-3d-synthetic.png"), png3);
                    Check("screenshot: the 3D page was captured", png3 != null && png3.Length > 5000, (png3?.Length ?? 0) + "");
                    Snapshot(P("worldmap-3d-window.png"));
                    var mem0 = WorldMapView.ProcessMemoryMbForTest(out var mdet0);
                    await WmEval("window.__bench = null; window.wm3.bench(4000).then(function (r) { window.__bench = r; }); 1");
                    await Task.Delay(4600);
                    string benchJson = null; for (int w = 0; w < 40 && benchJson == null; w++) { var br = await WmEval("window.__bench"); if (!string.IsNullOrEmpty(br) && br != "null") benchJson = Json.Parse(br) as string; else await Task.Delay(250); }
                    var bench = benchJson == null ? null : Json.Obj(benchJson);
                    var mem1 = WorldMapView.ProcessMemoryMbForTest(out var mdet1);
                    var s4 = await Wm3State();
                    notes.Add("note 3D page (synthetic continent, 32 tiles, " + (s4.Bool("software") ? "SOFTWARE" : "hardware") + " WebGL \"" + s4.Str("renderer") + "\"): orbit " + (bench?.Num("avg") ?? 0).ToString("0.0", I) + " fps average (longest frame " + (bench?.Num("maxFrameMs") ?? 0).ToString("0", I) + " ms, " + (bench?.Int("slowFrames") ?? 0) + " of " + (bench?.Int("measured") ?? 0) + " frames over 33 ms), " + s4.Int("meshes") + " meshes, " + s4.Int("tris") + " triangles, " + s4.Int("drawCalls") + " draw calls, " + s4.Int("textures") + " textures; web view processes " + mem0 + " MB before, " + mem1 + " MB after (" + mdet1 + "); JS heap " + s4.Int("js") + " MB");
                    Check("3D: a continuous orbit keeps drawing (" + (bench?.Num("avg") ?? 0).ToString("0.0", I) + " fps, ≥ 5 even on software rendering) and the web view stays far under 1.5 GB (" + mem1 + " MB)", bench != null && bench.Num("avg") >= 5 && (mem1 < 0 || mem1 < 1500), Json.Write(bench));

                    // back to the flat map: the view comes back
                    WorldMapMath.WorldToLatLng(-300, 800, out var blat, out var blng);
                    await WmEval("window.wm3.setView(" + blat.ToString("R", I) + "," + blng.ToString("R", I) + ",2400,0,50)");
                    await WaitFor(() => WorldMapView.LastView3 != null && Math.Abs(WorldMapView.LastView3.Dist - 2400) < 1, 8000);
                    await Task.Delay(700);
                    WorldMapView.SetMode(false);
                    bool back2d = await WaitFor(() => WorldMapView.LoadedPage == "2d" && WorldMapView.PageReady && !WorldMapView.Is3D, 60000);
                    await Task.Delay(500);
                    var st2d = await WmState();
                    WorldMapMath.WorldToLatLng(-300, 800, out var elat, out var elng);
                    Check("3D: switching back to 2D hands the view over: the flat map centres on the spot the 3D camera looked at, at the zoom its distance (2400 yards = zoom " + WorldMapView.DistToZoom(2400).ToString("0.00", I) + ") stands for; the id loses the mode; the setting says 2D",
                        back2d && st2d != null && Math.Abs((double)st2d.List("center")[0] - elat) < 0.05 && Math.Abs((double)st2d.List("center")[1] - elng) < 0.05 && Math.Abs(st2d.Num("zoom") - WorldMapView.DistToZoom(2400)) < 0.3 && !AtlasView.CurrentId.Contains("m=3") && !settings.WorldMap3d && WorldMapView.Segment3dChecked == false,
                        Json.Write(st2d) + " " + AtlasView.CurrentId);
                }

                // ---------------------------------------------------------------- no WebGL: the flat map with a note
                WorldMapView.ForceNoGlForTest = true;
                WorldMapView.SetMode(true);
                bool fell = await WaitFor(() => WorldMapView.GlFallbackReason != null && WorldMapView.LoadedPage == "2d" && WorldMapView.PageReady && !WorldMapView.Is3D, 90000);
                await Task.Delay(300);
                WorldMapView.ForceNoGlForTest = false;
                Check("3D fallback: when WebGL is not there (here: switched off for the test) the 3D page says so, the flat map is shown with a note about it, 2D is selected and the setting stays 2D",
                    fell && WorldMapView.Note.Contains("WebGL") && WorldMapView.GlFallbackReason.Contains("test") && !WorldMapView.Segment3dChecked && !settings.WorldMap3d && WorldMapView.WebShown, WorldMapView.Note + " | " + WorldMapView.GlFallbackReason + " | " + WorldMapView.LoadedPage);
                Snapshot(P("worldmap-3d-fallback-window.png"));
                WorldMapProtocol3dChecks(Check);
                WorldMapView.ClipboardSeamForTest = null;
            }
            finally
            {
                WorldMapView.ForceNoGlForTest = false; WorldMapView.ClipboardSeamForTest = null;
                WmAdtCacheDrop(); settings.WorldMapLayers = null; WorldMapService.AdtSourceForTest = savedAdt; settings.WorldMapExag = savedExag; settings.WorldMap3dDetail = savedDetail; settings.WorldMap3d = saved3d;
                MapData.SetForTest(lidx, "wow_classic_beta|1.60.1.70338|0123456789abcdef");
            }
        }
        // the real continents in 3D (FULL run only: the whole terrain and pictures exist): screenshots of mountains, cities and the zone borders, and what they cost
        async Task WorldMapReal3d(MapIndex idx, List<string> notes, CheckFn Check, Func<string, string> P)
        {
            var I = CultureInfo.InvariantCulture;
            bool saved3d = settings.WorldMap3d; var savedLayers = settings.WorldMapLayers; var savedDetail = settings.WorldMap3dDetail; var savedExag = settings.WorldMapExag;
            try
            {
                settings.WorldMap3d = false; settings.WorldMapLayers = new List<string> { "zones", "borders", "fm", "routes" }; settings.WorldMap3dDetail = null; settings.WorldMapExag = 0;
                ShowTab("atlas"); AtlasView.History.Clear();
                // the flat map with the zone borders
                await AtlasView.Open("worldmap:1?x=-1500&y=-2500&z=2.4&m=2");
                await WaitFor(() => WorldMapView.PageReady && WorldMapView.LoadedPage == "2d", 120000);
                await WmEval("(function(){ var r=[].slice.call(document.querySelectorAll('.wm-row')).filter(function(e){return e.textContent.indexOf('Zone borders')===0})[0]; if (r && !r.querySelector('input').checked) r.querySelector('input').click(); return 1; })()");
                bool gotB = await WmWaitCount("borders", 10, 60000);
                await Task.Delay(2500);
                var bj = Json.Obj(Json.Parse(await WmEval("window.wm.borders(null)")) as string);
                var p2 = await WorldMapView.CaptureForTest(); if (p2 != null) File.WriteAllBytes(P("worldmap-borders-2d-real.png"), p2);
                Check("real borders: the flat map shows Kalimdor's zone borders (" + bj?.Int("n") + " zones, " + bj?.Int("rings") + " rings) as thin lines", gotB && bj != null && bj.Int("n") >= 20, bj == null ? "?" : Json.Write(bj));
                // 3D
                WorldMapView.SetMode(true);
                await WaitFor(() => (WorldMapView.Is3D && WorldMapView.LoadedPage == "3d" && WorldMapView.PageReady) || WorldMapView.GlFallbackReason != null, 120000);
                if (!(WorldMapView.Is3D && WorldMapView.GlOk)) { notes.Add("note real 3D: no 3D here (" + WorldMapView.GlFallbackReason + ")"); return; }
                async Task<Dictionary<string, object>> Settle(int maxMs = 25000)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew(); int calm = 0; Dictionary<string, object> st = null; int lastMeshes = -1;
                    while (sw.ElapsedMilliseconds < maxMs)
                    {
                        await Task.Delay(500);
                        st = await Wm3State();
                        if (st != null && st.Int("inFlight") == 0 && st.Int("heightsLoaded") > 700 && st.Int("meshes") == lastMeshes && st.Int("meshes") > 0) { if (++calm >= 3) break; } else calm = 0;
                        lastMeshes = st?.Int("meshes") ?? -1;
                    }
                    return st;
                }
                var views = new[]
                {
                    new { name = "thousand-needles", x = -5000.0, y = -2400.0, dist = 4200.0, az = 205.0, tilt = 24.0 },
                    new { name = "mulgore", x = -1500.0, y = -150.0, dist = 4200.0, az = 160.0, tilt = 28.0 },
                    new { name = "orgrimmar", x = 1700.0, y = -4380.0, dist = 2300.0, az = 15.0, tilt = 33.0 },
                    new { name = "silithus-cliffs", x = -6800.0, y = 1250.0, dist = 5200.0, az = 70.0, tilt = 26.0 },
                    new { name = "razor-hill-close", x = 340.0, y = -4686.0, dist = 500.0, az = 30.0, tilt = 28.0 },
                    new { name = "continent", x = -1500.0, y = -1000.0, dist = 27000.0, az = 0.0, tilt = 55.0 },
                };
                var summaries = new List<string>(); long maxMem = 0; int worstMeshes = 0;
                foreach (var v in views)
                {
                    WorldMapMath.WorldToLatLng(v.x, v.y, out var lat, out var lng);
                    await WmEval("window.wm3.setView(" + lat.ToString("R", I) + "," + lng.ToString("R", I) + "," + v.dist.ToString("R", I) + "," + v.az.ToString("R", I) + "," + v.tilt.ToString("R", I) + ")");
                    var st = await Settle();
                    var png = await WorldMapView.CaptureForTest(); if (png != null) File.WriteAllBytes(P("worldmap-3d-" + v.name + ".png"), png);
                    var mem = WorldMapView.ProcessMemoryMbForTest(out var det); maxMem = Math.Max(maxMem, mem); worstMeshes = Math.Max(worstMeshes, st?.Int("meshes") ?? 0);
                    summaries.Add(v.name + ": " + st?.Int("meshes") + " meshes, " + st?.Int("tris") + " tris, " + st?.Int("textures") + " textures, " + st?.Int("drawCalls") + " calls, " + mem + " MB (" + det + ")");
                }
                // a continuous orbit over Thousand Needles
                WorldMapMath.WorldToLatLng(-5000, -2400, out var blat, out var blng);
                await WmEval("window.wm3.setView(" + blat.ToString("R", I) + "," + blng.ToString("R", I) + ",4200,0,30)");
                await Settle();
                await WmEval("window.__bench = null; window.wm3.bench(6000).then(function (r) { window.__bench = r; }); 1");
                await Task.Delay(6600);
                string bjson = null; for (int w = 0; w < 40 && bjson == null; w++) { var br = await WmEval("window.__bench"); if (!string.IsNullOrEmpty(br) && br != "null") bjson = Json.Parse(br) as string; else await Task.Delay(250); }
                var bench = bjson == null ? null : Json.Obj(bjson);
                var sEnd = await Wm3State(); var memEnd = WorldMapView.ProcessMemoryMbForTest(out var detEnd); maxMem = Math.Max(maxMem, memEnd);
                notes.Add("note real 3D (Kalimdor, " + (sEnd?.Str("renderer") ?? "?") + ", detail medium, height 1.5x): " + string.Join(" | ", summaries));
                notes.Add("note real 3D orbit over Thousand Needles: " + (bench?.Num("avg") ?? 0).ToString("0.0", I) + " fps average, longest frame " + (bench?.Num("maxFrameMs") ?? 0).ToString("0", I) + " ms, " + (bench?.Int("slowFrames") ?? 0) + " of " + (bench?.Int("measured") ?? 0) + " frames over 33 ms; web view processes " + memEnd + " MB at the end (" + detEnd + "), highest " + maxMem + " MB; page JS heap " + sEnd?.Int("js") + " MB; " + sEnd?.Int("textures") + " textures, " + sEnd?.Int("texCache") + " cached picture tiles, " + sEnd?.Int("canvasTex") + " of 512 px");
                Check("real 3D: Kalimdor in 3D (Thousand Needles, Mulgore, Silithus, Orgrimmar, the whole continent) runs without a script error, the orbit holds " + (bench?.Num("avg") ?? 0).ToString("0", I) + " fps and the web view stays under 1.5 GB (highest " + maxMem + " MB)",
                    sEnd != null && (sEnd.List("errs") ?? new List<object>()).Count == 0 && bench != null && bench.Num("avg") >= 20 && (maxMem < 0 || maxMem < 1500) && worstMeshes > 20, sEnd == null ? "?" : Json.Write(sEnd));
                var zname = Json.Parse(await WmEval("window.wm3.zoneAt(" + blat.ToString("R", I) + "," + blng.ToString("R", I) + ")")) as string;
                Check("real 3D: the zone borders are draped on the terrain in 3D and the zone at the Thousand Needles spot is \"" + zname + "\"", !string.IsNullOrEmpty(zname), zname);
                // Eastern Kingdoms too
                await AtlasView.Open("worldmap:0?x=-9000&y=300&z=4&m=3");
                await WaitFor(() => WorldMapView.PageReady && WorldMapView.MapId == 0 && WorldMapView.LoadedPage == "3d", 120000);
                WorldMapMath.WorldToLatLng(-8913, 554, out var slat, out var slng);
                await WmEval("window.wm3.setView(" + slat.ToString("R", I) + "," + slng.ToString("R", I) + ",2600,200,30)");
                var stE = await Settle();
                var pe = await WorldMapView.CaptureForTest(); if (pe != null) File.WriteAllBytes(P("worldmap-3d-stormwind.png"), pe);
                notes.Add("note real 3D (Eastern Kingdoms, Stormwind): " + stE?.Int("meshes") + " meshes, " + stE?.Int("tris") + " tris, " + WorldMapView.ProcessMemoryMbForTest(out var detS) + " MB (" + detS + ")");
            }
            finally { settings.WorldMap3d = saved3d; settings.WorldMapLayers = savedLayers; settings.WorldMap3dDetail = savedDetail; settings.WorldMapExag = savedExag; }
        }

    }
}

namespace ElansAddonHub
{
    static class WmJsonExt
    {
        public static double Num(this System.Collections.Generic.Dictionary<string, object> d, string k) => d != null && d.TryGetValue(k, out var o) && o is double x ? x : 0;
    }
}
