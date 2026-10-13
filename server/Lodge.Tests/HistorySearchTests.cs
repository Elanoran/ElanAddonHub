using System.Diagnostics;
using System.Text.Json.Nodes;
using Xunit;

namespace Lodge.Tests;

// Lodge 2.9: longer history (configurable cap), "history:before" paging, search
public class HistorySearchTests
{
    static JsonObject Page(FunRig r, Member who, string channel, string? before, int? limit = null, long? beforeAt = null)
    {
        r.Clock.Advance(TimeSpan.FromSeconds(2));
        r.Sock(who.Name).Clear();
        r.Do(who, new { t = "history:before", channel, before, limit, beforeAt }).GetAwaiter().GetResult();
        return r.Sock(who.Name).Of("history:page").Last();
    }

    static JsonObject Find(FunRig r, Member who, string q, string? channel = null)
    {
        r.Clock.Advance(TimeSpan.FromSeconds(11));   // the helper stays inside the search budget
        r.Sock(who.Name).Clear();
        r.Do(who, new { t = "search", q, channel }).GetAwaiter().GetResult();
        return r.Sock(who.Name).Of("search:result").Last();
    }

    static string[] Ids(JsonObject page) => ((JsonArray)page["messages"]!).Select(x => (string)x!["id"]!).ToArray();
    static string[] Texts(JsonObject page) => ((JsonArray)page["messages"]!).Select(x => (string)x!["text"]!).ToArray();

    static async Task<List<string>> Fill(FunRig r, Member who, int n, string channel = "general", string prefix = "m")
    {
        var ids = new List<string>();
        for (int i = 1; i <= n; i++) ids.Add(await r.Say(who, $"{prefix}{i:D4}", channel));
        return ids;
    }

    [Fact]
    public void The_history_cap_defaults_to_2000_and_is_read_from_LODGE_HISTORY_MAX()
    {
        using var d = new TempDir();
        string? Get(string k) => Environment.GetEnvironmentVariable(k);
        var had = (Get("LODGE_HISTORY_MAX"), Get("LODGE_DATA"));
        try
        {
            Environment.SetEnvironmentVariable("LODGE_DATA", d.Path);
            Environment.SetEnvironmentVariable("LODGE_HISTORY_MAX", null);
            Assert.Equal(2000, LodgeConfig.FromEnv().HistoryMax);
            Environment.SetEnvironmentVariable("LODGE_HISTORY_MAX", "5000");
            Assert.Equal(5000, LodgeConfig.FromEnv().HistoryMax);
            Environment.SetEnvironmentVariable("LODGE_HISTORY_MAX", "3");
            Assert.Equal(50, LodgeConfig.FromEnv().HistoryMax);          // clamped: at least 50
            Environment.SetEnvironmentVariable("LODGE_HISTORY_MAX", "1000000");
            Assert.Equal(50000, LodgeConfig.FromEnv().HistoryMax);       // and at most 50 000
            Environment.SetEnvironmentVariable("LODGE_HISTORY_MAX", "lots");
            Assert.Equal(2000, LodgeConfig.FromEnv().HistoryMax);
            Assert.Equal(HistoryStore.DefaultKeep, 2000);
        }
        finally { Environment.SetEnvironmentVariable("LODGE_HISTORY_MAX", had.Item1); Environment.SetEnvironmentVariable("LODGE_DATA", had.Item2); }
    }

    [Fact]
    public async Task A_channel_keeps_the_configured_number_of_messages_while_the_welcome_still_sends_100()
    {
        using var r = new FunRig(c => c.HistoryMax = 300);
        var elan = r.Join("Elan", "owner");
        var ids = await Fill(r, elan, 350);
        var welcome = r.Hub.Welcome(elan);
        var sent = (JsonArray)welcome["history"]!["general"]!;
        Assert.Equal(100, sent.Count);
        Assert.Equal(ids[^1], (string)sent[^1]!["id"]!);
        // 300 are kept: paging all the way back reaches message 51 and no further
        var first = Page(r, elan, "general", (string)sent[0]!["id"]!);
        Assert.Equal(100, Ids(first).Length);
        r.Hub.Chat.FlushAll();
        var reloaded = new HistoryStore(Path.Combine(r.Dir.Path, "history"), "general", 1 << 20, T.Log, 300);
        Assert.Equal(300, reloaded.Messages.Count);
        Assert.Equal(ids[50], (string)reloaded.Messages[0]["id"]!);
    }

    [Fact]
    public async Task Paging_back_returns_contiguous_pages_oldest_first_and_says_when_there_is_no_more()
    {
        using var r = new FunRig(c => c.HistoryMax = 300);
        var elan = r.Join("Elan", "owner");
        var ids = await Fill(r, elan, 350);                       // 51..350 are kept
        var welcome = (JsonArray)r.Hub.Welcome(elan)["history"]!["general"]!;
        var oldest = (string)welcome[0]!["id"]!;                  // 251
        Assert.Equal(ids[250], oldest);

        var p1 = Page(r, elan, "general", oldest);
        Assert.Equal(ids.Skip(150).Take(100), Ids(p1));           // 151..250, oldest first
        Assert.True((bool)p1["hasMore"]!);
        Assert.Equal("general", (string)p1["channel"]!);
        Assert.Equal(oldest, (string)p1["before"]!);

        var p2 = Page(r, elan, "general", Ids(p1)[0]);
        Assert.Equal(ids.Skip(50).Take(100), Ids(p2));            // 51..150
        Assert.False((bool)p2["hasMore"]!);                       // that was the oldest the server has

        var p3 = Page(r, elan, "general", Ids(p2)[0]);
        Assert.Empty(Ids(p3));
        Assert.False((bool)p3["hasMore"]!);

        // every message is in exactly one page
        var all = Ids(p2).Concat(Ids(p1)).Concat(welcome.Select(x => (string)x!["id"]!)).ToList();
        Assert.Equal(300, all.Distinct().Count());
        Assert.Equal(ids.Skip(50), all);
    }

    [Fact]
    public async Task The_page_size_is_bounded_and_odd_requests_are_harmless()
    {
        using var r = new FunRig(c => c.HistoryMax = 300);
        var elan = r.Join("Elan", "owner");
        var ids = await Fill(r, elan, 250);
        Assert.Equal(100, Ids(Page(r, elan, "general", ids[^1], 5000)).Length);       // at most 100
        Assert.Equal(10, Ids(Page(r, elan, "general", ids[^1], 10)).Length);
        Assert.Single(Ids(Page(r, elan, "general", ids[^1], 0)));                      // at least 1
        Assert.Single(Ids(Page(r, elan, "general", ids[^1], -7)));
        Assert.Empty(Ids(Page(r, elan, "general", "no-such-id")));                     // unknown id: nothing, no error
        Assert.Empty(Ids(Page(r, elan, "general", null)));
        Assert.Empty(Ids(Page(r, elan, "no-such-channel", ids[^1])));
        Assert.Empty(Ids(Page(r, elan, "hangout", ids[^1])));                          // a voice room has no history
        Assert.Empty(r.Sock("Elan").Of("error"));
        // a malformed payload costs nothing but that message
        r.Sock("Elan").Clear();
        await r.Do(elan, new { t = "history:before", channel = new[] { 1 }, before = 5, limit = "x" });
        Assert.Empty(r.Sock("Elan").Of("error"));
    }

    [Fact]
    public async Task A_deleted_anchor_falls_back_to_its_time()
    {
        using var r = new FunRig(c => c.HistoryMax = 300);
        var elan = r.Join("Elan", "owner");
        var ids = await Fill(r, elan, 60);
        var at = (long)r.Sock("Elan").Of("msg")[40]["at"]!;             // message 41
        await r.Do(elan, new { t = "delete", id = ids[40] });
        var p = Page(r, elan, "general", ids[40], 100, at);
        Assert.Equal(ids.Take(40), Ids(p));                              // everything older than where it was
        Assert.False((bool)p["hasMore"]!);
    }

    [Fact]
    public async Task Older_messages_of_a_channel_you_cannot_see_do_not_exist_for_you()
    {
        using var r = new FunRig(c => c.HistoryMax = 300);
        var bob = r.Join("Bob", "officer"); var pat = r.Join("Pat", "member"); var guest = r.Join("Gus", "guest");
        var ids = await Fill(r, bob, 40, "officers", "secret");
        var asBob = Page(r, bob, "officers", ids[^1]);
        Assert.Equal(39, Ids(asBob).Length);
        var asPat = Page(r, pat, "officers", ids[^1]);
        Assert.Empty(Ids(asPat));
        Assert.False((bool)asPat["hasMore"]!);
        var ghost = Page(r, pat, "no-such-channel", ids[^1]);
        Assert.Equal(asPat.ToJsonString().Replace("officers", "X"), ghost.ToJsonString().Replace("no-such-channel", "X")); // same shape as a missing channel
        Assert.Empty(Ids(Page(r, guest, "officers", ids[^1])));
        Assert.DoesNotContain("secret", asPat.ToJsonString());
    }

    [Fact]
    public async Task Paging_requests_have_their_own_budget()
    {
        using var r = new FunRig(c => c.HistoryMax = 300);
        var elan = r.Join("Elan", "owner");
        var ids = await Fill(r, elan, 30);
        r.Sock("Elan").Clear();
        for (int i = 0; i < 10; i++) await r.Do(elan, new { t = "history:before", channel = "general", before = ids[^1] });
        Assert.Equal(10, r.Sock("Elan").Of("history:page").Count);
        await r.Do(elan, new { t = "history:before", channel = "general", before = ids[^1] });
        Assert.Equal("rate", (string?)r.Sock("Elan").LastError()!["code"]);
        Assert.Equal(10, r.Sock("Elan").Of("history:page").Count);
        r.Clock.Advance(TimeSpan.FromSeconds(3));
        await r.Do(elan, new { t = "history:before", channel = "general", before = ids[^1] });
        Assert.Equal(11, r.Sock("Elan").Of("history:page").Count);
    }

    // ---------------------------------------------------------------- search

    [Fact]
    public async Task Search_is_case_insensitive_substring_newest_first_with_channel_id_author_time_and_snippet()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner"); var bob = r.Join("Bob", "officer");
        await r.Say(elan, "Anyone for Deadmines tonight?");
        await r.Say(bob, "the DEADMINES run is at eight", "loot");
        await r.Say(elan, "unrelated chatter");
        await r.Say(bob, "see you at the deadmines entrance");
        var res = Find(r, elan, "deadMINES");
        var hits = (JsonArray)res["hits"]!;
        Assert.Equal(3, hits.Count);
        Assert.Equal("deadMINES", (string)res["q"]!);
        Assert.False((bool)res["more"]!);
        // newest first, across channels
        Assert.Equal(new[] { "general", "loot", "general" }, hits.Select(h => (string)h!["channel"]!));
        Assert.True((long)hits[0]!["at"]! > (long)hits[1]!["at"]! && (long)hits[1]!["at"]! > (long)hits[2]!["at"]!);
        Assert.Equal("Bob", (string)hits[0]!["from"]!);
        Assert.Equal("see you at the deadmines entrance", (string)hits[0]!["snippet"]!);
        Assert.False(string.IsNullOrEmpty((string)hits[2]!["id"]!));
        // part of a word counts too
        Assert.Equal(3, ((JsonArray)Find(r, elan, "eadmin")["hits"]!).Count);
    }

    [Fact]
    public async Task Search_only_looks_in_channels_the_asker_can_see()
    {
        using var r = new FunRig();
        var bob = r.Join("Bob", "officer"); var pat = r.Join("Pat", "member"); var guest = r.Join("Gus", "guest");
        await r.Say(bob, "officers only: the password is hunter2", "officers");
        await r.Say(bob, "hunter2 is not a good password", "general");
        Assert.Equal(2, ((JsonArray)Find(r, bob, "hunter2")["hits"]!).Count);
        var forPat = Find(r, pat, "hunter2");
        Assert.Single((JsonArray)forPat["hits"]!);
        Assert.Equal("general", (string)forPat["hits"]![0]!["channel"]!);
        Assert.DoesNotContain("officers", forPat.ToJsonString());
        Assert.Single((JsonArray)Find(r, guest, "hunter2")["hits"]!);
        // asking for the hidden channel by name finds nothing and says nothing
        var direct = Find(r, pat, "hunter2", "officers");
        Assert.Empty((JsonArray)direct["hits"]!);
        Assert.Empty(r.Sock("Pat").Of("error"));
        // restricting to a visible channel works
        Assert.Single((JsonArray)Find(r, bob, "hunter2", "officers")["hits"]!);
    }

    [Fact]
    public async Task Search_returns_at_most_50_hits_newest_first_and_says_there_are_more()
    {
        using var r = new FunRig(c => c.HistoryMax = 300);
        var elan = r.Join("Elan", "owner");
        var ids = await Fill(r, elan, 80, "general", "needle");
        var res = Find(r, elan, "needle");
        var hits = (JsonArray)res["hits"]!;
        Assert.Equal(50, hits.Count);
        Assert.True((bool)res["more"]!);
        Assert.Equal(ids[79], (string)hits[0]!["id"]!);       // the newest
        Assert.Equal(ids[30], (string)hits[49]!["id"]!);
        // exactly 50 matches: no "more"
        using var r2 = new FunRig(c => c.HistoryMax = 300);
        var e2 = r2.Join("Elan", "owner");
        await Fill(r2, e2, 50, "general", "needle");
        var exact = Find(r2, e2, "needle");
        Assert.Equal(50, ((JsonArray)exact["hits"]!).Count);
        Assert.False((bool)exact["more"]!);
    }

    [Fact]
    public async Task Search_across_channels_keeps_the_newest_overall()
    {
        using var r = new FunRig(c => c.HistoryMax = 300);
        var elan = r.Join("Elan", "owner");
        for (int i = 0; i < 40; i++) { await r.Say(elan, "needle g" + i, "general"); await r.Say(elan, "needle l" + i, "loot"); }
        var res = Find(r, elan, "needle");
        var hits = (JsonArray)res["hits"]!;
        Assert.Equal(50, hits.Count);
        Assert.True((bool)res["more"]!);
        var times = hits.Select(h => (long)h!["at"]!).ToList();
        Assert.Equal(times.OrderByDescending(x => x), times);
        Assert.Equal("needle l39", ((string)hits[0]!["snippet"]!));   // the very last message was in loot
    }

    [Fact]
    public async Task The_query_is_cleaned_cut_to_64_characters_and_must_have_two()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner");
        await r.Say(elan, new string('a', 100));
        await r.Say(elan, "one two three");
        var longQ = Find(r, elan, new string('a', 300));
        Assert.Equal(64, ((string)longQ["q"]!).Length);
        Assert.Single((JsonArray)longQ["hits"]!);                    // 64 a's occur inside the 100
        Assert.Empty((JsonArray)Find(r, elan, "a")["hits"]!);         // one character is too short
        Assert.Empty((JsonArray)Find(r, elan, "")["hits"]!);
        Assert.Empty((JsonArray)Find(r, elan, "   ")["hits"]!);
        Assert.Single((JsonArray)Find(r, elan, "one   two")["hits"]!);   // whitespace collapses on both sides
        Assert.Equal("evil", (string)Find(r, elan, "ev\u0000il‮")["q"]!);
        r.Sock("Elan").Clear();
        await r.Do(elan, new { t = "search", q = new[] { 1 } });
        Assert.Empty(((JsonArray)r.Sock("Elan").Of("search:result").Last()["hits"]!));
        Assert.Empty(r.Sock("Elan").Of("error"));
    }

    [Fact]
    public async Task Snippets_are_short_with_ellipses_and_whitespace_collapsed()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner");
        var filler = string.Join(" ", Enumerable.Repeat("lorem", 60));
        await r.Say(elan, filler + " NEEDLE " + filler);
        await r.Say(elan, "line one\nline   two needle\nline three");
        var hits = (JsonArray)Find(r, elan, "needle")["hits"]!;
        var shortOne = (string)hits[0]!["snippet"]!;
        Assert.Equal("line one line two needle line three", shortOne);
        var cut = (string)hits[1]!["snippet"]!;
        Assert.True(cut.Length <= 122);
        Assert.Contains("NEEDLE", cut);
        Assert.StartsWith("…", cut);
        Assert.EndsWith("…", cut);
    }

    [Fact]
    public async Task Search_finds_file_names_sign_up_cards_and_not_deleted_messages()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner");
        var bob = r.Join("Bob", "officer");
        var keep = await r.Say(elan, "keep this zebra");
        var gone = await r.Say(elan, "delete this zebra");
        await r.Do(elan, new { t = "delete", id = gone });
        Assert.Single((JsonArray)Find(r, bob, "zebra")["hits"]!);
        r.Clock.Advance(TimeSpan.FromSeconds(3));
        await r.Do(elan, new { t = "msg", channel = "general", kind = "lfg", text = "", lfg = new { title = "Blackfathom Deeps", now = true, slots = new { tank = 1, dps = 4 } } });
        var res = Find(r, bob, "blackfathom");
        Assert.Single((JsonArray)res["hits"]!);
        Assert.StartsWith("[Looking for group] Blackfathom Deeps", (string)res["hits"]![0]!["snippet"]!);
    }

    [Fact]
    public void An_attachment_is_found_by_its_file_name()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner");
        var dir = Path.Combine(r.Dir.Path, "history");
        Directory.CreateDirectory(dir);
        // a history file written before the first message of the channel is read (stores load lazily)
        File.WriteAllText(HistoryStore.FileOf(dir, "general"),
            new JsonObject { ["id"] = "f1", ["at"] = 1_700_000_000_000L, ["from"] = "Bob", ["text"] = "", ["t"] = "msg", ["file"] = new JsonObject { ["id"] = "abc", ["name"] = "raid-roster-final.png", ["size"] = 10, ["mime"] = "image/png" } }.ToJsonString() + "\n");
        var res = Find(r, elan, "ROSTER");
        Assert.Single((JsonArray)res["hits"]!);
        Assert.Equal("[raid-roster-final.png]", (string)res["hits"]![0]!["snippet"]!);
    }

    [Fact]
    public async Task Search_has_its_own_budget_six_a_minute()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner");
        await r.Say(elan, "hello world");
        r.Sock("Elan").Clear();
        for (int i = 0; i < 6; i++) await r.Do(elan, new { t = "search", q = "hello" });
        Assert.Equal(6, r.Sock("Elan").Of("search:result").Count);
        await r.Do(elan, new { t = "search", q = "hello" });
        Assert.Equal("rate", (string?)r.Sock("Elan").LastError()!["code"]);
        Assert.Equal(6, r.Sock("Elan").Of("search:result").Count);
        r.Clock.Advance(TimeSpan.FromSeconds(11));                    // one token per 10 s
        await r.Do(elan, new { t = "search", q = "hello" });
        Assert.Equal(7, r.Sock("Elan").Of("search:result").Count);
        await r.Do(elan, new { t = "search", q = "hello" });
        Assert.Equal(7, r.Sock("Elan").Of("search:result").Count);
        // somebody else's budget is their own
        var bob = r.Join("Bob", "officer");
        await r.Do(bob, new { t = "search", q = "hello" });
        Assert.Single(r.Sock("Bob").Of("search:result"));
    }

    [Fact]
    public async Task Hammering_search_counts_towards_the_flood_disconnect_like_other_refused_messages()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner");
        for (int i = 0; i < 6; i++) await r.Do(elan, new { t = "search", q = "x1" });
        for (int i = 0; i < 20; i++) await r.Do(elan, new { t = "search", q = "x1" });
        Assert.True(elan.RecentDrops(r.Clock) >= 20);
    }

    [Fact]
    public async Task Searching_and_paging_while_people_post_never_breaks()
    {
        using var r = new FunRig(c => c.HistoryMax = 200);
        var posters = Enumerable.Range(0, 4).Select(i => r.Join("P" + i, "member")).ToList();
        var reader = r.Join("Reader", "officer");
        var stop = false;
        var writers = posters.Select(p => Task.Run(async () =>
        {
            for (int i = 0; i < 150; i++)
            {
                r.Clock.Advance(TimeSpan.FromSeconds(2));
                await r.Do(p, new { t = "msg", channel = "general", text = "needle " + p.Name + " " + i });
                if (i % 7 == 0) await r.Do(p, new { t = "edit", id = (string)r.Sock(p.Name).Of("msg").Last()["id"]!, text = "needle edited " + i });
            }
        })).ToList();
        var readers = Task.Run(async () =>
        {
            while (!stop)
            {
                r.Clock.Advance(TimeSpan.FromSeconds(11));
                await r.Do(reader, new { t = "search", q = "needle" });
                var last = r.Sock("Reader").Of("msg").LastOrDefault();
                if (last != null) await r.Do(reader, new { t = "history:before", channel = "general", before = (string)last["id"]! });
                await Task.Delay(1);
            }
        });
        await Task.WhenAll(writers);
        stop = true;
        await readers;
        var all = ((JsonArray)r.Hub.Welcome(reader)["history"]!["general"]!).Select(x => (string)x!["id"]!).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.True(Find(r, reader, "needle")["hits"]!.AsArray().Count > 0);
    }

    [Fact]
    public void Welcome_announces_the_new_features_and_the_version()
    {
        using var r = new FunRig();
        var elan = r.Join("Elan", "owner");
        var w = r.Hub.Welcome(elan);
        var features = ((JsonArray)w["features"]!).Select(x => (string)x!).ToList();
        Assert.Contains("lfg", features); Assert.Contains("history2", features); Assert.Contains("search", features);
        Assert.Contains("reply", features);   // the older ones are still there
        Assert.Equal("2.15.0", (string)w["server"]!);
    }

    // ---------------------------------------------------------------- storage

    static JsonObject Msg(int i, int pad = 0) => new() { ["id"] = $"m{i:D6}", ["at"] = 1_700_000_000_000L + i, ["from"] = "Elan", ["text"] = "x" + i + new string('y', pad), ["t"] = "msg" };

    [Fact]
    public void Compaction_over_the_byte_cap_is_linear_and_leaves_headroom()
    {
        using var d = new TempDir();
        var h = new HistoryStore(d.Path, "general", 512 * 1024, T.Log, 2000);
        var sw = Stopwatch.StartNew();
        int bigWrites = 0;
        long lastSize = 0;
        for (int i = 0; i < 6000; i++)
        {
            Assert.True(h.Append(Msg(i, 1500)));       // ~1.6 KB each: 2000 of them are 3 MB, far over the 512 KB cap
            var size = new FileInfo(HistoryStore.FileOf(d.Path, "general")).Length;
            if (size < lastSize) bigWrites++;           // the file shrank: a rewrite happened
            lastSize = size;
            Assert.True(size <= 512 * 1024, $"file is {size} bytes after {i}");
        }
        sw.Stop();
        // a rewrite drops to ~75 % of the cap, so rewrites happen about every 100 appends - not on every one
        Assert.InRange(bigWrites, 1, 200);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), "took " + sw.Elapsed);
        Assert.Equal((string)h.Messages[^1]["id"]!, "m005999");
        // memory holds what survives a restart
        var again = new HistoryStore(d.Path, "general", 512 * 1024, T.Log, 2000);
        Assert.Equal((string)h.Messages[0]["id"]!, (string)again.Messages[0]["id"]!);
        Assert.Equal(h.Messages.Count, again.Messages.Count);
    }

    [Fact]
    public void A_full_2000_record_history_loads_and_compacts_quickly()
    {
        using var d = new TempDir();
        var path = HistoryStore.FileOf(d.Path, "general");
        using (var w = new StreamWriter(path))
            for (int i = 0; i < 4000; i++) w.Write(Msg(i, 200).ToJsonString() + "\n");
        var sw = Stopwatch.StartNew();
        var h = new HistoryStore(d.Path, "general", 4 * 1024 * 1024, T.Log, 2000);
        var load = sw.Elapsed;
        Assert.Equal(2000, h.Messages.Count);
        Assert.Equal("m002000", (string)h.Messages[0]["id"]!);
        sw.Restart();
        h.MarkDirty();
        Assert.True(h.Flush());
        var compact = sw.Elapsed;
        Assert.True(load < TimeSpan.FromSeconds(2) && compact < TimeSpan.FromSeconds(2), $"load {load}, compact {compact}");
        Assert.Equal(2000, File.ReadAllLines(path).Length);
        // appends do not rewrite until twice the cap
        for (int i = 4000; i < 4500; i++) Assert.True(h.Append(Msg(i)));
        Assert.Equal(2500, File.ReadAllLines(path).Length);
        Assert.Equal(2000, h.Messages.Count);
    }
}
