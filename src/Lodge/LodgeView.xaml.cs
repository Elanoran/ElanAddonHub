using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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

        public event Action OpenSettings;
        public bool ForceOverlayForTest;
        public bool OverlayPreview; // the Settings page shows it while you place it

        public LodgeView()
        {
            InitializeComponent();
            tick.Tick += (s, e) => OnTick();
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
            session.MessageAdded += vm =>
            {
                if (vm.Channel != session.Selected?.Id) return;
                EmptyText.Visibility = Visibility.Collapsed;
                if (vm.Mine || MessageScroll.VerticalOffset >= MessageScroll.ScrollableHeight - 60) ScrollToEnd();
            };
            session.Stopped += why => ShowJoin(why);
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
                ScrollToEnd();
            }
            if (s.Selected != null)
                EmptyText.Visibility = s.MessagesOf(s.Selected.Id).Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            // me
            if (s.Me != null)
            {
                MeAvatar.Content = s.Me;
                MeName.Text = s.Me.Name;
                MeStatus.Text = MemberVM.RoleName(s.MyRole) + " · " +
                    (string.IsNullOrEmpty(s.Settings.LodgeNote) ? MemberVM.StatusLabel(s.MyStatus) : s.Settings.LodgeNote);
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
            UpdateOverlay();
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
            StatusPopup.PlacementTarget = MeRow;
            StatusPopup.IsOpen = true;
            e.Handled = true;
        }

        void StatusOption_Click(object sender, RoutedEventArgs e)
        {
            Session.SetMyStatus((sender as FrameworkElement)?.Tag as string ?? "online", NoteBox.Text);
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

        // ---- a person: volume, officer tools
        void Member_Click(object sender, MouseButtonEventArgs e)
        {
            if (!((sender as FrameworkElement)?.Tag is MemberVM m)) return;
            if (m.IsMe) { Me_Click(sender, e); return; }
            popMember = m;
            popLoading = true;
            PopName.Text = m.Name;
            PopRole.Text = m.RoleLabel;
            PopRole.Foreground = m.FrameBrush;
            PopStatus.Text = m.StatusLine + (m.ServerMuted ? " - muted by an officer" : "");
            PopVolume.Value = Session.PeerVolumeFor(m.Name);
            PopVolumeText.Text = $"{PopVolume.Value * 100:0}%";
            popLoading = false;
            PopModArea.Visibility = Session.CanModerateMember(m) ? Visibility.Visible : Visibility.Collapsed;
            PopMute.Content = m.ServerMuted ? "Unmute for everyone" : "Mute for everyone";
            MemberPopup.PlacementTarget = (UIElement)sender;
            MemberPopup.IsOpen = true;
            e.Handled = true;
        }

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
            if (MessageBox.Show(Window.GetWindow(this), $"Kick {popMember.Name} from the lodge? They can join again with their code.",
                    "Kick", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
                Session.Kick(popMember);
        }

        // ================================================================ messages

        void Reply_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.Tag is MessageVM m)) return;
            editing = null;
            replyTo = m;
            ModeText.Text = $"Replying to {m.From}";
            ModeBar.Visibility = Visibility.Visible;
            Composer.Focus();
        }

        void Edit_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is MessageVM m) StartEdit(m);
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

        void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.Tag is MessageVM m)) return;
            var whose = m.Mine ? "your message" : $"{m.From}'s message";
            if (MessageBox.Show(Window.GetWindow(this), $"Delete {whose}?", "Delete", MessageBoxButton.YesNo,
                    MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
                _ = Session.Delete(m.Id);
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
                if (Clipboard.ContainsImage())
                {
                    var img = Clipboard.GetImage();
                    var path = Path.Combine(Path.GetTempPath(), $"screenshot-{DateTime.Now:yyyyMMdd-HHmmss}.png");
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(img));
                    using (var fs = File.Create(path)) enc.Save(fs);
                    _ = Share(path);
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

        void OnDragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && Session.Online && Session.CanShareFiles ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        void OnDrop(object sender, DragEventArgs e)
        {
            if (!(e.Data.GetData(DataFormats.FileDrop) is string[] files)) return;
            foreach (var f in files.Where(File.Exists)) _ = Share(f);
        }

        public async Task Share(string path)
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
                Util.Log("upload: " + ex);
                ChatNote.Text = "Upload failed: " + ex.Message;
                noteUntil = DateTime.UtcNow.AddSeconds(8);
            }
        }

        void Image_Click(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is FileVM f && f.LocalPath != null)
                try { Process.Start(new ProcessStartInfo(f.LocalPath) { UseShellExecute = true }); } catch { }
        }

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

        void UpdateOverlay()
        {
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
        public void FillJoinForTest(string url, string code, string name) { UrlBox.Text = url; CodeBox.Password = code; NameBox.Text = name; TryJoin(); }
        public string JoinError => JoinStatus.Text;
    }
}
