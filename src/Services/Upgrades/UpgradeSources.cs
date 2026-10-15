using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Atlas.Classic;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Maps;

namespace ElansAddonHub.Services.Upgrades
{
    // ============================================================================================================================
    // Where an item can be had, for ONE character: the Atlas's own sources (Lodge drops, vendors and quest rewards first, then the Classic baseline's drops,
    // vendors and quests, then the client's recipes), with what the character cannot use left out (a vendor or quest of the other faction, a quest for
    // another race or class). Nothing here asks the network: the Lodge answers come from what the Outpost already holds.
    // ============================================================================================================================
    public sealed class UpgradeWorld
    {
        public UpgradeChar Who;
        public LodgeAtlas Lodge;
        public MapIndex Maps;
        public bool ClassicReady;
        public ulong RaceBit;                                  // the character's race in the race masks of quests (0 = not known)
        public int ClassMask;                                  // the character's class in the class masks of quests (0 = not known)
        public List<InvChar> Chars = new List<InvChar>();      // all of my characters (for "Bob can make it")
        readonly Dictionary<int, NpcFacts> npcs = new Dictionary<int, NpcFacts>();

        public sealed class NpcFacts
        {
            public string Name = "", Zone = ""; public int Level; public bool Dungeon, Raid, Hostile; public int ZoneMap; public double Fx = -1, Fy = -1;
        }

        public static UpgradeWorld Create(UpgradeChar who)
        {
            var w = new UpgradeWorld { Who = who, Lodge = AtlasQuery.LodgeOrNull(), Maps = MapData.Index, ClassicReady = ClassicAtlas.IsReady };
            try { w.Chars = AtlasQuery.Characters(); } catch (Exception) { w.Chars = new List<InvChar>(); }
            w.RaceBit = RaceBitOf(who?.Race);
            w.ClassMask = ClassMaskOf(who?.ClassFile);
            return w;
        }

        // classic race / class bits (race id - 1, class id - 1); the client's own tables win when they are read
        public static ulong RaceBitOf(string race)
        {
            if (string.IsNullOrEmpty(race)) return 0;
            var spaced = System.Text.RegularExpressions.Regex.Replace(race, "(?<=[a-z])(?=[A-Z])", " ");
            var r = ClassData.Data?.RaceByName(spaced) ?? ClassData.Data?.RaceByName(race);
            if (r != null && r.BitMask != 0) return r.BitMask;
            switch (race.Replace(" ", "").ToLowerInvariant())
            {
                case "human": return 1; case "orc": return 2; case "dwarf": return 4; case "nightelf": return 8; case "scourge": case "undead": return 16;
                case "tauren": return 32; case "gnome": return 64; case "troll": return 128; case "bloodelf": return 512; case "draenei": return 1024;
                default: return 0;
            }
        }

        public static int ClassMaskOf(string file)
        {
            if (string.IsNullOrEmpty(file)) return 0;
            var c = ClassData.Data?.ClassByFile(file);
            if (c != null && c.Mask != 0) return c.Mask;
            switch (file.ToUpperInvariant())
            {
                case "WARRIOR": return 1; case "PALADIN": return 2; case "HUNTER": return 4; case "ROGUE": return 8; case "PRIEST": return 16; case "SHAMAN": return 64;
                case "MAGE": return 128; case "WARLOCK": return 256; case "DRUID": return 1024;
                default: return 0;
            }
        }

        // ---- npc facts
        InstanceInfo InstanceOfUiMap(int uiMap)
            => Maps?.Instances.FirstOrDefault(i => !i.Placeholder && i.Floors.Any(f => f.Id == uiMap));

        public NpcFacts Npc(int id)
        {
            if (npcs.TryGetValue(id, out var f)) return f;
            f = new NpcFacts { Name = AtlasQuery.NpcName(id) };
            npcs[id] = f;
            var cn = ClassicAtlas.NpcInfo(id);
            if (cn != null)
            {
                f.Level = Math.Max(cn.MinLevel, cn.MaxLevel);
                f.Zone = AtlasQuery.ZoneOfSpawns(cn.Spawns);
                if (Maps != null && cn.Spawns != null && cn.Spawns.Length > 0)
                {
                    int inst = 0, instType = 0, placed = 0;
                    foreach (var s in cn.Spawns)
                    {
                        var info = Maps.Instance(s.Map);
                        if (info != null && !info.Placeholder) { inst++; if (info.InstanceType > instType) instType = info.InstanceType; }
                        else placed++;
                    }
                    if (inst > placed) { f.Dungeon = instType == 1; f.Raid = instType == 2; }
                    if (instType == 3 || instType == 4) { f.Dungeon = false; f.Raid = false; }
                    foreach (var s in cn.Spawns)                      // the first spawn that sits on a zone map: "Show on world map"
                    {
                        var p = Maps.WorldToMap(s.Map, s.X, s.Y);
                        if (p != null) { f.ZoneMap = p.Value.UiMapId; f.Fx = p.Value.Fx; f.Fy = p.Value.Fy; break; }
                    }
                }
                f.Hostile = IsHostile(cn.FactionId);
            }
            return f;
        }

        // hostile to the character's side (the faction template of the npc through the client's FactionTemplate data); unknown = not hostile
        bool IsHostile(int template)
        {
            if (template <= 0 || !FactionData.Ready) return false;
            var side = Who?.Side;
            if (side == "Alliance") return FactionData.ToAlliance(template) == Standing.Hostile;
            if (side == "Horde") return FactionData.ToHorde(template) == Standing.Hostile;
            return false;
        }

        // ---- the sources of one item
        public List<UpgradeSource> SourcesOf(ClientItem item, int level)
        {
            var res = new List<UpgradeSource>();
            if (item == null) return res;
            int id = item.Id;
            var seen = new Dictionary<string, UpgradeSource>();

            // Lodge: kills and openings (with the chance), vendors, quests, fishing
            if (Lodge != null)
            {
                foreach (var d in Lodge.Drops(id))
                {
                    string key = d.Kind + ":" + d.Id;
                    var s = new UpgradeSource { Lodge = true, Chance = d.Chance };
                    if (d.Kind == "fish")
                    {
                        s.Kind = "fishing"; s.What = "Fishing"; s.Zone = AtlasQuery.ZoneName(d.Id); s.ZoneMap = d.Id;
                        s.Link = "zone:" + d.Id; if (!seen.ContainsKey(key)) { seen[key] = s; res.Add(s); }
                        continue;
                    }
                    if (d.Kind == "obj")
                    {
                        s.Kind = "container"; s.What = AtlasQuery.ObjectName(d.Id); s.Link = "obj:" + d.Id;
                        var co = ClassicAtlas.ObjectInfo(d.Id);
                        if (co != null && co.Spawns != null) s.Zone = AtlasQuery.ZoneOfSpawns(co.Spawns);
                    }
                    else
                    {
                        var nf = Npc(d.Id);
                        s.Kind = "drop"; s.What = nf.Name; s.Link = "npc:" + d.Id; s.Level = nf.Level; s.Zone = nf.Zone; s.Dungeon = nf.Dungeon; s.Raid = nf.Raid; s.ZoneMap = nf.ZoneMap; s.Fx = nf.Fx; s.Fy = nf.Fy;
                    }
                    if (d.Zones.Count > 0)                         // where the Lodge saw it: the zone with most kills, an instance when it is one
                    {
                        int ui = d.Zones[0].Key;
                        var inst = InstanceOfUiMap(ui);
                        if (inst != null) { s.Zone = inst.Name; s.Dungeon = inst.InstanceType == 1; s.Raid = inst.InstanceType == 2; }
                        else if (string.IsNullOrEmpty(s.Zone)) s.Zone = AtlasQuery.ZoneName(ui);
                        if (s.ZoneMap == 0 && inst == null) s.ZoneMap = ui;
                    }
                    if (!seen.ContainsKey(key)) { seen[key] = s; res.Add(s); }
                }
                foreach (var v in Lodge.Vendors(id))
                {
                    var nf = Npc(v.NpcId);
                    if (nf.Hostile) continue;
                    string key = "vendor:" + v.NpcId;
                    var s = new UpgradeSource { Kind = "vendor", Lodge = true, What = nf.Name, Link = "npc:" + v.NpcId, Level = nf.Level, Price = v.Price, PriceSpecial = v.Extended, Zone = v.Zone > 0 ? AtlasQuery.ZoneName(v.Zone) : nf.Zone, ZoneMap = v.Zone > 0 ? v.Zone : nf.ZoneMap, Fx = v.Zone > 0 ? v.X : nf.Fx, Fy = v.Zone > 0 ? v.Y : nf.Fy };
                    if (!seen.ContainsKey(key)) { seen[key] = s; res.Add(s); }
                }
                foreach (var q in Lodge.QuestsRewarding(id))
                {
                    var s = Quest(q.QuestId, true, level);
                    if (s != null && !seen.ContainsKey("quest:" + q.QuestId)) { seen["quest:" + q.QuestId] = s; res.Add(s); }
                }
            }

            // the Classic baseline: what the Lodge did not see (a row both know keeps the Lodge's figures)
            if (ClassicReady)
            {
                foreach (var cs in ClassicAtlas.SourcesOf(id))
                {
                    switch (cs.Kind)
                    {
                        case ClassicSourceKind.NpcDrop:
                            {
                                if (cs.Chance < AtlasQuery.TinyChance) break;
                                string key = "npc:" + cs.SourceId;
                                if (seen.ContainsKey("drop:" + cs.SourceId) || seen.ContainsKey("npc:" + cs.SourceId)) break;
                                var nf = Npc(cs.SourceId);
                                var s = new UpgradeSource { Kind = "drop", What = nf.Name, Link = "npc:" + cs.SourceId, Chance = cs.Chance, Level = nf.Level, Zone = nf.Zone, Dungeon = nf.Dungeon, Raid = nf.Raid, ZoneMap = nf.ZoneMap, Fx = nf.Fx, Fy = nf.Fy };
                                if (cs.QuestOnly) s.Note = "only on the quest";
                                seen[key] = s; res.Add(s);
                                break;
                            }
                        case ClassicSourceKind.ObjectLoot:
                            {
                                if (cs.Chance < AtlasQuery.TinyChance) break;
                                string key = "obj:" + cs.SourceId;
                                if (seen.ContainsKey("obj:" + cs.SourceId)) break;
                                var co = ClassicAtlas.ObjectInfo(cs.SourceId);
                                var s = new UpgradeSource { Kind = "container", What = AtlasQuery.ObjectName(cs.SourceId), Link = "obj:" + cs.SourceId, Chance = cs.Chance, Zone = co?.Spawns != null ? AtlasQuery.ZoneOfSpawns(co.Spawns) : "" };
                                if (Maps != null && co?.Spawns != null)
                                    foreach (var sp in co.Spawns) { var p = Maps.WorldToMap(sp.Map, sp.X, sp.Y); if (p != null) { s.ZoneMap = p.Value.UiMapId; s.Fx = p.Value.Fx; s.Fy = p.Value.Fy; break; } }
                                if (Maps != null && co?.Spawns != null && co.Spawns.Length > 0 && co.Spawns.All(sp => { var ii = Maps.Instance(sp.Map); return ii != null && !ii.Placeholder; }))
                                { var ii = Maps.Instance(co.Spawns[0].Map); s.Dungeon = ii.InstanceType == 1; s.Raid = ii.InstanceType == 2; }
                                seen[key] = s; res.Add(s);
                                break;
                            }
                        case ClassicSourceKind.Vendor:
                            {
                                string key = "vendor:" + cs.SourceId;
                                if (seen.ContainsKey(key)) break;
                                var nf = Npc(cs.SourceId);
                                if (nf.Hostile) break;
                                var s = new UpgradeSource { Kind = "vendor", What = nf.Name, Link = "npc:" + cs.SourceId, Level = nf.Level, Zone = nf.Zone, ZoneMap = nf.ZoneMap, Fx = nf.Fx, Fy = nf.Fy };
                                if (cs.MaxCount > 0) s.Note = "limited stock";
                                seen[key] = s; res.Add(s);
                                break;
                            }
                        case ClassicSourceKind.QuestReward:
                        case ClassicSourceKind.QuestChoice:
                            {
                                string key = "quest:" + cs.SourceId;
                                if (seen.ContainsKey(key)) break;
                                var s = Quest(cs.SourceId, false, level);
                                if (s != null) { seen[key] = s; res.Add(s); }
                                break;
                            }
                    }
                }
            }

            // recipes of the client (the game's own data): the profession, the skill, who of mine can make it
            foreach (var r in SpellData.RecipesProducing(id))
            {
                var skill = SpellData.Skill(r.SkillId);
                string prof = skill?.Name ?? "Profession";
                string key = "craft:" + r.SkillId;
                if (seen.ContainsKey(key)) continue;
                var s = new UpgradeSource { Kind = "craft", Lodge = true, What = AtlasQuery.SpellName(r.SpellId), Link = AtlasIds.Spell(r.SpellId), Spell = r.SpellId, Profession = prof + (r.MinRank > 0 ? " " + r.MinRank : ""), Skill = r.MinRank };
                MakersOf(s, r, prof);
                seen[key] = s; res.Add(s);
            }
            return res;
        }

        // a quest reward: null when the character cannot take it (another race / class, a giver of the other faction)
        UpgradeSource Quest(int questId, bool lodge, int level)
        {
            var cq = ClassicAtlas.QuestInfo(questId);
            var s = new UpgradeSource { Kind = "quest", Lodge = lodge, What = AtlasQuery.QuestTitle(questId), Link = "quest:" + questId };
            if (cq != null)
            {
                if (cq.RequiredRaces != 0 && RaceBit != 0 && ((ulong)(uint)cq.RequiredRaces & RaceBit) == 0) return null;
                if (cq.RequiredClasses != 0 && ClassMask != 0 && (cq.RequiredClasses & ClassMask) == 0) return null;
                if (cq.Givers.Length > 0 && cq.Givers.All(g => !g.IsObject && Npc(g.Id).Hostile)) return null;
                s.Level = cq.QuestLevel;
                if (cq.ZoneOrSort > 0 && Maps != null) s.Zone = Maps.AreaName(cq.ZoneOrSort) ?? "";
                if (cq.MinLevel > level) { s.NeedsLevel = cq.MinLevel; s.Note = "needs level " + cq.MinLevel; }
                var giver = cq.Givers.FirstOrDefault(g => !g.IsObject);
                if (giver != null && Maps != null)
                {
                    var gn = Npc(giver.Id);
                    if (gn.ZoneMap > 0) { s.ZoneMap = gn.ZoneMap; s.Fx = gn.Fx; s.Fy = gn.Fy; if (string.IsNullOrEmpty(s.Zone)) s.Zone = gn.Zone; }
                }
            }
            return s;
        }

        // who of mine knows the recipe: "You can make it" for the shown character, "Bob can make it" for another
        public void MakersOf(UpgradeSource s, Recipe r, string profession)
        {
            var names = new List<string>();
            foreach (var c in Chars)
            {
                if (c.Hub == null) continue;
                var pm = HubModules.ProfessionsOf(c.Hub);
                if (pm == null) continue;
                var scan = pm.Known.Values.FirstOrDefault(k => r.SkillId > 0 && k.Line == r.SkillId) ?? pm.Known.Values.FirstOrDefault(k => string.Equals(k.Name, profession, StringComparison.OrdinalIgnoreCase));
                if (scan != null && scan.Ids.Contains(r.SpellId))
                {
                    bool me = c.Key == Who?.Key || (string.IsNullOrEmpty(Who?.Key) && c.Name == Who?.Name);
                    if (me) s.CanCraftNow = true; else s.OwnMaker = true;
                    names.Add(me ? "You can make it" : c.Name + " can make it");
                }
            }
            foreach (var n in names.OrderBy(n => n.StartsWith("You") ? 0 : 1).ThenBy(n => n, StringComparer.CurrentCultureIgnoreCase)) s.Makers.Add(n);
            // the shown character has the profession but has not learned the recipe yet: say so in numbers
            var me2 = Chars.FirstOrDefault(c => c.Key == Who?.Key);
            var mp = me2?.Hub == null ? null : HubModules.ProfessionsOf(me2.Hub);
            var have = mp?.List.FirstOrDefault(p => string.Equals(p.Name, profession, StringComparison.OrdinalIgnoreCase));
            if (s.Makers.Count == 0 && have != null) s.Note = have.Skill >= r.MinRank ? "you have the skill" : "your skill is " + have.Skill;
            s.ProfessionSkill = have?.Skill ?? -1;
        }

        // the Lodge's crafters, added from the Armory's answer (cached; "asking" adds nothing yet)
        public void AddLodgeMakers(IEnumerable<UpgradeSource> crafts)
        {
            var ask = AtlasQuery.Context.Crafters;
            if (ask == null) return;
            var list = crafts.Where(c => c.Spell > 0).ToList();
            if (list.Count == 0) return;
            LodgeCrafters r;
            try { r = ask(list.Select(c => c.Spell).Distinct().ToList()); } catch (Exception) { return; }
            if (r == null || !r.Available) return;
            foreach (var c in list)
            {
                int spell = c.Spell;
                var who = r.Rows.Where(x => x.Spell == spell && !x.Mine).Select(x => x.Name + " (" + x.By + ")").Distinct().Take(2).ToList();
                foreach (var w in who) c.Makers.Add(w + " can make it");
                if (who.Count > 0) c.LodgeMaker = true;
            }
        }
    }
}
