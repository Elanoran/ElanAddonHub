using System;
using System.Collections.Generic;
using System.Linq;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services.Atlas.Classic;

namespace ElansAddonHub.Services.Atlas
{
    // 3.10.4 (docs: one layout for every page, milestone 2): the quick answers of a creature page (Where · Level and rank · Top drop · Your Lodge) and
    // an object page (Where · What's inside · Your Lodge). Same rules as an item's (AtlasQuery.Item.cs ItemAnswers): one value, one detail line and
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
    }
}
