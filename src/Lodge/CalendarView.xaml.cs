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
        public bool Selected { get => selected; set { if (Set(ref selected, value)) { Raise(nameof(Fill)); } } }
        public Brush Fill => selected ? Avatar.Res("Brush.Accent.Tint") : Cell.IsToday ? Avatar.Res("Brush.Overlay.Chip") : System.Windows.Media.Brushes.Transparent;
        public Brush NumberBrush => Avatar.Res(Cell.InMonth ? "Brush.Text.Primary" : "Brush.Text.Tertiary");
        public double CellOpacity => Cell.InMonth ? 1 : 0.6;
        // a WoW holiday covering this day: a thin band along the bottom that joins the neighbouring days
        public Visibility BandVisibility => Cell.Bands.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Thickness BandMargin => new Thickness(Cell.BandHead ? 4 : -4, 0, Cell.BandTail ? 4 : -4, 4);
        // one dot per event (three at most): Success = going, Warn = maybe, Tertiary = no answer or cancelled
        public List<Brush> Dots => Cell.Events.Take(3).Select(e => Avatar.Res(e.IsCancelled ? "Brush.Text.Disabled" : e.MyStatus == "going" ? "Brush.Success" : e.MyStatus == "maybe" ? "Brush.Warn" : "Brush.Text.Tertiary")).ToList();
        public List<string> DotKeys => Cell.Events.Take(3).Select(e => e.IsCancelled ? "cancelled" : e.MyStatus ?? "none").ToList();
    }

    // agenda / day cards: the Lodge event card, or the quiet read-only card of a WoW event
    public class EventCardSelector : DataTemplateSelector
    {
        public DataTemplate Server { get; set; }
        public DataTemplate Wow { get; set; }
        public override DataTemplate SelectTemplate(object item, DependencyObject container) => (item as EventVM)?.IsWow == true ? Wow : Server;
    }

    // The calendar page of the Lodge: Agenda (default) and Month, the event detail card and the create / edit form. All state is in LodgeSession.
    public partial class CalendarView : UserControl
    {
        LodgeSession S;
        bool agenda = true;
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
                var groups = CalendarRules.Agenda(S.ViewEvents, Now);
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
        }

        void WowSwitch_Click(object sender, RoutedEventArgs e)
        {
            if (S == null || settingWowSwitch) return;
            S.Settings.WowEventsOff = WowSwitch.IsChecked != true;
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
        void BuildMonth()
        {
            S.EnsureMonth(year, month);
            var first = new DateTime(year, month, 1);
            MonthTitle.Text = first.ToString("MMMM yyyy", CalendarRules.English);
            var fdw = (int)CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
            WeekdayRow.ItemsSource = Enumerable.Range(0, 7).Select(i => CalendarRules.English.DateTimeFormat.GetAbbreviatedDayName((DayOfWeek)((fdw + i) % 7))).ToList();
            var cells = CalendarRules.MonthGrid(year, month, S.ViewEvents, Now).Select(c => new MonthCellVM(c)).ToList();
            var today = CalendarRules.ToLocal(Now).Date;
            if (selectedDay == null || selectedDay.Value.Year != year || selectedDay.Value.Month != month) selectedDay = today.Year == year && today.Month == month ? today : first;
            foreach (var c in cells) c.Selected = c.Cell.Day == selectedDay.Value;
            MonthGridList.ItemsSource = cells;
            ShowDay();
        }

        void ShowDay()
        {
            var day = selectedDay ?? CalendarRules.ToLocal(Now).Date;
            DayTitle.Text = CalendarRules.DayLabel(day, CalendarRules.ToLocal(Now).Date) + (day.Date == CalendarRules.ToLocal(Now).Date.AddDays(0) || day.Date == CalendarRules.ToLocal(Now).Date.AddDays(1) ? " · " + day.ToString("d MMMM", CalendarRules.English) : "");
            var evs = S.ViewEvents.Where(e => e.IsBand ? CalendarRules.BandCovers(e, day) : CalendarRules.ToLocal(e.StartUtc).Date == day.Date).OrderBy(e => e.IsBand ? 0 : 1).ThenBy(e => e.StartUtc).ToList();
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
            if (day.Year != year || day.Month != month) { year = day.Year; month = day.Month; BuildMonth(); return; }
            foreach (var c in MonthGridList.Items.OfType<MonthCellVM>()) c.Selected = c.Cell.Day == selectedDay.Value;
            ShowDay();
        }

        void MonthPrev_Click(object sender, RoutedEventArgs e) { var d = new DateTime(year, month, 1).AddMonths(-1); year = d.Year; month = d.Month; BuildMonth(); }
        void MonthNext_Click(object sender, RoutedEventArgs e) { var d = new DateTime(year, month, 1).AddMonths(1); year = d.Year; month = d.Month; BuildMonth(); }
        void MonthToday_Click(object sender, RoutedEventArgs e) { var t = CalendarRules.ToLocal(Now).Date; year = t.Year; month = t.Month; selectedDay = t; BuildMonth(); }

        // ---- opening one event (from a chat card, the sidebar line or a click)
        public void OpenEvent(EventVM ev)
        {
            if (ev == null) return;
            if (!ev.Stub) { OpenDetail(ev); return; }
            // only the chat card's snapshot is known: show its month, and open it when the server's answer arrives
            var l = CalendarRules.ToLocal(ev.StartUtc);
            ModeMonth.IsChecked = true;
            year = l.Year; month = l.Month; selectedDay = l.Date;
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
        public void ShowMonthForTest(int y, int m, DateTime? day = null) { year = y; month = m; selectedDay = day; ModeMonth.IsChecked = true; BuildMonth(); }
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
        public bool WowBarVisibleForTest => WowBar.Visibility == Visibility.Visible;
        public bool WowSwitchOnForTest => WowSwitch.IsChecked == true;
        public string WowCaptionForTest => WowCaption.Text;
        public void ClickWowSwitchForTest(bool on) { WowSwitch.IsChecked = on; WowSwitch_Click(WowSwitch, null); }
        public FrameworkElement MonthScrollForTest => MonthScroll;
        public FrameworkElement AgendaScrollForTest => AgendaScroll;
    }
}
