using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // The one place for settings: General, Addons, Lodge, Voice, Overlay, Notifications, Guild Master, About.
    public partial class SettingsPage : UserControl
    {
        public class RoleOption
        {
            public string Id { get; set; }
            public string Label { get; set; }
            public override string ToString() => Label;
        }
        public static readonly List<RoleOption> Ranks = new[] { "owner", "officer", "veteran", "member" }
            .Select(r => new RoleOption { Id = r, Label = MemberVM.RoleName(r) }).ToList();
        static readonly List<RoleOption> ChannelRanks = new[] { "guest", "member", "veteran", "officer", "owner" }
            .Select(r => new RoleOption { Id = r, Label = r == "guest" ? "Everyone" : MemberVM.RoleName(r) + "+" }).ToList();

        public class MemberRow
        {
            public string Name { get; set; }
            public string Role { get; set; }
            public bool Online { get; set; }
            public List<RoleOption> Roles => Ranks;
            public RoleOption RoleItem => Ranks.FirstOrDefault(r => r.Id == Role) ?? Ranks.Last();
            public Brush RoleBrush { get { var b = new SolidColorBrush(MemberVM.RoleColor(Role)); b.Freeze(); return b; } }
            public Brush DotBrush => Online ? (Brush)Application.Current.Resources["Accent"] : (Brush)Application.Current.Resources["Line"];
            public string OnlineText => Online ? "Online" : "Offline";
        }

        public class ChannelRow
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string Type { get; set; }
            public string Glyph => Type == "voice" ? "" : "#";
            public FontFamily GlyphFont => Type == "voice" ? new FontFamily("Segoe MDL2 Assets") : new FontFamily("Segoe UI");
            public string Info { get; set; }
        }

        MainWindow host;
        Settings settings;
        LodgeView lodge;
        LodgeSession session;
        bool loading;
        int capturingKey;
        readonly DispatcherTimer tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };

        public SettingsPage()
        {
            InitializeComponent();
            tick.Tick += (s, e) => OnTick();
            IsVisibleChanged += (s, e) =>
            {
                if (IsVisible) { tick.Start(); RefreshSection(); }
                else { tick.Stop(); if (lodge != null) lodge.OverlayPreview = false; }
            };
        }

        public void Init(MainWindow host, Settings s, LodgeView lodge)
        {
            this.host = host;
            settings = s;
            this.lodge = lodge;
            session = lodge.Session;
            loading = true;

            BackgroundBox.IsChecked = s.RunInBackground;
            StartupBox.IsChecked = s.StartWithWindows;
            AutoUpdateBox.IsChecked = s.AutoUpdate;
            AutoConnectBox.IsChecked = !s.LodgeManualConnect;
            AutoAwayBox.IsChecked = !s.AutoAwayOff;
            ShareGameBox.IsChecked = !s.ShareGameOff;
            PixelBox.IsChecked = !s.PixelOff;

            ModeVa.IsChecked = !s.VoicePushToTalk;
            ModePtt.IsChecked = s.VoicePushToTalk;
            ThresholdSlider.Value = s.VoiceThreshold ?? -45;
            VolumeSlider.Value = s.VoiceVolume ?? 1;
            SoundsBox.IsChecked = !s.SoundsOff;
            PttKeyButton.Content = VoiceEngine.KeyName(session.PttKey);
            InputBox.ItemsSource = VoiceEngine.InputDevices();
            OutputBox.ItemsSource = VoiceEngine.OutputDevices();
            InputBox.SelectedItem = ((List<Device>)InputBox.ItemsSource).FirstOrDefault(d => d.Id == (s.VoiceInput ?? -1));
            OutputBox.SelectedItem = ((List<Device>)OutputBox.ItemsSource).FirstOrDefault(d => d.Id == (s.VoiceOutput ?? -1));

            OverlayBox.IsChecked = !s.OverlayOff;
            OverlayLeft.IsChecked = !s.OverlayRight;
            OverlayRightBox.IsChecked = s.OverlayRight;
            OverlayTopSlider.Value = s.OverlayTop ?? 0.3;
            OverlayAlwaysBox.IsChecked = s.OverlayAlways;

            var mode = s.NotifyMode ?? "mentions";
            NotifyAll.IsChecked = mode == "all";
            NotifyMentions.IsChecked = mode == "mentions";
            NotifyNone.IsChecked = mode == "none";

            InviteRole.ItemsSource = Ranks;
            InviteRole.SelectedItem = Ranks.First(r => r.Id == "member");
            NewMinRole.ItemsSource = ChannelRanks;
            NewMinRole.SelectedItem = ChannelRanks.First();
            AboutVersion.Text = $"Version {App.Version}";
            loading = false;
            UpdateLabels();

            session.Changed += () => NavGm.Visibility = session.CanManage ? Visibility.Visible : Visibility.Collapsed;
            session.AdminMembers += FillAdmin;
            session.AdminInvited += (name, role, code) =>
            {
                InviteTitle.Text = $"Invite for {name} ({MemberVM.RoleName(role)})";
                InviteLink.Text = LodgeSession.InviteLink(session.BaseUrl, code);
                InviteResult.Visibility = Visibility.Visible;
                InviteName.Text = "";
                GmStatus.Text = "";
            };
            session.Error += text => { if (SecGm.Visibility == Visibility.Visible) GmStatus.Text = text; };
        }

        // ================================================================ navigation

        public void Show(string section)
        {
            var nav = section == "folder" ? NavGeneral : section == "addons" ? NavAddons : section == "lodge" ? NavLodge : section == "voice" ? NavVoice
                    : section == "overlay" ? NavOverlay : section == "gm" ? NavGm : NavGeneral;
            nav.IsChecked = true;
            RefreshSection();
        }

        void Nav_Checked(object sender, RoutedEventArgs e) => RefreshSection();

        void RefreshSection()
        {
            if (settings == null) return;
            var map = new (RadioButton nav, FrameworkElement sec)[]
            {
                (NavGeneral, SecGeneral), (NavAddons, SecAddons), (NavLodge, SecLodge), (NavVoice, SecVoice),
                (NavOverlay, SecOverlay), (NavNotify, SecNotify), (NavGm, SecGm), (NavAbout, SecAbout),
            };
            foreach (var (nav, sec) in map) sec.Visibility = nav.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            Scroll.ScrollToTop();
            lodge.OverlayPreview = NavOverlay.IsChecked == true && IsVisible;

            FolderText.Text = settings.WowRoot ?? "Not found - press Change";
            CheckText.Text = host.CheckStatus;
            LodgeAddress.Text = string.IsNullOrEmpty(settings.LodgeUrl) ? "Not connected to a lodge" : settings.LodgeUrl;
            LodgeWho.Text = session.Me == null ? (session.Connected ? session.StatusText : "Join one from the Lodge tab")
                : $"You're {session.Me.Name}, {MemberVM.RoleName(session.MyRole)} - {session.StatusText}";
            if (NavGm.IsChecked == true) { GmStatus.Text = ""; session.RequestAdmin(); }
            var c = session.Presence.Current;
            ShareGameHint.Text = (session.Presence.Playing ? "WoW is running. " : "")
                + (c != null ? $"Friends see {c.Name}, level {c.Level} {c.Class}{(c.Zone != null ? " in " + c.Zone : "")} " + (session.Presence.Strip.Status == "ok" ? "(live)." : "(updates on /reload and logout).") + ""
                             : "Friends see when you're in WoW. Install Elan's Hub (Addons tab) to also show your character, class and level.");
        }

        void OnTick()
        {
            // mic level under the sensitivity slider
            var v = session?.Voice;
            if (SecVoice.Visibility == Visibility.Visible && v != null)
            {
                var frac = Math.Max(0, Math.Min(1, (v.LevelDb - ThresholdSlider.Minimum) / (ThresholdSlider.Maximum - ThresholdSlider.Minimum)));
                LevelBar.Width = frac * Math.Max(0, ThresholdSlider.ActualWidth);
                LevelHint.Text = "Talk normally: the gold bar should pass the dot when you speak, not when you're quiet.";
            }
            else LevelBar.Width = 0;
            if (capturingKey > 0) CaptureKey();
        }

        // ================================================================ general + addons

        void General_Click(object sender, RoutedEventArgs e)
        {
            if (loading) return;
            settings.RunInBackground = BackgroundBox.IsChecked == true;
            settings.AutoUpdate = AutoUpdateBox.IsChecked == true;
            if (settings.StartWithWindows != (StartupBox.IsChecked == true))
            {
                settings.StartWithWindows = StartupBox.IsChecked == true;
                SettingsStore.ApplyStartWithWindows(settings.StartWithWindows, SelfUpdater.ExePath);
            }
            SettingsStore.Save(settings);
            if (sender == AutoUpdateBox && settings.AutoUpdate) host.AutoUpdateTurnedOn();
        }

        void ChangeFolder_Click(object sender, RoutedEventArgs e) { host.PickWowFolder(); RefreshSection(); }

        async void CheckNow_Click(object sender, RoutedEventArgs e)
        {
            CheckText.Text = "Checking...";
            await host.CheckForUpdates();
            CheckText.Text = host.CheckStatus;
        }

        void Backups_Click(object sender, RoutedEventArgs e) =>
            OpenFolder(Directory.CreateDirectory(Path.Combine(Util.DataDir, "backups")).FullName);

        // ================================================================ lodge

        void EditConnection_Click(object sender, RoutedEventArgs e) { host.ShowTab("lodge"); lodge.EditConnection(); }

        void SignOut_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(Window.GetWindow(this), "Leave this lodge? The hub forgets the address and your code.", "Leave lodge",
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            lodge.SignOut();
            RefreshSection();
        }

        void Lodge_Click(object sender, RoutedEventArgs e)
        {
            if (loading) return;
            settings.LodgeManualConnect = AutoConnectBox.IsChecked != true;
            settings.AutoAwayOff = AutoAwayBox.IsChecked != true;
            var share = settings.ShareGameOff;
            settings.ShareGameOff = ShareGameBox.IsChecked != true;
            var px = settings.PixelOff;
            settings.PixelOff = PixelBox.IsChecked != true;
            SettingsStore.Save(settings);
            if (share != settings.ShareGameOff) session.SendGame();
            if (px != settings.PixelOff) session.Presence.Poll();
        }

        // ================================================================ voice

        void Voice_Changed(object sender, RoutedEventArgs e)
        {
            if (loading || settings == null) return;
            settings.VoicePushToTalk = ModePtt.IsChecked == true;
            settings.SoundsOff = SoundsBox.IsChecked != true;
            SettingsStore.Save(settings);
            session.ApplyVoiceSettings(false);
            UpdateLabels();
        }

        void Threshold_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (loading || settings == null) return;
            settings.VoiceThreshold = Math.Round(e.NewValue);
            SettingsStore.Save(settings);
            session.ApplyVoiceSettings(false);
            UpdateLabels();
        }

        void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (loading || settings == null) return;
            settings.VoiceVolume = Math.Round(e.NewValue, 2);
            SettingsStore.Save(settings);
            session.ApplyVoiceSettings(false);
            UpdateLabels();
        }

        void Device_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (loading || settings == null) return;
            settings.VoiceInput = InputBox.SelectedItem is Device i ? i.Id : -1;
            settings.VoiceOutput = OutputBox.SelectedItem is Device o ? o.Id : -1;
            SettingsStore.Save(settings);
            session.ApplyVoiceSettings(true); // reopen the devices
        }

        void PttKey_Click(object sender, RoutedEventArgs e)
        {
            capturingKey = 1;
            PttKeyButton.Content = "Press a key...";
        }

        void CaptureKey()
        {
            if (capturingKey++ < 4) return; // let the click that started this end first
            if (VoiceEngine.KeyDown(0x1B) || capturingKey > 100) // Esc or ~10 s
            {
                capturingKey = 0;
                PttKeyButton.Content = VoiceEngine.KeyName(session.PttKey);
                return;
            }
            for (int vk = 0x02; vk < 0xFF; vk++)
            {
                if (vk == 0x1B || !VoiceEngine.KeyDown(vk)) continue;
                capturingKey = 0;
                settings.VoicePttKey = vk;
                SettingsStore.Save(settings);
                session.ApplyVoiceSettings(false);
                PttKeyButton.Content = VoiceEngine.KeyName(vk);
                return;
            }
        }

        void UpdateLabels()
        {
            if (settings == null) return;
            VaPanel.Visibility = settings.VoicePushToTalk ? Visibility.Collapsed : Visibility.Visible;
            PttPanel.Visibility = settings.VoicePushToTalk ? Visibility.Visible : Visibility.Collapsed;
            ThresholdText.Text = $"{ThresholdSlider.Value:0} dB";
            VolumeText.Text = $"{VolumeSlider.Value * 100:0}%";
        }

        // ================================================================ overlay + notifications

        void Overlay_Click(object sender, RoutedEventArgs e)
        {
            if (loading || settings == null) return;
            settings.OverlayOff = OverlayBox.IsChecked != true;
            settings.OverlayRight = OverlayRightBox.IsChecked == true;
            settings.OverlayAlways = OverlayAlwaysBox.IsChecked == true;
            SettingsStore.Save(settings);
        }

        void OverlayTop_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (loading || settings == null) return;
            settings.OverlayTop = Math.Round(e.NewValue, 3);
            SettingsStore.Save(settings);
        }

        void Notify_Changed(object sender, RoutedEventArgs e)
        {
            if (loading || settings == null) return;
            settings.NotifyMode = NotifyAll.IsChecked == true ? "all" : NotifyNone.IsChecked == true ? "none" : "mentions";
            SettingsStore.Save(settings);
        }

        // ================================================================ guild master

        void FillAdmin(List<Dictionary<string, object>> members, List<Dictionary<string, object>> channels)
        {
            loading = true;
            MemberRows.ItemsSource = members.Select(m => new MemberRow { Name = m.Str("name"), Role = m.Str("role"), Online = m.Bool("online") })
                .OrderByDescending(m => MemberVM.RoleLevel(m.Role)).ThenBy(m => m.Name).ToList();
            ChannelRows.ItemsSource = channels.Select(c =>
            {
                var type = c.Str("type");
                var min = c.Str("minRole") ?? "guest";
                var info = (min == "guest" ? "everyone" : MemberVM.RoleName(min) + "+") + (type == "voice" && c.Int("max") > 0 ? $" · max {c.Int("max")}" : "");
                return new ChannelRow { Id = c.Str("id"), Name = c.Str("name"), Type = type, Info = info };
            }).OrderBy(c => c.Type == "voice").ToList();
            loading = false;
        }

        void Invite_Click(object sender, RoutedEventArgs e)
        {
            var name = InviteName.Text.Trim();
            if (name.Length == 0) { GmStatus.Text = "Type their name first"; return; }
            session.Invite(name, (InviteRole.SelectedItem as RoleOption)?.Id ?? "member");
        }

        void CopyInvite_Click(object sender, RoutedEventArgs e)
        {
            try { Clipboard.SetText(InviteLink.Text); InviteTitle.Text = "Copied - now send it to them"; } catch { }
        }

        void MemberRole_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (loading || !(sender is ComboBox cb) || !(cb.SelectedItem is RoleOption r) || !(cb.Tag is string name)) return;
            session.SetRole(name, r.Id);
        }

        void RemoveMember_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.Tag is string name)) return;
            if (MessageBox.Show(Window.GetWindow(this), $"Remove {name}? Their invite code stops working right away.", "Remove member",
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
                session.RemoveMember(name);
        }

        void AddChannel_Click(object sender, RoutedEventArgs e)
        {
            var name = NewChannelName.Text.Trim();
            if (name.Length == 0) { GmStatus.Text = "Give the channel a name"; return; }
            int.TryParse(NewMax.Text.Trim(), out var max);
            session.AddChannel(name, NewVoice.IsChecked == true ? "voice" : "text", (NewMinRole.SelectedItem as RoleOption)?.Id ?? "guest", max);
            NewChannelName.Text = "";
            NewMax.Text = "";
        }

        void RemoveChannel_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.Tag is string id)) return;
            if (MessageBox.Show(Window.GetWindow(this), "Remove this channel? Its history is kept on the server, but nobody sees it anymore.",
                    "Remove channel", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
                session.RemoveChannel(id);
        }

        // ================================================================ about

        void LogFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(Util.DataDir);
        void GitHub_Click(object sender, RoutedEventArgs e) =>
            Process.Start(new ProcessStartInfo("https://github.com/Elanoran/ElanAddonHub") { UseShellExecute = true });

        static void OpenFolder(string path)
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); } catch { }
        }

        // test hook
        public void InviteForTest(string name, string role) => session.Invite(name, role);
    }
}
