using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace ElansAddonHub.Services.Atlas.Classic
{
    public sealed class ClassicLootEntry
    {
        public int ItemId;
        public ClassicItemSource Source;
    }

    // The compact local index built from the dump: items -> sources, npcs, objects, vendors, quests, plus the query API. Held fully in memory
    // (a few tens of MB for the whole classic world); stored as one gzip file with a small binary layout (varints, floats, string = length + UTF-8).
    public sealed class ClassicIndex
    {
        // 2 (Outpost 2.77): objects carry their lock, quests their chain (prev / next / exclusive group / breadcrumb), skill and reputation needs, enders.
        // A version-1 file still loads (those fields empty); ClassicAtlas.Outdated tells the page to read the dump again.
        public const int FormatVersion = 2, OldestReadable = 1;
        static readonly byte[] Magic = { (byte)'E', (byte)'C', (byte)'L', (byte)'I' };

        public ClassicProvenance Provenance = new ClassicProvenance();
        public readonly Dictionary<int, ClassicItemInfo> Items = new Dictionary<int, ClassicItemInfo>();
        public readonly Dictionary<int, ClassicNpc> Npcs = new Dictionary<int, ClassicNpc>();
        public readonly Dictionary<int, ClassicObject> Objects = new Dictionary<int, ClassicObject>();
        public readonly Dictionary<int, ClassicQuest> Quests = new Dictionary<int, ClassicQuest>();
        public readonly Dictionary<int, List<ClassicItemSource>> ItemSources = new Dictionary<int, List<ClassicItemSource>>();

        // derived at Finish()
        Dictionary<int, List<ClassicVendorItem>> vendors;
        Dictionary<int, List<ClassicQuest>> questsRewarding, questsNeeding;
        struct SearchRow { public string Lower; public string Name; public ClassicEntityKind Kind; public int Id; }
        SearchRow[] search;

        static readonly ClassicItemSource[] NoSources = new ClassicItemSource[0];

        public void Add(int itemId, ClassicItemSource s)
        {
            if (!ItemSources.TryGetValue(itemId, out var l)) ItemSources[itemId] = l = new List<ClassicItemSource>();
            l.Add(s);
        }

        // Builds the lookups; call once after filling (the importer and Load do).
        public void Finish()
        {
            foreach (var l in ItemSources.Values)
                l.Sort((a, b) =>
                {
                    int c = a.Kind.CompareTo(b.Kind);
                    if (c != 0) return c;
                    c = b.Chance.CompareTo(a.Chance);
                    return c != 0 ? c : a.SourceId.CompareTo(b.SourceId);
                });
            vendors = new Dictionary<int, List<ClassicVendorItem>>();
            foreach (var kv in ItemSources)
                foreach (var s in kv.Value)
                {
                    if (s.Kind == ClassicSourceKind.Vendor)
                    {
                        if (!vendors.TryGetValue(s.SourceId, out var v)) vendors[s.SourceId] = v = new List<ClassicVendorItem>();
                        v.Add(new ClassicVendorItem { ItemId = kv.Key, MaxCount = s.MaxCount, RestockSeconds = s.RestockSeconds });
                    }
                }
            foreach (var v in vendors.Values) v.Sort((a, b) => a.ItemId.CompareTo(b.ItemId));

            questsRewarding = new Dictionary<int, List<ClassicQuest>>();
            questsNeeding = new Dictionary<int, List<ClassicQuest>>();
            foreach (var q in Quests.Values.OrderBy(x => x.Id))
            {
                foreach (var i in q.RewardItems.Concat(q.ChoiceItems).Distinct()) Push(questsRewarding, i, q);
                foreach (var i in q.ObjectiveItems.Distinct()) Push(questsNeeding, i, q);
            }

            var rows = new List<SearchRow>(Items.Count + Npcs.Count + Objects.Count + Quests.Count);
            void R(string name, ClassicEntityKind k, int id) { if (!string.IsNullOrEmpty(name)) rows.Add(new SearchRow { Name = name, Lower = name.ToLowerInvariant(), Kind = k, Id = id }); }
            foreach (var x in Items.Values) R(x.Name, ClassicEntityKind.Item, x.Id);
            foreach (var x in Npcs.Values) R(x.Name, ClassicEntityKind.Npc, x.Id);
            foreach (var x in Objects.Values) R(x.Name, ClassicEntityKind.Object, x.Id);
            foreach (var x in Quests.Values) R(x.Title, ClassicEntityKind.Quest, x.Id);
            search = rows.ToArray();
        }

        static void Push(Dictionary<int, List<ClassicQuest>> d, int item, ClassicQuest q)
        {
            if (!d.TryGetValue(item, out var l)) d[item] = l = new List<ClassicQuest>();
            l.Add(q);
        }

        // ---- queries (every result carries Source = "Classic baseline")

        // Where an item comes from: loot (chance % per npc / object, quest-only flag), vendors, quest rewards / choices / objectives.
        public IReadOnlyList<ClassicItemSource> SourcesOf(int itemId) => ItemSources.TryGetValue(itemId, out var l) ? (IReadOnlyList<ClassicItemSource>)l : NoSources;

        public ClassicItemInfo ItemInfo(int itemId) => Items.TryGetValue(itemId, out var i) ? i : null;
        public ClassicNpc NpcInfo(int npcId) => Npcs.TryGetValue(npcId, out var n) ? n : null;
        public ClassicObject ObjectInfo(int objectId) => Objects.TryGetValue(objectId, out var o) ? o : null;
        public ClassicQuest QuestInfo(int questId) => Quests.TryGetValue(questId, out var q) ? q : null;

        public IReadOnlyList<ClassicSpawn> Spawns(int npcId) => Npcs.TryGetValue(npcId, out var n) ? n.Spawns : new ClassicSpawn[0];
        public IReadOnlyList<ClassicSpawn> ObjectSpawns(int objectId) => Objects.TryGetValue(objectId, out var o) ? o.Spawns : new ClassicSpawn[0];

        public IReadOnlyList<ClassicVendorItem> VendorItems(int npcId) => vendors != null && vendors.TryGetValue(npcId, out var v) ? (IReadOnlyList<ClassicVendorItem>)v : new ClassicVendorItem[0];

        // What a source gives (best chance first): an npc's drops, skinning, pickpocketing, an object's loot, a vendor's stock. Scans all sources (a few ms), not meant for a loop.
        public IReadOnlyList<ClassicLootEntry> LootOf(ClassicSourceKind kind, int sourceId)
        {
            var res = new List<ClassicLootEntry>();
            foreach (var kv in ItemSources)
                foreach (var s in kv.Value)
                    if (s.Kind == kind && s.SourceId == sourceId) res.Add(new ClassicLootEntry { ItemId = kv.Key, Source = s });
            res.Sort((a, b) => { int c = b.Source.Chance.CompareTo(a.Source.Chance); return c != 0 ? c : a.ItemId.CompareTo(b.ItemId); });
            return res;
        }

        public IReadOnlyList<ClassicQuest> QuestsRewarding(int itemId) => questsRewarding != null && questsRewarding.TryGetValue(itemId, out var l) ? (IReadOnlyList<ClassicQuest>)l : new ClassicQuest[0];
        public IReadOnlyList<ClassicQuest> QuestsNeeding(int itemId) => questsNeeding != null && questsNeeding.TryGetValue(itemId, out var l) ? (IReadOnlyList<ClassicQuest>)l : new ClassicQuest[0];

        // Case-insensitive search over item, npc, object and quest names: exact match first, then prefix, word start, anywhere. kinds = null for all.
        public List<ClassicSearchHit> Search(string text, int max = 50, params ClassicEntityKind[] kinds)
        {
            var res = new List<ClassicSearchHit>();
            text = (text ?? "").Trim().ToLowerInvariant();
            if (text.Length == 0 || search == null) return res;
            var hits = new List<KeyValuePair<long, int>>();   // (rank, index)
            for (int i = 0; i < search.Length; i++)
            {
                var r = search[i];
                if (kinds != null && kinds.Length > 0 && Array.IndexOf(kinds, r.Kind) < 0) continue;
                int at = r.Lower.IndexOf(text, StringComparison.Ordinal);
                if (at < 0) continue;
                int rank = r.Lower.Length == text.Length ? 0 : at == 0 ? 1 : r.Lower[at - 1] == ' ' ? 2 : 3;
                hits.Add(new KeyValuePair<long, int>(((long)rank << 40) | ((long)r.Lower.Length << 20) | (uint)i, i));
            }
            hits.Sort((a, b) => a.Key.CompareTo(b.Key));
            foreach (var h in hits.Take(Math.Max(1, max)))
            {
                var r = search[h.Value];
                res.Add(new ClassicSearchHit { Kind = r.Kind, Id = r.Id, Name = r.Name, Detail = DetailOf(r.Kind, r.Id) });
            }
            return res;
        }

        string DetailOf(ClassicEntityKind k, int id)
        {
            switch (k)
            {
                case ClassicEntityKind.Npc:
                    if (Npcs.TryGetValue(id, out var n))
                        return "level " + (n.MinLevel == n.MaxLevel ? n.MinLevel.ToString() : n.MinLevel + "-" + n.MaxLevel) + (n.RankName.Length > 0 ? " " + n.RankName : "");
                    break;
                case ClassicEntityKind.Quest:
                    if (Quests.TryGetValue(id, out var q)) return "quest level " + q.QuestLevel;
                    break;
                case ClassicEntityKind.Item:
                    if (Items.TryGetValue(id, out var it)) return "item level " + it.ItemLevel;
                    break;
            }
            return "";
        }

        // ---- counts for reports
        public int SourceRows => ItemSources.Values.Sum(l => l.Count);

        // ---- file format
        public void Save(string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = path + ".part";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var gz = new GZipStream(fs, CompressionLevel.Optimal))
            using (var w = new BinaryWriter(gz, new UTF8Encoding(false)))
                Write(w);
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        public static ClassicIndex Load(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var gz = new GZipStream(fs, CompressionMode.Decompress))
                using (var r = new BinaryReader(gz, new UTF8Encoding(false)))
                    return Read(r);
            }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is EndOfStreamException || e is UnauthorizedAccessException || e is OverflowException || e is FormatException)
            {
                Util.Log("Atlas Classic: could not read the index (" + e.Message + ")");
                return null;
            }
        }

        // format = FormatVersion; an older one only for the self-test (a file an earlier Outpost wrote)
        public void Write(BinaryWriter w, int format = FormatVersion)
        {
            w.Write(Magic);
            V(w, format);
            var p = Provenance;
            Str(w, p.Repo); Str(w, p.DumpName); Str(w, p.CommitSha); Str(w, p.CommitDate); V(w, p.UpdatesApplied); Str(w, p.BuiltUtc);

            V(w, Items.Count);
            foreach (var i in Items.Values.OrderBy(x => x.Id)) { V(w, i.Id); Str(w, i.Name); V(w, i.Quality); V(w, i.ItemLevel); V(w, i.RequiredLevel); }

            V(w, Npcs.Count);
            foreach (var n in Npcs.Values.OrderBy(x => x.Id))
            {
                V(w, n.Id); Str(w, n.Name); Str(w, n.SubName); V(w, n.MinLevel); V(w, n.MaxLevel); V(w, n.Rank); V(w, n.Type); V(w, n.Family); V(w, n.FactionId); V(w, n.NpcFlags);
                Spawns(w, n.Spawns);
            }

            V(w, Objects.Count);
            foreach (var o in Objects.Values.OrderBy(x => x.Id)) { V(w, o.Id); Str(w, o.Name); V(w, o.Type); V(w, o.LootId); if (format >= 2) V(w, o.LockId); Spawns(w, o.Spawns); }

            V(w, Quests.Count);
            foreach (var q in Quests.Values.OrderBy(x => x.Id))
            {
                V(w, q.Id); Str(w, q.Title); V(w, q.MinLevel); V(w, q.QuestLevel); V(w, q.ZoneOrSort); V(w, q.RequiredRaces); V(w, q.RequiredClasses);
                Pairs(w, q.RewardItems, q.RewardCounts); Pairs(w, q.ChoiceItems, q.ChoiceCounts); Pairs(w, q.ObjectiveItems, q.ObjectiveCounts);
                V(w, q.Givers.Length);
                foreach (var g in q.Givers) { w.Write((byte)(g.IsObject ? 1 : 0)); V(w, g.Id); }
                if (format < 2) continue;
                V(w, q.Enders.Length);
                foreach (var g in q.Enders) { w.Write((byte)(g.IsObject ? 1 : 0)); V(w, g.Id); }
                V(w, q.PrevQuestId); V(w, q.NextQuestId); V(w, q.ExclusiveGroup); V(w, q.NextQuestInChain); V(w, q.BreadcrumbFor);
                V(w, q.RequiredSkill); V(w, q.RequiredSkillValue); V(w, q.RequiredMinRepFaction); V(w, q.RequiredMinRepValue); V(w, q.RequiredMaxRepFaction); V(w, q.RequiredMaxRepValue); V(w, q.SourceItem);
            }

            V(w, ItemSources.Count);
            foreach (var kv in ItemSources.OrderBy(x => x.Key))
            {
                V(w, kv.Key); V(w, kv.Value.Count);
                foreach (var s in kv.Value)
                {
                    w.Write((byte)s.Kind); V(w, s.SourceId); w.Write((float)s.Chance); w.Write((byte)(s.QuestOnly ? 1 : 0));
                    V(w, s.MinCount); V(w, s.MaxCount); V(w, s.RestockSeconds);
                }
            }
            w.Write(Magic);   // end marker: a truncated file is rejected
        }

        public static ClassicIndex Read(BinaryReader r)
        {
            var m = r.ReadBytes(4);
            if (m.Length != 4 || m[0] != Magic[0] || m[1] != Magic[1] || m[2] != Magic[2] || m[3] != Magic[3]) throw new InvalidDataException("not a Classic index");
            int ver = RV(r);
            if (ver < OldestReadable || ver > FormatVersion) throw new InvalidDataException("index format " + ver + " (this app reads " + OldestReadable + " to " + FormatVersion + ")");
            var x = new ClassicIndex();
            var p = x.Provenance;
            p.Repo = RStr(r); p.DumpName = RStr(r); p.CommitSha = RStr(r); p.CommitDate = RStr(r); p.UpdatesApplied = RV(r); p.BuiltUtc = RStr(r); p.FormatVersion = ver;

            int n = Count(r);
            for (int i = 0; i < n; i++) { var it = new ClassicItemInfo { Id = RV(r), Name = RStr(r), Quality = RV(r), ItemLevel = RV(r), RequiredLevel = RV(r) }; x.Items[it.Id] = it; }

            n = Count(r);
            for (int i = 0; i < n; i++)
            {
                var c = new ClassicNpc { Id = RV(r), Name = RStr(r), SubName = RStr(r), MinLevel = RV(r), MaxLevel = RV(r), Rank = RV(r), Type = RV(r), Family = RV(r), FactionId = RV(r), NpcFlags = RV(r) };
                c.Spawns = RSpawns(r);
                x.Npcs[c.Id] = c;
            }

            n = Count(r);
            for (int i = 0; i < n; i++)
            {
                var o = new ClassicObject { Id = RV(r), Name = RStr(r), Type = RV(r), LootId = RV(r) };
                if (ver >= 2) o.LockId = RV(r);
                o.Spawns = RSpawns(r);
                x.Objects[o.Id] = o;
            }

            n = Count(r);
            for (int i = 0; i < n; i++)
            {
                var q = new ClassicQuest { Id = RV(r), Title = RStr(r), MinLevel = RV(r), QuestLevel = RV(r), ZoneOrSort = RV(r), RequiredRaces = RV(r), RequiredClasses = RV(r) };
                RPairs(r, out q.RewardItems, out q.RewardCounts); RPairs(r, out q.ChoiceItems, out q.ChoiceCounts); RPairs(r, out q.ObjectiveItems, out q.ObjectiveCounts);
                int g = Count(r);
                q.Givers = new ClassicQuestGiver[g];
                for (int k = 0; k < g; k++) q.Givers[k] = new ClassicQuestGiver { IsObject = r.ReadByte() == 1, Id = RV(r) };
                if (ver >= 2)
                {
                    g = Count(r);
                    q.Enders = new ClassicQuestGiver[g];
                    for (int k = 0; k < g; k++) q.Enders[k] = new ClassicQuestGiver { IsObject = r.ReadByte() == 1, Id = RV(r) };
                    q.PrevQuestId = RV(r); q.NextQuestId = RV(r); q.ExclusiveGroup = RV(r); q.NextQuestInChain = RV(r); q.BreadcrumbFor = RV(r);
                    q.RequiredSkill = RV(r); q.RequiredSkillValue = RV(r); q.RequiredMinRepFaction = RV(r); q.RequiredMinRepValue = RV(r); q.RequiredMaxRepFaction = RV(r); q.RequiredMaxRepValue = RV(r); q.SourceItem = RV(r);
                }
                x.Quests[q.Id] = q;
            }

            n = Count(r);
            for (int i = 0; i < n; i++)
            {
                int item = RV(r), cnt = Count(r);
                var l = new List<ClassicItemSource>(cnt);
                for (int k = 0; k < cnt; k++)
                    l.Add(new ClassicItemSource { Kind = (ClassicSourceKind)r.ReadByte(), SourceId = RV(r), Chance = r.ReadSingle(), QuestOnly = r.ReadByte() == 1, MinCount = RV(r), MaxCount = RV(r), RestockSeconds = RV(r) });
                x.ItemSources[item] = l;
            }
            var end = r.ReadBytes(4);
            if (end.Length != 4 || end[0] != Magic[0] || end[3] != Magic[3]) throw new InvalidDataException("truncated index");
            x.Finish();
            return x;
        }

        // ---- primitives: unsigned varint with zigzag for signed values, strings as (length + 1) + UTF-8 (0 = null)
        static void V(BinaryWriter w, int v) { uint z = (uint)((v << 1) ^ (v >> 31)); while (z >= 0x80) { w.Write((byte)(z | 0x80)); z >>= 7; } w.Write((byte)z); }
        static int RV(BinaryReader r)
        {
            uint z = 0; int shift = 0;
            while (true)
            {
                byte b = r.ReadByte();
                z |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;
                shift += 7;
                if (shift > 35) throw new InvalidDataException("bad varint");
            }
            return (int)(z >> 1) ^ -(int)(z & 1);
        }
        static int Count(BinaryReader r) { int n = RV(r); if (n < 0 || n > 50000000) throw new InvalidDataException("bad count"); return n; }
        static void Str(BinaryWriter w, string s)
        {
            if (s == null) { V(w, 0); return; }
            var b = Encoding.UTF8.GetBytes(s);
            V(w, b.Length + 1); w.Write(b);
        }
        static string RStr(BinaryReader r)
        {
            int n = RV(r);
            if (n == 0) return null;
            if (n < 0 || n > 1 << 24) throw new InvalidDataException("bad string");
            var b = r.ReadBytes(n - 1);
            if (b.Length != n - 1) throw new EndOfStreamException();
            return Encoding.UTF8.GetString(b);
        }
        static void Spawns(BinaryWriter w, ClassicSpawn[] a)
        {
            V(w, a.Length);
            foreach (var s in a) { V(w, s.Map); w.Write(s.X); w.Write(s.Y); w.Write(s.Z); V(w, s.RespawnSeconds); }
        }
        static ClassicSpawn[] RSpawns(BinaryReader r)
        {
            int n = Count(r);
            var a = new ClassicSpawn[n];
            for (int i = 0; i < n; i++) a[i] = new ClassicSpawn { Map = RV(r), X = r.ReadSingle(), Y = r.ReadSingle(), Z = r.ReadSingle(), RespawnSeconds = RV(r) };
            return a;
        }
        static void Pairs(BinaryWriter w, int[] ids, int[] counts)
        {
            V(w, ids.Length);
            for (int i = 0; i < ids.Length; i++) { V(w, ids[i]); V(w, i < counts.Length ? counts[i] : 1); }
        }
        static void RPairs(BinaryReader r, out int[] ids, out int[] counts)
        {
            int n = Count(r);
            ids = new int[n]; counts = new int[n];
            for (int i = 0; i < n; i++) { ids[i] = RV(r); counts[i] = RV(r); }
        }
    }
}
