using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace ElansAddonHub.Services
{
    // Replaces the running exe: download next to it, rename the running file to .old
    // (Windows allows renaming a running exe), move the new one in and restart.
    public static class SelfUpdater
    {
        public static string ExePath => Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule.FileName; // the single-file exe itself

        public static void CleanupOld()
        {
            try { var old = ExePath + ".old"; if (File.Exists(old)) File.Delete(old); } catch { }
        }

        public static bool IsNewer(HubInfo hub) =>
            hub != null && !string.IsNullOrEmpty(hub.Url) && Util.CompareVersions(hub.Version, App.Version) > 0;

        // Why this update must not be installed, or null. The address must be https on a GitHub host and the manifest must carry a valid SHA-256 of the exe:
        // without a hash the file cannot be checked, and this exe replaces the running program - so there is no "install anyway" for it.
        public static string Problem(HubInfo hub)
        {
            if (hub == null || string.IsNullOrEmpty(hub.Url)) return "There is no update to install.";
            var bad = Net.UpdateUrlProblem(hub.Url);
            if (bad != null) return bad + " Update refused.";
            if (string.IsNullOrEmpty(hub.Sha256)) return "This update has no checksum (SHA-256) in its release information, so it cannot be checked and was refused. Download the new version by hand from the release page.";
            if (!Net.IsSha256(hub.Sha256)) return "This update has a malformed checksum in its release information, so it was refused. Download the new version by hand from the release page.";
            return null;
        }

        public static async Task UpdateAndRestart(HubInfo hub, IProgress<double> progress)
        {
            var problem = Problem(hub);
            if (problem != null) { Util.Log("self-update refused: " + problem); throw new InvalidOperationException(problem); }
            var exe = ExePath;
            var fresh = exe + ".new";
            await Net.Download(hub.Url, fresh, progress, trustedOnly: true);
            if (!string.Equals(Util.Sha256(fresh), hub.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(fresh);
                throw new InvalidDataException("The download is damaged (checksum mismatch).");
            }
            var old = exe + ".old";
            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);
            File.Move(fresh, exe);
            Util.Log($"hub updated {App.Version} -> {hub.Version}");
            // the new hub waits for this one to finish quitting (the caller quits right after)
            Process.Start(new ProcessStartInfo(exe, "--updated") { UseShellExecute = false });
        }
    }
}
