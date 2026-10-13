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
        static AtlasPageModel BuildItem(int id)
        {
            var m = new AtlasPageModel { Id = AtlasIds.Item(id), Kind = "item" };
            var L = Lodge();
            var ci = ItemData.Get(id);
            var cl = ClassicAtlas.ItemInfo(id);
            var rev = L?.Revealed(id);
            var inv = CultureInfo.InvariantCulture;

            // ---- identity + the game's own tooltip
            m.Title = ci != null && !string.IsNullOrEmpty(ci.Name) ? ci.Name : rev?.Name ?? cl?.Name ?? "Item " + id.ToString(inv);
            m.Quality = ci?.Quality ?? rev?.Quality ?? cl?.Quality ?? -1;
            m.IconId = ci?.IconId ?? 0;
            if (ci != null)
            {
                m.Tooltip = ItemText.Lines(ci);
                m.TooltipSource = AtlasLabel.Forever;
                AddLayer(m, "client");
                m.Subtitle = "Item " + id + " · from your game data" + (ci.Revealed ? " (revealed by the server)" : "");
            }
            else if (rev != null)
            {
                m.Tooltip = RevealedLines(rev);
                m.TooltipSource = AtlasLabel.Forever;
                AddLayer(m, "lodge");
                m.Subtitle = "Item " + id + " · revealed by a Lodge member's game";
            }
            else if (cl != null)
            {
                if (cl.ItemLevel > 0) m.Tooltip.Add(new TipLine { Kind = "ilvl", Text = "Item Level " + cl.ItemLevel });
                if (cl.RequiredLevel > 1) m.Tooltip.Add(new TipLine { Kind = "req", Text = "Requires Level " + cl.RequiredLevel });
                m.TooltipSource = AtlasLabel.Classic;
                AddLayer(m, "classic");
                m.Subtitle = "Item " + id + " · only in the Classic baseline (your game has not shown it yet)";
                if (ItemData.Ready) m.Tooltip.Add(new TipLine { Kind = "unrevealed", Text = ItemText.NotRevealed });
            }

            // ---- your characters
            var owned = Context.Owned?.Invoke();
            if (owned != null && owned.TryGetValue(id, out var tot) && (tot.Chars.Count > 0 || tot.Guilds.Count > 0))
            {
                var s = Section(m, "owned", "Your characters");
                foreach (var c in tot.Chars.OrderByDescending(x => x.Total).ThenBy(x => x.Char.Name, StringComparer.OrdinalIgnoreCase))
                    s.Rows.Add(new AtlasRow { Title = c.Char.Name, Detail = c.Detail, Extra = c.Char.Realm ?? "", Right = "× " + c.Total.ToString("N0", CalendarRules.English), Type = "Character", Key = "char:" + (c.Char.Key ?? c.Char.Name) });
                foreach (var g in tot.Guilds.OrderByDescending(x => x.Count))
                    s.Rows.Add(new AtlasRow { Title = g.Guild.Name, Detail = "Guild bank", Extra = g.Guild.Realm ?? "", Right = "× " + g.Count.ToString("N0", CalendarRules.English), Type = "Guild", Key = "guild:" + g.Guild.Key });
                s.Note = "Counted in bags, bank, worn gear and guild banks from the last time each was saved.";
            }
            else if (owned != null) { var s = Section(m, "owned", "Your characters", "None of your characters has this item."); }

            // ---- sources
            var src = Section(m, "sources", "Where it comes from", "Nothing is known yet about where this item comes from.");
            var rows = new List<AtlasRow>();
            var tiny = new List<AtlasRow>();
            var byKey = new Dictionary<string, AtlasRow>();
            var plan = new PlanBuilder();

            void AddRow(AtlasRow r) { rows.Add(r); if (r.Key.Length > 0 && !byKey.ContainsKey(r.Key)) byKey[r.Key] = r; }

            // Lodge: observed drops (chance from kills), vendors, quests
            if (L != null)
            {
                foreach (var d in L.Drops(id))
                {
                    bool fish = d.Kind == "fish", obj = d.Kind == "obj";
                    var key = d.Kind + ":" + d.Id;
                    var r = new AtlasRow
                    {
                        Key = key, Link = fish ? "zone:" + d.Id : key, Label = AtlasLabel.Forever, Tier = 0, Sort = d.Chance + Math.Min(d.Kills, 100000) / 1e7,
                        Type = fish ? "Fishing" : obj ? "Object" : "Drop",
                        Title = fish ? "Fishing in " + ZoneName(d.Id) : obj ? ObjectName(d.Id) : NpcName(d.Id),
                        Detail = AtlasFormat.Percent(d.Chance) + " · " + AtlasFormat.Count(d.Kills, fish ? "cast" : obj ? "open" : "kill", fish ? "casts" : obj ? "opens" : "kills") + " · " + AtlasFormat.Count(d.Members, "member", "members"),
                        Zone = d.Zones.Count > 0 ? ZoneName(d.Zones[0].Key) : "",
                    };
                    AddRow(r);
                    AddLayer(m, "lodge");
                }
                foreach (var v in L.Vendors(id))
                {
                    var key = "vendor:" + v.NpcId;
                    var detail = (v.Price > 0 ? AtlasFormat.Money(v.Price) : "free") + (v.Stack > 1 ? " · stack of " + v.Stack : "") + (v.Limit >= 0 ? " · limit " + v.Limit : "");
                    AddRow(new AtlasRow { Key = key, Link = "npc:" + v.NpcId, Label = AtlasLabel.Forever, Tier = 0, Type = "Vendor", Title = NpcName(v.NpcId), Detail = detail, Zone = v.Zone > 0 ? ZoneName(v.Zone) : "", Sort = 1e6 - v.Price });
                    AddLayer(m, "lodge");
                }
                foreach (var q in L.QuestsRewarding(id))
                {
                    AddRow(new AtlasRow { Key = "quest:" + q.QuestId, Link = "quest:" + q.QuestId, Label = AtlasLabel.Forever, Tier = 0, Type = "Quest", Title = QuestTitle(q.QuestId), Detail = q.Choice ? "One of the rewards" : "Reward", Sort = 1 });
                    AddLayer(m, "lodge");
                }
            }

            // Classic baseline
            var needs = new List<AtlasRow>();                    // quests that want the item
            var ci2 = ClassicAtlas.Current;
            if (ci2 != null)
            {
                foreach (var s in ClassicAtlas.SourcesOf(id))
                {
                    AddLayer(m, "classic");
                    switch (s.Kind)
                    {
                        case ClassicSourceKind.NpcDrop:
                        case ClassicSourceKind.ObjectLoot:
                            {
                                bool obj = s.Kind == ClassicSourceKind.ObjectLoot;
                                var key = (obj ? "obj:" : "npc:") + s.SourceId;
                                var chance = AtlasFormat.Percent(s.Chance) + (s.QuestOnly ? " · only while on the quest" : "") + CountText(s);
                                if (byKey.TryGetValue(key, out var seen))
                                {
                                    seen.Extra = (seen.Extra.Length > 0 ? seen.Extra + " · " : "") + AtlasLabel.Classic + ": " + chance;
                                    continue;
                                }
                                var r = new AtlasRow { Key = key, Link = key, Label = AtlasLabel.Classic, Tier = 2, Sort = s.Chance, Type = obj ? "Object" : "Drop", Title = obj ? ObjectName(s.SourceId) : NpcName(s.SourceId), Detail = chance };
                                if (s.Chance < TinyChance) tiny.Add(r); else AddRow(r);
                                break;
                            }
                        case ClassicSourceKind.NpcSkin:
                        case ClassicSourceKind.NpcPickpocket:
                            {
                                bool skin = s.Kind == ClassicSourceKind.NpcSkin;
                                var r = new AtlasRow { Key = (skin ? "skin:" : "pick:") + s.SourceId, Link = "npc:" + s.SourceId, Label = AtlasLabel.Classic, Tier = 2, Sort = s.Chance, Type = skin ? "Skinning" : "Pickpocketing", Title = NpcName(s.SourceId), Detail = AtlasFormat.Percent(s.Chance) + CountText(s) };
                                if (s.Chance < TinyChance) tiny.Add(r); else AddRow(r);
                                break;
                            }
                        case ClassicSourceKind.Vendor:
                            {
                                var key = "vendor:" + s.SourceId;
                                var stock = s.MaxCount > 0 ? "limited stock (" + s.MaxCount + ")" : "unlimited stock";
                                if (byKey.TryGetValue(key, out var seen))
                                {
                                    seen.Extra = (seen.Extra.Length > 0 ? seen.Extra + " · " : "") + AtlasLabel.Classic + ": " + stock;
                                    continue;
                                }
                                AddRow(new AtlasRow { Key = key, Link = "npc:" + s.SourceId, Label = AtlasLabel.Classic, Tier = 2, Type = "Vendor", Title = NpcName(s.SourceId), Detail = "Sold here · " + stock, Sort = 1 });
                                break;
                            }
                        case ClassicSourceKind.QuestReward:
                        case ClassicSourceKind.QuestChoice:
                            {
                                var key = "quest:" + s.SourceId;
                                var what = s.Kind == ClassicSourceKind.QuestChoice ? "One of the rewards" : "Reward";
                                if (byKey.TryGetValue(key, out var seen)) { seen.Extra = (seen.Extra.Length > 0 ? seen.Extra + " · " : "") + AtlasLabel.Classic + ": " + what.ToLowerInvariant(); continue; }
                                var qi = ClassicAtlas.QuestInfo(s.SourceId);
                                AddRow(new AtlasRow { Key = key, Link = key, Label = AtlasLabel.Classic, Tier = 2, Type = "Quest", Title = QuestTitle(s.SourceId), Detail = what + (s.MinCount > 1 ? " ×" + s.MinCount : "") + (qi != null ? " · level " + qi.QuestLevel : ""), Sort = 1 });
                                break;
                            }
                        case ClassicSourceKind.QuestObjective:
                            {
                                var qi = ClassicAtlas.QuestInfo(s.SourceId);
                                needs.Add(new AtlasRow { Key = "questobj:" + s.SourceId, Link = "quest:" + s.SourceId, Label = AtlasLabel.Classic, Tier = 2, Type = "Quest", Title = QuestTitle(s.SourceId), Detail = "Needs " + Math.Max(1, s.MinCount) + (qi != null ? " · level " + qi.QuestLevel : "") });
                                break;
                            }
                    }
                }
            }

            // recipes (the client's own tables)
            foreach (var r in SpellData.RecipesProducing(id))
            {
                var sp = SpellData.Spell(r.SpellId);
                var skill = SpellData.Skill(r.SkillId);
                AddRow(new AtlasRow
                {
                    Key = "recipe:" + r.SpellId, Link = AtlasIds.Spell(r.SpellId), Label = AtlasLabel.Forever, Tier = 1, Type = "Recipe", Title = sp?.Name ?? "Recipe " + r.SpellId, IconId = sp?.IconId ?? 0,
                    Detail = (skill?.Name ?? "Profession") + (r.MinRank > 0 ? " " + r.MinRank : "") + (r.ReagentCount > 0 ? " · " + ReagentText(r) : ""), Sort = -r.MinRank,
                });
                AddLayer(m, "client");
            }

            // the table: Lodge-observed rows, then the client's, then the Classic baseline; tiny chances folded
            FoldTiny(src, rows, tiny);

            // ---- the map: where the sources are
            AddItemMarkers(m, id, src, L, plan, byKey);

            // ---- used as a reagent
            var use = SpellData.ReagentFor(id);
            if (use.Count > 0)
            {
                var s = Section(m, "reagent", "Used in recipes");
                s.Note = "Recipes of your game that use this item up.";
                foreach (var r in use.OrderBy(x => x.SkillId).ThenBy(x => x.MinRank).ThenBy(x => x.SpellId).Take(120))
                {
                    var sp = SpellData.Spell(r.SpellId);
                    var skill = SpellData.Skill(r.SkillId);
                    int per = 0;
                    for (int i = 0; i + 1 < r.Reagents.Length; i += 2) if (r.Reagents[i] == id) per += r.Reagents[i + 1];
                    var row = r.ItemId > 0 ? ItemRow(r.ItemId) : new AtlasRow { Link = AtlasIds.Spell(r.SpellId), Title = sp?.Name ?? "Recipe " + r.SpellId, IconId = sp?.IconId ?? 0 };
                    row.Label = AtlasLabel.Forever; row.Type = "Recipe"; row.Tier = 1;
                    row.Detail = (skill?.Name ?? "Profession") + (r.MinRank > 0 ? " " + r.MinRank : "") + " · uses " + per;
                    s.Rows.Add(row);
                }
                if (use.Count > 120) s.HiddenText = (use.Count - 120) + " more recipes not shown";
                AddLayer(m, "client");
            }

            // ---- quests that need it
            if (needs.Count > 0)
            {
                var s = Section(m, "needed", "Needed for quests");
                s.Rows.AddRange(needs.OrderBy(r => r.Title, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Key, StringComparer.Ordinal).Take(60));
                if (needs.Count > 60) s.HiddenText = (needs.Count - 60) + " more quests not shown";
            }

            // ---- found by the Lodge
            if (L != null)
            {
                var finds = L.Finds(id);
                if (finds.Count > 0)
                {
                    var s = Section(m, "finds", "Found by the Lodge");
                    var members = new HashSet<string>(finds.Select(f => f.By ?? "a member"));
                    s.Note = AtlasFormat.Count(finds.Count, "find", "finds") + " by " + AtlasFormat.Count(members.Count, "member", "members") + " (newest first). Names are Lodge display names.";
                    foreach (var f in finds.OrderByDescending(x => x.Seq).ThenByDescending(x => x.At).Take(15)) s.Rows.Add(FindRow(f));
                    if (finds.Count > 15) s.HiddenText = (finds.Count - 15) + " older finds not shown";
                    AddLayer(m, "lodge");
                }
            }

            m.Map = plan.Build();
            LayerNotes(m, !ClassicAtlas.IsReady, !LodgeHas(L), true);
            bool known = ci != null || cl != null || rev != null;
            if (!known && m.Sections.All(s => s.Key == "owned" || (s.Rows.Count == 0 && s.Hidden.Count == 0)))
            {
                m.NotFound = true;
                m.Message = ItemData.Ready ? "No layer knows item " + id + ". The game has not shown it to you yet." : "Item data is not ready, and no other layer knows item " + id + ".";
            }
            return m;
        }

        static string CountText(ClassicItemSource s)
            => s.MaxCount > 1 ? " · " + (s.MinCount == s.MaxCount ? s.MaxCount.ToString() : s.MinCount + "-" + s.MaxCount) + " per drop" : "";

        static string ReagentText(Recipe r)
        {
            var parts = new List<string>();
            for (int i = 0; i + 1 < r.Reagents.Length && parts.Count < 3; i += 2)
                if (r.Reagents[i] > 0) parts.Add(ItemName(r.Reagents[i]) + (r.Reagents[i + 1] > 1 ? " ×" + r.Reagents[i + 1] : ""));
            if (r.ReagentCount > 3) parts.Add("…");
            return string.Join(", ", parts);
        }

        static List<TipLine> RevealedLines(AtlasObs.Revealed r)
        {
            var l = new List<TipLine>();
            if (r.ItemLevel > 0) l.Add(new TipLine { Kind = "ilvl", Text = "Item Level " + r.ItemLevel });
            var slot = ItemNames.Slot(r.Slot);
            var sub = ItemNames.Subclass(r.Class, r.SubClass);
            if (slot != null) l.Add(new TipLine { Kind = "slot", Text = slot, Right = sub });
            else if (!string.IsNullOrEmpty(ItemNames.Class(r.Class))) l.Add(new TipLine { Kind = "slot", Text = ItemNames.Class(r.Class) });
            if (r.Stats != null)
                foreach (var st in r.Stats)
                    if (st != null && st.Length >= 2 && st[1] != 0)
                        l.Add(new TipLine { Kind = "stat", Text = (st[1] > 0 ? "+" : "-") + Math.Abs(st[1]).ToString(CultureInfo.InvariantCulture) + " " + ItemNames.Stat(st[0]) });
            if (r.RequiredLevel > 1) l.Add(new TipLine { Kind = "req", Text = "Requires Level " + r.RequiredLevel });
            return l;
        }

        // "Tess found it: 1× from Gnarlfang · Elwynn Forest · 3 h ago"
        static AtlasRow FindRow(AtlasObs.Find f)
        {
            string link = null, from = "";
            var src = f.Src ?? "";
            int c = src.IndexOf(':');
            string kind = c > 0 ? src.Substring(0, c) : src;
            int.TryParse(c > 0 ? src.Substring(c + 1) : "0", out var sid);
            switch (kind)
            {
                case "npc": link = "npc:" + sid; from = "from " + NpcName(sid); break;
                case "obj": link = "obj:" + sid; from = "from " + ObjectName(sid); break;
                case "vendor": link = "npc:" + sid; from = "bought from " + NpcName(sid); break;
                case "quest": link = "quest:" + sid; from = "quest reward: " + QuestTitle(sid); break;
                case "fish": from = "caught fishing"; if (f.Zone > 0) link = "zone:" + f.Zone; break;
                default: from = "found"; break;
            }
            var zone = f.Zone > 0 ? ZoneName(f.Zone) : "";
            return new AtlasRow
            {
                Key = "find:" + f.Seq, Link = link, Label = AtlasLabel.Forever, Tier = 0, Type = "Find", Title = string.IsNullOrEmpty(f.By) ? "a member" : f.By,
                Detail = f.Count + "× " + from + (zone.Length > 0 ? " · " + zone : ""), Right = AtlasFormat.Ago(f.At, Context.NowUtc()), Zone = zone, Sort = f.Seq,
            };
        }

        // markers for the item page: Lodge spawn points of the dropping npcs, Lodge finds and vendors, Classic spawns of the sources that are shown
        static void AddItemMarkers(AtlasPageModel m, int id, AtlasSection src, LodgeAtlas L, PlanBuilder plan, Dictionary<string, AtlasRow> byKey)
        {
            string F = AtlasLabel.Forever, C = AtlasLabel.Classic;
            var done = new HashSet<string>();
            if (L != null)
            {
                foreach (var r in src.Rows.Where(x => x.Tier == 0))
                {
                    if (r.Key.StartsWith("npc:", StringComparison.Ordinal) && int.TryParse(r.Key.Substring(4), out var nid))
                        foreach (var sp in L.Spawns(nid)) plan.Spot(sp.Zone, sp.X, sp.Y, MarkerKind.Spawn, r.Title, r.Detail + " · " + F, r.Link);
                    else if (r.Key.StartsWith("vendor:", StringComparison.Ordinal) && int.TryParse(r.Key.Substring(7), out var vid))
                    {
                        var v = L.Vendor(vid);
                        if (v != null) plan.Spot(v.Zone, v.X, v.Y, MarkerKind.Vendor, r.Title, "Sells it for " + r.Detail + " · " + F, r.Link);
                    }
                }
                foreach (var f in L.Finds(id))
                {
                    if (f.Zone <= 0) continue;
                    var row = FindRow(f);
                    plan.Spot(f.Zone, f.X, f.Y, MarkerKind.Find, (string.IsNullOrEmpty(f.By) ? "A member" : f.By) + " found it", row.Detail + " · " + F, row.Link);
                }
            }
            int quests = 0;
            foreach (var r in src.Rows)
            {
                if (plan.Full) break;
                var k = r.Key;
                if (k.StartsWith("npc:", StringComparison.Ordinal) && int.TryParse(k.Substring(4), out var nid) && done.Add(k))
                    plan.Classic(ClassicAtlas.Spawns(nid), MarkerKind.Spawn, r.Title, r.Detail + " · " + C, r.Link);
                else if (k.StartsWith("obj:", StringComparison.Ordinal) && int.TryParse(k.Substring(4), out var oid) && done.Add(k))
                    plan.Classic(ClassicAtlas.Current?.ObjectSpawns(oid), MarkerKind.Node, r.Title, r.Detail + " · " + C, r.Link);
                else if (k.StartsWith("vendor:", StringComparison.Ordinal) && int.TryParse(k.Substring(7), out var vid) && done.Add(k))
                    plan.Classic(ClassicAtlas.Spawns(vid), MarkerKind.Vendor, r.Title, "Sells it · " + C, r.Link);
                else if (k.StartsWith("quest:", StringComparison.Ordinal) && quests < 3 && int.TryParse(k.Substring(6), out var qid) && done.Add(k))
                {
                    quests++;
                    var q = ClassicAtlas.QuestInfo(qid);
                    if (q == null) continue;
                    foreach (var g in q.Givers.Take(3))
                        plan.Classic(g.IsObject ? ClassicAtlas.Current?.ObjectSpawns(g.Id) : ClassicAtlas.Spawns(g.Id), MarkerKind.Quest, r.Title, "Quest giver: " + (g.IsObject ? ObjectName(g.Id) : NpcName(g.Id)) + " · " + C, "quest:" + qid);
                }
            }
        }
    }
}
