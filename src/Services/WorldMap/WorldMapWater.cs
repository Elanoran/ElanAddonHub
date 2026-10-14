using System;
using System.Collections.Generic;
using System.IO;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Maps;

namespace ElansAddonHub.Services.WorldMap
{
    // What a liquid is, for the colour it is drawn in.
    public enum LiquidKind : byte { None = 0, Water = 1, Ocean = 2, Magma = 3, Slime = 4 }

    // ============================================================================================================================
    // LiquidType.db2 -> kind. The table (57 rows on build 1.60.1.70338; layout hash D1ECEEC9, see MapTables.LiquidType) has the classic ids (1 Water, 2 Ocean, 3 Magma,
    // 4 Slime and their slow / fast / WMO copies) and the new ones the root ADTs use ("PBRWater - Generic - Lake" 1240, "PBRWater - Generic - Ocean" 1250,
    // "1.60.0 FX - Fel - Generic - Bright - Lake" 1269 ...). Field 0 is the name, field 3 the SoundBank byte (0 water, 1 ocean, 2 magma, 3 slime). The kind is decided by
    // the name first (ocean / lava, magma, molten, tar / slime, fel), then by the sound bank; Fel (the green fire of the Burning Legion) and tar draw as slime.
    // Without the table (an unknown layout, no game files, a test) the well-known ids and the numbers of the root ADTs are used: see Fallback.
    // ============================================================================================================================
    public sealed class LiquidKinds
    {
        readonly Dictionary<int, LiquidKind> map = new Dictionary<int, LiquidKind>();
        public int Rows => map.Count;
        public bool FromTable { get; private set; }
        public string Problem;

        public static LiquidKind ByName(string name, int soundBank)
        {
            var n = (name ?? "").ToLowerInvariant();
            if (n.Contains("ocean")) return LiquidKind.Ocean;
            if (n.Contains("lava") || n.Contains("magma") || n.Contains("molten")) return LiquidKind.Magma;
            if (n.Contains("slime") || n.Contains("fel ") || n.Contains("- fel") || n.Contains("tar ")) return LiquidKind.Slime;
            switch (soundBank) { case 1: return LiquidKind.Ocean; case 2: return LiquidKind.Magma; case 3: return LiquidKind.Slime; default: return LiquidKind.Water; }
        }

        // the classic ids and the ones the build's root ADTs use, for when the table cannot be read
        public static LiquidKind Fallback(int id)
        {
            switch (id)
            {
                case 2: case 6: case 10: case 14: case 1250: case 1287: case 1296: return LiquidKind.Ocean;
                case 3: case 7: case 11: case 15: case 19: case 1174: case 1177: case 1267: case 1295: return LiquidKind.Magma;
                case 4: case 8: case 12: case 20: case 21: case 1268: case 1269: case 1284: case 1347: case 1348: case 1349: return LiquidKind.Slime;
                default: return LiquidKind.Water;
            }
        }

        public LiquidKind Of(int id) => map.TryGetValue(id, out var k) ? k : Fallback(id);

        public static LiquidKinds Fallbacks() => new LiquidKinds();

        // from the raw LiquidType.db2; Problem is set (and the fallbacks are used) when it is not the layout this Outpost knows
        public static LiquidKinds FromDb2(byte[] raw)
        {
            var r = new LiquidKinds();
            if (raw == null) { r.Problem = "LiquidType.db2 is not in this install"; return r; }
            try
            {
                var w = Wdc5.Parse(raw);
                if (w.TableHash != MapTables.LiquidType.TableHash || !MapTables.LiquidType.Knows(w.LayoutHash)) { r.Problem = "LiquidType.db2 has a layout this Outpost does not know (" + w.LayoutHash.ToString("X8") + ")"; return r; }
                for (int i = 0; i < w.Count; i++)
                {
                    string name = ""; try { name = w.String(i, MapTables.LiquidName); } catch (Exception) { }
                    r.map[w.IdAt(i)] = ByName(name, (int)w.Field(i, MapTables.LiquidSoundBank));
                }
                r.FromTable = r.map.Count > 0;
            }
            catch (Exception e) { r.Problem = "LiquidType.db2: " + e.Message; r.map.Clear(); }
            return r;
        }
    }

    public sealed class WaterLayer
    {
        public int Chunk;                   // IndexY * 16 + IndexX
        public LiquidKind Kind;
        public int TypeId;
        public ulong Mask;                  // bit (cy * 8 + cx) of the chunk's 8 x 8 cells
        public bool Flat => Heights == null;
        public float Height;                // flat: the surface
        public float[] Heights;             // 9 x 9 ([vy * 9 + vx], vy north -> south, vx west -> east); null = flat

        public float HeightAt(int vy, int vx) => Heights == null ? Height : Heights[vy * 9 + vx];
        public bool Has(int cx, int cy) => (Mask >> (cy * 8 + cx) & 1) != 0;
        public int Cells { get { int n = 0; for (ulong m = Mask; m != 0; m &= m - 1) n++; return n; } }
    }

    // ============================================================================================================================
    // The liquids of one map tile, made from its root ADT's MH2O and kept next to the height grid:   <map folder>\terrain\{col}_{row}.wmw
    //   "WMW1" | int col | int row | int layers | layers x { byte chunk, byte kind, byte flags (1 = flat), byte 0, ushort liquid type, ushort 0, ulong cell mask,
    //                                                          float height   (flat)   |   float height[9 x 9]   }
    //   chunk = IndexY * 16 + IndexX, mask bit cy * 8 + cx (cy north -> south, cx west -> east), height grids [vy * 9 + vx]. A flat full ocean chunk is 20 bytes, so an
    //   ocean tile is 5 KB and a land tile with a lake or two a few hundred bytes. A tile without liquid is a 16-byte file (so "no file" means "not read yet").
    // ============================================================================================================================
    public sealed class WaterTile
    {
        public int Col, Row;
        public List<WaterLayer> Layers = new List<WaterLayer>();

        public static WaterTile FromAdt(int col, int row, AdtFile f, LiquidKinds kinds)
        {
            var t = new WaterTile { Col = col, Row = row };
            foreach (var l in f.Liquids)
            {
                var k = kinds != null ? kinds.Of(l.LiquidType) : LiquidKinds.Fallback(l.LiquidType);
                var layer = new WaterLayer { Chunk = l.Chunk, Kind = k, TypeId = l.LiquidType, Mask = l.Mask, Height = l.MinHeight };
                if (l.Heights != null)
                {
                    float mn = float.MaxValue, mx = float.MinValue; double sum = 0;
                    foreach (var h in l.Heights) { if (h < mn) mn = h; if (h > mx) mx = h; sum += h; }
                    if (mx - mn <= 0.02f) layer.Height = (float)(sum / 81); else layer.Heights = (float[])l.Heights.Clone();
                }
                t.Layers.Add(layer);
            }
            return t;
        }

        public byte[] ToBytes()
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write(new[] { (byte)'W', (byte)'M', (byte)'W', (byte)'1' });
            w.Write(Col); w.Write(Row); w.Write(Layers.Count);
            foreach (var l in Layers)
            {
                w.Write((byte)l.Chunk); w.Write((byte)l.Kind); w.Write((byte)(l.Flat ? 1 : 0)); w.Write((byte)0);
                w.Write((ushort)l.TypeId); w.Write((ushort)0); w.Write(l.Mask);
                if (l.Flat) w.Write(l.Height); else foreach (var h in l.Heights) w.Write(h);
            }
            w.Flush();
            return ms.ToArray();
        }

        public static WaterTile FromBytes(byte[] b)
        {
            try
            {
                if (b == null || b.Length < 16 || b[0] != 'W' || b[1] != 'M' || b[2] != 'W' || b[3] != '1') return null;
                var t = new WaterTile { Col = BitConverter.ToInt32(b, 4), Row = BitConverter.ToInt32(b, 8) };
                int n = BitConverter.ToInt32(b, 12), o = 16;
                if (n < 0 || n > 256 * 16) return null;
                for (int i = 0; i < n; i++)
                {
                    if (o + 16 > b.Length) return null;
                    var l = new WaterLayer { Chunk = b[o], Kind = (LiquidKind)b[o + 1], TypeId = BitConverter.ToUInt16(b, o + 4), Mask = BitConverter.ToUInt64(b, o + 8) };
                    bool flat = (b[o + 2] & 1) != 0; o += 16;
                    if (l.Kind < LiquidKind.Water || l.Kind > LiquidKind.Slime) return null;
                    if (flat) { if (o + 4 > b.Length) return null; l.Height = BitConverter.ToSingle(b, o); o += 4; }
                    else
                    {
                        if (o + 324 > b.Length) return null;
                        l.Heights = new float[81]; for (int k = 0; k < 81; k++) l.Heights[k] = BitConverter.ToSingle(b, o + k * 4);
                        o += 324;
                    }
                    t.Layers.Add(l);
                }
                return o == b.Length ? t : null;
            }
            catch (Exception) { return null; }
        }

        public int Cells { get { int n = 0; foreach (var l in Layers) n += l.Cells; return n; } }
        // the highest liquid surface that covers the chunk cell (cx, cy) of chunk `chunk`; NaN = none
        public float SurfaceAt(int chunk, int cx, int cy)
        {
            float best = float.NaN;
            foreach (var l in Layers)
            {
                if (l.Chunk != chunk || !l.Has(cx, cy)) continue;
                float h = l.Flat ? l.Height : (l.Heights[cy * 9 + cx] + l.Heights[cy * 9 + cx + 1] + l.Heights[(cy + 1) * 9 + cx] + l.Heights[(cy + 1) * 9 + cx + 1]) / 4;
                if (float.IsNaN(best) || h > best) best = h;
            }
            return best;
        }
    }
}
