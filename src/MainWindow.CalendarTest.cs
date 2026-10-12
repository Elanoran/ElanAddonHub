using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // --selftest "calendar": the raid / event calendar (Lodge server 2.11) from synthetic protocol frames (no server): agenda grouping, local-time
    // display, the month grid's dots and fetch, the create / edit form and its checks, the sign-up buttons, reminders, the sidebar line, the
    // chat card and its fallback, and the calendar being hidden on an older server.
    public partial class MainWindow
    {
        string notesDebug = "";
        static long CalMs(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

        static string CalSig(string n, string s, string r, string c = "") => "{\"n\":" + Q(n) + ",\"s\":" + Q(s) + ",\"r\":" + Q(r) + ",\"c\":" + Q(c) + "}";

        static string CalEv(string id, string title, DateTime startUtc, int durMin, string by, bool roles, int tank, int healer, int dps, string signups = "",
            string status = "open", bool weekly = false, string note = "")
            => "{\"id\":" + Q(id) + ",\"title\":" + Q(title) + ",\"start\":" + CalMs(startUtc) + ",\"end\":" + (durMin > 0 ? CalMs(startUtc.AddMinutes(durMin)) : 0) + ",\"note\":" + Q(note)
            + ",\"status\":" + Q(status) + ",\"by\":" + Q(by) + ",\"roles\":" + (roles ? "true" : "false") + ",\"slots\":{\"tank\":" + tank + ",\"healer\":" + healer + ",\"dps\":" + dps + "}"
            + ",\"signups\":[" + signups + "],\"tz\":\"Europe/Berlin\",\"off\":120" + (weekly ? ",\"series\":\"s1\",\"rep\":{\"weeks\":4,\"open\":false}" : "") + ",\"created\":0}";

        // a 2.11 welcome with the calendar block
        string CalWelcome(string role, string events, long nowMs, string server = "2.11.0", bool feature = true, string history = "")
        {
            var features = "\"reply\",\"react\",\"react2\",\"pin\",\"profile\",\"displayname\",\"lfg\",\"history2\",\"search\"" + (feature ? ",\"calendar\"" : "");
            var w = FunWelcome(role, FunPeople(false), history, features, server);
            return w.Substring(0, w.Length - 1) + ",\"calendar\":{\"now\":" + nowMs + ",\"events\":[" + events + "]}}";
        }

        // the events of the synthetic lodge. Now = Sun 11 Oct 2026 17:00 UTC (19:00 in the test zone, UTC+2).
        static readonly DateTime CalNow = new DateTime(2026, 10, 11, 17, 0, 0, DateTimeKind.Utc);
        static string CalEvents()
        {
            var tomorrow20 = new DateTime(2026, 10, 12, 18, 0, 0, DateTimeKind.Utc);   // 20:00 local
            return string.Join(",",
                // today 21:00 local: I am the tank
                CalEv("a1", "The Deadmines", new DateTime(2026, 10, 11, 19, 0, 0, DateTimeKind.Utc), 120, "Bob", true, 1, 1, 3, string.Join(",", CalSig("Elan", "going", "tank", "bring water"), CalSig("Mia", "going", "healer"))),
                // tomorrow 20:00-23:00 local: a raid with a maybe
                CalEv("b1", "Molten Core", tomorrow20, 180, "Elan", true, 2, 3, 5, string.Join(",", CalSig("Mia", "going", "healer", "Lv 24 and keen"), CalSig("Kor", "going", "dps"), CalSig("Tess", "maybe", "healer"), CalSig("Pat", "going", "tank")), "open", true, "Bring fire resistance"),
                // tomorrow 22:00 local: a social evening, no roles
                CalEv("c1", "Guild night", new DateTime(2026, 10, 12, 20, 0, 0, DateTimeKind.Utc), 0, "Bob", false, 0, 0, 0, string.Join(",", CalSig("Dax", "going", ""), CalSig("Zed", "going", ""))),
                // 00:30 local on the 12th (22:30Z on the 11th): a late event belongs to TOMORROW in local time
                CalEv("h1", "Night run", new DateTime(2026, 10, 11, 22, 30, 0, DateTimeKind.Utc), 60, "Kor", true, 1, 1, 1, ""),
                // a cancelled one on the 13th
                CalEv("f1", "Scholomance", new DateTime(2026, 10, 13, 17, 0, 0, DateTimeKind.Utc), 120, "Bob", true, 1, 1, 3, "", "cancelled"),
                // next Saturday, weekly
                CalEv("d1", "Blackwing Lair", new DateTime(2026, 10, 17, 18, 0, 0, DateTimeKind.Utc), 180, "Elan", true, 2, 3, 5, "", "open", true),
                // earlier today and over: not in the agenda, still a dot in the month
                CalEv("g1", "Stockade", new DateTime(2026, 10, 11, 8, 0, 0, DateTimeKind.Utc), 60, "Bob", true, 1, 1, 3, ""));
        }

        async Task<string> CalendarTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            Action<string, bool> check = (what, cond) => { notes.Add((cond ? "ok   " : "FAIL ") + what); ok &= cond; };
            var hadClock = Session.Clock; var hadShown = Session.IsShownToUser; var hadZone = CalendarRules.Zone; var hadToastsOff = settings.ToastsOff;
            var hadCombat = Session.Toasts.InCombat; var hadToastClock = Session.Toasts.Clock;
            var sent = new List<Dictionary<string, object>>();
            try
            {
                Session.Disconnect();
                TabLodge.IsChecked = true;
                LodgePage.ShowChatForTest();
                Session.IsShownToUser = () => false;
                Session.Clock = () => CalNow;
                Session.SentForTest = o => sent.Add(o);
                settings.ToastsOff = false;
                var plus2 = TimeZoneInfo.CreateCustomTimeZone("test+2", TimeSpan.FromHours(2), "Test +2", "Test +2");
                CalendarRules.Zone = () => plus2;
                await CalOldServer(check);
                await CalAgenda(check, sent);
                await CalMonth(check, sent);
                await CalSignups(check, sent);
                await CalDetail(check, sent);
                await CalForm(check, sent);
                await CalReminders(check);
                await CalChatCard(check, sent);
                await CalTimeZones(check);
            }
            catch (Exception e) { ok = false; notes.Add("crashed: " + e); }
            finally
            {
                Session.Toasts.InCombat = hadCombat; Session.Toasts.Clock = hadToastClock; Session.Toasts.Visible.Clear();
                CalendarRules.Zone = hadZone; Session.Clock = hadClock; Session.IsShownToUser = hadShown; settings.ToastsOff = hadToastsOff; Session.SentForTest = null;
                LodgePage.HideCalendarForTest();
                Session.Disconnect();
            }
            return "calendar selftest: " + (ok ? "PASSED" : "FAILED") + "\r\n  " + string.Join("\r\n  ", notes);
        }

        // ---------------------------------------------------------------- older servers
        async Task CalOldServer(Action<string, bool> check)
        {
            var card = "{\"t\":\"msg\",\"channel\":\"general\",\"id\":\"k1\",\"at\":" + CalMs(CalNow) + ",\"from\":\"Bob\",\"fromId\":2,\"kind\":\"event\",\"event\":{\"id\":\"b1\",\"title\":\"Molten Core\",\"start\":"
                + CalMs(CalNow.AddDays(1)) + ",\"end\":0,\"by\":\"Bob\",\"status\":\"open\"},\"text\":\"[Event] Molten Core - Mon 12 Oct 17:00 UTC - Tank 0/2, Healer 0/3, DPS 0/5 (open the Calendar in Elan's Outpost to sign up)\"}";
            // a 2.10 server: no "calendar" feature at all
            Session.FeedForTest(CalWelcome("owner", "", CalMs(CalNow), "2.10.0", false, card));
            Session.Select(Session.TextChannels.First(c => c.Id == "general"));
            await Task.Delay(300);
            check("old server: no calendar feature -> the calendar is hidden (no row, no next-event line)", !Session.SupportsCalendar && !LodgePage.CalendarRowVisibleForTest && !LodgePage.NextEventVisibleForTest);
            LodgePage.ShowCalendarForTest();
            check("old server: ShowCalendar does nothing", !LodgePage.CalendarShownForTest);
            Session.FeedForTest("{\"t\":\"cal:events\",\"events\":[" + CalEv("z1", "Ghost", CalNow.AddHours(1), 60, "Bob", true, 1, 1, 1) + "]}");
            check("old server: calendar frames are ignored", Session.Events.Count == 0);
            var vm = Session.MessagesOf("general").First(m => m.Id == "k1");
            check("old server: an event card is just its readable text line", vm.Event == null && vm.TextVisibility == Visibility.Visible && vm.Text.StartsWith("[Event] Molten Core"));
            // a server that names the feature but is older than 2.11 (a broken proxy, a downgrade) is still treated as old
            Session.FeedForTest(CalWelcome("owner", "", CalMs(CalNow), "2.10.0", true));
            check("old server: version below 2.11 hides it even when the feature is listed", !Session.SupportsCalendar && !LodgePage.CalendarRowVisibleForTest);
            // a 2.11 server shows it
            Session.FeedForTest(CalWelcome("owner", CalEvents(), CalMs(CalNow)));
            await Task.Delay(300);
            check("2.11 server: the Calendar row appears and the events are loaded", Session.SupportsCalendar && LodgePage.CalendarRowVisibleForTest && Session.Events.Count == 7);
            check("2.11 server: events are kept sorted by start", Session.Events.Zip(Session.Events.Skip(1), (a, b) => a.StartUtc <= b.StartUtc).All(x => x));
        }

        // ---------------------------------------------------------------- agenda
        async Task CalAgenda(Action<string, bool> check, List<Dictionary<string, object>> sent)
        {
            LodgePage.ShowCalendarForTest();
            var cal = LodgePage.CalendarForTest;
            cal.SetModeForTest(true);
            await Task.Delay(500);
            LodgePage.UpdateLayout();
            check("page: the calendar takes the chat's place and the Calendar row is selected", LodgePage.CalendarShownForTest);
            var groups = cal.AgendaGroupsForTest;
            string Ids(CalendarRules.DayGroup g) => string.Join(",", g.Events.Select(e => e.Id));
            check("agenda: grouped by LOCAL day - Today, Tomorrow, then weekday and date",
                groups.Count == 4 && groups[0].Label == "Today" && groups[1].Label == "Tomorrow" && groups[2].Label == "Tuesday 13 October"
                && groups[3].Label == "Saturday 17 October");
            check("agenda: a late event (00:30 local) sits under Tomorrow, in time order", Ids(groups[1]) == "h1,b1,c1" && Ids(groups[0]) == "a1" && Ids(groups[2]) == "f1" && Ids(groups[3]) == "d1");
            check("agenda: an event that is over is not listed (the Stockade this morning)", !groups.SelectMany(g => g.Events).Any(e => e.Id == "g1"));
            check("agenda: a cancelled event stays, dimmed, with a Cancelled chip and no sign-up buttons",
                Session.EventById("f1").StatusText == "Cancelled" && Session.EventById("f1").CardOpacity < 1 && Session.EventById("f1").ActionsVisibility == Visibility.Collapsed);

            var a1 = Session.EventById("a1"); var b1 = Session.EventById("b1"); var c1 = Session.EventById("c1");
            check("card: start time in local time, end time under it (UTC+2: 21:00 - 23:00)", a1.StartHm == "21:00" && a1.EndHm == "23:00" && a1.TimeText == "21:00 - 23:00" && c1.EndVisibility == Visibility.Collapsed);
            check("card: the countdown follows the clock (in 2 h) and the day line reads Today 21:00", a1.Countdown == "in 2 h" && a1.DayTimeText == "Today 21:00" && b1.Countdown == "in 1 day");
            check("card: role fill reads 'Tank 1/1 · Healer 1/1 · DPS 0/3'", a1.FillText == "Tank 1/1 · Healer 1/1 · DPS 0/3");
            check("card: the maybe count follows the fill ('Tank 1/2 · Healer 1/3 · DPS 1/5 · 1 maybe')", b1.FillText == "Tank 1/2 · Healer 1/3 · DPS 1/5 · 1 maybe");
            check("card: a social event counts who is going instead of roles", c1.FillText == "2 going" && c1.RoleVisibility == Visibility.Collapsed);
            check("card: my answer is stated ('You are going as Tank')", a1.MyLine == "You are going as Tank" && b1.MyLine == "");
            check("card: weekly series says so", b1.Weekly && b1.RepeatText == "Repeats weekly" && b1.CardWhen.EndsWith("every week"));

            var texts = VisualsOf<TextBlock>(cal.AgendaListElementForTest).Select(t => t.Text).Where(t => !string.IsNullOrEmpty(t)).ToList();
            check("drawn: day headers, titles, fill lines and the note are on screen [" + string.Join("/", texts.Take(40)) + "] page " + cal.ActualWidth + "x" + cal.ActualHeight + " vis=" + cal.IsVisible + " agenda=" + cal.AgendaListElementForTest.ActualHeight + " items=" + ((System.Windows.Controls.ItemsControl)cal.AgendaListElementForTest).Items.Count + " all=" + VisualsOf<TextBlock>(cal).Count() + " lodgevis=" + LodgePage.IsVisible, texts.Contains("Today") && texts.Contains("Tomorrow") && texts.Contains("The Deadmines") && texts.Contains("Molten Core")
                && texts.Contains("Tank 1/1 · Healer 1/1 · DPS 0/3") && texts.Contains("Bring fire resistance") && texts.Contains("21:00"));
            check("drawn: the old-style text is never shown for an event row", !texts.Any(t => t.StartsWith("[Event]")));
            check("sidebar: the badge counts today's open events (1: The Deadmines)", LodgePage.CalendarBadgeForTest == "1");
            check("sidebar: the next-event line reads 'The Deadmines · Today 21:00 · in 2 h'", LodgePage.NextEventVisibleForTest && LodgePage.NextEventTextForTest == "The Deadmines · Today 21:00 · in 2 h");

            // a clock that moves on without any frame: a minute later the countdown moves (no new frames)
            Session.Clock = () => CalNow.AddMinutes(61);
            Session.CalendarTick();
            check("agenda: the countdown ticks on its own ('in 59 min' an hour later)", a1.Countdown == "in 59 min" && Session.NextEventLine.Contains("in 59 min"));
            Session.Clock = () => CalNow;
            Session.CalendarTick();

            // empty
            Session.FeedForTest(CalWelcome("owner", "", CalMs(CalNow)));
            await Task.Delay(200);
            check("agenda: nothing planned -> a calm empty state with the New event button", cal.AgendaEmptyVisibleForTest && cal.NewButtonVisibleForTest && !LodgePage.NextEventVisibleForTest);
            Session.FeedForTest(CalWelcome("owner", CalEvents(), CalMs(CalNow)));
            await Task.Delay(300);
        }

        // ---------------------------------------------------------------- month
        async Task CalMonth(Action<string, bool> check, List<Dictionary<string, object>> sent)
        {
            var cal = LodgePage.CalendarForTest;
            sent.Clear();
            cal.ShowMonthForTest(2026, 10, new DateTime(2026, 10, 12));
            await Task.Delay(300);
            var cells = cal.MonthCellsForTest;
            MonthCellVM C(int d) => cells.First(c => c.Cell.Day == new DateTime(2026, 10, d));
            check("month: 42 cells, the title is the month", cells.Count == 42 && cal.MonthTitleForTest == "October 2026");
            var lay = cal.MonthLayoutForTest;
            int Ix(int d) => cells.IndexOf(C(d));
            string[] ChipsOn(int d) => lay.Chips.Where(s => s.Row * 7 + s.Col == Ix(d)).OrderBy(s => s.Lane).Select(s => s.Label).ToArray();
            check("month: a day shows up to two labelled chips, each with its time (the 11th has two events)", string.Join("|", ChipsOn(11)) == "10:00 Stockade|21:00 The Deadmines" && lay.More[Ix(11)] == 0);
            check("month: a day with three events shows two chips and '+1 more' (the 12th), the earliest first", string.Join("|", ChipsOn(12)) == "00:30 Night run|20:00 Molten Core" && lay.More[Ix(12)] == 1 && C(12).Cell.Events.Count == 3);
            check("month: one event = one chip, an empty day none (13th, 17th, 14th)", ChipsOn(13).Length == 1 && ChipsOn(17).Length == 1 && ChipsOn(14).Length == 0 && lay.More[Ix(13)] == 0);
            var overlay = cal.MonthOverlayForTest;
            var drawn = overlay.Children.OfType<Grid>().ToList();
            var moreText = overlay.Children.OfType<TextBlock>().Select(t => t.Text).ToList();
            check("drawn: one chip per layout entry, each 16 high, and exactly one '+1 more' label", drawn.Count == lay.Segs.Count && drawn.All(g => Math.Abs(g.ActualHeight - 16) < 0.5) && moreText.SequenceEqual(new[] { "+1 more" }));
            check("drawn: Lodge chips carry the accent colour (the same colour as the Lodge type label)", drawn.Count > 0 && Session.Events.All(e => e.ColorKey == "Brush.Cal.Lodge" && e.TypeLabel == "Lodge") && ((System.Windows.Media.SolidColorBrush)Session.Events[0].BarBrush).Color == ((System.Windows.Media.SolidColorBrush)FindResource("Brush.Accent")).Color);
            var grid0 = cal.MonthGridElementForTest;
            double gridH = grid0.ActualHeight;
            check("month: fixed cell size - 42 cells of 84 x 84 slots, the grid is 504 high, with or without events", Math.Abs(gridH - 504) < 0.5 && cal.MonthCellsForTest.Count == 42
                && VisualsOf<Grid>(grid0).Where(g => g.Tag is MonthCellVM).All(g => Math.Abs(g.ActualHeight - 80) < 0.5));
            check("month: today is marked, days of other months are dimmed", C(11).Cell.IsToday && !C(12).Cell.IsToday && cells.Any(c => !c.Cell.InMonth && c.CellOpacity < 1));
            check("month: the selected day lists its events, soonest first", cal.DayEventsForTest.Select(e => e.Id).SequenceEqual(new[] { "h1", "b1", "c1" }) && cal.DayTitleForTest.StartsWith("Tomorrow") && C(12).Selected);
            cal.SelectDayForTest(new DateTime(2026, 10, 17));
            check("month: clicking a day shows that day's events", cal.DayEventsForTest.Count == 1 && cal.DayEventsForTest[0].Id == "d1" && C(17).Selected && !C(12).Selected);
            cal.SelectDayForTest(new DateTime(2026, 10, 14));
            check("month: an empty day says so", cal.DayEventsForTest.Count == 0);
            check("month: the current month is fetched once too (the welcome only carries what is still to come)", sent.Count(o => (string)o["t"] == "cal:range") == 1);
            sent.Clear();

            // a month beyond the welcome's 30 days is fetched once, and its events draw chips when the answer arrives
            cal.ShowMonthForTest(2026, 12);
            await Task.Delay(100);
            var req = sent.Where(o => (string)o["t"] == "cal:range").ToList();
            check("month: December is fetched (cal:range once, a window of 5-7 weeks)", req.Count == 1 && (double)req[0]["to"] - (double)req[0]["from"] > 34 * 86400000.0 && (double)req[0]["to"] - (double)req[0]["from"] < 62 * 86400000.0);
            cal.ShowMonthForTest(2026, 12);
            check("month: the same month is not asked for twice", sent.Count(o => (string)o["t"] == "cal:range") == 1);
            Session.FeedForTest("{\"t\":\"cal:range\",\"from\":1,\"to\":2,\"events\":[" + CalEv("x1", "Naxxramas", new DateTime(2026, 12, 5, 18, 0, 0, DateTimeKind.Utc), 180, "Elan", true, 2, 3, 5) + "]}");
            await Task.Delay(200);
            cells = cal.MonthCellsForTest;
            check("month: the fetched event shows as a chip on the 5th of December, and the grid keeps its size", cal.MonthLayoutForTest.Chips.Any(s => s.Label == "20:00 Naxxramas") && Session.Events.Any(e => e.Id == "x1") && Math.Abs(cal.MonthGridElementForTest.ActualHeight - gridH) < 0.5);
            check("month: the fetched event also joins the agenda, in a group of its own",
                cal.IsAgenda == false && CalendarRules.Agenda(Session.Events, CalNow).Last().Label == "Saturday 5 December");
            cal.SetModeForTest(true);
            await Task.Delay(200);
        }

        // ---------------------------------------------------------------- the sign-up buttons
        async Task CalSignups(Action<string, bool> check, List<Dictionary<string, object>> sent)
        {
            var cal = LodgePage.CalendarForTest;
            cal.SetModeForTest(true);
            await Task.Delay(300);
            var a1 = Session.EventById("a1"); var b1 = Session.EventById("b1"); var c1 = Session.EventById("c1"); var f1 = Session.EventById("f1");
            check("buttons: I am going as Tank on the Deadmines: Going is the chosen answer", a1.MyStatus == "going" && a1.GoingActive && !a1.MaybeActive && !a1.DeclinedActive && a1.SelectedRole == "tank");
            check("buttons: no answer yet on Molten Core: nothing chosen, all three can be pressed (tank has a place left)", b1.MyStatus == null && !b1.GoingActive && b1.GoingEnabled && b1.MaybeEnabled && b1.DeclinedEnabled && !b1.LeaveEnabled);
            check("buttons: the role list offers the roles that have places", string.Join(",", b1.RoleChoices) == "Tank,Healer,DPS" && b1.RoleVisibility == Visibility.Visible && c1.RoleVisibility == Visibility.Collapsed);

            // a full role: Going is off for it, back on for a role with room
            Session.FeedForTest("{\"t\":\"cal:events\",\"events\":[" + CalEv("b1", "Molten Core", new DateTime(2026, 10, 12, 18, 0, 0, DateTimeKind.Utc), 180, "Elan", true, 2, 3, 5,
                string.Join(",", CalSig("Pat", "going", "tank"), CalSig("Kor", "going", "tank"), CalSig("Mia", "going", "healer"))) + "]}");
            b1.SelectedRoleLabel = "Tank";
            check("buttons: Tank is full (2/2) -> Going is off with a reason; Maybe and Can't still work", !b1.GoingEnabled && b1.GoingTip == "That role is full" && b1.MaybeEnabled && b1.DeclinedEnabled);
            b1.SelectedRoleLabel = "Healer";
            check("buttons: pick Healer (1/3) -> Going is on again", b1.GoingEnabled && b1.SelectedRole == "healer");

            // what a press sends
            await Task.Delay(300);
            LodgePage.UpdateLayout();
            var allButtons = VisualsOf<Button>(cal.AgendaListElementForTest).ToList();
            notesDebug = string.Join("/", allButtons.Select(b => (b.Content as string) + ":" + (b.Tag?.GetType().Name)));
            check("dbg " + notesDebug, true);
            var buttons = VisualsOf<Button>(cal.AgendaListElementForTest).Where(b => b.Tag == b1 && b.Content is string).ToDictionary(b => (string)b.Content, b => b, StringComparer.Ordinal);
            sent.Clear();
            buttons["Going"].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check("buttons: 'Going' sends cal:signup with the status and the picked role", sent.Count == 1 && (string)sent[0]["t"] == "cal:signup" && (string)sent[0]["id"] == "b1" && (string)sent[0]["status"] == "going" && (string)sent[0]["role"] == "healer");
            check("buttons: nothing moved before the server answered", b1.MyStatus == null);
            sent.Clear();
            buttons["Maybe"].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check("buttons: 'Maybe' keeps the role as a preference", sent.Count == 1 && (string)sent[0]["status"] == "maybe" && (string)sent[0]["role"] == "healer");
            sent.Clear();
            buttons["Can't"].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check("buttons: 'Can't' sends declined without a role", sent.Count == 1 && (string)sent[0]["status"] == "declined" && !sent[0].ContainsKey("role"));

            // the answer comes back
            Session.FeedForTest("{\"t\":\"cal:events\",\"events\":[" + CalEv("b1", "Molten Core", new DateTime(2026, 10, 12, 18, 0, 0, DateTimeKind.Utc), 180, "Elan", true, 2, 3, 5,
                string.Join(",", CalSig("Kor", "going", "tank"), CalSig("Mia", "going", "healer"), CalSig("Elan", "going", "healer", "late maybe"))) + "]}");
            await Task.Delay(200);
            check("buttons: the frame makes Going the chosen answer; my line and the fill follow", b1.GoingActive && b1.MyRole == "healer" && b1.MyLine == "You are going as Healer" && b1.FillText == "Tank 1/2 · Healer 2/3 · DPS 0/5" && b1.LeaveVisibility == Visibility.Visible);
            sent.Clear();
            buttons = VisualsOf<Button>(cal.AgendaListElementForTest).Where(b => b.Tag == b1 && b.Content is string).ToDictionary(b => (string)b.Content, b => b, StringComparer.Ordinal);
            buttons["Going"].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check("buttons: pressing the answer I already gave (same role) sends nothing", sent.Count == 0 && b1.IsSameAnswer("going"));
            b1.SelectedRoleLabel = "DPS";
            check("buttons: another role is a change (moving from Healer to DPS is allowed)", !b1.IsSameAnswer("going") && b1.GoingEnabled);
            sent.Clear();
            buttons["Clear"].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check("buttons: 'Clear' sends cal:leave", sent.Count == 1 && (string)sent[0]["t"] == "cal:leave" && (string)sent[0]["id"] == "b1");
            b1.SelectedRoleLabel = "Healer";

            // closed events
            check("buttons: a cancelled event has no buttons; neither does one that is over", f1.ActionsVisibility == Visibility.Collapsed && !f1.GoingEnabled && !f1.MaybeEnabled && !Session.EventById("g1").MaybeEnabled
                && Session.EventById("g1").ActionsVisibility == Visibility.Collapsed);
            check("buttons: a social event answers without a role", c1.MyStatus == null && c1.GoingEnabled);
            sent.Clear();
            await Session.CalendarAnswer(c1, "going");
            check("buttons: ... and sends no role", sent.Count == 1 && !sent[0].ContainsKey("role") && (string)sent[0]["status"] == "going");
            check("buttons: the role I chose last is remembered for the next card", EventVM.LastRole == "healer" || EventVM.LastRole == "dps");
        }

        // ---------------------------------------------------------------- the detail card
        async Task CalDetail(Action<string, bool> check, List<Dictionary<string, object>> sent)
        {
            var cal = LodgePage.CalendarForTest;
            var b1 = Session.EventById("b1");
            Session.FeedForTest(CalWelcome("owner", CalEvents(), CalMs(CalNow)));
            await Task.Delay(300);
            b1 = Session.EventById("b1");
            cal.OpenDetail(b1);
            await Task.Delay(500);
            cal.DetailBoxForTest.UpdateLayout();
            check("detail: opens as an overlay card for the event", cal.DetailOpenForTest && cal.DetailForTest == b1);
            var groups = b1.Groups.ToList();
            check("detail: sign-ups are listed by role, then maybe (Tank, Healer, DPS, Maybe)", string.Join("|", groups.Select(g => g.Title + " " + g.CountText)) == "Tank 1/2|Healer 1/3|DPS 1/5|Maybe 1");
            var kor = groups.First(g => g.Title == "DPS").People.First();
            check("detail: names in class colour with 'Lv 60 Mage' where the game data has it", kor.Name == "Kor" && kor.Line == "Lv 60 Mage" && kor.NameBrush is System.Windows.Media.SolidColorBrush kb && kb.Color.ToString() != Avatar.Res("Brush.Text.Primary").ToString());
            var mia = groups.First(g => g.Title == "Healer").People.First();
            check("detail: Mia shows 'Lv 24 Priest' and her comment", mia.Name == "Mia" && mia.Line == "Lv 24 Priest" && mia.Comment == "Lv 24 and keen" && mia.CommentVisibility == Visibility.Visible);
            var pat = groups.First(g => g.Title == "Tank").People.First();
            check("detail: someone who is not in the online list (Appear offline, or away) is still listed by name, without game data", pat.Name == "Pat" && pat.Line == "" && pat.Member != null && pat.Member.Offline);
            check("detail: open places are counted per role ('1 place open' / '2 places open')", groups.First(g => g.Title == "Tank").OpenText == "1 place open" && groups.First(g => g.Title == "Healer").OpenText == "2 places open");
            var texts = VisualsOf<TextBlock>(cal.DetailBoxForTest).Select(t => t.Text).Where(t => !string.IsNullOrEmpty(t)).ToList();
            check("detail: shows the date, the organiser and the note", texts.Contains("Monday 12 October") && texts.Any(t => t.Contains("Organised by Elan")) && texts.Contains("Bring fire resistance")
                && texts.Contains("Lv 60 Mage") && texts.Contains("Repeats weekly"));
            check("detail: the owner (an officer) can edit and cancel anyone's event", cal.DetailEditVisibleForTest && cal.DetailCancelVisibleForTest);
            cal.CloseDetailForTest();
            await Task.Delay(300);

            // a plain member: Edit / Cancel only on their own events
            Session.FeedForTest(CalWelcome("member", CalEvents(), CalMs(CalNow)));
            await Task.Delay(300);
            var mine = Session.EventById("b1"); var theirs = Session.EventById("a1");
            check("detail: a member can manage their own event but not Bob's (the server enforces it too)", mine.CanManage && !theirs.CanManage);
            cal.OpenDetail(theirs);
            await Task.Delay(300);
            check("detail: ... so Bob's event shows no Edit / Cancel to a member", !cal.DetailEditVisibleForTest && !cal.DetailCancelVisibleForTest);
            cal.CloseDetailForTest();
            await Task.Delay(300);
            Session.FeedForTest(CalWelcome("owner", CalEvents(), CalMs(CalNow)));
            await Task.Delay(200);
        }

        // ---------------------------------------------------------------- the form
        async Task CalForm(Action<string, bool> check, List<Dictionary<string, object>> sent)
        {
            var cal = LodgePage.CalendarForTest;
            // the pure rules
            var f = new CalendarForm { Title = "Molten Core", Day = new DateTime(2026, 10, 17), Hour = 20 };
            check("form rules: a good form passes", f.Validate(CalNow) == null);
            check("form rules: a title is required", new CalendarForm { Title = "  ", Day = f.Day }.Validate(CalNow) != null);
            check("form rules: the day is required", new CalendarForm { Title = "x" }.Validate(CalNow) == "Pick the day");
            check("form rules: a role event needs a place, a social one does not", new CalendarForm { Title = "x", Day = f.Day, Tank = 0, Healer = 0, Dps = 0 }.Validate(CalNow) != null
                && new CalendarForm { Title = "x", Day = f.Day, Roles = false, Tank = 0, Healer = 0, Dps = 0 }.Validate(CalNow) == null);
            check("form rules: 41 of a role is refused", new CalendarForm { Title = "x", Day = f.Day, Tank = 41 }.Validate(CalNow) != null);
            check("form rules: a time that has passed is refused (10 minutes of grace), an edit may keep a past time",
                new CalendarForm { Title = "x", Day = new DateTime(2026, 10, 11), Hour = 18 }.Validate(CalNow) != null && new CalendarForm { Title = "x", Day = new DateTime(2026, 10, 11), Hour = 18, Editing = true }.Validate(CalNow) == null
                && new CalendarForm { Title = "x", Day = new DateTime(2026, 10, 11), Hour = 19, Minute = 5 }.Validate(CalNow) == null);
            check("form rules: more than a year ahead is refused", new CalendarForm { Title = "x", Day = new DateTime(2027, 12, 1) }.Validate(CalNow) != null);
            check("form rules: weekly repeat is 2 to 26 weeks or until cancelled", new CalendarForm { Title = "x", Day = f.Day, RepeatWeeks = 27 }.Validate(CalNow) != null && new CalendarForm { Title = "x", Day = f.Day, RepeatWeeks = 1 }.Validate(CalNow) != null
                && new CalendarForm { Title = "x", Day = f.Day, RepeatWeeks = 26 }.Validate(CalNow) == null && new CalendarForm { Title = "x", Day = f.Day, RepeatWeeks = -1 }.Validate(CalNow) == null);
            check("form rules: the start is stored as UTC (20:00 in UTC+2 is 18:00Z)", f.StartUtc.Value == new DateTime(2026, 10, 17, 18, 0, 0, DateTimeKind.Utc));

            // the real form
            cal.OpenFormForTest();
            await Task.Delay(500);
            LodgePage.UpdateLayout();
            check("form: opens as an overlay card with the LFG form's look; New event; repeat and chat channel shown", cal.FormOpenForTest && cal.FormHeadingForTest == "New event" && cal.FormRepeatAreaVisibleForTest && !cal.FormScopeVisibleForTest);
            check("form: instance suggestions are the same as the LFG form's", cal.FormSuggestionsForTest.Cast<string>().SequenceEqual(LfgCatalog.Suggest("")));
            cal.FormTitleForTest = "Mol";
            check("form: typing narrows the suggestions", cal.FormSuggestionsForTest.Cast<string>().Contains("Molten Core"));
            cal.FormTitleForTest = "";
            sent.Clear();
            check("form: an empty title is refused with a plain message", !cal.SubmitFormForTest() && cal.FormErrorForTest.Contains("title") && sent.Count == 0);
            cal.FormTitleForTest = "Molten Core";
            cal.FormRolesForTest(true);
            cal.FormStepForTest("tank", -2); cal.FormStepForTest("healer", -3); cal.FormStepForTest("dps", -5);
            check("form: no places at all is refused", !cal.SubmitFormForTest() && cal.FormErrorForTest.Contains("place") && sent.Count == 0);
            cal.FormStepForTest("tank", 2);
            cal.FormWhenForTest(0, 11, 18);
            check("form: a time that has passed is refused", !cal.SubmitFormForTest() && cal.FormErrorForTest.Contains("passed") && sent.Count == 0);
            cal.FormWhenForTest(0, 17, 20);
            cal.FormWeeklyForTest(true, 3);
            cal.FormLengthForTest(4);
            cal.FormNoteForTest = "  bring fire resistance ";
            var parsed = cal.ReadFormForTest();
            check("form: reads back what was picked (17 Oct 20:00, 3 h, 4 weeks, 2 tanks)", parsed.Day == new DateTime(2026, 10, 17) && parsed.Hour == 20 && parsed.DurationMin == 180 && parsed.RepeatWeeks == 4 && parsed.Tank == 2 && parsed.Healer == 0 && parsed.ChannelId == "general");
            check("form: a good form posts", cal.SubmitFormForTest() && !cal.FormOpenForTest);
            var req = sent.LastOrDefault(o => (string)o["t"] == "cal:create");
            check("form: sends cal:create with title, UTC start, zone id, offset, places, length, repeat and the channel",
                req != null && (string)req["title"] == "Molten Core" && (double)req["start"] == CalMs(new DateTime(2026, 10, 17, 18, 0, 0, DateTimeKind.Utc)) && (string)req["tz"] == "test+2" && (double)req["off"] == 120
                && (bool)req["roles"] && (double)((Dictionary<string, object>)req["slots"])["tank"] == 2 && (double)req["durationMin"] == 180 && (double)((Dictionary<string, object>)req["repeat"])["weeks"] == 4
                && (string)req["channel"] == "general" && (string)req["note"] == "bring fire resistance");

            // a social, one-off event that is not posted in chat
            cal.OpenFormForTest();
            await Task.Delay(300);
            cal.FormTitleForTest = "Guild night";
            cal.FormRolesForTest(false);
            cal.FormWhenForTest(0, 20, 21, 2);
            cal.FormWeeklyForTest(false);
            sent.Clear();
            var form = cal.ReadFormForTest();
            form.ChannelId = null;
            check("form: 'No roles' sends no slots; 'Don't post' says post=false", cal.SubmitFormForTest() && sent.Count == 1 && (bool)sent[0]["roles"] == false && !sent[0].ContainsKey("slots") && !sent[0].ContainsKey("repeat")
                && (double)sent[0]["start"] == CalMs(new DateTime(2026, 10, 20, 19, 30, 0, DateTimeKind.Utc)) && form.ToRequest("cal:create").ContainsKey("post"));

            // edit: no repeat / channel choices; a series event asks "this or following"
            var d1 = Session.EventById("d1");
            cal.OpenFormForTest(d1);
            await Task.Delay(400);
            check("form: Edit has the event's values, no repeat and no channel; a weekly event offers 'This and following'", cal.FormHeadingForTest == "Edit event" && cal.FormTitleForTest == "Blackwing Lair" && !cal.FormRepeatAreaVisibleForTest && cal.FormScopeVisibleForTest);
            var ef = cal.ReadFormForTest();
            check("form: the edit reads back the same instant (17 Oct 20:00 local) and length", ef.StartUtc.Value == d1.StartUtc && ef.DurationMin == 180 && ef.Tank == 2 && ef.Healer == 3 && ef.Dps == 5 && ef.Editing);
            cal.FormTitleForTest = "Blackwing Lair (full clear)";
            sent.Clear();
            check("form: saving an edit sends cal:edit with the id and scope", cal.SubmitFormForTest() && sent.Count == 1 && (string)sent[0]["t"] == "cal:edit" && (string)sent[0]["id"] == "d1" && (string)sent[0]["scope"] == "this");
            var c1 = Session.EventById("c1");
            cal.OpenFormForTest(c1);
            await Task.Delay(300);
            check("form: a one-off event has no 'following' choice; a social event opens with No roles", !cal.FormScopeVisibleForTest && !cal.ReadFormForTest().Roles);
            cal.CloseFormForTest();
            await Task.Delay(300);
            check("form: Esc / Cancel closes it", !cal.FormOpenForTest);
            // guests cannot create
            Session.FeedForTest(CalWelcome("guest", CalEvents(), CalMs(CalNow)));
            await Task.Delay(300);
            check("form: a guest sees the calendar but has no New event button", Session.SupportsCalendar && !cal.NewButtonVisibleForTest);
            Session.FeedForTest(CalWelcome("owner", CalEvents(), CalMs(CalNow)));
            await Task.Delay(200);
        }

        // ---------------------------------------------------------------- reminders
        async Task CalReminders(Action<string, bool> check)
        {
            var rem = new HashSet<string>();
            EventVM Ev(string id, DateTime start, string status, string answer, string st = "open")
            {
                Session.FeedForTest("{\"t\":\"cal:events\",\"events\":[" + CalEv(id, "Run " + id, start, 60, "Bob", true, 1, 1, 3, answer == null ? "" : CalSig("Elan", answer, answer == "declined" ? "" : "dps"), st) + "]}");
                return Session.EventById(id);
            }
            Session.FeedForTest(CalWelcome("owner", "", CalMs(CalNow)));
            var t0 = new DateTime(2026, 10, 11, 20, 0, 0, DateTimeKind.Utc);
            var going = Ev("r1", t0.AddMinutes(15), "open", "going");
            var maybe = Ev("r2", t0.AddMinutes(15), "open", "maybe");
            var declined = Ev("r3", t0.AddMinutes(15), "open", "declined");
            var none = Ev("r4", t0.AddMinutes(15), "open", null);
            var cancelled = Ev("r5", t0.AddMinutes(15), "open", "going", "cancelled");
            check("reminder: due exactly 15 minutes before the start, for Going and for Maybe", CalendarRules.ReminderDue(going, t0, rem) && CalendarRules.ReminderDue(maybe, t0, rem));
            check("reminder: not for Can't, not for no answer, not for a cancelled event", !CalendarRules.ReminderDue(declined, t0, rem) && !CalendarRules.ReminderDue(none, t0, rem) && !CalendarRules.ReminderDue(cancelled, t0, rem));
            check("reminder: not earlier than 15 minutes ahead, not at or after the start", !CalendarRules.ReminderDue(going, t0.AddSeconds(-1), rem) && CalendarRules.ReminderDue(going, t0.AddMinutes(10), rem)
                && !CalendarRules.ReminderDue(going, t0.AddMinutes(15), rem) && !CalendarRules.ReminderDue(going, t0.AddMinutes(16), rem));
            check("reminder: once per event", !CalendarRules.ReminderDue(going, t0, new HashSet<string> { "r1" }));

            // the session: a toast through the toast centre, at the 15-minute mark, once
            Session.Toasts.Visible.Clear();
            Session.Toasts.Clock = () => DateTime.UtcNow;
            Session.Clock = () => t0.AddSeconds(-1);
            int shown = Session.Toasts.Shown;
            Session.CalendarTick();
            check("reminder toast: nothing 16 minutes ahead", Session.Toasts.Shown == shown);
            Session.Clock = () => t0.AddMinutes(1);
            Session.CalendarTick();
            var toasts = Session.Toasts.Visible.Where(t => t.Kind == ToastKind.Event).ToList();
            check("reminder toast: two toasts at 14 min (Going and Maybe), titled with the minutes, kicker 'Event'", Session.Toasts.Shown == shown + 2 && toasts.Count == 2 && toasts.Any(t => t.Title == "Run r1 starts in 14 min" && t.Kicker == "Event")
                && toasts.Any(t => t.Text.StartsWith("You are going as DPS")) && toasts.Any(t => t.Text.StartsWith("You said maybe")));
            Session.CalendarTick();
            check("reminder toast: not again on the next tick", Session.Toasts.Shown == shown + 2 && Session.CalendarRemindedForTest("r1") && !Session.CalendarRemindedForTest("r3"));

            // combat hold and the global switch
            Session.Toasts.Visible.Clear();
            var held = Ev("r6", t0.AddMinutes(30), "open", "going");
            Session.Clock = () => t0.AddMinutes(16);
            Session.Toasts.InCombat = () => true;
            int shown2 = Session.Toasts.Shown;
            Session.CalendarTick();
            check("reminder toast: in combat it is held, not shown", Session.Toasts.Shown == shown2 && Session.Toasts.HeldCount == 1 && Session.Toasts.Visible.Count == 0);
            Session.Toasts.InCombat = () => false;
            Session.Toasts.Tick();
            check("reminder toast: after the fight it arrives", Session.Toasts.Visible.Any(t => t.Title.StartsWith("Run r6 starts in")));
            Session.Toasts.Visible.Clear();
            var off = Ev("r7", t0.AddMinutes(32), "open", "going");
            settings.ToastsOff = true;
            int shown3 = Session.Toasts.Shown;
            Session.Clock = () => t0.AddMinutes(18);
            Session.CalendarTick();
            check("reminder toast: with toasts switched off nothing is shown", Session.Toasts.Shown == shown3 && Session.Toasts.Visible.Count == 0);
            settings.ToastsOff = false;
            Session.Clock = () => CalNow;
            Session.Toasts.Visible.Clear();
            Session.FeedForTest(CalWelcome("owner", CalEvents(), CalMs(CalNow)));
            await Task.Delay(200);
        }

        // ---------------------------------------------------------------- the chat card
        async Task CalChatCard(Action<string, bool> check, List<Dictionary<string, object>> sent)
        {
            var cal = LodgePage.CalendarForTest;
            var b1 = Session.EventById("b1");
            var fallback = "[Event] Molten Core - Mon 12 Oct 18:00 UTC - Tank 1/2, Healer 1/3, DPS 1/5, 1 maybe - weekly - Bring fire resistance (open the Calendar in Elan's Outpost to sign up)";
            string Card(string id, string evId, string title, DateTime start) => "{\"t\":\"msg\",\"channel\":\"general\",\"id\":" + Q(id) + ",\"at\":" + CalMs(CalNow) + ",\"from\":\"Elan\",\"fromId\":1,\"kind\":\"event\",\"event\":{\"id\":" + Q(evId)
                + ",\"title\":" + Q(title) + ",\"start\":" + CalMs(start) + ",\"end\":0,\"by\":\"Elan\",\"status\":\"open\",\"weekly\":true},\"text\":" + Q(fallback) + "}";
            LodgePage.HideCalendarForTest();
            Session.FeedForTest(CalWelcome("owner", CalEvents(), CalMs(CalNow), "2.11.0", true, Card("k1", "b1", "Molten Core", b1.StartUtc) + "," + Card("k2", "old1", "Zul'Gurub", new DateTime(2026, 9, 3, 18, 0, 0, DateTimeKind.Utc))));
            Session.Select(Session.TextChannels.First(c => c.Id == "general"));
            await Task.Delay(500);
            LodgePage.UpdateLayout();
            var k1 = Session.MessagesOf("general").First(m => m.Id == "k1"); var k2 = Session.MessagesOf("general").First(m => m.Id == "k2");
            b1 = Session.EventById("b1");
            check("card: the chat message is the event itself (live), its fallback line is hidden", k1.Event == b1 && k1.EventVisibility == Visibility.Visible && k1.TextVisibility == Visibility.Collapsed && k1.Text.StartsWith("[Event] Molten Core"));
            var texts = VisualsOf<TextBlock>(LodgePage.ChatListForTest).Select(t => t.Text).Where(t => !string.IsNullOrEmpty(t)).ToList();
            var buttons = VisualsOf<Button>(LodgePage.ChatListForTest).Where(b => b.Content as string == "Open in Calendar").ToList();
            check("card: shows the title, the when line with the countdown and the role fill; a button links to the Calendar",
                texts.Contains("Molten Core") && texts.Any(t => t.StartsWith("Tomorrow 20:00") && t.Contains("every week")) && texts.Contains("Tank 1/2 · Healer 1/3 · DPS 1/5 · 1 maybe") && buttons.Count == 2);
            check("card: no '(edited)' and the fallback line is not drawn", !texts.Any(t => t.StartsWith("[Event]")));
            // a sign-up changes the card without a new chat frame
            Session.FeedForTest("{\"t\":\"cal:events\",\"events\":[" + CalEv("b1", "Molten Core", b1.StartUtc, 180, "Elan", true, 2, 3, 5, CalSig("Mia", "going", "healer") + "," + CalSig("Kor", "going", "dps")) + "]}");
            check("card: follows the event's sign-ups live", k1.Event.FillText == "Tank 0/2 · Healer 1/3 · DPS 1/5");
            Session.FeedForTest("{\"t\":\"edited\",\"channel\":\"general\",\"id\":\"k1\",\"text\":\"[Event] Molten Core - Mon 12 Oct 18:00 UTC - Tank 0/2 (cancelled)\",\"card\":true}");
            check("card: the server's text update for old clients does not mark the card (edited)", !k1.Edited && k1.Text.Contains("Tank 0/2"));
            // click -> the calendar opens on the event
            sent.Clear();
            buttons[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(500);
            check("card: 'Open in Calendar' shows the calendar and opens the event's detail", LodgePage.CalendarShownForTest && cal.DetailOpenForTest && cal.DetailForTest == b1);
            cal.CloseDetailForTest();
            await Task.Delay(300);
            // an event outside the loaded window: a stand-in from the card, resolved when the month arrives
            check("card: an old event outside the window is a stand-in (title and time from the card) with no sign-up numbers", k2.Event != null && k2.Event.Stub && k2.Event.Title == "Zul'Gurub" && k2.Event.FillText == "");
            LodgePage.HideCalendarForTest();
            await Task.Delay(200);
            sent.Clear();
            LodgePage.OpenCalendarEvent(k2.Event);
            await Task.Delay(300);
            check("card: opening it asks the server for that month (cal:range)", sent.Any(o => (string)o["t"] == "cal:range") && cal.IsAgenda == false);
            Session.FeedForTest("{\"t\":\"cal:range\",\"from\":1,\"to\":2,\"events\":[" + CalEv("old1", "Zul'Gurub", new DateTime(2026, 9, 3, 18, 0, 0, DateTimeKind.Utc), 180, "Elan", true, 2, 3, 5, CalSig("Kor", "going", "dps")) + "]}");
            await Task.Delay(400);
            check("card: when the month arrives the same card becomes the real event and its detail opens", !k2.Event.Stub && cal.DetailOpenForTest && k2.Event.FillText.Contains("DPS 1/5"));
            cal.CloseDetailForTest();
            await Task.Delay(300);
            cal.SetModeForTest(true);
            LodgePage.HideCalendarForTest();
            check("page: choosing a channel brings the chat back and the channel looks selected again", !LodgePage.CalendarShownForTest && !ChannelVM.CalendarShown);
            // the sidebar line opens the next event
            LodgePage.ClickNextEventForTest();
            await Task.Delay(400);
            check("sidebar: the next-event line opens the event (The Deadmines tonight)", LodgePage.CalendarShownForTest && cal.DetailOpenForTest && cal.DetailForTest.Id == "a1");
            cal.CloseDetailForTest();
            await Task.Delay(300);
            LodgePage.HideCalendarForTest();
        }

        // ---------------------------------------------------------------- time zones and DST
        async Task CalTimeZones(Action<string, bool> check)
        {
            var a1 = Session.EventById("a1"); var b1 = Session.EventById("b1");
            var minus5 = TimeZoneInfo.CreateCustomTimeZone("test-5", TimeSpan.FromHours(-5), "Test -5", "Test -5");
            CalendarRules.Zone = () => minus5;
            a1.RaiseAll(); b1.RaiseAll();
            check("time zone: the same event reads in the viewer's zone (19:00Z = 14:00 in UTC-5)", a1.StartHm == "14:00" && a1.EndHm == "16:00" && a1.DayTimeText == "Today 14:00" && Session.NextEventLine == "The Deadmines · Today 14:00 · in 2 h");
            check("time zone: 'tomorrow 20:00' in UTC+2 is 13:00 in UTC-5, the day does not change", b1.StartHm == "13:00" && CalendarRules.Agenda(Session.Events, CalNow).First(g => g.Events.Contains(b1)).Label == "Tomorrow");
            var tokyo = TimeZoneInfo.CreateCustomTimeZone("test+9", TimeSpan.FromHours(9), "Test +9", "Test +9");
            CalendarRules.Zone = () => tokyo;
            var h1 = Session.EventById("h1");
            check("time zone: far east the 20:00 raid is 03:00 the next day, and the late run (22:30Z) is Today for them but Tomorrow in UTC+2",
                b1.StartHm == "03:00" && CalendarRules.Agenda(Session.Events, CalNow).First(g => g.Events.Contains(h1)).Label == "Today");

            // DST: the form turns local wall-clock time into UTC with the real zone rules
            TimeZoneInfo berlin = null;
            try { berlin = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time"); } catch { }
            check("time zone: the Berlin zone is available for the DST checks", berlin != null);
            if (berlin != null)
            {
                CalendarRules.Zone = () => berlin;
                var summer = new CalendarForm { Title = "x", Day = new DateTime(2026, 10, 17), Hour = 20 }.StartUtc.Value;
                var winter = new CalendarForm { Title = "x", Day = new DateTime(2026, 10, 31), Hour = 20 }.StartUtc.Value;
                check("DST: 20:00 in Berlin is 18:00Z before the clocks go back (25 Oct) and 19:00Z after - the wall clock stays", summer.Hour == 18 && winter.Hour == 19);
                var gap = new CalendarForm { Title = "x", Day = new DateTime(2027, 3, 28), Hour = 2, Minute = 30 }.StartUtc.Value;
                check("DST: 02:30 on the spring change day does not exist -> it moves forward to 03:30 (01:30Z)", gap == new DateTime(2027, 3, 28, 1, 30, 0, DateTimeKind.Utc));
                var wire = new CalendarForm { Title = "x", Day = new DateTime(2026, 10, 31), Hour = 20 }.ToRequest("cal:create");
                check("DST: the request carries the zone id and the offset at THAT date (60 in winter, 120 in summer)", (string)wire["tz"] == berlin.Id && (double)wire["off"] == 60
                    && (double)new CalendarForm { Title = "x", Day = new DateTime(2026, 10, 17), Hour = 20 }.ToRequest("cal:create")["off"] == 120);
            }
            await Task.Delay(50);
        }
    }
}
