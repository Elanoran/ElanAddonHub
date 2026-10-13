using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ElansAddonHub.Services
{
    // What Elan's Toolkit observed, as the Outpost sees it (ARCHITECTURE.md, `atlas.obs` v1, Hub account module): loot per npc / object / fishing zone,
    // vendor stock, quest rewards, finds, spawn points. This file (1) reads the module and validates every field (ids positive whole numbers,
    // counts bounded, x/y 0..1, lists capped) - the same rules as the Lodge server's `Atlas` class, (2) computes what changed since the last upload
    // (a signature per entry, kept per Lodge server) and builds the `atlas.share` batches, (3) holds the validated shapes the Lodge's answer is parsed into
    // (LodgeAtlas). Counters are CUMULATIVE totals: the server stores max(stored, sent) per field, so a resend can never count twice. Nothing here writes
    // to the game folder, and nothing but numbers, ids, item details and coordinates ever leaves: no character names.
    public static class AtlasObs
    {
        // ---- limits (mirror the server)
        public const int MaxId = 9_999_999, MaxItemId = 999_999, MaxMapId = 99_999;
        public const long MaxCount = 100_000;                    // one member, one source
        public const long MaxAggregateCount = 2_000_000_000L;    // the lodge's sum
        public const int MaxDropsPerKill = 10, MaxStack = 200;
        public const int MaxItemsPerSource = 150, MaxZonesPerSource = 40;
        public const int MaxKillSources = 6000, MaxObjectSources = 3000, MaxFishingZones = 400;
        public const int MaxVendors = 6000, MaxQuests = 20000, MaxSpawnNpcs = 8000, MaxRevealed = 40000;
        public const int MaxVendorItems = 300, MaxQuestItems = 10, MaxStats = 10;
        public const int MaxFinds = 2000, MaxHubFinds = 500, MaxSpawnPoints = 50, MaxPointsPerNpcPerShare = 20;
        public const int MaxNameLength = 80;
        // per share message (the server's frame limit is 64 KB)
        public const int MaxKillsPerShare = 200, MaxObjectsPerShare = 100, MaxFishingPerShare = 50, MaxVendorsPerShare = 60,
            MaxQuestsPerShare = 200, MaxFindsPerShare = 500, MaxSpawnNpcsPerShare = 200, MaxRevealedPerShare = 200;
        public const int MaxShareBytes = 56 * 1024;
        public const string ModuleName = "atlas.obs";
        public const int ModuleVersion = 1;
        public static readonly TimeSpan ShareEvery = TimeSpan.FromMinutes(3);
        public static readonly Version ServerVersion = new Version(2, 15, 0);
        public const long MinUnix = 1_000_000_000L;

        // ---------------------------------------------------------------- shapes

        public class Counter
        {
            public long N;                                                     // looted kills / opens / casts
            public Dictionary<int, long[]> Items = new Dictionary<int, long[]>();   // item -> { times dropped, total count }
            public Dictionary<int, long> Zones = new Dictionary<int, long>();       // uiMapId -> n there
            public int Members;                                                // (lodge aggregate) how many members contributed
        }

        public class Vendor
        {
            public long At; public int Zone; public double X, Y;
            public Dictionary<int, long[]> Items = new Dictionary<int, long[]>();   // item -> { price copper, stack, limit (-1 none) }
        }

        public class Quest { public long At; public int[] Rewards = new int[0], Choices = new int[0]; }

        public struct Spot { public int Zone; public double X, Y; }

        public class Find
        {
            public int Item, Count, Zone; public double X, Y; public string Src; public long At;
            public string By;                                                  // (lodge aggregate) the member's display name
            public long Seq;                                                   // (lodge aggregate) server revision, orders the list
        }

        public class Revealed
        {
            public int Id, Quality, ItemLevel, RequiredLevel, Class, SubClass, Slot;
            public string Name;
            public int[][] Stats = new int[0][];                               // { statType, value }
            public int Armor, DmgMin, DmgMax;                                  // worked out by the Outpost from the client's item-level tables; kept locally, not on the wire (no field in atlas.share items yet)
        }

        // item details the game client revealed (name, quality, item level, slot, stats) that the Outpost shares with the Lodge. Another layer (ItemData, read from
        // DBCache.bin) implements this later; until then nothing is revealed.
        public interface IRevealedItemsSource { IEnumerable<Revealed> Items(); }
        sealed class NoRevealed : IRevealedItemsSource { public IEnumerable<Revealed> Items() { return new Revealed[0]; } }
        public static readonly IRevealedItemsSource Empty = new NoRevealed();
        public static IRevealedItemsSource RevealedSource = Empty;

        // What the client has SEEN (layer A3, SeenData: creaturecache.wdb / gameobjectcache.wdb / questcache.wdb): the NPCs, objects and quests this player's client has met on
        // Forever. Like the revealed items above they are shared with the Lodge, as `seenNpcs`, `seenObjects` and `seenQuests` sections of the atlas.share batch (ids, names,
        // types; the sections are named so because `objects` and `quests` already carry loot counters and quest rewards). Only sent to a server that advertises "atlas2".
        public interface ISeenSource { IEnumerable<ElansAddonHub.Services.Db2.WdbCreature> Npcs(); IEnumerable<ElansAddonHub.Services.Db2.WdbObject> Objects(); IEnumerable<ElansAddonHub.Services.Db2.WdbQuest> Quests(); }
        sealed class NoSeen : ISeenSource
        {
            public IEnumerable<ElansAddonHub.Services.Db2.WdbCreature> Npcs() { return new ElansAddonHub.Services.Db2.WdbCreature[0]; }
            public IEnumerable<ElansAddonHub.Services.Db2.WdbObject> Objects() { return new ElansAddonHub.Services.Db2.WdbObject[0]; }
            public IEnumerable<ElansAddonHub.Services.Db2.WdbQuest> Quests() { return new ElansAddonHub.Services.Db2.WdbQuest[0]; }
        }
        public static ISeenSource SeenSource = new NoSeen();
        public class SeenNpc { public int Id, Type, Family, Rank; public string Name = "", Title = ""; }
        public class SeenObject { public int Id, Type; public string Name = ""; }
        public class SeenQuest { public int Id, Level, MinLevel; public string Title = "", Objectives = ""; }
        public const int MaxSeenNpcs = 40000, MaxSeenObjects = 40000, MaxSeenQuests = 20000;
        public const int MaxSeenNpcsPerShare = 300, MaxSeenObjectsPerShare = 300, MaxSeenQuestsPerShare = 100;
        public const int MaxTitleLength = 80, MaxObjectivesLength = 1000;

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

        // the same rules as the server (a lodge answer is checked again here)
        public static SeenNpc ParseSeenNpc(int id, object node, ref int dropped)
        {
            var o = node as Dictionary<string, object>;
            string name, title;
            if (o == null || !SeenText(Field(o, "name"), MaxNameLength, false, true, out name) || !SeenText(Field(o, "title"), MaxTitleLength, false, false, out title)) { dropped++; return null; }
            bool ok = true;
            var r = new SeenNpc { Id = id, Name = name, Title = title };
            r.Type = OptInt(o, "type", 0, 100, ref ok); r.Family = OptInt(o, "family", 0, 1000, ref ok); r.Rank = OptInt(o, "rank", 0, 20, ref ok);
            if (!ok) { dropped++; return null; }
            return r;
        }

        public static SeenObject ParseSeenObject(int id, object node, ref int dropped)
        {
            var o = node as Dictionary<string, object>;
            string name;
            if (o == null || !SeenText(Field(o, "name"), MaxNameLength, false, true, out name)) { dropped++; return null; }
            bool ok = true;
            var r = new SeenObject { Id = id, Name = name, Type = OptInt(o, "type", 0, 1000, ref ok) };
            if (!ok) { dropped++; return null; }
            return r;
        }

        public static SeenQuest ParseSeenQuest(int id, object node, ref int dropped)
        {
            var o = node as Dictionary<string, object>;
            string title, obj;
            if (o == null || !SeenText(Field(o, "title"), MaxNameLength * 2, false, true, out title) || !SeenText(Field(o, "objectives"), MaxObjectivesLength, true, false, out obj)) { dropped++; return null; }
            bool ok = true;
            var r = new SeenQuest { Id = id, Title = title, Objectives = obj };
            r.Level = OptInt(o, "level", -1, 255, ref ok); r.MinLevel = OptInt(o, "minLevel", 0, 255, ref ok);
            if (!ok) { dropped++; return null; }
            return r;
        }

        internal static Dictionary<string, object> SeenNpcNode(SeenNpc n)
        {
            return new Dictionary<string, object> { ["name"] = n.Name, ["title"] = n.Title, ["type"] = n.Type, ["family"] = n.Family, ["rank"] = n.Rank };
        }

        internal static Dictionary<string, object> SeenObjectNode(SeenObject n)
        {
            return new Dictionary<string, object> { ["name"] = n.Name, ["type"] = n.Type };
        }

        internal static Dictionary<string, object> SeenQuestNode(SeenQuest n)
        {
            return new Dictionary<string, object> { ["title"] = n.Title, ["level"] = n.Level, ["minLevel"] = n.MinLevel, ["objectives"] = n.Objectives };
        }

        public static string Sig(SeenNpc n) { return Hash(n.Name + "|" + n.Title + "|" + n.Type + "|" + n.Family + "|" + n.Rank); }
        public static string Sig(SeenObject n) { return Hash(n.Name + "|" + n.Type); }
        public static string Sig(SeenQuest n) { return Hash(n.Title + "|" + n.Level + "|" + n.MinLevel + "|" + n.Objectives); }

        // what this client has seen, as validated shapes ready for the wire (anything that fails the rules is left out)
        static IEnumerable<SeenNpc> SeenNpcs(ISeenSource src)
        {
            int seen = 0;
            foreach (var c in src.Npcs() ?? new ElansAddonHub.Services.Db2.WdbCreature[0])
            {
                if (c == null || c.Id < 1 || c.Id > MaxId || ++seen > MaxSeenNpcs) continue;
                var n = new SeenNpc { Id = c.Id, Name = SeenStr(c.Name, MaxNameLength, false), Title = SeenStr(c.Title, MaxTitleLength, false), Type = c.Type, Family = c.Family, Rank = c.Rank };
                int d = 0;
                if (n.Name.Length > 0 && ParseSeenNpc(n.Id, ToParsed(SeenNpcNode(n)), ref d) != null) yield return n;
            }
        }

        static IEnumerable<SeenObject> SeenObjects(ISeenSource src)
        {
            int seen = 0;
            foreach (var c in src.Objects() ?? new ElansAddonHub.Services.Db2.WdbObject[0])
            {
                if (c == null || c.Id < 1 || c.Id > MaxId || ++seen > MaxSeenObjects) continue;
                var n = new SeenObject { Id = c.Id, Name = SeenStr(c.Name, MaxNameLength, false), Type = c.Type };
                int d = 0;
                if (n.Name.Length > 0 && ParseSeenObject(n.Id, ToParsed(SeenObjectNode(n)), ref d) != null) yield return n;
            }
        }

        static IEnumerable<SeenQuest> SeenQuests(ISeenSource src)
        {
            int seen = 0;
            foreach (var c in src.Quests() ?? new ElansAddonHub.Services.Db2.WdbQuest[0])
            {
                if (c == null || c.Id < 1 || c.Id > MaxId || ++seen > MaxSeenQuests) continue;
                var n = new SeenQuest { Id = c.Id, Title = SeenStr(c.Title, MaxNameLength * 2, false), Level = c.Level, MinLevel = c.MinLevel, Objectives = SeenStr(c.Objectives, MaxObjectivesLength, true) };
                int d = 0;
                if (n.Title.Length > 0 && ParseSeenQuest(n.Id, ToParsed(SeenQuestNode(n)), ref d) != null) yield return n;
            }
        }


        public class Snapshot
        {
            public Dictionary<int, Counter> Kills = new Dictionary<int, Counter>(), Objects = new Dictionary<int, Counter>(), Fishing = new Dictionary<int, Counter>();
            public Dictionary<int, Vendor> Vendors = new Dictionary<int, Vendor>();
            public Dictionary<int, Quest> Quests = new Dictionary<int, Quest>();
            public Dictionary<int, List<Spot>> Spawns = new Dictionary<int, List<Spot>>();
            public List<Find> Finds = new List<Find>();
            public int Dropped;                                                // entries (or parts of them) that failed validation and were left out
            public long Since;
            public bool IsEmpty => Kills.Count + Objects.Count + Fishing.Count + Vendors.Count + Quests.Count + Spawns.Count + Finds.Count == 0;
        }

        // ---------------------------------------------------------------- validation helpers

        static bool Num(object o, out double d)
        {
            d = 0;
            if (o is double x && !double.IsNaN(x) && !double.IsInfinity(x)) { d = x; return true; }
            return false;
        }

        public static bool Whole(object o, long min, long max, out long v)
        {
            v = 0;
            if (!Num(o, out var d) || d != Math.Floor(d) || d < min || d > max) return false;
            v = (long)d; return true;
        }

        public static bool Coord(object o, out double v)
        {
            if (!Num(o, out v) || v < 0 || v > 1) return false;
            v = Math.Round(v, 3, MidpointRounding.AwayFromZero); return true;
        }

        // a table key: plain decimal digits, no sign or leading zero, 1..max
        public static bool Key(string k, int max, out int id)
        {
            id = 0;
            if (string.IsNullOrEmpty(k) || k.Length > 7 || k[0] == '0') return false;
            foreach (var ch in k) if (ch < '0' || ch > '9') return false;
            id = int.Parse(k, CultureInfo.InvariantCulture);
            return id >= 1 && id <= max;
        }

        public static bool ValidSrc(string s)
        {
            if (s == null || s.Length > 16) return false;
            if (s == "fish") return true;
            int c = s.IndexOf(':');
            if (c < 0) return false;
            var kind = s.Substring(0, c);
            if (kind != "npc" && kind != "obj" && kind != "vendor" && kind != "quest") return false;
            return Key(s.Substring(c + 1), MaxId, out _);
        }

        // item or zone name: control / format characters out, no quote, backslash, angle bracket, pipe or square bracket, whitespace collapsed, at most 80 chars
        public static string CleanName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(Math.Min(s.Length, MaxNameLength * 2));
            bool space = false;
            for (int i = 0; i < s.Length && sb.Length < MaxNameLength * 2; i++)
            {
                char c = s[i];
                var cat = char.GetUnicodeCategory(c);
                if (char.IsWhiteSpace(c) || cat == UnicodeCategory.SpaceSeparator) { space = sb.Length > 0; continue; }
                if (cat == UnicodeCategory.Control || cat == UnicodeCategory.Format || cat == UnicodeCategory.LineSeparator || cat == UnicodeCategory.ParagraphSeparator
                    || cat == UnicodeCategory.PrivateUse || cat == UnicodeCategory.OtherNotAssigned) continue;
                if (cat == UnicodeCategory.Surrogate)
                {
                    if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { if (space) { sb.Append(' '); space = false; } sb.Append(c).Append(s[++i]); }
                    continue;
                }
                if (c == '"' || c == '\\' || c == '<' || c == '>' || c == '|' || c == '[' || c == ']') continue;
                if (space) { sb.Append(' '); space = false; }
                sb.Append(c);
            }
            var r = sb.ToString().Trim();
            if (r.Length > MaxNameLength)
            {
                int cut = MaxNameLength;
                if (char.IsHighSurrogate(r[cut - 1])) cut--;
                r = r.Substring(0, cut).TrimEnd();
            }
            return r;
        }

        // ---------------------------------------------------------------- parsing one entry (shared by the Hub module and the Lodge's answer)

        // A counter {n, items:{id:{n,c}}, z:{map:n}}. Invalid parts are left out and counted (an item or zone that fails, zones that add up to more than n);
        // an entry whose n is invalid is dropped. maxN: 100000 for one member's data, much more for the lodge's sums.
        public static Counter ParseCounter(object node, bool zones, long maxN, ref int dropped)
        {
            var o = node as Dictionary<string, object>;
            if (o == null || !o.TryGetValue("n", out var nv) || !Whole(nv, 0, maxN, out var n)) { dropped++; return null; }
            var c = new Counter { N = n };
            if (o.TryGetValue("m", out var mv) && Whole(mv, 0, 100_000, out var m)) c.Members = (int)m;
            if (o.TryGetValue("items", out var iv))
            {
                var io = iv as Dictionary<string, object>;
                if (io == null) dropped++;
                else
                    foreach (var kv in io)
                    {
                        if (c.Items.Count >= MaxItemsPerSource) { dropped++; continue; }
                        var it = kv.Value as Dictionary<string, object>;
                        if (!Key(kv.Key, MaxItemId, out var id) || it == null || !it.TryGetValue("n", out var inv) || !Whole(inv, 1, n * MaxDropsPerKill, out var dn)
                            || !it.TryGetValue("c", out var icv) || !Whole(icv, dn, dn * MaxStack, out var dc)) { dropped++; continue; }
                        c.Items[id] = new[] { dn, dc };
                    }
            }
            if (zones && o.TryGetValue("z", out var zv))
            {
                var zo = zv as Dictionary<string, object>;
                if (zo == null) dropped++;
                else
                {
                    var z = new Dictionary<int, long>();
                    long sum = 0;
                    foreach (var kv in zo)
                    {
                        if (z.Count >= MaxZonesPerSource) { dropped++; continue; }
                        if (!Key(kv.Key, MaxMapId, out var id) || !Whole(kv.Value, 1, n, out var zn)) { dropped++; continue; }
                        z[id] = zn; sum += zn;
                    }
                    if (sum > n) { dropped++; z.Clear(); }
                    c.Zones = z;
                }
            }
            return c;
        }

        public static Vendor ParseVendor(object node, ref int dropped)
        {
            var o = node as Dictionary<string, object>;
            if (o == null || !o.TryGetValue("at", out var at) || !Whole(at, MinUnix, 4_000_000_000L, out var a)
                || !o.TryGetValue("z", out var zv) || !Whole(zv, 1, MaxMapId, out var z) || !o.TryGetValue("x", out var xv) || !Coord(xv, out var x)
                || !o.TryGetValue("y", out var yv) || !Coord(yv, out var y)) { dropped++; return null; }
            var v = new Vendor { At = a, Zone = (int)z, X = x, Y = y };
            if (o.TryGetValue("items", out var iv) && iv is Dictionary<string, object> io)
                foreach (var kv in io)
                {
                    if (v.Items.Count >= MaxVendorItems) { dropped++; continue; }
                    var it = kv.Value as Dictionary<string, object>;
                    if (!Key(kv.Key, MaxItemId, out var id) || it == null || !it.TryGetValue("p", out var pv) || !Whole(pv, 0, 100_000_000, out var p)
                        || !it.TryGetValue("s", out var sv) || !Whole(sv, 1, 1000, out var s) || !it.TryGetValue("l", out var lv) || !Whole(lv, -1, 1000, out var l)) { dropped++; continue; }
                    v.Items[id] = new[] { p, s, l };
                }
            return v;
        }

        static int[] ItemList(object o, ref int dropped)
        {
            var d = o as Dictionary<string, object>;            // a Lua array: keys "1", "2", ...
            var l = o as List<object>;                          // a JSON array
            IEnumerable<object> vals = l != null ? (IEnumerable<object>)l : d != null ? d.OrderBy(kv => kv.Key.Length).ThenBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Value) : null;
            var res = new List<int>();
            if (vals == null) return new int[0];
            foreach (var e in vals)
            {
                if (res.Count >= MaxQuestItems || !Whole(e, 1, MaxItemId, out var id)) { dropped++; continue; }
                if (!res.Contains((int)id)) res.Add((int)id);
            }
            return res.ToArray();
        }

        public static Quest ParseQuest(object node, ref int dropped)
        {
            var o = node as Dictionary<string, object>;
            if (o == null || !o.TryGetValue("at", out var at) || !Whole(at, MinUnix, 4_000_000_000L, out var a)) { dropped++; return null; }
            var q = new Quest { At = a };
            if (o.TryGetValue("rewards", out var rw)) q.Rewards = ItemList(rw, ref dropped);
            if (o.TryGetValue("choices", out var ch)) q.Choices = ItemList(ch, ref dropped);
            return q;
        }

        public static bool ParseSpot(object node, out Spot s)
        {
            s = default(Spot);
            var o = node as Dictionary<string, object>;
            if (o == null || !o.TryGetValue("z", out var zv) || !Whole(zv, 1, MaxMapId, out var z) || !o.TryGetValue("x", out var xv) || !Coord(xv, out var x)
                || !o.TryGetValue("y", out var yv) || !Coord(yv, out var y)) return false;
            s = new Spot { Zone = (int)z, X = x, Y = y };
            return true;
        }

        // a list as a Lua array (dictionary keyed "1".."n", in index order) or a JSON array
        static IEnumerable<object> ArrayValues(object o)
        {
            if (o is List<object> l) return l;
            if (o is Dictionary<string, object> d) return d.OrderBy(kv => kv.Key.Length).ThenBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Value);
            return new object[0];
        }

        public static List<Spot> ParseSpots(object node, int max, ref int dropped)
        {
            var res = new List<Spot>();
            foreach (var e in ArrayValues(node))
            {
                if (res.Count >= max || !ParseSpot(e, out var s)) { dropped++; continue; }
                if (!res.Any(p => p.Zone == s.Zone && Math.Abs(p.X - s.X) <= 0.01 && Math.Abs(p.Y - s.Y) <= 0.01)) res.Add(s);
            }
            return res;
        }

        public static Find ParseFind(object node, ref int dropped)
        {
            var o = node as Dictionary<string, object>;
            if (o == null || !o.TryGetValue("i", out var iv) || !Whole(iv, 1, MaxItemId, out var i) || !o.TryGetValue("c", out var cv) || !Whole(cv, 1, 1000, out var c)
                || !o.TryGetValue("at", out var av) || !Whole(av, MinUnix, 4_000_000_000L, out var at) || !(o.TryGetValue("src", out var sv) && sv is string src && ValidSrc(src))) { dropped++; return null; }
            var f = new Find { Item = (int)i, Count = (int)c, At = at, Src = src };
            if (o.ContainsKey("z") || o.ContainsKey("x") || o.ContainsKey("y"))
            {
                if (!o.TryGetValue("z", out var zv) || !Whole(zv, 1, MaxMapId, out var z) || !o.TryGetValue("x", out var xv) || !Coord(xv, out var x) || !o.TryGetValue("y", out var yv) || !Coord(yv, out var y)) { dropped++; return null; }
                f.Zone = (int)z; f.X = x; f.Y = y;
            }
            if (o.TryGetValue("by", out var bv) && bv is string by) f.By = CleanName(by);
            if (o.TryGetValue("s", out var sq) && Whole(sq, 0, long.MaxValue / 4, out var seq)) f.Seq = seq;
            return f;
        }

        static int OptInt(Dictionary<string, object> o, string k, int min, int max, ref bool ok)
        {
            if (!o.TryGetValue(k, out var v) || v == null) return 0;
            if (Whole(v, min, max, out var l)) return (int)l;
            ok = false; return 0;
        }

        public static Revealed ParseRevealed(int id, object node, ref int dropped)
        {
            var o = node as Dictionary<string, object>;
            if (o == null || !(o.TryGetValue("n", out var nv) && nv is string raw) || !o.TryGetValue("q", out var qv) || !Whole(qv, 0, 7, out var q)) { dropped++; return null; }
            var name = CleanName(raw);
            if (name.Length == 0) { dropped++; return null; }
            bool ok = true;
            var r = new Revealed { Id = id, Name = name, Quality = (int)q };
            r.ItemLevel = OptInt(o, "il", 0, 1000, ref ok); r.RequiredLevel = OptInt(o, "rl", 0, 100, ref ok);
            r.Class = OptInt(o, "cls", 0, 30, ref ok); r.SubClass = OptInt(o, "sub", 0, 30, ref ok); r.Slot = OptInt(o, "slot", 0, 30, ref ok);
            if (o.TryGetValue("st", out var sv))
            {
                var l = new List<int[]>();
                foreach (var e in ArrayValues(sv))
                {
                    var p = ArrayValues(e).ToList();
                    if (l.Count >= MaxStats || p.Count != 2 || !Whole(p[0], 0, 127, out var t) || !Whole(p[1], -32768, 32767, out var val)) { ok = false; continue; }
                    l.Add(new[] { (int)t, (int)val });
                }
                r.Stats = l.ToArray();
            }
            if (!ok) { dropped++; return null; }
            return r;
        }

        // ---------------------------------------------------------------- reading the Hub module

        // The `atlas.obs` v1 account module as validated shapes; null when the module is absent, too new or another version.
        public static Snapshot Of(HubModel model)
        {
            if (model == null || !model.AccountModules.TryGetValue(ModuleName, out var m) || m == null || m.TooNew || m.V != ModuleVersion || m.Data == null) return null;
            var s = FromTables(m.Data);
            s.Since = m.Data.Long("since");
            return s;
        }

        // the validation itself, on the module's data table
        public static Snapshot FromTables(Dictionary<string, object> d)
        {
            var s = new Snapshot();
            if (d == null) return s;
            int dropped = 0;
            Sources(d.Child("kills"), s.Kills, MaxKillSources, true, ref dropped);
            Sources(d.Child("objects"), s.Objects, MaxObjectSources, true, ref dropped);
            Sources(d.Child("fishing"), s.Fishing, MaxFishingZones, false, ref dropped, MaxMapId);
            var vo = d.Child("vendors");
            if (vo != null)
                foreach (var kv in vo)
                {
                    if (s.Vendors.Count >= MaxVendors || !Key(kv.Key, MaxId, out var id)) { dropped++; continue; }
                    var v = ParseVendor(kv.Value, ref dropped);
                    if (v != null) s.Vendors[id] = v;
                }
            var qo = d.Child("quests");
            if (qo != null)
                foreach (var kv in qo)
                {
                    if (s.Quests.Count >= MaxQuests || !Key(kv.Key, MaxId, out var id)) { dropped++; continue; }
                    var q = ParseQuest(kv.Value, ref dropped);
                    if (q != null) s.Quests[id] = q;
                }
            var so = d.Child("spawns");
            if (so != null)
                foreach (var kv in so)
                {
                    if (s.Spawns.Count >= MaxSpawnNpcs || !Key(kv.Key, MaxId, out var id)) { dropped++; continue; }
                    var pts = ParseSpots(kv.Value, MaxSpawnPoints, ref dropped);
                    if (pts.Count > 0) s.Spawns[id] = pts;
                }
            var fo = d.Child("finds");
            if (fo != null)
            {
                foreach (var e in ArrayValues(fo))
                {
                    if (s.Finds.Count >= MaxHubFinds) { dropped++; continue; }
                    var f = ParseFind(e, ref dropped);
                    if (f != null) { f.By = null; f.Seq = 0; s.Finds.Add(f); }
                }
            }
            s.Dropped = dropped;
            return s;
        }

        static void Sources(Dictionary<string, object> t, Dictionary<int, Counter> into, int max, bool zones, ref int dropped, int maxKey = MaxId)
        {
            if (t == null) return;
            foreach (var kv in t)
            {
                if (into.Count >= max || !Key(kv.Key, maxKey, out var id)) { dropped++; continue; }
                var c = ParseCounter(kv.Value, zones, MaxCount, ref dropped);
                if (c != null) into[id] = c;
            }
        }

        static HubModel lastModel; static Snapshot lastSnap; static readonly object gate = new object();

        // HubData.Read caches the parsed file; the conversion is cached per parse
        public static Snapshot Read(string root)
        {
            var model = HubData.Read(root);
            lock (gate)
            {
                if (ReferenceEquals(model, lastModel)) return lastSnap;
                lastModel = model; lastSnap = Of(model);
                return lastSnap;
            }
        }

        public static void ResetCache() { lock (gate) { lastModel = null; lastSnap = null; } HubData.ResetCache(); }

        // tests replace the reading (null = the Hub file of the WoW folder)
        public static Func<Snapshot> TestSource;

        // ---------------------------------------------------------------- what changed: signatures and batches

        static string Hash(string s)
        {
            using (var sha = SHA1.Create())
            {
                var h = sha.ComputeHash(Encoding.UTF8.GetBytes(s));
                var sb = new StringBuilder(16);
                for (int i = 0; i < 8; i++) sb.Append(h[i].ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        static string I(long v) { return v.ToString(CultureInfo.InvariantCulture); }
        static string D(double v) { return v.ToString("0.000", CultureInfo.InvariantCulture); }

        public static string Sig(Counter c)
        {
            var sb = new StringBuilder();
            sb.Append(I(c.N)).Append('|');
            foreach (var kv in c.Items.OrderBy(k => k.Key)) sb.Append(I(kv.Key)).Append(':').Append(I(kv.Value[0])).Append(':').Append(I(kv.Value[1])).Append(',');
            sb.Append('|');
            foreach (var kv in c.Zones.OrderBy(k => k.Key)) sb.Append(I(kv.Key)).Append(':').Append(I(kv.Value)).Append(',');
            return Hash(sb.ToString());
        }

        public static string Sig(Vendor v)
        {
            var sb = new StringBuilder();
            sb.Append(I(v.At)).Append('|').Append(I(v.Zone)).Append('|').Append(D(v.X)).Append('|').Append(D(v.Y)).Append('|');
            foreach (var kv in v.Items.OrderBy(k => k.Key)) sb.Append(I(kv.Key)).Append(':').Append(I(kv.Value[0])).Append(':').Append(I(kv.Value[1])).Append(':').Append(I(kv.Value[2])).Append(',');
            return Hash(sb.ToString());
        }

        public static string Sig(Quest q) { return Hash(I(q.At) + "|" + string.Join(",", q.Rewards) + "|" + string.Join(",", q.Choices)); }

        public static string Sig(List<Spot> pts)
        {
            return Hash(string.Join(";", pts.Select(p => I(p.Zone) + ":" + D(p.X) + ":" + D(p.Y)).OrderBy(x => x, StringComparer.Ordinal)));
        }

        public static string Sig(Revealed r)
        {
            return Hash(r.Name + "|" + r.Quality + "|" + r.ItemLevel + "|" + r.RequiredLevel + "|" + r.Class + "|" + r.SubClass + "|" + r.Slot + "|" + string.Join(",", r.Stats.Select(s => s[0] + ":" + s[1])));
        }

        public static string FindKey(Find f)
        {
            return "F" + I(f.At) + ":" + I(f.Item) + ":" + f.Src + ":" + I(f.Zone) + ":" + D(f.X) + ":" + D(f.Y);
        }

        internal static Dictionary<string, object> CounterNode(Counter c)
        {
            var o = new Dictionary<string, object> { ["n"] = c.N };
            if (c.Items.Count > 0)
            {
                var io = new Dictionary<string, object>();
                foreach (var kv in c.Items.OrderBy(k => k.Key)) io[I(kv.Key)] = new Dictionary<string, object> { ["n"] = kv.Value[0], ["c"] = kv.Value[1] };
                o["items"] = io;
            }
            if (c.Zones.Count > 0)
            {
                var zo = new Dictionary<string, object>();
                foreach (var kv in c.Zones.OrderBy(k => k.Key)) zo[I(kv.Key)] = kv.Value;
                o["z"] = zo;
            }
            return o;
        }

        internal static Dictionary<string, object> VendorNode(Vendor v)
        {
            var io = new Dictionary<string, object>();
            foreach (var kv in v.Items.OrderBy(k => k.Key)) io[I(kv.Key)] = new Dictionary<string, object> { ["p"] = kv.Value[0], ["s"] = kv.Value[1], ["l"] = kv.Value[2] };
            return new Dictionary<string, object> { ["at"] = v.At, ["z"] = v.Zone, ["x"] = v.X, ["y"] = v.Y, ["items"] = io };
        }

        internal static Dictionary<string, object> RevealedNode(Revealed r)
        {
            var o = new Dictionary<string, object> { ["n"] = r.Name, ["q"] = r.Quality, ["il"] = r.ItemLevel, ["rl"] = r.RequiredLevel, ["cls"] = r.Class, ["sub"] = r.SubClass, ["slot"] = r.Slot };
            if (r.Stats.Length > 0) o["st"] = r.Stats.Select(s => (object)new List<object> { s[0], s[1] }).ToList();
            return o;
        }

        public class Batch
        {
            public Dictionary<string, object> Frame;                                         // the atlas.share message
            public Dictionary<string, string> Sigs = new Dictionary<string, string>();       // sent key -> signature, marked as sent after the answer
            public int Entries;                                                              // entries in this batch
            public int Remaining;                                                            // changed entries that did not fit (next time)
        }

        class Cand { public string Section, Key, SentKey, Sig; public object Node; public int Cost; public long Order; }

        // The entries whose signature differs from what was uploaded before, as one atlas.share frame within the per-section counts and 56 KB.
        // Null when nothing changed. `revealed` = the item details to share (validated again here); `sent` = key -> signature of what the lodge already has.
        // `includeSeen` = the lodge takes seenNpcs / seenObjects / seenQuests (feature "atlas2"): the details AtlasObs.SeenSource holds go too, only ids not sent before (sent keys N / G / W + id).
        public static Batch MakeBatch(Snapshot s, IEnumerable<Revealed> revealed, IDictionary<string, string> sent, bool includeSeen = false)
        {
            var cands = new List<Cand>();
            void Add(string section, string key, string sentKey, string sig, object node, long order = 0)
            {
                if (sent.TryGetValue(sentKey, out var have) && have == sig) return;
                cands.Add(new Cand { Section = section, Key = key, SentKey = sentKey, Sig = sig, Node = node, Cost = Json.Write(node).Length + key.Length + 8, Order = order });
            }
            if (s != null)
            {
                foreach (var kv in s.Kills.OrderBy(k => k.Key)) Add("kills", I(kv.Key), "k" + kv.Key, Sig(kv.Value), CounterNode(kv.Value));
                foreach (var kv in s.Objects.OrderBy(k => k.Key)) Add("objects", I(kv.Key), "o" + kv.Key, Sig(kv.Value), CounterNode(kv.Value));
                foreach (var kv in s.Fishing.OrderBy(k => k.Key)) Add("fishing", I(kv.Key), "h" + kv.Key, Sig(kv.Value), CounterNode(kv.Value));
                foreach (var kv in s.Vendors.OrderBy(k => k.Key)) Add("vendors", I(kv.Key), "v" + kv.Key, Sig(kv.Value), VendorNode(kv.Value));
                foreach (var kv in s.Quests.OrderBy(k => k.Key))
                    Add("quests", I(kv.Key), "q" + kv.Key, Sig(kv.Value), new Dictionary<string, object>
                    {
                        ["at"] = kv.Value.At, ["rewards"] = kv.Value.Rewards.Select(x => (object)x).ToList(), ["choices"] = kv.Value.Choices.Select(x => (object)x).ToList(),
                    });
                foreach (var kv in s.Spawns.OrderBy(k => k.Key))
                    Add("spawns", I(kv.Key), "s" + kv.Key, Sig(kv.Value), kv.Value.Take(MaxPointsPerNpcPerShare).Select(p => (object)new Dictionary<string, object> { ["z"] = p.Zone, ["x"] = p.X, ["y"] = p.Y }).ToList());
                foreach (var f in s.Finds.OrderBy(x => x.At).ThenBy(x => FindKey(x), StringComparer.Ordinal))
                {
                    var o = new Dictionary<string, object> { ["i"] = f.Item, ["c"] = f.Count, ["src"] = f.Src, ["at"] = f.At };
                    if (f.Zone > 0) { o["z"] = f.Zone; o["x"] = f.X; o["y"] = f.Y; }
                    Add("finds", "", FindKey(f), "1", o);
                }
            }
            if (revealed != null)
            {
                int dropped = 0, seen = 0;
                foreach (var r in revealed)
                {
                    if (++seen > MaxRevealed) break;
                    // the source's items are validated again with the same rules as a shared node
                    var node = RevealedNode(r);
                    var again = r.Id >= 1 && r.Id <= MaxItemId ? ParseRevealed(r.Id, ToParsed(node), ref dropped) : null;
                    if (again != null) Add("items", I(again.Id), "i" + again.Id, Sig(again), RevealedNode(again));
                }
            }
            if (includeSeen)
            {
                var src = SeenSource;
                try
                {
                    foreach (var n in SeenNpcs(src)) Add("seenNpcs", I(n.Id), "N" + n.Id, Sig(n), SeenNpcNode(n));
                    foreach (var n in SeenObjects(src)) Add("seenObjects", I(n.Id), "G" + n.Id, Sig(n), SeenObjectNode(n));
                    foreach (var n in SeenQuests(src)) Add("seenQuests", I(n.Id), "W" + n.Id, Sig(n), SeenQuestNode(n));
                }
                catch (Exception) { }                                              // a source that fails shares nothing more this round
            }
            if (cands.Count == 0) return null;
            var limits = new Dictionary<string, int>
            {
                ["kills"] = MaxKillsPerShare, ["objects"] = MaxObjectsPerShare, ["fishing"] = MaxFishingPerShare, ["vendors"] = MaxVendorsPerShare, ["quests"] = MaxQuestsPerShare,
                ["spawns"] = MaxSpawnNpcsPerShare, ["finds"] = MaxFindsPerShare, ["items"] = MaxRevealedPerShare,
                ["seenNpcs"] = MaxSeenNpcsPerShare, ["seenObjects"] = MaxSeenObjectsPerShare, ["seenQuests"] = MaxSeenQuestsPerShare,
            };
            var b = new Batch();
            var frame = new Dictionary<string, object> { ["t"] = "atlas.share" };
            var objs = new Dictionary<string, Dictionary<string, object>>();
            var finds = new List<object>();
            var counts = new Dictionary<string, int>();
            int bytes = 64;
            foreach (var c in cands)
            {
                counts.TryGetValue(c.Section, out var n);
                if (n >= limits[c.Section] || bytes + c.Cost > MaxShareBytes) continue;
                counts[c.Section] = n + 1; bytes += c.Cost;
                if (c.Section == "finds") finds.Add(c.Node);
                else
                {
                    if (!objs.TryGetValue(c.Section, out var so)) objs[c.Section] = so = new Dictionary<string, object>();
                    so[c.Key] = c.Node;
                }
                b.Sigs[c.SentKey] = c.Sig;
                b.Entries++;
            }
            foreach (var kv in objs) frame[kv.Key] = kv.Value;
            if (finds.Count > 0) frame["finds"] = finds;
            b.Remaining = cands.Count - b.Entries;
            if (b.Entries == 0) return null;
            b.Frame = frame;
            return b;
        }

        // RevealedNode output as the parsers see JSON: numbers stay numbers (the node only holds int / long), so convert to double
        static object ToParsed(object v)
        {
            switch (v)
            {
                case int n: return (double)n;
                case long n: return (double)n;
                case Dictionary<string, object> d: return d.ToDictionary(kv => kv.Key, kv => ToParsed(kv.Value));
                case List<object> l: return l.Select(ToParsed).ToList();
                default: return v;
            }
        }
    }
}
