using System.Text.Json.Nodes;
using Xunit;

namespace Lodge.Tests;

// Lodge 2.18: badges and the ladder (feature "badges")
public class BadgesTests
{
    static async Task<JsonObject> Mine(FunRig r, Member m, bool advance = true)
    {
        if (advance) r.Clock.Advance(TimeSpan.FromSeconds(3));
        r.Sock(m.Name).Clear();
        await r.Send(m, new { t = "badges.mine" });
        return r.Sock(m.Name).Of("badges.mine").Last();
    }

    static JsonObject B(JsonObject mine, string id) => ((JsonArray)mine["badges"]!).Select(x => (JsonObject)x!).Single(b => (string)b["id"]! == id);
    static bool Earned(JsonObject mine, string id) => (bool)B(mine, id)["earned"]!;

    static async Task<JsonObject> Ladder(FunRig r, Member m, string? season = "all")
    {
        r.Clock.Advance(TimeSpan.FromSeconds(3));
        r.Sock(m.Name).Clear();
        await r.Send(m, new { t = "ladder.get", season });
        return r.Sock(m.Name).Of("ladder.data").Last();
    }

    static List<JsonObject> Rows(JsonObject ladder) => ((JsonArray)ladder["rows"]!).Select(x => (JsonObject)x!).ToList();
    static List<string> Names(JsonObject ladder) => Rows(ladder).Select(x => (string)x["name"]!).ToList();

    static void Run(FunRig r, TimeSpan? advance = null)
    {
        r.Clock.Advance(advance ?? TimeSpan.FromMinutes(11));
        r.Hub.Badges.RunAll();
    }

    static Task Kills(FunRig r, Member m, long n, string npc = "100", Dictionary<string, object>? items = null) =>
        AtlasTests.ShareKills(r, m, npc, AtlasTests.Ctr(n, items));

    // ---------------------------------------------------------------- the definitions table

    [Fact]
    public void The_definitions_table_is_sane()
    {
        var ids = Badges.Table.Select(d => d.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.True(Badges.Table.Length >= 30);
        foreach (var fam in Badges.Table.GroupBy(d => d.Family))
        {
            Assert.True(Badges.FamilyNames.ContainsKey(fam.Key), "family name for " + fam.Key);
            if (fam.Key == "secret") continue;
            var tiers = fam.Select(d => d.Tier).ToList();
            Assert.True(tiers.SequenceEqual(tiers.OrderBy(t => t).Distinct()), fam.Key + ": tiers ascend");
            var at = fam.Select(d => d.At).ToList();
            Assert.True(at.SequenceEqual(at.OrderBy(t => t).Distinct()), fam.Key + ": thresholds ascend with the tiers");
            foreach (var d in fam) Assert.Equal(fam.Key + "." + Badges.TierNames[d.Tier], d.Id);
        }
        foreach (var d in Badges.Table)
        {
            Assert.InRange(d.Tier, Badges.Bronze, Badges.Legendary);
            Assert.False(string.IsNullOrWhiteSpace(d.Name));
            Assert.False(string.IsNullOrWhiteSpace(d.Flavour));
            Assert.False(string.IsNullOrWhiteSpace(d.How));
            Assert.True(d.At > 0);
            if (d.Special == null) Assert.NotNull(Badges.MetricOf(d.Metric));
        }
        Assert.Contains(Badges.Table, d => d.Hidden);
        Assert.All(Badges.Table.Where(d => d.Hidden), d => Assert.Equal("secret", d.Family));
        Assert.Equal(Badges.Metrics.Length, Badges.Metrics.Select(m => m.Key).Distinct().Count());
        Assert.Equal("1,000 kills recorded", Badges.Find("loot.silver")!.How);
        Assert.Null(Badges.Find("nope"));
    }

    [Fact]
    public void Points_are_log_scaled_capped_and_one_category_cannot_dominate()
    {
        Assert.Equal(0, Badges.CategoryPoints("kills", 0));
        Assert.Equal(0, Badges.CategoryPoints("host", 5));          // weight 0: a badge, no ladder points
        Assert.Equal(0, Badges.CategoryPoints("nope", 5));
        var a = Badges.CategoryPoints("kills", 100);
        var b = Badges.CategoryPoints("kills", 1_000);
        var c = Badges.CategoryPoints("kills", 10_000);
        Assert.True(a < b && b < c);
        Assert.True(b < a * 10 && c < b * 10, "ten times the kills is far from ten times the points");
        Assert.True(c - b < b - a + 40);
        Assert.Equal(Badges.CategoryCap, Badges.CategoryPoints("kills", long.MaxValue / 4));
        Assert.True(Badges.CategoryPoints("chatdays", 1_000_000) <= Badges.CategoryCap);
        Assert.True(Badges.BadgePoints(Badges.Bronze) < Badges.BadgePoints(Badges.Silver) && Badges.BadgePoints(Badges.Gold) < Badges.BadgePoints(Badges.Legendary));
        Assert.Equal(0, Badges.BadgePoints(0));
    }

    [Theory]
    [InlineData("2026-10", true)]
    [InlineData("all", true)]
    [InlineData("2026-13", false)]
    [InlineData("2026-1", false)]
    [InlineData("2026-10\n", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Season_keys_are_checked(string? s, bool ok) => Assert.Equal(ok, Badges.ValidSeason(s));

    // ---------------------------------------------------------------- tiers and progress

    [Fact]
    public async Task Badges_are_earned_tier_by_tier_with_progress()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner");
        Run(r);
        var m0 = await Mine(r, elan);
        Assert.False(Earned(m0, "loot.bronze"));
        Assert.Equal(0, (int)m0["earned"]!);
        await Kills(r, elan, 150);
        Run(r);
        var m1 = await Mine(r, elan);
        Assert.True(Earned(m1, "loot.bronze"));
        Assert.False(Earned(m1, "loot.silver"));
        Assert.Equal(150, (long)B(m1, "loot.silver")["value"]!);
        Assert.Equal(1000, (long)B(m1, "loot.silver")["target"]!);
        Assert.NotNull(B(m1, "loot.bronze")["at"]);
        Assert.Null(B(m1, "loot.silver")["at"]);
        await Kills(r, elan, 1_200);
        Run(r);
        var m2 = await Mine(r, elan);
        Assert.True(Earned(m2, "loot.silver"));
        Assert.False(Earned(m2, "loot.gold"));
        Assert.Equal("Loot Ledger", (string)B(m2, "loot.gold")["familyName"]!);
        Assert.True((long)m2["points"]! > (long)m1["points"]!);
    }

    [Fact]
    public async Task Hidden_badges_show_question_marks_until_earned()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner");
        Run(r);
        var m0 = await Mine(r, elan);
        var hidden = B(m0, "nightowl");
        Assert.Equal("???", (string)hidden["name"]!);
        Assert.Equal("???", (string)hidden["how"]!);
        Assert.Equal("", (string)hidden["flavour"]!);
        Assert.Null(hidden["value"]);
        Assert.True((bool)hidden["hidden"]!);
        // five late nights in chat
        for (int d = 2; d <= 6; d++)
        {
            r.Clock.UtcNow = new DateTime(2026, 10, d, 2, 0, 0, DateTimeKind.Utc);
            await r.Say(elan, "still up");
        }
        Run(r, TimeSpan.Zero);
        var m1 = await Mine(r, elan);
        Assert.True(Earned(m1, "nightowl"));
        Assert.Equal("Night Owl", (string)B(m1, "nightowl")["name"]!);
        Assert.NotEqual("???", (string)B(m1, "nightowl")["how"]!);
        Assert.True(Earned(m1, "hearthside.bronze"));
    }

    [Fact]
    public async Task Chat_counts_days_active_not_messages()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner");
        r.Clock.UtcNow = new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < 30; i++) await r.Say(elan, "spam " + i);
        lock (r.Hub.Badges.Store.Gate) Assert.Equal(1, r.Hub.Badges.Store.Get("elan")!.Of("chatdays"));
        r.Clock.UtcNow = new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);
        await r.Say(elan, "next day");
        lock (r.Hub.Badges.Store.Gate) Assert.Equal(2, r.Hub.Badges.Store.Get("elan")!.Of("chatdays"));
        Run(r);
        Assert.False(Earned(await Mine(r, elan), "hearthside.bronze"));       // 3 days needed
    }

    [Fact]
    public async Task Membership_age_comes_from_the_first_time_a_member_is_noticed()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner");
        Run(r);
        Assert.False(Earned(await Mine(r, elan), "veteran.bronze"));
        Run(r, TimeSpan.FromDays(31));
        Assert.True(Earned(await Mine(r, elan), "veteran.bronze"));
        Assert.False(Earned(await Mine(r, elan), "veteran.silver"));
    }

    [Fact]
    public async Task Flights_files_events_signups_and_the_host_badge_come_from_the_lodge_s_own_records()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner"); var bob = r.Join("Bob", "officer");
        Run(r);
        // 5 flight legs
        var legs = Enumerable.Range(1, 5).ToDictionary(i => $"Horde|{i}>{i + 10}", i => (object)new { t = 30.5, n = 3 });
        r.Clock.Advance(TimeSpan.FromMinutes(11));
        await r.Send(elan, new { t = "flights.share", legs });
        // an event, a sign-up (changed twice: counts once), hosting and a file
        r.Clock.Advance(TimeSpan.FromSeconds(3));
        await r.Send(elan, new { t = "cal:create", title = "Raid", start = new DateTimeOffset(2026, 10, 17, 18, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), slots = new { dps = 5 }, post = false, roles = false });
        var id = (string)((JsonArray)r.Sock("Elan").Of("cal:events").Last()["events"]!)[0]!["id"]!;
        r.Clock.Advance(TimeSpan.FromSeconds(3));
        await r.Send(bob, new { t = "cal:signup", id, status = "going", role = "dps" });
        r.Clock.Advance(TimeSpan.FromSeconds(3));
        await r.Send(bob, new { t = "cal:signup", id, status = "maybe", role = "dps" });
        r.Hub.Badges.Note("Bob", "host");
        r.Hub.Badges.Note("Bob", "host");
        r.Hub.Badges.Note("Elan", "files", 5);
        Run(r, TimeSpan.Zero);
        var e = await Mine(r, elan);
        Assert.True(Earned(e, "flight.bronze"));
        Assert.True(Earned(e, "herald.bronze"));
        Assert.True(Earned(e, "quartermaster.silver"));
        var b = await Mine(r, bob);
        Assert.True(Earned(b, "host.gold"));
        Assert.Equal(1, (long)B(b, "rollcall.bronze")["value"]!);
        Assert.False(Earned(b, "rollcall.bronze"));
        lock (r.Hub.Badges.Store.Gate) Assert.Equal(1, r.Hub.Badges.Store.Get("bob")!.Of("host"));
    }

    [Fact]
    public async Task Armory_characters_and_atlas_extras_count()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner");
        Run(r);
        await AtlasTests.Share(r, elan, new { seenNpcs = new Dictionary<string, object> { ["1"] = new { name = "Hogger", title = "", type = 7, family = 0, rank = 1 }, ["2"] = new { name = "Brann", title = "", type = 7, family = 0, rank = 0 } } });
        lock (r.Hub.Badges.Store.Gate) Assert.Equal(2, r.Hub.Badges.Store.Get("elan")!.Of("seen"));
        // a rare drop (2 of 300 kills) that nobody else recorded
        await Kills(r, elan, 300, "200", new() { ["7"] = new Dictionary<string, object> { ["n"] = 2, ["c"] = 2 } });
        Run(r);
        var m = await Mine(r, elan);
        Assert.True(Earned(m, "lucky"));
        Assert.Equal("Lucky Strike", (string)B(m, "lucky")["name"]!);
    }

    [Fact]
    public async Task A_rare_drop_two_members_both_recorded_is_nobody_s_first()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner"); var bob = r.Join("Bob", "officer");
        Run(r);
        var drop = new Dictionary<string, object> { ["7"] = new Dictionary<string, object> { ["n"] = 2, ["c"] = 2 } };
        await Kills(r, elan, 300, "200", drop);
        await Kills(r, bob, 300, "200", drop);
        Run(r);
        Assert.False(Earned(await Mine(r, elan), "lucky"));
        Assert.False(Earned(await Mine(r, bob), "lucky"));
    }

    [Fact]
    public async Task Well_rounded_is_a_badge_in_eight_families()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner");
        Run(r);
        await Kills(r, elan, 150);
        foreach (var (k, n) in new[] { ("files", 1), ("events", 1), ("lfg", 1), ("signups", 5), ("host", 1), ("seen", 25) }) r.Hub.Badges.Note("Elan", k, n);
        Run(r);
        var m = await Mine(r, elan);
        // seven families so far: loot, quartermaster, herald, partystarter, rollcall, host, fieldnotes
        Assert.False(Earned(m, "wellrounded"));
        for (int d = 2; d <= 4; d++)                                   // the eighth family: three days in chat
        {
            r.Clock.UtcNow = new DateTime(2026, 10, d, 12, 0, 0, DateTimeKind.Utc);
            await r.Say(elan, "hi");
        }
        Run(r, TimeSpan.Zero);
        Assert.True(Earned(await Mine(r, elan), "wellrounded"));
    }

    // ---------------------------------------------------------------- never revoked, persisted, left

    [Fact]
    public async Task An_earned_badge_is_never_taken_back_when_its_data_is_pruned_and_survives_a_restart()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner");
        Run(r);
        var legs = Enumerable.Range(1, 5).ToDictionary(i => $"Horde|{i}>{i + 10}", i => (object)new { t = 30.5, n = 3 });
        r.Clock.Advance(TimeSpan.FromMinutes(11));
        await r.Send(elan, new { t = "flights.share", legs });
        Run(r);
        Assert.True(Earned(await Mine(r, elan), "flight.bronze"));
        // 130 days later the flight contributions have expired
        r.Clock.Advance(TimeSpan.FromDays(130));
        r.Hub.Flight.Tick();
        Assert.Equal(0, r.Hub.Flight.Store.CountOf("elan"));
        r.Hub.Badges.RunAll();
        var m = await Mine(r, elan);
        Assert.True(Earned(m, "flight.bronze"));
        Assert.Equal(0, (long)B(m, "flight.silver")["value"]!);
        // a restart: the same file
        r.Hub.Badges.Flush();
        var again = new BadgesStore(r.Dir.Path, T.Log);
        Assert.Contains("flight.bronze", again.Get("elan")!.Earned.Keys);
        Assert.Equal("Elan", again.Get("elan")!.Name);
    }

    [Fact]
    public async Task A_member_who_leaves_loses_badges_tallies_and_ladder_rows()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner"); var bob = r.Join("Bob", "officer");
        Run(r);
        await Kills(r, elan, 150); await Kills(r, bob, 150);
        Run(r);
        Assert.Equal(2, Rows(await Ladder(r, elan)).Count);
        await new AdminModule(r.Hub).Handle(bob, "member.leave", new JsonObject { ["t"] = "member.leave" });
        await r.Hub.Disconnect(bob, "left");
        Assert.Null(r.Hub.Badges.Store.Get("bob"));
        r.Hub.Badges.RunAll();
        var ladder = await Ladder(r, elan);
        Assert.Equal(new[] { "Elan" }, Names(ladder));
        var theirs = await MemberBadges(r, elan, "Bob");
        Assert.Empty((JsonArray)theirs["badges"]!);
        r.Hub.Badges.Flush();
        Assert.Null(new BadgesStore(r.Dir.Path, T.Log).Get("bob"));
    }

    static async Task<JsonObject> MemberBadges(FunRig r, Member m, string name)
    {
        r.Clock.Advance(TimeSpan.FromSeconds(3));
        r.Sock(m.Name).Clear();
        await r.Send(m, new { t = "badges.member", name });
        return r.Sock(m.Name).Of("badges.member").Last();
    }

    // ---------------------------------------------------------------- the ladder

    [Fact]
    public async Task The_ladder_ranks_by_points_with_the_top_badges_and_marks_you()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner"); var bob = r.Join("Bob", "officer");
        Run(r);
        await Kills(r, elan, 1_200); await Kills(r, bob, 120);
        Run(r);
        var l = await Ladder(r, bob);
        var rows = Rows(l);
        Assert.Equal(new[] { "Elan", "Bob" }, Names(l));
        Assert.Equal(new[] { 1, 2 }, rows.Select(x => (int)x["rank"]!));
        Assert.True((long)rows[0]["points"]! > (long)rows[1]["points"]!);
        Assert.Null(rows[0]["you"]);
        Assert.True((bool)rows[1]["you"]!);
        Assert.Equal("loot.silver", (string)((JsonArray)rows[0]["top"]!)[0]!["id"]!);
        Assert.Equal(2, (int)l["you"]!["rank"]!);
        Assert.Equal("all", (string)l["season"]!);
        Assert.Equal("2026-10", (string)l["current"]!);
        // nothing from the game in the rows
        foreach (var row in rows) Assert.Equal(new[] { "name", "points", "rank", "top", "you" }.Where(row.ContainsKey).OrderBy(x => x), row.Select(kv => kv.Key).OrderBy(x => x));
    }

    [Fact]
    public async Task Equal_points_share_a_rank()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner"); var bob = r.Join("Bob", "officer");
        Run(r);
        await Kills(r, elan, 150); await Kills(r, bob, 150);
        Run(r);
        Assert.Equal(new[] { 1, 1 }, Rows(await Ladder(r, elan)).Select(x => (int)x["rank"]!));
    }

    [Fact]
    public async Task Hiding_from_the_ladder_omits_you_for_others_but_not_for_yourself_and_badges_stay()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner"); var bob = r.Join("Bob", "officer");
        Run(r);
        await Kills(r, elan, 150); await Kills(r, bob, 150);
        Run(r);
        r.Clock.Advance(TimeSpan.FromSeconds(3));
        r.Sock("Bob").Clear();
        await r.Send(bob, new { t = "badges.hide", hide = true });
        Assert.True((bool)r.Sock("Bob").Of("badges.mine").Last()["hide"]!);
        Assert.Equal(new[] { "Elan" }, Names(await Ladder(r, elan)));
        var own = await Ladder(r, bob);
        Assert.Equal(2, Rows(own).Count);
        Assert.True((bool)Rows(own).Single(x => (string)x["name"]! == "Bob")["hidden"]!);
        // badges are still earned and still visible on the profile
        var theirs = await MemberBadges(r, elan, "Bob");
        Assert.Equal("loot.bronze", (string)((JsonArray)theirs["badges"]!)[0]!["id"]!);
        Assert.True(Earned(await Mine(r, bob), "loot.bronze"));
        // and back
        await r.Send(bob, new { t = "badges.hide", hide = false });
        Assert.Equal(2, Rows(await Ladder(r, elan)).Count);
    }

    [Fact]
    public async Task The_season_resets_on_the_first_and_the_old_one_stays_readable()
    {
        using var r = new FunRig();
        r.Clock.UtcNow = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
        var elan = r.Join("Elan", "owner"); var bob = r.Join("Bob", "officer");
        Run(r);
        await Kills(r, elan, 1_200); await Kills(r, bob, 150);
        Run(r);
        var oct = await Ladder(r, elan, "2026-10");
        Assert.Equal(2, Rows(oct).Count);
        var octPoints = Rows(oct).Select(x => (long)x["points"]!).ToList();
        // November 1st, 00:05 UTC
        r.Clock.UtcNow = new DateTime(2026, 11, 1, 0, 5, 0, DateTimeKind.Utc);
        r.Hub.Badges.RunAll();
        var nov = await Ladder(r, elan, "2026-11");
        Assert.Empty(Rows(nov));
        Assert.Equal("2026-11", (string)nov["current"]!);
        Assert.Equal(new[] { "2026-11", "2026-10" }, ((JsonArray)nov["seasons"]!).Select(x => (string)x!));
        var oct2 = await Ladder(r, elan, "2026-10");
        Assert.Equal(new[] { "Elan", "Bob" }, Names(oct2));
        Assert.Equal(octPoints, Rows(oct2).Select(x => (long)x["points"]!).ToList());
        var all = await Ladder(r, elan, "all");
        Assert.Equal(2, Rows(all).Count);                               // all-time is untouched
        // new work in November counts for November only, on top of nothing
        await Kills(r, bob, 400);
        r.Hub.Badges.RunAll();
        var nov2 = await Ladder(r, elan, "2026-11");
        Assert.Equal(new[] { "Bob" }, Names(nov2));
        // the archive survives a restart
        r.Hub.Badges.Flush();
        Assert.Equal(2, new BadgesStore(r.Dir.Path, T.Log).HistoryOf("2026-10")!.Count);
        // an unknown or malformed season falls back to the current one
        Assert.Equal("2026-11", (string)(await Ladder(r, elan, "garbage"))["season"]!);
        Assert.Empty(Rows(await Ladder(r, elan, "2019-01")));
    }

    // ---------------------------------------------------------------- guests, the switches, limits

    [Fact]
    public async Task Guests_have_no_badges_and_do_not_appear()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner"); var pat = r.Join("Pat", "guest");
        Run(r);
        await Kills(r, elan, 150);
        await r.Do(pat, new { t = "msg", channel = "general", text = "hi there" });
        r.Hub.Badges.Note("Pat", "files");
        r.Hub.Badges.OnSignup(pat, "evt");
        Run(r);
        r.Sock("Pat").Clear();
        await r.Send(pat, new { t = "badges.mine" });
        Assert.Equal("guest", (string)r.Sock("Pat").LastError()!["code"]!);
        Assert.Empty(r.Sock("Pat").Of("badges.mine"));
        Assert.Equal(new[] { "Elan" }, Names(await Ladder(r, pat)));    // a guest may look at the ladder
        Assert.Null((await Ladder(r, pat))["you"]);
        lock (r.Hub.Badges.Store.Gate) Assert.DoesNotContain(r.Hub.Badges.Store.All, kv => kv.Value.Name == "Pat");
    }

    [Fact]
    public async Task The_owner_can_switch_the_whole_feature_off_and_on_and_the_data_stays()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner"); var bob = r.Join("Bob", "officer");
        Run(r);
        await Kills(r, elan, 150);
        Run(r);
        Assert.Contains("badges", r.Hub.FeatureList());
        // an officer can't
        r.Sock("Bob").Clear();
        await r.Send(bob, new { t = "admin.badges", on = false });
        Assert.NotNull(r.Sock("Bob").LastError());
        Assert.Contains("badges", r.Hub.FeatureList());
        // the owner can; everybody hears it
        r.Sock("Bob").Clear();
        await r.Send(elan, new { t = "admin.badges", on = false });
        var state = r.Sock("Bob").Of("badges.state").Last();
        Assert.False((bool)state["enabled"]!);
        Assert.False((bool)state["locked"]!);
        Assert.DoesNotContain("badges", r.Hub.FeatureList());
        Assert.False((bool)r.Hub.Welcome(bob)["badges"]!["enabled"]!);
        foreach (var t in new object[] { new { t = "badges.mine" }, new { t = "ladder.get", season = "all" }, new { t = "badges.member", name = "Elan" }, new { t = "badges.hide", hide = true } })
        {
            r.Sock("Bob").Clear();
            await r.Send(bob, t);
            Assert.Equal("disabled", (string)r.Sock("Bob").LastError()!["code"]!);
        }
        r.Hub.Badges.OnChat(elan, false);                                // nothing is tallied while it is off
        lock (r.Hub.Badges.Store.Gate) Assert.Equal(0, r.Hub.Badges.Store.Get("elan")!.Of("chatdays"));
        // it stays off after a restart, and on again brings everything back
        r.Hub.Badges.Flush();
        Assert.False(new BadgesStore(r.Dir.Path, T.Log).Enabled);
        await r.Send(elan, new { t = "admin.badges", on = true });
        Assert.Contains("badges", r.Hub.FeatureList());
        Assert.True(Earned(await Mine(r, elan), "loot.bronze"));
    }

    [Fact]
    public async Task The_server_setting_switches_it_off_for_good()
    {
        using var r = new FunRig(c => c.BadgesEnabled = false);
        var elan = r.Join("Elan", "owner");
        Assert.DoesNotContain("badges", r.Hub.FeatureList());
        Assert.True((bool)r.Hub.Welcome(elan)["badges"]!["locked"]!);
        r.Sock("Elan").Clear();
        await r.Send(elan, new { t = "badges.mine" });
        Assert.Equal("disabled", (string)r.Sock("Elan").LastError()!["code"]!);
        r.Sock("Elan").Clear();
        await r.Send(elan, new { t = "admin.badges", on = true });
        Assert.Equal("locked", (string)r.Sock("Elan").LastError()!["code"]!);
        r.Hub.Badges.RunAll();
        Assert.False(File.Exists(Path.Combine(r.Dir.Path, "badges.json")));
    }

    [Fact]
    public async Task Requests_are_rate_limited()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner");
        Run(r);
        r.Sock("Elan").Clear();
        for (int i = 0; i < 14; i++) await r.Send(elan, new { t = "badges.mine" });
        Assert.Equal(10, r.Sock("Elan").Of("badges.mine").Count);
        Assert.Equal("rate", (string)r.Sock("Elan").LastError()!["code"]!);
        // a flood of ladder requests with nonsense does no harm either
        r.Clock.Advance(TimeSpan.FromMinutes(1));
        foreach (var bad in new object[] { new { t = "ladder.get", season = 5 }, new { t = "ladder.get", season = new { x = 1 } }, new { t = "badges.member", name = 7 }, new { t = "badges.hide", hide = "yes" } })
            await r.Send(elan, bad);
    }

    [Fact]
    public async Task The_welcome_announces_the_feature_and_its_state()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner");
        Assert.Contains("badges", r.Hub.FeatureList());
        var w = r.Hub.Welcome(elan);
        Assert.True((bool)w["badges"]!["enabled"]!);
        Assert.False((bool)w["badges"]!["locked"]!);
        Assert.Contains(w["features"]!.AsArray(), f => (string)f! == "badges");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task The_new_dot_goes_when_the_member_has_looked()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner");
        Run(r);
        await Kills(r, elan, 150);
        Run(r);
        Assert.Equal(1, (int)(await Mine(r, elan))["new"]!);
        r.Clock.Advance(TimeSpan.FromSeconds(3));
        await r.Send(elan, new { t = "badges.seen" });
        Assert.Equal(0, (int)(await Mine(r, elan))["new"]!);
        await Kills(r, elan, 1_500);
        Run(r);
        Assert.Equal(1, (int)(await Mine(r, elan))["new"]!);             // only the silver one is new
    }

    [Fact]
    public async Task A_garbled_store_file_does_not_stop_the_lodge()
    {
        using var r = new FunRig();
        File.WriteAllText(Path.Combine(r.Dir.Path, "badges.json"), "{ \"members\": { \"elan\": { \"earned\": { \"loot.bronze\": 5, \"made.up\": 9, \"loot.silver\": \"x\" }, \"tally\": { \"files\": -4, \"zzz\": 1 } }, \"\": 5 }, \"season\": \"nonsense\" ");
        var s = new BadgesStore(r.Dir.Path, T.Log);
        Assert.Empty(s.All);                                   // unparsable: starts empty, keeps running
        File.WriteAllText(Path.Combine(r.Dir.Path, "badges.json"), "{ \"v\":1, \"season\": \"nonsense\", \"members\": { \"elan\": { \"earned\": { \"loot.bronze\": 5, \"made.up\": 9, \"loot.silver\": \"x\" }, \"tally\": { \"files\": -4, \"zzz\": 1 } }, \"\": 5 } }");
        var s2 = new BadgesStore(r.Dir.Path, T.Log);
        var rec = s2.Get("elan")!;
        Assert.Equal(new[] { "loot.bronze" }, rec.Earned.Keys);
        Assert.Equal(0, rec.Of("files"));
        Assert.Equal("", s2.Season);
        await Task.CompletedTask;
    }

    static string[] Keys(JsonObject o) => o.Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray();

    // the typed BadgesMineMsg / BadgesMemberMsg / LadderDataMsg / BadgesStateMsg (Elans.Shared) send the same keys the old JsonObjects did
    [Fact]
    public async Task Typed_badge_frames_keep_their_keys()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner"); var bob = r.Join("Bob", "officer");
        Run(r);
        await Kills(r, elan, 150); await Kills(r, bob, 1_200);
        Run(r);
        var mine = await Mine(r, elan);
        Assert.Equal(new[] { "badges", "earned", "hide", "new", "ok", "points", "rank", "season", "seasonPoints", "seasonRank", "t", "total", "you" }, Keys(mine));
        Assert.Equal(new[] { "at", "earned", "family", "familyName", "flavour", "hidden", "how", "id", "name", "target", "tier", "value" }, Keys(B(mine, "loot.bronze")));
        Assert.Equal(new[] { "earned", "family", "familyName", "flavour", "hidden", "how", "id", "name", "target", "tier", "value" }, Keys(B(mine, "loot.silver")));
        Assert.Equal(new[] { "earned", "family", "familyName", "flavour", "hidden", "how", "id", "name", "tier" }, Keys(B(mine, "nightowl")));

        var theirs = await MemberBadges(r, elan, "Bob");
        Assert.Equal(new[] { "badges", "count", "name", "ok", "t" }, Keys(theirs));
        Assert.Equal(new[] { "at", "family", "id", "name", "tier" }, Keys((JsonObject)theirs["badges"]![0]!));
        Assert.Equal(2, (int)theirs["count"]!);

        r.Clock.Advance(TimeSpan.FromSeconds(3));
        await r.Send(elan, new { t = "badges.hide", hide = true });
        Assert.Equal(new[] { "badges", "earned", "hide", "new", "ok", "points", "rank", "season", "seasonPoints", "seasonRank", "t", "total", "you" }, Keys(r.Sock("Elan").Of("badges.mine").Last()));
        var ladder = await Ladder(r, elan);
        Assert.Equal(new[] { "current", "ok", "rows", "season", "seasons", "t", "you" }, Keys(ladder));
        var rows = Rows(ladder);
        Assert.Equal(new[] { "name", "points", "rank", "top" }, Keys(rows.Single(x => (string)x["name"]! == "Bob")));
        Assert.Equal(new[] { "hidden", "name", "points", "rank", "top", "you" }, Keys(rows.Single(x => (string)x["name"]! == "Elan")));
        Assert.Equal(new[] { "at", "family", "id", "name", "tier" }, Keys((JsonObject)rows[0]["top"]![0]!));
        Assert.Equal(new[] { "hidden", "points", "rank" }, Keys((JsonObject)ladder["you"]!));
        var asBob = await Ladder(r, bob);                                // Elan left the ladder: Bob doesn't see the row
        Assert.Equal(new[] { "Bob" }, Names(asBob));

        var guest = r.Join("Gus", "guest");
        var gl = await Ladder(r, guest);
        Assert.True(gl.ContainsKey("you") && gl["you"] == null);           // not on it: "you" is sent as null

        Assert.Equal(new[] { "enabled", "locked" }, Keys((JsonObject)r.Hub.Welcome(elan)["badges"]!));
        await r.Send(elan, new { t = "admin.badges", on = false });
        Assert.Equal(new[] { "enabled", "locked", "t" }, Keys(r.Sock("Bob").Of("badges.state").Last()));
    }
}
