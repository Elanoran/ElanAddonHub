using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace ElansAddonHub.Lodge
{
    // The Lodge tab, part 4: the Files row under the Calendar, the Files page that takes the chat's place, and the "Wing updated Raid UI pack (v3)"
    // notice above the chat. Everything here is hidden on a server without the "files" feature (LodgeSession.SupportsFiles).
    public partial class LodgeView
    {
        bool filesShown;
        System.Windows.Threading.DispatcherTimer filesBarTimer;

        void FilesRow_Click(object sender, MouseButtonEventArgs e) => ShowFiles();

        public void ShowFiles()
        {
            if (Session == null || !Session.SupportsFiles) return;
            if (ladderShown) { ladderShown = false; LadderPage.Visibility = Visibility.Collapsed; }   // the ladder gives the place up quietly
            if (calendarShown)       // the calendar gives the place up quietly; the files page takes it
            {
                calendarShown = false;
                CalendarPage.Visibility = Visibility.Collapsed;
            }
            filesShown = true;
            ChannelVM.CalendarShown = true;      // "a page has the chat's place": no channel looks selected
            foreach (var c in Session.TextChannels) c.RaiseRow();
            ChatCard.Visibility = Visibility.Collapsed;
            FilesPage.Visibility = Visibility.Visible;
            FilesPage.Show();
            if (IsLoaded && IsVisible) Motion.PageIn(FilesPage);
            HideFilesBar();
            UpdateFilesChrome();
        }

        public void HideFiles()
        {
            if (!filesShown) return;
            filesShown = false;
            ChannelVM.CalendarShown = calendarShown;
            if (Session != null) foreach (var c in Session.TextChannels) c.RaiseRow();
            FilesPage.Visibility = Visibility.Collapsed;
            if (!calendarShown && !ladderShown) ChatCard.Visibility = Visibility.Visible;
            UpdateFilesChrome();
        }

        // the Files row, its badge (files this PC hasn't saved or that changed since) and the page itself when the server lost the feature
        void UpdateFilesChrome()
        {
            var s = Session;
            if (s == null) return;
            bool on = s.SupportsFiles && s.Selected != null;
            FilesRow.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (!on && filesShown) HideFiles();
            if (filesShown) { FilesRow.SetResourceReference(BackgroundProperty, "Brush.Section.Files.Tint"); FilesRow.SetResourceReference(BorderBrushProperty, "Brush.Section.Files"); } else { FilesRow.ClearValue(BackgroundProperty); FilesRow.ClearValue(BorderBrushProperty); }
            FilesRowText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, filesShown ? "Brush.Text.Primary" : "Brush.Text.Secondary");
            int n = on ? s.Slots.Count(x => x.Badge.Length > 0) : 0;
            FilesBadge.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
            FilesBadgeText.Text = n.ToString();
        }

        // ---- the notice
        void ShowFilesNotice(string text)
        {
            if (filesShown || string.IsNullOrEmpty(text)) return;      // the page itself shows the change
            FilesBarText.Text = text;
            FilesBar.Visibility = Visibility.Visible;
            if (filesBarTimer == null)
            {
                filesBarTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
                filesBarTimer.Tick += (a, b) => HideFilesBar();
            }
            filesBarTimer.Stop(); filesBarTimer.Start();
        }

        void HideFilesBar()
        {
            FilesBar.Visibility = Visibility.Collapsed;
            filesBarTimer?.Stop();
        }

        void FilesBarClose_Click(object sender, RoutedEventArgs e) => HideFilesBar();
        void FilesBarOpen_Click(object sender, RoutedEventArgs e) => ShowFiles();

        // ---- test hooks
        public FilesView FilesForTest => FilesPage;
        public bool FilesShownForTest => filesShown && FilesPage.Visibility == Visibility.Visible && ChatCard.Visibility != Visibility.Visible;
        public void ShowFilesForTest() => ShowFiles();
        public void HideFilesForTest() => HideFiles();
        public bool FilesRowVisibleForTest => FilesRow.Visibility == Visibility.Visible;
        public string FilesBadgeForTest => FilesBadge.Visibility == Visibility.Visible ? FilesBadgeText.Text : "";
        public bool FilesBarVisibleForTest => FilesBar.Visibility == Visibility.Visible;
        public string FilesBarTextForTest => FilesBarText.Text;
        public void OpenFilesBarForTest() => FilesBarOpen_Click(null, null);
        public void CloseFilesBarForTest() => FilesBarClose_Click(null, null);
    }
}
