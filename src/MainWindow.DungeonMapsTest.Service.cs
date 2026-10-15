using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ElansAddonHub.Services;
using ElansAddonHub.Services.DungeonMaps;
using ElansAddonHub.Services.Maps;
using ElansAddonHub.Services.WorldMap;

namespace ElansAddonHub
{
    // --selftest "dungeonmaps", part 2: the plan service (estimate, draw, cache per game build, ask for a big one, failures) and the Atlas map card, on a synthetic
    // install: Gloomhold (one building, two named groups: a hall with a gallery over it, a separate crypt), Sunken Vault (one map tile of flat terrain and its
    // minimap picture), Lost Hall (its world table is not in the install).
    public partial class MainWindow
    {
        // a root ADT of flat ground: 256 MCNK with zero heights at the tile's place
        static byte[] DmAdt(int col, int row)
        {
            var ms = new MemoryStream(); var w = new BinaryWriter(ms);
            void Tag(string t) => w.Write(Encoding.ASCII.GetBytes(new string(t.Reverse().ToArray())));
            Tag("MVER"); w.Write(4); w.Write(18);
            double tile = 1600.0 / 3, cs = tile / 16;
            for (int iy = 0; iy < 16; iy++)
                for (int ix = 0; ix < 16; ix++)
                {
                    var head = new byte[128];
                    Array.Copy(BitConverter.GetBytes(ix), 0, head, 4, 4); Array.Copy(BitConverter.GetBytes(iy), 0, head, 8, 4);
                    Array.Copy(BitConverter.GetBytes((float)((32 - row) * tile - iy * cs)), 0, head, 0x68, 4);
                    Array.Copy(BitConverter.GetBytes((float)((32 - col) * tile - ix * cs)), 0, head, 0x6C, 4);
                    Tag("MCNK"); w.Write(128 + 8 + 145 * 4); w.Write(head);
                    Tag("MCVT"); w.Write(145 * 4); for (int i = 0; i < 145; i++) w.Write(0f);
                }
            w.Flush(); return ms.ToArray();
        }

        static Dictionary<int, byte[]> DmInstall()
        {
            var f = new Dictionary<int, byte[]>();
            f[7000] = DmGlobalWdt(8000);
            f[8000] = DmRoot(new[] { 8001, 8002 }, new[] { "Hall of Echoes", "Far Crypt" });
            // model axes are the world's turned half way round: the hall at world x 0..40 is model x 0..-40
            var hall = DmStacked(); var crypt = DmUnit("Far Crypt", (200, 240, 0, 40, 0, 0));
            List<DmTri> Model(PlanUnit u)
            {
                var l = new List<DmTri>();
                for (int i = 0; i < u.TriCount; i++)
                {
                    float[] P(int k) => new[] { -u.Tri[i * 9 + k * 3], -u.Tri[i * 9 + k * 3 + 1], u.Tri[i * 9 + k * 3 + 2] };
                    l.Add(new DmTri { A = P(0), B = P(1), C = P(2) });     // the turn keeps the winding: still facing up
                }
                return l;
            }
            f[8001] = DmGroup(Model(hall)); f[8002] = DmGroup(Model(crypt));
            f[7100] = BldWdt(new[] { Tuple.Create(32, 32) });
            f[1070000 + 32 * 64 + 32] = DmAdt(32, 32);
            f[WmFdid(32, 32)] = BldBlp(40, 120, 60, 16);
            return f;
        }

        static MapIndex DmIndex()
        {
            var set = new MapSet { HasMaps = true };
            set.Maps.Add(new GameMapInfo { Id = 900, Name = "Gloomhold", Directory = "Gloomhold", InstanceType = 1, MaxPlayers = 5, WdtFileDataId = 7000, CorpseMapId = 0, CorpseX = 100, CorpseY = 100 });
            set.Maps.Add(new GameMapInfo { Id = 901, Name = "Sunken Vault", Directory = "SunkenVault", InstanceType = 2, MaxPlayers = 20, WdtFileDataId = 7100, CorpseMapId = 0, CorpseX = 200, CorpseY = 200 });
            set.Maps.Add(new GameMapInfo { Id = 902, Name = "Lost Hall", Directory = "LostHall", InstanceType = 1, MaxPlayers = 5, WdtFileDataId = 7200, CorpseMapId = 0, CorpseX = 300, CorpseY = 300 });
            set.Maps.Add(new GameMapInfo { Id = 903, Name = "Quiet Barrow", Directory = "QuietBarrow", InstanceType = 1, MaxPlayers = 5, WdtFileDataId = 0, CorpseMapId = 0, CorpseX = 400, CorpseY = 400 });
            return new MapIndex(set);
        }

        async Task DmServiceChecks(string work, CheckFn Check, List<string> notes, string dir)
        {
            var files = DmInstall();
            int reads = 0;
            DungeonPlans.SourceForTest = id => new FuncBuildingSource(fd => { reads++; return files.TryGetValue(fd, out var b) ? b : null; });
            DungeonPlans.RootOverride = Path.Combine(work, "cache");
            DungeonPlans.Enabled = () => true;
            DungeonPlans.AutoSeconds = 30;
            DungeonPlans.ResetForTest();
            MapData.SetForTest(DmIndex(), "wow_classic_beta|1.60.1.70338|0123456789abcdef");

            // ---- draw, cache
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await DungeonPlans.Request(900);
            var st = DungeonPlans.StateOf(900); var plan = st?.Plan;
            Check("service: the first look at Gloomhold draws its plan in the background (" + sw.ElapsedMilliseconds + " ms): two floors (Hall of Echoes, Level 2), one picture each, in the data folder per game build",
                st != null && st.Phase == "done" && plan != null && plan.Floors.Count == 2 && plan.Floors[0].Name == "Hall of Echoes" && plan.Floors[1].Name == "Level 2" && plan.Source == "building" && plan.Groups == 2
                && plan.Floors.All(f => File.Exists(plan.FloorPath(0)) && File.Exists(Path.Combine(plan.Dir, f.File))) && plan.Dir.StartsWith(Path.Combine(work, "cache", "1.60.1.70338-01234567_v" + DungeonPlan.Format), StringComparison.OrdinalIgnoreCase) && DungeonPlans.BuildCountForTest == 1,
                st?.Phase + " " + st?.Error + " " + (plan == null ? "" : string.Join(",", plan.Floors.Select(f => f.Name)) + " " + plan.Dir));
            var fl = plan == null ? -1 : plan.FloorAt(-20, -30, 0);
            Check("service: the plan's frame puts the world where the model is turned half way round: the hall's spot (-20, -30) at height 0 is on floor 1, 20 yards up on floor 2", plan != null && fl == 0 && plan.FloorAt(-20, -30, 20) == 1, fl + "");
            await DungeonPlans.Request(900);
            Check("service: asked again, it is done at once and not drawn again", DungeonPlans.StateOf(900).Phase == "done" && DungeonPlans.BuildCountForTest == 1);
            DungeonPlans.ResetForTest();
            await DungeonPlans.Request(900);
            Check("service: after a restart the plan comes from the data folder (no file of the game read, nothing drawn)", DungeonPlans.StateOf(900).Phase == "done" && DungeonPlans.BuildCountForTest == 0 && DungeonPlans.Cached(900) != null);
            File.WriteAllText(Path.Combine(DungeonPlans.DirOf(900), "plan.json"), "{\"format\":1, garbage");
            DungeonPlans.ResetForTest();
            await DungeonPlans.Request(900);
            Check("service: a damaged plan file is drawn again", DungeonPlans.StateOf(900).Phase == "done" && DungeonPlans.BuildCountForTest == 1 && DungeonPlans.Cached(900) != null);

            // ---- a big one waits for the person; terrain gets the minimap picture
            DungeonPlans.AutoSeconds = 0; DungeonPlans.ResetForTest();
            await DungeonPlans.Request(901);
            var sv = DungeonPlans.StateOf(901);
            Check("service: a plan over the estimate limit waits (\"ask\") with the estimate in plain words, nothing drawn", sv.Phase == "ask" && sv.EstimateText.Contains("on disk") && sv.Sentence.Contains("Sunken Vault") && DungeonPlans.BuildCountForTest == 0 && DungeonPlans.Cached(901) == null, sv.Phase + " " + sv.EstimateText);
            await DungeonPlans.Request(901);
            Check("service: looking again does not start it", DungeonPlans.StateOf(901).Phase == "ask" && DungeonPlans.BuildCountForTest == 0);
            await DungeonPlans.Start(901);
            var vp = DungeonPlans.StateOf(901).Plan;
            Check("service: \"Draw the map\" draws it; a map of terrain (no building) is the game's minimap of its tiles, one \"Outside\" picture, its frame the tile's corner",
                vp != null && vp.Floors.Count == 1 && vp.Floors[0].IsPicture && vp.Floors[0].Name == "Outside" && vp.Floors[0].Width == 16 && File.Exists(vp.FloorPath(0)) && Math.Abs(vp.Floors[0].MaxX - 0) < 1e-6 && Math.Abs(vp.Floors[0].MaxY - 0) < 1e-6 && vp.Source == "minimap",
                vp == null ? DungeonPlans.StateOf(901).Error : string.Join(",", vp.Floors.Select(f => f.Name + " " + f.Kind + " " + f.Width + " " + f.MaxX + " " + f.MaxY)) + " " + vp.Source);
            DungeonPlans.AutoSeconds = 30;

            // ---- failures
            await DungeonPlans.Request(902);
            var sl = DungeonPlans.StateOf(902);
            Check("service: a map whose world table is not in the install fails with the reason; a map with no world table at all cannot be drawn", sl.Phase == "failed" && sl.Error.Contains("not in this game install") && !DungeonPlans.CanDraw(903) && DungeonPlans.CanDraw(900), sl.Phase + " " + sl.Error);
            DungeonPlans.Enabled = () => false;
            Check("service: game data switched off = nothing is drawn", !DungeonPlans.CanDraw(900));
            DungeonPlans.Enabled = () => true;

            // ---- a new game build: drawn again in its own folder, the old folder goes
            var oldDir = DungeonPlans.DirOf(900);
            MapData.SetForTest(DmIndex(), "wow_classic_beta|1.60.2.71000|fedcba9876543210");
            DungeonPlans.ResetForTest();
            Check("service: a new game build has no plan yet", DungeonPlans.Cached(900) == null);
            await DungeonPlans.Request(900);
            Check("service: a new game build draws the plan again in its own folder and removes the old build's", DungeonPlans.StateOf(900).Phase == "done" && DungeonPlans.BuildCountForTest == 1 && DungeonPlans.DirOf(900) != oldDir && !Directory.Exists(oldDir) && Directory.Exists(DungeonPlans.DirOf(900)), oldDir);

            await DmControlChecks(work, Check, notes, dir);
        }

        async Task DmControlChecks(string work, CheckFn Check, List<string> notes, string dir)
        {
            var map = new AtlasMap();
            var host = new Border { Width = 892, Height = 632, Padding = new Thickness(16) };
            host.SetResourceReference(Border.BackgroundProperty, "Brush.Surface1");
            host.Child = map;
            var win = new Window { Width = 900, Height = 660, WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false, Left = -4000, Top = -4000, Content = host, ResizeMode = ResizeMode.NoResize };
            win.Show();
            try { await DmControlInWindow(map, host, Check, notes, dir); }
            finally { win.Close(); }
        }

        async Task DmControlInWindow(AtlasMap map, Border host, CheckFn Check, List<string> notes, string dir)
        {
            await Task.Delay(150);
            // drawn the first time, with progress
            if (Directory.Exists(DungeonPlans.Root)) Util.DeleteDir(DungeonPlans.Root);
            DungeonPlans.ResetForTest(); DungeonPlans.DelayMsForTest = 120;
            map.ShowInstance(900);
            bool sawDrawing = await WaitFor(() => map.State == AtlasMap.ViewState.Drawing && map.ProgressForTest >= 0 && map.StateText.Contains("Drawing Gloomhold"), 8000);
            RenderHost(host);
            SnapshotSheet(host, Path.Combine(dir, "dungeonmaps-drawing.png"));
            string drawingText = map.StateText;
            DungeonPlans.DelayMsForTest = 0;
            bool ready = await WaitFor(() => map.State == AtlasMap.ViewState.Ready, 20000);
            RenderHost(host);
            SnapshotSheet(host, Path.Combine(dir, "dungeonmaps-gloomhold.png"));
            Check("map card: an instance with no art draws its plan the first time with a progress line (\"Drawing Gloomhold from your game files: n / 2 parts\") and a bar, then shows it",
                sawDrawing && drawingText.Contains("Drawing Gloomhold from your game files") && ready && map.DrawnPlan != null && map.UiMapId == 0, drawingText + " | " + map.State);
            Check("map card: the plan's floors are the selector (Hall of Echoes | Level 2), the first is shown, the caption says where it comes from, the breadcrumb names the instance",
                map.FloorNamesForTest.SequenceEqual(new[] { "Hall of Echoes", "Level 2" }) && map.SelectedFloorForTest == 0 && map.DrawnFloor == 0 && map.CaptionForTest == "Drawn from your game files" && map.BreadcrumbForTest.SequenceEqual(new[] { "Gloomhold" }) && map.PictureSize.Width == map.DrawnPlan.Floors[0].Width,
                string.Join("|", map.FloorNamesForTest) + " " + map.CaptionForTest + " " + string.Join(">", map.BreadcrumbForTest));
            var changed = new List<int>(); map.DrawnFloorChanged += i => changed.Add(i);
            map.SelectFloorForTest(1);
            bool f2 = await WaitFor(() => map.DrawnFloor == 1 && map.State == AtlasMap.ViewState.Ready && changed.Contains(1), 5000);
            RenderHost(host);
            SnapshotSheet(host, Path.Combine(dir, "dungeonmaps-gloomhold-2.png"));
            Check("map card: picking Level 2 shows that floor (the hall under it faintly) and tells the pins' hook", f2 && map.SelectedFloorForTest == 1);
            map.ZoomBy(2);
            Check("map card: a drawn plan zooms and pans like any map", map.Zoom > 1.9);

            // a big one asks first
            DungeonPlans.AutoSeconds = 0; DungeonPlans.ResetForTest();
            map.ShowInstance(901);
            bool ask = await WaitFor(() => map.State == AtlasMap.ViewState.Ask, 8000);
            RenderHost(host);
            SnapshotSheet(host, Path.Combine(dir, "dungeonmaps-ask.png"));
            Check("map card: a big plan waits: the estimate, \"Nothing is uploaded\", and a \"Draw the map\" button", ask && map.ButtonShownForTest && map.ButtonTextForTest == "Draw the map" && map.StateText.Contains("on disk") && map.StateText.Contains("Nothing is uploaded"), map.State + " " + map.StateText);
            map.ClickStateButtonForTest();
            bool vault = await WaitFor(() => map.State == AtlasMap.ViewState.Ready && map.DrawnPlan != null, 20000);
            RenderHost(host);
            Check("map card: \"Draw the map\" draws it; terrain is the game's minimap, captioned so, with no floor selector", vault && map.CaptionForTest == "Minimap from your game files" && map.FloorNamesForTest.Count == 0, map.State + " " + map.CaptionForTest);
            DungeonPlans.AutoSeconds = 30;

            // failures keep the old words
            map.ShowInstance(902);
            bool lost = await WaitFor(() => map.State == AtlasMap.ViewState.NoArt, 8000);
            Check("map card: when no plan can be drawn the card says what it always said (\"Lost Hall has no map in the game files\") and why", lost && map.StateText.Contains("Lost Hall has no map in the game files") && map.StateText.Contains("could not be drawn") && map.CaptionForTest == null, map.StateText);
            map.ShowInstance(903);
            Check("map card: an instance with no world table at all: the old words, nothing started", map.State == AtlasMap.ViewState.NoArt && map.StateText.Contains("Quiet Barrow has no map in the game files") && DungeonPlans.StateOf(903) == null, map.StateText);
            DungeonPlans.Enabled = () => false;
            map.ShowInstance(900);
            Check("map card: game data off: the old words", map.State == AtlasMap.ViewState.NoArt && map.StateText.Contains("Gloomhold has no map in the game files"), map.State + " " + map.StateText);
            DungeonPlans.Enabled = () => true;

            // off screen the picture is let go and comes back
            map.ShowInstance(900);
            await WaitFor(() => map.State == AtlasMap.ViewState.Ready, 8000);
            host.Child = null;
            await Task.Delay(100);
            bool released = map.ReleasedForTest && !map.PictureHeldForTest;
            host.Child = map;
            await Task.Delay(100);
            bool back = await WaitFor(() => map.State == AtlasMap.ViewState.Ready && map.PictureHeldForTest && map.DrawnPlan != null, 8000);
            Check("map card: off screen the plan's picture is let go; shown again, it comes back from the data folder", released && back, released + " " + back);
            notes.Add("note dungeonmaps: pictures of the map card in " + dir + " (dungeonmaps-*.png)");
        }
    }
}
