using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ElansAddonHub.Lodge
{
    // The Lodge tab, part 2: the composer's "+" menu, the "Looking for group" form, and the buttons of a sign-up card.
    public partial class LodgeView
    {
        int lfgTank = 1, lfgHealer = 1, lfgDps = 3;
        readonly List<DateTime> lfgDays = new List<DateTime>();

        // ---- the composer's "+"
        void Plus_Click(object sender, RoutedEventArgs e)
        {
            bool files = Session.CanShareFiles, lfg = Session.SupportsLfg;
            if (files && !lfg) { Attach_Click(sender, e); return; }
            if (lfg && !files) { OpenLfgForm(); return; }
            PlusFile.Visibility = files ? Visibility.Visible : Visibility.Collapsed;
            PlusLfg.Visibility = lfg ? Visibility.Visible : Visibility.Collapsed;
            PlusPopup.PlacementTarget = AttachButton;
            PlusPopup.IsOpen = true;
        }

        void PlusFile_Click(object sender, RoutedEventArgs e) { PlusPopup.IsOpen = false; Attach_Click(sender, e); }
        void PlusLfg_Click(object sender, RoutedEventArgs e) { PlusPopup.IsOpen = false; OpenLfgForm(); }

        // ---- the form
        public void OpenLfgForm()
        {
            if (Session?.Selected == null || !Session.SupportsLfg) return;
            LfgWhere.Text = "Posts a sign-up card in #" + Session.Selected.Name;
            LfgTitle.Text = "";
            LfgNote.Text = "";
            LfgError.Text = "";
            lfgTank = 1; lfgHealer = 1; lfgDps = 3;
            ShowCounts();
            LfgNow.IsChecked = true;
            LfgTimeRow.Visibility = Visibility.Collapsed;

            lfgDays.Clear();
            var today = DateTime.Now.Date;
            var names = new List<string>();
            for (int i = 0; i < 14; i++)
            {
                lfgDays.Add(today.AddDays(i));
                names.Add(i == 0 ? "Today" : i == 1 ? "Tomorrow" : today.AddDays(i).ToString("ddd d MMM", CultureInfo.CurrentCulture));
            }
            LfgDay.ItemsSource = names;
            LfgHour.ItemsSource = Enumerable.Range(0, 24).Select(h => h.ToString("00")).ToList();
            LfgMinute.ItemsSource = new[] { "00", "15", "30", "45" };
            var next = DateTime.Now.AddMinutes(60 - DateTime.Now.Minute);   // the next full hour
            LfgDay.SelectedIndex = Math.Max(0, (next.Date - today).Days);
            LfgHour.SelectedIndex = next.Hour;
            LfgMinute.SelectedIndex = 0;

            RefreshLfgSuggestions();
            LfgOverlay.Visibility = Visibility.Visible;
            LfgTitle.Focus();
        }

        void CloseLfgForm() { LfgOverlay.Visibility = Visibility.Collapsed; Composer.Focus(); }

        void ShowCounts() { LfgTankN.Text = lfgTank.ToString(); LfgHealerN.Text = lfgHealer.ToString(); LfgDpsN.Text = lfgDps.ToString(); }

        void LfgStep_Click(object sender, RoutedEventArgs e)
        {
            var tag = ((sender as FrameworkElement)?.Tag as string ?? "").Split(':');
            if (tag.Length != 2 || !int.TryParse(tag[1], out var d)) return;
            int Clamp(int v) => Math.Max(0, Math.Min(40, v));
            if (tag[0] == "tank") lfgTank = Clamp(lfgTank + d);
            else if (tag[0] == "healer") lfgHealer = Clamp(lfgHealer + d);
            else if (tag[0] == "dps") lfgDps = Clamp(lfgDps + d);
            ShowCounts();
        }

        void LfgTitle_Changed(object sender, TextChangedEventArgs e) => RefreshLfgSuggestions();

        void RefreshLfgSuggestions() => LfgSuggest.ItemsSource = LfgCatalog.Suggest(LfgTitle.Text);

        void LfgSuggest_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.Tag is string name)) return;
            LfgTitle.Text = name;
            LfgTitle.CaretIndex = name.Length;
            LfgTitle.Focus();
        }

        void LfgWhen_Changed(object sender, RoutedEventArgs e)
        {
            if (LfgTimeRow == null) return;
            LfgTimeRow.Visibility = LfgLater.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        void LfgScrim_Down(object sender, MouseButtonEventArgs e) { CloseLfgForm(); e.Handled = true; }
        void LfgBox_Down(object sender, MouseButtonEventArgs e) { e.Handled = true; }   // a click inside the card must not reach the scrim
        void LfgCancelForm_Click(object sender, RoutedEventArgs e) => CloseLfgForm();

        void Lfg_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { CloseLfgForm(); e.Handled = true; }
            else if (e.Key == Key.Enter) { SubmitLfg(); e.Handled = true; }
        }

        void LfgPost_Click(object sender, RoutedEventArgs e) => SubmitLfg();

        // checks what can be checked here (the server checks everything again), then asks the session to post
        bool SubmitLfg()
        {
            var title = LfgTitle.Text.Trim();
            if (title.Length == 0) { LfgError.Text = "Give the group a title or pick an instance"; return false; }
            if (lfgTank + lfgHealer + lfgDps == 0) { LfgError.Text = "A group needs at least one place"; return false; }
            bool now = LfgLater.IsChecked != true;
            var start = DateTime.Now;
            if (!now)
            {
                if (LfgDay.SelectedIndex < 0 || LfgHour.SelectedIndex < 0 || LfgMinute.SelectedIndex < 0) { LfgError.Text = "Pick the day and the time"; return false; }
                start = lfgDays[LfgDay.SelectedIndex].AddHours(LfgHour.SelectedIndex).AddMinutes(LfgMinute.SelectedIndex * 15);
                if (start < DateTime.Now.AddMinutes(-10)) { LfgError.Text = "That time has already passed"; return false; }
            }
            if (!Session.Online && !Session.TestFedForView) { LfgError.Text = "You are offline - try again when you are back"; return false; }
            _ = Session.PostLfg(title, now, start, lfgTank, lfgHealer, lfgDps, LfgNote.Text.Trim());
            CloseLfgForm();
            return true;
        }

        // ---- the buttons of a card
        MessageVM CardOf(object sender) => ((sender as FrameworkElement)?.Tag as LfgVM)?.Msg;
        void LfgTank_Click(object sender, RoutedEventArgs e) { var m = CardOf(sender); if (m != null) _ = Session.LfgJoin(m, "tank"); }
        void LfgHealer_Click(object sender, RoutedEventArgs e) { var m = CardOf(sender); if (m != null) _ = Session.LfgJoin(m, "healer"); }
        void LfgDps_Click(object sender, RoutedEventArgs e) { var m = CardOf(sender); if (m != null) _ = Session.LfgJoin(m, "dps"); }
        void LfgMaybe_Click(object sender, RoutedEventArgs e) { var m = CardOf(sender); if (m != null) _ = Session.LfgMaybe(m); }
        void LfgLeave_Click(object sender, RoutedEventArgs e) { var m = CardOf(sender); if (m != null) _ = Session.LfgLeave(m); }
        void LfgClose_Click(object sender, RoutedEventArgs e) { var m = CardOf(sender); if (m != null) _ = Session.LfgClose(m); }
        void LfgCancel_Click(object sender, RoutedEventArgs e)
        {
            var m = CardOf(sender);
            if (m == null) return;
            if (Dialog.Confirm(Window.GetWindow(this), "Cancel this group?", "\"" + m.Lfg.Title + "\" is called off. Nobody can sign up or leave afterwards.", "Cancel the group", danger: true))
                _ = Session.LfgCancel(m);
        }

        // ---- test hooks
        public bool LfgFormOpenForTest => LfgOverlay.Visibility == Visibility.Visible;
        public void OpenLfgFormForTest() => OpenLfgForm();
        public void CloseLfgFormForTest() => CloseLfgForm();
        public string LfgTitleForTest { get => LfgTitle.Text; set => LfgTitle.Text = value; }
        public string LfgNoteForTest { get => LfgNote.Text; set => LfgNote.Text = value; }
        public string LfgErrorForTest => LfgError.Text;
        public void LfgSetLaterForTest(bool later, int dayIndex = 0, int hour = 20, int minuteIndex = 0)
        {
            if (later) { LfgLater.IsChecked = true; LfgDay.SelectedIndex = dayIndex; LfgHour.SelectedIndex = hour; LfgMinute.SelectedIndex = minuteIndex; }
            else LfgNow.IsChecked = true;
        }
        public void LfgStepForTest(string role, int d) => LfgStep_Click(new Button { Tag = role + ":" + d }, null);
        public int[] LfgCountsForTest => new[] { lfgTank, lfgHealer, lfgDps };
        public bool SubmitLfgForTest() => SubmitLfg();
        public System.Collections.IEnumerable LfgSuggestionsForTest => LfgSuggest.ItemsSource;
        public FrameworkElement LfgBoxForTest => LfgBox;
        public bool PlusIsVisibleForTest => AttachButton.Visibility == Visibility.Visible;
        public void PlusClickForTest() => Plus_Click(AttachButton, null);
        public bool PlusMenuOpenForTest => PlusPopup.IsOpen;
        public string PlusMenuForTest => (PlusFile.Visibility == Visibility.Visible ? "file " : "") + (PlusLfg.Visibility == Visibility.Visible ? "lfg" : "");
        public void ClosePlusForTest() => PlusPopup.IsOpen = false;
        public void ScrollToEndForTest() { MessageScroll.UpdateLayout(); MessageScroll.ScrollToEnd(); }
    }
}
