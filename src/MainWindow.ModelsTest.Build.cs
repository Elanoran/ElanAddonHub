using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Models3d;

namespace ElansAddonHub
{
    // --selftest "models", 3.6 step 2: a creature model built into "M2B2" (CreatureModel) from a small model written here (MD21 / SFID / TXID / AFID,
    // its .skin, an .anim raw and chunked): the default geoset rule, compressed quaternions, the Stand choice, the texture slots (TXID / the display's
    // skins / a stand-in), the tracks (per sequence, a global-sequence track, the axes), weights put right, the skin's bone lists against the vertices',
    // the file and its offsets, and CreatureModels (the cache folder per build, one build per display, reasons kept, other builds removed).
    public partial class MainWindow
    {
        // the MD20 data (or an .anim file) being written: everything 4-aligned, offsets from its start
        sealed class CmW
        {
            public readonly MemoryStream S = new MemoryStream(); public readonly BinaryWriter W;
            public CmW(int header = 0) { W = new BinaryWriter(S); W.Write(new byte[header]); }
            public int Here { get { while (S.Position % 4 != 0) W.Write((byte)0); return (int)S.Position; } }
            public void Set(int at, int v) { var p = S.Position; S.Position = at; W.Write(v); S.Position = p; }
            public void Set16(int at, int v) { var p = S.Position; S.Position = at; W.Write((short)v); S.Position = p; }
            public void SetF(int at, float v) { var p = S.Position; S.Position = at; W.Write(v); S.Position = p; }
            public void Arr(int at, int n, int o) { Set(at, n); Set(at + 4, o); }
            public byte[] Bytes => S.ToArray();
        }

        static byte[] CmVec(params float[] f) { var b = new byte[f.Length * 4]; for (int i = 0; i < f.Length; i++) BitConverter.GetBytes(f[i]).CopyTo(b, i * 4); return b; }
        static byte[] CmQuat(params short[] s) { var b = new byte[s.Length * 2]; for (int i = 0; i < s.Length; i++) BitConverter.GetBytes(s[i]).CopyTo(b, i * 2); return b; }

        // one M2Track: per sequence (times, values, keys); the data of sequence extSeq goes into the .anim file
        static void CmTrackW(CmW md, int at, int interp, int gs, (uint[] t, byte[] v)[] seqs, int extSeq = -1, CmW anim = null)
        {
            var to = new int[seqs.Length]; var vo = new int[seqs.Length];
            for (int i = 0; i < seqs.Length; i++)
            {
                if (seqs[i].t == null || seqs[i].t.Length == 0) continue;
                var w = i == extSeq && anim != null ? anim : md;
                to[i] = w.Here; foreach (var x in seqs[i].t) w.W.Write(x);
                vo[i] = w.Here; w.W.Write(seqs[i].v);
            }
            int ot = md.Here; for (int i = 0; i < seqs.Length; i++) { md.W.Write(seqs[i].t?.Length ?? 0); md.W.Write(to[i]); }
            int ov = md.Here; for (int i = 0; i < seqs.Length; i++) { md.W.Write(seqs[i].t?.Length ?? 0); md.W.Write(vo[i]); }
            md.Set16(at, interp); md.Set16(at + 2, gs); md.Arr(at + 4, seqs.Length, ot); md.Arr(at + 12, seqs.Length, ov);
        }

        // A small creature: 12 vertices in 4 one-triangle sub-meshes (geosets 0, 1, 101, 1702), 2 bones, 2 sequences (Walk, then Stand), 3 textures (type 0 =
        // TXID 5001, 11 = the display's first skin, 1 = a character skin), 5 batches. external = the Stand data in the .anim file 7001 (raw).
        static (byte[] root, byte[] skin, byte[] anim) CmModel(bool external = false, int version = 274)
        {
            var md = new CmW(0x130);
            md.Set(0, BitConverter.ToInt32(Encoding.ASCII.GetBytes("MD20"), 0)); md.Set(4, version);
            int no = md.Here; md.W.Write(Encoding.ASCII.GetBytes("TestWolf\0")); md.Arr(0x08, 9, no);
            int glo = md.Here; md.W.Write(3000u); md.Arr(0x14, 1, glo);
            // sequences: 0 Walk (id 4), 1 Stand (id 0) in the .m2 (flags 0x20) or in an .anim
            int so = md.Here;
            for (int i = 0; i < 2; i++)
            {
                var s = new byte[64];
                BitConverter.GetBytes((ushort)(i == 0 ? 4 : 0)).CopyTo(s, 0); BitConverter.GetBytes(i == 0 ? 800 : 2000).CopyTo(s, 4); BitConverter.GetBytes(i == 0 ? 3.5f : 0f).CopyTo(s, 8);
                BitConverter.GetBytes(i == 1 && external ? 0 : 0x20).CopyTo(s, 12); BitConverter.GetBytes((short)-1).CopyTo(s, 60);
                md.W.Write(s);
            }
            md.Arr(0x1C, 2, so);
            int bo = md.Here; md.W.Write(new byte[2 * 88]); md.Arr(0x2C, 2, bo);
            md.Set(bo, -1); md.Set(bo + 4, 0); md.Set16(bo + 8, -1);
            md.Set(bo + 88, -1); md.Set(bo + 88 + 4, 0x8); md.Set16(bo + 88 + 8, 0);
            md.SetF(bo + 88 + 76, 1); md.SetF(bo + 88 + 80, 2); md.SetF(bo + 88 + 84, 3);       // bone 1's pivot (1, 2, 3) in WoW's axes
            var anim = new CmW();
            // bone 0 translation: Walk 1 key, Stand 2 keys (1, 2, 3) and (4, 5, 6) at 0 and 1000 ms
            CmTrackW(md, bo + 16, 1, -1, new[] { (new uint[] { 0 }, CmVec(9, 9, 9)), (new uint[] { 0, 1000 }, CmVec(1, 2, 3, 4, 5, 6)) }, external ? 1 : -1, anim);
            CmTrackW(md, bo + 36, 1, -1, new (uint[], byte[])[] { (null, null), (null, null) });
            CmTrackW(md, bo + 56, 1, -1, new (uint[], byte[])[] { (null, null), (null, null) });
            // bone 1 rotation: Stand 2 keys, identity (32767 x3, -1) and x = 0.6, w = 0.8; scale on global sequence 0 (one key, in the .m2)
            CmTrackW(md, bo + 88 + 16, 0, -1, new (uint[], byte[])[] { (null, null), (null, null) });
            CmTrackW(md, bo + 88 + 36, 1, -1, new[] { (new uint[0], new byte[0]), (new uint[] { 0, 500 }, CmQuat(32767, 32767, 32767, -1, -13108, 32767, 32767, -6554)) }, external ? 1 : -1, anim);
            CmTrackW(md, bo + 88 + 56, 1, 0, new[] { (new uint[] { 0 }, CmVec(1, 1, 2)) });
            // vertices: x = i, y = i % 3, z = 0.5 i (WoW), normal +z (up), uv (i / 12, 0.5); weights (bone 0) except 1 (128 / 127 over 0 / 1) and 2 (200 / 54 = 254)
            int vo = md.Here;
            for (int i = 0; i < 12; i++)
            {
                md.W.Write((float)i); md.W.Write((float)(i % 3)); md.W.Write(i * 0.5f);
                var w = i == 1 ? new byte[] { 128, 127, 0, 0 } : i == 2 ? new byte[] { 200, 54, 0, 0 } : new byte[] { 255, 0, 0, 0 };
                var bi = i == 1 ? new byte[] { 0, 1, 0, 0 } : i == 2 ? new byte[] { 1, 0, 0, 0 } : new byte[] { 0, 0, 0, 0 };
                md.W.Write(w); md.W.Write(bi); md.W.Write(0f); md.W.Write(0f); md.W.Write(1f); md.W.Write(i / 12f); md.W.Write(0.5f); md.W.Write(0f); md.W.Write(0f);
            }
            md.Arr(0x3C, 12, vo); md.Set(0x44, 1);
            int to = md.Here; foreach (var (type, flags) in new[] { (0, 3), (11, 1), (1, 0) }) { md.W.Write(type); md.W.Write(flags); md.W.Write(0); md.W.Write(0); }
            md.Arr(0x50, 3, to);
            int mo = md.Here; md.W.Write((ushort)0x14); md.W.Write((ushort)2); md.W.Write((ushort)0x1); md.W.Write((ushort)0); md.Arr(0x70, 2, mo);
            int blo = md.Here; md.W.Write((ushort)0); md.W.Write((ushort)1); md.Arr(0x78, 2, blo);
            int lo = md.Here; md.W.Write((ushort)0); md.W.Write((ushort)1); md.W.Write((ushort)2); md.Arr(0x80, 3, lo);
            md.SetF(0xA0, -1); md.SetF(0xA4, -2); md.SetF(0xA8, -3); md.SetF(0xAC, 11); md.SetF(0xB0, 2); md.SetF(0xB4, 6); md.SetF(0xB8, 7.5f);
            var mdb = md.Bytes;

            // the skin
            var sk = new CmW(48);
            sk.Set(0, BitConverter.ToInt32(Encoding.ASCII.GetBytes("SKIN"), 0));
            int sv = sk.Here; for (int i = 0; i < 12; i++) sk.W.Write((ushort)i); sk.Arr(4, 12, sv);
            int si = sk.Here; for (int i = 0; i < 12; i++) sk.W.Write((ushort)i); sk.Arr(12, 12, si);
            int sb = sk.Here; for (int i = 0; i < 12; i++) sk.W.Write(i == 1 ? new byte[] { 0, 1, 0, 0 } : i == 2 ? new byte[] { 1, 0, 0, 0 } : new byte[4]); sk.Arr(20, 12, sb);
            int ss = sk.Here;
            foreach (var (k, id) in new[] { (0, 0), (1, 1), (2, 101), (3, 1702) })
            {
                var s = new byte[48];
                BitConverter.GetBytes((ushort)id).CopyTo(s, 0); BitConverter.GetBytes((ushort)(k * 3)).CopyTo(s, 4); BitConverter.GetBytes((ushort)3).CopyTo(s, 6);
                BitConverter.GetBytes((ushort)(k * 3)).CopyTo(s, 8); BitConverter.GetBytes((ushort)3).CopyTo(s, 10); BitConverter.GetBytes((ushort)2).CopyTo(s, 12);
                sk.W.Write(s);
            }
            sk.Arr(28, 4, ss);
            int sbt = sk.Here;
            // batches: (sub-mesh, material, texture combo): sub 0 twice (TXID; the character skin), 1, 2 (the display's skin), 3
            foreach (var (sub, mat, combo) in new[] { (0, 0, 0), (0, 1, 2), (1, 0, 0), (2, 1, 1), (3, 0, 0) })
            {
                var b = new byte[24]; b[1] = 0xFF;      // priority -1
                BitConverter.GetBytes((ushort)sub).CopyTo(b, 4); BitConverter.GetBytes((ushort)0xFFFF).CopyTo(b, 8); BitConverter.GetBytes((ushort)mat).CopyTo(b, 10);
                BitConverter.GetBytes((ushort)1).CopyTo(b, 14); BitConverter.GetBytes((ushort)combo).CopyTo(b, 16); BitConverter.GetBytes((ushort)0xFFFF).CopyTo(b, 20);
                sk.W.Write(b);
            }
            sk.Arr(36, 5, sbt);

            // the root file's chunks
            var rs = new MemoryStream(); var rw = new BinaryWriter(rs);
            void Chunk(string tag, byte[] data) { rw.Write(Encoding.ASCII.GetBytes(tag)); rw.Write(data.Length); rw.Write(data); }
            Chunk("MD21", mdb);
            Chunk("SFID", BitConverter.GetBytes(3001));
            Chunk("TXID", BitConverter.GetBytes(5001).Concat(BitConverter.GetBytes(0)).Concat(BitConverter.GetBytes(0)).ToArray());
            var af = new byte[8]; BitConverter.GetBytes((ushort)0).CopyTo(af, 0); BitConverter.GetBytes((ushort)0).CopyTo(af, 2); BitConverter.GetBytes(external ? 7001 : 0).CopyTo(af, 4);
            if (external) Chunk("AFID", af);
            rw.Flush();
            return (rs.ToArray(), sk.Bytes, external ? anim.Bytes : null);
        }

        static byte[] CmChunked(byte[] raw)
        {
            var o = new MemoryStream(); var w = new BinaryWriter(o);
            w.Write(Encoding.ASCII.GetBytes("AFM2")); w.Write(raw.Length); w.Write(raw); w.Flush(); return o.ToArray();
        }

        async Task ModelBuildChecks(string work, Action<string, bool, string> Check)
        {
            bool Near(float a, float b, float e = 1e-3f) => Math.Abs(a - b) <= e;
            // ---- the rules
            Check("build rules: default geosets after wow.export - 0, x01 and 32xx shown; 1, 102, 17xx (eye glow, also 1701) and 35xx not",
                new[] { 0, 101, 201, 1001, 2301, 3200, 3201 }.All(CreatureModel.DefaultGeoset) && !new[] { 1, 2, 102, 1701, 1702, 3501, 3500, 1100 }.Any(CreatureModel.DefaultGeoset), null);
            // 3.7 (format 2): FeetGeosets - a group with no default variant that holds the model's lowest part is shown with its lowest variant
            {
                float N = float.NaN;
                bool[] FG(int[] ids, float[] lo, float[] hi, List<string> nt = null) => CreatureModel.FeetGeosets(ids, lo, hi, nt);
                string Sh(int[] ids, bool[] sh) => string.Join("/", ids.Where((x, i) => sh[i]));
                var nt1 = new List<string>();
                // HUMSCitizenMid (display 21) as in 70707: boots only as 502, the feet 27 % of the height below the rest
                var cit = new[] { 0, 702, 2, 701, 401, 502 };
                var fg1 = FG(cit, new[] { 0.55f, 1.84f, 1.83f, 1.84f, 0.72f, 0f }, new[] { 1.9f, 1.95f, 2.03f, 1.9f, 1.3f, 0.6f }, nt1);
                // the boy (124495, Aiden): shins in 501 (shown), the feet in 503 below them - 503 is added to 501
                var boy = new[] { 0, 401, 501, 503 };
                var nt2 = new List<string>();
                var fg2 = FG(boy, new[] { 0.32f, 0.41f, 0.11f, 0.01f }, new[] { 1.29f, 0.78f, 0.38f, 0.13f }, nt2);
                // a second version of a part (tails 3801 / 3803 from the same root): reaches lower, but overlaps the shown one - left out; so is a head part
                var tails = new[] { 0, 3801, 3803, 201, 203 };
                var fg9 = FG(tails, new[] { 0.5f, -0.14f, -0.42f, 2.7f, 2.63f }, new[] { 2.9f, 0.9f, 0.9f, 2.77f, 2.76f });
                // the group's LOWEST variant is shown, not the sub-mesh that reaches lowest (504 lowest, 503 shown)
                var low3 = new[] { 0, 504, 503 };
                var fg3 = FG(low3, new[] { 0.6f, 0f, 0.02f }, new[] { 2f, 0.5f, 0.5f });
                // a second round: group 5's lowest variant (503) still stops high, so the next lowest group with nothing shown (1302) is added too
                var two = new[] { 0, 504, 503, 1302 };
                var fg4 = FG(two, new[] { 1.0f, 0f, 0.5f, 0.3f }, new[] { 2f, 1.0f, 0.6f, 1.2f });      // 504 is a taller boot over 503, not below it
                // left alone: hair (group 0), eye glow (17xx), 35xx, within 5 %, an empty sub-mesh, nothing default at all
                var fg5 = FG(new[] { 0, 2, 1702, 3502 }, new[] { 0.5f, 0f, 0f, 0f }, new[] { 2f, 2f, 2f, 2f });
                var fg6 = FG(new[] { 0, 502 }, new[] { 0.04f, 0f }, new[] { 1f, 0.5f });
                var fg7 = FG(new[] { 0, 502 }, new[] { 0.5f, N }, new[] { 1f, N });
                var fg8 = FG(new[] { 2, 502 }, new[] { 0.5f, 0f }, new[] { 1f, 0.5f });
                Check("build rules (format 2): feet geosets - the citizen's 502 is added (0/701/401/502, noted 5xx); the boy (Aiden) keeps 501 and gets 503 below it; a group gives its lowest variant (503, not 504); a second group when that one still stops high",
                    Sh(cit, fg1) == "0/701/401/502" && nt1.Count == 1 && nt1[0].Contains("group 5xx") && nt1[0].Contains("502 is shown") && Sh(boy, fg2) == "0/401/501/503" && nt2.Count == 1 && nt2[0].Contains("another variant")
                    && Sh(low3, fg3) == "0/503" && Sh(two, fg4) == "0/503/1302",
                    Sh(cit, fg1) + " " + string.Join(";", nt1) + " | " + Sh(boy, fg2) + " " + string.Join(";", nt2) + " | " + Sh(low3, fg3) + " | " + Sh(two, fg4));
                Check("build rules (format 2): feet geosets leave alone hair variants (0xx), 17xx, 35xx, a second tail that overlaps the shown one, a part that does not reach lower, a gap within 5 % of the height, an empty sub-mesh, and a skin with no default id (all shown later)",
                    Sh(new[] { 0, 2, 1702, 3502 }, fg5) == "0" && Sh(tails, fg9) == "0/3801/201" && Sh(new[] { 0, 502 }, fg6) == "0" && Sh(new[] { 0, 502 }, fg7) == "0" && !fg8.Any(b => b), null);
            }
            Check("build rules: a compressed quaternion component: 0x7FFF = 0, -1 = 1, 0 = -1, -13108 = 0.6, -6554 = 0.8",
                Near(CreatureModel.Quat(32767), 0) && Near(CreatureModel.Quat(-1), 1) && Near(CreatureModel.Quat(0), -1) && Near(CreatureModel.Quat(-13108), 0.6f) && Near(CreatureModel.Quat(-6554), 0.8f),
                string.Join(" ", new short[] { 32767, -1, 0, -13108, -6554 }.Select(s => CreatureModel.Quat(s).ToString("0.####"))));
            var txid = new[] { 5001, 0, 0 }; var vars = new[] { 6001, 6002, 0, 0 };
            Check("build rules: texture slots - type 0 = TXID, 11 / 12 = the display's skins 1 / 2, 13 with no third skin = none, type 1 / 2 / 6 (character, cape, hair) = none",
                CreatureModel.TextureFor(0, 0, txid, vars) == 5001 && CreatureModel.TextureFor(0, 1, txid, vars) == 0 && CreatureModel.TextureFor(11, 1, txid, vars) == 6001 && CreatureModel.TextureFor(12, 0, txid, vars) == 6002
                && CreatureModel.TextureFor(13, 0, txid, vars) == 0 && CreatureModel.TextureFor(1, 0, txid, vars) == 0 && CreatureModel.TextureFor(2, 0, txid, vars) == 0 && CreatureModel.TextureFor(6, 0, txid, vars) == 0
                && CreatureModel.TextureFor(0, 7, txid, vars) == 0, null);
            Check("build rules: the Stand choice - Stand variation 0 over an earlier Stand variation 1, else the first Stand, else the first sequence; an alias leads on",
                CreatureModel.StandIndex(new List<(int, int, int, int)> { (4, 0, 0x20, 0), (0, 1, 0x20, 0), (0, 0, 0x20, 0) }) == 2
                && CreatureModel.StandIndex(new List<(int, int, int, int)> { (4, 0, 0x20, 0), (0, 1, 0x20, 0) }) == 1
                && CreatureModel.StandIndex(new List<(int, int, int, int)> { (4, 0, 0x20, 0), (5, 0, 0x20, 0) }) == 0
                && CreatureModel.StandIndex(new List<(int, int, int, int)> { (0, 0, 0x60, 2), (4, 0, 0x20, 0), (0, 1, 0x20, 0) }) == 2
                && CreatureModel.StandIndex(new List<(int, int, int, int)>()) == -1, null);
            var rawA = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 };
            var chA = new MemoryStream(); var cw = new BinaryWriter(chA);
            cw.Write(Encoding.ASCII.GetBytes("AFM2")); cw.Write(3); cw.Write(new byte[] { 7, 7, 7 }); cw.Write(Encoding.ASCII.GetBytes("AFSB")); cw.Write(2); cw.Write(new byte[] { 8, 8 }); cw.Flush();
            var a1 = CreatureModel.AnimData(rawA, out var c1); var a2 = CreatureModel.AnimData(CmChunked(rawA), out var c2); var a3 = CreatureModel.AnimData(chA.ToArray(), out var c3);
            Check("build rules: an .anim file raw (as it is), chunked AFM2 (the chunk's data), AFM2 + AFSB (the skeleton bone data wins)",
                !c1 && a1.SequenceEqual(rawA) && c2 && a2.SequenceEqual(rawA) && c3 && a3.SequenceEqual(new byte[] { 8, 8 }), null);

            // ---- a model in the .m2
            var (root, skin, _) = CmModel();
            var m = CreatureModel.Parse(root, skin, vars, 0.71f, _ => throw new InvalidOperationException("no .anim is needed"), 604);
            Check("build: the small model parses (version 274, name, skin 3001)", m.Ok && m.Drawable && m.Version == 274 && m.Name == "TestWolf" && m.SkinFdid == 3001, m.Problem + " " + string.Join("; ", m.Notes));
            Check("build: geosets - 4 sub-meshes in the skin, 0 and 101 shown (2), 1 and 1702 left out with their vertices (12 -> 6) and batches (5 -> 3)",
                m.RawSubs == 4 && m.ShownSubs == 2 && m.Subs.Count == 2 && m.Subs[0].Id == 0 && m.Subs[1].Id == 101 && m.Nv == 6 && m.Idx.Count == 6 && m.Batches.Count == 3 && m.SkippedBatches == 2
                && m.HiddenIds.SequenceEqual(new[] { 1, 1702 }), "subs " + m.Subs.Count + " nv " + m.Nv + " batches " + m.Batches.Count);
            var b0 = m.Batches[0]; var b1 = m.Batches[1]; var b2 = m.Batches[2];
            Check("build: textures - TXID 5001 (type 0, wrap 3), the character skin (type 1) a stand-in and noted, the display's first skin 6001 (type 11, wrap 1); 2 textures listed",
                b0.Tex == 5001 && b0.TexType == 0 && b0.Wrap == 3 && b1.Tex == 0 && b1.TexType == 1 && b2.Tex == 6001 && b2.TexType == 11 && b2.Wrap == 1 && m.Textures.SequenceEqual(new[] { 5001, 6001 })
                && m.Notes.Any(n => n.Contains("texture type 1 ")), string.Join(" ", m.Batches.Select(b => b.Tex + "/" + b.TexType)) + " | " + string.Join("; ", m.Notes));
            Check("build: materials - blend 2 with two-sided + no depth write (0x14), blend 0 unlit (0x1); batches point at the shown sub-meshes",
                b0.Blend == 2 && b0.MatFlags == 0x14 && b1.Blend == 0 && b1.MatFlags == 1 && b0.Sub == 0 && b1.Sub == 0 && b2.Sub == 1 && b0.Prio == -1, null);
            Check("build: axes - a vertex (x, y, z) -> (y, z, x), the normal +z (up) -> +y, the header box and radius",
                Near(m.Pos[3], 1) && Near(m.Pos[4], 0.5f) && Near(m.Pos[5], 1) && m.Nor[1] == 127 && m.Nor[0] == 0 && Near(m.BoxMin[0], -2) && Near(m.BoxMin[1], -3) && Near(m.BoxMin[2], -1) && Near(m.BoxMax[2], 11) && Near(m.Radius, 7.5f)
                && Near(m.Max[2], 8) && Near(m.Min[2], 0), string.Join(",", m.Pos.Take(6)) + " box " + string.Join(",", m.BoxMin) + " min " + string.Join(",", m.Min) + " max " + string.Join(",", m.Max));
            var sums = Enumerable.Range(0, m.Nv).Select(i => m.Bw[i * 4] + m.Bw[i * 4 + 1] + m.Bw[i * 4 + 2] + m.Bw[i * 4 + 3]).ToList();
            Check("build: weights - every vertex sums to 255, the one at 254 put right (and counted), the 128 / 127 vertex keeps bones 0 / 1; the skin's bone lists agree",
                sums.All(s => s == 255) && m.FixedWeights == 1 && m.Bw[4] == 128 && m.Bw[5] == 127 && m.Bi[5] == 1 && m.Bi[8] == 1 && m.BadBoneIndices == 0 && m.LookupChecked > 0 && m.LookupDisagree == 0,
                string.Join(",", sums) + " fixed " + m.FixedWeights + " lookup " + m.LookupChecked + "/" + m.LookupDisagree);
            var bn = m.Bones;
            Check("build: bones - parents (-1, 0), bone 1's pivot (1, 2, 3) -> (2, 3, 1), billboard flag 0x8",
                bn.Count == 2 && bn[0].Parent == -1 && bn[1].Parent == 0 && Near(bn[1].Pivot[0], 2) && Near(bn[1].Pivot[1], 3) && Near(bn[1].Pivot[2], 1) && (bn[1].Flags & 8) != 0, null);
            Check("build: Stand is sequence 1 (in the .m2): duration 2000, Walk's key left out, translation (1, 2, 3) / (4, 5, 6) at 0 / 1000 -> (2, 3, 1) / (5, 6, 4)",
                m.AnimIndex == 1 && m.AnimId == 0 && m.AnimDuration == 2000 && m.AnimExternal == 0 && m.AnimProblem == null && bn[0].T.Keys == 2 && bn[0].T.Times[1] == 1000
                && bn[0].T.Values.Select(v => (double)v).SequenceEqual(new double[] { 2, 3, 1, 5, 6, 4 }), m.AnimIndex + " " + string.Join(",", bn[0].T.Values));
            var rq = bn[1].R.Values;
            Check("build: rotation keys - identity, and (x 0.6, w 0.8) -> (0, 0, 0.6, 0.8) after the axes, unit length; the global-sequence scale track keeps its key (1, 1, 2) -> (1, 2, 1)",
                bn[1].R.Keys == 2 && Near(rq[0], 0) && Near(rq[3], 1) && Near(rq[4], 0) && Near(rq[5], 0) && Near(rq[6], 0.6f) && Near(rq[7], 0.8f) && bn[1].S.Keys == 1 && bn[1].S.Gs == 0
                && Near(bn[1].S.Values[1], 2) && m.GlobalLoops.SequenceEqual(new uint[] { 3000 }), string.Join(",", rq));

            // ---- the file
            var bytes = m.ToBytes();
            var meta = CreatureModel.ReadMeta(bytes, out int ds);
            var at = meta?.Child("at");
            bool fileOk = meta != null && meta.Int("nv") == 6 && meta.Int("ni") == 6 && meta.Bool("idx16") && meta.Int("display") == 604 && at != null && at.Int("pos") == 0 && at.Int("nor") == 72
                && at.Int("uv") == 96 && at.Int("bw") == 144 && at.Int("bi") == 168 && at.Int("idx") == 192 && at.Int("anim") == 204 && ds % 4 == 0 && ds + at.Int("end") == bytes.Length;
            Check("file: \"M2B2\" + json + data; the sections at their offsets (pos 0, nor 72, uv 96, bw 144, bi 168, idx 192, anim 204), the end is the file's end",
                Encoding.ASCII.GetString(bytes, 0, 4) == "M2B2" && fileOk, meta == null ? "no meta" : Json.Write(at));
            bool dataOk = false; string why = "";
            try
            {
                float px = BitConverter.ToSingle(bytes, ds + at.Int("pos") + 12);
                int idx2 = BitConverter.ToUInt16(bytes, ds + at.Int("idx") + 4);      // the third index
                var t0 = meta.List("bones")[0] as Dictionary<string, object>;
                var tr = t0?["t"] as List<object>;
                int o = (int)(double)tr[0];
                uint t1 = BitConverter.ToUInt32(bytes, ds + o + 4); float v3 = BitConverter.ToSingle(bytes, ds + o + 8 + 3 * 4);
                dataOk = Near(px, 1) && idx2 == 2 && (int)(double)tr[1] == 2 && t1 == 1000 && Near(v3, 5) && meta.Child("anim").Int("keys") == m.AnimKeys && meta.List("textures").Count == 2
                    && meta.List("batches").Count == 3 && meta.Child("geosets").Int("raw") == 4 && Math.Abs((double)meta["scale"] - 0.71) < 1e-3;
                why = px + " " + idx2 + " " + t1 + " " + v3;
            }
            catch (Exception e) { why = e.Message; }
            Check("file: read back - vertex 1's position, an index, bone 0's translation keys (count, time 1000, the second value 5) from the anim block, the json's counts and scale", dataOk, why);
            var bad = (byte[])bytes.Clone(); bad[3] = (byte)'1';
            Check("file: another magic, a cut-off file and nothing read as no model", CreatureModel.ReadMeta(bad, out _) == null && CreatureModel.ReadMeta(bytes.Take(bytes.Length - 8).ToArray(), out _) == null && CreatureModel.ReadMeta(null, out _) == null, null);

            // ---- the Stand data in an .anim file
            var (rootE, skinE, animE) = CmModel(external: true);
            var asked = new List<int>();
            var mr = CreatureModel.Parse(rootE, skinE, vars, 1, f => { asked.Add(f); return f == 7001 ? animE : null; });
            var mc = CreatureModel.Parse(rootE, skinE, vars, 1, f => f == 7001 ? CmChunked(animE) : null);
            var mm = CreatureModel.Parse(rootE, skinE, vars, 1, f => null);
            bool Same(CreatureModel a) => a.Bones[0].T.Values.Select(v => (double)v).SequenceEqual(new double[] { 2, 3, 1, 5, 6, 4 }) && a.Bones[1].R.Keys == 2 && Near(a.Bones[1].R.Values[6], 0.6f);
            Check("anim file: Stand in .anim 7001 (AFID 0 / 0) read raw and chunked (AFM2) - the same keys as in the .m2; only that file is asked for",
                mr.Ok && mr.AnimExternal == 7001 && Same(mr) && mc.Ok && Same(mc) && asked.SequenceEqual(new[] { 7001 }) && mr.Notes.Any(n => n.Contains("(raw)")) && mc.Notes.Any(n => n.Contains("(chunked)")),
                string.Join(",", mr.Bones[0].T.Values) + " | " + string.Join("; ", mr.Notes));
            Check("anim file: missing - the model still builds, standing still (no sequence keys, the global-sequence track stays), the reason noted",
                mm.Drawable && mm.AnimProblem != null && mm.Bones[0].T.Keys == 0 && mm.Bones[1].R.Keys == 0 && mm.Bones[1].S.Keys == 1 && mm.Notes.Any(n => n.Contains("stands still")), mm.AnimProblem);
            var junk = CreatureModel.Parse(new byte[] { 1, 2, 3 }, skin, vars, 1, null);
            var noSkin = CreatureModel.Parse(root, null, vars, 1, null);
            Check("build: not a model / no skin = a reason, nothing to draw", !junk.Ok && junk.Problem != null && !noSkin.Ok && noSkin.Problem.Contains("skin"), junk.Problem + " | " + noSkin.Problem);

            // ---- CreatureModels: the cache folder, one build per display
            var savedRoot = CreatureModels.RootOverride; var savedSrc = CreatureModels.SourceForTest; var savedBuild = CreatureModels.BuildForTest;
            try
            {
                CreatureModels.ResetForTest();
                CreatureModels.RootOverride = Path.Combine(work, "atlas", "models");
                var blp = ObjBlp();
                var src = new FuncModelSource(f => f == 126487 ? root : f == 3001 ? skin : f == 5001 || f == 6001 ? blp : f == 222 ? new byte[] { 9, 9 } : null);
                CreatureModels.SourceForTest = () => src;
                string build = "wowf|1.60.1.70707|2417084bcafe";
                CreatureModels.BuildForTest = () => build;
                var set = new ModelSet { Displays = new Dictionary<int, CreatureDisplay>(), Models = new Dictionary<int, CreatureModelInfo>() };
                set.Displays[604] = new CreatureDisplay { Id = 604, ModelId = 43, Scale = 0.71f, Textures = new[] { 6001, 0, 0, 0 } };
                set.Displays[605] = new CreatureDisplay { Id = 605, ModelId = 44, Scale = 1, Textures = new int[4] };
                set.Displays[3167] = new CreatureDisplay { Id = 3167, ModelId = 43, ExtendedId = 9, Scale = 1, Textures = new int[4] };
                set.Models[43] = new CreatureModelInfo { Id = 43, FileId = 126487, Scale = 1 };
                set.Models[44] = new CreatureModelInfo { Id = 44, FileId = 222, Scale = 1 };
                ModelData.ResetForTest();
                var notRead = await CreatureModels.EnsureAsync(604);
                Check("cache: before the model data is read = a reason (not kept), nothing built", !notRead.Ok && notRead.Reason.Contains("not read yet") && CreatureModels.BuildCountForTest == 0, notRead.Reason);
                ModelData.SetForTest(set);
                var key = CreatureModels.KeyFor(build);
                var stale = Path.Combine(CreatureModels.Root, "1.60.1.70338-aaaaaaaa_m1"); Directory.CreateDirectory(Path.Combine(stale, "m"));
                // 3.7: a 3.6.0 cache of the same build (format 1, before FeetGeosets) is another folder and goes too
                var staleFormat = Path.Combine(CreatureModels.Root, "1.60.1.70707-2417084b_m1"); Directory.CreateDirectory(Path.Combine(staleFormat, "m"));
                var t1 = CreatureModels.EnsureAsync(604); var t2 = CreatureModels.EnsureAsync(604);
                var r1 = await t1; var r2 = await t2;
                Check("cache: two calls at once share one build; the file is <root>\\1.60.1.70707-2417084b_m2\\m\\604.m2b2 with t\\5001.mtx and t\\6001.mtx (MTX1)",
                    ReferenceEquals(t1, t2) && r1.Ok && r1.Built && CreatureModels.BuildCountForTest == 1 && key == "1.60.1.70707-2417084b_m2" && r1.Path == Path.Combine(CreatureModels.Root, key, "m", "604.m2b2")
                    && File.Exists(r1.Path) && File.Exists(CreatureModels.TexturePath(key, 5001)) && File.Exists(CreatureModels.TexturePath(key, 6001))
                    && Encoding.ASCII.GetString(File.ReadAllBytes(CreatureModels.TexturePath(key, 5001)), 0, 4) == "MTX1", r1.ToString());
                Check("cache: the folder of another game build, and the same build's format-1 folder (3.6.0), are removed the first time a model is made", !Directory.Exists(stale) && !Directory.Exists(staleFormat), null);
                var fm = CreatureModel.ReadMeta(File.ReadAllBytes(r1.Path), out _);
                Check("cache: the file names model 126487, display 604, the display's scale 0.71", fm != null && fm.Int("fdid") == 126487 && fm.Int("display") == 604 && Math.Abs((double)fm["scale"] - 0.71) < 1e-3, null);
                int reads = src.Reads;
                var r3 = await CreatureModels.EnsureAsync(604);
                Check("cache: asked again = the file at once (no build, nothing read)", r3.Ok && !r3.Built && r3.Path == r1.Path && src.Reads == reads && CreatureModels.BuildCountForTest == 1, null);
                File.Delete(CreatureModels.TexturePath(key, 6001)); CreatureModels.ResetForTest();
                var r4 = await CreatureModels.EnsureAsync(604);
                Check("cache: a texture gone = built again (the model file is only good with its textures)", r4.Ok && r4.Built && File.Exists(CreatureModels.TexturePath(key, 6001)), r4.ToString());
                var h = await CreatureModels.EnsureAsync(3167); var bad2 = await CreatureModels.EnsureAsync(605); var none = await CreatureModels.EnsureAsync(999);
                int builds = CreatureModels.BuildCountForTest;
                var bad3 = await CreatureModels.EnsureAsync(605);
                Check("cache: a humanoid, a broken model file and an unknown display each give a reason and no file; the broken one is not tried again",
                    !h.Ok && h.Reason == ModelData.HumanoidReason && !bad2.Ok && bad2.Reason.Contains("cannot be shown") && !none.Ok && none.Reason.Contains("not in the game's CreatureDisplayInfo")
                    && !bad3.Ok && CreatureModels.BuildCountForTest == builds && !File.Exists(CreatureModels.ModelPath(key, 605)), h.Reason + " | " + bad2.Reason + " | " + none.Reason);
                var src2 = new FuncModelSource(f => f == 126487 ? root : f == 3001 ? skin : f == 5001 ? blp : null);
                CreatureModels.SourceForTest = () => src2; build = "wowf|1.60.1.70800|0badc0de99";
                var r5 = await CreatureModels.EnsureAsync(604);
                var fm5 = r5.Ok ? CreatureModel.ReadMeta(File.ReadAllBytes(r5.Path), out _) : null;
                Check("cache: a new game build = a new folder (the old one removed); a texture that cannot be read is a stand-in (the batch's tex 0, noted, not listed)",
                    r5.Ok && r5.Path.Contains("1.60.1.70800-0badc0de_m2") && !Directory.Exists(Path.Combine(CreatureModels.Root, key)) && r5.MissingTextures.SequenceEqual(new[] { 6001 })
                    && fm5 != null && fm5.List("textures").Count == 1 && fm5.List("notes").Any(n => (n as string)?.Contains("could not be read") == true),
                    r5 + " | the old folder: " + (Directory.Exists(Path.Combine(CreatureModels.Root, key)) ? "still there (" + string.Join(", ", Directory.GetFileSystemEntries(Path.Combine(CreatureModels.Root, key), "*", SearchOption.AllDirectories).Select(Path.GetFileName)) + ")" : "gone")
                    + " | folders: " + string.Join(", ", Directory.GetDirectories(CreatureModels.Root).Select(Path.GetFileName)));
                // 3.7: the old folder is moved aside before it is deleted (a scanner holding a file once left part of it behind after a "successful" delete):
                // whatever is left of it is under a _gone- name, never under the old build key, and it goes with the next clean-up
                Directory.CreateDirectory(Path.Combine(CreatureModels.Root, "_gone-leftover", "t"));
                int gone = CreatureModels.RemoveOtherBuilds(CreatureModels.KeyFor(build));
                Check("cache: a folder left aside by an earlier clean-up (_gone-...) is removed by the next one, the build's own folder and the viewer's page stay",
                    gone >= 1 && !Directory.GetDirectories(CreatureModels.Root).Any(d => Path.GetFileName(d).StartsWith("_gone-", StringComparison.Ordinal)) && Directory.Exists(Path.Combine(CreatureModels.Root, CreatureModels.KeyFor(build))),
                    "removed " + gone + " | folders: " + string.Join(", ", Directory.GetDirectories(CreatureModels.Root).Select(Path.GetFileName)));
            }
            finally
            {
                CreatureModels.RootOverride = savedRoot; CreatureModels.SourceForTest = savedSrc; CreatureModels.BuildForTest = savedBuild; CreatureModels.ResetForTest();
                ModelData.ResetForTest();
            }
        }
    }
}
