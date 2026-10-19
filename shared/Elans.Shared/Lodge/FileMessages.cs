using System.Collections.Generic;

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

    // a slot { id (32 hex, server-made), name, createdBy, versions: [FileVersion] (oldest first) }
    public sealed class FileSlot
    {
        public string Id, Name, CreatedBy;
        public List<FileVersion> Versions = new List<FileVersion>();

        public void Write(ILodgeWriter w)
        {
            w.Set("id", Id); w.Set("name", Name); w.Set("createdBy", CreatedBy);
            var l = w.List("versions"); foreach (var v in Versions) v.Write(l.Add());
        }

        public static FileSlot Read(ILodgeFields f)
        {
            var s = new FileSlot { Id = f.Str("id"), Name = f.Str("name"), CreatedBy = f.Str("createdBy") };
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

        public void Write(ILodgeWriter w)
        {
            w.Set("v", V); w.Set("fileName", FileName); w.Set("size", Size); w.Set("sha256", Sha256); w.Set("uploadedBy", UploadedBy); w.Set("at", At); w.Set("note", Note);
        }

        public static FileVersion Read(ILodgeFields f) => new FileVersion
        {
            V = f.Int("v"), FileName = f.Str("fileName"), Size = f.Long("size"), Sha256 = f.Str("sha256"), UploadedBy = f.Str("uploadedBy"), At = f.Long("at"), Note = f.Str("note"),
        };
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
