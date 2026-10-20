using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ElansAddonHub.Services;
using ElansAddonHub.Services.WorldMap;

namespace ElansAddonHub
{
    // --selftest "objects" (3.2): trees & objects on the 3D world map (AdtObjects.ParseDoodads, M2Mesh, ObjTexture, WorldMapObjects*), on synthetic files made here.
    // The real game files run in their own section: "objectsreal" (MainWindow.ObjectsTest.Real.cs).
    public partial class MainWindow
    {
        async Task<string> ObjectsTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            void Check(string what, bool cond, string detail = null) { SelfTestClock.Mark(what); notes.Add((cond ? "ok   " : "FAIL ") + what + (!cond && detail != null ? " [" + detail + "]" : "")); ok &= cond; Util.Log("objects test: " + (cond ? "ok   " : "FAIL ") + what); }
            var work = Path.Combine(dir, "objects");
            try
            {
                if (Directory.Exists(work)) Util.DeleteDir(work);
                Directory.CreateDirectory(work);
                ObjectsFormatChecks(work, Check);
            }
            catch (Exception e) { Check("objects test crashed", false, e.ToString()); }
            await Task.CompletedTask;
            return (ok ? "objects: ok" : "objects: FAILED") + "\r\n" + string.Join("\r\n", notes);
        }

        // ---------------------------------------------------------------- synthetic files
        // an obj0 chunk file with an MDDF of these records (tag stored reversed, as the game does)
        static byte[] ObjMddf(params (uint id, uint uid, float x, float y, float z, float ry, ushort scale, ushort flags)[] recs)
        {
            var ms = new MemoryStream(); var w = new BinaryWriter(ms);
            w.Write(Encoding.ASCII.GetBytes("REVM")); w.Write(4); w.Write(18);
            w.Write(Encoding.ASCII.GetBytes("FDDM")); w.Write(recs.Length * 36);
            foreach (var r in recs) { w.Write(r.id); w.Write(r.uid); w.Write(r.x); w.Write(r.y); w.Write(r.z); w.Write(0f); w.Write(r.ry); w.Write(0f); w.Write(r.scale); w.Write(r.flags); }
            w.Flush(); return ms.ToArray();
        }

        // an M2 (chunked: MD21 + SFID + TXID) with 6 vertices, one texture and three materials (opaque, alpha key two-sided, modulate), and its .skin
        // with three sub-meshes (one triangle, two triangles, one triangle) and a batch on each
        static void ObjModel(int skinFdid, int texFdid, out byte[] m2, out byte[] skin)
        {
            var md = new byte[0x400];
            void I32(int o, int v) => BitConverter.GetBytes(v).CopyTo(md, o);
            void U16(int o, int v) => BitConverter.GetBytes((ushort)v).CopyTo(md, o);
            void F(int o, float v) => BitConverter.GetBytes(v).CopyTo(md, o);
            Encoding.ASCII.GetBytes("MD20").CopyTo(md, 0);
            Encoding.ASCII.GetBytes("Tree\0").CopyTo(md, 0x300); I32(0x08, 5); I32(0x0C, 0x300);
            // vertices at 0x100: WoW (x north, y west, z up): a triangle standing up at the origin, and two more
            int vo = 0x100; I32(0x3C, 6); I32(0x40, vo);
            float[][] V = { new[] { 0f, 0, 0 }, new[] { 0f, 1, 0 }, new[] { 0f, 0, 2 }, new[] { 1f, 0, 0 }, new[] { 1f, 1, 0 }, new[] { 1f, 0, 3 } };
            for (int i = 0; i < 6; i++) { int o = vo + i * 48; F(o, V[i][0]); F(o + 4, V[i][1]); F(o + 8, V[i][2]); F(o + 20, 1); F(o + 32, i * 0.1f); F(o + 36, 0.5f); }
            // one texture (type 0, wrap x and y)
            I32(0x50, 1); I32(0x54, 0x2A0); I32(0x2A0, 0); I32(0x2A4, 3);
            // texture weights: one track whose first value is 1.0 (32767)
            I32(0x58, 1); I32(0x5C, 0x2C0); I32(0x2C0 + 12, 1); I32(0x2C0 + 16, 0x2E0); I32(0x2E0, 1); I32(0x2E4, 0x2F0); U16(0x2F0, 32767);
            // materials: opaque; alpha key + two-sided (flag 4); modulate
            I32(0x70, 3); I32(0x74, 0x260); U16(0x260, 0); U16(0x262, 0); U16(0x264, 4); U16(0x266, 1); U16(0x268, 0); U16(0x26A, 5);
            I32(0x80, 1); I32(0x84, 0x280); U16(0x280, 0);         // texture lookup -> texture 0
            I32(0x90, 1); I32(0x94, 0x290); U16(0x290, 0);         // transparency lookup -> weight 0
            F(0xA0, 0); F(0xA4, 0); F(0xA8, 0); F(0xAC, 1); F(0xB0, 1); F(0xB4, 3);
            var ms = new MemoryStream(); var w = new BinaryWriter(ms);
            w.Write(Encoding.ASCII.GetBytes("MD21")); w.Write(md.Length); w.Write(md);
            w.Write(Encoding.ASCII.GetBytes("SFID")); w.Write(4); w.Write(skinFdid);
            w.Write(Encoding.ASCII.GetBytes("TXID")); w.Write(4); w.Write(texFdid);
            w.Flush(); m2 = ms.ToArray();
            var sk = new byte[0x200];
            void S32(int o, int v) => BitConverter.GetBytes(v).CopyTo(sk, o);
            void S16(int o, int v) => BitConverter.GetBytes((ushort)v).CopyTo(sk, o);
            Encoding.ASCII.GetBytes("SKIN").CopyTo(sk, 0);
            S32(4, 6); S32(8, 0x40); for (int i = 0; i < 6; i++) S16(0x40 + i * 2, i);
            int[] idx = { 0, 1, 2, 3, 4, 5, 0, 2, 5, 1, 4, 5 };
            S32(12, idx.Length); S32(16, 0x60); for (int i = 0; i < idx.Length; i++) S16(0x60 + i * 2, idx[i]);
            S32(28, 3); S32(32, 0x80);
            int[][] subs = { new[] { 0, 3 }, new[] { 3, 6 }, new[] { 9, 3 } };
            for (int s = 0; s < 3; s++) { S16(0x80 + s * 48 + 8, subs[s][0]); S16(0x80 + s * 48 + 10, subs[s][1]); }
            S32(36, 3); S32(40, 0x140);
            for (int b = 0; b < 3; b++) { int q = 0x140 + b * 24; S16(q + 4, b); S16(q + 8, 0xFFFF); S16(q + 10, b); S16(q + 16, 0); S16(q + 20, 0); }
            skin = sk;
        }

        // a BLP2 DXT1 texture, 8 x 8 with its 4 mip levels (8, 4, 2, 1), every block one colour
        static byte[] ObjBlp(int alphaDepth = 0, int alphaType = 0, int w = 8, int h = 8)
        {
            var levels = new List<byte[]>();
            for (int k = 0; ; k++)
            {
                int lw = Math.Max(1, w >> k), lh = Math.Max(1, h >> k), n = ((lw + 3) / 4) * ((lh + 3) / 4) * (alphaType == 7 || alphaType == 1 ? 16 : 8);
                var l = new byte[n]; for (int i = 0; i < n; i++) l[i] = (byte)(k * 16 + i);
                levels.Add(l);
                if (lw == 1 && lh == 1) break;
            }
            var d = new List<byte>(); var hdr = new byte[148];
            Encoding.ASCII.GetBytes("BLP2").CopyTo(hdr, 0); BitConverter.GetBytes(1).CopyTo(hdr, 4); hdr[8] = 2; hdr[9] = (byte)alphaDepth; hdr[10] = (byte)alphaType; hdr[11] = 1;
            BitConverter.GetBytes(w).CopyTo(hdr, 12); BitConverter.GetBytes(h).CopyTo(hdr, 16);
            int off = 148;
            for (int k = 0; k < levels.Count; k++) { BitConverter.GetBytes(off).CopyTo(hdr, 20 + k * 4); BitConverter.GetBytes(levels[k].Length).CopyTo(hdr, 84 + k * 4); off += levels[k].Length; }
            d.AddRange(hdr); foreach (var l in levels) d.AddRange(l);
            return d.ToArray();
        }

        void ObjectsFormatChecks(string work, CheckFn Check)
        {
            // each part on its own: a crash names its part and the others still run
            void Part(string name, Action a) { try { a(); } catch (Exception e) { Check(name + ": crashed", false, e.ToString()); } }
            List<M2Placement> dd = null; byte[] m2 = null, skin = null, blp = null; string odir = Path.Combine(work, "objects");
            Part("MDDF", () => {
            // ---- MDDF
            var obj0 = ObjMddf((101, 1, 17000f, 40f, 17100f, 90f, 1024, 0x40), (102, 2, 17010f, 41f, 17110f, 0f, 2048, 0x40), (103, 3, 17020f, 42f, 17120f, 0f, 0, 0), (104, 4, float.NaN, 0, 0, 0, 1024, 0x40));
            dd = AdtObjects.ParseDoodads(obj0, out var prob);
            Check("MDDF: 36-byte records read with the FileDataID flag, the scale (2048 = 2.0, 0 = 1.0), a name-index record flagged, a NaN position left out (" + dd.Count + " of 4)",
                prob == null && dd.Count == 3 && dd[0].NameId == 101 && dd[0].X == 17000f && dd[0].Y == 40f && dd[0].RotY == 90f && dd[0].Scale == 1f && !dd[0].HasNameIndex
                && dd[1].Scale == 2f && dd[2].HasNameIndex && dd[2].Scale == 1f);
            Check("MDDF: an empty or cut-off object file is no placements and says why", AdtObjects.ParseDoodads(new byte[4], out var p2).Count == 0 && p2 != null && AdtObjects.ParseDoodads(null, out _).Count == 0);

            });
            // ---- the model
            Part("the model", () => {
            ObjModel(900, 901, out m2, out skin);
            var root = M2Mesh.Root.Parse(m2);
            var mesh = M2Mesh.Parse(m2, skin);
            Check("M2: the chunked root names its skin (" + string.Join(",", root.Skins) + ") and texture (" + string.Join(",", root.Textures) + ")", root.Ok && root.Skins.SequenceEqual(new[] { 900 }) && root.Textures.SequenceEqual(new[] { 901 }));
            Check("M2: the batches - opaque, cut-out (alpha key, two-sided), and the modulate part left out (" + string.Join(" ", mesh.Batches.Select(b => b.Kind + (b.TwoSided ? "/2" : ""))) + ", " + mesh.Skipped + " skipped)",
                mesh.Drawable && mesh.Name == "Tree" && mesh.Batches.Count == 2 && mesh.Batches[0].Kind == "opaque" && !mesh.Batches[0].TwoSided && mesh.Batches[1].Kind == "cut" && mesh.Batches[1].TwoSided && mesh.Skipped == 1
                && mesh.Batches.All(b => b.Tex == 901 && b.Wrap == 3) && mesh.Batches[0].N == 3 && mesh.Batches[1].N == 6 && mesh.Batches[1].I0 == 3 && mesh.Idx.Count == 9);
            // WoW (x north, y west, z up) -> scene local (y, z, x): the vertex (0, 0, 2) is 2 up; (1, 0, 3) is 3 up and 1 along z
            Check("M2: the axes - WoW (x, y, z) becomes (y, z, x) in the scene, so up is up (vertex 2 at " + string.Join(",", mesh.Pos.Skip(6).Take(3)) + ", vertex 5 at " + string.Join(",", mesh.Pos.Skip(15).Take(3)) + "), and the box (" + string.Join(",", mesh.Min) + " / " + string.Join(",", mesh.Max) + ")",
                mesh.Pos[6] == 0 && mesh.Pos[7] == 2 && mesh.Pos[8] == 0 && mesh.Pos[15] == 0 && mesh.Pos[16] == 3 && mesh.Pos[17] == 1 && mesh.Max[1] == 3 && mesh.Min[1] == 0);
            var mb = mesh.ToBytes();
            Check("M2: the model file - \"M2B1\", its textures listed for the build, and a model with nothing to draw is the 4-byte stub",
                mb.Length > 64 && mb[0] == 'M' && mb[3] == '1' && M2Mesh.TexturesOf(mb).SequenceEqual(new[] { 901 }) && M2Mesh.Parse(m2, null).ToBytes().Length == 4 && M2Mesh.TexturesOf(M2Mesh.Stub).Count == 0 && M2Mesh.TexturesOf(new byte[3]) == null);
            Check("M2: a model without a skin chunk, a damaged skin or nonsense bytes is not drawable and never throws",
                !M2Mesh.Parse(m2.Take(0x408).ToArray(), skin).Drawable && !M2Mesh.Parse(m2, skin.Take(40).ToArray()).Drawable && !M2Mesh.Parse(new byte[500], new byte[500]).Drawable && !M2Mesh.Parse(null, null).Drawable);

            });
            // ---- the textures
            Part("the textures", () => {
            blp = ObjBlp();
            var tr = ObjTexture.TrimBytes(blp, 4);
            Check("texture: trimmed to the first level that fits (8 x 8 -> 4 x 4 for a cap of 4), the levels moved down and only their bytes kept",
                tr != null && BitConverter.ToInt32(tr, 12) == 4 && BitConverter.ToInt32(tr, 16) == 4 && BitConverter.ToInt32(tr, 20) == 148 && BitConverter.ToInt32(tr, 84) == 8 && BitConverter.ToInt32(tr, 84 + 2 * 4) == 8 && BitConverter.ToInt32(tr, 84 + 3 * 4) == 0 && tr.Length == 148 + 24 && tr[148] == 16);
            var pk = ObjTexture.Pack(tr);
            Check("texture: packed as DXT1 with every level down to 1 x 1 (3 levels: 4, 2, 1), the game's own blocks unchanged",
                pk != null && pk[0] == 'M' && pk[3] == '1' && BitConverter.ToUInt16(pk, 4) == 4 && pk[8] == 1 && pk[9] == 3 && BitConverter.ToInt32(pk, 12) == 8 && pk[16] == 16);
            var p5 = ObjTexture.Pack(ObjBlp(8, 7)); var p1a = ObjTexture.Pack(ObjBlp(1, 0)); var p3 = ObjTexture.Pack(ObjBlp(8, 1));
            Check("texture: the formats - DXT5 (alpha type 7), DXT1 with 1-bit alpha, DXT3 (alpha type 1)", p5 != null && p5[8] == 5 && p1a != null && p1a[8] == 2 && p3 != null && p3[8] == 3);
            var odd = ObjTexture.Pack(ObjBlp(0, 0, 6, 6));
            Check("texture: a DXT texture whose sides are not multiples of 4 is decoded to RGBA instead (one level, the page makes the rest)", odd != null && odd[8] == 0 && odd[9] == 1 && BitConverter.ToInt32(odd, 12) == 6 * 6 * 4);
            Check("texture: nonsense or a JPEG BLP is refused (null), never thrown", ObjTexture.Pack(new byte[200]) == null && ObjTexture.Pack(null) == null && ObjTexture.TrimBytes(new byte[10]) == null);

            });
            // ---- placements and the tile list
            Part("placements", () => {
            var scan = new ObjectScan();
            scan.ByTile[31 * 64 + 48] = dd.Take(2).ToList(); scan.Placements = 2;
            WorldMapObjects.WritePlacements(odir, scan);
            var back = WorldMapObjects.TileFromBytes(File.ReadAllBytes(WorldMapObjects.TileFile(odir, 31, 48)));
            var tl = WorldMapObjects.TilesFromBytes(File.ReadAllBytes(WorldMapObjects.TilesPath(odir)));
            Check("placements: a tile's file round-trips (fdid, position, rotation, scale) and the tile list names it with its count",
                back != null && back.Count == 2 && back[1].NameId == 102 && back[1].Scale == 2f && back[0].RotY == 90f && back[0].Z == 17100f && tl != null && tl.Count == 1 && tl[31 * 64 + 48] == 2 && WorldMapObjects.PlacementsOnDisk(odir, scan));
            Check("placements: a damaged file is refused", WorldMapObjects.TileFromBytes(new byte[9]) == null && WorldMapObjects.TilesFromBytes(Encoding.ASCII.GetBytes("M2T1\x01\0\0\0")) == null);

            });
            // ---- one model with its texture through the build's step
            Part("build", () => {
            var files = new Dictionary<int, byte[]> { [500] = m2, [900] = skin, [901] = blp };
            var src = new FuncBuildingSource(id => files.TryGetValue(id, out var b) ? b : null);
            long n = WorldMapObjects.BuildModel(500, src, odir, new Dictionary<int, bool>());
            Check("build: a model and its texture are written (the texture first), the model lists the texture", n > 0 && File.Exists(WorldMapObjects.TextureFile(odir, 901)) && M2Mesh.TexturesOf(File.ReadAllBytes(WorldMapObjects.ModelFile(odir, 500))).SequenceEqual(new[] { 901 }) && WorldMapObjects.BuiltModels(odir).SetEquals(new[] { 500 }));
            files.Remove(901); files[600] = m2;
            long n2 = WorldMapObjects.BuildModel(600, new FuncBuildingSource(id => id == 901 ? null : files.TryGetValue(id, out var b) ? b : null), Path.Combine(work, "objects2"), new Dictionary<int, bool>());
            var meta2 = Json.Obj(Encoding.UTF8.GetString(File.ReadAllBytes(WorldMapObjects.ModelFile(Path.Combine(work, "objects2"), 600)), 8, BitConverter.ToInt32(File.ReadAllBytes(WorldMapObjects.ModelFile(Path.Combine(work, "objects2"), 600)), 4)));
            Check("build: a texture that cannot be read makes its batches a stand-in colour (tex 0) and the model is still drawn", n2 > 0 && meta2 != null && meta2.List("batches").OfType<Dictionary<string, object>>().All(b => b.Int("tex") == 0) && meta2.List("textures").Count == 0);
            long n3 = WorldMapObjects.BuildModel(700, src, odir, new Dictionary<int, bool>());
            Check("build: a model that is not in the install is a stub on disk (not tried again) and not listed for the page", n3 == 0 && File.Exists(WorldMapObjects.ModelFile(odir, 700)) && !WorldMapObjects.BuiltModels(odir).Contains(700));
            });

            // ---- the page's messages: only the four levels and the four card actions; the app's state message
            Part("protocol", () =>
            {
                WorldMapMessage Pm(string j) => WorldMapProtocol.Parse(j, out _);
                string Why(string j) { WorldMapProtocol.Parse(j, out var why); return why; }
                var lv = new[] { "off", "low", "medium", "high" }.Select(l => Pm("{\"t\":\"objects\",\"level\":\"" + l + "\"}")).ToList();
                Check("protocol: objects {level} takes off, low, medium and high", lv.All(m => m != null && m.Type == WorldMapMessageType.Objects) && lv.Select(m => m.Detail).SequenceEqual(new[] { "off", "low", "medium", "high" }));
                Check("protocol: any other level, or none, is refused with the reason (" + Why("{\"t\":\"objects\",\"level\":\"ultra\"}") + ")",
                    Pm("{\"t\":\"objects\",\"level\":\"ultra\"}") == null && Pm("{\"t\":\"objects\"}") == null && Pm("{\"t\":\"objects\",\"level\":3}") == null && (Why("{\"t\":\"objects\",\"level\":\"x\"}") ?? "").Contains("low, medium or high"));
                Check("protocol: obuild, opause, oresume, ocancel", Pm("{\"t\":\"obuild\"}")?.Type == WorldMapMessageType.OBuild && Pm("{\"t\":\"opause\"}")?.Type == WorldMapMessageType.OPause
                    && Pm("{\"t\":\"oresume\"}")?.Type == WorldMapMessageType.OResume && Pm("{\"t\":\"ocancel\"}")?.Type == WorldMapMessageType.OCancel);
                var st = new ObjectsState { MapId = 0, Name = "Eastern Kingdoms", Phase = "ask", Total = 3000, Done = 0, Placements = 209000, Minutes = 2.5, Bytes = 180L << 20 };
                var js = Json.Obj(WorldMapObjectsHost.StateJson(0, "medium", st, new List<int> { 5, 6 }, null, new List<int> { 1984 }));
                Check("state: ostate carries the level, the phase, the card's sentence, the fresh models and the tiles (" + js?.Str("card") + ")",
                    js != null && js.Str("t") == "ostate" && js.Str("level") == "medium" && js.Str("phase") == "ask" && js.Int("placements") == 209000 && js.List("fresh").Count == 2 && js.List("tiles").Count == 1 && !js.ContainsKey("all")
                    && js.Str("card").StartsWith("Trees & objects of Eastern Kingdoms: 3,000 models for 209,000 placements, about 3 minutes and 180 MB", StringComparison.Ordinal));
                Check("setting: a saved level is one of the four, anything else is Off", WorldMapObjects.Normalize("high") == "high" && WorldMapObjects.Normalize(null) == "off" && WorldMapObjects.Normalize("ULTRA") == "off");
            });

            // ---- the developer's pipe (Services/DevPipe.cs) exists only in a build made with -p:ElansDevPipe=true (Tools/devoutpost.ps1): never in this one
            Part("dev pipe", () =>
            {
                var asm = typeof(MainWindow).Assembly;
                Check("dev pipe: this build (the one the self-test and release.py make) has no developer's pipe and no DevReload in the world map", asm.GetType("ElansAddonHub.Services.DevPipe") == null && typeof(WorldMapView).GetMethod("DevReload") == null);
            });
        }
    }
}
