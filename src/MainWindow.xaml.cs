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
            RailLogo.ToolTip = Brand.Name + " v" + App.Version + "\nClick for About";
            SelfUpdater.CleanupOld();
            SetupTray();
            RefreshRailBadges();

            if (!WowLocator.IsWowRoot(settings.WowRoot))
            {
                settings.WowRoot = WowLocator.Find();
                SettingsStore.Save(settings);
            }

            checkTimer.Interval = TimeSpan.FromMinutes(settings.CheckMinutes);
            checkTimer.Tick += async (s, e) => await CheckNow();
            checkTimer.Start();
            statusTimer.Tick += (s, e) => { UpdateStatusText(); MaybeCfAuto(); RefreshRailBadges(); };
            statusTimer.Start();
            Loaded += async (s, e) => { if (manifest == null) await CheckNow(); };

            if (settings.WindowWidth is double w && w >= MinWidth) Width = w;
            if (settings.WindowHeight is double h && h >= MinHeight) Height = h;
            SizeChanged += (s, e) => { settings.WindowWidth = ActualWidth; settings.WindowHeight = ActualHeight; };
            Activated += (s, e) => { if (TabLodge.IsChecked == true) Session.MarkRead(); };

            Session = new LodgeSession(settings);
            Session.IsShownToUser = () => IsVisible && IsActive && WindowState != WindowState.Minimized && TabLodge.IsChecked == true;
            Session.UnreadChanged += unread => UnreadDot.Visibility = unread ? Visibility.Visible : Visibility.Collapsed;
            PreviewKeyDown += (s, e) =>
            {
                if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control || (Keyboard.Modifiers & (ModifierKeys.Alt | ModifierKeys.Shift)) != 0) return;
                var t = e.Key == Key.D1 || e.Key == Key.NumPad1 ? "addons" : e.Key == Key.D2 || e.Key == Key.NumPad2 ? "lodge" : e.Key == Key.OemComma ? "settings" : null;
                if (t != null) { ShowTab(t); e.Handled = true; }
            };
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
            LodgePage.EditProfileRequested += () => { ShowTab("settings"); SettingsPage.Show("profile"); };
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

        // Addons item of the rail: how many updates are waiting (ours + other addons + the hub itself)
        public void RefreshRailBadges()
        {
            var n = cards.Count(c => c.State == CardState.UpdateAvailable) + (others?.Count(c => c.State == TpState.UpdateAvailable) ?? 0)
                + (manifest != null && SelfUpdater.IsNewer(manifest.Hub) ? 1 : 0);
            UpdateBadge.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
            var hubNew = manifest != null && SelfUpdater.IsNewer(manifest.Hub);
            RailVersion.Inlines.Clear();
            if (hubNew) RailVersion.Inlines.Add(new System.Windows.Documents.Run("● ") { Foreground = (System.Windows.Media.Brush)Application.Current.Resources["Gold"] });
            RailVersion.Inlines.Add("v" + App.Version);
            RailVersion.Opacity = hubNew ? 1 : 0.6;
            RailVersion.ToolTip = hubNew ? $"Update available: v{manifest.Hub.Version} - click to update" : "Version " + App.Version;
            UpdateBadgeText.Text = n > 9 ? "9+" : n.ToString();
            TabAddons.ToolTip = n > 0 ? $"Addons  (Ctrl+1)\n{n} update{(n == 1 ? "" : "s")} available" : "Addons  (Ctrl+1)";
        }

        void RailVersion_Click(object sender, MouseButtonEventArgs e)
        {
            ShowTab("settings"); SettingsPage.Show("about");
            if (manifest != null && SelfUpdater.IsNewer(manifest.Hub)) HubBanner.Visibility = Visibility.Visible;
        }

        public void ShowTab(string tab)
        {
            (tab == "lodge" ? TabLodge : tab == "settings" ? TabSettings : TabAddons).IsChecked = true;
        }

        // ---- for the Settings page
        public string CheckStatus => statusText;
        public string HubUpdateStatus => checking ? "Checking..." : manifest == null ? (lastError ?? "Not checked yet")
            : SelfUpdater.IsNewer(manifest.Hub) ? "Update available: v" + manifest.Hub.Version : "Up to date";
        public Task CheckForUpdates() => CheckAll();
        public void CardArtChanged() { AddonCard.Painted = settings.PaintedArt; foreach (var c in cards) c.RefreshArt(); }
        public void AutoUpdateTurnedOn() => _ = AfterCheck();

        // --selftest <dir>: render the window to PNGs before/after installing the first addon, then quit
        public async Task SelfTest(string dir)
        {
            System.IO.Directory.CreateDirectory(dir);
            Show();
            AddonCard.Painted = settings.PaintedArt;
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
            if (cards.Count >= 3 && others.Count >= 4)
            {
                cards[0].SetPillForTest("Update to 1.23.0", PillKind.Action, "x", true);
                cards[1].SetPillForTest("Install", PillKind.Action, "x", true);
                cards[2].SetPillForTest("Updating...", PillKind.Busy, "x", false);
                others[0].SetPillForTest("Update", PillKind.Action, "x", true);
                others[1].SetPillForTest("Update", PillKind.Action, "x", true); others[1].Message = "Something went wrong: test";
                others[2].SetPillForTest("Choose file", PillKind.Action, "x", true);
                others[3].SetPillForTest("Dev copy", PillKind.Quiet, "x", false);
                RefreshRailBadges();
                UpdateBadge.Visibility = Visibility.Visible; UpdateBadgeText.Text = "4";
                await Task.Delay(400);
                Snapshot(System.IO.Path.Combine(dir, "16-pills.png"));
                ShowTab("addons");
            }
            ShowTab("settings");
            SettingsPage.Show("general");
            await Task.Delay(300);
            Snapshot(System.IO.Path.Combine(dir, "3-settings.png"));
            foreach (var sec in new[] { "addons", "lodge", "voice", "overlay", "about" })
            {
                SettingsPage.Show(sec);
                await Task.Delay(sec == "about" ? 2200 : 300);
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
                // profile against the real server: set avatar/about, expect the echo, the broadcast and a profile:get answer
                if (Session.SupportsProfile)
                {
                    await Session.SaveProfile(new System.Collections.Generic.Dictionary<string, object> { ["avatarId"] = "av.bow", ["accent"] = "teal", ["about"] = "selftest about", ["playTimes"] = "evenings" });
                    for (int i = 0; i < 25 && Session.Me?.AvatarId != "av.bow"; i++) await Task.Delay(200);
                    ProfileData got = null;
                    Action<ProfileData> h = pd => got = pd;
                    Session.ProfileReceived += h;
                    Session.RequestProfile(Session.Me.Name);
                    for (int i = 0; i < 25 && got == null; i++) await Task.Delay(200);
                    Session.ProfileReceived -= h;
                    result += $"\r\nlive profile: avatar={Session.Me?.AvatarId} accent={Session.Me?.Accent} about='{Session.MyProfile?.About}' get={(got != null && got.About == "selftest about" && got.AvatarId == "av.bow")}";
                }
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
            result += "\r\n" + await LodgeFeaturesTest(dir);
            result += "\r\n" + await PresenceTest(dir);
            result += "\r\n" + await ProfileTest(dir);
            result += "\r\n" + await ToastTest(dir);
            result += "\r\n" + await DialogTest(dir);
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "result.txt"), result);
            Quit();
        }

        static string Q(string s) => "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        // Lodge 2.3 features (replies, reactions, unread line + badges, pins, old-server fallback) rendered from synthetic protocol
        // frames - no server needed
        async Task<string> LodgeFeaturesTest(string dir)
        {
            var notes = new System.Collections.Generic.List<string>();
            bool ok = true;
            void Check(string what, bool cond) { notes.Add((cond ? "ok   " : "FAIL ") + what); ok &= cond; }
            try
            {
                Session.Disconnect(); // BaseUrl null: the read marks key on "local|channel"
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                string Msg(string ch, string id, string from, string text, int minsAgo, string extra = "") =>
                    "{\"t\":\"msg\",\"channel\":" + Q(ch) + ",\"id\":" + Q(id) + ",\"at\":" + (now - minsAgo * 60000L) + ",\"from\":" + Q(from) +
                    ",\"fromId\":" + (from == "Elan" ? 1 : from == "Bob" ? 2 : 3) + ",\"text\":" + Q(text) + extra + "}";
                const string thumbs = "ready", swords = "fight", heart = "love";   // 2.4 reaction ids
                const string oThumbs = "\U0001F44D", oSwords = "⚔️", oHeart = "❤️";   // what a 2.3 server sends
                string general(bool legacy) => string.Join(",",
                    Msg("general", "m1", "Bob", "Anyone up for Wailing Caverns tonight?", 190),
                    Msg("general", "m2", "Elan", "I am! Meet at the entrance at 20:00", 188),
                    Msg("general", "m3", "Bob", "Great, I'll bring flasks.", 187),
                    Msg("general", "m4", "Bob", "The new tank is level 18 by the way", 12),
                    Msg("general", "m5", "Tess", "@Elan can you craft some bandages for us?", 9),
                    Msg("general", "m6", "Bob", "Sounds good, see you there", 7,
                        ",\"replyTo\":{\"id\":\"m2\",\"from\":\"Elan\",\"text\":\"I am! Meet at the entrance at 20:00\",\"by\":\"Elan\",\"snippet\":\"I am! Meet at the entrance at 20:00\"}"),
                    Msg("general", "m7", "Tess", "Pulling the first boss at 20:15, be ready", 3,
                        ",\"reactions\":{" + Q(legacy ? oThumbs : thumbs) + ":[\"Elan\",\"Bob\"]," + Q(legacy ? oHeart : heart) + ":[\"Tess\"]," + Q(legacy ? oSwords : swords) + ":[\"Bob\",\"Tess\",\"Elan\"]"
                        + (legacy ? "" : ",\"notready\":[\"Bob\"],\"lol\":[\"Elan\"],\"loot\":[\"Bob\",\"Tess\"],\"wipe\":[\"Bob\"],\"epic\":[\"Tess\"]") + "}"));
                string loot = string.Join(",",
                    Msg("loot", "l1", "Tess", "Selling a Linen Bag, 3s", 60),
                    Msg("loot", "l2", "Bob", "LF Healing Potion x5", 20),
                    Msg("loot", "l3", "Tess", "@elan do you still need Copper Bars?", 15),
                    Msg("loot", "l4", "Bob", "Thanks all!", 5));
                string pin(string id, string text, string by) => "{\"id\":" + Q(id) + ",\"text\":" + Q(text) + ",\"by\":" + Q(by) + ",\"at\":" + (now - 190 * 60000L) + ",\"pinnedBy\":\"Bob\",\"pinnedAt\":" + now + "}";
                string welcome(int mode) =>   // 2 = server 2.4 (ids), 1 = server 2.3 (emoji), 0 = older
                    "{\"t\":\"welcome\",\"you\":1,\"name\":\"Elan\",\"role\":\"owner\",\"server\":" + Q(mode == 2 ? "2.4.0" : mode == 1 ? "2.3.0" : "2.2.0") + ",\"maxFileMb\":25,\"canShareFiles\":true,\"canModerate\":true,\"canManage\":true,"
                    + (mode == 2 ? "\"canPin\":true,\"features\":[\"reply\",\"react\",\"react2\",\"pin\"],\"reactions\":[\"ready\",\"notready\",\"lol\",\"love\",\"fight\",\"loot\",\"wipe\",\"epic\"],"
                        : mode == 1 ? "\"canPin\":true,\"features\":[\"reply\",\"react\",\"pin\"],\"reactions\":[\"\U0001F44D\",\"\U0001F602\",\"❤️\",\"✅\",\"❌\",\"⚔️\"]," : "")
                    + "\"channels\":[{\"id\":\"general\",\"name\":\"General\",\"type\":\"text\",\"minRole\":\"guest\"},{\"id\":\"loot\",\"name\":\"Loot & trades\",\"type\":\"text\",\"minRole\":\"guest\"},{\"id\":\"hangout\",\"name\":\"Hangout\",\"type\":\"voice\",\"minRole\":\"guest\"}],"
                    + "\"users\":[{\"id\":1,\"name\":\"Elan\",\"role\":\"owner\"},{\"id\":2,\"name\":\"Bob\",\"role\":\"officer\"},{\"id\":3,\"name\":\"Tess\",\"role\":\"member\"}],"
                    + "\"history\":{\"general\":[" + general(mode == 1) + "],\"loot\":[" + loot + "]},"
                    + (mode >= 1 ? "\"pins\":{\"general\":[" + pin("m2", "I am! Meet at the entrance at 20:00", "Elan") + "," + pin("gone1", "Rules: be kind, no spoilers, loot rolls are need before greed", "Bob") + "]}" : "\"pins\":{}") + "}";

                Session.SetReadMarkForTest("general", "m3", now - 187 * 60000L);
                Session.SetReadMarkForTest("loot", "l1", now - 60 * 60000L);
                TabLodge.IsChecked = true;
                LodgePage.ShowChatForTest();
                Session.FeedForTest(welcome(2));
                var g = Session.TextChannels.First(c => c.Id == "general");
                var l = Session.TextChannels.First(c => c.Id == "loot");
                Check("loot channel: 3 unread, 1 mention", l.UnreadCount == 3 && l.Mentions == 1);
                Check("general: the line sits above the first unread message (m4)", g.Divider?.Id == "m4");
                var gm = Session.MessagesOf("general");
                Check("reply links to m2 and quotes Elan", gm.First(m => m.Id == "m6").ReplyId == "m2" && gm.First(m => m.Id == "m6").ReplyFrom == "Elan");
                var m7 = gm.First(m => m.Id == "m7");
                Check("reactions: 8 pills in set order with icons, my ready highlighted", m7.Reactions.Count == 8 && m7.Reactions[0].Icon != null && m7.Reactions[7].Icon != null && Session.ReactionSet.Length == 8 && m7.Reactions[0].Emoji == thumbs && m7.Reactions[0].Mine && !m7.Reactions[1].Mine);
                Check("pins: 2 in the channel, m2 flagged, the other outlives the history", Session.PinsOf("general").Count == 2 && gm.First(m => m.Id == "m2").Pinned && !gm.Any(m => m.Id == "gone1"));
                Check("pin button visible (server supports pins)", LodgePage.PinButtonVisibleForTest);
                await Task.Delay(900);
                Check("read when the list is at the bottom (nothing left unread in general)", !g.Unread && g.UnreadCount == 0);
                m7.ForceTools = true;
                gm.First(m => m.Id == "m5").Flash = false;
                await Task.Delay(300);
                Snapshot(System.IO.Path.Combine(dir, "10-lodge-features.png"));
                m7.ForceTools = false;
                LodgePage.OpenPinsForTest();
                await Task.Delay(500);
                SnapshotElement(LodgePage.PinBoxForTest, System.IO.Path.Combine(dir, "10-pins.png"));
                LodgePage.ClosePopupsForTest();
                LodgePage.OpenReactForTest(m7);
                await Task.Delay(400);
                SnapshotElement(LodgePage.PickerForTest, System.IO.Path.Combine(dir, "10-picker.png"));
                Check("picker offers the 8 icons", Session.ReactionSet.Length == 8 && Session.ReactionSet.All(i => ReactionArt.Icon(i) != null));
                LodgePage.ClosePopupsForTest();
                LodgePage.OpenMenuForTest(gm.First(m => m.Id == "m6"));
                await Task.Delay(400);
                SnapshotElement(LodgePage.MenuForTest, System.IO.Path.Combine(dir, "10-menu.png"));
                LodgePage.ClosePopupsForTest();
                LodgePage.ShowDropForTest(true);
                await Task.Delay(300);
                Snapshot(System.IO.Path.Combine(dir, "10-drop.png"));
                LodgePage.HideDropForTest();

                // live updates
                Session.FeedForTest("{\"t\":\"react\",\"channel\":\"general\",\"id\":\"m7\",\"emoji\":" + Q(thumbs) + ",\"users\":[\"Bob\"]}");
                Check("react frame: my thumbs-up removed, count 1", !m7.Reactions[0].Mine && m7.Reactions[0].Count == 1);
                Session.FeedForTest("{\"t\":\"react\",\"channel\":\"general\",\"id\":\"m7\",\"emoji\":" + Q(heart) + ",\"users\":[]}");
                Check("react frame: an empty list removes the pill", m7.Reactions.Count == 7);
                Session.FeedForTest("{\"t\":\"react\",\"channel\":\"general\",\"id\":\"m7\",\"reaction\":\"epic\",\"emoji\":\"\U0001F48E\",\"users\":[\"Tess\",\"Elan\"]}");
                Check("react frame: the id wins over the emoji fallback", m7.Reactions.First(r => r.Emoji == "epic").Count == 2 && m7.Reactions.All(r => r.Emoji != "\U0001F48E"));
                Session.FeedForTest("{\"t\":\"unpin\",\"channel\":\"general\",\"id\":\"m2\"}");
                Check("unpin frame", Session.PinsOf("general").Count == 1 && !gm.First(m => m.Id == "m2").Pinned);
                Session.FeedForTest(Msg("loot", "l5", "Bob", "@Elan one more thing", 0).Replace("\"t\":\"msg\"", "\"t\":\"msg\""));
                Check("a live mention in a channel I'm not in adds to its @ badge", l.Mentions == 2 && l.UnreadCount == 4);
                // a message arriving while scrolled up counts as unread even in the open channel
                Session.Select(l);
                await Task.Delay(700);
                Check("opening loot starts at its line; reading at the bottom clears the badges", l.Divider?.Id == "l2" && !l.Unread);
                var shot = System.IO.Path.Combine(dir, "10-loot.png");
                Snapshot(shot);

                // a 2.3 server: reactions arrive as plain emoji, the picker shows the old six as text
                Session.FeedForTest(welcome(1));
                await Task.Delay(400);
                var m7b = Session.MessagesOf("general").First(m => m.Id == "m7");
                Check("2.3 server: emoji pills stay text, picker has six", m7b.Reactions.Any(r => r.Emoji == oThumbs && r.Icon == null) && Session.ReactionSet.Length == 6);
                LodgePage.OpenReactForTest(m7b);
                await Task.Delay(400);
                SnapshotElement(LodgePage.PickerForTest, System.IO.Path.Combine(dir, "10-picker-2.3.png"));
                LodgePage.ClosePopupsForTest();

                // an old server (no "features"): no reaction/pin UI, replies still quote
                Session.FeedForTest(welcome(0));
                await Task.Delay(400);
                Check("old server: pin button hidden, reaction tools hidden", !LodgePage.PinButtonVisibleForTest
                    && Session.MessagesOf("general").All(m => m.ReactToolVisibility == Visibility.Collapsed && m.PinToolVisibility == Visibility.Collapsed));
                Snapshot(System.IO.Path.Combine(dir, "10-oldserver.png"));
                Check("upload errors are friendly (413/429/507)", Services.LodgeClient.UploadError(413, null).Contains("too big")
                    && Services.LodgeClient.UploadError(429, null).Contains("Too many") && Services.LodgeClient.UploadError(507, null).Contains("storage is full"));
            }
            catch (Exception e) { ok = false; notes.Add("crashed: " + e); }
            return "lodge features selftest: " + (ok ? "PASSED" : "FAILED") + "\r\n  " + string.Join("\r\n  ", notes);
        }

        // the themed dialogs (replace MessageBox): a destructive confirm and an error, rendered without blocking
        async Task<string> DialogTest(string dir)
        {
            var notes = new System.Collections.Generic.List<string>();
            bool ok = true;
            void Check(string what, bool cond) { notes.Add((cond ? "ok   " : "FAIL ") + what); ok &= cond; }
            try
            {
                var del = ThemedDialog.Create(DialogKind.Danger, "Delete message?", "Delete Verification A's message? This can't be undone.", "Delete", "Cancel");
                del.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                del.Show();
                await Task.Delay(500);
                Check("destructive confirm: keyboard focus on Cancel", System.Windows.Input.Keyboard.FocusedElement is System.Windows.Controls.Button fb && (string)fb.Content == "Cancel");
                SnapshotElement(del.RootForTest, System.IO.Path.Combine(dir, "11-dialog-confirm.png"));
                del.Close();
                var err = ThemedDialog.Create(DialogKind.Error, "Something went wrong", "Couldn't reach the lodge: the connection was closed.\n\nIt was written to the Outpost's log; the hub keeps running.", "OK", null);
                err.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                err.Show();
                await Task.Delay(500);
                SnapshotElement(err.RootForTest, System.IO.Path.Combine(dir, "11-dialog-error.png"));
                err.Close();
                var info = ThemedDialog.Create(DialogKind.Question, "Roll back?", "Put the previous version of Questie back?\n\nYour settings (WTF folder) are not touched.", "Roll back", "Cancel");
                info.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                info.Show();
                await Task.Delay(400);
                Check("normal confirm: focus on the confirm button", System.Windows.Input.Keyboard.FocusedElement is System.Windows.Controls.Button pb && (string)pb.Content == "Roll back");
                SnapshotElement(info.RootForTest, System.IO.Path.Combine(dir, "11-dialog-question.png"));
                info.Close();
            }
            catch (Exception e) { ok = false; notes.Add("crashed: " + e); }
            return "dialog selftest: " + (ok ? "PASSED" : "FAILED") + "\r\n  " + string.Join("\r\n  ", notes);
        }

        // a popup's content (popups are separate windows, so the window snapshot can't see them)
        void SnapshotElement(FrameworkElement el, string file)
        {
            el.UpdateLayout();
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(el);
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
                Math.Max(1, (int)(el.ActualWidth * dpi.DpiScaleX)), Math.Max(1, (int)(el.ActualHeight * dpi.DpiScaleY)),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY, System.Windows.Media.PixelFormats.Pbgra32);
            bmp.Render(el);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using (var fs = System.IO.File.Create(file)) enc.Save(fs);
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
                // a click on the picture (this threw "not an ancestor" in 2.13.0) and on the backdrop
                ok &= v.OverPictureForTest(new Point(v.HitCenterForTest.X, v.HitCenterForTest.Y));
                ok &= !v.OverPictureForTest(new Point(2, 2));
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

        // started with the splash screen: the window is built and shown fully transparent, the splash hands over (FadeIn) when it
        // fades out. The steps it shows are the real ones; nothing here waits for the splash, and a timer guarantees the window appears.
        bool faded;
        public void StartWithSplash()
        {
            Opacity = 0;
            Show();
            var guard = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4.5) };
            guard.Tick += (s, e) => { guard.Stop(); FadeIn(); };
            guard.Start();
            _ = SplashFlow();
        }

        public void FadeIn()
        {
            if (faded) return;
            faded = true;
            BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(Opacity, 1, TimeSpan.FromSeconds(0.3))
                { FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop });
            Opacity = 1;
        }

        async Task SplashFlow()
        {
            try
            {
                Splash.Step("Checking for updates...");
                for (int i = 0; i < 30 && (checking || (manifest == null && lastError == null)); i++) await Task.Delay(100);
                if (!string.IsNullOrEmpty(settings.LodgeUrl) && !settings.LodgeManualConnect)
                {
                    Splash.Step("Connecting to the Lodge...");
                    for (int i = 0; i < 8 && !Session.Online; i++) await Task.Delay(100);
                }
            }
            catch (Exception e) { Util.Log("splash flow: " + e.Message); }
            finally { Splash.Ready(); }
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
            RefreshRailBadges();
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
            if (!IsVisible) tray.ShowBalloonTip(5000, $"{card.Name} {card.Info.Version}", "An update is ready - click to open the Outpost.", WinForms.ToolTipIcon.None);
        }

        string toastedUpdate;
        void ShowHubBanner()
        {
            var newer = SelfUpdater.IsNewer(manifest.Hub);
            HubBanner.Visibility = newer ? Visibility.Visible : Visibility.Collapsed;
            if (newer) HubBannerText.Text = $"{Brand.Name} {manifest.Hub.Version} is ready.";
            if (newer && toastedUpdate != manifest.Hub.Version)
            {
                toastedUpdate = manifest.Hub.Version;
                Session.Toasts.Push(new ToastItem { Kind = ToastKind.Update, Sender = Brand.Name + " " + manifest.Hub.Version + " is ready", Text = "Open the Outpost to update." });
            }
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
                RefreshRailBadges();
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
            if (!((sender as FrameworkElement)?.DataContext is AddonCard card)) return;
            if (card.PillClickable && card.State != CardState.DevCopy && !card.Busy) await Install(card);
            else card.Expanded = !card.Expanded;   // a quiet pill (Up to date, Dev copy...) just opens the details
        }

        // only for friends who got the addon as a zip of a git checkout
        async void ReplaceDev_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.Tag is AddonCard card) || card.State != CardState.DevCopy) return;
            var ok = Dialog.Confirm(this, "Replace a git copy?",
                $"{card.Name} in your AddOns folder contains a .git folder: it's a developer's working copy, or an old zip of one.\n\n" +
                "If you DEVELOP this addon, keep your copy - replacing it removes your git history and unpublished work from this folder " +
                "(a backup is kept in %LOCALAPPDATA%\\ElansAddonHub\\backups).\n\n" +
                "If a friend sent you this folder, replace it to switch to the normal release version that the Outpost keeps updated.",
                "Replace it", danger: true, cancelText: "Keep my copy");
            if (ok) await Install(card);
        }

        async void HubUpdate_Click(object sender, RoutedEventArgs e)
        {
            HubUpdateButton.IsEnabled = false;
            HubBannerText.Text = "Downloading the new version...";
            try { await SelfUpdater.UpdateAndRestart(manifest.Hub, null); Quit(); }
            catch (Exception ex)
            {
                Util.Log("hub update failed: " + ex);
                HubBannerText.Text = "Update failed: " + ex.Message;
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
                Dialog.Info(this, "WoW folder", "That doesn't look like a World of Warcraft folder. Pick any Wow .exe inside the folder that has _retail_, _classic_beta_ and so on.");
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

        void Logo_Down(object sender, MouseButtonEventArgs e) => e.Handled = true;   // not a drag handle
        void Logo_Click(object sender, MouseButtonEventArgs e) { ShowTab("settings"); SettingsPage.Show("about"); }

        void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        void Close_Click(object sender, RoutedEventArgs e)
        {
            if (settings.RunInBackground)
            {
                Hide();
                if (!trayHintShown)
                {
                    trayHintShown = true;
                    tray.ShowBalloonTip(3000, "Still here", "The Outpost keeps checking for updates from the tray.", WinForms.ToolTipIcon.None);
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
            tray = new WinForms.NotifyIcon { Icon = icon, Text = Brand.Name + " v" + App.Version, Visible = true, ContextMenuStrip = menu };
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
