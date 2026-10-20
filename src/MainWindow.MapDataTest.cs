using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Maps;

namespace ElansAddonHub
{
    // --selftest "mapdata": Atlas layer B, maps read from the player's own WoW files, the coordinate system and the AtlasMap control.
    // Everything runs on tables and tiles built in the test (the WDC5 writer of the itemdata test), under the scratch folder. The real game files are
    // only touched by the last, optional check (read-only, skipped when there is no WoW install).
    public partial class MainWindow
    {
        // ---- a small invented world: a continent with two zones that overlap, a city, a world map with two windows, a dungeon with three floors ...
        static UiAssignment Asg(int id, int ui, int map, double minX, double maxX, double minY, double maxY, double u0 = 0, double v0 = 0, double u1 = 1, double v1 = 1, int area = 0, int order = 0)
            => new UiAssignment { Id = id, UiMapId = ui, MapId = map, AreaId = area, Order = order, MinX = (float)minX, MaxX = (float)maxX, MinY = (float)minY, MaxY = (float)maxY, MinZ = -1000000, MaxZ = 1000000, UiMinX = (float)u0, UiMinY = (float)v0, UiMaxX = (float)u1, UiMaxY = (float)v1 };

        static MapArtInfo SynthArt(int id, int w, int h, int tile, int firstFdid)
        {
            var layer = new ArtLayerInfo { Index = 0, Width = w, Height = h, TileWidth = tile, TileHeight = tile };
            var tiles = new List<ArtTile>();
            for (int r = 0; r < layer.Rows; r++) for (int c = 0; c < layer.Cols; c++) tiles.Add(new ArtTile { Row = r, Col = c, Layer = 0, FileDataId = firstFdid + r * layer.Cols + c });
            return new MapArtInfo { Id = id, StyleId = id, Layers = new[] { layer }, Tiles = tiles.ToArray() };
        }

        static MapSet SynthMapSet()
        {
            var s = new MapSet { HasArt = true, HasAssignments = true, HasMaps = true, HasFloors = true, HasAreas = true };
            UiMapInfo U(int id, string name, int parent, int type, int flags = 2, int sys = 0) => new UiMapInfo { Id = id, Name = name, ParentId = parent, Type = type, Flags = flags, System = sys };
            s.UiMaps.AddRange(new[]
            {
                U(1, "World", 0, UiMapInfo.World, 0), U(10, "Northland", 1, UiMapInfo.Continent, 0), U(11, "Greenvale", 10, UiMapInfo.Zone), U(12, "Redmoor", 10, UiMapInfo.Zone),
                U(13, "Highkeep", 10, UiMapInfo.Zone, 0), U(14, "Northland (alternative)", 0, UiMapInfo.Continent, 0, 1),
                U(20, "Crypt cellar", 0, UiMapInfo.Dungeon, 0), U(21, "Crypt hall", 0, UiMapInfo.Dungeon, 0), U(22, "Crypt tower", 0, UiMapInfo.Dungeon, 0),
                U(30, "Frostfall", 0, UiMapInfo.Orphan, 0), U(40, "Loop A", 41, UiMapInfo.Zone), U(41, "Loop B", 40, UiMapInfo.Zone), U(50, "Barren", 10, UiMapInfo.Zone),
                U(0 + 2, "Cosmos", 0, UiMapInfo.Cosmic, 0),
            });
            s.UiMaps.First(m => m.Id == 1).ParentId = 2;
            // the continent: x (north) -1000..1000, y (west) -2000..2000. Greenvale (north-west part) and Redmoor (south) overlap; the city is small and sits in both.
            s.Assignments.AddRange(new[]
            {
                Asg(1, 10, 0, -1000, 1000, -2000, 2000),
                Asg(2, 11, 0, 0, 1000, 0, 2000),
                Asg(3, 12, 0, -1000, 200, -1000, 1000),
                Asg(4, 13, 0, 100, 300, 100, 400),
                Asg(5, 14, 0, -1000, 1000, -2000, 2000),
                Asg(6, 1, 0, -1000, 1000, -2000, 2000, 0.1, 0.2, 0.5, 0.8),       // the world map shows this continent in its left part ...
                Asg(7, 1, 5, 0, 1000, 0, 1000, 0.5, 0.2, 0.9, 0.8),               // ... and another continent (map 5) in its right part
                Asg(8, 20, 99, 0, 100, 0, 100), Asg(9, 21, 99, 0, 100, 0, 100), Asg(10, 22, 99, 0, 100, 0, 100),
                Asg(11, 30, 96, 0, 1500, 0, 1500),
            });
            s.Assignments[0].AreaId = 77;
            s.Arts.AddRange(new[]
            {
                SynthArt(100, 600, 300, 256, 1000),                           // 3 x 2 tiles, the last column and row cut off
                SynthArt(101, 256, 256, 256, 2000), SynthArt(102, 256, 256, 256, 2100), SynthArt(103, 256, 256, 256, 2200),
                SynthArt(104, 512, 256, 256, 2300), SynthArt(150, 400, 200, 256, 3000), SynthArt(199, 100, 100, 256, 4000),
                new MapArtInfo { Id = 105, StyleId = 105, Layers = new[] { new ArtLayerInfo { Width = 256, Height = 256, TileWidth = 256, TileHeight = 256 } } },   // art without tiles
            });
            s.ArtLinks.AddRange(new[]
            {
                new MapArtLink { UiMapId = 11, ArtId = 199, Phase = 5 }, new MapArtLink { UiMapId = 11, ArtId = 100, Phase = 0 },   // phase 0 wins
                new MapArtLink { UiMapId = 10, ArtId = 150 }, new MapArtLink { UiMapId = 20, ArtId = 101 }, new MapArtLink { UiMapId = 21, ArtId = 102 },
                new MapArtLink { UiMapId = 22, ArtId = 103 }, new MapArtLink { UiMapId = 30, ArtId = 104 }, new MapArtLink { UiMapId = 12, ArtId = 105 },
            });
            s.Maps.AddRange(new[]
            {
                new GameMapInfo { Id = 0, Name = "Northland", Directory = "North", InstanceType = 0 },
                new GameMapInfo { Id = 99, Name = "Crypt of Bones", Directory = "Crypt", InstanceType = 1, MaxPlayers = 5 },
                new GameMapInfo { Id = 98, Name = "Deep Hall", InstanceType = 2, MaxPlayers = 40 },
                new GameMapInfo { Id = 97, Name = "<unused> Mine", InstanceType = 1 },
                new GameMapInfo { Id = 96, Name = "Frostfall", InstanceType = 3 },
                new GameMapInfo { Id = 95, Name = "Arena of Ash", InstanceType = 4 },
            });
            // the group's members are listed out of order on purpose
            s.Floors.AddRange(new[]
            {
                new FloorMember { GroupId = 7, UiMapId = 21, FloorIndex = 1, Name = "Hall" }, new FloorMember { GroupId = 7, UiMapId = 22, FloorIndex = 2, Name = "Tower" },
                new FloorMember { GroupId = 7, UiMapId = 20, FloorIndex = 0, Name = "Cellar" },
            });
            s.Areas.Add(new AreaInfo { Id = 77, Name = "Northland", ParentId = 0 });
            s.Areas.Add(new AreaInfo { Id = 78, Name = "Greenvale Mill", ParentId = 77 });
            return s;
        }

        // ---- WDC5 tables of the real layouts (the writer lives in MainWindow.ItemDataTest.cs)
        static W5Field F32(int i) => new W5Field { OffsetBits = i * 32, SizeBits = 32, Storage = 0 };

        // UiMap: Name_lang(0, string) ID(1) ParentUiMapID(2) Flags(3) System(4) Type(5); sections: [947 Azeroth, 1411 Durotar] [locked: 1500] [1412 Mulgore]
        // The string offsets count from the field to a string block laid out as if all four records (the locked one too) came first.
        static byte[] UiMapTable(uint tableHash = 0, uint layout = 0xDB51D55F)
        {
            if (tableHash == 0) tableHash = MapTables.UiMap.TableHash;
            var fields = Enumerable.Range(0, 6).Select(F32).ToArray();
            byte[] Rec(int strOffset, int globalIndex, int total, int parent, int flags, int sys, int type)
            {
                var r = new byte[24];
                void P(int at, int v) => Array.Copy(BitConverter.GetBytes(v), 0, r, at, 4);
                P(0, total * 24 - globalIndex * 24 + strOffset); P(8, parent); P(12, flags); P(16, sys); P(20, type);
                return r;
            }
            // concatenated string blocks: A "\0Azeroth\0Durotar\0" (17) + B "zzzz\0" (5) + C "\0Mulgore\0" (9)
            var a = new W5Section { Records = new[] { Rec(1, 0, 4, 0, 0x900, 0, 1), Rec(9, 1, 4, 947, 2, 0, 3) }, Strings = Encoding.ASCII.GetBytes("\0Azeroth\0Durotar\0"), Ids = new[] { 947, 1411 } };
            var b = new W5Section { Key = 0xABCDEF0123456789UL, Records = new[] { new byte[24] }, Strings = Encoding.ASCII.GetBytes("zzzz\0"), Ids = new[] { 1500 } };
            var c = new W5Section { Records = new[] { Rec(17 + 5 + 1, 3, 4, 947, 2, 0, 3) }, Strings = Encoding.ASCII.GetBytes("\0Mulgore\0"), Ids = new[] { 1412 } };
            return BuildWdc5(tableHash, layout, false, 24, fields, new[] { a, b, c });
        }

        // UiMapAssignment: UiMin[2] UiMax[2] Region[6] as plain float arrays (one field each, the field's width = the whole array), then ID, UiMapID, OrderIndex, MapID, AreaID, 3 more
        static byte[] AssignmentTable()
        {
            var fields = new List<W5Field>
            {
                new W5Field { OffsetBits = 0, SizeBits = 64 }, new W5Field { OffsetBits = 64, SizeBits = 64 }, new W5Field { OffsetBits = 128, SizeBits = 192 },
            };
            for (int i = 0; i < 8; i++) fields.Add(new W5Field { OffsetBits = 320 + i * 32, SizeBits = 32 });
            byte[] Rec(int ui, int map, int area, float[] uiMin, float[] uiMax, float[] region)
            {
                var r = new byte[72];
                void F(int at, float v) => Array.Copy(BitConverter.GetBytes(v), 0, r, at, 4);
                void I(int at, int v) => Array.Copy(BitConverter.GetBytes(v), 0, r, at, 4);
                F(0, uiMin[0]); F(4, uiMin[1]); F(8, uiMax[0]); F(12, uiMax[1]);
                for (int i = 0; i < 6; i++) F(16 + i * 4, region[i]);
                I(44, ui); I(48, 0); I(52, map); I(56, area);       // UiMapID @44 (field 4 starts at bit 352), OrderIndex @48, MapID @52, AreaID @56 (the id sits in the id list)
                return r;
            }
            // Durotar-like (min/max given the wrong way round on purpose: the reader orders them), a world window, and a broken one (UiMax <= UiMin) that is left out
            var s = new W5Section
            {
                Records = new[]
                {
                    Rec(1411, 1, 14, new[] { 0f, 0f }, new[] { 1f, 1f }, new[] { 1808.3333f, -1962.5f, 1000000f, -1716.6666f, -7249.9995f, -1000000f }),
                    Rec(947, 1, 0, new[] { 0.0399f, 0.0855f }, new[] { 0.4083f, 0.9234f }, new[] { -12800f, -9600f, -1000000f, 12266.7f, 6933.3f, 1000000f }),
                    Rec(1500, 1, 0, new[] { 0.5f, 0.5f }, new[] { 0.5f, 0.9f }, new[] { 0f, 0f, 0f, 1f, 1f, 1f }),
                },
                Ids = new[] { 46721, 46785, 46999 },
            };
            return BuildWdc5(MapTables.UiMapAssignment.TableHash, 0xC9CC8DFB, false, 72, fields.ToArray(), new[] { s });
        }

        // a table whose last section is followed by relationship data: (foreign id, record index) pairs, as in UiMapArtTile.UiMapArtID
        static byte[] TableWithRelations()
        {
            var fields = new[] { F32(0) };
            byte[] Rec(int v) => BitConverter.GetBytes(v);
            var sec = new W5Section { Records = new[] { Rec(5), Rec(6), Rec(7) }, Ids = new[] { 10, 11, 12 }, Copies = new[] { new[] { 13, 11 } } };
            var bytes = BuildWdc5(0x11112222, 0x33334444, false, 4, fields, new[] { sec });
            var rel = new MemoryStream(); var w = new BinaryWriter(rel);
            w.Write(3); w.Write(100); w.Write(300);                       // entries, min, max
            w.Write(100); w.Write(0); w.Write(300); w.Write(2); w.Write(200); w.Write(1);     // record 0 -> 100, record 2 -> 300, record 1 -> 200
            var all = bytes.Concat(rel.ToArray()).ToArray();
            Array.Copy(BitConverter.GetBytes((int)rel.Length), 0, all, 204 + 28, 4);          // relationship_data_size in the section header
            return all;
        }

        static Dictionary<string, byte[]> RawWith(params KeyValuePair<string, byte[]>[] tables)
        {
            var d = new Dictionary<string, byte[]>();
            foreach (var t in tables) d[t.Key] = t.Value;
            return d;
        }
        static KeyValuePair<string, byte[]> Raw(string n, byte[] b) => new KeyValuePair<string, byte[]>(n, b);

        // a BLP2 file of 4 x 4 raw BGRA pixels (encoding 3), as the game's uncompressed tiles
        static byte[] RawBlp(byte[] bgra4x4)
        {
            var b = new byte[1172 + 64];
            Encoding.ASCII.GetBytes("BLP2").CopyTo(b, 0);
            b[4] = 1; b[8] = 3; b[9] = 8; b[10] = 8; b[11] = 0;
            Array.Copy(BitConverter.GetBytes(4), 0, b, 12, 4); Array.Copy(BitConverter.GetBytes(4), 0, b, 16, 4);
            Array.Copy(BitConverter.GetBytes(1172), 0, b, 20, 4); Array.Copy(BitConverter.GetBytes(64), 0, b, 20 + 64, 4);
            Array.Copy(bgra4x4, 0, b, 1172, 64);
            return b;
        }

        static MapRender.TileImage Solid(int w, int h, byte b, byte g, byte r, byte a = 255)
        {
            var px = new byte[w * h * 4];
            for (int i = 0; i < w * h; i++) { px[i * 4] = b; px[i * 4 + 1] = g; px[i * 4 + 2] = r; px[i * 4 + 3] = a; }
            return new MapRender.TileImage { Bgra = px, Width = w, Height = h };
        }

        // WorldMapOverlay (13 fields: ID, UiMapArtID, TextureWidth, TextureHeight, OffsetX, OffsetY, 4 hit-rect numbers, PlayerConditionID, Flags, AreaID) and
        // WorldMapOverlayTile (RowIndex, ColIndex, LayerIndex, FileDataID + the relationship to the overlay), written in the layouts of build 70338.
        // Overlay 5001 (300 x 200 at 50,-40; two tiles) and 5002 (100 x 100 at 400,150; one tile) belong to `art`, 5009 to an art that does not exist.
        static Tuple<byte[], byte[]> OverlayTables(int art, uint overlayLayout = 0x9A196494, uint tileLayout = 0x2C843422)
        {
            var of = Enumerable.Range(0, 13).Select(F32).ToArray();
            byte[] ORec(int a, int w, int h, int x, int y) { var r = new byte[52]; void P(int i, int v) => Array.Copy(BitConverter.GetBytes(v), 0, r, i * 4, 4); P(1, a); P(2, w); P(3, h); P(4, x); P(5, y); return r; }
            var os = new W5Section { Records = new[] { ORec(art, 300, 200, 50, -40), ORec(art, 100, 100, 400, 150), ORec(art + 999, 64, 64, 0, 0) }, Ids = new[] { 5001, 5002, 5009 } };
            var ovTable = BuildWdc5(MapTables.WorldMapOverlay.TableHash, overlayLayout, false, 52, of, new[] { os });
            var tf = Enumerable.Range(0, 4).Select(F32).ToArray();
            byte[] TRec(int row, int col, int layer, int fdid) { var r = new byte[16]; void P(int i, int v) => Array.Copy(BitConverter.GetBytes(v), 0, r, i * 4, 4); P(0, row); P(1, col); P(2, layer); P(3, fdid); return r; }
            var ts = new W5Section { Records = new[] { TRec(0, 1, 0, 7002), TRec(0, 0, 0, 7001), TRec(0, 0, 0, 7003), TRec(0, 0, 0, 7009) }, Ids = new[] { 16901, 16902, 16903, 16904 } };
            var tileBytes = BuildWdc5(MapTables.WorldMapOverlayTile.TableHash, tileLayout, false, 16, tf, new[] { ts });
            var rel = new MemoryStream(); var w = new BinaryWriter(rel);
            w.Write(4); w.Write(5001); w.Write(5009);                                  // entries, min, max
            w.Write(5001); w.Write(0); w.Write(5001); w.Write(1); w.Write(5002); w.Write(2); w.Write(5009); w.Write(3);      // record 0 (col 1) and 1 (col 0) -> overlay 5001, 2 -> 5002, 3 -> 5009
            var all = tileBytes.Concat(rel.ToArray()).ToArray();
            Array.Copy(BitConverter.GetBytes((int)rel.Length), 0, all, 204 + 28, 4);
            return Tuple.Create(ovTable, all);
        }

        static byte[] PixelAt(byte[] bgra, int width, int x, int y) => new[] { bgra[(y * width + x) * 4], bgra[(y * width + x) * 4 + 1], bgra[(y * width + x) * 4 + 2], bgra[(y * width + x) * 4 + 3] };

        async Task<string> MapDataTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            void Check(string what, bool cond, string detail = null) { SelfTestClock.Mark(what); notes.Add((cond ? "ok   " : "FAIL ") + what + (!cond && detail != null ? " [" + detail + "]" : "")); ok &= cond; }
            string P(string n) => Path.Combine(dir, n);
            var work = Path.Combine(dir, "mapdata");
            var savedCache = MapData.CacheFile; var savedRoot = MapData.WowRoot; var savedEnabled = MapData.Enabled; var savedDir = MapRender.DirOverride; var savedTiles = MapRender.TileSourceForTest;
            try
            {
                if (Directory.Exists(work)) Util.DeleteDir(work);
                Directory.CreateDirectory(work);
                Check("fixture: everything of this test is under the scratch folder (never the real game, never the real data folder)", Path.GetFullPath(work).StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase));
                MapData.ResetForTest();
                MapData.CacheFile = Path.Combine(work, "cache", "mapdata.bin");
                MapRender.DirOverride = Path.Combine(work, "maps");

                // ---- 3.1.1, the root's ties (CascStorage.PickRootEntries) on a root of its own: one id listed twice with the same score where only the second copy is
                // on the disk (the Forever client's terrain and building textures), both copies local, neither, an id listed once, and a better score that is not local
                {
                    byte[] K(int id, char copy) { var k = new byte[16]; k[0] = (byte)id; k[1] = (byte)copy; return k; }
                    var blocks = new List<Tuple<int, int[], byte[][]>>
                    {
                        Tuple.Create(7, new[] { 10, 20, 30 }, new[] { K(10, 'a'), K(20, 'a'), K(30, 'a') }),
                        Tuple.Create(3, new[] { 50 }, new[] { K(50, 'c') }),
                        Tuple.Create(7, new[] { 10, 20, 30, 40, 50 }, new[] { K(10, 'b'), K(20, 'b'), K(30, 'b'), K(40, 'b'), K(50, 'd') }),
                    };
                    void Root(Action<int, byte[], byte[], int> block)
                    {
                        foreach (var bl in blocks)
                        {
                            var ids = new byte[bl.Item2.Length * 4]; var keys = new byte[bl.Item2.Length * 16];
                            for (int i = 0; i < bl.Item2.Length; i++) { Buffer.BlockCopy(BitConverter.GetBytes(bl.Item2[i]), 0, ids, i * 4, 4); Buffer.BlockCopy(bl.Item3[i], 0, keys, i * 16, 16); }
                            block(bl.Item2.Length, ids, keys, bl.Item1);
                        }
                    }
                    var localKeys = new HashSet<string> { "10b", "20a", "20b", "40b", "50c" };
                    int asked = 0;
                    bool[] Local(byte[] ks, int n) { asked += n; var r = new bool[n]; for (int i = 0; i < n; i++) r[i] = localKeys.Contains(ks[i * 16] + "" + (char)ks[i * 16 + 1]); return r; }
                    string Pick(CascStorage.RootPick rp, int id) { int i = Array.BinarySearch(rp.Ids, id); return i < 0 ? "none" : rp.Keys[i * 16] + "" + (char)rp.Keys[i * 16 + 1]; }
                    string Line(CascStorage.RootPick rp) => string.Join(" ", new[] { 10, 20, 30, 40, 50, 99 }.Select(id => id + ":" + Pick(rp, id)));
                    // the index: the first entries, the ties kept; an id is decided when asked for (here 10 alone, then all), the same answers as deciding at once
                    var all = CascStorage.PickRootEntries(Root, 4, null);
                    string before = Line(all);
                    CascStorage.DecideTies(all, new[] { 10, 40, 99 }, Local); int askedOne = asked; string afterOne = Line(all);
                    CascStorage.DecideTies(all, null, Local); int askedAll = asked;
                    CascStorage.DecideTies(all, null, Local);
                    var some = CascStorage.PickRootEntries(Root, 4, new HashSet<int> { 10, 30, 99 }); CascStorage.DecideTies(some, null, Local);
                    int took = all.TieState.Count(x => x == 2);
                    Check("root ties (3.1.1): an id listed twice with the same score takes the copy that is on the disk (10 -> the second copy), both local -> the first (20), neither -> the first (30, read as missing as before), listed once -> that one (40), a better score is kept even when only a worse one is local (50); " + Line(all),
                        Pick(all, 10) == "10b" && Pick(all, 20) == "20a" && Pick(all, 30) == "30a" && Pick(all, 40) == "40b" && Pick(all, 50) == "50d" && Pick(all, 99) == "none" && all.Ids.Length == 5 && all.TieIds.SequenceEqual(new[] { 10, 20, 30 }) && took == 1 && all.TieState[0] == 2,
                        Line(all) + " ties " + all.TieIds.Length + " took " + took);
                    Check("root ties (3.1.1): before any lookup the index holds the old choice, the first copy (" + before + "); asking for 10 decides only 10 (" + afterOne + ", " + askedOne + " keys checked), the rest when asked, each tie once (" + askedAll + " keys in all); a walk of a few wanted ids gives the same (" + Line(some) + ")",
                        before == "10:10a 20:20a 30:30a 40:40b 50:50d 99:none" && afterOne == "10:10b 20:20a 30:30a 40:40b 50:50d 99:none" && askedOne == 2 && askedAll == 6 && asked == 6 + 4 && Pick(some, 10) == "10b" && Pick(some, 30) == "30a" && some.Ids.Length == 2,
                        before + " / " + afterOne + " / " + Line(some) + " asked " + askedOne + "," + askedAll + "," + asked);
                }

                // ---- the table reader: relationship data, float arrays, strings across sections
                var rel = Wdc5.Parse(TableWithRelations());
                Check("wdc5: relationship data (UiMapArtTile.UiMapArtID style): record 0 -> 100, 1 -> 200, 2 -> 300; the copy row 13 of record 11 has 200 too; others 0",
                    rel.Count == 4 && rel.ParentOf(0) == 100 && rel.ParentOf(1) == 200 && rel.ParentOf(2) == 300 && rel.ParentOf(3) == 200, rel.ParentOf(0) + "," + rel.ParentOf(1) + "," + rel.ParentOf(2) + "," + rel.ParentOf(3));
                var noRel = Wdc5.Parse(SparseTable(0x6FCC3191));
                Check("wdc5: a table without relationship data says 0", noRel.ParentOf(0) == 0);
                var asgTable = Wdc5.Parse(AssignmentTable());
                Check("wdc5: a float[2] / float[6] field is one field as wide as the whole array; Element() reads the parts (0.0399, 0.0855 / -12800, 12266.7)",
                    asgTable.Count == 3 && BitConverter.ToSingle(BitConverter.GetBytes((int)asgTable.Element(1, 0, 0, 2)), 0) == 0.0399f && BitConverter.ToSingle(BitConverter.GetBytes((int)asgTable.Element(1, 0, 1, 2)), 0) == 0.0855f
                    && BitConverter.ToSingle(BitConverter.GetBytes((int)asgTable.Element(1, 2, 0, 6)), 0) == -12800f && BitConverter.ToSingle(BitConverter.GetBytes((int)asgTable.Element(1, 2, 3, 6)), 0) == 12266.7f);
                var um = Wdc5.Parse(UiMapTable());
                Check("wdc5: strings of a table with several sections (the locked section's records count towards the offsets): Azeroth, Durotar, Mulgore; the locked row is not there",
                    um.Count == 3 && um.String(0, 0) == "Azeroth" && um.String(1, 0) == "Durotar" && um.TryIndex(1412, out int mi) && um.String(mi, 0) == "Mulgore" && !um.TryIndex(1500, out _) && um.EncryptedRecords == 1, um.String(0, 0) + "/" + um.String(1, 0));

                // ---- MapTables.Parse: the layouts, nothing guessed
                var one = MapTables.Parse(RawWith(Raw("UiMap", UiMapTable())));
                Check("parse: UiMap alone is enough for the tree: 3 maps with names, parents, flags and types; the locked row noted; no art / assignments / maps (flags false)",
                    one.Ok && one.UiMaps.Count == 3 && one.UiMaps.First(m => m.Id == 1411).Name == "Durotar" && one.UiMaps.First(m => m.Id == 1411).ParentId == 947 && one.UiMaps.First(m => m.Id == 947).Flags == 0x900
                    && one.UiMaps.First(m => m.Id == 1411).Type == 3 && !one.HasArt && !one.HasAssignments && !one.HasMaps && one.Notes.Any(n => n.Contains("key-locked")) && one.Notes.Any(n => n.Contains("no map art")), one.Problem + " " + string.Join(";", one.Notes));
                var unk = MapTables.Parse(RawWith(Raw("UiMap", UiMapTable(layout: 0xDEADBEEF))));
                Check("parse: a UiMap layout this version does not know = unavailable, 'game updated' with the hash, no rows", !unk.Ok && unk.Problem.Contains("game updated") && unk.Problem.Contains("DEADBEEF") && unk.UiMaps.Count == 0, unk.Problem);
                var foreign = MapTables.Parse(RawWith(Raw("UiMap", UiMapTable(tableHash: 0x12345678))));
                Check("parse: another table in the file's place is refused", !foreign.Ok && foreign.Problem.Contains("not the one"), foreign.Problem);
                var missing = MapTables.Parse(RawWith(Raw("UiMap", null)));
                var nothing = MapTables.Parse(new Dictionary<string, byte[]>());
                Check("parse: a missing UiMap = unavailable with a reason, never an exception", !missing.Ok && !nothing.Ok && missing.Problem.Contains("not in this game install"), missing.Problem);
                var garbage = MapTables.Parse(RawWith(Raw("UiMap", new byte[] { 1, 2, 3 })));
                var cut = MapTables.Parse(RawWith(Raw("UiMap", UiMapTable().Take(300).ToArray())));
                Check("parse: garbage or a cut file = unavailable with a reason, no exception", !garbage.Ok && !cut.Ok && garbage.Problem.Length > 0 && cut.Problem.Length > 0, garbage.Problem + " / " + cut.Problem);
                var withAsg = MapTables.Parse(RawWith(Raw("UiMap", UiMapTable()), Raw("UiMapAssignment", AssignmentTable())));
                var d1 = withAsg.Assignments.FirstOrDefault(a => a.UiMapId == 1411);
                Check("parse: assignments: world rectangle ordered min < max although stored the other way round, Durotar's numbers, map id and area; the unusable row (UiMax <= UiMin) is left out",
                    withAsg.Ok && withAsg.HasAssignments && withAsg.Assignments.Count == 2 && d1 != null && d1.MapId == 1 && d1.AreaId == 14 && Math.Abs(d1.MinX - -1716.6666) < 0.01 && Math.Abs(d1.MaxX - 1808.3333) < 0.01 && Math.Abs(d1.MinY - -7249.9995) < 0.01
                    && Math.Abs(d1.MaxY - -1962.5) < 0.01 && d1.UiMaxX == 1f, withAsg.Assignments.Count + " " + (d1 == null ? "-" : d1.MinX + " " + d1.MaxY));
                var badAsg = MapTables.Parse(RawWith(Raw("UiMap", UiMapTable()), Raw("UiMapAssignment", AssignmentTable().Take(400).ToArray())));
                Check("parse: a broken optional table costs only that part (no world positions), the tree stays", badAsg.Ok && badAsg.UiMaps.Count == 3 && !badAsg.HasAssignments && badAsg.Notes.Any(n => n.Contains("UiMapAssignment")), string.Join(";", badAsg.Notes));
                Check("layout registry: sixteen tables (nine + the two overlay tables + the three flight tables + LFGDungeons and ContentTuning), each with a FileDataID, a table hash and a layout; all different", MapTables.All.Length == 16 && MapTables.All.Select(t => t.FileDataId).Distinct().Count() == 16 && MapTables.All.Select(t => t.TableHash).Distinct().Count() == 16 && MapTables.WorldMapOverlay.Knows(0x9A196494) && MapTables.WorldMapOverlayTile.Knows(0x2C843422) && MapTables.All.All(t => t.Layouts.Length > 0 && t.TableHash != 0) && MapTables.UiMap.Knows(0xDB51D55F) && !MapTables.UiMap.Knows(0xDEADBEEF));

                // ---- the coordinate system on the invented world
                var set = SynthMapSet();
                var idx = new MapIndex(set);
                MapPoint? W(int map, double x, double y) => idx.WorldToMap(map, x, y);
                string Nm(MapPoint? p) => p == null ? "none" : idx.Name(p.Value.UiMapId);
                bool Near(double a, double b, double e = 1e-6) => Math.Abs(a - b) < e;
                var p1 = W(0, 500, 1000);
                Check("world -> map: a point only Greenvale shows lands on Greenvale at the right fractions: x 500 of 0..1000 is half way down, y 1000 of 0..2000 half way across -> (0.5, 0.5)",
                    p1 != null && p1.Value.UiMapId == 11 && Near(p1.Value.Fx, 0.5) && Near(p1.Value.Fy, 0.5), p1?.ToString());
                var p2 = W(0, 900, 1900);
                Check("the picture runs west -> east and north -> south: more west (larger y) is further left, more north (larger x) is further up",
                    p2 != null && p2.Value.UiMapId == 11 && Near(p2.Value.Fx, 0.05) && Near(p2.Value.Fy, 0.1), p2?.ToString());
                Check("two neighbouring zones whose rectangles overlap: the one the point is most central in wins (150,600 is in the middle of Greenvale, 50,20 is far inside Redmoor)",
                    Nm(W(0, 150, 600)) == "Greenvale" && Nm(W(0, 50, 20)) == "Redmoor", Nm(W(0, 150, 600)) + " / " + Nm(W(0, 50, 20)));
                Check("a city beats the zones it sits in (its rectangle is tight): 200,200 is Highkeep at (0.5, 0.5)... of its own rectangle", Nm(W(0, 200, 200)) == "Highkeep" && Near(W(0, 200, 200).Value.Fx, (400 - 200) / 300.0) && Near(W(0, 200, 200).Value.Fy, (300 - 200) / 200.0));
                Check("outside every zone the continent shows it; the alternative copy of the continent is never chosen", Nm(W(0, -900, -1900)) == "Northland" && W(0, -900, -1900).Value.UiMapId == 10);
                Check("nothing shows a position outside the continent or on an unknown map", W(0, 5000, 0) == null && W(42, 0, 0) == null && W(0, 0, 9000) == null);
                var onCont = idx.WorldToMap(0, 500, 1000, 10);
                Check("world -> a given map: the same point on the continent map is (0.25, 0.25); on a map that does not show it, null", onCont != null && Near(onCont.Value.Fx, 0.25) && Near(onCont.Value.Fy, 0.25) && idx.WorldToMap(0, 500, 1000, 13) == null && idx.WorldToMap(1, 500, 1000, 10) == null);
                var onWorld = idx.WorldToMap(0, 500, 1000, 1);
                Check("the world map shows the continent in a window of its picture: (0.25, 0.25) of the continent is (0.1 + 0.25 x 0.4, 0.2 + 0.25 x 0.6) = (0.2, 0.35); the other continent has its own window",
                    onWorld != null && Near(onWorld.Value.Fx, 0.2) && Near(onWorld.Value.Fy, 0.35) && Near(idx.WorldToMap(5, 500, 500, 1).Value.Fx, 0.7) && Near(idx.WorldToMap(5, 500, 500, 1).Value.Fy, 0.5));
                var back = idx.MapToWorld(11, 0.5, 0.5);
                Check("map -> world is the inverse: (0.5, 0.5) of Greenvale is x 500, y 1000 on map 0", back != null && back.Value.MapId == 0 && Near(back.Value.X, 500) && Near(back.Value.Y, 1000), back?.ToString());
                var bw = idx.MapToWorld(1, 0.2, 0.35); var bw5 = idx.MapToWorld(1, 0.7, 0.5);
                Check("map -> world on the world map picks the window that holds the point: left part = map 0, right part = map 5; a spot no window covers = null",
                    bw != null && bw.Value.MapId == 0 && Near(bw.Value.X, 500, 1e-2) && Near(bw.Value.Y, 1000, 1e-2) && bw5 != null && bw5.Value.MapId == 5 && Near(bw5.Value.X, 500, 1e-2) && Near(bw5.Value.Y, 500, 1e-2) && idx.MapToWorld(1, 0.95, 0.95) == null);
                bool roundTrip = true;
                foreach (var m in idx.UiMaps.Where(m => m.System == 0 && idx.MapToWorld(m.Id, 0.5, 0.5) != null))
                    foreach (var f in new[] { 0.1, 0.5, 0.93 })
                    {
                        var wp = idx.MapToWorld(m.Id, f, 1 - f); if (wp == null) continue; var w = wp.Value;
                        var back2 = idx.WorldToMap(w.MapId, w.X, w.Y, m.Id);
                        if (back2 == null || !Near(back2.Value.Fx, f, 1e-4) || !Near(back2.Value.Fy, 1 - f, 1e-4)) roundTrip = false;
                    }
                Check("round trip: map -> world -> the same map gives the same fractions, for every map and three points", roundTrip);
                var conv = idx.Convert(11, 0.5, 0.5, 10);
                Check("Convert: a zone position on its continent map; the same map returns the same point; a map that does not show it, null",
                    conv != null && Near(conv.Value.Fx, 0.25) && Near(conv.Value.Fy, 0.25) && idx.Convert(11, 0.3, 0.4, 11).Value.Fx == 0.3 && idx.Convert(13, 0.5, 0.5, 11) != null && idx.Convert(10, 0.0, 0.0, 13) == null);
                Check("the area table finds the map of a zone: area 77 -> the continent, a sub-area (78) -> the same through its parent, an unknown area -> 0", idx.UiMapForArea(77) == 10 && idx.UiMapForArea(78) == 10 && idx.UiMapForArea(5) == 0 && idx.AreaName(78) == "Greenvale Mill" && idx.AreaName(1) == null);

                // ---- the tree, the art, instances and floors
                Check("tree: breadcrumb of Greenvale = Cosmos left out, World > Northland > Greenvale; a loop in the parents does not hang; an unknown map has none",
                    string.Join(" > ", idx.Breadcrumb(11).Select(m => m.Name)) == "World > Northland > Greenvale" && idx.Breadcrumb(40).Count == 2 && idx.Breadcrumb(9999).Count == 0, string.Join(" > ", idx.Breadcrumb(11).Select(m => m.Name)));
                Check("tree: children of the continent are its three zones + Barren + the alternative copy is not its child", idx.Children(10).Select(m => m.Id).SequenceEqual(new[] { 11, 12, 13, 50 }) && idx.Children(9999).Count == 0, string.Join(",", idx.Children(10).Select(m => m.Id)));
                Check("art: Greenvale shows art 100 (phase 0, not the phase 5 one); a map whose art has no tiles has none; 600 x 300 picture", idx.ArtFor(11).Id == 100 && !idx.HasArt(12) && !idx.HasArt(50) && idx.HasArt(10), idx.ArtFor(11)?.Id.ToString());
                idx.PictureSize(11, out int pw0, out int ph0); idx.PictureSize(50, out int pw1, out int ph1);
                Check("art: picture size from the layer (600 x 300, 3 x 2 tiles of 256), 0 x 0 without art", pw0 == 600 && ph0 == 300 && idx.ArtFor(11).BaseLayer.Cols == 3 && idx.ArtFor(11).BaseLayer.Rows == 2 && pw1 == 0 && ph1 == 0);
                Check("maps with art: 6 (the continent, Greenvale, the three floors, Frostfall) of 14", idx.MapsWithArt == 6 && idx.UiMapCount == 14, idx.MapsWithArt + "/" + idx.UiMapCount);
                var crypt = idx.Instance(99);
                Check("instances: the Crypt is a Dungeon for 5 with three floors in floor order (Cellar, Hall, Tower), a map with floors", crypt != null && crypt.Kind == "Dungeon" && crypt.MaxPlayers == 5 && crypt.HasMap && crypt.Floors.Select(f => f.Id).SequenceEqual(new[] { 20, 21, 22 }), crypt == null ? "null" : string.Join(",", crypt.Floors.Select(f => f.Id)));
                Check("instances: a raid without any map (Deep Hall) is listed with no floors; the unused leftover is flagged as a placeholder; the battleground has its one map; an arena is an arena; the open world is not an instance",
                    idx.Instance(98).Kind == "Raid" && !idx.Instance(98).HasMap && idx.Instance(97).Placeholder && !idx.Instance(98).Placeholder && idx.Instance(96).Floors.Select(f => f.Id).SequenceEqual(new[] { 30 }) && idx.Instance(95).Kind == "Arena" && idx.Instance(0) == null);
                Check("floors: any floor knows its siblings, names come from the group (Cellar / Hall / Tower), a plain map is its own only floor",
                    idx.FloorsOf(21).Select(f => f.Id).SequenceEqual(new[] { 20, 21, 22 }) && idx.FloorName(22) == "Tower" && idx.FloorsOf(11).Count == 1 && idx.FloorsOf(9999).Count == 0);
                Check("placeholder names: '<unused> Mine', 'Scott Test', 'nothing to see here', '2720', '' are placeholders; 'Deadmines', 'Test of Strength'-less real names are not", MapIndex.IsPlaceholderName("<unused> Mine") && MapIndex.IsPlaceholderName("Scott Test") && MapIndex.IsPlaceholderName("nothing to see here") && MapIndex.IsPlaceholderName("2720") && MapIndex.IsPlaceholderName("") && !MapIndex.IsPlaceholderName("Deadmines") && !MapIndex.IsPlaceholderName("Zul'Farrak"));
                Check("Atlas link ids meet here: the zone:<uiMapID> and instance:<mapId> of the foundation resolve to a map and an instance", idx.Get(11) != null && idx.Instance(99) != null);

                // ---- tile composition (no game files)
                var art = idx.ArtFor(11);
                MapRender.TileImage TileFor(int fdid)
                {
                    int i = fdid - 1000; int r = i / 3, c = i % 3;
                    return Solid(256, 256, 7, (byte)(r * 100 + 20), (byte)(c * 60 + 10));
                }
                var px = MapRender.Compose(art, TileFor, out int cw, out int ch, out int miss);
                Check("compose: 600 x 300 picture of 6 tiles, nothing missing, 4 bytes a pixel", cw == 600 && ch == 300 && miss == 0 && px.Length == 600 * 300 * 4);
                Check("compose: every tile goes to (column x 256, row x 256): top-left tile, top-middle tile, bottom-right tile (cut off at the picture's edge, pixel 599,299)",
                    PixelAt(px, 600, 10, 10).SequenceEqual(new byte[] { 7, 20, 10, 255 }) && PixelAt(px, 600, 300, 10).SequenceEqual(new byte[] { 7, 20, 70, 255 }) && PixelAt(px, 600, 255, 255).SequenceEqual(new byte[] { 7, 20, 10, 255 })
                    && PixelAt(px, 600, 256, 256).SequenceEqual(new byte[] { 7, 120, 70, 255 }) && PixelAt(px, 600, 599, 299).SequenceEqual(new byte[] { 7, 120, 130, 255 }), string.Join(",", PixelAt(px, 600, 599, 299)));
                var holes = MapRender.Compose(art, f => f == 1004 ? null : TileFor(f), out _, out _, out int holeCount);
                Check("compose: a tile the game does not have (null) is left transparent and counted; the rest is still there", holeCount == 1 && PixelAt(holes, 600, 300, 280)[3] == 0 && PixelAt(holes, 600, 10, 10)[3] == 255);
                Check("compose: when no tile exists at all there is nothing to make (null), when only some are missing the picture is made",
                    MapRender.RenderPng(art, f => null, out int m1, out _, out _) == null && m1 == 6 && MapRender.RenderPng(art, f => f == 1004 ? null : TileFor(f), out int m2, out _, out _) != null && m2 == 1);
                var png = MapRender.RenderPng(art, TileFor, out _, out _, out _);
                var pngFile = Path.Combine(work, "compose.png"); File.WriteAllBytes(pngFile, png);
                var loaded = MapRender.Load(pngFile) as BitmapSource;
                Check("PNG: 600 x 300, readable again, no file lock left behind", png.Length > 100 && loaded != null && loaded.PixelWidth == 600 && loaded.PixelHeight == 300 && TryDelete(pngFile));
                var blp = Blp.Decode(RawBlp(Enumerable.Range(0, 64).Select(i => (byte)(i * 3)).ToArray()), out int bw0, out int bh0);
                Check("tile decode: a BLP2 file through the existing decoder (BGRA, 4 x 4) gives its pixels", bw0 == 4 && bh0 == 4 && blp.Length == 64 && blp[5] == 15 && blp[63] == 189);
                bool tooBig = false;
                try { MapRender.Compose(new MapArtInfo { Layers = new[] { new ArtLayerInfo { Width = 100000, Height = 100000, TileWidth = 256, TileHeight = 256 } } }, f => null, out _, out _, out _); } catch (InvalidDataException) { tooBig = true; }
                Check("compose: an absurd layer size is refused (no 40 GB allocation)", tooBig);

                // ---- the explored-area overlays: drawn over the base art at their offsets, blended by alpha
                var ovArt = new MapArtInfo { Id = art.Id, StyleId = art.StyleId, Layers = art.Layers, Tiles = art.Tiles, Overlays = new[]
                {
                    new ArtOverlay { Id = 5001, Width = 300, Height = 200, OffsetX = 50, OffsetY = 40, Tiles = new[] { new ArtTile { Row = 0, Col = 0, FileDataId = 7001 }, new ArtTile { Row = 0, Col = 1, FileDataId = 7002 } } },
                    new ArtOverlay { Id = 5002, Width = 100, Height = 100, OffsetX = 400, OffsetY = 150, Tiles = new[] { new ArtTile { Row = 0, Col = 0, FileDataId = 7003 } } },
                    new ArtOverlay { Id = 5003, Width = 256, Height = 256, OffsetX = 1000, OffsetY = 1000, Tiles = new[] { new ArtTile { Row = 0, Col = 0, FileDataId = 7004 } } },      // outside the picture
                } };
                MapRender.TileImage OvTile(int f) => f == 7001 ? Solid(256, 256, 0, 0, 255) : f == 7002 ? Solid(256, 256, 0, 255, 0) : f == 7003 ? Solid(256, 256, 255, 0, 0, 128) : f == 7004 ? Solid(256, 256, 9, 9, 9) : TileFor(f);
                var withOv = MapRender.Compose(ovArt, OvTile, out int ow0, out int oh0, out int om0, out int ovMiss);
                Check("overlays: the picture keeps the art's size (600 x 300) and nothing is missing", ow0 == 600 && oh0 == 300 && om0 == 0 && ovMiss == 0 && withOv.Length == 600 * 300 * 4);
                Check("overlays: an overlay tile goes to (OffsetX + column x 256, OffsetY + row x 256): the first tile's corner pixel (50,40) is red and (49,40) / (50,39) are still the base art",
                    PixelAt(withOv, 600, 50, 40).SequenceEqual(new byte[] { 0, 0, 255, 255 }) && PixelAt(withOv, 600, 49, 40).SequenceEqual(PixelAt(px, 600, 49, 40)) && PixelAt(withOv, 600, 50, 39).SequenceEqual(PixelAt(px, 600, 50, 39)), string.Join(",", PixelAt(withOv, 600, 50, 40)));
                Check("overlays: the second column starts at 50 + 256 = 306 (green) and is cut at the overlay's TextureWidth 300: x 349 is green, x 350 is the base art again; the texture height 200 cuts at y 239 / 240",
                    PixelAt(withOv, 600, 305, 100).SequenceEqual(new byte[] { 0, 0, 255, 255 }) && PixelAt(withOv, 600, 306, 100).SequenceEqual(new byte[] { 0, 255, 0, 255 }) && PixelAt(withOv, 600, 349, 100).SequenceEqual(new byte[] { 0, 255, 0, 255 })
                    && PixelAt(withOv, 600, 350, 100).SequenceEqual(PixelAt(px, 600, 350, 100)) && PixelAt(withOv, 600, 100, 239)[2] == 255 && PixelAt(withOv, 600, 100, 240).SequenceEqual(PixelAt(px, 600, 100, 240)));
                var bg = PixelAt(px, 600, 450, 200); var bl = PixelAt(withOv, 600, 450, 200);
                Check("overlays: a half-transparent overlay tile is blended with what is under it (blue 255 at alpha 128 over the base: " + string.Join(",", bg) + " -> " + string.Join(",", bl) + "); the cut at 100 x 100 holds",
                    Math.Abs(bl[0] - (255 * 128 + bg[0] * 127) / 255.0) <= 1.5 && Math.Abs(bl[1] - bg[1] * 127 / 255.0) <= 1.5 && Math.Abs(bl[2] - bg[2] * 127 / 255.0) <= 1.5 && bl[3] == 255 && PixelAt(withOv, 600, 500, 200).SequenceEqual(PixelAt(px, 600, 500, 200)) && PixelAt(withOv, 600, 450, 250).SequenceEqual(PixelAt(px, 600, 450, 250)));
                Check("overlays: an overlay that lies outside the picture draws nothing and does not crash", !PixelAt(withOv, 600, 599, 299).SequenceEqual(new byte[] { 9, 9, 9, 255 }));
                var ovHoles = MapRender.Compose(ovArt, f => f == 7002 ? null : OvTile(f), out _, out _, out int baseMiss2, out int ovMiss2);
                Check("overlays: an overlay tile the game does not have is skipped and counted apart from the base tiles; the rest is drawn", baseMiss2 == 0 && ovMiss2 == 1 && PixelAt(ovHoles, 600, 60, 50)[2] == 255 && PixelAt(ovHoles, 600, 320, 100).SequenceEqual(PixelAt(px, 600, 320, 100)));
                var ovPng = MapRender.RenderPng(ovArt, OvTile, out _, out _, out _, out _);
                var basePng = MapRender.RenderPng(art, OvTile, out _, out _, out _);
                Check("overlays: the PNG with overlays differs from the base-only one; art without overlays composes exactly as before", ovPng != null && basePng != null && !ovPng.SequenceEqual(basePng) && PixelAt(MapRender.Compose(art, OvTile, out _, out _, out _), 600, 60, 50).SequenceEqual(PixelAt(px, 600, 60, 50)));

                // ---- the overlay tables: layouts verified, nothing guessed
                {
                    var ovTables = OverlayTables(100);
                    var ovSet = SynthMapSet();
                    MapTables.AddOverlays(ovSet, RawWith(Raw("WorldMapOverlay", ovTables.Item1), Raw("WorldMapOverlayTile", ovTables.Item2)));
                    var a100 = ovSet.Arts.First(a => a.Id == 100);
                    Check("overlay tables: read in the layouts of build 70338: art 100 gets 2 overlays (an overlay of an unknown art is dropped) with size, offset and tiles in row / column order",
                        ovSet.HasOverlays && ovSet.OverlayProblem == null && a100.Overlays.Length == 2 && a100.Overlays[0].Id == 5001 && a100.Overlays[0].Width == 300 && a100.Overlays[0].Height == 200 && a100.Overlays[0].OffsetX == 50 && a100.Overlays[0].OffsetY == -40
                        && a100.Overlays[0].Tiles.Select(t => t.FileDataId).SequenceEqual(new[] { 7001, 7002 }) && a100.Overlays[0].Tiles[1].Col == 1 && a100.Overlays[1].Id == 5002 && a100.Overlays[1].Tiles.Length == 1 && ovSet.Arts.Where(a => a.Id != 100).All(a => a.Overlays.Length == 0),
                        string.Join(";", ovSet.Notes) + " " + a100.Overlays.Length);
                    var unkSet = SynthMapSet();
                    MapTables.AddOverlays(unkSet, RawWith(Raw("WorldMapOverlay", OverlayTables(100, overlayLayout: 0xDEADBEEF).Item1), Raw("WorldMapOverlayTile", ovTables.Item2)));
                    Check("overlay tables: an overlay layout this Outpost does not know = no overlays at all, the base maps stay, and the reason (game updated, the hash) is kept and noted",
                        !unkSet.HasOverlays && unkSet.OverlayProblem != null && unkSet.OverlayProblem.Contains("game updated") && unkSet.OverlayProblem.Contains("DEADBEEF") && unkSet.Arts.All(a => a.Overlays.Length == 0) && unkSet.Notes.Any(n => n.StartsWith("map overlays:")) && unkSet.Ok, unkSet.OverlayProblem);
                    var unk2 = SynthMapSet();
                    MapTables.AddOverlays(unk2, RawWith(Raw("WorldMapOverlay", ovTables.Item1), Raw("WorldMapOverlayTile", OverlayTables(100, tileLayout: 0x0BADF00D).Item2)));
                    var gone = SynthMapSet();
                    MapTables.AddOverlays(gone, RawWith());
                    var brokenSet = SynthMapSet();
                    MapTables.AddOverlays(brokenSet, RawWith(Raw("WorldMapOverlay", new byte[] { 1, 2, 3 }), Raw("WorldMapOverlayTile", ovTables.Item2)));
                    Check("overlay tables: an unknown tile layout, missing tables or a broken file all fall back to the base maps with a reason (never an exception)",
                        unk2.OverlayProblem != null && unk2.OverlayProblem.Contains("0BADF00D") && gone.OverlayProblem != null && gone.OverlayProblem.Contains("not found") && brokenSet.OverlayProblem != null && brokenSet.Ok && unk2.Arts.All(a => a.Overlays.Length == 0) && gone.Arts.All(a => a.Overlays.Length == 0),
                        unk2.OverlayProblem + " / " + gone.OverlayProblem + " / " + brokenSet.OverlayProblem);
                    var noArt = new MapSet();
                    MapTables.AddOverlays(noArt, RawWith(Raw("WorldMapOverlay", ovTables.Item1), Raw("WorldMapOverlayTile", ovTables.Item2)));
                    Check("overlay tables: without the base art nothing is read", !noArt.HasOverlays && noArt.OverlayProblem == null && noArt.Notes.Count == 0);
                    var ovIdxPath = Path.Combine(work, "idx", "mapdata-ov.bin");
                    MapIndexFile.TryWrite(ovIdxPath, "wow_classic_beta|1.60.1.70338|abc", ovSet);
                    var ovBack = MapIndexFile.TryRead(ovIdxPath);
                    var b100 = ovBack?.Set.Arts.FirstOrDefault(a => a.Id == 100);
                    Check("index file: the overlays (and the reason when there are none) survive the saved copy; the format version is 5 (approximate entrances, instance entrances and dungeon levels, flight tables, map WDT ids) so a copy from before is read again from the game files",
                        b100 != null && b100.Overlays.Length == 2 && b100.Overlays[0].OffsetY == -40 && b100.Overlays[0].Tiles.Length == 2 && b100.Overlays[0].Tiles[1].FileDataId == 7002 && ovBack.Set.HasOverlays && MapData.FormatVersion == 5);
                    MapIndexFile.TryWrite(ovIdxPath, "wow_classic_beta|1.60.1.70338|abc", unkSet);
                    Check("index file: ... the 'no overlays' reason too", MapIndexFile.TryRead(ovIdxPath)?.Set.OverlayProblem == unkSet.OverlayProblem);
                }

                // ---- the saved index
                var idxPath = Path.Combine(work, "idx", "mapdata.bin");
                Check("index file: written, no .tmp left", MapIndexFile.TryWrite(idxPath, "wow_classic_beta|1.60.1.70338|abc", set) && File.Exists(idxPath) && !File.Exists(idxPath + ".tmp"));
                var saved = MapIndexFile.TryRead(idxPath);
                var back3 = saved == null ? null : new MapIndex(saved.Set);
                Check("index file round trip: build, every table's rows (maps, art with layers and tiles, links, assignments with all 15 numbers, instances, floors, areas), flags and the same answers from the lookups",
                    saved != null && saved.Build == "wow_classic_beta|1.60.1.70338|abc" && saved.Set.UiMaps.Count == set.UiMaps.Count && saved.Set.Arts.Count == set.Arts.Count && saved.Set.Arts.Sum(a => a.Tiles.Length) == set.Arts.Sum(a => a.Tiles.Length)
                    && saved.Set.Assignments.Zip(set.Assignments, (a, b) => a.Id == b.Id && a.MinX == b.MinX && a.MaxY == b.MaxY && a.UiMaxY == b.UiMaxY && a.MapId == b.MapId && a.AreaId == b.AreaId).All(x => x) && saved.Set.Maps.Count == set.Maps.Count
                    && saved.Set.Floors.Count == 3 && saved.Set.Areas.Count == 2 && saved.Set.HasArt && saved.Set.HasFloors && back3.WorldToMap(0, 500, 1000).Value.UiMapId == 11 && back3.Instance(99).Floors.Count == 3 && back3.MapsWithArt == idx.MapsWithArt);
                var sz = new FileInfo(idxPath).Length;
                Check("index file is compact (zipped, a few KB for this world)", sz < 6000, sz + " bytes");
                var raw = File.ReadAllBytes(idxPath);
                var corrupt = Path.Combine(work, "idx", "corrupt.bin"); File.WriteAllBytes(corrupt, raw.Take(raw.Length / 2).ToArray());
                var junk = Path.Combine(work, "idx", "junk.bin"); File.WriteAllText(junk, "this is not an index");
                Check("index file: a cut, a foreign or a missing file reads as null (the game files are read instead), no exception", MapIndexFile.TryRead(corrupt) == null && MapIndexFile.TryRead(junk) == null && MapIndexFile.TryRead(Path.Combine(work, "idx", "nope.bin")) == null);
                int gameReads = 0;
                Func<MapSet> game = () => { gameReads++; return SynthMapSet(); };
                var obt = Path.Combine(work, "obt", "mapdata.bin");
                var o1 = MapData.Obtain(MapIndexFile.TryRead(obt), "B1", obt, game, out var f1);
                Check("first start (nothing saved): the game files are read once and the index is saved", gameReads == 1 && f1 == "game files" && o1.Ok && File.Exists(obt));
                var o2 = MapData.Obtain(MapIndexFile.TryRead(obt), "B1", obt, game, out var f2);
                Check("second start, same build: from the saved index, the game files are NOT read", gameReads == 1 && f2 == "cache file" && o2.UiMaps.Count == set.UiMaps.Count, f2);
                var o3 = MapData.Obtain(MapIndexFile.TryRead(obt), "B2", obt, game, out var f3);
                Check("the game was patched (other build key): the index is not trusted, the game files are read again and saved", gameReads == 2 && f3 == "game files" && MapIndexFile.TryRead(obt).Build == "B2", f3);
                File.WriteAllBytes(obt, new byte[] { 1, 2, 3 });
                MapData.Obtain(MapIndexFile.TryRead(obt), "B2", obt, game, out var f4);
                Check("a damaged index file: the game files are read again and the file is replaced", gameReads == 3 && f4 == "game files" && MapIndexFile.TryRead(obt) != null);
                int unkReads = 0;
                var o5 = MapData.Obtain(null, "B3", Path.Combine(work, "obt", "unk.bin"), () => { unkReads++; return MapTables.Parse(RawWith(Raw("UiMap", UiMapTable(layout: 0xDEADBEEF)))); }, out _);
                Check("a game that was updated (unknown layout): unavailable, nothing saved", !o5.Ok && !File.Exists(Path.Combine(work, "obt", "unk.bin")) && unkReads == 1);

                // ---- the build stamp and the render cache
                Check("build stamp: product|version|key -> '1.60.1.70338-2417084b' (safe for a file name); nothing -> 'unknown'", MapData.Stamp("wow_classic_beta|1.60.1.70338|2417084b8b041ee120d02d4adf31b0bc") == "1.60.1.70338-2417084b" && MapData.Stamp(null) == "unknown" && MapData.Stamp("a|b/c|") == "b_c");
                MapData.SetForTest(idx, "wow_classic_beta|1.60.1.70338|0123456789abcdef");
                Check("states: ready -> 'Maps: 6 maps, 4 instances' (placeholders not counted), and the Settings suffix", MapData.Ready && MapData.StatusLine == "Maps: 6 maps, 4 instances" && MapData.StatusSuffix == "; 6 maps, 4 instances" && MapData.BuildStamp == "1.60.1.70338-01234567", MapData.StatusLine);
                MapData.SetForTest(null, null, MapData.State.Unavailable, "game updated: UiMap layout DEADBEEF is not known to this Outpost yet");
                Check("states: unavailable names the reason, no index, not ready", MapData.StatusLine.StartsWith("Maps: unavailable (game updated") && MapData.StatusSuffix.StartsWith("; maps unavailable") && !MapData.Ready && MapData.Index == null);
                MapData.SetForTest(null, null, MapData.State.NotStarted);
                Check("states: not read yet says so, adds nothing to the Settings line", MapData.StatusLine == "Maps: not read yet" && MapData.StatusSuffix == "");
                MapData.Enabled = () => false;
                MapData.Start();
                Check("switched off: Start() reads nothing and says 'off'", MapData.Status == MapData.State.Disabled && MapData.StatusLine == "Maps: off" && MapData.Index == null);
                MapData.Enabled = () => true;

                MapData.SetForTest(idx, "wow_classic_beta|1.60.1.70338|0123456789abcdef");
                MapRender.TileSourceForTest = fdid => Solid(256, 256, (byte)(fdid % 200), 90, 30);
                int renders0 = MapRender.RenderCountForTest;
                Directory.CreateDirectory(MapRender.Dir);
                var oldFogged = Path.Combine(MapRender.Dir, "1.60.1.70338-01234567_11.png");      // the name before render version 2: a picture without the explored-area overlays
                File.WriteAllBytes(oldFogged, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 });
                Check("render cache: a picture made before the render version (the old fogged name) is not used", MapRender.TryGet(11) == null);
                var gotPath = await RequestPath(11);
                var expectPath = Path.Combine(work, "maps", "1.60.1.70338-01234567_v" + MapRender.RenderVersion + "_11.png");
                var img = gotPath == null ? null : MapRender.Load(gotPath) as BitmapSource;
                Check("render cache: Greenvale is made in the background into the maps folder as <build>_<uiMapId>.png, 600 x 300", gotPath == expectPath && File.Exists(expectPath) && img != null && img.PixelWidth == 600 && img.PixelHeight == 300 && MapRender.RenderCountForTest == renders0 + 1, gotPath);
                var again = await RequestPath(11);
                Check("render cache: the old fogged picture is removed and replaced by one with the render version in its name (made again, not reused)", !File.Exists(oldFogged) && gotPath != null && Path.GetFileName(gotPath).Contains("_v" + MapRender.RenderVersion + "_"), gotPath);
                Check("render cache: asking again is served from the folder (nothing is composed twice)", again == expectPath && MapRender.RenderCountForTest == renders0 + 1 && MapRender.TryGet(11) == expectPath);
                Check("render cache: a map with no art, art without tiles and an unknown map answer null at once", await RequestPath(50) == null && await RequestPath(12) == null && await RequestPath(9999) == null && MapRender.RenderCountForTest == renders0 + 1);
                Directory.CreateDirectory(MapRender.Dir);
                File.WriteAllBytes(Path.Combine(MapRender.Dir, "1.60.1.69999-deadbeef_11.png"), new byte[10]);
                var cont = await RequestPath(10);
                Check("render cache: pictures of other game builds are removed when a new one is made; this build's stay", cont != null && !File.Exists(Path.Combine(MapRender.Dir, "1.60.1.69999-deadbeef_11.png")) && File.Exists(expectPath));
                MapData.SetForTest(idx, "wow_classic_beta|1.60.1.70400|ffffffffffffffff");
                Check("render cache: after a patch (other build stamp) the old picture is not used", MapRender.TryGet(11) == null);
                MapData.SetForTest(idx, "wow_classic_beta|1.60.1.70338|0123456789abcdef");

                // never on the UI thread, and the UI keeps going while a big continent is composed
                var bigSet = SynthMapSet(); bigSet.Arts.Add(SynthArt(300, 3840, 2560, 256, 10000)); bigSet.UiMaps.Add(new UiMapInfo { Id = 60, Name = "Big", Type = 2, Flags = 0 }); bigSet.ArtLinks.Add(new MapArtLink { UiMapId = 60, ArtId = 300 });
                MapData.SetForTest(new MapIndex(bigSet), "wow_classic_beta|1.60.1.70338|0123456789abcdef");
                int uiThread = Thread.CurrentThread.ManagedThreadId, workerThread = -1;
                var shared = Solid(256, 256, 40, 80, 120);
                MapRender.TileSourceForTest = fdid => { workerThread = Thread.CurrentThread.ManagedThreadId; Thread.Sleep(4); return shared; };
                long mem0 = GC.GetTotalMemory(true);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var bigTask = RequestPath(60);
                int ticks = 0;
                while (!bigTask.IsCompleted) { await Task.Delay(15); ticks++; }
                var bigPath = await bigTask;
                long bigMs = sw.ElapsedMilliseconds;
                long mem1 = GC.GetTotalMemory(true) - mem0;
                var bigImg = bigPath == null ? null : MapRender.Load(bigPath) as BitmapSource;
                Check("render: a 3840 x 2560 continent (150 tiles) is composed on a worker thread while the UI thread keeps running (ticks of 15 ms: " + ticks + ")", bigImg != null && bigImg.PixelWidth == 3840 && bigImg.PixelHeight == 2560 && workerThread != uiThread && workerThread != -1 && ticks >= 8, "ticks " + ticks + " " + bigMs + " ms");
                bigImg = null;
                long mem2 = GC.GetTotalMemory(true) - mem0;
                Check("render: the 39 MB bitmap is not kept (managed memory after: " + (mem2 / 1024) + " KB)", mem2 < 8 * 1024 * 1024, mem2 / 1024 + " KB");
                notes.Add("note render: synthetic 3840 x 2560 (150 tiles of 256 x 256, 4 ms each): " + bigMs + " ms, PNG " + new FileInfo(bigPath).Length / 1024 + " KB, composed bitmap " + MapRender.LastPeakBytes / 1024 / 1024 + " MB (held only while composing), managed memory +" + mem1 / 1024 + " KB while the image was loaded, " + mem2 / 1024 + " KB after");
                MapRender.TileSourceForTest = f => null;
                MapData.SetForTest(idx, "wow_classic_beta|1.60.1.70338|0123456789abcdef");
                File.Delete(expectPath);
                var failedPath = await RequestPath(11);
                MapRender.TileSourceForTest = fdid => Solid(256, 256, 1, 2, 3);
                var failedAgain = await RequestPath(11);
                Check("render: tiles that cannot be read = null (no picture), and a failed map is not retried for a while (then null at once)", failedPath == null && failedAgain == null && !File.Exists(expectPath));
                MapRender.ForgetFailuresForTest();
                Check("render: after the pause the next request tries again and succeeds", await RequestPath(11) == expectPath);

                // ---- the control + the real game files
                await MapControlChecks(dir, work, idx, Check, notes, P);
                // the real game files run in their own section: "mapdatareal" (MainWindow.MapDataTest.Real.cs)
            }
            catch (Exception ex) { ok = false; notes.Add("FAIL exception: " + ex); }
            finally
            {
                MapData.ResetForTest();
                MapData.CacheFile = savedCache; MapData.WowRoot = savedRoot; MapData.Enabled = savedEnabled; MapRender.DirOverride = savedDir; MapRender.TileSourceForTest = savedTiles;
                MapRender.ForgetFailuresForTest();
            }
            notes.Insert(0, "mapdata (Atlas layer B): " + (ok ? "all ok" : "FAILURES"));
            return string.Join("\r\n", notes);
        }

        static bool TryDelete(string f) { try { File.Delete(f); return !File.Exists(f); } catch { return false; } }

        // MapRender.Request as a task (the callback arrives on the worker thread)
        static Task<string> RequestPath(int uiMapId)
        {
            var tcs = new TaskCompletionSource<string>();
            MapRender.Request(uiMapId, p => tcs.TrySetResult(p));
            return Task.WhenAny(tcs.Task, Task.Delay(30000)).ContinueWith(t => tcs.Task.IsCompleted ? tcs.Task.Result : "TIMEOUT");
        }
    }
}
