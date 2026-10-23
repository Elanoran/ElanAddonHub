using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Atlas.Classic;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Maps;

namespace ElansAddonHub
{
    // --selftest "coldstart": how long the Outpost takes to get its game data ready, against the player's real WoW install (read-only; skipped when this PC
    // has no Forever beta client, like the other real sections), in an empty cache folder of the scratch dir (never the user's caches). The Classic baseline of
    // the real data folder is loaded read-only when there is one (the Zones list's levels need it, as on the player's PC).
    //   cold   - no saved copies: time until Atlas Home is drawn, the Zones list has its zones, the zones have their levels, every reader is ready; the peak
    //            working set while it runs; the strip shows, every reader reads the game files.
    //   warm   - the copies saved by the cold run: the same, much tighter; every reader loads its saved copy and the strip never shows.
    //   patch  - the game build changes (ItemData.BuildForTest): the previous build's data is on the page at once (no waiting page), the strip says the new
    //            files are read in the background, the readers end with the new build; a page whose data then changes offers "Updated with the new game data".
    // Budgets (measured on the dev PC, 2026-10-16, Forever beta 1.60.1.70425, Classic baseline loaded: cold zones list 1.3 s, levels 1.8 s, all 1.2 s,
    // peak +0.7 GB; before 2.70 the Zones list took 66 s and the process peaked at 2.5 GB) with headroom for a slower disk / CPU:
    public partial class MainWindow
    {
        public const int ColdZonesBudgetMs = 8000, ColdLevelsBudgetMs = 20000, ColdAllBudgetMs = 15000, ColdHomeBudgetMs = 4000;
        public const long ColdPeakBudgetMb = 1100;
        public const int WarmZonesBudgetMs = 2000, WarmAllBudgetMs = 2000, WarmHomeBudgetMs = 1000;
        public const long WarmPeakBudgetMb = 350;
        public const int PatchZonesBudgetMs = 2500, PatchAllBudgetMs = 20000;
        public const int UiStallBudgetMs = 1000;

        sealed class ColdRun
        {
            public long Home = -1, Zones = -1, Levels = -1, All = -1, PeakMb, StallMs;
            public bool SawWait, SawStrip, SawBackground, SawZonesWithoutWait;
            public string ZonesAtFirst = "", Readers = "", Reason = "", Diag = "", Stalls = "";
        }

        async Task ColdStartChecks(string dir, string work, CheckFn Check, Func<string, string> P, List<string> notes)
        {
            var real = Environment.GetEnvironmentVariable("ELANSHUB_WOW");
            if (string.IsNullOrEmpty(real)) real = WowLocator.Find();
            if (string.IsNullOrEmpty(real) || !Directory.Exists(Path.Combine(real, "Data", "data")) || !Directory.Exists(Path.Combine(real, "_classic_beta_")))
            {
                Check("real game files: skipped (no WoW install with the Forever beta client on this PC)", true);
                return;
            }
            var classicIdx = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ElansAddonHub", "atlas", "classic", "index.bin");
            ClassicIndex ci = null; try { if (File.Exists(classicIdx)) ci = ClassicIndex.Load(classicIdx); } catch (Exception) { }
            notes.Add("note Classic baseline: " + (ci != null ? "the real one, read-only (" + ci.Npcs.Count + " creatures)" : "none on this PC: the zones get no levels, the level checks are skipped"));

            // what this section changes, put back in finally (RealSection restores MapData and the Atlas)
            var sItem = ItemData.CacheFile; var sSpell = SpellData.CacheFile; var sClass = ClassData.CacheFile; var sTal = TalentData.CacheFile; var sSeen = SeenData.CacheFile;
            var sIRoot = ItemData.WowRoot; var sIEn = ItemData.Enabled; var sSRoot = SeenData.WowRoot; var sSEn = SeenData.Enabled;
            bool aI = ItemData.AutoStart, aS = SpellData.AutoStart, aC = ClassData.AutoStart, aT = TalentData.AutoStart, aM = MapData.AutoStart, aSe = SeenData.AutoStart;
            var sAreaDir = AreaProbe.CacheDirOverride; var sTiles = AreaProbe.TileSourceForTest;
            var cache = Path.Combine(work, "cache");
            var sBuilds = GameBuilds.FilePath;
            try
            {
                Func<string> root = () => real; Func<bool> on = () => true;
                ItemData.WowRoot = root; ItemData.Enabled = on; MapData.WowRoot = root; MapData.Enabled = on; SeenData.WowRoot = root; SeenData.Enabled = on;
                ItemData.AutoStart = SpellData.AutoStart = ClassData.AutoStart = TalentData.AutoStart = MapData.AutoStart = SeenData.AutoStart = true;
                ItemData.CacheFile = Path.Combine(cache, "itemdata.bin"); SpellData.CacheFile = Path.Combine(cache, "spelldata.bin"); ClassData.CacheFile = Path.Combine(cache, "classdata.bin");
                TalentData.CacheFile = Path.Combine(cache, "talentdata.bin"); MapData.CacheFile = Path.Combine(cache, "mapdata.bin"); SeenData.CacheFile = Path.Combine(cache, "seendata.bin");
                AreaProbe.CacheDirOverride = cache; AreaProbe.TileSourceForTest = null;
                GameBuilds.FilePath = Path.Combine(cache, "gamebuilds.json"); GameBuilds.ResetForTest();
                ClassicAtlas.Use(ci);
                AtlasQuery.Context.Lodge = () => null;
                if (ci != null) AtlasDerived.For(ci);          // the derived lookups are built once per loaded baseline, before any of this (not part of the game data)
                Width = 1400; Height = 1000;

                // ---- cold
                var cold = await ColdMeasure("cold", P, ci != null);
                notes.Add("note cold start: home " + cold.Home + " ms, zones list " + cold.Zones + " ms (" + cold.ZonesAtFirst + "), zone levels " + cold.Levels + " ms, all game data " + cold.All + " ms, peak working set +" + cold.PeakMb + " MB, longest UI stall " + cold.StallMs + " ms; " + cold.Readers);
                Check("cold start: every reader reads the game files (" + cold.Readers + ")", new[] { ItemData.LastLoadFrom, SpellData.LastLoadFrom, ClassData.LastLoadFrom, MapData.LastLoadFrom, TalentData.LastLoadFrom }.All(f => f == "game files"), cold.Readers);
                Check("cold start: Atlas Home is drawn within " + ColdHomeBudgetMs + " ms (" + cold.Home + ")", cold.Home >= 0 && cold.Home <= ColdHomeBudgetMs);
                Check("cold start: the Zones list has its zones within " + ColdZonesBudgetMs + " ms (" + cold.Zones + "; before 2.70: 66 s)", cold.Zones >= 0 && cold.Zones <= ColdZonesBudgetMs);
                if (ci != null) Check("cold start: the zones have their levels within " + ColdLevelsBudgetMs + " ms (" + cold.Levels + ")", cold.Levels >= 0 && cold.Levels <= ColdLevelsBudgetMs);
                Check("cold start: all game data is ready within " + ColdAllBudgetMs + " ms (" + cold.All + ")", cold.All >= 0 && cold.All <= ColdAllBudgetMs);
                Check("cold start: the working set grows by at most " + ColdPeakBudgetMb + " MB (" + cold.PeakMb + "; before 2.70 the process peaked at 2.5 GB)", cold.PeakMb <= ColdPeakBudgetMb);
                notes.Add("note cold start: longest stalls of the window: " + (cold.Stalls.Length > 0 ? cold.Stalls : "none over 250 ms") + " (the first draw of the Atlas lists in this process is part of it: not checked, the warm and patch runs are)");
                Check("cold start: the progress strip showed while the game files were read (reason '" + cold.Reason + "')", cold.SawStrip && cold.Reason == GameDataProgress.ReasonText(GameDataProgress.Why.FirstStart), cold.Diag);
                Check("cold start: the Zones page waits with the build stack, not a bare message (or had its zones before the first look)", cold.SawWait || cold.SawZonesWithoutWait);
                Check("cold start: the copies are saved for the next start", File.Exists(ItemData.CacheFile) && File.Exists(SpellData.CacheFile) && File.Exists(MapData.CacheFile) && File.Exists(ClassData.CacheFile) && File.Exists(TalentData.CacheFile));
                AreaProbe.Flush();
                Check("cold start: the zone borders read for the levels are saved too", ci == null || Directory.GetFiles(cache, "areas-*.bin").Length > 0);

                // ---- warm
                var warm = await ColdMeasure("warm", P, ci != null);
                notes.Add("note warm start: home " + warm.Home + " ms, zones list " + warm.Zones + " ms (" + warm.ZonesAtFirst + "), zone levels " + warm.Levels + " ms, all game data " + warm.All + " ms, peak working set +" + warm.PeakMb + " MB, longest UI stall " + warm.StallMs + " ms; " + warm.Readers);
                Check("warm start: every reader loads its saved copy (" + warm.Readers + ")", new[] { ItemData.LastLoadFrom, SpellData.LastLoadFrom, ClassData.LastLoadFrom, MapData.LastLoadFrom, TalentData.LastLoadFrom }.All(f => f != null && f.StartsWith("cache file", StringComparison.Ordinal)), warm.Readers);
                Check("warm start: Atlas Home within " + WarmHomeBudgetMs + " ms (" + warm.Home + ")", warm.Home >= 0 && warm.Home <= WarmHomeBudgetMs);
                Check("warm start: the Zones list with levels within " + WarmZonesBudgetMs + " ms (" + warm.Zones + " / " + warm.Levels + ")", warm.Zones >= 0 && warm.Zones <= WarmZonesBudgetMs && (ci == null || (warm.Levels >= 0 && warm.Levels <= WarmZonesBudgetMs)));
                Check("warm start: all game data within " + WarmAllBudgetMs + " ms (" + warm.All + ")", warm.All >= 0 && warm.All <= WarmAllBudgetMs);
                Check("warm start: the working set grows by at most " + WarmPeakBudgetMb + " MB (" + warm.PeakMb + ")", warm.PeakMb <= WarmPeakBudgetMb);
                Check("warm start: no progress strip (nothing is read from the game files)", !warm.SawStrip);

                // ---- after a patch: the saved copies are of the "previous" build
                ItemData.BuildForTest = (r, f) => "wow_classic_beta|1.60.1.99999|feedbeef00000000feedbeef00000000";
                var patch = await ColdMeasure("patch", P, ci != null);
                notes.Add("note after a patch: home " + patch.Home + " ms, zones list " + patch.Zones + " ms (" + patch.ZonesAtFirst + "), zone levels " + patch.Levels + " ms, all game data " + patch.All + " ms, peak working set +" + patch.PeakMb + " MB, longest UI stall " + patch.StallMs + " ms; " + patch.Readers);
                Check("after a patch: the Zones list shows the previous build's zones at once, within " + PatchZonesBudgetMs + " ms (" + patch.Zones + ", " + patch.ZonesAtFirst + "), with no waiting page", patch.Zones >= 0 && patch.Zones <= PatchZonesBudgetMs && !patch.SawWait);
                Check("after a patch: the strip says the new files are read in the background ('" + patch.Reason + "')", patch.SawStrip && patch.SawBackground && patch.Reason == GameDataProgress.ReasonText(GameDataProgress.Why.WowUpdated, true), patch.Diag);
                Check("after a patch: every reader ends with the new game files (" + patch.Readers + ") and nothing is marked old any more", new[] { ItemData.LastLoadFrom, SpellData.LastLoadFrom, ClassData.LastLoadFrom, MapData.LastLoadFrom, TalentData.LastLoadFrom }.All(f => f == "game files") && !ItemData.Stale && !SpellData.Stale && !MapData.Stale && !ClassData.Stale && !TalentData.Stale, patch.Readers);
                Check("after a patch: the window stays responsive while the new files are read: no stall longer than " + UiStallBudgetMs + " ms (" + patch.StallMs + ")", patch.StallMs <= UiStallBudgetMs, patch.Stalls);
                Check("warm start: no stall of the window longer than " + UiStallBudgetMs + " ms (" + warm.StallMs + ")", warm.StallMs <= UiStallBudgetMs, warm.Stalls);
                Check("after a patch: all game data within " + PatchAllBudgetMs + " ms (" + patch.All + ")", patch.All >= 0 && patch.All <= PatchAllBudgetMs);
                // the same game files under a new build: every reader compared its saved copy with them (Settings > About > Game patches) and found nothing changed
                var pe = GameBuilds.Find("wow_classic_beta|1.60.1.99999|feedbeef00000000feedbeef00000000");
                var compared = GameBuilds.Kinds.Where(GameBuilds.Compared).ToList();
                Check("after a patch: the game builds history has the new build, compared with the previous one, every kind compared and 0 changes (the same files)",
                    pe != null && pe.From != null && pe.From != pe.Build && compared.All(k => pe.Of(k) != null && pe.Of(k).Total == 0),
                    pe == null ? "no entry" : pe.From + ": " + string.Join(", ", compared.Select(k => k + "=" + (pe.Of(k)?.Total.ToString() ?? "missing"))));

                // ---- new data under an open page: offered, not swapped in under the reader; Show applies it
                var snap = ItemData.Snapshot;
                var item = snap?.Items.Values.Where(i => i.Quality >= 2 && i.ItemLevel > 10 && !string.IsNullOrEmpty(i.Name) && !ItemSnapshot.IsPlaceholder(i.Name)).OrderBy(i => i.Id).FirstOrDefault();
                if (item != null)
                {
                    await AtlasView.Open("item:" + item.Id); await AtlasView.WaitForTest(); UpdateLayout();
                    var before = AtlasView.Model;
                    int offered0 = AtlasView.OfferedUpdatesForTest, silent0 = AtlasView.SilentUpdatesForTest;
                    // the same data again (a reader that finished with nothing new): nothing is offered and nothing is redrawn
                    ItemData.RaiseChangedForTest();
                    await WaitFor(() => AtlasView.SilentUpdatesForTest > silent0 || AtlasView.OfferedUpdatesForTest > offered0, 5000);
                    Check("new data: the same data again leaves the page alone (no notice, not redrawn)", AtlasView.SilentUpdatesForTest > silent0 && !AtlasView.UpdateOfferedForTest && ReferenceEquals(before, AtlasView.Model));
                    // data that changes the page: the notice, the page itself still the old one
                    var changed = new ItemSnapshot(snap.Items.Values.Where(i => i.Id != item.Id).Select(i => i).ToList(), new List<ClientItem>());
                    ItemData.SetForTest(changed); ItemData.RaiseChangedForTest();
                    await WaitFor(() => AtlasView.UpdateOfferedForTest, 5000);
                    Check("new data: a page the new data would change offers 'Updated with the new game data' and is not redrawn under the reader", AtlasView.UpdateOfferedForTest && ReferenceEquals(before, AtlasView.Model));
                    await ShotWait(500);
                    Snapshot(P("coldstart-notice.png"));
                    AtlasView.ShowUpdateForTest();
                    await WaitFor(() => !ReferenceEquals(before, AtlasView.Model), 5000); await AtlasView.WaitForTest();
                    Check("new data: Show draws the new data and the notice goes", !ReferenceEquals(before, AtlasView.Model) && !AtlasView.UpdateOfferedForTest);
                    ItemData.SetForTest(snap); ItemData.RaiseChangedForTest();
                    await AtlasView.Open("browse:zones"); await AtlasView.WaitForTest();
                    Check("new data: going to another page draws the newest data and drops the offer", !AtlasView.UpdateOfferedForTest);
                }
                ItemData.BuildForTest = null;

                // ---- pictures: the strip and a page waiting for its first data (the maps held back a few seconds so they can be seen)
                if (SelfTestClock.ShotsOn)
                {
                    foreach (var f in Directory.GetFiles(cache)) File.Delete(f);
                    ResetGameDataForTest();
                    MapData.HoldMsForTest = 6000;
                    ShowTab("atlas"); AtlasView.History.Clear();
                    GameDataStart.StartAll();
                    await AtlasView.Open("browse:zones");
                    await WaitFor(() => GameDataBar.ShownForTest && AtlasView.WaitCardForTest != null, 5000);
                    await Task.Delay(1200); UpdateLayout();
                    Check("pictures: the zones page waits with the build stack ('" + AtlasView.WaitCardForTest?.LineForTest + "') and the strip shows ('" + GameDataBar.StepForTest + "')", AtlasView.WaitCardForTest != null && GameDataBar.ShownForTest);
                    Snapshot(P("coldstart-waiting.png"));
                    SnapshotSheet(GameDataBar, P("coldstart-strip.png"));
                    MapData.HoldMsForTest = 0;
                    await WaitFor(() => !GameDataProgress.Now().Busy, 60000);
                    await Task.Delay(300); UpdateLayout();
                    Snapshot(P("coldstart-ready.png"));
                }
            }
            finally
            {
                ItemData.BuildForTest = null; MapData.HoldMsForTest = 0;
                ResetGameDataForTest();
                ItemData.CacheFile = sItem; SpellData.CacheFile = sSpell; ClassData.CacheFile = sClass; TalentData.CacheFile = sTal; SeenData.CacheFile = sSeen;
                ItemData.WowRoot = sIRoot; ItemData.Enabled = sIEn; SeenData.WowRoot = sSRoot; SeenData.Enabled = sSEn;
                ItemData.AutoStart = aI; SpellData.AutoStart = aS; ClassData.AutoStart = aC; TalentData.AutoStart = aT; MapData.AutoStart = aM; SeenData.AutoStart = aSe;
                AreaProbe.CacheDirOverride = sAreaDir; AreaProbe.TileSourceForTest = sTiles;
                GameBuilds.FilePath = sBuilds; GameBuilds.ResetForTest();
                ClassicAtlas.Use(null);
            }
        }

        void ResetGameDataForTest()
        {
            ItemData.ResetForTest(); SpellData.ResetForTest(); ClassData.ResetForTest(); TalentData.ResetForTest(); MapData.ResetForTest(); SeenData.ResetForTest();
            ZoneLevels.ResetForTest(); AreaProbe.ResetForTest(); CascShared.CloseIdle(); GameDataProgress.ResetForTest(); AtlasBrowse.ForgetForTest();
        }

        // one start: everything reset (the files in the cache folder stay), Atlas Home, then the game data starts and the Zones list is opened at once
        async Task<ColdRun> ColdMeasure(string label, Func<string, string> P, bool classic)
        {
            var run = new ColdRun();
            // the previous run's readers must be idle before the reset (a reset under a running worker would race it)
            await WaitFor(() => !GameDataProgress.Now().Busy && !ZoneLevels.Running, 60000);
            ResetGameDataForTest();
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var proc = Process.GetCurrentProcess(); proc.Refresh();
            long ws0 = proc.WorkingSet64, peak = ws0;
            var stop = new CancellationTokenSource();
            var sampler = Task.Run(async () => { var p = Process.GetCurrentProcess(); while (!stop.IsCancelled()) { p.Refresh(); peak = Math.Max(peak, p.WorkingSet64); await Task.Delay(50); } });
            long stall = 0; var last = Stopwatch.StartNew();
            var uiTick = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(50) };
            var stalls = new List<string>(); Stopwatch swRef = null; int g2 = GC.CollectionCount(2);
            uiTick.Tick += (s, e) => { long gap = last.ElapsedMilliseconds - 50; if (gap > 250) stalls.Add(gap + " ms at " + (swRef?.ElapsedMilliseconds ?? -1) + " (gen2 GCs so far " + (GC.CollectionCount(2) - g2) + ", " + GameDataProgress.StepText(GameDataProgress.Now().Current) + ")"); stall = Math.Max(stall, gap); last.Restart(); };
            var sw = Stopwatch.StartNew(); swRef = sw;
            try
            {
                ShowTab("atlas"); AtlasView.History.Clear();
                await AtlasView.GoStart(); await AtlasView.StartFillForTest; UpdateLayout();
                run.Home = sw.ElapsedMilliseconds;
                last.Restart(); uiTick.Start();
                GameDataStart.StartAll();
                await AtlasView.Open("browse:zones");
                while (sw.ElapsedMilliseconds < 180000)
                {
                    var v = GameDataProgress.Now();
                    if (v.Busy && v.Rebuilding && run.Diag.Length == 0) run.Diag = "rebuilding seen at " + sw.ElapsedMilliseconds + " ms, strip " + GameDataBar.Visibility + "/" + GameDataBar.ShownForTest + " '" + GameDataBar.StepForTest + "'";
                    if (GameDataBar.ShownForTest && v.Rebuilding && v.Busy && GameDataBar.ReasonForTest.Length > 0) { run.SawStrip = true; run.Reason = GameDataBar.ReasonForTest; if (v.Background) run.SawBackground = true; }
                    if (AtlasView.WaitCardForTest != null) run.SawWait = true;
                    var rows = AtlasView.BrowseListForTest?.Items.OfType<AtlasRow>().ToList() ?? new List<AtlasRow>();
                    if (run.Zones < 0 && rows.Count > 0)
                    {
                        run.Zones = sw.ElapsedMilliseconds; run.SawZonesWithoutWait = !run.SawWait;
                        run.ZonesAtFirst = rows.Count + " zones, " + rows.Count(r => r.Detail.Contains("Levels")) + " with levels";
                    }
                    if (run.Levels < 0 && rows.Count(r => r.Detail.Contains("Levels")) >= Math.Max(1, rows.Count / 2)) run.Levels = sw.ElapsedMilliseconds;
                    bool all = ItemData.Ready && SpellData.Ready && ClassData.Ready && MapData.Ready && TalentData.Ready && SeenData.Status != SeenData.State.Loading && SeenData.Status != SeenData.State.NotStarted
                               && !ItemData.Stale && !SpellData.Stale && !ClassData.Stale && !MapData.Stale && !TalentData.Stale;
                    if (run.All < 0 && all) run.All = sw.ElapsedMilliseconds;
                    if (run.All >= 0 && run.Zones >= 0 && (run.Levels >= 0 || !classic) && !v.Busy && !ZoneLevels.Running) break;
                    await Task.Delay(40);
                }
            }
            finally { uiTick.Stop(); stop.Cancel(); try { await sampler; } catch (Exception) { } }
            run.PeakMb = (peak - ws0) / 1048576; run.StallMs = Math.Max(0, stall); run.Stalls = string.Join("; ", stalls);
            run.Readers = string.Join(", ", new[] { "items " + ItemData.LastLoadMs + " ms (" + ItemData.LastLoadFrom + ")", "spells " + SpellData.LastLoadMs + " ms (" + SpellData.LastLoadFrom + ")", "classes " + ClassData.LastLoadMs + " ms (" + ClassData.LastLoadFrom + ")",
                "maps " + MapData.LastLoadMs + " ms (" + MapData.LastLoadFrom + ")", "talents " + TalentData.LastLoadMs + " ms (" + TalentData.LastLoadFrom + ")", "seen " + SeenData.LastLoadMs + " ms (" + SeenData.LastLoadFrom + ")" });
            Util.Log("coldstart " + label + ": zones " + run.Zones + " ms, levels " + run.Levels + " ms, all " + run.All + " ms, peak +" + run.PeakMb + " MB, stall " + run.StallMs + " ms; " + run.Readers);
            return run;
        }

        Task<string> ColdStartTest(string dir) => RealSection("coldstart", dir, (d, work, Check, P, notes) => ColdStartChecks(d, work, Check, P, notes));
    }

    static class CtsExt { public static bool IsCancelled(this CancellationTokenSource c) => c.IsCancellationRequested; }
}
