using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ElansAddonHub.Services.Db2;

namespace ElansAddonHub.Services.Atlas
{
    // the browse lists of the character categories: every class, every playable race (grouped by side), every profession (2.85: primary / secondary)
    public static partial class AtlasBrowse
    {
        static BIndex BuildClasses(Ctx x)
        {
            var ix = new BIndex { Noun = "class" };
            foreach (var c in ClassData.Data.ClassList())
            {
                if (string.IsNullOrEmpty(c.Name)) continue;
                var e = Entry("class", c.Id, c.Name);
                e.Client = true; e.Icon = c.IconId;
                e.Detail = string.Join(" · ", new[] { c.RoleText, c.PowerTypes.Length > 0 ? ClassSnapshot.PowerName(c.PowerTypes[0]) : "" }.Where(t => !string.IsNullOrEmpty(t)));
                ix.All.Add(e);
            }
            ix.Root.Title = "Classes";
            return ix;
        }

        static BIndex BuildRaces(Ctx x)
        {
            var ix = new BIndex { Noun = "race" };
            foreach (var r in ClassData.Data.PlayableRaces())
            {
                if (string.IsNullOrEmpty(r.Name)) continue;
                var e = Entry("race", r.Id, r.Name);
                e.Client = true; e.SubLabel = r.SideName; e.Icon = RaceIcons.For(r);
                e.Detail = ClassData.Data.ClassesOf(r.Id).Count + " classes";
                ix.All.Add(e);
            }
            ix.Root.Title = "Races";
            KidsBy(ix.Root, ix.All, e => e.SubLabel, e => true);
            return ix;
        }
        // Professions (2.85): one row per profession of the game data (the same book the Professions page builds), Primary / Secondary as the chips
        // (the client's skill category: Fishing is secondary like Cooking and First Aid, the gatherers primary). A row says the skill range (the rank
        // spells' caps: "skill 1-300 · Apprentice to Artisan"), the recipes and - when the Classic baseline or the game's cache knows them - the trainers
        // (creatures with the trainer flag whose title names the profession: "Alchemy Trainer"). A row opens the profession's page on the Professions page
        // (professions:<slug>/recipes; a gathering profession without recipes on its guide). Rank < 0 = no trainer count known.
        static BIndex BuildProfessions(Ctx x)
        {
            var ix = new BIndex { Noun = "profession" };
            var book = Professions.ProfessionBook.Build(SpellData.Spells, ItemData.Get, SpellData.Data?.Details?.Effects);
            var trainers = TrainerTitles(x);
            var sn = SpellData.Spells;
            var all = book.Professions.ToList();
            // a profession line the game data names that the book leaves out (a secondary line with only a few recipes in a small data set): a row too,
            // so the list and the side menu's number agree with the profession names of the game (AtlasHosts.ProfessionCount)
            foreach (var s in sn.Skills.Values.Where(s => s.ParentId == 0 && AtlasNav.IsProfessionName(s.Name)).OrderBy(s => s.Id))
                if (!all.Any(p => string.Equals(p.Name, s.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    var extra = new Professions.Profession { Skill = s, Kind = Professions.ProfessionBook.GatheringNames.Contains(s.Name, StringComparer.OrdinalIgnoreCase) ? Professions.ProfKind.Gathering : s.Category == SkillInfo.CatProfession ? Professions.ProfKind.Primary : Professions.ProfKind.Secondary };
                    all.Add(extra);
                }
            foreach (var p in all)
            {
                if (p.Skill == null || string.IsNullOrEmpty(p.Name)) continue;
                var e = Entry("skill", p.Id, p.Name);
                e.Client = true; e.Icon = SkillIcons.For(p.Skill);
                bool primary = p.Skill.Category == SkillInfo.CatProfession;
                e.SubLabel = primary ? "Primary" : "Secondary";
                int recipes = p.Recipes.Count > 0 ? p.Craftable.Count() : sn.Recipes.Count(rc => rc.SkillId == p.Id);
                e.Type = recipes;
                int cap = p.Ranks.Count > 0 ? p.MaxCap : 0;
                e.Level = cap > 0 ? 1 : 0; e.Level2 = cap;
                string ranks = p.Ranks.Count >= 2 ? p.Ranks.First().Name + " to " + p.Ranks.Last().Name : p.Ranks.Count == 1 ? p.Ranks[0].Name : "";
                e.Detail = string.Join(" · ", new[] { e.SubLabel + (p.Gathering ? ", gathering" : ""), cap > 0 ? "skill 1-" + cap.ToString(CultureInfo.InvariantCulture) : "", ranks }.Where(t => t.Length > 0));
                e.Rank = trainers == null ? -1 : trainers.Count(t => t.IndexOf(p.Name, StringComparison.OrdinalIgnoreCase) >= 0);
                e.Extra = string.Join(" · ", new[] { recipes > 0 ? AtlasFormat.Count(recipes, "recipe", "recipes") : p.Gathering ? "gathered, no recipes" : "", e.Rank > 0 ? AtlasFormat.Count(e.Rank, "trainer", "trainers") : "" }.Where(t => t.Length > 0));
                e.Link = AtlasHosts.ProfessionsId(p.Name, recipes == 0 && p.Gathering ? "guide" : "recipes");
                ix.All.Add(e);
            }
            ix.Root.Title = "Professions";
            KidsBy(ix.Root, ix.All, en => en.SubLabel, en => true);
            return ix;
        }

        // the titles of the trainers the data knows (one per creature): the Classic baseline's trainers (npc flag 0x10) and the creatures the game described
        // whose title says "Trainer"; null when neither source is there
        static List<string> TrainerTitles(Ctx x)
        {
            var seen = SeenData.Snapshot;
            if (x.Classic == null && seen == null) return null;
            var byId = new Dictionary<int, string>();
            if (x.Classic != null)
                foreach (var id in x.Classic.Npcs.Keys)
                {
                    var c = x.Classic.NpcInfo(id);
                    if (c == null || (c.NpcFlags & 0x10) == 0 || string.IsNullOrEmpty(c.SubName) || c.SubName.IndexOf("Trainer", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    byId[id] = c.SubName;
                }
            if (seen != null)
                foreach (var id in seen.Npcs.Keys)
                {
                    if (byId.ContainsKey(id)) continue;
                    var t = SeenData.Npc(id)?.Title;
                    if (!string.IsNullOrEmpty(t) && t.IndexOf("Trainer", StringComparison.OrdinalIgnoreCase) >= 0) byId[id] = t;
                }
            return byId.Values.ToList();
        }
    }
}
