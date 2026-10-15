using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ElansAddonHub.Services.Db2;

namespace ElansAddonHub.Services.Upgrades
{
    // ============================================================================================================================
    // What an item is worth to a character: a weight per stat, per class and talent tree ("spec"). The defaults below are plain, round numbers meant
    // for levelling and everyday play, not a theory-crafted optimum; the person can change any of them (Settings stores only what was changed).
    // A weight is the value of ONE point of the stat, relative to one point of the main stat (Agility for a Hunter = 1.0).
    // ============================================================================================================================
    public static class UpgradeWeights
    {
        // the keys, in the order the editor lists them, with their labels
        public static readonly KeyValuePair<string, string>[] Keys =
        {
            P("str", "Strength"), P("agi", "Agility"), P("sta", "Stamina"), P("int", "Intellect"), P("spi", "Spirit"),
            P("ap", "Attack power"), P("crit", "Critical strike"), P("hit", "Hit"), P("haste", "Haste"), P("expertise", "Expertise"), P("pen", "Penetration"),
            P("spell", "Spell damage"), P("heal", "Healing"), P("mp5", "Mana per 5 sec."), P("hp5", "Health per 5 sec."),
            P("defense", "Defense"), P("dodge", "Dodge"), P("parry", "Parry"), P("block", "Block"), P("resil", "Resilience"),
            P("armor", "Armor"), P("dps", "Melee weapon DPS"), P("rdps", "Ranged weapon DPS"), P("res", "Resistances"), P("hp", "Health and mana"),
        };
        static KeyValuePair<string, string> P(string k, string l) => new KeyValuePair<string, string>(k, l);
        public static string Label(string key) => Keys.Where(k => k.Key == key).Select(k => k.Value).FirstOrDefault() ?? key;
        public static bool IsKey(string key) => Keys.Any(k => k.Key == key);

        // item stat type (ItemNames.Stat) -> weight key; null = not scored
        public static string KeyOfStat(int type)
        {
            switch (type)
            {
                case 3: return "agi";
                case 4: return "str";
                case 5: return "int";
                case 6: return "spi";
                case 7: return "sta";
                case 12: return "defense";
                case 13: return "dodge";
                case 14: return "parry";
                case 15: case 48: return "block";
                case 16: case 17: case 18: case 31: return "hit";
                case 19: case 20: case 21: case 32: return "crit";
                case 35: return "resil";
                case 36: return "haste";
                case 37: return "expertise";
                case 38: case 39: return "ap";
                case 41: return "heal";
                case 42: case 45: return "spell";
                case 43: return "mp5";
                case 44: case 47: return "pen";
                case 46: return "hp5";
                case 50: return "armor";
                case 0: case 1: return "hp";
                case 51: case 52: case 53: case 54: case 55: case 56: return "res";
                default: return null;
            }
        }

        // ---- the defaults. Class file ("HUNTER") + spec 0 (not known) or 1..3 (the talent tree, left to right in the game).
        public static readonly string[] Classes = { "WARRIOR", "PALADIN", "HUNTER", "ROGUE", "PRIEST", "SHAMAN", "MAGE", "WARLOCK", "DRUID" };

        static readonly Dictionary<string, string[]> SpecNames = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["WARRIOR"] = new[] { "Arms", "Fury", "Protection" },
            ["PALADIN"] = new[] { "Holy", "Protection", "Retribution" },
            ["HUNTER"] = new[] { "Beast Mastery", "Marksmanship", "Survival" },
            ["ROGUE"] = new[] { "Assassination", "Combat", "Subtlety" },
            ["PRIEST"] = new[] { "Discipline", "Holy", "Shadow" },
            ["SHAMAN"] = new[] { "Elemental", "Enhancement", "Restoration" },
            ["MAGE"] = new[] { "Arcane", "Fire", "Frost" },
            ["WARLOCK"] = new[] { "Affliction", "Demonology", "Destruction" },
            ["DRUID"] = new[] { "Balance", "Feral", "Restoration" },
        };
        // when the talents are not known (or the points are spread): the build most people level in
        static readonly Dictionary<string, int> DefaultSpec = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["WARRIOR"] = 2, ["PALADIN"] = 3, ["HUNTER"] = 1, ["ROGUE"] = 2, ["PRIEST"] = 3, ["SHAMAN"] = 2, ["MAGE"] = 1, ["WARLOCK"] = 3, ["DRUID"] = 2,
        };

        public static int DefaultSpecOf(string cls) => cls != null && DefaultSpec.TryGetValue(cls, out var s) ? s : 1;
        public static string SpecName(string cls, int spec) => cls != null && SpecNames.TryGetValue(cls, out var n) && spec >= 1 && spec <= n.Length ? n[spec - 1] : null;
        public static bool Known(string cls) => cls != null && SpecNames.ContainsKey(cls);

        // a compact way to write a preset: "agi=1,sta=.45"
        static Dictionary<string, double> W(string s, Dictionary<string, double> basis = null)
        {
            var d = basis == null ? new Dictionary<string, double>() : new Dictionary<string, double>(basis);
            foreach (var part in s.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('=');
                d[kv[0].Trim()] = double.Parse(kv[1], CultureInfo.InvariantCulture);
            }
            return d;
        }

        static readonly Dictionary<string, Dictionary<string, double>[]> Presets = BuildPresets();

        static Dictionary<string, Dictionary<string, double>[]> BuildPresets()
        {
            var res = new Dictionary<string, Dictionary<string, double>[]>(StringComparer.OrdinalIgnoreCase);
            const string tail = ",res=.1,hp=.02,resil=.1";
            var hunter = W("agi=1,str=.2,sta=.45,int=.35,spi=.05,ap=.5,crit=1.3,hit=1.4,haste=.8,mp5=.3,armor=.02,rdps=14,dps=1.5,defense=0,expertise=.5" + tail);
            res["HUNTER"] = new[] { hunter, W("crit=1.4", hunter), W("agi=1.1,sta=.55", hunter) };
            var warFury = W("str=1,agi=.65,sta=.45,ap=.5,crit=1.2,hit=1.4,haste=.8,expertise=1,armor=.03,dps=3.5,pen=.5,defense=.05" + tail);
            res["WARRIOR"] = new[] { W("dps=4", warFury), warFury,
                W("sta=1,defense=1.2,dodge=1,parry=1,block=.6,str=.3,agi=.5,ap=.1,armor=.08,dps=.8,hit=.5,crit=.1,expertise=.4,resil=.1,res=.1,hp=.02") };
            var ret = W("str=1,agi=.5,sta=.35,int=.1,ap=.5,crit=1.1,hit=1.3,haste=.7,expertise=1,spell=.1,armor=.02,dps=5" + tail);
            res["PALADIN"] = new[] { W("int=.9,heal=1,spell=.4,mp5=1.2,crit=.6,sta=.25,spi=.2,haste=.5,armor=.01,res=.1,hp=.02,resil=.1"),
                W("sta=1,defense=1.1,dodge=.9,parry=.9,block=.6,str=.3,agi=.3,spell=.6,hit=.3,armor=.08,dps=.6,int=.1,expertise=.4,res=.1,hp=.02,resil=.1"), ret };
            var rogue = W("agi=1,str=.35,sta=.4,ap=.5,crit=1.2,hit=1.5,haste=.8,expertise=1,armor=.02,dps=3,pen=.5" + tail);
            res["ROGUE"] = new[] { W("dps=3.5", rogue), W("hit=1.6,dps=3.5", rogue), rogue };
            var heal = "int=.8,heal=1,spell=.45,mp5=1,spi=.4,sta=.2,crit=.5,haste=.5,armor=.01,rdps=1,res=.1,hp=.02,resil=.1";
            res["PRIEST"] = new[] { W(heal), W("spi=.6,mp5=1.1", W(heal)), W("spell=1,int=.35,sta=.35,hit=1.3,crit=.9,spi=.2,haste=.6,mp5=.2,rdps=2,armor=.01,res=.1,hp=.02,resil=.1") };
            res["SHAMAN"] = new[] { W("spell=1,int=.4,sta=.3,crit=1,hit=1.3,haste=.6,mp5=.3,spi=.1,armor=.02,res=.1,hp=.02,resil=.1"),
                W("str=.8,agi=.7,sta=.4,ap=.5,crit=1.2,hit=1.4,haste=.7,expertise=1,int=.1,armor=.03,dps=3.5,pen=.4,res=.1,hp=.02,resil=.1"),
                W("heal=1,int=.8,mp5=1,spell=.35,sta=.25,spi=.2,crit=.5,haste=.5,armor=.03,res=.1,hp=.02,resil=.1") };
            var mage = W("spell=1,int=.4,sta=.35,crit=.9,hit=1.3,haste=.6,spi=.2,mp5=.3,rdps=2,armor=.01,res=.1,hp=.02,resil=.1,pen=.3");
            res["MAGE"] = new[] { W("int=.5,hit=1.3,crit=.8", mage), W("crit=1.1", mage), W("hit=1.2,crit=.9", mage) };
            var lock1 = W("spell=1,sta=.5,int=.3,hit=1.3,crit=.8,haste=.6,spi=.1,mp5=.2,rdps=2,armor=.01,res=.1,hp=.02,resil=.1,pen=.3");
            res["WARLOCK"] = new[] { W("hit=1.4", lock1), W("sta=.55", lock1), W("crit=1", lock1) };
            var feral = W("agi=1,str=.8,sta=.6,ap=.5,crit=1.1,hit=1.2,haste=.7,expertise=.8,int=.1,armor=.05,dps=.3,pen=.4,res=.1,hp=.02,resil=.1");
            res["DRUID"] = new[] { W("spell=1,int=.4,crit=1,hit=1.3,haste=.6,mp5=.3,spi=.2,sta=.3,armor=.01,res=.1,hp=.02,resil=.1"), feral,
                W("heal=1,int=.8,mp5=1,spi=.5,spell=.35,sta=.2,crit=.5,haste=.5,armor=.01,res=.1,hp=.02,resil=.1") };
            return res;
        }

        // the default weights of a class and spec (spec 0 or unknown = the class's levelling build); never null, a class that is not known gets stamina and armor only
        public static Dictionary<string, double> DefaultsOf(string cls, int spec)
        {
            if (cls != null && Presets.TryGetValue(cls, out var list))
            {
                int s = spec >= 1 && spec <= 3 ? spec : DefaultSpecOf(cls);
                return new Dictionary<string, double>(list[s - 1]);
            }
            return W("sta=1,str=.5,agi=.5,int=.5,armor=.02,dps=2,rdps=2");
        }

        // ---- the person's changes: "<CLASS>/<spec>" -> "agi=1.2;sta=0.4" (only the keys that differ from the default are stored)
        public static string StoreKey(string cls, int spec) => (cls ?? "").ToUpperInvariant() + "/" + Math.Max(0, Math.Min(3, spec));

        public static Dictionary<string, double> Parse(string text)
        {
            var d = new Dictionary<string, double>();
            if (string.IsNullOrWhiteSpace(text)) return d;
            foreach (var part in text.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('=');
                if (kv.Length != 2) continue;
                var k = kv[0].Trim().ToLowerInvariant();
                if (!IsKey(k)) continue;
                if (double.TryParse(kv[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && !double.IsNaN(v) && !double.IsInfinity(v) && v >= 0 && v <= 1000) d[k] = v;
            }
            return d;
        }

        public static string Format(Dictionary<string, double> changes)
            => string.Join(";", Keys.Where(k => changes.ContainsKey(k.Key)).Select(k => k.Key + "=" + changes[k.Key].ToString("0.###", CultureInfo.InvariantCulture)));

        // where the person's changes live (set by the window from the settings; null = no changes saved anywhere)
        public static Dictionary<string, string> Overrides = new Dictionary<string, string>();
        public static Action SaveOverrides = () => { };

        public static void Bind(Dictionary<string, string> store, Action save) { Overrides = store ?? new Dictionary<string, string>(); SaveOverrides = save ?? (() => { }); }

        public static Dictionary<string, double> Effective(string cls, int spec) => Merge(DefaultsOf(cls, spec), Overrides, cls, spec);

        static Dictionary<string, double> Merge(Dictionary<string, double> basis, Dictionary<string, string> overrides, string cls, int spec)
        {
            if (overrides != null && overrides.TryGetValue(StoreKey(cls, spec >= 1 && spec <= 3 ? spec : DefaultSpecOf(cls)), out var text))
                foreach (var kv in Parse(text)) basis[kv.Key] = kv.Value;
            return basis;
        }

        // the spec the weights are kept under: 1..3, the class default when the talents say nothing
        public static int SpecKeyOf(string cls, int spec) => spec >= 1 && spec <= 3 ? spec : DefaultSpecOf(cls);

        // save the weights as the person set them: only the differences from the default are kept; equal to the default = nothing stored
        public static void Set(string cls, int spec, Dictionary<string, double> weights)
        {
            int s = SpecKeyOf(cls, spec);
            var def = DefaultsOf(cls, s);
            var diff = new Dictionary<string, double>();
            foreach (var k in Keys)
            {
                weights.TryGetValue(k.Key, out var v);
                def.TryGetValue(k.Key, out var d);
                if (Math.Abs(v - d) > 1e-9) diff[k.Key] = v;
            }
            var key = StoreKey(cls, s);
            if (diff.Count == 0) Overrides.Remove(key); else Overrides[key] = Format(diff);
            try { SaveOverrides(); } catch { }
        }

        public static void Reset(string cls, int spec) { Overrides.Remove(StoreKey(cls, SpecKeyOf(cls, spec))); try { SaveOverrides(); } catch { } }
        public static bool IsChanged(string cls, int spec) => Overrides.ContainsKey(StoreKey(cls, SpecKeyOf(cls, spec)));

        // a short fingerprint of the weights that count for this character (part of the cache key)
        public static string Fingerprint(string cls, int spec)
            => string.Join(",", Effective(cls, spec).OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Key + k.Value.ToString("0.###", CultureInfo.InvariantCulture)));

        // ---- the spec from the talents: the tree (left to right) with the most points; null/0 when the points are few, tied or the trees were not recognised
        public static int DetectSpec(HubTalentsModule talents)
        {
            if (talents == null || talents.Nodes.Count == 0) return 0;
            var lay = TalentGrid.Group(talents.Nodes);
            if (lay.Trees.Count != 3) return 0;
            var pts = lay.Trees.Select(t => t.Points).ToList();
            int best = pts.IndexOf(pts.Max());
            if (pts[best] < 5) return 0;
            if (pts.Count(p => p == pts[best]) > 1) return 0;
            return best + 1;
        }
    }
}
