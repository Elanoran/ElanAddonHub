using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ElansAddonHub.Services.Db2;

namespace ElansAddonHub.Services
{
    // What the client has SEEN (layer A3, SeenData: creaturecache.wdb / gameobjectcache.wdb / questcache.wdb): the NPCs, objects and quests this player's client has met on
    // Forever. Like the revealed items they are shared with the Lodge, as `seenNpcs`, `seenObjects` and `seenQuests` sections of the atlas.share batch (ids, names,
    // types; the sections are named so because `objects` and `quests` already carry loot counters and quest rewards). Only sent to a server that advertises "atlas2".
    //
    // "atlas7" (Lodge server 2.25): the verified details of the client's query answers go too (WdbSeen.cs says how each was checked), with the game build the cache
    // was written by. The server keeps, per id, the record of the newest build (a patch that changed a quest replaces it), fills empty fields from the same build
    // and ignores older builds, and counts how many members contributed (seenMembers: a count, never names). Same rules here when the answer is read back.
    // Nothing personal: these are the server's descriptions of creatures, objects and quests, the same for every player.
    public static partial class AtlasObs
    {
        public interface ISeenSource { IEnumerable<WdbCreature> Npcs(); IEnumerable<WdbObject> Objects(); IEnumerable<WdbQuest> Quests(); }
        sealed class NoSeen : ISeenSource
        {
            public IEnumerable<WdbCreature> Npcs() { return new WdbCreature[0]; }
            public IEnumerable<WdbObject> Objects() { return new WdbObject[0]; }
            public IEnumerable<WdbQuest> Quests() { return new WdbQuest[0]; }
        }
        public static ISeenSource SeenSource = new NoSeen();
        public static Func<int> SeenBuild = () => 0;                   // the game build the seen caches were written by (SeenData.LastBuild); 0 = unknown

        public static readonly Version SeenDetailsServerVersion = new Version(2, 25, 0);   // sub-feature "atlas7": the verified details + build

        public class SeenNpc
        {
            public int Id, Type, Family, Rank, Build, Kc1, Kc2; public uint TypeFlags; public string Name = "", Title = "";
            public bool Tameable => (TypeFlags & WdbCreature.TypeTameable) != 0;
        }
        public class SeenObject { public int Id, Type, Build, Lock; public string Name = ""; }
        public class SeenQuest
        {
            public int Id, Level, MinLevel, Build, Sort, Type, Info, Flags, Group, Next, Money, Spell, Start;
            public bool HasRaces; public ulong Races = WdbQuest.AllRaces;
            public WdbItemCount[] Rewards = WdbQuest.NoItems, Choices = WdbQuest.NoItems, Provided = WdbQuest.NoItems;
            public int[] Rep = WdbCreature.NoInts;
            public WdbObjective[] Goals = WdbQuest.NoGoals;
            public string Title = "", Objectives = "", Desc = "", Area = "", Done = "";
            public bool Detailed => Build > 0;

            // the same shape the client cache gives, for the Atlas pages (Full only when the details came along)
            public WdbQuest ToWdb() => new WdbQuest
            {
                Id = Id, Level = Level, MinLevel = MinLevel, Sort = Sort, Type = Type, Title = Title, Objectives = Objectives, Description = Desc, AreaDescription = Area, CompletionLog = Done,
                Full = Detailed, InfoId = Info, Flags = Flags, SuggestedGroup = Group, NextQuest = Next, Money = Money, RewardSpell = Spell, StartItem = Start,
                Races = HasRaces ? Races : WdbQuest.AllRaces, Rewards = Rewards, Choices = Choices, Provided = Provided, RepFactions = Rep, Goals = Goals,
            };
        }

        public const int MaxSeenNpcs = 40000, MaxSeenObjects = 40000, MaxSeenQuests = 20000;
        public const int MaxSeenNpcsPerShare = 300, MaxSeenObjectsPerShare = 300, MaxSeenQuestsPerShare = 100;
        public const int MaxTitleLength = 80, MaxObjectivesLength = 1000;
        public const int MaxSeenBuild = 9_999_999, MaxQuestDescription = 3000, MaxQuestArea = 600, MaxQuestDone = 1000, MaxGoalText = 255;
        public const int MaxQuestRewards = 4, MaxQuestChoices = 6, MaxQuestRep = 5, MaxQuestGoals = 16;

        // a string for the wire: control characters dropped (newline and tab kept where multiline), trimmed, cut to `max`
        static string SeenStr(string s, int max, bool multiline)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(Math.Min(s.Length, max));
            foreach (var c in s)
            {
                if (sb.Length >= max) break;
                if (char.IsControl(c)) { if (multiline && (c == '\n' || c == '\t')) sb.Append(c); else if (c != '\r') sb.Append(' '); }
                else sb.Append(c);
            }
            return sb.ToString().Trim();
        }

        static bool SeenText(object o, int max, bool multiline, bool required, out string s)
        {
            s = "";
            if (o == null) return !required;
            var str = o as string;
            if (str == null || str.Length > max) return false;
            foreach (var c in str) if (char.IsControl(c) && !(multiline && (c == '\n' || c == '\t' || c == '\r'))) return false;
            s = str.Trim();
            return !required || s.Length > 0;
        }

        static object Field(Dictionary<string, object> o, string k) { object v; return o.TryGetValue(k, out v) ? v : null; }

        // ---- the same rules as the server (a lodge answer is checked again here)
        public static SeenNpc ParseSeenNpc(int id, object node, ref int dropped)
        {
            var o = node as Dictionary<string, object>;
            string name, title;
            if (o == null || !SeenText(Field(o, "name"), MaxNameLength, false, true, out name) || !SeenText(Field(o, "title"), MaxTitleLength, false, false, out title)) { dropped++; return null; }
            bool ok = true;
            var r = new SeenNpc { Id = id, Name = name, Title = title };
            r.Type = OptInt(o, "type", 0, 100, ref ok); r.Family = OptInt(o, "family", 0, 1000, ref ok); r.Rank = OptInt(o, "rank", 0, 20, ref ok);
            r.Build = OptInt(o, "build", 0, MaxSeenBuild, ref ok);
            var tf = Field(o, "tf");
            if (tf != null) { if (Whole(tf, 0, uint.MaxValue, out var t)) r.TypeFlags = (uint)t; else ok = false; }
            var kc = Field(o, "kc");
            if (kc != null)
            {
                if (kc is List<object> kl && kl.Count == 2 && Whole(kl[0], 0, MaxId, out var a) && Whole(kl[1], 0, MaxId, out var b)) { r.Kc1 = (int)a; r.Kc2 = (int)b; }
                else ok = false;
            }
            if (!ok) { dropped++; return null; }
            return r;
        }

        public static SeenObject ParseSeenObject(int id, object node, ref int dropped)
        {
            var o = node as Dictionary<string, object>;
            string name;
            if (o == null || !SeenText(Field(o, "name"), MaxNameLength, false, true, out name)) { dropped++; return null; }
            bool ok = true;
            var r = new SeenObject { Id = id, Name = name, Type = OptInt(o, "type", 0, 1000, ref ok), Build = OptInt(o, "build", 0, MaxSeenBuild, ref ok), Lock = OptInt(o, "lock", 0, MaxId, ref ok) };
            if (!ok) { dropped++; return null; }
            return r;
        }

        static bool SeenPairs(object n, int max, out WdbItemCount[] res)
        {
            res = WdbQuest.NoItems;
            if (n == null) return true;
            if (!(n is List<object> a) || a.Count > max) return false;
            var l = new List<WdbItemCount>();
            foreach (var e in a)
            {
                if (!(e is List<object> p) || p.Count != 2 || !Whole(p[0], 1, MaxItemId, out var id) || !Whole(p[1], 1, 1000, out var c)) return false;
                l.Add(new WdbItemCount { Id = (int)id, Count = (int)c });
            }
            res = l.ToArray(); return true;
        }

        public static SeenQuest ParseSeenQuest(int id, object node, ref int dropped)
        {
            var o = node as Dictionary<string, object>;
            string title, obj, desc, area, done;
            if (o == null || !SeenText(Field(o, "title"), MaxNameLength * 2, false, true, out title) || !SeenText(Field(o, "objectives"), MaxObjectivesLength, true, false, out obj)
                || !SeenText(Field(o, "desc"), MaxQuestDescription, true, false, out desc) || !SeenText(Field(o, "area"), MaxQuestArea, true, false, out area) || !SeenText(Field(o, "done"), MaxQuestDone, true, false, out done)) { dropped++; return null; }
            bool ok = true;
            var r = new SeenQuest { Id = id, Title = title, Objectives = obj, Desc = desc, Area = area, Done = done };
            r.Level = OptInt(o, "level", -1, 255, ref ok); r.MinLevel = OptInt(o, "minLevel", 0, 255, ref ok); r.Build = OptInt(o, "build", 0, MaxSeenBuild, ref ok);
            r.Sort = OptInt(o, "sort", -100000, 100000, ref ok); r.Type = OptInt(o, "type", 0, 100, ref ok); r.Info = OptInt(o, "info", 0, 10000, ref ok);
            r.Flags = OptInt(o, "flags", int.MinValue, int.MaxValue, ref ok); r.Group = OptInt(o, "group", 0, 100, ref ok); r.Next = OptInt(o, "next", 0, MaxId, ref ok);
            r.Money = OptInt(o, "money", 0, int.MaxValue, ref ok); r.Spell = OptInt(o, "spell", 0, MaxId, ref ok); r.Start = OptInt(o, "start", 0, MaxItemId, ref ok);
            var ra = Field(o, "races");
            if (ra != null)
            {
                if (ra is List<object> rl && rl.Count == 2 && Whole(rl[0], 0, uint.MaxValue, out var lo) && Whole(rl[1], 0, uint.MaxValue, out var hi)) { r.HasRaces = true; r.Races = (ulong)lo | ((ulong)hi << 32); }
                else ok = false;
            }
            if (!SeenPairs(Field(o, "rewards"), MaxQuestRewards, out r.Rewards) || !SeenPairs(Field(o, "choices"), MaxQuestChoices, out r.Choices) || !SeenPairs(Field(o, "provided"), MaxQuestRewards, out r.Provided)) ok = false;
            var rp = Field(o, "rep");
            if (rp != null)
            {
                var l = new List<int>();
                if (rp is List<object> pl && pl.Count <= MaxQuestRep) { foreach (var e in pl) { if (Whole(e, 1, MaxId, out var f)) l.Add((int)f); else ok = false; } }
                else ok = false;
                r.Rep = l.ToArray();
            }
            var gs = Field(o, "goals");
            if (gs != null)
            {
                var l = new List<WdbObjective>();
                if (gs is List<object> gl && gl.Count <= MaxQuestGoals)
                    foreach (var e in gl)
                    {
                        if (e is List<object> g && g.Count == 4 && Whole(g[0], 0, 255, out var t) && Whole(g[1], int.MinValue, int.MaxValue, out var ob) && Whole(g[2], int.MinValue, int.MaxValue, out var am)
                            && SeenText(g[3], MaxGoalText, false, false, out var gt)) l.Add(new WdbObjective { Type = (int)t, ObjectId = (int)ob, Amount = (int)am, Text = gt });
                        else ok = false;
                    }
                else ok = false;
                r.Goals = l.ToArray();
            }
            if (!ok) { dropped++; return null; }
            return r;
        }

        // ---- the wire shapes. details = false: the 2.15 shape (a server without "atlas7" gets exactly what it got before, with the same signatures)
        internal static Dictionary<string, object> SeenNpcNode(SeenNpc n, bool details = true)
        {
            var o = new Dictionary<string, object> { ["name"] = n.Name, ["title"] = n.Title, ["type"] = n.Type, ["family"] = n.Family, ["rank"] = n.Rank };
            if (!details) return o;
            if (n.Build > 0) o["build"] = n.Build;
            if (n.TypeFlags != 0) o["tf"] = (long)n.TypeFlags;
            if (n.Kc1 != 0 || n.Kc2 != 0) o["kc"] = new List<object> { n.Kc1, n.Kc2 };
            return o;
        }

        internal static Dictionary<string, object> SeenObjectNode(SeenObject n, bool details = true)
        {
            var o = new Dictionary<string, object> { ["name"] = n.Name, ["type"] = n.Type };
            if (!details) return o;
            if (n.Build > 0) o["build"] = n.Build;
            if (n.Lock > 0) o["lock"] = n.Lock;
            return o;
        }

        internal static Dictionary<string, object> SeenQuestNode(SeenQuest n, bool details = true)
        {
            var o = new Dictionary<string, object> { ["title"] = n.Title, ["level"] = n.Level, ["minLevel"] = n.MinLevel, ["objectives"] = n.Objectives };
            if (!details) return o;
            if (n.Build > 0) o["build"] = n.Build;
            void Int(string k, int v) { if (v != 0) o[k] = v; }
            void Str(string k, string v) { if (!string.IsNullOrEmpty(v)) o[k] = v; }
            void Pairs(string k, WdbItemCount[] v) { if (v.Length > 0) o[k] = v.Select(p => (object)new List<object> { p.Id, p.Count }).ToList(); }
            Int("sort", n.Sort); Int("type", n.Type); Int("info", n.Info); Int("flags", n.Flags); Int("group", n.Group); Int("next", n.Next); Int("money", n.Money); Int("spell", n.Spell); Int("start", n.Start);
            if (n.HasRaces) o["races"] = new List<object> { (long)(uint)n.Races, (long)(uint)(n.Races >> 32) };
            Pairs("rewards", n.Rewards); Pairs("choices", n.Choices); Pairs("provided", n.Provided);
            if (n.Rep.Length > 0) o["rep"] = n.Rep.Select(f => (object)f).ToList();
            if (n.Goals.Length > 0) o["goals"] = n.Goals.Select(g => (object)new List<object> { g.Type, g.ObjectId, g.Amount, g.Text ?? "" }).ToList();
            Str("desc", n.Desc); Str("area", n.Area); Str("done", n.Done);
            return o;
        }

        public static string Sig(SeenNpc n, bool details = true) { return Hash(n.Name + "|" + n.Title + "|" + n.Type + "|" + n.Family + "|" + n.Rank + (details ? "|" + Json.Write(SeenNpcNode(n)) : "")); }
        public static string Sig(SeenObject n, bool details = true) { return Hash(n.Name + "|" + n.Type + (details ? "|" + Json.Write(SeenObjectNode(n)) : "")); }
        public static string Sig(SeenQuest n, bool details = true) { return Hash(n.Title + "|" + n.Level + "|" + n.MinLevel + "|" + n.Objectives + (details ? "|" + Json.Write(SeenQuestNode(n)) : "")); }

        // ---- what this client has seen, as validated shapes ready for the wire (anything that fails the rules is left out)
        static int SafeBuild() { try { int b = SeenBuild(); return b > 0 && b <= MaxSeenBuild ? b : 0; } catch { return 0; } }

        static IEnumerable<SeenNpc> SeenNpcs(ISeenSource src, bool details)
        {
            int seen = 0, build = SafeBuild();
            foreach (var c in src.Npcs() ?? new WdbCreature[0])
            {
                if (c == null || c.Id < 1 || c.Id > MaxId || ++seen > MaxSeenNpcs) continue;
                var n = new SeenNpc { Id = c.Id, Name = SeenStr(c.Name, MaxNameLength, false), Title = SeenStr(c.Title, MaxTitleLength, false), Type = c.Type, Family = c.Family, Rank = c.Rank };
                if (details)
                {
                    n.Build = build; n.TypeFlags = c.Flags0;
                    n.Kc1 = c.KillCredit1 >= 0 && c.KillCredit1 <= MaxId ? c.KillCredit1 : 0; n.Kc2 = c.KillCredit2 >= 0 && c.KillCredit2 <= MaxId ? c.KillCredit2 : 0;
                }
                int d = 0;
                if (n.Name.Length > 0 && ParseSeenNpc(n.Id, ToParsed(SeenNpcNode(n, details)), ref d) != null) yield return n;
            }
        }

        static IEnumerable<SeenObject> SeenObjects(ISeenSource src, bool details)
        {
            int seen = 0, build = SafeBuild();
            foreach (var c in src.Objects() ?? new WdbObject[0])
            {
                if (c == null || c.Id < 1 || c.Id > MaxId || ++seen > MaxSeenObjects) continue;
                var n = new SeenObject { Id = c.Id, Name = SeenStr(c.Name, MaxNameLength, false), Type = c.Type };
                if (details) { n.Build = build; n.Lock = c.LockId > 0 && c.LockId <= MaxId ? c.LockId : 0; }
                int d = 0;
                if (n.Name.Length > 0 && ParseSeenObject(n.Id, ToParsed(SeenObjectNode(n, details)), ref d) != null) yield return n;
            }
        }

        static WdbItemCount[] Items(WdbItemCount[] a, int max) => (a ?? WdbQuest.NoItems).Where(x => x.Id >= 1 && x.Id <= MaxItemId).Select(x => new WdbItemCount { Id = x.Id, Count = Math.Max(1, Math.Min(1000, x.Count)) }).Take(max).ToArray();

        // a quest of the client cache as the wire shape (details only from a fully decoded record)
        public static SeenQuest FromWdb(WdbQuest c, bool details, int build)
        {
            var n = new SeenQuest { Id = c.Id, Title = SeenStr(c.Title, MaxNameLength * 2, false), Level = c.Level, MinLevel = c.MinLevel, Objectives = SeenStr(c.Objectives, MaxObjectivesLength, true) };
            if (!details || !c.Full) return n;
            n.Build = build;
            n.Sort = c.Sort; n.Type = c.Type; n.Info = c.InfoId; n.Flags = c.Flags; n.Group = c.SuggestedGroup; n.Next = c.NextQuest > 0 && c.NextQuest <= MaxId ? c.NextQuest : 0;
            n.Money = Math.Max(0, c.Money); n.Spell = c.RewardSpell > 0 && c.RewardSpell <= MaxId ? c.RewardSpell : 0; n.Start = c.StartItem > 0 && c.StartItem <= MaxItemId ? c.StartItem : 0;
            n.HasRaces = true; n.Races = c.Races;
            n.Rewards = Items(c.Rewards, MaxQuestRewards); n.Choices = Items(c.Choices, MaxQuestChoices); n.Provided = Items(c.Provided, MaxQuestRewards);
            n.Rep = (c.RepFactions ?? new int[0]).Where(f => f >= 1 && f <= MaxId).Take(MaxQuestRep).ToArray();
            n.Goals = (c.Goals ?? WdbQuest.NoGoals).Where(g => g.Type >= 0 && g.Type <= 255).Take(MaxQuestGoals).Select(g => new WdbObjective { Type = g.Type, ObjectId = g.ObjectId, Amount = g.Amount, Text = SeenStr(g.Text, MaxGoalText, false) }).ToArray();
            n.Desc = SeenStr(c.Description, MaxQuestDescription, true); n.Area = SeenStr(c.AreaDescription, MaxQuestArea, true); n.Done = SeenStr(c.CompletionLog, MaxQuestDone, true);
            return n;
        }

        static IEnumerable<SeenQuest> SeenQuests(ISeenSource src, bool details)
        {
            int seen = 0, build = SafeBuild();
            foreach (var c in src.Quests() ?? new WdbQuest[0])
            {
                if (c == null || c.Id < 1 || c.Id > MaxId || ++seen > MaxSeenQuests) continue;
                var n = FromWdb(c, details, build);
                int d = 0;
                if (n.Title.Length > 0 && ParseSeenQuest(n.Id, ToParsed(SeenQuestNode(n, details)), ref d) != null) yield return n;
            }
        }
    }
}
