using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ElansAddonHub.Services;

namespace ElansAddonHub.Lodge
{
    // The Lodge's pooled Atlas observations (what everybody in the lodge saw: drops per mob / object / fishing zone, vendors, finds, spawn points, revealed
    // items), as the Outpost holds them. Filled from `atlas.data` frames (every field validated again, whatever the server says), kept in a cache file per
    // Lodge, read by the Atlas page through the methods below. Pure data: no UI, no network.
    public sealed class LodgeAtlas
    {
        public class DropRow
        {
            public string Source;                          // "npc:123", "obj:45" or "fish:1429" (the zone)
            public string Kind;                            // "npc" | "obj" | "fish"
            public int Id;                                 // npc id, object id, or uiMapId for fishing
            public long Kills;                             // looted kills / opens / casts in the whole lodge
            public long Drops;                             // times the item dropped there
            public long Count;                             // items in those drops (stacks)
            public double Chance;                          // Drops / Kills in percent (2 decimals)
            public int Members;                            // members who contributed
            public List<KeyValuePair<int, long>> Zones = new List<KeyValuePair<int, long>>();   // uiMapId -> kills there (most first)
        }

        public class VendorRow { public int NpcId, Zone, Stack, Limit; public long Price, At; public double X, Y; public bool Extended; }
        public class QuestRow { public int QuestId; public bool Choice; public long At; }

        public string Epoch = "";
        public long Rev;
        public long Updated;                               // unix s of the last answer
        readonly Dictionary<int, AtlasObs.Counter> kills = new Dictionary<int, AtlasObs.Counter>(), objects = new Dictionary<int, AtlasObs.Counter>(), fishing = new Dictionary<int, AtlasObs.Counter>();
        readonly Dictionary<int, AtlasObs.Vendor> vendors = new Dictionary<int, AtlasObs.Vendor>();
        readonly Dictionary<int, AtlasObs.Quest> quests = new Dictionary<int, AtlasObs.Quest>();
        readonly Dictionary<int, List<AtlasObs.Spot>> spawns = new Dictionary<int, List<AtlasObs.Spot>>();
        readonly Dictionary<int, AtlasObs.Revealed> revealed = new Dictionary<int, AtlasObs.Revealed>();
        readonly Dictionary<int, AtlasObs.SeenNpc> seenNpcs = new Dictionary<int, AtlasObs.SeenNpc>();
        readonly Dictionary<int, AtlasObs.SeenObject> seenObjects = new Dictionary<int, AtlasObs.SeenObject>();
        readonly Dictionary<int, AtlasObs.SeenQuest> seenQuests = new Dictionary<int, AtlasObs.SeenQuest>();
        readonly List<AtlasObs.Find> finds = new List<AtlasObs.Find>();     // ascending by Seq
        readonly Dictionary<int, Dictionary<int, List<AtlasObs.NodePt>>> nodes = new Dictionary<int, Dictionary<int, List<AtlasObs.NodePt>>>();   // gathering nodes the lodge merged (server 2.18, atlas3): object -> uiMapId -> nodes
        public long NodesChanged;                          // counts every answer that changed the nodes (the followed guides are rewritten when it moves)
        readonly Dictionary<int, List<AtlasObs.FishSpot>> fishSpots = new Dictionary<int, List<AtlasObs.FishSpot>>();   // fishing spots the lodge merged (server 2.21, atlas4): uiMapId -> spots
        public long FishSpotsChanged;                      // counts every answer that changed the fishing spots
        public int Dropped { get; private set; }           // entries of the last answers that failed validation

        // index by item, built on first use after a change
        Dictionary<int, List<DropRow>> dropIdx; Dictionary<int, List<VendorRow>> vendorIdx; Dictionary<int, List<QuestRow>> questIdx; Dictionary<int, List<AtlasObs.Find>> findIdx;
        static readonly IReadOnlyList<DropRow> NoDrops = new DropRow[0];
        static readonly IReadOnlyList<VendorRow> NoVendors = new VendorRow[0];
        static readonly IReadOnlyList<QuestRow> NoQuests = new QuestRow[0];
        static readonly IReadOnlyList<AtlasObs.Find> NoFinds = new AtlasObs.Find[0];
        static readonly IReadOnlyList<AtlasObs.Spot> NoSpots = new AtlasObs.Spot[0];

        public bool HasData => kills.Count + objects.Count + fishing.Count + vendors.Count + quests.Count + spawns.Count + revealed.Count + finds.Count + seenNpcs.Count + seenObjects.Count + seenQuests.Count + nodes.Count + fishSpots.Count > 0;
        public int NodeObjectCount => nodes.Count;
        public int KillSources => kills.Count;
        public int ObjectSources => objects.Count;
        public int FishingZones => fishing.Count;
        public int VendorCount => vendors.Count;
        public int FindCount => finds.Count;
        public int SpawnNpcCount => spawns.Count;
        public int RevealedCount => revealed.Count;
        public int SeenNpcCount => seenNpcs.Count;
        public int SeenObjectCount => seenObjects.Count;
        public int SeenQuestCount => seenQuests.Count;

        public void Clear()
        {
            kills.Clear(); objects.Clear(); fishing.Clear(); vendors.Clear(); quests.Clear(); spawns.Clear(); revealed.Clear(); finds.Clear();
            seenNpcs.Clear(); seenObjects.Clear(); seenQuests.Clear(); nodes.Clear(); fishSpots.Clear();
            Epoch = ""; Rev = 0; Dropped = 0; Invalidate();
        }

        void Invalidate() { dropIdx = null; vendorIdx = null; questIdx = null; findIdx = null; }

        static bool ValidEpoch(string e) => !string.IsNullOrEmpty(e) && e.Length <= 32 && e.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'));

        // One `atlas.data` page (or a cache file in the same shape). full = replace everything held first. Returns false when the page was ignored
        // (not an atlas.data frame, bad epoch, or an incremental page of another data set).
        public bool Apply(Dictionary<string, object> m)
        {
            if (m == null) return false;
            var epoch = m.Str("epoch");
            if (!ValidEpoch(epoch)) return false;
            bool full = m.Bool("full");
            if (!full && Epoch.Length > 0 && epoch != Epoch) return false;
            if (full) { Clear(); Dropped = 0; }
            Epoch = epoch;
            int dropped = 0;
            Sources(m.Child("kills"), kills, AtlasObs.MaxKillSources * 2000, true, AtlasObs.MaxId, ref dropped);
            Sources(m.Child("objects"), objects, AtlasObs.MaxObjectSources * 2000, true, AtlasObs.MaxId, ref dropped);
            Sources(m.Child("fishing"), fishing, AtlasObs.MaxFishingZones * 2000, false, AtlasObs.MaxMapId, ref dropped);
            var vo = m.Child("vendors");
            if (vo != null)
                foreach (var kv in vo)
                {
                    if (!AtlasObs.Key(kv.Key, AtlasObs.MaxId, out var id) || (!vendors.ContainsKey(id) && vendors.Count >= AtlasObs.MaxVendors)) { dropped++; continue; }
                    var v = AtlasObs.ParseVendor(kv.Value, ref dropped);
                    if (v != null) vendors[id] = v;
                }
            var qo = m.Child("quests");
            if (qo != null)
                foreach (var kv in qo)
                {
                    if (!AtlasObs.Key(kv.Key, AtlasObs.MaxId, out var id) || (!quests.ContainsKey(id) && quests.Count >= AtlasObs.MaxQuests)) { dropped++; continue; }
                    var q = AtlasObs.ParseQuest(kv.Value, ref dropped);
                    if (q != null) quests[id] = q;
                }
            var so = m.Child("spawns");
            if (so != null)
                foreach (var kv in so)
                {
                    if (!AtlasObs.Key(kv.Key, AtlasObs.MaxId, out var id) || (!spawns.ContainsKey(id) && spawns.Count >= AtlasObs.MaxSpawnNpcs)) { dropped++; continue; }
                    var pts = AtlasObs.ParseSpots(kv.Value, AtlasObs.MaxSpawnPoints, ref dropped);
                    if (pts.Count > 0) spawns[id] = pts;
                }
            var io = m.Child("items");
            if (io != null)
                foreach (var kv in io)
                {
                    if (!AtlasObs.Key(kv.Key, AtlasObs.MaxItemId, out var id) || (!revealed.ContainsKey(id) && revealed.Count >= AtlasObs.MaxRevealed)) { dropped++; continue; }
                    var r = AtlasObs.ParseRevealed(id, kv.Value, ref dropped);
                    if (r != null) revealed[id] = r;
                }
            var sn = m.Child("seenNpcs");
            if (sn != null)
                foreach (var kv in sn)
                {
                    if (!AtlasObs.Key(kv.Key, AtlasObs.MaxId, out var id) || (!seenNpcs.ContainsKey(id) && seenNpcs.Count >= AtlasObs.MaxSeenNpcs)) { dropped++; continue; }
                    var x = AtlasObs.ParseSeenNpc(id, kv.Value, ref dropped);
                    if (x != null) seenNpcs[id] = x;
                }
            var sg = m.Child("seenObjects");
            if (sg != null)
                foreach (var kv in sg)
                {
                    if (!AtlasObs.Key(kv.Key, AtlasObs.MaxId, out var id) || (!seenObjects.ContainsKey(id) && seenObjects.Count >= AtlasObs.MaxSeenObjects)) { dropped++; continue; }
                    var x = AtlasObs.ParseSeenObject(id, kv.Value, ref dropped);
                    if (x != null) seenObjects[id] = x;
                }
            var sw = m.Child("seenQuests");
            if (sw != null)
                foreach (var kv in sw)
                {
                    if (!AtlasObs.Key(kv.Key, AtlasObs.MaxId, out var id) || (!seenQuests.ContainsKey(id) && seenQuests.Count >= AtlasObs.MaxSeenQuests)) { dropped++; continue; }
                    var x = AtlasObs.ParseSeenQuest(id, kv.Value, ref dropped);
                    if (x != null) seenQuests[id] = x;
                }
            var nd = m.Child("nodes");
            if (nd != null)
            {
                bool any = false;
                foreach (var kv in nd)
                {
                    if (!AtlasObs.Key(kv.Key, AtlasObs.MaxId, out var id) || (!nodes.ContainsKey(id) && nodes.Count >= AtlasObs.MaxNodeObjects * 50)) { dropped++; continue; }
                    var maps = AtlasObs.ParseNodeObject(kv.Value, ref dropped, MaxLodgeNodesPerMap);
                    if (maps.Count > 0) { nodes[id] = maps; any = true; }
                }
                if (any) NodesChanged++;
            }
            var fsd = m.Child("fishspots");
            if (fsd != null)
            {
                var maps = AtlasObs.ParseFishSpotMaps(fsd, ref dropped, MaxLodgeNodesPerMap, AtlasObs.MaxFishMaps * 50);
                foreach (var kv in maps) fishSpots[kv.Key] = kv.Value;
                if (maps.Count > 0) FishSpotsChanged++;
            }
            var fl = m.List("finds");
            if (fl.Count > 0)
            {
                var have = new HashSet<long>(finds.Select(f => f.Seq));
                foreach (var e in fl)
                {
                    var f = AtlasObs.ParseFind(e, ref dropped);
                    if (f == null || f.Seq <= 0 || !have.Add(f.Seq)) continue;
                    if (string.IsNullOrEmpty(f.By)) f.By = "a member";
                    finds.Add(f);
                }
                finds.Sort((a, b) => a.Seq.CompareTo(b.Seq));
                if (finds.Count > AtlasObs.MaxFinds) finds.RemoveRange(0, finds.Count - AtlasObs.MaxFinds);
            }
            var rev = m.Long("rev");
            if (rev >= 0 && rev < long.MaxValue / 4) Rev = rev;
            var at = m.Long("at"); if (at > 0 && at < 4_000_000_000L) Updated = at;
            Dropped += dropped;
            Invalidate();
            return true;
        }

        static void Sources(Dictionary<string, object> t, Dictionary<int, AtlasObs.Counter> into, int cap, bool zones, int maxKey, ref int dropped)
        {
            if (t == null) return;
            foreach (var kv in t)
            {
                if (!AtlasObs.Key(kv.Key, maxKey, out var id) || (!into.ContainsKey(id) && into.Count >= cap)) { dropped++; continue; }
                var c = AtlasObs.ParseCounter(kv.Value, zones, AtlasObs.MaxAggregateCount, ref dropped);
                if (c != null) into[id] = c;
            }
        }

        // ---------------------------------------------------------------- cache file (the same shape as a full atlas.data answer)

        public Dictionary<string, object> ToCache()
        {
            Dictionary<string, object> Counters(Dictionary<int, AtlasObs.Counter> d)
            {
                var o = new Dictionary<string, object>();
                foreach (var kv in d.OrderBy(k => k.Key)) { var n = AtlasObs.CounterNode(kv.Value); n["m"] = kv.Value.Members; o[kv.Key.ToString()] = n; }
                return o;
            }
            var vo = new Dictionary<string, object>();
            foreach (var kv in vendors.OrderBy(k => k.Key)) vo[kv.Key.ToString()] = AtlasObs.VendorNode(kv.Value);
            var qo = new Dictionary<string, object>();
            foreach (var kv in quests.OrderBy(k => k.Key))
                qo[kv.Key.ToString()] = new Dictionary<string, object>
                {
                    ["at"] = kv.Value.At, ["rewards"] = kv.Value.Rewards.Select(x => (object)x).ToList(), ["choices"] = kv.Value.Choices.Select(x => (object)x).ToList(),
                };
            var so = new Dictionary<string, object>();
            foreach (var kv in spawns.OrderBy(k => k.Key)) so[kv.Key.ToString()] = kv.Value.Select(p => (object)new Dictionary<string, object> { ["z"] = p.Zone, ["x"] = p.X, ["y"] = p.Y }).ToList();
            var io = new Dictionary<string, object>();
            foreach (var kv in revealed.OrderBy(k => k.Key)) io[kv.Key.ToString()] = AtlasObs.RevealedNode(kv.Value);
            var sno = new Dictionary<string, object>();
            foreach (var kv in seenNpcs.OrderBy(k => k.Key)) sno[kv.Key.ToString()] = AtlasObs.SeenNpcNode(kv.Value);
            var sgo = new Dictionary<string, object>();
            foreach (var kv in seenObjects.OrderBy(k => k.Key)) sgo[kv.Key.ToString()] = AtlasObs.SeenObjectNode(kv.Value);
            var swo = new Dictionary<string, object>();
            foreach (var kv in seenQuests.OrderBy(k => k.Key)) swo[kv.Key.ToString()] = AtlasObs.SeenQuestNode(kv.Value);
            var nco = new Dictionary<string, object>();
            foreach (var kv in nodes.OrderBy(k => k.Key)) nco[kv.Key.ToString()] = AtlasObs.NodesNode(kv.Value);
            var fso = new Dictionary<string, object>();
            foreach (var kv in fishSpots.OrderBy(k => k.Key)) fso[kv.Key.ToString()] = AtlasObs.FishSpotsNode(kv.Value);
            var fl = finds.Select(f =>
            {
                var o = new Dictionary<string, object> { ["s"] = f.Seq, ["i"] = f.Item, ["c"] = f.Count, ["src"] = f.Src, ["at"] = f.At, ["by"] = f.By ?? "" };
                if (f.Zone > 0) { o["z"] = f.Zone; o["x"] = f.X; o["y"] = f.Y; }
                return (object)o;
            }).ToList();
            return new Dictionary<string, object>
            {
                ["t"] = "atlas.data", ["epoch"] = Epoch, ["rev"] = Rev, ["at"] = Updated, ["full"] = true,
                ["kills"] = Counters(kills), ["objects"] = Counters(objects), ["fishing"] = Counters(fishing), ["vendors"] = vo, ["quests"] = qo, ["spawns"] = so, ["items"] = io, ["finds"] = fl,
                ["seenNpcs"] = sno, ["seenObjects"] = sgo, ["seenQuests"] = swo, ["nodes"] = nco, ["fishspots"] = fso,
            };
        }

        // ---------------------------------------------------------------- the read API (for the Atlas page)

        void BuildIndexes()
        {
            if (dropIdx != null) return;
            dropIdx = new Dictionary<int, List<DropRow>>(); vendorIdx = new Dictionary<int, List<VendorRow>>(); questIdx = new Dictionary<int, List<QuestRow>>(); findIdx = new Dictionary<int, List<AtlasObs.Find>>();
            void Drops(string kind, Dictionary<int, AtlasObs.Counter> d)
            {
                foreach (var src in d)
                {
                    var c = src.Value;
                    foreach (var it in c.Items)
                    {
                        var row = new DropRow
                        {
                            Kind = kind, Id = src.Key, Source = kind + ":" + src.Key, Kills = c.N, Drops = it.Value[0], Count = it.Value[1], Members = c.Members,
                            Chance = c.N > 0 ? Math.Round(it.Value[0] * 100.0 / c.N, 2) : 0,
                            Zones = kind == "fish" ? new List<KeyValuePair<int, long>> { new KeyValuePair<int, long>(src.Key, c.N) } : c.Zones.OrderByDescending(z => z.Value).ThenBy(z => z.Key).ToList(),
                        };
                        if (!dropIdx.TryGetValue(it.Key, out var l)) dropIdx[it.Key] = l = new List<DropRow>();
                        l.Add(row);
                    }
                }
            }
            Drops("npc", kills); Drops("obj", objects); Drops("fish", fishing);
            foreach (var l in dropIdx.Values) l.Sort((a, b) => { int c = b.Chance.CompareTo(a.Chance); return c != 0 ? c : b.Kills.CompareTo(a.Kills); });
            foreach (var v in vendors)
                foreach (var it in v.Value.Items)
                {
                    if (!vendorIdx.TryGetValue(it.Key, out var l)) vendorIdx[it.Key] = l = new List<VendorRow>();
                    l.Add(new VendorRow { NpcId = v.Key, Price = it.Value[0], Stack = (int)it.Value[1], Limit = (int)it.Value[2], Extended = it.Value.Length > 3 && it.Value[3] == 1, Zone = v.Value.Zone, X = v.Value.X, Y = v.Value.Y, At = v.Value.At });
                }
            foreach (var q in quests)
            {
                foreach (var id in q.Value.Rewards) { if (!questIdx.TryGetValue(id, out var l)) questIdx[id] = l = new List<QuestRow>(); l.Add(new QuestRow { QuestId = q.Key, Choice = false, At = q.Value.At }); }
                foreach (var id in q.Value.Choices) { if (!questIdx.TryGetValue(id, out var l)) questIdx[id] = l = new List<QuestRow>(); l.Add(new QuestRow { QuestId = q.Key, Choice = true, At = q.Value.At }); }
            }
            foreach (var l in vendorIdx.Values) l.Sort((a, b) => a.Price.CompareTo(b.Price));
            for (int i = finds.Count - 1; i >= 0; i--)                       // newest first
            {
                if (!findIdx.TryGetValue(finds[i].Item, out var l)) findIdx[finds[i].Item] = l = new List<AtlasObs.Find>();
                l.Add(finds[i]);
            }
        }

        // where an item dropped: per source the lodge's kills, drops, the chance in percent and the zones (best chance first)
        public IReadOnlyList<DropRow> Drops(int itemId) { BuildIndexes(); return dropIdx.TryGetValue(itemId, out var l) ? l : NoDrops; }
        // vendors that sold it (cheapest first)
        public IReadOnlyList<VendorRow> Vendors(int itemId) { BuildIndexes(); return vendorIdx.TryGetValue(itemId, out var l) ? l : NoVendors; }
        // who in the lodge found it, newest first (By = the member's display name)
        public IReadOnlyList<AtlasObs.Find> Finds(int itemId) { BuildIndexes(); return findIdx.TryGetValue(itemId, out var l) ? l : NoFinds; }
        // quests that gave it as a reward or choice
        public IReadOnlyList<QuestRow> QuestsRewarding(int itemId) { BuildIndexes(); return questIdx.TryGetValue(itemId, out var l) ? l : NoQuests; }
        // observed spawn points of an npc
        public IReadOnlyList<AtlasObs.Spot> Spawns(int npcId) { return spawns.TryGetValue(npcId, out var l) ? l : NoSpots; }
        // the nodes of an object the lodge merged, per map (empty: none known); the lists are shared, read only
        public IReadOnlyDictionary<int, List<AtlasObs.NodePt>> NodesOf(int objectId) { return nodes.TryGetValue(objectId, out var d) ? d : NoNodes; }
        public IEnumerable<int> NodeObjectIds => nodes.Keys;
        public IReadOnlyList<AtlasObs.NodePt> Nodes(int objectId, int uiMapId) { return nodes.TryGetValue(objectId, out var d) && d.TryGetValue(uiMapId, out var l) ? l : NoNodePts; }
        static readonly Dictionary<int, List<AtlasObs.NodePt>> NoNodes = new Dictionary<int, List<AtlasObs.NodePt>>();
        static readonly IReadOnlyList<AtlasObs.NodePt> NoNodePts = new AtlasObs.NodePt[0];
        public const int MaxLodgeNodesPerMap = 300;
        // the fishing spots the lodge merged on one map (most caught first); empty when nobody shared any
        public IReadOnlyList<AtlasObs.FishSpot> FishSpotsOn(int uiMapId) { return fishSpots.TryGetValue(uiMapId, out var l) ? l : NoFishSpots; }
        public IEnumerable<int> FishSpotMaps => fishSpots.Keys;
        static readonly IReadOnlyList<AtlasObs.FishSpot> NoFishSpots = new AtlasObs.FishSpot[0];
        // item details a member's client revealed (null: nobody has)
        public AtlasObs.Revealed Revealed(int itemId) { return revealed.TryGetValue(itemId, out var r) ? r : null; }
        public IEnumerable<AtlasObs.Revealed> AllRevealed => revealed.Values;
        // what one npc / object / fishing zone dropped overall (null: nothing observed)
        public AtlasObs.Counter Loot(string kind, int id)
        {
            var d = kind == "npc" ? kills : kind == "obj" ? objects : kind == "fish" ? fishing : null;
            return d != null && d.TryGetValue(id, out var c) ? c : null;
        }
        // what the lodge's clients have seen of an npc / object / quest (null: nobody has shared it)
        public AtlasObs.SeenNpc Npc(int id) { return seenNpcs.TryGetValue(id, out var x) ? x : null; }
        public AtlasObs.SeenObject Object(int id) { return seenObjects.TryGetValue(id, out var x) ? x : null; }
        public AtlasObs.SeenQuest QuestInfo(int questId) { return seenQuests.TryGetValue(questId, out var x) ? x : null; }
        public AtlasObs.Vendor Vendor(int npcId) { return vendors.TryGetValue(npcId, out var v) ? v : null; }
        public AtlasObs.Quest Quest(int questId) { return quests.TryGetValue(questId, out var q) ? q : null; }
        public IReadOnlyList<AtlasObs.Find> AllFinds => finds;
        // every vendor / observed spawn point the lodge has (the world map's layers)
        public IEnumerable<KeyValuePair<int, AtlasObs.Vendor>> AllVendors => vendors;
        public IEnumerable<KeyValuePair<int, List<AtlasObs.Spot>>> AllSpawns => spawns;
    }

    // The Atlas pool (Lodge server 2.15, feature "atlas"). The Outpost reads Elan's Toolkit's `atlas.obs` Hub module, uploads what changed (cumulative per-entry
    // totals, at most one batch per 3 minutes, only while "Share my Atlas observations with this Lodge" is on and the member is not a guest), downloads the
    // Lodge's aggregate (atlas.get {since, epoch}, pages, on connect and every few minutes) and keeps it in LodgeAtlas + a cache file per Lodge. On a server
    // without the feature nothing is sent. Downloading works with sharing off.
    public partial class LodgeSession
    {
        public static readonly Version AtlasServer = AtlasObs.ServerVersion;
        public bool SupportsAtlas { get; private set; }
        public bool SupportsAtlasNodes { get; private set; }                  // the server also takes `nodes` (feature "atlas3", server 2.18)
        public bool SupportsAtlasFish { get; private set; }                   // the server also takes `fishspots` (feature "atlas4", server 2.21)
        public bool SupportsAtlasSeen { get; private set; }                   // the server also takes seenNpcs / seenObjects / seenQuests (feature "atlas2")
        public readonly LodgeAtlas AtlasData = new LodgeAtlas();

        AtlasObs.Snapshot atlasLocal;                         // last read of the Hub module
        Dictionary<string, string> atlasSent;                 // sent key -> signature of what the lodge already has from me (persisted per server)
        AtlasObs.Batch atlasInflight;                         // an atlas.share waiting for its answer
        DateTime atlasNextShare = DateTime.MinValue;          // not before
        DateTime atlasLastGet = DateTime.MinValue;
        DateTime atlasCacheSaved = DateTime.MinValue;
        bool atlasGuest;                                      // the server said guests can't contribute
        bool atlasDisabled;                                   // the server said the owner switched the feature off
        bool atlasBacklog;                                    // changed entries are waiting for the next slot
        bool atlasCacheDirty;
        string atlasCacheFor;                                 // the server the in-memory Atlas belongs to
        int atlasPollTick;
        bool atlasFetching;                                   // a get was asked and the last page said more / is not in yet

        // one file per Lodge server, so several lodges each keep their own state
        string AtlasTag()
        {
            using (var sha = SHA1.Create())
            {
                var h = sha.ComputeHash(Encoding.UTF8.GetBytes((Settings?.LodgeUrl ?? "").Trim().ToLowerInvariant()));
                return string.Concat(h.Take(5).Select(b => b.ToString("x2")));
            }
        }

        string AtlasSentPath => Path.Combine(Util.DataDir, "atlas-sent-" + AtlasTag() + ".txt");
        string AtlasCachePath => Path.Combine(Util.DataDir, "atlas-cache-" + AtlasTag() + ".json");

        void ReadAtlasFeatures(List<string> features)
        {
            var v = ParseVersion(ServerVersion);
            SupportsAtlas = features.Contains("atlas") && (v == null || v >= AtlasServer);
            SupportsAtlasSeen = SupportsAtlas && features.Contains("atlas2");
            SupportsAtlasNodes = SupportsAtlas && features.Contains("atlas3") && (v == null || v >= AtlasObs.NodesServerVersion);
            SupportsAtlasFish = SupportsAtlas && features.Contains("atlas4") && (v == null || v >= AtlasObs.FishSpotsServerVersion);
            atlasInflight = null; atlasGuest = false; atlasDisabled = false; atlasFetching = false;
        }

        void ResetAtlas()
        {
            SaveAtlasCache(true);
            SupportsAtlas = false; SupportsAtlasSeen = false; SupportsAtlasNodes = false; SupportsAtlasFish = false; atlasInflight = null; atlasGuest = false; atlasDisabled = false; atlasBacklog = false; atlasFetching = false;
            atlasLocal = null; atlasSent = null; atlasNextShare = DateTime.MinValue; atlasLastGet = DateTime.MinValue;
        }

        // what the lodge already has from me (key -> signature)
        Dictionary<string, string> AtlasSent()
        {
            if (atlasSent != null) return atlasSent;
            atlasSent = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                if (File.Exists(AtlasSentPath) && new FileInfo(AtlasSentPath).Length < 32L * 1024 * 1024)
                    foreach (var l in File.ReadAllLines(AtlasSentPath))
                    {
                        var p = l.Split('\t');
                        if (p.Length == 2 && p[0].Length > 0 && p[0].Length < 120 && p[1].Length > 0 && p[1].Length <= 32) atlasSent[p[0]] = p[1];
                    }
            }
            catch (Exception e) { Util.Log("atlas: could not read the upload record (" + e.GetType().Name + ")"); }
            return atlasSent;
        }

        void SaveAtlasSent()
        {
            try
            {
                var sent = AtlasSent();
                // finds that left the Hub's list for good are not remembered
                if (atlasLocal != null)
                {
                    var live = new HashSet<string>(atlasLocal.Finds.Select(AtlasObs.FindKey), StringComparer.Ordinal);
                    foreach (var k in sent.Keys.Where(k => k.Length > 0 && k[0] == 'F' && !live.Contains(k)).ToList()) sent.Remove(k);
                }
                var tmp = AtlasSentPath + ".tmp";
                File.WriteAllLines(tmp, sent.Select(kv => kv.Key + "\t" + kv.Value), new UTF8Encoding(false));
                if (File.Exists(AtlasSentPath)) File.Replace(tmp, AtlasSentPath, null); else File.Move(tmp, AtlasSentPath);
            }
            catch (Exception e) { Util.Log("atlas: could not save the upload record (" + e.GetType().Name + ")"); }
        }

        // the cache of the Lodge's aggregate: loaded once per server (when the welcome arrives), saved at most every minute and when the session ends
        void LoadAtlasCache()
        {
            var tag = AtlasTag();
            if (atlasCacheFor == tag) return;
            atlasCacheFor = tag;
            AtlasData.Clear();
            try
            {
                if (File.Exists(AtlasCachePath) && new FileInfo(AtlasCachePath).Length < 48L * 1024 * 1024)
                {
                    var m = Json.Obj(File.ReadAllText(AtlasCachePath, Encoding.UTF8));
                    if (m != null) { m["full"] = true; AtlasData.Apply(m); }
                }
            }
            catch (Exception e) { Util.Log("atlas: could not read the cache (" + e.GetType().Name + ")"); AtlasData.Clear(); }
            atlasCacheDirty = false;
        }

        void SaveAtlasCache(bool force)
        {
            if (!atlasCacheDirty || atlasCacheFor == null) return;
            if (!force && Clock() - atlasCacheSaved < TimeSpan.FromSeconds(60)) return;
            try
            {
                var tmp = AtlasCachePath + ".tmp";
                File.WriteAllText(tmp, Json.Write(AtlasData.ToCache()), new UTF8Encoding(false));
                if (File.Exists(AtlasCachePath)) File.Replace(tmp, AtlasCachePath, null); else File.Move(tmp, AtlasCachePath);
                atlasCacheDirty = false; atlasCacheSaved = Clock();
            }
            catch (Exception e) { Util.Log("atlas: could not save the cache (" + e.GetType().Name + ")"); }
        }

        bool AtlasMayShare => Settings != null && !Settings.AtlasShareOff && !atlasGuest && !atlasDisabled && MyRole != "guest" && !(Me != null && Me.Guest);

        // After the welcome, after the Hub file changed, and when a waiting batch may go: upload what changed (if allowed), then ask for the aggregate.
        // `connect` = just signed in (loads the cache and always asks).
        public void AtlasSync(bool connect = false)
        {
            if (!SupportsAtlas) return;
            if (connect) LoadAtlasCache();
            bool shared = false;
            if (AtlasMayShare)
            {
                try { atlasLocal = AtlasObs.TestSource != null ? AtlasObs.TestSource() : AtlasObs.Read(Settings?.WowRoot); }
                catch (Exception e) { Util.Log("atlas: " + e.GetType().Name); atlasLocal = null; }
                LogAtlasLocal();
                shared = SendAtlasBatch();
            }
            if (!shared && !atlasDisabled && (connect || Clock() - atlasLastGet > TimeSpan.FromMinutes(5))) RequestAtlas();
        }

        bool SendAtlasBatch()
        {
            if (atlasInflight != null) return true;                                    // an answer is pending: the aggregate follows it
            IEnumerable<AtlasObs.Revealed> rev;
            try { rev = AtlasObs.RevealedSource?.Items()?.ToList() ?? new List<AtlasObs.Revealed>(); }
            catch (Exception e) { Util.Log("atlas: revealed items: " + e.GetType().Name); rev = new List<AtlasObs.Revealed>(); }
            var batch = AtlasObs.MakeBatch(atlasLocal, rev, AtlasSent(), SupportsAtlasSeen, SupportsAtlasNodes, SupportsAtlasFish);
            atlasBacklog = batch != null;
            if (batch == null) return false;
            if (Clock() < atlasNextShare) return false;                                // the next slot is not here yet
            atlasInflight = batch;
            LogAtlasUpload(batch);
            atlasNextShare = Clock() + AtlasObs.ShareEvery + TimeSpan.FromSeconds(5);
            _ = Send(batch.Frame);
            return true;
        }

        void RequestAtlas()
        {
            if (!SupportsAtlas || atlasDisabled) return;
            atlasLastGet = Clock();
            var o = new Dictionary<string, object> { ["t"] = "atlas.get" };
            if (AtlasData.Rev > 0 && AtlasData.Epoch.Length > 0) { o["since"] = AtlasData.Rev; o["epoch"] = AtlasData.Epoch; }
            atlasFetching = true;
            _ = Send(o);
        }

        void OnAtlasShared(Dictionary<string, object> m)
        {
            if (!SupportsAtlas) return;
            var batch = atlasInflight;
            atlasInflight = null;
            if (m.Bool("ok") && batch != null)
            {
                var sent = AtlasSent();
                foreach (var kv in batch.Sigs) sent[kv.Key] = kv.Value;                // refused entries are not retried forever either
                SaveAtlasSent();
                atlasBacklog = batch.Remaining > 0;
                Util.Log("atlas: shared " + batch.Entries + " entries (" + batch.Finds + " finds): accepted " + m.Long("accepted") + ", unchanged " + m.Long("unchanged") + ", rejected " + m.Long("rejected") + (batch.Remaining > 0 ? ", " + batch.Remaining + " more waiting" : ""));
            }
            else
            {
                var why = m.Str("why");
                if (why == "guest") atlasGuest = true;
                else if (why == "disabled") atlasDisabled = true;
                else if (why == "rate") atlasNextShare = Clock() + AtlasObs.ShareEvery;
                else if (why == "full") { atlasNextShare = Clock() + TimeSpan.FromMinutes(30); Util.Log("atlas: the lodge's Atlas is full - new contributions are refused until the Guild Master makes room"); }
                else atlasNextShare = Clock() + TimeSpan.FromMinutes(2);
            }
            RequestAtlas();
        }

        void OnAtlasData(Dictionary<string, object> m)
        {
            if (!SupportsAtlas) return;
            if (!AtlasData.Apply(m)) { atlasFetching = false; return; }
            atlasCacheDirty = true;
            if (m.Bool("more")) RequestAtlas();                                        // the next page
            else { atlasFetching = false; SaveAtlasCache(false); try { AtlasResendMissing(); } catch (Exception) { } }
            Util.Log("atlas: " + AtlasData.KillSources + " mobs, " + AtlasData.VendorCount + " vendors, " + AtlasData.FindCount + " finds from the lodge (rev " + AtlasData.Rev + ")");
            Changed?.Invoke();
        }

        // every 2 s with the other ticks: look at the Hub file about every 30 s; send a waiting batch when its slot comes; refresh the aggregate every 5 minutes
        void AtlasTick()
        {
            if (!SupportsAtlas) return;
            if (atlasPollTick++ % 15 == 0 || (atlasBacklog && atlasInflight == null && Clock() >= atlasNextShare)) AtlasSync();
            SaveAtlasCache(false);
        }

        // "Share my Atlas observations with this Lodge" was switched
        public void AtlasSettingsChanged()
        {
            if (SupportsAtlas) AtlasSync();
        }

        // hub.log, counts only: what this upload carries per section, and per section what the Hub file held / what validation left out
        void LogAtlasUpload(AtlasObs.Batch batch)
        {
            try
            {
                var sb = new StringBuilder("atlas: upload " + batch.Entries + " entries: ");
                sb.Append(string.Join(", ", batch.Counts.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Key + " " + k.Value)));
                if (batch.Remaining > 0) sb.Append("; ").Append(batch.Remaining).Append(" more waiting");
                var s = atlasLocal;
                if (s != null && s.Stats.Count > 0)
                {
                    sb.Append("; Hub file (entries/kept/left out): ").Append(string.Join(", ", s.Stats.Where(k => k.Value[0] > 0 || k.Value[2] > 0).Select(k => k.Key + " " + k.Value[0] + "/" + k.Value[1] + "/" + k.Value[2])));
                    if (s.UnknownSections.Count > 0) sb.Append("; not read: ").Append(string.Join(",", s.UnknownSections.Take(8)));
                }
                Util.Log(sb.ToString());
            }
            catch (Exception) { }
        }

        // hub.log: what the Hub module holds (so a missing find can be traced to the game side or to the upload), only when it changed
        string atlasLocalLogged = "";
        void LogAtlasLocal()
        {
            var s = atlasLocal;
            if (s == null) return;
            long newest = s.Finds.Count == 0 ? 0 : s.Finds.Max(f => f.At);
            var line = "atlas: Hub data: " + s.Finds.Count + " finds" + (newest > 0 ? " (newest " + DateTimeOffset.FromUnixTimeSeconds(newest).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + ")" : "")
                + ", " + s.Vendors.Count + " vendors, " + s.Kills.Count + " mobs, " + s.Dropped + " entries left out by validation" + (s.FindsOverCap > 0 ? " (" + s.FindsOverCap + " oldest finds over the " + AtlasObs.MaxHubFinds + " cap)" : "");
            if (line == atlasLocalLogged) return;
            atlasLocalLogged = line;
            Util.Log(line);
        }

        readonly HashSet<string> atlasResent = new HashSet<string>(StringComparer.Ordinal);   // find keys already asked for again this session (once each)

        // Finds marked as sent that the Lodge's pool does not hold (never stored: a refused or lost share) are forgotten, so the next batch sends them again - once per find and session.
        // Only finds newer than the pool's oldest find count (older ones may have been dropped by the server's own caps). The server drops duplicates by the same key.
        internal int AtlasResendMissing()
        {
            var s = atlasLocal; var pool = AtlasData.AllFinds;
            if (s == null || s.Finds.Count == 0 || pool.Count == 0) return 0;
            long oldest = pool.Min(f => f.At);
            var have = new HashSet<string>(pool.Select(AtlasObs.FindKey), StringComparer.Ordinal);
            var sent = AtlasSent(); int n = 0;
            foreach (var f in s.Finds)
            {
                var k = AtlasObs.FindKey(f);
                if (f.At < oldest || have.Contains(k) || !sent.ContainsKey(k) || !atlasResent.Add(k)) continue;
                sent.Remove(k); n++;
            }
            if (n > 0) { SaveAtlasSent(); atlasBacklog = true; Util.Log("atlas: " + n + " finds were marked as sent but are not in the Lodge pool - sending them again"); }
            return n;
        }

        // test hooks
        public bool AtlasBacklogForTest => atlasBacklog;
        public bool AtlasInflightForTest => atlasInflight != null;
        public bool AtlasGuestForTest => atlasGuest;
        public Dictionary<string, string> AtlasSentForTest => AtlasSent();
        public string AtlasSentPathForTest => AtlasSentPath;
        public int AtlasResendMissingForTest(AtlasObs.Snapshot local) { atlasLocal = local; return AtlasResendMissing(); }
        public string AtlasCachePathForTest => AtlasCachePath;
        public void AtlasSaveCacheForTest() { SaveAtlasCache(true); }
        public void AtlasResetForTest()
        {
            atlasCacheDirty = false; atlasCacheFor = null; AtlasData.Clear(); atlasSent = null; atlasInflight = null; atlasNextShare = DateTime.MinValue; atlasLastGet = DateTime.MinValue;
            atlasGuest = false; atlasDisabled = false; atlasBacklog = false; atlasLocal = null; atlasFetching = false; atlasCacheSaved = DateTime.MinValue; AtlasObs.ResetCache();
        }
    }
}
