using Xunit;

namespace Lodge.Tests;

// Small fixes from the 2026-10-19 security review: what a failed admin request tells the client, and the identity an HTTP route uses.
public class ReviewFixesTests
{
    [Fact]
    public void A_failed_admin_request_shows_the_servers_own_messages_and_nothing_else()
    {
        using var d = new TempDir();
        var auth = new Auth(T.Cfg(d.Path), T.Log);
        auth.Add("Elan", "member");
        // a message this server writes for people goes through
        var mine = Assert.Throws<InvalidOperationException>(() => auth.Add("bad:name", "member"));
        Assert.Equal(mine.Message, AdminModule.ErrorText(mine));
        // anything else - a framework's InvalidOperationException, an IO error naming a path - is a plain text (the detail is in the log)
        var linq = Assert.Throws<InvalidOperationException>(() => Array.Empty<int>().First());
        Assert.Equal(AdminModule.FailedText, AdminModule.ErrorText(linq));
        Assert.Equal(AdminModule.FailedText, AdminModule.ErrorText(new IOException("Access to the path '/var/lib/lodge/codes' is denied")));
        Assert.Equal(AdminModule.FailedText, AdminModule.ErrorText(new NullReferenceException()));
    }

    [Fact]
    public void An_HTTP_route_uses_only_the_verified_identity()
    {
        var member = new Identity("Elan", true, "officer");
        Assert.Equal(("officer", "Elan", "p:elan"), (member.Rank, member.By, member.RateKey("1.2.3.4")));
        // a non-personal credential is a guest whatever its role says, uploads as "Guest" and is budgeted per address
        var shared = new Identity(null, false, "officer");
        Assert.Equal(("guest", "Guest", "g:1.2.3.4"), (shared.Rank, shared.By, shared.RateKey("1.2.3.4")));
    }

    // M5: downloads (chat files and shared files together) have a budget per identity and hour: requests and bytes; guests by address
    [Fact]
    public void Downloads_have_a_request_and_byte_budget_per_identity_and_hour()
    {
        const long MB = 1024 * 1024;
        var clock = new FakeClock();
        var cfg = LodgeConfig.ForTests(Path.GetTempPath());
        cfg.DownloadsPerHour = 20; cfg.DownloadMbPerHour = 100;
        var b = new DownloadBudget(cfg, clock);
        var elan = new Identity("Elan", true, "member").RateKey("192.0.2.1");
        for (int i = 0; i < 20; i++) Assert.True(b.TryTake(elan, 1));
        Assert.False(b.TryTake(elan, 1));                                       // the 21st request in the hour
        Assert.True(b.TryTake(new Identity("Bob", true, "member").RateKey("192.0.2.1"), 1));   // someone else: their own budget

        // bytes: 100 MB an hour; the first download of a window always goes, however big
        var guest = new Identity(null, false, "guest");
        Assert.True(b.TryTake(guest.RateKey("2001:db8:1:2::1"), 300 * MB));
        Assert.False(b.TryTake(guest.RateKey("2001:db8:1:2::2"), 1));           // the same /64: the same guest budget, spent
        Assert.True(b.TryTake(guest.RateKey("2001:db8:1:3::1"), 60 * MB));      // another /64
        Assert.True(b.TryTake(guest.RateKey("2001:db8:1:3::9"), 40 * MB));
        Assert.False(b.TryTake(guest.RateKey("2001:db8:1:3::9"), 1));

        // a rolling hour: what is older comes back
        clock.Advance(TimeSpan.FromMinutes(61));
        Assert.True(b.TryTake(elan, 1));
        Assert.True(b.TryTake(guest.RateKey("2001:db8:1:2::1"), 99 * MB));
    }

    [Fact]
    public void The_download_budget_forgets_idle_identities()
    {
        var clock = new FakeClock();
        var b = new DownloadBudget(LodgeConfig.ForTests(Path.GetTempPath()), clock);
        for (int i = 0; i < 255; i++) b.TryTake("g:198.51.100." + i, 1);
        Assert.Equal(255, b.Tracked);
        clock.Advance(TimeSpan.FromHours(2));
        b.TryTake("g:203.0.113.1", 1);                                           // the 256th call sweeps
        Assert.Equal(1, b.Tracked);
    }
}
