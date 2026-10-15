using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Maps;

namespace ElansAddonHub
{
    // --selftest "mapdatareal": the map data and the AtlasMap control against the player's real game files (read-only; skipped when this PC has no Forever beta client).
    // Split out of "mapdata" (see SelfTestCache): runs in -Full, with -Only mapdatareal, and in changed mode when something that reads game data changes.
    public partial class MainWindow
    {
        // ---------------------------------------------------------------------------------------------------------------------
        async Task RealMapChecks(string dir, string work, CheckFn Check, List<string> notes, Func<string, string> P)
        {
            var real = Environment.GetEnvironmentVariable("ELANSHUB_WOW");
            if (string.IsNullOrEmpty(real)) real = WowLocator.Find();
            if (string.IsNullOrEmpty(real) || !Directory.Exists(Path.Combine(real, "Data", "data")) || !Directory.Exists(Path.Combine(real, "_classic_beta_")))
            {
                notes.Add("skip real game files: no WoW install with the Forever beta client on this PC");
                return;
            }
            try
            {
                var rw = Path.Combine(work, "real");
                MapData.ResetForTest();
                MapData.WowRoot = () => real; MapData.Enabled = () => true;
                MapData.CacheFile = Path.Combine(rw, "cache", "mapdata.bin");
                MapRender.DirOverride = Path.Combine(rw, "maps");
                MapRender.TileSourceForTest = null;
                MapRender.ForgetFailuresForTest();
                long m0 = GC.GetTotalMemory(true);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                MapData.Start();
                await WaitFor(() => MapData.Status == MapData.State.Ready || MapData.Status == MapData.State.Unavailable, 120000);
                long loadMs = sw.ElapsedMilliseconds;
                if (!MapData.Ready)
                {
                    notes.Add("skip real game files: " + MapData.StatusLine + " (this Outpost does not know that game build's map tables; nothing was guessed)");
                    return;
                }
                var idx = MapData.Index;
                long memIdx = GC.GetTotalMemory(true) - m0;
                string from1 = MapData.LastLoadFrom;
                Check("real game files: the map tables are read from the game files (" + MapData.StatusLine + ")", from1 == "game files" && idx.UiMapCount >= 50 && idx.MapsWithArt >= 50 && MapData.StatusLine.StartsWith("Maps: "), from1 + " " + MapData.StatusLine);
                Check("real game files: the saved index is written", File.Exists(MapData.CacheFile));
                MapData.ResetForTest(); MapData.CacheFile = Path.Combine(rw, "cache", "mapdata.bin");
                var sw2 = System.Diagnostics.Stopwatch.StartNew();
                MapData.Start();
                await WaitFor(() => MapData.Status == MapData.State.Ready || MapData.Status == MapData.State.Unavailable, 60000);
                long cacheMs = sw2.ElapsedMilliseconds;
                Check("real game files: the next start comes from the saved index (not the game files)", MapData.Ready && MapData.LastLoadFrom == "cache file" && MapData.Index.UiMapCount == idx.UiMapCount, MapData.LastLoadFrom);
                idx = MapData.Index;
                var dur = idx.Get(1411);
                idx.PictureSize(1411, out int dw, out int dh);
                Check("real game files: Durotar is uiMap 1411 under Kalimdor, a zone with a 1002 x 668 picture of 12 tiles", dur != null && dur.Name == "Durotar" && dur.ParentId == 1414 && dur.Type == UiMapInfo.Zone && dw == 1002 && dh == 668 && idx.ArtFor(1411).Tiles.Length == 12 && string.Join(" > ", idx.Breadcrumb(1411).Select(m => m.Name)) == "Azeroth > Kalimdor > Durotar", dur?.Name + " " + dw + "x" + dh);
                var rh = idx.WorldToMap(1, 340, -4686);
                Check("real game files: Razor Hill (Kalimdor x 340, y -4686) is Durotar at about 51 / 42", rh != null && rh.Value.UiMapId == 1411 && Math.Abs(rh.Value.Fx - 0.515) < 0.02 && Math.Abs(rh.Value.Fy - 0.4165) < 0.02, rh?.ToString());
                Check("real game files: Stormwind (map 0) is Stormwind City, Orgrimmar (map 1) Orgrimmar, the Crossroads is in The Barrens, a Durotar spot in Durotar (the city and neighbouring-zone rules)",
                    idx.Name(idx.WorldToMap(0, -8900, 640).Value.UiMapId) == "Stormwind City" && idx.Name(idx.WorldToMap(1, 1600, -4400).Value.UiMapId) == "Orgrimmar" && idx.Name(idx.WorldToMap(1, -441, -2596).Value.UiMapId) == "The Barrens" && idx.Name(idx.WorldToMap(1, 340, -4686).Value.UiMapId) == "Durotar");
                var w = idx.MapToWorld(1411, rh.Value.Fx, rh.Value.Fy);
                Check("real game files: map -> world gives Razor Hill back (within a yard)", w != null && w.Value.MapId == 1 && Math.Abs(w.Value.X - 340) < 1 && Math.Abs(w.Value.Y - -4686) < 1, w?.ToString());
                var kal = idx.Convert(1411, rh.Value.Fx, rh.Value.Fy, 1414);
                Check("real game files: the same spot on the Kalimdor map is inside the picture, Durotar is in the eastern half and the Azeroth world map shows Kalimdor in its left part", kal != null && kal.Value.Fx > 0.4 && kal.Value.Fx < 0.8 && idx.Convert(1414, 0.5, 0.5, 947) != null && idx.Convert(1414, 0.5, 0.5, 947).Value.Fx < 0.5, kal?.ToString());
                var probeWas = MapIndex.AreaAt; MapIndex.AreaAt = null; int bad = 0; foreach (var m in idx.UiMaps.Where(m => m.System == 0 && m.Type == UiMapInfo.Zone)) { var c = idx.MapToWorld(m.Id, 0.5, 0.5); if (c == null) continue; var p = idx.WorldToMap(c.Value.MapId, c.Value.X, c.Value.Y); if (p == null || p.Value.UiMapId != m.Id) bad++; } MapIndex.AreaAt = probeWas;
                Check("real game files: the centre of every zone map is shown by that zone (no other map wins on its own middle; the rectangles alone - the terrain's area rule is checked on its own)", bad == 0, bad + " zones");
                var inst = idx.Instances.Where(i => !i.Placeholder).ToList();
                var dm = idx.Instance(36); var rfc = idx.Instance(389); var av = idx.Instance(30);
                Check("real game files: instances listed from the Map table: the Deadmines and Ragefire Chasm are dungeons, Alterac Valley is a battleground with its map as floor, test leftovers are flagged", dm != null && dm.Name == "Deadmines" && dm.Kind == "Dungeon" && rfc != null && rfc.Name == "Ragefire Chasm" && av != null && av.Kind == "Battleground" && av.Floors.Any(f => f.Id == 1459) && idx.Instances.Any(i => i.Placeholder) && inst.Count >= 20, inst.Count + "");
                notes.Add("note real game files: " + idx.UiMapCount + " UiMaps (" + idx.MapsWithArt + " with art), " + idx.Set.Assignments.Count + " assignments, " + idx.Set.Arts.Count + " art sets with " + idx.Set.Arts.Sum(a => a.Tiles.Length) + " tiles, " + idx.Set.Maps.Count + " Map rows (" + idx.Set.LockedMapRows + " key-locked), " + idx.Set.Areas.Count + " areas, " + idx.Set.Floors.Count + " floor-group rows; " + inst.Count + " instances (+" + (idx.Instances.Count - inst.Count) + " test leftovers), " + idx.Instances.Count(i => i.HasMap) + " with a map in this client: " + string.Join(", ", idx.Instances.Where(i => i.HasMap).Select(i => i.Name + " [" + string.Join("/", i.Floors.Select(f => f.Name)) + "]")) + "; without art: " + string.Join(", ", idx.UiMaps.Where(m => !idx.HasArt(m.Id)).Select(m => m.Name)));
                notes.Add("note real game files: map tables read " + loadMs + " ms from the game files, " + cacheMs + " ms from the saved index (" + new FileInfo(MapData.CacheFile).Length / 1024 + " KB), about " + memIdx / 1024 + " KB in memory");
                notes.Add("note real game files: instances without a map in this client (the game ships UiMap art only for the world, continents, zones, cities and battlegrounds): " + string.Join(", ", inst.Where(i => !i.HasMap).Take(40).Select(i => i.Name)));

                // ---- render Durotar and the biggest map
                long mb = GC.GetTotalMemory(true);
                var swr = System.Diagnostics.Stopwatch.StartNew();
                var path = await RequestPath(1411);
                long durMs = swr.ElapsedMilliseconds;
                var img = path == null || path == "TIMEOUT" ? null : MapRender.Load(path) as BitmapSource;
                bool sensible = false; string stat = "";
                if (img != null)
                {
                    var px = new byte[img.PixelWidth * img.PixelHeight * 4];
                    img.CopyPixels(px, img.PixelWidth * 4, 0);
                    long opaque = 0, sum = 0; for (int i = 0; i < px.Length; i += 4) { if (px[i + 3] > 200) opaque++; sum += px[i] + px[i + 1] + px[i + 2]; }
                    double cover = opaque / (double)(img.PixelWidth * img.PixelHeight), mean = sum / (3.0 * img.PixelWidth * img.PixelHeight);
                    sensible = cover > 0.95 && mean > 25 && mean < 230;
                    stat = "opaque " + cover.ToString("0.00", CultureInfo.InvariantCulture) + ", mean " + mean.ToString("0", CultureInfo.InvariantCulture);
                }
                Check("real game files: Durotar's art renders: a 1002 x 668 PNG in the cache folder, the whole picture covered, neither black nor white (" + stat + ")", img != null && img.PixelWidth == 1002 && img.PixelHeight == 668 && sensible && path.EndsWith("_1411.png"), path + " " + stat);
                notes.Add("note real game files: Durotar rendered in " + durMs + " ms (CASC opened, 12 BLP tiles decoded, PNG " + (path != null && File.Exists(path) ? new FileInfo(path).Length / 1024 : 0) + " KB), composed bitmap " + MapRender.LastPeakBytes / 1024 + " KB, managed memory +" + (GC.GetTotalMemory(true) - mb) / 1024 + " KB afterwards");
                // the explored-area overlays on the real files: Durotar has them, and its picture is not the base art alone
                var durArt = idx.ArtFor(1411);
                Check("real game files: the overlay tables are read in the verified layouts (" + (MapData.OverlayProblem ?? "no problem") + "): Durotar's art has overlays with tiles, and so do most arts",
                    MapData.OverlayProblem == null && idx.Set.HasOverlays && durArt.Overlays.Length >= 5 && durArt.Overlays.All(o => o.Tiles.Length > 0 && o.Width > 0 && o.Height > 0) && idx.Set.Arts.Count(a => a.Overlays.Length > 0) >= 20, durArt.Overlays.Length + " overlays");
                if (img != null && durArt.Overlays.Length > 0)
                {
                    using (var casc = CascStorage.Open(real, MapData.Flavor() ?? "_classic_beta_"))
                    {
                        var want = new HashSet<int>(durArt.Tiles.Select(t => t.FileDataId)); foreach (var o in durArt.Overlays) foreach (var t in o.Tiles) want.Add(t.FileDataId);
                        var keys = casc.ResolveFileIds(want);
                        MapRender.TileImage Real(int fid) { if (!keys.TryGetValue(fid, out var ck)) return null; var b = casc.ReadByContentKey(ck); if (b == null) return null; var q = Blp.Decode(b, out int tw, out int th); return new MapRender.TileImage { Bgra = q, Width = tw, Height = th }; }
                        var baseOnly = MapRender.Compose(new MapArtInfo { Id = durArt.Id, Layers = durArt.Layers, Tiles = durArt.Tiles }, Real, out int bw1, out int bh1, out _);
                        var full = MapRender.Compose(durArt, Real, out _, out _, out _, out int ovMissing);
                        long diff = 0; for (int i = 0; i < baseOnly.Length; i += 4) if (Math.Abs(baseOnly[i] - full[i]) + Math.Abs(baseOnly[i + 1] - full[i + 1]) + Math.Abs(baseOnly[i + 2] - full[i + 2]) > 12) diff++;
                        double share = diff / (double)(bw1 * bh1);
                        var cached = new byte[img.PixelWidth * img.PixelHeight * 4]; img.CopyPixels(cached, img.PixelWidth * 4, 0);
                        long same = 0; for (int i = 0; i < cached.Length; i += 4) if (cached[i] == full[i] && cached[i + 1] == full[i + 1] && cached[i + 2] == full[i + 2]) same++;
                        Check("real game files: Durotar's composed picture is not the base art alone (" + (share * 100).ToString("0", CultureInfo.InvariantCulture) + " % of its pixels change: the explored areas), no overlay tile missing, and the picture in the cache folder is that composition",
                            share > 0.10 && ovMissing == 0 && same > 0.99 * (img.PixelWidth * img.PixelHeight), share.ToString("0.00", CultureInfo.InvariantCulture) + " missing " + ovMissing + " same " + same);
                    }
                }
                var biggest = idx.UiMaps.Where(m => idx.HasArt(m.Id)).OrderByDescending(m => { idx.PictureSize(m.Id, out int a, out int b); return (long)a * b; }).First();
                idx.PictureSize(biggest.Id, out int bw, out int bh);
                var swb = System.Diagnostics.Stopwatch.StartNew();
                var bpath = await RequestPath(biggest.Id);
                notes.Add("note real game files: the biggest map, " + biggest.Name + " (" + bw + " x " + bh + ", " + idx.ArtFor(biggest.Id).Tiles.Count(t => t.Layer == idx.ArtFor(biggest.Id).BaseLayer.Index) + " tiles) rendered in " + swb.ElapsedMilliseconds + " ms, composed bitmap " + MapRender.LastPeakBytes / 1024 / 1024 + " MB");
                Check("real game files: the biggest map renders too (" + biggest.Name + " " + bw + " x " + bh + ")", bpath != null && bpath != "TIMEOUT" && File.Exists(bpath));
                int renderedAll = 0;
                var swAll = System.Diagnostics.Stopwatch.StartNew();
                var allPaths = await Task.WhenAll(idx.UiMaps.Where(m => idx.HasArt(m.Id) && m.System == 0).Select(m => RequestPath(m.Id)));   // one batch: the game's file list is read once for all of them
                renderedAll = allPaths.Count(pp => pp != null && pp != "TIMEOUT");
                Check("real game files: every map of the index with art renders (" + renderedAll + " of " + idx.UiMaps.Count(m => idx.HasArt(m.Id) && m.System == 0) + " in " + swAll.ElapsedMilliseconds + " ms; a map whose tiles are not in the files is reported, not a failure of the test)", renderedAll >= idx.UiMaps.Count(m => idx.HasArt(m.Id) && m.System == 0) - 2, renderedAll + "");

                // ---- the control on the real map, with markers placed from world coordinates
                var map = new AtlasMap();
                var host = new Border { Width = 892, Height = 632, Padding = new Thickness(16) };
                host.SetResourceReference(Border.BackgroundProperty, "Brush.Surface1");
                host.Child = map;
                RenderHost(host);
                map.ShowMap(1411);
                bool ready = await WaitFor(() => map.State == AtlasMap.ViewState.Ready);
                RenderHost(host);
                var spots = new (int map, double x, double y, MarkerKind k, string label, string tip, string link)[]
                {
                    (1, 340, -4686, MarkerKind.Vendor, "Razor Hill", "Innkeeper Grosk, Trader and Weaponsmith", "npc:3321"),
                    (1, -840, -4950, MarkerKind.Vendor, "Sen'jin Village", "Innkeeper Kimlya", "npc:6928"),
                    (1, 1560, -4420, MarkerKind.Quest, "Orgrimmar gate", "Lazy Peons waits here", "quest:5441"),
                    (1, -620, -4180, MarkerKind.Node, "Copper Vein", "Mining 1", "obj:1731"),
                    (1, 20, -3820, MarkerKind.Find, "Battered Chest", "Opened 14 times", "obj:2744"),
                };
                var ms = new List<AtlasMarker>();
                foreach (var s in spots) { var f = map.WorldToFraction(s.map, s.x, s.y); if (f != null) ms.Add(new AtlasMarker(f.Value.X, f.Value.Y, s.k, s.label, s.tip, s.link)); }
                var rnd = new Random(11);
                var camp = map.WorldToFraction(1, -320, -4310).Value;      // a camp of scouts: the spawns overlap into one numbered marker
                for (int i = 0; i < 9; i++) ms.Add(new AtlasMarker(camp.X + (rnd.NextDouble() - 0.5) * 0.012, camp.Y + (rnd.NextDouble() - 0.5) * 0.012, MarkerKind.Spawn, "Razormane Scout " + (i + 1), "Level 7 · patrols the camp", "npc:3114"));
                for (int i = 0; i < 6; i++) ms.Add(new AtlasMarker(0.2 + rnd.NextDouble() * 0.6, 0.5 + rnd.NextDouble() * 0.4, MarkerKind.Spawn, "Dire Mottled Boar", "Level 3", "npc:3100"));
                map.AddMarkers(ms);
                Check("real game files: the control shows Durotar with the markers converted from world coordinates (5 places + a camp + boars), Razor Hill at 51 / 42 of the picture", ready && map.MarkerCount == ms.Count && ms.Count >= 16 && Math.Abs(ms[0].Fx - 0.515) < 0.02 && Math.Abs(ms[0].Fy - 0.4165) < 0.02 && string.Join(" > ", map.BreadcrumbForTest) == "Azeroth > Kalimdor > Durotar", ms.Count + " " + ms[0].Fx);
                map.HoverAtForTest(map.FractionToViewport(ms[0].Fx, ms[0].Fy));
                RenderHost(host);
                SnapshotSheet(host, P("23-mapdata-durotar.png"));
                map.ZoomToFraction(camp.X, camp.Y, 3);
                map.HoverAtForTest(new Point(1, 1));
                RenderHost(host);
                SnapshotSheet(host, P("23-mapdata-durotar-zoomed.png"));
                map.ClearMarkers();
                map.ShowMap(1414);
                ready = await WaitFor(() => map.State == AtlasMap.ViewState.Ready && map.UiMapId == 1414);
                var kms = new List<AtlasMarker>();
                foreach (var s in spots) { var f = map.WorldToFraction(s.map, s.x, s.y); if (f != null) kms.Add(new AtlasMarker(f.Value.X, f.Value.Y, s.k, s.label, s.tip, s.link)); }
                map.AddMarkers(kms);
                RenderHost(host);
                SnapshotSheet(host, P("23-mapdata-kalimdor.png"));
                Check("real game files: the same world positions on the Kalimdor map (a continent) are all on the picture", ready && kms.Count == spots.Length && kms.All(k => k.Fx > 0 && k.Fx < 1 && k.Fy > 0 && k.Fy < 1), kms.Count + "");
                map.ClearMarkers();
                map.ShowInstance(30);
                ready = await WaitFor(() => map.State == AtlasMap.ViewState.Ready);
                map.AddMarkers(new[] { new AtlasMarker(0.42, 0.38, MarkerKind.Spawn, "Drek'Thar", "Horde general", "npc:11946"), new AtlasMarker(0.45, 0.22, MarkerKind.Vendor, "Armorer", "", "npc:13176") });
                RenderHost(host);
                SnapshotSheet(host, P("23-mapdata-alterac.png"));
                Check("real game files: a battleground (Alterac Valley, map 30) opens through ShowInstance", ready && map.UiMapId == 1459);
                // the Deadmines has no picture in this client: the control draws a plan from the game files (the dungeonmaps section checks the plans themselves)
                var savedPlans = ElansAddonHub.Services.DungeonMaps.DungeonPlans.RootOverride;
                ElansAddonHub.Services.DungeonMaps.DungeonPlans.RootOverride = Path.Combine(work, "dungeonmaps");
                try
                {
                    map.ShowInstance(36);
                    bool drawn = await WaitFor(() => map.State == AtlasMap.ViewState.Ready || map.State == AtlasMap.ViewState.NoArt, 120000);
                    RenderHost(host);
                    SnapshotSheet(host, P("23-mapdata-deadmines.png"));
                    Check("real game files: the Deadmines has no picture in this client: the control shows a plan drawn from the game files instead, captioned so", drawn && map.State == AtlasMap.ViewState.Ready && map.DrawnPlan != null && (map.CaptionForTest ?? "").Contains("from your game files"), map.State + " " + map.StateText);
                }
                finally { ElansAddonHub.Services.DungeonMaps.DungeonPlans.RootOverride = savedPlans; ElansAddonHub.Services.DungeonMaps.DungeonPlans.ResetForTest(); }
            }
            catch (Exception e) { notes.Add("FAIL real game files: " + e); Check("real game files: no exception", false, e.Message); }
            finally
            {
                MapData.ResetForTest();
            }
        }
    }
}
