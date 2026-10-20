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
    public void Name_key_merges_lookalikes_and_separators()
    {
        // the rules of the older fold, all kept
        Assert.Equal(ProfileRules.NameKey("Elan"), ProfileRules.NameKey("E1an"));
        Assert.Equal(ProfileRules.NameKey("Elan"), ProfileRules.NameKey("EIan"));
        Assert.Equal(ProfileRules.NameKey("Elan"), ProfileRules.NameKey("E|an"));
        Assert.Equal(ProfileRules.NameKey("Rose"), ProfileRules.NameKey("R0s3"));
        Assert.Equal(ProfileRules.NameKey("Seth"), ProfileRules.NameKey("5eth"));
        Assert.Equal(ProfileRules.NameKey("Elan"), ProfileRules.NameKey("E-l.an"));
        Assert.Equal(ProfileRules.NameKey("Elan"), ProfileRules.NameKey("  El   an "));
        Assert.NotEqual(ProfileRules.NameKey("Elan"), ProfileRules.NameKey("Elana"));
        // L5: Unicode's look-alike list (UTS #39)
        Assert.Equal(ProfileRules.NameKey("Elan"), ProfileRules.NameKey("E\u029Fan"));      // small capital L
        Assert.Equal(ProfileRules.NameKey("Bob"), ProfileRules.NameKey("B\u0585b"));        // Armenian oh
        Assert.Equal(ProfileRules.NameKey("Elan"), ProfileRules.NameKey("\u0395\u217Ca\u0578")); // Greek E, Roman numeral L, Armenian vo
        Assert.Equal(ProfileRules.NameKey("Mia"), ProfileRules.NameKey("rnia"));             // rn looks like m
        // honest names stay apart: accents are visible, other scripts are names of their own
        Assert.NotEqual(ProfileRules.NameKey("Elan"), ProfileRules.NameKey("\u00C9lan"));
        Assert.NotEqual(ProfileRules.NameKey("Zoe"), ProfileRules.NameKey("Zo\u00EB"));
        Assert.NotEqual(ProfileRules.NameKey("Sakura"), ProfileRules.NameKey("\u3055\u304F\u3089"));
    }

    [Fact]
    public void Every_lookalike_of_the_old_cyrillic_greek_table_still_folds()
    {
        int n = 0;
        for (char c = '\u0370'; c < '\u0530'; c++)
        {
            var latin = Names.Latin(c);
            if (latin == c) continue;
            Assert.Equal(ProfileRules.NameKey("x" + latin + "y"), ProfileRules.NameKey("x" + c + "y"));
            n++;
        }
        Assert.Equal(38, n);                                                     // the whole table
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
    public async Task Lookalikes_from_other_alphabets_of_a_login_or_display_name_are_refused()
    {
        using var l = new Lodge3();
        var mia = await l.Join(MiaCode); var zed = await l.Join(ZedCode);
        // the Guild Master's login (offline) and an officer's, with look-alike letters
        foreach (var bad in new[] { "E\u029Fan", "\u1D07lan", "\u0415lan", "El\u0430n", "\u0395\u217Ca\u0578", "B\u0585b", "\u0412\u043Eb" })
        {
            mia.Clear();
            await l.Set(mia, Set(bad));
            Assert.Equal("That display name is taken", Err(mia));
        }
        Assert.Equal("", l.Hub.Members.First(m => m.Name == "Mia").Profile.DisplayName);
        // another member's display name
        mia.Clear(); await l.Set(mia, Set("Moonlight")); Assert.Null(Err(mia));
        foreach (var bad in new[] { "M\u043E\u043Enlight", "Moon\u029Fight", "rnoonlight", "MOONL1GHT" })
        {
            zed.Clear(); await l.Set(zed, Set(bad));
            Assert.Equal("That display name is taken", Err(zed));
        }
        Assert.Equal("", l.Hub.Members.First(m => m.Name == "Zed").Profile.DisplayName);
    }

    [Fact]
    public async Task Honest_names_in_any_script_and_with_accents_stay_allowed()
    {
        using var l = new Lodge3();
        var mia = await l.Join(MiaCode); var zed = await l.Join(ZedCode);
        await l.Set(mia, Set("\u00C9lan")); Assert.Null(Err(mia));                     // accented: visibly not "Elan"
        Assert.Equal("\u00C9lan", (string?)mia.Profile()["displayName"]);
        foreach (var good in new[] { "\u3055\u304F\u3089", "\u039D\u03AF\u03BA\u03B7", "\u041C\u0430\u0440\u0438\u044F", "Zo\u00EB-Ann" })
        {
            l.Clock.Advance(TimeSpan.FromSeconds(61));
            zed.Clear(); await l.Set(zed, Set(good));
            Assert.Null(Err(zed));
            Assert.Equal(good, (string?)zed.Profile()["displayName"]);
        }
    }

    // L5: names stored before the look-alike check may share a key. Nothing is deleted or renamed: they keep working, only a change must
    // pick a free name, and the server logs how many there are (no names).
    [Fact]
    public async Task Stored_lookalike_display_names_are_kept_counted_and_a_change_must_be_free()
    {
        var dir = new TempDir();
        using (var first = new Lodge3(dir)) { first.Dir = new TempDir(); }       // writes the codes file; keep `dir`
        File.WriteAllText(Path.Combine(dir.Path, "profiles.json"),
            "{\"mia\":{\"displayName\":\"E\\u029Fan\"},\"zed\":{\"displayName\":\"Moonlight\"},\"bob\":{\"displayName\":\"M\\u043E\\u043Enlight\"}}");
        var cfg = LodgeConfig.ForTests(dir.Path);
        var log = new CaptureLogger();
        _ = new LodgeHub(cfg, new Auth(cfg, log), new FileStore(cfg), log, new FakeClock());
        var line = Assert.Single(log.Lines, x => x.StartsWith("Display names:"));
        Assert.Contains(" 3 stored display name(s) look like", line);       // Mia (like the GM's login) + Zed and Bob (like each other)
        Assert.DoesNotContain("Moonlight", line);
        Assert.DoesNotContain("Mia", line);

        using var l = new Lodge3(dir);
        var mia = await l.Join(MiaCode);
        Assert.Equal("E\u029Fan", l.Hub.Members.First(m => m.Name == "Mia").Profile.DisplayName);   // kept
        mia.Clear(); await l.Set(mia, "{\"t\":\"profile:set\",\"displayName\":\"E\u029Fan\",\"about\":\"hi\"}");
        Assert.Null(Err(mia));                                                  // saving the form again works
        Assert.Equal("hi", l.Hub.Members.First(m => m.Name == "Mia").Profile.About);
        mia.Clear(); await l.Set(mia, Set("E\u029F\u0430n"));                       // another look-alike: refused
        Assert.Equal("That display name is taken", Err(mia));
        Assert.Equal("E\u029Fan", l.Hub.Members.First(m => m.Name == "Mia").Profile.DisplayName);
        mia.Clear(); await l.Set(mia, Set("Mia Moon")); Assert.Null(Err(mia));  // a free one: fine
        Assert.Equal("Mia Moon", (string?)mia.Profile()["displayName"]);
        dir.Dispose();
    }

    [Fact]
    public void Lookalike_count_on_a_realistic_guild()
    {
        var logins = new[] { "Elan", "Bob", "Mia", "Zed", "Kira", "Thorgar", "Ana", "Sven", "Yuki", "Ole", "Dmitri", "Lena" };
        var displays = new List<(string, string)>
        {
            ("bob", "Bobby"), ("mia", "Mia Moon"), ("kira", "Kira the Swift"), ("thorgar", "Thorgar Ironfist"),
            ("ana", "\u00C1na"),                       // accented own name: not a look-alike
            ("yuki", "\u96EA"),                         // snow
            ("dmitri", "\u0414\u043C\u0438\u0442\u0440\u0438\u0439"),   // Dmitri in Cyrillic
            ("lena", "\u039B\u03AD\u03BD\u03B1"),          // Lena in Greek
            ("sven", "\u0405ven"),                      // Cyrillic S: looks like "Sven", but that is his own login - fine
            ("ole", "E\u029Fan"),                       // looks like the Guild Master's login
            ("zed", "Kira the 5wift"),                   // looks like Kira's display name (the older fold caught this one too)
        };
        Assert.Equal(3, ProfileRules.LookalikeDisplayNames(displays, logins));   // ole, zed, and kira (zed's twin)
        Assert.Equal(0, ProfileRules.LookalikeDisplayNames(displays.Take(9), logins));
        Assert.Equal(0, ProfileRules.LookalikeDisplayNames(new List<(string, string)>(), logins));
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
        Assert.Equal("", l.Hub.Members.First(m => m.Name == "Wanderer (guest)").Profile.DisplayName);
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
