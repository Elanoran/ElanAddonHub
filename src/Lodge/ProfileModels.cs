using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
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

    // Display names (Lodge server 2.7): the login name stays the identity everywhere (messages, history, pins, reactions); the UI shows the
    // display name instead when the person set one. Resolved live from what we know, and remembered for people who have left, so a rename
    // also changes the old messages. Mirrors the server's format rules (the server has the final say, incl. uniqueness).
    public static class DisplayNames
    {
        public const int Min = 2, Max = 24;
        static readonly Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static string Shown(string login)
        {
            if (string.IsNullOrEmpty(login)) return login;
            return map.TryGetValue(login, out var d) && d.Length > 0 ? d : login;
        }

        // remember (or forget, with "") a person's display name; true when it changed
        public static bool Remember(string login, string display)
        {
            if (string.IsNullOrEmpty(login)) return false;
            display = display ?? "";
            map.TryGetValue(login, out var old);
            if ((old ?? "") == display) return false;
            if (display.Length == 0) map.Remove(login); else map[login] = display;
            return true;
        }

        public static void Forget() => map.Clear();

        // the same cleaning the server does: NFKC, control and format characters out, whitespace runs become one space
        public static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            try { s = s.Normalize(NormalizationForm.FormKC); } catch (ArgumentException) { }
            var sb = new StringBuilder();
            bool space = false;
            foreach (var c in s)
            {
                var cat = char.GetUnicodeCategory(c);
                if (char.IsWhiteSpace(c) || cat == UnicodeCategory.SpaceSeparator) { space = sb.Length > 0; continue; }
                if (cat == UnicodeCategory.Control || cat == UnicodeCategory.Format || cat == UnicodeCategory.Surrogate
                    || cat == UnicodeCategory.PrivateUse || cat == UnicodeCategory.OtherNotAssigned) continue;
                if (space) { sb.Append(' '); space = false; }
                sb.Append(c);
            }
            return sb.ToString();
        }

        // null = fine (or empty = none); else what is wrong, for the hint under the box
        public static string Problem(string raw)
        {
            var n = Clean(raw);
            if (n.Length == 0) return null;
            if (n.Length < Min) return "At least 2 characters.";
            if (n.Length > Max) return "At most 24 characters.";
            foreach (var c in n)
                if (!(char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '_' || c == '\'' || c == '.' || c == '!'))
                    return "Only letters, digits, spaces and - _ ' . ! are allowed.";
            if (!n.Any(char.IsLetterOrDigit)) return "Needs at least one letter or digit.";
            return null;
        }

        // "@Elan" and "@Hunter Elan" both mention the person (case-insensitive; a display name may contain spaces)
        public static string MentionPattern(string login, string display)
        {
            var alts = new List<string> { Regex.Escape(login ?? "") };
            if (!string.IsNullOrEmpty(display) && !string.Equals(display, login, StringComparison.OrdinalIgnoreCase))
                alts.Add(Regex.Escape(Clean(display)).Replace("\\ ", "\\s+"));
            return string.Join("|", alts);
        }
    }

    // What the server answered for a profile (profile:data) - own or someone else's.
    public class ProfileData
    {
        public string Name, DisplayName = "", Role, AvatarId, Accent, About = "", PlayTimes = "", Main, ShowChars = "all", VisibleTo = "everyone";
        public bool Found, Guest, Online, Restricted;
        public List<CharVM> Chars = new List<CharVM>();
        public Dictionary<string, object> Game;

        public static ProfileData Parse(Dictionary<string, object> p)
        {
            if (p == null) return null;
            var d = new ProfileData
            {
                Name = p.Str("name"), Found = p.Bool("found"), Guest = p.Bool("guest"), Role = p.Str("role") ?? "member", Online = p.Bool("online"),
                Restricted = p.Bool("restricted"), DisplayName = p.Str("displayName") ?? "", AvatarId = p.Str("avatarId"), Accent = p.Str("accent"), About = p.Str("about") ?? "",
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
                nameof(NoteVisibility), nameof(Banner), nameof(NowVisibility), nameof(NowLine), nameof(NowClassLine), nameof(OnlineText), nameof(OnlineBrush), nameof(Name), nameof(LoginText), nameof(LoginVisibility), nameof(RoleLabel), nameof(FrameBrush), nameof(NameBrush) }) Raise(n);
        }

        public string Name => Member.Display;
        public string LoginText => Member.LoginText;
        public Visibility LoginVisibility => Member.HasDisplayName ? Visibility.Visible : Visibility.Collapsed;
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
