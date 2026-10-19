using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using ElansAddonHub.Services;
using Elans.Shared;

namespace ElansAddonHub.Lodge
{
    // The raid / event calendar (Lodge server 2.11). The pure rules are in CalendarModels.cs; this holds the events the server sent,
    // asks for months on demand, sends the changes and fires the "starts in 15 minutes" reminders.
    public partial class LodgeSession
    {
        public bool SupportsCalendar { get; private set; }               // server 2.11: hidden on older servers
        public ObservableCollection<EventVM> Events { get; } = new ObservableCollection<EventVM>();   // sorted by start, no stubs
        public event Action CalendarChanged;                              // events added / changed / the answer to a month fetch
        readonly Dictionary<string, EventVM> eventIndex = new Dictionary<string, EventVM>();
        readonly HashSet<string> loadedMonths = new HashSet<string>();
        readonly HashSet<string> eventReminded = new HashSet<string>();
        public static readonly Version CalendarServer = new Version(2, 11, 0);

        void ReadCalendarFeatures(Dictionary<string, object> m, List<string> features)
        {
            var v = ParseVersion(ServerVersion);
            SupportsCalendar = features.Contains("calendar") && (v == null || v >= CalendarServer);
            // the welcome is a full refresh: the window it names replaces what we had (chat cards keep their instances)
            Events.Clear(); loadedMonths.Clear();
            if (!SupportsCalendar) Changed?.Invoke();
        }

        // after Me is known (an event stores who signed up; "my" answer is found by name)
        void LoadWelcomeCalendar(Dictionary<string, object> m)
        {
            if (!SupportsCalendar) return;
            var cal = m.Child("calendar");
            if (cal != null) ApplyEvents(CalWelcome.Read(new LodgeFieldsDict(cal)).Events);
            ReloadWowEvents(true);                                    // WoW's own calendar from the companion addon (local, read-only)
            Changed?.Invoke();
        }

        void ApplyEvents(List<CalEvent> list)
        {
            if (list == null) return;
            foreach (var e in list) Upsert(e);
            CalendarChanged?.Invoke();
        }

        EventVM Upsert(CalEvent d)
        {
            var id = d.Id;
            if (string.IsNullOrEmpty(id)) return null;
            if (!eventIndex.TryGetValue(id, out var vm)) eventIndex[id] = vm = new EventVM();
            var listed = Events.Contains(vm);
            vm.Apply(d);
            if (listed) Events.Remove(vm);                            // its start may have moved: put it back in order
            int at = Events.Count;
            while (at > 0 && Events[at - 1].StartUtc > vm.StartUtc) at--;
            Events.Insert(at, vm);
            return vm;
        }

        void OnCalendarFrame(Dictionary<string, object> m)
        {
            if (!SupportsCalendar) return;
            var f = new LodgeFieldsDict(m);
            ApplyEvents(m.Str("t") == LodgeMsg.CalRange ? CalRangeMsg.Read(f).Events : CalEventsMsg.Read(f).Events);
        }

        public EventVM EventById(string id) => id != null && eventIndex.TryGetValue(id, out var e) ? e : null;

        // the live event behind a chat card (a stand-in built from the card's snapshot when the event is outside the loaded window)
        public EventVM EventForCard(Dictionary<string, object> snap)
        {
            var id = snap?.Str("id");
            if (string.IsNullOrEmpty(id)) return null;
            if (!eventIndex.TryGetValue(id, out var vm)) eventIndex[id] = vm = new EventVM();
            vm.ApplyStub(CalCard.Read(new LodgeFieldsDict(snap)));
            return vm;
        }

        // the month view shows a grid of weeks: ask the server for the month (once each) - the welcome only carries what is still to come
        public void EnsureMonth(int year, int month)
        {
            if (!SupportsCalendar) return;
            var key = year + "-" + month;
            if (!loadedMonths.Add(key)) return;
            CalendarRules.MonthWindow(year, month, out var from, out var to);
            _ = Send(LodgeFieldsDict.Build(new CalRangeGetMsg { From = CalendarRules.Ms(from), To = CalendarRules.Ms(to) }.Write));
        }

        // events the grid of one month may show
        public IEnumerable<EventVM> EventsBetween(DateTime fromUtc, DateTime toUtc) => Events.Where(e => e.StartUtc >= fromUtc && e.StartUtc < toUtc);

        public Task CalendarCreate(CalendarForm f) => Send(f.ToRequest(LodgeMsg.CalCreate));
        public Task CalendarEdit(EventVM e, CalendarForm f) => Send(f.ToRequest(LodgeMsg.CalEdit, e.Id));
        public Task CalendarCancel(EventVM e, bool following) => Send(LodgeFieldsDict.Build(new CalCancelMsg { Id = e.Id, Scope = following ? "following" : "this" }.Write));
        public Task CalendarLeave(EventVM e) => Send(LodgeFieldsDict.Build(new CalLeaveMsg { Id = e.Id }.Write));

        // Going / Maybe / Can't, with the role picked on the card; the comment I wrote before stays unless a new one is given
        public Task CalendarAnswer(EventVM e, string status, string comment = null)
        {
            var msg = new CalSignupMsg { Id = e.Id, Status = status, Comment = comment ?? e.MyComment ?? "" };
            if (e.Roles && status != "declined") { msg.Role = e.SelectedRole; EventVM.LastRole = e.SelectedRole; }
            return Send(LodgeFieldsDict.Build(msg.Write));
        }

        public EventVM NextEvent => SupportsCalendar ? CalendarRules.NextEvent(Events, Clock()) : null;
        public string NextEventLine => SupportsCalendar ? CalendarRules.NextEventLine(NextEvent, Clock()) : "";

        // every few seconds: countdowns move on, and a toast 15 minutes before the start for Going and Maybe (the toast settings and the
        // combat hold apply: ToastCenter.Push decides)
        public void CalendarTick()
        {
            if (!SupportsCalendar) return;
            WowCalendarTick();
            var now = Clock();
            bool moved = false;
            foreach (var e in Events.ToList())
            {
                moved |= e.RefreshClock();
                if (!CalendarRules.ReminderDue(e, now, eventReminded)) continue;
                eventReminded.Add(e.Id);
                var left = e.StartUtc - now;
                Toasts.Push(new ToastItem
                {
                    Kind = ToastKind.Event, Kicker = "Event", Sender = e.Title + " starts in " + Math.Max(1, (int)Math.Ceiling(left.TotalMinutes)) + " min", Who = Me,
                    Text = e.MyStatus == "going" ? (e.Roles && !string.IsNullOrEmpty(e.MyRole) ? "You are going as " + EventVM.RoleLabel(e.MyRole) : "You are going") + " · " + CalendarRules.ShortWhen(e.StartUtc, now)
                        : "You said maybe · " + CalendarRules.ShortWhen(e.StartUtc, now),
                });
            }
            if (moved) CalendarChanged?.Invoke();
        }

        void ResetCalendar()
        {
            Events.Clear(); eventIndex.Clear(); loadedMonths.Clear(); eventReminded.Clear(); SupportsCalendar = false;
        }

        public bool CalendarRemindedForTest(string id) => eventReminded.Contains(id);
    }
}
