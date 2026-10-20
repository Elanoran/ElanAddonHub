using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ElansAddonHub.Services;
using Elans.Shared;

namespace ElansAddonHub.Lodge
{
    // Everything about the lodge except drawing it: connection, channels, people, messages, voice,
    // status, admin requests. The views bind to its collections and call its methods.
    public partial class LodgeSession
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
        readonly Dictionary<string, ChannelVM> channelCache = new Dictionary<string, ChannelVM>();   // a channel keeps its object across welcomes
        readonly HashSet<string> failedImages = new HashSet<string>();                                  // 404 for this session: not asked again
        static bool housekeepingDone;

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
        // the grouped online list (In game / Online / Away / Invisible); see MemberGroups
        public MemberGroups Groups { get; private set; }
        // only the owner is ever sent invisible members (server: Perm.SeeInvisible); the list also checks this itself
        public bool CanSeeInvisible => MyRole == "owner";

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
        public event Action<string> Hint;                  // a gentle one-time tip
        public event Action<PendingSend, string> SendFailed; // the text to put back into the box, and why
        public Func<bool> ToastWillShow;                   // a toast would be on screen right now (then no tray balloon for the same message)
        public event Action<List<AdminMember>, List<LodgeChannelView>> AdminMembers; // members, channels
        public event Action<string, string, string> AdminInvited; // name, role, code
        public event Action<AdminPurgedMsg> AdminPurged; // the owner's "revert member" answer: name, atlas (items removed)
        public Func<bool> IsShownToUser;                   // the Lodge tab is visible and the window active

        // ---- profiles (server 2.6): my own profile, avatars we have seen (so history keeps its pictures), answers to profile:get
        public ProfileData MyProfile { get; private set; }
        public bool SupportsProfile { get; private set; }
        public bool SupportsDisplayName { get; private set; }   // server 2.7
        public bool SupportsLfg { get; private set; }           // server 2.9: sign-up cards
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
            LodgeCredentials.MigrateDeviceId(settings);     // 3.1.2: before anything can change the saved Lodge (per-Lodge device ids)
            Groups = new MemberGroups(Members, () => CanSeeInvisible, settings);
            LfgVM.Clock = () => Clock(); LfgVM.MyName = () => Me?.Name; LfgVM.IAmOfficer = () => CanModerate;
            ItemKnowledge.WowRoot = () => settings.WowRoot;
            ItemKnowledge.Changed += RefreshItemChips;
            // The game data loads in the background a few seconds after the start, AFTER the history's chips were built: they would stay "Item 249409"
            // for good. When it (re)arrives the chips are built again (on the UI thread, once the data is ready).
            Action gameData = () => { if (Services.Db2.ItemData.Ready || Services.Db2.SpellData.Ready) System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(RefreshItemChips)); };
            Services.Db2.ItemData.Changed += gameData; Services.Db2.SpellData.Changed += gameData;
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
            YouTube.AutoLoad = () => settings.VideoPreviewsAuto;
            InitToasts();
            marksTimer.Tick += (s, e) => { marksTimer.Stop(); WriteMarksAsync(); };
            try { var disp = System.Windows.Application.Current?.Dispatcher; if (disp != null) disp.ShutdownStarted += (s, e) => FlushMarks(); } catch { }
            // once per start: old pictures out of the cache, leftovers of pasted screenshots out of %TEMP% (off the UI thread)
            if (!housekeepingDone && Util.DataDir != null)
            {
                housekeepingDone = true;
                Task.Run(() =>
                {
                    SafeFile.PruneCache(CacheDir, TimeSpan.FromDays(30), 200L * 1024 * 1024, DateTime.UtcNow);
                    SafeFile.DeletePasteLeftovers(Path.GetTempPath());
                });
                Task.Run(() => LoadMarks());
            }
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

        bool ToastFor(ToastKind kind, MessageVM vm, string who, string text)
        {
            var ch = TextChannels.FirstOrDefault(c => c.Id == vm.Channel);
            return Toasts.Push(new ToastItem
            {
                Kind = kind, Sender = DisplayNames.Shown(who), Channel = ch?.Name, ChannelId = vm.Channel, Text = text,
                Who = Members.FirstOrDefault(m => string.Equals(m.Name, who, StringComparison.OrdinalIgnoreCase)),
            });
        }

        static string OneLine(string t) { t = (t ?? "").Replace("\r", " ").Replace("\n", " "); return t.Length > 90 ? t.Substring(0, 90) + "..." : t; }

        // the item data (names, icons from the Bags addon) arrived or changed: every message rebuilds its chips
        public void RefreshItemChips() { foreach (var l in messages.Values.ToList()) foreach (var m in l.ToList()) m.RefreshItems(); }

        // "Load YouTube previews automatically" changed: switched on, every waiting card loads; switched off, cards already loaded stay
        public void RefreshVideos() { foreach (var l in messages.Values.ToList()) foreach (var m in l.ToList()) m.RefreshVideo(); }

        public ObservableCollection<MessageVM> MessagesOf(string channel)
        {
            if (channel == null) return new ObservableCollection<MessageVM>();
            if (!messages.TryGetValue(channel, out var l)) messages[channel] = l = new ObservableCollection<MessageVM>();
            return l;
        }

        // ================================================================ invite links

        // "elanshub://lodge/join#server|invite", "https://site/lodge#invite=CODE" (the part after # never reaches any server or log) or just an address.
        // `code` is the secret in the link: a one-time invite (InviteLinks.LooksLikeInvite) or an old permanent code. See InviteLinks.Parse.
        public static string ParseInvite(string text, out string code)
        {
            var link = InviteLinks.Parse(text);
            code = link.Secret;
            return link.Url;
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

        // A sign-in that the server has not accepted yet. The settings keep the previous credential until the welcome arrives.
        PendingCredential pending;
        public bool HasPendingCredential => pending != null;
        public bool LastStopWasCandidate { get; private set; }   // the last stop was a credential that was never accepted (the saved one is untouched)

        // candidate: a new credential / address to store only once the server welcomes this connection
        public void Connect(string url, string code, bool isToken = false, PendingCredential candidate = null)
        {
            Disconnect();
            pending = candidate;
            UsingToken = isToken;
            var c = new LodgeClient(url, code, Settings.LodgeName ?? Environment.UserName, isToken) { Invisible = () => Invisible };
            client = c;
            c.Status += (text, online) =>
            {
                if (!ReferenceEquals(client, c)) return;     // the last words of a connection that was replaced
                StatusText = online ? OnlineText : text;
                if (!online && pending != null && client != null && client.IsRunning && text != "Connecting...")
                {
                    // the first connect with a new credential did not work (unreachable, full, refused): drop it, keep what was saved
                    StopConnection(text != null && text.StartsWith("Offline") ? "Can't reach the lodge - check the address and your connection" : text, false);
                    return;
                }
                if (!online && client != null && !client.IsRunning)
                {
                    StopConnection(text, c.SignedOut);
                }
                if (!online)
                {
                    // a dropped connection (network blip, server restart) is not the user leaving: remember the room and
                    // the mute/deafen state, and go back in after the reconnect (see Welcome)
                    var wanted = MyRoom ?? pendingRoom;
                    if (wanted != null && client != null && client.IsRunning)
                    {
                        rejoinRoom = wanted;
                        rejoinMuted = voice?.Muted ?? false;
                        rejoinDeaf = voice?.Deafened ?? false;
                    }
                    LeaveVoice(false);
                    FailPending("The connection dropped before your message went through");
                }
                Changed?.Invoke();
            };
            c.Received += m => { if (ReferenceEquals(client, c)) OnReceived(m); };   // posts from an old client are ignored
            c.Voice += (id, data, off, count) => { if (ReferenceEquals(client, c)) voice?.Incoming(id, data, off, count); };
            tick.Start();
            if (!NoNetworkForTest) c.Start();
            Changed?.Invoke();
        }

        // the connection ended for good. A credential that was never accepted is discarded and never counts as "signed out".
        void StopConnection(string why, bool signedOut)
        {
            LastStopWasCandidate = pending != null;
            StoppedSignedOut = signedOut && !LastStopWasCandidate;   // a revoked saved token: the view forgets it
            Disconnect();
            Stopped?.Invoke(why);
        }

        public bool NoNetworkForTest;   // the self-test connects without opening a socket and feeds frames itself
        public void StopForTest(string why, bool signedOut = false) => StopConnection(why, signedOut);

        // the welcome arrived: now the credential (and address) is proven, and only now replaces the saved one
        void CommitPending()
        {
            var p = pending;
            if (p == null) return;
            pending = null;
            if (p.UrlOnly) Settings.LodgeUrl = p.Url;
            else if (p.IsToken) LodgeCredentials.StoreToken(Settings, p.Url, p.Secret, p.DeviceName ?? LodgeCredentials.DeviceName(Settings));
            else LodgeCredentials.StoreCode(Settings, p.Url, p.Secret);
            if (!string.IsNullOrWhiteSpace(p.Name)) Settings.LodgeName = p.Name;
            SettingsStore.Save(Settings);
            p.OnCommitted?.Invoke();
        }

        // voice room to go back into after a dropped connection (null = the user left, or wasn't in voice)
        string rejoinRoom;
        bool rejoinMuted, rejoinDeaf;

        // 3.0.1: the Outpost restarts for an update while I am in voice: the room (and mute / deafen) is saved, and the new
        // version goes back in after its welcome - like after a dropped connection. Only for the same lodge, only within
        // VoiceResumeFor (a normal start later never joins voice by itself), and only once.
        public static readonly TimeSpan VoiceResumeFor = TimeSpan.FromMinutes(10);
        public void RememberVoiceForRestart()
        {
            var room = MyRoom ?? pendingRoom;
            Settings.VoiceResume = room == null ? null : new VoiceResume
            {
                Room = room, Url = BaseUrl ?? Settings.LodgeUrl, Muted = voice?.Muted ?? pendingMuted, Deaf = voice?.Deafened ?? pendingDeaf,
                At = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
            SettingsStore.Save(Settings);
        }
        public void ForgetVoiceResume()
        {
            if (Settings.VoiceResume == null) return;
            Settings.VoiceResume = null;
            SettingsStore.Save(Settings);
        }
        // the saved room becomes the room to rejoin (when it is still fresh and for this lodge); the saved one is used up
        void TakeVoiceResume()
        {
            var r = Settings.VoiceResume;
            if (r == null) return;
            ForgetVoiceResume();
            var age = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - r.At;
            if (string.IsNullOrEmpty(r.Room) || age < 0 || age > VoiceResumeFor.TotalSeconds) return;
            var here = BaseUrl ?? Settings.LodgeUrl;   // the connected lodge (the saved address when the welcome is fed without a socket)
            if (r.Url != null && here != null && !string.Equals(NormalizeUrl(r.Url), NormalizeUrl(here), StringComparison.OrdinalIgnoreCase)) return;
            rejoinRoom = r.Room; rejoinMuted = r.Muted; rejoinDeaf = r.Deaf;
        }

        public void Disconnect()
        {
            rejoinRoom = null; // leaving the lodge on purpose
            pending = null;    // a credential that was not accepted yet is simply dropped
            pendingRoom = null;
            UsingToken = false;
            LeaveVoice(false);
            FlushMarks();
            client?.Stop();
            client = null;
            TestFed = false;
            Members.Clear();
            foreach (var r in VoiceRooms) r.Members.Clear();
            // leaving a lodge (or switching to another) starts clean; only a reconnect to the same lodge keeps what is shown
            foreach (var l in messages.Values) l.Clear();
            foreach (var l in pins.Values) l.Clear();
            channelCache.Clear();
            ResetCalendar();
            ResetFlights();
            ResetAtlas();
            ResetArmory();
            ResetFiles();
            ResetBadges();
            ResetRealms();
            ResetClassic();
            ResetLivePos();
            ResetHistoryState();
            Pending = null;
            failedImages.Clear();
            tick.Stop();
            Changed?.Invoke();
        }

        string OnlineText => $"{Members.Count} online";

        // ================================================================ incoming

        void OnReceived(JsonObject m)
        {
            switch (LodgeJson.Str(m, "t"))
            {
                case LodgeMsg.Welcome: Welcome(m); break;
                case JoinMsg.Type:
                    var jv = JoinMsg.Read(new LodgeJsonFields(m)).User;
                    if (jv == null) break;                              // no member entry: nothing to add
                    var j = ParseMember(jv);
                    if (Members.All(x => x.Id != j.Id)) Members.Add(j);
                    RefreshAuthorAvatars(j.Name);
                    RefreshAuthorNames();
                    SyncRooms(joined: j);
                    StatusText = OnlineText;
                    Changed?.Invoke();
                    break;
                case LeaveMsg.Type:
                    int goneId = LeaveMsg.Read(new LodgeJsonFields(m)).Id;
                    var gone = Members.FirstOrDefault(x => x.Id == goneId);
                    if (gone != null)
                    {
                        DropLivePos(gone);
                        if (gone.Room != null && gone.Room == MyRoom) Sounds.Leave();
                        Members.Remove(gone);
                        RefreshAuthorAvatars(gone.Name);
                    }
                    voice?.RemovePeer(goneId);
                    SyncRooms();
                    StatusText = OnlineText;
                    Changed?.Invoke();
                    break;
                case UserMsg.Type:
                    var uv = UserMsg.Read(new LodgeJsonFields(m)).User;
                    if (uv == null) break;
                    var u = ParseMember(uv);
                    var mem = Members.FirstOrDefault(x => x.Id == u.Id);
                    if (mem == null) break;
                    var oldRoom = mem.Room;
                    var oldBadge = mem.BadgeKey;
                    mem.SetAvatar(uv.AvatarId, uv.Accent);
                    if (uv.HasDisplayName) ApplyDisplayName(mem, uv.DisplayName);
                    mem.Guest = u.Guest;
                    mem.Room = u.Room; mem.Muted = u.Muted; mem.Deaf = u.Deaf; mem.ServerMuted = u.ServerMuted;
                    mem.Status = u.Status; mem.Note = u.Note; mem.Role = u.Role;
                    mem.Invisible = mem.IsMe ? Invisible : u.Invisible;
                    if (mem.Invisible && !mem.IsMe && !CanSeeInvisible) DropLivePos(mem);   // never a dot for someone I can't see
                    ApplyGame(mem, uv.Game);
                    Remember(mem);
                    if (mem.BadgeKey != oldBadge) RefreshAuthorAvatars(mem.Name);
                    if (!mem.Voice) voice?.RemovePeer(mem.Id);
                    if (!mem.IsMe && MyRoom != null && oldRoom != mem.Room)
                    {
                        if (mem.Room == MyRoom) Sounds.Join();
                        else if (oldRoom == MyRoom) Sounds.Leave();
                    }
                    if (mem.IsMe && pendingRoom != null && mem.Room == pendingRoom) CommitRoom(); // the server confirmed the join
                    if (mem.IsMe)
                    {
                        if (mem.ServerMuted != (voice?.Muted ?? false) && mem.ServerMuted) Error?.Invoke("An officer muted you");
                        // (a removed room arrives as a fresh welcome; a stale "no room" update here must not kick me out)
                    }
                    SyncRooms();
                    break;
                case ChatMessage.Type:
                    var vm = AddMessage(ChatMessage.Read(new LodgeJsonFields(m)));
                    TypingMap(vm.Channel).Remove(vm.FromId);
                    if (vm.Mine && Pending != null && Pending.Channel == vm.Channel) Pending = null;   // the server echoed it: it is out
                    OnNewMessage(vm);
                    break;
                case LodgeMsg.Lfg:
                    // a sign-up card changed: the whole current state arrives (one place per person is the server's rule)
                    var lu = LfgUpdateMsg.Read(new LodgeJsonFields(m));
                    var lm = MessagesOf(lu.Channel).FirstOrDefault(x => x.Id == lu.Id);
                    if (lm?.Lfg != null && lu.Card != null)
                    {
                        lm.Lfg.Apply(lu.Card);
                        if (lu.Text != null) lm.Text = SafeFile.Text(lu.Text);
                        LfgChanged?.Invoke(lm);
                    }
                    break;
                case ReactionMsg.Type:
                    var rx = ReactionMsg.Read(new LodgeJsonFields(m));
                    var rm = MessagesOf(rx.Channel).FirstOrDefault(x => x.Id == rx.Id);
                    if (rm != null)
                    {
                        ApplyReaction(rm, rx.Reaction ?? rx.Emoji, rx.Users.ToArray());
                        var by = SafeFile.Who(rx.By);
                        if (rm.Mine && rx.On && by != null && !string.Equals(by, Me?.Name, StringComparison.OrdinalIgnoreCase))
                            ToastFor(ToastKind.Reaction, rm, by, "reacted \"" + ReactionArt.Name(rx.Reaction ?? rx.Emoji) + "\" to: " + OneLine(rm.Text));
                    }
                    break;
                case PinnedMsg.Type:
                    var pin = PinnedMsg.Read(new LodgeJsonFields(m));
                    var pc = pin.Channel;
                    var pv = ParsePin(pin.Pin);
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
                case UnpinnedMsg.Type:
                    var unp = UnpinnedMsg.Read(new LodgeJsonFields(m));
                    var uc = unp.Channel;
                    var ul = PinsOf(uc);
                    var uo = ul.FirstOrDefault(x => x.Id == unp.Id);
                    if (uo != null) ul.Remove(uo);
                    var um = MessagesOf(uc).FirstOrDefault(x => x.Id == unp.Id);
                    if (um != null) um.Pinned = false;
                    break;
                case EditedMsg.Type:
                    var ed = EditedMsg.Read(new LodgeJsonFields(m));
                    var e = MessagesOf(ed.Channel).FirstOrDefault(x => x.Id == ed.Id);
                    if (e != null)
                    {
                        e.Text = SafeFile.Text(ed.Text); if (!ed.Card) e.Edited = true;   // an event card follows its event: not "(edited)"
                        bool was = e.Mentioned;
                        e.Mentioned = !e.Mine && MentionsMe(e.Text, e.From);
                        // an edit that adds a mention to a message I haven't read yet badges the channel; one that removes it takes it back
                        var ech = TextChannels.FirstOrDefault(c => c.Id == e.Channel);
                        if (ech != null && e.Unread && was != e.Mentioned)
                        {
                            ech.Mentions = Math.Max(0, ech.Mentions + (e.Mentioned ? 1 : -1));
                            UnreadChanged?.Invoke(TextChannels.Any(c => c.Unread));
                        }
                    }
                    break;
                case DeletedMsg.Type:
                    var del = DeletedMsg.Read(new LodgeJsonFields(m));
                    var list = MessagesOf(del.Channel);
                    var d = list.FirstOrDefault(x => x.Id == del.Id);
                    if (d != null) RemoveMessage(list, d, TextChannels.FirstOrDefault(c => c.Id == del.Channel));
                    break;
                case LodgeMsg.HistoryPage: OnHistoryPage(m); break;
                case LodgeMsg.SearchResult: OnSearchResult(m); break;
                case MemberTypingMsg.Type:
                    var tm = MemberTypingMsg.Read(new LodgeJsonFields(m));
                    TypingMap(tm.Channel ?? TextChannels.FirstOrDefault()?.Id)[tm.Id] = DateTime.UtcNow.AddSeconds(4);
                    break;
                case LodgeMsg.Profile:
                    // someone's avatar / accent changed (small broadcast)
                    var pmsg = ProfileMsg.Read(new LodgeJsonFields(m));
                    var pu = Members.FirstOrDefault(x => x.Id == pmsg.Id);
                    if (pu == null && pmsg.DisplayNameSent && DisplayNames.Remember(pmsg.Name, pmsg.DisplayName)) RefreshAuthorNames(); // the name directory stays fresh
                    if (pu != null)
                    {
                        pu.SetAvatar(pmsg.AvatarId, pmsg.Accent);
                        if (pmsg.DisplayNameSent) ApplyDisplayName(pu, pmsg.DisplayName);
                        Remember(pu);
                        RefreshAuthorAvatars(pu.Name);
                        if (pu.IsMe && MyProfile != null) { MyProfile.AvatarId = pu.AvatarId; MyProfile.Accent = pu.Accent; MyProfile.DisplayName = pu.DisplayName; MyProfileChanged?.Invoke(); }
                    }
                    break;
                case LodgeMsg.ProfileData:
                    var pview = ProfileDataMsg.Read(new LodgeJsonFields(m)).Profile;
                    var pd = ProfileData.From(pview);
                    if (pd == null) break;
                    if (pd.Found && !string.IsNullOrEmpty(pd.Name) && pview.DisplayNameSent)
                    {
                        var who = Members.FirstOrDefault(x => string.Equals(x.Name, pd.Name, StringComparison.OrdinalIgnoreCase));
                        if (who != null) ApplyDisplayName(who, pd.DisplayName);
                        else if (DisplayNames.Remember(pd.Name, pd.DisplayName)) RefreshAuthorNames();
                    }
                    if (Me != null && string.Equals(pd.Name, Me.Name, StringComparison.OrdinalIgnoreCase) && pd.Found) { MyProfile = pd; MyProfileChanged?.Invoke(); }
                    ProfileReceived?.Invoke(pd);
                    break;
                case LodgeErrorMsg.Type:
                    {
                        var err = LodgeErrorMsg.Read(new LodgeJsonFields(m));      // the one error frame (shared/Elans.Shared/Lodge/ErrorMessages.cs)
                        OnServerError(err.Text, err.Code, err.Room);
                        break;
                    }
                case LodgeMsg.AdminMembers:
                    {
                        var am = AdminMembersMsg.Read(new LodgeJsonFields(m));
                        AdminMembers?.Invoke(am.Members, am.Channels);
                        break;
                    }
                case LodgeMsg.AdminPurged:
                    AdminPurged?.Invoke(AdminPurgedMsg.Read(new LodgeJsonFields(m)));
                    break;
                case LodgeMsg.AdminInvited:
                    {
                        var ai = AdminInvitedMsg.Read(new LodgeJsonFields(m));
                        AdminInvited?.Invoke(ai.Name, ai.Role, ai.Code);
                        break;
                    }
                case LodgeMsg.CalEvents: case LodgeMsg.CalRange:
                    OnCalendarFrame(m);
                    break;
                case LodgeMsg.AdminInvites: case LodgeMsg.AdminInviteCreated: case LodgeMsg.Devices: case LodgeMsg.AdminDevices: case LodgeMsg.DeviceAdded:
                    OnInviteFrame(m);
                    break;
                case LodgeMsg.LodgeInfo: case LodgeMsg.MemberLeft:
                    OnLeaveFrame(m);
                    break;
                case LodgeMsg.FlightsShared: OnFlightShared(m); break;
                case LodgeMsg.FlightsData: OnFlightData(m); break;
                case LodgeMsg.FlightsRoutesShared: OnFlightRoutesShared(m); break;
                case LodgeMsg.FlightsRoutesData: OnFlightRoutesData(m); break;
                case LodgeMsg.AtlasShared: OnAtlasShared(AtlasSharedMsg.Read(new LodgeJsonFields(m))); break;
                case LodgeMsg.AtlasData: OnAtlasData(AtlasDataMsg.Read(new LodgeJsonFields(m))); break;
                case LodgeMsg.CairnStatus: OnCairnStatus(m); break;
                case LodgeMsg.CairnJoin: OnCairnJoin(m); break;
                case LodgeMsg.CairnNotices: OnCairnNotices(m); break;
                case LodgeMsg.ArmorySaved: OnArmorySaved(m); break;
                case LodgeMsg.ArmoryDeleted: OnArmoryDeleted(m); break;
                case LodgeMsg.ArmoryChars: OnArmoryChars(m); break;
                case LodgeMsg.ArmoryChar: OnArmoryChar(m); break;
                case LodgeMsg.ArmoryCrafters: OnArmoryCrafters(m); break;
                case LodgeMsg.ArmoryStanding: OnArmoryStanding(m); break;
                case LodgeMsg.FilesData: case LodgeMsg.FilesChanged: case LodgeMsg.FilesResult: OnFilesFrame(m); break;
                case LodgeMsg.BadgesMine: case LodgeMsg.BadgesMember: case LodgeMsg.BadgesState: case LodgeMsg.LadderData: OnBadgesFrame(m); break;
                case LodgeMsg.RealmsStatus: case LodgeMsg.RealmsBack: OnRealmsFrame(m); break;
                case LodgeMsg.MemberPos: OnMemberPos(m); break;
                case LodgeMsg.DatasetData: case LodgeMsg.DatasetChanged: case LodgeMsg.DatasetResult: OnDatasetFrame(m); break;
            }
        }

        void Welcome(JsonObject m)
        {
            CommitPending();
            var w = WelcomeMsg.Read(new LodgeJsonFields(m));                   // shared/Elans.Shared/Lodge/WelcomeMessages.cs
            int myId = w.You;
            ServerVersion = w.Server;
            MyRole = w.Role ?? "member";
            MaxFileMb = w.MaxFileMb > 0 ? w.MaxFileMb : 25;
            CanShareFiles = w.CanShareFiles;
            CanModerate = w.CanModerate;
            CanManage = w.CanManage;
            var features = w.Features;
            SupportsReact = features.Contains("react");
            SupportsPin = features.Contains("pin");
            CanPin = SupportsPin && w.CanPin;
            SupportsProfile = features.Contains("profile");
            SupportsDisplayName = features.Contains("displayname");
            SupportsLfg = features.Contains("lfg");
            ReadHistoryFeatures(features);
            ReadInviteFeatures(w, features);
            ReadCalendarFeatures(features);
            ReadFlightFeatures(features);
            ReadAtlasFeatures(features);
            ReadArmoryFeatures(features);
            ReadFilesFeatures(w, features);
            ReadBadgesFeatures(w, features);
            ReadRealmsFeatures(features);
            ReadClassicFeatures(w, features);
            ReadLivePosFeatures(features);
            var rs = w.Reactions.ToArray();
            ReactionSet = rs.Length > 0 ? rs : DefaultReactions;

            // the name directory (server 2.8): { login: display name } for people who may be offline - authoritative when present
            if (w.Names != null)
            {
                DisplayNames.Forget();
                foreach (var kv in w.Names) DisplayNames.Remember(kv.Key, kv.Value);
            }

            // people: the same objects stay (a resync must not flicker the lists), newcomers are added, who left is removed
            var incoming = new List<MemberVM>();
            foreach (var u in w.Users)
            {
                var pm = ParseMember(u);
                pm.IsMe = pm.Id == myId;
                incoming.Add(pm);
            }
            foreach (var gone in Members.Where(x => incoming.All(i => i.Id != x.Id)).ToList()) Members.Remove(gone);
            foreach (var pm in incoming)
            {
                var have = Members.FirstOrDefault(x => x.Id == pm.Id);
                if (have == null) { Members.Add(pm); continue; }
                have.IsMe = pm.IsMe; have.Guest = pm.Guest; have.Name = pm.Name;
                have.SetAvatar(pm.AvatarId, pm.Accent); have.SetDisplayName(pm.DisplayName);
                have.Room = pm.Room; have.Muted = pm.Muted; have.Deaf = pm.Deaf; have.ServerMuted = pm.ServerMuted;
                have.Status = pm.Status; have.Note = pm.Note; have.Role = pm.Role; have.Invisible = pm.Invisible;
                have.CopyGameFrom(pm);
                Remember(have);
            }
            Me = Members.FirstOrDefault(x => x.IsMe);
            AfterWelcomeBadges();
            if (Me != null) Me.Invisible = Invisible;
            LoadWelcomeCalendar(w);
            if (w.Profile != null) { MyProfile = ProfileData.From(w.Profile); MyProfileChanged?.Invoke(); }
            RefreshAuthorNames();

            // channels: a 1.x server has none - pretend it has one text channel and one room
            var chans = w.Channels;
            if (chans.Count == 0)
                chans = new List<LodgeChannelView>
                {
                    new LodgeChannelView { Id = "general", Name = "General", ChannelType = "text" },
                    new LodgeChannelView { Id = "voice", Name = "Voice", ChannelType = "voice" },
                };
            var keepSelected = Selected?.Id ?? Settings.LastChannel;
            var wantChannels = new List<ChannelVM>();
            foreach (var c in chans)
            {
                var id = c.Id;
                if (id == null) continue;
                if (!channelCache.TryGetValue(id, out var ch)) channelCache[id] = ch = new ChannelVM { Id = id };
                ch.Name = c.Name; ch.Type = c.ChannelType ?? "text"; ch.MinRole = c.MinRole; ch.Max = c.Max;
                wantChannels.Add(ch);
            }
            foreach (var id in channelCache.Keys.Where(k => wantChannels.All(w => w.Id != k)).ToList()) channelCache.Remove(id);
            SyncList(TextChannels, wantChannels.Where(c => !c.IsVoice).ToList());
            SyncList(VoiceRooms, wantChannels.Where(c => c.IsVoice).ToList());

            // history: per channel (2.x) or one list (1.x). Merged into what is already shown: same message objects (scroll position,
            // decoded pictures, an edit in progress survive), new ones inserted by time, deleted ones removed.
            if (w.History != null)
            {
                foreach (var h in w.History)
                {
                    MergeHistory(h.Channel, h.Items);
                    NoteWelcomeHistory(h.Channel, h.Items.Count);
                }
            }
            else
                foreach (var grp in w.OldHistory.GroupBy(x => x.Channel ?? TextChannels.FirstOrDefault()?.Id ?? "general"))
                {
                    foreach (var x in grp) x.Channel = grp.Key;
                    MergeHistory(grp.Key, grp.ToList());
                }

            // pins (the server keeps its own copy of each), then flag the ones still in the loaded history
            if (w.Pins != null)
            {
                foreach (var l in pins.Values) l.Clear();
                foreach (var l in messages.Values) foreach (var x in l) x.Pinned = false;
                foreach (var cp in w.Pins)
                {
                    var list = PinsOf(cp.Channel);
                    foreach (var p in cp.Items)
                    {
                        var pv = ParsePin(p);
                        if (pv == null) continue;
                        list.Add(pv);
                        var pm = MessagesOf(cp.Channel).FirstOrDefault(x => x.Id == pv.Id);
                        if (pm != null) pm.Pinned = true;
                    }
                }
            }
            // what the server can do (and what I may do) may have changed: every shown message follows
            foreach (var l in messages.Values)
                foreach (var x in l) { x.ReactEnabled = SupportsReact; x.PinEnabled = CanPin; x.CanDelete = CanModerate; x.RefreshAvatar(); x.RefreshNames(); }
            foreach (var ch in TextChannels) RecomputeUnread(ch);

            Select(TextChannels.FirstOrDefault(c => c.Id == keepSelected) ?? TextChannels.FirstOrDefault());
            if (MyRoom == null && rejoinRoom == null && pendingRoom == null) TakeVoiceResume();   // 3.0.1: back from an update restart
            if (MyRoom != null && VoiceRooms.All(r => r.Id != MyRoom)) LeaveVoice(false);
            else if (MyRoom != null) SendVoice(MyRoom); // resync on the same connection while in voice
            else if (rejoinRoom != null)
            {
                // the connection dropped while we were in voice: go back into the same room, quietly, same mute/deafen.
                // The server decides (the room may be full by now): see OnServerError.
                var room = rejoinRoom; bool wasMuted = rejoinMuted, wasDeaf = rejoinDeaf;
                rejoinRoom = null;
                if (VoiceRooms.Any(r => r.Id == room))
                {
                    pendingMuted = wasMuted; pendingDeaf = wasDeaf;
                    JoinRoom(room, quiet: true);
                }
            }
            Groups.Recompute();
            SyncRooms();
            StatusText = OnlineText;
            Auto.MarkSent(Clock(), "online", "");                // a fresh connection starts as Online on the server
            EvaluateAuto();
            if (Auto.Status != "online" || Auto.Note.Length > 0 || Invisible) SendStatus();
            lastGameJson = null;
            SendGame();
            SendPosition();                                     // after the game (the server needs my shared zone first); a no-op unless I share
            foreach (var r in VoiceRooms) r.InvisibleHint = Invisible;
            FlightSync(connect: true);                          // shared flight times (a no-op without the server feature)
            AtlasSync(connect: true);                          // the Lodge's Atlas pool (a no-op without the server feature)
            ArmorySync(true);                                   // the characters I chose to share (a no-op without the server feature)
            RequestFiles();                                     // the Files page (a no-op without the server feature)
            Changed?.Invoke();
            // a gentle, one-time nudge: without a display name everybody sees the login name
            if (SupportsDisplayName && Me != null && !Me.Guest && string.IsNullOrEmpty(Me.DisplayName) && !Settings.DisplayNameHintShown)
            {
                Settings.DisplayNameHintShown = true;
                SettingsStore.Save(Settings);
                Hint?.Invoke("Tip: pick a display name in Settings > Profile - it is what others see instead of your login name");
            }
        }

        // keep `list` equal to `want` (same objects, same order) with the fewest changes
        static void SyncList<T>(ObservableCollection<T> list, List<T> want)
        {
            for (int i = list.Count - 1; i >= 0; i--) if (!want.Contains(list[i])) list.RemoveAt(i);
            for (int i = 0; i < want.Count; i++)
            {
                if (i < list.Count && ReferenceEquals(list[i], want[i])) continue;
                var at = list.IndexOf(want[i]);
                if (at >= 0) list.Move(at, i); else list.Insert(i, want[i]);
            }
        }

        // a member entry of join / user / welcome.users (typed: Elans.Shared LodgeUser)
        MemberVM ParseMember(LodgeUser u)
        {
            var room = u.Room;
            if (room == null && u.Voice) room = "voice"; // 1.x server: one voice channel
            var m = new MemberVM
            {
                Id = u.Id, Name = u.Name, Guest = u.Guest, IsMe = Me != null && u.Id == Me.Id,
                Room = room, Muted = u.Muted, Deaf = u.Deaf, ServerMuted = u.ServerMuted,
                Status = u.Status, Note = u.Note, Role = u.Role, Invisible = u.Invisible,
            };
            m.SetAvatar(u.AvatarId, u.Accent);
            if (u.HasDisplayName) { m.SetDisplayName(u.DisplayName); DisplayNames.Remember(m.Name, m.DisplayName); }
            ApplyGame(m, u.Game);
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

        // user.game (the raw presence object of a LodgeUser), read through Elans.Shared LodgeGame; null = not shared
        static void ApplyGame(MemberVM m, object raw)
        {
            var g = raw is JsonObject d ? LodgeGame.Read(new LodgeJsonFields(d)) : null;
            m.SetGame(g != null && g.Playing, g?.Name, g?.Class, g?.ClassFile, g?.Level ?? 0, g?.Zone, g?.Guild, g?.Race, g?.RaceFile, g?.Sex ?? 0);
            m.SetPresence(g?.Flags ?? 0, g?.XpPct ?? -1, g?.Rested ?? false, g?.GroupSize ?? 0, g?.InInstance ?? false, g?.InstanceName);
        }

        // the "game" message: what I play and what I'm doing (older servers ignore the extra fields). Zone/instance and XP/rested are
        // left out when their sharing is off; the flags (combat, dead, AFK, group) always go.
        public JsonObject BuildGameMessage()
        {
            var vis = Invisible ? "invisible" : "visible";
            if (Settings.ShareGameOff) return LodgeJson.Build(new GameMsg { Share = false, Visibility = vis }.Write);
            var c = Presence.Current;
            var g = new GameMsg { Playing = Presence.Playing, Visibility = vis };      // typed: Elans.Shared GameMsg (the Has* switches say which parts go)
            if (c != null)
            {
                g.HasCharacter = true;
                g.Name = c.Name; g.Realm = c.Realm; g.Class = c.Class; g.ClassFile = c.ClassFile;
                g.Level = c.Level; g.Guild = c.Guild;
                g.Race = c.Race; g.RaceFile = c.RaceFile; g.Sex = c.Sex;
                if (!Settings.ShareZoneOff) { g.HasZone = true; g.Zone = c.Zone; }
                if (c.HasLive)
                {
                    g.HasLive = true;
                    g.Flags = c.CombatKnown ? c.Flags : c.Flags & ~1; g.GroupSize = c.GroupSize; g.InInstance = c.InInstance;
                    if (!Settings.ShareZoneOff) g.InstanceName = c.InstanceName;           // sent only when there is one
                    if (!Settings.ShareXpOff) { g.HasXp = true; g.XpPct = c.XpPercent >= 0 ? c.XpPercent : (int?)null; g.Rested = c.Rested; }
                }
            }
            return LodgeJson.Build(g.Write);
        }

        // sent right away when something I chose changes, and by the 0.8 s timer for live changes; identical messages are skipped
        public void SendGame()
        {
            if (!Online) return;
            var obj = BuildGameMessage();
            var json = LodgeJson.Serialize(obj);
            if (json == lastGameJson) return;
            lastGameJson = json;
            _ = Send(obj);
            SendPosition();                                     // sharing the zone (or the game) switched off takes the position with it
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
            voice?.SetRoomMembers(VoiceRosterFor(MyRoom));   // 3.1.1: only these are played (VoiceEngine.Incoming)
        }

        // who the roster puts in `room`, me excepted: the only senders whose voice frames are played
        public List<int> VoiceRosterFor(string room) =>
            room == null ? new List<int>() : Members.Where(x => !x.IsMe && x.Room == room).Select(x => x.Id).Distinct().ToList();


        // ================================================================ messages

        MessageVM AddMessage(ChatMessage m)
        {
            var vm = BuildMessage(m, out var list);
            list.Add(vm);
            return vm;
        }

        static bool IsContinuation(MessageVM prev, MessageVM cur) => prev != null && prev.From == cur.From && (cur.At - prev.At).TotalMinutes < 5;

        MessageVM BuildMessage(ChatMessage m, out ObservableCollection<MessageVM> list)
        {
            var channel = m.Channel ?? TextChannels.FirstOrDefault()?.Id ?? "general";
            list = MessagesOf(channel);
            var at = DateTimeOffset.FromUnixTimeMilliseconds(m.At).UtcDateTime;
            var prev = list.LastOrDefault();
            var from = m.From;
            var reply = m.ReplyTo;
            var vm = new MessageVM
            {
                Id = m.Id, Channel = channel, FromId = m.FromId ?? 0, From = from, At = at,
                Text = SafeFile.Text(m.Text), Edited = m.Edited,
                Mine = Me != null && string.Equals(from, Me.Name, StringComparison.OrdinalIgnoreCase),
                CanDelete = CanModerate,
                ReplyFrom = SafeFile.Who(reply?.By ?? reply?.From), ReplyText = SafeFile.Text(reply?.Snippet ?? reply?.Text), ReplyId = reply?.Id,
                ReactEnabled = SupportsReact, PinEnabled = CanPin,
            };
            vm.Continuation = prev != null && IsContinuation(prev, vm);
            if (m.Kind == "lfg" && m.Lfg != null) { vm.Lfg = new LfgVM { Msg = vm }; vm.Lfg.Apply(m.Lfg); }
            if (m.Kind == "event" && m.Event != null && SupportsCalendar) vm.Event = EventForCard(m.Event);
            foreach (var r in m.Reactions.OrderBy(k => Array.IndexOf(ReactionSet, k.Id)))
                ApplyReaction(vm, r.Id, r.Users.ToArray());
            vm.Mentioned = !vm.Mine && MentionsMe(vm.Text, from);
            var f = m.File;
            if (f != null)
            {
                // the name is cleaned here, before anything else sees it (older servers send it as uploaded)
                vm.File = new FileVM { Id = f.Id, Name = SafeFile.Name(f.Name), Size = f.Size, Mime = f.Mime };
                if (vm.File.IsImage) _ = LoadImage(vm.File);
            }
            return vm;
        }

        // the welcome's history for one channel, merged into the messages already shown
        void MergeHistory(string channel, List<ChatMessage> items)
        {
            var list = MessagesOf(channel);
            long oldest = items.Count > 0 ? items.Min(x => x.At) : long.MaxValue;
            var ids = new HashSet<string>(items.Select(x => x.Id));
            // deleted while I was away: inside the time window the server just sent, but not in it
            if (items.Count > 0)
                foreach (var gone in list.Where(v => v.Id != null && Ms(v.At) >= oldest && !ids.Contains(v.Id)).ToList()) list.Remove(gone);
            var byId = new Dictionary<string, MessageVM>();
            foreach (var v in list) if (v.Id != null) byId[v.Id] = v;
            foreach (var x in items)
            {
                if (x.Id != null && byId.TryGetValue(x.Id, out var have)) { UpdateMessage(have, x); continue; }
                var vm = BuildMessage(x, out _);
                int at = list.Count;
                while (at > 0 && list[at - 1].At > vm.At) at--;
                list.Insert(at, vm);
            }
            for (int i = 0; i < list.Count; i++) list[i].Continuation = i > 0 && IsContinuation(list[i - 1], list[i]);
        }

        void UpdateMessage(MessageVM vm, ChatMessage x)
        {
            var text = SafeFile.Text(x.Text);
            if (vm.Text != text) vm.Text = text;
            if (x.Kind == "event" && x.Event != null && SupportsCalendar) vm.Event = EventForCard(x.Event);
            if (x.Kind == "lfg" && x.Lfg != null)
            {
                if (vm.Lfg == null) vm.Lfg = new LfgVM { Msg = vm };
                vm.Lfg.Apply(x.Lfg);
            }
            vm.Edited = x.Edited;
            vm.Mentioned = !vm.Mine && MentionsMe(vm.Text, vm.From);
            var keep = new HashSet<string>();
            foreach (var r in x.Reactions.OrderBy(k => Array.IndexOf(ReactionSet, k.Id)))
            {
                keep.Add(r.Id);
                ApplyReaction(vm, r.Id, r.Users.ToArray());
            }
            foreach (var r in vm.Reactions.Where(r => !keep.Contains(r.Emoji)).ToList()) vm.Reactions.Remove(r);
            vm.RaiseReactions();
        }

        // a message was deleted: unread numbers, the "New messages" line and the neighbour's header follow
        void RemoveMessage(ObservableCollection<MessageVM> list, MessageVM d, ChannelVM ch)
        {
            int idx = list.IndexOf(d);
            if (ch != null)
            {
                if (d.Unread)
                {
                    d.Unread = false;
                    ch.UnreadCount = Math.Max(0, ch.UnreadCount - 1);
                    if (d.Mentioned) ch.Mentions = Math.Max(0, ch.Mentions - 1);
                    if (ch.UnreadCount == 0) ch.Unread = false;
                }
                var next = idx >= 0 ? list.Skip(idx + 1).FirstOrDefault(x => x.Unread) : null;
                if (ch.FirstUnreadId == d.Id) ch.FirstUnreadId = next?.Id;
                if (ch.Divider == d)
                {
                    d.ShowDivider = false; ch.Divider = null;
                    if (next != null) { ch.Divider = next; next.ShowDivider = true; }
                }
                UnreadChanged?.Invoke(TextChannels.Any(c => c.Unread));
            }
            list.Remove(d);
            // the next message may have been a follow-up of the deleted one: it needs its header again
            if (idx >= 0 && idx < list.Count) list[idx].Continuation = idx > 0 && IsContinuation(list[idx - 1], list[idx]);
        }

        // @name / @display name for me; @everyone and @here only count from officers and up (a lower rank can't ping the lodge)
        bool MentionsMe(string text, string from)
        {
            if (string.IsNullOrEmpty(text) || Me == null) return false;
            var group = Members.FirstOrDefault(x => string.Equals(x.Name, from, StringComparison.OrdinalIgnoreCase));
            bool mayPingAll = group != null && MemberVM.RoleLevel(group.Role) >= MemberVM.RoleLevel("officer");
            return Regex.IsMatch(text, @"(?<![\w@])@(" + DisplayNames.MentionPattern(Me.Name, Me.DisplayName) + (mayPingAll ? @"|everyone|here" : "") + @")(?![\w])", RegexOptions.IgnoreCase);
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
                vm.Unread = true;
                ch.Unread = true;
                ch.UnreadCount++;
                if (vm.Mentioned) ch.Mentions++;
                if (ch.FirstUnreadId == null) ch.FirstUnreadId = vm.Id;
                if (ch == Selected && ch.Divider == null) { ch.Divider = vm; vm.ShowDivider = true; }
                UnreadChanged?.Invoke(true);
            }
            if (shown) looking = true; // (notifications: the window is looking at this channel)
            bool toasted = false;
            if (vm.Mentioned) toasted = ToastFor(ToastKind.Mention, vm, vm.From, string.IsNullOrEmpty(vm.Text) ? "shared " + vm.File?.Name : OneLine(vm.Text));
            else if (Me != null && vm.ReplyFrom != null && string.Equals(vm.ReplyFrom, Me.Name, StringComparison.OrdinalIgnoreCase))
                toasted = ToastFor(ToastKind.Reply, vm, vm.From, string.IsNullOrEmpty(vm.Text) ? "shared " + vm.File?.Name : OneLine(vm.Text));
            var mode = Settings.NotifyMode ?? "mentions";
            if (vm.Mentioned && mode != "none") Sounds.Mention();
            // one notification per message: when a toast is on screen the tray balloon stays quiet
            bool toastOnScreen = toasted && (ToastWillShow?.Invoke() ?? false);
            if (!looking && !toastOnScreen && (mode == "all" || (mode == "mentions" && vm.Mentioned)))
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

        readonly object marksLock = new object();
        readonly DispatcherTimer marksTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        bool marksDirty;

        void LoadMarks()
        {
            lock (marksLock)
            {
                if (readMarks != null) return;
                var loaded = new Dictionary<string, ReadMark>();
                try
                {
                    if (File.Exists(ReadFile))
                    {
                        var o = Json.Obj(File.ReadAllText(ReadFile));
                        if (o != null)
                            foreach (var kv in o)
                                if (kv.Value is Dictionary<string, object> d) loaded[kv.Key] = new ReadMark { Id = d.Str("id"), At = d.Long("at") };
                    }
                }
                catch (Exception e) { Util.Log("read state: " + e.Message); }
                readMarks = loaded;
            }
        }

        // the state is snapshotted on the UI thread and written a moment later on a worker: reading a channel costs no disk time
        void SaveMarks()
        {
            marksDirty = true;
            if (!marksTimer.IsEnabled) marksTimer.Start();
        }

        string MarksJson()
        {
            lock (marksLock)
            {
                var o = new Dictionary<string, object>();
                foreach (var kv in readMarks) o[kv.Key] = new Dictionary<string, object> { ["id"] = kv.Value.Id, ["at"] = (double)kv.Value.At };
                return Json.Write(o);
            }
        }

        void WriteMarksAsync()
        {
            if (!marksDirty || readMarks == null) return;
            marksDirty = false;
            var json = MarksJson();
            Task.Run(() => WriteMarksFile(json));
        }

        // now, on this thread (leaving the lodge, quitting)
        public void FlushMarks()
        {
            marksTimer.Stop();
            if (!marksDirty || readMarks == null) return;
            marksDirty = false;
            WriteMarksFile(MarksJson());
        }

        static readonly object fileLock = new object();

        // temp file in the same folder, then replaced in one step: a crash leaves the old file or the new one, never half
        static void WriteMarksFile(string json)
        {
            lock (fileLock)
                try
                {
                    var tmp = ReadFile + ".tmp";
                    File.WriteAllText(tmp, json);
                    if (File.Exists(ReadFile)) File.Replace(tmp, ReadFile, null);
                    else File.Move(tmp, ReadFile);
                }
                catch (Exception e) { Util.Log("read state: " + e.Message); }
        }

        static long Ms(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

        // everything up to and including `upTo` is read; clears the counters (the divider stays until I leave)
        void SetReadMark(ChannelVM ch, MessageVM upTo)
        {
            if (ch == null) return;
            LoadMarks();
            if (upTo != null) { lock (marksLock) readMarks[ReadKey(ch)] = new ReadMark { Id = upTo.Id, At = Ms(upTo.At) }; SaveMarks(); }
            bool changed = ch.Unread || ch.Mentions > 0 || ch.UnreadCount > 0;
            foreach (var x in MessagesOf(ch.Id)) x.Unread = false;
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
            foreach (var x in list) x.Unread = false;
            var unread = list.Skip(start).Where(x => !x.Mine).ToList();
            foreach (var x in unread) x.Unread = true;
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
            users = users.Select(u => SafeFile.Who(u)).Where(u => !string.IsNullOrEmpty(u)).ToArray();   // 3.1.1: cleaned like every name
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

        PinVM ParsePin(ChatPin p)
        {
            if (p == null || p.Id == null) return null;
            return new PinVM
            {
                // 3.1.1: the pin's text, names and file name are cleaned like a message's (the "pinned:" toast shows the same)
                Id = p.Id, Text = SafeFile.Text(p.Text), By = SafeFile.Who(p.By), PinnedBy = SafeFile.Who(p.PinnedBy),
                At = DateTimeOffset.FromUnixTimeMilliseconds(p.At).UtcDateTime, FileName = p.File?.Name == null ? null : SafeFile.Name(p.File.Name), CanUnpin = CanPin,

            };
        }

        public Task React(MessageVM msg, string emoji) => Send(LodgeJson.Build(new ReactMsg { Id = msg.Id, Emoji = emoji }.Write));
        public Task PinMessage(string id) => Send(LodgeJson.Build(new PinMsg { Id = id }.Write));
        public Task UnpinMessage(string id, string channel) => Send(LodgeJson.Build(new UnpinMsg { Id = id, Channel = channel }.Write));

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

        // the self-test sees what would go out: the bytes as the Lodge would parse them, as plain dictionaries (LodgeJson.ToPlain)
        public Action<Dictionary<string, object>> SentForTest;
        Task Send(JsonObject obj)
        {
            var bytes = LodgeJson.SerializeUtf8(obj);                       // the shared Lodge JSON engine (Elans.Shared LodgeJson)
            SentForTest?.Invoke(LodgeJson.ToPlain(LodgeJson.Parse(bytes)) as Dictionary<string, object>);
            return client?.SendUtf8(bytes) ?? Task.CompletedTask;
        }

        // The text I just sent, until the server echoes it. When the send fails (offline, closed socket, rate limit) the view puts it back.
        public sealed class PendingSend { public string Text, ReplyId, Channel; public DateTime At; }
        public PendingSend Pending { get; private set; }

        // false = not sent (nothing was queued); `why` says so. The caller keeps the text in the box.
        public bool TrySend(string text, string replyTo, out string why)
        {
            why = null;
            if (!Online && !TestFed) { why = "You are offline - your message is waiting in the box"; return false; }
            Pending = new PendingSend { Text = text, ReplyId = replyTo, Channel = Selected?.Id, At = Clock() };
            _ = SendMessage(text, replyTo);
            return true;
        }

        void FailPending(string why)
        {
            var p = Pending;
            if (p == null) return;
            Pending = null;
            SendFailed?.Invoke(p, why);
            Error?.Invoke(why);
        }

        void OnServerError(string text, string code, string room)
        {
            text = text ?? "";
            if (code == "last-owner") { LeaveRefused?.Invoke(text); return; }   // the last Guild Master tried to leave
            if (HistoryRateError(text)) return;
            if (code == "noreps") { OnRepsOff(); return; }                  // the Lodge owner switched reputations off
            if (code == "notfound" || (code == "disabled" && text.IndexOf("Armory", StringComparison.OrdinalIgnoreCase) >= 0) || (code == "guest" && text.IndexOf("Armory", StringComparison.OrdinalIgnoreCase) >= 0)) { OnArmoryError(code); return; }
            if (pendingRoom != null && (code == "room-full" || text.IndexOf("is full (", StringComparison.OrdinalIgnoreCase) >= 0 || text.StartsWith("You can't join", StringComparison.OrdinalIgnoreCase)))
            {
                // the server said no: never connected, no mic opened
                pendingRoom = null; pendingMuted = pendingDeaf = false;
                Error?.Invoke(code == "room-full" || text.IndexOf("is full", StringComparison.OrdinalIgnoreCase) >= 0 ? text + " - you're not in voice" : text);
                SyncRooms();
                Changed?.Invoke();
                return;
            }
            if (Pending != null && (code == "rate" || text.StartsWith("Slow down", StringComparison.OrdinalIgnoreCase) || text.StartsWith("You can't write", StringComparison.OrdinalIgnoreCase)))
            {
                FailPending(text);
                return;
            }
            if (OnBadgesError(text, code)) return;      // this feature stays quiet
            Error?.Invoke(text);
        }

        public Task SendMessage(string text, string replyTo = null, string fileId = null)
        {
            // appearing offline: messages still carry my name - say so once
            if (Invisible && !Settings.InvisibleHintShown)
            {
                Settings.InvisibleHintShown = true;
                SettingsStore.Save(Settings);
                Error?.Invoke("Others can see messages you send, even while you appear offline");
            }
            return Send(LodgeJson.Build(new ChatSendMsg { Channel = Selected?.Id, Text = text ?? "", ReplyTo = replyTo, FileId = fileId }.Write));
        }

        // ---- sign-up cards ("Looking for group")
        public event Action<MessageVM> LfgChanged;
        // the server decides everything (limits, one place per person, who may close); these only ask
        public Task PostLfg(string title, bool now, DateTime startLocal, int tank, int healer, int dps, string note)
        {
            var lfg = new LfgRequest { Title = title ?? "", Now = now, Note = note ?? "", Tank = tank, Healer = healer, Dps = dps };
            if (!now) lfg.Start = Ms(startLocal.ToUniversalTime());
            return Send(LodgeJson.Build(new ChatSendMsg { Channel = Selected?.Id, Kind = "lfg", Text = title ?? "", Lfg = lfg }.Write));
        }
        public Task LfgJoin(MessageVM m, string role) => Send(LodgeJson.Build(new LfgJoinMsg { Id = m.Id, Role = role }.Write));
        public Task LfgMaybe(MessageVM m) => Send(LodgeJson.Build(new LfgMaybeMsg { Id = m.Id }.Write));
        public Task LfgLeave(MessageVM m) => Send(LodgeJson.Build(new LfgLeaveMsg { Id = m.Id }.Write));
        public Task LfgClose(MessageVM m) => Send(LodgeJson.Build(new LfgCloseMsg { Id = m.Id }.Write));
        public Task LfgCancel(MessageVM m) => Send(LodgeJson.Build(new LfgCancelMsg { Id = m.Id }.Write));

        readonly HashSet<string> lfgReminded = new HashSet<string>();
        public static readonly TimeSpan LfgRemindBefore = TimeSpan.FromMinutes(10);

        // every few seconds: countdowns move on, and a toast 10 minutes before the start for the groups I signed up for (or organise)
        public void LfgTick()
        {
            var now = Clock();
            foreach (var kv in messages.ToList())
                foreach (var m in kv.Value.ToList())
                {
                    var l = m.Lfg;
                    if (l == null) continue;
                    l.RefreshClock();
                    if (!l.IsOpen || l.IsNow || Me == null || lfgReminded.Contains(m.Id)) continue;
                    var left = l.StartUtc - now;
                    if (left <= TimeSpan.Zero || left > LfgRemindBefore) continue;
                    bool inIt = (l.MyRole != null && l.MyRole != "maybe") || l.IAmAuthor;
                    if (!inIt) continue;
                    lfgReminded.Add(m.Id);
                    var ch = TextChannels.FirstOrDefault(c => c.Id == m.Channel);
                    Toasts.Push(new ToastItem
                    {
                        Kind = ToastKind.Event, Sender = l.Title + " starts in " + Math.Max(1, (int)Math.Ceiling(left.TotalMinutes)) + " min",
                        Channel = ch?.Name, ChannelId = m.Channel, Who = Me,
                        Text = l.MyRole != null && l.MyRole != "maybe" ? "You are signed up as " + (l.MyRole == "tank" ? "Tank" : l.MyRole == "healer" ? "Healer" : "DPS") : "You are organising this group",
                    });
                }
        }

        public Task Edit(string id, string text) => Send(LodgeJson.Build(new EditMsg { Id = id, Text = text }.Write));
        public Task Delete(string id) => Send(LodgeJson.Build(new DeleteMsg { Id = id }.Write));

        DateTime lastTyping;
        public void Typing()
        {
            if (!Online || (DateTime.UtcNow - lastTyping).TotalSeconds < 3) return;
            lastTyping = DateTime.UtcNow;
            _ = Send(LodgeJson.Build(new TypingMsg { Channel = Selected?.Id }.Write));
        }

        // ================================================================ files

        public async Task Share(string path, string text, string replyTo, Action<double> progress = null)
        {
            if (!Online) return;
            if (!CanShareFiles) throw new InvalidOperationException("Initiates can't share files");
            if (new FileInfo(path).Length > MaxFileMb * 1024L * 1024L)
                throw new InvalidOperationException($"{Path.GetFileName(path)} is larger than {MaxFileMb} MB");
            using (Activity.Begin("Uploading " + Path.GetFileName(path) + " to your Lodge", Activity.Kind.Save))
                await ShareNow(path, text, replyTo, progress);
        }

        async Task ShareNow(string path, string text, string replyTo, Action<double> progress)
        {
            var meta = await client.Upload(path, progress);
            await SendMessage(text, replyTo, LodgeJson.Str(meta, "id"));
        }

        static string CacheDir => Directory.CreateDirectory(Path.Combine(Util.DataDir, "cache")).FullName;

        async Task LoadImage(FileVM f)
        {
            var id = SafeFile.Id(f.Id);
            if (id == null || failedImages.Contains(id)) return;      // never asked for again this session
            var path = SafeFile.PathIn(CacheDir, id + SafeFile.Ext(f.Name));
            if (path == null) return;
            try
            {
                if (!File.Exists(path)) { if (client == null) return; await client.Download(id, path, LodgeClient.CapFor(f.Size, FileVM.AutoImageMax), default, ImageTimeout); }
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
            catch (Exception e)
            {
                if ((e is LodgeClient.DownloadException de && de.Status == 404) || e is LodgeClient.DownloadStoppedException) failedImages.Add(id);   // 3.1.1: a stopped one is not tried again either
                Util.Log("image: " + e.Message);
            }
        }

        // where a download goes: always inside `dir`, with the cleaned name; null = refused
        public static string DownloadTarget(string dir, FileVM f) => SafeFile.FreePath(dir, f?.Name);

        public Task Download(FileVM f, string target) => client.Download(f.Id, target, ChatFileCap(f?.Size ?? 0));

        // 3.1.1: how much a chat file download may bring: the size the Lodge announced for it, else the Lodge's MaxFileMb - never more than
        // LodgeClient.HardCeiling. A picture that is downloaded on its own (LoadImage) stops at FileVM.AutoImageMax and after ImageTimeout.
        public long ChatFileCap(long announced) => announced > 0 ? LodgeClient.CapFor(announced, LodgeClient.HardCeiling) : Math.Min(MaxFileMb * 1024L * 1024L, LodgeClient.HardCeiling);
        public static TimeSpan ImageTimeout = TimeSpan.FromMinutes(2);


        // ================================================================ voice

        public void JoinRoom(string room) => JoinRoom(room, false);

        // The join is only asked for here. MyRoom (and the microphone) follow when the server confirms it with my own "user"
        // update - a full room answers with an error instead and nothing was opened.
        string pendingRoom;
        DateTime pendingSince;
        bool pendingMuted, pendingDeaf, pendingQuiet;
        public string PendingRoom => pendingRoom;

        // quiet: no join sound (rejoining after a dropped connection)
        public void JoinRoom(string room, bool quiet)
        {
            if ((!Online && !TestFed) || room == null) return;
            if (room == MyRoom) return;
            pendingRoom = room; pendingSince = Clock(); pendingQuiet = quiet;
            SendVoice(room);
            Changed?.Invoke();
        }

        void CommitRoom()
        {
            var room = pendingRoom;
            pendingRoom = null;
            if (room == null || client == null) return;
            if (voice == null)
            {
                voice = new VoiceEngine
                {
                    PushToTalk = Settings.VoicePushToTalk,
                    PttKey = PttKey,
                    ThresholdDb = Settings.VoiceThreshold ?? -45,
                    Preset = VoicePresets.Get(Settings.VoicePreset),
                    NoiseSuppression = !Settings.VoiceNoiseSuppressOff,
                    AutoGain = !Settings.VoiceAutoGainOff,
                    HighQuality = Settings.VoiceHighQuality,
                    PeerVolume = id => PeerVolumeFor(Members.FirstOrDefault(x => x.Id == id)?.Name),
                };
                var c = client;
                voice.Send = (p, n) => _ = c.SendVoice(p, n);
                voice.Start(Settings.VoiceInput ?? -1, Settings.VoiceOutput ?? -1, (float)(Settings.VoiceVolume ?? 1));
                voice.Muted = pendingMuted || pendingDeaf;
                voice.Deafened = pendingDeaf;
            }
            pendingMuted = pendingDeaf = false;
            MyRoom = room;
            _ = Send(LodgeJson.Build(new VoiceStateMsg { Muted = voice.Muted, Deaf = voice.Deafened }.Write));
            if (!pendingQuiet) Sounds.Join();
            SyncRooms();
            Changed?.Invoke();
        }

        void SendVoice(string room) =>
            _ = Send(LodgeJson.Build(new VoiceMsg { Room = room, On = true }.Write)); // "on" for 1.x servers

        public void LeaveVoice(bool tellServer = true)
        {
            if (MyRoom == null && voice == null && pendingRoom == null) return;
            pendingRoom = null;
            MyRoom = null;
            voice?.Dispose();
            voice = null;
            if (tellServer && client != null) _ = Send(LodgeJson.Build(new VoiceMsg { Room = null, On = false }.Write));
            if (tellServer) Sounds.Leave();
            foreach (var x in Members) x.Speaking = false;
            SyncRooms();
            Changed?.Invoke();
        }

        public void SetMuteDeaf(bool muted, bool deaf)
        {
            if (voice == null) { if (pendingRoom != null) { pendingMuted = muted; pendingDeaf = deaf; } return; }
            voice.Muted = muted || deaf; // deafened people can't talk either
            voice.Deafened = deaf;
            voice.Volume = deaf ? 0 : (float)(Settings.VoiceVolume ?? 1);
            _ = Send(LodgeJson.Build(new VoiceStateMsg { Muted = voice.Muted, Deaf = deaf }.Write));
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
            voice.NoiseSuppression = !Settings.VoiceNoiseSuppressOff;
            voice.AutoGain = !Settings.VoiceAutoGainOff;
            voice.HighQuality = Settings.VoiceHighQuality;
            if (!voice.Deafened) voice.Volume = (float)(Settings.VoiceVolume ?? 1);
        }

        void OnTick()
        {
            tickCount++;
            foreach (var m in Members)
            {
                bool inRoom = MyRoom != null && m.Room == MyRoom && voice != null;
                m.Speaking = inRoom && (m.IsMe ? voice.Transmitting : voice.IsSpeaking(m.Id));
                m.SpeakLevel = inRoom ? (m.IsMe ? voice.MyLevel : voice.SpeakLevel(m.Id)) : 0;
            }
            if (tickCount % 50 == 0) CheckIdle();
            if (Pending != null && Clock() - Pending.At > TimeSpan.FromSeconds(15)) Pending = null;   // surely out by now
            if (pendingRoom != null && Clock() - pendingSince > TimeSpan.FromSeconds(6)) { pendingRoom = null; Error?.Invoke("Couldn't join the voice room - no answer from the lodge"); Changed?.Invoke(); }
            AutoStatusTick();
            if (tickCount % 20 == 0) { LfgTick(); CalendarTick(); FlightTick(); AtlasTick(); ArmoryTick(); }
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
            _ = Send(LodgeJson.Build(new StatusMsg { Status = Auto.Status, Note = Auto.Note, Visibility = Invisible ? "invisible" : "visible" }.Write));
        }

        // every 100 ms: send an automatic status change when it really changed (debounced to 2 s)
        void AutoStatusTick()
        {
            if (!Online) return;
            EvaluateAuto();
            if (Auto.TrySend(Clock(), out var st, out var note))
            {
                _ = Send(LodgeJson.Build(new StatusMsg { Status = st, Note = note, Visibility = Invisible ? "invisible" : "visible" }.Write));
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
            if (SupportsProfile && !string.IsNullOrEmpty(name)) _ = Send(LodgeJson.Build(new ProfileGetMsg { Name = name }.Write));
        }

        // own profile: only the fields given are changed (the server validates everything and answers with profile:data)
        public Task SaveProfile(ProfileSetMsg fields) => Send(LodgeJson.Build(fields.Write));

        // owner only: back to the default avatar and accent
        public void ResetProfile(string name, bool clearText = false) =>
            _ = Send(LodgeJson.Build(new ProfileResetMsg { Name = name, ClearText = clearText }.Write));

        // ================================================================ officers and the guild master

        // (typed: shared/Elans.Shared/Lodge/AdminMessages.cs)
        public void RequestAdmin() => _ = Send(LodgeJson.Build(new AdminMembersGetMsg().Write));
        public void Invite(string name, string role) => _ = Send(LodgeJson.Build(new AdminInviteMsg { Name = name, Role = role }.Write));
        public void SetRole(string name, string role) => _ = Send(LodgeJson.Build(new AdminRoleMsg { Name = name, Role = role }.Write));
        public void RemoveMember(string name) => _ = Send(LodgeJson.Build(new AdminRemoveMsg { Name = name }.Write));
        // owner (server 2.22): take everything this member shared with the Atlas back out; they stay a member
        public void RevertAtlas(string name) => _ = Send(LodgeJson.Build(new AdminPurgeMsg { Name = name, AtlasOnly = true }.Write));
        public void AddChannel(string name, string type, string minRole, int max) =>
            _ = Send(LodgeJson.Build(new AdminChannelAddMsg { Name = name, ChannelType = type, MinRole = minRole, Max = max }.Write));
        public void RemoveChannel(string id) => _ = Send(LodgeJson.Build(new AdminChannelRemoveMsg { Id = id }.Write));
        public void Kick(MemberVM m) => _ = Send(LodgeJson.Build(new ModKickMsg { Id = m.Id }.Write));
        public void ServerMute(MemberVM m, bool on) => _ = Send(LodgeJson.Build(new ModMuteMsg { Id = m.Id, On = on }.Write));

        public bool CanModerateMember(MemberVM m) => CanModerate && m != null && !m.IsMe && MemberVM.RoleLevel(m.Role) < MemberVM.RoleLevel(MyRole);

        // test hooks
        public Task SendForTest(string text) => SendMessage(text);
        // offline rendering test: feed protocol frames without a server
        public bool TestFed { get; private set; }
        public bool TestFedForView => TestFed;
        // the text goes through the same parse as a received frame (LodgeJson.Parse); one that would be skipped is skipped here too
        public void FeedForTest(string json) { TestFed = true; var m = LodgeJson.Parse(System.Text.Encoding.UTF8.GetBytes(json ?? "")); if (m != null) OnReceived(m); }
        public void SetReadMarkForTest(string channel, string id, long at)
        {
            LoadMarks();
            lock (marksLock) readMarks["local|" + channel] = new ReadMark { Id = id, At = at };
        }
        public bool MarksDirtyForTest => marksDirty;
        public bool ChannelObjectKept(string id) => channelCache.ContainsKey(id);
    }
}
