using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services.Atlas.Classic;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Maps;

namespace ElansAddonHub.Services.Atlas
{
    public static partial class AtlasQuery
    {
        static string Races(int mask)
        {
            var names = new[] { "Human", "Orc", "Dwarf", "Night Elf", "Undead", "Tauren", "Gnome", "Troll" };
            var l = new List<string>();
            for (int i = 0; i < names.Length; i++) if ((mask & (1 << i)) != 0) l.Add(names[i]);
            return string.Join(", ", l);
        }
        static string Classes(int mask)
        {
            var map = new[] { new KeyValuePair<int, string>(1, "Warrior"), new KeyValuePair<int, string>(2, "Paladin"), new KeyValuePair<int, string>(4, "Hunter"), new KeyValuePair<int, string>(8, "Rogue"),
                new KeyValuePair<int, string>(16, "Priest"), new KeyValuePair<int, string>(64, "Shaman"), new KeyValuePair<int, string>(128, "Mage"), new KeyValuePair<int, string>(256, "Warlock"), new KeyValuePair<int, string>(1024, "Druid") };
            return string.Join(", ", map.Where(k => (mask & k.Key) != 0).Select(k => k.Value));
        }

        static AtlasPageModel BuildQuest(int id)
        {
            var seen = SeenData.Quest(id);
            var cq = ClassicAtlas.QuestInfo(id);
            var L = Lodge();
            var lq = L?.Quest(id);
            if (seen == null && cq == null && lq == null)
            {
                var nf = NotFoundPage("quest", id, "No layer knows this quest. Your game has not shown it to you yet.");
                LayerNotes(nf, true, false);
                return nf;
            }
            string F = AtlasLabel.Forever, C = AtlasLabel.Classic;
            var m = new AtlasPageModel { Id = "quest:" + id, Kind = "quest", MapFirst = false };
            m.Title = seen != null && seen.Title.Length > 0 ? seen.Title : cq?.Title ?? "Quest " + id;
            if (seen != null) AddLayer(m, "seen");
            if (cq != null) AddLayer(m, "classic");
            if (lq != null) AddLayer(m, "lodge");
            int level = seen != null && seen.Level > 0 ? seen.Level : cq?.QuestLevel ?? 0;
            int min = seen != null && seen.MinLevel > 0 ? seen.MinLevel : cq?.MinLevel ?? 0;
            if (level > 0) m.Facts.Add(new AtlasFact("Level", level.ToString(CultureInfo.InvariantCulture), seen != null && seen.Level > 0 ? F : C));
            if (min > 0) m.Facts.Add(new AtlasFact("Requires level", min.ToString(CultureInfo.InvariantCulture), seen != null && seen.MinLevel > 0 ? F : C));
            int sort = seen != null && seen.Sort != 0 ? seen.Sort : cq?.ZoneOrSort ?? 0;
            var idx = MapData.Index;
            if (sort > 0 && idx != null)
            {
                int ui = idx.UiMapForArea(sort);
                var area = idx.AreaName(sort);
                if (ui > 0) m.Facts.Add(new AtlasFact("Zone", ZoneName(ui), seen != null && seen.Sort != 0 ? F : C, "zone:" + ui));
                else if (!string.IsNullOrEmpty(area)) m.Facts.Add(new AtlasFact("Zone", area, seen != null && seen.Sort != 0 ? F : C));
            }
            if (cq != null && cq.RequiredRaces != 0 && Races(cq.RequiredRaces).Length > 0) m.Facts.Add(new AtlasFact("Races", Races(cq.RequiredRaces), C));
            if (cq != null && cq.RequiredClasses != 0 && Classes(cq.RequiredClasses).Length > 0) m.Facts.Add(new AtlasFact("Classes", Classes(cq.RequiredClasses), C));
            m.Subtitle = string.Join(" · ", new[] { "Quest " + id, level > 0 ? "Level " + level : "" }.Where(s => s.Length > 0));
            if (seen != null && !string.IsNullOrWhiteSpace(seen.Objectives)) m.Texts.Add(new AtlasText("Objectives", seen.Objectives.Trim(), F));
            if (seen != null && !string.IsNullOrWhiteSpace(seen.Description)) m.Texts.Add(new AtlasText("Description", seen.Description.Trim(), F));

            // reward items: what the Lodge saw, then the Classic baseline's table on the same rows
            void ItemList(string key, string title, IEnumerable<int> lodgeItems, int[] classicItems, int[] classicCounts, string note = "")
            {
                var s = Section(m, key, title);
                s.Note = note;
                var by = new Dictionary<int, AtlasRow>();
                foreach (var i in lodgeItems ?? Enumerable.Empty<int>())
                {
                    if (by.ContainsKey(i)) continue;
                    var r = ItemRow(i, "Seen in a Lodge member's game", F); r.Type = "Reward"; r.Tier = 0; by[i] = r; s.Rows.Add(r);
                }
                for (int k = 0; classicItems != null && k < classicItems.Length; k++)
                {
                    int cnt = classicCounts != null && k < classicCounts.Length ? classicCounts[k] : 1;
                    var text = cnt > 1 ? "×" + cnt : "";
                    if (by.TryGetValue(classicItems[k], out var r0)) { r0.Extra = C + (text.Length > 0 ? ": " + text : ": listed too"); continue; }
                    var r = ItemRow(classicItems[k], text, C); r.Type = "Reward"; r.Tier = 2;
                    by[classicItems[k]] = r; s.Rows.Add(r);
                }
                if (s.Rows.Count == 0) m.Sections.Remove(s);
            }
            ItemList("rewards", "Rewards", lq?.Rewards, cq?.RewardItems, cq?.RewardCounts);
            ItemList("choices", "Choose one of", lq?.Choices, cq?.ChoiceItems, cq?.ChoiceCounts);
            ItemList("objectives", "Needs", null, cq?.ObjectiveItems, cq?.ObjectiveCounts, "Items the quest asks for.");

            // given by
            var plan = new PlanBuilder();
            if (cq != null && cq.Givers.Length > 0)
            {
                var s = Section(m, "givers", "Given by");
                s.Note = "From the Classic baseline.";
                foreach (var g in cq.Givers.Take(12))
                {
                    var spawns = g.IsObject ? ClassicAtlas.Current?.ObjectSpawns(g.Id) : ClassicAtlas.Spawns(g.Id);
                    var name = g.IsObject ? ObjectName(g.Id) : NpcName(g.Id);
                    s.Rows.Add(new AtlasRow { Key = (g.IsObject ? "obj:" : "npc:") + g.Id, Link = (g.IsObject ? "obj:" : "npc:") + g.Id, Title = name, Label = C, Tier = 2, Type = g.IsObject ? "Object" : "NPC", Zone = ZoneOfSpawns(spawns), Detail = ZoneOfSpawns(spawns) });
                    plan.Classic(spawns, MarkerKind.Quest, name, "Gives " + m.Title + " · " + C, (g.IsObject ? "obj:" : "npc:") + g.Id);
                }
            }
            m.Map = plan.Build();
            LayerNotes(m, !ClassicAtlas.IsReady, false);
            return m;
        }

        // ============================================================================================================================ spells, skills, factions
        static string Seconds(int ms) => ms <= 0 ? "Instant" : ms >= 60000 && ms % 60000 == 0 ? ms / 60000 + " min" : (ms / 1000.0).ToString("0.##", CultureInfo.InvariantCulture) + " s";

        static AtlasPageModel BuildSpell(int id)
        {
            var sp = SpellData.Spell(id);
            if (sp == null) return NotFoundPage("spell", id, SpellData.Ready ? "Your game has no spell with this id." : "Spell data is read from your game files and is not ready yet.");
            string F = AtlasLabel.Forever;
            var m = new AtlasPageModel { Id = "spell:" + id, Kind = "spell", Title = sp.Name, IconId = sp.IconId };
            AddLayer(m, "client");
            m.Subtitle = string.Join(" · ", new[] { "Spell " + id, sp.Rank ?? "" }.Where(s => s.Length > 0));
            if (!string.IsNullOrEmpty(sp.Rank)) m.Facts.Add(new AtlasFact("Rank", sp.Rank, F));
            m.Facts.Add(new AtlasFact("Cast time", Seconds(sp.CastMs), F));
            if (sp.RangeMax > 0) m.Facts.Add(new AtlasFact("Range", sp.RangeMax.ToString("0.##", CultureInfo.InvariantCulture) + " yards", F));
            if (sp.DurationMs > 0) m.Facts.Add(new AtlasFact("Duration", Seconds(sp.DurationMs), F));
            else if (sp.DurationMs < 0) m.Facts.Add(new AtlasFact("Duration", "Until cancelled", F));
            if (!string.IsNullOrWhiteSpace(sp.Description)) m.Texts.Add(new AtlasText("Description", sp.Description.Trim(), F));
            if (!string.IsNullOrWhiteSpace(sp.AuraDescription)) m.Texts.Add(new AtlasText("While it lasts", sp.AuraDescription.Trim(), F));
            var rec = SpellData.Spells?.RecipeOf(id);
            if (rec != null)
            {
                var skill = SpellData.Skill(rec.SkillId);
                var s = Section(m, "recipe", "Recipe");
                s.Note = (skill?.Name ?? "Profession") + (rec.MinRank > 0 ? ", needs skill " + rec.MinRank : "") + (rec.Green > 0 ? " · green at " + rec.Green : "") + (rec.Gray > 0 ? " · grey at " + rec.Gray : "");
                if (skill != null) s.Rows.Add(new AtlasRow { Key = "skill:" + skill.Id, Link = AtlasIds.Skill(skill.Id), Title = skill.Name, IconId = skill.IconId, Label = F, Tier = 1, Type = "Skill", Detail = "The profession that teaches it" });
                if (rec.ItemId > 0) { var r = ItemRow(rec.ItemId, "Makes", F); r.Type = "Creates"; r.Tier = 1; s.Rows.Add(r); }
                for (int i = 0; i + 1 < rec.Reagents.Length; i += 2)
                    if (rec.Reagents[i] > 0) { var r = ItemRow(rec.Reagents[i], "Needs ×" + rec.Reagents[i + 1], F); r.Type = "Reagent"; r.Tier = 1; s.Rows.Add(r); }
            }
            return m;
        }

        static AtlasPageModel BuildSkill(int id)
        {
            var sk = SpellData.Skill(id);
            if (sk == null) return NotFoundPage("skill", id, SpellData.Ready ? "Your game has no skill with this id." : "Skill data is read from your game files and is not ready yet.");
            string F = AtlasLabel.Forever;
            var m = new AtlasPageModel { Id = "skill:" + id, Kind = "skill", Title = sk.Name, IconId = sk.IconId };
            AddLayer(m, "client");
            m.Subtitle = "Skill " + id + " · " + SkillCategory(sk.Category);
            m.Facts.Add(new AtlasFact("Kind", SkillCategory(sk.Category), F));
            if (!string.IsNullOrWhiteSpace(sk.Description)) m.Texts.Add(new AtlasText("About", sk.Description.Trim(), F));
            var recipes = SpellData.RecipesOfSkill(id);
            if (recipes.Count > 0)
            {
                var s = Section(m, "recipes", "Recipes");
                s.Note = AtlasFormat.Count(recipes.Count, "recipe", "recipes") + ", lowest skill first.";
                foreach (var r in recipes.Take(MaxZoneRows))
                {
                    var spell = SpellData.Spell(r.SpellId);
                    AtlasRow row;
                    if (r.ItemId > 0) { row = ItemRow(r.ItemId); row.Detail = spell?.Name ?? ""; }
                    else row = new AtlasRow { Title = spell?.Name ?? "Recipe " + r.SpellId, IconId = spell?.IconId ?? 0 };
                    row.Link = AtlasIds.Spell(r.SpellId); row.Key = "recipe:" + r.SpellId;
                    row.Label = F; row.Tier = 1; row.Type = "Recipe";
                    row.Detail = ((row.Detail.Length > 0 ? row.Detail + " · " : "") + "Skill " + r.MinRank + (r.ReagentCount > 0 ? " · " + ReagentText(r) : ""));
                    row.Sort = -r.MinRank;
                    s.Rows.Add(row);
                }
                if (recipes.Count > MaxZoneRows) s.HiddenText = (recipes.Count - MaxZoneRows) + " more recipes not listed";
            }
            else
            {
                var abil = SpellData.SpellsOfSkill(id).Take(MaxZoneRows).ToList();
                if (abil.Count > 0)
                {
                    var s = Section(m, "abilities", "What it teaches");
                    foreach (var a in abil)
                    {
                        var spell = SpellData.Spell(a.Spell);
                        if (spell == null || string.IsNullOrEmpty(spell.Name)) continue;
                        s.Rows.Add(new AtlasRow { Key = "spell:" + a.Spell, Link = AtlasIds.Spell(a.Spell), Title = spell.Name, IconId = spell.IconId, Label = F, Tier = 1, Type = "Spell", Detail = spell.Rank ?? "" });
                    }
                }
            }
            return m;
        }

        static AtlasPageModel BuildFaction(int id)
        {
            var f = FactionData.Faction(id);
            if (f == null) return NotFoundPage("faction", id, SpellData.Ready ? "Your game has no faction with this id." : "Faction data is read from your game files and is not ready yet.");
            string F = AtlasLabel.Forever;
            var m = new AtlasPageModel { Id = "faction:" + id, Kind = "faction", Title = f.Name };
            AddLayer(m, "client");
            m.Subtitle = "Faction " + id;
            if (f.ParentId > 0 && FactionData.Name(f.ParentId) != null) m.Facts.Add(new AtlasFact("Part of", FactionData.Name(f.ParentId), F, AtlasIds.Faction(f.ParentId)));
            if (!string.IsNullOrWhiteSpace(f.Description)) m.Texts.Add(new AtlasText("About", f.Description.Trim(), F));
            var kids = SpellData.Factions.Factions.Values.Where(x => x.ParentId == id && !string.IsNullOrEmpty(x.Name)).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
            if (kids.Count > 0)
            {
                var s = Section(m, "members", "Factions in it");
                foreach (var k in kids) s.Rows.Add(new AtlasRow { Key = "faction:" + k.Id, Link = AtlasIds.Faction(k.Id), Title = k.Name, Label = F, Tier = 1, Type = "Faction" });
            }
            return m;
        }
    }
}
