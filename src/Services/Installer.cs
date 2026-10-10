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
            root != null && a.Folders != null &&
            a.Folders.Any(f => Directory.Exists(Path.Combine(WowLocator.AddOnsDir(root, a.Flavor), f, ".git")));

        // replaceDevCopy: only after the user confirmed (a git checkout is usually someone's work in progress)
        public static async Task Install(string root, AddonInfo a, IProgress<double> progress, bool replaceDevCopy = false)
        {
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
                await Net.Download(a.Url, zip, new Progress<double>(p => progress?.Report(p * 0.8)));
                if (!string.IsNullOrEmpty(a.Sha256) && !string.Equals(Util.Sha256(zip), a.Sha256, StringComparison.OrdinalIgnoreCase))
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
                        string backup = null;
                        if (Directory.Exists(dest))
                        {
                            backup = Path.Combine(BackupDir, a.Id, $"{installed}-{stamp}", f);
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
