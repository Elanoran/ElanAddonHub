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

        // ---- "made again after a game update" (2.82.1): before the folders of an older game build go, the maps and terrains that were made there are
        // noted in the new build's folder (older-maps.txt: "123" = the map pictures, "t123" = the 3D terrain), so the build card can say the game was
        // updated instead of "the first time"
        const string OlderFile = "older-maps.txt";

        static IEnumerable<string> MadeIn(string otherKey)
        {
            var res = new List<string>();
            try
            {
                foreach (var md in Directory.GetDirectories(Path.Combine(Root, otherKey)))
                {
                    if (!int.TryParse(Path.GetFileName(md), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) continue;
                    if (Built(id, otherKey) != null) res.Add(id.ToString(CultureInfo.InvariantCulture));
                    if (TerrainBuilt(id, otherKey) != null) res.Add("t" + id.ToString(CultureInfo.InvariantCulture));
                }
            }
            catch (Exception) { }
            return res;
        }

        static IEnumerable<string> OlderMaps(string key)
        {
            try { var p = Path.Combine(Root, key, OlderFile); return File.Exists(p) ? File.ReadAllLines(p).Select(l => l.Trim()).Where(l => l.Length > 0).ToList() : new List<string>(); }
            catch (Exception) { return new List<string>(); }
        }

        static void WriteOlderMaps(string key, IEnumerable<string> maps)
        {
            try { var dir = Path.Combine(Root, key); Directory.CreateDirectory(dir); File.WriteAllLines(Path.Combine(dir, OlderFile), maps.OrderBy(x => x, StringComparer.Ordinal)); }
            catch (Exception) { }
        }

        // this map (or its 3D terrain) was made for an older game build: it is being made again because the game was updated, not for the first time
        public static bool MadeForOlderGame(int mapId, bool terrain, string key = null)
        {
            key = key ?? BuildKey;
            var id = (terrain ? "t" : "") + mapId.ToString(CultureInfo.InvariantCulture);
            if (OlderMaps(key).Contains(id)) return true;
            try
            {
                if (!Directory.Exists(Root)) return false;
                foreach (var d in Directory.GetDirectories(Root))
                {
                    var name = Path.GetFileName(d);
                    if (string.Equals(name, PageFolder, StringComparison.OrdinalIgnoreCase) || string.Equals(name, key, StringComparison.Ordinal)) continue;
                    if (MadeIn(name).Contains(id)) return true;
                }
            }
            catch (Exception) { }
            return false;
        }

        // folders of other game builds are of no use any more
        public static int RemoveOtherBuilds(string key)
        {
            int n = 0;
            try
            {
                if (!Directory.Exists(Root)) return 0;
                var older = new HashSet<string>(OlderMaps(key));
                foreach (var d in Directory.GetDirectories(Root))
                {
                    var name = Path.GetFileName(d);
                    if (string.Equals(name, PageFolder, StringComparison.OrdinalIgnoreCase) || string.Equals(name, key, StringComparison.Ordinal)) continue;
                    foreach (var m in MadeIn(name)) older.Add(m);
                    try { Util.DeleteDir(d); n++; } catch (Exception) { }
                }
                if (older.Count > 0) WriteOlderMaps(key, older);
            }
            catch (Exception) { }
            return n;
        }

        // the 3D buildings (<map folder>\buildings) are counted and removed on their own (Settings > General > "3D buildings"), not with the pictures and terrain
        static bool InBuildings(string path) => path.IndexOf(System.IO.Path.DirectorySeparatorChar + WorldMapBuildings.Folder + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0;

        // a build folder without the buildings of its maps: everything else goes; the folder itself goes when nothing is left
        static void DeleteExceptBuildings(string buildDir)
        {
            foreach (var f in Directory.GetFiles(buildDir)) { try { File.Delete(f); } catch (Exception) { } }
            foreach (var m in Directory.GetDirectories(buildDir))
            {
                foreach (var f in Directory.GetFiles(m)) { try { File.Delete(f); } catch (Exception) { } }
                foreach (var s in Directory.GetDirectories(m)) { if (string.Equals(Path.GetFileName(s), WorldMapBuildings.Folder, StringComparison.OrdinalIgnoreCase)) continue; try { Util.DeleteDir(s); } catch (Exception) { } }
                try { if (Directory.GetFileSystemEntries(m).Length == 0) Directory.Delete(m); } catch (Exception) { }
            }
            try { if (Directory.GetFileSystemEntries(buildDir).Length == 0) Directory.Delete(buildDir); } catch (Exception) { }
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
                    foreach (var f in Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories)) { if (InBuildings(f) || string.Equals(Path.GetFileName(f), OlderFile, StringComparison.OrdinalIgnoreCase)) continue; try { total += new FileInfo(f).Length; } catch (Exception) { } }
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
                        try { DeleteExceptBuildings(d); } catch (Exception e) { Util.Log("world map cache: " + e.Message); }
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
