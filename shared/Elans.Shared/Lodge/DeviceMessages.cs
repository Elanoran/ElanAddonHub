using System.Collections.Generic;

namespace Elans.Shared
{
    // Devices and leaving (server/PROTOCOL.md, 2.10 and 2.12 additions): the field names live only here. The classes only map fields; every
    // rule (who may see and sign out which device, officers only below their own rank, the last-owner rule, what leaving removes) stays in
    // the Lodge server (Admin.cs, Auth.Tokens.cs).
    // Secrets: a device's token never appears in any of these messages - a device is named by its id (and its name, coarse address and times).
    // Not here: join / leave / user (presence, with the member's user entry; typed with the presence messages later).

    // ---------------------------------------------------------------- client -> server

    // your own devices (a guest sees only the one they are on); answered with DevicesMsg
    public sealed class DeviceListMsg : LodgeEmptyMsg<DeviceListMsg> { public const string Type = LodgeMsg.DeviceList; protected override string MsgType => Type; }

    // sign a device out by id: yours, or (Perm.Invite) a lower rank's (owner: anyone's); answered with DevicesMsg (yours) or AdminDevicesMsg
    public sealed class DeviceRevokeMsg : LodgeIdMsg<DeviceRevokeMsg> { public const string Type = LodgeMsg.DeviceRevoke; protected override string MsgType => Type; }

    // (Perm.Invite) every member's devices, or one member's by name; answered with AdminDevicesMsg
    public sealed class AdminDevicesGetMsg
    {
        public const string Type = LodgeMsg.AdminDevices;
        public string Name;            // null = everyone (left out)

        public void Write(ILodgeWriter w) { w.Set("t", Type); if (Name != null) w.Set("name", Name); }
        public static AdminDevicesGetMsg Read(ILodgeFields f) => new AdminDevicesGetMsg { Name = f.Str("name") };
    }

    // leave the Lodge for good (2.12): answered with MemberLeftMsg, or an error with code "last-owner"
    public sealed class MemberLeaveMsg : LodgeEmptyMsg<MemberLeaveMsg> { public const string Type = LodgeMsg.MemberLeave; protected override string MsgType => Type; }

    // ---------------------------------------------------------------- server -> client

    // a device list { name ("" = everyone / a guest), now (unix ms), devices: [DeviceView] } - devices and admin.devices
    public abstract class LodgeDeviceListMsg<TSelf> where TSelf : LodgeDeviceListMsg<TSelf>, new()
    {
        public string Name;
        public long Now;
        public List<DeviceView> Devices = new List<DeviceView>();
        protected abstract string MsgType { get; }

        public void Write(ILodgeWriter w)
        {
            w.Set("t", MsgType); w.Set("name", Name); w.Set("now", Now);
            var l = w.List("devices"); foreach (var d in Devices) d.Write(l.Add());
        }

        public static TSelf Read(ILodgeFields f)
        {
            var r = new TSelf { Name = f.Str("name"), Now = f.Long("now") };
            foreach (var d in f.List("devices")) r.Devices.Add(DeviceView.Read(d));
            return r;
        }
    }

    // your own devices (the answer to device.list, and to device.revoke of one of yours)
    public sealed class DevicesMsg : LodgeDeviceListMsg<DevicesMsg> { public const string Type = LodgeMsg.Devices; protected override string MsgType => Type; }

    // (Perm.Invite) every member's devices (the answer to admin.devices, and to device.revoke of someone else's)
    public sealed class AdminDevicesMsg : LodgeDeviceListMsg<AdminDevicesMsg> { public const string Type = LodgeMsg.AdminDevices; protected override string MsgType => Type; }

    // a device { id, name ("" for a guest), role, device (its name), created, lastSeen (unix ms), ip (coarse), current (the one asking), online }
    public sealed class DeviceView
    {
        public string Id, Name, Role, Device, Ip;
        public long Created, LastSeen;
        public bool Current, Online;

        public void Write(ILodgeWriter w)
        {
            w.Set("id", Id); w.Set("name", Name); w.Set("role", Role); w.Set("device", Device);
            w.Set("created", Created); w.Set("lastSeen", LastSeen); w.Set("ip", Ip);
            w.Set("current", Current); w.Set("online", Online);
        }

        public static DeviceView Read(ILodgeFields f) => new DeviceView
        {
            Id = f.Str("id"), Name = f.Str("name"), Role = f.Str("role"), Device = f.Str("device"),
            Created = f.Long("created"), LastSeen = f.Long("lastSeen"), Ip = f.Str("ip"), Current = f.Bool("current"), Online = f.Bool("online"),
        };
    }

    // you have left the Lodge (or this guest device was signed out); the socket closes next
    public sealed class MemberLeftMsg : LodgeEmptyMsg<MemberLeftMsg> { public const string Type = LodgeMsg.MemberLeft; protected override string MsgType => Type; }
}
