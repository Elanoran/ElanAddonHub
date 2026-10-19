using System;
using System.Text.Json.Nodes;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ElansAddonHub.Services;
using Elans.Shared;

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

        void ReadClassicFeatures(WelcomeMsg w, List<string> features)
        {
            var v = ParseVersion(ServerVersion);
            SupportsClassic = features.Contains("classic") && (v == null || v >= ClassicServer);
            CanShareClassic = SupportsClassic && w.CanShareClassic && MyRole != "guest";
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
            _ = Send(LodgeJson.Build(new DatasetInfoMsg { Name = "classic" }.Write));
        }

        // the messages are typed in shared/Elans.Shared/Lodge/DatasetMessages.cs
        void OnDatasetFrame(JsonObject m)
        {
            if (!SupportsClassic) return;
            switch (LodgeJson.Str(m, "t"))
            {
                case LodgeMsg.DatasetData:
                case LodgeMsg.DatasetChanged:
                {
                    // dataset.changed carries the same view (plus its action) and normally neither canManage nor maxMb: read through
                    // DatasetDataMsg, either one is applied only when the frame has it, as before
                    var d = DatasetDataMsg.Read(new LodgeJsonFields(m));
                    if (d.Dataset.Name != "classic") return;
                    ApplyClassic(d.Dataset, d.CanManage, d.MaxMb);
                    break;
                }
                case LodgeMsg.DatasetResult:
                {
                    var r = DatasetResultMsg.Read(new LodgeJsonFields(m));
                    if (r.Name != "classic" || r.Ok) return;
                    ClassicProblem = ClassicWhy(r.Why);
                    ClassicChanged?.Invoke();
                    RequestClassicInfo();
                    break;
                }
            }
        }

        // canManage: null = not in the frame (keep what was known); maxMb: 0 = not in the frame
        void ApplyClassic(DatasetView v, bool? canManage, int maxMb)
        {
            if (canManage is bool can) CanShareClassic = can && !IsGuestNow;
            if (maxMb > 0) ClassicMaxMb = maxMb;
            ClassicHosted = v.Available && !string.IsNullOrEmpty(v.Version) && (v.Sha256 ?? "").Length == 64
                ? new ClassicHosted { Version = v.Version, Sha256 = v.Sha256.ToLowerInvariant(), Size = v.Size, UploadedBy = DisplayNames.Shown(v.UploadedBy ?? ""), At = v.At }
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
            _ = Send(LodgeJson.Build(new DatasetDeleteMsg { Name = "classic" }.Write));
            return null;
        }
    }
}
