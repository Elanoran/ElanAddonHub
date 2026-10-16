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
    // --selftest "worldmapreal": the world map against the player's real game files (read-only; skipped when this PC has no Forever beta client). Split out of "worldmap" because it
    // reads CASC and builds real regions (minutes); the synthetic checks of the same code stay in "worldmap". It runs in -Full, with -Only worldmapreal, and in changed mode when
    // something that reads game data changes (see SelfTestCache.Defs); an unchanged game build + unchanged readers since a passing run skip it.
    public partial class MainWindow
    {
        async Task<string> WorldMapRealTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            void Check(string what, bool cond, string detail = null) { SelfTestClock.Mark(what); notes.Add((cond ? "ok   " : "FAIL ") + what + (!cond && detail != null ? " [" + detail + "]" : "")); ok &= cond; Util.Log("worldmapreal test " + (cond ? "ok " : "FAIL ") + what.Substring(0, Math.Min(80, what.Length))); }
            string P(string n) => Path.Combine(dir, n);
            var work = Path.Combine(dir, "worldmapreal");
            string why;
            bool engine = WebEngine.RuntimeAvailable(out why);
            if (!engine) notes.Add("note WebView2 runtime not available here (" + why + "): the page checks of the real files are skipped");
            var savedRoot = WorldMapCache.RootOverride; var savedWdt = WorldMapService.WdtSourceForTest; var savedTile = WorldMapService.TileSourceForTest; var savedWr = WorldMapService.WowRoot;
            var savedLodge = AtlasQuery.Context.Lodge; var savedLayers = settings.WorldMapLayers; var savedLast = settings.WorldMapLast;
            var savedCache = MapData.CacheFile; var savedMapRoot = MapData.WowRoot; var savedMapEnabled = MapData.Enabled;
            try
            {
                if (Directory.Exists(work)) Util.DeleteDir(work);
                Directory.CreateDirectory(work);
                WorldMapCache.RootOverride = Path.Combine(work, "cache");
                WorldMapService.ResetForTest();
                await WorldMapRealChecks(dir, work, engine, notes, Check, P);
            }
            catch (Exception e) { Check("worldmap real-data test crashed", false, e.ToString()); }
            finally
            {
                WorldMapView.ForceNoWebViewForTest = false; WorldMapView.ClipboardSeamForTest = null;
                try { WorldMapView.Dispose(); } catch (Exception) { }
                WorldMapService.CancelAll(); WorldMapService.ResetForTest();
                WorldMapService.WdtSourceForTest = savedWdt; WorldMapService.TileSourceForTest = savedTile; WorldMapCache.RootOverride = savedRoot; WorldMapService.WowRoot = savedWr;
                AtlasQuery.Context.Lodge = savedLodge; settings.WorldMapLayers = savedLayers; settings.WorldMapLast = savedLast;
                MapData.CacheFile = savedCache; MapData.WowRoot = savedMapRoot; MapData.Enabled = savedMapEnabled;
                WorldMapLayers.SavedRoutes = () => settings.SavedRoutes; WorldMapLayers.FlightTimes = FlightTimesForMap;
                try { ResetAtlasForTest(); } catch (Exception) { }
            }
            return (ok ? "worldmapreal: ok" : "worldmapreal: FAILED") + "\r\n" + string.Join("\r\n", notes);
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
                    if (SelfTestClock.ShotsOn) { await Task.Delay(1200); var p1 = await WorldMapView.CaptureForTest(); if (p1 != null) File.WriteAllBytes(P("worldmap-real-region.png"), p1); }
                    // layers on: nodes (Classic), vendors, rares ...
                    if (ci != null)
                    {
                        foreach (var name in new[] { "Gathering nodes", "Vendors", "Rare creatures" })
                            await WmEval("(function(){ var r=[].slice.call(document.querySelectorAll('.wm-row')).filter(function(e){return e.textContent.indexOf('" + name + "')===0})[0]; if (r && !r.querySelector('input').checked) r.querySelector('input').click(); return 1; })()");
                        await WmWaitCount("nodes", 1, 90000); await WmWaitCount("vendors", 1, 90000); await WmWaitCount("rares", 1, 90000);
                        var cs = await WmState();
                        Check("real game files: with the Classic baseline the node, vendor and rare layers fill in on the real region (" + Json.Write(cs.Child("counts")) + ")", cs.Child("counts").Int("nodes") > 100 && cs.Child("counts").Int("vendors") > 20 && cs.Child("counts").Int("rares") > 5, Json.Write(cs.Child("counts")));
                        await AtlasView.Open("worldmap:1?x=1550&y=-4400&z=6");
                        if (SelfTestClock.ShotsOn) { await Task.Delay(1800); var p2 = await WorldMapView.CaptureForTest(); if (p2 != null) File.WriteAllBytes(P("worldmap-real-zoomed.png"), p2); }
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
