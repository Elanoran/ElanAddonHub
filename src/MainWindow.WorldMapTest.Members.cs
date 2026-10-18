using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ElansAddonHub.Services;
using ElansAddonHub.Services.WorldMap;

namespace ElansAddonHub
{
    // --selftest "worldmap", part 6b (2.97): the Lodge members' dots on the world map (Lodge 2.30 "liveposition"), next to the live layer's own tests
    // (MainWindow.WorldMapTest.Live.cs). The "members" message, what the session sends (opt-in, scope, about once a second, taken back at once) and keeps,
    // and the page: dots, names, Follow this member, a member who leaves, goes invisible or stops sharing.
    public partial class MainWindow
    {
        async Task<Dictionary<string, object>> WmMembers(string args = "")
        {
            var r = await WmEval("(function(){try{return window.wm.members(" + args + ")}catch(e){return JSON.stringify({err:e.message})}})()");
            var s = Json.Parse(r ?? "null") as string;
            return s == null ? null : Json.Obj(s);
        }
        async Task<Dictionary<string, object>> WmMembersWait(Func<Dictionary<string, object>, bool> cond, int ms = 6000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew(); Dictionary<string, object> last = null;
            while (sw.ElapsedMilliseconds < ms) { try { last = await WmMembers(); if (last != null && cond(last)) return last; } catch (Exception) { } await Task.Delay(100); }
            return last;
        }
        static string MembersText(Dictionary<string, object> m) => m == null ? "null" : string.Join(" ", m.Select(kv => kv.Key + "=" + (kv.Value is List<object> l ? "[" + string.Join(",", l) + "]" : kv.Value)));

        static string MemberWelcome(string myRole, bool guest, bool feature) =>
            "{\"t\":\"welcome\",\"you\":1,\"name\":\"Elan\",\"role\":\"" + myRole + "\",\"server\":\"" + (feature ? "2.30.0" : "2.29.0") + "\",\"features\":[\"reply\",\"react\"" + (feature ? ",\"liveposition\"" : "") + "],"
            + "\"channels\":[{\"id\":\"general\",\"name\":\"General\",\"type\":\"text\",\"minRole\":\"guest\"}],\"history\":{\"general\":[]},\"pins\":{},\"users\":["
            + "{\"id\":1,\"name\":\"Elan\",\"role\":\"" + myRole + "\",\"guest\":" + (guest ? "true" : "false") + ",\"game\":{\"playing\":true,\"name\":\"Elan\",\"classFile\":\"HUNTER\",\"zone\":\"Westmark Fields\"}},"
            + "{\"id\":2,\"name\":\"Tess\",\"role\":\"member\",\"game\":{\"playing\":true,\"name\":\"Tess\",\"classFile\":\"MAGE\",\"zone\":\"Westmark Fields\"}},"
            + "{\"id\":3,\"name\":\"<b>Bad|Guy</b>\",\"role\":\"member\",\"game\":{\"playing\":true,\"name\":\"Bad\",\"classFile\":\"WARRIOR\",\"zone\":\"Westmark Fields\"}},"
            + "{\"id\":4,\"name\":\"Mira\",\"role\":\"member\",\"invisible\":true},"
            + "{\"id\":5,\"name\":\"Rover (guest)\",\"role\":\"guest\",\"guest\":true}]}";

        static string MemberPos(int id, double fx, double fy, int mapId = 11, bool valid = true) =>
            valid ? "{\"t\":\"memberpos\",\"id\":" + id + ",\"mapId\":" + mapId + ",\"fx\":" + fx.ToString(CultureInfo.InvariantCulture) + ",\"fy\":" + fy.ToString(CultureInfo.InvariantCulture) + ",\"move\":0,\"valid\":true}"
                  : "{\"t\":\"memberpos\",\"id\":" + id + ",\"valid\":false}";

        // pure checks: the "members" message, the page -> app "mfollow", the texts
        void WorldMapMembersUnitTest(CheckFn Check)
        {
            var dots = new List<MemberDot>
            {
                new MemberDot { Id = 2, Name = "Tess", Color = "#69CCF0", MapId = 0, X = 500, Y = 1000 },
                new MemberDot { Id = 3, Name = "<b>Bad|Guy</b>\n", Color = "red;}", MapId = 0, X = 520, Y = 1000 },
                new MemberDot { Id = 3, Name = "Again", Color = "#FFFFFF", MapId = 0, X = 530, Y = 1000 },        // the same member twice: once
                new MemberDot { Id = 6, Name = "Far", Color = "#FFFFFF", MapId = 1, X = 500, Y = 1000 },           // another continent: never on this map
                new MemberDot { Id = 7, Name = "Out", Color = "#FFFFFF", MapId = 0, X = 99999, Y = 99999 },        // outside the plane
                new MemberDot { Id = 0, Name = "Nobody", Color = "#FFFFFF", MapId = 0, X = 500, Y = 1000 },        // no id
                new MemberDot { Id = 8, Name = "", Color = null, MapId = 0, X = 510, Y = 1000 },
            };
            var mm = Json.Obj(WorldMapProtocol.Members(dots, 0));
            var list = (mm["list"] as List<object>).Cast<Dictionary<string, object>>().ToList();
            Check("members: only this map's members inside the plane, once each, names cleaned (no markup, no line break), a bad colour replaced, a nameless one \"A member\"",
                mm.Str("t") == "members" && list.Count == 3 && list[0].Int("id") == 2 && list[0].Str("name") == "Tess" && list[0].Str("color") == "#69CCF0"
                && list[1].Str("name") == "bBadGuy/b" && list[1].Str("color") == "#E8E6E3" && list[2].Str("name") == "A member" && (list[0]["ll"] as List<object>).Count == 2, Json.Write(mm));
            var many = Enumerable.Range(1, 300).Select(i => new MemberDot { Id = i, Name = "M" + i, Color = "#FFFFFF", MapId = 0, X = 500, Y = 1000 }).ToList();
            Check("members: never more than " + WorldMapProtocol.MaxMembers + " dots; none (null) is an empty list that takes every dot away",
                (Json.Obj(WorldMapProtocol.Members(many, 0))["list"] as List<object>).Count == WorldMapProtocol.MaxMembers && (Json.Obj(WorldMapProtocol.Members(null, 0))["list"] as List<object>).Count == 0);
            Check("protocol: mfollow carries a member id (0 = stop), anything else is refused",
                WorldMapProtocol.Parse("{\"t\":\"mfollow\",\"id\":7}", out _)?.MemberId == 7 && WorldMapProtocol.Parse("{\"t\":\"mfollow\",\"id\":0}", out _)?.Type == WorldMapMessageType.MemberFollow
                && WorldMapProtocol.Parse("{\"t\":\"mfollow\",\"id\":-1}", out _) == null && WorldMapProtocol.Parse("{\"t\":\"mfollow\",\"id\":1.5}", out _) == null && WorldMapProtocol.Parse("{\"t\":\"mfollow\"}", out _) == null);
            Check("texts: the Lodge switch and its one-line note (off by default, everyone or officers, never guests, Appear offline hides it); the Hub's description no longer says Lodge sharing \"does not exist yet\"",
                LivePositionText.LodgeSettingLabel == "Share my map position with the Lodge" && LivePositionText.LodgeSettingNote.StartsWith("Off by default.") && LivePositionText.LodgeSettingNote.Contains("never guests")
                && LivePositionText.LodgeSettingNote.Contains("Appear offline") && LivePositionText.HubDescription.Contains("sharing with your Lodge is a separate switch in the Outpost, off until you turn it on")
                && !LivePositionText.HubDescription.Contains("does not exist yet"));
        }

        // the session (what goes out, what comes in) and the page: dots, names, Follow this member, a member who leaves or goes invisible
        async Task WorldMapMembersTest(CheckFn Check, Func<string, string> P)
        {
            var s = settings;
            bool savedOn = s.WorldMapLivePos, savedShare = s.ShareLivePosWithLodge, savedZoneOff = s.ShareZoneOff, savedGameOff = s.ShareGameOff, savedFollow = s.WorldMapFollow; var savedScope = s.LivePosScope;
            var savedClock = Session.Clock; var view = WorldMapView;
            var sent = new List<Dictionary<string, object>>();
            try
            {
                var t = DateTime.UtcNow; Session.Clock = () => t;
                s.WorldMapLivePos = true; s.WorldMapFollow = false; s.ShareLivePosWithLodge = false; s.LivePosScope = null; s.ShareZoneOff = false; s.ShareGameOff = false;
                Session.Disconnect();
                Session.SentForTest = o => { if (o.Str("t") == "position") sent.Add(Json.Obj(Json.Write(o))); };      // as the Lodge reads it
                Session.FeedForTest(MemberWelcome("member", false, true));
                await Task.Delay(300);
                Check("session: a Lodge 2.30 announces liveposition, and the Settings row can be used", Session.SupportsLivePosition && SettingsPage.LodgePosRow.IsEnabled);

                // ---- what goes out
                Session.Presence.SetPositionForTest(LiveChar(fx: 0.5, fy: 0.5), true);
                sent.Clear(); Session.SendPosition();
                Check("send: with the switch off nothing goes out (not even a 'stop': nothing was ever shared)", sent.Count == 0, sent.Count + "");
                s.ShareLivePosWithLodge = true; Session.SendPosition();
                var p1 = sent.LastOrDefault();
                Check("send: on - the map, the fractions, valid and the scope 'everyone'",
                    sent.Count == 1 && p1.Int("mapId") == 11 && Math.Abs((double)p1["fx"] - 0.5) < 1e-9 && p1.Bool("valid") && p1.Str("scope") == "everyone", p1 == null ? "none" : Json.Write(p1));
                Session.SendPosition();
                Check("send: an unchanged position is not sent again", sent.Count == 1, sent.Count + "");
                t = t.AddSeconds(0.3); Session.Presence.SetPositionForTest(LiveChar(fx: 0.52, fy: 0.5), true); Session.SendPosition();
                Check("send: a new position within a second waits (about once a second)", sent.Count == 1, sent.Count + "");
                t = t.AddSeconds(1); Session.SendPosition();
                Check("send: ... and goes out after it", sent.Count == 2 && Math.Abs((double)sent[1]["fx"] - 0.52) < 1e-9, sent.Count + "");
                t = t.AddSeconds(1.2); SettingsPage.LodgePosForTest(true, true);
                Check("send: Officers only (the Settings row) goes out with the scope 'officers'", sent.Count == 3 && sent[2].Str("scope") == "officers" && s.LivePosScope == "officers", sent.Count + "");
                s.ShareZoneOff = true; Session.SendPosition();
                Check("send: sharing my zone off takes the position back at once (share:false)", sent.Count == 4 && sent[3].ContainsKey("share") && !sent[3].Bool("share") && !sent[3].ContainsKey("mapId"), sent.Count + "");
                s.ShareZoneOff = false; t = t.AddSeconds(2); Session.SendPosition();
                Session.Presence.SetPositionForTest(LiveChar(valid: false), true); Session.SendPosition();
                Check("send: in a dungeon (no position from the game) the dot goes (valid:false)", sent.Count == 6 && !sent[5].Bool("valid") && !sent[5].ContainsKey("mapId"), sent.Count + "");
                Session.Presence.SetPositionForTest(LiveChar(fx: 0.5, fy: 0.5), true); t = t.AddSeconds(2); Session.SendPosition();
                int before = sent.Count;
                t = t.AddSeconds(0.1); SettingsPage.LodgePosForTest(false, true);
                Check("send: switching it off goes out at once, even within the second (share:false)", sent.Count == before + 1 && !sent.Last().Bool("share") && !s.ShareLivePosWithLodge, sent.Count + "");
                Session.SendPosition();
                Check("send: ... once", sent.Count == before + 1, sent.Count + "");
                s.ShareLivePosWithLodge = false; s.LivePosScope = null;
                UpdateLive();                                       // my own dot is on the map too (its Follow me is checked against the member's below)

                // ---- what comes in
                foreach (var f in new[] { MemberPos(2, 0.45, 0.5), MemberPos(3, 0.55, 0.52), MemberPos(4, 0.5, 0.5), MemberPos(5, 0.5, 0.5), MemberPos(1, 0.5, 0.5), MemberPos(66, 0.5, 0.5) }) Session.FeedForTest(f);
                var md = Session.MemberDots();
                Check("receive: dots for the members who share - never mine, a guest's, an invisible member's or an unknown id's",
                    md.Count == 2 && md.Any(d => d.Id == 2 && d.Name == "Tess" && d.Color == "#69CCF0") && md.Any(d => d.Id == 3) && md.All(d => d.MapId == 0), string.Join(",", md.Select(d => d.Id + ":" + d.Name + "@" + d.MapId)));
                var by = Session.Members.ToDictionary(m => m.Id);
                Check("receive: the member keeps where they are (memory only)", by[2].LiveValid && by[2].LiveMapId == 11 && Math.Abs(by[2].LiveFx - 0.45) < 1e-9);

                // ---- the page
                view.PushMembers(true);
                var w1 = await WmMembersWait(m => m.Int("n") == 2);
                var names = w1 == null || !(w1["names"] is List<object> nl) ? new List<string>() : nl.Cast<string>().ToList();
                Check("page: one dot per sharing member in their class colour, the names made safe",
                    w1 != null && w1.Bool("layer") && w1.Int("n") == 2 && names.Contains("Tess") && names.Contains("bBadGuy/b") && (w1["colors"] as List<object>).Contains("#69CCF0"), MembersText(w1));
                var wt = await WmMembers("null, 2");
                Check("page: pointing at a dot names the member", wt != null && (wt.Str("tip") ?? "").Contains("Tess") && wt.Str("tip").Contains("Click to follow"), wt?.Str("tip"));
                var wf = await WmMembers("2");
                await WaitFor(() => view.FollowingMember == 2, 3000);
                var wf2 = await WmMembersWait(m => m.Bool("centered"));
                Check("page: \"Follow this member\" (the dot's menu) centres the map on them and tells the app",
                    wf != null && wf.Str("menu") == "Follow this member" && wf.Int("following") == 2 && view.FollowingMember == 2 && wf2 != null && wf2.Bool("centered"), MembersText(wf));
                if (SelfTestClock.ShotsOn) { await Task.Delay(400); var png = await view.CaptureForTest(); if (png != null) File.WriteAllBytes(P("worldmap-members.png"), png); }
                Session.FeedForTest(MemberPos(2, 0.62, 0.47));
                await Task.Delay(1300);
                var wm2 = await WmMembersWait(m => m.Bool("centered") && m.Int("following") == 2, 4000);
                Check("page: ... and keeps them in the middle when they move", wm2 != null && wm2.Bool("centered") && wm2.Int("following") == 2 && view.LastMembersForTest.Contains("\"id\":2"), MembersText(wm2));
                await WmMe("follow");
                var wm3 = await WmMembersWait(m => m.Int("following") == 0, 3000);
                Check("page: Follow me stops following the member (one thing at a time)", wm3 != null && wm3.Int("following") == 0 && await WaitFor(() => view.FollowingMember == 0, 3000), MembersText(wm3));
                await WmMe("follow"); await WaitFor(() => !s.WorldMapFollow, 3000);
                await WmMembers("2");
                await WaitFor(() => view.FollowingMember == 2, 3000);
                Session.FeedForTest("{\"t\":\"leave\",\"id\":2}");
                var wl = await WmMembersWait(m => m.Int("n") == 1, 4000);
                Check("page: a member who leaves loses their dot at once, and following them stops", wl != null && wl.Int("n") == 1 && wl.Int("following") == 0 && !Session.MemberDots().Any(d => d.Id == 2), MembersText(wl));
                Session.FeedForTest("{\"t\":\"user\",\"user\":{\"id\":3,\"name\":\"<b>Bad|Guy</b>\",\"role\":\"member\",\"invisible\":true}}");
                var wi = await WmMembersWait(m => m.Int("n") == 0, 4000);
                Check("page: a member who goes invisible draws nothing", wi != null && wi.Int("n") == 0 && Session.MemberDots().Count == 0, MembersText(wi));
                Session.FeedForTest("{\"t\":\"user\",\"user\":{\"id\":3,\"name\":\"<b>Bad|Guy</b>\",\"role\":\"member\",\"game\":{\"playing\":true,\"classFile\":\"WARRIOR\",\"zone\":\"Westmark Fields\"}}}");
                Session.FeedForTest(MemberPos(3, 0.55, 0.52));
                await WmMembersWait(m => m.Int("n") == 1, 4000);
                Session.FeedForTest(MemberPos(3, 0.55, 0.52, valid: false));
                var wv = await WmMembersWait(m => m.Int("n") == 0, 4000);
                Check("page: valid:false (they stopped sharing) takes the dot away", wv != null && wv.Int("n") == 0 && Session.MemberDots().Count == 0, MembersText(wv));
                Session.FeedForTest(MemberPos(3, 0.5, 0.5, mapId: 9999));
                Check("page: a position on a map this Outpost has no place for draws nothing (never a wrong dot)", Session.MemberDots().Count == 0);
                Session.FeedForTest(MemberPos(3, 0.55, 0.52));
                await WmMembersWait(m => m.Int("n") == 1, 4000);
                s.WorldMapLivePos = false; view.PushMembers(true);
                var wo = await WmMembersWait(m => m.Int("n") == 0, 4000);
                Check("page: with \"Show my live position on the world map\" off there are no member dots either", wo != null && wo.Int("n") == 0, MembersText(wo));
                s.WorldMapLivePos = true;

                // ---- an older Lodge, a guest: nothing is shared, nothing shown
                s.ShareLivePosWithLodge = true;
                Session.Disconnect(); sent.Clear();
                Session.FeedForTest(MemberWelcome("member", false, false));
                Session.FeedForTest(MemberPos(2, 0.45, 0.5));
                t = t.AddSeconds(5); Session.SendPosition();
                Check("an older Lodge (no liveposition): nothing is sent, no dots, and the Settings row is greyed out", !Session.SupportsLivePosition && sent.Count == 0 && Session.MemberDots().Count == 0 && !SettingsPage.LodgePosRow.IsEnabled, sent.Count + "");
                Session.Disconnect();
                Session.FeedForTest(MemberWelcome("guest", true, true));
                t = t.AddSeconds(5); Session.SendPosition();
                Check("a guest never sends a position (the Settings row is greyed out for guests too)", sent.Count == 0 && !SettingsPage.LodgePosRow.IsEnabled, sent.Count + "");
            }
            finally
            {
                Session.SentForTest = null; Session.Clock = savedClock;
                s.WorldMapLivePos = savedOn; s.ShareLivePosWithLodge = savedShare; s.ShareZoneOff = savedZoneOff; s.ShareGameOff = savedGameOff; s.WorldMapFollow = savedFollow; s.LivePosScope = savedScope;
                try { Session.Disconnect(); view.PushMembers(true); } catch (Exception) { }
            }
        }
    }
}
