using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ElansAddonHub.Services;
using WinForms = System.Windows.Forms;

namespace ElansAddonHub
{
    public partial class MainWindow : Window
    {
        readonly Settings settings = SettingsStore.Load();
        readonly ObservableCollection<AddonCard> cards = new ObservableCollection<AddonCard>();
        readonly DispatcherTimer checkTimer = new DispatcherTimer();
        readonly DispatcherTimer statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        Manifest manifest;
        DateTime? lastCheck;
        string lastError;
        bool checking, quitting, trayHintShown;
        WinForms.NotifyIcon tray;

        public MainWindow()
        {
            InitializeComponent();
            Cards.ItemsSource = cards;
            VersionText.Text = "v" + App.Version;
            AutoUpdateBox.IsChecked = settings.AutoUpdate;
            BackgroundBox.IsChecked = settings.RunInBackground;
            StartupBox.IsChecked = settings.StartWithWindows;
            SelfUpdater.CleanupOld();
            SetupTray();

            if (!WowLocator.IsWowRoot(settings.WowRoot))
            {
                settings.WowRoot = WowLocator.Find();
                SettingsStore.Save(settings);
            }
            UpdateFolderText();

            checkTimer.Interval = TimeSpan.FromMinutes(settings.CheckMinutes);
            checkTimer.Tick += async (s, e) => await CheckNow();
            checkTimer.Start();
            statusTimer.Tick += (s, e) => UpdateStatusText();
            statusTimer.Start();
            Loaded += async (s, e) => { if (manifest == null) await CheckNow(); };
        }

        // --selftest <dir>: render the window to PNGs before/after installing the first addon, then quit
        public async Task SelfTest(string dir)
        {
            System.IO.Directory.CreateDirectory(dir);
            Show();
            await CheckNow();
            await Task.Delay(400);
            Snapshot(System.IO.Path.Combine(dir, "1-before.png"));
            var card = cards.FirstOrDefault(c => c.ButtonEnabled);
            if (card != null) await Install(card);
            await Task.Delay(400);
            Snapshot(System.IO.Path.Combine(dir, "2-after.png"));
            SettingsPanel.Visibility = Visibility.Visible;
            await Task.Delay(300);
            Snapshot(System.IO.Path.Combine(dir, "3-settings.png"));
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "result.txt"),
                string.Join("\r\n", cards.Select(c => $"{c.Info.Id}: {c.State} installed={c.Installed} msg={c.Message}")) + "\r\nstatus=" + StatusText.Text);
            Quit();
        }

        void Snapshot(string file)
        {
            UpdateLayout();
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)(ActualWidth * dpi.DpiScaleX), (int)(ActualHeight * dpi.DpiScaleY),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY, System.Windows.Media.PixelFormats.Pbgra32);
            bmp.Render(this);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using (var fs = System.IO.File.Create(file)) enc.Save(fs);
        }

        // started with --tray (Windows startup): no window, just the tray icon and checks
        public void StartHidden()
        {
            _ = CheckNow();
        }

        public void ShowFromTray()
        {
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            Topmost = true; Topmost = false; // bring to front
            foreach (var c in cards) c.Update(c.Info, settings.WowRoot); // versions may have changed meanwhile
        }

        // ------------------------------------------------------------------ checking

        async Task CheckNow()
        {
            if (checking) return;
            checking = true;
            StatusText.Text = "Checking for updates...";
            try
            {
                manifest = Util.FromJson<Manifest>(await Net.GetText(settings.ManifestUrl));
                lastError = null;
                lastCheck = DateTime.Now;
                RebuildCards();
                ShowHubBanner();
                await AfterCheck();
            }
            catch (Exception e)
            {
                lastError = "Couldn't reach the update server";
                Util.Log("check failed: " + e.Message);
            }
            finally
            {
                checking = false;
                UpdateStatusText();
            }
        }

        void RebuildCards()
        {
            foreach (var a in manifest.Addons ?? Enumerable.Empty<AddonInfo>())
            {
                var card = cards.FirstOrDefault(c => c.Info.Id == a.Id);
                if (card == null) { card = new AddonCard(a); cards.Add(card); }
                if (!card.Busy) card.Update(a, settings.WowRoot);
            }
            foreach (var gone in cards.Where(c => manifest.Addons == null || manifest.Addons.All(a => a.Id != c.Info.Id)).ToList())
                cards.Remove(gone);
        }

        async Task AfterCheck()
        {
            foreach (var card in cards.Where(c => c.State == CardState.UpdateAvailable).ToList())
            {
                if (settings.AutoUpdate) await Install(card);
                else NotifyOnce(card);
            }
        }

        void NotifyOnce(AddonCard card)
        {
            var key = card.Info.Id + "@" + card.Info.Version;
            if (settings.Notified.Contains(key)) return;
            settings.Notified.Add(key);
            SettingsStore.Save(settings);
            if (!IsVisible) tray.ShowBalloonTip(5000, $"{card.Name} {card.Info.Version}", "An update is ready - click to open the hub.", WinForms.ToolTipIcon.None);
        }

        void ShowHubBanner()
        {
            var newer = SelfUpdater.IsNewer(manifest.Hub);
            HubBanner.Visibility = newer ? Visibility.Visible : Visibility.Collapsed;
            if (newer) HubBannerText.Text = $"Hub {manifest.Hub.Version} is ready.";
        }

        void UpdateStatusText()
        {
            if (checking) return;
            if (lastError != null) { StatusText.Text = lastError; return; }
            if (lastCheck == null) { StatusText.Text = ""; return; }
            var mins = (int)(DateTime.Now - lastCheck.Value).TotalMinutes;
            StatusText.Text = mins < 1 ? "Checked just now" : $"Checked {mins} min ago";
        }

        void UpdateFolderText()
        {
            FolderText.Text = settings.WowRoot ?? "WoW folder not found - press Change";
            FolderText.ToolTip = settings.WowRoot;
        }

        // ------------------------------------------------------------------ installing

        async Task Install(AddonCard card)
        {
            var wasInstalled = card.State != CardState.NotInstalled;
            card.Message = null;
            card.SetBusy(true, wasInstalled ? "Updating..." : "Installing...");
            try
            {
                await Installer.Install(settings.WowRoot, card.Info, new Progress<double>(p => card.Progress = p));
                card.SetBusy(false);
                card.Update(card.Info, settings.WowRoot);
                card.Message = "Done! In game, type /reload (or /rl) to load it.";
                if (!IsVisible)
                    tray.ShowBalloonTip(4000, $"{card.Name} {card.Info.Version} installed", "Type /reload in game to load it.", WinForms.ToolTipIcon.None);
            }
            catch (Exception e)
            {
                Util.Log($"install {card.Info.Id} failed: {e}");
                card.SetBusy(false);
                card.Update(card.Info, settings.WowRoot);
                card.Message = "Something went wrong: " + e.Message;
            }
        }

        async void Action_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is AddonCard card) await Install(card);
        }

        async void HubUpdate_Click(object sender, RoutedEventArgs e)
        {
            HubUpdateButton.IsEnabled = false;
            HubBannerText.Text = "Downloading the new hub...";
            try { await SelfUpdater.UpdateAndRestart(manifest.Hub, null); quitting = true; }
            catch (Exception ex)
            {
                Util.Log("hub update failed: " + ex);
                HubBannerText.Text = "Hub update failed: " + ex.Message;
                HubUpdateButton.IsEnabled = true;
            }
        }

        // ------------------------------------------------------------------ footer & settings

        void ChangeFolder_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Pick any Wow .exe inside your World of Warcraft folder",
                Filter = "World of Warcraft|Wow*.exe;World of Warcraft Launcher.exe|All files|*.*",
                InitialDirectory = settings.WowRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            };
            if (dlg.ShowDialog(this) != true) return;
            var root = WowLocator.RootFrom(dlg.FileName);
            if (root == null)
            {
                StatusText.Text = "That doesn't look like a World of Warcraft folder";
                return;
            }
            settings.WowRoot = root;
            SettingsStore.Save(settings);
            UpdateFolderText();
            foreach (var c in cards) c.Update(c.Info, root);
            _ = AfterCheck();
        }

        async void CheckNow_Click(object sender, RoutedEventArgs e) => await CheckNow();

        void Settings_Click(object sender, RoutedEventArgs e) =>
            SettingsPanel.Visibility = SettingsPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

        void Setting_Click(object sender, RoutedEventArgs e)
        {
            settings.AutoUpdate = AutoUpdateBox.IsChecked == true;
            settings.RunInBackground = BackgroundBox.IsChecked == true;
            if (settings.StartWithWindows != (StartupBox.IsChecked == true))
            {
                settings.StartWithWindows = StartupBox.IsChecked == true;
                SettingsStore.ApplyStartWithWindows(settings.StartWithWindows, SelfUpdater.ExePath);
            }
            SettingsStore.Save(settings);
            if (sender == AutoUpdateBox && settings.AutoUpdate) _ = AfterCheck();
        }

        // ------------------------------------------------------------------ window & tray

        void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) DragMove();
        }

        void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        void Close_Click(object sender, RoutedEventArgs e)
        {
            if (settings.RunInBackground)
            {
                Hide();
                if (!trayHintShown)
                {
                    trayHintShown = true;
                    tray.ShowBalloonTip(3000, "Still here", "The hub keeps checking for updates from the tray.", WinForms.ToolTipIcon.None);
                }
            }
            else Quit();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!quitting) { e.Cancel = true; Close_Click(this, null); return; }
            base.OnClosing(e);
        }

        void SetupTray()
        {
            System.Drawing.Icon icon;
            using (var s = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/hub.ico")).Stream)
                icon = new System.Drawing.Icon(s);
            var menu = new WinForms.ContextMenuStrip();
            menu.Items.Add("Open", null, (s, e) => ShowFromTray());
            menu.Items.Add("Check for updates", null, async (s, e) => await CheckNow());
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("Quit", null, (s, e) => Quit());
            tray = new WinForms.NotifyIcon { Icon = icon, Text = "Elan's Addon Hub", Visible = true, ContextMenuStrip = menu };
            tray.MouseClick += (s, e) => { if (e.Button == WinForms.MouseButtons.Left) ShowFromTray(); };
            tray.BalloonTipClicked += (s, e) => ShowFromTray();
        }

        void Quit()
        {
            quitting = true;
            tray.Visible = false;
            tray.Dispose();
            Application.Current.Shutdown();
        }
    }
}
