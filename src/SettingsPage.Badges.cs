using System;
using System.Windows;
using ElansAddonHub.Lodge;

namespace ElansAddonHub
{
    // Badges and the ladder in the settings (server feature "badges"): Settings > Lodge > Ladder ("Hide me from the ladder"), Settings > Profile > Badges (your badges, with a
    // small dot while there are new ones you have not looked at) and Settings > Guild Master > Badges and ladder (the whole feature on / off). Every part is hidden on a server
    // without the feature. The "hide" choice and the Guild Master's switch live on the server; nothing is stored on this PC.
    public partial class SettingsPage
    {
        bool badgesOpen;

        void InitBadges()
        {
            session.BadgesChanged += () => Dispatcher.BeginInvoke(new Action(FillBadgesSettings));
        }

        bool BadgesMember => session.SupportsBadges && session.Me != null && !session.Me.Guest && (session.Connected || session.TestFed);

        void FillBadgesSettings()
        {
            if (LadderSettings == null || settings == null) return;
            bool wasLoading = loading;
            loading = true;
            try
            {
                LadderSettings.Visibility = BadgesMember ? Visibility.Visible : Visibility.Collapsed;
                HideLadderBox.IsChecked = session.HideFromLadder;

                bool profile = BadgesMember && ProfileForm.Visibility == Visibility.Visible;
                BadgesHead.Visibility = BadgesCard.Visibility = profile ? Visibility.Visible : Visibility.Collapsed;
                BadgesDot.Visibility = session.NewBadges > 0 && !badgesOpen ? Visibility.Visible : Visibility.Collapsed;
                BadgesBody.Visibility = badgesOpen && profile ? Visibility.Visible : Visibility.Collapsed;
                BadgesToggle.Content = badgesOpen ? "Hide my badges" : "Show my badges";
                if (badgesOpen && profile) FillBadgesPanel();

                // the Guild Master's switch: only where the server knows about badges at all
                bool known = (session.Connected || session.TestFed) && (session.BadgesStateKnown || session.SupportsBadges);
                BadgesLodgeHead.Visibility = BadgesLodgeCard.Visibility = known ? Visibility.Visible : Visibility.Collapsed;
                BadgesLodgeBox.IsChecked = session.BadgesStateKnown ? session.BadgesOwnerOn && !session.BadgesLocked : session.SupportsBadges;
                BadgesLodgeBox.IsEnabled = !session.BadgesLocked;
                BadgesLodgeNote.Text = session.BadgesLocked ? "Switched off in the lodge server's own settings (LODGE_BADGES_ENABLED), so it can't be turned on here." : "";
                BadgesLodgeNote.Visibility = session.BadgesLocked ? Visibility.Visible : Visibility.Collapsed;
            }
            finally { loading = wasLoading; }
        }

        void FillBadgesPanel()
        {
            BadgesPanelHost.Show(session.MyBadges, session.MyBadgesLoaded ? session.EarnedCount + " of " + session.TotalBadges + " earned." : "Loading...", null);
        }

        void BadgesToggle_Click(object sender, RoutedEventArgs e)
        {
            badgesOpen = !badgesOpen;
            if (badgesOpen) { session.RequestMyBadges(); session.MarkBadgesSeen(); }
            FillBadgesSettings();
        }

        void HideLadder_Click(object sender, RoutedEventArgs e)
        {
            if (loading) return;
            session.SetHideFromLadder(HideLadderBox.IsChecked == true);
        }

        void BadgesLodge_Click(object sender, RoutedEventArgs e)
        {
            if (loading) return;
            session.SetBadgesForLodge(BadgesLodgeBox.IsChecked == true);
        }

        // ---- test hooks
        public bool LadderSettingsVisibleForTest => LadderSettings.Visibility == Visibility.Visible;
        public bool HideLadderForTest => HideLadderBox.IsChecked == true;
        public void HideLadderSwitchForTest(bool on) { HideLadderBox.IsChecked = on; HideLadder_Click(HideLadderBox, new RoutedEventArgs()); }
        public bool BadgesCardVisibleForTest => BadgesCard.Visibility == Visibility.Visible;
        public bool BadgesDotVisibleForTest => BadgesDot.Visibility == Visibility.Visible;
        public void BadgesToggleForTest() => BadgesToggle_Click(null, null);
        public BadgesPanel BadgesPanelForTest => BadgesPanelHost;
        public bool BadgesLodgeVisibleForTest => BadgesLodgeCard.Visibility == Visibility.Visible;
        public bool BadgesLodgeOnForTest => BadgesLodgeBox.IsChecked == true;
        public bool BadgesLodgeEnabledForTest => BadgesLodgeBox.IsEnabled;
        public void BadgesLodgeSwitchForTest(bool on) { BadgesLodgeBox.IsChecked = on; BadgesLodge_Click(BadgesLodgeBox, new RoutedEventArgs()); }
        public void FillBadgesForTest() => FillBadgesSettings();
    }
}
