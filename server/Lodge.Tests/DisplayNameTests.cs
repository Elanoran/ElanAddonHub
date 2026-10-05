using System.Text.Json.Nodes;
using Xunit;
using static Lodge.Tests.ProfileTests;

namespace Lodge.Tests;

// 2.7.0: display names - validation, uniqueness against login names and other display names, look-alike fold,
// rate limit, guests refused, owner reset, broadcast/welcome fields, persistence.
public class DisplayNameTests
{
    static string Set(string name) => "{\"t\":\"profile:set\",\"displayName\":" + JsonValue.Create(name)!.ToJsonString() + "}";

    static string? Err(ScriptSocket s) => s.Of("error").Count > 0 ? (string?)s.Of("error").Last()["text"] : null;

    [Fact]
    public void Format_rules_normalize_and_bound()
    {
        Assert.Equal("Mia Moon", ProfileRules.CheckDisplayFormat("  Mia ​  Moon ", out var e)); Assert.Null(e);
        Assert.Equal("", ProfileRules.CheckDisplayFormat("   ", out e)); Assert.Null(e);                // empty = none
        Assert.Equal("Mia", ProfileRules.CheckDisplayFormat("Ｍia", out e)); // NFKC folds fullwidth
        Assert.Equal("Zoë-Ann_o'k.!", ProfileRules.CheckDisplayFormat("Zoë-Ann_o'k.!", out e)); Assert.Null(e);
        Assert.Null(ProfileRules.CheckDisplayFormat("A", out e)); Assert.NotNull(e);                    // too short
        Assert.Null(ProfileRules.CheckDisplayFormat(new string('a', 25), out e)); Assert.NotNull(e);    // too long
        Assert.NotNull(ProfileRules.CheckDisplayFormat(new string('a', 24), out e)); Assert.Null(e);
        foreach (var bad in new[] { "Mia<b>", "Mia@home", "Mia#1", "a:b", "Mia/x", "--", "..", "\"quote\"" })
        { Assert.Null(ProfileRules.CheckDisplayFormat(bad, out e)); Assert.NotNull(e); }
    }

    [Fact]
    public void Fold_merges_lookalikes_and_separators()
    {
        Assert.Equal(ProfileRules.Fold("Elan"), ProfileRules.Fold("E1an"));
        Assert.Equal(ProfileRules.Fold("Elan"), ProfileRules.Fold("EIan"));
        Assert.Equal(ProfileRules.Fold("Elan"), ProfileRules.Fold("E|an"));
        Assert.Equal(ProfileRules.Fold("Rose"), ProfileRules.Fold("R0s3"));
        Assert.Equal(ProfileRules.Fold("Seth"), ProfileRules.Fold("5eth"));
        Assert.Equal(ProfileRules.Fold("Elan"), ProfileRules.Fold("E-l.an"));
        Assert.NotEqual(ProfileRules.Fold("Elan"), ProfileRules.Fold("Elana"));
    }

    [Fact]
    public async Task A_display_name_is_stored_normalized_returned_broadcast_and_in_welcome()
    {
        using var l = new Lodge3();
        var mia = await l.Join(MiaCode); var bob = await l.Join(T.BobCode);
        bob.Clear(); mia.Clear();
        await l.Set(mia, Set("  Mia   the Moon "));
        Assert.Equal("Mia the Moon", (string?)mia.Profile()["displayName"]);
        var bc = Assert.Single(bob.Of("profile"));
        Assert.Equal("Mia the Moon", (string?)bc["displayName"]);
        Assert.Equal("Mia", (string?)bc["name"]);                                           // the login name stays the identity
        Assert.Equal("Mia the Moon", (string?)(await l.Get(bob, "Mia"))["displayName"]);
        Assert.Equal("Mia the Moon", (string?)l.Hub.Members.First(m => m.Name == "Mia").ToJson()["displayName"]);

        var late = await l.Join(T.ElanCode);
        var users = (JsonArray)late.Of("welcome")[0]["users"]!;
        Assert.Equal("Mia the Moon", (string?)users.First(u => (string?)u!["name"] == "Mia")!["displayName"]);
        Assert.Equal("", (string?)users.First(u => (string?)u!["name"] == "Bob")!["displayName"]); // none = empty string

        // offline: still in the profile lookup
        mia.Hangup(); await Lodge3.Settle();
        Assert.Equal("Mia the Moon", (string?)(await l.Get(bob, "Mia"))["displayName"]);
    }

    [Fact]
    public async Task Bad_display_names_are_refused_and_nothing_else_in_the_message_is_applied()
    {
        using var l = new Lodge3();
        var mia = await l.Join(MiaCode);
        foreach (var bad in new[] { "A", new string('x', 25), "Mia<script>", "Mia@Bob" })
        {
            mia.Clear();
            await l.Set(mia, "{\"t\":\"profile:set\",\"about\":\"changed\",\"displayName\":" + JsonValue.Create(bad)!.ToJsonString() + "}");
            Assert.NotNull(Err(mia)); Assert.Empty(mia.Of("profile:data"));
        }
        mia.Clear();
        await l.Set(mia, "{\"t\":\"profile:set\",\"displayName\":42}");
        Assert.NotNull(Err(mia));
        Assert.Equal("", l.Hub.Members.First(m => m.Name == "Mia").Profile.About);
    }

    [Fact]
    public async Task Login_names_of_other_people_are_blocked_including_lookalikes_and_offline_ones()
    {
        using var l = new Lodge3();
        var mia = await l.Join(MiaCode);                       // Elan (owner) and Zed are offline: they still count
        foreach (var bad in new[] { "Elan", "elan", "ELAN", "E1an", "El an", "EIan", "Z3d", "Bob", "B0b", "E-lan" })
        {
            mia.Clear();
            await l.Set(mia, Set(bad));
            Assert.Equal("That display name is taken", Err(mia));
        }
        Assert.Equal("", l.Hub.Members.First(m => m.Name == "Mia").Profile.DisplayName);
        // one's own login name (and its look-alikes) is not impersonation
        mia.Clear(); await l.Set(mia, Set("mia")); Assert.Null(Err(mia));
        Assert.Equal("mia", (string?)mia.Profile()["displayName"]);
    }

    [Fact]
    public async Task Display_names_are_unique_across_members_case_insensitively_with_the_fold_and_free_again_when_cleared()
    {
        using var l = new Lodge3();
        var mia = await l.Join(MiaCode); var zed = await l.Join(ZedCode);
        await l.Set(mia, Set("Moonlight"));
        Assert.Null(Err(mia));
        foreach (var bad in new[] { "moonlight", "MOONLIGHT", "M00nl1ght", "Moon light", "M0onIight" })
        {
            zed.Clear(); await l.Set(zed, Set(bad));
            Assert.Equal("That display name is taken", Err(zed));
        }
        zed.Clear(); await l.Set(zed, Set("Moonlit")); Assert.Null(Err(zed));
        // the holder going offline doesn't free the name (stored profile)
        mia.Hangup(); await Lodge3.Settle();
        l.Clock.Advance(TimeSpan.FromMinutes(2));
        zed.Clear(); await l.Set(zed, Set("Moonlight"));
        Assert.Equal("That display name is taken", Err(zed));
        // clearing frees it
        var mia2 = await l.Join(MiaCode);
        l.Clock.Advance(TimeSpan.FromMinutes(2));
        await l.Set(mia2, Set("")); Assert.Null(Err(mia2));
        Assert.Equal("", (string?)mia2.Profile()["displayName"]);
        l.Clock.Advance(TimeSpan.FromMinutes(2));
        zed.Clear(); await l.Set(zed, Set("Moonlight")); Assert.Null(Err(zed));
        Assert.Equal("Moonlight", (string?)zed.Profile()["displayName"]);
    }

    [Fact]
    public async Task Changing_the_display_name_is_limited_to_one_per_minute_but_resaving_the_same_name_is_free()
    {
        using var l = new Lodge3();
        var mia = await l.Join(MiaCode);
        await l.Set(mia, Set("First Name")); Assert.Null(Err(mia));
        mia.Clear(); await l.Set(mia, Set("Second Name"));                   // 3 s later
        Assert.Contains("once a minute", Err(mia));
        Assert.Equal("First Name", l.Hub.Members.First(m => m.Name == "Mia").Profile.DisplayName);
        mia.Clear(); await l.Set(mia, "{\"t\":\"profile:set\",\"displayName\":\"First Name\",\"about\":\"saving the form again\"}");
        Assert.Null(Err(mia));                                              // unchanged: no token spent
        Assert.Equal("saving the form again", l.Hub.Members.First(m => m.Name == "Mia").Profile.About);
        l.Clock.Advance(TimeSpan.FromSeconds(61));
        mia.Clear(); await l.Set(mia, Set("Second Name")); Assert.Null(Err(mia));
        Assert.Equal("Second Name", (string?)mia.Profile()["displayName"]);

        // the budget belongs to the identity: reconnecting doesn't reset it
        mia.Hangup(); await Lodge3.Settle();
        var again = await l.Join(MiaCode);
        await l.Set(again, Set("Third Name"));
        Assert.Contains("once a minute", Err(again));
    }

    [Fact]
    public async Task A_failed_attempt_does_not_use_up_the_minute()
    {
        using var l = new Lodge3();
        var mia = await l.Join(MiaCode);
        await l.Set(mia, Set("Elan")); Assert.Equal("That display name is taken", Err(mia));
        mia.Clear(); await l.Set(mia, Set("Mia Moon")); Assert.Null(Err(mia));
    }

    [Fact]
    public async Task Guests_cannot_set_a_display_name_but_everything_else_still_works()
    {
        using var l = new Lodge3();
        var g = await l.JoinGuest("Wanderer");
        await l.Set(g, Set("Fancy Guest"));
        Assert.Equal("Guests can't set a display name", Err(g));
        g.Clear(); await l.Set(g, Set(""));                                   // clearing nothing is harmless
        Assert.Null(Err(g));
        g.Clear(); await l.Set(g, "{\"t\":\"profile:set\",\"avatarId\":\"av.fish\"}");
        Assert.Equal("av.fish", (string?)g.Profile()["avatarId"]);
        Assert.Equal("", l.Hub.Members.First(m => m.Name == "Wanderer").Profile.DisplayName);
        // a guest cannot be named like a display name holder? Their name stays "(guest)"-tagged only for login names - unchanged.
    }

    [Fact]
    public async Task Owner_reset_clears_the_display_name_and_tells_everyone()
    {
        using var l = new Lodge3();
        var elan = await l.Join(T.ElanCode); var bob = await l.Join(T.BobCode); var mia = await l.Join(MiaCode);
        await l.Set(mia, Set("Rude Name"));
        bob.Say("{\"t\":\"profile:reset\",\"name\":\"Mia\"}"); await Lodge3.Settle();       // officers can't
        Assert.Equal("Rude Name", l.Hub.Members.First(m => m.Name == "Mia").Profile.DisplayName);
        mia.Clear(); bob.Clear();
        elan.Say("{\"t\":\"profile:reset\",\"name\":\"Mia\"}"); await Lodge3.Settle();
        Assert.Equal("", l.Hub.Members.First(m => m.Name == "Mia").Profile.DisplayName);
        Assert.Equal("", (string?)mia.Profile()["displayName"]);
        Assert.Equal("", (string?)Assert.Single(bob.Of("profile"))["displayName"]);
        Assert.Equal("", l.Hub.Profiles.Peek("Mia")!.DisplayName);
    }

    [Fact]
    public async Task Display_names_survive_a_restart_and_a_tampered_file_cannot_smuggle_bad_ones()
    {
        var dir = new TempDir();
        using (var l = new Lodge3(dir))
        {
            var mia = await l.Join(MiaCode);
            await l.Set(mia, Set("Moon Child"));
            l.Hub.Profiles.Flush();
            Assert.Contains("Moon Child", File.ReadAllText(Path.Combine(dir.Path, "profiles.json")));
            l.Dir = new TempDir(); // keep `dir` alive: Dispose below removes the replacement only
        }
        var s = new ProfileStore(dir.Path, T.Log, new FakeClock());
        Assert.Equal("Moon Child", s.Peek("Mia")!.DisplayName);
        File.WriteAllText(Path.Combine(dir.Path, "profiles.json"),
            "{\"mia\":{\"displayName\":\"<b>evil</b>\"},\"zed\":{\"displayName\":\"  Zed   Z \"},\"bob\":{\"displayName\":7}}");
        var s2 = new ProfileStore(dir.Path, T.Log, new FakeClock());
        Assert.Equal("", s2.Peek("mia")!.DisplayName);
        Assert.Equal("Zed Z", s2.Peek("zed")!.DisplayName);
        Assert.Equal("", s2.Peek("bob")!.DisplayName);
        dir.Dispose();
    }

    [Fact]
    public async Task An_invisible_member_display_name_is_not_leaked_through_lookup_as_online()
    {
        using var l = new Lodge3();
        var mia = await l.Join(MiaCode, invisible: true); var zed = await l.Join(ZedCode);
        await l.Set(mia, Set("Ghost Rider"));
        var seen = await l.Get(zed, "Mia");
        Assert.False((bool)seen["online"]!);                                   // behaves like offline, as before
        Assert.Empty(zed.Of("profile"));                                       // no broadcast to people who can't see her
    }
}
