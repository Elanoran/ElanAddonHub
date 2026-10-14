using System.Text.Json.Nodes;

namespace Lodge;

// What members' game clients have SEEN (2.15, feature "atlas2"): npc, object and quest details from the client caches. The wire sections are
// seenNpcs { id: {name, title?, type?, family?, rank?} }, seenObjects { id: {name, type?} } and seenQuests { id: {title, level?, minLevel?, objectives?} }
// (the names objects / quests are taken by the loot counters and quest rewards). The first good description of an id stays; a later one only fills
// fields that are still empty. Strings are checked, never trusted: length limits, no control characters.
public static partial class Atlas
{
    public const int MaxSeenNpcs = 40000, MaxSeenObjects = 40000, MaxSeenQuests = 20000;
    public const int MaxSeenNpcsPerShare = 300, MaxSeenObjectsPerShare = 300, MaxSeenQuestsPerShare = 100;
    public const int MaxTitleLength = 80, MaxObjectivesLength = 1000;

    // a displayed string: at most `max` chars, no control characters (newline and tab only where `multiline`), trimmed. Null = refused.
    public static bool SeenText(JsonNode n, int max, bool multiline, bool required, out string s)
    {
        s = "";
        if (n == null) return !required;
        if (n is not JsonValue v || !v.TryGetValue<string>(out var raw)) return false;
        if (raw.Length > max) return false;
        foreach (var c in raw)
            if (char.IsControl(c) && !(multiline && (c == '\n' || c == '\t' || c == '\r'))) return false;
        s = multiline ? raw.Replace("\r\n", "\n").Replace('\r', '\n').Trim() : raw.Trim();
        return !required || s.Length > 0;
    }

    public sealed class SeenNpc
    {
        public long Rev; public string By = ""; public string Name = "", Title = ""; public int Type, Family, Rank;
        // later descriptions only fill what is empty (0 = unknown / none); true when something was filled
        public bool Fill(SeenNpc o)
        {
            bool ch = false;
            if (Title.Length == 0 && o.Title.Length > 0) { Title = o.Title; ch = true; }
            if (Type == 0 && o.Type != 0) { Type = o.Type; ch = true; }
            if (Family == 0 && o.Family != 0) { Family = o.Family; ch = true; }
            if (Rank == 0 && o.Rank != 0) { Rank = o.Rank; ch = true; }
            return ch;
        }
    }

    public sealed class SeenObject
    {
        public long Rev; public string By = ""; public string Name = ""; public int Type;
        public bool Fill(SeenObject o)
        {
            if (Type == 0 && o.Type != 0) { Type = o.Type; return true; }
            return false;
        }
    }

    public sealed class SeenQuest
    {
        public long Rev; public string By = ""; public string Title = "", Objectives = ""; public int Level, MinLevel;
        public bool Fill(SeenQuest o)
        {
            bool ch = false;
            if (Objectives.Length == 0 && o.Objectives.Length > 0) { Objectives = o.Objectives; ch = true; }
            if (Level == 0 && o.Level != 0) { Level = o.Level; ch = true; }
            if (MinLevel == 0 && o.MinLevel != 0) { MinLevel = o.MinLevel; ch = true; }
            return ch;
        }
    }

    public static bool ParseSeenNpc(JsonNode node, out SeenNpc r)
    {
        r = null;
        if (node is not JsonObject o || !SeenText(o["name"], MaxNameLength, false, true, out var name) || !SeenText(o["title"], MaxTitleLength, false, false, out var title)) return false;
        var x = new SeenNpc { Name = name, Title = title };
        bool ok;
        x.Type = Opt(o, "type", 0, 100, out ok); if (!ok) return false;
        x.Family = Opt(o, "family", 0, 1000, out ok); if (!ok) return false;
        x.Rank = Opt(o, "rank", 0, 20, out ok); if (!ok) return false;
        r = x; return true;
    }

    public static bool ParseSeenObject(JsonNode node, out SeenObject r)
    {
        r = null;
        if (node is not JsonObject o || !SeenText(o["name"], MaxNameLength, false, true, out var name)) return false;
        var x = new SeenObject { Name = name };
        x.Type = Opt(o, "type", 0, 1000, out var ok); if (!ok) return false;
        r = x; return true;
    }

    public static bool ParseSeenQuest(JsonNode node, out SeenQuest r)
    {
        r = null;
        if (node is not JsonObject o || !SeenText(o["title"], MaxNameLength * 2, false, true, out var title) || !SeenText(o["objectives"], MaxObjectivesLength, true, false, out var obj)) return false;
        var x = new SeenQuest { Title = title, Objectives = obj };
        bool ok;
        x.Level = Opt(o, "level", -1, 255, out ok); if (!ok) return false;
        x.MinLevel = Opt(o, "minLevel", 0, 255, out ok); if (!ok) return false;
        r = x; return true;
    }
}
