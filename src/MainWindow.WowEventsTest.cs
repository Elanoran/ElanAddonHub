using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // --selftest "wowevents": WoW's own calendar (from the companion addon's saved variables) inside the Lodge calendar and the strip of upcoming events
    // above the chat. Everything runs on a fixture ElansHub.lua written into the fake WoW folder and synthetic Lodge frames (no server, no game):
    // parsing, local-time conversion, the agenda ("until Sat"), the month view's all-day bands, the two switches, the strip (what it lists, that it
    // keeps a fixed height, that it does not move the message list) and that none of it is ever sent to the server.
    public partial class MainWindow
    {
        static long UnixSecs(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();
        static string LuaStr(string s) => "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        // the calendar clock of the fixture is the realm's, UTC-5: realm wall-clock -> the real UTC instant
        static DateTime RealmUtc(int day, int hour, int minute) => new DateTime(2026, 10, day, hour, minute, 0, DateTimeKind.Utc).AddHours(5);
        static DateTime Utc(int day, int hour, int minute = 0) => new DateTime(2026, 10, day, hour, minute, 0, DateTimeKind.Utc);

        static string WowEv(string title, string kind, DateTime? start, DateTime? end, string startText = null, string endText = null, bool allDay = false, int icon = 0, string desc = null, string status = null, int days = 0, bool seq = false)
        {
            var sb = new StringBuilder("\t\t\t{\n");
            void Kv(string k, string v) { if (v != null) sb.Append("\t\t\t\t[\"" + k + "\"] = " + v + ",\n"); }
            Kv("title", title == null ? null : LuaStr(title)); Kv("kind", kind == null ? null : LuaStr(kind));
            Kv("start", start == null ? null : UnixSecs(start.Value).ToString(CultureInfo.InvariantCulture));
            Kv("finish", end == null ? null : UnixSecs(end.Value).ToString(CultureInfo.InvariantCulture));
            Kv("startText", startText == null ? null : LuaStr(startText)); Kv("endText", endText == null ? null : LuaStr(endText));
            Kv("allDay", allDay ? "true" : null); Kv("icon", icon > 0 ? icon.ToString() : null); Kv("days", days > 0 ? days.ToString() : null); Kv("seq", seq ? "true" : null);
            Kv("desc", desc == null ? null : LuaStr(desc)); Kv("status", status == null ? null : LuaStr(status)); Kv("src", LuaStr("calendar"));
            sb.Append("\t\t\t},\n");
            return sb.ToString();
        }

        static string WowLua(string events, int? offset = -18000, int v = 2) =>
            "ElansHubDB = {\n\t[\"current\"] = \"Forever-Elan\",\n\t[\"chars\"] = {\n\t\t[\"Forever-Elan\"] = {\n\t\t\t[\"name\"] = \"Elan\",\n\t\t\t[\"realm\"] = \"Forever\",\n\t\t},\n\t},\n"
            + "\t[\"calendar\"] = {\n\t\t[\"v\"] = " + v + ",\n\t\t[\"at\"] = " + UnixSecs(CalNow.AddMinutes(-20)) + ",\n\t\t[\"realm\"] = \"Forever\",\n\t\t[\"region\"] = 3,\n"
            + (offset == null ? "" : "\t\t[\"serverOffset\"] = " + offset + ",\n") + "\t\t[\"localOffset\"] = 7200,\n\t\t[\"tz\"] = \"Times in the in-game calendar are the realm's clock.\",\n"
            + "\t\t[\"events\"] = {\n" + events + "\t\t},\n\t},\n\t[\"bugs\"] = {},\n}\n";

        const int IconDarkmoon = 135001, IconHallow = 135002;

        // the events of the fixture: two holidays (one running, one coming), a raid reset, my events and a guild event, one that is too far away, one with
        // only the calendar's wall-clock text, a finished holiday and three that are unusable (a made-up kind is a player event; no time / no title is dropped)
        static string WowFixtureEvents() =>
            WowEv("Darkmoon Faire", "holiday", RealmUtc(5, 0, 0), RealmUtc(16, 23, 59), "2026-10-05 00:00", "2026-10-16 23:59", true, IconDarkmoon, "The Darkmoon Faire is in town.")
            + WowEv("Hallow's End", "holiday", RealmUtc(13, 0, 0), RealmUtc(20, 23, 59), "2026-10-13 00:00", "2026-10-20 23:59", true, IconHallow, "Trick or treat!")
            + WowEv("Molten Core resets", "raidreset", Utc(14, 15), null, "2026-10-14 10:00", null, false, 0, "Your 40 Player lockout ends.")
            + WowEv("Karazhan run", "player", Utc(14, 18), Utc(14, 21), "2026-10-14 13:00", "2026-10-14 16:00", false, 0, null, "going")
            + WowEv("Guild night", "guild", Utc(15, 19), null, "2026-10-15 14:00", null, false, 0, null, "maybe")
            + WowEv("Far guild raid", "guild", Utc(25, 19), null, "2026-10-25 14:00", null, false, 0, null, "going")
            + WowEv("Text only", "banana", null, null, "2026-10-13 21:30", null, false, 0, null, "invited")
            + WowEv("Old holiday", "holiday", new DateTime(2026, 9, 18, 5, 0, 0, DateTimeKind.Utc), new DateTime(2026, 9, 25, 4, 59, 0, DateTimeKind.Utc), "2026-09-18 00:00", "2026-09-24 23:59", true, IconDarkmoon)
            + WowEv("No time at all", "player", null, null)
            + WowEv("", "player", Utc(14, 12), null);

        static void CacheFixtureIcons()
        {
            foreach (var id in new[] { IconDarkmoon, IconHallow })
            {
                var dv = new System.Windows.Media.DrawingVisual();
                using (var dc = dv.RenderOpen()) dc.DrawRectangle(id == IconDarkmoon ? System.Windows.Media.Brushes.MediumPurple : System.Windows.Media.Brushes.DarkOrange, null, new Rect(0, 0, 32, 32));
                var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(32, 32, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bmp.Render(dv);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                Directory.CreateDirectory(IconStore.Dir);
                using (var fs = File.Create(IconStore.PathFor(id))) enc.Save(fs);
            }
        }

        string WowSvFile() => Path.Combine(settings.WowRoot, "_classic_beta_", "WTF", "Account", "WOWEVT", "SavedVariables", "ElansHub.lua");

        void WriteWow(string lua)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(WowSvFile()));
            File.WriteAllText(WowSvFile(), lua);
            WowCalendar.ResetCache();
        }

        async Task<string> WowEventsTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            Action<string, bool> check = (what, cond) => { notes.Add((cond ? "ok   " : "FAIL ") + what); ok &= cond; };
            var hadClock = Session.Clock; var hadShown = Session.IsShownToUser; var hadZone = CalendarRules.Zone; var hadToastsOff = settings.ToastsOff;
            var hadStripOff = settings.EventsStripOff; var hadWowOff = settings.WowEventsOff; var hadW = Width; var hadH = Height;
            var sent = new List<Dictionary<string, object>>();
            try
            {
                Session.Disconnect();
                TabLodge.IsChecked = true;
                Width = 1180; Height = 760;
                LodgePage.ShowChatForTest();
                Session.IsShownToUser = () => false;
                Session.Clock = () => CalNow;
                Session.SentForTest = o => sent.Add(o);
                settings.ToastsOff = false; settings.EventsStripOff = false; settings.WowEventsOff = false;
                var plus2 = TimeZoneInfo.CreateCustomTimeZone("test+2", TimeSpan.FromHours(2), "Test +2", "Test +2");
                CalendarRules.Zone = () => plus2;
                CacheFixtureIcons();
                WowCalendar.TestSource = null;
                await WowParsing(check);
                await WowTimes(check, plus2);
                await WowAgenda(check, sent);
                await WowMonth(check, sent);
                await WowToggles(check);
                await WowDistinct(check, sent);
                await WowDetail(check, sent);
                await WowStrip(check, sent);
                await WowNoShift(check, sent);
                await WowOldServer(check);
                check("privacy: nothing about WoW events was ever sent to the server (" + sent.Count + " frames)", !sent.Any(o => Json.Write(o).IndexOf("Hallow", StringComparison.OrdinalIgnoreCase) >= 0 || Json.Write(o).IndexOf("Darkmoon", StringComparison.OrdinalIgnoreCase) >= 0 || Json.Write(o).Contains("wow:")));
            }
            catch (Exception e) { ok = false; notes.Add("crashed: " + e); }
            finally
            {
                try { if (File.Exists(WowSvFile())) File.Delete(WowSvFile()); } catch { }
                WowCalendar.ResetCache();
                CalendarRules.Zone = hadZone; Session.Clock = hadClock; Session.IsShownToUser = hadShown; settings.ToastsOff = hadToastsOff; Session.SentForTest = null;
                settings.EventsStripOff = hadStripOff; settings.WowEventsOff = hadWowOff; Width = hadW; Height = hadH;
                LodgePage.HideCalendarForTest();
                Session.Disconnect();
            }
            return "wowevents selftest: " + (ok ? "PASSED" : "FAILED") + "\r\n  " + string.Join("\r\n  ", notes);
        }

        // ---------------------------------------------------------------- parsing (the real read path: files under the WoW folder)
        async Task WowParsing(Action<string, bool> check)
        {
            WriteWow(WowLua(WowFixtureEvents()));
            var d = WowCalendar.Read(settings.WowRoot);
            check("parse: the calendar block is found in SavedVariables\\ElansHub.lua (other keys are ignored)", d.Found && d.SourceFile != null && d.SourceFile.EndsWith("ElansHub.lua"));
            check("parse: realm, region, server offset (-5 h) and the read time", d.Realm == "Forever" && d.Region == 3 && d.ServerOffsetSeconds == -18000 && d.At == UnixSecs(CalNow.AddMinutes(-20)));
            check("parse: 8 usable events; the one without a time and the one without a title are dropped", d.Events.Count == 8 && !d.Events.Any(e => e.Title == "No time at all" || e.Title.Length == 0));
            WowEvent E(string t) => d.Events.First(e => e.Title == t);
            check("parse: order is the addon's (the Lua array part is read by index)", d.Events[0].Title == "Darkmoon Faire" && d.Events[1].Title == "Hallow's End" && d.Events[6].Title == "Text only");
            var dm = E("Darkmoon Faire");
            check("parse: a holiday keeps kind, all-day flag, texture id, description and unix range", dm.Kind == "holiday" && dm.AllDay && dm.Icon == IconDarkmoon && dm.Desc == "The Darkmoon Faire is in town." && dm.Start == UnixSecs(RealmUtc(5, 0, 0)) && dm.Finish == UnixSecs(RealmUtc(16, 23, 59)));
            check("parse: player / guild events keep the invite status", E("Karazhan run").Status == "going" && E("Guild night").Status == "maybe" && E("Karazhan run").Kind == "player" && E("Guild night").Kind == "guild");
            check("parse: an unknown kind becomes a player event; an event with only wall-clock text is kept", E("Text only").Kind == "player" && E("Text only").Start == 0 && E("Text only").StartText == "2026-10-13 21:30");
            check("parse: a raid reset is one instant", E("Molten Core resets").Kind == "raidreset" && E("Molten Core resets").Finish == 0);
            // broken or foreign files never throw
            check("parse: a broken file reads as 'no calendar'", !WowCalendar.Parse("ElansHubDB = {{{ not valid").Found && !WowCalendar.Parse("").Found && !WowCalendar.Parse(null).Found);
            check("parse: a file with no calendar block reads as 'no calendar'", !WowCalendar.Parse("ElansHubDB = { [\"chars\"] = {} }").Found);
            var noClock = WowCalendar.Parse(WowLua(WowEv("Text only", "player", null, null, "2026-10-13 21:30"), null));
            check("parse: no server offset stored -> unknown (null), text-only events still load", noClock.Found && noClock.ServerOffsetSeconds == null && noClock.Events.Count == 1);
            await Task.CompletedTask;
        }

        // ---------------------------------------------------------------- local time
        async Task WowTimes(Action<string, bool> check, TimeZoneInfo plus2)
        {
            var d = WowCalendar.Read(settings.WowRoot);
            var kara = EventVM.FromWow(d.Events.First(e => e.Title == "Karazhan run"));
            var text = EventVM.FromWow(d.Events.First(e => e.Title == "Text only"));
            var mc = EventVM.FromWow(d.Events.First(e => e.Title == "Molten Core resets"));
            var dm = EventVM.FromWow(d.Events.First(e => e.Title == "Darkmoon Faire"));
            check("time: unix seconds are the real instant (Karazhan 18:00 UTC = 13:00 on the realm clock)", kara.StartUtc == Utc(14, 18) && kara.EndUtc == Utc(14, 21));
            check("time: shown in the PC's zone (UTC+2: 20:00 - 23:00), with the weekday in front", kara.StartHm == "20:00" && kara.EndHm == "23:00" && kara.TimeText == "Wed 20:00 - 23:00");
            check("time: a raid reset is a single local time (17:00 on Wed 14 Oct)", mc.StartHm == "17:00" && mc.EndVisibility == Visibility.Collapsed && mc.DayTimeText == "Wed 17:00");
            check("time: wall-clock text without unix seconds is read as this PC's local time (21:30 local = 19:30 UTC)", text.StartUtc == Utc(13, 19, 30) && text.StartHm == "21:30");
            check("time: a holiday is 'All day' with no end time shown", dm.StartHm == "All day" && dm.EndVisibility == Visibility.Collapsed && dm.TimeText == "All day" && dm.AllDay);
            var minus5 = TimeZoneInfo.CreateCustomTimeZone("test-5", TimeSpan.FromHours(-5), "Test -5", "Test -5");
            CalendarRules.Zone = () => minus5;
            var kara5 = EventVM.FromWow(d.Events.First(e => e.Title == "Karazhan run"));
            var text5 = EventVM.FromWow(d.Events.First(e => e.Title == "Text only"));
            check("time: another PC zone shows another local time (UTC-5: 13:00; text-only 21:30 local stays 21:30)", kara5.StartHm == "13:00" && text5.StartHm == "21:30" && text5.StartUtc == new DateTime(2026, 10, 14, 2, 30, 0, DateTimeKind.Utc));
            CalendarRules.Zone = () => plus2;
            await Task.CompletedTask;
        }

        // ---------------------------------------------------------------- agenda
        async Task WowAgenda(Action<string, bool> check, List<Dictionary<string, object>> sent)
        {
            Session.FeedForTest(CalWelcome("owner", CalEvents(), CalMs(CalNow)));
            Session.Select(Session.TextChannels.First(c => c.Id == "general"));
            check("session: the 8 WoW events are loaded next to the server's 7 (never mixed into them)", await Until(() => Session.WowEvents.Count == 8 && Session.Events.Count == 7 && Session.Events.All(e => !e.IsWow) && Session.WowEvents.All(e => e.IsWow)));
            check("session: reminders, the sidebar line and the 'today' badge still mean Lodge events", await Until(() => Session.NextEvent.Id == "a1" && LodgePage.CalendarBadgeForTest == "1" && LodgePage.NextEventTextForTest.StartsWith("The Deadmines")));
            LodgePage.ShowCalendarForTest();
            var cal = LodgePage.CalendarForTest;
            cal.SetModeForTest(true);
            // the agenda is drawn and the two holiday pictures have come from the icon cache
            await Until(() => cal.AgendaGroupsForTest.Count == 7 && VisualsOf<Image>(cal.AgendaListElementForTest).Count(i => i.Source != null) == 2);
            LodgePage.UpdateLayout();
            var groups = cal.AgendaGroupsForTest;
            string Ids(CalendarRules.DayGroup g) => string.Join(",", g.Events.Select(e => e.Title));
            check("agenda: WoW events sit in the same day groups (Today, Tomorrow, then Tue 13, Wed 14, Thu 15, Sat 17, Sun 25 Oct)",
                string.Join("|", groups.Select(g => g.Label)) == "Today|Tomorrow|Tuesday 13 October|Wednesday 14 October|Thursday 15 October|Saturday 17 October|Sunday 25 October");
            check("agenda: a holiday that is already running is listed under Today, first (it started on the 5th)", Ids(groups[0]) == "Darkmoon Faire,The Deadmines");
            check("agenda: Tue 13 = the coming holiday, the cancelled Lodge event, then the text-only event by local time", Ids(groups[2]) == "Hallow's End,Scholomance,Text only");
            check("agenda: Wed 14 = raid reset 17:00 then my Karazhan run 20:00", Ids(groups[3]) == "Molten Core resets,Karazhan run" && Ids(groups[4]) == "Guild night");
            check("agenda: the finished holiday is not listed", !groups.SelectMany(g => g.Events).Any(e => e.Title == "Old holiday"));
            var dm = Session.WowEvents.First(e => e.Title == "Darkmoon Faire"); var hw = Session.WowEvents.First(e => e.Title == "Hallow's End");
            check("agenda: a running holiday reads 'until Sat' (it ends on the 17th, within the week)", dm.SubLine == "until Sat" && dm.WhenLine == "All day  ·  until Sat");
            check("agenda: a coming holiday reads 'in 2 days · until Wed 21 Oct' (more than a week away: with the date)", hw.SubLine == "in 2 days  ·  until Wed 21 Oct");
            check("agenda: the type line names the kind and, for my events, the invite", dm.FillText == "Holiday" && Session.WowEvents.First(e => e.Title == "Karazhan run").FillText == "Your event  ·  going"
                && Session.WowEvents.First(e => e.Title == "Guild night").FillText == "Guild event  ·  maybe" && Session.WowEvents.First(e => e.Title == "Molten Core resets").FillText == "Raid reset");
            check("until: today -> clock time, tomorrow, within the week -> weekday, later -> weekday and date",
                CalendarRules.UntilText(Utc(11, 20), CalNow) == "until 22:00" && CalendarRules.UntilText(Utc(12, 20), CalNow) == "until tomorrow" && CalendarRules.UntilText(Utc(15, 20), CalNow) == "until Thu"
                && CalendarRules.UntilText(Utc(21, 4, 59), CalNow) == "until Wed 21 Oct" && CalendarRules.UntilText(RealmUtc(16, 23, 59).AddMinutes(1), CalNow) == "until Sat");
            check("agenda: WoW cards have no sign-up buttons or role list and cannot be edited", Session.WowEvents.All(e => e.ActionsVisibility == Visibility.Collapsed && !e.CanManage && !e.GoingEnabled));
            var texts = VisualsOf<TextBlock>(cal.AgendaListElementForTest).Select(t => t.Text).ToList();
            check("drawn: every WoW card carries its type chip (7 in the agenda: 2 Holiday, Raid reset, Guild event, 2 Your event... ), 'All day' and 'until Sat' are on screen", new[] { "Holiday", "Raid reset", "Guild event", "Your event" }.Sum(k => texts.Count(t => t == k)) == 7 && texts.Count(t => t == "Holiday") == 2 && texts.Contains("All day") && texts.Contains("until Sat"));
            var imgs = VisualsOf<Image>(cal.AgendaListElementForTest).Where(i => i.Source != null).ToList();
            check("drawn: the holiday pictures come from the local icon cache (2 holidays have one)", imgs.Count == 2 && Session.WowEvents.First(e => e.Title == "Darkmoon Faire").IconVisibility == Visibility.Visible && Session.WowEvents.First(e => e.Title == "Molten Core resets").IconVisibility == Visibility.Collapsed);
            check("drawn: the caption says WoW events update after a /reload or logout, and the switch is on", cal.WowBarVisibleForTest && cal.WowCaptionForTest.Contains("/reload") && cal.WowCaptionForTest.Contains("logout") && cal.WowSwitchOnForTest);
            check("agenda: the Lodge events are unchanged by all this (7 events, same groups' Lodge cards)", Session.Events.Count == 7 && groups.SelectMany(g => g.Events).Count(e => !e.IsWow) == 6);
        }

        // ---------------------------------------------------------------- month
        async Task WowMonth(Action<string, bool> check, List<Dictionary<string, object>> sent)
        {
            var cal = LodgePage.CalendarForTest;
            cal.ShowMonthForTest(2026, 10, new DateTime(2026, 10, 14));
            await Until(() => cal.MonthCellsForTest.Count == 42 && cal.MonthOverlayForTest.Children.Count > 0 && cal.MonthGridElementForTest.ActualHeight > 0);
            var cells = cal.MonthCellsForTest;
            MonthCellVM C(int d) => cells.First(c => c.Cell.Day == new DateTime(2026, 10, d));
            check("month: a holiday covers every day from its first to its last (Darkmoon 5-17, Hallow's End 13-21)", Enumerable.Range(5, 17).All(d => C(d).Cell.Bands.Count > 0) && C(4).Cell.Bands.Count == 0 && C(22).Cell.Bands.Count == 0);
            check("month: two holidays overlap on the 13th-17th", C(13).Cell.Bands.Count == 2 && C(17).Cell.Bands.Count == 2 && C(12).Cell.Bands.Count == 1 && C(18).Cell.Bands.Count == 1);
            var lay = cal.MonthLayoutForTest;
            int Ix(int d) => cells.IndexOf(C(d));
            // a bar is drawn per week row, labelled with its title, square on a side where it goes on (into the next row, or past a day that is full)
            bool BarOk(CalendarRules.MonthSeg s)
            {
                CalendarRules.BandSpan(s.Ev, out var bf, out var bl);
                var rs = cells[s.Row * 7].Cell.Day.Date; var re = cells[s.Row * 7 + 6].Cell.Day.Date;
                int f = Math.Max(0, (bf - rs).Days), l = Math.Min(6, (bl - rs).Days);
                return s.Label == s.Ev.Title && s.Col >= f && s.Col + s.Span - 1 <= l && s.LeftOpen == (s.Col > f || bf < rs) && s.RightOpen == (s.Col + s.Span - 1 < l || bl > re);
            }
            check("month: every holiday bar is labelled with its title, inside the days it covers, square where it goes on", lay.Bars.Any() && lay.Bars.All(BarOk));
            check("month: Darkmoon Faire (5-17) is drawn in " + lay.Bars.Count(s => s.Ev.Title == "Darkmoon Faire") + " part(s), the first of them labelled at its start, none outside 5-17", lay.Bars.Count(s => s.Ev.Title == "Darkmoon Faire") >= 1
                && lay.Bars.Where(s => s.Ev.Title == "Darkmoon Faire").All(s => Enumerable.Range(s.Row * 7 + s.Col, s.Span).All(i => cells[i].Cell.Day.Month == 10 && cells[i].Cell.Day.Day >= 5 && cells[i].Cell.Day.Day <= 17)));            var overlay = cal.MonthOverlayForTest;
            var labels = overlay.Children.OfType<Grid>().SelectMany(g => g.Children.OfType<TextBlock>()).Select(t => t.Text).ToList();
            check("drawn: the bar title is on screen once per week row it covers", labels.Count(t => t == "Darkmoon Faire") == lay.Bars.Count(s => s.Ev.Title == "Darkmoon Faire") && labels.Count(t => t == "Darkmoon Faire") >= 1 && labels.Contains(cal.MonthMetricsForTest.Wide ? "20:00 Karazhan run" : "Karazhan run"));
            check("month: a day has two lines at most under its number and nothing is lost: shown + ''+N more'' = every event of the day", cells.Select((c, i) => lay.ShownOn(i) <= 2 && lay.ShownOn(i) + lay.More[i] == c.Cell.Count).All(x => x));
            check("month: a day with an event of its own always keeps one chip (the Lodge event is not pushed out by two holidays)", cells.Select((c, i) => c.Cell.Events.Count == 0 || lay.Chips.Any(s => s.Row * 7 + s.Col == i)).All(x => x));
            check("month: the 14th has two holidays, the raid reset and Karazhan: one bar, my Karazhan run first, ''+2 more''", lay.Segs.Count(s => s.Row * 7 + s.Col <= Ix(14) && Ix(14) < s.Row * 7 + s.Col + s.Span) == 2
                && lay.Chips.Any(s => s.Row * 7 + s.Col == Ix(14) && s.Label == "20:00 Karazhan run") && lay.More[Ix(14)] == 2);
            check("month: the 25th shows the far guild raid as a chip with its time", lay.Chips.Any(s => s.Row * 7 + s.Col == Ix(25) && s.Label == "21:00 Far guild raid"));
            check("month: holidays are bars, never chips (the chips of the 14th are timed events)", lay.Chips.All(s => !s.Ev.IsBand) && lay.Bars.All(s => s.Ev.IsBand));
            check("month: bars in their colour, a holiday's colour is the one its title hashes to", lay.Bars.All(s => s.Ev.ColorKey == "Brush.Cal.Holiday." + CalendarColors.HolidayIndex(s.Ev.Title)));
            // behaviour, not pixels: the days do not grow with what is in them (all 42 the same height, room for the number and two lines), the grid is just
            // those six rows, and every bar and chip has one height
            var dayHs = VisualsOf<Grid>(cal.MonthGridElementForTest).Where(g => g.Tag is MonthCellVM).Select(g => g.ActualHeight).ToList();
            var segHs = overlay.Children.OfType<Grid>().Select(g => g.ActualHeight).ToList();
            double gridH = cal.MonthGridElementForTest.ActualHeight;
            check("drawn: fixed size - every day the same height whatever it holds (" + (dayHs.Count > 0 ? dayHs.Min() + "-" + dayHs.Max() : "none") + "), the grid six such rows (" + gridH + "), every bar and chip one height",
                dayHs.Count == 42 && dayHs.Max() - dayHs.Min() < 0.5 && dayHs.Min() >= 48 && gridH >= 6 * dayHs.Min() - 0.5 && gridH <= 6 * (dayHs.Min() + 12)
                && segHs.Count > 0 && segHs.Max() - segHs.Min() < 0.5 && segHs.Min() >= 12);            check("month: the selected day lists the bands first, then the day's events by time", cal.DayEventsForTest.Select(e => e.Title).SequenceEqual(new[] { "Darkmoon Faire", "Hallow's End", "Molten Core resets", "Karazhan run" }));
            cal.SelectDayForTest(new DateTime(2026, 10, 19));
            check("month: on the 19th the day list shows only the running holiday", cal.DayEventsForTest.Select(e => e.Title).SequenceEqual(new[] { "Hallow's End" }));
            cal.SelectDayForTest(new DateTime(2026, 10, 25));
            check("month: the far guild raid is one event on the 25th", C(25).Cell.Events.Count == 1 && C(25).Cell.Events[0].MyStatus == "going");
        }

        // ---------------------------------------------------------------- toggles
        async Task WowToggles(Action<string, bool> check)
        {
            var cal = LodgePage.CalendarForTest;
            var stripBox = (CheckBox)SettingsPage.FindName("EventsStripBox");
            var wowBox = (CheckBox)SettingsPage.FindName("WowEventsBox");
            void ClickBox(CheckBox cb, bool on) { cb.IsChecked = on; cb.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); }
            check("settings: both switches exist in Settings > Lodge and are on by default", stripBox != null && wowBox != null && !new Settings().EventsStripOff && !new Settings().WowEventsOff);
            cal.SetModeForTest(true);
            await Settle();
            cal.ClickWowSwitchForTest(false);
            check("toggle: 'Show in-game WoW events' off (on the Calendar page) is stored, and the WoW cards leave the agenda", await Until(() => settings.WowEventsOff && !cal.AgendaGroupsForTest.SelectMany(g => g.Events).Any(e => e.IsWow) && cal.AgendaGroupsForTest.Count == 4));
            check("toggle: the bar stays so it can be switched back on; the Settings switch follows", cal.WowBarVisibleForTest && !cal.WowSwitchOnForTest);
            cal.ShowMonthForTest(2026, 10, new DateTime(2026, 10, 14));
            check("toggle: off -> no bars in the month, and the 14th has no events", await Until(() => cal.MonthCellsForTest.All(c => c.Cell.Bands.Count == 0) && cal.DayEventsForTest.Count == 0));
            check("toggle: off -> the strip lists no WoW events either", !LodgePage.StripItemsForTest.Any(e => e.IsWow));
            cal.SetModeForTest(true);
            ClickBox(wowBox, true);
            check("toggle: the Settings switch turns them back on (same setting)", await Until(() => !settings.WowEventsOff && cal.AgendaGroupsForTest.SelectMany(g => g.Events).Count(e => e.IsWow) == 7 && cal.WowSwitchOnForTest));
            ClickBox(wowBox, false);
            check("toggle: ... and off from Settings", settings.WowEventsOff && Session.ViewEvents.All(e => !e.IsWow));
            ClickBox(wowBox, true);
            LodgePage.HideCalendarForTest();
            check("toggle: 'Show upcoming events above the chat' on -> the strip is shown", await Until(() => LodgePage.StripShownForTest && LodgePage.StripVisibleForTest));
            ClickBox(stripBox, false);
            check("toggle: off -> stored, the strip collapses to nothing (0 high, collapsed)", await Until(() => settings.EventsStripOff && !LodgePage.StripShownForTest && !LodgePage.StripVisibleForTest && LodgePage.StripHeightForTest == 0));
            ClickBox(stripBox, true);
            check("toggle: on again -> the strip is back at its fixed height", await Until(() => !settings.EventsStripOff && LodgePage.StripShownForTest && Math.Abs(LodgePage.StripHeightForTest - 64) < 0.5));
        }

        // ---------------------------------------------------------------- detail
        async Task WowDetail(Action<string, bool> check, List<Dictionary<string, object>> sent)
        {
            var cal = LodgePage.CalendarForTest;
            LodgePage.ShowCalendarForTest();
            cal.SetModeForTest(true);
            await Settle();
            sent.Clear();
            var hw = Session.WowEvents.First(e => e.Title == "Hallow's End");
            cal.OpenDetail(hw);
            check("detail: a WoW event opens the same detail card", await Until(() => cal.DetailOpenForTest && cal.DetailForTest == hw && VisualsOf<TextBlock>(cal.DetailBoxForTest).Any(t => t.IsVisible && t.Text == "Trick or treat!")));
            check("detail: dates span the holiday, description and source are stated", hw.DateText == "Tuesday 13 October - Wednesday 21 October" && hw.Note == "Trick or treat!" && hw.ByLine == "From your in-game calendar - only on this PC");
            check("detail: no Edit, no Cancel event, no 'Nobody has answered yet', no answer buttons", !cal.DetailEditVisibleForTest && !cal.DetailCancelVisibleForTest && hw.ActionsVisibility == Visibility.Collapsed && hw.Groups.Count == 0);
            var texts = VisualsOf<TextBlock>(cal.DetailBoxForTest).Where(t => t.IsVisible).Select(t => t.Text).ToList();
            check("detail: drawn - title, 'Holiday' chip, the description; no 'Nobody has answered yet'", texts.Contains("Hallow's End") && texts.Contains("Holiday") && texts.Contains("Trick or treat!") && !texts.Contains("Nobody has answered yet."));
            cal.CloseDetailForTest();
            await Until(() => !cal.DetailOpenForTest);
            var kara = Session.WowEvents.First(e => e.Title == "Karazhan run");
            check("detail: my in-game invite is stated, not an answer I can change", kara.MyLine == "In-game invite: going" && kara.StatusText == "Open" && !kara.IsOpen);
            check("detail: opening it sent nothing to the server", sent.Count == 0);
            LodgePage.HideCalendarForTest();
        }

        // ---------------------------------------------------------------- strip
        string CalCancelled(string id, string title, DateTime start, string status) => CalEv(id, title, start, 60, "Bob", true, 1, 1, 1, "", status);

        async Task WowStrip(Action<string, bool> check, List<Dictionary<string, object>> sent)
        {
            Session.FeedForTest(CalWelcome("owner", CalEvents(), CalMs(CalNow)));
            await Settle();
            LodgePage.HideCalendarForTest();
            await Until(() => LodgePage.StripShownForTest && Math.Abs(LodgePage.StripHeightForTest - 64) < 0.5 && LodgePage.StripItemsForTest.Count == 3);
            var up = CalendarRules.Upcoming(Session.ViewEvents, CalNow);
            check("strip: the next three events within 7 days, soonest first (Deadmines 21:00, Night run 00:30, Molten Core 20:00)", up.Select(e => e.Id).SequenceEqual(new[] { "a1", "h1", "b1" }));
            var all = CalendarRules.Upcoming(Session.ViewEvents, CalNow, 20);
            check("strip: cancelled, finished, declined, all-day, holiday and raid-reset entries are left out; the far guild raid is beyond 7 days",
                !all.Any(e => e.Id == "f1" || e.Id == "g1" || e.AllDay || e.WowKind == "holiday" || e.WowKind == "raidreset" || e.Title == "Far guild raid") && all.Count(e => e.IsWow) == 3);
            check("strip: my in-game player / guild events (Karazhan, Guild night, Text only) do count", all.Where(e => e.IsWow).Select(e => e.Title).OrderBy(x => x).SequenceEqual(new[] { "Guild night", "Karazhan run", "Text only" }));
            check("strip: shown under the channel header with a fixed height of 64, one pill per event", LodgePage.StripShownForTest && LodgePage.StripVisibleForTest && Math.Abs(LodgePage.StripHeightForTest - 64) < 0.5 && LodgePage.StripItemsForTest.Count == 3);
            var a1 = Session.EventById("a1"); var b1 = Session.EventById("b1");
            check("strip: a pill reads title, 'Today 21:00 · in 2 h', my answer and the role fill ('2/5')", a1.StripWhen == "Today 21:00  ·  in 2 h" && a1.StripStatus == "Going" && a1.StripFill == "2/5" && b1.StripFill == "3/10" && b1.StripStatus == "");
            var texts = VisualsOf<TextBlock>(LodgePage.StripItemsControlForTest).Select(t => t.Text).ToList();
            check("drawn: titles, times, fills and the answer are on screen", texts.Contains("The Deadmines") && texts.Contains("Today 21:00  ·  in 2 h") && texts.Contains("2/5") && texts.Contains("Going") && texts.Contains("Night run"));
            // WoW events in the strip (the Lodge has little this week)
            Session.FeedForTest(CalWelcome("owner", CalEv("c1", "Guild night", new DateTime(2026, 10, 12, 20, 0, 0, DateTimeKind.Utc), 0, "Bob", false, 0, 0, 0, string.Join(",", CalSig("Dax", "going", ""), CalSig("Zed", "going", ""))), CalMs(CalNow)));
            await Until(() => LodgePage.StripItemsForTest.Count == 3 && LodgePage.StripItemsForTest[0].Title == "Guild night");
            var mixed = LodgePage.StripItemsForTest;
            check("strip: a Lodge event and my in-game events share it (Guild night, Text only, Karazhan run)", mixed.Select(e => e.Title).SequenceEqual(new[] { "Guild night", "Text only", "Karazhan run" }) && mixed[1].IsWow && mixed[1].StripFill == "WoW" && mixed[0].StripFill == "2 going");
            check("strip: the height is the same for three pills as for one (fixed 64)", Math.Abs(LodgePage.StripHeightForTest - 64) < 0.5);
            // fewer events: same height, fewer pills
            settings.WowEventsOff = true; Session.WowCalendarSettingsChanged();
            check("strip: with WoW events off one pill is left and the strip is still 64 high", await Until(() => LodgePage.StripItemsForTest.Count == 1 && Math.Abs(LodgePage.StripHeightForTest - 64) < 0.5));
            settings.WowEventsOff = false; Session.WowCalendarSettingsChanged();
            await Until(() => LodgePage.StripItemsForTest.Count == 3);
            // clicking
            LodgePage.ClickStripItemForTest(0);
            check("strip: a click opens that event's detail in the Calendar", await Until(() => LodgePage.CalendarShownForTest && LodgePage.CalendarForTest.DetailOpenForTest && LodgePage.CalendarForTest.DetailForTest.Id == "c1"));
            LodgePage.CalendarForTest.CloseDetailForTest();
            LodgePage.HideCalendarForTest();
            await Until(() => !LodgePage.CalendarShownForTest && !LodgePage.CalendarForTest.DetailOpenForTest);
            LodgePage.ClickStripItemForTest(1);
            check("strip: a click on my in-game event opens its read-only detail", await Until(() => LodgePage.CalendarForTest.DetailOpenForTest && LodgePage.CalendarForTest.DetailForTest.IsWow && LodgePage.CalendarForTest.DetailForTest.Title == "Text only"));
            LodgePage.CalendarForTest.CloseDetailForTest();
            LodgePage.HideCalendarForTest();
            await Until(() => !LodgePage.CalendarShownForTest && !LodgePage.CalendarForTest.DetailOpenForTest);
            LodgePage.ClickStripAllForTest();
            check("strip: 'See all' opens the Calendar", await Until(() => LodgePage.CalendarShownForTest));
            LodgePage.HideCalendarForTest();
            await Until(() => !LodgePage.CalendarShownForTest);
            // nothing to show -> nothing at all
            Session.FeedForTest(CalWelcome("owner", "", CalMs(CalNow)));
            WriteWow(WowLua(WowEv("Hallow's End", "holiday", RealmUtc(13, 0, 0), RealmUtc(20, 23, 59), "2026-10-13 00:00", "2026-10-20 23:59", true, IconHallow)));
            Session.ReloadWowEvents(true);
            check("strip: no timed events this week (only a holiday) -> it collapses to nothing: 0 high, not visible, no pills", await Until(() => !LodgePage.StripShownForTest && !LodgePage.StripVisibleForTest && LodgePage.StripHeightForTest == 0 && LodgePage.StripItemsForTest.Count == 0));
            WriteWow(WowLua(WowFixtureEvents()));
            Session.ReloadWowEvents(true);
            check("strip: the fixture's own events bring it back", await Until(() => LodgePage.StripShownForTest && LodgePage.StripItemsForTest.Count == 3 && Math.Abs(LodgePage.StripHeightForTest - 64) < 0.5));
        }

        // ---------------------------------------------------------------- the strip must not move the message list
        async Task WowNoShift(Action<string, bool> check, List<Dictionary<string, object>> sent)
        {
            string chat(string id, int n) => "{\"t\":\"msg\",\"channel\":\"general\",\"id\":" + Q(id) + ",\"at\":" + (CalMs(CalNow) - (60 - n) * 60000L) + ",\"from\":\"Bob\",\"fromId\":2,\"text\":" + Q("message number " + n + " with a little text so the row has some height") + "}";
            var history = string.Join(",", Enumerable.Range(1, 48).Select(n => chat("n" + n, n)));
            // a quiet week: no events, so no strip
            WriteWow(WowLua(""));
            Session.FeedForTest(CalWelcome("owner", "", CalMs(CalNow), "2.11.0", true, history));
            Session.Select(Session.TextChannels.First(c => c.Id == "general"));
            Session.ReloadWowEvents(true);
            check("noshift: no events -> no strip", await Until(() => !LodgePage.StripShownForTest && LodgePage.StripHeightForTest == 0 && LodgePage.MessageScrollableForTest > 400));
            double scrollable = LodgePage.MessageScrollableForTest;
            check("noshift: the history is long enough to scroll (" + Math.Round(scrollable) + " px)", scrollable > 400);

            async Task<double> Worst(int idx, double baseY, int frames)
            {
                double worst = 0; var seen = new StringBuilder();
                for (int i = 0; i < frames; i++)
                {
                    await Task.Delay(16);
                    var y = LodgePage.FirstMessageYForTest(idx);
                    if (!double.IsNaN(y)) worst = Math.Max(worst, Math.Abs(y - baseY));
                    seen.Append(Math.Round(y - baseY, 1)).Append(' ');
                }
                return worst;
            }

            // reading in the middle of the history
            LodgePage.ScrollMessagesToForTest(scrollable / 2);
            await Until(() => Math.Abs(LodgePage.MessageOffsetForTest - scrollable / 2) < 2);
            await Settle();
            int row = 24;
            double y0 = LodgePage.FirstMessageYForTest(row);
            double off0 = LodgePage.MessageOffsetForTest;
            check("noshift: a message is in view while reading (y=" + Math.Round(y0) + ")", !double.IsNaN(y0));
            Session.FeedForTest("{\"t\":\"cal:events\",\"events\":[" + CalEv("s1", "Molten Core", new DateTime(2026, 10, 12, 18, 0, 0, DateTimeKind.Utc), 180, "Bob", true, 2, 3, 5, "") + "]}");
            double worstShow = await Worst(row, y0, 40);
            await Until(() => LodgePage.StripShownForTest && Math.Abs(LodgePage.StripHeightForTest - 64) < 0.5);
            await Settle();
            double y1 = LodgePage.FirstMessageYForTest(row);
            check("noshift: the strip appears (64 high) and the message being read stays within 1 px the whole time (worst " + worstShow.ToString("0.0", CultureInfo.InvariantCulture) + " px)", LodgePage.StripShownForTest && Math.Abs(LodgePage.StripHeightForTest - 64) < 0.5 && worstShow <= 1.5 && Math.Abs(y1 - y0) <= 1);
            check("noshift: the list's offset moved by the strip's height instead (" + Math.Round(LodgePage.MessageOffsetForTest - off0) + " px)", Math.Abs(LodgePage.MessageOffsetForTest - off0 - 64) < 1.5);
            Session.FeedForTest("{\"t\":\"cal:events\",\"events\":[" + CalCancelled("s1", "Molten Core", new DateTime(2026, 10, 12, 18, 0, 0, DateTimeKind.Utc), "cancelled") + "]}");
            double worstHide = await Worst(row, y0, 40);
            await Until(() => !LodgePage.StripShownForTest && LodgePage.StripHeightForTest == 0);
            await Settle();
            double y2 = LodgePage.FirstMessageYForTest(row);
            check("noshift: it disappears again (0 high, collapsed) and the message stays put (worst " + worstHide.ToString("0.0", CultureInfo.InvariantCulture) + " px)", !LodgePage.StripShownForTest && LodgePage.StripHeightForTest == 0 && worstHide <= 1.5 && Math.Abs(y2 - y0) <= 1 && Math.Abs(LodgePage.MessageOffsetForTest - off0) < 1.5);
            // following the newest messages: still at the bottom afterwards
            LodgePage.ScrollToEndForTest();
            await Until(() => Math.Abs(LodgePage.MessageScrollableForTest - LodgePage.MessageOffsetForTest) <= 1.5);
            Session.FeedForTest("{\"t\":\"cal:events\",\"events\":[" + CalEv("s2", "Blackwing Lair", new DateTime(2026, 10, 13, 18, 0, 0, DateTimeKind.Utc), 180, "Bob", true, 2, 3, 5, "") + "]}");
            // the strip must be fully open (the animation done) before "still at the bottom" means anything
            await Until(() => LodgePage.StripShownForTest && Math.Abs(LodgePage.StripHeightForTest - 64) < 0.5);
            await Settle();
            check("noshift: at the bottom the list stays at the bottom when the strip appears", LodgePage.StripShownForTest && Math.Abs(LodgePage.MessageScrollableForTest - LodgePage.MessageOffsetForTest) <= 1.5);
            Session.FeedForTest("{\"t\":\"cal:events\",\"events\":[" + CalCancelled("s2", "Blackwing Lair", new DateTime(2026, 10, 13, 18, 0, 0, DateTimeKind.Utc), "cancelled") + "]}");
            await Until(() => !LodgePage.StripShownForTest && LodgePage.StripHeightForTest == 0);
            await Settle();
            check("noshift: ... and when it goes", !LodgePage.StripShownForTest && Math.Abs(LodgePage.MessageScrollableForTest - LodgePage.MessageOffsetForTest) <= 1.5);
            // the strip's content changing while it stays does not change its height
            Session.FeedForTest("{\"t\":\"cal:events\",\"events\":[" + CalEv("s3", "Karazhan", new DateTime(2026, 10, 12, 18, 0, 0, DateTimeKind.Utc), 180, "Bob", true, 1, 1, 3, "") + "]}");
            await Until(() => LodgePage.StripShownForTest && LodgePage.StripItemsForTest.Count == 1 && Math.Abs(LodgePage.StripHeightForTest - 64) < 0.5);
            double h1 = LodgePage.StripHeightForTest;
            Session.FeedForTest("{\"t\":\"cal:events\",\"events\":[" + CalEv("s4", "Zul'Gurub", new DateTime(2026, 10, 12, 19, 0, 0, DateTimeKind.Utc), 180, "Bob", true, 1, 1, 3, "") + "," + CalEv("s5", "Stratholme", new DateTime(2026, 10, 12, 20, 0, 0, DateTimeKind.Utc), 180, "Bob", true, 1, 1, 3, "") + "]}");
            await Until(() => LodgePage.StripItemsForTest.Count == 3);
            check("noshift: one pill, then three pills: the strip keeps its height (" + h1 + " -> " + LodgePage.StripHeightForTest + ")", Math.Abs(h1 - 64) < 0.5 && Math.Abs(LodgePage.StripHeightForTest - 64) < 0.5 && LodgePage.StripItemsForTest.Count == 3);
        }

        // ---------------------------------------------------------------- older servers
        async Task WowOldServer(Action<string, bool> check)
        {
            Session.FeedForTest(CalWelcome("owner", "", CalMs(CalNow), "2.10.0", false));
            check("old server: no calendar feature -> no strip, no calendar row (WoW events need the Lodge calendar page)", await Until(() => !LodgePage.StripShownForTest && LodgePage.StripHeightForTest == 0 && !LodgePage.CalendarRowVisibleForTest && Session.StripEvents(CalNow).Count == 0));
            // no saved calendar at all (the addon is older / the game never ran): nothing to show, no bar
            try { File.Delete(WowSvFile()); } catch { }
            WowCalendar.ResetCache();
            Session.FeedForTest(CalWelcome("owner", CalEvents(), CalMs(CalNow)));
            await Settle();
            LodgePage.ShowCalendarForTest();
            check("no addon data: the WoW list is empty, the switch bar stays out of the way, the agenda is just the Lodge's", await Until(() => Session.WowEvents.Count == 0 && !Session.WowAvailable && !LodgePage.CalendarForTest.WowBarVisibleForTest && LodgePage.CalendarForTest.AgendaGroupsForTest.Count == 4));
            LodgePage.HideCalendarForTest();
        }
    }
}
