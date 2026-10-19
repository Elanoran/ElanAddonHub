using System.Text.Json.Nodes;
using Elans.Shared;

namespace Lodge;

// Guild Master panel (members, ranks, invites, channels) and officer tools (kick, server-mute).
// Every request is checked against the sender's rank here - the hub only hides buttons.
// The messages are typed in shared/Elans.Shared/Lodge/AdminMessages.cs (devices and leaving: DeviceMessages.cs); admin.purge's "atlasOnly" is still read from the raw JSON below
// (only a JSON false turns it off).
public class AdminModule : IModule
{
    readonly LodgeHub hub;
    public AdminModule(LodgeHub hub) { this.hub = hub; }

    // exactly the admin.* / mod.* types answered here. Others with the same prefix (admin.badges, admin.cairn) belong to their own modules,
    // so this module says "not handled" for them and the order of the hub's module list doesn't matter.
    public static readonly IReadOnlySet<string> Handles = new HashSet<string>
    {
        LodgeMsg.AdminInviteCreate, LodgeMsg.AdminInvites, LodgeMsg.AdminInviteRevoke, LodgeMsg.AdminDevices, LodgeMsg.AdminRetire, LodgeMsg.AdminLodgeSet,
        LodgeMsg.AdminMembers, LodgeMsg.AdminInvite, LodgeMsg.AdminRole, LodgeMsg.AdminRemove, LodgeMsg.AdminPurge,
        LodgeMsg.AdminChannelAdd, LodgeMsg.AdminChannelRemove, LodgeMsg.ModKick, LodgeMsg.ModMute,
    };

    public async Task<bool> Handle(Member me, string type, JsonObject m)
    {
        if (type.StartsWith("device.")) return await HandleDevice(me, type, m);
        if (type == MemberLeaveMsg.Type) return await Leave(me);       // { t } only
        if (!Handles.Contains(type)) return false;
        var perm = type switch
        {
            LodgeMsg.AdminInviteCreate or LodgeMsg.AdminInvites or LodgeMsg.AdminInviteRevoke or LodgeMsg.AdminDevices => Perm.Invite,   // officers too (2.10)
            LodgeMsg.AdminRetire => Perm.RetireCodes,
            LodgeMsg.AdminLodgeSet => Perm.ManageMembers,
            _ => type.StartsWith("mod.") ? Perm.Moderate : type.StartsWith("admin.channel") ? Perm.ManageChannels : Perm.ManageMembers,
        };
        if (!Roles.Can(me.Role, perm)) { await hub.Error(me, "Your rank can't do that"); return true; }
        try
        {
            switch (type)
            {
                case LodgeMsg.AdminInviteCreate: await CreateInvite(me, m); break;
                case LodgeMsg.AdminInvites: await SendInvites(me); break;
                case LodgeMsg.AdminInviteRevoke: await RevokeInvite(me, m); break;
                case LodgeMsg.AdminDevices: await SendDevices(me, LodgeMsg.AdminDevices, AdminDevicesGetMsg.Read(new LodgeJsonFields(m)).Name); break;
                case LodgeMsg.AdminRetire: await Retire(me, m); break;
                case LodgeMsg.AdminLodgeSet: await SetLodgeInfo(me, m); break;
                case LodgeMsg.AdminMembers: await SendMembers(me); break;
                case LodgeMsg.AdminInvite: await Invite(me, m); break;
                case LodgeMsg.AdminRole: await SetRole(me, m); break;
                case LodgeMsg.AdminRemove: await Remove(me, m); break;
                case LodgeMsg.AdminPurge: await Purge(me, m); break;
                case LodgeMsg.AdminChannelAdd: await AddChannel(me, m); break;
                case LodgeMsg.AdminChannelRemove: await RemoveChannel(me, m); break;
                case LodgeMsg.ModKick: await Kick(me, m); break;
                case LodgeMsg.ModMute: await ServerMute(me, m); break;
            }
        }
        catch (UnauthorizedAccessException)
        {
            await hub.Error(me, "The server can't write its codes file - run the server update (install.sh)");
        }
        catch (Exception e)
        {
            hub.Log.LogWarning(e, "{Type} failed", type);
            await hub.Error(me, e.Message);
        }
        return true;
    }

    Task SendMembers(Member me) => hub.Send(me, LodgeJson.Build(new AdminMembersMsg
    {
        Members = hub.Auth.ListPersonal().Select(p => new AdminMember
        {
            Name = p.Name, Role = p.Role, HasCode = hub.Auth.HasPermanentCode(p.Name), Devices = hub.Auth.ListDevices(p.Name).Count,
            Online = hub.Members.Any(x => x.Personal && LodgeHub.Same(x.Name, p.Name) && hub.CanSee(me, x)),
        }).ToList(),
        Channels = hub.Channels.All.Select(c => c.View()).ToList(),
    }.Write));

    async Task Invite(Member me, JsonObject m)
    {
        var req = AdminInviteMsg.Read(new LodgeJsonFields(m));
        var name = (req.Name ?? "").Trim();
        var role = Roles.Normalize(req.Role);
        var code = hub.Auth.Add(name, role); // throws with a readable message when the name is bad or taken
        hub.Log.LogInformation("{By} invited {Name} as {Role}", me.Name, name, role);
        await hub.Send(me, LodgeJson.Build(new AdminInvitedMsg { Name = name, Role = role, Code = code }.Write));   // the code: only to whoever asked, never logged
        await SendMembers(me);
    }

    // ---------------------------------------------------------------- one-time invites and devices (2.10)

    static long Ms(DateTime t) => new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    InviteView InviteOf(InviteInfo i)
    {
        var now = hub.Clock.UtcNow;
        return new InviteView
        {
            Id = i.Id, Kind = i.Kind, Name = i.Kind == "guest" ? "" : i.Name, Role = i.Role, By = i.CreatedBy,
            Created = Ms(i.Created), Expires = Ms(i.Expires), Max = i.MaxUses, Uses = i.Uses,
            Used = i.Used, Expired = i.Expired(now), Revoked = i.Revoked, Pending = i.Pending(now),
        };
    }

    async Task SendInvites(Member me) => await hub.Send(me, LodgeJson.Build(new AdminInvitesMsg
    {
        Now = Ms(hub.Clock.UtcNow),
        Invites = hub.Auth.ListInvites().Where(i => i.Kind != Auth.ClaimKind).Select(InviteOf).ToList(),   // never a secret (the store keeps only its hash)
    }.Write));

    // A person invite makes (or re-uses, for another device) the name; officers can only invite below their own rank.
    // The secret is in this one reply and nowhere else; it is never logged.
    async Task CreateInvite(Member me, JsonObject m)
    {
        var req = AdminInviteCreateMsg.Read(new LodgeJsonFields(m));
        bool guest = req.Kind == "guest";
        string name = guest ? "" : (req.Name ?? "").Trim();
        var role = Roles.Normalize(req.Role);
        if (!guest)
        {
            var existing = hub.Auth.ListPersonal().FirstOrDefault(p => Names.Same(p.Name, name));
            var target = existing.Name != null ? existing.Role : role;
            bool isOwner = Roles.Level(me.Role) >= Roles.Level("owner");
            if (!isOwner && Roles.Level(target) >= Roles.Level(me.Role))
                throw new InvalidOperationException(existing.Name != null ? "You can only invite people below your rank" : "You can only invite ranks below your own");
        }
        int hours = req.Hours;
        if (hours <= 0 && req.Days > 0) hours = req.Days * 24;
        var (info, secret) = hub.Auth.CreateInvite(guest ? "guest" : "person", name, role, me.Name, hours > 0 ? hours : null, req.Uses);
        hub.Log.LogInformation("{By} created invite {Id} ({Kind}, {Role}) expiring {Expires:u}", me.Name, info.Id, info.Kind, info.Role, info.Expires);
        await hub.Send(me, LodgeJson.Build(new AdminInviteCreatedMsg { Invite = InviteOf(info), Secret = secret }.Write));
        await SendInvites(me);
    }

    async Task RevokeInvite(Member me, JsonObject m)
    {
        var info = hub.Auth.RevokeInvite(AdminInviteRevokeMsg.Read(new LodgeJsonFields(m)).Id);
        hub.Log.LogInformation("{By} revoked invite {Id}", me.Name, info.Id);
        await SendInvites(me);
    }

    // a device as the lists show it: by id, never with its token (the store keeps only the token's hash)
    DeviceView DeviceOf(DeviceInfo d, Member viewer) => new()
    {
        Id = d.Id, Name = d.Guest ? "" : d.Name, Role = d.Role, Device = d.DeviceName,
        Created = Ms(d.Created), LastSeen = Ms(d.LastSeen), Ip = d.LastIp ?? "",
        Current = viewer.DeviceId == d.Id, Online = hub.Members.Any(x => x.DeviceId == d.Id),
    };

    // devices (your own) or admin.devices (everyone's, or one member's)
    JsonObject DeviceList(Member me, string type, string name, IEnumerable<DeviceInfo> list)
    {
        var rows = list.Select(d => DeviceOf(d, me)).ToList();
        long now = Ms(hub.Clock.UtcNow);
        return type == LodgeMsg.Devices
            ? LodgeJson.Build(new DevicesMsg { Name = name ?? "", Now = now, Devices = rows }.Write)
            : LodgeJson.Build(new AdminDevicesMsg { Name = name ?? "", Now = now, Devices = rows }.Write);
    }

    Task SendDevices(Member me, string type, string name) =>
        hub.Send(me, DeviceList(me, type, name, hub.Auth.ListDevices(string.IsNullOrWhiteSpace(name) ? null : name)));

    // device.list: your own devices. device.revoke {id}: sign one out - yours, or (officer+) a lower-ranked member's.
    async Task<bool> HandleDevice(Member me, string type, JsonObject m)
    {
        try
        {
            switch (type)
            {
                case LodgeMsg.DeviceList:
                    // a guest sees only the device they are on
                    if (me.Personal) await SendDevices(me, LodgeMsg.Devices, me.Name);
                    else await hub.Send(me, DeviceList(me, LodgeMsg.Devices, "", hub.Auth.ListDevices().Where(d => d.Id == me.DeviceId)));
                    break;
                case LodgeMsg.DeviceRevoke:
                {
                    var d = hub.Auth.FindDevice(DeviceRevokeMsg.Read(new LodgeJsonFields(m)).Id) ?? throw new InvalidOperationException("That device is already signed out");
                    bool mine = d.Guest ? d.Id == me.DeviceId : me.Personal && Names.Same(d.Name, me.Name);
                    if (!mine)
                    {
                        if (!Roles.Can(me.Role, Perm.Invite)) throw new InvalidOperationException("Your rank can't do that");
                        var theirs = d.Guest ? "guest" : hub.Auth.ListPersonal().FirstOrDefault(p => Names.Same(p.Name, d.Name)).Role ?? d.Role;
                        if (Roles.Level(theirs) >= Roles.Level(me.Role) && Roles.Level(me.Role) < Roles.Level("owner"))
                            throw new InvalidOperationException("You can only do that to lower ranks");
                    }
                    hub.Auth.RevokeDevice(d.Id);
                    hub.Log.LogInformation("{By} signed out device {Id} of {Name}", me.Name, d.Id, d.Guest ? "a guest" : Names.ForLog(d.Name));
                    await hub.DisconnectDevice(d.Id);   // the socket closes now, not at the next 15 s check
                    if (hub.Find(me.Id) != null) await SendDevices(me, mine ? LodgeMsg.Devices : LodgeMsg.AdminDevices, mine ? me.Name : null);
                    break;
                }
                default: return false;
            }
        }
        catch (UnauthorizedAccessException) { await hub.Error(me, "The server can't write its device list - run the server update (install.sh)"); }
        catch (InvalidOperationException e) { await hub.Error(me, e.Message); }
        return true;
    }

    // owner: the person's old permanent code stops working (they must already have a device on a token)
    async Task Retire(Member me, JsonObject m)
    {
        var req = AdminRetireMsg.Read(new LodgeJsonFields(m));
        var name = req.Name;
        hub.Auth.RetireCode(name, req.Force);
        hub.Log.LogInformation("{By} retired the permanent code of {Name}", me.Name, Names.ForLog(name));
        await hub.ApplyCodeChanges();
        await SendMembers(me);
    }

    async Task SetRole(Member me, JsonObject m)
    {
        var req = AdminRoleMsg.Read(new LodgeJsonFields(m));
        var name = req.Name;
        var role = Roles.Normalize(req.Role);
        hub.Auth.SetRole(name, role);   // refuses to demote the last Guild Master
        hub.Log.LogInformation("{By} made {Name} {Role}", me.Name, name, role);
        await hub.ApplyCodeChanges();
        await SendMembers(me);
    }

    async Task Remove(Member me, JsonObject m)
    {
        var name = AdminRemoveMsg.Read(new LodgeJsonFields(m)).Name;
        if (LodgeHub.Same(name, me.Name)) throw new InvalidOperationException("You can't remove yourself");
        hub.Auth.Remove(name);
        var gone = await hub.PurgeMember(name);         // everything they authored goes with them (see LodgeHub.PurgeMember)
        hub.Log.LogInformation("{By} removed {Name} ({Count} stored item(s) forgotten)", me.Name, name, gone.Total);
        await hub.ApplyCodeChanges();
        await SendMembers(me);
    }

    // owner: "revert member". atlasOnly (the default) takes one member's Atlas contributions out of the pool and leaves them a member - the undo for
    // poisoned data. atlasOnly = false forgets everything stored about a person who is NO LONGER a member (leftovers from before 2.22, or a console removal).
    async Task Purge(Member me, JsonObject m)
    {
        var name = (AdminPurgeMsg.Read(new LodgeJsonFields(m)).Name ?? "").Trim();
        if (name.Length == 0) throw new InvalidOperationException("Pick a member");
        bool atlasOnly = !(m["atlasOnly"] is JsonValue v && v.TryGetValue<bool>(out var b) && !b);     // raw on purpose: only a JSON false turns it off
        if (!atlasOnly && hub.Auth.ListPersonal().Any(p => Names.Same(p.Name, name)))
            throw new InvalidOperationException("They are still a member - revert their Atlas contributions, or remove them first");
        var r = await hub.PurgeMember(name, atlasOnly);
        hub.Log.LogInformation("{By} {What} {Name}: {Count} item(s)", me.Name, atlasOnly ? "reverted the Atlas contributions of" : "purged the stored data of", Names.ForLog(name), r.Total);
        await hub.Send(me, LodgeJson.Build(r.ToMsg().Write));
    }

    // owner: the lodge's display name and welcome text (validated and length-capped by LodgeInfoStore)
    async Task SetLodgeInfo(Member me, JsonObject m)
    {
        var req = AdminLodgeSetMsg.Read(new LodgeJsonFields(m));
        hub.Info.Set(req.Name, req.Welcome);
        hub.Log.LogInformation("{By} changed the lodge name / welcome text", me.Name);
        await hub.Broadcast(LodgeJson.Build(new LodgeInfoMsg { Lodge = hub.Info.View() }.Write));
    }

    // "Leave the lodge": the person's membership goes (codes entry, all devices, pending device links); their messages stay.
    // The last Guild Master is refused with code "last-owner" - they transfer ownership first (admin.role -> owner for someone else).
    async Task<bool> Leave(Member me)
    {
        try
        {
            if (!me.Personal)
            {
                // a guest has no membership: this only signs the device out
                if (me.DeviceId != null) { hub.Auth.RevokeDevice(me.DeviceId); await hub.Send(me, LodgeJson.Build(new MemberLeftMsg().Write)); await hub.DisconnectDevice(me.DeviceId); }
                else await hub.Error(me, "Nothing to leave");
                return true;
            }
            var devices = hub.Auth.Leave(me.Name);
            var gone = await hub.PurgeMember(me.Name);      // everything they authored goes (see LodgeHub.PurgeMember)
            hub.Log.LogInformation("{Name} left the lodge ({Count} devices signed out, {Items} stored item(s) forgotten)", Names.ForLog(me.Name), devices.Count, gone.Total);
            await hub.Send(me, LodgeJson.Build(new MemberLeftMsg().Write));
            foreach (var d in devices) await hub.DisconnectDevice(d.Id, "You left the lodge");
            await hub.ApplyCodeChanges();   // sessions on a permanent code, and anything left over
        }
        catch (InvalidOperationException e) when (e.Message == Auth.LastOwnerMessage)
        {
            await hub.Error(me, "Transfer ownership first", "last-owner");
        }
        catch (InvalidOperationException e) { await hub.Error(me, e.Message); }
        catch (UnauthorizedAccessException) { await hub.Error(me, "The server can't write its codes file - run the server update (install.sh)"); }
        return true;
    }

    async Task AddChannel(Member me, JsonObject m)
    {
        var req = AdminChannelAddMsg.Read(new LodgeJsonFields(m));
        var name = (req.Name ?? "").Trim();
        if (name.Length is < 1 or > 24) throw new InvalidOperationException("Channel names are 1-24 characters");
        var ch = hub.Channels.Add(name, req.ChannelType, req.MinRole, req.Max);
        hub.Log.LogInformation("{By} added channel {Id}", me.Name, ch.Id);
        await hub.Resync();
        await SendMembers(me);
    }

    async Task RemoveChannel(Member me, JsonObject m)
    {
        var id = AdminChannelRemoveMsg.Read(new LodgeJsonFields(m)).Id;
        if (!hub.Channels.Remove(id)) throw new InvalidOperationException("That channel can't be removed (the last text channel stays)");
        hub.Chat.DropChannel(id);
        foreach (var x in hub.Members.Where(x => x.Room == id)) x.Room = null;
        hub.Log.LogInformation("{By} removed channel {Id}", me.Name, id);
        await hub.Resync();
        await SendMembers(me);
    }

    // officers can only act on lower ranks
    Member Target(Member me, int id)
    {
        var t = hub.Find(id) ?? throw new InvalidOperationException("They're not online");
        if (!hub.CanSee(me, t)) throw new InvalidOperationException("They're not online"); // an officer can't act on someone invisible
        if (Roles.Level(t.Role) >= Roles.Level(me.Role)) throw new InvalidOperationException("You can only do that to lower ranks");
        return t;
    }

    async Task Kick(Member me, JsonObject m)
    {
        var t = Target(me, ModKickMsg.Read(new LodgeJsonFields(m)).Id);
        hub.Log.LogInformation("{By} kicked {Name}", me.Name, t.Name);
        await hub.Disconnect(t, $"Kicked by {me.Name}");
    }

    async Task ServerMute(Member me, JsonObject m)
    {
        var req = ModMuteMsg.Read(new LodgeJsonFields(m));
        var t = Target(me, req.Id);
        bool on = req.On;
        hub.SetServerMuted(t, on); // by identity: a reconnect keeps it
        hub.Log.LogInformation("{By} {Action} {Name}", me.Name, on ? "server-muted" : "unmuted", t.Name);
        await hub.BroadcastUser(t);
    }
}
