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

        // WorldMapOverlay / WorldMapOverlayTile (WoWDBDefs, layout blocks listing 1.60.1.70338; table hash, layout hash, row counts verified on the real files of that build)
        public static readonly Def WorldMapOverlay = new Def { Name = "WorldMapOverlay", FileDataId = 1134579, TableHash = 0x1D740E72, Layouts = new uint[] { 0x9A196494 } };
        public static readonly Def WorldMapOverlayTile = new Def { Name = "WorldMapOverlayTile", FileDataId = 1957212, TableHash = 0xAC599725, Layouts = new uint[] { 0x2C843422 } };

        // TaxiNodes / TaxiPath / TaxiPathNode (flight masters and their routes; FileDataIDs from the community listfile, layout blocks of 1.60.1.70338 in WoWDBDefs;
        // table and layout hashes and row counts - 100 nodes, 328 paths, 10,778 path points - verified on the real files of that build)
        public static readonly Def TaxiNodes = new Def { Name = "TaxiNodes", FileDataId = 1068100, TableHash = 0x50D91A78, Layouts = new uint[] { 0xE7B597F0 } };
        public static readonly Def TaxiPath = new Def { Name = "TaxiPath", FileDataId = 1067802, TableHash = 0xAB30A45C, Layouts = new uint[] { 0xA303DE51 } };
        public static readonly Def TaxiPathNode = new Def { Name = "TaxiPathNode", FileDataId = 1000437, TableHash = 0xE5E988BC, Layouts = new uint[] { 0xFE362E70 } };

        // LiquidType (the liquid kinds of the world map's water: MH2O type id -> water / ocean / magma / slime). Kept out of `All` on purpose: it is read on its own while the
        // terrain is built, not part of the map data cache. Table hash, layout hash and the 57 rows verified on the real file of 1.60.1.70338 (FileDataID from the community listfile).
        public static readonly Def LiquidType = new Def { Name = "LiquidType", FileDataId = 1371380, TableHash = 0x6613BED3, Layouts = new uint[] { 0xD1ECEEC9 } };
        // LiquidType: Name_lang 0, Texture[6] 1, Flags 2, SoundBank 3 (0 water, 1 ocean, 2 magma, 3 slime) ...
        public const int LiquidName = 0, LiquidSoundBank = 3;

        // LFGDungeons / ContentTuning (the level the game suggests for a dungeon or raid; FileDataIDs from the community listfile, layout blocks of 1.60.1.70338 in WoWDBDefs;
        // table hashes, layout hashes and row counts - 71 LFG rows, 100 tuning rows - verified on the real files of that build)
        public static readonly Def LfgDungeons = new Def { Name = "LFGDungeons", FileDataId = 1361033, TableHash = 0x999BB9C2, Layouts = new uint[] { 0x34B02DE8 } };
        public static readonly Def ContentTuning = new Def { Name = "ContentTuning", FileDataId = 1962930, TableHash = 0xBD3635FF, Layouts = new uint[] { 0xA3E13004 } };

        public static readonly Def[] All = { UiMap, UiMapArt, UiMapArtTile, UiMapArtStyleLayer, UiMapXMapArt, UiMapAssignment, UiMapGroupMember, Map, AreaTable, WorldMapOverlay, WorldMapOverlayTile, TaxiNodes, TaxiPath, TaxiPathNode, LfgDungeons, ContentTuning };

        // field numbers (stored columns)
        // UiMap: Name_lang 0, ID 1 (stored inline), ParentUiMapID 2, Flags 3, System 4, Type 5
        public const int UiMapName = 0, UiMapParent = 2, UiMapFlags = 3, UiMapSystem = 4, UiMapType = 5;
        // UiMapArt: HighlightFileDataID 0, HighlightAtlasID 1, UiMapArtStyleID 2 (ID in the id list)
        public const int ArtStyle = 2;
        // UiMapArtTile: RowIndex 0, ColIndex 1, LayerIndex 2, FileDataID 3; UiMapArtID = relationship section
        public const int TileRow = 0, TileCol = 1, TileLayer = 2, TileFileData = 3;
        // UiMapArtStyleLayer: LayerIndex 0, LayerWidth 1, LayerHeight 2, TileWidth 3, TileHeight 4, MinScale 5, MaxScale 6, AdditionalZoomSteps 7; UiMapArtStyleID = relationship section
        public const int LayerIndex = 0, LayerWidth = 1, LayerHeight = 2, LayerTileWidth = 3, LayerTileHeight = 4;
        // WorldMapOverlay: ID 0 (stored inline), UiMapArtID 1, TextureWidth 2, TextureHeight 3, OffsetX 4, OffsetY 5, HitRect* 6-9, PlayerConditionID 10, Flags 11, AreaID[4] 12
        public const int OvArt = 1, OvWidth = 2, OvHeight = 3, OvOffsetX = 4, OvOffsetY = 5;
        // WorldMapOverlayTile: RowIndex 0, ColIndex 1, LayerIndex 2, FileDataID 3; WorldMapOverlayID = relationship section
        public const int OtRow = 0, OtCol = 1, OtLayer = 2, OtFileData = 3;
        // UiMapXMapArt: PhaseID 0, UiMapArtID 1; UiMapID = relationship section
        public const int XPhase = 0, XArt = 1;
        // UiMapAssignment: UiMin[2] 0, UiMax[2] 1, Region[6] 2 (minX minY minZ maxX maxY maxZ), ID 3 (inline), UiMapID 4, OrderIndex 5, MapID 6, AreaID 7
        public const int AsUiMin = 0, AsUiMax = 1, AsRegion = 2, AsUiMap = 4, AsOrder = 5, AsMap = 6, AsArea = 7;
        // UiMapGroupMember: Name_lang 0, UiMapGroupID 1, UiMapID 2, FloorIndex 3, RelativeHeightIndex 4
        public const int GmName = 0, GmGroup = 1, GmUiMap = 2, GmFloor = 3, GmHeight = 4;
        // Map: Directory 0, MapName_lang 1, ... MapType 7, InstanceType 8, ExpansionID 9, AreaTableID 10, ... MaxPlayers 18
        public const int MapDirectory = 0, MapName = 1, MapMapType = 7, MapInstanceType = 8, MapExpansion = 9, MapAreaTable = 10, MapMaxPlayers = 18;
        // (layout D43AFAC3 in order: Directory, MapName_lang, MapDescription0_lang, MapDescription1_lang, PvpShortDescription_lang, PvpLongDescription_lang, Corpse[2],
        // MapType 7, InstanceType 8, ExpansionID 9, AreaTableID 10, LoadingScreenID, TimeOfDayOverride, ParentMapID 13, CosmeticParentMapID, TimeOffset, MinimapIconScale, CorpseMapID,
        // MaxPlayers 18, WindSettingsID, ZmpFileDataID 20, WdtFileDataID 21, OceanLiquidTypeID, NavigationMaxDistance, PreloadFileDataID, Flags[3]);
        // WdtFileDataID reads 775971 for Azeroth (Eastern Kingdoms) and 782779 for Kalimdor on the real file
        public const int MapWdtFileData = 21;
        // Corpse[2] is stored column 6 (a float pair: world x north, y west of the spot on map CorpseMapID, field 17 = where a ghost of that instance is put: the entrance);
        // verified on the real file: Deadmines (-11208, 1681) in Westfall, Wailing Caverns (-751, -2209) in the Barrens, Ragefire Chasm (1816, -4423) in Orgrimmar
        public const int MapCorpse = 6, MapCorpseMap = 17;
        // LFGDungeons (layout 34B02DE8, the ID is not stored): Name_lang 0, Description_lang 1, TypeID 2 ... ContentTuningID 31. ContentTuning (A3E13004): ID 0 (inline), Flags 1,
        // ExpansionID 2 ... MinLevelSquish 9, MaxLevelSquish 10 (the level the game suggests: 17 for Wailing Caverns, 13 for Ragefire Chasm, 60 for Molten Core)
        public const int LfgName = 0, LfgType = 2, LfgTuning = 31, CtMinLevel = 9, CtMaxLevel = 10;
        // TaxiNodes (layout E7B597F0): Name_lang 0, Pos[3] 1, MapOffset[2] 2, FlightMapOffset[2] 3, ID 4 (inline), ContinentID 5 (inline relation), ConditionID 6, CharacterBitNumber 7,
        // Flags 8, UiTextureKitID 9, MinimapAtlasMemberID 10, Facing 11, SpecialIconConditionID 12, VisibilityConditionID 13, MountCreatureID[2] 14 (Horde, Alliance)
        public const int TnName = 0, TnPos = 1, TnContinent = 5, TnFlags = 8, TnMount = 14;
        // TaxiPath (A303DE51): ID 0, FromTaxiNode 1 (inline relation), ToTaxiNode 2, Cost 3
        public const int TpFrom = 1, TpTo = 2, TpCost = 3;
        // TaxiPathNode (FE362E70): Loc[3] 0, ID 1, PathID 2 (inline relation), NodeIndex 3, ContinentID 4, Flags 5, Delay 6, ArrivalEventID 7, DepartureEventID 8
        public const int TpnLoc = 0, TpnPath = 2, TpnIndex = 3, TpnContinent = 4;
        // AreaTable: ZoneName 0, AreaName_lang 1, ContinentID 2, ParentAreaID 3
        public const int AreaZoneName = 0, AreaName = 1, AreaContinent = 2, AreaParent = 3;

        static float Flt(Wdc5 w, int i, int field, int element, int count) => BitConverter.ToSingle(BitConverter.GetBytes((int)w.Element(i, field, element, count)), 0);

        static Wdc5 OpenTable(MapSet set, IDictionary<string, byte[]> raw, Def d, bool required)
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

        // The explored-area overlays that sit on top of the base art: WorldMapOverlay + WorldMapOverlayTile, both in a layout this version knows or none
        // (the maps are then drawn as the base art alone and set.OverlayProblem says why). Needs set.Arts (read before); never throws.
        public static void AddOverlays(MapSet set, IDictionary<string, byte[]> raw)
        {
            int notes0 = set.Notes.Count;
            if (set.HasArt)
            {
                var ow = OpenTable(set, raw, WorldMapOverlay, false); var ot = OpenTable(set, raw, WorldMapOverlayTile, false);
                if (ow != null && ot != null)
                {
                    try
                    {
                        var byOverlay = new Dictionary<int, List<ArtTile>>();
                        for (int i = 0; i < ot.Count; i++)
                        {
                            int ov = ot.ParentOf(i);
                            if (!byOverlay.TryGetValue(ov, out var l)) byOverlay[ov] = l = new List<ArtTile>();
                            l.Add(new ArtTile { Row = (int)ot.Field(i, OtRow), Col = (int)ot.Field(i, OtCol), Layer = (int)ot.Field(i, OtLayer), FileDataId = (int)ot.Field(i, OtFileData) });
                        }
                        var artById = set.Arts.ToDictionary(a => a.Id);
                        var perArt = new Dictionary<int, List<ArtOverlay>>();
                        int n = 0;
                        for (int i = 0; i < ow.Count; i++)
                        {
                            int id = ow.IdAt(i), art = (int)ow.Field(i, OvArt);
                            if (!artById.ContainsKey(art) || !byOverlay.TryGetValue(id, out var tl)) continue;
                            if (!perArt.TryGetValue(art, out var l)) perArt[art] = l = new List<ArtOverlay>();
                            l.Add(new ArtOverlay { Id = id, Width = (int)ow.Field(i, OvWidth), Height = (int)ow.Field(i, OvHeight), OffsetX = (int)ow.Field(i, OvOffsetX), OffsetY = (int)ow.Field(i, OvOffsetY), Tiles = tl.OrderBy(t => t.Layer).ThenBy(t => t.Row).ThenBy(t => t.Col).ToArray() });
                            n++;
                        }
                        foreach (var kv in perArt) artById[kv.Key].Overlays = kv.Value.OrderBy(o => o.Id).ToArray();
                        set.HasOverlays = n > 0;
                        if (n == 0) set.OverlayProblem = "the game has no explored-area overlays for these maps";
                    }
                    catch (Exception e) { set.OverlayProblem = "WorldMapOverlay tables: " + e.Message; foreach (var a in set.Arts) a.Overlays = new ArtOverlay[0]; set.HasOverlays = false; }
                }
                else set.OverlayProblem = set.Notes.Count > notes0 ? set.Notes[set.Notes.Count - 1] : "the overlay tables are not available";
                if (set.OverlayProblem != null) set.Notes.Add("map overlays: " + set.OverlayProblem + ": maps are drawn without the explored-area layer");
            }
        }

        // The raw files -> rows. A table that is missing, not WDC5, another table or in a layout this version does not know is left out (with a note).
        // UiMap is the one the others hang on: without it everything is unavailable.
        public static MapSet Parse(IDictionary<string, byte[]> raw)
        {
            var set = new MapSet();
            Wdc5 Open(Def d, bool required) => OpenTable(set, raw, d, required);

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

            AddOverlays(set, raw);

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
                            MaxPlayers = (int)mw.Field(i, MapMaxPlayers), WdtFileDataId = (int)mw.Field(i, MapWdtFileData),
                        });
                    set.HasMaps = true;
                    try
                    {
                        // where a ghost of the instance is put (the entrance): a float pair and the continent it is on
                        for (int i = 0; i < mw.Count; i++)
                        {
                            var g = set.Maps[i];
                            g.CorpseMapId = (int)mw.Field(i, MapCorpseMap);
                            g.CorpseX = Flt(mw, i, MapCorpse, 0, 2); g.CorpseY = Flt(mw, i, MapCorpse, 1, 2);
                        }
                    }
                    catch (Exception e) { set.Notes.Add("Map.db2 corpse positions: " + e.Message + ": no instance entrances"); foreach (var g in set.Maps) { g.CorpseMapId = -1; g.CorpseX = g.CorpseY = 0; } }
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
            AddTaxi(set, raw);
            AddLfg(set, raw);
            return set;
        }


        // The level the game suggests for each dungeon and raid: LFGDungeons (name, tuning id) + ContentTuning (MinLevelSquish / MaxLevelSquish), both in a known layout or neither
        // (set.HasLfg false, with a note). Left alone silently when the raw set does not name the tables (a test with its own tables).
        public static void AddLfg(MapSet set, IDictionary<string, byte[]> raw)
        {
            if (!raw.ContainsKey(LfgDungeons.Name) && !raw.ContainsKey(ContentTuning.Name)) return;
            var lw = OpenTable(set, raw, LfgDungeons, false); var cw = OpenTable(set, raw, ContentTuning, false);
            if (lw == null || cw == null) { set.Notes.Add("no dungeon levels (a table is missing or its layout is not known)"); return; }
            try
            {
                var tuning = new Dictionary<int, int[]>();
                for (int i = 0; i < cw.Count; i++) tuning[cw.IdAt(i)] = new[] { (int)cw.Field(i, CtMinLevel), (int)cw.Field(i, CtMaxLevel) };
                var list = new List<LfgDungeonInfo>();
                for (int i = 0; i < lw.Count; i++)
                {
                    var d = new LfgDungeonInfo { Id = lw.IdAt(i), Name = lw.String(i, LfgName), TypeId = (int)lw.Field(i, LfgType), ContentTuningId = (int)lw.Field(i, LfgTuning) };
                    if (tuning.TryGetValue(d.ContentTuningId, out var lv) && lv[0] > 0 && lv[0] <= 255 && lv[1] >= lv[0] && lv[1] <= 255) { d.MinLevel = lv[0]; d.MaxLevel = lv[1]; }
                    list.Add(d);
                }
                set.Lfg.AddRange(list);
                set.HasLfg = list.Count > 0;
            }
            catch (Exception e) { set.Notes.Add("dungeon level tables: " + e.Message); set.Lfg.Clear(); set.HasLfg = false; }
        }
        // Flight masters and routes: all three tables in a known layout or none (set.HasTaxi false, with a note). A raw set that does not mention the tables at
        // all (a test with its own tables) is left alone silently; the game's real file list always names them (null = not in this install).
        public static void AddTaxi(MapSet set, IDictionary<string, byte[]> raw)
        {
            if (!raw.ContainsKey(TaxiNodes.Name) && !raw.ContainsKey(TaxiPath.Name) && !raw.ContainsKey(TaxiPathNode.Name)) return;
            var nw = OpenTable(set, raw, TaxiNodes, false); var pw = OpenTable(set, raw, TaxiPath, false); var qw = OpenTable(set, raw, TaxiPathNode, false);
            if (nw == null || pw == null || qw == null) { set.Notes.Add("no flight masters or routes (a table is missing or its layout is not known)"); return; }
            try
            {
                var nodes = new List<TaxiNodeInfo>(); var paths = new List<TaxiPathInfo>(); var points = new List<TaxiPoint>();
                for (int i = 0; i < nw.Count; i++)
                    nodes.Add(new TaxiNodeInfo
                    {
                        Id = nw.IdAt(i), Name = nw.String(i, TnName), MapId = (int)nw.Field(i, TnContinent), Flags = (int)nw.Field(i, TnFlags),
                        X = Flt(nw, i, TnPos, 0, 3), Y = Flt(nw, i, TnPos, 1, 3), Z = Flt(nw, i, TnPos, 2, 3),
                        MountHorde = (int)nw.Element(i, TnMount, 0, 2), MountAlliance = (int)nw.Element(i, TnMount, 1, 2),
                    });
                for (int i = 0; i < pw.Count; i++)
                    paths.Add(new TaxiPathInfo { Id = pw.IdAt(i), FromNode = (int)pw.Field(i, TpFrom), ToNode = (int)pw.Field(i, TpTo), Cost = (int)pw.Field(i, TpCost) });
                for (int i = 0; i < qw.Count; i++)
                    points.Add(new TaxiPoint
                    {
                        PathId = (int)qw.Field(i, TpnPath), Index = (int)qw.Field(i, TpnIndex), MapId = (int)qw.Field(i, TpnContinent),
                        X = Flt(qw, i, TpnLoc, 0, 3), Y = Flt(qw, i, TpnLoc, 1, 3), Z = Flt(qw, i, TpnLoc, 2, 3),
                    });
                if (nw.EncryptedRecords + pw.EncryptedRecords + qw.EncryptedRecords > 0) set.Notes.Add("some flight rows are key-locked in the game files and skipped");
                set.TaxiNodes.AddRange(nodes); set.TaxiPaths.AddRange(paths); set.TaxiPoints.AddRange(points);
                set.HasTaxi = nodes.Count > 0;
            }
            catch (Exception e) { set.Notes.Add("flight tables: " + e.Message); set.TaxiNodes.Clear(); set.TaxiPaths.Clear(); set.TaxiPoints.Clear(); set.HasTaxi = false; }
        }
    }
}
