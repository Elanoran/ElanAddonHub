using System.Text.Json.Nodes;
using Xunit;

namespace Lodge.Tests;

// Lodge 2.9: "Looking for group" sign-up cards
public class LfgTests
{
    static object Card(object? slots = null, string title = "Deadmines", long? start = null, bool now = true, string note = "bring water", string channel = "general")
        => new { t = "msg", channel, kind = "lfg", text = "ignored", lfg = new { title, now, start, note, slots = slots ?? new { tank = 1, healer = 1, dps = 3 } } };

    static async Task<string> Post(FunRig r, Member m, object card)
    {
        r.Clock.Advance(TimeSpan.FromSeconds(3));
        r.Sock(m.Name).Clear();
        await r.Do(m, card);
        var sent = r.Sock(m.Name).Of("msg");
        return sent.Count == 0 ? "" : (string)sent.Last()["id"]!;
    }

    static JsonObject Lfg(FunRig r, string viewer, string id) => (JsonObject)r.Sock(viewer).Of("lfg").Last(x => (string)x["id"]! == id)["lfg"]!;

    [Fact]
    public async Task A_valid_card_is_broadcast_with_a_payload_and_a_plain_text_fallback_and_survives_a_restart()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner"); r.Join("Bob", "officer");
        var id = await Post(r, elan, Card());
        Assert.NotEqual("", id);
        var msg = r.Sock("Bob").Of("msg").Last();
        Assert.Equal("lfg", (string)msg["kind"]!);
        Assert.Equal("open", (string)msg["lfg"]!["status"]!);
        Assert.Equal(5, (int)msg["lfg"]!["slots"]!["tank"]! + (int)msg["lfg"]!["slots"]!["healer"]! + (int)msg["lfg"]!["slots"]!["dps"]!);
        var text = (string)msg["text"]!;
        Assert.StartsWith("[Looking for group] Deadmines - now - Tank 0/1, Healer 0/1, DPS 0/3", text); // what an old client shows
        Assert.Contains("bring water", text);
        Assert.DoesNotContain("ignored", text);

        r.Hub.Chat.FlushAll();
        var again = new HistoryStore(Path.Combine(r.Dir.Path, "history"), "general", 1 << 20, T.Log);
        var stored = again.Messages.Single(x => (string)x["id"]! == id);
        Assert.Equal("lfg", (string)stored["kind"]!);
        Assert.Equal("Deadmines", (string)stored["lfg"]!["title"]!);
    }

    [Fact]
    public async Task Fallback_text_reflects_the_picked_time_signups_and_closing()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner"); var bob = r.Join("Bob", "officer");
        var start = r.NowMs + 3_600_000;
        var id = await Post(r, elan, Card(title: "Molten Core", now: false, start: start, slots: new { tank = 2, healer = 0, dps = 0 }));
        await r.Do(bob, new { t = "lfg:join", id, role = "tank" });
        var text = (string)r.Sock("Elan").Of("lfg").Last()["text"]!;
        Assert.Contains("Molten Core", text);
        Assert.Contains("UTC", text);
        Assert.Contains("Tank 1/2", text);
        Assert.DoesNotContain("Healer", text);
        await r.Do(elan, new { t = "lfg:cancel", id });
        var last = (string)r.Sock("Elan").Of("lfg").Last()["text"]!;
        Assert.Contains("(cancelled)", last);
        Assert.DoesNotContain("sign up in", last);
    }

    [Theory]
    [InlineData(41, 0, 0)]
    [InlineData(-1, 1, 1)]
    [InlineData(0, 0, 0)]
    public async Task Role_counts_are_bounded_and_a_group_needs_a_slot(int tank, int healer, int dps)
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner");
        var id = await Post(r, elan, Card(slots: new { tank, healer, dps }));
        Assert.Equal("", id);
        Assert.Equal("lfg", (string?)r.Sock("Elan").LastError()!["code"]);
    }

    [Fact]
    public async Task Forty_of_a_role_is_allowed_and_junk_values_are_refused()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner");
        Assert.NotEqual("", await Post(r, elan, Card(slots: new { tank = 0, healer = 0, dps = 40 })));
        Assert.Equal("", await Post(r, elan, Card(slots: new { tank = "many", healer = 1, dps = 1 })));
        Assert.Equal("", await Post(r, elan, Card(slots: new { tank = 1.5, healer = 1, dps = 1 })));
        Assert.Equal("", await Post(r, elan, Card(slots: new { tank = new[] { 1 }, healer = 1, dps = 1 })));
    }

    [Fact]
    public async Task Text_fields_are_cleaned_and_bounded_and_a_title_is_required()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner"); var guest = r.Join("Pat", "member");
        Assert.Equal("", await Post(r, elan, Card(title: "   ")));
        var id = await Post(r, elan, Card(title: new string('x', 500), note: new string('n', 900) + "‮"));
        Assert.NotEqual("", id);
        var l = (JsonObject)r.Sock("Elan").Of("msg").Last()["lfg"]!;
        Assert.Equal(Lodge.Lfg.MaxTitle, ((string)l["title"]!).Length);
        Assert.Equal(Lodge.Lfg.MaxNote, ((string)l["note"]!).Length);
        // @everyone is for officers and up, also inside a card
        await Post(r, guest, Card(title: "@everyone come", note: "@here"));
        var g = (JsonObject)r.Sock("Pat").Of("msg").Last()["lfg"]!;
        Assert.False(((string)g["title"]!).Contains("@everyone", StringComparison.Ordinal));
        Assert.False(((string)g["note"]!).Contains("@here", StringComparison.Ordinal));
        Assert.False(((string)r.Sock("Pat").Of("msg").Last()["text"]!).Contains("@everyone", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_start_time_must_be_sensible_and_a_malformed_payload_is_refused()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner");
        Assert.Equal("", await Post(r, elan, Card(now: false, start: r.NowMs - 3_600_000)));
        Assert.Equal("", await Post(r, elan, Card(now: false, start: r.NowMs + 40L * 86_400_000)));
        Assert.NotEqual("", await Post(r, elan, Card(now: false, start: r.NowMs + 600_000)));
        var at = (long)r.Sock("Elan").Of("msg").Last()["lfg"]!["start"]!;
        Assert.InRange(at, r.NowMs + 590_000, r.NowMs + 610_000);
        r.Sock("Elan").Clear();
        await r.Do(elan, new { t = "msg", channel = "general", kind = "lfg", text = "hi" });
        Assert.Empty(r.Sock("Elan").Of("msg"));
        Assert.NotNull(r.Sock("Elan").LastError());
    }

    [Fact]
    public async Task One_slot_per_person_and_moving_is_atomic()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner"); var bob = r.Join("Bob", "officer"); var pat = r.Join("Pat", "member");
        var id = await Post(r, elan, Card(slots: new { tank = 1, healer = 1, dps = 1 }));
        await r.Do(bob, new { t = "lfg:join", id, role = "tank" });
        await r.Do(bob, new { t = "lfg:join", id, role = "healer" }); // moves, doesn't add
        var l = Lfg(r, "Elan", id);
        Assert.Single((JsonArray)l["signups"]!);
        Assert.Equal("healer", (string)l["signups"]![0]!["r"]!);
        // Pat wants the healer slot that Bob holds: refused, and Pat's old place (maybe) stays
        await r.Do(pat, new { t = "lfg:maybe", id });
        await r.Do(pat, new { t = "lfg:join", id, role = "healer" });
        Assert.Equal("lfg-full", (string?)r.Sock("Pat").LastError()!["code"]);
        l = Lfg(r, "Elan", id);
        Assert.Equal(2, ((JsonArray)l["signups"]!).Count);
        Assert.Equal("maybe", (string)l["signups"]!.AsArray().First(x => (string)x!["n"]! == "Pat")!["r"]!);
        // a made-up role changes nothing
        await r.Do(pat, new { t = "lfg:join", id, role = "bard" });
        Assert.Equal(2, ((JsonArray)Lfg(r, "Elan", id)["signups"]!).Count);
        // leaving frees the slot
        await r.Do(bob, new { t = "lfg:leave", id });
        await r.Do(pat, new { t = "lfg:join", id, role = "healer" });
        Assert.Equal("healer", (string)Lfg(r, "Elan", id)["signups"]![0]!["r"]!);
    }

    [Fact]
    public async Task Two_people_taking_the_last_slot_at_the_same_moment_get_one_slot()
    {
        for (int round = 0; round < 300; round++)
        {
            using var r = new FunRig();
            var elan = r.Join("Elan", "owner");
            var a = r.Join("Alice", "member"); var b = r.Join("Bob", "member");
            var id = await Post(r, elan, Card(slots: new { tank = 0, healer = 0, dps = 1 }));
            var go = new TaskCompletionSource();
            var t1 = Task.Run(async () => { await go.Task; await r.Do(a, new { t = "lfg:join", id, role = "dps" }); });
            var t2 = Task.Run(async () => { await go.Task; await r.Do(b, new { t = "lfg:join", id, role = "dps" }); });
            go.SetResult();
            await Task.WhenAll(t1, t2);
            var l = Lfg(r, "Elan", id);
            Assert.Single((JsonArray)l["signups"]!);
            Assert.Equal(1, r.Sock("Alice").Of("error").Count + r.Sock("Bob").Of("error").Count);
        }
    }

    [Fact]
    public async Task Many_simultaneous_joins_never_exceed_the_slots_or_double_up_a_person()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner");
        var id = await Post(r, elan, Card(slots: new { tank = 1, healer = 1, dps = 3 }));
        var people = Enumerable.Range(0, 12).Select(i => r.Join("P" + i, "member")).ToList();
        var go = new TaskCompletionSource();
        var tasks = people.SelectMany(p => new[] { "tank", "healer", "dps" }.Select(role => Task.Run(async () => { await go.Task; await r.Do(p, new { t = "lfg:join", id, role }); }))).ToList();
        go.SetResult();
        await Task.WhenAll(tasks);
        var s = (JsonArray)Lfg(r, "Elan", id)["signups"]!;
        Assert.True(s.Count(x => (string)x!["r"]! == "tank") <= 1);
        Assert.True(s.Count(x => (string)x!["r"]! == "healer") <= 1);
        Assert.True(s.Count(x => (string)x!["r"]! == "dps") <= 3);
        Assert.Equal(s.Count, s.Select(x => (string)x!["n"]!).Distinct().Count());
    }

    // regression for the flaky "Collection was modified" (2.22): the handlers read the card JSON while another join was setting its text
    [Fact]
    public async Task Concurrent_joins_leaves_and_closes_never_throw()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner");
        var people = Enumerable.Range(0, 16).Select(i => r.Join("Q" + i, "member")).ToList();
        for (int round = 0; round < 300; round++)
        {
            var id = await Post(r, elan, Card(slots: new { tank = 2, healer = 2, dps = 8 }));
            var go = new TaskCompletionSource();
            var tasks = people.SelectMany(p => new[] { "join", "leave", "maybe" }.Select(kind => Task.Run(async () =>
            {
                await go.Task;
                if (kind == "join") await r.Do(p, new { t = "lfg:join", id, role = "dps" });
                else if (kind == "maybe") await r.Do(p, new { t = "lfg:maybe", id });
                else await r.Do(p, new { t = "lfg:leave", id });
            }))).Append(Task.Run(async () => { await go.Task; await r.Do(elan, new { t = "lfg:close", id }); })).ToList();
            go.SetResult();
            await Task.WhenAll(tasks);   // an exception in any handler fails the test here
        }
    }

    [Fact]
    public async Task Only_the_author_or_an_officer_can_close_or_cancel_and_a_finished_card_cannot_change()
    {
        using var r = new FunRig();
        var pat = r.Join("Pat", "member"); var lee = r.Join("Lee", "member"); var bob = r.Join("Bob", "officer");
        var id = await Post(r, pat, Card());
        await r.Do(lee, new { t = "lfg:close", id });
        Assert.NotNull(r.Sock("Lee").LastError());
        Assert.Empty(r.Sock("Pat").Of("lfg"));
        await r.Do(lee, new { t = "lfg:join", id, role = "dps" });
        await r.Do(bob, new { t = "lfg:close", id }); // an officer may
        Assert.Equal("closed", (string)Lfg(r, "Pat", id)["status"]!);
        var before = r.Sock("Pat").Of("lfg").Count;
        r.Sock("Lee").Clear();
        await r.Do(lee, new { t = "lfg:join", id, role = "tank" });
        await r.Do(lee, new { t = "lfg:leave", id });
        await r.Do(pat, new { t = "lfg:cancel", id });   // even the author can't turn closed into cancelled
        await r.Do(pat, new { t = "lfg:close", id });
        Assert.Equal(before, r.Sock("Pat").Of("lfg").Count);
        Assert.Equal("closed", (string)Lfg(r, "Pat", id)["status"]!);
        Assert.Single((JsonArray)Lfg(r, "Pat", id)["signups"]!);
        Assert.Equal(2, r.Sock("Lee").Of("error").Count);
    }

    [Fact]
    public async Task A_card_in_a_channel_you_cannot_see_does_not_exist_for_you()
    {
        using var r = new FunRig();
        var bob = r.Join("Bob", "officer"); var pat = r.Join("Pat", "member");
        var id = await Post(r, bob, Card(channel: "officers", slots: new { tank = 1 }));
        Assert.NotEqual("", id);
        Assert.Empty(r.Sock("Pat").Of("msg"));
        await r.Do(pat, new { t = "lfg:join", id, role = "tank" });
        await r.Do(pat, new { t = "lfg:close", id });
        Assert.Empty(r.Sock("Pat").Of("lfg"));
        Assert.Empty(r.Sock("Pat").Of("error"));
        Assert.Empty(r.Sock("Bob").Of("lfg"));
        // and a member can't post into it
        await Post(r, pat, Card(channel: "officers"));
        Assert.NotNull(r.Sock("Pat").LastError());
    }

    [Fact]
    public async Task Cards_cannot_be_edited_and_cannot_be_forged_through_plain_messages()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner");
        var id = await Post(r, elan, Card());
        await r.Do(elan, new { t = "edit", id, text = "something else" });
        Assert.Contains("can't be edited", (string)r.Sock("Elan").LastError()!["text"]!);
        // a plain message with an lfg field but no kind is just text
        r.Clock.Advance(TimeSpan.FromSeconds(3));
        await r.Do(elan, new { t = "msg", channel = "general", text = "hello", lfg = new { title = "fake" } });
        Assert.Null(r.Sock("Elan").Of("msg").Last()["lfg"]);
        // joining something that isn't a card does nothing
        var plain = (string)r.Sock("Elan").Of("msg").Last()["id"]!;
        r.Sock("Elan").Clear();
        await r.Do(elan, new { t = "lfg:join", id = plain, role = "tank" });
        Assert.Empty(r.Sock("Elan").Of("lfg"));
    }

    [Fact]
    public async Task At_most_three_open_cards_per_author_and_creating_spends_the_chat_budget()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner");
        for (int i = 0; i < 3; i++) Assert.NotEqual("", await Post(r, elan, Card(title: "Run " + i)));
        Assert.Equal("", await Post(r, elan, Card(title: "One too many")));
        Assert.Contains("3 open", (string)r.Sock("Elan").LastError()!["text"]!);
        // closing one makes room
        r.Sock("Elan").Clear();
        var all = r.Hub.Chat.HistoryFor("general");
        var firstId = (string)all[0]!["id"]!;
        await r.Do(elan, new { t = "lfg:close", id = firstId });
        Assert.NotEqual("", await Post(r, elan, Card(title: "Again")));

        // the chat budget (8 per 10 s) applies to cards like any message
        using var r2 = new FunRig();
        var bob = r2.Join("Bob", "officer");
        for (int i = 0; i < 12; i++)
        {
            await r2.Do(bob, Card(title: "x" + i));
            var last = r2.Sock("Bob").Of("msg").LastOrDefault();
            if (last != null) await r2.Do(bob, new { t = "lfg:close", id = (string)last["id"]! });
        }
        Assert.Equal(8, r2.Sock("Bob").Of("msg").Count);
        Assert.Contains(r2.Sock("Bob").Of("error"), e => (string?)e["code"] == "rate");
    }

    [Fact]
    public void The_fallback_is_plain_and_complete_for_a_full_group()
    {
        var lfg = Lodge.Lfg.Create(JsonNode.Parse("{\"title\":\"Stratholme\",\"now\":true,\"slots\":{\"tank\":1,\"healer\":1,\"dps\":3},\"note\":\"live side\"}")!.AsObject(), "Elan", 1_700_000_000_000, true, out var err)!;
        Assert.Null(err);
        Lodge.Lfg.Join(lfg, "Pat", "tank"); Lodge.Lfg.Join(lfg, "Lee", "maybe");
        Assert.Equal("[Looking for group] Stratholme - now - Tank 1/1, Healer 0/1, DPS 0/3, 1 maybe - live side (sign up in Elan's Outpost)", Lodge.Lfg.Fallback(lfg));
    }
}
