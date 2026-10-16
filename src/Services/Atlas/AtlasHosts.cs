using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using ElansAddonHub.Services.Db2;

namespace ElansAddonHub.Services.Atlas
{
    // ============================================================================================================================
    // The two hosted Atlas categories, Talents and Professions: their ids ("talents:tree/hunter", "professions:cooking/recipes"), how a search text
    // names them and the links the entity pages give them. Pure text and tables, no controls: the pages themselves are hosted by the main window
    // (MainWindow.Atlas.cs) and keep their own state; the Atlas only knows the sub-path.
    //   talents:                      the calculator as it was left (the class last used)
    //   talents:tree/<classfile>      one class ("hunter", "warlock" ...)
    //   talents:build/<ETB code>      the calculator with that build code imported (the class follows from the code)
    //   professions:                  the page as it was left
    //   professions:<profession>/<tab>  e.g. "cooking/recipes", "first-aid/guide"; the tab is recipes | craft | guide | camp
    //   professions:camp              the Camping tab (it belongs to no profession)
    // ============================================================================================================================
    public static class AtlasHosts
    {
        public const string Talents = "talents", Professions = "professions";
        public static readonly string[] ProfTabs = { "recipes", "craft", "guide", "camp" };
        public static readonly string[] ClassFiles = { "WARRIOR", "PALADIN", "HUNTER", "ROGUE", "PRIEST", "SHAMAN", "MAGE", "WARLOCK", "DRUID" };
        public static readonly string[] ProfessionNames = { "Alchemy", "Blacksmithing", "Enchanting", "Engineering", "Herbalism", "Leatherworking", "Mining", "Skinning", "Tailoring", "Cooking", "First Aid", "Fishing" };

        // "First Aid" -> "first-aid"
        public static string Slug(string name)
        {
            var sb = new StringBuilder();
            foreach (var ch in (name ?? "").Trim().ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(ch)) sb.Append(ch);
                else if (sb.Length > 0 && sb[sb.Length - 1] != '-') sb.Append('-');
            }
            return sb.ToString().Trim('-');
        }

        // ---- ids
        public static string TalentsId(string classFile) => string.IsNullOrEmpty(classFile) ? Talents + ":" : Talents + ":tree/" + classFile.ToLowerInvariant();
        public static string TalentsBuildId(string code) => Talents + ":build/" + (code ?? "").Trim();

        public static string ProfessionsId(string profession, string tab)
        {
            tab = ProfTabs.Contains(tab ?? "") ? tab : "recipes";
            if (tab == "camp") return Professions + ":camp";
            var slug = Slug(profession);
            return slug.Length == 0 ? Professions + ":" + tab : Professions + ":" + slug + "/" + tab;
        }

        // sub-path of "talents:" -> the class file (upper case, "" = as it was left) and a build code to import ("" = none)
        public static void ParseTalents(string sub, out string classFile, out string code)
        {
            classFile = ""; code = "";
            sub = (sub ?? "").Trim('/');
            if (sub.StartsWith("tree/", StringComparison.OrdinalIgnoreCase)) classFile = sub.Substring(5).Trim('/').ToUpperInvariant();
            else if (sub.StartsWith("build/", StringComparison.OrdinalIgnoreCase)) code = sub.Substring(6).Trim('/');
        }

        // sub-path of "professions:" -> the profession slug ("" = as it was left) and the tab ("" = as it was left)
        public static void ParseProfessions(string sub, out string slug, out string tab)
        {
            slug = ""; tab = "";
            sub = (sub ?? "").Trim('/').ToLowerInvariant();
            if (sub.Length == 0) return;
            var parts = sub.Split(new[] { '/' }, 2);
            if (ProfTabs.Contains(parts[0])) { tab = parts[0]; return; }          // "camp", "recipes"
            slug = Slug(parts[0]);
            if (parts.Length > 1 && ProfTabs.Contains(parts[1].Trim('/'))) tab = parts[1].Trim('/');
        }

        // ---- names
        public static string ClassName(string classFile)
        {
            var t = TalentData.ForClassFile(classFile);
            if (t != null && !string.IsNullOrEmpty(t.ClassName)) return t.ClassName;
            var f = (classFile ?? "").ToLowerInvariant();
            return f.Length == 0 ? "" : char.ToUpperInvariant(f[0]) + f.Substring(1);
        }

        static List<KeyValuePair<string, string>> Classes()
        {
            var d = TalentData.Data;
            if (d != null && d.Trees.Count > 0) return d.Trees.Select(t => new KeyValuePair<string, string>(t.ClassFile, t.ClassName)).ToList();
            return ClassFiles.Select(f => new KeyValuePair<string, string>(f, ClassName(f))).ToList();
        }

        public static List<string> ProfessionList()
        {
            var sp = SpellData.Spells;
            if (SpellData.Ready && sp?.Skills != null)
            {
                var names = sp.Skills.Values.Where(s => s.IsProfession && !string.IsNullOrEmpty(s.Name)).Select(s => s.Name).Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                if (names.Count > 0) return names;
            }
            return ProfessionNames.ToList();
        }

        // the side menu's number for Professions: the professions of the game data (the twelve by name, a profession the client lists twice counted once); -1 without spell data
        public static int ProfessionCount()
        {
            var sp = SpellData.Spells;
            if (!SpellData.Ready || sp?.Skills == null) return -1;
            return sp.Skills.Values.Where(s => s.ParentId == 0 && AtlasNav.IsProfessionName(s.Name)).Select(s => s.Name.Trim().ToLowerInvariant()).Distinct().Count();
        }

        // the title the breadcrumb ends with
        public static string TalentsSubTitle(string sub)
        {
            ParseTalents(sub, out var cf, out var code);
            if (cf.Length > 0) return ClassName(cf);
            return code.Length > 0 ? "Build" : "";
        }

        public static string ProfessionsSubTitle(string sub)
        {
            ParseProfessions(sub, out var slug, out var tab);
            if (slug.Length == 0) return tab == "camp" ? "Camping" : "";
            var name = ProfessionList().FirstOrDefault(n => Slug(n) == slug);
            return name ?? slug;
        }

        // ---- search: "talents", "hunter talents", "cooking", "camping"
        public static List<AtlasHit> TalentHits(string q)
        {
            var hits = new List<AtlasHit>();
            q = (q ?? "").Trim().ToLowerInvariant();
            if (q.Length < 2) return hits;
            int r = AtlasQuery.MatchRank("Talents", q);
            if (r < 0 && "talent calculator".StartsWith(q, StringComparison.Ordinal)) r = 1;
            if (r >= 0) hits.Add(new AtlasHit { Id = Talents + ":", Kind = "page", Name = "Talents", Detail = "The talent calculator, builds and build links", Label = "Talents", Rank = r });
            foreach (var c in Classes())
            {
                var name = c.Value + " talents";
                int cr = AtlasQuery.MatchRank(name, q);
                if (cr < 0) continue;
                hits.Add(new AtlasHit { Id = TalentsId(c.Key), Kind = "page", Name = name, Detail = "Plan a " + c.Value + " build", Label = "Talents", Rank = Math.Max(1, cr) });
            }
            return hits;
        }

        public static List<AtlasHit> ProfessionHits(string q)
        {
            var hits = new List<AtlasHit>();
            q = (q ?? "").Trim().ToLowerInvariant();
            if (q.Length < 2) return hits;
            int r = AtlasQuery.MatchRank("Professions", q);
            if (r >= 0) hits.Add(new AtlasHit { Id = Professions + ":", Kind = "page", Name = "Professions", Detail = "Recipes, leveling guides, what you can craft, camping", Label = "Professions", Rank = r });
            foreach (var n in ProfessionList())
            {
                int pr = AtlasQuery.MatchRank(n, q);
                if (pr < 0) continue;
                hits.Add(new AtlasHit { Id = ProfessionsId(n, "recipes"), Kind = "page", Name = n, Detail = "Recipes, leveling guide and what you can craft", Label = "Professions", Rank = pr });
            }
            int cr = AtlasQuery.MatchRank("Camping", q);
            if (cr < 0 && "campfire".StartsWith(q, StringComparison.Ordinal)) cr = 1;
            if (cr >= 0) hits.Add(new AtlasHit { Id = ProfessionsId(null, "camp"), Kind = "page", Name = "Camping", Detail = "Campfire kits and camp objects", Label = "Professions", Rank = cr });
            return hits;
        }

        // ---- the links the entity pages give: null when the category is not there (the page is not hosted, e.g. in a test without it)
        static bool Has(string id) { var c = AtlasCategories.Find(id); return c != null && c.Hosted; }

        // "Crafted by Tailoring 125": the Professions page on that profession's recipes
        public static string RecipesLink(string skillName)
        {
            if (!Has(Professions) || string.IsNullOrWhiteSpace(skillName)) return null;
            var slug = Slug(skillName);
            return slug.Length == 0 ? null : ProfessionsId(skillName, "recipes");
        }

        // a spell that is a talent of a class: the calculator on that class
        public static string TalentTreeLink(int spellId, out string className)
        {
            className = null;
            var d = TalentData.Data;
            if (!Has(Talents) || d == null || spellId <= 0) return null;
            foreach (var t in d.Trees)
                if (t.Nodes.Any(n => n.Entries.Any(e => e.SpellId == spellId))) { className = t.ClassName; return TalentsId(t.ClassFile); }
            return null;
        }
    }
}
