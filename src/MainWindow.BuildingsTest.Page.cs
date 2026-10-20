using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Maps;
using ElansAddonHub.Services.WorldMap;

namespace ElansAddonHub
{
    // --selftest "buildings", part 3: the Buildings switch, card and progress line of the 3D page (a real WebView2) on the invented continent with 196 houses in a block, a few towers and a
    // bridge: the states of the switch, the card (inline, in the panel, never a modal), progress and Pause with the map still turning, buildings appearing as their shapes finish,
    // instancing (a handful of meshes for hundreds of buildings), the levels of detail by distance, culling by the Detail setting, the memory cap, Off, a finished set on the next start,
    // junk messages, nothing from the internet.
    public partial class MainWindow
    {
        async Task BuildingsPageChecks(string work, CheckFn Check, List<string> notes, Func<string, string> P)
        {
            var I = CultureInfo.InvariantCulture;
            if (!WebEngine.RuntimeAvailable(out var why)) { notes.Add("note WebView2 runtime not available here (" + why + "): the page checks are skipped"); return; }
            double savedW = Width, savedH = Height;
            Width = 1500; Height = 950; await Task.Delay(400);
            try
            {
                WorldMapCache.RootOverride = Path.Combine(work, "page");
                WorldMapService.ResetForTest(); BuildingsService.ResetForTest();
                var tiles = WmCols.SelectMany(c => WmRows.Select(r => Tuple.Create(c, r))).ToList();
                WorldMapService.WdtSourceForTest = id => BldWdt(tiles);
                WorldMapService.TileSourceForTest = fdid => { Thread.Sleep(12); return WmTile((fdid - 70000) % 64, (fdid - 70000) / 64); };
                WorldMapService.AdtSourceForTest = fdid => { var (c, r) = WmAdtOf(fdid); return WmAdtCached(c, r); };
                var taxi = MapTables.Parse(RawWith(Raw("UiMap", UiMapTable()), Raw("TaxiNodes", WmTaxiNodes()), Raw("TaxiPath", WmTaxiPaths()), Raw("TaxiPathNode", WmTaxiPoints())));
                MapData.SetForTest(new MapIndex(WmAreaSet(taxi)), "wow_classic_beta|1.60.1.70338|0123456789abcdef");
                var world = BldMakeWorld(14);
                BuildingsService.SourceForTest = world.Source; BuildingsService.GroundForTest = id => BldGorge(world);
                settings.WorldMap3d = true; settings.WorldMapLayers = new List<string>(); settings.WorldMap3dDetail = "medium"; settings.WorldMapExag = 1.5; settings.WorldMap3dBuildings = null;
                ShowTab("atlas"); AtlasView.History.Clear();
                double T = 1600.0 / 3;
                // the block of houses: scene (31T + 40 .. 31T + 365, 32T + 40 .. 32T + 365): the game's x (north) and y (west) of its middle
                double hx = -(200.0), hy = T - 200;
                await AtlasView.Open("worldmap:0?x=" + hx.ToString("R", I) + "&y=" + hy.ToString("R", I) + "&z=6&m=3");
                bool up = await WaitFor(() => WorldMapView.PageReady && WorldMapView.LoadedPage == "3d" && WorldMapView.MapId == 0, 120000);
                if (!up || !(WorldMapView.Is3D && WorldMapView.GlOk)) { notes.Add("note buildings page: no 3D here (" + WorldMapView.GlFallbackReason + "): the page checks are skipped"); return; }
                int blocked0 = WorldMapView.BlockedForTest;
                async Task View(double x, double y, double dist, double az, double tilt)
                {
                    WorldMapMath.WorldToLatLng(x, y, out var lat, out var lng);
                    await WmEval("window.wm3.setView(" + lat.ToString("R", I) + "," + lng.ToString("R", I) + "," + dist.ToString("R", I) + "," + az.ToString("R", I) + "," + tilt.ToString("R", I) + ")");
                }
                async Task<Dictionary<string, object>> Bld(int settleMs = 1200)
                {
                    await WmEval("window.wm3.step(4,130)"); await Task.Delay(settleMs); await WmEval("window.wm3.step(4,130)");
                    return await Wm3Bld();
                }
                async Task<Dictionary<string, object>> BldWait(Func<Dictionary<string, object>, bool> cond, int ms = 20000)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew(); Dictionary<string, object> b = null;
                    while (sw.ElapsedMilliseconds < ms) { await WmEval("window.wm3.step(2,130)"); b = await Wm3Bld(); if (b != null && cond(b)) return b; await Task.Delay(150); }
                    return b;
                }
                async Task<string> Dom(string js) => Json.Parse(await WmEval(js)) as string;
                Func<Dictionary<string, object>, string, double> Dbl = (d, k) => d != null && d.TryGetValue(k, out var v) && v is double x ? x : -1;
                int builds0 = BuildingsService.BuildCountForTest;
                await View(hx, hy, 700, 20, 55);

                // ---------------------------------------------------------------- the switch, off by default
                var b0 = await Bld(600);
                var labels = await Dom("[].map.call(document.querySelectorAll('#v3bld button'), function(b){return b.textContent + (b.classList.contains('on') ? '*' : '')}).join('|')");
                Check("switch: the 3D panel has \"Buildings: Off | Bridges only | All buildings\", Off is selected by default (" + labels + "), no card, no building is drawn, nothing was asked of the app",
                    labels == "Off*|Bridges only|All buildings" && b0 != null && b0.Str("mode") == "off" && b0.Int("drawn") == 0 && !(bool)b0["card"] && settings.WorldMap3dBuildings == null && BuildingsService.StateOf(0) == null);

                // ---------------------------------------------------------------- the card: inline, with the estimate
                BuildingsService.ModelDelayMsForTest = 0;
                await WmEval("window.wm3.buildingMode('all')");
                var bAsk = await BldWait(b => (bool)b["card"] && b.Str("cardText").StartsWith("Show buildings"), 30000);
                var est = BuildingsService.StateOf(0)?.Estimate;
                string expectCard = est == null ? "?" : WorldMapBuildingsHost.CardText(est, "Northland", 0);
                var inPanel = await Dom("(function(){var c=document.getElementById('v3bcard'); return JSON.stringify({inPanel: !!c.closest('.wm-panel'), pos: getComputedStyle(c).position, dialogs: document.querySelectorAll('[role=dialog],dialog,.modal').length, hidden: c.hidden})})()");
                var ip = inPanel == null ? null : Json.Obj(inPanel);
                Check("card: switching to All buildings the first time shows an inline card in the panel (not a modal: " + ip?.Str("pos") + " positioned, no dialog) with the estimate from the scan - \"" + bAsk?.Str("cardText") + "\" - and the buttons Build now and Not now; nothing is drawn and nothing was built yet",
                    bAsk != null && bAsk.Str("cardText") == expectCard && bAsk.Str("cardText").Contains("about " + (world.Placements - 1) + " buildings in Northland") && string.Join("|", (bAsk.List("buttons")).Select(o => (string)o)) == "Build now|Not now" && ip != null && ip.Bool("inPanel") && ip.Str("pos") != "fixed" && ip.Str("pos") != "absolute" && ip.Int("dialogs") == 0
                    && bAsk.Int("drawn") == 0 && settings.WorldMap3dBuildings == "all" && !Directory.Exists(BuildingsService.DirOf(0)) && BuildingsService.BuildCountForTest == builds0, (bAsk == null ? "no card" : "expect=" + expectCard + " same=" + (bAsk.Str("cardText") == expectCard) + " ip=" + inPanel + " settings=" + settings.WorldMap3dBuildings + " dir=" + Directory.Exists(BuildingsService.DirOf(0)) + " builds=" + (BuildingsService.BuildCountForTest - builds0) + " " + Json.Write(bAsk)));
                var shot1 = SelfTestClock.ShotsOn ? await WorldMapView.CaptureForTest() : null; if (shot1 != null) File.WriteAllBytes(P("buildings-synthetic-card.png"), shot1);
                await WmEval("window.wm3.buildingClick('later')");
                var bNot = await BldWait(b => b.Str("mode") == "off", 10000);
                Check("card: Not now puts the switch back to Off (saved: " + (settings.WorldMap3dBuildings ?? "off") + "), hides the card and builds nothing", bNot != null && bNot.Str("mode") == "off" && !(bool)bNot["card"] && settings.WorldMap3dBuildings == null && BuildingsService.BuildCountForTest == builds0 && !Directory.Exists(BuildingsService.DirOf(0)));

                // ---------------------------------------------------------------- bridges only
                await WmEval("window.wm3.buildingMode('bridges')");
                var bAskB = await BldWait(b => (bool)b["card"] && b.Str("cardText").StartsWith("Show bridges"), 30000);
                Check("card: Bridges only counts just the bridges (\"" + bAskB?.Str("cardText") + "\")", bAskB != null && bAskB.Str("cardText").Contains("about 1 bridges in Northland") && bAskB.Str("cardText").Contains("less than a minute"), bAskB?.Str("cardText"));
                await View(433.33, 966.67, 160, 40, 40);
                await WmEval("window.wm3.buildingClick('build')");
                var bBr = await BldWait(b => b.Str("phase") == "done" && b.Int("drawn") >= 1, 30000);
                Check("bridges: Build now builds the one bridge in the background and it is drawn (" + bBr?.Int("drawn") + " instance, " + bBr?.Int("meshes") + " mesh, phase " + bBr?.Str("phase") + "); the card is down to one quiet line: \"" + bBr?.Str("cardText") + "\"",
                    bBr != null && bBr.Str("phase") == "done" && bBr.Int("drawn") == 1 && bBr.Int("meshes") == 1 && bBr.Str("cardText").StartsWith("1 bridges") && bBr.Str("cardText").Contains("on disk") && BuildingsService.IsReady(0, "bridges") && bBr.Int("modelsReady") == 1, bBr == null ? "?" : Json.Write(bBr));
                var png2 = SelfTestClock.ShotsOn ? await WorldMapView.CaptureForTest() : null; if (png2 != null) File.WriteAllBytes(P("buildings-synthetic-bridge.png"), png2);

                // ---------------------------------------------------------------- all buildings: progress, the map stays usable, Pause, nearest first
                await View(hx, hy, 700, 20, 55);
                await WmEval("window.wm3.buildingMode('all')");
                var bAsk2 = await BldWait(b => (bool)b["card"] && b.Str("cardText").StartsWith("Show buildings") && b.List("buttons").Count > 0, 30000);
                Check("card: after the bridges, All buildings asks again, with the shape already made counted and the button saying Continue (\"" + bAsk2?.Str("cardText") + "\" " + string.Join("|", bAsk2?.List("buttons").Select(o => (string)o) ?? new string[0]) + ")",
                    bAsk2 != null && bAsk2.Str("cardText").EndsWith("1 of 5 shapes are made already.") && string.Join("|", bAsk2.List("buttons").Select(o => (string)o)) == "Continue|Not now");
                BuildingsService.ModelDelayMsForTest = 500;
                var cardTexts = new List<string>(); var framesSeen = new List<int>(); int drawnWhileBuilding = 0; string firstNew = null;
                var s0 = await Wm3State(); int frames0 = s0?.Int("frames") ?? 0;
                BuildingsService.Focus(0, 31 * T + 200, 32 * T + 200);
                await WmEval("window.wm3.buildingClick('build')");
                var swb = System.Diagnostics.Stopwatch.StartNew(); bool paused = false; int viewMoves = 0; Dictionary<string, object> bNow = null;
                while (swb.ElapsedMilliseconds < 25000)
                {
                    bNow = await Wm3Bld(); var s = await Wm3State();
                    if (bNow == null) { await Task.Delay(100); continue; }
                    var t = bNow.Str("cardText"); if (!string.IsNullOrEmpty(t) && (cardTexts.Count == 0 || cardTexts.Last() != t)) cardTexts.Add(t);
                    if (bNow.Str("phase") == "building") { drawnWhileBuilding = Math.Max(drawnWhileBuilding, bNow.Int("drawn")); framesSeen.Add(s?.Int("frames") ?? 0); await View(hx + 10 * (viewMoves % 5), hy, 700, 20 + 5 * viewMoves, 55); viewMoves++; }
                    if (firstNew == null && Directory.Exists(WorldMapBuildings.ModelsDir(BuildingsService.DirOf(0))) && Directory.GetFiles(WorldMapBuildings.ModelsDir(BuildingsService.DirOf(0)), "*.wmb").Length >= 2) firstNew = Directory.GetFiles(WorldMapBuildings.ModelsDir(BuildingsService.DirOf(0)), "*.wmb").Select(f => Path.GetFileNameWithoutExtension(f)).OrderBy(x => x).FirstOrDefault(x => x != "5000");
                    if (!paused && bNow.Str("phase") == "building" && drawnWhileBuilding > 1) { await WmEval("window.wm3.buildingClick('pause')"); paused = true; break; }
                    if (bNow.Str("phase") == "done") break;
                    await Task.Delay(120);
                }
                var bPaused = await BldWait(b => b.Str("phase") == "paused", 10000);
                string pausedCard = bPaused?.Str("cardText"); string pausedBtns = bPaused == null ? "" : string.Join("|", bPaused.List("buttons").Select(o => (string)o));
                int filesAtPause = Directory.GetFiles(WorldMapBuildings.ModelsDir(BuildingsService.DirOf(0)), "*.wmb").Length;
                await Task.Delay(1500);
                int filesLater = Directory.GetFiles(WorldMapBuildings.ModelsDir(BuildingsService.DirOf(0)), "*.wmb").Length;
                var s1 = await Wm3State();
                Check("progress: while building the card shows \"Building Northland: n / m buildings\" with a Pause and a Cancel button (" + cardTexts.Count + " different texts, e.g. \"" + cardTexts.FirstOrDefault(c => c.StartsWith("Building")) + "\"), the map keeps turning (the page drew " + (framesSeen.Count > 1 ? framesSeen.Last() - framesSeen.First() : 0) + " frames and took " + viewMoves + " camera moves meanwhile) and buildings appear as their shapes finish (" + drawnWhileBuilding + " drawn before it was done)",
                    paused && cardTexts.Count(c => System.Text.RegularExpressions.Regex.IsMatch(c, @"^Building Northland: [\d,]+ / [\d,]+ buildings")) >= 2 && framesSeen.Count > 1 && framesSeen.Last() > framesSeen.First() && viewMoves >= 2 && drawnWhileBuilding > 1, string.Join(" | ", cardTexts) + " moves " + viewMoves + " drawn " + drawnWhileBuilding);
                Check("progress: nearest first - with the camera over the houses the first shape made after the bridge is the house's (" + firstNew + "); Pause answers \"" + pausedCard + "\" with Resume and Cancel (" + pausedBtns + ") and nothing more is made while paused (" + filesAtPause + " -> " + filesLater + " shapes)",
                    firstNew == BldWorld.House.ToString() && pausedCard != null && pausedCard.StartsWith("Paused: ") && pausedBtns == "Resume|Cancel" && filesLater <= filesAtPause + 1, firstNew + " " + pausedCard + " " + pausedBtns + " " + filesAtPause + "/" + filesLater);
                await WmEval("window.wm3.buildingClick('resume')");
                BuildingsService.ModelDelayMsForTest = 0;
                var bDone = await BldWait(b => b.Str("phase") == "done", 40000);
                Check("progress: Resume finishes the job: \"" + bDone?.Str("cardText") + "\" - the card is down to one line again and the manifest says everything is made", bDone != null && bDone.Str("phase") == "done" && bDone.Str("cardText").StartsWith((world.Placements - 1).ToString("N0", I) + " buildings") && BuildingsService.IsReady(0, "all"), bDone == null ? "?" : bDone.Str("cardText"));

                // ---------------------------------------------------------------- instancing and levels of detail
                await View(hx, hy, 800, 20, 62);
                var bNear = await Bld(2500);
                                var shot3 = SelfTestClock.ShotsOn ? await WorldMapView.CaptureForTest() : null; if (shot3 != null) File.WriteAllBytes(P("buildings-synthetic-houses.png"), shot3);
                Check("instancing: " + bNear?.Int("drawn") + " buildings are on screen from " + bNear?.Int("visibleMeshes") + " meshes (one per shape and level, not one per building), " + bNear?.Int("drawCalls") + " draw calls in all (terrain included), " + bNear?.Int("tris") + " triangles",
                    bNear != null && bNear.Int("drawn") >= 100 && bNear.Int("visibleMeshes") >= 1 && bNear.Int("visibleMeshes") <= 8 && bNear.Int("instances") == bNear.Int("drawn") && bNear.Int("meshes") <= 12, bNear == null ? "?" : Json.Write(bNear));
                await View(hx, hy, 130, 20, 45);
                var bClose = await Bld(2500);
                var lods = bClose == null ? new List<int>() : bClose.List("byLod").Select(o => Convert.ToInt32(o)).ToList();
                await View(hx, hy, 3400, 20, 60);
                var bFar = await Bld(2500);
                var lodsFar = bFar == null ? new List<int>() : bFar.List("byLod").Select(o => Convert.ToInt32(o)).ToList();
                Check("levels: close up the buildings are drawn in the full level (by level " + string.Join("/", lods) + "), from far away as boxes (the last level of a small building: " + string.Join("/", lodsFar) + ", " + bFar?.Int("drawn") + " drawn); a far building costs 12 triangles, a near one " + (bClose == null ? 0 : bClose.Int("tris") / Math.Max(1, bClose.Int("drawn"))) + " on average",
                    lods.Count == 3 && lods[0] > 0 && lodsFar.Count == 3 && lodsFar[0] == 0 && lodsFar[1] + lodsFar[2] == bFar.Int("drawn") && bFar.Int("drawn") >= 100 && bFar.Int("tris") <= 12 * bFar.Int("drawn") && bClose.Int("tris") / Math.Max(1, bClose.Int("drawn")) > 12, string.Join("/", lods) + " | " + string.Join("/", lodsFar));

                // ---------------------------------------------------------------- the detail setting: view distance and budget
                await View(hx, hy, 2300, 20, 55);
                async Task<int> DrawnAt(string detail)
                {
                    await WmEval("(function(){ var b = document.querySelector('#v3detail button[data-d=" + detail + "]'); if (b) b.click(); return 1 })()");
                    var b2 = await Bld(900); return b2?.Int("drawn") ?? -1;
                }
                int dLow = await DrawnAt("low"), dMed = await DrawnAt("medium"), dHigh = await DrawnAt("high");
                await DrawnAt("medium");
                Check("detail: the Detail setting is the view distance of the buildings - from 2,300 yards Low draws " + dLow + " (limit 1,800), Medium " + dMed + " (4,000), High " + dHigh + " (8,000)", dLow == 0 && dMed >= 100 && dHigh >= dMed, dLow + "/" + dMed + "/" + dHigh);
                await View(hx, hy, 1400, 20, 55);
                var bTb = await Bld(900);
                await WmEval("(function(){ var b = document.querySelector('#v3detail button[data-d=low]'); b.click(); return 1 })()");
                var bTbLow = await Bld(900);
                await WmEval("(function(){ var b = document.querySelector('#v3detail button[data-d=medium]'); b.click(); return 1 })()");
                Check("detail: Low also has the smaller triangle budget and a nearer level change (" + bTbLow?.Int("tris") + " triangles vs " + bTb?.Int("tris") + " at Medium over the same houses, the full level used by " + (bTbLow == null ? "?" : Convert.ToString(bTbLow.List("byLod")[0])) + " vs " + (bTb == null ? "?" : Convert.ToString(bTb.List("byLod")[0])) + ")",
                    bTb != null && bTbLow != null && Convert.ToInt32(bTbLow.List("byLod")[0]) <= Convert.ToInt32(bTb.List("byLod")[0]) && bTbLow.Int("tris") <= bTb.Int("tris") && bTbLow.Int("tris") <= 120000);

                // ---------------------------------------------------------------- the memory cap
                await View(hx, hy, 700, 20, 55); var bMem0 = await Bld(1200);
                int req0 = bMem0?.Int("requests") ?? 0;
                await WmEval("window.wm3.buildingCap(0.0001)");
                await View(hx + 6000, hy + 6000 > 17000 ? hy - 6000 : hy + 6000, 3000, 0, 50);
                await Task.Delay(3200); var bMem1 = await Bld(300);
                await WmEval("window.wm3.buildingCap(null)");
                await View(hx, hy, 700, 20, 55); var bMem2 = await Bld(2500);
                Check("memory: with the cap far below what the shapes need and the camera elsewhere, the shapes not used for a moment are let go (" + bMem1?.Int("evicted") + " evicted, " + bMem1?.Int("modelsReady") + " left, " + Dbl(bMem1, "memMb") + " MB); back over the houses they are fetched again (" + (bMem2?.Int("requests") - req0) + " requests) and drawn (" + bMem2?.Int("drawn") + ")",
                    bMem0 != null && bMem0.Int("modelsReady") >= 2 && bMem1 != null && bMem1.Int("evicted") >= 2 && bMem1.Int("modelsReady") == 0 && Dbl(bMem1, "memMb") == 0 && bMem2 != null && bMem2.Int("drawn") >= 100 && bMem2.Int("requests") > req0, bMem1 == null ? "?" : Json.Write(bMem1));
                // 2.89: a shape made again while the page shows it (the big cities, once, after the group rule changed) is fetched again when the app announces it, not kept from before
                {
                    int reqR = bMem2?.Int("requests") ?? 0;
                    WorldMapView.PostForTest(WorldMapBuildingsHost.StateJson(0, "all", BuildingsService.StateOf(0), new[] { BldWorld.House }, null));
                    await WmEval("window.wm3.step(4,130)"); var bRe = await Bld(1500);
                    Check("remade shape: when the app announces a shape the page has already drawn (fresh " + BldWorld.House + "), the page fetches it again (" + (bRe?.Int("requests") - reqR) + " request) and keeps drawing (" + bRe?.Int("drawn") + ")",
                        bRe != null && bRe.Int("requests") > reqR && bRe.Int("drawn") >= 100 && bRe.Int("errors") == 0, bRe == null ? "?" : Json.Write(bRe));
                }
                var stN = await Wm3State(); var lastStats = WorldMapView.LastStats;

                // ---------------------------------------------------------------- junk from the page, nothing from the internet
                int refused0 = WorldMapView.RefusedForTest.Count; string modeBefore = (await Wm3Bld())?.Str("mode"); int saves = 0;
                await WmEval("[{t:'buildings',mode:'evil'},{t:'bbuild',mode:'off'},{t:'bstate',mode:'all'},{t:'buildings',mode:7},{t:'bbuild'}].forEach(function(m){ window.chrome.webview.postMessage(m) }); 1");
                await Task.Delay(600);
                Check("junk: five bad building messages from the page are all refused (" + (WorldMapView.RefusedForTest.Count - refused0) + " refusals), the mode and the files are untouched, nothing was fetched from the internet (" + (WorldMapView.BlockedForTest - blocked0) + " blocked requests), the page's own script reports no error",
                    WorldMapView.RefusedForTest.Count - refused0 == 5 && (await Wm3Bld())?.Str("mode") == modeBefore && settings.WorldMap3dBuildings == "all" && WorldMapView.BlockedForTest == blocked0 && stN != null && stN.List("errs").Count == 0, string.Join("|", WorldMapView.RefusedForTest.Skip(refused0)) + " " + (stN == null ? "" : Json.Write(stN.List("errs"))));
                var html = System.Text.Encoding.UTF8.GetString(WorldMapPage.Resource("view3d.html"));
                Check("csp: the 3D page's content security policy is unchanged (default-src 'none', scripts and connections only from the page's own host, no eval, no inline script); the buildings add no new file or host",
                    html.Contains("default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; connect-src 'self'; font-src 'self'; base-uri 'none'; form-action 'none'") && !html.Contains("unsafe-eval") && WorldMapPage.Files.Length == 13);   // 12 since 2.88: trailprints.js (the paw prints); 13 since 3.2: panel.js (the shared side panel) - all from the page's own host
                Check("stats: the page's frame report carries the number of buildings drawn (" + lastStats?.Buildings + ")", lastStats != null && lastStats.Buildings >= 100, lastStats == null ? "no stats" : lastStats.Buildings.ToString());

                // ---------------------------------------------------------------- Off removes them
                await WmEval("[].forEach.call(document.querySelectorAll('#v3bld button'), function(b){ if (b.textContent === 'Off') b.click() }); 1");
                var bOff = await BldWait(b => b.Str("mode") == "off", 10000); await WmEval("window.wm3.step(4,130)"); bOff = await Wm3Bld();
                Check("off: switching Off removes every building from the scene (" + bOff?.Int("drawn") + " drawn, " + bOff?.Int("visibleMeshes") + " meshes shown), hides the card and saves the choice (" + (settings.WorldMap3dBuildings ?? "off") + ")",
                    bOff != null && bOff.Int("drawn") == 0 && bOff.Int("visibleMeshes") == 0 && bOff.Int("instances") == 0 && !(bool)bOff["card"] && settings.WorldMap3dBuildings == null, bOff == null ? "?" : Json.Write(bOff));

                // ---------------------------------------------------------------- the next start: a finished set shows at once, with no question
                settings.WorldMap3dBuildings = "all"; int buildsBefore = BuildingsService.BuildCountForTest;
                WorldMapView.SetMode(false); await WaitFor(() => WorldMapView.LoadedPage == "2d" && WorldMapView.PageReady, 60000);
                WorldMapView.SetMode(true); await WaitFor(() => WorldMapView.LoadedPage == "3d" && WorldMapView.PageReady && WorldMapView.Is3D, 60000);
                await View(hx, hy, 800, 20, 62);
                var bAgain = await BldWait(b => b.Int("drawn") >= 100 && b.Str("phase") == "done", 40000);
                Check("restart: the next time the page starts with Buildings on and everything made, the buildings are there at once (" + bAgain?.Int("drawn") + " drawn, phase " + bAgain?.Str("phase") + ") without a card or a build (" + (BuildingsService.BuildCountForTest - buildsBefore) + " new builds); \"" + bAgain?.Str("cardText") + "\"",
                    bAgain != null && bAgain.Int("drawn") >= 100 && bAgain.Str("phase") == "done" && BuildingsService.BuildCountForTest == buildsBefore && bAgain.Str("cardText").Contains("on disk") && bAgain.List("buttons").Count == 0 && bAgain.Int("built") == 5);
                var shot4 = SelfTestClock.ShotsOn ? await WorldMapView.CaptureForTest() : null; if (shot4 != null) File.WriteAllBytes(P("buildings-synthetic-restart.png"), shot4);
            }
            finally
            {
                BuildingsService.ModelDelayMsForTest = 0; Width = savedW; Height = savedH;
                try { WorldMapView.Dispose(); } catch (Exception) { }
                try { ResetAtlasForTest(); } catch (Exception) { }
                try { WmAdtCacheDrop(); } catch (Exception) { }
            }
        }
    }
}
