using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ElansAddonHub.Services;

namespace ElansAddonHub.Lodge
{
    // The Lodge tab: join screen, channel sidebar, chat. All state lives in LodgeSession.
    public partial class LodgeView : UserControl
    {
        public LodgeSession Session { get; private set; }
        readonly DispatcherTimer tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        OverlayWindow overlay;
        bool editingConnection;
        MessageVM replyTo, editing;
        MemberVM popMember;
        bool popLoading;
        DateTime noteUntil;
        ChannelVM shownChannel;
        MessageVM menuMsg, reactTarget;
        readonly DispatcherTimer dropTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        string jumpMode; // "unread" | "present" | null
        readonly System.Collections.Generic.Dictionary<string, string> drafts = new System.Collections.Generic.Dictionary<string, string>();   // unsent text per channel

        public event Action OpenSettings;
        public bool ForceOverlayForTest;
        public bool OverlayPreview; // the Settings page shows it while you place it

        // the transparent margin of a popover card (PopupCard) that holds its shadow; popups subtract it from their offsets
        const double PopupShadow = 24;

        public LodgeView()
        {
            InitializeComponent();
            Messages.ItemsSource = window.Shown;   // the rows of the open channel (LodgeView.ChatList.cs)
            foreach (var p in new[] { SearchPopup, PlusPopup, MsgMenu, ReactPopup, PinPopup, StatusPopup, MemberPopup })
            {
                var pop = p;
                pop.Opened += (s, e) => Motion.DialogIn(pop.Child as FrameworkElement);   // scale 0.96 -> 1 + fade; instant with Windows animations off
            }
            tick.Tick += (s, e) => OnTick();
            dropTimer.Tick += (s, e) => { dropTimer.Stop(); DropOverlay.Visibility = Visibility.Collapsed; };
        }

        public void Init(LodgeSession session)
        {
            Session = session;
            TextList.ItemsSource = session.TextChannels;
            RoomList.ItemsSource = session.VoiceRooms;
            // the grouped online list (In game / Online / Away / Invisible); people in a voice room show under their room
            GroupList.ItemsSource = session.Groups.Groups;

            session.Changed += Refresh;
            InviteSheetHost.Bind(session);
            CreateGuide.LinkAccepted += link => ShowInviteLink(link, true);
            CalendarPage.Init(session);
            FilesPage.Init(session);
            LadderPage.Init(session);
            LadderPage.OpenBadges = () => ShowBadges(session.Me?.Name);
            session.BadgesChanged += OnBadgesChanged;
            session.MemberBadgesLoaded += OnMemberBadges;
            session.FilesChanged += UpdateFilesChrome;
            session.FilesNotice += ShowFilesNotice;
            session.CalendarChanged += UpdateCalendarChrome;
            ChatCard.SizeChanged += (s, e) => { if (e.WidthChanged) UpdateEventsStrip(); };   // how many pills fit follows the width
            session.OlderRequested += OnOlderRequested;
            session.OlderLoaded += OnOlderLoaded;
            session.IsAtBottom = () => AtBottom;
            session.PinsChanged += UpdatePins;
            session.MessageAdded += vm =>
            {
                if (vm.Channel != session.Selected?.Id) return;
                EmptyText.Visibility = Visibility.Collapsed;
                if (vm.Mine || AtBottom) ScrollToEnd();
            };
            session.Stopped += why =>
            {
                // a revoked device: the token is dead, forget it so the next start asks for a new invite instead of retrying forever
                if (session.StoppedSignedOut) { session.Settings.LodgeTokenProtected = null; SettingsStore.Save(session.Settings); }
                ShowJoin(why, session.LastStopWasCandidate);
            };
            session.ProfileReceived += OnProfileReceived;
            session.Error += text => { SetNote(text, true); noteUntil = DateTime.UtcNow.AddSeconds(6); };
            session.Hint += ShowHint;
            session.SendFailed += (p, why) => RestoreText(p.Text);
            session.ToastWillShow = () => !session.Settings.ToastsOff && (OverlayWindow.WowInFront() != IntPtr.Zero || session.Settings.OverlayAlways || ForceToastsForTest);
            tick.Start();

            UrlBox.Text = session.Settings.LodgeUrl ?? "";
            NameBox.Text = session.Settings.LodgeName ?? Environment.UserName;
            DeviceBox.Text = LodgeCredentials.DeviceName(session.Settings);
            if (!string.IsNullOrEmpty(session.Settings.LodgeUrl) && LodgeCredentials.Load(session.Settings, out var credential, out var isToken) && !session.Settings.LodgeManualConnect)
            {
                ShowChat();
                session.Connect(session.Settings.LodgeUrl, credential, isToken);
            }
            Refresh();
        }

        void Refresh()
        {
            var s = Session;
            if (s == null) return;
            StatusDot.Fill = (Brush)FindResource(s.Online ? "Accent" : "TextDim");
            StatusText.Text = s.StatusText;
            try { LodgeTitle.Text = s.BaseUrl != null ? new Uri(s.BaseUrl).Host : "Lodge"; } catch { }
            UpdateOffline();
            InviteSideButton.Visibility = s.CanInvite ? Visibility.Visible : Visibility.Collapsed;   // owners and officers

            UpdateCalendarChrome();
            UpdateFilesChrome();
            UpdateLadderChrome();

            // the channel on screen
            if (s.Selected != shownChannel)
            {
                HideCalendar(); HideFiles(); HideLadder();
                // what I was typing stays with the channel it was for
                if (shownChannel != null && editing == null) drafts[shownChannel.Id] = Composer.Text;
                shownChannel = s.Selected;
                CancelMode();
                Composer.Text = s.Selected != null && drafts.TryGetValue(s.Selected.Id, out var draft) ? draft : "";
                Composer.CaretIndex = Composer.Text.Length;
                window.Attach(s.MessagesOf(s.Selected?.Id), s.Selected?.Divider);
                ChannelTitle.Text = s.Selected?.Name ?? "";
                EmptyText.Text = $"No messages in #{s.Selected?.Name} yet - say hi!";
                ComposerHint.Text = $"Message #{s.Selected?.Name}";
                PinList.ItemsSource = s.PinsOf(s.Selected?.Id);
                // open at the "New messages" line when there is one, otherwise at the newest message
                if (s.Selected?.Divider != null) ScrollToMessage(s.Selected.Divider, false);
                else ScrollToEnd();
                if (IsLoaded && IsVisible && s.Selected != null) Motion.PageIn(MessageScroll);   // a channel switch eases in (fade + 8 px slide)
            }
            PinButton.Visibility = s.SupportsPin ? Visibility.Visible : Visibility.Collapsed;
            SearchButton.Visibility = s.SupportsSearch ? Visibility.Visible : Visibility.Collapsed;   // servers before 2.9 have no search
            UpdateOlderNote();
            UpdatePins();
            if (s.Selected != null)
                EmptyText.Visibility = s.MessagesOf(s.Selected.Id).Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            // until the welcome frame names the channels the history is on its way: skeleton rows instead of blank space
            bool connecting = s.Selected == null;
            MessagesSkeleton.Visibility = SidebarSkeleton.Visibility = connecting ? Visibility.Visible : Visibility.Collapsed;
            TextHeader.Visibility = VoiceHeader.Visibility = HeaderHash.Visibility = MeCard.Visibility = connecting ? Visibility.Collapsed : Visibility.Visible;
            if (connecting) EmptyText.Visibility = Visibility.Collapsed;

            // me
            if (s.Me != null)
            {
                MeAvatar.Content = s.Me;
                MeName.Text = s.Me.Display;
                MeStatus.Text = MemberVM.RoleName(s.MyRole) + " · " +
                    (s.Invisible ? "Invisible" : string.IsNullOrEmpty(s.MyNote) ? MemberVM.StatusLabel(s.MyStatus) : s.MyNote);
            }
            UpdateServerNotice();
            UpdateUpgradeNotice();
            UpdateCairnNotice();
            AttachButton.Visibility = s.CanShareFiles || s.SupportsLfg ? Visibility.Visible : Visibility.Collapsed;

            // voice bar
            var inVoice = s.MyRoom != null;
            VoiceBar.Visibility = inVoice ? Visibility.Visible : Visibility.Collapsed;
            if (inVoice)
            {
                VoiceRoomText.Text = s.VoiceRooms.FirstOrDefault(r => r.Id == s.MyRoom)?.Name ?? "";
                if (s.Voice?.MicError != null) VoiceRoomText.Text += " - " + s.Voice.MicError;
                else if (s.Settings.VoicePushToTalk) VoiceRoomText.Text += $" - hold {VoiceEngine.KeyName(s.PttKey)}";
                DeafToggle.IsChecked = s.Voice?.Deafened == true;
                MuteToggle.IsChecked = s.Voice?.Muted == true && s.Voice?.Deafened != true;
                MuteToggle.Content = MuteToggle.IsChecked == true ? "" : "";
            }
        }

        // the note in the channel header: calm Secondary text; Danger only for a real error (a failed send, upload or download)
        void SetNote(string text, bool error = false)
        {
            ChatNote.Text = text;
            ChatNote.ToolTip = string.IsNullOrEmpty(text) ? null : text;   // the note is short on screen: the whole text on hover
            ChatNote.SetResourceReference(TextBlock.ForegroundProperty, error ? "Brush.Danger" : "Brush.Text.Secondary");
        }

        // the one-time display-name tip: an inline hint under the header, dismissed with its X (it is never shown again)
        void ShowHint(string text)
        {
            HintText.Text = text;
            HintBar.Visibility = Visibility.Visible;
            Motion.PageIn(HintBar);
        }
        void HintClose_Click(object sender, RoutedEventArgs e) => HintBar.Visibility = Visibility.Collapsed;
        public bool HintVisibleForTest => HintBar.Visibility == Visibility.Visible;
        public string HintTextForTest => HintText.Text;
        public void CloseHintForTest() => HintClose_Click(null, null);

        void OnTick()
        {
            if (Session == null) return;
            TypingText.Text = Session.TypingText(Session.Selected?.Id);
            UpdateOffline();
            UpdateNextEvent();
            if (ChatNote.Text.Length > 0 && DateTime.UtcNow > noteUntil) ChatNote.Text = "";
            if (ChatPanel.Visibility == Visibility.Visible && (Session.IsShownToUser?.Invoke() ?? true)) Session.MarkRead(); // only acts at the bottom
            UpdateJump();
            // the overlay and toasts sit on top of WoW: nothing to place while it isn't running (unless they are pinned to the desktop / being previewed)
            var st = Session.Settings;
            if (WowWatch.Shared.Running || st.OverlayAlways || OverlayPreview || ForceOverlayForTest || ForceToastsForTest || (overlay != null && overlay.IsVisible) || (toastWin != null && toastWin.IsVisible) || Session.Toasts?.NeedsTick == true)
                UpdateOverlay();
        }

        // while disconnected: a calm banner above the box, and Send is off (the text stays in the box)
        void UpdateOffline()
        {
            if (Session == null) return;
            bool off = !Session.Online && !Session.TestFedForView;
            OfflineBanner.Visibility = off ? Visibility.Visible : Visibility.Collapsed;
            if (off) OfflineText.Text = string.IsNullOrEmpty(Session.StatusText) ? "You are offline - reconnecting..." : "You are offline - " + Session.StatusText.TrimEnd('.');
            SendButton.IsEnabled = !off;
        }

        // a message that did not go out comes back into the box (in front of anything typed since)
        void RestoreText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            Composer.Text = Composer.Text.Length == 0 ? text : text + " " + Composer.Text;
            Composer.CaretIndex = Composer.Text.Length;
        }
        public string ComposerTextForTest { get => Composer.Text; set => Composer.Text = value; }
        public int ComposerMaxLengthForTest => Composer.MaxLength;
        public bool SendEnabledForTest => SendButton.IsEnabled;
        public bool OfflineBannerForTest => OfflineBanner.Visibility == Visibility.Visible;
        public string DraftForTest(string channel) => drafts.TryGetValue(channel, out var d) ? d : null;
        public void SendTextForTest() => SendText();
        public string ChatNoteForTest => ChatNote.Text;

        // "Your Lodge server is on 2.5 - 2.7 is available ..." for the owner / officers of an outdated server
        void UpdateServerNotice()
        {
            var show = Session != null && Session.ShowServerNotice;
            ServerNotice.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (show) { ServerNoticeText.Text = Session.ServerNoticeText; ServerNoticeCopy.Content = "Copy command"; }
        }
        void ServerNoticeClose_Click(object sender, RoutedEventArgs e) => Session.DismissServerNotice();

        // a notice from the Cairn: shown over the other notes (it shares row 0, and wins), the colour says how urgent
        void UpdateCairnNotice()
        {
            var n = Session?.ShownCairnNotice;
            CairnNotice.Visibility = n != null ? Visibility.Visible : Visibility.Collapsed;
            if (n == null) return;
            var key = n.Level == "critical" ? "Brush.Danger" : n.Level == "warning" ? "Brush.Warn" : "Brush.Info";
            var brush = (System.Windows.Media.Brush)FindResource(key);
            CairnNotice.BorderBrush = brush; CairnNoticeIcon.Foreground = brush;
            CairnNoticeIcon.Text = n.Level == "info" ? "\uE946" : "\uE7BA";
            CairnNoticeTitle.Text = (n.Level == "critical" ? "Important - from the Cairn" : n.Level == "warning" ? "Heads up - from the Cairn" : "From the Cairn") + " \u2713";
            CairnNoticeText.Text = n.Text;
            if (ServerNotice.Visibility == Visibility.Visible) ServerNotice.Visibility = Visibility.Collapsed;
            if (UpgradeNotice.Visibility == Visibility.Visible) UpgradeNotice.Visibility = Visibility.Collapsed;
        }
        void CairnNoticeClose_Click(object sender, RoutedEventArgs e) => Session.DismissCairnNotice();
        public bool CairnNoticeVisibleForTest => CairnNotice.Visibility == Visibility.Visible;
        public string CairnNoticeTextForTest => CairnNoticeText.Text;
        public string CairnNoticeTitleForTest => CairnNoticeTitle.Text;
        public void CairnNoticeCloseForTest() => CairnNoticeClose_Click(null, null);

        // "Give this PC its own sign-in": shown once to someone on a permanent code on a 2.10 server; Not now is remembered for this lodge
        void UpdateUpgradeNotice()
        {
            var show = Session != null && Session.UpgradeOffered && !upgrading;
            UpgradeNotice.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        bool upgrading;

        async void UpgradeNow_Click(object sender, RoutedEventArgs e)
        {
            upgrading = true;
            UpgradeNow.IsEnabled = false;
            var problem = await Session.UpgradeThisDevice();
            upgrading = false;
            UpgradeNow.IsEnabled = true;
            if (problem != null) { ChatNote.Text = problem; noteUntil = DateTime.UtcNow.AddSeconds(8); }
            UpdateUpgradeNotice();
        }

        void UpgradeLater_Click(object sender, RoutedEventArgs e) => Session.DeclineUpgrade();
        public bool UpgradeNoticeVisibleForTest => UpgradeNotice.Visibility == Visibility.Visible;
        public string JoinStatusForTest => JoinStatus.Text;
        public bool RestoreVisibleForTest => RestoreSavedButton.Visibility == Visibility.Visible;
        public bool JoinPanelVisibleForTest => JoinPanel.Visibility == Visibility.Visible;
        public string UrlBoxForTest { get => UrlBox.Text; set => UrlBox.Text = value; }
        public string CodeBoxForTest { get => CodeBox.Password; set => CodeBox.Password = value; }
        public void JoinForTest() => TryJoin();
        public void RestoreSavedForTest() => RestoreSaved_Click(null, null);
        public void CancelEditForTest() => CancelEdit_Click(null, null);
        public void DeclineUpgradeForTest() => UpgradeLater_Click(null, null);
        void ServerNoticeCopy_Click(object sender, RoutedEventArgs e)
        {
            try { Clipboard.SetText(Brand.ServerUpdateCommand); ServerNoticeCopy.Content = "Copied"; } catch { }
        }
        public bool ServerNoticeVisibleForTest => ServerNotice.Visibility == Visibility.Visible;
        public string ServerNoticeTextForTest => ServerNoticeText.Text;
        public void ServerNoticeCopyForTest() => ServerNoticeCopy_Click(null, null);
        public void ServerNoticeCloseForTest() => ServerNoticeClose_Click(null, null);

        // at the end of the list, and the newest message has a row
        bool AtBottom => window.Live && MessageScroll.VerticalOffset >= MessageScroll.ScrollableHeight - 60;

        // ---- scrolling, the "jump" button, highlight
        FrameworkElement ContainerOf(MessageVM vm) => Messages.ItemContainerGenerator.ContainerFromItem(vm) as FrameworkElement;

        double OffsetInView(FrameworkElement c)
        {
            try { return c.TransformToAncestor(MessageScroll).Transform(new Point(0, 0)).Y; } catch { return double.NaN; }
        }

        void ScrollToMessage(MessageVM vm, bool flash)
        {
            window.Include(vm);   // a message without a row: the rows move to it
            Dispatcher.BeginInvoke(new Action(() =>
            {
                MessageScroll.UpdateLayout();
                var c = ContainerOf(vm);
                if (c == null) return;
                var y = OffsetInView(c);
                if (!double.IsNaN(y)) MessageScroll.ScrollToVerticalOffset(Math.Max(0, MessageScroll.VerticalOffset + y - 36));
                if (flash) Flash(vm);
            }), DispatcherPriority.Background);
        }

        void Flash(MessageVM vm)
        {
            vm.Flash = true;
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
            t.Tick += (s, e) => { t.Stop(); vm.Flash = false; };
            t.Start();
        }

        void MessageScroll_Changed(object sender, ScrollChangedEventArgs e)
        {
            HoldMessagesInPlace(e);
            KeepReaderInPlace(e);
            UpdateJump();
            MaybeLoadOlder(e);
            MaybeGrowWindow(e);
            NoteReaderPlace();
            QueueTrim();
        }

        void UpdateJump()
        {
            var ch = Session?.Selected;
            if (ch == null) { JumpButton.Visibility = Visibility.Collapsed; return; }
            string mode = null;
            if (ch.Unread && ch.Divider != null)
            {
                var c = ContainerOf(ch.Divider);
                var y = c == null ? double.NaN : OffsetInView(c);
                if (c == null) { var at = window.IndexOf(ch.Divider); if (at >= 0 && at < window.Start) y = double.NegativeInfinity; }   // above the rows that are built
                if (!double.IsNaN(y) && y < -4) mode = "unread";
            }
            if (mode == null && !AtBottom) mode = "present";
            if (mode == jumpMode) return;
            jumpMode = mode;
            JumpButton.Visibility = mode == null ? Visibility.Collapsed : Visibility.Visible;
            JumpText.Text = mode == "unread" ? "Jump to first unread" : "Jump to present";
            JumpGlyph.Text = mode == "unread" ? "\uE74A" : "\uE74B";
        }

        void Jump_Click(object sender, RoutedEventArgs e)
        {
            var ch = Session?.Selected;
            if (jumpMode == "unread" && ch?.Divider != null) ScrollToMessage(ch.Divider, false);
            else ScrollToEnd();
        }

        // ---- pins
        void UpdatePins()
        {
            var ch = Session?.Selected;
            var n = ch == null ? 0 : Session.PinsOf(ch.Id).Count;
            PinCount.Text = n > 0 ? n.ToString() : "";
            PinButton.ToolTip = n == 0 ? "Pinned messages" : $"{n} pinned message{(n == 1 ? "" : "s")}";
            PinEmpty.Visibility = n == 0 ? Visibility.Visible : Visibility.Collapsed;
            PinTitle.Text = n == 0 ? "Pinned messages" : $"Pinned messages  ·  {n}";
        }

        void PinButton_Click(object sender, RoutedEventArgs e)
        {
            UpdatePins();
            PinPopup.PlacementTarget = PinButton;
            PinPopup.HorizontalOffset = PinButton.ActualWidth - PinBox.Width - 8 - PopupShadow;
            PinPopup.IsOpen = true;
        }

        void PinRow_Click(object sender, MouseButtonEventArgs e)
        {
            if (!((sender as FrameworkElement)?.Tag is PinVM p)) return;
            PinPopup.IsOpen = false;
            _ = JumpToAsync(Session.Selected?.Id, p.Id, new DateTimeOffset(DateTime.SpecifyKind(p.At, DateTimeKind.Utc)).ToUnixTimeMilliseconds());
        }

        void PinRemove_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if ((sender as FrameworkElement)?.Tag is PinVM p && Session.Selected != null) _ = Session.UnpinMessage(p.Id, Session.Selected.Id);
        }

        // scroll to a message in this channel and flash it (older ones are loaded first: see LodgeView.History.cs)
        void JumpTo(string id) => _ = JumpToAsync(Session.Selected?.Id, id, 0);

        void ScrollToEnd()
        {
            if (!window.Live) window.ShowEnd();   // the rows were further up (a jump, a long scroll up): back to the newest
            Dispatcher.BeginInvoke(new Action(() => MessageScroll.ScrollToEnd()), DispatcherPriority.Background);
        }

        // ================================================================ join / connection

        // offerRestore: a new sign-in just failed and an older one is still saved - offer to go back to it (the error stays on screen)
        bool offerRestore;

        void UpdateRestoreButton()
        {
            RestoreSavedButton.Visibility = offerRestore && SavedSignIn(out _, out _, out _) ? Visibility.Visible : Visibility.Collapsed;
        }

        bool SavedSignIn(out string url, out string credential, out bool isToken)
        {
            url = Session.Settings.LodgeUrl; credential = null; isToken = false;
            return !string.IsNullOrEmpty(url) && LodgeCredentials.Load(Session.Settings, out credential, out isToken);
        }

        void RestoreSaved_Click(object sender, RoutedEventArgs e)
        {
            if (!SavedSignIn(out var url, out var cred, out var isToken)) { offerRestore = false; UpdateRestoreButton(); return; }
            offerRestore = false;
            UpdateRestoreButton();
            EndEditConnection();
            JoinStatus.Text = "";
            ShowChat();
            Session.Connect(url, cred, isToken);
        }

        void ShowJoin(string why, bool offer = false)
        {
            ClearIncoming();
            ChatPanel.Visibility = Visibility.Collapsed;
            JoinPanel.Visibility = Visibility.Visible;
            JoinStatus.Foreground = (Brush)FindResource("Danger");
            JoinStatus.Text = why ?? "";
            offerRestore = offer;
            UpdateRestoreButton();
            UrlBox.Text = Session.Settings.LodgeUrl ?? "";
            DeviceBox.Text = LodgeCredentials.DeviceName(Session.Settings);
        }

        void ShowChat()
        {
            ClearIncoming();
            offerRestore = false;
            RestoreSavedButton.Visibility = Visibility.Collapsed;
            JoinPanel.Visibility = Visibility.Collapsed;
            ChatPanel.Visibility = Visibility.Visible;
        }

        void UrlBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var link = InviteLinks.Parse(EffectiveLinkText);
            if (link.Insecure) JoinFail(InviteLinks.InsecureText);
            else if (JoinStatus.Text == InviteLinks.InsecureText) JoinStatus.Text = "";   // the address was fixed: the warning goes
            CodeFromLink.Text = link.IsInvite ? "One-time invite found in the link" : "Invite code found in the link";
            CodeFromLink.Visibility = link.Secret != null ? Visibility.Visible : Visibility.Collapsed;
            CodeArea.Visibility = link.Secret != null ? Visibility.Collapsed : Visibility.Visible;
        }

        void Join_Click(object sender, RoutedEventArgs e) => TryJoin();
        void CodeBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) TryJoin(); }

        bool joining;

        void JoinFail(string text)
        {
            JoinStatus.Foreground = (Brush)FindResource("Danger");
            JoinStatus.Text = text;
            UpdateRestoreButton();
        }

        const string MissingAddressText = "This link is missing the lodge address - ask for a new link";
        public const string OtherLodgeNeedsInviteText = "That is a different Lodge - your saved sign-in only works for its own. Paste an invite link for the new one.";


        // Nothing here writes the saved sign-in. The new one is handed to the session as a candidate, which stores it only after the welcome.
        void TryJoin()
        {
            if (joining) return;
            // a whole link pasted into the code box belongs in the link box (and is handled as the link it is)
            var typed = CodeBox.Password.Trim();
            if (InviteLinks.LooksLikeLink(typed)) { ClearIncoming(); UrlBox.Text = typed; CodeBox.Password = ""; }
            var link = InviteLinks.Parse(EffectiveLinkText);
            if (link.MissingAddress) { JoinFail(MissingAddressText); return; }
            if (link.Insecure) { JoinFail(InviteLinks.InsecureText); return; }   // https only (localhost / 127.0.0.1 excepted for testing)
            var url = link.Url;
            var secret = link.Secret ?? CodeBox.Password.Trim();
            var bad = !Uri.TryCreate(url ?? "", UriKind.Absolute, out var u) || (u.Scheme != "https" && u.Scheme != "http");
            // editing the connection with nothing new typed: keep the saved sign-in - but only for the same Lodge (3.1.1: the same scheme, host
            // and port). A saved token or code is never sent to another address; that one needs its own invite.
            if (secret.Length == 0 && editingConnection && LodgeCredentials.Load(Session.Settings, out var kept, out var keptIsToken))
            {
                if (bad) { JoinFail("Paste the invite link you were sent (elanshub://lodge/join#...)"); return; }
                if (!InviteLinks.SameOrigin(url, Session.Settings.LodgeUrl)) { JoinFail(OtherLodgeNeedsInviteText); return; }
                Session.Settings.LodgeName = NameBox.Text.Trim();
                Session.Settings.LodgeDeviceName = string.IsNullOrWhiteSpace(DeviceBox.Text) ? null : DeviceBox.Text.Trim();
                RejoinWithSaved(url, kept, keptIsToken);
                return;
            }
            if (bad) { JoinFail("Paste the invite link you were sent (elanshub://lodge/join#...)"); return; }
            var s = Session.Settings;
            s.LodgeName = NameBox.Text.Trim();
            s.LodgeDeviceName = string.IsNullOrWhiteSpace(DeviceBox.Text) ? null : DeviceBox.Text.Trim();
            if (link.IsInvite || InviteLinks.LooksLikeInvite(secret)) { _ = JoinWithInvite(url, secret); return; }
            if (secret.Length < 6) { JoinFail("Enter your invite code"); return; }
            // an old permanent code (older server, or an older-style link): works exactly as before, but is only kept once the lodge says welcome
            UrlBox.Text = url; // the code stays out of the visible box
            CodeBox.Password = "";
            EndEditConnection();
            JoinStatus.Text = "";
            ShowChat();
            Session.Connect(url, secret, false, new PendingCredential { Url = url, Secret = secret, IsToken = false });
        }

        // only the address changed (or nothing): the saved credential is reused, the new address is kept once the lodge welcomes it
        void RejoinWithSaved(string url, string credential, bool isToken)
        {
            var s = Session.Settings;
            bool same = string.Equals(url, s.LodgeUrl, StringComparison.OrdinalIgnoreCase);
            if (same) SettingsStore.Save(s);
            EndEditConnection();
            JoinStatus.Text = "";
            ShowChat();
            Session.Connect(url, credential, isToken, same ? null : new PendingCredential { Url = url, UrlOnly = true });
        }

        // a one-time invite: trade it for this PC's own token, then connect with that. The invite is sent in a header, never in a URL.
        async Task JoinWithInvite(string url, string invite)
        {
            joining = true;
            JoinButton.IsEnabled = false;
            JoinStatus.Foreground = (Brush)FindResource("TextDim");
            JoinStatus.Text = "Joining...";
            try
            {
                var s = Session.Settings;
                var res = await InviteExchange.Exchange(url, invite, null, LodgeCredentials.DeviceName(s), LodgeCredentials.DeviceId(s), NameBox.Text);
                if (!res.Ok)
                {
                    if (res.Reason == "name-required") NameCaption.Text = NameCaptionOwner;
                    offerRestore = true;    // a saved sign-in (if any) is untouched: offer to go back to it
                    JoinFail(res.Message);
                    return;
                }
                // the saved sign-in stays until the lodge welcomes the new token
                UrlBox.Text = url;          // the invite is used up and gone from the box
                CodeBox.Password = "";
                EndEditConnection();
                JoinStatus.Text = "";
                ShowChat();
                Session.Connect(url, res.Token, true, new PendingCredential
                {
                    Url = url, Secret = res.Token, IsToken = true, DeviceName = LodgeCredentials.DeviceName(s),
                    Name = !res.Guest && !string.IsNullOrEmpty(res.Name) ? res.Name : null
                });
            }
            finally
            {
                joining = false;
                JoinButton.IsEnabled = true;
                JoinStatus.Foreground = (Brush)FindResource("Danger");
            }
        }

        // the first-run guide: join with a link that was already checked (code inside the link)
        public void JoinWithLink(string link) { UrlBox.Text = link; NameBox.Text = Session.Settings.LodgeName ?? Environment.UserName; TryJoin(); }


        // ================================================================ an elanshub:// link was clicked (see Services/LinkOpen.cs)
        // The link is shown for the person to confirm: nothing connects until they press Join. The secret part is kept in memory and the box shows it masked.
        string incoming, incomingShown;
        string EffectiveLinkText => incoming != null && UrlBox.Text == incomingShown ? incoming : UrlBox.Text;

        const string NameCaptionDefault = "NAME  (only used with a shared guest code)";
        const string NameCaptionOwner = "YOUR NAME IN THE LODGE  (you will be its Guild Master)";

        void ClearIncoming()
        {
            incoming = incomingShown = null;
            IncomingBanner.Visibility = Visibility.Collapsed;
            NameCaption.Text = NameCaptionDefault;   // the Guild Master wording belongs to one owner link only: it must not stick to the next join
        }

        // "nasferatu.dk/lodge" - how a lodge address is shown to people
        // 3.1.1: what is shown is the host the Outpost really connects to: an internationalised name as its punycode (xn--...), never a
        // user-info part, the port only when it is not the default
        public static string ShortAddress(string url)
        {
            var u = (url ?? "").Trim();
            if (u.Length > 0 && Uri.TryCreate(LodgeSession.NormalizeUrl(u), UriKind.Absolute, out var parsed) && parsed.IdnHost.Length > 0)
            {
                var host = parsed.HostNameType == UriHostNameType.IPv6 ? "[" + parsed.IdnHost.Trim('[', ']') + "]" : parsed.IdnHost;
                return (host + (parsed.IsDefaultPort ? "" : ":" + parsed.Port) + parsed.AbsolutePath).TrimEnd('/');
            }
            foreach (var p in new[] { "https://", "http://" }) if (u.StartsWith(p, StringComparison.OrdinalIgnoreCase)) { u = u.Substring(p.Length); break; }
            return u.TrimEnd('/');
        }


        public static string MaskSecret(string secret)
        {
            if (string.IsNullOrEmpty(secret)) return "";
            var keep = secret.StartsWith(InviteLinks.Prefix, StringComparison.Ordinal) ? InviteLinks.Prefix.Length : 0;
            return secret.Substring(0, keep) + new string((char)0x2022, 12);
        }

        // the sentence under the "You were invited" title: depends on whether this PC is already signed in, and to which lodge
        public const string NameAfterJoinLine = "The lodge's name appears once you join.";

        public static string IncomingWording(string linkUrl, string savedUrl, bool signedIn)
        {
            var target = ShortAddress(linkUrl);
            if (!signedIn || string.IsNullOrEmpty(savedUrl))
                return "Check that you trust whoever sent this link, then press Join. Nothing happens until you do.";
            var current = ShortAddress(savedUrl);
            if (string.Equals(target, current, StringComparison.OrdinalIgnoreCase))
                return "This link is for " + target + ". You are signed in to " + current + ". Joining adds this PC as another device.";
            return "This link is for " + target + ". You are signed in to " + current + ". Joining switches this PC to " + target + " and signs it out of " + current + ". Nothing changes until you press Join.";
        }

        public void ShowInviteLink(string link, bool ownerLink = false)
        {
            if (joining) return;
            link = LinkOpen.Validate(link) ?? (ownerLink ? FromWebOrNull(link) : null);
            if (link == null) return;
            var parsed = InviteLinks.Parse(link);
            var target = LodgeSession.NormalizeUrl(parsed.Url);
            var signedIn = SavedSignIn(out var savedUrl, out _, out _);
            editingConnection = false;
            EndEditConnection();
            incoming = link;
            incomingShown = InviteLinks.LinkStart + "#" + ShortAddress(parsed.Url) + (parsed.Secret != null ? "|" + MaskSecret(parsed.Secret) : "");
            ChatPanel.Visibility = Visibility.Collapsed;
            JoinPanel.Visibility = Visibility.Visible;
            offerRestore = false;
            UpdateRestoreButton();
            UrlBox.Text = incomingShown;
            CodeBox.Password = "";
            DeviceBox.Text = LodgeCredentials.DeviceName(Session.Settings);
            if (string.IsNullOrWhiteSpace(NameBox.Text)) NameBox.Text = Session.Settings.LodgeName ?? "";
            JoinStatus.Text = "";
            // No network request is made for a link, ever, before Join: the banner holds only what the link says, plus the name this PC already knows
            // when the link is for the lodge it is signed in to (from the welcome / lodge.info of the current session).
            var known = signedIn && string.Equals(target, LodgeSession.NormalizeUrl(savedUrl), StringComparison.OrdinalIgnoreCase) && (Session.Online || Session.TestFed) && !string.IsNullOrWhiteSpace(Session.LodgeName);
            var wording = ownerLink
                ? "This is the owner link from your server. Type the name you want below, then press Join: you become the Guild Master."
                : IncomingWording(target, savedUrl, signedIn);
            if (known)
            {
                IncomingTitle.Text = (ownerLink ? "Your lodge: " : "You were invited to ") + Session.LodgeName.Trim();
                IncomingText.Text = (string.IsNullOrWhiteSpace(Session.LodgeWelcome) ? "" : Session.LodgeWelcome.Trim() + "\n\n") + wording;
            }
            else
            {
                IncomingTitle.Text = (ownerLink ? "Your new lodge: " : "You were invited to ") + ShortAddress(parsed.Url);
                IncomingText.Text = wording + "\n\n" + NameAfterJoinLine;
            }
            IncomingBanner.Visibility = Visibility.Visible;
            NameCaption.Text = ownerLink ? NameCaptionOwner : NameCaptionDefault;
            if (ownerLink) NameBox.Text = "";
            if (signedIn) CancelEditButton.Visibility = Visibility.Visible;      // a way out that changes nothing
        }

        // an owner link pasted in the web form is accepted too: hand it on as the elanshub:// link it stands for
        static string FromWebOrNull(string text)
        {
            var l = InviteLinks.Parse(text);
            return l.IsInvite && l.Url != null ? InviteLinks.Make(l.Url, l.Secret) : null;
        }

        public string IncomingTitleForTest => IncomingBanner.Visibility == Visibility.Visible ? IncomingTitle.Text : null;
        public string IncomingTextForTest => IncomingBanner.Visibility == Visibility.Visible ? IncomingText.Text : null;
        public bool CancelVisibleForTest => CancelEditButton.Visibility == Visibility.Visible;
        public bool JoiningForTest => joining;
        public string NameBoxForTest { get => NameBox.Text; set => NameBox.Text = value; }

        public void EditConnection()
        {
            ClearIncoming();
            editingConnection = true;
            UrlBox.Text = Session.Settings.LodgeUrl ?? "";
            NameBox.Text = Session.Settings.LodgeName ?? "";
            CodeBox.Password = "";
            JoinTitle.Text = "Edit connection";
            CodeCaption.Text = "INVITE CODE  (leave empty to keep your current one)";
            JoinButton.Content = "Save and reconnect";
            CancelEditButton.Visibility = Visibility.Visible;
            JoinStatus.Text = "";
            ChatPanel.Visibility = Visibility.Collapsed;
            JoinPanel.Visibility = Visibility.Visible;
        }

        void CancelEdit_Click(object sender, RoutedEventArgs e)
        {
            var wasIncoming = incoming != null;
            CodeBox.Password = "";      // a code typed and then cancelled must not wait there to be sent with the next join
            EndEditConnection();
            if (wasIncoming) UrlBox.Text = "";
            ClearIncoming();
            if (Session.Connected || (wasIncoming && SavedSignIn(out _, out _, out _))) ShowChat();
        }

        void EndEditConnection()
        {
            editingConnection = false;
            JoinTitle.Text = "Join a lodge";
            CodeCaption.Text = "INVITE CODE";
            JoinButton.Content = "Join";
            CancelEditButton.Visibility = Visibility.Collapsed;
        }

        // forget the sign-in on THIS PC only (the lodge itself is untouched). note: a calm line for the join box, e.g. after leaving.
        public void SignOut(string note = "")
        {
            Session.Disconnect();
            LodgeCredentials.Clear(Session.Settings);
            SettingsStore.Save(Session.Settings);
            CodeBox.Password = "";      // (see CancelEdit_Click)
            ShowJoin("");
            if (!string.IsNullOrEmpty(note)) { JoinStatus.Foreground = (Brush)FindResource("TextDim"); JoinStatus.Text = note; }
        }

        // ================================================================ sidebar

        void Channel_Click(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is ChannelVM ch) { HideCalendar(); HideFiles(); HideLadder(); Session.Select(ch); }
        }

        void Room_Click(object sender, MouseButtonEventArgs e)
        {
            if (!((sender as FrameworkElement)?.Tag is ChannelVM room) || room.Id == Session.MyRoom) return;
            Session.JoinRoom(room.Id);
        }

        void LeaveVoice_Click(object sender, RoutedEventArgs e) => Session.LeaveVoice();

        void MuteDeaf_Click(object sender, RoutedEventArgs e)
        {
            var deaf = DeafToggle.IsChecked == true;
            var muted = MuteToggle.IsChecked == true || deaf;
            if (sender == DeafToggle && !deaf) muted = false; // undeafen also unmutes, like Discord
            Session.SetMuteDeaf(muted, deaf);
        }

        // a group header: collapse / expand the group (remembered)
        void GroupHeader_Click(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is MemberGroupVM g) g.Expanded = !g.Expanded;
            e.Handled = true;
        }
        public ItemsControl GroupListForTest => GroupList;

        void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings?.Invoke();

        // ---- status
        void Me_Click(object sender, MouseButtonEventArgs e)
        {
            NoteBox.Text = Session.Settings.LodgeNote ?? "";
            InvisibleCheck.Visibility = Session.Invisible ? Visibility.Visible : Visibility.Collapsed;
            StatusPopup.PlacementTarget = MeRow;
            StatusPopup.IsOpen = true;
            e.Handled = true;
        }

        void StatusOption_Click(object sender, RoutedEventArgs e)
        {
            var tag = (sender as FrameworkElement)?.Tag as string ?? "online";
            if (tag == "invisible") Session.SetInvisible(!Session.Invisible);   // a switch, independent of the status above it
            else Session.SetMyStatus(tag, NoteBox.Text);
            StatusPopup.IsOpen = false;
            Refresh();
        }

        void NoteBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            Session.SetMyStatus(Session.Settings.LodgeStatus ?? "online", NoteBox.Text);
            StatusPopup.IsOpen = false;
            Refresh();
        }

        // the hover card of a name in the chat: only for people who are still in the lodge
        void NameTip_Opening(object sender, ToolTipEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is MessageVM m) || m.Author == null) e.Handled = true;
        }

        // ---- a person: the profile card, then mention / join their room / volume / officer tools
        ProfileCardVM popCard;

        void Member_Click(object sender, MouseButtonEventArgs e)
        {
            if (!((sender as FrameworkElement)?.Tag is MemberVM m)) return;
            if (m.IsMe) { Me_Click(sender, e); return; }
            ShowProfileCard(m, (UIElement)sender);
            e.Handled = true;
        }

        // a name or picture in the chat: the card of that person (online, or offline from what the server knows)
        void MessageAuthor_Click(object sender, MouseButtonEventArgs e)
        {
            if (!((sender as FrameworkElement)?.Tag is MessageVM msg) || string.IsNullOrEmpty(msg.From)) return;
            var live = msg.Author;
            if (live != null && live.IsMe) OpenProfileEditor();
            else if (live != null) ShowProfileCard(live, (UIElement)sender);
            else ShowProfileCardByName(msg.From, (UIElement)sender);
            e.Handled = true;
        }

        void ShowProfileCardByName(string name, UIElement target)
        {
            var standIn = new MemberVM { Id = 0, Name = name, Offline = true };
            standIn.SetDisplayName(DisplayNames.Shown(name) == name ? "" : DisplayNames.Shown(name));
            var known = Session.KnownAvatarOf(name);
            if (known != null) standIn.SetAvatar(known[0], known[1]);
            ShowProfileCard(standIn, target);
        }

        public void ShowProfileCard(MemberVM m, UIElement target)
        {
            popMember = m;
            popLoading = true;
            popCard = new ProfileCardVM(m) { Loading = Session.SupportsProfile };
            if (!Session.SupportsProfile) popCard.Data = new ProfileData { Name = m.Name, Found = true };
            popCard.RaiseAll();
            PopCard.Content = popCard;
            if (Session.SupportsBadges && !m.Guest) { popCard.SetBadges(Session.BadgesOf(m.Name)); Session.RequestMemberBadges(m.Name); }
            UpdateLadderChrome();
            PopVolume.Value = Session.PeerVolumeFor(m.Name);
            PopVolumeText.Text = $"{PopVolume.Value * 100:0}%";
            popLoading = false;
            PopVolumeArea.Visibility = m.Offline || m.IsMe ? Visibility.Collapsed : Visibility.Visible;
            PopModArea.Visibility = !m.Offline && Session.CanModerateMember(m) ? Visibility.Visible : Visibility.Collapsed;
            PopMute.Content = m.ServerMuted ? "Unmute for everyone" : "Mute for everyone";
            PopMention.Visibility = m.IsMe ? Visibility.Collapsed : Visibility.Visible;
            bool joinable = !m.Offline && m.Room != null && m.Room != Session.MyRoom && Session.Me != null;
            PopJoin.Visibility = joinable ? Visibility.Visible : Visibility.Collapsed;
            PopJoin.Content = "Join " + (string.IsNullOrEmpty(m.RoomName) ? "their voice room" : m.RoomName);
            PopReset.Visibility = Session.CanManage && !m.IsMe && (m.HasAvatar || m.HasDisplayName) ? Visibility.Visible : Visibility.Collapsed;
            UpdateDeviceLinkRow(m.Role);
            MemberPopup.PlacementTarget = target;
            MemberPopup.IsOpen = true;
            Session.RequestProfile(m.Name);
        }

        void OnProfileReceived(ProfileData pd)
        {
            if (popCard == null || !MemberPopup.IsOpen || !string.Equals(pd.Name, popCard.Member.Name, StringComparison.OrdinalIgnoreCase)) return;
            popCard.Loading = false;
            if (popCard.Member.Offline && pd.Found) { popCard.Member.Role = pd.Role; popCard.Member.SetAvatar(pd.AvatarId, pd.Accent); popCard.Member.SetDisplayName(pd.DisplayName); popCard.Member.Offline = !pd.Online; }
            popCard.Data = pd;
            UpdateDeviceLinkRow(popCard.Member.Role);
            PopReset.Visibility = Session.CanManage && !popCard.Member.IsMe && (popCard.Member.HasAvatar || popCard.Member.HasDisplayName) ? Visibility.Visible : Visibility.Collapsed;
        }

        // "Send a new device link": owners and officers, for people below them (officers) who are members (not guests, not me)
        void UpdateDeviceLinkRow(string theirRole)
        {
            var m = popMember;
            PopDeviceLink.Visibility = m != null && !m.IsMe && !m.Guest
                && InviteRanks.CanSendDeviceLink(Session.MyRole, Session.CanInvite, Session.SupportsTokens, theirRole ?? m.Role, m.Guest)
                ? Visibility.Visible : Visibility.Collapsed;
        }

        void PopDeviceLink_Click(object sender, RoutedEventArgs e)
        {
            MemberPopup.IsOpen = false;
            if (popMember == null) return;
            InviteSheetHost.Open(popMember.Name, popMember.Role);
        }

        void InviteSide_Click(object sender, RoutedEventArgs e) => InviteSheetHost.Open();

        void CreateLodge_Click(object sender, RoutedEventArgs e) => CreateGuide.Open();

        public InviteSheet InviteSheetForTest => InviteSheetHost;
        public CreateLodgeGuide GuideForTest => CreateGuide;
        public bool InviteButtonVisibleForTest => InviteSideButton.Visibility == Visibility.Visible;
        public bool CreateLodgeVisibleForTest => CreateLodgeButton.Visibility == Visibility.Visible && JoinPanel.Visibility == Visibility.Visible;
        public void ShowMemberForTest(MemberVM m) { popMember = m; UpdateDeviceLinkRow(m.Role); }
        public bool DeviceLinkVisibleForTest => PopDeviceLink.Visibility == Visibility.Visible;
        public void DeviceLinkClickForTest() => PopDeviceLink_Click(null, null);
        public void InviteSideClickForTest() => InviteSide_Click(null, null);
        public void CreateLodgeClickForTest() => CreateLodge_Click(null, null);
        public string NameCaptionForTest => NameCaption.Text;

        void PopMention_Click(object sender, RoutedEventArgs e)
        {
            MemberPopup.IsOpen = false;
            if (popMember != null) InsertMention(popMember.Display);
        }

        public void InsertMention(string name)
        {
            var at = Math.Min(Composer.CaretIndex, Composer.Text.Length);
            var text = "@" + name + " ";
            Composer.Text = Composer.Text.Insert(at, text);
            Composer.CaretIndex = at + text.Length;
            Composer.Focus();
        }

        void PopJoin_Click(object sender, RoutedEventArgs e)
        {
            MemberPopup.IsOpen = false;
            if (popMember?.Room != null) Session.JoinRoom(popMember.Room);
        }

        void PopReset_Click(object sender, RoutedEventArgs e)
        {
            MemberPopup.IsOpen = false;
            if (popMember == null) return;
            if (Dialog.Confirm(Window.GetWindow(this), $"Reset {popMember.Display}'s profile?", "Their avatar, accent and display name go back to the default. They can pick new ones afterwards.", "Reset", danger: true))
                Session.ResetProfile(popMember.Name);
        }

        // ---- my profile
        void MeAvatar_Click(object sender, MouseButtonEventArgs e) { OpenProfileEditor(); e.Handled = true; }
        void EditProfile_Click(object sender, RoutedEventArgs e) { StatusPopup.IsOpen = false; OpenProfileEditor(); }

        // every "Edit profile" entry point goes to Settings > Profile
        public event Action EditProfileRequested;
        public void OpenProfileEditor() => EditProfileRequested?.Invoke();

        public void ShowProfileCardByNameForTest(string name, UIElement target) => ShowProfileCardByName(name, target);
        public ProfileCardVM PopCardForTest => popCard;
        public FrameworkElement PopupCardForTest => (FrameworkElement)MemberPopup.Child;
        public void CloseProfileForTest() { MemberPopup.IsOpen = false; }

        void PopVolume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (popLoading || popMember == null) return;
            PopVolumeText.Text = $"{e.NewValue * 100:0}%";
            Session.SetPeerVolume(popMember, e.NewValue);
        }

        void PopMute_Click(object sender, RoutedEventArgs e)
        {
            if (popMember != null) Session.ServerMute(popMember, !popMember.ServerMuted);
            MemberPopup.IsOpen = false;
        }

        void PopKick_Click(object sender, RoutedEventArgs e)
        {
            MemberPopup.IsOpen = false;
            if (popMember == null) return;
            if (Dialog.Confirm(Window.GetWindow(this), $"Kick {popMember.Display}?", "They are removed from the lodge right now, but can join again with their code.", "Kick", danger: true))
                Session.Kick(popMember);
        }

        // ================================================================ messages

        void Reply_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is MessageVM m) StartReply(m);
        }

        void StartReply(MessageVM m)
        {
            editing = null;
            replyTo = m;
            var snip = (m.Text ?? m.File?.Name ?? "").Replace("\n", " ");
            if (snip.Length > 60) snip = snip.Substring(0, 60) + "...";
            ModeText.Text = $"Replying to {m.FromShown}  -  {snip}";
            ModeBar.Visibility = Visibility.Visible;
            Composer.Focus();
        }

        void ReplyQuote_Click(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is MessageVM m) JumpTo(m.ReplyId);
            e.Handled = true;
        }

        void StartEdit(MessageVM m)
        {
            replyTo = null;
            editing = m;
            ModeText.Text = "Editing your message - Enter saves, Esc cancels";
            ModeBar.Visibility = Visibility.Visible;
            Composer.Text = m.Text ?? "";
            Composer.CaretIndex = Composer.Text.Length;
            Composer.Focus();
        }

        void DeleteMessage(MessageVM m)
        {
            var whose = m.Mine ? "your message" : $"{m.FromShown}'s message";
            if (Dialog.Confirm(Window.GetWindow(this), "Delete message?", $"Delete {whose}? This can't be undone.", "Delete", danger: true))
                _ = Session.Delete(m.Id);
        }

        // ---- reactions
        void React_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.Tag is MessageVM m)) return;
            // the picker opens to the left of the hover toolbar
            var toolbar = (sender as FrameworkElement)?.Parent is FrameworkElement p ? p.Parent as FrameworkElement : null;
            OpenReactPicker(m, toolbar);
        }

        void OpenReactPicker(MessageVM m, FrameworkElement target)
        {
            reactTarget = m;
            ReactItems.ItemsSource = Session.ReactionSet.Select(id => new ReactionChoice { Id = id }).ToList();
            ReactPopup.Placement = target != null ? PlacementMode.Left : PlacementMode.MousePoint;
            ReactPopup.PlacementTarget = target;
            ReactPopup.HorizontalOffset = target != null ? 10 : -PopupShadow;   // (card margin holds the shadow)
            ReactPopup.VerticalOffset = -PopupShadow;
            ReactPopup.IsOpen = true;
        }

        void ReactPick_Click(object sender, RoutedEventArgs e)
        {
            ReactPopup.IsOpen = false;
            if (reactTarget != null && (sender as FrameworkElement)?.Tag is string emoji) _ = Session.React(reactTarget, emoji);
        }

        void Reaction_Click(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is ReactionVM r) _ = Session.React(r.Msg, r.Emoji);
            e.Handled = true;
        }

        // ---- pin / unpin
        void PinTool_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is MessageVM m) TogglePin(m);
        }

        void TogglePin(MessageVM m)
        {
            if (m.Pinned) _ = Session.UnpinMessage(m.Id, m.Channel);
            else _ = Session.PinMessage(m.Id);
        }

        // ---- the message menu (More button and right click)
        void More_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is MessageVM m) OpenMenu(m);
        }

        void Message_RightClick(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is MessageVM m) { OpenMenu(m); e.Handled = true; }
        }

        void OpenMenu(MessageVM m)
        {
            menuMsg = m;
            MenuReact.Visibility = m.ReactEnabled ? Visibility.Visible : Visibility.Collapsed;
            MenuPin.Visibility = m.PinEnabled ? Visibility.Visible : Visibility.Collapsed;
            MenuPinText.Text = m.Pinned ? "Unpin message" : "Pin message";
            MenuPinGlyph.Text = m.Pinned ? "\uE77A" : "\uE718";
            MenuCopy.Visibility = string.IsNullOrEmpty(m.Text) ? Visibility.Collapsed : Visibility.Visible;
            MenuEdit.Visibility = m.Mine && !string.IsNullOrEmpty(m.Text) ? Visibility.Visible : Visibility.Collapsed;
            MenuDelete.Visibility = m.Mine || m.CanDelete ? Visibility.Visible : Visibility.Collapsed;
            MsgMenu.PlacementTarget = null;
            MsgMenu.IsOpen = true;
        }

        void MenuReply_Click(object sender, RoutedEventArgs e) { MsgMenu.IsOpen = false; if (menuMsg != null) StartReply(menuMsg); }
        void MenuReact_Click(object sender, RoutedEventArgs e) { MsgMenu.IsOpen = false; if (menuMsg != null) OpenReactPicker(menuMsg, null); }
        void MenuPin_Click(object sender, RoutedEventArgs e) { MsgMenu.IsOpen = false; if (menuMsg != null) TogglePin(menuMsg); }
        void MenuEdit_Click(object sender, RoutedEventArgs e) { MsgMenu.IsOpen = false; if (menuMsg != null) StartEdit(menuMsg); }
        void MenuDelete_Click(object sender, RoutedEventArgs e) { MsgMenu.IsOpen = false; if (menuMsg != null) DeleteMessage(menuMsg); }
        void MenuCopy_Click(object sender, RoutedEventArgs e)
        {
            MsgMenu.IsOpen = false;
            try { if (!string.IsNullOrEmpty(menuMsg?.Text)) Clipboard.SetText(menuMsg.ShownText); } catch { }
        }

        void CancelMode_Click(object sender, RoutedEventArgs e) => CancelMode();

        void CancelMode()
        {
            if (editing != null) Composer.Text = "";
            replyTo = null;
            editing = null;
            ModeBar.Visibility = Visibility.Collapsed;
        }

        void Composer_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { CancelMode(); e.Handled = true; return; }
            if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && PasteFiles()) { e.Handled = true; return; }
            if (e.Key == Key.Up && Composer.Text.Length == 0)
            {
                // edit your last message, like Discord
                var last = Session.MessagesOf(Session.Selected?.Id).LastOrDefault(m => m.Mine && !string.IsNullOrEmpty(m.Text));
                if (last != null) { StartEdit(last); e.Handled = true; }
                return;
            }
            if (e.Key != Key.Enter) return;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                var i = Composer.CaretIndex;
                Composer.Text = Composer.Text.Insert(i, "\n");
                Composer.CaretIndex = i + 1;
            }
            else SendText();
            e.Handled = true;
        }

        void Composer_TextChanged(object sender, TextChangedEventArgs e)
        {
            ComposerHint.Visibility = Composer.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (Composer.Text.Length > 0 && editing == null) Session.Typing();
        }

        void Send_Click(object sender, RoutedEventArgs e) => SendText();

        void SendText()
        {
            var text = Composer.Text.Trim();
            if (!Session.Online && !Session.TestFedForView) { SetNote("You are offline - your message stays in the box"); noteUntil = DateTime.UtcNow.AddSeconds(6); return; }
            if (editing != null)
            {
                if (text.Length > 0 && text != editing.Text) _ = Session.Edit(editing.Id, text);
                Composer.Text = "";
                CancelMode();
                return;
            }
            if (text.Length == 0) return;
            // the text only leaves the box when the send was really started; if it fails later the session hands it back
            if (!Session.TrySend(text, replyTo?.Id, out var why)) { SetNote(why, true); noteUntil = DateTime.UtcNow.AddSeconds(6); return; }
            Composer.Text = "";
            CancelMode();
        }

        // ================================================================ files

        // Ctrl+V: a screenshot or copied files go up as attachments; plain text pastes as usual
        bool PasteFiles()
        {
            try
            {
                if (Clipboard.ContainsFileDropList())
                {
                    foreach (string f in Clipboard.GetFileDropList()) if (File.Exists(f)) _ = Share(f);
                    return true;
                }
                // a picture on the clipboard (not text that merely came with one): upload it as pasted-<time>.png
                if (Clipboard.ContainsImage() && !Clipboard.ContainsText())
                {
                    var img = Clipboard.GetImage();
                    var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "elanshub-paste-" + Guid.NewGuid().ToString("N").Substring(0, 8))).FullName;
                    var path = Path.Combine(dir, $"pasted-{DateTime.Now:yyyyMMdd-HHmmss}.png");
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(img));
                    using (var fs = File.Create(path)) enc.Save(fs);
                    _ = Share(path, true);
                    return true;
                }
            }
            catch (Exception ex) { Util.Log("paste: " + ex.Message); }
            return false;
        }

        void Attach_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Share a file with the lodge", Multiselect = true };
            if (dlg.ShowDialog(Window.GetWindow(this)) == true) foreach (var f in dlg.FileNames) _ = Share(f);
        }

        // while files hover over the chat: "Drop to share" (initiates get the reason instead; the drop still lands so the usual message shows)
        void OnDragOver(object sender, DragEventArgs e)
        {
            var files = e.Data.GetDataPresent(DataFormats.FileDrop) && Session != null && Session.Online;
            e.Effects = files ? DragDropEffects.Copy : DragDropEffects.None;
            if (files && ChatPanel.Visibility == Visibility.Visible)
            {
                var ok = Session.CanShareFiles;
                DropText.Text = ok ? "Drop to share" : "Initiates can't share files";
                DropSub.Text = ok ? $"Pictures and files go to #{Session.Selected?.Name}" : "Ask the Guild Master for a rank";
                DropGlyph.Text = ok ? "\uE896" : "\uE72E";
                DropOverlay.Visibility = Visibility.Visible;
                dropTimer.Stop(); dropTimer.Start(); // hides itself when the drag leaves
            }
            e.Handled = true;
        }

        void OnDrop(object sender, DragEventArgs e)
        {
            dropTimer.Stop();
            DropOverlay.Visibility = Visibility.Collapsed;
            if (!(e.Data.GetData(DataFormats.FileDrop) is string[] files)) return;
            foreach (var f in files.Where(File.Exists)) _ = Share(f);
        }

        // Several files (a drop, a multi-select, a paste): one upload at a time, in order; only the first carries the caption
        sealed class ShareJob { public string Path; public bool DeleteAfter; }
        readonly System.Collections.Generic.Queue<ShareJob> shareQueue = new System.Collections.Generic.Queue<ShareJob>();
        bool sharing;
        int shareTotal, shareDone;

        public Task Share(string path, bool deleteAfter = false)
        {
            shareQueue.Enqueue(new ShareJob { Path = path, DeleteAfter = deleteAfter });
            shareTotal++;
            return sharing ? Task.CompletedTask : RunShareQueue();
        }

        async Task RunShareQueue()
        {
            sharing = true;
            try
            {
                while (shareQueue.Count > 0)
                {
                    var job = shareQueue.Dequeue();
                    shareDone++;
                    var n = shareTotal > 1 ? $" ({shareDone} of {shareTotal})" : "";
                    var name = System.IO.Path.GetFileName(job.Path);
                    SetNote($"Uploading {name}{n}...");
                    noteUntil = DateTime.UtcNow.AddMinutes(5);
                    try
                    {
                        var first = shareDone == 1;
                        var text = first ? Composer.Text.Trim() : "";
                        // the progress is reported from the upload's own (background) thread: hand it to the window's thread,
                        // touching the note from there threw "the calling thread cannot access this object" and failed every upload
                        await Session.Share(job.Path, text, first ? replyTo?.Id : null, pr => Dispatcher.BeginInvoke(new Action(() =>
                        {
                            SetNote($"Uploading {name}{n} {pr * 100:0}%");
                            noteUntil = DateTime.UtcNow.AddMinutes(5);
                        })));
                        if (first && editing == null) { Composer.Text = ""; CancelMode(); }
                        ChatNote.Text = "";
                    }
                    catch (Exception ex)
                    {
                        Util.Log("upload: " + ex.Message);
                        SetNote((ex is InvalidOperationException ? ex.Message : "Upload failed: " + ex.Message) + (shareTotal > 1 ? $" ({name})" : ""), true);
                        noteUntil = DateTime.UtcNow.AddSeconds(8);
                    }
                    finally
                    {
                        if (job.DeleteAfter) try { File.Delete(job.Path); Directory.Delete(System.IO.Path.GetDirectoryName(job.Path)); } catch { }
                    }
                }
            }
            finally { sharing = false; shareTotal = 0; shareDone = 0; }
        }

        void Image_Click(object sender, MouseButtonEventArgs e)
        {
            if (!((sender as FrameworkElement)?.Tag is FileVM f) || f.LocalPath == null) return;
            var list = Session?.MessagesOf(Session.Selected?.Id);
            var msg = list?.FirstOrDefault(m => m.File == f);
            if (msg != null) Viewer.Open(list, msg);
            else if (!LodgeFiles.TryOpen(f.LocalPath)) { SetNote("Not opened: only pictures, text and PDF files open from here.", true); noteUntil = DateTime.UtcNow.AddSeconds(6); }
        }

        // ---- YouTube: the preview card plays in the overlay; a click on the URL text itself does the same (or opens the browser when previews are off)
        void Video_Click(object sender, MouseButtonEventArgs e)
        {
            if (!((sender as FrameworkElement)?.Tag is VideoVM v)) return;
            e.Handled = true;
            VideoPlayer.Open(v.Link, v.Title, v.Channel);
        }

        YouTubeLink LinkUnder(object sender, MouseEventArgs e)
        {
            if (!(sender is TextBox tb)) return null;
            return YouTube.FindAt(tb.Text, tb.GetCharacterIndexFromPoint(e.GetPosition(tb), false));
        }

        // any http(s) link in the message text (YouTube links included; those are handled first)
        string WebLinkUnder(object sender, MouseEventArgs e)
        {
            if (!(sender is TextBox tb)) return null;
            return ChatLinks.At(tb.Text, tb.GetCharacterIndexFromPoint(e.GetPosition(tb), false));
        }

        // a non-YouTube link was clicked: the host decides (Outpost browser or default browser)
        public event Action<string> OpenLink;
        public Action<string> OpenLinkOverrideForTest;   // the self-test must never launch a real browser

        // an item chip: the item on Wowhead Classic, through the same path as every chat link (Settings > Privacy > Outpost browser decides where)
        public event Action<string> OpenAtlas;   // Ctrl+click on an item chip: the item's page in the Atlas
        // the chip's hover tooltip is built when it opens (the game tooltip of the item or Atlas thing)
        void ItemChipTip_Opened(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.ToolTip tt && tt.DataContext is ItemChipVM chip) tt.Content = chip.BuildTip();
        }

        void ItemChip_Click(object sender, MouseButtonEventArgs e)
        {
            if (!((sender as FrameworkElement)?.Tag is ItemChipVM chip)) return;
            e.Handled = true;
            if (chip.Ref.AtlasId != null)   // a pasted Atlas link (item:2589, npc:46, browse:...): the Atlas, never Wowhead
            {
                if (OpenAtlas != null) OpenAtlas(chip.Ref.AtlasId);
                return;
            }
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && chip.Ref.Id > 0 && OpenAtlas != null) { OpenAtlas(Services.Db2.AtlasIds.Item(chip.Ref.Id)); return; }
            var url = chip.Url;
            if (OpenLinkOverrideForTest != null) OpenLinkOverrideForTest(url);
            else if (OpenLink != null) OpenLink(url);
            else WebEngine.OpenExternal(url);
        }
        public void ItemChipClickForTest(ItemChipVM chip) => ItemChip_Click(new FrameworkElement { Tag = chip }, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });

        void MessageText_Move(object sender, MouseEventArgs e)
        {
            var tb = (TextBox)sender;
            if (LinkUnder(sender, e) != null || WebLinkUnder(sender, e) != null) tb.Cursor = Cursors.Hand; else tb.ClearValue(FrameworkElement.CursorProperty);
        }

        void MessageText_Up(object sender, MouseButtonEventArgs e)
        {
            var tb = (TextBox)sender;
            if (tb.SelectionLength > 0) return;
            var l = LinkUnder(sender, e);
            if (l != null)
            {
                if (tb.Tag is MessageVM m && m.Video?.Thumb != null) VideoPlayer.Open(l, m.Video.Title, m.Video.Channel);   // a preview exists: same overlay as the card
                else VideoViewer.OpenInBrowser(l);                                             // previews off / unavailable video: the browser
                return;
            }
            var url = WebLinkUnder(sender, e);
            if (url == null) return;
            if (OpenLink != null) OpenLink(url); else WebEngine.OpenExternal(url);
        }

        public ImageViewer ViewerForTest => Viewer;
        public VideoViewer VideoPlayerForTest => VideoPlayer;
        public Task<bool> VideoCardVisibleForTest(MessageVM m) => Task.FromResult(m.VideoVisibility == Visibility.Visible);

        async void Download_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.Tag is FileVM f)) return;
            try
            {
                // a program or script: say what it really is before it lands in Downloads
                if (f.IsRisky && !Dialog.Confirm(Window.GetWindow(this), $"Download a {f.ExtText} file?",
                        $"\"{f.Name}\" is a {f.Ext} file. Files like this can run programs on your PC when opened. Only download it if you know the sender and expected it.", "Download anyway", danger: true))
                    return;
                var target = LodgeSession.DownloadTarget(DownloadsFolder(), f);
                if (target == null) { f.Status = "failed"; SetNote("That file's name can't be saved safely", true); noteUntil = DateTime.UtcNow.AddSeconds(8); return; }
                f.Status = "downloading...";
                await Session.Download(f, target);
                f.Status = "saved";
                Process.Start("explorer.exe", $"/select,\"{target}\"");
            }
            catch (Exception ex)
            {
                f.Status = "failed";
                Util.Log("download: " + ex.Message);
                SetNote("Couldn't download " + f.Name + " - try again in a moment", true);
                noteUntil = DateTime.UtcNow.AddSeconds(8);
            }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out string path);

        static string DownloadsFolder()
        {
            try { if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out var p) == 0) return p; }
            catch { }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }

        // ================================================================ in-game overlay

        ToastWindow toastWin;
        public bool ForceToastsForTest;
        public ToastWindow ToastWindowForTest => toastWin;

        // the toast stack: shown over WoW (or anywhere with "Also when WoW isn't in front"), hidden when empty
        void UpdateToasts()
        {
            var t = Session.Toasts;
            if (t == null) return;
            var s = Session.Settings;
            var wow = OverlayWindow.WowInFront();
            bool show = t.Visible.Count > 0 && !s.ToastsOff && (wow != IntPtr.Zero || s.OverlayAlways || ForceToastsForTest);
            if (!show) { if (toastWin != null && toastWin.IsVisible) toastWin.Hide(); return; }
            if (toastWin == null) toastWin = new ToastWindow(t.Visible);
            if (!toastWin.IsVisible) toastWin.Show();
            var main = Window.GetWindow(this);
            var near = wow != IntPtr.Zero ? wow : main != null ? new System.Windows.Interop.WindowInteropHelper(main).Handle : IntPtr.Zero;
            toastWin.UpdateLayout();
            toastWin.Place(near, s.ToastCorner ?? "tr");
        }

        public void SnapshotToasts(string file)
        {
            if (toastWin == null || !toastWin.IsVisible) return;
            toastWin.UpdateLayout();
            var w = (int)toastWin.ActualWidth; var h = (int)toastWin.ActualHeight;
            var bmp = new RenderTargetBitmap(w * 2, h * 2, 192, 192, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x3A, 0x4A, 0x3A)), null, new Rect(0, 0, w, h));
                dc.DrawRectangle(new VisualBrush((Visual)toastWin.Content), null, new Rect(0, 0, w, h));
            }
            bmp.Render(dv);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using (var fs = File.Create(file)) enc.Save(fs);
        }

        void UpdateOverlay()
        {
            UpdateToasts();
            var s = Session.Settings;
            var wow = OverlayWindow.WowInFront();
            bool show = Session.MyRoom != null && !s.OverlayOff && (wow != IntPtr.Zero || s.OverlayAlways || ForceOverlayForTest || OverlayPreview);
            if (!show)
            {
                if (overlay != null && overlay.IsVisible) overlay.Hide();
                return;
            }
            if (overlay == null) overlay = new OverlayWindow(Session.Members);
            if (!overlay.IsVisible) overlay.Show();
            var main = Window.GetWindow(this);
            var near = wow != IntPtr.Zero ? wow : main != null ? new System.Windows.Interop.WindowInteropHelper(main).Handle : IntPtr.Zero;
            overlay.Place(near, s.OverlayRight, s.OverlayTop ?? 0.3);
        }

        public void SnapshotOverlay(string file)
        {
            if (overlay == null || !overlay.IsVisible) return;
            overlay.UpdateLayout();
            var bmp = new RenderTargetBitmap((int)overlay.ActualWidth * 2, (int)overlay.ActualHeight * 2, 192, 192, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x3A, 0x4A, 0x3A)), null, new Rect(0, 0, overlay.ActualWidth, overlay.ActualHeight));
                dc.DrawRectangle(new VisualBrush((Visual)overlay.Content), null, new Rect(0, 0, overlay.ActualWidth, overlay.ActualHeight));
            }
            bmp.Render(dv);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using (var fs = File.Create(file)) enc.Save(fs);
        }

        // test hooks
        public void ShowChatForTest() => ShowChat();
        public void OpenPinsForTest() => PinButton_Click(null, null);
        public void OpenStatusForTest() { Me_Click(MeRow, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent }); }
        public void CloseStatusForTest() => StatusPopup.IsOpen = false;
        public void UpdateMeForTest() => Refresh();
        // the hover card of a person, opened next to `target` (the real tooltip style and template)
        public ToolTip ShowCardForTest(MemberVM m, FrameworkElement target)
        {
            var tip = new ToolTip
            {
                Style = (Style)FindResource("CardTip"), ContentTemplate = (DataTemplate)Resources["MemberCard"], Content = m,
                PlacementTarget = target, Placement = PlacementMode.Right, IsOpen = true,
            };
            return tip;
        }
        public FrameworkElement StatusBoxForTest => StatusBox;
        public FrameworkElement SidebarForTest => SidebarBox;
        public FrameworkElement ChatListForTest => MessageScroll;
        public void OpenReactForTest(MessageVM m) => OpenReactPicker(m, null);
        public void OpenMenuForTest(MessageVM m) => OpenMenu(m);
        public void ClosePopupsForTest() { PinPopup.IsOpen = false; ReactPopup.IsOpen = false; MsgMenu.IsOpen = false; }
        public FrameworkElement PinBoxForTest => PinBox;
        public FrameworkElement PickerForTest => ReactPickerBox;
        public FrameworkElement MenuForTest => MsgMenuBox;
        public bool PinButtonVisibleForTest => PinButton.Visibility == Visibility.Visible;
        public string JumpModeForTest => jumpMode;
        public void ScrollToTopForTest() => MessageScroll.ScrollToTop();
        public void ShowDropForTest(bool canShare)
        {
            DropText.Text = canShare ? "Drop to share" : "Initiates can't share files";
            DropOverlay.Visibility = Visibility.Visible;
        }
        public void HideDropForTest() => DropOverlay.Visibility = Visibility.Collapsed;
        public void FillJoinForTest(string url, string code, string name) { UrlBox.Text = url; CodeBox.Password = code; NameBox.Text = name; TryJoin(); }
        public string JoinError => JoinStatus.Text;
    }
}
