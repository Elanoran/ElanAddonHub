using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Elans.Shared;

namespace ElansAddonHub.Services
{
    // 2.99: release signing. Every release's manifest.json comes with manifest.json.sig, made by Tools/release.py (addon repo) with the release key
    // that only the release PC holds. The Outpost checks it against the public keys below before it trusts any checksum in the manifest, so a
    // release someone uploads with a stolen GitHub login or token alone is refused.
    //   manifest.json.sig = "elans-release-sig/1" \n <key id> \n <base64 of the ECDSA P-256 / SHA-256 signature, r||s> \n  (over the manifest's exact bytes)
    // Two keys: the main one, and a backup kept apart from it (a lost main key is replaced by signing a release with the backup).
    // Phase 1 (2.99): a missing signature was still accepted (older releases had none) and logged; a wrong one refused the whole update check.
    // 3.1.1: RequireSignature - every release since 2.99 is signed, so an unsigned manifest is refused too (else leaving the .sig out was enough).
    public static class ReleaseSignature
    {
        public const string Format = ReleaseSig.Format;
        public static bool RequireSignature = true;

        // Self-test only: the fake update server (FakeUpdateServer) signs its manifest with a key it makes in memory for this one run; its private
        // half never leaves the process. TrustRunKey only works once App has marked the process as a self-test run, so a normal start trusts no
        // key but the two below.
        public static bool SelfTestRun;
        static readonly List<Key> runKeys = new List<Key>();
        public static bool TrustRunKey(Key k)
        {
            if (!SelfTestRun || k == null) return false;
            lock (runKeys) runKeys.Add(k);
            return true;
        }
        static IEnumerable<Key> Trusted()
        {
            List<Key> extra;
            lock (runKeys) extra = runKeys.ToList();
#if ELANS_UPDATETEST
            // Tools/updatetest.ps1 only: a build made with -p:ElansUpdateTest=true also trusts the key that script makes for its run (the public
            // half in ELANSHUB_UPDATETEST_KEY = "<id>,<x>,<y>"). release.py never sets the property, so a shipped Outpost has none of this.
            var env = Environment.GetEnvironmentVariable("ELANSHUB_UPDATETEST_KEY")?.Split(',');
            if (env != null && env.Length == 3) extra.Add(new Key { Id = env[0], X = env[1], Y = env[2] });
#endif
            return Keys.Concat(extra);
        }
        public static bool TestBuild =>
#if ELANS_UPDATETEST
            true;
#else
            false;
#endif

        public sealed class Key { public string Id, X, Y; }
        public static readonly List<Key> Keys = new List<Key>
        {
            new Key { Id = "1af1a4f7e60df4e5", X = "coFG/qWzMQR6dUJGuxwuXVvMLCysohtz/US+g5IoVuY=", Y = "qyPkh9qXn1EWWsZCeNo6FNMvyA1pTe97a85tuGEmzbU=" },   // main
            new Key { Id = "8a59df7612818dff", X = "3p6sNfH17/2ONaZxuAr2yxnEuKA+/kOE43K1OB1vkiQ=", Y = "iFT6eh3WtHoNB1wRAWIH6mCwnzpO0Dt4YbjHusbQC/o=" },   // backup
        };

        public enum Result { Valid, Missing, Invalid }

        // the manifest's exact bytes and the .sig file's text (null = there is none); why = plain words for the log and the status line.
        // 3.1.1: the format check itself is Elans.Shared.ReleaseSig (the same code and tests as the Lodge and the Cairn); this keeps the pinned keys.
        public static Result Check(byte[] manifest, string sigText, out string why, IList<Key> keys = null)
        {
            var shared = (keys ?? Trusted()).Select(k => new ReleaseSig.Key { Id = k.Id, X = k.X, Y = k.Y });
            switch (ReleaseSig.Check(manifest, sigText, shared, RequireSignature, out why))
            {
                case ReleaseSig.Result.Valid: return Result.Valid;
                case ReleaseSig.Result.Missing: return Result.Missing;
                default: return Result.Invalid;
            }
        }

        // where the signature of a manifest is: next to it (releases/latest/download/manifest.json -> .../manifest.json.sig, and the same for an asset url)
        public static string SigUrl(string manifestUrl)
        {
            if (string.IsNullOrEmpty(manifestUrl)) return null;
            int q = manifestUrl.IndexOf('?');
            return q < 0 ? manifestUrl + ".sig" : manifestUrl.Substring(0, q) + ".sig" + manifestUrl.Substring(q);
        }

        // ---------------------------------------------------------------- self-test ("update" section)
        public static string SelfTest(string releaseDir = null)
        {
            var sb = new StringBuilder();
            void Ck(string what, bool ok, string info = null) => sb.AppendLine($"releasesig {what}: {(ok ? "ok" : "FAIL")}{(info == null ? "" : " (" + info + ")")}");
            bool savedReq = RequireSignature;
            try
            {
                using (var k = Es256.NewKey())
                {
                    var p = k.ExportParameters(false);
                    var test = new List<Key> { new Key { Id = "0123456789abcdef", X = Convert.ToBase64String(p.Q.X), Y = Convert.ToBase64String(p.Q.Y) } };
                    var man = Encoding.UTF8.GetBytes("{\r\n  \"hub\": {\"version\": \"9.9.9\", \"sha256\": \"" + new string('a', 64) + "\"}\r\n}");
                    string Sig(byte[] data, string kid = "0123456789abcdef") => ReleaseSig.Make(k, kid, data);
                    Ck("a manifest with its signature is valid", Check(man, Sig(man), out var w1, test) == Result.Valid, w1);
                    var tampered = (byte[])man.Clone(); tampered[tampered.Length - 5] ^= 1;
                    Ck("a changed manifest (one byte: another checksum) is refused", Check(tampered, Sig(man), out var w2, test) == Result.Invalid && w2.Contains("does not match"), w2);
                    Ck("a signature with an unknown key id is refused", Check(man, Sig(man, "ffffffffffffffff"), out var w3, test) == Result.Invalid && w3.Contains("unknown key"), w3);
                    Ck("garbage is refused (not a crash)", Check(man, "hello", out _, test) == Result.Invalid && Check(man, Format + "\n0123456789abcdef\n!!!\n", out _, test) == Result.Invalid
                        && Check(man, Format + "\n0123456789abcdef\n" + Convert.ToBase64String(new byte[64]) + "\n", out _, test) == Result.Invalid);
                    Ck("the same signature with CRLF line ends still reads", Check(man, Sig(man).Replace("\n", "\r\n"), out _, test) == Result.Valid);
                    // 3.1.1: a manifest without its .sig is refused (and said so) - leaving the signature out is no way around it
                    Ck("signatures are required in this build", savedReq);
                    Ck("no signature = refused, and said", Check(man, null, out var w4, test) == Result.Invalid && w4.Contains("not signed"), w4);
                    Ck("no signature with the shipped keys = refused too", Check(man, null, out _) == Result.Invalid);
                    RequireSignature = false;
                    Ck("(the old phase 1 rule, kept only as a switch: no signature = accepted, but said)", Check(man, null, out var w6, test) == Result.Missing && w6.Contains("not signed"));
                    RequireSignature = savedReq;
                    // the real keys: a test key's signature is refused by them (no test key is ever trusted by the shipped Outpost)
                    Ck("the shipped keys refuse a signature from any other key", Check(man, Sig(man, Keys[0].Id), out _) == Result.Invalid);
                }
                Ck("the signature sits next to the manifest", SigUrl("https://github.com/Elanoran/ElanAddonHub/releases/latest/download/manifest.json") == "https://github.com/Elanoran/ElanAddonHub/releases/latest/download/manifest.json.sig"
                    && SigUrl("http://127.0.0.1:1234/manifest.json?x=1") == "http://127.0.0.1:1234/manifest.json.sig?x=1");
                Ck("this is not an update-test build (no extra key from the environment)", !TestBuild);
                using (var k2 = Es256.NewKey())
                {
                    var p2 = k2.ExportParameters(false);
                    var rk = new Key { Id = "fedcba9876543210", X = Convert.ToBase64String(p2.Q.X), Y = Convert.ToBase64String(p2.Q.Y) };
                    var m2 = Encoding.UTF8.GetBytes("{\"hub\":{}}");
                    var was = SelfTestRun;
                    SelfTestRun = false;
                    bool refusedOutside = !TrustRunKey(rk) && Check(m2, ReleaseSig.Make(k2, rk.Id, m2), out _) == Result.Invalid;
                    SelfTestRun = was;
                    Ck("a run key is trusted only inside a self-test run", refusedOutside);
                }
                Ck("two keys are built in (main and backup), each a P-256 point", Keys.Count == 2 && Keys.All(x => { using (var ec = Es256.FromPoint(B64.FromStd(x.X), B64.FromStd(x.Y))) return ec != null; }));
                // the release PC: the manifest release.py signed last, checked with the shipped keys (Python's signature read by .NET)
                var dir = releaseDir;
                if (!string.IsNullOrEmpty(dir) && System.IO.File.Exists(System.IO.Path.Combine(dir, "manifest.json.sig")))
                {
                    var r = Check(System.IO.File.ReadAllBytes(System.IO.Path.Combine(dir, "manifest.json")), System.IO.File.ReadAllText(System.IO.Path.Combine(dir, "manifest.json.sig")), out var w5);
                    Ck("the last manifest release.py signed checks with the shipped keys", r == Result.Valid, w5);
                }
            }
            catch (Exception e) { sb.AppendLine("releasesig test: FAIL " + e); }
            finally { RequireSignature = savedReq; }
            return sb.ToString().TrimEnd();
        }
    }
}
