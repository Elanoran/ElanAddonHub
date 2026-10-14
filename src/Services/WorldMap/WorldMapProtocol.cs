using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ElansAddonHub.Services.Maps;

namespace ElansAddonHub.Services.WorldMap
{
    // ============================================================================================================================
    // The two-way messages between the app and the world map page (JSON through WebView2's postMessage / PostWebMessageAsJson).
    // The page is not trusted: every message it sends is parsed and checked here, and anything that does not fit (not JSON, too long, an unknown action,
    // a position outside the plane, a layer or link that is not on the lists below) is refused with a reason and does nothing.
    //
    //   page -> app   hello | inited                       the page started / drew its map (diagnostics)
    //                 ready {tiles, errors}               the first tiles are in
    //                 view {lat, lng, z}                  the user moved the map (the Atlas id follows)
    //                 open {id}                           a marker was clicked: an Atlas id of an entity kind ("npc:46")
    //                 nav {lat, lng}                      "Navigate here" on a spot of the plane
    //                 layers {on:[ids]}                   the toggles changed (saved in the settings)
    //                 need {layer}                        a layer was switched on and has no data yet
    //   app -> page   init | goto | layer | layerinfo | toast | on     (built by WorldMapView, see worldmap.js for the shapes)
    // ============================================================================================================================
    public enum WorldMapMessageType { Hello, Inited, Ready, View, Open, Nav, Layers, Need }

    public sealed class WorldMapMessage
    {
        public WorldMapMessageType Type;
        public double Lat, Lng, Z;
        public string Id = "", Layer = "";
        public List<string> On = new List<string>();
        public int Tiles, Errors;
    }

    public static class WorldMapProtocol
    {
        public const int MaxMessageChars = 4096;
        // the plane the page draws in: lng = col * 4 (0..256), lat = -row * 4 (-256..0)
        public const double PlaneMax = WorldMapMath.Grid * WorldMapMath.UnitsPerAdt;
        static readonly Regex OpenId = new Regex(@"^(item|npc|obj|quest|zone|instance|spell|skill|faction):[1-9]\d{0,8}\z", RegexOptions.CultureInvariant);

        public static bool IsOpenId(string id) => id != null && OpenId.IsMatch(id);
        public static bool InPlane(double lat, double lng) => !double.IsNaN(lat + lng) && !double.IsInfinity(lat + lng) && lat <= 0 && lat >= -PlaneMax && lng >= 0 && lng <= PlaneMax;

        // null + a reason when the message is refused
        public static WorldMapMessage Parse(string json, out string refusal)
        {
            refusal = null;
            if (string.IsNullOrWhiteSpace(json)) { refusal = "empty message"; return null; }
            if (json.Length > MaxMessageChars) { refusal = "message too long"; return null; }
            Dictionary<string, object> d;
            try { d = Json.Parse(json) as Dictionary<string, object>; }
            catch (Exception) { refusal = "not JSON"; return null; }
            if (d == null) { refusal = "not an object"; return null; }
            var t = d.Str("t");
            if (string.IsNullOrEmpty(t)) { refusal = "no action"; return null; }
            var m = new WorldMapMessage();
            switch (t)
            {
                case "hello": m.Type = WorldMapMessageType.Hello; return m;
                case "inited": m.Type = WorldMapMessageType.Inited; return m;
                case "ready":
                    m.Type = WorldMapMessageType.Ready; m.Tiles = Math.Max(0, Math.Min(100000, d.Int("tiles"))); m.Errors = Math.Max(0, Math.Min(1000000, d.Int("errors"))); return m;
                case "view":
                case "nav":
                    {
                        if (!(Num(d, "lat", out m.Lat) && Num(d, "lng", out m.Lng))) { refusal = "a position needs lat and lng numbers"; return null; }
                        if (!InPlane(m.Lat, m.Lng)) { refusal = "position outside the map"; return null; }
                        if (t == "view")
                        {
                            if (!Num(d, "z", out m.Z) || m.Z < 0 || m.Z > WorldMapIds.MaxZoom) { refusal = "zoom outside 0-9"; return null; }
                            m.Type = WorldMapMessageType.View;
                        }
                        else m.Type = WorldMapMessageType.Nav;
                        return m;
                    }
                case "open":
                    {
                        var id = d.Str("id");
                        if (!IsOpenId(id)) { refusal = "not a link the map may open"; return null; }
                        m.Type = WorldMapMessageType.Open; m.Id = id; return m;
                    }
                case "layers":
                    {
                        if (!d.TryGetValue("on", out var o) || !(o is List<object> l)) { refusal = "layers needs a list"; return null; }
                        if (l.Count > 32) { refusal = "too many layers"; return null; }
                        foreach (var x in l)
                        {
                            var s = x as string;
                            if (!WorldMapIds.IsLayerId(s)) { refusal = "unknown layer"; return null; }
                            if (!m.On.Contains(s)) m.On.Add(s);
                        }
                        m.Type = WorldMapMessageType.Layers; return m;
                    }
                case "need":
                    {
                        var s = d.Str("layer");
                        if (!WorldMapIds.IsTopLayer(s)) { refusal = "unknown layer"; return null; }
                        m.Type = WorldMapMessageType.Need; m.Layer = s; return m;
                    }
                default: refusal = "unknown action"; return null;
            }
        }

        static bool Num(Dictionary<string, object> d, string k, out double v)
        {
            v = 0;
            if (!(d.TryGetValue(k, out var o) && o is double x) || double.IsNaN(x) || double.IsInfinity(x)) return false;
            v = x;
            return true;
        }

        // ---- app -> page: the shapes of the messages are built by plain dictionaries (Json.Write)
        public static string Toast(string text, bool ok) => Json.Write(new Dictionary<string, object> { ["t"] = "toast", ["text"] = text ?? "", ["ok"] = ok });
        public static string Goto(double lat, double lng, double z, string pinName = null, string pinTip = null)
        {
            var d = new Dictionary<string, object> { ["t"] = "goto", ["lat"] = lat, ["lng"] = lng, ["z"] = z };
            if (pinName != null) d["pin"] = new Dictionary<string, object> { ["name"] = pinName, ["tip"] = pinTip ?? "" };
            return Json.Write(d);
        }
    }

    // ============================================================================================================================
    // "Navigate here": the chat command a spot on the map turns into. Contract with Elan's Toolkit:  /etk way <uiMapID> <x> <y> [title]
    // x and y are percent of the zone map (0-100, two decimals), the zone is the most specific map that shows the spot (the same rule every Atlas
    // marker uses), the title is the zone's name made safe for a chat line.
    // ============================================================================================================================
    public static class WorldMapCommands
    {
        public const string Command = "/etk way";

        // the text for the world position (x north, y west) on world map `mapId`; null + a reason when no zone map shows it
        public static string Way(MapIndex idx, int mapId, double x, double y, out string problem)
        {
            problem = null;
            if (idx == null) { problem = "The maps are not ready yet."; return null; }
            var p = idx.WorldToMap(mapId, x, y);
            if (p == null) { problem = "No zone map shows that spot."; return null; }
            double px = Math.Max(0, Math.Min(100, p.Value.Fx * 100)), py = Math.Max(0, Math.Min(100, p.Value.Fy * 100));
            var title = SafeTitle(idx.Name(p.Value.UiMapId));
            return Command + " " + p.Value.UiMapId.ToString(CultureInfo.InvariantCulture) + " " + px.ToString("0.00", CultureInfo.InvariantCulture) + " " + py.ToString("0.00", CultureInfo.InvariantCulture) + (title.Length > 0 ? " " + title : "");
        }

        // a title that is one chat-safe line: control characters and the chat escape characters out, spaces collapsed, at most 40 characters
        public static string SafeTitle(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder();
            bool space = false;
            foreach (var ch in s)
            {
                if (char.IsControl(ch) || ch == '|' || ch == '\\' || ch == '[' || ch == ']' || ch == '"' || ch == '<' || ch == '>') continue;
                if (char.IsWhiteSpace(ch)) { space = sb.Length > 0; continue; }
                if (space) { sb.Append(' '); space = false; }
                sb.Append(ch);
                if (sb.Length >= 40) break;
            }
            return sb.ToString().TrimEnd();
        }
    }
}
