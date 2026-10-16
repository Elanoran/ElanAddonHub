using System;
using System.Collections.Generic;
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
                && LivePositionText.HubDescription.StartsWith("Shows where you are on the Outpost's world map, with a short trail of the last few minutes.") && LivePositionText.HubDescription.EndsWith("so the trail pauses there.")
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

            // the how-to card (not shown here): the steps and the honest description
            var card = HelpPages.Create(HelpPages.LivePosition);
            Check("how-to card: opened by its page id (" + HelpPages.LivePosition + "), titled \"" + card?.TitleForTest + "\", with the two steps and the Hub's description verbatim; an unknown id opens nothing",
                card != null && card.TitleForTest == "Share your map position" && card.StepsForTest.SequenceEqual(LivePositionText.HowToSteps) && card.AboutForTest == LivePositionText.HubDescription
                && HelpPages.Create("help:nope") == null && !HelpPages.Open("help:nope"));
            card?.Close();
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
                try { UpdateLive(); } catch (Exception) { }
            }
        }
    }
}
