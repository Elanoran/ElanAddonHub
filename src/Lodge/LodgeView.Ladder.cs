using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace ElansAddonHub.Lodge
{
    // The Lodge tab, part 5: the Ladder row under Files, the Ladder page that takes the chat's place, and the badges card (a person's badges over a scrim).
    // Everything here is hidden on a server without the "badges" feature (LodgeSession.SupportsBadges), and nothing in it ever pops up by itself.
    public partial class LodgeView
    {
        bool ladderShown;
        string badgesOf;                  // whose badges the card shows (a login)

        void LadderRow_Click(object sender, MouseButtonEventArgs e) => ShowLadder();

        public void ShowLadder()
        {
            if (Session == null || !Session.SupportsBadges) return;
            if (calendarShown) { calendarShown = false; CalendarPage.Visibility = Visibility.Collapsed; }
            if (filesShown) { filesShown = false; FilesPage.Visibility = Visibility.Collapsed; }
            ladderShown = true;
            ChannelVM.CalendarShown = true;      // "a page has the chat's place": no channel looks selected
            foreach (var c in Session.TextChannels) c.RaiseRow();
            ChatCard.Visibility = Visibility.Collapsed;
            LadderPage.Visibility = Visibility.Visible;
            LadderPage.Show();
            if (IsLoaded && IsVisible) Motion.PageIn(LadderPage);
            UpdatePageRows();
        }

        public void HideLadder()
        {
            if (!ladderShown) return;
            ladderShown = false;
            ChannelVM.CalendarShown = calendarShown;
            if (Session != null) foreach (var c in Session.TextChannels) c.RaiseRow();
            LadderPage.Visibility = Visibility.Collapsed;
            if (!calendarShown && !filesShown) ChatCard.Visibility = Visibility.Visible;
            UpdateLadderChrome();
        }

        // the Ladder row, and the page itself when the server (or the Guild Master) switches the feature off
        // a page that takes the chat's place from another one: every page row (Calendar, Files, Ladder) shows which page is open now
        void UpdatePageRows() { UpdateCalendarChrome(); UpdateFilesChrome(); UpdateLadderChrome(); }

        void UpdateLadderChrome()
        {
            var s = Session;
            if (s == null) return;
            bool on = s.SupportsBadges && s.Selected != null;
            LadderRow.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (!on && ladderShown) HideLadder();
            if (!on && BadgesOverlay.Visibility == Visibility.Visible) CloseBadges();
            PopBadges.Visibility = on && popMember != null && !popMember.Guest ? Visibility.Visible : Visibility.Collapsed;
            if (ladderShown) { LadderRow.SetResourceReference(BackgroundProperty, "Brush.Section.Ladder.Tint"); LadderRow.SetResourceReference(BorderBrushProperty, "Brush.Section.Ladder"); } else { LadderRow.ClearValue(BackgroundProperty); LadderRow.ClearValue(BorderBrushProperty); }
            LadderRowText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, ladderShown ? "Brush.Text.Primary" : "Brush.Text.Secondary");
        }

        // ---- the badges card
        void PopBadges_Click(object sender, RoutedEventArgs e)
        {
            if (popMember == null) return;
            var who = popMember.Name;
            MemberPopup.IsOpen = false;
            ShowBadges(who);
        }

        public void ShowBadges(string login)
        {
            var s = Session;
            if (s == null || !s.SupportsBadges || string.IsNullOrEmpty(login)) return;
            badgesOf = login;
            bool me = s.Me != null && string.Equals(login, s.Me.Name, StringComparison.OrdinalIgnoreCase);
            BadgesOverlay.Visibility = Visibility.Visible;
            FillBadges();
            if (me) { s.RequestMyBadges(); s.MarkBadgesSeen(); } else s.RequestMemberBadges(login);
        }

        void FillBadges()
        {
            var s = Session;
            if (s == null || badgesOf == null) return;
            bool me = s.Me != null && string.Equals(badgesOf, s.Me.Name, StringComparison.OrdinalIgnoreCase);
            if (me)
            {
                BadgesHeading.Text = "Your badges";
                BadgesSub.Text = s.MyBadgesLoaded ? s.EarnedCount + " of " + s.TotalBadges + " earned. Just for fun: nothing here asks anything of you." : "Loading...";
                BadgesList.Show(s.MyBadges, null, null);
            }
            else
            {
                var list = s.BadgesOf(badgesOf);
                BadgesHeading.Text = DisplayNames.Shown(badgesOf) + "'s badges";
                BadgesSub.Text = list == null ? "Loading..." : list.Count + (list.Count == 1 ? " badge earned" : " badges earned");
                BadgesList.Show(list ?? new System.Collections.Generic.List<BadgeVM>(), null, list != null && list.Count == 0 ? "No badges yet." : null);
            }
        }

        void CloseBadges()
        {
            BadgesOverlay.Visibility = Visibility.Collapsed;
            badgesOf = null;
        }

        void BadgesClose_Click(object sender, RoutedEventArgs e) => CloseBadges();
        void BadgesScrim_Down(object sender, MouseButtonEventArgs e) { CloseBadges(); e.Handled = true; }
        void BadgesBox_Down(object sender, MouseButtonEventArgs e) => e.Handled = true;

        // the session says something about badges changed: the open card and the profile card follow
        void OnBadgesChanged()
        {
            UpdateLadderChrome();
            if (BadgesOverlay.Visibility == Visibility.Visible) FillBadges();
        }

        void OnMemberBadges(string login)
        {
            if (BadgesOverlay.Visibility == Visibility.Visible && string.Equals(login, badgesOf, StringComparison.OrdinalIgnoreCase)) FillBadges();
            if (popCard != null && MemberPopup.IsOpen && string.Equals(login, popCard.Member.Name, StringComparison.OrdinalIgnoreCase)) popCard.SetBadges(Session.BadgesOf(login));
        }

        // ---- test hooks
        public LadderView LadderForTest => LadderPage;
        public bool LadderShownForTest => ladderShown && LadderPage.Visibility == Visibility.Visible && ChatCard.Visibility != Visibility.Visible;
        public void ShowLadderForTest() => ShowLadder();
        public void HideLadderForTest() => HideLadder();
        public bool LadderRowVisibleForTest => LadderRow.Visibility == Visibility.Visible;
        public string[] LitPageRowsForTest => new[] { (CalendarRow, "Calendar"), (FilesRow, "Files"), (LadderRow, "Ladder") }
            .Where(r => r.Item1.ReadLocalValue(BackgroundProperty) != DependencyProperty.UnsetValue).Select(r => r.Item2).ToArray();
        public bool BadgesCardVisibleForTest => BadgesOverlay.Visibility == Visibility.Visible;
        public string BadgesHeadingForTest => BadgesHeading.Text;
        public BadgesPanel BadgesListForTest => BadgesList;
        public void ShowBadgesForTest(string login) => ShowBadges(login);
        public void CloseBadgesForTest() => CloseBadges();
        public bool PopBadgesVisibleForTest => PopBadges.Visibility == Visibility.Visible;
        public void PopBadgesClickForTest() => PopBadges_Click(null, null);
        public void HideCardForTest() => MemberPopup.IsOpen = false;
        public System.Windows.FrameworkElement PopCardElementForTest => PopCard;
    }
}
