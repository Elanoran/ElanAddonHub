using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ElansAddonHub.Lodge;

namespace ElansAddonHub
{
    // Settings > Profile: the edit-profile form (display name, avatar, accent, about, play times, characters, privacy) with a live preview card.
    public partial class SettingsPage
    {
        ProfileEditVM pvm;

        public ProfileEditVM ProfileModel => pvm;

        void InitProfile()
        {
            session.MyProfileChanged += () =>
            {
                if (SecProfile.Visibility != Visibility.Visible) return;
                if (pvm != null && pvm.Saving) { pvm.Saved(); return; }
                if ((pvm == null || !pvm.Dirty) && !(pvm != null && DateTime.UtcNow - pvm.SavedAt < TimeSpan.FromSeconds(2))) BuildProfile();
            };
            session.Changed += () => { if (SecProfile.Visibility == Visibility.Visible && StateChanged()) BuildProfile(); };
            session.Error += text =>
            {
                if (pvm != null && pvm.Saving) pvm.ServerError(text);
            };
        }

        bool ProfileReady => session.Me != null && (session.Connected || session.TestFed) && session.SupportsProfile;
        bool StateChanged() => (pvm != null) != ProfileReady;

        // entering the page (or the connection changed): fresh data, unless there are unsaved edits
        void ProfileEnter()
        {
            if (pvm == null || !pvm.Dirty || StateChanged()) BuildProfile();
        }

        void BuildProfile()
        {
            bool ready = ProfileReady;
            pvm = ready ? new ProfileEditVM(session) : null;
            ProfileForm.DataContext = pvm;
            ProfileForm.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
            ProfileEmpty.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
            if (ready) return;
            bool connected = session.Me != null && (session.Connected || session.TestFed);
            ProfileEmptyTitle.Text = connected ? "This lodge server doesn't have profiles yet" : "You're not connected to a lodge";
            ProfileEmptyText.Text = connected
                ? "Your Guild Master needs to update the lodge server before you can pick an avatar or a display name."
                : "Join a lodge from the Lodge tab and your display name, avatar, about text and characters show up here.";
            ProfileEmptyButton.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
        }

        void ProfileGoLodge_Click(object sender, RoutedEventArgs e) => host.ShowTab("lodge");

        void ProfileSave_Click(object sender, RoutedEventArgs e)
        {
            if (pvm == null || !pvm.CanSave) return;
            pvm.BeginSave();
            _ = session.SaveProfile(pvm.ToMessage());
        }

        void Avatar_Click(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is AvatarTile t) pvm?.SelectAvatar(t.Id);
        }

        void Swatch_Click(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is SwatchTile t) pvm?.SelectAccent(t.Id);
        }

        void Hide_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is CharVM c) pvm?.HideChanged(c);
        }

        public void ScrollForTest(double offset) => Scroll.ScrollToVerticalOffset(offset);
        public FrameworkElement ProfileSectionForTest => SecProfile;
    }
}
