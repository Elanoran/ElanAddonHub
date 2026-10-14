using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ElansAddonHub.Services
{
    public enum ItemRefKind { Bracket, Wowhead, ChatLink, Atlas }

    // one item mentioned in a chat message
    public class ItemRef
    {
        public ItemRefKind Kind;
        public int Start, Length;
        public int Id;               // 0 = not known (a [Name] that is not unambiguous in the data we have)
        public string Name;          // as written / from the link / from the URL's slug; null when nothing readable
        public int Quality = -1;     // from a pasted chat link's colour code, else -1 (the item data may know better)
        public string AtlasId;       // Atlas links only: the canonical Atlas id ("item:2589", "npc:46", "browse:items/weapons", "talents:tree/hunter"); Id is also set for item:N
        public string AtlasKind;     // Atlas links only: "item" | "npc" | ... | "browse" | "talents" | "professions"
    }

    // What this PC knows about items: the names, qualities and icons the Bags addon saved (the same data the Bags page shows).
    // Nothing here touches the network and nothing is written to the WoW folder.
    public class ItemInfo { public int Id; public string Name; public int Quality = 1; public int Icon; }

    public static class ItemKnowledge
    {
        static readonly object gate = new object();
        static Dictionary<int, ItemInfo> byId = new Dictionary<int, ItemInfo>();
        static Dictionary<string, int> byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);   // 0 = two different items share the name
        static DateTime loadedAt = DateTime.MinValue;
        static bool loading;
        public static Func<string> WowRoot = () => null;
        public static event Action Changed;                 // raised on the UI thread when new data arrived

        public static int Count { get { lock (gate) return byId.Count; } }

        public static ItemInfo Get(int id) { lock (gate) return byId.TryGetValue(id, out var i) ? i : null; }

        // the id for an item NAME, only when exactly one item has it (otherwise 0: a plain chip)
        public static int IdOf(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return 0;
            lock (gate) return byName.TryGetValue(name.Trim(), out var id) ? id : 0;
        }

        public static void Set(IEnumerable<InvChar> chars, IEnumerable<InvGuild> guilds = null)
        {
            var ids = new Dictionary<int, ItemInfo>();
            void Add(int id, string name, int quality, int icon)
            {
                if (id <= 0) return;
                if (!ids.TryGetValue(id, out var it)) ids[id] = it = new ItemInfo { Id = id };
                if (!string.IsNullOrEmpty(name) && !name.StartsWith("Item #", StringComparison.Ordinal)) it.Name = name;
                if (quality >= 0) it.Quality = quality;
                if (icon > 0) it.Icon = icon;
            }
            foreach (var c in chars ?? Enumerable.Empty<InvChar>())
            {
                foreach (var list in new[] { c.Bags, c.Bank, c.Equipped })
                    if (list != null) foreach (var x in list) Add(x.Id, x.Name, x.Quality, x.Icon);
                foreach (var conts in new[] { c.BagsCont, c.BankCont })
                    if (conts != null) foreach (var ct in conts) foreach (var sl in ct.Slots.Values) Add(sl.Id, sl.Name, sl.Quality, sl.Icon);
            }
            foreach (var g in guilds ?? Enumerable.Empty<InvGuild>())
                foreach (var t in g.Tabs) foreach (var sl in t.Slots.Values) Add(sl.Id, sl.Name, sl.Quality, sl.Icon);

            var names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var it in ids.Values.Where(i => !string.IsNullOrEmpty(i.Name)))
                names[it.Name.Trim()] = names.TryGetValue(it.Name.Trim(), out var have) && have != it.Id ? 0 : it.Id;
            lock (gate) { byId = ids; byName = names; loadedAt = DateTime.UtcNow; }
        }

        // reads the Bags addon's saved data once in a while (in the background; read-only) and tells the UI when it is there
        public static void EnsureLoaded()
        {
            lock (gate)
            {
                if (loading || (DateTime.UtcNow - loadedAt).TotalSeconds < 120) return;
                loading = true;
                loadedAt = DateTime.UtcNow;
            }
            var root = WowRoot?.Invoke();
            var app = System.Windows.Application.Current?.Dispatcher;
            Task.Run(() =>
            {
                try
                {
                    if (string.IsNullOrEmpty(root)) return;
                    var data = InventoryReader.ReadData(root);
                    Set(data.Chars, data.Guilds);
                    app?.BeginInvoke(new Action(() => Changed?.Invoke()));
                }
                catch (Exception e) { Util.Log("item links: " + e.Message); }
                finally { lock (gate) loading = false; }
            });
        }

        public static void ResetForTest() { lock (gate) { byId = new Dictionary<int, ItemInfo>(); byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); loadedAt = DateTime.UtcNow; loading = false; } }
        public static void RaiseChanged() => Changed?.Invoke();
    }

    // Finds item references in chat text: [Item Name], Wowhead item URLs and WoW chat links (|cffa335ee|Hitem:12345:...|h[Name]|h|r).
    // Pure text work - nothing is fetched. A [bracket] only counts when it looks like an item name; it gets an id only when the item data
    // knows exactly one item of that name.
    public static class ItemLinks
    {
        public const int MaxPerMessage = 6;

        static readonly Regex ChatLink = new Regex(@"(?:\|c[0-9a-fA-F]{2}([0-9a-fA-F]{6}))?\|Hitem:(\d{1,7})(?::[-\d]*)*\|h\[([^\[\]|\r\n]{1,80})\]\|h(?:\|r)?",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // https://[www.|classic.|tbc.|wotlk.|cata.]wowhead.com/[locale/][classic/|tbc/|wotlk/|cata/]item=ID[/slug][?...][#...]
        // the host must END at wowhead.com (a "/" follows), so wowhead.com.evil.example and notwowhead.com do not match
        static readonly Regex WowheadUrl = new Regex(
            @"(?<![\w/=?&.:@%#-])https?://(?:www\.|classic\.|tbc\.|wotlk\.|cata\.|mop-classic\.)?wowhead\.com/(?:[a-z]{2}/)?(?:(?:classic|tbc|wotlk|cata|mop-classic|classic-ptr)/)?item=(\d{1,7})(?:/([a-z0-9][a-z0-9\-]{0,80}))?(?=$|[\s/?#&<>""'),.;:!\]])[^\s<>""']*",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // [Name]: not part of [[nested]], not a Markdown link [text](url), 3-48 plain characters
        static readonly Regex Bracket = new Regex(@"(?<![\[\w])\[([^\[\]\r\n|]{3,48})\](?!\(|\])", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        static readonly Regex NameChars = new Regex(@"^[\p{L}\p{N}][\p{L}\p{N} '\-,.:!()]*[\p{L}\p{N})!.]$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        static readonly string[] QualityColors = { "9d9d9d", "ffffff", "1eff00", "0070dd", "a335ee", "ff8000", "e6cc80", "00ccff" };
        public static readonly string[] QualityHex = { "#9D9D9D", "#FFFFFF", "#1EFF00", "#0070DD", "#A335EE", "#FF8000", "#E6CC80", "#00CCFF" };
        public static readonly string[] QualityNames = { "Poor", "Common", "Uncommon", "Rare", "Epic", "Legendary", "Artifact", "Heirloom" };

        public static int QualityOfColor(string rrggbb)
        {
            if (string.IsNullOrEmpty(rrggbb)) return -1;
            var i = Array.IndexOf(QualityColors, rrggbb.ToLowerInvariant());
            return i;
        }

        // the words that look like an item name: letters, digits, spaces, a little punctuation; at least one letter, at most 8 words, not a marker like [x]
        public static bool LooksLikeItemName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length < 3 || name.Length > 48) return false;
            if (!NameChars.IsMatch(name) || !name.Any(char.IsLetter)) return false;
            if (name.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length > 8) return false;
            if (name.Contains("  ")) return false;
            return !Blocked.Contains(name.Trim());
        }

        // brackets people write for other reasons (and the sign-up fallback line)
        static readonly HashSet<string> Blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Looking for group", "edited", "deleted", "image", "picture", "screenshot", "video", "link", "url", "todo", "sic", "note", "spoiler", "citation needed",
        };

        // Atlas links (the Copy button on every Atlas page): "<kind>:<number>" for the entity kinds, "browse:" / "talents:" / "professions:" + a path.
        // Whole tokens only: not after a letter, digit or one of / = ? & . : @ % # - | (so "xitem:5", "http://x/item:5" and "|Hitem:5" do not count)
        static readonly Regex AtlasEntity = new Regex(@"(?<![\w/=?&.:@%#|\-])(item|npc|obj|object|quest|zone|instance|spell|skill|faction):(\d{1,9})(?!\w)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly char[] PathPunct = { '.', ',', ';', ':', '!', '?' };
        static readonly Regex AtlasPath = new Regex(@"(?<![\w/=?&.:@%#|\-])(browse|talents|professions):([^\s<>""'`|\[\]()]{1,200})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // the canonical Atlas id of a browse / talents / professions path, null when it is not one the Atlas knows
        static string PathId(string kind, string sub)
        {
            if (string.IsNullOrEmpty(sub)) return null;
            try
            {
                switch (kind)
                {
                    case "browse":
                        {
                            if (!Services.Atlas.BrowseQuery.TryParse("browse:" + sub, out var q)) return null;
                            var cat = Services.Atlas.AtlasCategories.Find(q.Cat);
                            return cat != null && !cat.Hosted ? q.ToId() : null;
                        }
                    case "talents":
                        {
                            Services.Atlas.AtlasHosts.ParseTalents(sub, out var cf, out var code);
                            if (cf.Length > 0) return Array.IndexOf(Services.Atlas.AtlasHosts.ClassFiles, cf) >= 0 ? Services.Atlas.AtlasHosts.TalentsId(cf) : null;
                            return code.Length > 0 ? Services.Atlas.AtlasHosts.TalentsBuildId(code) : null;
                        }
                    case "professions":
                        {
                            Services.Atlas.AtlasHosts.ParseProfessions(sub, out var slug, out var tab);
                            if (slug.Length == 0 && tab.Length == 0) return null;
                            if (slug.Length > 0 && !Services.Atlas.AtlasHosts.ProfessionNames.Any(n => Services.Atlas.AtlasHosts.Slug(n) == slug)
                                && !Services.Atlas.AtlasHosts.ProfessionList().Any(n => Services.Atlas.AtlasHosts.Slug(n) == slug)) return null;
                            return "professions:" + sub.ToLowerInvariant();
                        }
                }
            }
            catch (Exception) { }
            return null;
        }

        public static List<ItemRef> Find(string text, Func<string, int> idOfName = null)
        {
            var found = new List<ItemRef>();
            if (string.IsNullOrEmpty(text) || text.Length > 4000) return found;
            var taken = new List<Tuple<int, int>>();
            bool Free(int s, int l) => taken.All(t => s + l <= t.Item1 || s >= t.Item1 + t.Item2);
            void Add(ItemRef r) { found.Add(r); taken.Add(Tuple.Create(r.Start, r.Length)); }

            foreach (Match m in ChatLink.Matches(text))
            {
                if (!int.TryParse(m.Groups[2].Value, out var id) || id <= 0) continue;
                Add(new ItemRef { Kind = ItemRefKind.ChatLink, Start = m.Index, Length = m.Length, Id = id, Name = m.Groups[3].Value.Trim(), Quality = QualityOfColor(m.Groups[1].Value) });
            }
            foreach (Match m in WowheadUrl.Matches(text))
            {
                if (!Free(m.Index, m.Length) || !int.TryParse(m.Groups[1].Value, out var id) || id <= 0) continue;
                Add(new ItemRef { Kind = ItemRefKind.Wowhead, Start = m.Index, Length = m.Length, Id = id, Name = SlugName(m.Groups[2].Value) });
            }
            foreach (Match m in AtlasEntity.Matches(text))
            {
                if (!Free(m.Index, m.Length) || !int.TryParse(m.Groups[2].Value, out var id) || id <= 0) continue;
                var kind = m.Groups[1].Value.ToLowerInvariant();
                if (kind == "object") kind = "obj";
                Add(new ItemRef { Kind = ItemRefKind.Atlas, Start = m.Index, Length = m.Length, AtlasKind = kind, AtlasId = kind + ":" + id.ToString(CultureInfo.InvariantCulture), Id = kind == "item" ? id : 0 });
            }
            foreach (Match m in AtlasPath.Matches(text))
            {
                if (!Free(m.Index, m.Length)) continue;
                var kind = m.Groups[1].Value.ToLowerInvariant();
                var sub = m.Groups[2].Value.TrimEnd(PathPunct).Trim('/');
                var aid = PathId(kind, sub);
                if (aid == null) continue;
                Add(new ItemRef { Kind = ItemRefKind.Atlas, Start = m.Index, Length = kind.Length + 1 + m.Groups[2].Value.TrimEnd(PathPunct).Length, AtlasKind = kind, AtlasId = aid });
            }
            foreach (Match m in Bracket.Matches(text))
            {
                if (!Free(m.Index, m.Length)) continue;
                var name = m.Groups[1].Value;
                if (!LooksLikeItemName(name)) continue;
                Add(new ItemRef { Kind = ItemRefKind.Bracket, Start = m.Index, Length = m.Length, Name = name, Id = idOfName?.Invoke(name) ?? 0 });
            }
            found.Sort((a, b) => a.Start.CompareTo(b.Start));
            // one chip per item: the same id or the same [name] twice is shown once; a cap keeps a flood of brackets calm
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var res = new List<ItemRef>();
            foreach (var r in found)
            {
                var key = r.Id > 0 ? "#" + r.Id : r.AtlasId != null ? "a:" + r.AtlasId : "n:" + r.Name;
                if (!seen.Add(key)) continue;
                res.Add(r);
                if (res.Count >= MaxPerMessage) break;
            }
            return res;
        }

        static string SlugName(string slug)
        {
            if (string.IsNullOrEmpty(slug)) return null;
            var words = slug.Split(new[] { '-' }, StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]) + w.Substring(1));
            var s = string.Join(" ", words);
            return s.Length >= 3 ? s : null;
        }

        // the text as shown: pasted game links read as [Item Name] (the colour codes and ids are noise)
        public static string Display(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf("|H", StringComparison.Ordinal) < 0) return text;
            return ChatLink.Replace(text, m => "[" + m.Groups[3].Value.Trim() + "]");
        }

        // where a chip leads: the item on Wowhead Classic; for a name we could not resolve, Wowhead's search. The URL is built here (never the pasted one).
        public static string UrlFor(ItemRef r) =>
            r.Id > 0 ? "https://www.wowhead.com/classic/item=" + r.Id.ToString(CultureInfo.InvariantCulture)
            : "https://www.wowhead.com/classic/search?q=" + Uri.EscapeDataString(r.Name ?? "");
    }
}
