using System.Text.Json.Nodes;
using Elans.Shared;

namespace Elans.Shared.Tests;

// The typed chat messages: every message survives Write -> Read, and writes exactly the JSON the old hand-built dictionary / JsonObject
// sent (golden values copied from src/Lodge/LodgeSession.cs (SendMessage, PostLfg, Edit, Delete, React, PinMessage, UnpinMessage) and
// server/Lodge.Server/Chat.cs (Message, Edit, Delete, React, Pin), ChatCalendar.cs (PostEventCard, RefreshEventCard) at c712015) - the
// wire must not change.
public class ChatMessagesTests
{
    static TestFields Written(Action<ILodgeWriter> write) => TestFields.Written(write);
    static void SameJson(object expected, object actual) => TestFields.SameJson(expected, actual);

    static LfgCard Card() => new LfgCard
    {
        Title = "The Deadmines", Start = 1760900400000, Now = false, Note = "bring water", Status = "open", By = "Elan",
        Tank = 1, Healer = 1, Dps = 3, Signups = { new LfgSignup { Name = "Elan", Role = "tank" } },
    };

    static JsonObject OldCard() => new JsonObject
    {
        ["title"] = "The Deadmines", ["start"] = 1760900400000, ["now"] = false, ["note"] = "bring water", ["status"] = "open", ["by"] = "Elan",
        ["slots"] = new JsonObject { ["tank"] = 1, ["healer"] = 1, ["dps"] = 3 }, ["signups"] = new JsonArray(new JsonObject { ["n"] = "Elan", ["r"] = "tank" }),
    };

    static ChatMessage Full() => new ChatMessage
    {
        Channel = "general", Id = "a1b2c3d4e5f6", At = 1760900000123, From = "Elan", FromId = 7, Text = "Deadmines at 8? 🗡️ @Mira",
        File = new ChatFile { Id = "f9", Name = "map.png", Size = 48213, Mime = "image/png" },
        ReplyTo = new ChatReply { Id = "m0", From = "Mira", Text = "who's up for a run?", By = "Mira", Snippet = "who's up for a run?" },
        Edited = true,
        Reactions = { new ChatReaction { Id = "ready", Users = { "Mira", "Bob" } }, new ChatReaction { Id = "lol", Users = { "Zed" } } },
    };

    static readonly string[] NewMsgKeys = { "t", "channel", "id", "at", "from", "fromId", "text", "file", "kind", "lfg", "replyTo" };

    // ---- round trips

    [Fact]
    public void Client_messages_round_trip()
    {
        var s = ChatSendMsg.Read(Written(new ChatSendMsg { Channel = "general", Text = "hi 👋", ReplyTo = "m1", FileId = "f1" }.Write));
        Assert.Equal(("general", "hi 👋", "m1", "f1", (string)null, (LfgRequest)null), (s.Channel, s.Text, s.ReplyTo, s.FileId, s.Kind, s.Lfg));
        var l = ChatSendMsg.Read(Written(new ChatSendMsg
        {
            Channel = "lfg", Kind = "lfg", Text = "SFK",
            Lfg = new LfgRequest { Title = "SFK", Now = false, Note = "lvl 20+", Tank = 1, Healer = 1, Dps = 3, Start = 1760900400000 },
        }.Write));
        Assert.Equal(("lfg", "SFK"), (l.Kind, l.Text));
        Assert.Equal(("SFK", false, "lvl 20+", 1, 1, 3, (long?)1760900400000), (l.Lfg.Title, l.Lfg.Now, l.Lfg.Note, l.Lfg.Tank, l.Lfg.Healer, l.Lfg.Dps, l.Lfg.Start));
        Assert.NotNull(l.LfgRaw);                                             // what the server's Lfg.Create validates
        Assert.Null(ChatSendMsg.Read(Written(new ChatSendMsg { Kind = "lfg", Lfg = new LfgRequest { Now = true } }.Write)).Lfg.Start);

        var e = EditMsg.Read(Written(new EditMsg { Id = "m1", Text = "fixed" }.Write));
        Assert.Equal(("m1", "fixed"), (e.Id, e.Text));
        Assert.Equal("m2", DeleteMsg.Read(Written(new DeleteMsg { Id = "m2" }.Write)).Id);
        var r = ReactMsg.Read(Written(new ReactMsg { Id = "m3", Emoji = "lol" }.Write));
        Assert.Equal(("m3", "lol"), (r.Id, r.Emoji));
        Assert.Equal("m4", PinMsg.Read(Written(new PinMsg { Id = "m4" }.Write)).Id);
        var u = UnpinMsg.Read(Written(new UnpinMsg { Id = "m5", Channel = "general" }.Write));
        Assert.Equal(("m5", "general"), (u.Id, u.Channel));
    }

    [Fact]
    public void The_stored_message_round_trips()
    {
        var m = ChatMessage.Read(Written(Full().Write));
        Assert.Equal(("general", "a1b2c3d4e5f6", 1760900000123L, "Elan", (int?)7, "Deadmines at 8? 🗡️ @Mira", true), (m.Channel, m.Id, m.At, m.From, m.FromId, m.Text, m.Edited));
        Assert.Equal(("f9", "map.png", 48213L, "image/png"), (m.File.Id, m.File.Name, m.File.Size, m.File.Mime));
        Assert.Equal(("m0", "Mira", "who's up for a run?", "Mira", "who's up for a run?"), (m.ReplyTo.Id, m.ReplyTo.From, m.ReplyTo.Text, m.ReplyTo.By, m.ReplyTo.Snippet));
        Assert.Equal(new[] { ("ready", "Mira,Bob"), ("lol", "Zed") }, m.Reactions.Select(x => (x.Id, string.Join(",", x.Users))));
        Assert.Null(m.Kind); Assert.Null(m.Lfg); Assert.Null(m.Event);

        var lfg = ChatMessage.Read(Written(new ChatMessage { Channel = "lfg", Id = "c1", At = 5, From = "Elan", FromId = 7, Text = "[LFG]", Kind = "lfg", Lfg = Card() }.Write));
        Assert.Equal(("lfg", "The Deadmines", 3, "Elan"), (lfg.Kind, lfg.Lfg.Title, lfg.Lfg.Dps, lfg.Lfg.Signups[0].Name));

        var ev = ChatMessage.Read(Written(new ChatMessage
        {
            Channel = "general", Id = "e1", At = 5, From = "Elan", FromId = 7, Text = "[Event] Raid", Kind = "event",
            Event = new CalCard { Id = "ev1", Title = "Raid", Start = 10, End = 20, By = "Elan", Status = "open", Weekly = true },
        }.Write));
        Assert.Equal(("event", "ev1", "Raid", 10L, 20L, true), (ev.Kind, ev.Event.Id, ev.Event.Title, ev.Event.Start, ev.Event.End, ev.Event.Weekly));

        // a message whose author left the Lodge (2.22): no fromId, the former-member name - and a bare one with every optional part empty
        var gone = ChatMessage.Read(Written(new ChatMessage { Channel = "general", Id = "g1", At = 1, From = "(former member)", Text = "old news" }.Write));
        Assert.Equal(((int?)null, "(former member)", false, 0), (gone.FromId, gone.From, gone.Edited, gone.Reactions.Count));
        Assert.Null(gone.File); Assert.Null(gone.ReplyTo);
    }

    [Fact]
    public void Server_messages_round_trip()
    {
        var e = EditedMsg.Read(Written(new EditedMsg { Channel = "general", Id = "m1", Text = "now longer" }.Write));
        Assert.Equal(("general", "m1", "now longer", false), (e.Channel, e.Id, e.Text, e.Card));
        Assert.True(EditedMsg.Read(Written(new EditedMsg { Card = true }.Write)).Card);
        var d = DeletedMsg.Read(Written(new DeletedMsg { Channel = "general", Id = "m2" }.Write));
        Assert.Equal(("general", "m2"), (d.Channel, d.Id));
        var r = ReactionMsg.Read(Written(new ReactionMsg { Channel = "general", Id = "m3", Reaction = "love", Emoji = "❤️", Users = { "Elan", "Mira" }, By = "Mira", On = true }.Write));
        Assert.Equal(("general", "m3", "love", "❤️", "Elan,Mira", "Mira", true), (r.Channel, r.Id, r.Reaction, r.Emoji, string.Join(",", r.Users), r.By, r.On));
        var p = PinnedMsg.Read(Written(new PinnedMsg
        {
            Channel = "general",
            Pin = new ChatPin { Id = "m4", Text = "rules", By = "Elan", At = 1, PinnedBy = "Bob", PinnedAt = 2, File = new ChatFile { Id = "f1", Name = "rules.txt", Size = 3, Mime = "text/plain" } },
        }.Write));
        Assert.Equal(("general", "m4", "rules", "Elan", 1L, "Bob", 2L, "rules.txt"), (p.Channel, p.Pin.Id, p.Pin.Text, p.Pin.By, p.Pin.At, p.Pin.PinnedBy, p.Pin.PinnedAt, p.Pin.File.Name));
        var u = UnpinnedMsg.Read(Written(new UnpinnedMsg { Channel = "general", Id = "m4" }.Write));
        Assert.Equal(("general", "m4"), (u.Channel, u.Id));
    }

    [Fact]
    public void Each_message_writes_its_own_type()
    {
        Assert.Equal("msg", Written(new ChatSendMsg().Write).Str("t"));
        Assert.Equal("msg", Written(new ChatMessage().Write).Str("t"));
        Assert.Equal("edit", Written(new EditMsg().Write).Str("t"));
        Assert.Equal("edited", Written(new EditedMsg().Write).Str("t"));
        Assert.Equal("delete", Written(new DeleteMsg().Write).Str("t"));
        Assert.Equal("deleted", Written(new DeletedMsg().Write).Str("t"));
        Assert.Equal("react", Written(new ReactMsg().Write).Str("t"));
        Assert.Equal("react", Written(new ReactionMsg().Write).Str("t"));
        Assert.Equal("pin", Written(new PinMsg().Write).Str("t"));
        Assert.Equal("pin", Written(new PinnedMsg().Write).Str("t"));
        Assert.Equal("unpin", Written(new UnpinMsg().Write).Str("t"));
        Assert.Equal("unpin", Written(new UnpinnedMsg().Write).Str("t"));
    }

    [Fact]
    public void Reading_is_lenient_like_before()
    {
        // wrong types are "missing": the Outpost's old m.Str / m.Int / m.Child / m.List reads
        var m = ChatMessage.Read(new TestFields(new Dictionary<string, object>
        {
            ["channel"] = 5, ["id"] = "x", ["at"] = "soon", ["from"] = null, ["fromId"] = "7", ["text"] = true, ["edited"] = "yes",
            ["file"] = "f.png", ["replyTo"] = "m0", ["lfg"] = new List<object>(), ["event"] = 3,
            ["reactions"] = new Dictionary<string, object> { ["ready"] = new List<object> { "Mira", 3, null, "Bob" }, ["lol"] = "Zed" },
        }));
        Assert.Equal(((string)null, "x", 0L, (string)null, (string)null, false), (m.Channel, m.Id, m.At, m.From, m.Text, m.Edited));
        Assert.Equal(0, m.FromId);                                            // present but not a number: 0 (the old m.Int)
        Assert.Null(m.File); Assert.Null(m.ReplyTo); Assert.Null(m.Lfg); Assert.Null(m.Event);
        // a reaction keeps only its string entries; one that is no list has nobody (the old "as List<object> ?? empty")
        Assert.Equal(new[] { ("ready", "Mira,Bob"), ("lol", "") }, m.Reactions.Select(x => (x.Id, string.Join(",", x.Users))));

        // the send: a file without its id, an lfg that is no object
        var s = ChatSendMsg.Read(new TestFields(new Dictionary<string, object> { ["file"] = new Dictionary<string, object> { ["id"] = 3 }, ["lfg"] = "card", ["replyTo"] = 9 }));
        Assert.Equal(((string)null, (string)null, (LfgRequest)null, (object)null), (s.FileId, s.ReplyTo, s.Lfg, s.LfgRaw));

        // the reaction broadcast: users that are no strings are skipped; a pin frame without its pin
        var r = ReactionMsg.Read(new TestFields(new Dictionary<string, object> { ["users"] = new List<object> { "Elan", 1, null }, ["on"] = "true" }));
        Assert.Equal(("Elan", false), (string.Join(",", r.Users), r.On));
        Assert.Null(PinnedMsg.Read(new TestFields(new Dictionary<string, object> { ["pin"] = "m1" })).Pin);
        Assert.False(EditedMsg.Read(new TestFields(new Dictionary<string, object> { ["card"] = 1 })).Card);
    }

    // ---- golden: what the old code sent

    [Fact]
    public void Golden_client_messages_match_the_old_Outpost_dictionaries()
    {
        // SendMessage: plain, a reply with a file, no channel selected (null), an empty text
        SameJson(new Dictionary<string, object> { ["t"] = "msg", ["channel"] = "general", ["text"] = "hi 👋" },
            Written(new ChatSendMsg { Channel = "general", Text = "hi 👋" }.Write).D);
        var full = new Dictionary<string, object> { ["t"] = "msg", ["channel"] = "general", ["text"] = "see this", ["replyTo"] = "m1", ["file"] = new Dictionary<string, object> { ["id"] = "f1" } };
        var w = Written(new ChatSendMsg { Channel = "general", Text = "see this", ReplyTo = "m1", FileId = "f1" }.Write);
        SameJson(full, w.D);
        Assert.Equal(full.Keys, w.D.Keys);
        SameJson(new Dictionary<string, object> { ["t"] = "msg", ["channel"] = null, ["text"] = "" }, Written(new ChatSendMsg { Text = "" }.Write).D);
        var longText = new string('a', 1999) + "😀";                       // at the server's 2000 cut, an emoji straddling it
        SameJson(new Dictionary<string, object> { ["t"] = "msg", ["channel"] = "general", ["text"] = longText }, Written(new ChatSendMsg { Channel = "general", Text = longText }.Write).D);

        // PostLfg: later (start sent) and now (no start); counts as numbers
        var lfg = new Dictionary<string, object>
        {
            ["t"] = "msg", ["channel"] = "lfg", ["kind"] = "lfg", ["text"] = "SFK",
            ["lfg"] = new Dictionary<string, object>
            {
                ["title"] = "SFK", ["now"] = false, ["note"] = "",
                ["slots"] = new Dictionary<string, object> { ["tank"] = 1.0, ["healer"] = 1.0, ["dps"] = 3.0 }, ["start"] = 1760900400000.0,
            },
        };
        var lw = Written(new ChatSendMsg { Channel = "lfg", Kind = "lfg", Text = "SFK", Lfg = new LfgRequest { Title = "SFK", Note = "", Tank = 1, Healer = 1, Dps = 3, Start = 1760900400000 } }.Write);
        SameJson(lfg, lw.D);
        Assert.Equal(lfg.Keys, lw.D.Keys);
        Assert.Equal(new[] { "title", "now", "note", "slots", "start" }, ((Dictionary<string, object>)lw.D["lfg"]).Keys);
        var now = (Dictionary<string, object>)lfg["lfg"]; now.Remove("start"); now["now"] = true;
        SameJson(lfg, Written(new ChatSendMsg { Channel = "lfg", Kind = "lfg", Text = "SFK", Lfg = new LfgRequest { Title = "SFK", Now = true, Note = "", Tank = 1, Healer = 1, Dps = 3 } }.Write).D);

        SameJson(new Dictionary<string, object> { ["t"] = "edit", ["id"] = "m1", ["text"] = "fixed" }, Written(new EditMsg { Id = "m1", Text = "fixed" }.Write).D);
        SameJson(new Dictionary<string, object> { ["t"] = "delete", ["id"] = "m1" }, Written(new DeleteMsg { Id = "m1" }.Write).D);
        SameJson(new Dictionary<string, object> { ["t"] = "react", ["id"] = "m1", ["emoji"] = "ready" }, Written(new ReactMsg { Id = "m1", Emoji = "ready" }.Write).D);
        SameJson(new Dictionary<string, object> { ["t"] = "pin", ["id"] = "m1" }, Written(new PinMsg { Id = "m1" }.Write).D);
        SameJson(new Dictionary<string, object> { ["t"] = "unpin", ["id"] = "m1", ["channel"] = "general" }, Written(new UnpinMsg { Id = "m1", Channel = "general" }.Write).D);
        Assert.Equal(new[] { "t", "id", "channel" }, Written(new UnpinMsg().Write).D.Keys);
    }

    [Fact]
    public void Golden_server_messages_match_the_old_JsonObjects()
    {
        // Chat.Message: a plain message, a reply with an attachment, a sign-up card
        var plain = new JsonObject { ["t"] = "msg", ["channel"] = "general", ["id"] = "a1b2c3d4e5f6", ["at"] = 1760900000123, ["from"] = "Elan", ["fromId"] = 7, ["text"] = "hi 🙂" };
        var pw = Written(new ChatMessage { Channel = "general", Id = "a1b2c3d4e5f6", At = 1760900000123, From = "Elan", FromId = 7, Text = "hi 🙂" }.Write);
        SameJson(plain, pw.D);
        Assert.Equal(plain.Select(kv => kv.Key), pw.D.Keys);

        var reply = new JsonObject
        {
            ["t"] = "msg", ["channel"] = "general", ["id"] = "b1", ["at"] = 2, ["from"] = "Bob", ["fromId"] = 8, ["text"] = "here",
            ["file"] = new JsonObject { ["id"] = "f9", ["name"] = "map.png", ["size"] = 48213, ["mime"] = "image/png" },
            ["replyTo"] = new JsonObject { ["id"] = "a1", ["from"] = "Elan", ["text"] = "where?", ["by"] = "Elan", ["snippet"] = "where?" },
        };
        var rw = Written(new ChatMessage
        {
            Channel = "general", Id = "b1", At = 2, From = "Bob", FromId = 8, Text = "here",
            File = new ChatFile { Id = "f9", Name = "map.png", Size = 48213, Mime = "image/png" },
            ReplyTo = new ChatReply { Id = "a1", From = "Elan", Text = "where?", By = "Elan", Snippet = "where?" },
        }.Write);
        SameJson(reply, rw.D);
        Assert.Equal(reply.Select(kv => kv.Key), rw.D.Keys);
        Assert.Equal(new[] { "id", "from", "text", "by", "snippet" }, ((Dictionary<string, object>)rw.D["replyTo"]).Keys);
        // a file-only message (empty text) with a null mime, as an upload without a type was stored
        SameJson(new JsonObject { ["t"] = "msg", ["channel"] = "general", ["id"] = "b2", ["at"] = 2, ["from"] = "Bob", ["fromId"] = 8, ["text"] = "",
                ["file"] = new JsonObject { ["id"] = "f1", ["name"] = "x.bin", ["size"] = 0, ["mime"] = null } },
            Written(new ChatMessage { Channel = "general", Id = "b2", At = 2, From = "Bob", FromId = 8, Text = "", File = new ChatFile { Id = "f1", Name = "x.bin" } }.Write).D);

        var card = new JsonObject { ["t"] = "msg", ["channel"] = "lfg", ["id"] = "c1", ["at"] = 3, ["from"] = "Elan", ["fromId"] = 7, ["text"] = "[Looking for group] The Deadmines", ["kind"] = "lfg", ["lfg"] = OldCard() };
        var cw = Written(new ChatMessage { Channel = "lfg", Id = "c1", At = 3, From = "Elan", FromId = 7, Text = "[Looking for group] The Deadmines", Kind = "lfg", Lfg = Card() }.Write);
        SameJson(card, cw.D);
        Assert.Equal(card.Select(kv => kv.Key), cw.D.Keys);

        // ChatCalendar.PostEventCard
        var ev = new JsonObject
        {
            ["t"] = "msg", ["channel"] = "general", ["id"] = "e1", ["at"] = 4, ["from"] = "Elan", ["fromId"] = 7, ["text"] = "[Event] Raid", ["kind"] = "event",
            ["event"] = new JsonObject { ["id"] = "ev1", ["title"] = "Raid", ["start"] = 10, ["end"] = 20, ["by"] = "Elan", ["status"] = "open", ["weekly"] = false },
        };
        var ew = Written(new ChatMessage
        {
            Channel = "general", Id = "e1", At = 4, From = "Elan", FromId = 7, Text = "[Event] Raid", Kind = "event",
            Event = new CalCard { Id = "ev1", Title = "Raid", Start = 10, End = 20, By = "Elan", Status = "open" },
        }.Write);
        SameJson(ev, ew.D);
        Assert.Equal(ev.Select(kv => kv.Key), ew.D.Keys);

        // a stored message after a moderator's edit and reactions, and one whose author left (no fromId) - as history carries them
        var stored = new JsonObject
        {
            ["t"] = "msg", ["channel"] = "general", ["id"] = "a1b2c3d4e5f6", ["at"] = 1760900000123, ["from"] = "Elan", ["fromId"] = 7, ["text"] = "Deadmines at 8? 🗡️ @Mira",
            ["file"] = new JsonObject { ["id"] = "f9", ["name"] = "map.png", ["size"] = 48213, ["mime"] = "image/png" },
            ["replyTo"] = new JsonObject { ["id"] = "m0", ["from"] = "Mira", ["text"] = "who's up for a run?", ["by"] = "Mira", ["snippet"] = "who's up for a run?" },
            ["edited"] = true,
            ["reactions"] = new JsonObject { ["ready"] = new JsonArray("Mira", "Bob"), ["lol"] = new JsonArray("Zed") },
        };
        SameJson(stored, Written(Full().Write).D);
        SameJson(new JsonObject { ["t"] = "msg", ["channel"] = "general", ["id"] = "g1", ["at"] = 1, ["from"] = "(former member)", ["text"] = "old" },
            Written(new ChatMessage { Channel = "general", Id = "g1", At = 1, From = "(former member)", Text = "old" }.Write).D);
        Assert.Equal(new[] { "t", "channel", "id", "at", "from", "text" }, Written(new ChatMessage().Write).D.Keys);

        // Edit / RefreshEventCard / Delete / React / Pin / Unpin broadcasts
        SameJson(new JsonObject { ["t"] = "edited", ["channel"] = "general", ["id"] = "m1", ["text"] = "fixed" }, Written(new EditedMsg { Channel = "general", Id = "m1", Text = "fixed" }.Write).D);
        SameJson(new JsonObject { ["t"] = "edited", ["channel"] = "general", ["id"] = "e1", ["text"] = "[Event] Raid (full)", ["card"] = true },
            Written(new EditedMsg { Channel = "general", Id = "e1", Text = "[Event] Raid (full)", Card = true }.Write).D);
        SameJson(new JsonObject { ["t"] = "deleted", ["channel"] = "general", ["id"] = "m1" }, Written(new DeletedMsg { Channel = "general", Id = "m1" }.Write).D);
        var react = new JsonObject { ["t"] = "react", ["channel"] = "general", ["id"] = "m1", ["reaction"] = "ready", ["emoji"] = "✅", ["users"] = new JsonArray("Elan", "Mira"), ["by"] = "Mira", ["on"] = true };
        var rx = Written(new ReactionMsg { Channel = "general", Id = "m1", Reaction = "ready", Emoji = "✅", Users = { "Elan", "Mira" }, By = "Mira", On = true }.Write);
        SameJson(react, rx.D);
        Assert.Equal(react.Select(kv => kv.Key), rx.D.Keys);
        SameJson(new JsonObject { ["t"] = "react", ["channel"] = "general", ["id"] = "m1", ["reaction"] = "ready", ["emoji"] = "✅", ["users"] = new JsonArray(), ["by"] = "Mira", ["on"] = false },
            Written(new ReactionMsg { Channel = "general", Id = "m1", Reaction = "ready", Emoji = "✅", By = "Mira", On = false }.Write).D);
        var pin = new JsonObject
        {
            ["t"] = "pin", ["channel"] = "general",
            ["pin"] = new JsonObject { ["id"] = "m1", ["text"] = "rules", ["by"] = "Elan", ["at"] = 1, ["pinnedBy"] = "Bob", ["pinnedAt"] = 2, ["file"] = new JsonObject { ["id"] = "f1", ["name"] = "r.txt", ["size"] = 3, ["mime"] = "text/plain" } },
        };
        var pnw = Written(new PinnedMsg { Channel = "general", Pin = new ChatPin { Id = "m1", Text = "rules", By = "Elan", At = 1, PinnedBy = "Bob", PinnedAt = 2, File = new ChatFile { Id = "f1", Name = "r.txt", Size = 3, Mime = "text/plain" } } }.Write);
        SameJson(pin, pnw.D);
        Assert.Equal(new[] { "id", "text", "by", "at", "pinnedBy", "pinnedAt", "file" }, ((Dictionary<string, object>)pnw.D["pin"]).Keys);
        SameJson(new JsonObject { ["t"] = "unpin", ["channel"] = "general", ["id"] = "m1" }, Written(new UnpinnedMsg { Channel = "general", Id = "m1" }.Write).D);
        Assert.Equal(new[] { "t", "channel", "id" }, Written(new UnpinnedMsg().Write).D.Keys);
    }
}
