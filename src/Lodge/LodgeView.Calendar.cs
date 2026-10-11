using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace ElansAddonHub.Lodge
{
    // The Lodge tab, part 3: the Calendar row above the channels, the calendar page that takes the chat's place, and the "next event" line at the
    // bottom of the sidebar. Everything here is hidden on a server older than 2.11 (LodgeSession.SupportsCalendar).
    public partial class LodgeView
    {
        bool calendarShown;

        void CalendarRow_Click(object sender, MouseButtonEventArgs e) => ShowCalendar();

        public void ShowCalendar()
        {
            if (Session == null || !Session.SupportsCalendar) return;
            calendarShown = true;
            ChannelVM.CalendarShown = true;
            foreach (var c in Session.TextChannels) c.RaiseRow();
            ChatCard.Visibility = Visibility.Collapsed;
            CalendarPage.Visibility = Visibility.Visible;
            CalendarPage.Show();
            if (IsLoaded && IsVisible) Motion.PageIn(CalendarPage);
            UpdateCalendarChrome();
        }

        public void HideCalendar()
        {
            if (!calendarShown) return;
            calendarShown = false;
            ChannelVM.CalendarShown = false;
            if (Session != null) foreach (var c in Session.TextChannels) c.RaiseRow();
            CalendarPage.Visibility = Visibility.Collapsed;
            ChatCard.Visibility = Visibility.Visible;
            UpdateCalendarChrome();
        }

        // a chat card's "Open in Calendar", the sidebar's next-event line
        void EventOpen_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is EventVM ev) OpenCalendarEvent(ev);
        }

        void NextEvent_Click(object sender, MouseButtonEventArgs e) => OpenCalendarEvent(Session?.NextEvent);

        public void OpenCalendarEvent(EventVM ev)
        {
            if (ev == null) return;
            ShowCalendar();
            CalendarPage.OpenEvent(ev);
        }

        // the Calendar row, its "events today" badge and the next-event line
        void UpdateCalendarChrome()
        {
            var s = Session;
            if (s == null) return;
            bool on = s.SupportsCalendar && s.Selected != null;
            CalendarRow.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (!on && calendarShown) HideCalendar();
            if (calendarShown) CalendarRow.SetResourceReference(BackgroundProperty, "Brush.Accent.Tint"); else CalendarRow.ClearValue(BackgroundProperty);
            CalendarRowText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, calendarShown ? "Brush.Text.Primary" : "Brush.Text.Secondary");
            if (on)
            {
                var now = LfgVM.Clock();
                var today = CalendarRules.ToLocal(now).Date;
                int n = s.Events.Count(e => !e.IsCancelled && !e.IsOver(now) && CalendarRules.ToLocal(e.StartUtc).Date == today);
                CalendarBadge.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
                CalendarBadgeText.Text = n.ToString();
            }
            UpdateNextEvent();
        }

        void UpdateNextEvent()
        {
            var s = Session;
            if (s == null) return;
            var line = s.SupportsCalendar && s.Selected != null ? s.NextEventLine : "";
            NextEventText.Text = line;
            NextEventRow.Visibility = line.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---- test hooks
        public CalendarView CalendarForTest => CalendarPage;
        public bool CalendarShownForTest => calendarShown && CalendarPage.Visibility == Visibility.Visible && ChatCard.Visibility != Visibility.Visible;
        public void ShowCalendarForTest() => ShowCalendar();
        public void HideCalendarForTest() => HideCalendar();
        public bool CalendarRowVisibleForTest => CalendarRow.Visibility == Visibility.Visible;
        public string CalendarBadgeForTest => CalendarBadge.Visibility == Visibility.Visible ? CalendarBadgeText.Text : "";
        public bool NextEventVisibleForTest => NextEventRow.Visibility == Visibility.Visible;
        public string NextEventTextForTest => NextEventText.Text;
        public FrameworkElement NextEventRowForTest => NextEventRow;
        public void RefreshCalendarChromeForTest() => UpdateCalendarChrome();
        public void ClickNextEventForTest() => NextEvent_Click(null, null);
    }
}
