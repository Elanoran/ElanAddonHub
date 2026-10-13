using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // --selftest "readers": how the app reads the game's saved files (Services/SavedVarsFiles.cs: the one folder walk, SafeRead and SavedVarsCache; used by
    // GamePresence, WowCalendar, InventoryReader, HealthReader, FlightShare and HubData). Fixture folders under the scratch directory only (never the real
    // WoW folder): one walk finds the same files as the old per-reader walks did (several flavors / accounts), links are skipped, ElansHub.lua is parsed
    // ONCE when all readers ask (parses are counted), parsed again after a change, the last good parse survives a half-written or locked file, and two threads
    // reading at once get consistent results.
    public partial class MainWindow
    {
        static string RdHub(int level) => @"
ElansHubDB = { current = 'Realm-Elan', chars = { ['Realm-Elan'] = { name = 'Elan', realm = 'Realm', class = 'Hunter', classFile = 'HUNTER', race = 'Orc', raceFile = 'Orc', sex = 2, level = " + level + @", updated = 1790000000, zone = 'Orgrimmar' } },
  calendar = { at = 1790000000, realm = 'Realm', region = 3, v = 2, events = { { title = 'Test Day', kind = 'holiday', start = 1790100000, finish = 1790200000 } } } }
ElansHubData = { schema = 1, chars = { ['Realm-Elan'] = { at = 1790000000, mods = { core = { v = 1, at = 1790000000, by = 'ElansHub', ver = '1.0', data = { name = 'Elan', level = " + level + @" } } } } } }
";
        const string RdBags = @"
ElansBagsDB = { chars = { ['Realm-Elan'] = { name = 'Elan', realm = 'Realm', class = 'HUNTER', level = 60, updated = 5, bags = {} } } }
";
        const string RdHunter = @"
ElansHunterHelperDB = { flightLegs = { ['Horde|11>22'] = { t = 20, n = 3, at = 1790000000 } }, flightNodeNames = { ['11'] = 'Orgrimmar', ['22'] = 'Crossroads' } }
";

        static string RdFile(string root, string flavor, string account, string name) => Path.Combine(root, flavor, "WTF", "Account", account, "SavedVariables", name);

        static void RdWrite(string path, string text, DateTime? stamp = null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text);
            if (stamp != null) File.SetLastWriteTimeUtc(path, stamp.Value);
        }

        // atomic replace, so a reader on another thread never sees half a file
        static void RdSwap(string path, string text, DateTime stamp)
        {
            var tmp = path + ".new";
            File.WriteAllText(tmp, text);
            File.SetLastWriteTimeUtc(tmp, stamp);
            File.Replace(tmp, path, null);
        }

        // the walk every reader wrote for itself before (kept here as the reference): all _*_ flavors, all account folders, the file if it exists
        static List<string> RdOldWalk(string root, string name)
        {
            var files = new List<string>();
            foreach (var flavor in Directory.GetDirectories(root, "_*_"))
            {
                var accounts = Path.Combine(flavor, "WTF", "Account");
                if (!Directory.Exists(accounts)) continue;
                foreach (var acc in Directory.GetDirectories(accounts))
                {
                    var f = Path.Combine(acc, "SavedVariables", name);
                    if (File.Exists(f)) files.Add(f);
                }
            }
            return files;
        }

        async Task<string> ReadersTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            Action<string, bool> check = (what, cond) => { notes.Add((cond ? "ok   " : "FAIL ") + what); ok &= cond; };
            var root = Path.Combine(dir, "readerswow");
            try
            {
                if (Directory.Exists(root)) Util.DeleteDir(root);
                Directory.CreateDirectory(root);
                check("fixture: the WoW folder of this test is under the scratch folder (never the real game)", Path.GetFullPath(root).StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase));
                var t0 = new DateTime(2026, 10, 12, 10, 0, 0, DateTimeKind.Utc);

                // ---- one walk finds the same files as the old walks did
                var wide = Path.Combine(root, "wide");
                RdWrite(RdFile(wide, "_retail_", "ACC1", "ElansHub.lua"), RdHub(60), t0);
                RdWrite(RdFile(wide, "_retail_", "ACC2", "ElansHub.lua"), RdHub(61), t0.AddHours(1));
                RdWrite(RdFile(wide, "_classic_", "ACC3", "ElansHub.lua"), RdHub(62), t0.AddHours(2));
                RdWrite(RdFile(wide, "_anniversary_", "ACC4", "ElansBags.lua"), RdBags, t0);
                RdWrite(RdFile(wide, "_classic_", "ACC3", "ElansHunterHelper.lua"), RdHunter, t0);
                Directory.CreateDirectory(Path.Combine(wide, "_classic_", "WTF", "Account", "NOSV"));        // an account without SavedVariables
                Directory.CreateDirectory(Path.Combine(wide, "Interface", "AddOns"));                        // not a flavor folder
                RdWrite(Path.Combine(wide, "Interface", "WTF", "Account", "X", "SavedVariables", "ElansHub.lua"), RdHub(1));   // not under _*_: never found
                foreach (var name in new[] { "ElansHub.lua", "ElansBags.lua", "ElansHunterHelper.lua", "Missing.lua" })
                {
                    var mine = SavedVarsFiles.For(wide, name).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                    var old = RdOldWalk(wide, name).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                    check("walk: " + name + " - the same " + old.Count + " file(s) as the old per-reader walk", mine.SequenceEqual(old, StringComparer.OrdinalIgnoreCase));
                }
                check("walk: Dirs lists every SavedVariables folder (4 accounts with one), none for a missing root", SavedVarsFiles.Dirs(wide).Count == 4 && SavedVarsFiles.Dirs(wide + "-nope").Count == 0 && SavedVarsFiles.Dirs(null).Count == 0);
                check("walk: the readers' own entry points use it (Files / Dirs forward to the walk)",
                    InventoryReader.Files(wide).SequenceEqual(SavedVarsFiles.For(wide, "ElansBags.lua")) && InventoryReader.Dirs(wide).SequenceEqual(SavedVarsFiles.Dirs(wide)));
                check("walk: a path or an empty name is not a file name", SavedVarsFiles.For(wide, "..\\ElansHub.lua").Count == 0 && SavedVarsFiles.For(wide, "a/b.lua").Count == 0 && SavedVarsFiles.For(wide, "").Count == 0);
                check("walk: the newest ElansHub.lua is the one in the newest flavor (presence)", GamePresence.NewestSavedVariables(wide) == RdFile(wide, "_classic_", "ACC3", "ElansHub.lua"));
                SavedVarsCache.Reset(); HubData.ResetCache();
                check("walk: HubData.Read picks the same newest file", HubData.Read(wide).SourceFile == RdFile(wide, "_classic_", "ACC3", "ElansHub.lua") && HubData.Read(wide).Char("Realm-Elan").Core.Level == 62);

                // ---- links are skipped
                var outside = Path.Combine(dir, "readers-outside");
                RdWrite(Path.Combine(outside, "SavedVariables", "ElansHub.lua"), RdHub(99));
                var junction = Path.Combine(wide, "_retail_", "WTF", "Account", "LINK");
                var sv = Path.Combine(wide, "_retail_", "WTF", "Account", "ACC2", "SavedVariables");
                bool made = RdJunction(junction, outside);
                if (made)
                {
                    check("links: an account folder that is a junction is skipped (still 3 hub files, 4 folders)", SavedVarsFiles.For(wide, "ElansHub.lua").Count == 3 && SavedVarsFiles.Dirs(wide).Count == 4 && !SavedVarsFiles.For(wide, "ElansHub.lua").Any(f => f.Contains("LINK")));
                    try { Directory.Delete(junction); } catch { }
                }
                else notes.Add("skip links: could not create a junction here");
                var junction2 = Path.Combine(wide, "_retail_", "WTF", "Account", "ACC5");
                Directory.CreateDirectory(junction2);
                var svLink = Path.Combine(junction2, "SavedVariables");
                if (RdJunction(svLink, Path.Combine(outside, "SavedVariables")))
                {
                    check("links: a SavedVariables folder that is a junction is skipped", SavedVarsFiles.For(wide, "ElansHub.lua").Count == 3 && SavedVarsFiles.Dirs(wide).Count == 4);
                    try { Directory.Delete(svLink); } catch { }
                }
                else notes.Add("skip links: could not create a junction here");
                try { Util.DeleteDir(outside); } catch { }

                // ---- one parse per change when all readers ask
                var one = Path.Combine(root, "one");
                var hub = RdFile(one, "_classic_beta_", "ACC", "ElansHub.lua");
                var bags = RdFile(one, "_classic_beta_", "ACC", "ElansBags.lua");
                var hunter = RdFile(one, "_classic_beta_", "ACC", "ElansHunterHelper.lua");
                RdWrite(hub, RdHub(60), t0); RdWrite(bags, RdBags, t0); RdWrite(hunter, RdHunter, t0);
                SavedVarsCache.Reset(); HubData.ResetCache();
                var pres = GamePresence.Read(hub);
                var cal = WowCalendar.Read(one);
                var looks = InventoryReader.ReadLooks(one);
                var model = HubData.Read(one);
                var rep = HealthReader.Read(one);
                var inv = InventoryReader.ReadData(one);
                var fl = FlightShare.Read(one);
                check("readers: every reader got its data (presence, calendar, looks, hub, health, inventory, flights)",
                    pres != null && pres.Name == "Elan" && pres.Level == 60 && cal.Found && cal.Events.Count == 1 && looks.ContainsKey("elan|realm") && model.Found && model.Char("Realm-Elan").Core.Level == 60
                    && rep.Addons.Count(a => a.HasFile) == 3 && inv.Chars.Count == 1 && fl.Legs.Count == 1);
                check("readers: 3 files, 3 parses (ElansHub.lua once for presence + calendar + looks + hub + health + flights, Bags and Hunter Helper once each); was 9", SavedVarsCache.ParseCount == 3);
                GamePresence.Read(hub); WowCalendar.Read(one); InventoryReader.ReadLooks(one); HubData.Read(one); HealthReader.Read(one); InventoryReader.ReadData(one); FlightShare.Read(one);
                check("readers: asking everyone again parses nothing", SavedVarsCache.ParseCount == 3);
                var g1 = SavedVarsCache.Get(hub);
                check("cache: the same parsed tables are handed out while the file is unchanged", ReferenceEquals(g1, SavedVarsCache.Get(hub)) && g1.ContainsKey("ElansHubDB") && g1.ContainsKey("ElansHubData"));

                // ---- parsed again after a change
                RdWrite(hub, RdHub(61), t0.AddHours(1));
                check("change: a changed ElansHub.lua is parsed again, once, for all readers",
                    GamePresence.Read(hub).Level == 61 && WowCalendar.Read(one).Found && HubData.Read(one).Char("Realm-Elan").Core.Level == 61 && HealthReader.Read(one).Addons.Count(a => a.HasFile) == 3
                    && SavedVarsCache.ParseCount == 4);
                check("change: the new tables are a new object, the old ones were not touched", !ReferenceEquals(g1, SavedVarsCache.Get(hub)) && g1["ElansHubDB"] is Dictionary<string, object> od && od.Child("chars").Child("Realm-Elan").Int("level") == 60);

                // ---- the last good parse survives a half-written file
                var good = SavedVarsCache.Get(hub);
                RdWrite(hub, "ElansHubDB = { current = 'Realm-Elan', chars = { ['Realm-Elan'] = { name = 'Elan', ", t0.AddHours(2));
                int before = SavedVarsCache.ParseCount;
                check("half file: the last good parse is kept", ReferenceEquals(SavedVarsCache.Get(hub), good) && GamePresence.Read(hub).Level == 61);
                SavedVarsCache.Get(hub); GamePresence.Read(hub);
                check("half file: the same bad content is not parsed again and again", SavedVarsCache.ParseCount == before + 1);
                RdWrite(hub, RdHub(63), t0.AddHours(3));
                check("half file: once the file is whole the new content is picked up", GamePresence.Read(hub).Level == 63);

                // ---- locked for good
                good = SavedVarsCache.Get(hub);
                RdWrite(hub, RdHub(64), t0.AddHours(4));
                using (new FileStream(hub, FileMode.Open, FileAccess.Read, FileShare.None))
                    check("locked: a file locked exclusively keeps the last good parse (no exception)", ReferenceEquals(SavedVarsCache.Get(hub), good) && GamePresence.Read(hub).Level == 63);
                check("locked: once the lock is gone the new content is read", GamePresence.Read(hub).Level == 64);
                string shared;
                using (var fs = new FileStream(hub, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
                    shared = SavedVarsFiles.SafeRead(hub);
                check("safe read: a file the game still has open for writing is readable", shared != null && shared.Contains("ElansHubDB"));
                check("safe read: a missing file gives null at once (no retry wait), the cache has nothing for it", SavedVarsFiles.SafeRead(Path.Combine(root, "no-such.lua")) == null && SavedVarsCache.Get(Path.Combine(root, "no-such.lua")) == null && SavedVarsCache.Get(null) == null);

                // ---- two threads at once
                SavedVarsCache.Reset(); HubData.ResetCache();
                RdWrite(hub, RdHub(70), t0.AddHours(10));
                var results = new object[8];
                var start = new ManualResetEventSlim(false);
                var gets = Enumerable.Range(0, 8).Select(i => Task.Run(() => { start.Wait(); results[i] = SavedVarsCache.Get(hub); })).ToArray();
                start.Set();
                await Task.WhenAll(gets);
                check("threads: 8 readers asking at the same moment cause one parse and get the same tables", SavedVarsCache.ParseCount == 1 && results.All(r => ReferenceEquals(r, results[0]) && r != null));

                var errors = new List<string>();
                int stop = 0, reads = 0, level = 70;
                var worker = Task.Run(() =>      // like HealthMonitor: a worker thread reading the big files
                {
                    try { while (Volatile.Read(ref stop) == 0) { var r = HealthReader.Read(one); if (r.Addons.Count(a => a.HasFile) > 3) lock (errors) errors.Add("health invented a file"); Interlocked.Increment(ref reads); } }
                    catch (Exception e) { lock (errors) errors.Add("worker: " + e.GetType().Name + " " + e.Message); }
                });
                var ui = Task.Run(() =>          // like the UI thread: presence, calendar, hub model
                {
                    try
                    {
                        while (Volatile.Read(ref stop) == 0)
                        {
                            var p = GamePresence.Read(hub);
                            var m = HubData.Read(one); WowCalendar.Read(one);
                            // the game replaces the file: for a moment it may be missing (null / empty is fine), but whatever is read must be a real version
                            var lv = m.Char("Realm-Elan")?.Core?.Level ?? 70;
                            if ((p != null && (p.Level < 70 || p.Level > 79)) || lv < 70 || lv > 79) lock (errors) errors.Add("ui: bad read (level " + p?.Level + "/" + lv + ")");
                            Interlocked.Increment(ref reads);
                        }
                    }
                    catch (Exception e) { lock (errors) errors.Add("ui: " + e.GetType().Name + " " + e.Message); }
                });
                for (int i = 1; i <= 9; i++)
                {
                    level = 70 + i;
                    try { RdSwap(hub, RdHub(level), t0.AddHours(10 + i)); } catch (Exception e) { lock (errors) errors.Add("writer: " + e.GetType().Name + " " + e.Message); }
                    await Task.Delay(40);
                }
                Volatile.Write(ref stop, 1);
                await Task.WhenAll(worker, ui);
                check("threads: a worker and the UI thread reading while the file is replaced 9 times: no exception, never a wrong version (" + reads + " reads)", errors.Count == 0 && reads > 4);
                if (errors.Count > 0) notes.Add("      " + string.Join("; ", errors.Take(3)));
                check("threads: afterwards everybody sees the last version", GamePresence.Read(hub).Level == level && HubData.Read(one).Char("Realm-Elan").Core.Level == level);
            }
            catch (Exception e) { ok = false; notes.Add("crashed: " + e); }
            finally { HubData.ResetCache(); SavedVarsCache.Reset(); }
            return "readers selftest: " + (ok ? "PASSED" : "FAILED") + "\r\n  " + string.Join("\r\n  ", notes);
        }

        static bool RdJunction(string link, string target)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(link));
                var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + link + "\" \"" + target + "\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true });
                p.WaitForExit(10000);
                return p.ExitCode == 0 && Directory.Exists(link);
            }
            catch { return false; }
        }
    }
}
