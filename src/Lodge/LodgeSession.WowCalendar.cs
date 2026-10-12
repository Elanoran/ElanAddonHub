using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using ElansAddonHub.Services;

namespace ElansAddonHub.Lodge
{
    // WoW's own calendar in the Lodge calendar: holidays, raid resets, the player's own and guild events, read from the companion addon's saved
    // variables (Services/WowCalendar.cs). Local to this PC: nothing here is ever sent to the server. They live next to the server's events, not in
    // them, so reminders, the sidebar line and the "events today" badge keep meaning "Lodge events".
    public partial class LodgeSession
    {
        public ObservableCollection<EventVM> WowEvents { get; } = new ObservableCollection<EventVM>();   // sorted by start
        public WowCalendarData WowData { get; private set; }
        int wowPollTick;

        // "Show in-game WoW events" (Settings > Lodge, also on the Calendar page)
        public bool WowEventsEnabled => Settings != null && !Settings.WowEventsOff;
        // the addon has saved a calendar (so the switch and the caption are worth showing)
        public bool WowAvailable => WowData != null && WowData.Found;
        // "Show upcoming events above the chat"
        public bool EventsStripEnabled => Settings != null && !Settings.EventsStripOff;

        // what the agenda and the month grid list: the server's events plus WoW's (when shown)
        public IEnumerable<EventVM> ViewEvents => WowEventsEnabled && WowEvents.Count > 0 ? Events.Concat(WowEvents) : (IEnumerable<EventVM>)Events;

        // what the strip above the chat lists
        public List<EventVM> StripEvents(DateTime nowUtc) => SupportsCalendar && EventsStripEnabled ? CalendarRules.Upcoming(ViewEvents, nowUtc) : new List<EventVM>();

        // read the saved variables again (only when the file changed, unless forced) and rebuild the WoW events; true when the list changed
        public bool ReloadWowEvents(bool force = false)
        {
            WowCalendarData data;
            try
            {
                if (force) WowCalendar.ResetCache();
                data = WowCalendar.ReadCached(Settings?.WowRoot, out var changed);
                if (!changed && !force && WowData != null) return false;
            }
            catch (Exception e) { Util.Log("wow calendar: " + e.Message); return false; }
            WowData = data;
            if (!string.IsNullOrEmpty(data.SourceFile))
            {
                // the icons come from the same game install the saved variables belong to
                var flavor = InventoryReader.FlavorOf(data.SourceFile);
                if (flavor != null) IconStore.Flavor = flavor;
                if (string.IsNullOrEmpty(IconStore.WowRoot())) { var root = Settings?.WowRoot; IconStore.WowRoot = () => root; }
            }
            var sig = string.Join("|", WowEvents.Select(e => e.Id));
            var list = data.Events.Select(EventVM.FromWow).Where(e => e != null).OrderBy(e => e.StartUtc).ThenBy(e => e.Title).ToList();
            if (sig == string.Join("|", list.Select(e => e.Id)) && !force) return false;
            WowEvents.Clear();
            foreach (var e in list) WowEvents.Add(e);
            CalendarChanged?.Invoke();
            return true;
        }

        // the switches in Settings changed
        public void WowCalendarSettingsChanged() => CalendarChanged?.Invoke();

        // called with the calendar tick: look for a changed file about every minute (the game writes it on /reload and logout)
        void WowCalendarTick()
        {
            if (!SupportsCalendar) return;
            bool moved = false;
            foreach (var e in WowEvents) moved |= e.RefreshClock();       // countdowns and "until Sun" move on
            if (wowPollTick++ % 3 == 0) moved |= ReloadWowEvents();
            if (moved) CalendarChanged?.Invoke();
        }

        public EventVM WowEventById(string id) => id == null ? null : WowEvents.FirstOrDefault(e => e.Id == id);
    }
}
