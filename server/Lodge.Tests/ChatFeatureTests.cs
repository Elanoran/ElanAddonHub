using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Lodge.Tests;

// Lodge 2.3: replies, reactions, pins.
public class ChatFeatureTests
{
    sealed class Sock : WebSocket
    {
        public readonly List<JsonObject> Sent = new();
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken ct) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken ct) => Task.CompletedTask;
        public override void Dispose() { }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct) => Task.Delay(Timeout.Infinite, ct).ContinueWith<WebSocketReceiveResult>(_ => null!);
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType t, bool end, CancellationToken ct)
        {
            var o = JsonNode.Parse(Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count))!.AsObject();
            lock (Sent) Sent.Add(o);
            return Task.CompletedTask;
        }
        public List<JsonObject> Of(string t) { lock (Sent) return Sent.Where(x => (string)x["t"] == t).ToList(); }
    }

    sealed class Rig : IDisposable
    {
        public readonly TempDir Dir = new();
        public readonly FakeClock Clock = new();
        public LodgeHub Hub;
        public readonly Dictionary<string, (Member m, Sock s)> People = new();

        public Rig() { Hub = Make(); }

        public LodgeHub Make()
        {
            var cfg = LodgeConfig.ForTests(Dir.Path);
            T.WriteCodes(cfg, $"Elan:{T.ElanCode}:owner", $"Bob:{T.BobCode}:officer");
            return new LodgeHub(cfg, new Auth(cfg, T.Log), new FileStore(cfg), T.Log, Clock);
        }

        public Member Join(string name, string role, bool admit = true)
        {
            var s = new Sock();
            var m = new Member { Id = Hub.NextId(), Name = name, Role = role, Ws = s, Personal = true, Budget = new Budget(Clock), Ip = "127.0.0.1" };
            if (admit) Assert.True(Hub.TryAdmit(m));
            People[name] = (m, s);
            return m;
        }

        public Sock Sock(string name) => People[name].s;

        public Task Do(Member m, object o)
        {
            var j = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(o))!.AsObject();
            return Hub.Chat.Handle(m, (string)j["t"]!, j);
        }

        public async Task<string> Say(Member m, string text, string channel = "general", string? replyTo = null)
        {
            Clock.Advance(TimeSpan.FromSeconds(3));
            await Do(m, new { t = "msg", channel, text, replyTo });
            var sent = Sock(m.Name).Of("msg").Last();
            return (string)sent["id"]!;
        }

        public void Dispose() => Dir.Dispose();
    }

    [Fact]
    public async Task Reply_gets_a_normalized_truncated_snippet_from_the_same_channel_only()
    {
        using var r = new Rig();
        var elan = r.Join("Elan", "owner"); r.Join("Bob", "officer");
        var original = await r.Say(elan, "line one\n\tline\u0007 two " + new string('x', 200));
        var other = await r.Say(elan, "in loot", "loot");

        await r.Say(elan, "answer", "general", original);
        var reply = r.Sock("Bob").Of("msg").Last()["replyTo"]!.AsObject();
        Assert.Equal(original, (string)reply["id"]!);
        var snip = (string)reply["snippet"]!;
        Assert.StartsWith("line one line two x", snip);
        Assert.True(snip.Length <= ChatModule.SnippetMax + 1, snip.Length.ToString());
        Assert.DoesNotContain('\n', snip);
        Assert.DoesNotContain('\u0007', snip);
        Assert.Equal("Elan", (string)reply["by"]!);
        Assert.Equal(snip, (string)reply["text"]!);   // 2.x field names carry the same values

        await r.Say(elan, "wrong channel", "general", other);            // exists, but in another channel
        Assert.Null(r.Sock("Bob").Of("msg").Last()["replyTo"]);
        await r.Say(elan, "unknown", "general", "doesnotexist");
        Assert.Null(r.Sock("Bob").Of("msg").Last()["replyTo"]);
        r.Clock.Advance(TimeSpan.FromSeconds(3));
        await r.Hub.Chat.Handle(elan, "msg", JsonNode.Parse("{\"t\":\"msg\",\"channel\":\"general\",\"text\":\"odd\",\"replyTo\":{\"id\":1}}")!.AsObject());
        Assert.Null(r.Sock("Bob").Of("msg").Last()["replyTo"]);
    }

    [Fact]
    public async Task Reactions_are_whitelisted_toggle_and_persist_with_the_history()
    {
        using var r = new Rig();
        var elan = r.Join("Elan", "owner"); var bob = r.Join("Bob", "officer");
        var id = await r.Say(elan, "hello");

        await r.Do(bob, new { t = "react", id, emoji = "\U0001F921" });  // clown: not in the set
        await r.Do(bob, new { t = "react", id, emoji = "<b>x</b>" });
        Assert.Empty(r.Sock("Elan").Of("react"));

        await r.Do(bob, new { t = "react", id, emoji = "ready" });
        await r.Do(elan, new { t = "react", id, emoji = "ready" });
        await r.Do(bob, new { t = "react", id, emoji = "love" });      // legacy-free canonical id
        var reacts = r.Sock("Elan").Of("react");
        Assert.Equal(3, reacts.Count);
        Assert.Equal(new[] { "Bob", "Elan" }, reacts[1]["users"]!.AsArray().Select(x => (string)x!).ToArray());
        Assert.Equal("love", (string)reacts[2]["reaction"]!);
        Assert.Equal("❤️", (string)reacts[2]["emoji"]!);   // fallback for 2.14 hubs

        await r.Do(bob, new { t = "react", id, emoji = "ready" });  // toggles off
        var off = r.Sock("Elan").Of("react").Last();
        Assert.False((bool)off["on"]!);
        Assert.Equal(new[] { "Elan" }, off["users"]!.AsArray().Select(x => (string)x!).ToArray());

        // saved with the history record, and still there after a restart
        var again = new HistoryStore(System.IO.Path.Combine(r.Dir.Path, "history"), "general", 1 << 20, T.Log);
        var rec = again.Messages.Single(x => (string)x["id"] == id);
        var stored = rec["reactions"]!.AsObject();
        Assert.Equal(2, stored.Count);
        Assert.Equal(new[] { "Elan" }, stored["ready"]!.AsArray().Select(x => (string)x!).ToArray());
        var hub2 = r.Make();
        Assert.NotNull(hub2.Chat.HistoryFor("general").Single(x => (string)x!["id"] == id)!["reactions"]);

        // removing the last reaction removes the field again
        await r.Do(elan, new { t = "react", id, emoji = "ready" });
        await r.Do(bob, new { t = "react", id, emoji = "love" });
        var cleared = new HistoryStore(System.IO.Path.Combine(r.Dir.Path, "history"), "general", 1 << 20, T.Log);
        Assert.Null(cleared.Messages.Single(x => (string)x["id"] == id)["reactions"]);
    }

    [Theory]
    [InlineData("✅", "ready")] [InlineData("\U0001F44D", "ready")] [InlineData("❌", "notready")]
    [InlineData("\U0001F602", "lol")] [InlineData("❤️", "love")] [InlineData("❤", "love")]
    [InlineData("⚔️", "fight")] [InlineData("⚔", "fight")]
    [InlineData("ready", "ready")] [InlineData("notready", "notready")] [InlineData("lol", "lol")] [InlineData("love", "love")]
    [InlineData("fight", "fight")] [InlineData("loot", "loot")] [InlineData("wipe", "wipe")] [InlineData("epic", "epic")]
    public void Reaction_ids_and_legacy_emoji_map_to_canonical_ids(string input, string expected)
        => Assert.Equal(expected, ChatModule.CanonReaction(input));

    [Theory]
    [InlineData("\U0001F921")] [InlineData("READY")] [InlineData("<b>x</b>")] [InlineData("")] [InlineData("0123456789abcdef")]
    public void Anything_else_is_not_a_reaction(string input) => Assert.Null(ChatModule.CanonReaction(input));

    [Fact]
    public async Task Legacy_emoji_from_2_14_hubs_are_accepted_and_broadcast_with_id_and_emoji_fallback()
    {
        using var r = new Rig();
        var elan = r.Join("Elan", "owner"); var bob = r.Join("Bob", "officer");
        var id = await r.Say(elan, "hello");
        await r.Do(bob, new { t = "react", id, emoji = "⚔️" });
        await r.Do(bob, new { t = "react", id, emoji = "loot" });
        var reacts = r.Sock("Elan").Of("react");
        Assert.Equal("fight", (string)reacts[0]["reaction"]!);
        Assert.Equal("⚔️", (string)reacts[0]["emoji"]!);
        Assert.Equal("loot", (string)reacts[1]["reaction"]!);
        Assert.Equal("\U0001F4B0", (string)reacts[1]["emoji"]!);
        Assert.Equal(new[] { "ready", "notready", "lol", "love", "fight", "loot", "wipe", "epic" }, ChatModule.ReactionSet);
        Assert.Equal(ChatModule.ReactionSet.Length, ChatModule.ReactionEmoji.Length);
    }

    [Fact]
    public void Stored_emoji_reactions_are_migrated_to_ids_on_load_and_idempotently()
    {
        using var d = new TempDir();
        var hist = System.IO.Path.Combine(d.Path, "history");
        System.IO.Directory.CreateDirectory(hist);
        System.IO.File.WriteAllText(System.IO.Path.Combine(hist, "general.jsonl"),
            "{\"id\":\"a\",\"at\":1,\"from\":\"Elan\",\"text\":\"x\",\"reactions\":{\"\U0001F44D\":[\"Bob\"],\"✅\":[\"bob\",\"Cy\"],\"❤️\":[\"Elan\"],\"\U0001F921\":[\"Zed\"]}}\n" +
            "{\"id\":\"b\",\"at\":2,\"from\":\"Elan\",\"text\":\"y\",\"reactions\":{\"wipe\":[\"Bob\"]}}\n" +
            "{\"id\":\"c\",\"at\":3,\"from\":\"Elan\",\"text\":\"z\"}\n");
        var s = new HistoryStore(hist, "general", 1 << 20, T.Log);
        var a = s.Messages.Single(x => (string)x["id"] == "a")["reactions"]!.AsObject();
        Assert.Equal(new[] { "ready", "love" }, a.Select(kv => kv.Key).ToArray());   // clown dropped, thumbs up + check merged
        Assert.Equal(new[] { "Bob", "Cy" }, a["ready"]!.AsArray().Select(x => (string)x!).ToArray()); // "bob" deduped case-insensitively
        Assert.Equal("wipe", s.Messages.Single(x => (string)x["id"] == "b")["reactions"]!.AsObject().Single().Key);
        Assert.Null(s.Messages.Single(x => (string)x["id"] == "c")["reactions"]);
        foreach (var m in s.Messages) Assert.False(ChatModule.MigrateReactions(m)); // idempotent
        Assert.True(s.Compact());
        var again = new HistoryStore(hist, "general", 1 << 20, T.Log);
        Assert.Equal(new[] { "ready", "love" }, again.Messages[0]["reactions"]!.AsObject().Select(kv => kv.Key).ToArray());
    }

    [Fact]
    public async Task Reactions_cannot_reach_channels_the_rank_may_not_see_and_are_bounded_per_emoji()
    {
        using var r = new Rig();
        var elan = r.Join("Elan", "owner"); var guest = r.Join("Gus", "guest");
        var secret = await r.Say(elan, "officers only", "officers");
        await r.Do(guest, new { t = "react", id = secret, emoji = "ready" });
        Assert.Empty(r.Sock("Elan").Of("react"));

        var id = await r.Say(elan, "popular");
        for (int i = 0; i < ChatModule.MaxReactors + 5; i++)
        {
            var p = r.Join("Fan" + i, "member", admit: false);
            await r.Do(p, new { t = "react", id, emoji = "ready" });
        }
        Assert.Equal(ChatModule.MaxReactors, r.Sock("Elan").Of("react").Last()["users"]!.AsArray().Count);
    }

    [Fact]
    public void Control_budget_stops_a_reaction_flood()
    {
        // reactions travel as ordinary (non-"msg") frames, so they spend the Control bucket: burst 30, then 5/s
        var clock = new FakeClock();
        var b = new Budget(clock);
        int passed = 0;
        for (int i = 0; i < 1000; i++) if (b.Control.TryTake()) passed++;
        Assert.Equal(30, passed);
    }

    [Fact]
    public async Task Only_officers_pin_up_to_25_and_pins_survive_compaction_and_restart()
    {
        using var r = new Rig();
        var elan = r.Join("Elan", "owner"); var bob = r.Join("Bob", "officer");
        var mem = r.Join("Mem", "member"); var guest = r.Join("Gus", "guest");
        var first = await r.Say(elan, "the rules: be nice");

        await r.Do(mem, new { t = "pin", id = first });
        await r.Do(guest, new { t = "pin", id = first });
        Assert.Empty(r.Sock("Elan").Of("pin"));
        Assert.Contains("officers", (string)r.Sock("Mem").Of("error").Last()["text"]!);
        Assert.False(File.Exists(System.IO.Path.Combine(r.Dir.Path, "pins.json")));

        await r.Do(bob, new { t = "pin", id = first });
        await r.Do(bob, new { t = "pin", id = first });                       // already pinned: no duplicate
        var pins = r.Sock("Elan").Of("pin");
        Assert.Single(pins);
        Assert.Equal("Bob", (string)pins[0]["pin"]!["pinnedBy"]!);
        Assert.Equal("Elan", (string)pins[0]["pin"]!["by"]!);

        await r.Do(mem, new { t = "unpin", id = first });                     // a member can't unpin either
        Assert.Single(r.Hub.Chat.Pins.For("general"));

        // push the message out of the 200-message history (several compactions) - the pin keeps its own copy
        for (int i = 0; i < HistoryStore.Keep * 2 + 20; i++) await r.Say(elan, "filler " + i);
        Assert.DoesNotContain(r.Hub.Chat.HistoryFor("general"), x => (string)x!["id"] == first);
        var welcome = r.Hub.Welcome(bob);
        Assert.Equal("the rules: be nice", (string)welcome["pins"]!["general"]![0]!["text"]!);
        Assert.True((bool)welcome["canPin"]!);
        Assert.False((bool)r.Hub.Welcome(mem)["canPin"]!);
        Assert.Contains("pin", welcome["features"]!.AsArray().Select(x => (string)x!));

        // restart: new hub, same data folder
        var hub2 = r.Make();
        Assert.Equal("the rules: be nice", (string)hub2.Chat.Pins.For("general")[0]!["text"]!);
        Assert.Empty(hub2.Chat.Pins.For("loot"));

        // limit
        for (int i = 1; i < PinStore.MaxPerChannel; i++)
        {
            var id = await r.Say(elan, "pin me " + i);
            await r.Do(bob, new { t = "pin", id });
        }
        Assert.Equal(PinStore.MaxPerChannel, r.Hub.Chat.Pins.For("general").Count);
        var one = await r.Say(elan, "one too many");
        await r.Do(bob, new { t = "pin", id = one });
        Assert.Equal(PinStore.MaxPerChannel, r.Hub.Chat.Pins.For("general").Count);
        Assert.Contains("25", (string)r.Sock("Bob").Of("error").Last()["text"]!);

        // unpin works even though the original message is long gone from history
        await r.Do(bob, new { t = "unpin", id = first });
        Assert.Equal(PinStore.MaxPerChannel - 1, r.Hub.Chat.Pins.For("general").Count);
        Assert.Equal("unpin", (string)r.Sock("Elan").Sent.Last(x => ((string)x["t"]!).EndsWith("pin"))["t"]!);
        Assert.Equal(PinStore.MaxPerChannel - 1, r.Make().Chat.Pins.For("general").Count);
    }

    [Fact]
    public async Task Pins_follow_deletes_and_edits_and_stay_in_their_channel()
    {
        using var r = new Rig();
        var elan = r.Join("Elan", "owner"); var gus = r.Join("Gus", "guest");
        var id = await r.Say(elan, "old text");
        var secret = await r.Say(elan, "secret plan", "officers");
        await r.Do(elan, new { t = "pin", id });
        await r.Do(elan, new { t = "pin", id = secret });
        Assert.Empty(r.Hub.Welcome(gus)["pins"]!.AsObject().Where(kv => kv.Key == "officers"));   // not visible to a guest

        await r.Do(elan, new { t = "edit", id, text = "new text" });
        Assert.Equal("new text", (string)r.Hub.Chat.Pins.For("general")[0]!["text"]!);
        await r.Do(elan, new { t = "delete", id });
        Assert.Empty(r.Hub.Chat.Pins.For("general"));
        Assert.Single(r.Hub.Chat.Pins.For("officers"));
    }

    [Fact]
    public void A_bad_or_oversized_pins_file_is_ignored_and_a_leftover_tmp_is_recovered()
    {
        using var d = new TempDir();
        var path = System.IO.Path.Combine(d.Path, "pins.json");
        File.WriteAllText(path, "{ not json");
        Assert.Empty(new PinStore(d.Path, T.Log).For("general"));
        File.Delete(path);
        File.WriteAllText(path + ".tmp", "{\"general\":[{\"id\":\"abc\",\"text\":\"kept\",\"by\":\"Elan\",\"at\":1}]}");
        Assert.Single(new PinStore(d.Path, T.Log).For("general"));   // only the tmp existed: used
        Assert.False(File.Exists(path + ".tmp"));
    }
}
