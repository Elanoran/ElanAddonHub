using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace ElansAddonHub.Lodge
{
    // The Ladder page of the Lodge (server feature "badges"): who has shared the most this month or all time, with their best badges. Your own row is highlighted.
    // Quiet: it only asks when the page is opened or the switch is flipped, and never pops anything up.
    public partial class LadderView : UserControl
    {
        LodgeSession S;
        public Action OpenBadges;       // "Your badges" (the Lodge shows the badges card)

        public LadderView() { InitializeComponent(); }

        public void Init(LodgeSession session)
        {
            S = session;
            S.LadderChanged += Refresh;
            S.BadgesChanged += Refresh;
            Refresh();
        }

        public void Show()
        {
            Refresh();
            Ask();
        }

        bool AllTime => ModeAll.IsChecked == true;
        string MonthKey => S.Clock().ToUniversalTime().ToString("yyyy-MM", CultureInfo.InvariantCulture);
        string WantedSeason => AllTime ? "all" : (string.IsNullOrEmpty(S.LadderCurrent) ? MonthKey : S.LadderCurrent);

        void Ask() { if (S != null) S.RequestLadder(WantedSeason); }

        void Mode_Changed(object sender, RoutedEventArgs e)
        {
            if (S == null) return;
            Ask();
            Refresh();
        }

        void Badges_Click(object sender, RoutedEventArgs e) => OpenBadges?.Invoke();

        void Refresh()
        {
            if (S == null) return;
            NewDot.Visibility = S.NewBadges > 0 ? Visibility.Visible : Visibility.Collapsed;
            bool mine = S.LadderLoaded && S.LadderSeason == WantedSeason;
            var rows = mine ? S.LadderRows : new List<LadderRowVM>();
            RowList.ItemsSource = rows;
            EmptyState.Visibility = mine && rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyTitle.Text = AllTime ? "Nobody is on the ladder yet" : "A fresh month, nobody is on the ladder yet";
            EmptyHint.Text = "Share what you find with the lodge (Atlas, flight times, files, the calendar) and your name shows up here.";
            var you = rows.FirstOrDefault(r => r.IsYou);
            if (!mine) YouLine.Text = "";
            else if (you != null) YouLine.Text = "You are number " + you.Rank + (AllTime ? " of all time" : " this month") + " with " + you.PointsText + " points." + (you.Hidden ? " Only you can see this row." : "");
            else if (S.HideFromLadder) YouLine.Text = "You are hidden from the ladder. Your badges are still yours.";
            else YouLine.Text = AllTime ? "" : "You have no points this month yet.";
            YouLine.Visibility = YouLine.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---- test hooks
        public void PickMonthForTest() { ModeMonth.IsChecked = true; }
        public void PickAllForTest() { ModeAll.IsChecked = true; }
        public bool AllTimeForTest => AllTime;
        public IEnumerable<LadderRowVM> RowsForTest => (RowList.ItemsSource as IEnumerable<LadderRowVM>) ?? Enumerable.Empty<LadderRowVM>();
        public string YouLineForTest => YouLine.Visibility == Visibility.Visible ? YouLine.Text : "";
        public bool EmptyVisibleForTest => EmptyState.Visibility == Visibility.Visible;
        public bool NewDotVisibleForTest => NewDot.Visibility == Visibility.Visible;
        public void BadgesClickForTest() => Badges_Click(null, null);
    }
}
