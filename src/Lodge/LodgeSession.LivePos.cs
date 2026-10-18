using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Maps;
using ElansAddonHub.Services.WorldMap;

namespace ElansAddonHub.Lodge
{
    // Live map positions between members (Lodge server 2.30, feature "liveposition"; Outpost 2.97). Opt-in and off by default:
    //   out  position {mapId, fx, fy, move, valid, scope} about once a second while "Share my map position with the Lodge" is on, the game shares a
    //        position (Elan's Hub's own opt-in) and my zone is shared; {share:false} the moment any of that stops. Never from a guest.
    //   in   memberpos {id, mapId, fx, fy, move, valid} - where a member is (the server decides who may see whom: scope, rank, Appear offline)
    // Nothing of it is ever written to disk; the world map draws the members' dots (MainWindow.Atlas.cs -> WorldMapView.MemberDots).
    public partial class LodgeSession
    {
        public bool SupportsLivePosition { get; private set; }
        public event Action LivePositionsChanged;                       // a member's dot moved, came or went
        public static readonly TimeSpan PosInterval = TimeSpan.FromSeconds(1);
        DispatcherTimer posTimer;
        string lastPosJson;                                             // the last position message sent on this connection (null = none yet)
        DateTime lastPosSent = DateTime.MinValue;
        bool posWired;
        public int PositionsSentForTest { get; private set; }

        public static string ScopeOf(Settings s) => s?.LivePosScope == "officers" ? "officers" : "everyone";

        void ReadLivePosFeatures(List<string> features)
        {
            SupportsLivePosition = features.Contains("liveposition");
            WireLivePos();
            lastPosJson = null; lastPosSent = DateTime.MinValue;       // a new connection starts with nothing shared
            bool had = false;
            foreach (var m in Members) had |= m.ClearLivePos();         // the server sends the positions I may see right after the welcome
            if (had) LivePositionsChanged?.Invoke();
        }

        void ResetLivePos()
        {
            SupportsLivePosition = false; lastPosJson = null;
            posTimer?.Stop();
            LivePositionsChanged?.Invoke();
        }

        void WireLivePos()
        {
            if (posWired) return;
            posWired = true;
            posTimer = new DispatcherTimer { Interval = PosInterval };
            posTimer.Tick += (s, e) => { posTimer.Stop(); SendPosition(); };
            // the strip's position (and whether I play at all) changed: sent at most about once a second, the newest wins
            Presence.PositionChanged += () => { if (!posTimer.IsEnabled) posTimer.Start(); };
            Presence.Changed += () => { if (!posTimer.IsEnabled) posTimer.Start(); };
        }

        // what would be shared now; null = nothing (sharing is off, or there is no position to share)
        public bool LivePosWanted => SupportsLivePosition && Settings.ShareLivePosWithLodge && !Settings.ShareGameOff && !Settings.ShareZoneOff
            && Me != null && !Me.Guest && MyRole != "guest";

        // 2.97.1: why nothing is shared right now, in plain words (the log says it once per change)
        public string LivePosWhy()
        {
            if (!SupportsLivePosition) return "the Lodge server does not support it (needs Lodge 2.30)";
            if (!Settings.ShareLivePosWithLodge) return "the switch is off";
            if (Settings.ShareGameOff || Settings.ShareZoneOff) return "your game or zone is not shared (Settings > Privacy)";
            if (Me == null || Me.Guest || MyRole == "guest") return "guests can't share it";
            var c = Presence.Current;
            if (!Presence.Playing || c == null) return "not in the game";
            if (!c.PosEnabled) return "Elan's Hub in the game does not share your map position (/ehub position on)";
            if (!c.PosValid || c.PosMapId <= 0) return "no map position right now (an instance, a loading screen)";
            return "";
        }
        string posNote = "";
        void NotePos(string note) { if (note == posNote) return; posNote = note; Util.Log("lodge position: " + note); }

        public Dictionary<string, object> BuildPositionMessage()
        {
            if (!LivePosWanted) return new Dictionary<string, object> { ["t"] = "position", ["share"] = false };
            var c = Presence.Current;
            if (!Presence.Playing || c == null || !c.PosEnabled || !c.PosValid || c.PosMapId <= 0 || double.IsNaN(c.PosFx + c.PosFy) || double.IsInfinity(c.PosFx + c.PosFy))
                return new Dictionary<string, object> { ["t"] = "position", ["valid"] = false };      // in a dungeon, or the game shares no position: the dot goes
            return new Dictionary<string, object>
            {
                ["t"] = "position", ["mapId"] = c.PosMapId, ["fx"] = Math.Round(Math.Max(0, Math.Min(1, c.PosFx)), 4), ["fy"] = Math.Round(Math.Max(0, Math.Min(1, c.PosFy)), 4),
                ["move"] = c.PosMove & 0xFC, ["valid"] = true, ["scope"] = ScopeOf(Settings),
            };
        }

        // a new position at most once per PosInterval (the timer brings the newest); turning it off goes out at once. Identical messages are skipped,
        // and a connection that never shared sends nothing at all.
        public void SendPosition()
        {
            if (!SupportsLivePosition || (!Online && !TestFed)) return;
            var obj = BuildPositionMessage();
            bool valid = obj.ContainsKey("mapId");
            NotePos(valid ? "sharing my map position with the Lodge (" + ScopeOf(Settings) + ")" : Settings.ShareLivePosWithLodge ? "not sharing: " + LivePosWhy() : "not sharing (switched off)");
            if (!valid && lastPosJson == null) return;                  // never shared on this connection: nothing to take back
            var json = Json.Write(obj);
            if (json == lastPosJson) return;
            var wait = PosInterval - (Clock() - lastPosSent);
            if (valid && lastPosJson != null && lastPosJson.Contains("\"mapId\"") && wait > TimeSpan.Zero)
            {
                if (posTimer != null && !posTimer.IsEnabled) { posTimer.Interval = wait; posTimer.Start(); }
                return;
            }
            if (posTimer != null) posTimer.Interval = PosInterval;
            lastPosJson = valid ? json : null;                          // after a withdrawal there is nothing left to take back
            lastPosSent = Clock();
            PositionsSentForTest++;
            _ = Send(obj);
        }

        void OnMemberPos(Dictionary<string, object> m)
        {
            var mem = Members.FirstOrDefault(x => x.Id == m.Int("id"));
            if (mem == null) return;
            bool was = mem.LiveValid;
            bool changed = m.Bool("valid") && m.TryGetValue("fx", out var fo) && fo is double fx && m.TryGetValue("fy", out var yo) && yo is double fy
                ? mem.SetLivePos(true, m.Int("mapId"), fx, fy, m.Int("move")) : mem.ClearLivePos();
            if (!mem.IsMe && was != mem.LiveValid)
                Util.Log("lodge position: " + (mem.LiveValid ? "receiving " + mem.Display + "'s map position (map " + mem.LiveMapId + ")" : mem.Display + " stopped sharing their map position with me"));
            if (changed) LivePositionsChanged?.Invoke();
        }

        // the dot of a member who left, or who I can no longer see (Appear offline) goes at once
        void DropLivePos(MemberVM mem)
        {
            if (mem != null && mem.ClearLivePos()) LivePositionsChanged?.Invoke();
        }

        // the members' dots for the world map: in world coordinates (continent map id, yards), never mine. toWorld: tests (null = the map tables).
        readonly HashSet<int> noPlaceSaid = new HashSet<int>();
        void NoPlace(int mapId) { if (noPlaceSaid.Add(mapId)) Util.Log("lodge position: no place on the world map for map " + mapId + " (a member's dot there is not drawn)"); }

        public List<MemberDot> MemberDots(Func<int, double, double, WorldPoint?> toWorld = null)
        {
            var list = new List<MemberDot>();
            if (!SupportsLivePosition) return list;
            foreach (var m in Members.ToList())
            {
                if (m.IsMe || !m.LiveValid || m.Guest) continue;
                if (m.Invisible && !CanSeeInvisible) continue;
                WorldPoint? w = null;
                try { w = toWorld != null ? toWorld(m.LiveMapId, m.LiveFx, m.LiveFy) : MapData.Index?.MapToWorld(m.LiveMapId, m.LiveFx, m.LiveFy); } catch (Exception) { }
                if (w == null) { NoPlace(m.LiveMapId); continue; }      // a map this Outpost has no place for: no dot rather than a wrong one
                list.Add(new MemberDot { Id = m.Id, Name = m.Display, Color = LivePosition.ColorOf(m.GameClassFile), MapId = w.Value.MapId, X = w.Value.X, Y = w.Value.Y });
            }
            return list;
        }
    }
}
