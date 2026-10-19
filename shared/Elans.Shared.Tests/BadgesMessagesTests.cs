using System.Text.Json.Nodes;
using Elans.Shared;

namespace Elans.Shared.Tests;

// The typed badge and ladder messages: every message survives Write -> Read, and writes exactly the JSON the old hand-built dictionary /
// JsonObject sent (golden values copied from src/Lodge/LodgeSession.Badges.cs, server/Lodge.Server/BadgesModule.cs and Hub.cs at c712015)
// - the wire must not change.
public class BadgesMessagesTests
{
    static TestFields Written(Action<ILodgeWriter> write) => TestFields.Written(write);
    static void SameJson(object expected, object actual) => TestFields.SameJson(expected, actual);

    static BadgeView Short(string id, int tier, long at) => new BadgeView { Id = id, Family = id.Split('.')[0], Tier = tier, Name = "N " + id, At = at };

    // ---- round trips

    [Fact]
    public void Client_messages_round_trip()
    {
        Assert.True(BadgesHideMsg.Read(Written(new BadgesHideMsg { Hide = true }.Write)).Hide);
        Assert.Equal("Mia", BadgesMemberGetMsg.Read(Written(new BadgesMemberGetMsg { Name = "Mia" }.Write)).Name);
        Assert.Equal("2026-10", LadderGetMsg.Read(Written(new LadderGetMsg { Season = "2026-10" }.Write)).Season);
        Assert.False(AdminBadgesMsg.Read(Written(new AdminBadgesMsg { On = false }.Write)).On);
        Assert.True(AdminBadgesMsg.Read(Written(new AdminBadgesMsg { On = true }.Write)).On);
        Assert.NotNull(BadgesMineGetMsg.Read(Written(new BadgesMineGetMsg().Write)));
        Assert.NotNull(BadgesSeenMsg.Read(Written(new BadgesSeenMsg().Write)));
    }

    [Fact]
    public void Server_messages_round_trip()
    {
        var st = BadgesStateMsg.Read(Written(new BadgesStateMsg { Enabled = true, Locked = true }.Write));
        Assert.Equal((true, true), (st.Enabled, st.Locked));

        var mine = BadgesMineMsg.Read(Written(new BadgesMineMsg
        {
            You = "Mia", Hide = true, New = 2, Points = 1234, Rank = 3, Season = "2026-10", SeasonPoints = 56, SeasonRank = 1, Earned = 4, Total = 40,
            Badges =
            {
                new BadgeView { Id = "loot.bronze", Family = "loot", FamilyName = "Loot Ledger", Tier = 1, Hidden = false, Earned = true, Name = "Pocketful", Flavour = "f", How = "100 kills recorded", At = 1_790_000_000, Value = 100, Target = 100 },
                new BadgeView { Id = "nightowl", Family = "secret", FamilyName = "Secrets", Tier = 2, Hidden = true, Earned = false, Name = "???", Flavour = "", How = "???" },
            },
        }.Write));
        Assert.Equal((true, "Mia", true, 2, 1234L, 3, "2026-10", 56L, 1, 4, 40), (mine.Ok, mine.You, mine.Hide, mine.New, mine.Points, mine.Rank, mine.Season, mine.SeasonPoints, mine.SeasonRank, mine.Earned, mine.Total));
        var b0 = mine.Badges[0];
        Assert.Equal(("loot.bronze", "loot", "Loot Ledger", 1, (bool?)false, (bool?)true, "Pocketful", "f", "100 kills recorded", (long?)1_790_000_000, (long?)100, (long?)100),
            (b0.Id, b0.Family, b0.FamilyName, b0.Tier, b0.Hidden, b0.Earned, b0.Name, b0.Flavour, b0.How, b0.At, b0.Value, b0.Target));
        var b1 = mine.Badges[1];
        Assert.Equal(((bool?)true, (bool?)false, (long?)null, (long?)null, (long?)null), (b1.Hidden, b1.Earned, b1.At, b1.Value, b1.Target));

        var mem = BadgesMemberMsg.Read(Written(new BadgesMemberMsg { Name = "Zed", Badges = { Short("loot.gold", 3, 1_790_000_100) } }.Write));
        Assert.Equal((true, "Zed", 1), (mem.Ok, mem.Name, mem.Count));
        var e = mem.Badges.Single();
        Assert.Equal(("loot.gold", 3, (long?)1_790_000_100, (string)null, (bool?)null), (e.Id, e.Tier, e.At, e.FamilyName, e.Earned));

        var lad = LadderDataMsg.Read(Written(new LadderDataMsg
        {
            Season = "all", Current = "2026-10", Seasons = { "2026-10", "2026-09" },
            Rows = { new LadderRow { Rank = 1, Name = "Zed", Points = 900, Top = { Short("loot.gold", 3, 5) } }, new LadderRow { Rank = 2, Name = "Mia", Points = 800, You = true, Hidden = true } },
            You = new LadderYou { Rank = 2, Points = 800, Hidden = true },
        }.Write));
        Assert.Equal((true, "all", "2026-10"), (lad.Ok, lad.Season, lad.Current));
        Assert.Equal(new[] { "2026-10", "2026-09" }, lad.Seasons);
        Assert.Equal(new[] { (1, "Zed", 900L, false, false, 1), (2, "Mia", 800L, true, true, 0) }, lad.Rows.Select(r => (r.Rank, r.Name, r.Points, r.You, r.Hidden, r.Top.Count)));
        Assert.Equal((2, 800L, true), (lad.You.Rank, lad.You.Points, lad.You.Hidden));
        Assert.Null(LadderDataMsg.Read(Written(new LadderDataMsg { Season = "all", Current = "" }.Write)).You);
    }

    [Fact]
    public void Each_message_writes_its_own_type()
    {
        Assert.Equal("badges.mine", Written(new BadgesMineGetMsg().Write).Str("t"));
        Assert.Equal("badges.mine", Written(new BadgesMineMsg().Write).Str("t"));
        Assert.Equal("badges.seen", Written(new BadgesSeenMsg().Write).Str("t"));
        Assert.Equal("badges.hide", Written(new BadgesHideMsg().Write).Str("t"));
        Assert.Equal("badges.member", Written(new BadgesMemberGetMsg().Write).Str("t"));
        Assert.Equal("badges.member", Written(new BadgesMemberMsg().Write).Str("t"));
        Assert.Equal("badges.state", Written(new BadgesStateMsg().Write).Str("t"));
        Assert.Equal("ladder.get", Written(new LadderGetMsg().Write).Str("t"));
        Assert.Equal("ladder.data", Written(new LadderDataMsg().Write).Str("t"));
        Assert.Equal("admin.badges", Written(new AdminBadgesMsg().Write).Str("t"));
        Assert.False(Written(new BadgesStateMsg().WriteState).Has("t"));     // welcome.badges
    }

    [Fact]
    public void Reading_is_lenient_like_before()
    {
        // a badge without "earned" (the short form) is earned; a non-object badge or row is skipped; a non-string season is skipped
        var mine = BadgesMineMsg.Read(new TestFields(new Dictionary<string, object>
        {
            ["ok"] = true, ["points"] = "x",
            ["badges"] = new List<object> { 5, new Dictionary<string, object> { ["id"] = "loot.bronze", ["tier"] = "1", ["earned"] = "yes" } },
        }));
        Assert.Equal(0, mine.Points);
        var b = mine.Badges.Single();
        Assert.Equal(("loot.bronze", 0, (bool?)false, (string)null, (long?)null), (b.Id, b.Tier, b.Earned, b.FamilyName, b.At));
        Assert.Null(BadgeView.Read(new TestFields(new Dictionary<string, object> { ["id"] = "x" })).Earned);
        var lad = LadderDataMsg.Read(new TestFields(new Dictionary<string, object>
        {
            ["seasons"] = new List<object> { "2026-10", 7 }, ["rows"] = new List<object> { "x", new Dictionary<string, object> { ["rank"] = 1 } }, ["you"] = null,
        }));
        Assert.Equal(new[] { "2026-10" }, lad.Seasons);
        Assert.Single(lad.Rows);
        Assert.Null(lad.You);
        Assert.False(lad.Ok);
        // admin.badges: missing "on" means on
        Assert.True(AdminBadgesMsg.Read(new TestFields()).On);
    }

    // ---- golden: what the old code sent

    [Fact]
    public void Golden_client_messages_match_the_old_Outpost_dictionaries()
    {
        SameJson(new Dictionary<string, object> { ["t"] = "badges.mine" }, Written(new BadgesMineGetMsg().Write).D);
        SameJson(new Dictionary<string, object> { ["t"] = "badges.seen" }, Written(new BadgesSeenMsg().Write).D);
        SameJson(new Dictionary<string, object> { ["t"] = "badges.hide", ["hide"] = true }, Written(new BadgesHideMsg { Hide = true }.Write).D);
        SameJson(new Dictionary<string, object> { ["t"] = "ladder.get", ["season"] = "all" }, Written(new LadderGetMsg { Season = "all" }.Write).D);
        SameJson(new Dictionary<string, object> { ["t"] = "badges.member", ["name"] = "Mia" }, Written(new BadgesMemberGetMsg { Name = "Mia" }.Write).D);
        SameJson(new Dictionary<string, object> { ["t"] = "admin.badges", ["on"] = false }, Written(new AdminBadgesMsg { On = false }.Write).D);
    }

    [Fact]
    public void Golden_server_messages_match_the_old_JsonObjects()
    {
        // BadgesModule.StateJson and Hub.Welcome's "badges"
        SameJson(new JsonObject { ["t"] = "badges.state", ["enabled"] = true, ["locked"] = false }, Written(new BadgesStateMsg { Enabled = true }.Write).D);
        SameJson(new JsonObject { ["enabled"] = false, ["locked"] = true }, Written(new BadgesStateMsg { Locked = true }.WriteState).D);

        // MineJson: an earned badge with progress, a hidden one not earned, a family badge not earned yet
        SameJson(new JsonObject
        {
            ["t"] = "badges.mine", ["ok"] = true, ["you"] = "Mia", ["hide"] = false, ["new"] = 1,
            ["points"] = 250L, ["rank"] = 2, ["season"] = "2026-10", ["seasonPoints"] = 40L, ["seasonRank"] = 1, ["earned"] = 1, ["total"] = 40,
            ["badges"] = new JsonArray(
                new JsonObject { ["id"] = "loot.bronze", ["family"] = "loot", ["familyName"] = "Loot Ledger", ["tier"] = 1, ["hidden"] = false, ["earned"] = true,
                    ["name"] = "Pocketful", ["flavour"] = "It adds up.", ["how"] = "100 kills recorded", ["at"] = 1_790_000_000L, ["value"] = 100L, ["target"] = 100L },
                new JsonObject { ["id"] = "nightowl", ["family"] = "secret", ["familyName"] = "Secrets", ["tier"] = 2, ["hidden"] = true, ["earned"] = false,
                    ["name"] = "???", ["flavour"] = "", ["how"] = "???" },
                new JsonObject { ["id"] = "loot.silver", ["family"] = "loot", ["familyName"] = "Loot Ledger", ["tier"] = 2, ["hidden"] = false, ["earned"] = false,
                    ["name"] = "Hoarder", ["flavour"] = "f", ["how"] = "1,000 kills recorded", ["value"] = 150L, ["target"] = 1000L }),
        }, Written(new BadgesMineMsg
        {
            You = "Mia", New = 1, Points = 250, Rank = 2, Season = "2026-10", SeasonPoints = 40, SeasonRank = 1, Earned = 1, Total = 40,
            Badges =
            {
                new BadgeView { Id = "loot.bronze", Family = "loot", FamilyName = "Loot Ledger", Tier = 1, Hidden = false, Earned = true, Name = "Pocketful", Flavour = "It adds up.", How = "100 kills recorded", At = 1_790_000_000, Value = 100, Target = 100 },
                new BadgeView { Id = "nightowl", Family = "secret", FamilyName = "Secrets", Tier = 2, Hidden = true, Earned = false, Name = "???", Flavour = "", How = "???" },
                new BadgeView { Id = "loot.silver", Family = "loot", FamilyName = "Loot Ledger", Tier = 2, Hidden = false, Earned = false, Name = "Hoarder", Flavour = "f", How = "1,000 kills recorded", Value = 150, Target = 1000 },
            },
        }.Write).D);

        // MemberView: the short form (EarnedJson) and the count; an unknown name: no badges
        SameJson(new JsonObject
        {
            ["t"] = "badges.member", ["ok"] = true, ["name"] = "Zed", ["count"] = 1,
            ["badges"] = new JsonArray(new JsonObject { ["id"] = "loot.gold", ["family"] = "loot", ["tier"] = 3, ["name"] = "N loot.gold", ["at"] = 1_790_000_100L }),
        }, Written(new BadgesMemberMsg { Name = "Zed", Badges = { Short("loot.gold", 3, 1_790_000_100) } }.Write).D);
        SameJson(new JsonObject { ["t"] = "badges.member", ["ok"] = true, ["name"] = "", ["badges"] = new JsonArray(), ["count"] = 0 },
            Written(new BadgesMemberMsg { Name = "" }.Write).D);

        // LadderJson: your own (hidden) row, someone else's, the seasons, "you"; and a viewer who is not on it ("you": null)
        SameJson(new JsonObject
        {
            ["t"] = "ladder.data", ["ok"] = true, ["season"] = "2026-10", ["current"] = "2026-10", ["seasons"] = new JsonArray("2026-10", "2026-09"),
            ["rows"] = new JsonArray(
                new JsonObject { ["rank"] = 1, ["name"] = "Zed", ["points"] = 900L, ["top"] = new JsonArray(new JsonObject { ["id"] = "loot.gold", ["family"] = "loot", ["tier"] = 3, ["name"] = "N loot.gold", ["at"] = 5L }) },
                new JsonObject { ["rank"] = 2, ["name"] = "Mia", ["points"] = 800L, ["top"] = new JsonArray(), ["you"] = true, ["hidden"] = true },
                new JsonObject { ["rank"] = 2, ["name"] = "Bob", ["points"] = 800L, ["top"] = new JsonArray() }),
            ["you"] = new JsonObject { ["rank"] = 2, ["points"] = 800L, ["hidden"] = true },
        }, Written(new LadderDataMsg
        {
            Season = "2026-10", Current = "2026-10", Seasons = { "2026-10", "2026-09" },
            Rows =
            {
                new LadderRow { Rank = 1, Name = "Zed", Points = 900, Top = { Short("loot.gold", 3, 5) } },
                new LadderRow { Rank = 2, Name = "Mia", Points = 800, You = true, Hidden = true },
                new LadderRow { Rank = 2, Name = "Bob", Points = 800 },
            },
            You = new LadderYou { Rank = 2, Points = 800, Hidden = true },
        }.Write).D);
        SameJson(new JsonObject
        {
            ["t"] = "ladder.data", ["ok"] = true, ["season"] = "all", ["current"] = "", ["seasons"] = new JsonArray(),
            ["rows"] = new JsonArray(new JsonObject { ["rank"] = 1, ["name"] = "Zed", ["points"] = 10L, ["top"] = new JsonArray() }),
            ["you"] = null,
        }, Written(new LadderDataMsg { Season = "all", Current = "", Rows = { new LadderRow { Rank = 1, Name = "Zed", Points = 10 } } }.Write).D);
    }
}
