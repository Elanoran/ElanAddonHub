using System;
using System.Collections.Generic;
using System.Linq;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services.Atlas.Classic;
using ElansAddonHub.Services.Db2;

namespace ElansAddonHub.Services.Atlas
{
    // 3.10.4 (docs: one layout for every page, milestones 2-3): the quick answers of a creature page (Where · Level and rank · Top drop · Your Lodge),
    // an object page (Where · What's inside · Your Lodge), a quest page (Given by · Level · Rewards) and a zone page (Levels · Dungeons · Your Lodge). Same rules as an item's (AtlasQuery.Item.cs ItemAnswers): one value, one detail line and
    // where it comes from (a DataSources key); "Not known yet" is an answer too, so the page needs no empty cards.
    public static partial class AtlasQuery
    {
        // where it stands: the zone with the most places (the Lodge's first), or the dungeon it is inside
        static AtlasAnswer WhereAnswer(AtlasPageModel m, string one, string many)
        {
            var a = new AtlasAnswer { Label = "Where" };
            var z = m.Map.Best;
            if (z != null)
            {
                a.Value = z.Name; a.Link = "zone:" + z.UiMapId;
                a.Detail = AtlasFormat.Count(z.Markers.Count, one, many) + (m.Map.Zones.Count > 1 ? " · " + (m.Map.Zones.Count - 1) + " more zone" + (m.Map.Zones.Count == 2 ? "" : "s") : "");
                a.Source = z.Forever > 0 ? DataSources.Lodge : DataSources.Classic;
                return a;
            }
            foreach (var i in m.Map.InInstances)
            {
                var inst = Maps.MapData.Index?.Instance(i.Key);
                if (inst == null) continue;
                a.Value = inst.Name; a.Link = "instance:" + i.Key; a.Detail = "inside · " + AtlasFormat.Count(i.Value, one, many);
                a.Source = DataSources.Classic;
                return a;
            }
            a.Value = "Not known yet"; a.Detail = "no " + one + " known";
            return a;
        }

        // the drop worth naming: the first uncommon-or-better item of the list (the list is the Lodge's rows first, then by chance), else its first row
        internal static AtlasRow TopDropOf(AtlasSection loot)
        {
            if (loot == null || loot.Rows.Count == 0) return null;
            return loot.Rows.FirstOrDefault(r => r.Quality >= 2) ?? loot.Rows[0];
        }

        static AtlasAnswer DropAnswer(AtlasSection loot, string label, string none)
        {
            var a = new AtlasAnswer { Label = label };
            var top = TopDropOf(loot);
            if (top != null)
            {
                int total = loot.Rows.Count + loot.Hidden.Count;
                a.Value = top.Title; a.Link = top.Link; a.Source = top.SourceKey;
                a.Detail = top.Detail.Split(new[] { " · " }, StringSplitOptions.None)[0] + (total > 1 ? " · " + AtlasFormat.Count(total, "item", "items") + " known" : "");
            }
            else { a.Value = "Not known yet"; a.Detail = none; }
            return a;
        }

        // what the Lodge has: its looted kills / opens, else the places members saw it at
        static AtlasAnswer LodgeLootAnswer(LodgeAtlas L, AtlasObs.Counter loot, string what, int seenPlaces, string notYet)
        {
            var a = new AtlasAnswer { Label = "Your Lodge" };
            long n = loot != null ? AtlasObs.UniqueKills(loot) : 0;
            if (n > 0)
            {
                a.Value = AtlasFormat.Count(n, what, what + "s");
                a.Detail = "by " + AtlasFormat.Count(Math.Max(1, loot.Members), "member", "members") + " · " + AtlasFormat.Count(loot.Items.Count, "item", "items") + " seen";
                a.Source = DataSources.Lodge;
            }
            else if (seenPlaces > 0) { a.Value = "Seen at " + AtlasFormat.Count(seenPlaces, "place", "places"); a.Detail = "no loot shared yet"; a.Source = DataSources.Lodge; }
            else if (L == null) { a.Value = "No Lodge data"; a.Detail = "a Lodge shares what its members find"; }
            else { a.Value = "Not seen yet"; a.Detail = notYet; }
            return a;
        }

        static void NpcAnswers(AtlasPageModel m, LodgeAtlas L, int id, ClassicNpc cn, int rank, bool rankSeen, AtlasObs.Counter loot, int seenPlaces)
        {
            m.Answers.Add(WhereAnswer(m, "spawn point", "spawn points"));

            var lv = new AtlasAnswer { Label = "Level and rank" };
            var lx = loot?.X;
            string levels = lx != null && lx.Lo > 0 ? AtlasFormat.Levels(lx.Lo, lx.Hi) : cn != null ? AtlasFormat.Levels(cn.MinLevel, cn.MaxLevel) : "";
            string rankWord = rank > 0 ? AtlasFormat.RankName(rank) : "Normal";
            if (levels.Length > 0)
            {
                lv.Value = "Level " + levels; lv.Detail = rankWord + (lx != null && lx.Lo > 0 ? " · as members saw it" : "");
                lv.Source = lx != null && lx.Lo > 0 ? DataSources.Lodge : DataSources.Classic;
            }
            else if (rankSeen) { lv.Value = rankWord; lv.Detail = "level not known yet"; lv.Source = DataSources.Game; }
            else { lv.Value = "Not known yet"; lv.Detail = "no level or rank known"; }
            m.Answers.Add(lv);

            m.Answers.Add(DropAnswer(m.Section("loot"), "Top drop", "no loot known"));
            m.Answers.Add(LodgeLootAnswer(L, loot, "kill", seenPlaces, "no member has looted it"));
        }

        static void ObjectAnswers(AtlasPageModel m, LodgeAtlas L, AtlasObs.Counter loot)
        {
            m.Answers.Add(WhereAnswer(m, "place", "places"));
            m.Answers.Add(DropAnswer(m.Section("loot"), "What's inside", "no loot known"));
            m.Answers.Add(LodgeLootAnswer(L, loot, "open", 0, "no member has opened it"));
        }
        // ---- a quest: who gives it (the first giver, or the item that starts it), its level, and what it gives
        static void QuestAnswers(AtlasPageModel m, int level, int min, string levelSource, int startItem, long money)
        {
            var giver = new AtlasAnswer { Label = "Given by" };
            var gs = m.Section("givers");
            var g = gs?.Rows.FirstOrDefault();
            if (g != null)
            {
                int more = gs.Rows.Count - 1;
                giver.Value = g.Title; giver.Link = g.Link; giver.Source = g.SourceKey;
                giver.Detail = (g.Zone.Length > 0 ? g.Zone : g.Type == "Object" ? "an object" : "a creature") + (more > 0 ? " · " + more + " more" : "");
            }
            else if (startItem > 0) { giver.Value = ItemName(startItem); giver.Link = AtlasIds.Item(startItem); giver.Detail = "an item starts it"; giver.Source = DataSources.Game; }
            else { giver.Value = "Not known yet"; giver.Detail = "no giver known"; }
            m.Answers.Add(giver);

            var lv = new AtlasAnswer { Label = "Level" };
            if (level > 0)
            {
                lv.Value = "Level " + level; lv.Source = levelSource;
                var kind = m.Facts.FirstOrDefault(f => f.Label == "Kind")?.Value ?? "";
                lv.Detail = string.Join(" · ", new[] { min > 0 ? "from level " + min : "", kind }.Where(x => x.Length > 0));
            }
            else { lv.Value = "Not known yet"; lv.Detail = "no level known"; }
            m.Answers.Add(lv);

            var rw = new AtlasAnswer { Label = "Rewards" };
            var rewards = m.Section("rewards")?.Rows ?? new List<AtlasRow>();
            var choices = m.Section("choices")?.Rows ?? new List<AtlasRow>();
            var extra = new List<string>();
            if (money > 0) extra.Add(AtlasFormat.Money(money));
            int reps = m.Facts.Count(f => f.Label == "Reputation");
            if (reps > 0) extra.Add(AtlasFormat.Count(reps, "reputation", "reputations"));
            if (choices.Count > 0)
            {
                rw.Value = "Choose one of " + choices.Count; rw.Source = choices[0].SourceKey;
                rw.Detail = string.Join(" · ", new[] { rewards.Count > 0 ? "+ " + AtlasFormat.Count(rewards.Count, "item", "items") : "" }.Concat(extra).Where(x => x.Length > 0));
                if (rw.Detail.Length == 0) rw.Detail = string.Join(", ", choices.Take(2).Select(r => r.Title)) + (choices.Count > 2 ? " ..." : "");
            }
            else if (rewards.Count > 0)
            {
                rw.Value = rewards[0].Title; rw.Link = rewards[0].Link; rw.Source = rewards[0].SourceKey;
                rw.Detail = string.Join(" · ", new[] { rewards.Count > 1 ? "+ " + AtlasFormat.Count(rewards.Count - 1, "more item", "more items") : "" }.Concat(extra).Where(x => x.Length > 0));
            }
            else if (extra.Count > 0) { rw.Value = extra[0]; rw.Detail = string.Join(" · ", extra.Skip(1)); rw.Source = levelSource; }
            else { rw.Value = "Not known yet"; rw.Detail = "no item reward known"; }
            m.Answers.Add(rw);
        }

        // ---- a zone: its creature levels, the dungeons whose entrance is here, and what the Lodge found here
        static void ZoneAnswers(AtlasPageModel m, LodgeAtlas L, int id, AtlasDerived.ZoneContent zc)
        {
            var lv = new AtlasAnswer { Label = "Levels" };
            if (zc != null && zc.MinLevel > 0)
            {
                lv.Value = "Level " + AtlasFormat.Levels(zc.MinLevel, zc.MaxLevel); lv.Source = DataSources.Classic;
                lv.Detail = "creatures · " + AtlasFormat.Count(zc.Npcs.Count, "kind", "kinds");
            }
            else if (m.Lore != null && m.Lore.FactsLine.Length > 0)
            {
                // no Classic baseline here: the Outpost's zone notes say it ("Levels 1-10 · Horde", "City · Alliance")
                var parts = m.Lore.FactsLine.Split(new[] { " · " }, StringSplitOptions.None);
                lv.Value = parts[0]; lv.Detail = string.Join(" · ", parts.Skip(1)); lv.Source = AtlasAnswer.Notes;
            }
            else { lv.Value = "Not known yet"; lv.Detail = "no creature levels known"; }
            m.Answers.Add(lv);

            var dn = new AtlasAnswer { Label = "Dungeons" };
            var inst = m.Section("instances")?.Rows ?? new List<AtlasRow>();
            if (inst.Count == 1) { dn.Value = inst[0].Title; dn.Link = inst[0].Link; dn.Detail = inst[0].Detail; dn.Source = DataSources.Game; }
            else if (inst.Count > 1)
            {
                dn.Value = AtlasFormat.Count(inst.Count, "entrance", "entrances"); dn.Source = DataSources.Game;
                dn.Detail = string.Join(", ", inst.Take(2).Select(r => r.Title)) + (inst.Count > 2 ? " and " + (inst.Count - 2) + " more" : "");
            }
            else { dn.Value = "None here"; dn.Detail = "no dungeon or raid entrance"; dn.Source = DataSources.Game; }
            m.Answers.Add(dn);

            var lo = new AtlasAnswer { Label = "Your Lodge" };
            var finds = L?.AllFinds.Where(f => f.Zone == id).ToList() ?? new List<AtlasObs.Find>();
            if (finds.Count > 0)
            {
                lo.Value = AtlasFormat.Count(finds.Count, "find", "finds"); lo.Source = DataSources.Lodge;
                lo.Detail = "by " + AtlasFormat.Count(finds.Select(f => f.By ?? "").Distinct().Count(), "member", "members") + " · last " + AtlasFormat.Ago(finds.Max(f => f.At), Context.NowUtc());
            }
            else if (L == null) { lo.Value = "No Lodge data"; lo.Detail = "a Lodge shares what its members find"; }
            else { lo.Value = "Nothing found yet"; lo.Detail = "no member has looted here"; }
            m.Answers.Add(lo);
        }
    }
}
