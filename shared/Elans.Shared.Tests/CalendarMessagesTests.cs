using System.Text.Json.Nodes;
using Elans.Shared;

namespace Elans.Shared.Tests;

// The typed calendar messages: every message survives Write -> Read, and writes exactly the JSON the old hand-built dictionary /
// JsonObject sent (golden values copied from src/Lodge/CalendarModels.cs, src/Lodge/LodgeSession.Calendar.cs,
// server/Lodge.Server/Calendar.cs, CalendarModule.cs and ChatCalendar.cs at fe605bf) - the wire must not change.
public class CalendarMessagesTests
{
    static TestFields Written(Action<ILodgeWriter> write) => TestFields.Written(write);
    static void SameJson(object expected, object actual) => TestFields.SameJson(expected, actual);

    const long Start = 1792260000000L;

    static CalCreateMsg Create(int repeat = 0, string channel = "general", bool roles = true, int duration = 180) => new CalCreateMsg
    {
        Title = "Molten Core", Note = "bring fire resist", Start = Start, DurationMin = duration, Roles = roles, Tank = 2, Healer = 3, Dps = 5,
        Tz = "Europe/Berlin", Off = 120, RepeatWeeks = repeat, Channel = channel,
    };

    // an event as Cal.Build stores it, after a sign-up and with its chat card posted
    static JsonObject StoredEvent() => new JsonObject
    {
        ["id"] = "e1", ["title"] = "Molten Core", ["start"] = Start, ["end"] = Start + 180 * 60_000L,
        ["note"] = "bring fire resist", ["status"] = "open", ["by"] = "Elan", ["roles"] = true,
        ["slots"] = new JsonObject { ["tank"] = 2, ["healer"] = 3, ["dps"] = 5 },
        ["signups"] = new JsonArray(new JsonObject { ["n"] = "Pat", ["s"] = "going", ["r"] = "dps", ["c"] = "hi" }),
        ["tz"] = "Europe/Berlin", ["off"] = 120, ["created"] = Start - 86_400_000L,
        ["series"] = "s1", ["rep"] = new JsonObject { ["weeks"] = 3, ["open"] = false },
        ["msg"] = "m1", ["channel"] = "general",
    };

    static CalEvent SampleEvent() => new CalEvent
    {
        Id = "e1", Series = "s1", Title = "Molten Core", Start = Start, End = Start + 180 * 60_000L, Note = "bring fire resist", Status = "open", By = "Elan",
        Roles = true, Tank = 2, Healer = 3, Dps = 5, Signups = { new CalSignup { Name = "Pat", Status = "going", Role = "dps", Comment = "hi" } },
        Channel = "general", Msg = "m1", Tz = "Europe/Berlin", Off = 120, Rep = new CalRepeat { Weeks = 3, Open = false }, Created = Start - 86_400_000L,
    };

    // ---- round trips

    [Fact]
    public void Client_messages_round_trip()
    {
        var c = CalCreateMsg.Read(Written(Create(repeat: 4).Write));
        Assert.Equal(("Molten Core", "bring fire resist", Start, 180, true), (c.Title, c.Note, c.Start, c.DurationMin, c.Roles));
        Assert.Equal((2, 3, 5, "Europe/Berlin", 120, 4, "general"), (c.Tank, c.Healer, c.Dps, c.Tz, c.Off, c.RepeatWeeks, c.Channel));
        Assert.Equal(-1, CalCreateMsg.Read(Written(Create(repeat: -1).Write)).RepeatWeeks);
        Assert.Null(CalCreateMsg.Read(Written(Create(channel: null).Write)).Channel);
        var social = CalCreateMsg.Read(Written(Create(roles: false, duration: 0).Write));
        Assert.Equal((false, 0, 0, 0), (social.Roles, social.Tank, social.DurationMin, social.RepeatWeeks));

        var e = new CalEditMsg { Id = "e1", Scope = "following", Title = "MC", Start = Start, Tank = 1, Tz = "UTC" };
        var er = CalEditMsg.Read(Written(e.Write));
        Assert.Equal(("e1", "following", "MC", Start, 1, "UTC", true), (er.Id, er.Scope, er.Title, er.Start, er.Tank, er.Tz, er.Roles));

        var x = CalCancelMsg.Read(Written(new CalCancelMsg { Id = "e2", Scope = "this" }.Write));
        Assert.Equal(("e2", "this"), (x.Id, x.Scope));
        var s = CalSignupMsg.Read(Written(new CalSignupMsg { Id = "e3", Status = "going", Role = "tank", Comment = "late" }.Write));
        Assert.Equal(("e3", "going", "tank", "late"), (s.Id, s.Status, s.Role, s.Comment));
        Assert.Null(CalSignupMsg.Read(Written(new CalSignupMsg { Id = "e3", Status = "declined", Comment = "" }.Write)).Role);
        Assert.Equal("e4", CalLeaveMsg.Read(Written(new CalLeaveMsg { Id = "e4" }.Write)).Id);
        var g = CalRangeGetMsg.Read(Written(new CalRangeGetMsg { From = 1, To = 2 }.Write));
        Assert.Equal((1L, 2L), (g.From, g.To));
    }

    [Fact]
    public void Each_message_writes_its_own_type()
    {
        Assert.Equal("cal:create", Written(new CalCreateMsg().Write).Str("t"));
        Assert.Equal("cal:edit", Written(new CalEditMsg().Write).Str("t"));
        Assert.Equal("cal:cancel", Written(new CalCancelMsg().Write).Str("t"));
        Assert.Equal("cal:signup", Written(new CalSignupMsg().Write).Str("t"));
        Assert.Equal("cal:leave", Written(new CalLeaveMsg().Write).Str("t"));
        Assert.Equal("cal:range", Written(new CalRangeGetMsg().Write).Str("t"));
        Assert.Equal("cal:range", Written(new CalRangeMsg().Write).Str("t"));
        Assert.Equal("cal:events", Written(new CalEventsMsg().Write).Str("t"));
        Assert.Null(Written(new CalWelcome().Write).Str("t"));
    }

    [Fact]
    public void Events_round_trip_in_every_frame()
    {
        static void Same(CalEvent a, CalEvent b)
        {
            Assert.Equal((a.Id, a.Series, a.Title, a.Start, a.End, a.Note, a.Status, a.By), (b.Id, b.Series, b.Title, b.Start, b.End, b.Note, b.Status, b.By));
            Assert.Equal((a.Roles, a.Tank, a.Healer, a.Dps, a.Channel, a.Msg, a.Tz, a.Off, a.Created), (b.Roles, b.Tank, b.Healer, b.Dps, b.Channel, b.Msg, b.Tz, b.Off, b.Created));
            Assert.Equal((a.Rep?.Weeks, a.Rep?.Open), (b.Rep?.Weeks, b.Rep?.Open));
            Assert.Equal(a.Signups.Select(s => (s.Name, s.Status, s.Role, s.Comment)), b.Signups.Select(s => (s.Name, s.Status, s.Role, s.Comment)));
        }
        Same(SampleEvent(), CalEventsMsg.Read(Written(new CalEventsMsg { Events = { SampleEvent() } }.Write)).Events.Single());
        var r = CalRangeMsg.Read(Written(new CalRangeMsg { From = 5, To = 9, Events = { SampleEvent(), SampleEvent() } }.Write));
        Assert.Equal((5L, 9L, 2), (r.From, r.To, r.Events.Count));
        Same(SampleEvent(), r.Events[1]);
        var w = CalWelcome.Read(Written(new CalWelcome { Now = 7, Events = { SampleEvent() } }.Write));
        Assert.Equal(7L, w.Now);
        Same(SampleEvent(), w.Events.Single());

        var card = CalCard.Read(Written(new CalCard { Id = "e1", Title = "MC", Start = Start, End = 0, By = "Elan", Status = "open", Weekly = true }.Write));
        Assert.Equal(("e1", "MC", Start, 0L, "Elan", "open", true), (card.Id, card.Title, card.Start, card.End, card.By, card.Status, card.Weekly));
    }

    [Fact]
    public void Reading_is_lenient_like_before()
    {
        // "roles" missing = null (readers: true); present but not a bool = false, as the Outpost's !ContainsKey || Bool read it
        Assert.Null(CalEvent.Read(new TestFields(new Dictionary<string, object> { ["id"] = "e1" })).Roles);
        Assert.False(CalEvent.Read(new TestFields(new Dictionary<string, object> { ["roles"] = "yes" })).Roles);
        // wrong types are "missing", a non-object signup or event is skipped, a missing rep stays null
        var e = CalEventsMsg.Read(new TestFields(new Dictionary<string, object>
        {
            ["events"] = new List<object> { 5, new Dictionary<string, object> { ["id"] = 7, ["title"] = "x", ["slots"] = "x", ["signups"] = new List<object> { "junk", new Dictionary<string, object> { ["n"] = "A" } } } },
        })).Events.Single();
        Assert.Null(e.Id);
        Assert.Null(e.Rep);
        Assert.Equal(0, e.Tank);
        Assert.Single(e.Signups);
        Assert.Null(e.Signups[0].Status);
        Assert.Empty(CalEventsMsg.Read(new TestFields()).Events);
    }

    // ---- golden: what the old code sent

    [Fact]
    public void Golden_create_and_edit_match_the_old_CalendarForm_ToRequest()
    {
        // CalendarForm.ToRequest(LodgeMsg.CalCreate): numbers as double, slots only with roles, durationMin only when > 0, repeat, post | channel
        SameJson(new Dictionary<string, object>
        {
            ["t"] = "cal:create", ["title"] = "Molten Core", ["note"] = "bring fire resist", ["start"] = (double)Start,
            ["roles"] = true, ["tz"] = "Europe/Berlin", ["off"] = (double)120,
            ["slots"] = new Dictionary<string, object> { ["tank"] = (double)2, ["healer"] = (double)3, ["dps"] = (double)5 },
            ["durationMin"] = (double)180, ["repeat"] = new Dictionary<string, object> { ["weeks"] = (double)4 }, ["channel"] = "general",
        }, Written(Create(repeat: 4).Write).D);
        SameJson(new Dictionary<string, object>
        {
            ["t"] = "cal:create", ["title"] = "Molten Core", ["note"] = "bring fire resist", ["start"] = (double)Start,
            ["roles"] = false, ["tz"] = "Europe/Berlin", ["off"] = (double)120,
            ["repeat"] = new Dictionary<string, object> { ["open"] = true }, ["post"] = false,
        }, Written(Create(repeat: -1, channel: null, roles: false, duration: 0).Write).D);
        // a repeat of 1 (refused by the form) is not sent, as before
        Assert.False(Written(Create(repeat: 1).Write).D.ContainsKey("repeat"));

        // CalendarForm.ToRequest(LodgeMsg.CalEdit, id): + id and scope, never repeat / post / channel
        var edit = new CalEditMsg { Id = "e1", Scope = "this", Title = "Molten Core", Note = "", Start = Start, Roles = true, Tank = 2, Healer = 3, Dps = 5, Tz = "Europe/Berlin", Off = 60 };
        SameJson(new Dictionary<string, object>
        {
            ["t"] = "cal:edit", ["title"] = "Molten Core", ["note"] = "", ["start"] = (double)Start, ["roles"] = true, ["tz"] = "Europe/Berlin", ["off"] = (double)60,
            ["slots"] = new Dictionary<string, object> { ["tank"] = (double)2, ["healer"] = (double)3, ["dps"] = (double)5 },
            ["id"] = "e1", ["scope"] = "this",
        }, Written(edit.Write).D);
    }

    [Fact]
    public void Golden_requests_match_the_old_LodgeSession_dictionaries()
    {
        SameJson(new Dictionary<string, object> { ["t"] = "cal:cancel", ["id"] = "e1", ["scope"] = "following" }, Written(new CalCancelMsg { Id = "e1", Scope = "following" }.Write).D);
        SameJson(new Dictionary<string, object> { ["t"] = "cal:leave", ["id"] = "e1" }, Written(new CalLeaveMsg { Id = "e1" }.Write).D);
        SameJson(new Dictionary<string, object> { ["t"] = "cal:signup", ["id"] = "e1", ["status"] = "going", ["comment"] = "", ["role"] = "dps" },
            Written(new CalSignupMsg { Id = "e1", Status = "going", Comment = "", Role = "dps" }.Write).D);
        // Can't, or a social event: no role key at all
        SameJson(new Dictionary<string, object> { ["t"] = "cal:signup", ["id"] = "e1", ["status"] = "declined", ["comment"] = "sorry" },
            Written(new CalSignupMsg { Id = "e1", Status = "declined", Comment = "sorry" }.Write).D);
        SameJson(new Dictionary<string, object> { ["t"] = "cal:range", ["from"] = (double)Start, ["to"] = (double)(Start + 1) },
            Written(new CalRangeGetMsg { From = Start, To = Start + 1 }.Write).D);
    }

    [Fact]
    public void Golden_server_frames_match_the_old_JsonObjects()
    {
        // CalendarModule.Broadcast: { t, events: [stored event clones] }
        SameJson(new JsonObject { ["t"] = "cal:events", ["events"] = new JsonArray(StoredEvent()) }, Written(new CalEventsMsg { Events = { SampleEvent() } }.Write).D);
        // CalendarModule.Range: { t, from, to, events }
        SameJson(new JsonObject { ["t"] = "cal:range", ["from"] = 1L, ["to"] = 2L, ["events"] = new JsonArray(StoredEvent()) },
            Written(new CalRangeMsg { From = 1, To = 2, Events = { SampleEvent() } }.Write).D);
        // CalendarModule.WelcomeView: { now, events }, here empty
        SameJson(new JsonObject { ["now"] = 3L, ["events"] = new JsonArray() }, Written(new CalWelcome { Now = 3 }.Write).D);

        // an event fresh from Cal.Build: no series / rep / msg / channel, no sign-ups yet, a social event without an end
        var fresh = new JsonObject
        {
            ["id"] = "e2", ["title"] = "Fishing", ["start"] = Start, ["end"] = 0L, ["note"] = "", ["status"] = "open", ["by"] = "Pat", ["roles"] = false,
            ["slots"] = new JsonObject { ["tank"] = 0, ["healer"] = 0, ["dps"] = 0 }, ["signups"] = new JsonArray(), ["tz"] = "", ["off"] = 0, ["created"] = Start - 5,
        };
        var ev = new CalEvent { Id = "e2", Title = "Fishing", Start = Start, End = 0, Note = "", Status = "open", By = "Pat", Roles = false, Tz = "", Off = 0, Created = Start - 5 };
        SameJson(fresh, Written(ev.Write).D);

        // ChatModule.EventSnapshot: the card's snapshot
        SameJson(new JsonObject { ["id"] = "e1", ["title"] = "Molten Core", ["start"] = Start, ["end"] = 0L, ["by"] = "Elan", ["status"] = "open", ["weekly"] = true },
            Written(new CalCard { Id = "e1", Title = "Molten Core", Start = Start, End = 0, By = "Elan", Status = "open", Weekly = true }.Write).D);
    }

    [Fact]
    public void A_stored_event_reads_and_writes_back_unchanged()
    {
        // what the server does on every broadcast: the stored JSON -> CalEvent -> the frame
        var stored = StoredEvent();
        SameJson(stored, Written(CalEvent.Read(TestFields.From(stored)).Write).D);
        SameJson(stored, Written(SampleEvent().Write).D);
    }
}
