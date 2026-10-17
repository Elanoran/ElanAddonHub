using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ElansAddonHub.Services.Maps;

namespace ElansAddonHub.Services.WorldMap
{
    // Where the world map page is asked to look: the sub-path of the hosted Atlas category "worldmap":
    //   worldmap:                                 the map as it was left (the continent last shown)
    //   worldmap:kalimdor                         a continent by its slug (the slug of its name; also the map's directory name: "azeroth")
    //   worldmap:1                                a continent by its world map id
    //   worldmap:1?x=1630&y=-4373&z=5             centred on the WORLD position x (north) / y (west) in yards, at zoom level z (0-9)
    // The id the page shows (and copies) is the canonical one: a bare continent keeps its slug, a view is "<map id>?x=&y=&z=" with whole yards and
    // zoom to two decimals. Positions outside the world (|x| or |y| above 17,066.67) and zoom outside 0..9 are refused, not clamped.
    public sealed class WorldMapTarget
    {
        public int MapId = -1;             // -1 = as left
        public string Slug = "";           // set when the text named the continent by slug
        public bool HasView;
        public bool Pin;                   // mark the position with a ring ("Show on world map"); dropped from the id once the view moves
        public int Mode;                   // 0 = as last used, 2 = the flat map, 3 = the 3D terrain view (&m=2 / &m=3)
        public double X, Y, Z = 4;
        public string Problem;             // set = the text is not a valid world map id
        public bool Valid => Problem == null;
    }

    public static class WorldMapIds
    {
        public const string Category = "worldmap";
        public const double MaxCoord = 32 * WorldMapMath.TileYards;     // 17,066.67
        public const double MaxZoom = 9;

        // the layer ids the page and the app agree on (messages naming any other are refused)
        public static readonly string[] LayerIds = { "zones", "borders", "fm", "routes", "instances", "nodes", "saved", "vendors", "trainers", "quests", "rares", "seen", "finds", "trailevents" };
        public static readonly string[] NodeGroups = { "herbs", "ore", "chests", "fishing" };
        public static readonly string[] InstanceGroups = { "dungeons", "raids" };
        public static readonly string[] FactionGroups = { "horde", "alliance", "neutral" };
        public static readonly string[] SeenGroups = { "met", "killed", "rares" };
        public static bool IsLayerId(string id) => id != null && (LayerIds.Contains(id) || (id.StartsWith("nodes.", StringComparison.Ordinal) && NodeGroups.Contains(id.Substring(6))) || (id.StartsWith("instances.", StringComparison.Ordinal) && InstanceGroups.Contains(id.Substring(10))) || (id.StartsWith("fm.", StringComparison.Ordinal) && FactionGroups.Contains(id.Substring(3))) || (id.StartsWith("routes.", StringComparison.Ordinal) && FactionGroups.Contains(id.Substring(7))) || (id.StartsWith("seen.", StringComparison.Ordinal) && SeenGroups.Contains(id.Substring(5))));
        public static bool IsTopLayer(string id) => id != null && LayerIds.Contains(id);

        static string Inv(double d, string f) => d.ToString(f, CultureInfo.InvariantCulture);

        // "kalimdor", "1", "1?x=1&y=2&z=3" -> a target. `slugs` maps a slug to its map id (null = no continent is known by name: slugs are then refused).
        public static WorldMapTarget Parse(string sub, Func<string, int?> slugs)
        {
            var t = new WorldMapTarget();
            sub = (sub ?? "").Trim().Trim('/');
            if (sub.Length == 0) return t;
            if (sub.Length > 200) { t.Problem = "too long"; return t; }
            string path = sub, query = "";
            int q = sub.IndexOf('?');
            if (q >= 0) { path = sub.Substring(0, q); query = sub.Substring(q + 1); }
            path = path.Trim('/').ToLowerInvariant();
            if (path.Length == 0 && query.Length > 0) { t.Problem = "no map named"; return t; }
            if (path.Length > 0)
            {
                if (path.All(ch => ch >= '0' && ch <= '9'))
                {
                    if (path.Length > 5 || !int.TryParse(path, NumberStyles.None, CultureInfo.InvariantCulture, out var id)) { t.Problem = "bad map id"; return t; }
                    t.MapId = id;
                }
                else
                {
                    if (!path.All(ch => (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '-')) { t.Problem = "bad map name"; return t; }
                    var id = slugs?.Invoke(path);
                    if (id == null) { t.Problem = "unknown continent '" + path + "'"; return t; }
                    t.MapId = id.Value; t.Slug = path;
                }
            }
            if (query.Length == 0) return t;
            bool hx = false, hy = false;
            foreach (var part in query.Split('&'))
            {
                if (part.Length == 0) continue;
                int e = part.IndexOf('=');
                if (e <= 0) { t.Problem = "bad parameter"; return t; }
                var k = part.Substring(0, e).ToLowerInvariant(); var v = part.Substring(e + 1);
                if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || double.IsNaN(d) || double.IsInfinity(d)) { t.Problem = "bad number for " + k; return t; }
                switch (k)
                {
                    case "x": if (Math.Abs(d) > MaxCoord) { t.Problem = "x is outside the world"; return t; } t.X = d; hx = true; break;
                    case "y": if (Math.Abs(d) > MaxCoord) { t.Problem = "y is outside the world"; return t; } t.Y = d; hy = true; break;
                    case "p": t.Pin = d == 1; break;
                    case "m": if (d != 2 && d != 3) { t.Problem = "m is 2 or 3"; return t; } t.Mode = (int)d; break;
                    case "z": if (d < 0 || d > MaxZoom) { t.Problem = "zoom is outside 0-9"; return t; } t.Z = Math.Round(d, 2); break;
                }
            }
            if (hx != hy) { t.Problem = "a position needs both x and y"; return t; }
            t.HasView = hx;
            if (t.HasView && t.MapId < 0) { t.Problem = "a position needs a map"; return t; }
            return t;
        }

        public static string SubOf(int mapId, string slug, bool hasView, double x, double y, double z, bool pin = false, int mode = 0)
        {
            if (mapId < 0) return "";
            if (!hasView) return (string.IsNullOrEmpty(slug) ? mapId.ToString(CultureInfo.InvariantCulture) : slug) + (mode == 3 ? "?m=3" : mode == 2 ? "?m=2" : "");
            return mapId.ToString(CultureInfo.InvariantCulture) + "?x=" + Inv(Math.Round(x), "0") + "&y=" + Inv(Math.Round(y), "0") + "&z=" + Inv(Math.Round(z, 2), "0.##") + (pin ? "&p=1" : "") + (mode == 3 ? "&m=3" : mode == 2 ? "&m=2" : "");
        }

        public static string Id(int mapId, string slug, bool hasView, double x, double y, double z, bool pin = false, int mode = 0) => Category + ":" + SubOf(mapId, slug, hasView, x, y, z, pin, mode);

        // the Atlas id that opens the map centred on a world position
        public static string IdAt(int mapId, double x, double y, double z = 5, bool pin = true) => Id(mapId, null, true, x, y, z, pin);

        // a canonical sub for text that is only checked for shape (chat links, no continent list needed): digits or a slug, with a valid view
        public static string CanonicalSyntax(string sub)
        {
            var t = Parse(sub, s => s.Length >= 3 ? 0 : (int?)null);
            if (!t.Valid || (t.MapId < 0)) return null;
            return t.HasView ? SubOf(t.MapId, null, true, t.X, t.Y, t.Z, t.Pin, t.Mode) : SubOf(t.MapId, t.Slug, false, 0, 0, 0, false, t.Mode);
        }

        public static Func<string, int?> SlugsOf(MapIndex idx)
        {
            var conts = WorldMapService.Continents(idx);
            return slug =>
            {
                var c = conts.FirstOrDefault(x => x.Slug == slug);
                if (c != null) return c.MapId;
                var gm = conts.Select(x => idx.GameMap(x.MapId)).FirstOrDefault(g => g != null && string.Equals(AtlasSlug(g.Directory), slug, StringComparison.Ordinal));
                return gm?.Id;
            };
        }

        static string AtlasSlug(string s) => Atlas.AtlasHosts.Slug(s);

        // ---- search: "world map", "kalimdor map", "eastern kingdoms"
        public static List<Atlas.AtlasHit> Hits(string q)
        {
            var hits = new List<Atlas.AtlasHit>();
            q = (q ?? "").Trim().ToLowerInvariant();
            if (q.Length < 2) return hits;
            var idx = MapData.Index;
            var conts = WorldMapService.Continents(idx);
            if (conts.Count == 0 && MapData.Status == MapData.State.Disabled) return hits;
            int r = Atlas.AtlasQuery.MatchRank("World map", q);
            if (r < 0 && ("worldmap".StartsWith(q, StringComparison.Ordinal) || "map of the world".StartsWith(q, StringComparison.Ordinal))) r = 1;
            if (r >= 0) hits.Add(new Atlas.AtlasHit { Id = Category + ":", Kind = "page", Name = "World map", Detail = "A zoomable map of each continent with flight paths, nodes, vendors and more", Label = "World map", Rank = r });
            foreach (var c in conts)
            {
                int cr = Math.Min(Atlas.AtlasQuery.MatchRank(c.Name + " map", q) < 0 ? 99 : Atlas.AtlasQuery.MatchRank(c.Name + " map", q), Atlas.AtlasQuery.MatchRank(c.Name, q) < 0 ? 99 : Atlas.AtlasQuery.MatchRank(c.Name, q));
                if (cr >= 99) continue;
                hits.Add(new Atlas.AtlasHit { Id = Category + ":" + c.Slug, Kind = "page", Name = c.Name + " map", Detail = c.IsContinent ? "The whole continent, zoomable" : "The whole map of " + c.Name + ", zoomable", Label = "World map", Rank = Math.Max(1, cr) });
            }
            return hits;
        }

        // "Show on world map" from a page: the id that opens the continent centred on the markers (fractions of the zone map `uiMapId`; none = the zone's
        // middle) with a zoom that fits them, and a ring on the spot when it is one place. null = the zone is not on a continent (an instance, a battleground).
        public static string ShowOnMapId(MapIndex idx, int uiMapId, IList<AtlasMarker> markers)
        {
            if (idx == null || uiMapId <= 0 || !WorldMapAvailable(idx)) return null;
            double fx = 0.5, fy = 0.5; bool pin = false;
            var yards = Atlas.GatherNodes.YardsOf(idx, uiMapId);
            double spread = Math.Max(yards.W, yards.H);
            if (markers != null && markers.Count > 0)
            {
                fx = markers.Average(m => m.Fx); fy = markers.Average(m => m.Fy);
                pin = markers.Count == 1;
                double sx = (markers.Max(m => m.Fx) - markers.Min(m => m.Fx)) * yards.W, sy = (markers.Max(m => m.Fy) - markers.Min(m => m.Fy)) * yards.H;
                spread = Math.Max(Math.Max(sx, sy) * 1.6, 500);
            }
            if (spread <= 0) spread = 3000;
            // a 700 pixel wide view shows 700 * 133.33 / 2^z yards (the native level has 1.04 yards per pixel: 4 units per 533.33 yards)
            double z = Math.Log(700.0 * (WorldMapMath.TileYards / WorldMapMath.UnitsPerAdt) / spread, 2);
            z = Math.Max(3, Math.Min(6.5, Math.Round(z * 4) / 4));
            if (!TryPlace(idx, uiMapId, fx, fy, out var mapId, out var x, out var y)) return null;
            return IdAt(mapId, x, y, z, pin);
        }

        // the continents the client has pictures for are known
        public static bool WorldMapAvailable(MapIndex idx) => WorldMapService.Continents(idx).Count > 0;

        // ---- a position on a zone map -> the world map to open
        // "Show on world map" from a zone page: the continent (world map id) and the world position of a place given as fractions of a UiMap.
        // false when the place is not on a continent that has a world map (an instance, a battleground).
        public static bool TryPlace(MapIndex idx, int uiMapId, double fx, double fy, out int mapId, out double x, out double y)
        {
            mapId = 0; x = y = 0;
            if (idx == null) return false;
            var w = idx.MapToWorld(uiMapId, fx, fy);
            if (w == null) return false;
            var c = WorldMapService.Continent(idx, w.Value.MapId);
            if (c == null) return false;
            mapId = w.Value.MapId; x = w.Value.X; y = w.Value.Y;
            return true;
        }
    }
}
