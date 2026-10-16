using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using ElansAddonHub.Services.Atlas.Classic;
using ElansAddonHub.Services.Maps;

namespace ElansAddonHub.Services.Atlas
{
    // Reverse lookups over the Classic baseline that the index itself does not keep (what a source gives, who gives a quest, which creatures stand on a map,
    // what is in a zone). Built once per loaded index, on the thread that asks first (the page asks from a worker), kept for as long as the index lives.
    public sealed class AtlasDerived
    {
        static readonly ConditionalWeakTable<ClassicIndex, AtlasDerived> cache = new ConditionalWeakTable<ClassicIndex, AtlasDerived>();
        public static AtlasDerived For(ClassicIndex idx)
        {
            if (idx == null) return null;
            lock (cache) { if (!cache.TryGetValue(idx, out var d)) { d = new AtlasDerived(idx); cache.Add(idx, d); } return d; }
        }

        public readonly ClassicIndex Index;
        readonly Dictionary<long, List<ClassicLootEntry>> loot = new Dictionary<long, List<ClassicLootEntry>>();
        readonly Dictionary<long, List<ClassicQuest>> givers = new Dictionary<long, List<ClassicQuest>>();
        readonly Dictionary<int, List<int>> npcsByMap = new Dictionary<int, List<int>>();
        readonly Dictionary<int, List<int>> objectsByMap = new Dictionary<int, List<int>>();
        static readonly ClassicLootEntry[] NoLoot = new ClassicLootEntry[0];
        static readonly ClassicQuest[] NoQuests = new ClassicQuest[0];
        static readonly int[] NoIds = new int[0];

        static long Key(ClassicSourceKind k, int id) => ((long)(byte)k << 32) | (uint)id;

        AtlasDerived(ClassicIndex idx)
        {
            Index = idx;
            foreach (var kv in idx.ItemSources)
                foreach (var s in kv.Value)
                {
                    if (s.Kind == ClassicSourceKind.Vendor || s.Kind >= ClassicSourceKind.QuestReward) continue;
                    var k = Key(s.Kind, s.SourceId);
                    if (!loot.TryGetValue(k, out var l)) loot[k] = l = new List<ClassicLootEntry>();
                    l.Add(new ClassicLootEntry { ItemId = kv.Key, Source = s });
                }
            foreach (var l in loot.Values) l.Sort((a, b) => { int c = b.Source.Chance.CompareTo(a.Source.Chance); return c != 0 ? c : a.ItemId.CompareTo(b.ItemId); });
            foreach (var q in idx.Quests.Values.OrderBy(x => x.Id))
                foreach (var g in q.Givers)
                {
                    var k = ((g.IsObject ? 1L : 0L) << 32) | (uint)g.Id;
                    if (!givers.TryGetValue(k, out var l)) givers[k] = l = new List<ClassicQuest>();
                    l.Add(q);
                }
            foreach (var n in idx.Npcs.Values)
                foreach (var m in n.Spawns.Select(s => s.Map).Distinct())
                {
                    if (!npcsByMap.TryGetValue(m, out var l)) npcsByMap[m] = l = new List<int>();
                    l.Add(n.Id);
                }
            foreach (var o in idx.Objects.Values)
                foreach (var m in o.Spawns.Select(s => s.Map).Distinct())
                {
                    if (!objectsByMap.TryGetValue(m, out var l)) objectsByMap[m] = l = new List<int>();
                    l.Add(o.Id);
                }
        }

        // what an npc / object gives (best chance first)
        public IReadOnlyList<ClassicLootEntry> LootOf(ClassicSourceKind kind, int sourceId) => loot.TryGetValue(Key(kind, sourceId), out var l) ? (IReadOnlyList<ClassicLootEntry>)l : NoLoot;
        public IReadOnlyList<ClassicQuest> QuestsGivenBy(bool isObject, int id) => givers.TryGetValue(((isObject ? 1L : 0L) << 32) | (uint)id, out var l) ? (IReadOnlyList<ClassicQuest>)l : NoQuests;
        public IReadOnlyList<int> NpcsOnMap(int mapId) => npcsByMap.TryGetValue(mapId, out var l) ? (IReadOnlyList<int>)l : NoIds;
        public IReadOnlyList<int> ObjectsOnMap(int mapId) => objectsByMap.TryGetValue(mapId, out var l) ? (IReadOnlyList<int>)l : NoIds;

        // the levels most creatures inside an instance have (spawn-weighted, the middle 80 %); false when the baseline has too few to say
        public bool InstanceCreatureLevels(int mapId, out int min, out int max)
        {
            min = max = 0;
            var lv = new List<double>();
            foreach (var nid in NpcsOnMap(mapId))
            {
                var n = Index.Npcs[nid];
                if (n == null || n.MaxLevel <= 0 || n.Type == 8 || n.Type == 11) continue;
                int w = n.Spawns.Count(s => s.Map == mapId);
                for (int k = 0; k < Math.Min(w, 20); k++) lv.Add((n.MinLevel + n.MaxLevel) / 2.0);
            }
            if (lv.Count < 3) return false;
            lv.Sort();
            min = (int)Math.Round(lv[(int)(lv.Count * 0.1)]); max = Math.Max(min, (int)Math.Round(lv[Math.Min(lv.Count - 1, (int)(lv.Count * 0.9))]));
            return true;
        }

        // ---- what stands in a zone (cached per map index: a new game build gives new rectangles)
        public sealed class ZoneContent
        {
            public MapIndex Map;
            public int UiMapId;
            public readonly Dictionary<int, int> Npcs = new Dictionary<int, int>();      // npc id -> spawn points in the zone
            public readonly Dictionary<int, int> Objects = new Dictionary<int, int>();
            public int MinLevel, MaxLevel;                                               // the range most creatures there are (0 = unknown)
        }
        readonly Dictionary<int, ZoneContent> zones = new Dictionary<int, ZoneContent>();

        public ZoneContent Zone(MapIndex map, int uiMapId)
        {
            if (map == null) return null;
            lock (zones)
            {
                if (zones.TryGetValue(uiMapId, out var z) && ReferenceEquals(z.Map, map)) return z;
                z = Compute(map, uiMapId);
                zones[uiMapId] = z;
                return z;
            }
        }

        // what was worked out last for this zone, of whichever map index (the previous game build's while the new one is being worked out); null = never
        public ZoneContent ZoneAny(int uiMapId) { lock (zones) return zones.TryGetValue(uiMapId, out var z) ? z : null; }

        // already worked out for this map index (no work, no wait)
        public bool HasZone(MapIndex map, int uiMapId)
        {
            if (map == null) return false;
            lock (zones) return zones.TryGetValue(uiMapId, out var z) && ReferenceEquals(z.Map, map);
        }

        // The terrain tiles Zone(map, id) will ask the area probe about, per world map: the tile of every spawn that stands inside one of the zone's rectangles
        // (Compute asks MapIndex.WorldToMap, which asks AreaProbe.AreaAt, for exactly those). Lets ZoneLevels read them ahead on a few threads.
        public Dictionary<int, HashSet<int>> ZoneTiles(MapIndex map, IEnumerable<int> uiMapIds)
        {
            var res = new Dictionary<int, HashSet<int>>();
            if (map == null) return res;
            var want = new HashSet<int>(uiMapIds);
            foreach (var g in map.Set.Assignments.Where(a => want.Contains(a.UiMapId)).GroupBy(a => a.MapId))
            {
                var rects = g.ToList();
                if (!res.TryGetValue(g.Key, out var set)) res[g.Key] = set = new HashSet<int>();
                void Add(float x, float y)
                {
                    foreach (var a in rects) if (x >= a.MinX && x <= a.MaxX && y >= a.MinY && y <= a.MaxY) { int k = AreaProbe.TileKey(x, y); if (k >= 0) set.Add(k); return; }
                }
                foreach (var nid in NpcsOnMap(g.Key)) foreach (var s in Index.Npcs[nid].Spawns) if (s.Map == g.Key) Add(s.X, s.Y);
                foreach (var oid in ObjectsOnMap(g.Key)) foreach (var s in Index.Objects[oid].Spawns) if (s.Map == g.Key) Add(s.X, s.Y);
            }
            return res;
        }

        ZoneContent Compute(MapIndex map, int uiMapId)
        {
            var z = new ZoneContent { Map = map, UiMapId = uiMapId };
            var rects = map.Set.Assignments.Where(a => a.UiMapId == uiMapId).ToList();
            if (rects.Count == 0) return z;
            var levels = new List<KeyValuePair<double, int>>();   // level, weight
            foreach (var mapId in rects.Select(r => r.MapId).Distinct())
            {
                var mine = rects.Where(r => r.MapId == mapId).ToList();
                bool In(float x, float y) { foreach (var a in mine) if (x >= a.MinX && x <= a.MaxX && y >= a.MinY && y <= a.MaxY) return true; return false; }
                foreach (var nid in NpcsOnMap(mapId))
                {
                    var n = Index.Npcs[nid];
                    int c = 0;
                    foreach (var s in n.Spawns)
                    {
                        if (s.Map != mapId || !In(s.X, s.Y)) continue;
                        var p = map.WorldToMap(mapId, s.X, s.Y);
                        if (p == null || p.Value.UiMapId != uiMapId) continue;
                        c++;
                    }
                    if (c == 0) continue;
                    z.Npcs[nid] = c;
                    bool wildlife = n.NpcFlags == 0 && n.Type != 8 && n.Type != 11 && n.Rank <= 2 && n.MaxLevel > 0;
                    if (wildlife) levels.Add(new KeyValuePair<double, int>((n.MinLevel + n.MaxLevel) / 2.0, c));
                }
                foreach (var oid in ObjectsOnMap(mapId))
                {
                    var o = Index.Objects[oid];
                    int c = 0;
                    foreach (var s in o.Spawns)
                    {
                        if (s.Map != mapId || !In(s.X, s.Y)) continue;
                        var p = map.WorldToMap(mapId, s.X, s.Y);
                        if (p == null || p.Value.UiMapId != uiMapId) continue;
                        c++;
                    }
                    if (c > 0) z.Objects[oid] = c;
                }
            }
            if (levels.Sum(l => (long)l.Value) >= 3)
            {
                levels.Sort((a, b) => a.Key.CompareTo(b.Key));
                long total = levels.Sum(l => (long)l.Value), acc = 0; double lo = levels[0].Key, hi = levels[levels.Count - 1].Key; bool gotLo = false;
                foreach (var l in levels)
                {
                    acc += l.Value;
                    if (!gotLo && acc >= total * 0.05) { lo = l.Key; gotLo = true; }
                    if (acc >= total * 0.95) { hi = l.Key; break; }
                }
                z.MinLevel = (int)Math.Round(lo); z.MaxLevel = Math.Max(z.MinLevel, (int)Math.Round(hi));
            }
            return z;
        }
    }
}
