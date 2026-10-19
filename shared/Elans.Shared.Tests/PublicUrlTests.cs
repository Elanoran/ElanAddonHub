using System.Net;
using Elans.Shared;

namespace Elans.Shared.Tests;

public class PublicUrlTests
{
    [Theory]
    [InlineData("https://Lodge.Example.com/lodge/", "https://lodge.example.com/lodge")]
    [InlineData("  https://lodge.example.com  ", "https://lodge.example.com")]
    [InlineData("https://lodge.example.com:443/x", "https://lodge.example.com/x")]
    [InlineData("https://lodge.example.com:8443/x", "https://lodge.example.com:8443/x")]
    [InlineData("https://1.1.1.1/lodge", "https://1.1.1.1/lodge")]
    [InlineData("https://[2606:4700::1111]/lodge", "https://[2606:4700::1111]/lodge")]
    [InlineData("https://bücher.example/lodge", "https://xn--bcher-kva.example/lodge")]
    public void Canonical_form(string input, string expected)
    {
        Assert.True(PublicUrl.TryNormalize(input, out var n, out var why), why.ToString());
        Assert.Equal(expected, n);
    }

    [Theory]
    [InlineData("", PublicUrl.Problem.Empty)]
    [InlineData(null, PublicUrl.Problem.Empty)]
    [InlineData("lodge.example.com", PublicUrl.Problem.NotUrl)]
    [InlineData("http://lodge.example.com", PublicUrl.Problem.NotHttps)]
    [InlineData("https://user@lodge.example.com", PublicUrl.Problem.UserQueryFragment)]
    [InlineData("https://lodge.example.com/?a=1", PublicUrl.Problem.UserQueryFragment)]
    [InlineData("https://lodge.example.com/#x", PublicUrl.Problem.UserQueryFragment)]
    [InlineData("https://lodge.example.com:80/", PublicUrl.Problem.Port)]
    [InlineData("https://localhost/lodge", PublicUrl.Problem.LocalName)]
    [InlineData("https://box.local/lodge", PublicUrl.Problem.LocalName)]
    [InlineData("https://nas.internal/", PublicUrl.Problem.LocalName)]
    [InlineData("https://lodgebox/", PublicUrl.Problem.LocalName)]
    [InlineData("https://192.168.1.10/", PublicUrl.Problem.PrivateIp)]
    [InlineData("https://10.0.0.1/", PublicUrl.Problem.PrivateIp)]
    [InlineData("https://127.0.0.1/", PublicUrl.Problem.PrivateIp)]
    [InlineData("https://100.64.1.1/", PublicUrl.Problem.PrivateIp)]       // carrier-grade NAT
    [InlineData("https://192.0.0.8/", PublicUrl.Problem.PrivateIp)]        // the Lodge's old copy let this one through
    [InlineData("https://203.0.113.5/", PublicUrl.Problem.PrivateIp)]      // documentation (old Lodge copy: let through)
    [InlineData("https://[::1]/", PublicUrl.Problem.PrivateIp)]
    [InlineData("https://[fd00::1]/", PublicUrl.Problem.PrivateIp)]
    [InlineData("https://[2002:c0a8:0101::1]/", PublicUrl.Problem.PrivateIp)]   // 6to4 wrapping 192.168.1.1 (old Lodge copy: let through)
    [InlineData("https://[2001:0:1:2::1]/", PublicUrl.Problem.PrivateIp)]       // Teredo
    [InlineData("https://[64:ff9b::a00:1]/", PublicUrl.Problem.PrivateIp)]      // NAT64 wrapping 10.0.0.1
    [InlineData("https://[::ffff:192.168.1.1]/", PublicUrl.Problem.PrivateIp)]  // IPv4-mapped
    [InlineData("https://lodge.example.com/a//b", PublicUrl.Problem.OddPath)]   // (old Lodge copy: let through)
    [InlineData("https://lodge.example.com/a%2fb", PublicUrl.Problem.OddPath)]
    public void Refused(string input, PublicUrl.Problem expected)
    {
        Assert.False(PublicUrl.TryNormalize(input, out var n, out var why));
        Assert.Null(n);
        Assert.Equal(expected, why);
    }

    [Fact]
    public void Too_long() => Assert.Equal(PublicUrl.Problem.TooLong, PublicUrl.TryNormalize("https://lodge.example.com/" + new string('a', 200), out _, out var w) ? PublicUrl.Problem.None : w);

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("1.1.1.1", true)]
    [InlineData("2606:4700::1111", true)]
    [InlineData("255.255.255.255", false)]
    [InlineData("0.1.2.3", false)]
    [InlineData("169.254.1.1", false)]
    [InlineData("172.20.0.1", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("198.18.0.1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("ff02::1", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("::", false)]
    public void Public_addresses(string ip, bool pub) => Assert.Equal(pub, PublicUrl.IsPublic(IPAddress.Parse(ip)));

    [Fact]
    public void Host_of_a_normalized_address() => Assert.Equal("lodge.example.com", PublicUrl.HostOf("https://lodge.example.com:8443/x"));
}
