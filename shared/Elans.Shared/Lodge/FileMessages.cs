using System;
using System.Collections.Generic;
using System.Linq;

namespace Elans.Shared
{
    // Shared files (server/PROTOCOL.md, 2.14 "files"): the field names of the socket messages live only here. The classes only map fields;
    // every rule (who may list / delete / roll back, the slot and size limits, the rate limits) stays in the Lodge server (FilesModule.cs).
    // The bytes themselves never go over the socket: uploads and downloads are authenticated HTTP (PUT /slots/new, PUT /slots/{id},
    // GET /slots/{id}/{v}); the upload's answer is a FileSlot as well (the same JSON as a files.data slot).

    // ---------------------------------------------------------------- client -> server

    // ask for the shared files (answered with FilesDataMsg, or FilesResultMsg { op: "list", ok: false } when the feature is off)
    public sealed class FilesListMsg : LodgeEmptyMsg<FilesListMsg> { public const string Type = LodgeMsg.FilesList; protected override string MsgType => Type; }

    // (Perm.FilesManage) delete a slot and every kept version { slot } (answered with FilesResultMsg)
    public sealed class FilesDeleteMsg
    {
        public const string Type = LodgeMsg.FilesDelete;
        public string Slot;

        public void Write(ILodgeWriter w) { w.Set("t", Type); w.Set("slot", Slot); }
        public static FilesDeleteMsg Read(ILodgeFields f) => new FilesDeleteMsg { Slot = f.Str("slot") };
    }

    // (Perm.FilesManage) make an older kept version the newest { slot, v } (answered with FilesResultMsg)
    public sealed class FilesRollbackMsg
    {
        public const string Type = LodgeMsg.FilesRollback;
        public string Slot;
        public int V;

        public void Write(ILodgeWriter w) { w.Set("t", Type); w.Set("slot", Slot); w.Set("v", V); }
        public static FilesRollbackMsg Read(ILodgeFields f) => new FilesRollbackMsg { Slot = f.Str("slot"), V = f.Int("v") };
    }

    // ---------------------------------------------------------------- server -> client

    // the shared files { maxMb (per file), totalMb (all kept versions), usedBytes, keep (versions kept per slot), canManage, slots: [FileSlot] }
    public sealed class FilesDataMsg
    {
        public const string Type = LodgeMsg.FilesData;
        public int MaxMb, TotalMb, Keep;
        public long UsedBytes;
        public bool CanManage;
        public List<FileSlot> Slots = new List<FileSlot>();

        public void Write(ILodgeWriter w)
        {
            w.Set("t", Type); w.Set("maxMb", MaxMb); w.Set("totalMb", TotalMb); w.Set("usedBytes", UsedBytes); w.Set("keep", Keep); w.Set("canManage", CanManage);
            var l = w.List("slots"); foreach (var s in Slots) s.Write(l.Add());
        }

        public static FilesDataMsg Read(ILodgeFields f)
        {
            var m = new FilesDataMsg { MaxMb = f.Int("maxMb"), TotalMb = f.Int("totalMb"), UsedBytes = f.Long("usedBytes"), Keep = f.Int("keep"), CanManage = f.Bool("canManage") };
            foreach (var s in f.List("slots")) m.Slots.Add(FileSlot.Read(s));
            return m;
        }
    }

    // 2.33: change a slot without uploading { t, slot, name?, desc?, see?, edit?, kind? ("addon" | "file"), noteV? + note?, cover? (false = remove the picture) }.
    // name, see and edit are the officers' (FilesManage); desc, kind, a version's note and removing the picture also anyone who may update the slot.
    // The answer is files.result {op "edit"}; everybody who sees the slot gets files.changed {action "edit"}.
    public sealed class FilesEditMsg
    {
        public const string Type = "files.edit";
        public string Slot, Name, Desc, See, Edit, Kind, Note;
        public int NoteV;
        public bool? Cover;

        public void Write(ILodgeWriter w)
        {
            w.Set("t", Type); w.Set("slot", Slot);
            if (Name != null) w.Set("name", Name);
            if (Desc != null) w.Set("desc", Desc);
            if (See != null) w.Set("see", See);
            if (Edit != null) w.Set("edit", Edit);
            if (Kind != null) w.Set("kind", Kind);
            if (NoteV > 0) { w.Set("noteV", NoteV); w.Set("note", Note ?? ""); }
            if (Cover != null) w.Set("cover", Cover.Value);
        }

        public static FilesEditMsg Read(ILodgeFields f) => new FilesEditMsg
        {
            Slot = f.Str("slot"), Name = f.Str("name"), Desc = f.Str("desc"), See = f.Str("see"), Edit = f.Str("edit"), Kind = f.Str("kind"),
            NoteV = f.Int("noteV"), Note = f.Str("note"), Cover = f.Has("cover") ? f.Bool("cover") : (bool?)null,
        };
    }

    // a slot { id (32 hex, server-made), name, createdBy, kind?, versions: [FileVersion] (oldest first) }; kind "addon" (server 2.32,
    // feature "filesaddons") = a zip of WoW addon folders the Outpost can install (Elans.Shared.AddonZip); absent = a plain file
    // 2.33 (feature "files2"): desc (a longer description), see (the lowest rank that sees the slot: guest | member | veteran | officer; absent =
    // everyone), edit (the lowest rank that may upload a new version / roll back / change the description: member | veteran | officer; absent =
    // officers), cover (> 0 = a cover picture, GET /slots/{id}/cover, the number changes with every new picture), canUpdate (this viewer may update it)
    public sealed class FileSlot
    {
        public const string AddonKind = "addon", TextKind = "text";        // text (2.34, feature "filestext"): an import string - a WeakAura, a profile, a macro
        public static readonly string[] SeeRanks = { "guest", "member", "veteran", "officer" }, EditRanks = { "member", "veteran", "officer" };
        public string Id, Name, CreatedBy, Kind, Desc, See, Edit;
        public int Cover;
        public bool CanUpdate;
        public int HaveNewest, HaveOlder;              // 2.35 (addon slots): how many people have the newest / an older version installed (counts only, no names)
        public bool IsAddon => Kind == AddonKind;
        public bool IsText => Kind == TextKind;
        public const long MaxTextBytes = 256 * 1024;

        // what a text slot's version must be: UTF-8 (strict), no NUL, at most MaxTextBytes; null = fine
        public static string TextProblem(byte[] data)
        {
            if (data == null || data.Length == 0) return "The text is empty";
            if (data.Length > MaxTextBytes) return "Text can be 256 KB at most";
            try
            {
                var s = new System.Text.UTF8Encoding(false, true).GetString(data);
                if (s.IndexOf('\0') >= 0) return "That isn't plain text";
            }
            catch (System.Text.DecoderFallbackException) { return "That isn't plain text (UTF-8)"; }
            return null;
        }
        public List<FileVersion> Versions = new List<FileVersion>();

        public void Write(ILodgeWriter w)
        {
            w.Set("id", Id); w.Set("name", Name); w.Set("createdBy", CreatedBy);
            if (Kind == AddonKind || Kind == TextKind) w.Set("kind", Kind);
            if (!string.IsNullOrEmpty(Desc)) w.Set("desc", Desc);
            if (!string.IsNullOrEmpty(See)) w.Set("see", See);
            if (!string.IsNullOrEmpty(Edit)) w.Set("edit", Edit);
            if (Cover > 0) w.Set("cover", Cover);
            if (CanUpdate) w.Set("canUpdate", true);
            if (HaveNewest > 0) w.Set("haveNewest", HaveNewest);
            if (HaveOlder > 0) w.Set("haveOlder", HaveOlder);
            var l = w.List("versions"); foreach (var v in Versions) v.Write(l.Add());
        }

        public static FileSlot Read(ILodgeFields f)
        {
            var s = new FileSlot
            {
                Id = f.Str("id"), Name = f.Str("name"), CreatedBy = f.Str("createdBy"), Kind = f.Str("kind") == AddonKind ? AddonKind : f.Str("kind") == TextKind ? TextKind : null,
                Desc = f.Str("desc"), See = Array.IndexOf(SeeRanks, f.Str("see")) >= 0 ? f.Str("see") : null, Edit = Array.IndexOf(EditRanks, f.Str("edit")) >= 0 ? f.Str("edit") : null,
                Cover = Math.Max(0, f.Int("cover")), CanUpdate = f.Bool("canUpdate"), HaveNewest = Math.Max(0, f.Int("haveNewest")), HaveOlder = Math.Max(0, f.Int("haveOlder")),
            };
            foreach (var v in f.List("versions")) s.Versions.Add(FileVersion.Read(v));
            return s;
        }
    }

    // a kept version { v, fileName (cleaned), size, sha256 (hex), uploadedBy, at (unix ms), note }
    public sealed class FileVersion
    {
        public int V;
        public string FileName, Sha256, UploadedBy, Note;
        public long Size, At;
        public List<AddonFolder> Addon = new List<AddonFolder>();       // 2.35: an addon version's folders as their tocs say (empty = not read / not an addon)

        public void Write(ILodgeWriter w)
        {
            w.Set("v", V); w.Set("fileName", FileName); w.Set("size", Size); w.Set("sha256", Sha256); w.Set("uploadedBy", UploadedBy); w.Set("at", At); w.Set("note", Note);
            if (Addon.Count > 0) { var l = w.List("addon"); foreach (var a in Addon) a.Write(l.Add()); }
        }

        public static FileVersion Read(ILodgeFields f)
        {
            var v = new FileVersion
            {
                V = f.Int("v"), FileName = f.Str("fileName"), Size = f.Long("size"), Sha256 = f.Str("sha256"), UploadedBy = f.Str("uploadedBy"), At = f.Long("at"), Note = f.Str("note"),
            };
            foreach (var a in f.List("addon").Take(AddonZip.MaxFolders)) v.Addon.Add(AddonFolder.Read(a));
            v.Addon.RemoveAll(a => !AddonZip.SafeFolder(a.F));
            return v;
        }
    }

    // 2.35: one folder of an addon version { f (folder), v (its toc's version), i ("16001,11509": the Interface numbers), d ("DBM-Core,Ace3": what it needs) }
    public sealed class AddonFolder
    {
        public string F = "", V = "";
        public List<int> I = new List<int>();
        public List<string> D = new List<string>();

        public void Write(ILodgeWriter w)
        {
            w.Set("f", F);
            if (V.Length > 0) w.Set("v", V);
            if (I.Count > 0) w.Set("i", string.Join(",", I));
            if (D.Count > 0) w.Set("d", string.Join(",", D));
        }

        public static AddonFolder Read(ILodgeFields f)
        {
            var a = new AddonFolder { F = f.Str("f") ?? "", V = (f.Str("v") ?? "").Length > 40 ? f.Str("v").Substring(0, 40) : f.Str("v") ?? "" };
            foreach (var x in (f.Str("i") ?? "").Split(',')) if (int.TryParse(x, out var i) && i > 0 && i < 1000000 && a.I.Count < 8) a.I.Add(i);
            foreach (var x in (f.Str("d") ?? "").Split(',')) if (AddonZip.SafeFolder(x.Trim()) && a.D.Count < 20) a.D.Add(x.Trim());
            return a;
        }

        public static List<AddonFolder> From(AddonZip.Result r) =>
            r == null || !r.Ok ? new List<AddonFolder>() : r.Folders.Select(f => r.Info.TryGetValue(f, out var i)
                ? new AddonFolder { F = f, V = i.Version, I = i.Interfaces.ToList(), D = i.Deps.ToList() } : new AddonFolder { F = f }).ToList();
    }

    // 2.35: this Outpost has version v of an addon slot installed in WoW (0 = not any more) - only counts go back out (FileSlot.HaveNewest / HaveOlder)
    public sealed class FilesInstalledMsg
    {
        public const string Type = "files.installed";
        public string Slot;
        public int V;
        public void Write(ILodgeWriter w) { w.Set("t", Type); w.Set("slot", Slot); w.Set("v", V); }
        public static FilesInstalledMsg Read(ILodgeFields f) => new FilesInstalledMsg { Slot = f.Str("slot"), V = f.Int("v") };
    }

    // to everybody: a slot changed { action: new | version | rollback | delete, slot, name, by, at (unix ms), v? (not on delete) }
    public sealed class FilesChangedMsg
    {
        public const string Type = LodgeMsg.FilesChanged;
        public string Action, Slot, Name, By;
        public long At;
        public int? V;                 // null = not sent

        public void Write(ILodgeWriter w)
        {
            w.Set("t", Type); w.Set("action", Action); w.Set("slot", Slot); w.Set("name", Name); w.Set("by", By); w.Set("at", At);
            if (V is int v) w.Set("v", v);
        }

        public static FilesChangedMsg Read(ILodgeFields f) => new FilesChangedMsg
        {
            Action = f.Str("action"), Slot = f.Str("slot"), Name = f.Str("name"), By = f.Str("by"), At = f.Long("at"), V = f.Has("v") ? f.Int("v") : (int?)null,
        };
    }

    // the answer to files.list (only when refused) / files.delete / files.rollback { op: list | delete | rollback, ok, slot (null for list), why? }
    // why: disabled | forbidden | missing | already-newest | save
    public sealed class FilesResultMsg
    {
        public const string Type = LodgeMsg.FilesResult;
        public string Op, Slot, Why;   // Slot is always written (JSON null when there is none); Why only when there is one
        public bool Ok;

        public void Write(ILodgeWriter w)
        {
            w.Set("t", Type); w.Set("op", Op); w.Set("ok", Ok); w.Set("slot", Slot);
            if (Why != null) w.Set("why", Why);
        }

        public static FilesResultMsg Read(ILodgeFields f) => new FilesResultMsg { Op = f.Str("op"), Ok = f.Bool("ok"), Slot = f.Str("slot"), Why = f.Str("why") };
    }
}
