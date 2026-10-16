using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using ElansAddonHub.Services.Db2;

namespace ElansAddonHub.Services.Atlas
{
    // ============================================================================================================================
    // What is a spell and what is a skill in the Atlas (2.77): ONE rule, so nothing is listed under both.
    //   * a SPELL is something you cast: the class abilities (the class skill lines, pets included), the racial abilities and the professions' spells.
    //     Spells (browse:spells) lists them, grouped Class abilities / Racial abilities / Professions; spells of no skill line stay in the full list.
    //   * a SKILL is a line you get better at: weapon skills, armor skills, languages, riding and the rest (lockpicking, poisons, defense ...).
    //     Skills (browse:skills) lists those LINES, grouped Weapon skills / Armor skills / Languages / Riding / Other skills. The spells that come with
    //     them ("Swords", "Language: Orcish", "Riding") are no longer in the Spells list: they are those skills.
    //   * the professions themselves are the Professions page (and Recipes), not a group of Skills.
    // Links keep working: a spell:<id> of a skill line belongs to the Skills row, a skill:<id> of a class or racial line to the Spells row, and the
    // group ids of before 2.77 (browse:spells/weapon-skills, browse:skills/class-abilities ...) open the list that holds them now (AtlasNav.Upgrade).
    // ============================================================================================================================
    public static class AtlasSkillModel
    {
        // the groups of the Skills list
        public const string Weapon = "Weapon skills", Armor = "Armor skills", Languages = "Languages", Riding = "Riding", Other = "Other skills";
        // the groups of the Spells list
        public const string ClassAbilities = "Class abilities", Racial = "Racial abilities", Professions = "Professions";

        public static readonly string[] SkillGroups = { Weapon, Armor, Languages, Riding, Other };
        public static readonly string[] SpellGroups = { ClassAbilities, Racial, Professions };

        public static bool IsRacialLine(SkillInfo s) => s != null && (s.Name ?? "").IndexOf("Racial", StringComparison.OrdinalIgnoreCase) >= 0;
        public static bool IsRidingLine(SkillInfo s) => s != null && (s.Name ?? "").IndexOf("Riding", StringComparison.OrdinalIgnoreCase) >= 0;
        public static bool IsProfessionLine(SkillInfo s) => s != null && (s.Category == SkillInfo.CatProfession || AtlasNav.IsProfessionName(s.Name));

        // the Spells group of a skill line ("" = the line is a skill: its spells are not in the Spells list)
        public static string SpellGroup(SkillInfo s)
        {
            if (s == null) return null;                          // no skill line: an ordinary spell (in the full list, no group)
            if (IsProfessionLine(s)) return Professions;
            if (s.Category == SkillInfo.CatClass) return ClassAbilities;
            if (IsRacialLine(s)) return Racial;
            return "";
        }

        // the Skills group of a skill line ("" = not on the Skills list: a class or racial line is spells, a profession has its own page)
        public static string SkillGroup(SkillInfo s)
        {
            if (s == null || IsProfessionLine(s) || s.Category == SkillInfo.CatClass || IsRacialLine(s)) return "";
            if (IsRidingLine(s)) return Riding;
            switch (s.Category)
            {
                case SkillInfo.CatWeapon: return Weapon;
                case SkillInfo.CatArmor: return Armor;
                case SkillInfo.CatLanguage: return Languages;
                default: return Other;                           // secondary (lockpicking, poisons), attributes, anything else
            }
        }

        public static bool IsSkillLine(SkillInfo s) => SkillGroup(s).Length > 0;

        // spell -> the skill line that teaches it (the first, as the Spells list has always read it); cached per spell snapshot
        static readonly ConditionalWeakTable<SpellSnapshot, Dictionary<int, SkillInfo>> lineOf = new ConditionalWeakTable<SpellSnapshot, Dictionary<int, SkillInfo>>();

        public static Dictionary<int, SkillInfo> LinesOf(SpellSnapshot sn)
        {
            if (sn == null) return new Dictionary<int, SkillInfo>();
            return lineOf.GetValue(sn, x =>
            {
                var d = new Dictionary<int, SkillInfo>();
                foreach (var a in x.Abilities) if (!d.ContainsKey(a.Spell) && x.Skills.TryGetValue(a.Skill, out var sk)) d[a.Spell] = sk;
                return d;
            });
        }

        public static SkillInfo LineOfSpell(int spellId)
        {
            var sn = SpellData.Spells;
            return sn != null && LinesOf(sn).TryGetValue(spellId, out var s) ? s : null;
        }

        // a spell that is part of a skill ("Swords", "Riding", "Language: Common"): its page belongs to Skills
        public static bool SpellIsSkill(int spellId) { try { return SpellGroup(LineOfSpell(spellId)) == ""; } catch (Exception) { return false; } }

        // a skill line whose spells are abilities (a class or racial line): its page belongs to Spells
        public static bool SkillIsSpells(int skillId)
        {
            SkillInfo s = null;
            try { s = SpellData.Skill(skillId); } catch (Exception) { }
            return s != null && !IsProfessionLine(s) && (s.Category == SkillInfo.CatClass || IsRacialLine(s));
        }

        // the group slugs of before 2.77 -> the list that holds them now (category, slug); null = unchanged
        public static Tuple<string, string> Moved(string cat, string slug)
        {
            if (cat == "spells")
                switch (slug)
                {
                    case "weapon-skills": return Tuple.Create("skills", "weapon-skills");
                    case "armor-skills": return Tuple.Create("skills", "armor-skills");
                    case "languages": return Tuple.Create("skills", "languages");
                    case "attributes": return Tuple.Create("skills", "other-skills");
                    case "secondary-skills": return Tuple.Create("skills", "other-skills");
                }
            if (cat == "skills")
                switch (slug)
                {
                    case "class-abilities": return Tuple.Create("spells", "class-abilities");
                    case "secondary-skills": return Tuple.Create("skills", "other-skills");
                    case "attributes": return Tuple.Create("skills", "other-skills");
                }
            return null;
        }
    }
}
