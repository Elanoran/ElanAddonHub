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
    //                 webgl {ok, why, renderer}           (3D page) WebGL is there, or why not: the app falls back to the flat map
    //                 view3 {lat, lng, dist, az, tilt}    (3D page) the camera moved: its target on the plane, distance in yards, bearing and tilt in degrees
    //                 stats {fps, mem, tris, tiles, tex}  (3D page) a frame-rate / memory sample every few seconds (diagnostics)
    //                 prefs {detail, exag}                (3D page) the detail (low|medium|high) and height scale (0.5-1.5, 1 = true height) the person chose
    //                 buildings {mode}                    (3D page) the Buildings switch: off | bridges | all
    //                 bbuild {mode} | bpause | bresume | bcancel   (3D page) the card and the progress line: Build now, Pause, Resume, Cancel
    //                 objects {level}                     (3D page, 3.2) the Trees & objects switch: off | low | medium | high (also when the page steps it down by itself)
    //                 obuild | opause | oresume | ocancel  (3D page, 3.2) its card and progress line: Build now, Pause, Resume, Cancel
    //                 gneed {c, r}                        (3D page, 3.2) the real ground of tile (c, r) is wanted: the app makes its files and answers gready {c, r, ok}
    //                 wneed {fdid}                        (3D page, 3.2) a building's detailed textured model is wanted: the app makes it and answers wready {fdid, ok}
    //                 error {msg}                         a script error in the page (logged and kept for "Copy details"; at most a few per page)
    //                 howto                               the live layer's "How to turn it on" link: the app opens its how-to card
    //                 follow {on}                         the live layer's "Follow me" switch (remembered in the settings)
//                 showmap                             the live card's "Show <map>" button: the app opens the world map of the map you are on (2.91)
    //                 mfollow {id}                        "Follow this member" on a Lodge member's dot or their row in the card (id > 0), or following stopped (0)
    //                                                      (2.97; the page pans itself; 2.98: a member on another world map -> the app opens that map first)
    //   app -> page   init | goto | layer | layerinfo | toast | on     (built by WorldMapView, see worldmap.js for the shapes)
    //                 me {state, ...}                      the live position layer (see Me below), at most once a second
    //                 members {list}                      the Lodge members who share their position with me (see Members below), at most once a second
    // ============================================================================================================================
    public enum WorldMapMessageType { Hello, Inited, Ready, View, Open, Nav, Layers, Need, Webgl, View3, Stats, Prefs, Buildings, BBuild, BPause, BResume, BCancel, Error, HowTo, Follow, ShowMap, MemberFollow, Objects, OBuild, OPause, OResume, OCancel, GNeed, WNeed }

    public sealed class WorldMapMessage
    {
        public WorldMapMessageType Type;
        public double Lat, Lng, Z;
        public string Id = "", Layer = "";
        public List<string> On = new List<string>();
        public int Tiles, Errors;
        // the 3D page: webgl {ok, why, renderer}, view3 {lat, lng, dist, az, tilt}, stats {fps, mem, tris, tiles, tex}, prefs {detail, exag}
        public bool Ok;
        public string Why = "", Detail = "";
        public double Dist, Az, Tilt, Fps, MemMb, Exag;
        public long Tris;
        public int Buildings;                                  // stats: building instances drawn
        public int Textures;
        public int Seq = -1;                                   // view / view3: the last place the app sent the page to ("s" of init/goto/fit), -1 when the page names none
        public int MemberId;                                   // mfollow: the member followed, 0 = none
        public int Col = -1, Row = -1;                         // gneed (3.2): the tile whose real ground the page wants
    }

    // a Lodge member's shared position for the world map (2.97): world coordinates (continent map id, x north, y west, yards), never the viewer's own.
    // 2.98: drawn like your own - Here (the point with its height for 3D, null = none worked out), Trail (the last 10 minutes, MemberTrails), Move (their
    // move bits now), At (the time the trail's ages count from), Foot / Hunter (the prints), Zone (the list row's hover)
    public struct MemberDot
    {
        public int Id; public string Name; public string Color; public int MapId; public double X, Y;
        public string Zone; public int Move; public LiveTrail.Point? Here; public List<LiveTrail.Point> Trail; public DateTime At; public string Foot; public bool Hunter;
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
                case "hello": m.Type = WorldMapMessageType.Hello; m.Detail = Clean(d.Str("p"), 4); return m;
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
                            m.Type = WorldMapMessageType.View; m.Seq = SeqOf(d);
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
                case "webgl":
                    {
                        if (!d.TryGetValue("ok", out var okv) || !(okv is bool okb)) { refusal = "webgl needs ok true or false"; return null; }
                        m.Type = WorldMapMessageType.Webgl; m.Ok = okb; m.Why = Clean(d.Str("why"), 160); m.Detail = Clean(d.Str("renderer"), 100); return m;
                    }
                case "view3":
                    {
                        if (!(Num(d, "lat", out m.Lat) && Num(d, "lng", out m.Lng) && Num(d, "dist", out m.Dist) && Num(d, "az", out m.Az) && Num(d, "tilt", out m.Tilt))) { refusal = "a 3D view needs lat, lng, dist, az and tilt numbers"; return null; }
                        if (!InPlane(m.Lat, m.Lng)) { refusal = "position outside the map"; return null; }
                        if (m.Dist < 5 || m.Dist > 200000 || Math.Abs(m.Az) > 720 || m.Tilt < -1 || m.Tilt > 91) { refusal = "camera outside its limits"; return null; }
                        m.Type = WorldMapMessageType.View3; m.Seq = SeqOf(d); return m;
                    }
                case "stats":
                    {
                        if (!(Num(d, "fps", out m.Fps) && Num(d, "mem", out m.MemMb)) || m.Fps < 0 || m.Fps > 2000 || m.MemMb < 0 || m.MemMb > 1000000) { refusal = "stats out of range"; return null; }
                        m.Tris = (long)Math.Max(0, Math.Min(2000000000, d.Long("tris"))); m.Buildings = (int)Math.Max(0, Math.Min(10000000, d.Long("bld"))); m.Tiles = Math.Max(0, Math.Min(100000, d.Int("tiles"))); m.Textures = Math.Max(0, Math.Min(100000, d.Int("tex")));
                        m.Type = WorldMapMessageType.Stats; return m;
                    }
                case "prefs":
                    {
                        var det = d.Str("detail");
                        if (!(det == "low" || det == "medium" || det == "high") || !Num(d, "exag", out m.Exag) || m.Exag < 0.5 || m.Exag > 1.5) { refusal = "prefs: detail low, medium or high and exag 0.5 to 1.5"; return null; }
                        m.Type = WorldMapMessageType.Prefs; m.Detail = det; return m;
                    }
                case "buildings":
                case "bbuild":
                    {
                        var md = d.Str("mode");
                        if (!(md == "off" || md == "bridges" || md == "all") || (t == "bbuild" && md == "off")) { refusal = t + ": mode bridges or all" + (t == "buildings" ? " (or off)" : ""); return null; }
                        m.Type = t == "buildings" ? WorldMapMessageType.Buildings : WorldMapMessageType.BBuild; m.Detail = md; return m;
                    }
                case "bpause": m.Type = WorldMapMessageType.BPause; return m;
                case "bresume": m.Type = WorldMapMessageType.BResume; return m;
                case "bcancel": m.Type = WorldMapMessageType.BCancel; return m;
                case "objects":
                    {
                        var lv = d.Str("level");
                        if (!(lv == "off" || lv == "low" || lv == "medium" || lv == "high")) { refusal = "objects: level off, low, medium or high"; return null; }
                        m.Type = WorldMapMessageType.Objects; m.Detail = lv; return m;
                    }
                case "obuild": m.Type = WorldMapMessageType.OBuild; return m;
                case "wneed":
                    {
                        // the building's FileDataID rides in MemberId (an int the message already has)
                        if (!(d.TryGetValue("fdid", out var fo) && fo is double fd && fd >= 1 && fd <= int.MaxValue && fd == Math.Floor(fd))) { refusal = "wneed needs a building's FileDataID"; return null; }
                        m.Type = WorldMapMessageType.WNeed; m.MemberId = (int)fd; return m;
                    }
                case "gneed":
                    {
                        if (!(d.TryGetValue("c", out var co) && co is double cd && cd >= 0 && cd <= 63 && cd == Math.Floor(cd)) || !(d.TryGetValue("r", out var ro) && ro is double rd && rd >= 0 && rd <= 63 && rd == Math.Floor(rd)))
                        { refusal = "gneed: c and r are tile numbers 0 to 63"; return null; }
                        m.Type = WorldMapMessageType.GNeed; m.Col = (int)cd; m.Row = (int)rd; return m;
                    }
                case "opause": m.Type = WorldMapMessageType.OPause; return m;
                case "oresume": m.Type = WorldMapMessageType.OResume; return m;
                case "ocancel": m.Type = WorldMapMessageType.OCancel; return m;
                case "howto": m.Type = WorldMapMessageType.HowTo; return m;
                case "showmap": m.Type = WorldMapMessageType.ShowMap; return m;
                case "mfollow":
                    {
                        if (!(d.TryGetValue("id", out var io) && io is double iv) || iv < 0 || iv > int.MaxValue || iv != Math.Floor(iv)) { refusal = "mfollow needs a member id"; return null; }
                        m.Type = WorldMapMessageType.MemberFollow; m.MemberId = (int)iv; return m;
                    }
                case "follow":
                    {
                        if (!d.TryGetValue("on", out var fo) || !(fo is bool fb)) { refusal = "follow needs on true or false"; return null; }
                        m.Type = WorldMapMessageType.Follow; m.Ok = fb; return m;
                    }
                case "error": m.Type = WorldMapMessageType.Error; m.Detail = Clean(d.Str("msg"), 240); if (m.Detail.Length == 0) { refusal = "error needs a msg"; return null; } return m;
                default: refusal = "unknown action"; return null;
            }
        }

        // a text from the page made safe to keep in a log or show: control characters out, at most `max` characters
        public static string Clean(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder();
            foreach (var ch in s) { if (char.IsControl(ch)) continue; sb.Append(ch); if (sb.Length >= max) break; }
            return sb.ToString();
        }

        static int SeqOf(Dictionary<string, object> d) => d.TryGetValue("s", out var o) && o is double x && x >= 0 && x <= int.MaxValue && x == Math.Floor(x) ? (int)x : -1;

        static bool Num(Dictionary<string, object> d, string k, out double v)
        {
            v = 0;
            if (!(d.TryGetValue(k, out var o) && o is double x) || double.IsNaN(x) || double.IsInfinity(x)) return false;
            v = x;
            return true;
        }

        // ---- app -> page: the shapes of the messages are built by plain dictionaries (Json.Write)
        public static string Toast(string text, bool ok) => Json.Write(new Dictionary<string, object> { ["t"] = "toast", ["text"] = text ?? "", ["ok"] = ok });
        // ---- "me": the live layer (your dot, trail and notice). Built and checked here: a state from the list, a #rrggbb colour, a clean one-line name,
        // only points inside the plane (a dropped point breaks the line), at most LiveTrail.MaxPoints, ages 0..600 s. The page checks the shapes again.
        //   {t:'me', state, name, color, notice, howto, here:[lat,lng], last:[lat,lng], trail:[lat,lng,age,brk, ...], where}
        //   state: off (no layer) | none (not playing) | ok | elsewhere (a position on another continent) | unavailable | notshared | oldhub
        //   show: "Show <map>" when elsewhere and that map has a world map of its own (2.91)
        public const double TrailBreakYards = 200, TrailBreakSeconds = 30;
        static readonly Regex HexColor = new Regex(@"^#[0-9A-Fa-f]{6}\z", RegexOptions.CultureInvariant);
        //   events (companion 1.16.0+): [{lat, lng, age, k, name, line, c, link, at}] - the marks on this continent's trail, oldest first, at most LiveTrail.MaxMarks;
        //   k = kill | loot | herb | ore | skin | level | died | quest | area | flight, name / line from TrailEventText (the Outpost's own data), c = #rrggbb, at = local "HH:mm"
        //   2.82, the height for the 3D view (LiveElevation): elev:[z, s, ...] one pair per trail point (in the trail's order), hz:[z, s] for here, lz for last, z / zs on an event;
        //   z = yards (world height, not exaggerated), s = 0 terrain (the page drapes it over its own ground) | 1 a building's floor | 2 water | 3 in the air (a flight) | -1 unknown
        //   2.90, the paw prints (TrailPrints): mv:[bits, ...] how you moved at each trail point (LiveMove), od:[yards, ...] the trail's odometer, hmv = here's bits,
        //   prints:{foot: paw | hoof | bone | tiny, hunter, petKnown} (only when `prints` is given)
        public static string Me(LiveState state, string name, string color, LiveTrail.Point? here, IList<LiveTrail.Point> trail, int viewMapId, DateTime now, string elsewhere = null, IList<LiveTrail.Mark> marks = null, Dictionary<string, object> prints = null, bool canShow = false)
        {
            string st;
            switch (state)
            {
                case LiveState.Off: st = "off"; break;
                case LiveState.NotPlaying: st = "none"; break;
                case LiveState.Ok: st = here.HasValue && here.Value.MapId == viewMapId ? "ok" : "elsewhere"; break;
                case LiveState.Unavailable: st = "unavailable"; break;
                case LiveState.NotShared: st = "notshared"; break;
                case LiveState.OldHub: st = "oldhub"; break;
                default: st = "none"; break;
            }
            var d = new Dictionary<string, object> { ["t"] = "me", ["state"] = st };
            if (st == "off" || st == "none") return Json.Write(d);
            d["name"] = LivePositionText.CleanName(name);
            d["color"] = color != null && HexColor.IsMatch(color) ? color : "#E8E6E3";
            d["notice"] = st == "elsewhere" ? "You are on another map" + (string.IsNullOrEmpty(elsewhere) ? "" : ": " + Clean(elsewhere, 60)) : LivePositionText.NoticeFor(state) ?? "";
            // 2.91: the map you are on has a world map of its own (another continent, an island): the card offers "Show <map>" (the page sends showmap; the app picks the map from your position, never from the page)
            if (st == "elsewhere" && canShow && !string.IsNullOrEmpty(elsewhere)) d["show"] = "Show " + Clean(elsewhere, 60);
            d["howto"] = LivePositionText.HasHowTo(state);
            d["howtoText"] = LivePositionText.HowToLink;
            if (st == "ok") { WorldMapMath.WorldToLatLng(here.Value.X, here.Value.Y, out var la, out var ln); if (InPlane(la, ln)) { d["here"] = new List<object> { R6(la), R6(ln) }; d["hz"] = ZOf(here.Value.HasZ, here.Value.Z, here.Value.Src); } else { d["state"] = "unavailable"; d["notice"] = LivePositionText.Unavailable; } }
            TrailParts(trail, viewMapId, now, LiveTrail.MaxPoints, false, out var list, out var elev, out var mv, out var od);
            d["trail"] = list;
            d["elev"] = elev;
            if (prints != null) { d["mv"] = mv; d["od"] = od; d["hmv"] = here.HasValue ? here.Value.Move & 0xFF : 0; d["prints"] = prints; }
            // unavailable: the dot greys out where you were last seen on this map (if anywhere)
            if ((string)d["state"] == "unavailable" && list.Count >= 4) { d["last"] = new List<object> { list[list.Count - 4], list[list.Count - 3] }; d["lz"] = new List<object> { elev[elev.Count - 2], elev[elev.Count - 1] }; }
            var evs = new List<object>();
            if (marks != null)
            {
                foreach (var mk in marks.Skip(Math.Max(0, marks.Count - LiveTrail.MaxMarks)))
                {
                    if (mk.MapId != viewMapId) continue;
                    double age = (now - mk.At).TotalSeconds;
                    if (age > LiveTrail.Keep.TotalSeconds) continue;
                    WorldMapMath.WorldToLatLng(mk.X, mk.Y, out var la, out var ln);
                    if (!InPlane(la, ln)) continue;
                    var info = TrailEventText.Describe(mk);
                    if (string.IsNullOrEmpty(info.Key)) continue;
                    evs.Add(new Dictionary<string, object>
                    {
                        ["lat"] = R6(la), ["lng"] = R6(ln), ["age"] = Math.Round(Math.Max(0, age), 1), ["k"] = info.Key, ["name"] = Clean(info.Title ?? "", 80), ["line"] = Clean(info.Line ?? "", 80),
                        ["c"] = info.Color != null && HexColor.IsMatch(info.Color) ? info.Color : WorldMapLayers.EventFallbackColor, ["link"] = info.Link ?? "",
                        ["at"] = mk.At.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture),
                        ["z"] = ZOf(mk.HasZ, mk.Z, mk.Src)[0], ["zs"] = ZOf(mk.HasZ, mk.Z, mk.Src)[1],
                    });
                }
            }
            d["events"] = evs;
            return Json.Write(d);
        }
        // the trail's parts for the page (your own and, 2.98, each member's): [lat, lng, age, brk, ...] on this map only (a point elsewhere or outside the plane
        // breaks the line), its heights, move bits and odometer. At most `max` points: the newest (yours), or with `thin` a longer trail thinned evenly (every
        // k-th point, the newest always kept), so a member's 10 minutes stay whole at a fraction of the size; the break rules widen with the step.
        static void TrailParts(IList<LiveTrail.Point> trail, int viewMapId, DateTime now, int max, bool thin, out List<object> list, out List<object> elev, out List<object> mv, out List<object> od)
        {
            list = new List<object>(); elev = new List<object>(); mv = new List<object>(); od = new List<object>();
            if (trail == null) return;
            int n = trail.Count, k = thin && n > max ? (n + max - 1) / max : 1;
            int start = Math.Max(0, n - max * k);
            bool brk = true; LiveTrail.Point? prev = null;
            for (int i = start; i < n; i++)
            {
                if (k > 1 && (n - 1 - i) % k != 0) continue;
                var p = trail[i];
                if (p.MapId != viewMapId) { brk = true; prev = null; continue; }
                WorldMapMath.WorldToLatLng(p.X, p.Y, out var la, out var ln);
                if (!InPlane(la, ln)) { brk = true; prev = null; continue; }
                if (prev.HasValue && (LiveTrail.Dist(prev.Value.X, prev.Value.Y, p.X, p.Y) > TrailBreakYards * k || (p.At - prev.Value.At).TotalSeconds > TrailBreakSeconds * k)) brk = true;
                double age = Math.Max(0, Math.Min(LiveTrail.Keep.TotalSeconds, (now - p.At).TotalSeconds));
                list.Add(R6(la)); list.Add(R6(ln)); list.Add(Math.Round(age, 1)); list.Add(brk ? 1 : 0);
                elev.AddRange(ZOf(p.HasZ, p.Z, p.Src));
                mv.Add(p.Move & 0xFF); od.Add(Math.Round(p.Odo, 1));
                brk = false; prev = p;
            }
        }
        static double R6(double v) => Math.Round(v, 6);
        // a height for the page: [yards rounded to 0.1, source], or [0, -1] when there is none (or it is not a sane number)
        static List<object> ZOf(bool has, double z, ElevSrc s) => has && !double.IsNaN(z) && !double.IsInfinity(z) && Math.Abs(z) < 20000 ? new List<object> { Math.Round(z, 1), (int)s } : new List<object> { 0.0, -1 };

        // ---- "members" (2.97): the Lodge members who share their live position with me.
        //   {t:'members', list:[{id, name, color, ll:[lat, lng], hz, hmv, trail, elev, mv, od, prints}], rows:[{id, name, color, zone, here, map}]}
        //   list: the members on the map shown now, drawn like your own (2.98): a dot with a heading arrow, the fading trail and its prints, the height in 3D -
        //         the same shapes as "me" (hz / hmv / trail / elev / mv / od / prints, see Me); at most MaxDrawn (the one followed first, then in the order given), at most
        //         MaxMemberPoints trail points each; a clean one-line name, a #rrggbb colour, a point inside the plane.
        //   rows: everyone who shares with me on any map (the card's "Lodge, live" list, 2.98): here = drawn on the map shown now, map = the name of the world
        //         map they are on when it is another one ("" = none known), zone = their zone for the hover; at most MaxRows, by name.
        // A dot on another continent or outside the plane is never drawn (never a dot on the wrong map); an empty list takes every dot away.
        public const int MaxDrawn = 20, MaxMemberPoints = 300, MaxRows = 40;
        public static string Members(IEnumerable<MemberDot> dots, int viewMapId, int following = 0, Func<int, string> mapName = null)
        {
            var list = new List<object>(); var rows = new List<object>();
            var seen = new HashSet<int>(); var drawn = new HashSet<int>();
            var all = (dots ?? Enumerable.Empty<MemberDot>()).Where(m => m.Id > 0 && seen.Add(m.Id)).ToList();
            foreach (var m in all.Where(m => m.MapId == viewMapId).OrderBy(m => m.Id == following ? 0 : 1))
            {
                if (list.Count >= MaxDrawn) break;
                WorldMapMath.WorldToLatLng(m.X, m.Y, out var la, out var ln);
                if (!InPlane(la, ln)) continue;
                var d = new Dictionary<string, object> { ["id"] = m.Id, ["name"] = NameOf(m), ["color"] = ColorOf(m), ["ll"] = new List<object> { R6(la), R6(ln) } };
                var h = m.Here;
                d["hz"] = h.HasValue ? ZOf(h.Value.HasZ, h.Value.Z, h.Value.Src) : ZOf(false, 0, ElevSrc.Terrain);
                d["hmv"] = m.Move & 0xFF;
                TrailParts(m.Trail, viewMapId, m.At == default(DateTime) ? DateTime.UtcNow : m.At, MaxMemberPoints, true, out var tl, out var el, out var mv, out var od);
                d["trail"] = tl; d["elev"] = el; d["mv"] = mv; d["od"] = od;
                // the prints: their race's foot; a hunter's pet beside them only while their move bits say it is out (relayed from their Elan's Hub)
                d["prints"] = new Dictionary<string, object> { ["foot"] = m.Foot == "hoof" || m.Foot == "bone" || m.Foot == "tiny" ? m.Foot : "paw", ["hunter"] = m.Hunter, ["petKnown"] = true };
                list.Add(d); drawn.Add(m.Id);
            }
            foreach (var m in all.OrderBy(m => NameOf(m), StringComparer.OrdinalIgnoreCase))
            {
                if (rows.Count >= MaxRows) break;
                bool here = drawn.Contains(m.Id);
                string map = "";
                if (m.MapId != viewMapId) { try { map = LivePositionText.CleanName(mapName?.Invoke(m.MapId) ?? ""); } catch (Exception) { map = ""; } }
                rows.Add(new Dictionary<string, object> { ["id"] = m.Id, ["name"] = NameOf(m), ["color"] = ColorOf(m), ["zone"] = LivePositionText.CleanName(m.Zone), ["here"] = here, ["map"] = map });
            }
            return Json.Write(new Dictionary<string, object> { ["t"] = "members", ["list"] = list, ["rows"] = rows });
        }
        static string NameOf(MemberDot m) { var n = LivePositionText.CleanName(m.Name); return n.Length > 0 ? n : "A member"; }
        static string ColorOf(MemberDot m) => m.Color != null && HexColor.IsMatch(m.Color) ? m.Color : "#E8E6E3";

        public static string Goto(double lat, double lng, double z, string pinName = null, string pinTip = null, int seq = -1)
        {
            var d = new Dictionary<string, object> { ["t"] = "goto", ["lat"] = lat, ["lng"] = lng, ["z"] = z };
            if (seq >= 0) d["s"] = seq;
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
            // the zone rectangles of the map tables are boxes that overlap along borders; the terrain's own area ids are exact per 33-yard chunk: when this
            // continent's terrain has been read, the zone the spot is really in decides which zone map the command names
            try
            {
                if (WorldMapCache.TerrainBuilt(mapId) != null)
                {
                    var hit = WorldMapZones.ZoneAt(WorldMapCache.TerrainDir(WorldMapCache.BuildKey, mapId), x, y, ZoneResolver.FromIndex(idx));
                    if (hit != null && hit.UiMapId > 0) { var q = idx.WorldToMap(mapId, x, y, hit.UiMapId); if (q != null) p = q; }
                }
            }
            catch (Exception) { }
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
