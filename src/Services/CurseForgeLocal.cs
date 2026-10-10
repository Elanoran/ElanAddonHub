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
        public string Path, Name;
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
        public static string LastLoadError;                 // why the last Load found nothing usable (null = fine / simply no instance)

        public static CfInstance Load(string addOnsDir) => Load(addOnsDir, DataFile);

        public static CfInstance Load(string addOnsDir, string dataFile)
        {
            LastLoadError = null;
            try
            {
                if (string.IsNullOrEmpty(addOnsDir) || !File.Exists(dataFile)) return null;
                string text;
                using (var fs = new FileStream(dataFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var sr = new StreamReader(fs, Encoding.UTF8)) text = sr.ReadToEnd();
                var root = Json.Parse(text) as List<object>;
                if (root == null) { LastLoadError = "the file isn't in the format the Outpost knows"; return null; }
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
                    var res = new CfInstance { Path = path, Name = inst.Str("name"), LastRefresh = ParseUtc(inst.Str("lastRefreshAttempt")) };
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
            catch (Exception e) { LastLoadError = e is IOException || e is UnauthorizedAccessException ? e.Message : "the file looks damaged (" + e.Message + ")"; Util.Log("curseforge file unreadable: " + e.Message); }
            return null;
        }

        // ---- Settings > Addons: "CurseForge integration: OK (found Forever, 6 addons, checked 12m ago)". Local files only, nothing is started.
        public enum CfStatusKind { Ok, NotInstalled, NoInstance, Unreadable, NoWowFolder }
        public class CfStatus
        {
            public CfStatusKind Kind;
            public string Text;
            public int Addons;
            public override string ToString() => Text;
        }

        public static string StatusTip =>
            "What this reads: CurseForge's own local list of the addons it manages (AddonGameInstance.json in %APPDATA%\\CurseForge\\agent\\GameInstances) and its log files "
            + "(only the time of its last update check). Local files only - nothing is sent anywhere, nothing in CurseForge's folders is ever written, "
            + "and CurseForge isn't started by this check.";

        public static CfStatus Probe(string addOnsDir, string dataFile = null, bool? installed = null)
        {
            dataFile = dataFile ?? DataFile;
            bool inst = installed ?? Installed;
            if (string.IsNullOrEmpty(addOnsDir)) return new CfStatus { Kind = CfStatusKind.NoWowFolder, Text = "CurseForge integration: no WoW folder picked yet" };
            if (!File.Exists(dataFile) && !inst) return new CfStatus { Kind = CfStatusKind.NotInstalled, Text = "CurseForge not installed" };
            if (!File.Exists(dataFile))
                return new CfStatus { Kind = CfStatusKind.Unreadable, Text = "Couldn't read CurseForge data - its addon list doesn't exist yet (open CurseForge once)" };
            var cf = Load(addOnsDir, dataFile);
            if (cf == null)
            {
                var why = LastLoadError;
                if (why != null) return new CfStatus { Kind = CfStatusKind.Unreadable, Text = "Couldn't read CurseForge data - " + Short(why) };
                return new CfStatus { Kind = CfStatusKind.NoInstance, Text = "CurseForge is installed but doesn't manage this WoW folder (no game instance for it)" };
            }
            var when = cf.LastRefresh == DateTime.MinValue ? "no update check seen yet" : "checked " + Ago(cf.LastRefresh);
            var name = string.IsNullOrEmpty(cf.Name) ? "your WoW folder" : cf.Name;
            return new CfStatus
            {
                Kind = CfStatusKind.Ok, Addons = cf.Addons.Count,
                Text = $"CurseForge integration: OK (found {name}, {cf.Addons.Count} addon{(cf.Addons.Count == 1 ? "" : "s")}, {when})",
            };
        }

        static string Short(string s)
        {
            s = System.Text.RegularExpressions.Regex.Replace(s ?? "", @"\s+", " ").Trim();
            return s.Length > 90 ? s.Substring(0, 87) + "..." : s;
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
        // Why this is more than "send the link and poll": when CurseForge is not running, Windows starts it with the link as a startup
        // argument, and if it then finds its own app update it installs it, quits and relaunches WITHOUT the argument - the request is lost
        // (its log: "Performing app update ... RelaunchAfterUpdate: true", "Shutdown complete", a new session without the link).
        // Link parameters (read-only check of CurseForge 1.322.0 app.asar, deep-link parser "case install"): only addonId, fileId, gameId,
        // code, source, medium, campaign are read. There is NO instance parameter, so since 1.322.0 CurseForge itself shows its modal
        // "Where would you like to install your addon?" and the user must pick the instance and click Install (the hub never clicks for them).
        // So: (1) start CurseForge ourselves first and wait until it is up and idle, (2) after sending, look for CurseForge's own
        // "Processing project install command" log line, (3) no confirmation in ConfirmSec or a self-update/restart seen = wait until it
        // is up again and resend the link ONCE, (4) still nothing = NotStarted (the card shows Retry instead of hanging).
        public enum CfInstallResult { Installed, Started, NotStarted }

        // timings in seconds (fields, so the selftest can shorten them)
        static int SettleSec = 5, ConfirmSec = 15, ReadyWaitSec = 90, TotalSec = 180, PollMs = 1500;
        // seams for the selftest (never start or message the real CurseForge there)
        static Func<string, bool> SendLink = link =>
        {
            try { Process.Start(new ProcessStartInfo(link) { UseShellExecute = true }); return true; }
            catch (Exception e) { Util.Log("curseforge deep link failed: " + e.Message); return false; }
        };
        static Func<bool> StartApp = () => OpenApp();
        static Func<bool> RunningNow = () => IsRunning();
        static Func<int> MainPid = () =>
        {
            try { var p = Process.GetProcessesByName("CurseForge").OrderBy(x => { try { return x.StartTime; } catch { return DateTime.MaxValue; } }).FirstOrDefault(); return p?.Id ?? 0; }
            catch { return 0; }
        };

        public class MainLogScan { public DateTime Confirmed, Update, Shutdown, Startup; }        // local time of the newest such line, MinValue = none

        static string MainLogRoot =>
            Environment.GetEnvironmentVariable("ELANSHUB_CF_MAINLOGS") is string o && o.Length > 0 ? o
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "curseforge", "logs");

        // Reads the newest main-*.log files of CurseForge (read only) for the lines that tell what the app is doing.
        public static MainLogScan ScanMainLogs()
        {
            var r = new MainLogScan();
            try
            {
                var root = MainLogRoot;
                if (!Directory.Exists(root)) return r;
                foreach (var f in new DirectoryInfo(root).GetFiles("main-*.log", SearchOption.AllDirectories).OrderByDescending(x => x.LastWriteTimeUtc).Take(3))
                {
                    string text;
                    using (var fs = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var sr = new StreamReader(fs, Encoding.UTF8)) text = sr.ReadToEnd();
                    var lines = text.Split('\n');
                    for (int i = Math.Max(0, lines.Length - 600); i < lines.Length; i++)
                    {
                        var l = lines[i];
                        if (l.Length < 25 || l[0] != '[') continue;
                        ref DateTime slot = ref r.Confirmed;
                        if (l.IndexOf("Processing project install command", StringComparison.Ordinal) >= 0) slot = ref r.Confirmed;
                        else if (l.IndexOf("Performing app update", StringComparison.Ordinal) >= 0) slot = ref r.Update;
                        else if (l.IndexOf("Shutdown complete", StringComparison.Ordinal) >= 0 || l.IndexOf("Exiting app", StringComparison.Ordinal) >= 0) slot = ref r.Shutdown;
                        else if (l.IndexOf("App startup args", StringComparison.Ordinal) >= 0) slot = ref r.Startup;
                        else continue;
                        if (DateTime.TryParse(l.Substring(1, 23), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var ts) && ts > slot) slot = ts;
                    }
                }
            }
            catch (Exception e) { Util.Log("curseforge main log unreadable: " + e.GetType().Name); }
            return r;
        }

        // running, its newest session started after the last shutdown / self-update, and that was at least SettleSec ago
        static bool IsReady(MainLogScan s)
        {
            if (!RunningNow()) return false;
            if (s.Startup == DateTime.MinValue) return false;
            if (s.Shutdown >= s.Startup || s.Update >= s.Startup) return false;
            return (DateTime.Now - s.Startup).TotalSeconds >= SettleSec;
        }

        static async Task<bool> WaitReady(DateTime deadlineUtc, Action<string> status, string waitText)
        {
            while (DateTime.UtcNow < deadlineUtc)
            {
                var s = ScanMainLogs();
                if (IsReady(s)) return true;
                if (status != null) status(s.Update != DateTime.MinValue && s.Update >= s.Startup ? "CurseForge is updating itself - retrying..." : waitText);
                await Task.Delay(Math.Min(PollMs, 1000));
            }
            return IsReady(ScanMainLogs());
        }

        public static async Task<CfInstallResult> RequestInstall(CfAddon a, long fileId, string addOnsDir, Action<string> status = null, int timeoutSeconds = -1)
        {
            if (a == null || fileId <= 0) return CfInstallResult.NotStarted;
            if (timeoutSeconds <= 0) timeoutSeconds = TotalSec;
            void Say(string t) { try { status?.Invoke(t); } catch { } }
            var link = DeepLink(a, fileId);
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

            // cold start: bring CurseForge up (a normal start, the user sees it) and let it finish starting / updating itself BEFORE the link
            if (!RunningNow())
            {
                Say("Starting CurseForge...");
                if (!StartApp()) return CfInstallResult.NotStarted;
            }
            if (!IsReady(ScanMainLogs()))
            {
                Say("Waiting for CurseForge...");
                if (!await WaitReady(DateTime.UtcNow.AddSeconds(Math.Min(ReadyWaitSec, timeoutSeconds)), Say, "Waiting for CurseForge...")) Util.Log("curseforge not confirmed idle; sending the link anyway");
            }

            var sentAt = DateTime.Now.AddSeconds(-1);
            var pid0 = MainPid();
            if (!SendLink(link)) return CfInstallResult.NotStarted;
            Say("Waiting for CurseForge...");
            var t0 = DateTime.UtcNow;
            bool confirmed = false, resent = false;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(PollMs);
                var now = Load(addOnsDir)?.Addons.FirstOrDefault(x => x.Id == a.Id);
                if (now != null && now.InstalledId == fileId) return CfInstallResult.Installed;
                var s = ScanMainLogs();
                if (!confirmed && s.Confirmed >= sentAt) { confirmed = true; Say("Confirm in CurseForge"); }
                if (confirmed || resent) continue;
                int pid = pid0 == 0 ? 0 : MainPid();
                bool restarted = s.Update >= sentAt || s.Shutdown >= sentAt || (pid0 != 0 && pid != pid0);
                if (!restarted && (DateTime.UtcNow - t0).TotalSeconds < ConfirmSec) continue;
                if (restarted) { Say("CurseForge is updating itself - retrying..."); Util.Log("curseforge restarted/self-updated after the link; will resend once"); }
                else Util.Log("curseforge didn't confirm the link in " + ConfirmSec + " s; will resend once");
                if (!await WaitReady(deadline, Say, restarted ? "CurseForge is updating itself - retrying..." : "Waiting for CurseForge...")) break;
                s = ScanMainLogs();
                if (s.Confirmed >= sentAt) { confirmed = true; Say("Confirm in CurseForge"); continue; }
                resent = true;
                sentAt = DateTime.Now.AddSeconds(-1); pid0 = MainPid();
                if (!SendLink(link)) break;
                Say("Waiting for CurseForge...");
                t0 = DateTime.UtcNow;
            }
            return confirmed ? CfInstallResult.Started : CfInstallResult.NotStarted;
        }

        // ---- selftest of the flow above with synthetic main logs and fake start/send seams (nothing real is started or messaged)
        public static async Task<string> SelfTestInstall()
        {
            var sb = new StringBuilder();
            void Check(string what, bool c, string info = null) => sb.AppendLine($"curseforge install {what}: {(c ? "ok" : "FAIL")}{(info == null ? "" : " (" + info + ")")}");
            var oldEnv = Environment.GetEnvironmentVariable("ELANSHUB_CF_MAINLOGS");
            var oldT = new[] { SettleSec, ConfirmSec, ReadyWaitSec, TotalSec, PollMs };
            var oSend = SendLink; var oStart = StartApp; var oRun = RunningNow; var oPid = MainPid;
            var root = Path.Combine(Path.GetTempPath(), "elanshub-cflogs-" + Guid.NewGuid().ToString("N"));
            try
            {
                SettleSec = 0; ConfirmSec = 2; ReadyWaitSec = 4; TotalSec = 8; PollMs = 150;
                var fake = new CfAddon { Id = 987654321, Name = "Nope" };
                string Line(DateTime t, string m) => "[" + t.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] [info]  [BackgroundController] " + m + "\n";
                int session = 0;
                void Log(string text) { var d = Path.Combine(root, "s" + (session++).ToString("D3")); Directory.CreateDirectory(d); File.WriteAllText(Path.Combine(d, "main-" + session + ".log"), text); }
                void Append(string text) { var d = Directory.GetDirectories(root).OrderBy(x => x).Last(); File.AppendAllText(Directory.GetFiles(d, "main-*.log")[0], text); }
                void Reset() { if (Directory.Exists(root)) Directory.Delete(root, true); Directory.CreateDirectory(root); session = 0; Environment.SetEnvironmentVariable("ELANSHUB_CF_MAINLOGS", root); }
                var order = new List<string>();
                bool running = true; int pid = 100;
                RunningNow = () => running; MainPid = () => running ? pid : 0;

                // 1) running + idle, CurseForge confirms the link
                Reset(); running = true; order.Clear();
                Log(Line(DateTime.Now.AddMinutes(-5), "App startup args: CurseForge.exe."));
                int sends = 0;
                SendLink = l => { sends++; order.Add("send"); Append(Line(DateTime.Now, "Processing project install command.")); return true; };
                var texts = new List<string>();
                var r = await RequestInstall(fake, 1, "", t => texts.Add(t));
                Check("confirmed link = one send, no retry", r == CfInstallResult.Started && sends == 1, r + ", sends=" + sends);
                Check("status shows installing", texts.Contains("Confirm in CurseForge"));

                // 2) self-update + relaunch swallows the link: wait for it to come back, resend once
                Reset(); running = true; sends = 0; texts.Clear();
                Log(Line(DateTime.Now.AddMinutes(-5), "App startup args: CurseForge.exe."));
                SendLink = l =>
                {
                    sends++;
                    if (sends == 1)
                    {
                        Append(Line(DateTime.Now, "Performing app update. IsSilent: false, RelaunchAfterUpdate: true.") + Line(DateTime.Now, "Shutdown complete. Exiting app...."));
                        running = false;
                        Task.Run(async () => { await Task.Delay(1200); Log(Line(DateTime.Now, "App startup args: CurseForge.exe,--updated.")); pid = 200; running = true; });
                    }
                    else Append(Line(DateTime.Now, "Processing project install command."));
                    return true;
                };
                r = await RequestInstall(fake, 1, "", t => texts.Add(t));
                Check("self-update + restart = exactly one resend", r == CfInstallResult.Started && sends == 2, r + ", sends=" + sends);
                Check("status shows updating itself", texts.Contains("CurseForge is updating itself - retrying..."));

                // 3) nothing ever confirms: one resend, then NotStarted (the card shows Retry)
                Reset(); running = true; pid = 100; sends = 0; texts.Clear();
                Log(Line(DateTime.Now.AddMinutes(-5), "App startup args: CurseForge.exe."));
                SendLink = l => { sends++; return true; };
                r = await RequestInstall(fake, 1, "", t => texts.Add(t));
                Check("no confirmation = one resend then NotStarted", r == CfInstallResult.NotStarted && sends == 2, r + ", sends=" + sends);

                // 4) not running: start it first, wait until its first session shows it up, only then send the link
                Reset(); running = false; sends = 0; order.Clear(); texts.Clear();
                StartApp = () => { order.Add("start"); Task.Run(async () => { await Task.Delay(800); Log(Line(DateTime.Now, "App startup args: CurseForge.exe.")); running = true; }); return true; };
                SendLink = l => { sends++; order.Add("send"); Append(Line(DateTime.Now, "Processing project install command.")); return true; };
                r = await RequestInstall(fake, 1, "", t => texts.Add(t));
                Check("cold start: started first, link after it was up", r == CfInstallResult.Started && string.Join(",", order) == "start,send" && texts.Contains("Starting CurseForge..."), r + ", " + string.Join(",", order));

                // 5) the log reader itself
                Reset(); Log(Line(new DateTime(2026, 1, 2, 3, 4, 5, 6), "Performing app update."));
                var sc = ScanMainLogs();
                Check("log line timestamp parsed", sc.Update == new DateTime(2026, 1, 2, 3, 4, 5, 6), sc.Update.ToString("o"));
            }
            catch (Exception e) { sb.AppendLine("curseforge install selftest crashed: " + e); sb.AppendLine("curseforge install: FAIL"); }
            finally
            {
                SettleSec = oldT[0]; ConfirmSec = oldT[1]; ReadyWaitSec = oldT[2]; TotalSec = oldT[3]; PollMs = oldT[4];
                SendLink = oSend; StartApp = oStart; RunningNow = oRun; MainPid = oPid;
                Environment.SetEnvironmentVariable("ELANSHUB_CF_MAINLOGS", oldEnv);
                try { Directory.Delete(root, true); } catch { }
            }
            return sb.ToString();
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

        // Close the instance the hub started, politely, never killing it. Evidence (CurseForge app.asar + its logs): the app has no
        // quit switch or deep link, and its window 'close' handler cancels the close and asks the renderer, which quits when the user's
        // "closeCurseForgeAction" setting is Exit (storage.json) or hides to the tray when it is Hide. A minimized start leaves the main
        // window hidden and Process.CloseMainWindow() ignores hidden windows - that is why nothing happened. So: WM_CLOSE (posted,
        // like the X button) to every top-level window of the main process, hidden ones included. With the Hide setting the app
        // stays in the tray (we only read the setting and log it - the hub never changes CurseForge's settings).
        static async Task CloseStarted(Process p)
        {
            try
            {
                if (p == null || p.HasExited) return;
                // never interrupt work: wait (up to 90 s) until its log has been quiet about installs/downloads for 15 s
                for (int i = 0; i < 90 && RecentInstallActivity(); i++) await Task.Delay(1000);
                if (RecentInstallActivity()) { Util.Log("curseforge busy; left running"); return; }
                for (int i = 0; i < 3 && !p.HasExited; i++)
                {
                    int n = PostCloseToWindows(p.Id);
                    Util.Log("curseforge close: WM_CLOSE to " + n + " window(s)");
                    for (int k = 0; k < 10 && !p.HasExited; k++) await Task.Delay(1000);
                }
                if (p.HasExited)
                {
                    for (int k = 0; k < 15 && Process.GetProcessesByName("Curse.Agent.Host").Length > 0; k++) await Task.Delay(1000);
                    if (Process.GetProcessesByName("Curse.Agent.Host").Length > 0) Util.Log("curseforge agent host still running after the app quit; left alone");
                }
                else Util.Log("curseforge started by the hub is still running (its close action is " + CloseActionSetting() + "); left alone");
            }
            catch (Exception e) { Util.Log("curseforge close: " + e.Message); }
        }

        // storage.json: "closeCurseForgeAction":0 = hide to tray, 1 = exit (read only)
        static string CloseActionSetting()
        {
            try
            {
                var f = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "curseforge", "storage.json");
                if (!File.Exists(f)) return "unknown";
                string t; using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) using (var sr = new StreamReader(fs)) t = sr.ReadToEnd();
                var m = System.Text.RegularExpressions.Regex.Match(t, @"closeCurseForgeAction[\\]*""?\s*:\s*(\d)");
                return m.Success ? (m.Groups[1].Value == "1" ? "exit" : m.Groups[1].Value == "0" ? "hide to tray" : "other") : "unknown";
            }
            catch { return "unknown"; }
        }

        // CurseForge's main log (%APPDATA%\curseforge\logs\<session>\main-*.log): any install/download line in the last 15 s?
        static bool RecentInstallActivity()
        {
            try
            {
                var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "curseforge", "logs");
                if (Environment.GetEnvironmentVariable("ELANSHUB_CF_MAINLOGS") is string o && o.Length > 0) root = o;
                if (!Directory.Exists(root)) return false;
                var f = new DirectoryInfo(root).GetFiles("main-*.log", SearchOption.AllDirectories).OrderByDescending(x => x.LastWriteTimeUtc).FirstOrDefault();
                if (f == null || (DateTime.UtcNow - f.LastWriteTimeUtc).TotalSeconds > 15) return false;
                string text; using (var fs = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) using (var sr = new StreamReader(fs)) text = sr.ReadToEnd();
                var lines = text.Split((char)10);
                var now = DateTime.Now;
                for (int i = lines.Length - 1; i >= 0 && i >= lines.Length - 60; i--)
                {
                    var l = lines[i];
                    if (l.Length < 25 || l[0] != '[') continue;
                    if (!DateTime.TryParse(l.Substring(1, 23), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var ts)) continue;
                    if ((now - ts).TotalSeconds > 15) break;
                    if (l.IndexOf("install", StringComparison.OrdinalIgnoreCase) >= 0 || l.IndexOf("download", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                }
            }
            catch { }
            return false;
        }

        delegate bool EnumProc(IntPtr h, IntPtr l);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int n);

        // WM_CLOSE (posted: same as the window's X button) to the top-level windows of one process, hidden ones too
        static int PostCloseToWindows(int pid)
        {
            int n = 0;
            EnumWindows((h, l) =>
            {
                GetWindowThreadProcessId(h, out uint wp);
                if (wp != (uint)pid) return true;
                var sb = new StringBuilder(64); GetClassName(h, sb, 64);
                if (!sb.ToString().StartsWith("Chrome_WidgetWin")) return true;      // only Electron's real windows
                if (PostMessage(h, 0x0010, IntPtr.Zero, IntPtr.Zero)) n++;
                return true;
            }, IntPtr.Zero);
            return n;
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
