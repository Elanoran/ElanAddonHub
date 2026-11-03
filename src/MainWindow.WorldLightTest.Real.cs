using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Maps;
using ElansAddonHub.Services.WorldMap;

namespace ElansAddonHub
{
    // --selftest "worldlightreal" (3.8), the real game files, read-only (skipped when this PC has no Forever client): the six light tables of the installed build
    // in the layouts this Outpost knows, the light at noon in the zones of the data check (the Eastern Kingdoms default, Elwynn Forest, Duskwood, Tirisfal
    // Glades, Burning Steppes, Orgrimmar, Mulgore), then the real 3D page at the same height over Elwynn, Duskwood, Tirisfal and Burning Steppes: the colours
    // the page applies (fog and sky differ between zones, the water's colour, the game-lighting switch off gives the fixed look back), with pictures
    // (ELANSHUB_SHOTS=1 into the section's folder, or ELANSHUB_LIGHT_SHOTS=<folder>). The page part needs the Eastern Kingdoms pictures and terrain
    // (ELANSHUB_WORLDMAP_FULL=1 builds them in the scratch folder, ELANSHUB_BUILDINGS_CACHE=<folder> reuses them, as objectsreal).
    public partial class MainWindow
    {
        Task<string> WorldLightRealTest(string dir) => RealSection("worldlightreal", dir, (d, work, Check, P, notes) => WorldLightRealChecks(d, work, Check, P, notes));

        // the spots (world x north, y west, yards) of the zones the data check looked at; the light at noon there in 1.60.1.70707
        static readonly (string name, int map, double x, double y)[] WlSpots =
        {
            ("Elwynn Forest", 0, -9460, 60), ("Duskwood", 0, -10560, -1180), ("Tirisfal Glades", 0, 2260, 290), ("Burning Steppes", 0, -7900, -1300),
            ("Orgrimmar", 1, 1630, -4400), ("Mulgore", 1, -2300, -400),
        };

        async Task WorldLightRealChecks(string dir, string work, CheckFn Check, Func<string, string> P, List<string> notes)
        {
            var I = CultureInfo.InvariantCulture;
            var real = Environment.GetEnvironmentVariable("ELANSHUB_WOW");
            if (string.IsNullOrEmpty(real)) real = WowLocator.Find();
            var flv = string.IsNullOrEmpty(real) ? WowLocator.ForeverBetaFolder : WowLocator.Resolve(real, WowLocator.ForeverBetaFolder);   // _forever_ once released
            if (string.IsNullOrEmpty(real) || !Directory.Exists(Path.Combine(real, "Data", "data")) || !Directory.Exists(Path.Combine(real, flv)))
            {
                notes.Add("skip real game files: no WoW install with the Forever client on this PC");
                return;
            }
            var savedAuto = WorldLight.AutoStart; var savedCache = WorldLight.CacheFile;
            try
            {
                WorldLight.AutoStart = false; WorldLight.ResetForTest(); WorldLight.CacheFile = Path.Combine(work, "worldlight.bin");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var raw = await Task.Run(() => WorldLight.ReadCasc(real, flv));
                var set = LightSet.Parse(raw);
                long ms = sw.ElapsedMilliseconds;
                var build = ItemData.GameBuild(real, flv);
                if (set.Problems.Values.Any(p => p.StartsWith("game updated")))
                {
                    notes.Add("skip real game files: " + string.Join("; ", set.Problems.Values) + " (build " + build + "; this Outpost does not know that layout - the 3D map keeps its fixed look, nothing was guessed; Tools\\layoutwatch.ps1 shows the new fields)");
                    return;
                }
                Check("real game files: the six light tables read in the layouts this Outpost knows (" + (set.Lights?.Count ?? 0) + " lights, " + (set.Keys?.Values.Sum(k => k.Count) ?? 0).ToString("N0", I) + " LightData rows of "
                    + (set.Keys?.Count ?? 0) + " params, " + (set.Params?.Count ?? 0) + " LightParams, " + (set.Zones?.Count ?? 0) + " zone polygons, " + (set.Skyboxes?.Count ?? 0) + " skyboxes; " + ms + " ms)",
                    set.Problems.Count == 0 && set.Usable && set.Lights.Count > 500 && set.Keys.Values.Sum(k => k.Count) > 5000 && set.Params?.Count > 800 && set.Zones?.Count >= 18 && set.Skyboxes?.Count >= 20
                    && LightTables.All.All(d => set.LayoutsSeen.GetValueOrDefault(d.Name) == d.Layouts[0]),
                    string.Join("; ", set.Problems.Values) + " " + string.Join(",", set.LayoutsSeen.Select(kv => kv.Key + "=" + kv.Value.ToString("X8", I))));
                if (!set.Usable) return;
                foreach (var n in set.Notes) notes.Add("info real light: " + n);
                int z0 = set.Zones?.Count(z => z.Map == 0 && z.Points.Count >= 3) ?? 0, z1 = set.Zones?.Count(z => z.Map == 1 && z.Points.Count >= 3) ?? 0;
                int pts = set.Zones?.Sum(z => z.Points.Count) ?? 0;
                Check("real game files: the zone light polygons of the two continents (9 on the Eastern Kingdoms, 9 on Kalimdor); 645 of ZoneLightPoint's 649 points (the other 4 belong to the key-locked ZoneLight row)", z0 == 9 && z1 == 9 && pts == 645, z0 + " " + z1 + " " + pts);

                // ---- the light at noon in the zones of the data check
                var def = WorldLight.LightAt(set, 0, -5000, 5000, 0, WorldLight.Noon);
                Check("real light: the Eastern Kingdoms default at noon, far from every zone light (at sea level): fog #659EBB, FogEnd 18000",
                    def != null && def.DefaultLightId == 1 && LightResult.Hex(def.SkyFog) == "#659EBB" && Math.Abs(def.FogEnd - 18000) < 1 && def.DefaultFogEnd == 18000, def?.ToString());
                var got = WlSpots.Select(s => (s, r: WorldLight.LightAt(set, s.map, s.x, s.y, double.NaN, WorldLight.Noon))).ToList();
                LightResult R(string n) => got.First(g => g.s.name == n).r;
                foreach (var g in got) notes.Add("info real light: " + g.s.name + " (" + g.s.x.ToString(I) + ", " + g.s.y.ToString(I) + "): " + g.r);
                var elw = R("Elwynn Forest"); var dusk = R("Duskwood"); var tir = R("Tirisfal Glades"); var bs = R("Burning Steppes"); var org = R("Orgrimmar"); var mul = R("Mulgore");
                Check("real light: Elwynn Forest's zone light gives the warm direct light #ECB374 and otherwise the default's fog (#659EBB, 18000)",
                    elw != null && LightResult.Hex(elw.Direct) == "#ECB374" && LightResult.Hex(elw.SkyFog) == "#659EBB" && elw.FogEnd == 18000 && elw.From.Any(f => f.Contains("Elwynn Forest Zone Light")), elw?.ToString());
                Check("real light: Duskwood (Darkshire, inside light sphere 15) differs: fog #009ACD, FogEnd 6500, ambient #406589, direct #54A0D3",
                    dusk != null && LightResult.Hex(dusk.SkyFog) == "#009ACD" && dusk.FogEnd == 6500 && LightResult.Hex(dusk.Ambient) == "#406589" && LightResult.Hex(dusk.Direct) == "#54A0D3" && dusk.From.Any(f => f == "sphere 15 1.00"), dusk?.ToString());
                Check("real light: Tirisfal Glades (Brill) fog #2C222E with a black sky top; Burning Steppes fog #852902; Orgrimmar fog #BD563D; Mulgore FogEnd 48000",
                    tir != null && LightResult.Hex(tir.SkyFog) == "#2C222E" && tir.SkyTop == 0 && bs != null && LightResult.Hex(bs.SkyFog) == "#852902" && org != null && LightResult.Hex(org.SkyFog) == "#BD563D" && mul != null && mul.FogEnd == 48000,
                    tir + " | " + bs + " | " + org + " | " + mul);
                var hi = WorldLight.LightAt(set, 0, -5000, 5000, 100, WorldLight.Noon);
                Check("real light: the height counts - \"EK High Light\" (Zmin 50) is laid over the default at 100 yards, not at sea level", hi != null && hi.From.Any(f => f.Contains("EK High Light")) && !def.From.Any(f => f.Contains("EK High Light")), hi?.Source + " | " + def?.Source);
                var dawn = WorldLight.LightAt(set, 0, -10560, -1180, double.NaN, 360); var dusk2 = WorldLight.LightAt(set, 0, -10560, -1180, double.NaN, 2400);
                notes.Add("info real light: Duskwood at 03:00 " + dawn + "; at 20:00 " + dusk2);
                Check("real light: the time of day changes it (Duskwood at 03:00 and 20:00 are not noon's); noon is the default the map uses", dawn != null && dusk2 != null && (dawn.SkyFog != dusk.SkyFog || dawn.Direct != dusk.Direct) && (dusk2.SkyFog != dusk.SkyFog || dusk2.Direct != dusk.Direct));
                int water = set.Keys.Values.Sum(k => k.Count(x => x.RiverClose != 0)), rows = set.Keys.Values.Sum(k => k.Count);
                notes.Add("info real light: water colours set in " + water + " of " + rows + " LightData rows (RiverCloseColor; the Classic-era lights of the continents leave them 0 - the page then tints today's water with the zone's fog)");
                var mf = WorldLight.ForView(set, 0, -10560, -1180, double.NaN, 30000);
                Check("real light: zoomed out to the continent (30000 yd) over Duskwood the map default's light is used", mf != null && LightResult.Hex(mf.SkyFog) == LightResult.Hex(WorldLight.LightAt(set, 0, -10560, -1180, double.NaN, WorldLight.Noon, 0).SkyFog) && mf.From.Count == 1, mf?.Source);

                // ---- the saved index of the real tables
                bool wrote = WorldLightIndexFile.TryWrite(WorldLight.CacheFile, build, set);
                var back = WorldLightIndexFile.TryRead(WorldLight.CacheFile);
                Check("real index: the real tables survive the saved index (" + (File.Exists(WorldLight.CacheFile) ? new FileInfo(WorldLight.CacheFile).Length / 1024 : 0) + " KB) and Duskwood reads the same from it",
                    wrote && back != null && back.Build == build && WorldLight.LightAt(back.Set, 0, -10560, -1180, double.NaN, WorldLight.Noon)?.ToString() == dusk.ToString());

                // ---- the real 3D page (below)
                WorldLight.SetForTest(set);
                try { await WorldLightRealPage(real, work, set, Check, P, notes); } catch (Exception e) { Check("real light page checks crashed", false, e.ToString()); }
            }
            finally { WorldLight.ResetForTest(); WorldLight.AutoStart = savedAuto; WorldLight.CacheFile = savedCache; }
        }
    

        // the real 3D page over Elwynn Forest, Duskwood, Tirisfal Glades and Burning Steppes, the camera at the same distance and a low tilt over each (sky and fog in view)
        async Task WorldLightRealPage(string real, string work, LightSet set, CheckFn Check, Func<string, string> P, List<string> notes)
        {
            var I = CultureInfo.InvariantCulture;
            string cacheEnv = Environment.GetEnvironmentVariable("ELANSHUB_BUILDINGS_CACHE"), shotEnv = Environment.GetEnvironmentVariable("ELANSHUB_LIGHT_SHOTS");
            bool full = Environment.GetEnvironmentVariable("ELANSHUB_WORLDMAP_FULL") == "1" || !string.IsNullOrEmpty(cacheEnv);
            string shots = !string.IsNullOrEmpty(shotEnv) ? shotEnv : SelfTestClock.ShotsOn ? Path.Combine(work, "shots") : null;
            if (shots != null) Directory.CreateDirectory(shots);
            var savedLevel = settings.WorldMap3dObjects; var savedMode = settings.WorldMap3dBuildings; bool savedOff = settings.WorldMapLightOff;
            try
            {
                WorldMapService.WdtSourceForTest = null; WorldMapService.AdtSourceForTest = null; WorldMapService.TileSourceForTest = null; ObjectsService.SourceForTest = null; BuildingsService.SourceForTest = null;
                WorldMapService.WowRoot = () => real;
                MapData.ResetForTest();
                MapData.WowRoot = () => real; MapData.Enabled = () => true; MapData.CacheFile = Path.Combine(work, "mapdata.bin");
                WorldMapCache.RootOverride = string.IsNullOrEmpty(cacheEnv) ? Path.Combine(work, "cache") : cacheEnv;
                ObjectsService.ResetForTest(); BuildingsService.ResetForTest(); WorldMapService.ResetForTest();
                MapData.Start();
                await WaitFor(() => MapData.Status == MapData.State.Ready || MapData.Status == MapData.State.Unavailable, 180000);
                if (!MapData.Ready) { notes.Add("skip the real light page: " + MapData.StatusLine + " (nothing was guessed)"); return; }
                if (WorldMapCache.TerrainBuilt(0) == null || WorldMapCache.Built(0) == null)
                {
                    if (!full) { notes.Add("skip the real light page: the pictures and terrain of the Eastern Kingdoms are not built (ELANSHUB_WORLDMAP_FULL=1 builds them in the scratch folder, ELANSHUB_BUILDINGS_CACHE=<folder> reuses them)"); return; }
                    bool b1 = await WorldMapService.EnsureBuilt(0), b2 = await WorldMapService.EnsureTerrain(0);
                    if (!b1 || !b2) { Check("real light page: the Eastern Kingdoms pictures and terrain could be built for the 3D page", false); return; }
                }
                Width = 1500; Height = 950; await Task.Delay(400);
                settings.WorldMap3d = true; settings.WorldMapLayers = new List<string>(); settings.WorldMap3dDetail = "medium"; settings.WorldMapExag = 1.0; settings.WorldMap3dBuildings = "off"; settings.WorldMap3dObjects = "off"; settings.WorldMapLightOff = false;
                WorldLight.SetForTest(set);
                ShowTab("atlas"); AtlasView.History.Clear();
                await AtlasView.Open("worldmap:0?x=-9460&y=60&z=6&m=3");
                if (!await WaitFor(() => WorldMapView.PageReady && WorldMapView.LoadedPage == "3d" && WorldMapView.MapId == 0, 150000) || !(WorldMapView.Is3D && WorldMapView.GlOk)) { notes.Add("note real light page: no 3D here (" + WorldMapView.GlFallbackReason + ")"); return; }
                async Task View(double x, double y, double dist, double az, double tilt)
                {
                    WorldMapMath.WorldToLatLng(x, y, out var lat, out var lng);
                    await WmEval("window.wm3.setView(" + lat.ToString("R", I) + "," + lng.ToString("R", I) + "," + dist.ToString("R", I) + "," + az.ToString("R", I) + "," + tilt.ToString("R", I) + ")");
                }
                // the terrain and pictures around the spot are in: no picture in flight three times in a row
                async Task Settle(int maxMs = 30000)
                {
                    var s2 = System.Diagnostics.Stopwatch.StartNew(); int calm = 0;
                    while (s2.ElapsedMilliseconds < maxMs) { await Task.Delay(400); await WmEval("window.wm3.step(2,130)"); var st = await Wm3State(); if (st != null && st.Int("inFlight") == 0 && st.Int("meshes") > 4) { if (++calm >= 3) break; } else calm = 0; }
                }
                var zones = new[] { ("elwynn", "Elwynn Forest", -9460.0, 60.0), ("duskwood", "Duskwood", -10560.0, -1180.0), ("tirisfal", "Tirisfal Glades", 2260.0, 290.0), ("burning-steppes", "Burning Steppes", -7900.0, -1300.0) };
                var seen = new Dictionary<string, Dictionary<string, object>>();
                foreach (var (key, name, x, y) in zones)
                {
                    await View(x, y, 1800, 30, 14);
                    // the host's light for this spot (its view3 names the ground height there), then the page's once the blend is over
                    await WaitFor(() => WorldMapView.LastView3 != null && Math.Abs(WorldMapView.LastView3.Dist - 1800) < 1 && WlNear(WorldMapView.LastView3, x, y) && WorldMapView.LastLightForTest != null, 15000);
                    var sent = Json.Obj(WorldMapView.LastLightForTest);
                    string want = (sent?.Str("fog") ?? "").TrimStart('#').ToLowerInvariant();
                    var l = await Wm3LightUntil(q => q.Bool("on") && q.Str("fog") == want, 12000);
                    await Settle();
                    seen[key] = l;
                    notes.Add("info real light page: " + name + ": fog #" + l?.Str("fog") + ", sky " + string.Join(" ", (l?.List("sky") ?? new List<object>()).Select(o => "#" + o)) + ", ambient #" + l?.Str("amb") + ", sun #" + l?.Str("sun")
                        + ", water #" + l?.Str("w1") + " / #" + l?.Str("w2") + ", fog density x" + l?.Num("dens").ToString("0.00", I) + " (near " + l?.Num("fogNear") + " yd at " + l?.Num("camDist") + " yd) - " + sent?.Str("src"));
                    if (shots != null) { await Task.Delay(800); var png = await WorldMapView.CaptureForTest(); if (png != null) File.WriteAllBytes(Path.Combine(shots, "sky-" + key + ".png"), png); }
                }
                Dictionary<string, object> L(string k) => seen.TryGetValue(k, out var v) ? v : null;
                var elw = L("elwynn"); var dusk = L("duskwood"); var tir = L("tirisfal"); var bs = L("burning-steppes");
                Check("real light page: at the same height Elwynn's view has the default fog #659ebb and a blue sky, Duskwood's fog #009acd and a darker sky: fog and background differ",
                    elw != null && dusk != null && elw.Str("fog") == "659ebb" && dusk.Str("fog") == "009acd" && elw.Bool("skyTex") && dusk.Bool("skyTex") && !elw.List("sky").SequenceEqual(dusk.List("sky")),
                    Json.Write(elw) + " | " + Json.Write(dusk));
                Check("real light page: Tirisfal's fog #2c222e under a black sky top, Burning Steppes' fog #852902",
                    tir != null && tir.Str("fog") == "2c222e" && (string)tir.List("sky")[0] == "000000" && bs != null && bs.Str("fog") == "852902", Json.Write(tir) + " | " + Json.Write(bs));
                Check("real light page: Elwynn's sun light is warm (the zone light's #ECB374 as a tint: more red than blue), Duskwood's bluish; the strengths stay today's",
                    elw != null && dusk != null && Convert.ToInt32(elw.Str("sun").Substring(0, 2), 16) > Convert.ToInt32(elw.Str("sun").Substring(4, 2), 16) && Convert.ToInt32(dusk.Str("sun").Substring(4, 2), 16) > Convert.ToInt32(dusk.Str("sun").Substring(0, 2), 16)
                    && elw.Num("sunI") == 0.5 && elw.Num("ambI") == 0.66, elw?.Str("sun") + " " + dusk?.Str("sun"));
                Check("real light page: the water's colour changed from today's (#3a93c4) in every zone and differs between them; Duskwood's fog is drawn a little nearer (FogEnd 6500), Elwynn's not",
                    seen.Count == 4 && seen.Values.All(v => v != null && v.Str("w1") != "3a93c4" && v.Str("w2") != "103f6e") && elw.Str("w1") != dusk.Str("w1") && dusk.Num("dens") < 0.9 && elw.Num("dens") == 1,
                    string.Join(" ", seen.Select(kv => kv.Key + ":" + kv.Value?.Str("w1") + "/" + kv.Value?.Num("dens"))));
                // the switch: off over Duskwood gives today's look back, on again the zone's
                await View(-10560, -1180, 1800, 30, 14);
                await Wm3LightUntil(q => q.Bool("on") && q.Str("fog") == "009acd", 12000);
                SettingsPage.WorldMapLightClickForTest(false);
                var off = await Wm3LightUntil(q => !q.Bool("on"), 8000);
                Check("real light page: game lighting off over Duskwood = the fixed look (fog / background #0b1c20, white lights, today's water)", WlFixedLook(off), off == null ? "null" : Json.Write(off));
                if (shots != null) { await Settle(); await Task.Delay(600); var png = await WorldMapView.CaptureForTest(); if (png != null) File.WriteAllBytes(Path.Combine(shots, "sky-duskwood-off.png"), png); }
                SettingsPage.WorldMapLightClickForTest(true);
                var back = await Wm3LightUntil(q => q.Bool("on") && q.Str("fog") == "009acd", 8000);
                Check("real light page: on again and Duskwood's light is back", back != null && back.Str("fog") == "009acd");
                // zoomed out to the continent: the map default's light
                await View(-10560, -1180, 30000, 0, 50);
                var cont = await Wm3LightUntil(q => q.Bool("on") && q.Num("camDist") > 20000 && q.Str("fog") == "659ebb", 12000);
                Check("real light page: zoomed out to the continent the Eastern Kingdoms default light (fog #659ebb)", cont != null && cont.Str("fog") == "659ebb", cont == null ? "null" : Json.Write(cont));
                if (shots != null)
                {
                    await Settle(); await Task.Delay(600); var png = await WorldMapView.CaptureForTest(); if (png != null) File.WriteAllBytes(Path.Combine(shots, "sky-continent.png"), png);
                    SettingsPage.WorldMapLightClickForTest(false); await Wm3LightUntil(q => !q.Bool("on"), 8000); await Task.Delay(400);
                    png = await WorldMapView.CaptureForTest(); if (png != null) File.WriteAllBytes(Path.Combine(shots, "sky-continent-off.png"), png);
                    SettingsPage.WorldMapLightClickForTest(true);
                    notes.Add("info real light page: pictures in " + shots);
                }
                var st3 = await Wm3State();
                Check("real light page: no script error on the page (" + (st3?.List("errs")?.Count ?? -1) + ")", st3 != null && (st3.List("errs") ?? new List<object>()).Count == 0, st3 == null ? "?" : Json.Write(st3.List("errs")));
            }
            finally
            {
                settings.WorldMap3dObjects = savedLevel; settings.WorldMap3dBuildings = savedMode; settings.WorldMapLightOff = savedOff;
            }
        }

        static bool WlNear(WorldMapMessage v, double x, double y) { WorldMapMath.LatLngToWorld(v.Lat, v.Lng, out var vx, out var vy); return Math.Abs(vx - x) < 20 && Math.Abs(vy - y) < 20; }
    }
}
