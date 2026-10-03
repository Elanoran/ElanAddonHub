using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ElansAddonHub.Services;

namespace ElansAddonHub.Lodge
{
    // Everything about the lodge except drawing it: connection, channels, people, messages, voice,
    // status, admin requests. The views bind to its collections and call its methods.
    public class LodgeSession
    {
        public readonly Settings Settings;
        public readonly ObservableCollection<MemberVM> Members = new ObservableCollection<MemberVM>();
        public readonly ObservableCollection<ChannelVM> TextChannels = new ObservableCollection<ChannelVM>();
        public readonly ObservableCollection<ChannelVM> VoiceRooms = new ObservableCollection<ChannelVM>();
        readonly Dictionary<string, ObservableCollection<MessageVM>> messages = new Dictionary<string, ObservableCollection<MessageVM>>();
        readonly Dictionary<string, Dictionary<int, DateTime>> typing = new Dictionary<string, Dictionary<int, DateTime>>();
        readonly DispatcherTimer tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        LodgeClient client;
        VoiceEngine voice;
        int tickCount;

        // ---- what the views read
        public string BaseUrl => client?.BaseUrl;
        public bool Online => client != null && client.Online;
        public bool Connected => client != null;
        public MemberVM Me { get; private set; }
        public string MyRole { get; private set; } = "member";
        public bool CanShareFiles { get; private set; } = true;
        public bool CanModerate { get; private set; }
        public bool CanManage { get; private set; }
        public string ServerVersion { get; private set; }
        public int MaxFileMb { get; private set; } = 25;
        public ChannelVM Selected { get; private set; }
        public string MyRoom { get; private set; }
        public string StatusText { get; private set; } = "";
        public VoiceEngine Voice => voice;

        // ---- what the views listen to
        public event Action Changed;                       // status / channels / capabilities - refresh chrome
        public event Action<MessageVM> MessageAdded;       // new live message in any channel
        public event Action<string> Stopped;               // disconnected for good (wrong code, kicked, removed)
        public event Action<string, string> Notify;        // title, text - the window decides how
        public event Action<bool> UnreadChanged;
        public event Action<string> Error;
        public event Action<List<Dictionary<string, object>>, List<Dictionary<string, object>>> AdminMembers; // members, channels
        public event Action<string, string, string> AdminInvited; // name, role, code
        public Func<bool> IsShownToUser;                   // the Lodge tab is visible and the window active

        public readonly GamePresence Presence;

        public LodgeSession(Settings settings)
        {
            Settings = settings;
            Presence = new GamePresence(() => settings.WowRoot);
            Presence.Changed += SendGame;
            Presence.Start();
            tick.Tick += (s, e) => OnTick();
            Sounds.Off = settings.SoundsOff;
        }

        public ObservableCollection<MessageVM> MessagesOf(string channel)
        {
            if (channel == null) return new ObservableCollection<MessageVM>();
            if (!messages.TryGetValue(channel, out var l)) messages[channel] = l = new ObservableCollection<MessageVM>();
            return l;
        }

        // ================================================================ invite links

        // "https://site/lodge#invite=CODE" (the part after # never reaches any server or log) or just an address
        public static string ParseInvite(string text, out string code)
        {
            code = null;
            text = (text ?? "").Trim();
            var hash = text.IndexOf('#');
            if (hash >= 0)
            {
                var frag = text.Substring(hash + 1);
                var m = Regex.Match(frag, @"(?:^|&)invite=([A-Za-z0-9]+)");
                if (m.Success) code = m.Groups[1].Value;
                text = text.Substring(0, hash);
            }
            return NormalizeUrl(text);
        }

        public static string InviteLink(string baseUrl, string code) => baseUrl.TrimEnd('/') + "#invite=" + code;

        // forgiving input: "site.dk/lodge", "https://https://...", ".../lodge/", ".../lodge/health"
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

        // ================================================================ connection

        public void Connect(string url, string code)
        {
            Disconnect();
            client = new LodgeClient(url, code, Settings.LodgeName ?? Environment.UserName);
            client.Status += (text, online) =>
            {
                StatusText = online ? OnlineText : text;
                if (!online && client != null && !client.IsRunning)
                {
                    var why = text;
                    Disconnect();
                    Stopped?.Invoke(why);
                }
                if (!online) LeaveVoice(false);
                Changed?.Invoke();
            };
            client.Received += OnReceived;
            client.Voice += (id, data, off, count) => voice?.Incoming(id, data, off, count);
            tick.Start();
            client.Start();
            Changed?.Invoke();
        }

        public void Disconnect()
        {
            LeaveVoice(false);
            client?.Stop();
            client = null;
            Members.Clear();
            foreach (var r in VoiceRooms) r.Members.Clear();
            tick.Stop();
            Changed?.Invoke();
        }

        string OnlineText => $"{Members.Count} online";

        // ================================================================ incoming

        void OnReceived(Dictionary<string, object> m)
        {
            switch (m.Str("t"))
            {
                case "welcome": Welcome(m); break;
                case "join":
                    var j = ParseMember(m.Child("user"));
                    if (Members.All(x => x.Id != j.Id)) Members.Add(j);
                    SyncRooms(joined: j);
                    StatusText = OnlineText;
                    Changed?.Invoke();
                    break;
                case "leave":
                    var gone = Members.FirstOrDefault(x => x.Id == m.Int("id"));
                    if (gone != null)
                    {
                        if (gone.Room != null && gone.Room == MyRoom) Sounds.Leave();
                        Members.Remove(gone);
                    }
                    voice?.RemovePeer(m.Int("id"));
                    SyncRooms();
                    StatusText = OnlineText;
                    Changed?.Invoke();
                    break;
                case "user":
                    var u = ParseMember(m.Child("user"));
                    var mem = Members.FirstOrDefault(x => x.Id == u.Id);
                    if (mem == null) break;
                    var oldRoom = mem.Room;
                    mem.Room = u.Room; mem.Muted = u.Muted; mem.Deaf = u.Deaf; mem.ServerMuted = u.ServerMuted;
                    mem.Status = u.Status; mem.Note = u.Note; mem.Role = u.Role;
                    ApplyGame(mem, m.Child("user").Child("game"));
                    if (!mem.Voice) voice?.RemovePeer(mem.Id);
                    if (!mem.IsMe && MyRoom != null && oldRoom != mem.Room)
                    {
                        if (mem.Room == MyRoom) Sounds.Join();
                        else if (oldRoom == MyRoom) Sounds.Leave();
                    }
                    if (mem.IsMe)
                    {
                        if (mem.ServerMuted != (voice?.Muted ?? false) && mem.ServerMuted) Error?.Invoke("An officer muted you");
                        // (a removed room arrives as a fresh welcome; a stale "no room" update here must not kick me out)
                    }
                    SyncRooms();
                    break;
                case "msg":
                    var vm = AddMessage(m);
                    TypingMap(vm.Channel).Remove(vm.FromId);
                    OnNewMessage(vm);
                    break;
                case "edited":
                    var e = MessagesOf(m.Str("channel")).FirstOrDefault(x => x.Id == m.Str("id"));
                    if (e != null) { e.Text = m.Str("text"); e.Edited = true; e.Mentioned = MentionsMe(e.Text) && !e.Mine; }
                    break;
                case "deleted":
                    var list = MessagesOf(m.Str("channel"));
                    var d = list.FirstOrDefault(x => x.Id == m.Str("id"));
                    if (d != null) list.Remove(d);
                    break;
                case "typing":
                    TypingMap(m.Str("channel") ?? TextChannels.FirstOrDefault()?.Id)[m.Int("id")] = DateTime.UtcNow.AddSeconds(4);
                    break;
                case "error":
                    Error?.Invoke(m.Str("text"));
                    break;
                case "admin.members":
                    AdminMembers?.Invoke(m.List("members").OfType<Dictionary<string, object>>().ToList(),
                                         m.List("channels").OfType<Dictionary<string, object>>().ToList());
                    break;
                case "admin.invited":
                    AdminInvited?.Invoke(m.Str("name"), m.Str("role"), m.Str("code"));
                    break;
            }
        }

        void Welcome(Dictionary<string, object> m)
        {
            int myId = m.Int("you");
            ServerVersion = m.Str("server");
            MyRole = m.Str("role") ?? "member";
            MaxFileMb = m.Int("maxFileMb") > 0 ? m.Int("maxFileMb") : 25;
            CanShareFiles = !m.ContainsKey("canShareFiles") || m.Bool("canShareFiles");
            CanModerate = m.Bool("canModerate");
            CanManage = m.Bool("canManage");

            Members.Clear();
            foreach (var u in m.List("users").OfType<Dictionary<string, object>>())
            {
                var mem = ParseMember(u);
                mem.IsMe = mem.Id == myId;
                Members.Add(mem);
            }
            Me = Members.FirstOrDefault(x => x.IsMe);

            // channels: a 1.x server has none - pretend it has one text channel and one room
            var chans = m.List("channels").OfType<Dictionary<string, object>>().ToList();
            if (chans.Count == 0)
                chans = new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object> { ["id"] = "general", ["name"] = "General", ["type"] = "text" },
                    new Dictionary<string, object> { ["id"] = "voice", ["name"] = "Voice", ["type"] = "voice" },
                };
            var keepSelected = Selected?.Id ?? Settings.LastChannel;
            TextChannels.Clear();
            VoiceRooms.Clear();
            foreach (var c in chans)
            {
                var ch = new ChannelVM { Id = c.Str("id"), Name = c.Str("name"), Type = c.Str("type") ?? "text", MinRole = c.Str("minRole"), Max = c.Int("max") };
                (ch.IsVoice ? VoiceRooms : TextChannels).Add(ch);
            }

            // history: per channel (2.x) or one list (1.x)
            foreach (var key in messages.Keys.ToList()) messages[key].Clear();
            if (m.TryGetValue("history", out var h) && h is Dictionary<string, object> perChannel)
            {
                foreach (var kv in perChannel)
                    foreach (var x in (kv.Value as List<object> ?? new List<object>()).OfType<Dictionary<string, object>>())
                    {
                        if (!x.ContainsKey("channel")) x["channel"] = kv.Key;
                        AddMessage(x);
                    }
            }
            else foreach (var x in m.List("history").OfType<Dictionary<string, object>>()) AddMessage(x);

            Select(TextChannels.FirstOrDefault(c => c.Id == keepSelected) ?? TextChannels.FirstOrDefault());
            if (MyRoom != null && VoiceRooms.All(r => r.Id != MyRoom)) LeaveVoice(false);
            else if (MyRoom != null) SendVoice(MyRoom); // reconnected while in voice
            SyncRooms();
            StatusText = OnlineText;
            if (MyStatus != "online" || !string.IsNullOrEmpty(Settings.LodgeNote)) SendStatus();
            SendGame();
            Changed?.Invoke();
        }

        MemberVM ParseMember(Dictionary<string, object> u)
        {
            var room = u.Str("room");
            if (room == null && u.Bool("voice")) room = "voice"; // 1.x server: one voice channel
            var m = new MemberVM
            {
                Id = u.Int("id"), Name = u.Str("name"), Guest = u.Bool("guest"), IsMe = Me != null && u.Int("id") == Me.Id,
                Room = room, Muted = u.Bool("muted"), Deaf = u.Bool("deaf"), ServerMuted = u.Bool("serverMuted"),
                Status = u.Str("status"), Note = u.Str("note"), Role = u.Str("role"),
            };
            ApplyGame(m, u.Child("game"));
            return m;
        }

        static void ApplyGame(MemberVM m, Dictionary<string, object> g) =>
            m.SetGame(g != null && g.Bool("playing"), g?.Str("name"), g?.Str("class"), g?.Str("classFile"), g?.Int("level") ?? 0, g?.Str("zone"), g?.Str("guild"));

        // rich presence: WoW running + the character from the Elan's Hub addon (unless sharing is off)
        public void SendGame()
        {
            if (!Online) return;
            if (Settings.ShareGameOff) { _ = Send(new Dictionary<string, object> { ["t"] = "game", ["share"] = false }); return; }
            var c = Presence.Current;
            var obj = new Dictionary<string, object> { ["t"] = "game", ["playing"] = Presence.Playing };
            if (c != null)
            {
                obj["name"] = c.Name; obj["realm"] = c.Realm; obj["class"] = c.Class; obj["classFile"] = c.ClassFile;
                obj["level"] = c.Level; obj["zone"] = c.Zone; obj["guild"] = c.Guild;
            }
            _ = Send(obj);
        }

        // keep each voice room's member list (and my overlay) in step with everyone's Room
        void SyncRooms(MemberVM joined = null)
        {
            foreach (var r in VoiceRooms)
            {
                var want = Members.Where(x => x.Room == r.Id).ToList();
                foreach (var x in r.Members.Where(x => !want.Contains(x)).ToList()) r.Members.Remove(x);
                foreach (var x in want.Where(x => !r.Members.Contains(x))) r.Members.Add(x);
                r.IAmHere = r.Id == MyRoom;
                r.RefreshCount();
            }
            foreach (var x in Members) x.InMyRoom = MyRoom != null && x.Room == MyRoom;
        }

        // ================================================================ messages

        MessageVM AddMessage(Dictionary<string, object> m)
        {
            var channel = m.Str("channel") ?? TextChannels.FirstOrDefault()?.Id ?? "general";
            var list = MessagesOf(channel);
            var at = DateTimeOffset.FromUnixTimeMilliseconds(m.Long("at")).UtcDateTime;
            var prev = list.LastOrDefault();
            var from = m.Str("from");
            var reply = m.Child("replyTo");
            var vm = new MessageVM
            {
                Id = m.Str("id"), Channel = channel, FromId = m.Int("fromId"), From = from, At = at,
                Text = m.Str("text"), Edited = m.Bool("edited"),
                Mine = Me != null && string.Equals(from, Me.Name, StringComparison.OrdinalIgnoreCase),
                CanDelete = CanModerate,
                ReplyFrom = reply?.Str("from"), ReplyText = reply?.Str("text"),
                Continuation = prev != null && prev.From == from && (at - prev.At).TotalMinutes < 5,
            };
            vm.Mentioned = !vm.Mine && MentionsMe(vm.Text);
            var f = m.Child("file");
            if (f != null)
            {
                vm.File = new FileVM { Id = f.Str("id"), Name = f.Str("name"), Size = f.Long("size"), Mime = f.Str("mime") };
                if (vm.File.IsImage) _ = LoadImage(vm.File);
            }
            list.Add(vm);
            return vm;
        }

        bool MentionsMe(string text)
        {
            if (string.IsNullOrEmpty(text) || Me == null) return false;
            return Regex.IsMatch(text, @"@(" + Regex.Escape(Me.Name) + @"|everyone|here)(?![\w])", RegexOptions.IgnoreCase);
        }

        void OnNewMessage(MessageVM vm)
        {
            MessageAdded?.Invoke(vm);
            if (vm.Mine) return;
            var ch = TextChannels.FirstOrDefault(c => c.Id == vm.Channel);
            bool looking = ch == Selected && (IsShownToUser?.Invoke() ?? true);
            if (!looking && ch != null)
            {
                ch.Unread = true;
                if (vm.Mentioned) ch.Mentions++;
                UnreadChanged?.Invoke(true);
            }
            var mode = Settings.NotifyMode ?? "mentions";
            if (vm.Mentioned && mode != "none") Sounds.Mention();
            if (!looking && (mode == "all" || (mode == "mentions" && vm.Mentioned)))
                Notify?.Invoke($"{vm.From} in #{ch?.Name}", string.IsNullOrEmpty(vm.Text) ? "shared " + vm.File?.Name : vm.Text);
        }

        public void Select(ChannelVM ch)
        {
            if (ch == null || ch.IsVoice) return;
            foreach (var c in TextChannels) c.Selected = c == ch;
            Selected = ch;
            ch.Unread = false;
            ch.Mentions = 0;
            Settings.LastChannel = ch.Id;
            UnreadChanged?.Invoke(TextChannels.Any(c => c.Unread));
            Changed?.Invoke();
        }

        public void MarkRead()
        {
            if (Selected != null) { Selected.Unread = false; Selected.Mentions = 0; }
            UnreadChanged?.Invoke(TextChannels.Any(c => c.Unread));
        }

        Dictionary<int, DateTime> TypingMap(string channel)
        {
            if (!typing.TryGetValue(channel ?? "", out var t)) typing[channel ?? ""] = t = new Dictionary<int, DateTime>();
            return t;
        }

        public string TypingText(string channel)
        {
            var now = DateTime.UtcNow;
            var who = TypingMap(channel).Where(t => t.Value > now && (Me == null || t.Key != Me.Id))
                .Select(t => Members.FirstOrDefault(x => x.Id == t.Key)?.Name).Where(n => n != null).ToList();
            return who.Count == 0 ? "" : who.Count == 1 ? $"{who[0]} is typing..." : $"{string.Join(", ", who)} are typing...";
        }

        Task Send(Dictionary<string, object> obj) => client?.Send(obj) ?? Task.CompletedTask;

        public Task SendMessage(string text, string replyTo = null, string fileId = null)
        {
            var obj = new Dictionary<string, object> { ["t"] = "msg", ["channel"] = Selected?.Id, ["text"] = text ?? "" };
            if (replyTo != null) obj["replyTo"] = replyTo;
            if (fileId != null) obj["file"] = new Dictionary<string, object> { ["id"] = fileId };
            return Send(obj);
        }

        public Task Edit(string id, string text) => Send(new Dictionary<string, object> { ["t"] = "edit", ["id"] = id, ["text"] = text });
        public Task Delete(string id) => Send(new Dictionary<string, object> { ["t"] = "delete", ["id"] = id });

        DateTime lastTyping;
        public void Typing()
        {
            if (!Online || (DateTime.UtcNow - lastTyping).TotalSeconds < 3) return;
            lastTyping = DateTime.UtcNow;
            _ = Send(new Dictionary<string, object> { ["t"] = "typing", ["channel"] = Selected?.Id });
        }

        // ================================================================ files

        public async Task Share(string path, string text, string replyTo)
        {
            if (!Online) return;
            if (!CanShareFiles) throw new InvalidOperationException("Initiates can't share files");
            if (new FileInfo(path).Length > MaxFileMb * 1024L * 1024L)
                throw new InvalidOperationException($"{Path.GetFileName(path)} is larger than {MaxFileMb} MB");
            var meta = await client.Upload(path);
            await SendMessage(text, replyTo, meta.Str("id"));
        }

        static string CacheDir => Directory.CreateDirectory(Path.Combine(Util.DataDir, "cache")).FullName;

        async Task LoadImage(FileVM f)
        {
            var path = Path.Combine(CacheDir, f.Id + Path.GetExtension(f.Name));
            try
            {
                if (!File.Exists(path)) { if (client == null) return; await client.Download(f.Id, path); }
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

        public Task Download(FileVM f, string target) => client.Download(f.Id, target);

        // ================================================================ voice

        public void JoinRoom(string room)
        {
            if (!Online || room == null) return;
            if (voice == null)
            {
                voice = new VoiceEngine
                {
                    PushToTalk = Settings.VoicePushToTalk,
                    PttKey = PttKey,
                    ThresholdDb = Settings.VoiceThreshold ?? -45,
                    PeerVolume = id => PeerVolumeFor(Members.FirstOrDefault(x => x.Id == id)?.Name),
                };
                var c = client;
                voice.Send = (p, n) => _ = c.SendVoice(p, n);
                voice.Start(Settings.VoiceInput ?? -1, Settings.VoiceOutput ?? -1, (float)(Settings.VoiceVolume ?? 1));
                voice.Muted = false;
                voice.Deafened = false;
            }
            MyRoom = room;
            SendVoice(room);
            _ = Send(new Dictionary<string, object> { ["t"] = "state", ["muted"] = voice.Muted, ["deaf"] = voice.Deafened });
            Sounds.Join();
            SyncRooms();
            Changed?.Invoke();
        }

        void SendVoice(string room) =>
            _ = Send(new Dictionary<string, object> { ["t"] = "voice", ["room"] = room, ["on"] = true }); // "on" for 1.x servers

        public void LeaveVoice(bool tellServer = true)
        {
            if (MyRoom == null && voice == null) return;
            MyRoom = null;
            voice?.Dispose();
            voice = null;
            if (tellServer && client != null) _ = Send(new Dictionary<string, object> { ["t"] = "voice", ["room"] = null, ["on"] = false });
            if (tellServer) Sounds.Leave();
            foreach (var x in Members) x.Speaking = false;
            SyncRooms();
            Changed?.Invoke();
        }

        public void SetMuteDeaf(bool muted, bool deaf)
        {
            if (voice == null) return;
            voice.Muted = muted || deaf; // deafened people can't talk either
            voice.Deafened = deaf;
            voice.Volume = deaf ? 0 : (float)(Settings.VoiceVolume ?? 1);
            _ = Send(new Dictionary<string, object> { ["t"] = "state", ["muted"] = voice.Muted, ["deaf"] = deaf });
            Changed?.Invoke();
        }

        public int PttKey => Settings.VoicePttKey > 0 ? Settings.VoicePttKey : 0x05;

        public float PeerVolumeFor(string name) =>
            name != null && Settings.PeerVolumes != null && Settings.PeerVolumes.TryGetValue(name.ToLowerInvariant(), out var v) ? (float)v : 1f;

        public void SetPeerVolume(MemberVM m, double v)
        {
            Settings.PeerVolumes = Settings.PeerVolumes ?? new Dictionary<string, double>();
            Settings.PeerVolumes[m.Name.ToLowerInvariant()] = Math.Round(v, 2);
            SettingsStore.Save(Settings);
            voice?.SetPeerVolume(m.Id, (float)v);
        }

        // voice settings changed in the Settings page: apply to a running engine
        public void ApplyVoiceSettings(bool restartDevices)
        {
            Sounds.Off = Settings.SoundsOff;
            if (voice == null) return;
            if (restartDevices)
            {
                var room = MyRoom;
                LeaveVoice(true);
                JoinRoom(room);
                return;
            }
            voice.PushToTalk = Settings.VoicePushToTalk;
            voice.PttKey = PttKey;
            voice.ThresholdDb = Settings.VoiceThreshold ?? -45;
            if (!voice.Deafened) voice.Volume = (float)(Settings.VoiceVolume ?? 1);
        }

        void OnTick()
        {
            tickCount++;
            foreach (var m in Members)
                m.Speaking = MyRoom != null && m.Room == MyRoom && voice != null && (m.IsMe ? voice.Transmitting : voice.IsSpeaking(m.Id));
            if (tickCount % 50 == 0) CheckIdle();
        }

        // ================================================================ status

        bool autoAway;
        public string MyStatus => autoAway ? "away" : string.IsNullOrEmpty(Settings.LodgeStatus) ? "online" : Settings.LodgeStatus;

        public void SetMyStatus(string status, string note)
        {
            autoAway = false;
            Settings.LodgeStatus = status;
            Settings.LodgeNote = (note ?? "").Trim();
            SettingsStore.Save(Settings);
            SendStatus();
        }

        void SendStatus() =>
            _ = Send(new Dictionary<string, object> { ["t"] = "status", ["status"] = MyStatus, ["note"] = Settings.LodgeNote ?? "" });

        [StructLayout(LayoutKind.Sequential)] struct LastInput { public uint cbSize, dwTime; }
        [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LastInput li);

        static double IdleSeconds()
        {
            var li = new LastInput { cbSize = (uint)Marshal.SizeOf(typeof(LastInput)) };
            return GetLastInputInfo(ref li) ? unchecked((uint)Environment.TickCount - li.dwTime) / 1000.0 : 0;
        }

        // AFK after 10 minutes without input (only from Online); back on the first input
        void CheckIdle()
        {
            if (!Online) return;
            var idle = IdleSeconds();
            var chosen = string.IsNullOrEmpty(Settings.LodgeStatus) ? "online" : Settings.LodgeStatus;
            if (!Settings.AutoAwayOff && !autoAway && chosen == "online" && idle > 600) { autoAway = true; SendStatus(); }
            else if (autoAway && (idle < 5 || Settings.AutoAwayOff)) { autoAway = false; SendStatus(); }
        }

        // ================================================================ officers and the guild master

        public void RequestAdmin() => _ = Send(new Dictionary<string, object> { ["t"] = "admin.members" });
        public void Invite(string name, string role) => _ = Send(new Dictionary<string, object> { ["t"] = "admin.invite", ["name"] = name, ["role"] = role });
        public void SetRole(string name, string role) => _ = Send(new Dictionary<string, object> { ["t"] = "admin.role", ["name"] = name, ["role"] = role });
        public void RemoveMember(string name) => _ = Send(new Dictionary<string, object> { ["t"] = "admin.remove", ["name"] = name });
        public void AddChannel(string name, string type, string minRole, int max) =>
            _ = Send(new Dictionary<string, object> { ["t"] = "admin.channel.add", ["name"] = name, ["type"] = type, ["minRole"] = minRole, ["max"] = max });
        public void RemoveChannel(string id) => _ = Send(new Dictionary<string, object> { ["t"] = "admin.channel.remove", ["id"] = id });
        public void Kick(MemberVM m) => _ = Send(new Dictionary<string, object> { ["t"] = "mod.kick", ["id"] = m.Id });
        public void ServerMute(MemberVM m, bool on) => _ = Send(new Dictionary<string, object> { ["t"] = "mod.mute", ["id"] = m.Id, ["on"] = on });

        public bool CanModerateMember(MemberVM m) => CanModerate && m != null && !m.IsMe && MemberVM.RoleLevel(m.Role) < MemberVM.RoleLevel(MyRole);

        // test hooks
        public Task SendForTest(string text) => SendMessage(text);
    }
}
