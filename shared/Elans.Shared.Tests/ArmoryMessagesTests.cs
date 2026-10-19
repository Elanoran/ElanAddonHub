using System.Text.Json.Nodes;
using Elans.Shared;

namespace Elans.Shared.Tests;

// The typed Armory messages: every message survives Write -> Read, and writes exactly the JSON the old hand-built dictionary / JsonObject
// sent (golden values copied from src/Lodge/LodgeSession.Armory.cs, LodgeSession.Reps.cs, server/Lodge.Server/ArmoryModule.cs, ArmoryStore.cs
// and Armory.cs at c712015) - the wire must not change. The character sheet is a free-form child passed through as it is.
public class ArmoryMessagesTests
{
    static TestFields Written(Action<ILodgeWriter> write) => TestFields.Written(write);
    static void SameJson(object expected, object actual) => TestFields.SameJson(expected, actual);

    static Dictionary<string, object> Sheet() => new()
    {
        ["name"] = "Aria", ["class"] = "HUNTER", ["level"] = 60L, ["at"] = 1_790_000_000L,
        ["gear"] = new Dictionary<string, object> { ["ilvl"] = 41.5, ["slots"] = new Dictionary<string, object> { ["1"] = new Dictionary<string, object> { ["i"] = 3000L } } },
    };

    static ArmorySummary Line(bool mine) => new()
    {
        Name = "Aria", Realm = "Forever", Class = "HUNTER", ClassText = "Hunter", Race = "Night Elf", RaceFile = "NightElf", Sex = 3, Faction = "Alliance", Guild = "Friends",
        Level = 60, Ilvl = 41.5, At = 1_790_000_000, Professions = { new ArmoryProfession { Name = "Alchemy", Skill = 150, Max = 300 } },
        Owner = "mia", By = "Mi", Key = "Forever-Aria", Up = 1_790_000_123_456, Mine = mine,
    };

    // ---- round trips

    [Fact]
    public void Client_messages_round_trip()
    {
        var put = ArmoryPutMsg.Read(Written(new ArmoryPutMsg { Key = "Forever-Aria", Char = Sheet() }.Write));
        Assert.Equal("Forever-Aria", put.Key);
        Assert.Equal("HUNTER", ((Dictionary<string, object>)put.Char)["class"]);

        var d1 = ArmoryDeleteMsg.Read(Written(new ArmoryDeleteMsg { Key = "Forever-Aria" }.Write));
        Assert.Equal(("Forever-Aria", false), (d1.Key, d1.All));
        var d2 = ArmoryDeleteMsg.Read(Written(new ArmoryDeleteMsg { All = true }.Write));
        Assert.Equal(((string)null, true), (d2.Key, d2.All));

        var g = ArmoryGetMsg.Read(Written(new ArmoryGetMsg { Owner = "mia", Key = "Forever-Aria" }.Write));
        Assert.Equal(("mia", "Forever-Aria"), (g.Owner, g.Key));
        Assert.Equal(new long[] { 100, 101 }, ArmoryCraftersGetMsg.Read(Written(new ArmoryCraftersGetMsg { Spells = { 100, 101 } }.Write)).Spells);
        Assert.Null(ArmoryCraftersGetMsg.Read(Written(new ArmoryCraftersGetMsg { Spells = { 100 } }.Write)).Spell);
        Assert.Equal(72, ArmoryStandingGetMsg.Read(Written(new ArmoryStandingGetMsg { Faction = 72 }.Write)).Faction);
        Assert.NotNull(ArmoryListMsg.Read(Written(new ArmoryListMsg().Write)));
    }

    [Fact]
    public void Server_messages_round_trip()
    {
        var s = ArmorySavedMsg.Read(Written(new ArmorySavedMsg { Ok = true, Stored = true, Key = "Forever-Aria" }.Write));
        Assert.Equal((true, true, "Forever-Aria", (string)null), (s.Ok, s.Stored, s.Key, s.Why));
        var d = ArmoryDeletedMsg.Read(Written(new ArmoryDeletedMsg { Ok = true, Removed = 2 }.Write));
        Assert.Equal((true, (int?)2, (string)null, (string)null, false), (d.Ok, d.Removed, d.Key, d.Why, d.All));
        var r = ArmoryDeletedMsg.Read(Written(new ArmoryDeletedMsg { Why = "guest" }.Write));
        Assert.Equal((false, (int?)null, "guest"), (r.Ok, r.Removed, r.Why));

        var line = ArmoryCharsMsg.Read(Written(new ArmoryCharsMsg { Chars = { Line(true) } }.Write)).Chars.Single();
        Assert.Equal(("Aria", "Forever", "HUNTER", "Hunter", "Night Elf", "NightElf", (int?)3, "Alliance", "Friends", (int?)60, (double?)41.5, (long?)1_790_000_000),
            (line.Name, line.Realm, line.Class, line.ClassText, line.Race, line.RaceFile, line.Sex, line.Faction, line.Guild, line.Level, line.Ilvl, line.At));
        Assert.Equal(("mia", "Mi", "Forever-Aria", 1_790_000_123_456L, true), (line.Owner, line.By, line.Key, line.Up, line.Mine));
        Assert.Equal(("Alchemy", 150, 300), (line.Professions[0].Name, line.Professions[0].Skill, line.Professions[0].Max));

        var ch = ArmoryCharMsg.Read(Written(new ArmoryCharMsg { Owner = "mia", By = "Mi", Key = "Forever-Aria", Char = Sheet() }.Write));
        Assert.Equal(("mia", "Mi", "Forever-Aria", "Aria"), (ch.Owner, ch.By, ch.Key, ((Dictionary<string, object>)ch.Char)["name"]));

        var cr = ArmoryCraftersMsg.Read(Written(new ArmoryCraftersMsg
        {
            Spells = { 100, 101 },
            Who = { new ArmoryCrafter { Spell = 100, Profession = "Alchemy", Skill = 150, Max = 300, Name = "Aria", Class = "HUNTER", Level = 60, Partial = false, Owner = "mia", By = "Mi", Key = "Forever-Aria", Mine = true } },
        }.Write));
        Assert.Equal(new long[] { 100, 101 }, cr.Spells);
        var c = cr.Who.Single();
        Assert.Equal((100, "Alchemy", 150, 300, "Aria", "HUNTER", 60, false, "mia", "Mi", "Forever-Aria", true), (c.Spell, c.Profession, c.Skill, c.Max, c.Name, c.Class, c.Level, c.Partial, c.Owner, c.By, c.Key, c.Mine));

        var st = ArmoryStandingMsg.Read(Written(new ArmoryStandingMsg
        {
            Faction = 72,
            Who = { new ArmoryStanding { Faction = 72, S = 6, V = 12500, Min = 9000, Max = 21000, Name = "Aria", Class = "HUNTER", Level = 60, At = 1_790_000_200, Owner = "mia", By = "Mi", Key = "Forever-Aria" } },
        }.Write));
        var w = st.Who.Single();
        Assert.Equal((72, 72, 6, 12500L, (long?)9000, (long?)21000, 1_790_000_200L, false), (st.Faction, w.Faction, w.S, w.V, w.Min, w.Max, w.At, w.Mine));
    }

    [Fact]
    public void Each_message_writes_its_own_type()
    {
        Assert.Equal("armory.put", Written(new ArmoryPutMsg().Write).Str("t"));
        Assert.Equal("armory.saved", Written(new ArmorySavedMsg().Write).Str("t"));
        Assert.Equal("armory.delete", Written(new ArmoryDeleteMsg().Write).Str("t"));
        Assert.Equal("armory.deleted", Written(new ArmoryDeletedMsg().Write).Str("t"));
        Assert.Equal("armory.list", Written(new ArmoryListMsg().Write).Str("t"));
        Assert.Equal("armory.chars", Written(new ArmoryCharsMsg().Write).Str("t"));
        Assert.Equal("armory.get", Written(new ArmoryGetMsg().Write).Str("t"));
        Assert.Equal("armory.char", Written(new ArmoryCharMsg().Write).Str("t"));
        Assert.Equal("armory.crafters", Written(new ArmoryCraftersGetMsg().Write).Str("t"));
        Assert.Equal("armory.crafters", Written(new ArmoryCraftersMsg().Write).Str("t"));
        Assert.Equal("armory.standing", Written(new ArmoryStandingGetMsg().Write).Str("t"));
        Assert.Equal("armory.standing", Written(new ArmoryStandingMsg().Write).Str("t"));
    }

    [Fact]
    public void Reading_is_lenient_like_before()
    {
        // a summary: a missing field stays null, ilvl is strict (a string is no number), a non-object profession is skipped; a non-object line is skipped
        var chars = ArmoryCharsMsg.Read(new TestFields(new Dictionary<string, object>
        {
            ["chars"] = new List<object>
            {
                "x",
                new Dictionary<string, object> { ["name"] = "Aria", ["ilvl"] = "41", ["level"] = "60", ["professions"] = new List<object> { 5, new Dictionary<string, object> { ["name"] = "Mining", ["skill"] = "x" } } },
            },
        }));
        var l = chars.Chars.Single();
        Assert.Equal(("Aria", (string)null, (double?)null, (int?)0, (int?)null, (long?)null, false), (l.Name, l.Owner, l.Ilvl, l.Level, l.Sex, l.At, l.Mine));
        Assert.Equal(("Mining", 0), (l.Professions.Single().Name, l.Professions.Single().Skill));
        // a sheet that is not an object reads as none; armory.deleted: a key sent reads as sent even when it isn't a string
        Assert.Null(ArmoryCharMsg.Read(new TestFields(new Dictionary<string, object> { ["char"] = "x" })).Char);
        Assert.Equal("", ArmoryDeletedMsg.Read(new TestFields(new Dictionary<string, object> { ["ok"] = true, ["key"] = 5L })).Key);
        Assert.True(ArmoryDeletedMsg.Read(new TestFields(new Dictionary<string, object> { ["ok"] = true, ["all"] = true })).All);
        // a standing without min / max
        var w = ArmoryStanding.Read(new TestFields(new Dictionary<string, object> { ["faction"] = 72L, ["s"] = 5L, ["v"] = -400L }));
        Assert.Equal(((long?)null, (long?)null, -400L), (w.Min, w.Max, w.V));
    }

    // ---- golden: what the old code sent

    [Fact]
    public void Golden_client_messages_match_the_old_Outpost_dictionaries()
    {
        SameJson(new Dictionary<string, object> { ["t"] = "armory.put", ["key"] = "Forever-Aria", ["char"] = Sheet() }, Written(new ArmoryPutMsg { Key = "Forever-Aria", Char = Sheet() }.Write).D);
        SameJson(new Dictionary<string, object> { ["t"] = "armory.delete", ["all"] = true }, Written(new ArmoryDeleteMsg { All = true }.Write).D);
        SameJson(new Dictionary<string, object> { ["t"] = "armory.delete", ["key"] = "Forever-Aria" }, Written(new ArmoryDeleteMsg { Key = "Forever-Aria" }.Write).D);
        SameJson(new Dictionary<string, object> { ["t"] = "armory.list" }, Written(new ArmoryListMsg().Write).D);
        SameJson(new Dictionary<string, object> { ["t"] = "armory.get", ["owner"] = "mia", ["key"] = "Forever-Aria" }, Written(new ArmoryGetMsg { Owner = "mia", Key = "Forever-Aria" }.Write).D);
        SameJson(new Dictionary<string, object> { ["t"] = "armory.crafters", ["spells"] = new List<object> { 100, 101 } }, Written(new ArmoryCraftersGetMsg { Spells = { 100, 101 } }.Write).D);
        SameJson(new Dictionary<string, object> { ["t"] = "armory.standing", ["faction"] = 72 }, Written(new ArmoryStandingGetMsg { Faction = 72 }.Write).D);
    }

    [Fact]
    public void Golden_server_messages_match_the_old_JsonObjects()
    {
        // ArmoryModule.Saved / Delete
        SameJson(new JsonObject { ["t"] = "armory.saved", ["ok"] = true, ["stored"] = true, ["key"] = "Forever-Aria" }, Written(new ArmorySavedMsg { Ok = true, Stored = true, Key = "Forever-Aria" }.Write).D);
        SameJson(new JsonObject { ["t"] = "armory.saved", ["ok"] = false, ["stored"] = false, ["why"] = "guest" }, Written(new ArmorySavedMsg { Why = "guest" }.Write).D);
        SameJson(new JsonObject { ["t"] = "armory.saved", ["ok"] = false, ["stored"] = false, ["key"] = "Forever-Aria", ["why"] = "rate" }, Written(new ArmorySavedMsg { Key = "Forever-Aria", Why = "rate" }.Write).D);
        SameJson(new JsonObject { ["t"] = "armory.deleted", ["ok"] = false, ["why"] = "invalid" }, Written(new ArmoryDeletedMsg { Why = "invalid" }.Write).D);
        SameJson(new JsonObject { ["t"] = "armory.deleted", ["ok"] = true, ["removed"] = 1, ["key"] = "Forever-Aria" }, Written(new ArmoryDeletedMsg { Ok = true, Removed = 1, Key = "Forever-Aria" }.Write).D);
        SameJson(new JsonObject { ["t"] = "armory.deleted", ["ok"] = true, ["removed"] = 0 }, Written(new ArmoryDeletedMsg { Ok = true, Removed = 0 }.Write).D);

        // ArmoryStore.List + Armory.Summary: mine only when true; a field the sheet lacks is left out
        SameJson(new JsonObject
        {
            ["t"] = "armory.chars",
            ["chars"] = new JsonArray(
                new JsonObject
                {
                    ["name"] = "Aria", ["realm"] = "Forever", ["class"] = "HUNTER", ["classText"] = "Hunter", ["race"] = "Night Elf", ["raceFile"] = "NightElf", ["sex"] = 3, ["faction"] = "Alliance",
                    ["guild"] = "Friends", ["level"] = 60, ["ilvl"] = 41.5, ["at"] = 1_790_000_000L, ["professions"] = new JsonArray(new JsonObject { ["name"] = "Alchemy", ["skill"] = 150, ["max"] = 300 }),
                    ["owner"] = "mia", ["by"] = "Mi", ["key"] = "Forever-Aria", ["up"] = 1_790_000_123_456L, ["mine"] = true,
                },
                new JsonObject
                {
                    ["name"] = "Bo", ["class"] = "MAGE", ["level"] = 42, ["at"] = 5L, ["professions"] = new JsonArray(),
                    ["owner"] = "bob", ["by"] = "bob", ["key"] = "Forever-Bo", ["up"] = 6L,
                }),
        }, Written(new ArmoryCharsMsg
        {
            Chars = { Line(true), new ArmorySummary { Name = "Bo", Class = "MAGE", Level = 42, At = 5, Owner = "bob", By = "bob", Key = "Forever-Bo", Up = 6 } },
        }.Write).D);

        // ArmoryModule.Get
        SameJson(new JsonObject { ["t"] = "armory.char", ["owner"] = "mia", ["by"] = "Mi", ["key"] = "Forever-Aria", ["char"] = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(Sheet())) },
            Written(new ArmoryCharMsg { Owner = "mia", By = "Mi", Key = "Forever-Aria", Char = Sheet() }.Write).D);

        // ArmoryModule.Crafters + Armory.CraftersOf
        SameJson(new JsonObject
        {
            ["t"] = "armory.crafters", ["spells"] = new JsonArray(100, 101),
            ["who"] = new JsonArray(new JsonObject
            {
                ["spell"] = 100, ["profession"] = "Alchemy", ["skill"] = 150, ["max"] = 300, ["name"] = "Aria", ["class"] = "HUNTER", ["level"] = 60, ["partial"] = false,
                ["owner"] = "mia", ["by"] = "Mi", ["key"] = "Forever-Aria", ["mine"] = true,
            }, new JsonObject
            {
                ["spell"] = 101, ["profession"] = "Mining", ["skill"] = 0, ["max"] = 0, ["name"] = "Bo", ["class"] = "MAGE", ["level"] = 42, ["partial"] = true,
                ["owner"] = "bob", ["by"] = "bob", ["key"] = "Forever-Bo",
            }),
        }, Written(new ArmoryCraftersMsg
        {
            Spells = { 100, 101 },
            Who =
            {
                new ArmoryCrafter { Spell = 100, Profession = "Alchemy", Skill = 150, Max = 300, Name = "Aria", Class = "HUNTER", Level = 60, Partial = false, Owner = "mia", By = "Mi", Key = "Forever-Aria", Mine = true },
                new ArmoryCrafter { Spell = 101, Profession = "Mining", Name = "Bo", Class = "MAGE", Level = 42, Partial = true, Owner = "bob", By = "bob", Key = "Forever-Bo" },
            },
        }.Write).D);

        // ArmoryModule.Standing + Armory.StandingOf: min / max only when the reputation has them
        SameJson(new JsonObject
        {
            ["t"] = "armory.standing", ["faction"] = 72,
            ["who"] = new JsonArray(new JsonObject
            {
                ["faction"] = 72, ["s"] = 6, ["v"] = 12500, ["name"] = "Aria", ["class"] = "HUNTER", ["level"] = 60, ["at"] = 1_790_000_200L, ["min"] = 9000, ["max"] = 21000,
                ["owner"] = "mia", ["by"] = "Mi", ["key"] = "Forever-Aria", ["mine"] = true,
            }, new JsonObject
            {
                ["faction"] = 72, ["s"] = 5, ["v"] = -400, ["name"] = "Bo", ["class"] = "MAGE", ["level"] = 42, ["at"] = 0, ["owner"] = "bob", ["by"] = "bob", ["key"] = "Forever-Bo",
            }),
        }, Written(new ArmoryStandingMsg
        {
            Faction = 72,
            Who =
            {
                new ArmoryStanding { Faction = 72, S = 6, V = 12500, Min = 9000, Max = 21000, Name = "Aria", Class = "HUNTER", Level = 60, At = 1_790_000_200, Owner = "mia", By = "Mi", Key = "Forever-Aria", Mine = true },
                new ArmoryStanding { Faction = 72, S = 5, V = -400, Name = "Bo", Class = "MAGE", Level = 42, Owner = "bob", By = "bob", Key = "Forever-Bo" },
            },
        }.Write).D);
    }
}
