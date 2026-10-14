using System.Text.RegularExpressions;

namespace Lodge;

// Badges and the ladder (feature "badges"): fun milestones for what a member shares with the lodge. Nothing here collects anything new from the game -
// every number is derived from data the lodge already holds (the Atlas pool, flights, the Armory) or from a small per-member tally counted from the day
// the feature went live (BadgesStore: files, events, chat days...). This file holds the pure rules: the ONE definitions table, the point formula and the
// season keys. A new badge is one more row in Table (and, for a new kind of number, one more row in Metrics and in BadgesModule.Gather): the clients
// render whatever the server sends, so no client update is needed.
public static partial class Badges
{
    public const int Bronze = 1, Silver = 2, Gold = 3, Legendary = 4;
    public static readonly string[] TierNames = { "", "bronze", "silver", "gold", "legendary" };
    public static readonly int[] TierPoints = { 0, 10, 25, 50, 100 };
    public const int CategoryCap = 120;           // no single category is worth more than this, however much is shared
    public const int TopBadges = 5;               // badges shown next to a name (highest tier first)
    public const int MaxSeasonsKept = 12;
    public const int MaxHistoryRows = 100;
    public static readonly TimeSpan RunEvery = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan RunOnRequestAfter = TimeSpan.FromSeconds(30);

    // A number a badge is earned from. Unit/Weight are the ladder points: Weight * log2(1 + value / Unit), at most CategoryCap (Weight 0 = no ladder points).
    public sealed record Metric(string Key, string Label, double Unit, double Weight);

    public static readonly Metric[] Metrics =
    {
        new("kills", "kills recorded", 50, 12),
        new("objects", "nodes and chests opened", 40, 10),
        new("fishing", "catches recorded", 25, 10),
        new("vendors", "vendors recorded", 5, 8),
        new("revealed", "item details revealed", 10, 8),
        new("finds", "finds recorded", 3, 6),
        new("zones", "zones covered", 3, 10),
        new("quests", "quest rewards recorded", 20, 8),
        new("seen", "creatures, objects and quests seen", 100, 8),
        new("flights", "flight legs shared", 10, 10),
        new("files", "files shared", 1, 10),
        new("events", "events created", 1, 10),
        new("signups", "event sign-ups", 3, 8),
        new("lfg", "group posts", 2, 8),
        new("chatdays", "days active in chat", 3, 10),
        new("chars", "characters shared", 1, 8),
        new("host", "Classic data hosted", 1, 0),
        new("nightdays", "late nights in chat", 1, 0),
        new("rare", "rare drops recorded first", 1, 0),
        new("days", "days in the lodge", 1, 0),
    };

    static readonly Dictionary<string, Metric> byKey = Metrics.ToDictionary(m => m.Key);
    public static Metric MetricOf(string key) => byKey.TryGetValue(key, out var m) ? m : null;

    // One badge. Metric = the number it is earned from, At = the threshold. Special = earned by a rule instead of a number (the hidden "wellrounded").
    public sealed record Def(string Id, string Family, int Tier, string Name, string Flavour, string Metric, long At, bool Hidden = false, string Special = null)
    {
        public string How => Special != null ? "Earn a badge in " + At + " different families" : Count(At) + " " + MetricOf(Metric).Label;
    }

    static string Count(long n) => n.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    public const int WellRoundedFamilies = 8;

    public static readonly Dictionary<string, string> FamilyNames = new()
    {
        ["loot"] = "Loot Ledger", ["cartographer"] = "Cartographer", ["forager"] = "Forager", ["fisher"] = "Fisher", ["trader"] = "Trader's Eye",
        ["flight"] = "Flight Clerk", ["archivist"] = "Archivist", ["quartermaster"] = "Quartermaster", ["herald"] = "Herald", ["partystarter"] = "Party Starter",
        ["rollcall"] = "Roll Call", ["hearthside"] = "Hearthside", ["veteran"] = "Veteran", ["armorer"] = "Armorer", ["host"] = "Host",
        ["fieldnotes"] = "Field Notes", ["secret"] = "Secrets",
    };

    static Def[] Family(string family, string metric, params (int tier, long at, string name, string flavour)[] rows) =>
        rows.Select(r => new Def(family + "." + TierNames[r.tier], family, r.tier, r.name, r.flavour, metric, r.at)).ToArray();

    // The table. Tiers ascend within a family; ids are "<family>.<tier>" (hidden ones are plain words). Never reuse or rename an id: earned badges are kept by id.
    public static readonly Def[] Table = new[]
    {
        Family("loot", "kills",
            (Bronze, 100, "Pocket Change", "A hundred corpses, a hundred small surprises."),
            (Silver, 1_000, "Fat Purse", "The ledger needs a second page."),
            (Gold, 10_000, "Dragon's Hoard", "Somewhere a vendor is nervous."),
            (Legendary, 50_000, "Ledger of Ledgers", "Fifty thousand kills, every drop written down.")),
        Family("cartographer", "zones",
            (Bronze, 5, "Wayfinder", "You have left the starting area. Mostly."),
            (Silver, 15, "Pathfinder", "Fifteen zones, and not one of them lost."),
            (Gold, 30, "Cartographer", "The map is a little more yours than anyone's.")),
        Family("forager", "objects",
            (Bronze, 100, "Green Thumb", "Herbs, ore and the odd chest."),
            (Silver, 2_000, "Vein Whisperer", "The nodes know you by name.")),
        Family("fisher", "fishing",
            (Bronze, 25, "Bait and Wait", "The first catches of many."),
            (Silver, 250, "Patient Angler", "Patience, and a very good hat."),
            (Gold, 2_500, "Master Angler", "The lake owes you money.")),
        Family("trader", "vendors",
            (Bronze, 10, "Window Shopper", "You looked at the wares."),
            (Silver, 100, "Trader's Eye", "You know who sells what, and where.")),
        Family("flight", "flights",
            (Bronze, 5, "First Flight", "Timetable pages, one by one."),
            (Silver, 25, "Flight Clerk", "Stamps on a lot of tickets."),
            (Gold, 100, "Master of the Skies", "The lodge arrives on time because of you.")),
        Family("archivist", "revealed",
            (Bronze, 10, "Curious", "You looked closer."),
            (Silver, 100, "Archivist", "A shelf of item details, all shared."),
            (Gold, 1_000, "Keeper of Tomes", "The library has a wing named after you. Unofficially.")),
        Family("quartermaster", "files",
            (Bronze, 1, "Courier", "A file for everyone."),
            (Silver, 5, "Quartermaster", "The shelves are well stocked."),
            (Gold, 20, "Master of Supplies", "Whatever the lodge needs, you already shared it.")),
        Family("herald", "events",
            (Bronze, 1, "Town Crier", "You put the first event on the calendar."),
            (Silver, 5, "Herald", "People plan their week around you."),
            (Gold, 25, "Master of Ceremonies", "A calendar with your handwriting all over it.")),
        Family("partystarter", "lfg",
            (Bronze, 1, "Banner Raiser", "You asked who wants to come."),
            (Silver, 10, "Party Starter", "There is always a group when you are around.")),
        Family("rollcall", "signups",
            (Bronze, 5, "Reliable", "You said you would come."),
            (Silver, 50, "Roll Call Regular", "Your name is on every list.")),
        Family("hearthside", "chatdays",
            (Bronze, 3, "Pull Up a Chair", "A few evenings by the fire."),
            (Silver, 20, "Regular", "The chair has your shape."),
            (Gold, 60, "Heart of the Hearth", "Sixty days of company.")),
        Family("veteran", "days",
            (Bronze, 30, "Settled In", "A month in the lodge."),
            (Silver, 180, "Old Hand", "Half a year, and still here."),
            (Gold, 365, "Veteran", "A full year."),
            (Legendary, 730, "Founding Stone", "Two years. The walls are built around you.")),
        Family("armorer", "chars",
            (Bronze, 1, "Show and Tell", "One character on the shelf."),
            (Silver, 5, "Armorer", "A whole rack of characters to look at.")),
        Family("host", "host",
            (Gold, 1, "Keeper of the Hall", "You host the Classic data for the whole lodge.")),
        Family("fieldnotes", "seen",
            (Bronze, 25, "Notebook", "Field notes, started."),
            (Silver, 250, "Field Naturalist", "Creatures, objects and quests, all written down."),
            (Gold, 2_500, "Bestiarist", "You have seen more of the world than the world has seen of you.")),
        new[]
        {
            new Def("lucky", "secret", Silver, "Lucky Strike", "You were the first in the lodge to see a drop rarer than one in a hundred.", "rare", 1, Hidden: true),
            new Def("nightowl", "secret", Bronze, "Night Owl", "Five nights, wide awake, still talking.", "nightdays", 5, Hidden: true),
            new Def("wellrounded", "secret", Gold, "Well Rounded", "A bit of everything.", "families", WellRoundedFamilies, Hidden: true, Special: "families"),
        },
    }.SelectMany(x => x).ToArray();

    static readonly Dictionary<string, Def> byId = Table.ToDictionary(d => d.Id);
    public static Def Find(string id) => id != null && byId.TryGetValue(id, out var d) ? d : null;

    // ---- points
    public static double CategoryPoints(string key, long value)
    {
        var m = MetricOf(key);
        if (m == null || m.Weight <= 0 || value <= 0) return 0;
        return Math.Min(CategoryCap, m.Weight * Math.Log2(1 + value / m.Unit));
    }

    public static int BadgePoints(int tier) => tier >= 1 && tier < TierPoints.Length ? TierPoints[tier] : 0;

    // ---- seasons: a calendar month in UTC, "2026-10"
    public static string SeasonKey(DateTime utc) => utc.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^\d{4}-(0[1-9]|1[0-2])\z", RegexOptions.CultureInvariant)]
    private static partial Regex SeasonRx();

    public static bool ValidSeason(string s) => s != null && (s == "all" || SeasonRx().IsMatch(s));

    public static DateTime SeasonStart(string key) => DateTime.ParseExact(key + "-01", "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);

    // highest tier first, then the newest, then the id; hidden ones count like any other
    public static IEnumerable<string> TopIds(IReadOnlyDictionary<string, long> earned, int n) =>
        earned.Select(kv => (Def: Find(kv.Key), At: kv.Value)).Where(x => x.Def != null)
              .OrderByDescending(x => x.Def.Tier).ThenByDescending(x => x.At).ThenBy(x => x.Def.Id, StringComparer.Ordinal)
              .Take(n).Select(x => x.Def.Id);
}
