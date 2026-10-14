using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ElansAddonHub.Services;

namespace ElansAddonHub.Lodge
{
    // What the Lodge holds of the Classic baseline (server 2.17, dataset "classic").
    public sealed class ClassicHosted
    {
        public string Version, Sha256, UploadedBy;
        public long Size, At;
    }

    // The Lodge's hosted Classic data (server 2.17, feature "classic"): the server stores the original upstream bundle once for the whole Lodge; members download it,
    // owner and officers upload / replace / remove it. Info and removal are WebSocket messages (dataset.info / dataset.delete / dataset.data / dataset.changed /
    // dataset.result), the bytes go over HTTP (LodgeClient.UploadDataset / DownloadDataset). What to do with it lives in Services/Atlas/ClassicLodge.cs.
    // Nothing here runs against a server that doesn't list the "classic" feature, and guests are never asked.
    public partial class LodgeSession
    {
        public static readonly Version ClassicServer = new Version(2, 17, 0);
        public bool SupportsClassic { get; private set; }
        public bool CanShareClassic { get; private set; }               // owner and officers
        public bool ClassicKnown { get; private set; }                  // an answer arrived on this connection
        public ClassicHosted ClassicHosted { get; private set; }        // null = the Lodge holds none (or not known yet: see ClassicKnown)
        public int ClassicMaxMb { get; private set; } = 40;
        public string ClassicProblem { get; private set; }              // the last refusal of a change, in plain words
        public event Action ClassicChanged;
        LodgeClient classicClientOverride;                              // the self-test points this at a fake server

        public bool IsGuestNow => MyRole == "guest" || (Me != null && Me.Guest);

        void ReadClassicFeatures(Dictionary<string, object> m, List<string> features)
        {
            var v = ParseVersion(ServerVersion);
            SupportsClassic = features.Contains("classic") && (v == null || v >= ClassicServer);
            CanShareClassic = SupportsClassic && m.Bool("canShareClassic") && MyRole != "guest";
            ClassicKnown = false; ClassicHosted = null; ClassicProblem = null;
            if (SupportsClassic && MyRole != "guest") RequestClassicInfo();
        }

        void ResetClassic()
        {
            SupportsClassic = false; CanShareClassic = false; ClassicKnown = false; ClassicHosted = null; ClassicProblem = null;
            ClassicChanged?.Invoke();
        }

        public void RequestClassicInfo()
        {
            if (!SupportsClassic || IsGuestNow) return;
            _ = Send(new Dictionary<string, object> { ["t"] = "dataset.info", ["name"] = "classic" });
        }

        void OnDatasetFrame(Dictionary<string, object> m)
        {
            if (!SupportsClassic) return;
            switch (m.Str("t"))
            {
                case "dataset.data":
                case "dataset.changed":
                    if (m.Str("name") != "classic") return;
                    ApplyClassic(m);
                    break;
                case "dataset.result":
                    if (m.Str("name") != "classic" || m.Bool("ok")) return;
                    ClassicProblem = ClassicWhy(m.Str("why"));
                    ClassicChanged?.Invoke();
                    RequestClassicInfo();
                    break;
            }
        }

        void ApplyClassic(Dictionary<string, object> m)
        {
            if (m.ContainsKey("canManage")) CanShareClassic = m.Bool("canManage") && !IsGuestNow;
            if (m.Int("maxMb") > 0) ClassicMaxMb = m.Int("maxMb");
            ClassicHosted = m.Bool("available") && !string.IsNullOrEmpty(m.Str("version")) && (m.Str("sha256") ?? "").Length == 64
                ? new ClassicHosted { Version = m.Str("version"), Sha256 = m.Str("sha256").ToLowerInvariant(), Size = m.Long("size"), UploadedBy = DisplayNames.Shown(m.Str("uploadedBy") ?? ""), At = m.Long("at") }
                : null;
            ClassicKnown = true;
            ClassicProblem = null;
            ClassicChanged?.Invoke();
        }

        public static string ClassicWhy(string why)
        {
            switch (why)
            {
                case "forbidden": return "Only officers and the Lodge owner can change the Classic data of the Lodge";
                case "missing": return "The Lodge doesn't hold the Classic data";
                case "disabled": return "The Lodge owner has switched this off";
                case "save": return "The Lodge couldn't save that change - try again";
                default: return "The Lodge refused that";
            }
        }

        LodgeClient ClassicHttp => classicClientOverride ?? client;
        public void ClassicClientForTest(LodgeClient c) { classicClientOverride = c; }

        // Downloads the Lodge's bundle to `toFile` (via toFile + ".part"), checked against what the Lodge announced. null = fine, otherwise the plain reason.
        public async Task<string> FetchClassicAsync(string toFile, ClassicHosted expect, Action<double> progress, CancellationToken ct)
        {
            if (!SupportsClassic || IsGuestNow) return "The Lodge can't hand out the Classic data to you";
            var http = ClassicHttp;
            if (http == null || (!Online && classicClientOverride == null)) return "You are offline";
            var part = toFile + ".part";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(toFile));
                var d = await http.DownloadDataset("classic", part, expect.Size, progress, ct).ConfigureAwait(false);
                if (d.Size != expect.Size || !string.Equals(d.Sha256, expect.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(part); } catch { }
                    return "The data arrived damaged or the Lodge changed it meanwhile (the checksum doesn't match) - nothing was kept";
                }
                if (File.Exists(toFile)) File.Delete(toFile);
                File.Move(part, toFile);
                return null;
            }
            catch (OperationCanceledException) { try { File.Delete(part); } catch { } throw; }
            catch (Exception ex)
            {
                Util.Log("classic fetch: " + ex.GetType().Name);
                try { if (File.Exists(part)) File.Delete(part); } catch { }
                return ex is InvalidOperationException ? ex.Message : "Couldn't reach the Lodge: " + ex.Message;
            }
        }

        // Uploads a bundle (zip) as the Lodge's Classic data. null = fine, otherwise the plain reason.
        public async Task<string> ShareClassicAsync(string file, string version, string sha256, Action<double> progress)
        {
            if (!SupportsClassic) return "This Lodge's server can't host the Classic data yet";
            if (!CanShareClassic) return ClassicWhy("forbidden");
            var http = ClassicHttp;
            if (http == null || (!Online && classicClientOverride == null)) return "You are offline - try again when you are back";
            try
            {
                if (new FileInfo(file).Length > ClassicMaxMb * 1024L * 1024L) return "The Classic data is larger than this Lodge accepts (" + ClassicMaxMb + " MB)";
                await http.UploadDataset("classic", file, version, sha256, progress).ConfigureAwait(false);
                RequestClassicInfo();
                return null;
            }
            catch (Exception ex)
            {
                Util.Log("classic share: " + ex.GetType().Name);
                return ex is InvalidOperationException ? ex.Message : "Upload failed: " + ex.Message;
            }
        }

        // asks the Lodge to forget its Classic data (the answer arrives as dataset.result / dataset.changed)
        public string UnshareClassic()
        {
            if (!SupportsClassic) return "This Lodge's server can't host the Classic data yet";
            if (!CanShareClassic) return ClassicWhy("forbidden");
            _ = Send(new Dictionary<string, object> { ["t"] = "dataset.delete", ["name"] = "classic" });
            return null;
        }
    }
}
