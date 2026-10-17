using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Maps;

namespace ElansAddonHub.Services.Atlas
{
    // ============================================================================================================================
    // Atlas layer F (the Atlas page): the models the page shows. Pure data, no controls: AtlasQuery builds them from all the layers
    // (client game data, what the client has seen, the Lodge pool, the Classic baseline, the maps) and the page only draws them.
    // Every row names where it comes from (Label): "Forever" (this client's own data, or observed by the Lodge) or "Classic baseline".
    // ============================================================================================================================

    public static class AtlasLabel
    {
        public const string Forever = "Forever";
        public const string Classic = "Classic baseline";
        public const string Mine = "Your characters";
        public const string Hotfix = "Server hotfix";        // rows the server sent the client (DBCache.bin) over the shipped game files
        public const string Cairn = "Cairn";                 // the open dataset merged from many Lodges (CairnData.cs); below the user's own Lodge
    }

    // the kinds of thing an id link can name, in the order search groups them
    public static class AtlasKinds
    {
        public static readonly string[] Order = { "item", "npc", "obj", "quest", "zone", "instance", "spell", "skill", "faction", "class", "race" };
        public static string Plural(string kind)
        {
            switch (kind)
            {
                case "item": return "Items";
                case "npc": return "NPCs";
                case "obj": return "Objects";
                case "quest": return "Quests";
                case "zone": return "Zones";
                case "instance": return "Instances";
                case "spell": return "Spells";
                case "skill": return "Skills";
                case "faction": return "Factions";
                case "class": return "Classes";
                case "race": return "Races";
                default: return kind;
            }
        }
        public static string Singular(string kind)
        {
            switch (kind)
            {
                case "item": return "Item";
                case "npc": return "NPC";
                case "obj": return "Object";
                case "quest": return "Quest";
                case "zone": return "Zone";
                case "instance": return "Instance";
                case "spell": return "Spell";
                case "skill": return "Skill";
                case "faction": return "Faction";
                case "class": return "Class";
                case "race": return "Race";
                default: return kind;
            }
        }
        public static string Glyph(string kind)
        {
            switch (kind)
            {
                case "item": return "";      // shopping bag
                case "npc": return "";       // person
                case "obj": return "";       // box
                case "quest": return "";     // page
                case "zone": return "";      // map pin
                case "instance": return "";  // home / gate
                case "spell": return "";     // lightning
                case "skill": return "";     // book
                case "faction": return "";   // people
                case "class": return "";    // star
                case "race": return "";     // globe
                default: return "";
            }
        }
    }

    // one row of a table on an Atlas page. Bindable so the icon can arrive after the row is drawn (it is only asked for when the row is shown).
    public sealed class AtlasRow : Bindable
    {
        // properties, not fields: the row template binds to them
        public string Link { get; set; }                  // the Atlas id a click opens ("npc:123"), null = not clickable
        public string Title { get; set; } = "";
        public string Detail { get; set; } = "";
        public string Extra { get; set; } = "";           // a second line (the Classic baseline's own figure for a source the Lodge also saw, a zone ...)
        public string Label { get; set; } = "";           // where the row comes from: AtlasLabel.Forever / .Classic / ""
        public string Type { get; set; } = "";            // "Drop", "Vendor", "Quest", "Recipe" ... (the table's kind word)
        public int Quality { get; set; } = -1;            // item quality: the title takes its colour
        public int IconId { get; set; }                   // item / spell icon (FileDataID), 0 = none
        public string Right { get; set; } = "";           // a figure aligned to the right (a price, a count)
        public int Tier { get; set; }                     // 0 Lodge-observed, 1 client data, 2 Classic baseline: rows sort by tier first
        public double Sort { get; set; }                  // within a tier, higher first
        public string Zone { get; set; } = "";            // the zone name when known
        public string Key { get; set; } = "";             // identity for merging rows of the same source ("npc:123")
        public double Progress { get; set; } = -1;        // 0..1: a thin bar under the text (a reputation inside its standing); -1 = none
        public bool HasProgress => Progress >= 0;
        public bool Forever => Label == AtlasLabel.Forever;

        ImageSource icon;
        bool asked;
        public ImageSource Icon
        {
            get
            {
                if (icon == null && !asked && IconId > 0)
                {
                    asked = true;
                    var have = IconStore.TryGet(IconId);
                    if (have != null) icon = have;
                    else
                    {
                        var disp = Application.Current?.Dispatcher;
                        IconStore.Request(IconId, img => disp?.BeginInvoke(new Action(() => { icon = img; Raise(nameof(Icon)); Raise(nameof(HasIcon)); })));
                    }
                }
                return icon;
            }
        }
        public bool HasIcon => IconId > 0;
        public string Initial => string.IsNullOrEmpty(Title) ? "?" : Title.Substring(0, 1).ToUpperInvariant();
        public Brush TitleBrush => Quality >= 0 && Quality < ItemLinks.QualityHex.Length ? Avatar.Frozen(ItemLinks.QualityHex[Quality]) : Avatar.Res("Brush.Text.Primary");
        public Brush TileBrush => Quality >= 0 && Quality < ItemLinks.QualityHex.Length ? Avatar.Frozen(ItemLinks.QualityHex[Quality]) : Avatar.Res("Brush.Text.Tertiary");
        public bool Clickable => !string.IsNullOrEmpty(Link);
        // the tile of a row with no picture and no quality colour shows the glyph of what the row is
        public bool ShowGlyph => IconId <= 0 && Quality < 0;
        public string Glyph
        {
            get
            {
                if (!string.IsNullOrEmpty(Link) && AtlasIds.TryParse(Link, out var k, out _)) return AtlasKinds.Glyph(k);
                if (!string.IsNullOrEmpty(Link) && AtlasCategories.TryHosted(Link, out var hc, out _)) return hc.Glyph;
                return Type == "Guild" ? AtlasKinds.Glyph("faction") : Type == "Character" ? AtlasKinds.Glyph("npc") : "";
            }
        }
        public string LabelText => Label;
        public override string ToString() => (Type.Length > 0 ? Type + ": " : "") + Title + (string.IsNullOrEmpty(Detail) ? "" : " | " + Detail) + (string.IsNullOrEmpty(Extra) ? "" : " | " + Extra) + (Label.Length > 0 ? " [" + Label + "]" : "");
    }

    // a table: visible rows, and the ones folded behind one line ("+ 14 world-drop sources under 0.1%")
    public sealed class AtlasSection
    {
        public string Key, Title, Note = "", Empty = "";
        public List<AtlasRow> Rows = new List<AtlasRow>();
        public List<AtlasRow> Hidden = new List<AtlasRow>();   // folded rows
        public string HiddenText = "";                          // the line that stands for them
        public bool Open => Rows.Count > 0 || Hidden.Count > 0;
        public override string ToString() => Title + " (" + Rows.Count + (Hidden.Count > 0 ? " + " + Hidden.Count + " folded" : "") + ")";
    }

    public sealed class AtlasFact
    {
        public string Label, Value, Link, Source = "";
        public AtlasFact(string label, string value, string source = "", string link = null) { Label = label; Value = value; Source = source ?? ""; Link = link; }
        public override string ToString() => Label + ": " + Value + (Source.Length > 0 ? " [" + Source + "]" : "");
    }

    // one map the page can show (a zone's picture) with the markers that belong on it
    public sealed class AtlasMapZone
    {
        public int UiMapId;
        public string Name;
        public List<AtlasMarker> Markers = new List<AtlasMarker>();
        public int Forever => Markers.Count(m => (m.Tooltip ?? "").Contains(AtlasLabel.Forever));
        public override string ToString() => Name + " (" + Markers.Count + ")";
    }

    public sealed class AtlasMapPlan
    {
        public List<AtlasMapZone> Zones = new List<AtlasMapZone>();
        public int InstanceId;                  // a page about an instance: its map (usually "no map in the game files")
        public string InstanceName = "";
        public int ShowZone;                    // a zone page: the map to show even without markers
        public int Unplaced;                    // spawn points no map shows (inside an instance with no picture, outside every zone)
        public List<KeyValuePair<int, int>> InInstances = new List<KeyValuePair<int, int>>();   // instance id -> spawn points inside it
        public string Note = "";
        public bool Any => Zones.Count > 0 || InstanceId > 0 || ShowZone > 0;
        public int MarkerCount => Zones.Sum(z => z.Markers.Count);
        public AtlasMapZone Best => Zones.OrderByDescending(z => z.Forever).ThenByDescending(z => z.Markers.Count).FirstOrDefault();
    }

    public sealed class AtlasText
    {
        public string Title, Body, Source = "";
        public AtlasText(string title, string body, string source = "") { Title = title; Body = body; Source = source ?? ""; }
    }

    public sealed class AtlasCrumb
    {
        public string Title, Link;
        public AtlasCrumb(string title, string link) { Title = title; Link = link; }
    }

    // everything the page needs about one id
    public sealed class AtlasPageModel
    {
        public string Id = "", Kind = "";
        public string Title = "", Subtitle = "";
        public int Quality = -1, IconId;
        public bool NotFound;
        public string Message = "";                                  // NotFound: what to say
        public List<TipLine> Tooltip = new List<TipLine>();          // item: the game's own tooltip
        public string TooltipSource = "";
        public List<AtlasFact> Facts = new List<AtlasFact>();
        public List<AtlasText> Texts = new List<AtlasText>();
        public List<AtlasSection> Sections = new List<AtlasSection>();
        public AtlasMapPlan Map = new AtlasMapPlan();
        public object Custom;                                        // a page with a control of its own (a class page: its spells by level)
        public string Accent = "";                                   // a colour the title takes (#RRGGBB): a class
        public bool MapFirst;                                        // zones, instances, npcs: the map comes before the tables
        public AtlasEntrance Entrance;                               // an instance page: where its way in is (null = the game files do not say)
        public ZoneLoreEntry Lore;                                   // a zone page: the Outpost's notes on the zone (ZoneLore; null = none)
        public ClassLorePage ClassLore;                              // a class or race page: the game's own description and the Outpost's notes (ClassLore; null = neither)
        public List<string> Notes = new List<string>();              // what is missing and what turns it on ("Classic baseline not downloaded ...")
        public List<AtlasCrumb> Crumbs = new List<AtlasCrumb>();     // the way up (a zone's continent)
        public List<string> Layers = new List<string>();             // the layers that contributed: "client", "seen", "lodge", "classic", "maps"
        public AtlasSection Section(string key) => Sections.FirstOrDefault(s => s.Key == key);
        public IEnumerable<AtlasRow> AllRows => Sections.SelectMany(s => s.Rows.Concat(s.Hidden));
    }

    // search results
    public sealed class AtlasHit
    {
        public string Id = "", Kind = "", Name = "", Detail = "", Label = "";
        public int Quality = -1, IconId;
        public int Rank;                // 0 exact, 1 prefix, 2 word start, 3 anywhere, 4 by id
        public int Tier;                // 0 Forever, 1 Classic baseline only
        public override string ToString() => Kind + ": " + Name + (Detail.Length > 0 ? " (" + Detail + ")" : "") + " [" + Label + "] " + Id;
    }

    public sealed class AtlasGroup
    {
        public string Kind, Title;
        public List<AtlasHit> Hits = new List<AtlasHit>();
        public int Total;               // how many matched (Hits holds the best few)
    }

    public sealed class AtlasSearchResult
    {
        public string Query = "";
        public bool ById;
        public List<AtlasGroup> Groups = new List<AtlasGroup>();
        public int Total => Groups.Sum(g => g.Hits.Count);
        public IEnumerable<AtlasHit> All => Groups.SelectMany(g => g.Hits);
    }

    // back / forward through the pages visited
    public sealed class AtlasHistory
    {
        readonly List<string> items = new List<string>();
        int pos = -1;
        public const int Max = 80;
        public string Current => pos >= 0 && pos < items.Count ? items[pos] : null;
        public bool CanBack => pos > 0;
        public bool CanForward => pos >= 0 && pos < items.Count - 1;
        public int Count => items.Count;
        public IReadOnlyList<string> Items => items;
        public int Position => pos;

        // visit an id (null/"" = the start page). Visiting what is already shown changes nothing. A new visit drops the forward part.
        public bool Go(string id)
        {
            id = id ?? "";
            if (Current == id) return false;
            if (pos < items.Count - 1) items.RemoveRange(pos + 1, items.Count - pos - 1);
            items.Add(id);
            if (items.Count > Max) items.RemoveAt(0);
            pos = items.Count - 1;
            return true;
        }
        // the page now shown becomes another id without a new step in the history (filters changing on a browse page)
        public void Replace(string id) { if (pos >= 0 && pos < items.Count) items[pos] = id ?? ""; }
        public string Back() { if (!CanBack) return null; pos--; return items[pos]; }
        public string Forward() { if (!CanForward) return null; pos++; return items[pos]; }
        public void Clear() { items.Clear(); pos = -1; }
    }

    public static class AtlasFormat
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // 12 -> "12%", 0.42 -> "0.42%", 33.333 -> "33%", 0.004 -> "under 0.01%"
        public static string Percent(double p)
        {
            if (p <= 0) return "0%";
            if (p < 0.01) return "under 0.01%";
            if (p < 1) return p.ToString("0.##", Inv) + "%";
            if (p < 10) return p.ToString("0.#", Inv) + "%";
            return Math.Round(p).ToString("0", Inv) + "%";
        }

        public static string Money(long copper) => MoneyFormat.Text(copper);

        public static string Count(long n, string one, string many) => n.ToString("N0", CalendarRules.English) + " " + (n == 1 ? one : many);

        public static string Ago(long unixSeconds, DateTime nowUtc)
        {
            if (unixSeconds <= 0) return "";
            var then = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(unixSeconds);
            var d = nowUtc - then;
            if (d.TotalSeconds < 90) return "just now";
            if (d.TotalMinutes < 90) return Math.Round(d.TotalMinutes) + " min ago";
            if (d.TotalHours < 36) return Math.Round(d.TotalHours) + " h ago";
            if (d.TotalDays < 45) return Math.Round(d.TotalDays) + " d ago";
            return then.ToString("d MMM yyyy", CalendarRules.English);
        }

        public static string Levels(int min, int max) => min <= 0 && max <= 0 ? "" : (min == max || max <= 0 ? min.ToString(Inv) : min + "-" + max);

        public static readonly string[] Ranks = { "Normal", "Elite", "Rare Elite", "Boss", "Rare" };
        public static string RankName(int rank) => rank > 0 && rank < Ranks.Length ? Ranks[rank] : "";

        public static string CreatureType(int t)
        {
            switch (t)
            {
                case 1: return "Beast"; case 2: return "Dragonkin"; case 3: return "Demon"; case 4: return "Elemental"; case 5: return "Giant";
                case 6: return "Undead"; case 7: return "Humanoid"; case 8: return "Critter"; case 9: return "Mechanical"; case 10: return "Not specified"; case 11: return "Totem";
                default: return "";
            }
        }
    }
}
