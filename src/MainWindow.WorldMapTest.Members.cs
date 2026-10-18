using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ElansAddonHub.Services;
using ElansAddonHub.Services.WorldMap;

namespace ElansAddonHub
{
    // --selftest "worldmap", part 6b (2.97): the Lodge members' dots on the world map (Lodge 2.30 "liveposition"), next to the live layer's own tests
    // (MainWindow.WorldMapTest.Live.cs). The "members" message, what the session sends (opt-in, scope, about once a second, taken back at once) and keeps,
    // and the page: dots, names, Follow this member, a member who leaves, goes invisible or stops sharing.
    // 2.98: members drawn like you - their trails (MemberTrails, memory only), prints by their move bits, the card's "Lodge, live" list, following from it
    // (also onto another world map: WorldMapMemberIslandTest), and nothing about them on disk.
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
            + "{\"id\":5,\"name\":\"Rover (guest)\",\"role\":\"guest\",\"guest\":true},"
            + "{\"id\":6,\"name\":\"Wing\",\"role\":\"officer\",\"game\":{\"playing\":true,\"name\":\"Wing\",\"classFile\":\"HUNTER\",\"raceFile\":\"Tauren\",\"zone\":\"Greenvale\"}}]}";

        static string MemberPos(int id, double fx, double fy, int mapId = 11, bool valid = true, int move = 0) =>
            valid ? "{\"t\":\"memberpos\",\"id\":" + id + ",\"mapId\":" + mapId + ",\"fx\":" + fx.ToString(CultureInfo.InvariantCulture) + ",\"fy\":" + fy.ToString(CultureInfo.InvariantCulture) + ",\"move\":" + move + ",\"valid\":true}"
                  : "{\"t\":\"memberpos\",\"id\":" + id + ",\"valid\":false}";

        // 2.98 on the flat page: a member walks, rides and swims - their dot with its arrow, the trail and its prints like yours; the card's list; following from it;
        // the trail fading out; leaving; and nothing of it written to disk (only the log's lines without a place)
        async Task WorldMapMemberTrailsPageTest(CheckFn Check, Func<string, string> P, Func<DateTime> getT, Action<DateTime> setT)
        {
            var s = settings; var view = WorldMapView;
            var before = DataSnapshot();
            var t = getT();
            // Wing (a tauren hunter) walks with the pet out, rides, then swims: 16 steps two seconds apart
            for (int i = 0; i < 16; i++)
            {
                int move = i < 6 ? LiveMove.PetOut : i < 12 ? LiveMove.Mounted | LiveMove.PetOut : LiveMove.Swimming;
                t = t.AddSeconds(2); setT(t);
                Session.FeedForTest(MemberPos(6, 0.40 + i * 0.004, 0.46, move: move));
            }
            Check("trails: the session keeps Wing's trail in memory (16 steps, each with its move bits); the dot is where they are",
                Session.Trails.PointsOf(6, t).Count >= 14 && Session.MemberDots().Any(d => d.Id == 6 && d.Trail.Count >= 14 && d.Foot == "hoof" && d.Hunter && d.Move == LiveMove.Swimming), Session.Trails.PointsOf(6, t).Count + "");
            view.PushMembers(true);
            var w1 = await WmMembersWait(m => m.List("walkers")?.Cast<Dictionary<string, object>>().Any(x => x.Int("id") == 6 && x.Int("points") >= 14) == true);
            var wing = w1?.List("walkers")?.Cast<Dictionary<string, object>>().FirstOrDefault(x => x.Int("id") == 6);
            Check("page: Wing is drawn like you - the dot in their class colour with the heading arrow, the fading trail behind them (" + MembersText(wing) + ")",
                wing != null && wing.Bool("meDot") && wing.Bool("arrow") && wing.Int("points") >= 14 && wing.Int("lines") >= 2 && (w1["colors"] as List<object>).Contains("#ABD473"), MembersText(wing));
            var ages = wing?.List("ages");
            Check("page: ... the trail's oldest point is older than its newest (it fades out along the 10 minutes)", ages != null && ages.Count == 2 && Convert.ToDouble(ages[0]) > Convert.ToDouble(ages[1]) + 20, ages == null ? "null" : string.Join(",", ages));

            // the card: "Lodge, live" with a row per member who shares, the zone on hover; following from it
            var rows = w1?.List("rows")?.Cast<Dictionary<string, object>>().ToList() ?? new List<Dictionary<string, object>>();
            Check("card: \"Lodge, live\" under your own name lists a row per member who shares with you (by name) with their zone on hover, and says nothing is followed",
                w1 != null && w1.Bool("card") && w1.Str("head") == "Lodge, live" && rows.Select(r => r.Str("name")).SequenceEqual(new[] { "bBadGuy/b", "Wing" })
                && rows[1].Str("title").StartsWith("Greenvale") && rows.All(r => !r.Bool("on") && r.Str("wh") == "") && w1.Str("fl") == "Not following", MembersText(w1));
            await WmMembers("null, null, 6");
            await WaitFor(() => view.FollowingMember == 6, 3000);
            var r1 = await WmMembersWait(m => m.Bool("centered") && m.Int("following") == 6, 4000);
            Check("card: clicking Wing's row follows them - the map centres on them, the row is marked, the card says \"Following Wing\"",
                r1 != null && r1.Str("fl") == "Following Wing" && r1.List("rows").Cast<Dictionary<string, object>>().Single(r => r.Int("id") == 6).Bool("on") && view.FollowingMember == 6, MembersText(r1));
            // zoomed in on them: the prints - hooves on foot (a tauren), horseshoes mounted, ripples swimming, the pet's paws beside them
            WorldMapMath.WorldToLatLng(Session.Trails.HereOf(6).Value.X, Session.Trails.HereOf(6).Value.Y, out var hla, out var hln);
            await WmEval(string.Format(CultureInfo.InvariantCulture, "window.wm.feed({{t:'goto',lat:{0},lng:{1},z:9}})", hla, hln));
            await Task.Delay(500);
            var pz = await WmMembersWait(m => m.List("walkers")?.Cast<Dictionary<string, object>>().Any(x => x.Int("id") == 6 && x.Child("prints") != null && x.Child("prints").Int("n") > 0) == true, 5000);
            var wp = pz?.List("walkers")?.Cast<Dictionary<string, object>>().FirstOrDefault(x => x.Int("id") == 6)?.Child("prints");
            var kinds = wp?.Child("kinds");
            Check("prints: zoomed in, Wing's trail is prints in their colour like yours - hooves on foot, horseshoes mounted, ripples swimming, and the pet's paws beside them (" + (wp == null ? "none" : Json.Write(wp["kinds"]) + " pets " + wp.Int("pets")) + ")",
                wp != null && wp.Int("n") > 0 && wp.Str("color") == "#ABD473" && kinds != null && kinds.ContainsKey("hoof") && kinds.ContainsKey("shoe") && kinds.ContainsKey("ripple") && wp.Int("pets") > 0 && wp.Int("painted") > 0, wp == null ? "null" : Json.Write(wp));
            if (SelfTestClock.ShotsOn) { await Task.Delay(400); var png = await view.CaptureForTest(); if (png != null) File.WriteAllBytes(P("worldmap-members-trail.png"), png); }
            await WmEval("window.wm.feed({t:'goto',lat:-128,lng:128,z:5})");
            await WmMembers("null, null, 6");
            var r2 = await WmMembersWait(m => m.Int("following") == 0, 3000);
            Check("card: clicking the row again stops following (\"Not following\")", r2 != null && r2.Str("fl") == "Not following" && await WaitFor(() => view.FollowingMember == 0, 3000), MembersText(r2));
            await WmMembers("null, null, 6"); await WaitFor(() => view.FollowingMember == 6, 3000);
            await WmMe("follow");
            var r3 = await WmMembersWait(m => m.Int("following") == 0, 3000);
            Check("card: Follow me stops following Wing and the card says \"Following you\"", r3 != null && r3.Str("fl") == "Following you" && await WaitFor(() => view.FollowingMember == 0 && s.WorldMapFollow, 3000), MembersText(r3));
            await WmMembers("null, null, 6"); await WaitFor(() => view.FollowingMember == 6, 3000);
            var r4 = await WmMembers();
            Check("card: ... and following Wing again stops Follow me (one thing at a time)", r4 != null && r4.Str("fl") == "Following Wing" && await WaitFor(() => !s.WorldMapFollow, 3000), MembersText(r4));
            await WmEval("window.wm.dragForTest()");
            var r5 = await WmMembersWait(m => m.Int("following") == 0, 3000);
            Check("card: dragging the map stops following them, as before", r5 != null && r5.Str("fl") == "Not following" && await WaitFor(() => view.FollowingMember == 0, 3000), MembersText(r5));

            // the trail fades out: eleven minutes on, a new step leaves only that one (nothing older than 10 minutes is kept or drawn)
            t = t.AddMinutes(11); setT(t);
            Session.FeedForTest(MemberPos(6, 0.47, 0.46));
            view.PushMembers(true);
            var f1 = await WmMembersWait(m => m.List("walkers")?.Cast<Dictionary<string, object>>().Any(x => x.Int("id") == 6 && x.Int("points") <= 1) == true, 4000);
            Check("trails: after ten minutes the old trail is gone (memory and page)", f1 != null && Session.Trails.PointsOf(6, t).Count == 1, f1 == null ? "null" : MembersText(f1));

            // they stop sharing: dot, trail and row go
            Session.FeedForTest(MemberPos(6, 0, 0, valid: false));
            view.PushMembers(true);
            var g1 = await WmMembersWait(m => !m.List("ids").Cast<object>().Any(o => Convert.ToInt32(o) == 6) && !m.List("rows").Cast<Dictionary<string, object>>().Any(r => r.Int("id") == 6), 4000);
            Check("leave: Wing stops sharing - their dot, trail, prints renderer and row go at once (and the session forgets the trail)",
                g1 != null && !Session.Trails.Has(6) && g1.Int("renderers") == g1.List("walkers").Count && !g1.List("walkers").Cast<Dictionary<string, object>>().Any(x => x.Int("id") == 6), MembersText(g1));
            for (int i = 0; i < 3; i++) { t = t.AddSeconds(2); setT(t); Session.FeedForTest(MemberPos(6, 0.40 + i * 0.004, 0.46)); }
            Session.FeedForTest("{\"t\":\"leave\",\"id\":6}");
            view.PushMembers(true);
            var g2 = await WmMembersWait(m => !m.List("rows").Cast<Dictionary<string, object>>().Any(r => r.Int("id") == 6), 4000);
            Check("leave: a member who leaves the Lodge loses dot, trail and row the same way", g2 != null && !Session.Trails.Has(6) && !Session.MemberDots().Any(d => d.Id == 6), MembersText(g2));
            s.WorldMapLivePos = false; UpdateLive();
            Check("trails: \"Show my live position on the world map\" off clears every member's trail at once", Session.Trails.Count == 0, Session.Trails.Count + "");
            s.WorldMapLivePos = true;

            // nothing about the members is written to disk: only the log changed (and the settings for Follow me), with no positions in either
            var changed = DataChanged(before).Where(f => f.IndexOf("EBWebView", StringComparison.OrdinalIgnoreCase) < 0).ToList();      // the web view's own caches (shaders, code) are not ours
            string place = (0.412).ToString(CultureInfo.InvariantCulture);
            var bad = changed.Where(f => { try { var txt = NewText(f, before); return txt.Contains("\"trail\"") || txt.Contains("memberpos") || (txt.Contains("Wing") && txt.Contains(place)) || Regex.IsMatch(txt, @"lodge position: [^\r\n]*\b0\.\d{3,}"); } catch (Exception) { return false; } }).ToList();
            Check("privacy: nothing about the members is written to disk - only " + string.Join(", ", changed.Select(Path.GetFileName)) + " changed, with no member's position or trail in it",
                bad.Count == 0 && changed.All(f => Path.GetFileName(f) == "hub.log" || Path.GetFileName(f).StartsWith("settings")), string.Join(", ", changed.Select(Path.GetFileName)) + " | bad: " + string.Join(", ", bad.Select(Path.GetFileName)));
            setT(t);
        }
        static Dictionary<string, Tuple<long, DateTime>> DataSnapshot()
        {
            var d = new Dictionary<string, Tuple<long, DateTime>>(StringComparer.OrdinalIgnoreCase);
            try { foreach (var f in Directory.GetFiles(Util.DataDir, "*", SearchOption.AllDirectories)) { var fi = new FileInfo(f); d[f] = Tuple.Create(fi.Length, fi.LastWriteTimeUtc); } } catch (Exception) { }
            return d;
        }
        // what a file holds that it did not before (the log: only the lines added since the snapshot)
        static string NewText(string f, Dictionary<string, Tuple<long, DateTime>> before)
        {
            var bytes = File.ReadAllBytes(f);
            long from = before.TryGetValue(f, out var b) && b.Item1 <= bytes.Length && Path.GetFileName(f) == "hub.log" ? b.Item1 : 0;
            return System.Text.Encoding.UTF8.GetString(bytes, (int)from, bytes.Length - (int)from);
        }
        static List<string> DataChanged(Dictionary<string, Tuple<long, DateTime>> before)
        {
            var now = DataSnapshot();
            return now.Where(kv => !before.TryGetValue(kv.Key, out var b) || b.Item1 != kv.Value.Item1 || b.Item2 != kv.Value.Item2).Select(kv => kv.Key).ToList();
        }

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
            var many = Enumerable.Range(1, 300).Select(i => new MemberDot { Id = i, Name = "M" + i.ToString("000"), Color = "#FFFFFF", MapId = 0, X = 500, Y = 1000 }).ToList();
            var mMany = Json.Obj(WorldMapProtocol.Members(many, 0, following: 250));
            var lMany = (mMany["list"] as List<object>).Cast<Dictionary<string, object>>().ToList();
            Check("members: never more than " + WorldMapProtocol.MaxDrawn + " drawn (the one followed first) and " + WorldMapProtocol.MaxRows + " rows; none (null) is an empty list that takes every dot away",
                lMany.Count == WorldMapProtocol.MaxDrawn && lMany[0].Int("id") == 250 && lMany[1].Int("id") == 1 && lMany[2].Int("id") == 2 && (mMany["rows"] as List<object>).Count == WorldMapProtocol.MaxRows
                && (Json.Obj(WorldMapProtocol.Members(null, 0))["list"] as List<object>).Count == 0 && (Json.Obj(WorldMapProtocol.Members(null, 0))["rows"] as List<object>).Count == 0);
            WorldMapMemberTrailsUnitTest(Check);
            Check("protocol: mfollow carries a member id (0 = stop), anything else is refused",
                WorldMapProtocol.Parse("{\"t\":\"mfollow\",\"id\":7}", out _)?.MemberId == 7 && WorldMapProtocol.Parse("{\"t\":\"mfollow\",\"id\":0}", out _)?.Type == WorldMapMessageType.MemberFollow
                && WorldMapProtocol.Parse("{\"t\":\"mfollow\",\"id\":-1}", out _) == null && WorldMapProtocol.Parse("{\"t\":\"mfollow\",\"id\":1.5}", out _) == null && WorldMapProtocol.Parse("{\"t\":\"mfollow\"}", out _) == null);
            Check("texts: the Lodge switch and its one-line note (off by default, everyone or officers, never guests, Appear offline hides it); the Hub's description no longer says Lodge sharing \"does not exist yet\"",
                LivePositionText.LodgeSettingLabel == "Share my map position with the Lodge" && LivePositionText.LodgeSettingNote.StartsWith("Off by default.") && LivePositionText.LodgeSettingNote.Contains("never guests")
                && LivePositionText.LodgeSettingNote.Contains("Appear offline") && LivePositionText.HubDescription.Contains("sharing with your Lodge is a separate switch in the Outpost, off until you turn it on")
                && !LivePositionText.HubDescription.Contains("does not exist yet"));
        }

        // 2.98: the members' trails (memory only) and the "members" message's trails, prints and rows
        void WorldMapMemberTrailsUnitTest(CheckFn Check)
        {
            var t0 = new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
            var mt = new MemberTrails { ElevationWorld = new FixtureWorld { G = (x, y) => double.NaN, F = (x, y) => new double[] { 16, 24 } } };
            for (int i = 0; i < 6; i++) mt.Add(2, "Tess", 0, 1000 + i * 10, 2000, LiveMove.Mounted | LiveMove.PetOut, t0.AddSeconds(i * 2));
            mt.Add(2, "Tess", 0, 1052, 2000, 0, t0.AddSeconds(10.5));                 // half a second after the last: it is where they are, not a trail point yet
            var pts = mt.PointsOf(2, t0.AddSeconds(11));
            Check("member trails: one point per step (at most one a second), the move bits kept with each (mounted, pet out); the newest position is where they are even before it joins the trail",
                pts.Count == 6 && pts.All(p => p.Move == (LiveMove.Mounted | LiveMove.PetOut)) && pts[5].Odo > pts[0].Odo && mt.HereOf(2)?.X == 1052, pts.Count + " " + string.Join(",", pts.Select(p => p.Move)));
            mt.Add(2, "Tess", 0, 1064, 2000, LiveMove.Indoors, t0.AddSeconds(14));
            var hz = mt.HereOf(2);
            Check("member trails: indoors the height is worked out like yours (LiveElevation: the building's floor, 16 yards)", hz.HasValue && hz.Value.HasZ && hz.Value.Src == ElevSrc.Floor && Math.Abs(hz.Value.Z - 16) < 0.01, hz.HasValue ? hz.Value.Src + " " + hz.Value.Z : "none");
            Check("member trails: 10 minutes like yours - eleven minutes on, nothing of it is left", mt.PointsOf(2, t0.AddMinutes(9)).Count == 7 && mt.PointsOf(2, t0.AddMinutes(11)).Count == 0);
            mt.Add(2, "Tessalt", 0, 1070, 2000, 0, t0.AddSeconds(20));
            Check("member trails: another character of theirs starts a new trail", mt.PointsOf(2, t0.AddSeconds(21)).Count == 1);
            mt.Add(3, "Bad", 0, 500, 500, 0, t0); mt.Add(4, "Mira", 0, 600, 600, 0, t0);
            bool dropped = mt.Drop(2); mt.KeepOnly(new[] { 3 });
            Check("member trails: a member who stops sharing or leaves loses their trail at once (Drop), and whoever is no longer shown goes too (KeepOnly)", dropped && !mt.Has(2) && mt.Has(3) && !mt.Has(4) && mt.Count == 1);
            mt.Clear();
            Check("member trails: switching the live layer off clears them all", mt.Count == 0);

            // the message: a member drawn like you - trail, heights, move bits, odometer, prints - thinned to at most MaxMemberPoints points
            var trail = new List<LiveTrail.Point>(); double odo = 0;
            for (int i = 0; i < 600; i++) { var p = new LiveTrail.Point { At = t0.AddSeconds(i), MapId = 0, X = 1000 + i * 4, Y = 2000, Move = i < 300 ? LiveMove.Mounted : LiveMove.Swimming, Odo = odo }; odo += 4; trail.Add(p); }
            var here = trail[599]; here.Z = 16; here.Src = ElevSrc.Floor; here.HasZ = true;
            var dots = new List<MemberDot>
            {
                new MemberDot { Id = 6, Name = "Wing", Color = "#ABD473", MapId = 0, X = here.X, Y = here.Y, Here = here, Trail = trail, At = t0.AddSeconds(600), Move = LiveMove.Swimming, Foot = "hoof", Hunter = true, Zone = "Greenvale" },
                new MemberDot { Id = 9, Name = "Isla", Color = "#69CCF0", MapId = 7, X = 100, Y = 100, Zone = "Windy Shore" },
            };
            var mj = Json.Obj(WorldMapProtocol.Members(dots, 0, 0, m => m == 7 ? "Gale Isle" : null));
            var w = (mj["list"] as List<object>).Cast<Dictionary<string, object>>().Single();
            var tl = w.List("trail"); var mv = w.List("mv"); var od = w.List("od"); var pr = w.Child("prints");
            Check("members: a member's trail, heights, move bits and odometer go with their dot in the shapes of your own (" + tl.Count / 4 + " points of 600, thinned evenly, the newest kept)",
                tl.Count / 4 <= WorldMapProtocol.MaxMemberPoints && tl.Count / 4 >= WorldMapProtocol.MaxMemberPoints - 1 && w.List("elev").Count == tl.Count / 2 && mv.Count == tl.Count / 4 && od.Count == mv.Count
                && Math.Abs(Convert.ToDouble(od[od.Count - 1]) - here.Odo) < 0.1 && Convert.ToInt32(mv[0]) == LiveMove.Mounted && Convert.ToInt32(mv[mv.Count - 1]) == LiveMove.Swimming
                && Convert.ToDouble(tl[2]) > 500 && Convert.ToDouble(tl[tl.Count - 2]) <= 1.01, Json.Write(w).Length + " chars");
            Check("members: their prints - a tauren's hooves, a hunter's pet while their move bits say it is out; the height where they are (a floor) and their move bits now",
                pr.Str("foot") == "hoof" && pr.Bool("hunter") && pr.Bool("petKnown") && w.List("hz").Count == 2 && Convert.ToDouble(w.List("hz")[0]) == 16 && Convert.ToInt32(w.List("hz")[1]) == 1 && w.Int("hmv") == LiveMove.Swimming);
            var rows = (mj["rows"] as List<object>).Cast<Dictionary<string, object>>().ToList();
            Check("members: the card's rows list everyone who shares, on any map, by name - here or the name of the world map they are on, their zone for the hover",
                rows.Count == 2 && rows[0].Str("name") == "Isla" && !rows[0].Bool("here") && rows[0].Str("map") == "Gale Isle" && rows[0].Str("zone") == "Windy Shore"
                && rows[1].Str("name") == "Wing" && rows[1].Bool("here") && rows[1].Str("map") == "" && rows[1].Str("zone") == "Greenvale", Json.Write(mj["rows"]));
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
                await WorldMapMemberTrailsPageTest(Check, P, () => t, v => t = v);

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
