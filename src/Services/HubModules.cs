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

    // damage range with swing speed (gear.stats.melee / .ranged); null fields = not saved
    public class HubDamage
    {
        public double? Min, Max, Speed;
    }

    // gear.stats (additive, Hub 1.13.0): every field optional, null = the game did not return it readably
    public class HubStats
    {
        public double? Health, Power, Str, Agi, Sta, Int, Spi, Armor, Ap, Rap, Crit, Rcrit, Dodge, Parry, Block, Speed;
        public string PowerType;                       // "MANA", "RAGE", "ENERGY" ...
        public HubDamage Melee, Ranged;
    }

    // gear: { at, ilvl, slots = { [inventorySlotId] = { i, q, ic, il } }, links, stats = { ... } }
    public class HubGearModule
    {
        public long At;
        public HubStats Stats;                         // null for gear saved before Hub 1.13.0 (or when the game gave nothing)
        public double Ilvl;                            // 0 = not known
        public SortedDictionary<int, HubItem> Slots = new SortedDictionary<int, HubItem>();
        public Dictionary<int, string> Links = new Dictionary<int, string>();
    }

    public class HubTalentNode
    {
        public int Id, R, M, S, Ic;                    // Ic = spell icon file id, 0 when missing
        public double X, Y;
        public string N;
        public bool HasPos;
    }

    // talents: { at, api = "traits" | "classic", spent, unspent, nodes = { { id, r, m, x, y, s, n, ic } } }
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
        public int Line;                               // skill line id (Hub 1.14.0+), 0 = not saved
    }

    // one profession's learned recipes as the Hub read them from the profession window (Hub 1.14.0): the spell ids, when, and whether the read was partial
    public class HubKnownScan
    {
        public int Line;                               // skill line id
        public string Name;                            // profession name as the Hub saw it, may be null
        public long At;
        public bool Partial;                           // collapsed headers / unreadable entries: the ids are a lower bound
        public HashSet<int> Ids = new HashSet<int>();
        public const int MaxIds = 2000;
    }

    // professions: { at, list = { { name, skill, max, ic, line, kind = "primary" | "secondary" } }, known = { [skillLineId] = { at, partial, n, ids = { spellId, ... } } } }
    public class HubProfessionsModule
    {
        public long At;
        public List<HubProfession> List = new List<HubProfession>();
        public Dictionary<int, HubKnownScan> Known = new Dictionary<int, HubKnownScan>();     // empty before the Hub 1.14.0 or before a profession window was opened
    }

    public class HubPetAbility
    {
        public string Id, Name;
        public int Rank;
    }

    public class HubPet
    {
        public string Key, Name, Family, FamilyId, LoyaltyText;
        public int Level, Loyalty, Ic;                 // Loyalty 1-6, 0 = not known; Ic = icon file id, 0 = none
        public int Stable;                             // stable slot; 0 = with you (no slot published)
        public int TpTotal = -1, TpSpent = -1;         // -1 = not known
        public int Happiness;                          // 1 unhappy, 2 content, 3 happy; 0 = not known
        public List<HubPetAbility> Abilities = new List<HubPetAbility>();
        public bool InStable => Stable > 0;
    }

    // hunter.pets (Hunter Helper, per character): { at, active = petKey, pets = { [petKey] = { name, family, familyId, level, loyalty, loyaltyText, ic, stable,
    // tpTotal, tpSpent, happiness, abilities = { { id, name, rank } } } }, known = { [abilityId] = rank } }
    public class HubPetsModule
    {
        public long At;
        public string Active;                          // key of the pet that is with the hunter, null when none
        public List<HubPet> Pets = new List<HubPet>(); // with you first (the active one first), then the stable by slot
        public Dictionary<string, int> Known = new Dictionary<string, int>(StringComparer.Ordinal);
        public IEnumerable<HubPet> WithYou => Pets.Where(p => !p.InStable);
        public IEnumerable<HubPet> Stabled => Pets.Where(p => p.InStable);
    }

    public static class HubModules
    {
        public const int MaxPets = 40, MaxPetAbilities = 40, MaxKnown = 100, MaxText = 48;

        // names for the abilities a hunter can teach (the module only carries ids there); mirrors Hunter Helper's ability list
        public static readonly Dictionary<string, string> PetAbilityNames = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["arcaneresistance"] = "Arcane Resistance", ["bite"] = "Bite", ["charge"] = "Charge", ["claw"] = "Claw", ["cower"] = "Cower", ["dash"] = "Dash",
            ["demoralizingscreech"] = "Demoralizing Screech", ["dismember"] = "Dismember", ["dive"] = "Dive", ["dustcloud"] = "Dust Cloud", ["fasterattack"] = "Faster Attack",
            ["fireresistance"] = "Fire Resistance", ["frostresistance"] = "Frost Resistance", ["furioushowl"] = "Furious Howl", ["greatstamina"] = "Great Stamina", ["growl"] = "Growl",
            ["lavabreath"] = "Lava Breath", ["lightningbreath"] = "Lightning Breath", ["mine"] = "Mine!", ["naturalarmor"] = "Natural Armor", ["natureresistance"] = "Nature Resistance",
            ["pinch"] = "Pinch", ["prowl"] = "Prowl", ["savagerend"] = "Savage Rend", ["scorpidpoison"] = "Scorpid Poison", ["shadowresistance"] = "Shadow Resistance",
            ["shellshield"] = "Shell Shield", ["slowerattack"] = "Slower Attack", ["swipe"] = "Swipe", ["tendonrip"] = "Tendon Rip", ["thunderstomp"] = "Thunderstomp",
            ["trickstersdance"] = "Trickster's Dance", ["web"] = "Web", ["sonicblast"] = "Sonic Blast",
        };
        public static readonly string[] LoyaltyNames = { "Rebellious", "Unruly", "Submissive", "Dependable", "Faithful", "Best Friend" };

        // the display name of a pet ability id; unknown ids get their text with the first letter capitalised
        public static string PetAbilityName(string id)
        {
            if (string.IsNullOrEmpty(id)) return "?";
            if (PetAbilityNames.TryGetValue(id, out var n)) return n;
            return char.ToUpperInvariant(id[0]) + id.Substring(1);
        }

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
            res.Stats = StatsOf(d.Child("stats"));
            return res;
        }

        static double? Opt(Dictionary<string, object> d, string k, double lo = 0, double hi = 1e9)
        {
            if (d != null && d.TryGetValue(k, out var v) && v is double n && !double.IsNaN(n) && !double.IsInfinity(n)) return Math.Max(lo, Math.Min(hi, n));
            return null;
        }

        static HubDamage DamageOf(Dictionary<string, object> d)
        {
            if (d == null) return null;
            var r = new HubDamage { Min = Opt(d, "min"), Max = Opt(d, "max"), Speed = Opt(d, "speed", 0, 100) };
            return r.Min == null && r.Max == null ? null : r;
        }

        // gear.stats; null when absent or empty
        static HubStats StatsOf(Dictionary<string, object> s)
        {
            if (s == null) return null;
            var r = new HubStats
            {
                Health = Opt(s, "health"), Power = Opt(s, "power"), PowerType = Short(s.Str("powerType")),
                Str = Opt(s, "str"), Agi = Opt(s, "agi"), Sta = Opt(s, "sta"), Int = Opt(s, "int"), Spi = Opt(s, "spi"),
                Armor = Opt(s, "armor"), Ap = Opt(s, "ap", -1e6, 1e6), Rap = Opt(s, "rap", -1e6, 1e6),
                Crit = Opt(s, "crit", 0, 1000), Rcrit = Opt(s, "rcrit", 0, 1000), Dodge = Opt(s, "dodge", 0, 1000), Parry = Opt(s, "parry", 0, 1000), Block = Opt(s, "block", 0, 1000),
                Speed = Opt(s, "speed", 0, 1000), Melee = DamageOf(s.Child("melee")), Ranged = DamageOf(s.Child("ranged")),
            };
            bool any = r.Health != null || r.Power != null || r.Str != null || r.Agi != null || r.Sta != null || r.Int != null || r.Spi != null || r.Armor != null
                || r.Ap != null || r.Rap != null || r.Crit != null || r.Rcrit != null || r.Dodge != null || r.Parry != null || r.Block != null || r.Speed != null
                || r.Melee != null || r.Ranged != null;
            return any ? r : null;
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
                        Id = n.Int("id"), R = Math.Max(0, (int)Math.Min(1000, Num(n, "r"))), M = Math.Max(0, (int)Math.Min(1000, Num(n, "m"))), S = (int)Math.Max(0, Math.Min(int.MaxValue, Num(n, "s"))), Ic = (int)Math.Max(0, Math.Min(int.MaxValue, Num(n, "ic"))),
                        N = n.Str("n"), X = Num(n, "x"), Y = Num(n, "y"), HasPos = n.ContainsKey("x") && n.ContainsKey("y"),
                    };
                    res.Nodes.Add(node);
                }
            return res;
        }

        static string Short(string s) => string.IsNullOrWhiteSpace(s) ? null : (s.Length > MaxText ? s.Substring(0, MaxText) : s);
        static int Clamp(double v, int lo, int hi) => (int)Math.Max(lo, Math.Min(hi, v));

        // hunter.pets v1
        public static HubPetsModule PetsOf(HubChar c)
        {
            var m = Mod(c, "hunter.pets");
            if (!Usable(m)) return null;
            var d = m.Data;
            var res = new HubPetsModule { At = AtOf(m, d), Active = Short(d.Str("active")) };
            var pets = d.Child("pets");
            if (pets != null)
                foreach (var kv in pets)
                {
                    if (res.Pets.Count >= MaxPets) break;
                    if (!(kv.Value is Dictionary<string, object> p)) continue;
                    var name = Short(p.Str("name"));
                    if (name == null) continue;
                    var pet = new HubPet
                    {
                        Key = kv.Key, Name = name, Family = Short(p.Str("family")), FamilyId = Short(p.Str("familyId")), LoyaltyText = Short(p.Str("loyaltyText")),
                        Level = Clamp(Num(p, "level"), 0, 1000), Loyalty = Clamp(Num(p, "loyalty"), 0, 6), Ic = Clamp(Num(p, "ic"), 0, int.MaxValue),
                        Stable = Clamp(Num(p, "stable"), 0, 1000), Happiness = Clamp(Num(p, "happiness"), 0, 3),
                    };
                    if (p.ContainsKey("tpTotal") && Num(p, "tpTotal") > 0) { pet.TpTotal = Clamp(Num(p, "tpTotal"), 0, 100000); pet.TpSpent = Clamp(Num(p, "tpSpent"), 0, 100000); }
                    var abs = p.Child("abilities");
                    if (abs != null)
                        foreach (var ak in abs.OrderBy(k => int.TryParse(k.Key, out var n) ? n : int.MaxValue))
                        {
                            if (pet.Abilities.Count >= MaxPetAbilities) break;
                            if (!(ak.Value is Dictionary<string, object> a)) continue;
                            var id = Short(a.Str("id"));
                            var an = Short(a.Str("name")) ?? (id != null ? PetAbilityName(id) : null);
                            if (an == null) continue;
                            pet.Abilities.Add(new HubPetAbility { Id = id, Name = an, Rank = Clamp(Num(a, "rank"), 0, 100) });
                        }
                    res.Pets.Add(pet);
                }
            // with you: the active pet first; then the stable by slot; alphabetical within the same rank
            res.Pets = res.Pets.OrderBy(p => p.InStable ? 1 : 0).ThenBy(p => p.Key == res.Active ? 0 : 1).ThenBy(p => p.Stable).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            var known = d.Child("known");
            if (known != null)
                foreach (var kv in known)
                {
                    if (res.Known.Count >= MaxKnown) break;
                    if (string.IsNullOrEmpty(kv.Key) || kv.Key.Length > MaxText || !(kv.Value is double r) || double.IsNaN(r) || r < 1) continue;
                    res.Known[kv.Key] = Clamp(r, 1, 100);
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
                    res.List.Add(new HubProfession { Name = name, Skill = Math.Max(0, (int)Math.Min(100000, Num(p, "skill"))), Max = Math.Max(0, (int)Math.Min(100000, Num(p, "max"))), Ic = (int)Math.Max(0, Math.Min(int.MaxValue, Num(p, "ic"))), Kind = p.Str("kind") ?? "primary", Line = (int)Math.Max(0, Math.Min(1000000, Num(p, "line"))) });
                }
            var known = d.Child("known");
            if (known != null)
                foreach (var kv in known)
                {
                    if (res.Known.Count >= 16) break;
                    if (!int.TryParse(kv.Key, NumberStyles.None, CultureInfo.InvariantCulture, out var line) || line < 1 || line > 1000000) continue;
                    if (!(kv.Value is Dictionary<string, object> e)) continue;
                    var ks = new HubKnownScan { Line = line, Name = e.Str("n"), At = e.Long("at"), Partial = !(e.TryGetValue("partial", out var pv) && pv is bool pb && !pb) };
                    var ids = e.Child("ids");
                    if (ids != null)
                        foreach (var iv in ids.Values)
                        {
                            if (ks.Ids.Count >= HubKnownScan.MaxIds) break;
                            if (iv is double dn && !double.IsNaN(dn) && dn >= 1 && dn <= int.MaxValue && dn == Math.Floor(dn)) ks.Ids.Add((int)dn);
                        }
                    res.Known[line] = ks;
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
        public List<HubTalentNode> Copies = new List<HubTalentNode>();      // a second node of a talent already in the tree (left out)
    }

    public static class TalentGrid
    {
        public const double MaxCoord = 40000;          // |x| or |y| above this is not a position (the real data has had 102800)
        public const int MaxCols = 12, MaxRows = 14, MaxTrees = 4;

        public static TalentLayout Group(IEnumerable<HubTalentNode> nodes)
        {
            var res = new TalentLayout();
            var good = new List<HubTalentNode>();
            // the game's own positions have a few with an extra digit (102800 for 10280, 39300 for 3930): divided back like the talent data does
            // (TalentNormalise.Divide), so those talents land on their real cell instead of "nowhere"
            var px = new Dictionary<HubTalentNode, double>(); var py = new Dictionary<HubTalentNode, double>();
            var repaired = new HashSet<HubTalentNode>();
            foreach (var n in nodes ?? Enumerable.Empty<HubTalentNode>())
            {
                if (n == null) continue;
                double x = n.HasPos ? Talents.TalentNormalise.Divide(n.X) : double.NaN, y = n.HasPos ? Talents.TalentNormalise.Divide(n.Y) : double.NaN;
                if (!n.HasPos || double.IsNaN(x) || double.IsNaN(y) || Math.Abs(x) > MaxCoord || Math.Abs(y) > MaxCoord) { res.Loose.Add(n); continue; }
                px[n] = x; py[n] = y;
                if (x != n.X || y != n.Y) repaired.Add(n);
                good.Add(n);
            }
            // the same talent (spell) twice: the game keeps a hidden copy of a few (Lightning Reflexes 104982 next to 110859). One stays: the one with
            // ranks, else the one whose position needed no repair, else the lower id; the copy is left out (res.Copies), never shown as a talent of its own
            foreach (var g in good.Where(n => n.S > 0).GroupBy(n => n.S).Where(g => g.Count() > 1).ToList())
            {
                var keep = g.OrderByDescending(n => n.R).ThenBy(n => repaired.Contains(n) ? 1 : 0).ThenBy(n => n.Id).First();
                foreach (var n in g.Where(n => n != keep)) { good.Remove(n); res.Copies.Add(n); }
            }
            if (good.Count == 0) return res;

            // trees: split the sorted x values where a gap is much wider than the usual one
            var xs = good.Select(n => px[n]).Distinct().OrderBy(v => v).ToList();
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
            var groups = good.GroupBy(n => TreeOf(px[n])).OrderBy(g => g.Key).ToList();
            int idx = 1;
            foreach (var g in groups)
            {
                var list = g.ToList();
                var tree = new TalentTree { Index = idx++, Points = list.Sum(n => n.R) };
                var colOf = Bucket(list.Select(n => px[n]), MaxCols, out var cols);
                var rowOf = Bucket(list.Select(n => py[n]), MaxRows, out var rows);
                tree.Cols = cols; tree.Rows = rows;
                var cells = new Dictionary<long, TalentCell>();
                foreach (var n in list)
                {
                    int c = colOf(px[n]), r = rowOf(py[n]);
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
