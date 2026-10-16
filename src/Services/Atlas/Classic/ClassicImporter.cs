using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace ElansAddonHub.Services.Atlas.Classic
{
    public sealed class ClassicProgress
    {
        public string Phase;        // "Reading the dump", "Applying updates", "Resolving loot", "Writing the index"
        public double Fraction;     // 0..1 over the whole import
    }

    public sealed class ClassicImportReport
    {
        public string DumpFile;
        public int? ContentVersion;
        public int UpdateFiles, UpdateFilesApplied, UpdateFilesAlreadyInDump;
        public long DumpStatements, DumpStatementsSkippedUnwanted, DumpStatementsApplied, DumpStatementsFailed;
        public long UpdateStatementsApplied, UpdateStatementsSkipped;
        public readonly SortedDictionary<string, int> RowsAfterDump = new SortedDictionary<string, int>();
        public readonly SortedDictionary<string, int> RowsFinal = new SortedDictionary<string, int>();
        public readonly SortedDictionary<string, int> SkipReasons = new SortedDictionary<string, int>();
        public readonly List<string> SkipSamples = new List<string>();
        public readonly List<string> Notes = new List<string>();
        public int Items, Npcs, Objects, Quests, NpcSpawns, ObjectSpawns, VendorNpcs, ItemsWithSource, ItemRowsWithSource;
        public readonly SortedDictionary<string, int> SourceRowsByKind = new SortedDictionary<string, int>();
        public readonly SortedDictionary<string, int> ItemsByKind = new SortedDictionary<string, int>();
        public int ReferenceCycles;
        public double Seconds;
        public long PeakWorkingSetMb, ManagedMb;
        public long IndexBytes;
        public bool Cancelled;

        public string ToText()
        {
            var sb = new StringBuilder();
            sb.AppendLine("dump: " + DumpFile + (ContentVersion != null ? " (content " + ContentVersion + ")" : ""));
            sb.AppendLine("statements: dump " + DumpStatements + " (" + DumpStatementsSkippedUnwanted + " not needed), updates applied " + UpdateStatementsApplied + ", skipped " + UpdateStatementsSkipped + " (dump failures " + DumpStatementsFailed + ")");
            sb.AppendLine("update files: " + UpdateFiles + " found, " + UpdateFilesApplied + " applied, " + UpdateFilesAlreadyInDump + " already in the dump");
            sb.AppendLine("rows (dump -> after updates):");
            foreach (var kv in RowsAfterDump) sb.AppendLine("  " + kv.Key + ": " + kv.Value + " -> " + (RowsFinal.TryGetValue(kv.Key, out var f) ? f : 0));
            sb.AppendLine("index: items " + Items + ", npcs " + Npcs + " (" + NpcSpawns + " spawns), objects " + Objects + " (" + ObjectSpawns + " spawns), quests " + Quests + ", vendors " + VendorNpcs);
            sb.AppendLine("items with at least one source: " + ItemsWithSource + " of " + Items + "; source rows " + ItemRowsWithSource);
            foreach (var kv in SourceRowsByKind) sb.AppendLine("  " + kv.Key + ": " + kv.Value + " rows, " + (ItemsByKind.TryGetValue(kv.Key, out var n) ? n : 0) + " items");
            sb.AppendLine("time " + Seconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s, peak working set " + PeakWorkingSetMb + " MB, managed " + ManagedMb + " MB, index " + IndexBytes + " bytes");
            if (SkipReasons.Count > 0)
            {
                sb.AppendLine("skipped update statements by reason:");
                foreach (var kv in SkipReasons.OrderByDescending(x => x.Value).Take(15)) sb.AppendLine("  " + kv.Value + "x " + kv.Key);
            }
            return sb.ToString();
        }
    }

    // Dump (gzip SQL, streamed) + update scripts -> ClassicIndex. Nothing is executed: the SQL is only read as text by ClassicSqlExec.
    public static class ClassicImporter
    {
        static readonly Regex Leading = new Regex(@"^(\d+)", RegexOptions.CultureInvariant);

        // Full import from the downloaded files: dump = .sql.gz, updatesDir = the *.sql update scripts (may be null), writes indexPath.
        public static ClassicImportReport Import(string dumpGz, string updatesDir, string indexPath, ClassicProvenance provenance, IProgress<ClassicProgress> progress, CancellationToken ct)
        {
            var rep = new ClassicImportReport { DumpFile = Path.GetFileName(dumpGz) };
            var sw = Stopwatch.StartNew();
            var exec = new ClassicSqlExec();
            long total = new FileInfo(dumpGz).Length;
            using (var fs = new FileStream(dumpGz, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var gz = new GZipStream(fs, CompressionMode.Decompress))
            using (var sr = new StreamReader(gz, new UTF8Encoding(false), false, 1 << 18))
            {
                Run(sr, exec, "dump", ct, () => progress?.Report(new ClassicProgress { Phase = "Reading the dump", Fraction = 0.5 * Math.Min(1.0, (double)fs.Position / Math.Max(1, total)) }), rep, true);
            }
            if (ct.IsCancellationRequested) { rep.Cancelled = true; return rep; }
            rep.ContentVersion = exec.ContentVersion;
            foreach (var kv in exec.Tables) rep.RowsAfterDump[kv.Key] = kv.Value.Rows.Count;

            var files = new List<KeyValuePair<string, Func<TextReader>>>();
            if (updatesDir != null && Directory.Exists(updatesDir))
                foreach (var f in Directory.GetFiles(updatesDir, "*.sql"))
                {
                    var path = f;
                    files.Add(new KeyValuePair<string, Func<TextReader>>(Path.GetFileName(f), () => new StreamReader(path, new UTF8Encoding(false), true, 1 << 16)));
                }
            ApplyUpdates(exec, files, rep, progress, ct, 0.5, 0.8);
            if (ct.IsCancellationRequested) { rep.Cancelled = true; return rep; }

            progress?.Report(new ClassicProgress { Phase = "Resolving loot", Fraction = 0.8 });
            var idx = BuildIndex(exec, rep, provenance);
            progress?.Report(new ClassicProgress { Phase = "Writing the index", Fraction = 0.95 });
            idx.Save(indexPath);
            rep.IndexBytes = new FileInfo(indexPath).Length;
            Finish(rep, sw);
            progress?.Report(new ClassicProgress { Phase = "Done", Fraction = 1 });
            return rep;
        }

        // For tests and tools: dump and updates as text.
        public static ClassicIndex BuildFromText(string dumpSql, IList<KeyValuePair<string, string>> updates, ClassicImportReport rep = null, ClassicProvenance provenance = null)
        {
            rep = rep ?? new ClassicImportReport();
            var exec = new ClassicSqlExec();
            using (var sr = new StringReader(dumpSql)) Run(sr, exec, "dump", CancellationToken.None, null, rep, true);
            rep.ContentVersion = exec.ContentVersion;
            foreach (var kv in exec.Tables) rep.RowsAfterDump[kv.Key] = kv.Value.Rows.Count;
            var files = (updates ?? new List<KeyValuePair<string, string>>()).Select(u => { var text = u.Value; return new KeyValuePair<string, Func<TextReader>>(u.Key, () => new StringReader(text)); }).ToList();
            ApplyUpdates(exec, files, rep, null, CancellationToken.None, 0, 1);
            return BuildIndex(exec, rep, provenance);
        }

        static void Run(TextReader tr, ClassicSqlExec exec, string origin, CancellationToken ct, Action tick, ClassicImportReport rep, bool isDump)
        {
            var rd = new ClassicSqlReader(tr);
            long n = 0;
            while (rd.Next(ClassicSqlExec.WantHead, out var stmt, out var skipped))
            {
                if (skipped) continue;
                exec.Execute(stmt, origin);
                if ((++n & 0x3FF) == 0)
                {
                    if (ct.IsCancellationRequested) return;
                    tick?.Invoke();
                }
            }
            if (isDump)
            {
                rep.DumpStatements = rd.Statements; rep.DumpStatementsSkippedUnwanted = rd.Skipped;
                rep.DumpStatementsApplied = exec.Applied; rep.DumpStatementsFailed = exec.Skipped;
            }
        }

        // update scripts: numbered files above the dump's content version, in number order (ties by name), each in its own variable scope
        static void ApplyUpdates(ClassicSqlExec exec, List<KeyValuePair<string, Func<TextReader>>> files, ClassicImportReport rep, IProgress<ClassicProgress> progress, CancellationToken ct, double from, double to)
        {
            rep.UpdateFiles = files.Count;
            var ordered = files.Select(f => new { f, num = Leading.Match(f.Key) is Match m && m.Success ? long.Parse(m.Groups[1].Value) : -1L })
                .OrderBy(x => x.num).ThenBy(x => x.f.Key, StringComparer.OrdinalIgnoreCase).ToList();
            long baseApplied = exec.Applied, baseSkipped = exec.Skipped;
            // statistics of the dump pass are kept apart: reasons recorded so far belong to the dump
            foreach (var s in exec.SkipSamples.Take(10)) rep.Notes.Add("dump: " + s);
            exec.SkipReasons.Clear(); exec.SkipSamples.Clear();
            int done = 0;
            foreach (var u in ordered)
            {
                if (ct.IsCancellationRequested) return;
                done++;
                if (u.num < 0) { rep.Notes.Add("update file without a number ignored: " + u.f.Key); continue; }
                if (exec.ContentVersion != null && u.num <= exec.ContentVersion) { rep.UpdateFilesAlreadyInDump++; continue; }
                exec.ResetVars();
                try
                {
                    using (var tr = u.f.Value()) Run(tr, exec, u.f.Key, ct, null, rep, false);
                }
                catch (IOException e) { rep.Notes.Add("could not read " + u.f.Key + ": " + e.Message); continue; }
                rep.UpdateFilesApplied++;
                progress?.Report(new ClassicProgress { Phase = "Applying updates " + done + "/" + ordered.Count, Fraction = from + (to - from) * done / Math.Max(1, ordered.Count) });
            }
            rep.UpdateStatementsApplied = exec.Applied - baseApplied;
            rep.UpdateStatementsSkipped = exec.Skipped - baseSkipped;
            foreach (var kv in exec.SkipReasons) rep.SkipReasons[kv.Key] = kv.Value;
            rep.SkipSamples.AddRange(exec.SkipSamples);
            rep.Notes.AddRange(exec.Notes);
            foreach (var kv in exec.Tables) rep.RowsFinal[kv.Key] = kv.Value.Rows.Count;
        }

        static void Finish(ClassicImportReport rep, Stopwatch sw)
        {
            rep.Seconds = sw.Elapsed.TotalSeconds;
            try { rep.PeakWorkingSetMb = Process.GetCurrentProcess().PeakWorkingSet64 / (1024 * 1024); } catch { }
            rep.ManagedMb = GC.GetTotalMemory(false) / (1024 * 1024);
        }

        // ---- tables -> index
        static int I(object[] r, int i) => (int)SqlVal.ToLong(r[i]);
        static string S(object[] r, int i) => SqlVal.ToStr(r[i]);

        static ClassicIndex BuildIndex(ClassicSqlExec exec, ClassicImportReport rep, ClassicProvenance prov)
        {
            var T = exec.Tables;
            var idx = new ClassicIndex();
            if (prov != null) idx.Provenance = prov;
            idx.Provenance.BuiltUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            idx.Provenance.FormatVersion = ClassicIndex.FormatVersion;
            idx.Provenance.UpdatesApplied = rep.UpdateFilesApplied;

            // items
            {
                var t = T["item_template"];
                int e = t.Idx["entry"], n = t.Idx["name"], q = t.Idx["quality"], l = t.Idx["itemlevel"], rl = t.Idx["requiredlevel"];
                foreach (var r in t.Rows) { int id = I(r, e); idx.Items[id] = new ClassicItemInfo { Id = id, Name = S(r, n), Quality = I(r, q), ItemLevel = I(r, l), RequiredLevel = I(r, rl) }; }
            }

            // spawn entries: guid -> extra entries
            var cSpawnEntries = SpawnEntries(T["creature_spawn_entry"]);
            var oSpawnEntries = SpawnEntries(T["gameobject_spawn_entry"]);

            // npcs
            var vendorTemplate = new Dictionary<int, int>();
            var lootOf = new Dictionary<int, int[]>();   // npc -> { loot, skin, pickpocket }
            {
                var t = T["creature_template"];
                int e = t.Idx["entry"], n = t.Idx["name"], sn = t.Idx["subname"], mn = t.Idx["minlevel"], mx = t.Idx["maxlevel"], f = t.Idx["faction"], fam = t.Idx["family"],
                    ty = t.Idx["creaturetype"], fl = t.Idx["npcflags"], rk = t.Idx["rank"], lo = t.Idx["lootid"], pp = t.Idx["pickpocketlootid"], sk = t.Idx["skinninglootid"], vt = t.Idx["vendortemplateid"];
                foreach (var r in t.Rows)
                {
                    int id = I(r, e);
                    idx.Npcs[id] = new ClassicNpc { Id = id, Name = S(r, n), SubName = S(r, sn), MinLevel = I(r, mn), MaxLevel = I(r, mx), FactionId = I(r, f), Family = I(r, fam), Type = I(r, ty), NpcFlags = I(r, fl), Rank = I(r, rk) };
                    lootOf[id] = new[] { I(r, lo), I(r, sk), I(r, pp) };
                    if (I(r, vt) > 0) vendorTemplate[id] = I(r, vt);
                }
            }
            {
                var t = T["creature"];
                var byNpc = new Dictionary<int, List<ClassicSpawn>>();
                int g = t.Idx["guid"], id = t.Idx["id"], m = t.Idx["map"], x = t.Idx["position_x"], y = t.Idx["position_y"], z = t.Idx["position_z"], rs = t.Idx["spawntimesecsmin"];
                foreach (var r in t.Rows)
                {
                    var sp = new ClassicSpawn { Map = I(r, m), X = (float)SqlVal.ToDouble(r[x]), Y = (float)SqlVal.ToDouble(r[y]), Z = (float)SqlVal.ToDouble(r[z]), RespawnSeconds = I(r, rs) };
                    foreach (var npc in EntriesOf(I(r, id), I(r, g), cSpawnEntries))
                    {
                        if (!byNpc.TryGetValue(npc, out var l)) byNpc[npc] = l = new List<ClassicSpawn>();
                        l.Add(sp);
                    }
                }
                foreach (var kv in byNpc) if (idx.Npcs.TryGetValue(kv.Key, out var n)) { n.Spawns = kv.Value.ToArray(); rep.NpcSpawns += n.Spawns.Length; }
            }

            // objects
            {
                var t = T["gameobject_template"];
                int e = t.Idx["entry"], ty = t.Idx["type"], n = t.Idx["name"], d0 = t.Idx["data0"], d1 = t.Idx["data1"];
                foreach (var r in t.Rows)
                {
                    int id = I(r, e), type = I(r, ty);
                    idx.Objects[id] = new ClassicObject { Id = id, Name = S(r, n), Type = type, LootId = (type == 3 || type == 25) ? I(r, d1) : 0, LockId = (type == 2 || type == 3 || type == 10) ? Math.Max(0, I(r, d0)) : 0 };
                }
                var st = T["gameobject"];
                var byObj = new Dictionary<int, List<ClassicSpawn>>();
                int g = st.Idx["guid"], id2 = st.Idx["id"], m = st.Idx["map"], x = st.Idx["position_x"], y = st.Idx["position_y"], z = st.Idx["position_z"], rs = st.Idx["spawntimesecsmin"];
                foreach (var r in st.Rows)
                {
                    var sp = new ClassicSpawn { Map = I(r, m), X = (float)SqlVal.ToDouble(r[x]), Y = (float)SqlVal.ToDouble(r[y]), Z = (float)SqlVal.ToDouble(r[z]), RespawnSeconds = I(r, rs) };
                    foreach (var o in EntriesOf(I(r, id2), I(r, g), oSpawnEntries))
                    {
                        if (!byObj.TryGetValue(o, out var l)) byObj[o] = l = new List<ClassicSpawn>();
                        l.Add(sp);
                    }
                }
                foreach (var kv in byObj) if (idx.Objects.TryGetValue(kv.Key, out var o)) { o.Spawns = kv.Value.ToArray(); rep.ObjectSpawns += o.Spawns.Length; }
            }

            // loot
            var refStore = ClassicLoot.Store.From(T["reference_loot_template"]);
            var resolver = new ClassicLoot.Resolver(refStore);
            // most loot ids belong to one npc only: keep results of the shared ones (many npcs of a kind share a table), not of every npc
            var uses = new Dictionary<string, int>();
            foreach (var l in lootOf.Values)
                for (int k = 0; k < 3; k++)
                    if (l[k] > 0) { var key = k + ":" + l[k]; uses[key] = uses.TryGetValue(key, out var u) ? u + 1 : 1; }
            var cache = new Dictionary<string, Dictionary<int, ClassicLoot.Result>>();
            Dictionary<int, ClassicLoot.Result> Cached(string kind, ClassicLoot.Store store, int entry)
            {
                var key = kind + ":" + entry;
                if (cache.TryGetValue(key, out var res)) return res;
                res = resolver.Resolve(store, entry);
                if (uses.TryGetValue(key, out var n) && n > 1) cache[key] = res;
                return res;
            }
            void AddLoot(ClassicSourceKind kind, int sourceId, Dictionary<int, ClassicLoot.Result> res)
            {
                foreach (var kv in res)
                {
                    double pct = Math.Round(kv.Value.P * 100.0, 4);
                    if (pct <= 0) continue;
                    idx.Add(kv.Key, new ClassicItemSource
                    {
                        Kind = kind, SourceId = sourceId, Chance = pct, QuestOnly = kv.Value.QuestOnly,
                        MinCount = kv.Value.Min == int.MaxValue ? 1 : kv.Value.Min, MaxCount = Math.Max(kv.Value.Max, 1)
                    });
                }
            }
            {
                var cStore = ClassicLoot.Store.From(T["creature_loot_template"]);
                var sStore = ClassicLoot.Store.From(T["skinning_loot_template"]);
                var pStore = ClassicLoot.Store.From(T["pickpocketing_loot_template"]);
                var oStore = ClassicLoot.Store.From(T["gameobject_loot_template"]);
                foreach (var kv in lootOf.OrderBy(x => x.Key))
                {
                    if (kv.Value[0] > 0) AddLoot(ClassicSourceKind.NpcDrop, kv.Key, Cached("0", cStore, kv.Value[0]));
                    if (kv.Value[1] > 0) AddLoot(ClassicSourceKind.NpcSkin, kv.Key, Cached("1", sStore, kv.Value[1]));
                    if (kv.Value[2] > 0) AddLoot(ClassicSourceKind.NpcPickpocket, kv.Key, Cached("2", pStore, kv.Value[2]));
                }
                foreach (var o in idx.Objects.Values.OrderBy(x => x.Id))
                    if (o.LootId > 0) AddLoot(ClassicSourceKind.ObjectLoot, o.Id, Cached("o", oStore, o.LootId));
            }
            rep.ReferenceCycles = resolver.ReferenceCycles;

            // vendors
            {
                var direct = VendorRows(T["npc_vendor"]);
                var templ = VendorRows(T["npc_vendor_template"]);
                var vendorNpcs = new HashSet<int>();
                foreach (var npc in idx.Npcs.Keys.OrderBy(x => x))
                {
                    var items = new Dictionary<int, int[]>();
                    if (direct.TryGetValue(npc, out var d)) foreach (var v in d) items[v[0]] = v;
                    if (vendorTemplate.TryGetValue(npc, out var tid) && templ.TryGetValue(tid, out var tl)) foreach (var v in tl) if (!items.ContainsKey(v[0])) items[v[0]] = v;
                    foreach (var v in items.Values)
                    {
                        idx.Add(v[0], new ClassicItemSource { Kind = ClassicSourceKind.Vendor, SourceId = npc, MaxCount = v[1], MinCount = 1, RestockSeconds = v[2] });
                        vendorNpcs.Add(npc);
                    }
                }
                rep.VendorNpcs = vendorNpcs.Count;
            }

            // quests
            {
                var t = T["quest_template"];
                var giversN = Givers(T["creature_questrelation"], false);
                var giversO = Givers(T["gameobject_questrelation"], true);
                var endersN = Givers(T["creature_involvedrelation"], false);
                var endersO = Givers(T["gameobject_involvedrelation"], true);
                int Ix(string c) => t.Idx[c];
                foreach (var r in t.Rows)
                {
                    int id = I(r, Ix("entry"));
                    var q = new ClassicQuest
                    {
                        Id = id, Title = S(r, Ix("title")), MinLevel = I(r, Ix("minlevel")), QuestLevel = I(r, Ix("questlevel")), ZoneOrSort = I(r, Ix("zoneorsort")),
                        RequiredRaces = I(r, Ix("requiredraces")), RequiredClasses = I(r, Ix("requiredclasses")),
                        PrevQuestId = I(r, Ix("prevquestid")), NextQuestId = I(r, Ix("nextquestid")), ExclusiveGroup = I(r, Ix("exclusivegroup")),
                        NextQuestInChain = I(r, Ix("nextquestinchain")), BreadcrumbFor = I(r, Ix("breadcrumbforquestid")),
                        RequiredSkill = I(r, Ix("requiredskill")), RequiredSkillValue = I(r, Ix("requiredskillvalue")),
                        RequiredMinRepFaction = I(r, Ix("requiredminrepfaction")), RequiredMinRepValue = I(r, Ix("requiredminrepvalue")),
                        RequiredMaxRepFaction = I(r, Ix("requiredmaxrepfaction")), RequiredMaxRepValue = I(r, Ix("requiredmaxrepvalue")),
                        SourceItem = Math.Max(0, I(r, Ix("srcitemid"))),
                    };
                    Items(r, t, "rewitemid", "rewitemcount", 4, out q.RewardItems, out q.RewardCounts);
                    Items(r, t, "rewchoiceitemid", "rewchoiceitemcount", 6, out q.ChoiceItems, out q.ChoiceCounts);
                    Items(r, t, "reqitemid", "reqitemcount", 4, out q.ObjectiveItems, out q.ObjectiveCounts);
                    var gl = new List<ClassicQuestGiver>();
                    if (giversN.TryGetValue(id, out var gn)) gl.AddRange(gn);
                    if (giversO.TryGetValue(id, out var go)) gl.AddRange(go);
                    q.Givers = gl.ToArray();
                    var el = new List<ClassicQuestGiver>();
                    if (endersN.TryGetValue(id, out var en)) el.AddRange(en);
                    if (endersO.TryGetValue(id, out var eo)) el.AddRange(eo);
                    q.Enders = el.ToArray();
                    idx.Quests[id] = q;
                    for (int k = 0; k < q.RewardItems.Length; k++) idx.Add(q.RewardItems[k], new ClassicItemSource { Kind = ClassicSourceKind.QuestReward, SourceId = id, MinCount = q.RewardCounts[k], MaxCount = q.RewardCounts[k] });
                    for (int k = 0; k < q.ChoiceItems.Length; k++) idx.Add(q.ChoiceItems[k], new ClassicItemSource { Kind = ClassicSourceKind.QuestChoice, SourceId = id, MinCount = q.ChoiceCounts[k], MaxCount = q.ChoiceCounts[k] });
                    for (int k = 0; k < q.ObjectiveItems.Length; k++) idx.Add(q.ObjectiveItems[k], new ClassicItemSource { Kind = ClassicSourceKind.QuestObjective, SourceId = id, MinCount = q.ObjectiveCounts[k], MaxCount = q.ObjectiveCounts[k] });
                }
            }

            idx.Finish();

            rep.Items = idx.Items.Count; rep.Npcs = idx.Npcs.Count; rep.Objects = idx.Objects.Count; rep.Quests = idx.Quests.Count;
            rep.ItemsWithSource = idx.ItemSources.Count; rep.ItemRowsWithSource = idx.SourceRows;
            foreach (ClassicSourceKind k in Enum.GetValues(typeof(ClassicSourceKind)))
            {
                int rows = 0, items = 0;
                foreach (var l in idx.ItemSources.Values)
                {
                    int c = l.Count(s => s.Kind == k);
                    rows += c; if (c > 0) items++;
                }
                rep.SourceRowsByKind[k.ToString()] = rows; rep.ItemsByKind[k.ToString()] = items;
            }
            return idx;
        }

        static Dictionary<int, List<int>> SpawnEntries(ClassicSqlTable t)
        {
            var d = new Dictionary<int, List<int>>();
            int g = t.Idx["guid"], e = t.Idx["entry"];
            foreach (var r in t.Rows)
            {
                int guid = I(r, g);
                if (!d.TryGetValue(guid, out var l)) d[guid] = l = new List<int>();
                l.Add(I(r, e));
            }
            return d;
        }

        // the npcs / objects a spawn row stands for: its own id plus the entries of a shared (multi-entry) spawn
        static IEnumerable<int> EntriesOf(int id, int guid, Dictionary<int, List<int>> extra)
        {
            if (id > 0) yield return id;
            if (extra.TryGetValue(guid, out var l)) foreach (var e in l) if (e > 0 && e != id) yield return e;
        }

        // npc -> items: { item, maxcount, incrtime }
        static Dictionary<int, List<int[]>> VendorRows(ClassicSqlTable t)
        {
            var d = new Dictionary<int, List<int[]>>();
            int e = t.Idx["entry"], it = t.Idx["item"], m = t.Idx["maxcount"], inc = t.Idx["incrtime"];
            foreach (var r in t.Rows)
            {
                int npc = I(r, e), item = I(r, it);
                if (item <= 0) continue;
                if (!d.TryGetValue(npc, out var l)) d[npc] = l = new List<int[]>();
                l.Add(new[] { item, I(r, m), I(r, inc) });
            }
            return d;
        }

        static Dictionary<int, List<ClassicQuestGiver>> Givers(ClassicSqlTable t, bool isObject)
        {
            var d = new Dictionary<int, List<ClassicQuestGiver>>();
            int id = t.Idx["id"], q = t.Idx["quest"];
            foreach (var r in t.Rows)
            {
                int quest = I(r, q);
                if (!d.TryGetValue(quest, out var l)) d[quest] = l = new List<ClassicQuestGiver>();
                l.Add(new ClassicQuestGiver { IsObject = isObject, Id = I(r, id) });
            }
            return d;
        }

        static void Items(object[] r, ClassicSqlTable t, string idCol, string countCol, int n, out int[] ids, out int[] counts)
        {
            var a = new List<int>(); var c = new List<int>();
            for (int i = 1; i <= n; i++)
            {
                int item = I(r, t.Idx[idCol + i]);
                if (item <= 0) continue;
                a.Add(item);
                c.Add(Math.Max(1, I(r, t.Idx[countCol + i])));
            }
            ids = a.ToArray(); counts = c.ToArray();
        }
    }
}
