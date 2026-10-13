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

    // a bag that only takes certain items (reagent bag, quiver, soul pouch, profession bags)
    public class InvSpecial
    {
        public string Key, Name, Short, Only, What, Hex;
        public string Explain => Name + ": only " + What + " fit here.";
    }

    public static class BagKinds
    {
        // bag family flags (the same table Elan's Bags uses); colours: reagent teal, ammo brown, soul purple, professions green
        const string Teal = "#33C7B8", Brown = "#C28547", Purple = "#AD6BEB", Green = "#66D15C", Grey = "#A6ADB3";
        static readonly InvSpecial[] Families =
        {
            Mk(1, "arrows", "Quiver", "Quiver", "Arrows only", "arrows", Brown),
            Mk(2, "bullets", "Ammo pouch", "Ammo", "Bullets only", "bullets", Brown),
            Mk(4, "shards", "Soul pouch", "Soul", "Soul shards only", "soul shards", Purple),
            Mk(8, "leather", "Leatherworking bag", "Leather", "Leather only", "leatherworking items", Green),
            Mk(16, "inscr", "Inscription bag", "Inscription", "Inscription only", "inscription items", Green),
            Mk(32, "herbs", "Herb bag", "Herbs", "Herbs only", "herbs", Green),
            Mk(64, "enchant", "Enchanting bag", "Enchanting", "Enchanting only", "enchanting items", Green),
            Mk(128, "engineer", "Engineering bag", "Engineering", "Engineering only", "engineering items", Green),
            Mk(256, "keys", "Key ring", "Keys", "Keys only", "keys", Green),
            Mk(512, "gems", "Gem bag", "Gems", "Gems only", "gems", Green),
            Mk(1024, "mining", "Mining bag", "Mining", "Ore only", "ore and bars", Green),
        };
        static readonly int[] Bits = { 1, 2, 4, 8, 16, 32, 64, 128, 256, 512, 1024 };
        static InvSpecial Mk(int bit, string key, string name, string sh, string only, string what, string hex) =>
            new InvSpecial { Key = key, Name = name, Short = sh, Only = only, What = what, Hex = hex };
        public static readonly InvSpecial Reagent = Mk(0, "reagent", "Reagent bag", "Reagents", "Reagents only", "reagents", Teal);
        static readonly InvSpecial Other = Mk(0, "special", "Special bag", "Special", "Special items only", "special items", Grey);

        public static InvSpecial FromFamily(int family)
        {
            if (family <= 0) return null;
            for (int i = 0; i < Bits.Length; i++) if ((family & Bits[i]) != 0) return Families[i];
            return Other;
        }

        // reagentBag: the client's reagent bag container index (ElansBagsDB.client.reagentBag), 0 = not saved (older Elan's Bags):
        // then a bag in slot 5 whose item is a "Reagent Bag" counts. The bank has no reagent bag.
        public static void Classify(List<InvContainer> conts, int reagentBag, bool bank)
        {
            if (conts == null) return;
            foreach (var c in conts)
            {
                c.Special = null;
                if (!bank && c.BagId > 0 && (c.BagId == reagentBag || (reagentBag <= 0 && c.BagId == 5 && (c.BagName ?? "").IndexOf("Reagent Bag", StringComparison.OrdinalIgnoreCase) >= 0)))
                    c.Special = Reagent;
                else if (c.Family > 0) c.Special = FromFamily(c.Family);
            }
        }
    }

    // one bag / bank container: its size, the bag item itself and the filled slots
    public class InvContainer
    {
        public int BagId;                  // 0 backpack, 1-4 bags, 5 reagent bag, -1 main bank, 6-12 bank bags
        public int Size, Family;
        public InvSpecial Special;         // null: takes anything
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

    // name + icon of a guild bank tab, known for every tab (Elan's Bags 0.4.0) even when its contents were never seen
    public class InvGuildTabMeta
    {
        public int Index, Icon;
        public string Name;
        public bool CanView = true;
    }

    public class InvGuild
    {
        public string Key, Name, Realm;
        public long Money, Updated;
        public int NumTabs;
        public List<InvGuildTab> Tabs = new List<InvGuildTab>();       // tabs whose contents were seen
        public List<InvGuildTabMeta> Meta = new List<InvGuildTabMeta>();
    }

    // what Elan's Bags recorded about the client (build, which guild bank API exists)
    public class InvClient
    {
        public string Build;
        public bool Known;
        public bool GuildBankApi;      // GetGuildBankItemInfo exists
        public bool GuildBankUi;       // Blizzard_GuildBankUI is part of the client
        public int ReagentBag;         // container index of the reagent bag slot (Elan's Bags 0.3.0 and later), 0 = not saved
    }

    public class InvData
    {
        public List<InvChar> Chars = new List<InvChar>();
        public List<InvGuild> Guilds = new List<InvGuild>();
        public InvClient Client = new InvClient();
        public HubModel Hub;                     // set by CharacterMerge.Merge: the Hub's data (module states, account modules)
    }

    public class InvChar
    {
        public string Key, Name, Realm, Class, Faction;
        public string RaceFile;                  // "Orc", "NightElf", "Scourge"...: Elan's Bags does not save it yet; filled from the Outpost's ElansHub.lua (see ReadLooks)
        public int Level;
        public int Sex;                          // 2 male, 3 female (UnitSex), 0 = unknown
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
        // Characters page (see CharacterMerge): where the bags / bank came from, and the Hub's record of this character when it has one
        public HubChar Hub;
        public HubCore Core;                     // Hub core v1, null when the Hub has none (or it is newer than this app understands)
        public string BagsFrom, BankFrom;        // CharacterMerge.FromHub / FromBags, null when there is nothing
        public InvChar Clone() => (InvChar)MemberwiseClone();
    }

    // one item with everything the characters hold of it
    public class InvHolding
    {
        public InvChar Char;               // null for a guild bank tab
        public string GuildLabel;          // "Guild: <guild> - Tab <n>" for a guild bank tab
        public int Bags, Bank, Equipped, GuildBank;
        public long GuildSeen;
        public int Total => Bags + Bank + Equipped + GuildBank;
    }

    // how many of one item a character holds, per place
    public class InvCount
    {
        public InvChar Char;
        public int Bags, Bank, Equipped;
        public int Total => Bags + Bank + Equipped;
        // "Bags 14 · Bank 20 · Total 34" (just "Bags 14" when it is in one place only)
        public string Detail
        {
            get
            {
                var parts = new List<string>();
                if (Bags > 0) parts.Add("Bags " + Bags.ToString("N0", ElansAddonHub.Lodge.CalendarRules.English));
                if (Bank > 0) parts.Add("Bank " + Bank.ToString("N0", ElansAddonHub.Lodge.CalendarRules.English));
                if (Equipped > 0) parts.Add("Worn " + Equipped.ToString("N0", ElansAddonHub.Lodge.CalendarRules.English));
                if (parts.Count > 1) parts.Add("Total " + Total.ToString("N0", ElansAddonHub.Lodge.CalendarRules.English));
                return string.Join(" · ", parts);
            }
        }
    }

    public class InvGuildCount { public InvGuild Guild; public int Count; public long Seen; }

    public class InvItemTotals
    {
        public int Id;
        public List<InvCount> Chars = new List<InvCount>();
        public List<InvGuildCount> Guilds = new List<InvGuildCount>();
    }

    public class ItemTip
    {
        public int Id;
        public InvCount Me;
        public List<InvCount> Others = new List<InvCount>();
        public List<InvGuildCount> Guilds = new List<InvGuildCount>();
        public int Grand => (Me?.Total ?? 0) + Others.Sum(o => o.Total) + Guilds.Sum(g => g.Count);
        public int Sources => (Me != null ? 1 : 0) + Others.Count + Guilds.Count;
        public bool ShowGrand => Sources > 1;
        // plain text of what the tooltip shows (for the self-test)
        public List<string> Lines()
        {
            var l = new List<string>();
            if (Me != null) l.Add("This: " + Me.Char.Name + " " + Me.Detail);
            foreach (var o in Others) l.Add("Other: " + o.Char.Name + " " + o.Detail);
            foreach (var g in Guilds) l.Add("Guild: " + g.Guild.Name + " " + g.Count.ToString("N0", ElansAddonHub.Lodge.CalendarRules.English));
            if (ShowGrand) l.Add("All: " + Grand.ToString("N0", ElansAddonHub.Lodge.CalendarRules.English));
            return l;
        }
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

        // the Bags files / SavedVariables folders of every client folder: SavedVarsFiles is the one walk (these two stay for the Characters page)
        public static List<string> Files(string root) => SavedVarsFiles.For(root, FileName);
        public static List<string> Dirs(string root) => SavedVarsFiles.Dirs(root);

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
            foreach (var f in SavedVarsFiles.For(root, FileName))
            {
                try
                {
                    var one = ParseAllFrom(SavedVarsCache.Get(f), f);
                    foreach (var c in one.Chars)
                        if (!best.TryGetValue(c.Key, out var old) || c.Updated >= old.Updated) best[c.Key] = c;
                    foreach (var g in one.Guilds)
                        if (!guilds.TryGetValue(g.Key, out var og) || g.Updated >= og.Updated) guilds[g.Key] = g;
                    if (one.Client.Known && (!data.Client.Known || one.Client.GuildBankApi)) data.Client = one.Client;
                }
                catch (Exception e) { Util.Log("inventory read failed: " + f + ": " + e.Message); }
            }
            data.Chars = best.Values.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            ApplyLooks(data.Chars, ReadLooks(root));
            data.Guilds = guilds.Values.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            return data;
        }

        // race and sex per character, from the Outpost companion's SavedVariables (ElansHub.lua, "chars": raceFile / sex / name / realm).
        // Read-only. Elan's Bags itself saves class and faction only. Key: "name|realm" (lower case).
        public class Look { public string RaceFile; public int Sex; }

        public static Dictionary<string, Look> ReadLooks(string root)
        {
            var res = new Dictionary<string, Look>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in SavedVarsFiles.For(root, HubData.FileName))
            {
                try { ParseLooksFrom(SavedVarsCache.Get(f), res); }
                catch (Exception e) { Util.Log("inventory looks failed: " + f + ": " + e.Message); }
            }
            return res;
        }

        public static void ParseLooks(string text, Dictionary<string, Look> into) => ParseLooksFrom(LuaData.ReadGlobals(text), into);

        public static void ParseLooksFrom(Dictionary<string, object> globals, Dictionary<string, Look> into)
        {
            var db = globals != null && globals.TryGetValue("ElansHubDB", out var v) ? v as Dictionary<string, object> : null;
            var chars = db?.Child("chars");
            if (chars == null) return;
            foreach (var kv in chars)
            {
                var c = kv.Value as Dictionary<string, object>;
                if (c == null) continue;
                var name = c.Str("name");
                var race = c.Str("raceFile");
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(race)) continue;
                var look = new Look { RaceFile = race, Sex = c.Int("sex") };
                into[LookKey(name, c.Str("realm"))] = look;
                into[LookKey(name, null)] = look;   // name only, when the realm is spelled differently
            }
        }

        static string LookKey(string name, string realm) => (name ?? "").ToLowerInvariant() + "|" + (realm ?? "").ToLowerInvariant();

        public static void ApplyLooks(IEnumerable<InvChar> chars, Dictionary<string, Look> looks)
        {
            if (looks == null || looks.Count == 0) return;
            foreach (var c in chars)
            {
                if (!string.IsNullOrEmpty(c.RaceFile) && c.Sex >= 2) continue;
                if (looks.TryGetValue(LookKey(c.Name, c.Realm), out var l) || looks.TryGetValue(LookKey(c.Name, null), out l))
                {
                    if (string.IsNullOrEmpty(c.RaceFile)) c.RaceFile = l.RaceFile;
                    if (c.Sex < 2) c.Sex = l.Sex;
                }
            }
        }

        public static InvData ParseAll(string text, string file = null) => ParseAllFrom(LuaData.ReadGlobals(text), file);

        // the same from an already parsed file (SavedVarsCache); the tables are shared, read only
        public static InvData ParseAllFrom(Dictionary<string, object> globals, string file = null)
        {
            var data = new InvData();
            var list = data.Chars;
            if (globals == null || !globals.TryGetValue("ElansBagsDB", out var dbObj) || !(dbObj is Dictionary<string, object> db)) return data;
            if (db.TryGetValue("client", out var clObj) && clObj is Dictionary<string, object> cl && cl.Count > 0)
            {
                data.Client.Known = true;
                data.Client.Build = cl.Text("build");
                data.Client.ReagentBag = cl.Int("reagentBag");
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
                    Name = t.Text("name") ?? kv.Key,
                    Realm = t.Text("realm"),
                    Class = t.Text("class"),
                    Faction = t.Text("faction"),
                    RaceFile = t.Text("raceFile"),     // a later Elan's Bags may save these two
                    Sex = t.Int("sex"),
                    Level = t.Int("level"),
                    Money = t.Long("money"),
                    Updated = t.Long("updated"),
                    BagsAt = t.Long("bagsAt"),
                    BankAt = t.Long("bankAt"),
                    Hidden = t.TryGetValue("hidden", out var h) && h is bool hb && hb,
                    File = file,
                };
                c.Guild = t.Text("guild");
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
                BagKinds.Classify(c.BagsCont, data.Client.ReagentBag, false);
                BagKinds.Classify(c.BankCont, 0, true);
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
                var c = new InvContainer { BagId = bag, Size = ct.Int("n"), Family = ct.Int("k"), BagItemId = ct.Int("id"), BagIcon = ct.Int("ic"), BagQuality = ct.ContainsKey("q") ? ct.Int("q") : 1 };
                var link = ct.Text("l");
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
                var id = st.Int("i");
                if (id <= 0) continue;
                string name = null;
                var link = st.Text("l");
                if (link != null) { var m = NameInLink.Match(link); if (m.Success) name = m.Groups[1].Value; }
                int q = st.ContainsKey("q") ? st.Int("q") : -1;
                int icon = st.Int("ic");
                if (names != null && names.TryGetValue(id, out var known))
                {
                    name = name ?? known.Name;
                    if (q < 0) q = known.Quality;
                    if (icon <= 0) icon = known.Icon;
                }
                into[slot] = new InvSlot { Slot = slot, Id = id, Count = Math.Max(1, st.Int("c")), Quality = q < 0 ? 1 : q, Icon = icon, Name = name ?? ("Item #" + id) };
            }
        }

        static InvGuild ParseGuild(string key, Dictionary<string, object> t)
        {
            var g = new InvGuild { Key = key, Name = t.Text("name") ?? key, Realm = t.Text("realm"), Money = t.Long("money"), Updated = t.Long("updated"), NumTabs = t.Int("numTabs") };
            if (t.TryGetValue("tabs", out var to) && to is Dictionary<string, object> tabs)
                foreach (var kv in tabs)
                {
                    if (!(kv.Value is Dictionary<string, object> tt) || !int.TryParse(kv.Key, out var idx)) continue;
                    var tab = new InvGuildTab { Index = idx, Name = tt.Text("name"), Icon = tt.Int("icon"), At = tt.Long("at"), Size = tt.Int("n") };
                    if (tt.TryGetValue("s", out var so) && so is Dictionary<string, object> slots) ReadSlots(slots, tab.Slots, null);
                    if (tab.Size <= 0) tab.Size = 98;
                    g.Tabs.Add(tab);
                }
            g.Tabs = g.Tabs.OrderBy(x => x.Index).ToList();
            if (t.TryGetValue("meta", out var mo) && mo is Dictionary<string, object> metas)
                foreach (var kv in metas)
                {
                    if (!(kv.Value is Dictionary<string, object> mt) || !int.TryParse(kv.Key, out var idx)) continue;
                    g.Meta.Add(new InvGuildTabMeta { Index = idx, Name = mt.Text("name"), Icon = mt.Int("icon"), CanView = !(mt.TryGetValue("view", out var vv) && vv is bool vb && !vb) });
                }
            g.Meta = g.Meta.OrderBy(x => x.Index).ToList();
            return g;
        }

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
                var id = it.Int("i");
                if (id <= 0) continue;
                var link = it.Text("l");
                string name = null;
                int q = it.ContainsKey("q") ? it.Int("q") : -1;
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
                res.Add(new InvItem { Id = id, Count = Math.Max(1, it.Int("c")), Quality = q < 0 ? 1 : q, Icon = it.Int("ic"), Name = name ?? ("Item #" + id) });
            }
            return res;
        }

        static readonly string[] QualityWords = { "poor", "common", "uncommon", "rare", "epic", "legendary", "artifact", "heirloom" };

        // Search across bags, bank and equipped of every (non hidden) character, grouped per item.
        // Every word must occur in the item name or quality word (or be the item id). `only` limits it to one character.
        // `guilds`: guild banks to search too (labelled "Guild: <guild> - Tab <n>"); the caller decides which ones belong to the scope.
        public static List<InvResult> Search(IEnumerable<InvChar> chars, string query, InvChar only = null, IEnumerable<InvGuild> guilds = null)
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
            if (guilds != null)
                foreach (var g in guilds)
                    foreach (var tab in g.Tabs)
                    {
                        var label = "Guild: " + g.Name + " - Tab " + tab.Index;
                        foreach (var sl in tab.Slots.Values)
                        {
                            if (!groups.TryGetValue(sl.Id, out var r)) { r = new InvResult { Id = sl.Id, Name = sl.Name, Quality = sl.Quality }; groups[sl.Id] = r; }
                            var h = r.Holdings.FirstOrDefault(x => x.GuildLabel == label);
                            if (h == null) { h = new InvHolding { GuildLabel = label, GuildSeen = tab.At }; r.Holdings.Add(h); }
                            h.GuildBank += sl.Count;
                            r.Total += sl.Count;
                        }
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

        // ---- per item totals (built once per data load; the tooltips only look things up)
        // Counts come from the saved bag layout when there is one (that includes the reagent bag and the bank bags), else from the summed lists.
        public static Dictionary<int, InvItemTotals> BuildIndex(IEnumerable<InvChar> chars, IEnumerable<InvGuild> guilds)
        {
            var idx = new Dictionary<int, InvItemTotals>();
            InvItemTotals Of(int id) { if (!idx.TryGetValue(id, out var t)) idx[id] = t = new InvItemTotals { Id = id }; return t; }
            foreach (var c in chars)
            {
                if (c.Hidden) continue;
                var per = new Dictionary<int, InvCount>();
                void Add(int id, int n, int where)
                {
                    if (id <= 0) return;
                    if (!per.TryGetValue(id, out var k)) per[id] = k = new InvCount { Char = c };
                    if (where == 0) k.Bags += n; else if (where == 1) k.Bank += n; else k.Equipped += n;
                }
                void FromLayout(List<InvContainer> conts, List<InvItem> list, int where)
                {
                    if (conts != null && conts.Count > 0) { foreach (var ct in conts) foreach (var sl in ct.Slots.Values) Add(sl.Id, sl.Count, where); }
                    else if (list != null) foreach (var it in list) Add(it.Id, it.Count, where);
                }
                FromLayout(c.BagsCont, c.Bags, 0);
                FromLayout(c.BankCont, c.Bank, 1);
                foreach (var it in c.Equipped ?? new List<InvItem>()) Add(it.Id, it.Count, 2);
                foreach (var kv in per) Of(kv.Key).Chars.Add(kv.Value);
            }
            if (guilds != null)
                foreach (var g in guilds)
                {
                    var per = new Dictionary<int, InvGuildCount>();
                    foreach (var tab in g.Tabs)
                        foreach (var sl in tab.Slots.Values)
                        {
                            if (sl.Id <= 0) continue;
                            if (!per.TryGetValue(sl.Id, out var k)) per[sl.Id] = k = new InvGuildCount { Guild = g, Seen = tab.At };
                            k.Count += sl.Count;
                            if (tab.At > k.Seen) k.Seen = tab.At;
                        }
                    foreach (var kv in per) Of(kv.Key).Guilds.Add(kv.Value);
                }
            return idx;
        }

        // what the item tooltip says about item `id` seen on `me`: me, the other characters, the guild banks, everything together
        public static ItemTip TipFor(Dictionary<int, InvItemTotals> index, int id, InvChar me)
        {
            var tip = new ItemTip { Id = id };
            if (index == null || !index.TryGetValue(id, out var t)) return tip;
            tip.Me = me == null ? null : t.Chars.FirstOrDefault(k => ReferenceEquals(k.Char, me) || k.Char.Key == me.Key);
            tip.Others = t.Chars.Where(k => k != tip.Me).OrderByDescending(k => k.Total).ThenBy(k => k.Char.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            tip.Guilds = t.Guilds.OrderByDescending(g => g.Count).ThenBy(g => g.Guild.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            return tip;
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
