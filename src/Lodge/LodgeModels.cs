using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

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
        int mentions;
        public bool Selected { get => selected; set { if (Set(ref selected, value)) { Raise(nameof(RowBrush)); Raise(nameof(NameBrush)); } } }
        public bool Unread { get => unread; set { if (Set(ref unread, value)) { Raise(nameof(NameBrush)); Raise(nameof(NameWeight)); Raise(nameof(UnreadVisibility)); } } }
        public int Mentions { get => mentions; set { if (Set(ref mentions, value)) { Raise(nameof(MentionVisibility)); Raise(nameof(UnreadVisibility)); } } }
        public bool IAmHere { get => here; set { if (Set(ref here, value)) Raise(nameof(NameBrush)); } } // the voice room I'm in

        public Brush RowBrush => selected ? Avatar.Res("SurfaceHi") : Brushes.Transparent;
        public Brush NameBrush => selected || unread || here ? Avatar.Res("Text") : Avatar.Res("TextDim");
        public FontWeight NameWeight => unread ? FontWeights.SemiBold : FontWeights.Normal;
        public Visibility MentionVisibility => mentions > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility UnreadVisibility => unread && mentions == 0 ? Visibility.Visible : Visibility.Collapsed;
        public void RefreshCount() => Raise(nameof(CountText));
    }

    // ================================================================ people

    public class MemberVM : Bindable
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public bool Guest { get; set; }
        public bool IsMe { get; set; }
        public Brush Color => Avatar.ColorFor(Name);
        public string Initial => Avatar.Initial(Name);

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
        void RaiseVoice() { Raise(nameof(StateGlyph)); Raise(nameof(StateBrush)); Raise(nameof(MutedVisibility)); }

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
        string gameName, gameClass, gameClassFile, gameZone, gameGuild;
        int gameLevel;
        public void SetGame(bool playing, string name, string cls, string classFile, int level, string zone, string guild)
        {
            this.playing = playing; gameName = name; gameClass = cls; gameClassFile = classFile; gameLevel = level; gameZone = zone; gameGuild = guild;
            Raise(nameof(GameLine)); Raise(nameof(ClassBrush)); Raise(nameof(ClassBrushOrText)); Raise(nameof(GameVisibility)); Raise(nameof(PlayingVisibility));
            Raise(nameof(Tip)); Raise(nameof(GameDetail));
        }
        public bool Playing => playing;
        static readonly System.Collections.Generic.Dictionary<string, string> ClassColors = new System.Collections.Generic.Dictionary<string, string>
        {
            ["HUNTER"] = "#ABD473", ["WARRIOR"] = "#C79C6E", ["MAGE"] = "#69CCF0", ["ROGUE"] = "#FFF569", ["DRUID"] = "#FF7D0A",
            ["PALADIN"] = "#F58CBA", ["PRIEST"] = "#FFFFFF", ["SHAMAN"] = "#0070DE", ["WARLOCK"] = "#9482C9",
        };
        // names take their class colour once we know it (like in WoW)
        public Brush ClassBrushOrText => gameClassFile != null && ClassColors.ContainsKey(gameClassFile) ? ClassBrush : Avatar.Res("Text");
        public Brush ClassBrush => gameClassFile != null && ClassColors.TryGetValue(gameClassFile, out var hex) ? Avatar.Frozen(hex) : Avatar.Res("TextDim");
        // "42 Hunter" next to the name
        public string GameLine => gameLevel > 0 ? $"{gameLevel}" : "";
        public Visibility GameVisibility => gameLevel > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility PlayingVisibility => playing ? Visibility.Visible : Visibility.Collapsed;
        public string GameDetail
        {
            get
            {
                if (gameName == null && !playing) return null;
                var who = gameName == null ? "" : $"{gameName}, level {gameLevel} {gameClass}";
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
                Raise(nameof(NameOpacity)); Raise(nameof(Tip)); Raise(nameof(StatusLine));
            }
        }
        public string Note { get => note; set { if (Set(ref note, value)) { Raise(nameof(Tip)); Raise(nameof(StatusLine)); } } }

        public static string StatusLabel(string s) =>
            s == "away" ? "AFK" : s == "busy" ? "Busy" : s == "dungeon" ? "In a dungeon" : s == "lfg" ? "Looking for group" : "Online";
        public static string GlyphFor(string s) =>
            s == "away" ? "" : s == "busy" ? "" : s == "dungeon" ? "" : s == "lfg" ? "" : "";
        static readonly Brush Away = Avatar.Frozen("#E6B85C"), Busy = Avatar.Frozen("#E06C6C"), Dungeon = Avatar.Frozen("#B48CE0"),
                              Lfg = Avatar.Frozen("#6CB4E0"), On = Avatar.Frozen("#ABD473");
        public static Brush BrushFor(string s) => s == "away" ? Away : s == "busy" ? Busy : s == "dungeon" ? Dungeon : s == "lfg" ? Lfg : On;

        public string StatusGlyph => GlyphFor(status);
        public Brush StatusBrush => BrushFor(status);
        public Visibility StatusVisibility => status == "online" ? Visibility.Collapsed : Visibility.Visible;
        public double NameOpacity => status == "away" ? 0.55 : 1;
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

        string text;
        bool edited, mentioned;
        public string Text { get => text; set { if (Set(ref text, value)) Raise(nameof(TextVisibility)); } }
        public bool Edited { get => edited; set { if (Set(ref edited, value)) Raise(nameof(EditedVisibility)); } }
        public bool Mentioned { get => mentioned; set { if (Set(ref mentioned, value)) { Raise(nameof(RowBackground)); Raise(nameof(MentionBar)); } } }

        public Brush Color => Avatar.ColorFor(From);
        public string Initial => Avatar.Initial(From);
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
        public Brush RowBackground => mentioned ? Avatar.Frozen("#1FE6B85C") : Brushes.Transparent;
        public Brush MentionBar => mentioned ? Avatar.Frozen("#E6B85C") : Brushes.Transparent;
    }
}
