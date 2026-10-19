using System.Text.Json.Nodes;
using Elans.Shared;

namespace Elans.Shared.Tests;

// The typed hosted-dataset messages: every message survives Write -> Read, and writes exactly the JSON the old hand-built dictionary /
// JsonObject sent (golden values copied from src/Lodge/LodgeSession.Classic.cs and server/Lodge.Server/DatasetsModule.cs at c712015) -
// the wire must not change.
public class DatasetMessagesTests
{
    static TestFields Written(Action<ILodgeWriter> write) => TestFields.Written(write);
    static void SameJson(object expected, object actual) => TestFields.SameJson(expected, actual);

    const string Sha = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    static DatasetView Held() => new DatasetView
    {
        Name = "classic", Available = true, Version = "2026-01-01|ClassicDB_9_9_9_z0000.sql.gz|abc", Sha256 = Sha, Size = 123456, UploadedBy = "Wing", At = 1_790_000_000_000,
    };

    // DatasetsModule.View at c712015: name, available, then (only when held) version, sha256, size, uploadedBy, at
    static JsonObject OldView(bool held)
    {
        var o = new JsonObject { ["name"] = "classic", ["available"] = held };
        if (held) { o["version"] = "2026-01-01|ClassicDB_9_9_9_z0000.sql.gz|abc"; o["sha256"] = Sha; o["size"] = 123456L; o["uploadedBy"] = "Wing"; o["at"] = 1_790_000_000_000L; }
        return o;
    }

    // ---- round trips

    [Fact]
    public void Client_messages_round_trip()
    {
        Assert.Equal("classic", DatasetInfoMsg.Read(Written(new DatasetInfoMsg { Name = "classic" }.Write)).Name);
        Assert.Equal("classic", DatasetDeleteMsg.Read(Written(new DatasetDeleteMsg { Name = "classic" }.Write)).Name);
    }

    [Fact]
    public void Server_messages_round_trip()
    {
        var d = DatasetDataMsg.Read(Written(new DatasetDataMsg { Dataset = Held(), CanManage = true, MaxMb = 40 }.Write));
        var v = d.Dataset;
        Assert.Equal(("classic", true, Held().Version, Sha, 123456L, "Wing", 1_790_000_000_000L), (v.Name, v.Available, v.Version, v.Sha256, v.Size, v.UploadedBy, v.At));
        Assert.Equal(((bool?)true, 40), (d.CanManage, d.MaxMb));
        var none = DatasetDataMsg.Read(Written(new DatasetDataMsg { Dataset = { Name = "classic" }, CanManage = false, MaxMb = 40 }.Write));
        Assert.Equal(("classic", false, (string)null, (bool?)false), (none.Dataset.Name, none.Dataset.Available, none.Dataset.Version, none.CanManage));

        var c = DatasetChangedMsg.Read(Written(new DatasetChangedMsg { Dataset = Held(), Action = "replace" }.Write));
        Assert.Equal(("replace", "classic", true, Sha), (c.Action, c.Dataset.Name, c.Dataset.Available, c.Dataset.Sha256));

        var r = DatasetResultMsg.Read(Written(new DatasetResultMsg { Op = "delete", Name = "classic", Ok = false, Why = "forbidden" }.Write));
        Assert.Equal(("delete", "classic", false, "forbidden"), (r.Op, r.Name, r.Ok, r.Why));
        Assert.Null(DatasetResultMsg.Read(Written(new DatasetResultMsg { Op = "delete", Name = "classic", Ok = true }.Write)).Why);
    }

    [Fact]
    public void Each_message_writes_its_own_type()
    {
        Assert.Equal("dataset.info", Written(new DatasetInfoMsg().Write).Str("t"));
        Assert.Equal("dataset.delete", Written(new DatasetDeleteMsg().Write).Str("t"));
        Assert.Equal("dataset.data", Written(new DatasetDataMsg().Write).Str("t"));
        Assert.Equal("dataset.changed", Written(new DatasetChangedMsg().Write).Str("t"));
        Assert.Equal("dataset.result", Written(new DatasetResultMsg().Write).Str("t"));
    }

    [Fact]
    public void Reading_is_lenient_like_before()
    {
        // a field of the wrong type is null / 0 / false; canManage is null only when the frame has none (the Outpost keeps what it knew)
        var d = DatasetDataMsg.Read(new TestFields(new Dictionary<string, object> { ["name"] = 3, ["available"] = "yes", ["size"] = "9", ["maxMb"] = "40" }));
        Assert.Equal(((string)null, false, 0L, 0, (bool?)null), (d.Dataset.Name, d.Dataset.Available, d.Dataset.Size, d.MaxMb, d.CanManage));
        Assert.Equal((bool?)false, DatasetDataMsg.Read(new TestFields(new Dictionary<string, object> { ["canManage"] = "true" })).CanManage);
        Assert.Equal((bool?)false, DatasetDataMsg.Read(new TestFields(new Dictionary<string, object> { ["canManage"] = null })).CanManage);
        Assert.Null(DatasetDataMsg.Read(Written(new DatasetChangedMsg { Dataset = Held(), Action = "new" }.Write)).CanManage);   // a change has none
    }

    // ---- golden: what the old code sent

    [Fact]
    public void Golden_client_messages_match_the_old_Outpost_dictionaries()
    {
        SameJson(new Dictionary<string, object> { ["t"] = "dataset.info", ["name"] = "classic" }, Written(new DatasetInfoMsg { Name = "classic" }.Write).D);
        SameJson(new Dictionary<string, object> { ["t"] = "dataset.delete", ["name"] = "classic" }, Written(new DatasetDeleteMsg { Name = "classic" }.Write).D);
    }

    [Fact]
    public void Golden_server_messages_match_the_old_JsonObjects()
    {
        // DatasetsModule.Info: the view, then t, canManage, maxMb
        var info = OldView(true); info["t"] = "dataset.data"; info["canManage"] = true; info["maxMb"] = 40;
        SameJson(info, Written(new DatasetDataMsg { Dataset = Held(), CanManage = true, MaxMb = 40 }.Write).D);
        var empty = OldView(false); empty["t"] = "dataset.data"; empty["canManage"] = false; empty["maxMb"] = 40;
        SameJson(empty, Written(new DatasetDataMsg { Dataset = { Name = "classic" }, CanManage = false, MaxMb = 40 }.Write).D);
        // DatasetsModule.Announce: the view, then t, action
        var changed = OldView(true); changed["t"] = "dataset.changed"; changed["action"] = "new";
        SameJson(changed, Written(new DatasetChangedMsg { Dataset = Held(), Action = "new" }.Write).D);
        var deleted = OldView(false); deleted["t"] = "dataset.changed"; deleted["action"] = "delete";
        SameJson(deleted, Written(new DatasetChangedMsg { Dataset = { Name = "classic" }, Action = "delete" }.Write).D);
        // the HTTP upload's answer (View)
        SameJson(OldView(true), Written(Held().Write).D);
        // DatasetsModule.Handle dataset.delete
        SameJson(new JsonObject { ["t"] = "dataset.result", ["op"] = "delete", ["name"] = "classic", ["ok"] = false, ["why"] = "missing" },
            Written(new DatasetResultMsg { Op = "delete", Name = "classic", Ok = false, Why = "missing" }.Write).D);
        SameJson(new JsonObject { ["t"] = "dataset.result", ["op"] = "delete", ["name"] = "classic", ["ok"] = true },
            Written(new DatasetResultMsg { Op = "delete", Name = "classic", Ok = true }.Write).D);
        // key order as before (the type after the view's fields)
        Assert.Equal(info.Select(kv => kv.Key), Written(new DatasetDataMsg { Dataset = Held(), CanManage = true, MaxMb = 40 }.Write).D.Keys);
        Assert.Equal(changed.Select(kv => kv.Key), Written(new DatasetChangedMsg { Dataset = Held(), Action = "new" }.Write).D.Keys);
        Assert.Equal(new[] { "t", "op", "name", "ok", "why" }, Written(new DatasetResultMsg { Why = "x" }.Write).D.Keys);
    }
}
