using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // The one place for settings: General, Addons, Lodge, Profile, Voice, Overlay, Notifications, Guild Master, About.
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
            public Brush DotBrush => Online ? (Brush)Application.Current.Resources["Brush.Success"] : (Brush)Application.Current.Resources["Brush.Text.Tertiary"];
            public string OnlineText => Online ? "Online" : "Offline";
            // 2.10 servers: whether a permanent code still exists and how many devices have a token (-1 = the server doesn't say)
            public bool HasCode { get; set; } = true;
            public int Devices { get; set; } = -1;
            public bool CanRetire { get; set; }
            public bool CanPurge { get; set; }
            public Visibility RevertVisibility => CanPurge ? Visibility.Visible : Visibility.Collapsed;
            public string DevicesText => Devices < 0 ? "" : Devices == 1 ? "1 device" : Devices + " devices";
            public Visibility RetireVisibility => CanRetire && HasCode && Devices > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // one row of the invite list (Guild Master)
        public class InviteRowVM
        {
            readonly InviteVM i;
            public InviteRowVM(InviteVM i) { this.i = i; }
            public bool ShowState { get; set; } = true;
            public Visibility StateVisibility => ShowState ? Visibility.Visible : Visibility.Collapsed;
            public string Id => i.Id;
            public string Title => i.Title + " · " + i.RoleText;
            public string Detail => i.Detail;
            public string State => i.State;
            public double RowOpacity => i.RowOpacity;
            public Visibility RevokeVisibility => i.CanRevoke ? Visibility.Visible : Visibility.Collapsed;
            public Brush StateBrush => (Brush)Application.Current.Resources[i.Pending ? "Brush.Accent.Tint" : "Brush.Overlay.Chip"];
            public Brush StateText => (Brush)Application.Current.Resources[i.Pending ? "Brush.Text.Primary" : "Brush.Text.Secondary"];
        }

        // one row of a device list: your own (Settings > Lodge) or everyone's (Guild Master)
        public class DeviceRowVM
        {
            readonly DeviceVM d;
            public DeviceRowVM(DeviceVM d) { this.d = d; }
            public string Id => d.Id;
            public string Title => d.Title;
            public string GmTitle => d.Who + " · " + d.Title;
            public string Detail => d.Detail;
            public Visibility CurrentVisibility => d.Current ? Visibility.Visible : Visibility.Collapsed;
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
            LinkBox.IsChecked = !s.NoLinkHandler;
            RealmStatusBox.IsChecked = !s.RealmStatusOff;
            RealmsBackBox.IsChecked = s.RealmsBackNotice;
            AutoUpdateBox.IsChecked = s.AutoUpdate;
            ChannelTest.IsChecked = UpdateChannel.IsTest(s);
            ChannelStable.IsChecked = !UpdateChannel.IsTest(s);
            CfAutoBox.IsChecked = s.CfAutoCheck;
            PaintedBox.IsChecked = s.PaintedArt;
            SplashBox.IsChecked = !s.NoSplash;
            AutoConnectBox.IsChecked = !s.LodgeManualConnect;
            EventsStripBox.IsChecked = !s.EventsStripOff;
            WowEventsBox.IsChecked = !s.WowEventsOff;
            FlightShareBox.IsChecked = !s.FlightShareOff;
            AtlasShareBox.IsChecked = !s.AtlasShareOff;
            ArmoryShareBox.IsChecked = s.ArmoryShareOn;
            ClassicAutoBox.IsChecked = !s.ClassicFromLodgeOff;
            ItemDataBox.IsChecked = !s.ItemDataOff;
            {
                var det = s.WorldMap3dDetail;
                Detail3dLow.IsChecked = det == "low"; Detail3dHigh.IsChecked = det == "high"; Detail3dMedium.IsChecked = det != "low" && det != "high";
                Exag3dSlider.Value = s.WorldMapExag >= 1 && s.WorldMapExag <= 3 ? s.WorldMapExag : 1.5;
                Exag3dText.Text = Exag3dSlider.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "x";
            }
            UpdateWorldMapLine();
            ElansAddonHub.Services.Db2.ItemData.Changed -= OnItemDataChanged; ElansAddonHub.Services.Db2.ItemData.Changed += OnItemDataChanged;
            ElansAddonHub.Services.SeenData.Changed -= OnItemDataChanged; ElansAddonHub.Services.SeenData.Changed += OnItemDataChanged;
            ElansAddonHub.Services.Maps.MapData.Changed -= OnItemDataChanged; ElansAddonHub.Services.Maps.MapData.Changed += OnItemDataChanged;
            ElansAddonHub.Services.Db2.SpellData.Changed -= OnItemDataChanged; ElansAddonHub.Services.Db2.SpellData.Changed += OnItemDataChanged;
            UpdateItemDataLine();

            ModeVa.IsChecked = !s.VoicePushToTalk;
            ModePtt.IsChecked = s.VoicePushToTalk;
            ThresholdSlider.Value = s.VoiceThreshold ?? -45;
            VolumeSlider.Value = s.VoiceVolume ?? 1;
            SoundsBox.IsChecked = !s.SoundsOff;
            NoiseBox.IsChecked = !s.VoiceNoiseSuppressOff;
            AgcBox.IsChecked = !s.VoiceAutoGainOff;
            QualityStd.IsChecked = !s.VoiceHighQuality;
            QualityHigh.IsChecked = s.VoiceHighQuality;
            PttKeyButton.Content = VoiceEngine.KeyName(session.PttKey);
            InputBox.ItemsSource = VoiceEngine.InputDevices();
            OutputBox.ItemsSource = VoiceEngine.OutputDevices();
            InputBox.SelectedItem = ((List<Device>)InputBox.ItemsSource).FirstOrDefault(d => d.Id == (s.VoiceInput ?? -1));
            OutputBox.SelectedItem = ((List<Device>)OutputBox.ItemsSource).FirstOrDefault(d => d.Id == (s.VoiceOutput ?? -1));
            PresetBox.ItemsSource = VoicePresets.All;
            PresetBox.SelectedItem = VoicePresets.Get(s.VoicePreset);

            OverlayBox.IsChecked = !s.OverlayOff;
            OverlayLeft.IsChecked = !s.OverlayRight;
            OverlayRightBox.IsChecked = s.OverlayRight;
            OverlayTopSlider.Value = s.OverlayTop ?? 0.3;
            OverlayAlwaysBox.IsChecked = s.OverlayAlways;
            ToastsBox.IsChecked = !s.ToastsOff; ToastMentionBox.IsChecked = !s.ToastMentionsOff; ToastReplyBox.IsChecked = !s.ToastRepliesOff;
            ToastReactBox.IsChecked = s.ToastReactions; ToastPinBox.IsChecked = s.ToastPins; ToastUpdateBox.IsChecked = !s.ToastUpdateOff; ToastHealthBox.IsChecked = !s.ToastHealthOff;
            var tc = s.ToastCorner ?? "tr";
            ToastTL.IsChecked = tc == "tl"; ToastTR.IsChecked = tc == "tr"; ToastBL.IsChecked = tc == "bl"; ToastBR.IsChecked = tc == "br";

            var mode = s.NotifyMode ?? "mentions";
            NotifyAll.IsChecked = mode == "all";
            NotifyMentions.IsChecked = mode == "mentions";
            NotifyNone.IsChecked = mode == "none";

            NewMinRole.ItemsSource = ChannelRanks;
            NewMinRole.SelectedItem = ChannelRanks.First();
            AboutVersion.Text = $"Version {App.Version}";
            ChannelHint.Text = ChannelText();
            InitAbout();
            loading = false;
            UpdateLabels();

            GmInvite.Bind(session);          // the same form and list the Lodge sidebar Invite sheet uses
            GmInvites.Bind(session);
            GmInvite.Created += (inv, link) => GmStatus.Text = "";
            session.LodgeInfoChanged += FillLodgeInfo;
            session.LeaveRefused += ShowTransfer;
            session.Left += () => { lodge.SignOut("You left the lodge. Your messages stay, with your name on them."); RefreshSection(); };
            FillLodgeInfo();
            session.Changed += () => { NavGm.Visibility = session.CanManage || session.CanInvite ? Visibility.Visible : Visibility.Collapsed; UpdateInviteMode(); UpdateDevicesCard(); };
            session.AdminMembers += FillAdmin;
            session.AdminPurged += m => GmStatus.Text = m.Int("atlas") > 0 ? $"Took {m.Int("atlas")} Atlas item(s) shared by {m.Str("name")} back out. They stay a member." : $"{m.Str("name")} had nothing in the Atlas.";
            session.DevicesChanged += FillDevices;
            session.Error += text => { if (SecGm.Visibility == Visibility.Visible) GmStatus.Text = text; };
            InitProfile();
            InitArmory();
            InitClassic();
            InitBadges();
        }

        // ================================================================ navigation

        public void Show(string section)
        {
            var nav = section == "folder" ? NavGeneral : section == "addons" ? NavAddons : section == "lodge" ? NavLodge : section == "profile" ? NavProfile : section == "voice" ? NavVoice : section == "privacy" ? NavPrivacy
                    : section == "overlay" ? NavOverlay : section == "notify" ? NavOverlay : section == "gm" ? NavGm : section == "about" ? NavAbout : NavGeneral;
            nav.IsChecked = true;
            RefreshSection();
        }

        // ---- About: the campfire scene, live, only while the page is on screen
        SplashScene aboutScene;
        System.Windows.Threading.DispatcherTimer aboutTimer;
        readonly System.Diagnostics.Stopwatch aboutClock = new System.Diagnostics.Stopwatch();
        bool AboutStill => !SystemParameters.ClientAreaAnimation;

        void InitAbout()
        {
            aboutScene = new SplashScene(Brand.Name);
            AboutHost.Child = aboutScene;
            aboutTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
            aboutTimer.Tick += (s, e) => aboutScene.Update(aboutClock.Elapsed.TotalSeconds);
            SecAbout.IsVisibleChanged += (s, e) => AboutRun();
            Loaded += (s, e) => { var w = Window.GetWindow(this); if (w != null) w.StateChanged += (s2, e2) => AboutRun(); };
            UpdateAbout();
        }

        void AboutRun()
        {
            var w = Window.GetWindow(this);
            var on = SecAbout.IsVisible && IsVisible && (w == null || w.WindowState != WindowState.Minimized) && !AboutStill;
            if (on && !aboutTimer.IsEnabled) { aboutClock.Restart(); UpdateAbout(); aboutTimer.Start(); }
            else if (!on && aboutTimer.IsEnabled) { aboutTimer.Stop(); aboutClock.Stop(); }
            if (AboutStill && SecAbout.IsVisible) { UpdateAbout(); aboutScene.Update(3.0); }
        }

        void UpdateAbout()
        {
            if (aboutScene == null) return;
            var st = host.HubUpdateStatus;
            aboutScene.SetStatus($"Version {App.Version}{(host.IsTestBuild ? " (test build)" : "")}  ·  {st}", AboutStill ? -10 : aboutClock.Elapsed.TotalSeconds, 1);
            if (AboutStill) aboutScene.Update(3.0);
        }

        void Replay_Click(object sender, RoutedEventArgs e)
        {
            if (AboutStill) return;
            aboutClock.Restart(); UpdateAbout();
            if (!aboutTimer.IsEnabled) aboutTimer.Start();
        }

        FrameworkElement lastSection;   // the sub-page on screen: it eases in (Motion.PageIn) when another one is picked

        void Nav_Checked(object sender, RoutedEventArgs e) => RefreshSection();

        void RefreshSection()
        {
            if (settings == null) return;
            var map = new (RadioButton nav, FrameworkElement sec)[]
            {
                (NavGeneral, SecGeneral), (NavAddons, SecAddons), (NavLodge, SecLodge), (NavProfile, SecProfile), (NavPrivacy, SecPrivacy), (NavVoice, SecVoice),
                (NavOverlay, SecOverlay), (NavGm, SecGm), (NavAbout, SecAbout),
            };
            FrameworkElement shown = null;
            foreach (var (nav, sec) in map) { bool on = nav.IsChecked == true; sec.Visibility = on ? Visibility.Visible : Visibility.Collapsed; if (on) shown = sec; }
            if (shown != null && shown != lastSection) { if (lastSection != null && IsLoaded) Motion.PageIn(shown); lastSection = shown; }
            Scroll.ScrollToTop();
            lodge.OverlayPreview = NavOverlay.IsChecked == true && IsVisible;

            FolderText.Text = settings.WowRoot ?? "Not found - press Change";
            UpdateItemDataLine();
            UpdateWorldMapLine();
            CheckText.Text = host.CheckStatus;
            LodgeAddress.Text = string.IsNullOrEmpty(settings.LodgeUrl) ? "Not connected to a lodge" : settings.LodgeUrl;
            LodgeWho.Text = session.Me == null ? (session.Connected ? session.StatusText : "Join one from the Lodge tab")
                : $"You're {session.Me.Name}, {MemberVM.RoleName(session.MyRole)} - {session.StatusText}";
            if (NavProfile.IsChecked == true) ProfileEnter();
            UpdateProfileBar();
            UpdateAbout();
            if (NavGm.IsChecked == true)
            {
                GmStatus.Text = "";
                FillLodgeInfo();
                GmInvite.Reset();
                UpdateInviteMode();
                if (session.CanManage) session.RequestAdmin();
                session.RequestInvites();
                session.RequestAllDevices();
            }
            if (NavLodge.IsChecked == true) { session.RequestMyDevices(); UpdateDevicesCard(); FillArmory(); FillClassicSettings(); }
            FillBadgesSettings();
            SyncPrivacy();
            if (NavAddons.IsChecked == true) UpdateCfStatus();
            var c = session.Presence.Current;
            PvShareGameRow.Tag = (session.Presence.Playing ? "WoW is running. " : "")
                + (c != null ? $"Friends see {c.Name}, level {c.Level} {c.Class}{(c.Zone != null ? " in " + c.Zone : "")} " + (session.Presence.Strip.Status == "ok" ? "(live)." : "(updates on /reload and logout).") + ""
                             : "Friends see when you're in WoW. Install Elan's Hub (Addons tab) to also show your character, class and level.");
        }

        int qualityTick;

        // Connection quality: per speaker, from their jitter buffer (refreshed twice a second while this page is open)
        void UpdateQuality()
        {
            if (++qualityTick % 5 != 0) return;
            var v = session?.Voice;
            if (v == null) { QualityText.Text = "Join a voice room to see how the connection to each speaker is doing."; return; }
            var st = v.Stats();
            if (st.Count == 0) { QualityText.Text = "Nobody is talking right now. Jitter, buffer delay and concealed frames show up here while someone speaks."; return; }
            var sb = new System.Text.StringBuilder();
            foreach (var s in st)
            {
                var name = session.Members.FirstOrDefault(m => m.Id == s.Id)?.Name ?? "Someone";
                sb.AppendLine($"{name}: jitter {s.JitterMs:0} ms, delay {s.BufferMs:0} ms (aims for {s.TargetMs:0}), {s.Concealed} frame{(s.Concealed == 1 ? "" : "s")} concealed, {s.Underruns} gap{(s.Underruns == 1 ? "" : "s")}");
            }
            sb.Append($"Your microphone: {v.ProcessMs:0.0} ms of processing per 20 ms.");
            QualityText.Text = sb.ToString();
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
            if (SecVoice.Visibility == Visibility.Visible) UpdateQuality();
            if (capturingKey > 0) CaptureKey();
        }

        // ================================================================ general + addons

        void General_Click(object sender, RoutedEventArgs e)
        {
            if (loading) return;
            settings.RunInBackground = BackgroundBox.IsChecked == true;
            settings.AutoUpdate = AutoUpdateBox.IsChecked == true;
            settings.CfAutoCheck = CfAutoBox.IsChecked == true;
            settings.NoSplash = SplashBox.IsChecked != true;
            bool realmsChanged = settings.RealmStatusOff != (RealmStatusBox.IsChecked != true);
            settings.RealmStatusOff = RealmStatusBox.IsChecked != true; settings.RealmsBackNotice = RealmsBackBox.IsChecked == true;
            bool itemDataOff = ItemDataBox.IsChecked != true, itemDataChanged = settings.ItemDataOff != itemDataOff;
            settings.ItemDataOff = itemDataOff;
            bool art = PaintedBox.IsChecked == true;
            bool artChanged = settings.PaintedArt != art;
            settings.PaintedArt = art;
            if (settings.StartWithWindows != (StartupBox.IsChecked == true))
            {
                settings.StartWithWindows = StartupBox.IsChecked == true;
                SettingsStore.ApplyStartWithWindows(settings.StartWithWindows, SelfUpdater.ExePath);
            }
            if (settings.NoLinkHandler == (LinkBox.IsChecked == true))
            {
                settings.NoLinkHandler = LinkBox.IsChecked != true;
                LinkOpen.Apply(!settings.NoLinkHandler, SelfUpdater.ExePath);   // off removes the registration again
            }
            SettingsStore.Save(settings);
            if (artChanged) host.CardArtChanged();
            if (realmsChanged) host.RefreshRealms();
            if (itemDataChanged) { if (itemDataOff) { ElansAddonHub.Services.Db2.ItemData.Disable(); ElansAddonHub.Services.SeenData.Disable(); ElansAddonHub.Services.Maps.MapData.Disable(); } else { ElansAddonHub.Services.Db2.ItemData.Start(); ElansAddonHub.Services.SeenData.Start(); ElansAddonHub.Services.Maps.MapData.Start(); } UpdateItemDataLine(); }
            if (sender == AutoUpdateBox && settings.AutoUpdate) host.AutoUpdateTurnedOn();
        }

        string ChannelText() => UpdateChannel.IsTest(settings)
            ? "Test: you get new Outpost and addon builds before they are released to everyone. They can still have rough edges. Switching back to Stable never downgrades anything."
            : "Stable: only releases that were tested and promoted." + (host.ChannelNote != null ? "\n" + host.ChannelNote : "");

        async void Channel_Changed(object sender, RoutedEventArgs e)
        {
            if (loading) return;
            settings.Channel = ChannelTest.IsChecked == true ? "test" : null;
            SettingsStore.Save(settings);
            UpdateChannel.Invalidate();
            ChannelHint.Text = ChannelText();
            await host.CheckForUpdates();
            ChannelHint.Text = ChannelText();
        }

        void ChangeFolder_Click(object sender, RoutedEventArgs e) { host.PickWowFolder(); ElansAddonHub.Services.Db2.ItemData.Start(); ElansAddonHub.Services.SeenData.Start(); ElansAddonHub.Services.Maps.MapData.Start(); RefreshSection(); }

        // "Item data: read from your WoW installation (N items, M revealed)" under the Game data switch
        // "World map pictures: 2 continents, 212 MB." under the Game data switch, and the button that deletes them (they are made again on the next visit)
        void UpdateWorldMapLine()
        {
            var busy = false;
            try { busy = ElansAddonHub.Services.WorldMap.WorldMapService.StateOf(0)?.Running == true || ElansAddonHub.Services.WorldMap.WorldMapService.StateOf(1)?.Running == true; } catch (Exception) { }
            WorldMapCacheRow.Tag = ElansAddonHub.Services.WorldMap.WorldMapCache.StatusLine() + " Made from your game files, read-only; nothing is uploaded.";
            ClearWorldMapButton.IsEnabled = !busy && ElansAddonHub.Services.WorldMap.WorldMapCache.SizeBytes() > 0;
        }

        // 3D detail (low / medium / high) and height exaggeration of the Atlas world map: read by the 3D page when it starts, so the next time it opens
        void WorldMap3d_Click(object sender, RoutedEventArgs e)
        {
            if (loading || settings == null) return;
            settings.WorldMap3dDetail = Detail3dLow.IsChecked == true ? "low" : Detail3dHigh.IsChecked == true ? "high" : "medium";
            SettingsStore.Save(settings);
        }

        void Exag3d_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (Exag3dText == null) return;
            Exag3dText.Text = e.NewValue.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "x";
            if (loading || settings == null) return;
            settings.WorldMapExag = Math.Round(e.NewValue, 1);
            SettingsStore.Save(settings);
        }

        void ClearWorldMap_Click(object sender, RoutedEventArgs e)
        {
            ElansAddonHub.Services.WorldMap.WorldMapService.CancelAll();
            ElansAddonHub.Services.WorldMap.WorldMapCache.Clear();
            ElansAddonHub.Services.WorldMap.WorldMapService.ResetForTest();
            UpdateWorldMapLine();
        }

        void UpdateItemDataLine() => ItemDataRow.Tag = ElansAddonHub.Services.Db2.ItemData.StatusLine + ElansAddonHub.Services.Maps.MapData.StatusSuffix + ". " + ElansAddonHub.Services.SeenData.StatusLine + ". Read-only, on this PC: nothing is uploaded and nothing is written to the WoW folder.";
        void OnItemDataChanged() => Dispatcher.BeginInvoke(new Action(UpdateItemDataLine));

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

        // ---- Danger zone. Two different actions: "Sign out this PC" forgets this PC only; "Leave the lodge" removes the membership on the server.

        // An owner who has only this one device would lock themselves out of their own lodge: they type the lodge's name to confirm, and the dialog
        // says how to get back in.
        public bool SignOutNeedsTyping => LeavePolicy.SignOutNeedsTypedName(session.MyRole, session.MyDevices.Count);
        public string SignOutPhrase => LeavePolicy.Phrase(session.LodgeName, session.LodgeAddress ?? settings.LodgeUrl);

        void SignOutPc_Click(object sender, RoutedEventArgs e)
        {
            var owner = Window.GetWindow(this);
            if (SignOutNeedsTyping)
            {
                var phrase = SignOutPhrase;
                if (!Dialog.ConfirmTyped(owner, "Sign out the only device of a Guild Master?",
                        "This is the only PC you are signed in on. Nobody else can let you back in, and the lodge keeps your Guild Master place. To get back in, run this on the server:\n\n"
                        + LeavePolicy.RecoverCommand(session.Me?.Name) + "\n\nType " + phrase + " to confirm.", "Sign out this PC", phrase)) return;
            }
            else if (!Dialog.Confirm(owner, "Sign out this PC?", "The Outpost forgets the sign-in on this PC. You and your other devices stay in the lodge; a new invite link brings this PC back.", "Sign out", danger: true)) return;
            lodge.SignOut();
            RefreshSection();
        }

        void LeaveLodge_Click(object sender, RoutedEventArgs e)
        {
            if (!session.SupportsLeave)
            {
                Dialog.Info(Window.GetWindow(this), "This lodge's server is too old", "Leaving a lodge needs server 2.12. Ask the Guild Master to update it (sudo lodge-update), or sign out this PC.");
                return;
            }
            TransferPanel.Visibility = Visibility.Collapsed;
            if (!Dialog.Confirm(Window.GetWindow(this), "Leave the lodge?",
                "You are removed from the lodge for good: your name, all your devices and your open invite links. Your messages stay, with your name on them. To come back you need a new invite.",
                "Leave the lodge", danger: true)) return;
            session.RequestLeave();
        }

        // the server said no: I am the last Guild Master
        void ShowTransfer(string text)
        {
            TransferTitle.Text = string.IsNullOrEmpty(text) ? "Transfer ownership first" : text;
            TransferText.Text = "You are the last Guild Master, and a lodge always needs one. Pick someone to take over; afterwards you can leave, or stay.";
            TransferPanel.Visibility = Visibility.Visible;
            session.RequestAdmin();
            FillTransferPick();
        }

        List<KeyValuePair<string, string>> adminPeople = new List<KeyValuePair<string, string>>();

        // officers first; members only when there is no officer
        void FillTransferPick()
        {
            if (TransferPanel.Visibility != Visibility.Visible) return;
            var me = session.Me?.Name;
            if (adminPeople.Any(p => p.Value == "owner" && !string.Equals(p.Key, me, StringComparison.OrdinalIgnoreCase)))
            {
                TransferTitle.Text = "Ownership transferred";
                TransferText.Text = "Someone else is a Guild Master now. You can leave the lodge, or stay.";
                TransferRow.Visibility = Visibility.Collapsed; TransferEmpty.Visibility = Visibility.Collapsed;
                return;
            }
            var names = LeavePolicy.TransferCandidates(adminPeople, me);
            TransferPick.ItemsSource = names;
            TransferPick.SelectedIndex = names.Count > 0 ? 0 : -1;
            bool any = names.Count > 0;
            TransferRow.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            TransferEmpty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
            if (any && adminPeople.All(p => p.Value != "officer")) TransferText.Text = "You are the last Guild Master, and a lodge always needs one. There is no officer, so you can pick any member. Afterwards you can leave, or stay.";
        }

        void Transfer_Click(object sender, RoutedEventArgs e)
        {
            var name = TransferPick.SelectedItem as string;
            if (name == null) return;
            if (!Dialog.Confirm(Window.GetWindow(this), "Transfer ownership to " + name + "?",
                name + " becomes a Guild Master: they can invite, change ranks and remove people. You stay a Guild Master until you leave or someone changes your rank.", "Transfer ownership")) return;
            session.TransferOwnership(name);
            session.RequestAdmin();
        }

        void LodgeInfoSave_Click(object sender, RoutedEventArgs e)
        {
            var problem = LodgeSession.CheckLodgeInfo(LodgeNameBox.Text, LodgeWelcomeBox.Text);
            if (problem != null) { GmStatus.Text = problem; return; }
            GmStatus.Text = "";
            session.SetLodgeInfo(LodgeNameBox.Text.Trim(), LodgeWelcomeBox.Text.Trim());
        }

        void FillLodgeInfo()
        {
            if (LodgeNameBox == null) return;
            if (!LodgeNameBox.IsKeyboardFocused) LodgeNameBox.Text = session.LodgeName ?? "";
            if (!LodgeWelcomeBox.IsKeyboardFocused) LodgeWelcomeBox.Text = session.LodgeWelcome ?? "";
            LodgeInfoSave.IsEnabled = session.SupportsLodgeInfo;
            LodgeInfoHint.Text = session.SupportsLodgeInfo
                ? "Shown on the invite page and when someone joins. It is public: nobody has to be signed in to read it, so keep secrets out of it."
                : "Your Lodge server is older than 2.12 and can't keep a lodge name yet. Update it (sudo lodge-update).";
        }

        void Lodge_Click(object sender, RoutedEventArgs e)
        {
            if (loading) return;
            settings.LodgeManualConnect = AutoConnectBox.IsChecked != true;
            bool calendarChanged = settings.EventsStripOff != (EventsStripBox.IsChecked != true) || settings.WowEventsOff != (WowEventsBox.IsChecked != true);
            settings.EventsStripOff = EventsStripBox.IsChecked != true;
            settings.WowEventsOff = WowEventsBox.IsChecked != true;
            bool flightChanged = settings.FlightShareOff != (FlightShareBox.IsChecked != true);
            settings.FlightShareOff = FlightShareBox.IsChecked != true;
            bool atlasChanged = settings.AtlasShareOff != (AtlasShareBox.IsChecked != true);
            settings.AtlasShareOff = AtlasShareBox.IsChecked != true;
            SettingsStore.Save(settings);
            if (calendarChanged) session.WowCalendarSettingsChanged();
            if (flightChanged) session.FlightSettingsChanged();
            if (atlasChanged) session.AtlasSettingsChanged();
        }

        void OpenPrivacy_Click(object sender, RoutedEventArgs e) => Show("privacy");

        // ================================================================ CurseForge status (Settings > Addons)

        public CurseForgeLocal.CfStatus CfOverrideForTest;
        public string CfStatusTextForTest => CfStatusText.Text;

        void UpdateCfStatus()
        {
            var st = CfOverrideForTest ?? host.CfProbe();
            CfStatusText.Text = st.Text;
            var res = Application.Current.Resources;
            CfDot.Fill = (Brush)res[st.Kind == CurseForgeLocal.CfStatusKind.Ok ? "Brush.Success" : st.Kind == CurseForgeLocal.CfStatusKind.Unreadable ? "Brush.Danger" : "Brush.Text.Tertiary"];
            CfStatusText.ToolTip = CfRecheck.ToolTip = CurseForgeLocal.StatusTip;
        }

        async void CfRecheck_Click(object sender, RoutedEventArgs e)
        {
            CfStatusText.Text = "Checking...";
            await host.RecheckCurseForge();
            UpdateCfStatus();
        }

        // ================================================================ privacy (the switches are the same settings as on the other pages)

        void SyncPrivacy()
        {
            if (settings == null) return;
            PvShareGame.IsChecked = !settings.ShareGameOff; PvShareZone.IsChecked = !settings.ShareZoneOff; PvShareXp.IsChecked = !settings.ShareXpOff;
            PvAutoStatus.IsChecked = !settings.AutoStatusOff; PvAutoAway.IsChecked = !settings.AutoAwayOff; PvPixel.IsChecked = !settings.PixelOff;
            PvRememberInvis.IsChecked = !settings.InvisibleForget; PvCfAuto.IsChecked = settings.CfAutoCheck; PvVideo.IsChecked = !settings.VideoPreviewsOff;
            PvBrPrivate.IsChecked = !settings.BrowserRemember; PvBrAds.IsChecked = !settings.BrowserAdsOff; PvBrChat.IsChecked = settings.BrowserFromChat;
            if (PvBrSearch.Items.Count == 0) { PvBrSearch.ItemsSource = BrowserUrl.Engines.Select(e => e.Name).ToList(); }
            PvBrSearch.SelectedIndex = Array.IndexOf(BrowserUrl.Engines, BrowserUrl.EngineById(settings.BrowserSearch));
            PvInvisible.IsChecked = session.Invisible;
            PvInvisible.IsEnabled = session.Me != null;
            PrivacyLodgeLine.Text = string.IsNullOrEmpty(settings.LodgeUrl) ? "You are not in a lodge, so nothing from the first group is sent anywhere."
                : (session.Online ? "Connected to " : "Your lodge: ") + settings.LodgeUrl;
        }

        void Privacy_Click(object sender, RoutedEventArgs e)
        {
            if (loading || !(sender is CheckBox cb)) return;
            if (cb == PvInvisible) { session.SetInvisible(cb.IsChecked == true); return; }
            if (cb == PvBrPrivate || cb == PvBrAds || cb == PvBrChat)
            {
                bool profile = cb == PvBrPrivate;
                if (cb == PvBrPrivate) settings.BrowserRemember = cb.IsChecked != true;
                else if (cb == PvBrAds) settings.BrowserAdsOff = cb.IsChecked != true;
                else settings.BrowserFromChat = cb.IsChecked == true;
                SettingsStore.Save(settings);
                host.BrowserSettingsChanged(profile);
                return;
            }
            if (cb == PvVideo)
            {
                settings.VideoPreviewsOff = cb.IsChecked != true;
                SettingsStore.Save(settings);
                session.RefreshVideos();
                return;
            }
            if (cb == PvCfAuto) { CfAutoBox.IsChecked = cb.IsChecked; General_Click(CfAutoBox, e); return; }
            var share = settings.ShareGameOff; var px = settings.PixelOff; var zone = settings.ShareZoneOff; var xp = settings.ShareXpOff;
            bool on = cb.IsChecked == true;
            if (cb == PvShareGame) settings.ShareGameOff = !on;
            else if (cb == PvShareZone) settings.ShareZoneOff = !on;
            else if (cb == PvShareXp) settings.ShareXpOff = !on;
            else if (cb == PvAutoStatus) settings.AutoStatusOff = !on;
            else if (cb == PvAutoAway) settings.AutoAwayOff = !on;
            else if (cb == PvPixel) settings.PixelOff = !on;
            else if (cb == PvRememberInvis) settings.InvisibleForget = !on;
            else return;
            SettingsStore.Save(settings);
            if (share != settings.ShareGameOff || zone != settings.ShareZoneOff || xp != settings.ShareXpOff) session.SendGame();
            if (px != settings.PixelOff) session.Presence.Poll();
        }

        void BrowserSearch_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (loading || settings == null || PvBrSearch.SelectedIndex < 0) return;
            settings.BrowserSearch = BrowserUrl.Engines[PvBrSearch.SelectedIndex].Id;
            SettingsStore.Save(settings);
        }

        void PrivacyProfile_Click(object sender, RoutedEventArgs e) => Show("profile");
        public void ScrollEndForTest() { Scroll.UpdateLayout(); Scroll.ScrollToEnd(); }
        public Task CfRecheckForTest() { CfRecheck_Click(null, null); return Task.Delay(1500); }
        public void WelcomeClickForTest() => Welcome_Click(null, null);
        public bool PrivacyShowsForTest => SecPrivacy.Visibility == Visibility.Visible;
        public void ToggleForTest(string name) { var cb = (CheckBox)FindName(name); cb.IsChecked = cb.IsChecked != true; Privacy_Click(cb, null); }

        void Welcome_Click(object sender, RoutedEventArgs e) => host.ShowWelcomeGuide();

        // ================================================================ voice

        void Voice_Changed(object sender, RoutedEventArgs e)
        {
            if (loading || settings == null) return;
            settings.VoicePushToTalk = ModePtt.IsChecked == true;
            settings.SoundsOff = SoundsBox.IsChecked != true;
            settings.VoiceNoiseSuppressOff = NoiseBox.IsChecked != true;
            settings.VoiceAutoGainOff = AgcBox.IsChecked != true;
            settings.VoiceHighQuality = QualityHigh.IsChecked == true;
            SettingsStore.Save(settings);
            session.ApplyVoiceSettings(false);
            if (preview != null) { preview.NoiseSuppression = !settings.VoiceNoiseSuppressOff; preview.AutoGain = !settings.VoiceAutoGainOff; }
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

        void Preset_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (loading || settings == null || !(PresetBox.SelectedItem is VoicePreset p)) return;
            settings.VoicePreset = p.Id;
            SettingsStore.Save(settings);
            session.ApplyVoiceSettings(false);
            if (preview != null) preview.Preset = p;
        }

        // "Hear myself": a private engine that only plays your own processed mic back (nothing is sent)
        VoiceEngine preview;

        void Monitor_Click(object sender, RoutedEventArgs e)
        {
            StopPreview();
            if (MonitorBox.IsChecked != true) return;
            preview = new VoiceEngine { Monitor = true, Muted = true, Preset = VoicePresets.Get(settings?.VoicePreset), NoiseSuppression = settings?.VoiceNoiseSuppressOff != true, AutoGain = settings?.VoiceAutoGainOff != true };
            preview.Start(settings?.VoiceInput ?? -1, settings?.VoiceOutput ?? -1, 1f);
            if (preview.MicError != null) { StopPreview(); MonitorBox.IsChecked = false; }
            IsVisibleChanged -= StopPreviewOnHide; IsVisibleChanged += StopPreviewOnHide;
        }

        void StopPreviewOnHide(object s, DependencyPropertyChangedEventArgs e) { if (!IsVisible) { StopPreview(); MonitorBox.IsChecked = false; } }

        void StopPreview() { try { preview?.Dispose(); } catch { } preview = null; }

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
            settings.ToastsOff = ToastsBox.IsChecked != true; settings.ToastMentionsOff = ToastMentionBox.IsChecked != true;
            settings.ToastRepliesOff = ToastReplyBox.IsChecked != true; settings.ToastReactions = ToastReactBox.IsChecked == true;
            settings.ToastPins = ToastPinBox.IsChecked == true; settings.ToastUpdateOff = ToastUpdateBox.IsChecked != true; settings.ToastHealthOff = ToastHealthBox.IsChecked != true;
            settings.ToastCorner = ToastTL.IsChecked == true ? "tl" : ToastBL.IsChecked == true ? "bl" : ToastBR.IsChecked == true ? "br" : "tr";
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
            adminPeople = members.Select(m => new KeyValuePair<string, string>(m.Str("name"), m.Str("role"))).ToList();
            FillTransferPick();
            loading = true;
            MemberRows.ItemsSource = members.Select(m => new MemberRow
                {
                    Name = m.Str("name"), Role = m.Str("role"), Online = m.Bool("online"),
                    HasCode = !m.ContainsKey("hasCode") || m.Bool("hasCode"), Devices = m.ContainsKey("devices") ? m.Int("devices") : -1, CanRetire = session.CanRetire, CanPurge = session.CanPurge,
                })
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

        // design screenshots: the Guild Master page with sample members and channels (no server needed)
        public void GmDesignForTest()
        {
            NavGm.Visibility = Visibility.Visible;
            Dictionary<string, object> M(string n, string r, bool on) => new Dictionary<string, object> { ["name"] = n, ["role"] = r, ["online"] = on };
            Dictionary<string, object> C(string n, string type, string min, int max) => new Dictionary<string, object> { ["id"] = n, ["name"] = n, ["type"] = type, ["minRole"] = min, ["max"] = max };
            FillAdmin(new List<Dictionary<string, object>> { M("Elan", "owner", true), M("Bob", "officer", true), M("Dax", "member", false) },
                new List<Dictionary<string, object>> { C("general", "text", "guest", 0), C("officers", "text", "officer", 0), C("Campfire", "voice", "guest", 8) });
            GmInvite.SetResultForDesign("Invite for Cleo (Member) - copied", "elanshub://lodge/join#nasferatu.dk/lodge|i1.XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX",
                "https://nasferatu.dk/lodge/#join=nasferatu.dk/lodge|i1.XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX", "Works once, expires in 7 days. Send it privately (a DM, not a channel). It is shown only now.");
        }

        // ---------------------------------------------------------------- one-time invite links (server 2.10): the form and the list are shared controls (InviteForm, InviteListView)

        // the owner-only part of the page (ranks, channels, lodge name) is for the Guild Master; officers see invites and devices
        void UpdateInviteMode()
        {
            if (GmInvite == null) return;
            GmInvite.UpdateMode();
            GmOwnerOnly.Visibility = session.CanManage ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---------------------------------------------------------------- devices

        void FillDevices()
        {
            var mine = session.MyDevices.Select(d => new DeviceRowVM(d)).ToList();
            MyDeviceRows.ItemsSource = mine;
            var all = session.AllDevices.Select(d => new DeviceRowVM(d)).ToList();
            AllDeviceRows.ItemsSource = all;
            AllDevicesEmpty.Visibility = all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateDevicesCard();
        }

        // what the "Your devices" card says, depending on how this PC signs in
        void UpdateDevicesCard()
        {
            if (MyDevicesEmpty == null) return;
            bool any = session.MyDevices.Count > 0;
            MyDevicesEmpty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
            UpgradeDeviceButton.Visibility = session.UpgradeOffered || (session.SupportsTokens && !session.UsingToken && LodgeCredentials.HasCode(settings)) ? Visibility.Visible : Visibility.Collapsed;
            if (!session.Connected && !session.TestFedForView) MyDevicesEmpty.Text = "Join a lodge to see its devices.";
            else if (!session.SupportsTokens) MyDevicesEmpty.Text = "This lodge's server is older than 2.10 and signs you in with a permanent code. Ask the Guild Master to update it (sudo lodge-update) to use device keys.";
            else if (!session.UsingToken && !any) MyDevicesEmpty.Text = "This PC still signs in with your permanent code. Upgrade it to give this PC its own key.";
            else MyDevicesEmpty.Text = "No devices.";
        }

        void RevokeDevice_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.Tag is string id)) return;
            bool here = id == session.DeviceId;
            if (Dialog.Confirm(Window.GetWindow(this), here ? "Sign out this PC?" : "Sign out this device?",
                here ? "You will be disconnected from the lodge and need a new invite link to come back." : "It is disconnected at once. It can only come back with a new invite link.",
                "Sign out", danger: true))
                session.RevokeDevice(id);
        }

        async void UpgradeDevice_Click(object sender, RoutedEventArgs e)
        {
            UpgradeDeviceButton.IsEnabled = false;
            var problem = await session.UpgradeThisDevice();
            UpgradeDeviceButton.IsEnabled = true;
            if (problem != null) MyDevicesEmpty.Text = problem;
            else session.RequestMyDevices();
        }

        void RetireCode_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.Tag is string name)) return;
            if (Dialog.Confirm(Window.GetWindow(this), $"Retire {name}'s code?",
                $"Their old permanent code stops working for good. Their devices keep working. Only do this when all of {name}'s PCs have joined with an invite or been upgraded.",
                "Retire code", danger: true))
                session.RetireCode(name);
        }

        void RevertAtlas_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.Tag is string name)) return;
            if (Dialog.Confirm(Window.GetWindow(this), $"Revert {name}'s Atlas contributions?",
                $"Everything {name} shared with the lodge's Atlas (kill counts, drops, vendors, quests, finds, gathering spots) is taken out for everybody. {name} stays a member, and the Atlas fills in again from the next shares. This cannot be undone.",
                "Revert", danger: true))
                session.RevertAtlas(name);
        }

        void MemberRole_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (loading || !(sender is ComboBox cb) || !(cb.SelectedItem is RoleOption r) || !(cb.Tag is string name)) return;
            session.SetRole(name, r.Id);
        }

        void RemoveMember_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.Tag is string name)) return;
            if (Dialog.Confirm(Window.GetWindow(this), $"Remove {name}?", "Their invite code stops working right away.", "Remove", danger: true))
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
            if (Dialog.Confirm(Window.GetWindow(this), "Remove channel?", "Its history is kept on the server, but nobody sees it anymore.", "Remove", danger: true))
                session.RemoveChannel(id);
        }

        // ================================================================ about

        void LogFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(Util.DataDir);
        void Link_Navigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch { }
            e.Handled = true;
        }

        void GitHub_Click(object sender, RoutedEventArgs e) =>
            Process.Start(new ProcessStartInfo("https://github.com/Elanoran/ElanAddonHub") { UseShellExecute = true });

        static void OpenFolder(string path)
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); } catch { }
        }

        // test hooks
        public void InviteForTest(string name, string role) => session.Invite(name, role);
        public List<InviteRowVM> InviteRowsForTest => GmInvites.RowsForTest;
        public List<DeviceRowVM> MyDeviceRowsForTest => (MyDeviceRows.ItemsSource as List<DeviceRowVM>) ?? new List<DeviceRowVM>();
        public List<DeviceRowVM> AllDeviceRowsForTest => (AllDeviceRows.ItemsSource as List<DeviceRowVM>) ?? new List<DeviceRowVM>();
        public List<MemberRow> MemberRowsForTest => (MemberRows.ItemsSource as List<MemberRow>) ?? new List<MemberRow>();
        public bool GmOwnerOnlyVisibleForTest => GmOwnerOnly.Visibility == Visibility.Visible;
        public bool NavGmVisibleForTest => NavGm.Visibility == Visibility.Visible;
        public string InviteButtonTextForTest => GmInvite.ButtonTextForTest;
        public string InviteLinkForTest => GmInvite.LinkForTest;
        public InviteForm GmInviteForTest => GmInvite;
        public void SetLinkHandlerForTest(bool on) { LinkBox.IsChecked = on; General_Click(LinkBox, null); }
        public bool LinkBoxForTest => LinkBox.IsChecked == true;
        public bool RealmStatusBoxForTest => RealmStatusBox.IsChecked == true;
        public bool RealmsBackBoxForTest => RealmsBackBox.IsChecked == true;
        public void SetRealmStatusForTest(bool on, bool backNotice) { RealmStatusBox.IsChecked = on; RealmsBackBox.IsChecked = backNotice; General_Click(RealmStatusBox, null); }
        public string InviteClickHintForTest => GmInvite.ClickHintForTest;
        public string InviteTitleForTest => GmInvite.TitleForTest;
        public string InviteNoteForTest => GmInvite.NoteForTest;
        public bool InviteResultVisibleForTest => GmInvite.ResultVisibleForTest;
        public bool AdvancedVisibleForTest => GmInvite.AdvancedVisibleForTest;
        public bool InviteKindVisibleForTest => GmInvite.KindVisibleForTest;
        public string DevicesNoteForTest => MyDevicesEmpty.Text;
        public bool UpgradeDeviceVisibleForTest => UpgradeDeviceButton.Visibility == Visibility.Visible;
        public void SetGuestKindForTest(bool guest) => GmInvite.SetGuestKindForTest(guest);
        public void CreateInviteClickForTest(string name) => GmInvite.CreateClickForTest(name);
        public void RevokeInviteForTest(string id) => session.RevokeInvite(id);
        public ScrollViewer ScrollForShots => Scroll;
        public bool TransferVisibleForTest => TransferPanel.Visibility == Visibility.Visible;
        public string TransferTitleForTest => TransferTitle.Text;
        public List<string> TransferChoicesForTest => (TransferPick.ItemsSource as List<string>) ?? new List<string>();
        public bool TransferRowVisibleForTest => TransferRow.Visibility == Visibility.Visible;
        public bool TransferEmptyVisibleForTest => TransferEmpty.Visibility == Visibility.Visible;
        public string TransferTextForTest => TransferText.Text;
        public void PickTransferForTest(string name) => TransferPick.SelectedItem = name;
        public void TransferClickForTest() { var name = TransferPick.SelectedItem as string; if (name != null) { session.TransferOwnership(name); session.RequestAdmin(); } }
        public string LodgeNameBoxForTest { get => LodgeNameBox.Text; set => LodgeNameBox.Text = value; }
        public string LodgeWelcomeBoxForTest { get => LodgeWelcomeBox.Text; set => LodgeWelcomeBox.Text = value; }
        public void LodgeInfoSaveForTest() => LodgeInfoSave_Click(null, null);
        public string GmStatusForTest => GmStatus.Text;
        public bool LodgeInfoSaveEnabledForTest => LodgeInfoSave.IsEnabled;
        public void FillAdminForTest(List<Dictionary<string, object>> members, List<Dictionary<string, object>> channels) => FillAdmin(members, channels);
        public void ShowTransferForTest(string text) => ShowTransfer(text);
        public bool LeaveLodgeEnabledForTest => LeaveLodgeButton.IsEnabled;
        public string LodgeAddressTextForTest => LodgeAddress.Text;
        public void DangerPanelDesign() { SignOutPcButton.UpdateLayout(); }
        public void SignOutPcClickForTest() => SignOutPc_Click(null, null);
        public void LeaveLodgeClickForTest() => LeaveLodge_Click(null, null);
        public void TransferConfirmClickForTest() => Transfer_Click(null, null);   // through the confirm dialog (Dialog.TestHook answers it)
        public int LodgeNameMaxForTest => LodgeNameBox.MaxLength;
        public int LodgeWelcomeMaxForTest => LodgeWelcomeBox.MaxLength;
        public string TransferActionTextForTest => TransferButton.Content as string;
    }
}
