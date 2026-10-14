using System;

namespace ElansAddonHub.Services.WorldMap
{
    // ============================================================================================================================
    // The coordinates of the world map, in one place, free of any control, so the 2D page (Leaflet) and a later 3D terrain view use the same numbers.
    //
    // The world is a 64 x 64 grid of ADT tiles ("map tiles"), each 533.3333 yards (1600 / 3) square, the grid centred on the world origin. The game's
    // world coordinates are x = NORTH and y = WEST (see MapIndex). A tile is named by (col, row): col runs west -> east, row runs north -> south,
    //     col = 32 - y / 533.3333       row = 32 - x / 533.3333       (fractions; the tile is the floor of each)
    // so the origin sits at the corner where tiles (31,31) (32,31) (31,32) (32,32) meet. The WDT holds the tile table row by row (index = row * 64 + col;
    // checked on the real Eastern Kingdoms / Kalimdor tables: the Kingdoms' land is 20 columns wide and 43 rows tall, as it should be).
    //
    // The picture: each tile's minimap BLP is 512 x 512 pixels (about 1.04 yards per pixel). The tile pyramid cuts the 64 x 64 grid into 256-pixel
    // tiles: level 7 (the native level) is 128 x 128 tiles = 2 x 2 per ADT, every level below halves it, level 0 is one tile for the whole grid.
    //
    // The viewer's plane (Leaflet CRS.Simple): lng = col * 4, lat = -row * 4 ("map units", 4 per ADT tile, so the whole grid is 256 units and a
    // 256-pixel tile of level z is 256 / 2^z units wide: the standard slippy-map arrangement). A 3D terrain view reads (col,row) or world x/y directly.
    // ============================================================================================================================
    public static class WorldMapMath
    {
        public const double TileYards = 1600.0 / 3.0;      // 533.3333: one ADT tile
        public const int Grid = 64;                        // tiles per side
        public const int Center = 32;                      // the origin is at tile 32 (its north-west corner)
        public const int MinimapPx = 512;                  // a minimap texture
        public const int TilePx = 256;                     // a pyramid tile
        public const int NativeLevel = 7;                  // 2^7 = 128 tiles per side = 2 per ADT
        public const double UnitsPerAdt = 4.0;             // viewer plane units per ADT tile

        public static int TilesPerAxis(int level) => 1 << level;
        // how many ADT tiles one pyramid tile of the level spans per side (0.5 at the native level)
        public static double AdtPerTile(int level) => Grid / (double)TilesPerAxis(level);

        // ---- world <-> tile grid
        public static void WorldToGrid(double x, double y, out double col, out double row)
        {
            col = Center - y / TileYards;
            row = Center - x / TileYards;
        }

        public static void GridToWorld(double col, double row, out double x, out double y)
        {
            x = (Center - row) * TileYards;
            y = (Center - col) * TileYards;
        }

        // the tile that holds a world position (not range-checked: use InGrid)
        public static void TileOf(double x, double y, out int col, out int row)
        {
            WorldToGrid(x, y, out var c, out var r);
            col = (int)Math.Floor(c); row = (int)Math.Floor(r);
        }

        public static bool InGrid(int col, int row) => col >= 0 && col < Grid && row >= 0 && row < Grid;
        public static bool InWorld(double x, double y)
        {
            WorldToGrid(x, y, out var c, out var r);
            return !double.IsNaN(c + r) && c >= 0 && c < Grid && r >= 0 && r < Grid;
        }

        // ---- world <-> viewer plane
        public static void WorldToLatLng(double x, double y, out double lat, out double lng)
        {
            WorldToGrid(x, y, out var c, out var r);
            lng = c * UnitsPerAdt; lat = -r * UnitsPerAdt;
        }

        public static void LatLngToWorld(double lat, double lng, out double x, out double y)
            => GridToWorld(lng / UnitsPerAdt, -lat / UnitsPerAdt, out x, out y);

        // ---- the pyramid
        // the pyramid tiles of a level that an ADT tile touches: at the native level 2 x 2, above it one (shared with its neighbours)
        public static void PyramidRangeOfAdt(int level, int col, int row, out int tx0, out int ty0, out int tx1, out int ty1)
        {
            double s = AdtPerTile(level);
            tx0 = (int)Math.Floor(col / s); ty0 = (int)Math.Floor(row / s);
            tx1 = (int)Math.Floor((col + 1 - 1e-9) / s); ty1 = (int)Math.Floor((row + 1 - 1e-9) / s);
        }

        public static string TilePath(int level, int tx, int ty) => "z" + level + "/" + tx + "_" + ty + ".jpg";
    }
}
