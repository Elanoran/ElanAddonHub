using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.DungeonMaps;
using ElansAddonHub.Services.Maps;

namespace ElansAddonHub
{
    // --selftest "dungeonmaps", part 3: the real game files, read-only (skipped when this PC has no Forever beta client). The Deadmines (buildings placed in
    // map tiles, a filler landscape over them), Molten Core (one building) and Zul'Gurub (the terrain is the instance: minimap + interiors) are drawn in the
    // scratch folder and timed; a few known positions (where the Classic baseline puts Edwin VanCleef and Lucifron - coordinates, no data of it is used
    // here) have ground on the floor FloorAt picks; the event and placeholder labels of the real instance list. Pictures in the theme's colours go to
    // ELANSHUB_DUNGEONMAPS_SHOTS=<folder> (outside the repo), else the scratch folder. ELANSHUB_DUNGEONMAPS_ALL=1 draws every instance with no art and notes the times.
    public partial class MainWindow
    {
        async Task DmRealChecks(string dir, string work, CheckFn Check, List<string> notes)
        {
            var real = Environment.GetEnvironmentVariable("ELANSHUB_WOW");
            if (string.IsNullOrEmpty(real)) real = WowLocator.Find();
            if (string.IsNullOrEmpty(real) || !Directory.Exists(Path.Combine(real, "Data", "data")) || !Directory.Exists(Path.Combine(real, "_classic_beta_")))
            {
                notes.Add("skip real game files: no WoW install with the Forever beta client on this PC");
                return;
            }
            var rw = Path.Combine(work, "real"); Directory.CreateDirectory(rw);
            var shotEnv = Environment.GetEnvironmentVariable("ELANSHUB_DUNGEONMAPS_SHOTS");
            var shots = string.IsNullOrEmpty(shotEnv) ? Path.Combine(rw, "shots") : shotEnv; Directory.CreateDirectory(shots);
            var savedWowRoot = MapData.WowRoot; var savedEnabled = MapData.Enabled; var savedCache = MapData.CacheFile;
            try
            {
                MapData.ResetForTest();
                MapData.WowRoot = () => real; MapData.Enabled = () => true; MapData.CacheFile = Path.Combine(rw, "mapdata.bin");
                DungeonPlans.WowRoot = () => real; DungeonPlans.SourceForTest = null; DungeonPlans.Enabled = () => true; DungeonPlans.AutoSeconds = 1e9;
                DungeonPlans.RootOverride = Path.Combine(rw, "plans"); DungeonPlans.ResetForTest();
                MapData.Start();
                await WaitFor(() => MapData.Status == MapData.State.Ready || MapData.Status == MapData.State.Unavailable, 180000);
                if (!MapData.Ready) { notes.Add("skip real game files: " + MapData.StatusLine + " (nothing was guessed)"); return; }
                var idx = MapData.Index;
                var colors = AtlasMap.ThemeColors();
                var bg = ((SolidColorBrush)System.Windows.Application.Current.FindResource("Brush.Bg")).Color;
                var times = new List<string>();

                async Task<DungeonPlan> Draw(int mapId)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    await DungeonPlans.Start(mapId);
                    var st = DungeonPlans.StateOf(mapId);
                    var p = st?.Plan;
                    times.Add((st?.Name ?? mapId.ToString(CultureInfo.InvariantCulture)) + " " + sw.ElapsedMilliseconds + " ms" + (p == null ? " (" + st?.Phase + ": " + st?.Error + ")" : ", " + p.Floors.Count + " floors, " + WorldMapCacheSize(p.Bytes)));
                    if (p == null) return null;
                    var safe = new string(p.Name.Where(char.IsLetterOrDigit).ToArray());
                    for (int i = 0; i < p.Floors.Count; i++)
                    {
                        var target = Path.Combine(shots, "dungeonmap-" + safe + "-" + (i + 1) + (p.Floors[i].IsPicture ? ".jpg" : ".png"));
                        if (p.Floors[i].IsPicture) File.Copy(p.FloorPath(i), target, true);
                        else { var px = DungeonPlanImage.ReadPng(p.FloorPath(i), out var w, out var h); if (px != null) DungeonPlanImage.SaveColoured(target, px, w, h, colors, bg); }
                    }
                    return p;
                }
                bool Ground(DungeonPlan p, double x, double y, double z, out int floor)
                {
                    floor = p.FloorAt(x, y, z);
                    var f = p.Floors[floor];
                    if (f.IsPicture) return true;
                    var px = DungeonPlanImage.ReadPng(p.FloorPath(floor), out var w, out var h);
                    var fr = f.WorldToFraction(x, y);
                    int cx = (int)(fr.X * w), cy = (int)(fr.Y * h);
                    for (int dy = -4; dy <= 4; dy++) for (int dx = -4; dx <= 4; dx++)
                        {
                            int xx = cx + dx, yy = cy + dy;
                            if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                            var v = px[yy * w + xx];
                            if (v != DungeonPlanImage.Empty && v != DungeonPlanImage.Ghost) return true;
                        }
                    return false;
                }

                var dm = await Draw(36);
                Check("real Deadmines: drawn from the buildings placed in its tiles (the filler landscape over the mine left out), 2 to 6 floors, every floor a picture in the data folder",
                    dm != null && dm.Source == "buildings" && dm.TerrainRule.Contains("filler") && dm.Floors.Count >= 2 && dm.Floors.Count <= 6 && dm.Floors.All(f => !f.IsPicture && File.Exists(Path.Combine(dm.Dir, f.File))) && dm.Groups > 20,
                    dm == null ? DungeonPlans.StateOf(36)?.Error : dm.Source + " " + dm.TerrainRule + " " + string.Join(",", dm.Floors.Select(f => f.Name)));
                int fvc = -1;
                Check("real Deadmines: where Edwin VanCleef stands (-87.4, -819.9, 39.3) is ground on the floor FloorAt picks", dm != null && Ground(dm, -87.4, -819.9, 39.3, out fvc), fvc + "");
                var mc = await Draw(409);
                Check("real Molten Core: one building (the WDT's global WMO), 1 to 4 floors", mc != null && mc.Source == "building" && mc.Floors.Count >= 1 && mc.Floors.Count <= 4, mc == null ? DungeonPlans.StateOf(409)?.Error : mc.Source + " " + mc.Floors.Count);
                int flc = -1;
                Check("real Molten Core: where Lucifron stands (1000.2, -955.4, -179.6) is ground on the floor FloorAt picks (the global WMO turned half way round)", mc != null && Ground(mc, 1000.2, -955.4, -179.6, out flc), flc + "");
                var zg = await Draw(309);
                Check("real Zul'Gurub: the buildings stand on the terrain, so the first floor is the game's minimap of its tiles (\"Outside\")", zg != null && zg.Floors.Count >= 1 && zg.Floors[0].IsPicture && zg.Floors[0].Name == "Outside" && zg.TerrainRule.Contains("stand on"), zg == null ? DungeonPlans.StateOf(309)?.Error : string.Join(",", zg.Floors.Select(f => f.Name + "/" + f.Kind)));
                // ---- the game's own minimap pictures of the buildings (WMOMinimapTexture), laid onto the drawn floors
                void Shots(DungeonPlan p)
                {
                    if (p == null || !(SelfTestClock.ShotsOn || !string.IsNullOrEmpty(shotEnv))) return;
                    var safe = new string(p.Name.Where(char.IsLetterOrDigit).ToArray());
                    for (int i = 0; i < p.Floors.Count; i++)
                    {
                        var f = p.Floors[i]; if (!f.HasGame) continue;
                        var roles = DungeonPlanImage.ReadPng(p.FloorPath(i), out var w, out var h);
                        var game = DungeonMinimap.ReadPng(Path.Combine(p.Dir, f.GameFile), w, h);
                        if (roles == null || game == null) continue;
                        var bmp = DungeonMinimap.Blend(roles, game, w, h, colors);
                        var dv = new DrawingVisual();
                        using (var dc = dv.RenderOpen()) { dc.DrawRectangle(new SolidColorBrush(bg), null, new System.Windows.Rect(0, 0, w, h)); dc.DrawImage(bmp, new System.Windows.Rect(0, 0, w, h)); }
                        var rt = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32); rt.Render(dv);
                        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder(); enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rt));
                        using (var fs = File.Create(Path.Combine(shots, "dungeonmap-" + safe + "-" + (i + 1) + "-game.png"))) enc.Save(fs);
                    }
                }
                string GameLine(DungeonPlan p) => p == null ? "none" : string.Join(", ", p.Floors.Select(f => f.Name + (f.IsPicture ? " (minimap of the tiles)" : f.HasGame ? " " + Math.Round(f.GameCoverage * 100) + " %" : " plan only (" + Math.Round(f.GameCoverage * 100) + " %)")));
                Check("real game minimaps: Molten Core's drawn floors get the game's own pictures, covering nearly all of each floor", mc != null && mc.Floors.All(f => f.HasGame && f.GameCoverage > 0.9), GameLine(mc));
                Check("real game minimaps: the Deadmines' cave floors get them (its surface buildings have none: the plan shows there)", dm != null && dm.Floors.Count(f => f.HasGame) >= 2 && dm.Floors.Where(f => f.HasGame).All(f => f.GameCoverage >= DungeonMinimap.MinCoverage), GameLine(dm));
                var brd = await Draw(230);
                Check("real game minimaps: every floor of Blackrock Depths gets them", brd != null && brd.Floors.Count >= 2 && brd.Floors.All(f => f.HasGame && f.GameCoverage > 0.9), GameLine(brd));
                notes.Add("note real game minimaps: Molten Core " + GameLine(mc) + "; the Deadmines " + GameLine(dm) + "; Blackrock Depths " + GameLine(brd) + "; Zul'Gurub " + GameLine(zg));
                Shots(mc); Shots(dm); Shots(brd); Shots(zg);
                // alignment: the same composition with the frame moved 10 yards covers clearly less of the floor
                using (var casc = CascStorage.Open(real, "_classic_beta_"))
                using (var src = new Services.WorldMap.CascBuildingSource(casc))
                {
                    var mi = DungeonMinimap.Index(src);
                    var cov = new List<string>(); int with = 0, without = 0; var missing = new List<string>();
                    foreach (var inst in idx.Instances.Where(i => !i.Placeholder && !i.HasMap && (i.InstanceType == 1 || i.InstanceType == 2)))
                    {
                        var gm = idx.GameMap(inst.MapId);
                        var wb = gm == null || gm.WdtFileDataId <= 0 ? null : src.Read(gm.WdtFileDataId);
                        if (wb == null) { without++; missing.Add(inst.Name + " (no world table)"); continue; }
                        var places = DungeonGeometry.Buildings(Services.WorldMap.WdtFile.Parse(wb), src, out _);
                        int models = 0, mm = 0;
                        foreach (var fd in places.Select(x => (int)x.NameId).Distinct()) { var r = Services.WorldMap.WmoRoot.Parse(src.Read(fd)); if (!r.Ok) continue; models++; if (mi != null && mi.Has(r.WmoId)) mm++; }
                        if (mm > 0) with++; else { without++; missing.Add(inst.Name); }
                        cov.Add(inst.Name + " " + mm + "/" + models);
                    }
                    Check("real game minimaps: WMOMinimapTexture (layout 01230A50) is read: " + (mi?.Rows ?? 0) + " blocks of " + (mi?.Wmos ?? 0) + " models; " + with + " of " + (with + without) + " dungeons and raids with no map art have pictures for their buildings",
                        mi != null && mi.Rows > 50000 && with >= 40, (mi?.Problem ?? "") + " missing: " + string.Join(", ", missing));
                    notes.Add("note real game minimaps per instance (buildings with pictures / buildings): " + string.Join("; ", cov) + " | without any: " + string.Join(", ", missing));
                    var mcMap = idx.GameMap(409);
                    var geo = DungeonGeometry.Collect(Services.WorldMap.WdtFile.Parse(src.Read(mcMap.WdtFileDataId)), src, System.Threading.CancellationToken.None);
                    var floors = DungeonFloors.Split(geo, "Molten Core");
                    var fr = DungeonPlanImage.FrameOf(floors);
                    var roles = DungeonPlanImage.DrawAll(floors, fr);
                    var texs = new Dictionary<int, DungeonMinimap.Picture>();
                    DungeonMinimap.Picture Tex(int fd) { if (!texs.TryGetValue(fd, out var t)) texs[fd] = t = DungeonMinimap.Decode(src.Read(fd)); return t; }
                    var at = DungeonMinimap.Compose(floors[0], fr, roles[0], geo, mi, Tex);
                    var moved = new DungeonPlanImage.Frame { MaxX = fr.MaxX + 10, MaxY = fr.MaxY, Scale = fr.Scale, Width = fr.Width, Height = fr.Height };
                    var off = DungeonMinimap.Compose(floors[0], moved, roles[0], geo, mi, Tex);
                    var moved2 = new DungeonPlanImage.Frame { MaxX = fr.MaxX, MaxY = fr.MaxY - 10, Scale = fr.Scale, Width = fr.Width, Height = fr.Height };
                    var off2 = DungeonMinimap.Compose(floors[0], moved2, roles[0], geo, mi, Tex);
                    Check("real game minimaps: the pictures sit on Molten Core's floor (" + Math.Round(at.Coverage * 100) + " % covered); moved 10 yards north or east they cover clearly less (" + Math.Round(off.Coverage * 100) + " %, " + Math.Round(off2.Coverage * 100) + " %)",
                        at.Coverage > 0.9 && off.Coverage < at.Coverage - 0.05 && off2.Coverage < at.Coverage - 0.05 && at.MissingBlocks == 0, at.Coverage + " " + off.Coverage + " " + off2.Coverage + " missing " + at.MissingBlocks);
                }

                // ---- the creatures on the map (the Classic baseline the player downloaded: read-only; skipped when it is not on this PC)
                {
                    var cdir = Environment.GetEnvironmentVariable("ELANSHUB_CLASSICDB");      // as atlasdatareal: the player's own download, read only
                    if (string.IsNullOrEmpty(cdir)) cdir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ElansAddonHub", "atlas", "classic");
                    var dump = Directory.Exists(cdir) ? Directory.GetFiles(cdir, "ClassicDB_*.sql.gz").OrderBy(x => x, StringComparer.Ordinal).LastOrDefault() : null;
                    if (dump == null) notes.Add("skip real creatures: no Classic baseline on this PC");
                    else
                    {
                        var msw = System.Diagnostics.Stopwatch.StartNew();
                        var mobs = DungeonMobData.Read(dump, Services.Atlas.Classic.ClassicPaths.UpdatesDir(cdir), "real", System.Threading.CancellationToken.None);
                        long readMs = msw.ElapsedMilliseconds;
                        var mobFile = Path.Combine(rw, "mobs.bin"); mobs.Save(mobFile);
                        Check("real creatures: the baseline's instance tables read (" + mobs.SpawnsByMap.Sum(k => k.Value.Count) + " spawns on " + mobs.SpawnsByMap.Count + " maps, " + mobs.Encounters.Count + " encounters, " + mobs.Entrances.Count + " entrances, " + mobs.Spells.Count + " spells) in " + readMs + " ms; saved " + new FileInfo(mobFile).Length / 1024 + " KB",
                            mobs.SpawnsByMap.Sum(k => k.Value.Count) > 5000 && mobs.SpawnsByMap.Count >= 25 && mobs.Encounters.Count >= 300 && mobs.Entrances.Count >= 30 && DungeonMobData.Load(mobFile)?.Templates.Count == mobs.Templates.Count);
                        var wc = await Draw(43);
                        bool OnGrid(DungeonMobLayer l, DungeonPlan p) { var nv = p.Nav(); return nv != null && l.Route.All(pt => nv.Nearest(pt.X, pt.Y, pt.Z, 1.5, 1) >= 0); }
                        string Bosses(DungeonMobLayer l) => string.Join(", ", l.Bosses.Select(b => b.Order + ". " + b.Name + " (" + FloorNameOf(l, b) + ")"));
                        string FloorNameOf(DungeonMobLayer l, MobPack b) => b.Floor.ToString(CultureInfo.InvariantCulture);
                        msw.Restart();
                        var dml = dm == null ? null : DungeonMobLayers.Build(36, dm, mobs, DungeonMobLayers.FriendlyToAll);
                        long dmMs = msw.ElapsedMilliseconds;
                        var dmNames = new[] { "Rhahk'Zor", "Gilnid", "Mr. Smite", "Cookie", "Captain Greenskin", "Edwin VanCleef" };
                        Check("real creatures, the Deadmines: " + (dml?.Packs.Count ?? 0) + " pulls (" + (dml?.Packs.Count(p => p.Patrol) ?? 0) + " patrols) from " + (dml?.Spawns ?? 0) + " spawns; the six bosses with a spawn, in the route's order: " + (dml == null ? "" : Bosses(dml)) + "; Sneed comes with his shredder (not placed)",
                            dml != null && dmNames.All(n => dml.Bosses.Any(b => string.Equals(b.Name, n, StringComparison.OrdinalIgnoreCase))) && dml.BossesWithoutPlace.Contains("Sneed") && dml.Packs.Count > 80);
                        Check("real creatures, the Deadmines: the route walks from the entrance past every boss (" + (dml?.RouteYards ?? 0) + " yards, " + dmMs + " ms), every point on the walkable grid" + (dml != null && dml.Route.Any(x => x.Passage) ? ", " + dml.Route.Count(x => x.Passage) + " passages the plan has no floor for" : ""),
                            dml != null && dml.Unreached.Count == 0 && dml.Route.Count > 10 && dml.RouteYards > 500 && OnGrid(dml, dm), dml?.RouteNote);
                        int vf = -1;
                        var van = dml?.Bosses.FirstOrDefault(b => b.Name == "Edwin VanCleef");
                        Check("real creatures, the Deadmines: Edwin VanCleef is on the floor whose plan shows ground where he stands", van != null && Ground(dm, van.X, van.Y, van.Z, out vf) && vf == van.Floor, van?.Floor + " / " + vf);
                        var allW = dml == null ? new List<string>() : dml.Packs.SelectMany(p => p.Warnings.Select(w => w.Text)).Distinct().ToList();
                        Check("real creatures, the Deadmines: warnings from the data (the miners run for help, VanCleef calls his allies, the Blackguards are stealthed)", allW.Any(w => w.Contains("Runs for help")) && allW.Any(w => w.Contains("VanCleef") && w.Contains("Summons")) && allW.Any(w => w.Contains("Stealthed")), string.Join(" | ", allW.Take(12)));
                        msw.Restart();
                        var wcl = wc == null ? null : DungeonMobLayers.Build(43, wc, mobs, DungeonMobLayers.FriendlyToAll);
                        long wcMs = msw.ElapsedMilliseconds;
                        var wcW = wcl == null ? new List<string>() : wcl.Packs.SelectMany(p => p.Warnings.Select(w => w.Text)).Distinct().ToList();
                        Check("real creatures, Wailing Caverns: " + (wcl?.Packs.Count ?? 0) + " pulls, bosses " + (wcl == null ? "" : Bosses(wcl)) + "; the route reaches every placed boss (" + (wcl?.RouteYards ?? 0) + " yards, " + wcMs + " ms), on the grid",
                            wcl != null && wcl.Bosses.Count >= 5 && wcl.Unreached.Count == 0 && wcl.Route.Count > 10 && OnGrid(wcl, wc), wcl?.RouteNote);
                        Check("real creatures, Wailing Caverns: the Deviate Dreadfang fears (Terrify); the druids' Serpent Form (it only changes the caster) is not called a silence", wcW.Any(w => w.Contains("Fears: Terrify")) && !wcW.Any(w => w.Contains("Silences: Serpent Form")), string.Join(" | ", wcW.Take(12)));
                        notes.Add("note real creatures: Deadmines " + (dml == null ? "none" : dml.Packs.Count + " pulls, route " + dml.RouteYards + " yd, " + dmMs + " ms, plan " + WorldMapCacheSize(dm.Bytes)) + "; Wailing Caverns " + (wcl == null ? "none" : wcl.Packs.Count + " pulls, route " + wcl.RouteYards + " yd, " + wcMs + " ms, plan " + WorldMapCacheSize(wc.Bytes)) + (wcl?.RouteNote.Length > 0 ? " (" + wcl.RouteNote + ")" : ""));
                        // the map card with the real creatures (pictures only)
                        if (SelfTestClock.ShotsOn || !string.IsNullOrEmpty(shotEnv))
                        {
                            var savedData = DungeonMobs.DataForTest; var savedFriendly = DungeonMobs.FriendlyForTest;
                            DungeonMobs.DataForTest = () => mobs; DungeonMobs.FriendlyForTest = null; DungeonMobs.ResetForTest();
                            try
                            {
                                var map = new AtlasMap();
                                var host = new System.Windows.Controls.Border { Width = 1192, Height = 832, Padding = new System.Windows.Thickness(16) };
                                host.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "Brush.Surface1");
                                host.Child = map;
                                var win = new System.Windows.Window { Width = 1200, Height = 860, WindowStyle = System.Windows.WindowStyle.None, ShowInTaskbar = false, ShowActivated = false, Left = -4000, Top = -4000, Content = host, ResizeMode = System.Windows.ResizeMode.NoResize };
                                win.Show();
                                try
                                {
                                    foreach (var inst in new[] { Tuple.Create(36, dm), Tuple.Create(43, wc) })
                                    {
                                        if (inst.Item2 == null) continue;
                                        map.ShowInstance(inst.Item1);
                                        for (int fi = 0; fi < inst.Item2.Floors.Count; fi++)
                                        {
                                            if (fi > 0) map.SelectFloorForTest(fi);
                                            int want = fi;
                                            await WaitFor(() => map.State == AtlasMap.ViewState.Ready && map.DrawnFloor == want && map.MobLayerForTest != null, 60000);
                                            await System.Threading.Tasks.Task.Delay(200);
                                            RenderHost(host);
                                            SnapshotSheet(host, Path.Combine(shots, "dungeonmap-mobs-" + new string(inst.Item2.Name.Where(char.IsLetterOrDigit).ToArray()) + "-" + (fi + 1) + ".png"));
                                        }
                                    }
                                }
                                finally { win.Close(); }
                            }
                            finally { DungeonMobs.DataForTest = savedData; DungeonMobs.FriendlyForTest = savedFriendly; DungeonMobs.ResetForTest(); }
                        }
                    }
                }

                DungeonPlans.ResetForTest();
                await DungeonPlans.Request(36);
                Check("real: the second look comes from the data folder", DungeonPlans.StateOf(36)?.Phase == "done" && DungeonPlans.BuildCountForTest == 0);

                // the instance list's labels on the real tables
                var cash = idx.Instance(29); var dfc = idx.Instance(2784); var dead = idx.Instance(36); var hyjal = idx.Instance(2995); var av = idx.Instance(30);
                var events = idx.Instances.Where(i => i.Event).Select(i => i.Name).OrderBy(n => n).ToList();
                Check("real instances: CashTest is a leftover, Demon Fall Canyon is an event / unreleased, the Deadmines, Alterac Valley are not, and the old Hyjal Crater placeholder (2995) is gone or no longer an event (Forever released Mount Hyjal)",
                    cash != null && cash.Placeholder && dfc != null && dfc.Event && (hyjal == null || !hyjal.Event) && dead != null && !dead.Event && !dead.Placeholder && av != null && !av.Event, "cash " + (cash == null ? "none" : cash.Name + " ph=" + cash.Placeholder) + "; dfc " + (dfc == null ? "none" : "ev=" + dfc.Event) + "; hyjal " + (hyjal == null ? "none" : hyjal.Name + " ev=" + hyjal.Event) + "; dead " + (dead == null ? "none" : "ev=" + dead.Event + " ph=" + dead.Placeholder) + "; av " + (av == null ? "none" : "ev=" + av.Event) + " | events: " + string.Join(", ", events));
                var dfcE = dfc?.Entrance; var cvE = idx.Instance(2804)?.Entrance; var thanes = idx.Instance(3065);
                Check("real entrances: with no ghost spot, Demon Fall Canyon is placed near its map point in Ashenvale (AreaPOI) and The Crystal Vale in the middle of its area in Silithus (approximate, labelled so); The Hall of Thanes (level 13) stays a dungeon",
                    dfcE != null && dfcE.Approximate && dfcE.ZoneName == "Ashenvale" && dfcE.Source == MapIndex.PoiSourceName && cvE != null && cvE.Approximate && cvE.ZoneName == "Silithus" && thanes != null && !thanes.Event && thanes.NoGhostSpot,
                    dfcE?.ZoneName + " " + dfcE?.Source + " | " + cvE?.ZoneName + " | " + thanes?.Event);
                notes.Add("note real approximate entrances: " + string.Join("; ", idx.Instances.Where(i => i.Entrance != null && i.Entrance.Approximate).Select(i => i.Name + " near " + i.Entrance.ZoneName + " " + AtlasInstances.Pct(i.Entrance.Fx) + " / " + AtlasInstances.Pct(i.Entrance.Fy))) + "; no entrance in the game data at all: " + string.Join(", ", idx.Instances.Where(i => i.NoGhostSpot && i.Entrance == null).Select(i => i.Name)));
                notes.Add("note real instances labelled event or unreleased: " + string.Join(", ", events) + "; placeholders: " + string.Join(", ", idx.Instances.Where(i => i.Placeholder).Select(i => i.Name)));

                if (Environment.GetEnvironmentVariable("ELANSHUB_DUNGEONMAPS_ALL") == "1")
                    foreach (var i in idx.Instances.Where(i => !i.Placeholder && !i.HasMap && i.MapId != 36 && i.MapId != 409 && i.MapId != 309).ToList()) await Draw(i.MapId);
                notes.Add("note real dungeon plans (drawn in the scratch folder): " + string.Join("; ", times));
                notes.Add("note real dungeon plan pictures (theme colours) in " + shots);
            }
            finally
            {
                MapData.WowRoot = savedWowRoot; MapData.Enabled = savedEnabled; MapData.CacheFile = savedCache; DungeonPlans.AutoSeconds = 30;
            }
        }

        static string WorldMapCacheSize(long bytes) => Services.WorldMap.WorldMapCache.SizeText(bytes);
    }
}
