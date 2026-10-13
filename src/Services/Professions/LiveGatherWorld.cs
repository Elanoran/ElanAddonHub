using System;
using System.Collections.Generic;
using System.Linq;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services.Atlas.Classic;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Maps;

namespace ElansAddonHub.Services.Professions
{
    // The real gathering world: Classic baseline spawns (nodes, skinnable creatures, fishing holes) put on the zones of the player's own map data,
    // zone levels from the creatures spawned in them, and the Lodge's observations. Prepare() does the heavy part (all spawns once, a second or two) and
    // must not run on the UI thread. Without the Classic baseline or the map data it says what is missing.
    public sealed class LiveGatherWorld : IGatherWorld
    {
        readonly Func<LodgeAtlas> lodge;
        readonly Func<int, ClientItem> item;
        readonly object gate = new object();
        bool prepared;
        string missing;
        readonly Dictionary<int, ZoneInfo> zones = new Dictionary<int, ZoneInfo>();
        readonly Dictionary<string, List<GatherEntity>> byProfession = new Dictionary<string, List<GatherEntity>>(StringComparer.OrdinalIgnoreCase);

        public LiveGatherWorld(Func<LodgeAtlas> lodge = null, Func<int, ClientItem> item = null) { this.lodge = lodge; this.item = item ?? ItemData.Get; }

        public bool Available { get { Prepare(); return missing == null; } }
        public string Missing { get { Prepare(); return missing; } }
        public IEnumerable<GatherEntity> Entities(string profession) { Prepare(); return byProfession.TryGetValue(profession, out var l) ? l : new List<GatherEntity>(); }
        public ZoneInfo Zone(int uiMapId) { Prepare(); return zones.TryGetValue(uiMapId, out var z) ? z : null; }

        static int ZoneOf(MapIndex map, int mapId, float x, float y)
        {
            var p = map.WorldToMap(mapId, x, y);
            return p == null ? 0 : p.Value.UiMapId;
        }

        void Prepare()
        {
            lock (gate)
            {
                if (prepared) return;
                prepared = true;
                var idx = ClassicAtlas.Current;
                var map = MapData.Index;
                if (idx == null) { missing = "The Classic baseline is not downloaded yet (Settings > Game data). It holds the places of herbs, veins and creatures this guide is made from."; return; }
                if (map == null || !MapData.Ready) { missing = "The map data is not read yet (it comes from your WoW installation). It puts the Classic baseline's spawns on zones."; return; }
                try { Compute(idx, map); }
                catch (Exception e) { missing = "The place data could not be worked out: " + e.Message; Util.Log("professions: gather world: " + e); }
            }
        }

        void Compute(ClassicIndex idx, MapIndex map)
        {
            // zone levels: the levels of the ordinary hostile creatures spawned in each zone
            var levels = new Dictionary<int, List<int>>();
            var zoneOfNpc = new Dictionary<int, Dictionary<int, int>>();
            foreach (var n in idx.Npcs.Values)
            {
                if (n.Spawns == null || n.Spawns.Length == 0) continue;
                bool ordinary = n.Type != 8 && n.Type != 10 && n.Type != 11 && !n.IsVendor && n.Rank <= 1 && n.MaxLevel >= 1 && n.MaxLevel <= 62;
                var perZone = new Dictionary<int, int>();
                foreach (var sp in n.Spawns)
                {
                    int z = ZoneOf(map, sp.Map, sp.X, sp.Y);
                    if (z == 0) continue;
                    perZone[z] = (perZone.TryGetValue(z, out var c) ? c : 0) + 1;
                    if (ordinary) { if (!levels.TryGetValue(z, out var l)) levels[z] = l = new List<int>(); l.Add((n.MinLevel + n.MaxLevel + 1) / 2); }
                }
                zoneOfNpc[n.Id] = perZone;
            }
            foreach (var kv in levels)
            {
                kv.Value.Sort();
                var ui = map.Get(kv.Key);
                zones[kv.Key] = new ZoneInfo
                {
                    Id = kv.Key, Name = ui?.Name ?? "zone " + kv.Key, Median = kv.Value[kv.Value.Count / 2],
                    MinLevel = kv.Value[Math.Min(kv.Value.Count - 1, kv.Value.Count / 10)], MaxLevel = kv.Value[Math.Min(kv.Value.Count - 1, kv.Value.Count * 9 / 10)],
                };
            }

            // what each object / creature gives, in one pass over the sources
            var objItems = new Dictionary<int, List<int>>(); var skinItems = new Dictionary<int, List<int>>();
            foreach (var kv in idx.ItemSources)
                foreach (var s in kv.Value)
                {
                    if (s.Kind == ClassicSourceKind.ObjectLoot) { if (!objItems.TryGetValue(s.SourceId, out var l)) objItems[s.SourceId] = l = new List<int>(); l.Add(kv.Key); }
                    else if (s.Kind == ClassicSourceKind.NpcSkin) { if (!skinItems.TryGetValue(s.SourceId, out var l)) skinItems[s.SourceId] = l = new List<int>(); l.Add(kv.Key); }
                }

            var lod = lodge == null ? null : SafeLodge();
            GatherEntity ObjEntity(ClassicObject o, List<int> items)
            {
                var e = new GatherEntity { Kind = "obj", Id = o.Id, Name = o.Name ?? "object " + o.Id, Items = items };
                foreach (var sp in o.Spawns) { int z = ZoneOf(map, sp.Map, sp.X, sp.Y); if (z != 0) e.ClassicZones[z] = (e.ClassicZones.TryGetValue(z, out var c) ? c : 0) + 1; }
                if (lod != null)
                {
                    var c = lod.Loot("obj", o.Id);
                    if (c != null) foreach (var z in c.Zones) e.LodgeZones[z.Key] = (int)Math.Min(int.MaxValue, z.Value);
                }
                return e;
            }

            // herbs and veins: nodes (type 3) whose loot is a herb / ore of the client's item table; fishing holes: type 25
            var herb = new List<GatherEntity>(); var ore = new List<GatherEntity>(); var fish = new List<GatherEntity>();
            foreach (var o in idx.Objects.Values)
            {
                if (!objItems.TryGetValue(o.Id, out var items) || items.Count == 0) continue;
                if (o.Type == 25) { fish.Add(ObjEntity(o, items.Take(8).ToList())); continue; }
                if (o.Type != 3) continue;
                int herbs = items.Count(i => Sub(i) == 9), ores = items.Count(i => Sub(i) == 7);
                if (herbs > 0 && herbs >= ores) herb.Add(ObjEntity(o, items.Where(i => Sub(i) == 9).ToList()));
                else if (ores > 0) ore.Add(ObjEntity(o, items.Where(i => Sub(i) == 7).ToList()));
            }
            byProfession["Herbalism"] = herb.Where(e => e.ClassicZones.Count + e.LodgeZones.Count > 0).ToList();
            byProfession["Mining"] = ore.Where(e => e.ClassicZones.Count + e.LodgeZones.Count > 0).ToList();
            byProfession["Fishing"] = fish.Where(e => e.ClassicZones.Count + e.LodgeZones.Count > 0).ToList();

            // skinning: creatures with skinning loot
            var skin = new List<GatherEntity>();
            foreach (var kv in skinItems)
            {
                var n = idx.NpcInfo(kv.Key);
                if (n == null || n.MaxLevel < 1 || !zoneOfNpc.TryGetValue(n.Id, out var pz) || pz.Count == 0) continue;
                var e = new GatherEntity { Kind = "npc", Id = n.Id, Name = n.Name, Level = (n.MinLevel + n.MaxLevel + 1) / 2, Items = kv.Value.Take(6).ToList() };
                foreach (var z in pz) e.ClassicZones[z.Key] = z.Value;
                skin.Add(e);
            }
            byProfession["Skinning"] = skin;
        }

        LodgeAtlas SafeLodge() { try { return lodge(); } catch { return null; } }

        // item subclass of the trade-goods class (9 = herb, 7 = metal & stone), -1 otherwise
        int Sub(int itemId)
        {
            var it = item(itemId);
            return it != null && it.ClassId == 7 ? it.SubclassId : -1;
        }
    }
}
