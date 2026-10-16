using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ElansAddonHub.Services.WorldMap
{
    // ============================================================================================================================
    // "Copy details" on the world map (2.70): a short plain-text report a Lodge member can paste to whoever helps them (their own log stays on their
    // PC). What is in it: the Outpost version, the game build and flavour, the WebView2 runtime version, the graphics adapter as WebGL names it, the
    // continent and its tile counts, the tiles that could not be read (position and why), what the page reported, and the last world map lines of
    // hub.log. What is not: account or character names, Lodge data, and no paths except with the user folder written as %USERPROFILE%.
    // ============================================================================================================================
    public static class WorldMapReport
    {
        public sealed class Info
        {
            public string Outpost = "", Game = "", Flavor = "", WebView = "", Renderer = "";
            public string Continent = ""; public int MapId = -1; public string Mode = "";
            public string Problem = "";                            // what failed, in plain words ("" = nothing failed)
            public int PictureTiles, TerrainTiles;
            public List<PendingTile> Pending = new List<PendingTile>();
            public string Page = "";                               // the page's state: hello / ready / timed out ...
            public List<string> PageErrors = new List<string>();
        }

        public static Func<string> LogPathForTest;
        static string LogPath => LogPathForTest?.Invoke() ?? Path.Combine(Util.DataDir, "hub.log");

        // the user folder (and the user name inside a path) as %USERPROFILE%, the local / roaming app data as %LOCALAPPDATA% / %APPDATA%
        public static string Redact(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            string Rep(string text, string what, string with) => string.IsNullOrEmpty(what) || what.Length < 3 ? text : Regex.Replace(text, Regex.Escape(what.TrimEnd('\\', '/')), with.Replace("$", "$$"), RegexOptions.IgnoreCase);
            s = Rep(s, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "%LOCALAPPDATA%");
            s = Rep(s, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "%APPDATA%");
            s = Rep(s, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%");
            s = Rep(s, Path.GetTempPath(), "%TEMP%");
            // any other user's folder (another drive letter, an old profile): C:\Users\<name> -> %USERPROFILE%
            s = Regex.Replace(s, @"[A-Za-z]:[\\/]+Users[\\/]+[^\\/:*?""<>|\r\n]+", "%USERPROFILE%", RegexOptions.IgnoreCase);
            var user = Environment.UserName;
            if (!string.IsNullOrEmpty(user) && user.Length >= 3) s = Regex.Replace(s, @"(?<=[\\/])" + Regex.Escape(user) + @"(?=[\\/]|$)", "%USERNAME%", RegexOptions.IgnoreCase);
            return s;
        }

        // the last `max` lines of hub.log about the world map (pictures, terrain, the page, WebView2, WebGL); the self-test's own lines are left out
        public static List<string> LogLines(int max = 30)
        {
            var res = new List<string>();
            try
            {
                var p = LogPath;
                if (!File.Exists(p)) return res;
                string all;
                using (var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) using (var sr = new StreamReader(fs)) all = sr.ReadToEnd();
                foreach (var line in all.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var l = line.ToLowerInvariant();
                    if (l.Contains("worldmap test")) continue;
                    if (l.Contains("world map") || l.Contains("worldmap") || l.Contains("webview") || l.Contains("webgl")) res.Add(Redact(line.Length > 400 ? line.Substring(0, 400) : line));
                }
            }
            catch (Exception) { }
            return res.Skip(Math.Max(0, res.Count - max)).ToList();
        }

        public static string Build(Info i)
        {
            var I = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendLine("Elan's Outpost - world map report");
            sb.AppendLine("Outpost: " + Or(i.Outpost));
            sb.AppendLine("Game: " + Or(i.Game) + (string.IsNullOrEmpty(i.Flavor) ? "" : " (" + i.Flavor + ")"));
            sb.AppendLine("WebView2 runtime: " + Or(i.WebView));
            sb.AppendLine("Graphics (WebGL): " + Or(i.Renderer));
            sb.AppendLine("Continent: " + Or(i.Continent) + (i.MapId >= 0 ? " (map " + i.MapId.ToString(I) + ")" : "") + (string.IsNullOrEmpty(i.Mode) ? "" : ", " + i.Mode + " view"));
            if (!string.IsNullOrEmpty(i.Problem)) sb.AppendLine("Problem: " + Redact(i.Problem));
            var pics = i.Pending.Where(p => p.Part == "picture").ToList(); var ter = i.Pending.Where(p => p.Part == "terrain").ToList();
            sb.AppendLine("Pictures: " + i.PictureTiles.ToString(I) + " map tiles, " + pics.Count.ToString(I) + " shown with a stand-in");
            sb.AppendLine("Terrain: " + i.TerrainTiles.ToString(I) + " tiles, " + ter.Count(p => p.Shown == "low").ToString(I) + " at low detail, " + ter.Count(p => p.Shown != "low").ToString(I) + " placeholders");
            if (i.Pending.Count > 0)
            {
                sb.AppendLine("Not readable yet (" + i.Pending.Count.ToString(I) + "):");
                foreach (var p in i.Pending.OrderBy(p => p.Part).ThenBy(p => p.Row).ThenBy(p => p.Col).Take(60))
                    sb.AppendLine("  " + p.Part + " tile " + p.Col.ToString(I) + "," + p.Row.ToString(I) + ": " + Redact(p.ReasonText) + " - shown as " + p.ShownText);
                if (i.Pending.Count > 60) sb.AppendLine("  ... and " + (i.Pending.Count - 60).ToString(I) + " more");
            }
            if (!string.IsNullOrEmpty(i.Page)) sb.AppendLine("Page: " + i.Page);
            foreach (var e in i.PageErrors.Take(5)) sb.AppendLine("Page error: " + Redact(e));
            var log = LogLines();
            sb.AppendLine("Recent world map log lines (" + log.Count.ToString(I) + "):");
            foreach (var l in log) sb.AppendLine("  " + l);
            return sb.ToString();
        }

        static string Or(string s) => string.IsNullOrEmpty(s) ? "unknown" : s;
    }
}
