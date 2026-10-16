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
        public const string SettingNote = "Needs the matching option in Elan's Hub in game. The trail keeps the last 10 minutes and is never saved.";
        public const string NotShared = "Your position isn't shared from the game yet";
        public const string OldHub = "Your Elan's Hub is too old to share positions — update it";
        public const string Unavailable = "Position unavailable here";
        public const string HowToLink = "How to turn it on";
        public const string HowToTitle = "Share your map position";
        public const string HubSettingLabel = "Share my map position with Elan's Outpost";
        // the in-game setting's description, verbatim (ElansHub.lua EHUB.POSITION_DESC): what it really does
        public const string HubDescription = "Shows where you are on the Outpost's world map, with a short trail of the last few minutes. The Hub draws your map position into its small status strip in the corner of your screen, and the Outpost on this PC reads it from there. It is only shown on a map — nothing presses keys or plays the game for you, and nothing is sent anywhere unless you also turn on sharing with your Lodge. Positions are not available in dungeons, raids and battlegrounds, so the trail pauses there.";
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
        public struct Point { public DateTime At; public int MapId; public double X, Y; }   // world position (continent map id, x north, y west, yards)
        public static readonly TimeSpan Keep = TimeSpan.FromMinutes(10);
        public const double MinSeconds = 1.0, MinYards = 3.0;
        public const int MaxPoints = 600;                           // 10 minutes at one a second
        readonly List<Point> pts = new List<Point>();
        string owner = "";
        readonly object gate = new object();

        public int Count { get { lock (gate) return pts.Count; } }
        public string Owner { get { lock (gate) return owner; } }

        // false = dropped (too soon, too close, or another character, which starts a new trail first)
        public bool Add(string charKey, DateTime at, int mapId, double x, double y)
        {
            if (double.IsNaN(x + y) || double.IsInfinity(x + y)) return false;
            lock (gate)
            {
                charKey = charKey ?? "";
                if (charKey != owner) { pts.Clear(); owner = charKey; }
                Prune(at);
                if (pts.Count > 0)
                {
                    var l = pts[pts.Count - 1];
                    if ((at - l.At).TotalSeconds < MinSeconds) return false;
                    if (l.MapId == mapId && Dist(l.X, l.Y, x, y) < MinYards) return false;
                }
                pts.Add(new Point { At = at, MapId = mapId, X = x, Y = y });
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
                bool had = pts.Count > 0;
                pts.Clear(); owner = charKey;
                return had;
            }
        }

        public List<Point> Points(DateTime now) { lock (gate) { Prune(now); return pts.ToList(); } }
        public Point? Last { get { lock (gate) return pts.Count > 0 ? pts[pts.Count - 1] : (Point?)null; } }
        public void Clear() { lock (gate) { pts.Clear(); owner = ""; } }

        void Prune(DateTime now) { int n = 0; while (n < pts.Count && now - pts[n].At > Keep) n++; if (n > 0) pts.RemoveRange(0, n); }
        public static double Dist(double x1, double y1, double x2, double y2) { double dx = x1 - x2, dy = y1 - y2; return Math.Sqrt(dx * dx + dy * dy); }
    }

    public sealed class LivePosition
    {
        public readonly LiveTrail Trail = new LiveTrail();
        public LiveState State { get; private set; } = LiveState.Off;
        public string Name { get; private set; } = "";
        public string ClassFile { get; private set; }
        public LiveTrail.Point? Here { get; private set; }          // the current world position (Ok only)
        public int UiMapId { get; private set; }
        public event Action Changed;
        // tests: the world position of a zone fraction (null = MapData.Index)
        public Func<int, double, double, WorldPoint?> ToWorld;

        // the newest strip state. enabled = the Outpost setting; c = the character shown (null = none)
        public void Update(bool enabled, bool playing, GamePresence.Character c, DateTime nowUtc)
        {
            var before = Sig();
            if (!enabled) { State = LiveState.Off; Trail.Clear(); Here = null; }
            else if (!playing || c == null || string.IsNullOrEmpty(c.Name)) { State = LiveState.NotPlaying; Trail.Clear(); Here = null; Name = ""; ClassFile = null; }
            else
            {
                var key = (c.Realm ?? "") + "-" + c.Name;
                if (Trail.Claim(key)) Here = null;                         // another character: a new trail
                Name = c.Name; ClassFile = c.ClassFile;
                if (!c.HasLive) State = LiveState.NotShared;               // no live strip (strip off in the game, or a Hub before 1.5)
                else if (!c.PosSupported) State = LiveState.OldHub;
                else if (!c.PosEnabled) State = LiveState.NotShared;
                else if (!c.PosValid) State = LiveState.Unavailable;
                else
                {
                    WorldPoint? w = null;
                    try { w = ToWorld != null ? ToWorld(c.PosMapId, c.PosFx, c.PosFy) : MapData.Index?.MapToWorld(c.PosMapId, c.PosFx, c.PosFy); } catch (Exception) { }
                    if (w == null) State = LiveState.Unavailable;              // a map the Outpost has no place for
                    else
                    {
                        State = LiveState.Ok; UiMapId = c.PosMapId;
                        Here = new LiveTrail.Point { At = nowUtc, MapId = w.Value.MapId, X = w.Value.X, Y = w.Value.Y };
                        Trail.Add(key, nowUtc, w.Value.MapId, w.Value.X, w.Value.Y);
                    }
                }
                if (State != LiveState.Ok) Here = null;
            }
            if (Sig() != before) Changed?.Invoke();
        }

        string Sig() => State + "|" + Name + "|" + (Here.HasValue ? Here.Value.MapId + ":" + Here.Value.X + ":" + Here.Value.Y : "-") + "|" + Trail.Count;

        static readonly Dictionary<string, string> ClassColors = new Dictionary<string, string>
        {
            ["HUNTER"] = "#ABD473", ["WARRIOR"] = "#C79C6E", ["MAGE"] = "#69CCF0", ["ROGUE"] = "#FFF569", ["DRUID"] = "#FF7D0A",
            ["PALADIN"] = "#F58CBA", ["PRIEST"] = "#FFFFFF", ["SHAMAN"] = "#0070DE", ["WARLOCK"] = "#9482C9",
            ["DEATHKNIGHT"] = "#C41F3B", ["MONK"] = "#00FF96", ["DEMONHUNTER"] = "#A330C9", ["EVOKER"] = "#33937F",
        };
        public static string ColorOf(string classFile) => classFile != null && ClassColors.TryGetValue(classFile.ToUpperInvariant(), out var c) ? c : "#E8E6E3";
    }
}
