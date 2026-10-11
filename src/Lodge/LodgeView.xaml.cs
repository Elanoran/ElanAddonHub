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
            session.Stopped += why => ShowJoin(why);
            session.ProfileReceived += OnProfileReceived;
            session.Error += text => { SetNote(text, true); noteUntil = DateTime.UtcNow.AddSeconds(6); };
            session.Hint += ShowHint;
            session.SendFailed += (p, why) => RestoreText(p.Text);
            session.ToastWillShow = () => !session.Settings.ToastsOff && (OverlayWindow.WowInFront() != IntPtr.Zero || session.Settings.OverlayAlways || ForceToastsForTest);
            tick.Start();

            UrlBox.Text = session.Settings.LodgeUrl ?? "";
            NameBox.Text = session.Settings.LodgeName ?? Environment.UserName;
            var code = SettingsStore.Unprotect(session.Settings.LodgeCodeProtected);
            if (!string.IsNullOrEmpty(session.Settings.LodgeUrl) && !string.IsNullOrEmpty(code) && !session.Settings.LodgeManualConnect)
            {
                ShowChat();
                session.Connect(session.Settings.LodgeUrl, code);
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

            // the channel on screen
            if (s.Selected != shownChannel)
            {
                // what I was typing stays with the channel it was for
                if (shownChannel != null && editing == null) drafts[shownChannel.Id] = Composer.Text;
                shownChannel = s.Selected;
                CancelMode();
                Composer.Text = s.Selected != null && drafts.TryGetValue(s.Selected.Id, out var draft) ? draft : "";
                Composer.CaretIndex = Composer.Text.Length;
                Messages.ItemsSource = s.MessagesOf(s.Selected?.Id);
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
        void ServerNoticeCopy_Click(object sender, RoutedEventArgs e)
        {
            try { Clipboard.SetText(Brand.ServerUpdateCommand); ServerNoticeCopy.Content = "Copied"; } catch { }
        }
        public bool ServerNoticeVisibleForTest => ServerNotice.Visibility == Visibility.Visible;
        public string ServerNoticeTextForTest => ServerNoticeText.Text;
        public void ServerNoticeCopyForTest() => ServerNoticeCopy_Click(null, null);
        public void ServerNoticeCloseForTest() => ServerNoticeClose_Click(null, null);

        bool AtBottom => MessageScroll.VerticalOffset >= MessageScroll.ScrollableHeight - 60;

        // ---- scrolling, the "jump" button, highlight
        FrameworkElement ContainerOf(MessageVM vm) => Messages.ItemContainerGenerator.ContainerFromItem(vm) as FrameworkElement;

        double OffsetInView(FrameworkElement c)
        {
            try { return c.TransformToAncestor(MessageScroll).Transform(new Point(0, 0)).Y; } catch { return double.NaN; }
        }

        void ScrollToMessage(MessageVM vm, bool flash)
        {
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

        void MessageScroll_Changed(object sender, ScrollChangedEventArgs e) { UpdateJump(); MaybeLoadOlder(e); }

        void UpdateJump()
        {
            var ch = Session?.Selected;
            if (ch == null) { JumpButton.Visibility = Visibility.Collapsed; return; }
            string mode = null;
            if (ch.Unread && ch.Divider != null)
            {
                var c = ContainerOf(ch.Divider);
                var y = c == null ? double.NaN : OffsetInView(c);
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

        void ScrollToEnd() => Dispatcher.BeginInvoke(new Action(() => MessageScroll.ScrollToEnd()), DispatcherPriority.Background);

        // ================================================================ join / connection

        void ShowJoin(string why)
        {
            ChatPanel.Visibility = Visibility.Collapsed;
            JoinPanel.Visibility = Visibility.Visible;
            JoinStatus.Text = why ?? "";
            UrlBox.Text = Session.Settings.LodgeUrl ?? "";
        }

        void ShowChat()
        {
            JoinPanel.Visibility = Visibility.Collapsed;
            ChatPanel.Visibility = Visibility.Visible;
        }

        void UrlBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            LodgeSession.ParseInvite(UrlBox.Text, out var code);
            CodeFromLink.Visibility = code != null ? Visibility.Visible : Visibility.Collapsed;
            CodeArea.Visibility = code != null ? Visibility.Collapsed : Visibility.Visible;
        }

        void Join_Click(object sender, RoutedEventArgs e) => TryJoin();
        void CodeBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) TryJoin(); }

        void TryJoin()
        {
            var url = LodgeSession.ParseInvite(UrlBox.Text, out var linkCode);
            var code = linkCode ?? CodeBox.Password.Trim();
            if (code.Length == 0 && editingConnection) code = SettingsStore.Unprotect(Session.Settings.LodgeCodeProtected) ?? ""; // keep the saved one
            if (!Uri.TryCreate(url ?? "", UriKind.Absolute, out var u) || (u.Scheme != "https" && u.Scheme != "http"))
            {
                JoinStatus.Text = "Paste the invite link you were sent (https://...#invite=...)";
                return;
            }
            if (code.Length < 6) { JoinStatus.Text = "Enter your invite code"; return; }
            var s = Session.Settings;
            s.LodgeUrl = url;
            s.LodgeCodeProtected = SettingsStore.Protect(code);
            s.LodgeName = NameBox.Text.Trim();
            SettingsStore.Save(s);
            UrlBox.Text = url; // the code stays out of the visible box
            CodeBox.Password = "";
            EndEditConnection();
            JoinStatus.Text = "";
            ShowChat();
            Session.Connect(url, code);
        }

        // the first-run guide: join with a link that was already checked (code inside the link)
        public void JoinWithLink(string link) { UrlBox.Text = link; NameBox.Text = Session.Settings.LodgeName ?? Environment.UserName; TryJoin(); }

        public void EditConnection()
        {
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
            EndEditConnection();
            if (Session.Connected) ShowChat();
        }

        void EndEditConnection()
        {
            editingConnection = false;
            JoinTitle.Text = "Join a lodge";
            CodeCaption.Text = "INVITE CODE";
            JoinButton.Content = "Join";
            CancelEditButton.Visibility = Visibility.Collapsed;
        }

        public void SignOut()
        {
            Session.Disconnect();
            Session.Settings.LodgeCodeProtected = null;
            SettingsStore.Save(Session.Settings);
            ShowJoin("");
        }

        // ================================================================ sidebar

        void Channel_Click(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is ChannelVM ch) Session.Select(ch);
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
            PopReset.Visibility = Session.CanManage && !popCard.Member.IsMe && (popCard.Member.HasAvatar || popCard.Member.HasDisplayName) ? Visibility.Visible : Visibility.Collapsed;
        }

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
                        await Session.Share(job.Path, text, first ? replyTo?.Id : null, pr =>
                        {
                            SetNote($"Uploading {name}{n} {pr * 100:0}%");
                            noteUntil = DateTime.UtcNow.AddMinutes(5);
                        });
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
            else try { Process.Start(new ProcessStartInfo(f.LocalPath) { UseShellExecute = true }); } catch { }
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
        void ItemChip_Click(object sender, MouseButtonEventArgs e)
        {
            if (!((sender as FrameworkElement)?.Tag is ItemChipVM chip)) return;
            e.Handled = true;
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
