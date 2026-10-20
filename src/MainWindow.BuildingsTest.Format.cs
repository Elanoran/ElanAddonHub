using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ElansAddonHub.Services;
using ElansAddonHub.Services.WorldMap;

namespace ElansAddonHub
{
    // --selftest "buildings", part 1: the formats on synthetic chunks (nothing of the game's files).
    public partial class MainWindow
    {
        // ---------------------------------------------------------------- synthetic files, as the game writes them (chunk tags reversed on disk)
        static byte[] BldChunk(string tag, byte[] body, bool reversed = true)
        {
            var ms = new MemoryStream(); var w = new BinaryWriter(ms);
            w.Write(Encoding.ASCII.GetBytes(reversed ? new string(tag.Reverse().ToArray()) : tag)); w.Write(body.Length); w.Write(body); w.Flush();
            return ms.ToArray();
        }
        static byte[] BldCat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

        sealed class BldRec
        {
            public uint Name = 1, Uid = 1; public float X, Y, Z, Rx, Ry, Rz; public float[] Min = new float[3], Max = new float[3];
            public ushort Flags, Ds, Ns, Scale = 1024;
        }

        static byte[] BldModfBody(params BldRec[] l)
        {
            var ms = new MemoryStream(); var w = new BinaryWriter(ms);
            foreach (var r in l)
            {
                w.Write(r.Name); w.Write(r.Uid); w.Write(r.X); w.Write(r.Y); w.Write(r.Z); w.Write(r.Rx); w.Write(r.Ry); w.Write(r.Rz);
                foreach (var f in r.Min) w.Write(f); foreach (var f in r.Max) w.Write(f);
                w.Write(r.Flags); w.Write(r.Ds); w.Write(r.Ns); w.Write(r.Scale);
            }
            w.Flush(); return ms.ToArray();
        }

        // an obj0 ADT: version, a doodad chunk (ignored), optionally the old name table, the WMO placements
        static byte[] BldObj(BldRec[] recs, bool nameTable = false, bool reversed = true, bool truncateModf = false)
        {
            var modf = BldModfBody(recs); if (truncateModf) modf = modf.Take(modf.Length - 20).ToArray();
            return BldCat(BldChunk("MVER", BitConverter.GetBytes(18), reversed), BldChunk("MDDF", new byte[72], reversed),
                nameTable ? BldCat(BldChunk("MWMO", Encoding.ASCII.GetBytes("world\\wmo\\x.wmo\0"), reversed), BldChunk("MWID", new byte[4], reversed)) : new byte[0],
                BldChunk("MODF", modf, reversed));
        }

        // a WMO root: nGroups groups with the given flags and file ids, nMaterials = textures.Length
        static byte[] BldRoot(float[] bbox, uint[] groupFlags, int[] groupFdids, uint[] textures, bool reversed = true, bool gfid = true, int numLod = 1, uint[] matColors = null)
        {
            int ng = groupFlags.Length;
            var mohd = new MemoryStream(); var w = new BinaryWriter(mohd);
            w.Write(textures.Length); w.Write(ng); w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(1); w.Write(0); w.Write(7);
            foreach (var f in bbox) w.Write(f);
            w.Write((ushort)0x41F); w.Write((ushort)numLod); w.Flush();
            var momt = new MemoryStream(); var m = new BinaryWriter(momt);
            for (int i = 0; i < textures.Length; i++)
            {
                var mb = new byte[64]; Array.Copy(BitConverter.GetBytes(textures[i]), 0, mb, 12, 4);
                if (matColors != null) Array.Copy(BitConverter.GetBytes(matColors[i]), 0, mb, 28, 4);
                m.Write(mb);
            }
            m.Flush();
            var mogi = new MemoryStream(); var g = new BinaryWriter(mogi);
            for (int i = 0; i < ng; i++) { g.Write(groupFlags[i]); foreach (var f in bbox) g.Write(f); g.Write(-1); }
            g.Flush();
            var gf = new MemoryStream(); var gw = new BinaryWriter(gf);
            for (int l = 0; l < numLod; l++) for (int i = 0; i < ng; i++) gw.Write((uint)(l == 0 ? groupFdids[i] : 0)); gw.Flush();
            return BldCat(BldChunk("MVER", BitConverter.GetBytes(17), reversed), BldChunk("MOHD", mohd.ToArray(), reversed), BldChunk("MOMT", momt.ToArray(), reversed), BldChunk("MOGI", mogi.ToArray(), reversed),
                gfid ? BldChunk("GFID", gf.ToArray(), reversed) : new byte[0]);
        }

        // a group file: header (68 bytes, flags at +8), MOPY, MOVI, MOVT, MONR, MOBA; vertices in WoW axes (x north, y west, z up)
        static byte[] BldGroup(uint flags, float[][] v, ushort[] idx, byte[] triMat, bool normals = true, bool batches = true, bool reversed = true, int batchStart = 0, int batchCount = -1)
        {
            var body = new MemoryStream(); var w = new BinaryWriter(body);
            var head = new byte[68]; Array.Copy(BitConverter.GetBytes(flags), 0, head, 8, 4); w.Write(head);
            void Sub(string tag, byte[] b) { w.Write(Encoding.ASCII.GetBytes(reversed ? new string(tag.Reverse().ToArray()) : tag)); w.Write(b.Length); w.Write(b); }
            var py = new byte[triMat.Length * 2]; for (int i = 0; i < triMat.Length; i++) { py[i * 2] = 0x10; py[i * 2 + 1] = triMat[i]; }
            Sub("MOPY", py);
            Sub("MOVI", idx.SelectMany(i => BitConverter.GetBytes(i)).ToArray());
            Sub("MOVT", v.SelectMany(p => p.SelectMany(f => BitConverter.GetBytes(f))).ToArray());
            if (normals) Sub("MONR", v.SelectMany(p => new[] { 0f, 0f, 1f }.SelectMany(f => BitConverter.GetBytes(f))).ToArray());
            if (batches)
            {
                var ba = new byte[24]; Array.Copy(BitConverter.GetBytes((uint)batchStart), 0, ba, 12, 4); Array.Copy(BitConverter.GetBytes((ushort)(batchCount < 0 ? idx.Length - batchStart : batchCount)), 0, ba, 16, 2);
                Sub("MOBA", ba);
            }
            w.Flush();
            return BldCat(BldChunk("MVER", BitConverter.GetBytes(17), reversed), BldChunk("MOGP", body.ToArray(), reversed));
        }

        // a box in WoW axes: x in [x0, x1] (north), y in [y0, y1] (west), z in [z0, z1] (up); 8 vertices, 12 triangles
        static void BldBox(float x0, float x1, float y0, float y1, float z0, float z1, out float[][] v, out ushort[] idx)
        {
            v = new float[8][];
            for (int i = 0; i < 8; i++) v[i] = new[] { (i & 1) == 0 ? x0 : x1, (i & 2) == 0 ? y0 : y1, (i & 4) == 0 ? z0 : z1 };
            idx = new ushort[] { 0, 1, 3, 0, 3, 2, 4, 6, 7, 4, 7, 5, 0, 4, 5, 0, 5, 1, 2, 3, 7, 2, 7, 6, 0, 2, 6, 0, 6, 4, 1, 5, 7, 1, 7, 3 };
        }

        // a WDT with the obj0 file of every tile as well (root = 1070000 + row x 64 + col, obj0 = 2000000 + row x 64 + col, minimap = the world map tests' ids)
        static byte[] BldWdt(IEnumerable<Tuple<int, int>> tiles)
        {
            var ms = new MemoryStream(); var w = new BinaryWriter(ms);
            void Chunk(string tag, byte[] body) { w.Write(Encoding.ASCII.GetBytes(new string(tag.Reverse().ToArray()))); w.Write(body.Length); w.Write(body); }
            Chunk("MVER", BitConverter.GetBytes(18)); Chunk("MPHD", new byte[32]);
            var list = tiles.ToList();
            var main = new byte[4096 * 8]; foreach (var t in list) Array.Copy(BitConverter.GetBytes(1u), 0, main, (t.Item2 * 64 + t.Item1) * 8, 4);
            Chunk("MAIN", main);
            var m = new byte[131072];
            foreach (var t in list)
            {
                int o = (t.Item2 * 64 + t.Item1) * 32;
                Array.Copy(BitConverter.GetBytes((uint)(1070000 + t.Item2 * 64 + t.Item1)), 0, m, o, 4);
                Array.Copy(BitConverter.GetBytes((uint)(2000000 + t.Item2 * 64 + t.Item1)), 0, m, o + 4, 4);
                Array.Copy(BitConverter.GetBytes((uint)WmFdid(t.Item1, t.Item2)), 0, m, o + 28, 4);
            }
            Chunk("MAID", m);
            return ms.ToArray();
        }

        // ground that is a function of the scene position
        sealed class BldGround : IGroundSampler
        {
            public Func<double, double, double> G = (x, z) => 10, W = (x, z) => double.NaN;
            public double Ground(double x, double z) => G(x, z);
            public double Water(double x, double z) => W(x, z);
        }

        // a bare 16 x 16 raw BGRA texture of one colour, as a BLP2 (type 1, encoding 3)
        static byte[] BldBlp(byte r, byte g, byte b, int size = 16)
        {
            var h = new byte[148 + size * size * 4];
            h[0] = (byte)'B'; h[1] = (byte)'L'; h[2] = (byte)'P'; h[3] = (byte)'2'; Array.Copy(BitConverter.GetBytes(1u), 0, h, 4, 4); h[8] = 3; h[9] = 8; h[10] = 8;
            Array.Copy(BitConverter.GetBytes(size), 0, h, 12, 4); Array.Copy(BitConverter.GetBytes(size), 0, h, 16, 4);
            Array.Copy(BitConverter.GetBytes(148), 0, h, 20, 4); Array.Copy(BitConverter.GetBytes(size * size * 4), 0, h, 84, 4);
            for (int i = 0; i < size * size; i++) { h[148 + i * 4] = b; h[148 + i * 4 + 1] = g; h[148 + i * 4 + 2] = r; h[148 + i * 4 + 3] = 255; }
            return h;
        }

        void BuildingsFormatChecks(CheckFn Check)
        {
            var I = CultureInfo.InvariantCulture;
            // ---------------------------------------------------------------- the placements (MODF of an obj0 ADT)
            var r1 = new BldRec { Name = 7962574, Uid = 10047, X = 16527.4f, Y = 102f, Z = 25998.3f, Ry = 38.5f, Min = new[] { 15603.4f, 2.2f, 24986.9f }, Max = new[] { 17091.7f, 378.6f, 26475f }, Flags = 12, Scale = 1024 };
            var r2 = new BldRec { Name = 108135, Uid = 2, X = 19347.4f, Y = 62.2f, Z = 26414.1f, Ry = 355.5f, Min = new[] { 19306.8f, 44.3f, 26347.5f }, Max = new[] { 19382.6f, 77.5f, 26480.2f }, Scale = 0 };
            var r3 = new BldRec { Name = 5, Uid = 3, X = 100, Y = 5, Z = 200, Rx = 16.5f, Rz = 1.5f, Scale = 2048, Min = new[] { 90f, 0f, 190f }, Max = new[] { 110f, 10f, 210f } };
            var rBad = new BldRec { Name = 9, Uid = 4, X = float.NaN, Y = 1, Z = 1 };
            var rZero = new BldRec { Name = 0, Uid = 5, X = 1, Y = 1, Z = 1 };
            var ps = AdtObjects.ParsePlacements(BldObj(new[] { r1, r2, r3, rBad, rZero }), out var prob);
            Check("MODF: the 64-byte records of an obj0 file are read (reversed tags): name = the WMO's FileDataID, unique id, position, rotation in degrees, the placed box, flags, the scale 1024 = 1 (0 = 1, 2048 = 2); a position that is NaN and a name of 0 are dropped",
                prob == null && ps.Count == 3 && ps[0].NameId == 7962574 && ps[0].UniqueId == 10047 && Math.Abs(ps[0].X - 16527.4f) < 0.01 && ps[0].RotY == 38.5f && ps[0].MinX == 15603.4f && ps[0].MaxY == 378.6f && ps[0].Flags == 12 && ps[0].Scale == 1f
                && ps[1].Scale == 1f && ps[2].Scale == 2f && ps[2].RotX == 16.5f && ps[2].RotZ == 1.5f && !ps.Any(p => p.HasNameIndex), prob + " " + ps.Count);
            var fwd = AdtObjects.ParsePlacements(BldObj(new[] { r1 }, false, false), out _);
            var nameTab = AdtObjects.ParsePlacements(BldObj(new[] { r1 }, true), out _);
            var cut = AdtObjects.ParsePlacements(BldObj(new[] { r1, r2 }, false, true, true), out _);
            var none = AdtObjects.ParsePlacements(BldCat(BldChunk("MVER", BitConverter.GetBytes(18)), BldChunk("MDDF", new byte[72])), out _);
            var junk = AdtObjects.ParsePlacements(new byte[] { 1, 2, 3 }, out var jp); var nul = AdtObjects.ParsePlacements(null, out var np);
            Check("MODF: tags the right way round are read too; a file that still has the old name table (MWID / MWMO) is flagged so its records can be left out; a cut-off chunk gives its whole records; no MODF = no buildings; junk or nothing is a problem, never an exception",
                fwd.Count == 1 && fwd[0].NameId == 7962574 && nameTab.Count == 1 && nameTab[0].HasNameIndex && cut.Count == 1 && cut[0].NameId == 7962574 && none.Count == 0 && junk.Count == 0 && jp != null && nul.Count == 0 && np != null);

            // ---------------------------------------------------------------- the WMO root and group files
            BldBox(-10, 10, -3, 3, -1, 0.5f, out var bv, out var bi);
            var rootBytes = BldRoot(new[] { -10f, -3f, -1f, 10f, 3f, 0.5f }, new uint[] { 0x8, 0x2000, 0x8 | 0x2000, 0 }, new[] { 601, 602, 603, 604 }, new uint[] { 1111, 0 }, matColors: new uint[] { 0xFF102030, 0x00FFFFFF });
            var root = WmoRoot.Parse(rootBytes);
            var og = root.OutsideGroups().Select(g => g.Fdid).ToList();
            Check("WMO root: MOHD (materials, groups, the box), MOMT (the texture FileDataID and colour of each material), MOGI (flags and box of each group), GFID (the group files) are read from a file with reversed tags",
                root.Ok && root.Materials == 2 && root.Groups == 4 && root.NumLod == 1 && root.MinX == -10 && root.MaxX == 10 && root.MinY == -3 && root.MaxY == 3 && root.MinZ == -1 && root.MaxZ == 0.5f && root.TextureFdid[0] == 1111 && root.TextureFdid[1] == 0
                && root.MaterialColor[0] == 0xFF102030 && root.GroupInfos.Count == 4 && root.GroupInfos[0].Fdid == 601 && root.GroupInfos[0].Flags == 8 && root.GroupInfos[1].Flags == 0x2000, root.Problem);
            Check("WMO root: only the groups seen from outside are drawn - the exterior ones (flag 0x8, also when marked interior too); a building with none falls back to the groups that are not interior; interior-only is nothing",
                og.SequenceEqual(new[] { 601, 603 }) && WmoRoot.Parse(BldRoot(new float[6], new uint[] { 0, 0x2000 }, new[] { 1, 2 }, new uint[0])).OutsideGroups().Select(g => g.Fdid).SequenceEqual(new[] { 1 })
                && WmoRoot.Parse(BldRoot(new float[6], new uint[] { 0x2000, 0x2000 }, new[] { 1, 2 }, new uint[0])).OutsideGroups().Count == 0);
            // 2.89: a city built as one WMO (Orgrimmar: 2 exterior groups, 19 open-air streets flagged interior + exterior lit 0x2040, 117 rooms 0x2000) - the streets are drawn,
            // the rooms are not; before, only 0x8 counted and Orgrimmar was a gate and a canyon wall
            var city = WmoRoot.Parse(BldRoot(new float[6], new uint[] { 0x80000408, 0x2040, 0x2000, 0x202040, 0x2400, 0x2000 | 0x40 | 0x100 }, new[] { 701, 702, 703, 704, 705, 706 }, new uint[0]));
            var cityOg = city.OutsideGroups().Select(g => g.Fdid).ToList();
            Check("WMO root: a whole city in one model (Orgrimmar, Stormwind) - its open-air streets are flagged interior + exterior lit (0x2040) and are drawn with the exterior groups; rooms (interior only, 0x2000 / 0x2400) stay out; ExteriorLitOnlyGroups counts the groups the old exterior-only rule left out ("
                + string.Join(",", cityOg) + "; " + city.ExteriorLitOnlyGroups() + ")",
                cityOg.SequenceEqual(new[] { 701, 702, 704, 706 }) && city.ExteriorLitOnlyGroups() == 3 && root.ExteriorLitOnlyGroups() == 0 && WmoRoot.IsOutside(0x2040) && !WmoRoot.IsOutside(0x2000) && WmoRoot.IsOutside(0x8));
            Check("buildings: the nearest level's triangle budget grows with the triangles a model has, not only its groups - Orgrimmar (21 groups, 234,000 triangles) keeps " + WorldMapBuildings.Lod0Cap(21, 234000) + " (was 26,900), a house keeps 8,900, a huge city never more than 150,000",
                WorldMapBuildings.Lod0Cap(21, 234000) == 117000 && WorldMapBuildings.Lod0Cap(1, 3000) == 8900 && WorldMapBuildings.Lod0Cap(100, 364000) == 150000 && WorldMapBuildings.Lod0Cap(300, 0) == 150000 && WorldMapBuildings.Lod0Cap(50, 98000) == 53000);
            var lod4 = WmoRoot.Parse(BldRoot(new float[6], new uint[] { 8, 8 }, new[] { 10, 11 }, new uint[0], numLod: 4));
            Check("WMO root: numLod 4 (the GFID holds four lists, the first is the full-detail groups: the other three are not used); wrong roots are a problem, not an exception - no MOHD, an impossible group count, no GFID, cut off, junk",
                lod4.Ok && lod4.NumLod == 4 && lod4.GroupInfos.Count == 2 && lod4.GroupInfos.All(g => g.Fdid >= 10)
                && !WmoRoot.Parse(BldChunk("MVER", BitConverter.GetBytes(17))).Ok && !WmoRoot.Parse(new byte[] { 1, 2, 3 }).Ok && !WmoRoot.Parse(null).Ok && WmoRoot.Parse(BldRoot(new float[6], new uint[] { 8 }, new[] { 1 }, new uint[0], gfid: false)).Problem != null
                && !WmoRoot.Parse(rootBytes.Take(40).ToArray()).Ok);
            var gb = BldGroup(0x8009, bv, bi, Enumerable.Range(0, 12).Select(i => (byte)(i < 6 ? 0 : 1)).ToArray());
            var gm = WmoGroupMesh.Parse(gb);
            Check("WMO group: MOGP flags, MOVT vertices into the scene's axes ((x, y, z) of the game = (y, z, x): nothing mirrored), MONR normals, MOVI triangles and the material of each triangle from MOPY",
                gm.Ok && gm.Flags == 0x8009 && gm.Pos.Length == 24 && gm.Idx.Length == 36 && gm.Mat.Length == 12 && gm.Pos[0] == -3 && gm.Pos[1] == -1 && gm.Pos[2] == -10 && gm.Pos[21] == 3 && gm.Pos[22] == 0.5f && gm.Pos[23] == 10 && gm.Nor.Length == 24 && gm.Nor[0] == 0 && gm.Nor[1] == 1 && gm.Nor[2] == 0
                && gm.Mat.Take(6).All(m => m == 0) && gm.Mat.Skip(6).All(m => m == 1), gm.Problem);
            var gNoBatch = WmoGroupMesh.Parse(BldGroup(8, bv, bi, new byte[12], batches: false, normals: false));
            var gPart = WmoGroupMesh.Parse(BldGroup(8, bv, bi, new byte[12], batchStart: 6, batchCount: 12));
            var mats = new byte[12]; mats[3] = 0xFF; mats[4] = 0xFF;
            var gNoMat = WmoGroupMesh.Parse(BldGroup(8, bv, bi, mats));
            var gDegen = WmoGroupMesh.Parse(BldGroup(8, bv, new ushort[] { 0, 0, 1, 0, 1, 2, 0, 1, 99 }, new byte[3]));
            Check("WMO group: the triangles drawn are the batches' ranges (a batch of 12 indices from index 6 = triangles 2 and 3 and so on); no batch = every triangle; no normals = none; a triangle with the material 0xFF (collision only), a degenerate one and one with a vertex that does not exist are left out",
                gNoBatch.Ok && gNoBatch.Idx.Length == 36 && gNoBatch.Nor.Length == 0 && gPart.Ok && gPart.Idx.Length == 12 && gPart.Idx[0] == bi[6] && gNoMat.Idx.Length == 30 && gDegen.Ok && gDegen.Idx.Length == 3, gNoBatch.Idx.Length + " " + gPart.Idx.Length + " " + gNoMat.Idx.Length + " " + gDegen.Idx.Length);
            Check("WMO group: a group without MOGP, vertices or triangles, cut off in the middle of its vertices, junk or nothing is a problem and no mesh, never an exception",
                !WmoGroupMesh.Parse(BldChunk("MVER", BitConverter.GetBytes(17))).Ok && !WmoGroupMesh.Parse(BldGroup(8, new float[0][], new ushort[0], new byte[0])).Ok && !WmoGroupMesh.Parse(new byte[] { 9 }).Ok && !WmoGroupMesh.Parse(null).Ok
                && Assert(() => WmoGroupMesh.Parse(gb.Take(150).ToArray())) == null);

            // ---------------------------------------------------------------- the transform
            // yaw 90 degrees, scale 2: the game's (x, y, z) = (4, 1, 3) -> local (y, z, x) x 2 = (2, 6, 8) -> Ry(90): x' = z = 8, z' = -x = -2 -> + position
            var pl = new WmoPlacement { X = 1000, Y = 50, Z = 2000, RotY = 90, Scale = 2 };
            WmoTransform.ToScene(pl, 4, 1, 3, out var tx, out var ty, out var tz);
            Check("transform: a point of the building goes (x, y, z) -> (y, z, x), is scaled, turned by Ry(rotY) and moved to the position: (4, 1, 3) at yaw 90, scale 2 -> (" + tx.ToString("0.###", I) + ", " + ty.ToString("0.###", I) + ", " + tz.ToString("0.###", I) + ")",
                Math.Abs(tx - 1008) < 1e-6 && Math.Abs(ty - 56) < 1e-6 && Math.Abs(tz - 1998) < 1e-6);
            // tilt: pitch 90 (rotX) of the local point (0, 1, 0)(its y) turns y into z: Rx(90): y' = y cos - z sin = 0, z' = y sin + z cos = 1
            var pt = new WmoPlacement { RotX = 90, Scale = 1 };
            WmoTransform.ToScene(pt, 0, 0, 1, out var ax, out var ay, out var az);        // game (0, 0, 1) = up -> local (0, 1, 0)
            var pr = new WmoPlacement { RotZ = 90, Scale = 1 };
            WmoTransform.ToScene(pr, 1, 0, 0, out var bx, out var by, out var bz);        // game (1, 0, 0) = north -> local (0, 0, 1): Rz leaves z alone
            Check("transform: the order is Ry Rx Rz (three.js 'YXZ', checked on 300 tilted buildings of the real game: the placed box agrees to " + "0.02 yards): up turned by 90 degrees of rotX goes to south (+z), rotZ leaves the south axis alone",
                Math.Abs(ax) < 1e-6 && Math.Abs(ay) < 1e-6 && Math.Abs(az - 1) < 1e-6 && Math.Abs(bx) < 1e-6 && Math.Abs(by) < 1e-6 && Math.Abs(bz - 1) < 1e-6, ax + "," + ay + "," + az + " " + bx + "," + by + "," + bz);
            // a whole box: the placed AABB equals the stored extents of such a placement (what the game's own MODF extents are)
            var plx = new WmoPlacement { X = 500, Y = 20, Z = 800, RotY = 30, Scale = 1.5f };
            double mnx = 1e9, mxx = -1e9, mnz = 1e9, mxz = -1e9, mny = 1e9, mxy = -1e9;
            for (int c = 0; c < 8; c++) { WmoTransform.ToScene(plx, (c & 1) == 0 ? -10 : 10, (c & 2) == 0 ? -3 : 3, (c & 4) == 0 ? -1 : 0.5, out var x, out var y, out var z); mnx = Math.Min(mnx, x); mxx = Math.Max(mxx, x); mny = Math.Min(mny, y); mxy = Math.Max(mxy, y); mnz = Math.Min(mnz, z); mxz = Math.Max(mxz, z); }
            double th = 30 * Math.PI / 180, ex = 1.5 * (3 * Math.Cos(th) + 10 * Math.Sin(th)), ez = 1.5 * (3 * Math.Sin(th) + 10 * Math.Cos(th));
            Check("transform: the box of a 20 x 6 yard building turned 30 degrees and scaled 1.5 spans " + (mxx - mnx).ToString("0.00", I) + " x " + (mxz - mnz).ToString("0.00", I) + " yards (hand worked " + (2 * ex).ToString("0.00", I) + " x " + (2 * ez).ToString("0.00", I) + "), centred on the position, up from 20 - 1.5 to 20 + 0.75",
                Math.Abs((mxx - mnx) - 2 * ex) < 1e-6 && Math.Abs((mxz - mnz) - 2 * ez) < 1e-6 && Math.Abs(mny - 18.5) < 1e-6 && Math.Abs(mxy - 20.75) < 1e-6 && Math.Abs((mxx + mnx) / 2 - 500 - 1.5 * (0 * Math.Cos(th))) < 1e-6);

            // ---------------------------------------------------------------- bridges
            var wbox = new float[] { -15, -3, -1, 15, 3, 1.5f };                   // 30 yards long (game x = north), 6 wide, 2.5 high
            var rBr = WmoRoot.Parse(BldRoot(wbox, new uint[] { 8 }, new[] { 1 }, new uint[0]));
            var gorge = new BldGround { G = (x, z) => Math.Abs(z - 3000) < 8 ? 0 : 10 };       // a 16 yard gorge along x at z = 3000
            var span = new WmoPlacement { X = 2000, Y = 10, Z = 3000, RotY = 90, Scale = 1 };       // yaw 90: the length (game x -> local z) lies along scene x ... see below
            // at yaw 0 the length (local z) is along scene z; at yaw 90 along scene x. The gorge runs along x, so a bridge across it lies along z: yaw 0
            var across = new WmoPlacement { X = 2000, Y = 10, Z = 3000, RotY = 0, Scale = 1 };
            var along = new WmoPlacement { X = 2000, Y = 10, Z = 3000, RotY = 90, Scale = 1 };
            Check("bridge rule: a long, low building across a gap (ground 10 yards below its deck in the middle, dry ground at both ends) is a bridge; the same building lying along the gorge or on flat ground is not, a deeper gorge counts too",
                BridgeRules.IsBridge(rBr, across, gorge) && !BridgeRules.IsBridge(rBr, along, gorge) && !BridgeRules.IsBridge(rBr, across, new BldGround()) && BridgeRules.IsBridge(rBr, new WmoPlacement { X = 2000, Y = 10, Z = 3000, Scale = 1 }, new BldGround { G = (x, z) => Math.Abs(z - 3000) < 8 ? -20 : 10 }));
            var wet = new BldGround { G = (x, z) => Math.Abs(z - 3000) < 8 ? 6 : 9.5, W = (x, z) => Math.Abs(z - 3000) < 8 ? 9.0 : double.NaN };
            var wetEnd = new BldGround { G = (x, z) => z > 3012 || Math.Abs(z - 3000) < 8 ? 6 : 9.5, W = (x, z) => z > 3012 || Math.Abs(z - 3000) < 8 ? 9.0 : double.NaN };
            var cliff = new BldGround { G = (x, z) => z > 3012 ? -20 : Math.Abs(z - 3000) < 8 ? 0 : 10 };
            var hill = new BldGround { G = (x, z) => z > 3012 ? 40 : Math.Abs(z - 3000) < 8 ? 0 : 10 };
            Check("bridge rule: a river under the deck counts as a gap (water over the ground), but water at an end (a pier or a dock) or an end 30 yards below the deck is not a bridge, while an end on a hill above the deck is fine",
                BridgeRules.IsBridge(rBr, across, wet) && !BridgeRules.IsBridge(rBr, across, new BldGround { G = (x, z) => 0, W = (x, z) => double.NaN }) && !BridgeRules.IsBridge(rBr, across, wetEnd) && !BridgeRules.IsBridge(rBr, across, cliff) && BridgeRules.IsBridge(rBr, across, hill));
            var rTall = WmoRoot.Parse(BldRoot(new float[] { -15, -3, -1, 15, 3, 60 }, new uint[] { 8 }, new[] { 1 }, new uint[0]));
            var rFat = WmoRoot.Parse(BldRoot(new float[] { -15, -20, -1, 15, 20, 1.5f }, new uint[] { 8 }, new[] { 1 }, new uint[0]));
            var rShort = WmoRoot.Parse(BldRoot(new float[] { -4, -1, -1, 4, 1, 1 }, new uint[] { 8 }, new[] { 1 }, new uint[0]));
            var rHuge = WmoRoot.Parse(BldRoot(new float[] { -90, -3, -1, 90, 3, 1.5f }, new uint[] { 8 }, new[] { 1 }, new uint[0]));
            var rMany = WmoRoot.Parse(BldRoot(wbox, new uint[] { 8, 8, 8, 8, 8 }, new[] { 1, 2, 3, 4, 5 }, new uint[0]));
            var rBroad = WmoRoot.Parse(BldRoot(new float[] { -15, -8, -1, 15, 8, 1.5f }, new uint[] { 8 }, new[] { 1 }, new uint[0]));
            var narrowGorge = new BldGround { G = (x, z) => Math.Abs(z - 3000) < 4 ? 0 : 10 };
            Check("bridge rule: a broader building (width 53 % of the length: a bridge with piers or towers sticking out) is a bridge only when every sample across the middle is a gap (the 16 yard gorge: yes, an 8 yard one: no); the slim bridge needs two of five",
                BridgeRules.IsBridge(rBroad, across, gorge) && !BridgeRules.IsBridge(rBroad, across, narrowGorge) && BridgeRules.IsBridge(rBr, across, narrowGorge));
            var deepGorge = new BldGround { G = (x, z) => Math.Abs(z - 3000) < 40 ? 0 : 10 };
            Check("bridge rule: a wall (60 yards high), a fat slab (width over 55 % of the length), a short plank (8 yards), a 180 yard viaduct and a building of five groups are not bridges, however the ground lies; no ground data = nothing is a bridge",
                !BridgeRules.IsBridge(rTall, across, gorge) && !BridgeRules.IsBridge(rFat, across, gorge) && !BridgeRules.IsBridge(rShort, across, gorge) && !BridgeRules.IsBridge(rHuge, across, deepGorge) && !BridgeRules.IsBridge(rMany, across, gorge)
                && !BridgeRules.IsBridge(rBr, across, null) && !BridgeRules.IsBridge(null, across, gorge));

            // ---------------------------------------------------------------- the simplified model
            // two groups: a box (12 triangles) and a second, shifted one; materials 0 and 1 have colours
            BldBox(-10, 10, -3, 3, -1, 0.5f, out var v1, out var i1); BldBox(-10, 10, 20, 26, -1, 0.5f, out var v2, out var i2);
            var gA = WmoGroupMesh.Parse(BldGroup(8, v1, i1, Enumerable.Repeat((byte)0, 12).ToArray())); var gB = WmoGroupMesh.Parse(BldGroup(8, v2, i2, Enumerable.Repeat((byte)1, 12).ToArray()));
            var model = WmoModel.Build(new[] { gA, gB }, mid => mid == 0 ? 0xAA2200 : 0x2255CC);
            Check("model: two box groups weld to 16 vertices (each box's corners have one colour) and 24 triangles; levels: the full building, no cluster level for a mesh that small, and the box (8 vertices, 12 triangles) in the average colour",
                model.Lods.Count == 2 && model.Lods[0].TriCount == 24 && model.Lods[0].VertexCount == 16 && model.Lods[1].TriCount == 12 && model.Lods[1].VertexCount == 8 && model.Lods[1].Colors[0] == 0x66 && model.Lods[1].Colors[1] == 0x3B && model.Lods[1].Colors[2] == 0x66,
                model.Lods.Count + " " + string.Join(",", model.Lods.Select(l => l.TriCount + "/" + l.VertexCount)) + " " + (model.Lods.Count > 1 ? BitConverter.ToString(model.Lods[model.Lods.Count - 1].Colors.Take(3).ToArray()) : ""));
            var bytes = model.ToBytes(); var back = WmoModel.FromBytes(bytes);
            float maxErr = 0;
            if (back != null) for (int v = 0; v < model.Lods[0].VertexCount; v++) { model.Vertex(0, v, out var ox, out var oy, out var oz); back.Vertex(0, v, out var qx, out var qy, out var qz); maxErr = Math.Max(maxErr, Math.Max(Math.Abs(ox - qx), Math.Max(Math.Abs(oy - qy), Math.Abs(oz - qz)))); }
            model.Vertex(0, 0, out var vx0, out var vy0, out var vz0);
            Check("model file: .wmb round trips (" + bytes.Length + " bytes, a multiple of 4 so the page can view it in place): levels, vertices to within " + maxErr.ToString("0.00000", I) + " yards, colours, indices, the bridge flag; the box is the model's own box (x -3 .. 29 from the two groups' y -3 .. 26)",
                back != null && bytes.Length % 4 == 0 && back.Lods.Count == model.Lods.Count && back.Lods[0].TriCount == 24 && back.Lods[0].Indices.SequenceEqual(model.Lods[0].Indices) && back.Lods[0].Colors.SequenceEqual(model.Lods[0].Colors) && maxErr < 0.001 && !back.Bridge
                && Math.Abs(model.Center[0] - 11.5f) < 1e-5 && Math.Abs(model.Half[0] - 14.5f) < 1e-5 && Math.Abs(model.Center[2]) < 1e-5 && Math.Abs(model.Half[2] - 10) < 1e-5, maxErr + " " + (back == null ? "null" : "ok"));
            var br = WmoModel.Build(new[] { gA }, null, new WmoBuildOptions { Bridge = true }); br.Bridge = true; var brBack = WmoModel.FromBytes(br.ToBytes());
            var gNoNor = WmoGroupMesh.Parse(BldGroup(8, bv, bi, new byte[12], normals: false)); var stone = WmoModel.Build(new[] { gNoNor }, null); var stoneUp = WmoModel.Build(new[] { gA }, null);
            Check("model: a material with no texture gets a stand-in colour by which way the triangle faces (flat tops stone grey, upright faces plaster, undersides dark); a bridge's stand-ins are wood brown; the bridge flag is in the file",
                brBack != null && brBack.Bridge && br.Lods[0].Colors[0] > br.Lods[0].Colors[2] && stone.Lods[0].Colors.Where((c, i) => i % 3 == 0).Distinct().Count() >= 2 && (br.Lods[0].Colors[0] - br.Lods[0].Colors[2]) > 2 * (stoneUp.Lods[0].Colors[0] - stoneUp.Lods[0].Colors[2]));
            var stub = new WmoModel(); var stubBack = WmoModel.FromBytes(stub.ToBytes());
            Check("model file: a building with nothing to draw is a stub (no levels, 32 bytes) that reads back as one, so it is not tried again; damaged bytes (a wrong magic, cut off, an index outside the vertices, junk) read as nothing",
                stubBack != null && stubBack.Stub && stub.ToBytes().Length == 32 && WmoModel.FromBytes(null) == null && WmoModel.FromBytes(new byte[40]) == null && WmoModel.FromBytes(bytes.Take(bytes.Length - 9).ToArray()) == null
                && WmoModel.FromBytes(Corrupt(bytes, bytes.Length - 4)) == null && WmoModel.FromBytes(new byte[] { 1, 2, 3 }) == null);
            // clustering: a 80 x 80 quad grid (12,800 triangles) kept to 500 triangles in the full level, a sixth of that in the next
            var grid = BldGrid(80, 80);
            var big = WmoModel.Build(new[] { grid }, mid => 0x808080, new WmoBuildOptions { Lod0MaxTris = 500 });
            Check("model: a building of 12,800 triangles is clustered to the cap of its full level (" + big.Lods[0].TriCount + " <= 500), the next level to a sixth of that (" + (big.Lods.Count > 2 ? big.Lods[1].TriCount.ToString() : "none") + "), the last is the box (12); every level keeps its vertices inside the model's box",
                big.Lods.Count == 3 && big.Lods[0].TriCount <= 500 && big.Lods[0].TriCount > 100 && big.Lods[1].TriCount < big.Lods[0].TriCount / 3 && big.Lods[1].TriCount >= 4 && big.Lods[2].TriCount == 12 && big.Lods.All(l => l.Pos.All(p => p >= -32767 && p <= 32767)));
            var huge = WmoModel.Build(Enumerable.Range(0, 8).Select(i => BldGrid(100, 100, i * 60f)).ToArray(), mid => 0x808080, new WmoBuildOptions { Lod0MaxTris = 1000000, WeldStep = 0.001f });
            var hugeBack = WmoModel.FromBytes(huge.ToBytes());
            Check("model file: more than 65,535 vertices use 32-bit indices (" + huge.Lods[0].VertexCount + " vertices), and the file still reads back",
                huge.Lods[0].VertexCount > 65535 && hugeBack != null && hugeBack.Lods[0].VertexCount == huge.Lods[0].VertexCount && hugeBack.Lods[0].Indices.SequenceEqual(huge.Lods[0].Indices));
            Check("model: a building with no drawn triangles (no groups, only bad groups) is a stub", WmoModel.Build(new WmoGroupMesh[0], null).Stub && WmoModel.Build(new[] { new WmoGroupMesh { Problem = "x" } }, null).Stub);

            // ---------------------------------------------------------------- textures
            Check("texture colour: the average colour of a BLP2 (here a solid 200, 120, 40) comes out as 0xC87828 (a small mip level is decoded, not the whole picture); not a BLP, empty or cut off = no colour (-1, the stand-in is used)",
                WorldMapBuildings.AverageColor(BldBlp(200, 120, 40)) == 0xC87828 && WorldMapBuildings.AverageColor(new byte[200]) == -1 && WorldMapBuildings.AverageColor(null) == -1 && WorldMapBuildings.AverageColor(BldBlp(1, 2, 3).Take(160).ToArray()) == -1);

            // ---------------------------------------------------------------- placements.bin
            var list = new List<WmoPlacement> { ps[0], ps[1], ps[2] }; var flags = new List<bool> { false, true, false };
            var pbytes = WorldMapBuildings.PlacementsToBytes(list, flags);
            var pback = WorldMapBuildings.PlacementsFromBytes(pbytes, out var pflags);
            Check("placements.bin: " + pbytes.Length + " bytes for 3 buildings (8 + 3 x 60), round trip keeps the name, position, rotation, scale, box and the bridge flag; a wrong size or magic reads as nothing",
                pbytes.Length == 8 + 3 * 60 && pback != null && pback.Count == 3 && pback[1].NameId == 108135 && pback[0].X == ps[0].X && pback[2].Scale == 2f && pback[0].MaxY == ps[0].MaxY && pflags.SequenceEqual(flags)
                && WorldMapBuildings.PlacementsFromBytes(pbytes.Take(pbytes.Length - 1).ToArray(), out _) == null && WorldMapBuildings.PlacementsFromBytes(new byte[20], out _) == null);

            // ---------------------------------------------------------------- the protocol
            WorldMapMessage Pm(string j) { var m = WorldMapProtocol.Parse(j, out _); return m; }
            string Why(string j) { WorldMapProtocol.Parse(j, out var why); return why; }
            var mb = Pm("{\"t\":\"buildings\",\"mode\":\"bridges\"}"); var mo = Pm("{\"t\":\"buildings\",\"mode\":\"off\"}"); var bb = Pm("{\"t\":\"bbuild\",\"mode\":\"all\"}");
            var stp = Pm("{\"t\":\"stats\",\"fps\":60,\"mem\":100,\"tris\":5,\"tiles\":1,\"tex\":2,\"bld\":321}");
            Check("protocol: buildings {mode off | bridges | all}, bbuild {mode bridges | all}, bpause, bresume, bcancel parse; stats carries the number of buildings drawn",
                mb != null && mb.Type == WorldMapMessageType.Buildings && mb.Detail == "bridges" && mo != null && mo.Detail == "off" && bb != null && bb.Type == WorldMapMessageType.BBuild && bb.Detail == "all" && Pm("{\"t\":\"bpause\"}").Type == WorldMapMessageType.BPause
                && Pm("{\"t\":\"bresume\"}").Type == WorldMapMessageType.BResume && Pm("{\"t\":\"bcancel\"}").Type == WorldMapMessageType.BCancel && stp != null && stp.Buildings == 321);
            var badB = new[] { "{\"t\":\"buildings\"}", "{\"t\":\"buildings\",\"mode\":\"everything\"}", "{\"t\":\"buildings\",\"mode\":3}", "{\"t\":\"buildings\",\"mode\":\"ALL\"}", "{\"t\":\"bbuild\"}", "{\"t\":\"bbuild\",\"mode\":\"off\"}", "{\"t\":\"bbuild\",\"mode\":\"../all\"}", "{\"t\":\"bstate\"}" };
            var notRefused = badB.Where(j => Why(j) == null).ToList();
            Check("protocol: eight bad building messages are all refused (no mode, an unknown mode, a number, capital letters, bbuild with off or a path, a state message coming from the page)", notRefused.Count == 0, string.Join(" | ", notRefused));
            var clamp = Pm("{\"t\":\"stats\",\"fps\":60,\"mem\":100,\"bld\":99999999999}");
            Check("protocol: a silly building count in stats is held to the limit (10 million), a negative one to 0", clamp != null && clamp.Buildings == 10000000 && Pm("{\"t\":\"stats\",\"fps\":60,\"mem\":100,\"bld\":-5}").Buildings == 0);

            // ---------------------------------------------------------------- the card and the progress line
            var est = new BuildingEstimate { Mode = "all", Buildings = 1830, Models = 410, ModelsDone = 0, Minutes = 3.2, Bytes = 17 * 1048576L };
            var estB = new BuildingEstimate { Mode = "bridges", Buildings = 47, Models = 21, Minutes = 0.1, Bytes = 300 * 1024L };
            var estP = new BuildingEstimate { Mode = "all", Buildings = 2252, Models = 521, ModelsDone = 21, Minutes = 1.0, Bytes = 15 * 1048576L };
            Check("card: \"" + WorldMapBuildingsHost.CardText(est, "Kalimdor", 0) + "\"",
                WorldMapBuildingsHost.CardText(est, "Kalimdor", 0) == "Show buildings in 3D. Reads the shapes of about 1,830 buildings in Kalimdor from your own game files once: about 3 minutes in the background, about 17 MB on disk."
                && WorldMapBuildingsHost.CardText(estB, "Kalimdor", 0) == "Show bridges in 3D. Reads the shapes of about 47 bridges in Kalimdor from your own game files once: less than a minute in the background, under 1 MB on disk."
                && WorldMapBuildingsHost.CardText(estP, "Eastern Kingdoms", 21).EndsWith("about a minute in the background, about 15 MB on disk. 21 of 521 shapes are made already."), WorldMapBuildingsHost.CardText(estP, "Eastern Kingdoms", 21));
            var bs = new BuildingsState { Name = "Kalimdor", Phase = "building", Done = 412, Total = 1830, SecondsLeft = 180 };
            var bp = new BuildingsState { Name = "Kalimdor", Phase = "paused", Done = 412, Total = 1830 };
            Check("progress line: \"" + bs.Text + "\"; paused: \"" + bp.Text + "\"; less than 45 seconds left says so",
                bs.Text == "Building Kalimdor: 412 / 1,830 buildings - 3 min left" && bp.Text == "Paused: 412 / 1,830 buildings" && BuildingsState.LeftText(20) == "less than a minute left" && BuildingsState.LeftText(100) == "2 min left" && new BuildingsState { Phase = "ask" }.Text == "" && new BuildingsState { Error = "boom" }.Text == "boom");
            var sj = Json.Obj(WorldMapBuildingsHost.StateJson(0, "all", bs, new[] { 5, 6 }, new[] { 1, 2, 3 }));
            Check("state message: bstate carries the mode, phase, counts, the time left, the text and the shapes finished since the last one (fresh) and, when asked, all that exist (all)",
                sj != null && sj.Str("t") == "bstate" && sj.Str("mode") == "all" && sj.Str("phase") == "building" && sj.Int("done") == 412 && sj.Int("total") == 1830 && sj.Int("left") == 180 && sj.Str("text").StartsWith("Building Kalimdor") && sj.List("fresh").Count == 2 && sj.List("all").Count == 3);
        }

        static byte[] Corrupt(byte[] b, int at) { var c = (byte[])b.Clone(); c[at] = 0xFF; c[at + 1] = 0xFF; c[at + 2] = 0xFF; c[at + 3] = 0x7F; return c; }

        // a flat w x h grid of quads (2 triangles each) in WoW axes, one group
        static WmoGroupMesh BldGrid(int w, int h, float shift = 0)
        {
            var v = new List<float[]>(); var idx = new List<ushort>();
            for (int y = 0; y <= h; y++) for (int x = 0; x <= w; x++) v.Add(new[] { x * 0.5f + shift, y * 0.5f, (float)(Math.Sin(x * 0.3) * Math.Cos(y * 0.2)) });
            var ids = new List<int>();
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) { int a = y * (w + 1) + x, b = a + 1, c = a + w + 1, d = c + 1; ids.AddRange(new[] { a, b, d, a, d, c }); }
            // vertices past 65,535 cannot be indexed by a group file: the mesh is built directly with 32 bit ids
            var m = new WmoGroupMesh { Pos = new float[v.Count * 3], Idx = new ushort[0] };
            for (int i = 0; i < v.Count; i++) { m.Pos[i * 3] = v[i][1]; m.Pos[i * 3 + 1] = v[i][2]; m.Pos[i * 3 + 2] = v[i][0]; }
            return BldMeshFromIds(m, ids);
        }
        static WmoGroupMesh BldMeshFromIds(WmoGroupMesh m, List<int> ids)
        {
            // WmoGroupMesh keeps ushort indices (a group file's own limit); a mesh of more vertices is made of several groups
            int nv = m.Pos.Length / 3;
            if (nv <= 65535) { m.Idx = ids.Select(i => (ushort)i).ToArray(); m.Mat = new ushort[ids.Count / 3]; return m; }
            throw new InvalidOperationException("a group has at most 65,535 vertices");
        }
    }
}
