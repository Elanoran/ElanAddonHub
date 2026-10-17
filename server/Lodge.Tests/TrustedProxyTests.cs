using System.Net;
using Lodge;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lodge.Tests;

// The real ForwardedHeadersMiddleware, configured exactly as Program.cs does (TrustedProxies.Configure): only the configured
// proxy (and loopback) may set the visitor IP - otherwise anyone could dodge the wrong-code lockout with a fake X-Forwarded-For.
public class TrustedProxyTests
{
    static async Task<(string Ip, string Scheme)> Through(string[] trusted, string remote, string xff, string? proto = null)
    {
        var o = new ForwardedHeadersOptions();
        TrustedProxies.Configure(o, trusted);
        string? ip = null, scheme = null;
        var mw = new ForwardedHeadersMiddleware(ctx => { ip = ctx.Connection.RemoteIpAddress?.ToString(); scheme = ctx.Request.Scheme; return Task.CompletedTask; },
            NullLoggerFactory.Instance, Options.Create(o));
        var c = new DefaultHttpContext();
        c.Connection.RemoteIpAddress = IPAddress.Parse(remote);
        c.Request.Scheme = "http";
        c.Request.Headers["X-Forwarded-For"] = xff;
        if (proto != null) c.Request.Headers["X-Forwarded-Proto"] = proto;
        await mw.Invoke(c);
        return (ip!, scheme!);
    }

    [Fact]
    public async Task The_configured_proxy_sets_the_visitor_ip_and_scheme()
    {
        var r = await Through(new[] { "192.0.2.200/32" }, "192.0.2.200", "203.0.113.7", "https");
        Assert.Equal("203.0.113.7", r.Ip);
        Assert.Equal("https", r.Scheme);
    }

    [Fact]
    public async Task Forwarded_headers_from_anyone_else_are_ignored()
    {
        // a neighbour on the same /24 but not the proxy, and an outsider: both keep their own address
        var r = await Through(new[] { "192.0.2.200/32" }, "192.0.2.201", "203.0.113.7", "https");
        Assert.Equal("192.0.2.201", r.Ip);
        Assert.Equal("http", r.Scheme);
        r = await Through(new[] { "192.0.2.200/32" }, "198.51.100.9", "127.0.0.1");
        Assert.Equal("198.51.100.9", r.Ip);
    }

    [Fact]
    public async Task With_no_proxy_configured_only_loopback_is_trusted()
    {
        Assert.Equal("198.51.100.9", (await Through(Array.Empty<string>(), "198.51.100.9", "203.0.113.7")).Ip);
        Assert.Equal("203.0.113.7", (await Through(Array.Empty<string>(), "127.0.0.1", "203.0.113.7")).Ip);
    }

    [Fact]
    public async Task A_spoofed_chain_is_cut_at_the_first_untrusted_hop()
    {
        // the client prepends a fake address; the proxy appends the real one - only the hop the proxy saw counts
        var r = await Through(new[] { "192.0.2.200" }, "192.0.2.200", "10.9.9.9, 203.0.113.7");
        Assert.Equal("203.0.113.7", r.Ip);
    }

    [Fact]
    public void Entries_parse_like_before_and_bad_ones_are_reported()
    {
        var bad = new List<string>();
        var nets = TrustedProxies.Parse(new[] { "192.0.2.200", "10.0.0.5/24", "2001:db8::1", "fd00::/8", "nonsense", "10.0.0.0/33", "1.2.3.4/x", " " }, bad);
        Assert.Equal(new[] { "192.0.2.200/32", "10.0.0.0/24", "2001:db8::1/128", "fd00::/8" }, nets.Select(n => n.ToString()).ToArray());
        Assert.Equal(new[] { "nonsense", "10.0.0.0/33", "1.2.3.4/x" }, bad.ToArray());
        Assert.True(nets[1].Contains(IPAddress.Parse("10.0.0.77")));
        Assert.False(nets[2].Contains(IPAddress.Parse("2001:db8::2"))); // a bare IPv6 address is that one address, not a /32 block
    }
}
