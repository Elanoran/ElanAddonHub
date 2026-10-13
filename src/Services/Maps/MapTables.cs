using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ElansAddonHub.Services.Db2;

namespace ElansAddonHub.Services.Maps
{
    // Where each field we use sits in the map tables of ONE client build. Like ItemTables: keyed by the table's layout hash (the game writes the hash
    // into the file), so a patch that changes a table shows up as an unknown hash and that table is simply not read - nothing is guessed.
    //
    // Source of every number: wowdev/WoWDBDefs definitions/UiMap.dbd, UiMapArt, UiMapArtTile, UiMapArtStyleLayer, UiMapXMapArt, UiMapAssignment,
    // UiMapGroupMember, Map and AreaTable, the LAYOUT blocks that list build 1.60.1.70338 (WoW Forever). Field numbers count the STORED columns in
    // the order of the .dbd (a "$noninline,id$" column is not stored; a "$noninline,relation$" column is not stored either: it comes from the
    // relationship section, Wdc5.ParentOf). FileDataIDs from the community listfile (dbfilesclient/*.db2). Checked against the real files of that
    // build (60 UiMaps with Durotar 1411 / Kalimdor 1414 / Azeroth 947, 61 assignments, 144 art sets with 1,672 tiles, 71 Map rows, 1,373 areas).
    public static class MapTables
    {
        public sealed class Def
        {
            public string Name;
            public int FileDataId;
            public uint TableHash;
            public uint[] Layouts;                     // the layout hashes the field numbers below are written for
            public bool Knows(uint layout) => Layouts.Contains(layout);
        }

        public static readonly Def UiMap = new Def { Name = "UiMap", FileDataId = 1957206, TableHash = 0x89927BF7, Layouts = new uint[] { 0xDB51D55F } };
        public static readonly Def UiMapArt = new Def { Name = "UiMapArt", FileDataId = 1957202, TableHash = 0x05089A9C, Layouts = new uint[] { 0x3AE7D144 } };
        public static readonly Def UiMapArtTile = new Def { Name = "UiMapArtTile", FileDataId = 1957210, TableHash = 0x9C7EC549, Layouts = new uint[] { 0x2DA5B77B } };
        public static readonly Def UiMapArtStyleLayer = new Def { Name = "UiMapArtStyleLayer", FileDataId = 1957208, TableHash = 0x382DB23A, Layouts = new uint[] { 0x3F597F5A } };
        public static readonly Def UiMapXMapArt = new Def { Name = "UiMapXMapArt", FileDataId = 1957217, TableHash = 0xADB3FC8C, Layouts = new uint[] { 0xEAE7DA2A } };
        public static readonly Def UiMapAssignment = new Def { Name = "UiMapAssignment", FileDataId = 1957219, TableHash = 0x5153A338, Layouts = new uint[] { 0xC9CC8DFB } };
        public static readonly Def UiMapGroupMember = new Def { Name = "UiMapGroupMember", FileDataId = 1957204, TableHash = 0x84EEB993, Layouts = new uint[] { 0x0FF75B01 } };
        public static readonly Def Map = new Def { Name = "Map", FileDataId = 1349477, TableHash = 0xBD84CD62, Layouts = new uint[] { 0xD43AFAC3 } };
        public static readonly Def AreaTable = new Def { Name = "AreaTable", FileDataId = 1353545, TableHash = 0x7253EB43, Layouts = new uint[] { 0x9995B797 } };

        public static readonly Def[] All = { UiMap, UiMapArt, UiMapArtTile, UiMapArtStyleLayer, UiMapXMapArt, UiMapAssignment, UiMapGroupMember, Map, AreaTable };

        // field numbers (stored columns)
        // UiMap: Name_lang 0, ID 1 (stored inline), ParentUiMapID 2, Flags 3, System 4, Type 5
        public const int UiMapName = 0, UiMapParent = 2, UiMapFlags = 3, UiMapSystem = 4, UiMapType = 5;
        // UiMapArt: HighlightFileDataID 0, HighlightAtlasID 1, UiMapArtStyleID 2 (ID in the id list)
        public const int ArtStyle = 2;
        // UiMapArtTile: RowIndex 0, ColIndex 1, LayerIndex 2, FileDataID 3; UiMapArtID = relationship section
        public const int TileRow = 0, TileCol = 1, TileLayer = 2, TileFileData = 3;
        // UiMapArtStyleLayer: LayerIndex 0, LayerWidth 1, LayerHeight 2, TileWidth 3, TileHeight 4, MinScale 5, MaxScale 6, AdditionalZoomSteps 7; UiMapArtStyleID = relationship section
        public const int LayerIndex = 0, LayerWidth = 1, LayerHeight = 2, LayerTileWidth = 3, LayerTileHeight = 4;
        // UiMapXMapArt: PhaseID 0, UiMapArtID 1; UiMapID = relationship section
        public const int XPhase = 0, XArt = 1;
        // UiMapAssignment: UiMin[2] 0, UiMax[2] 1, Region[6] 2 (minX minY minZ maxX maxY maxZ), ID 3 (inline), UiMapID 4, OrderIndex 5, MapID 6, AreaID 7
        public const int AsUiMin = 0, AsUiMax = 1, AsRegion = 2, AsUiMap = 4, AsOrder = 5, AsMap = 6, AsArea = 7;
        // UiMapGroupMember: Name_lang 0, UiMapGroupID 1, UiMapID 2, FloorIndex 3, RelativeHeightIndex 4
        public const int GmName = 0, GmGroup = 1, GmUiMap = 2, GmFloor = 3, GmHeight = 4;
        // Map: Directory 0, MapName_lang 1, ... MapType 7, InstanceType 8, ExpansionID 9, AreaTableID 10, ... MaxPlayers 18
        public const int MapDirectory = 0, MapName = 1, MapMapType = 7, MapInstanceType = 8, MapExpansion = 9, MapAreaTable = 10, MapMaxPlayers = 18;
        // AreaTable: ZoneName 0, AreaName_lang 1, ContinentID 2, ParentAreaID 3
        public const int AreaZoneName = 0, AreaName = 1, AreaContinent = 2, AreaParent = 3;

        static float Flt(Wdc5 w, int i, int field, int element, int count) => BitConverter.ToSingle(BitConverter.GetBytes((int)w.Element(i, field, element, count)), 0);

        // The raw files -> rows. A table that is missing, not WDC5, another table or in a layout this version does not know is left out (with a note).
        // UiMap is the one the others hang on: without it everything is unavailable.
        public static MapSet Parse(IDictionary<string, byte[]> raw)
        {
            var set = new MapSet();
            Wdc5 Open(Def d, bool required)
            {
                if (!raw.TryGetValue(d.Name, out var bytes) || bytes == null)
                {
                    if (required) set.Problem = d.Name + ".db2 is not in this game install"; else set.Notes.Add(d.Name + ".db2 not found");
                    return null;
                }
                Wdc5 w;
                try { w = Wdc5.Parse(bytes); }
                catch (Exception e)
                {
                    if (required) set.Problem = d.Name + ".db2 could not be read: " + e.Message; else set.Notes.Add(d.Name + ".db2: " + e.Message);
                    return null;
                }
                if (w.TableHash != d.TableHash || w.Sparse)
                {
                    if (required) set.Problem = "the " + d.Name + " table is not the one this version expects"; else set.Notes.Add(d.Name + " is not the table this version expects");
                    return null;
                }
                if (!d.Knows(w.LayoutHash))
                {
                    var why = "game updated: " + d.Name + " layout " + w.LayoutHash.ToString("X8", CultureInfo.InvariantCulture) + " is not known to this Outpost yet";
                    if (required) set.Problem = why; else set.Notes.Add(why);
                    return null;
                }
                return w;
            }

            // ---- UiMap (required)
            var um = Open(UiMap, true);
            if (um == null) return set;
            try
            {
                for (int i = 0; i < um.Count; i++)
                    set.UiMaps.Add(new UiMapInfo
                    {
                        Id = um.IdAt(i), Name = um.String(i, UiMapName), ParentId = (int)um.Field(i, UiMapParent), Flags = (int)um.Field(i, UiMapFlags),
                        System = (int)um.Field(i, UiMapSystem), Type = (int)um.Field(i, UiMapType),
                    });
            }
            catch (Exception e) { set.Problem = "UiMap.db2: " + e.Message; set.UiMaps.Clear(); return set; }
            if (um.EncryptedRecords > 0) set.Notes.Add(um.EncryptedRecords + " UiMap rows are key-locked in the game files and skipped");

            // ---- the art: UiMapXMapArt + UiMapArt + UiMapArtStyleLayer + UiMapArtTile (all four or no art)
            var xw = Open(UiMapXMapArt, false); var aw = Open(UiMapArt, false); var lw = Open(UiMapArtStyleLayer, false); var tw = Open(UiMapArtTile, false);
            if (xw != null && aw != null && lw != null && tw != null)
            {
                try
                {
                    for (int i = 0; i < xw.Count; i++)
                        set.ArtLinks.Add(new MapArtLink { UiMapId = xw.ParentOf(i), ArtId = (int)xw.Field(i, XArt), Phase = (int)xw.Field(i, XPhase) });
                    var layers = new Dictionary<int, List<ArtLayerInfo>>();
                    for (int i = 0; i < lw.Count; i++)
                    {
                        int style = lw.ParentOf(i);
                        if (!layers.TryGetValue(style, out var l)) layers[style] = l = new List<ArtLayerInfo>();
                        l.Add(new ArtLayerInfo
                        {
                            Index = (int)lw.Field(i, LayerIndex), Width = (int)lw.Field(i, LayerWidth), Height = (int)lw.Field(i, LayerHeight),
                            TileWidth = (int)lw.Field(i, LayerTileWidth), TileHeight = (int)lw.Field(i, LayerTileHeight),
                        });
                    }
                    var tiles = new Dictionary<int, List<ArtTile>>();
                    int related = 0;
                    for (int i = 0; i < tw.Count; i++)
                    {
                        int art = tw.ParentOf(i);
                        if (art != 0) related++;
                        if (!tiles.TryGetValue(art, out var l)) tiles[art] = l = new List<ArtTile>();
                        l.Add(new ArtTile { Row = (int)tw.Field(i, TileRow), Col = (int)tw.Field(i, TileCol), Layer = (int)tw.Field(i, TileLayer), FileDataId = (int)tw.Field(i, TileFileData) });
                    }
                    for (int i = 0; i < aw.Count; i++)
                    {
                        int id = aw.IdAt(i), style = (int)aw.Field(i, ArtStyle);
                        set.Arts.Add(new MapArtInfo
                        {
                            Id = id, StyleId = style,
                            Layers = layers.TryGetValue(style, out var ll) ? ll.OrderBy(x => x.Index).ToArray() : new ArtLayerInfo[0],
                            Tiles = tiles.TryGetValue(id, out var tl) ? tl.ToArray() : new ArtTile[0],
                        });
                    }
                    set.HasArt = related > 0 || tw.Count == 0;
                    if (!set.HasArt) { set.Notes.Add("UiMapArtTile has no relationship data: no map art"); set.Arts.Clear(); set.ArtLinks.Clear(); }
                }
                catch (Exception e) { set.Notes.Add("map art tables: " + e.Message); set.Arts.Clear(); set.ArtLinks.Clear(); set.HasArt = false; }
            }
            else set.Notes.Add("no map art (a table is missing or its layout is not known)");

            // ---- UiMapAssignment: world rectangle <-> map
            var sw = Open(UiMapAssignment, false);
            if (sw != null)
            {
                try
                {
                    for (int i = 0; i < sw.Count; i++)
                    {
                        var a = new UiAssignment
                        {
                            Id = sw.IdAt(i), UiMapId = (int)sw.Field(i, AsUiMap), Order = (int)sw.Field(i, AsOrder), MapId = (int)sw.Field(i, AsMap), AreaId = (int)sw.Field(i, AsArea),
                            UiMinX = Flt(sw, i, AsUiMin, 0, 2), UiMinY = Flt(sw, i, AsUiMin, 1, 2), UiMaxX = Flt(sw, i, AsUiMax, 0, 2), UiMaxY = Flt(sw, i, AsUiMax, 1, 2),
                            MinX = Flt(sw, i, AsRegion, 0, 6), MinY = Flt(sw, i, AsRegion, 1, 6), MinZ = Flt(sw, i, AsRegion, 2, 6),
                            MaxX = Flt(sw, i, AsRegion, 3, 6), MaxY = Flt(sw, i, AsRegion, 4, 6), MaxZ = Flt(sw, i, AsRegion, 5, 6),
                        };
                        if (a.MinX > a.MaxX) { var t = a.MinX; a.MinX = a.MaxX; a.MaxX = t; }
                        if (a.MinY > a.MaxY) { var t = a.MinY; a.MinY = a.MaxY; a.MaxY = t; }
                        if (a.UiMaxX <= a.UiMinX || a.UiMaxY <= a.UiMinY || float.IsNaN(a.MinX + a.MaxX + a.MinY + a.MaxY)) continue;     // not a usable rectangle
                        set.Assignments.Add(a);
                    }
                    set.HasAssignments = true;
                }
                catch (Exception e) { set.Notes.Add("UiMapAssignment.db2: " + e.Message + ": no world positions"); set.Assignments.Clear(); }
            }

            // ---- Map: instances (part of the file can be key-locked)
            var mw = Open(Map, false);
            if (mw != null)
            {
                try
                {
                    for (int i = 0; i < mw.Count; i++)
                        set.Maps.Add(new GameMapInfo
                        {
                            Id = mw.IdAt(i), Directory = mw.String(i, MapDirectory), Name = mw.String(i, MapName), MapType = (int)mw.Field(i, MapMapType),
                            InstanceType = (int)mw.Field(i, MapInstanceType), Expansion = (int)mw.Field(i, MapExpansion), AreaTableId = (int)mw.Field(i, MapAreaTable),
                            MaxPlayers = (int)mw.Field(i, MapMaxPlayers),
                        });
                    set.HasMaps = true;
                    set.LockedMapRows = mw.EncryptedRecords;
                    if (mw.EncryptedRecords > 0) set.Notes.Add(mw.EncryptedRecords + " Map rows are key-locked in the game files and skipped");
                }
                catch (Exception e) { set.Notes.Add("Map.db2: " + e.Message + ": no instance list"); set.Maps.Clear(); }
            }

            // ---- UiMapGroupMember: floors (an empty table is fine: this client may simply have none)
            var gw = Open(UiMapGroupMember, false);
            if (gw != null)
            {
                try
                {
                    for (int i = 0; i < gw.Count; i++)
                        set.Floors.Add(new FloorMember
                        {
                            GroupId = (int)gw.Field(i, GmGroup), UiMapId = (int)gw.Field(i, GmUiMap), FloorIndex = (int)gw.Field(i, GmFloor),
                            RelativeHeight = (int)gw.Field(i, GmHeight), Name = gw.String(i, GmName),
                        });
                    set.HasFloors = true;
                }
                catch (Exception e) { set.Notes.Add("UiMapGroupMember.db2: " + e.Message + ": no floors"); set.Floors.Clear(); }
            }

            // ---- AreaTable: names of zones and areas
            var rw = Open(AreaTable, false);
            if (rw != null)
            {
                try
                {
                    for (int i = 0; i < rw.Count; i++)
                        set.Areas.Add(new AreaInfo { Id = rw.IdAt(i), Name = rw.String(i, AreaName), ParentId = (int)rw.Field(i, AreaParent), ContinentId = (int)rw.Field(i, AreaContinent) });
                    set.HasAreas = true;
                    if (rw.EncryptedRecords > 0) set.Notes.Add(rw.EncryptedRecords + " AreaTable rows are key-locked in the game files and skipped");
                }
                catch (Exception e) { set.Notes.Add("AreaTable.db2: " + e.Message + ": no area names"); set.Areas.Clear(); }
            }
            return set;
        }
    }
}
