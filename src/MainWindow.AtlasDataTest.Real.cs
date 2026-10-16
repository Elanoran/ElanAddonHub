using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Atlas.Classic;
using ElansAddonHub.Services.Db2;

namespace ElansAddonHub
{
    // --selftest "atlasdatareal": the object classes and quest chains on the player's own data, read-only. The items and the Lock table come from the WoW install
    // (CASC), the client's caches from its WDB folder, the Classic baseline is read again from the dump in the Outpost's data folder (only read; the new
    // index goes to the scratch folder, so the locks and chain fields are there even when the data folder still holds a format-1 index). Every object is
    // classified the old way (before 2.77) and the new way; objects-audit.txt in the scratch folder lists every one that moved, with why.
    public partial class MainWindow
    {
        static readonly Regex OldOreName = new Regex(@"\b(vein|deposit|ore|mineral)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly Regex OldChestName = new Regex(@"\b(chest|crate|barrel|box|cache|footlocker|trunk|coffer|strongbox|lockbox|basket|sack|cask|urn)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // the classification before 2.77 (AtlasBrowse.ObjectCat as it was): any herb in the loot made a herb
        static string OldObjectGroup(int id, ClassicIndex ci)
        {
            var seen = SeenData.Object(id); var cl = ci?.ObjectInfo(id);
            int type = seen?.Type ?? cl?.Type ?? 0;
            string name = seen?.Name ?? cl?.Name ?? "";
            var items = new List<int>();
            var dv = AtlasDerived.For(ci);
            if (dv != null) foreach (var le in dv.LootOf(ClassicSourceKind.ObjectLoot, id)) items.Add(le.ItemId);
            bool herb = false, ore = false;
            var need = SeenView.SkillNeedOf(id);
            if (need.HasValue && need.Value.LockType == WorldLookups.LockTypeHerbalism) herb = true;
            else if (need.HasValue && need.Value.LockType == WorldLookups.LockTypeMining) ore = true;
            foreach (var it in items)
            {
                var c = ItemData.Get(it);
                if (c != null && c.ClassId == 7 && c.SubclassId == 9) herb = true;
                else if (c != null && c.ClassId == 7 && c.SubclassId == 7) ore = true;
            }
            if (type == 25) return "fishing";
            if (herb) return "herbs";
            if (ore || OldOreName.IsMatch(name)) return "ore";
            if (type == 3 && (OldChestName.IsMatch(name) || items.Count > 0)) return "chests";
            if (OldChestName.IsMatch(name)) return "chests";
            return "other";
        }

        async Task AtlasDataRealChecks(string dir, string work, CheckFn Check, Func<string, string> P, List<string> notes)
        {
            var real = Environment.GetEnvironmentVariable("ELANSHUB_WOW");
            if (string.IsNullOrEmpty(real)) real = WowLocator.Find();
            if (string.IsNullOrEmpty(real) || !Directory.Exists(Path.Combine(real, "Data", "data")) || !Directory.Exists(Path.Combine(real, "_classic_beta_")))
            {
                Check("real data: skipped (no WoW install with the Forever beta client on this PC)", true);
                return;
            }
            var cdir = Environment.GetEnvironmentVariable("ELANSHUB_CLASSICDB");
            if (string.IsNullOrEmpty(cdir)) cdir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ElansAddonHub", "atlas", "classic");
            var src = ClassicDownloader.ReadSource(cdir);
            var dump = src?.Str("dump") is string dn ? ClassicPaths.DumpFile(dn, cdir) : null;
            if (dump == null || !File.Exists(dump)) { Check("real data: skipped (no Classic dump on this PC)", true); return; }
            try
            {
                // the Classic baseline, read again (format 2) into the scratch folder
                var ipath = Path.Combine(work, "classic-index.bin");
                var rep = await Task.Run(() => ClassicImporter.Import(dump, ClassicPaths.UpdatesDir(cdir), ipath, new ClassicProvenance { DumpName = Path.GetFileName(dump) }, null, CancellationToken.None));
                var ci = ClassicIndex.Load(ipath);
                Check("real data: the Classic dump reads into a format-2 index (" + (ci?.Objects.Count ?? 0) + " objects, " + (ci?.Quests.Count ?? 0) + " quests, " + (ci?.Objects.Values.Count(o => o.LockId > 0) ?? 0) + " objects with a lock, "
                    + (ci?.Quests.Values.Count(q => q.NextQuestInChain > 0 || q.PrevQuestId != 0) ?? 0) + " quests with chain links)",
                    ci != null && ci.Provenance.FormatVersion == ClassicIndex.FormatVersion && ci.Objects.Values.Count(o => o.LockId > 0) > 1000 && ci.Quests.Values.Count(q => q.PrevQuestId != 0) > 500);
                if (ci == null) return;
                // the game: items (for the loot's classes), the Lock table, the client's caches
                var rawI = ItemData.ReadCasc(real, "_classic_beta_");
                var iset = ItemData.ParseTables(rawI.Sparse, rawI.Item, rawI.Budget);
                if (!iset.Ok) { Check("real data: skipped (" + iset.Problem + ")", true); return; }
                ItemData.ReadCacheRows(iset, ItemData.AdbFile(real, "_classic_beta_"));
                ItemData.SetForTest(new ItemSnapshot(iset.Client, iset.Cache), ItemData.State.Ready);
                var sset = SpellParse.All(SpellData.ReadCasc(real, "_classic_beta_"));
                SpellData.SetForTest(sset);
                var wdb = SeenData.WdbDir(real, "_classic_beta_");
                var seen = wdb != null && File.Exists(Path.Combine(wdb, "creaturecache.wdb")) ? SeenData.ReadFiles(wdb) : null;
                if (seen != null && seen.Ok) SeenData.SetForTest(new SeenSnapshot(seen.Npcs, seen.Objects, seen.Quests)); else SeenData.SetForTest(null, SeenData.State.NotStarted);
                ClassicAtlas.Use(ci);
                AtlasQuery.Context.Lodge = () => null;
                AtlasBrowse.ForgetForTest();
                Check("real data: the Lock table is read (" + (SpellData.World?.Locks.Count ?? 0) + " locks with a skill entry)", SpellData.World != null && SpellData.World.Locks.Count > 100);

                // ---- every object, the old way and the new way
                var ids = new SortedSet<int>(ci.Objects.Keys);
                if (SeenData.Snapshot != null) foreach (var k in SeenData.Snapshot.Objects.Keys) ids.Add(k);
                var rows = new List<(int id, string name, string was, ObjectKinds.Verdict now)>();
                await Task.Run(() =>
                {
                    foreach (var id in ids)
                    {
                        var name = AtlasQuery.ObjectName(id);
                        rows.Add((id, name, OldObjectGroup(id, ci), AtlasBrowse.ObjectVerdictOf(id)));
                    }
                });
                var moved = rows.Where(r => r.was != r.now.Kind).ToList();
                var sb = new StringBuilder();
                sb.AppendLine("Objects whose group changed (old -> new), " + moved.Count + " of " + rows.Count + ":");
                foreach (var g in moved.GroupBy(r => r.was + " -> " + r.now.Kind).OrderByDescending(g => g.Count()))
                {
                    sb.AppendLine();
                    sb.AppendLine("== " + g.Key + " (" + g.Count() + ")");
                    foreach (var r in g.OrderBy(r => r.name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.id)) sb.AppendLine("  " + r.id.ToString(CultureInfo.InvariantCulture) + "  " + r.name + "  [" + r.now.Why + "]");
                }
                sb.AppendLine();
                sb.AppendLine("Totals now: " + string.Join(", ", rows.GroupBy(r => r.now.Kind).OrderBy(g => g.Key).Select(g => g.Key + " " + g.Count())));
                sb.AppendLine("Totals before: " + string.Join(", ", rows.GroupBy(r => r.was).OrderBy(g => g.Key).Select(g => g.Key + " " + g.Count())));
                File.WriteAllText(Path.Combine(work, "objects-audit.txt"), sb.ToString(), new UTF8Encoding(false));
                notes.Add("info objects: " + moved.Count + " of " + rows.Count + " moved; " + string.Join(", ", moved.GroupBy(r => r.was + "->" + r.now.Kind).OrderByDescending(g => g.Count()).Select(g => g.Key + " " + g.Count())) + " (objects-audit.txt in " + work + ")");
                string Now(int id) => rows.FirstOrDefault(r => r.id == id).now?.Kind ?? "?";
                Check("real data: the Alliance chests and strongboxes (a herb among their loot) are chests now, and were herbs before",
                    new[] { 3714, 3715, 4095, 4096, 105579 }.All(i => Now(i) == "chests") && rows.Where(r => new[] { 3715, 4096 }.Contains(r.id)).All(r => r.was == "herbs"),
                    string.Join(",", new[] { 3714, 3715, 4095, 4096, 105579 }.Select(i => i + "=" + Now(i))));
                Check("real data: the herbs stay herbs (Peacebloom, Silverleaf, Briarthorn, Black Lotus) and the veins ore (Copper, Tin, Iron)",
                    new[] { 1617, 1618, 1621, 176589 }.All(i => Now(i) == "herbs") && new[] { 1731, 1732, 1735 }.All(i => Now(i) == "ore"),
                    string.Join(",", new[] { 1617, 1618, 1621, 176589, 1731, 1732, 1735 }.Select(i => i + "=" + Now(i))));
                var herbLocked = rows.Where(r => ObjectKinds.LockNeeds(ObjectKinds.LockOf(r.id, out _))?.Any(n => n.LockType == WorldLookups.LockTypeHerbalism && n.Skill > 0) == true).ToList();
                var chestInHerbs = rows.Where(r => r.now.Kind == "herbs" && ObjectKinds.IsChestName(r.name)).ToList();
                Check("real data: every object whose lock needs a Herbalism level is a herb (" + herbLocked.Count + "), and no herb has a container's name",
                    herbLocked.Count > 40 && herbLocked.All(r => r.now.Kind == "herbs") && chestInHerbs.Count == 0, string.Join(", ", chestInHerbs.Take(10).Select(r => r.id + " " + r.name)));
                var lostHerbs = moved.Where(r => r.was == "herbs" && r.now.Kind != "herbs" && ObjectKinds.LockNeeds(ObjectKinds.LockOf(r.id, out _)) == null).ToList();
                notes.Add("info herbs that left Herbs without a known lock (check these by hand): " + (lostHerbs.Count == 0 ? "none" : string.Join(", ", lostHerbs.Take(20).Select(r => r.id + " " + r.name + " [" + r.now.Why + "]"))));

                // ---- quest chains over every quest
                var sw = System.Diagnostics.Stopwatch.StartNew();
                int withChain = 0, longest = 0, cut = 0, longestId = 0; string longestText = "";
                await Task.Run(() =>
                {
                    var t = QuestChains.Live();
                    foreach (var q in ci.Quests.Keys)
                    {
                        var c = QuestChains.For(t, q, SpellData.World?.LineOf(q), AtlasQuery.QuestTitle);
                        if (c.HasChain) withChain++;
                        if (c.Cut) cut++;
                        if (c.Total > longest) { longest = c.Total; longestId = q; longestText = c.Name.Length > 0 ? c.Name : c.Steps[0].Title + " ... " + c.Steps[c.Steps.Count - 1].Title; }
                    }
                });
                notes.Add("info quest chains: " + withChain + " of " + ci.Quests.Count + " quests are in a chain; longest " + longest + " steps (quest " + longestId + ": " + longestText + "); " + cut + " stopped at a loop or the limit; " + sw.ElapsedMilliseconds + " ms for all");
                var north = QuestChains.LiveFor(7);
                Check("real data: Northshire's chain - A Threat Within, Kobold Camp Cleanup (step 2), Investigate Echo Ridge, Skirmish at Echo Ridge, Report to Goldshire",
                    north.Steps.Select(x => x.Id).Take(5).SequenceEqual(new[] { 783, 7, 15, 21, 54 }) && north.Position == 2 && north.Requires.SequenceEqual(new[] { 783 }), string.Join(", ", north.Steps));
                Check("real data: chains are worked out for every quest without a hang (" + withChain + " in a chain, longest " + longest + ", " + sw.ElapsedMilliseconds + " ms)", withChain > 500 && longest <= QuestChains.MaxSteps * 2 + 1 && sw.ElapsedMilliseconds < 60000);
            }
            finally
            {
                ClassicAtlas.Use(null);
                ItemData.SetForTest(null, ItemData.State.Disabled, "off"); SpellData.SetForTest(null, SpellData.State.Disabled); SeenData.SetForTest(null, SeenData.State.Disabled);
                AtlasBrowse.ForgetForTest();
            }
        }

        Task<string> AtlasDataRealTest(string dir) => RealSection("atlasdatareal", dir, (d, work, Check, P, notes) => AtlasDataRealChecks(d, work, Check, P, notes));
    }
}
