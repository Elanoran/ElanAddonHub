using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Talents;

namespace ElansAddonHub.Lodge
{
    // One line of the Armory list (armory.chars): who shared which character, with the few facts the list filters on. The full character is fetched when opened.
    public class ArmoryEntry
    {
        public string Owner, By, Key, Name, Realm, Class, ClassText, Race, RaceFile, Faction, Guild;
        public int Sex, Level;
        public double Ilvl;
        public long At, Up;
        public bool Mine;
        public List<ArmoryProf> Professions = new List<ArmoryProf>();

        public string Id => Owner + "|" + Key;
        public string ClassName => !string.IsNullOrEmpty(ClassText) ? ClassText : (string.IsNullOrEmpty(Class) ? "" : char.ToUpperInvariant(Class[0]) + Class.Substring(1).ToLowerInvariant());
        public bool Has(string profession) => Professions.Any(p => string.Equals(p.Name, profession, StringComparison.OrdinalIgnoreCase));

        // every field is checked again here, whatever the server sent
        public static ArmoryEntry From(Dictionary<string, object> d)
        {
            if (d == null) return null;
            string S(string k, int max) { var s = d.Str(k); return string.IsNullOrWhiteSpace(s) ? null : (s.Length > max ? s.Substring(0, max) : s.Trim()); }
            var e = new ArmoryEntry
            {
                Owner = S("owner", 64), By = S("by", 64), Key = S("key", 80), Name = S("name", 24), Realm = S("realm", 40), Class = S("class", 20), ClassText = S("classText", 24),
                Race = S("race", 24), RaceFile = S("raceFile", 20), Faction = S("faction", 16), Guild = S("guild", 40), Sex = d.Int("sex"), Level = Math.Max(0, Math.Min(100, d.Int("level"))),
                At = d.Long("at"), Up = d.Long("up"), Mine = d.Bool("mine"),
            };
            if (d.TryGetValue("ilvl", out var iv) && iv is double idv && !double.IsNaN(idv) && !double.IsInfinity(idv)) e.Ilvl = Math.Max(0, Math.Min(2000, idv));
            if (e.Owner == null || e.Key == null || e.Name == null || e.Class == null) return null;
            if (e.By == null) e.By = e.Owner;
            foreach (var o in d.List("professions"))
                if (o is Dictionary<string, object> p && e.Professions.Count < 12 && !string.IsNullOrWhiteSpace(p.Str("name")))
                    e.Professions.Add(new ArmoryProf { Name = p.Str("name").Trim(), Skill = Math.Max(0, p.Int("skill")), Max = Math.Max(0, p.Int("max")) });
            return e;
        }
    }

    public class ArmoryProf
    {
        public string Name;
        public int Skill, Max;
    }

    // What of a character goes to the Lodge, and how a shared one is turned back into the model the Characters page already draws.
    // SHARED: name, realm, class, race, level, faction, guild, what is worn (item ids, quality, icon, item level, the item links with their enchant / suffix), the stats summary,
    // talents (points spent, the nodes with points, our build code when the game data allows), professions with their skill, hunter pets.
    // NEVER: bags, bank, guild bank, gold, mail, experience, the zone, recipes (the Hub does not record them), anything about the account.
    public static class ArmoryShare
    {
        public const int MaxChars = 30;

        static Dictionary<string, object> Obj() => new Dictionary<string, object>();

        // the characters of this PC that can be shared: the ones the Hub has a record of
        public static List<InvChar> Local(string wowRoot)
        {
            HubModel hub;
            try { hub = HubData.Read(wowRoot); }
            catch (Exception) { return new List<InvChar>(); }
            return LocalFrom(hub);
        }

        public static List<InvChar> LocalFrom(HubModel hub)
        {
            if (hub == null || hub.Characters.Count == 0) return new List<InvChar>();
            return CharacterMerge.Merge(hub, null).Chars.Where(c => c.Hub != null && !c.Hidden && c.Level > 0 && !string.IsNullOrEmpty(c.Name)).ToList();
        }

        static string Letters(string s, bool upper)
        {
            var r = new string((s ?? "").Where(char.IsLetter).ToArray());
            return upper ? r.ToUpperInvariant() : r;
        }

        // the snapshot of one character, or null when there is nothing to show (no gear, talents or professions). `withTimes` = false for the signature.
        public static Dictionary<string, object> Build(InvChar c, bool withTimes = true)
        {
            if (c == null || c.Hub == null || c.Level < 1) return null;
            var core = c.Core;
            var cls = Letters(!string.IsNullOrEmpty(core?.ClassFile) ? core.ClassFile : c.Class, true);
            if (cls.Length == 0 || string.IsNullOrEmpty(c.Name)) return null;
            var o = Obj();
            o["name"] = c.Name;
            if (!string.IsNullOrEmpty(c.Realm)) o["realm"] = c.Realm;
            o["class"] = cls;
            if (!string.IsNullOrEmpty(core?.Class)) o["classText"] = core.Class;
            if (!string.IsNullOrEmpty(core?.Race)) o["race"] = core.Race;
            var rf = Letters(!string.IsNullOrEmpty(core?.RaceFile) ? core.RaceFile : c.RaceFile, false);
            if (rf.Length > 0) o["raceFile"] = rf;
            if (c.Sex >= 2) o["sex"] = c.Sex;
            if (!string.IsNullOrEmpty(c.Faction)) o["faction"] = c.Faction;
            if (!string.IsNullOrEmpty(c.Guild)) o["guild"] = c.Guild;
            o["level"] = c.Level;

            var gear = HubModules.GearOf(c.Hub);
            if (gear != null)
            {
                var il = AvgIlvl(gear);
                if (il > 0) o["ilvl"] = Math.Round(il, 1);
                o["gear"] = GearOf(gear, withTimes);
            }
            var tal = HubModules.TalentsOf(c.Hub);
            if (tal != null && (tal.Nodes.Count > 0)) o["talents"] = TalentsOf(tal, cls, c.Level, withTimes);
            var pro = HubModules.ProfessionsOf(c.Hub);
            if (pro != null && pro.List.Count > 0) o["professions"] = ProfessionsOf(pro, withTimes);
            var pets = HubModules.PetsOf(c.Hub);
            if (pets != null && pets.Pets.Count > 0) o["pets"] = PetsOf(pets, withTimes);
            if (!o.ContainsKey("gear") && !o.ContainsKey("talents") && !o.ContainsKey("professions")) return null;
            if (withTimes) o["at"] = Math.Max(c.Updated, 0);
            return o;
        }

        // item level: the Hub's average, else the mean of the worn items' levels (the game's own item level, else the base level from the player's game files)
        public static double AvgIlvl(HubGearModule g)
        {
            if (g.Ilvl > 0) return g.Ilvl;
            int Il(HubItem it) => it.Il > 0 ? it.Il : (ItemData.Get(it.I)?.ItemLevel ?? 0);
            var levels = g.Slots.Values.Select(Il).Where(v => v > 0).ToList();
            return levels.Count == 0 ? 0 : levels.Average();
        }

        static Dictionary<string, object> GearOf(HubGearModule g, bool withTimes)
        {
            var o = Obj();
            if (withTimes) o["at"] = g.At;
            if (g.Ilvl > 0) o["ilvl"] = Math.Round(g.Ilvl, 1);
            var slots = Obj();
            foreach (var kv in g.Slots)
            {
                var s = Obj();
                s["i"] = kv.Value.I; s["q"] = kv.Value.Q;
                if (kv.Value.Ic > 0) s["ic"] = kv.Value.Ic;
                if (kv.Value.Il > 0) s["il"] = kv.Value.Il;
                slots[kv.Key.ToString(CultureInfo.InvariantCulture)] = s;
            }
            o["slots"] = slots;
            var worn = new HashSet<int>(g.Slots.Values.Select(i => i.I));
            var links = Obj();
            foreach (var kv in g.Links.OrderBy(k => k.Key))
                if (worn.Contains(kv.Key) && links.Count < 40 && !string.IsNullOrEmpty(kv.Value) && kv.Value.Length <= 220) links[kv.Key.ToString(CultureInfo.InvariantCulture)] = kv.Value;
            if (links.Count > 0) o["links"] = links;
            var st = g.Stats;
            if (st != null)
            {
                var so = Obj();
                void N(string k, double? v) { if (v != null) so[k] = Math.Round(v.Value, 2); }
                N("health", st.Health); N("power", st.Power); N("str", st.Str); N("agi", st.Agi); N("sta", st.Sta); N("int", st.Int); N("spi", st.Spi); N("armor", st.Armor);
                N("ap", st.Ap); N("rap", st.Rap); N("crit", st.Crit); N("rcrit", st.Rcrit); N("dodge", st.Dodge); N("parry", st.Parry); N("block", st.Block); N("speed", st.Speed);
                if (!string.IsNullOrEmpty(st.PowerType)) so["powerType"] = st.PowerType;
                Dictionary<string, object> Dmg(HubDamage d)
                {
                    if (d == null) return null;
                    var x = Obj();
                    if (d.Min != null) x["min"] = Math.Round(d.Min.Value, 2);
                    if (d.Max != null) x["max"] = Math.Round(d.Max.Value, 2);
                    if (d.Speed != null) x["speed"] = Math.Round(d.Speed.Value, 2);
                    return x;
                }
                var me = Dmg(st.Melee); if (me != null) so["melee"] = me;
                var ra = Dmg(st.Ranged); if (ra != null) so["ranged"] = ra;
                if (so.Count > 0) o["stats"] = so;
            }
            return o;
        }

        static Dictionary<string, object> TalentsOf(HubTalentsModule t, string classFile, int level, bool withTimes)
        {
            var o = Obj();
            if (withTimes) o["at"] = t.At;
            if (!string.IsNullOrEmpty(t.Api)) o["api"] = t.Api;
            o["spent"] = t.Spent > 0 ? t.Spent : t.Nodes.Sum(n => n.R);
            o["unspent"] = t.Unspent;
            var build = BuildCodeOf(t, classFile, level);
            if (build != null) o["build"] = build;
            var nodes = new List<object>();
            foreach (var n in t.Nodes.Where(n => n.R > 0).Take(400))
            {
                var e = Obj();
                e["id"] = n.Id; e["r"] = n.R;
                if (n.M > 0) e["m"] = n.M;
                if (n.S > 0) e["s"] = n.S;
                if (n.Ic > 0) e["ic"] = n.Ic;
                if (n.HasPos) { e["x"] = n.X; e["y"] = n.Y; }
                if (!string.IsNullOrEmpty(n.N)) e["n"] = n.N.Length > 48 ? n.N.Substring(0, 48) : n.N;
                nodes.Add(e);
            }
            o["nodes"] = nodes;
            return o;
        }

        // our build code (ETB1-...) when the game data of this PC knows the class's tree; null otherwise (the nodes are shared either way)
        public static string BuildCodeOf(HubTalentsModule t, string classFile, int level)
        {
            try
            {
                var tree = TalentData.Data?.ForClassFile(classFile);
                if (tree == null) return null;
                var loaded = CharacterTalents.Load(tree, t, level, n => null);
                if (loaded.Placed <= 0) return null;
                return BuildCode.Encode(tree, loaded.State);
            }
            catch (Exception) { return null; }
        }

        static Dictionary<string, object> ProfessionsOf(HubProfessionsModule p, bool withTimes)
        {
            var o = Obj();
            if (withTimes) o["at"] = p.At;
            var list = new List<object>();
            foreach (var x in p.List.Take(12))
            {
                var e = Obj();
                e["name"] = x.Name; e["skill"] = x.Skill; e["max"] = x.Max; e["kind"] = x.Kind == "secondary" ? "secondary" : "primary";
                if (x.Ic > 0) e["ic"] = x.Ic;
                list.Add(e);
            }
            o["list"] = list;
            return o;
        }

        static Dictionary<string, object> PetsOf(HubPetsModule m, bool withTimes)
        {
            var o = Obj();
            if (withTimes) o["at"] = m.At;
            if (!string.IsNullOrEmpty(m.Active)) o["active"] = m.Active;
            var pets = new List<object>();
            foreach (var p in m.Pets.Take(HubModules.MaxPets))
            {
                var e = Obj();
                e["key"] = p.Key; e["name"] = p.Name;
                if (!string.IsNullOrEmpty(p.Family)) e["family"] = p.Family;
                if (!string.IsNullOrEmpty(p.FamilyId)) e["familyId"] = p.FamilyId;
                if (!string.IsNullOrEmpty(p.LoyaltyText)) e["loyaltyText"] = p.LoyaltyText;
                e["level"] = p.Level; e["loyalty"] = p.Loyalty; e["stable"] = p.Stable; e["happiness"] = p.Happiness;
                if (p.Ic > 0) e["ic"] = p.Ic;
                if (p.TpTotal >= 0) { e["tpTotal"] = p.TpTotal; e["tpSpent"] = Math.Max(0, p.TpSpent); }
                var abs = new List<object>();
                foreach (var a in p.Abilities.Take(HubModules.MaxPetAbilities))
                {
                    var ae = Obj();
                    if (!string.IsNullOrEmpty(a.Id)) ae["id"] = a.Id;
                    ae["name"] = a.Name; ae["rank"] = a.Rank;
                    abs.Add(ae);
                }
                e["abilities"] = abs;
                pets.Add(e);
            }
            o["pets"] = pets;
            if (m.Known.Count > 0) { var k = Obj(); foreach (var kv in m.Known) k[kv.Key] = kv.Value; o["known"] = k; }
            return o;
        }

        // a short fingerprint of what would be uploaded, without the "saved at" times, so a re-save by the game that changed nothing is not an upload
        public static string Signature(Dictionary<string, object> snapWithoutTimes)
        {
            using (var sha = SHA1.Create())
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(Json.Write(snapWithoutTimes))).Take(8).Select(b => b.ToString("x2")));
        }

        // ---- the other way: a snapshot from the Lodge -> the model the Characters page draws (the same readers, so the same views)

        // Lua-style tables: lists become { ["1"] = ..., ["2"] = ... } like the Hub's own SavedVariables, which is what HubModules reads
        static object LuaShape(object v)
        {
            if (v is List<object> l)
            {
                var d = new Dictionary<string, object>();
                for (int i = 0; i < l.Count; i++) d[(i + 1).ToString(CultureInfo.InvariantCulture)] = LuaShape(l[i]);
                return d;
            }
            if (v is Dictionary<string, object> o)
            {
                var d = new Dictionary<string, object>();
                foreach (var kv in o) d[kv.Key] = LuaShape(kv.Value);
                return d;
            }
            return v;
        }

        static HubModule Mod(string name, long at, Dictionary<string, object> data) => new HubModule { Name = name, V = 1, At = at, By = "Lodge", Ver = "", Data = data };

        public static InvChar ToChar(string owner, Dictionary<string, object> snap)
        {
            if (snap == null) return null;
            var key = owner + "|" + (snap.Str("realm") ?? "") + "-" + (snap.Str("name") ?? "");
            var h = new HubChar { Key = key, At = snap.Long("at") };
            var core = new Dictionary<string, object>
            {
                ["name"] = snap.Str("name") ?? "", ["realm"] = snap.Str("realm") ?? "", ["class"] = snap.Str("classText") ?? "", ["classFile"] = snap.Str("class") ?? "",
                ["race"] = snap.Str("race") ?? "", ["raceFile"] = snap.Str("raceFile") ?? "", ["sex"] = (double)snap.Int("sex"), ["faction"] = snap.Str("faction") ?? "",
                ["level"] = (double)snap.Int("level"), ["guild"] = snap.Str("guild") ?? "",
            };
            h.Modules["core"] = Mod("core", h.At, core);
            var gear = snap.Child("gear");
            if (gear != null) h.Modules["gear"] = Mod("gear", gear.Long("at"), (Dictionary<string, object>)LuaShape(gear));
            var tal = snap.Child("talents");
            if (tal != null) h.Modules["talents"] = Mod("talents", tal.Long("at"), (Dictionary<string, object>)LuaShape(tal));
            var pro = snap.Child("professions");
            if (pro != null) h.Modules["professions"] = Mod("professions", pro.Long("at"), (Dictionary<string, object>)LuaShape(pro));
            var pets = snap.Child("pets");
            if (pets != null)
            {
                var data = new Dictionary<string, object>();
                foreach (var kv in pets) if (kv.Key != "pets") data[kv.Key] = LuaShape(kv.Value);
                var pd = new Dictionary<string, object>();
                int n = 0;
                foreach (var po in pets.List("pets"))
                {
                    if (!(po is Dictionary<string, object> p)) continue;
                    var k = p.Str("key");
                    if (string.IsNullOrEmpty(k) || pd.ContainsKey(k)) k = "pet" + (++n);
                    pd[k] = LuaShape(p);
                }
                data["pets"] = pd;
                h.Modules["hunter.pets"] = Mod("hunter.pets", pets.Long("at"), data);
            }
            var model = new HubModel { Found = true, Schema = 1 };
            model.Characters.Add(h);
            var c = CharacterMerge.Merge(model, null).Chars.FirstOrDefault();
            if (c == null) return null;
            c.Key = key;
            return c;
        }

        // the build code of a shared character (null when it did not come with one)
        public static string BuildOf(Dictionary<string, object> snap)
        {
            var b = snap?.Child("talents")?.Str("build");
            return string.IsNullOrEmpty(b) ? null : b;
        }
    }
}
