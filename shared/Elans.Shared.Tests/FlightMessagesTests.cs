using System.Text.Json.Nodes;
using Elans.Shared;

namespace Elans.Shared.Tests;

// The typed flight-time and learned-route messages: every message survives Write -> Read, and writes exactly the JSON the old hand-built
// dictionary / JsonObject sent (golden values copied from src/Services/FlightShare.cs, src/Services/WorldMap/LearnedFlights.Lodge.cs,
// src/Lodge/LodgeSession.FlightShare.cs / .FlightRoutes.cs, server/Lodge.Server/FlightModule.cs, Flights.cs and FlightRoutes.cs at c712015)
// - the wire must not change. The numbers are strict: a value that is not a JSON number reads as null, so the readers on both sides can
// keep refusing it.
public class FlightMessagesTests
{
    static TestFields Written(Action<ILodgeWriter> write) => TestFields.Written(write);
    static void SameJson(object expected, object actual) => TestFields.SameJson(expected, actual);
    static KeyValuePair<string, T> KV<T>(string k, T v) => new(k, v);

    static FlightRoute Line() => new FlightRoute { M = 1, P = new List<double?> { 0, 0, 120.5, 1000, 300, null, 2000, 0, 118 }, T = 70.5, N = 2 };

    // ---- round trips

    [Fact]
    public void Client_messages_round_trip()
    {
        var share = FlightsShareMsg.Read(Written(new FlightsShareMsg
        {
            Legs = { KV("Horde|11>22", new FlightLeg { T = 61.6, N = 12 }), KV("Alliance|2>3", new FlightLeg { T = 120, N = 50 }) },
            Names = new() { KV("11", "Orgrimmar"), KV("22", "Crossroads") },
        }.Write));
        Assert.Equal(new[] { ("Horde|11>22", (double?)61.6, (double?)12), ("Alliance|2>3", (double?)120, (double?)50) }, share.Legs.Select(kv => (kv.Key, kv.Value.T, kv.Value.N)));
        Assert.Equal(new[] { ("11", "Orgrimmar"), ("22", "Crossroads") }, share.Names.Select(kv => (kv.Key, kv.Value)));
        Assert.Null(FlightsShareMsg.Read(Written(new FlightsShareMsg { Legs = { KV("Horde|1>2", new FlightLeg { T = 5, N = 1 }) } }.Write)).Names);

        var rs = FlightsRoutesShareMsg.Read(Written(new FlightsRoutesShareMsg { Routes = { KV("23>80", Line()), KV("23>81", new FlightRoute { M = 0, Game = true }) } }.Write));
        var r0 = rs.Routes[0].Value;
        Assert.Equal(("23>80", (double?)1, (double?)70.5, (double?)2, (double?)null, false), (rs.Routes[0].Key, r0.M, r0.T, r0.N, r0.K, r0.Game));
        Assert.Equal(Line().P, r0.P);
        var r1 = rs.Routes[1].Value;
        Assert.Equal(((double?)0, true, (List<double?>)null), (r1.M, r1.Game, r1.P));

        Assert.NotNull(FlightsGetMsg.Read(Written(new FlightsGetMsg().Write)));
        Assert.NotNull(FlightsRoutesGetMsg.Read(Written(new FlightsRoutesGetMsg().Write)));
    }

    [Fact]
    public void Server_messages_round_trip()
    {
        var a = FlightsSharedMsg.Read(Written(new FlightsSharedMsg { Ok = false, Accepted = 0, Rejected = 3, Why = "rate" }.Write));
        Assert.Equal((false, 0, 3, "rate"), (a.Ok, a.Accepted, a.Rejected, a.Why));
        var b = FlightsRoutesSharedMsg.Read(Written(new FlightsRoutesSharedMsg { Ok = true, Accepted = 4, Rejected = 1 }.Write));
        Assert.Equal((true, 4, 1, (string)null), (b.Ok, b.Accepted, b.Rejected, b.Why));

        var d = FlightsDataMsg.Read(Written(new FlightsDataMsg { At = 1_790_000_000, Legs = { KV("Horde|11>22", new FlightLeg { T = 30.6, N = 3 }) }, Names = { KV("11", "Orgrimmar") } }.Write));
        Assert.Equal(1_790_000_000, d.At);
        Assert.Equal((30.6, 3.0), (d.Legs.Single().Value.T.Value, d.Legs.Single().Value.N.Value));
        Assert.Equal("Orgrimmar", d.Names.Single().Value);

        var pub = Line(); pub.K = 2;
        var rd = FlightsRoutesDataMsg.Read(Written(new FlightsRoutesDataMsg { At = 7, Routes = { KV("23>80", pub) } }.Write));
        Assert.Equal((7L, "23>80", (double?)2), (rd.At, rd.Routes.Single().Key, rd.Routes.Single().Value.K));
        Assert.Equal(pub.P, rd.Routes.Single().Value.P);
    }

    [Fact]
    public void Each_message_writes_its_own_type()
    {
        Assert.Equal("flights.get", Written(new FlightsGetMsg().Write).Str("t"));
        Assert.Equal("flights.data", Written(new FlightsDataMsg().Write).Str("t"));
        Assert.Equal("flights.share", Written(new FlightsShareMsg().Write).Str("t"));
        Assert.Equal("flights.shared", Written(new FlightsSharedMsg().Write).Str("t"));
        Assert.Equal("flights.routes.get", Written(new FlightsRoutesGetMsg().Write).Str("t"));
        Assert.Equal("flights.routes.data", Written(new FlightsRoutesDataMsg().Write).Str("t"));
        Assert.Equal("flights.routes.share", Written(new FlightsRoutesShareMsg().Write).Str("t"));
        Assert.Equal("flights.routes.shared", Written(new FlightsRoutesSharedMsg().Write).Str("t"));
    }

    [Fact]
    public void Reading_is_strict_about_numbers_and_keeps_every_map_entry()
    {
        // a leg that is not an object stays in the map as null (the Outpost counts it as refused); "50" / true are no numbers; names keep only strings
        var d = FlightsDataMsg.Read(new TestFields(new Dictionary<string, object>
        {
            ["at"] = 5L,
            ["legs"] = new Dictionary<string, object>
            {
                ["Horde|1>2"] = new Dictionary<string, object> { ["t"] = 50.0, ["n"] = 2.5 },
                ["Horde|1>4"] = new Dictionary<string, object> { ["t"] = "50", ["n"] = true },
                ["Horde|1>6"] = 7L,
            },
            ["names"] = new Dictionary<string, object> { ["1"] = "A", ["2"] = 123L },
        }));
        Assert.Equal(new[] { "Horde|1>2", "Horde|1>4", "Horde|1>6" }, d.Legs.Select(kv => kv.Key));
        Assert.Equal(((double?)50, (double?)2.5), (d.Legs[0].Value.T, d.Legs[0].Value.N));
        Assert.Equal(((double?)null, (double?)null), (d.Legs[1].Value.T, d.Legs[1].Value.N));
        Assert.Null(d.Legs[2].Value);
        Assert.Equal(new[] { ("1", "A"), ("2", (string)null) }, d.Names.Select(kv => (kv.Key, kv.Value)));
        Assert.Empty(FlightsDataMsg.Read(new TestFields(new Dictionary<string, object> { ["legs"] = "x" })).Legs);

        // a route's points: null unless every entry is a number or null; a missing number is null, not 0
        FlightRoute R(object p, Dictionary<string, object> more = null)
        {
            var o = new Dictionary<string, object> { ["p"] = p };
            if (more != null) foreach (var kv in more) o[kv.Key] = kv.Value;
            return FlightRoute.Read(new TestFields(o));
        }
        Assert.Equal(new double?[] { 1, 2, null }, R(new List<object> { 1.0, 2L, null }).P);
        Assert.Null(R(new List<object> { 1.0, "2", null }).P);
        Assert.Null(R(new List<object> { 1.0, 2.0, true }).P);
        Assert.Null(R("1,2,3").P);
        var r = R(new List<object>(), new() { ["m"] = "0", ["k"] = 2L });
        Assert.Equal(((double?)null, (double?)null, (double?)null, (double?)2), (r.M, r.T, r.N, r.K));
        Assert.Empty(r.P);
        Assert.True(R(null, new() { ["g"] = 1L }).Game);
        Assert.False(R(null, new() { ["g"] = 2L }).Game);
    }

    // ---- golden: what the old code sent

    [Fact]
    public void Golden_client_messages_match_the_old_Outpost_dictionaries()
    {
        SameJson(new Dictionary<string, object> { ["t"] = "flights.get" }, Written(new FlightsGetMsg().Write).D);
        SameJson(new Dictionary<string, object> { ["t"] = "flights.routes.get" }, Written(new FlightsRoutesGetMsg().Write).D);

        // FlightShare.MakeBatch: legs { t, n }, names only when there are some
        SameJson(new Dictionary<string, object>
        {
            ["t"] = "flights.share",
            ["legs"] = new Dictionary<string, object> { ["Horde|11>22"] = new Dictionary<string, object> { ["t"] = 61.6, ["n"] = 12 }, ["Horde|5>6"] = new Dictionary<string, object> { ["t"] = 30.0, ["n"] = 1 } },
            ["names"] = new Dictionary<string, object> { ["11"] = "Orgrimmar", ["22"] = "Crossroads" },
        }, Written(new FlightsShareMsg
        {
            Legs = { KV("Horde|11>22", new FlightLeg { T = 61.6, N = 12 }), KV("Horde|5>6", new FlightLeg { T = 30, N = 1 }) },
            Names = new() { KV("11", "Orgrimmar"), KV("22", "Crossroads") },
        }.Write).D);
        SameJson(new Dictionary<string, object> { ["t"] = "flights.share", ["legs"] = new Dictionary<string, object> { ["Horde|5>6"] = new Dictionary<string, object> { ["t"] = 30.0, ["n"] = 1 } } },
            Written(new FlightsShareMsg { Legs = { KV("Horde|5>6", new FlightLeg { T = 30, N = 1 }) } }.Write).D);

        // LearnedFlights.Entry / ShareEntries / MakeBatch: { m, p, t, n } (z null where unknown) and { m, g: 1 }
        SameJson(new Dictionary<string, object>
        {
            ["t"] = "flights.routes.share",
            ["routes"] = new Dictionary<string, object>
            {
                ["23>80"] = new Dictionary<string, object> { ["m"] = 1, ["p"] = new List<object> { 0.0, 0.0, 120.5, 1000.0, 300.0, null, 2000.0, 0.0, 118.0 }, ["t"] = 70.5, ["n"] = 2 },
                ["23>81"] = new Dictionary<string, object> { ["m"] = 0, ["g"] = 1 },
                ["23>82"] = new Dictionary<string, object> { ["m"] = 1, ["p"] = new List<object> { 0.0, 0.0, null, 2000.0, 0.0, null }, ["t"] = 0.0, ["n"] = 1 },
            },
        }, Written(new FlightsRoutesShareMsg
        {
            Routes =
            {
                KV("23>80", Line()), KV("23>81", new FlightRoute { M = 0, Game = true }),
                KV("23>82", new FlightRoute { M = 1, P = new List<double?> { 0, 0, null, 2000, 0, null }, T = 0, N = 1 }),
            },
        }.Write).D);
    }

    [Fact]
    public void Golden_server_messages_match_the_old_JsonObjects()
    {
        // FlightModule.Reply / ReplyRoutes: why only when given
        SameJson(new JsonObject { ["t"] = "flights.shared", ["ok"] = true, ["accepted"] = 5, ["rejected"] = 1 }, Written(new FlightsSharedMsg { Ok = true, Accepted = 5, Rejected = 1 }.Write).D);
        SameJson(new JsonObject { ["t"] = "flights.shared", ["ok"] = false, ["accepted"] = 0, ["rejected"] = 0, ["why"] = "guest" }, Written(new FlightsSharedMsg { Why = "guest" }.Write).D);
        SameJson(new JsonObject { ["t"] = "flights.routes.shared", ["ok"] = false, ["accepted"] = 0, ["rejected"] = 0, ["why"] = "rate" }, Written(new FlightsRoutesSharedMsg { Why = "rate" }.Write).D);
        SameJson(new JsonObject { ["t"] = "flights.routes.shared", ["ok"] = true, ["accepted"] = 2, ["rejected"] = 0 }, Written(new FlightsRoutesSharedMsg { Ok = true, Accepted = 2 }.Write).D);

        // FlightStore.MergedJson
        SameJson(new JsonObject
        {
            ["t"] = "flights.data", ["at"] = 1_790_000_000L,
            ["legs"] = new JsonObject { ["Horde|11>22"] = new JsonObject { ["t"] = 30.6, ["n"] = 3 }, ["Horde|22>33"] = new JsonObject { ["t"] = 100.0, ["n"] = 50 } },
            ["names"] = new JsonObject { ["11"] = "Orgrimmar", ["22"] = "Crossroads" },
        }, Written(new FlightsDataMsg
        {
            At = 1_790_000_000,
            Legs = { KV("Horde|11>22", new FlightLeg { T = 30.6, N = 3 }), KV("Horde|22>33", new FlightLeg { T = 100, N = 50 }) },
            Names = { KV("11", "Orgrimmar"), KV("22", "Crossroads") },
        }.Write).D);
        SameJson(new JsonObject { ["t"] = "flights.data", ["at"] = 5L, ["legs"] = new JsonObject(), ["names"] = new JsonObject() }, Written(new FlightsDataMsg { At = 5 }.Write).D);

        // FlightRouteStore.MergedJson: { m, p (FlightRoutes.Flat), t, n, k }
        var pub = Line(); pub.K = 2;
        SameJson(new JsonObject
        {
            ["t"] = "flights.routes.data", ["at"] = 7L,
            ["routes"] = new JsonObject { ["23>80"] = new JsonObject { ["m"] = 1, ["p"] = new JsonArray(0.0, 0.0, 120.5, 1000.0, 300.0, null, 2000.0, 0.0, 118.0), ["t"] = 70.5, ["n"] = 2, ["k"] = 2 } },
        }, Written(new FlightsRoutesDataMsg { At = 7, Routes = { KV("23>80", pub) } }.Write).D);
    }
}
