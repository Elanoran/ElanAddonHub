using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace ElansAddonHub.Services
{
    // Cheap release stats for the addon cards: ONE unauthenticated api.github.com call per
    // refresh (cached on disk for an hour, so restarts and the 30-minute checks don't burn the
    // 60/h rate limit). Every failure is silent - the cards simply show no stats.
    public class AddonStats
    {
        public DateTime? Released;       // when the newest release carrying this addon was published
        public int Downloads;            // total downloads of this addon's zips
        public int Releases;             // number of releases that shipped this addon
    }

    [DataContract] class GhAsset
    {
        [DataMember(Name = "name")] public string Name { get; set; }
        [DataMember(Name = "download_count")] public int Downloads { get; set; }
    }
    [DataContract] class GhRelease
    {
        [DataMember(Name = "published_at")] public string PublishedAt { get; set; }
        [DataMember(Name = "assets")] public List<GhAsset> Assets { get; set; }
    }

    public static class GitHubStats
    {
        public const string Repo = "Elanoran/ElanAddonHub";
        static readonly string CacheFile = Path.Combine(Util.DataDir, "ghcache.json");
        static readonly TimeSpan Ttl = TimeSpan.FromHours(1);
        static string json;
        static DateTime fetched = DateTime.MinValue;

        public static string ReleasesUrl => $"https://github.com/{Repo}/releases";

        // ids double as zip name prefixes, e.g. "ElansHub" matches ElansHub-1.1.0.zip
        public static async Task<Dictionary<string, AddonStats>> Load(IEnumerable<string> addonIds)
        {
            var result = new Dictionary<string, AddonStats>();
            try
            {
                if (json == null)
                    try { var fi = new FileInfo(CacheFile); if (fi.Exists) { json = File.ReadAllText(CacheFile); fetched = fi.LastWriteTimeUtc; } } catch { }
                if (json == null || DateTime.UtcNow - fetched > Ttl)
                {
                    var fresh = await Fetch();
                    if (fresh != null)
                    {
                        json = fresh; fetched = DateTime.UtcNow;
                        try { File.WriteAllText(CacheFile, json); } catch { }
                    }
                    else if (json == null) return result; // nothing cached, rate limited / offline: no stats
                    else fetched = DateTime.UtcNow - Ttl + TimeSpan.FromMinutes(10); // retry in 10 min
                }
                var releases = Util.FromJson<List<GhRelease>>(json) ?? new List<GhRelease>();
                foreach (var id in addonIds)
                {
                    var st = new AddonStats();
                    foreach (var r in releases)
                    {
                        var mine = (r.Assets ?? new List<GhAsset>()).Where(a => a.Name != null && a.Name.StartsWith(id + "-") && a.Name.EndsWith(".zip")).ToList();
                        if (mine.Count == 0) continue;
                        st.Releases++;
                        st.Downloads += mine.Sum(a => a.Downloads);
                        if (DateTime.TryParse(r.PublishedAt, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d)
                            && (st.Released == null || d > st.Released)) st.Released = d;
                    }
                    if (st.Releases > 0) result[id] = st;
                }
            }
            catch (Exception e) { Util.Log("github stats: " + e.Message); }
            return result;
        }

        static async Task<string> Fetch()
        {
            try
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                {
                    var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases?per_page=100");
                    req.Headers.Accept.ParseAdd("application/vnd.github+json");
                    var resp = await Net.Http.SendAsync(req, cts.Token);
                    if (!resp.IsSuccessStatusCode) return null;
                    return await resp.Content.ReadAsStringAsync();
                }
            }
            catch { return null; }
        }

        // compact form for icon chips: 5m, 1h, 3d, 4mo, 2y
        public static string ShortAgo(DateTime utc)
        {
            if (utc <= DateTime.MinValue.AddDays(2)) return "never";
            var s = DateTime.UtcNow - utc;
            if (s.TotalMinutes < 2) return "now";
            if (s.TotalMinutes < 60) return (int)s.TotalMinutes + "m";
            if (s.TotalHours < 24) return (int)s.TotalHours + "h";
            if (s.TotalDays < 60) return (int)s.TotalDays + "d";
            if (s.TotalDays < 365) return (int)(s.TotalDays / 30) + "mo";
            return (int)(s.TotalDays / 365) + "y";
        }

        public static string Ago(DateTime utc)
        {
            var span = DateTime.UtcNow - utc;
            if (span.TotalMinutes < 2) return "just now";
            if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} min ago";
            if (span.TotalHours < 24) return $"{(int)span.TotalHours} h ago";
            if (span.TotalDays < 2) return "yesterday";
            if (span.TotalDays < 60) return $"{(int)span.TotalDays} days ago";
            if (span.TotalDays < 365) return $"{(int)(span.TotalDays / 30)} months ago";
            return $"{(int)(span.TotalDays / 365)} y ago";
        }
    }
}
