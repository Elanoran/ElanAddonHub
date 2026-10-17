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
        // after a layer was switched off and on in the 3D page, the page asks the app ("need") and the app's answer lands a moment later: wait for the ask (at most `maxMs`, the old fixed wait), then a short settle for the answer
        async Task WmLayerAsked(int needsBefore, int asks, int maxMs)
        {
            await WaitFor(() => WorldMapView.ReceivedForTest.Count(x => x == "need") >= needsBefore + asks, maxMs);
            await Task.Delay(400);
        }

        async Task<bool> Wm3WaitState(Func<Dictionary<string, object>, bool> cond, int ms = 20000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                try { var st = await Wm3State(); if (st != null && cond(st)) return true; } catch (Exception) { }
                await Task.Delay(120);
            }
            return false;
        }

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
            var pf = WorldMapProtocol.Parse("{\"t\":\"prefs\",\"detail\":\"high\",\"exag\":1.3}", out _);
            Check("protocol 3D: webgl {ok, why, renderer}, view3 {lat, lng, dist, az, tilt}, stats {fps, mem, tris, tiles, tex} and prefs {detail, exag} parse; control characters are removed from the page's text",
                gl != null && gl.Type == WorldMapMessageType.Webgl && !gl.Ok && gl.Why == "no GPU" && gl.Detail == "ANGLE (x)" && v3 != null && v3.Type == WorldMapMessageType.View3 && v3.Dist == 2400 && v3.Az == 370 && v3.Tilt == 45
                && st != null && st.Fps == 59.5 && st.MemMb == 310 && st.Tris == 412345 && st.Tiles == 88 && st.Textures == 71 && pf != null && pf.Detail == "high" && pf.Exag == 1.3, gl?.Why);
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
                if (SelfTestClock.ShotsOn) { var png2 = await WorldMapView.CaptureForTest(); if (png2 != null) File.WriteAllBytes(P("worldmap-borders-2d.png"), png2); }

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
                    await FlightLearnPage3d(Check, P);      // 2.88: the learned flight path in 3D (MainWindow.FlightLearnTest.Page.cs)
                    // the kind filters (Herbs, Ore ...) hide their points in 3D too, and a layer switched on with every kind off shows all kinds
                    await WmEval("window.wm3.setOnForTest('nodes', true); window.wm3.setOnForTest('nodes.herbs', true); window.wm3.setOnForTest('nodes.ore', true)");
                    // the app answers the layer switch with its own node data: wait for it, so the fed test layer is not replaced afterwards
                    for (int w = 0; w < 150; w++) { var c = await WmEval("(function(){var s=JSON.parse(window.wm3.state()); return s.counts && s.counts.nodes !== undefined ? 1 : 0})()"); if (c == "1") break; await Task.Delay(200); }
                    var feedRaw = await WmEval("(function(){try{return window.wm3.feedForTest({t:'layer', id:'nodes', kind:'points', pts:[0,0,0,-1,0,-1, 1,1,0,-1,1,-1, 2,2,0,-1,1,-1], names:['n'], styles:[{c:'#4ade80',s:'circle',f:true,l:'Herb',g:'nodes.herbs'},{c:'#d08a4e',s:'square',f:true,l:'Ore',g:'nodes.ore'}]})}catch(e){return 'ERR '+e.message}})()");
                    var bothRaw = await WmEval("(function(){try{return String(window.wm3.shownPoints('nodes'))}catch(e){return 'ERR '+e.message}})()"); int.TryParse((bothRaw ?? "").Trim('"'), out var both);
                    await WmEval("window.wm3.setOnForTest('nodes.ore', false)");
                    int herbsOnly = Convert.ToInt32(await WmEval("window.wm3.shownPoints('nodes')"));
                    int needs167 = WorldMapView.ReceivedForTest.Count(x => x == "need");
                    await WmEval("['nodes.herbs','nodes.ore','nodes.chests','nodes.fishing'].forEach(function(k){ window.wm3.setOnForTest(k, false) }); window.wm3.setOnForTest('nodes', false); window.wm3.setOnForTest('nodes', true)");
                    await WmLayerAsked(needs167, 1, 2500);   // the switch asks the app for the layer again: let that answer land, then feed the test layer once more
                    await WmEval("window.wm3.feedForTest({t:'layer', id:'nodes', kind:'points', pts:[0,0,0,-1,0,-1, 1,1,0,-1,1,-1, 2,2,0,-1,1,-1], names:['n'], styles:[{c:'#4ade80',s:'circle',f:true,l:'Herb',g:'nodes.herbs'},{c:'#d08a4e',s:'square',f:true,l:'Ore',g:'nodes.ore'}]})");
                    int afterReopen = Convert.ToInt32(await WmEval("window.wm3.shownPoints('nodes')"));
                    Check("3D: the node kind filters hide their own points (3 shown, Ore off = 1 herb, all kinds off then the layer on again = all 3)", both == 3 && herbsOnly == 1 && afterReopen == 3, both + " / " + herbsOnly + " / " + afterReopen + " feed " + feedRaw + " raw " + bothRaw);
                    // dungeon and raid entrances in 3D: markers that carry a kind group (Dungeons / Raids), the child rows switch them one by one, a layer switched on with every kind off shows all kinds,
                    // a right click offers "Open its page" and "Navigate here" (which sends the spot to the app)
                    await WmEval("window.wm3.setOnForTest('instances', true); window.wm3.setOnForTest('instances.dungeons', true); window.wm3.setOnForTest('instances.raids', true)");
                    for (int w = 0; w < 100; w++) { var c = await WmEval("(function(){var s=JSON.parse(window.wm3.state()); return s.counts && s.counts.instances !== undefined ? 1 : 0})()"); if (c == "1") break; await Task.Delay(100); }
                    const string feedIns = "window.wm3.feedForTest({t:'layer', id:'instances', kind:'markers', items:[{lat:-124,lng:130,name:'Crypt of Bones',ic:'dun',g:'instances.dungeons',lab:'Crypt of Bones · 17',tip:'Dungeon · 5 players · level 17',extra:'Greenvale · click to open its page',link:'instance:99'},{lat:-125,lng:131,name:'Old Mine',ic:'dun',g:'instances.dungeons',lab:'Old Mine',tip:'Dungeon',extra:'',link:'instance:97'},{lat:-126,lng:129,name:'Deep Hall',ic:'raid',g:'instances.raids',lab:'Deep Hall · 60',tip:'Raid · 40 players · level 60',extra:'',link:'instance:98'}]})";
                    async Task<List<Dictionary<string, object>>> Ins(string fire = null)
                    {
                        var raw = await WmEval("(function(){try{return window.wm3.instances(" + (fire ?? "") + ")}catch(e){return 'ERR '+e.message}})()");
                        var js = Json.Parse(raw) as string;
                        var arr = js == null ? null : Json.Parse(js) as List<object>;
                        return arr == null ? new List<Dictionary<string, object>>() : arr.OfType<Dictionary<string, object>>().ToList();
                    }
                    await WmEval(feedIns);
                    var insAll = await Ins();
                    await WmEval("window.wm3.setOnForTest('instances.raids', false)");
                    var insNoRaid = await Ins();
                    await WmEval("window.wm3.setOnForTest('instances.raids', true); window.wm3.setOnForTest('instances.dungeons', false)");
                    var insRaidOnly = await Ins();
                    int needs190 = WorldMapView.ReceivedForTest.Count(x => x == "need");
                    await WmEval("['instances.dungeons','instances.raids'].forEach(function(k){ window.wm3.setOnForTest(k, false) }); window.wm3.setOnForTest('instances', false); window.wm3.setOnForTest('instances', true)");
                    await WmLayerAsked(needs190, 1, 1500);
                    await WmEval(feedIns);
                    var insReopen = await Ins();
                    int Wanted(List<Dictionary<string, object>> l) => l.Count(x => x.TryGetValue("wanted", out var v) && v is bool b && b);
                    Check("3D: the dungeon and raid markers follow their kind rows (3 wanted, Raids off = 2 dungeons, Dungeons off = the raid, every kind off then the layer on again = all 3)",
                        insAll.Count == 3 && Wanted(insAll) == 3 && Wanted(insNoRaid) == 2 && insNoRaid.Where(x => (string)x["g"] == "instances.raids").All(x => !(bool)x["wanted"]) && Wanted(insRaidOnly) == 1 && (string)insRaidOnly.First(x => (bool)x["wanted"])["name"] == "Deep Hall" && Wanted(insReopen) == 3,
                        Wanted(insAll) + " / " + Wanted(insNoRaid) + " / " + Wanted(insRaidOnly) + " / " + Wanted(insReopen));
                    Check("3D: a dungeon is a door, a raid a skull badge, each with its label; the markers stand on the ground",
                        insAll.Count == 3 && ((string)insAll[0]["html"]).Contains("class=\"inst dun\"") && ((string)insAll[2]["html"]).Contains("class=\"inst raid\"") && ((string)insAll[0]["html"]).Contains("Crypt of Bones · 17") && ((string)insAll[2]["html"]).Contains("Deep Hall · 60")
                        && insAll.All(x => Math.Abs(Convert.ToDouble(x["y"]) - Convert.ToDouble(x["ground"])) < 1.0 || Convert.ToDouble(x["y"]) == 0), string.Join(" | ", insAll.Select(x => x["name"] + " y" + x["y"] + " g" + x["ground"])));
                    var navBefore = WorldMapView.ReceivedForTest.Count(x => x == "nav");
                    await Ins("[0,'contextmenu']");
                    var menuItems = await WmEval("Array.prototype.map.call(document.querySelectorAll('#ctx button'), function(b){return b.textContent}).join('|')");
                    await WmEval("(function(){var b = Array.prototype.filter.call(document.querySelectorAll('#ctx button'), function(b){return b.textContent==='Navigate here'})[0]; if (b) b.click(); return 1})()");
                    bool navSent = await WaitFor(() => WorldMapView.ReceivedForTest.Count(x => x == "nav") > navBefore, 5000);
                    Check("3D: a right click on an entrance offers \"Open its page\" and \"Navigate here\"; Navigate here sends the spot to the app, which copies the /etk way command",
                        (menuItems ?? "").Contains("Open its page") && (menuItems ?? "").Contains("Navigate here") && navSent && (WorldMapView.LastCommandForTest ?? "").StartsWith("/etk way"), menuItems + " " + WorldMapView.LastCommandForTest);
                    // flight masters and routes by faction in 3D: the kind rows hide markers (per marker) and route lines (per line)
                    await WmEval("['fm','fm.horde','fm.alliance','fm.neutral','routes','routes.horde','routes.alliance','routes.neutral'].forEach(function(k){ window.wm3.setOnForTest(k, true) })");
                    for (int w = 0; w < 100; w++) { var c = await WmEval("(function(){var s=JSON.parse(window.wm3.state()); return s.counts && s.counts.fm !== undefined && s.counts.routes !== undefined ? 1 : 0})()"); if (c == "1") break; await Task.Delay(100); }
                    const string feedFm = "window.wm3.feedForTest({t:'layer', id:'fm', kind:'markers', items:[{lat:-124,lng:130,name:'H1',f:'h',g:'fm.horde'},{lat:-125,lng:131,name:'A1',f:'a',g:'fm.alliance'},{lat:-126,lng:129,name:'N1',f:'n',g:'fm.neutral'},{lat:-127,lng:128,name:'H2',f:'h',g:'fm.horde'}]})";
                    const string feedRt = "window.wm3.feedForTest({t:'layer', id:'routes', kind:'lines', items:[{pts:[-124,130,-125,131],c:'#ff6b5e',name:'r1',g:'routes.horde'},{pts:[-125,131,-126,129],c:'#5aa9ff',name:'r2',g:'routes.alliance'},{pts:[-126,129,-127,128],c:'#f0b429',name:'r3',g:'routes.neutral'}]})";
                    await WmEval(feedFm); await WmEval(feedRt);
                    async Task<List<Dictionary<string, object>>> L3(string fn, string arg)
                    {
                        var raw = await WmEval("(function(){try{return window.wm3." + fn + "(" + arg + ")}catch(e){return 'ERR '+e.message}})()");
                        var js = Json.Parse(raw) as string; var arr = js == null ? null : Json.Parse(js) as List<object>;
                        return arr == null ? new List<Dictionary<string, object>>() : arr.OfType<Dictionary<string, object>>().ToList();
                    }
                    int FmWanted(List<Dictionary<string, object>> l) => l.Count(x => ((string)x["g"]).StartsWith("fm.") && (bool)x["wanted"]);
                    int LinesShown(List<Dictionary<string, object>> l) => l.Count(x => (bool)x["shown"]);
                    var fm3All = await L3("instances", "null,'fm.'"); var rt3All = await L3("lines", "'routes'");
                    await WmEval("window.wm3.setOnForTest('fm.horde', false); window.wm3.setOnForTest('routes.alliance', false)");
                    var fm3NoH = await L3("instances", "null,'fm.'"); var rt3NoA = await L3("lines", "'routes'");
                    int needs225 = WorldMapView.ReceivedForTest.Count(x => x == "need");
                    await WmEval("['fm.horde','fm.alliance','fm.neutral','routes.horde','routes.alliance','routes.neutral'].forEach(function(k){ window.wm3.setOnForTest(k, false) }); window.wm3.setOnForTest('fm', false); window.wm3.setOnForTest('fm', true); window.wm3.setOnForTest('routes', false); window.wm3.setOnForTest('routes', true)");
                    await WmLayerAsked(needs225, 2, 1500);
                    await WmEval(feedFm); await WmEval(feedRt);
                    var fm3Re = await L3("instances", "null,'fm.'"); var rt3Re = await L3("lines", "'routes'");
                    // the emblems in 3D: the same SVG badges as DOM overlays; a fixture picture of the three close to the camera
                    await WmEval("(function(){ var s=JSON.parse(window.wm3.state()); var c=window.wm3.xz2ll(s.tx, s.tz); function it(dx, f, nm){ return {lat:c[0], lng:c[1]+dx, name:nm, f:f, g:'fm.'+(f==='a'?'alliance':f==='h'?'horde':'neutral'), tip:'Flight master', extra:''}; } window.wm3.feedForTest({t:'layer', id:'fm', kind:'markers', items:[it(-0.5,'a','Alliance'), it(0,'h','Horde'), it(0.5,'n','Neutral')]}); return 1; })()");
                    await WmEval("['fm.horde','fm.alliance','fm.neutral'].forEach(function(k){ window.wm3.setOnForTest(k, true) })");
                    // the three emblems arrive as DOM overlays: wait for them instead of a fixed 2.5 s
                    bool Emb3Ok(List<Dictionary<string, object>> e) => e.Count == 3 && e.All(x => ((string)x["html"]).Contains("fmsvg")) && ((string)e.First(x => (string)x["name"] == "Alliance")["html"]).Contains("fmv a");
                    var emb3 = await L3("instances", "null,'fm.'");
                    for (var embClock = System.Diagnostics.Stopwatch.StartNew(); !Emb3Ok(emb3) && embClock.ElapsedMilliseconds < 8000;) { await Task.Delay(100); emb3 = await L3("instances", "null,'fm.'"); }
                    byte[] png3E = null;
                    if (SelfTestClock.ShotsOn) { await Task.Delay(1000); png3E = await WorldMapView.CaptureForTest(); if (png3E != null) File.WriteAllBytes(P("instances-fm-icons-3d.png"), png3E); }
                    Check("3D: the flight masters are the same SVG emblems (shield / spiked ring / coin) as DOM overlays" + (SelfTestClock.ShotsOn ? "; the fixture picture is taken (instances-fm-icons-3d.png)" : ""), Emb3Ok(emb3) && (!SelfTestClock.ShotsOn || png3E != null), string.Join("|", emb3.Select(x => x["name"])));
                    Check("3D: the faction kinds hide flight master markers one by one (4 shown, Horde off = 2) and route lines one by one (3 drawn, Alliance off = 2); all kinds off then the layer on again shows all of them",
                        FmWanted(fm3All) == 4 && LinesShown(rt3All) == 3 && FmWanted(fm3NoH) == 2 && fm3NoH.Where(x => (string)x["g"] == "fm.horde").All(x => !(bool)x["wanted"]) && LinesShown(rt3NoA) == 2 && !(bool)rt3NoA.First(x => (string)x["g"] == "routes.alliance")["shown"]
                        && FmWanted(fm3Re) == 4 && LinesShown(rt3Re) == 3, FmWanted(fm3All) + "/" + LinesShown(rt3All) + " " + FmWanted(fm3NoH) + "/" + LinesShown(rt3NoA) + " " + FmWanted(fm3Re) + "/" + LinesShown(rt3Re));

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
                    await WorldMapLive3dTest(Check, P);      // (after the camera check: Follow me moves the camera) the live position layer in 3D (MainWindow.WorldMapTest.Live.cs)
                    await WorldMapPrints3dTest(Check, P);    // 2.88: the paw prints in 3D (MainWindow.WorldMapTest.Prints.cs)
                    // markers on the ground: the flight masters sit on the height grid (the page's own grid against the invented landscape the ADTs were made from)
                    var mk = Json.Parse(await WmEval("window.wm3.markers()")) as string; var marks = Json.Parse(mk) as List<object>;
                    double worstG = 0, worstY = 0; int nm = 0;
                    foreach (var o in marks.Cast<Dictionary<string, object>>().Where(o => !((string)o["name"]).StartsWith("point") && !new[] { "Alliance", "Horde", "Neutral", "H1", "H2", "A1", "N1" }.Contains((string)o["name"])))
                    {
                        double x = (double)o["x"], z = (double)o["z"], expect = WmZ((32 - z / WorldMapMath.TileYards) * WorldMapMath.TileYards, (32 - x / WorldMapMath.TileYards) * WorldMapMath.TileYards);
                        worstG = Math.Max(worstG, Math.Abs((double)o["ground"] / 1.0 - expect)); worstY = Math.Max(worstY, Math.Abs((double)o["y"] - (double)o["ground"])); nm++;
                    }
                    Check("3D: " + nm + " flight masters stand on the ground: their height is the grid's (x" + "1.0" + ", true height) to within " + worstY.ToString("0.000", I) + " yd, and the grid agrees with the landscape the ADTs were made from to " + worstG.ToString("0.00", I) + " yd", nm >= 3 && worstY < 0.01 && worstG < 0.5, worstG + " " + worstY);
                    // zone borders in 3D come from the same file: the layer is on (it was switched on in the flat map), the zone under a point is found
                    WorldMapMath.WorldToLatLng((32 - 30.5) * WorldMapMath.TileYards, (32 - 29.7) * WorldMapMath.TileYards, out var zlat, out var zlng);     // tile col 29.7 / row 30.5: area 101 / 102 (Westmark)
                    var zone = Json.Parse(await WmEval("window.wm3.zoneAt(" + zlat.ToString("R", I) + "," + zlng.ToString("R", I) + ")")) as string;
                    var area = Json.Parse(await WmEval("window.wm3.areaAt(" + zlat.ToString("R", I) + "," + zlng.ToString("R", I) + ")"));
                    int expectArea = WmChunkArea((int)((29.7 - 28) * 16), (int)((30.5 - 30) * 16));
                    var s3b = await Wm3State();
                    Check("3D: the zone borders layer arrived in the 3D page too (" + s3b.Child("counts").Int("borders") + " zones), the zone under a ground point is found (" + zone + ") and the area id read from the tile's grid is " + area + " (the invented landscape says " + expectArea + ")",
                        s3b.Child("counts").Int("borders") == 3 && zone == "Westmark" && Convert.ToInt32(area) == expectArea, zone + " " + area + " " + expectArea);
                    // the other layers reach the 3D page: flight masters, zone names, routes
                    // (waits on the 3D page's own state: the flat map's state does not exist here, so this used to run out its whole 20 s)
                    await Wm3WaitState(st => { var c = st.Child("counts"); return c != null && c.Int("zones") >= 3 && c.Int("fm") >= 3 && c.Int("routes") >= 1; }, 20000);
                    var c3 = (await Wm3State())?.Child("counts");
                    Check("3D: the counts of the layers are the same as in the flat map: 3 zone names, 3 flight masters, the routes (2 pairs), 3 zones with borders", c3 != null && c3.Int("zones") == 3 && c3.Int("fm") >= 3 && c3.Int("routes") >= 1 && c3.Int("borders") == 3, c3 == null ? "?" : Json.Write(c3));

                    // 2.85: the layers panel (and every other scroll box) has the thin dark scrollbar: a real overflowing box measures 6 px (Chromium's own is about 17 px), and the rule is in the page's style sheet
                    var sbj = Json.Parse(await WmEval("(function(){ var d=document.createElement('div'); d.className='wm-panel'; d.style.cssText='position:absolute;left:-999px;top:0;width:120px;height:80px;overflow:scroll'; var i=document.createElement('div'); i.style.cssText='width:400px;height:400px'; d.appendChild(i); document.body.appendChild(d); var cs=getComputedStyle(d), bx=parseFloat(cs.borderLeftWidth)+parseFloat(cs.borderRightWidth), by=parseFloat(cs.borderTopWidth)+parseFloat(cs.borderBottomWidth); var w=d.offsetWidth-d.clientWidth-bx, h=d.offsetHeight-d.clientHeight-by; d.remove(); var rule=false, hover=false, ruleText=''; for (var sh of document.styleSheets) { try { for (var r of sh.cssRules) { var t=r.cssText||''; if (t.indexOf('::-webkit-scrollbar-thumb:hover')>=0) hover=true; else if (t.indexOf('::-webkit-scrollbar')>=0 && t.indexOf('6px')>=0) { rule=true; ruleText=t; } } } catch(e){} } var b=document.querySelector('#hud .wm-panel .body'); return JSON.stringify({w:w,h:h,rule:rule,hover:hover,body:b?getComputedStyle(b).overflowY:'',dpr:devicePixelRatio,sw:getComputedStyle(document.body).scrollbarWidth,sup:CSS.supports('selector(::-webkit-scrollbar)'),ua:navigator.userAgent.replace(/.*(Edg.[0-9.]+).*/,'$1'),ruleText:ruleText}); })()")) as string;
                    var sbo = sbj == null ? null : Json.Obj(sbj);
                    Check("3D (2.85): the scrollbars are thin and dark: an overflowing panel's bar is 6 px wide and high, its border not counted (was the browser's wide light bar), the ::-webkit-scrollbar rule and a brighter :hover thumb are in the style sheet, the layers panel scrolls on its own",
                        sbo != null && sbo.Int("w") == 6 && sbo.Int("h") == 6 && Equals(sbo["rule"], true) && Equals(sbo["hover"], true) && sbo.Str("body") == "auto", sbj);

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
                    var pj = "{\"t\":\"prefs\",\"detail\":\"high\",\"exag\":1.2}";
                    await WmEval("window.chrome.webview.postMessage(" + pj + ")");
                    bool prefsSaved = await WaitFor(() => settings.WorldMap3dDetail == "high" && settings.WorldMapExag == 1.2, 8000);
                    Check("3D: the page's detail and height buttons are remembered in the settings (high, 1.2x)", prefsSaved, settings.WorldMap3dDetail + " " + settings.WorldMapExag);
                    settings.WorldMap3dDetail = null; settings.WorldMapExag = 0;

                    // the liquids: the lake (with its lava), the sea along the south edge and the ground where there is none
                    {
                        async Task<Dictionary<string, object>> WaterState(Func<Dictionary<string, object>, bool> until, int ms = 15000)
                        {
                            Dictionary<string, object> w = null; var sww = System.Diagnostics.Stopwatch.StartNew();
                            while (sww.ElapsedMilliseconds < ms) { await Task.Delay(300); var r = await WmEval("window.wm3.water()"); w = string.IsNullOrEmpty(r) || r == "null" ? null : Json.Obj(Json.Parse(r) as string); if (w != null && until(w)) break; }
                            return w;
                        }
                        // the lake: chunk (22, 22) = tile col 29 / row 31, chunk 6,6 of the tile; the cell (4, 4) is wet, the cell (0, 0) is a hole
                        WorldMapMath.GridToWorld(29 + (6 + 4.5 / 8.0) / 16.0, 31 + (6 + 4.5 / 8.0) / 16.0, out var wx1, out var wy1);
                        WorldMapMath.WorldToLatLng(wx1, wy1, out var wlat, out var wlng);
                        await WmEval("window.wm3.setView(" + wlat.ToString("R", I) + "," + wlng.ToString("R", I) + ",700,25,40)");
                        var w1 = await WaterState(w => w.Int("meshes") > 0 && w.Child("byKind") != null && w.Child("byKind").Int("1") > 0 && w.Child("byKind").Int("3") > 0);
                        var wetAt = Json.Parse(await WmEval("window.wm3.waterAt(" + wlat.ToString("R", I) + "," + wlng.ToString("R", I) + ")")) as string;
                        WorldMapMath.GridToWorld(29 + (6 + 0.5 / 8.0) / 16.0, 31 + (6 + 0.5 / 8.0) / 16.0, out var hx1, out var hy1);
                        WorldMapMath.WorldToLatLng(hx1, hy1, out var hlat1, out var hlng1);
                        var holeAt = Json.Parse(await WmEval("window.wm3.waterAt(" + hlat1.ToString("R", I) + "," + hlng1.ToString("R", I) + ")")) as string;
                        WorldMapMath.GridToWorld(28 + 5.5 / 16.0, 30 + 5.5 / 16.0, out var dx1, out var dy1);
                        WorldMapMath.WorldToLatLng(dx1, dy1, out var dlat, out var dlng);
                        var dryAt = Json.Parse(await WmEval("window.wm3.waterAt(" + dlat.ToString("R", I) + "," + dlng.ToString("R", I) + ")")) as string;
                        var lspec = WmLiquidsAt(22, 22).First(q => q.Type == 1240);
                        double expectWet = (lspec.VertexH(4, 4) + lspec.VertexH(5, 4) + lspec.VertexH(4, 5) + lspec.VertexH(5, 5)) / 4;
                        Check("3D liquids: looking at the invented lake the page draws " + (w1?.Int("meshes") ?? 0) + " liquid meshes (" + (w1 == null ? "?" : Json.Write(w1.Child("byKind"))) + ": water and lava) from the .wmw files; a cell of the lake has the surface the file says (" + wetAt + " expected " + expectWet.ToString("0.00", I) + "), a hole in its exists mask and a dry place far away have none",
                            w1 != null && w1.Int("meshes") > 0 && w1.Child("byKind").Int("1") > 0 && w1.Child("byKind").Int("3") > 0 && double.TryParse(wetAt, NumberStyles.Float, I, out var wetV) && Math.Abs(wetV - expectWet) < 0.01 && holeAt == "null" && dryAt == "null", wetAt + " " + holeAt + " " + dryAt + " " + (w1 == null ? "" : Json.Write(w1)));
                        if (SelfTestClock.ShotsOn) { await Task.Delay(800); var pngW = await WorldMapView.CaptureForTest(); if (pngW != null) File.WriteAllBytes(P("worldmap-3d-water-lake-synthetic.png"), pngW); }
                        // the sea along the south edge: tile row 33, chunks 8..15
                        WorldMapMath.GridToWorld(31.5, 33 + 11.0 / 16.0, out var sx1, out var sy1);
                        WorldMapMath.WorldToLatLng(sx1, sy1, out var slat1, out var slng1);
                        await WmEval("window.wm3.setView(" + slat1.ToString("R", I) + "," + slng1.ToString("R", I) + ",1800,10,35)");
                        var w2 = await WaterState(w => w.Child("byKind") != null && w.Child("byKind").Int("2") > 0);
                        var seaAt = Json.Parse(await WmEval("window.wm3.waterAt(" + slat1.ToString("R", I) + "," + slng1.ToString("R", I) + ")")) as string;
                        var js = File.ReadAllText(Path.Combine(WorldMapCache.Root, "_page", "view3d.js"));
                        Check("3D liquids: the sea along the south edge is drawn as ocean meshes (" + (w2 == null ? "?" : Json.Write(w2.Child("byKind"))) + ", surface " + seaAt + " = sea level), the open sea outside the continent's tiles is one flat plane (" + (w2 != null && w2.Bool("outer")) + "), and the old global sea plane is gone from the page (no 260000-yard plane)",
                            w2 != null && w2.Child("byKind").Int("2") > 0 && seaAt == "0" && w2.Bool("outer") && !js.Contains("260000") && !js.Contains("sea.position"), seaAt + " " + (w2 == null ? "" : Json.Write(w2)));
                        if (SelfTestClock.ShotsOn) { var pngS = await WorldMapView.CaptureForTest(); if (pngS != null) File.WriteAllBytes(P("worldmap-3d-water-sea-synthetic.png"), pngS); }
                        // 2.93: the open sea has a floor under it, lit like a tile's empty sea floor (the same colour taken to linear, the same lit material), so its blue is the tiles' sea blue
                        Check("3D liquids: under the open sea lies a floor in the tiles' empty-sea colour (" + w2?.Str("outerFloorColor") + " = " + w2?.Str("tileFloorColor") + "), lit like the terrain and below sea level (" + w2?["outerFloorY"] + "), so the open sea and the sea inside the tiles are one blue",
                            w2 != null && w2.Bool("outerFloor") && w2.Str("outerFloorColor") == w2.Str("tileFloorColor") && w2.Bool("outerFloorLit") && Convert.ToDouble(w2["outerFloorY"], I) < -1, w2 == null ? "null" : Json.Write(w2));
                        var s5 = await Wm3State();
                        Check("3D liquids: no script error while the liquids load and rebuild (" + (s5?.List("errs")?.Count ?? -1) + " errors)", s5 != null && (s5.List("errs") ?? new List<object>()).Count == 0, s5 == null ? "?" : Json.Write(s5.List("errs")));
                    }
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
                Check("3D fallback (2.70): it is remembered (Automatic) with the reason, logged with the renderer and the WebView2 version, and the header offers \"Try 3D again\"",
                    settings.WorldMap3dProblem != null && settings.WorldMap3dProblem.Contains("WebGL") && WorldMapView.Try3dShown && WorldMapView.StatusText.StartsWith("3D is off after a problem on this PC") && WorldReportHas("3D view not available"), WorldMapView.StatusText + " | " + settings.WorldMap3dProblem);
                // "Try 3D again" with graphics that work: 3D is back and the remembered problem is forgotten
                WorldMapView.SetMode(true);
                bool back3d = await WaitFor(() => WorldMapView.Is3D && WorldMapView.LoadedPage == "3d" && WorldMapView.PageReady && WorldMapView.GlOk, 90000);
                Check("3D fallback: Try 3D again starts the 3D view, and once it draws the remembered problem is cleared", back3d && settings.WorldMap3dProblem == null && !WorldMapView.Try3dShown, settings.WorldMap3dProblem);
                // the graphics context is lost while the 3D view draws (a driver reset, a GPU that is taken away): the flat map, never a black view
                if (back3d)
                {
                    WorldMapView.Handle("{\"t\":\"webgl\",\"ok\":false,\"why\":\"the graphics context was lost\",\"renderer\":\"Test GPU\"}");
                    bool lost = await WaitFor(() => !WorldMapView.Is3D && WorldMapView.LoadedPage == "2d" && WorldMapView.PageReady && WorldMapView.WebShown, 60000);
                    Check("3D context lost: the flat map is shown with \"The 3D view can't draw on this PC (graphics/WebGL unavailable: the graphics context was lost)\" and Try 3D again",
                        lost && WorldMapView.Note.Contains("can't draw on this PC") && WorldMapView.Note.Contains("context was lost") && WorldMapView.Try3dShown && WorldMapView.Problem.Contains("context was lost"), WorldMapView.Note);
                }
                // a 3D page that never becomes ready (a hung graphics process, a script that cannot run): after the timeout the flat map, never an endless black view
                {
                    int savedTimeout = WorldMapView.PageTimeoutMs;
                    settings.WorldMap3dProblem = null; WorldMapView.PageTimeoutMs = 2500; WorldMapView.Ignore3dReadyForTest = true;
                    int t0 = WorldMapView.TimeoutsForTest;
                    WorldMapView.SetMode(true);
                    bool timed = await WaitFor(() => WorldMapView.TimeoutsForTest > t0 && !WorldMapView.Is3D && WorldMapView.LoadedPage == "2d" && WorldMapView.PageReady, 90000);
                    WorldMapView.Ignore3dReadyForTest = false; WorldMapView.PageTimeoutMs = savedTimeout;
                    Check("3D no-ready timeout: a 3D page that does not finish loading falls back to the flat map with a note, remembers why and logs it",
                        timed && WorldMapView.GlFallbackReason != null && WorldMapView.GlFallbackReason.Contains("did not finish loading") && settings.WorldMap3dProblem != null && WorldReportHas("page not ready after"), WorldMapView.GlFallbackReason + " | " + WorldMapView.Note);
                    WorldMapView.CopyDetailsForTest();
                    Check("3D no-ready timeout: Copy details says the 3D view was not available and why", WorldMapView.LastReportForTest != null && WorldMapView.LastReportForTest.Contains("Problem: 3D view not available: the 3D page did not finish loading"), WorldMapView.LastReportForTest);
                    settings.WorldMap3dProblem = null;
                }
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
                // the rest of the borders keep arriving: wait for the 20 the check wants (it was a fixed 2.5 s)
                var bj = Json.Obj(Json.Parse(await WmEval("window.wm.borders(null)")) as string);
                for (var bClock = System.Diagnostics.Stopwatch.StartNew(); (bj == null || bj.Int("n") < 20) && bClock.ElapsedMilliseconds < 15000;) { await Task.Delay(150); bj = Json.Obj(Json.Parse(await WmEval("window.wm.borders(null)")) as string); }
                if (SelfTestClock.ShotsOn) { await Task.Delay(1500); var p2 = await WorldMapView.CaptureForTest(); if (p2 != null) File.WriteAllBytes(P("worldmap-borders-2d-real.png"), p2); }
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
                // the liquids on the real Kalimdor: Orgrimmar's canyon floor is dry, the Durotar coast has ocean
                async Task<Dictionary<string, object>> WaterView(string name, double x, double y, double dist, double az, double tilt)
                {
                    WorldMapMath.WorldToLatLng(x, y, out var vlat, out var vlng);
                    await WmEval("window.wm3.setView(" + vlat.ToString("R", I) + "," + vlng.ToString("R", I) + "," + dist.ToString("R", I) + "," + az.ToString("R", I) + "," + tilt.ToString("R", I) + ")");
                    await Settle();
                    for (int w = 0; w < 30; w++) { await Task.Delay(300); var q = await WmEval("window.wm3.water()"); var qs = string.IsNullOrEmpty(q) || q == "null" ? null : Json.Obj(Json.Parse(q) as string); if (qs != null && !qs.Bool("pending") && qs.Int("active") == 0) break; }
                    await Task.Delay(800);
                    var pw = await WorldMapView.CaptureForTest(); if (pw != null) File.WriteAllBytes(P("worldmap-3d-" + name + ".png"), pw);
                    var r = await WmEval("window.wm3.water()");
                    return string.IsNullOrEmpty(r) || r == "null" ? null : Json.Obj(Json.Parse(r) as string);
                }
                var orgNode = idx.Set.TaxiNodes.FirstOrDefault(nd => nd.Name == "Orgrimmar, Durotar");
                if (orgNode != null)
                {
                    var wOrg = await WaterView("water-orgrimmar", orgNode.X, orgNode.Y, 1500, 15, 34);
                    WorldMapMath.WorldToLatLng(orgNode.X, orgNode.Y, out var olat, out var olng);
                    var atOrg = Json.Parse(await WmEval("window.wm3.waterAt(" + olat.ToString("R", I) + "," + olng.ToString("R", I) + ")")) as string;
                    var gOrg = Json.Parse(await WmEval("window.wm3.ground(" + olat.ToString("R", I) + "," + olng.ToString("R", I) + ")"));
                    var jsOrg = await WmEval("window.wm3.water()");
                    notes.Add("note real 3D water at Orgrimmar: ground z " + gOrg + ", liquid there " + atOrg + ", state " + jsOrg);
                    Check("real 3D liquids: Orgrimmar's centre is dry in the 3D page (no liquid over the flight master, ground z " + gOrg + ") - the city no longer sits under a flat sea plane", atOrg == "null" && wOrg != null && wOrg.Bool("outer"), atOrg + " " + gOrg);
                }
                var wCoast = await WaterView("water-durotar-coast", 900, -4800, 2600, 70, 30);
                notes.Add("note real 3D water at the Durotar coast: " + (wCoast == null ? "?" : Json.Write(wCoast)));
                var coastOcean = wCoast?.Child("byKind")?.Int("2") ?? 0;
                Check("real 3D liquids: the Durotar coast is drawn with " + (wCoast?.Int("meshes") ?? 0) + " liquid meshes in " + (wCoast?.Int("tiles") ?? 0) + " tiles, " + coastOcean + " of them ocean", wCoast != null && wCoast.Int("meshes") > 0 && coastOcean > 0, wCoast == null ? "?" : Json.Write(wCoast));
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
                // Eastern Kingdoms: Loch Modan's lake and the lava of Searing Gorge and the Burning Steppes, found in the water files
                {
                    var td0 = WorldMapCache.TerrainDir(WorldMapCache.BuildKey, 0);
                    bool Find(LiquidKind kind, int col, int row, out double fx, out double fy)
                    {
                        fx = fy = 0; var wt0 = WorldMapTerrain.ReadWater(td0, col, row); if (wt0 == null) return false;
                        var ls = wt0.Layers.Where(l => l.Kind == kind).ToList(); if (ls.Count == 0) return false;
                        double cxs = 0, cys = 0; int n = 0;
                        foreach (var l in ls) { WmChunkWorld(col, row, l.Chunk, out var cx0, out var cy0); cxs += cx0; cys += cy0; n++; }
                        fx = cxs / n; fy = cys / n; return true;
                    }
                    var spots = new[] { new { name = "loch-modan", kind = LiquidKind.Water, col = 38, row = 42 }, new { name = "searing-gorge", kind = LiquidKind.Magma, col = 33, row = 44 }, new { name = "burning-steppes", kind = LiquidKind.Magma, col = 35, row = 47 } };
                    foreach (var sp in spots)
                    {
                        if (!Find(sp.kind, sp.col, sp.row, out var fx, out var fy)) { notes.Add("note real 3D water: no " + sp.kind + " in tile " + sp.col + "," + sp.row); continue; }
                        var wv = await WaterView("water-" + sp.name, fx, fy, 1400, 30, 32);
                        int kcount = wv?.Child("byKind")?.Int(((int)sp.kind).ToString(I)) ?? 0;
                        Check("real 3D liquids: " + sp.name + " shows " + sp.kind.ToString().ToLowerInvariant() + " (" + kcount + " meshes of that kind, " + (wv?.Int("meshes") ?? 0) + " in all)", wv != null && kcount > 0, wv == null ? "?" : Json.Write(wv));
                    }
                }
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
