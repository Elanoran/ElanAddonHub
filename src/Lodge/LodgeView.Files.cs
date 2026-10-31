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
            UpdatePageRows();
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
            barSlot = Session?.FilesNoticeFor;
            BindBar();
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
        void FilesBarOpen_Click(object sender, RoutedEventArgs e)
        {
            var slot = BarSlot();
            ShowFiles();
            if (slot != null) FilesPage.OpenVersions(slot);
        }

        // ---- 3.5: the notice as an update card - the file's picture, its version and the one thing to do with it (install / update, copy, save)
        string barSlot;
        SlotVM BarSlot() => barSlot == null || Session == null ? null : Session.Slots.FirstOrDefault(s => s.Id == barSlot);

        void BindBar()
        {
            var s = BarSlot();
            bool live = s != null && s.Newest != null;
            FilesBarAction.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
            FilesBarAction.Content = !live ? "" : s.IsAddon ? s.InstallLabel : s.IsText ? "Copy" : "Save...";
            FilesBarOpen.Content = live ? "Details" : "Open Files";
            FilesBarSub.Text = live ? s.VersionLine : "";
            FilesBarSub.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
            FilesBarGlyph.Text = live ? s.KindGlyph : "\uE8A5";
            var img = live ? s.CoverImage : null;
            FilesBarCover.Background = img == null ? null : new System.Windows.Media.ImageBrush(img) { Stretch = System.Windows.Media.Stretch.UniformToFill };
            FilesBarCover.Visibility = img == null ? Visibility.Collapsed : Visibility.Visible;
        }

        // the list arrives after the notice: the card fills in when it does
        void FilesBarRebind() { if (FilesBar.Visibility == Visibility.Visible) BindBar(); }

        async void FilesBarAction_Click(object sender, RoutedEventArgs e)
        {
            var s = BarSlot();
            if (s == null) return;
            string err = s.IsAddon ? await FilesPage.InstallAsync(s) : s.IsText ? await FilesPage.CopyAsync(s) : await FilesPage.SaveAsync(s, s.Newest, null, null);
            if (!string.IsNullOrEmpty(err)) FilesBarText.Text = err;
            else if (err == null) { FilesBarText.Text = Session.FilesNoticeText.Length > 0 ? Session.FilesNoticeText : FilesBarText.Text; BindBar(); }
        }

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
        public string FilesBarActionForTest => FilesBarAction.Visibility == Visibility.Visible ? (string)FilesBarAction.Content : null;
        public string FilesBarSubForTest => FilesBarSub.Text;
        public void FilesBarActionClickForTest() => FilesBarAction_Click(null, null);
    }
}
