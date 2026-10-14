using System;
using System.Collections.Generic;

namespace ElansAddonHub.Services.Maps
{
    // What the client's map tables hold, as plain rows. MapSet is the raw result of reading the tables (and what the saved index stores);
    // MapIndex (MapIndex.cs) builds the lookups on top of it.

    // UiMap.db2: one map of the world map UI (a zone, a continent, the world, a battleground ...)
    public sealed class UiMapInfo
    {
        public int Id;
        public string Name = "";
        public int ParentId;        // UiMap id of the map above (a zone's continent); 0 = none
        public int Flags;
        public int System;          // 0 = the normal world map UI, 1 = an alternative (flight map style) copy of a map
        public int Type;            // UiMapType: 0 cosmic, 1 world, 2 continent, 3 zone, 4 dungeon, 5 micro, 6 orphan (battlegrounds, islands)
        public const int Cosmic = 0, World = 1, Continent = 2, Zone = 3, Dungeon = 4, Micro = 5, Orphan = 6;

        public string TypeName
        {
            get
            {
                switch (Type)
                {
                    case Cosmic: return "Cosmic";
                    case World: return "World";
                    case Continent: return "Continent";
                    case Zone: return "Zone";
                    case Dungeon: return "Dungeon";
                    case Micro: return "Micro";
                    case Orphan: return "Orphan";
                    default: return "Type " + Type;
                }
            }
        }
    }

    // one layer of a map's art: the size of the finished picture and of the tiles it is cut into
    public sealed class ArtLayerInfo
    {
        public int Index, Width, Height, TileWidth, TileHeight;
        public int Cols => TileWidth <= 0 ? 0 : (Width + TileWidth - 1) / TileWidth;
        public int Rows => TileHeight <= 0 ? 0 : (Height + TileHeight - 1) / TileHeight;
    }

    // one BLP picture of a map's art (FileDataID) and where it goes in the grid
    public sealed class ArtTile { public int Row, Col, Layer, FileDataId; }

    // WorldMapOverlay + its WorldMapOverlayTile rows: one explored-area picture (TextureWidth x TextureHeight, cut into 256 px tiles) that the game draws
    // on top of the base art at (OffsetX, OffsetY) of the art layer
    public sealed class ArtOverlay
    {
        public int Id, Width, Height, OffsetX, OffsetY;
        public ArtTile[] Tiles = new ArtTile[0];
    }

    // UiMapArt + its style layers + its tiles
    public sealed class MapArtInfo
    {
        public int Id, StyleId;
        public ArtLayerInfo[] Layers = new ArtLayerInfo[0];
        public ArtTile[] Tiles = new ArtTile[0];
        public ArtOverlay[] Overlays = new ArtOverlay[0];

        public ArtLayerInfo BaseLayer
        {
            get
            {
                ArtLayerInfo best = null;
                foreach (var l in Layers) if (l.Width > 0 && l.Height > 0 && (best == null || l.Index < best.Index)) best = l;
                return best;
            }
        }
    }

    // UiMapXMapArt: which art a map shows (phase 0 = always)
    public sealed class MapArtLink { public int UiMapId, ArtId, Phase; }

    // UiMapAssignment: the rectangle of the world (map id, x/y) that one UiMap shows, and which part of the UiMap it fills
    public sealed class UiAssignment
    {
        public int Id, UiMapId, MapId, AreaId, Order;
        public float UiMinX, UiMinY, UiMaxX, UiMaxY;          // the part of the UiMap (0..1 each way) this world rectangle fills
        public float MinX, MinY, MinZ, MaxX, MaxY, MaxZ;       // the world rectangle (x = north, y = west, as the game uses them)
        public double Area => Math.Abs((double)(MaxX - MinX) * (MaxY - MinY));
    }

    // Map.db2: the continents and every instance
    public sealed class GameMapInfo
    {
        public int Id;
        public string Directory = "", Name = "";
        public int InstanceType;    // 0 none (open world), 1 dungeon, 2 raid, 3 battleground, 4 arena, 5 scenario
        public int MapType, Expansion, MaxPlayers, AreaTableId;
    }

    // UiMapGroupMember: the floors of a multi-floor map
    public sealed class FloorMember { public int GroupId, UiMapId, FloorIndex, RelativeHeight; public string Name = ""; }

    public sealed class AreaInfo { public int Id, ParentId, ContinentId; public string Name = ""; }

    // the result of reading the tables, before the lookups are built
    public sealed class MapSet
    {
        public List<UiMapInfo> UiMaps = new List<UiMapInfo>();
        public List<MapArtInfo> Arts = new List<MapArtInfo>();
        public List<MapArtLink> ArtLinks = new List<MapArtLink>();
        public List<UiAssignment> Assignments = new List<UiAssignment>();
        public List<GameMapInfo> Maps = new List<GameMapInfo>();
        public List<FloorMember> Floors = new List<FloorMember>();
        public List<AreaInfo> Areas = new List<AreaInfo>();
        // which tables were read (a table whose layout this version does not know is simply not there)
        public bool HasArt, HasAssignments, HasMaps, HasFloors, HasAreas, HasOverlays;
        public string OverlayProblem;                  // why the maps have no overlays (unknown layout ...); null = fine
        public int LockedMapRows;                      // Map rows that sit in key-locked parts of the file
        public string Problem;                         // set = unavailable (nothing else is valid)
        public List<string> Notes = new List<string>();   // partial results
        public bool Ok => Problem == null;
    }

    // where a world position lands on a UiMap (fx, fy = 0..1 from the left / top of the picture)
    public struct MapPoint
    {
        public int UiMapId;
        public double Fx, Fy;
        public MapPoint(int uiMapId, double fx, double fy) { UiMapId = uiMapId; Fx = fx; Fy = fy; }
        public override string ToString() => "ui " + UiMapId + " (" + Fx.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) + ", " + Fy.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) + ")";
    }

    // a position in the world: the map id (0 Eastern Kingdoms, 1 Kalimdor, an instance id ...) and x / y
    public struct WorldPoint
    {
        public int MapId;
        public double X, Y;
        public WorldPoint(int mapId, double x, double y) { MapId = mapId; X = x; Y = y; }
        public override string ToString() => "map " + MapId + " (" + X.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + ", " + Y.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + ")";
    }

    // a dungeon, raid, battleground or arena with the maps that show it (empty when the game has no art for it)
    public sealed class InstanceInfo
    {
        public int MapId;
        public string Name = "", Kind = "";
        public int InstanceType, MaxPlayers;
        public bool Placeholder;                       // a test or unused leftover ("<unused>...", "Scott Test")
        public List<UiMapInfo> Floors = new List<UiMapInfo>();   // bottom to top
        public bool HasMap => Floors.Count > 0;
    }
}
