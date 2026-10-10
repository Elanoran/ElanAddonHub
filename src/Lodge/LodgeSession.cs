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

        // ---- "your Lodge server is outdated": only for the people who can actually update it (owner / officers / Guild Master panel)
        public bool CanUpdateServer => MyRole == "owner" || MyRole == "officer" || CanManage;
        public static Version ParseVersion(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return null;
            var parts = v.Trim().TrimStart('v', 'V').Split('.');
            var n = new int[3];
            for (int i = 0; i < 3 && i < parts.Length; i++) if (!int.TryParse(new string(parts[i].TakeWhile(char.IsDigit).ToArray()), out n[i])) return null;
            return new Version(n[0], n[1], n[2]);
        }
        public static string ShortVersion(string v) { var p = ParseVersion(v); return p == null ? v : p.Build == 0 ? p.Major + "." + p.Minor : p.ToString(); }
        public bool ServerOutdated => (Online || TestFed) && ParseVersion(ServerVersion) is Version have && ParseVersion(Brand.LatestLodgeServer) is Version want && have < want;
        public bool ShowServerNotice => CanUpdateServer && ServerOutdated && Settings.LodgeUpdateDismissed != Brand.LatestLodgeServer;
        public string ServerNoticeText => $"Your Lodge server is on {ShortVersion(ServerVersion)} - {ShortVersion(Brand.LatestLodgeServer)} is available. On the server run: {Brand.ServerUpdateCommand}";
        public void DismissServerNotice() { Settings.LodgeUpdateDismissed = Brand.LatestLodgeServer; SettingsStore.Save(Settings); Changed?.Invoke(); }
        public int MaxFileMb { get; private set; } = 25;
        // a server older than 2.3 sends no "features": reactions and pins are hidden then
        public bool SupportsReact { get; private set; }
        public bool SupportsPin { get; private set; }
        public bool CanPin { get; private set; }
        public string[] ReactionSet { get; private set; } = DefaultReactions;
        public static readonly string[] DefaultReactions = { "\U0001F44D", "\U0001F602", "❤️", "✅", "❌", "⚔️" };
        readonly Dictionary<string, ObservableCollection<PinVM>> pins = new Dictionary<string, ObservableCollection<PinVM>>();
        public event Action PinsChanged;
        public Func<bool> IsAtBottom;                      // the message list is scrolled to the end (set by the view)

        public ObservableCollection<PinVM> PinsOf(string channel)
        {
            if (channel == null) return new ObservableCollection<PinVM>();
            if (!pins.TryGetValue(channel, out var l)) { pins[channel] = l = new ObservableCollection<PinVM>(); l.CollectionChanged += (s, e) => PinsChanged?.Invoke(); }
            return l;
        }
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

        // ---- profiles (server 2.6): my own profile, avatars we have seen (so history keeps its pictures), answers to profile:get
        public ProfileData MyProfile { get; private set; }
        public bool SupportsProfile { get; private set; }
        public bool SupportsDisplayName { get; private set; }   // server 2.7
        public string MyDisplayName => Me?.DisplayName ?? "";
        public event Action MyProfileChanged;
        public event Action<ProfileData> ProfileReceived;   // an answer to profile:get (and my own after a save)
        readonly Dictionary<string, string[]> knownAvatars = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        public readonly GamePresence Presence;
        public readonly AutoStatus Auto = new AutoStatus();
        public Func<DateTime> Clock = () => DateTime.UtcNow;   // tests drive time
        readonly DispatcherTimer gameTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        string lastGameJson;

        public LodgeSession(Settings settings)
        {
            Settings = settings;
            Presence = new GamePresence(() => settings.WowRoot, () => !settings.PixelOff);
            AvatarArt.Root = () => settings.WowRoot;
            if (settings.InvisibleForget) settings.LodgeInvisible = false; // "Appear offline" is not remembered across restarts
            MessageVM.AuthorLookup = name => Members.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
            MessageVM.AvatarLookup = name => name != null && knownAvatars.TryGetValue(name, out var a) ? a : null;
            // presence changes (zone, combat, XP...) are sent at most every ~1 s, and only when the message really differs
            gameTimer.Tick += (s, e) => { gameTimer.Stop(); SendGame(); };
            Presence.Changed += () => { PresenceChanged?.Invoke(); if (!gameTimer.IsEnabled) gameTimer.Start(); };
            WowWatch.Shared.Start();
            Presence.Start();
            tick.Tick += (s, e) => OnTick();
            Sounds.Off = settings.SoundsOff;
            InitToasts();
        }

        public event Action PresenceChanged;                 // my own character / flags changed (the toasts use it)
        public ToastCenter Toasts { get; private set; }

        void InitToasts()
        {
            Toasts = new ToastCenter(Settings)
            {
                InCombat = () => Presence.Current != null && Presence.Current.InCombat,
                ChannelOnScreen = ch => Selected != null && Selected.Id == ch && (IsShownToUser?.Invoke() ?? false) && (IsAtBottom?.Invoke() ?? true),
            };
        }

        void ToastFor(ToastKind kind, MessageVM vm, string who, string text)
        {
            var ch = TextChannels.FirstOrDefault(c => c.Id == vm.Channel);
            Toasts.Push(new ToastItem
            {
                Kind = kind, Sender = DisplayNames.Shown(who), Channel = ch?.Name, ChannelId = vm.Channel, Text = text,
                Who = Members.FirstOrDefault(m => string.Equals(m.Name, who, StringComparison.OrdinalIgnoreCase)),
            });
        }

        static string OneLine(string t) { t = (t ?? "").Replace("\r", " ").Replace("\n", " "); return t.Length > 90 ? t.Substring(0, 90) + "..." : t; }

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
            client = new LodgeClient(url, code, Settings.LodgeName ?? Environment.UserName) { Invisible = () => Invisible };
            client.Status += (text, online) =>
            {
                StatusText = online ? OnlineText : text;
                if (!online && client != null && !client.IsRunning)
                {
                    var why = text;
                    Disconnect();
                    Stopped?.Invoke(why);
                }
                if (!online)
                {
                    // a dropped connection (network blip, server restart) is not the user leaving: remember the room and
                    // the mute/deafen state, and go back in after the reconnect (see Welcome)
                    if (MyRoom != null && client != null && client.IsRunning)
                    {
                        rejoinRoom = MyRoom;
                        rejoinMuted = voice?.Muted ?? false;
                        rejoinDeaf = voice?.Deafened ?? false;
                    }
                    LeaveVoice(false);
                }
                Changed?.Invoke();
            };
            client.Received += OnReceived;
            client.Voice += (id, data, off, count) => voice?.Incoming(id, data, off, count);
            tick.Start();
            client.Start();
            Changed?.Invoke();
        }

        // voice room to go back into after a dropped connection (null = the user left, or wasn't in voice)
        string rejoinRoom;
        bool rejoinMuted, rejoinDeaf;

        public void Disconnect()
        {
            rejoinRoom = null; // leaving the lodge on purpose
            LeaveVoice(false);
            client?.Stop();
            client = null;
            TestFed = false;
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
                    RefreshAuthorAvatars(j.Name);
                    RefreshAuthorNames();
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
                        RefreshAuthorAvatars(gone.Name);
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
                    var oldBadge = mem.BadgeKey;
                    mem.SetAvatar(m.Child("user").Str("avatarId"), m.Child("user").Str("accent"));
                    if (m.Child("user").ContainsKey("displayName")) ApplyDisplayName(mem, m.Child("user").Str("displayName"));
                    mem.Room = u.Room; mem.Muted = u.Muted; mem.Deaf = u.Deaf; mem.ServerMuted = u.ServerMuted;
                    mem.Status = u.Status; mem.Note = u.Note; mem.Role = u.Role;
                    mem.Invisible = mem.IsMe ? Invisible : u.Invisible;
                    ApplyGame(mem, m.Child("user").Child("game"));
                    Remember(mem);
                    if (mem.BadgeKey != oldBadge) RefreshAuthorAvatars(mem.Name);
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
                case "react":
                    var rm = MessagesOf(m.Str("channel")).FirstOrDefault(x => x.Id == m.Str("id"));
                    if (rm != null)
                    {
                        ApplyReaction(rm, m.Str("reaction") ?? m.Str("emoji"), m.List("users").OfType<string>().ToArray());
                        var by = m.Str("by");
                        if (rm.Mine && m.Bool("on") && by != null && !string.Equals(by, Me?.Name, StringComparison.OrdinalIgnoreCase))
                            ToastFor(ToastKind.Reaction, rm, by, "reacted \"" + ReactionArt.Name(m.Str("reaction") ?? m.Str("emoji")) + "\" to: " + OneLine(rm.Text));
                    }
                    break;
                case "pin":
                    var pc = m.Str("channel");
                    var pv = ParsePin(m.Child("pin"));
                    if (pc == null || pv == null) break;
                    var plist = PinsOf(pc);
                    var old = plist.FirstOrDefault(x => x.Id == pv.Id);
                    if (old == null && TextChannels.Any(c => c.Id == pc) && !string.Equals(pv.PinnedBy, Me?.Name, StringComparison.OrdinalIgnoreCase))
                        Toasts.Push(new ToastItem { Kind = ToastKind.Pin, Sender = pv.PinnedBy != null ? DisplayNames.Shown(pv.PinnedBy) : "Someone", Channel = TextChannels.First(c => c.Id == pc).Name, ChannelId = pc,
                            Text = "pinned: " + OneLine(pv.Shown), Who = Members.FirstOrDefault(x => string.Equals(x.Name, pv.PinnedBy, StringComparison.OrdinalIgnoreCase)) });
                    if (old != null) plist.Remove(old);
                    plist.Add(pv);
                    var pm = MessagesOf(pc).FirstOrDefault(x => x.Id == pv.Id);
                    if (pm != null) pm.Pinned = true;
                    break;
                case "unpin":
                    var uc = m.Str("channel");
                    var ul = PinsOf(uc);
                    var uo = ul.FirstOrDefault(x => x.Id == m.Str("id"));
                    if (uo != null) ul.Remove(uo);
                    var um = MessagesOf(uc).FirstOrDefault(x => x.Id == m.Str("id"));
                    if (um != null) um.Pinned = false;
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
                case "profile":
                    // someone's avatar / accent changed (small broadcast)
                    var pu = Members.FirstOrDefault(x => x.Id == m.Int("id"));
                    if (pu != null)
                    {
                        pu.SetAvatar(m.Str("avatarId"), m.Str("accent"));
                        if (m.ContainsKey("displayName")) ApplyDisplayName(pu, m.Str("displayName"));
                        Remember(pu);
                        RefreshAuthorAvatars(pu.Name);
                        if (pu.IsMe && MyProfile != null) { MyProfile.AvatarId = pu.AvatarId; MyProfile.Accent = pu.Accent; MyProfile.DisplayName = pu.DisplayName; MyProfileChanged?.Invoke(); }
                    }
                    break;
                case "profile:data":
                    var pd = ProfileData.Parse(m.Child("profile"));
                    if (pd == null) break;
                    if (pd.Found && !string.IsNullOrEmpty(pd.Name) && m.Child("profile").ContainsKey("displayName"))
                    {
                        var who = Members.FirstOrDefault(x => string.Equals(x.Name, pd.Name, StringComparison.OrdinalIgnoreCase));
                        if (who != null) ApplyDisplayName(who, pd.DisplayName);
                        else if (DisplayNames.Remember(pd.Name, pd.DisplayName)) RefreshAuthorNames();
                    }
                    if (Me != null && string.Equals(pd.Name, Me.Name, StringComparison.OrdinalIgnoreCase) && pd.Found) { MyProfile = pd; MyProfileChanged?.Invoke(); }
                    ProfileReceived?.Invoke(pd);
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
            var features = m.List("features").OfType<string>().ToList();
            SupportsReact = features.Contains("react");
            SupportsPin = features.Contains("pin");
            CanPin = SupportsPin && m.Bool("canPin");
            SupportsProfile = features.Contains("profile");
            SupportsDisplayName = features.Contains("displayname");
            var rs = m.List("reactions").OfType<string>().ToArray();
            ReactionSet = rs.Length > 0 ? rs : DefaultReactions;

            Members.Clear();
            foreach (var u in m.List("users").OfType<Dictionary<string, object>>())
            {
                var mem = ParseMember(u);
                mem.IsMe = mem.Id == myId;
                Members.Add(mem);
            }
            Me = Members.FirstOrDefault(x => x.IsMe);
            if (Me != null) Me.Invisible = Invisible;
            if (m.Child("profile") != null) { MyProfile = ProfileData.Parse(m.Child("profile")); MyProfileChanged?.Invoke(); }

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

            // pins (the server keeps its own copy of each), then flag the ones still in the loaded history
            foreach (var l in pins.Values) l.Clear();
            if (m.Child("pins") != null)
                foreach (var kv in m.Child("pins"))
                {
                    var list = PinsOf(kv.Key);
                    foreach (var p in (kv.Value as List<object> ?? new List<object>()).OfType<Dictionary<string, object>>())
                    {
                        var pv = ParsePin(p);
                        if (pv == null) continue;
                        list.Add(pv);
                        var pm = MessagesOf(kv.Key).FirstOrDefault(x => x.Id == pv.Id);
                        if (pm != null) pm.Pinned = true;
                    }
                }
            foreach (var ch in TextChannels) RecomputeUnread(ch);

            Select(TextChannels.FirstOrDefault(c => c.Id == keepSelected) ?? TextChannels.FirstOrDefault());
            if (MyRoom != null && VoiceRooms.All(r => r.Id != MyRoom)) LeaveVoice(false);
            else if (MyRoom != null) SendVoice(MyRoom); // reconnected while in voice
            else if (rejoinRoom != null)
            {
                // the connection dropped while we were in voice: go back into the same room, quietly, same mute/deafen
                var room = rejoinRoom; bool wasMuted = rejoinMuted, wasDeaf = rejoinDeaf;
                rejoinRoom = null;
                if (VoiceRooms.Any(r => r.Id == room))
                {
                    JoinRoom(room, quiet: true);
                    if (wasMuted || wasDeaf) SetMuteDeaf(wasMuted, wasDeaf);
                }
            }
            SyncRooms();
            StatusText = OnlineText;
            Auto.MarkSent(Clock(), "online", "");                // a fresh connection starts as Online on the server
            EvaluateAuto();
            if (Auto.Status != "online" || Auto.Note.Length > 0 || Invisible) SendStatus();
            lastGameJson = null;
            SendGame();
            foreach (var r in VoiceRooms) r.InvisibleHint = Invisible;
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
                Status = u.Str("status"), Note = u.Str("note"), Role = u.Str("role"), Invisible = u.Bool("invisible"),
            };
            m.SetAvatar(u.Str("avatarId"), u.Str("accent"));
            if (u.ContainsKey("displayName")) { m.SetDisplayName(u.Str("displayName")); DisplayNames.Remember(m.Name, m.DisplayName); }
            ApplyGame(m, u.Child("game"));
            Remember(m);
            return m;
        }

        public string[] KnownAvatarOf(string name) => name != null && knownAvatars.TryGetValue(name, out var a) ? a : null;

        void Remember(MemberVM m)
        {
            if (m.Name != null) knownAvatars[m.Name] = new[] { m.AvatarId, m.Accent };
        }

        // a person's display name changed: the member, and every place that shows it (chat rows, reply quotes, reaction tips)
        void ApplyDisplayName(MemberVM m, string display)
        {
            display = display ?? "";
            m.SetDisplayName(display);
            if (DisplayNames.Remember(m.Name, display)) RefreshAuthorNames();
        }

        void RefreshAuthorNames()
        {
            foreach (var list in messages.Values)
                foreach (var vm in list) vm.RefreshNames();
        }

        // chat rows show their author's picture and class badge: refresh them when it changes (messages are bounded, this is cheap)
        void RefreshAuthorAvatars(string name)
        {
            if (name == null) return;
            foreach (var list in messages.Values)
                foreach (var vm in list)
                    if (string.Equals(vm.From, name, StringComparison.OrdinalIgnoreCase)) vm.RefreshAvatar();
        }

        static void ApplyGame(MemberVM m, Dictionary<string, object> g)
        {
            m.SetGame(g != null && g.Bool("playing"), g?.Str("name"), g?.Str("class"), g?.Str("classFile"), g?.Int("level") ?? 0, g?.Str("zone"), g?.Str("guild"), g?.Str("race"), g?.Str("raceFile"), g?.Int("sex") ?? 0);
            bool xp = g != null && g.TryGetValue("xpPct", out var xv) && xv is double;
            m.SetPresence(g?.Int("flags") ?? 0, xp ? g.Int("xpPct") : -1, g?.Bool("rested") ?? false, g?.Int("groupSize") ?? 0, g?.Bool("inInstance") ?? false, g?.Str("instanceName"));
        }

        // the "game" message: what I play and what I'm doing (older servers ignore the extra fields). Zone/instance and XP/rested are
        // left out when their sharing is off; the flags (combat, dead, AFK, group) always go.
        public Dictionary<string, object> BuildGameMessage()
        {
            var vis = Invisible ? "invisible" : "visible";
            if (Settings.ShareGameOff) return new Dictionary<string, object> { ["t"] = "game", ["share"] = false, ["visibility"] = vis };
            var c = Presence.Current;
            var obj = new Dictionary<string, object> { ["t"] = "game", ["playing"] = Presence.Playing, ["visibility"] = vis };
            if (c != null)
            {
                obj["name"] = c.Name; obj["realm"] = c.Realm; obj["class"] = c.Class; obj["classFile"] = c.ClassFile;
                obj["level"] = c.Level; obj["guild"] = c.Guild;
                obj["race"] = c.Race; obj["raceFile"] = c.RaceFile; obj["sex"] = c.Sex;
                if (!Settings.ShareZoneOff) obj["zone"] = c.Zone;
                if (c.HasLive)
                {
                    int flags = c.CombatKnown ? c.Flags : c.Flags & ~1;
                    obj["flags"] = flags; obj["groupSize"] = c.GroupSize; obj["inInstance"] = c.InInstance;
                    if (!Settings.ShareZoneOff && c.InstanceName != null) obj["instanceName"] = c.InstanceName;
                    if (!Settings.ShareXpOff) { if (c.XpPercent >= 0) obj["xpPct"] = c.XpPercent; obj["rested"] = c.Rested; }
                }
            }
            return obj;
        }

        // sent right away when something I chose changes, and by the 0.8 s timer for live changes; identical messages are skipped
        public void SendGame()
        {
            if (!Online) return;
            var obj = BuildGameMessage();
            var json = Json.Write(obj);
            if (json == lastGameJson) return;
            lastGameJson = json;
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
            foreach (var x in Members) { x.InMyRoom = MyRoom != null && x.Room == MyRoom; x.RoomName = x.Room == null ? null : VoiceRooms.FirstOrDefault(r => r.Id == x.Room)?.Name; }
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
                ReplyFrom = reply?.Str("by") ?? reply?.Str("from"), ReplyText = reply?.Str("snippet") ?? reply?.Str("text"), ReplyId = reply?.Str("id"),
                ReactEnabled = SupportsReact, PinEnabled = CanPin,
                Continuation = prev != null && prev.From == from && (at - prev.At).TotalMinutes < 5,
            };
            var reacts = m.Child("reactions");
            if (reacts != null)
                foreach (var kv in reacts.OrderBy(k => Array.IndexOf(ReactionSet, k.Key)))
                    ApplyReaction(vm, kv.Key, (kv.Value as List<object> ?? new List<object>()).OfType<string>().ToArray());
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
            return Regex.IsMatch(text, @"@(" + DisplayNames.MentionPattern(Me.Name, Me.DisplayName) + @"|everyone|here)(?![\w])", RegexOptions.IgnoreCase);
        }

        void OnNewMessage(MessageVM vm)
        {
            MessageAdded?.Invoke(vm);
            if (vm.Mine) return;
            var ch = TextChannels.FirstOrDefault(c => c.Id == vm.Channel);
            bool shown = ch == Selected && (IsShownToUser?.Invoke() ?? true);
            // reading = the channel is on screen AND the list is at the bottom; otherwise it counts as unread
            bool looking = shown && (IsAtBottom?.Invoke() ?? true);
            if (looking) SetReadMark(ch, vm);
            else if (ch != null)
            {
                ch.Unread = true;
                ch.UnreadCount++;
                if (vm.Mentioned) ch.Mentions++;
                if (ch.FirstUnreadId == null) ch.FirstUnreadId = vm.Id;
                if (ch == Selected && ch.Divider == null) { ch.Divider = vm; vm.ShowDivider = true; }
                UnreadChanged?.Invoke(true);
            }
            if (shown) looking = true; // (notifications: the window is looking at this channel)
            if (vm.Mentioned) ToastFor(ToastKind.Mention, vm, vm.From, string.IsNullOrEmpty(vm.Text) ? "shared " + vm.File?.Name : OneLine(vm.Text));
            else if (Me != null && vm.ReplyFrom != null && string.Equals(vm.ReplyFrom, Me.Name, StringComparison.OrdinalIgnoreCase))
                ToastFor(ToastKind.Reply, vm, vm.From, string.IsNullOrEmpty(vm.Text) ? "shared " + vm.File?.Name : OneLine(vm.Text));
            var mode = Settings.NotifyMode ?? "mentions";
            if (vm.Mentioned && mode != "none") Sounds.Mention();
            if (!looking && (mode == "all" || (mode == "mentions" && vm.Mentioned)))
                Notify?.Invoke($"{vm.FromShown} in #{ch?.Name}", string.IsNullOrEmpty(vm.Text) ? "shared " + vm.File?.Name : vm.Text);
        }

        public void Select(ChannelVM ch)
        {
            if (ch == null || ch.IsVoice) return;
            // leaving a channel: its "New messages" line goes away (what I saw is read as soon as I scrolled to the end)
            if (Selected != null && Selected != ch && Selected.Divider != null) { Selected.Divider.ShowDivider = false; Selected.Divider = null; }
            foreach (var c in TextChannels) c.Selected = c == ch;
            Selected = ch;
            Settings.LastChannel = ch.Id;
            // the line sits above the first unread message; the channel stays unread until the view reaches the bottom
            if (ch.Divider == null && ch.FirstUnreadId != null)
            {
                var first = MessagesOf(ch.Id).FirstOrDefault(x => x.Id == ch.FirstUnreadId);
                if (first != null) { ch.Divider = first; first.ShowDivider = true; }
            }
            UnreadChanged?.Invoke(TextChannels.Any(c => c.Unread));
            Changed?.Invoke();
        }

        // called by the view / window when the chat is on screen: reading happens only at the bottom of the list
        public void MarkRead()
        {
            var ch = Selected;
            if (ch == null || !ch.Unread) return;
            if (!(IsAtBottom?.Invoke() ?? true)) return;
            SetReadMark(ch, MessagesOf(ch.Id).LastOrDefault());
        }

        // ---- read state: per lodge + channel, in the hub's data folder (never sent anywhere)
        sealed class ReadMark { public string Id; public long At; }
        Dictionary<string, ReadMark> readMarks;
        static string ReadFile => Path.Combine(Util.DataDir, "lodge-read.json");

        string ReadKey(ChannelVM ch)
        {
            string host = "";
            try { host = new Uri(BaseUrl ?? "http://local").Host; } catch { }
            return host + "|" + ch.Id;
        }

        void LoadMarks()
        {
            if (readMarks != null) return;
            readMarks = new Dictionary<string, ReadMark>();
            try
            {
                if (!File.Exists(ReadFile)) return;
                var o = Json.Obj(File.ReadAllText(ReadFile));
                if (o != null)
                    foreach (var kv in o)
                        if (kv.Value is Dictionary<string, object> d) readMarks[kv.Key] = new ReadMark { Id = d.Str("id"), At = d.Long("at") };
            }
            catch (Exception e) { Util.Log("read state: " + e.Message); }
        }

        void SaveMarks()
        {
            try
            {
                var o = new Dictionary<string, object>();
                foreach (var kv in readMarks) o[kv.Key] = new Dictionary<string, object> { ["id"] = kv.Value.Id, ["at"] = (double)kv.Value.At };
                var tmp = ReadFile + ".tmp";
                File.WriteAllText(tmp, Json.Write(o));
                if (File.Exists(ReadFile)) File.Delete(ReadFile);
                File.Move(tmp, ReadFile);
            }
            catch (Exception e) { Util.Log("read state: " + e.Message); }
        }

        static long Ms(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

        // everything up to and including `upTo` is read; clears the counters (the divider stays until I leave)
        void SetReadMark(ChannelVM ch, MessageVM upTo)
        {
            if (ch == null) return;
            LoadMarks();
            if (upTo != null) { readMarks[ReadKey(ch)] = new ReadMark { Id = upTo.Id, At = Ms(upTo.At) }; SaveMarks(); }
            bool changed = ch.Unread || ch.Mentions > 0 || ch.UnreadCount > 0;
            ch.Unread = false; ch.Mentions = 0; ch.UnreadCount = 0; ch.FirstUnreadId = null;
            if (changed) UnreadChanged?.Invoke(TextChannels.Any(c => c.Unread));
        }

        // after a (re)connect: count what arrived since the saved mark. A channel I've never opened starts as read.
        void RecomputeUnread(ChannelVM ch)
        {
            LoadMarks();
            var list = MessagesOf(ch.Id);
            if (ch.Divider != null) { ch.Divider.ShowDivider = false; ch.Divider = null; }
            if (!readMarks.TryGetValue(ReadKey(ch), out var mark))
            {
                var last = list.LastOrDefault();
                if (last != null) { readMarks[ReadKey(ch)] = new ReadMark { Id = last.Id, At = Ms(last.At) }; SaveMarks(); }
                mark = null;
            }
            int start = list.Count;
            if (mark != null)
            {
                int idx = -1;
                for (int i = 0; i < list.Count; i++) if (list[i].Id == mark.Id) { idx = i; break; }
                if (idx >= 0) start = idx + 1;
                else { start = 0; while (start < list.Count && Ms(list[start].At) <= mark.At) start++; } // the marked message is gone: go by time
            }
            var unread = list.Skip(start).Where(x => !x.Mine).ToList();
            ch.UnreadCount = unread.Count;
            ch.Mentions = unread.Count(x => x.Mentioned);
            ch.Unread = unread.Count > 0;
            ch.FirstUnreadId = unread.FirstOrDefault()?.Id;
            UnreadChanged?.Invoke(TextChannels.Any(c => c.Unread));
        }

        // ---- reactions, pins
        void ApplyReaction(MessageVM msg, string emoji, string[] users)
        {
            if (string.IsNullOrEmpty(emoji)) return;
            var r = msg.Reactions.FirstOrDefault(x => x.Emoji == emoji);
            if (users.Length == 0) { if (r != null) msg.Reactions.Remove(r); }
            else
            {
                if (r == null)
                {
                    r = new ReactionVM { Msg = msg, Emoji = emoji };
                    int at = 0; // keep the fixed order of the set
                    while (at < msg.Reactions.Count && Array.IndexOf(ReactionSet, msg.Reactions[at].Emoji) < Array.IndexOf(ReactionSet, emoji)) at++;
                    msg.Reactions.Insert(at, r);
                }
                r.Update(users, Me?.Name);
            }
            msg.RaiseReactions();
        }

        PinVM ParsePin(Dictionary<string, object> p)
        {
            if (p == null || p.Str("id") == null) return null;
            var f = p.Child("file");
            return new PinVM
            {
                Id = p.Str("id"), Text = p.Str("text"), By = p.Str("by"), PinnedBy = p.Str("pinnedBy"),
                At = DateTimeOffset.FromUnixTimeMilliseconds(p.Long("at")).UtcDateTime, FileName = f?.Str("name"), CanUnpin = CanPin,
            };
        }

        public Task React(MessageVM msg, string emoji) =>
            Send(new Dictionary<string, object> { ["t"] = "react", ["id"] = msg.Id, ["emoji"] = emoji });
        public Task PinMessage(string id) => Send(new Dictionary<string, object> { ["t"] = "pin", ["id"] = id });
        public Task UnpinMessage(string id, string channel) =>
            Send(new Dictionary<string, object> { ["t"] = "unpin", ["id"] = id, ["channel"] = channel });

        Dictionary<int, DateTime> TypingMap(string channel)
        {
            if (!typing.TryGetValue(channel ?? "", out var t)) typing[channel ?? ""] = t = new Dictionary<int, DateTime>();
            return t;
        }

        public string TypingText(string channel)
        {
            var now = DateTime.UtcNow;
            var who = TypingMap(channel).Where(t => t.Value > now && (Me == null || t.Key != Me.Id))
                .Select(t => Members.FirstOrDefault(x => x.Id == t.Key)?.Display).Where(n => n != null).ToList();
            return who.Count == 0 ? "" : who.Count == 1 ? $"{who[0]} is typing..." : $"{string.Join(", ", who)} are typing...";
        }

        Task Send(Dictionary<string, object> obj) => client?.Send(obj) ?? Task.CompletedTask;

        public Task SendMessage(string text, string replyTo = null, string fileId = null)
        {
            // appearing offline: messages still carry my name - say so once
            if (Invisible && !Settings.InvisibleHintShown)
            {
                Settings.InvisibleHintShown = true;
                SettingsStore.Save(Settings);
                Error?.Invoke("Others can see messages you send, even while you appear offline");
            }
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

        public void JoinRoom(string room) => JoinRoom(room, false);

        // quiet: no join sound (rejoining after a dropped connection)
        public void JoinRoom(string room, bool quiet)
        {
            if (!Online || room == null) return;
            if (voice == null)
            {
                voice = new VoiceEngine
                {
                    PushToTalk = Settings.VoicePushToTalk,
                    PttKey = PttKey,
                    ThresholdDb = Settings.VoiceThreshold ?? -45,
                    Preset = VoicePresets.Get(Settings.VoicePreset),
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
            if (!quiet) Sounds.Join();
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
            voice.Preset = VoicePresets.Get(Settings.VoicePreset);
            if (!voice.Deafened) voice.Volume = (float)(Settings.VoiceVolume ?? 1);
        }

        void OnTick()
        {
            tickCount++;
            foreach (var m in Members)
                m.Speaking = MyRoom != null && m.Room == MyRoom && voice != null && (m.IsMe ? voice.Transmitting : voice.IsSpeaking(m.Id));
            if (tickCount % 50 == 0) CheckIdle();
            AutoStatusTick();
            if (Toasts.NeedsTick) Toasts.Tick();
        }

        // test hooks for the automatic status
        public void TickForTest() => AutoStatusTick();
        public void SetIdleAwayForTest(bool on) => autoAway = on;

        // ================================================================ status

        bool autoAway;   // no mouse/keyboard for 10 minutes
        public bool Invisible => Settings.LodgeInvisible;
        // what I show right now: my own choice, or the automatic one (In combat / AFK) when I haven't chosen
        public string MyStatus { get { EvaluateAuto(); return Auto.Status; } }
        public string MyNote { get { EvaluateAuto(); return Auto.Note; } }
        public bool MyStatusIsAuto { get { EvaluateAuto(); return Auto.IsAuto; } }

        public void SetMyStatus(string status, string note)
        {
            autoAway = false;
            Settings.LodgeStatus = status;
            Settings.LodgeNote = (note ?? "").Trim();
            SettingsStore.Save(Settings);
            SendStatus();
        }

        // "Appear offline" on/off: kept in the settings; the server hides me from everyone who isn't allowed to see invisible members
        public void SetInvisible(bool on)
        {
            if (Settings.LodgeInvisible == on) return;
            Settings.LodgeInvisible = on;
            SettingsStore.Save(Settings);
            if (Me != null) Me.Invisible = on;
            foreach (var r in VoiceRooms) r.InvisibleHint = on;
            SendStatus();
            lastGameJson = null; SendGame();
            Changed?.Invoke();
        }

        void EvaluateAuto()
        {
            var c = Presence.Current;
            Auto.Evaluate(Clock(), !Settings.AutoStatusOff, c != null && c.InCombat, c != null && c.Afk, autoAway, Settings.LodgeStatus, Settings.LodgeNote);
        }

        void SendStatus()
        {
            EvaluateAuto();
            Auto.MarkSent(Clock(), Auto.Status, Auto.Note);
            _ = Send(new Dictionary<string, object> { ["t"] = "status", ["status"] = Auto.Status, ["note"] = Auto.Note, ["visibility"] = Invisible ? "invisible" : "visible" });
        }

        // every 100 ms: send an automatic status change when it really changed (debounced to 2 s)
        void AutoStatusTick()
        {
            if (!Online) return;
            EvaluateAuto();
            if (Auto.TrySend(Clock(), out var st, out var note))
            {
                _ = Send(new Dictionary<string, object> { ["t"] = "status", ["status"] = st, ["note"] = note, ["visibility"] = Invisible ? "invisible" : "visible" });
                Changed?.Invoke();
            }
        }

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
            if (!Settings.AutoAwayOff && !autoAway && chosen == "online" && idle > 600) autoAway = true;      // AutoStatusTick sends it
            else if (autoAway && (idle < 5 || Settings.AutoAwayOff)) autoAway = false;
        }

        // ================================================================ profile

        public void RequestProfile(string name)
        {
            if (SupportsProfile && !string.IsNullOrEmpty(name)) _ = Send(new Dictionary<string, object> { ["t"] = "profile:get", ["name"] = name });
        }

        // own profile: only the fields given are changed (the server validates everything and answers with profile:data)
        public Task SaveProfile(Dictionary<string, object> fields)
        {
            fields["t"] = "profile:set";
            return Send(fields);
        }

        // owner only: back to the default avatar and accent
        public void ResetProfile(string name, bool clearText = false) =>
            _ = Send(new Dictionary<string, object> { ["t"] = "profile:reset", ["name"] = name, ["clearText"] = clearText });

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
        // offline rendering test: feed protocol frames without a server
        public bool TestFed { get; private set; }
        public void FeedForTest(string json) { TestFed = true; OnReceived(Json.Obj(json)); }
        public void SetReadMarkForTest(string channel, string id, long at)
        {
            LoadMarks();
            readMarks["local|" + channel] = new ReadMark { Id = id, At = at };
        }
    }
}
