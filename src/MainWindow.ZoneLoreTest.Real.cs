using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Atlas.Classic;
using ElansAddonHub.Services.Maps;

namespace ElansAddonHub
{
    // --selftest "zonelorereal": the zone notes against the player's own data, read-only. The maps come from the WoW install (CASC, into the scratch
    // folder), the Classic baseline from the Outpost's data folder (index.bin, only read). Every notable creature with the source "classic" must be a
    // creature of the baseline, and its spawn points must put it in its zone (ZoneLoreCheck) unless it is marked "unchecked" (then the check must not
    // say it is elsewhere). The verdict of every creature is written to zonelore-check.json in the scratch folder: that file is how the shipped notes
    // were cleaned (a creature the baseline puts elsewhere dropped, one it cannot place kept as unchecked). Pictures of the real pages with
    // ELANSHUB_SHOTS=1.
    public partial class MainWindow
    {
        async Task ZoneLoreRealChecks(string dir, string work, CheckFn Check, Func<string, string> P, List<string> notes)
        {
            var real = Environment.GetEnvironmentVariable("ELANSHUB_WOW");
            if (string.IsNullOrEmpty(real)) real = WowLocator.Find();
            if (string.IsNullOrEmpty(real) || !Directory.Exists(Path.Combine(real, "Data", "data")) || !Directory.Exists(Path.Combine(real, "_classic_beta_")))
            {
                Check("real data: skipped (no WoW install with the Forever beta client on this PC)", true);
                return;
            }
            var classicIdx = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ElansAddonHub", "atlas", "classic", "index.bin");
            ClassicIndex ci = null; try { if (File.Exists(classicIdx)) ci = ClassicIndex.Load(classicIdx); } catch (Exception) { }
            if (ci == null) { Check("real data: skipped (no Classic baseline on this PC)", true); return; }
            var savedIcon = IconStore.WowRoot;
            try
            {
                MapData.ResetForTest();
                MapData.WowRoot = () => real; MapData.Enabled = () => true; MapData.CacheFile = Path.Combine(work, "cache", "mapdata.bin");
                MapData.Start();
                await WaitFor(() => MapData.Status == MapData.State.Ready || MapData.Status == MapData.State.Unavailable, 120000);
                Check("real data: the maps are read (" + MapData.StatusLine + ") and the Classic baseline is loaded (" + ci.Npcs.Count + " creatures)", MapData.Ready && ci.Npcs.Count > 1000);
                if (!MapData.Ready) return;
                var idx = MapData.Index;
                ZoneLore.ResetForTest();
                var all = ZoneLore.All;
                Check("real data: every zone of the notes is a map of this game (" + all.Count + " zones; the alias " + string.Join(", ", ZoneLore.Aliases.Select(a => a.Key + " -> " + a.Value)) + " too)",
                    all.All(e => idx.Get(e.ZoneId) != null) && ZoneLore.Aliases.All(a => idx.Get(a.Key) != null), string.Join(", ", all.Where(e => idx.Get(e.ZoneId) == null).Select(e => e.ZoneId)));
                var classic = all.SelectMany(e => e.Mobs.Where(m => m.Source == "classic").Select(m => new { e, m })).ToList();
                var missing = classic.Where(x => ci.NpcInfo(x.m.Id) == null).ToList();
                Check("real data: every 'classic' creature of the notes is a creature of the Classic baseline (" + classic.Count + ")", missing.Count == 0, string.Join(", ", missing.Select(x => x.e.Name + ": " + x.m)));
                var named = classic.Where(x => ci.NpcInfo(x.m.Id) != null && !string.Equals(ci.NpcInfo(x.m.Id).Name, x.m.Name, StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var x in named) notes.Add("note real data: " + x.e.Name + ": the notes say '" + x.m.Name + "', the baseline names " + x.m.Id + " '" + ci.NpcInfo(x.m.Id).Name + "'");

                var results = ZoneLoreCheck.CheckAll(all, ci, idx);
                var rows = results.Select(r => new Dictionary<string, object> { ["zone"] = (double)r.Zone, ["zoneName"] = idx.Get(r.Zone)?.Name ?? "", ["id"] = (double)r.Mob.Id, ["name"] = r.Mob.Name, ["verdict"] = r.Verdict.ToString(), ["detail"] = r.Detail, ["unchecked"] = r.Mob.Unchecked }).Cast<object>().ToList();
                File.WriteAllText(P("zonelore-check.json"), Json.Write(rows));
                foreach (var r in results.Where(r => r.Verdict != ZoneLoreVerdict.Here)) notes.Add("note real data: " + (idx.Get(r.Zone)?.Name ?? r.Zone.ToString()) + ": " + r.Mob.Name + " (" + r.Mob.Id + ") " + r.Verdict + " - " + r.Detail + (r.Mob.Unchecked ? " [unchecked]" : ""));
                var wrong = results.Where(r => !r.Mob.Unchecked && r.Verdict != ZoneLoreVerdict.Here).ToList();
                var badUnchecked = results.Where(r => r.Mob.Unchecked && r.Verdict == ZoneLoreVerdict.Elsewhere).ToList();
                Check("real data: every checked creature has a Classic spawn point in its zone (" + results.Count(r => r.Verdict == ZoneLoreVerdict.Here) + " of " + results.Count + "; the verdicts are in zonelore-check.json)", wrong.Count == 0, string.Join(" | ", wrong.Take(12)));
                Check("real data: no 'unchecked' creature is one the baseline clearly puts in another zone", badUnchecked.Count == 0, string.Join(" | ", badUnchecked));

                // the real pages (pictures)
                ClassicAtlas.Use(ci);
                IconStore.WowRoot = () => real; IconStore.ForgetMemory();
                AtlasQuery.Context.Lodge = () => null;
                MapRender.TileSourceForTest = null; MapRender.ForgetFailuresForTest();
                Width = 1400; Height = 1100;
                ShowTab("atlas"); AtlasView.History.Clear();
                foreach (var z in new[] { 1411, 1429, 2521 })
                {
                    await AtlasView.Open("zone:" + z); await AtlasView.WaitForTest(); UpdateLayout();
                    AtlasView.SelectTab(AtlasTabs.Overview, false); await Task.Delay(200); UpdateLayout();
                    var card = AtlasView.LoreCardForTest;
                    Check("real data: zone " + z + " (" + idx.Get(z)?.Name + ") shows its notes on the Overview", card != null && card.IsVisible && AtlasTexts(card).Contains("About " + idx.Get(z)?.Name));
                    await ShotWait(600);
                    Snapshot(P("zonelore-real-" + z + ".png"));
                    if (card != null) SnapshotSheet(card, P("zonelore-real-" + z + "-card.png"));
                }
            }
            finally { IconStore.WowRoot = savedIcon; ZoneLore.ResetForTest(); }
        }

        Task<string> ZoneLoreRealTest(string dir) => RealSection("zonelorereal", dir, (d, work, Check, P, notes) => ZoneLoreRealChecks(d, work, Check, P, notes));
    }
}
