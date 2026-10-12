using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using ElansAddonHub.Services;

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
            public List<EventVM> Events { get; set; } = new List<EventVM>();   // timed events starting this day
            public List<EventVM> Bands { get; set; } = new List<EventVM>();    // all-day WoW events (holidays) that cover this day
            public bool BandHead { get; set; }                                 // the band drawn here starts today (the day before draws none or another)
            public bool BandTail { get; set; }                                 // ... ends today
            public int Count => Events.Count;
            public int Dots => Math.Min(3, Events.Count);
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
            var bands = events.Where(e => e.IsBand).ToList();
            var byDay = events.Where(e => !e.IsBand).GroupBy(e => ToLocal(e.StartUtc).Date).ToDictionary(g => g.Key, g => g.OrderBy(e => e.StartUtc).ToList());
            var cells = new List<DayCell>();
            for (int i = 0; i < 42; i++)
            {
                var d = start.AddDays(i);
                cells.Add(new DayCell { Day = d, InMonth = d.Month == month, IsToday = d == today, Events = byDay.TryGetValue(d, out var l) ? l : new List<EventVM>(),
                    Bands = bands.Where(b => BandCovers(b, d)).OrderBy(b => b.StartUtc).ToList() });
            }
            // the cell draws its first band (the earliest holiday); it starts / ends where the neighbouring day draws another one
            for (int i = 0; i < cells.Count; i++)
            {
                var mine = cells[i].Bands.FirstOrDefault();
                cells[i].BandHead = i == 0 || cells[i - 1].Bands.FirstOrDefault() != mine;
                cells[i].BandTail = i == cells.Count - 1 || cells[i + 1].Bands.FirstOrDefault() != mine;
            }
            return cells;
        }

        // an all-day band (a holiday) covers the local days from its start to its end (the end is exclusive)
        public static bool BandCovers(EventVM e, DateTime localDay)
        {
            var first = ToLocal(e.StartUtc).Date;
            var last = e.EndUtc > e.StartUtc ? ToLocal(e.EndUtc.AddMinutes(-1)).Date : first;
            return localDay.Date >= first && localDay.Date <= last;
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

        public Dictionary<string, object> ToRequest(string type, string id = null)
        {
            var start = StartUtc.Value;
            var zone = CalendarRules.Zone();
            var d = new Dictionary<string, object>
            {
                ["t"] = type, ["title"] = Title.Trim(), ["note"] = (Note ?? "").Trim(), ["start"] = (double)CalendarRules.Ms(start),
                ["roles"] = Roles, ["tz"] = zone.Id, ["off"] = (double)(int)zone.GetUtcOffset(StartLocal.Value).TotalMinutes,
            };
            if (Roles) d["slots"] = new Dictionary<string, object> { ["tank"] = (double)Tank, ["healer"] = (double)Healer, ["dps"] = (double)Dps };
            if (DurationMin > 0) d["durationMin"] = (double)DurationMin;
            if (id != null) { d["id"] = id; d["scope"] = Scope; }
            else
            {
                if (RepeatWeeks == -1) d["repeat"] = new Dictionary<string, object> { ["open"] = true };
                else if (RepeatWeeks >= 2) d["repeat"] = new Dictionary<string, object> { ["weeks"] = (double)RepeatWeeks };
                if (ChannelId == null) d["post"] = false; else d["channel"] = ChannelId;
            }
            return d;
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
        public bool IsBand => IsWow && EndUtc > StartUtc && (AllDay || CalendarRules.ToLocal(EndUtc.AddMinutes(-1)).Date > CalendarRules.ToLocal(StartUtc).Date);
        bool Running => IsWow && StartUtc <= LfgVM.Clock() && !IsOver(LfgVM.Clock());
        string UntilShort => IsWow && EndUtc > StartUtc ? CalendarRules.UntilText(EndUtc, LfgVM.Clock()) : "";

        public static EventVM FromWow(WowEvent w)
        {
            var s = WowCalendar.StartUtc(w);
            if (s == null) return null;
            var e = WowCalendar.EndUtc(w);
            var vm = new EventVM { IsWow = true, WowKind = w.Kind, AllDay = w.AllDay, IconId = w.Icon, wowStatus = w.Status ?? "" };
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

        public void ApplyStub(Dictionary<string, object> snap)
        {
            if (snap == null) return;
            if (Id == null) { Id = snap.Str("id"); Stub = true; }
            if (!Stub) return;                                   // real data wins over a snapshot
            Title = SafeFile.Text(snap.Str("title") ?? "");
            By = snap.Str("by"); Status = snap.Str("status") ?? "open"; Weekly = snap.Bool("weekly");
            StartUtc = CalendarRules.FromMs(snap.Long("start"));
            EndUtc = snap.Long("end") > 0 ? CalendarRules.FromMs(snap.Long("end")) : DateTime.MinValue;
            RaiseAll();
        }

        public void Apply(Dictionary<string, object> d)
        {
            if (d == null) return;
            Id = d.Str("id") ?? Id; Stub = false;
            Title = SafeFile.Text(d.Str("title") ?? ""); Note = SafeFile.Text(d.Str("note") ?? "");
            By = d.Str("by"); Status = d.Str("status") ?? "open"; Series = d.Str("series"); Weekly = d.Child("rep") != null;
            Roles = !d.ContainsKey("roles") || d.Bool("roles");
            Channel = d.Str("channel");
            StartUtc = CalendarRules.FromMs(d.Long("start"));
            EndUtc = d.Long("end") > 0 ? CalendarRules.FromMs(d.Long("end")) : DateTime.MinValue;
            var slots = d.Child("slots");
            Tank = slots?.Int("tank") ?? 0; Healer = slots?.Int("healer") ?? 0; Dps = slots?.Int("dps") ?? 0;
            var me = LfgVM.MyName();
            var list = new List<SignupVM>();
            foreach (var x in d.List("signups").OfType<Dictionary<string, object>>())
            {
                var n = x.Str("n");
                if (string.IsNullOrEmpty(n)) continue;
                var st = x.Str("s"); if (st != "going" && st != "maybe" && st != "declined") continue;
                var isMe = me != null && string.Equals(n, me, StringComparison.OrdinalIgnoreCase);
                list.Add(new SignupVM(n, st, x.Str("r") ?? "", SafeFile.Text(x.Str("c") ?? ""), LfgVM.MemberOf(n) ?? LfgSlotVM.StandIn(n), isMe));
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
        public string TimeText => IsWow && AllDay ? "All day" : CalendarRules.TimeRange(StartUtc, EndUtc);
        public string StartHm => IsWow && AllDay ? "All day" : CalendarRules.ToLocal(StartUtc).ToString("HH:mm", CultureInfo.CurrentCulture);
        public string EndHm => IsWow && AllDay ? "" : EndUtc > StartUtc ? CalendarRules.ToLocal(EndUtc).ToString("HH:mm", CultureInfo.CurrentCulture) : "";
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
