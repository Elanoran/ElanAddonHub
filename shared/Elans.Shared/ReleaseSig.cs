using System;
using System.Collections.Generic;
using System.Linq;

namespace Elans.Shared
{
    // The release manifest signature (Outpost 2.99+). Tools/release.py (addon repo, Python) writes manifest.json.sig:
    //   "elans-release-sig/1" \n <key id> \n <base64 of the ES256 signature, r||s> \n   - over the manifest's exact bytes
    // The Outpost checks it against the public keys it carries. The keys stay with the caller: this file only knows the format.
    public static class ReleaseSig
    {
        public const string Format = "elans-release-sig/1";

        public sealed class Key { public string Id, X, Y; }   // X / Y: the point's coordinates, plain base64 (32 bytes each)

        public enum Result { Valid, Missing, Invalid }

        // sigText = the .sig file (null = there is none). why = plain words for the log and the status line. requireSignature: a missing
        // signature is Invalid instead of Missing.
        public static Result Check(byte[] manifest, string sigText, IEnumerable<Key> keys, bool requireSignature, out string why)
        {
            if (sigText == null) { why = "the update information is not signed"; return requireSignature ? Result.Invalid : Result.Missing; }
            var lines = sigText.Replace("\r", "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length != 3 || lines[0] != Format) { why = "the update information's signature is not readable"; return Result.Invalid; }
            var key = (keys ?? Enumerable.Empty<Key>()).FirstOrDefault(k => k.Id == lines[1]);
            if (key == null) { why = "the update information is signed with an unknown key (" + Clip(lines[1]) + ")"; return Result.Invalid; }
            var sig = B64.FromStd(lines[2]);
            if (sig == null || sig.Length != Es256.SignatureBytes || manifest == null) { why = "the update information's signature is not readable"; return Result.Invalid; }
            using (var ec = Es256.FromPoint(B64.FromStd(key.X), B64.FromStd(key.Y)))
            {
                if (ec == null) { why = "the update information's signature could not be checked"; return Result.Invalid; }
                if (!Es256.Verify(ec, manifest, sig)) { why = "the update information does not match its signature"; return Result.Invalid; }
            }
            why = "signed with key " + key.Id;
            return Result.Valid;
        }

        // the .sig text for a manifest (tests; the real signing happens in release.py)
        public static string Make(System.Security.Cryptography.ECDsa key, string keyId, byte[] manifest) =>
            Format + "\n" + keyId + "\n" + Convert.ToBase64String(Es256.Sign(key, manifest)) + "\n";

        static string Clip(string s) => s.Length > 20 ? s.Substring(0, 20) + "..." : s;
    }
}
