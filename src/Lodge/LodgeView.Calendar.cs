using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;

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

        // ---- the strip of upcoming events above the chat
        // Fixed height while it is shown (StripHeight). It only appears / disappears when the number of events crosses zero; that is a Standard-motion
        // height animation (instant with Windows animations off), and while the viewport changes the message list's offset is moved by the same amount
        // (MessageScroll_Changed), so what the reader is looking at does not jump.
        const double StripHeight = 64, StripPillWidth = 208;
        bool stripShown;
        int stripToken;
        string stripSig = "";
        List<EventVM> stripItems = new List<EventVM>();
        bool stripAdjusting;

        void UpdateEventsStrip()
        {
            var s = Session;
            if (s == null) return;
            var all = s.SupportsCalendar && s.Selected != null ? s.StripEvents(LfgVM.Clock()) : new List<EventVM>();
            double room = (ChatCard.ActualWidth > 0 ? ChatCard.ActualWidth : 560) - 32 - 80;
            int fit = Math.Max(1, Math.Min(3, (int)Math.Floor(room / StripPillWidth)));
            var list = all.Take(fit).ToList();
            var sig = string.Join("|", list.Select(e => e.Id));
            if (sig != stripSig) { stripSig = sig; stripItems = list; StripItems.ItemsSource = list; }
            SetStrip(list.Count > 0);
        }

        void SetStrip(bool want)
        {
            if (want == stripShown) return;
            stripShown = want;
            var my = ++stripToken;
            double target = want ? StripHeight : 0;
            EventsStrip.BeginAnimation(HeightProperty, null);
            if (want) EventsStrip.Visibility = Visibility.Visible;
            stripAdjusting = true;
            void Done()
            {
                if (my != stripToken) return;
                EventsStrip.BeginAnimation(HeightProperty, null);
                EventsStrip.Height = target;
                if (!want) EventsStrip.Visibility = Visibility.Collapsed;
                // one more layout pass may still report the last viewport change: hold the adjustment until it has run
                Dispatcher.BeginInvoke(new Action(() => { if (my == stripToken) stripAdjusting = false; }), DispatcherPriority.ContextIdle);
            }
            if (!Motion.Enabled || !IsLoaded || !IsVisible || ChatCard.Visibility != Visibility.Visible) { EventsStrip.Height = target; Done(); return; }
            var a = new DoubleAnimation(EventsStrip.Height, target, Motion.Standard) { EasingFunction = want ? Motion.EaseOut : Motion.EaseIn };
            a.Completed += (x, y) => Done();
            EventsStrip.BeginAnimation(HeightProperty, a);
        }

        // a message list that is not held in place: the strip changes the viewport's height, so the offset follows (called from MessageScroll_Changed)
        void HoldMessagesInPlace(System.Windows.Controls.ScrollChangedEventArgs e)
        {
            if (!stripAdjusting || e.ViewportHeightChange == 0) return;
            var before = MessageScroll.VerticalOffset - e.VerticalChange;
            var want = Math.Max(0, Math.Min(MessageScroll.ScrollableHeight, before - e.ViewportHeightChange));
            if (Math.Abs(want - MessageScroll.VerticalOffset) > 0.5) MessageScroll.ScrollToVerticalOffset(want);
        }

        void StripItem_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is EventVM ev) OpenCalendarEvent(ev);
        }

        void StripAll_Click(object sender, RoutedEventArgs e) => ShowCalendar();

        // the Calendar row, its "events today" badge and the next-event line
        void UpdateCalendarChrome()
        {
            var s = Session;
            if (s == null) return;
            UpdateEventsStrip();
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
        public bool StripShownForTest => stripShown;
        public double StripHeightForTest => EventsStrip.ActualHeight;
        public bool StripVisibleForTest => EventsStrip.Visibility == Visibility.Visible;
        public IList<EventVM> StripItemsForTest => stripItems;
        public System.Windows.Controls.ItemsControl StripItemsControlForTest => StripItems;
        public void ClickStripItemForTest(int i) => StripItem_Click(new System.Windows.Controls.Button { Tag = stripItems[i] }, null);
        public void ClickStripAllForTest() => StripAll_Click(null, null);
        public bool StripAdjustingForTest => stripAdjusting;
        public double MessageOffsetForTest => MessageScroll.VerticalOffset;
        public void ScrollMessagesToForTest(double y) => MessageScroll.ScrollToVerticalOffset(y);
        public double MessageScrollableForTest => MessageScroll.ScrollableHeight;
        public double FirstMessageYForTest(int index)
        {
            MessageScroll.UpdateLayout();
            var c = Messages.ItemContainerGenerator.ContainerFromIndex(index) as FrameworkElement;
            if (c == null) return double.NaN;
            try { return c.TransformToAncestor(ChatCard).Transform(new Point(0, 0)).Y; } catch { return double.NaN; }   // on screen: from the chat card, which does not move
        }
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
