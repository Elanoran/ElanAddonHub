using System.Reflection;
using System.Text.Json.Nodes;
using Elans.Shared;
using Xunit;

namespace Lodge.Tests;

// 2.31.1: AdminModule answers only its own admin.* / mod.* types. admin.badges (BadgesModule) and admin.cairn (the Cairn module) used to
// reach the right module only because those came before AdminModule in the hub's module list - AdminModule claimed every "admin." type.
public class AdminRoutingTests
{
    static JsonObject Msg(string t) => new() { ["t"] = t };

    [Theory]
    [InlineData("admin.badges")]
    [InlineData("admin.cairn")]
    [InlineData("admin.nonsense")]
    [InlineData("mod.nonsense")]
    public async Task Admin_types_of_other_modules_and_unknown_ones_are_not_handled(string type)
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner");
        var bob = r.Join("Bob", "officer");
        var admin = new AdminModule(r.Hub);
        Assert.False(await admin.Handle(elan, type, Msg(type)));
        Assert.False(await admin.Handle(bob, type, Msg(type)));
        Assert.Empty(r.Sock("Elan").Sent);     // no "Your rank can't do that", no wrong answer
        Assert.Empty(r.Sock("Bob").Sent);
    }

    [Fact]
    public async Task Its_own_types_are_still_handled_and_rank_checked()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner");
        var mia = r.Join("Mia", "member");
        var admin = new AdminModule(r.Hub);
        Assert.True(await admin.Handle(elan, LodgeMsg.AdminMembers, Msg(LodgeMsg.AdminMembers)));
        Assert.Single(r.Sock("Elan").Of(LodgeMsg.AdminMembers));
        Assert.True(await admin.Handle(mia, LodgeMsg.AdminMembers, Msg(LodgeMsg.AdminMembers)));
        Assert.Equal("Your rank can't do that", (string)r.Sock("Mia").LastError()!["text"]!);
    }

    [Fact]
    public void Every_client_admin_type_belongs_to_exactly_one_place()
    {
        // server -> client only, and the two that belong to other modules
        var elsewhere = new HashSet<string> { LodgeMsg.AdminInvited, LodgeMsg.AdminInviteCreated, LodgeMsg.AdminPurged, LodgeMsg.AdminBadges, LodgeMsg.AdminCairn };
        var all = typeof(LodgeMsg).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!)
            .Where(v => v.StartsWith("admin.") || v.StartsWith("mod.")).ToList();
        Assert.NotEmpty(all);
        foreach (var v in all) Assert.True(AdminModule.Handles.Contains(v) ^ elsewhere.Contains(v), v + " is handled by AdminModule and listed elsewhere, or by neither");
    }

    [Fact]
    public async Task admin_badges_reaches_the_badges_module_whatever_the_order()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner");
        // AdminModule first (the order that used to answer wrongly), then the real owner of the type
        var j = new JsonObject { ["t"] = LodgeMsg.AdminBadges, ["on"] = false };
        foreach (var mod in new IModule[] { new AdminModule(r.Hub), r.Hub.Badges })
            if (await mod.Handle(elan, LodgeMsg.AdminBadges, j)) break;
        Assert.Null(r.Sock("Elan").LastError());
        Assert.NotEmpty(r.Sock("Elan").Of(LodgeMsg.BadgesState));
    }
}
