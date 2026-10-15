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
    // --selftest "buildings", the real game files (read-only; skipped when this PC has no Forever beta client): the placements of both continents, the bridge rule against the
    // community's names (offline, in the author's check), the build times and sizes, and the 3D page with the real shapes: Stormwind, Orgrimmar and Lakeshire's bridge, frame rates
    // and memory at Medium detail, Off removes the shapes. Needs the pictures and the terrain of the continents (a few minutes the first time: ELANSHUB_WORLDMAP_FULL=1 builds them in the
    // scratch folder; ELANSHUB_BUILDINGS_CACHE=<folder> keeps and reuses them). Pictures with ELANSHUB_BUILDINGS_SHOTS=<folder>.
    public partial class MainWindow
    {
        async Task<Dictionary<string, object>> Wm3Bld()
        {
            var r = await WorldMapView.EvalForTest("window.wm3 && window.wm3.buildings ? window.wm3.buildings() : null");
            if (string.IsNullOrEmpty(r) || r == "null") return null;
            var s = Json.Parse(r) as string;
            return s == null ? null : Json.Obj(s);
        }

        async Task BuildingsRealChecks(string dir, string work, CheckFn Check, Func<string, string> P, List<string> notes)
        {
            var I = CultureInfo.InvariantCulture;
            var real = Environment.GetEnvironmentVariable("ELANSHUB_WOW");
            if (string.IsNullOrEmpty(real)) real = WowLocator.Find();
            if (string.IsNullOrEmpty(real) || !Directory.Exists(Path.Combine(real, "Data", "data")) || !Directory.Exists(Path.Combine(real, "_classic_beta_")))
            {
                notes.Add("skip real game files: no WoW install with the Forever beta client on this PC");
                return;
            }
            string cacheEnv = Environment.GetEnvironmentVariable("ELANSHUB_BUILDINGS_CACHE"), shotEnv = Environment.GetEnvironmentVariable("ELANSHUB_BUILDINGS_SHOTS");
            bool full = Environment.GetEnvironmentVariable("ELANSHUB_WORLDMAP_FULL") == "1" || !string.IsNullOrEmpty(cacheEnv);
            var rw = Path.Combine(work, "real"); Directory.CreateDirectory(rw);
            var shots = string.IsNullOrEmpty(shotEnv) ? Path.Combine(rw, "shots") : shotEnv; Directory.CreateDirectory(shots);
            string Shot(string n) => Path.Combine(shots, "buildings-" + n + ".png");
            var savedRoot = WorldMapCache.RootOverride; var savedWowRoot = MapData.WowRoot; var savedEnabled = MapData.Enabled; var savedCache = MapData.CacheFile; var savedWdt = WorldMapService.WdtSourceForTest;
            var savedAdt = WorldMapService.AdtSourceForTest; var savedTile = WorldMapService.TileSourceForTest; var savedWr = WorldMapService.WowRoot;
            var savedLayers = settings.WorldMapLayers; var savedMode = settings.WorldMap3dBuildings; var savedDetail = settings.WorldMap3dDetail; bool saved3d = settings.WorldMap3d; var savedExag = settings.WorldMapExag;
            double savedW = Width, savedH = Height;
            try
            {
                WorldMapService.WdtSourceForTest = null; WorldMapService.AdtSourceForTest = null; WorldMapService.TileSourceForTest = null; BuildingsService.SourceForTest = null; BuildingsService.GroundForTest = null;
                WorldMapService.WowRoot = () => real;
                MapData.ResetForTest();
                MapData.WowRoot = () => real; MapData.Enabled = () => true; MapData.CacheFile = Path.Combine(rw, "mapdata.bin");
                WorldMapCache.RootOverride = string.IsNullOrEmpty(cacheEnv) ? Path.Combine(rw, "cache") : cacheEnv;
                BuildingsService.ResetForTest(); WorldMapService.ResetForTest();
                MapData.Start();
                await WaitFor(() => MapData.Status == MapData.State.Ready || MapData.Status == MapData.State.Unavailable, 180000);
                if (!MapData.Ready) { notes.Add("skip real game files: " + MapData.StatusLine + " (nothing was guessed)"); return; }
                var idx = MapData.Index;
                var conts = WorldMapService.Continents(idx);
                var kal = conts.FirstOrDefault(c => c.MapId == 1); var ek = conts.FirstOrDefault(c => c.MapId == 0);
                if (kal == null || ek == null) { notes.Add("skip real game files: no Kalimdor / Eastern Kingdoms world map in the tables"); return; }

                // ---------------------------------------------------------------- the pictures and terrain the 3D page needs (the scratch folder builds them when asked to, or finds them)
                foreach (var c in new[] { ek, kal })
                {
                    var sw0 = System.Diagnostics.Stopwatch.StartNew();
                    if (WorldMapCache.TerrainBuilt(c.MapId) == null || WorldMapCache.Built(c.MapId) == null)
                    {
                        if (!full) { notes.Add("skip real game files: the pictures and terrain of " + c.Name + " are not built (ELANSHUB_WORLDMAP_FULL=1 builds them in the scratch folder, ELANSHUB_BUILDINGS_CACHE=<folder> keeps them)"); return; }
                        bool b1 = await WorldMapService.EnsureBuilt(c.MapId); bool b2 = await WorldMapService.EnsureTerrain(c.MapId);
                        notes.Add("note real game files: " + c.Name + " pictures " + b1 + ", terrain " + b2 + " built in " + sw0.ElapsedMilliseconds / 1000 + " s");
                        if (!b1 || !b2) { Check("real game files: " + c.Name + " pictures and terrain could be built for the 3D page", false); return; }
                    }
                }

                // ---------------------------------------------------------------- the build: bridges first, then everything, with times and sizes
                var perCont = new Dictionary<int, string>();
                var scanOf = new Dictionary<int, BuildingScan>();
                foreach (var c in new[] { ek, kal })
                {
                    BuildingsService.Remove(c.MapId); BuildingsService.ResetForTest();
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    await BuildingsService.Prepare(c.MapId, "bridges");
                    var stB = BuildingsService.StateOf(c.MapId); long scanMs = sw.ElapsedMilliseconds;
                    var scan = BuildingsService.ScanOf(c.MapId);
                    scanOf[c.MapId] = scan;
                    var estB = stB?.Estimate;
                    Check("real " + c.Name + ": the scan finds " + scan?.Placements.Count + " placed buildings of " + scan?.Models.Count + " different shapes in " + scanMs + " ms (" + scan?.Duplicates + " repeats at tile edges dropped) and " + scan?.Bridge.Count(b => b) + " bridges; the card would say: " + (estB == null ? "?" : WorldMapBuildingsHost.CardText(estB, c.Name, 0)),
                        stB != null && stB.Phase == "ask" && scan != null && scan.Placements.Count > 1000 && scan.Models.Count > 300 && scan.NameIndexSkipped == 0 && scan.Models.Values.All(m => !m.RootMissing) && scan.Bridge.Count(b => b) >= 20 && estB != null && estB.Buildings == scan.Bridge.Count(b => b), stB?.Phase + " " + stB?.Error);
                    sw.Restart();
                    await BuildingsService.Start(c.MapId, "bridges");
                    long msB = sw.ElapsedMilliseconds; var stB2 = BuildingsService.StateOf(c.MapId); long bytesB = WorldMapBuildings.SizeBytes(BuildingsService.DirOf(c.MapId));
                    var estAll = WorldMapBuildings.Estimate(scan, "all", BuildingsService.DirOf(c.MapId));
                    sw.Restart();
                    await BuildingsService.Prepare(c.MapId, "all");
                    await BuildingsService.Start(c.MapId, "all");
                    long msA = sw.ElapsedMilliseconds; var stA = BuildingsService.StateOf(c.MapId); long bytesA = WorldMapBuildings.SizeBytes(BuildingsService.DirOf(c.MapId));
                    var man = WorldMapBuildings.ReadManifest(BuildingsService.DirOf(c.MapId));
                    int stubs = scan.Models.Values.Count(m => { try { var b = File.ReadAllBytes(WorldMapBuildings.ModelFile(BuildingsService.DirOf(c.MapId), m.Fdid)); return b.Length < 40 || b[5] == 0; } catch (Exception) { return true; } });
                    notes.Add("note real " + c.Name + ": bridges " + msB + " ms, " + WorldMapCache.SizeText(bytesB) + "; then all buildings " + msA + " ms, " + WorldMapCache.SizeText(bytesA) + " on disk (" + man?.ModelsBuilt + " shapes, " + stubs + " empty), measured " + Math.Round((man?.SecPerGroup ?? 0) * 1000, 2) + " ms and " + Math.Round((man?.BytesPerGroup ?? 0) / 1024.0, 1) + " KB per group; the card had said " + Math.Round(estAll.Minutes * 60) + " s and " + WorldMapCache.SizeText(estAll.Bytes));
                    perCont[c.MapId] = c.Name + ": " + scan.Placements.Count + " buildings, " + scan.Models.Count + " shapes, " + scan.Bridge.Count(b => b) + " bridges; scan " + scanMs + " ms, bridges " + msB + " ms, all " + msA + " ms, " + WorldMapCache.SizeText(bytesA);
                    Check("real " + c.Name + ": the bridges build (" + msB + " ms) and the whole continent after them (" + msA + " ms, " + WorldMapCache.SizeText(bytesA) + "); the manifest says every shape is made and nothing is half-written",
                        stB2?.Phase == "done" && stA?.Phase == "done" && man != null && man.Valid && man.AllDone && man.BridgesDone && man.ModelsBuilt >= scan.Models.Count && !Directory.EnumerateFiles(BuildingsService.DirOf(c.MapId), "*.tmp", SearchOption.AllDirectories).Any() && bytesA > bytesB && bytesA > 2 * 1024 * 1024, stA?.Phase + " " + stA?.Error);
                }
                foreach (var kv in perCont) notes.Add("note real buildings: " + kv.Value);
                // the placement transform against the game's own extents: the box of each building, turned and moved by our rule, is the box the file stores for it
                {
                    var scanE = scanOf[0];
                    using (var casc2 = CascStorage.Open(real, "_classic_beta_"))
                    using (var src2 = new CascBuildingSource(casc2))
                    {
                        var sample = scanE.Placements.Where(p => Math.Abs(p.RotX) > 0.5 || Math.Abs(p.RotZ) > 0.5).Take(150).Concat(scanE.Placements.Where(p => p.RotX == 0 && p.RotZ == 0 && p.RotY != 0).Take(150)).ToList();
                        src2.Prefetch(sample.Select(p => (int)p.NameId).Distinct());
                        var roots2 = new Dictionary<uint, WmoRoot>(); double tot = 0, mxe = 0; int cnt = 0, tilted = 0;
                        foreach (var p in sample)
                        {
                            if (!roots2.TryGetValue(p.NameId, out var r)) roots2[p.NameId] = r = WmoRoot.Parse(src2.Read((int)p.NameId));
                            if (r == null || !r.Ok) continue;
                            double a0 = 1e9, a1 = -1e9, b0 = 1e9, b1 = -1e9, c0 = 1e9, c1 = -1e9;
                            for (int k = 0; k < 8; k++)
                            {
                                WmoTransform.ToScene(p, (k & 1) == 0 ? r.MinX : r.MaxX, (k & 2) == 0 ? r.MinY : r.MaxY, (k & 4) == 0 ? r.MinZ : r.MaxZ, out var x, out var y, out var z);
                                a0 = Math.Min(a0, x); a1 = Math.Max(a1, x); b0 = Math.Min(b0, y); b1 = Math.Max(b1, y); c0 = Math.Min(c0, z); c1 = Math.Max(c1, z);
                            }
                            double e = Math.Abs(a0 - p.MinX) + Math.Abs(a1 - p.MaxX) + Math.Abs(b0 - p.MinY) + Math.Abs(b1 - p.MaxY) + Math.Abs(c0 - p.MinZ) + Math.Abs(c1 - p.MaxZ);
                            tot += e; mxe = Math.Max(mxe, e); cnt++; if (Math.Abs(p.RotX) > 0.5 || Math.Abs(p.RotZ) > 0.5) tilted++;
                        }
                        notes.Add("note real buildings: transform vs the files' own extents over " + cnt + " placements (" + tilted + " tilted): mean error " + Math.Round(tot / Math.Max(1, cnt), 3) + " yards (sum of the six box sides), worst " + Math.Round(mxe, 2));
                        Check("real transform: the building's box turned by Ry Rx Rz, scaled and moved by our rule is the box the game stores for it (" + cnt + " placements, " + tilted + " of them tilted: mean error " + Math.Round(tot / Math.Max(1, cnt), 3) + " yards over the six sides, worst " + Math.Round(mxe, 2) + ") - so nothing is mirrored or turned the wrong way",
                            cnt >= 200 && tilted >= 30 && tot / cnt < 0.5 && mxe < 6);
                    }
                }

                // ---------------------------------------------------------------- the 3D page with the real shapes
                Width = 1500; Height = 950; await Task.Delay(400);
                settings.WorldMap3d = true; settings.WorldMapLayers = new List<string> { "fm" }; settings.WorldMap3dDetail = "medium"; settings.WorldMapExag = 1.5; settings.WorldMap3dBuildings = "all";
                ShowTab("atlas"); AtlasView.History.Clear();
                async Task<bool> OpenAt(int mapId, double x, double y)
                {
                    var c = mapId == 0 ? ek : kal;
                    await AtlasView.Open("worldmap:" + mapId + "?x=" + x.ToString("R", I) + "&y=" + y.ToString("R", I) + "&z=6&m=3");
                    return await WaitFor(() => WorldMapView.PageReady && WorldMapView.LoadedPage == "3d" && WorldMapView.MapId == mapId, 150000);
                }
                async Task<Dictionary<string, object>> SettleB(int maxMs = 30000)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew(); int calm = 0; Dictionary<string, object> b = null; int last = -1;
                    while (sw.ElapsedMilliseconds < maxMs)
                    {
                        await Task.Delay(500);
                        var st = await Wm3State(); b = await Wm3Bld();
                        if (st != null && b != null && st.Int("inFlight") == 0 && st.Int("heightsLoaded") > 600 && b.Int("modelsLoading") == 0 && b.Int("drawn") == last && b.Int("drawn") > 0) { if (++calm >= 3) break; } else calm = 0;
                        last = b?.Int("drawn") ?? -1;
                    }
                    return b;
                }
                async Task View(double x, double y, double dist, double az, double tilt)
                {
                    WorldMapMath.WorldToLatLng(x, y, out var lat, out var lng);
                    await WmEval("window.wm3.setView(" + lat.ToString("R", I) + "," + lng.ToString("R", I) + "," + dist.ToString("R", I) + "," + az.ToString("R", I) + "," + tilt.ToString("R", I) + ")");
                }
                if (!await OpenAt(0, -8913, 554) || !(WorldMapView.Is3D && WorldMapView.GlOk)) { notes.Add("note real 3D buildings: no 3D here (" + WorldMapView.GlFallbackReason + ")"); return; }
                var results = new List<string>();
                var views = new[]
                {
                    new { map = 0, name = "stormwind", x = -8860.0, y = 540.0, dist = 420.0, az = 160.0, tilt = 30.0 },
                    new { map = 0, name = "stormwind-far", x = -8870.0, y = 560.0, dist = 2600.0, az = 200.0, tilt = 42.0 },
                    new { map = 1, name = "orgrimmar", x = 1630.0, y = -4380.0, dist = 700.0, az = 0.0, tilt = 68.0 },
                    new { map = 1, name = "orgrimmar-far", x = 1630.0, y = -4380.0, dist = 2600.0, az = 15.0, tilt = 42.0 },
                    new { map = 0, name = "lakeshire", x = -9347.0, y = -2281.0, dist = 220.0, az = 35.0, tilt = 30.0 },
                };
                int curMap = 0; var fpsBy = new Dictionary<string, double>(); var drawnBy = new Dictionary<string, int>(); Func<string, int> DB = n => drawnBy.TryGetValue(n, out var q) ? q : 0;
                foreach (var v in views)
                {
                    if (v.map != curMap) { if (!await OpenAt(v.map, v.x, v.y)) { Check("real 3D buildings: the page for map " + v.map + " opened", false); continue; } curMap = v.map; }
                    await View(v.x, v.y, v.dist, v.az, v.tilt);
                    var b = await SettleB();
                    await WmEval("window.__bench = null; window.wm3.bench(3500).then(function (r) { window.__bench = r; }); 1"); await Task.Delay(3900);
                    string bjson = null; for (int w = 0; w < 40 && bjson == null; w++) { var br = await WmEval("window.__bench"); if (!string.IsNullOrEmpty(br) && br != "null") bjson = Json.Parse(br) as string; else await Task.Delay(250); }
                    var bench = bjson == null ? null : Json.Obj(bjson);
                    fpsBy[v.name] = bench == null ? 0 : (bench.TryGetValue("avg", out var av) && av is double ad ? ad : 0);
                    var st = await Wm3State(); b = await Wm3Bld() ?? b;
                    await Task.Delay(300);
                    var png = await WorldMapView.CaptureForTest(); if (png != null) File.WriteAllBytes(Shot(v.name), png);
                    var mem = WorldMapView.ProcessMemoryMbForTest(out var det);
                    drawnBy[v.name] = b?.Int("drawn") ?? 0;
                    results.Add(v.name + ": " + b?.Int("drawn") + " buildings drawn (" + string.Join("/", (b?.List("byLod") ?? new List<object>()).Select(o => Convert.ToString(o, I))) + " by level), " + b?.Int("tris") + " triangles, " + (b != null && b.TryGetValue("memMb", out var mmv) ? Convert.ToDouble(mmv, I) : 0) + " MB of shapes, " + st?.Int("drawCalls") + " calls, bench " + Json.Write(bench) + ", WebView2 " + mem + " MB (" + det + ")");
                    notes.Add("note real 3D buildings at Medium: " + results.Last());
                    // the same view with Buildings Off: what the buildings cost in frame rate and memory
                    await WmEval("window.wm3.buildingMode('off')"); await Task.Delay(900); await WmEval("window.wm3.step(4,130)"); await Task.Delay(500);
                    await WmEval("window.__bench = null; window.wm3.bench(3000).then(function (r) { window.__bench = r; }); 1"); await Task.Delay(3400);
                    string bj0 = null; for (int w = 0; w < 40 && bj0 == null; w++) { var br = await WmEval("window.__bench"); if (!string.IsNullOrEmpty(br) && br != "null") bj0 = Json.Parse(br) as string; else await Task.Delay(250); }
                    var bench0 = bj0 == null ? null : Json.Obj(bj0); var mem0 = WorldMapView.ProcessMemoryMbForTest(out var det0);
                    notes.Add("note real 3D buildings, " + v.name + " with Buildings Off: orbit " + (bench0 != null && bench0.TryGetValue("avg", out var a0) && a0 is double a0d ? Math.Round(a0d, 1) : 0) + " fps average, WebView2 " + mem0 + " MB (" + det0 + ") - the buildings add " + (mem - mem0) + " MB");
                    await WmEval("window.wm3.buildingMode('all')"); await Task.Delay(900); await WmEval("window.wm3.step(4,130)");
                }
                Check("real 3D buildings: Stormwind (" + DB("stormwind") + " drawn near, " + DB("stormwind-far") + " far), Orgrimmar (" + DB("orgrimmar") + " / " + DB("orgrimmar-far") + ") and Lakeshire (" + DB("lakeshire") + ") have building shapes on screen",
                    DB("stormwind") > 3 && DB("orgrimmar") > 3 && DB("lakeshire") >= 1);

                // Lakeshire's bridge: the deck is above the water under it
                await OpenAt(0, -9347, -2281);
                await View(-9347, -2281, 220, 35, 30); await SettleB();
                WorldMapMath.WorldToLatLng(-9347, -2281, out var llat, out var llng);
                var bridgesNear = Json.Parse(await WmEval("window.wm3.bridges(" + llat.ToString("R", I) + "," + llng.ToString("R", I) + ",6)")) as string;
                var bl = Json.Parse(bridgesNear) as List<object>;
                var nearest = bl?.OfType<Dictionary<string, object>>().FirstOrDefault();
                bool deckOk = nearest != null && nearest.TryGetValue("water", out var wv) && wv is double wd && (double)nearest["deck"] > wd + 0.5;
                notes.Add("note real 3D buildings: bridges near Lakeshire: " + bridgesNear);
                Check("real 3D buildings: the bridge at Lakeshire (Redridge) has its deck above the water surface in the 3D scene (deck " + (nearest == null ? "?" : Convert.ToDouble(nearest["deck"], I).ToString("0.0", I)) + " vs water " + (nearest == null || !(nearest["water"] is double) ? "none" : Convert.ToDouble(nearest["water"], I).ToString("0.0", I)) + ")", deckOk, bridgesNear);

                // the switch: Bridges only shows fewer than All; Off removes every one
                var bAll = await Wm3Bld();
                await WmEval("window.wm3.buildingMode('bridges')"); await Task.Delay(700); await WmEval("window.wm3.step(3,120)"); await SettleB(8000);
                var bBr = await Wm3Bld();
                await WmEval("window.wm3.buildingMode('off')"); await Task.Delay(500); await WmEval("window.wm3.step(3,120)");
                var bOff = await Wm3Bld();
                Check("real 3D buildings: Bridges only draws " + bBr?.Int("drawn") + " of the " + bAll?.Int("drawn") + " that All buildings draws here, and Off removes them all (" + bOff?.Int("drawn") + " drawn, " + bOff?.Int("visibleMeshes") + " meshes visible)",
                    bAll != null && bBr != null && bOff != null && bBr.Int("drawn") < bAll.Int("drawn") && bBr.Int("drawn") >= 1 && bOff.Int("drawn") == 0 && bOff.Int("visibleMeshes") == 0, Json.Write(bBr) + " | " + Json.Write(bOff));
                var png2 = await WorldMapView.CaptureForTest(); if (png2 != null) File.WriteAllBytes(Shot("lakeshire-off"), png2);
            }
            finally
            {
                Width = savedW; Height = savedH;
                settings.WorldMapLayers = savedLayers; settings.WorldMap3dBuildings = savedMode; settings.WorldMap3dDetail = savedDetail; settings.WorldMap3d = saved3d; settings.WorldMapExag = savedExag;
                try { WorldMapView.Dispose(); } catch (Exception) { }
                WorldMapCache.RootOverride = savedRoot; MapData.WowRoot = savedWowRoot; MapData.Enabled = savedEnabled; MapData.CacheFile = savedCache; WorldMapService.WdtSourceForTest = savedWdt; WorldMapService.AdtSourceForTest = savedAdt;
                WorldMapService.TileSourceForTest = savedTile; WorldMapService.WowRoot = savedWr;
                try { ResetAtlasForTest(); } catch (Exception) { }
            }
        }
    }
}
