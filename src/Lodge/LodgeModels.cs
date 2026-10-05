using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using ElansAddonHub.Services;

namespace ElansAddonHub.Lodge
{
    public abstract class Bindable : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        protected void Raise([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            Raise(name);
            return true;
        }
    }

    static class Avatar
    {
        // warm, readable colours; the same name always gets the same one
        static readonly string[] Palette = { "#ABD473", "#E6B85C", "#6CB4E0", "#E07C6C", "#B48CE0", "#5CC8A8", "#E09C5C", "#D07CB0" };

        public static Brush ColorFor(string name)
        {
            int h = 0;
            foreach (var c in (name ?? "").ToLowerInvariant()) h = h * 31 + c;
            return Frozen(Palette[Math.Abs(h % Palette.Length)]);
        }

        public static Brush Frozen(string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }

        public static string Initial(string name) => string.IsNullOrEmpty(name) ? "?" : name.Substring(0, 1).ToUpperInvariant();
        public static Brush Res(string key) => (Brush)Application.Current.Resources[key];
    }

    // ================================================================ channels

    public class ChannelVM : Bindable
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Type { get; set; }   // text | voice
        public string MinRole { get; set; }
        public int Max { get; set; }
        public bool IsVoice => Type == "voice";
        public string Glyph => IsVoice ? "" : "#";
        public string LockText => MinRole == "veteran" || MinRole == "officer" || MinRole == "owner" ? MemberVM.RoleName(MinRole) + "+" : "";
        public Visibility LockVisibility => LockText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        // people in this voice room (kept in sync by the session)
        public ObservableCollection<MemberVM> Members { get; } = new ObservableCollection<MemberVM>();
        public string CountText => IsVoice && Max > 0 ? $"{Members.Count}/{Max}" : "";

        bool selected, unread, here;
        int mentions, unreadCount;
        public string FirstUnreadId { get; set; }   // the first message I haven't read (the "New messages" line goes above it)
        public MessageVM Divider { get; set; }      // the message currently carrying the line (cleared when I leave the channel)
        public bool Selected { get => selected; set { if (Set(ref selected, value)) { Raise(nameof(RowBrush)); Raise(nameof(NameBrush)); } } }
        public bool Unread { get => unread; set { if (Set(ref unread, value)) { Raise(nameof(NameBrush)); Raise(nameof(NameWeight)); Raise(nameof(UnreadVisibility)); } } }
        public int Mentions { get => mentions; set { if (Set(ref mentions, value)) { Raise(nameof(MentionVisibility)); Raise(nameof(MentionText)); Raise(nameof(UnreadVisibility)); } } }
        public int UnreadCount { get => unreadCount; set { if (Set(ref unreadCount, value)) { Raise(nameof(UnreadText)); Raise(nameof(UnreadVisibility)); } } }
        public string MentionText => "@" + (mentions > 99 ? "99+" : mentions.ToString());
        public string UnreadText => unreadCount > 99 ? "99+" : unreadCount.ToString();
        public bool IAmHere { get => here; set { if (Set(ref here, value)) { Raise(nameof(NameBrush)); Raise(nameof(InvisibleHintVisibility)); } } } // the voice room I'm in
        bool invisibleHint;
        // while I appear offline, joining a voice room shows me in it: say so on the room
        public bool InvisibleHint { get => invisibleHint; set { if (Set(ref invisibleHint, value)) Raise(nameof(InvisibleHintVisibility)); } }
        public Visibility InvisibleHintVisibility => invisibleHint && IsVoice && !here ? Visibility.Visible : Visibility.Collapsed;

        public Brush RowBrush => selected ? Avatar.Res("SurfaceHi") : Brushes.Transparent;
        public Brush NameBrush => selected || unread || here ? Avatar.Res("Text") : Avatar.Res("TextDim");
        public FontWeight NameWeight => unread ? FontWeights.SemiBold : FontWeights.Normal;
        public Visibility MentionVisibility => mentions > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility UnreadVisibility => unreadCount > 0 && mentions == 0 ? Visibility.Visible : Visibility.Collapsed;
        public void RefreshCount() => Raise(nameof(CountText));
    }

    // ================================================================ people

    public class MemberVM : Bindable
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public bool Guest { get; set; }
        public bool IsMe { get; set; }
        // the avatar is the class colour + race code once we know the character (e.g. NE on orange); otherwise name colour + initial
        public Brush Color => gameClassFile != null && ClassColors.TryGetValue(gameClassFile, out var hex) ? Avatar.Frozen(hex) : Avatar.ColorFor(Name);
        // with a class icon found on this PC (see AvatarArt) the avatar shows it instead of the race code
        public Brush IconBrush => gameClassFile != null ? AvatarArt.ClassBrush(gameClassFile) : null;
        public Visibility InitialVisibility => IconBrush != null ? Visibility.Collapsed : Visibility.Visible;
        public string Initial => gameRaceFile != null ? RaceCode(gameRaceFile) : Avatar.Initial(Name);
        static readonly System.Collections.Generic.Dictionary<string, string> RaceCodes = new System.Collections.Generic.Dictionary<string, string>
        {
            ["Human"] = "Hu", ["Dwarf"] = "Dw", ["NightElf"] = "NE", ["Gnome"] = "Gn", ["Orc"] = "Or", ["Scourge"] = "Ud", ["Undead"] = "Ud",
            ["Tauren"] = "Ta", ["Troll"] = "Tr", ["BloodElf"] = "BE", ["Draenei"] = "Dr", ["Goblin"] = "Go", ["Worgen"] = "Wo",
        };
        // ---- personal avatar (Lodge server 2.6): a preset picture + accent colour. None chosen = the class-colour default above.
        string avatarId, accent;
        public string AvatarId => avatarId;
        public string Accent => accent;
        public bool HasAvatar => avatarId != null;
        public ImageSource AvatarImage => AvatarCatalog.Image(avatarId, accent);
        public Visibility PersonalVisibility => HasAvatar ? Visibility.Visible : Visibility.Collapsed;
        public Visibility DefaultVisibility => HasAvatar ? Visibility.Collapsed : Visibility.Visible;
        // the class badge (bottom-right, 40% of the avatar): only with a personal avatar and only while they are in game
        public Visibility BadgeVisibility => HasAvatar && playing && gameClassFile != null ? Visibility.Visible : Visibility.Collapsed;
        public Brush BadgeRing => ClassBrush;
        public string BadgeText => string.IsNullOrEmpty(gameClass) ? "?" : gameClass.Substring(0, 1).ToUpperInvariant();
        public Visibility BadgeTextVisibility => IconBrush == null ? Visibility.Visible : Visibility.Collapsed;
        public bool SetAvatar(string id, string accent)
        {
            id = AvatarCatalog.IsAvatar(id) ? id : null;
            accent = AvatarCatalog.IsAccent(accent) ? accent : null;
            if (id == avatarId && accent == this.accent) return false;
            avatarId = id; this.accent = accent;
            RaiseAvatar();
            return true;
        }
        void RaiseAvatar()
        {
            Raise(nameof(AvatarId)); Raise(nameof(Accent)); Raise(nameof(HasAvatar)); Raise(nameof(AvatarImage)); Raise(nameof(PersonalVisibility));
            Raise(nameof(DefaultVisibility)); Raise(nameof(BadgeVisibility)); Raise(nameof(BadgeRing)); Raise(nameof(BadgeText)); Raise(nameof(BadgeTextVisibility));
        }
        // what a message row needs to know about its author's badge: changes when they log in/out of the game or switch class
        // (also covers the default look: class icon / race code / class colour, so chat rows match the member list)
        public string BadgeKey => (HasAvatar ? avatarId + "/" + accent : "") + "|" + (playing ? gameClassFile + "/" + gameRaceFile : "");

        static string RaceCode(string raceFile) => RaceCodes.TryGetValue(raceFile, out var c) ? c : raceFile.Substring(0, Math.Min(2, raceFile.Length));

        // ---- voice
        string room;
        bool muted, deaf, serverMuted, speaking, inMyRoom;
        public string Room { get => room; set { if (Set(ref room, value)) { Raise(nameof(Voice)); Raise(nameof(StateVisibility)); RaiseVoice(); } } }
        public bool Voice => room != null;
        public bool Muted { get => muted; set { if (Set(ref muted, value)) RaiseVoice(); } }
        public bool Deaf { get => deaf; set { if (Set(ref deaf, value)) RaiseVoice(); } }
        public bool ServerMuted { get => serverMuted; set { if (Set(ref serverMuted, value)) { RaiseVoice(); Raise(nameof(Tip)); } } }
        public bool Speaking { get => speaking; set { if (Set(ref speaking, value)) Raise(nameof(Ring)); } }
        // the overlay only lists people in my room
        public bool InMyRoom { get => inMyRoom; set { if (Set(ref inMyRoom, value)) Raise(nameof(OverlayVisibility)); } }
        void RaiseVoice() { Raise(nameof(StateGlyph)); Raise(nameof(StateBrush)); Raise(nameof(MutedVisibility)); Raise(nameof(CardVoiceLine)); Raise(nameof(CardVoiceVisibility)); Raise(nameof(Tip)); }

        string roomName;
        public string RoomName { get => roomName; set { if (Set(ref roomName, value)) { Raise(nameof(CardVoiceLine)); Raise(nameof(CardVoiceVisibility)); } } }
        public Brush Ring => speaking ? Avatar.Res("Accent") : Brushes.Transparent;
        public string StateGlyph => deaf ? "" : (muted || serverMuted) ? "" : "";
        public Brush StateBrush => deaf || muted || serverMuted ? Avatar.Res("Danger") : Avatar.Res("TextDim");
        public Visibility StateVisibility => Voice ? Visibility.Visible : Visibility.Collapsed;
        public Visibility MutedVisibility => Voice && (muted || deaf || serverMuted) ? Visibility.Visible : Visibility.Collapsed;
        public Visibility OverlayVisibility => inMyRoom ? Visibility.Visible : Visibility.Collapsed;

        // ---- rank: WoW item-quality frames
        string role = "member";
        public string Role
        {
            get => role;
            set
            {
                if (!Set(ref role, string.IsNullOrEmpty(value) ? "member" : value)) return;
                Raise(nameof(FrameBrush)); Raise(nameof(FrameColor)); Raise(nameof(GlowVisibility));
                Raise(nameof(ShimmerVisibility)); Raise(nameof(RoleLabel)); Raise(nameof(Tip));
            }
        }
        public static readonly string[] RoleOrder = { "guest", "member", "veteran", "officer", "owner" };
        public static int RoleLevel(string r) => Math.Max(0, Array.IndexOf(RoleOrder, r ?? "member"));
        public static string RoleName(string r) =>
            r == "owner" ? "Guild Master" : r == "officer" ? "Officer" : r == "veteran" ? "Veteran" : r == "guest" ? "Initiate" : "Member";
        // legendary, epic, rare, uncommon, poor - a touch brighter than in-game so they read on dark
        public static Color RoleColor(string r) => (Color)ColorConverter.ConvertFromString(
            r == "owner" ? "#FF8000" : r == "officer" ? "#B048F8" : r == "veteran" ? "#2F8FFF" : r == "guest" ? "#9D9D9D" : "#3EE03E");
        public string RoleLabel => RoleName(role);
        public Color FrameColor => RoleColor(role);
        public Brush FrameBrush { get { var b = new SolidColorBrush(FrameColor); b.Freeze(); return b; } }
        public Visibility GlowVisibility => role == "officer" || role == "owner" ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ShimmerVisibility => role == "owner" ? Visibility.Visible : Visibility.Collapsed;

        // ---- what they play (Elan's Hub companion addon + "WoW is running")
        bool playing;
        string gameName, gameClass, gameClassFile, gameZone, gameGuild, gameRace, gameRaceFile;
        int gameSex;
        int gameLevel;
        public void SetGame(bool playing, string name, string cls, string classFile, int level, string zone, string guild, string race = null, string raceFile = null, int sex = 0)
        {
            gameRace = race; gameRaceFile = raceFile; gameSex = sex;
            this.playing = playing; gameName = name; gameClass = cls; gameClassFile = classFile; gameLevel = level; gameZone = zone; gameGuild = guild;
            Raise(nameof(GameLine)); Raise(nameof(ClassBrush)); Raise(nameof(ClassBrushOrText)); Raise(nameof(GameVisibility)); Raise(nameof(PlayingVisibility));
            Raise(nameof(Tip)); Raise(nameof(GameDetail)); Raise(nameof(Color)); Raise(nameof(Initial)); Raise(nameof(IconBrush)); Raise(nameof(InitialVisibility));
            Raise(nameof(BadgeVisibility)); Raise(nameof(BadgeRing)); Raise(nameof(BadgeText)); Raise(nameof(BadgeTextVisibility));
            RaiseCard();
        }

        // a copy of what someone plays now (the profile editor's preview card)
        public void CopyGameFrom(MemberVM o)
        {
            SetGame(o.playing, o.gameName, o.gameClass, o.gameClassFile, o.gameLevel, o.gameZone, o.gameGuild, o.gameRace, o.gameRaceFile, o.gameSex);
            SetPresence(o.flags, o.xpPct, o.rested, o.groupSize, o.inInstance, o.instanceName);
        }

        // ---- live presence (Lodge server 2.5 / companion 1.5): flags 1 combat 2 dead 4 AFK 8 resting 16 instance 32 raid 64 party 128 group
        int flags, xpPct = -1, groupSize;
        bool rested, inInstance;
        string instanceName;
        public void SetPresence(int flags, int xpPct, bool rested, int groupSize, bool inInstance, string instanceName)
        {
            this.flags = flags; this.xpPct = xpPct; this.rested = rested; this.groupSize = groupSize; this.inInstance = inInstance; this.instanceName = instanceName;
            RaiseCard();
        }
        public bool InCombat => (flags & 1) != 0;
        public bool IsDead => (flags & 2) != 0;
        public bool GameAfk => (flags & 4) != 0;
        public int GroupSize => groupSize;
        public int XpPercent => xpPct;
        public bool Rested => rested;
        public bool InInstance => inInstance;

        // the second line of a row and the card's place line: "The Barrens · 62% · Rested", "Wailing Caverns · group 5"
        public string Place => inInstance && !string.IsNullOrEmpty(instanceName) ? instanceName : gameZone;
        public string PlaceLine
        {
            get
            {
                if (!playing) return "";
                var parts = new System.Collections.Generic.List<string>();
                if (!string.IsNullOrEmpty(Place)) parts.Add(Place);
                if (!inInstance && xpPct >= 0) parts.Add(xpPct + "%");
                if (!inInstance && rested) parts.Add("Rested");
                if (groupSize > 1) parts.Add("group " + groupSize);
                return string.Join(" · ", parts);
            }
        }
        // sidebar second line: the place line, else the status note when there is one
        public string SecondLine => invisible && !IsMe ? "invisible" : PlaceLine.Length > 0 ? PlaceLine : (string.IsNullOrEmpty(note) ? "" : note);
        public Visibility SecondVisibility => SecondLine.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        // small state glyphs (vector art from the app's reaction icons, the moon from the icon font)
        public ImageSource StateIcon => IsDead ? ReactionArt.Icon("wipe") : InCombat ? ReactionArt.Icon("fight") : null;
        public Visibility StateIconVisibility => playing && StateIcon != null ? Visibility.Visible : Visibility.Collapsed;
        public Visibility AfkVisibility => playing && GameAfk && !IsDead && !InCombat ? Visibility.Visible : Visibility.Collapsed;
        public string StateText => !playing ? "" : IsDead ? "Dead" : InCombat ? "In combat" : GameAfk ? "AFK" : "";

        // ---- appear offline: self and (for the owner) other invisible members
        bool invisible;
        public bool Invisible
        {
            get => invisible;
            set
            {
                if (!Set(ref invisible, value)) return;
                Raise(nameof(InvisibleVisibility)); Raise(nameof(StatusVisibility)); Raise(nameof(NameOpacity)); Raise(nameof(SecondLine));
                Raise(nameof(SecondVisibility)); Raise(nameof(Tip)); RaiseCard();
            }
        }
        public Visibility InvisibleVisibility => invisible ? Visibility.Visible : Visibility.Collapsed;

        // ---- the hover card
        void RaiseCard()
        {
            Raise(nameof(SecondLine)); Raise(nameof(SecondVisibility)); Raise(nameof(PlaceLine)); Raise(nameof(StateIcon)); Raise(nameof(StateIconVisibility));
            Raise(nameof(AfkVisibility)); Raise(nameof(StateText)); Raise(nameof(CardClassLine)); Raise(nameof(CardClassVisibility));
            Raise(nameof(CardPlaceLine)); Raise(nameof(CardPlaceVisibility)); Raise(nameof(CardStatusLine)); Raise(nameof(CardStatusVisibility));
            Raise(nameof(CardVoiceLine)); Raise(nameof(CardVoiceVisibility)); Raise(nameof(CardStateVisibility)); Raise(nameof(Tip));
        }
        public string CardNameText => IsMe ? Name + " (you)" : Name;
        // "Level 25 Human Paladin · Measley" (the character's name only when it differs from the Lodge name)
        public string CardClassLine
        {
            get
            {
                if (gameLevel <= 0 && gameClass == null) return "";
                var s = (gameLevel > 0 ? "Level " + gameLevel + " " : "") + (gameRace != null ? gameRace + " " : "") + gameClass;
                s = s.Trim();
                if (!string.IsNullOrEmpty(gameName) && !string.Equals(gameName, Name, StringComparison.OrdinalIgnoreCase)) s += " · " + gameName;
                return s;
            }
        }
        public Visibility CardClassVisibility => CardClassLine.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        public string CardPlaceLine => playing ? PlaceLine : (gameName != null ? "Not in game" : "");
        public Visibility CardPlaceVisibility => CardPlaceLine.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility CardStateVisibility => StateText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        // dim line: the chosen status and note (never "Online" alone), the officer's mute, and the invisible marker
        public string CardStatusLine
        {
            get
            {
                var parts = new System.Collections.Generic.List<string>();
                if (status != "online") parts.Add(StatusLabel(status));
                if (!string.IsNullOrEmpty(note)) parts.Add(note);
                if (serverMuted) parts.Add("muted by an officer");
                if (invisible) parts.Add(IsMe ? "Invisible" : "invisible");
                return string.Join(" · ", parts);
            }
        }
        public Visibility CardStatusVisibility => CardStatusLine.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        public string CardVoiceLine => Voice ? (string.IsNullOrEmpty(roomName) ? "In voice" : roomName) + (deaf ? " · deafened" : muted || serverMuted ? " · muted" : "") : "";
        public Visibility CardVoiceVisibility => Voice ? Visibility.Visible : Visibility.Collapsed;
        public bool Playing => playing;
        static readonly System.Collections.Generic.Dictionary<string, string> ClassColors = new System.Collections.Generic.Dictionary<string, string>
        {
            ["HUNTER"] = "#ABD473", ["WARRIOR"] = "#C79C6E", ["MAGE"] = "#69CCF0", ["ROGUE"] = "#FFF569", ["DRUID"] = "#FF7D0A",
            ["PALADIN"] = "#F58CBA", ["PRIEST"] = "#FFFFFF", ["SHAMAN"] = "#0070DE", ["WARLOCK"] = "#9482C9",
        };
        // names take their class colour once we know it (like in WoW)
        public Brush ClassBrushOrText => gameClassFile != null && ClassColors.ContainsKey(gameClassFile) ? ClassBrush : Avatar.Res("Text");
        public Brush ClassBrush => gameClassFile != null && ClassColors.TryGetValue(gameClassFile, out var hex) ? Avatar.Frozen(hex) : Avatar.Res("TextDim");
        public static Brush ClassBrushFor(string classFile) => classFile != null && ClassColors.TryGetValue(classFile.ToUpperInvariant(), out var hex) ? Avatar.Frozen(hex) : Avatar.Res("TextDim");
        // a stand-in for someone who isn't online (profile card from the server's data): never in the lists
        public bool Offline { get; set; }
        public string StatusLabelText => StatusLabel(status);
        // "42 Hunter" next to the name
        public string GameLine => gameLevel > 0 ? $"{gameLevel}" : "";
        public Visibility GameVisibility => gameLevel > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility PlayingVisibility => playing ? Visibility.Visible : Visibility.Collapsed;
        public string GameDetail
        {
            get
            {
                if (gameName == null && !playing) return null;
                var who = gameName == null ? "" : $"{gameName}, level {gameLevel} {(gameRace != null ? (gameSex == 3 ? "female " : gameSex == 2 ? "male " : "") + gameRace + " " : "")}{gameClass}";
                var where = gameZone == null ? "" : $" - {gameZone}";
                var guild = gameGuild == null ? "" : $" <{gameGuild}>";
                return (playing ? "Playing " : "Last played ") + (who.Length > 0 ? who + guild + where : "WoW");
            }
        }

        // ---- presence
        string status = "online", note;
        public string Status
        {
            get => status;
            set
            {
                if (!Set(ref status, string.IsNullOrEmpty(value) ? "online" : value)) return;
                Raise(nameof(StatusGlyph)); Raise(nameof(StatusBrush)); Raise(nameof(StatusVisibility));
                Raise(nameof(NameOpacity)); Raise(nameof(Tip)); Raise(nameof(StatusLine)); RaiseCard();
            }
        }
        public string Note { get => note; set { if (Set(ref note, value)) { Raise(nameof(Tip)); Raise(nameof(StatusLine)); RaiseCard(); } } }

        public static string StatusLabel(string s) =>
            s == "away" ? "AFK" : s == "busy" ? "Busy" : s == "dungeon" ? "In a dungeon" : s == "lfg" ? "Looking for group" : "Online";
        public static string GlyphFor(string s) =>
            s == "away" ? "" : s == "busy" ? "" : s == "dungeon" ? "" : s == "lfg" ? "" : "";
        static readonly Brush Away = Avatar.Frozen("#E6B85C"), Busy = Avatar.Frozen("#E06C6C"), Dungeon = Avatar.Frozen("#B48CE0"),
                              Lfg = Avatar.Frozen("#6CB4E0"), On = Avatar.Frozen("#ABD473");
        public static Brush BrushFor(string s) => s == "away" ? Away : s == "busy" ? Busy : s == "dungeon" ? Dungeon : s == "lfg" ? Lfg : On;

        public string StatusGlyph => GlyphFor(status);
        public Brush StatusBrush => BrushFor(status);
        public Visibility StatusVisibility => status == "online" || invisible ? Visibility.Collapsed : Visibility.Visible;
        public double NameOpacity => status == "away" || invisible ? 0.55 : 1;
        public string StatusLine => string.IsNullOrEmpty(note) ? StatusLabel(status) : note;
        public string Tip => (IsMe ? Name + " (you)" : Name) + " · " + RoleName(role)
            + (status != "online" ? " - " + StatusLabel(status) : "")
            + (string.IsNullOrEmpty(note) ? "" : ": " + note)
            + (serverMuted ? " (muted by an officer)" : "")
            + (GameDetail != null ? "\n" + GameDetail : "");
    }

    // ================================================================ messages

    public class FileVM : Bindable
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public long Size { get; set; }
        public string Mime { get; set; }
        public bool IsImage => Mime != null && Mime.StartsWith("image/") && Size <= 8 * 1024 * 1024;
        public string SizeText => Size < 1024 ? $"{Size} B" : Size < 1024 * 1024 ? $"{Size / 1024.0:0} KB" : $"{Size / 1048576.0:0.0} MB";

        ImageSource image;
        public ImageSource Image { get => image; set { if (Set(ref image, value)) { Raise(nameof(ImageVisibility)); Raise(nameof(CardVisibility)); } } }
        public string LocalPath { get; set; }
        public Visibility ImageVisibility => image != null ? Visibility.Visible : Visibility.Collapsed;
        public Visibility CardVisibility => image != null ? Visibility.Collapsed : Visibility.Visible;

        string status;
        public string Status { get => status; set => Set(ref status, value); }
    }

    public class MessageVM : Bindable
    {
        public string Id { get; set; }
        public string Channel { get; set; }
        public int FromId { get; set; }
        public string From { get; set; }
        public DateTime At { get; set; }
        public FileVM File { get; set; }
        public bool Continuation { get; set; } // same person, shortly after: no avatar/name
        public bool Mine { get; set; }
        public bool CanDelete { get; set; }
        public string ReplyFrom { get; set; }
        public string ReplyText { get; set; }
        public string ReplyId { get; set; }
        public bool ReactEnabled { get; set; }   // the server supports reactions / pins (welcome "features")
        public bool PinEnabled { get; set; }     // ... and I may pin
        bool forceTools;
        public bool ForceTools { get => forceTools; set => Set(ref forceTools, value); } // screenshots only
        public ObservableCollection<ReactionVM> Reactions { get; } = new ObservableCollection<ReactionVM>();
        public Brush ReplyColor => Avatar.ColorFor(ReplyFrom);

        string text;
        bool edited, mentioned, divider, flash, pinned;
        public bool ShowDivider { get => divider; set { if (Set(ref divider, value)) Raise(nameof(DividerVisibility)); } }
        public bool Flash { get => flash; set { if (Set(ref flash, value)) { Raise(nameof(RowBackground)); } } }
        public bool Pinned { get => pinned; set { if (Set(ref pinned, value)) Raise(nameof(PinnedVisibility)); } }
        public Visibility DividerVisibility => divider ? Visibility.Visible : Visibility.Collapsed;
        public Visibility PinnedVisibility => pinned ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ReactToolVisibility => ReactEnabled ? Visibility.Visible : Visibility.Collapsed;
        public Visibility PinToolVisibility => PinEnabled ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ReactionsVisibility => Reactions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        public void RaiseReactions() => Raise(nameof(ReactionsVisibility));
        public string Text { get => text; set { if (Set(ref text, value)) Raise(nameof(TextVisibility)); } }
        public bool Edited { get => edited; set { if (Set(ref edited, value)) Raise(nameof(EditedVisibility)); } }
        public bool Mentioned { get => mentioned; set { if (Set(ref mentioned, value)) { Raise(nameof(RowBackground)); Raise(nameof(MentionBar)); } } }

        // the default look follows the author while they are here (class colour, class icon / race code), like the member list
        public Brush Color => Author?.Color ?? Avatar.ColorFor(From);
        public string Initial => Author?.Initial ?? Avatar.Initial(From);
        // the member behind the name (the hover card); null once they have left
        public static Func<string, MemberVM> AuthorLookup;
        public MemberVM Author => From == null ? null : AuthorLookup?.Invoke(From);
        // personal avatar of the author: the live member when still here, else what we last knew (so history keeps its pictures)
        public static Func<string, string[]> AvatarLookup;   // name -> { avatarId, accent } or null
        string[] KnownAvatar => From == null ? null : AvatarLookup?.Invoke(From);
        string AvId => Author?.AvatarId ?? KnownAvatar?[0];
        public ImageSource AvatarImage => AvatarCatalog.Image(AvId, Author != null ? Author.Accent : KnownAvatar?[1]);
        public Visibility PersonalVisibility => AvatarImage != null ? Visibility.Visible : Visibility.Collapsed;
        public Visibility DefaultVisibility => AvatarImage != null ? Visibility.Collapsed : Visibility.Visible;
        public Brush IconBrush => Author?.IconBrush;
        public Visibility InitialVisibility => IconBrush != null ? Visibility.Collapsed : Visibility.Visible;
        public Visibility BadgeVisibility => Author?.BadgeVisibility ?? Visibility.Collapsed;
        public Brush BadgeRing => Author?.BadgeRing;
        public string BadgeText => Author?.BadgeText;
        public Visibility BadgeTextVisibility => Author?.IconBrush == null ? Visibility.Visible : Visibility.Collapsed;
        public Brush FrameBrush => null;
        public Brush Ring => Brushes.Transparent;
        public Brush StatusBrush => null;
        public string StatusGlyph => "";
        public Visibility StatusVisibility => Visibility.Collapsed;
        public Visibility InvisibleVisibility => Visibility.Collapsed;
        public void RefreshAvatar()
        {
            Raise(nameof(AvatarImage)); Raise(nameof(PersonalVisibility)); Raise(nameof(DefaultVisibility)); Raise(nameof(BadgeVisibility));
            Raise(nameof(BadgeRing)); Raise(nameof(BadgeText)); Raise(nameof(BadgeTextVisibility)); Raise(nameof(Author));
            Raise(nameof(Color)); Raise(nameof(Initial)); Raise(nameof(IconBrush)); Raise(nameof(InitialVisibility));
        }
        public string Time
        {
            get
            {
                var local = At.ToLocalTime();
                if (local.Date == DateTime.Today) return local.ToString("HH:mm");
                if (local.Date == DateTime.Today.AddDays(-1)) return "Yesterday " + local.ToString("HH:mm");
                return local.ToString("d MMM HH:mm");
            }
        }
        bool Compact => Continuation && ReplyFrom == null;
        public Visibility HeaderVisibility => Compact ? Visibility.Collapsed : Visibility.Visible;
        public Visibility AvatarVisibility => Compact ? Visibility.Hidden : Visibility.Visible;
        public Thickness RowMargin => Compact ? new Thickness(0) : new Thickness(0, 10, 0, 0);
        public Visibility TextVisibility => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        public Visibility FileVisibility => File == null ? Visibility.Collapsed : Visibility.Visible;
        public Visibility ReplyVisibility => ReplyFrom == null ? Visibility.Collapsed : Visibility.Visible;
        public Visibility EditedVisibility => edited ? Visibility.Visible : Visibility.Collapsed;
        public Visibility EditVisibility => Mine ? Visibility.Visible : Visibility.Collapsed;
        public Visibility DeleteVisibility => Mine || CanDelete ? Visibility.Visible : Visibility.Collapsed;
        static readonly Brush MentionTint = Avatar.Frozen("#1FE6B85C"), FlashTint = Avatar.Frozen("#33ABD473"), Gold = Avatar.Frozen("#E6B85C");
        public Brush RowBackground => flash ? FlashTint : mentioned ? MentionTint : Brushes.Transparent;
        public Brush MentionBar => mentioned ? Gold : Brushes.Transparent;
    }

    // reaction art: canonical ids (server 2.4) draw as colour vector badges; anything else (2.3 servers: plain emoji) stays text
    public static class ReactionArt
    {
        static readonly System.Collections.Generic.Dictionary<string, string> names = new System.Collections.Generic.Dictionary<string, string>
        {
            ["ready"] = "Ready", ["notready"] = "Not ready", ["lol"] = "LOL", ["love"] = "Love",
            ["fight"] = "Let's fight", ["loot"] = "Loot!", ["wipe"] = "Wipe", ["epic"] = "Epic",
        };
        public static ImageSource Icon(string id)
        {
            if (string.IsNullOrEmpty(id) || !names.ContainsKey(id)) return null;
            return Application.Current?.TryFindResource("Reaction." + id) as ImageSource;
        }
        public static string Name(string id) => id != null && names.TryGetValue(id, out var n) ? n : id;
    }

    // one entry of the reaction picker
    public class ReactionChoice
    {
        public string Id { get; set; }
        public ImageSource Icon => ReactionArt.Icon(Id);
        public string Name => ReactionArt.Name(Id);
        public Visibility IconVisibility => Icon != null ? Visibility.Visible : Visibility.Collapsed;
        public Visibility TextVisibility => Icon != null ? Visibility.Collapsed : Visibility.Visible;
    }

    // one reaction pill under a message: the reaction (id or legacy emoji), who reacted
    public class ReactionVM : Bindable
    {
        static readonly Brush MineFill = Avatar.Frozen("#26ABD473"), PlainFill = Avatar.Frozen("#14FFFFFF"),
                              MineLine = Avatar.Frozen("#99ABD473"), PlainLine = Avatar.Frozen("#00000000");
        public MessageVM Msg { get; set; }
        public string Emoji { get; set; }   // the reaction key: a canonical id, or an emoji from an older server
        public ImageSource Icon => ReactionArt.Icon(Emoji);
        public Visibility IconVisibility => Icon != null ? Visibility.Visible : Visibility.Collapsed;
        public Visibility TextVisibility => Icon != null ? Visibility.Collapsed : Visibility.Visible;
        string[] users = new string[0];
        bool mine;
        public string[] Users => users;
        public void Update(string[] who, string me)
        {
            users = who ?? new string[0];
            mine = me != null && users.Any(u => string.Equals(u, me, StringComparison.OrdinalIgnoreCase));
            Raise(nameof(Count)); Raise(nameof(Mine)); Raise(nameof(Fill)); Raise(nameof(Line)); Raise(nameof(Tip));
        }
        public int Count => users.Length;
        public bool Mine => mine;
        public Brush Fill => mine ? MineFill : PlainFill;
        public Brush Line => mine ? MineLine : PlainLine;
        public string Tip => users.Length == 0 ? "" : ReactionArt.Name(Emoji) + ": " + string.Join(", ", users);
    }

    // a pinned message: the server keeps its own copy of the text, so it outlives the history window
    public class PinVM
    {
        public string Id { get; set; }
        public string Text { get; set; }
        public string By { get; set; }
        public string PinnedBy { get; set; }
        public DateTime At { get; set; }
        public string FileName { get; set; }
        public string Shown => !string.IsNullOrEmpty(Text) ? Text : FileName != null ? "[" + FileName + "]" : "";
        public Brush Color => Avatar.ColorFor(By);
        public string Time => At.ToLocalTime().ToString("d MMM HH:mm");
        public bool CanUnpin { get; set; }
        public Visibility UnpinVisibility => CanUnpin ? Visibility.Visible : Visibility.Collapsed;
    }
}
