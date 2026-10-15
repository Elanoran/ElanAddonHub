using System;
using System.Collections.Generic;
using System.Linq;
using ElansAddonHub.Services.Db2;

namespace ElansAddonHub.Services.Upgrades
{
    // What stops a character from using an item although the class and level allow it: the class and race lists of the item, a profession it asks for, a
    // reputation and a PvP rank. A limit that is KNOWN to be broken blocks the item (it is listed greyed, with the reason); one the Outpost cannot check
    // (the Hub saved no professions, no module has the reputations, ...) only becomes a note. Pure: no window, no files.
    public sealed class UpgradeVerdict
    {
        public static readonly UpgradeVerdict None = new UpgradeVerdict();
        public bool Blocked => Reasons.Count > 0;
        public readonly List<string> Reasons = new List<string>();     // why the character cannot use it: "Rogue only", "Needs Engineering 245"
        public readonly List<string> Notes = new List<string>();       // limits that could not be checked: "Needs Revered with Argent Dawn"
        public string Reason => string.Join(" · ", Reasons);
        public string Note => string.Join(" · ", Notes);
    }

    public static partial class UpgradeRules
    {
        // the classic class bits (class id - 1): warrior 1, paladin 2, hunter 4, rogue 8, priest 16, shaman 64, mage 128, warlock 256, druid 1024
        const int AllClassicClasses = 1 | 2 | 4 | 8 | 16 | 64 | 128 | 256 | 1024;

        static readonly string[] FallbackClasses = { "Warrior", "Paladin", "Hunter", "Rogue", "Priest", "Death Knight", "Shaman", "Mage", "Warlock", "Monk", "Druid" };       // by class id - 1
        // race id - 1 -> name for the classic races, used only while the game's race table is not read
        static readonly Dictionary<int, KeyValuePair<string, string>> FallbackRaces = new Dictionary<int, KeyValuePair<string, string>>
        {
            { 0, new KeyValuePair<string, string>("Human", "Alliance") }, { 1, new KeyValuePair<string, string>("Orc", "Horde") }, { 2, new KeyValuePair<string, string>("Dwarf", "Alliance") },
            { 3, new KeyValuePair<string, string>("Night Elf", "Alliance") }, { 4, new KeyValuePair<string, string>("Undead", "Horde") }, { 5, new KeyValuePair<string, string>("Tauren", "Horde") },
            { 6, new KeyValuePair<string, string>("Gnome", "Alliance") }, { 7, new KeyValuePair<string, string>("Troll", "Horde") },
        };
        // the PvP ranks as the item table counts them (5 = the first rank .. 18 = the last), Alliance and Horde title; checked against the names of the real rank gear (self-test "upgrades")
        public static readonly string[] AllianceRanks = { "Private", "Corporal", "Sergeant", "Master Sergeant", "Sergeant Major", "Knight", "Knight-Lieutenant", "Knight-Captain", "Knight-Champion", "Lieutenant Commander", "Commander", "Marshal", "Field Marshal", "Grand Marshal" };
        public static readonly string[] HordeRanks = { "Scout", "Grunt", "Sergeant", "Senior Sergeant", "First Sergeant", "Stone Guard", "Blood Guard", "Legionnaire", "Centurion", "Champion", "Lieutenant General", "General", "Warlord", "High Warlord" };
        public const int FirstPvpRank = 5;

        // "rank 11 (Knight-Lieutenant)": the title of the character's side, both when the side is not known, just the number for a rank the table does not name
        public static string PvpRankText(int rank, string side)
        {
            int i = rank - FirstPvpRank;
            if (i < 0 || i >= AllianceRanks.Length) return "rank " + rank;
            string a = AllianceRanks[i], h = HordeRanks[i];
            return "rank " + rank + " (" + (side == "Alliance" ? a : side == "Horde" ? h : a == h ? a : a + " / " + h) + ")";
        }

        public static readonly string[] Standings = { "Hated", "Hostile", "Unfriendly", "Neutral", "Friendly", "Honored", "Revered", "Exalted" };   // the index the item tables store

        // the profession skill lines (SkillLine ids) the Hub's professions list covers; the game's own skill table decides first
        static readonly Dictionary<int, string> FallbackSkills = new Dictionary<int, string>
        {
            { 164, "Blacksmithing" }, { 165, "Leatherworking" }, { 171, "Alchemy" }, { 185, "Cooking" }, { 186, "Mining" }, { 197, "Tailoring" }, { 202, "Engineering" },
            { 333, "Enchanting" }, { 129, "First Aid" }, { 356, "Fishing" }, { 182, "Herbalism" }, { 393, "Skinning" },
        };

        public static string SkillName(int id)
        {
            var s = SpellData.Skill(id);
            if (s != null && !string.IsNullOrEmpty(s.Name)) return s.Name;
            return FallbackSkills.TryGetValue(id, out var n) ? n : null;
        }

        static bool IsProfessionSkill(int id)
        {
            var s = SpellData.Skill(id);
            return s != null ? s.IsProfession : FallbackSkills.ContainsKey(id);
        }

        static bool AllClasses(int mask) => mask <= 0 || mask == 32767 || (mask & AllClassicClasses) == AllClassicClasses;

        static string ClassName(int bit)
        {
            var c = ClassData.Data != null && ClassData.Data.Classes.TryGetValue(bit + 1, out var ci) ? ci : null;
            if (c != null && !string.IsNullOrEmpty(c.Name)) return c.Name;
            return bit >= 0 && bit < FallbackClasses.Length ? FallbackClasses[bit] : null;
        }

        // race bit -> (name, side); null name = not known
        static KeyValuePair<string, string> RaceOf(int bit)
        {
            var r = ClassData.Data?.Races.Values.FirstOrDefault(x => x.Bit == bit && x.Id == bit + 1) ?? ClassData.Data?.Races.Values.FirstOrDefault(x => x.Bit == bit);
            if (r != null && !string.IsNullOrEmpty(r.Name)) return new KeyValuePair<string, string>(r.Name, r.Side == RaceInfo.Alliance ? "Alliance" : r.Side == RaceInfo.Horde ? "Horde" : "");
            return FallbackRaces.TryGetValue(bit, out var f) ? f : new KeyValuePair<string, string>(null, "");
        }

        // the playable races the Outpost knows, by bit
        static List<int> KnownRaceBits()
        {
            var bits = new HashSet<int>(FallbackRaces.Keys);
            if (ClassData.Data != null) foreach (var r in ClassData.Data.Races.Values) if (r.Playable && r.Bit >= 0 && r.Bit < 64) bits.Add(r.Bit);
            return bits.OrderBy(b => b).ToList();
        }

        // a race mask as words: "Horde" / "Alliance" when it is exactly one side's races, else the names ("Orc, Troll"); null when nothing in it is known
        public static string RaceMaskText(long mask, out string side)
        {
            side = "";
            var known = KnownRaceBits();
            var inMask = known.Where(b => (mask & (1L << b)) != 0).ToList();
            if (inMask.Count == 0) return null;
            foreach (var s in new[] { "Alliance", "Horde" })
            {
                var of = known.Where(b => RaceOf(b).Value == s).ToList();
                if (of.Count > 0 && of.Count == inMask.Count && of.All(b => inMask.Contains(b))) { side = s; return s; }
            }
            var names = inMask.Select(b => RaceOf(b).Key).Where(n => n != null).ToList();
            return names.Count == 0 ? null : string.Join(", ", names);
        }

        static bool AllRaces(long mask)
        {
            if (mask == 0 || mask == -1) return true;
            var known = KnownRaceBits();
            return known.Count > 0 && known.All(b => (mask & (1L << b)) != 0);
        }

        // Can this character use the item, apart from its class and level (CanUse / Evaluate answer those)?
        public static UpgradeVerdict Restriction(UpgradeChar who, ClientItem it)
        {
            if (who == null || it == null) return UpgradeVerdict.None;
            bool classRes = !AllClasses(it.AllowableClass), raceRes = !AllRaces(it.AllowableRace);
            bool skillRes = it.RequiredSkill > 0, repRes = it.MinFactionId > 0 && it.MinReputation > 3, pvpRes = it.RequiredPvpRank > 0;
            if (!classRes && !raceRes && !skillRes && !repRes && !pvpRes) return UpgradeVerdict.None;
            var v = new UpgradeVerdict();

            if (classRes)
            {
                int cm = who.ClassMask;
                if (cm != 0 && (it.AllowableClass & cm) == 0)
                {
                    var names = Enumerable.Range(0, 15).Where(b => (it.AllowableClass & (1 << b)) != 0).Select(ClassName).Where(n => n != null).ToList();
                    v.Reasons.Add(names.Count == 0 ? "Other classes only" : string.Join(", ", names) + " only");
                }
            }

            if (raceRes)
            {
                string text = RaceMaskText(it.AllowableRace, out var side);
                ulong rb = who.RaceBit;
                if (rb != 0)
                {
                    if (((ulong)it.AllowableRace & rb) == 0) v.Reasons.Add((text ?? "Other races") + " only");
                }
                else if (side.Length > 0 && who.Side.Length > 0)
                {
                    if (side != who.Side) v.Reasons.Add(side + " only");
                }
                else if (text != null) v.Notes.Add(text + " only (this character's race is not known)");
            }

            if (skillRes)
            {
                string sn = SkillName(it.RequiredSkill) ?? ("skill " + it.RequiredSkill);
                string need = "Needs " + sn + (it.RequiredSkillRank > 0 ? " " + it.RequiredSkillRank : "");
                int have = who.SkillRank(it.RequiredSkill, SkillName(it.RequiredSkill), IsProfessionSkill(it.RequiredSkill));
                if (have == UpgradeChar.SkillUnknown) v.Notes.Add(need);
                else if (have < Math.Max(1, it.RequiredSkillRank)) v.Reasons.Add(need);
            }

            if (repRes)
            {
                string fn = FactionData.Name(it.MinFactionId);
                int st = Math.Min(it.MinReputation, Standings.Length - 1);
                string need = "Needs " + Standings[st] + (fn != null ? " with " + fn : " reputation (faction " + it.MinFactionId + ")");
                if (who.Reputation != null && who.ReputationKnown)
                {
                    who.Reputation.TryGetValue(it.MinFactionId, out var cur);
                    bool have = who.Reputation.ContainsKey(it.MinFactionId);
                    if ((have ? cur : 3) < it.MinReputation) v.Reasons.Add(need);
                }
                else v.Notes.Add(need);
            }

            if (pvpRes)
            {
                string need = "Needs PvP " + PvpRankText(it.RequiredPvpRank, who.Side);
                if (who.PvpRank >= 0) { if (who.PvpRank < it.RequiredPvpRank) v.Reasons.Add(need); }
                else v.Notes.Add(need);
            }
            return v;
        }
    }
}
