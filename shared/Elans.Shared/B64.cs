using System;

namespace Elans.Shared
{
    // Base64 for the signed formats. base64url (RFC 4648 section 5, no padding) carries keys, nonces and request signatures; plain base64
    // carries the notice, dataset and release signatures and the pinned key halves. Every decoder returns null instead of throwing.
    public static class B64
    {
        public static string Url(byte[] b) => Convert.ToBase64String(b ?? Array.Empty<byte>()).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        // strict base64url: only A-Z a-z 0-9 - _ (no padding, no spaces), at most maxChars characters
        public static byte[] FromUrl(string s, int maxChars = 4096)
        {
            if (string.IsNullOrEmpty(s) || s.Length > maxChars || s.Length % 4 == 1) return null;
            foreach (var ch in s)
                if (!(ch >= 'A' && ch <= 'Z' || ch >= 'a' && ch <= 'z' || ch >= '0' && ch <= '9' || ch == '-' || ch == '_')) return null;
            var t = s.Replace('-', '+').Replace('_', '/') + new string('=', (4 - s.Length % 4) % 4);
            try { return Convert.FromBase64String(t); } catch (FormatException) { return null; }
        }

        // plain base64 (padding allowed, as Convert writes it), at most maxChars characters
        public static byte[] FromStd(string s, int maxChars = 4096)
        {
            if (string.IsNullOrEmpty(s) || s.Length > maxChars) return null;
            try { return Convert.FromBase64String(s); } catch (FormatException) { return null; }
        }
    }
}
