using System;
using System.Globalization;
using System.Linq;

namespace Elans.Shared
{
    // The messages the Cairn and the Lodges sign (server/PROTOCOL.md, Cairn PROTOCOL.md). One definition for both sides: a change here is a
    // protocol change, so the golden strings in Elans.Shared.Tests must change with it - on purpose.
    //   request    "elans-cairn-req/1\n<METHOD>\n<path?query>\n<time>\n<nonce>\n<sha256 hex of the body>"   Lodge signs, Cairn checks
    //   challenge  "elans-cairn-challenge/1\n<nonce>\n<public url>"                                         Lodge signs, Cairn checks
    //   dataset    "elans-cairn/1\n<day>\n<seq>\n<content sha256 hex>"                                    Cairn signs, Lodges / Outposts check
    //   notice     "elans-cairn-notice/1\n<id>\n<created>\n<expires>\n<level>\n<to or *>\n<text>"          Cairn signs, Lodges / Outposts check
    // The prefixes differ and no field can hold a line break, so a signature made for one kind can never pass for another.
    public static class CairnFormats
    {
        public const string RequestPrefix = "elans-cairn-req/1", ChallengePrefix = "elans-cairn-challenge/1";
        public const string DatasetPrefix = "elans-cairn/1", NoticePrefix = "elans-cairn-notice/1";

        // the headers of a signed request
        public const string HKey = "X-Cairn-Key", HTime = "X-Cairn-Time", HNonce = "X-Cairn-Nonce", HSig = "X-Cairn-Signature";

        static string N(long v) => v.ToString(CultureInfo.InvariantCulture);

        public static string Request(string method, string pathAndQuery, string time, string nonce, string bodySha256Hex) =>
            RequestPrefix + "\n" + (method ?? "").ToUpperInvariant() + "\n" + pathAndQuery + "\n" + time + "\n" + nonce + "\n" + bodySha256Hex;

        public static string Challenge(string nonce, string publicUrl) => ChallengePrefix + "\n" + nonce + "\n" + publicUrl;

        public static string Dataset(string day, long seq, string contentSha256Hex) => DatasetPrefix + "\n" + day + "\n" + N(seq) + "\n" + contentSha256Hex;

        public static string Notice(long id, long created, long expires, string level, string to, string text) =>
            NoticePrefix + "\n" + N(id) + "\n" + N(created) + "\n" + N(expires) + "\n" + level + "\n" + (to ?? "*") + "\n" + text;   // null = everyone ("*"); an empty target stays empty, as the Cairn signs it

        // 16..64 base64url characters
        public static bool ValidNonce(string n) =>
            n != null && n.Length >= 16 && n.Length <= 64 && n.All(ch => ch >= 'A' && ch <= 'Z' || ch >= 'a' && ch <= 'z' || ch >= '0' && ch <= '9' || ch == '-' || ch == '_');

        // 16 lowercase hex characters
        public static bool ValidKeyId(string k) => k != null && k.Length == 16 && k.All(ch => ch >= '0' && ch <= '9' || ch >= 'a' && ch <= 'f');

        // unix seconds, digits only, at most 12 of them
        public static bool TryTime(string s, out long t)
        {
            t = 0;
            return s != null && s.Length <= 12 && long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out t);
        }
    }
}
