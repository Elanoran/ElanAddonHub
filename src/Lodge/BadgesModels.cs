using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using ElansAddonHub.Services;

namespace ElansAddonHub.Lodge
{
    // Badges and the ladder (Lodge server feature "badges"). The server owns the definitions: what a badge is called, its flavour text and how to earn it all come over
    // the wire, so a badge added on the server shows here without an Outpost update. This file only knows how to DRAW one: an icon glyph per family on a tier-coloured disc.
    public static class BadgeArt
    {
        // family -> Segoe Fluent Icons glyph; an unknown family (a new one on the server) gets the star
        static readonly Dictionary<string, string> glyphs = new Dictionary<string, string>
        {
            ["loot"] = "\uE719",          // shopping bag
            ["cartographer"] = "\uE707",  // map pin
            ["forager"] = "\uEB42",       // droplet (a green thumb)
            ["fisher"] = "\uE7E3",        // boat
            ["trader"] = "\uEB25",        // glasses (the trader's eye)
            ["flight"] = "\uE709",        // airplane
            ["archivist"] = "\uE8A5",     // document
            ["quartermaster"] = "\uE7B8", // box
            ["herald"] = "\uE789",        // megaphone
            ["partystarter"] = "\uE716",  // people
            ["rollcall"] = "\uE73E",      // check
            ["hearthside"] = "\uE8BD",    // chat
            ["veteran"] = "\uE823",       // clock
            ["armorer"] = "\uEA18",       // shield
            ["host"] = "\uE80F",          // home
            ["fieldnotes"] = "\uE70F",    // pencil
            ["secret"] = "\uE897",        // help
        };

        public const string Star = "\uE734";
        public static IEnumerable<string> KnownFamilies => glyphs.Keys;

        public static string Glyph(string family) => family != null && glyphs.TryGetValue(family, out var g) ? g : Star;

        public static readonly string[] TierNames = { "", "Bronze", "Silver", "Gold", "Legendary" };
        public static string TierName(int tier) => tier >= 1 && tier < TierNames.Length ? TierNames[tier] : "";

        public static Brush TierBrush(int tier) => Avatar.Res("Brush.Badge." + (TierName(tier).Length > 0 ? TierName(tier) : "Bronze"));
        public static Brush TierTint(int tier) => Avatar.Res("Brush.Badge." + (TierName(tier).Length > 0 ? TierName(tier) : "Bronze") + ".Tint");
    }

    // One badge, as the server describes it (badges.mine: all of them with progress; badges.member and the ladder rows: earned ones only).
    public class BadgeVM : Bindable
    {
        public string Id { get; set; }
        public string Family { get; set; }
        public string FamilyName { get; set; }
        public int Tier { get; set; }
        public string Name { get; set; }
        public string Flavour { get; set; } = "";
        public string How { get; set; } = "";
        public bool Hidden { get; set; }
        public bool Earned { get; set; }
        public long At { get; set; }                 // unix seconds
        public long Value { get; set; }
        public long Target { get; set; }

        public string Glyph => Hidden && !Earned ? BadgeArt.Glyph("secret") : BadgeArt.Glyph(Family);
        public Brush DiscBrush => Earned ? BadgeArt.TierBrush(Tier) : Avatar.Res("Brush.Surface3");
        public Brush GlyphBrush => Earned ? Avatar.Res("Brush.OnAccent") : Avatar.Res("Brush.Text.Tertiary");
        public double RowOpacity => Earned ? 1 : 0.7;
        public Brush TierTint => Earned ? BadgeArt.TierTint(Tier) : Avatar.Res("Brush.Overlay.Chip");
        public string TierLabel => BadgeArt.TierName(Tier);
        public DateTime? EarnedAt => At > 0 ? DateTimeOffset.FromUnixTimeSeconds(At).LocalDateTime : (DateTime?)null;
        public string EarnedLine => Earned ? "Earned " + (EarnedAt?.ToString("d MMM yyyy", CultureInfo.CurrentCulture) ?? "") : Hidden ? "A secret" : "Not earned yet";
        public string Tip => Name + " (" + TierLabel.ToLowerInvariant() + ")" + (Flavour.Length > 0 ? " - " + Flavour : "");

        // progress toward a number ("3,412 / 10,000 kills recorded"); none for earned badges, hidden ones and badges without a number
        public bool HasProgress => !Earned && !(Hidden) && Target > 0;
        public Visibility ProgressVisibility => HasProgress ? Visibility.Visible : Visibility.Collapsed;
        public double Progress => Target > 0 ? Math.Min(1.0, (double)Value / Target) : 0;
        public string ProgressText => Target > 0 ? Value.ToString("N0", CultureInfo.CurrentCulture) + " / " + How : "";
        public Visibility FlavourVisibility => Flavour.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        public static BadgeVM Parse(Dictionary<string, object> b) => new BadgeVM
        {
            Id = b.Str("id"), Family = b.Str("family"), FamilyName = b.Str("familyName") ?? b.Str("family"), Tier = b.Int("tier"), Name = b.Str("name") ?? "???",
            Flavour = b.Str("flavour") ?? "", How = b.Str("how") ?? "", Hidden = b.Bool("hidden"),
            Earned = b.ContainsKey("earned") ? b.Bool("earned") : true,      // a list of someone else's badges holds earned ones only
            At = b.Long("at"), Value = b.Long("value"), Target = b.Long("target"),
        };

    }

    public class BadgeGroupVM
    {
        public string Name { get; set; }
        public ObservableCollection<BadgeVM> Items { get; } = new ObservableCollection<BadgeVM>();
    }

    // one row of the ladder
    public class LadderRowVM : Bindable
    {
        public int Rank { get; set; }
        public string Login { get; set; }
        public string Display => DisplayNames.Shown(Login);
        public long Points { get; set; }
        public bool IsYou { get; set; }
        public bool Hidden { get; set; }              // only on your own row: others do not see you
        public List<BadgeVM> Top { get; } = new List<BadgeVM>();
        public string RankText => Rank.ToString(CultureInfo.CurrentCulture);
        public string PointsText => Points.ToString("N0", CultureInfo.CurrentCulture);
        public string NameText => Display + (IsYou ? " (you)" : "");
        public string HiddenNote => IsYou && Hidden ? "Only you see this row" : "";
        public Visibility HiddenNoteVisibility => HiddenNote.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Brush RowBrush => IsYou ? Avatar.Res("Brush.Accent.Tint") : Brushes.Transparent;

        public static LadderRowVM Parse(Dictionary<string, object> r)
        {
            var row = new LadderRowVM { Rank = r.Int("rank"), Login = r.Str("name") ?? "", Points = r.Long("points"), IsYou = r.Bool("you"), Hidden = r.Bool("hidden") };
            foreach (var t in (r.List("top") ?? new List<object>()).OfType<Dictionary<string, object>>().Take(5)) row.Top.Add(BadgeVM.Parse(t));
            return row;
        }
    }
}
