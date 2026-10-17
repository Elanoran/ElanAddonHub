using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Maps;
using ElansAddonHub.Services.WorldMap;

namespace ElansAddonHub
{
    // --selftest "worldmap", 2.89: the open-world maps that are not under a continent UiMap (WoW Forever's Zephras Isle, the Skyborne start: map 2991 drawn by
    // the zone UiMap 2521, whose parent is the world map). They are world maps of their own: in the continent switch, with 2D tiles, 3D terrain and layers;
    // a live position on them resolves to them, and the live card's "You are on another map: <name>" offers "Show <name>". The real files are checked in
    // "worldmapreal" (WorldMapIslandsRealChecks).
    public partial class MainWindow
    {
        // the invented world with islands: Northland (a continent), Gale Isle (a zone hung under the world, map 7), Big Isle with a cove inside it (map 14: the top map
        // is the big one), and the maps that are not world maps: a test map, a map no normal UiMap shows, a transport map, a dungeon, an island with only an alternative copy
        static MapSet IslandSet(MapSet baseSet = null)
        {
            var s = baseSet ?? SynthMapSet();
            if (baseSet == null) { s.Maps.First(m => m.Id == 0).WdtFileDataId = 7001; s.Maps.First(m => m.Id == 0).MapType = 1; }
            UiMapInfo U(int id, string name, int parent, int type, int sys = 0) => new UiMapInfo { Id = id, Name = name, ParentId = parent, Type = type, Flags = 2, System = sys };
            s.UiMaps.AddRange(new[]
            {
                U(60, "Gale Isle", 1, UiMapInfo.Zone), U(61, "Gale Isle", 0, UiMapInfo.Zone, 1),
                U(62, "Big Isle", 1, UiMapInfo.Zone), U(63, "Small Cove", 62, UiMapInfo.Zone),
                U(64, "Collin's Test", 1, UiMapInfo.Zone), U(65, "Tram", 0, UiMapInfo.Zone), U(66, "Island Cellar", 0, UiMapInfo.Dungeon), U(67, "Hidden Isle", 0, UiMapInfo.Zone, 1),
            });
            s.Maps.AddRange(new[]
            {
                new GameMapInfo { Id = 7, Name = "Gale Isle", Directory = "7", InstanceType = 0, MapType = 1, WdtFileDataId = 7007 },
                new GameMapInfo { Id = 14, Name = "Big Isle", Directory = "14", InstanceType = 0, MapType = 1, WdtFileDataId = 7014 },
                new GameMapInfo { Id = 42, Name = "Collin's Test", Directory = "Collin", InstanceType = 0, MapType = 2, WdtFileDataId = 7042 },
                new GameMapInfo { Id = 43, Name = "Testing Isle", Directory = "TEST_04", InstanceType = 0, MapType = 1, WdtFileDataId = 7043 },
                new GameMapInfo { Id = 2980, Name = "Dalaran City", Directory = "2980", InstanceType = 0, MapType = 1, WdtFileDataId = 7090 },
                new GameMapInfo { Id = 369, Name = "Deeprun Tram", Directory = "DeeprunTram", InstanceType = 0, MapType = 2, WdtFileDataId = 7369 },
                new GameMapInfo { Id = 66, Name = "Island Cellar", Directory = "IslandCellar", InstanceType = 1, MapType = 1, WdtFileDataId = 7066 },
                new GameMapInfo { Id = 67, Name = "Hidden Isle", Directory = "67", InstanceType = 0, MapType = 1, WdtFileDataId = 7067 },
            });
            int id = 900;
            s.Assignments.AddRange(new[]
            {
                Asg(id++, 60, 7, -1000, 1000, -2000, 2000), Asg(id++, 61, 7, -1000, 1000, -2000, 2000),
                Asg(id++, 63, 14, 0, 200, 0, 200), Asg(id++, 62, 14, -500, 500, -500, 500),
                Asg(id++, 64, 43, 0, 100, 0, 100), Asg(id++, 65, 369, 0, 100, 0, 100), Asg(id++, 66, 66, 0, 100, 0, 100), Asg(id++, 67, 67, 0, 100, 0, 100),
            });
            return s;
        }

        // pure checks: which maps are world maps, their UiMap, a position on them, the card's button, the new message
        void WorldMapIslandsUnitTest(CheckFn Check)
        {
            var idx = new MapIndex(IslandSet());
            var conts = WorldMapService.Continents(idx);
            string Desc(WorldContinent c) => c.Name + "/" + c.MapId + "/ui" + c.UiMapId + "/" + c.Slug + (c.IsContinent ? "/continent" : "");
            Check("islands: the world maps are the continents and the open-world maps whose top map is a zone under the world (Gale Isle, Big Isle; sorted by name); a test map, a map no normal map shows (Dalaran City), a transport map (Deeprun Tram), a dungeon and an island with only an alternative copy are not (" + string.Join(", ", conts.Select(Desc)) + ")",
                conts.Select(c => c.Name).SequenceEqual(new[] { "Big Isle", "Gale Isle", "Northland" }) && conts.Single(c => c.Name == "Northland").IsContinent && !conts.Single(c => c.Name == "Gale Isle").IsContinent
                && conts.Single(c => c.MapId == 7).UiMapId == 60 && conts.Single(c => c.MapId == 7).Slug == "gale-isle" && conts.Single(c => c.MapId == 7).WdtFileDataId == 7007,
                string.Join(", ", conts.Select(Desc)));
            Check("islands: of an island's maps the top one is its map (Big Isle, not the cove inside it, nor the alternative copy of Gale Isle)", conts.Single(c => c.MapId == 14).UiMapId == 62 && conts.Single(c => c.MapId == 7).UiMapId == 60);
            Check("islands: the continent rules are unchanged: Northland keeps its continent map and slug", conts.Single(c => c.MapId == 0).UiMapId == 10 && conts.Single(c => c.MapId == 0).Slug == "northland");
            var slugs = WorldMapIds.SlugsOf(idx);
            Check("islands: worldmap:gale-isle opens the island (its slug) and worldmap:7?x=&y= a view on it", slugs("gale-isle") == 7 && WorldMapIds.Parse("gale-isle", slugs).MapId == 7 && WorldMapIds.Parse("7?x=10&y=20&z=5", slugs).Valid);

            // a live position on the island's own map (the UiMap the game reports) is on the island's world map
            var w = idx.MapToWorld(60, 0.5, 0.5);
            bool placed = WorldMapIds.TryPlace(idx, 60, 0.25, 0.75, out var pm, out var px, out var py);
            var show = WorldMapIds.ShowOnMapId(idx, 60, null);
            Check("islands: a position on the island's map (UiMap 60) is on map 7 and that map has a world map; \"Show on world map\" from its zone page opens it (" + show + ")",
                w != null && w.Value.MapId == 7 && placed && pm == 7 && WorldMapService.Continent(idx, pm)?.Name == "Gale Isle" && show != null && show.StartsWith("worldmap:7?"), w + " " + show);
            var lp = new LivePosition { ToWorld = (ui, fx, fy) => idx.MapToWorld(ui, fx, fy) };
            lp.Update(true, true, new GamePresence.Character { Name = "Ashlain", Realm = "Realm", ClassFile = "HUNTER", HasLive = true, PosSupported = true, PosEnabled = true, PosValid = true, PosMapId = 60, PosFx = 0.5, PosFy = 0.5 }, DateTime.UtcNow);
            Check("islands: the live position on the island is a position (not 'unavailable'), on map 7", lp.State == LiveState.Ok && lp.Here.HasValue && lp.Here.Value.MapId == 7, lp.State + " " + (lp.Here.HasValue ? lp.Here.Value.MapId + "" : "-"));

            // the card: "You are on another map: Gale Isle" with "Show Gale Isle" when that map has a world map; without one, the notice only
            var now = DateTime.UtcNow;
            var here = new LiveTrail.Point { At = now, MapId = 7, X = 10, Y = 20 };
            var withShow = Json.Obj(WorldMapProtocol.Me(LiveState.Ok, "Ashlain", "#ABD473", here, new List<LiveTrail.Point> { here }, 0, now, "Gale Isle", null, true));
            var noShow = Json.Obj(WorldMapProtocol.Me(LiveState.Ok, "Ashlain", "#ABD473", here, new List<LiveTrail.Point> { here }, 0, now, null, null, false));
            var onIt = Json.Obj(WorldMapProtocol.Me(LiveState.Ok, "Ashlain", "#ABD473", here, new List<LiveTrail.Point> { here }, 7, now, "Gale Isle", null, true));
            var evil = Json.Obj(WorldMapProtocol.Me(LiveState.Ok, "Ashlain", "#ABD473", here, null, 0, now, "Gale\n" + new string('x', 200), null, true));
            Check("islands: the card on another map says \"You are on another map: Gale Isle\" and offers \"Show Gale Isle\"; a map with no world map says only that you are elsewhere; on the island itself there is no button; the name is one clean line",
                withShow.Str("state") == "elsewhere" && withShow.Str("notice") == "You are on another map: Gale Isle" && withShow.Str("show") == "Show Gale Isle"
                && noShow.Str("state") == "elsewhere" && noShow.Str("notice") == "You are on another map" && !noShow.ContainsKey("show")
                && onIt.Str("state") == "ok" && !onIt.ContainsKey("show") && evil.Str("show").Length <= 65 && !evil.Str("show").Contains("\n"),
                withShow.Str("show") + " | " + noShow.Str("notice"));
            // the sky around a floating island: a chunk whose cells are all holes has no ground. Invented tile: ground (height 500) in the west half, all-hole chunks
            // with leftover depths (-3000) in the east half, one chunk with a single hole (a cave mouth) in the ground
            AdtFile Adt(Func<int, int, bool> skyAt)
            {
                var f = new AdtFile();
                for (int iy = 0; iy < 16; iy++)
                    for (int ix = 0; ix < 16; ix++)
                    {
                        bool s = skyAt(ix, iy);
                        f.Chunks[iy * 16 + ix] = new AdtChunk { IndexX = ix, IndexY = iy, AreaId = 77, PosZ = s ? -3000 : 500, Heights = new float[AdtFile.HeightCount], Holes = s ? ulong.MaxValue : (ix == 2 && iy == 2 ? 1UL : 0) };
                    }
                return f;
            }
            var half = Adt((ix, iy) => ix >= 8);
            var plain = TerrainTile.FromAdt(30, 30, half);
            var skyT = TerrainTile.FromAdt(30, 30, half, true);
            Check("sky: without the island rule (a continent) the heights stay as the files say (-3000 under the holes); with it the samples no ground touches go to the sea floor, the shared edge keeps the ground, a cave mouth (one hole) is untouched, and the sky has no area",
                Math.Abs(plain.Z(32, 60) + 3000) < 1 && Math.Abs(skyT.Z(32, 60) - TerrainTile.SkyFloor) < 1 && Math.Abs(skyT.Z(32, 32) - 500) < 1 && Math.Abs(skyT.Z(32, 33) - TerrainTile.SkyFloor) < 1
                && Math.Abs(skyT.Z(8, 8) - 500) < 1 && skyT.Area[0 * 16 + 12] == 0 && skyT.Area[0 * 16 + 3] == 77 && plain.Area[0 * 16 + 12] == 77,
                plain.Z(32, 60) + " / " + skyT.Z(32, 60) + " / " + skyT.Z(32, 32) + " / " + skyT.Z(32, 33));
            var allSky = Adt((ix, iy) => true);
            var wt = WaterTile.FromAdt(30, 30, half, null); WorldMapTerrain.AddSkySea(wt, half);
            Check("sky: a tile with no ground at all is all sky (and half a tile is not); the chunks with no ground get a flat ocean at sea level (128 of them), so the 3D view shows the sea the map floats over",
                TerrainTile.AllSky(allSky) && !TerrainTile.AllSky(half) && wt.Layers.Count == 128 && wt.Layers.All(l => l.Kind == LiquidKind.Ocean && l.Flat && l.Height == 0 && l.Mask == ulong.MaxValue), wt.Layers.Count + "");
            // the Atlas zone list: an island's zones are grouped under the island (its own folder), not left without a continent
            var savedIdx = MapData.Index; var savedStamp = MapData.BuildStamp;
            try
            {
                MapData.SetForTest(idx, "wow_classic_beta|1.60.1.70338|0123456789abcdef");
                AtlasBrowse.ForgetForTest();
                var zr = AtlasBrowse.Run("browse:zones");
                var gale = zr.Rows.FirstOrDefault(r => r.Title == "Gale Isle"); var cove = zr.Rows.FirstOrDefault(r => r.Title == "Small Cove"); var green = zr.Rows.FirstOrDefault(r => r.Title == "Greenvale");
                Check("islands: the Atlas zone list has the island's zones under the island (Gale Isle; Small Cove under Big Isle) with a folder each, next to Northland's; the test map's zone is not listed",
                    gale != null && gale.Detail.StartsWith("Gale Isle") && cove != null && cove.Detail.StartsWith("Big Isle") && green != null && green.Detail.StartsWith("Northland")
                    && zr.Children.Select(c => c.Title).Contains("Gale Isle") && zr.Children.Select(c => c.Title).Contains("Big Isle") && !zr.Rows.Any(r => r.Title == "Collin's Test"),
                    string.Join(", ", zr.Rows.Select(r => r.Title + "=" + r.Detail)) + " | " + string.Join(",", zr.Children.Select(c => c.Title)));
            }
            finally { if (savedIdx != null) MapData.SetForTest(savedIdx, savedStamp); AtlasBrowse.ForgetForTest(); }
            Check("protocol: showmap (the card's Show <map> button) is accepted and carries nothing the page could choose", WorldMapProtocol.Parse("{\"t\":\"showmap\"}", out _)?.Type == WorldMapMessageType.ShowMap && WorldMapProtocol.Parse("{\"t\":\"showmap\",\"id\":5}", out _)?.Type == WorldMapMessageType.ShowMap);
        }

        // the page: the switch lists the island, a position on it shows the button, the button opens it with your dot, Follow me follows you onto it
        async Task WorldMapIslandPageTest(CheckFn Check, Func<string, string> P, MapSet taxi, MapIndex lidx)
        {
            bool savedOn = settings.WorldMapLivePos, savedFollow = settings.WorldMapFollow;
            var view = WorldMapView; var lp = LivePos;
            var iidx = new MapIndex(IslandSet(WmLayerSet(taxi)));
            GamePresence.Character OnIsle(double fx) => new GamePresence.Character { Name = "Ashlain", Realm = "Realm", ClassFile = "HUNTER", Class = "Hunter", Level = 10, HasLive = true, PosSupported = true, PosEnabled = true, PosValid = true, PosMapId = 60, PosFx = fx, PosFy = 0.5 };
            try
            {
                MapData.SetForTest(iidx, "wow_classic_beta|1.60.1.70338|0123456789abcdef");
                await AtlasView.Open("worldmap:northland");
                await WaitFor(() => view.PageReady && view.MapId == 0, 30000);
                await WaitFor(() => view.Continents.Count == 3, 5000);
                Check("island page: the map switch lists the islands next to the continent (" + string.Join(", ", view.Continents) + ")", view.Continents.SequenceEqual(new[] { "Big Isle", "Gale Isle", "Northland" }), string.Join(", ", view.Continents));

                // on the island while Northland is shown: the card says so and offers the island
                settings.WorldMapLivePos = true; settings.WorldMapFollow = false;
                lp.ToWorld = null; view.Live = lp;
                lp.Update(true, true, OnIsle(0.5), DateTime.UtcNow); view.PushLive(true);
                var me = await WmMeWait(m => m.Str("state") == "elsewhere" && m.Str("show").Length > 0);
                Check("island page: on Gale Isle with Northland shown, the card says \"You are on another map: Gale Isle\" with a \"Show Gale Isle\" button (" + MeText(me) + ")",
                    me != null && me.Str("notice") == "You are on another map: Gale Isle" && me.Str("show") == "Show Gale Isle" && !me.Bool("dot"), MeText(me));
                await Task.Delay(300);
                var png = await view.CaptureForTest();
                if (png != null && SelfTestClock.ShotsOn) File.WriteAllBytes(P("worldmap-island-elsewhere.png"), png);
                int shows = view.ShowMapsForTest;
                await WmMe("show");
                bool opened = await WaitFor(() => view.MapId == 7 && view.PageReady, 30000);
                var me2 = await WmMeWait(m => m.Str("state") == "ok" && m.Bool("dot"));
                Check("island page: the button opens Gale Isle (built from the game files like a continent) with your dot on it, and the switch shows it", opened && view.ShowMapsForTest == shows + 1 && me2 != null && me2.Bool("dot") && view.SelectedContinent == "Gale Isle", MeText(me2) + " map " + view.MapId);
                var zones = WorldMapLayers.Build("zones", 7);
                Check("island page: the island's zone name layer has its zone (Gale Isle)", zones.Count == 1 && zones.Json.Contains("\"Gale Isle\""), zones.Note);

                // Follow me follows you there: on Northland, following, you take the boat to the island -> the map changes by itself
                await AtlasView.Open("worldmap:northland");
                await WaitFor(() => view.PageReady && view.MapId == 0, 30000);
                var t0 = DateTime.UtcNow.AddSeconds(5);
                var onNorth = OnIsle(0.5); onNorth.PosMapId = 11;
                lp.Update(true, true, onNorth, t0); settings.WorldMapFollow = true; view.PushLive(true);
                await Task.Delay(600);
                bool stayed = view.MapId == 0;
                lp.Update(true, true, OnIsle(0.55), t0.AddSeconds(2)); view.PushLive(true);
                bool followed = await WaitFor(() => view.MapId == 7 && view.PageReady, 30000);
                var mf = await WmMeWait(m => m.Str("state") == "ok" && m.Bool("dot"));
                Check("island page: with Follow me on, arriving on the island switches the map to it by itself and your dot is there (on Northland it stayed on Northland)", stayed && followed && mf != null && mf.Bool("dot"), "map " + view.MapId + " " + MeText(mf));
                await AtlasView.Open("worldmap:northland");
                await WaitFor(() => view.PageReady && view.MapId == 0, 30000);
                lp.Update(true, true, OnIsle(0.6), t0.AddSeconds(4)); view.PushLive(true); await Task.Delay(800);
                Check("island page: ... and going back to Northland yourself while you are still on the island stays on Northland (the card offers the island)", view.MapId == 0, "map " + view.MapId);
            }
            finally
            {
                settings.WorldMapLivePos = savedOn; settings.WorldMapFollow = savedFollow;
                MapData.SetForTest(lidx, "wow_classic_beta|1.60.1.70338|0123456789abcdef");
                try { UpdateLive(); } catch (Exception) { }
                await AtlasView.Open("worldmap:northland");
                await WaitFor(() => view.PageReady && view.MapId == 0, 30000);
            }
        }

        // the real game: Zephras Isle (WoW Forever's Skyborne start) is a world map with tiles, it builds, and a position on its map resolves to it
        async Task WorldMapIslandsRealChecks(MapIndex idx, string real, List<string> notes, CheckFn Check, bool engine, Func<string, string> P)
        {
            var conts = WorldMapService.Continents(idx);
            notes.Add("note real world maps: " + string.Join(", ", conts.Select(c => c.Name + " (map " + c.MapId + ", ui " + c.UiMapId + ")")));
            var zi = idx.Set.Maps.FirstOrDefault(m => m.Id == 2991);
            if (zi == null) { notes.Add("skip real islands: this client has no map 2991 (Zephras Isle)"); return; }
            var z = conts.FirstOrDefault(c => c.MapId == 2991);
            Check("real islands: Zephras Isle (map 2991, the Skyborne start) is a world map, drawn by its own zone map (UiMap " + (z?.UiMapId ?? 0) + ") with the WDT of Map.db2 (" + zi.WdtFileDataId + ")",
                z != null && !z.IsContinent && z.UiMapId == 2521 && z.Name == "Zephras Isle" && z.Slug == "zephras-isle" && z.WdtFileDataId == zi.WdtFileDataId && z.WdtFileDataId > 0, z == null ? "missing" : z.Name + " ui " + z.UiMapId);
            Check("real islands: the maps no normal map shows (Azshara Crater, Dalaran City, Hyjal Crater, the Scarlet raid phase, Eastern Kingdoms Preserved) and test maps are not world maps; the continents stay",
                !conts.Any(c => c.MapId == 37 || c.MapId == 2980 || c.MapId == 2995 || c.MapId == 2868 || c.MapId == 3021 || c.MapId == 3104 || c.MapId == 25 || c.MapId == 42 || c.MapId == 369) && conts.Any(c => c.MapId == 0) && conts.Any(c => c.MapId == 1), string.Join(",", conts.Select(c => c.MapId)));
            if (z == null) return;
            // its world table: tiles with minimaps and terrain
            using (var casc = CascStorage.Open(real, "_classic_beta_"))
            {
                casc.ZeroFillEncrypted = true;
                var k = casc.ResolveFileIds(new HashSet<int> { z.WdtFileDataId });
                var wf = k.TryGetValue(z.WdtFileDataId, out var ck) ? WdtFile.Parse(casc.ReadByContentKey(ck)) : null;
                int mm = wf == null ? 0 : wf.Tiles.Count(t => t.MinimapFdid != 0), rt = wf == null ? 0 : wf.Tiles.Count(t => t.RootFdid != 0);
                Check("real islands: Zephras Isle's world table lists " + mm + " map tiles with minimaps and " + rt + " with terrain (the client has 72)", wf != null && wf.Ok && mm >= 40 && rt >= 40, wf?.Problem);
            }
            // a live position on its map (UiMap 2521, the one the game reports there) is on it, and "Show on world map" from its zone page opens it
            var w = idx.MapToWorld(2521, 0.5, 0.5);
            var show = WorldMapIds.ShowOnMapId(idx, 2521, null);
            Check("real islands: a position on UiMap 2521 (the middle of Zephras Isle) is on map 2991 (" + w + "), and Show on world map from the zone page opens it (" + show + ")",
                w != null && w.Value.MapId == 2991 && WorldMapIds.TryPlace(idx, 2521, 0.5, 0.5, out var pm, out _, out _) && pm == 2991 && show != null && show.StartsWith("worldmap:2991?"), w + " " + show);
            // the pictures and terrain build (the whole island: 72 tiles, seconds)
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool b1 = await WorldMapService.EnsureBuilt(2991);
            var st = WorldMapService.StateOf(2991);
            long ms1 = sw.ElapsedMilliseconds; sw.Restart();
            bool b2 = await WorldMapService.EnsureTerrain(2991);
            var tst = WorldMapService.TerrainStateOf(2991);
            var man = WorldMapCache.Built(2991); var tman = WorldMapCache.TerrainBuilt(2991);
            Check("real islands: Zephras Isle's pictures build (" + (man?.Adt ?? 0) + " tiles in " + (ms1 / 1000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s) and its 3D terrain too (" + (tman?.Tiles ?? 0) + " tiles in " + (sw.ElapsedMilliseconds / 1000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s)",
                b1 && b2 && man != null && man.Adt >= 40 && tman != null && tman.Tiles >= 40, st?.Error + " / " + tst?.Error);
            // the sky around the island: the tiles with no ground at all get no height grid (open sea in the page), the others no leftover depths under their holes
            var tr = tst?.TerrainResult;
            var tdir = WorldMapTerrain.Dir(WorldMapCache.MapDir(WorldMapCache.BuildKey, 2991));
            var grids = Directory.Exists(tdir) ? Directory.GetFiles(tdir, "*.wmt").Select(f => TerrainTile.FromBytes(File.ReadAllBytes(f))).Where(t => t != null).ToList() : new List<TerrainTile>();
            float lowest = grids.Count == 0 ? 0 : grids.Min(t => t.MinZ);
            Check("real islands: the sky around Zephras Isle is open sea, not black land: " + (tr?.Sky ?? 0) + " tiles with no ground get no height grid (" + grids.Count + " grids written), and no grid reaches the leftover depths under the holes (lowest " + Math.Round(lowest) + " yards; the files have -3,350)",
                tr != null && tr.Sky >= 20 && grids.Count >= 20 && grids.Count + tr.Sky == 72 && lowest > -1000, (tr?.Sky ?? -1) + " / " + lowest);
            var zl = WorldMapLayers.Build("zones", 2991);
            Check("real islands: the zone names layer of Zephras Isle has its zone", zl.Count >= 1 && zl.Json.Contains("Zephras Isle"), zl.Note);
            // its buildings (the 3D view's "All buildings"): the scan finds the island's houses and docks and they build like a continent's
            BuildingsService.ResetForTest();
            await BuildingsService.Prepare(2991, "all");
            var scan = BuildingsService.ScanOf(2991);
            await WaitFor(() => { var s = BuildingsService.StateOf(2991); return s != null && (s.Phase == "ask" || s.Phase == "done" || s.Phase == "error"); }, 60000);
            var bsw = System.Diagnostics.Stopwatch.StartNew();
            if (BuildingsService.StateOf(2991)?.Phase == "ask") await BuildingsService.Start(2991, "all");
            await WaitFor(() => { var s = BuildingsService.StateOf(2991); return s != null && (s.Phase == "done" || s.Phase == "error"); }, 300000);
            var bst = BuildingsService.StateOf(2991);
            Check("real islands: Zephras Isle's buildings are found (" + (scan?.Placements.Count ?? 0) + " placed, " + (scan?.Models.Count ?? 0) + " models) and made for the 3D view (" + bst?.Phase + " in " + (bsw.ElapsedMilliseconds / 1000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s)",
                scan != null && scan.Placements.Count > 10 && bst != null && bst.Phase == "done" && BuildingsService.IsReady(2991, "all"), bst?.Phase + " " + bst?.Error);
            var bl = WorldMapLayers.Build("borders", 2991);
            notes.Add("note real islands: zone borders of Zephras Isle: " + bl.Count + " (" + (bl.Note ?? "") + "), flight masters: " + WorldMapLayers.Build("fm", 2991).Count);
            if (!engine || w == null) return;

            // the page: 2D and 3D on the island (pictures with ELANSHUB_SHOTS=1)
            var I = System.Globalization.CultureInfo.InvariantCulture;
            string at = "?x=" + Math.Round(w.Value.X).ToString(I) + "&y=" + Math.Round(w.Value.Y).ToString(I);
            bool saved3d = settings.WorldMap3d; settings.WorldMap3d = false; var savedBld = settings.WorldMap3dBuildings; settings.WorldMap3dBuildings = "all";
            ShowTab("atlas"); AtlasView.History.Clear();
            await AtlasView.Open("worldmap:zephras-isle");
            bool r2 = await WaitFor(() => WorldMapView.PageReady && WorldMapView.MapId == 2991 && WorldMapView.LoadedPage == "2d", 90000);
            var st2 = await WmState();
            Check("real islands: worldmap:zephras-isle opens the 2D map of the island, the switch shows it (" + WorldMapView.SelectedContinent + " of " + string.Join(", ", WorldMapView.Continents) + "), tiles drawn (" + st2?.Int("tiles") + ")",
                r2 && WorldMapView.SelectedContinent == "Zephras Isle" && WorldMapView.Continents.Contains("Kalimdor") && st2 != null && st2.Int("tiles") > 0, "ready=" + r2);
            if (SelfTestClock.ShotsOn) { await Task.Delay(1500); var p1 = await WorldMapView.CaptureForTest(); if (p1 != null) File.WriteAllBytes(P("worldmap-zephras-2d.png"), p1); }
            await AtlasView.Open("worldmap:2991" + at + "&z=3.5&m=3");
            bool r3 = await WaitFor(() => WorldMapView.PageReady && WorldMapView.MapId == 2991 && WorldMapView.LoadedPage == "3d", 150000);
            notes.Add("note real islands: the 3D page on Zephras Isle: ready " + r3 + ", WebGL " + WorldMapView.GlOk + (WorldMapView.GlFallbackReason != null ? " (" + WorldMapView.GlFallbackReason + ")" : ""));
            Check("real islands: the 3D view of the island opens (or falls back to the flat map with a reason where this PC has no WebGL)", r3 || WorldMapView.GlFallbackReason != null, "ready=" + r3);
            if (r3 && SelfTestClock.ShotsOn)
            {
                await WmEval("window.wm3 && window.wm3.setView ? window.wm3.setView(" + LatLng(w.Value.X, w.Value.Y) + ",5200,30,48" + ") : 0");
                await Task.Delay(4000); var p3 = await WorldMapView.CaptureForTest(); if (p3 != null) File.WriteAllBytes(P("worldmap-zephras-3d.png"), p3);
            }
            // back to the flat view the next checks expect (the 3D link switched the remembered mode on)
            await AtlasView.Open("worldmap:2991" + at + "&z=3&m=2");
            await WaitFor(() => WorldMapView.PageReady && WorldMapView.LoadedPage == "2d", 60000);
            settings.WorldMap3d = saved3d; settings.WorldMap3dBuildings = savedBld;
            ResetAtlasForTest();
        }

        static string LatLng(double x, double y) { WorldMapMath.WorldToLatLng(x, y, out var lat, out var lng); var I = System.Globalization.CultureInfo.InvariantCulture; return lat.ToString("R", I) + "," + lng.ToString("R", I); }
    }
}
