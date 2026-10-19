using System.Text.Json.Nodes;
using Elans.Shared;

namespace Elans.Shared.Tests;

// The typed profile messages: every message survives Write -> Read, and writes exactly the JSON the old hand-built dictionary /
// JsonObject sent (golden values copied from src/Lodge/LodgeSession.cs, src/Lodge/ProfileEdit.cs, server/Lodge.Server/Profiles.cs and
// Hub.cs at 3cee748) - the wire must not change.
public class ProfileMessagesTests
{
    static TestFields Written(Action<ILodgeWriter> write) => TestFields.Written(write);
    static void SameJson(object expected, object actual) => TestFields.SameJson(expected, actual);

    static ProfileView OwnView() => new ProfileView
    {
        Name = "Mia", Found = true, Guest = false, Role = "member", Online = true, Restricted = false, AvatarId = "av.owl", Accent = null, DisplayName = "",
        ShowChars = "all", VisibleTo = "everyone", About = "hi", PlayTimes = "nights", Main = "Alpha",
        Game = new Dictionary<string, object> { ["playing"] = true, ["name"] = "Alpha", ["zone"] = "Elwynn Forest", ["level"] = 20L },
        Chars =
        {
            new ProfileChar { Name = "Alpha", Class = "Hunter", ClassFile = "HUNTER", Race = "Dwarf", RaceFile = "Dwarf", Level = 20, Main = true, Seen = 1_790_000_000, Hidden = false },
            new ProfileChar { Name = "Beta", Class = "Mage", ClassFile = "MAGE", Race = "Gnome", RaceFile = "Gnome", Level = 5, Main = false, Seen = 1_780_000_000, Hidden = true },
        },
    };

    // ---- round trips

    [Fact]
    public void Client_messages_round_trip()
    {
        var set = ProfileSetMsg.Read(Written(new ProfileSetMsg { AvatarId = "av.owl", Accent = null, About = "x", Hidden = new List<string> { "Beta" } }.Write));
        Assert.Equal((true, "av.owl", true, (string)null, true, "x"), (set.AvatarId.Sent, set.AvatarId.Value, set.Accent.Sent, set.Accent.Value, set.About.Sent, set.About.Value));
        Assert.False(set.PlayTimes.Sent || set.DisplayName.Sent || set.Main.Sent || set.ShowChars.Sent || set.VisibleTo.Sent);
        Assert.Equal(new[] { "Beta" }, set.Hidden);
        Assert.Null(ProfileSetMsg.Read(Written(new ProfileSetMsg { About = "x" }.Write)).Hidden);

        Assert.Equal("Mia", ProfileGetMsg.Read(Written(new ProfileGetMsg { Name = "Mia" }.Write)).Name);
        var r = ProfileResetMsg.Read(Written(new ProfileResetMsg { Name = "Mia", ClearText = true }.Write));
        Assert.Equal(("Mia", true), (r.Name, r.ClearText));
    }

    [Fact]
    public void Server_messages_round_trip()
    {
        var p = ProfileMsg.Read(Written(new ProfileMsg { Id = 7, Name = "Mia", AvatarId = "av.owl", Accent = "teal", DisplayName = "Mi" }.Write));
        Assert.Equal((7, "Mia", "av.owl", "teal", "Mi", true), (p.Id, p.Name, p.AvatarId, p.Accent, p.DisplayName, p.DisplayNameSent));

        var v = ProfileDataMsg.Read(Written(new ProfileDataMsg { Profile = OwnView() }.Write)).Profile;
        Assert.Equal(("Mia", true, false, "member", true, false), (v.Name, v.Found, v.Guest, v.Role, v.Online, v.Restricted));
        Assert.Equal(("av.owl", (string)null, "", true, "all", "everyone"), (v.AvatarId, v.Accent, v.DisplayName, v.DisplayNameSent, v.ShowChars, v.VisibleTo));
        Assert.Equal(("hi", "nights", "Alpha"), (v.About, v.PlayTimes, v.Main));
        Assert.Equal("Elwynn Forest", ((Dictionary<string, object>)v.Game)["zone"]);
        Assert.Equal(new[] { ("Alpha", 20, true, (long?)1_790_000_000, (bool?)false), ("Beta", 5, false, (long?)1_780_000_000, (bool?)true) },
            v.Chars.Select(c => (c.Name, c.Level, c.Main, c.Seen, c.Hidden)));

        var nf = ProfileDataMsg.Read(Written(new ProfileDataMsg { Profile = new ProfileView { Name = "Nobody" } }.Write)).Profile;
        Assert.Equal(("Nobody", false, false, (string)null), (nf.Name, nf.Found, nf.DisplayNameSent, nf.Role));
    }

    [Fact]
    public void Each_message_writes_its_own_type()
    {
        Assert.Equal("profile:set", Written(new ProfileSetMsg().Write).Str("t"));
        Assert.Equal("profile:get", Written(new ProfileGetMsg().Write).Str("t"));
        Assert.Equal("profile:reset", Written(new ProfileResetMsg().Write).Str("t"));
        Assert.Equal("profile:data", Written(new ProfileDataMsg().Write).Str("t"));
        Assert.Equal("profile", Written(new ProfileMsg().Write).Str("t"));
    }

    [Fact]
    public void Reading_is_lenient_like_before()
    {
        // no displayName key: not sent (a pre-2.7 server); seen / hidden left out stay null; a non-object char or game is skipped
        var v = ProfileView.Read(new TestFields(new Dictionary<string, object>
        {
            ["name"] = "Mia", ["found"] = true, ["game"] = "x",
            ["chars"] = new List<object> { 5, new Dictionary<string, object> { ["name"] = "Alpha", ["level"] = "20" } },
        }));
        Assert.False(v.DisplayNameSent);
        Assert.Null(v.DisplayName);
        Assert.Null(v.Game);
        var c = v.Chars.Single();
        Assert.Equal(("Alpha", 0, (long?)null, (bool?)null), (c.Name, c.Level, c.Seen, c.Hidden));
        Assert.False(ProfileMsg.Read(new TestFields(new Dictionary<string, object> { ["id"] = 3 })).DisplayNameSent);
        Assert.Null(ProfileDataMsg.Read(new TestFields()).Profile);
        // profile:set: a hidden list keeps only its strings (the server's own check refuses the whole message instead)
        Assert.Equal(new[] { "A" }, ProfileSetMsg.Read(new TestFields(new Dictionary<string, object> { ["hidden"] = new List<object> { "A", 5 } })).Hidden);
    }

    // ---- golden: what the old code sent

    [Fact]
    public void Golden_client_messages_match_the_old_Outpost_dictionaries()
    {
        // ProfileEditVM.ToMessage + SaveProfile: null avatar / accent go out as null (= clear); displayName, main and hidden only when given
        SameJson(new Dictionary<string, object>
        {
            ["t"] = "profile:set", ["avatarId"] = "av.owl", ["accent"] = null, ["about"] = "hi", ["playTimes"] = "", ["showChars"] = "main", ["visibleTo"] = "officers",
            ["displayName"] = "Hunter Elan", ["main"] = null, ["hidden"] = new List<string> { "Elanpriest" },
        }, Written(new ProfileSetMsg
        {
            AvatarId = "av.owl", Accent = null, About = "hi", PlayTimes = "", ShowChars = "main", VisibleTo = "officers", DisplayName = "Hunter Elan",
            Main = null, Hidden = new List<string> { "Elanpriest" },
        }.Write).D);
        // a guest without characters: no displayName, main or hidden at all
        SameJson(new Dictionary<string, object> { ["t"] = "profile:set", ["avatarId"] = "av.bow", ["accent"] = "teal", ["about"] = "selftest about", ["playTimes"] = "evenings" },
            Written(new ProfileSetMsg { AvatarId = "av.bow", Accent = "teal", About = "selftest about", PlayTimes = "evenings" }.Write).D);

        SameJson(new Dictionary<string, object> { ["t"] = "profile:get", ["name"] = "Mia" }, Written(new ProfileGetMsg { Name = "Mia" }.Write).D);
        SameJson(new Dictionary<string, object> { ["t"] = "profile:reset", ["name"] = "Mia", ["clearText"] = false }, Written(new ProfileResetMsg { Name = "Mia" }.Write).D);
    }

    [Fact]
    public void Golden_server_messages_match_the_old_JsonObjects()
    {
        // Hub.BroadcastProfile
        SameJson(new JsonObject { ["t"] = "profile", ["id"] = 7, ["name"] = "Mia", ["avatarId"] = null, ["accent"] = "teal", ["displayName"] = "" },
            Written(new ProfileMsg { Id = 7, Name = "Mia", AvatarId = null, Accent = "teal", DisplayName = "" }.Write).D);

        // ProfileModule.Build: your own view (showChars / visibleTo, hidden on each character), with the game as the hub sent it
        var own = new JsonObject
        {
            ["name"] = "Mia", ["found"] = true, ["guest"] = false, ["role"] = "member", ["online"] = true, ["restricted"] = false,
            ["avatarId"] = "av.owl", ["accent"] = null, ["displayName"] = "", ["showChars"] = "all", ["visibleTo"] = "everyone",
            ["about"] = "hi", ["playTimes"] = "nights",
            ["game"] = new JsonObject { ["playing"] = true, ["name"] = "Alpha", ["zone"] = "Elwynn Forest", ["level"] = 20L },
            ["chars"] = new JsonArray(
                new JsonObject { ["name"] = "Alpha", ["class"] = "Hunter", ["classFile"] = "HUNTER", ["race"] = "Dwarf", ["raceFile"] = "Dwarf", ["level"] = 20, ["main"] = true, ["seen"] = 1_790_000_000L, ["hidden"] = false },
                new JsonObject { ["name"] = "Beta", ["class"] = "Mage", ["classFile"] = "MAGE", ["race"] = "Gnome", ["raceFile"] = "Gnome", ["level"] = 5, ["main"] = false, ["seen"] = 1_780_000_000L, ["hidden"] = true }),
            ["main"] = "Alpha",
        };
        SameJson(new JsonObject { ["t"] = "profile:data", ["profile"] = own }, Written(new ProfileDataMsg { Profile = OwnView() }.Write).D);

        // someone else's, invisible (no seen), no game, main not shown
        var other = new JsonObject
        {
            ["name"] = "Mia", ["found"] = true, ["guest"] = false, ["role"] = "member", ["online"] = false, ["restricted"] = false,
            ["avatarId"] = null, ["accent"] = null, ["displayName"] = "Mi", ["about"] = "", ["playTimes"] = "",
            ["chars"] = new JsonArray(new JsonObject { ["name"] = "Alpha", ["class"] = "", ["classFile"] = "", ["race"] = "", ["raceFile"] = "", ["level"] = 0, ["main"] = false }),
        };
        SameJson(other, Written(new ProfileView
        {
            Name = "Mia", Found = true, Role = "member", DisplayName = "Mi", About = "", PlayTimes = "",
            Chars = { new ProfileChar { Name = "Alpha", Class = "", ClassFile = "", Race = "", RaceFile = "" } },
        }.Write).D);

        // "visible to officers" for a member: avatar and accent only
        SameJson(new JsonObject
        {
            ["name"] = "Mia", ["found"] = true, ["guest"] = false, ["role"] = "member", ["online"] = true, ["restricted"] = true,
            ["avatarId"] = "av.owl", ["accent"] = null, ["displayName"] = "",
        }, Written(new ProfileView { Name = "Mia", Found = true, Role = "member", Online = true, Restricted = true, AvatarId = "av.owl", DisplayName = "", About = "x" }.Write).D);

        // not found
        SameJson(new JsonObject { ["name"] = "", ["found"] = false }, Written(new ProfileView { Name = "" }.Write).D);
    }
}
