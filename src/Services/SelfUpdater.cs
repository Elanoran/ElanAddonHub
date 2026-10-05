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
        public static string ExePath => Process.GetCurrentProcess().MainModule.FileName;

        public static void CleanupOld()
        {
            try { var old = ExePath + ".old"; if (File.Exists(old)) File.Delete(old); } catch { }
        }

        public static bool IsNewer(HubInfo hub) =>
            hub != null && !string.IsNullOrEmpty(hub.Url) && Util.CompareVersions(hub.Version, App.Version) > 0;

        public static async Task UpdateAndRestart(HubInfo hub, IProgress<double> progress)
        {
            var exe = ExePath;
            var fresh = exe + ".new";
            await Net.Download(hub.Url, fresh, progress);
            if (!string.IsNullOrEmpty(hub.Sha256) && !string.Equals(Util.Sha256(fresh), hub.Sha256, StringComparison.OrdinalIgnoreCase))
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
