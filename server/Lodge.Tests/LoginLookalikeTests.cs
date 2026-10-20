using Xunit;

namespace Lodge.Tests;

// L5: a NEW login name (an invite made in the hub, the owner claim, a code added by the Guild Master) may not look like an existing
// login or another person's display name by Unicode's look-alike list (Names.Key = Confusables.NameKey). Existing logins are never
// renamed or blocked: they sign in exactly as before. Synthetic names and codes only.
public class LoginLookalikeTests
{
    const string Ip = "198.51.100.7";
    const string MiaCode = "synthetic0mia00code0000000000003";
    const string TwinCode = "synthetic0twin0code0000000000004";

    sealed class Rig : IDisposable
    {
        public readonly TempDir Dir = new();
        public readonly CaptureLogger Log = new();
        public readonly LodgeConfig Cfg;
        public readonly Auth Auth;
        public readonly LodgeHub Hub;
        public Rig(params string[] codes)
        {
            Cfg = T.Cfg(Dir.Path);
            T.WriteCodes(Cfg, codes);
            Auth = new Auth(Cfg, Log, new FakeClock());
            Hub = new LodgeHub(Cfg, Auth, new FileStore(Cfg), Log, new FakeClock());
        }
        public void Dispose() => Dir.Dispose();
    }

    static Rig Guild(params string[] more) =>
        new(new[] { $"Elan:{T.ElanCode}:owner", $"Bob:{T.BobCode}:officer", $"Mia:{MiaCode}:member" }.Concat(more).ToArray());

    [Theory]
    [InlineData("Eʟan")]               // small capital L
    [InlineData("Εlan")]               // Greek capital Epsilon
    [InlineData("Elаn")]               // Cyrillic a
    [InlineData("E1an")]
    [InlineData("ᴇʟᴀɴ")] // all small capitals
    [InlineData("Bօb")]                // Armenian oh
    [InlineData("rnia")]                    // rn looks like m
    public void An_invite_for_a_new_name_that_looks_like_an_existing_login_is_refused(string name)
    {
        using var r = Guild();
        var ex = Assert.Throws<InvalidOperationException>(() => r.Auth.CreateInvite("person", name, "member", "Elan"));
        Assert.Contains("looks just like a name that's already taken", ex.Message);
        var add = Assert.Throws<InvalidOperationException>(() => r.Auth.Add(name, "member"));
        Assert.Contains("already has a code (or a name that looks just like it)", add.Message);
        Assert.Equal(3, r.Auth.ListPersonal().Count);
    }

    [Fact]
    public void A_new_login_may_not_look_like_a_display_name_either()
    {
        using var r = Guild();
        lock (r.Hub.Profiles.Gate)
        {
            var p = r.Hub.Profiles.For("Mia");
            p.DisplayName = "Moonlight";
            r.Hub.Profiles.Save(p);
        }
        foreach (var name in new[] { "Moonlight", "moonlight", "Mооnlight", "Moonʟight", "M00nlight" })
        {
            var ex = Assert.Throws<InvalidOperationException>(() => r.Auth.CreateInvite("person", name, "member", "Elan"));
            Assert.Contains("looks just like", ex.Message);
            Assert.Throws<InvalidOperationException>(() => r.Auth.Add(name, "member"));
        }
        r.Auth.CreateInvite("person", "Moonlit", "member", "Elan");   // a different name is fine
    }

    [Fact]
    public void Honest_new_names_in_any_script_and_with_accents_are_allowed()
    {
        using var r = Guild();
        foreach (var name in new[] { "Élan", "Søren", "さくら", "Νίκη", "Мария", "Elana", "Robert" })
        {
            var (_, secret) = r.Auth.CreateInvite("person", name, "member", "Elan");
            var res = r.Auth.ExchangeInvite(Ip, secret, "PC", "cid-" + name.GetHashCode());
            Assert.Equal(200, res.Status);
            Assert.Equal(name, res.Who!.Name);
        }
    }

    [Fact]
    public void The_exchange_checks_again_with_the_same_code_and_text()
    {
        using var r = Guild();
        var (_, secret) = r.Auth.CreateInvite("person", "Nova", "member", "Elan");
        r.Auth.Add("N0va", "member");                                   // a look-alike was added while the invite waited
        var res = r.Auth.ExchangeInvite(Ip, secret, "PC", "cid-n");
        Assert.Equal(409, res.Status);
        Assert.Equal("name-taken", res.Reason);
        Assert.Equal("That name was just taken - ask for a new invite", res.Error);
        Assert.DoesNotContain(r.Auth.ListPersonal(), p => p.Name == "Nova");
    }

    [Fact]
    public void The_owner_claim_refuses_a_look_alike_with_the_same_code()
    {
        using var r = new Rig($"Elan:{T.ElanCode}:member");
        var (_, claim) = r.Auth.CreateOwnerClaim();
        var res = r.Auth.ExchangeInvite(Ip, claim, "PC", "c", "Eʟan");
        Assert.Equal(409, res.Status);
        Assert.Equal("name-taken", res.Reason);
        Assert.Equal("Eʟan looks just like a name that's already taken - pick another", res.Error);
        Assert.False(r.Auth.HasOwner());
        Assert.Equal(200, r.Auth.ExchangeInvite(Ip, claim, "PC", "c", "Elan").Status);   // the member's own name: promoted
    }

    [Fact]
    public void An_existing_look_alike_login_keeps_signing_in_and_is_counted_at_start()
    {
        // made before the look-alike check: "Eʟan" next to the Guild Master "Elan"
        using var r = Guild($"Eʟan:{TwinCode}:member");
        var line = Assert.Single(r.Log.Lines, x => x.StartsWith("Login names:"));
        Assert.Contains(" 2 existing login name(s) look like", line);       // Elan and Eʟan look like each other
        Assert.DoesNotContain("Elan", line);
        Assert.Equal("Eʟan", r.Auth.Check(TwinCode)!.Name);           // signs in as before, as herself
        Assert.Equal("Elan", r.Auth.Check(T.ElanCode)!.Name);
        Assert.Equal("owner", r.Auth.Check(T.ElanCode)!.Role);
        Assert.Equal("member", r.Auth.Check(TwinCode)!.Role);

        // a device link for her existing account still works (an existing name is looked up exactly, not by the look-alike key)
        var (_, secret) = r.Auth.CreateInvite("person", "Eʟan", "member", "Elan");
        var res = r.Auth.ExchangeInvite(Ip, secret, "Laptop", "cid-twin");
        Assert.Equal(200, res.Status);
        Assert.Equal("Eʟan", res.Who!.Name);
        Assert.Equal("member", res.Who.Role);
        Assert.NotNull(r.Auth.CheckCredential(res.Token, true));
        // and the Guild Master's own device link too
        var (_, gm) = r.Auth.CreateInvite("person", "Elan", "member", "Elan");
        var g = r.Auth.ExchangeInvite(Ip, gm, "PC", "cid-gm");
        Assert.Equal(200, g.Status);
        Assert.Equal("owner", g.Who!.Role);
    }

    [Fact]
    public void Login_lookalike_count_on_a_realistic_guild()
    {
        var logins = new[] { "Elan", "Bob", "Mia", "Zed", "Kira", "Thorgar", "Ana", "Sven", "Yuki", "Élan", "B0b" };
        var displays = new List<(string, string)> { ("kira", "Kira the Swift"), ("zed", "Thorgar"), ("ana", "Ana") };
        // B0b ~ Bob (2), Thorgar ~ Zed's display name (1); Élan is not Elan; Ana's display name is her own login
        Assert.Equal(3, ProfileRules.LookalikeLogins(displays, logins));
        Assert.Equal(0, ProfileRules.LookalikeLogins(new List<(string, string)>(), logins.Take(9)));
    }
}
