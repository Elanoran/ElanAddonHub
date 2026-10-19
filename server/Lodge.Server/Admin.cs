using System.Text.Json.Nodes;
using Elans.Shared;

namespace Lodge;

// Guild Master panel (members, ranks, invites, channels) and officer tools (kick, server-mute).
// Every request is checked against the sender's rank here - the hub only hides buttons.
public class AdminModule : IModule
{
    readonly LodgeHub hub;
    public AdminModule(LodgeHub hub) { this.hub = hub; }

    public async Task<bool> Handle(Member me, string type, JsonObject m)
    {
        if (type.StartsWith("device.")) return await HandleDevice(me, type, m);
        if (type == LodgeMsg.MemberLeave) return await Leave(me);
        if (!type.StartsWith("admin.") && !type.StartsWith("mod.")) return false;
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
                case LodgeMsg.AdminDevices: await SendDevices(me, LodgeMsg.AdminDevices, J.Str(m, "name")); break;
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

    Task SendMembers(Member me) => hub.Send(me, new JsonObject
    {
        ["t"] = LodgeMsg.AdminMembers,
        ["members"] = new JsonArray(hub.Auth.ListPersonal().Select(p => (JsonNode)new JsonObject
        {
            ["name"] = p.Name, ["role"] = p.Role, ["hasCode"] = hub.Auth.HasPermanentCode(p.Name), ["devices"] = hub.Auth.ListDevices(p.Name).Count,
            ["online"] = hub.Members.Any(x => x.Personal && LodgeHub.Same(x.Name, p.Name) && hub.CanSee(me, x)),
        }).ToArray()),
        ["channels"] = new JsonArray(hub.Channels.All.Select(c => (JsonNode)c.ToJson()).ToArray()),
    });

    async Task Invite(Member me, JsonObject m)
    {
        var name = (J.Str(m, "name") ?? "").Trim();
        var role = Roles.Normalize(J.Str(m, "role"));
        var code = hub.Auth.Add(name, role); // throws with a readable message when the name is bad or taken
        hub.Log.LogInformation("{By} invited {Name} as {Role}", me.Name, name, role);
        await hub.Send(me, new JsonObject { ["t"] = LodgeMsg.AdminInvited, ["name"] = name, ["role"] = role, ["code"] = code });
        await SendMembers(me);
    }

    // ---------------------------------------------------------------- one-time invites and devices (2.10)

    static long Ms(DateTime t) => new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    JsonObject InviteJson(InviteInfo i)
    {
        var now = hub.Clock.UtcNow;
        return new JsonObject
        {
            ["id"] = i.Id, ["kind"] = i.Kind, ["name"] = i.Kind == "guest" ? "" : i.Name, ["role"] = i.Role, ["by"] = i.CreatedBy,
            ["created"] = Ms(i.Created), ["expires"] = Ms(i.Expires), ["max"] = i.MaxUses, ["uses"] = i.Uses,
            ["used"] = i.Used, ["expired"] = i.Expired(now), ["revoked"] = i.Revoked, ["pending"] = i.Pending(now),
        };
    }

    async Task SendInvites(Member me) => await hub.Send(me, new JsonObject
    {
        ["t"] = LodgeMsg.AdminInvites, ["now"] = Ms(hub.Clock.UtcNow),
        ["invites"] = new JsonArray(hub.Auth.ListInvites().Where(i => i.Kind != Auth.ClaimKind).Select(i => (JsonNode)InviteJson(i)).ToArray()),
    });

    // A person invite makes (or re-uses, for another device) the name; officers can only invite below their own rank.
    // The secret is in this one reply and nowhere else; it is never logged.
    async Task CreateInvite(Member me, JsonObject m)
    {
        bool guest = J.Str(m, "kind") == "guest";
        string name = guest ? "" : (J.Str(m, "name") ?? "").Trim();
        var role = Roles.Normalize(J.Str(m, "role"));
        if (!guest)
        {
            var existing = hub.Auth.ListPersonal().FirstOrDefault(p => Names.Same(p.Name, name));
            var target = existing.Name != null ? existing.Role : role;
            bool isOwner = Roles.Level(me.Role) >= Roles.Level("owner");
            if (!isOwner && Roles.Level(target) >= Roles.Level(me.Role))
                throw new InvalidOperationException(existing.Name != null ? "You can only invite people below your rank" : "You can only invite ranks below your own");
        }
        int hours = J.Int(m, "hours", 0);
        if (hours <= 0 && J.Int(m, "days", 0) > 0) hours = J.Int(m, "days", 0) * 24;
        var (info, secret) = hub.Auth.CreateInvite(guest ? "guest" : "person", name, role, me.Name, hours > 0 ? hours : null, J.Int(m, "uses", 1));
        hub.Log.LogInformation("{By} created invite {Id} ({Kind}, {Role}) expiring {Expires:u}", me.Name, info.Id, info.Kind, info.Role, info.Expires);
        var o = InviteJson(info);
        o["t"] = LodgeMsg.AdminInviteCreated;
        o["secret"] = secret;
        await hub.Send(me, o);
        await SendInvites(me);
    }

    async Task RevokeInvite(Member me, JsonObject m)
    {
        var info = hub.Auth.RevokeInvite(J.Str(m, "id"));
        hub.Log.LogInformation("{By} revoked invite {Id}", me.Name, info.Id);
        await SendInvites(me);
    }

    JsonObject DeviceJson(DeviceInfo d, Member viewer) => new()
    {
        ["id"] = d.Id, ["name"] = d.Guest ? "" : d.Name, ["role"] = d.Role, ["device"] = d.DeviceName,
        ["created"] = Ms(d.Created), ["lastSeen"] = Ms(d.LastSeen), ["ip"] = d.LastIp ?? "",
        ["current"] = viewer.DeviceId == d.Id, ["online"] = hub.Members.Any(x => x.DeviceId == d.Id),
    };

    Task SendDevices(Member me, string type, string name) => hub.Send(me, new JsonObject
    {
        ["t"] = type, ["name"] = name ?? "", ["now"] = Ms(hub.Clock.UtcNow),
        ["devices"] = new JsonArray(hub.Auth.ListDevices(string.IsNullOrWhiteSpace(name) ? null : name).Select(d => (JsonNode)DeviceJson(d, me)).ToArray()),
    });

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
                    else await hub.Send(me, new JsonObject { ["t"] = LodgeMsg.Devices, ["name"] = "", ["now"] = Ms(hub.Clock.UtcNow), ["devices"] = new JsonArray(
                        hub.Auth.ListDevices().Where(d => d.Id == me.DeviceId).Select(d => (JsonNode)DeviceJson(d, me)).ToArray()) });
                    break;
                case LodgeMsg.DeviceRevoke:
                {
                    var d = hub.Auth.FindDevice(J.Str(m, "id")) ?? throw new InvalidOperationException("That device is already signed out");
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
        var name = J.Str(m, "name");
        hub.Auth.RetireCode(name, J.Bool(m, "force"));
        hub.Log.LogInformation("{By} retired the permanent code of {Name}", me.Name, Names.ForLog(name));
        await hub.ApplyCodeChanges();
        await SendMembers(me);
    }

    async Task SetRole(Member me, JsonObject m)
    {
        var name = J.Str(m, "name");
        var role = Roles.Normalize(J.Str(m, "role"));
        hub.Auth.SetRole(name, role);   // refuses to demote the last Guild Master
        hub.Log.LogInformation("{By} made {Name} {Role}", me.Name, name, role);
        await hub.ApplyCodeChanges();
        await SendMembers(me);
    }

    async Task Remove(Member me, JsonObject m)
    {
        var name = J.Str(m, "name");
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
        var name = (J.Str(m, "name") ?? "").Trim();
        if (name.Length == 0) throw new InvalidOperationException("Pick a member");
        bool atlasOnly = !(m["atlasOnly"] is JsonValue v && v.TryGetValue<bool>(out var b) && !b);
        if (!atlasOnly && hub.Auth.ListPersonal().Any(p => Names.Same(p.Name, name)))
            throw new InvalidOperationException("They are still a member - revert their Atlas contributions, or remove them first");
        var r = await hub.PurgeMember(name, atlasOnly);
        hub.Log.LogInformation("{By} {What} {Name}: {Count} item(s)", me.Name, atlasOnly ? "reverted the Atlas contributions of" : "purged the stored data of", Names.ForLog(name), r.Total);
        var o = r.ToJson();
        o["t"] = LodgeMsg.AdminPurged;
        await hub.Send(me, o);
    }

    // owner: the lodge's display name and welcome text (validated and length-capped by LodgeInfoStore)
    async Task SetLodgeInfo(Member me, JsonObject m)
    {
        hub.Info.Set(J.Str(m, "name"), J.Str(m, "welcome"));
        hub.Log.LogInformation("{By} changed the lodge name / welcome text", me.Name);
        await hub.Broadcast(new JsonObject { ["t"] = LodgeMsg.LodgeInfo, ["lodge"] = hub.Info.ToJson() });
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
                if (me.DeviceId != null) { hub.Auth.RevokeDevice(me.DeviceId); await hub.Send(me, new JsonObject { ["t"] = LodgeMsg.MemberLeft }); await hub.DisconnectDevice(me.DeviceId); }
                else await hub.Error(me, "Nothing to leave");
                return true;
            }
            var devices = hub.Auth.Leave(me.Name);
            var gone = await hub.PurgeMember(me.Name);      // everything they authored goes (see LodgeHub.PurgeMember)
            hub.Log.LogInformation("{Name} left the lodge ({Count} devices signed out, {Items} stored item(s) forgotten)", Names.ForLog(me.Name), devices.Count, gone.Total);
            await hub.Send(me, new JsonObject { ["t"] = LodgeMsg.MemberLeft });
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
        var name = (J.Str(m, "name") ?? "").Trim();
        if (name.Length is < 1 or > 24) throw new InvalidOperationException("Channel names are 1-24 characters");
        var ch = hub.Channels.Add(name, J.Str(m, "type"), J.Str(m, "minRole"), J.Int(m, "max"));
        hub.Log.LogInformation("{By} added channel {Id}", me.Name, ch.Id);
        await hub.Resync();
        await SendMembers(me);
    }

    async Task RemoveChannel(Member me, JsonObject m)
    {
        var id = J.Str(m, "id");
        if (!hub.Channels.Remove(id)) throw new InvalidOperationException("That channel can't be removed (the last text channel stays)");
        hub.Chat.DropChannel(id);
        foreach (var x in hub.Members.Where(x => x.Room == id)) x.Room = null;
        hub.Log.LogInformation("{By} removed channel {Id}", me.Name, id);
        await hub.Resync();
        await SendMembers(me);
    }

    // officers can only act on lower ranks
    Member Target(Member me, JsonObject m)
    {
        var t = hub.Find(J.Int(m, "id")) ?? throw new InvalidOperationException("They're not online");
        if (!hub.CanSee(me, t)) throw new InvalidOperationException("They're not online"); // an officer can't act on someone invisible
        if (Roles.Level(t.Role) >= Roles.Level(me.Role)) throw new InvalidOperationException("You can only do that to lower ranks");
        return t;
    }

    async Task Kick(Member me, JsonObject m)
    {
        var t = Target(me, m);
        hub.Log.LogInformation("{By} kicked {Name}", me.Name, t.Name);
        await hub.Disconnect(t, $"Kicked by {me.Name}");
    }

    async Task ServerMute(Member me, JsonObject m)
    {
        var t = Target(me, m);
        bool on = J.Bool(m, "on");
        hub.SetServerMuted(t, on); // by identity: a reconnect keeps it
        hub.Log.LogInformation("{By} {Action} {Name}", me.Name, on ? "server-muted" : "unmuted", t.Name);
        await hub.BroadcastUser(t);
    }
}
