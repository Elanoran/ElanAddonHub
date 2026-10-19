using System.Text.Json.Nodes;
using Elans.Shared;

namespace Elans.Shared.Tests;

// The typed welcome: a full welcome survives Write -> Read, and writes exactly the JSON the old hand-built JsonObject sent (the golden
// literal follows server/Lodge.Server/Hub.cs Welcome at c712015, key order included) - the wire must not change. An older server's welcome
// (parts missing) and a 1.x server's (no channels, one history list) read the way the Outpost read them before.
public class WelcomeMessagesTests
{
    static TestFields Written(Action<ILodgeWriter> write) => TestFields.Written(write);
    static void SameJson(object expected, object actual) => TestFields.SameJson(expected, actual);
    static string[] KeysOf(JsonObject o) => o.Select(kv => kv.Key).ToArray();

    static readonly string[] WelcomeKeys =
    {
        "t", "you", "name", "role", "server", "maxFileMb", "canShareFiles", "canModerate", "canManage", "canPin", "features", "canManageFiles",
        "filesMaxMb", "canShareClassic", "badges", "lodge", "calendar", "canInvite", "canRetire", "canPurge", "device", "reactions", "pins",
        "channels", "users", "profile", "names", "history",
    };

    static readonly string[] Features = { "reply", "react", "react2", "pin", "profile", "displayname", "lfg", "history2", "search", "tokens", "invites", "devices", "calendar", "leave", "lodgeinfo", "flights", "flightroutes", "files", "armory", "armory2", "atlas", "liveposition", "badges", "realms", "classic" };
    static readonly string[] Reactions = { "thumbsup", "heart", "laugh", "wow", "sad", "fire" };

    // ---- the parts as the server stores / builds them (the welcome's history and pins are stored records, passed through)

    static Dictionary<string, object> StoredMsg(string id, long at, string text) => new()
    {
        ["t"] = "msg", ["channel"] = "general", ["id"] = id, ["at"] = at, ["from"] = "Elan", ["fromId"] = 1L, ["text"] = text,
    };
    static JsonObject OldMsg(string id, long at, string text) => new()
    {
        ["t"] = "msg", ["channel"] = "general", ["id"] = id, ["at"] = at, ["from"] = "Elan", ["fromId"] = 1, ["text"] = text,
    };
    static Dictionary<string, object> StoredPin() => new()
    {
        ["id"] = "m1", ["text"] = "Raid Thursday 20:00", ["by"] = "Elan", ["at"] = 1760900000000L, ["pinnedBy"] = "Nova", ["pinnedAt"] = 1760900100000L,
    };
    static JsonObject OldPin() => new()
    {
        ["id"] = "m1", ["text"] = "Raid Thursday 20:00", ["by"] = "Elan", ["at"] = 1760900000000, ["pinnedBy"] = "Nova", ["pinnedAt"] = 1760900100000,
    };

    static LodgeUser Owner() => new LodgeUser
    {
        Id = 1, Name = "Elan", Role = "owner", Room = null, AvatarId = "a3", Accent = "#c08040", DisplayName = "Elan the Hunter", Status = "online", Note = "",
        Game = new Dictionary<string, object> { ["playing"] = true, ["name"] = "Elan", ["zone"] = "Orgrimmar" },
    };
    static JsonObject OldOwner() => new()
    {
        ["id"] = 1, ["name"] = "Elan", ["guest"] = false, ["role"] = "owner", ["room"] = null, ["voice"] = false, ["muted"] = false, ["deaf"] = false,
        ["serverMuted"] = false, ["avatarId"] = "a3", ["accent"] = "#c08040", ["displayName"] = "Elan the Hunter", ["status"] = "online", ["note"] = "",
        ["game"] = new JsonObject { ["playing"] = true, ["name"] = "Elan", ["zone"] = "Orgrimmar" },
    };
    static LodgeUser Guest() => new LodgeUser { Id = 9, Name = "Wanderer", Guest = true, Role = "guest", AvatarId = null, Accent = null, DisplayName = "", Status = "online", Note = "" };
    static JsonObject OldGuest() => new()
    {
        ["id"] = 9, ["name"] = "Wanderer", ["guest"] = true, ["role"] = "guest", ["room"] = null, ["voice"] = false, ["muted"] = false, ["deaf"] = false,
        ["serverMuted"] = false, ["avatarId"] = null, ["accent"] = null, ["displayName"] = "", ["status"] = "online", ["note"] = "", ["game"] = null,
    };

    static CalEvent Event() => new CalEvent
    {
        Id = "e1", Title = "Molten Core", Start = 1761000000000, End = 1761010000000, Note = "bring fire resist", Status = "open", By = "Elan", Roles = true,
        Tank = 2, Healer = 8, Dps = 30, Signups = { new CalSignup { Name = "Nova", Status = "going", Role = "healer", Comment = "" } }, Channel = "general", Msg = "m7",
        Tz = "Europe/Copenhagen", Off = 120, Created = 1760800000000,
    };
    static JsonObject OldEvent() => new()
    {
        ["id"] = "e1", ["title"] = "Molten Core", ["start"] = 1761000000000, ["end"] = 1761010000000, ["note"] = "bring fire resist", ["status"] = "open", ["by"] = "Elan",
        ["roles"] = true, ["slots"] = new JsonObject { ["tank"] = 2, ["healer"] = 8, ["dps"] = 30 },
        ["signups"] = new JsonArray(new JsonObject { ["n"] = "Nova", ["s"] = "going", ["r"] = "healer", ["c"] = "" }),
        ["channel"] = "general", ["msg"] = "m7", ["tz"] = "Europe/Copenhagen", ["off"] = 120, ["created"] = 1760800000000,
    };

    static ProfileView OwnProfile() => new ProfileView
    {
        Name = "Elan", Found = true, Role = "owner", Online = true, AvatarId = "a3", Accent = "#c08040", DisplayName = "Elan the Hunter", ShowChars = "members",
        VisibleTo = "members", About = "Hunter main", PlayTimes = "evenings",
        Chars = { new ProfileChar { Name = "Elan", Class = "Hunter", ClassFile = "HUNTER", Race = "Orc", RaceFile = "Orc", Level = 60, Main = true, Seen = 1760900000, Hidden = false } },
        Main = "Elan",
    };
    static JsonObject OldOwnProfile() => new()
    {
        ["name"] = "Elan", ["found"] = true, ["guest"] = false, ["role"] = "owner", ["online"] = true, ["restricted"] = false, ["avatarId"] = "a3", ["accent"] = "#c08040",
        ["displayName"] = "Elan the Hunter", ["showChars"] = "members", ["visibleTo"] = "members", ["about"] = "Hunter main", ["playTimes"] = "evenings",
        ["chars"] = new JsonArray(new JsonObject { ["name"] = "Elan", ["class"] = "Hunter", ["classFile"] = "HUNTER", ["race"] = "Orc", ["raceFile"] = "Orc", ["level"] = 60, ["main"] = true, ["seen"] = 1760900000, ["hidden"] = false }),
        ["main"] = "Elan",
    };

    // the owner's welcome on a 2.31 Lodge: three channels (one officers-only, one voice room), two members online, a device token,
    // 2 messages and a pin in general, an event, the name directory
    static WelcomeMsg FullOwner() => new WelcomeMsg
    {
        You = 1, Name = "Elan", Role = "owner", Server = "2.31.1", MaxFileMb = 25, CanShareFiles = true, CanModerate = true, CanManage = true, CanPin = true,
        Features = Features.ToList(), CanManageFiles = true, FilesMaxMb = 50, CanShareClassic = true,
        Badges = new BadgesStateMsg { Enabled = true, Locked = false }, Lodge = new LodgeInfoView { Name = "The Silver Lodge", Welcome = "Be nice.\nRaids Thu." },
        Calendar = new CalWelcome { Now = 1760950000000, Events = { Event() } },
        CanInvite = true, CanRetire = true, CanPurge = true, Device = new WelcomeDevice { Id = "d-4f2a", Name = "Desktop" }, Reactions = Reactions.ToList(),
        Pins = new List<WelcomePins>
        {
            new WelcomePins { Channel = "general", Pins = new List<object> { StoredPin() } },
            new WelcomePins { Channel = "officers", Pins = new List<object>() },
        },
        Channels =
        {
            new LodgeChannelView { Id = "general", Name = "General", ChannelType = "text", MinRole = "guest", Max = 0 },
            new LodgeChannelView { Id = "officers", Name = "Officers", ChannelType = "text", MinRole = "officer", Max = 0 },
            new LodgeChannelView { Id = "v1", Name = "Raid voice", ChannelType = "voice", MinRole = "member", Max = 25 },
        },
        Users = { Owner(), Guest() },
        Profile = OwnProfile(),
        Names = new List<KeyValuePair<string, string>> { new("Elan", "Elan the Hunter"), new("Nova", "Nova Star") },
        History = new List<WelcomeHistory>
        {
            new WelcomeHistory { Channel = "general", Messages = new List<object> { StoredMsg("m1", 1760900000000, "Raid Thursday 20:00"), StoredMsg("m2", 1760900050000, "🗡️ in") } },
            new WelcomeHistory { Channel = "officers", Messages = new List<object>() },
        },
    };

    static JsonObject Channel(string id, string name, string type, string minRole, int max) => new() { ["id"] = id, ["name"] = name, ["type"] = type, ["minRole"] = minRole, ["max"] = max };

    // Hub.Welcome at c712015, with the same values
    static JsonObject OldFullOwner() => new()
    {
        ["t"] = "welcome", ["you"] = 1, ["name"] = "Elan", ["role"] = "owner", ["server"] = "2.31.1",
        ["maxFileMb"] = 25, ["canShareFiles"] = true, ["canModerate"] = true, ["canManage"] = true, ["canPin"] = true,
        ["features"] = new JsonArray(Features.Select(f => (JsonNode)JsonValue.Create(f)).ToArray()),
        ["canManageFiles"] = true, ["filesMaxMb"] = 50, ["canShareClassic"] = true,
        ["badges"] = new JsonObject { ["enabled"] = true, ["locked"] = false },
        ["lodge"] = new JsonObject { ["name"] = "The Silver Lodge", ["welcome"] = "Be nice.\nRaids Thu." },
        ["calendar"] = new JsonObject { ["now"] = 1760950000000, ["events"] = new JsonArray(OldEvent()) },
        ["canInvite"] = true, ["canRetire"] = true, ["canPurge"] = true,
        ["device"] = new JsonObject { ["id"] = "d-4f2a", ["name"] = "Desktop" },
        ["reactions"] = new JsonArray(Reactions.Select(f => (JsonNode)JsonValue.Create(f)).ToArray()),
        ["pins"] = new JsonObject { ["general"] = new JsonArray(OldPin()), ["officers"] = new JsonArray() },
        ["channels"] = new JsonArray(Channel("general", "General", "text", "guest", 0), Channel("officers", "Officers", "text", "officer", 0), Channel("v1", "Raid voice", "voice", "member", 25)),
        ["users"] = new JsonArray(OldOwner(), OldGuest()),
        ["profile"] = OldOwnProfile(),
        ["names"] = new JsonObject { ["Elan"] = "Elan the Hunter", ["Nova"] = "Nova Star" },
        ["history"] = new JsonObject
        {
            ["general"] = new JsonArray(OldMsg("m1", 1760900000000, "Raid Thursday 20:00"), OldMsg("m2", 1760900050000, "🗡️ in")),
            ["officers"] = new JsonArray(),
        },
    };

    // a guest on the same Lodge: no officers channel, no permissions, no device token, an unnamed session-only profile, no badges asked
    static WelcomeMsg GuestWelcome() => new WelcomeMsg
    {
        You = 9, Name = "Wanderer", Role = "guest", Server = "2.31.1", MaxFileMb = 25, CanShareFiles = false, Features = Features.ToList(), FilesMaxMb = 50,
        Badges = new BadgesStateMsg { Enabled = true }, Lodge = new LodgeInfoView { Name = "The Silver Lodge", Welcome = "" }, Calendar = new CalWelcome { Now = 1760950000000 },
        Reactions = Reactions.ToList(), Pins = new List<WelcomePins> { new WelcomePins { Channel = "general", Pins = new List<object>() } },
        Channels = { new LodgeChannelView { Id = "general", Name = "General", ChannelType = "text", MinRole = "guest" } },
        Users = { Guest() },
        Profile = new ProfileView { Name = "Wanderer", Found = true, Guest = true, Role = "guest", Online = true, DisplayName = "", About = "", PlayTimes = "" },
        Names = new List<KeyValuePair<string, string>>(),
        History = new List<WelcomeHistory> { new WelcomeHistory { Channel = "general", Messages = new List<object>() } },
    };

    static JsonObject OldGuestWelcome() => new()
    {
        ["t"] = "welcome", ["you"] = 9, ["name"] = "Wanderer", ["role"] = "guest", ["server"] = "2.31.1",
        ["maxFileMb"] = 25, ["canShareFiles"] = false, ["canModerate"] = false, ["canManage"] = false, ["canPin"] = false,
        ["features"] = new JsonArray(Features.Select(f => (JsonNode)JsonValue.Create(f)).ToArray()),
        ["canManageFiles"] = false, ["filesMaxMb"] = 50, ["canShareClassic"] = false,
        ["badges"] = new JsonObject { ["enabled"] = true, ["locked"] = false },
        ["lodge"] = new JsonObject { ["name"] = "The Silver Lodge", ["welcome"] = "" },
        ["calendar"] = new JsonObject { ["now"] = 1760950000000, ["events"] = new JsonArray() },
        ["canInvite"] = false, ["canRetire"] = false, ["canPurge"] = false,
        ["device"] = null,
        ["reactions"] = new JsonArray(Reactions.Select(f => (JsonNode)JsonValue.Create(f)).ToArray()),
        ["pins"] = new JsonObject { ["general"] = new JsonArray() },
        ["channels"] = new JsonArray(Channel("general", "General", "text", "guest", 0)),
        ["users"] = new JsonArray(OldGuest()),
        ["profile"] = new JsonObject
        {
            ["name"] = "Wanderer", ["found"] = true, ["guest"] = true, ["role"] = "guest", ["online"] = true, ["restricted"] = false, ["avatarId"] = null, ["accent"] = null,
            ["displayName"] = "", ["about"] = "", ["playTimes"] = "", ["chars"] = new JsonArray(),
        },
        ["names"] = new JsonObject(),
        ["history"] = new JsonObject { ["general"] = new JsonArray() },
    };

    // ---- golden: the wire is what the old JsonObject literal sent, key order included

    [Fact]
    public void Golden_full_welcome_matches_the_old_literal()
    {
        var w = Written(FullOwner().Write);
        SameJson(OldFullOwner(), w.D);
        Assert.Equal(WelcomeKeys, w.D.Keys);
        Assert.Equal(KeysOf(OldFullOwner()), w.D.Keys);
        Assert.Equal(new[] { "general", "officers" }, ((Dictionary<string, object>)w.D["history"]).Keys);
        Assert.Equal(new[] { "Elan", "Nova" }, ((Dictionary<string, object>)w.D["names"]).Keys);
    }

    [Fact]
    public void Golden_guest_welcome_matches_the_old_literal()
    {
        var w = Written(GuestWelcome().Write);
        SameJson(OldGuestWelcome(), w.D);
        Assert.Equal(WelcomeKeys, w.D.Keys);
        Assert.True(w.D.ContainsKey("device") && w.D["device"] == null);                // JSON null, as the old literal wrote DeviceView's null
    }

    [Fact]
    public void Typed_parts_write_like_the_stored_records()
    {
        // the server passes stored records through; written from the typed items they come out the same (what the read side sees)
        var typed = FullOwner();
        typed.History[0] = new WelcomeHistory { Channel = "general", Items = { ChatMessage.Read(new TestFields(StoredMsg("m1", 1760900000000, "Raid Thursday 20:00"))), ChatMessage.Read(new TestFields(StoredMsg("m2", 1760900050000, "🗡️ in"))) } };
        typed.Pins[0] = new WelcomePins { Channel = "general", Items = { ChatPin.Read(new TestFields(StoredPin())) } };
        SameJson(OldFullOwner(), Written(typed.Write).D);
    }

    // ---- round trips (read through the lenient reader, as the Outpost does)

    [Fact]
    public void Full_welcome_round_trips()
    {
        var r = WelcomeMsg.Read(TestFields.From(OldFullOwner()));
        Assert.Equal((1, "Elan", "owner", "2.31.1", 25, 50), (r.You, r.Name, r.Role, r.Server, r.MaxFileMb, r.FilesMaxMb));
        Assert.True(r.CanShareFiles && r.CanModerate && r.CanManage && r.CanPin && r.CanManageFiles && r.CanShareClassic && r.CanInvite && r.CanRetire && r.CanPurge);
        Assert.Equal(Features, r.Features);
        Assert.Equal(Reactions, r.Reactions);
        Assert.Equal((true, false), (r.Badges.Enabled, r.Badges.Locked));
        Assert.Equal(("The Silver Lodge", "Be nice.\nRaids Thu."), (r.Lodge.Name, r.Lodge.Welcome));
        Assert.Equal((1760950000000L, "Molten Core", 8, "Nova"), (r.Calendar.Now, r.Calendar.Events[0].Title, r.Calendar.Events[0].Healer, r.Calendar.Events[0].Signups[0].Name));
        Assert.Equal(("d-4f2a", "Desktop"), (r.Device.Id, r.Device.Name));
        Assert.Equal(new[] { "general", "officers" }, r.Pins.Select(p => p.Channel));
        Assert.Equal(("m1", "Raid Thursday 20:00", "Nova", 1760900100000L), (r.Pins[0].Items[0].Id, r.Pins[0].Items[0].Text, r.Pins[0].Items[0].PinnedBy, r.Pins[0].Items[0].PinnedAt));
        Assert.Empty(r.Pins[1].Items);
        Assert.Equal(new[] { "general", "officers", "v1" }, r.Channels.Select(c => c.Id));
        Assert.Equal(("voice", "member", 25), (r.Channels[2].ChannelType, r.Channels[2].MinRole, r.Channels[2].Max));
        Assert.Equal(new[] { 1, 9 }, r.Users.Select(u => u.Id));
        Assert.Equal(("Elan the Hunter", true), (r.Users[0].DisplayName, r.Users[1].Guest));
        Assert.Equal("Orgrimmar", LodgeGame.Read(TestFields.From((JsonObject)OldOwner()["game"]!.DeepClone())).Zone);
        Assert.NotNull(r.Users[0].Game);
        Assert.Equal(("Elan", "members", "Elan", 1), (r.Profile.Name, r.Profile.VisibleTo, r.Profile.Main, r.Profile.Chars.Count));
        Assert.Equal(new[] { "Elan:Elan the Hunter", "Nova:Nova Star" }, r.Names.Select(kv => kv.Key + ":" + kv.Value));
        Assert.Equal(new[] { "general", "officers" }, r.History.Select(h => h.Channel));
        Assert.Equal(new[] { "m1", "m2" }, r.History[0].Items.Select(x => x.Id));
        Assert.Equal(("🗡️ in", "general", (int?)1), (r.History[0].Items[1].Text, r.History[0].Items[1].Channel, r.History[0].Items[1].FromId));
        Assert.NotNull(r.History[0].Messages);                                           // the raw list, as the server passes its records through
        Assert.Empty(r.OldHistory);

        // and back: what was read writes the same welcome again
        SameJson(OldFullOwner(), Written(r.Write).D);
        Assert.Equal("welcome", Written(new WelcomeMsg().Write).Str("t"));
    }

    [Fact]
    public void Guest_welcome_round_trips()
    {
        var r = WelcomeMsg.Read(TestFields.From(OldGuestWelcome()));
        Assert.Equal((9, "guest", false), (r.You, r.Role, r.CanShareFiles));
        Assert.False(r.CanModerate || r.CanManage || r.CanPin || r.CanManageFiles || r.CanShareClassic || r.CanInvite || r.CanRetire || r.CanPurge);
        Assert.Null(r.Device);
        Assert.Equal(("Wanderer", true, true), (r.Profile.Name, r.Profile.Found, r.Profile.Guest));
        Assert.Empty(r.Names);
        Assert.Single(r.Users);
        Assert.Equal("general", r.History.Single().Channel);
        Assert.Empty(r.History[0].Items);
        SameJson(OldGuestWelcome(), Written(r.Write).D);
    }

    // ---- older servers: every part may be missing, and is then read as the Outpost read it before

    [Fact]
    public void An_older_servers_welcome_reads_with_parts_missing()
    {
        // a 2.2 server: no features, no permission flags, no badges / lodge / calendar / device / pins / profile / names
        var old = new JsonObject
        {
            ["t"] = "welcome", ["you"] = 4, ["name"] = "Elan", ["role"] = "member", ["server"] = "2.2.0",
            ["channels"] = new JsonArray(Channel("general", "General", "text", "guest", 0)),
            ["users"] = new JsonArray(new JsonObject { ["id"] = 4, ["name"] = "Elan", ["guest"] = false, ["room"] = null }),
            ["history"] = new JsonObject { ["general"] = new JsonArray(new JsonObject { ["id"] = "m1", ["at"] = 5, ["from"] = "Elan", ["text"] = "hi" }, "junk", 3) },
        };
        var r = WelcomeMsg.Read(TestFields.From(old));
        Assert.Equal((4, "2.2.0", 0, 0), (r.You, r.Server, r.MaxFileMb, r.FilesMaxMb));
        Assert.True(r.CanShareFiles);                                                    // missing = allowed (servers before the flag let everyone attach)
        Assert.False(r.CanPin || r.CanModerate || r.CanInvite);
        Assert.Empty(r.Features);
        Assert.Empty(r.Reactions);                                                       // the Outpost falls back to its default set
        Assert.Null(r.Badges); Assert.Null(r.Lodge); Assert.Null(r.Calendar); Assert.Null(r.Device);
        Assert.Null(r.Pins); Assert.Null(r.Profile); Assert.Null(r.Names);              // null = not sent: the Outpost keeps what it had
        Assert.False(r.Users[0].HasDisplayName);
        var h = r.History.Single();
        Assert.Equal(("m1", "general"), (h.Items.Single().Id, h.Items[0].Channel));      // an entry without a channel gets its list's channel
        Assert.Empty(r.OldHistory);

        // a 1.x server: no channels at all, the history as one list (the Outpost groups it by channel)
        var v1 = new JsonObject
        {
            ["t"] = "welcome", ["you"] = 2,
            ["users"] = new JsonArray(),
            ["history"] = new JsonArray(new JsonObject { ["id"] = "a", ["text"] = "one", ["channel"] = "general" }, new JsonObject { ["id"] = "b", ["text"] = "two" }),
        };
        var r1 = WelcomeMsg.Read(TestFields.From(v1));
        Assert.Null(r1.History);
        Assert.Empty(r1.Channels);
        Assert.Equal(new[] { "a", "b" }, r1.OldHistory.Select(x => x.Id));
        Assert.Equal(new[] { "general", null }, r1.OldHistory.Select(x => x.Channel));
        Assert.Null(r1.Role);                                                            // the Outpost: "member"
    }

    [Fact]
    public void Welcome_reads_leniently_like_before()
    {
        // wrong types are "missing"; a name whose display name is no string reads as null; entries that are no objects are skipped
        var f = new TestFields(new Dictionary<string, object>
        {
            ["you"] = "7", ["canShareFiles"] = "no", ["canPin"] = 1L, ["features"] = new List<object> { "react", 5, null, "pin" },
            ["device"] = "d1", ["badges"] = new List<object>(), ["names"] = new Dictionary<string, object> { ["Elan"] = 5L, ["Nova"] = "Nova Star" },
            ["users"] = new List<object> { "x", new Dictionary<string, object> { ["id"] = 3L } },
            ["channels"] = new List<object> { 1L, new Dictionary<string, object> { ["id"] = "general" } },
            ["pins"] = new Dictionary<string, object> { ["general"] = "not a list", ["lfg"] = new List<object> { 2L, new Dictionary<string, object> { ["id"] = "p1" } } },
            ["history"] = new Dictionary<string, object> { ["general"] = null },
        });
        var r = WelcomeMsg.Read(f);
        Assert.Equal(0, r.You);
        Assert.False(r.CanShareFiles);                                                   // present but not true: off, as m.Bool read it
        Assert.False(r.CanPin);
        Assert.Equal(new[] { "react", "pin" }, r.Features);
        Assert.Null(r.Device); Assert.Null(r.Badges);
        Assert.Equal(new[] { "Elan:", "Nova:Nova Star" }, r.Names.Select(kv => kv.Key + ":" + kv.Value));
        Assert.Equal(3, r.Users.Single().Id);
        Assert.Equal("general", r.Channels.Single().Id);
        Assert.Equal((0, 1, "p1"), (r.Pins[0].Items.Count, r.Pins[1].Items.Count, r.Pins[1].Items[0].Id));
        Assert.Empty(r.History.Single().Items);
    }
}
