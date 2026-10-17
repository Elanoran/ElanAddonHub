using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Atlas.Classic;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Professions;

namespace ElansAddonHub
{
    // --selftest "atlashiddenreal" (2.93): the test / placeholder rule (AtlasHidden) on the player's own game files, read-only. Lists EVERYTHING the rule
    // hides (hidden-audit.txt in the scratch folder, and the notes), checks that real names stay (Zzarc' Vul, Test of Lore ...), and writes the evidence on
    // the skill lines in doubt (Comprehension, Survival). Skips without a WoW install with the Forever client.
    public partial class MainWindow
    {
        Task<string> AtlasHiddenRealTest(string dir) => RealSection("atlashiddenreal", dir, (d, work, Check, P, notes) => AtlasHiddenRealChecks(work, Check, notes));

        async Task AtlasHiddenRealChecks(string work, CheckFn Check, List<string> notes)
        {
            var real = Environment.GetEnvironmentVariable("ELANSHUB_WOW");
            if (string.IsNullOrEmpty(real)) real = WowLocator.Find();
            if (string.IsNullOrEmpty(real) || !Directory.Exists(Path.Combine(real, "Data", "data")) || !Directory.Exists(Path.Combine(real, "_classic_beta_")))
            {
                notes.Add("skip real game files: no WoW install with the Forever beta client on this PC");
                return;
            }
            await Task.Yield();
            var sset = SpellParse.All(SpellData.ReadCasc(real, "_classic_beta_"));
            var hard = sset.Problems.Where(p => p.Value.StartsWith("game updated")).ToList();
            if (hard.Count > 0 || sset.Spells == null) { notes.Add("skip real game files: " + string.Join("; ", hard.Select(p => p.Value)) + " (this Outpost does not know that game build's tables; nothing was guessed)"); return; }
            var cset = ClassParse.All(ClassData.ReadCasc(real, "_classic_beta_"));
            var rawI = ItemData.ReadCasc(real, "_classic_beta_");
            var iset = ItemData.ParseTables(rawI.Sparse, rawI.Item, rawI.Budget);
            if (!iset.Ok) { notes.Add("skip real game files: item table not readable (" + iset.Problem + ")"); return; }
            ItemData.ReadCacheRows(iset, ItemData.AdbFile(real, "_classic_beta_"));
            ItemData.SetForTest(new ItemSnapshot(iset.Client, iset.Cache), ItemData.State.Ready);
            SpellData.SetForTest(sset); ClassData.SetForTest(cset);
            var wdb = SeenData.WdbDir(real, "_classic_beta_");
            var seen = wdb != null && File.Exists(Path.Combine(wdb, "creaturecache.wdb")) ? SeenData.ReadFiles(wdb) : null;
            if (seen != null && seen.Ok) SeenData.SetForTest(new SeenSnapshot(seen.Npcs, seen.Objects, seen.Quests)); else SeenData.SetForTest(null, SeenData.State.NotStarted);
            ClassicAtlas.Use(null);
            AtlasHidden.ForgetForTest(); AtlasBrowse.ForgetForTest();
            var sn = sset.Spells;
            var fx = sset.Details?.Effects;
            var sb = new StringBuilder();
            sb.AppendLine("What the Atlas hides as test / placeholder data (AtlasHidden), real game files, build " + (SpellData.Build ?? "?"));

            // ---- everything the rule hides, by kind
            int total = 0;
            void List(string kind, IEnumerable<KeyValuePair<int, string>> named)
            {
                var hid = named.Where(kv => AtlasHidden.IsHiddenName(kv.Value)).OrderBy(kv => kv.Key).ToList();
                total += hid.Count;
                sb.AppendLine().AppendLine("== " + kind + ": " + hid.Count + " hidden");
                foreach (var kv in hid) sb.AppendLine("  " + kv.Key + "  " + kv.Value + "   (" + AtlasHidden.WhyName(kv.Value) + ")");
                notes.Add("hidden " + kind + ": " + hid.Count + (hid.Count > 0 ? " - e.g. " + string.Join(", ", hid.Take(4).Select(kv => kv.Value)) : ""));
            }
            List("items", iset.Client.Select(i => new KeyValuePair<int, string>(i.Id, i.Name)));
            List("spells", sn.Spells.Values.Select(s => new KeyValuePair<int, string>(s.Id, s.Name)));
            List("skill lines (by name)", sn.Skills.Values.Select(s => new KeyValuePair<int, string>(s.Id, s.Name)));
            if (seen != null && seen.Ok)
            {
                List("creatures (cache)", seen.Npcs.Select(n => new KeyValuePair<int, string>(n.Id, n.Name)));
                List("objects (cache)", seen.Objects.Select(o => new KeyValuePair<int, string>(o.Id, o.Name)));
                List("quests (cache)", seen.Quests.Select(q => new KeyValuePair<int, string>(q.Id, q.Title)));
            }
            var unlearn = sn.Skills.Values.Where(s => sn.Recipes.Any(r => r.SkillId == s.Id) && AtlasHidden.WhySkill(s) == "nobody can learn it").OrderBy(s => s.Id).ToList();
            sb.AppendLine().AppendLine("== skill lines nobody can learn (with recipes): " + unlearn.Count);
            foreach (var s in unlearn) sb.AppendLine("  " + s.Id + "  " + s.Name + "  (category " + s.Category + ", " + sn.Recipes.Count(r => r.SkillId == s.Id) + " recipes)");
            notes.Add("hidden skill lines nobody can learn: " + unlearn.Count + (unlearn.Count > 0 ? " - " + string.Join(", ", unlearn.Select(s => s.Name + " (" + s.Id + ")")) : ""));

            // ---- the names a careless rule would take: they must stay
            var keep = new[] { "Test of Lore", "Test of Faith", "Test of Endurance", "Zzarc' Vul", "Testament", "Old Blanchy" };
            var allNames = iset.Client.Select(i => i.Name).Concat(sn.Spells.Values.Select(s => s.Name)).Concat(seen != null && seen.Ok ? seen.Npcs.Select(n => n.Name).Concat(seen.Quests.Select(q => q.Title)).Concat(seen.Objects.Select(o => o.Name)) : Enumerable.Empty<string>()).Where(n => !string.IsNullOrEmpty(n)).Distinct().ToList();
            var keptReal = allNames.Where(n => keep.Any(k => n.StartsWith(k, StringComparison.Ordinal))).ToList();
            Check("real: real names that look like test words stay (" + string.Join(", ", keptReal.Take(6)) + ")", keptReal.All(n => !AtlasHidden.IsHiddenName(n)), string.Join(", ", keptReal.Where(AtlasHidden.IsHiddenName)));
            var startsTest = allNames.Where(n => n.StartsWith("Test", StringComparison.OrdinalIgnoreCase)).OrderBy(n => n).ToList();
            sb.AppendLine().AppendLine("== every name starting with \"Test\" (" + startsTest.Count + "): kept / HIDDEN");
            foreach (var n in startsTest) sb.AppendLine("  " + (AtlasHidden.IsHiddenName(n) ? "HIDDEN " : "kept   ") + n);
            var candidates = new Regex(@"\[|\(old\)|^zz|unused|deprecated|placeholder|\bdnt\b|\bnyi\b|\bQA\b|\bdummy\b|\bdebug\b|\btest\b", RegexOptions.IgnoreCase);
            var looks = allNames.Where(n => candidates.IsMatch(n) && !AtlasHidden.IsHiddenName(n)).OrderBy(n => n).ToList();
            sb.AppendLine().AppendLine("== names that look like test words but are KEPT (" + looks.Count + ")");
            foreach (var n in looks) sb.AppendLine("  " + n);
            Check("real: the rule finds the developer leftovers (Test Profession [DNT] and 50+ more), the browse lists stay large", total > 50 && sn.Skills.Values.Any(s => s.Name == "Test Profession [DNT]" && AtlasHidden.IsHiddenSkill(s)), total + "");

            // ---- the lines in doubt: Comprehension (Bundle of Scrolls), Survival (Bright Campfire)
            foreach (var name in new[] { "Comprehension", "Survival", "Test Profession [DNT]" })
            {
                foreach (var s in sn.Skills.Values.Where(x => x.Name == name))
                {
                    sb.AppendLine().AppendLine("== evidence: " + name + " (skill line " + s.Id + ")");
                    sb.AppendLine("  SkillLine: category " + s.Category + ", parent " + s.ParentId + ", flags 0x" + s.Flags.ToString("X") + ", canLink " + s.CanLink + ", description \"" + s.Description + "\"");
                    var rows = cset.Data?.SkillRows.Where(r => r.Skill == s.Id).ToList() ?? new List<SkillClassRow>();
                    sb.AppendLine("  SkillRaceClassInfo rows: " + rows.Count + (rows.Count > 0 ? " - " + string.Join("; ", rows.Select(r => "classes 0x" + r.ClassMask.ToString("X") + " races 0x" + r.Races.ToString("X") + " flags 0x" + r.Flags.ToString("X") + " availability " + r.Availability + " minLevel " + r.MinLevel)) : ""));
                    foreach (var a in sn.AbilitiesOf(s.Id))
                    {
                        var sp = sn.Get(a.Spell);
                        sb.AppendLine("  ability " + a.Id + ": spell " + a.Spell + " \"" + sp?.Name + "\" acquire " + a.Acquire + " minRank " + a.MinRank + " classes 0x" + a.ClassMask.ToString("X") + " races 0x" + a.RaceMask.ToString("X") + " flags 0x" + a.Flags.ToString("X") + " recipe " + (sn.RecipeOf(a.Spell) != null));
                        if (sp != null) foreach (var e in sp.Effects) sb.AppendLine("     effect " + e.Effect + " trigger " + e.Trigger + " item " + e.ItemType + " points " + e.Points);
                    }
                    foreach (var r in sn.Recipes.Where(r => r.SkillId == s.Id))
                    {
                        var teach = (fx ?? Enumerable.Empty<KeyValuePair<int, int[]>>()).Where(kv => { var a = kv.Value; for (int i = 0; i + 1 < a.Length; i += 2) { if (a[i] == 6 && a[i + 1] == r.SpellId) return true; if (sn.Spells.TryGetValue(a[i + 1], out var tsp) && tsp.Effects.Any(e => e.Effect == 36 && e.Trigger == r.SpellId)) return true; } return false; }).Select(kv => kv.Key).ToList();
                        sb.AppendLine("  recipe spell " + r.SpellId + " makes item " + r.ItemId + " \"" + ItemData.Get(r.ItemId)?.Name + "\" (source " + ItemData.Get(r.ItemId)?.Source + ") teaching items: " + (teach.Count == 0 ? "none" : string.Join(", ", teach.Select(t => t + " " + ItemData.Get(t)?.Name))));
                    }
                    // which spells grant this skill (effect 118 skill / 44 skill step with misc = the skill id)
                    var grants = sn.Spells.Values.Where(sp => sp.Effects.Any(e => (e.Effect == 118 || e.Effect == 44) && e.Misc == s.Id)).ToList();
                    sb.AppendLine("  spells that grant the skill (effect 44 / 118): " + (grants.Count == 0 ? "none" : string.Join(", ", grants.Select(g => g.Id + " " + g.Name))));
                    var reqItems = iset.Client.Where(i => i.RequiredSkill == s.Id).ToList();
                    sb.AppendLine("  items that need the skill: " + (reqItems.Count == 0 ? "none" : string.Join(", ", reqItems.Take(10).Select(i => i.Id + " " + i.Name))));
                    sb.AppendLine("  AtlasHidden: " + (AtlasHidden.WhySkill(s).Length > 0 ? "HIDDEN (" + AtlasHidden.WhySkill(s) + ")" : "kept"));
                    notes.Add("evidence " + name + " (" + s.Id + "): category " + s.Category + ", " + rows.Count + " race/class rows, " + sn.AbilitiesOf(s.Id).Count + " abilities, grants " + grants.Count + " -> " + (AtlasHidden.WhySkill(s).Length > 0 ? "hidden" : "kept"));
                }
            }

            // ---- the lists as the Atlas shows them
            AtlasBrowse.ForgetForTest();
            var rec = AtlasBrowse.Run("browse:recipes");
            Check("real: the Recipes list has no Test Profession chip and no hidden recipe; its count is the listed rows", !rec.Children.Any(c => c.Title.Contains("[DNT]")) && rec.Rows.All(r => !AtlasHidden.IsHiddenName(r.Title)), string.Join(",", rec.Children.Select(c => c.Title)));
            var book = ProfessionBook.Build(sn, ItemData.Get, fx);
            Check("real: the Professions list has no hidden line", book.Professions.All(p => !AtlasHidden.IsHiddenSkill(p.Skill)), string.Join(",", book.Professions.Select(p => p.Name)));
            var srch = AtlasQuery.Search("test profession");
            Check("real: search leaves the test profession out", !srch.All.Any(h => h.Name.Contains("[DNT]")), string.Join(",", srch.All.Select(h => h.Name)));
            // the evidence decided (2.93): Survival 142 is the old pre-release line (no ability that comes with it, no teaching item, its one recipe makes
            // no item, and no trainer of the Classic baseline teaches it) - hidden; Comprehension 3012 is the Mage's scroll skill (an ability that comes
            // with the line, Forever's own spell ids, 10+ scrolls that need it) - kept
            var surv = sn.Skills.TryGetValue(142, out var s142) ? s142 : null; var comp = sn.Skills.TryGetValue(3012, out var s3012) ? s3012 : null;
            Check("real: Survival (142, Bright Campfire) is hidden as nobody can learn it; Comprehension (3012, Bundle of Scrolls) stays and is in the Recipes chips",
                (surv == null || AtlasHidden.WhySkill(surv) == AtlasHidden.NobodyLearns) && (comp == null || (AtlasHidden.WhySkill(comp) == "" && rec.Children.Any(c => c.Title == "Comprehension")))
                && !rec.Children.Any(c => c.Title == "Survival"), (surv == null ? "-" : AtlasHidden.WhySkill(surv)) + " | " + (comp == null ? "-" : AtlasHidden.WhySkill(comp)));
            var items = AtlasBrowse.Run("browse:items");
            Check("real: the items list stays large and has no hidden name (" + items.Total + " items)", items.Total > 10000 && items.Rows.All(r => !AtlasHidden.IsHiddenName(r.Title)), items.Total + "");
            File.WriteAllText(Path.Combine(work, "hidden-audit.txt"), sb.ToString());
            notes.Add("the full list: " + Path.Combine(work, "hidden-audit.txt"));

            // the Data sources page on the real game data (no Lodge, no Cairn, no baseline in this section): a picture only, never committed
            if (SelfTestClock.ShotsOn)
            {
                ShowTab("atlas");
                AtlasView.SourcesFactsOverrideForTest = null;
                await AtlasView.Open(AtlasNav.DataId); await AtlasView.WaitForTest(); await Task.Delay(300);
                await ShotWait(400); Snapshot(Path.Combine(work, "datasources-real.png"));
                ShowTab("addons");
            }
        }
    }
}
