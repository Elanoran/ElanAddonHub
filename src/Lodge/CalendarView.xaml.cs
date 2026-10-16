using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using ElansAddonHub.Services;

namespace ElansAddonHub.Lodge
{
    // One day of the month grid, as the template binds it
    public class MonthCellVM : Bindable
    {
        public CalendarRules.DayCell Cell { get; }
        bool selected;
        public MonthCellVM(CalendarRules.DayCell c) { Cell = c; }
        public string Number => Cell.Number;
        public bool IsWeekend => Cell.Day.DayOfWeek == DayOfWeek.Saturday || Cell.Day.DayOfWeek == DayOfWeek.Sunday;
        public bool Selected { get => selected; set { if (Set(ref selected, value)) { Raise(nameof(Fill)); } } }
        // selected = the accent tint; a day of this month = a faint tile (fainter on the weekend); other months = nothing (and dimmed)
        public Brush Fill => selected ? Avatar.Res("Brush.Accent.Tint") : !Cell.InMonth ? System.Windows.Media.Brushes.Transparent : Avatar.Res(IsWeekend ? "Brush.Cal.Weekend" : "Brush.Cal.Day");
        // today: the number sits in an accent circle (on-accent text), whatever is selected
        public Visibility TodayVisibility => Cell.IsToday ? Visibility.Visible : Visibility.Collapsed;
        public Brush NumberBrush => Avatar.Res(Cell.IsToday ? "Brush.OnAccent" : Cell.InMonth ? "Brush.Text.Primary" : "Brush.Text.Tertiary");
        public FontWeight NumberWeight => Cell.IsToday ? FontWeights.SemiBold : FontWeights.Normal;
        public double CellOpacity => Cell.InMonth ? 1 : 0.6;
    }

    // a weekday name over the grid (the weekend ones quieter)
    public class WeekdayVM
    {
        public string Name { get; set; }
        public Brush Brush { get; set; }
        public bool Weekend { get; set; }
    }

    // agenda / day cards: the Lodge event card, or the quiet read-only card of a WoW event
    public class EventCardSelector : DataTemplateSelector
    {
        public DataTemplate Server { get; set; }
        public DataTemplate Wow { get; set; }
        public override DataTemplate SelectTemplate(object item, DependencyObject container) => (item as EventVM)?.IsWow == true ? Wow : Server;
    }

    // The calendar page of the Lodge: Month (default) and Agenda (the last one picked is kept while the app runs), the event detail card and the create / edit form. All state is in LodgeSession.
    public partial class CalendarView : UserControl
    {
        LodgeSession S;
        bool agenda;                       // Month is the default; the choice is kept for as long as the app runs (this view lives with the Lodge page)
        bool monthPicked;                  // a month was chosen (navigation, an event): Show() keeps it; else it opens on today's month
        int year, month;
        DateTime? selectedDay;
        string lastSignature = "";
        string pendingOpenId;
        EventVM detail, editing;
        bool formClosing, detailClosing; int formToken, detailToken;
        readonly List<DateTime> formMonths = new List<DateTime>();
        int fTank = 2, fHealer = 3, fDps = 5;
        static readonly int[] LengthMinutes = { 0, 60, 90, 120, 180, 240, 360, 480 };
        static readonly string[] LengthNames = { "No end", "1 hour", "1 hour 30 min", "2 hours", "3 hours", "4 hours", "6 hours", "8 hours" };
        List<string> channelIds = new List<string>();

        public CalendarView()
        {
            InitializeComponent();
            PreviewKeyDown += (s, e) =>
            {
                if (e.Key != Key.Escape) return;
                if (FormOverlay.Visibility == Visibility.Visible) { CloseForm(); e.Handled = true; }
                else if (DetailOverlay.Visibility == Visibility.Visible) { CloseDetail(); e.Handled = true; }
            };
        }

        public void Init(LodgeSession session)
        {
            S = session;
            S.CalendarChanged += OnChanged;
            S.Changed += () => { NewButton.Visibility = EmptyNew.Visibility = S.MyRole == "guest" ? Visibility.Collapsed : Visibility.Visible; };
            var t = DateTime.Now; year = t.Year; month = t.Month;
        }

        // ---- showing
        public void Show()
        {
            NewButton.Visibility = EmptyNew.Visibility = S.MyRole == "guest" ? Visibility.Collapsed : Visibility.Visible;
            if (!monthPicked) { var t = CalendarRules.ToLocal(Now); year = t.Year; month = t.Month; selectedDay = null; }
            MonthScroll.ScrollToTop();             // the calendar opens on the grid, not where a day's list was scrolled to
            Rebuild(true);
        }

        void OnChanged()
        {
            if (Visibility != Visibility.Visible) { lastSignature = ""; return; }
            Rebuild(false);
            if (pendingOpenId != null && S.EventById(pendingOpenId) is EventVM ev && !ev.Stub) { pendingOpenId = null; OpenDetail(ev); }
        }

        DateTime Now => LfgVM.Clock();

        void Rebuild(bool force)
        {
            if (S == null) return;
            ShowWowBar();
            if (agenda)
            {
                var groups = CalendarRules.Agenda(S.CalendarEvents, Now);
                var sig = string.Join("|", groups.SelectMany(g => g.Events.Select(e => g.Label + ":" + e.Id)));
                if (force || sig != lastSignature) { AgendaList.ItemsSource = groups; lastSignature = sig; }
                AgendaEmpty.Visibility = groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            else BuildMonth();
        }

        // the quiet bar under the header: the switch for WoW's own events and the note that they update after a /reload or logout
        // (shown once the addon has saved a calendar, or while the switch is off so it can be turned back on)
        bool settingWowSwitch;
        void ShowWowBar()
        {
            bool show = S.WowAvailable || !S.WowEventsEnabled;
            WowBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            settingWowSwitch = true; WowSwitch.IsChecked = S.WowEventsEnabled; settingWowSwitch = false;
            WowCaption.Text = S.WowData != null && S.WowData.Events.Any(x => x.Recurring)
                ? "Some holidays repeat every week. Update Elan's Hub to 1.9.1 and /reload to see each date."
                : "WoW events come from your game and update after a /reload or logout.";
            ShowFilters();
        }

        void WowSwitch_Click(object sender, RoutedEventArgs e)
        {
            if (S == null || settingWowSwitch) return;
            S.Settings.WowEventsOff = WowSwitch.IsChecked != true;
            SettingsStore.Save(S.Settings);
            S.WowCalendarSettingsChanged();
        }

        // the filter chips (Lodge / Holidays / Raid resets / My game events): a toggle each, saved in the settings
        void ShowFilters()
        {
            FilterBar.Visibility = WowBar.Visibility;
            settingFilters = true;
            FilterLodge.IsChecked = S.CalendarKindShown("lodge");
            FilterHoliday.IsChecked = !S.Settings.CalHideHolidays; FilterReset.IsChecked = !S.Settings.CalHideResets; FilterMine.IsChecked = !S.Settings.CalHideMine;
            FilterHoliday.IsEnabled = FilterReset.IsEnabled = FilterMine.IsEnabled = S.WowEventsEnabled;      // the in-game kinds are all off with the switch
            settingFilters = false;
        }
        bool settingFilters;

        void Filter_Click(object sender, RoutedEventArgs e)
        {
            if (S == null || settingFilters) return;
            var on = (sender as ToggleButton)?.IsChecked == true;
            if (sender == FilterLodge) S.Settings.CalHideLodge = !on;
            else if (sender == FilterHoliday) S.Settings.CalHideHolidays = !on;
            else if (sender == FilterReset) S.Settings.CalHideResets = !on;
            else if (sender == FilterMine) S.Settings.CalHideMine = !on;
            SettingsStore.Save(S.Settings);
            S.WowCalendarSettingsChanged();
        }

        void Mode_Changed(object sender, RoutedEventArgs e)
        {
            if (AgendaScroll == null) return;
            agenda = ModeAgenda.IsChecked == true;
            AgendaScroll.Visibility = agenda ? Visibility.Visible : Visibility.Collapsed;
            MonthScroll.Visibility = agenda ? Visibility.Collapsed : Visibility.Visible;
            Rebuild(true);
            Motion.PageIn(agenda ? (FrameworkElement)AgendaScroll : MonthScroll);
        }

        // ---- month
        CalendarRules.MonthMetrics metrics = CalendarRules.Metrics(632, 0);
        List<MonthCellVM> monthCells;
        int builtMonth = -1;

        void BuildMonth()
        {
            S.EnsureMonth(year, month);
            ApplyMonthMetrics(false, false);
            var first = new DateTime(year, month, 1);
            if (builtMonth != year * 12 + month) { builtMonth = year * 12 + month; MonthScroll.ScrollToTop(); }   // another month starts at its grid
            MonthTitle.Text = first.ToString("MMMM yyyy", CalendarRules.English);
            var fdw = (int)CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
            WeekdayRow.ItemsSource = Enumerable.Range(0, 7).Select(i => (DayOfWeek)((fdw + i) % 7)).Select(d => new WeekdayVM
            {
                Name = CalendarRules.English.DateTimeFormat.GetAbbreviatedDayName(d), Weekend = d == DayOfWeek.Saturday || d == DayOfWeek.Sunday,
                Brush = Avatar.Res(d == DayOfWeek.Saturday || d == DayOfWeek.Sunday ? "Brush.Text.Tertiary" : "Brush.Text.Secondary"),
            }).ToList();
            var cells = CalendarRules.MonthGrid(year, month, S.CalendarEvents, Now).Select(c => new MonthCellVM(c)).ToList();
            var today = CalendarRules.ToLocal(Now).Date;
            if (selectedDay == null || selectedDay.Value.Year != year || selectedDay.Value.Month != month) selectedDay = today.Year == year && today.Month == month ? today : first;
            foreach (var c in cells) c.Selected = c.Cell.Day == selectedDay.Value;
            monthCells = cells;
            MonthGridList.ItemsSource = cells;
            UpdateTodayButton();
            monthLayout = CalendarRules.Layout(cells.Select(c => c.Cell).ToList(), metrics.Lanes);
            DrawOverlay(cells);
            ShowDay();
        }

        // "Today" does nothing while today is the selected day of the month shown
        void UpdateTodayButton()
        {
            var today = CalendarRules.ToLocal(Now).Date;
            TodayButton.IsEnabled = !(today.Year == year && today.Month == month && selectedDay == today);
        }

        // ---- size: the grid follows the window (CalendarRules.Metrics); what a month holds never changes it
        void MonthScroll_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyMonthMetrics(true, false);

        void ApplyMonthMetrics(bool redraw, bool force)
        {
            double w = MonthScroll.ActualWidth;
            if (w <= 0) return;
            var m = CalendarRules.Metrics(w - MonthHost.Margin.Left - MonthHost.Margin.Right, MonthScroll.ActualHeight);
            if (!force && m.Equals(metrics) && MonthGridList.Height == CalendarRules.GridRows * m.RowHeight) return;
            bool relayout = m.Lanes != metrics.Lanes || m.Compact != metrics.Compact || m.Wide != metrics.Wide;
            metrics = m;
            SideColumn.Width = new GridLength(m.Side ? CalendarRules.SidePanel + CalendarRules.SideGap : 0);
            Grid.SetRow(DayPanel, m.Side ? 0 : 1); Grid.SetColumn(DayPanel, m.Side ? 1 : 0);
            DayPanel.Margin = m.Side ? new Thickness(CalendarRules.SideGap, 0, 0, 0) : new Thickness(0, 24, 0, 0);
            MonthGridList.Height = CalendarRules.GridRows * m.RowHeight;
            foreach (var rd in MonthOverlay.RowDefinitions) rd.Height = new GridLength(m.RowHeight);
            if (redraw && (relayout || force) && monthCells != null && S != null)
            {
                monthLayout = CalendarRules.Layout(monthCells.Select(c => c.Cell).ToList(), m.Lanes);
                DrawOverlay(monthCells);
            }
        }

        // ---- the bars and chips over the month grid (positions come from CalendarRules.Layout, sizes from the metrics; nothing here changes the grid's height)
        CalendarRules.MonthLayout monthLayout;

        void DrawOverlay(IList<MonthCellVM> cells)
        {
            MonthOverlay.Children.Clear();
            if (monthLayout == null) return;
            var cap = (Style)FindResource("Text.Caption");
            var m = metrics;
            foreach (var s in monthLayout.Segs)
            {
                var ev = s.Ev;
                double dim = cells[s.Row * CalendarRules.Columns + s.Col].Cell.InMonth ? 1 : 0.6;
                double r = m.Compact ? m.Chip / 2 : 8;
                var cr = new CornerRadius(s.LeftOpen ? 0 : r, s.RightOpen ? 0 : r, s.RightOpen ? 0 : r, s.LeftOpen ? 0 : r);
                var g = new Grid { Height = m.Chip, VerticalAlignment = VerticalAlignment.Top, Opacity = dim, ClipToBounds = true, Cursor = Cursors.Hand, Background = Brushes.Transparent };
                g.Margin = new Thickness(s.LeftOpen ? 0 : 4, m.LaneTop(s.Lane), s.RightOpen ? 0 : 4, 0);
                // compact: the bar itself in the kind's colour (no room for words); otherwise the tint with a mark in the colour and the label
                g.Children.Add(new Border { Background = m.Compact ? ev.BarBrush : ev.TintBrush, CornerRadius = cr });
                var hv = new Border { Background = Avatar.Res("Brush.Overlay.Hover"), CornerRadius = cr, Opacity = 0, IsHitTestVisible = false };
                g.Children.Add(hv);
                if (!m.Compact && (!s.LeftOpen || s.IsBar))
                    g.Children.Add(new Rectangle { Width = 3, Height = 8, RadiusX = 1.5, RadiusY = 1.5, Fill = ev.BarBrush, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) });
                g.Children.Add(new TextBlock
                {
                    Text = CalendarRules.DrawnLabel(s, m), Style = cap, Foreground = Avatar.Res("Brush.Text.Primary"), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 4, 0), Visibility = m.Compact ? Visibility.Collapsed : Visibility.Visible,
                });
                if (ev.IsCancelled) g.Opacity = dim * 0.6;
                g.ToolTip = s.Label + " · " + ev.TypeLabel + (ev.IsCancelled ? " · cancelled" : "");
                g.MouseEnter += (o, a) => hv.Opacity = 1;
                g.MouseLeave += (o, a) => hv.Opacity = 0;
                g.MouseLeftButtonUp += (o, a) => { a.Handled = true; OpenDetail(ev); };
                Grid.SetRow(g, s.Row); Grid.SetColumn(g, s.Col); Grid.SetColumnSpan(g, s.Span);
                g.Tag = s;
                MonthOverlay.Children.Add(g);
            }
            for (int i = 0; i < monthLayout.More.Length && i < cells.Count; i++)
            {
                int n = monthLayout.More[i];
                if (n <= 0) continue;
                var day = cells[i].Cell.Day;
                var t = new TextBlock
                {
                    Text = "+" + n + " more", Style = cap, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(4, m.MoreTop, 4, 0),
                    Padding = new Thickness(4, 0, 4, 0), Opacity = cells[i].Cell.InMonth ? 1 : 0.6, Background = Brushes.Transparent, Cursor = Cursors.Hand,
                    TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, ToolTip = "Show everything on " + day.ToString("ddd d MMM", CalendarRules.English),
                };
                t.MouseEnter += (o, a) => t.Foreground = Avatar.Res("Brush.Text.Primary");
                t.MouseLeave += (o, a) => t.ClearValue(TextBlock.ForegroundProperty);
                t.MouseLeftButtonUp += (o, a) => { a.Handled = true; OpenDay(day); };
                Grid.SetRow(t, i / CalendarRules.Columns); Grid.SetColumn(t, i % CalendarRules.Columns);
                t.Tag = i;
                MonthOverlay.Children.Add(t);
            }
        }

        // "+N more": select that day and bring its list into view (beside the grid on a wide window it already is)
        void OpenDay(DateTime day)
        {
            SelectDay(day);
            if (!metrics.Side) { UpdateLayout(); DayPanel.BringIntoView(new Rect(0, 0, Math.Max(1, DayPanel.ActualWidth), Math.Max(1, Math.Min(DayPanel.ActualHeight, 240)))); }
        }

        void ShowDay()
        {
            var day = selectedDay ?? CalendarRules.ToLocal(Now).Date;
            DayTitle.Text = CalendarRules.DayLabel(day, CalendarRules.ToLocal(Now).Date) + (day.Date == CalendarRules.ToLocal(Now).Date.AddDays(0) || day.Date == CalendarRules.ToLocal(Now).Date.AddDays(1) ? " · " + day.ToString("d MMMM", CalendarRules.English) : "");
            var evs = S.CalendarEvents.Where(e => !e.Recurring).Where(e => e.IsBand ? CalendarRules.BandCovers(e, day) : CalendarRules.ToLocal(e.StartUtc).Date == day.Date).OrderBy(e => e.IsBand ? 0 : 1).ThenBy(e => e.StartUtc).ToList();
            DayEvents.ItemsSource = evs;
            DayEmpty.Visibility = evs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        void Day_Click(object sender, MouseButtonEventArgs e)
        {
            if (!((sender as FrameworkElement)?.Tag is MonthCellVM c)) return;
            SelectDay(c.Cell.Day);
        }

        void SelectDay(DateTime day)
        {
            selectedDay = day.Date;
            monthPicked = true;
            if (day.Year != year || day.Month != month)
            {
                int dir = new DateTime(day.Year, day.Month, 1) > new DateTime(year, month, 1) ? 1 : -1;
                year = day.Year; month = day.Month;
                BuildMonth(); SlideMonth(dir);
                return;
            }
            foreach (var c in MonthGridList.Items.OfType<MonthCellVM>()) c.Selected = c.Cell.Day == selectedDay.Value;
            UpdateTodayButton();
            ShowDay();
        }

        void GoMonth(int delta)
        {
            var d = new DateTime(year, month, 1).AddMonths(delta);
            year = d.Year; month = d.Month; monthPicked = true;
            BuildMonth();
            SlideMonth(delta);
        }
        void MonthPrev_Click(object sender, RoutedEventArgs e) => GoMonth(-1);
        void MonthNext_Click(object sender, RoutedEventArgs e) => GoMonth(1);
        void MonthToday_Click(object sender, RoutedEventArgs e)
        {
            var t = CalendarRules.ToLocal(Now).Date;
            int dir = new DateTime(t.Year, t.Month, 1).CompareTo(new DateTime(year, month, 1));
            year = t.Year; month = t.Month; selectedDay = t; monthPicked = false;
            BuildMonth();
            SlideMonth(dir);
        }

        // a month change slides the grid and the title in from the side it comes from (24 px + fade, Standard motion); nothing moves with Windows animations off
        int slides;
        void SlideMonth(int dir)
        {
            if (!Motion.Enabled || dir == 0) return;
            slides++;
            foreach (var el in new FrameworkElement[] { MonthGridHost, MonthTitle })
            {
                var tt = new TranslateTransform(24 * Math.Sign(dir), 0);
                el.RenderTransform = tt;
                var slide = new DoubleAnimation(24 * Math.Sign(dir), 0, Motion.Standard) { EasingFunction = Motion.EaseOut };
                var target = el;
                slide.Completed += (o, a) => { if (target.RenderTransform == tt) target.RenderTransform = Transform.Identity; };
                tt.BeginAnimation(TranslateTransform.XProperty, slide);
                el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Motion.Standard) { EasingFunction = Motion.EaseOut });
            }
        }

        // ---- opening one event (from a chat card, the sidebar line or a click)
        public void OpenEvent(EventVM ev)
        {
            if (ev == null) return;
            if (!ev.Stub) { OpenDetail(ev); return; }
            // only the chat card's snapshot is known: show its month, and open it when the server's answer arrives
            var l = CalendarRules.ToLocal(ev.StartUtc);
            ModeMonth.IsChecked = true;
            year = l.Year; month = l.Month; selectedDay = l.Date; monthPicked = true;
            pendingOpenId = ev.Id;
            BuildMonth();
        }

        void Card_Click(object sender, MouseButtonEventArgs e)
        {
            // buttons and the role list handle their own clicks; a click anywhere else on the card opens it
            for (var o = e.OriginalSource as DependencyObject; o != null && o != sender; o = (o is Visual || o is System.Windows.Media.Media3D.Visual3D) ? VisualTreeHelper.GetParent(o) : LogicalTreeHelper.GetParent(o))
                if (o is ButtonBase || o is ComboBox || o is ComboBoxItem || o is Popup) return;
            if ((sender as FrameworkElement)?.Tag is EventVM ev) OpenDetail(ev);
        }

        // ---- answers
        void Answer(object sender, string status)
        {
            if (!((sender as FrameworkElement)?.Tag is EventVM ev) || S == null) return;
            string comment = detail == ev && DetailOverlay.Visibility == Visibility.Visible ? DetailComment.Text.Trim() : null;
            if (ev.IsSameAnswer(status) && (comment == null || comment == ev.MyComment)) return;
            _ = S.CalendarAnswer(ev, status, comment);
        }
        void Going_Click(object sender, RoutedEventArgs e) => Answer(sender, "going");
        void Maybe_Click(object sender, RoutedEventArgs e) => Answer(sender, "maybe");
        void Declined_Click(object sender, RoutedEventArgs e) => Answer(sender, "declined");
        void Leave_Click(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.Tag is EventVM ev) _ = S.CalendarLeave(ev); }
        void DetailComment_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || detail == null || detail.MyStatus == null) return;
            _ = S.CalendarAnswer(detail, detail.MyStatus, DetailComment.Text.Trim());
            e.Handled = true;
        }

        // ================================================================ detail
        public void OpenDetail(EventVM ev)
        {
            if (detail != null) detail.PropertyChanged -= OnDetailChanged;
            detail = ev;
            ev.PropertyChanged += OnDetailChanged;
            DetailBox.DataContext = ev;
            DetailComment.Text = ev.MyComment ?? "";
            RefreshDetailButtons();
            detailClosing = false; detailToken++;
            DetailOverlay.BeginAnimation(OpacityProperty, null);
            DetailOverlay.Visibility = Visibility.Visible;
            if (Motion.Enabled) DetailOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Motion.Standard) { EasingFunction = Motion.EaseOut });
            Motion.DialogIn(DetailBox);
            DetailDone.Focus();
        }

        void OnDetailChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(EventVM.CanManage) || e.PropertyName == nameof(EventVM.Groups) || e.PropertyName == nameof(EventVM.Status)) RefreshDetailButtons();
        }

        void RefreshDetailButtons()
        {
            if (detail == null) return;
            DetailEdit.Visibility = DetailCancelEvent.Visibility = detail.CanManage ? Visibility.Visible : Visibility.Collapsed;
            DetailNobody.Visibility = detail.Signups.Count == 0 && !detail.IsWow ? Visibility.Visible : Visibility.Collapsed;
        }

        void CloseDetail()
        {
            if (DetailOverlay.Visibility != Visibility.Visible || detailClosing) return;
            if (detail != null) detail.PropertyChanged -= OnDetailChanged;
            if (!Motion.Enabled) { DetailOverlay.Visibility = Visibility.Collapsed; return; }
            detailClosing = true;
            var my = ++detailToken;
            DetailOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, Motion.Fast) { EasingFunction = Motion.EaseIn, FillBehavior = FillBehavior.HoldEnd });
            Motion.DialogOut(DetailBox, () =>
            {
                if (my != detailToken) return;
                detailClosing = false;
                DetailOverlay.Visibility = Visibility.Collapsed;
                DetailOverlay.BeginAnimation(OpacityProperty, null);
                DetailBox.BeginAnimation(OpacityProperty, null); DetailBox.Opacity = 1; DetailBox.RenderTransform = Transform.Identity;
            });
        }

        void DetailScrim_Down(object sender, MouseButtonEventArgs e) { CloseDetail(); e.Handled = true; }
        void Box_Down(object sender, MouseButtonEventArgs e) { e.Handled = true; }
        void DetailClose_Click(object sender, RoutedEventArgs e) => CloseDetail();
        void DetailEdit_Click(object sender, RoutedEventArgs e) { var ev = detail; CloseDetail(); if (ev != null) OpenForm(ev); }

        void DetailCancelEvent_Click(object sender, RoutedEventArgs e)
        {
            var ev = detail;
            if (ev == null) return;
            var owner = Window.GetWindow(this);
            if (!Dialog.Confirm(owner, "Cancel this event?", "\"" + ev.Title + "\" is called off. Nobody can sign up or change their answer afterwards.", "Cancel the event", danger: true)) return;
            bool following = false;
            if (ev.Series != null)
                following = Dialog.Confirm(owner, "Cancel the following weeks too?", "This event repeats every week. Cancel this date and all the later ones, or only this one?", "This and following", danger: true, cancelText: "Only this one");
            _ = S.CalendarCancel(ev, following);
            CloseDetail();
        }

        // ================================================================ form
        void New_Click(object sender, RoutedEventArgs e) => OpenForm(null);

        public void OpenForm(EventVM edit)
        {
            if (S == null || !S.SupportsCalendar) return;
            editing = edit;
            bool isEdit = edit != null;
            FormHeading.Text = isEdit ? "Edit event" : "New event";
            FPost.Content = isEdit ? "Save" : "Post it";
            FError.Text = "";
            FRepeatArea.Visibility = FChannelArea.Visibility = isEdit ? Visibility.Collapsed : Visibility.Visible;
            FScopeArea.Visibility = isEdit && edit.Series != null ? Visibility.Visible : Visibility.Collapsed;
            FScopeThis.IsChecked = true;

            // channels for the card (the lodge's text channels this person can see; the first is General when there is one)
            var chans = S.TextChannels.ToList();
            channelIds = new List<string> { null }; channelIds.AddRange(chans.Select(c => c.Id));
            FChannel.ItemsSource = new[] { "Don't post" }.Concat(chans.Select(c => "#" + c.Name)).ToList();
            var general = chans.FirstOrDefault(c => string.Equals(c.Name, "General", StringComparison.OrdinalIgnoreCase)) ?? chans.FirstOrDefault();
            FChannel.SelectedIndex = general == null ? 0 : chans.IndexOf(general) + 1;
            FormWhere.Text = isEdit ? "Changes reach everyone at once." : "Posts a card in chat that links to the event.";

            DateTime start;
            if (isEdit)
            {
                FTitle.Text = edit.Title; FNote.Text = edit.Note;
                start = CalendarRules.ToLocal(edit.StartUtc);
                fTank = edit.Tank; fHealer = edit.Healer; fDps = edit.Dps;
                (edit.Roles ? FRoles : FNoRoles).IsChecked = true;
            }
            else
            {
                FTitle.Text = ""; FNote.Text = "";
                var t = CalendarRules.ToLocal(Now);
                start = t.Date.AddDays(1).AddHours(20);            // tomorrow evening
                fTank = 2; fHealer = 3; fDps = 5;
                FRoles.IsChecked = true;
                FOnce.IsChecked = true;
            }
            ShowCounts();
            FPlaces.Visibility = FRoles.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

            // month list: this month and the next twelve (plus the event's own month when editing a past one)
            formMonths.Clear();
            var m0 = CalendarRules.ToLocal(Now).Date; m0 = new DateTime(m0.Year, m0.Month, 1);
            if (isEdit && new DateTime(start.Year, start.Month, 1) < m0) m0 = new DateTime(start.Year, start.Month, 1);
            for (int i = 0; i < 13; i++) formMonths.Add(m0.AddMonths(i));
            FMonth.ItemsSource = formMonths.Select(d => d.ToString("MMMM yyyy", CalendarRules.English)).ToList();
            FHour.ItemsSource = Enumerable.Range(0, 24).Select(h => h.ToString("00")).ToList();
            var minutes = new List<int> { 0, 15, 30, 45 }; if (!minutes.Contains(start.Minute)) { minutes.Add(start.Minute); minutes.Sort(); }
            FMinute.ItemsSource = minutes.Select(x => x.ToString("00")).ToList();
            int mi = formMonths.FindIndex(d => d.Year == start.Year && d.Month == start.Month);
            FMonth.SelectedIndex = mi < 0 ? 0 : mi;
            FillDays(start.Day);
            FHour.SelectedIndex = start.Hour;
            FMinute.SelectedIndex = minutes.IndexOf(start.Minute);

            FLength.ItemsSource = LengthNames;
            int dur = isEdit && edit.EndUtc > edit.StartUtc ? (int)(edit.EndUtc - edit.StartUtc).TotalMinutes : isEdit ? 0 : 180;
            int li = Array.IndexOf(LengthMinutes, dur);
            if (li < 0) { var names = LengthNames.ToList(); names.Add(dur + " min"); FLength.ItemsSource = names; li = names.Count - 1; customLength = dur; } else customLength = -1;
            FLength.SelectedIndex = li;

            var weeks = new List<string> { "Until I cancel" }; weeks.AddRange(Enumerable.Range(2, CalendarRules.MaxWeeks - 1).Select(w => w + " weeks"));
            FWeeks.ItemsSource = weeks;
            FWeeks.SelectedIndex = 4;   // 5 weeks... a month of Saturdays
            FWeeks.Visibility = Visibility.Collapsed;

            RefreshSuggestions();
            formClosing = false; formToken++;
            FormOverlay.BeginAnimation(OpacityProperty, null);
            FormOverlay.Visibility = Visibility.Visible;
            if (Motion.Enabled) FormOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Motion.Standard) { EasingFunction = Motion.EaseOut });
            Motion.DialogIn(FormBox);
            FTitle.Focus();
        }
        int customLength = -1;


        void FillDays(int pick)
        {
            if (FMonth.SelectedIndex < 0) return;
            var m = formMonths[FMonth.SelectedIndex];
            int n = DateTime.DaysInMonth(m.Year, m.Month);
            FDay.ItemsSource = Enumerable.Range(1, n).Select(d => d.ToString(CultureInfo.CurrentCulture)).ToList();
            FDay.SelectedIndex = Math.Max(0, Math.Min(n, pick) - 1);
        }

        void FMonth_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (FDay == null || FMonth.SelectedIndex < 0) return;
            FillDays(FDay.SelectedIndex + 1);
        }

        void FTitle_Changed(object sender, TextChangedEventArgs e) => RefreshSuggestions();
        void RefreshSuggestions() { if (FSuggest != null) FSuggest.ItemsSource = LfgCatalog.Suggest(FTitle.Text); }

        void FSuggest_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.Tag is string name)) return;
            FTitle.Text = name; FTitle.CaretIndex = name.Length; FTitle.Focus();
        }

        void ShowCounts() { FTankN.Text = fTank.ToString(); FHealerN.Text = fHealer.ToString(); FDpsN.Text = fDps.ToString(); }

        void FStep_Click(object sender, RoutedEventArgs e)
        {
            var tag = ((sender as FrameworkElement)?.Tag as string ?? "").Split(':');
            if (tag.Length != 2 || !int.TryParse(tag[1], out var d)) return;
            int Clamp(int v) => Math.Max(0, Math.Min(40, v));
            if (tag[0] == "tank") fTank = Clamp(fTank + d); else if (tag[0] == "healer") fHealer = Clamp(fHealer + d); else if (tag[0] == "dps") fDps = Clamp(fDps + d);
            ShowCounts();
        }

        void FRoles_Changed(object sender, RoutedEventArgs e) { if (FPlaces != null) FPlaces.Visibility = FRoles.IsChecked == true ? Visibility.Visible : Visibility.Collapsed; }
        void FRepeat_Changed(object sender, RoutedEventArgs e) { if (FWeeks != null) FWeeks.Visibility = FWeekly.IsChecked == true ? Visibility.Visible : Visibility.Collapsed; }

        // what the form says, as the data the rules check and send
        CalendarForm ReadForm()
        {
            var f = new CalendarForm
            {
                Title = FTitle.Text.Trim(), Note = FNote.Text.Trim(), Roles = FRoles.IsChecked == true, Tank = fTank, Healer = fHealer, Dps = fDps,
                Editing = editing != null, Scope = FScopeFollowing.IsChecked == true ? "following" : "this",
            };
            if (FMonth.SelectedIndex >= 0 && FDay.SelectedIndex >= 0) f.Day = new DateTime(formMonths[FMonth.SelectedIndex].Year, formMonths[FMonth.SelectedIndex].Month, FDay.SelectedIndex + 1);
            if (FHour.SelectedIndex >= 0) f.Hour = FHour.SelectedIndex;
            if (FMinute.SelectedIndex >= 0) f.Minute = int.Parse((string)FMinute.SelectedItem, CultureInfo.InvariantCulture);
            var li = FLength.SelectedIndex;
            f.DurationMin = li >= 0 && li < LengthMinutes.Length ? LengthMinutes[li] : customLength > 0 ? customLength : 0;
            if (editing == null)
            {
                f.RepeatWeeks = FWeekly.IsChecked != true ? 0 : FWeeks.SelectedIndex <= 0 ? -1 : FWeeks.SelectedIndex + 1;
                f.ChannelId = FChannel.SelectedIndex > 0 && FChannel.SelectedIndex < channelIds.Count ? channelIds[FChannel.SelectedIndex] : null;
            }
            return f;
        }

        void CloseForm()
        {
            if (FormOverlay.Visibility != Visibility.Visible || formClosing) return;
            if (!Motion.Enabled) { FormOverlay.Visibility = Visibility.Collapsed; return; }
            formClosing = true;
            var my = ++formToken;
            FormOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, Motion.Fast) { EasingFunction = Motion.EaseIn, FillBehavior = FillBehavior.HoldEnd });
            Motion.DialogOut(FormBox, () =>
            {
                if (my != formToken) return;
                formClosing = false;
                FormOverlay.Visibility = Visibility.Collapsed;
                FormOverlay.BeginAnimation(OpacityProperty, null);
                FormBox.BeginAnimation(OpacityProperty, null); FormBox.Opacity = 1; FormBox.RenderTransform = Transform.Identity;
            });
        }

        void FormScrim_Down(object sender, MouseButtonEventArgs e) { CloseForm(); e.Handled = true; }
        void FCancel_Click(object sender, RoutedEventArgs e) => CloseForm();
        void FPost_Click(object sender, RoutedEventArgs e) => SubmitForm();
        void Form_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { SubmitForm(); e.Handled = true; } }

        bool SubmitForm()
        {
            var f = ReadForm();
            var why = f.Validate(Now);
            if (why != null) { FError.Text = why; return false; }
            if (!S.Online && !S.TestFedForView) { FError.Text = "You are offline - try again when you are back"; return false; }
            _ = editing != null ? S.CalendarEdit(editing, f) : S.CalendarCreate(f);
            CloseForm();
            return true;
        }

        // ================================================================ test hooks
        public bool IsAgenda => agenda;
        public IList<CalendarRules.DayGroup> AgendaGroupsForTest => AgendaList.ItemsSource as IList<CalendarRules.DayGroup>;
        public FrameworkElement AgendaListElementForTest => AgendaList;
        public bool AgendaEmptyVisibleForTest => AgendaEmpty.Visibility == Visibility.Visible;
        public void SetModeForTest(bool agendaMode) { (agendaMode ? ModeAgenda : ModeMonth).IsChecked = true; }
        public IList<MonthCellVM> MonthCellsForTest => MonthGridList.ItemsSource as IList<MonthCellVM>;
        public string MonthTitleForTest => MonthTitle.Text;
        public void ShowMonthForTest(int y, int m, DateTime? day = null) { year = y; month = m; selectedDay = day; monthPicked = true; ModeMonth.IsChecked = true; BuildMonth(); }
        public bool MonthFirstInSegmentForTest => ((Panel)ModeMonth.Parent).Children.IndexOf(ModeMonth) == 0 && ((Panel)ModeMonth.Parent).Children.IndexOf(ModeAgenda) == 1;
        public CalendarRules.MonthMetrics MonthMetricsForTest => metrics;
        public bool DayPanelBesideForTest => Grid.GetColumn(DayPanel) == 1;
        public FrameworkElement DayPanelForTest => DayPanel;
        public FrameworkElement MonthColumnForTest => MonthColumn;
        public IList<WeekdayVM> WeekdaysForTest => WeekdayRow.ItemsSource as IList<WeekdayVM>;
        public void MonthGoForTest(int delta) => GoMonth(delta);
        public void MonthTodayForTest() => MonthToday_Click(null, null);
        public bool TodayButtonEnabledForTest => TodayButton.IsEnabled;
        public int SlidesForTest => slides;
        public void OpenDayForTest(DateTime day) => OpenDay(day);
        public void SelectDayForTest(DateTime d) => SelectDay(d);
        public IList<EventVM> DayEventsForTest => (DayEvents.ItemsSource as IEnumerable<EventVM>)?.ToList() ?? new List<EventVM>();
        public string DayTitleForTest => DayTitle.Text;
        public bool DetailOpenForTest => DetailOverlay.Visibility == Visibility.Visible && !detailClosing;
        public EventVM DetailForTest => detail;
        public FrameworkElement DetailBoxForTest => DetailBox;
        public void CloseDetailForTest() => CloseDetail();
        public bool DetailEditVisibleForTest => DetailEdit.Visibility == Visibility.Visible;
        public bool DetailCancelVisibleForTest => DetailCancelEvent.Visibility == Visibility.Visible;
        public bool FormOpenForTest => FormOverlay.Visibility == Visibility.Visible && !formClosing;
        public FrameworkElement FormBoxForTest => FormBox;
        public void OpenFormForTest(EventVM edit = null) => OpenForm(edit);
        public void CloseFormForTest() => CloseForm();
        public string FormTitleForTest { get => FTitle.Text; set => FTitle.Text = value; }
        public string FormNoteForTest { get => FNote.Text; set => FNote.Text = value; }
        public string FormErrorForTest => FError.Text;
        public bool SubmitFormForTest() => SubmitForm();
        public CalendarForm ReadFormForTest() => ReadForm();
        public void FormWhenForTest(int monthIndex, int day, int hour, int minuteIndex = 0) { FMonth.SelectedIndex = monthIndex; FillDays(day); FHour.SelectedIndex = hour; FMinute.SelectedIndex = minuteIndex; }
        public void FormRolesForTest(bool roles) { (roles ? FRoles : FNoRoles).IsChecked = true; }
        public void FormStepForTest(string role, int d) => FStep_Click(new Button { Tag = role + ":" + d }, null);
        public void FormWeeklyForTest(bool weekly, int weeksIndex = 0) { (weekly ? FWeekly : FOnce).IsChecked = true; FWeeks.SelectedIndex = weeksIndex; }
        public void FormLengthForTest(int index) => FLength.SelectedIndex = index;
        public System.Collections.IEnumerable FormSuggestionsForTest => FSuggest.ItemsSource;
        public bool FormRepeatAreaVisibleForTest => FRepeatArea.Visibility == Visibility.Visible;
        public bool FormScopeVisibleForTest => FScopeArea.Visibility == Visibility.Visible;
        public string FormHeadingForTest => FormHeading.Text;
        public int FormChannelCountForTest => FChannel.Items.Count;
        public bool NewButtonVisibleForTest => NewButton.Visibility == Visibility.Visible;
        public FrameworkElement MonthGridElementForTest => MonthGridList;
        public Grid MonthOverlayForTest => MonthOverlay;
        public CalendarRules.MonthLayout MonthLayoutForTest => monthLayout;
        public bool FilterBarVisibleForTest => FilterBar.Visibility == Visibility.Visible;
        public ToggleButton FilterButtonForTest(string group) => group == "lodge" ? FilterLodge : group == "holiday" ? FilterHoliday : group == "raidreset" ? FilterReset : FilterMine;
        public void ClickFilterForTest(string group, bool on) { var b = FilterButtonForTest(group); b.IsChecked = on; Filter_Click(b, null); }
        public bool WowBarVisibleForTest => WowBar.Visibility == Visibility.Visible;
        public bool WowSwitchOnForTest => WowSwitch.IsChecked == true;
        public string WowCaptionForTest => WowCaption.Text;
        public void ClickWowSwitchForTest(bool on) { WowSwitch.IsChecked = on; WowSwitch_Click(WowSwitch, null); }
        public FrameworkElement MonthScrollForTest => MonthScroll;
        public FrameworkElement AgendaScrollForTest => AgendaScroll;
    }
}
