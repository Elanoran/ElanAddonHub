using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Maps;
using ElansAddonHub.Services.WorldMap;

namespace ElansAddonHub
{
    // --selftest "buildings", part 2: the build service on an invented continent (the world map tests' Northland): the scan, the estimate, building bridges and then everything, pause / resume /
    // cancel, nearest first, a new game build, damaged files, the host's messages, the Settings rows, the cache row leaving the buildings alone.
    public partial class MainWindow
    {
        // the invented world's files: obj0 ADTs, building roots and groups, two textures
        sealed class BldWorld
        {
            public readonly Dictionary<int, byte[]> Files = new Dictionary<int, byte[]>();
            public const int Bridge = 5000, House = 5001, Tower = 5002, Broken = 5003, InteriorOnly = 5004, BadGroup = 5005;
            public int Placements, Bridges;
            public double TowerX, TowerZ, BridgeX, BridgeZ, HouseX, HouseZ;
            public int Opens;
            public FuncBuildingSource Source() { Opens++; return new FuncBuildingSource(f => Files.TryGetValue(f, out var b) ? b : null); }
        }

        static int BldObjFdid(int col, int row) => 2000000 + row * 64 + col;
        static BldRec BldAt(uint name, uint uid, double x, double y, double z, double ry = 0, float half = 12) =>
            new BldRec { Name = name, Uid = uid, X = (float)x, Y = (float)y, Z = (float)z, Ry = (float)ry, Min = new[] { (float)x - half, (float)y, (float)z - half }, Max = new[] { (float)x + half, (float)y + 12, (float)z + half } };

        // houseGrid x houseGrid houses in tile (31, 32), every 25 yards; plus the small hand-made set (see BuildingsServiceChecks)
        static BldWorld BldMakeWorld(int houseGrid = 0)
        {
            var w = new BldWorld();
            // textures
            w.Files[7000] = BldBlp(150, 90, 50); w.Files[7001] = BldBlp(120, 120, 125);
            // bridge: 30 yards long (game x), 6 wide, one exterior group
            BldBox(-15, 15, -3, 3, -1, 1.5f, out var bv, out var bi);
            w.Files[BldWorld.Bridge] = BldRoot(new[] { -15f, -3f, -1f, 15f, 3f, 1.5f }, new uint[] { 8 }, new[] { 6000 }, new uint[] { 7000 });
            w.Files[6000] = BldGroup(0x8009, bv, bi, new byte[12]);
            // house: walls + roof (exterior) and a far away interior group that must not be drawn
            BldBox(-8, 8, -8, 8, 0, 8, out var hv, out var hi); BldBox(-9, 9, -9, 9, 8, 11, out var rv, out var ri); BldBox(-4, 4, 100, 108, 0, 5, out var iv, out var ii);
            w.Files[BldWorld.House] = BldRoot(new[] { -9f, -9f, 0f, 9f, 9f, 11f }, new uint[] { 8, 8, 0x2000 }, new[] { 6001, 6002, 6003 }, new uint[] { 7001, 0 });
            w.Files[6001] = BldGroup(0x8009, hv, hi, new byte[12]); w.Files[6002] = BldGroup(0x8009, rv, ri, Enumerable.Repeat((byte)1, 12).ToArray()); w.Files[6003] = BldGroup(0x2001, iv, ii, new byte[12]);
            // tower
            BldBox(-3, 3, -3, 3, 0, 30, out var tv, out var ti);
            w.Files[BldWorld.Tower] = BldRoot(new[] { -3f, -3f, 0f, 3f, 3f, 30f }, new uint[] { 8 }, new[] { 6004 }, new uint[] { 7001 });
            w.Files[6004] = BldGroup(0x8009, tv, ti, new byte[12]);
            // a root that is not a root, a building of interior groups only, a building whose group file is damaged
            w.Files[BldWorld.Broken] = new byte[] { 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9 };
            w.Files[BldWorld.InteriorOnly] = BldRoot(new[] { -5f, -5f, 0f, 5f, 5f, 5f }, new uint[] { 0x2000 }, new[] { 6005 }, new uint[0]); w.Files[6005] = BldGroup(0x2001, hv, hi, new byte[12]);
            w.Files[BldWorld.BadGroup] = BldRoot(new[] { -5f, -5f, 0f, 5f, 5f, 5f }, new uint[] { 8 }, new[] { 6006 }, new uint[0]); w.Files[6006] = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 };

            // the placements (scene positions; tile col c holds x in [c x 533.33, (c + 1) x 533.33], row r the same for z)
            double T = 1600.0 / 3;
            var t3031 = new List<BldRec>(); var t3131 = new List<BldRec>(); var t2931 = new List<BldRec>(); var t3331 = new List<BldRec>(); var t3232 = new List<BldRec>(); var t3132 = new List<BldRec>();
            w.BridgeX = 30 * T + 100; w.BridgeZ = 31 * T + 100;
            t3031.Add(BldAt(BldWorld.Bridge, 1, w.BridgeX, 10, w.BridgeZ));
            w.HouseX = 30 * T + 200; w.HouseZ = 31 * T + 200;
            for (int i = 0; i < 6; i++) t3031.Add(BldAt(BldWorld.House, (uint)(10 + i), 30 * T + 200 + (i % 3) * 30, 12, 31 * T + 200 + (i / 3) * 30, i * 15));
            w.TowerX = 30 * T + 400; w.TowerZ = 31 * T + 400;
            t3031.Add(BldAt(BldWorld.Tower, 30, w.TowerX, 12, w.TowerZ)); t3031.Add(BldAt(BldWorld.Tower, 31, w.TowerX + 40, 12, w.TowerZ));
            for (int i = 0; i < 4; i++) t3131.Add(BldAt(BldWorld.House, (uint)(40 + i), 31 * T + 100 + i * 30, 12, 31 * T + 100));
            t3131.Add(BldAt(BldWorld.House, 10, 30 * T + 200, 12, 31 * T + 200));             // the same placement again (a building at a tile edge is in both files): dropped
            t2931.Add(BldAt(BldWorld.Broken, 50, 29 * T + 100, 12, 31 * T + 100)); t2931.Add(BldAt(BldWorld.InteriorOnly, 51, 29 * T + 200, 12, 31 * T + 100)); t2931.Add(BldAt(BldWorld.BadGroup, 52, 29 * T + 300, 12, 31 * T + 100));
            t3331.Add(BldAt(BldWorld.Bridge, 60, 33 * T + 100, 10, 31 * T + 100));              // the same bridge shape on flat ground: a building, not a bridge
            t3232.Add(BldAt(BldWorld.House, 70, 32 * T + 100, 12, 32 * T + 100)); t3232.Add(BldAt(BldWorld.House, 71, 32 * T + 200, 12, 32 * T + 100));      // in a file with the old name table: left out
            w.Placements = 6 + 1 + 2 + 4 + 3 + 1; w.Bridges = 1;
            for (int gx = 0; gx < houseGrid; gx++) for (int gz = 0; gz < houseGrid; gz++) t3132.Add(BldAt(BldWorld.House, (uint)(1000 + gx * 100 + gz), 31 * T + 40 + gx * 25, 12, 32 * T + 40 + gz * 25, (gx * 7 + gz * 3) % 360));
            if (houseGrid > 0) w.Placements += houseGrid * houseGrid;
            w.Files[BldObjFdid(30, 31)] = BldObj(t3031.ToArray()); w.Files[BldObjFdid(31, 31)] = BldObj(t3131.ToArray()); w.Files[BldObjFdid(29, 31)] = BldObj(t2931.ToArray()); w.Files[BldObjFdid(33, 31)] = BldObj(t3331.ToArray());
            w.Files[BldObjFdid(32, 32)] = BldObj(t3232.ToArray(), true); if (houseGrid > 0) w.Files[BldObjFdid(31, 32)] = BldObj(t3132.ToArray());
            return w;
        }

        // flat ground at 10 with a gorge along x at the bridge
        static BldGround BldGorge(BldWorld w) => new BldGround { G = (x, z) => Math.Abs(z - w.BridgeZ) < 8 && Math.Abs(x - w.BridgeX) < 40 ? 0 : 10 };

        async Task BuildingsServiceChecks(string work, CheckFn Check, List<string> notes)
        {
            var I = CultureInfo.InvariantCulture;
            var sw = Path.Combine(work, "service");
            WorldMapCache.RootOverride = Path.Combine(sw, "cache");
            var taxi = MapTables.Parse(RawWith(Raw("UiMap", UiMapTable()), Raw("TaxiNodes", WmTaxiNodes()), Raw("TaxiPath", WmTaxiPaths()), Raw("TaxiPathNode", WmTaxiPoints())));
            var idx = new MapIndex(WmAreaSet(taxi));
            const string stamp = "wow_classic_beta|1.60.1.70338|0123456789abcdef";
            MapData.SetForTest(idx, stamp);
            var tiles = WmCols.SelectMany(c => WmRows.Select(r => Tuple.Create(c, r))).ToList();
            WorldMapService.WdtSourceForTest = id => BldWdt(tiles);
            var world = BldMakeWorld();
            BuildingsService.SourceForTest = world.Source;
            BuildingsService.GroundForTest = id => BldGorge(world);
            BuildingsService.ResetForTest();
            string D() => BuildingsService.DirOf(0);
            Func<int> Files = () => Directory.Exists(WorldMapBuildings.ModelsDir(D())) ? Directory.GetFiles(WorldMapBuildings.ModelsDir(D()), "*.wmb").Length : 0;

            // ---------------------------------------------------------------- the scan and the estimate
            await BuildingsService.Prepare(0, "bridges");
            var st = BuildingsService.StateOf(0); var scan = BuildingsService.ScanOf(0);
            Check("scan: the invented continent has " + scan?.Placements.Count + " placed buildings of " + scan?.Models.Count + " shapes; the repeat at a tile edge is dropped (" + scan?.Duplicates + "), the file with the old name table is left out (" + scan?.NameIndexSkipped + " records), a root that is not a root is marked, " + scan?.Bridge.Count(b => b) + " bridge",
                scan != null && scan.Placements.Count == world.Placements && scan.Duplicates == 1 && scan.NameIndexSkipped == 2 && scan.Models.Count == 6 && scan.Models[BldWorld.Broken].RootMissing && !scan.Models[BldWorld.House].RootMissing && scan.Bridge.Count(b => b) == 1 && scan.Tiles == 5,
                scan == null ? "no scan" : scan.Placements.Count + " " + scan.Duplicates + " " + scan.NameIndexSkipped + " " + scan.Models.Count + " " + scan.Bridge.Count(b => b) + " tiles " + scan.Tiles);
            var bi0 = scan.Placements.FindIndex(p => p.NameId == BldWorld.Bridge && scan.Bridge[scan.Placements.IndexOf(p)]);
            Check("scan: the bridge shape stands across the gorge in one place (a bridge) and on flat ground in another (not one); the building count of the 'bridges' mode is 1, of 'all' " + (world.Placements - 1) + " (the broken root has nothing to draw)",
                bi0 >= 0 && Math.Abs(scan.Placements[bi0].X - world.BridgeX) < 1 && scan.Models[BldWorld.Bridge].Bridge && scan.Models[BldWorld.Bridge].BridgePlacements == 1 && scan.Models[BldWorld.Bridge].Placements == 2
                && st.Estimate.Buildings == 1 && WorldMapBuildings.Estimate(scan, "all", null).Buildings == world.Placements - 1 && WorldMapBuildings.ModelsFor(scan, "all").Count() == 5 && WorldMapBuildings.ModelsFor(scan, "bridges").Count() == 1);
            Check("state: Prepare ends in 'ask' with the estimate (" + st?.Estimate?.Buildings + " bridge, " + st?.Estimate?.Models + " shape, " + st?.Estimate?.Minutes.ToString("0.00", I) + " min, " + st?.Estimate?.Bytes + " bytes) and nothing is written yet: no folder, no model",
                st != null && st.Phase == "ask" && st.Mode == "bridges" && st.Estimate != null && st.Estimate.Models == 1 && st.Estimate.Minutes > 0 && st.Estimate.Minutes < 0.75 && st.Estimate.Bytes > 0 && !Directory.Exists(D()) && !BuildingsService.IsReady(0, "bridges"));
            int opens1 = world.Opens; double minBridges = st.Estimate.Minutes; await BuildingsService.Prepare(0, "all");
            var stAll = BuildingsService.StateOf(0);
            Check("state: asking again (Bridges -> All) reuses the scan - the files are not opened again (" + opens1 + " -> " + world.Opens + ") - and the estimate grows to " + stAll?.Estimate?.Models + " shapes / " + stAll?.Estimate?.Buildings + " buildings",
                world.Opens == opens1 && stAll.Phase == "ask" && stAll.Estimate.Models == 5 && stAll.Estimate.Buildings == world.Placements - 1 && stAll.Estimate.Minutes > minBridges);
            await BuildingsService.Prepare(0, "bridges");

            // ---------------------------------------------------------------- build the bridges
            int builds0 = BuildingsService.BuildCountForTest;
            await BuildingsService.Start(0, "bridges");
            st = BuildingsService.StateOf(0);
            var man = WorldMapBuildings.ReadManifest(D());
            var plBytes = File.ReadAllBytes(WorldMapBuildings.PlacementsPath(D())); var pls = WorldMapBuildings.PlacementsFromBytes(plBytes, out var plFlags);
            var bridgeModel = WmoModel.FromBytes(File.ReadAllBytes(WorldMapBuildings.ModelFile(D(), BldWorld.Bridge)));
            var fresh = BuildingsService.TakeFresh(0);
            Check("build bridges: the state ends 'done' (" + st.Done + " / " + st.Total + "), one shape file (m/5000.wmb) and no other, placements.bin holds all " + pls?.Count + " placements with " + plFlags?.Count(b => b) + " flagged as a bridge, the manifest says bridges done and not everything, no .tmp is left, the shape is announced to the page (fresh " + string.Join(",", fresh) + ")",
                st.Phase == "done" && st.Done == 1 && st.Total == 1 && Files() == 1 && File.Exists(WorldMapBuildings.ModelFile(D(), BldWorld.Bridge)) && pls != null && pls.Count == world.Placements && plFlags.Count(b => b) == 1 && man != null && man.Valid && man.BridgesDone && !man.AllDone && man.BuildKey == WorldMapCache.BuildKey
                && !Directory.GetFiles(D(), "*.tmp", SearchOption.AllDirectories).Any() && fresh.SequenceEqual(new[] { BldWorld.Bridge }) && BuildingsService.BuildCountForTest == builds0 + 1 && BuildingsService.IsReady(0, "bridges") && !BuildingsService.IsReady(0, "all"), st.Phase + " " + st.Error);
            Check("build bridges: the shape is a bridge (flag), tinted from its texture's average colour (150, 90, 50), with three levels or two (the box last); a second 'Prepare' of a finished mode goes straight to 'done' and shows the same counts",
                bridgeModel != null && bridgeModel.Bridge && bridgeModel.Lods.Count >= 2 && bridgeModel.Lods[0].Colors[0] == 150 && bridgeModel.Lods[0].Colors[1] == 90 && bridgeModel.Lods[0].Colors[2] == 50 && bridgeModel.Lods.Last().TriCount == 12);
            await BuildingsService.Prepare(0, "bridges");
            Check("state: a finished mode is 'done' at once, without a scan (" + BuildingsService.StateOf(0).Phase + ", " + BuildingsService.StateOf(0).Done + " / " + BuildingsService.StateOf(0).Total + ")", BuildingsService.StateOf(0).Phase == "done" && BuildingsService.StateOf(0).Total == 1);

            // ---------------------------------------------------------------- then everything: pause and resume
            await BuildingsService.Prepare(0, "all");
            Check("state: 'All buildings' after the bridges asks again, with the shapes already made counted (" + BuildingsService.StateOf(0).Estimate.ModelsDone + " of " + BuildingsService.StateOf(0).Estimate.Models + ") and the card saying so: \"" + WorldMapBuildingsHost.CardText(BuildingsService.StateOf(0).Estimate, "Northland", BuildingsService.StateOf(0).Estimate.ModelsDone) + "\"",
                BuildingsService.StateOf(0).Phase == "ask" && BuildingsService.StateOf(0).Estimate.ModelsDone == 1 && BuildingsService.StateOf(0).Estimate.Models == 5 && WorldMapBuildingsHost.CardText(BuildingsService.StateOf(0).Estimate, "Northland", 1).EndsWith("1 of 5 shapes are made already."));
            BuildingsService.ModelDelayMsForTest = 250;
            var phases = new List<string>(); void OnCh(int id) { var s = BuildingsService.StateOf(id); if (s != null) lock (phases) { if (phases.Count == 0 || phases.Last() != s.Phase) phases.Add(s.Phase); } }
            BuildingsService.Changed += OnCh;
            var task = BuildingsService.Start(0, "all");
            await WaitFor(() => Files() >= 2, 20000);
            BuildingsService.Pause(0); await Task.Delay(700);
            int pausedAt = Files(); var stP = BuildingsService.StateOf(0); string pausedText = stP.Text;
            await Task.Delay(900);
            Check("pause: Pause stops after the shape in hand (" + pausedAt + " shapes), the state is 'paused' (\"" + pausedText + "\") and nothing more is made in the next second (" + Files() + ")", stP.Phase == "paused" && Files() == pausedAt && pausedAt >= 2 && pausedAt < 5 && pausedText.StartsWith("Paused: "), stP.Phase + " " + pausedAt + " " + Files());
            BuildingsService.Resume(0);
            await WaitFor(() => task.IsCompleted, 30000); await Task.Delay(100);
            BuildingsService.Changed -= OnCh;
            st = BuildingsService.StateOf(0); man = WorldMapBuildings.ReadManifest(D()); fresh = BuildingsService.TakeFresh(0);
            string[] ph; lock (phases) ph = phases.ToArray();
            var stubBytes = File.ReadAllBytes(WorldMapBuildings.ModelFile(D(), BldWorld.InteriorOnly)); var stubBad = File.ReadAllBytes(WorldMapBuildings.ModelFile(D(), BldWorld.BadGroup));
            var houseModel = WmoModel.FromBytes(File.ReadAllBytes(WorldMapBuildings.ModelFile(D(), BldWorld.House)));
            Check("resume: Resume carries on to 'done' (phases " + string.Join(" > ", ph) + "); all five shapes are on disk, the manifest says everything done; the interior-only building and the one with a damaged group file are stubs (32 bytes, no levels) that are not announced (fresh " + string.Join(",", fresh.OrderBy(i => i)) + ")",
                st.Phase == "done" && Files() == 5 && man != null && man.AllDone && man.BridgesDone && man.ModelsBuilt == 5 && stubBytes.Length == 32 && stubBytes[5] == 0 && stubBad.Length == 32 && fresh.OrderBy(i => i).SequenceEqual(new[] { BldWorld.House, BldWorld.Tower }) && ph.Contains("paused") && ph.Contains("building") && ph.Last() == "done"
                && !File.Exists(WorldMapBuildings.ModelFile(D(), BldWorld.Broken)), st.Phase + " " + st.Error + " files " + Files());
            Check("build: only the groups seen from outside are in the house (the far away interior group at y 100 .. 108 is not: the model's box is 18 yards across, not 108); its roof has no texture and is tinted by the way it faces, its walls from the texture (120, 120, 125)",
                houseModel != null && houseModel.Half[0] < 10 && houseModel.Half[2] < 10 && houseModel.Lods[0].Colors.Select((c, i) => new { c, i }).Any(x => x.i % 3 == 0 && x.c == 120) && houseModel.Lods[0].Colors.Select((c, i) => new { c, i }).Any(x => x.i % 3 == 0 && x.c != 120 && x.c != 0), houseModel == null ? "?" : string.Join(",", houseModel.Half));
            BuildingsService.ModelDelayMsForTest = 0;

            // ---------------------------------------------------------------- cancel, nearest first, resume
            BuildingsService.Remove(0);
            Check("remove: Remove deletes the continent's buildings folder (the state goes back to idle, nothing of it is left on disk), and the Settings rows list nothing",
                !Directory.Exists(D()) && BuildingsService.Rows().Count == 0 && BuildingsService.StateOf(0).Phase == "idle" && BuildingsService.StatusLine().StartsWith("Not made yet"));
            await BuildingsService.Prepare(0, "all");
            BuildingsService.Focus(0, world.TowerX, world.TowerZ); BuildingsService.ModelDelayMsForTest = 300;
            var t2 = BuildingsService.Start(0, "all");
            await WaitFor(() => Files() >= 1, 20000);
            var firstFile = Directory.GetFiles(WorldMapBuildings.ModelsDir(D()), "*.wmb").OrderBy(f => File.GetLastWriteTimeUtc(f)).First(); var firstTime = File.GetLastWriteTimeUtc(firstFile);
            BuildingsService.Cancel(0);
            await WaitFor(() => t2.IsCompleted, 20000); await Task.Delay(100);
            int partial = Files(); var stC = BuildingsService.StateOf(0);
            Check("nearest first: with the camera at the towers the first shape made is the tower's (" + Path.GetFileNameWithoutExtension(firstFile) + "); Cancel stops it - the state is 'ask' again with the shapes made so far (" + partial + " of 5) kept on disk and no manifest, so the folder is not taken for a finished one",
                Path.GetFileNameWithoutExtension(firstFile) == BldWorld.Tower.ToString() && stC.Phase == "ask" && partial >= 1 && partial < 5 && WorldMapBuildings.ReadManifest(D()) == null && !BuildingsService.IsReady(0, "all") && !BuildingsService.IsRunning(0), stC.Phase + " " + partial);
            BuildingsService.ModelDelayMsForTest = 0; BuildingsService.Focus(0, world.BridgeX, world.BridgeZ);
            await BuildingsService.Prepare(0, "all");
            Check("resume after cancel: the card knows " + BuildingsService.StateOf(0).Estimate.ModelsDone + " shapes are made already (Continue rather than Build now)", BuildingsService.StateOf(0).Estimate.ModelsDone == partial);
            await BuildingsService.Start(0, "all");
            Check("resume after cancel: the build goes on from there to 'done' - all five shapes, the first one made before the cancel was not made again (same file time)",
                BuildingsService.StateOf(0).Phase == "done" && Files() == 5 && File.GetLastWriteTimeUtc(firstFile) == firstTime && WorldMapBuildings.ReadManifest(D()) != null && WorldMapBuildings.ReadManifest(D()).AllDone);

            // ---------------------------------------------------------------- the manifest, the rows in Settings, the cache row
            var rows = BuildingsService.Rows();
            WorldMapCache.Clear();
            Check("settings: the row lists the continent with its size (" + rows.FirstOrDefault()?.Bytes + " bytes), " + rows.FirstOrDefault()?.Models + " shapes and 'all buildings': \"" + BuildingsService.StatusLine() + "\"",
                rows.Count == 1 && rows[0].Name == "Northland" && rows[0].Models == 5 && rows[0].Complete && rows[0].Mode == "all" && rows[0].Bytes > 1000 && BuildingsService.StatusLine().Contains("Northland") && BuildingsService.StatusLine().Contains("(all buildings)"));
            Check("settings: 'Clear world map cache' leaves the buildings alone (they have their own Remove) and the cache row does not count them (" + WorldMapCache.SizeBytes() + " bytes)", Directory.Exists(D()) && Files() == 5 && WorldMapCache.SizeBytes() == 0 && BuildingsService.Rows().Count == 1);
            Check("manifest: the measured cost replaces the estimate's defaults - " + Math.Round(man.SecPerGroup * 1000, 2) + " ms and " + Math.Round(man.BytesPerGroup) + " bytes per group are recorded", WorldMapBuildings.ReadManifest(D()).BytesPerGroup > 0 && WorldMapBuildings.ReadManifest(D()).Models == 5 && WorldMapBuildings.ReadManifest(D()).Placements == world.Placements);

            // ---------------------------------------------------------------- 2.89: a folder made under the old group rule (exterior groups only) is brought up to date without asking
            {
                var houseFile = WorldMapBuildings.ModelFile(D(), BldWorld.House); var towerFile = WorldMapBuildings.ModelFile(D(), BldWorld.Tower);
                var towerTime = File.GetLastWriteTimeUtc(towerFile); var houseBefore = WmoModel.FromBytes(File.ReadAllBytes(houseFile));
                var savedHouse = world.Files[BldWorld.House];
                // the house gains a courtyard open to the sky (interior + exterior lit, 0x2040) 30 yards to the side - as Orgrimmar's streets are; the old rule had left such a group out
                BldBox(-4, 4, 30, 40, 0, 2, out var cv, out var ci);
                world.Files[BldWorld.House] = BldRoot(new[] { -9f, -9f, 0f, 9f, 40f, 11f }, new uint[] { 8, 8, 0x2000, 0x2040 }, new[] { 6001, 6002, 6003, 6007 }, new uint[] { 7001, 0 });
                world.Files[6007] = BldGroup(0x2041, cv, ci, new byte[12]);
                var old = WorldMapBuildings.ReadManifest(D());
                File.WriteAllText(WorldMapBuildings.ManifestPath(D()), File.ReadAllText(WorldMapBuildings.ManifestPath(D())).Replace("\"rules\":2,", ""));     // a 2.88 manifest has no "rules"
                var oldRead = WorldMapBuildings.ReadManifest(D());
                BuildingsService.ResetForTest();
                bool readyBefore = BuildingsService.IsReady(0, "all");
                int up0 = BuildingsService.UpgradeCountForTest, b0 = BuildingsService.BuildCountForTest;
                var upPhases = new List<string>(); void OnUp(int id) { var s2 = BuildingsService.StateOf(id); if (s2 != null) lock (upPhases) { if (upPhases.Count == 0 || upPhases.Last() != s2.Phase) upPhases.Add(s2.Phase); } }
                BuildingsService.Changed += OnUp;
                await BuildingsService.Prepare(0, "all");
                await WaitFor(() => !BuildingsService.IsRunning(0), 20000);
                BuildingsService.Changed -= OnUp;
                var manUp = WorldMapBuildings.ReadManifest(D()); var houseAfter = WmoModel.FromBytes(File.ReadAllBytes(houseFile)); var freshUp = BuildingsService.TakeFresh(0);
                string[] up; lock (upPhases) up = upPhases.ToArray();
                Check("group rule: a finished folder from before 2.89 (no \"rules\" in its manifest = rule " + oldRead?.Rules + ") is not ready (" + readyBefore + "); Prepare remakes only the models the new rule makes differently, without asking (phases " + string.Join(" > ", up) + "): the house now has its open-air courtyard (box "
                    + (houseBefore == null ? "?" : (houseBefore.Half[0] * 2).ToString("0", I)) + " -> " + (houseAfter == null ? "?" : (houseAfter.Half[0] * 2).ToString("0", I)) + " yards), the tower was not made again, the page hears of the house (fresh " + string.Join(",", freshUp) + "), the manifest says rule " + manUp?.Rules,
                    oldRead != null && oldRead.Rules == 1 && !readyBefore && BuildingsService.UpgradeCountForTest == up0 + 1 && BuildingsService.BuildCountForTest == b0 + 1 && !up.Contains("ask") && BuildingsService.StateOf(0).Phase == "done"
                    && houseBefore != null && houseAfter != null && houseBefore.Half[0] < 10 && houseAfter.Half[0] > 20 && File.GetLastWriteTimeUtc(towerFile) == towerTime && freshUp.SequenceEqual(new[] { BldWorld.House })
                    && manUp != null && manUp.Rules == WorldMapBuildings.Rules && manUp.AllDone && Files() == 5 && BuildingsService.IsReady(0, "all"), string.Join(" > ", up) + " " + BuildingsService.StateOf(0).Error);
                await BuildingsService.Prepare(0, "all");
                Check("group rule: once remade, the folder is ready again - Prepare is 'done' at once and nothing is remade (" + BuildingsService.UpgradeCountForTest + " upgrades)", BuildingsService.StateOf(0).Phase == "done" && BuildingsService.UpgradeCountForTest == up0 + 1);
                world.Files[BldWorld.House] = savedHouse; world.Files.Remove(6007);
                // and back to the plain house for what follows (its file removed, an old rule: the upgrade makes the missing one)
                File.Delete(houseFile);
                File.WriteAllText(WorldMapBuildings.ManifestPath(D()), File.ReadAllText(WorldMapBuildings.ManifestPath(D())).Replace("\"rules\":2,", "\"rules\":1,"));
                BuildingsService.ResetForTest(); await BuildingsService.Prepare(0, "all"); await WaitFor(() => !BuildingsService.IsRunning(0), 20000);
                BuildingsService.TakeFresh(0);
            }

            // ---------------------------------------------------------------- a new game build asks again
            MapData.SetForTest(idx, "wow_classic_beta|1.60.1.70999|ffffffffffffffff");
            BuildingsService.ResetForTest();
            Check("new game build: the finished buildings belong to the old build, so for the new one nothing is ready (" + BuildingsService.IsReady(0, "all") + "), the folder is another one (" + Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(D()))) + "), and the card asks again",
                !BuildingsService.IsReady(0, "all") && !BuildingsService.IsReady(0, "bridges") && !Directory.Exists(D()));
            await BuildingsService.Prepare(0, "bridges");
            Check("new game build: Prepare ends in 'ask' (not 'done'), with the estimate for the whole job again", BuildingsService.StateOf(0).Phase == "ask" && BuildingsService.StateOf(0).Estimate.ModelsDone == 0 && BuildingsService.StateOf(0).Estimate.Models == 1);
            var oldBuild = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(WorldMapBuildings.ModelsDir(BuildingsService.DirOf(0)))));
            await BuildingsService.Start(0, "bridges");
            Check("new game build: building for the new version removes the folders of the old one (their buildings are of no use any more); the new folder holds the bridge", Directory.GetDirectories(WorldMapCache.Root).Where(d => Path.GetFileName(d) != WorldMapCache.PageFolder).Select(Path.GetFileName).SequenceEqual(new[] { WorldMapCache.BuildKey }) && Files() == 1 && BuildingsService.IsReady(0, "bridges"),
                string.Join(",", Directory.GetDirectories(WorldMapCache.Root).Select(Path.GetFileName)));
            MapData.SetForTest(idx, stamp); BuildingsService.ResetForTest();

            // ---------------------------------------------------------------- the host: the page's messages
            BuildingsService.Remove(-1);
            var posted = new List<string>(); int saves = 0;
            settings.WorldMap3dBuildings = null;
            var host = new WorldMapBuildingsHost(j => { lock (posted) posted.Add(j); }, a => { try { Dispatcher.BeginInvoke(a); return true; } catch (Exception) { return false; } }, () => 0, () => settings, () => saves++);
            host.Attach();
            var init = host.InitInfo(0, "https://worldmap.outpost.local/k/0");
            Check("host: the page's init carries the saved mode (off by default), the base of the buildings folder and the shapes that exist (none)", (string)init["mode"] == "off" && (string)init["base"] == "https://worldmap.outpost.local/k/0/buildings" && ((List<int>)init["models"]).Count == 0 && !(bool)init["placements"]);
            WorldMapMessage Pm(string j) => WorldMapProtocol.Parse(j, out _);
            host.Handle(Pm("{\"t\":\"buildings\",\"mode\":\"all\"}"), 0);
            bool asked = await WaitFor(() => { lock (posted) return posted.Any(p => p.Contains("\"phase\":\"ask\"")); }, 20000);
            string askJson; lock (posted) askJson = posted.LastOrDefault(p => p.Contains("\"phase\":\"ask\""));
            var ask = askJson == null ? null : Json.Obj(askJson);
            Check("host: switching to All buildings saves the mode (" + settings.WorldMap3dBuildings + ", saved " + saves + "x), counts the buildings and answers with the card: " + ask?.Str("card"),
                asked && settings.WorldMap3dBuildings == "all" && saves == 1 && ask != null && ask.Str("mode") == "all" && ask.Child("est").Int("buildings") == world.Placements - 1 && ask.Child("est").Int("models") == 5 && ask.Str("card").StartsWith("Show buildings in 3D. Reads the shapes of about 16 buildings in Northland"), askJson);
            BuildingsService.ModelDelayMsForTest = 500;
            lock (posted) posted.Clear();
            host.Handle(Pm("{\"t\":\"bbuild\",\"mode\":\"all\"}"), 0);
            await WaitFor(() => { lock (posted) return posted.Any(p => p.Contains("\"phase\":\"building\"") && p.Contains("\"fresh\"")); }, 20000);
            host.Handle(Pm("{\"t\":\"bpause\"}"), 0); await Task.Delay(500);
            string pausedJson; lock (posted) pausedJson = posted.LastOrDefault(p => p.Contains("\"phase\":\"paused\""));
            Check("host: Build now starts the job and the page hears of the shapes as they finish; Pause answers 'paused' with the progress text", pausedJson != null && Json.Obj(pausedJson).Str("text").StartsWith("Paused: ") && BuildingsService.StateOf(0).Phase == "paused", pausedJson);
            host.Handle(Pm("{\"t\":\"bresume\"}"), 0);
            bool done = await WaitFor(() => BuildingsService.StateOf(0).Phase == "done", 30000); await Task.Delay(700);
            List<string> all; lock (posted) all = posted.ToList();
            var freshIds = all.Select(Json.Obj).Where(o => o != null && o.List("fresh").Count > 0).SelectMany(o => o.List("fresh").Select(Convert.ToInt32)).ToList();
            var lastJ = all.Select(Json.Obj).LastOrDefault(o => o != null);
            Check("host: after Resume the job finishes; every finished shape was announced once (fresh: " + string.Join(",", freshIds.OrderBy(i => i)) + "), the last message says done (" + lastJ?.Str("text") + lastJ?.Str("phase") + ") with the count and the size on disk (" + lastJ?.Int("bytes") + " bytes)",
                done && freshIds.Distinct().Count() == freshIds.Count && freshIds.OrderBy(i => i).SequenceEqual(new[] { BldWorld.Bridge, BldWorld.House, BldWorld.Tower }) && lastJ != null && lastJ.Str("phase") == "done" && lastJ.Int("total") == world.Placements - 1 && lastJ.Int("bytes") > 1000);
            BuildingsService.ModelDelayMsForTest = 0;
            host.Handle(Pm("{\"t\":\"buildings\",\"mode\":\"off\"}"), 0);
            Check("host: Off is saved as the default again (the setting is empty), the page is told 'off'", settings.WorldMap3dBuildings == null && Json.Obj(host.PostedForTest.Last()).Str("mode") == "off" && saves >= 2);
            BuildingsService.Remove(0);
            host.Handle(Pm("{\"t\":\"buildings\",\"mode\":\"bridges\"}"), 0);
            await WaitFor(() => BuildingsService.StateOf(0).Phase == "ask", 20000);
            BuildingsService.ModelDelayMsForTest = 400;
            host.Handle(Pm("{\"t\":\"bbuild\",\"mode\":\"all\"}"), 0);
            await WaitFor(() => Files() >= 1, 20000);
            host.Handle(Pm("{\"t\":\"bcancel\"}"), 0);
            await WaitFor(() => !BuildingsService.IsRunning(0), 20000); await Task.Delay(300);
            Check("host: Cancel stops the job, saves Off and tells the page; the shapes made so far stay (" + Files() + " on disk)", settings.WorldMap3dBuildings == null && BuildingsService.StateOf(0).Phase == "ask" && Files() >= 1 && Files() < 5 && Json.Obj(host.PostedForTest.Last()).Str("mode") == "off", settings.WorldMap3dBuildings + " " + BuildingsService.StateOf(0).Phase);
            BuildingsService.ModelDelayMsForTest = 0; host.Detach();

            // ---------------------------------------------------------------- errors
            BuildingsService.Remove(-1); BuildingsService.ResetForTest();
            WorldMapService.WdtSourceForTest = id => new byte[] { 1, 2, 3 };
            await BuildingsService.Prepare(0, "all");
            var stE = BuildingsService.StateOf(0);
            Check("error: a world table that cannot be read ends in 'error' with a sentence, not a crash (\"" + stE.Error + "\"); a continent with no buildings in the install says so",
                stE.Phase == "error" && stE.Error != null && stE.Error.Contains("could not be counted") && stE.Text == stE.Error);
            WorldMapService.WdtSourceForTest = id => BldWdt(tiles); BuildingsService.ResetForTest();
            BuildingsService.SourceForTest = () => new FuncBuildingSource(f => null);
            await BuildingsService.Prepare(0, "all");
            Check("error: an install without the building files has no placements - 'error' (\"" + BuildingsService.StateOf(0).Error + "\")", BuildingsService.StateOf(0).Phase == "error" && BuildingsService.StateOf(0).Error.Contains("no buildings"));
            BuildingsService.SourceForTest = world.Source; BuildingsService.ResetForTest();
        }
    }
}
