using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ElansAddonHub.Services
{
    // What Elan's Bags saved about one item stack group (summed per item and location)
    public class InvItem
    {
        public int Id;
        public int Count;
        public int Quality;
        public int Icon;       // FileDataID of the icon (Elan's Bags 0.2.0 and later), 0 when unknown
        public string Name;
    }

    // one bag slot as the game showed it (Elan's Bags 0.2.0 and later)
    public class InvSlot
    {
        public int Slot, Id, Count, Quality, Icon;
        public string Name;
    }

    // one bag / bank container: its size, the bag item itself and the filled slots
    public class InvContainer
    {
        public int BagId;                  // 0 backpack, 1-4 bags, 5 reagent bag, -1 main bank, 6-12 bank bags
        public int Size, Family;
        public int BagItemId, BagIcon, BagQuality;
        public string BagName;
        public Dictionary<int, InvSlot> Slots = new Dictionary<int, InvSlot>();
        public int Used => Slots.Count;
        public int Free => Math.Max(0, Size - Slots.Count);
        public string Title => BagId == 0 ? "Backpack" : BagId == -1 ? "Bank" : BagName ?? ("Bag " + BagId);
    }

    public class InvGuildTab
    {
        public int Index, Size;
        public string Name;
        public int Icon;
        public long At;
        public Dictionary<int, InvSlot> Slots = new Dictionary<int, InvSlot>();
    }

    public class InvGuild
    {
        public string Key, Name, Realm;
        public long Money, Updated;
        public int NumTabs;
        public List<InvGuildTab> Tabs = new List<InvGuildTab>();
    }

    // what Elan's Bags recorded about the client (build, which guild bank API exists)
    public class InvClient
    {
        public string Build;
        public bool Known;
        public bool GuildBankApi;      // GetGuildBankItemInfo exists
        public bool GuildBankUi;       // Blizzard_GuildBankUI is part of the client
    }

    public class InvData
    {
        public List<InvChar> Chars = new List<InvChar>();
        public List<InvGuild> Guilds = new List<InvGuild>();
        public InvClient Client = new InvClient();
    }

    public class InvChar
    {
        public string Key, Name, Realm, Class, Faction;
        public int Level;
        public long Money;
        public long Updated, BagsAt, BankAt;
        public bool Hidden;
        public string Guild;
        public List<InvContainer> BagsCont;      // null: saved by an older Elan's Bags (no layout)
        public List<InvContainer> BankCont;
        public List<InvItem> Bags = new List<InvItem>();
        public List<InvItem> Bank;               // null: the bank was never opened
        public List<InvItem> Equipped = new List<InvItem>();
        public string File;
    }

    // one item with everything the characters hold of it
    public class InvHolding
    {
        public InvChar Char;
        public int Bags, Bank, Equipped;
        public int Total => Bags + Bank + Equipped;
    }

    public class InvResult
    {
        public int Id, Quality, Total;
        public string Name;
        public List<InvHolding> Holdings = new List<InvHolding>();
    }

    // Reads WTF\Account\*\SavedVariables\ElansBags.lua of every client folder (read-only, FileShare.ReadWrite, never writes).
    public static class InventoryReader
    {
        public const string FileName = "ElansBags.lua";

        public static List<string> Files(string root)
        {
            var files = new List<string>();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return files;
            try
            {
                foreach (var flavor in Directory.GetDirectories(root, "_*_"))
                {
                    var accounts = Path.Combine(flavor, "WTF", "Account");
                    if (!Directory.Exists(accounts)) continue;
                    foreach (var acc in Directory.GetDirectories(accounts))
                    {
                        var f = Path.Combine(acc, "SavedVariables", FileName);
                        if (File.Exists(f)) files.Add(f);
                    }
                }
            }
            catch { }
            return files;
        }

        // the SavedVariables folders to watch (they may not contain the file yet)
        public static List<string> Dirs(string root)
        {
            var dirs = new List<string>();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return dirs;
            try
            {
                foreach (var flavor in Directory.GetDirectories(root, "_*_"))
                {
                    var accounts = Path.Combine(flavor, "WTF", "Account");
                    if (!Directory.Exists(accounts)) continue;
                    foreach (var acc in Directory.GetDirectories(accounts))
                    {
                        var d = Path.Combine(acc, "SavedVariables");
                        if (Directory.Exists(d)) dirs.Add(d);
                    }
                }
            }
            catch { }
            return dirs;
        }

        static string ReadShared(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs, new UTF8Encoding(false), true))
                return sr.ReadToEnd();
        }

        // all characters of all files; the same character in two files (two clients) keeps the newer record
        public static List<InvChar> Read(string root) => ReadData(root).Chars;

        // "...\_classic_beta_\WTF\Account\X\SavedVariables\ElansBags.lua" -> "_classic_beta_"
        public static string FlavorOf(string file)
        {
            try
            {
                var m = Regex.Match(file ?? "", @"[\\/](_[a-z_]+_)[\\/]WTF[\\/]", RegexOptions.IgnoreCase);
                return m.Success ? m.Groups[1].Value : null;
            }
            catch { return null; }
        }

        public static List<InvChar> ParseText(string text, string file = null) => ParseAll(text, file).Chars;

        // characters, guild banks and the client facts of all files (the newer record wins per character / guild)
        public static InvData ReadData(string root)
        {
            var best = new Dictionary<string, InvChar>(StringComparer.OrdinalIgnoreCase);
            var guilds = new Dictionary<string, InvGuild>(StringComparer.OrdinalIgnoreCase);
            var data = new InvData();
            foreach (var f in Files(root))
            {
                try
                {
                    var one = ParseAll(ReadShared(f), f);
                    foreach (var c in one.Chars)
                        if (!best.TryGetValue(c.Key, out var old) || c.Updated >= old.Updated) best[c.Key] = c;
                    foreach (var g in one.Guilds)
                        if (!guilds.TryGetValue(g.Key, out var og) || g.Updated >= og.Updated) guilds[g.Key] = g;
                    if (one.Client.Known && (!data.Client.Known || one.Client.GuildBankApi)) data.Client = one.Client;
                }
                catch (Exception e) { Util.Log("inventory read failed: " + f + ": " + e.Message); }
            }
            data.Chars = best.Values.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            data.Guilds = guilds.Values.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            return data;
        }

        public static InvData ParseAll(string text, string file = null)
        {
            var data = new InvData();
            var list = data.Chars;
            var globals = LuaData.ReadGlobals(text);
            if (!globals.TryGetValue("ElansBagsDB", out var dbObj) || !(dbObj is Dictionary<string, object> db)) return data;
            if (db.TryGetValue("client", out var clObj) && clObj is Dictionary<string, object> cl && cl.Count > 0)
            {
                data.Client.Known = true;
                data.Client.Build = Str(cl, "build");
                if (cl.TryGetValue("guildBank", out var gbObj) && gbObj is Dictionary<string, object> gbApi)
                {
                    bool Has(string k) => gbApi.TryGetValue(k, out var v) && v is bool b && b;
                    data.Client.GuildBankApi = Has("GetGuildBankItemInfo") || Has("C_GuildBank");
                    data.Client.GuildBankUi = Has("GuildBankUIAddon") || Has("GuildBankFrame");
                }
            }
            if (db.TryGetValue("guilds", out var gObj) && gObj is Dictionary<string, object> guilds)
                foreach (var kv in guilds)
                    if (kv.Value is Dictionary<string, object> gt) data.Guilds.Add(ParseGuild(kv.Key, gt));
            if (!db.TryGetValue("chars", out var charsObj) || !(charsObj is Dictionary<string, object> chars)) return data;
            foreach (var kv in chars)
            {
                if (!(kv.Value is Dictionary<string, object> t)) continue;
                var c = new InvChar
                {
                    Key = kv.Key,
                    Name = Str(t, "name") ?? kv.Key,
                    Realm = Str(t, "realm"),
                    Class = Str(t, "class"),
                    Faction = Str(t, "faction"),
                    Level = (int)Num(t, "level"),
                    Money = (long)Num(t, "money"),
                    Updated = (long)Num(t, "updated"),
                    BagsAt = (long)Num(t, "bagsAt"),
                    BankAt = (long)Num(t, "bankAt"),
                    Hidden = t.TryGetValue("hidden", out var h) && h is bool hb && hb,
                    File = file,
                };
                c.Guild = Str(t, "guild");
                c.Bags = Items(t, "bags") ?? new List<InvItem>();
                c.Bank = Items(t, "bank");
                c.Equipped = Items(t, "equipped") ?? new List<InvItem>();
                var names = new Dictionary<int, InvItem>();
                foreach (var it in c.Bags.Concat(c.Bank ?? new List<InvItem>()).Concat(c.Equipped)) if (!names.ContainsKey(it.Id)) names[it.Id] = it;
                c.BagsCont = Containers(t, "bagsCont", names);
                c.BankCont = Containers(t, "bankCont", names);
                // Elan's Bags before 0.2.3 saved the reagent bag (container 5) as a bank bag; a container that is
                // one of your bags is never also a bank bag
                if (c.BagsCont != null && c.BankCont != null)
                    c.BankCont.RemoveAll(b => b.BagId > 0 && c.BagsCont.Any(x => x.BagId == b.BagId));
                list.Add(c);
            }
            return data;
        }

        // {"0": {n=, k=, id=, ic=, l=, q=, s={[slot]={i,c,q,ic}}}, ...} in bag order; null when the character has no layout saved
        static List<InvContainer> Containers(Dictionary<string, object> t, string key, Dictionary<int, InvItem> names)
        {
            if (!t.TryGetValue(key, out var o) || !(o is Dictionary<string, object> conts)) return null;
            var res = new List<InvContainer>();
            foreach (var kv in conts)
            {
                if (!(kv.Value is Dictionary<string, object> ct) || !int.TryParse(kv.Key, out var bag)) continue;
                var c = new InvContainer { BagId = bag, Size = (int)Num(ct, "n"), Family = (int)Num(ct, "k"), BagItemId = (int)Num(ct, "id"), BagIcon = (int)Num(ct, "ic"), BagQuality = ct.ContainsKey("q") ? (int)Num(ct, "q") : 1 };
                var link = Str(ct, "l");
                if (link != null) { var m = NameInLink.Match(link); if (m.Success) c.BagName = m.Groups[1].Value; }
                if (ct.TryGetValue("s", out var so) && so is Dictionary<string, object> slots) ReadSlots(slots, c.Slots, names);
                if (c.Size <= 0) c.Size = c.Slots.Count == 0 ? 0 : c.Slots.Keys.Max();
                if (c.Size > 0) res.Add(c);
            }
            // backpack, bags 1-4 (bank: main bank, then bank bags 5-11)
            return res.OrderBy(c => c.BagId).ToList();
        }

        static void ReadSlots(Dictionary<string, object> slots, Dictionary<int, InvSlot> into, Dictionary<int, InvItem> names)
        {
            foreach (var sv in slots)
            {
                if (!(sv.Value is Dictionary<string, object> st) || !int.TryParse(sv.Key, out var slot)) continue;
                var id = (int)Num(st, "i");
                if (id <= 0) continue;
                string name = null;
                var link = Str(st, "l");
                if (link != null) { var m = NameInLink.Match(link); if (m.Success) name = m.Groups[1].Value; }
                int q = st.ContainsKey("q") ? (int)Num(st, "q") : -1;
                int icon = (int)Num(st, "ic");
                if (names != null && names.TryGetValue(id, out var known))
                {
                    name = name ?? known.Name;
                    if (q < 0) q = known.Quality;
                    if (icon <= 0) icon = known.Icon;
                }
                into[slot] = new InvSlot { Slot = slot, Id = id, Count = Math.Max(1, (int)Num(st, "c")), Quality = q < 0 ? 1 : q, Icon = icon, Name = name ?? ("Item #" + id) };
            }
        }

        static InvGuild ParseGuild(string key, Dictionary<string, object> t)
        {
            var g = new InvGuild { Key = key, Name = Str(t, "name") ?? key, Realm = Str(t, "realm"), Money = (long)Num(t, "money"), Updated = (long)Num(t, "updated"), NumTabs = (int)Num(t, "numTabs") };
            if (t.TryGetValue("tabs", out var to) && to is Dictionary<string, object> tabs)
                foreach (var kv in tabs)
                {
                    if (!(kv.Value is Dictionary<string, object> tt) || !int.TryParse(kv.Key, out var idx)) continue;
                    var tab = new InvGuildTab { Index = idx, Name = Str(tt, "name"), Icon = (int)Num(tt, "icon"), At = (long)Num(tt, "at"), Size = (int)Num(tt, "n") };
                    if (tt.TryGetValue("s", out var so) && so is Dictionary<string, object> slots) ReadSlots(slots, tab.Slots, null);
                    if (tab.Size <= 0) tab.Size = 98;
                    g.Tabs.Add(tab);
                }
            g.Tabs = g.Tabs.OrderBy(x => x.Index).ToList();
            return g;
        }

        static string Str(Dictionary<string, object> t, string k) => t.TryGetValue(k, out var v) && v != null ? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) : null;
        static double Num(Dictionary<string, object> t, string k) => t.TryGetValue(k, out var v) && v is double d ? d : 0;

        static readonly Regex NameInLink = new Regex(@"\[(.+?)\]", RegexOptions.Compiled);
        static readonly Regex ColorInLink = new Regex(@"\|c[0-9a-fA-F]{2}([0-9a-fA-F]{6})", RegexOptions.Compiled);
        static readonly Dictionary<string, int> ColorQuality = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["9d9d9d"] = 0, ["ffffff"] = 1, ["1eff00"] = 2, ["0070dd"] = 3, ["a335ee"] = 4, ["ff8000"] = 5, ["e6cc80"] = 6, ["00ccff"] = 7,
        };

        static List<InvItem> Items(Dictionary<string, object> t, string key)
        {
            if (!t.TryGetValue(key, out var o) || !(o is Dictionary<string, object> list)) return null;
            var res = new List<InvItem>();
            foreach (var e in list.Values)
            {
                if (!(e is Dictionary<string, object> it)) continue;
                var id = (int)Num(it, "i");
                if (id <= 0) continue;
                var link = Str(it, "l");
                string name = null;
                int q = it.ContainsKey("q") ? (int)Num(it, "q") : -1;
                if (link != null)
                {
                    var m = NameInLink.Match(link);
                    if (m.Success) name = m.Groups[1].Value;
                    if (q < 0)
                    {
                        var cm = ColorInLink.Match(link);
                        if (cm.Success && ColorQuality.TryGetValue(cm.Groups[1].Value, out var cq)) q = cq;
                    }
                }
                res.Add(new InvItem { Id = id, Count = Math.Max(1, (int)Num(it, "c")), Quality = q < 0 ? 1 : q, Icon = (int)Num(it, "ic"), Name = name ?? ("Item #" + id) });
            }
            return res;
        }

        static readonly string[] QualityWords = { "poor", "common", "uncommon", "rare", "epic", "legendary", "artifact", "heirloom" };

        // Search across bags, bank and equipped of every (non hidden) character, grouped per item.
        // Every word must occur in the item name or quality word (or be the item id). `only` limits it to one character.
        public static List<InvResult> Search(IEnumerable<InvChar> chars, string query, InvChar only = null)
        {
            var terms = (query ?? "").ToLowerInvariant().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var groups = new Dictionary<int, InvResult>();
            foreach (var c in chars)
            {
                if (c.Hidden || (only != null && !ReferenceEquals(only, c) && only.Key != c.Key)) continue;
                void Add(List<InvItem> items, int where)
                {
                    if (items == null) return;
                    foreach (var it in items)
                    {
                        if (!groups.TryGetValue(it.Id, out var r)) { r = new InvResult { Id = it.Id, Name = it.Name, Quality = it.Quality }; groups[it.Id] = r; }
                        var h = r.Holdings.FirstOrDefault(x => x.Char == c);
                        if (h == null) { h = new InvHolding { Char = c }; r.Holdings.Add(h); }
                        if (where == 0) h.Bags += it.Count; else if (where == 1) h.Bank += it.Count; else h.Equipped += it.Count;
                        r.Total += it.Count;
                    }
                }
                Add(c.Bags, 0); Add(c.Bank, 1); Add(c.Equipped, 2);
            }
            IEnumerable<InvResult> res = groups.Values;
            if (terms.Length > 0)
                res = res.Where(r =>
                {
                    var hay = (r.Name + " " + (r.Quality >= 0 && r.Quality < QualityWords.Length ? QualityWords[r.Quality] : "") + " " + r.Id).ToLowerInvariant();
                    return terms.All(t => hay.Contains(t));
                });
            return res.OrderByDescending(r => r.Quality).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        public static string Ago(long unix, DateTime? now = null)
        {
            if (unix <= 0) return "never";
            var span = (now ?? DateTime.UtcNow) - DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
            if (span.TotalSeconds < 0) span = TimeSpan.Zero;
            if (span.TotalMinutes < 1) return "just now";
            if (span.TotalMinutes < 60) return Plural((int)span.TotalMinutes, "minute") + " ago";
            if (span.TotalHours < 24) return Plural((int)span.TotalHours, "hour") + " ago";
            if (span.TotalDays < 60) return Plural((int)span.TotalDays, "day") + " ago";
            return Plural((int)(span.TotalDays / 30), "month") + " ago";
        }
        static string Plural(int n, string unit) => n + " " + unit + (n == 1 ? "" : "s");

        public static string Money(long copper)
        {
            long g = copper / 10000, s = copper / 100 % 100, c = copper % 100;
            return g > 0 ? $"{g:N0}g {s}s {c}c" : s > 0 ? $"{s}s {c}c" : $"{c}c";
        }
    }
}
