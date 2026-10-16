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

        // the races a seen quest's mask names (ChrRaces.PlayableRaceBit bits): "Horde" / "Alliance" when it is exactly one side's playable races, else the names
        public static string SeenRaces(ulong mask)
        {
            var cd = ClassData.Data;
            if (cd == null || mask == WdbQuest.AllRaces || mask == 0) return "";
            var playable = cd.PlayableRaces().Where(r => r.BitMask != 0).ToList();
            var hit = playable.Where(r => (mask & r.BitMask) != 0).ToList();
            if (hit.Count == 0 || hit.Count == playable.Count) return "";
            foreach (var side in new[] { RaceInfo.Alliance, RaceInfo.Horde })
            {
                var all = playable.Where(r => r.Side == side).ToList();
                if (all.Count > 0 && hit.Count == all.Count && hit.All(r => r.Side == side)) return side == RaceInfo.Alliance ? "Alliance" : "Horde";
            }
            return string.Join(", ", hit.Select(r => r.Name).Distinct());
        }

        static string GoalText(WdbObjective g, out string link)
        {
            link = null;
            string n = g.Amount > 1 ? " ×" + g.Amount.ToString(CultureInfo.InvariantCulture) : "";
            switch (g.Type)
            {
                case 0: link = "npc:" + g.ObjectId; return (g.Text.Length > 0 ? g.Text : "Kill or talk to " + NpcName(g.ObjectId)) + n;
                case 1: link = AtlasIds.Item(g.ObjectId); return (g.Text.Length > 0 ? g.Text : "Bring " + ItemName(g.ObjectId)) + n;
                case 2: link = "obj:" + g.ObjectId; return (g.Text.Length > 0 ? g.Text : "Use " + ObjectName(g.ObjectId)) + n;
                case 6:
                    var fn = FactionData.Name(g.ObjectId);
                    if (fn != null) link = AtlasIds.Faction(g.ObjectId);
                    return g.Text.Length > 0 ? g.Text : "Reputation with " + (fn ?? "faction " + g.ObjectId) + " (" + g.Amount.ToString("N0", CultureInfo.InvariantCulture) + ")";
                case 10: return g.Text.Length > 0 ? g.Text : "Explore an area";
                default: return g.Text;
            }
        }

        static AtlasPageModel BuildQuest(int id)
        {
            var seen = SeenView.Quest(id, out bool seenFromLodge);
            var cq = ClassicAtlas.QuestInfo(id);
            var L = Lodge();
            var lq = L?.Quest(id);
            if (seen == null && cq == null && lq == null)
            {
                bool exists = SpellData.World != null && SpellData.World.QuestIds.Contains(id);
                bool fromHotfix = SpellData.Hotfixes.NewQuests.Contains(id);
                var nf = NotFoundPage("quest", id, fromHotfix ? "Your game knows this quest exists from a server hotfix (it is not in the game files), but nobody's game in your Lodge has been shown it yet."
                    : exists ? "Your game knows this quest exists, but nobody's game in your Lodge has been shown it yet." : "No layer knows this quest. Your game has not shown it to you yet.");
                LayerNotes(nf, true, false);
                return nf;
            }
            string F = AtlasLabel.Forever, C = AtlasLabel.Classic;
            var m = new AtlasPageModel { Id = "quest:" + id, Kind = "quest", MapFirst = false };
            m.Title = seen != null && seen.Title.Length > 0 ? seen.Title : cq?.Title ?? "Quest " + id;
            if (seen != null) AddLayer(m, seenFromLodge ? "lodge" : "seen");
            if (cq != null) AddLayer(m, "classic");
            if (lq != null) AddLayer(m, "lodge");
            int level = seen != null && seen.Level > 0 ? seen.Level : cq?.QuestLevel ?? 0;
            int min = seen != null && seen.MinLevel > 0 ? seen.MinLevel : cq?.MinLevel ?? 0;
            if (level > 0) m.Facts.Add(new AtlasFact("Level", level.ToString(CultureInfo.InvariantCulture), seen != null && seen.Level > 0 ? F : C));
            if (min > 0) m.Facts.Add(new AtlasFact("Requires level", min.ToString(CultureInfo.InvariantCulture), seen != null && seen.MinLevel > 0 ? F : C));
            if (SpellData.Hotfixes.NewQuests.Contains(id)) m.Facts.Add(new AtlasFact("Source", "A quest the server added (not in the game files)", AtlasLabel.Hotfix));
            int sort = seen != null && seen.Sort != 0 ? seen.Sort : cq?.ZoneOrSort ?? 0;
            var idx = MapData.Index;
            var world = SpellData.World;
            if (sort > 0 && idx != null)
            {
                int ui = idx.UiMapForArea(sort);
                var area = idx.AreaName(sort);
                if (ui > 0) m.Facts.Add(new AtlasFact("Zone", ZoneName(ui), seen != null && seen.Sort != 0 ? F : C, "zone:" + ui));
                else if (!string.IsNullOrEmpty(area)) m.Facts.Add(new AtlasFact("Zone", area, seen != null && seen.Sort != 0 ? F : C));
            }
            else if (sort < 0 && world?.SortName(sort) is string sortName)
            {
                var skill = SpellData.SkillByName(sortName);
                var cls = ClassData.Data?.Classes.Values.FirstOrDefault(c => string.Equals(c.Name, sortName, StringComparison.OrdinalIgnoreCase));
                m.Facts.Add(new AtlasFact("Category", sortName, F, cls != null ? AtlasIds.Class(cls.Id) : skill != null ? AtlasIds.Skill(skill.Id) : null));
            }
            bool seenFull = seen != null && seen.Full;
            if (seenFull)
            {
                var kind = new List<string>();
                if (world?.InfoName(seen.InfoId) is string info) kind.Add(info);
                if (seen.SuggestedGroup > 1) kind.Add("group of " + seen.SuggestedGroup.ToString(CultureInfo.InvariantCulture));
                if (kind.Count > 0) m.Facts.Add(new AtlasFact("Kind", string.Join(", ", kind), F));
                var races = SeenRaces(seen.Races);
                if (races.Length > 0) m.Facts.Add(new AtlasFact("Races", races, F));
                else if (cq != null && cq.RequiredRaces != 0 && Races(cq.RequiredRaces).Length > 0) m.Facts.Add(new AtlasFact("Races", Races(cq.RequiredRaces), C));   // Forever names no races: the baseline's, labelled
                if ((seen.Flags & 0x8) != 0) m.Facts.Add(new AtlasFact("Shareable", "Yes, with your group", F));
                if (seen.StartItem > 0) m.Facts.Add(new AtlasFact("Starts from", ItemName(seen.StartItem), F, AtlasIds.Item(seen.StartItem)));
                if (seen.Money > 0) m.Facts.Add(new AtlasFact("Money", AtlasFormat.Money(seen.Money), F));
                if (seen.RewardSpell > 0) m.Facts.Add(new AtlasFact("Reward spell", SpellName(seen.RewardSpell), F, AtlasIds.Spell(seen.RewardSpell)));
                foreach (var f in seen.RepFactions)
                    m.Facts.Add(new AtlasFact("Reputation", FactionData.Name(f) ?? "Faction " + f, F, FactionData.Name(f) != null ? AtlasIds.Faction(f) : null));
                if (seen.NextQuest > 0) m.Facts.Add(new AtlasFact("Leads to", QuestTitle(seen.NextQuest), F, "quest:" + seen.NextQuest));
            }
            else
            {
                if (cq != null && cq.RequiredRaces != 0 && Races(cq.RequiredRaces).Length > 0) m.Facts.Add(new AtlasFact("Races", Races(cq.RequiredRaces), C));
            }
            if (cq != null && cq.RequiredClasses != 0 && Classes(cq.RequiredClasses).Length > 0) m.Facts.Add(new AtlasFact("Classes", Classes(cq.RequiredClasses), C));
            var line = world?.LineOf(id);
            if (line != null && line.Name.Length > 0)
            {
                int pos = Array.IndexOf(line.Quests, id) + 1;
                m.Facts.Add(new AtlasFact("Quest line", line.Name + (pos > 0 ? " (" + pos + " of " + line.Quests.Length + ")" : ""), F));
            }
            // the chain (2.77): step N of M, what comes before and after, prerequisites, breadcrumbs, exclusive groups (QuestChains; the card on the Overview)
            var chain = QuestChains.LiveFor(id);
            if (chain.Any) m.Custom = chain;
            if (chain.HasChain && !chain.FromLine) m.Facts.Add(new AtlasFact("Chain", chain.StepText, chain.AnyClassic ? C : F));
            if (cq != null && cq.RequiredSkill > 0)
                m.Facts.Add(new AtlasFact("Requires skill", SkillName(cq.RequiredSkill) + (cq.RequiredSkillValue > 0 ? " " + cq.RequiredSkillValue.ToString(CultureInfo.InvariantCulture) : ""), C, AtlasIds.Skill(cq.RequiredSkill)));
            if (cq != null && cq.RequiredMinRepFaction > 0)
                m.Facts.Add(new AtlasFact("Standing", QuestChains.Standing(cq.RequiredMinRepValue) + " with " + (FactionData.Name(cq.RequiredMinRepFaction) ?? "faction " + cq.RequiredMinRepFaction), C, FactionData.Name(cq.RequiredMinRepFaction) != null ? AtlasIds.Faction(cq.RequiredMinRepFaction) : null));
            if (cq != null && cq.RequiredMaxRepFaction > 0)
                m.Facts.Add(new AtlasFact("Standing under", QuestChains.Standing(cq.RequiredMaxRepValue) + " with " + (FactionData.Name(cq.RequiredMaxRepFaction) ?? "faction " + cq.RequiredMaxRepFaction), C, FactionData.Name(cq.RequiredMaxRepFaction) != null ? AtlasIds.Faction(cq.RequiredMaxRepFaction) : null));
            if (cq != null && cq.SourceItem > 0 && !(seenFull && seen.StartItem > 0)) m.Facts.Add(new AtlasFact("You are given", ItemName(cq.SourceItem), C, AtlasIds.Item(cq.SourceItem)));
            m.Subtitle = string.Join(" · ", new[] { "Quest " + id, level > 0 ? "Level " + level : "" }.Where(s => s.Length > 0));
            if (seen != null && !string.IsNullOrWhiteSpace(seen.Objectives)) m.Texts.Add(new AtlasText("Objectives", seen.Objectives.Trim(), F));
            if (seen != null && !string.IsNullOrWhiteSpace(seen.Description)) m.Texts.Add(new AtlasText("Description", seen.Description.Trim(), F));
            if (seenFull && !string.IsNullOrWhiteSpace(seen.CompletionLog)) m.Texts.Add(new AtlasText("When it is done", seen.CompletionLog.Trim(), F));
            if (seenFromLodge) m.Notes.Add("This quest's details come from a Lodge member's game (your own game has not shown it to you" + (SeenData.Quest(id) != null ? " in this version" : "") + ").");

            // what it asks for (the server's own objective list)
            if (seenFull && seen.Goals.Length > 0)
            {
                var s = Section(m, "goals", "Goals");
                foreach (var g in seen.Goals)
                {
                    var text = GoalText(g, out var link);
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    var row = g.Type == 1 ? ItemRow(g.ObjectId, g.Amount > 1 ? "×" + g.Amount : "", F) : new AtlasRow { Title = text, Link = link, Key = "goal:" + g.Type + ":" + g.ObjectId, Label = F, Tier = 1 };
                    if (g.Type == 1 && g.Text.Length > 0) row.Detail = g.Text + (g.Amount > 1 ? " ×" + g.Amount : "");
                    row.Type = g.Type == 0 ? "NPC" : g.Type == 1 ? "Item" : g.Type == 2 ? "Object" : g.Type == 6 ? "Reputation" : "Goal";
                    s.Rows.Add(row);
                }
                if (s.Rows.Count == 0) m.Sections.Remove(s);
            }

            // reward items: what the server says (seen), what the Lodge saw handed out, then the Classic baseline's table on the same rows
            void ItemList(string key, string title, IEnumerable<WdbItemCount> seenItems, IEnumerable<int> lodgeItems, int[] classicItems, int[] classicCounts, string note = "")
            {
                var s = Section(m, key, title);
                s.Note = note;
                var by = new Dictionary<int, AtlasRow>();
                foreach (var it in seenItems ?? Enumerable.Empty<WdbItemCount>())
                {
                    if (by.ContainsKey(it.Id)) continue;
                    var r = ItemRow(it.Id, it.Count > 1 ? "×" + it.Count : "", F); r.Type = "Reward"; r.Tier = 0; by[it.Id] = r; s.Rows.Add(r);
                }
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
            ItemList("rewards", "Rewards", seenFull ? seen.Rewards : null, lq?.Rewards, cq?.RewardItems, cq?.RewardCounts);
            ItemList("choices", "Choose one of", seenFull ? seen.Choices : null, lq?.Choices, cq?.ChoiceItems, cq?.ChoiceCounts);
            if (seenFull && seen.Provided.Length > 0) ItemList("provided", "You are given", seen.Provided, null, null, null, "Items the quest hands you for its goals.");
            if (!(seenFull && seen.Goals.Any(g => g.Type == 1))) ItemList("objectives", "Needs", null, null, cq?.ObjectiveItems, cq?.ObjectiveCounts, "Items the quest asks for.");

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
            if (cq != null && cq.Enders.Length > 0)
            {
                var s = Section(m, "enders", "Turn in to");
                s.Note = "From the Classic baseline.";
                foreach (var g in cq.Enders.Take(12))
                {
                    var spawns = g.IsObject ? ClassicAtlas.Current?.ObjectSpawns(g.Id) : ClassicAtlas.Spawns(g.Id);
                    var name = g.IsObject ? ObjectName(g.Id) : NpcName(g.Id);
                    s.Rows.Add(new AtlasRow { Key = (g.IsObject ? "obj:" : "npc:") + g.Id, Link = (g.IsObject ? "obj:" : "npc:") + g.Id, Title = name, Label = C, Tier = 2, Type = g.IsObject ? "Object" : "NPC", Zone = ZoneOfSpawns(spawns), Detail = ZoneOfSpawns(spawns) });
                    if (!cq.Givers.Any(x => x.Id == g.Id && x.IsObject == g.IsObject)) plan.Classic(spawns, MarkerKind.Quest, name, "Takes " + m.Title + " back · " + C, (g.IsObject ? "obj:" : "npc:") + g.Id);
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
            string F = AtlasLabel.Forever, H = AtlasLabel.Hotfix;
            var m = new AtlasPageModel { Id = "spell:" + id, Kind = "spell", Title = sp.Name, IconId = sp.IconId };
            AddLayer(m, "client");
            // what the server's hotfix rows changed is labelled "Server hotfix" (a new spell: everything), the rest is the game files' own
            var hf = sp.Hotfix;
            bool hNew = (hf & HotfixParts.New) != 0;
            string Src(HotfixParts part) => hNew || (hf & part) != 0 ? H : F;
            m.Subtitle = string.Join(" · ", new[] { "Spell " + id, sp.Rank ?? "" }.Where(s => s.Length > 0));
            if (hf != HotfixParts.None) m.Facts.Add(new AtlasFact("Source", ServerHotfixes.Describe(hf), H));
            if (!string.IsNullOrEmpty(sp.Rank)) m.Facts.Add(new AtlasFact("Rank", sp.Rank, Src(HotfixParts.Text)));
            if (!string.IsNullOrEmpty(sp.CostText)) m.Facts.Add(new AtlasFact("Cost", sp.CostText, F));
            m.Facts.Add(new AtlasFact("Cast time", Seconds(sp.CastMs), Src(HotfixParts.Numbers)));
            if (sp.CooldownMs > 0) m.Facts.Add(new AtlasFact("Cooldown", ClassSnapshot.CooldownText(sp.CooldownMs).Replace(" cooldown", ""), F));
            if (sp.RangeMax > 0) m.Facts.Add(new AtlasFact("Range", sp.RangeMax.ToString("0.##", CultureInfo.InvariantCulture) + " yards", Src(HotfixParts.Numbers)));
            if (sp.DurationMs > 0) m.Facts.Add(new AtlasFact("Duration", Seconds(sp.DurationMs), Src(HotfixParts.Numbers)));
            else if (sp.DurationMs < 0) m.Facts.Add(new AtlasFact("Duration", "Until cancelled", Src(HotfixParts.Numbers)));
            if (!string.IsNullOrWhiteSpace(sp.Description)) m.Texts.Add(new AtlasText("Description", sp.Description.Trim(), Src(HotfixParts.Text | HotfixParts.Effects)));
            if (!string.IsNullOrWhiteSpace(sp.AuraDescription)) m.Texts.Add(new AtlasText("While it lasts", sp.AuraDescription.Trim(), Src(HotfixParts.Text | HotfixParts.Effects)));
            AddSpellOrigins(m, id);
            var talentLink = AtlasHosts.TalentTreeLink(id, out var talentClass);
            if (talentLink != null) m.Facts.Add(new AtlasFact("Talent of", talentClass, F, talentLink));
            var rec = SpellData.Spells?.RecipeOf(id);
            if (rec != null)
            {
                var skill = SpellData.Skill(rec.SkillId);
                var s = Section(m, "recipe", "Recipe");
                s.Note = (skill?.Name ?? "Profession") + (rec.MinRank > 0 ? ", needs skill " + rec.MinRank : "") + (rec.Green > 0 ? " · green at " + rec.Green : "") + (rec.Gray > 0 ? " · grey at " + rec.Gray : "");
                if (skill != null) s.Rows.Add(new AtlasRow { Key = "skill:" + skill.Id, Link = AtlasIds.Skill(skill.Id), Title = skill.Name, IconId = SkillIcons.For(skill), Label = F, Tier = 1, Type = "Skill", Detail = "The profession that teaches it" });
                var recLink = AtlasHosts.RecipesLink(skill?.Name);
                if (recLink != null) m.Facts.Add(new AtlasFact("Profession", skill.Name, F, recLink));
                AddRecipeKnowledge(m, new[] { rec });
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
            var m = new AtlasPageModel { Id = "skill:" + id, Kind = "skill", Title = sk.Name, IconId = SkillIcons.For(sk) };
            AddLayer(m, "client");
            m.Subtitle = "Skill " + id + " · " + SkillCategory(sk.Category);
            m.Facts.Add(new AtlasFact("Kind", SkillCategory(sk.Category), F));
            var profLink = sk.IsProfession ? AtlasHosts.RecipesLink(sk.Name) : null;
            if (profLink != null) m.Facts.Add(new AtlasFact("Professions page", sk.Name + ": recipes, leveling guide, what you can craft", F, profLink));
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
            var m = new AtlasPageModel { Id = "faction:" + id, Kind = "faction", Title = f.Name, IconId = FactionIcons.For(id) };
            AddLayer(m, "client");
            m.Subtitle = "Faction " + id;
            if (f.ParentId > 0 && FactionData.Name(f.ParentId) != null) m.Facts.Add(new AtlasFact("Part of", FactionData.Name(f.ParentId), F, AtlasIds.Faction(f.ParentId)));
            var sideText = FactionData.SideFact(FactionData.SideOf(id));
            if (!string.Equals(FactionData.Name(f.ParentId), sideText, StringComparison.OrdinalIgnoreCase) && !string.Equals(f.Name, sideText, StringComparison.OrdinalIgnoreCase)) m.Facts.Add(new AtlasFact("Side", sideText, F));   // "Part of Alliance" already says it
            if (!string.IsNullOrWhiteSpace(f.Description)) m.Texts.Add(new AtlasText("About", f.Description.Trim(), F));
            var kids = SpellData.Factions.Factions.Values.Where(x => x.ParentId == id && !string.IsNullOrEmpty(x.Name)).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
            if (kids.Count > 0)
            {
                var s = Section(m, "members", "Factions in it");
                foreach (var k in kids) s.Rows.Add(new AtlasRow { Key = "faction:" + k.Id, Link = AtlasIds.Faction(k.Id), Title = k.Name, IconId = FactionIcons.For(k.Id), Detail = BestOwnStanding(k.Id), Label = F, Tier = 1, Type = "Faction" });
            }
            if (f.ReputationIndex >= 0) { AddOwnStandings(m, id); AddLodgeStandings(m, id); }
            return m;
        }
    }
}
