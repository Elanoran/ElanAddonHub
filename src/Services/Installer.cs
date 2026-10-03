using System;
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
                    PruneBackups(a.Id, 3);
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
            foreach (var old in new DirectoryInfo(dir).GetDirectories().OrderByDescending(d => d.CreationTimeUtc).Skip(keep))
                try { Util.DeleteDir(old.FullName); } catch { }
        }
    }
}
