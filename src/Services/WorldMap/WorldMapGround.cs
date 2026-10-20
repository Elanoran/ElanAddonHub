using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace ElansAddonHub.Services.WorldMap
{
    // ============================================================================================================================
    // The real ground of the 3D world map (3.2, part of the experimental Trees & objects switch, levels Medium and High): near the spot you look at, the page
    // paints the terrain with the game's own ground textures as the game blends them, and fades to the minimap picture beyond. A continent of it would be
    // 600+ MB, so nothing is made ahead: the page asks for a tile when it comes near, the app makes its file from the game files then (~40 ms) and keeps it:
    //   objects\g\{col}_{row}.bin   "M2G2" | int col | int row | ushort textures | ushort flags (1 = vertex colours) | uint fdid[textures] |
    //                               byte slot[256 x 4] (per chunk iy * 16 + ix its layers' texture slots; 255 = no layer; 254 in the first = this chunk has no
    //                               ground: the page shows the minimap there) | int n | deflate( alpha: layer 1's 1024 x 1024 bytes, then layer 2's, then layer 3's (planar: a third smaller than interleaved) ) |
    //                               int n | deflate( colours 129 x 129 x RGB: the MCCV of the chunks' corners, 127 = unchanged )
    //   objects\gt\{fdid}.bin       "M2GT" | ushort 256 | ushort 256 | deflate( RGBA 256 x 256 ): one ground texture (its mip level of 256 px, resized when it has none)
    // Pixel (x, y) of a tile's pictures: x east, y south (the scene's x and z), chunk ix = x / 64, iy = y / 64.
    // ============================================================================================================================
    public static class WorldMapGround
    {
        public const int AlphaPx = 1024, ColorPx = 129, TexPx = 256;
        public const byte NoLayer = 255, NoGround = 254;
        public static string TileFile(string objDir, int col, int row) => Path.Combine(objDir, "g", col.ToString(CultureInfo.InvariantCulture) + "_" + row.ToString(CultureInfo.InvariantCulture) + ".bin");
        public static string TextureFile(string objDir, int fdid) => Path.Combine(objDir, "gt", fdid.ToString(CultureInfo.InvariantCulture) + ".bin");

        static byte[] Deflate(byte[] raw)
        {
            var ms = new MemoryStream();
            using (var z = new DeflateStream(ms, CompressionLevel.Optimal, true)) z.Write(raw, 0, raw.Length);       // a third smaller than Fastest (0.75 MB a tile), still well under 100 ms
            return ms.ToArray();
        }
        public static byte[] Inflate(byte[] d, int off, int len)
        {
            using (var z = new DeflateStream(new MemoryStream(d, off, len), CompressionMode.Decompress))
            using (var o = new MemoryStream()) { z.CopyTo(o); return o.ToArray(); }
        }

        // a tile's file from its tex0 (null = none: the whole tile shows the minimap) and root ADT; bigAlpha from the WDT (MPHD 0x4 | 0x80)
        public static byte[] MakeTile(int col, int row, byte[] tex0, byte[] root, bool bigAlpha, out int textures, out string problem)
        {
            problem = null;
            var rootFlags = Tex0File.RootChunks(root, out var mccv);
            var t = tex0 == null ? null : Tex0File.Parse(tex0, bigAlpha, rootFlags);
            if (t == null) problem = "no texture file";
            else if (t.Problem != null) problem = t.Problem;
            var slots = new byte[1024]; for (int i = 0; i < slots.Length; i++) slots[i] = NoLayer;
            var used = new List<int>(); var slotOf = new Dictionary<int, int>();
            var alpha = new byte[AlphaPx * AlphaPx * 3];
            for (int k = 0; k < 256; k++)
            {
                int iy = k / 16, ix = k % 16;
                var c = problem == null ? t.Chunks[k] : null;
                bool ok = c != null && c.Layers.Count > 0;
                if (ok)
                    foreach (var L in c.Layers)
                        if (L.Texture < 0 || L.Texture >= t.Diffuse.Count || t.Diffuse[L.Texture] <= 0) { ok = false; break; }
                if (!ok) { slots[k * 4] = NoGround; continue; }
                for (int li = 0; li < c.Layers.Count; li++)
                {
                    int fd = t.Diffuse[c.Layers[li].Texture];
                    if (!slotOf.TryGetValue(fd, out var sl)) { if (used.Count >= 250) { ok = false; break; } sl = used.Count; slotOf[fd] = sl; used.Add(fd); }
                    slots[k * 4 + li] = (byte)sl;
                }
                if (!ok) { slots[k * 4] = NoGround; continue; }
                for (int y = 0; y < 64; y++)
                    for (int x = 0; x < 64; x++)
                    {
                        int o = (iy * 64 + y) * AlphaPx + ix * 64 + x, p = y * 64 + x;
                        for (int li = 1; li < 4; li++) alpha[o + (li - 1) * AlphaPx * AlphaPx] = li < c.Layers.Count && c.Layers[li].Alpha != null ? c.Layers[li].Alpha[p] : (byte)0;
                    }
            }
            // the colours of the chunks' corners: outer vertex (r, c) of chunk (ix, iy) is pixel (ix * 8 + c, iy * 8 + r); MCCV entries are B, G, R, A
            var col3 = new byte[ColorPx * ColorPx * 3]; for (int i = 0; i < col3.Length; i++) col3[i] = 127;
            bool anyMccv = false;
            for (int k = 0; k < 256; k++)
            {
                var m = mccv[k]; if (m == null) continue;
                anyMccv = true;
                int iy = k / 16, ix = k % 16;
                for (int r = 0; r < 9; r++) for (int cc = 0; cc < 9; cc++)
                {
                    int e = (r * 17 + cc) * 4, o = ((iy * 8 + r) * ColorPx + ix * 8 + cc) * 3;
                    col3[o] = m[e + 2]; col3[o + 1] = m[e + 1]; col3[o + 2] = m[e];
                }
            }
            textures = used.Count;
            var a = Deflate(alpha); var cz = Deflate(col3);
            var ms = new MemoryStream(); var w = new BinaryWriter(ms);
            w.Write(new[] { (byte)'M', (byte)'2', (byte)'G', (byte)'2' }); w.Write(col); w.Write(row); w.Write((ushort)used.Count); w.Write((ushort)(anyMccv ? 1 : 0));
            foreach (var fd in used) w.Write((uint)fd);
            w.Write(slots);
            w.Write(a.Length); w.Write(a); w.Write(cz.Length); w.Write(cz);
            w.Flush(); return ms.ToArray();
        }

        // a ground texture: the BLP's level of 256 px (trimmed by the source), decoded and resized to 256 x 256 when needed; null when it cannot be read
        public static byte[] MakeTexture(byte[] trimmedBlp)
        {
            try
            {
                if (trimmedBlp == null) return null;
                var bgra = Blp.Decode(trimmedBlp, out int w, out int h);
                var rgba = new byte[TexPx * TexPx * 4];
                for (int y = 0; y < TexPx; y++)
                    for (int x = 0; x < TexPx; x++)
                    {
                        int sx = x * w / TexPx, sy = y * h / TexPx, s = (sy * w + sx) * 4, o = (y * TexPx + x) * 4;
                        rgba[o] = bgra[s + 2]; rgba[o + 1] = bgra[s + 1]; rgba[o + 2] = bgra[s]; rgba[o + 3] = 255;
                    }
                var z = Deflate(rgba);
                var ms = new MemoryStream(); var bw = new BinaryWriter(ms);
                bw.Write(new[] { (byte)'M', (byte)'2', (byte)'G', (byte)'T' }); bw.Write((ushort)TexPx); bw.Write((ushort)TexPx); bw.Write(z);
                bw.Flush(); return ms.ToArray();
            }
            catch (Exception) { return null; }
        }
    }

    // ============================================================================================================================
    // Makes the ground files when the page asks for them (gneed {c, r} -> WorldMapView -> MakeTile -> gready). One open
    // set of game files per continent, closed after a minute without a request; one request at a time per continent (the CASC reader is not thread-safe).
    // ============================================================================================================================
    public static class GroundService
    {
        public static Func<IBuildingSource> SourceForTest;
        public static int MadeForTest;

        sealed class Open { public IBuildingSource Src; public Dictionary<int, WdtTile> Tiles; public bool BigAlpha; public DateTime Used; public readonly object Gate = new object(); }
        static readonly Dictionary<int, Open> open = new Dictionary<int, Open>();
        static System.Threading.Timer closer;

        static Open Get(int mapId)
        {
            lock (open)
            {
                if (open.TryGetValue(mapId, out var o)) { o.Used = DateTime.UtcNow; return o; }
                var src = BuildingsService.OpenSourceFor(mapId, SourceForTest, out var tiles);
                bool big = true;
                try
                {
                    var cont = WorldMapService.Continent(Maps.MapData.Index, mapId);
                    var wdt = cont != null ? WdtFile.Parse(src.Read(cont.WdtFileDataId)) : null;
                    if (wdt != null && wdt.Ok) big = (wdt.MphdFlags & (0x4 | 0x80)) != 0;
                }
                catch (Exception) { }
                o = new Open { Src = src, Tiles = tiles.ToDictionary(t => t.Col * 64 + t.Row), BigAlpha = big, Used = DateTime.UtcNow };
                open[mapId] = o;
                if (closer == null) closer = new System.Threading.Timer(_ => CloseIdle(TimeSpan.FromSeconds(60)), null, 30000, 30000);
                return o;
            }
        }

        public static void CloseIdle(TimeSpan idle)
        {
            lock (open)
                foreach (var k in open.Keys.ToList())
                {
                    var o = open[k];
                    if (DateTime.UtcNow - o.Used < idle) continue;
                    lock (o.Gate) { try { o.Src.Dispose(); } catch (Exception) { } }
                    open.Remove(k);
                }
        }

        // the file's bytes (made now and kept on disk when it was not there); null = cannot be made (the page then keeps the minimap)
        public static byte[] Make(int mapId, int col, int row, int fdid)
        {
            var objDir = WorldMapObjects.Dir(WorldMapCache.MapDir(mapId));
            var path = fdid > 0 ? WorldMapGround.TextureFile(objDir, fdid) : WorldMapGround.TileFile(objDir, col, row);
            // a file of an older format (a tile "M2G1": interleaved alpha) is made again
            try { if (File.Exists(path)) { var b = File.ReadAllBytes(path); if (fdid > 0 || (b.Length > 4 && b[3] == '2')) return b; } } catch (Exception) { }
            var o = Get(mapId);
            byte[] data;
            lock (o.Gate)
            {
                o.Used = DateTime.UtcNow;
                if (fdid > 0) data = WorldMapGround.MakeTexture(o.Src.ReadBlp(fdid, WorldMapGround.TexPx));
                else
                {
                    if (!o.Tiles.TryGetValue(col * 64 + row, out var t)) return null;
                    data = WorldMapGround.MakeTile(col, row, t.Tex0Fdid > 0 ? o.Src.Read(t.Tex0Fdid) : null, t.RootFdid > 0 ? o.Src.Read(t.RootFdid) : null, o.BigAlpha, out _, out var problem);
                    if (problem != null) Util.Log("world map ground " + mapId + " " + col + "," + row + ": " + problem + " (the minimap is shown there)");
                }
            }
            if (data == null) return null;
            System.Threading.Interlocked.Increment(ref MadeForTest);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var tmp = path + "." + System.Threading.Thread.CurrentThread.ManagedThreadId + ".tmp";
                File.WriteAllBytes(tmp, data);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch (Exception e) { Util.Log("world map ground: " + e.Message); }
            return data;
        }

        // 3.2 cities: a building's detailed model (WmoDetail) and its textures; false = it cannot be made (the simplified shape stays)
        public static bool MakeBuilding(int mapId, int fdid)
        {
            var objDir = WorldMapObjects.Dir(WorldMapCache.MapDir(mapId));
            var path = WmoDetail.ModelFile(objDir, fdid);
            if (File.Exists(path)) return new FileInfo(path).Length > 8;
            var o = Get(mapId);
            byte[] bytes;
            lock (o.Gate)
            {
                o.Used = DateTime.UtcNow;
                var rb = o.Src.Read(fdid); var root = rb == null ? null : WmoRoot.Parse(rb);
                if (root == null || !root.Ok) return false;
                o.Src.Prefetch(root.OutsideGroups().Select(gi => gi.Fdid).Concat(root.TextureFdid.Where(t => t > 0 && t <= int.MaxValue).Select(t => (int)t)));
                var texOk = new Dictionary<int, bool>();
                bool TexOk(int t)
                {
                    if (texOk.TryGetValue(t, out var ok)) return ok;
                    var tf = WorldMapObjects.TextureFile(objDir, t);
                    ok = File.Exists(tf);
                    if (!ok) { var packed = ObjTexture.Pack(o.Src.ReadBlp(t, ObjTexture.MaxSize)); if (packed != null) { Write(tf, packed); ok = true; } }
                    return texOk[t] = ok;
                }
                var mesh = WmoDetail.Make(root, gf => o.Src.Read(gf), TexOk, out int groups, out int skipped);
                bytes = mesh.Drawable ? mesh.ToBytes() : (byte[])M2Mesh.Stub.Clone();
                Util.Log("world map buildings detail " + fdid + ": " + groups + " groups (" + skipped + " not readable), " + mesh.Nv + " vertices, " + mesh.Idx.Count / 3 + " triangles, " + mesh.Batches.Count + " materials, " + bytes.Length / 1024 + " KB");
            }
            Write(path, bytes);
            System.Threading.Interlocked.Increment(ref MadeForTest);
            return bytes.Length > 8;
        }

        static void Write(string path, byte[] data)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var tmp = path + "." + System.Threading.Thread.CurrentThread.ManagedThreadId + "." + Guid.NewGuid().ToString("N").Substring(0, 6) + ".tmp";
                File.WriteAllBytes(tmp, data);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch (Exception e) { Util.Log("world map ground: " + e.Message); }
        }

        // a tile's file and the files of all its textures (the page fetches them once it hears gready); false = the tile has none (the minimap stays)
        public static bool MakeTile(int mapId, int col, int row)
        {
            var b = Make(mapId, col, row, 0);
            if (b == null || b.Length < 16) return false;
            int n = BitConverter.ToUInt16(b, 12);
            for (int i = 0; i < n; i++) Make(mapId, -1, -1, (int)BitConverter.ToUInt32(b, 16 + i * 4));
            return true;
        }

        public static void ResetForTest() { lock (open) { foreach (var o in open.Values) { try { o.Src.Dispose(); } catch (Exception) { } } open.Clear(); } }
    }
}
