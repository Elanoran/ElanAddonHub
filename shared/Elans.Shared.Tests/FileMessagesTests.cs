using System.Text.Json.Nodes;
using Elans.Shared;

namespace Elans.Shared.Tests;

// The typed shared-file messages: every message survives Write -> Read, and writes exactly the JSON the old hand-built dictionary /
// JsonObject sent (golden values copied from src/Lodge/LodgeSession.Files.cs and server/Lodge.Server/FilesModule.cs at c712015) - the
// wire must not change.
public class FileMessagesTests
{
    static TestFields Written(Action<ILodgeWriter> write) => TestFields.Written(write);
    static void SameJson(object expected, object actual) => TestFields.SameJson(expected, actual);

    const string SlotId = "0123456789abcdef0123456789abcdef";
    const string Sha = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    static FileVersion Ver(int v, string note) => new FileVersion
    {
        V = v, FileName = "pack.zip", Size = 1234 + v, Sha256 = Sha, UploadedBy = "Wing", At = 1_790_000_000_000 + v, Note = note,
    };

    static FileSlot Slot() => new FileSlot { Id = SlotId, Name = "Raid UI pack", CreatedBy = "Wing", Versions = { Ver(1, "first cut"), Ver(2, "") } };

    // FilesModule.VersionJson / SlotJson at c712015
    static JsonObject OldVer(int v, string note) => new JsonObject
    {
        ["v"] = v, ["fileName"] = "pack.zip", ["size"] = 1234L + v, ["sha256"] = Sha, ["uploadedBy"] = "Wing", ["at"] = 1_790_000_000_000L + v, ["note"] = note,
    };

    static JsonObject OldSlot() => new JsonObject
    {
        ["id"] = SlotId, ["name"] = "Raid UI pack", ["createdBy"] = "Wing", ["versions"] = new JsonArray(OldVer(1, "first cut"), OldVer(2, "")),
    };

    // ---- round trips

    [Fact]
    public void Client_messages_round_trip()
    {
        Assert.NotNull(FilesListMsg.Read(Written(new FilesListMsg().Write)));
        Assert.Equal(SlotId, FilesDeleteMsg.Read(Written(new FilesDeleteMsg { Slot = SlotId }.Write)).Slot);
        var rb = FilesRollbackMsg.Read(Written(new FilesRollbackMsg { Slot = SlotId, V = 3 }.Write));
        Assert.Equal((SlotId, 3), (rb.Slot, rb.V));
    }

    [Fact]
    public void Server_messages_round_trip()
    {
        var d = FilesDataMsg.Read(Written(new FilesDataMsg { MaxMb = 50, TotalMb = 500, UsedBytes = 2471, Keep = 3, CanManage = true, Slots = { Slot() } }.Write));
        Assert.Equal((50, 500, 2471L, 3, true), (d.MaxMb, d.TotalMb, d.UsedBytes, d.Keep, d.CanManage));
        var s = d.Slots.Single();
        Assert.Equal((SlotId, "Raid UI pack", "Wing"), (s.Id, s.Name, s.CreatedBy));
        Assert.Equal(new[] { (1, "pack.zip", 1235L, Sha, "Wing", 1_790_000_000_001L, "first cut"), (2, "pack.zip", 1236L, Sha, "Wing", 1_790_000_000_002L, "") },
            s.Versions.Select(v => (v.V, v.FileName, v.Size, v.Sha256, v.UploadedBy, v.At, v.Note)));

        var c = FilesChangedMsg.Read(Written(new FilesChangedMsg { Action = "version", Slot = SlotId, Name = "Raid UI pack", By = "Wing", At = 7, V = 2 }.Write));
        Assert.Equal(("version", SlotId, "Raid UI pack", "Wing", 7L, (int?)2), (c.Action, c.Slot, c.Name, c.By, c.At, c.V));
        Assert.Null(FilesChangedMsg.Read(Written(new FilesChangedMsg { Action = "delete", Slot = SlotId }.Write)).V);

        var r = FilesResultMsg.Read(Written(new FilesResultMsg { Op = "rollback", Ok = false, Slot = SlotId, Why = "already-newest" }.Write));
        Assert.Equal(("rollback", false, SlotId, "already-newest"), (r.Op, r.Ok, r.Slot, r.Why));
        var l = FilesResultMsg.Read(Written(new FilesResultMsg { Op = "list", Ok = false, Why = "disabled" }.Write));
        Assert.Null(l.Slot);
    }

    [Fact]
    public void Each_message_writes_its_own_type()
    {
        Assert.Equal("files.list", Written(new FilesListMsg().Write).Str("t"));
        Assert.Equal("files.delete", Written(new FilesDeleteMsg().Write).Str("t"));
        Assert.Equal("files.rollback", Written(new FilesRollbackMsg().Write).Str("t"));
        Assert.Equal("files.data", Written(new FilesDataMsg().Write).Str("t"));
        Assert.Equal("files.changed", Written(new FilesChangedMsg().Write).Str("t"));
        Assert.Equal("files.result", Written(new FilesResultMsg().Write).Str("t"));
    }

    [Fact]
    public void Reading_is_lenient_like_before()
    {
        // a non-object slot or version is skipped; a field of the wrong type is null / 0 / false
        var d = FilesDataMsg.Read(new TestFields(new Dictionary<string, object>
        {
            ["maxMb"] = "50", ["canManage"] = 1,
            ["slots"] = new List<object> { "x", new Dictionary<string, object> { ["id"] = 5, ["versions"] = new List<object> { 1, new Dictionary<string, object> { ["v"] = "2" } } } },
        }));
        Assert.Equal((0, false), (d.MaxMb, d.CanManage));
        var s = d.Slots.Single();
        Assert.Null(s.Id);
        Assert.Equal((0, (string)null, 0L), (s.Versions.Single().V, s.Versions.Single().Sha256, s.Versions.Single().Size));
        Assert.Empty(FilesDataMsg.Read(new TestFields()).Slots);
        Assert.Equal(((string)null, 0), (FilesRollbackMsg.Read(new TestFields()).Slot, FilesRollbackMsg.Read(new TestFields()).V));
        Assert.Null(FilesChangedMsg.Read(new TestFields()).V);
    }

    // ---- golden: what the old code sent

    [Fact]
    public void Golden_client_messages_match_the_old_Outpost_dictionaries()
    {
        SameJson(new Dictionary<string, object> { ["t"] = "files.list" }, Written(new FilesListMsg().Write).D);
        SameJson(new Dictionary<string, object> { ["t"] = "files.delete", ["slot"] = SlotId }, Written(new FilesDeleteMsg { Slot = SlotId }.Write).D);
        SameJson(new Dictionary<string, object> { ["t"] = "files.rollback", ["slot"] = SlotId, ["v"] = 2 }, Written(new FilesRollbackMsg { Slot = SlotId, V = 2 }.Write).D);
    }

    [Fact]
    public void Golden_server_messages_match_the_old_JsonObjects()
    {
        // FilesModule.ListJson
        SameJson(new JsonObject
        {
            ["t"] = "files.data", ["maxMb"] = 50, ["totalMb"] = 500, ["usedBytes"] = 2471L, ["keep"] = 3, ["canManage"] = true, ["slots"] = new JsonArray(OldSlot()),
        }, Written(new FilesDataMsg { MaxMb = 50, TotalMb = 500, UsedBytes = 2471, Keep = 3, CanManage = true, Slots = { Slot() } }.Write).D);
        SameJson(new JsonObject { ["t"] = "files.data", ["maxMb"] = 1, ["totalMb"] = 2, ["usedBytes"] = 0L, ["keep"] = 3, ["canManage"] = false, ["slots"] = new JsonArray() },
            Written(new FilesDataMsg { MaxMb = 1, TotalMb = 2, Keep = 3 }.Write).D);
        // the HTTP upload's answer (SlotJson)
        SameJson(OldSlot(), Written(Slot().Write).D);
        // FilesModule.Announce (v left out on delete)
        SameJson(new JsonObject { ["t"] = "files.changed", ["action"] = "new", ["slot"] = SlotId, ["name"] = "Raid UI pack", ["by"] = "Wing", ["at"] = 7L, ["v"] = 1 },
            Written(new FilesChangedMsg { Action = "new", Slot = SlotId, Name = "Raid UI pack", By = "Wing", At = 7, V = 1 }.Write).D);
        SameJson(new JsonObject { ["t"] = "files.changed", ["action"] = "delete", ["slot"] = SlotId, ["name"] = "Raid UI pack", ["by"] = "Wing", ["at"] = 7L },
            Written(new FilesChangedMsg { Action = "delete", Slot = SlotId, Name = "Raid UI pack", By = "Wing", At = 7 }.Write).D);
        // FilesModule.Result (slot always there, null for list; why only on a refusal)
        SameJson(new JsonObject { ["t"] = "files.result", ["op"] = "delete", ["ok"] = true, ["slot"] = SlotId },
            Written(new FilesResultMsg { Op = "delete", Ok = true, Slot = SlotId }.Write).D);
        SameJson(new JsonObject { ["t"] = "files.result", ["op"] = "list", ["ok"] = false, ["slot"] = null, ["why"] = "disabled" },
            Written(new FilesResultMsg { Op = "list", Ok = false, Why = "disabled" }.Write).D);
        // key order as before
        Assert.Equal(new[] { "t", "maxMb", "totalMb", "usedBytes", "keep", "canManage", "slots" }, Written(new FilesDataMsg().Write).D.Keys);
        Assert.Equal(new[] { "id", "name", "createdBy", "versions" }, Written(Slot().Write).D.Keys);
        Assert.Equal(new[] { "v", "fileName", "size", "sha256", "uploadedBy", "at", "note" }, Written(Ver(1, "").Write).D.Keys);
        Assert.Equal(new[] { "t", "action", "slot", "name", "by", "at", "v" }, Written(new FilesChangedMsg { V = 1 }.Write).D.Keys);
        Assert.Equal(new[] { "t", "op", "ok", "slot", "why" }, Written(new FilesResultMsg { Why = "x" }.Write).D.Keys);
    }
}
