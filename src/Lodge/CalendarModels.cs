using System;
using System.Text.Json.Nodes;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media;
using ElansAddonHub.Services;
using Elans.Shared;

namespace ElansAddonHub.Lodge
{
    // The raid / event calendar (Lodge server 2.11). The server keeps every rule (who may create, role places, one answer per person,
    // recurrence in the creator's time zone); this only shows what it sends and asks for changes. All times arrive as UTC milliseconds
    // and are shown in the viewer's own time zone - CalendarRules.Zone is the one place that decides which (tests set a fixed one).

    // ---------------------------------------------------------------- pure rules (agenda, month grid, reminders, form checks)
    public static class CalendarRules
    {
        // day and month names in English like the rest of the app (times and the first weekday still follow Windows)
        public static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-GB");
        public static Func<TimeZoneInfo> Zone = () => TimeZoneInfo.Local;
        public static readonly TimeSpan RemindBefore = TimeSpan.FromMinutes(15);
        public const int MaxWeeks = 26;

        public static DateTime ToLocal(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone());

        // local wall clock -> UTC; a time that does not exist (the spring change) moves forward an hour, an ambiguous one takes the first
        public static DateTime ToUtc(DateTime local)
        {
            local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
            var z = Zone();
            if (z.IsInvalidTime(local)) local = local.AddHours(1);
            var off = z.IsAmbiguousTime(local) ? z.GetAmbiguousTimeOffsets(local).Max() : z.GetUtcOffset(local);
            return DateTime.SpecifyKind(local - off, DateTimeKind.Utc);
        }

        public static DateTime FromMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
        public static long Ms(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

        // "Today" / "Tomorrow" / "Saturday 11 October"
        public static string DayLabel(DateTime localDay, DateTime localToday)
        {
            if (localDay.Date == localToday.Date) return "Today";
            if (localDay.Date == localToday.Date.AddDays(1)) return "Tomorrow";
            return localDay.ToString("dddd d MMMM", CalendarRules.English);
        }

        // "20:00" or "20:00 - 23:00" in local time
        public static string TimeRange(DateTime startUtc, DateTime endUtc)
        {
            var s = ToLocal(startUtc).ToString("HH:mm", CultureInfo.CurrentCulture);
            if (endUtc <= startUtc) return s;
            return s + " - " + ToLocal(endUtc).ToString("HH:mm", CultureInfo.CurrentCulture);
        }

        // "Today 20:00" / "Tomorrow 20:00" / "Sat 20:00" (within the week) / "Sat 24 Oct 20:00"
        public static string ShortWhen(DateTime startUtc, DateTime nowUtc)
        {
            var l = ToLocal(startUtc); var today = ToLocal(nowUtc).Date;
            var hm = l.ToString("HH:mm", CultureInfo.CurrentCulture);
            if (l.Date == today) return "Today " + hm;
            if (l.Date == today.AddDays(1)) return "Tomorrow " + hm;
            if (l.Date > today && l.Date < today.AddDays(7)) return l.ToString("ddd", CalendarRules.English) + " " + hm;
            return l.ToString("ddd d MMM", CalendarRules.English) + " " + hm;
        }

        // the short countdown of the sidebar line: "in 25 min" / "in 3 h" / "in 2 d" / "now"
        public static string ShortCountdown(DateTime startUtc, DateTime nowUtc)
        {
            var d = startUtc - nowUtc;
            if (d.TotalMinutes <= 0) return "now";
            if (d.TotalMinutes < 60) return "in " + (int)Math.Ceiling(d.TotalMinutes) + " min";
            if (d.TotalHours < 24) return "in " + (int)Math.Round(d.TotalHours) + " h";
            return "in " + (int)Math.Round(d.TotalDays) + " d";
        }

        public class DayGroup
        {
            public DateTime Day { get; set; }              // local date
            public string Label { get; set; }
            public List<EventVM> Events { get; } = new List<EventVM>();
        }

        // upcoming (or still running) events grouped by local day, soonest first; cancelled ones stay (dimmed) until they pass
        public static List<DayGroup> Agenda(IEnumerable<EventVM> events, DateTime nowUtc)
        {
            var today = ToLocal(nowUtc).Date;
            var groups = new List<DayGroup>();
            foreach (var e in events.Where(x => !x.IsOver(nowUtc)).OrderBy(x => x.StartUtc))
            {
                var day = ToLocal(e.StartUtc).Date;
                if (day < today) day = today;                       // started before midnight and still running: it is today's
                var g = groups.FirstOrDefault(x => x.Day == day);
                if (g == null) { g = new DayGroup { Day = day, Label = DayLabel(day, today) }; groups.Add(g); }
                g.Events.Add(e);
            }
            return groups;
        }

        public class DayCell
        {
            public DateTime Day { get; set; }              // local date
            public bool InMonth { get; set; }
            public bool IsToday { get; set; }
            public List<EventVM> Events { get; set; } = new List<EventVM>();   // events starting this day that are not spans (chips)
            public List<EventVM> Bands { get; set; } = new List<EventVM>();    // multi-day / all-day WoW events (holidays) that cover this day (bars)
            public int Count => Events.Count + Bands.Count;
            public string Number => Day.Day.ToString(CultureInfo.InvariantCulture);
        }

        // six weeks of day cells for one month (weeks start on the culture's first day of the week), each with its events
        public static List<DayCell> MonthGrid(int year, int month, IEnumerable<EventVM> events, DateTime nowUtc)
        {
            var first = new DateTime(year, month, 1);
            var fdw = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
            int back = ((int)first.DayOfWeek - (int)fdw + 7) % 7;
            var start = first.AddDays(-back);
            var today = ToLocal(nowUtc).Date;
            events = events.ToList();
            events = events.Where(e => !e.Recurring).ToList();                  // old-format merged weekly holidays are never drawn in the grid
            var bands = events.Where(e => e.IsBand).ToList();
            var byDay = events.Where(e => !e.IsBand).GroupBy(e => ToLocal(e.StartUtc).Date).ToDictionary(g => g.Key, g => g.OrderBy(e => e.StartUtc).ToList());
            var cells = new List<DayCell>();
            for (int i = 0; i < 42; i++)
            {
                var d = start.AddDays(i);
                cells.Add(new DayCell { Day = d, InMonth = d.Month == month, IsToday = d == today, Events = byDay.TryGetValue(d, out var l) ? l : new List<EventVM>(),
                    Bands = bands.Where(b => BandCovers(b, d)).OrderBy(b => b.StartUtc).ToList() });
            }
            return cells;
        }

        // ---------------------------------------------------------------- month cell content: labelled bars and chips
        public const int MaxLanes = 2;                      // lines of content a day cell has under its number at the smallest size: bars and chips share them (a bigger window gives more, see MonthMetrics)
        public const int Columns = 7, GridRows = 6;

        // ---------------------------------------------------------------- month grid size (Outpost 2.77)
        // The grid takes the width it is given; day cells stay square-ish (row = 0.85 x the cell's width) between MinRow and MaxRow, so they grow with the
        // window. From SideMin on, the selected day's list moves beside the grid (SidePanel wide) and a row is also kept short enough for the whole month to
        // fit the visible height. The lines a day has (Lanes) follow its height: the number on top, lines from FirstLaneTop, LanePitch apart, then "+N more".
        // Narrow cells (Compact, under 64 wide) draw slim colour bars without text; cells under WideCell drop the start time from a chip.
        public const double MinRow = 84, MaxRow = 220, FirstLaneTop = 28, LanePitch = 20, CompactPitch = 8, ChipHeight = 16, CompactChip = 6, MoreHeight = 16;
        public const double SidePanel = 320, SideGap = 24, SideMin = 1040, CompactCell = 64, WideCell = 120, MonthChrome = 112;
        public const int MostLanes = 8;

        public struct MonthMetrics
        {
            public double GridWidth, CellWidth, RowHeight;
            public int Lanes;
            public bool Side, Compact, Wide;
            public double Pitch => Compact ? CompactPitch : LanePitch;
            public double Chip => Compact ? CompactChip : ChipHeight;
            public double LaneTop(int lane) => FirstLaneTop + lane * Pitch;
            public double MoreTop => FirstLaneTop + Lanes * Pitch;            // its 14 px line ends 2 above the row's end (the cell's margin)
        }

        // width = what the month column has (inside its margins), viewportHeight = the visible height of the month page (0 = unknown)
        public static MonthMetrics Metrics(double width, double viewportHeight)
        {
            var m = new MonthMetrics();
            width = Math.Max(Columns * 32, width);
            m.Side = width >= SideMin;
            m.GridWidth = m.Side ? width - SidePanel - SideGap : width;
            m.CellWidth = m.GridWidth / Columns;
            m.Compact = m.CellWidth < CompactCell;
            m.Wide = m.CellWidth >= WideCell;
            double row = Math.Round(m.CellWidth * 0.85);
            if (m.Side && viewportHeight > 0)
            {
                double fit = Math.Floor((viewportHeight - MonthChrome) / GridRows);
                row = Math.Min(row, Math.Max(MinRow, fit));
            }
            m.RowHeight = Math.Max(MinRow, Math.Min(MaxRow, row));
            m.Lanes = Math.Max(MaxLanes, Math.Min(MostLanes, (int)Math.Floor((m.RowHeight - FirstLaneTop - MoreHeight) / m.Pitch)));
            return m;
        }

        // the words on a chip at this size: "20:00 Molten Core" when the cell is wide enough, else the title alone (a holiday is always its title)
        public static string DrawnLabel(MonthSeg s, MonthMetrics m) => m.Wide || s.IsBar ? s.Label : s.Ev.Title;

        public class MonthSeg
        {
            public int Row, Col, Span, Lane;                // grid position; Span = days covered in this week row
            public EventVM Ev;
            public bool IsBar;                              // a multi-day / all-day holiday (one continuous bar per week row); otherwise a one-day chip
            public bool LeftOpen, RightOpen;                // the bar goes on beyond this week row (drawn square on that side)
            public string Label;                            // the text the bar / chip carries ("20:00 Karaoke", "Hallow's End")
        }

        public class MonthLayout
        {
            public List<MonthSeg> Segs { get; } = new List<MonthSeg>();
            public int[] More { get; } = new int[Columns * GridRows];       // per day: events that did not fit ("+N more")
            public IEnumerable<MonthSeg> Bars => Segs.Where(s => s.IsBar);
            public IEnumerable<MonthSeg> Chips => Segs.Where(s => !s.IsBar);
            public int ShownOn(int cellIndex) => Segs.Count(s => s.Row * Columns + s.Col <= cellIndex && cellIndex < s.Row * Columns + s.Col + s.Span);
        }

        // which events a day shows first when there is not room for all: Lodge events, then my own and guild events, raid resets, holidays
        public static int ChipPriority(EventVM e) => !e.IsWow ? 0 : e.WowKind == "player" ? 1 : e.WowKind == "guild" ? 2 : e.WowKind == "raidreset" ? 3 : 4;

        public static string ChipLabel(EventVM e)
        {
            if (e.IsWow && e.WowKind == "holiday") return e.Title;
            return CalendarRules.ToLocal(e.StartUtc).ToString("HH:mm", CultureInfo.CurrentCulture) + " " + e.Title;
        }

        // The first and last local day a bar covers.
        public static void BandSpan(EventVM e, out DateTime first, out DateTime last)
        {
            first = ToLocal(e.StartUtc).Date;
            last = e.EndUtc > e.StartUtc ? ToLocal(e.EndUtc.AddMinutes(-1)).Date : first;
        }

        // Lays the six week rows out. Every day has `lanes` lines under its number (MaxLanes = 2 at the smallest size). A multi-day holiday takes ONE line for the days it covers inside a week
        // row (so it reads as a single bar, labelled where the row starts and again after a gap); the lines left on a day go to its one-day events, most important
        // first. A day always keeps one line for its own event when it has one (a Lodge event is never pushed out by two holidays). What does not fit is counted
        // as "+N more".
        public static MonthLayout Layout(IList<DayCell> cells, int lanes = MaxLanes)
        {
            int nLanes = Math.Max(1, lanes);                 // the lines this size of cell has (2 at the smallest)
            var res = new MonthLayout();
            for (int row = 0; row < GridRows && (row + 1) * Columns <= cells.Count; row++)
            {
                int r0 = row * Columns;
                var cap = new int[Columns]; var occ = new bool[Columns, nLanes]; var hidden = new int[Columns];
                for (int c = 0; c < Columns; c++) cap[c] = nLanes - (cells[r0 + c].Events.Count > 0 ? 1 : 0);
                var bars = new List<(EventVM ev, int first, int last)>();
                foreach (var ev in cells.Skip(r0).Take(Columns).SelectMany(x => x.Bands).Distinct())
                {
                    int f = -1, l = -1;
                    for (int c = 0; c < Columns; c++) if (cells[r0 + c].Bands.Contains(ev)) { if (f < 0) f = c; l = c; }
                    bars.Add((ev, f, l));
                }
                foreach (var b in bars.OrderBy(x => x.first).ThenByDescending(x => x.last - x.first).ThenBy(x => x.ev.StartUtc).ThenBy(x => x.ev.Title, StringComparer.Ordinal))
                {
                    // the line (0 or 1) that is free on most of the bar's days; the bar is drawn on the runs of days where it is free, the other days are counted as "more"
                    int lane = 0, best = -1;
                    for (int L = 0; L < nLanes; L++)
                    {
                        int n = 0;
                        for (int c = b.first; c <= b.last; c++) if (L < cap[c] && !occ[c, L]) n++;
                        if (n > best) { best = n; lane = L; }
                    }
                    BandSpan(b.ev, out var bf, out var bl);
                    bool startsBefore = bf < cells[r0].Day.Date, endsAfter = bl > cells[r0 + Columns - 1].Day.Date;
                    for (int c = b.first; c <= b.last; c++)
                    {
                        if (lane >= cap[c] || occ[c, lane]) { hidden[c]++; continue; }
                        int e = c;
                        while (e + 1 <= b.last && lane < cap[e + 1] && !occ[e + 1, lane]) e++;
                        for (int k = c; k <= e; k++) occ[k, lane] = true;
                        res.Segs.Add(new MonthSeg
                        {
                            Row = row, Col = c, Span = e - c + 1, Lane = lane, Ev = b.ev, IsBar = true, Label = b.ev.Title,
                            LeftOpen = c > b.first || startsBefore, RightOpen = e < b.last || endsAfter,
                        });
                        c = e;
                    }
                }
                for (int c = 0; c < Columns; c++)
                {
                    var free = Enumerable.Range(0, nLanes).Where(L => !occ[c, L]).ToList();
                    var chips = cells[r0 + c].Events.OrderBy(ChipPriority).ThenBy(e => e.StartUtc).ThenBy(e => e.Title, StringComparer.Ordinal).ToList();
                    var shown = chips.Take(free.Count).OrderBy(e => e.StartUtc).ThenBy(e => ChipPriority(e)).ToList();
                    for (int i = 0; i < shown.Count; i++)
                        res.Segs.Add(new MonthSeg { Row = row, Col = c, Span = 1, Lane = free[i], Ev = shown[i], IsBar = false, Label = ChipLabel(shown[i]) });
                    res.More[r0 + c] = chips.Count - shown.Count + hidden[c];
                }
            }
            return res;
        }

        // an all-day band (a holiday) covers the local days from its start to its end (the end is exclusive)
        public static bool BandCovers(EventVM e, DateTime localDay)
        {
            if (e.Recurring) return false;
            BandSpan(e, out var first, out var last);
            return localDay.Date >= first && localDay.Date <= last;
        }

        // "Sun 23:00" (an instant) / "Sun 23:00 - 00:00" (one day) / "Thu 15 Oct 17:00 - Mon 19 Oct 16:00" (several days). Ends are exclusive, so 00:00 ends the day before.
        public static string WowWhen(DateTime startUtc, DateTime endUtc)
        {
            var s = ToLocal(startUtc);
            var hm = s.ToString("HH:mm", CultureInfo.CurrentCulture);
            if (endUtc <= startUtc) return s.ToString("ddd", English) + " " + hm;
            var e = ToLocal(endUtc);
            if (ToLocal(endUtc.AddMinutes(-1)).Date > s.Date)
                return s.ToString("ddd d MMM", English) + " " + hm + " - " + e.ToString("ddd d MMM", English) + " " + e.ToString("HH:mm", CultureInfo.CurrentCulture);
            return s.ToString("ddd", English) + " " + hm + " - " + e.ToString("HH:mm", CultureInfo.CurrentCulture);
        }

        // the window of UTC instants that covers a month's grid (what a month fetch asks for): the local month widened by a day on each side
        public static void MonthWindow(int year, int month, out DateTime fromUtc, out DateTime toUtc)
        {
            fromUtc = ToUtc(new DateTime(year, month, 1).AddDays(-7));
            toUtc = ToUtc(new DateTime(year, month, 1).AddMonths(1).AddDays(7));
        }

        // "until 06:59" (today) / "until tomorrow" / "until Sun" (within the week) / "until Sat 31 Oct"; the end is exclusive, so a 00:00 end belongs to the day before
        public static string UntilText(DateTime endUtc, DateTime nowUtc)
        {
            var last = ToLocal(endUtc.AddMinutes(-1)); var today = ToLocal(nowUtc).Date;
            if (last.Date <= today) return "until " + ToLocal(endUtc).ToString("HH:mm", CultureInfo.CurrentCulture);
            if (last.Date == today.AddDays(1)) return "until tomorrow";
            if (last.Date < today.AddDays(7)) return "until " + last.ToString("ddd", English);
            return "until " + last.ToString("ddd d MMM", English);
        }

        // the strip above the chat: the next events within `days`, soonest first. Lodge events that are open and not declined, plus the player's own and
        // guild events from the in-game calendar (timed ones); holidays and raid resets are calendar material, not a reminder.
        public static List<EventVM> Upcoming(IEnumerable<EventVM> events, DateTime nowUtc, int max = 3, int days = 7) =>
            events.Where(e => !e.Stub && !e.IsCancelled && !e.IsOver(nowUtc) && e.MyStatus != "declined" && !e.AllDay
                    && (!e.IsWow || e.WowKind == "player" || e.WowKind == "guild") && e.StartUtc < nowUtc.AddDays(days))
                .OrderBy(e => e.StartUtc).Take(max).ToList();

        // a reminder is due 15 minutes before the start (and not after it) for everyone who answered Going or Maybe, once per event
        public static bool ReminderDue(EventVM e, DateTime nowUtc, ISet<string> already)
        {
            if (e == null || e.Stub || e.IsCancelled || already.Contains(e.Id)) return false;
            if (e.MyStatus != "going" && e.MyStatus != "maybe") return false;
            var left = e.StartUtc - nowUtc;
            return left > TimeSpan.Zero && left <= RemindBefore;
        }

        // the sidebar's "next event" line, or null when there is nothing coming up
        public static EventVM NextEvent(IEnumerable<EventVM> events, DateTime nowUtc) =>
            events.Where(e => !e.Stub && !e.IsCancelled && !e.IsOver(nowUtc) && e.MyStatus != "declined").OrderBy(e => e.StartUtc).FirstOrDefault();

        public static string NextEventLine(EventVM e, DateTime nowUtc) =>
            e == null ? "" : e.Title + " · " + ShortWhen(e.StartUtc, nowUtc) + " · " + ShortCountdown(e.StartUtc, nowUtc);
    }

    // ---------------------------------------------------------------- colours of the calendar kinds (tokens: Theme.xaml "calendar kinds", DESIGN.md "Calendar colours")
    public static class CalendarColors
    {
        public const int HolidayHues = 7;                    // Brush.Cal.Holiday.1 ... 7
        public static readonly string[] Kinds = { "lodge", "holiday", "raidreset", "player", "guild" };
        public static readonly string[] Groups = { "lodge", "holiday", "raidreset", "mine" };      // what the filter chips switch

        // stable across runs and PCs (FNV-1a over the lower-cased, trimmed title; string.GetHashCode is randomised per process)
        public static int HolidayIndex(string title)
        {
            uint h = 2166136261;
            foreach (var b in Encoding.UTF8.GetBytes(((title ?? "").Trim()).ToLowerInvariant())) { h ^= b; h *= 16777619; }
            return (int)(h % HolidayHues) + 1;
        }

        public static string Key(string kind, string title) =>
            kind == "holiday" ? "Brush.Cal.Holiday." + HolidayIndex(title) : kind == "raidreset" ? "Brush.Cal.Raid" : kind == "player" ? "Brush.Cal.Mine" : kind == "guild" ? "Brush.Cal.Guild" : "Brush.Cal.Lodge";

        // the filter group of an entry: lodge | holiday | raidreset | mine (my own and guild events in the game)
        public static string GroupOf(EventVM e) => !e.IsWow ? "lodge" : e.WowKind == "holiday" ? "holiday" : e.WowKind == "raidreset" ? "raidreset" : "mine";

        // WCAG contrast ratio of two colours (alpha ignored: pass blended colours)
        public static double Contrast(Color a, Color b)
        {
            double L(Color c) { double F(byte v) { var s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); } return 0.2126 * F(c.R) + 0.7152 * F(c.G) + 0.0722 * F(c.B); }
            double x = L(a), y = L(b);
            return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
        }

        // `top` drawn over `under` at top's own alpha
        public static Color Over(Color top, Color under)
        {
            double a = top.A / 255.0;
            return Color.FromRgb((byte)Math.Round(top.R * a + under.R * (1 - a)), (byte)Math.Round(top.G * a + under.G * (1 - a)), (byte)Math.Round(top.B * a + under.B * (1 - a)));
        }
    }
    // ---------------------------------------------------------------- the create / edit form's data and its checks
    public class CalendarForm
    {
        public string Title = "", Note = "", ChannelId;      // ChannelId null = no card in chat
        public DateTime? Day;                                 // local date
        public int Hour = 20, Minute;
        public int DurationMin;                               // 0 = no end
        public bool Roles = true;
        public int Tank = 2, Healer = 3, Dps = 5;
        public int RepeatWeeks;                               // 0 = once, 2..26 = that many weeks, -1 = until I cancel
        public bool Editing;
        public string Scope = "this";                         // this | following (series only)

        public DateTime? StartLocal => Day?.Date.AddHours(Hour).AddMinutes(Minute);
        public DateTime? StartUtc => StartLocal == null ? (DateTime?)null : CalendarRules.ToUtc(StartLocal.Value);

        // what can be checked here (the server checks everything again); null = fine
        public string Validate(DateTime nowUtc)
        {
            if ((Title ?? "").Trim().Length == 0) return "Give the event a title or pick an instance";
            if (Day == null) return "Pick the day";
            if (Roles && Tank + Healer + Dps == 0) return "Add at least one place, or choose No roles";
            if (Roles && (Tank > 40 || Healer > 40 || Dps > 40 || Tank < 0 || Healer < 0 || Dps < 0)) return "Each role takes 0 to 40 people";
            var start = StartUtc.Value;
            if (!Editing && start < nowUtc.AddMinutes(-10)) return "That time has already passed";
            if (start > nowUtc.AddDays(365)) return "Pick a date within the next year";
            if (RepeatWeeks > CalendarRules.MaxWeeks || RepeatWeeks == 1 || RepeatWeeks < -1) return "A weekly event can repeat for 2 to " + CalendarRules.MaxWeeks + " weeks";
            if (DurationMin < 0 || DurationMin > 1440) return "An event lasts up to 24 hours";
            return null;
        }

        // cal:create (type LodgeMsg.CalCreate) or cal:edit of event `id` (LodgeMsg.CalEdit), as the frame LodgeClient sends
        public JsonObject ToRequest(string type, string id = null)
        {
            if (type == LodgeMsg.CalEdit) { var e = new CalEditMsg { Id = id, Scope = Scope }; Fill(e); return LodgeJson.Build(e.Write); }
            var c = new CalCreateMsg { RepeatWeeks = RepeatWeeks, Channel = ChannelId }; Fill(c); return LodgeJson.Build(c.Write);
        }

        void Fill(CalEventFields f)
        {
            var zone = CalendarRules.Zone();
            f.Title = Title.Trim(); f.Note = (Note ?? "").Trim(); f.Start = CalendarRules.Ms(StartUtc.Value);
            f.Roles = Roles; f.Tz = zone.Id; f.Off = (int)zone.GetUtcOffset(StartLocal.Value).TotalMinutes;
            f.Tank = Tank; f.Healer = Healer; f.Dps = Dps; f.DurationMin = DurationMin;
        }
    }

    // ---------------------------------------------------------------- one person's answer, as the detail card lists it
    public class SignupVM : Bindable
    {
        public string Name { get; }
        public string Status { get; }            // going | maybe | declined
        public string Role { get; }
        public string Comment { get; }
        public MemberVM Member { get; }
        public bool IsMe { get; }
        public string Shown => DisplayNames.Shown(Name);
        public Brush NameBrush => Member != null && !Member.Offline ? Member.ClassBrushOrText : Avatar.Res("Brush.Text.Primary");
        // "Lv 60 Priest" where the presence data has it
        public string Line => Member != null && !Member.Offline ? Member.InGameClassLine : "";
        public Visibility LineVisibility => Line.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility CommentVisibility => string.IsNullOrEmpty(Comment) ? Visibility.Collapsed : Visibility.Visible;
        public string CommentText => string.IsNullOrEmpty(Comment) ? "" : "“" + Comment + "”";
        public SignupVM(string name, string status, string role, string comment, MemberVM member, bool isMe)
        { Name = name; Status = status; Role = role; Comment = comment; Member = member; IsMe = isMe; }
    }

    public class SignupGroupVM
    {
        public string Title { get; set; }
        public string CountText { get; set; }
        public string OpenText { get; set; }                     // "2 places open"
        public List<SignupVM> People { get; } = new List<SignupVM>();
        public Visibility OpenVisibility => string.IsNullOrEmpty(OpenText) ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---------------------------------------------------------------- one event
    public class EventVM : Bindable
    {
        public static readonly string[] RoleIds = { "tank", "healer", "dps" };
        public static string RoleLabel(string r) => r == "tank" ? "Tank" : r == "healer" ? "Healer" : r == "dps" ? "DPS" : "";
        public static string LastRole;                           // what I picked last: the next card starts there

        public string Id { get; private set; }
        public string Title { get; private set; } = "";
        public string Note { get; private set; } = "";
        public string By { get; private set; }
        public string Status { get; private set; } = "open";
        public string Series { get; private set; }
        public bool Weekly { get; private set; }
        public bool Roles { get; private set; } = true;
        public bool Stub { get; private set; }                   // only the chat card's snapshot is known (the event is outside the loaded window)
        public DateTime StartUtc { get; private set; }
        public DateTime EndUtc { get; private set; }             // MinValue = no end
        public int Tank { get; private set; }
        public int Healer { get; private set; }
        public int Dps { get; private set; }
        public List<SignupVM> Signups { get; private set; } = new List<SignupVM>();
        public ObservableCollection<SignupGroupVM> Groups { get; } = new ObservableCollection<SignupGroupVM>();
        public string MyStatus { get; private set; }             // going | maybe | declined | null
        public string MyRole { get; private set; }               // tank | healer | dps | "" | null
        public string MyComment { get; private set; } = "";
        public string Channel { get; private set; }

        string selectedRole;

        public bool IsCancelled => Status == "cancelled";
        public bool IsOver(DateTime nowUtc) => nowUtc > OverUtc;
        public DateTime OverUtc => EndUtc > StartUtc ? EndUtc : StartUtc.AddHours(IsWow ? 1 : 3);
        public bool IsOpen => !IsWow && !IsCancelled && !Stub && !IsOver(LfgVM.Clock());
        public bool IAmOrganiser => By != null && string.Equals(By, LfgVM.MyName(), StringComparison.OrdinalIgnoreCase);
        public bool CanManage => !IsWow && !Stub && !IsCancelled && (IAmOrganiser || LfgVM.IAmOfficer());
        public string ByShown => By == null ? "" : DisplayNames.Shown(By);
        public string ByLine => IsWow ? "From your in-game calendar - only on this PC" : By == null ? "" : "Organised by " + ByShown;

        // ---- WoW's own calendar (read from the companion addon's saved variables): read-only, local to this PC, never sent to the server
        public bool IsWow { get; private set; }
        public string WowKind { get; private set; } = "";           // holiday | raidreset | player | guild
        public bool AllDay { get; private set; }
        public int IconId { get; private set; }                      // texture file id for IconStore, 0 = none
        string wowStatus = "";
        public bool Recurring { get; private set; }                  // old-format data: weekly occurrences merged into one long span (listed once, never drawn in the month)
        public bool IsBand => IsWow && !Recurring && EndUtc > StartUtc && (AllDay || CalendarRules.ToLocal(EndUtc.AddMinutes(-1)).Date > CalendarRules.ToLocal(StartUtc).Date);
        bool Running => IsWow && StartUtc <= LfgVM.Clock() && !IsOver(LfgVM.Clock());
        string UntilShort => IsWow && EndUtc > StartUtc ? CalendarRules.UntilText(EndUtc, LfgVM.Clock()) : "";

        public static EventVM FromWow(WowEvent w)
        {
            var s = WowCalendar.StartUtc(w);
            if (s == null) return null;
            var e = WowCalendar.EndUtc(w);
            var vm = new EventVM { IsWow = true, WowKind = w.Kind, AllDay = w.AllDay, IconId = w.Icon, wowStatus = w.Status ?? "", Recurring = w.Recurring };
            vm.Id = "wow:" + w.Kind + ":" + s.Value.Ticks.ToString(CultureInfo.InvariantCulture) + ":" + w.Title;
            vm.Title = w.Title; vm.Note = w.Desc ?? ""; vm.Status = "open"; vm.Roles = false;
            vm.StartUtc = s.Value;
            vm.EndUtc = e != null && e.Value > s.Value ? e.Value : DateTime.MinValue;
            vm.MyStatus = w.Status == "going" || w.Status == "maybe" || w.Status == "declined" ? w.Status : null;
            return vm;
        }

        ImageSource iconImg; bool iconAsked;
        // the holiday's picture from the game's own files (IconStore: local, read-only, no network); null until it is cached / when unresolvable
        public ImageSource IconImage
        {
            get
            {
                if (!IsWow || IconId <= 0) return null;
                if (iconImg != null) return iconImg;
                iconImg = IconStore.TryGet(IconId);
                if (iconImg == null && !iconAsked && !string.IsNullOrEmpty(IconStore.WowRoot()))
                {
                    iconAsked = true;
                    var disp = Application.Current?.Dispatcher;
                    IconStore.Request(IconId, img => disp?.BeginInvoke(new Action(() => { iconImg = img; Raise(nameof(IconImage)); Raise(nameof(IconVisibility)); })));
                }
                return iconImg;
            }
        }
        public Visibility IconVisibility => IconImage != null ? Visibility.Visible : Visibility.Collapsed;
        public Visibility WowVisibility => IsWow ? Visibility.Visible : Visibility.Collapsed;

        // ---- which kind of calendar entry this is, and its colour (the same colour on the month chip, the agenda bar and the type label)
        public string KindKey => IsWow ? WowKind : "lodge";                   // lodge | holiday | raidreset | player | guild
        public string TypeLabel => !IsWow ? "Lodge" : WowKind == "holiday" ? "Holiday" : WowKind == "raidreset" ? "Raid reset" : WowKind == "guild" ? "Guild event" : "Your event";
        public string ColorKey => CalendarColors.Key(KindKey, Title);          // resource key of the solid brush, e.g. "Brush.Cal.Holiday.3"
        public Brush BarBrush => Avatar.Res(ColorKey);
        public Brush TintBrush => Avatar.Res(ColorKey + ".Tint");
        public bool MultiDay => EndUtc > StartUtc && CalendarRules.ToLocal(EndUtc.AddMinutes(-1)).Date > CalendarRules.ToLocal(StartUtc).Date;
        public Visibility TimeLineVisibility => IsWow && !Recurring ? Visibility.Visible : Visibility.Collapsed;

        // the strip above the chat: "Molten Core    8/10" over "Sat 20:00 · in 2 d    Going"
        public string StripFill
        {
            get
            {
                if (IsWow) return "WoW";
                if (!Roles) return Count("going") + " going";
                int total = RoleIds.Sum(SlotsOf), going = RoleIds.Sum(Going);
                return total > 0 ? going + "/" + total : "";
            }
        }
        public string StripWhen => CalendarRules.ShortWhen(StartUtc, LfgVM.Clock()) + "  ·  " + CalendarRules.ShortCountdown(StartUtc, LfgVM.Clock());
        public string StripStatus => MyStatus == "going" ? "Going" : MyStatus == "maybe" ? "Maybe" : "";
        public Brush StripStatusBrush => Avatar.Res(MyStatus == "going" ? "Brush.Success" : "Brush.Warn");
        public string StripTip => Title + "\n" + CalendarRules.ToLocal(StartUtc).ToString("dddd d MMMM HH:mm", CalendarRules.English) + (IsWow ? "\nFrom your in-game calendar" : "\nOpen it in the Calendar");

        public int SlotsOf(string role) => role == "tank" ? Tank : role == "healer" ? Healer : role == "dps" ? Dps : 0;
        public int Going(string role) => Signups.Count(s => s.Status == "going" && s.Role == role);
        public int Count(string status) => Signups.Count(s => s.Status == status);

        // the snapshot in an event's chat card (CalCard)
        public void ApplyStub(CalCard snap)
        {
            if (snap == null) return;
            if (Id == null) { Id = snap.Id; Stub = true; }
            if (!Stub) return;                                   // real data wins over a snapshot
            Title = SafeFile.Text(snap.Title ?? "");
            By = snap.By; Status = snap.Status ?? "open"; Weekly = snap.Weekly;
            StartUtc = CalendarRules.FromMs(snap.Start);
            EndUtc = snap.End > 0 ? CalendarRules.FromMs(snap.End) : DateTime.MinValue;
            RaiseAll();
        }

        // an event of welcome.calendar, cal:events or cal:range
        public void Apply(CalEvent d)
        {
            if (d == null) return;
            Id = d.Id ?? Id; Stub = false;
            Title = SafeFile.Text(d.Title ?? ""); Note = SafeFile.Text(d.Note ?? "");
            By = d.By; Status = d.Status ?? "open"; Series = d.Series; Weekly = d.Rep != null;
            Roles = d.Roles ?? true;
            Channel = d.Channel;
            StartUtc = CalendarRules.FromMs(d.Start);
            EndUtc = d.End > 0 ? CalendarRules.FromMs(d.End) : DateTime.MinValue;
            Tank = d.Tank; Healer = d.Healer; Dps = d.Dps;
            var me = LfgVM.MyName();
            var list = new List<SignupVM>();
            foreach (var x in d.Signups)
            {
                var n = x.Name;
                if (string.IsNullOrEmpty(n)) continue;
                var st = x.Status; if (st != "going" && st != "maybe" && st != "declined") continue;
                var isMe = me != null && string.Equals(n, me, StringComparison.OrdinalIgnoreCase);
                list.Add(new SignupVM(n, st, x.Role ?? "", SafeFile.Text(x.Comment ?? ""), LfgVM.MemberOf(n) ?? LfgSlotVM.StandIn(n), isMe));
            }
            Signups = list;
            var mine = list.FirstOrDefault(s => s.IsMe);
            MyStatus = mine?.Status; MyRole = mine?.Role; MyComment = mine?.Comment ?? "";
            BuildGroups();
            RaiseAll();
        }

        void BuildGroups()
        {
            Groups.Clear();
            if (Roles)
                foreach (var r in RoleIds)
                {
                    int total = SlotsOf(r), taken = Going(r);
                    if (total == 0 && taken == 0) continue;
                    var g = new SignupGroupVM { Title = RoleLabel(r), CountText = taken + "/" + total, OpenText = total > taken && IsOpen ? (total - taken) + (total - taken == 1 ? " place open" : " places open") : null };
                    g.People.AddRange(Signups.Where(s => s.Status == "going" && s.Role == r));
                    Groups.Add(g);
                }
            else
            {
                var g = new SignupGroupVM { Title = "Going", CountText = Count("going").ToString(CultureInfo.InvariantCulture) };
                g.People.AddRange(Signups.Where(s => s.Status == "going"));
                if (g.People.Count > 0) Groups.Add(g);
            }
            var maybe = Signups.Where(s => s.Status == "maybe").ToList();
            if (maybe.Count > 0) { var g = new SignupGroupVM { Title = "Maybe", CountText = maybe.Count.ToString(CultureInfo.InvariantCulture) }; g.People.AddRange(maybe); Groups.Add(g); }
            var no = Signups.Where(s => s.Status == "declined").ToList();
            if (no.Count > 0) { var g = new SignupGroupVM { Title = "Can't make it", CountText = no.Count.ToString(CultureInfo.InvariantCulture) }; g.People.AddRange(no); Groups.Add(g); }
        }

        // ---- what the cards show
        // WoW entries name the day: "Sun 23:00 - 00:00", and for a holiday of several days "Thu 15 Oct 17:00 - Mon 19 Oct 16:00" (never two bare times)
        public string TimeText => !IsWow ? CalendarRules.TimeRange(StartUtc, EndUtc) : Recurring ? "Repeats weekly" : AllDay ? "All day" : CalendarRules.WowWhen(StartUtc, EndUtc);
        public string StartHm => IsWow && AllDay ? "All day" : CalendarRules.ToLocal(StartUtc).ToString("HH:mm", CultureInfo.CurrentCulture);
        public string EndHm => IsWow && (AllDay || MultiDay) ? "" : EndUtc > StartUtc ? CalendarRules.ToLocal(EndUtc).ToString("HH:mm", CultureInfo.CurrentCulture) : "";
        public Visibility EndVisibility => EndHm.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        public string DateText
        {
            get
            {
                var text = CalendarRules.ToLocal(StartUtc).ToString("dddd d MMMM", CalendarRules.English);
                if (IsBand) text += " - " + CalendarRules.ToLocal(EndUtc.AddMinutes(-1)).ToString("dddd d MMMM", CalendarRules.English);
                return text;
            }
        }
        public string DayTimeText => CalendarRules.ShortWhen(StartUtc, LfgVM.Clock());
        public string Countdown => Stub || IsCancelled || Running ? "" : LfgVM.CountdownFor(StartUtc, LfgVM.Clock(), false, !IsOver(LfgVM.Clock()));
        public string WhenLine
        {
            get
            {
                var tail = Running ? UntilShort : Countdown;
                return tail.Length > 0 ? TimeText + "  ·  " + tail : TimeText;
            }
        }
        // the chat card: "Sat 20:00  ·  in 2 days  ·  every week"
        public string CardWhen => DayTimeText + (Countdown.Length > 0 ? "  ·  " + Countdown : "") + (Weekly ? "  ·  every week" : "");
        public string RepeatText => Weekly ? "Repeats weekly" : "";
        // the agenda card's second line: "in 2 days  ·  every week"
        public string SubLine
        {
            get
            {
                if (IsWow)
                {
                    var parts = new List<string>();
                    if (Recurring) return Running ? UntilShort : "";
                    if (Running) parts.Add(UntilShort.Length > 0 ? UntilShort : "now");
                    else { if (Countdown.Length > 0) parts.Add(Countdown); if (IsBand && UntilShort.Length > 0) parts.Add(UntilShort); }
                    return string.Join("  ·  ", parts);
                }
                return Countdown.Length > 0 ? Countdown + (Weekly ? "  ·  every week" : "") : Weekly ? "Every week" : "";
            }
        }
        public Visibility RepeatVisibility => Weekly ? Visibility.Visible : Visibility.Collapsed;
        public Visibility NoteVisibility => string.IsNullOrEmpty(Note) ? Visibility.Collapsed : Visibility.Visible;

        // "Tank 1/1 · Healer 0/1 · DPS 2/3" (roles) or "5 going" (no roles), then "· 2 maybe"
        public string FillText
        {
            get
            {
                if (Stub) return "";
                if (IsWow) return WowCalendar.KindLabel(WowKind) + (WowCalendar.StatusWord(wowStatus).Length > 0 && WowKind != "holiday" && WowKind != "raidreset" ? "  ·  " + WowCalendar.StatusWord(wowStatus) : "");
                var parts = new List<string>();
                if (Roles) { foreach (var r in RoleIds) if (SlotsOf(r) > 0) parts.Add(RoleLabel(r) + " " + Going(r) + "/" + SlotsOf(r)); }
                else parts.Add(Count("going") + " going");
                if (Count("maybe") > 0) parts.Add(Count("maybe") + " maybe");
                return string.Join(" · ", parts);
            }
        }

        public string StatusText => IsCancelled ? "Cancelled" : IsOver(LfgVM.Clock()) ? "Over" : "Open";
        public Brush StatusFill => Avatar.Res(IsCancelled ? "Brush.Danger.Tint" : IsOver(LfgVM.Clock()) ? "Brush.Overlay.Chip" : "Brush.Success.Tint");
        public Brush StatusBrush => Avatar.Res(IsCancelled ? "Brush.Danger" : IsOver(LfgVM.Clock()) ? "Brush.Text.Secondary" : "Brush.Success");
        public Visibility StatusVisibility => IsCancelled || IsOver(LfgVM.Clock()) ? Visibility.Visible : Visibility.Collapsed;
        public double CardOpacity => IsCancelled || IsOver(LfgVM.Clock()) ? 0.7 : 1;

        // ---- my answer
        public string MyLine
        {
            get
            {
                if (Stub || MyStatus == null) return "";
                if (IsWow) return "In-game invite: " + WowCalendar.StatusWord(MyStatus);
                if (MyStatus == "going") return Roles && !string.IsNullOrEmpty(MyRole) ? "You are going as " + RoleLabel(MyRole) : "You are going";
                if (MyStatus == "maybe") return Roles && !string.IsNullOrEmpty(MyRole) ? "You are a maybe (" + RoleLabel(MyRole) + ")" : "You are a maybe";
                return "You can't make it";
            }
        }
        public Visibility MyLineVisibility => MyLine.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ActionsVisibility => IsOpen ? Visibility.Visible : Visibility.Collapsed;
        public Visibility RoleVisibility => IsOpen && Roles ? Visibility.Visible : Visibility.Collapsed;
        public bool GoingActive => MyStatus == "going";
        public bool MaybeActive => MyStatus == "maybe";
        public bool DeclinedActive => MyStatus == "declined";

        public List<string> RoleChoices => RoleIds.Where(r => SlotsOf(r) > 0 || Going(r) > 0).Select(RoleLabel).ToList();

        // the role the buttons use: the dropdown's pick, else what I am signed up as, else what I used last, else the first role with room
        public string SelectedRole
        {
            get
            {
                if (selectedRole != null && SlotsOf(selectedRole) + Going(selectedRole) > 0) return selectedRole;
                if (!string.IsNullOrEmpty(MyRole) && SlotsOf(MyRole) > 0) return MyRole;
                if (LastRole != null && SlotsOf(LastRole) > 0) return LastRole;
                return RoleIds.FirstOrDefault(r => SlotsOf(r) > Going(r)) ?? RoleIds.FirstOrDefault(r => SlotsOf(r) > 0) ?? "";
            }
            set { if (value != selectedRole) { selectedRole = value; LastRole = value; RaiseButtons(); Raise(nameof(SelectedRoleLabel)); } }
        }
        public string SelectedRoleLabel
        {
            get => RoleLabel(SelectedRole);
            set { var id = RoleIds.FirstOrDefault(r => RoleLabel(r) == value); if (id != null) SelectedRole = id; }
        }

        bool RoleHasRoom(string role) => !Roles || (SlotsOf(role) > Going(role)) || (MyStatus == "going" && MyRole == role);
        // an answer I already gave stays enabled (it shows as chosen; pressing it again changes nothing); Going is off only for a role with no place left
        public bool GoingEnabled => IsOpen && (!Roles || (SlotsOf(SelectedRole) > 0 && RoleHasRoom(SelectedRole)));
        public bool MaybeEnabled => IsOpen;
        public bool DeclinedEnabled => IsOpen;
        // pressing the answer that is already chosen (same role) is a no-op
        public bool IsSameAnswer(string status) => MyStatus == status && (!Roles || status == "declined" || MyRole == SelectedRole);
        public bool LeaveEnabled => IsOpen && MyStatus != null;
        public Visibility LeaveVisibility => IsOpen && MyStatus != null ? Visibility.Visible : Visibility.Collapsed;
        public string GoingTip => !IsOpen ? "" : GoingEnabled ? "Take a place" : "That role is full";

        public string Summary => Title + " - " + DayTimeText + (FillText.Length > 0 ? " - " + FillText : "");

        // the countdown moves on without any frame: the view's timer asks for a refresh
        string lastWhen = "";
        public bool RefreshClock()
        {
            var w = WhenLine + "|" + StatusText + "|" + DayTimeText;
            if (w == lastWhen) return false;
            lastWhen = w;
            Raise(nameof(Countdown)); Raise(nameof(WhenLine)); Raise(nameof(CardWhen)); Raise(nameof(SubLine)); Raise(nameof(DayTimeText)); Raise(nameof(StatusText)); Raise(nameof(StatusFill)); Raise(nameof(StatusBrush)); Raise(nameof(StatusVisibility));
            Raise(nameof(CardOpacity)); Raise(nameof(StripWhen)); Raise(nameof(StripFill)); Raise(nameof(StripStatus)); RaiseButtons();
            return true;
        }

        void RaiseButtons()
        {
            foreach (var n in new[] { nameof(ActionsVisibility), nameof(RoleVisibility), nameof(GoingActive), nameof(MaybeActive), nameof(DeclinedActive), nameof(GoingEnabled), nameof(MaybeEnabled),
                nameof(DeclinedEnabled), nameof(LeaveEnabled), nameof(LeaveVisibility), nameof(GoingTip), nameof(MyLine), nameof(MyLineVisibility) }) Raise(n);
        }

        public void RaiseAll()
        {
            lastWhen = WhenLine + "|" + StatusText + "|" + DayTimeText;
            foreach (var n in new[] { nameof(Title), nameof(Note), nameof(By), nameof(ByShown), nameof(ByLine), nameof(Status), nameof(IsCancelled), nameof(IsOpen), nameof(Weekly), nameof(RepeatText), nameof(RepeatVisibility),
                nameof(NoteVisibility), nameof(TimeText), nameof(StartHm), nameof(EndHm), nameof(EndVisibility), nameof(DateText), nameof(DayTimeText), nameof(Countdown), nameof(WhenLine), nameof(CardWhen), nameof(SubLine), nameof(FillText), nameof(StatusText), nameof(StatusFill),
                nameof(StatusBrush), nameof(StatusVisibility), nameof(CardOpacity), nameof(RoleChoices), nameof(SelectedRoleLabel), nameof(Summary), nameof(CanManage), nameof(IAmOrganiser), nameof(Groups),
                nameof(StripFill), nameof(StripWhen), nameof(StripStatus), nameof(StripStatusBrush), nameof(StripTip) }) Raise(n);
            RaiseButtons();
        }
    }
}
