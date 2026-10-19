namespace Elans.Shared
{
    // Hosted datasets (server/PROTOCOL.md, 2.17 "classic"): the field names of the socket messages live only here. The classes only map
    // fields; every rule (which names exist, who may read / delete, the size and rate limits) stays in the Lodge server (DatasetsModule.cs).
    // The bytes never go over the socket: PUT / GET /datasets/{name} are authenticated HTTP; the upload's answer is a DatasetView as well.
    // A refused dataset.info is answered with an error frame (codes disabled, guest, rate), not with a dataset message.

    // ---------------------------------------------------------------- client -> server

    // (members) what the Lodge holds of one dataset { name } (answered with DatasetDataMsg)
    public sealed class DatasetInfoMsg
    {
        public const string Type = LodgeMsg.DatasetInfo;
        public string Name;            // "classic" (the server's fixed list)

        public void Write(ILodgeWriter w) { w.Set("t", Type); w.Set("name", Name); }
        public static DatasetInfoMsg Read(ILodgeFields f) => new DatasetInfoMsg { Name = f.Str("name") };
    }

    // (Perm.DatasetManage) stop hosting a dataset { name } (answered with DatasetResultMsg)
    public sealed class DatasetDeleteMsg
    {
        public const string Type = LodgeMsg.DatasetDelete;
        public string Name;

        public void Write(ILodgeWriter w) { w.Set("t", Type); w.Set("name", Name); }
        public static DatasetDeleteMsg Read(ILodgeFields f) => new DatasetDeleteMsg { Name = f.Str("name") };
    }

    // ---------------------------------------------------------------- server -> client

    // what the Lodge holds of a dataset { name, available, version, sha256, size, uploadedBy (display name), at (unix ms) } - the last five only
    // while available. The fields of dataset.data and dataset.changed (flat, in that order, before their own) and the HTTP upload's answer.
    public sealed class DatasetView
    {
        public string Name;
        public bool Available;
        public string Version, Sha256, UploadedBy;   // written only when Available (Version as JSON null when there is none)
        public long Size, At;

        public void Write(ILodgeWriter w)
        {
            w.Set("name", Name); w.Set("available", Available);
            if (Available) { w.Set("version", Version); w.Set("sha256", Sha256); w.Set("size", Size); w.Set("uploadedBy", UploadedBy); w.Set("at", At); }
        }

        public static DatasetView Read(ILodgeFields f) => new DatasetView
        {
            Name = f.Str("name"), Available = f.Bool("available"), Version = f.Str("version"), Sha256 = f.Str("sha256"), Size = f.Long("size"),
            UploadedBy = f.Str("uploadedBy"), At = f.Long("at"),
        };
    }

    // the answer to dataset.info { ...DatasetView, canManage, maxMb } (the type follows the view's fields on the wire, as it always did)
    public sealed class DatasetDataMsg
    {
        public const string Type = LodgeMsg.DatasetData;
        public DatasetView Dataset = new DatasetView();
        public bool? CanManage;        // the server always sends it; reading: null when a frame has no canManage (the Outpost then keeps what it knew)
        public int MaxMb;              // the upload limit; reading: 0 when missing

        public void Write(ILodgeWriter w)
        {
            Dataset.Write(w);
            w.Set("t", Type);
            if (CanManage is bool c) w.Set("canManage", c);
            w.Set("maxMb", MaxMb);
        }

        public static DatasetDataMsg Read(ILodgeFields f) => new DatasetDataMsg
        {
            Dataset = DatasetView.Read(f), CanManage = f.Has("canManage") ? f.Bool("canManage") : (bool?)null, MaxMb = f.Int("maxMb"),
        };
    }

    // to every member who may read it: a dataset changed { ...DatasetView, action: new | replace | delete } (the type after the view's fields)
    public sealed class DatasetChangedMsg
    {
        public const string Type = LodgeMsg.DatasetChanged;
        public DatasetView Dataset = new DatasetView();
        public string Action;

        public void Write(ILodgeWriter w) { Dataset.Write(w); w.Set("t", Type); w.Set("action", Action); }
        public static DatasetChangedMsg Read(ILodgeFields f) => new DatasetChangedMsg { Dataset = DatasetView.Read(f), Action = f.Str("action") };
    }

    // the answer to dataset.delete { op: "delete", name, ok, why? } - why: unknown | disabled | forbidden | missing | save
    public sealed class DatasetResultMsg
    {
        public const string Type = LodgeMsg.DatasetResult;
        public string Op, Name, Why;   // Why: null = not sent
        public bool Ok;

        public void Write(ILodgeWriter w)
        {
            w.Set("t", Type); w.Set("op", Op); w.Set("name", Name); w.Set("ok", Ok);
            if (Why != null) w.Set("why", Why);
        }

        public static DatasetResultMsg Read(ILodgeFields f) => new DatasetResultMsg { Op = f.Str("op"), Name = f.Str("name"), Ok = f.Bool("ok"), Why = f.Str("why") };
    }
}
