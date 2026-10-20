using System.Text.Json.Nodes;
using Elans.Shared;

namespace Elans.Shared.Tests;

// The typed device and leaving messages: every message survives Write -> Read, and writes exactly the JSON the old hand-built dictionary /
// JsonObject sent (golden values copied from src/Lodge/LodgeSession.Invites.cs, LodgeSession.Leave.cs and server/Lodge.Server/Admin.cs at
// c712015) - the wire must not change.
public class DeviceMessagesTests
{
    static TestFields Written(Action<ILodgeWriter> write) => TestFields.Written(write);
    static void SameJson(object expected, object actual) => TestFields.SameJson(expected, actual);

    static DeviceView Dev(string id, string name, bool current) => new DeviceView
    {
        Id = id, Name = name, Role = name == "" ? "guest" : "member", Device = "Nova-PC", Created = 1_790_000_000_000, LastSeen = 1_790_000_500_000,
        Ip = "203.0.113.x", Current = current, Online = current,
    };

    static JsonObject OldDev(string id, string name, bool current) => new JsonObject
    {
        ["id"] = id, ["name"] = name, ["role"] = name == "" ? "guest" : "member", ["device"] = "Nova-PC",
        ["created"] = 1_790_000_000_000L, ["lastSeen"] = 1_790_000_500_000L, ["ip"] = "203.0.113.x", ["current"] = current, ["online"] = current,
    };

    // ---- round trips

    [Fact]
    public void Client_messages_round_trip()
    {
        Assert.NotNull(DeviceListMsg.Read(Written(new DeviceListMsg().Write)));
        Assert.Equal("d1", DeviceRevokeMsg.Read(Written(new DeviceRevokeMsg { Id = "d1" }.Write)).Id);
        Assert.Equal("Nova", AdminDevicesGetMsg.Read(Written(new AdminDevicesGetMsg { Name = "Nova" }.Write)).Name);
        Assert.Null(AdminDevicesGetMsg.Read(Written(new AdminDevicesGetMsg().Write)).Name);
        Assert.NotNull(MemberLeaveMsg.Read(Written(new MemberLeaveMsg().Write)));
    }

    [Fact]
    public void Server_messages_round_trip()
    {
        var mine = DevicesMsg.Read(Written(new DevicesMsg { Name = "Nova", Now = 1_790_000_600_000, Devices = { Dev("d1", "Nova", true), Dev("d2", "Nova", false) } }.Write));
        Assert.Equal(("Nova", 1_790_000_600_000L), (mine.Name, mine.Now));
        Assert.Equal(new[] { ("d1", "Nova", "member", "Nova-PC", true, true), ("d2", "Nova", "member", "Nova-PC", false, false) },
            mine.Devices.Select(d => (d.Id, d.Name, d.Role, d.Device, d.Current, d.Online)));
        Assert.Equal((1_790_000_000_000L, 1_790_000_500_000L, "203.0.113.x"), (mine.Devices[0].Created, mine.Devices[0].LastSeen, mine.Devices[0].Ip));
        var all = AdminDevicesMsg.Read(Written(new AdminDevicesMsg { Name = "", Now = 5, Devices = { Dev("g1", "", false) } }.Write));
        Assert.Equal(("", 5L, "g1", "guest"), (all.Name, all.Now, all.Devices.Single().Id, all.Devices.Single().Role));
        Assert.NotNull(MemberLeftMsg.Read(Written(new MemberLeftMsg().Write)));
    }

    [Fact]
    public void Each_message_writes_its_own_type()
    {
        Assert.Equal("device.list", Written(new DeviceListMsg().Write).Str("t"));
        Assert.Equal("device.revoke", Written(new DeviceRevokeMsg().Write).Str("t"));
        Assert.Equal("admin.devices", Written(new AdminDevicesGetMsg().Write).Str("t"));
        Assert.Equal("admin.devices", Written(new AdminDevicesMsg().Write).Str("t"));
        Assert.Equal("devices", Written(new DevicesMsg().Write).Str("t"));
        Assert.Equal("member.leave", Written(new MemberLeaveMsg().Write).Str("t"));
        Assert.Equal("member.left", Written(new MemberLeftMsg().Write).Str("t"));
    }

    [Fact]
    public void Reading_is_lenient_like_before()
    {
        // a non-object row is skipped; a missing field is null / 0 / false
        var l = DevicesMsg.Read(new TestFields(new Dictionary<string, object>
        {
            ["devices"] = new List<object> { 3, new Dictionary<string, object> { ["id"] = "d1", ["created"] = "x", ["current"] = 1 } },
        }));
        var d = l.Devices.Single();
        Assert.Equal(("d1", (string)null, 0L, false, (string)null), (d.Id, d.Name, d.Created, d.Current, d.Ip));
        Assert.Equal((null, 0L), (l.Name, l.Now));
        Assert.Empty(AdminDevicesMsg.Read(new TestFields()).Devices);
    }

    // ---- golden: what the old code sent

    [Fact]
    public void Golden_client_messages_match_the_old_Outpost_dictionaries()
    {
        SameJson(new Dictionary<string, object> { ["t"] = "device.list" }, Written(new DeviceListMsg().Write).D);
        SameJson(new Dictionary<string, object> { ["t"] = "admin.devices" }, Written(new AdminDevicesGetMsg().Write).D);
        SameJson(new Dictionary<string, object> { ["t"] = "device.revoke", ["id"] = "d1" }, Written(new DeviceRevokeMsg { Id = "d1" }.Write).D);
        SameJson(new Dictionary<string, object> { ["t"] = "member.leave" }, Written(new MemberLeaveMsg().Write).D);
    }

    [Fact]
    public void Golden_server_messages_match_the_old_JsonObjects()
    {
        // AdminModule.SendDevices / DeviceJson (devices for your own, admin.devices for everyone's) and the guest's own list
        SameJson(new JsonObject { ["t"] = "devices", ["name"] = "Nova", ["now"] = 1_790_000_600_000L, ["devices"] = new JsonArray(OldDev("d1", "Nova", true), OldDev("d2", "Nova", false)) },
            Written(new DevicesMsg { Name = "Nova", Now = 1_790_000_600_000, Devices = { Dev("d1", "Nova", true), Dev("d2", "Nova", false) } }.Write).D);
        SameJson(new JsonObject { ["t"] = "admin.devices", ["name"] = "", ["now"] = 7L, ["devices"] = new JsonArray(OldDev("g1", "", false)) },
            Written(new AdminDevicesMsg { Name = "", Now = 7, Devices = { Dev("g1", "", false) } }.Write).D);
        SameJson(new JsonObject { ["t"] = "devices", ["name"] = "", ["now"] = 7L, ["devices"] = new JsonArray() }, Written(new DevicesMsg { Name = "", Now = 7 }.Write).D);
        // AdminModule.Leave
        SameJson(new JsonObject { ["t"] = "member.left" }, Written(new MemberLeftMsg().Write).D);
        // key order as before: t, name, now, devices; a row as DeviceJson built it
        Assert.Equal(new[] { "t", "name", "now", "devices" }, Written(new DevicesMsg().Write).D.Keys);
        Assert.Equal(new[] { "id", "name", "role", "device", "created", "lastSeen", "ip", "current", "online" }, Written(Dev("d1", "Nova", true).Write).D.Keys);
    }

    // ---- 2.31.2: addedBy on a device row, and device.added

    [Fact]
    public void AddedBy_is_optional_and_left_out_when_empty()
    {
        var own = Dev("d1", "Nova", true);
        Assert.Equal(new[] { "id", "name", "role", "device", "created", "lastSeen", "ip", "current", "online" }, Written(own.Write).D.Keys);
        own.AddedBy = "";
        Assert.Equal(new[] { "id", "name", "role", "device", "created", "lastSeen", "ip", "current", "online" }, Written(own.Write).D.Keys);
        var added = Dev("d2", "Nova", false); added.AddedBy = "Elan";
        var w = Written(added.Write);
        Assert.Equal(new[] { "id", "name", "role", "device", "created", "lastSeen", "ip", "current", "online", "addedBy" }, w.D.Keys);
        var old = OldDev("d2", "Nova", false); old["addedBy"] = "Elan";
        SameJson(old, w.D);
        Assert.Equal("Elan", DeviceView.Read(w).AddedBy);
        Assert.Null(DeviceView.Read(Written(own.Write)).AddedBy);                    // an older Lodge: no addedBy, read as null
        Assert.Null(DeviceView.Read(new TestFields(new Dictionary<string, object> { ["addedBy"] = 5 })).AddedBy);   // wrong type: missing
    }

    [Fact]
    public void Device_added_golden_and_round_trip()
    {
        var m = new DeviceAddedMsg { Id = "d9", Name = "Laptop", By = "Elan", At = 1_790_000_700_000 };
        var w = Written(m.Write);
        Assert.Equal(new[] { "t", "id", "name", "by", "at" }, w.D.Keys);
        SameJson(new JsonObject { ["t"] = "device.added", ["id"] = "d9", ["name"] = "Laptop", ["by"] = "Elan", ["at"] = 1_790_000_700_000L }, w.D);
        var r = DeviceAddedMsg.Read(w);
        Assert.Equal(("d9", "Laptop", "Elan", 1_790_000_700_000L), (r.Id, r.Name, r.By, r.At));
        // lenient: a malformed frame reads as null / 0, never throws
        var odd = DeviceAddedMsg.Read(new TestFields(new Dictionary<string, object> { ["id"] = 3, ["at"] = "x", ["by"] = true }));
        Assert.Equal(((string)null, (string)null, (string)null, 0L), (odd.Id, odd.Name, odd.By, odd.At));
        Assert.Equal("device.added", Written(new DeviceAddedMsg().Write).Str("t"));
    }
}
