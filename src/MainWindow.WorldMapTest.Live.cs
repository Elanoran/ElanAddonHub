using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Maps;
using ElansAddonHub.Services.WorldMap;

namespace ElansAddonHub
{
    // --selftest "worldmap", part 6 (2.74): the live position layer ("Show my live position on the world map"). The trail (memory only, 10 minutes,
    // deduped), the notices (not shared / old Hub / unavailable here), the "me" message both ways, the page's dot + trail + Follow me, the how-to card.
    public partial class MainWindow
    {
        async Task<Dictionary<string, object>> WmMe(string click = null)
        {
            var r = await WmEval("window.wm && window.wm.me ? window.wm.me(" + (click == null ? "" : "'" + click + "'") + ") : null");
            var s = Json.Parse(r ?? "null") as string;
            return s == null ? null : Json.Obj(s);
        }
        async Task<Dictionary<string, object>> WmMeWait(Func<Dictionary<string, object>, bool> cond, int ms = 8000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew(); Dictionary<string, object> last = null;
            while (sw.ElapsedMilliseconds < ms) { try { last = await WmMe(); if (last != null && cond(last)) return last; } catch (Exception) { } await Task.Delay(100); }
            return last;
        }
        static string MeText(Dictionary<string, object> m) => m == null ? "null" : string.Join(" ", m.Select(kv => kv.Key + "=" + kv.Value));

        static GamePresence.Character LiveChar(bool live = true, bool supported = true, bool enabled = true, bool valid = true, double fx = 0.5, double fy = 0.5, string name = "Elan") =>
            new GamePresence.Character { Name = name, Realm = "Realm", ClassFile = "HUNTER", Class = "Hunter", Level = 60, HasLive = live, PosSupported = supported, PosEnabled = enabled, PosValid = valid, PosMapId = valid ? 11 : 0, PosFx = valid ? fx : 0, PosFy = valid ? fy : 0 };

        // pure checks (no page): texts, trail, the message shapes
        void WorldMapLiveUnitTest(CheckFn Check)
        {
            // texts: the Lodge helper (later) and the how-to
            Check("live texts: the member notice for the Lodge (later) names the member, a missing name says \"This member\", and markup / line breaks never get through",
                LivePositionText.MemberNotShared("Elan") == "Elan hasn't turned on position sharing" && LivePositionText.MemberNotShared(null) == "This member hasn't turned on position sharing"
                && LivePositionText.MemberNotShared("  ") == "This member hasn't turned on position sharing" && LivePositionText.MemberNotShared("<b>Bad|r\nGuy</b>") == "bBadrGuy/b hasn't turned on position sharing",
                LivePositionText.MemberNotShared("<b>Bad|r\nGuy</b>"));
            Check("live texts: the notices for your own map, verbatim, and the how-to link only where something can be switched on",
                LivePositionText.NoticeFor(LiveState.NotShared) == "Your position isn't shared from the game yet" && LivePositionText.NoticeFor(LiveState.OldHub) == "Your Elan's Hub is too old to share positions — update it"
                && LivePositionText.NoticeFor(LiveState.Unavailable) == "Position unavailable here" && LivePositionText.NoticeFor(LiveState.Ok) == null && LivePositionText.HowToLink == "How to turn it on"
                && LivePositionText.HasHowTo(LiveState.NotShared) && LivePositionText.HasHowTo(LiveState.OldHub) && !LivePositionText.HasHowTo(LiveState.Unavailable) && !LivePositionText.HasHowTo(LiveState.Ok));
            Check("live texts: the how-to steps and the Hub's own description (verbatim, the same text as in the addon)",
                LivePositionText.HowToSteps.Length == 2 && LivePositionText.HowToSteps[0] == "In game: type /ehub position on (or open Elan's Hub settings > Share my map position with Elan's Outpost)."
                && LivePositionText.HowToSteps[1] == "In the Outpost: Settings > General > Show my live position on the world map."
                && LivePositionText.HubDescription.StartsWith("Shows where you are on the Outpost's world map, with a short trail of the last few minutes and small marks on it") && LivePositionText.HubDescription.EndsWith("so the trail pauses there.")
                && AddonLuaHas(LivePositionText.HubDescription.Replace("—", "\\226\\128\\148")));

            // the trail: 10 minutes, at most one point a second, points within 3 yards dropped, another character starts again
            var t = new LiveTrail(); var t0 = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
            bool a1 = t.Add("R-Elan", t0, 0, 100, 100), a2 = t.Add("R-Elan", t0.AddSeconds(0.5), 0, 150, 150), a3 = t.Add("R-Elan", t0.AddSeconds(2), 0, 101, 101), a4 = t.Add("R-Elan", t0.AddSeconds(3), 0, 110, 100);
            Check("trail: the first point is kept, one 0.5 s later is dropped (at most one a second), one 1.4 yards away is dropped (a few yards), a real step is kept", a1 && !a2 && !a3 && a4 && t.Count == 2, t.Count + "");
            for (int i = 1; i <= 700; i++) t.Add("R-Elan", t0.AddSeconds(3 + i), 0, 110 + i * 5, 100);
            var pts = t.Points(t0.AddSeconds(703));
            Check("trail: only the last 10 minutes are kept (" + pts.Count + " points, the oldest " + (t0.AddSeconds(703) - pts[0].At).TotalSeconds + " s old), never more than 600",
                pts.Count <= LiveTrail.MaxPoints && pts.Count >= 595 && (t0.AddSeconds(703) - pts[0].At) <= LiveTrail.Keep && pts.All(p => p.At > t0.AddSeconds(3)));
            Check("trail: points older than 10 minutes go even without new points", t.Points(t0.AddSeconds(703 + 601)).Count == 0);
            t.Add("R-Elan", t0.AddSeconds(2000), 0, 0, 0); bool other = t.Add("R-Other", t0.AddSeconds(2001), 0, 0, 0);
            Check("trail: another character starts a new trail", other && t.Count == 1 && t.Owner == "R-Other");

            // the state from the strip: off / not playing / no live strip / old Hub / off in game / instance / valid
            var lp = new LivePosition { ToWorld = (ui, fx, fy) => ui == 11 ? new WorldPoint(0, 1000 - fy * 1000, 2000 - fx * 1000) : (WorldPoint?)null };
            var now = DateTime.UtcNow;
            lp.Update(false, true, LiveChar(), now); var sOff = lp.State;
            lp.Update(true, false, LiveChar(), now); var sNone = lp.State;
            lp.Update(true, true, LiveChar(live: false), now); var sNoStrip = lp.State;
            lp.Update(true, true, LiveChar(supported: false, enabled: false, valid: false), now); var sOld = lp.State;
            lp.Update(true, true, LiveChar(enabled: false, valid: false), now); var sNotShared = lp.State;
            lp.Update(true, true, LiveChar(valid: false), now); var sInst = lp.State;
            lp.Update(true, true, LiveChar(), now); var sOk = lp.State;
            Check("live state: setting off -> Off, not playing -> NotPlaying, no live strip -> NotShared, an old Hub -> OldHub, off in the game -> NotShared, in an instance -> Unavailable (not the off notice), a position -> Ok",
                sOff == LiveState.Off && sNone == LiveState.NotPlaying && sNoStrip == LiveState.NotShared && sOld == LiveState.OldHub && sNotShared == LiveState.NotShared && sInst == LiveState.Unavailable && sOk == LiveState.Ok && lp.Here.HasValue && lp.Trail.Count == 1,
                string.Join(",", sOff, sNone, sNoStrip, sOld, sNotShared, sInst, sOk));
            lp.Update(true, true, LiveChar(valid: true, fx: 0.6), now.AddSeconds(2));
            int before = lp.Trail.Count;
            lp.Update(true, true, LiveChar(name: "Other"), now.AddSeconds(4));
            Check("live state: a character change clears the trail (" + before + " -> " + lp.Trail.Count + ")", before == 2 && lp.Trail.Count == 1);
            lp.Update(true, false, null, now.AddSeconds(5));
            Check("live state: leaving the game clears the trail", lp.Trail.Count == 0 && lp.State == LiveState.NotPlaying);
            lp.Update(true, true, LiveChar(), now.AddSeconds(6)); lp.Update(false, true, LiveChar(), now.AddSeconds(8));
            Check("live state: switching the setting off clears the trail at once", lp.Trail.Count == 0 && lp.State == LiveState.Off);
            lp.Update(true, true, new GamePresence.Character { Name = "Elan", HasLive = true, PosSupported = true, PosEnabled = true, PosValid = true, PosMapId = 9999, PosFx = .5, PosFy = .5 }, now.AddSeconds(9));
            Check("live state: a valid position on a map the Outpost has no place for is 'unavailable', never a wrong dot", lp.State == LiveState.Unavailable && !lp.Here.HasValue);

            // the messages: page -> app howto / follow, app -> page me
            Check("protocol: howto is accepted, follow needs a true / false", WorldMapProtocol.Parse("{\"t\":\"howto\"}", out _)?.Type == WorldMapMessageType.HowTo
                && WorldMapProtocol.Parse("{\"t\":\"follow\",\"on\":true}", out _)?.Ok == true && WorldMapProtocol.Parse("{\"t\":\"follow\",\"on\":\"yes\"}", out var fr) == null && fr != null);
            var trail = new List<LiveTrail.Point>
            {
                new LiveTrail.Point { At = now.AddSeconds(-30), MapId = 0, X = 500, Y = 1000 },
                new LiveTrail.Point { At = now.AddSeconds(-20), MapId = 0, X = 510, Y = 1000 },
                new LiveTrail.Point { At = now.AddSeconds(-15), MapId = 1, X = 510, Y = 1000 },          // another continent: left out, the line breaks
                new LiveTrail.Point { At = now.AddSeconds(-10), MapId = 0, X = 99999, Y = 99999 },     // outside the plane: left out
                new LiveTrail.Point { At = now.AddSeconds(-5), MapId = 0, X = 520, Y = 1000 },
                new LiveTrail.Point { At = now.AddSeconds(-4), MapId = 0, X = 900, Y = 1000 },         // 380 yards in a second: a jump, the line breaks
            };
            var me = Json.Obj(WorldMapProtocol.Me(LiveState.Ok, "Elan\n<x>", "red;}", trail[5], trail, 0, now));
            var tr = (me["trail"] as List<object>).Cast<double>().ToList();
            Check("protocol: me (ok) - a clean name, a bad colour replaced, only this continent's points inside the plane, a break after a gap or a jump, ages in seconds",
                me.Str("state") == "ok" && me.Str("name") == "Elanx" && me.Str("color") == "#E8E6E3" && tr.Count == 16 && tr[3] == 1 && tr[7] == 0 && tr[11] == 1 && tr[15] == 1 && Math.Abs(tr[2] - 30) < 0.2 && me.ContainsKey("here"),
                string.Join(",", tr));
            var big = Enumerable.Range(0, 900).Select(i => new LiveTrail.Point { At = now.AddSeconds(-900 + i), MapId = 0, X = 500 + i, Y = 1000 }).ToList();
            var meBig = Json.Obj(WorldMapProtocol.Me(LiveState.Ok, "Elan", "#ABD473", big[899], big, 0, now));
            Check("protocol: me never carries more than 600 points", (meBig["trail"] as List<object>).Count == 600 * 4 && meBig.Str("color") == "#ABD473");
            var meEl = Json.Obj(WorldMapProtocol.Me(LiveState.Ok, "Elan", "#ABD473", trail[2], trail, 0, now, "Southland"));
            var meUn = Json.Obj(WorldMapProtocol.Me(LiveState.Unavailable, "Elan", "#ABD473", null, trail, 0, now));
            var meNs = Json.Obj(WorldMapProtocol.Me(LiveState.NotShared, "Elan", "#ABD473", null, null, 0, now));
            var meOld = Json.Obj(WorldMapProtocol.Me(LiveState.OldHub, "Elan", "#ABD473", null, null, 0, now));
            var meOff = Json.Obj(WorldMapProtocol.Me(LiveState.Off, "Elan", "#ABD473", trail[0], trail, 0, now));
            Check("protocol: me on another continent says where you are; unavailable greys out at the last point with \"Position unavailable here\" and no link; not shared / old Hub carry their notice and the how-to link; off carries nothing",
                meEl.Str("state") == "elsewhere" && meEl.Str("notice") == "You are on another map: Southland" && !meEl.ContainsKey("here")
                && meUn.Str("state") == "unavailable" && meUn.Str("notice") == "Position unavailable here" && meUn.ContainsKey("last") && !meUn.Bool("howto")
                && meNs.Str("notice") == "Your position isn't shared from the game yet" && meNs.Bool("howto") && meNs.Str("howtoText") == "How to turn it on"
                && meOld.Str("notice") == LivePositionText.OldHub && meOld.Bool("howto") && meOff.Count == 2 && meOff.Str("state") == "off");

            WorldMapLiveEventsUnitTest(Check);
            WorldMapLiveElevationUnitTest(Check);

            // the how-to card (not shown here): the steps and the honest description
            var card = HelpPages.Create(HelpPages.LivePosition);
            Check("how-to card: opened by its page id (" + HelpPages.LivePosition + "), titled \"" + card?.TitleForTest + "\", with the two steps and the Hub's description verbatim; an unknown id opens nothing",
                card != null && card.TitleForTest == "Share your map position" && card.StepsForTest.SequenceEqual(LivePositionText.HowToSteps) && card.AboutForTest == LivePositionText.HubDescription
                && HelpPages.Create("help:nope") == null && !HelpPages.Open("help:nope"));
            card?.Close();
        }

        // the names the tests give the event marks (the real ones come from the Atlas); restored by EventHooks(false)
        static void EventHooks(bool on)
        {
            if (!on) { TrailEventText.NpcName = TrailEventText.ItemName = TrailEventText.QuestTitle = TrailEventText.AreaByHash = null; TrailEventText.ItemQuality = null; TrailEventText.FlightMasterNear = null; return; }
            TrailEventText.NpcName = id => id == 6 ? "Kobold Vermin" : null;
            TrailEventText.ItemName = id => id == 774 ? "Malachite" : id == 2447 ? "Peacebloom" : id == 5056 ? "Kobold Candle" : null;
            TrailEventText.QuestTitle = id => id == 783 ? "A Threat Within" : null;
            TrailEventText.AreaByHash = h => h == TrailEventText.AreaHash("Westmark Fields") ? "Westmark Fields" : null;
            TrailEventText.FlightMasterNear = (m, x, y) => "Greenvale Post";
        }
        static StripEvent Ev(TrailKind k, int id, DateTime at, int extra = 0, string who = "Elan") => new StripEvent { Kind = k, Id = id, Extra = extra, At = at, Who = who, Seq = 1 };

        // the events on the trail without a page: where they land, when they are dropped, what they say, the "me" message
        void WorldMapLiveEventsUnitTest(CheckFn Check)
        {
            EventHooks(true);
            try
            {
                Check("events: the area hash is the Hub's (Goldshire = 9837803 in both, see companion_test.py), never 0", TrailEventText.AreaHash("Goldshire") == 9837803 && TrailEventText.AreaHash("") == 1, TrailEventText.AreaHash("Goldshire") + "");
                var lp = new LivePosition { ToWorld = (ui, fx, fy) => ui == 11 ? new WorldPoint(0, 1000 - fy * 1000, 2000 - fx * 1000) : (WorldPoint?)null };
                var t0 = DateTime.UtcNow.AddMinutes(-5);
                lp.Update(true, true, LiveChar(fx: 0.40), t0);
                lp.Update(true, true, LiveChar(fx: 0.45), t0.AddSeconds(30));          // 50 yards on
                lp.Update(true, true, LiveChar(fx: 0.50), t0.AddSeconds(60));
                int changes = 0; lp.Changed += () => changes++;
                bool k1 = lp.AddEvent(true, Ev(TrailKind.Kill, 6, t0.AddSeconds(40)), t0.AddSeconds(62));
                var mk = lp.Trail.Marks(t0.AddSeconds(62));
                Check("events: a kill 40 s in is put where you were then (the point from 30 s: x 1550, 50 yards before the dot), the map is told", k1 && mk.Count == 1 && Math.Abs(mk[0].Y - 1550) < 0.01 && mk[0].Kind == TrailKind.Kill && changes == 1,
                    mk.Count > 0 ? mk[0].X + "," + mk[0].Y : "none");
                bool k2 = lp.AddEvent(true, Ev(TrailKind.Loot, 774, t0.AddSeconds(61), 3), t0.AddSeconds(62));
                bool off = lp.AddEvent(false, Ev(TrailKind.Died, 0, t0.AddSeconds(61)), t0.AddSeconds(62));
                bool other = lp.AddEvent(true, Ev(TrailKind.Died, 0, t0.AddSeconds(61), who: "Someone"), t0.AddSeconds(62));
                bool unknown = lp.AddEvent(true, new StripEvent { Kind = (TrailKind)42, Who = "Elan", At = t0.AddSeconds(61) }, t0.AddSeconds(62));
                bool tooOld = lp.AddEvent(true, Ev(TrailKind.Died, 0, t0.AddMinutes(-20)), t0.AddSeconds(62));
                Check("events: a loot just now goes on the current point; dropped with the setting off, from another character, of an unknown kind, or from before the trail",
                    k2 && !off && !other && !unknown && !tooOld && lp.Trail.MarkCount == 2 && lp.Trail.Marks(t0.AddSeconds(62))[1].Y == 1500, lp.Trail.MarkCount + "");
                lp.Update(true, true, LiveChar(valid: false), t0.AddSeconds(70));
                bool inInst = lp.AddEvent(true, Ev(TrailKind.Died, 0, t0.AddSeconds(70)), t0.AddSeconds(71));
                Check("events: inside an instance (no position) an event has no place on the map: dropped", !inInst && lp.Trail.MarkCount == 2);
                lp.Update(true, true, LiveChar(fx: 0.5), t0.AddSeconds(72));
                Check("events: the marks are memory only and go after 10 minutes like the trail", lp.Trail.Marks(t0.AddSeconds(40 + 601)).Count == 1 && lp.Trail.Marks(t0.AddSeconds(62 + 601)).Count == 0);
                var lp2 = new LivePosition { ToWorld = lp.ToWorld };
                lp2.Update(true, true, LiveChar(), DateTime.UtcNow); lp2.AddEvent(true, Ev(TrailKind.Died, 0, DateTime.UtcNow), DateTime.UtcNow);
                int had = lp2.Trail.MarkCount;
                lp2.Update(true, true, LiveChar(name: "Other"), DateTime.UtcNow.AddSeconds(2));
                int afterChar = lp2.Trail.MarkCount;
                lp2.Update(true, true, LiveChar(name: "Other"), DateTime.UtcNow.AddSeconds(3)); lp2.AddEvent(true, Ev(TrailKind.Died, 0, DateTime.UtcNow.AddSeconds(3), who: "Other"), DateTime.UtcNow.AddSeconds(3));
                lp2.Update(false, true, LiveChar(name: "Other"), DateTime.UtcNow.AddSeconds(4));
                Check("events: a character change and switching the setting off forget the marks with the trail", had == 1 && afterChar == 0 && lp2.Trail.MarkCount == 0);
                var many = new LiveTrail(); var n0 = DateTime.UtcNow;
                many.Add("k", n0, 0, 0, 0);
                for (int i = 0; i < 260; i++) many.AddMark(new LiveTrail.Mark { At = n0.AddSeconds(i), MapId = 0, X = i, Y = 0, Kind = TrailKind.Kill, Id = 6 }, n0.AddSeconds(i));
                Check("events: at most " + LiveTrail.MaxMarks + " marks are kept (the newest)", many.MarkCount == LiveTrail.MaxMarks && many.Marks(n0.AddSeconds(260))[0].X == 60);

                // what the marks say (names from the Outpost's data, never from the game)
                LiveTrail.Mark M(TrailKind k, int id, int extra = 0) => new LiveTrail.Mark { At = DateTime.UtcNow, MapId = 0, X = 500, Y = 1000, Kind = k, Id = id, Extra = extra };
                var dk = TrailEventText.Describe(M(TrailKind.Kill, 6)); var dk0 = TrailEventText.Describe(M(TrailKind.Kill, 0)); var dkU = TrailEventText.Describe(M(TrailKind.Kill, 99));
                var dl = TrailEventText.Describe(M(TrailKind.Loot, 774, 3)); var dq = TrailEventText.Describe(M(TrailKind.Loot, 5056, 1 + 16)); var dlU = TrailEventText.Describe(M(TrailKind.Loot, 774, 15));
                var dh = TrailEventText.Describe(M(TrailKind.Herb, 2447)); var dh0 = TrailEventText.Describe(M(TrailKind.Ore, 0)); var dsk = TrailEventText.Describe(M(TrailKind.Skin, 0));
                var dv = TrailEventText.Describe(M(TrailKind.Level, 61)); var dd = TrailEventText.Describe(M(TrailKind.Died, 0)); var dqu = TrailEventText.Describe(M(TrailKind.Quest, 783));
                var da = TrailEventText.Describe(M(TrailKind.Area, TrailEventText.AreaHash("Westmark Fields"))); var da0 = TrailEventText.Describe(M(TrailKind.Area, 12345)); var df = TrailEventText.Describe(M(TrailKind.Flight, 0));
                Check("events: the texts - killed / looted (quality colour and name, quest item) / gathered / level / died / quest / discovered / flight path, with a link to the Atlas page where there is one",
                    dk.Title == "Killed Kobold Vermin" && dk.Link == "npc:6" && dk.Key == "kill" && dk0.Title == "Killed a creature" && dk0.Link == "" && dkU.Title == "Killed a creature" && dkU.Link == "npc:99"
                    && dl.Title == "Looted Malachite" && dl.Line == "Rare" && dl.Color == "#0070DD" && dl.Link == "item:774" && dq.Title == "Looted Kobold Candle" && dq.Line == "Quest item" && dq.Color == "#FFFFFF" && dlU.Line == ""
                    && dh.Title == "Gathered Peacebloom" && dh.Key == "herb" && dh0.Title == "Mined a vein" && dh0.Link == "" && dsk.Title == "Skinned a creature"
                    && dv.Title == "Reached level 61" && dd.Title == "Died" && dqu.Title == "Completed A Threat Within" && dqu.Link == "quest:783"
                    && da.Title == "Discovered Westmark Fields" && da0.Title == "Discovered a new area" && df.Title == "New flight path" && df.Line == "Greenvale Post",
                    string.Join(" | ", new[] { dk, dl, dq, dh, dv, dqu, da, df }.Select(i => i.Title + "/" + i.Line + "/" + i.Color + "/" + i.Link)));
                TrailEventText.NpcName = id => "<b>Bad</b>\nname|r";
                var bad = TrailEventText.Describe(M(TrailKind.Kill, 6));
                TrailEventText.NpcName = id => throw new InvalidOperationException("no data");
                var thr = TrailEventText.Describe(M(TrailKind.Kill, 6));
                Check("events: a name with markup or line breaks is cleaned, a lookup that fails says \"a creature\" (never an error)", bad.Title == "Killed bBad/bnamer" && thr.Title == "Killed a creature", bad.Title + " / " + thr.Title);
                EventHooks(true);

                // the "me" message carries them: this continent only, inside the plane, ages, at most 200; off carries nothing
                var now = DateTime.UtcNow;
                var tr = new List<LiveTrail.Point> { new LiveTrail.Point { At = now.AddSeconds(-30), MapId = 0, X = 500, Y = 1000 }, new LiveTrail.Point { At = now, MapId = 0, X = 520, Y = 1000 } };
                var marks = new List<LiveTrail.Mark>
                {
                    new LiveTrail.Mark { At = now.AddSeconds(-30), MapId = 0, X = 500, Y = 1000, Kind = TrailKind.Kill, Id = 6 },
                    new LiveTrail.Mark { At = now.AddSeconds(-20), MapId = 1, X = 500, Y = 1000, Kind = TrailKind.Loot, Id = 774, Extra = 2 },     // another continent
                    new LiveTrail.Mark { At = now.AddSeconds(-10), MapId = 0, X = 99999, Y = 99999, Kind = TrailKind.Died },                        // outside the plane
                    new LiveTrail.Mark { At = now.AddSeconds(-5), MapId = 0, X = 520, Y = 1000, Kind = TrailKind.Loot, Id = 774, Extra = 4 },
                };
                var me = Json.Obj(WorldMapProtocol.Me(LiveState.Ok, "Elan", "#ABD473", tr[1], tr, 0, now, null, marks));
                var evs = (me["events"] as List<object>).OfType<Dictionary<string, object>>().ToList();
                Check("protocol: me carries the events of this continent inside the plane (2 of 4): kind, name, line, colour, link, age, local time",
                    evs.Count == 2 && evs[0].Str("k") == "kill" && evs[0].Str("name") == "Killed Kobold Vermin" && evs[0].Str("link") == "npc:6" && Math.Abs(Convert.ToDouble(evs[0]["age"]) - 30) < 0.5
                    && evs[1].Str("k") == "loot" && evs[1].Str("c") == "#A335EE" && evs[1].Str("line") == "Epic" && evs[1].Str("at").Length == 5, Json.Write(evs));
                var meOff = Json.Obj(WorldMapProtocol.Me(LiveState.Off, "Elan", "#ABD473", tr[1], tr, 0, now, null, marks));
                var meNone = Json.Obj(WorldMapProtocol.Me(LiveState.Ok, "Elan", "#ABD473", tr[1], tr, 0, now));
                var big = Enumerable.Range(0, 300).Select(i => new LiveTrail.Mark { At = now.AddSeconds(-i), MapId = 0, X = 500, Y = 1000, Kind = TrailKind.Died }).ToList();
                var meBig = Json.Obj(WorldMapProtocol.Me(LiveState.Ok, "Elan", "#ABD473", tr[1], tr, 0, now, null, big));
                Check("protocol: off carries no events, no marks = an empty list, never more than 200", !meOff.ContainsKey("events") && (meNone["events"] as List<object>).Count == 0 && (meBig["events"] as List<object>).Count == LiveTrail.MaxMarks);
                Check("layers: \"Events on my trail\" is a live row (drawn from the live position, never built by the app), on by default and switched on once for a saved list",
                    WorldMapLayers.Catalog().Any(d => (string)d["id"] == "trailevents" && d.ContainsKey("live") && (bool)d["live"] && (string)d["title"] == "Events on my trail") && WorldMapLayers.DefaultOn.Contains("trailevents")
                    && WorldMapIds.IsLayerId("trailevents") && WorldMapLayers.WithNewDefaults(new List<string> { "zones" }, 4).Contains("trailevents") && !WorldMapLayers.WithNewDefaults(new List<string> { "zones" }, 5).Contains("trailevents"));
            }
            finally { EventHooks(false); }
        }

        // every file of the data folder (but the log and the browser's own profile) with its size and time
        static Dictionary<string, string> DataFilesForTest()
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var f in Directory.EnumerateFiles(Util.DataDir, "*", SearchOption.AllDirectories))
                {
                    var rel = f.Substring(Util.DataDir.Length).TrimStart('\\');
                    if (rel.StartsWith("webview2", StringComparison.OrdinalIgnoreCase) || rel.EndsWith(".log", StringComparison.OrdinalIgnoreCase)) continue;
                    try { var fi = new FileInfo(f); d[rel] = fi.Length + "@" + fi.LastWriteTimeUtc.Ticks; } catch (Exception) { }
                }
            }
            catch (Exception) { }
            return d;
        }

        static bool AddonLuaHas(string text)
        {
            try
            {
                var dir = AppDomain.CurrentDomain.BaseDirectory;
                for (int i = 0; i < 6 && dir != null; i++, dir = Path.GetDirectoryName(dir))
                {
                    var f = Path.Combine(dir, "addon", "ElansHub", "ElansHub.lua");
                    if (File.Exists(f)) return File.ReadAllText(f).Replace("\"\r\n  .. \"", "").Replace("\"\n  .. \"", "").Contains(text);
                }
            }
            catch (Exception) { }
            return true;   // no repository next to the exe (a release build elsewhere): nothing to compare with
        }

        // the page: the layer only with the setting on, the dot + trail, Follow me, the notices with the how-to link, the grey dot in an instance
        async Task WorldMapLiveTest(CheckFn Check, Func<string, string> P)
        {
            bool savedOn = settings.WorldMapLivePos, savedFollow = settings.WorldMapFollow;
            var savedLayers = settings.WorldMapLayers?.ToList(); var savedLayersV = settings.WorldMapLayersVersion;
            var savedHook = HelpPages.TestHook; var opened = new List<string>();
            try
            {
                HelpPages.TestHook = id => opened.Add(id);
                var lp = LivePos; lp.ToWorld = null;
                var view = WorldMapView;
                view.Live = lp;
                // setting off: no layer at all
                settings.WorldMapLivePos = false; settings.WorldMapFollow = false;
                lp.Update(false, true, LiveChar(), DateTime.UtcNow); view.PushLive(true);
                var m0 = await WmMeWait(m => m.Str("state") == "off");
                Check("live layer: with the setting off there is no layer, no dot and no card on the map", m0 != null && m0.Str("state") == "off" && !m0.Bool("layer") && !m0.Bool("dot") && !m0.Bool("panel"), MeText(m0));

                // on, walking: a dot in the class colour with a heading arrow, and the trail
                settings.WorldMapLivePos = true;
                var start = DateTime.UtcNow.AddSeconds(-400);
                var filesBefore = DataFilesForTest();
                for (int i = 0; i <= 40; i++) lp.Update(true, true, LiveChar(fx: 0.40 + i * 0.004, fy: 0.45 + (i % 20) * 0.002), start.AddSeconds(i * 10));
                var filesAfter = DataFilesForTest();
                var touched = filesAfter.Where(kv => !filesBefore.TryGetValue(kv.Key, out var v) || v != kv.Value).Select(kv => kv.Key).ToList();
                view.PushLive(true);
                var m1 = await WmMeWait(m => m.Str("state") == "ok" && m.Bool("dot"));
                Check("live layer: on and walking - your dot (with a heading arrow) and the trail are drawn (" + MeText(m1) + ")",
                    m1 != null && m1.Bool("layer") && m1.Bool("dot") && !m1.Bool("grey") && m1.Bool("arrow") && m1.Int("lines") >= 2 && m1.Int("points") >= 30 && m1.Str("notice") == "" && m1.Str("link") == "" && m1.Bool("followBtn"), MeText(m1));
                Check("live layer: the trail lives only in memory (" + lp.Trail.Count + " points, no file in the data folder written or changed while walking)", lp.Trail.Count >= 30 && touched.Count == 0, string.Join(", ", touched));
                await Task.Delay(400);
                var png = await view.CaptureForTest();
                if (png != null && SelfTestClock.ShotsOn) File.WriteAllBytes(P("worldmap-live-dot-trail.png"), png);
                // Follow me
                var mf = await WmMe("follow");
                await Task.Delay(400);
                var mf2 = await WmMeWait(m => m.Bool("centered"));
                Check("live layer: Follow me centres the map on you and is remembered", mf != null && mf.Bool("follow") && mf2 != null && mf2.Bool("centered") && settings.WorldMapFollow, MeText(mf2));
                lp.Update(true, true, LiveChar(fx: 0.62, fy: 0.5), DateTime.UtcNow); view.PushLive(true);
                var mf3 = await WmMeWait(m => m.Bool("centered") && m.Int("points") > mf2.Int("points"));
                Check("live layer: ... and keeps following when you move", mf3 != null && mf3.Bool("centered"), MeText(mf3));
                await WmMe("follow");
                await WaitFor(() => !settings.WorldMapFollow, 3000);
                Check("live layer: Follow me off again", !settings.WorldMapFollow);

                // events on the trail: through the real path (GamePresence.TrailEvent -> LivePosition -> the page), drawn as marks with a tooltip, the row switches them
                await WorldMapLiveEventsPageTest(Check, P, lp, view, false);

                // in an instance: the dot greys out with "Position unavailable here", no how-to link (nothing to switch on)
                lp.Update(true, true, LiveChar(valid: false), DateTime.UtcNow); view.PushLive(true);
                var mi = await WmMeWait(m => m.Str("state") == "unavailable");
                Check("live layer: in an instance the dot greys out with \"Position unavailable here\" (not the 'turn it on' notice, no link)",
                    mi != null && mi.Bool("dot") && mi.Bool("grey") && mi.Str("label") == "Position unavailable here" && mi.Str("notice") == "Position unavailable here" && mi.Str("link") == "", MeText(mi));

                // off in the game: a calm notice with the how-to link, and no dot
                lp.Update(true, true, LiveChar(enabled: false, valid: false), DateTime.UtcNow); view.PushLive(true);
                var mn = await WmMeWait(m => m.Str("state") == "notshared");
                Check("live layer: switched off in the game - \"Your position isn't shared from the game yet\" with \"How to turn it on\", no dot",
                    mn != null && !mn.Bool("dot") && mn.Str("notice") == "Your position isn't shared from the game yet" && mn.Str("link") == "How to turn it on", MeText(mn));
                await Task.Delay(300);
                png = await view.CaptureForTest();
                if (png != null && SelfTestClock.ShotsOn) File.WriteAllBytes(P("worldmap-live-notshared.png"), png);
                int before = view.HowToOpenedForTest;
                await WmMe("howto");
                await WaitFor(() => opened.Count > 0, 3000);
                Check("live layer: \"How to turn it on\" opens the how-to card (" + string.Join(",", opened) + ")", view.HowToOpenedForTest == before + 1 && opened.LastOrDefault() == HelpPages.LivePosition);

                // an Elan's Hub that is too old
                lp.Update(true, true, LiveChar(supported: false, enabled: false, valid: false), DateTime.UtcNow); view.PushLive(true);
                var mo = await WmMeWait(m => m.Str("state") == "oldhub");
                Check("live layer: an old Elan's Hub - \"Your Elan's Hub is too old to share positions — update it\" with the link", mo != null && !mo.Bool("dot") && mo.Str("notice") == LivePositionText.OldHub && mo.Str("link") == "How to turn it on", MeText(mo));

                // through the real path: the strip's position (GamePresence) -> LivePosition -> the page
                Session.Presence.SetPositionForTest(LiveChar(fx: 0.5, fy: 0.5), true);
                var mr = await WmMeWait(m => m.Str("state") == "ok" && m.Bool("dot"));
                Check("live layer: a position from the strip reaches the map through GamePresence.PositionChanged", mr != null && lp.State == LiveState.Ok, MeText(mr));
                // pushed at most once a second
                int posts = view.LivePostsForTest;
                for (int i = 0; i < 10; i++) { lp.Update(true, true, LiveChar(fx: 0.5 + i * 0.01), DateTime.UtcNow.AddSeconds(i + 1)); view.PushLive(false); }
                Check("live layer: ten updates within a moment are at most one push now (the newest follows within a second)", view.LivePostsForTest - posts <= 1, (view.LivePostsForTest - posts) + "");
                await Task.Delay(1300);
                Check("live layer: ... and the newest state follows", view.LivePostsForTest - posts <= 2 && view.LastMeForTest.Contains("\"ok\""));

                // the setting off again: the layer goes, the trail is forgotten
                settings.WorldMapLivePos = false; UpdateLive();
                var mx = await WmMeWait(m => m.Str("state") == "off");
                Check("live layer: switching the setting off removes the layer and forgets the trail", mx != null && !mx.Bool("layer") && !mx.Bool("panel") && lp.Trail.Count == 0, MeText(mx));

                // the how-to card itself and the Settings row (pictures)
                if (SelfTestClock.ShotsOn)
                {
                    HelpPages.TestHook = null;
                    var card = HelpPages.Create(HelpPages.LivePosition);
                    card.Owner = this; card.WindowStartupLocation = WindowStartupLocation.CenterOwner; card.ShowActivated = false;
                    card.Show(); await Task.Delay(500);
                    try
                    {
                        var dpi = VisualTreeHelper.GetDpi(card);
                        var bmp = new RenderTargetBitmap((int)(card.ActualWidth * dpi.DpiScaleX), (int)(card.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                        bmp.Render(card);
                        var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp));
                        using (var fs = File.Create(P("live-position-howto.png"))) enc.Save(fs);
                    }
                    catch (Exception) { }
                    card.Close();
                    ShowTab("settings"); SettingsPage.Show("general");
                    SettingsPage.LivePosBox.IsChecked = true;
                    await Task.Delay(300);
                    SettingsPage.LivePosRow.BringIntoView();
                    await Task.Delay(400);
                    Snapshot(P("settings-live-position.png"));
                    SettingsPage.LivePosBox.IsChecked = savedOn;
                    ShowTab("atlas");
                }
            }
            finally
            {
                HelpPages.TestHook = savedHook;
                settings.WorldMapLivePos = savedOn; settings.WorldMapFollow = savedFollow;
                settings.WorldMapLayers = savedLayers; settings.WorldMapLayersVersion = savedLayersV;
                EventHooks(false);
                try { UpdateLive(); } catch (Exception) { }
            }
        }

        async Task<Dictionary<string, object>> Wm3Me(string click = null)
        {
            var r = await WmEval("(function(){try{return window.wm3.me(" + (click == null ? "" : "'" + click + "'") + ")}catch(e){return JSON.stringify({err:e.message})}})()");
            var s = Json.Parse(r ?? "null") as string;
            return s == null ? null : Json.Obj(s);
        }
        async Task<Dictionary<string, object>> Wm3MeWait(Func<Dictionary<string, object>, bool> cond, int ms = 8000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew(); Dictionary<string, object> last = null;
            while (sw.ElapsedMilliseconds < ms) { try { last = await Wm3Me(); if (last != null && cond(last)) return last; } catch (Exception) { } await Task.Delay(100); }
            return last;
        }

        // --selftest "worldmap": the live layer in the 3D view (2.77): the trail draped over the ground, the dot, the card, Follow me that eases the camera to you and stops when you
        // drag, the events on the trail with their tooltips and the row. Runs on the 3D page of the invented continent (only where the machine has WebGL).
        async Task WorldMapLive3dTest(CheckFn Check, Func<string, string> P)
        {
            bool savedOn = settings.WorldMapLivePos, savedFollow = settings.WorldMapFollow;
            var savedLayers = settings.WorldMapLayers?.ToList(); var savedLayersV = settings.WorldMapLayersVersion;
            var view = WorldMapView; var lp = LivePos;
            try
            {
                lp.ToWorld = null; view.Live = lp;
                settings.WorldMapLivePos = false; settings.WorldMapFollow = false;
                lp.Update(false, true, LiveChar(), DateTime.UtcNow); view.PushLive(true);
                var m0 = await Wm3MeWait(m => m.Str("state") == "off");
                Check("3D live: with the setting off there is nothing (no dot, no trail, no card)", m0 != null && m0.Str("state") == "off" && !m0.Bool("dot") && m0.Int("lines") == 0 && !m0.Bool("panel"), m0 == null ? "null" : string.Join(" ", m0.Select(kv => kv.Key + "=" + kv.Value)));
                settings.WorldMapLivePos = true;
                var start = DateTime.UtcNow.AddSeconds(-400);
                for (int i = 0; i <= 40; i++) lp.Update(true, true, LiveChar(fx: 0.40 + i * 0.004, fy: 0.45 + (i % 20) * 0.002), start.AddSeconds(i * 10));
                view.PushLive(true);
                var m1 = await Wm3MeWait(m => m.Str("state") == "ok" && m.Bool("dot") && m.Int("lines") >= 2);
                string T(Dictionary<string, object> m) => m == null ? "null" : string.Join(" ", m.Select(kv => kv.Key + "=" + kv.Value));
                Check("3D live: the 3D view draws the live layer now - your dot with its heading arrow, the trail as ribbons over the ground, the card with Follow me (" + T(m1) + ")",
                    m1 != null && m1.Bool("dot") && m1.Bool("arrow") && !m1.Bool("grey") && m1.Int("lines") >= 2 && m1.Int("points") >= 30 && m1.Bool("panel") && m1.Bool("followBtn") && m1.Str("notice") == "", T(m1));
                double lift = m1 != null && m1["ribbonLift"] is double dl ? dl : -99;
                Check("3D live: the trail lies on the ground (a little above it: " + lift + " yards), not in the air or under it", lift > 0.5 && lift < 8, lift + "");
                // Follow me: the camera's target eases to you; a new position is followed; a drag stops it (like the flat map)
                var f1 = await Wm3Me("follow");
                var f2 = await Wm3MeWait(m => m.Bool("centered"), 6000);
                Check("3D live: Follow me brings the camera to you and is remembered", f1 != null && f1.Bool("follow") && f2 != null && f2.Bool("centered") && settings.WorldMapFollow, T(f2));
                lp.Update(true, true, LiveChar(fx: 0.62, fy: 0.5), DateTime.UtcNow); view.PushLive(true);
                var f3a = await Wm3MeWait(m => m.Int("off") > 0 || m.Int("points") > f2.Int("points"), 3000);
                var f3 = await Wm3MeWait(m => m.Bool("centered") && m.Int("points") > f2.Int("points"), 6000);
                Check("3D live: ... and keeps following when you move (the target eases over, it does not jump: " + (f3a?.Int("off") ?? -1) + " yards to go at first)", f3 != null && f3.Bool("centered") && f3.Bool("follow"), T(f3));
                var dragged = await WmEval("window.wm3.drag(120, 10, false)");
                bool stopped = await WaitFor(() => !settings.WorldMapFollow, 3000);
                var f4 = await Wm3Me();
                Check("3D live: orbiting the camera with the mouse stops Follow me (and the app remembers it)", dragged == "false" && stopped && f4 != null && !f4.Bool("follow"), dragged + " " + T(f4));
                await Wm3Me("follow"); await Wm3MeWait(m => m.Bool("centered"), 6000);
                await WmEval("window.wm3.drag(60, 40, true)");
                bool stopped2 = await WaitFor(() => !settings.WorldMapFollow, 3000);
                Check("3D live: panning with the mouse stops it too", stopped2);
                if (SelfTestClock.ShotsOn) { await Task.Delay(400); var png = await view.CaptureForTest(); if (png != null) File.WriteAllBytes(P("worldmap-live-3d.png"), png); }

                // the events on the trail in 3D (the camera back on you first, so they are on screen)
                await Wm3Me("follow");
                var fc = await Wm3MeWait(m => m.Bool("centered") && m.Bool("dotShown"), 6000);
                Check("3D live: following again, your dot is on screen in the middle", fc != null && fc.Bool("centered") && fc.Bool("dotShown"), T(fc));
                await WorldMapLiveEventsPageTest(Check, P, lp, view, true);
                var e3 = await WmEvWait(true, m => m.Int("shown") >= 1, 4000);
                var ops = e3?["ops"] as List<object>;
                Check("3D events: the marks stand on screen near your dot (" + (e3?.Int("shown") ?? 0) + " of " + (e3?.Int("drawn") ?? 0) + "), and fade with the trail (all recent: fully drawn)",
                    e3 != null && e3.Int("shown") >= 1 && ops != null && ops.Count > 0 && ops.All(o => Convert.ToDouble(o) > 0.95), EvText(e3));
                await Wm3Me("follow");
                await WaitFor(() => !settings.WorldMapFollow, 3000);

                // 2.97.1: the Lodge members' dots in 3D (2.97.0 drew them on the flat map only), Follow this member, and a member who leaves the map
                {
                    var c0 = LiveChar(fx: 0.45, fy: 0.5);
                    string mm(int id, string name, double fx) { var w = MapData.Index?.MapToWorld(c0.PosMapId, fx, 0.5); if (w == null) return null; WorldMapMath.WorldToLatLng(w.Value.X, w.Value.Y, out var la, out var ln); return "{\"id\":" + id + ",\"name\":\"" + name + "\",\"color\":\"#69CCF0\",\"ll\":[" + la.ToString(CultureInfo.InvariantCulture) + "," + ln.ToString(CultureInfo.InvariantCulture) + "]}"; }
                    var a = mm(2, "Tess", 0.47); var b = mm(3, "Wing", 0.5);
                    async Task<Dictionary<string, object>> M3(string arg = "") { var r = await WmEval("(function(){try{return window.wm3.members(" + arg + ")}catch(e){return JSON.stringify({err:e.message})}})()"); var s = Json.Parse(r ?? "null") as string; return s == null ? null : Json.Obj(s); }
                    if (a != null && b != null)
                    {
                        await WmEval("window.wm3.feed({\"t\":\"members\",\"list\":[" + a + "," + b + "]})");
                        var g1 = await M3();
                        Check("3D members: the members who share with you stand on the 3D map as dots with their names (" + T(g1) + ")", g1 != null && g1.Int("n") == 2 && g1.Int("dots") == 2 && g1.List("names").Cast<object>().Select(o => o as string).SequenceEqual(new[] { "Tess", "Wing" }), T(g1));
                        var g2 = await M3("3");
                        Dictionary<string, object> g3 = null;
                        for (int i = 0; i < 60 && !(g3 != null && g3.Bool("centered")); i++) { await Task.Delay(100); g3 = await M3(); }
                        Check("3D members: Follow this member brings the camera to them (and following yourself is off)", g3 != null && g3.Int("following") == 3 && g3.Bool("centered") && !g3.Bool("follow"), T(g3));
                        await WmEval("window.wm3.feed({\"t\":\"members\",\"list\":[" + a + "]})");
                        var g4 = await M3();
                        Check("3D members: a member who leaves the map loses their dot, and following them stops", g4 != null && g4.Int("n") == 1 && g4.Int("following") == 0, T(g4));
                        await WmEval("window.wm3.feed({\"t\":\"members\",\"list\":[]})");
                    }
                    else Check("3D members: the invented continent has a place for the test map", false, "no world point");
                }

                // the height (2.82): in the air on a flight, back on the ground after landing, on a building's floor indoors
                await WorldMapLiveElevation3dTest(Check, P, lp, view);

                // in an instance: a grey dot with the label, the card says so
                lp.Update(true, true, LiveChar(valid: false), DateTime.UtcNow); view.PushLive(true);
                var mi = await Wm3MeWait(m => m.Str("state") == "unavailable");
                Check("3D live: in an instance the dot greys out with \"Position unavailable here\"", mi != null && mi.Bool("dot") && mi.Bool("grey") && mi.Str("label") == "Position unavailable here" && mi.Str("notice") == "Position unavailable here", T(mi));
                // switched off in the game: the notice with the how-to link, no dot
                lp.Update(true, true, LiveChar(enabled: false, valid: false), DateTime.UtcNow); view.PushLive(true);
                var mn = await Wm3MeWait(m => m.Str("state") == "notshared");
                Check("3D live: not shared from the game - the notice and \"How to turn it on\", no dot", mn != null && !mn.Bool("dot") && mn.Str("notice") == LivePositionText.NotShared && mn.Str("link") == LivePositionText.HowToLink, T(mn));
                // the setting off: everything goes, the marks and the row too
                settings.WorldMapLivePos = false; UpdateLive();
                var mx = await Wm3MeWait(m => m.Str("state") == "off");
                var ex = await WmEv(true);
                Check("3D live: switching the setting off removes the dot, the trail, the card and the marks; the row hides", mx != null && !mx.Bool("dot") && mx.Int("lines") == 0 && !mx.Bool("panel") && ex != null && ex.Int("drawn") == 0 && !ex.Bool("row") && lp.Trail.MarkCount == 0, T(mx) + " | " + EvText(ex));
            }
            finally
            {
                settings.WorldMapLivePos = savedOn; settings.WorldMapFollow = savedFollow;
                settings.WorldMapLayers = savedLayers; settings.WorldMapLayersVersion = savedLayersV;
                EventHooks(false);
                try { UpdateLive(); } catch (Exception) { }
            }
        }

        async Task<Dictionary<string, object>> WmEv(bool is3d, string args = "")
        {
            var r = await WmEval("(function(){try{return window." + (is3d ? "wm3" : "wm") + ".ev(" + args + ")}catch(e){return JSON.stringify({err:e.message})}})()");
            var s = Json.Parse(r ?? "null") as string;
            return s == null ? null : Json.Obj(s);
        }
        async Task<Dictionary<string, object>> WmEvWait(bool is3d, Func<Dictionary<string, object>, bool> cond, int ms = 8000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew(); Dictionary<string, object> last = null;
            while (sw.ElapsedMilliseconds < ms) { try { last = await WmEv(is3d); if (last != null && cond(last)) return last; } catch (Exception) { } await Task.Delay(100); }
            return last;
        }
        static string EvText(Dictionary<string, object> m) => m == null ? "null" : string.Join(" ", m.Where(kv => kv.Key != "tipHtml").Select(kv => kv.Key + "=" + (kv.Value is List<object> l ? "[" + string.Join(",", l) + "]" : kv.Value)));

        // the marks on the page (2D or 3D): the live layer is on and walking (state ok, trail) when this is called
        async Task WorldMapLiveEventsPageTest(CheckFn Check, Func<string, string> P, LivePosition lp, WorldMapView view, bool is3d)
        {
            string D = is3d ? "3D " : "";
            EventHooks(true);
            var now = DateTime.UtcNow;
            int before = lp.Trail.MarkCount;
            var evs = new[]
            {
                Ev(TrailKind.Kill, 6, now.AddSeconds(-8)), Ev(TrailKind.Loot, 774, now.AddSeconds(-7), 3), Ev(TrailKind.Herb, 2447, now.AddSeconds(-6)), Ev(TrailKind.Level, 61, now.AddSeconds(-5)),
                Ev(TrailKind.Quest, 783, now.AddSeconds(-4)), Ev(TrailKind.Area, TrailEventText.AreaHash("Westmark Fields"), now.AddSeconds(-3)), Ev(TrailKind.Flight, 0, now.AddSeconds(-2)), Ev(TrailKind.Died, 0, now.AddSeconds(-1)),
            };
            foreach (var e in evs) Session.Presence.TrailEventForTest(e);
            Session.Presence.TrailEventForTest(Ev(TrailKind.Kill, 6, now, who: "Someone else"));
            view.PushLive(true);
            Check("events " + D + "page: 8 events from the strip land on the trail through GamePresence.TrailEvent (another character's is dropped)", lp.Trail.MarkCount == before + 8, (lp.Trail.MarkCount - before) + "");
            var e1 = await WmEvWait(is3d, m => m.Int("drawn") >= 8);
            var kinds = e1 == null ? new List<string>() : (e1["kinds"] as List<object>).Cast<string>().ToList();
            Check("events " + D + "page: the marks are drawn on the trail, one per event, each with its kind's icon; the row \"Events on my trail\" shows with the count (" + EvText(e1) + ")",
                e1 != null && e1.Int("drawn") == lp.Trail.MarkCount && e1.Bool("on") && e1.Bool("row") && e1.Str("count") == lp.Trail.MarkCount.ToString()
                && new[] { "kill", "loot", "herb", "level", "quest", "area", "flight", "died" }.All(kinds.Contains), EvText(e1));
            await Task.Delay(400);
            var spots = await WmEval("(function(){var s={};document.querySelectorAll('.me-ev').forEach(function(e){var r=e.getBoundingClientRect(); if(r.width>0) s[Math.round(r.left)+','+Math.round(r.top)]=1}); return Object.keys(s).length})()");
            Check("events " + D + "page: 8 marks on the same spot (where you stand) fan out around your dot, each one visible on its own (" + spots + " places on screen)", int.TryParse(spots, out var nsp) && nsp >= 8, spots);
            int lootAt = kinds.LastIndexOf("loot"), killAt = kinds.LastIndexOf("kill");
            var tl = await WmEv(is3d, lootAt.ToString());
            var tk = await WmEv(is3d, killAt.ToString());
            Check("events " + D + "page: pointing at a mark says what and when (\"Looted Malachite\", \"Rare\", the time and \"just now\", click to open; \"Killed Kobold Vermin\")",
                tl != null && tl.Str("tip").Contains("Looted Malachite") && tl.Str("tip").Contains("Rare") && tl.Str("tip").Contains("just now") && tl.Str("tip").Contains("click to open") && tk != null && tk.Str("tip").Contains("Killed Kobold Vermin"),
                (tl?.Str("tip") ?? "null") + " | " + (tk?.Str("tip") ?? "null"));
            Check("events " + D + "page: the loot mark is drawn in its quality colour (rare blue)", tl != null && tl.Str("tipHtml").Length > 0 && (await WmEval("(function(){var e=document.querySelectorAll('.me-ev[data-k=loot] circle'); return e.length ? e[e.length-1].getAttribute('stroke') : ''})()") ?? "").Contains("#0070DD"));
            if (SelfTestClock.ShotsOn) { await Task.Delay(400); var png = await view.CaptureForTest(); if (png != null) File.WriteAllBytes(P(is3d ? "worldmap-live-events-3d.png" : "worldmap-live-events-2d.png"), png); }
            // the row switches them off and on again (saved like any layer)
            await WmEval("window." + (is3d ? "wm3.setOnForTest" : "wm.setOn") + "('trailevents', false)");
            var eOff = await WmEvWait(is3d, m => m.Int("drawn") == 0);
            bool savedOff = await WaitFor(() => settings.WorldMapLayers != null && !settings.WorldMapLayers.Contains("trailevents"), 3000);
            await WmEval("window." + (is3d ? "wm3.setOnForTest" : "wm.setOn") + "('trailevents', true)");
            var eOn = await WmEvWait(is3d, m => m.Int("drawn") >= 8);
            Check("events " + D + "page: \"Events on my trail\" off hides the marks (and is saved), on shows them again; the trail stays", eOff != null && eOff.Int("drawn") == 0 && !eOff.Bool("on") && savedOff && eOn != null && eOn.Int("drawn") == lp.Trail.MarkCount, EvText(eOff) + " / " + EvText(eOn));
        }
    }
}
