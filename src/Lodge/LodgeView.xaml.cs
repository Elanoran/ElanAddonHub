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

        public event Action OpenSettings;
        public bool ForceOverlayForTest;
        public bool OverlayPreview; // the Settings page shows it while you place it

        public LodgeView()
        {
            InitializeComponent();
            tick.Tick += (s, e) => OnTick();
            dropTimer.Tick += (s, e) => { dropTimer.Stop(); DropOverlay.Visibility = Visibility.Collapsed; };
        }

        public void Init(LodgeSession session)
        {
            Session = session;
            TextList.ItemsSource = session.TextChannels;
            RoomList.ItemsSource = session.VoiceRooms;
            // online list: people not in a voice room (those show under their room)
            var online = new CollectionViewSource { Source = session.Members }.View;
            online.Filter = o => ((MemberVM)o).Room == null;
            if (online is ICollectionViewLiveShaping live && live.CanChangeLiveFiltering)
            {
                live.LiveFilteringProperties.Add(nameof(MemberVM.Room));
                live.IsLiveFiltering = true;
            }
            OnlineList.ItemsSource = online;

            session.Changed += Refresh;
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
            Editor.Saved += vm => { if (Session.SupportsProfile) _ = Session.SaveProfile(vm.ToMessage()); };
            session.Error += text => { ChatNote.Text = text; noteUntil = DateTime.UtcNow.AddSeconds(6); };
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
            OnlineCaption.Text = $"ONLINE - {s.Members.Count(m => m.Room == null)}";

            // the channel on screen
            if (s.Selected != shownChannel)
            {
                shownChannel = s.Selected;
                CancelMode();
                Messages.ItemsSource = s.MessagesOf(s.Selected?.Id);
                ChannelTitle.Text = s.Selected?.Name ?? "";
                EmptyText.Text = $"No messages in #{s.Selected?.Name} yet - say hi!";
                ComposerHint.Text = $"Message #{s.Selected?.Name}";
                PinList.ItemsSource = s.PinsOf(s.Selected?.Id);
                // open at the "New messages" line when there is one, otherwise at the newest message
                if (s.Selected?.Divider != null) ScrollToMessage(s.Selected.Divider, false);
                else ScrollToEnd();
            }
            PinButton.Visibility = s.SupportsPin ? Visibility.Visible : Visibility.Collapsed;
            UpdatePins();
            if (s.Selected != null)
                EmptyText.Visibility = s.MessagesOf(s.Selected.Id).Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            // me
            if (s.Me != null)
            {
                MeAvatar.Content = s.Me;
                MeName.Text = s.Me.Name;
                MeStatus.Text = MemberVM.RoleName(s.MyRole) + " · " +
                    (s.Invisible ? "Invisible" : string.IsNullOrEmpty(s.MyNote) ? MemberVM.StatusLabel(s.MyStatus) : s.MyNote);
            }
            AttachButton.Visibility = s.CanShareFiles ? Visibility.Visible : Visibility.Collapsed;

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

        void OnTick()
        {
            if (Session == null) return;
            TypingText.Text = Session.TypingText(Session.Selected?.Id);
            var online = Session.Members.Count(m => m.Room == null);
            OnlineCaption.Text = $"ONLINE - {online}";
            OnlineCaption.Visibility = online > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (ChatNote.Text.Length > 0 && DateTime.UtcNow > noteUntil) ChatNote.Text = "";
            if (ChatPanel.Visibility == Visibility.Visible && (Session.IsShownToUser?.Invoke() ?? true)) Session.MarkRead(); // only acts at the bottom
            UpdateJump();
            UpdateOverlay();
        }

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

        void MessageScroll_Changed(object sender, ScrollChangedEventArgs e) => UpdateJump();

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
            PinTitle.Text = n == 0 ? "PINNED MESSAGES" : $"PINNED MESSAGES - {n}";
        }

        void PinButton_Click(object sender, RoutedEventArgs e)
        {
            UpdatePins();
            PinPopup.PlacementTarget = PinButton;
            PinPopup.HorizontalOffset = PinButton.ActualWidth - PinBox.Width - 8;
            PinPopup.IsOpen = true;
        }

        void PinRow_Click(object sender, MouseButtonEventArgs e)
        {
            if (!((sender as FrameworkElement)?.Tag is PinVM p)) return;
            PinPopup.IsOpen = false;
            JumpTo(p.Id);
        }

        void PinRemove_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if ((sender as FrameworkElement)?.Tag is PinVM p && Session.Selected != null) _ = Session.UnpinMessage(p.Id, Session.Selected.Id);
        }

        // scroll to a message in this channel and flash it; if it has left the loaded history say so
        void JumpTo(string id)
        {
            var target = id == null ? null : Session.MessagesOf(Session.Selected?.Id).FirstOrDefault(m => m.Id == id);
            if (target != null) { ScrollToMessage(target, true); return; }
            ChatNote.Text = "That message is older than what is loaded here";
            noteUntil = DateTime.UtcNow.AddSeconds(4);
        }

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
            PopReset.Visibility = Session.CanManage && !m.IsMe && m.HasAvatar ? Visibility.Visible : Visibility.Collapsed;
            MemberPopup.PlacementTarget = target;
            MemberPopup.IsOpen = true;
            Session.RequestProfile(m.Name);
        }

        void OnProfileReceived(ProfileData pd)
        {
            if (popCard == null || !MemberPopup.IsOpen || !string.Equals(pd.Name, popCard.Member.Name, StringComparison.OrdinalIgnoreCase)) return;
            popCard.Loading = false;
            if (popCard.Member.Offline && pd.Found) { popCard.Member.Role = pd.Role; popCard.Member.SetAvatar(pd.AvatarId, pd.Accent); popCard.Member.Offline = !pd.Online; }
            popCard.Data = pd;
            PopReset.Visibility = Session.CanManage && !popCard.Member.IsMe && popCard.Member.HasAvatar ? Visibility.Visible : Visibility.Collapsed;
        }

        void PopMention_Click(object sender, RoutedEventArgs e)
        {
            MemberPopup.IsOpen = false;
            if (popMember != null) InsertMention(popMember.Name);
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
            if (Dialog.Confirm(Window.GetWindow(this), $"Reset {popMember.Name}'s avatar?", "Their avatar and accent go back to the default look. They can pick a new one afterwards.", "Reset", danger: true))
                Session.ResetProfile(popMember.Name);
        }

        // ---- my profile
        void MeAvatar_Click(object sender, MouseButtonEventArgs e) { OpenProfileEditor(); e.Handled = true; }
        void EditProfile_Click(object sender, RoutedEventArgs e) { StatusPopup.IsOpen = false; OpenProfileEditor(); }

        public void OpenProfileEditor()
        {
            if (Session?.Me == null) return;
            Editor.Open(new ProfileEditVM(Session));
        }

        public ProfileEditor EditorForTest => Editor;
        public void ShowProfileCardByNameForTest(string name, UIElement target) => ShowProfileCardByName(name, target);
        public ProfileCardVM PopCardForTest => popCard;
        public FrameworkElement PopupCardForTest => (FrameworkElement)MemberPopup.Child;
        public void CloseProfileForTest() { MemberPopup.IsOpen = false; Editor.Close(); }

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
            if (Dialog.Confirm(Window.GetWindow(this), $"Kick {popMember.Name}?", "They are removed from the lodge right now, but can join again with their code.", "Kick", danger: true))
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
            ModeText.Text = $"Replying to {m.From}  -  {snip}";
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
            var whose = m.Mine ? "your message" : $"{m.From}'s message";
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
            try { if (!string.IsNullOrEmpty(menuMsg?.Text)) Clipboard.SetText(menuMsg.Text); } catch { }
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
            if (!Session.Online) return;
            if (editing != null)
            {
                if (text.Length > 0 && text != editing.Text) _ = Session.Edit(editing.Id, text);
                Composer.Text = "";
                CancelMode();
                return;
            }
            if (text.Length == 0) return;
            _ = Session.SendMessage(text, replyTo?.Id);
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

        public async Task Share(string path, bool deleteAfter = false)
        {
            ChatNote.Text = $"Uploading {Path.GetFileName(path)}...";
            noteUntil = DateTime.UtcNow.AddMinutes(5);
            try
            {
                var text = Composer.Text.Trim();
                await Session.Share(path, text, replyTo?.Id);
                if (editing == null) { Composer.Text = ""; CancelMode(); }
                ChatNote.Text = "";
            }
            catch (Exception ex)
            {
                Util.Log("upload: " + ex.Message);
                ChatNote.Text = ex is InvalidOperationException ? ex.Message : "Upload failed: " + ex.Message;
                noteUntil = DateTime.UtcNow.AddSeconds(8);
            }
            finally
            {
                if (deleteAfter) try { File.Delete(path); Directory.Delete(Path.GetDirectoryName(path)); } catch { }
            }
        }

        void Image_Click(object sender, MouseButtonEventArgs e)
        {
            if (!((sender as FrameworkElement)?.Tag is FileVM f) || f.LocalPath == null) return;
            var list = Session?.MessagesOf(Session.Selected?.Id);
            var msg = list?.FirstOrDefault(m => m.File == f);
            if (msg != null) Viewer.Open(list, msg);
            else try { Process.Start(new ProcessStartInfo(f.LocalPath) { UseShellExecute = true }); } catch { }
        }

        public ImageViewer ViewerForTest => Viewer;

        async void Download_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.Tag is FileVM f)) return;
            var dir = DownloadsFolder();
            var target = Path.Combine(dir, f.Name);
            for (int i = 2; File.Exists(target); i++)
                target = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(f.Name)} ({i}){Path.GetExtension(f.Name)}");
            f.Status = "downloading...";
            try
            {
                await Session.Download(f, target);
                f.Status = "saved";
                Process.Start("explorer.exe", $"/select,\"{target}\"");
            }
            catch (Exception ex) { f.Status = "failed"; Util.Log("download: " + ex.Message); }
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
