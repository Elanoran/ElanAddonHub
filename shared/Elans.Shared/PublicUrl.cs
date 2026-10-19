using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace Elans.Shared
{
    // A Lodge's public address: the canonical form both the Lodge and the Cairn sign (the join challenge) and the rule for what the Cairn may
    // ever call. https, lowercase host, ":port" only when not 443 (and then 1024 or above), the path without a trailing '/', no user / query /
    // fragment, nothing local or private (localhost, *.local, a bare host name, private and special IP ranges). Each side words the reason
    // itself (the Lodge to its Guild Master, the Cairn to its log): TryNormalize only says which rule failed.
    public static class PublicUrl
    {
        public const int MaxLength = 200;

        public enum Problem { None, Empty, TooLong, NotUrl, NotHttps, UserQueryFragment, NoHost, Port, LocalName, PrivateIp, OddPath }

        public static bool TryNormalize(string s, out string norm, out Problem why)
        {
            if (!Normalize(s, out norm, out why)) return false;
            // what comes out must pass again as itself (IDNA can turn an odd name into a host the rules refuse, e.g. a zero-width space before
            // "_x.com"): both sides re-check the canonical form, so an address that isn't stable is no address
            if (Normalize(norm, out var again, out _) && again == norm) return true;
            norm = null; why = Problem.NoHost;
            return false;
        }

        static bool Normalize(string s, out string norm, out Problem why)
        {
            norm = null; why = Problem.None;
            s = (s ?? "").Trim();
            if (s.Length == 0) { why = Problem.Empty; return false; }
            if (s.Length > MaxLength) { why = Problem.TooLong; return false; }
            if (!TryUri(s, out var u)) { why = Problem.NotUrl; return false; }
            if (u.Scheme != Uri.UriSchemeHttps) { why = Problem.NotHttps; return false; }
            if (!string.IsNullOrEmpty(u.UserInfo) || !string.IsNullOrEmpty(u.Query) || !string.IsNullOrEmpty(u.Fragment) || s.Contains('?') || s.Contains('#'))
            { why = Problem.UserQueryFragment; return false; }
            if (!(u.HostNameType == UriHostNameType.Dns || u.HostNameType == UriHostNameType.IPv4 || u.HostNameType == UriHostNameType.IPv6)) { why = Problem.NoHost; return false; }
            if (u.Port != 443 && u.Port < 1024) { why = Problem.Port; return false; }
            // IdnHost throws on a host IDNA refuses (e.g. a zero-width space as the host): that is no host
            string host;
            try { host = u.IdnHost.ToLowerInvariant(); } catch (UriFormatException) { why = Problem.NoHost; return false; }
            if (u.HostNameType == UriHostNameType.Dns && (host == "localhost" || host.EndsWith(".localhost") || host.EndsWith(".local") || host.EndsWith(".internal") || !host.Contains('.')))
            { why = Problem.LocalName; return false; }
            if (u.HostNameType != UriHostNameType.Dns && (!IPAddress.TryParse(u.Host.Trim('[', ']'), out var ip) || !IsPublic(ip)))
            { why = Problem.PrivateIp; return false; }
            // a "name" that IDNA turns into an IP address (a zero-width space or full-width digits around 127.0.0.1): the IP rules apply
            if (u.HostNameType == UriHostNameType.Dns && IPAddress.TryParse(host, out var mapped) && !IsPublic(mapped))
            { why = Problem.PrivateIp; return false; }
            var path = u.AbsolutePath.TrimEnd('/');
            if (path.Contains("//") || path.Contains("/../") || path.EndsWith("/..") || path.IndexOf("%2f", StringComparison.OrdinalIgnoreCase) >= 0)
            { why = Problem.OddPath; return false; }
            norm = "https://" + (u.HostNameType == UriHostNameType.IPv6 ? "[" + host.Trim('[', ']') + "]" : host)
                + (u.Port == 443 ? "" : ":" + u.Port.ToString(CultureInfo.InvariantCulture)) + path;
            return true;
        }

        // Uri.TryCreate itself can throw on odd input (IndexOutOfRangeException in Uri.CreateUriInfo for "//", a bidi control, "//" and a punycode host): no URL then
        static bool TryUri(string s, out Uri u)
        {
            try { return Uri.TryCreate(s, UriKind.Absolute, out u); }
            catch (Exception e) when (e is IndexOutOfRangeException || e is UriFormatException || e is ArgumentException) { u = null; return false; }
        }

        public static string HostOf(string norm)
        {
            if (!TryUri(norm, out var u)) return "";
            try { return u.IdnHost.ToLowerInvariant(); } catch (UriFormatException) { return ""; }
        }

        static bool In(byte[] a, byte[] net, int bits)
        {
            for (int i = 0; i < bits / 8; i++) if (a[i] != net[i]) return false;
            int rest = bits % 8;
            if (rest == 0) return true;
            int mask = 0xFF << (8 - rest) & 0xFF;
            return (a[bits / 8] & mask) == (net[bits / 8] & mask);
        }

        static byte[] V6(params byte[] head) => head.Concat(new byte[16 - head.Length]).ToArray();

        static readonly (byte[] net, int bits)[] V4Blocked =
        {
            (new byte[] { 0, 0, 0, 0 }, 8), (new byte[] { 10, 0, 0, 0 }, 8), (new byte[] { 100, 64, 0, 0 }, 10), (new byte[] { 127, 0, 0, 0 }, 8),
            (new byte[] { 169, 254, 0, 0 }, 16), (new byte[] { 172, 16, 0, 0 }, 12), (new byte[] { 192, 0, 0, 0 }, 24), (new byte[] { 192, 0, 2, 0 }, 24),
            (new byte[] { 192, 88, 99, 0 }, 24), (new byte[] { 192, 168, 0, 0 }, 16), (new byte[] { 198, 18, 0, 0 }, 15), (new byte[] { 198, 51, 100, 0 }, 24),
            (new byte[] { 203, 0, 113, 0 }, 24), (new byte[] { 224, 0, 0, 0 }, 4), (new byte[] { 240, 0, 0, 0 }, 4),
        };

        static readonly (byte[] net, int bits)[] V6Blocked =
        {
            (new byte[16], 127),                     // :: and ::1 (unspecified, loopback)
            (V6(0xfe, 0x80), 10),                    // link-local
            (V6(0xfe, 0xc0), 10),                    // site-local (old)
            (V6(0xfc), 7),                           // unique local
            (V6(0xff), 8),                           // multicast
            (V6(0x20, 0x01, 0x0d, 0xb8), 32),        // documentation
            (V6(0x20, 0x01, 0x00, 0x00), 32),        // Teredo (carries an IPv4 inside)
            (V6(0x20, 0x02), 16),                    // 6to4 (carries an IPv4 inside)
            (V6(0x00, 0x64, 0xff, 0x9b), 32),        // NAT64 (64:ff9b::/96 and the local-use /48)
            (V6(0x01, 0x00), 64),                    // discard-only
        };

        // true only for an address on the public internet
        public static bool IsPublic(IPAddress a)
        {
            if (a == null) return false;
            if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
            var b = a.GetAddressBytes();
            if (a.AddressFamily == AddressFamily.InterNetwork)
            {
                if (b[0] == 255 && b[1] == 255 && b[2] == 255 && b[3] == 255) return false;
                return !V4Blocked.Any(x => In(b, x.net, x.bits));
            }
            if (a.AddressFamily == AddressFamily.InterNetworkV6)
            {
                if (a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || a.IsIPv6Multicast || IPAddress.IsLoopback(a)) return false;
                if (b.Take(12).All(x => x == 0)) return false;   // IPv4-compatible ::a.b.c.d (deprecated)
                return !V6Blocked.Any(x => In(b, x.net, x.bits));
            }
            return false;
        }
    }
}
