using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace ElansAddonHub.Services
{
    // Typed readers for the Hub modules that carry what the Characters page shows (ARCHITECTURE.md "Schemas"): bags, bank, guildbank, gear, talents,
    // professions, all v1. Every reader returns null when the module is missing, newer than this app understands (TooNew) or another version, so the
    // page can tell "not there yet" from "update the Outpost" with HubModules.StateOf. Unknown fields are ignored; odd values never throw.
    public enum HubModState { Missing, TooNew, Ok }

    // Item (inside containers and gear): { i = itemId, c = count, q = quality, ic = iconFileId, il = itemLevel (gear only) }
    public class HubItem
    {
        public int I, C = 1, Q = 1, Ic, Il;
        public string Name;                            // from the module's links table when it has the item
    }

    // Container: { n = slots, k = kind, id = bag itemId or nil, ic = icon, s = { [slot] = Item } }
    public class HubContainer
    {
        public int Index;                              // the key in cont
        public int N;
        public string K;                               // "backpack" | "bag" | "reagent" | "bank" | "bankbag" | "tab"
        public int Id, Ic;
        public string BagName;                         // name of the bag item (links), null when unknown
        public int BagQuality = 1;
        public Dictionary<int, HubItem> S = new Dictionary<int, HubItem>();
    }

    // bags / bank: { at, cont = { [containerIndex] = Container }, links }
    public class HubBagsModule
    {
        public long At;
        public List<HubContainer> Cont = new List<HubContainer>();     // ordered by index
        public Dictionary<int, string> Links = new Dictionary<int, string>();
    }

    public class HubGuildTabData
    {
        public int Index, Ic;
        public string Name;
        public long At;
        public Dictionary<int, HubItem> S = new Dictionary<int, HubItem>();
    }

    public class HubGuildData
    {
        public string Key, Name, Realm;
        public long Money, At;
        public List<HubGuildTabData> Tabs = new List<HubGuildTabData>();
    }

    // guildbank (account): { guilds = { ["Realm-Guild"] = { name, realm, money, at, tabs = { [tab] = { name, ic, at, s } } } }, links }
    public class HubGuildBankModule
    {
        public List<HubGuildData> Guilds = new List<HubGuildData>();
        public Dictionary<int, string> Links = new Dictionary<int, string>();
    }

    // gear: { at, ilvl, slots = { [inventorySlotId] = { i, q, ic, il } }, links }
    public class HubGearModule
    {
        public long At;
        public double Ilvl;                            // 0 = not known
        public SortedDictionary<int, HubItem> Slots = new SortedDictionary<int, HubItem>();
        public Dictionary<int, string> Links = new Dictionary<int, string>();
    }

    public class HubTalentNode
    {
        public int Id, R, M, S;
        public double X, Y;
        public string N;
        public bool HasPos;
    }

    // talents: { at, api = "traits" | "classic", spent, unspent, nodes = { { id, r, m, x, y, s, n } } }
    public class HubTalentsModule
    {
        public long At;
        public string Api;
        public int Spent, Unspent;
        public List<HubTalentNode> Nodes = new List<HubTalentNode>();
    }

    public class HubProfession
    {
        public string Name, Kind;
        public int Skill, Max, Ic;
    }

    // professions: { at, list = { { name, skill, max, ic, kind = "primary" | "secondary" } } }
    public class HubProfessionsModule
    {
        public long At;
        public List<HubProfession> List = new List<HubProfession>();
    }

    public static class HubModules
    {
        public static HubModState StateOf(HubModule m)
        {
            if (m == null) return HubModState.Missing;
            if (m.TooNew || m.Data == null) return HubModState.TooNew;
            return m.V == 1 ? HubModState.Ok : HubModState.TooNew;
        }

        static HubModule Mod(HubChar c, string name) => c != null && c.Modules.TryGetValue(name, out var m) ? m : null;
        public static HubModState StateOf(HubChar c, string name) => StateOf(Mod(c, name));

        // the account module (guildbank)
        public static HubModState StateOf(HubModel hub, string name) => StateOf(hub != null && hub.AccountModules.TryGetValue(name, out var m) ? m : null);

        static bool Usable(HubModule m) => StateOf(m) == HubModState.Ok;
        static long AtOf(HubModule m, Dictionary<string, object> d) { var at = d.Long("at"); return at > 0 ? at : m.At; }

        static double Num(Dictionary<string, object> d, string k)
        {
            if (d != null && d.TryGetValue(k, out var v) && v is double n && !double.IsNaN(n) && !double.IsInfinity(n)) return n;
            return 0;
        }

        static readonly Regex NameInLink = new Regex(@"\[(.+?)\]", RegexOptions.Compiled);
        static readonly Regex ColorInLink = new Regex(@"\|c[0-9a-fA-F]{2}([0-9a-fA-F]{6})", RegexOptions.Compiled);

        // "|cff0070dd|Hitem:3000::::::::|h[Linen Bag]|h|r" -> "Linen Bag"
        public static string NameOfLink(string link)
        {
            if (string.IsNullOrEmpty(link)) return null;
            var m = NameInLink.Match(link);
            return m.Success ? m.Groups[1].Value : null;
        }

        public static int QualityOfLink(string link)
        {
            if (string.IsNullOrEmpty(link)) return -1;
            var m = ColorInLink.Match(link);
            return m.Success ? ItemLinks.QualityOfColor(m.Groups[1].Value) : -1;
        }

        static Dictionary<int, string> LinksOf(Dictionary<string, object> d)
        {
            var res = new Dictionary<int, string>();
            var l = d.Child("links");
            if (l == null) return res;
            foreach (var kv in l)
                if (int.TryParse(kv.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && kv.Value is string s) res[id] = s;
            return res;
        }

        static HubItem ItemOf(Dictionary<string, object> e, Dictionary<int, string> links)
        {
            int id = (int)Math.Max(0, Math.Min(int.MaxValue, Num(e, "i")));
            if (id <= 0) return null;
            var it = new HubItem { I = id, C = Math.Max(1, (int)Math.Min(1000000, Num(e, "c"))), Ic = (int)Math.Max(0, Math.Min(int.MaxValue, Num(e, "ic"))), Il = (int)Math.Max(0, Math.Min(100000, Num(e, "il"))) };
            int q = e.ContainsKey("q") ? (int)Num(e, "q") : -1;
            if (links != null && links.TryGetValue(id, out var link))
            {
                it.Name = NameOfLink(link);
                if (q < 0) q = QualityOfLink(link);
            }
            it.Q = q < 0 ? 1 : Math.Min(q, 7);
            return it;
        }

        static void ItemsOf(Dictionary<string, object> slots, Dictionary<int, HubItem> into, Dictionary<int, string> links)
        {
            if (slots == null) return;
            foreach (var kv in slots)
            {
                if (!int.TryParse(kv.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var slot) || slot < 0 || slot > 10000) continue;
                if (!(kv.Value is Dictionary<string, object> e)) continue;
                var it = ItemOf(e, links);
                if (it != null) into[slot] = it;
            }
        }

        // bags v1 or bank v1
        public static HubBagsModule BagsOf(HubChar c, string module = "bags")
        {
            var m = Mod(c, module);
            if (!Usable(m)) return null;
            var d = m.Data;
            var res = new HubBagsModule { At = AtOf(m, d), Links = LinksOf(d) };
            var cont = d.Child("cont");
            if (cont != null)
                foreach (var kv in cont)
                {
                    if (!int.TryParse(kv.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx) || !(kv.Value is Dictionary<string, object> ct)) continue;
                    var hc = new HubContainer { Index = idx, N = (int)Math.Max(0, Math.Min(500, Num(ct, "n"))), K = ct.Str("k"), Id = (int)Math.Max(0, Math.Min(int.MaxValue, Num(ct, "id"))), Ic = (int)Math.Max(0, Math.Min(int.MaxValue, Num(ct, "ic"))) };
                    if (hc.Id > 0 && res.Links.TryGetValue(hc.Id, out var link))
                    {
                        hc.BagName = NameOfLink(link);
                        var q = QualityOfLink(link);
                        if (q >= 0) hc.BagQuality = q;
                    }
                    ItemsOf(ct.Child("s"), hc.S, res.Links);
                    if (hc.N <= 0 && hc.S.Count > 0) hc.N = hc.S.Keys.Max();
                    if (hc.N > 0) res.Cont.Add(hc);
                }
            res.Cont = res.Cont.OrderBy(x => x.Index).ToList();
            return res;
        }

        public static HubBagsModule BankOf(HubChar c) => BagsOf(c, "bank");

        // guildbank v1 (account module)
        public static HubGuildBankModule GuildBankOf(HubModel hub)
        {
            if (hub == null || !hub.AccountModules.TryGetValue("guildbank", out var m) || !Usable(m)) return null;
            var d = m.Data;
            var res = new HubGuildBankModule { Links = LinksOf(d) };
            var gs = d.Child("guilds");
            if (gs == null) return res;
            foreach (var kv in gs)
            {
                if (!(kv.Value is Dictionary<string, object> g)) continue;
                var hg = new HubGuildData { Key = kv.Key, Name = g.Str("name") ?? kv.Key, Realm = g.Str("realm"), Money = g.Long("money"), At = g.Long("at") };
                var tabs = g.Child("tabs");
                if (tabs != null)
                    foreach (var tk in tabs)
                    {
                        if (!int.TryParse(tk.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx) || !(tk.Value is Dictionary<string, object> t)) continue;
                        var ht = new HubGuildTabData { Index = idx, Name = t.Str("name"), Ic = (int)Math.Max(0, Math.Min(int.MaxValue, Num(t, "ic"))), At = t.Long("at") };
                        ItemsOf(t.Child("s"), ht.S, res.Links);
                        hg.Tabs.Add(ht);
                    }
                hg.Tabs = hg.Tabs.OrderBy(x => x.Index).ToList();
                res.Guilds.Add(hg);
            }
            return res;
        }

        // gear v1
        public static HubGearModule GearOf(HubChar c)
        {
            var m = Mod(c, "gear");
            if (!Usable(m)) return null;
            var d = m.Data;
            var res = new HubGearModule { At = AtOf(m, d), Ilvl = Num(d, "ilvl"), Links = LinksOf(d) };
            var slots = d.Child("slots");
            if (slots != null)
            {
                var tmp = new Dictionary<int, HubItem>();
                ItemsOf(slots, tmp, res.Links);
                foreach (var kv in tmp) res.Slots[kv.Key] = kv.Value;
            }
            return res;
        }

        // talents v1
        public static HubTalentsModule TalentsOf(HubChar c)
        {
            var m = Mod(c, "talents");
            if (!Usable(m)) return null;
            var d = m.Data;
            var res = new HubTalentsModule { At = AtOf(m, d), Api = d.Str("api"), Spent = d.Int("spent"), Unspent = d.Int("unspent") };
            var nodes = d.Child("nodes");
            if (nodes != null)
                foreach (var kv in nodes.OrderBy(k => int.TryParse(k.Key, out var n) ? n : int.MaxValue))
                {
                    if (!(kv.Value is Dictionary<string, object> n)) continue;
                    var node = new HubTalentNode
                    {
                        Id = n.Int("id"), R = Math.Max(0, (int)Math.Min(1000, Num(n, "r"))), M = Math.Max(0, (int)Math.Min(1000, Num(n, "m"))), S = (int)Math.Max(0, Math.Min(int.MaxValue, Num(n, "s"))),
                        N = n.Str("n"), X = Num(n, "x"), Y = Num(n, "y"), HasPos = n.ContainsKey("x") && n.ContainsKey("y"),
                    };
                    res.Nodes.Add(node);
                }
            return res;
        }

        // professions v1
        public static HubProfessionsModule ProfessionsOf(HubChar c)
        {
            var m = Mod(c, "professions");
            if (!Usable(m)) return null;
            var d = m.Data;
            var res = new HubProfessionsModule { At = AtOf(m, d) };
            var list = d.Child("list");
            if (list != null)
                foreach (var kv in list.OrderBy(k => int.TryParse(k.Key, out var n) ? n : int.MaxValue))
                {
                    if (!(kv.Value is Dictionary<string, object> p)) continue;
                    var name = p.Str("name");
                    if (string.IsNullOrEmpty(name)) continue;
                    res.List.Add(new HubProfession { Name = name, Skill = Math.Max(0, (int)Math.Min(100000, Num(p, "skill"))), Max = Math.Max(0, (int)Math.Min(100000, Num(p, "max"))), Ic = (int)Math.Max(0, Math.Min(int.MaxValue, Num(p, "ic"))), Kind = p.Str("kind") ?? "primary" });
                }
            return res;
        }
    }

    // Groups raw talent node positions into trees, columns and rows for display. Positions come from the game as they are (the traits API uses large
    // numbers, several trees side by side), so this never trusts them: nodes with no or absurd positions go to Loose, a huge number of distinct
    // values is bucketed, and nothing here can throw.
    public class TalentCell
    {
        public int Col, Row;
        public List<HubTalentNode> Nodes = new List<HubTalentNode>();
    }

    public class TalentTree
    {
        public int Index, Cols, Rows, Points;
        public List<TalentCell> Cells = new List<TalentCell>();
    }

    public class TalentLayout
    {
        public List<TalentTree> Trees = new List<TalentTree>();
        public List<HubTalentNode> Loose = new List<HubTalentNode>();       // no usable position
    }

    public static class TalentGrid
    {
        public const double MaxCoord = 40000;          // |x| or |y| above this is not a position (the real data has had 102800)
        public const int MaxCols = 12, MaxRows = 14, MaxTrees = 4;

        public static TalentLayout Group(IEnumerable<HubTalentNode> nodes)
        {
            var res = new TalentLayout();
            var good = new List<HubTalentNode>();
            foreach (var n in nodes ?? Enumerable.Empty<HubTalentNode>())
            {
                if (n == null) continue;
                if (!n.HasPos || double.IsNaN(n.X) || double.IsNaN(n.Y) || Math.Abs(n.X) > MaxCoord || Math.Abs(n.Y) > MaxCoord) res.Loose.Add(n);
                else good.Add(n);
            }
            if (good.Count == 0) return res;

            // trees: split the sorted x values where a gap is much wider than the usual one
            var xs = good.Select(n => n.X).Distinct().OrderBy(v => v).ToList();
            var cuts = new List<double>();                  // x values that start a new tree
            if (xs.Count >= 4)
            {
                var gaps = new List<double>();
                for (int i = 1; i < xs.Count; i++) gaps.Add(xs[i] - xs[i - 1]);
                var sorted = gaps.OrderBy(g => g).ToList();
                var median = sorted[sorted.Count / 2];
                var limit = Math.Max(median * 2.5, 1);
                var big = gaps.Select((g, i) => new { g, i }).Where(t => t.g > limit).OrderByDescending(t => t.g).Take(MaxTrees - 1).OrderBy(t => t.i).ToList();
                foreach (var t in big) cuts.Add(xs[t.i + 1]);
            }
            int TreeOf(double x) { int t = 0; foreach (var c in cuts) if (x >= c) t++; return t; }
            var groups = good.GroupBy(n => TreeOf(n.X)).OrderBy(g => g.Key).ToList();
            int idx = 1;
            foreach (var g in groups)
            {
                var list = g.ToList();
                var tree = new TalentTree { Index = idx++, Points = list.Sum(n => n.R) };
                var colOf = Bucket(list.Select(n => n.X), MaxCols, out var cols);
                var rowOf = Bucket(list.Select(n => n.Y), MaxRows, out var rows);
                tree.Cols = cols; tree.Rows = rows;
                var cells = new Dictionary<long, TalentCell>();
                foreach (var n in list)
                {
                    int c = colOf(n.X), r = rowOf(n.Y);
                    long key = (long)r * 1000 + c;
                    if (!cells.TryGetValue(key, out var cell)) cells[key] = cell = new TalentCell { Col = c, Row = r };
                    cell.Nodes.Add(n);
                }
                tree.Cells = cells.Values.OrderBy(c => c.Row).ThenBy(c => c.Col).ToList();
                res.Trees.Add(tree);
            }
            return res;
        }

        // maps a value to a 0-based index: distinct values (merged when nearly equal) in order, or equal-width buckets when there are more than `max`
        static Func<double, int> Bucket(IEnumerable<double> values, int max, out int count)
        {
            var v = values.Distinct().OrderBy(x => x).ToList();
            if (v.Count == 1) { count = 1; return x => 0; }
            var gaps = new List<double>();
            for (int i = 1; i < v.Count; i++) gaps.Add(v[i] - v[i - 1]);
            var med = gaps.OrderBy(g => g).ToList()[gaps.Count / 2];
            // values closer than a third of the usual step are the same line
            var merged = new List<double> { v[0] };
            for (int i = 1; i < v.Count; i++) if (v[i] - merged[merged.Count - 1] >= med * 0.34) merged.Add(v[i]);
            if (merged.Count <= max)
            {
                count = merged.Count;
                var arr = merged.ToArray();
                return x => { int best = 0; for (int i = 1; i < arr.Length; i++) if (x >= arr[i] - med * 0.34 + 1e-9) best = i; return best; };
            }
            double lo = v[0], hi = v[v.Count - 1], span = Math.Max(hi - lo, 1);
            count = max;
            return x => Math.Max(0, Math.Min(max - 1, (int)((x - lo) / span * max)));
        }
    }
}
