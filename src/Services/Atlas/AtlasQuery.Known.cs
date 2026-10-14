using System;
using System.Collections.Generic;
using System.Linq;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services.Db2;

namespace ElansAddonHub.Services.Atlas
{
    // Recipe pages and item pages: which of the user's characters have learned the recipe (from the profession windows Elan's Hub read) and which members
    // of the Lodge can craft it (the Armory, server 2.19). Both only add a fact / a section; a layer that is not there (no Hub scan, older server, offline) adds nothing
    // or says what would turn it on.
    public static partial class AtlasQuery
    {
        // "Learned by": the user's own characters. `recipes` are the recipes of ONE profession that make the thing.
        static void AddOwnKnown(AtlasPageModel m, string profession, int skillId, ICollection<int> spells)
        {
            if (string.IsNullOrEmpty(profession) || spells.Count == 0) return;
            var knowing = new List<string>();
            int read = 0, have = 0;
            foreach (var c in Characters())
            {
                if (c.Hub == null) continue;
                var pm = HubModules.ProfessionsOf(c.Hub);
                if (pm == null) continue;
                if (pm.List.Any(p => string.Equals(p.Name, profession, StringComparison.OrdinalIgnoreCase))) have++;
                var scan = pm.Known.Values.FirstOrDefault(k => skillId > 0 && k.Line == skillId) ?? pm.Known.Values.FirstOrDefault(k => string.Equals(k.Name, profession, StringComparison.OrdinalIgnoreCase));
                if (scan == null) continue;
                read++;
                if (spells.Any(scan.Ids.Contains)) knowing.Add(c.Name);
            }
            string value;
            if (knowing.Count > 0) value = string.Join(", ", knowing.OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase));
            else if (read > 0) value = "none of your characters";
            else if (have > 0) value = "not read yet: open your " + profession + " window in game";
            else return;
            m.Facts.Add(new AtlasFact("Learned by", value, AtlasLabel.Mine));
        }

        // "Who in the Lodge can craft this?" for the spell(s) that make it
        static void AddLodgeCrafters(AtlasPageModel m, ICollection<int> spells)
        {
            var ask = Context.Crafters;
            if (ask == null || spells.Count == 0) return;
            LodgeCrafters r;
            try { r = ask(spells.ToList()); } catch (Exception) { return; }
            if (r == null || !r.Available) return;                       // an older server, a guest or offline: the Armory page says why, here the section is simply absent
            var s = Section(m, "crafters", "Who in the Lodge can craft this?");
            s.Note = "Members who shared a character with the Lodge's Armory that has learned it. Recipes are read by Elan's Hub when a profession window is opened in game.";
            s.Empty = r.Pending ? "Asking the Lodge..." : "Nobody has shared a character that has learned this yet.";
            foreach (var c in r.Rows.OrderByDescending(x => x.Skill).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).Take(40))
                s.Rows.Add(new AtlasRow
                {
                    Key = "crafter:" + c.Owner + "|" + c.Key + "|" + c.Spell, Title = c.Name + " (" + c.By + ")", Detail = c.Detail + (c.Partial ? " · recipe list partly read" : ""),
                    Type = "Crafter", Label = AtlasLabel.Forever, Tier = 0, Sort = c.Skill,
                });
        }

        static void AddRecipeKnowledge(AtlasPageModel m, IEnumerable<Recipe> recipes)
        {
            var list = recipes.ToList();
            foreach (var g in list.GroupBy(r => r.SkillId))
            {
                var skill = SpellData.Skill(g.Key);
                AddOwnKnown(m, skill?.Name, g.Key, g.Select(r => r.SpellId).ToList());
            }
            AddLodgeCrafters(m, list.Select(r => r.SpellId).Distinct().ToList());
        }
    }
}
