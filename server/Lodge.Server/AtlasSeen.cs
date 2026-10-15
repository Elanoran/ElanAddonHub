using System.Text.Json.Nodes;

namespace Lodge;

// What members' game clients have SEEN (2.15, feature "atlas2"): npc, object and quest details from the client caches. The wire sections are
// seenNpcs { id: {name, title?, type?, family?, rank?} }, seenObjects { id: {name, type?} } and seenQuests { id: {title, level?, minLevel?, objectives?} }
// (the names objects / quests are taken by the loot counters and quest rewards). Strings are checked, never trusted: length limits, no control characters.
//
// 2.25 (feature "atlas7"): the verified details of the client's own query answers ride along, all optional:
//   every kind   build        the game build the client cache was written by (0..9,999,999)
//   seenNpcs     tf           the creature type flags (0..4294967295: 0x1 tameable, 0x4 boss level), kc: [proxy creature, proxy creature]
//   seenObjects  lock         the Lock id a chest-type object opens with (herbs, ore veins, chests)
//   seenQuests   sort, type, info, flags, group, next, money, spell, start, races: [low 32 bits, high 32 bits] of the race mask,
//                rewards / choices / provided: [[item, count], ...], rep: [faction, ...], goals: [[type, object, amount, "text"], ...],
//                desc, area, done (description, area text, completion text)
// Merge per id: a record from a NEWER game build replaces the stored one (a patch changed the quest), one from the same build only fills fields that
// are still empty (the first good description stays), one from an older build is ignored. Resends never change anything (idempotent).
public static partial class Atlas
{
    public const int MaxSeenNpcs = 40000, MaxSeenObjects = 40000, MaxSeenQuests = 20000;
    public const int MaxSeenNpcsPerShare = 300, MaxSeenObjectsPerShare = 300, MaxSeenQuestsPerShare = 100;
    public const int MaxTitleLength = 80, MaxObjectivesLength = 1000;
    public const int MaxBuild = 9_999_999, MaxQuestDescription = 3000, MaxQuestArea = 600, MaxQuestDone = 1000, MaxGoalText = 255;
    public const int MaxQuestRewards = 4, MaxQuestChoices = 6, MaxQuestRep = 5, MaxQuestGoals = 16;
    public const int MaxSeenMembers = 2000;

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

    // how a later description of the same id is taken: newer build = replace, same build = fill the empty fields, older build = ignore
    public enum SeenMerge { Ignore, Fill, Replace }
    public static SeenMerge MergeOf(int storedBuild, int incomingBuild) => incomingBuild > storedBuild ? SeenMerge.Replace : incomingBuild == storedBuild ? SeenMerge.Fill : SeenMerge.Ignore;

    public sealed class SeenNpc
    {
        public long Rev; public string By = ""; public string Name = "", Title = ""; public int Type, Family, Rank, Build, Kc1, Kc2; public long TypeFlags;
        // true when something changed
        public bool Merge(SeenNpc o)
        {
            switch (MergeOf(Build, o.Build))
            {
                case SeenMerge.Replace:
                    Name = o.Name; Title = o.Title; Type = o.Type; Family = o.Family; Rank = o.Rank; TypeFlags = o.TypeFlags; Kc1 = o.Kc1; Kc2 = o.Kc2; Build = o.Build;
                    return true;
                case SeenMerge.Fill: return Fill(o);
                default: return false;
            }
        }
        // later descriptions only fill what is empty (0 = unknown / none); true when something was filled
        public bool Fill(SeenNpc o)
        {
            bool ch = false;
            if (Title.Length == 0 && o.Title.Length > 0) { Title = o.Title; ch = true; }
            if (Type == 0 && o.Type != 0) { Type = o.Type; ch = true; }
            if (Family == 0 && o.Family != 0) { Family = o.Family; ch = true; }
            if (Rank == 0 && o.Rank != 0) { Rank = o.Rank; ch = true; }
            if (TypeFlags == 0 && o.TypeFlags != 0) { TypeFlags = o.TypeFlags; ch = true; }
            if (Kc1 == 0 && o.Kc1 != 0) { Kc1 = o.Kc1; ch = true; }
            if (Kc2 == 0 && o.Kc2 != 0) { Kc2 = o.Kc2; ch = true; }
            return ch;
        }
    }

    public sealed class SeenObject
    {
        public long Rev; public string By = ""; public string Name = ""; public int Type, Build, Lock;
        public bool Merge(SeenObject o)
        {
            switch (MergeOf(Build, o.Build))
            {
                case SeenMerge.Replace: Name = o.Name; Type = o.Type; Lock = o.Lock; Build = o.Build; return true;
                case SeenMerge.Fill: return Fill(o);
                default: return false;
            }
        }
        public bool Fill(SeenObject o)
        {
            bool ch = false;
            if (Type == 0 && o.Type != 0) { Type = o.Type; ch = true; }
            if (Lock == 0 && o.Lock != 0) { Lock = o.Lock; ch = true; }
            return ch;
        }
    }

    public sealed class Goal { public int Type, Obj, Amount; public string Text = ""; }

    public sealed class SeenQuest
    {
        public long Rev; public string By = ""; public string Title = "", Objectives = "", Desc = "", Area = "", Done = "";
        public int Level, MinLevel, Build, Sort, Type, Info, Flags, Group, Next, Money, Spell, Start;
        public bool HasRaces; public uint RacesLo, RacesHi;
        public int[][] Rewards = NoPairs, Choices = NoPairs, Provided = NoPairs;
        public int[] Rep = NoInts;
        public Goal[] Goals = NoGoals;
        public static readonly int[][] NoPairs = new int[0][];
        public static readonly int[] NoInts = new int[0];
        public static readonly Goal[] NoGoals = new Goal[0];

        public bool Merge(SeenQuest o)
        {
            switch (MergeOf(Build, o.Build))
            {
                case SeenMerge.Replace:
                    Title = o.Title; Objectives = o.Objectives; Desc = o.Desc; Area = o.Area; Done = o.Done; Level = o.Level; MinLevel = o.MinLevel; Build = o.Build;
                    Sort = o.Sort; Type = o.Type; Info = o.Info; Flags = o.Flags; Group = o.Group; Next = o.Next; Money = o.Money; Spell = o.Spell; Start = o.Start;
                    HasRaces = o.HasRaces; RacesLo = o.RacesLo; RacesHi = o.RacesHi; Rewards = o.Rewards; Choices = o.Choices; Provided = o.Provided; Rep = o.Rep; Goals = o.Goals;
                    return true;
                case SeenMerge.Fill: return Fill(o);
                default: return false;
            }
        }

        public bool Fill(SeenQuest o)
        {
            bool ch = false;
            void S(ref string a, string b) { if (a.Length == 0 && b.Length > 0) { a = b; ch = true; } }
            void I(ref int a, int b) { if (a == 0 && b != 0) { a = b; ch = true; } }
            S(ref Objectives, o.Objectives); S(ref Desc, o.Desc); S(ref Area, o.Area); S(ref Done, o.Done);
            I(ref Level, o.Level); I(ref MinLevel, o.MinLevel); I(ref Sort, o.Sort); I(ref Type, o.Type); I(ref Info, o.Info); I(ref Flags, o.Flags); I(ref Group, o.Group);
            I(ref Next, o.Next); I(ref Money, o.Money); I(ref Spell, o.Spell); I(ref Start, o.Start);
            if (!HasRaces && o.HasRaces) { HasRaces = true; RacesLo = o.RacesLo; RacesHi = o.RacesHi; ch = true; }
            if (Rewards.Length == 0 && o.Rewards.Length > 0) { Rewards = o.Rewards; ch = true; }
            if (Choices.Length == 0 && o.Choices.Length > 0) { Choices = o.Choices; ch = true; }
            if (Provided.Length == 0 && o.Provided.Length > 0) { Provided = o.Provided; ch = true; }
            if (Rep.Length == 0 && o.Rep.Length > 0) { Rep = o.Rep; ch = true; }
            if (Goals.Length == 0 && o.Goals.Length > 0) { Goals = o.Goals; ch = true; }
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
        x.Build = Opt(o, "build", 0, MaxBuild, out ok); if (!ok) return false;
        if (o["tf"] != null) { if (!Whole(o["tf"], 0, uint.MaxValue, out var tf)) return false; x.TypeFlags = tf; }
        if (o["kc"] != null)
        {
            if (o["kc"] is not JsonArray ka || ka.Count != 2 || !Whole(ka[0], 0, MaxId, out var a) || !Whole(ka[1], 0, MaxId, out var b)) return false;
            x.Kc1 = (int)a; x.Kc2 = (int)b;
        }
        r = x; return true;
    }

    public static bool ParseSeenObject(JsonNode node, out SeenObject r)
    {
        r = null;
        if (node is not JsonObject o || !SeenText(o["name"], MaxNameLength, false, true, out var name)) return false;
        var x = new SeenObject { Name = name };
        bool ok;
        x.Type = Opt(o, "type", 0, 1000, out ok); if (!ok) return false;
        x.Build = Opt(o, "build", 0, MaxBuild, out ok); if (!ok) return false;
        x.Lock = Opt(o, "lock", 0, MaxId, out ok); if (!ok) return false;
        r = x; return true;
    }

    // [[id, count], ...]: at most `max` pairs, ids 1..MaxItemId, counts 1..1000
    static bool Pairs(JsonNode n, int max, out int[][] res)
    {
        res = SeenQuest.NoPairs;
        if (n == null) return true;
        if (n is not JsonArray a || a.Count > max) return false;
        var l = new List<int[]>();
        foreach (var e in a)
        {
            if (e is not JsonArray p || p.Count != 2 || !Whole(p[0], 1, MaxItemId, out var id) || !Whole(p[1], 1, 1000, out var c)) return false;
            l.Add(new[] { (int)id, (int)c });
        }
        res = l.ToArray(); return true;
    }

    public static bool ParseSeenQuest(JsonNode node, out SeenQuest r)
    {
        r = null;
        if (node is not JsonObject o || !SeenText(o["title"], MaxNameLength * 2, false, true, out var title) || !SeenText(o["objectives"], MaxObjectivesLength, true, false, out var obj)) return false;
        if (!SeenText(o["desc"], MaxQuestDescription, true, false, out var desc) || !SeenText(o["area"], MaxQuestArea, true, false, out var area) || !SeenText(o["done"], MaxQuestDone, true, false, out var done)) return false;
        var x = new SeenQuest { Title = title, Objectives = obj, Desc = desc, Area = area, Done = done };
        bool ok;
        x.Level = Opt(o, "level", -1, 255, out ok); if (!ok) return false;
        x.MinLevel = Opt(o, "minLevel", 0, 255, out ok); if (!ok) return false;
        x.Build = Opt(o, "build", 0, MaxBuild, out ok); if (!ok) return false;
        x.Sort = Opt(o, "sort", -100000, 100000, out ok); if (!ok) return false;
        x.Type = Opt(o, "type", 0, 100, out ok); if (!ok) return false;
        x.Info = Opt(o, "info", 0, 10000, out ok); if (!ok) return false;
        x.Flags = Opt(o, "flags", int.MinValue, int.MaxValue, out ok); if (!ok) return false;
        x.Group = Opt(o, "group", 0, 100, out ok); if (!ok) return false;
        x.Next = Opt(o, "next", 0, MaxId, out ok); if (!ok) return false;
        x.Money = Opt(o, "money", 0, int.MaxValue, out ok); if (!ok) return false;
        x.Spell = Opt(o, "spell", 0, MaxId, out ok); if (!ok) return false;
        x.Start = Opt(o, "start", 0, MaxItemId, out ok); if (!ok) return false;
        if (o["races"] != null)
        {
            if (o["races"] is not JsonArray ra || ra.Count != 2 || !Whole(ra[0], 0, uint.MaxValue, out var lo) || !Whole(ra[1], 0, uint.MaxValue, out var hi)) return false;
            x.HasRaces = true; x.RacesLo = (uint)lo; x.RacesHi = (uint)hi;
        }
        if (!Pairs(o["rewards"], MaxQuestRewards, out x.Rewards) || !Pairs(o["choices"], MaxQuestChoices, out x.Choices) || !Pairs(o["provided"], MaxQuestRewards, out x.Provided)) return false;
        if (o["rep"] != null)
        {
            if (o["rep"] is not JsonArray rp || rp.Count > MaxQuestRep) return false;
            var l = new List<int>();
            foreach (var e in rp) { if (!Whole(e, 1, MaxId, out var f)) return false; l.Add((int)f); }
            x.Rep = l.ToArray();
        }
        if (o["goals"] != null)
        {
            if (o["goals"] is not JsonArray ga || ga.Count > MaxQuestGoals) return false;
            var l = new List<Goal>();
            foreach (var e in ga)
            {
                if (e is not JsonArray g || g.Count != 4 || !Whole(g[0], 0, 255, out var t) || !Whole(g[1], int.MinValue, int.MaxValue, out var ob) || !Whole(g[2], int.MinValue, int.MaxValue, out var am)
                    || !SeenText(g[3], MaxGoalText, false, false, out var gt)) return false;
                l.Add(new Goal { Type = (int)t, Obj = (int)ob, Amount = (int)am, Text = gt });
            }
            x.Goals = l.ToArray();
        }
        r = x; return true;
    }

    public static JsonObject SeenNpcJson(SeenNpc x)
    {
        var o = new JsonObject { ["name"] = x.Name, ["title"] = x.Title, ["type"] = x.Type, ["family"] = x.Family, ["rank"] = x.Rank };
        if (x.Build > 0) o["build"] = x.Build;
        if (x.TypeFlags != 0) o["tf"] = x.TypeFlags;
        if (x.Kc1 != 0 || x.Kc2 != 0) o["kc"] = new JsonArray(x.Kc1, x.Kc2);
        return o;
    }

    public static JsonObject SeenObjectJson(SeenObject x)
    {
        var o = new JsonObject { ["name"] = x.Name, ["type"] = x.Type };
        if (x.Build > 0) o["build"] = x.Build;
        if (x.Lock > 0) o["lock"] = x.Lock;
        return o;
    }

    public static JsonObject SeenQuestJson(SeenQuest x)
    {
        var o = new JsonObject { ["title"] = x.Title, ["level"] = x.Level, ["minLevel"] = x.MinLevel, ["objectives"] = x.Objectives };
        if (x.Build > 0) o["build"] = x.Build;
        void I(string k, int v) { if (v != 0) o[k] = v; }
        void S(string k, string v) { if (v.Length > 0) o[k] = v; }
        void P(string k, int[][] v) { if (v.Length > 0) o[k] = new JsonArray(v.Select(p => (JsonNode)new JsonArray(p[0], p[1])).ToArray()); }
        I("sort", x.Sort); I("type", x.Type); I("info", x.Info); I("flags", x.Flags); I("group", x.Group); I("next", x.Next); I("money", x.Money); I("spell", x.Spell); I("start", x.Start);
        if (x.HasRaces) o["races"] = new JsonArray((long)x.RacesLo, (long)x.RacesHi);
        P("rewards", x.Rewards); P("choices", x.Choices); P("provided", x.Provided);
        if (x.Rep.Length > 0) o["rep"] = new JsonArray(x.Rep.Select(f => (JsonNode)f).ToArray());
        if (x.Goals.Length > 0) o["goals"] = new JsonArray(x.Goals.Select(g => (JsonNode)new JsonArray(g.Type, g.Obj, g.Amount, g.Text)).ToArray());
        S("desc", x.Desc); S("area", x.Area); S("done", x.Done);
        return o;
    }
}
