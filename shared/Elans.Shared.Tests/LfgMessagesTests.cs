using System.Text.Json;
using System.Text.Json.Nodes;
using Elans.Shared;

namespace Elans.Shared.Tests;

// The typed LFG messages (stage 2 pilot): every message survives Write -> Read, and writes exactly the JSON the old hand-built
// dictionary / JsonObject sent (golden values copied from the code at c712015) - the wire must not change.
public class LfgMessagesTests
{
    static TestFields Written(Action<ILodgeWriter> write) => TestFields.Written(write);
    static void SameJson(object expected, object actual) => TestFields.SameJson(expected, actual);

    static LfgCard SampleCard() => new LfgCard
    {
        Title = "The Deadmines", Start = 1760900400000, Now = false, Note = "bring water", Status = "open", By = "Elan",
        Tank = 1, Healer = 1, Dps = 3,
        Signups = { new LfgSignup { Name = "Elan", Role = "tank" }, new LfgSignup { Name = "Mira", Role = "maybe" } },
    };

    // ---- round trips

    [Fact]
    public void Client_messages_round_trip()
    {
        var j = LfgJoinMsg.Read(Written(new LfgJoinMsg { Id = "c1", Role = "healer" }.Write));
        Assert.Equal(("c1", "healer"), (j.Id, j.Role));
        Assert.Equal("c2", LfgMaybeMsg.Read(Written(new LfgMaybeMsg { Id = "c2" }.Write)).Id);
        Assert.Equal("c3", LfgLeaveMsg.Read(Written(new LfgLeaveMsg { Id = "c3" }.Write)).Id);
        Assert.Equal("c4", LfgCloseMsg.Read(Written(new LfgCloseMsg { Id = "c4" }.Write)).Id);
        Assert.Equal("c5", LfgCancelMsg.Read(Written(new LfgCancelMsg { Id = "c5" }.Write)).Id);
    }

    [Fact]
    public void Each_message_writes_its_own_type()
    {
        Assert.Equal("lfg:join", Written(new LfgJoinMsg().Write).Str("t"));
        Assert.Equal("lfg:maybe", Written(new LfgMaybeMsg().Write).Str("t"));
        Assert.Equal("lfg:leave", Written(new LfgLeaveMsg().Write).Str("t"));
        Assert.Equal("lfg:close", Written(new LfgCloseMsg().Write).Str("t"));
        Assert.Equal("lfg:cancel", Written(new LfgCancelMsg().Write).Str("t"));
        Assert.Equal("lfg", Written(new LfgUpdateMsg().Write).Str("t"));
    }

    [Fact]
    public void Update_round_trips_with_the_whole_card()
    {
        var u = LfgUpdateMsg.Read(Written(new LfgUpdateMsg { Channel = "general", Id = "c1", Card = SampleCard(), Text = "[Looking for group] x" }.Write));
        Assert.Equal(("general", "c1", "[Looking for group] x"), (u.Channel, u.Id, u.Text));
        var c = u.Card;
        Assert.Equal(("The Deadmines", 1760900400000L, false, "bring water", "open", "Elan"), (c.Title, c.Start, c.Now, c.Note, c.Status, c.By));
        Assert.Equal((1, 1, 3), (c.Tank, c.Healer, c.Dps));
        Assert.Equal((1, 1, 3, 0), (c.SlotsOf("tank"), c.SlotsOf("healer"), c.SlotsOf("dps"), c.SlotsOf("maybe")));
        Assert.Equal(new[] { ("Elan", "tank"), ("Mira", "maybe") }, c.Signups.Select(s => (s.Name, s.Role)));
    }

    [Fact]
    public void Reading_is_lenient_like_before()
    {
        // wrong types are "missing", a non-object signup is skipped, a missing card stays null
        var f = new TestFields(new Dictionary<string, object>
        {
            ["channel"] = 5, ["id"] = "c1",
            ["lfg"] = new Dictionary<string, object> { ["title"] = 7, ["now"] = "yes", ["slots"] = "x", ["signups"] = new List<object> { "junk", new Dictionary<string, object> { ["n"] = "A", ["r"] = "dps" } } },
        });
        var u = LfgUpdateMsg.Read(f);
        Assert.Null(u.Channel);
        Assert.Null(u.Text);
        Assert.Null(u.Card.Title);
        Assert.Null(u.Card.Status);
        Assert.False(u.Card.Now);
        Assert.Equal(0, u.Card.Tank);
        Assert.Single(u.Card.Signups);
        Assert.Null(LfgUpdateMsg.Read(new TestFields(new Dictionary<string, object> { ["id"] = "c1" })).Card);
    }

    // ---- golden: what the old code sent

    [Fact]
    public void Golden_client_messages_match_the_old_Outpost_dictionaries()
    {
        // src/Lodge/LodgeSession.cs at c712015
        SameJson(new Dictionary<string, object> { ["t"] = "lfg:join", ["id"] = "c1", ["role"] = "tank" }, Written(new LfgJoinMsg { Id = "c1", Role = "tank" }.Write).D);
        SameJson(new Dictionary<string, object> { ["t"] = "lfg:maybe", ["id"] = "c1" }, Written(new LfgMaybeMsg { Id = "c1" }.Write).D);
        SameJson(new Dictionary<string, object> { ["t"] = "lfg:leave", ["id"] = "c1" }, Written(new LfgLeaveMsg { Id = "c1" }.Write).D);
        SameJson(new Dictionary<string, object> { ["t"] = "lfg:close", ["id"] = "c1" }, Written(new LfgCloseMsg { Id = "c1" }.Write).D);
        SameJson(new Dictionary<string, object> { ["t"] = "lfg:cancel", ["id"] = "c1" }, Written(new LfgCancelMsg { Id = "c1" }.Write).D);
        // a null role (never sent by the buttons) still goes out as null, as the dictionary did
        SameJson(new Dictionary<string, object> { ["t"] = "lfg:join", ["id"] = "c1", ["role"] = null }, Written(new LfgJoinMsg { Id = "c1" }.Write).D);
    }

    [Fact]
    public void Golden_update_matches_the_old_server_JsonObject()
    {
        // the card as server/Lodge.Server/Lfg.cs stores it (Create + Join), and ChatLfg.Finish's broadcast at c712015
        var stored = new JsonObject
        {
            ["title"] = "The Deadmines", ["start"] = 1760900400000L, ["now"] = false, ["note"] = "bring water", ["status"] = "open", ["by"] = "Elan",
            ["slots"] = new JsonObject { ["tank"] = 1, ["healer"] = 1, ["dps"] = 3 },
            ["signups"] = new JsonArray(new JsonObject { ["n"] = "Elan", ["r"] = "tank" }, new JsonObject { ["n"] = "Mira", ["r"] = "maybe" }),
        };
        var lfg = stored.DeepClone().AsObject();
        var old = new JsonObject { ["t"] = "lfg", ["channel"] = "general", ["id"] = "c1", ["lfg"] = lfg, ["text"] = "[Looking for group] The Deadmines" };
        var now = Written(new LfgUpdateMsg { Channel = "general", Id = "c1", Card = SampleCard(), Text = "[Looking for group] The Deadmines" }.Write).D;
        SameJson(old, now);

        // a fresh "now" card with no sign-ups yet: the empty signups array is still sent
        var fresh = new JsonObject
        {
            ["title"] = "RFC", ["start"] = 1760900000000L, ["now"] = true, ["note"] = "", ["status"] = "closed", ["by"] = "Elan",
            ["slots"] = new JsonObject { ["tank"] = 0, ["healer"] = 0, ["dps"] = 5 }, ["signups"] = new JsonArray(),
        };
        var card = new LfgCard { Title = "RFC", Start = 1760900000000L, Now = true, Note = "", Status = "closed", By = "Elan", Dps = 5 };
        SameJson(fresh, Written(card.Write).D);
    }
}
