using System;
using System.Security.Cryptography;
using System.Text;

namespace Elans.Shared
{
    // ES256 as every signed Elan format uses it: ECDSA on P-256 with SHA-256, the signature as IEEE P1363 r||s (exactly 64 bytes, never DER),
    // over the UTF-8 bytes of a message (or over raw bytes, for the release manifest). Verify never throws: anything malformed is just "no".
    public static class Es256
    {
        public const int SignatureBytes = 64;
        const string P256Oid = "1.2.840.10045.3.1.7";
        const DSASignatureFormat Format = DSASignatureFormat.IeeeP1363FixedFieldConcatenation;

        public static ECDsa NewKey() => ECDsa.Create(ECCurve.NamedCurves.nistP256);

        public static byte[] Sign(ECDsa key, byte[] data) => key.SignData(data, HashAlgorithmName.SHA256, Format);
        public static byte[] Sign(ECDsa key, string message) => Sign(key, Encoding.UTF8.GetBytes(message));

        public static bool Verify(ECDsa key, byte[] data, byte[] sig)
        {
            if (key == null || data == null || sig == null || sig.Length != SignatureBytes) return false;
            try { return key.VerifyData(data, sig, HashAlgorithmName.SHA256, Format); }
            catch (CryptographicException) { return false; }
        }
        public static bool Verify(ECDsa key, string message, byte[] sig) => message != null && Verify(key, Encoding.UTF8.GetBytes(message), sig);

        // a public key from its two 32-byte coordinates (how the Outposts pin the Cairn and release keys), or null when it is not a P-256 point
        public static ECDsa FromPoint(byte[] x, byte[] y)
        {
            if (x == null || y == null || x.Length != 32 || y.Length != 32) return null;
            ECDsa k = null;
            try
            {
                k = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = x, Y = y } });
                return k;
            }
            catch (Exception e) when (e is CryptographicException || e is ArgumentException || e is PlatformNotSupportedException) { k?.Dispose(); return null; }
        }

        // a P-256 public key from its base64url SubjectPublicKeyInfo (how a Lodge sends its key to the Cairn), or null for anything else:
        // another curve, RSA, junk, trailing bytes, a private key
        public static ECDsa FromSpki(string b64Url, out byte[] spki)
        {
            spki = B64.FromUrl(b64Url);
            if (spki == null || spki.Length < 60 || spki.Length > 200) { spki = null; return null; }
            var k = ECDsa.Create();
            try
            {
                k.ImportSubjectPublicKeyInfo(spki, out int read);
                if (read != spki.Length) throw new CryptographicException("trailing bytes");
                var p = k.ExportParameters(false);
                if (p.Curve.Oid?.Value != P256Oid || p.Q.X?.Length != 32) throw new CryptographicException("not P-256");
                return k;
            }
            catch (Exception e) when (e is CryptographicException || e is ArgumentException || e is PlatformNotSupportedException)
            {
                k.Dispose(); spki = null; return null;
            }
        }

        // the key id every format uses: the first 16 hex characters of SHA-256 over the SPKI DER
        public static string KeyIdOf(byte[] spki) => Sha256Hex(spki).Substring(0, 16);
        public static string KeyIdOf(ECDsa key) => KeyIdOf(key.ExportSubjectPublicKeyInfo());

        public static string Hex(byte[] b) => Convert.ToHexString(b ?? Array.Empty<byte>()).ToLowerInvariant();
        public static string Sha256Hex(byte[] b) => Hex(SHA256.HashData(b ?? Array.Empty<byte>()));
    }
}
