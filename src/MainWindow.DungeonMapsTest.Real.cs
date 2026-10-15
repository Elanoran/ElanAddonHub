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
                DungeonPlans.ResetForTest();
                await DungeonPlans.Request(36);
                Check("real: the second look comes from the data folder", DungeonPlans.StateOf(36)?.Phase == "done" && DungeonPlans.BuildCountForTest == 0);

                // the instance list's labels on the real tables
                var cash = idx.Instance(29); var dfc = idx.Instance(2784); var dead = idx.Instance(36); var hyjal = idx.Instance(2995); var av = idx.Instance(30);
                var events = idx.Instances.Where(i => i.Event).Select(i => i.Name).OrderBy(n => n).ToList();
                Check("real instances: CashTest is a leftover, Demon Fall Canyon and Hyjal Crater are events / unreleased, the Deadmines and Alterac Valley are not",
                    cash != null && cash.Placeholder && dfc != null && dfc.Event && hyjal != null && hyjal.Event && dead != null && !dead.Event && !dead.Placeholder && av != null && !av.Event, string.Join(", ", events));
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
