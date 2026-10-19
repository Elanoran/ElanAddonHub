using System.Text.Json.Nodes;
using Elans.Shared;

namespace Elans.Shared.Tests;

// The typed realm-status messages: every message survives Write -> Read, and writes exactly the JSON the old hand-built JsonObject sent
// (golden values copied from server/Lodge.Server/Realms.cs at c712015) - the wire must not change.
public class RealmMessagesTests
{
    static TestFields Written(Action<ILodgeWriter> write) => TestFields.Written(write);
    static void SameJson(object expected, object actual) => TestFields.SameJson(expected, actual);

    static RealmView Realm(int id, string name, string status, string population = null, bool? queue = null) => new RealmView
    {
        Id = id, Name = name, Slug = name.ToLowerInvariant(), Ruleset = "Normal", Status = status, Namespace = "dynamic-classic1x-eu", Population = population, HasQueue = queue,
    };

    // RealmEntry.ToJson at c712015
    static JsonObject OldRealm(int id, string name, string status, string population = null, bool? queue = null)
    {
        var o = new JsonObject { ["id"] = id, ["name"] = name, ["slug"] = name.ToLowerInvariant(), ["ruleset"] = "Normal", ["status"] = status, ["namespace"] = "dynamic-classic1x-eu" };
        if (!string.IsNullOrEmpty(population)) o["population"] = population;
        if (queue != null) o["hasQueue"] = queue.Value;
        return o;
    }

    [Fact]
    public void Messages_round_trip()
    {
        var s = RealmsStatusMsg.Read(Written(new RealmsStatusMsg
        {
            Available = true, Region = "eu", CheckedAt = 1_790_000_000, Realms = { Realm(1, "Forever", "UP", "Full", true), Realm(2, "Forever PvP", "DOWN") },
        }.Write));
        Assert.Equal((true, "eu", 1_790_000_000L, (string)null), (s.Available, s.Region, s.CheckedAt, s.Why));
        Assert.Equal(new[] { (1, "Forever", "forever", "Normal", "UP", "dynamic-classic1x-eu", "Full", (bool?)true), (2, "Forever PvP", "forever pvp", "Normal", "DOWN", "dynamic-classic1x-eu", (string)null, (bool?)null) },
            s.Realms.Select(r => (r.Id, r.Name, r.Slug, r.Ruleset, r.Status, r.Namespace, r.Population, r.HasQueue)));
        var none = RealmsStatusMsg.Read(Written(new RealmsStatusMsg { Available = false, Region = "eu", Why = "not-found" }.Write));
        Assert.Equal((false, "not-found", 0), (none.Available, none.Why, none.Realms.Count));

        var b = RealmsBackMsg.Read(Written(new RealmsBackMsg { Realms = { "Forever", "Forever PvP" }, CheckedAt = 5 }.Write));
        Assert.Equal(new[] { "Forever", "Forever PvP" }, b.Realms);
        Assert.Equal(5L, b.CheckedAt);
    }

    [Fact]
    public void Each_message_writes_its_own_type()
    {
        Assert.Equal("realms.status", Written(new RealmsStatusMsg().Write).Str("t"));
        Assert.Equal("realms.back", Written(new RealmsBackMsg().Write).Str("t"));
    }

    [Fact]
    public void Reading_is_lenient_like_before()
    {
        // a non-object realm or a non-string name is skipped; hasQueue is null only when missing, false when not a JSON true
        var s = RealmsStatusMsg.Read(new TestFields(new Dictionary<string, object>
        {
            ["available"] = 1, ["checkedAt"] = "x", ["realms"] = new List<object> { "Forever", new Dictionary<string, object> { ["id"] = "1", ["name"] = 3, ["hasQueue"] = "yes" } },
        }));
        Assert.Equal((false, 0L), (s.Available, s.CheckedAt));
        var r = s.Realms.Single();
        Assert.Equal((0, (string)null, (bool?)false), (r.Id, r.Name, r.HasQueue));
        Assert.Null(RealmView.Read(new TestFields()).HasQueue);
        Assert.Equal(new[] { "Forever" }, RealmsBackMsg.Read(new TestFields(new Dictionary<string, object> { ["realms"] = new List<object> { "Forever", 3, null } })).Realms);
    }

    [Fact]
    public void Golden_messages_match_the_old_JsonObjects()
    {
        // RealmsModule.StatusJson: realms only while available, why only when not
        SameJson(new JsonObject
        {
            ["t"] = "realms.status", ["available"] = true, ["region"] = "eu",
            ["realms"] = new JsonArray(OldRealm(1, "Forever", "UP", "Full", true), OldRealm(2, "Forever PvP", "DOWN")), ["checkedAt"] = 1_790_000_000L,
        }, Written(new RealmsStatusMsg { Available = true, Region = "eu", CheckedAt = 1_790_000_000, Realms = { Realm(1, "Forever", "UP", "Full", true), Realm(2, "Forever PvP", "DOWN") } }.Write).D);
        SameJson(new JsonObject { ["t"] = "realms.status", ["available"] = false, ["region"] = "eu", ["realms"] = new JsonArray(), ["checkedAt"] = 0L, ["why"] = "not-found" },
            Written(new RealmsStatusMsg { Available = false, Region = "eu", Why = "not-found" }.Write).D);
        SameJson(OldRealm(3, "X", "UNKNOWN", ""), Written(Realm(3, "X", "UNKNOWN", "").Write).D);      // an empty population is left out
        // the back-up broadcast
        SameJson(new JsonObject { ["t"] = "realms.back", ["realms"] = new JsonArray("Forever"), ["checkedAt"] = 9L }, Written(new RealmsBackMsg { Realms = { "Forever" }, CheckedAt = 9 }.Write).D);
        // key order as before
        Assert.Equal(new[] { "t", "available", "region", "realms", "checkedAt", "why" }, Written(new RealmsStatusMsg { Why = "x" }.Write).D.Keys);
        Assert.Equal(new[] { "id", "name", "slug", "ruleset", "status", "namespace", "population", "hasQueue" }, Written(Realm(1, "A", "UP", "Low", false).Write).D.Keys);
        Assert.Equal(new[] { "t", "realms", "checkedAt" }, Written(new RealmsBackMsg().Write).D.Keys);
    }
}
