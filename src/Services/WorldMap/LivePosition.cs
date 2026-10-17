using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ElansAddonHub.Services.Maps;

namespace ElansAddonHub.Services.WorldMap
{
    // ============================================================================================================================
    // "Show my live position on the world map" (Settings > General, off by default). The Elan's Hub companion draws your position on the zone map into
    // its status strip only when "Share my map position with Elan's Outpost" is on in the game (also off by default); the strip reader (PixelStrip /
    // GamePresence) hands it here. This class keeps the trail of the last 10 minutes IN MEMORY ONLY (never written to disk, never sent anywhere) and
    // says what the world map's live layer shows: your dot, a greyed dot ("Position unavailable here"), or a calm notice with a "How to turn it on" link.
    // It never touches the game: it only turns what the strip says into map coordinates.
    // ============================================================================================================================
    public enum LiveState
    {
        Off,          // the Outpost setting is off: no layer at all
        NotPlaying,   // WoW is not running / no character: nothing to show
        NotShared,    // a live strip is there but "Share my map position" is off in the game (or the strip itself is off)
        OldHub,       // the strip comes from an Elan's Hub that is too old to send positions
        Unavailable,  // sharing is on, but the game gives no position here (dungeon, raid, battleground ...)
        Ok,           // a position
    }

    // The texts of the live layer and of the how-to, in one place (the Lodge will reuse them for members later).
    public static class LivePositionText
    {
        public const string SettingLabel = "Show my live position on the world map";
        public const string SettingNote = "Needs the matching option in Elan's Hub in game. The trail keeps the last 10 minutes and is never saved (only the simplified line of a flight is kept, for Learned flight routes).";
        public const string NotShared = "Your position isn't shared from the game yet";
        public const string OldHub = "Your Elan's Hub is too old to share positions — update it";
        public const string Unavailable = "Position unavailable here";
        public const string HowToLink = "How to turn it on";
        public const string HowToTitle = "Share your map position";
        public const string HubSettingLabel = "Share my map position with Elan's Outpost";
        // the in-game setting's description, verbatim (ElansHub.lua EHUB.POSITION_DESC): what it really does
        public const string HubDescription = "Shows where you are on the Outpost's world map, with a short trail of the last few minutes and small marks on it for what happened there (a kill, a notable item looted, a herb, ore or skin gathered, a level up, a death, a quest turned in, a new area or flight path found). The Hub draws your map position and those marks into its small status strip in the corner of your screen, and the Outpost on this PC reads it from there, together with whether you are on a flight, on a mount, indoors or swimming and whether your pet is out (so its 3D map shows you at the right height and your trail can show paw and hoof prints). It is only shown on a map — nothing presses keys or plays the game for you, and nothing is sent anywhere (sharing with your Lodge does not exist yet; it will be a separate switch). Positions are not available in dungeons, raids and battlegrounds, so the trail pauses there.";
        public static readonly string[] HowToSteps =
        {
            "In game: type /ehub position on (or open Elan's Hub settings > Share my map position with Elan's Outpost).",
            "In the Outpost: Settings > General > Show my live position on the world map.",
        };

        // the notice for your own map in each state (null = no notice)
        public static string NoticeFor(LiveState s)
        {
            switch (s)
            {
                case LiveState.NotShared: return NotShared;
                case LiveState.OldHub: return OldHub;
                case LiveState.Unavailable: return Unavailable;
                default: return null;
            }
        }
        // whether the notice carries the "How to turn it on" link (only when something can be switched on)
        public static bool HasHowTo(LiveState s) => s == LiveState.NotShared || s == LiveState.OldHub;

        // for the Lodge (later): when someone looks at a member who has not switched sharing on. The name is made safe for one line.
        public static string MemberNotShared(string name)
        {
            var n = CleanName(name);
            return (n.Length > 0 ? n : "This member") + " hasn't turned on position sharing";
        }

        public static string CleanName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder();
            foreach (var ch in s) { if (char.IsControl(ch) || ch == '<' || ch == '>' || ch == '|') continue; sb.Append(ch); if (sb.Length >= 48) break; }
            return sb.ToString().Trim();
        }
    }

    // the trail: memory only, the last 10 minutes, at most one point a second, points closer than a few yards dropped
    public sealed class LiveTrail
    {
        // world position (continent map id, x north, y west, yards); Z / Src = the height worked out for the 3D view (LiveElevation), HasZ false = none
        public struct Point { public DateTime At; public int MapId; public double X, Y; public double Z; public ElevSrc Src; public bool HasZ; public int Move; public double Odo; }
        // Move (2.90, the trail's prints): how you moved there (LiveMove bits: flight, indoors, swimming, mounted, pet out, dead); Odo = yards walked along the trail
        // so far (set by Add: the prints are placed at fixed distances of it, so they stay put while old points drop off)
        public static readonly TimeSpan Keep = TimeSpan.FromMinutes(10);
        public const double MinSeconds = 1.0, MinYards = 3.0;
        public const int MaxPoints = 600;                           // 10 minutes at one a second
        readonly List<Point> pts = new List<Point>();
        // the events on the trail (companion 1.16.0+): where and when, what (kind + the game's id), never saved; they go with the trail
        public struct Mark { public DateTime At; public int MapId; public double X, Y; public TrailKind Kind; public int Id, Extra; public double Z; public ElevSrc Src; public bool HasZ; }
        public const int MaxMarks = 200;
        readonly List<Mark> marks = new List<Mark>();
        string owner = "";
        readonly object gate = new object();

        public int Count { get { lock (gate) return pts.Count; } }
        public string Owner { get { lock (gate) return owner; } }

        // false = dropped (too soon, too close, or another character, which starts a new trail first)
        public bool Add(string charKey, DateTime at, int mapId, double x, double y) => Add(charKey, new Point { At = at, MapId = mapId, X = x, Y = y });
        public bool Add(string charKey, Point p)
        {
            DateTime at = p.At; int mapId = p.MapId; double x = p.X, y = p.Y;
            if (double.IsNaN(x + y) || double.IsInfinity(x + y)) return false;
            lock (gate)
            {
                charKey = charKey ?? "";
                if (charKey != owner) { pts.Clear(); marks.Clear(); owner = charKey; }
                Prune(at);
                if (pts.Count > 0)
                {
                    var l = pts[pts.Count - 1];
                    if ((at - l.At).TotalSeconds < MinSeconds) return false;
                    if (l.MapId == mapId && Dist(l.X, l.Y, x, y) < MinYards) return false;
                }
                p.Odo = TrailPrints.NextOdo(pts.Count > 0 ? pts[pts.Count - 1] : (Point?)null, p);
                pts.Add(p);
                while (pts.Count > MaxPoints) pts.RemoveAt(0);
                return true;
            }
        }

        // the trail belongs to this character from now on; true = it was someone else's (or nobody's with points) and was cleared
        public bool Claim(string charKey)
        {
            lock (gate)
            {
                charKey = charKey ?? "";
                if (charKey == owner) return false;
                bool had = pts.Count > 0 || marks.Count > 0;
                pts.Clear(); marks.Clear(); owner = charKey;
                return had;
            }
        }

        public List<Point> Points(DateTime now) { lock (gate) { Prune(now); return pts.ToList(); } }
        public Point? Last { get { lock (gate) return pts.Count > 0 ? pts[pts.Count - 1] : (Point?)null; } }
        public void Clear() { lock (gate) { pts.Clear(); marks.Clear(); owner = ""; } }
        public int MarkCount { get { lock (gate) return marks.Count; } }
        public List<Mark> Marks(DateTime now) { lock (gate) { Prune(now); return marks.ToList(); } }

        // where you were at `at`: the last point at or before it (points are only added when you move, so you stood there since), else the first one
        // within a few seconds after it; null = the trail has no place for that moment
        public Point? PointAt(DateTime at)
        {
            lock (gate)
            {
                for (int i = pts.Count - 1; i >= 0; i--)
                    if (pts[i].At <= at.AddSeconds(1)) return at - pts[i].At <= Keep ? pts[i] : (Point?)null;
                if (pts.Count > 0 && pts[0].At - at <= TimeSpan.FromSeconds(10)) return pts[0];
                return null;
            }
        }

        public bool AddMark(Mark m, DateTime now)
        {
            if (double.IsNaN(m.X + m.Y) || double.IsInfinity(m.X + m.Y) || now - m.At > Keep) return false;
            lock (gate)
            {
                Prune(now);
                int i = marks.Count; while (i > 0 && marks[i - 1].At > m.At) i--;        // kept in time order (an event may arrive a little late)
                marks.Insert(i, m);
                while (marks.Count > MaxMarks) marks.RemoveAt(0);
                return true;
            }
        }

        void Prune(DateTime now)
        {
            int n = 0; while (n < pts.Count && now - pts[n].At > Keep) n++; if (n > 0) pts.RemoveRange(0, n);
            n = 0; while (n < marks.Count && now - marks[n].At > Keep) n++; if (n > 0) marks.RemoveRange(0, n);
        }
        public static double Dist(double x1, double y1, double x2, double y2) { double dx = x1 - x2, dy = y1 - y2; return Math.Sqrt(dx * dx + dy * dy); }
    }

    // What a trail event says on the map: a short title, a line, a colour (the item's quality for loot) and the Atlas page it links to. The Hub sends only
    // numbers; the names come from the Outpost's own game data (the Atlas: this client's files, what was seen, the Lodge, the Classic baseline).
    public static class TrailEventText
    {
        public struct Info { public string Key, Title, Line, Color, Link; }
        // hooks (tests set their own); null = the Atlas / the map tables
        public static Func<int, string> NpcName, ItemName, QuestTitle;
        public static Func<int, int> ItemQuality;
        public static Func<int, string> AreaByHash;                       // the AreaTable name whose hash is this (see AreaHash)
        public static Func<int, double, double, string> FlightMasterNear; // continent, world x, y -> the flight master's name within 120 yards

        public static string[] QualityColors => WorldMapLayers.EventQualityColors;
        static readonly string[] QualityNames = { "Poor", "Common", "Uncommon", "Rare", "Epic", "Legendary", "Artifact", "Heirloom" };
        public static string KeyOf(TrailKind k)
        {
            switch (k)
            {
                case TrailKind.Kill: return "kill"; case TrailKind.Loot: return "loot"; case TrailKind.Herb: return "herb"; case TrailKind.Ore: return "ore";
                case TrailKind.Skin: return "skin"; case TrailKind.Level: return "level"; case TrailKind.Died: return "died"; case TrailKind.Quest: return "quest";
                case TrailKind.Area: return "area"; case TrailKind.Flight: return "flight"; default: return "";
            }
        }
        static Dictionary<TrailKind, string> KindColors => WorldMapLayers.EventKindColors;

        // the same 24-bit hash of an area's name as the Hub's areaHash (ElansHub.lua): (h * 31 + byte) mod 16777213 over the UTF-8 bytes, 0 -> 1
        public static int AreaHash(string name)
        {
            long h = 0;
            foreach (var b in Encoding.UTF8.GetBytes(name ?? "")) h = (h * 31 + b) % 16777213;
            return h == 0 ? 1 : (int)h;
        }

        static string Safe(Func<string> f) { try { var s = f(); return string.IsNullOrWhiteSpace(s) ? null : LivePositionText.CleanName(s); } catch (Exception) { return null; } }
        static string Npc(int id) => id <= 0 ? null : Safe(() => NpcName != null ? NpcName(id) : Atlas.AtlasQuery.NpcName(id));
        static string Item(int id) => id <= 0 ? null : Safe(() => ItemName != null ? ItemName(id) : Atlas.AtlasQuery.ItemName(id));
        static string Quest(int id) => id <= 0 ? null : Safe(() => QuestTitle != null ? QuestTitle(id) : Atlas.AtlasQuery.QuestTitle(id));
        static string Area(int hash)
        {
            if (hash <= 0) return null;
            if (AreaByHash != null) return Safe(() => AreaByHash(hash));
            return Safe(() =>
            {
                var set = MapData.Index?.Set; if (set == null) return null;
                var hits = set.Areas.Where(a => !string.IsNullOrEmpty(a.Name) && AreaHash(a.Name) == hash).Select(a => a.Name).Distinct().ToList();
                return hits.Count == 1 ? hits[0] : null;                  // two names with the same number: say nothing rather than a wrong place
            });
        }
        static string Flight(int mapId, double x, double y)
        {
            if (FlightMasterNear != null) return Safe(() => FlightMasterNear(mapId, x, y));
            return Safe(() =>
            {
                var set = MapData.Index?.Set; if (set == null || !set.HasTaxi) return null;
                var n = set.TaxiNodes.Where(t => t.MapId == mapId && LiveTrail.Dist(t.X, t.Y, x, y) <= 120).OrderBy(t => LiveTrail.Dist(t.X, t.Y, x, y)).FirstOrDefault();
                return n?.Name;
            });
        }

        public static Info Describe(LiveTrail.Mark m)
        {
            var i = new Info { Key = KeyOf(m.Kind), Color = KindColors.TryGetValue(m.Kind, out var c) ? c : WorldMapLayers.EventFallbackColor, Line = "", Link = "" };
            string n;
            switch (m.Kind)
            {
                case TrailKind.Kill:
                    n = Npc(m.Id); i.Title = n != null ? "Killed " + n : "Killed a creature"; if (m.Id > 0) i.Link = "npc:" + m.Id; break;
                case TrailKind.Loot:
                    {
                        n = Item(m.Id); int q = m.Extra & 15; bool quest = (m.Extra & 16) != 0;
                        if (q > 7) q = -1;
                        i.Title = "Looted " + (n ?? "an item"); i.Line = quest ? "Quest item" : q >= 0 ? QualityNames[q] : "";
                        if (q >= 0) i.Color = QualityColors[q];
                        if (m.Id > 0) i.Link = "item:" + m.Id; break;
                    }
                case TrailKind.Herb: n = Item(m.Id); i.Title = n != null ? "Gathered " + n : "Gathered a herb"; i.Line = "Herbalism"; if (m.Id > 0) i.Link = "item:" + m.Id; break;
                case TrailKind.Ore: n = Item(m.Id); i.Title = n != null ? "Mined " + n : "Mined a vein"; i.Line = "Mining"; if (m.Id > 0) i.Link = "item:" + m.Id; break;
                case TrailKind.Skin: n = Item(m.Id); i.Title = n != null ? "Skinned " + n : "Skinned a creature"; i.Line = "Skinning"; if (m.Id > 0) i.Link = "item:" + m.Id; break;
                case TrailKind.Level: i.Title = m.Id > 0 ? "Reached level " + m.Id : "Level up"; break;
                case TrailKind.Died: i.Title = "Died"; break;
                case TrailKind.Quest: n = Quest(m.Id); i.Title = n != null ? "Completed " + n : "Completed a quest"; if (m.Id > 0) i.Link = "quest:" + m.Id; break;
                case TrailKind.Area: n = Area(m.Id); i.Title = n != null ? "Discovered " + n : "Discovered a new area"; break;
                case TrailKind.Flight: n = Flight(m.MapId, m.X, m.Y); i.Title = "New flight path"; i.Line = n ?? ""; break;
                default: i.Title = "Event"; break;
            }
            if (i.Link.Length > 0 && !WorldMapProtocol.IsOpenId(i.Link)) i.Link = "";
            return i;
        }
    }

    public sealed class LivePosition
    {
        public readonly LiveTrail Trail = new LiveTrail();
        public LiveState State { get; private set; } = LiveState.Off;
        public string Name { get; private set; } = "";
        public string ClassFile { get; private set; }
        public LiveTrail.Point? Here { get; private set; }          // the current world position (Ok only)
        public readonly TrailPrints.Owner Prints = new TrailPrints.Owner();     // 2.88: race and whether the Hub says if your pet is out, for the trail's prints
        public int UiMapId { get; private set; }
        public event Action Changed;
        // tests: the world position of a zone fraction (null = MapData.Index)
        public Func<int, double, double, WorldPoint?> ToWorld;
        // the height for the 3D view (2.82): the solver keeps a little memory (the last point, the flight under way), like the trail never saved.
        // Tests set their own world; null = the Outpost's terrain cache, buildings and flight routes (GameElevationWorld.Shared).
        IElevationWorld elevWorld; LiveElevation elev;
        public IElevationWorld ElevationWorld { get => elevWorld; set { elevWorld = value; elev = null; } }
        public ElevResult? LastElevForTest { get; private set; }
        string lastElevWhy;
        LiveElevation Elev => elev ?? (elev = new LiveElevation(elevWorld ?? GameElevationWorld.Shared));
        // learned flight paths (2.88): the line of each flight is recorded (memory) and its legs handed to the store (null = LearnedFlights.Shared)
        public readonly FlightRecorder Recorder = new FlightRecorder();
        public LearnedFlights Learned;
        void Record(LiveTrail.Point pt, ElevResult? e, GamePresence.Character c, DateTime now)
        {
            try
            {
                if (!c.PosMoveKnown) { Recorder.Break("the Hub sends no flight bit"); return; }
                bool taxi = (c.PosMove & LiveMove.Taxi) != 0;
                // the height is kept only where the solver had a route under the flight (else it is a guess over the ground)
                double z = e.HasValue && e.Value.Src == ElevSrc.Air && e.Value.RouteId != 0 && pt.HasZ ? pt.Z : double.NaN;
                var legs = Recorder.Sample(pt.MapId, pt.X, pt.Y, z, taxi, now);
                if (legs != null && legs.Count > 0) (Learned ?? LearnedFlights.Shared).Add(legs);
            }
            catch (Exception ex) { Util.Log("learned flights: " + ex.Message); }
        }

        // the newest strip state. enabled = the Outpost setting; c = the character shown (null = none)
        public void Update(bool enabled, bool playing, GamePresence.Character c, DateTime nowUtc)
        {
            var before = Sig();
            if (!enabled) { State = LiveState.Off; Trail.Clear(); Here = null; elev?.Reset(); Recorder.Break("live position off"); }
            else if (!playing || c == null || string.IsNullOrEmpty(c.Name)) { State = LiveState.NotPlaying; Trail.Clear(); Here = null; Name = ""; ClassFile = null; elev?.Reset(); Recorder.Break("not playing"); }
            else
            {
                var key = (c.Realm ?? "") + "-" + c.Name;
                if (Trail.Claim(key)) { Here = null; elev?.Reset(); Recorder.Break("another character"); }      // another character: a new trail
                Name = c.Name; ClassFile = c.ClassFile;
                if (!c.HasLive) State = LiveState.NotShared;               // no live strip (strip off in the game, or a Hub before 1.5)
                else if (!c.PosSupported) State = LiveState.OldHub;
                else if (!c.PosEnabled) State = LiveState.NotShared;
                else if (!c.PosValid) State = LiveState.Unavailable;
                else
                {
                    WorldPoint? w = null; ElevResult? er = null;
                    try { w = ToWorld != null ? ToWorld(c.PosMapId, c.PosFx, c.PosFy) : MapData.Index?.MapToWorld(c.PosMapId, c.PosFx, c.PosFy); } catch (Exception) { }
                    if (w == null) State = LiveState.Unavailable;              // a map the Outpost has no place for
                    else
                    {
                        State = LiveState.Ok; UiMapId = c.PosMapId;
                        var pt = new LiveTrail.Point { At = nowUtc, MapId = w.Value.MapId, X = w.Value.X, Y = w.Value.Y, Move = TrailPrints.MoveOf(c) };
                        Prints.Take(c);
                        try
                        {
                            var e = Elev.Solve(pt.MapId, pt.X, pt.Y, c.PosMoveKnown ? c.PosMove : 0, nowUtc);
                            if (!double.IsNaN(e.Z) && !double.IsInfinity(e.Z)) { pt.Z = e.Z; pt.Src = e.Src; pt.HasZ = true; }
                            LastElevForTest = e; er = e;
                            // one log line when what the height is based on changes (the Hub's move bits, a flight and its matched route), so a
                            // dot that stays on the ground on a flight can be explained from the log
                            var why = (c.PosMoveKnown ? "hub sends move bits " + c.PosMove : "hub sends no move bits (older Hub, or no /reload since the update)")
                                + ", height from " + e.Src + ((c.PosMove & LiveMove.Taxi) != 0 ? ", flight route " + (e.RouteId > 0 ? e.RouteId.ToString() : "none fitted") : "");
                            if (why != lastElevWhy) { lastElevWhy = why; Util.Log("live elevation: " + why + (string.IsNullOrEmpty(e.Why) ? "" : " (" + e.Why + ")")); }
                        }
                        catch (Exception ex) { Util.Log("live elevation: " + ex.Message); }
                        Here = pt;
                        Trail.Add(key, pt);
                        Record(pt, er, c, nowUtc);
                    }
                }
                if (State != LiveState.Ok) { Here = null; Recorder.Break("position unavailable"); }
            }
            if (Sig() != before) Changed?.Invoke();
        }

        string Sig() => State + "|" + Name + "|" + (Here.HasValue ? Here.Value.MapId + ":" + Here.Value.X + ":" + Here.Value.Y + ":" + Math.Round(Here.Value.Z, 1) : "-") + "|" + Trail.Count + "|" + Trail.MarkCount;

        // A trail event from the strip: put on the trail where you were at that moment. Only while the setting is on, a position is shown (Ok) and the
        // event comes from the character the trail belongs to; false = dropped (it has no place on the map). Memory only, like the trail.
        public int EventsDroppedForTest { get; private set; }
        public bool AddEvent(bool enabled, StripEvent ev, DateTime nowUtc)
        {
            if (!enabled || ev == null || State != LiveState.Ok || !StripEvent.KnownKind((int)ev.Kind) || !string.Equals(ev.Who ?? "", Name ?? "", StringComparison.OrdinalIgnoreCase)) { EventsDroppedForTest++; return false; }
            var at = ev.At > nowUtc ? nowUtc : ev.At;
            var p = Trail.PointAt(at);
            if (p == null && Here.HasValue && (Here.Value.At - at).Duration() <= TimeSpan.FromSeconds(10)) p = Here;
            if (p == null) { EventsDroppedForTest++; return false; }
            var before = Sig();
            bool ok = Trail.AddMark(new LiveTrail.Mark { At = at, MapId = p.Value.MapId, X = p.Value.X, Y = p.Value.Y, Kind = ev.Kind, Id = ev.Id, Extra = ev.Extra, Z = p.Value.Z, Src = p.Value.Src, HasZ = p.Value.HasZ }, nowUtc);
            if (!ok) { EventsDroppedForTest++; return false; }
            if (Sig() != before) Changed?.Invoke();
            return true;
        }

        static readonly Dictionary<string, string> ClassColors = new Dictionary<string, string>
        {
            ["HUNTER"] = "#ABD473", ["WARRIOR"] = "#C79C6E", ["MAGE"] = "#69CCF0", ["ROGUE"] = "#FFF569", ["DRUID"] = "#FF7D0A",
            ["PALADIN"] = "#F58CBA", ["PRIEST"] = "#FFFFFF", ["SHAMAN"] = "#0070DE", ["WARLOCK"] = "#9482C9",
            ["DEATHKNIGHT"] = "#C41F3B", ["MONK"] = "#00FF96", ["DEMONHUNTER"] = "#A330C9", ["EVOKER"] = "#33937F",
        };
        public static string ColorOf(string classFile) => classFile != null && ClassColors.TryGetValue(classFile.ToUpperInvariant(), out var c) ? c : "#E8E6E3";
    }
}
