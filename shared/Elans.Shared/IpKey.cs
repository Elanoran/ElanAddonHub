using System.Net;
using System.Net.Sockets;

namespace Elans.Shared
{
    // The key per-address limits count by (wrong-code lockouts, request rates, budgets): an IPv4 address as itself (a /32), an IPv4-mapped
    // IPv6 address as its IPv4 address, and any other IPv6 address by its /64 ("2001:db8:1:2::/64") - one home or server gets a whole /64,
    // so a limit per IPv6 address is no limit: rotating through it would fill any table. "?" when there is no address.
    // The same rule as the Cairn's Limits.IpKey (1.6.1); top-level in Elans.Shared so the Cairn's synced copy can use this one.
    public static class IpKey
    {
        public static string Of(IPAddress a)
        {
            if (a == null) return "?";
            if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
            if (a.AddressFamily != AddressFamily.InterNetworkV6) return a.ToString();
            var b = a.GetAddressBytes();
            for (int i = 8; i < 16; i++) b[i] = 0;
            return new IPAddress(b).ToString() + "/64";
        }

        // the same for an address as text; anything that is no address (null, "", "?") is kept as it is ("?" for null / empty)
        public static string Of(string ip)
        {
            if (string.IsNullOrEmpty(ip)) return "?";
            return IPAddress.TryParse(ip, out var a) ? Of(a) : ip;
        }
    }
}
