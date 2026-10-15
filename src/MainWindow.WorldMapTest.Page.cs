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
using ElansAddonHub.Services.Atlas.Classic;
using ElansAddonHub.Services.Maps;
using ElansAddonHub.Services.WorldMap;

namespace ElansAddonHub
{
    // --selftest "worldmap", part 2: the page in a real WebView2 (on the invented continent of part 1) and the real game files (read-only, when there are any).
    public partial class MainWindow
    {
        // the invented continent as a map set: Northland with its zones, the flight nodes / paths / points of the taxi test moved onto it
        static MapSet WmLayerSet(MapSet taxi)
        {
            var lset = SynthMapSet();
            lset.Maps.First(m => m.Id == 0).WdtFileDataId = 7001; lset.Maps.First(m => m.Id == 0).MapType = 1;
            lset.TaxiNodes.AddRange(taxi.TaxiNodes.Select(n => new TaxiNodeInfo { Id = n.Id, MapId = n.MapId == 1 ? 0 : n.MapId, Flags = n.Flags, MountHorde = n.MountHorde, MountAlliance = n.MountAlliance, Name = n.Name, X = n.X, Y = n.Y, Z = n.Z }));
            lset.TaxiNodes.First(n => n.Id == 23).X = 300; lset.TaxiNodes.First(n => n.Id == 23).Y = 400; lset.TaxiNodes.First(n => n.Id == 80).X = -400; lset.TaxiNodes.First(n => n.Id == 80).Y = 1200;
            lset.TaxiNodes.First(n => n.Id == 2).X = 100; lset.TaxiNodes.First(n => n.Id == 2).Y = -800; lset.TaxiNodes.First(n => n.Id == 2).MapId = 0; lset.TaxiNodes.First(n => n.Id == 3274).MapId = 5;
            lset.TaxiPaths.AddRange(taxi.TaxiPaths);
            lset.TaxiPoints.AddRange(taxi.TaxiPoints.Select(p => new TaxiPoint { PathId = p.PathId, Index = p.Index, MapId = p.MapId == 1 ? 0 : p.MapId, X = p.X / 10, Y = p.Y / 5, Z = p.Z }));
            lset.HasTaxi = true;
            // two instances with their way in on the invented continent (Map.db2's ghost spot), and the level the game suggests for them
            var crypt = lset.Maps.First(m => m.Id == 99); crypt.CorpseMapId = 0; crypt.CorpseX = 300; crypt.CorpseY = 800;
            var hall = lset.Maps.First(m => m.Id == 98); hall.CorpseMapId = 0; hall.CorpseX = -400; hall.CorpseY = -500;
            lset.Lfg.Add(new LfgDungeonInfo { Id = 1, Name = "Crypt of Bones", MinLevel = 17, MaxLevel = 17 }); lset.HasLfg = true;
            return lset;
        }

        async Task<Dictionary<string, object>> WmState()
        {
            var r = await WorldMapView.EvalForTest("window.wm ? window.wm.state() : null");
            if (string.IsNullOrEmpty(r) || r == "null") return null;
            var s = Json.Parse(r) as string;
            return s == null ? null : Json.Obj(s);
        }

        async Task<bool> WmWaitState(Func<Dictionary<string, object>, bool> cond, int ms = 20000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                try { var st = await WmState(); if (st != null && cond(st)) return true; } catch (Exception) { }
                await Task.Delay(120);
            }
            return false;
        }

        Task<bool> WmWaitCount(string layer, int atLeast, int ms = 20000)
            => WmWaitState(st => { var c = st.Child("counts"); return c != null && c.TryGetValue(layer, out var v) && v is double d && d >= atLeast; }, ms);

        async Task<string> WmEval(string js) { var r = await WorldMapView.EvalForTest(js); return r; }

        async Task WorldMapPageTest(string dir, string work, List<string> notes, Action<bool> record)
        {
            void Check(string what, bool cond, string detail = null) { notes.Add((cond ? "ok   " : "FAIL ") + what + (!cond && detail != null ? " [" + detail + "]" : "")); record(cond); Util.Log("worldmap test " + (cond ? "ok " : "FAIL ") + what.Substring(0, Math.Min(80, what.Length))); }
            string P(string n) => Path.Combine(dir, n);
            string why;
            bool engine = WebEngine.RuntimeAvailable(out why);
            if (!engine) { notes.Add("note WebView2 runtime not available here (" + why + "): the page checks are skipped"); }
            var savedRoot = WorldMapCache.RootOverride; var savedWdt = WorldMapService.WdtSourceForTest; var savedTile = WorldMapService.TileSourceForTest;
            var savedLodge = AtlasQuery.Context.Lodge; var savedLayers = settings.WorldMapLayers; var savedLast = settings.WorldMapLast;
            var savedCache = MapData.CacheFile; var savedRoot2 = MapData.WowRoot; var savedEnabled = MapData.Enabled;
            WorldMapView.ClipboardSeamForTest = null;
            try
            {
                WorldMapCache.RootOverride = Path.Combine(work, "page");
                WorldMapService.ResetForTest();
                var tiles = WmCols.SelectMany(c => WmRows.Select(r => Tuple.Create(c, r))).ToList();
                WorldMapService.WdtSourceForTest = mapId => WmWdt(tiles);
                WorldMapService.TileSourceForTest = fdid => { Thread.Sleep(12); return WmTile((fdid - 70000) % 64, (fdid - 70000) / 64); };
                var taxi = MapTables.Parse(RawWith(Raw("UiMap", UiMapTable()), Raw("TaxiNodes", WmTaxiNodes()), Raw("TaxiPath", WmTaxiPaths()), Raw("TaxiPathNode", WmTaxiPoints())));
                var lset = WmLayerSet(taxi);
                var lidx = new MapIndex(lset);
                MapData.SetForTest(lidx, "wow_classic_beta|1.60.1.70338|0123456789abcdef");
                AtlasQuery.Context.Lodge = () => null;
                settings.WorldMapLayers = null; settings.WorldMapLast = null;
                WorldMapLayers.FlightTimes = () => new Dictionary<string, double> { ["Horde|23>80"] = 130.4 };

                // ---- no WebView2: a calm note, no crash (the runtime is pretended to be missing)
                WorldMapView.ForceNoWebViewForTest = true;
                var lone = new WorldMapView { Settings = settings };
                lone.Navigate("northland");
                await WaitFor(() => lone.NoWebViewShown, 60000);
                Check("no WebView2: the control shows a calm explanation (no link, nothing crashes) and the rest of the Atlas is said to work", lone.NoWebViewShown && lone.OverlayShown && lone.ProgressText.Contains("WebView2") && !lone.WebShown && !lone.HasWebForTest, lone.ProgressText);
                WorldMapView.ForceNoWebViewForTest = false;
                lone.Dispose();
                WorldMapService.ResetForTest();
                WorldMapCache.Clear();

                if (engine)
                {
                    ShowTab("atlas");
                    AtlasView.History.Clear();
                    var texts = new List<string>(); void OnBuild(int id) { var s = WorldMapService.StateOf(id); if (s != null && s.Running) lock (texts) texts.Add(s.Text); }
                    WorldMapService.BuildChanged += OnBuild;
                    var swOpen = System.Diagnostics.Stopwatch.StartNew();
                    await AtlasView.Open("worldmap:northland");
                    Check("page: worldmap:northland opens the hosted category: crumbs Atlas > World map > Northland, the continent switch lists the continent, the id is the page's own",
                        AtlasView.IsHosted && AtlasView.CurrentId.StartsWith("worldmap:") && AtlasView.HostedCrumbsForTest == "Atlas > World map > Northland" || AtlasView.HostedCrumbsForTest.StartsWith("Atlas > World map"), AtlasView.HostedCrumbsForTest + " " + AtlasView.CurrentId);
                    bool building = await WaitFor(() => WorldMapView.OverlayShown && WorldMapView.ProgressText.StartsWith("Building Northland"), 8000) || texts.Any(t => t.StartsWith("Building Northland"));
                    bool ready = await WaitFor(() => WorldMapView.PageReady, 90000);
                    WorldMapService.BuildChanged -= OnBuild;
                    string[] seen; lock (texts) seen = texts.Distinct().ToArray();
                    Check("page: the first open builds the continent in the background and shows \"Building Northland: N / M tiles\" with progress (" + seen.Length + " different texts), then the page reports ready",
                        building && seen.Any(t => System.Text.RegularExpressions.Regex.IsMatch(t, @"^Building Northland: [\d,]+ / [\d,]+ tiles$")) && ready, string.Join(" | ", seen.Take(4)) + " ready=" + ready);
                    var st = await WmState();
                    Check("page: Leaflet 1.9.4 from the app's own copy is running, the tiles of the continent loaded (some 404 for the sea), nothing was fetched from the internet",
                        st != null && st.Bool("ready") && st.Str("version") == "1.9.4" && st.Int("tiles") > 3 && WorldMapView.BlockedForTest == 0, st == null ? "no state" : Json.Write(st));
                    Check("page: the map is shown instead of the overlay once ready (the overlay is hidden, the web view visible), and the Atlas id follows the view", WorldMapView.WebShown && !WorldMapView.OverlayShown && AtlasView.CurrentId.StartsWith("worldmap:"), AtlasView.CurrentId);
                    Check("palette over the map: Ctrl+K pressed while the web page has the keyboard is caught (the WebView2 reports keys first), because the page is another window and WPF never sees them", WorldMapView.KeyHookForTest);
                    // ---- the Ctrl+K palette over the map. A WebView2 is a window of its own, drawn above every WPF element (airspace), so a WPF palette would be hidden behind it:
                    // while the palette is open over the map the live view is swapped for a snapshot of it (an Image WPF can cover) and swapped back when it closes.
                    {
                        var navBefore = AtlasView.CurrentId;
                        var swFreeze = System.Diagnostics.Stopwatch.StartNew();
                        await AtlasView.OpenPalette("north");
                        swFreeze.Stop();
                        await AtlasView.PaletteWaitForTest(); await Task.Delay(150); UpdateLayout();
                        var snap = WorldMapView.FrozenSnapshotForTest;
                        bool varied = false;
                        if (snap != null && snap.PixelWidth > 100 && snap.PixelHeight > 100)
                        {
                            var src = new System.Windows.Media.Imaging.FormatConvertedBitmap(snap, System.Windows.Media.PixelFormats.Bgra32, null, 0);
                            var px = new byte[4 * 40 * 40]; var colours = new HashSet<int>();
                            for (int gy = 0; gy < 5; gy++) for (int gx = 0; gx < 5; gx++)
                            {
                                src.CopyPixels(new Int32Rect(gx * (snap.PixelWidth - 40) / 4, gy * (snap.PixelHeight - 40) / 4, 40, 40), px, 160, 0);
                                for (int i = 0; i < px.Length; i += 4 * 37) colours.Add((px[i] >> 4) | ((px[i + 1] >> 4) << 4) | ((px[i + 2] >> 4) << 8));
                            }
                            varied = colours.Count >= 3;
                        }
                        Check("palette over the map: the live web view is swapped for a snapshot of the map (web view hidden, a real picture of " + (snap?.PixelWidth ?? 0) + " x " + (snap?.PixelHeight ?? 0) + " not a blank) in " + swFreeze.ElapsedMilliseconds + " ms, so the palette draws on top of it",
                            AtlasView.PaletteOpen && WorldMapView.FrozenForTest && !WorldMapView.WebShown && WorldMapView.FrozenImageShownForTest && varied && AtlasView.PaletteCardForTest.IsVisible && AtlasView.PaletteCardForTest.ActualWidth > 300, "frozen=" + WorldMapView.FrozenForTest + " web=" + WorldMapView.WebShown + " snap=" + (snap == null ? "none" : snap.PixelWidth + "x" + snap.PixelHeight) + " varied=" + varied);
                        Check("palette over the map: the palette works as everywhere - search results (the zone Northland for 'north'), the first selected, with a preview", await WaitFor(() => AtlasView.PaletteResultForTest != null && AtlasView.PaletteResultForTest.Query == "north" && AtlasView.PaletteResultForTest.Total > 0, 5000) && AtlasView.PaletteIdsForTest.Any(i => i.StartsWith("zone:")) && AtlasView.PaletteSelectedForTest == 0, string.Join(",", AtlasView.PaletteIdsForTest));
                        Snapshot(P("worldmap-palette-window.png"));
                        AtlasView.PressPaletteForTest(System.Windows.Input.Key.Escape);
                        await Task.Delay(150);
                        var stBack = await WmState();
                        Check("palette over the map: Esc closes it and the live map is back as it was - shown, the same page, still ready (it stays the Atlas world map page)", !AtlasView.PaletteOpen && !WorldMapView.FrozenForTest && WorldMapView.WebShown && !WorldMapView.FrozenImageShownForTest && WorldMapView.PageReady && stBack != null && stBack.Bool("ready") && AtlasView.CurrentId.StartsWith("worldmap:") && AtlasView.IsHosted, AtlasView.CurrentId + " vs " + navBefore);
                        await AtlasView.OpenPalette(); await Task.Delay(100);
                        AtlasView.PaletteDimForTest.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent, Source = AtlasView.PaletteDimForTest });
                        await Task.Delay(100);
                        Check("palette over the map: opened and closed again (a click on the dim) twice in a row - the map is live each time", !AtlasView.PaletteOpen && WorldMapView.WebShown && !WorldMapView.FrozenForTest);
                        var swHead = System.Diagnostics.Stopwatch.StartNew();
                        AtlasView.SearchBoxForTest.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseDownEvent, Source = AtlasView.SearchBoxForTest });
                        await AtlasView.PaletteOpenWaitForTest();
                        Check("palette over the map: the header search box opens it here too, over the snapshot", AtlasView.PaletteOpen && WorldMapView.FrozenForTest && !WorldMapView.WebShown);
                        AtlasView.ClosePalette(); await Task.Delay(100);
                        Check("palette over the map: closed from code (a page change does the same) the live map returns", WorldMapView.WebShown && !WorldMapView.FrozenForTest);
                    }
                    var key = WorldMapCache.BuildKey;
                    Check("page: the pictures are in <data>\\atlas\\worldmap\\<build key>\\<map id>\\ with a manifest, the page's own files in _page (Leaflet, its licence)",
                        File.Exists(Path.Combine(WorldMapCache.Root, key, "0", "manifest.json")) && File.Exists(Path.Combine(WorldMapCache.Root, "_page", "leaflet.js")) && File.Exists(Path.Combine(WorldMapCache.Root, "_page", "LICENSE-leaflet.txt")) && File.Exists(Path.Combine(WorldMapCache.Root, "_page", "index.html")));
                    // JS math == C# math
                    var js = Json.Parse(await WmEval("JSON.stringify([wm.w2ll(1630,-4373), wm.ll2w(-115.76,160.82), wm.w2ll(-8913,554), wm.w2ll(0,0)])")) as string;
                    var jarr = Json.Parse(js) as List<object>;
                    WorldMapMath.WorldToLatLng(1630, -4373, out var la1, out var ln1); WorldMapMath.LatLngToWorld(-115.76, 160.82, out var wx, out var wy); WorldMapMath.WorldToLatLng(-8913, 554, out var la2, out var ln2);
                    double D(object o, int i) => (double)((List<object>)o)[i];
                    Check("page: the page's world <-> plane formulas give the same numbers as WorldMapMath (Orgrimmar, Stormwind, the origin)", jarr != null && Math.Abs(D(jarr[0], 0) - la1) < 1e-9 && Math.Abs(D(jarr[0], 1) - ln1) < 1e-9 && Math.Abs(D(jarr[1], 0) - wx) < 1e-6 && Math.Abs(D(jarr[1], 1) - wy) < 1e-6 && Math.Abs(D(jarr[2], 1) - ln2) < 1e-9 && Math.Abs(D(jarr[3], 0) + 128) < 1e-9 && Math.Abs(D(jarr[3], 1) - 128) < 1e-9, js);
                    // the default layers arrive on their own
                    bool zones = await WmWaitCount("zones", 3), fms = await WmWaitCount("fm", 3), routes = await WmWaitCount("routes", 1);
                    Check("page: the layers that are on by default (zone names, flight masters, flight routes) are asked for by the page and arrive: 3 zones, 3 flight masters, 1 route", zones && fms && routes);
                    var st2 = await WmState();
                    Check("page: the layers are not asked for twice and the ones that are off are not built (vendors, trainers, nodes ... not requested yet; the dungeon and raid layer is on by default and arrives empty on this invented continent)", st2 != null && st2.Child("counts").Count == 4 && st2.Child("counts").ContainsKey("instances") && ((List<object>)st2.List("on")).Cast<string>().OrderBy(x => x).SequenceEqual(WorldMapLayers.DefaultOn.OrderBy(x => x)), Json.Write(st2?.Child("counts")));
                    // dungeon and raid entrances (Map.db2's ghost spot): on by default, a door for a dungeon and a skull for a raid, labelled with the level the game suggests
                    bool insArrived = await WmWaitCount("instances", 2);
                    async Task<List<Dictionary<string, object>>> InsMarkers(string fire = null)
                    {
                        var raw = await WmEval("window.wm.markers('instances'" + (fire == null ? "" : ", " + fire) + ")");
                        var js = Json.Parse(raw) as string; var arr = js == null ? null : Json.Parse(js) as List<object>;
                        return arr == null ? new List<Dictionary<string, object>>() : arr.OfType<Dictionary<string, object>>().ToList();
                    }
                    var insM = await InsMarkers();
                    Check("page: the dungeon and raid layer arrives on its own (2 entrances on this continent): a door for Crypt of Bones, a skull badge for Deep Hall, labels \"Crypt of Bones · 17\" (the game's suggested level) and \"Deep Hall\", both kinds shown",
                        insArrived && insM.Count == 2 && insM.Any(m => ((string)m["ic"]).Contains("dun") && (string)m["lab"] == "Crypt of Bones · 17" && (bool)m["shown"] && (string)m["g"] == "instances.dungeons") && insM.Any(m => ((string)m["ic"]).Contains("raid") && (string)m["lab"] == "Deep Hall" && (string)m["g"] == "instances.raids" && (bool)m["shown"]), Json.Write(insM));
                    // the child rows switch the kinds one by one
                    await WmEval("(function(){ var r=[].slice.call(document.querySelectorAll('.wm-row')).filter(function(e){return e.textContent.indexOf('Raids')===0})[0]; r.querySelector('input').click(); return 1; })()");
                    var insNoRaid = await InsMarkers();
                    await WmEval("(function(){ var r=[].slice.call(document.querySelectorAll('.wm-row')).filter(function(e){return e.textContent.indexOf('Raids')===0})[0]; r.querySelector('input').click(); return 1; })()");
                    var insRaidBack = await InsMarkers();
                    Check("page: switching Raids off in the panel hides the skull and keeps the door; switching it on shows it again",
                        insNoRaid.Count(m => (bool)m["shown"]) == 1 && (string)insNoRaid.First(m => (bool)m["shown"])["g"] == "instances.dungeons" && insRaidBack.Count(m => (bool)m["shown"]) == 2);
                    var stZ = await WmState(); double zoomNow = stZ == null ? 0 : Convert.ToDouble(stZ["zoom"], CultureInfo.InvariantCulture); bool labelsNow = (await InsMarkers()).All(m => (bool)m["labVisible"]);
                    // right-click on a marker: Open its page + Navigate here -> the /etk way command of the spot
                    string insCopied = null; WorldMapView.ClipboardSeamForTest = t => insCopied = t;
                    await InsMarkers("[0,'contextmenu']");
                    var insMenu = Json.Parse(await WmEval("[].slice.call(document.querySelectorAll('#ctx button')).map(function(b){return b.textContent}).join('|')")) as string;
                    await WmEval("[].slice.call(document.querySelectorAll('#ctx button')).filter(function(b){return b.textContent==='Navigate here'})[0].click()");
                    bool insNav = await WaitFor(() => insCopied != null, 8000);
                    Check("page: a right click on an entrance offers \"Open its page\" and \"Navigate here\" (not the plain map menu); Navigate here copies the /etk way command of the zone map that shows it",
                        insMenu != null && insMenu.Contains("Open its page") && insMenu.Contains("Navigate here") && insNav && insCopied.StartsWith("/etk way 11 "), insMenu + " | " + insCopied);
                    WorldMapView.ClipboardSeamForTest = null;
                    Check("page: the labels of the entrances show from zoom 3.5 on (zoom " + zoomNow + ": " + (labelsNow ? "shown" : "hidden") + ")", insM.Count == 2 && labelsNow == (zoomNow >= 3.5));
                    // flight masters and routes by faction: Horde / Alliance / Neutral kind rows; the kinds hide markers and routes one by one, in this view and the 3D one
                    async Task<List<Dictionary<string, object>>> WmList(string fn, string id)
                    {
                        var raw = await WmEval("window.wm." + fn + "('" + id + "')");
                        var js = Json.Parse(raw) as string; var arr = js == null ? null : Json.Parse(js) as List<object>;
                        return arr == null ? new List<Dictionary<string, object>>() : arr.OfType<Dictionary<string, object>>().ToList();
                    }
                    async Task ClickKid(string title, int nth) => await WmEval("(function(){ var r=[].slice.call(document.querySelectorAll('.wm-row.sub')).filter(function(e){return e.textContent.indexOf('" + title + "')===0})[" + nth + "]; r.querySelector('input').click(); return 1; })()");
                    var fmK = await WmList("markers", "fm"); var rtK = await WmList("lines", "routes");
                    string KidTitle(string g) => g.EndsWith(".horde") ? "Horde" : g.EndsWith(".alliance") ? "Alliance" : "Neutral";
                    Check("page: every flight master and every flight route carries its faction kind (fm.horde / fm.alliance / fm.neutral, routes.*), all shown by default (" + fmK.Count + " masters, " + rtK.Count + " routes)",
                        fmK.Count == 3 && rtK.Count >= 1 && fmK.All(m => ((string)m["g"]).StartsWith("fm.") && (bool)m["shown"]) && rtK.All(l => ((string)l["g"]).StartsWith("routes.") && (bool)l["shown"]), Json.Write(fmK) + Json.Write(rtK));
                    if (fmK.Count == 3 && rtK.Count >= 1)
                    {
                        var kFm = (string)fmK[0]["g"]; int nFm = fmK.Count(m => (string)m["g"] == kFm);
                        await ClickKid(KidTitle(kFm), 0);
                        var fmOff = await WmList("markers", "fm");
                        await ClickKid(KidTitle(kFm), 0);
                        var fmBack = await WmList("markers", "fm");
                        var kRt = (string)rtK[0]["g"]; int nRt = rtK.Count(l => (string)l["g"] == kRt);
                        await ClickKid(KidTitle(kRt), 1);
                        var rtOff = await WmList("lines", "routes");
                        await ClickKid(KidTitle(kRt), 1);
                        var rtBack = await WmList("lines", "routes");
                        Check("page: switching a faction off under Flight masters hides just that faction's markers (" + KidTitle(kFm) + ", " + nFm + " of 3), under Flight routes just that faction's routes (" + KidTitle(kRt) + ", " + nRt + " of " + rtK.Count + "); switching it on shows them again",
                            fmOff.Count(m => (bool)m["shown"]) == 3 - nFm && fmOff.Where(m => (string)m["g"] == kFm).All(m => !(bool)m["shown"]) && fmBack.All(m => (bool)m["shown"])
                            && rtOff.Count(l => (bool)l["shown"]) == rtK.Count - nRt && rtOff.Where(l => (string)l["g"] == kRt).All(l => !(bool)l["shown"]) && rtBack.All(l => (bool)l["shown"]), Json.Write(fmOff) + Json.Write(rtOff));
                        // every kind off, then the layer off and on again: all kinds come back
                        foreach (var k in new[] { "Horde", "Alliance", "Neutral" }) await ClickKid(k, 0);
                        var fmNone = await WmList("markers", "fm");
                        await WmEval("(function(){ var r=[].slice.call(document.querySelectorAll('.wm-row')).filter(function(e){return e.textContent.indexOf('Flight masters')===0})[0]; r.querySelector('input').click(); r.querySelector('input').click(); return 1; })()");
                        var fmAgain = await WmList("markers", "fm");
                        Check("page: with every faction off nothing of the layer shows, and the layer switched off and on again shows all factions (the same rule as the node kinds)", fmNone.All(m => !(bool)m["shown"]) && fmAgain.Count == 3 && fmAgain.All(m => (bool)m["shown"]), Json.Write(fmNone) + Json.Write(fmAgain));
                        // the emblems: three own SVG badges (shield, spiked ring, coin) in the colours of the panel's swatches; a fixture picture of all three, large and small, on the map
                        var emb = Json.Parse(await WmEval("JSON.stringify((function(){ var F = window.WmIcons.factions; var sw = {}; [].slice.call(document.querySelectorAll('.wm-row.sub')).forEach(function(r){ sw[r.textContent.replace(/[0-9 ]/g,'')] = r.querySelector('.sw').style.backgroundColor; }); return { a: F.a.fill, h: F.h.fill, n: F.n.fill, paths: [F.a.body, F.h.body, F.n.body].map(function(p){return p.length}), svgs: document.querySelectorAll('.fmv svg').length, sw: sw }; })())")) as string;
                        var embO = emb == null ? null : Json.Obj(emb);
                        var catFm = ((List<Dictionary<string, object>>)WorldMapLayers.Catalog().First(c => (string)c["id"] == "fm")["children"]).ToDictionary(k => (string)k["id"], k => (string)k["color"]);
                        Check("page: the flight master emblems (icons.js) - a heater shield with a chevron (Alliance), a spiked ring (Horde), a coin with scales (Neutral) - are SVG badges drawn for every marker (" + embO?.Int("svgs") + " on the map), and their fills are the colours of the panel swatches " + string.Join("/", catFm.Values),
                            embO != null && embO.Int("svgs") >= 3 && string.Equals(embO.Str("a"), catFm["fm.alliance"], StringComparison.OrdinalIgnoreCase) && string.Equals(embO.Str("h"), catFm["fm.horde"], StringComparison.OrdinalIgnoreCase) && string.Equals(embO.Str("n"), catFm["fm.neutral"], StringComparison.OrdinalIgnoreCase), emb);
                        var stE = await WmState(); var cen = stE.List("center");
                        string Dd(object o) => Convert.ToDouble(o, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture);
                        string cLat = Dd(cen[0]), cLng = Dd(cen[1]);
                        await WmEval("window.wm.feed({t:'goto', lat:" + cLat + ", lng:" + cLng + ", z:7})");
                        string Item(double dx, double dy, string f, string nm) => "{lat:" + (Convert.ToDouble(cen[0], CultureInfo.InvariantCulture) + dy).ToString("R", CultureInfo.InvariantCulture) + ",lng:" + (Convert.ToDouble(cen[1], CultureInfo.InvariantCulture) + dx).ToString("R", CultureInfo.InvariantCulture) + ",name:'" + nm + "',f:'" + f + "',g:'fm." + (f == "a" ? "alliance" : f == "h" ? "horde" : "neutral") + "',tip:'Flight master',extra:'" + nm + "'}";
                        await WmEval("window.wm.feed({t:'layer', id:'fm', kind:'markers', items:[" + Item(-0.35, 0, "a", "Alliance") + "," + Item(0, 0, "h", "Horde") + "," + Item(0.35, 0, "n", "Neutral") + "]})");
                        await Task.Delay(900);
                        var pngE = await WorldMapView.CaptureForTest(); if (pngE != null) File.WriteAllBytes(P("instances-fm-icons-2d.png"), pngE);
                        Check("page: the fixture picture of the three emblems on the map is taken (instances-fm-icons-2d.png)", pngE != null && pngE.Length > 2000);
                    }
                    // a toggle in the panel: Vendors on -> need -> "none" (no Classic data here), saved in the settings
                    var before = WorldMapView.ReceivedForTest.Count(x => x == "need");
                    await WmEval("(function(){ var r=[].slice.call(document.querySelectorAll('.wm-row')).filter(function(e){return e.textContent.indexOf('Vendors')===0})[0]; r.querySelector('input').click(); return 1; })()");
                    bool needed = await WaitFor(() => WorldMapView.ReceivedForTest.Count(x => x == "need") > before, 8000);
                    bool vend = await WmWaitCount("vendors", 0);
                    var rowText = Json.Parse(await WmEval("[].slice.call(document.querySelectorAll('.wm-row')).filter(function(e){return e.textContent.indexOf('Vendors')===0})[0].textContent")) as string;
                    bool saved = await WaitFor(() => settings.WorldMapLayers != null && settings.WorldMapLayers.Contains("vendors"), 8000);
                    Check("page: switching Vendors on in the layer panel asks the app for it (once), the empty layer says \"none\" in its row, and the choice is remembered in the settings (zones, flight masters, routes, nodes and the new one)",
                        needed && vend && rowText.Contains("none") && saved && settings.WorldMapLayers.Contains("zones") && settings.WorldMapLayers.Contains("fm") && settings.WorldMapLayers.Contains("vendors"), rowText + " " + string.Join(",", settings.WorldMapLayers ?? new List<string>()));
                    // Navigate here through the real right-click menu
                    string copied = null; WorldMapView.ClipboardSeamForTest = t => copied = t;
                    WorldMapMath.WorldToLatLng(250, 500, out var nlat, out var nlng);
                    await WmEval("(function(){ window.chrome.webview.postMessage({t:'nav', lat:" + nlat.ToString("R", CultureInfo.InvariantCulture) + ", lng:" + nlng.ToString("R", CultureInfo.InvariantCulture) + "}); return 1; })()");
                    bool navd = await WaitFor(() => copied != null, 8000);
                    await Task.Delay(400);
                    var toast = Json.Parse(await WmEval("document.getElementById('toast').textContent")) as string;
                    Check("page: \"Navigate here\" copies /etk way <zone ui map> <x %> <y %> <zone name> (Greenvale 75.00 / 75.00) and the page says \"Copied - paste it in WoW chat\"", navd && copied == "/etk way 11 75.00 75.00 Greenvale" && WorldMapView.LastCommandForTest == copied && toast == "Copied - paste it in WoW chat", copied + " | " + toast);
                    copied = null;
                    var menuOpened = Json.Parse(await WmEval("(function(){ var m=document.getElementById('map'); var r=m.getBoundingClientRect(); m.dispatchEvent(new MouseEvent('contextmenu',{bubbles:true,cancelable:true,clientX:r.left+r.width/2,clientY:r.top+r.height/2,button:2})); var c=document.getElementById('ctx'); return c.hidden ? '' : c.textContent; })()")) as string;
                    Check("page: a right-click opens the map's own menu with the position in yards and \"Navigate here\"", !string.IsNullOrEmpty(menuOpened) && menuOpened.Contains("Navigate here") && menuOpened.Contains("x ") && menuOpened.Contains("y "), menuOpened);
                    await WmEval("[].slice.call(document.querySelectorAll('#ctx button')).filter(function(b){return b.textContent.indexOf('Navigate here')===0})[0].click()");
                    bool menuNav = await WaitFor(() => copied != null, 8000);
                    Check("page: choosing it in the menu copies a /etk way command for the spot under the cursor (any zone of the continent, or says so when none shows it)", menuNav ? copied.StartsWith("/etk way ") : WorldMapView.LastToastForTest != null && WorldMapView.LastToastForTest.Contains("No zone map"), copied + " | " + WorldMapView.LastToastForTest);
                    WorldMapView.ClipboardSeamForTest = null;
                    // a page that sends junk: refused, nothing happens
                    int refused0 = WorldMapView.RefusedForTest.Count; string opened = null; void OnOpen(string id) => opened = id;
                    await WmEval("(function(){ var p=window.chrome.webview.postMessage.bind(window.chrome.webview); p('garbage'); p({t:'open',id:'../../evil'}); p({t:'eval',js:'1'}); p({t:'view',lat:1e9,lng:3,z:4}); p({t:'layers',on:['hack']}); p({t:'nav',lat:5,lng:5}); return 1; })()");
                    await WaitFor(() => WorldMapView.RefusedForTest.Count >= refused0 + 6, 8000);
                    Check("page: junk from the page (not JSON, a link outside the entity kinds, an unknown action, a position outside the plane, an unknown layer, a position of the wrong sign) is refused six times over and does nothing", WorldMapView.RefusedForTest.Count == refused0 + 6 && AtlasView.CurrentId.StartsWith("worldmap:"), (WorldMapView.RefusedForTest.Count - refused0) + " " + string.Join(",", WorldMapView.RefusedForTest.Skip(refused0)));
                    // the sandbox
                    var core = WorldMapView.CoreForTest;
                    var src0 = core.Source;
                    await WmEval("window.__r = ''; try { fetch('https://example.com/').then(function(){ window.__r = 'reached'; }).catch(function(){ window.__r = 'blocked'; }); } catch(e) { window.__r = 'blocked'; }");
                    await WmEval("window.open('https://example.com/'); location.href = 'https://example.com/';");
                    await Task.Delay(1500);
                    var fetched = Json.Parse(await WmEval("window.__r")) as string;
                    Check("sandbox: a fetch to the internet is blocked, window.open opens nothing, setting location to another site is cancelled: the page is still the map",
                        fetched == "blocked" && core.Source == src0 && WorldMapView.PageReady, fetched + " " + core.Source);
                    Check("sandbox: dev tools off, no host objects, no default context menu, no status bar, no zoom control; the one place it may navigate is its own virtual host (https://worldmap.outpost.local/)",
                        !core.Settings.AreDevToolsEnabled && !core.Settings.AreHostObjectsAllowed && !core.Settings.AreDefaultContextMenusEnabled && !core.Settings.IsStatusBarEnabled && !core.Settings.IsZoomControlEnabled
                        && WorldMapView.IsOwnUrl("https://worldmap.outpost.local/_page/index.html") && !WorldMapView.IsOwnUrl("https://worldmap.outpost.local.evil.net/") && !WorldMapView.IsOwnUrl("http://worldmap.outpost.local/") && !WorldMapView.IsOwnUrl("https://example.com/") && !WorldMapView.IsOwnUrl("file:///c:/x") && !WorldMapView.IsOwnUrl("https://user@worldmap.outpost.local/") && !WorldMapView.IsOwnUrl("about:blank") && !WorldMapView.IsOwnUrl(""));
                    Check("sandbox: it runs in its own InPrivate profile folder (<data>\\webview2\\worldmap) and a content security policy limits the page to its own files", core.Environment.UserDataFolder.EndsWith("worldmap", StringComparison.OrdinalIgnoreCase) && Directory.Exists(WebEngine.UserDataFolder(WebProfile.WorldMap))
                        && File.ReadAllText(Path.Combine(WorldMapCache.Root, "_page", "index.html")).Contains("Content-Security-Policy") && !File.ReadAllText(Path.Combine(WorldMapCache.Root, "_page", "index.html")).Contains("http://") && !File.ReadAllText(Path.Combine(WorldMapCache.Root, "_page", "index.html")).Contains("https://"));
                    // a click on a marker opens its Atlas page; Back returns to the map (and the map is still alive)
                    await WmEval("window.chrome.webview.postMessage({t:'open', id:'zone:11'})");
                    bool atZone = await WaitFor(() => AtlasView.CurrentId == "zone:11", 20000);
                    await AtlasView.Back();
                    bool backOnMap = await WaitFor(() => AtlasView.CurrentId.StartsWith("worldmap:") && AtlasView.IsHosted, 20000);
                    await Task.Delay(500);
                    var st3 = await WmState();
                    Check("page: a click on a zone opens zone:11 in the Atlas and Back returns to the world map, which is still the same live page", atZone && backOnMap && st3 != null && st3.Bool("ready"), AtlasView.CurrentId);
                    // a click on a dungeon marker opens the instance page (with its Entrance card); Back returns to the live map
                    await WmEval("window.wm.markers('instances', [0,'click'])");
                    bool atInst = await WaitFor(() => AtlasView.CurrentId == "instance:99", 20000);
                    await AtlasView.Back();
                    bool backOnMap2 = await WaitFor(() => AtlasView.CurrentId.StartsWith("worldmap:") && AtlasView.IsHosted, 20000);
                    await Task.Delay(500);
                    var st3b = await WmState();
                    Check("page: a click on an entrance opens instance:99 in the Atlas and Back returns to the same world map", atInst && backOnMap2 && st3b != null && st3b.Bool("ready"), AtlasView.CurrentId);
                    // the view moves -> the id follows without a new history step
                    int stepsBefore = AtlasView.History.Count;
                    WorldMapView.Handle("{\"t\":\"view\",\"lat\":-123.4,\"lng\":130.5,\"z\":5.5}");
                    WorldMapMath.LatLngToWorld(-123.4, 130.5, out var vx, out var vy);
                    string expectId = "worldmap:0?x=" + Math.Round(vx).ToString("0", CultureInfo.InvariantCulture) + "&y=" + Math.Round(vy).ToString("0", CultureInfo.InvariantCulture) + "&z=5.5";
                    Check("page: moving the map changes the page's id to worldmap:<map>?x=&y=&z= (whole yards) without a new step in the history", AtlasView.CurrentId == expectId && AtlasView.History.Count == stepsBefore, AtlasView.CurrentId + " vs " + expectId);
                    // history over the map and other pages
                    await AtlasView.Open("worldmap:0?x=250&y=500&z=6&p=1");
                    await Task.Delay(300);
                    var st4 = await WmState();
                    Check("page: opening worldmap:0?x=250&y=500&z=6&p=1 centres the map there at zoom 6 (the page's centre is that position)", st4 != null && Math.Abs(st4.List("center").Count > 0 ? (double)st4.List("center")[0] - nlat : 99) < 0.05 && Math.Abs((double)st4.List("center")[1] - nlng) < 0.05 && Math.Abs(st4.Int("zoom") - 6) < 0.01, Json.Write(st4?.List("center")));
                    await AtlasView.Open("item:2589"); await AtlasView.Back();
                    await Task.Delay(300);
                    Check("page: item page -> Back is the map again at the id it had (history works for the hosted map)", AtlasView.CurrentId.StartsWith("worldmap:0?x=250&y=500&z=6") && WorldMapView.MapId == 0, AtlasView.CurrentId);
                    // the Atlas start page lists the category; a search finds it
                    await AtlasView.GoStart();
                    var hits = WorldMapIds.Hits("northland");
                    var hits2 = WorldMapIds.Hits("world map");
                    Check("Atlas: the World map category is registered with a tile, and a search for the continent's name or \"world map\" offers the page",
                        AtlasCategories.Find("worldmap") != null && AtlasCategories.Visible.Any(c => c.Id == "worldmap") && hits.Any(h => h.Id == "worldmap:northland") && hits2.Any(h => h.Id == "worldmap:" && h.Kind == "page"), string.Join(",", hits.Select(h => h.Id)));
                    // Show on world map from a zone page
                    await AtlasView.Open("zone:11");
                    await WaitFor(() => AtlasView.worldMapButtons.Count > 0, 10000);
                    Check("zone page: a \"Show on world map\" button sits in the map card (not on an instance's page)", AtlasView.worldMapButtons.Count == 1 && (string)AtlasView.worldMapButtons[0].Content == "Show on world map", AtlasView.worldMapButtons.Count + "");
                    if (AtlasView.worldMapButtons.Count > 0) AtlasView.worldMapButtons[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    bool opened2 = await WaitFor(() => AtlasView.CurrentId.StartsWith("worldmap:0?x=500&y=1000&z=") && WorldMapView.PageReady && AtlasView.IsHosted, 20000);
                    Check("zone page: the button opens the world map centred on the zone (x 500, y 1000), without a ring", opened2 && !AtlasView.CurrentId.Contains("p=1"), AtlasView.CurrentId);
                    // screenshots + the shot of the synthetic continent
                    await WmWaitState(s => s.Int("tiles") > 10, 10000);
                    await Task.Delay(500);
                    var png = await WorldMapView.CaptureForTest();
                    if (png != null) File.WriteAllBytes(P("worldmap-synthetic.png"), png);
                    Check("screenshot: the page was captured", png != null && png.Length > 5000, (png?.Length ?? 0) + "");

                    await WorldMapPage3dTest(dir, notes, Check, P, taxi, lidx);
                    ShowTab("atlas"); AtlasView.History.Clear();
                    // a new game build: the pictures of the old build are removed, the new build is made on the next visit
                    WorldMapView.Dispose();
                    var oldDir = Path.Combine(WorldMapCache.Root, WorldMapCache.BuildKey);
                    MapData.SetForTest(lidx, "wow_classic_beta|1.60.2.71000|fedcba9876543210");
                    int builds0 = WorldMapService.BuildCountForTest;
                    WorldMapService.ResetForTest();
                    await AtlasView.Open("worldmap:northland");
                    bool rebuilt = await WaitFor(() => WorldMapView.PageReady, 60000);
                    Check("new game build: the folder of the old build is deleted, the new key's pictures are built (a new key folder with its manifest), the page opens again",
                        rebuilt && WorldMapService.BuildCountForTest == builds0 + 1 && !Directory.Exists(oldDir) && File.Exists(Path.Combine(WorldMapCache.Root, WorldMapCache.BuildKey, "0", "manifest.json")) && WorldMapCache.BuildKey.StartsWith("1.60.2.71000-fedcba98"), WorldMapCache.BuildKey + " " + oldDir);
                    long bytesBefore = WorldMapCache.SizeBytes();
                    WorldMapView.Dispose();
                    WorldMapService.ResetForTest();
                    long freed = WorldMapCache.Clear();
                    Check("settings: Clear world map cache frees the pictures (" + WorldMapCache.SizeText(freed) + ") and the next visit builds them again", bytesBefore > 0 && freed == bytesBefore && WorldMapCache.SizeBytes() == 0 && !WorldMapService.IsBuilt(0));
                    ResetAtlasForTest();
                }

                // ---------------------------------------------------------------- the real game files (read-only; optional)
                await WorldMapRealChecks(dir, work, engine, notes, Check, P);
            }
            finally
            {
                WorldMapView.ForceNoWebViewForTest = false; WorldMapView.ClipboardSeamForTest = null;
                try { WorldMapView.Dispose(); } catch (Exception) { }
                WorldMapService.CancelAll(); WorldMapService.ResetForTest();
                WorldMapService.WdtSourceForTest = savedWdt; WorldMapService.TileSourceForTest = savedTile; WorldMapCache.RootOverride = savedRoot;
                AtlasQuery.Context.Lodge = savedLodge; settings.WorldMapLayers = savedLayers; settings.WorldMapLast = savedLast;
                MapData.CacheFile = savedCache; MapData.WowRoot = savedRoot2; MapData.Enabled = savedEnabled;
                WorldMapLayers.SavedRoutes = () => settings.SavedRoutes; WorldMapLayers.FlightTimes = FlightTimesForMap;
                try { ResetAtlasForTest(); } catch (Exception) { }
            }
        }

        // ================================================================================================================ the real game
        async Task WorldMapRealChecks(string dir, string work, bool engine, List<string> notes, CheckFn Check, Func<string, string> P)
        {
            var real = Environment.GetEnvironmentVariable("ELANSHUB_WOW");
            if (string.IsNullOrEmpty(real)) real = WowLocator.Find();
            if (string.IsNullOrEmpty(real) || !Directory.Exists(Path.Combine(real, "Data", "data")) || !Directory.Exists(Path.Combine(real, "_classic_beta_")))
            {
                notes.Add("skip real game files: no WoW install with the Forever beta client on this PC");
                return;
            }
            var rw = Path.Combine(work, "real");
            double savedW = Width, savedH = Height;
            Width = 1500; Height = 950; await Task.Delay(400);
            try
            {
            WorldMapService.WdtSourceForTest = null; WorldMapService.TileSourceForTest = null;
            WorldMapService.WowRoot = () => real;
            MapData.ResetForTest();
            MapData.WowRoot = () => real; MapData.Enabled = () => true; MapData.CacheFile = Path.Combine(rw, "mapdata.bin");
            MapData.Start();
            await WaitFor(() => MapData.Status == MapData.State.Ready || MapData.Status == MapData.State.Unavailable, 120000);
            if (!MapData.Ready) { notes.Add("skip real game files: " + MapData.StatusLine + " (nothing was guessed)"); return; }
            var idx = MapData.Index;
            var conts = WorldMapService.Continents(idx);
            var kal = conts.FirstOrDefault(c => c.MapId == 1); var ek = conts.FirstOrDefault(c => c.MapId == 0);
            Check("real game files: Map.db2's WdtFileDataID field (index 21) reads 782779 for Kalimdor and 775971 for the Eastern Kingdoms; they are the client's continent maps (" + string.Join(", ", conts.Select(c => c.Name + " " + c.MapId)) + ")",
                kal != null && ek != null && kal.WdtFileDataId == 782779 && ek.WdtFileDataId == 775971 && kal.Slug == "kalimdor" && ek.Slug == "eastern-kingdoms" && WorldMapIds.SlugsOf(idx)("azeroth") == 0, string.Join(",", conts.Select(c => c.Name)));
            var set = idx.Set;
            var orgr = set.TaxiNodes.FirstOrDefault(n => n.Name == "Orgrimmar, Durotar");
            Check("real game files: the flight tables of the verified layouts are read: " + set.TaxiNodes.Count + " nodes, " + set.TaxiPaths.Count + " paths, " + set.TaxiPoints.Count + " path points; Orgrimmar's flight master is on Kalimdor at x 1677.6, y -4315.7",
                set.HasTaxi && set.TaxiNodes.Count >= 50 && set.TaxiPaths.Count >= 100 && set.TaxiPoints.Count >= 1000 && orgr != null && orgr.MapId == 1 && Math.Abs(orgr.X - 1677.6) < 1 && Math.Abs(orgr.Y + 4315.7) < 1 && !MapData.StatusSuffix.Contains("unavailable"));
            // the WDTs, straight from the game
            WdtFile kw, ew; List<WdtTile> strayK = new List<WdtTile>(), strayE = new List<WdtTile>();
            using (var casc = CascStorage.Open(real, "_classic_beta_"))
            {
                casc.ZeroFillEncrypted = true;
                var keys = casc.ResolveFileIds(new HashSet<int> { kal.WdtFileDataId, ek.WdtFileDataId });
                kw = WdtFile.Parse(casc.ReadByContentKey(keys[kal.WdtFileDataId])); ew = WdtFile.Parse(casc.ReadByContentKey(keys[ek.WdtFileDataId]));
            }
            var keptK = kw.Ok ? WdtFile.WithoutStrays(kw.Tiles.Where(t => t.MinimapFdid != 0).ToList(), 12, strayK) : new List<WdtTile>();
            var keptE = ew.Ok ? WdtFile.WithoutStrays(ew.Tiles.Where(t => t.MinimapFdid != 0).ToList(), 12, strayE) : new List<WdtTile>();
            Check("real game files: the WDTs read (MAID with all eight FileDataIDs per tile): Kalimdor " + kw.Tiles.Count + " tiles (MAIN says " + kw.MainTiles + "), Eastern Kingdoms " + ew.Tiles.Count + " (MAIN " + ew.MainTiles + "); every tile has a minimap",
                kw.Ok && ew.Ok && kw.Tiles.Count == kw.MainTiles && ew.Tiles.Count == ew.MainTiles && kw.WithMinimap == kw.Tiles.Count && ew.WithMinimap == ew.Tiles.Count && kw.Tiles.Count > 900 && ew.Tiles.Count > 700, kw.Problem + " " + ew.Problem);
            Check("real game files: Kalimdor's stray 3 x 3 block at the grid's top-left corner is left out (" + strayK.Count + " tiles), the Kingdoms have none (" + strayE.Count + "); the land keeps its " + keptK.Count + " / " + keptE.Count + " tiles",
                strayK.Count <= 12 && strayK.All(t => t.Col < 4 && t.Row < 4) && strayE.Count == 0 && keptK.Count > 900 && keptE.Count > 700 && !keptK.Any(t => t.Col < 4 && t.Row < 4));
            // the orientation, proven on the game's own flight masters: every one of them stands on a tile that has a minimap (a mirrored or transposed grid would put most of them in the sea)
            int onLand = 0, total = 0; var misses = new List<string>();
            foreach (var n in set.TaxiNodes.Where(WorldMapLayers.IsFlightMaster))
            {
                var tl = n.MapId == 1 ? keptK : n.MapId == 0 ? keptE : null; if (tl == null) continue;
                WorldMapMath.TileOf(n.X, n.Y, out var tc, out var tr);
                total++; if (tl.Any(t => t.Col == tc && t.Row == tr)) onLand++; else misses.Add(n.Name);
            }
            Check("real game files: all " + total + " flight masters of the two continents stand on a map tile that exists in their WDT (the grid is not mirrored or turned: tile col = 32 - y/533.33, row = 32 - x/533.33)", total >= 40 && onLand == total, onLand + "/" + total + " " + string.Join(",", misses.Take(5)));
            int zoneLand = 0, zoneTotal = 0;
            foreach (var z in idx.UiMaps.Where(m => m.System == 0 && m.Type == UiMapInfo.Zone))
            {
                var c = idx.MapToWorld(z.Id, 0.5, 0.5); if (c == null) continue;
                var tl = c.Value.MapId == 1 ? keptK : c.Value.MapId == 0 ? keptE : null; if (tl == null) continue;
                WorldMapMath.TileOf(c.Value.X, c.Value.Y, out var tc, out var tr);
                zoneTotal++; if (tl.Any(t => t.Col == tc && t.Row == tr)) zoneLand++;
            }
            Check("real game files: the middle of every zone map (" + zoneTotal + " of them) lies on a map tile of its continent", zoneTotal > 40 && zoneLand >= zoneTotal - 1, zoneLand + "/" + zoneTotal);

            // a real build of a small region: Durotar and the Orgrimmar coast, map tiles col 38..42, row 28..33
            WorldMapCache.RootOverride = Path.Combine(rw, "cache");
            var regionKey = WorldMapCache.BuildKey; var regionDir = WorldMapCache.MapDir(regionKey, 1);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var rres = WorldMapService.Build(1, kal.Name, kal.WdtFileDataId, regionKey, regionDir, t => t.Col >= 38 && t.Col <= 42 && t.Row >= 28 && t.Row <= 33, CancellationToken.None, null);
            long regionMs = sw.ElapsedMilliseconds;
            int expectTiles = keptK.Count(t => t.Col >= 38 && t.Col <= 42 && t.Row >= 28 && t.Row <= 33);
            Check("real game files: a region of Kalimdor (" + expectTiles + " map tiles of real minimaps) is built from the game's BLP files: 4 native 256 px tiles per map tile, the levels above, a manifest (" + regionMs + " ms, " + rres.Files + " files, " + WorldMapCache.SizeText(rres.Bytes) + ")",
                rres.Complete && rres.Adt == expectTiles && rres.Decoded == expectTiles && Directory.GetFiles(Path.Combine(regionDir, "z7")).Length == expectTiles * 4 && rres.Missing == 0 && WorldMapCache.Built(1) != null, rres.Adt + "/" + expectTiles);
            // a real tile is a picture, not black or white
            WorldMapMath.TileOf(340, -4686, out var rzc, out var rzr);
            var rtile = WorldMapPyramid.TileFile(regionDir, 7, rzc * 2, rzr * 2);
            var rbuf = new byte[256 * 256 * 4]; bool rread = WorldMapPyramid.ReadJpeg(rtile, rbuf);
            long sum = 0, distinct = 0; var hist = new HashSet<int>(); for (int i = 0; i < rbuf.Length; i += 4) { sum += rbuf[i] + rbuf[i + 1] + rbuf[i + 2]; hist.Add((rbuf[i] >> 4) << 8 | (rbuf[i + 1] >> 4) << 4 | (rbuf[i + 2] >> 4)); } distinct = hist.Count;
            double mean = sum / (3.0 * 256 * 256);
            Check("real game files: the native tile under Razor Hill is a real picture (mean brightness " + mean.ToString("0", CultureInfo.InvariantCulture) + ", " + distinct + " distinct colours)", rread && mean > 25 && mean < 220 && distinct > 20, mean + " " + distinct);
            var rres2 = WorldMapService.Build(1, kal.Name, kal.WdtFileDataId, regionKey, regionDir, t => t.Col >= 38 && t.Col <= 42 && t.Row >= 28 && t.Row <= 33, CancellationToken.None, null);
            Check("real game files: building the same region again reuses every tile (nothing decoded, nothing written)", rres2.Complete && rres2.Decoded == 0 && rres2.Reused == expectTiles && rres2.Written == 0, rres2.Decoded + " " + rres2.Reused);
            notes.Add("note real game files: region build " + regionMs + " ms for " + expectTiles + " map tiles, " + rres.Files + " files, " + (rres.Bytes / 1024) + " KB; managed memory after " + GC.GetTotalMemory(true) / 1048576 + " MB");
            await WorldMapTerrainRealChecks(idx, real, rw, notes, Check);
            await WorldMapWaterRealChecks(idx, real, rw, notes, Check);
            WorldMapCache.RootOverride = Path.Combine(rw, "cache");

            if (engine)
            {
                // the page on the real region, with the real layers; the Classic baseline (read-only, from the user's own data folder) when it is there
                var classicIdx = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ElansAddonHub", "atlas", "classic", "index.bin");
                ClassicIndex ci = null; try { if (File.Exists(classicIdx)) ci = ClassicIndex.Load(classicIdx); } catch (Exception) { }
                if (ci != null) ClassicAtlas.Use(ci);
                try
                {
                    WorldMapLayers.FlightTimes = () => new Dictionary<string, double>();
                    ShowTab("atlas"); AtlasView.History.Clear();
                    WorldMapMath.WorldToLatLng(340, -4686, out _, out _);
                    await AtlasView.Open("worldmap:1?x=1200&y=-4500&z=4.5");
                    bool ready = await WaitFor(() => WorldMapView.PageReady, 90000);
                    Check("real game files: the page opens on the real region and reports ready (the first tiles of the real pyramid are in)", ready && WorldMapView.WebShown, "ready=" + ready);
                    var zl = await WmWaitCount("zones", 3, 60000); var fm = await WmWaitCount("fm", 10, 60000); var rt = await WmWaitCount("routes", 5, 60000);
                    var rs = await WmState();
                    Check("real game files: the default layers arrive from the real tables: " + (rs?.Child("counts") == null ? "?" : Json.Write(rs.Child("counts"))) + " (zone names, flight masters, flight routes of Kalimdor)", zl && fm && rt);
                    await Task.Delay(1200);
                    var p1 = await WorldMapView.CaptureForTest();
                    if (p1 != null) File.WriteAllBytes(P("worldmap-real-region.png"), p1);
                    // layers on: nodes (Classic), vendors, rares ...
                    if (ci != null)
                    {
                        foreach (var name in new[] { "Gathering nodes", "Vendors", "Rare creatures" })
                            await WmEval("(function(){ var r=[].slice.call(document.querySelectorAll('.wm-row')).filter(function(e){return e.textContent.indexOf('" + name + "')===0})[0]; if (r && !r.querySelector('input').checked) r.querySelector('input').click(); return 1; })()");
                        await WmWaitCount("nodes", 1, 90000); await WmWaitCount("vendors", 1, 90000); await WmWaitCount("rares", 1, 90000);
                        var cs = await WmState();
                        Check("real game files: with the Classic baseline the node, vendor and rare layers fill in on the real region (" + Json.Write(cs.Child("counts")) + ")", cs.Child("counts").Int("nodes") > 100 && cs.Child("counts").Int("vendors") > 20 && cs.Child("counts").Int("rares") > 5, Json.Write(cs.Child("counts")));
                        await AtlasView.Open("worldmap:1?x=1550&y=-4400&z=6");
                        await Task.Delay(1800);
                        var p2 = await WorldMapView.CaptureForTest();
                        if (p2 != null) File.WriteAllBytes(P("worldmap-real-zoomed.png"), p2);
                    }
                    else notes.Add("note real game files: no Classic baseline on this PC: the node / vendor / quest / rare layers have no data to show");
                    ResetAtlasForTest();
                }
                finally { if (ci != null) ClassicAtlas.Use(null); }

                // the whole continents (slow, a full build each): ELANSHUB_WORLDMAP_FULL=1
                if (Environment.GetEnvironmentVariable("ELANSHUB_WORLDMAP_FULL") == "1")
                {
                    WorldMapView.Dispose();       // the page's virtual host points at the cache folder it was made with
                    settings.WorldMapLayers = null;   // the default layers for the pictures of the whole continents
                    WorldMapCache.RootOverride = Path.Combine(rw, "full");
                    WorldMapService.ResetForTest();
                    foreach (var c in new[] { kal, ek })
                    {
                        var s2 = System.Diagnostics.Stopwatch.StartNew(); long m0 = GC.GetTotalMemory(true);
                        bool built = await WorldMapService.EnsureBuilt(c.MapId);
                        var man = WorldMapCache.Built(c.MapId); var stt = WorldMapService.StateOf(c.MapId);
                        Check("real game files (full): " + c.Name + " is built: " + (man?.Adt ?? 0) + " map tiles, " + (man?.Files ?? 0) + " files, " + WorldMapCache.SizeText(man?.Bytes ?? 0) + " in " + s2.ElapsedMilliseconds / 1000.0 + " s", built && man != null && man.Adt > 700);
                        notes.Add("note real game files (full): " + c.Name + " " + (man?.Adt ?? 0) + " map tiles -> " + (man?.Files ?? 0) + " files, " + WorldMapCache.SizeText(man?.Bytes ?? 0) + ", " + s2.ElapsedMilliseconds + " ms, bounds col " + man?.MinCol + "-" + man?.MaxCol + " row " + man?.MinRow + "-" + man?.MaxRow + ", managed memory +" + (GC.GetTotalMemory(true) - m0) / 1024 + " KB afterwards");
                    }
                    notes.Add("note real game files (full): cache " + WorldMapCache.SizeText(WorldMapCache.SizeBytes()));
                    var ci2 = File.Exists(classicIdx) ? ClassicIndex.Load(classicIdx) : null; if (ci2 != null) ClassicAtlas.Use(ci2);
                    try
                    {
                        ShowTab("atlas"); AtlasView.History.Clear();
                        foreach (var c in new[] { kal, ek })
                        {
                            await AtlasView.Open("worldmap:" + c.Slug);
                            await WaitFor(() => WorldMapView.PageReady && WorldMapView.MapId == c.MapId, 90000);
                            await WmWaitCount("zones", 3, 60000); await WmWaitCount("fm", 5, 60000); await WmWaitCount("routes", 3, 60000);
                            await Task.Delay(2500);
                            var pa = await WorldMapView.CaptureForTest(); if (pa != null) File.WriteAllBytes(P("worldmap-" + c.Slug + "-out.png"), pa);
                        }
                        await AtlasView.Open("worldmap:1?x=1650&y=-4350&z=5.5");
                        await WmWaitCount("fm", 5, 30000);
                        if (ci2 != null)
                        {
                            foreach (var name in new[] { "Gathering nodes", "Vendors", "Trainers", "Quest givers" })
                                await WmEval("(function(){ var r=[].slice.call(document.querySelectorAll('.wm-row')).filter(function(e){return e.textContent.indexOf('" + name + "')===0})[0]; if (r && !r.querySelector('input').checked) r.querySelector('input').click(); return 1; })()");
                            await WmWaitCount("nodes", 1, 90000); await WmWaitCount("vendors", 1, 90000); await WmWaitCount("quests", 1, 90000);
                        }
                        await Task.Delay(2500);
                        var pb = await WorldMapView.CaptureForTest(); if (pb != null) File.WriteAllBytes(P("worldmap-kalimdor-in.png"), pb);
                        await AtlasView.Open("worldmap:1?x=-4500&y=-1200&z=3.25");
                        await Task.Delay(3000);
                        var pc = await WorldMapView.CaptureForTest(); if (pc != null) File.WriteAllBytes(P("worldmap-kalimdor-mid.png"), pc);
                        await WorldMapReal3d(idx, notes, Check, P);
                    }
                    finally { if (ci2 != null) ClassicAtlas.Use(null); ResetAtlasForTest(); }
                }
            }
            }
            finally { Width = savedW; Height = savedH; }
        }
    }
}
