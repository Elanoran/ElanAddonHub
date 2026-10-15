using System.Text.Json.Nodes;
using Xunit;

namespace Lodge.Tests;

// Lodge 2.23: hunter field notes (sub-feature "atlas5")
public partial class AtlasTests
{
    static Dictionary<string, object> Note(long n, int? min = null, int? max = null, long? last = null, string? cls = null, string? fam = null, string? name = null,
        string[]? taught = null, Dictionary<string, object>? pts = null)
    {
        var o = new Dictionary<string, object> { ["n"] = n };
        if (min != null) o["min"] = min.Value;
        if (max != null) o["max"] = max.Value;
        if (last != null) o["last"] = last.Value;
        if (cls != null) o["class"] = cls;
        if (fam != null) o["fam"] = fam;
        if (name != null) o["name"] = name;
        if (taught != null) o["taught"] = taught;
        if (pts != null) o["pts"] = pts;
        return o;
    }
    static Dictionary<string, object> Pt(double x, double y) => new() { ["x"] = x, ["y"] = y };
    static Dictionary<string, object> Pts(string map, params Dictionary<string, object>[] p) => new() { [map] = p };
    static object Notes(string npc, Dictionary<string, object> note) => new Dictionary<string, object> { [npc] = note };
    static JsonObject NoteOf(JsonObject d, string npc) => (JsonObject)d["fieldnotes"]![npc]!;

    [Fact]
    public void The_features_announce_atlas5_when_on_and_not_when_off()
    {
        using var r = new FunRig();
        Assert.Contains("atlas5", r.Hub.FeatureList());
        using var off = new FunRig(c => c.AtlasEnabled = false);
        Assert.DoesNotContain("atlas5", off.Hub.FeatureList());
    }

    [Fact]
    public async Task A_field_note_is_stored_and_returned_to_everybody_with_member_count_and_names()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member"); var bob = r.Join("Bob", "member");
        lock (r.Hub.Profiles.Gate) r.Hub.Profiles.For("Amy").DisplayName = "Amy the Bold";
        var t = Unix(r);
        var rep = await Share(r, amy, new { fieldnotes = Notes("1234", Note(3, 35, 35, t - 60, "rare", "Wolf", "Spot", new[] { "bite4", "claw3" }, Pts("15", Pt(0.68123, 0.4678)))) });
        Assert.True((bool)rep["ok"]!); Assert.Equal(1, (int)rep["accepted"]!);
        var n = NoteOf(await GetAll(r, bob), "1234");
        Assert.Equal("Spot", (string)n["name"]!); Assert.Equal(35, (int)n["min"]!); Assert.Equal(35, (int)n["max"]!); Assert.Equal(3, (long)n["n"]!);
        Assert.Equal(t - 60, (long)n["last"]!); Assert.Equal("rare", (string)n["class"]!); Assert.Equal("Wolf", (string)n["fam"]!); Assert.Equal(1, (int)n["m"]!);
        Assert.Equal(1, (int)n["taught"]!["bite4"]!); Assert.Equal(1, (int)n["taught"]!["claw3"]!);
        Assert.Equal(0.6812, (double)n["pts"]!["15"]![0]!["x"]!, 6); Assert.Equal(1, (int)n["pts"]!["15"]![0]!["m"]!);
        Assert.Equal("Amy the Bold", (string)n["by"]![0]!);
    }

    [Fact]
    public async Task Notes_merge_across_members_levels_sightings_last_seen_points_and_taught_sets()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member"); var bob = r.Join("Bob", "member"); var cy = r.Join("Cy", "member");
        var t = Unix(r);
        await Share(r, amy, new { fieldnotes = Notes("77", Note(3, 34, 35, t - 7200, "rare", "Wolf", "Spot", new[] { "bite4" }, Pts("15", Pt(0.5, 0.5), Pt(0.9, 0.9)))) });
        await Share(r, bob, new { fieldnotes = Notes("77", Note(4, 35, 36, t - 600, "elite", "Wolf", "Spot", new[] { "bite4", "dash2" }, Pts("15", Pt(0.505, 0.505)))) });
        await Share(r, cy, new { fieldnotes = Notes("77", Note(1, 35, 35, t - 9000, "rare", "Boar", "Spot")) });
        var n = NoteOf(await GetAll(r, amy), "77");
        Assert.Equal(34, (int)n["min"]!); Assert.Equal(36, (int)n["max"]!);
        Assert.Equal(8, (long)n["n"]!);                                           // 3 + 4 + 1
        Assert.Equal(t - 600, (long)n["last"]!);                                  // the newest
        Assert.Equal(3, (int)n["m"]!);
        Assert.Equal("rare", (string)n["class"]!);                                // majority 2 : 1
        Assert.Equal("Wolf", (string)n["fam"]!);
        Assert.Equal(2, (int)n["taught"]!["bite4"]!); Assert.Equal(1, (int)n["taught"]!["dash2"]!);
        var pts = (JsonArray)n["pts"]!["15"]!;
        Assert.Equal(2, pts.Count);                                               // 0.5/0.5 and 0.505/0.505 are one point
        Assert.Equal(2, (int)pts[0]!["m"]!); Assert.Equal(1, (int)pts[1]!["m"]!);
        var by = (JsonArray)n["by"]!;
        Assert.Equal(new[] { "Bob", "Amy", "Cy" }, by.Select(x => (string)x!).ToArray());      // newest first
    }

    [Fact]
    public async Task A_resend_never_counts_twice_and_a_members_note_only_grows()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member"); var bob = r.Join("Bob", "member");
        var t = Unix(r);
        var note = Note(5, 30, 32, t - 100, "normal", "Boar", "Tusker", new[] { "bite1" }, Pts("12", Pt(0.2, 0.2)));
        await Share(r, amy, new { fieldnotes = Notes("9", note) });
        var again = await Share(r, amy, new { fieldnotes = Notes("9", note) });
        Assert.Equal(0, (int)again["accepted"]!); Assert.Equal(1, (int)again["unchanged"]!);
        var rev = (long)(await GetAll(r, bob))["rev"]!;
        // a smaller / older note changes nothing
        var older = await Share(r, amy, new { fieldnotes = Notes("9", Note(2, 31, 31, t - 5000, "rare", "Wolf")) });
        Assert.Equal(0, (int)older["accepted"]!);
        var n = NoteOf(await GetAll(r, bob), "9");
        Assert.Equal(5, (long)n["n"]!); Assert.Equal(30, (int)n["min"]!); Assert.Equal(32, (int)n["max"]!); Assert.Equal("normal", (string)n["class"]!); Assert.Equal("Boar", (string)n["fam"]!);
        Assert.Equal(rev, (long)(await GetAll(r, bob))["rev"]!);
        // a newer one extends it, and its class replaces the member's old vote
        await Share(r, amy, new { fieldnotes = Notes("9", Note(6, 29, 33, t - 10, "rare", "Wolf", null, new[] { "claw2" }, Pts("12", Pt(0.7, 0.7)))) });
        n = NoteOf(await GetAll(r, bob), "9");
        Assert.Equal(6, (long)n["n"]!); Assert.Equal(29, (int)n["min"]!); Assert.Equal(33, (int)n["max"]!); Assert.Equal("rare", (string)n["class"]!); Assert.Equal(1, (int)n["m"]!);
        Assert.Equal(2, ((JsonArray)n["pts"]!["12"]!).Count); Assert.Equal(2, ((JsonObject)n["taught"]!).Count);
        // delta: a client that has the old revision gets just this npc, and an unrelated npc is not part of it
        await Share(r, bob, new { fieldnotes = Notes("10", Note(1)) });
        var delta = await Get(r, bob, rev, (string)(await GetAll(r, bob))["epoch"]!);
        Assert.NotNull(delta["fieldnotes"]!["9"]); Assert.NotNull(delta["fieldnotes"]!["10"]);
    }

    [Theory]
    [InlineData("n", "-1")] [InlineData("n", "1.5")] [InlineData("n", "\"3\"")] [InlineData("n", "100001")]
    [InlineData("min", "0")] [InlineData("min", "81")] [InlineData("min", "\"x\"")]
    [InlineData("last", "1")] [InlineData("last", "99999999999")]
    [InlineData("class", "\"mythic\"")] [InlineData("class", "5")]
    [InlineData("taught", "[\"Bite 4\"]")] [InlineData("taught", "[\"bite\"]")] [InlineData("taught", "\"bite4\"")] [InlineData("taught", "[7]")]
    [InlineData("fam", "\"Wo\\u0000lf\"")] [InlineData("name", "5")]
    [InlineData("pts", "{\"0\":[{\"x\":0.5,\"y\":0.5}]}")] [InlineData("pts", "{\"15\":[{\"x\":1.5,\"y\":0.5}]}")] [InlineData("pts", "{\"15\":[]}")] [InlineData("pts", "{\"15\":[{\"x\":0.5}]}")]
    public async Task Bad_notes_are_refused_whole(string field, string json)
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member");
        var t = Unix(r);
        var note = JsonNode.Parse("{\"n\":2,\"min\":30,\"max\":31,\"last\":" + (t - 10) + ",\"class\":\"rare\"}")!.AsObject();
        note[field] = JsonNode.Parse(json);
        var frame = new JsonObject { ["fieldnotes"] = new JsonObject { ["5"] = note } };
        r.Clock.Advance(TimeSpan.FromMinutes(3));
        r.Sock("Amy").Clear();
        frame["t"] = "atlas.share";
        await r.Send(amy, frame);
        var rep = r.Sock("Amy").Of("atlas.shared").Last();
        Assert.Equal(1, (int)rep["rejected"]!); Assert.Equal(0, (int)rep["accepted"]!);
        Assert.Null((await GetAll(r, amy))["fieldnotes"]!["5"]);
    }

    [Fact]
    public async Task Bad_ids_bad_shapes_and_empty_notes_are_refused_and_good_ones_in_the_same_share_still_count()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member");
        var rep = await Share(r, amy, new { fieldnotes = new Dictionary<string, object> { ["0"] = Note(1), ["01"] = Note(1), ["abc"] = Note(1), ["8"] = Note(0), ["9"] = Note(0, 35, 34), ["10"] = Note(2, null, 40), ["11"] = Note(2, 35, 35) } });
        Assert.Equal(1, (int)rep["accepted"]!); Assert.Equal(6, (int)rep["rejected"]!);     // an empty note, min > max and max without min are refused too
        var d = await GetAll(r, amy);
        Assert.Single((JsonObject)d["fieldnotes"]!);
        var wrongType = await Share(r, amy, new { fieldnotes = new[] { 1, 2 } });
        Assert.False((bool)wrongType["ok"]!); Assert.Equal("empty", (string)wrongType["why"]!);     // not a table: no content
    }

    [Fact]
    public async Task Names_and_families_are_cleaned_unknown_fields_dropped_and_no_character_names_stored()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member");
        var n = Note(1, 20, 20, null, null, "Wo\"lf|", "Sp\\ot<b>");
        n["char"] = "Thrallsmite-Realm"; n["dmin"] = 20; n["hist"] = new[] { 1, 2 };
        await Share(r, amy, new { fieldnotes = Notes("5", n) });
        var d = await GetAll(r, amy);
        var o = NoteOf(d, "5");
        Assert.Equal("Spotb", (string)o["name"]!); Assert.Equal("Wolf", (string)o["fam"]!);
        Assert.DoesNotContain("Thrallsmite", d.ToJsonString());
        Assert.Null(o["dmin"]); Assert.Null(o["hist"]);
    }

    [Fact]
    public async Task Caps_per_share_per_npc_and_per_member_hold()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member");
        // more npcs than one share may carry: the rest is refused
        var many = new Dictionary<string, object>();
        for (int i = 1; i <= Atlas.MaxNotesPerShare + 5; i++) many[i.ToString()] = Note(1);
        var rep = await Share(r, amy, new { fieldnotes = many });
        Assert.Equal(Atlas.MaxNotesPerShare, (int)rep["accepted"]!); Assert.Equal(5, (int)rep["rejected"]!);
        // too many points on one npc: refused whole
        var pts = Enumerable.Range(0, Atlas.MaxNotePtsPerNpcPerMember + 1).Select(i => Pt(0.01 + i * 0.03 % 0.97, 0.5 + (i % 2) * 0.3 - i * 0.001)).ToArray();
        var rejected = await Share(r, amy, new { fieldnotes = Notes("999", Note(1, null, null, null, null, null, null, null, Pts("1", pts))) });
        Assert.Equal(1, (int)rejected["rejected"]!);
        // more taught than allowed
        var taught = Enumerable.Range(1, Atlas.MaxNoteTaught + 1).Select(i => "abc" + i).ToArray();
        Assert.Equal(1, (int)(await Share(r, amy, new { fieldnotes = Notes("998", Note(1, null, null, null, null, null, null, taught)) }))["rejected"]!);
        // a note grown over time past the per-npc point cap keeps what fits and reports the rest as refused
        for (int round = 0; round < 3; round++)
        {
            var batch = Enumerable.Range(0, 12).Select(i => Pt(0.05 + (round * 12 + i) * 0.025, 0.1 + round * 0.3)).ToArray();
            await Share(r, amy, new { fieldnotes = Notes("500", Note(1, null, null, null, null, null, null, null, Pts("2", batch))) });
        }
        var o = NoteOf(await GetAll(r, amy), "500");
        Assert.Equal(Atlas.MaxNotePtsPerMapAgg, ((JsonArray)o["pts"]!["2"]!).Count);                 // what everybody sees is capped per map too
        var full = await Share(r, amy, new { fieldnotes = Notes("500", Note(1, null, null, null, null, null, null, null, Pts("2", Pt(0.95, 0.95)))) });
        Assert.Equal(1, (int)full["rejected"]!);                                                      // the member's own 24 are used up
    }

    [Fact]
    public async Task The_pool_and_the_member_cap_on_npcs_refuse_new_npcs()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member");
        int accepted = 0;
        for (int start = 1; start <= Atlas.MaxNotesPerMember; start += Atlas.MaxNotesPerShare)
        {
            var batch = new Dictionary<string, object>();
            for (int i = start; i < start + Atlas.MaxNotesPerShare && i <= Atlas.MaxNotesPerMember + 10; i++) batch[i.ToString()] = Note(1);
            accepted += (int)(await Share(r, amy, new { fieldnotes = batch }))["accepted"]!;
        }
        Assert.Equal(Atlas.MaxNotesPerMember, accepted);
        var over = await Share(r, amy, new { fieldnotes = Notes("9000", Note(1)) });
        Assert.Equal(1, (int)over["rejected"]!);
        // but a note the member already has can still grow
        Assert.Equal(1, (int)(await Share(r, amy, new { fieldnotes = Notes("5", Note(9)) }))["accepted"]!);
    }

    [Fact]
    public async Task Guests_can_read_field_notes_without_member_names_or_exact_times_and_cannot_share()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member"); var pat = r.Join("Pat", "guest");
        lock (r.Hub.Profiles.Gate) r.Hub.Profiles.For("Amy").DisplayName = "Amy the Bold";
        var t = Unix(r) - 3 * 3600 - 17;
        await Share(r, amy, new { fieldnotes = Notes("1234", Note(3, 35, 35, t, "rare", "Wolf", "Spot")) });
        var asGuest = await GetAll(r, pat);
        var n = NoteOf(asGuest, "1234");
        Assert.Equal(35, (int)n["min"]!); Assert.Equal(1, (int)n["m"]!); Assert.Equal("Spot", (string)n["name"]!);
        Assert.Null(n["by"]);
        Assert.DoesNotContain("Amy", asGuest.ToJsonString());
        Assert.Equal(t - t % 86_400, (long)n["last"]!);
        Assert.NotNull(NoteOf(await GetAll(r, amy), "1234")["by"]);
        var denied = await Share(r, pat, new { fieldnotes = Notes("1", Note(1)) });
        Assert.False((bool)denied["ok"]!); Assert.Equal("guest", (string)denied["why"]!);
    }

    [Fact]
    public async Task Purging_a_member_removes_their_notes_and_rebuilds_the_merge()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member"); var bob = r.Join("Bob", "member");
        var t = Unix(r);
        await Share(r, amy, new { fieldnotes = new Dictionary<string, object> { ["1"] = Note(5, 30, 40, t - 10, "rare", "Wolf", "Spot", new[] { "bite4" }, Pts("15", Pt(0.1, 0.1))), ["2"] = Note(2, 20, 20, t - 20) } });
        await Share(r, bob, new { fieldnotes = Notes("1", Note(1, 31, 33, t - 500, "elite", "Boar", "Spot", new[] { "claw1" })) });
        var before = await GetAll(r, bob);
        Assert.Equal(6, (long)NoteOf(before, "1")["n"]!);
        var res = r.Hub.AtlasPool.Purge("Amy");
        Assert.Equal(2, res.FieldNotes); Assert.Equal(2, res.Total);
        var after = await Get(r, bob, (long)before["rev"]!, (string)before["epoch"]!);
        Assert.True(after.ContainsKey("full")); Assert.NotEqual((string)before["epoch"]!, (string)after["epoch"]!);
        var n = NoteOf(after, "1");
        Assert.Equal(1, (long)n["n"]!); Assert.Equal(31, (int)n["min"]!); Assert.Equal(33, (int)n["max"]!); Assert.Equal("elite", (string)n["class"]!);
        Assert.Null(n["pts"]); Assert.Null(n["taught"]!["bite4"]); Assert.Null(after["fieldnotes"]!["2"]);
        Assert.DoesNotContain("Amy", after.ToJsonString());
        Assert.Equal(1, r.Hub.AtlasPool.Store.NoteCount);
        // they can share again after a revert
        Assert.Equal(1, (int)(await Share(r, amy, new { fieldnotes = Notes("2", Note(2, 20, 20)) }))["accepted"]!);
    }

    [Fact]
    public async Task Field_notes_survive_a_restart_with_their_merge_and_revisions()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member"); var bob = r.Join("Bob", "member");
        var t = Unix(r);
        await Share(r, amy, new { fieldnotes = Notes("1", Note(5, 30, 40, t - 10, "rare", "Wolf", "Spot", new[] { "bite4" }, Pts("15", Pt(0.1, 0.2)))) });
        await Share(r, bob, new { fieldnotes = Notes("1", Note(2, 29, 30, t - 100, "rare", "Wolf", "Spot", new[] { "bite4", "dash1" })) });
        var live = await GetAll(r, amy);
        r.Hub.AtlasPool.Flush();
        var again = new AtlasStore(r.Dir.Path, T.Log, r.Clock);
        JsonObject d;
        lock (again.Gate) d = again.Get(0, null, s => s);
        var n = NoteOf(d, "1");
        Assert.Equal(7, (long)n["n"]!); Assert.Equal(29, (int)n["min"]!); Assert.Equal(40, (int)n["max"]!); Assert.Equal(2, (int)n["taught"]!["bite4"]!);
        Assert.Equal(0.1, (double)n["pts"]!["15"]![0]!["x"]!, 6);
        Assert.Equal((long)NoteOf(live, "1")["last"]!, (long)n["last"]!);
        Assert.Equal((string)live["epoch"]!, (string)d["epoch"]!);
    }

    [Fact]
    public async Task Field_notes_count_as_share_content_and_obey_the_share_rate_limit()
    {
        using var r = new FunRig();
        var amy = r.Join("Amy", "member");
        Assert.True(AtlasStore.HasContent(JsonNode.Parse("{\"fieldnotes\":{\"1\":{\"n\":1}}}")!.AsObject()));
        Assert.False(AtlasStore.HasContent(JsonNode.Parse("{\"fieldnotes\":{}}")!.AsObject()));
        for (int i = 1; i <= Atlas.ShareBurst; i++) Assert.True((bool)(await Share(r, amy, new { fieldnotes = Notes("1", Note(i)) }, advance: false))["ok"]!);
        var rep = await Share(r, amy, new { fieldnotes = Notes("1", Note(50)) }, advance: false);
        Assert.False((bool)rep["ok"]!); Assert.Equal("rate", (string)rep["why"]!);
    }

    [Fact]
    public async Task With_the_feature_off_notes_are_not_taken()
    {
        using var r = new FunRig(c => c.AtlasEnabled = false);
        var amy = r.Join("Amy", "member");
        var rep = await Share(r, amy, new { fieldnotes = Notes("1", Note(1)) });
        Assert.False((bool)rep["ok"]!); Assert.Equal("disabled", (string)rep["why"]!);
    }
}
