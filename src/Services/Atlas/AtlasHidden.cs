using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ElansAddonHub.Services.Db2;

namespace ElansAddonHub.Services.Atlas
{
    // Test and placeholder entries (2.92): the game files carry developer leftovers - "Test Profession [DNT]", "[PH] Placeholder Item", "zzOLD Shoulders",
    // "Deprecated ..." - that nobody can get in the live game. ONE rule decides what the Atlas leaves out of its lists, chips, counts, search, the side menu's
    // numbers and the Professions list; a page opened by its id (a link, a pasted "item:123") still opens, because a real spell can trigger a [DNT] helper.
    //   1. the name: a developer tag in brackets ([DNT] [PH] [NYI] [DND] [UNUSED] [OLD] [TEST] [QA] [DEPRECATED], any [CAPITALS]), the words UNUSED /
    //      DEPRECATED / PLACEHOLDER / DEBUG / (OLD), DNT / DND / NYI / DUMMY in capitals, a "zz" / "ZZ" / "zzOLD" / "OLD " / "QA " prefix, or the word "test"
    //      ("Test Profession", "TEST SWORD", "Level 60 Test Gear") - but not the real "Test of Lore", "Test of Faith", "The Test of Righteousness" or "High
    //      Test Eternium Fishing Line" (checked on the real game files and the Classic baseline, 2.92);
    //   2. a skill line that nobody can learn: no race / class row in SkillRaceClassInfo, no ability a player gets with it, no teaching item for any of
    //      its recipes (LearnableSkill); its recipes go with it.
    // Self-test sections "atlashidden" (fixtures) and "atlashiddenreal" (lists everything the rule hides in the real game files).
    public static class AtlasHidden
    {
        static readonly Regex DevTag = new Regex(@"\[\s*(?:DNT|DND|PH|NYI|UNUSED|OLD|TEST|QA|DEPRECATED|TBD|WIP)\s*\]|<\s*(?:DNT|DND)\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        // a bracket of capitals is a developer's note ("[DOES NOT WORK]"); no real name has one
        // ("[TMP] Satyrbane" is left alone: a temporary name on an item that may well be in the game)
        static readonly Regex CapsTag = new Regex(@"\[(?!TMP\])[A-Z][A-Z0-9 _\-]*\]", RegexOptions.CultureInvariant);
        static readonly Regex DevWord = new Regex(@"\bUNUSED\b|\bDEPRECATED\b|\(\s*(?:OLD|DNT|DND|PH|NYI|UNUSED|TEST|DELETE ME)\s*\)|PLACEHOLDER|\bDEBUG\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        // case matters: DNT / DND / NYI / DUMMY in capitals are notes ("DNT Kill Credit", "Gnomish Weather Machine NYI"); "Target Dummy" is a real item
        static readonly Regex CapsWord = new Regex(@"\b(?:DNT|DND|NYI|DUMMY)\b", RegexOptions.CultureInvariant);
        // case matters here too: "Zzarc' Vul" is a real creature, "zzOLD" / "ZZOLD" / "zz " are not; "OLD " and "QA " in capitals only
        static readonly Regex DevPrefix = new Regex(@"^\s*(?:zz|ZZ|OLD[\s_\-]|QA[\s_\-:])", RegexOptions.CultureInvariant);
        // the word "test" anywhere ("Test Profession", "TEST SWORD", "Eric Test Item A", "Level 60 Test Gear ..."), but not the real "Test of Lore",
        // "Test of Faith", "The Test of Righteousness", "Test of Endurance" or the fishing line "High Test Eternium Fishing Line"; "Testing" and "Testament" stay
        static readonly Regex DevTest = new Regex(@"\btest\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly Regex DevTestJoined = new Regex(@"^Test(?=[A-Z])|\bTEST[A-Z0-9]+\b", RegexOptions.CultureInvariant);      // "TestBoots - Puffed Mail Green", "TESTT"
        static readonly Regex RealTest = new Regex(@"\btests?\s+of\b|\bhigh\s+test\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // a name the Atlas does not list ("" / null are not hidden here: callers already skip nameless rows)
        public static bool IsHiddenName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            return WhyName(name).Length > 0;
        }

        // why a name is hidden ("" = it is not), for the real-data listing
        public static string WhyName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            if (DevTag.IsMatch(name)) return "tag " + DevTag.Match(name).Value;
            if (CapsTag.IsMatch(name)) return "tag " + CapsTag.Match(name).Value;
            if (DevWord.IsMatch(name)) return "word " + DevWord.Match(name).Value;
            if (CapsWord.IsMatch(name)) return "word " + CapsWord.Match(name).Value;
            if (DevPrefix.IsMatch(name)) return "prefix " + DevPrefix.Match(name).Value.Trim();
            if ((DevTest.IsMatch(name) && !RealTest.IsMatch(name)) || DevTestJoined.IsMatch(name)) return "test name";
            return "";
        }

        // ---- skill lines
        // the answers are kept per snapshot (the professions list, the recipes list and search ask for every skill)
        static readonly object gate = new object();
        static SpellSnapshot memoSpells; static IEnumerable<KeyValuePair<int, int[]>> memoEffects;
        static Dictionary<int, string> memo = new Dictionary<int, string>();

        // tests swap the inputs (fixture snapshots); the app reads SpellData
        public static Func<SpellSnapshot> SpellsSource = () => SpellData.Spells;
        public static Func<IEnumerable<KeyValuePair<int, int[]>>> EffectsSource = () => SpellData.Data?.Details?.Effects;
        public static void ForgetForTest() { lock (gate) { memoSpells = null; memoEffects = null; memo = new Dictionary<int, string>(); } }

        public static bool IsHiddenSkill(SkillInfo s) => WhySkill(s).Length > 0;
        public static bool IsHiddenSkill(int skillId)
        {
            if (skillId <= 0) return false;
            var sn = SpellsSource?.Invoke();
            return sn != null && sn.Skills.TryGetValue(skillId, out var s) && IsHiddenSkill(s);
        }

        public const string NobodyLearns = "nobody can learn it";

        // "" = a real skill line; else why it is hidden
        public static string WhySkill(SkillInfo s) => WhySkill(s, SpellsSource?.Invoke(), EffectsSource?.Invoke());
        public static bool IsHiddenSkill(SkillInfo s, SpellSnapshot sn, IEnumerable<KeyValuePair<int, int[]>> fx) => WhySkill(s, sn, fx).Length > 0;

        // the same for a given snapshot (the professions book is built from its own)
        public static string WhySkill(SkillInfo s, SpellSnapshot sn, IEnumerable<KeyValuePair<int, int[]>> fx)
        {
            if (s == null) return "";
            var why = WhyName(s.Name);
            if (why.Length > 0) return why;
            if (sn == null) return "";
            lock (gate)
            {
                if (!ReferenceEquals(sn, memoSpells) || !ReferenceEquals(fx, memoEffects)) { memoSpells = sn; memoEffects = fx; memo = new Dictionary<int, string>(); }
                if (memo.TryGetValue(s.Id, out var w)) return w;
                w = LearnableSkill(s, sn, fx) ? "" : NobodyLearns;
                memo[s.Id] = w;
                return w;
            }
        }

        // can a player learn this crafting line? Asked only of profession and secondary lines (SkillLine category 11 / 9) that have recipes - the class,
        // weapon, armor and language lines are the game's own business. A line is learnable when it is one of the professions the game names (their
        // recipes come from trainers, which the game files do not list), when one of its abilities comes with the line itself (SkillLineAbility
        // AcquireMethod 1 / 2: learned with the skill or at a skill value), or when an item teaches one of its recipes. The race / class table
        // (SkillRaceClassInfo) is no proof: old pre-release lines such as Survival (142, "Bright Campfire") still have a row for every class.
        public static bool LearnableSkill(SkillInfo s, SpellSnapshot sn, IEnumerable<KeyValuePair<int, int[]>> effects)
        {
            if (s == null || sn == null) return true;
            if (s.Category != SkillInfo.CatProfession && s.Category != SkillInfo.CatSecondary) return true;
            var recipes = sn.Recipes.Where(r => r.SkillId == s.Id).ToList();
            if (recipes.Count == 0) return true;                       // not a crafting line: the name rule alone decides
            if (AtlasNav.IsProfessionName(s.Name)) return true;
            if (sn.AbilitiesOf(s.Id).Any(a => a.Acquire == 1 || a.Acquire == 2)) return true;
            if (effects == null) return true;                          // the item effects are not read: nothing is hidden on half the facts
            var spells = new HashSet<int>(recipes.Select(r => r.SpellId));
            foreach (var kv in effects)
            {
                var a = kv.Value;
                for (int i = 0; i + 1 < a.Length; i += 2)
                {
                    int spell = a[i + 1];
                    if (a[i] == 6 && spells.Contains(spell)) return true;
                    if (sn.Spells.TryGetValue(spell, out var sp)) foreach (var e in sp.Effects) if (e.Effect == 36 && spells.Contains(e.Trigger)) return true;
                }
            }
            return false;
        }

        // ---- one browse entry: its name, the group / chip it sits under, the skill line it belongs to
        public static bool IsHidden(BEntry e)
        {
            if (e == null) return false;
            if (IsHiddenName(e.Name) || IsHiddenName(e.SubLabel)) return true;
            if (e.Kind == "skill" && IsHiddenSkill(e.Id)) return true;
            if (e.Skill > 0 && e.Spell > 0 && IsHiddenSkill(e.Skill)) return true;      // a recipe of a hidden line
            return false;
        }

        // a search hit
        public static bool IsHidden(AtlasHit h)
        {
            if (h == null) return false;
            if (IsHiddenName(h.Name)) return true;
            if (h.Kind == "skill" && AtlasIds.TryParse(h.Id, out var k, out var n) && k == "skill" && IsHiddenSkill(n)) return true;
            return false;
        }
    }
}
