using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ElansAddonHub.Services.WorldMap
{
    // ============================================================================================================================
    // An M2 model (a tree, a rock, a fence) for the 3D world map's "Trees & objects": the first .skin's triangles with their real textures, one batch per
    // texture / material, leaves as cut-outs (alpha test in the page). No bones, no animation: the model as it stands in its first frame.
    // Written from wowdev.wiki/M2 and /M2/.skin and checked on the real files of the Forever client in Elan's Lab (offsets from the start of the MD20 data):
    //   the file is chunked: MD21 (the MD20 data), SFID (the .skin files' FileDataIDs), TXID (the textures' FileDataIDs)
    //   0x08 name, 0x3C vertices (48 bytes: pos 0, bone weights 12, bone indices 16, normal 20, uv0 32, uv1 40), 0x48 colours (40 bytes: colour track, alpha track at +20),
    //   0x50 textures (16 bytes: type, flags (1 wrap x, 2 wrap y), name), 0x58 texture weights (20-byte M2Track<fixed16>), 0x70 materials (uint16 flags, uint16 blend),
    //   0x80 texture lookup, 0x90 transparency lookup, 0xA0 bounding box (min, max). M2Track: interpolation, global sequence, timestamps, values (M2Array<M2Array> at +12).
    //   Material flags: 0x1 unlit, 0x4 two-sided. Blending: 0 opaque, 1 alpha key, 2 alpha, 3 no-alpha add, 4 add, 5 mod, 6 mod2x, 7 blend add.
    //   .skin ("SKIN"): 4 vertex lookup (uint16), 12 indices (uint16), 28 sub-meshes (48 bytes: level 2, index start 8, index count 10), 36 batches (24 bytes:
    //   priority 1, sub-mesh 4, colour 8, material 10, layer 12, texture combo 16, transparency combo 20).
    // The page's kinds: opaque (blend 0), cut (1, and 2 approximated as a cut-out at a lower threshold), add (3, 4, 7: additive, no depth write);
    // 5 / 6 (modulate) and parts whose fixed transparency is 0 (hidden unless animated) are left out. A replaceable texture (type != 0: creature skins,
    // banners) gets a stand-in colour.
    //
    // File  m\{fdid}.m2b  = "M2B1" | int jsonLength | json (padded to 4) | float pos[3 nv] | sbyte normal[4 nv] | float uv[2 nv] | index[ni] (uint16 when nv <= 65535,
    // padded to 4; else uint32). Positions are in the model's local scene axes (WoW (x, y, z) -> (y, z, x), the cyclic swap WmoTransform uses, so the winding stays).
    // json: { nv, ni, idx16, min[3], max[3], tris, batches: [{ i0, n, tex, wrap, kind, two, unlit, ref }], textures: [fdid] }. A model with nothing to draw is the 4-byte stub "M2BX".
    // ============================================================================================================================
    public sealed class M2Batch
    {
        public int I0, N, Tex, Wrap;          // Tex = texture FileDataID (0 = none: a stand-in colour)
        public string Kind = "opaque";        // opaque | cut | add
        public bool TwoSided, Unlit;
        public double Ref = 0.5;              // the cut-out threshold (0.25 for blend 2)
        public int Rgb = 0x8A7A60;            // the colour of a batch without a texture of its own
    }

    public sealed class M2Mesh
    {
        public static readonly byte[] Stub = { (byte)'M', (byte)'2', (byte)'B', (byte)'X' };
        public bool Ok;
        public string Problem;
        public string Name = "";
        public int Nv;
        public float[] Pos = new float[0], Uv = new float[0];
        public sbyte[] Nor = new sbyte[0];
        public List<int> Idx = new List<int>();
        public List<M2Batch> Batches = new List<M2Batch>();
        public float[] Min = new float[3], Max = new float[3];
        public int SkinFdid;
        public int Skipped;                   // batches left out (modulate, hidden)
        public HashSet<int> Textures = new HashSet<int>();

        static int I(byte[] d, int o) => BitConverter.ToInt32(d, o);
        static int U16(byte[] d, int o) => BitConverter.ToUInt16(d, o);
        static float F(byte[] d, int o) => BitConverter.ToSingle(d, o);
        static string Tag(byte[] d, int p) => new string(new[] { (char)d[p], (char)d[p + 1], (char)d[p + 2], (char)d[p + 3] });

        // the root file's chunks: where the MD20 data is, and the skin and texture ids. Ok = false when it is not a chunked M2 with a skin.
        public sealed class Root
        {
            public bool Ok; public int Md, MdSize; public List<int> Skins = new List<int>(), Textures = new List<int>();
            public static Root Parse(byte[] d)
            {
                var r = new Root();
                if (d == null || d.Length < 16) return r;
                r.Md = -1;
                for (int p = 0; p + 8 <= d.Length;)
                {
                    var tag = Tag(d, p);
                    int size = I(d, p + 4);
                    if (tag == "MD20") { r.Md = p; r.MdSize = d.Length - p; break; }          // an old unchunked file: it has no SFID, so it is refused below
                    if (size < 0 || (long)p + 8 + size > d.Length) break;
                    if (tag == "MD21") { r.Md = p + 8; r.MdSize = size; }
                    else if (tag == "SFID") for (int i = 0; i + 4 <= size; i += 4) r.Skins.Add(I(d, p + 8 + i));
                    else if (tag == "TXID") for (int i = 0; i + 4 <= size; i += 4) r.Textures.Add(I(d, p + 8 + i));
                    p += 8 + size;
                }
                r.Ok = r.Md >= 0 && r.MdSize >= 0x100 && r.Skins.Count > 0 && r.Skins[0] > 0;
                return r;
            }
        }

        // the first value of an M2Track<fixed16> (the fixed value of the first sequence that has one); null when it has none (or it lives in an .anim file)
        static int? TrackFirst(byte[] d, int md, int mdSize, int track)
        {
            if (track < 0 || track + 20 > mdSize) return null;
            int n = I(d, md + track + 12), o = I(d, md + track + 16);
            for (int s = 0; s < n && s < 64; s++)
            {
                int q = o + s * 8; if (q < 0 || q + 8 > mdSize) return null;
                int cn = I(d, md + q), co = I(d, md + q + 4);
                if (cn > 0 && co > 0 && co + 2 <= mdSize) return BitConverter.ToInt16(d, md + co);
            }
            return null;
        }

        public static string KindOf(int blend, double alpha)
        {
            if (alpha < 0.05) return "hidden";
            if (blend == 5 || blend == 6) return "mod";
            if (blend == 3 || blend == 4 || blend == 7) return "add";
            if (blend == 1 || blend == 2) return "cut";
            return "opaque";
        }

        // root = the .m2 file, skin = its first .skin file (Root.Skins[0])
        public static M2Mesh Parse(byte[] root, byte[] skin)
        {
            var m = new M2Mesh();
            try
            {
                var r = Root.Parse(root);
                if (!r.Ok) { m.Problem = "not a model with a skin"; return m; }
                byte[] d = root; int md = r.Md, mdSize = r.MdSize;
                m.SkinFdid = r.Skins[0];
                int nameN = I(d, md + 8), nameO = I(d, md + 12);
                if (nameN > 0 && nameN < 256 && nameO > 0 && nameO + nameN <= mdSize) m.Name = Encoding.ASCII.GetString(d, md + nameO, nameN).TrimEnd('\0');
                int nv = I(d, md + 0x3C), vo = I(d, md + 0x40);
                int ncol = I(d, md + 0x48), colO = I(d, md + 0x4C);
                int ntex = I(d, md + 0x50), texO = I(d, md + 0x54);
                int nweight = I(d, md + 0x58), weightO = I(d, md + 0x5C);
                int nmat = I(d, md + 0x70), matO = I(d, md + 0x74);
                int nlut = I(d, md + 0x80), lutO = I(d, md + 0x84);
                int ntlut = I(d, md + 0x90), tlutO = I(d, md + 0x94);
                if (nv <= 0 || nv > 200000 || vo <= 0 || (long)vo + (long)nv * 48 > mdSize) { m.Problem = "no vertices"; return m; }
                var sk = skin;
                if (sk == null || sk.Length < 48 || sk[0] != 'S' || sk[1] != 'K' || sk[2] != 'I' || sk[3] != 'N') { m.Problem = "the skin file is missing"; return m; }
                int sv = I(sk, 4), svo = I(sk, 8), si = I(sk, 12), sio = I(sk, 16);
                int nsub = I(sk, 28), subO = I(sk, 32), nbat = I(sk, 36), batO = I(sk, 40);
                if (sv <= 0 || sv > 200000 || si < 0 || svo < 0 || sio < 0 || (long)svo + sv * 2L > sk.Length || (long)sio + si * 2L > sk.Length) { m.Problem = "the skin file is damaged"; return m; }
                m.Nv = sv; m.Pos = new float[sv * 3]; m.Nor = new sbyte[sv * 4]; m.Uv = new float[sv * 2];
                for (int i = 0; i < sv; i++)
                {
                    int gv = U16(sk, svo + i * 2); if (gv >= nv) gv = 0;
                    int o = md + vo + gv * 48;
                    m.Pos[i * 3] = F(d, o + 4); m.Pos[i * 3 + 1] = F(d, o + 8); m.Pos[i * 3 + 2] = F(d, o);
                    float nx = F(d, o + 24), ny = F(d, o + 28), nz = F(d, o + 20);
                    double len = Math.Sqrt(nx * nx + ny * ny + nz * nz); if (!(len > 1e-6)) { nx = 0; ny = 1; nz = 0; len = 1; }
                    m.Nor[i * 4] = (sbyte)Math.Round(nx / len * 127); m.Nor[i * 4 + 1] = (sbyte)Math.Round(ny / len * 127); m.Nor[i * 4 + 2] = (sbyte)Math.Round(nz / len * 127);
                    float u = F(d, o + 32), v = F(d, o + 36);
                    m.Uv[i * 2] = float.IsFinite(u) ? u : 0; m.Uv[i * 2 + 1] = float.IsFinite(v) ? v : 0;
                    for (int k = 0; k < 3; k++) if (!float.IsFinite(m.Pos[i * 3 + k])) m.Pos[i * 3 + k] = 0;
                }
                for (int b = 0; b < nbat && (long)batO + b * 24L + 24 <= sk.Length; b++)
                {
                    int q = batO + b * 24;
                    int sub = U16(sk, q + 4), colorI = U16(sk, q + 8), mat = U16(sk, q + 10), combo = U16(sk, q + 16), weightCombo = U16(sk, q + 20);
                    if (sub >= nsub || (long)subO + sub * 48L + 48 > sk.Length) continue;
                    int sq = subO + sub * 48, level = U16(sk, sq + 2), istart = U16(sk, sq + 8) | (level << 16), icount = U16(sk, sq + 10);
                    int mflags = 0, blend = 0;
                    if (mat < nmat && matO + mat * 4 + 4 <= mdSize) { mflags = U16(d, md + matO + mat * 4); blend = U16(d, md + matO + mat * 4 + 2); }
                    int texFdid = 0, texType = -1, texFlags = 0;
                    if (combo < nlut && lutO + combo * 2 + 2 <= mdSize)
                    {
                        int ti = U16(d, md + lutO + combo * 2);
                        if (ti < ntex && texO + ti * 16 + 8 <= mdSize) { texType = I(d, md + texO + ti * 16); texFlags = I(d, md + texO + ti * 16 + 4); }
                        if (texType == 0 && ti < r.Textures.Count && r.Textures[ti] > 0) texFdid = r.Textures[ti];
                    }
                    // the fixed transparency: texture weight x colour alpha (fixed16, 32767 = 1)
                    double alpha = 1;
                    if (weightCombo < ntlut && tlutO + weightCombo * 2 + 2 <= mdSize)
                    {
                        int wi = U16(d, md + tlutO + weightCombo * 2);
                        if (wi < nweight) { var v = TrackFirst(d, md, mdSize, weightO + wi * 20); if (v != null) alpha *= Math.Clamp(v.Value / 32767.0, 0, 1); }
                    }
                    if (colorI != 0xFFFF && colorI < ncol) { var v = TrackFirst(d, md, mdSize, colO + colorI * 40 + 20); if (v != null) alpha *= Math.Clamp(v.Value / 32767.0, 0, 1); }
                    var kind = KindOf(blend, alpha);
                    if (kind == "hidden" || kind == "mod") { m.Skipped++; continue; }
                    var bo = new M2Batch { I0 = m.Idx.Count, Tex = texFdid, Wrap = texFlags & 3, Kind = kind, TwoSided = (mflags & 4) != 0 || kind == "cut", Unlit = (mflags & 1) != 0, Ref = blend == 2 ? 0.25 : 0.5 };
                    for (int i = istart; i + 2 < istart + icount && i + 2 < si; i += 3)
                        for (int j = 0; j < 3; j++) { int v = U16(sk, sio + (i + j) * 2); m.Idx.Add(v < sv ? v : 0); }
                    bo.N = m.Idx.Count - bo.I0;
                    if (bo.N == 0) continue;
                    if (texFdid > 0) m.Textures.Add(texFdid);
                    m.Batches.Add(bo);
                }
                for (int k = 0; k < 3; k++) { m.Min[k] = float.MaxValue; m.Max[k] = float.MinValue; }
                foreach (var i in m.Idx) for (int k = 0; k < 3; k++) { float p = m.Pos[i * 3 + k]; if (p < m.Min[k]) m.Min[k] = p; if (p > m.Max[k]) m.Max[k] = p; }
                if (m.Idx.Count == 0) { m.Min = new float[3]; m.Max = new float[3]; }
                m.Ok = true;
            }
            catch (Exception e) { m.Ok = false; m.Problem = e.Message; }
            return m;
        }

        public bool Drawable => Ok && Idx.Count >= 3 && Batches.Count > 0;

        public byte[] ToBytes()
        {
            if (!Drawable) return (byte[])Stub.Clone();
            bool small = Nv <= 65535;
            var meta = new Dictionary<string, object>
            {
                ["nv"] = Nv, ["ni"] = Idx.Count, ["idx16"] = small, ["tris"] = Idx.Count / 3,
                ["min"] = new List<object> { R(Min[0]), R(Min[1]), R(Min[2]) }, ["max"] = new List<object> { R(Max[0]), R(Max[1]), R(Max[2]) },
                ["batches"] = Batches.ConvertAll(b => (object)new Dictionary<string, object>
                {
                    ["i0"] = b.I0, ["n"] = b.N, ["tex"] = b.Tex, ["wrap"] = b.Wrap, ["kind"] = b.Kind, ["two"] = b.TwoSided, ["unlit"] = b.Unlit, ["ref"] = b.Ref, ["rgb"] = b.Rgb,
                }),
                ["textures"] = new List<object>(ToObjects(Textures)),
            };
            var js = Encoding.UTF8.GetBytes(Json.Write(meta));
            var ms = new MemoryStream(); var w = new BinaryWriter(ms);
            w.Write(new[] { (byte)'M', (byte)'2', (byte)'B', (byte)'1' }); w.Write(js.Length); w.Write(js);
            for (int k = js.Length; k % 4 != 0; k++) w.Write((byte)0);
            foreach (var f in Pos) w.Write(f);
            foreach (var n in Nor) w.Write(n);
            foreach (var f in Uv) w.Write(f);
            if (small) { foreach (var i in Idx) w.Write((ushort)i); if (Idx.Count % 2 != 0) w.Write((ushort)0); }
            else foreach (var i in Idx) w.Write(i);
            w.Flush(); return ms.ToArray();
        }

        static object R(float f) => Math.Round((double)f, 3);
        static IEnumerable<object> ToObjects(IEnumerable<int> l) { var s = new List<int>(l); s.Sort(); foreach (var i in s) yield return i; }

        // the texture ids a model file names (for the page and the build's "done" check); null when it is not a model file
        public static List<int> TexturesOf(byte[] b)
        {
            try
            {
                if (b == null || b.Length < 4 || b[0] != 'M' || b[1] != '2' || b[2] != 'B') return null;
                if (b[3] == 'X') return new List<int>();
                if (b.Length < 8) return null;
                int jl = BitConverter.ToInt32(b, 4);
                if (jl <= 0 || 8L + jl > b.Length) return null;
                var d = Json.Obj(Encoding.UTF8.GetString(b, 8, jl));
                var res = new List<int>();
                if (d != null && d.TryGetValue("textures", out var t) && t is List<object> l) foreach (var o in l) if (o is double x) res.Add((int)x);
                return res;
            }
            catch (Exception) { return null; }
        }
    }
}
