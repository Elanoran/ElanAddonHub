using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ElansAddonHub.Services
{
    public static class Installer
    {
        static readonly string BackupDir = Path.Combine(Util.DataDir, "backups");

        // Version from the addon's .toc ("## Version: 1.17.0"), or null if not installed.
        public static string InstalledVersion(string root, AddonInfo a)
        {
            if (root == null || a.Folders == null || a.Folders.Count == 0) return null;
            if (PathSafety.AddonProblem(root, null, a) != null) return null;   // a name from the manifest that is not a plain folder name is never looked at
            var dir = Path.Combine(WowLocator.AddOnsDir(root, a.Flavor), a.Folders[0]);
            if (!Directory.Exists(dir)) return null;
            foreach (var toc in Directory.GetFiles(dir, "*.toc").OrderBy(f => f.Length))
            {
                var m = Regex.Match(File.ReadAllText(toc), @"^##\s*Version:\s*(.+)$", RegexOptions.Multiline);
                if (m.Success) return m.Groups[1].Value.Trim();
            }
            return "?";
        }

        // A git checkout is someone's development copy - never overwrite it.
        public static bool IsDevCopy(string root, AddonInfo a) =>
            root != null && a.Folders != null && PathSafety.AddonProblem(root, null, a) == null &&
            a.Folders.Any(f => Directory.Exists(Path.Combine(WowLocator.AddOnsDir(root, a.Flavor), f, ".git")));

        // replaceDevCopy: only after the user confirmed (a git checkout is usually someone's work in progress)
        public static async Task Install(string root, AddonInfo a, IProgress<double> progress, bool replaceDevCopy = false)
        {
            // the manifest names the folders, the id and the game version that become paths: only plain names, all inside AddOns / the backup folder,
            // or the whole install is refused before anything is touched
            var unsafeName = PathSafety.AddonProblem(root, BackupDir, a);
            if (unsafeName != null)
            {
                Util.Log($"install refused for {PathSafety.Show(a?.Id)}: {unsafeName}");
                throw new InvalidOperationException($"Install refused: {unsafeName}. Nothing on this PC was changed. The update information looks wrong - tell Elan.");
            }
            if (!replaceDevCopy && IsDevCopy(root, a)) throw new InvalidOperationException("This is a development copy (git) - not touching it.");
            var addOns = WowLocator.AddOnsDir(root, a.Flavor);
            if (!WowLocator.HasFlavor(root, a.Flavor))
                throw new InvalidOperationException($"No {a.FlavorName ?? a.Flavor} client found in this WoW folder.");
            Directory.CreateDirectory(addOns);

            var work = Path.Combine(Path.GetTempPath(), "ElansAddonHub-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            try
            {
                var zip = Path.Combine(work, "addon.zip");
                await Net.Download(a.Url, zip, new Progress<double>(p => progress?.Report(p * 0.8)), trustedOnly: true);   // https + GitHub only (also after redirects)
                // a manifest from a release carries the checksum and it is then mandatory; an older manifest without one is installed with a warning in the log
                if (string.IsNullOrEmpty(a.Sha256)) Util.Log($"WARNING: the manifest has no checksum for {a.Id} {a.Version} - installed without verification");
                else if (!Net.IsSha256(a.Sha256)) throw new InvalidDataException("The update information has a malformed checksum - nothing was installed.");
                else if (!string.Equals(Util.Sha256(zip), a.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Download is damaged (checksum mismatch) - try again.");

                var unpacked = Path.Combine(work, "unpacked");
                await Task.Run(() => ZipFile.ExtractToDirectory(zip, unpacked));
                foreach (var f in a.Folders)
                    if (!Directory.Exists(Path.Combine(unpacked, f)))
                        throw new InvalidDataException($"The download doesn't contain the {f} folder.");
                progress?.Report(0.85);

                var installed = InstalledVersion(root, a) ?? "none";
                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                await Task.Run(() =>
                {
                    foreach (var f in a.Folders)
                    {
                        var dest = Path.Combine(addOns, f);
                        if (!PathSafety.IsInside(addOns, dest)) throw new InvalidOperationException("Install refused: a folder would end up outside the AddOns folder.");   // belt and braces
                        string backup = null;
                        if (Directory.Exists(dest))
                        {
                            backup = Path.Combine(BackupDir, a.Id, $"{installed}-{stamp}", f);
                            if (!PathSafety.IsInside(BackupDir, backup)) throw new InvalidOperationException("Install refused: a backup would end up outside the backup folder.");
                            Util.CopyDir(dest, backup);
                            Util.DeleteDir(dest);
                        }
                        try { Util.CopyDir(Path.Combine(unpacked, f), dest); }
                        catch
                        {
                            // put the old version back so the addon is never left half-installed
                            if (backup != null) { Util.DeleteDir(dest); Util.CopyDir(backup, dest); }
                            throw;
                        }
                    }
                    PruneBackups(a.Id, 2);
                });
                progress?.Report(1);
                Util.Log($"installed {a.Id} {a.Version} (was {installed})");
            }
            finally
            {
                try { Util.DeleteDir(work); } catch { }
            }
        }

        static void PruneBackups(string id, int keep)
        {
            var dir = Path.Combine(BackupDir, id);
            if (!Directory.Exists(dir)) return;
            var keepPaths = new HashSet<string>(Backups(id).Take(keep).Select(b => b.Path));
            foreach (var d in new DirectoryInfo(dir).GetDirectories())
                if (!keepPaths.Contains(d.FullName)) try { Util.DeleteDir(d.FullName); } catch { }
        }

        public class BackupInfo { public string Path; public string Version; public DateTime Stamp; }

        // backup folders are named "<version>-<yyyyMMdd-HHmmss>"; newest first, the "none" ones (nothing was installed) are skipped
        public static List<BackupInfo> Backups(string id)
        {
            var dir = Path.Combine(BackupDir, id);
            var list = new List<BackupInfo>();
            if (!Directory.Exists(dir)) return list;
            foreach (var d in new DirectoryInfo(dir).GetDirectories())
            {
                var m = Regex.Match(d.Name, @"^(.*)-(\d{8}-\d{6})$");
                if (!m.Success || m.Groups[1].Value == "none") continue;
                DateTime.TryParseExact(m.Groups[2].Value, "yyyyMMdd-HHmmss", null, System.Globalization.DateTimeStyles.None, out var t);
                list.Add(new BackupInfo { Path = d.FullName, Version = m.Groups[1].Value, Stamp = t });
            }
            return list.OrderByDescending(b => b.Stamp).ToList();
        }

        // Put a backed-up version back (folders swapped, WTF untouched); the used backup is dropped so the next click goes one further back.
        public static void Rollback(string root, AddonInfo a, BackupInfo b)
        {
            if (IsDevCopy(root, a)) throw new InvalidOperationException("This is a development copy (git) - not touching it.");
            var unsafeName = PathSafety.AddonProblem(root, BackupDir, a);
            if (unsafeName != null) throw new InvalidOperationException($"Roll back refused: {unsafeName}. Nothing on this PC was changed.");
            if (b == null || !PathSafety.IsInside(BackupDir, b.Path)) throw new InvalidOperationException("Roll back refused: that backup is not in the backup folder.");
            foreach (var d in Directory.GetDirectories(b.Path))
            {
                var bad = PathSafety.FolderProblem(Path.GetFileName(d));
                if (bad != null) throw new InvalidOperationException($"Roll back refused: {bad}. Nothing on this PC was changed.");
            }
            var addOns = WowLocator.AddOnsDir(root, a.Flavor);
            var oldRoot = Path.Combine(addOns, ".elanshub-old-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var moved = new List<string>();
            try
            {
                Directory.CreateDirectory(oldRoot);
                foreach (var dir in Directory.GetDirectories(b.Path))
                {
                    var f = Path.GetFileName(dir);
                    var dest = Path.Combine(addOns, f);
                    if (Directory.Exists(dest)) { Directory.Move(dest, Path.Combine(oldRoot, f)); moved.Add(f); }
                    Util.CopyDir(dir, dest);
                }
            }
            catch (Exception ex)
            {
                foreach (var f in moved)
                    try { var dest = Path.Combine(addOns, f); if (Directory.Exists(dest)) Util.DeleteDir(dest); Directory.Move(Path.Combine(oldRoot, f), dest); } catch { }
                throw new IOException("Couldn't roll back (is WoW running? close it and try again). " + ex.Message);
            }
            finally { try { Util.DeleteDir(oldRoot); } catch { } }
            Util.DeleteDir(b.Path);
            Util.Log($"rolled back {a.Id} to {b.Version}");
        }

        // a hostile or broken manifest cannot make the installer touch anything outside AddOns; downloads must be https from GitHub; the self-update needs a hash
        public static async Task<string> SafetySelfTest(string dir)
        {
            var sb = new System.Text.StringBuilder();
            void Ck(string what, bool ok, string info = null) => sb.AppendLine($"install safety {what}: {(ok ? "ok" : "FAIL")}{(info == null ? "" : " (" + info + ")")}");
            var wasDev = Net.AllowDevSources;
            try
            {
                var root = Path.Combine(dir, "safetyroot");
                if (Directory.Exists(root)) Util.DeleteDir(root);
                var addOns = WowLocator.AddOnsDir(root, "_classic_beta_");
                Directory.CreateDirectory(addOns);
                // a folder that must survive: next to AddOns (reachable with ..\..) and an absolute one
                var victim = Path.Combine(root, "_classic_beta_", "Interface", "victim");
                var outsider = Path.Combine(dir, "safety-outsider");
                foreach (var v in new[] { victim, outsider }) { if (Directory.Exists(v)) Util.DeleteDir(v); Directory.CreateDirectory(v); File.WriteAllText(Path.Combine(v, "keep.txt"), "keep"); }
                var zip = Path.Combine(dir, "safety.zip");
                if (File.Exists(zip)) File.Delete(zip);
                using (var z = ZipFile.Open(zip, ZipArchiveMode.Create)) { var en = z.CreateEntry("Safe/Safe.toc"); using (var w = new StreamWriter(en.Open())) w.Write("## Version: 1\n"); }
                var sha = Util.Sha256(zip);
                AddonInfo Mk(string id, string flavor, params string[] folders) => new AddonInfo { Id = id, Name = id, Version = "1", Url = zip, Sha256 = sha, Flavor = flavor, Folders = folders.ToList() };

                Net.AllowDevSources = true;   // the local zip stands in for a download
                async Task<string> Try(AddonInfo a) { try { await Install(root, a, null, replaceDevCopy: true); return null; } catch (Exception ex) { return ex.Message; } }
                var r1 = await Try(Mk("Safe", "_classic_beta_", "..\\victim"));
                Ck("a ..\\ folder is refused", r1 != null && r1.Contains("refused") && r1.Contains("Nothing"), r1);
                Ck("the folder it pointed at is untouched", File.Exists(Path.Combine(victim, "keep.txt")));
                var r2 = await Try(Mk("Safe", "_classic_beta_", outsider));
                Ck("an absolute folder is refused", r2 != null && r2.Contains("refused"), r2);
                Ck("the absolute folder is untouched", File.Exists(Path.Combine(outsider, "keep.txt")));
                var r3 = await Try(Mk("..\\..\\evil", "_classic_beta_", "Safe"));
                Ck("a traversal in the id is refused", r3 != null && r3.Contains("refused"), r3);
                var r4 = await Try(Mk("Safe", "..\\..\\x", "Safe"));
                Ck("a traversal in the game version is refused", r4 != null && r4.Contains("refused"), r4);
                var r5 = await Try(Mk("Safe", "_classic_beta_", "Safe", "CON"));
                Ck("a reserved name is refused and the whole install with it", r5 != null && r5.Contains("refused") && !Directory.Exists(Path.Combine(addOns, "Safe")), r5);
                var r6 = await Try(Mk("Safe", "_classic_beta_", new string('x', 80)));
                Ck("a too long name is refused", r6 != null && r6.Contains("refused"), r6);
                var r7 = await Try(Mk("Safe", "_classic_beta_"));
                Ck("an entry with no folders is refused", r7 != null && r7.Contains("refused"), r7);
                Ck("nothing was created by any of them", Directory.GetDirectories(addOns).Length == 0, string.Join(",", Directory.GetDirectories(addOns)));
                Ck("a normal entry installs", await Try(Mk("Safe", "_classic_beta_", "Safe")) == null && InstalledVersion(root, Mk("Safe", "_classic_beta_", "Safe")) == "1");
                Ck("rollback refuses a backup outside the backup folder", ThrowsMsg(() => Rollback(root, Mk("Safe", "_classic_beta_", "Safe"), new BackupInfo { Path = outsider, Version = "0" })).Contains("refused"));
                Ck("InstalledVersion ignores an unsafe entry", InstalledVersion(root, Mk("Safe", "_classic_beta_", "..\\victim")) == null);

                // checksums: a wrong one is damaged, a malformed one refused, none = installed with a warning (older manifests)
                var bad = Mk("Safe", "_classic_beta_", "Safe"); bad.Sha256 = new string('0', 64);
                var rb = await Try(bad);
                Ck("a wrong checksum is refused", rb != null && rb.Contains("damaged"), rb);
                var mal = Mk("Safe", "_classic_beta_", "Safe"); mal.Sha256 = "xyz";
                var rm = await Try(mal);
                Ck("a malformed checksum is refused", rm != null && rm.Contains("malformed"), rm);
                var none = Mk("Safe", "_classic_beta_", "Safe"); none.Sha256 = null;
                Ck("an older manifest without a checksum still installs", await Try(none) == null);

                // where an update may come from
                Net.AllowDevSources = false;
                Ck("https://github.com release url ok", Net.UpdateUrlProblem("https://github.com/Elanoran/ElanAddonHub/releases/download/v1.0/ElansAddonHub.exe") == null);
                Ck("objects.githubusercontent.com ok", Net.UpdateUrlProblem("https://objects.githubusercontent.com/github-production-release-asset/1/2?x=1") == null);
                Ck("release-assets.githubusercontent.com ok", Net.UpdateUrlProblem("https://release-assets.githubusercontent.com/github-production-release-asset/1/2") == null);
                foreach (var badUrl in new[] { "http://github.com/a/b.exe", "https://evil.example/github.com/a.exe", "https://github.com.evil.example/a.exe", "https://notgithub.com/a.exe", "https://githubusercontent.com.evil.example/a",
                    "https://github.com@evil.example/a.exe", "ftp://github.com/a.exe", "C:\\Users\\x\\a.exe", "\\\\server\\share\\a.exe", "file:///C:/a.exe", "http://127.0.0.1:1/a.exe", "github.com/a.exe", "", null, "   " })
                    Ck("update url refused: " + (badUrl ?? "null"), Net.UpdateUrlProblem(badUrl) != null);
                string ex1 = null; try { await Net.Download(zip, Path.Combine(dir, "never.bin"), null, trustedOnly: true); } catch (Exception ex) { ex1 = ex.Message; }
                Ck("a trusted-only download of a local path is refused and writes nothing", ex1 != null && !File.Exists(Path.Combine(dir, "never.bin")), ex1);
                var rl = await Try(Mk("Safe", "_classic_beta_", "Safe"));
                Ck("an addon update from a local path is refused without the developer switch", rl != null && rl.Contains("not https"), rl);

                // the self-update: https + GitHub + a valid hash, no exception
                var good = new HubInfo { Version = "9.9.9", Url = "https://github.com/Elanoran/ElanAddonHub/releases/download/v9.9.9/ElansAddonHub.exe", Sha256 = new string('a', 64) };
                Ck("self-update: a complete entry passes the checks", SelfUpdater.Problem(good) == null, SelfUpdater.Problem(good));
                Ck("self-update: no hash is refused", SelfUpdater.Problem(new HubInfo { Version = "9.9.9", Url = good.Url }) != null);
                Ck("self-update: an empty hash is refused", SelfUpdater.Problem(new HubInfo { Version = "9.9.9", Url = good.Url, Sha256 = "" }) != null);
                Ck("self-update: a short hash is refused", SelfUpdater.Problem(new HubInfo { Version = "9.9.9", Url = good.Url, Sha256 = "abc123" }) != null);
                Ck("self-update: a non-hex hash is refused", SelfUpdater.Problem(new HubInfo { Version = "9.9.9", Url = good.Url, Sha256 = new string('g', 64) }) != null);
                Ck("self-update: http is refused", SelfUpdater.Problem(new HubInfo { Version = "9.9.9", Url = good.Url.Replace("https:", "http:"), Sha256 = good.Sha256 }) != null);
                Ck("self-update: another host is refused", SelfUpdater.Problem(new HubInfo { Version = "9.9.9", Url = "https://example.org/ElansAddonHub.exe", Sha256 = good.Sha256 }) != null);
                string ex2 = null; try { await SelfUpdater.UpdateAndRestart(new HubInfo { Version = "9.9.9", Url = good.Url }, null); } catch (Exception ex) { ex2 = ex.Message; }
                Ck("self-update: UpdateAndRestart throws before downloading anything when the hash is missing", ex2 != null && ex2.Contains("checksum") && !File.Exists(SelfUpdater.ExePath + ".new"), ex2);
                Ck("IsSha256: 64 hex only", Net.IsSha256(new string('0', 64)) && Net.IsSha256(new string('F', 64)) && !Net.IsSha256(new string('0', 63)) && !Net.IsSha256(new string('0', 65)) && !Net.IsSha256(null) && !Net.IsSha256(new string(' ', 64)));
                try { Util.DeleteDir(Path.Combine(BackupDir, "Safe")); } catch { }
            }
            catch (Exception e) { sb.AppendLine("install safety test: FAIL " + e); }
            finally { Net.AllowDevSources = wasDev; }
            return sb.ToString().TrimEnd();
        }

        static string ThrowsMsg(Action act) { try { act(); return ""; } catch (Exception e) { return e.Message; } }

        // fake WoW root + local zips: install v1, v2, v3 (keeps 2 backups), roll back twice, WTF untouched
        public static async Task<string> SelfTest(string dir)
        {
            var sb = new System.Text.StringBuilder();
            void Ck(string what, bool ok, string info = null) => sb.AppendLine($"rollback {what}: {(ok ? "ok" : "FAIL")}{(info == null ? "" : " (" + info + ")")}");
            try
            {
                var root = Path.Combine(dir, "rbroot");
                if (Directory.Exists(root)) Util.DeleteDir(root);
                var addOns = WowLocator.AddOnsDir(root, "_classic_beta_");
                Directory.CreateDirectory(addOns);
                var wtf = Path.Combine(root, "WTF", "keep.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(wtf)); File.WriteAllText(wtf, "keep");
                var id = "RbTest" + Guid.NewGuid().ToString("N").Substring(0, 6);
                AddonInfo Make(string v)
                {
                    var zip = Path.Combine(dir, id + "-" + v + ".zip");
                    if (File.Exists(zip)) File.Delete(zip);
                    using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
                    {
                        var en = z.CreateEntry(id + "/" + id + ".toc"); using (var w = new StreamWriter(en.Open())) w.Write("## Interface: 16001\n## Version: " + v + "\n");
                    }
                    return new AddonInfo { Id = id, Name = id, Version = v, Url = zip, Sha256 = Util.Sha256(zip), Flavor = "_classic_beta_", Folders = new System.Collections.Generic.List<string> { id } };
                }
                AddonInfo a1 = Make("1.0.0"), a2 = Make("2.0.0"), a3 = Make("3.0.0");
                await Install(root, a1, null);
                await Task.Delay(1100);   // backup stamps have 1 s resolution
                await Install(root, a2, null);
                await Task.Delay(1100);
                await Install(root, a3, null);
                var bs = Backups(id);
                Ck("keeps the last 2 versions", InstalledVersion(root, a3) == "3.0.0" && bs.Count == 2 && bs[0].Version == "2.0.0" && bs[1].Version == "1.0.0",
                    string.Join(",", bs.Select(b => b.Version)));
                Rollback(root, a3, bs[0]);
                Ck("roll back to previous", InstalledVersion(root, a3) == "2.0.0" && Backups(id).Count == 1 && Backups(id)[0].Version == "1.0.0");
                Rollback(root, a3, Backups(id)[0]);
                Ck("second roll back goes further", InstalledVersion(root, a3) == "1.0.0" && Backups(id).Count == 0);
                Ck("WTF untouched, no leftovers", File.ReadAllText(wtf) == "keep" && Directory.GetDirectories(addOns, ".elanshub-*").Length == 0);
                Directory.CreateDirectory(Path.Combine(addOns, id, ".git"));
                string err = null; try { Rollback(root, a3, new BackupInfo { Path = dir }); } catch (Exception ex) { err = ex.Message; }
                Ck("dev copy refused", err != null && err.Contains("development copy"));
                try { Util.DeleteDir(Path.Combine(BackupDir, id)); } catch { }
            }
            catch (Exception e) { sb.AppendLine("rollback test: FAIL " + e); }
            return sb.ToString().TrimEnd();
        }
    }
}
