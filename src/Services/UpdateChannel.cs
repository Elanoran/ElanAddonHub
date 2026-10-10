using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ElansAddonHub.Services
{
    [DataContract] public class RelAsset
    {
        [DataMember(Name = "name")] public string Name { get; set; }
        [DataMember(Name = "browser_download_url")] public string Url { get; set; }
    }

    [DataContract] public class RelInfo
    {
        [DataMember(Name = "tag_name")] public string Tag { get; set; }
        [DataMember(Name = "prerelease")] public bool Prerelease { get; set; }
        [DataMember(Name = "draft")] public bool Draft { get; set; }
        [DataMember(Name = "published_at")] public string Published { get; set; }
        [DataMember(Name = "assets")] public List<RelAsset> Assets { get; set; }
    }

    public class ChannelChoice
    {
        public string Url;
        public bool Pre;          // the chosen release is a pre-release (test build)
        public string Tag;
        public bool FellBack;     // test channel was asked for but the stable manifest is used
    }

    // Stable = releases/latest (GitHub only serves promoted releases there).
    // Test = the newest release including pre-releases, found through the releases API (cached, falls back to stable).
    public static class UpdateChannel
    {
        public const string ApiUrl = "https://api.github.com/repos/Elanoran/ElanAddonHub/releases?per_page=10";
        public static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);

        // replaceable in the self-test
        public static Func<string, Task<string>> Fetch = DefaultFetch;
        static string cacheJson;
        static DateTime cacheAt;

        public static bool IsTest(Settings s) => s != null && string.Equals(s.Channel, "test", StringComparison.OrdinalIgnoreCase);
        public static void Invalidate() { cacheJson = null; }

        static async Task<string> DefaultFetch(string url)
        {
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                req.Headers.Accept.ParseAdd("application/vnd.github+json");
                var resp = await Net.Http.SendAsync(req, cts.Token);
                resp.EnsureSuccessStatusCode();   // 403/429 = rate limited -> caller falls back to stable
                return await resp.Content.ReadAsStringAsync();
            }
        }

        // Pure: newest non-draft release that carries a manifest.json (pre-releases included). Null if nothing usable.
        public static ChannelChoice Pick(string json)
        {
            try
            {
                var list = Util.FromJson<List<RelInfo>>(json);
                if (list == null) return null;
                var best = list.Where(r => !r.Draft && r.Assets != null && r.Assets.Any(a => a.Name == "manifest.json" && !string.IsNullOrEmpty(a.Url)))
                    .OrderByDescending(r => Stamp(r.Published)).FirstOrDefault();
                if (best == null) return null;
                return new ChannelChoice { Url = best.Assets.First(a => a.Name == "manifest.json").Url, Pre = best.Prerelease, Tag = best.Tag };
            }
            catch (Exception e) { Util.Log("channel pick failed: " + e.Message); return null; }
        }

        static DateTime Stamp(string s) => DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : DateTime.MinValue;

        public static ChannelChoice Stable(Settings s) => new ChannelChoice { Url = s.ManifestUrl };

        public static async Task<ChannelChoice> Resolve(Settings s)
        {
            // a custom manifest URL (testing) always wins
            if (!IsTest(s) || s.ManifestUrl != SettingsStore.DefaultManifestUrl) return Stable(s);
            try
            {
                if (cacheJson == null || DateTime.UtcNow - cacheAt > CacheFor)
                {
                    cacheJson = await Fetch(ApiUrl);
                    cacheAt = DateTime.UtcNow;
                }
                var pick = Pick(cacheJson);
                if (pick != null) return pick;
                cacheJson = null;
            }
            catch (Exception e) { Util.Log("releases api failed, using stable: " + e.Message); }
            var st = Stable(s); st.FellBack = true;
            return st;
        }

        // Remember which hub version is a pre-release (for the "Test build" chip); forget it once a stable release reaches it.
        public static bool Track(Settings s, string hubVersion, bool pre)
        {
            if (string.IsNullOrEmpty(hubVersion)) return false;
            var before = s.TestBuild;
            if (pre) { if (s.TestBuild == null || Util.CompareVersions(hubVersion, s.TestBuild) > 0) s.TestBuild = hubVersion; }
            else if (s.TestBuild != null && Util.CompareVersions(hubVersion, s.TestBuild) >= 0) s.TestBuild = null;
            return before != s.TestBuild;
        }

        public static bool RunningTestBuild(Settings s) => s.TestBuild != null && Util.CompareVersions(App.Version, s.TestBuild) == 0;

        // shown under the channel setting when stable is behind what runs here
        public static string StableOlderNote(Settings s, string stableHubVersion, string running) =>
            !IsTest(s) && !string.IsNullOrEmpty(stableHubVersion) && Util.CompareVersions(stableHubVersion, running) < 0
                ? "Stable is older than your test build - you'll get the next stable." : null;

        public static string SelfTest()
        {
            var sb = new StringBuilder();
            void Ck(string what, bool ok, string info = null) => sb.AppendLine($"channel {what}: {(ok ? "ok" : "FAIL")}{(info == null ? "" : " (" + info + ")")}");
            string Rel(string tag, bool pre, string date, bool draft = false, bool manifest = true) =>
                "{\"tag_name\":\"" + tag + "\",\"prerelease\":" + (pre ? "true" : "false") + ",\"draft\":" + (draft ? "true" : "false") + ",\"published_at\":\"" + date + "\",\"assets\":["
                + (manifest ? "{\"name\":\"manifest.json\",\"browser_download_url\":\"https://x/" + tag + "/manifest.json\"}," : "")
                + "{\"name\":\"a.zip\",\"browser_download_url\":\"https://x/" + tag + "/a.zip\"}],\"other\":{\"x\":1}}";
            var json = "[" + Rel("t3", true, "2026-10-12T10:00:00Z") + "," + Rel("t2", false, "2026-10-11T10:00:00Z") + "," + Rel("t1", false, "2026-10-10T10:00:00Z") + "]";
            var p = Pick(json);
            Ck("test picks newest incl. pre-release", p != null && p.Tag == "t3" && p.Pre && p.Url.EndsWith("t3/manifest.json"), p?.Tag);
            json = "[" + Rel("d", true, "2026-10-13T10:00:00Z", draft: true) + "," + Rel("nm", true, "2026-10-12T10:00:00Z", manifest: false) + "," + Rel("ok", false, "2026-10-11T10:00:00Z") + "]";
            p = Pick(json);
            Ck("skips drafts and releases without manifest", p != null && p.Tag == "ok" && !p.Pre, p?.Tag);
            Ck("garbage / empty gives null", Pick("not json") == null && Pick("[]") == null);

            var s = new Settings { ManifestUrl = SettingsStore.DefaultManifestUrl };
            var saved = Fetch; int calls = 0; string reply = "[" + Rel("t3", true, "2026-10-12T10:00:00Z") + "]";
            try
            {
                Fetch = u => { calls++; if (reply == null) throw new HttpRequestException("403 rate limit"); return Task.FromResult(reply); };
                Invalidate();
                var c = Resolve(s).GetAwaiter().GetResult();
                Ck("stable channel never calls the API", c.Url == s.ManifestUrl && !c.Pre && calls == 0);
                s.Channel = "test";
                c = Resolve(s).GetAwaiter().GetResult();
                Ck("test channel uses the pre-release manifest", c.Pre && c.Tag == "t3" && calls == 1, c.Url);
                c = Resolve(s).GetAwaiter().GetResult();
                Ck("API answer is cached", calls == 1);
                Invalidate(); reply = null;
                c = Resolve(s).GetAwaiter().GetResult();
                Ck("rate limit / failure falls back to stable", c.FellBack && c.Url == s.ManifestUrl && !c.Pre);
                Invalidate(); reply = "[]";
                c = Resolve(s).GetAwaiter().GetResult();
                Ck("empty list falls back to stable", c.FellBack && c.Url == s.ManifestUrl);
            }
            finally { Fetch = saved; Invalidate(); }

            var t = new Settings();
            Track(t, "2.25.0", true);
            Ck("track remembers pre-release", t.TestBuild == "2.25.0");
            Track(t, "2.24.0", false);
            Ck("older stable keeps the mark", t.TestBuild == "2.25.0");
            Track(t, "2.25.0", false);
            Ck("promotion clears the mark", t.TestBuild == null);
            Ck("stable-older note", StableOlderNote(new Settings(), "2.24.0", "2.25.0") != null && StableOlderNote(new Settings(), "2.26.0", "2.25.0") == null);
            return sb.ToString().TrimEnd();
        }
    }
}
