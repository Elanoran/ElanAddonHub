using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ElansAddonHub.Services
{
    // 3.8.1: a way around a lost GitHub ("update homes"). GitHub stays the first and normal source, exactly as before. Only when the GitHub check
    // fails (network error, 5xx, rate limit, a missing release file, a refused manifest) are the homes tried, one after the other, never in parallel.
    //   A home is a plain static folder over https:  <home>manifest.json, <home>manifest.json.sig and every file the manifest names, by file name.
    //   Mapping: the manifest keeps its GitHub download addresses (the same signed file for both sources); a manifest read from a home has each
    //   address replaced by <home> + the address's file name (https://github.com/.../releases/download/<tag>/ElansBags-1.2.0.zip -> <home>ElansBags-1.2.0.zip).
    //   Which homes: the "homes" list of the last valid signed manifest (https only, at most MaxHomes), else the one built in below.
    //   Rest: after every home failed, none is tried again for Rest (persisted), so Outposts don't keep knocking on an empty address; logged once per run.
    // Same safety as GitHub: the signature is required and checked against the built-in keys, every download's SHA-256 must match the signed manifest,
    // https only, install paths unchanged. And for both sources: never older - a manifest whose Outpost version is older than the newest one this
    // Outpost already accepted is refused (stable is compared with stable, a pre-release with everything seen, so the test channel still works).
    // Only for the normal update source (settings.manifestUrl = the GitHub default); a custom manifest address (a developer, the self-test) is left alone.
    public static class UpdateHomes
    {
        // the built-in home: nothing is served there until the day it is needed (until then the address may not even resolve)
        public const string BuiltIn = "https://updates.nasferatu.dk/outpost/";
        public const int MaxHomes = 4;
        public static readonly TimeSpan Rest = TimeSpan.FromHours(6);

        // self-test only (ReleaseSignature.SelfTestRun): the fake update server plays GitHub (SelfTestPrimary) and a second fake plays the built-in
        // home (SelfTestBuiltIn). A self-test run never knocks on the real home.
        public static string SelfTestPrimary;
        public static List<string> SelfTestBuiltIn;
        public static Func<DateTime> Now = () => DateTime.UtcNow;

        public static bool Applies(Settings s) => s != null && (s.ManifestUrl == SettingsStore.DefaultManifestUrl
            || (ReleaseSignature.SelfTestRun && SelfTestPrimary != null && s.ManifestUrl == SelfTestPrimary));

        // a home as it is kept: absolute, https (loopback http only in a self-test run), no user, query or fragment, ending in "/"; null if not usable
        public static string Normalize(string url)
        {
            if (string.IsNullOrWhiteSpace(url) || url.Length > 300) return null;
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u)) return null;
            bool https = u.Scheme == Uri.UriSchemeHttps, testHttp = u.Scheme == Uri.UriSchemeHttp && u.IsLoopback && ReleaseSignature.SelfTestRun;
            if (!https && !testHttp) return null;
            if (!string.IsNullOrEmpty(u.UserInfo) || !string.IsNullOrEmpty(u.Query) || !string.IsNullOrEmpty(u.Fragment) || string.IsNullOrEmpty(u.Host)) return null;
            var s = u.GetLeftPart(UriPartial.Path);
            return s.EndsWith("/") ? s : s + "/";
        }

        public static List<string> Clean(IEnumerable<string> homes) =>
            (homes ?? Enumerable.Empty<string>()).Select(Normalize).Where(h => h != null).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxHomes).ToList();

        // the homes to try after GitHub, in order
        public static List<string> List(Settings s)
        {
            var signed = Clean(s?.UpdateHomes);
            if (signed.Count > 0) return signed;
            return ReleaseSignature.SelfTestRun ? Clean(SelfTestBuiltIn) : Clean(new[] { BuiltIn });
        }

        // the homes downloads may come from (Net.UpdateUrlProblem); set at every update check and when the list changes
        static volatile List<string> trusted = new List<string>();
        public static void Use(Settings s) { trusted = Applies(s) ? List(s) : new List<string>(); }

        // the file name a download address ends in: plain names only (letters, digits, . - _ +), never a folder
        public static string FileName(string url)
        {
            if (!Uri.TryCreate(url ?? "", UriKind.Absolute, out var u)) return null;
            var seg = u.AbsolutePath;
            var name = Uri.UnescapeDataString(seg.Substring(seg.LastIndexOf('/') + 1));
            if (name.Length == 0 || name.Length > 120 || name.StartsWith(".") || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '.' || c == '-' || c == '_' || c == '+'))) return null;
            return name;
        }

        // the manifest read from a home: its downloads come from that home too (a name that is not a plain file name gets no address = not installable)
        public static void Rewrite(Manifest m, string home)
        {
            string On(string url) { var f = FileName(url); return f == null ? null : home + f; }
            if (m?.Hub != null && !string.IsNullOrEmpty(m.Hub.Url)) m.Hub.Url = On(m.Hub.Url);
            foreach (var a in m?.Addons ?? Enumerable.Empty<AddonInfo>()) if (!string.IsNullOrEmpty(a.Url)) a.Url = On(a.Url);
        }

        // an address right inside one of the trusted homes (same scheme, host and port; nothing but a plain file name after the home's path)
        public static bool Covers(Uri u)
        {
            if (u == null) return false;
            foreach (var h in trusted)
            {
                if (!Uri.TryCreate(h, UriKind.Absolute, out var hu)) continue;
                if (u.Scheme != hu.Scheme || !string.Equals(u.Host, hu.Host, StringComparison.OrdinalIgnoreCase) || u.Port != hu.Port) continue;
                if (!u.AbsolutePath.StartsWith(hu.AbsolutePath, StringComparison.Ordinal)) continue;
                var rest = u.AbsolutePath.Substring(hu.AbsolutePath.Length);
                if (rest.Length > 0 && !rest.Contains('/') && FileName(u.GetLeftPart(UriPartial.Path)) != null) return true;
            }
            return false;
        }
        public static bool Covers(string url) => Uri.TryCreate(url ?? "", UriKind.Absolute, out var u) && Covers(u);

        // ---- never older
        static string Max(string a, string b) => Util.CompareVersions(a, b) >= 0 ? a : b;

        // why this manifest is refused as a downgrade, or null. Stable is compared with the newest stable seen; a pre-release with everything seen.
        public static string Older(Settings s, Manifest m, bool pre)
        {
            if (!Applies(s)) return null;
            var mark = pre ? Max(s.SeenHub, s.SeenHubAny) : s.SeenHub;
            if (string.IsNullOrEmpty(mark)) return null;
            var v = m?.Hub?.Version;
            if (string.IsNullOrEmpty(v)) return "the update information names no Outpost version (newest seen: " + mark + "), so it was refused";
            return Util.CompareVersions(v, mark) < 0 ? $"the update information is for Outpost {v}, older than {mark} already seen here - an older release is never accepted" : null;
        }

        // an accepted manifest: remember the newest version seen, and (signed only) its list of homes. True when settings changed (save them).
        public static bool Accepted(Settings s, Manifest m, bool pre, bool signed)
        {
            if (!Applies(s) || m == null) return false;
            bool changed = false;
            var v = m.Hub?.Version;
            if (!string.IsNullOrEmpty(v))
            {
                if (Util.CompareVersions(v, s.SeenHubAny) > 0) { s.SeenHubAny = v; changed = true; }
                if (!pre && Util.CompareVersions(v, s.SeenHub) > 0) { s.SeenHub = v; changed = true; }
            }
            if (signed)
            {
                var homes = Clean(m.Homes);
                var keep = homes.Count > 0 ? homes : null;
                if (!(keep ?? new List<string>()).SequenceEqual(s.UpdateHomes ?? new List<string>()))
                {
                    s.UpdateHomes = keep;
                    changed = true;
                    Util.Log("update homes: " + (keep == null ? "the built-in one" : string.Join(", ", keep)) + " (from the signed release information)");
                    Use(s);
                }
            }
            return changed;
        }

        // ---- trying the homes
        public static bool Resting(Settings s) => s != null && s.HomesRestUntil > 0 && Now() < DateTimeOffset.FromUnixTimeSeconds(s.HomesRestUntil).UtcDateTime;

        static readonly HashSet<string> logged = new HashSet<string>();
        static void LogOnce(string key, string msg) { lock (logged) if (!logged.Add(key)) return; Util.Log(msg); }

        // after GitHub failed (why): the first home whose signed manifest reads (read = the same signed check GitHub's goes through), its downloads
        // pointed at that home; null when there is none or all failed (then they rest)
        public static async Task<Manifest> TryHomes(Settings s, Exception why, Func<string, Task<Manifest>> read)
        {
            if (!Applies(s)) return null;
            if (Resting(s)) { LogOnce("rest", "update homes: GitHub failed (" + why?.Message + "); the homes rest until " + DateTimeOffset.FromUnixTimeSeconds(s.HomesRestUntil).ToLocalTime().ToString("yyyy-MM-dd HH:mm")); return null; }
            var homes = List(s);
            if (homes.Count == 0) return null;
            var fails = new List<string>();
            foreach (var home in homes)
            {
                try
                {
                    var m = await read(home + "manifest.json");
                    Rewrite(m, home);
                    if (s.HomesRestUntil != 0) { s.HomesRestUntil = 0; SettingsStore.Save(s); }
                    LogOnce("ok " + home, "update information from " + home + " (GitHub failed: " + why?.Message + ")");
                    return m;
                }
                catch (Exception e) { fails.Add(home + ": " + e.Message); }
            }
            s.HomesRestUntil = new DateTimeOffset(Now()).Add(Rest).ToUnixTimeSeconds();
            SettingsStore.Save(s);
            LogOnce("fail", "update homes: GitHub failed (" + why?.Message + ") and so did " + string.Join("; ", fails) + " - not tried again for " + Rest.TotalHours + " h");
            return null;
        }

        // ---------------------------------------------------------------- self-test ("update" section; the real check is MainWindow.HomesCheckSelfTest)
        public static string SelfTest()
        {
            var sb = new StringBuilder();
            void Ck(string what, bool ok, string info = null) => sb.AppendLine($"homes {what}: {(ok ? "ok" : "FAIL")}{(info == null ? "" : " (" + info + ")")}");
            var wasRun = ReleaseSignature.SelfTestRun; var wasPrimary = SelfTestPrimary; var wasBuiltIn = SelfTestBuiltIn; var wasNow = Now; var wasTrusted = trusted;
            try
            {
                Ck("the built-in home is https://updates.nasferatu.dk/outpost/", BuiltIn == "https://updates.nasferatu.dk/outpost/");
                ReleaseSignature.SelfTestRun = false;
                var gh = new Settings { ManifestUrl = SettingsStore.DefaultManifestUrl };
                Ck("only for the normal GitHub source (a custom manifest address has no homes)", Applies(gh) && !Applies(new Settings { ManifestUrl = "http://127.0.0.1:1/manifest.json" }) && !Applies(null));
                Ck("outside a self-test the list is the built-in home", List(gh).SequenceEqual(new[] { BuiltIn }), string.Join(",", List(gh)));
                Ck("https only: http, loopback http, ftp, user info, a query are dropped",
                    Normalize("http://updates.example.org/x/") == null && Normalize("http://127.0.0.1:5/x/") == null && Normalize("ftp://a.example/") == null
                    && Normalize("https://u:p@a.example/") == null && Normalize("https://a.example/x/?q=1") == null && Normalize("not a url") == null);
                Ck("a home gets its trailing slash", Normalize("https://a.example/outpost") == "https://a.example/outpost/" && Normalize("https://A.example/o/") == "https://a.example/o/");
                var many = Clean(new[] { "https://a.example/", "http://b.example/", "https://a.example/", "https://c.example/", "https://d.example/", "https://e.example/", "https://f.example/" });
                Ck("at most " + MaxHomes + " homes, no doubles, https only", many.Count == MaxHomes && many[0] == "https://a.example/" && many[1] == "https://c.example/" && !many.Contains("http://b.example/"), string.Join(",", many));
                gh.UpdateHomes = new List<string> { "https://new.example/o/" };
                Ck("a signed list replaces the built-in home", List(gh).SequenceEqual(new[] { "https://new.example/o/" }));
                gh.UpdateHomes = new List<string> { "http://bad.example/" };
                Ck("a list without one usable entry = the built-in home", List(gh).SequenceEqual(new[] { BuiltIn }));
                gh.UpdateHomes = null;

                // mapping
                var m = new Manifest { Hub = new HubInfo { Version = "9.9.9", Url = "https://github.com/Elanoran/ElanAddonHub/releases/download/ehh-1-hub-9.9.9/ElansAddonHub.exe" },
                    Addons = new List<AddonInfo> { new AddonInfo { Id = "A", Url = "https://github.com/Elanoran/ElanAddonHub/releases/download/t/ElansBags-1.2.0.zip" },
                                                   new AddonInfo { Id = "B", Url = "https://github.com/x/releases/download/t/..%2Fevil.zip" } } };
                Rewrite(m, BuiltIn);
                Ck("downloads map onto the home by file name", m.Hub.Url == BuiltIn + "ElansAddonHub.exe" && m.Addons[0].Url == BuiltIn + "ElansBags-1.2.0.zip", m.Addons[0].Url);
                Ck("a name that is not a plain file name gets no address", m.Addons[1].Url == null && FileName("https://x/a/") == null && FileName("https://x/.hidden") == null);
                Use(gh);
                Ck("downloads are trusted right inside the home only",
                    Covers(BuiltIn + "ElansBags-1.2.0.zip") && !Covers(BuiltIn + "sub/ElansBags.zip") && !Covers("https://updates.nasferatu.dk/other/x.zip")
                    && !Covers("http://updates.nasferatu.dk/outpost/x.zip") && !Covers("https://updates.nasferatu.dk:8443/outpost/x.zip") && !Covers(BuiltIn));
                var wasDev = Net.AllowDevSources; Net.AllowDevSources = false;
                try
                {
                    Ck("the update rule takes a home download, still refuses other hosts", Net.UpdateUrlProblem(BuiltIn + "ElansAddonHub.exe") == null
                        && Net.UpdateUrlProblem("https://example.org/ElansAddonHub.exe") != null && Net.UpdateUrlProblem("http://updates.nasferatu.dk/outpost/ElansAddonHub.exe") != null);
                    Use(new Settings { ManifestUrl = "http://127.0.0.1:1/manifest.json" });
                    Ck("with a custom manifest address no home is trusted", Net.UpdateUrlProblem(BuiltIn + "ElansAddonHub.exe") != null);
                }
                finally { Net.AllowDevSources = wasDev; }

                // never older
                var s = new Settings { ManifestUrl = SettingsStore.DefaultManifestUrl };
                Manifest V(string v, params string[] homes) => new Manifest { Hub = new HubInfo { Version = v }, Homes = homes.Length == 0 ? null : homes.ToList() };
                Ck("nothing seen yet: anything goes", Older(s, V("1.0.0"), false) == null);
                Accepted(s, V("3.8.1"), false, true);
                Ck("an older stable release is refused", Older(s, V("3.8.0"), false) != null && Older(s, V("3.8.1"), false) == null && Older(s, V("3.9.0"), false) == null, Older(s, V("3.8.0"), false));
                Ck("a release without an Outpost version is refused once one was seen", Older(s, new Manifest(), false) != null);
                Accepted(s, V("3.9.0"), true, true);
                Ck("a pre-release raises the pre mark only", s.SeenHub == "3.8.1" && s.SeenHubAny == "3.9.0");
                Ck("test channel -> stable still works (stable is compared with stable)", Older(s, V("3.8.1"), false) == null);
                Ck("an older pre-release than one seen is refused", Older(s, V("3.8.5"), true) != null && Older(s, V("3.9.0"), true) == null);
                Ck("a custom manifest address keeps no marks and refuses nothing", Older(new Settings { ManifestUrl = "file:///x", SeenHub = "9.0" }, V("1.0"), false) == null
                    && !Accepted(new Settings { ManifestUrl = "file:///x" }, V("1.0"), false, true));
                // homes from the signed manifest
                Ck("signed homes are remembered", Accepted(s, V("3.9.0", "https://a.example/o", "http://b.example/"), false, true) && s.UpdateHomes.SequenceEqual(new[] { "https://a.example/o/" }));
                Ck("homes of an unsigned manifest are ignored", !Accepted(s, V("3.9.0", "https://evil.example/"), false, false) && s.UpdateHomes.SequenceEqual(new[] { "https://a.example/o/" }));
                Ck("a signed manifest without homes = back to the built-in one", Accepted(s, V("3.9.0"), false, true) && s.UpdateHomes == null);

                // rest
                var t0 = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
                Now = () => t0;
                var r = new Settings { ManifestUrl = SettingsStore.DefaultManifestUrl, HomesRestUntil = new DateTimeOffset(t0).Add(Rest).ToUnixTimeSeconds() };
                Ck("resting inside the window", Resting(r));
                Now = () => t0.Add(Rest).AddSeconds(1);
                Ck("tried again after " + Rest.TotalHours + " h", !Resting(r) && !Resting(new Settings()));
            }
            catch (Exception e) { sb.AppendLine("homes test: FAIL " + e); }
            finally { ReleaseSignature.SelfTestRun = wasRun; SelfTestPrimary = wasPrimary; SelfTestBuiltIn = wasBuiltIn; Now = wasNow; trusted = wasTrusted; }
            return sb.ToString().TrimEnd();
        }
    }
}
