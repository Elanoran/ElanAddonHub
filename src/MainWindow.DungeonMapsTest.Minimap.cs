using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ElansAddonHub.Services;
using ElansAddonHub.Services.DungeonMaps;
using ElansAddonHub.Services.Maps;
using ElansAddonHub.Services.WorldMap;

namespace ElansAddonHub
{
    // --selftest "dungeonmaps", part 5: the game's own minimap pictures of the buildings laid onto the drawn floors (DungeonMinimap). The block rule (where a
    // texel lands), the index, a composed floor on a synthetic building (orientation: model x along the picture's columns, the picture upside down against model
    // y; the placement turned half way round), which groups go on which floor (a group's picture only where that floor is), missing pictures (the plan stays),
    // the plan file, and the map card's "Game minimap | Plan" switch. The real pictures: dungeonmapsreal.
    public partial class MainWindow
    {
        // a block picture whose colour says where each texel is: red for the first 64 columns, blue after; green for the last 32 rows
        static DungeonMinimap.Picture DmQuadrants(int size = 128)
        {
            var px = new byte[size * size * 4];
            for (int r = 0; r < size; r++)
                for (int c = 0; c < size; c++)
                {
                    int o = (r * size + c) * 4;
                    px[o] = (byte)(c < 64 ? 0 : 255); px[o + 1] = (byte)(r >= size - 32 ? 255 : 0); px[o + 2] = (byte)(c < 64 ? 255 : 0); px[o + 3] = 255;
                }
            return new DungeonMinimap.Picture { Bgra = px, Width = size, Height = size };
        }

        static DungeonMinimap.Picture DmSolid(byte r, byte g, byte b, int size) { var px = new byte[size * size * 4]; for (int i = 0; i < size * size; i++) { px[i * 4] = b; px[i * 4 + 1] = g; px[i * 4 + 2] = r; px[i * 4 + 3] = 255; } return new DungeonMinimap.Picture { Bgra = px, Width = size, Height = size }; }

        static WmoPlacement DmGlobalPlace(uint fdid) => new WmoPlacement { NameId = fdid, X = WdtFile.HalfWorld, Z = WdtFile.HalfWorld, Scale = 1f };

        static byte[] DmPixel(DungeonMinimap.GameFloor g, DungeonPlanImage.Frame fr, double x, double y)
        {
            int u = (int)fr.Px(y), v = (int)fr.Py(x), o = (v * g.Width + u) * 4;
            return new[] { g.Bgra[o], g.Bgra[o + 1], g.Bgra[o + 2], g.Bgra[o + 3] };
        }

        async Task DmMinimapChecks(string work, CheckFn Check, List<string> notes, string dir)
        {
            // ---- the block rule
            DungeonMinimap.TexelOf(5, 20, 0, 0, 0, 0, 128, out var c1, out var r1);
            DungeonMinimap.TexelOf(130, 140, 0, 0, 1, 1, 256, out var c2, out var r2);
            Check("block rule: 2 pixels per yard from the group box's corner; the first row is the block's high-y end (model (5, 20) is column 10, row 88 of a 128-pixel picture; block (1, 1) starts 128 yards on)",
                Math.Abs(c1 - 10) < 1e-9 && Math.Abs(r1 - 88) < 1e-9 && Math.Abs(c2 - 4) < 1e-9 && Math.Abs(r2 - 232) < 1e-9, c1 + " " + r1 + " " + c2 + " " + r2);

            // ---- the index
            var ix = WmoMinimapIndex.FromRows(new[] { (77, 0, 0, 0, 9001), (77, 0, 0, 0, 9002), (77, 1, 1, 0, 9003), (0, 0, 0, 0, 9004), (77, 0, 99, 0, 9005), (78, 2, 0, 0, 0) });
            Check("index: rows by model and group; a block given twice, a model 0, an index off the grid and a file 0 are left out", ix.Rows == 2 && ix.Wmos == 1 && ix.BlocksOf(77, 0).Count == 1 && ix.BlocksOf(77, 0)[0].Fdid == 9001 && ix.BlocksOf(77, 1)[0].X == 1 && ix.BlocksOf(78, 2).Count == 0 && ix.Has(77) && !ix.Has(78));
            var none = WmoMinimapIndex.Parse(null); var junk = WmoMinimapIndex.Parse(new byte[] { 1, 2, 3, 4 });
            Check("index: no table in the install, or garbage, is an empty index with the reason (nothing guessed)", none.Rows == 0 && none.Problem.Contains("not in this game install") && junk.Rows == 0 && junk.Problem != null, junk.Problem);

            // ---- one building, one group, one block: where the colours land
            var root = new WmoRoot { WmoId = 77 };
            root.GroupInfos.Add(new WmoGroupInfo { MinX = 0, MinY = 0, MinZ = 0, MaxX = 40, MaxY = 40, MaxZ = 5, Fdid = 1 });
            var geo = new PlanGeometry { GlobalWmo = true };
            geo.Places.Add(DmGlobalPlace(5000)); geo.Roots[5000] = root;
            var hall = DmUnit("Hall", (-40, 0, -40, 0, 0, 0)); hall.Place = 0; hall.Group = 0;
            geo.Units.Add(hall);
            var floors = DungeonFloors.Split(geo, "Test");
            var fr = DungeonPlanImage.FrameOf(floors);
            var roles = DungeonPlanImage.DrawAll(floors, fr);
            var quad = DmQuadrants();
            var one = WmoMinimapIndex.FromRows(new[] { (77, 0, 0, 0, 9001) });
            var g = DungeonMinimap.Compose(floors[0], fr, roles[0], geo, one, fd => fd == 9001 ? quad : null);
            var pRed = DmPixel(g, fr, -5, -20); var pBlue = DmPixel(g, fr, -35, -20); var pGreen = DmPixel(g, fr, -20, -5); var pDark = DmPixel(g, fr, -20, -35); var pOff = DmPixel(g, fr, -46, -20);
            Check("composed floor: the model (x 5) is red and (x 35) blue - the picture's columns run along model x; low model y is the picture's last rows (green) - it is upside down; the building is turned half way round (world = -model); off the floor nothing",
                pRed[2] > 200 && pRed[0] < 50 && pBlue[0] > 200 && pBlue[2] < 50 && pGreen[1] > 200 && pDark[1] < 50 && pOff[3] == 0 && g.Coverage > 0.95 && g.Groups == 1 && g.Blocks == 1,
                string.Join(",", pRed) + " | " + string.Join(",", pBlue) + " | " + string.Join(",", pGreen) + " | " + string.Join(",", pDark) + " | " + string.Join(",", pOff) + " cov " + g.Coverage);
            var gone = DungeonMinimap.Compose(floors[0], fr, roles[0], geo, one, fd => null);
            Check("composed floor: a block whose picture is not in the install is counted missing and covers nothing", gone.Coverage == 0 && gone.MissingBlocks == 1 && gone.Blocks == 0);

            // ---- two floors: a group's picture only where its floor is
            var root2 = new WmoRoot { WmoId = 88 };
            root2.GroupInfos.Add(new WmoGroupInfo { MinX = -90, MinY = -40, MinZ = 0, MaxX = 0, MaxY = 0, MaxZ = 20, Fdid = 1 });      // hall, ramp, landing
            root2.GroupInfos.Add(new WmoGroupInfo { MinX = -80, MinY = -40, MinZ = 20, MaxX = 0, MaxY = -15, MaxZ = 21, Fdid = 2 });   // the gallery
            var geo2 = new PlanGeometry { GlobalWmo = true };
            geo2.Places.Add(DmGlobalPlace(6000)); geo2.Roots[6000] = root2;
            var lower = DmUnit("Hall of Echoes", (0, 40, 0, 40, 0, 0), (40, 80, 0, 10, 0, 20), (80, 90, 0, 40, 20, 20)); lower.Place = 0; lower.Group = 0;
            var gallery = DmUnit("Gallery", (0, 80, 15, 40, 20, 20)); gallery.Place = 0; gallery.Group = 1;
            geo2.Units.Add(lower); geo2.Units.Add(gallery);
            var mini2 = WmoMinimapIndex.FromRows(new[] { (88, 1, 0, 0, 9101) });
            var gal = DmSolid(40, 160, 220, 256);
            var dir2 = Path.Combine(work, "minimap-two");
            var plan2 = DungeonPlans.Make(901, "Two Floors", geo2, dir2, null, mini2, fd => fd == 9101 ? gal : null);
            int upper = plan2.Floors.FindIndex(f => f.MinZ >= 19);
            var upperGame = upper < 0 || !plan2.Floors[upper].HasGame ? null : DungeonMinimap.ReadPng(Path.Combine(dir2, plan2.Floors[upper].GameFile), plan2.Floors[upper].Width, plan2.Floors[upper].Height);
            var ufr = upper < 0 ? null : new DungeonPlanImage.Frame { MaxX = plan2.Floors[upper].MaxX, MaxY = plan2.Floors[upper].MaxY, Scale = plan2.Floors[upper].Scale, Width = plan2.Floors[upper].Width, Height = plan2.Floors[upper].Height };
            byte AlphaAt(double x, double y) { int u = (int)ufr.Px(y), v = (int)ufr.Py(x); return upperGame[(v * ufr.Width + u) * 4 + 3]; }
            Check("floors: the gallery's pictures cover the floor over the hall (its own floor); where the gallery runs on the lower floor (x 60) that floor's picture keeps them, the upper one does not; the group without pictures leaves its floor to the plan",
                plan2.Floors.Count == 2 && upper >= 0 && plan2.Floors[upper].HasGame && plan2.Floors[upper].GameCoverage > 0.9 && upperGame != null && AlphaAt(20, 30) > 200 && AlphaAt(60, 30) == 0 && AlphaAt(20, 5) == 0,
                string.Join(" | ", plan2.Floors.Select(f => f.Name + " z" + f.MinZ + " game " + f.GameFile + " " + f.GameCoverage)));
            var lowerFloor = plan2.Floors[1 - Math.Max(0, upper)];
            var lowerGame = DungeonMinimap.Compose(DungeonFloors.Split(geo2, "Two Floors").OrderBy(f => f.MeanZ).First(), ufr, DungeonPlanImage.ReadPng(Path.Combine(dir2, lowerFloor.File), out _, out _), geo2, mini2, fd => fd == 9101 ? gal : null);
            byte LowAlpha(double x, double y) { int u = (int)ufr.Px(y), v = (int)ufr.Py(x); return lowerGame.Bgra[(v * ufr.Width + u) * 4 + 3]; }
            Check("floors: on the lower floor the gallery's pictures cover only the gallery's open end (x 60), never the hall under the gallery (x 20) - a group paints where it stands on that floor",
                LowAlpha(60, 30) > 200 && LowAlpha(20, 30) == 0 && lowerGame.Coverage > 0.2 && lowerGame.Coverage < 0.6 && Math.Abs(lowerFloor.GameCoverage - Math.Round(lowerGame.Coverage, 4)) < 1e-6, LowAlpha(60, 30) + " " + LowAlpha(20, 30) + " " + lowerGame.Coverage + " / " + lowerFloor.GameCoverage);

            // ---- the plan file
            File.WriteAllText(Path.Combine(dir2, "plan.json"), plan2.ToJson());
            var re = DungeonPlan.FromJson(File.ReadAllText(Path.Combine(dir2, "plan.json")), dir2);
            Check("plan file: the game picture and its coverage survive the round trip (format " + DungeonPlan.Format + ")", re != null && re.Floors[upper].GameFile == plan2.Floors[upper].GameFile && Math.Abs(re.Floors[upper].GameCoverage - plan2.Floors[upper].GameCoverage) < 1e-9 && re.Floors[1 - upper].HasGame == plan2.Floors[1 - upper].HasGame);
            File.Delete(Path.Combine(dir2, plan2.Floors[upper].GameFile));
            Check("plan file: a missing game picture makes the plan be drawn again", DungeonPlan.FromJson(File.ReadAllText(Path.Combine(dir2, "plan.json")), dir2) == null);

            // ---- the map card: Game minimap | Plan
            var savedIx = DungeonMinimap.IndexForTest; var savedLook = AtlasMap.DrawnLook;
            try
            {
                var files = DmInstall();
                files[8000] = DmRoot(new[] { 8001, 8002 }, new[] { "Hall of Echoes", "Far Crypt" }, null, 77, new[] { new[] { -90f, -40f, 0f, 0f, 0f, 20f }, new[] { -240f, -40f, 0f, -200f, 0f, 0f } });
                files[9100] = BldBlp(150, 90, 40, 256);
                DungeonMinimap.IndexForTest = () => WmoMinimapIndex.FromRows(new[] { (77, 0, 0, 0, 9100) });
                DungeonPlans.SourceForTest = id => new FuncBuildingSource(fd => files.TryGetValue(fd, out var b) ? b : null);
                DungeonPlans.RootOverride = Path.Combine(work, "cache-minimap");
                DungeonPlans.Enabled = () => true; DungeonPlans.AutoSeconds = 30; DungeonPlans.ResetForTest();
                MapData.SetForTest(DmIndex(), "wow_classic_beta|1.60.1.70338|0123456789abcdef");
                AtlasMap.DrawnLook = AtlasMap.LookGame;
                var map = new AtlasMap();
                var host = new Border { Width = 892, Height = 632, Padding = new Thickness(16) };
                host.SetResourceReference(Border.BackgroundProperty, "Brush.Surface1");
                host.Child = map;
                var win = new Window { Width = 900, Height = 660, WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false, Left = -4000, Top = -4000, Content = host, ResizeMode = ResizeMode.NoResize };
                win.Show();
                try
                {
                    await Task.Delay(150);
                    map.ShowInstance(900);
                    bool ready = await WaitFor(() => map.State == AtlasMap.ViewState.Ready && map.LookShownNowForTest.Length > 0, 20000);
                    var p = map.DrawnPlan;
                    Check("map card: a floor the game's pictures cover opens with them (the switch shows, \"Game minimap from your game files\")",
                        ready && p != null && p.Floors[0].HasGame && map.LookShownForTest && map.LookShownNowForTest == AtlasMap.LookGame && map.CaptionForTest == "Game minimap from your game files", map.State + " " + map.CaptionForTest + " " + map.LookShownNowForTest);
                    if (SelfTestClock.ShotsOn) { RenderHost(host); SnapshotSheet(host, Path.Combine(dir, "dungeonmaps-game-minimap.png")); }
                    map.ZoomBy(2); double zoomed = map.Zoom;
                    map.SelectLookForTest(AtlasMap.LookPlan);
                    bool plan = await WaitFor(() => map.LookShownNowForTest == AtlasMap.LookPlan, 8000);
                    Check("map card: Plan switches to the drawn plan in place (same floor, the zoom kept), captioned \"Drawn from your game files\"; the choice holds for the session",
                        plan && map.CaptionForTest == "Drawn from your game files" && map.DrawnFloor == 0 && Math.Abs(map.Zoom - zoomed) < 1e-6 && AtlasMap.DrawnLook == AtlasMap.LookPlan, map.CaptionForTest + " " + map.Zoom + "/" + zoomed);
                    if (SelfTestClock.ShotsOn) { RenderHost(host); SnapshotSheet(host, Path.Combine(dir, "dungeonmaps-game-plan.png")); }
                    map.SelectLookForTest(AtlasMap.LookGame);
                    bool back = await WaitFor(() => map.LookShownNowForTest == AtlasMap.LookGame, 8000);
                    File.Delete(Path.Combine(p.Dir, p.Floors[0].GameFile));
                    map.SelectFloorForTest(1); await WaitFor(() => map.DrawnFloor == 1 && map.State == AtlasMap.ViewState.Ready, 8000);
                    map.SelectFloorForTest(0);
                    bool fell = await WaitFor(() => map.DrawnFloor == 0 && map.State == AtlasMap.ViewState.Ready && map.LookShownNowForTest == AtlasMap.LookPlan, 8000);
                    Check("map card: back to Game minimap; a game picture that went missing shows the plan instead of failing", back && fell && map.CaptionForTest == "Drawn from your game files", map.LookShownNowForTest + " " + map.CaptionForTest);
                    DungeonMinimap.IndexForTest = () => null;
                    DungeonPlans.RootOverride = Path.Combine(work, "cache-minimap-none"); DungeonPlans.ResetForTest();
                    map.ShowInstance(900);
                    bool plain = await WaitFor(() => map.State == AtlasMap.ViewState.Ready && map.LookShownNowForTest.Length > 0, 20000);
                    Check("map card: an install with no minimap pictures: the plan as before, no switch", plain && !map.LookShownForTest && map.LookShownNowForTest == AtlasMap.LookPlan && map.CaptionForTest == "Drawn from your game files" && !map.DrawnPlan.Floors.Any(f => f.HasGame), map.CaptionForTest);
                }
                finally { win.Close(); }
            }
            finally { DungeonMinimap.IndexForTest = savedIx; AtlasMap.DrawnLook = savedLook; DungeonPlans.ResetForTest(); MapData.ResetForTest(); }
        }
    }
}
