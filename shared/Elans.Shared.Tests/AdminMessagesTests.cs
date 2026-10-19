using System.Text.Json.Nodes;
using Elans.Shared;

namespace Elans.Shared.Tests;

// The typed Guild Master / officer messages: every message survives Write -> Read, and writes exactly the JSON the old hand-built dictionary /
// JsonObject sent (golden values copied from src/Lodge/LodgeSession.cs, LodgeSession.Invites.cs, LodgeSession.Leave.cs and
// server/Lodge.Server/Admin.cs, Channels.cs, Hub.Purge.cs, LodgeInfo.cs at c712015) - the wire must not change.
public class AdminMessagesTests
{
    static TestFields Written(Action<ILodgeWriter> write) => TestFields.Written(write);
    static void SameJson(object expected, object actual) => TestFields.SameJson(expected, actual);

    static InviteView Invite(string id = "inv1", bool guest = false) => new InviteView
    {
        Id = id, Kind = guest ? "guest" : "person", Name = guest ? "" : "Nova", Role = "member", By = "Bob",
        Created = 1_790_000_000_000, Expires = 1_790_604_800_000, Max = guest ? 5 : 1, Uses = guest ? 2 : 0,
        Used = guest, Expired = false, Revoked = false, Pending = true,
    };

    static JsonObject OldInvite(string id = "inv1", bool guest = false) => new JsonObject
    {
        ["id"] = id, ["kind"] = guest ? "guest" : "person", ["name"] = guest ? "" : "Nova", ["role"] = "member", ["by"] = "Bob",
        ["created"] = 1_790_000_000_000L, ["expires"] = 1_790_604_800_000L, ["max"] = guest ? 5 : 1, ["uses"] = guest ? 2 : 0,
        ["used"] = guest, ["expired"] = false, ["revoked"] = false, ["pending"] = true,
    };

    // ---- round trips

    [Fact]
    public void Client_messages_round_trip()
    {
        Assert.NotNull(AdminMembersGetMsg.Read(Written(new AdminMembersGetMsg().Write)));
        Assert.NotNull(AdminInvitesGetMsg.Read(Written(new AdminInvitesGetMsg().Write)));
        var inv = AdminInviteMsg.Read(Written(new AdminInviteMsg { Name = "Nova", Role = "member" }.Write));
        Assert.Equal(("Nova", "member"), (inv.Name, inv.Role));
        var role = AdminRoleMsg.Read(Written(new AdminRoleMsg { Name = "Bob", Role = "owner" }.Write));
        Assert.Equal(("Bob", "owner"), (role.Name, role.Role));
        Assert.Equal("Dax", AdminRemoveMsg.Read(Written(new AdminRemoveMsg { Name = "Dax" }.Write)).Name);
        var p = AdminPurgeMsg.Read(Written(new AdminPurgeMsg { Name = "Dax", AtlasOnly = false }.Write));
        Assert.Equal(("Dax", false), (p.Name, p.AtlasOnly));
        Assert.True(AdminPurgeMsg.Read(Written(new AdminPurgeMsg { Name = "Dax" }.Write)).AtlasOnly);
        var ch = AdminChannelAddMsg.Read(Written(new AdminChannelAddMsg { Name = "Raid", ChannelType = "voice", MinRole = "member", Max = 10 }.Write));
        Assert.Equal(("Raid", "voice", "member", 10), (ch.Name, ch.ChannelType, ch.MinRole, ch.Max));
        Assert.Equal("raid", AdminChannelRemoveMsg.Read(Written(new AdminChannelRemoveMsg { Id = "raid" }.Write)).Id);
        var ic = AdminInviteCreateMsg.Read(Written(new AdminInviteCreateMsg { Kind = "guest", Name = "", Role = "guest", Hours = 24, Uses = 5 }.Write));
        Assert.Equal(("guest", "", "guest", 24, 5, 0), (ic.Kind, ic.Name, ic.Role, ic.Hours, ic.Uses, ic.Days));
        Assert.Equal(3, AdminInviteCreateMsg.Read(Written(new AdminInviteCreateMsg { Kind = "person", Days = 3 }.Write)).Days);
        Assert.Equal("inv1", AdminInviteRevokeMsg.Read(Written(new AdminInviteRevokeMsg { Id = "inv1" }.Write)).Id);
        var rt = AdminRetireMsg.Read(Written(new AdminRetireMsg { Name = "Bob", Force = true }.Write));
        Assert.Equal(("Bob", true), (rt.Name, rt.Force));
        var ls = AdminLodgeSetMsg.Read(Written(new AdminLodgeSetMsg { Name = "The Silver Lodge", Welcome = "Hi\nall" }.Write));
        Assert.Equal(("The Silver Lodge", "Hi\nall"), (ls.Name, ls.Welcome));
        Assert.Equal(7, ModKickMsg.Read(Written(new ModKickMsg { Id = 7 }.Write)).Id);
        var mu = ModMuteMsg.Read(Written(new ModMuteMsg { Id = 7, On = true }.Write));
        Assert.Equal((7, true), (mu.Id, mu.On));
    }

    [Fact]
    public void Server_messages_round_trip()
    {
        var am = AdminMembersMsg.Read(Written(new AdminMembersMsg
        {
            Members = { new AdminMember { Name = "Bob", Role = "officer", HasCode = false, Devices = 2, Online = true }, new AdminMember { Name = "Dax", Role = "member" } },
            Channels = { new LodgeChannelView { Id = "general", Name = "general", ChannelType = "text", MinRole = "guest" }, new LodgeChannelView { Id = "camp", Name = "Campfire", ChannelType = "voice", MinRole = "member", Max = 8 } },
        }.Write));
        Assert.Equal(new[] { ("Bob", "officer", (bool?)false, (int?)2, true), ("Dax", "member", (bool?)null, (int?)null, false) },
            am.Members.Select(m => (m.Name, m.Role, m.HasCode, m.Devices, m.Online)));
        Assert.Equal(new[] { ("general", "general", "text", "guest", 0), ("camp", "Campfire", "voice", "member", 8) },
            am.Channels.Select(c => (c.Id, c.Name, c.ChannelType, c.MinRole, c.Max)));

        var ai = AdminInvitedMsg.Read(Written(new AdminInvitedMsg { Name = "Nova", Role = "member", Code = "synthetic-code" }.Write));
        Assert.Equal(("Nova", "member", "synthetic-code"), (ai.Name, ai.Role, ai.Code));

        var made = AdminInviteCreatedMsg.Read(Written(new AdminInviteCreatedMsg { Invite = Invite(), Secret = "i1.synthetic" }.Write));
        Assert.Equal(("inv1", "person", "Nova", "member", "Bob", "i1.synthetic"), (made.Invite.Id, made.Invite.Kind, made.Invite.Name, made.Invite.Role, made.Invite.By, made.Secret));
        Assert.Equal((1_790_000_000_000L, 1_790_604_800_000L, 1, 0, false, false, false, true),
            (made.Invite.Created, made.Invite.Expires, made.Invite.Max, made.Invite.Uses, made.Invite.Used, made.Invite.Expired, made.Invite.Revoked, made.Invite.Pending));

        var list = AdminInvitesMsg.Read(Written(new AdminInvitesMsg { Now = 1_790_000_100_000, Invites = { Invite(), Invite("inv2", guest: true) } }.Write));
        Assert.Equal(1_790_000_100_000L, list.Now);
        Assert.Equal(new[] { ("inv1", "person", 1, 0), ("inv2", "guest", 5, 2) }, list.Invites.Select(i => (i.Id, i.Kind, i.Max, i.Uses)));

        var pg = AdminPurgedMsg.Read(Written(new AdminPurgedMsg { Name = "Dax", AtlasOnly = true, Atlas = 12, Flights = 1, Signups = 2, Organised = 3, Profile = 1, Armory = 4, Badges = 1, Messages = 5, Reactions = 6, Uploads = 7, Total = 43 }.Write));
        Assert.Equal(("Dax", true, 12, 1, 2, 3, 1, 4, 1, 5, 6, 7, 43),
            (pg.Name, pg.AtlasOnly, pg.Atlas, pg.Flights, pg.Signups, pg.Organised, pg.Profile, pg.Armory, pg.Badges, pg.Messages, pg.Reactions, pg.Uploads, pg.Total));

        var li = LodgeInfoMsg.Read(Written(new LodgeInfoMsg { Lodge = new LodgeInfoView { Name = "The Silver Lodge", Welcome = "Hi" } }.Write)).Lodge;
        Assert.Equal(("The Silver Lodge", "Hi"), (li.Name, li.Welcome));
    }

    [Fact]
    public void Each_message_writes_its_own_type()
    {
        Assert.Equal("admin.members", Written(new AdminMembersGetMsg().Write).Str("t"));
        Assert.Equal("admin.members", Written(new AdminMembersMsg().Write).Str("t"));
        Assert.Equal("admin.invite", Written(new AdminInviteMsg().Write).Str("t"));
        Assert.Equal("admin.invited", Written(new AdminInvitedMsg().Write).Str("t"));
        Assert.Equal("admin.role", Written(new AdminRoleMsg().Write).Str("t"));
        Assert.Equal("admin.remove", Written(new AdminRemoveMsg().Write).Str("t"));
        Assert.Equal("admin.purge", Written(new AdminPurgeMsg().Write).Str("t"));
        Assert.Equal("admin.purged", Written(new AdminPurgedMsg().Write).Str("t"));
        Assert.Equal("admin.channel.add", Written(new AdminChannelAddMsg().Write).Str("t"));
        Assert.Equal("admin.channel.remove", Written(new AdminChannelRemoveMsg().Write).Str("t"));
        Assert.Equal("admin.invite.create", Written(new AdminInviteCreateMsg().Write).Str("t"));
        Assert.Equal("admin.invite.created", Written(new AdminInviteCreatedMsg().Write).Str("t"));
        Assert.Equal("admin.invites", Written(new AdminInvitesGetMsg().Write).Str("t"));
        Assert.Equal("admin.invites", Written(new AdminInvitesMsg().Write).Str("t"));
        Assert.Equal("admin.invite.revoke", Written(new AdminInviteRevokeMsg().Write).Str("t"));
        Assert.Equal("admin.retire", Written(new AdminRetireMsg().Write).Str("t"));
        Assert.Equal("admin.lodge.set", Written(new AdminLodgeSetMsg().Write).Str("t"));
        Assert.Equal("lodge.info", Written(new LodgeInfoMsg().Write).Str("t"));
        Assert.Equal("mod.kick", Written(new ModKickMsg().Write).Str("t"));
        Assert.Equal("mod.mute", Written(new ModMuteMsg().Write).Str("t"));
    }

    [Fact]
    public void Reading_is_lenient_like_before()
    {
        // admin.members from before 2.10: no hasCode / devices (the Outpost shows "has a code" and no device count); a non-object row is skipped
        var am = AdminMembersMsg.Read(new TestFields(new Dictionary<string, object>
        {
            ["members"] = new List<object> { "x", new Dictionary<string, object> { ["name"] = "Bob", ["role"] = "officer", ["online"] = "yes" } },
        }));
        var b = am.Members.Single();
        Assert.Equal(("Bob", (bool?)null, (int?)null, false), (b.Name, b.HasCode, b.Devices, b.Online));
        Assert.Empty(am.Channels);
        // invite create: uses missing or not a number is 1 (the server's old J.Int(m, "uses", 1)); hours / days missing are 0
        Assert.Equal(1, AdminInviteCreateMsg.Read(new TestFields()).Uses);
        Assert.Equal(1, AdminInviteCreateMsg.Read(new TestFields(new Dictionary<string, object> { ["uses"] = "3" })).Uses);
        Assert.Equal(0, AdminInviteCreateMsg.Read(new TestFields(new Dictionary<string, object> { ["uses"] = 0L })).Uses);
        Assert.Equal((0, 0), (AdminInviteCreateMsg.Read(new TestFields()).Hours, AdminInviteCreateMsg.Read(new TestFields()).Days));
        // lodge.info without a lodge block: none (the Outpost clears the name and the welcome text, as before)
        Assert.Null(LodgeInfoMsg.Read(new TestFields()).Lodge);
        // admin.purge without atlasOnly: Atlas only (the server reads it raw; a JSON false is the only way to switch it off)
        Assert.True(AdminPurgeMsg.Read(new TestFields(new Dictionary<string, object> { ["name"] = "Dax" })).AtlasOnly);
        // mod.kick with a string id: 0 (nobody)
        Assert.Equal(0, ModKickMsg.Read(new TestFields(new Dictionary<string, object> { ["id"] = "7" })).Id);
    }

    // ---- golden: what the old code sent

    [Fact]
    public void Golden_client_messages_match_the_old_Outpost_dictionaries()
    {
        SameJson(new Dictionary<string, object> { ["t"] = "admin.members" }, Written(new AdminMembersGetMsg().Write).D);
        SameJson(new Dictionary<string, object> { ["t"] = "admin.invite", ["name"] = "Nova", ["role"] = "member" }, Written(new AdminInviteMsg { Name = "Nova", Role = "member" }.Write).D);
        SameJson(new Dictionary<string, object> { ["t"] = "admin.role", ["name"] = "Bob", ["role"] = "owner" }, Written(new AdminRoleMsg { Name = "Bob", Role = "owner" }.Write).D);
        SameJson(new Dictionary<string, object> { ["t"] = "admin.remove", ["name"] = "Dax" }, Written(new AdminRemoveMsg { Name = "Dax" }.Write).D);
        SameJson(new Dictionary<string, object> { ["t"] = "admin.purge", ["name"] = "Dax", ["atlasOnly"] = true }, Written(new AdminPurgeMsg { Name = "Dax", AtlasOnly = true }.Write).D);
        SameJson(new Dictionary<string, object> { ["t"] = "admin.channel.add", ["name"] = "Raid", ["type"] = "voice", ["minRole"] = "member", ["max"] = 10 },
            Written(new AdminChannelAddMsg { Name = "Raid", ChannelType = "voice", MinRole = "member", Max = 10 }.Write).D);
        SameJson(new Dictionary<string, object> { ["t"] = "admin.channel.remove", ["id"] = "raid" }, Written(new AdminChannelRemoveMsg { Id = "raid" }.Write).D);
        SameJson(new Dictionary<string, object> { ["t"] = "mod.kick", ["id"] = 7 }, Written(new ModKickMsg { Id = 7 }.Write).D);
        SameJson(new Dictionary<string, object> { ["t"] = "mod.mute", ["id"] = 7, ["on"] = false }, Written(new ModMuteMsg { Id = 7, On = false }.Write).D);
        // LodgeSession.Invites.cs
        SameJson(new Dictionary<string, object> { ["t"] = "admin.invites" }, Written(new AdminInvitesGetMsg().Write).D);
        SameJson(new Dictionary<string, object> { ["t"] = "admin.invite.create", ["kind"] = "guest", ["name"] = "", ["role"] = "member", ["hours"] = 0, ["uses"] = 5 },
            Written(new AdminInviteCreateMsg { Kind = "guest", Name = "", Role = "member", Hours = 0, Uses = 5 }.Write).D);
        SameJson(new Dictionary<string, object> { ["t"] = "admin.invite.create", ["kind"] = "person", ["name"] = "Nova", ["role"] = "officer", ["hours"] = 24, ["uses"] = 1 },
            Written(new AdminInviteCreateMsg { Kind = "person", Name = "Nova", Role = "officer", Hours = 24 }.Write).D);
        SameJson(new Dictionary<string, object> { ["t"] = "admin.invite.revoke", ["id"] = "inv1" }, Written(new AdminInviteRevokeMsg { Id = "inv1" }.Write).D);
        SameJson(new Dictionary<string, object> { ["t"] = "admin.retire", ["name"] = "Bob" }, Written(new AdminRetireMsg { Name = "Bob" }.Write).D);
        // LodgeSession.Leave.cs
        SameJson(new Dictionary<string, object> { ["t"] = "admin.lodge.set", ["name"] = "", ["welcome"] = "Hi" }, Written(new AdminLodgeSetMsg { Name = "", Welcome = "Hi" }.Write).D);
    }

    [Fact]
    public void Golden_server_messages_match_the_old_JsonObjects()
    {
        // AdminModule.SendMembers with Channel.ToJson
        SameJson(new JsonObject
        {
            ["t"] = "admin.members",
            ["members"] = new JsonArray(
                new JsonObject { ["name"] = "Elan", ["role"] = "owner", ["hasCode"] = true, ["devices"] = 0, ["online"] = true },
                new JsonObject { ["name"] = "Bob", ["role"] = "officer", ["hasCode"] = false, ["devices"] = 2, ["online"] = false }),
            ["channels"] = new JsonArray(
                new JsonObject { ["id"] = "general", ["name"] = "general", ["type"] = "text", ["minRole"] = "guest", ["max"] = 0 },
                new JsonObject { ["id"] = "camp", ["name"] = "Campfire", ["type"] = "voice", ["minRole"] = "member", ["max"] = 8 }),
        }, Written(new AdminMembersMsg
        {
            Members = { new AdminMember { Name = "Elan", Role = "owner", HasCode = true, Devices = 0, Online = true }, new AdminMember { Name = "Bob", Role = "officer", HasCode = false, Devices = 2 } },
            Channels = { new LodgeChannelView { Id = "general", Name = "general", ChannelType = "text", MinRole = "guest" }, new LodgeChannelView { Id = "camp", Name = "Campfire", ChannelType = "voice", MinRole = "member", Max = 8 } },
        }.Write).D);
        SameJson(new JsonObject { ["t"] = "admin.members", ["members"] = new JsonArray(), ["channels"] = new JsonArray() }, Written(new AdminMembersMsg().Write).D);

        // AdminModule.Invite
        SameJson(new JsonObject { ["t"] = "admin.invited", ["name"] = "Nova", ["role"] = "member", ["code"] = "synthetic-code" },
            Written(new AdminInvitedMsg { Name = "Nova", Role = "member", Code = "synthetic-code" }.Write).D);

        // AdminModule.CreateInvite: InviteJson + t + secret; SendInvites
        var created = OldInvite(); created["t"] = "admin.invite.created"; created["secret"] = "i1.synthetic";
        SameJson(created, Written(new AdminInviteCreatedMsg { Invite = Invite(), Secret = "i1.synthetic" }.Write).D);
        SameJson(new JsonObject { ["t"] = "admin.invites", ["now"] = 1_790_000_100_000L, ["invites"] = new JsonArray(OldInvite(), OldInvite("inv2", guest: true)) },
            Written(new AdminInvitesMsg { Now = 1_790_000_100_000, Invites = { Invite(), Invite("inv2", guest: true) } }.Write).D);

        // AdminModule.Purge: PurgeSummary.ToJson + t
        SameJson(new JsonObject
        {
            ["name"] = "Dax", ["atlasOnly"] = true, ["atlas"] = 12, ["flights"] = 0, ["signups"] = 0, ["organised"] = 0,
            ["profile"] = 0, ["armory"] = 0, ["badges"] = 0, ["messages"] = 0, ["reactions"] = 0, ["uploads"] = 0, ["total"] = 12, ["t"] = "admin.purged",
        }, Written(new AdminPurgedMsg { Name = "Dax", AtlasOnly = true, Atlas = 12, Total = 12 }.Write).D);

        // AdminModule.SetLodgeInfo with LodgeInfoStore.ToJson
        SameJson(new JsonObject { ["t"] = "lodge.info", ["lodge"] = new JsonObject { ["name"] = "The Silver Lodge", ["welcome"] = "" } },
            Written(new LodgeInfoMsg { Lodge = new LodgeInfoView { Name = "The Silver Lodge", Welcome = "" } }.Write).D);
    }

    // the two frames whose t is not first keep the server's old key order (t after the fields)
    [Fact]
    public void Invite_created_and_purged_keep_the_old_key_order()
    {
        var made = Written(new AdminInviteCreatedMsg { Invite = Invite(), Secret = "i1.synthetic" }.Write).D.Keys.ToArray();
        Assert.Equal(new[] { "id", "kind", "name", "role", "by", "created", "expires", "max", "uses", "used", "expired", "revoked", "pending", "t", "secret" }, made);
        var purged = Written(new AdminPurgedMsg().Write).D.Keys.ToArray();
        Assert.Equal("t", purged.Last());
        Assert.Equal("name", purged.First());
    }
}
