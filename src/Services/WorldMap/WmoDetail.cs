using System;
using System.Collections.Generic;
using System.IO;

namespace ElansAddonHub.Services.WorldMap
{
    // ============================================================================================================================
    // A building's detailed model for the 3D world map (3.2, the Trees & ground levels Medium and High): near the spot you look at, the simplified one-colour
    // shape (WorldMapBuildings) is swapped for the building's full-detail outside groups with their real textures. Made on demand when the page asks (wneed ->
    // GroundService.MakeBuilding -> wready) and kept as  objects\w\{fdid}.m2b  in the trees' model format (M2Mesh), so the page draws it with the same code:
    // every outside group's triangles in the model's local scene axes, one batch per material (all groups together: a city is a few dozen draw calls).
    // Materials: blend 0 opaque, 1 alpha key -> cut-out, 2+ (blended: glass, glows) -> cut-out at a low threshold; flag 0x4 two-sided, 0x1 unlit.
    // ============================================================================================================================
    public static class WmoDetail
    {
        public const string Folder = "w";
        public static string ModelFile(string objDir, int fdid) => Path.Combine(objDir, Folder, fdid.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".m2b");

        // the model from its root and its groups' files (readGroup: a group's FileDataID -> its bytes); texOk: a material's texture -> whether its file is there
        public static M2Mesh Make(WmoRoot root, Func<int, byte[]> readGroup, Func<int, bool> texOk, out int groups, out int skippedGroups)
        {
            groups = 0; skippedGroups = 0;
            var m = new M2Mesh();
            if (root == null || !root.Ok) { m.Problem = root?.Problem ?? "no building"; return m; }
            var pos = new List<float>(); var nor = new List<sbyte>(); var uv = new List<float>();
            var byMat = new Dictionary<int, List<int>>();
            foreach (var g in root.OutsideGroups())
            {
                var gm = WmoGroupMesh.Parse(readGroup(g.Fdid));
                if (!gm.Ok) { skippedGroups++; continue; }
                groups++;
                int nv = gm.Pos.Length / 3, b0 = pos.Count / 3;
                pos.AddRange(gm.Pos);
                for (int i = 0; i < nv; i++)
                {
                    float nx = 0, ny = 1, nz = 0;
                    if (gm.Nor.Length >= (i + 1) * 3) { nx = gm.Nor[i * 3]; ny = gm.Nor[i * 3 + 1]; nz = gm.Nor[i * 3 + 2]; }
                    double len = Math.Sqrt(nx * nx + ny * ny + nz * nz); if (!(len > 1e-6)) { nx = 0; ny = 1; nz = 0; len = 1; }
                    nor.Add((sbyte)Math.Round(nx / len * 127)); nor.Add((sbyte)Math.Round(ny / len * 127)); nor.Add((sbyte)Math.Round(nz / len * 127)); nor.Add(0);
                    if (gm.Uv.Length >= (i + 1) * 2) { uv.Add(gm.Uv[i * 2]); uv.Add(gm.Uv[i * 2 + 1]); } else { uv.Add(0); uv.Add(0); }
                }
                for (int t = 0; t < gm.Mat.Length; t++)
                {
                    int mat = gm.Mat[t];
                    if (!byMat.TryGetValue(mat, out var l)) byMat[mat] = l = new List<int>();
                    l.Add(b0 + gm.Idx[t * 3]); l.Add(b0 + gm.Idx[t * 3 + 1]); l.Add(b0 + gm.Idx[t * 3 + 2]);
                }
            }
            m.Nv = pos.Count / 3; m.Pos = pos.ToArray(); m.Nor = nor.ToArray(); m.Uv = uv.ToArray();
            var mats = new List<int>(byMat.Keys); mats.Sort();
            foreach (var mat in mats)
            {
                var l = byMat[mat];
                uint flags = mat < root.MaterialFlags.Length ? root.MaterialFlags[mat] : 0, blend = mat < root.MaterialBlend.Length ? root.MaterialBlend[mat] : 0;
                int tex = mat < root.TextureFdid.Length && root.TextureFdid[mat] > 0 && root.TextureFdid[mat] <= int.MaxValue ? (int)root.TextureFdid[mat] : 0;
                if (tex > 0 && !texOk(tex)) tex = 0;
                var b = new M2Batch { I0 = m.Idx.Count, N = l.Count, Tex = tex, Kind = blend == 0 ? "opaque" : "cut", Ref = blend == 1 ? 0.5 : 0.25, TwoSided = (flags & 0x4) != 0 || blend != 0, Unlit = (flags & 0x1) != 0 };
                if (tex == 0 && mat < root.MaterialColor.Length) { uint c = root.MaterialColor[mat]; b.Rgb = (int)(((c >> 16) & 0xFF) << 16 | ((c >> 8) & 0xFF) << 8 | (c & 0xFF)); if (b.Rgb == 0) b.Rgb = 0x8A7A60; }
                m.Idx.AddRange(l);
                if (tex > 0) m.Textures.Add(tex);
                m.Batches.Add(b);
            }
            for (int k = 0; k < 3; k++) { m.Min[k] = float.MaxValue; m.Max[k] = float.MinValue; }
            for (int i = 0; i < m.Nv; i++) for (int k = 0; k < 3; k++) { float p = m.Pos[i * 3 + k]; if (p < m.Min[k]) m.Min[k] = p; if (p > m.Max[k]) m.Max[k] = p; }
            if (m.Nv == 0) { m.Min = new float[3]; m.Max = new float[3]; }
            m.Ok = m.Idx.Count >= 3;
            if (!m.Ok) m.Problem = "no outside triangles";
            return m;
        }

        // ---------------------------------------------------------------- the furnishings: a building's doodads placed in the scene
        // The doodad's own transform (position, quaternion, scale, in the model's WoW axes) is put into the scene's axes ((x, y, z) -> (y, z, x), the cyclic swap
        // WmoTransform uses: q (x, y, z, w) -> (y, z, x, w)), then the building's placement (WmoTransform: scale, Rz Rx Ry, move) is applied; the result is written
        // as the trees' placement: position, Euler angles (degrees, three.js order YXZ: R = Ry Rx Rz) and a uniform scale.
        public static List<M2Placement> Doodads(WmoRoot root, WmoPlacement p, uint uidBase)
        {
            var res = new List<M2Placement>();
            if (root == null || root.Doodads.Count == 0) return res;
            var sets = new List<int> { 0 }; if (p.DoodadSet > 0 && p.DoodadSet < root.DoodadSets.Count) sets.Add(p.DoodadSet);
            var R = Rot(p.RotX, p.RotY, p.RotZ);
            foreach (var si in sets)
            {
                if (si >= root.DoodadSets.Count) continue;
                int first = root.DoodadSets[si][0], count = root.DoodadSets[si][1];
                for (int i = first; i < first + count && i < root.Doodads.Count; i++)
                {
                    if (i < 0) continue;
                    var dd = root.Doodads[i];
                    if (dd.NameIndex >= root.DoodadFdids.Count) continue;
                    int fd = root.DoodadFdids[dd.NameIndex];
                    if (fd <= 0) continue;
                    WmoTransform.ToScene(p, dd.X, dd.Y, dd.Z, out var x, out var y, out var z);
                    // the doodad's rotation in scene axes, then the placement's
                    var D = Quat(dd.Qy, dd.Qz, dd.Qx, dd.Qw);
                    var M = Mul(R, D);
                    Euler(M, out var ex, out var ey, out var ez);
                    var mp = new M2Placement { NameId = (uint)fd, UniqueId = uidBase + (uint)i, X = (float)x, Y = (float)y, Z = (float)z, RotX = (float)ex, RotY = (float)ey, RotZ = (float)ez, Scale = dd.Scale * p.Scale, Flags = 0x40 };
                    if (AdtObjects.Sane(mp)) res.Add(mp);
                }
            }
            return res;
        }

        // R = Ry Rx Rz (degrees), as a row-major 3 x 3
        public static double[] Rot(double rx, double ry, double rz)
        {
            double ax = rx * Math.PI / 180, ay = ry * Math.PI / 180, az = rz * Math.PI / 180;
            return Mul(Mul(Ry(ay), Rx(ax)), Rz(az));
        }
        static double[] Rx(double a) { double c = Math.Cos(a), s = Math.Sin(a); return new[] { 1, 0, 0, 0, c, -s, 0, s, c }; }
        static double[] Ry(double a) { double c = Math.Cos(a), s = Math.Sin(a); return new[] { c, 0, s, 0, 1, 0, -s, 0, c }; }
        static double[] Rz(double a) { double c = Math.Cos(a), s = Math.Sin(a); return new[] { c, -s, 0, s, c, 0, 0, 0, 1 }; }
        public static double[] Mul(double[] a, double[] b)
        {
            var r = new double[9];
            for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) r[i * 3 + j] = a[i * 3] * b[j] + a[i * 3 + 1] * b[3 + j] + a[i * 3 + 2] * b[6 + j];
            return r;
        }
        public static double[] Quat(double x, double y, double z, double w)
        {
            double n = Math.Sqrt(x * x + y * y + z * z + w * w); if (!(n > 1e-9)) return new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
            x /= n; y /= n; z /= n; w /= n;
            return new[]
            {
                1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w),
                2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w),
                2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y),
            };
        }
        // the angles (degrees) of R = Ry(y) Rx(x) Rz(z) - three.js Euler.setFromRotationMatrix, order YXZ
        public static void Euler(double[] m, out double x, out double y, out double z)
        {
            double m13 = m[2], m21 = m[3], m22 = m[4], m23 = m[5], m31 = m[6], m33 = m[8], m11 = m[0];
            x = Math.Asin(-Math.Max(-1, Math.Min(1, m23)));
            if (Math.Abs(m23) < 0.9999999) { y = Math.Atan2(m13, m33); z = Math.Atan2(m21, m22); }
            else { y = Math.Atan2(-m31, m11); z = 0; }
            x *= 180 / Math.PI; y *= 180 / Math.PI; z *= 180 / Math.PI;
        }
    }
}
