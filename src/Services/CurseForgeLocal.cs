using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ElansAddonHub.Services
{
    // One addon the CurseForge app manages (read from its local AddonGameInstance.json - the hub never calls CurseForge's
    // web API, never reads its download links and never writes to its files or to these addon folders itself).
    public class CfAddon
    {
        public long Id, InstalledId, LatestId;
        public string Name, Url, InstalledFileName, LatestFileName;
        public DateTime LatestDate;                          // UTC, 0 = unknown
        public List<string> Folders = new List<string>();
        public bool UpdateAvailable => LatestId > 0 && InstalledId > 0 && LatestId != InstalledId;
    }

    public class CfInstance
    {
        public string Path;
        public DateTime LastRefresh;                         // UTC, MinValue = unknown
        public List<CfAddon> Addons = new List<CfAddon>();
    }

    // Read-only view of the CurseForge desktop app's state, plus the two things the app lets other programs ask for:
    // its curseforge://install deep link and a start with --minimized (it refreshes its update info on startup).
    public static class CurseForgeLocal
    {
        public static string DataFile =>
            Environment.GetEnvironmentVariable("ELANSHUB_CF_FILE") is string f && f.Length > 0 ? f
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CurseForge", "agent", "GameInstances", "AddonGameInstance.json");

        public static string ExePath =>
            Environment.GetEnvironmentVariable("ELANSHUB_CF_EXE") is string f && f.Length > 0 ? f
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "CurseForge Windows", "CurseForge.exe");

        public static bool Installed => File.Exists(ExePath);

        static string Norm(string p)
        {
            try { return Path.GetFullPath(p ?? "").TrimEnd('\\', '/').ToLowerInvariant(); } catch { return (p ?? "").TrimEnd('\\', '/').ToLowerInvariant(); }
        }

        static DateTime ParseUtc(string s)
        {
            return DateTimeOffset.TryParse(s ?? "", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? d.UtcDateTime : DateTime.MinValue;
        }

        // The instance for this WoW client folder (matched by install path), or null. Any problem with the file = null (feature off).
        public static CfInstance Load(string addOnsDir)
        {
            try
            {
                if (string.IsNullOrEmpty(addOnsDir) || !File.Exists(DataFile)) return null;
                string text;
                using (var fs = new FileStream(DataFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var sr = new StreamReader(fs, Encoding.UTF8)) text = sr.ReadToEnd();
                var root = Json.Parse(text) as List<object>;
                if (root == null) return null;
                var want = Norm(Path.Combine(addOnsDir, "..", ".."));
                var wantAdd = Norm(addOnsDir);
                foreach (var o in root)
                {
                    var inst = o as Dictionary<string, object>;
                    if (inst == null) continue;
                    var path = inst.Str("installPath");
                    var list = inst.List("installedAddons");
                    bool pathMatch = !string.IsNullOrEmpty(path) && Norm(path) == want;
                    if (!pathMatch && !list.OfType<Dictionary<string, object>>().Any(a => Norm(a.Str("modFolderPath")) == wantAdd)) continue;
                    var res = new CfInstance { Path = path, LastRefresh = ParseUtc(inst.Str("lastRefreshAttempt")) };
                    // CurseForge only rewrites this file when something changed; its log records every update check
                    var logged = LastLoggedCheck(inst.Str("name"));
                    if (logged > res.LastRefresh) res.LastRefresh = logged;
                    foreach (var ao in list)
                    {
                        var a = ao as Dictionary<string, object>;
                        if (a == null) continue;
                        var ins = a.Child("installedFile"); var lat = a.Child("latestFile");
                        var add = new CfAddon
                        {
                            Id = a.Long("addonID"), Name = a.Str("name"), Url = a.Str("webSiteURL"),
                            InstalledId = ins.Long("id"), LatestId = lat.Long("id"),
                            InstalledFileName = ins.Str("fileName"), LatestFileName = lat.Str("fileName"),
                            LatestDate = ParseUtc(lat.Str("fileDate")),
                        };
                        foreach (var m in (ins ?? lat).List("modules").OfType<Dictionary<string, object>>())
                            if (m.Str("foldername") is string fn && fn.Length > 0 && !add.Folders.Contains(fn, StringComparer.OrdinalIgnoreCase)) add.Folders.Add(fn);
                        foreach (var fp in a.List("filePaths").OfType<string>())
                        {
                            // only folders directly inside AddOns
                            if (Norm(Path.GetDirectoryName(fp)) == wantAdd && Path.GetFileName(fp) is string n && n.Length > 0
                                && !add.Folders.Contains(n, StringComparer.OrdinalIgnoreCase) && Directory.Exists(fp)) add.Folders.Add(n);
                        }
                        if (add.Id > 0 && add.Folders.Count > 0) res.Addons.Add(add);
                    }
                    return res;
                }
            }
            catch (Exception e) { Util.Log("curseforge file unreadable: " + e.Message); }
            return null;
        }

        // Folders CurseForge manages: the cards for these are handled by CurseForge, never by the hub's own updaters.
        // Entries are re-split so each CurseForge addon is exactly the folders CurseForge says it owns.
        public static List<AddonEntry> Apply(List<AddonEntry> entries, CfInstance cf)
        {
            if (cf == null || cf.Addons.Count == 0) return entries;
            var result = new List<AddonEntry>(entries);
            foreach (var add in cf.Addons)
            {
                var have = add.Folders.Where(f => result.Any(e => e.Folders.Contains(f, StringComparer.OrdinalIgnoreCase))).ToList();
                if (have.Count == 0) continue;
                var src = result.Where(e => e.Folders.Any(f => have.Contains(f, StringComparer.OrdinalIgnoreCase)))
                    .OrderByDescending(e => e.Folders.Count(f => have.Contains(f, StringComparer.OrdinalIgnoreCase))).First();
                var key = have.Contains(src.Key, StringComparer.OrdinalIgnoreCase) ? src.Key : have.OrderBy(h => h.Length).First();
                var n = new AddonEntry
                {
                    Key = key, Folders = have.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(), Title = string.IsNullOrEmpty(add.Name) ? src.Title : add.Name,
                    Version = src.Version, Author = src.Author, Notes = src.Notes, Interface = src.Interface, CurseId = add.Id.ToString(),
                    Website = src.Website, Modified = src.Modified, TocName = src.TocName, Cf = add,
                };
                if (string.IsNullOrEmpty(n.Version)) n.Version = Regex_StripZip(add.InstalledFileName);
                foreach (var e in result.ToList())
                {
                    if (!e.Folders.Any(f => have.Contains(f, StringComparer.OrdinalIgnoreCase))) continue;
                    n.IsDev |= e.IsDev;
                    e.Folders = e.Folders.Where(f => !have.Contains(f, StringComparer.OrdinalIgnoreCase)).ToList();
                    if (e.Folders.Count == 0) result.Remove(e);
                    else if (!e.Folders.Contains(e.Key, StringComparer.OrdinalIgnoreCase)) e.Key = e.Folders[0];
                }
                result.Add(n);
            }
            return result.OrderBy(a => a.Title, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // CurseForge's agent log (agent\logs\CurseClient\*.json) has a line "Completed checking for updates in instance <name>"
        // after every update check, even when nothing changed. UTC time of the newest one, or MinValue.
        public static string LogDir =>
            Environment.GetEnvironmentVariable("ELANSHUB_CF_LOGS") is string f && f.Length > 0 ? f
            : Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(DataFile)) ?? "", "logs", "CurseClient");

        public static DateTime LastLoggedCheck(string instanceName)
        {
            try
            {
                if (string.IsNullOrEmpty(instanceName) || !Directory.Exists(LogDir)) return DateTime.MinValue;
                var marker = "Completed checking for updates in instance " + instanceName + ".";
                foreach (var file in new DirectoryInfo(LogDir).GetFiles("*.json").OrderByDescending(x => x.LastWriteTimeUtc).Take(2))
                {
                    string text;
                    using (var fs = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var sr = new StreamReader(fs, Encoding.UTF8)) text = sr.ReadToEnd();
                    var i = text.LastIndexOf(marker, StringComparison.Ordinal);
                    if (i < 0) continue;
                    var t = text.LastIndexOf("\"timestamp\"", i, StringComparison.Ordinal);
                    if (t < 0) continue;
                    var q1 = text.IndexOf('"', text.IndexOf(':', t) + 1);
                    var q2 = q1 < 0 ? -1 : text.IndexOf('"', q1 + 1);
                    if (q2 > q1) return ParseUtc(text.Substring(q1 + 1, q2 - q1 - 1));
                }
            }
            catch (Exception e) { Util.Log("curseforge log unreadable: " + e.GetType().Name); }
            return DateTime.MinValue;
        }

        static string Regex_StripZip(string s) => string.IsNullOrEmpty(s) ? null : (s.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? s.Substring(0, s.Length - 4) : s);

        public static string DeepLink(CfAddon a, long fileId) => $"curseforge://install?addonId={a.Id}&fileId={fileId}";

        public static bool IsRunning()
        {
            try { return Process.GetProcessesByName("CurseForge").Length > 0; } catch { return false; }
        }

        public static bool OpenApp(string args = null)
        {
            try
            {
                if (!Installed) return false;
                Process.Start(new ProcessStartInfo(ExePath, args ?? "") { UseShellExecute = false });
                return true;
            }
            catch (Exception e) { Util.Log("open curseforge failed: " + e.Message); return false; }
        }

        // Ask CurseForge itself to install one specific file of one addon (verified in its app.asar: the curseforge://install handler).
        // Returns once the file shows the new installed id, or after the timeout (CurseForge may still be working).
        public static async Task<bool> RequestInstall(CfAddon a, long fileId, string addOnsDir, int timeoutSeconds = 120)
        {
            if (a == null || fileId <= 0) return false;
            try { Process.Start(new ProcessStartInfo(DeepLink(a, fileId)) { UseShellExecute = true }); }
            catch (Exception e) { Util.Log("curseforge deep link failed: " + e.Message); return false; }
            var end = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (DateTime.UtcNow < end)
            {
                await Task.Delay(1500);
                var now = Load(addOnsDir)?.Addons.FirstOrDefault(x => x.Id == a.Id);
                if (now != null && now.InstalledId == fileId) return true;
            }
            return false;
        }

        public static bool Busy;

        // Let CurseForge refresh its update info. Already running: just read what it has. Not running: start it
        // minimized (it checks on startup), wait for a newer update check (file or its log), then close only that instance politely.
        // Returns a short status text.
        public static async Task<string> CheckNow(string addOnsDir, int timeoutSeconds = 120)
        {
            if (Busy) return "Already checking.";
            Busy = true;
            try
            {
                var before = Load(addOnsDir);
                if (before == null) return "CurseForge doesn't manage this WoW folder.";
                if (IsRunning()) return "CurseForge is already running - showing what it knows.";
                if (!Installed) return "CurseForge isn't installed.";
                Process p;
                try { p = Process.Start(new ProcessStartInfo(ExePath, "--minimized") { UseShellExecute = false }); }
                catch (Exception e) { Util.Log("start curseforge failed: " + e.Message); return "Couldn't start CurseForge."; }
                bool done = false;
                var end = DateTime.UtcNow.AddSeconds(timeoutSeconds);
                while (DateTime.UtcNow < end)
                {
                    await Task.Delay(1000);
                    var now = Load(addOnsDir);
                    if (now != null && now.LastRefresh > before.LastRefresh) { done = true; break; }
                    if (p != null && p.HasExited) break;
                }
                if (done) await Task.Delay(5000);          // let it finish writing
                await CloseStarted(p);
                return done ? "CurseForge checked just now." : "CurseForge didn't finish checking in time.";
            }
            finally { Busy = false; }
        }

        // polite close (WM_CLOSE) of the instance the hub started; never kills it. CurseForge may keep running in its tray.
        static async Task CloseStarted(Process p)
        {
            try
            {
                if (p == null) return;
                for (int i = 0; i < 3 && !p.HasExited; i++)
                {
                    try { p.Refresh(); p.CloseMainWindow(); } catch { }
                    for (int k = 0; k < 8 && !p.HasExited; k++) await Task.Delay(1000);
                }
                if (!p.HasExited) Util.Log("curseforge started by the hub is still running (tray); left alone");
            }
            catch (Exception e) { Util.Log("curseforge close: " + e.Message); }
        }

        public static string Ago(DateTime utc)
        {
            if (utc <= DateTime.MinValue.AddDays(2)) return "never";
            var d = DateTime.UtcNow - utc;
            if (d.TotalMinutes < 2) return "just now";
            if (d.TotalMinutes < 90) return $"{(int)d.TotalMinutes} min ago";
            if (d.TotalHours < 36) return $"{(int)d.TotalHours} h ago";
            return $"{(int)d.TotalDays} days ago";
        }

        public static bool IsStale(DateTime utc) => (DateTime.UtcNow - utc).TotalHours > 24;

        // ---- self-test: synthetic AddonGameInstance.json + fake AddOns folder; never starts CurseForge
        public static string SelfTest(string addOnsDir)
        {
            var sb = new StringBuilder();
            bool ok = true;
            void Check(string what, bool c, string info = null) { sb.AppendLine($"curseforge {what}: {(c ? "ok" : "FAIL")}{(info == null ? "" : " (" + info + ")")}"); ok &= c; }
            try
            {
                var cf = Load(addOnsDir);
                Check("instance found by install path", cf != null);
                if (cf != null)
                {
                    Check("addons read", cf.Addons.Count == 3, cf.Addons.Count.ToString());
                    var up = cf.Addons.Where(a => a.UpdateAvailable).Select(a => a.Name).ToList();
                    Check("update = ids differ", up.Count == 2 && up[0] == "CfMulti", string.Join(",", up));
                    Check("deep link format", cf.Addons.Count > 0 && DeepLink(cf.Addons[0], 42) == $"curseforge://install?addonId={cf.Addons[0].Id}&fileId=42", cf.Addons.Count > 0 ? DeepLink(cf.Addons[0], 42) : "");
                    Check("last refresh parsed", cf.LastRefresh > DateTime.MinValue, cf.LastRefresh.ToString("s"));
                }
                Check("other install path ignored", Load(Path.Combine(Path.GetTempPath(), "nope", "Interface", "AddOns")) == null);
            }
            catch (Exception e) { sb.AppendLine("curseforge selftest crashed: " + e); ok = false; }
            sb.AppendLine("curseforge selftest " + (ok ? "PASSED" : "FAILED"));
            return sb.ToString();
        }
    }
}
