using System.Text.Json.Nodes;

namespace Lodge;

// Guild Master panel (members, ranks, invites, channels) and officer tools (kick, server-mute).
// Every request is checked against the sender's rank here - the hub only hides buttons.
public class AdminModule : IModule
{
    readonly LodgeHub hub;
    public AdminModule(LodgeHub hub) { this.hub = hub; }

    public async Task<bool> Handle(Member me, string type, JsonObject m)
    {
        if (!type.StartsWith("admin.") && !type.StartsWith("mod.")) return false;
        var perm = type.StartsWith("mod.") ? Perm.Moderate : type.StartsWith("admin.channel") ? Perm.ManageChannels : Perm.ManageMembers;
        if (!Roles.Can(me.Role, perm)) { await hub.Error(me, "Your rank can't do that"); return true; }
        try
        {
            switch (type)
            {
                case "admin.members": await SendMembers(me); break;
                case "admin.invite": await Invite(me, m); break;
                case "admin.role": await SetRole(me, m); break;
                case "admin.remove": await Remove(me, m); break;
                case "admin.channel.add": await AddChannel(me, m); break;
                case "admin.channel.remove": await RemoveChannel(me, m); break;
                case "mod.kick": await Kick(me, m); break;
                case "mod.mute": await ServerMute(me, m); break;
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
        ["t"] = "admin.members",
        ["members"] = new JsonArray(hub.Auth.ListPersonal().Select(p => (JsonNode)new JsonObject
        {
            ["name"] = p.Name, ["role"] = p.Role,
            ["online"] = hub.Members.Any(x => x.Personal && LodgeHub.Same(x.Name, p.Name) && hub.CanSee(me, x)),
        }).ToArray()),
        ["channels"] = new JsonArray(hub.Channels.All.Select(c => (JsonNode)c.ToJson()).ToArray()),
    });

    async Task Invite(Member me, JsonObject m)
    {
        var name = ((string)m["name"] ?? "").Trim();
        var role = Roles.Normalize((string)m["role"]);
        var code = hub.Auth.Add(name, role); // throws with a readable message when the name is bad or taken
        hub.Log.LogInformation("{By} invited {Name} as {Role}", me.Name, name, role);
        await hub.Send(me, new JsonObject { ["t"] = "admin.invited", ["name"] = name, ["role"] = role, ["code"] = code });
        await SendMembers(me);
    }

    async Task SetRole(Member me, JsonObject m)
    {
        var name = (string)m["name"];
        var role = Roles.Normalize((string)m["role"]);
        if (role != "owner" && hub.Auth.ListPersonal().Count(p => p.Role == "owner" && !LodgeHub.Same(p.Name, name)) == 0)
            throw new InvalidOperationException("The lodge needs at least one Guild Master");
        hub.Auth.SetRole(name, role);
        hub.Log.LogInformation("{By} made {Name} {Role}", me.Name, name, role);
        await hub.ApplyCodeChanges();
        await SendMembers(me);
    }

    async Task Remove(Member me, JsonObject m)
    {
        var name = (string)m["name"];
        if (LodgeHub.Same(name, me.Name)) throw new InvalidOperationException("You can't remove yourself");
        hub.Auth.Remove(name);
        hub.Log.LogInformation("{By} removed {Name}", me.Name, name);
        await hub.ApplyCodeChanges();
        await SendMembers(me);
    }

    async Task AddChannel(Member me, JsonObject m)
    {
        var name = ((string)m["name"] ?? "").Trim();
        if (name.Length is < 1 or > 24) throw new InvalidOperationException("Channel names are 1-24 characters");
        var ch = hub.Channels.Add(name, (string)m["type"], (string)m["minRole"], m["max"]?.GetValue<int>() ?? 0);
        hub.Log.LogInformation("{By} added channel {Id}", me.Name, ch.Id);
        await hub.Resync();
        await SendMembers(me);
    }

    async Task RemoveChannel(Member me, JsonObject m)
    {
        var id = (string)m["id"];
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
        var t = hub.Find(m["id"]?.GetValue<int>() ?? 0) ?? throw new InvalidOperationException("They're not online");
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
        t.ServerMuted = m["on"]?.GetValue<bool>() == true;
        hub.Log.LogInformation("{By} {Action} {Name}", me.Name, t.ServerMuted ? "server-muted" : "unmuted", t.Name);
        await hub.BroadcastUser(t);
    }
}
