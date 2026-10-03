using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ElansAddonHub.Services;

namespace ElansAddonHub.Lodge
{
    // The Lodge tab: join screen, people + voice, chat with files.
    public partial class LodgeView : UserControl
    {
        readonly ObservableCollection<MemberVM> members = new ObservableCollection<MemberVM>();
        readonly ObservableCollection<MessageVM> messages = new ObservableCollection<MessageVM>();
        readonly DispatcherTimer tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        readonly Dictionary<int, DateTime> typing = new Dictionary<int, DateTime>();
        Settings settings;
        LodgeClient client;
        VoiceEngine voice;
        int myId;
        string myName;
        int maxFileMb = 25;
        bool inVoice, loading;
        DateTime lastTypingSent;
        int capturingKey; // >0 while waiting for a push-to-talk key

        public event Action<string, string> Notify;   // title, text - shown when the window is hidden
        public event Action<bool> UnreadChanged;
        public Func<bool> IsShownToUser;              // window visible and this tab selected
        public string Status { get; private set; } = "";

        public LodgeView()
        {
            InitializeComponent();
            People.ItemsSource = members;
            Messages.ItemsSource = messages;
            tick.Tick += (s, e) => OnTick();
        }

        public void Init(Settings s)
        {
            settings = s;
            loading = true;
            var fixedUrl = NormalizeUrl(s.LodgeUrl);
            if (fixedUrl != s.LodgeUrl) { s.LodgeUrl = fixedUrl; SettingsStore.Save(s); } // e.g. "https://https://..."
            UrlBox.Text = s.LodgeUrl ?? "";
            NameBox.Text = s.LodgeName ?? Environment.UserName;
            ModeVa.IsChecked = !s.VoicePushToTalk;
            ModePtt.IsChecked = s.VoicePushToTalk;
            ThresholdSlider.Value = s.VoiceThreshold ?? -45;
            VolumeSlider.Value = s.VoiceVolume ?? 1;
            AutoConnectBox.IsChecked = !s.LodgeManualConnect;
            PttKeyButton.Content = VoiceEngine.KeyName(PttKey);
            InputBox.ItemsSource = VoiceEngine.InputDevices();
            OutputBox.ItemsSource = VoiceEngine.OutputDevices();
            InputBox.SelectedItem = ((List<Device>)InputBox.ItemsSource).FirstOrDefault(d => d.Id == (s.VoiceInput ?? -1));
            OutputBox.SelectedItem = ((List<Device>)OutputBox.ItemsSource).FirstOrDefault(d => d.Id == (s.VoiceOutput ?? -1));
            loading = false;
            UpdateLabels();

            var code = SettingsStore.Unprotect(s.LodgeCodeProtected);
            if (!string.IsNullOrEmpty(s.LodgeUrl) && !string.IsNullOrEmpty(code) && !s.LodgeManualConnect) Connect(s.LodgeUrl, code);
        }

        int PttKey => settings.VoicePttKey > 0 ? settings.VoicePttKey : 0x05;

        // ================================================================ connection

        void Join_Click(object sender, RoutedEventArgs e) => TryJoin();
        void CodeBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) TryJoin(); }

        // Forgiving address input: "nasferatu.dk/lodge", "https://https://...", ".../lodge/", ".../lodge/health"
        public static string NormalizeUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return url;
            url = url.Trim();
            var scheme = url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "http" : "https";
            int i;
            while ((i = url.IndexOf("://", StringComparison.Ordinal)) >= 0) url = url.Substring(i + 3);
            url = url.TrimEnd('/');
            foreach (var tail in new[] { "/health", "/ws" })
                if (url.EndsWith(tail, StringComparison.OrdinalIgnoreCase)) url = url.Substring(0, url.Length - tail.Length);
            return scheme + "://" + url.TrimEnd('/');
        }

        bool editing;

        void EditConnection_Click(object sender, RoutedEventArgs e)
        {
            editing = true;
            UrlBox.Text = settings.LodgeUrl ?? "";
            NameBox.Text = settings.LodgeName ?? "";
            CodeBox.Password = "";
            JoinTitle.Text = "Edit connection";
            CodeCaption.Text = "INVITE CODE  (leave empty to keep your current one)";
            JoinButton.Content = "Save and reconnect";
            CancelEditButton.Visibility = Visibility.Visible;
            JoinStatus.Text = "";
            SettingsCard.Visibility = Visibility.Collapsed;
            ChatPanel.Visibility = Visibility.Collapsed;
            JoinPanel.Visibility = Visibility.Visible;
        }

        void CancelEdit_Click(object sender, RoutedEventArgs e)
        {
            EndEdit();
            if (client != null) { JoinPanel.Visibility = Visibility.Collapsed; ChatPanel.Visibility = Visibility.Visible; }
        }

        void EndEdit()
        {
            editing = false;
            JoinTitle.Text = "Join a lodge";
            CodeCaption.Text = "INVITE CODE";
            JoinButton.Content = "Join";
            CancelEditButton.Visibility = Visibility.Collapsed;
        }

        void TryJoin()
        {
            var url = NormalizeUrl(UrlBox.Text);
            UrlBox.Text = url ?? "";
            var code = CodeBox.Password.Trim();
            if (code.Length == 0 && editing) code = SettingsStore.Unprotect(settings.LodgeCodeProtected) ?? ""; // keep the saved code
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != "https" && u.Scheme != "http"))
            {
                JoinStatus.Text = "The address should look like https://example.com/lodge";
                return;
            }
            if (code.Length < 6) { JoinStatus.Text = "Enter your invite code"; return; }
            settings.LodgeUrl = url;
            settings.LodgeCodeProtected = SettingsStore.Protect(code);
            settings.LodgeName = NameBox.Text.Trim();
            SettingsStore.Save(settings);
            CodeBox.Password = "";
            EndEdit();
            Connect(url, code);
        }

        public void Connect(string url, string code)
        {
            Disconnect();
            JoinStatus.Text = "";
            LodgeTitle.Text = "Lodge";
            client = new LodgeClient(url, code, settings.LodgeName ?? Environment.UserName);
            client.Status += OnStatus;
            client.Received += OnReceived;
            client.Voice += (id, data, off, count) => voice?.Incoming(id, data, off, count);
            JoinPanel.Visibility = Visibility.Collapsed;
            ChatPanel.Visibility = Visibility.Visible;
            tick.Start();
            client.Start();
        }

        public void Disconnect()
        {
            LeaveVoice(false);
            client?.Stop();
            client = null;
            members.Clear();
            tick.Stop();
        }

        void OnStatus(string text, bool online)
        {
            Status = text;
            StatusText.Text = online ? $"{members.Count} online" : text;
            StatusDot.Fill = (Brush)FindResource(online ? "Accent" : "TextDim");
            if (!online && client != null && !client.IsRunning)
            {
                // wrong code / removed: back to the join screen, with the address kept for a quick fix
                var why = text;
                Disconnect();
                UrlBox.Text = settings.LodgeUrl ?? "";
                ChatPanel.Visibility = Visibility.Collapsed;
                JoinPanel.Visibility = Visibility.Visible;
                JoinStatus.Text = why;
            }
            if (!online && inVoice) LeaveVoice(false);
        }

        void OnReceived(Dictionary<string, object> m)
        {
            switch (m.Str("t"))
            {
                case "welcome":
                    myId = m.Int("you");
                    myName = m.Str("name");
                    maxFileMb = m.Int("maxFileMb") > 0 ? m.Int("maxFileMb") : 25;
                    try { LodgeTitle.Text = new Uri(client.BaseUrl).Host; } catch { }
                    members.Clear();
                    foreach (var u in m.List("users").OfType<Dictionary<string, object>>()) members.Add(ToMember(u));
                    messages.Clear();
                    foreach (var h in m.List("history").OfType<Dictionary<string, object>>()) AddMessage(h, false);
                    ScrollToEnd();
                    StatusText.Text = $"{members.Count} online";
                    break;
                case "join":
                    var j = ToMember(m.Child("user"));
                    if (members.All(x => x.Id != j.Id)) members.Add(j);
                    StatusText.Text = $"{members.Count} online";
                    break;
                case "leave":
                    var gone = members.FirstOrDefault(x => x.Id == m.Int("id"));
                    if (gone != null) members.Remove(gone);
                    voice?.RemovePeer(m.Int("id"));
                    StatusText.Text = $"{members.Count} online";
                    break;
                case "user":
                    var u2 = m.Child("user");
                    var mem = members.FirstOrDefault(x => x.Id == u2.Int("id"));
                    if (mem != null) { mem.Voice = u2.Bool("voice"); mem.Muted = u2.Bool("muted"); mem.Deaf = u2.Bool("deaf"); }
                    if (mem != null && !mem.Voice) voice?.RemovePeer(mem.Id);
                    break;
                case "msg":
                    var atEnd = MessageScroll.VerticalOffset >= MessageScroll.ScrollableHeight - 40;
                    var vm = AddMessage(m, true);
                    typing.Remove(vm.FromId);
                    if (atEnd || vm.FromId == myId) ScrollToEnd();
                    if (vm.FromId != myId && !(IsShownToUser?.Invoke() ?? true))
                    {
                        UnreadChanged?.Invoke(true);
                        Notify?.Invoke(vm.From, string.IsNullOrEmpty(vm.Text) ? "shared " + vm.File?.Name : vm.Text);
                    }
                    break;
                case "typing":
                    typing[m.Int("id")] = DateTime.UtcNow.AddSeconds(4);
                    break;
                case "error":
                    StatusText.Text = m.Str("text");
                    break;
            }
        }

        MemberVM ToMember(Dictionary<string, object> u) => new MemberVM
        {
            Id = u.Int("id"), Name = u.Str("name"), Guest = u.Bool("guest"), IsMe = u.Int("id") == myId,
            Voice = u.Bool("voice"), Muted = u.Bool("muted"), Deaf = u.Bool("deaf"),
        };

        MessageVM AddMessage(Dictionary<string, object> m, bool live)
        {
            var at = DateTimeOffset.FromUnixTimeMilliseconds(m.Long("at")).UtcDateTime;
            var prev = messages.LastOrDefault();
            var vm = new MessageVM
            {
                Id = m.Str("id"), FromId = m.Int("fromId"), From = m.Str("from"), At = at, Text = m.Str("text"),
                Continuation = prev != null && prev.From == m.Str("from") && (at - prev.At).TotalMinutes < 5,
            };
            var f = m.Child("file");
            if (f != null)
            {
                vm.File = new FileVM { Id = f.Str("id"), Name = f.Str("name"), Size = f.Long("size"), Mime = f.Str("mime") };
                if (vm.File.IsImage) _ = LoadImage(vm.File);
            }
            messages.Add(vm);
            EmptyText.Visibility = Visibility.Collapsed;
            return vm;
        }

        void ScrollToEnd() => Dispatcher.BeginInvoke(new Action(() => MessageScroll.ScrollToEnd()), DispatcherPriority.Background);

        public void MarkRead() => UnreadChanged?.Invoke(false);

        // ================================================================ sending

        void Composer_PreviewKeyDown(object sender, KeyEventArgs e)
        {
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
            if (Composer.Text.Length > 0 && client != null && client.Online && (DateTime.UtcNow - lastTypingSent).TotalSeconds > 3)
            {
                lastTypingSent = DateTime.UtcNow;
                _ = client.Send(new Dictionary<string, object> { ["t"] = "typing" });
            }
        }

        void Send_Click(object sender, RoutedEventArgs e) => SendText();

        void SendText()
        {
            var text = Composer.Text.Trim();
            if (text.Length == 0 || client == null || !client.Online) return;
            _ = client.Send(new Dictionary<string, object> { ["t"] = "msg", ["text"] = text });
            Composer.Text = "";
        }

        public Task SendTextForTest(string text) =>
            client?.Send(new Dictionary<string, object> { ["t"] = "msg", ["text"] = text }) ?? Task.CompletedTask;

        // ================================================================ files

        void Attach_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Share a file with the lodge", Multiselect = true };
            if (dlg.ShowDialog(Window.GetWindow(this)) == true) foreach (var f in dlg.FileNames) _ = Share(f);
        }

        void OnDragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && client != null ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        void OnDrop(object sender, DragEventArgs e)
        {
            if (!(e.Data.GetData(DataFormats.FileDrop) is string[] files) || client == null) return;
            foreach (var f in files.Where(File.Exists)) _ = Share(f);
        }

        public async Task Share(string path)
        {
            if (client == null || !client.Online) return;
            var size = new FileInfo(path).Length;
            if (size > maxFileMb * 1024L * 1024L)
            {
                StatusText.Text = $"{Path.GetFileName(path)} is larger than {maxFileMb} MB";
                return;
            }
            StatusText.Text = $"Uploading {Path.GetFileName(path)}...";
            try
            {
                var meta = await client.Upload(path);
                var text = Composer.Text.Trim();
                Composer.Text = "";
                await client.Send(new Dictionary<string, object>
                {
                    ["t"] = "msg", ["text"] = text, ["file"] = new Dictionary<string, object> { ["id"] = meta.Str("id") },
                });
                StatusText.Text = $"{members.Count} online";
            }
            catch (Exception ex)
            {
                Util.Log("upload: " + ex);
                StatusText.Text = "Upload failed: " + ex.Message;
            }
        }

        static string CacheDir => Directory.CreateDirectory(Path.Combine(Util.DataDir, "cache")).FullName;

        async Task LoadImage(FileVM f)
        {
            if (client == null) return;
            var path = Path.Combine(CacheDir, f.Id + Path.GetExtension(f.Name));
            try
            {
                if (!File.Exists(path)) await client.Download(f.Id, path);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad; // don't keep the file locked
                bmp.DecodePixelWidth = 640;
                bmp.UriSource = new Uri(path);
                bmp.EndInit();
                bmp.Freeze();
                f.LocalPath = path;
                f.Image = bmp;
            }
            catch (Exception e) { Util.Log("image: " + e.Message); }
        }

        void Image_Click(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is FileVM f && f.LocalPath != null)
                try { Process.Start(new ProcessStartInfo(f.LocalPath) { UseShellExecute = true }); } catch { }
        }

        async void Download_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.Tag is FileVM f) || client == null) return;
            var dir = DownloadsFolder();
            var target = Path.Combine(dir, f.Name);
            for (int i = 2; File.Exists(target); i++)
                target = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(f.Name)} ({i}){Path.GetExtension(f.Name)}");
            f.Status = "downloading...";
            try
            {
                await client.Download(f.Id, target);
                f.Status = "saved";
                Process.Start("explorer.exe", $"/select,\"{target}\"");
            }
            catch (Exception ex) { f.Status = "failed"; Util.Log("download: " + ex.Message); }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out string path);

        static string DownloadsFolder()
        {
            try
            {
                if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out var p) == 0) return p;
            }
            catch { }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }

        // ================================================================ voice

        void JoinVoice_Click(object sender, RoutedEventArgs e) => JoinVoice();

        public void JoinVoice()
        {
            if (client == null || !client.Online || inVoice) return;
            voice = new VoiceEngine
            {
                PushToTalk = settings.VoicePushToTalk,
                PttKey = PttKey,
                ThresholdDb = settings.VoiceThreshold ?? -45,
            };
            var c = client;
            voice.Send = (p, n) => _ = c.SendVoice(p, n);
            voice.Start(settings.VoiceInput ?? -1, settings.VoiceOutput ?? -1, (float)(settings.VoiceVolume ?? 1));
            inVoice = true;
            MuteToggle.IsChecked = false;
            DeafToggle.IsChecked = false;
            _ = client.Send(new Dictionary<string, object> { ["t"] = "voice", ["on"] = true });
            _ = client.Send(new Dictionary<string, object> { ["t"] = "state", ["muted"] = false, ["deaf"] = false });
            JoinVoiceButton.Visibility = Visibility.Collapsed;
            VoiceControls.Visibility = Visibility.Visible;
            VoiceNote.Text = voice.MicError != null ? voice.MicError + " - you can still listen." :
                settings.VoicePushToTalk ? $"Hold {VoiceEngine.KeyName(PttKey)} to talk." : "";
            VoiceNote.Visibility = VoiceNote.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        void LeaveVoice_Click(object sender, RoutedEventArgs e) => LeaveVoice(true);

        void LeaveVoice(bool tellServer)
        {
            if (!inVoice) return;
            inVoice = false;
            voice?.Dispose();
            voice = null;
            if (tellServer && client != null) _ = client.Send(new Dictionary<string, object> { ["t"] = "voice", ["on"] = false });
            JoinVoiceButton.Visibility = Visibility.Visible;
            VoiceControls.Visibility = Visibility.Collapsed;
            VoiceNote.Visibility = Visibility.Collapsed;
            foreach (var m in members) m.Speaking = false;
        }

        void Mute_Click(object sender, RoutedEventArgs e) => SendVoiceState();

        void Deaf_Click(object sender, RoutedEventArgs e)
        {
            if (DeafToggle.IsChecked == true) MuteToggle.IsChecked = true; // deafened people can't talk either (like Discord)
            SendVoiceState();
        }

        void SendVoiceState()
        {
            if (voice == null || client == null) return;
            voice.Muted = MuteToggle.IsChecked == true;
            voice.Deafened = DeafToggle.IsChecked == true;
            voice.Volume = voice.Deafened ? 0 : (float)(settings.VoiceVolume ?? 1);
            MuteToggle.Content = voice.Muted ? "" : "";
            _ = client.Send(new Dictionary<string, object> { ["t"] = "state", ["muted"] = voice.Muted, ["deaf"] = voice.Deafened });
        }

        public string VoiceStats => voice == null ? "not in voice" : $"in voice, frames sent {voice.FramesSent}, mic {(voice.MicError ?? "ok")}";

        void OnTick()
        {
            foreach (var m in members)
                m.Speaking = inVoice && m.Voice && (m.IsMe ? voice != null && voice.Transmitting : voice != null && voice.IsSpeaking(m.Id));

            // typing line
            var now = DateTime.UtcNow;
            var who = typing.Where(t => t.Value > now && t.Key != myId)
                            .Select(t => members.FirstOrDefault(x => x.Id == t.Key)?.Name).Where(n => n != null).ToList();
            TypingText.Text = who.Count == 0 ? "" : who.Count == 1 ? $"{who[0]} is typing..." : $"{string.Join(", ", who)} are typing...";

            // mic level under the sensitivity slider
            if (SettingsCard.Visibility == Visibility.Visible && voice != null)
            {
                var frac = Math.Max(0, Math.Min(1, (voice.LevelDb - ThresholdSlider.Minimum) / (ThresholdSlider.Maximum - ThresholdSlider.Minimum)));
                LevelBar.Width = frac * Math.Max(0, ThresholdSlider.ActualWidth);
            }
            else LevelBar.Width = 0;

            if (capturingKey > 0) CaptureKey();
        }

        // ================================================================ settings card

        void VoiceSettings_Click(object sender, RoutedEventArgs e) =>
            SettingsCard.Visibility = SettingsCard.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

        public void ShowSettingsForTest() => SettingsCard.Visibility = Visibility.Visible;

        void Mode_Changed(object sender, RoutedEventArgs e)
        {
            if (settings == null) return;
            settings.VoicePushToTalk = ModePtt.IsChecked == true;
            if (voice != null) voice.PushToTalk = settings.VoicePushToTalk;
            if (!loading) SettingsStore.Save(settings);
            UpdateLabels();
        }

        void Threshold_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (settings == null) return;
            settings.VoiceThreshold = Math.Round(e.NewValue);
            if (voice != null) voice.ThresholdDb = e.NewValue;
            if (!loading) SettingsStore.Save(settings);
            UpdateLabels();
        }

        void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (settings == null) return;
            settings.VoiceVolume = Math.Round(e.NewValue, 2);
            if (voice != null && !voice.Deafened) voice.Volume = (float)e.NewValue;
            if (!loading) SettingsStore.Save(settings);
            UpdateLabels();
        }

        void Device_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (settings == null || loading) return;
            settings.VoiceInput = InputBox.SelectedItem is Device i ? i.Id : -1;
            settings.VoiceOutput = OutputBox.SelectedItem is Device o ? o.Id : -1;
            SettingsStore.Save(settings);
            if (inVoice) { LeaveVoice(true); JoinVoice(); } // restart audio on the new devices
        }

        void AutoConnect_Click(object sender, RoutedEventArgs e)
        {
            settings.LodgeManualConnect = AutoConnectBox.IsChecked != true;
            SettingsStore.Save(settings);
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
                PttKeyButton.Content = VoiceEngine.KeyName(PttKey);
                return;
            }
            for (int vk = 0x02; vk < 0xFF; vk++)
            {
                if (vk == 0x1B || !VoiceEngine.KeyDown(vk)) continue;
                capturingKey = 0;
                settings.VoicePttKey = vk;
                SettingsStore.Save(settings);
                if (voice != null) voice.PttKey = vk;
                PttKeyButton.Content = VoiceEngine.KeyName(vk);
                UpdateLabels();
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

        void SignOut_Click(object sender, RoutedEventArgs e)
        {
            Disconnect();
            settings.LodgeCodeProtected = null;
            SettingsStore.Save(settings);
            SettingsCard.Visibility = Visibility.Collapsed;
            ChatPanel.Visibility = Visibility.Collapsed;
            JoinPanel.Visibility = Visibility.Visible;
            JoinStatus.Text = "";
        }

        // test hooks
        public void FillJoinForTest(string url, string code, string name) { UrlBox.Text = url; CodeBox.Password = code; NameBox.Text = name; TryJoin(); }
        public bool IsOnline => client != null && client.Online;
        public string JoinError => JoinStatus.Text;
        public int MessageCount => messages.Count;
        public int MemberCount => members.Count;
    }
}
