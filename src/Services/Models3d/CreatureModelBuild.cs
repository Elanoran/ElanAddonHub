using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace ElansAddonHub.Services.Models3d
{
    // ============================================================================================================================
    // 3.6: Atlas 3D models, step 2 - a creature's model (a wolf, a boar, Ragnaros) built into one file the model viewer reads. Apart from the world map's
    // M2Mesh / "M2B1" on purpose (that one is a still, bone-less tree or rock; this one keeps the skeleton and the idle animation): the same M2 structures
    // read again here, the world map untouched. Written from wowdev.wiki/M2, /M2/.skin and /M2/.anim, the default geosets and the .anim chunks after
    // wow.export (MIT, Kruithne, Marlamin), and checked on the real files of 1.60.1.70707 (modelsreal). Offsets from the start of the MD20 data:
    //   chunks MD21 (the MD20 data), SFID (skins first, then lod skins), TXID (a FileDataID per texture), AFID (uint16 anim id, uint16 sub id, uint32 fdid:
    //   the .anim files), SKID (a .skel file: not read here, noted)
    //   0x04 version (272 / 274 here), 0x14 global loops (uint32), 0x1C sequences (64 bytes: id 0, sub id 2, duration 4, move speed 8, flags 12 (0x20 = the
    //   data is in the .m2, else in an .anim; 0x40 = an alias of aliasNext 62), frequency 16), 0x2C bones (88 bytes: key bone 0, flags 4 (0x8 spherical
    //   billboard, 0x10 / 0x20 / 0x40 cylindrical about x / y / z), parent int16 8, translation track 16, rotation 36 (compressed quaternions: 4 x int16),
    //   scale 56, pivot 76), 0x3C vertices (48 bytes: pos 0, bone weights 12, bone indices 16, normal 20, uv0 32), 0x48 colours (40: alpha track at +20),
    //   0x50 textures (16: type, flags (1 wrap x, 2 wrap y)), 0x58 texture weights, 0x70 materials (uint16 flags: 0x1 unlit, 0x2 unfogged, 0x4 two-sided,
    //   0x8 no depth test, 0x10 no depth write; uint16 blend 0 opaque .. 7), 0x80 texture lookup, 0x90 transparency lookup, 0xA0 bounding box, 0xB8 radius.
    //   M2Track (20 bytes): interpolation 0, global sequence int16 2 (-1 = none), timestamps M2Array<M2Array<uint32>> 4, values M2Array<M2Array<T>> 12 -
    //   one inner array per sequence (index 0 for a global-sequence track); for a sequence in an .anim file the inner arrays' offsets point into that file.
    //   .skin: 4 vertex lookup (uint16 -> the model's vertex), 12 indices, 28 sub-meshes (48 bytes: skinSectionId 0, level 2, index start 8, count 10),
    //   36 batches (24 bytes: flags 0, priority 1, sub-mesh 4, colour 8, material 10, layer 12, texture count 14, texture combo 16, transparency combo 20).
    // Bone indices: a vertex's four bone indices are the model's own bone numbers (checked on the five sample models: every index < the bone count, and the
    // skin's per-section bone lists through the bone lookup table give the same bones), so they are written as they are.
    // Geosets: the default look, after wow.export: skinSectionId 0, any id ending in "01" (the first variant of a group) and the 32xx group; never 17xx
    // (eye glow) or 35xx. Nothing left = every sub-mesh (noted). The game's own CreatureGeosetData is not read yet.
    // Textures: type 0 -> the TXID FileDataID; 11 / 12 / 13 (monster skins 1 - 3) -> the display's TextureVariationFileDataID[0 / 1 / 2]; any other type
    // (character skin, hair, cape ...) -> none, a stand-in, noted.
    // Axes: like M2Mesh, WoW (x, y, z) -> (y, z, x) (a rotation, so the winding stays): positions, normals, pivots, translations, scales and the quaternions'
    // (x, y, z) all get it (w stays). Up is +y, the creature faces +z.
    //
    // File  m\{displayId}.m2b2 = "M2B2" | int jsonLength | json (padded to 4) | data:
    //   float pos[3 nv] | sbyte normal[4 nv] | float uv[2 nv] | byte boneWeight[4 nv] (sum 255) | byte boneIndex[4 nv] | index[ni] (uint16 when idx16,
    //   padded to 4; else uint32) | anim: per track key block uint32 time[n] then float value[n * w] (w 3 translation / scale, 4 rotation x y z w)
    //   json: { v, name, fdid, display, version, nv, ni, idx16, at: { pos, nor, uv, bw, bi, idx, anim, end } (byte offsets from the start of data),
    //     min, max (the shown vertices, bind pose), box: { min, max, r } (the model header's), scale (ModelData's), subs: [{ id, i0, n }] (shown sub-meshes),
    //     batches: [{ sub, tex, texType, texIndex, texCount, wrap, blend, flags, unlit, unfogged, two, noZTest, noZWrite, alpha, prio, layer }],
    //     textures: [fdid] (each in t\{fdid}.mtx, ObjTexture "MTX1"), bones: [{ p (parent, -1 none), pivot[3], flags, bill (0 none, 1 spherical, 2 / 3 / 4
    //     cylindrical about WoW's x / y / z), t, r, s: [offset into data, keys, interpolation, global sequence] or null }],
    //     anim: { index, id, sub, duration, flags, loop, speed, external (the .anim fdid, 0 = in the .m2), keys, problem }, globalLoops: [ms],
    //     (loop is always true: the client repeats Stand; the M2 has no plain loop flag, the sequence's own flags are in flags)
    //     geosets: { raw, shown, ids, hidden }, notes: [text] }
    // ============================================================================================================================
    public sealed class CmSub { public int Id, I0, N; }
    public sealed class CmBatch
    {
        public int Sub, Tex, TexType = -1, TexIndex = -1, TexCount = 1, Wrap, Blend, MatFlags, Prio, Layer;
        public double Alpha = 1;
    }
    public sealed class CmTrack
    {
        public int Interp, Gs = -1, Width;
        public uint[] Times = new uint[0];
        public float[] Values = new float[0];         // Width per key
        public int Keys => Times.Length;
    }
    public sealed class CmBone { public int Parent = -1, Flags, KeyId; public float[] Pivot = new float[3]; public CmTrack T, R, S; }

    public sealed class CreatureModel
    {
        public const int FormatVersion = 1;            // the json's "v"; a new value is a new cache folder (CreatureModels.KeyFor)
        public static readonly byte[] Magic = { (byte)'M', (byte)'2', (byte)'B', (byte)'2' };

        public bool Ok;
        public string Problem;
        public string Name = "";
        public int Version, ModelFdid, SkinFdid, DisplayId;
        public float Scale = 1;
        public int Nv;
        public float[] Pos = new float[0], Uv = new float[0];
        public sbyte[] Nor = new sbyte[0];
        public byte[] Bw = new byte[0], Bi = new byte[0];
        public List<int> Idx = new List<int>();
        public List<CmSub> Subs = new List<CmSub>();
        public List<CmBatch> Batches = new List<CmBatch>();
        public List<CmBone> Bones = new List<CmBone>();
        public List<int> Textures = new List<int>();
        public List<uint> GlobalLoops = new List<uint>();
        public List<string> Notes = new List<string>();
        public float[] Min = new float[3], Max = new float[3], BoxMin = new float[3], BoxMax = new float[3];
        public float Radius;
        // the Stand sequence
        public int AnimIndex = -1, AnimId, AnimSub, AnimFlags, AnimExternal;
        public uint AnimDuration;
        public float AnimSpeed;
        public bool AnimLoop = true;
        public string AnimProblem;
        // what the checks look at
        public int RawSubs, ShownSubs, RawVertices, SkippedBatches, BadBoneIndices, FixedWeights, LookupDisagree, LookupChecked;
        public List<int> ShownIds = new List<int>(), HiddenIds = new List<int>();
        public int AnimKeys => Bones.Sum(b => (b.T?.Keys ?? 0) + (b.R?.Keys ?? 0) + (b.S?.Keys ?? 0));

        static int I(byte[] d, int o) => BitConverter.ToInt32(d, o);
        static int U16(byte[] d, int o) => BitConverter.ToUInt16(d, o);
        static short S16(byte[] d, int o) => BitConverter.ToInt16(d, o);
        static float F(byte[] d, int o) => BitConverter.ToSingle(d, o);
        static string Tag(byte[] d, int p) => new string(new[] { (char)d[p], (char)d[p + 1], (char)d[p + 2], (char)d[p + 3] });
        static bool In(long o, long n, int size) => o >= 0 && n >= 0 && o + n <= size;

        // ---------------------------------------------------------------------------------------------------------------- the root file's chunks
        public sealed class Chunks
        {
            public bool Ok; public int Md = -1, MdSize, Skeleton;
            public List<int> Skins = new List<int>(), Textures = new List<int>();
            public List<(int Id, int Sub, int Fdid)> Anims = new List<(int, int, int)>();
            public static Chunks Parse(byte[] d)
            {
                var r = new Chunks();
                if (d == null || d.Length < 16) return r;
                for (int p = 0; p + 8 <= d.Length;)
                {
                    var tag = Tag(d, p); int size = I(d, p + 4);
                    if (tag == "MD20") break;                 // an old unchunked file: no SFID, refused below
                    if (size < 0 || (long)p + 8 + size > d.Length) break;
                    if (tag == "MD21") { r.Md = p + 8; r.MdSize = size; }
                    else if (tag == "SFID") for (int i = 0; i + 4 <= size; i += 4) r.Skins.Add(I(d, p + 8 + i));
                    else if (tag == "TXID") for (int i = 0; i + 4 <= size; i += 4) r.Textures.Add(I(d, p + 8 + i));
                    else if (tag == "AFID") for (int i = 0; i + 8 <= size; i += 8) r.Anims.Add((U16(d, p + 8 + i), U16(d, p + 10 + i), I(d, p + 12 + i)));
                    else if (tag == "SKID" && size >= 4) r.Skeleton = I(d, p + 8);
                    p += 8 + size;
                }
                r.Ok = r.Md >= 0 && r.MdSize >= 0x100 && Tag(d, r.Md) == "MD20" && r.Skins.Count > 0 && r.Skins[0] > 0;
                return r;
            }
        }

        // ---------------------------------------------------------------------------------------------------------------- the small rules (self-tested)
        // the default look's sub-meshes, after wow.export's M2 renderer (MIT, Kruithne, Marlamin)
        public static bool DefaultGeoset(int id)
        {
            var s = id.ToString(CultureInfo.InvariantCulture);
            if (s.StartsWith("17", StringComparison.Ordinal) || s.StartsWith("35", StringComparison.Ordinal)) return false;
            return id == 0 || s.EndsWith("01", StringComparison.Ordinal) || s.StartsWith("32", StringComparison.Ordinal);
        }

        // a compressed quaternion component (wowdev M2 "M2CompQuat"): int16 -> -1 .. 1 (0x7FFF = 0, -1 = 1, 0 = -1)
        public static float Quat(short s) => s < 0 ? (s + 32768) / 32767f : (s - 32767) / 32767f;

        // the texture a batch's texture slot shows: type 0 = the file's own (TXID), 11 / 12 / 13 = the display's skins 1 - 3; 0 = none (a stand-in)
        public static int TextureFor(int type, int index, IList<int> txid, IList<int> variations)
        {
            if (type == 0) return index >= 0 && txid != null && index < txid.Count && txid[index] > 0 ? txid[index] : 0;
            if (type >= 11 && type <= 13) { int k = type - 11; return variations != null && k < variations.Count && variations[k] > 0 ? variations[k] : 0; }
            return 0;
        }

        // the sequence the viewer plays: Stand (id 0) variation 0, else the first Stand, else the first sequence; an alias leads to the one it names
        public static int StandIndex(IList<(int Id, int Sub, int Flags, int AliasNext)> seqs)
        {
            if (seqs == null || seqs.Count == 0) return -1;
            int k = -1;
            for (int i = 0; i < seqs.Count && k < 0; i++) if (seqs[i].Id == 0 && seqs[i].Sub == 0) k = i;
            for (int i = 0; i < seqs.Count && k < 0; i++) if (seqs[i].Id == 0) k = i;
            if (k < 0) k = 0;
            for (int hop = 0; hop < 8 && (seqs[k].Flags & 0x40) != 0 && seqs[k].AliasNext >= 0 && seqs[k].AliasNext < seqs.Count && seqs[k].AliasNext != k; hop++) k = seqs[k].AliasNext;
            return k;
        }

        // the model's sequences (id, sub id, flags, aliasNext); empty when the table is damaged
        public static List<(int Id, int Sub, int Flags, int AliasNext)> Sequences(byte[] d, Chunks c)
        {
            var l = new List<(int Id, int Sub, int Flags, int AliasNext)>();
            if (c == null || !c.Ok) return l;
            int md = c.Md, nseq = I(d, md + 0x1C), sqo = I(d, md + 0x20);
            if (nseq > 0 && nseq < 10000 && In(sqo, nseq * 64L, c.MdSize))
                for (int i = 0; i < nseq; i++) { int q = md + sqo + i * 64; l.Add((U16(d, q), U16(d, q + 2), I(d, q + 12), U16(d, q + 62))); }
            return l;
        }

        // an .anim file's data: chunked (AFM2, with AFSB = the skeleton's bone data when present) or raw, as wow.export's ANIMLoader reads it (MIT, Kruithne,
        // Marlamin); here the chunks are recognised by their tags rather than by the model's flags
        public static byte[] AnimData(byte[] f, out bool chunked)
        {
            chunked = false;
            if (f == null || f.Length < 8) return f;
            var t0 = Tag(f, 0);
            if (t0 != "AFM2" && t0 != "AFSA" && t0 != "AFSB") return f;
            chunked = true;
            byte[] afm2 = null, afsb = null;
            for (int p = 0; p + 8 <= f.Length;)
            {
                var tag = Tag(f, p); int size = I(f, p + 4);
                if (size < 0 || (long)p + 8 + size > f.Length) break;
                var c = new byte[size]; Buffer.BlockCopy(f, p + 8, c, 0, size);
                if (tag == "AFM2") afm2 = c; else if (tag == "AFSB") afsb = c;
                p += 8 + size;
            }
            return afsb ?? afm2;
        }

        // one track's keys for one sequence. at = the M2Track's offset in the MD20 data; anim = the sequence's .anim data (null = the data is in the .m2).
        // A global-sequence track uses its first inner array, in the .m2. quat = compressed quaternions (8 bytes), else three floats.
        public static CmTrack Track(byte[] d, int md, int mdSize, int at, int seq, bool quat, byte[] anim, List<string> issues = null)
        {
            var t = new CmTrack { Width = quat ? 4 : 3 };
            if (!In(at, 20, mdSize)) return t;
            t.Interp = U16(d, md + at); t.Gs = S16(d, md + at + 2);
            int tsN = I(d, md + at + 4), tsO = I(d, md + at + 8), vN = I(d, md + at + 12), vO = I(d, md + at + 16);
            int idx = t.Gs >= 0 ? 0 : seq;
            if (idx < 0 || idx >= tsN || idx >= vN || !In(tsO + idx * 8L, 8, mdSize) || !In(vO + idx * 8L, 8, mdSize)) return t;
            int tc = I(d, md + tsO + idx * 8), to = I(d, md + tsO + idx * 8 + 4), vc = I(d, md + vO + idx * 8), vo = I(d, md + vO + idx * 8 + 4);
            int n = Math.Min(tc, vc);
            if (n <= 0) return t;
            if (tc != vc) issues?.Add("a track has " + tc + " times and " + vc + " values");
            bool ext = t.Gs < 0 && anim != null;
            byte[] src = ext ? anim : d; int b0 = ext ? 0 : md, lim = ext ? anim.Length : mdSize;
            // a spline track keeps (value, in tangent, out tangent) per key; only the value is taken (and the viewer interpolates linearly)
            int el = (quat ? 8 : 12) * (t.Interp >= 2 ? 3 : 1);
            if (!In(to, n * 4L, lim) || !In(vo, (long)n * el, lim)) { issues?.Add("a track points outside its " + (ext ? ".anim" : ".m2") + " data"); return t; }
            if (t.Interp >= 2) issues?.Add("a spline track (interpolation " + t.Interp + ") is read as linear");
            t.Times = new uint[n]; t.Values = new float[n * t.Width];
            for (int k = 0; k < n; k++)
            {
                t.Times[k] = BitConverter.ToUInt32(src, b0 + to + k * 4);
                int o = b0 + vo + k * el;
                if (quat)
                {
                    float x = Quat(S16(src, o)), y = Quat(S16(src, o + 2)), z = Quat(S16(src, o + 4)), w = Quat(S16(src, o + 6));
                    double len = Math.Sqrt(x * x + y * y + z * z + w * w); if (!(len > 1e-6)) { x = y = z = 0; w = 1; len = 1; }
                    t.Values[k * 4] = (float)(y / len); t.Values[k * 4 + 1] = (float)(z / len); t.Values[k * 4 + 2] = (float)(x / len); t.Values[k * 4 + 3] = (float)(w / len);
                }
                else
                {
                    float x = F(src, o), y = F(src, o + 4), z = F(src, o + 8);
                    t.Values[k * 3] = Fin(y); t.Values[k * 3 + 1] = Fin(z); t.Values[k * 3 + 2] = Fin(x);
                }
            }
            return t;
        }

        static float Fin(float f) => float.IsFinite(f) ? f : 0;

        // the first fixed value of an M2Track<fixed16> (an alpha), as M2Mesh reads it; null = none
        static int? TrackFirst(byte[] d, int md, int mdSize, int track)
        {
            if (!In(track, 20, mdSize)) return null;
            int n = I(d, md + track + 12), o = I(d, md + track + 16);
            for (int s = 0; s < n && s < 64; s++)
            {
                int q = o + s * 8; if (!In(q, 8, mdSize)) return null;
                int cn = I(d, md + q), co = I(d, md + q + 4);
                if (cn > 0 && co > 0 && co + 2 <= mdSize) return S16(d, md + co);
            }
            return null;
        }

        // ---------------------------------------------------------------------------------------------------------------- the build
        // root = the .m2, skin = its first .skin (Chunks.Skins[0]), variations = the display's TextureVariationFileDataID[], readAnim = an .anim file by fdid
        public static CreatureModel Parse(byte[] root, byte[] skin, IList<int> variations, float scale, Func<int, byte[]> readAnim, int displayId = 0, int sequence = -1)
        {
            var m = new CreatureModel { DisplayId = displayId, Scale = scale > 0 && float.IsFinite(scale) ? scale : 1 };
            try
            {
                var c = Chunks.Parse(root);
                if (!c.Ok) { m.Problem = "not a chunked model with a skin"; return m; }
                byte[] d = root; int md = c.Md, ms = c.MdSize;
                m.SkinFdid = c.Skins[0];
                m.Version = I(d, md + 4);
                if (m.Version < 264 || m.Version > 280) m.Notes.Add("model version " + m.Version + " (this reader knows 272 / 274)");
                int nameN = I(d, md + 8), nameO = I(d, md + 12);
                if (nameN > 0 && nameN < 256 && In(nameO, nameN, ms)) m.Name = Encoding.ASCII.GetString(d, md + nameO, nameN).TrimEnd('\0');
                if (c.Skeleton > 0) m.Notes.Add("the model has a skeleton file (" + c.Skeleton + "); it is not read");
                int nv = I(d, md + 0x3C), vo = I(d, md + 0x40);
                if (nv <= 0 || nv > 500000 || !In(vo, nv * 48L, ms)) { m.Problem = "no vertices"; return m; }
                var sk = skin;
                if (sk == null || sk.Length < 48 || Tag(sk, 0) != "SKIN") { m.Problem = "the skin file is missing"; return m; }
                int sv = I(sk, 4), svo = I(sk, 8), si = I(sk, 12), sio = I(sk, 16), sbN = I(sk, 20), sbO = I(sk, 24);
                int nsub = I(sk, 28), subO = I(sk, 32), nbat = I(sk, 36), batO = I(sk, 40);
                if (sv <= 0 || sv > 500000 || si < 0 || !In(svo, sv * 2L, sk.Length) || !In(sio, si * 2L, sk.Length) || nsub < 0 || !In(subO, nsub * 48L, sk.Length) || nbat < 0 || !In(batO, nbat * 24L, sk.Length))
                { m.Problem = "the skin file is damaged"; return m; }
                m.RawVertices = sv; m.RawSubs = nsub;

                // ---- bones and the Stand sequence
                int nb = I(d, md + 0x2C), bo = I(d, md + 0x30);
                if (nb < 0 || nb > 4096 || !In(bo, nb * 88L, ms)) { m.Notes.Add("the bone table is damaged; no bones"); nb = 0; }
                int sqo = I(d, md + 0x20);
                var seqs = Sequences(d, c);
                int ngl = I(d, md + 0x14), glo = I(d, md + 0x18);
                if (ngl > 0 && ngl < 10000 && In(glo, ngl * 4L, ms)) for (int i = 0; i < ngl; i++) m.GlobalLoops.Add(BitConverter.ToUInt32(d, md + glo + i * 4));
                m.AnimIndex = sequence >= 0 && sequence < seqs.Count ? sequence : StandIndex(seqs);      // sequence: another one than Stand (the real-file check of an .anim)
                byte[] anim = null;
                if (m.AnimIndex >= 0)
                {
                    int q = md + sqo + m.AnimIndex * 64;
                    m.AnimId = U16(d, q); m.AnimSub = U16(d, q + 2); m.AnimDuration = BitConverter.ToUInt32(d, q + 4); m.AnimSpeed = Fin(F(d, q + 8)); m.AnimFlags = I(d, q + 12);
                    if (sequence < 0 && seqs[m.AnimIndex].Id != 0) m.Notes.Add("no Stand sequence; sequence " + m.AnimIndex + " (id " + m.AnimId + ") is used");
                    if ((m.AnimFlags & 0x20) == 0 && nb > 0)
                    {
                        var af = c.Anims.FirstOrDefault(a => a.Id == m.AnimId && a.Sub == m.AnimSub);
                        m.AnimExternal = af.Fdid;
                        if (af.Fdid <= 0) m.AnimProblem = "the sequence's data is in an .anim file the model does not name";
                        else
                        {
                            byte[] f = null;
                            try { f = readAnim?.Invoke(af.Fdid); } catch (Exception e) { m.AnimProblem = "the .anim file " + af.Fdid + " could not be read: " + e.Message; }
                            anim = AnimData(f, out var chunked);
                            if (anim == null) m.AnimProblem = m.AnimProblem ?? "the .anim file " + af.Fdid + " is not in the install";
                            else m.Notes.Add("the Stand data is in the .anim file " + af.Fdid + (chunked ? " (chunked)" : " (raw)"));
                        }
                    }
                }
                else if (nb > 0) m.AnimProblem = "the model has no sequences";
                var trackIssues = new List<string>();
                for (int i = 0; i < nb; i++)
                {
                    int b = bo + i * 88, q = md + b;
                    var bone = new CmBone { KeyId = I(d, q), Flags = I(d, q + 4), Parent = S16(d, q + 8) };
                    if (bone.Parent >= nb || bone.Parent == i) { trackIssues.Add("a bone names a parent that is not there"); bone.Parent = -1; }
                    bone.Pivot = new[] { Fin(F(d, q + 80)), Fin(F(d, q + 84)), Fin(F(d, q + 76)) };
                    bool skipAnim = m.AnimIndex < 0 || ((m.AnimFlags & 0x20) == 0 && anim == null);
                    // a track on a global sequence still plays (its keys are in the .m2); the sequence's own keys only when its data could be read
                    bone.T = Track(d, md, ms, b + 16, skipAnim ? -1 : m.AnimIndex, false, anim, trackIssues);
                    bone.R = Track(d, md, ms, b + 36, skipAnim ? -1 : m.AnimIndex, true, anim, trackIssues);
                    bone.S = Track(d, md, ms, b + 56, skipAnim ? -1 : m.AnimIndex, false, anim, trackIssues);
                    m.Bones.Add(bone);
                }
                foreach (var g in trackIssues.GroupBy(x => x)) m.Notes.Add(g.Key + (g.Count() > 1 ? " (" + g.Count() + " times)" : ""));

                // ---- the default geosets
                var shown = new bool[nsub];
                for (int s = 0; s < nsub; s++)
                {
                    int id = U16(sk, subO + s * 48);
                    shown[s] = DefaultGeoset(id);
                    if (shown[s]) { m.ShownSubs++; if (!m.ShownIds.Contains(id)) m.ShownIds.Add(id); } else if (!m.HiddenIds.Contains(id)) m.HiddenIds.Add(id);
                }
                if (m.ShownSubs == 0 && nsub > 0) { for (int s = 0; s < nsub; s++) shown[s] = true; m.ShownSubs = nsub; m.Notes.Add("no sub-mesh is a default geoset; all are shown"); }

                // ---- vertices of the shown sub-meshes (re-numbered), their triangles
                var map = new int[sv]; for (int i = 0; i < sv; i++) map[i] = -1;
                var keep = new List<int>();
                var subOut = new int[nsub]; for (int s = 0; s < nsub; s++) subOut[s] = -1;
                var tri = new List<int>();
                for (int s = 0; s < nsub; s++)
                {
                    if (!shown[s]) continue;
                    int q = subO + s * 48, level = U16(sk, q + 2), istart = U16(sk, q + 8) | (level << 16), icount = U16(sk, q + 10);
                    var cs = new CmSub { Id = U16(sk, q), I0 = tri.Count };
                    for (int i = istart; i + 2 < istart + icount && i + 2 < si; i += 3)
                        for (int j = 0; j < 3; j++)
                        {
                            int v = U16(sk, sio + (i + j) * 2); if (v >= sv) v = 0;
                            if (map[v] < 0) { map[v] = keep.Count; keep.Add(v); }
                            tri.Add(map[v]);
                        }
                    cs.N = tri.Count - cs.I0;
                    if (cs.N == 0) continue;
                    subOut[s] = m.Subs.Count; m.Subs.Add(cs);
                }
                m.Idx = tri;
                int n = keep.Count; m.Nv = n;
                m.Pos = new float[n * 3]; m.Nor = new sbyte[n * 4]; m.Uv = new float[n * 2]; m.Bw = new byte[n * 4]; m.Bi = new byte[n * 4];
                for (int i = 0; i < n; i++)
                {
                    int gv = U16(sk, svo + keep[i] * 2); if (gv >= nv) gv = 0;
                    int o = md + vo + gv * 48;
                    m.Pos[i * 3] = Fin(F(d, o + 4)); m.Pos[i * 3 + 1] = Fin(F(d, o + 8)); m.Pos[i * 3 + 2] = Fin(F(d, o));
                    float nx = F(d, o + 24), ny = F(d, o + 28), nz = F(d, o + 20);
                    double len = Math.Sqrt(nx * nx + ny * ny + nz * nz); if (!(len > 1e-6) || double.IsNaN(len)) { nx = 0; ny = 1; nz = 0; len = 1; }
                    m.Nor[i * 4] = (sbyte)Math.Round(nx / len * 127); m.Nor[i * 4 + 1] = (sbyte)Math.Round(ny / len * 127); m.Nor[i * 4 + 2] = (sbyte)Math.Round(nz / len * 127);
                    m.Uv[i * 2] = Fin(F(d, o + 32)); m.Uv[i * 2 + 1] = Fin(F(d, o + 36));
                    // weights and bones as they are; an index past the bone table, or weights that do not add up to 255, are put right (and counted)
                    int sum = 0;
                    for (int k = 0; k < 4; k++)
                    {
                        int w = d[o + 12 + k], bi = d[o + 16 + k];
                        if (bi >= Math.Max(1, nb)) { if (w > 0) m.BadBoneIndices++; bi = 0; w = 0; }
                        m.Bw[i * 4 + k] = (byte)w; m.Bi[i * 4 + k] = (byte)bi; sum += w;
                    }
                    if (sum != 255)
                    {
                        m.FixedWeights++;
                        if (sum == 0) { m.Bw[i * 4] = 255; for (int k = 1; k < 4; k++) m.Bw[i * 4 + k] = 0; }
                        else
                        {
                            int acc = 0, big = 0;
                            for (int k = 0; k < 4; k++) { int w = (int)Math.Round(m.Bw[i * 4 + k] * 255.0 / sum); m.Bw[i * 4 + k] = (byte)w; acc += w; if (m.Bw[i * 4 + k] > m.Bw[i * 4 + big]) big = k; }
                            m.Bw[i * 4 + big] = (byte)(m.Bw[i * 4 + big] + 255 - acc);
                        }
                    }
                }
                // the skin's own bone lists (per section, through the bone lookup table) against the vertices' indices: they must name the same bones
                int nbl = I(d, md + 0x78), blo = I(d, md + 0x7C);
                if (nb > 0 && sbN == sv && In(sbO, sv * 4L, sk.Length) && nbl > 0 && In(blo, nbl * 2L, ms))
                    for (int s = 0; s < nsub; s++)
                    {
                        int q = subO + s * 48, vs = U16(sk, q + 4) | (U16(sk, q + 2) << 16), vc = U16(sk, q + 6), combo = U16(sk, q + 14);
                        for (int v = vs; v < vs + vc && v < sv; v++)
                        {
                            int gv = U16(sk, svo + v * 2); if (gv >= nv) continue;
                            for (int k = 0; k < 4; k++)
                            {
                                if (d[md + vo + gv * 48 + 12 + k] == 0) continue;
                                int li = combo + sk[sbO + v * 4 + k]; if (li >= nbl) { m.LookupDisagree++; continue; }
                                m.LookupChecked++;
                                if (U16(d, md + blo + li * 2) != d[md + vo + gv * 48 + 16 + k]) m.LookupDisagree++;
                            }
                        }
                    }

                // ---- batches: material, texture, alpha
                int ncol = I(d, md + 0x48), colO = I(d, md + 0x4C), ntex = I(d, md + 0x50), texO = I(d, md + 0x54);
                int nweight = I(d, md + 0x58), weightO = I(d, md + 0x5C), nmat = I(d, md + 0x70), matO = I(d, md + 0x74);
                int nlut = I(d, md + 0x80), lutO = I(d, md + 0x84), ntlut = I(d, md + 0x90), tlutO = I(d, md + 0x94);
                var unknownTypes = new SortedSet<int>(); int noSkin = 0;
                for (int b = 0; b < nbat; b++)
                {
                    int q = batO + b * 24;
                    int sub = U16(sk, q + 4);
                    if (sub >= nsub || subOut[sub] < 0) { m.SkippedBatches++; continue; }
                    var cb = new CmBatch { Sub = subOut[sub], Prio = (sbyte)sk[q + 1], Layer = U16(sk, q + 12), TexCount = U16(sk, q + 14) };
                    int colorI = U16(sk, q + 8), mat = U16(sk, q + 10), combo = U16(sk, q + 16), weightCombo = U16(sk, q + 20);
                    if (mat < nmat && In(matO + mat * 4L, 4, ms)) { cb.MatFlags = U16(d, md + matO + mat * 4); cb.Blend = U16(d, md + matO + mat * 4 + 2); }
                    if (combo < nlut && In(lutO + combo * 2L, 2, ms))
                    {
                        int ti = U16(d, md + lutO + combo * 2);
                        cb.TexIndex = ti;
                        if (ti < ntex && In(texO + ti * 16L, 8, ms)) { cb.TexType = I(d, md + texO + ti * 16); cb.Wrap = I(d, md + texO + ti * 16 + 4) & 3; }
                        cb.Tex = TextureFor(cb.TexType, ti, c.Textures, variations);
                        if (cb.TexType != 0 && (cb.TexType < 11 || cb.TexType > 13)) unknownTypes.Add(cb.TexType);
                        else if (cb.Tex == 0) noSkin++;
                    }
                    if (weightCombo < ntlut && In(tlutO + weightCombo * 2L, 2, ms))
                    {
                        int wi = U16(d, md + tlutO + weightCombo * 2);
                        if (wi < nweight) { var v = TrackFirst(d, md, ms, weightO + wi * 20); if (v != null) cb.Alpha *= Math.Clamp(v.Value / 32767.0, 0, 1); }
                    }
                    if (colorI != 0xFFFF && colorI < ncol) { var v = TrackFirst(d, md, ms, colO + colorI * 40 + 20); if (v != null) cb.Alpha *= Math.Clamp(v.Value / 32767.0, 0, 1); }
                    if (cb.Tex > 0 && !m.Textures.Contains(cb.Tex)) m.Textures.Add(cb.Tex);
                    m.Batches.Add(cb);
                }
                if (unknownTypes.Count > 0) m.Notes.Add("texture type " + string.Join(", ", unknownTypes) + " is not a creature skin; a stand-in is shown");
                if (noSkin > 0) m.Notes.Add(noSkin + (noSkin == 1 ? " batch has" : " batches have") + " no texture (the display or the model gives none)");

                // ---- bounds
                for (int k = 0; k < 3; k++) { m.Min[k] = float.MaxValue; m.Max[k] = float.MinValue; }
                for (int i = 0; i < n; i++) for (int k = 0; k < 3; k++) { float p = m.Pos[i * 3 + k]; if (p < m.Min[k]) m.Min[k] = p; if (p > m.Max[k]) m.Max[k] = p; }
                if (n == 0) { m.Min = new float[3]; m.Max = new float[3]; }
                if (In(0xA0, 28, ms))
                {
                    int q = md + 0xA0;
                    m.BoxMin = new[] { Fin(F(d, q + 4)), Fin(F(d, q + 8)), Fin(F(d, q)) }; m.BoxMax = new[] { Fin(F(d, q + 16)), Fin(F(d, q + 20)), Fin(F(d, q + 12)) }; m.Radius = Fin(F(d, q + 24));
                }
                if (m.AnimProblem != null) m.Notes.Add(m.AnimProblem + "; the model stands still");
                m.Ok = true;
            }
            catch (Exception e) { m.Ok = false; m.Problem = e.Message; }
            return m;
        }

        public bool Drawable => Ok && Idx.Count >= 3 && Batches.Count > 0;

        // ---------------------------------------------------------------------------------------------------------------- the file
        static object R(float f) => Math.Round((double)f, 4);
        static List<object> V(float[] a) => a.Select(R).ToList();

        public byte[] ToBytes()
        {
            if (!Drawable) throw new InvalidOperationException("nothing to draw: " + (Problem ?? "no triangles"));
            bool small = Nv <= 65535;
            var data = new MemoryStream(); var w = new BinaryWriter(data);
            var at = new Dictionary<string, object>();
            at["pos"] = (int)data.Position; foreach (var f in Pos) w.Write(f);
            at["nor"] = (int)data.Position; foreach (var x in Nor) w.Write(x);
            at["uv"] = (int)data.Position; foreach (var f in Uv) w.Write(f);
            at["bw"] = (int)data.Position; w.Write(Bw);
            at["bi"] = (int)data.Position; w.Write(Bi);
            at["idx"] = (int)data.Position;
            if (small) { foreach (var i in Idx) w.Write((ushort)i); if (Idx.Count % 2 != 0) w.Write((ushort)0); } else foreach (var i in Idx) w.Write(i);
            at["anim"] = (int)data.Position;
            object Tr(CmTrack t)
            {
                if (t == null || t.Keys == 0) return null;
                int o = (int)data.Position;
                foreach (var x in t.Times) w.Write(x);
                foreach (var f in t.Values) w.Write(f);
                return new List<object> { o, t.Keys, t.Interp, t.Gs };
            }
            var bones = Bones.Select(b => (object)new Dictionary<string, object>
            {
                ["p"] = b.Parent, ["pivot"] = V(b.Pivot), ["flags"] = b.Flags,
                ["bill"] = (b.Flags & 0x8) != 0 ? 1 : (b.Flags & 0x10) != 0 ? 2 : (b.Flags & 0x20) != 0 ? 3 : (b.Flags & 0x40) != 0 ? 4 : 0,
                ["t"] = Tr(b.T), ["r"] = Tr(b.R), ["s"] = Tr(b.S),
            }).ToList();
            at["end"] = (int)data.Position;
            w.Flush();
            var meta = new Dictionary<string, object>
            {
                ["v"] = FormatVersion, ["name"] = Name, ["fdid"] = ModelFdid, ["skin"] = SkinFdid, ["display"] = DisplayId, ["version"] = Version,
                ["nv"] = Nv, ["ni"] = Idx.Count, ["idx16"] = small, ["at"] = at,
                ["min"] = V(Min), ["max"] = V(Max), ["box"] = new Dictionary<string, object> { ["min"] = V(BoxMin), ["max"] = V(BoxMax), ["r"] = R(Radius) }, ["scale"] = R(Scale),
                ["subs"] = Subs.Select(s => (object)new Dictionary<string, object> { ["id"] = s.Id, ["i0"] = s.I0, ["n"] = s.N }).ToList(),
                ["batches"] = Batches.Select(b => (object)new Dictionary<string, object>
                {
                    ["sub"] = b.Sub, ["tex"] = b.Tex, ["texType"] = b.TexType, ["texIndex"] = b.TexIndex, ["texCount"] = b.TexCount, ["wrap"] = b.Wrap, ["blend"] = b.Blend, ["flags"] = b.MatFlags,
                    ["unlit"] = (b.MatFlags & 1) != 0, ["unfogged"] = (b.MatFlags & 2) != 0, ["two"] = (b.MatFlags & 4) != 0, ["noZTest"] = (b.MatFlags & 8) != 0, ["noZWrite"] = (b.MatFlags & 0x10) != 0,
                    ["alpha"] = Math.Round(b.Alpha, 4), ["prio"] = b.Prio, ["layer"] = b.Layer,
                }).ToList(),
                ["textures"] = Textures.Cast<object>().ToList(),
                ["bones"] = bones,
                ["anim"] = new Dictionary<string, object>
                {
                    ["index"] = AnimIndex, ["id"] = AnimId, ["sub"] = AnimSub, ["duration"] = (long)AnimDuration, ["flags"] = AnimFlags, ["loop"] = AnimLoop, ["speed"] = R(AnimSpeed),
                    ["external"] = AnimExternal, ["keys"] = AnimKeys, ["problem"] = AnimProblem,
                },
                ["globalLoops"] = GlobalLoops.Select(x => (object)(long)x).ToList(),
                ["geosets"] = new Dictionary<string, object> { ["raw"] = RawSubs, ["shown"] = ShownSubs, ["ids"] = ShownIds.Cast<object>().ToList(), ["hidden"] = HiddenIds.Cast<object>().ToList() },
                ["notes"] = Notes.Cast<object>().ToList(),
            };
            var js = Encoding.UTF8.GetBytes(Json.Write(meta));
            var ms = new MemoryStream(); var o2 = new BinaryWriter(ms);
            o2.Write(Magic); o2.Write(js.Length); o2.Write(js);
            for (int k = js.Length; k % 4 != 0; k++) o2.Write((byte)0);
            o2.Write(data.GetBuffer(), 0, (int)data.Length);
            o2.Flush(); return ms.ToArray();
        }

        // the json of a built file and where its data starts; null when it is not an M2B2 file
        public static Dictionary<string, object> ReadMeta(byte[] b, out int dataStart)
        {
            dataStart = 0;
            try
            {
                if (b == null || b.Length < 8 || b[0] != 'M' || b[1] != '2' || b[2] != 'B' || b[3] != '2') return null;
                int jl = BitConverter.ToInt32(b, 4);
                if (jl <= 0 || 8L + jl > b.Length) return null;
                dataStart = 8 + ((jl + 3) & ~3);
                var d = Json.Obj(Encoding.UTF8.GetString(b, 8, jl));
                if (d == null || d.Int("v") != FormatVersion) return null;
                var at = d.Child("at");
                if (at == null || dataStart + (long)at.Int("end") > b.Length) return null;
                return d;
            }
            catch (Exception) { return null; }
        }
    }
}
