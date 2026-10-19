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
}
