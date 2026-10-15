using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.DungeonMaps;
using ElansAddonHub.Services.Maps;
using ElansAddonHub.Services.WorldMap;

namespace ElansAddonHub
{
    // --selftest "dungeonmaps": plans of the instances the game ships no map art for (Services/DungeonMaps, AtlasMap.Drawn.cs), and the event / placeholder labels
    // of the instance list. Part 1 (this file): the readers, the walkable rule, the floor split, the pictures and the plan file on synthetic data. Part 2
    // (.Service.cs): the service and the map card. Part 3 (.Real.cs): the real game files when this PC has them.
    public partial class MainWindow
    {
        async Task<string> DungeonMapsTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            void Check(string what, bool cond, string detail = null) { notes.Add((cond ? "ok   " : "FAIL ") + what + (!cond && detail != null ? " [" + detail + "]" : "")); ok &= cond; Util.Log("dungeonmaps test: " + (cond ? "ok   " : "FAIL ") + what); }
            var work = Path.Combine(dir, "dungeonmaps");
            var savedRoot = DungeonPlans.RootOverride; var savedSrc = DungeonPlans.SourceForTest; var savedAuto = DungeonPlans.AutoSeconds; var savedEnabled = DungeonPlans.Enabled;
            var savedWow = DungeonPlans.WowRoot;
            try
            {
                if (Directory.Exists(work)) Util.DeleteDir(work);
                Directory.CreateDirectory(work);
                Check("fixture: everything of this test is under the scratch folder (never the real game, never the real data folder)", Path.GetFullPath(work).StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase));
                DmFormatChecks(Check);
                DmFloorChecks(Check);
                DmImageChecks(Check, work);
                DmEventChecks(Check);
                DmEntranceChecks(Check);
                await DmServiceChecks(work, Check, notes, dir);
            }
            catch (Exception e) { Check("dungeonmaps test crashed", false, e.ToString()); }
            finally
            {
                DungeonPlans.RootOverride = savedRoot; DungeonPlans.SourceForTest = savedSrc; DungeonPlans.AutoSeconds = savedAuto; DungeonPlans.Enabled = savedEnabled; DungeonPlans.WowRoot = savedWow;
                DungeonPlans.DelayMsForTest = 0; DungeonPlans.ResetForTest(); MapData.ResetForTest();
            }
            try { await DmRealChecks(dir, work, Check, notes); }
            catch (Exception e) { Check("dungeonmaps real-data test crashed", false, e.ToString()); }
            finally { DungeonPlans.RootOverride = savedRoot; DungeonPlans.WowRoot = savedWow; DungeonPlans.ResetForTest(); MapData.ResetForTest(); }
            return (ok ? "dungeonmaps: ok" : "dungeonmaps: FAILED") + "\r\n" + string.Join("\r\n", notes);
        }

        // ---------------------------------------------------------------- synthetic files (tags reversed on disk, as the game writes them)
        static byte[] DmChunk(string tag, byte[] body)
        {
            var ms = new MemoryStream(); var w = new BinaryWriter(ms);
            w.Write(Encoding.ASCII.GetBytes(new string(tag.Reverse().ToArray()))); w.Write(body.Length); w.Write(body); w.Flush();
            return ms.ToArray();
        }
        static byte[] DmCat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

        // a WDT of a map made of one building: MPHD flag 0x1, no tiles, one MODF record naming the WMO
        static byte[] DmGlobalWdt(uint wmo)
        {
            var modf = new byte[64];
            Array.Copy(BitConverter.GetBytes(wmo), 0, modf, 0, 4); Array.Copy(BitConverter.GetBytes(7u), 0, modf, 4, 4);
            Array.Copy(BitConverter.GetBytes((ushort)1024), 0, modf, 62, 2);
            var mphd = new byte[32]; mphd[0] = 1;
            return DmCat(DmChunk("MVER", BitConverter.GetBytes(18)), DmChunk("MPHD", mphd), DmChunk("MAIN", new byte[4096 * 8]), DmChunk("MAID", new byte[131072]), DmChunk("MODF", modf));
        }

        // a WMO root with named groups (MOGI name offsets into MOGN)
        static byte[] DmRoot(int[] groupFdids, string[] names, uint[] flags = null)
        {
            int ng = groupFdids.Length;
            var mohd = new byte[64];
            Array.Copy(BitConverter.GetBytes(0), 0, mohd, 0, 4); Array.Copy(BitConverter.GetBytes(ng), 0, mohd, 4, 4);
            var mogn = new MemoryStream(); mogn.WriteByte(0); var offs = new int[ng];
            for (int i = 0; i < ng; i++) { offs[i] = (int)mogn.Length; var b = Encoding.ASCII.GetBytes(names[i] + "\0"); mogn.Write(b, 0, b.Length); }
            var mogi = new MemoryStream(); var g = new BinaryWriter(mogi);
            for (int i = 0; i < ng; i++) { g.Write(flags != null ? flags[i] : 0x2000u); for (int k = 0; k < 6; k++) g.Write(0f); g.Write(names[i].Length == 0 ? 0 : offs[i]); }
            g.Flush();
            var gfid = groupFdids.SelectMany(f => BitConverter.GetBytes((uint)f)).ToArray();
            return DmCat(DmChunk("MVER", BitConverter.GetBytes(17)), DmChunk("MOHD", mohd), DmChunk("MOGN", mogn.ToArray()), DmChunk("MOGI", mogi.ToArray()), DmChunk("GFID", gfid));
        }

        // a group file: triangles (3 vertices each, model axes x north, y west, z up), a MOPY flag per triangle, a normal per triangle (all three vertices), optional MLIQ
        sealed class DmTri { public float[] A, B, C; public byte Flags = 0x20; public float[] N = { 0, 0, 1 }; }

        static byte[] DmGroup(IList<DmTri> tris, uint groupFlags = 0x2000, byte[] mliq = null)
        {
            var body = new MemoryStream(); var w = new BinaryWriter(body);
            var head = new byte[68]; Array.Copy(BitConverter.GetBytes(groupFlags), 0, head, 8, 4); w.Write(head);
            void Sub(string tag, byte[] b) { w.Write(Encoding.ASCII.GetBytes(new string(tag.Reverse().ToArray()))); w.Write(b.Length); w.Write(b); }
            Sub("MOPY", tris.SelectMany(t => new byte[] { t.Flags, t.Flags == 0x08 ? (byte)0xFF : (byte)0 }).ToArray());
            Sub("MOVI", Enumerable.Range(0, tris.Count * 3).SelectMany(i => BitConverter.GetBytes((ushort)i)).ToArray());
            Sub("MOVT", tris.SelectMany(t => new[] { t.A, t.B, t.C }).SelectMany(p => p.SelectMany(f => BitConverter.GetBytes(f))).ToArray());
            Sub("MONR", tris.SelectMany(t => new[] { t.N, t.N, t.N }).SelectMany(p => p.SelectMany(f => BitConverter.GetBytes(f))).ToArray());
            if (mliq != null) Sub("MLIQ", mliq);
            w.Flush();
            return DmCat(DmChunk("MVER", BitConverter.GetBytes(17)), DmChunk("MOGP", body.ToArray()));
        }

        // a 1 x 1 tile liquid at (x0, y0) of the given height
        static byte[] DmLiquid(float x0, float y0, float height)
        {
            var ms = new MemoryStream(); var w = new BinaryWriter(ms);
            w.Write(2); w.Write(2); w.Write(1); w.Write(1); w.Write(x0); w.Write(y0); w.Write(0f); w.Write((ushort)0);
            for (int i = 0; i < 4; i++) { w.Write(0); w.Write(height); }
            w.Write((byte)0x00);
            w.Flush(); return ms.ToArray();
        }

        // floor quads as triangles (up-facing, counter-clockwise seen from above: x north, y west)
        static IEnumerable<DmTri> DmQuad(float x0, float x1, float y0, float y1, float z0, float z1, bool alongX = true, byte flags = 0x20)
        {
            float Z(float x, float y) => alongX ? z0 + (z1 - z0) * (x - x0) / Math.Max(1e-6f, x1 - x0) : z0 + (z1 - z0) * (y - y0) / Math.Max(1e-6f, y1 - y0);
            float[] P(float x, float y) => new[] { x, y, Z(x, y) };
            yield return new DmTri { A = P(x0, y0), B = P(x1, y0), C = P(x1, y1), Flags = flags };
            yield return new DmTri { A = P(x0, y0), B = P(x1, y1), C = P(x0, y1), Flags = flags };
        }

        // the same in world axes as plan units, cut into pieces of 5 yards at most (the game's floors are many small triangles; a triangle goes to one floor whole)
        static PlanUnit DmUnit(string name, params (float x0, float x1, float y0, float y1, float z0, float z1)[] quads)
        {
            var u = new PlanUnit { Kind = PlanUnitKind.Building, Name = name ?? "" };
            foreach (var q in quads)
            {
                int nx = Math.Max(1, (int)Math.Ceiling((q.x1 - q.x0) / 5)), ny = Math.Max(1, (int)Math.Ceiling((q.y1 - q.y0) / 5));
                float Z(float x) => q.z0 + (q.z1 - q.z0) * (x - q.x0) / Math.Max(1e-6f, q.x1 - q.x0);
                for (int i = 0; i < nx; i++)
                    for (int j = 0; j < ny; j++)
                    {
                        float a0 = q.x0 + (q.x1 - q.x0) * i / nx, a1 = q.x0 + (q.x1 - q.x0) * (i + 1) / nx, b0 = q.y0 + (q.y1 - q.y0) * j / ny, b1 = q.y0 + (q.y1 - q.y0) * (j + 1) / ny;
                        foreach (var t in DmQuad(a0, a1, b0, b1, Z(a0), Z(a1)))
                            u.Add(t.A[0], t.A[1], t.A[2], t.B[0], t.B[1], t.B[2], t.C[0], t.C[1], t.C[2]);
                    }
            }
            return u;
        }

        // the stacked test dungeon: a hall at the bottom, a ramp up, a landing and a gallery over the hall (one group: the floor split has to cut it)
        static PlanUnit DmStacked(string name = "Hall of Echoes") => DmUnit(name,
            (0, 40, 0, 40, 0, 0),          // the hall, z 0
            (40, 80, 0, 10, 0, 20),        // the ramp, 0 -> 20 along x
            (80, 90, 0, 40, 20, 20),       // the landing, z 20
            (0, 80, 15, 40, 20, 20));      // the gallery, z 20: over the hall from x 0 to 40

        // ---------------------------------------------------------------- the readers
        void DmFormatChecks(CheckFn Check)
        {
            var wdt = WdtFile.Parse(DmGlobalWdt(5001));
            var g = wdt.GlobalWmo;
            DungeonGeometry.ToWorld(g ?? new WmoPlacement(), 100, 200, 5, out var wx, out var wy, out var wz);
            Check("WDT: a map of one building (MPHD flag 0x1, MODF) is read: the WMO's file id, no tiles, and the model's (100, 200, 5) is the world's (-100, -200, 5) (the axes the Classic baseline's spawns of Molten Core / Onyxia's Lair / Blackfathom Deeps / Blackrock Depths fall into)",
                g != null && g.NameId == 5001 && (wdt.MphdFlags & 1) == 1 && wdt.Tiles.Count == 0 && Math.Abs(wx + 100) < 1e-3 && Math.Abs(wy + 200) < 1e-3 && Math.Abs(wz - 5) < 1e-3, g == null ? "no global WMO" : wx + "," + wy + "," + wz);
            var noModf = WdtFile.Parse(DmCat(DmChunk("MVER", BitConverter.GetBytes(18)), DmChunk("MPHD", new byte[32]), DmChunk("MAID", new byte[131072])));
            Check("WDT: a map of tiles has no global building", noModf.Ok && noModf.GlobalWmo == null);
            var root = WmoRoot.Parse(DmRoot(new[] { 11, 12, 13 }, new[] { "Ironclad Cove", "cave2_18", "" }));
            Check("WMO root: group names from MOGN by MOGI's offsets (\"Ironclad Cove\", \"cave2_18\", none)", root.Ok && root.GroupInfos.Count == 3 && root.GroupInfos[0].Name == "Ironclad Cove" && root.GroupInfos[1].Name == "cave2_18" && root.GroupInfos[2].Name == "", string.Join("|", root.GroupInfos.Select(x => x.Name)));

            // the walkable rule on one group
            var tris = new List<DmTri>();
            tris.AddRange(DmQuad(0, 4, 0, 4, 0, 0).Take(1));                                  // 0 floor, rendered: walkable
            tris.AddRange(DmQuad(10, 14, 0, 4, 0, 0, true, 0x24).Take(1));                    // 1 detail (rubble): not collided with
            tris.AddRange(DmQuad(20, 24, 0, 4, 0, 0, true, 0x08).Take(1));                    // 2 invisible collision floor: walkable
            var ceil = DmQuad(30, 34, 0, 4, 10, 10).First(); ceil.N = new float[] { 0, 0, -1 }; var cb = ceil.B; ceil.B = ceil.C; ceil.C = cb; tris.Add(ceil);   // 3 a ceiling
            tris.Add(new DmTri { A = new float[] { 40, 0, 0 }, B = new float[] { 40, 4, 0 }, C = new float[] { 40, 4, 4 }, N = new float[] { 1, 0, 0 } });                 // 4 a wall
            var rev = DmQuad(50, 54, 0, 4, 0, 0).First(); var rb = rev.B; rev.B = rev.C; rev.C = rb; tris.Add(rev);   // 5 wound the other way, normals up: still a floor
            tris.Add(new DmTri { A = new float[] { 60.5f, 0.5f, 0 }, B = new float[] { 63.5f, 0.5f, 0 }, C = new float[] { 63.5f, 3.5f, 0 } });   // 6 deep under the liquid
            tris.Add(new DmTri { A = new float[] { 60.5f, 0.5f, 9 }, B = new float[] { 63.5f, 0.5f, 9 }, C = new float[] { 63.5f, 3.5f, 9 } });   // 7 just under its surface: shallow
            var grp = DungeonGeometry.ParseGroup(DmGroup(tris, 0x2000, DmLiquid(60, 0, 10)));
            Check("WMO group: a floor counts when a character collides with it (MOPY 0x20 rendered, or 0x08 collision) and it faces up; detail (0x24), a ceiling, a wall and a floor 2.5+ yards under the group's liquid (MLIQ) do not; a floor wound the other way is turned by its vertex normals",
                grp.Ok && grp.Triangles == 8 && grp.Collidable == 7 && grp.Tris.Count == 4 * 3 && grp.UnderLiquid == 1, grp.Problem + " " + grp.Triangles + "/" + grp.Collidable + "/" + grp.Tris.Count / 3 + "/" + grp.UnderLiquid);
            var empty = DungeonGeometry.ParseGroup(new byte[] { 1, 2, 3 });
            Check("WMO group: a cut-off file is a problem, not a crash", !empty.Ok);

            // terrain holes and units
            ulong low = AdtFile.LowResHoles(0x0001);
            Check("ADT holes: the old 4 x 4 mask's bit 0 is the 2 x 2 cells at the north-west corner of the chunk", low == (1UL | 2UL | (1UL << 8) | (1UL << 9)), low.ToString("x"));
            var chunk = new AdtChunk { PosX = 1000, PosY = 2000, PosZ = 10, Heights = new float[145], Holes = AdtFile.LowResHoles(0x0001) };
            var tu = DungeonGeometry.TerrainUnit(chunk, 1);
            double cell = DungeonGeometry.CellSize;
            Check("terrain: a flat chunk is 4 triangles per cell, holes left out (60 cells x 4), its area the cells' and its corner at MCNK's position going south and east",
                tu.TriCount == 240 && Math.Abs(tu.Area - 60 * cell * cell) < 1 && tu.Tri.Where((v, i) => i % 3 == 0).Max() <= 1000.001 && tu.Tri.Where((v, i) => i % 3 == 1).Max() <= 2000.001 && tu.Tri.Where((v, i) => i % 3 == 2).All(z => Math.Abs(z - 10) < 1e-4), tu.TriCount + " " + tu.Area);
            var steep = new AdtChunk { PosX = 0, PosY = 0, PosZ = 0, Heights = Enumerable.Range(0, 145).Select(i => (float)((i % 17 < 9 ? i / 17 * 2 : i / 17 * 2 + 1) * 10)).ToArray() };
            Check("terrain: a cliff (a 10 yard rise per 2 yards) is not ground", DungeonGeometry.TerrainUnit(steep, 2).TriCount == 0);

            // the terrain rule
            var ground = new List<PlanUnit> { DungeonGeometry.TerrainUnit(new AdtChunk { PosX = 40, PosY = 40, PosZ = 0, Heights = new float[145] }, 3) };
            var onIt = new List<PlanUnit> { DmUnit("Temple", (5, 30, 5, 30, 2, 2)) };
            var under = new List<PlanUnit> { DmUnit("Mine", (5, 30, 5, 30, -60, -60)) };
            bool r1 = DungeonGeometry.TerrainRule(onIt, ground, out var w1), r2 = DungeonGeometry.TerrainRule(under, ground, out var w2);
            Check("terrain rule: buildings standing on the terrain make it part of the plan; a building 60 yards under it (a filler landscape over a mine) does not; no building = the terrain is the instance",
                r1 && !r2 && DungeonGeometry.TerrainRule(new List<PlanUnit>(), ground, out _) && w1.Contains("stand on") && w2.Contains("filler"), w1 + " | " + w2);
            var cropped = DungeonGeometry.Crop(ground, onIt, 2);
            Check("terrain rule: the terrain is cut to the buildings' extent (plus a margin)", cropped.Sum(u => u.TriCount) > 0 && cropped.Sum(u => u.TriCount) < ground[0].TriCount && cropped.SelectMany(u => u.Tri.Where((v, i) => i % 3 == 0)).All(x => x <= 33));
        }

        // ---------------------------------------------------------------- the floor split
        void DmFloorChecks(CheckFn Check)
        {
            var geo = new PlanGeometry { Units = { DmStacked() } };
            var floors = DungeonFloors.Split(geo, "Gloomhold");
            Check("floors: one group whose gallery passes over its own hall is cut in two: the hall with the ramp and the landing below, the part of the gallery over the hall above (" + floors.Count + " floors)",
                floors.Count == 2 && floors[0].MeanZ < floors[1].MeanZ && floors[1].MinZ >= 19 && floors[0].MinZ < 1 && floors[1].Area > 900 && floors[1].Area < 1100, string.Join(" | ", floors.Select(f => f.Name + " " + Math.Round(f.Area) + " z " + f.MinZ + ".." + f.MaxZ)));
            Check("floors: named after the group with a real name, one floor each (\"Hall of Echoes\" once, then \"Level 2\")", floors.Count == 2 && floors.Count(f => f.Name == "Hall of Echoes") == 1 && floors.Any(f => f.Name == "Level 2"), string.Join(",", floors.Select(f => f.Name)));

            var flat = new PlanGeometry { Units = { DmUnit("cave2_18", (0, 40, 0, 40, 0, 0), (40, 80, 0, 10, 0, 15), (80, 120, 0, 40, 15, 15)) } };
            var ff = DungeonFloors.Split(flat, "Gloomhold");
            Check("floors: a dungeon that never covers itself is one floor, however much it climbs; an internal group name is no name (\"Level 1\")", ff.Count == 1 && ff[0].Name == "Level 1", string.Join(",", ff.Select(f => f.Name)));

            var withRoof = new PlanGeometry { Units = { DmUnit("Gloomhold", (0, 60, 0, 60, 0, 0)), DmUnit("roof", (20, 26, 20, 26, 12, 12)), DmUnit("Far Crypt", (200, 240, 0, 40, 0, 0)) } };
            var fr = DungeonFloors.Split(withRoof, "Gloomhold");
            Check("floors: a small island nobody walks to (a 6 x 6 rooftop) is left out, a big room that is not joined to the rest (a teleporter's end) is kept; the instance's own name is not a floor name",
                withRoof.Islands == 1 && Math.Abs(withRoof.IslandArea - 36) < 1 && fr.Count == 1 && Math.Abs(fr[0].Area - 3600 - 1600) < 1 && fr[0].Name == "Far Crypt", withRoof.Islands + " " + withRoof.IslandArea + " " + string.Join(",", fr.Select(f => f.Name + " " + f.Area)));

            var reach = DungeonFloors.Reachable(withRoof.Units, out var isl, out var dropped);
            Check("floors: Reachable gives the units without the island", isl == 1 && reach.Count == 2 && Math.Abs(dropped - 36) < 1);

            Check("floor names: real names are capitalised words of letters", DungeonFloors.IsRealName("Ironclad Cove") && DungeonFloors.IsRealName("Halls of the Law") && DungeonFloors.IsRealName("Ragnaros' Lair") && !DungeonFloors.IsRealName("cave2_18") && !DungeonFloors.IsRealName("DerRing") && !DungeonFloors.IsRealName("LAVA") && !DungeonFloors.IsRealName(""));
        }

        // ---------------------------------------------------------------- the pictures and the plan file
        void DmImageChecks(CheckFn Check, string work)
        {
            var geo = new PlanGeometry { Units = { DmStacked() } };
            var floors = DungeonFloors.Split(geo, "Gloomhold");
            var fr = DungeonPlanImage.FrameOf(floors);
            var pics = DungeonPlanImage.DrawAll(floors, fr);
            byte At(int k, double x, double y) { int px = (int)fr.Px(y), py = (int)fr.Py(x); return pics[k][py * fr.Width + px]; }
            Check("frame: north up and east right, 3 pixels per yard at most, the floors share it (the picture of a 106 x 56 yard plan with an 8 yard margin)", fr.Scale == 3 && fr.Width == (int)Math.Ceiling(56 * 3.0) && fr.Height == (int)Math.Ceiling(106 * 3.0) && pics.Count == 2 && pics.All(p => p.Length == fr.Width * fr.Height), fr.Width + "x" + fr.Height + " @" + fr.Scale);
            var big = DungeonPlanImage.FrameOf(new[] { new PlanFloor { Units = { DmUnit("x", (0, 3000, 0, 1500, 0, 0)) } } });
            Check("frame: a big plan is at most MaxSide pixels long", Math.Max(big.Width, big.Height) <= DungeonPlanImage.MaxSide + 1 && big.Scale < 1);
            byte hall = At(0, 20, 7), gallery = At(1, 20, 30), outside = At(0, 60, 12), ghost = At(1, 20, 7), edge = At(0, 0.1, 7);
            Check("pictures: the floor is floor (roles 1-32), outside it nothing (0), the other floor shows faintly under this one (33), the edge is outline (34-37)",
                hall >= DungeonPlanImage.FloorLow && hall <= DungeonPlanImage.FloorHigh && gallery >= DungeonPlanImage.FloorLow && gallery <= DungeonPlanImage.FloorHigh && outside == DungeonPlanImage.Empty && ghost == DungeonPlanImage.Ghost && edge >= DungeonPlanImage.Edge25 && edge <= DungeonPlanImage.Edge100,
                hall + " " + gallery + " " + outside + " " + ghost + " " + edge);
            // a ledge: two halves of one room 5 yards apart in height
            var ledgeFloors = DungeonFloors.Split(new PlanGeometry { Units = { DmUnit("Steps", (0, 40, 0, 20, 0, 0), (0, 40, 20, 40, 5, 5)) } }, "x");
            var lfr = DungeonPlanImage.FrameOf(ledgeFloors);
            var lp = DungeonPlanImage.DrawAll(ledgeFloors, lfr);
            int ly = (int)lfr.Py(20), lx0 = (int)lfr.Px(19.5), lx1 = (int)lfr.Px(20.5);
            bool ledge = Enumerable.Range(Math.Min(lx0, lx1) - 1, Math.Abs(lx1 - lx0) + 3).Any(x => lp[0][ly * lfr.Width + x] == DungeonPlanImage.Ledge);
            Check("pictures: a 5 yard drop inside a floor is drawn as a ledge line (38)", ledgeFloors.Count == 1 && ledge, ledgeFloors.Count + "");
            // a crack in a floor is closed
            var cracked = DungeonFloors.Split(new PlanGeometry { Units = { DmUnit("Cracked", (0, 30, 0, 40, 0, 0), (30.8f, 60, 0, 40, 0, 0)) } }, "x");
            var cfr = DungeonPlanImage.FrameOf(cracked); var cp = DungeonPlanImage.DrawAll(cracked, cfr);
            byte crack = cp[0][(int)cfr.Py(30.4) * cfr.Width + (int)cfr.Px(20)];
            Check("pictures: a crack under 3 yards between triangles of a floor is closed (no outline through the room)", crack >= DungeonPlanImage.FloorLow && crack <= DungeonPlanImage.FloorHigh, crack + "");

            // the PNG and the colours
            var png = Path.Combine(work, "roles.png");
            DungeonPlanImage.WritePng(png, pics[0], fr.Width, fr.Height);
            var back = DungeonPlanImage.ReadPng(png, out var bw, out var bh);
            Check("PNG: the role picture comes back byte for byte (8-bit, a few KB)", back != null && bw == fr.Width && bh == fr.Height && back.SequenceEqual(pics[0]) && new FileInfo(png).Length < 64 * 1024, new FileInfo(png).Length + " bytes");
            File.WriteAllBytes(Path.Combine(work, "bad.png"), new byte[] { 1, 2, 3 });
            Check("PNG: a damaged file is null, not a crash", DungeonPlanImage.ReadPng(Path.Combine(work, "bad.png"), out _, out _) == null && DungeonPlanImage.ReadPng(Path.Combine(work, "missing.png"), out _, out _) == null);
            var colors = AtlasMap.ThemeColors();
            var bmp = DungeonPlanImage.Colour(pics[0], fr.Width, fr.Height, colors);
            var one = new byte[4]; bmp.CopyPixels(new Int32Rect((int)fr.Px(7), (int)fr.Py(20), 1, 1), one, 4, 0);
            var none = new byte[4]; bmp.CopyPixels(new Int32Rect((int)fr.Px(12), (int)fr.Py(60), 1, 1), none, 4, 0);
            var s3 = ((SolidColorBrush)Application.Current.FindResource("Brush.Surface3")).Color;
            Check("colours: the theme's tokens colour the roles (the floor between Surface3 and a lighter mix with Secondary text, nothing transparent)",
                bmp.IsFrozen && one[3] == 255 && one[2] >= s3.R && one[2] <= colors.FloorHigh.R + 1 && none[3] == 0, string.Join(",", one) + " / " + string.Join(",", none));

            // the plan file
            var dir = Path.Combine(work, "plan");
            var plan = DungeonPlans.Make(900, "Gloomhold", geo, dir);
            plan.Ms = 12;
            File.WriteAllText(Path.Combine(dir, "plan.json"), plan.ToJson());
            var re = DungeonPlan.FromJson(File.ReadAllText(Path.Combine(dir, "plan.json")), dir);
            Check("plan file: the floors, frames and facts survive the round trip", re != null && re.MapId == 900 && re.Floors.Count == plan.Floors.Count && re.Floors.Select(f => f.Name).SequenceEqual(plan.Floors.Select(f => f.Name)) && re.Floors[0].Width == fr.Width && Math.Abs(re.Floors[0].Scale - fr.Scale) < 1e-9 && re.Ms == 12 && re.Source == "buildings");
            var corner = re.WorldToFraction(0, re.Floors[0].MaxX, re.Floors[0].MaxY);
            Check("plan: the frame's corner is the picture's top-left (0, 0); a spot in the hall is on floor 1, the same spot 20 yards up is on floor 2 (FloorAt by the pictures and the heights)",
                Math.Abs(corner.X) < 1e-9 && Math.Abs(corner.Y) < 1e-9 && re.FloorAt(20, 30, 0) == 0 && re.FloorAt(20, 30, 20) == 1 && re.FloorAt(85, 5, 20) == 0 && re.FloorAt(-500) == 0, re.FloorAt(20, 30, 0) + " " + re.FloorAt(20, 30, 20) + " " + re.FloorAt(85, 5, 20));
            File.Delete(Path.Combine(dir, plan.Floors[1].File));
            Check("plan file: a missing floor picture or another format makes it unusable (it is drawn again)", DungeonPlan.FromJson(File.ReadAllText(Path.Combine(dir, "plan.json")), dir) == null && DungeonPlan.FromJson("{\"format\":99}", dir) == null && DungeonPlan.FromJson("garbage", dir) == null);
            Check("estimate text: seconds and size in plain words", DungeonPlans.EstimateText(3, 200 * 1024) == "a few seconds, 200 KB on disk" && DungeonPlans.EstimateText(32, 2 * 1024 * 1024).StartsWith("about 35 seconds") && DungeonPlans.EstimateText(150, 0).StartsWith("about 3 minutes"), DungeonPlans.EstimateText(3, 200 * 1024));
        }

        // ---------------------------------------------------------------- event and placeholder instances
        void DmEventChecks(CheckFn Check)
        {
            var set = new MapSet { HasMaps = true };
            set.Maps.Add(new GameMapInfo { Id = 36, Name = "Deadmines", Directory = "DeadminesInstance", InstanceType = 1, MaxPlayers = 10, CorpseMapId = 0, CorpseX = 100, CorpseY = 100 });
            set.Maps.Add(new GameMapInfo { Id = 29, Name = "CashTest", Directory = "TEST_01", InstanceType = 1, MaxPlayers = 10, CorpseMapId = -1 });
            set.Maps.Add(new GameMapInfo { Id = 28, Name = "Old Hall", Directory = "TEST_02", InstanceType = 1, CorpseMapId = 0, CorpseX = 10, CorpseY = 10 });
            set.Maps.Add(new GameMapInfo { Id = 2784, Name = "Demon Fall Canyon", Directory = "2784", InstanceType = 1, MaxPlayers = 5, CorpseMapId = -1 });
            set.Maps.Add(new GameMapInfo { Id = 2959, Name = "City of Dalaran", Directory = "2959", InstanceType = 1, CorpseMapId = -1 });
            set.Maps.Add(new GameMapInfo { Id = 2995, Name = "Hyjal Crater", Directory = "2995", InstanceType = 4 });
            set.Maps.Add(new GameMapInfo { Id = 2997, Name = "Darkspear Islands", Directory = "2997", InstanceType = 3 });
            set.UiMaps.Add(new UiMapInfo { Id = 5000, Name = "Darkspear Islands", Type = UiMapInfo.Orphan });
            set.Assignments.Add(new UiAssignment { UiMapId = 5000, MapId = 2997, UiMaxX = 1, UiMaxY = 1, MinX = -100, MaxX = 100, MinY = -100, MaxY = 100 });
            set.HasAssignments = true;
            var idx = new MapIndex(set);
            var dm = idx.Instance(36); var cash = idx.Instance(29); var old = idx.Instance(28); var dfc = idx.Instance(2784); var dal = idx.Instance(2959); var hy = idx.Instance(2995); var ds = idx.Instance(2997);
            Check("instances: a dungeon with a way in from the world is a dungeon; one the game puts no ghost of on a continent (Demon Fall Canyon, City of Dalaran) is an event or unreleased map; an arena with no map (Hyjal Crater) too; a battleground with a map is not",
                !dm.Event && !dm.Placeholder && dfc.Event && !dfc.Placeholder && dal.Event && hy.Event && !ds.Event, dm.Event + " " + dfc.Event + " " + dal.Event + " " + hy.Event + " " + ds.Event);
            Check("instances: test leftovers are placeholders by name (\"CashTest\") or folder (\"TEST_02\"), never by a number folder (\"2784\"); real words with the letters are names (\"Contest\", \"Testament\")",
                cash.Placeholder && old.Placeholder && !cash.Event && MapIndex.IsPlaceholderName("CashTest") && !MapIndex.IsPlaceholderName("Contest") && !MapIndex.IsPlaceholderName("Testament") && !MapIndex.IsTestDirectory("2784") && MapIndex.IsTestDirectory("TEST_01") && MapIndex.IsTestDirectory("test"));
            Check("instances: the detail line says it (\"Event or unreleased · Dungeon · 5 players\"); a real one does not", AtlasQuery.InstanceDetail(dfc) == "Event or unreleased · Dungeon · 5 players" && AtlasQuery.InstanceDetail(dm) == "Dungeon · 10 players", AtlasQuery.InstanceDetail(dfc));
            MapData.SetForTest(idx); AtlasBrowse.ForgetForTest();
            try
            {
                var br = AtlasBrowse.Run("browse:instances"); var ev = AtlasBrowse.Run("browse:instances/events"); var du = AtlasBrowse.Run("browse:instances/dungeons");
                Check("browse: the events and unreleased maps are listed apart (\"Events and unreleased\": Demon Fall Canyon, City of Dalaran, Hyjal Crater), not among the dungeons; placeholders nowhere",
                    br.Children.Any(c => c.Title == "Events and unreleased") && ev.Rows.Select(r => r.Title).OrderBy(t => t).SequenceEqual(new[] { "City of Dalaran", "Demon Fall Canyon", "Hyjal Crater" }) && du.Rows.Select(r => r.Title).SequenceEqual(new[] { "Deadmines" }) && !br.Rows.Any(r => r.Title == "CashTest" || r.Title == "Old Hall"),
                    string.Join(",", br.Children.Select(c => c.Title)) + " | " + string.Join(",", ev.Rows.Select(r => r.Title)) + " | " + string.Join(",", du.Rows.Select(r => r.Title)));
            }
            finally { MapData.ResetForTest(); AtlasBrowse.ForgetForTest(); }
        }
    }
}
