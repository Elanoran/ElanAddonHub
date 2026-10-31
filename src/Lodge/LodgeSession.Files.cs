using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ElansAddonHub.Services;
using Elans.Shared;

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
        InstalledAddons installedAddons;
        InstalledAddons Installed() => installedAddons ?? (installedAddons = new InstalledAddons(Settings?.LodgeUrl));
        public bool SupportsAddonFiles { get; private set; }     // 2.32 "filesaddons": slots can be marked as WoW addons and installed
        public bool SupportsFiles2 { get; private set; }         // 2.33 "files2": who sees / updates a slot, description, cover picture, files.edit
        public bool SupportsFilesText { get; private set; }      // 2.34 "filestext": text slots (import strings) with a Copy button
        public bool SupportsFilesInfo { get; private set; }      // 2.35 "filesinfo": addon versions carry their tocs; Outposts say what they have installed (counts only)
        public bool SupportsFilesKits { get; private set; }      // 2.36 "fileskits": kits - named sets of files
        public readonly ObservableCollection<KitVM> Kits = new ObservableCollection<KitVM>();
        readonly HashSet<string> installReported = new HashSet<string>(StringComparer.Ordinal);   // "slot:v" told to this Lodge on this connection

        void ReadFilesFeatures(WelcomeMsg w, List<string> features)
        {
            var v = ParseVersion(ServerVersion);
            SupportsFiles = features.Contains("files") && (v == null || v >= LodgeFiles.Server);
            CanManageFiles = SupportsFiles && w.CanManageFiles;
            SupportsAddonFiles = SupportsFiles && features.Contains("filesaddons");
            SupportsFiles2 = SupportsFiles && features.Contains("files2");
            SupportsFilesText = SupportsFiles && features.Contains("filestext");
            SupportsFilesInfo = SupportsFiles && features.Contains("filesinfo");
            SupportsFilesKits = SupportsFiles && features.Contains("fileskits");
            installReported.Clear();
            if (w.FilesMaxMb > 0) FilesMaxMb = w.FilesMaxMb;
            savedFiles = null; installedAddons = null;
            if (!SupportsFiles) { Slots.Clear(); FilesLoaded = false; }
        }

        void ResetFiles()
        {
            SupportsFiles = false; CanManageFiles = false; SupportsAddonFiles = false; SupportsFiles2 = false; SupportsFilesText = false; SupportsFilesInfo = false; SupportsFilesKits = false; Kits.Clear(); installReported.Clear(); FilesLoaded = false; savedFiles = null; installedAddons = null;
            Slots.Clear(); FilesNoticeText = ""; FilesNoticeFor = null; FilesUsedBytes = 0;
            filesLastRequest = DateTime.MinValue;
        }

        public void RequestFiles()
        {
            if (!SupportsFiles) return;
            filesLastRequest = Clock();
            _ = Send(LodgeJson.Build(new FilesListMsg().Write));
        }

        void OnFilesFrame(JsonObject m)
        {
            if (!SupportsFiles) return;
            switch (LodgeJson.Str(m, "t"))
            {
                case LodgeMsg.FilesData: ApplyFilesData(FilesDataMsg.Read(new LodgeJsonFields(m))); break;
                case LodgeMsg.FilesChanged: OnFilesChanged(FilesChangedMsg.Read(new LodgeJsonFields(m))); break;
                case LodgeMsg.FilesResult: OnFilesResult(FilesResultMsg.Read(new LodgeJsonFields(m))); break;
            }
        }

        // the messages are typed in shared/Elans.Shared/Lodge/FileMessages.cs
        void ApplyFilesData(FilesDataMsg m)
        {
            CanManageFiles = m.CanManage;
            if (m.MaxMb > 0) FilesMaxMb = m.MaxMb;
            if (m.TotalMb > 0) FilesTotalMb = m.TotalMb;
            FilesUsedBytes = m.UsedBytes;
            var incoming = m.Slots.Where(d => SafeFile.Id(d.Id) != null).ToList();
            // the same objects stay (a progress bar must survive a refresh), newcomers are added, deleted slots go
            foreach (var gone in Slots.Where(s => incoming.All(d => d.Id != s.Id)).ToList()) Slots.Remove(gone);
            for (int i = 0; i < incoming.Count; i++)
            {
                var d = incoming[i];
                var have = Slots.FirstOrDefault(s => s.Id == d.Id);
                if (have == null) { have = new SlotVM(); Slots.Insert(Math.Min(i, Slots.Count), have); }
                have.Update(d, CanManageFiles, Saved().Get(d.Id), SupportsAddonFiles ? Installed().Get(d.Id) : null, SupportsAddonFiles, SupportsFilesText);
                if (SupportsFiles2) _ = LoadCoverAsync(have);
            }
            if (SupportsAddonFiles)
            {
                int iface = 0; string addOns = null;
                try { iface = LodgeAddons.ClientInterface(); addOns = LodgeAddons.AddOnsDir(); } catch { }
                foreach (var s in Slots.Where(s => s.IsAddon))
                {
                    s.SetAddonContext(iface, addOns == null ? null : (Func<string, bool>)(f => Directory.Exists(Path.Combine(addOns, f))));
                    if (s.InstalledV > 0) ReportInstalled(s.Id, s.InstalledV);
                }
            }
            // kits: the same objects stay, newcomers are added, deleted ones go
            var kitsIn = SupportsFilesKits ? m.Kits.Where(k => SafeFile.Id(k.Id) != null).ToList() : new List<FileKit>();
            foreach (var gone in Kits.Where(k => kitsIn.All(d => d.Id != k.Id)).ToList()) Kits.Remove(gone);
            for (int i = 0; i < kitsIn.Count; i++)
            {
                var have = Kits.FirstOrDefault(k => k.Id == kitsIn[i].Id);
                if (have == null) { have = new KitVM(); Kits.Insert(Math.Min(i, Kits.Count), have); }
                have.Update(kitsIn[i], Slots, CanManageFiles);
            }
            FilesLoaded = true;
            FilesChanged?.Invoke();
            // installed addons that keep themselves updated: a newer version goes in by itself
            if (SupportsAddonFiles)
                foreach (var s in Slots.Where(s => s.IsAddon && s.AutoUpdate && s.Newest != null && s.InstalledV > 0 && s.Newest.V > s.InstalledV && !s.Busy).ToList())
                    _ = AutoUpdateAsync(s);
        }

        void OnFilesChanged(FilesChangedMsg m)
        {
            var by = SafeFile.Who(m.By) ?? "";
            var text = LodgeFiles.NoticeText(m.Action, DisplayNames.Shown(by), Names(m.Name), m.V ?? 0);
            if (text != null && !string.Equals(by, Me?.Name, StringComparison.OrdinalIgnoreCase))
            {
                FilesNoticeText = text; FilesNoticeFor = m.Slot;
                FilesNotice?.Invoke(text);
            }
            // refresh the list, unless one was just asked for (my own upload asks right after it)
            if (Clock() - filesLastRequest > TimeSpan.FromMilliseconds(400)) RequestFiles();
        }

        static string Names(string s) => string.IsNullOrWhiteSpace(s) ? "a file" : s.Trim();

        void OnFilesResult(FilesResultMsg m)
        {
            if (m.Ok) return;
            var slot = Slots.FirstOrDefault(s => s.Id == m.Slot);
            var text = FilesWhy(m.Why);
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

        // slotId null = a new slot called `name`; otherwise a new version of that slot. addon (server 2.32): true marks it as a WoW addon (the zip must be one,
        // AddonZip), false unmarks it, null says nothing (an older server)
        public async Task<string> UploadSlotAsync(string slotId, string name, string path, string note, Action<double> progress = null, bool? addon = null, string kindOverride = null)
        {
            if (!SupportsFiles) return "This lodge doesn't share files";
            var target = slotId == null ? null : Slots.FirstOrDefault(s => s.Id == slotId);
            if (slotId == null ? !CanManageFiles : (target != null && !target.CanUpdate)) return FilesWhy("forbidden");
            var http = FilesHttp;
            if (http == null || (!Online && filesClientOverride == null)) return "You are offline - try again when you are back";
            name = (name ?? "").Trim();
            if (slotId == null && (name.Length < 1 || name.Length > LodgeFiles.MaxNameChars)) return "Give the file a name of 1 to " + LodgeFiles.MaxNameChars + " characters";
            if (slotId == null && Slots.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))) return "A slot with that name exists - use Replace on it";
            note = (note ?? "").Trim();
            if (note.Length > LodgeFiles.MaxNoteChars) return "The note can be " + LodgeFiles.MaxNoteChars + " characters at most";
            var refusal = LodgeFiles.CheckUpload(path, FilesMaxMb);
            if (refusal != null) return refusal;
            if (addon == true)
            {
                var chk = await Task.Run(() => AddonZip.Check(path));
                if (!chk.Ok) return "That isn't an addon zip: " + chk.Problem;
            }
            var kind = kindOverride ?? (SupportsAddonFiles && addon != null ? (addon.Value ? "addon" : "file") : null);
            var slot = slotId == null ? null : Slots.FirstOrDefault(s => s.Id == slotId);
            if (slotId != null && slot == null) return FilesWhy("missing");
            try
            {
                if (slot != null) { slot.Error = ""; slot.BusyText = "Uploading..."; slot.Progress = 0; }
                using (Activity.Begin("Uploading " + name + " to your Lodge", Activity.Kind.Save))
                {
                    var sha = await Task.Run(() => LodgeFiles.Sha256(path));
                    await http.UploadSlot(slotId, name, path, note, sha, p => { if (slot != null) slot.Progress = p; progress?.Invoke(p); }, kind);
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
            _ = Send(LodgeJson.Build(new FilesDeleteMsg { Slot = slot.Id }.Write));
            return null;
        }

        public string RollBackSlot(SlotVM slot, int version)
        {
            if (!SupportsFiles || slot == null) return "That file isn't there any more";
            if (!slot.CanUpdate) return FilesWhy("forbidden");
            _ = Send(LodgeJson.Build(new FilesRollbackMsg { Slot = slot.Id, V = version }.Write));
            return null;
        }

        // ---- addons (server 2.32): Install in WoW / Update in WoW, and "Keep it updated"
        // the newest version is downloaded (checked against its SHA-256 like Save), checked as an addon zip on this PC, shown to confirm (null =
        // no question: an automatic update), installed (LodgeAddons.Install) and remembered. Returns null on success, "" when cancelled, else why.
        public async Task<string> InstallSlotAsync(SlotVM slot, Func<AddonZip.Result, bool> confirm)
        {
            if (!SupportsAddonFiles || slot == null || !slot.IsAddon || slot.Newest == null) return "That isn't an addon the Outpost can install";
            var dir = LodgeAddons.AddOnsDir();
            if (dir == null) return "WoW Forever wasn't found on this PC - check the WoW folder in Settings";
            if (!installing.Add(slot.Id)) return "";
            var ver = slot.Newest;
            var tmp = Path.Combine(Util.DataDir, "lodge-addon-" + slot.Id + ".zip");
            try
            {
                var err = await SaveVersionAsync(slot, ver, tmp);
                if (err != null) return err;
                var chk = await Task.Run(() => AddonZip.Check(tmp));
                if (!chk.Ok) return "Not installed: " + chk.Problem;
                if (confirm != null && !confirm(chk)) return "";
                var before = Installed().Get(slot.Id);
                slot.Error = ""; slot.BusyText = "Installing in WoW..."; slot.Progress = 0.5;
                var res = await Task.Run(() => LodgeAddons.Install(dir, slot.Name, tmp, before?.Folders));
                if (!res.Ok) return res.Error;
                var rec = new InstalledRecord { V = ver.V, Sha = ver.Sha256, Auto = before?.Auto ?? false, Folders = res.Folders };
                Installed().Set(slot.Id, rec);
                slot.SetInstalled(rec, slot.Newest);
                ReportInstalled(slot.Id, rec.V);
                foreach (var k in Kits.Where(k => k.Slots.Contains(slot))) k.Refresh();
                FilesChanged?.Invoke();
                return null;
            }
            finally
            {
                installing.Remove(slot.Id);
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                slot.SetInstalled(Installed().Get(slot.Id), slot.Newest);      // the download marked it saved: what is in WoW decides the badge
                slot.SetIdle();
            }
        }
        readonly HashSet<string> installing = new HashSet<string>(StringComparer.Ordinal);

        // a newer version of an addon set to keep itself updated: installed without asking when it is the same addon (the same folders);
        // a version that changes the folders waits for a click (the slot shows Update in WoW)
        async Task AutoUpdateAsync(SlotVM slot)
        {
            var rec = Installed().Get(slot.Id);
            if (rec == null || !rec.Auto) return;
            var err = await InstallSlotAsync(slot, chk => chk.Folders.Count == rec.Folders.Count && chk.Folders.All(f => rec.Folders.Contains(f, StringComparer.OrdinalIgnoreCase)));
            if (err == null) { var t = slot.Name + " was updated to v" + slot.InstalledV + " in WoW - type /reload in the game"; FilesNoticeText = t; FilesNoticeFor = slot.Id; FilesNotice?.Invoke(t); }
            else if (err == "") slot.Error = "This version changes which addon folders it has - press Update in WoW to check and install it";
            else slot.Error = err;
        }

        public void SetAutoUpdate(SlotVM slot, bool on)
        {
            var rec = slot == null ? null : Installed().Get(slot.Id);
            if (rec == null) return;
            rec.Auto = on; Installed().Set(slot.Id, rec);
            slot.SetInstalled(rec, slot.Newest);
            if (on && slot.Newest != null && slot.Newest.V > rec.V && !slot.Busy) _ = AutoUpdateAsync(slot);
        }

        public InstalledRecord InstalledOfForTest(string slotId) => Installed().Get(slotId);

        // ---- 2.36 kits
        public string SaveKit(KitVM kit, string name, string desc, string see, IEnumerable<string> slotIds)
        {
            if (!SupportsFilesKits) return "This lodge can't make kits - update the Lodge server";
            if (!CanManageFiles) return FilesWhy("forbidden");
            name = (name ?? "").Trim();
            if (name.Length == 0) return "Give the kit a name";
            var ids = (slotIds ?? Enumerable.Empty<string>()).Distinct().ToList();
            if (ids.Count == 0) return "Pick the files that go in it";
            if (ids.Count > FileKit.MaxSlots) return "A kit can hold " + FileKit.MaxSlots + " files at most";
            _ = Send(LodgeJson.Build(new FilesKitMsg { Id = kit?.Id, Name = name, Desc = (desc ?? "").Trim(), See = see ?? "", Slots = string.Join(",", ids) }.Write));
            return null;
        }

        public string DeleteKit(KitVM kit)
        {
            if (!SupportsFilesKits || kit == null) return "That kit isn't there any more";
            if (!CanManageFiles) return FilesWhy("forbidden");
            _ = Send(new JsonObject { ["t"] = FilesKitMsg.DeleteType, ["id"] = kit.Id });
            return null;
        }

        // every addon of the kit that isn't installed or has a newer version, after ONE question (confirm gets the kit's addons and what changes);
        // returns null when all went in, "" when cancelled, else what went wrong (per addon, the rest still goes)
        public async Task<string> InstallKitAsync(KitVM kit, Func<List<SlotVM>, bool> confirm)
        {
            if (kit == null) return "That kit isn't there any more";
            var todo = kit.Addons.Where(s => s.InstalledV == 0 || (s.Newest != null && s.Newest.V > s.InstalledV)).ToList();
            if (todo.Count == 0) todo = kit.Addons.ToList();            // everything is current: Reinstall all
            if (todo.Count == 0) return "This kit has no addons";
            if (confirm != null && !confirm(todo)) return "";
            var errors = new List<string>();
            kit.Error = "";
            foreach (var s in todo)
            {
                var err = await InstallSlotAsync(s, null);
                if (!string.IsNullOrEmpty(err)) errors.Add(s.Name + ": " + err);
            }
            kit.Refresh();
            if (errors.Count > 0) { kit.Error = string.Join(" · ", errors); return kit.Error; }
            NoteFiles(kit.Name + ": " + todo.Count + " addon" + (todo.Count == 1 ? "" : "s") + " installed in WoW - type /reload in the game", kit.Id);
            return null;
        }

        public void SetKitAuto(KitVM kit, bool on)
        {
            if (kit == null) return;
            foreach (var s in kit.Addons.Where(s => s.InstalledV > 0)) SetAutoUpdate(s, on);
            kit.Refresh();
        }

        // tell the Lodge which version of an addon slot this PC has (once per version and connection; the Lodge only ever shows counts)
        void ReportInstalled(string slotId, int v)
        {
            if (!SupportsFilesInfo || v <= 0 || !installReported.Add(slotId + ":" + v)) return;
            _ = Send(LodgeJson.Build(new FilesInstalledMsg { Slot = slotId, V = v }.Write));
        }
        public void NoteFiles(string text, string slotId) { FilesNoticeText = text ?? ""; FilesNoticeFor = slotId; FilesChanged?.Invoke(); }

        // ---- 2.34: text slots (import strings). Shared as a .txt of UTF-8 text; Copy fetches the newest (checked like Save, kept in files-text) and returns it
        public async Task<string> UploadTextAsync(string slotId, string name, string text, string note, Action<double> progress = null)
        {
            if (!SupportsFilesText) return "This lodge can't share text - update the Lodge server";
            text = (text ?? "").Trim();
            var data = new System.Text.UTF8Encoding(false).GetBytes(text);
            var why = FileSlot.TextProblem(data);
            if (why != null) return why;
            var dir = Path.Combine(Util.DataDir, "files-text");
            Directory.CreateDirectory(dir);
            var tmp = Path.Combine(dir, "upload-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmp);
            var file = Path.Combine(tmp, "import.txt");
            try
            {
                File.WriteAllBytes(file, data);
                return await UploadSlotAsync(slotId, name, file, note, progress, null, "text");
            }
            finally { try { Directory.Delete(tmp, true); } catch { } }
        }

        public static string TextDir => Path.Combine(Util.DataDir, "files-text");
        public async Task<(string text, string error)> FetchTextAsync(SlotVM slot)
        {
            var ver = slot?.Newest;
            if (slot == null || !slot.IsText || ver == null) return (null, "That isn't text");
            var file = Path.Combine(TextDir, slot.Id + "-" + ver.V + ".txt");
            if (!File.Exists(file) || !string.Equals(LodgeFiles.Sha256(file), ver.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(TextDir);
                var err = await SaveVersionAsync(slot, ver, file);
                if (err != null) return (null, err);
                foreach (var old in Directory.GetFiles(TextDir, slot.Id + "-*.txt")) if (!string.Equals(old, file, StringComparison.OrdinalIgnoreCase)) try { File.Delete(old); } catch { }
            }
            var data = File.ReadAllBytes(file);
            var why = FileSlot.TextProblem(data);
            if (why != null) return (null, why);
            return (new System.Text.UTF8Encoding(false, true).GetString(data), null);
        }

        // ---- 2.33: change a slot without uploading (the answer comes as files.result: an error lands on the slot), its cover picture
        public string EditSlot(SlotVM slot, FilesEditMsg edit)
        {
            if (!SupportsFiles2 || slot == null || edit == null) return "This lodge can't change files that way - update the Lodge server";
            if (!slot.CanUpdate) return FilesWhy("forbidden");
            if ((edit.Name != null || edit.See != null || edit.Edit != null) && !CanManageFiles) return FilesWhy("forbidden");
            edit.Slot = slot.Id;
            slot.Error = "";
            _ = Send(LodgeJson.Build(edit.Write));
            return null;
        }

        public async Task<string> SetCoverAsync(SlotVM slot, string path)
        {
            if (!SupportsFiles2 || slot == null) return "This lodge can't show pictures for files - update the Lodge server";
            if (!slot.CanUpdate) return FilesWhy("forbidden");
            var http = FilesHttp;
            if (http == null || (!Online && filesClientOverride == null)) return "You are offline - try again when you are back";
            try
            {
                if (new FileInfo(path).Length > LodgeClient.MaxCoverBytes) return "A picture can be 2 MB at most - pick a smaller one";
                if (LodgeFiles.PictureType(path) == null) return "That isn't a picture (png, jpg, gif or webp)";
                await http.PutCover(slot.Id, path);
                RequestFiles();
                return null;
            }
            catch (Exception ex) { Util.Log("files cover: " + ex.GetType().Name); return ex is InvalidOperationException ? ex.Message : "Couldn't set the picture: " + ex.Message; }
        }

        // the picture of a slot, cached by slot and picture number in the Outpost's data folder (files-covers); decoded small, never run or shown by a browser
        public static string CoverDir => Path.Combine(Util.DataDir, "files-covers");
        async Task LoadCoverAsync(SlotVM slot)
        {
            if (slot.CoverV <= 0) { slot.CoverImage = null; return; }
            var file = Path.Combine(CoverDir, slot.Id + "-" + slot.CoverV + ".img");
            try
            {
                if (!File.Exists(file))
                {
                    var http = FilesHttp;
                    if (http == null) return;
                    Directory.CreateDirectory(CoverDir);
                    var part = file + ".part";
                    await http.GetCover(slot.Id, part);
                    if (LodgeFiles.PictureType(part) == null) { try { File.Delete(part); } catch { } return; }
                    if (File.Exists(file)) File.Delete(file);
                    File.Move(part, file);
                    foreach (var old in Directory.GetFiles(CoverDir, slot.Id + "-*.img")) if (!string.Equals(old, file, StringComparison.OrdinalIgnoreCase)) try { File.Delete(old); } catch { }
                }
                slot.CoverImage = LodgeFiles.LoadPicture(file, 640);
            }
            catch (Exception ex) { Util.Log("files cover load: " + ex.GetType().Name); }
        }

        public void ClearFilesNotice() { FilesNoticeText = ""; FilesNoticeFor = null; FilesChanged?.Invoke(); }
        public SavedRecord SavedOfForTest(string slotId) => Saved().Get(slotId);
    }
}
