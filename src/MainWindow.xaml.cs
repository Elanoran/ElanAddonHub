using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ElansAddonHub.Lodge;
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
        string statusText = "";   // "Checked 3 min ago" - shown in Settings > Addons
        WinForms.NotifyIcon tray;
        public LodgeSession Session { get; private set; }

        public MainWindow()
        {
            InitializeComponent();
            Cards.ItemsSource = cards;
            InitOthers();
            VersionText.Text = "v" + App.Version;
            SelfUpdater.CleanupOld();
            SetupTray();

            if (!WowLocator.IsWowRoot(settings.WowRoot))
            {
                settings.WowRoot = WowLocator.Find();
                SettingsStore.Save(settings);
            }

            checkTimer.Interval = TimeSpan.FromMinutes(settings.CheckMinutes);
            checkTimer.Tick += async (s, e) => await CheckNow();
            checkTimer.Start();
            statusTimer.Tick += (s, e) => { UpdateStatusText(); MaybeCfAuto(); };
            statusTimer.Start();
            Loaded += async (s, e) => { if (manifest == null) await CheckNow(); };

            if (settings.WindowWidth is double w && w >= MinWidth) Width = w;
            if (settings.WindowHeight is double h && h >= MinHeight) Height = h;
            SizeChanged += (s, e) => { settings.WindowWidth = ActualWidth; settings.WindowHeight = ActualHeight; };
            Activated += (s, e) => { if (TabLodge.IsChecked == true) Session.MarkRead(); };

            Session = new LodgeSession(settings);
            Session.IsShownToUser = () => IsVisible && IsActive && WindowState != WindowState.Minimized && TabLodge.IsChecked == true;
            Session.UnreadChanged += unread => UnreadDot.Visibility = unread ? Visibility.Visible : Visibility.Collapsed;
            Session.Notify += (from, text) =>
            {
                // a tray note while you're elsewhere (e.g. in WoW), at most one every 8 s
                if ((IsVisible && IsActive) || (DateTime.Now - lastChatNote).TotalSeconds < 8) return;
                lastChatNote = DateTime.Now;
                tray.ShowBalloonTip(4000, from, text.Length > 120 ? text.Substring(0, 120) + "..." : text, WinForms.ToolTipIcon.None);
            };
            // "Recommended for your Paladin": follow the class you play (hint only, never installs anything)
            Session.Presence.Changed += () => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (AddonCard.ClassHint == Session.Presence.LastClassFile) return;
                AddonCard.ClassHint = Session.Presence.LastClassFile;
                foreach (var c in cards) if (!c.Busy) c.Update(c.Info, settings.WowRoot);
            }));
            LodgePage.OpenSettings += () => { ShowTab("settings"); SettingsPage.Show("voice"); };
            LodgePage.Init(Session);
            SettingsPage.Init(this, settings, LodgePage);
        }

        DateTime lastChatNote;

        void Tab_Checked(object sender, RoutedEventArgs e)
        {
            if (LodgePage == null || SettingsPage == null) return;
            var lodge = TabLodge.IsChecked == true;
            var set = TabSettings.IsChecked == true;
            LodgePage.Visibility = lodge ? Visibility.Visible : Visibility.Collapsed;
            SettingsPage.Visibility = set ? Visibility.Visible : Visibility.Collapsed;
            AddonsPage.Visibility = !lodge && !set ? Visibility.Visible : Visibility.Collapsed;
            RefreshBar.Visibility = !lodge && !set ? Visibility.Visible : Visibility.Collapsed;
            if (lodge) Session.MarkRead();
        }

        public void ShowTab(string tab)
        {
            (tab == "lodge" ? TabLodge : tab == "settings" ? TabSettings : TabAddons).IsChecked = true;
        }

        // ---- for the Settings page
        public string CheckStatus => statusText;
        public Task CheckForUpdates() => CheckAll();
        public void AutoUpdateTurnedOn() => _ = AfterCheck();

        // --selftest <dir>: render the window to PNGs before/after installing the first addon, then quit
        public async Task SelfTest(string dir)
        {
            System.IO.Directory.CreateDirectory(dir);
            Show();
            await CheckNow();
            // the window's own startup check may still be running (slow network): wait for it
            for (int i = 0; i < 100 && (checking || manifest == null); i++) await Task.Delay(200);
            await Task.Delay(400);
            Snapshot(System.IO.Path.Combine(dir, "1-before.png"));
            var card = cards.FirstOrDefault(c => c.ButtonEnabled);
            if (card != null) await Install(card);
            await CheckAll();
            var checkAllLine = $"check-all button: {(CheckAllButton.IsEnabled && CheckResult.Text != "" && CheckChipText.Text != "" ? "ok" : "FAIL")} (result='{CheckResult.Text}', chip='{CheckChipText.Text}', tip={CheckAllButton.ToolTip})";
            await Task.Delay(400);
            Snapshot(System.IO.Path.Combine(dir, "2-after.png"));
            ShowTab("settings");
            SettingsPage.Show("general");
            await Task.Delay(300);
            Snapshot(System.IO.Path.Combine(dir, "3-settings.png"));
            foreach (var sec in new[] { "addons", "lodge", "voice", "overlay" })
            {
                SettingsPage.Show(sec);
                await Task.Delay(300);
                Snapshot(System.IO.Path.Combine(dir, "3-settings-" + sec + ".png"));
            }
            SettingsPage.Show("general");
            var tp = await ThirdPartyTest(dir);
            tp += "\r\n" + await ViewerTest(dir);
            var result = string.Join("\r\n", cards.Select(c => $"{c.Info.Id}: {c.State} installed={c.Installed} msg={c.Message}")) + "\r\nstatus=" + statusText + "\r\n" + checkAllLine + "\r\n" + tp + "\r\n" + StripSelfTest.Run(dir) + Environment.NewLine + await StripSelfTest.Live();

            // Lodge: ELANSHUB_TEST_LODGE="url|code|name" joins, chats, shares a picture and talks (a test tone, not the mic)
            var lodgeTest = Environment.GetEnvironmentVariable("ELANSHUB_TEST_LODGE");
            if (!string.IsNullOrEmpty(lodgeTest))
            {
                var p = lodgeTest.Split('|');
                TabLodge.IsChecked = true;
                await Task.Delay(300);
                Snapshot(System.IO.Path.Combine(dir, "4-join.png"));
                LodgePage.FillJoinForTest(p[0], p[1], p.Length > 2 ? p[2] : "Tester");
                for (int i = 0; i < 50 && !Session.Online; i++) await Task.Delay(200);
                await Task.Delay(700);
                await Session.SendForTest("Hello from the hub! @Bob are we doing Wailing Caverns tonight?");
                var img = System.IO.Path.Combine(dir, "wolf.png");
                using (var s = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/hub.png")).Stream)
                using (var f = System.IO.File.Create(img)) s.CopyTo(f);
                await LodgePage.Share(img);
                Session.SetMyStatus("dungeon", "Wailing Caverns");
                VoiceEngine.TestTone = true;
                Session.JoinRoom(Session.VoiceRooms.FirstOrDefault()?.Id);
                await Task.Delay(2500);
                Snapshot(System.IO.Path.Combine(dir, "5-lodge.png"));
                ShowTab("settings");
                SettingsPage.Show("voice");
                await Task.Delay(400);
                Snapshot(System.IO.Path.Combine(dir, "6-voice-settings.png"));
                if (Session.CanManage)
                {
                    SettingsPage.Show("gm");
                    await Task.Delay(600);
                    SettingsPage.InviteForTest("Testfriend", "member");
                    await Task.Delay(800);
                    Snapshot(System.IO.Path.Combine(dir, "8-guild-master.png"));
                }
                ShowTab("lodge");
                LodgePage.ForceOverlayForTest = true;
                await Task.Delay(800);
                LodgePage.SnapshotOverlay(System.IO.Path.Combine(dir, "7-overlay.png"));
                var all = Session.TextChannels.Sum(c => Session.MessagesOf(c.Id).Count);
                result += $"\r\njoin error='{LodgePage.JoinError}'";
                result += $"\r\nlodge online={Session.Online} role={Session.MyRole} channels={Session.TextChannels.Count}+{Session.VoiceRooms.Count} " +
                          $"members={Session.Members.Count} messages={all} room={Session.MyRoom} frames sent={Session.Voice?.FramesSent}"
                        + $"\r\n{VoiceEngine.CodecSelfTest()}";
            }
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "result.txt"), result);
            Quit();
        }

        // in-app picture viewer on synthetic local PNGs (no lodge connection)
        async Task<string> ViewerTest(string dir)
        {
            try
            {
                var msgs = new System.Collections.Generic.List<MessageVM>();
                for (int i = 0; i < 3; i++)
                {
                    var path = System.IO.Path.Combine(dir, $"viewer-test{i}.png");
                    var dv = new System.Windows.Media.DrawingVisual();
                    using (var dc = dv.RenderOpen())
                    {
                        dc.DrawRectangle(new System.Windows.Media.LinearGradientBrush(
                            System.Windows.Media.Color.FromRgb((byte)(40 + i * 80), 120, 220), System.Windows.Media.Color.FromRgb(240, (byte)(60 + i * 70), 90), 35), null, new Rect(0, 0, 1600, 900));
                        dc.DrawEllipse(System.Windows.Media.Brushes.White, null, new Point(800, 450), 220, 220);
                        dc.DrawText(new System.Windows.Media.FormattedText("Test picture " + (i + 1), System.Globalization.CultureInfo.InvariantCulture,
                            FlowDirection.LeftToRight, new System.Windows.Media.Typeface("Segoe UI"), 90, System.Windows.Media.Brushes.Black, 1.0), new Point(560, 400));
                    }
                    var rt = new System.Windows.Media.Imaging.RenderTargetBitmap(1600, 900, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    rt.Render(dv);
                    var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rt));
                    using (var fs = System.IO.File.Create(path)) enc.Save(fs);
                    msgs.Add(new MessageVM
                    {
                        Id = "t" + i, From = "Bob", At = DateTime.UtcNow,
                        File = new FileVM { Id = "t" + i, Name = $"viewer-test{i}.png", Size = new System.IO.FileInfo(path).Length, Mime = "image/png", LocalPath = path }
                    });
                }
                TabLodge.IsChecked = true;
                await Task.Delay(300);
                var v = LodgePage.ViewerForTest;
                v.Open(msgs, msgs[0]);
                await Task.Delay(700);
                bool ok = v.IsOpen && v.ErrorForTest == null;
                Snapshot(System.IO.Path.Combine(dir, "9-viewer.png"));
                v.Step(1); await Task.Delay(400);
                ok &= v.IndexForTest == 1;
                v.ZoomForTest = 4; await Task.Delay(200);
                ok &= Math.Abs(v.ZoomForTest - 4) < 0.01;
                Snapshot(System.IO.Path.Combine(dir, "9-viewer-zoom.png"));
                var cap = v.CaptionForTest;
                v.Close();
                ok &= !v.IsOpen;
                return $"viewer selftest: {(ok ? "PASSED" : "FAILED")} (caption='{cap}')";
            }
            catch (Exception e) { return "viewer selftest crashed: " + e; }
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

        // --test-selfupdate: check, then update to the manifest's hub version like the banner button (for testing)
        public async Task TestSelfUpdate()
        {
            await CheckNow();
            for (int i = 0; i < 100 && (checking || manifest == null); i++) await Task.Delay(200);
            if (SelfUpdater.IsNewer(manifest?.Hub)) HubUpdate_Click(this, null);
            else Quit();
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

        // One click checks everything: the hub itself + our addons (manifest), linked third-party addons (forced past the
        // disk cache, within GitHub's rate-limit guard) and CurseForge's local file (CurseForge itself is only asked when
        // the user's "let CurseForge check" setting allows it; otherwise the file is just re-read).
        async void CheckAll_Click(object sender, RoutedEventArgs e) => await CheckAll();

        bool checkAllRunning;
        System.Windows.Threading.DispatcherTimer resultFade;
        public async Task CheckAll()
        {
            if (checkAllRunning) return;
            checkAllRunning = true;
            CheckAllButton.IsEnabled = false;
            CheckResult.Text = "";
            var spin = new System.Windows.Media.Animation.DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever };
            SpinRot.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, spin);
            AddonSources.Force = true;
            try
            {
                await CheckNow();
                for (int i = 0; i < 150 && (checking || scanning || checkingOthers); i++) await Task.Delay(200);
                if (manifest != null) { await RefreshOthers(); await CheckOthers(); }
                if (settings.CfAutoCheck && cfInst != null && !CurseForgeLocal.IsRunning()) await RunCfCheck(); else await RefreshOthers();
            }
            catch (Exception e) { Util.Log("check all failed: " + e.Message); }
            finally
            {
                AddonSources.Force = false;
                SpinRot.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, null);
                CheckAllButton.IsEnabled = true;
                checkAllRunning = false;
                var n = cards.Count(c => c.State == CardState.UpdateAvailable) + others.Count(c => c.State == TpState.UpdateAvailable)
                    + (SelfUpdater.IsNewer(manifest?.Hub) ? 1 : 0);
                CheckResult.Text = lastError != null ? lastError : n == 0 ? "All up to date" : n + (n == 1 ? " update" : " updates");
                CheckResult.Foreground = (System.Windows.Media.Brush)Application.Current.Resources[lastError != null ? "Danger" : n == 0 ? "Accent" : "Gold"];
                UpdateStatusText();
                if (resultFade != null) resultFade.Stop();
                if (lastError == null)
                {
                    resultFade = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
                    resultFade.Tick += (s, ev) => { resultFade.Stop(); CheckResult.Text = ""; };
                    resultFade.Start();
                }
            }
        }

        async Task CheckNow()
        {
            if (checking) return;
            checking = true;
            statusText = "Checking for updates...";
            try
            {
                manifest = Util.FromJson<Manifest>(await Net.GetText(settings.ManifestUrl));
                lastError = null;
                lastCheck = DateTime.Now;
                RebuildCards();
                _ = RefreshOthersAndCheck();
                _ = LoadStats(); // GitHub stats arrive later, never block the check
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
                if (manifest == null) _ = RefreshOthersAndCheck();
                UpdateStatusText();
            }
        }

        async Task RefreshOthersAndCheck() { await RefreshOthers(); await CheckOthers(); _ = SuggestOthers(); }

        async Task LoadStats()
        {
            var stats = await GitHubStats.Load(cards.Select(c => c.Info.Id).ToList());
            foreach (var c in cards) c.SetStats(stats.TryGetValue(c.Info.Id, out var st) ? st : null);
        }

        void Card_Toggle(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is AddonCard card) card.Expanded = !card.Expanded;
        }

        void OpenLink_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is string url && url.StartsWith("https://"))
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }

        void RebuildCards()
        {
            AddonCard.ClassHint = Session?.Presence?.LastClassFile ?? AddonCard.ClassHint;
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
            if (settings.AutoUpdate)
                foreach (var card in cards.Where(c => c.State == CardState.NotInstalled && c.Info.Required).ToList())
                    await Install(card);
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
            if (lastError != null) { statusText = lastError; return; }
            if (lastCheck == null) { statusText = ""; return; }
            var mins = (int)(DateTime.Now - lastCheck.Value).TotalMinutes;
            statusText = mins < 1 ? "Checked just now" : $"Checked {mins} min ago";
            CheckChipText.Text = mins < 1 ? "now" : mins < 60 ? mins + "m" : mins < 1440 ? mins / 60 + "h" : mins / 1440 + "d";
            CheckAllButton.ToolTip = "Check for updates\n" + statusText;
            CheckChip.ToolTip = statusText;
        }

        // ------------------------------------------------------------------ installing

        async Task Install(AddonCard card)
        {
            var replaceDev = card.State == CardState.DevCopy;
            var wasInstalled = card.State != CardState.NotInstalled;
            card.Message = null;
            card.SetBusy(true, wasInstalled ? "Updating..." : "Installing...");
            try
            {
                await Installer.Install(settings.WowRoot, card.Info, new Progress<double>(p => card.Progress = p), replaceDev);
                card.SetBusy(false);
                card.Update(card.Info, settings.WowRoot);
                // WoW only discovers new addon folders when it starts; updates to known addons just need /reload
                var hint = !wasInstalled && GamePresence.WowRunningNow()
                    ? "Installed! WoW is running - restart WoW to load a new addon (/reload isn't enough)."
                    : HasHubAddon() ? "Done! In game, type /reload (or /rl) to load it."
                    : "Done! In game, type /reload to load it.";
                card.Message = hint;
                if (!IsVisible)
                    tray.ShowBalloonTip(4000, $"{card.Name} {card.Info.Version} installed", hint, WinForms.ToolTipIcon.None);
            }
            catch (Exception e)
            {
                Util.Log($"install {card.Info.Id} failed: {e}");
                card.SetBusy(false);
                card.Update(card.Info, settings.WowRoot);
                card.Message = "Something went wrong: " + e.Message;
            }
        }

        // /rl comes from the Elan's Hub companion addon, so only mention it when that's installed
        bool HasHubAddon() => cards.Any(c => c.Info.Id == "ElansHub" && c.State != CardState.NotInstalled
            && c.State != CardState.NoClient && c.State != CardState.NoFolder);

        async void Action_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is AddonCard card && card.State != CardState.DevCopy) await Install(card);
        }

        // only for friends who got the addon as a zip of a git checkout
        async void ReplaceDev_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.Tag is AddonCard card) || card.State != CardState.DevCopy) return;
            var ok = MessageBox.Show(this,
                $"{card.Name} in your AddOns folder contains a .git folder: it's a developer's working copy, or an old zip of one.\n\n" +
                "If you DEVELOP this addon, click No - replacing it removes your git history and unpublished work from this folder " +
                "(a backup is kept in %LOCALAPPDATA%\\ElansAddonHub\\backups).\n\n" +
                "If a friend sent you this folder, click Yes to switch to the normal release version that the hub keeps updated.",
                "Replace a git copy?", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (ok == MessageBoxResult.Yes) await Install(card);
        }

        async void HubUpdate_Click(object sender, RoutedEventArgs e)
        {
            HubUpdateButton.IsEnabled = false;
            HubBannerText.Text = "Downloading the new hub...";
            try { await SelfUpdater.UpdateAndRestart(manifest.Hub, null); Quit(); }
            catch (Exception ex)
            {
                Util.Log("hub update failed: " + ex);
                HubBannerText.Text = "Hub update failed: " + ex.Message;
                HubUpdateButton.IsEnabled = true;
            }
        }

        // ------------------------------------------------------------------ footer & settings

        public void PickWowFolder()
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
                MessageBox.Show(this, "That doesn't look like a World of Warcraft folder. Pick any Wow .exe inside the folder that has _retail_, _classic_beta_ and so on.",
                    "WoW folder", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            settings.WowRoot = root;
            SettingsStore.Save(settings);
            foreach (var c in cards) c.Update(c.Info, root);
            _ = AfterCheck();
            _ = RefreshOthersAndCheck();
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
            SettingsStore.Save(settings); // window size
            Session.Disconnect();
            tray.Visible = false;
            tray.Dispose();
            Application.Current.Shutdown();
        }
    }
}
