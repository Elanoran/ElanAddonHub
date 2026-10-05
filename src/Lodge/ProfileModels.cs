using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using ElansAddonHub.Services;

namespace ElansAddonHub.Lodge
{
    // One character of a member (collected by the server from their presence).
    public class CharVM : Bindable
    {
        public string Name { get; set; }
        public string Class { get; set; }
        public string ClassFile { get; set; }
        public string Race { get; set; }
        public string RaceFile { get; set; }
        public int Level { get; set; }
        public long Seen { get; set; }
        public bool IsMain { get; set; }
        bool hidden;
        public bool Hidden { get => hidden; set { if (Set(ref hidden, value)) Raise(nameof(HiddenOpacity)); } }
        public double HiddenOpacity => hidden ? 0.5 : 1;
        public Brush ClassBrush => MemberVM.ClassBrushFor(ClassFile);
        public string Line => ((Level > 0 ? "Level " + Level + " " : "") + (Race ?? "") + " " + (Class ?? "")).Replace("  ", " ").Trim();
        public Visibility StarVisibility => IsMain ? Visibility.Visible : Visibility.Hidden;

        public static CharVM Parse(Dictionary<string, object> c) => new CharVM
        {
            Name = c.Str("name"), Class = c.Str("class"), ClassFile = c.Str("classFile"), Race = c.Str("race"), RaceFile = c.Str("raceFile"),
            Level = c.Int("level"), Seen = c.Long("seen"), IsMain = c.Bool("main"), hidden = c.Bool("hidden"),
        };
    }

    // What the server answered for a profile (profile:data) - own or someone else's.
    public class ProfileData
    {
        public string Name, Role, AvatarId, Accent, About = "", PlayTimes = "", Main, ShowChars = "all", VisibleTo = "everyone";
        public bool Found, Guest, Online, Restricted;
        public List<CharVM> Chars = new List<CharVM>();
        public Dictionary<string, object> Game;

        public static ProfileData Parse(Dictionary<string, object> p)
        {
            if (p == null) return null;
            var d = new ProfileData
            {
                Name = p.Str("name"), Found = p.Bool("found"), Guest = p.Bool("guest"), Role = p.Str("role") ?? "member", Online = p.Bool("online"),
                Restricted = p.Bool("restricted"), AvatarId = p.Str("avatarId"), Accent = p.Str("accent"), About = p.Str("about") ?? "",
                PlayTimes = p.Str("playTimes") ?? "", Main = p.Str("main"), ShowChars = p.Str("showChars") ?? "all", VisibleTo = p.Str("visibleTo") ?? "everyone",
                Game = p.Child("game"),
            };
            foreach (var c in p.List("chars").OfType<Dictionary<string, object>>()) d.Chars.Add(CharVM.Parse(c));
            return d;
        }
    }

    // The card of a person: banner in their accent colour, big avatar with class badge, name and rank, about, play times, what they play
    // now, their characters (main starred). Built from a live MemberVM (or a stand-in for someone offline) plus the server's profile.
    public class ProfileCardVM : Bindable
    {
        public MemberVM Member { get; }
        public ObservableCollection<CharVM> Chars { get; } = new ObservableCollection<CharVM>();
        ProfileData data;
        public bool Loading { get; set; }

        public ProfileCardVM(MemberVM member) { Member = member; }

        public ProfileData Data
        {
            get => data;
            set
            {
                data = value;
                Chars.Clear();
                if (data != null) foreach (var c in data.Chars) Chars.Add(c);
                RaiseAll();
            }
        }

        public void RaiseAll()
        {
            foreach (var n in new[] { nameof(About), nameof(AboutVisibility), nameof(PlayTimes), nameof(PlayTimesVisibility), nameof(CharsVisibility), nameof(NoteText),
                nameof(NoteVisibility), nameof(Banner), nameof(NowVisibility), nameof(NowLine), nameof(NowClassLine), nameof(OnlineText), nameof(OnlineBrush), nameof(Name), nameof(RoleLabel), nameof(FrameBrush), nameof(NameBrush) }) Raise(n);
        }

        public string Name => Member.Name;
        public string RoleLabel => Member.RoleLabel;
        public Brush FrameBrush => Member.FrameBrush;
        public Brush NameBrush => Member.ClassBrushOrText;

        // banner: the accent colour fading to the card colour; no accent = a quiet slate
        public Brush Banner
        {
            get
            {
                var hex = AvatarCatalog.IsAccent(Member.Accent) ? AvatarCatalog.AccentColor(Member.Accent) : Color.FromRgb(0x3A, 0x42, 0x55);
                Func<double, Color> shade = k => Color.FromRgb((byte)Math.Min(255, hex.R * k), (byte)Math.Min(255, hex.G * k), (byte)Math.Min(255, hex.B * k));
                var b = new LinearGradientBrush(shade(1.0), shade(0.40), 20);
                b.GradientStops.Insert(1, new GradientStop(shade(0.7), 0.5));
                b.Freeze();
                return b;
            }
        }

        public string About => data?.About ?? "";
        public Visibility AboutVisibility => About.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        public string PlayTimes => data?.PlayTimes ?? "";
        public Visibility PlayTimesVisibility => PlayTimes.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility CharsVisibility => Chars.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        public string NoteText => Loading && data == null ? "Loading profile..." : data != null && data.Restricted ? "This member keeps their profile to officers." : "";
        public Visibility NoteVisibility => NoteText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        public string OnlineText => Member.Playing ? "Playing" : Member.Id > 0 && !Member.Offline ? Member.StatusLabelText : "Offline";
        public Brush OnlineBrush => Member.Offline ? Avatar.Res("TextDim") : Member.StatusBrush;

        public Visibility NowVisibility => Member.Playing ? Visibility.Visible : Visibility.Collapsed;
        public string NowClassLine => Member.CardClassLine;
        public string NowLine => Member.PlaceLine;
    }
}
