using System;
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
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(Palette[Math.Abs(h % Palette.Length)]));
            b.Freeze();
            return b;
        }

        public static string Initial(string name) => string.IsNullOrEmpty(name) ? "?" : name.Substring(0, 1).ToUpperInvariant();
    }

    public class MemberVM : Bindable
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public bool Guest { get; set; }
        public bool IsMe { get; set; }
        public Brush Color => Avatar.ColorFor(Name);
        public string Initial => Avatar.Initial(Name);
        public string Label => IsMe ? Name + " (you)" : Name;

        // ---- rank (from the server): WoW item-quality frames
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

        // ---- presence
        string status = "online", note;
        public string Status
        {
            get => status;
            set
            {
                if (!Set(ref status, string.IsNullOrEmpty(value) ? "online" : value)) return;
                Raise(nameof(StatusGlyph)); Raise(nameof(StatusBrush)); Raise(nameof(StatusVisibility));
                Raise(nameof(NameOpacity)); Raise(nameof(Tip));
            }
        }
        public string Note { get => note; set { if (Set(ref note, value)) Raise(nameof(Tip)); } }

        public static string StatusLabel(string s) =>
            s == "away" ? "AFK" : s == "busy" ? "Busy" : s == "dungeon" ? "In a dungeon" : s == "lfg" ? "Looking for group" : "Online";
        public static string GlyphFor(string s) =>
            s == "away" ? "\uE708" : s == "busy" ? "\uE738" : s == "dungeon" ? "\uEA18" : s == "lfg" ? "\uE716" : "";
        static readonly Brush Away = Frozen("#E6B85C"), Busy = Frozen("#E06C6C"), Dungeon = Frozen("#B48CE0"), Lfg = Frozen("#6CB4E0"), On = Frozen("#ABD473");
        static Brush Frozen(string hex) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }
        public static Brush BrushFor(string s) => s == "away" ? Away : s == "busy" ? Busy : s == "dungeon" ? Dungeon : s == "lfg" ? Lfg : On;

        public string StatusGlyph => GlyphFor(status);
        public Brush StatusBrush => BrushFor(status);
        public Visibility StatusVisibility => status == "online" ? Visibility.Collapsed : Visibility.Visible;
        public double NameOpacity => status == "away" ? 0.55 : 1;
        public string Tip => (IsMe ? Name + " (you) - click to set your status" : Name)
            + " \u00b7 " + RoleName(role)
            + (status != "online" ? " - " + StatusLabel(status) : "")
            + (string.IsNullOrEmpty(note) ? "" : ": " + note);

        bool voice, muted, deaf, speaking;
        public bool Voice { get => voice; set { if (Set(ref voice, value)) { Raise(nameof(StateGlyph)); Raise(nameof(StateVisibility)); Raise(nameof(MutedVisibility)); } } }
        public bool Muted { get => muted; set { if (Set(ref muted, value)) { Raise(nameof(StateGlyph)); Raise(nameof(StateBrush)); Raise(nameof(MutedVisibility)); } } }
        public bool Deaf { get => deaf; set { if (Set(ref deaf, value)) { Raise(nameof(StateGlyph)); Raise(nameof(StateBrush)); Raise(nameof(MutedVisibility)); } } }
        public bool Speaking { get => speaking; set { if (Set(ref speaking, value)) Raise(nameof(Ring)); } }

        public Brush Ring => speaking ? (Brush)Application.Current.Resources["Accent"] : Brushes.Transparent;
        // headphones = in voice, crossed speaker = deafened, mic-off = muted
        public string StateGlyph => deaf ? "" : muted ? "" : "";
        public Brush StateBrush => deaf || muted ? (Brush)Application.Current.Resources["Danger"] : (Brush)Application.Current.Resources["TextDim"];
        public Visibility StateVisibility => voice ? Visibility.Visible : Visibility.Collapsed;
        public Visibility MutedVisibility => voice && (muted || deaf) ? Visibility.Visible : Visibility.Collapsed;
    }

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
        public int FromId { get; set; }
        public string From { get; set; }
        public DateTime At { get; set; }
        public string Text { get; set; }
        public FileVM File { get; set; }
        public bool Continuation { get; set; } // same person, shortly after: no avatar/name

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
        public Visibility HeaderVisibility => Continuation ? Visibility.Collapsed : Visibility.Visible;
        public Visibility AvatarVisibility => Continuation ? Visibility.Hidden : Visibility.Visible;
        public Thickness RowMargin => Continuation ? new Thickness(0, 1, 0, 0) : new Thickness(0, 12, 0, 0);
        public Visibility TextVisibility => string.IsNullOrEmpty(Text) ? Visibility.Collapsed : Visibility.Visible;
        public Visibility FileVisibility => File == null ? Visibility.Collapsed : Visibility.Visible;
    }
}
