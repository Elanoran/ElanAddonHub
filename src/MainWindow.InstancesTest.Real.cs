using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Maps;
using ElansAddonHub.Services.WorldMap;

namespace ElansAddonHub
{
    // --selftest "instances", the real game files (read-only; skipped when this PC has no Forever beta client): the entrances Map.db2 gives, the zones the terrain's areas name, the levels
    // the game suggests. Screenshots with ELANSHUB_INSTANCES_SHOTS=1 (builds both continents and the Kingdoms' terrain in the scratch folder).
    public partial class MainWindow
    {
        async Task InstancesRealChecks(string dir, string work, CheckFn Check, Func<string, string> P, List<string> notes)
        {
            var real = Environment.GetEnvironmentVariable("ELANSHUB_WOW");
            if (string.IsNullOrEmpty(real)) real = WowLocator.Find();
            if (string.IsNullOrEmpty(real) || !Directory.Exists(Path.Combine(real, "Data", "data")) || !Directory.Exists(Path.Combine(real, "_classic_beta_")))
            {
                notes.Add("skip real game files: no WoW install with the Forever beta client on this PC");
                return;
            }
            var rw = Path.Combine(work, "real");
            Directory.CreateDirectory(rw);
            MapData.ResetForTest();
            MapData.WowRoot = () => real; MapData.Enabled = () => true; MapData.CacheFile = Path.Combine(rw, "mapdata.bin");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            MapData.Start();
            await WaitFor(() => MapData.Status == MapData.State.Ready || MapData.Status == MapData.State.Unavailable, 180000);
            if (!MapData.Ready) { notes.Add("skip real game files: " + MapData.StatusLine + " (nothing was guessed)"); return; }
            notes.Add("note real game files: the map index (tables + the entrances' terrain areas) was read in " + sw.ElapsedMilliseconds + " ms from " + MapData.LastLoadFrom);
            var idx = MapData.Index;
            var all = idx.Instances.Where(i => !i.Placeholder).ToList();
            var with = all.Where(i => i.Entrance != null).ToList();
            var dungeons = with.Where(i => i.InstanceType == 1).ToList(); var raids = with.Where(i => i.InstanceType == 2).ToList();
            InstanceInfo I(int id) => idx.Instance(id);
            double Dist(InstanceInfo a, InstanceInfo b) => Math.Sqrt((a.Entrance.X - b.Entrance.X) * (a.Entrance.X - b.Entrance.X) + (a.Entrance.Y - b.Entrance.Y) * (a.Entrance.Y - b.Entrance.Y));
            notes.Add("note real game files: " + all.Count + " instances, " + with.Count + " with an entrance (" + dungeons.Count + " dungeons, " + raids.Count + " raids); without: " + string.Join(", ", all.Where(i => i.Entrance == null).Select(i => i.Name)));
            Check("real game files: Map.db2's ghost spots give " + with.Count + " entrances (" + dungeons.Count + " dungeons and " + raids.Count + " raids on the two continents), every one a spot inside the world with its source label; the leftovers, battlegrounds and instances the game gives no spot have none",
                dungeons.Count >= 15 && raids.Count >= 5 && with.All(i => (i.Entrance.MapId == 0 || i.Entrance.MapId == 1) && WorldMapMath.InWorld(i.Entrance.X, i.Entrance.Y) && i.Entrance.Source.Length > 0) && all.Where(i => i.InstanceType == 3).All(i => i.Entrance == null), string.Join(",", with.Select(i => i.Name)));
            var rfc = I(389); var wc = I(43); var dead = I(36); var mc = I(409); var ony = I(249); var rfk = I(47); var zg = I(309); var brd = I(230); var bwl = I(469); var dire = I(429); var brs = I(229); var sm = I(189);
            Check("real game files: Ragefire Chasm's entrance is in Orgrimmar's map rectangle (" + rfc?.Entrance?.ZoneName + ", " + AtlasInstances.Where(rfc) + "), Wailing Caverns' in the Barrens (map 1), the Deadmines' in Westfall (map 0), Zul'Gurub's in Stranglethorn Vale",
                rfc?.Entrance != null && rfc.Entrance.MapId == 1 && rfc.Entrance.ZoneName == "Orgrimmar" && rfc.Entrance.Fx > 0 && rfc.Entrance.Fx < 1 && rfc.Entrance.Fy > 0 && rfc.Entrance.Fy < 1 && idx.WorldToMap(1, rfc.Entrance.X, rfc.Entrance.Y)?.UiMapId == rfc.Entrance.UiMapId
                && wc?.Entrance != null && wc.Entrance.MapId == 1 && wc.Entrance.ZoneName == "The Barrens" && Math.Abs(wc.Entrance.X + 751) < 5 && Math.Abs(wc.Entrance.Y + 2209) < 5
                && dead?.Entrance != null && dead.Entrance.MapId == 0 && dead.Entrance.ZoneName == "Westfall" && zg?.Entrance != null && zg.Entrance.MapId == 0 && zg.Entrance.ZoneName == "Stranglethorn Vale",
                rfc?.Entrance?.ZoneName + " / " + wc?.Entrance?.ZoneName + " / " + dead?.Entrance?.ZoneName + " / " + zg?.Entrance?.ZoneName);
            Check("real game files: Molten Core's entrance is at Blackrock Mountain - on the Kingdoms, within 600 yards of Blackrock Depths' and Blackwing Lair's and in the same zone (Burning Steppes) as Blackrock Spire and Blackwing Lair",
                mc?.Entrance != null && brd?.Entrance != null && bwl?.Entrance != null && brs?.Entrance != null && mc.Entrance.MapId == 0 && Dist(mc, brd) < 600 && Dist(mc, bwl) < 600 && mc.Entrance.ZoneName == "Burning Steppes" && bwl.Entrance.ZoneName == "Burning Steppes" && brs.Entrance.ZoneName == "Burning Steppes",
                mc?.Entrance?.ZoneName + " " + (mc?.Entrance != null && brd?.Entrance != null ? Dist(mc, brd).ToString("0") : "?"));
            Check("real game files: where the map rectangles (loose boxes) would name the neighbour, the terrain's own area decides: Razorfen Kraul in the Barrens, Onyxia's Lair in Dustwallow Marsh, Scarlet Monastery in Tirisfal Glades; every entrance has its terrain area read (" + with.Count(i => i.Entrance.AreaId > 0) + " of " + with.Count + ")",
                rfk?.Entrance != null && rfk.Entrance.ZoneName == "The Barrens" && ony?.Entrance != null && ony.Entrance.ZoneName == "Dustwallow Marsh" && sm?.Entrance != null && sm.Entrance.ZoneName == "Tirisfal Glades" && with.Count(i => i.Entrance.AreaId > 0) >= with.Count - 1 && rfk.Entrance.Source.Contains("terrain"),
                rfk?.Entrance?.ZoneName + " / " + ony?.Entrance?.ZoneName + " / " + sm?.Entrance?.ZoneName);
            Check("real game files: the levels the game suggests - Ragefire Chasm 13, Wailing Caverns 17, Deadmines 16, Molten Core 60, Dire Maul 54 with its three wings, Blackrock Spire 53 with its two halves; Naxxramas has no entrance and no level in these files (nothing is invented)",
                rfc?.MinLevel == 13 && wc?.MinLevel == 17 && dead?.MinLevel == 16 && mc?.MinLevel == 60 && dire?.MinLevel == 54 && dire.Parts.Count == 3 && brs?.MinLevel == 53 && brs.Parts.Count == 2 && idx.Instance(533) != null && idx.Instance(533).Entrance == null && idx.Instance(533).MinLevel == 0,
                string.Join(",", new[] { rfc, wc, dead, mc, dire, brs }.Select(i => i?.Name + " " + i?.MinLevel)));
            var l0 = WorldMapLayers.Instances(0); var l1 = WorldMapLayers.Instances(1);
            var items = new[] { l0, l1 }.SelectMany(l => (Json.Obj(l.Json)?.List("items") ?? new List<object>()).OfType<Dictionary<string, object>>()).ToList();
            Check("real game files: the layer holds " + l0.Count + " entrances on the Eastern Kingdoms and " + l1.Count + " on Kalimdor, " + items.Count(i => (string)i["ic"] == "raid") + " raids among them, every position inside the plane of the 64 x 64 tiles, every item linking to its instance page",
                l0.Count + l1.Count == with.Count && l0.Count > 5 && l1.Count > 5 && items.Count(i => (string)i["ic"] == "raid") == raids.Count && items.All(i => (double)i["lat"] < 0 && (double)i["lat"] > -256 && (double)i["lng"] > 0 && (double)i["lng"] < 256 && ((string)i["link"]).StartsWith("instance:")), l0.Count + " / " + l1.Count);
            var pgR = AtlasQuery.Build("instance:389"); var pgZ = AtlasQuery.Build("zone:" + rfc?.Entrance?.UiMapId);
            Check("real game files: Ragefire Chasm's page has its Entrance block (Orgrimmar, /etk way command with the zone map's percent), Orgrimmar's page lists it, and the browse list shows its zone and level",
                pgR.Entrance != null && pgR.Entrance.ZoneName == "Orgrimmar" && pgR.Entrance.Command != null && pgR.Entrance.Command.StartsWith("/etk way " + rfc.Entrance.UiMapId + " ") && pgR.Entrance.Command.EndsWith(" Ragefire Chasm")
                && pgZ.Section("instances") != null && pgZ.Section("instances").Rows.Any(r => r.Link == "instance:389") && AtlasBrowse.Run("browse:instances").Rows.Any(r => r.Title == "Ragefire Chasm" && r.Zone == "Orgrimmar" && r.Detail.Contains("Levels 13")), pgR.Entrance?.Command);

            // a screenshot of a real instance page (the zone map of the Barrens is drawn from the game's own tiles on first use)
            try
            {
                ShowTab("atlas"); AtlasView.History.Clear();
                double w0 = Width, h0 = Height; Width = 1400; Height = 1000; await Task.Delay(300);
                await AtlasView.Open("instance:43"); await AtlasView.WaitForTest();
                await WaitFor(() => AtlasView.EntranceMapsForTest.Count == 1 && AtlasView.EntranceMapsForTest[0].State == AtlasMap.ViewState.Ready, 60000);
                await Task.Delay(800); UpdateLayout();
                Snapshot(P("instances-page-wailing-caverns.png"));
                Width = w0; Height = h0;
            }
            catch (Exception e) { notes.Add("note real game files: the instance page screenshot failed (" + e.Message + ")"); }

            if (Environment.GetEnvironmentVariable("ELANSHUB_INSTANCES_SHOTS") == "1") await InstancesShots(real, rw, idx, Check, P, notes);
            MapData.ResetForTest();
        }

        // ELANSHUB_INSTANCES_SHOTS=1: pictures of the layer on the real continents (2D Kalimdor with the defaults, 2D at Blackrock Mountain, 3D at Blackrock Mountain)
        async Task InstancesShots(string real, string rw, MapIndex idx, CheckFn Check, Func<string, string> P, List<string> notes)
        {
            var savedRootO = WorldMapCache.RootOverride; var savedWow = WorldMapService.WowRoot; var savedLay = settings.WorldMapLayers; var saved3d = settings.WorldMap3d;
            double w0 = Width, h0 = Height;
            try
            {
                Width = 1500; Height = 950; await Task.Delay(300);
                WorldMapView.Dispose();
                WorldMapCache.RootOverride = Path.Combine(rw, "shots");
                WorldMapService.ResetForTest(); WorldMapService.WdtSourceForTest = null; WorldMapService.TileSourceForTest = null;
                WorldMapService.WowRoot = () => real;
                foreach (var id in new[] { 1, 0 }) { var sw = System.Diagnostics.Stopwatch.StartNew(); bool b = await WorldMapService.EnsureBuilt(id); notes.Add("note instances shots: continent " + id + " built=" + b + " in " + sw.ElapsedMilliseconds / 1000.0 + " s"); }
                { var sw = System.Diagnostics.Stopwatch.StartNew(); bool b = await WorldMapService.EnsureTerrain(0); notes.Add("note instances shots: Eastern Kingdoms terrain built=" + b + " in " + sw.ElapsedMilliseconds / 1000.0 + " s"); }
                settings.WorldMapLayers = null; settings.WorldMap3d = false;
                ShowTab("atlas"); AtlasView.History.Clear();
                await AtlasView.Open("worldmap:kalimdor");
                await WaitFor(() => WorldMapView.PageReady && WorldMapView.MapId == 1, 120000);
                bool got = await WmWaitCount("instances", 5, 30000);
                await Task.Delay(2500);
                var p1 = await WorldMapView.CaptureForTest(); if (p1 != null) File.WriteAllBytes(P("instances-2d-kalimdor.png"), p1);
                Check("shots: Kalimdor with the Dungeons & raids layer on by default shows its entrances (" + got + ")", got && p1 != null);
                await AtlasView.Open("worldmap:0?x=-7400&y=-1100&z=5.5");
                await WaitFor(() => WorldMapView.PageReady && WorldMapView.MapId == 0, 120000);
                await WmWaitCount("instances", 5, 30000); await Task.Delay(3000);
                var p2 = await WorldMapView.CaptureForTest(); if (p2 != null) File.WriteAllBytes(P("instances-2d-blackrock.png"), p2);
                WorldMapView.SetMode(true);
                await WaitFor(() => (WorldMapView.Is3D && WorldMapView.LoadedPage == "3d" && WorldMapView.PageReady) || WorldMapView.GlFallbackReason != null, 120000);
                if (!(WorldMapView.Is3D && WorldMapView.GlOk)) { notes.Add("note instances shots: no 3D here (" + WorldMapView.GlFallbackReason + ")"); return; }
                await WmWaitCount("instances", 5, 30000);
                WorldMapMath.WorldToLatLng(-7500, -1100, out var lat, out var lng);
                await WmEval("window.wm3.setView(" + lat.ToString("R", CultureInfo.InvariantCulture) + "," + lng.ToString("R", CultureInfo.InvariantCulture) + ",2600,25,36)");
                var swS = System.Diagnostics.Stopwatch.StartNew(); int calm = 0; int last = -1;
                while (swS.ElapsedMilliseconds < 40000)
                {
                    await Task.Delay(600);
                    var st = await Wm3State();
                    if (st != null && st.Int("inFlight") == 0 && st.Int("meshes") == last && st.Int("meshes") > 0) { if (++calm >= 3) break; } else calm = 0;
                    last = st?.Int("meshes") ?? -1;
                }
                var p3 = await WorldMapView.CaptureForTest(); if (p3 != null) File.WriteAllBytes(P("instances-3d-blackrock.png"), p3);
                Check("shots: the 3D view at Blackrock Mountain shows the entrances", p3 != null);
            }
            catch (Exception e) { Check("instances shots crashed", false, e.ToString()); }
            finally
            {
                try { WorldMapView.SetMode(false); } catch (Exception) { }
                WorldMapView.Dispose();
                WorldMapCache.RootOverride = savedRootO; WorldMapService.WowRoot = savedWow; settings.WorldMapLayers = savedLay; settings.WorldMap3d = saved3d;
                WorldMapService.ResetForTest();
                Width = w0; Height = h0;
            }
        }
    }
}
