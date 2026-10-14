using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using ElansAddonHub.Services.Maps;

namespace ElansAddonHub.Services.WorldMap
{
    // Where the world map pictures live: <data>\atlas\worldmap\<build key>\<mapId>\z{level}\{x}_{y}.jpg  (+ manifest.json), one folder per game build.
    // <data>\atlas\worldmap\_page holds the viewer's own files (the page, Leaflet), made from the app's resources. Nothing here is ever game art in the
    // repository, and nothing is written outside the Outpost's data folder.
    public static class WorldMapCache
    {
        public const string PageFolder = "_page";
        public static string RootOverride;                      // self-test: a scratch folder
        public static string Root => RootOverride ?? Path.Combine(Util.DataDir, "atlas", "worldmap");

        // "1.60.1.70338-2417084b_p1": the game build and the pyramid version; a new game build (or a new picture format) is a new folder
        public static string KeyFor(string buildStamp) => (string.IsNullOrEmpty(buildStamp) ? "unknown" : buildStamp) + "_p" + WorldMapPyramid.Version;
        public static string BuildKey => KeyFor(MapData.BuildStamp);
        public static string MapDir(string key, int mapId) => Path.Combine(Root, key, mapId.ToString(CultureInfo.InvariantCulture));
        public static string MapDir(int mapId) => MapDir(BuildKey, mapId);

        // the finished pyramid of this build, or null
        public static WorldMapPyramid.Manifest Built(int mapId, string key = null)
        {
            var m = WorldMapPyramid.ReadManifest(MapDir(key ?? BuildKey, mapId));
            return m != null && m.Valid && m.MapId == mapId ? m : null;
        }

        public static string TerrainDir(string key, int mapId) => WorldMapTerrain.Dir(MapDir(key, mapId));
        // the finished height grids + borders of this build, or null
        public static WorldMapTerrain.Manifest TerrainBuilt(int mapId, string key = null)
        {
            var m = WorldMapTerrain.ReadManifest(TerrainDir(key ?? BuildKey, mapId));
            return m != null && m.Valid && m.MapId == mapId ? m : null;
        }

        // folders of other game builds are of no use any more
        public static int RemoveOtherBuilds(string key)
        {
            int n = 0;
            try
            {
                if (!Directory.Exists(Root)) return 0;
                foreach (var d in Directory.GetDirectories(Root))
                {
                    var name = Path.GetFileName(d);
                    if (string.Equals(name, PageFolder, StringComparison.OrdinalIgnoreCase) || string.Equals(name, key, StringComparison.Ordinal)) continue;
                    try { Util.DeleteDir(d); n++; } catch (Exception) { }
                }
            }
            catch (Exception) { }
            return n;
        }

        // bytes of the pictures (the viewer's own files are not counted)
        public static long SizeBytes()
        {
            long total = 0;
            try
            {
                if (!Directory.Exists(Root)) return 0;
                foreach (var d in Directory.GetDirectories(Root))
                {
                    if (string.Equals(Path.GetFileName(d), PageFolder, StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (var f in Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories)) { try { total += new FileInfo(f).Length; } catch (Exception) { } }
                }
            }
            catch (Exception) { }
            return total;
        }

        // "Clear world map cache": every picture goes (the viewer's files stay). Returns the bytes freed.
        public static long Clear()
        {
            long before = SizeBytes();
            try
            {
                if (Directory.Exists(Root))
                    foreach (var d in Directory.GetDirectories(Root))
                    {
                        if (string.Equals(Path.GetFileName(d), PageFolder, StringComparison.OrdinalIgnoreCase)) continue;
                        try { Util.DeleteDir(d); } catch (Exception e) { Util.Log("world map cache: " + e.Message); }
                    }
            }
            catch (Exception) { }
            return before - SizeBytes();
        }

        public static string SizeText(long bytes)
        {
            if (bytes <= 0) return "empty";
            if (bytes < 1024 * 1024) return Math.Max(1, bytes / 1024).ToString("N0", CultureInfo.InvariantCulture) + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / 1048576.0).ToString("N0", CultureInfo.InvariantCulture) + " MB";
            return (bytes / 1073741824.0).ToString("0.0", CultureInfo.InvariantCulture) + " GB";
        }

        // "World map pictures: 2 continents, 212 MB" for Settings
        public static string StatusLine()
        {
            long b = SizeBytes();
            int maps = 0, tmaps = 0;
            try
            {
                var key = BuildKey; var dir = Path.Combine(Root, key);
                if (Directory.Exists(dir)) { maps = Directory.GetDirectories(dir).Count(d => File.Exists(WorldMapPyramid.ManifestPath(d))); tmaps = Directory.GetDirectories(dir).Count(d => File.Exists(WorldMapTerrain.ManifestPath(WorldMapTerrain.Dir(d)))); }
            }
            catch (Exception) { }
            if (b <= 0) return "World map pictures: none made yet (they are made from your game files the first time you open a continent; the 3D terrain is read the first time you use the 3D view or the zone borders).";
            return "World map pictures: " + maps + (maps == 1 ? " continent, " : " continents, ") + SizeText(b) + (tmaps > 0 ? " (with the terrain of " + tmaps + (tmaps == 1 ? " continent" : " continents") + " for the 3D view and the zone borders)" : "") + ".";
        }
    }
}
