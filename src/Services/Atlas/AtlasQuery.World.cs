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
        const int MaxZoneRows = 400;

        static AtlasPageModel BuildZone(int id)
        {
            var idx = MapData.Index;
            if (idx == null)
            {
                var nf = NotFoundPage("zone", id, MapData.Status == MapData.State.Disabled ? "Game data is switched off (Settings > General), so there are no maps." : "The maps are read from your game files and are not ready yet.");
                return nf;
            }
            var z = idx.Get(id);
            if (z == null) return NotFoundPage("zone", id, "Your game has no map with this id.");
            string F = AtlasLabel.Forever, C = AtlasLabel.Classic;
            var m = new AtlasPageModel { Id = "zone:" + id, Kind = "zone", Title = z.Name, MapFirst = true };
            AddLayer(m, "maps");
            foreach (var c in idx.Breadcrumb(id)) if (c.Id != id) m.Crumbs.Add(new AtlasCrumb(c.Name, "zone:" + c.Id));
            m.Facts.Add(new AtlasFact("Type", z.TypeName, F));
            if (m.Crumbs.Count > 0) m.Facts.Add(new AtlasFact("In", m.Crumbs[m.Crumbs.Count - 1].Title, F, m.Crumbs[m.Crumbs.Count - 1].Link));
            m.Map.ShowZone = id;
            m.Lore = ZoneLore.For(id);
            var subs = idx.Children(id);
            var L = Lodge();
            var plan = new PlanBuilder();
            bool isZone = z.Type == UiMapInfo.Zone || z.Type == UiMapInfo.Micro || z.Type == UiMapInfo.Orphan || z.Type == UiMapInfo.Dungeon;

            if (subs.Count > 0)
            {
                var s = Section(m, "children", z.Type == UiMapInfo.Continent ? "Zones" : "Places in it");
                foreach (var c in subs.Where(x => x.System == 0).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                    s.Rows.Add(new AtlasRow { Key = "zone:" + c.Id, Link = "zone:" + c.Id, Title = c.Name, Detail = c.TypeName, Label = F, Tier = 1, Type = "Zone" });
            }

            var dv = AtlasDerived.For(ClassicAtlas.Current);
            AtlasDerived.ZoneContent zc = null;
            if (dv != null && isZone) zc = dv.Zone(idx, id);
            if (zc != null && zc.MinLevel > 0) m.Facts.Add(new AtlasFact("Creature levels", AtlasFormat.Levels(zc.MinLevel, zc.MaxLevel), C));
            if (zc != null) AddLayer(m, "classic");

            if (zc != null && zc.Npcs.Count > 0)
            {
                // creatures: the notable ones first (bosses, rare elites, rares), then the rest by how many stand there
                var s = Section(m, "creatures", "Creatures");
                s.Note = AtlasFormat.Count(zc.Npcs.Count, "kind of creature", "kinds of creature") + " stand here, by the Classic baseline's spawn points.";
                var rowsN = zc.Npcs.Select(kv => new { n = ClassicAtlas.NpcInfo(kv.Key), c = kv.Value }).Where(x => x.n != null)
                    .OrderByDescending(x => x.n.Rank == 3 ? 3 : x.n.Rank == 2 ? 2 : x.n.Rank == 4 ? 2 : 0).ThenByDescending(x => x.c).ThenBy(x => x.n.Name, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.n.Id).Take(MaxZoneRows).ToList();
                foreach (var x in rowsN)
                {
                    var n = x.n;
                    s.Rows.Add(new AtlasRow
                    {
                        Key = "npc:" + n.Id, Link = "npc:" + n.Id, Title = n.Name, Label = SeenData.Npc(n.Id) != null ? F : C, Tier = 2, Type = "NPC",
                        Detail = string.Join(" · ", new[] { AtlasFormat.Levels(n.MinLevel, n.MaxLevel).Length > 0 ? "Level " + AtlasFormat.Levels(n.MinLevel, n.MaxLevel) : "", n.Rank > 0 ? AtlasFormat.RankName(n.Rank) : "", n.SubName ?? "", x.c + (x.c == 1 ? " spawn" : " spawns") }.Where(t => t.Length > 0)),
                    });
                    // map: bosses, rares and vendors only (a thousand wolves would hide the map)
                    if (n.Rank >= 2 || n.IsVendor) plan.Classic(n.Spawns.Where(sp => PlacedHere(idx, sp, id)).Take(3).ToList(), n.IsVendor && n.Rank < 2 ? MarkerKind.Vendor : MarkerKind.Spawn, n.Name, (n.IsVendor ? "Vendor · " : n.RankName + " · ") + "Level " + AtlasFormat.Levels(n.MinLevel, n.MaxLevel) + " · " + C, "npc:" + n.Id);
                }
                if (zc.Npcs.Count > rowsN.Count) s.HiddenText = (zc.Npcs.Count - rowsN.Count) + " more creatures not listed";
            }
            if (zc != null && zc.Objects.Count > 0)
            {
                var s = Section(m, "objects", "Objects");
                s.Note = "Chests, nodes and other things that stand here.";
                foreach (var kv in zc.Objects.OrderByDescending(k => k.Value).ThenBy(k => k.Key).Take(MaxZoneRows))
                {
                    var o = ClassicAtlas.ObjectInfo(kv.Key);
                    if (o == null) continue;
                    s.Rows.Add(new AtlasRow { Key = "obj:" + o.Id, Link = "obj:" + o.Id, Title = o.Name, Label = SeenData.Object(o.Id) != null ? F : C, Tier = 2, Type = "Object", Detail = (ObjectKind(o.Type) + " · " + kv.Value + (kv.Value == 1 ? " spawn" : " spawns")).TrimStart(' ', '·') });
                }
            }

            if (isZone) AddZoneNodes(plan, m, id);

            // the dungeons and raids whose way in is in this zone (the game's own entrance spots), also as door markers on its map
            var here = isZone ? AtlasInstances.InZone(idx, id) : new List<InstanceInfo>();
            if (here.Count > 0)
            {
                var s = Section(m, "instances", "Dungeons and raids");
                s.Note = "Their entrances are in " + z.Name + " (the spot where the game puts your ghost; for the newer instances where Lodge members or you entered from, or the game data's approximate spot).";
                foreach (var i in here)
                {
                    var en = AtlasInstances.Entrance(idx, i);
                    var lab = AtlasInstances.LabelOf(en);
                    s.Rows.Add(new AtlasRow { Key = "instance:" + i.MapId, Link = "instance:" + i.MapId, Title = i.Name, Detail = AtlasInstances.Summary(i), Extra = (en.Approximate ? "Near " : "Entrance ") + AtlasInstances.Pct(en.Fx) + " / " + AtlasInstances.Pct(en.Fy), Label = lab, Tier = en.Learned ? 1 : 0, Type = i.Kind, Sort = i.MinLevel });
                    plan.Spot(id, en.Fx, en.Fy, MarkerKind.Entrance, i.Name, AtlasInstances.Summary(i) + " · " + lab + (en.Approximate ? " (approximate)" : ""), "instance:" + i.MapId);
                }
            }

            // quests of the zone: the client's cache and the Classic baseline, the client's first
            var quests = new Dictionary<int, AtlasRow>();
            var areaMap = new Dictionary<int, int>();
            int MapOfArea(int area) { if (!areaMap.TryGetValue(area, out var u)) areaMap[area] = u = idx.UiMapForArea(area); return u; }
            if (SeenData.Ready && isZone)
                foreach (var q in SeenData.Snapshot.Quests.Values)
                    if (q.Sort > 0 && MapOfArea(q.Sort) == id)
                        quests[q.Id] = new AtlasRow { Key = "quest:" + q.Id, Link = "quest:" + q.Id, Title = q.Title, Label = F, Tier = 0, Type = "Quest", Detail = q.Level > 0 ? "Level " + q.Level : "", Sort = q.Level };
            if (ClassicAtlas.Current != null && isZone)
                foreach (var q in ClassicAtlas.Current.Quests.Values)
                    if (q.ZoneOrSort > 0 && !quests.ContainsKey(q.Id) && MapOfArea(q.ZoneOrSort) == id)
                        quests[q.Id] = new AtlasRow { Key = "quest:" + q.Id, Link = "quest:" + q.Id, Title = q.Title, Label = C, Tier = 2, Type = "Quest", Detail = "Level " + q.QuestLevel + (q.MinLevel > 0 ? " · from level " + q.MinLevel : ""), Sort = q.QuestLevel };
            if (quests.Count > 0)
            {
                var s = Section(m, "quests", "Quests");
                s.Note = AtlasFormat.Count(quests.Count, "quest", "quests") + " sorted under this zone.";
                s.Rows.AddRange(quests.Values.OrderBy(r => r.Tier).ThenBy(r => r.Sort).ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Key, StringComparer.Ordinal).Take(MaxZoneRows));
                if (quests.Count > MaxZoneRows) s.HiddenText = (quests.Count - MaxZoneRows) + " more quests not listed";
            }

            // notable drops: what the Lodge found here, then the best of the creatures' Classic loot
            var notable = new List<AtlasRow>();
            if (L != null)
                foreach (var f in L.AllFinds.Where(f => f.Zone == id).OrderByDescending(f => f.Seq))
                {
                    if (ItemQuality(f.Item) < 3 || notable.Any(r => r.Key == "item:" + f.Item)) continue;
                    var r = ItemRow(f.Item, "Found by " + (string.IsNullOrEmpty(f.By) ? "a member" : f.By) + " " + AtlasFormat.Ago(f.At, Context.NowUtc()), F);
                    r.Type = "Find"; r.Tier = 0; r.Sort = 1000 + ItemQuality(f.Item);
                    notable.Add(r);
                    plan.Spot(f.Zone, f.X, f.Y, MarkerKind.Find, ItemName(f.Item), "Found by " + (string.IsNullOrEmpty(f.By) ? "a member" : f.By) + " · " + F, "item:" + f.Item);
                }
            if (zc != null && dv != null)
            {
                var best = new Dictionary<int, AtlasRow>();
                foreach (var nid in zc.Npcs.Keys)
                    foreach (var e in dv.LootOf(ClassicSourceKind.NpcDrop, nid))
                    {
                        if (e.Source.Chance < 0.5 || e.Source.QuestOnly) continue;
                        var info = ClassicAtlas.ItemInfo(e.ItemId);
                        if (info == null || info.Quality < 3) continue;
                        if (best.TryGetValue(e.ItemId, out var have) && have.Sort >= e.Source.Chance) continue;
                        var r = ItemRow(e.ItemId, "from " + NpcName(nid) + " · " + AtlasFormat.Percent(e.Source.Chance), C);
                        r.Type = "Drop"; r.Tier = 2; r.Sort = info.Quality * 1000 + e.Source.Chance; r.Extra = "";
                        best[e.ItemId] = r;
                    }
                notable.AddRange(best.Values.Where(r => !notable.Any(n => n.Key == r.Key)));
            }
            if (notable.Count > 0)
            {
                var s = Section(m, "drops", "Notable drops");
                s.Note = "Rare and better items from creatures here, and what the Lodge found.";
                s.Rows.AddRange(notable.OrderBy(r => r.Tier).ThenByDescending(r => r.Sort).ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase).Take(24));
            }

            { var isec = m.Section("instances"); if (isec != null) { m.Sections.Remove(isec); m.Sections.Insert(m.Sections.FindIndex(x => x.Key == "children") + 1, isec); } }     // right after the places in it
            // the markers belong on this zone's own map
            var built = plan.Build();
            built.Zones = built.Zones.Where(zn => zn.UiMapId == id).ToList();
            built.ShowZone = id;
            m.Map = built;
            m.Subtitle = z.TypeName + (m.Crumbs.Count > 0 ? " in " + m.Crumbs[m.Crumbs.Count - 1].Title : "") + (zc != null && zc.MinLevel > 0 ? " · creatures level " + AtlasFormat.Levels(zc.MinLevel, zc.MaxLevel) : "");
            if (!idx.HasArt(id)) m.Notes.Add(z.Name + " has no picture in the game files.");
            if (ClassicAtlas.IsReady == false && isZone) m.Notes.Add("Creatures, objects and quests of a zone come from the Classic baseline (and your game's cache). Get it from the Atlas start page.");
            return m;
        }

        static bool PlacedHere(MapIndex idx, ClassicSpawn sp, int uiMap)
        {
            var p = idx.WorldToMap(sp.Map, sp.X, sp.Y);
            return p != null && p.Value.UiMapId == uiMap;
        }

        static AtlasPageModel BuildInstance(int mapId)
        {
            var idx = MapData.Index;
            if (idx == null) return NotFoundPage("instance", mapId, MapData.Status == MapData.State.Disabled ? "Game data is switched off (Settings > General), so there is no list of instances." : "The instance list is read from your game files and is not ready yet.");
            var inst = idx.Instance(mapId);
            if (inst == null || inst.Placeholder) return NotFoundPage("instance", mapId, "Your game has no instance with this id.");
            string F = AtlasLabel.Forever, C = AtlasLabel.Classic;
            var m = new AtlasPageModel { Id = "instance:" + mapId, Kind = "instance", Title = inst.Name, MapFirst = true, IconId = InstanceIcons.For(inst.MapId, inst.Name, inst.InstanceType) };
            AddLayer(m, "maps");
            m.Facts.Add(new AtlasFact("Type", inst.Kind, F));
            if (inst.MaxPlayers > 0) m.Facts.Add(new AtlasFact("Players", inst.MaxPlayers.ToString(CultureInfo.InvariantCulture), F));
            // the level the game suggests, the wings, and the way in: Map.db2 says where a ghost of the instance is put, which is its door
            if (inst.MinLevel > 0) m.Facts.Add(new AtlasFact("Suggested level", AtlasFormat.Levels(inst.MinLevel, inst.MaxLevel), F));
            if (inst.Parts.Count > 1) m.Facts.Add(new AtlasFact("Wings", string.Join(", ", inst.Parts), F));
            m.Entrance = AtlasInstances.EntranceFor(idx, inst);
            if (m.Entrance != null)
                m.Facts.Add(new AtlasFact("Entrance", m.Entrance.UiMapId > 0 ? (m.Entrance.Approximate ? "near " : "") + m.Entrance.ZoneName + " · " + m.Entrance.Coords : "continent " + m.Entrance.MapId + ", x " + Math.Round(m.Entrance.X) + ", y " + Math.Round(m.Entrance.Y), m.Entrance.Label, m.Entrance.UiMapId > 0 ? "zone:" + m.Entrance.UiMapId : null));
            m.Map.InstanceId = mapId; m.Map.InstanceName = inst.Name;
            var L = Lodge();
            var dv = AtlasDerived.For(ClassicAtlas.Current);
            m.Subtitle = InstanceDetail(inst) + (AtlasInstances.LevelText(inst).Length > 0 ? " · level " + AtlasInstances.LevelText(inst) : "") + (m.Entrance != null && m.Entrance.UiMapId > 0 ? " · entrance in " + m.Entrance.ZoneName : "") + (inst.HasMap ? "" : " · no map in the game files");
            if (dv != null)
            {
                AddLayer(m, "classic");
                var npcs = dv.NpcsOnMap(mapId).Select(ClassicAtlas.NpcInfo).Where(n => n != null).ToList();
                var wild = npcs.Where(n => n.NpcFlags == 0 && n.Type != 8 && n.Type != 11 && n.MaxLevel > 0).ToList();
                if (wild.Count >= 2)
                {
                    var ls = wild.Select(n => (n.MinLevel + n.MaxLevel) / 2.0).OrderBy(x => x).ToList();
                    m.Facts.Add(new AtlasFact("Creature levels", AtlasFormat.Levels((int)Math.Round(ls[(int)(ls.Count * 0.05)]), (int)Math.Round(ls[Math.Min(ls.Count - 1, (int)(ls.Count * 0.95))])), C));
                }
                var bosses = npcs.Where(n => n.Rank == 3).OrderByDescending(n => n.MaxLevel).ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ThenBy(n => n.Id).ToList();
                if (bosses.Count == 0) bosses = npcs.Where(n => n.Rank == 1 && n.MaxLevel > 0).OrderByDescending(n => n.MaxLevel).ThenBy(n => n.Name).Take(10).ToList();
                var plan = new PlanBuilder();
                var sb = Section(m, "bosses", bosses.Any(n => n.Rank == 3) || bosses.Count == 0 ? "Bosses" : "Strongest creatures", "No boss is known for this instance.");
                sb.Note = "By the Classic baseline's creature ranks.";
                foreach (var b in bosses)
                {
                    var kills = L?.Loot("npc", b.Id);
                    sb.Rows.Add(new AtlasRow
                    {
                        Key = "npc:" + b.Id, Link = "npc:" + b.Id, Title = b.Name, Label = C, Tier = 2, Type = "Boss",
                        Detail = "Level " + AtlasFormat.Levels(b.MinLevel, b.MaxLevel) + (b.SubName != null && b.SubName.Length > 0 ? " · " + b.SubName : "") + (kills != null && kills.N > 0 ? " · " + AtlasFormat.Count(kills.N, "kill", "kills") + " seen by the Lodge" : ""),
                    });
                    plan.Classic(b.Spawns.Where(s => s.Map == mapId).Take(2).ToList(), MarkerKind.Spawn, b.Name, "Boss · " + C, "npc:" + b.Id);
                }
                // what the bosses drop: the best of each table (quality first, then chance), the Lodge's observed chance where it has one
                var drops = new Dictionary<int, AtlasRow>();
                foreach (var b in bosses.Take(15))
                {
                    var lc = L?.Loot("npc", b.Id);
                    foreach (var e in dv.LootOf(ClassicSourceKind.NpcDrop, b.Id).Where(e => e.Source.Chance >= 1 && !e.Source.QuestOnly).Take(12))
                    {
                        var info = ClassicAtlas.ItemInfo(e.ItemId);
                        int q = info?.Quality ?? ItemQuality(e.ItemId);
                        if (q < 2 && drops.Count >= 8) continue;
                        double chance = e.Source.Chance; string label = C; int tier = 2; string extra = "";
                        if (lc != null && lc.N > 0 && lc.Items.TryGetValue(e.ItemId, out var li)) { chance = li[0] * 100.0 / lc.N; label = F; tier = 0; extra = C + ": " + AtlasFormat.Percent(e.Source.Chance); }
                        if (drops.TryGetValue(e.ItemId, out var have) && have.Sort >= q * 1000 + chance) continue;
                        var r = ItemRow(e.ItemId, "from " + b.Name + " · " + AtlasFormat.Percent(chance), label);
                        r.Type = "Drop"; r.Tier = tier; r.Sort = q * 1000 + chance; r.Extra = extra;
                        drops[e.ItemId] = r;
                    }
                }
                if (drops.Count > 0)
                {
                    var sd = Section(m, "drops", "Notable loot");
                    sd.Note = "The better items from the bosses' loot tables.";
                    sd.Rows.AddRange(drops.Values.OrderByDescending(r => r.Sort).ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase).Take(30));
                }
                var plain = npcs.Where(n => n.Rank != 3 && n.NpcFlags == 0).OrderByDescending(n => n.Spawns.Count(s => s.Map == mapId)).ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase).Take(60).ToList();
                if (plain.Count > 0)
                {
                    var sc = Section(m, "creatures", "Other creatures");
                    foreach (var n in plain)
                        sc.Rows.Add(new AtlasRow { Key = "npc:" + n.Id, Link = "npc:" + n.Id, Title = n.Name, Label = C, Tier = 2, Type = "NPC", Detail = "Level " + AtlasFormat.Levels(n.MinLevel, n.MaxLevel) + (n.Rank > 0 ? " · " + n.RankName : "") + " · " + n.Spawns.Count(s => s.Map == mapId) + " spawns" });
                }
                var built = plan.Build();
                built.InstanceId = mapId; built.InstanceName = inst.Name;
                m.Map = built;
            }
            else LayerNotes(m, true, false);
            if (!inst.HasMap) m.Notes.Add(inst.Name + " has no map in the game files" + (ElansAddonHub.Services.DungeonMaps.DungeonPlans.CanDraw(mapId) ? "; the map card draws a plan of it from them." : "."));
            return m;
        }
    }
}
