using System.Text.Json.Nodes;
using Elans.Shared;

namespace Elans.Shared.Tests;

// The typed presence messages: every message survives Write -> Read, and writes exactly the JSON the old hand-built dictionary / JsonObject
// sent (golden values copied from src/Lodge/LodgeSession.cs, LodgeSession.LivePos.cs, server/Lodge.Server/Hub.cs (Member.ToJson, UserFor,
// the join / leave / user broadcasts), Hub.LivePos.cs (PosMessage) and Voice.cs (PresenceModule.BuildGame) at c712015) - the wire must not change.
public class PresenceMessagesTests
{
    static TestFields Written(Action<ILodgeWriter> write) => TestFields.Written(write);
    static void SameJson(object expected, object actual) => TestFields.SameJson(expected, actual);

    static Dictionary<string, object> Game() => new()
    {
        ["playing"] = true, ["name"] = "Elan", ["realm"] = "Realm", ["class"] = "Hunter", ["classFile"] = "HUNTER", ["level"] = 60, ["zone"] = "The Barrens",
        ["guild"] = "G", ["race"] = "Orc", ["raceFile"] = "Orc", ["sex"] = 2, ["flags"] = 129, ["xpPct"] = 41, ["rested"] = true, ["groupSize"] = 3,
        ["inInstance"] = false, ["instanceName"] = null,
    };

    static LodgeUser User(bool invisible = false, object game = null) => new LodgeUser
    {
        Id = 7, Name = "Nova", Guest = false, Role = "officer", Room = "v1", Voice = true, Muted = true, Deaf = false, ServerMuted = false,
        AvatarId = "a12", Accent = null, DisplayName = "Nova Star", Status = "busy", Note = "raid", Game = game, Invisible = invisible,
    };

    // Member.ToJson + UserFor as the server built them
    static JsonObject OldUser(bool invisible = false, JsonNode game = null)
    {
        var j = new JsonObject
        {
            ["id"] = 7, ["name"] = "Nova", ["guest"] = false, ["role"] = "officer",
            ["room"] = "v1", ["voice"] = true, ["muted"] = true, ["deaf"] = false, ["serverMuted"] = false,
            ["avatarId"] = "a12", ["accent"] = null, ["displayName"] = "Nova Star",
            ["status"] = "busy", ["note"] = "raid", ["game"] = game,
        };
        if (invisible) j["invisible"] = true;
        return j;
    }

    static readonly string[] UserKeys = { "id", "name", "guest", "role", "room", "voice", "muted", "deaf", "serverMuted", "avatarId", "accent", "displayName", "status", "note", "game" };
    static readonly string[] GameKeys = { "playing", "name", "realm", "class", "classFile", "level", "zone", "guild", "race", "raceFile", "sex", "flags", "xpPct", "rested", "groupSize", "inInstance", "instanceName" };

    // ---- round trips

    [Fact]
    public void Client_messages_round_trip()
    {
        var st = StatusMsg.Read(Written(new StatusMsg { Status = "away", Note = "afk", Visibility = "invisible" }.Write));
        Assert.Equal(("away", "afk", "invisible"), (st.Status, st.Note, st.Visibility));

        var g = GameMsg.Read(Written(new GameMsg
        {
            Playing = true, Visibility = "visible", HasCharacter = true, Name = "Elan", Realm = "R", Class = "Hunter", ClassFile = "HUNTER", Level = 60, Guild = "G",
            Race = "Orc", RaceFile = "Orc", Sex = 2, HasZone = true, Zone = "Barrens", HasLive = true, Flags = 17, GroupSize = 2, InInstance = true, InstanceName = "WC",
            HasXp = true, XpPct = 41, Rested = true,
        }.Write));
        Assert.True(g.Share && g.Playing && g.HasCharacter && g.HasZone && g.HasLive && g.HasXp && g.InInstance && g.Rested);
        Assert.Equal(("visible", "Elan", "R", "Hunter", "HUNTER", 60, "G", "Orc", "Orc", 2), (g.Visibility, g.Name, g.Realm, g.Class, g.ClassFile, g.Level, g.Guild, g.Race, g.RaceFile, g.Sex));
        Assert.Equal(("Barrens", 17, 2, "WC", (int?)41), (g.Zone, g.Flags, g.GroupSize, g.InstanceName, g.XpPct));
        var off = GameMsg.Read(Written(new GameMsg { Share = false, Visibility = "visible" }.Write));
        Assert.Equal((false, "visible", false), (off.Share, off.Visibility, off.HasCharacter));
        var bare = GameMsg.Read(Written(new GameMsg { Playing = false, Visibility = "visible" }.Write));
        Assert.True(bare.Share && !bare.HasCharacter && !bare.HasZone && !bare.HasLive && !bare.HasXp && bare.XpPct == null);

        var p = PositionMsg.Read(Written(new PositionMsg { Valid = true, MapId = 1429, Fx = 0.4123, Fy = 0.62, Move = 32, Scope = "officers" }.Write));
        Assert.Equal((true, true, 1429, 0.4123, 0.62, 32, "officers"), (p.Share, p.Valid, p.MapId, p.Fx, p.Fy, p.Move, p.Scope));
        Assert.Equal((true, false), (PositionMsg.Read(Written(new PositionMsg { Valid = false }.Write)) is var n ? (n.Share, n.Valid) : default));
        Assert.False(PositionMsg.Read(Written(new PositionMsg { Share = false }.Write)).Share);
    }

    [Fact]
    public void Server_messages_round_trip()
    {
        var j = JoinMsg.Read(Written(new JoinMsg { User = User(game: Game()) }.Write)).User;
        Assert.Equal((7, "Nova", false, "officer", "v1", true, true, false, false), (j.Id, j.Name, j.Guest, j.Role, j.Room, j.Voice, j.Muted, j.Deaf, j.ServerMuted));
        Assert.Equal(("a12", (string)null, "Nova Star", true, "busy", "raid", false), (j.AvatarId, j.Accent, j.DisplayName, j.HasDisplayName, j.Status, j.Note, j.Invisible));
        var game = LodgeGame.Read(new TestFields((Dictionary<string, object>)j.Game));
        Assert.Equal((true, "Elan", "The Barrens", 60, 129, (int?)41, 3, (string)null), (game.Playing, game.Name, game.Zone, game.Level, game.Flags, game.XpPct, game.GroupSize, game.InstanceName));

        var u = UserMsg.Read(Written(new UserMsg { User = User(invisible: true) }.Write)).User;
        Assert.True(u.Invisible);
        Assert.Null(u.Game);
        Assert.Equal(7, LeaveMsg.Read(Written(new LeaveMsg { Id = 7 }.Write)).Id);

        var mp = MemberPosMsg.Read(Written(new MemberPosMsg { Id = 7, Valid = true, MapId = 1429, Fx = 0.41, Fy = 0.62, Move = 64 }.Write));
        Assert.Equal((7, true, 1429, (double?)0.41, (double?)0.62, 64), (mp.Id, mp.Valid, mp.MapId, mp.Fx, mp.Fy, mp.Move));
        var gone = MemberPosMsg.Read(Written(new MemberPosMsg { Id = 7, Valid = false }.Write));
        Assert.Equal((7, false, (double?)null), (gone.Id, gone.Valid, gone.Fx));

        var lg = new LodgeGame { Playing = true, Name = "Elan", Level = 60, Sex = 3, Flags = 16, InInstance = true, InstanceName = "WC" };
        var back = LodgeGame.Read(Written(lg.Write));
        Assert.Equal((true, "Elan", 60, 3, 16, true, "WC", (int?)null), (back.Playing, back.Name, back.Level, back.Sex, back.Flags, back.InInstance, back.InstanceName, back.XpPct));
    }

    [Fact]
    public void Each_message_writes_its_own_type()
    {
        Assert.Equal("status", Written(new StatusMsg().Write).Str("t"));
        Assert.Equal("game", Written(new GameMsg().Write).Str("t"));
        Assert.Equal("game", Written(new GameMsg { Share = false }.Write).Str("t"));
        Assert.Equal("position", Written(new PositionMsg().Write).Str("t"));
        Assert.Equal("join", Written(new JoinMsg { User = User() }.Write).Str("t"));
        Assert.Equal("user", Written(new UserMsg { User = User() }.Write).Str("t"));
        Assert.Equal("leave", Written(new LeaveMsg().Write).Str("t"));
        Assert.Equal("memberpos", Written(new MemberPosMsg().Write).Str("t"));
    }

    [Fact]
    public void Reading_is_lenient_like_before()
    {
        // a join / user without its entry: no user (the Outpost ignores it); wrong types are null / 0 / false
        Assert.Null(JoinMsg.Read(new TestFields()).User);
        Assert.Null(UserMsg.Read(new TestFields(new Dictionary<string, object> { ["user"] = "x" })).User);
        var u = LodgeUser.Read(new TestFields(new Dictionary<string, object> { ["id"] = "7", ["name"] = 5, ["muted"] = 1, ["game"] = "playing", ["displayName"] = 3 }));
        Assert.Equal((0, (string)null, false, (object)null, true, (string)null), (u.Id, u.Name, u.Muted, u.Game, u.HasDisplayName, u.DisplayName));
        Assert.False(LodgeUser.Read(new TestFields()).HasDisplayName);           // an older server: no display names

        // xpPct: only a number counts (as ApplyGame's "is double" check)
        Assert.Null(LodgeGame.Read(new TestFields(new Dictionary<string, object> { ["xpPct"] = "41" })).XpPct);
        Assert.Null(LodgeGame.Read(null));

        // memberpos: fx / fy that are not numbers drop the dot (null)
        var mp = MemberPosMsg.Read(new TestFields(new Dictionary<string, object> { ["id"] = 3, ["valid"] = true, ["fx"] = "0.4", ["fy"] = 0.5 }));
        Assert.Equal((3, true, (double?)null, (double?)0.5), (mp.Id, mp.Valid, mp.Fx, mp.Fy));

        // share: only a JSON false stops sharing (as the server's raw check)
        foreach (var notFalse in new object[] { "no", 0, null, true, new Dictionary<string, object>(), new List<object>() })
            Assert.True(GameMsg.Read(new TestFields(new Dictionary<string, object> { ["share"] = notFalse })).Share);
        Assert.False(GameMsg.Read(new TestFields(new Dictionary<string, object> { ["share"] = false })).Share);
        Assert.False(PositionMsg.Read(new TestFields(new Dictionary<string, object> { ["share"] = false })).Share);
    }

    // ---- golden: what the old code sent

    [Fact]
    public void Golden_client_messages_match_the_old_Outpost_dictionaries()
    {
        SameJson(new Dictionary<string, object> { ["t"] = "status", ["status"] = "away", ["note"] = "back soon", ["visibility"] = "visible" },
            Written(new StatusMsg { Status = "away", Note = "back soon", Visibility = "visible" }.Write).D);
        SameJson(new Dictionary<string, object> { ["t"] = "status", ["status"] = "online", ["note"] = null, ["visibility"] = "invisible" },
            Written(new StatusMsg { Status = "online", Visibility = "invisible" }.Write).D);

        // BuildGameMessage: sharing off; no character; everything; zone and XP off
        SameJson(new Dictionary<string, object> { ["t"] = "game", ["share"] = false, ["visibility"] = "visible" }, Written(new GameMsg { Share = false, Visibility = "visible" }.Write).D);
        SameJson(new Dictionary<string, object> { ["t"] = "game", ["playing"] = false, ["visibility"] = "invisible" }, Written(new GameMsg { Playing = false, Visibility = "invisible" }.Write).D);
        var full = new Dictionary<string, object>
        {
            ["t"] = "game", ["playing"] = true, ["visibility"] = "visible",
            ["name"] = "Elan", ["realm"] = "Realm", ["class"] = "Hunter", ["classFile"] = "HUNTER", ["level"] = 60, ["guild"] = null,
            ["race"] = "Orc", ["raceFile"] = "Orc", ["sex"] = 2, ["zone"] = "Wailing Caverns",
            ["flags"] = 209, ["groupSize"] = 5, ["inInstance"] = true, ["instanceName"] = "Wailing Caverns", ["xpPct"] = 41, ["rested"] = true,
        };
        GameMsg Full() => new GameMsg
        {
            Playing = true, Visibility = "visible", HasCharacter = true, Name = "Elan", Realm = "Realm", Class = "Hunter", ClassFile = "HUNTER", Level = 60, Guild = null,
            Race = "Orc", RaceFile = "Orc", Sex = 2, HasZone = true, Zone = "Wailing Caverns", HasLive = true, Flags = 209, GroupSize = 5, InInstance = true,
            InstanceName = "Wailing Caverns", HasXp = true, XpPct = 41, Rested = true,
        };
        var w = Written(Full().Write);
        SameJson(full, w.D);
        Assert.Equal(full.Keys, w.D.Keys);                                   // key order as before
        var zoneXpOff = Full(); zoneXpOff.HasZone = false; zoneXpOff.Zone = null; zoneXpOff.InstanceName = null; zoneXpOff.HasXp = false;
        var expect = new Dictionary<string, object>(full); expect.Remove("zone"); expect.Remove("instanceName"); expect.Remove("xpPct"); expect.Remove("rested");
        SameJson(expect, Written(zoneXpOff.Write).D);
        var noXpYet = Full(); noXpYet.XpPct = null;                          // XP sharing on, but the game did not say (xpPct left out, rested sent)
        expect = new Dictionary<string, object>(full); expect.Remove("xpPct");
        SameJson(expect, Written(noXpYet.Write).D);
        var zoneNull = Full(); zoneNull.Zone = null;                         // zone shared but unknown: sent as null, as before
        expect = new Dictionary<string, object>(full) { ["zone"] = null };
        SameJson(expect, Written(zoneNull.Write).D);

        // BuildPositionMessage: stopped, none right now, a position
        SameJson(new Dictionary<string, object> { ["t"] = "position", ["share"] = false }, Written(new PositionMsg { Share = false }.Write).D);
        SameJson(new Dictionary<string, object> { ["t"] = "position", ["valid"] = false }, Written(new PositionMsg { Valid = false }.Write).D);
        var pos = new Dictionary<string, object> { ["t"] = "position", ["mapId"] = 1429, ["fx"] = 0.4123, ["fy"] = 0.62, ["move"] = 32, ["valid"] = true, ["scope"] = "everyone" };
        var pw = Written(new PositionMsg { Valid = true, MapId = 1429, Fx = 0.4123, Fy = 0.62, Move = 32, Scope = "everyone" }.Write);
        SameJson(pos, pw.D);
        Assert.Equal(pos.Keys, pw.D.Keys);
    }

    [Fact]
    public void Golden_server_messages_match_the_old_JsonObjects()
    {
        // BuildGame's object (every key, nulls kept)
        var oldGame = new JsonObject
        {
            ["playing"] = true, ["name"] = "Elan", ["realm"] = null, ["class"] = "Hunter", ["classFile"] = "HUNTER", ["level"] = 60, ["zone"] = "The Barrens",
            ["guild"] = null, ["race"] = "Orc", ["raceFile"] = "Orc", ["sex"] = 2, ["flags"] = 128, ["xpPct"] = null, ["rested"] = false, ["groupSize"] = 2,
            ["inInstance"] = false, ["instanceName"] = null,
        };
        var gw = Written(new LodgeGame { Playing = true, Name = "Elan", Class = "Hunter", ClassFile = "HUNTER", Level = 60, Zone = "The Barrens", Race = "Orc", RaceFile = "Orc", Sex = 2, Flags = 128, GroupSize = 2 }.Write);
        SameJson(oldGame, gw.D);
        Assert.Equal(GameKeys, gw.D.Keys);

        // join / user: the full entry, an invisible member's own (or SeeInvisible's) entry, the masked voice-room entry; leave
        SameJson(new JsonObject { ["t"] = "join", ["user"] = OldUser(game: oldGame.DeepClone()) }, Written(new JoinMsg { User = User(game: TestFields.From(oldGame).D) }.Write).D);
        SameJson(new JsonObject { ["t"] = "user", ["user"] = OldUser(invisible: true) }, Written(new UserMsg { User = User(invisible: true) }.Write).D);
        var masked = OldUser(); masked["status"] = "online"; masked["note"] = "";
        var mu = User(); mu.Status = "online"; mu.Note = "";
        SameJson(new JsonObject { ["t"] = "user", ["user"] = masked }, Written(new UserMsg { User = mu }.Write).D);
        SameJson(new JsonObject { ["t"] = "leave", ["id"] = 7 }, Written(new LeaveMsg { Id = 7 }.Write).D);

        // memberpos: a position and its withdrawal
        SameJson(new JsonObject { ["t"] = "memberpos", ["id"] = 7, ["mapId"] = 1429, ["fx"] = 0.41, ["fy"] = 0.62, ["move"] = 64, ["valid"] = true },
            Written(new MemberPosMsg { Id = 7, Valid = true, MapId = 1429, Fx = 0.41, Fy = 0.62, Move = 64 }.Write).D);
        SameJson(new JsonObject { ["t"] = "memberpos", ["id"] = 7, ["valid"] = false }, Written(new MemberPosMsg { Id = 7, Valid = false }.Write).D);

        // key order as before
        Assert.Equal(UserKeys, Written(User().Write).D.Keys);
        Assert.Equal(UserKeys.Append("invisible"), Written(User(invisible: true).Write).D.Keys);
        Assert.Equal(new[] { "t", "user" }, Written(new JoinMsg { User = User() }.Write).D.Keys);
        Assert.Equal(new[] { "t", "id", "mapId", "fx", "fy", "move", "valid" }, Written(new MemberPosMsg { Valid = true }.Write).D.Keys);
        Assert.Equal(new[] { "t", "id", "valid" }, Written(new MemberPosMsg().Write).D.Keys);
    }
}
