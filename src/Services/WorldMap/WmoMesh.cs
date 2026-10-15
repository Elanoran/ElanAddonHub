using System;
using System.Collections.Generic;
using System.IO;

namespace ElansAddonHub.Services.WorldMap
{
    // ============================================================================================================================
    // The simplified shape of one building as the 3D page draws it:   <map folder>\buildings\m\{WMO FileDataID}.wmb
    //   "WMB1" | byte flags (1 = bridge) | byte lodCount (0 = a stub: this building has nothing to draw, kept so it is not tried again) | ushort 0
    //   float center[3] | float half[3]          the model's box in scene axes (x east, y up, z south), relative to the model's origin
    //   per level of detail (lodCount of them, nearest first):
    //     uint vertexCount | uint indexCount
    //     short  pos[vertexCount x 3]            position = center + q / 32767 x half    (padded to 4 bytes)
    //     byte   rgb[vertexCount x 3]            sRGB colour per vertex                   (padded to 4 bytes)
    //     ushort | uint index[indexCount]        16 bit when vertexCount <= 65535, else 32 bit (padded to 4 bytes)
    // Level 0 is the building welded (vertices with the same place and colour are one) and, when it has more than Lod0MaxTris triangles, clustered down to that;
    // level 1 is a clustering of level 0 to about a sixth of it; level 2 is the model's box (12 triangles) in its average colour. There are no normals and no
    // texture coordinates: the page lights the buildings flat (from the triangles) with the vertex colours, which come from the average colour of each
    // material's own texture (read from the player's game files), so stone looks grey, wood brown and roofs red.
    // ============================================================================================================================
    public sealed class WmoLod
    {
        public int VertexCount => Colors.Length / 3;
        public int TriCount => Indices.Length / 3;
        public short[] Pos = new short[0];
        public byte[] Colors = new byte[0];
        public int[] Indices = new int[0];
    }

    public sealed class WmoBuildOptions
    {
        public int Lod0MaxTris = 24000, Lod1MaxTris = 3000, Lod1Divisor = 6, MinLod1Tris = 24;
        public float WeldStep = 0.02f;
        public bool Bridge;             // the building is a bridge: its stand-in colours are wood rather than stone
    }

    public sealed class WmoModel
    {
        public const int Magic = 0x31424D57;      // "WMB1"
        public float[] Center = new float[3], Half = new float[3];
        public bool Bridge;
        public List<WmoLod> Lods = new List<WmoLod>();
        public bool Stub => Lods.Count == 0;

        // ---------------------------------------------------------------- build
        // groups: the outside groups' meshes (scene axes, model origin); materialRgb: 0xRRGGBB per material id
        public static WmoModel Build(IList<WmoGroupMesh> groups, Func<int, int> materialRgb, WmoBuildOptions o = null)
        {
            o = o ?? new WmoBuildOptions();
            var m = new WmoModel();
            var raw = Weld(groups, materialRgb, o.WeldStep, o.Bridge);
            if (raw.Tris == 0) return m;
            var lod0 = raw;
            if (lod0.Tris > o.Lod0MaxTris) lod0 = ClusterTo(raw, o.Lod0MaxTris);
            float mnx = float.MaxValue, mny = float.MaxValue, mnz = float.MaxValue, mxx = float.MinValue, mxy = float.MinValue, mxz = float.MinValue;
            for (int i = 0; i < lod0.P.Length; i += 3)
            {
                if (lod0.P[i] < mnx) mnx = lod0.P[i]; if (lod0.P[i] > mxx) mxx = lod0.P[i];
                if (lod0.P[i + 1] < mny) mny = lod0.P[i + 1]; if (lod0.P[i + 1] > mxy) mxy = lod0.P[i + 1];
                if (lod0.P[i + 2] < mnz) mnz = lod0.P[i + 2]; if (lod0.P[i + 2] > mxz) mxz = lod0.P[i + 2];
            }
            m.Center = new[] { (mnx + mxx) / 2, (mny + mxy) / 2, (mnz + mxz) / 2 };
            m.Half = new[] { Math.Max(0.01f, (mxx - mnx) / 2), Math.Max(0.01f, (mxy - mny) / 2), Math.Max(0.01f, (mxz - mnz) / 2) };
            m.Lods.Add(Pack(lod0, m.Center, m.Half));
            int t1 = Math.Min(o.Lod1MaxTris, Math.Max(o.MinLod1Tris, lod0.Tris / o.Lod1Divisor));
            if (lod0.Tris > t1 * 1.5) m.Lods.Add(Pack(ClusterTo(lod0, t1), m.Center, m.Half));
            m.Lods.Add(Pack(Box(lod0), m.Center, m.Half));
            return m;
        }

        // an unpacked mesh: float positions, per-vertex colour 0xRRGGBB, triangles
        internal sealed class Raw { public float[] P = new float[0]; public int[] C = new int[0]; public int[] I = new int[0]; public int Tris => I.Length / 3; }

        static Raw Weld(IList<WmoGroupMesh> groups, Func<int, int> materialRgb, float step, bool bridge)
        {
            var pos = new List<float>(); var col = new List<int>(); var idx = new List<int>();
            var map = new Dictionary<long, int>();
            var seen = new HashSet<long>();
            float inv = 1f / step;
            foreach (var g in groups)
            {
                if (g == null || !g.Ok) continue;
                int nv = g.Pos.Length / 3;
                var remap = new int[nv];
                for (int t = 0; t < g.Idx.Length / 3; t++)
                {
                    int rgb = materialRgb != null ? materialRgb(g.Mat[t]) : -1;
                    if (rgb < 0) rgb = Palette(g, t, g.Mat[t], bridge);
                    var tri = new int[3];
                    for (int k = 0; k < 3; k++)
                    {
                        int v = g.Idx[t * 3 + k];
                        float x = g.Pos[v * 3], y = g.Pos[v * 3 + 1], z = g.Pos[v * 3 + 2];
                        long key = Hash(x, y, z, rgb, inv);
                        if (!map.TryGetValue(key, out var id)) { id = pos.Count / 3; map[key] = id; pos.Add(x); pos.Add(y); pos.Add(z); col.Add(rgb); }
                        tri[k] = id;
                    }
                    if (tri[0] == tri[1] || tri[1] == tri[2] || tri[0] == tri[2]) continue;
                    if (!seen.Add(TriKey(tri[0], tri[1], tri[2]))) continue;
                    idx.Add(tri[0]); idx.Add(tri[1]); idx.Add(tri[2]);
                }
            }
            return new Raw { P = pos.ToArray(), C = col.ToArray(), I = idx.ToArray() };
        }

        // the stand-in colour of a triangle whose material has no texture to take a colour from (the player's install streams textures in as they are needed, so some are not on disk):
        // by which way the triangle faces - flat tops stone, slanted tops roof, upright faces plaster, undersides dark; a bridge is wood. Each material gets a slightly different shade.
        internal static int Palette(WmoGroupMesh g, int t, int mat, bool bridge)
        {
            float ny;
            int a = g.Idx[t * 3], b = g.Idx[t * 3 + 1], c = g.Idx[t * 3 + 2];
            if (g.Nor.Length >= (Math.Max(a, Math.Max(b, c)) + 1) * 3) ny = (g.Nor[a * 3 + 1] + g.Nor[b * 3 + 1] + g.Nor[c * 3 + 1]) / 3;
            else
            {
                float ux = g.Pos[b * 3] - g.Pos[a * 3], uy = g.Pos[b * 3 + 1] - g.Pos[a * 3 + 1], uz = g.Pos[b * 3 + 2] - g.Pos[a * 3 + 2];
                float vx = g.Pos[c * 3] - g.Pos[a * 3], vy = g.Pos[c * 3 + 1] - g.Pos[a * 3 + 1], vz = g.Pos[c * 3 + 2] - g.Pos[a * 3 + 2];
                float nx = uy * vz - uz * vy, nyy = uz * vx - ux * vz, nz = ux * vy - uy * vx, len = (float)Math.Sqrt(nx * nx + nyy * nyy + nz * nz);
                ny = len < 1e-6f ? 0 : Math.Abs(nyy / len);
            }
            int baseRgb;
            if (bridge) baseRgb = ny > 0.85f ? 0x8c6c46 : ny < -0.3f ? 0x4f3b28 : 0x7a5c3b;
            else if (ny > 0.9f) baseRgb = 0x8d877b;
            else if (ny > 0.3f) baseRgb = 0x93604a;
            else if (ny < -0.3f) baseRgb = 0x5a554d;
            else baseRgb = 0xb3a68d;
            int shade = 90 + (mat * 37 % 21);          // 90 .. 110 percent
            int r = Math.Min(255, ((baseRgb >> 16) & 255) * shade / 100), gg = Math.Min(255, ((baseRgb >> 8) & 255) * shade / 100), bl = Math.Min(255, (baseRgb & 255) * shade / 100);
            return (r << 16) | (gg << 8) | bl;
        }

        static long Hash(float x, float y, float z, int rgb, float inv)
        {
            unchecked
            {
                long qx = (long)Math.Round(x * inv), qy = (long)Math.Round(y * inv), qz = (long)Math.Round(z * inv);
                long h = 1469598103934665603L;
                h = (h ^ qx) * 1099511628211L; h = (h ^ qy) * 1099511628211L; h = (h ^ qz) * 1099511628211L; h = (h ^ rgb) * 1099511628211L;
                return h;
            }
        }

        static long TriKey(int a, int b, int c)
        {
            int t;
            if (a > b) { t = a; a = b; b = t; }
            if (b > c) { t = b; b = c; c = t; }
            if (a > b) { t = a; a = b; b = t; }
            unchecked { return ((long)a * 2097152L + b) * 2097152L + c; }
        }

        // vertex clustering: every vertex in the same grid cell becomes one (average place and colour); the cell grows until the triangles fit
        internal static Raw ClusterTo(Raw src, int maxTris)
        {
            if (src.Tris <= maxTris) return src;
            float mnx = float.MaxValue, mny = float.MaxValue, mnz = float.MaxValue, mxx = float.MinValue, mxy = float.MinValue, mxz = float.MinValue;
            for (int i = 0; i < src.P.Length; i += 3)
            {
                if (src.P[i] < mnx) mnx = src.P[i]; if (src.P[i] > mxx) mxx = src.P[i];
                if (src.P[i + 1] < mny) mny = src.P[i + 1]; if (src.P[i + 1] > mxy) mxy = src.P[i + 1];
                if (src.P[i + 2] < mnz) mnz = src.P[i + 2]; if (src.P[i + 2] > mxz) mxz = src.P[i + 2];
            }
            float diag = (float)Math.Sqrt((mxx - mnx) * (mxx - mnx) + (mxy - mny) * (mxy - mny) + (mxz - mnz) * (mxz - mnz));
            float cell = Math.Max(0.05f, diag / 400f);
            Raw best = null;
            for (int round = 0; round < 40; round++)
            {
                best = Cluster(src, cell, mnx, mny, mnz);
                if (best.Tris <= maxTris) break;
                // aim by the ratio, but always grow
                double ratio = Math.Sqrt((double)best.Tris / Math.Max(1, maxTris));
                cell *= (float)Math.Max(1.25, Math.Min(2.5, ratio));
            }
            return best;
        }

        static Raw Cluster(Raw src, float cell, float ox, float oy, float oz)
        {
            float inv = 1f / cell;
            var map = new Dictionary<long, int>();
            int nv = src.P.Length / 3;
            var remap = new int[nv];
            var sum = new List<double>(); var csum = new List<double>(); var cnt = new List<int>();
            for (int v = 0; v < nv; v++)
            {
                long ix = (long)((src.P[v * 3] - ox) * inv), iy = (long)((src.P[v * 3 + 1] - oy) * inv), iz = (long)((src.P[v * 3 + 2] - oz) * inv);
                long key = (ix * 1048576L + iy) * 1048576L + iz;
                if (!map.TryGetValue(key, out var id))
                {
                    id = cnt.Count; map[key] = id; cnt.Add(0);
                    sum.Add(0); sum.Add(0); sum.Add(0); csum.Add(0); csum.Add(0); csum.Add(0);
                }
                remap[v] = id; cnt[id]++;
                sum[id * 3] += src.P[v * 3]; sum[id * 3 + 1] += src.P[v * 3 + 1]; sum[id * 3 + 2] += src.P[v * 3 + 2];
                int c = src.C[v];
                csum[id * 3] += (c >> 16) & 255; csum[id * 3 + 1] += (c >> 8) & 255; csum[id * 3 + 2] += c & 255;
            }
            var r = new Raw { P = new float[cnt.Count * 3], C = new int[cnt.Count] };
            for (int i = 0; i < cnt.Count; i++)
            {
                r.P[i * 3] = (float)(sum[i * 3] / cnt[i]); r.P[i * 3 + 1] = (float)(sum[i * 3 + 1] / cnt[i]); r.P[i * 3 + 2] = (float)(sum[i * 3 + 2] / cnt[i]);
                r.C[i] = ((int)(csum[i * 3] / cnt[i]) << 16) | ((int)(csum[i * 3 + 1] / cnt[i]) << 8) | (int)(csum[i * 3 + 2] / cnt[i]);
            }
            var idx = new List<int>(src.I.Length / 2); var seen = new HashSet<long>();
            for (int t = 0; t < src.I.Length / 3; t++)
            {
                int a = remap[src.I[t * 3]], b = remap[src.I[t * 3 + 1]], c = remap[src.I[t * 3 + 2]];
                if (a == b || b == c || a == c) continue;
                if (!seen.Add(TriKey(a, b, c))) continue;
                idx.Add(a); idx.Add(b); idx.Add(c);
            }
            r.I = idx.ToArray();
            return r;
        }

        // the box of the mesh in its average colour (the farthest band)
        internal static Raw Box(Raw src)
        {
            float mnx = float.MaxValue, mny = float.MaxValue, mnz = float.MaxValue, mxx = float.MinValue, mxy = float.MinValue, mxz = float.MinValue;
            double r = 0, g = 0, b = 0; int n = 0;
            for (int i = 0; i < src.P.Length; i += 3)
            {
                if (src.P[i] < mnx) mnx = src.P[i]; if (src.P[i] > mxx) mxx = src.P[i];
                if (src.P[i + 1] < mny) mny = src.P[i + 1]; if (src.P[i + 1] > mxy) mxy = src.P[i + 1];
                if (src.P[i + 2] < mnz) mnz = src.P[i + 2]; if (src.P[i + 2] > mxz) mxz = src.P[i + 2];
                int c = src.C[i / 3]; r += (c >> 16) & 255; g += (c >> 8) & 255; b += c & 255; n++;
            }
            int rgb = n == 0 ? 0x909090 : ((int)(r / n) << 16) | ((int)(g / n) << 8) | (int)(b / n);
            var box = new Raw { P = new float[24], C = new int[8], I = new int[36] };
            for (int i = 0; i < 8; i++) { box.P[i * 3] = (i & 1) == 0 ? mnx : mxx; box.P[i * 3 + 1] = (i & 2) == 0 ? mny : mxy; box.P[i * 3 + 2] = (i & 4) == 0 ? mnz : mxz; box.C[i] = rgb; }
            int[] tri = { 0, 1, 3, 0, 3, 2, 4, 6, 7, 4, 7, 5, 0, 4, 5, 0, 5, 1, 2, 3, 7, 2, 7, 6, 0, 2, 6, 0, 6, 4, 1, 5, 7, 1, 7, 3 };
            Array.Copy(tri, box.I, 36);
            return box;
        }

        static WmoLod Pack(Raw r, float[] center, float[] half)
        {
            var l = new WmoLod { Pos = new short[r.P.Length], Colors = new byte[r.C.Length * 3], Indices = (int[])r.I.Clone() };
            for (int i = 0; i < r.P.Length; i++)
            {
                int axis = i % 3;
                double q = (r.P[i] - center[axis]) / half[axis] * 32767.0;
                l.Pos[i] = (short)Math.Max(-32767, Math.Min(32767, Math.Round(q)));
            }
            for (int i = 0; i < r.C.Length; i++) { l.Colors[i * 3] = (byte)(r.C[i] >> 16); l.Colors[i * 3 + 1] = (byte)(r.C[i] >> 8); l.Colors[i * 3 + 2] = (byte)r.C[i]; }
            return l;
        }

        // ---------------------------------------------------------------- file
        public byte[] ToBytes()
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write(Magic); w.Write((byte)(Bridge ? 1 : 0)); w.Write((byte)Lods.Count); w.Write((ushort)0);
            foreach (var f in Center) w.Write(f);
            foreach (var f in Half) w.Write(f);
            foreach (var l in Lods)
            {
                w.Write((uint)l.VertexCount); w.Write((uint)l.Indices.Length);
                foreach (var s in l.Pos) w.Write(s);
                Pad(w, 2); // the shorts are 2 bytes each: a multiple of 4 needs padding when the count is odd
                w.Write(l.Colors); Pad(w, l.Colors.Length);
                if (l.VertexCount <= 65535) { foreach (var i in l.Indices) w.Write((ushort)i); Pad(w, l.Indices.Length * 2); }
                else foreach (var i in l.Indices) w.Write((uint)i);
            }
            w.Flush();
            return ms.ToArray();
        }

        static void Pad(BinaryWriter w, int bytesWritten)
        {
            // callers pass either the byte count of the block just written, or 2 for "position is checked below"
            long p = w.BaseStream.Position;
            while (p % 4 != 0) { w.Write((byte)0); p++; }
        }

        public static WmoModel FromBytes(byte[] b)
        {
            try
            {
                if (b == null || b.Length < 32 || BitConverter.ToInt32(b, 0) != Magic) return null;
                var m = new WmoModel { Bridge = (b[4] & 1) != 0 };
                int n = b[5]; if (n > 8) return null;
                for (int i = 0; i < 3; i++) { m.Center[i] = BitConverter.ToSingle(b, 8 + i * 4); m.Half[i] = BitConverter.ToSingle(b, 20 + i * 4); }
                int p = 32;
                for (int k = 0; k < n; k++)
                {
                    if (p + 8 > b.Length) return null;
                    long nv = BitConverter.ToUInt32(b, p), ni = BitConverter.ToUInt32(b, p + 4); p += 8;
                    if (nv > 4000000 || ni > 12000000 || ni % 3 != 0) return null;
                    var l = new WmoLod { Pos = new short[nv * 3], Colors = new byte[nv * 3], Indices = new int[ni] };
                    if (p + nv * 6 > b.Length) return null;
                    for (long i = 0; i < nv * 3; i++) l.Pos[i] = BitConverter.ToInt16(b, p + (int)i * 2);
                    p += (int)(nv * 6); p = (p + 3) & ~3;
                    if (p + nv * 3 > b.Length) return null;
                    Buffer.BlockCopy(b, p, l.Colors, 0, (int)(nv * 3)); p += (int)(nv * 3); p = (p + 3) & ~3;
                    int ib = nv <= 65535 ? 2 : 4;
                    if (p + ni * ib > b.Length) return null;
                    for (long i = 0; i < ni; i++) { l.Indices[i] = ib == 2 ? BitConverter.ToUInt16(b, p + (int)i * 2) : (int)BitConverter.ToUInt32(b, p + (int)i * 4); if (l.Indices[i] >= nv) return null; }
                    p += (int)(ni * ib); p = (p + 3) & ~3;
                    m.Lods.Add(l);
                }
                return m;
            }
            catch (Exception) { return null; }
        }

        // a vertex of a level back in the model's own scene axes (for the tests)
        public void Vertex(int lod, int v, out float x, out float y, out float z)
        {
            var l = Lods[lod];
            x = Center[0] + l.Pos[v * 3] / 32767f * Half[0]; y = Center[1] + l.Pos[v * 3 + 1] / 32767f * Half[1]; z = Center[2] + l.Pos[v * 3 + 2] / 32767f * Half[2];
        }
    }
}
