using System.Net;
using Elans.Shared;

namespace Elans.Shared.Tests;

// IpKey: what per-address limits count by - an IPv4 address, an IPv6 /64 (the Cairn's Limits.IpKey rule)
public class IpKeyTests
{
    [Theory]
    [InlineData("203.0.113.9", "203.0.113.9")]
    [InlineData("::ffff:203.0.113.9", "203.0.113.9")]                       // IPv4-mapped: its IPv4 address
    [InlineData("2001:db8:1:2:3:4:5:6", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2:ffff:ffff:ffff:ffff", "2001:db8:1:2::/64")]
    [InlineData("2001:DB8:1:2::1", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:3::1", "2001:db8:1:3::/64")]
    [InlineData("::1", "::/64")]
    [InlineData("fe80::1%3", "fe80::/64")]
    [InlineData("?", "?")]
    [InlineData("", "?")]
    [InlineData(null, "?")]
    [InlineData("not an address", "not an address")]
    [InlineData("2001:db8:1:2::/64", "2001:db8:1:2::/64")]                   // a key stays itself
    public void Keys(string? ip, string key)
    {
        Assert.Equal(key, IpKey.Of(ip));
        if (ip != null && IPAddress.TryParse(ip, out var a)) Assert.Equal(key, IpKey.Of(a));
    }

    [Fact]
    public void No_address_is_question_mark() => Assert.Equal("?", IpKey.Of((IPAddress?)null));
}
