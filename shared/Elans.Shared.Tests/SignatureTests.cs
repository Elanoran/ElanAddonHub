using System.Security.Cryptography;
using System.Text;
using Elans.Shared;

namespace Elans.Shared.Tests;

public class B64Tests
{
    [Fact]
    public void Url_round_trips_without_padding()
    {
        for (int n = 0; n < 40; n++)
        {
            var b = RandomNumberGenerator.GetBytes(n);
            var s = B64.Url(b);
            Assert.DoesNotContain("=", s);
            Assert.DoesNotContain("+", s);
            Assert.DoesNotContain("/", s);
            Assert.Equal(b, n == 0 ? Array.Empty<byte>() : B64.FromUrl(s));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc=")]        // padding is not base64url here
    [InlineData("ab+c")]        // plain base64 characters
    [InlineData("ab/c")]
    [InlineData("ab c")]
    [InlineData("abcde")]       // length % 4 == 1 can't be base64
    public void FromUrl_refuses_junk(string s) => Assert.Null(B64.FromUrl(s));

    [Fact]
    public void FromUrl_has_a_length_cap() => Assert.Null(B64.FromUrl(new string('A', 5000)));

    [Fact]
    public void FromStd_refuses_junk_without_throwing()
    {
        Assert.Null(B64.FromStd("!!!"));
        Assert.Null(B64.FromStd(null));
        Assert.Equal(new byte[] { 1, 2, 3 }, B64.FromStd("AQID"));
    }
}

public class Es256Tests
{
    [Fact]
    public void Sign_then_verify_and_tampering_fails()
    {
        using var k = Es256.NewKey();
        var sig = Es256.Sign(k, "hello");
        Assert.Equal(64, sig.Length);
        Assert.True(Es256.Verify(k, "hello", sig));
        Assert.False(Es256.Verify(k, "hellO", sig));
        var bad = (byte[])sig.Clone(); bad[10] ^= 1;
        Assert.False(Es256.Verify(k, "hello", bad));
        using var other = Es256.NewKey();
        Assert.False(Es256.Verify(other, "hello", sig));
    }

    [Fact]
    public void Only_64_byte_P1363_signatures_count()
    {
        using var k = Es256.NewKey();
        var der = k.SignData(Encoding.UTF8.GetBytes("x"), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        Assert.False(Es256.Verify(k, "x", der));
        Assert.False(Es256.Verify(k, "x", new byte[63]));
        Assert.False(Es256.Verify(k, "x", new byte[65]));
        Assert.False(Es256.Verify(k, "x", new byte[64]));
        Assert.False(Es256.Verify(null, "x", new byte[64]));
        Assert.False(Es256.Verify(k, (string)null, new byte[64]));
    }

    [Fact]
    public void Default_VerifyData_format_matches_P1363()
    {
        // the Outposts used ECDsa.VerifyData without a format before Elans.Shared: .NET's default is r||s too, so nothing old breaks
        using var k = Es256.NewKey();
        var data = Encoding.UTF8.GetBytes("format");
        Assert.True(k.VerifyData(data, Es256.Sign(k, data), HashAlgorithmName.SHA256));
        Assert.True(Es256.Verify(k, data, k.SignData(data, HashAlgorithmName.SHA256)));
    }

    [Fact]
    public void FromPoint_accepts_a_real_point_and_refuses_others()
    {
        using var k = Es256.NewKey();
        var p = k.ExportParameters(false);
        using var pub = Es256.FromPoint(p.Q.X, p.Q.Y);
        Assert.NotNull(pub);
        Assert.True(Es256.Verify(pub, "m", Es256.Sign(k, "m")));
        Assert.Null(Es256.FromPoint(new byte[31], p.Q.Y));
        Assert.Null(Es256.FromPoint(null, p.Q.Y));
        var y = (byte[])p.Q.Y.Clone(); y[5] ^= 0x40;   // no longer on the curve
        Assert.Null(Es256.FromPoint(p.Q.X, y));
    }

    [Fact]
    public void FromSpki_takes_P256_only()
    {
        using var k = Es256.NewKey();
        var spki = k.ExportSubjectPublicKeyInfo();
        using var pub = Es256.FromSpki(B64.Url(spki), out var back);
        Assert.NotNull(pub);
        Assert.Equal(spki, back);
        Assert.Equal(Es256.KeyIdOf(k), Es256.KeyIdOf(spki));
        Assert.Equal(16, Es256.KeyIdOf(spki).Length);

        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Assert.Null(Es256.FromSpki(B64.Url(p384.ExportSubjectPublicKeyInfo()), out _));
        using var rsa = RSA.Create(2048);
        Assert.Null(Es256.FromSpki(B64.Url(rsa.ExportSubjectPublicKeyInfo()), out _));
        Assert.Null(Es256.FromSpki(B64.Url(spki.Concat(new byte[] { 0 }).ToArray()), out _));   // trailing byte
        Assert.Null(Es256.FromSpki(B64.Url(k.ExportPkcs8PrivateKey()), out _));                  // a private key
        Assert.Null(Es256.FromSpki("junk!", out _));
    }

    [Fact]
    public void Key_id_is_the_first_16_hex_of_sha256_of_the_spki()
    {
        var spki = Encoding.ASCII.GetBytes("not really a key");
        Assert.Equal(Convert.ToHexString(SHA256.HashData(spki)).ToLowerInvariant()[..16], Es256.KeyIdOf(spki));
    }
}

public class CairnFormatsTests
{
    // golden strings: these ARE the protocol. If one of these has to change, every Lodge, Cairn and Outpost changes with it.
    [Fact]
    public void Request_message() =>
        Assert.Equal("elans-cairn-req/1\nPOST\n/v1/contribute?x=1\n1792000000\nAAAAAAAAAAAAAAAAAAAA\n" + new string('a', 64),
            CairnFormats.Request("post", "/v1/contribute?x=1", "1792000000", "AAAAAAAAAAAAAAAAAAAA", new string('a', 64)));

    [Fact]
    public void Challenge_message() =>
        Assert.Equal("elans-cairn-challenge/1\nNONCENONCENONCE1\nhttps://lodge.example/lodge",
            CairnFormats.Challenge("NONCENONCENONCE1", "https://lodge.example/lodge"));

    [Fact]
    public void Dataset_message() =>
        Assert.Equal("elans-cairn/1\n2026-10-19\n12\n" + new string('b', 64), CairnFormats.Dataset("2026-10-19", 12, new string('b', 64)));

    [Fact]
    public void Notice_message_and_its_target_rule()
    {
        Assert.Equal("elans-cairn-notice/1\n7\n100\n200\nwarning\n*\nServer update", CairnFormats.Notice(7, 100, 200, "warning", null, "Server update"));
        Assert.Equal("elans-cairn-notice/1\n7\n100\n200\ninfo\nlodge-a\nHi", CairnFormats.Notice(7, 100, 200, "info", "lodge-a", "Hi"));
        // the Cairn signs `target ?? "*"`: an empty target stays empty (turning it into "*" would break that notice's signature)
        Assert.Equal("elans-cairn-notice/1\n7\n100\n200\ninfo\n\nHi", CairnFormats.Notice(7, 100, 200, "info", "", "Hi"));
    }

    [Fact]
    public void Notice_v2_message_names_the_target_Lodge()
    {
        Assert.Equal("elans-cairn-notice/2\n7\n100\n200\nwarning\n*\nServer update", CairnFormats.NoticeV2(7, 100, 200, "warning", null, "Server update"));
        Assert.Equal("elans-cairn-notice/2\n7\n100\n200\nwarning\n*\nServer update", CairnFormats.NoticeV2(7, 100, 200, "warning", "*", "Server update"));
        Assert.Equal("elans-cairn-notice/2\n8\n100\n200\ncritical\nhttps://lodge.example.com/guild\nPatch",
            CairnFormats.NoticeV2(8, 100, 200, "critical", "https://lodge.example.com/guild", "Patch"));
        // the same fields never give the same text in v1 and v2, so a v1 signature can't pass as a v2 one (or the other way round)
        Assert.NotEqual(CairnFormats.Notice(7, 100, 200, "info", "*", "Hi"), CairnFormats.NoticeV2(7, 100, 200, "info", "*", "Hi"));
    }

    [Fact]
    public void A_v1_signature_never_verifies_as_v2()
    {
        using var cairn = Es256.NewKey();
        var p = cairn.ExportParameters(false);
        using var pinned = Es256.FromPoint(p.Q.X, p.Q.Y);
        var v1 = Es256.Sign(cairn, CairnFormats.Notice(3, 10, 20, "info", "*", "Hi"));
        var v2 = Es256.Sign(cairn, CairnFormats.NoticeV2(3, 10, 20, "info", "*", "Hi"));
        Assert.True(Es256.Verify(pinned, CairnFormats.Notice(3, 10, 20, "info", "*", "Hi"), v1));
        Assert.True(Es256.Verify(pinned, CairnFormats.NoticeV2(3, 10, 20, "info", "*", "Hi"), v2));
        Assert.False(Es256.Verify(pinned, CairnFormats.NoticeV2(3, 10, 20, "info", "*", "Hi"), v1));
        Assert.False(Es256.Verify(pinned, CairnFormats.Notice(3, 10, 20, "info", "*", "Hi"), v2));
    }

    [Fact]
    public void Prefixes_differ_so_signatures_never_cross_kinds()
    {
        var all = new[] { CairnFormats.RequestPrefix, CairnFormats.ChallengePrefix, CairnFormats.DatasetPrefix, CairnFormats.NoticePrefix, CairnFormats.NoticeV2Prefix };
        Assert.Equal(all.Length, all.Distinct().Count());
        foreach (var a in all) foreach (var b in all) if (a != b) Assert.False((a + "\n").StartsWith(b + "\n"));
    }

    [Fact]
    public void A_notice_signed_by_the_Cairn_way_verifies()
    {
        // the Cairn's Dataset.SignText: plain base64 of SignData(UTF-8 message) with the default (r||s) format
        using var cairn = Es256.NewKey();
        var msg = CairnFormats.Notice(3, 10, 20, "critical", null, "Patch your Lodge");
        var sig = Convert.ToBase64String(cairn.SignData(Encoding.UTF8.GetBytes(msg), HashAlgorithmName.SHA256));
        var p = cairn.ExportParameters(false);
        using var pinned = Es256.FromPoint(B64.FromUrl(B64.Url(p.Q.X)), B64.FromUrl(B64.Url(p.Q.Y)));
        Assert.True(Es256.Verify(pinned, msg, B64.FromStd(sig)));
        Assert.False(Es256.Verify(pinned, CairnFormats.Notice(3, 10, 20, "critical", null, "Patch your Lodge!"), B64.FromStd(sig)));
    }

    [Theory]
    [InlineData("AAAAAAAAAAAAAAAA", true)]
    [InlineData("AAAAAAAAAAAAAAA", false)]           // 15
    [InlineData("AAAAAAAAAAAAAAA=", false)]
    [InlineData("AAAAAAAA\nAAAAAAAA", false)]        // a line break could forge another field
    public void Nonce_rule(string n, bool ok) => Assert.Equal(ok, CairnFormats.ValidNonce(n));

    [Fact]
    public void Key_id_and_time_rules()
    {
        Assert.True(CairnFormats.ValidKeyId("0123456789abcdef"));
        Assert.False(CairnFormats.ValidKeyId("0123456789ABCDEF"));
        Assert.False(CairnFormats.ValidKeyId("0123456789abcde"));
        Assert.True(CairnFormats.TryTime("1792000000", out var t) && t == 1792000000);
        Assert.False(CairnFormats.TryTime("-1", out _));
        Assert.False(CairnFormats.TryTime("+5", out _));
        Assert.False(CairnFormats.TryTime("1234567890123", out _));
        Assert.False(CairnFormats.TryTime(null, out _));
    }
}

public class ReleaseSigTests
{
    // made by the same code path as Tools/release.py sign_manifest() (Python "cryptography", DER -> r||s), with a throwaway key
    static readonly ReleaseSig.Key PyKey = new() { Id = "ecc196ce5ace1add", X = "2Xl9rKFtwsZf42k/3PNHhsLdfG1kqa9L4k5ARHISxMA=", Y = "Gxtn6HsmTJvfRHXrHkM7duug51r/1fOcTON5rMup/EA=" };
    static readonly byte[] PyManifest = Convert.FromBase64String("ew0KICAiaHViIjogeyJ2ZXJzaW9uIjogIjkuOS45IiwgInVybCI6ICJodHRwczovL2dpdGh1Yi5jb20veC95LmV4ZSIsICJzaGEyNTYiOiAiYWJhYmFiYWJhYmFiYWJhYmFiYWJhYmFiYWJhYmFiYWJhYmFiYWJhYmFiYWJhYmFiYWJhYmFiYWJhYmFiYWJhYiJ9DQp9DQo=");
    const string PySig = "elans-release-sig/1\necc196ce5ace1add\nCz07jwKWSwYuP+ZGoLVWN8MCq/gTaBdWUlAHqFNSo7xGmbhHxSUztR6lCFyIe9m+niLS2xZFfqRQkeSjmbaYLA==\n";

    [Fact]
    public void A_signature_from_release_py_verifies()
    {
        Assert.Equal(ReleaseSig.Result.Valid, ReleaseSig.Check(PyManifest, PySig, new[] { PyKey }, true, out var why));
        Assert.Contains("ecc196ce5ace1add", why);
    }

    [Fact]
    public void One_changed_byte_is_refused()
    {
        var t = (byte[])PyManifest.Clone(); t[^5] ^= 1;
        Assert.Equal(ReleaseSig.Result.Invalid, ReleaseSig.Check(t, PySig, new[] { PyKey }, true, out var why));
        Assert.Contains("does not match", why);
    }

    [Fact]
    public void Unknown_key_missing_and_junk()
    {
        Assert.Equal(ReleaseSig.Result.Invalid, ReleaseSig.Check(PyManifest, PySig, Array.Empty<ReleaseSig.Key>(), true, out var w1));
        Assert.Contains("unknown key", w1);
        Assert.Equal(ReleaseSig.Result.Missing, ReleaseSig.Check(PyManifest, null, new[] { PyKey }, false, out _));
        Assert.Equal(ReleaseSig.Result.Invalid, ReleaseSig.Check(PyManifest, null, new[] { PyKey }, true, out _));
        Assert.Equal(ReleaseSig.Result.Invalid, ReleaseSig.Check(PyManifest, "hello", new[] { PyKey }, true, out _));
        Assert.Equal(ReleaseSig.Result.Invalid, ReleaseSig.Check(PyManifest, "elans-release-sig/1\necc196ce5ace1add\n!!!\n", new[] { PyKey }, true, out _));
        Assert.Equal(ReleaseSig.Result.Invalid, ReleaseSig.Check(PyManifest, "elans-release-sig/1\necc196ce5ace1add\n" + Convert.ToBase64String(new byte[64]) + "\n", new[] { PyKey }, true, out _));
        Assert.Equal(ReleaseSig.Result.Invalid, ReleaseSig.Check(PyManifest, PySig, new[] { new ReleaseSig.Key { Id = PyKey.Id, X = "AAAA", Y = "AAAA" } }, true, out _));
    }

    [Fact]
    public void CRLF_line_ends_still_read() =>
        Assert.Equal(ReleaseSig.Result.Valid, ReleaseSig.Check(PyManifest, PySig.Replace("\n", "\r\n"), new[] { PyKey }, true, out _));

    [Fact]
    public void Make_and_Check_agree()
    {
        using var k = Es256.NewKey();
        var p = k.ExportParameters(false);
        var key = new ReleaseSig.Key { Id = Es256.KeyIdOf(k), X = Convert.ToBase64String(p.Q.X), Y = Convert.ToBase64String(p.Q.Y) };
        var man = Encoding.UTF8.GetBytes("{}");
        Assert.Equal(ReleaseSig.Result.Valid, ReleaseSig.Check(man, ReleaseSig.Make(k, key.Id, man), new[] { key }, true, out _));
    }
}
