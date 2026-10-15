using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ElansAddonHub.Services.Db2;

namespace ElansAddonHub.Services.Upgrades
{
    // The character the engine looks at: plain values, nothing from the window. Built from the merged character (InvChar) by UpgradeChar.From.
    public sealed class UpgradeChar
    {
        public string Key = "", Name = "", ClassFile = "", Race = "", Faction = "";   // Faction: "Alliance" | "Horde" | "" (then the race decides)
        public int Level;
        public int Spec;                                                              // 0 = not known (the class's levelling weights), 1..3 = the talent tree
        public Dictionary<int, int> Worn = new Dictionary<int, int>();                // paper-doll slot (1..18) -> item id
        public bool WornKnown = true;                                                 // false = no gear module: nothing is known about what is worn
        // what the Hub saved about professions: skill line id -> rank. SkillsKnown = the professions module was read (a profession missing from it is then really not learned)
        public bool SkillsKnown, SkillsUnlined;                                       // SkillsUnlined: an entry without a skill line id (older Hub) that is told apart by its name only
        public Dictionary<int, int> Skills = new Dictionary<int, int>();
        public Dictionary<string, int> SkillsByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        // reputations: faction id -> standing index (3 Neutral .. 7 Exalted). No Hub module saves them yet, so this stays unknown (then a reputation limit is only a note)
        public bool ReputationKnown;
        public Dictionary<int, int> Reputation = new Dictionary<int, int>();
        public int PvpRank = -1;                                                      // -1 = not known
        public const int SkillUnknown = -2;
        ulong raceBit = ulong.MaxValue; int classMask = -1;
        public ulong RaceBit { get { if (raceBit == ulong.MaxValue) raceBit = UpgradeWorld.RaceBitOf(Race); return raceBit; } }
        public int ClassMask { get { if (classMask < 0) classMask = UpgradeWorld.ClassMaskOf(ClassFile); return classMask; } }

        // the character's rank in a profession; SkillUnknown when the professions are not known or the skill is not one the Hub lists
        public int SkillRank(int line, string name, bool isProfession)
        {
            if (!SkillsKnown || !isProfession) return SkillUnknown;
            if (Skills.TryGetValue(line, out var r)) return r;
            if (name != null && SkillsByName.TryGetValue(name, out r)) return r;
            return SkillsUnlined ? SkillUnknown : 0;
        }

        public Dictionary<int, int> Enchants = new Dictionary<int, int>();           // paper-doll slot -> enchant id read from the item link the Hub saved (0 / missing = none)

        public string Side
        {
            get
            {
                if (string.Equals(Faction, "Alliance", StringComparison.OrdinalIgnoreCase)) return "Alliance";
                if (string.Equals(Faction, "Horde", StringComparison.OrdinalIgnoreCase)) return "Horde";
                switch ((Race ?? "").Replace(" ", "").ToLowerInvariant())
                {
                    case "human": case "dwarf": case "nightelf": case "gnome": case "draenei": case "worgen": return "Alliance";
                    case "orc": case "scourge": case "undead": case "tauren": case "troll": case "bloodelf": case "goblin": return "Horde";
                    default: return "";
                }
            }
        }

        public string Display => Name + " (" + Pretty(ClassFile) + ")";
        public static string Pretty(string cls) => string.IsNullOrEmpty(cls) ? "" : char.ToUpperInvariant(cls[0]) + cls.Substring(1).ToLowerInvariant();

        // the stable part of the cache key
        public string Fingerprint => Key + "|" + ClassFile + "|" + Race + "|" + Side + "|" + Level + "|" + Spec + "|" + string.Join(",", Worn.OrderBy(k => k.Key).Select(k => k.Key + ":" + k.Value)) + "|" + string.Join(",", Enchants.OrderBy(k => k.Key).Select(k => k.Key + "e" + k.Value)) + "|" + (SkillsKnown ? "s" + string.Join(",", Skills.OrderBy(k => k.Key).Select(k => k.Key + "=" + k.Value)) + string.Join(",", SkillsByName.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Key + "=" + k.Value)) : "-") + "|" + (ReputationKnown ? string.Join(",", Reputation.OrderBy(k => k.Key).Select(k => k.Key + "r" + k.Value)) : "-") + "|" + PvpRank;

        // the enchant id of an item link: "|Hitem:<id>:<enchant>:<gem1> ..." (0 = none or not a link)
        public static int EnchantOfLink(string link)
        {
            if (string.IsNullOrEmpty(link)) return 0;
            var m = System.Text.RegularExpressions.Regex.Match(link, @"item:\d+:(\d*)");
            return m.Success && m.Groups[1].Value.Length > 0 && int.TryParse(m.Groups[1].Value, out var n) && n > 0 ? n : 0;
        }

        public static UpgradeChar From(InvChar c)
        {
            if (c == null) return null;
            var u = new UpgradeChar
            {
                Key = c.Key ?? c.Name ?? "", Name = c.Name ?? "", ClassFile = (!string.IsNullOrEmpty(c.Core?.ClassFile) ? c.Core.ClassFile : c.Class ?? "").ToUpperInvariant(),
                Race = !string.IsNullOrEmpty(c.RaceFile) ? c.RaceFile : c.Core?.RaceFile ?? c.Core?.Race ?? "", Faction = !string.IsNullOrEmpty(c.Core?.Faction) ? c.Core.Faction : c.Faction ?? "", Level = c.Level,
            };
            if (u.Level <= 0 && c.Core != null) u.Level = c.Core.Level;
            if (c.Hub != null && HubModules.StateOf(c.Hub, "gear") == HubModState.Ok)
            {
                var g = HubModules.GearOf(c.Hub);
                if (g != null)
                    foreach (var kv in g.Slots)
                        if (kv.Value.I > 0 && kv.Key >= 1 && kv.Key <= 18 && kv.Key != 4)
                        {
                            u.Worn[kv.Key] = kv.Value.I;
                            if (g.Links.TryGetValue(kv.Value.I, out var link)) { int en = EnchantOfLink(link); if (en > 0) u.Enchants[kv.Key] = en; }
                        }
            }
            else u.WornKnown = false;
            var prof = c.Hub != null ? HubModules.ProfessionsOf(c.Hub) : null;
            if (prof != null)
            {
                u.SkillsKnown = true;
                foreach (var p in prof.List)
                {
                    if (p.Line > 0) u.Skills[p.Line] = Math.Max(u.Skills.TryGetValue(p.Line, out var o) ? o : 0, p.Skill);
                    else if (!string.IsNullOrEmpty(p.Name)) { u.SkillsByName[p.Name] = Math.Max(u.SkillsByName.TryGetValue(p.Name, out var o2) ? o2 : 0, p.Skill); u.SkillsUnlined = true; }
                }
            }
            if (c.Hub != null && HubModules.StateOf(c.Hub, "talents") == HubModState.Ok) u.Spec = UpgradeWeights.DetectSpec(HubModules.TalentsOf(c.Hub));
            return u;
        }
    }

    // What the person asked to see (the filters of the panel). Saved as one short text in the settings.
    public sealed class UpgradeFilters
    {
        public bool Dungeons = true, Raids, Classic = true, TradeableOnly;
        public bool Enchants = true, OnlyApplicable, Food = true, ShowBlocked = true;      // the Enchant rows, "only enchants I or my Lodge can apply", the Food & drink section
        public int LevelOffset;                    // 0 = only what can be worn now; 3 = also what is a few levels away (listed apart)
        public int PerSlot = 3;
        public double RareChance = 2;              // a drop under this many percent counts as rare: ranked low

        public string Serialize() => "D" + (Dungeons ? 1 : 0) + "R" + (Raids ? 1 : 0) + "C" + (Classic ? 1 : 0) + "T" + (TradeableOnly ? 1 : 0) + "L" + LevelOffset + "N" + (Enchants ? 1 : 0) + "A" + (OnlyApplicable ? 1 : 0) + "F" + (Food ? 1 : 0) + (ShowBlocked ? "" : "U0") + (HiddenGroups.Count > 0 ? "H" + string.Join("+", HiddenGroups.OrderBy(g => g, StringComparer.Ordinal)) : "");
        public static UpgradeFilters Parse(string s)
        {
            var f = new UpgradeFilters();
            if (string.IsNullOrEmpty(s)) return f;
            bool B(char key, bool def) { int i = s.IndexOf(key); return i >= 0 && i + 1 < s.Length && (s[i + 1] == '0' || s[i + 1] == '1') ? s[i + 1] == '1' : def; }
            f.Dungeons = B('D', true); f.Raids = B('R', false); f.Classic = B('C', true); f.TradeableOnly = B('T', false);
            f.Enchants = B('N', true); f.OnlyApplicable = B('A', false); f.Food = B('F', true); f.ShowBlocked = B('U', true);
            int h = s.IndexOf('H');
            if (h >= 0) foreach (var g in s.Substring(h + 1).Split('+')) if (Array.IndexOf(UpgradeExtras.Groups, g) >= 0) f.HiddenGroups.Add(g);
            int l = s.IndexOf('L');
            if (l >= 0 && l + 1 < s.Length && s[l + 1] == '3') f.LevelOffset = 3;
            return f;
        }
        public HashSet<string> HiddenGroups = new HashSet<string>();     // consumable groups the person collapsed (food, drink, elixir ...)
        public UpgradeFilters Clone() { var c = (UpgradeFilters)MemberwiseClone(); c.HiddenGroups = new HashSet<string>(HiddenGroups); return c; }
    }

    // Where an item can be had, in the Atlas's own terms.
    public sealed class UpgradeSource
    {
        public string Kind = "";                   // "drop" | "vendor" | "quest" | "craft" | "container" | "fishing"
        public string Link = "";                   // Atlas page of the creature / npc / quest / spell / object ("" = none)
        public string What = "", Zone = "";        // "Defias Pillager", "Westfall"
        public int Level;                          // creature / quest level, 0 = not known
        public double Chance;                      // percent, drops only
        public bool Lodge;                         // seen by the Lodge (the game's own server), otherwise the Classic baseline
        public bool Dungeon, Raid;
        public long Price;                         // copper, vendors the Lodge saw; 0 = not known
        public bool PriceSpecial;                  // a price that is more than copper (a token, an item)
        public string Profession = "";             // crafts: "Leatherworking 110"
        public int Skill;
        public List<string> Makers = new List<string>();      // crafts: "Bob can make it", "You can make it"
        public bool CanCraftNow;                   // the shown character knows the recipe
        public bool LodgeMaker;                    // a Lodge member can make it
        public bool OwnMaker;                      // another character of mine knows the recipe
        public int Spell;                          // the recipe spell
        public int ProfessionSkill = -1;           // the shown character's skill in that profession, -1 = does not have it
        public int ZoneMap;                        // uiMapId for "Show on world map", 0 = none
        public double Fx = -1, Fy = -1;            // where on that map (fractions), -1 = the zone only
        public int NeedsLevel;                     // quests: the level the quest asks for when it is above the character's
        public double Reach;                       // 0..1, how reachable this is for the character
        public string Note = "";

        public string Label => Lodge ? "Lodge" : "Classic baseline";

        // "Drop · Defias Pillager · Westfall · 4% (Lodge)"
        public string Line
        {
            get
            {
                var inv = CultureInfo.InvariantCulture;
                var parts = new List<string>();
                switch (Kind)
                {
                    case "drop": parts.Add("Drop"); break;
                    case "vendor": parts.Add("Vendor"); break;
                    case "quest": parts.Add("Quest"); break;
                    case "craft": parts.Add("Crafted"); break;
                    case "container": parts.Add("Container"); break;
                    case "fishing": parts.Add("Fishing"); break;
                    default: parts.Add(Kind); break;
                }
                if (Kind == "craft")
                {
                    parts.Add(Profession);
                    if (Makers.Count > 0) parts.Add(string.Join(", ", Makers));
                    else parts.Add("learn it first");
                    return string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));
                }
                if (Kind == "quest") { parts.Add(What); if (Level > 0) parts.Add("lvl " + Level); if (!string.IsNullOrEmpty(Zone)) parts.Add(Zone); }
                else
                {
                    parts.Add(What);
                    if (!string.IsNullOrEmpty(Zone)) parts.Add(Zone);
                    if (Kind == "vendor") { if (PriceSpecial) parts.Add("special cost"); else if (Price > 0) parts.Add(Money(Price)); }
                    string tag = Lodge ? " (Lodge)" : " (Classic)";
                    if (Chance > 0) parts.Add(Percent(Chance) + tag);
                    else if (Kind == "vendor" || Kind == "container") parts[parts.Count - 1] += tag;
                }
                if (!string.IsNullOrEmpty(Note)) parts.Add(Note);
                return string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));
            }
        }

        public static string Percent(double p) => (p >= 10 ? Math.Round(p).ToString("0", CultureInfo.InvariantCulture) : p >= 1 ? p.ToString("0.#", CultureInfo.InvariantCulture) : p.ToString("0.##", CultureInfo.InvariantCulture)) + "%";

        public static string Money(long copper)
        {
            long g = copper / 10000, s = copper / 100 % 100, c = copper % 100;
            var l = new List<string>();
            if (g > 0) l.Add(g.ToString("N0", CultureInfo.GetCultureInfo("en-US")) + "g");
            if (s > 0) l.Add(s + "s");
            if (c > 0 || l.Count == 0) l.Add(c + "c");
            return string.Join(" ", l);
        }
    }

    // One better item for one slot.
    public sealed class UpgradeCandidate
    {
        public ClientItem Item;
        public int Slot;
        public double Score, OldScore;
        public double Gain => Score - OldScore;
        public double? Pct;                         // null = the slot is empty (everything counts)
        public string Delta = "";                   // "+12 Agility, +6 Stamina, +18 armor (score +14%)"
        public string EnchantNote = "";             // the best enchant for this item: "Enchant Boots - Agility: +5 Agility"
        public string DeltaStats = "";              // the same without the score
        public bool Soon;                           // needs a higher level than the character has (up to +3)
        public int NeedsLevel;                      // the level it needs (Soon items)
        public bool ReplacesOffhand;                // a two-hand weapon takes the off hand slot too
        public bool Revealed;                       // known only from a Lodge member's game
        public bool Blocked;                        // the character cannot use it (wrong class or race, a profession, a reputation ...): listed greyed, never a "best upgrade"
        public string BlockReason = "";             // "Rogue only", "Needs Engineering 245"
        public string Requirement = "";             // a limit that could not be checked ("Needs Revered with Argent Dawn"): a note, the item is not greyed
        public List<UpgradeSource> Sources = new List<UpgradeSource>();
        public double Metric;                       // the gain in percent (a share of the slot's best item for an empty slot)
        public double Reach;                        // the best source's reach
        public double Rank;                         // what the list is sorted by
        public int OldItemId;                       // what it is compared to (0 = nothing worn)
        public bool Tradeable => Item != null && Item.Bonding != 1 && Item.Bonding != 4 && Item.Bonding != 5;
        public UpgradeCandidate Copy() => (UpgradeCandidate)MemberwiseClone();
        public UpgradeSource Best => Sources.OrderByDescending(s => s.Reach).FirstOrDefault();
    }

    public sealed class UpgradeSlotResult
    {
        public int Slot;
        public string SlotName = "";
        public int WornId;
        public List<UpgradeCandidate> Upgrades = new List<UpgradeCandidate>();    // top ones, usable now
        public List<UpgradeCandidate> Soon = new List<UpgradeCandidate>();        // a few levels away (only when asked for)
        public List<UpgradeCandidate> Blocked = new List<UpgradeCandidate>();     // better, but this character cannot use them: greyed, after the usable ones
        public int BlockedHidden;                                                  // the same, left out because "Show items I can't use" is off
        public int Hidden;                                                         // better items that the filters hide
        public string Note = "";                                                   // "You wield a two-hand weapon."
        public string EnchantCurrent = "";                                         // "No enchant" / "Enchanted: +3 Strength"; "" = nothing can be enchanted there
        public bool NoEnchant;                                                     // the worn item could be enchanted and is not
        public List<ExtraEntry> Enchants = new List<ExtraEntry>();                  // the enchants that beat the current one, best first
    }

    // Everything one run found for one character, before the filters.
    public sealed class UpgradeSet
    {
        public UpgradeChar Who;
        public string WeightsText = "";
        public string SpecName = "";
        public readonly Dictionary<int, List<UpgradeCandidate>> BySlot = new Dictionary<int, List<UpgradeCandidate>>();
        public int Considered;                       // items looked at
        public bool TwoHandWorn;                     // the main hand holds a two-hand weapon
        public readonly Dictionary<int, SlotEnchants> EnchantBySlot = new Dictionary<int, SlotEnchants>();
        public List<ExtraEntry> Food = new List<ExtraEntry>();     // well fed / healing food / drinks, all kinds, before the filters
        public string EnchantProblem = "";           // "" = the enchant tables were read; else why not
        public bool ShowsFood;                       // the class has weights and the item data was read
        public int WithoutSource;                    // better items with no known place to get them
        public bool GameData, ClassicData, LodgeData;
        public string Problem = "";                  // "" = fine
        public List<string> Notes = new List<string>();
        public long BuiltMs;
    }
}
