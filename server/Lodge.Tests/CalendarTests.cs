using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Lodge.Tests;

// Lodge 2.11: the raid / event calendar
public class CalendarTests
{
    // 2026-10-17 18:00 UTC = 20:00 in Berlin (CEST); Berlin's clocks go back on 2026-10-25
    const long Oct17_18Z = 1_792_260_000_000L;
    static long Ms(int y, int mo, int d, int h = 0, int mi = 0) => new DateTimeOffset(y, mo, d, h, mi, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    static object Ev(long start, string title = "Molten Core", object? slots = null, string? tz = "Europe/Berlin", object? repeat = null,
        bool roles = true, string note = "bring fire resist", int? durationMin = 180, bool post = true, string? channel = null, int off = 120)
        => new { t = "cal:create", title, start, slots = slots ?? new { tank = 2, healer = 3, dps = 5 }, tz, off, repeat, roles, note, durationMin, post, channel };

    static async Task<JsonObject?> Create(FunRig r, Member m, object ev)
    {
        r.Clock.Advance(TimeSpan.FromSeconds(3));
        r.Sock(m.Name).Clear();
        await r.Send(m, ev);
        return r.Sock(m.Name).Of("cal:events").LastOrDefault();
    }

    static List<JsonObject> Events(JsonObject? frame) => frame == null ? new() : ((JsonArray)frame["events"]!).Select(x => (JsonObject)x!).ToList();
    static string Err(FunRig r, string who) => (string?)r.Sock(who).LastError()?["text"] ?? "";

    static JsonObject Stored(FunRig r, string id) { lock (r.Hub.Calendar.Store.Gate) return (JsonObject)r.Hub.Calendar.Store.Find(id)!.DeepClone(); }

    // ---------------------------------------------------------------- validation

    [Fact]
    public async Task A_valid_event_is_broadcast_to_everyone_and_survives_a_restart()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner"); r.Join("Bob", "officer"); r.Join("Pat", "guest");
        var frame = await Create(r, elan, Ev(Oct17_18Z));
        var ev = Events(frame).Single();
        Assert.Equal("Molten Core", (string)ev["title"]!);
        Assert.Equal(Oct17_18Z, (long)ev["start"]!);
        Assert.Equal(Oct17_18Z + 180 * 60_000L, (long)ev["end"]!);
        Assert.Equal("Elan", (string)ev["by"]!);
        Assert.Equal("open", (string)ev["status"]!);
        Assert.NotEmpty(r.Sock("Bob").Of("cal:events"));
        Assert.NotEmpty(r.Sock("Pat").Of("cal:events"));   // guests see the calendar too

        var again = new CalendarStore(r.Dir.Path, T.Log, r.Clock);
        Assert.Equal("Molten Core", (string)again.Events.Single()["title"]!);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_title_is_required(string title)
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner");
        Assert.Null(await Create(r, elan, Ev(Oct17_18Z, title: title)));
        Assert.Contains("title", Err(r, "Elan"));
    }

    [Theory]
    [InlineData(41, 0, 0)]
    [InlineData(-1, 1, 1)]
    [InlineData(0, 0, 0)]
    public async Task Role_counts_are_bounded_and_a_role_event_needs_a_slot(int tank, int healer, int dps)
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner");
        Assert.Null(await Create(r, elan, Ev(Oct17_18Z, slots: new { tank, healer, dps })));
        Assert.Equal("cal", (string?)r.Sock("Elan").LastError()!["code"]);
    }

    [Fact]
    public async Task A_social_event_needs_no_slots_and_title_and_note_are_bounded_and_defanged()
    {
        using var r = new FunRig();
        var pat = r.Join("Pat", "member");
        var frame = await Create(r, pat, Ev(Oct17_18Z, title: new string('x', 90) + " @everyone", note: new string('n', 300) + "@here", roles: false, slots: new { }));
        var ev = Events(frame).Single();
        Assert.Equal(60, ((string)ev["title"]!).Length);
        Assert.Equal(200, ((string)ev["note"]!).Length);
        Assert.False((bool)ev["roles"]!);
        var f2 = await Create(r, pat, Ev(Oct17_18Z + 3_600_000, title: "Party @everyone", note: "@here", roles: false));
        var e2 = Events(f2).Single();
        Assert.DoesNotContain("@everyone", (string)e2["title"]!, StringComparison.Ordinal);   // (culture compare ignores the zero-width space)
        Assert.DoesNotContain("@here", (string)e2["note"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_must_be_in_range_and_the_length_is_bounded()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner");
        Assert.Null(await Create(r, elan, Ev(r.NowMs - 3_600_000)));                      // passed
        Assert.Null(await Create(r, elan, Ev(r.NowMs + 400L * 86_400_000)));              // beyond a year
        Assert.Null(await Create(r, elan, Ev(Oct17_18Z, durationMin: 5)));                // too short
        Assert.Null(await Create(r, elan, Ev(Oct17_18Z, durationMin: 2000)));             // too long
        Assert.Null(await Create(r, elan, new { t = "cal:create", title = "x", start = 0, slots = new { dps = 1 } }));
        Assert.NotNull(await Create(r, elan, Ev(Oct17_18Z, durationMin: null)));          // no length at all is fine
        Assert.NotNull(await Create(r, elan, Ev(Oct17_18Z + 7_200_000, durationMin: 1440)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(27)]
    [InlineData(-3)]
    public async Task Recurrence_is_bounded_to_26_weeks(int weeks)
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner");
        Assert.Null(await Create(r, elan, Ev(Oct17_18Z, repeat: new { weeks })));
        Assert.Contains("26", Err(r, "Elan"));
    }

    [Fact]
    public async Task A_weekly_series_makes_one_event_per_week_sharing_a_series_id()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner");
        var evs = Events(await Create(r, elan, Ev(Oct17_18Z, repeat: new { weeks = 26 })));
        Assert.Equal(26, evs.Count);
        Assert.Single(evs.Select(e => (string)e["series"]!).Distinct());
        Assert.Equal(26, evs.Select(e => (string)e["id"]!).Distinct().Count());
        Assert.NotNull(await Create(r, elan, Ev(Oct17_18Z + 86_400_000 * 2, repeat: new { weeks = 1 })));
    }

    [Fact]
    public async Task Bad_json_shapes_cost_the_message_only()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner");
        foreach (var bad in new object[]
        {
            new { t = "cal:create", title = 5, start = "soon" },
            new { t = "cal:create", title = "x", start = Oct17_18Z, slots = new { tank = "many" } },
            new { t = "cal:create", title = "x", start = Oct17_18Z, slots = 7, repeat = "weekly" },
            new { t = "cal:signup", id = 12, status = new[] { 1 } },
            new { t = "cal:range", from = "a", to = new { } },
            new { t = "cal:edit" }, new { t = "cal:cancel", id = new { } },
        })
        {
            try { await r.Send(elan, bad); } catch (Exception e) when (e is InvalidCastException or FormatException or InvalidOperationException) { /* the hub's receive loop drops such frames */ }
        }
        Assert.Empty(r.Hub.Calendar.Store.Events);
    }

    // ---------------------------------------------------------------- permissions

    [Fact]
    public async Task Guests_can_view_and_sign_up_but_not_create()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner"); var pat = r.Join("Pat (guest)", "guest");
        Assert.Null(await Create(r, pat, Ev(Oct17_18Z)));
        Assert.Equal("cal", (string?)r.Sock("Pat (guest)").LastError()!["code"]);
        var id = (string)Events(await Create(r, elan, Ev(Oct17_18Z))).Single()["id"]!;
        r.Sock("Pat (guest)").Clear();
        await r.Send(pat, new { t = "cal:signup", id, status = "going", role = "dps" });
        var e = Stored(r, id);
        Assert.Contains(e["signups"]!.AsArray(), x => (string)x!["n"]! == "Pat (guest)");
        await r.Send(pat, new { t = "cal:range", from = Oct17_18Z - 86_400_000, to = Oct17_18Z + 86_400_000 });
        Assert.Single(r.Sock("Pat (guest)").Of("cal:range"));
        // and may not edit or cancel it
        await r.Send(pat, new { t = "cal:cancel", id });
        Assert.Equal("open", (string)Stored(r, id)["status"]!);
    }

    [Fact]
    public async Task Members_edit_and_cancel_their_own_but_not_others_and_officers_can_change_any()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner"); var bob = r.Join("Bob", "officer"); var pat = r.Join("Pat", "member"); var amy = r.Join("Amy", "veteran");
        var mine = (string)Events(await Create(r, pat, Ev(Oct17_18Z, title: "Pat's run"))).Single()["id"]!;
        var other = (string)Events(await Create(r, elan, Ev(Oct17_18Z + 86_400_000, title: "Guild raid"))).Single()["id"]!;

        // Pat edits her own
        r.Clock.Advance(TimeSpan.FromSeconds(3));
        await r.Send(pat, new { t = "cal:edit", id = mine, title = "Pat's run II", start = Oct17_18Z, slots = new { tank = 1, healer = 1, dps = 3 } });
        Assert.Equal("Pat's run II", (string)Stored(r, mine)["title"]!);
        // ...but not Elan's
        r.Clock.Advance(TimeSpan.FromSeconds(3));
        await r.Send(pat, new { t = "cal:edit", id = other, title = "Hijacked", start = Oct17_18Z, slots = new { dps = 1 } });
        Assert.Equal("Guild raid", (string)Stored(r, other)["title"]!);
        r.Clock.Advance(TimeSpan.FromSeconds(3));
        await r.Send(pat, new { t = "cal:cancel", id = other });
        Assert.Equal("open", (string)Stored(r, other)["status"]!);
        // another member (veteran) can't touch Pat's either
        r.Clock.Advance(TimeSpan.FromSeconds(3));
        await r.Send(amy, new { t = "cal:cancel", id = mine });
        Assert.Equal("open", (string)Stored(r, mine)["status"]!);
        // an officer can edit and cancel anyone's
        r.Clock.Advance(TimeSpan.FromSeconds(3));
        await r.Send(bob, new { t = "cal:edit", id = mine, title = "Officer fix", start = Oct17_18Z, slots = new { tank = 1, healer = 1, dps = 3 } });
        Assert.Equal("Officer fix", (string)Stored(r, mine)["title"]!);
        r.Clock.Advance(TimeSpan.FromSeconds(3));
        await r.Send(bob, new { t = "cal:cancel", id = mine });
        Assert.Equal("cancelled", (string)Stored(r, mine)["status"]!);
        // Pat cancels her own
        r.Clock.Advance(TimeSpan.FromSeconds(3));
        var again = (string)Events(await Create(r, pat, Ev(Oct17_18Z + 2 * 86_400_000))).Single()["id"]!;
        r.Clock.Advance(TimeSpan.FromSeconds(3));
        await r.Send(pat, new { t = "cal:cancel", id = again });
        Assert.Equal("cancelled", (string)Stored(r, again)["status"]!);
        // a cancelled event takes no sign-ups and can't be edited
        r.Sock("Amy").Clear();
        await r.Send(amy, new { t = "cal:signup", id = again, status = "going", role = "dps" });
        Assert.Empty(Stored(r, again)["signups"]!.AsArray());
        Assert.Equal("cal", (string?)r.Sock("Amy").LastError()!["code"]);
    }

    // ---------------------------------------------------------------- sign-ups

    [Fact]
    public async Task Signing_up_has_roles_statuses_comments_and_one_entry_per_person()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner"); var pat = r.Join("Pat", "member");
        var id = (string)Events(await Create(r, elan, Ev(Oct17_18Z, slots: new { tank = 1, healer = 0, dps = 1 }))).Single()["id"]!;
        await r.Send(pat, new { t = "cal:signup", id, status = "going", role = "tank", comment = "  can  bring\ncake " });
        var e = Stored(r, id);
        var me = e["signups"]!.AsArray().Single()!;
        Assert.Equal("going", (string)me["s"]!); Assert.Equal("tank", (string)me["r"]!); Assert.Equal("can bring cake", (string)me["c"]!);
        // changing the answer moves her (still one entry)
        await r.Send(pat, new { t = "cal:signup", id, status = "maybe", role = "dps" });
        await r.Send(pat, new { t = "cal:signup", id, status = "declined" });
        e = Stored(r, id);
        Assert.Single(e["signups"]!.AsArray());
        Assert.Equal("declined", (string)e["signups"]![0]!["s"]!); Assert.Equal("", (string)e["signups"]![0]!["r"]!);
        // a role with no places, a missing role for going and junk are refused
        r.Sock("Pat").Clear();
        await r.Send(pat, new { t = "cal:signup", id, status = "going", role = "healer" });
        Assert.Equal("cal-full", (string?)r.Sock("Pat").LastError()!["code"]);
        await r.Send(pat, new { t = "cal:signup", id, status = "going" });
        await r.Send(pat, new { t = "cal:signup", id, status = "perhaps", role = "dps" });
        await r.Send(pat, new { t = "cal:signup", id, status = "going", role = "bard" });
        Assert.Equal("declined", (string)Stored(r, id)["signups"]![0]!["s"]!);
        // leaving removes the entry
        await r.Send(pat, new { t = "cal:leave", id });
        Assert.Empty(Stored(r, id)["signups"]!.AsArray());
    }

    [Fact]
    public async Task A_refused_move_keeps_the_old_place_and_maybes_dont_take_a_place()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner"); var pat = r.Join("Pat", "member"); var amy = r.Join("Amy", "member");
        var id = (string)Events(await Create(r, elan, Ev(Oct17_18Z, slots: new { tank = 1, healer = 1, dps = 0 }))).Single()["id"]!;
        await r.Send(pat, new { t = "cal:signup", id, status = "going", role = "tank" });
        await r.Send(amy, new { t = "cal:signup", id, status = "going", role = "healer" });
        await r.Send(amy, new { t = "cal:signup", id, status = "going", role = "tank" });   // full: Amy stays healer
        Assert.Equal("healer", (string)Cal.Entry(Stored(r, id), "Amy")!["r"]!);
        await r.Send(elan, new { t = "cal:signup", id, status = "maybe", role = "tank" });  // a maybe may name a role
        Assert.Equal(1, Cal.Going(Stored(r, id), "tank"));
        Assert.Equal(1, Cal.Count(Stored(r, id), "maybe"));
    }

    [Fact]
    public async Task Social_events_ignore_roles_and_have_no_place_limit_until_the_signup_cap()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner");
        var id = (string)Events(await Create(r, elan, Ev(Oct17_18Z, roles: false, slots: new { }))).Single()["id"]!;
        JsonObject live;
        lock (r.Hub.Calendar.Store.Gate) live = r.Hub.Calendar.Store.Find(id)!;
        for (int i = 0; i < 61; i++)   // more people than the hub admits at once, so straight at the rules
        {
            var res = Cal.SignUp(live, "P" + i, "going", "tank", "", r.NowMs, false);
            Assert.Equal(i < Cal.MaxSignups ? Cal.Result.Changed : Cal.Result.Full, res);
        }
        Assert.Equal(Cal.MaxSignups, live["signups"]!.AsArray().Count);
        Assert.All(live["signups"]!.AsArray(), x => Assert.Equal("", (string)x!["r"]!));
    }

    [Fact]
    public async Task Parallel_signups_for_the_last_place_give_it_to_exactly_one()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner");
        var id = (string)Events(await Create(r, elan, Ev(Oct17_18Z, slots: new { tank = 1, healer = 0, dps = 0 }))).Single()["id"]!;
        var people = Enumerable.Range(0, 24).Select(i => r.Join("Raider" + i, "member")).ToList();
        await Task.WhenAll(people.Select(p => Task.Run(() => r.Send(p, new { t = "cal:signup", id, status = "going", role = "tank" }))));
        var e = Stored(r, id);
        Assert.Equal(1, Cal.Going(e, "tank"));
        Assert.Equal(1, e["signups"]!.AsArray().Count);
    }

    [Fact]
    public async Task Signups_close_when_the_event_is_over()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner"); var pat = r.Join("Pat", "member");
        var id = (string)Events(await Create(r, elan, Ev(r.NowMs + 3_600_000, durationMin: 60))).Single()["id"]!;
        r.Clock.Advance(TimeSpan.FromHours(3));
        r.Sock("Pat").Clear();
        await r.Send(pat, new { t = "cal:signup", id, status = "going", role = "dps" });
        Assert.Empty(Stored(r, id)["signups"]!.AsArray());
    }

    [Fact]
    public async Task An_edit_cannot_push_a_role_below_the_people_in_it_and_switching_roles_keeps_signups_valid()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner"); var pat = r.Join("Pat", "member"); var amy = r.Join("Amy", "member");
        var id = (string)Events(await Create(r, elan, Ev(Oct17_18Z, slots: new { tank = 2, healer = 0, dps = 0 }))).Single()["id"]!;
        await r.Send(pat, new { t = "cal:signup", id, status = "going", role = "tank" });
        await r.Send(amy, new { t = "cal:signup", id, status = "going", role = "tank" });
        r.Clock.Advance(TimeSpan.FromSeconds(3)); r.Sock("Elan").Clear();
        await r.Send(elan, new { t = "cal:edit", id, title = "x", start = Oct17_18Z, slots = new { tank = 1, healer = 1, dps = 0 } });
        Assert.Contains("can't go below", Err(r, "Elan"));
        r.Clock.Advance(TimeSpan.FromSeconds(3));
        await r.Send(elan, new { t = "cal:edit", id, title = "Social", start = Oct17_18Z, roles = false });
        Assert.All(Stored(r, id)["signups"]!.AsArray(), x => { Assert.Equal("going", (string)x!["s"]!); Assert.Equal("", (string)x["r"]!); });
        r.Clock.Advance(TimeSpan.FromSeconds(3));
        await r.Send(elan, new { t = "cal:edit", id, title = "Raid", start = Oct17_18Z, slots = new { tank = 2, healer = 1, dps = 1 } });
        Assert.All(Stored(r, id)["signups"]!.AsArray(), x => Assert.Equal("maybe", (string)x!["s"]!));   // going-without-role became maybe
    }

    // ---------------------------------------------------------------- recurrence and time zones

    static (long utc, DateTime local)[] Expand(string tzId, DateTime firstLocal, int count)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(tzId);
        var first = Cal.FromLocal(firstLocal, tz);
        return Cal.Occurrences(first, tz, count).Select(u => (u, Cal.ToLocal(u, tz))).ToArray();
    }

    [Fact]
    public void Weekly_recurrence_keeps_the_local_wall_clock_across_the_autumn_DST_change()
    {
        // Berlin 20:00 on Sat 17 Oct 2026 (CEST); clocks go back on Sun 25 Oct
        var occ = Expand("Europe/Berlin", new DateTime(2026, 10, 17, 20, 0, 0), 4);
        Assert.All(occ, o => Assert.Equal(new TimeSpan(20, 0, 0), o.local.TimeOfDay));
        Assert.Equal(Ms(2026, 10, 17, 18), occ[0].utc);   // UTC+2
        Assert.Equal(Ms(2026, 10, 24, 18), occ[1].utc);
        Assert.Equal(Ms(2026, 10, 31, 19), occ[2].utc);   // UTC+1 after the change: the instant moved, the wall clock did not
        Assert.Equal(Ms(2026, 11, 7, 19), occ[3].utc);
        Assert.Equal(new[] { 17, 24, 31, 7 }, occ.Select(o => o.local.Day));
    }

    [Fact]
    public void Weekly_recurrence_across_the_spring_change_and_a_time_that_does_not_exist()
    {
        // New York 02:30 on Sat 7 Mar 2026; on Sun 8 Mar 02:30 does not exist (clocks jump 02:00 -> 03:00)
        var occ = Expand("America/New_York", new DateTime(2026, 3, 7, 20, 0, 0), 3);
        Assert.All(occ, o => Assert.Equal(new TimeSpan(20, 0, 0), o.local.TimeOfDay));
        Assert.Equal(Ms(2026, 3, 8, 1), occ[0].utc);      // 20:00 EST = 01:00Z next day
        Assert.Equal(Ms(2026, 3, 15, 0), occ[1].utc);     // 20:00 EDT = 00:00Z
        var tz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var gap = Cal.FromLocal(new DateTime(2026, 3, 8, 2, 30, 0), tz);
        Assert.Equal(new TimeSpan(3, 30, 0), Cal.ToLocal(gap, tz).TimeOfDay);       // moved forward by the gap
        var overlap = Cal.FromLocal(new DateTime(2026, 11, 1, 1, 30, 0), tz);
        Assert.Equal(Ms(2026, 11, 1, 5, 30), overlap);                              // the first 01:30 (EDT)
    }

    [Fact]
    public void Windows_ids_unknown_ids_and_fixed_offsets_are_resolved()
    {
        Assert.Equal(Cal.ToLocal(Ms(2026, 7, 1, 12), Cal.ResolveTz("Europe/Berlin", 0)), Cal.ToLocal(Ms(2026, 7, 1, 12), Cal.ResolveTz("W. Europe Standard Time", 0)));
        Assert.Equal(TimeSpan.FromHours(2), Cal.ResolveTz("Nowhere/Land", 120).GetUtcOffset(DateTime.UtcNow));   // falls back to the offset
        Assert.Equal(TimeSpan.Zero, Cal.ResolveTz("Nowhere/Land", 0).GetUtcOffset(DateTime.UtcNow));
        Assert.Equal(TimeSpan.Zero, Cal.ResolveTz(null, 99999).GetUtcOffset(DateTime.UtcNow));                  // out of range: UTC
    }

    [Fact]
    public async Task A_created_series_uses_the_creators_zone_for_every_week()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner");
        var evs = Events(await Create(r, elan, Ev(Oct17_18Z, repeat: new { weeks = 4 })));
        Assert.Equal(new[] { Oct17_18Z, Ms(2026, 10, 24, 18), Ms(2026, 10, 31, 19), Ms(2026, 11, 7, 19) }, evs.Select(e => (long)e["start"]!).ToArray());
        Assert.Equal(new[] { Oct17_18Z + 180 * 60_000L, Ms(2026, 10, 24, 21), Ms(2026, 10, 31, 22), Ms(2026, 11, 7, 22) }, evs.Select(e => (long)e["end"]!).ToArray());
    }

    [Fact]
    public async Task Cancelling_following_events_and_editing_following_events_keeps_the_wall_clock()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner");
        var evs = Events(await Create(r, elan, Ev(Oct17_18Z, repeat: new { weeks = 4 })));
        var second = (string)evs[1]["id"]!;
        r.Clock.Advance(TimeSpan.FromSeconds(3));
        // move "this and following" from 20:00 to 21:00 Berlin
        await r.Send(elan, new { t = "cal:edit", id = second, scope = "following", title = "Molten Core", start = Ms(2026, 10, 24, 19), slots = new { tank = 2, healer = 3, dps = 5 }, tz = "Europe/Berlin", off = 120, durationMin = 180 });
        var stored = r.Hub.Calendar.Store.Events.OrderBy(Cal.Start).ToList();
        Assert.Equal(Oct17_18Z, Cal.Start(stored[0]));                       // the first is untouched
        Assert.Equal(Ms(2026, 10, 24, 19), Cal.Start(stored[1]));
        Assert.Equal(Ms(2026, 10, 31, 20), Cal.Start(stored[2]));            // 21:00 CET
        Assert.Equal(Ms(2026, 11, 7, 20), Cal.Start(stored[3]));
        r.Clock.Advance(TimeSpan.FromSeconds(3));
        await r.Send(elan, new { t = "cal:cancel", id = (string)stored[2]["id"]!, scope = "following" });
        Assert.Equal(new[] { "open", "open", "cancelled", "cancelled" }, r.Hub.Calendar.Store.Events.OrderBy(Cal.Start).Select(e => (string)e["status"]!).ToArray());
    }

    [Fact]
    public async Task An_open_ended_series_is_topped_up_26_weeks_ahead_and_stops_when_cancelled()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner");
        var evs = Events(await Create(r, elan, Ev(Oct17_18Z, repeat: new { open = true })));
        Assert.Equal(26, evs.Count);
        await r.Hub.Calendar.Tick();
        Assert.Equal(26, r.Hub.Calendar.Store.Events.Count);                      // nothing to add yet
        r.Clock.Advance(TimeSpan.FromDays(28));
        r.Sock("Elan").Clear();
        await r.Hub.Calendar.Tick();
        Assert.Equal(2, Events(r.Sock("Elan").Of("cal:events").Last()).Count);   // two more weeks to keep 26 ahead
        Assert.Equal(28, r.Hub.Calendar.Store.Events.Count);
        var first = r.Hub.Calendar.Store.Events.OrderBy(Cal.Start).First();
        r.Clock.Advance(TimeSpan.FromSeconds(3));
        await r.Send(elan, new { t = "cal:cancel", id = (string)first["id"]!, scope = "following" });
        r.Clock.Advance(TimeSpan.FromDays(14));
        await r.Hub.Calendar.Tick();
        Assert.Equal(28, r.Hub.Calendar.Store.Events.Count);                      // nothing added after the cancel
    }

    // ---------------------------------------------------------------- limits and pruning

    [Fact]
    public async Task At_most_200_future_events_and_a_per_person_bound()
    {
        using var r = new FunRig();
        var owner = r.Join("Elan", "owner");
        var people = Enumerable.Range(0, 6).Select(i => r.Join("Org" + i, "member")).ToList();
        int made = 0;
        foreach (var p in people)
            for (int i = 0; i < 40 && made < 200; i++, made++)
            {
                p.Budget.Chat.TryTake(); // keep the budget out of this test
                r.Clock.Advance(TimeSpan.FromSeconds(2));
                await r.Send(p, Ev(Oct17_18Z + 86_400_000L * (made % 300), title: "Ev" + made, post: false));
            }
        Assert.Equal(200, r.Hub.Calendar.Store.Events.Count);
        r.Clock.Advance(TimeSpan.FromSeconds(5)); r.Sock("Elan").Clear();
        await r.Send(owner, Ev(Oct17_18Z, post: false));
        Assert.Contains("full", Err(r, "Elan"));
        Assert.Equal(200, r.Hub.Calendar.Store.Events.Count);

        // a series that would not fit is refused whole, nothing half-made
        r.Hub.Calendar.Store.Events.RemoveRange(0, 10);
        r.Clock.Advance(TimeSpan.FromSeconds(5));
        await r.Send(owner, Ev(Oct17_18Z, repeat: new { weeks = 20 }, post: false));
        Assert.Equal(190, r.Hub.Calendar.Store.Events.Count);
    }

    [Fact]
    public async Task One_person_cannot_hold_more_than_40_upcoming_events()
    {
        using var r = new FunRig();
        var pat = r.Join("Pat", "member");
        for (int i = 0; i < 41; i++)
        {
            r.Clock.Advance(TimeSpan.FromSeconds(2));
            await r.Send(pat, Ev(Oct17_18Z + i * 3_600_000L, post: false));
        }
        Assert.Equal(Cal.MaxOpenPerAuthor, r.Hub.Calendar.Store.Events.Count);
    }

    [Fact]
    public void Past_events_are_pruned_30_days_after_they_end_not_before()
    {
        using var dir = new TempDir();
        var clock = new FakeClock();
        var store = new CalendarStore(dir.Path, T.Log, clock);
        var now = store.NowMs;
        JsonObject E(string id, long start, long end) => new() { ["id"] = id, ["title"] = id, ["start"] = start, ["end"] = end, ["status"] = "open", ["signups"] = new JsonArray() };
        const long Day = 86_400_000;
        store.Events.AddRange(new[]
        {
            E("old-ended", now - 40 * Day, now - 40 * Day + 3_600_000),   // ended 40 days ago
            E("recent", now - 29 * Day, now - 29 * Day + 3_600_000),      // ended 29 days ago
            E("noend-old", now - 31 * Day, 0),                            // no end: counts from its start
            E("noend-ok", now - 29 * Day, 0),
            E("long", now - 31 * Day, now - 29 * Day),                    // started long ago but ended 29 days ago
            E("future", now + Day, 0),
        });
        Assert.Equal(2, store.Prune(now));
        Assert.Equal(new[] { "future", "long", "noend-ok", "recent" }, store.Events.Select(e => (string)e["id"]!).OrderBy(x => x).ToArray());
        Assert.Equal(3, store.Prune(now + 2 * Day));   // two days later all three that ended 29 days ago are 31 days past their end
        Assert.Equal(new[] { "future" }, store.Events.Select(e => (string)e["id"]!).ToArray());
        Assert.True(store.Save());
        Assert.Equal(store.Events.Count, new CalendarStore(dir.Path, T.Log, clock).Events.Count);
    }

    [Fact]
    public async Task The_housekeeping_tick_prunes_and_a_damaged_file_is_survived()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner");
        await Create(r, elan, Ev(Oct17_18Z));
        r.Clock.Advance(TimeSpan.FromDays(60));
        await r.Hub.Calendar.Tick();
        Assert.Empty(r.Hub.Calendar.Store.Events);
        File.WriteAllText(Path.Combine(r.Dir.Path, "calendar.json"), "{ not json");
        Assert.Empty(new CalendarStore(r.Dir.Path, T.Log, r.Clock).Events);
        File.WriteAllText(Path.Combine(r.Dir.Path, "calendar.json"), "{\"v\":1,\"events\":[1,\"x\",{\"id\":\"a\"},{\"id\":\"b\",\"title\":\"ok\",\"start\":5}]}");
        Assert.Empty(new CalendarStore(r.Dir.Path, T.Log, r.Clock).Events); // start long ago -> pruned; junk entries skipped
    }

    // ---------------------------------------------------------------- welcome, range, fallback, privacy

    [Fact]
    public async Task The_welcome_carries_the_next_30_days_and_the_feature_and_a_range_fetches_a_month()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner"); var pat = r.Join("Pat (guest)", "guest");
        await Create(r, elan, Ev(r.NowMs + 5 * 86_400_000L, title: "Soon"));
        await Create(r, elan, Ev(r.NowMs + 45 * 86_400_000L, title: "Later"));
        var w = r.Hub.Welcome(pat);
        Assert.Contains("calendar", ((JsonArray)w["features"]!).Select(x => (string)x!));
        var titles = ((JsonArray)w["calendar"]!["events"]!).Select(x => (string)x!["title"]!).ToList();
        Assert.Equal(new[] { "Soon" }, titles);
        Assert.True((long)w["calendar"]!["now"]! > 0);

        r.Sock("Elan").Clear();
        await r.Send(elan, new { t = "cal:range", from = r.NowMs + 30 * 86_400_000L, to = r.NowMs + 60 * 86_400_000L });
        Assert.Equal(new[] { "Later" }, ((JsonArray)r.Sock("Elan").Of("cal:range").Single()["events"]!).Select(x => (string)x!["title"]!).ToArray());
        r.Sock("Elan").Clear();
        await r.Send(elan, new { t = "cal:range", from = 1, to = r.NowMs + 400 * 86_400_000L });
        Assert.Empty(r.Sock("Elan").Of("cal:range"));      // more than 62 days: refused
        Assert.NotNull(r.Sock("Elan").LastError());
    }

    [Fact]
    public async Task The_chat_card_has_a_readable_fallback_that_follows_the_event_and_cannot_be_edited_by_hand()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner"); var bob = r.Join("Bob", "officer");
        await Create(r, elan, Ev(Oct17_18Z, slots: new { tank = 1, healer = 1, dps = 3 }, note: "bring water", repeat: new { weeks = 2 }));
        var card = r.Sock("Bob").Of("msg").Last();
        Assert.Equal("event", (string)card["kind"]!);
        Assert.Equal("general", (string)card["channel"]!);
        var text = (string)card["text"]!;
        Assert.StartsWith("[Event] Molten Core - Sat 17 Oct 18:00 UTC - Tank 0/1, Healer 0/1, DPS 0/3", text);
        Assert.Contains("weekly", text); Assert.Contains("bring water", text); Assert.Contains("Calendar", text);
        Assert.Equal(Oct17_18Z, (long)card["event"]!["start"]!);
        var id = (string)Events(r.Sock("Elan").Of("cal:events").Last())[0]["id"]!;

        r.Sock("Bob").Clear();
        await r.Send(bob, new { t = "cal:signup", id, status = "going", role = "tank" });
        var edited = r.Sock("Bob").Of("edited").Last();
        Assert.Contains("Tank 1/1", (string)edited["text"]!);
        Assert.Equal((string)card["id"]!, (string)edited["id"]!);

        r.Clock.Advance(TimeSpan.FromSeconds(3));
        await r.Send(elan, new { t = "cal:cancel", id });
        Assert.Contains("(cancelled)", (string)r.Sock("Bob").Of("edited").Last()["text"]!);
        Assert.DoesNotContain("to sign up", (string)r.Sock("Bob").Of("edited").Last()["text"]!);

        // the card is not an ordinary message: nobody edits its text by hand
        r.Sock("Elan").Clear();
        await r.Send(elan, new { t = "edit", id = (string)card["id"]!, text = "hello" });
        Assert.Equal("cal", (string?)r.Sock("Elan").LastError()!["code"]);

        // it survives in history for a reconnecting client
        var hist = ((JsonArray)r.Hub.Welcome(bob)["history"]!["general"]!).Select(x => (JsonObject)x!).Single(x => (string)x["id"]! == (string)card["id"]!);
        Assert.Equal("event", (string)hist["kind"]!);
        Assert.Contains("(cancelled)", (string)hist["text"]!);
    }

    [Fact]
    public async Task The_card_goes_to_the_chosen_channel_only_if_it_may_and_not_at_all_when_switched_off()
    {
        using var r = new FunRig();
        var pat = r.Join("Pat", "member"); var bob = r.Join("Bob", "officer");
        Assert.Null(await Create(r, pat, Ev(Oct17_18Z, channel: "officers")));       // Pat can't see #officers
        Assert.Null(await Create(r, pat, Ev(Oct17_18Z, channel: "hangout")));        // a voice room
        Assert.Null(await Create(r, pat, Ev(Oct17_18Z, channel: "nope")));
        r.Sock("Bob").Clear();
        Assert.NotNull(await Create(r, pat, Ev(Oct17_18Z, channel: "loot")));
        Assert.Equal("loot", (string)r.Sock("Bob").Of("msg").Last()["channel"]!);
        r.Sock("Bob").Clear();
        Assert.NotNull(await Create(r, pat, Ev(Oct17_18Z + 86_400_000, post: false)));
        Assert.Empty(r.Sock("Bob").Of("msg"));
    }

    [Fact]
    public async Task Sign_up_lists_show_an_invisible_member_by_name_and_the_calendar_never_carries_presence()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner"); var ghost = r.Join("Ghost", "member"); var pat = r.Join("Pat", "member");
        ghost.Invisible = true; ghost.Status = "dungeon"; ghost.Game = new JsonObject { ["name"] = "Ghostly", ["class"] = "Priest", ["zone"] = "Secret Zone" };
        var id = (string)Events(await Create(r, elan, Ev(Oct17_18Z))).Single()["id"]!;
        r.Sock("Pat").Clear();
        await r.Send(ghost, new { t = "cal:signup", id, status = "going", role = "healer" });
        var seen = r.Sock("Pat").Of("cal:events").Last().ToJsonString();
        Assert.Contains("\"n\":\"Ghost\"", seen);                     // a sign-up is an explicit act
        foreach (var leak in new[] { "dungeon", "Ghostly", "Secret Zone", "invisible", "online", "Priest" }) Assert.DoesNotContain(leak, seen);
        // ...while the roster still hides them from a viewer who may not see them
        Assert.DoesNotContain(((JsonArray)r.Hub.Welcome(pat)["users"]!), u => (string)u!["name"]! == "Ghost");
        Assert.DoesNotContain("Ghost", r.Hub.Welcome(pat)["users"]!.ToJsonString());
        Assert.Contains("Ghost", r.Hub.Welcome(pat)["calendar"]!.ToJsonString());
    }

    // ---------------------------------------------------------------- rate limits and logs

    [Fact]
    public async Task Creating_editing_and_cancelling_spend_the_chat_budget_and_a_month_fetch_the_page_budget()
    {
        using var r = new FunRig();
        var pat = r.Join("Pat", "member");
        int ok = 0;
        for (int i = 0; i < 12; i++)
        {
            r.Sock("Pat").Clear();
            await r.Send(pat, Ev(Oct17_18Z + i * 3_600_000L, post: false));
            if (r.Sock("Pat").Of("cal:events").Count > 0) ok++;
        }
        Assert.Equal(8, ok);                                     // the Chat bucket's burst
        Assert.Equal("rate", (string?)r.Sock("Pat").LastError()!["code"]);
        var id = (string)r.Hub.Calendar.Store.Events[0]["id"]!;
        r.Sock("Pat").Clear();
        await r.Send(pat, new { t = "cal:cancel", id });
        Assert.Equal("rate", (string?)r.Sock("Pat").LastError()!["code"]);
        Assert.Equal("open", (string)Stored(r, id)["status"]!);

        for (int i = 0; i < 15; i++) await r.Send(pat, new { t = "cal:range", from = Oct17_18Z, to = Oct17_18Z + 86_400_000L });
        Assert.Equal(10, r.Sock("Pat").Of("cal:range").Count);   // the Page bucket's burst
        r.Clock.Advance(TimeSpan.FromSeconds(30));
        r.Sock("Pat").Clear();
        await r.Send(pat, Ev(Oct17_18Z + 20 * 3_600_000L, post: false));
        Assert.NotEmpty(r.Sock("Pat").Of("cal:events"));         // refilled
    }

    [Fact]
    public async Task Event_text_never_reaches_the_logs()
    {
        var log = new CaptureLogger();
        using var r = new FunRig(log: log);
        var elan = r.Join("Elan", "owner"); var pat = r.Join("Pat", "member");
        var id = (string)Events(await Create(r, elan, Ev(Oct17_18Z, title: "SECRETTITLE", note: "SECRETNOTE"))).Single()["id"]!;
        await r.Send(pat, new { t = "cal:signup", id, status = "going", role = "dps", comment = "SECRETCOMMENT" });
        r.Clock.Advance(TimeSpan.FromSeconds(3));
        await r.Send(elan, new { t = "cal:cancel", id });
        File.WriteAllText(Path.Combine(r.Dir.Path, "calendar.json"), "{ SECRETFILE");
        _ = new CalendarStore(r.Dir.Path, log, r.Clock);
        string all; lock (log.Lines) all = string.Join("\n", log.Lines);
        Assert.Contains("created calendar event", all);
        foreach (var s in new[] { "SECRET", T.ElanCode, T.BobCode }) Assert.DoesNotContain(s, all);
    }
}
