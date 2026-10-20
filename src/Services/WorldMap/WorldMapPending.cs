using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace ElansAddonHub.Services.WorldMap
{
    // ============================================================================================================================
    // Map tiles whose own game files could not be read yet (2.70). A Battle.net install that is still downloading in the background (the game is
    // playable while it streams), or a game that is updating, has some files listed but not on disk yet ("missing in local storage") or a file it
    // cannot give whole ("read error"). Before 2.70 such a tile was left out, drawn as open sea (near black) and the build was cached as final, so the
    // holes stayed forever. Now:
    //   * the tile gets a stand-in that is clearly not real land: a picture tile from the continent's own Atlas map art under a light hatch (or a
    //     plain hatch), and a terrain tile at low detail from the continent's WDL (or a flat hatched placeholder in the 3D view)
    //   * a small marker file next to it ("<x>_<y>.low": why, and what was drawn instead) says it is not final: every build reads that tile
    //     again, and a tile that reads now replaces the stand-in (and the zoom levels above it are made again)
    //   * the world map retries by itself on the next open (once per app start, and whenever the game's storage index changed), and has a Retry
    // Nothing here is shared between Lodge members: the pictures are made from each player's own game files.
    // ============================================================================================================================
    public sealed class PendingTile
    {
        public int Col, Row;
        public string Part = "";          // "picture" (the minimap) | "terrain" (the root ADT)
        // why (3.1.1: finer, for "Copy details"): "unlisted" (not in the game's file list at all) | "missing" (listed, but no copy of it is on this disk)
        // | "locked" (on disk but key-locked / encrypted) | "unreadable" (on disk, a read error: damaged or cut off)
        public string Reason = "";
        public string Detail = "";        // the read error, short
        public string Shown = "";         // what is drawn instead: "art" (continent map art) | "plain" (hatch) | "low" (WDL heights) | "flat" (hatched placeholder)

        public string ReasonText => WorldMapPending.ReasonText(Reason, Detail);
        public string ShownText => Shown == "art" ? "continent map art" : Shown == "low" ? "low-detail heights (WDL)" : Shown == "flat" ? "flat placeholder" : "hatched placeholder";

        public Dictionary<string, object> ToJson() => new Dictionary<string, object> { ["c"] = Col, ["r"] = Row, ["part"] = Part, ["why"] = Reason, ["detail"] = Detail, ["shown"] = Shown };

        public static PendingTile FromJson(Dictionary<string, object> d)
        {
            if (d == null) return null;
            return new PendingTile { Col = d.Int("c"), Row = d.Int("r"), Part = d.Str("part") ?? "", Reason = d.Str("why") ?? "", Detail = d.Str("detail") ?? "", Shown = d.Str("shown") ?? "" };
        }
    }

    // a file a world map loader asked for and did not get, with why: "unlisted" or "missing" (PendingTile.Reason). Any other exception of a loader is a read error.
    public sealed class FileNotThereException : IOException
    {
        public readonly string Reason;
        public FileNotThereException(string reason, string what) : base(what + (reason == "unlisted" ? " is not in the game's file list" : " is listed but not on disk")) { Reason = reason; }
    }

    public static class WorldMapPending
    {
        public const string MarkerExt = ".low";

        // the reason in words (the same for every kind of file: a minimap tile, a terrain tile, a building, a texture)
        public static string ReasonText(string reason, string detail)
        {
            string d = string.IsNullOrEmpty(detail) ? "" : " (" + detail + ")";
            switch (reason)
            {
                case "unlisted": return "not in the game's file list";
                case "missing": return "listed but not on disk";
                case "locked": return "on disk but key-locked (encrypted)" + d;
                case "unreadable": return "on disk but could not be read" + d;
                default: return "not readable" + d;
            }
        }

        // the reason of a loader's exception: a key-locked BLTE block (CascStorage: NotSupportedException "encrypted or unknown") or another read error
        public static string ReasonOf(Exception e) => e is FileNotThereException f ? f.Reason : e is NotSupportedException ? "locked" : "unreadable";

        // "27 listed but not on disk, 2 not in the game's file list": a count per reason, the most common first
        public static string Summary(IEnumerable<string> reasons)
        {
            var g = reasons.GroupBy(r => r ?? "").OrderByDescending(x => x.Count()).ThenBy(x => x.Key, StringComparer.Ordinal);
            return string.Join(", ", g.Select(x => x.Count().ToString(CultureInfo.InvariantCulture) + " " + ReasonText(x.Key, "")));
        }

        // a read error made short and free of paths (it goes into the log, the manifest and "Copy details")
        public static string Short(Exception e)
        {
            var m = (e?.Message ?? "").Replace("\r", " ").Replace("\n", " ");
            m = WorldMapReport.Redact(m);
            return m.Length > 120 ? m.Substring(0, 120) : m;
        }

        public static void WriteMarker(string path, PendingTile p)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, Json.Write(p.ToJson()));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch (Exception) { }
        }

        public static PendingTile ReadMarker(string path)
        {
            try { return File.Exists(path) ? PendingTile.FromJson(Json.Obj(File.ReadAllText(path))) : null; }
            catch (Exception) { return null; }
        }

        public static void RemoveMarker(string path) { try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { } }

        public static List<object> ListJson(IEnumerable<PendingTile> l) => l.Select(p => (object)p.ToJson()).ToList();

        public static List<PendingTile> ListFrom(Dictionary<string, object> d, string key)
        {
            var res = new List<PendingTile>();
            if (d == null || !d.TryGetValue(key, out var o) || !(o is List<object> l)) return res;
            foreach (var x in l) { var p = PendingTile.FromJson(x as Dictionary<string, object>); if (p != null) res.Add(p); }
            return res;
        }

        // A stamp of the game's local storage: the newest index file and how many there are. It changes when Battle.net adds downloaded data
        // (or the game updates), which is when a tile that could not be read is worth trying again.
        public static Func<string> StampForTest;
        public static string StorageStamp(string wowRoot)
        {
            if (StampForTest != null) return StampForTest();
            try
            {
                if (string.IsNullOrEmpty(wowRoot)) return "";
                var dir = Path.Combine(wowRoot, "Data", "data");
                if (!Directory.Exists(dir)) return "";
                long newest = 0; int n = 0;
                foreach (var f in Directory.GetFiles(dir, "*.idx")) { n++; var t = File.GetLastWriteTimeUtc(f).Ticks; if (t > newest) newest = t; }
                return n.ToString(CultureInfo.InvariantCulture) + "-" + newest.ToString(CultureInfo.InvariantCulture);
            }
            catch (Exception) { return ""; }
        }
    }
}
