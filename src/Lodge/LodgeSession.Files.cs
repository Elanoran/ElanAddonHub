using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ElansAddonHub.Services;

namespace ElansAddonHub.Lodge
{
    // Shared files (Lodge server 2.14, feature "files"): named slots, each with up to 3 kept versions. Officers and the owner upload, replace
    // (= a new version), roll back and delete; everybody lists and saves. The list, delete and roll back are WebSocket messages; the bytes go over
    // HTTP (LodgeClient.UploadSlot / DownloadSlot). Nothing here runs against a server that doesn't list the "files" feature.
    public partial class LodgeSession
    {
        public bool SupportsFiles { get; private set; }
        public bool CanManageFiles { get; private set; }
        public int FilesMaxMb { get; private set; } = 50;
        public int FilesTotalMb { get; private set; } = 500;
        public long FilesUsedBytes { get; private set; }
        public bool FilesLoaded { get; private set; }
        public readonly ObservableCollection<SlotVM> Slots = new ObservableCollection<SlotVM>();
        public event Action FilesChanged;                 // the list, a badge or a permission changed
        public event Action<string> FilesNotice;          // "Wing updated Raid UI pack (v3)"
        public string FilesNoticeText { get; private set; } = "";
        public string FilesNoticeFor { get; private set; }   // the slot the notice is about
        SavedFiles savedFiles;
        DateTime filesLastRequest = DateTime.MinValue;
        LodgeClient filesClientOverride;                   // the self-test points this at a fake server

        SavedFiles Saved() => savedFiles ?? (savedFiles = new SavedFiles(Settings?.LodgeUrl));

        void ReadFilesFeatures(Dictionary<string, object> m, List<string> features)
        {
            var v = ParseVersion(ServerVersion);
            SupportsFiles = features.Contains("files") && (v == null || v >= LodgeFiles.Server);
            CanManageFiles = SupportsFiles && m.Bool("canManageFiles");
            if (m.Int("filesMaxMb") > 0) FilesMaxMb = m.Int("filesMaxMb");
            savedFiles = null;
            if (!SupportsFiles) { Slots.Clear(); FilesLoaded = false; }
        }

        void ResetFiles()
        {
            SupportsFiles = false; CanManageFiles = false; FilesLoaded = false; savedFiles = null;
            Slots.Clear(); FilesNoticeText = ""; FilesNoticeFor = null; FilesUsedBytes = 0;
            filesLastRequest = DateTime.MinValue;
        }

        public void RequestFiles()
        {
            if (!SupportsFiles) return;
            filesLastRequest = Clock();
            _ = Send(new Dictionary<string, object> { ["t"] = "files.list" });
        }

        void OnFilesFrame(Dictionary<string, object> m)
        {
            if (!SupportsFiles) return;
            switch (m.Str("t"))
            {
                case "files.data": ApplyFilesData(m); break;
                case "files.changed": OnFilesChanged(m); break;
                case "files.result": OnFilesResult(m); break;
            }
        }

        void ApplyFilesData(Dictionary<string, object> m)
        {
            CanManageFiles = m.Bool("canManage");
            if (m.Int("maxMb") > 0) FilesMaxMb = m.Int("maxMb");
            if (m.Int("totalMb") > 0) FilesTotalMb = m.Int("totalMb");
            FilesUsedBytes = m.Long("usedBytes");
            var incoming = (m.List("slots") ?? new List<object>()).OfType<Dictionary<string, object>>().Where(d => SafeFile.Id(d.Str("id")) != null).ToList();
            // the same objects stay (a progress bar must survive a refresh), newcomers are added, deleted slots go
            foreach (var gone in Slots.Where(s => incoming.All(d => d.Str("id") != s.Id)).ToList()) Slots.Remove(gone);
            for (int i = 0; i < incoming.Count; i++)
            {
                var d = incoming[i];
                var have = Slots.FirstOrDefault(s => s.Id == d.Str("id"));
                if (have == null) { have = new SlotVM(); Slots.Insert(Math.Min(i, Slots.Count), have); }
                have.Update(d, CanManageFiles, Saved().Get(d.Str("id")));
            }
            FilesLoaded = true;
            FilesChanged?.Invoke();
        }

        void OnFilesChanged(Dictionary<string, object> m)
        {
            var by = m.Str("by") ?? "";
            var text = LodgeFiles.NoticeText(m.Str("action"), DisplayNames.Shown(by), Names(m.Str("name")), m.Int("v"));
            if (text != null && !string.Equals(by, Me?.Name, StringComparison.OrdinalIgnoreCase))
            {
                FilesNoticeText = text; FilesNoticeFor = m.Str("slot");
                FilesNotice?.Invoke(text);
            }
            // refresh the list, unless one was just asked for (my own upload asks right after it)
            if (Clock() - filesLastRequest > TimeSpan.FromMilliseconds(400)) RequestFiles();
        }

        static string Names(string s) => string.IsNullOrWhiteSpace(s) ? "a file" : s.Trim();

        void OnFilesResult(Dictionary<string, object> m)
        {
            if (m.Bool("ok")) return;
            var slot = Slots.FirstOrDefault(s => s.Id == m.Str("slot"));
            var text = FilesWhy(m.Str("why"));
            if (slot != null) slot.Error = text;
            Error?.Invoke(text);
            RequestFiles();
        }

        public static string FilesWhy(string why)
        {
            switch (why)
            {
                case "forbidden": return "Only officers and the lodge owner can change shared files";
                case "missing": return "That file isn't there any more";
                case "disabled": return "File sharing is switched off on this lodge";
                case "already-newest": return "That is already the newest version";
                case "save": return "The lodge couldn't save that change - try again";
                case "rate": return "Slow down a little";
                default: return "The lodge refused that";
            }
        }

        LodgeClient FilesHttp => filesClientOverride ?? client;
        public void FilesClientForTest(LodgeClient c) { filesClientOverride = c; }

        // ---- actions. Upload / save return null on success, otherwise the text to show.

        // slotId null = a new slot called `name`; otherwise a new version of that slot
        public async Task<string> UploadSlotAsync(string slotId, string name, string path, string note, Action<double> progress = null)
        {
            if (!SupportsFiles) return "This lodge doesn't share files";
            if (!CanManageFiles) return FilesWhy("forbidden");
            var http = FilesHttp;
            if (http == null || (!Online && filesClientOverride == null)) return "You are offline - try again when you are back";
            name = (name ?? "").Trim();
            if (slotId == null && (name.Length < 1 || name.Length > LodgeFiles.MaxNameChars)) return "Give the file a name of 1 to " + LodgeFiles.MaxNameChars + " characters";
            if (slotId == null && Slots.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))) return "A slot with that name exists - use Replace on it";
            note = (note ?? "").Trim();
            if (note.Length > LodgeFiles.MaxNoteChars) return "The note can be " + LodgeFiles.MaxNoteChars + " characters at most";
            var refusal = LodgeFiles.CheckUpload(path, FilesMaxMb);
            if (refusal != null) return refusal;
            var slot = slotId == null ? null : Slots.FirstOrDefault(s => s.Id == slotId);
            if (slotId != null && slot == null) return FilesWhy("missing");
            try
            {
                if (slot != null) { slot.Error = ""; slot.BusyText = "Uploading..."; slot.Progress = 0; }
                using (Activity.Begin("Uploading " + name + " to your Lodge", Activity.Kind.Save))
                {
                    var sha = await Task.Run(() => LodgeFiles.Sha256(path));
                    await http.UploadSlot(slotId, name, path, note, sha, p => { if (slot != null) slot.Progress = p; progress?.Invoke(p); });
                }
                RequestFiles();
                return null;
            }
            catch (Exception ex)
            {
                Util.Log("files upload: " + ex.GetType().Name);
                return ex is InvalidOperationException ? ex.Message : "Upload failed: " + ex.Message;
            }
            finally { slot?.SetIdle(); }
        }

        // downloads one version to `dest`: into dest + ".part" first, checked against the announced SHA-256 and size, then moved into place and
        // marked as downloaded (Mark of the Web). A file that doesn't match is deleted and never appears under its real name.
        public async Task<string> SaveVersionAsync(SlotVM slot, SlotVersionInfo ver, string dest, Action<double> progress = null)
        {
            if (!SupportsFiles || slot == null || ver == null) return "That file isn't there any more";
            var http = FilesHttp;
            if (http == null || (!Online && filesClientOverride == null)) return "You are offline - try again when you are back";
            var part = dest + ".part";
            try
            {
                slot.Error = ""; slot.BusyText = "Saving..."; slot.Progress = 0;
                var sha = await http.DownloadSlot(slot.Id, ver.V, part, ver.Size, p => { slot.Progress = p; progress?.Invoke(p); });
                long size = new FileInfo(part).Length;
                if (size != ver.Size || !string.Equals(sha, ver.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(part); } catch { }
                    return "The file arrived damaged (the checksum doesn't match) - nothing was saved. Try again.";
                }
                if (File.Exists(dest)) File.Delete(dest);
                File.Move(part, dest);
                if (!LodgeFiles.MarkOfTheWeb(dest)) Util.Log("files: the Mark of the Web could not be written (not an NTFS drive?)");
                Saved().Set(slot.Id, ver.V, ver.Sha256);
                slot.Badge = LodgeFiles.Badge(slot.Newest, Saved().Get(slot.Id));
                FilesChanged?.Invoke();
                return null;
            }
            catch (Exception ex)
            {
                Util.Log("files save: " + ex.GetType().Name);
                try { if (File.Exists(part)) File.Delete(part); } catch { }
                return ex is InvalidOperationException ? ex.Message : "Couldn't save the file: " + ex.Message;
            }
            finally { slot.SetIdle(); }
        }

        public string DeleteSlot(SlotVM slot)
        {
            if (!SupportsFiles || slot == null) return "That file isn't there any more";
            if (!CanManageFiles) return FilesWhy("forbidden");
            _ = Send(new Dictionary<string, object> { ["t"] = "files.delete", ["slot"] = slot.Id });
            return null;
        }

        public string RollBackSlot(SlotVM slot, int version)
        {
            if (!SupportsFiles || slot == null) return "That file isn't there any more";
            if (!CanManageFiles) return FilesWhy("forbidden");
            _ = Send(new Dictionary<string, object> { ["t"] = "files.rollback", ["slot"] = slot.Id, ["v"] = version });
            return null;
        }

        public void ClearFilesNotice() { FilesNoticeText = ""; FilesNoticeFor = null; FilesChanged?.Invoke(); }
        public SavedRecord SavedOfForTest(string slotId) => Saved().Get(slotId);
    }
}
