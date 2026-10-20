using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Maps;
using ElansAddonHub.Services.WorldMap;

namespace ElansAddonHub
{
    // --selftest "objectsreal" (3.2), the real game files (read-only; skipped when this PC has no Forever beta client): the trees & objects of the Eastern Kingdoms
    // read and built (counts, kinds, texture formats, sizes, time), then the 3D page in Elwynn Forest and Duskwood at each level: what is drawn, the textures, the
    // frame rate, and Off removes them. Needs the continent's pictures and terrain, as buildingsreal (ELANSHUB_WORLDMAP_FULL=1 builds them in the scratch folder,
    // ELANSHUB_BUILDINGS_CACHE=<folder> reuses them). Pictures with ELANSHUB_SHOTS=1 (into the section's folder, or ELANSHUB_OBJECTS_SHOTS=<folder>).
    public partial class MainWindow
    {
        async Task<Dictionary<string, object>> Wm3Obj()
        {
            var r = await WorldMapView.EvalForTest("window.wm3 && window.wm3.objects ? window.wm3.objects() : null");
            if (string.IsNullOrEmpty(r) || r == "null") return null;
            var s = Json.Parse(r) as string;
            return s == null ? null : Json.Obj(s);
        }

        async Task ObjectsRealChecks(string dir, string work, CheckFn Check, Func<string, string> P, List<string> notes)
        {
            var I = CultureInfo.InvariantCulture;
            var real = Environment.GetEnvironmentVariable("ELANSHUB_WOW");
            if (string.IsNullOrEmpty(real)) real = WowLocator.Find();
            if (string.IsNullOrEmpty(real) || !Directory.Exists(Path.Combine(real, "Data", "data")) || !Directory.Exists(Path.Combine(real, "_classic_beta_")))
            {
                notes.Add("skip real game files: no WoW install with the Forever beta client on this PC");
                return;
            }
            string cacheEnv = Environment.GetEnvironmentVariable("ELANSHUB_BUILDINGS_CACHE"), shotEnv = Environment.GetEnvironmentVariable("ELANSHUB_OBJECTS_SHOTS");
            bool full = Environment.GetEnvironmentVariable("ELANSHUB_WORLDMAP_FULL") == "1" || !string.IsNullOrEmpty(cacheEnv);
            var rw = Path.Combine(work, "real"); Directory.CreateDirectory(rw);
            var shots = string.IsNullOrEmpty(shotEnv) ? Path.Combine(rw, "shots") : shotEnv; Directory.CreateDirectory(shots);
            string Shot(string n) => Path.Combine(shots, "objects-" + n + ".png");
            var savedLevel = settings.WorldMap3dObjects; var savedMode = settings.WorldMap3dBuildings;
            try
            {
                WorldMapService.WdtSourceForTest = null; WorldMapService.AdtSourceForTest = null; WorldMapService.TileSourceForTest = null; ObjectsService.SourceForTest = null; BuildingsService.SourceForTest = null;
                WorldMapService.WowRoot = () => real;
                MapData.ResetForTest();
                MapData.WowRoot = () => real; MapData.Enabled = () => true; MapData.CacheFile = Path.Combine(rw, "mapdata.bin");
                WorldMapCache.RootOverride = string.IsNullOrEmpty(cacheEnv) ? Path.Combine(rw, "cache") : cacheEnv;
                ObjectsService.ResetForTest(); BuildingsService.ResetForTest(); WorldMapService.ResetForTest();
                MapData.Start();
                await WaitFor(() => MapData.Status == MapData.State.Ready || MapData.Status == MapData.State.Unavailable, 180000);
                if (!MapData.Ready) { notes.Add("skip real game files: " + MapData.StatusLine + " (nothing was guessed)"); return; }
                var ek = WorldMapService.Continents(MapData.Index).FirstOrDefault(c => c.MapId == 0);
                if (ek == null) { notes.Add("skip real game files: no Eastern Kingdoms world map in the tables"); return; }
                if (WorldMapCache.TerrainBuilt(0) == null || WorldMapCache.Built(0) == null)
                {
                    if (!full) { notes.Add("skip real game files: the pictures and terrain of the Eastern Kingdoms are not built (ELANSHUB_WORLDMAP_FULL=1 builds them in the scratch folder, ELANSHUB_BUILDINGS_CACHE=<folder> keeps them)"); return; }
                    bool b1 = await WorldMapService.EnsureBuilt(0), b2 = await WorldMapService.EnsureTerrain(0);
                    if (!b1 || !b2) { Check("real game files: the Eastern Kingdoms pictures and terrain could be built for the 3D page", false); return; }
                }

                // ---------------------------------------------------------------- the build: the whole continent, with what it costs
                ObjectsService.Remove(0); ObjectsService.ResetForTest();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await ObjectsService.Prepare(0);
                var ask = ObjectsService.StateOf(0); var scan = ObjectsService.ScanOf(0);
                long scanMs = sw.ElapsedMilliseconds;
                string card = ask != null && ask.Phase == "ask" ? WorldMapObjects.CardText(ask) : "";
                notes.Add("note real objects: scan of " + ek.Name + " in " + scanMs + " ms: " + scan?.Tiles + " tiles, " + scan?.Placements.ToString("N0", I) + " placements, " + scan?.Models.Count.ToString("N0", I) + " distinct models, " + scan?.Duplicates + " listed twice, " + scan?.NameIndexSkipped + " by name; card: " + card);
                Check("real objects: the scan of the Eastern Kingdoms finds the trees and objects (" + scan?.Placements.ToString("N0", I) + " placements of " + scan?.Models.Count.ToString("N0", I) + " models in " + scan?.Tiles + " tiles) and asks before reading them",
                    ask != null && ask.Phase == "ask" && scan != null && scan.Placements > 50000 && scan.Models.Count > 1000 && scan.NameIndexSkipped == 0 && card.Contains("Eastern Kingdoms"), ask?.Phase + " " + ask?.Error);
                sw.Restart();
                await ObjectsService.Start(0);
                long buildMs = sw.ElapsedMilliseconds;
                var man = WorldMapObjects.ReadManifest(ObjectsService.DirOf(0));
                var odir = ObjectsService.DirOf(0);
                // what the models and textures are made of
                int drawable = 0, stubs = 0; long mBytes = 0; var kinds = new Dictionary<string, int>(); int tris = 0;
                foreach (var f in Directory.EnumerateFiles(WorldMapObjects.ModelsDir(odir), "*.m2b"))
                {
                    var b = File.ReadAllBytes(f); mBytes += b.Length;
                    if (b.Length <= 8) { stubs++; continue; }
                    drawable++;
                    int jl = BitConverter.ToInt32(b, 4); var meta = Json.Obj(System.Text.Encoding.UTF8.GetString(b, 8, jl));
                    tris += meta.Int("tris");
                    foreach (var bo in meta.List("batches").OfType<Dictionary<string, object>>()) { var k = bo.Str("kind"); kinds.TryGetValue(k, out var n); kinds[k] = n + 1; }
                }
                var fmts = new Dictionary<int, int>(); long tBytes = 0; int tCount = 0;
                foreach (var f in Directory.EnumerateFiles(WorldMapObjects.TexturesDir(odir), "*.mtx")) { var b = File.ReadAllBytes(f); tBytes += b.Length; tCount++; fmts.TryGetValue(b[8], out var n); fmts[b[8]] = n + 1; }
                string Mb(long b) => (b / 1048576.0).ToString("0.0", I) + " MB";
                notes.Add("note real objects: built in " + (buildMs / 1000.0).ToString("0.0", I) + " s (" + (buildMs / Math.Max(1.0, scan.Models.Count)).ToString("0.0", I) + " ms per model): " + drawable + " drawable models + " + stubs + " stubs (" + Mb(mBytes) + ", " + (drawable > 0 ? tris / drawable : 0) + " triangles a model), "
                    + tCount + " textures (" + Mb(tBytes) + "; formats " + string.Join(" ", fmts.OrderBy(kv => kv.Key).Select(kv => (kv.Key == 0 ? "rgba" : kv.Key == 1 ? "dxt1" : kv.Key == 2 ? "dxt1a" : kv.Key == 3 ? "dxt3" : kv.Key == 5 ? "dxt5" : "?" + kv.Key) + ":" + kv.Value)) + "), batches " + string.Join(" ", kinds.Select(kv => kv.Key + "=" + kv.Value))
                    + ", folder " + Mb(WorldMapObjects.SizeBytes(odir)) + "; not readable: " + (man == null ? "?" : ObjectsService.UnreadText(man.Unread)) + "; measured " + (man?.SecPerModel * 1000)?.ToString("0.0", I) + " ms and " + ((man?.BytesPerModel ?? 0) / 1024).ToString("0", I) + " KB a model");
                Check("real objects: the build finishes with a manifest, most models drawable (" + drawable + " of " + scan.Models.Count + ") and their textures packed (" + tCount + ", " + fmts.Where(kv => kv.Key != 0).Sum(kv => kv.Value) + " kept as DXT)",
                    man != null && man.AllDone && drawable >= scan.Models.Count * 0.9 && tCount > 500 && fmts.Where(kv => kv.Key != 0).Sum(kv => kv.Value) >= tCount * 0.8 && ObjectsService.StateOf(0)?.Phase == "done", ObjectsService.StateOf(0)?.Phase + " " + ObjectsService.StateOf(0)?.Error);

                // ---------------------------------------------------------------- the 3D page with the real trees
                Width = 1500; Height = 950; await Task.Delay(400);
                settings.WorldMap3d = true; settings.WorldMapLayers = new List<string>(); settings.WorldMap3dDetail = "medium"; settings.WorldMapExag = 1.0; settings.WorldMap3dBuildings = "off"; settings.WorldMap3dObjects = "medium";
                ShowTab("atlas"); AtlasView.History.Clear();
                await AtlasView.Open("worldmap:0?x=-9460&y=62&z=6&m=3");
                if (!await WaitFor(() => WorldMapView.PageReady && WorldMapView.LoadedPage == "3d" && WorldMapView.MapId == 0, 150000) || !(WorldMapView.Is3D && WorldMapView.GlOk)) { notes.Add("note real objects: no 3D here (" + WorldMapView.GlFallbackReason + ")"); return; }
                async Task View(double x, double y, double dist, double az, double tilt)
                {
                    WorldMapMath.WorldToLatLng(x, y, out var lat, out var lng);
                    await WmEval("window.wm3.setView(" + lat.ToString("R", I) + "," + lng.ToString("R", I) + "," + dist.ToString("R", I) + "," + az.ToString("R", I) + "," + tilt.ToString("R", I) + ")");
                }
                async Task<Dictionary<string, object>> Settle(int maxMs = 40000)
                {
                    var s2 = System.Diagnostics.Stopwatch.StartNew(); int calm = 0; Dictionary<string, object> o = null; int last = -1;
                    while (s2.ElapsedMilliseconds < maxMs)
                    {
                        await Task.Delay(500); await WmEval("window.wm3.step(2,130)");
                        var st = await Wm3State(); o = await Wm3Obj();
                        if (st != null && o != null && st.Int("inFlight") == 0 && o.Int("modelsLoading") == 0 && o.Int("drawn") == last && o.Int("drawn") > 0) { if (++calm >= 3) break; } else calm = 0;
                        last = o?.Int("drawn") ?? -1;
                    }
                    return o;
                }
                async Task<double> Bench(int ms)
                {
                    await WmEval("window.__bench = null; window.wm3.bench(" + ms + ").then(function (r) { window.__bench = r; }); 1"); await Task.Delay(ms + 400);
                    for (int w = 0; w < 40; w++) { var br = await WmEval("window.__bench"); if (!string.IsNullOrEmpty(br) && br != "null") { var b = Json.Obj(Json.Parse(br) as string); return b != null && b.TryGetValue("avg", out var av) && av is double ad ? ad : 0; } await Task.Delay(250); }
                    return 0;
                }
                var o0 = await Wm3Obj();
                Check("real objects page: the page knows the built models (" + o0?.Int("built") + ") and the tiles with placements (" + o0?.Int("tilesListed") + "), and this graphics card can draw them (" + o0?.Str("gpuWhy") + ")",
                    o0 != null && o0.Int("built") > 1000 && o0.Int("tilesListed") > 300 && o0["gpu"] is bool g && g, o0 == null ? "no info" : Json.Write(o0));
                var views = new[]
                {
                    // normal map views: the camera a few hundred yards from the spot it looks at, above the trees (the levels' circles are around that spot)
                    new { name = "goldshire", x = -9460.0, y = 62.0, dist = 380.0, az = 30.0, tilt = 40.0 },
                    new { name = "elwynn-forest", x = -9620.0, y = -380.0, dist = 420.0, az = 300.0, tilt = 38.0 },
                    new { name = "duskwood", x = -10870.0, y = -500.0, dist = 400.0, az = 200.0, tilt = 35.0 },
                    new { name = "duskwood-high", x = -10870.0, y = -500.0, dist = 1100.0, az = 200.0, tilt = 45.0 },
                };
                var res = new Dictionary<string, Dictionary<string, object>>();
                foreach (var v in views)
                {
                    await View(v.x, v.y, v.dist, v.az, v.tilt);
                    foreach (var lv in new[] { "low", "medium", "high" })
                    {
                        await WmEval("window.wm3.objectsLevel('" + lv + "', true)");
                        var o = await Settle();
                        double fps = await Bench(2500);
                        var png = SelfTestClock.ShotsOn || !string.IsNullOrEmpty(shotEnv) ? await WorldMapView.CaptureForTest() : null; if (png != null) File.WriteAllBytes(Shot(v.name + "-" + lv), png);
                        var mem = WorldMapView.ProcessMemoryMbForTest(out var det);
                        res[v.name + "-" + lv] = o;
                        notes.Add("note real objects at " + lv + ", " + v.name + ": " + o?.Int("drawn") + " drawn of " + o?.Int("inRange") + " in range, " + o?.Int("tris") + " triangles, " + o?.Int("modelsReady") + " models, " + o?.Int("texLoaded") + " textures, "
                            + o?.Int("materials") + " materials, " + (o != null && o.TryGetValue("memMb", out var mm) ? Convert.ToDouble(mm, I) : 0) + " MB, " + o?.Int("drawCalls") + " calls, " + Math.Round(fps, 1) + " fps orbiting, WebView2 " + mem + " MB, errors " + o?.Int("errors"));
                    }
                    await WmEval("window.wm3.objectsLevel('off', true)"); await Task.Delay(500); await WmEval("window.wm3.step(3,130)");
                    double fps0 = await Bench(2500);
                    var pngOff = SelfTestClock.ShotsOn || !string.IsNullOrEmpty(shotEnv) ? await WorldMapView.CaptureForTest() : null; if (pngOff != null) File.WriteAllBytes(Shot(v.name + "-off"), pngOff);
                    var oOff = await Wm3Obj();
                    notes.Add("note real objects off, " + v.name + ": " + Math.Round(fps0, 1) + " fps orbiting, " + oOff?.Int("drawn") + " drawn, " + oOff?.Int("drawCalls") + " calls");
                    res[v.name + "-off"] = oOff;
                }
                // ---- the real ground (3.2): made on demand for the tiles near the spot you look at; Westfall's open fields at Medium
                {
                    var swg = System.Diagnostics.Stopwatch.StartNew(); GroundService.ResetForTest();
                    bool made = GroundService.MakeTile(0, 30, 50); long tileMs = swg.ElapsedMilliseconds;
                    var gdir = Path.Combine(WorldMapObjects.Dir(WorldMapCache.MapDir(0)), "g");
                    long tileBytes = File.Exists(WorldMapGround.TileFile(WorldMapObjects.Dir(WorldMapCache.MapDir(0)), 30, 50)) ? new FileInfo(WorldMapGround.TileFile(WorldMapObjects.Dir(WorldMapCache.MapDir(0)), 30, 50)).Length : 0;
                    await WmEval("window.wm3.objectsLevel('medium', true)");
                    await View(-10650, 1050, 160, 90, 28);
                    var og = await Settle();
                    for (int w = 0; w < 30; w++) { var gi = Json.Obj(Json.Parse(await WmEval("window.wm3.groundState()")) as string ?? "{}"); if (gi != null && gi.Int("shown") >= 2) break; await Task.Delay(500); await WmEval("window.wm3.step(2,130)"); }
                    var gd = Json.Obj(Json.Parse(await WmEval("window.wm3.groundState()")) as string ?? "{}");
                    var pngG = SelfTestClock.ShotsOn || !string.IsNullOrEmpty(shotEnv) ? await WorldMapView.CaptureForTest() : null; if (pngG != null) File.WriteAllBytes(Shot("westfall-ground"), pngG);
                    long onDisk = Directory.Exists(gdir) ? Directory.EnumerateFiles(gdir).Sum(f => new FileInfo(f).Length) : 0;
                    var gtdir = Path.Combine(WorldMapObjects.Dir(WorldMapCache.MapDir(0)), "gt");
                    long texDisk = Directory.Exists(gtdir) ? Directory.EnumerateFiles(gtdir).Sum(f => new FileInfo(f).Length) : 0;
                    notes.Add("note real ground: one tile and its textures made in " + tileMs + " ms (" + (tileBytes / 1024) + " KB for the tile); the page at Westfall: " + Json.Write(gd) + "; on disk now: tiles " + (onDisk / 1024) + " KB, textures " + (texDisk / 1024) + " KB");
                    Check("real ground: a tile is made from the game files on demand (" + tileMs + " ms, " + (tileBytes / 1024) + " KB) and the page shows the real ground near the spot it looks at (" + gd?.Int("shown") + " tiles, " + gd?.Int("layers") + " textures, " + gd?.Int("errors") + " errors)",
                        made && tileBytes > 1000 && gd != null && gd.Int("shown") >= 2 && gd.Int("layers") >= 4 && gd.Int("errors") == 0);
                    await WmEval("window.wm3.objectsLevel('low', true)"); await Task.Delay(400); await WmEval("window.wm3.step(3,130)");
                    var gLow = Json.Obj(Json.Parse(await WmEval("window.wm3.groundState()")) as string ?? "{}");
                    Check("real ground: Low shows no real ground (the minimap only; " + gLow?.Int("shown") + " tiles)", gLow != null && gLow.Int("shown") == 0);
                }
                // ---- the cities (3.2): Stormwind's detailed model with its materials, and its furnishings, on the page at Medium with the buildings on
                {
                    notes.Add("note real cities: " + scan.Furnishings.ToString("N0", I) + " furnishings of the buildings in the scan");
                    BuildingsService.ResetForTest(); await BuildingsService.Prepare(0, "all"); var bt = BuildingsService.Start(0, "all"); if (bt != null) await bt;
                    var sws = System.Diagnostics.Stopwatch.StartNew(); bool swOk = GroundService.MakeBuilding(0, 7962574); long swMs = sws.ElapsedMilliseconds;
                    var swFile = WmoDetail.ModelFile(WorldMapObjects.Dir(WorldMapCache.MapDir(0)), 7962574);
                    Dictionary<string, object> swMeta = null;
                    if (File.Exists(swFile)) { var b = File.ReadAllBytes(swFile); if (b.Length > 8) swMeta = Json.Obj(System.Text.Encoding.UTF8.GetString(b, 8, BitConverter.ToInt32(b, 4))); }
                    int swMats = swMeta?.List("batches").Count ?? 0, swTex = swMeta?.List("textures").Count ?? 0;
                    notes.Add("note real cities: Stormwind's detailed model made in " + swMs + " ms: " + swMeta?.Int("tris") + " triangles, " + swMats + " materials, " + swTex + " textures, " + (File.Exists(swFile) ? new FileInfo(swFile).Length / 1024 : 0) + " KB");
                    Check("real cities: Stormwind's detailed model has its many materials and real textures (" + swMats + " materials, " + swTex + " textures, " + swMeta?.Int("tris") + " triangles) and the scan finds the buildings' furnishings (" + scan.Furnishings.ToString("N0", I) + ")",
                        swOk && swMats > 50 && swTex > 50 && swMeta.Int("tris") > 100000 && scan.Furnishings > 10000);
                    await WmEval("window.wm3.buildingMode('all'); window.wm3.objectsLevel('medium', true)");
                    await View(-8860, 600, 500, 160, 35);
                    for (int w = 0; w < 40; w++) { var oc = await Wm3Obj(); if (oc != null && oc.TryGetValue("cities", out var cv) && cv is Dictionary<string, object> cd && cd.Int("shown") >= 1) break; await Task.Delay(500); await WmEval("window.wm3.step(2,130)"); }
                    var oCity = await Settle();
                    var cities = oCity != null && oCity.TryGetValue("cities", out var cv2) ? cv2 as Dictionary<string, object> : null;
                    var pngC = SelfTestClock.ShotsOn || !string.IsNullOrEmpty(shotEnv) ? await WorldMapView.CaptureForTest() : null; if (pngC != null) File.WriteAllBytes(Shot("stormwind"), pngC);
                    var errs = Json.Obj(Json.Parse(await WmEval("window.wm3.state()")) as string ?? "{}")?.List("errs");
                    Check("real cities: the page shows Stormwind in detail near the spot it looks at (" + cities?.Int("shown") + " buildings, " + cities?.Int("tris") + " triangles) with no page errors (" + (errs?.Count ?? -1) + ")",
                        cities != null && cities.Int("shown") >= 1 && cities.Int("tris") > 100000 && errs != null && errs.Count == 0, Json.Write(errs));
                    await WmEval("window.wm3.buildingMode('off')");
                }
                int D(string k) => res.TryGetValue(k, out var o) && o != null ? o.Int("drawn") : -1;
                Check("real objects page: Elwynn (" + D("goldshire-medium") + " near Goldshire, " + D("elwynn-forest-medium") + " in the forest) and Duskwood (" + D("duskwood-medium") + ") have trees and objects drawn at Medium, with textures and no read errors",
                    D("goldshire-medium") > 50 && D("elwynn-forest-medium") > 100 && D("duskwood-medium") > 100 && res.Values.Where(o => o != null).All(o => o.Int("errors") == 0) && res["duskwood-medium"].Int("texLoaded") > 20);
                Check("real objects page: a higher level draws more (Duskwood low " + D("duskwood-low") + " < medium " + D("duskwood-medium") + " < high " + D("duskwood-high") + "), every level draws from a normal map height (from 1,100 yards: low " + D("duskwood-high-low") + ", medium " + D("duskwood-high-medium") + ", high " + D("duskwood-high-high") + "), and Off draws none (" + D("duskwood-off") + ")",
                    D("duskwood-low") > 0 && D("duskwood-low") < D("duskwood-medium") && D("duskwood-medium") < D("duskwood-high") && D("duskwood-high-low") > 0 && D("duskwood-high-medium") > D("duskwood-high-low") && D("duskwood-off") == 0 && D("goldshire-off") == 0);
            }
            finally
            {
                settings.WorldMap3dObjects = savedLevel; settings.WorldMap3dBuildings = savedMode;
                ObjectsService.CancelAll(); ObjectsService.ResetForTest();
            }
        }
    }
}
