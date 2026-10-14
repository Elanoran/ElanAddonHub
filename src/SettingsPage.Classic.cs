using System;
using System.Windows;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;

namespace ElansAddonHub
{
    // Settings > Lodge > Classic data: "Get Classic data from my Lodge automatically" (on by default) and, for the owner and officers, "Classic data for the Lodge" with the
    // one-click share (ClassicLodgeSync does the work). The status line under them says where the data is: the Lodge hosts it, this PC has it, or why not.
    public partial class SettingsPage
    {
        ClassicLodgeSync classicSync;

        void InitClassic()
        {
            session.ClassicChanged += () => Dispatcher.BeginInvoke(new Action(FillClassicSettings));
        }

        public void UseClassicSync(ClassicLodgeSync sync)
        {
            if (classicSync != null) classicSync.Changed -= OnClassicSyncChanged;
            classicSync = sync;
            if (sync != null) sync.Changed += OnClassicSyncChanged;
            FillClassicSettings();
        }

        void OnClassicSyncChanged() => Dispatcher.BeginInvoke(new Action(FillClassicSettings));

        void FillClassicSettings()
        {
            if (ClassicStatus == null || settings == null) return;
            bool wasLoading = loading;
            loading = true;
            try
            {
                ClassicAutoBox.IsChecked = !settings.ClassicFromLodgeOff;
                bool owner = (session.Connected || session.TestFed) && session.SupportsClassic && session.CanShareClassic;
                ClassicLodgeRow.Visibility = owner ? Visibility.Visible : Visibility.Collapsed;
                ClassicShareButton.Visibility = owner && classicSync != null && classicSync.ShareAvailable ? Visibility.Visible : Visibility.Collapsed;
                ClassicShareButton.IsEnabled = classicSync != null && !classicSync.Sharing;
                ClassicShareButton.Content = classicSync != null && classicSync.Sharing ? "Sharing..." : classicSync != null && classicSync.Hosted != null ? "Share my newer Classic data with the Lodge" : "Share my Classic data with the Lodge";
                ClassicUnshareButton.Visibility = owner && session.ClassicHosted != null ? Visibility.Visible : Visibility.Collapsed;
                var text = classicSync?.StatusText ?? "";
                ClassicStatus.Text = text;
                ClassicStatus.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            finally { loading = wasLoading; }
        }

        void ClassicAuto_Click(object sender, RoutedEventArgs e)
        {
            if (loading) return;
            settings.ClassicFromLodgeOff = ClassicAutoBox.IsChecked != true;
            SettingsStore.Save(settings);
            classicSync?.SettingsChanged();
            FillClassicSettings();
        }

        async void ClassicShare_Click(object sender, RoutedEventArgs e)
        {
            if (classicSync == null) return;
            var task = classicSync.ShareAsync();
            FillClassicSettings();
            await task;
            FillClassicSettings();
        }

        void ClassicUnshare_Click(object sender, RoutedEventArgs e)
        {
            if (classicSync == null) return;
            if (!Dialog.Confirm(Window.GetWindow(this), "Stop hosting the Classic data?", "Members who already have it keep it. New members and PCs without it will download it from the public source instead.", "Stop hosting", true)) return;
            classicSync.Unshare();
        }

        // self-test hooks
        public bool ClassicAutoForTest => ClassicAutoBox.IsChecked == true;
        public void ClassicAutoSwitchForTest(bool on) { ClassicAutoBox.IsChecked = on; ClassicAuto_Click(ClassicAutoBox, new RoutedEventArgs()); }
        public bool ClassicLodgeRowVisibleForTest => ClassicLodgeRow.Visibility == Visibility.Visible;
        public bool ClassicShareVisibleForTest => ClassicShareButton.Visibility == Visibility.Visible;
        public bool ClassicUnshareVisibleForTest => ClassicUnshareButton.Visibility == Visibility.Visible;
        public string ClassicStatusForTest => ClassicStatus.Text;
        public void ClassicRefreshForTest() => FillClassicSettings();
        public System.Threading.Tasks.Task ClassicShareClickForTest() { var t = classicSync.ShareAsync(); FillClassicSettings(); return t; }
        public void ClassicUnshareClickForTest() => ClassicUnshare_Click(ClassicUnshareButton, new RoutedEventArgs());
        public void ClassicScrollForTest() { ClassicAutoBox.BringIntoView(); ClassicStatus.BringIntoView(); }
    }
}
