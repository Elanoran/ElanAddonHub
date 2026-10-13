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
        static readonly string[] Families =
        {
            "", "Wolf", "Cat", "Spider", "Bear", "Boar", "Crocolisk", "Carrion Bird", "Crab", "Gorilla", "", "Raptor", "Tallstrider", "", "", "Felhunter", "Voidwalker", "Succubus", "", "Doomguard",
            "Scorpid", "Turtle", "", "Imp", "Bat", "Hyena", "Owl", "Wind Serpent",
        };
        static string FamilyName(int f) => f > 0 && f < Families.Length ? Families[f] : "";

        static string Services(uint flags)
        {
            var l = new List<string>();
            if ((flags & 0x2) != 0) l.Add("Quest giver");
            if ((flags & 0x80) != 0) l.Add("Vendor");
            if ((flags & 0x10) != 0) l.Add("Trainer");
            if ((flags & 0x1000) != 0) l.Add("Repairs");
            if ((flags & 0x2000) != 0) l.Add("Flight master");
            if ((flags & 0x10000) != 0) l.Add("Innkeeper");
            if ((flags & 0x20000) != 0) l.Add("Banker");
            if ((flags & 0x200000) != 0) l.Add("Auctioneer");
            if ((flags & 0x400000) != 0) l.Add("Stable master");
            return string.Join(", ", l);
        }

        // ---- loot of one source (an npc, an object): what the Lodge saw drop with the chance from kills, merged with the Classic baseline's table
        // Lodge rows come first; a Classic figure for the same item rides on the same row; Classic chances under TinyChance are folded.
        static void LootSection(AtlasPageModel m, string key, string title, string lodgeKind, int id, ClassicSourceKind ck, string empty)
        {
            var s = Section(m, key, title, empty);
            var L = Lodge();
            var rows = new List<AtlasRow>(); var tiny = new List<AtlasRow>(); var by = new Dictionary<int, AtlasRow>();
            var counter = lodgeKind != null ? L?.Loot(lodgeKind, id) : null;
            string what = lodgeKind == "obj" ? "open" : "kill";
            if (counter != null && counter.N > 0)
            {
                foreach (var kv in counter.Items)
                {
                    double chance = Math.Min(100, kv.Value[0] * 100.0 / counter.N);
                    var r = ItemRow(kv.Key, AtlasFormat.Percent(chance) + " · " + kv.Value[0] + " of " + AtlasFormat.Count(counter.N, what, what + "s") + (kv.Value[1] > kv.Value[0] ? " · " + kv.Value[1] + " items" : ""), AtlasLabel.Forever);
                    r.Type = "Drop"; r.Tier = 0; r.Sort = chance;
                    rows.Add(r); by[kv.Key] = r;
                }
                s.Note = AtlasFormat.Count(counter.N, "looted " + what, "looted " + what + "s") + " seen by " + AtlasFormat.Count(Math.Max(1, counter.Members), "member", "members") + " of the Lodge.";
                AddLayer(m, "lodge");
            }
            var dv = AtlasDerived.For(ClassicAtlas.Current);
            if (dv != null)
            {
                foreach (var e in dv.LootOf(ck, id))
                {
                    var src = e.Source;
                    var txt = AtlasFormat.Percent(src.Chance) + (src.QuestOnly ? " · only while on the quest" : "") + CountText(src);
                    if (by.TryGetValue(e.ItemId, out var seen)) { seen.Extra = (seen.Extra.Length > 0 ? seen.Extra + " · " : "") + AtlasLabel.Classic + ": " + txt; continue; }
                    var r = ItemRow(e.ItemId, txt, AtlasLabel.Classic);
                    r.Type = "Drop"; r.Tier = 2; r.Sort = src.Chance;
                    if (src.Chance < TinyChance) tiny.Add(r); else rows.Add(r);
                }
                if (s.Note.Length == 0) s.Note = "From the Classic baseline's loot table.";
                else s.Note += " Classic baseline figures ride along on the same row.";
                AddLayer(m, "classic");
            }
            FoldTiny(s, rows, tiny, 8);
            if (s.Hidden.Count > 0) s.HiddenText = s.HiddenText.Replace("world-drop sources", "drops").Replace("world-drop source", "drop");
        }

        static void VendorSection(AtlasPageModel m, int npcId)
        {
            var L = Lodge();
            var lv = L?.Vendor(npcId);
            var cv = ClassicAtlas.VendorItems(npcId);
            if (lv == null && cv.Count == 0) return;
            var s = Section(m, "vendor", "Sells");
            var by = new Dictionary<int, AtlasRow>();
            var rows = new List<AtlasRow>();
            if (lv != null)
            {
                foreach (var kv in lv.Items)
                {
                    var p = kv.Value;
                    var r = ItemRow(kv.Key, (p[0] > 0 ? AtlasFormat.Money(p[0]) : "free") + (p[1] > 1 ? " · stack of " + p[1] : "") + (p[2] >= 0 ? " · limit " + p[2] : ""), AtlasLabel.Forever);
                    r.Type = "Vendor"; r.Tier = 0; r.Sort = 0;
                    rows.Add(r); by[kv.Key] = r;
                }
                s.Note = "Prices were seen in a Lodge member's game " + AtlasFormat.Ago(lv.At, Context.NowUtc()) + ".";
                AddLayer(m, "lodge");
            }
            foreach (var v in cv)
            {
                var stock = v.MaxCount > 0 ? "limited stock (" + v.MaxCount + ")" : "unlimited stock";
                if (by.TryGetValue(v.ItemId, out var seen)) { seen.Extra = AtlasLabel.Classic + ": " + stock; continue; }
                var r = ItemRow(v.ItemId, "Sold here · " + stock, AtlasLabel.Classic);
                r.Type = "Vendor"; r.Tier = 2;
                rows.Add(r);
            }
            if (cv.Count > 0) { AddLayer(m, "classic"); if (s.Note.Length == 0) s.Note = "Prices are not in the Classic baseline."; }
            SortRows(rows);
            s.Rows.AddRange(rows);
        }

        static AtlasPageModel BuildNpc(int id)
        {
            var m = new AtlasPageModel { Id = "npc:" + id, Kind = "npc", MapFirst = true };
            var L = Lodge();
            var seen = SeenData.Npc(id);
            var cn = ClassicAtlas.NpcInfo(id);
            var loot = L?.Loot("npc", id);
            var spots = L?.Spawns(id);
            bool lodgeKnows = (loot != null && loot.N > 0) || (spots != null && spots.Count > 0) || L?.Vendor(id) != null;
            if (seen == null && cn == null && !lodgeKnows)
            {
                var nf = NotFoundPage("npc", id, SeenData.Ready || ClassicAtlas.IsReady ? "No layer knows this NPC. Your game has not met it yet." : "Nothing is read yet that could know this NPC (the game's creature cache and the Classic baseline are both missing).");
                LayerNotes(nf, true, false);
                return nf;
            }
            m.Title = seen != null && seen.Name.Length > 0 ? seen.Name : cn?.Name ?? "NPC " + id;
            string F = AtlasLabel.Forever, C = AtlasLabel.Classic;
            string title = seen != null && seen.Title.Length > 0 ? seen.Title : cn?.SubName ?? "";
            if (seen != null) AddLayer(m, "seen");
            if (cn != null) AddLayer(m, "classic");
            if (title.Length > 0) m.Facts.Add(new AtlasFact("Title", "<" + title + ">", seen != null && seen.Title.Length > 0 ? F : C));
            if (cn != null) { var lv = AtlasFormat.Levels(cn.MinLevel, cn.MaxLevel); if (lv.Length > 0) m.Facts.Add(new AtlasFact("Level", lv, C)); }
            int rank = seen != null ? seen.Rank : cn?.Rank ?? 0;
            m.Facts.Add(new AtlasFact("Rank", rank > 0 ? AtlasFormat.RankName(rank) : "Normal", seen != null ? F : C));
            int type = seen != null && seen.Type > 0 ? seen.Type : cn?.Type ?? 0;
            if (AtlasFormat.CreatureType(type).Length > 0) m.Facts.Add(new AtlasFact("Type", AtlasFormat.CreatureType(type), seen != null && seen.Type > 0 ? F : C));
            int family = seen != null && seen.Family > 0 ? seen.Family : cn?.Family ?? 0;
            if (FamilyName(family).Length > 0) m.Facts.Add(new AtlasFact("Family", FamilyName(family), seen != null && seen.Family > 0 ? F : C));
            if (cn != null && cn.FactionId > 0)
            {
                var tpl = FactionData.Template(cn.FactionId);
                var desc = FactionData.Describe(cn.FactionId);
                m.Facts.Add(new AtlasFact("Faction", desc ?? "Faction template " + cn.FactionId, C, tpl != null && FactionData.Name(tpl.FactionId) != null ? AtlasIds.Faction(tpl.FactionId) : null));
            }
            var svc = Services(seen != null ? seen.Flags0 : (uint)(cn?.NpcFlags ?? 0));
            if (svc.Length > 0) m.Facts.Add(new AtlasFact("Services", svc, seen != null ? F : C));
            m.Subtitle = string.Join(" · ", new[] { "NPC " + id, title.Length > 0 ? "<" + title + ">" : "", cn != null && AtlasFormat.Levels(cn.MinLevel, cn.MaxLevel).Length > 0 ? "Level " + AtlasFormat.Levels(cn.MinLevel, cn.MaxLevel) : "", rank > 0 ? AtlasFormat.RankName(rank) : "", AtlasFormat.CreatureType(type) }.Where(s => s.Length > 0));

            bool vendor = (seen != null && (seen.Flags0 & 0x80) != 0) || (cn != null && cn.IsVendor) || L?.Vendor(id) != null;
            // where it stands
            var plan = new PlanBuilder();
            string tip = (cn != null ? "Level " + AtlasFormat.Levels(cn.MinLevel, cn.MaxLevel) + " · " : "");
            if (spots != null) foreach (var sp in spots) plan.Spot(sp.Zone, sp.X, sp.Y, vendor ? MarkerKind.Vendor : MarkerKind.Spawn, m.Title, "Seen by the Lodge · " + F, "npc:" + id);
            if (cn != null) plan.Classic(cn.Spawns, vendor ? MarkerKind.Vendor : MarkerKind.Spawn, m.Title, tip + C, "npc:" + id);
            m.Map = plan.Build();
            AddFoundIn(m, cn?.Spawns, spots);

            LootSection(m, "loot", "Loot", "npc", id, ClassicSourceKind.NpcDrop, "No loot is known for this NPC.");
            var sk = ClassicAtlas.Current != null ? AtlasDerived.For(ClassicAtlas.Current).LootOf(ClassicSourceKind.NpcSkin, id) : null;
            if (sk != null && sk.Count > 0) AddSimpleLoot(m, "skin", "Skinning", sk);
            var pk = ClassicAtlas.Current != null ? AtlasDerived.For(ClassicAtlas.Current).LootOf(ClassicSourceKind.NpcPickpocket, id) : null;
            if (pk != null && pk.Count > 0) AddSimpleLoot(m, "pick", "Pickpocketing", pk);
            if (vendor) VendorSection(m, id);
            var dv = AtlasDerived.For(ClassicAtlas.Current);
            if (dv != null)
            {
                var qs = dv.QuestsGivenBy(false, id);
                if (qs.Count > 0)
                {
                    var s = Section(m, "quests", "Quests it gives");
                    s.Note = "From the Classic baseline.";
                    foreach (var q in qs.OrderBy(x => x.QuestLevel).ThenBy(x => x.Id).Take(60))
                        s.Rows.Add(new AtlasRow { Key = "quest:" + q.Id, Link = "quest:" + q.Id, Label = C, Tier = 2, Type = "Quest", Title = QuestTitle(q.Id), Detail = "Level " + q.QuestLevel + (q.MinLevel > 0 ? " · from level " + q.MinLevel : "") });
                    if (qs.Count > 60) s.HiddenText = (qs.Count - 60) + " more quests not shown";
                }
            }
            LayerNotes(m, !ClassicAtlas.IsReady, !LodgeHas(L));
            if (!SeenData.Ready) m.Notes.Add("What your game has seen (names, ranks) is not read yet.");
            return m;
        }

        static void AddSimpleLoot(AtlasPageModel m, string key, string title, IReadOnlyList<ClassicLootEntry> list)
        {
            var s = Section(m, key, title);
            s.Note = "From the Classic baseline.";
            var rows = new List<AtlasRow>(); var tiny = new List<AtlasRow>();
            foreach (var e in list)
            {
                var r = ItemRow(e.ItemId, AtlasFormat.Percent(e.Source.Chance) + CountText(e.Source), AtlasLabel.Classic);
                r.Type = "Drop"; r.Tier = 2; r.Sort = e.Source.Chance;
                if (e.Source.Chance < TinyChance) tiny.Add(r); else rows.Add(r);
            }
            FoldTiny(s, rows, tiny, 8);
            if (s.Hidden.Count > 0) s.HiddenText = s.HiddenText.Replace("world-drop sources", "drops").Replace("world-drop source", "drop");
        }

        // "Found in": the zones of a thing's spawn points, each a link
        static void AddFoundIn(AtlasPageModel m, IReadOnlyList<ClassicSpawn> spawns, IReadOnlyList<AtlasObs.Spot> spots)
        {
            var zones = new Dictionary<int, int>();
            foreach (var z in m.Map.Zones) zones[z.UiMapId] = z.Markers.Count;
            if (zones.Count == 0 && m.Map.InInstances.Count == 0) return;
            foreach (var z in zones.OrderByDescending(k => k.Value).ThenBy(k => k.Key).Take(4))
                m.Facts.Add(new AtlasFact(m.Facts.Any(f => f.Label == "Found in") ? "Also in" : "Found in", ZoneName(z.Key), m.Map.Zones.First(x => x.UiMapId == z.Key).Forever > 0 ? AtlasLabel.Forever : AtlasLabel.Classic, AtlasIds.TryParse("zone:" + z.Key, out _, out _) ? "zone:" + z.Key : null));
            foreach (var i in m.Map.InInstances.Take(2))
            {
                var inst = MapData.Index?.Instance(i.Key);
                if (inst != null) m.Facts.Add(new AtlasFact("Inside", inst.Name, AtlasLabel.Classic, "instance:" + i.Key));
            }
        }

        static AtlasPageModel BuildObject(int id)
        {
            var m = new AtlasPageModel { Id = "obj:" + id, Kind = "obj", MapFirst = true };
            var L = Lodge();
            var seen = SeenData.Object(id);
            var co = ClassicAtlas.ObjectInfo(id);
            var loot = L?.Loot("obj", id);
            if (seen == null && co == null && (loot == null || loot.N == 0))
            {
                var nf = NotFoundPage("obj", id, "No layer knows this object. Your game has not met it yet.");
                LayerNotes(nf, true, false);
                return nf;
            }
            string F = AtlasLabel.Forever, C = AtlasLabel.Classic;
            m.Title = seen != null && seen.Name.Length > 0 ? seen.Name : co?.Name ?? "Object " + id;
            if (seen != null) AddLayer(m, "seen");
            if (co != null) AddLayer(m, "classic");
            int type = seen != null ? seen.Type : co?.Type ?? 0;
            var kind = ObjectKind(type);
            if (kind.Length > 0) m.Facts.Add(new AtlasFact("Kind", kind, seen != null ? F : C));
            m.Subtitle = string.Join(" · ", new[] { "Object " + id, kind }.Where(s => s.Length > 0));
            var plan = new PlanBuilder();
            if (co != null) plan.Classic(co.Spawns, MarkerKind.Node, m.Title, kind + " · " + C, "obj:" + id);
            m.Map = plan.Build();
            AddFoundIn(m, co?.Spawns, null);
            LootSection(m, "loot", "Loot", "obj", id, ClassicSourceKind.ObjectLoot, "No loot is known for this object.");
            LayerNotes(m, !ClassicAtlas.IsReady, !LodgeHas(L));
            return m;
        }
    }
}
