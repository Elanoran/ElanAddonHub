using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ElansAddonHub.Services
{
    // Blizzard's news for WoW Forever (Settings > About > Blizzard news, 3.10): what Blizzard's staff post on the forums about Forever - hotfixes,
    // refunds, faction balance, server trouble. The source is the forum's "blizzard-tracker" group (every post by a Blizzard employee, newest
    // first, 20 per answer), kept to the Forever categories (Categories) or a topic whose title names Forever. Outage notices that Blizzard only
    // posts on X don't reach the forums and so don't show here.
    // Privacy as PatchNotes: nothing is fetched until the reader clicks "Load Blizzard's news" once (Settings.BlizzardNewsLoad); then one anonymous
    // GET of that one address on the chosen forum (EU or US): https only, no cookies, no redirects, only the Outpost's user agent, nothing about the
    // user in the address. Each post keeps plain text only (the forum's excerpt; no images, no links followed) and is saved to blizzardnews.json,
    // so the tab shows it offline. The forum is asked at most every MaxAge.
    public static class BlizzardNews
    {
        public static readonly string[] Regions = { "eu", "us" };
        public static string Host(string region) => (region == "us" ? "us" : "eu") + ".forums.blizzard.com";
        public static string FeedUrl(string region) => "https://" + Host(region) + "/en/wow/groups/blizzard-tracker/posts.json";
        public static string ForumPage(string region) => "https://" + Host(region) + "/en/wow/c/" + (region == "us" ? "346" : "359");

        // the Forever categories (and their sub-categories) on each forum, from its categories.json (2026-11-14)
        public static readonly Dictionary<string, HashSet<int>> Categories = new Dictionary<string, HashSet<int>>
        {
            ["eu"] = new HashSet<int> { 359, 360, 361, 362 },        // WoW: Forever, General Discussion, Community Connections, Beta Discussion
            ["us"] = new HashSet<int> { 346, 347, 348, 349, 352 },   // WoW: Forever, General Discussion, Community Connections, Beta Discussion, Bug Report
        };

        public enum Kind { News, Hotfix, Server }

        public sealed class Post
        {
            public long Id;
            public int Topic, Number;
            public string Region = "eu", Author = "", Title = "", TopicTitle = "", Excerpt = "", Path = "";
            public DateTime Posted;                       // UTC
            public Kind Kind => KindOf(TopicTitle, Excerpt);
            public string Url => "https://" + Host(Region) + "/en/wow" + Path;
        }

        public sealed class Result
        {
            public List<Post> Posts = new List<Post>();   // newest first
            public DateTime Fetched = DateTime.MinValue;  // UTC; MinValue = never
            public string Region = "eu";
            public string Problem;                        // the last fetch failed (the saved posts stay)
        }

        public static string CacheFile = System.IO.Path.Combine(Util.DataDir, "blizzardnews.json");
        public static TimeSpan MaxAge = TimeSpan.FromMinutes(30);
        public const int MaxBytes = 2 << 20, Keep = 60;

        // the one network call; the self-test swaps it for a fixture (the test never reaches the internet)
        public static Func<string, CancellationToken, Task<string>> Get = DefaultGet;
        public static int GetsForTest;
        public static event Action Changed;               // a fetch finished (any thread)

        public static readonly HttpClientHandler Handler = new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false, UseDefaultCredentials = false, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate };
        static readonly HttpClient http = MakeClient();

        static HttpClient MakeClient()
        {
            var c = new HttpClient(Handler) { Timeout = TimeSpan.FromSeconds(20) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("ElansAddonHub/" + App.Version);
            c.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            return c;
        }

        // only the tracker feed of the EU or US forum over https, exactly: anything else is refused before a connection is made
        public static bool UrlAllowed(string url) => url == FeedUrl("eu") || url == FeedUrl("us");

        static async Task<string> DefaultGet(string url, CancellationToken ct)
        {
            if (!UrlAllowed(url)) throw new InvalidOperationException("refused address " + url);
            using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                if ((int)resp.StatusCode >= 300) throw new HttpRequestException("the forum answered " + (int)resp.StatusCode);
                if (resp.Content.Headers.ContentLength > MaxBytes) throw new HttpRequestException("the answer is too large");
                using (var s = await resp.Content.ReadAsStreamAsync(ct))
                using (var mem = new MemoryStream())
                {
                    var buf = new byte[1 << 16]; int n;
                    while ((n = await s.ReadAsync(buf, 0, buf.Length, ct)) > 0) { mem.Write(buf, 0, n); if (mem.Length > MaxBytes) throw new HttpRequestException("the answer is too large"); }
                    return Encoding.UTF8.GetString(mem.ToArray());
                }
            }
        }

        // ---------------------------------------------------------------------------------------------------------------------
        // the saved news and a refresh

        static readonly object gate = new object();
        static Result saved;
        static Task<Result> running;

        public static Result Saved() { lock (gate) return saved ?? (saved = Load()); }
        public static bool Busy { get { lock (gate) return running != null && !running.IsCompleted; } }
        public static bool Stale(Result r, string region) => r.Fetched == DateTime.MinValue || r.Region != region || DateTime.UtcNow - r.Fetched > MaxAge;

        // ask the forum unless the saved news is fresh (force: the Refresh button). The saved posts stay when the forum can't be reached; posts
        // of the same forum that dropped out of the feed's 20 are kept (up to Keep), another forum's are replaced.
        public static Task<Result> Refresh(string region, bool force = false)
        {
            region = region == "us" ? "us" : "eu";
            lock (gate)
            {
                if (running != null && !running.IsCompleted) return running;
                var have = saved ?? (saved = Load());
                if (!force && !Stale(have, region)) return Task.FromResult(have);
                return running = Task.Run(() => Fetch(have, region));
            }
        }

        static async Task<Result> Fetch(Result have, string region)
        {
            Result res;
            try
            {
                Interlocked.Increment(ref GetsForTest);
                string json;
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30))) json = await Get(FeedUrl(region), cts.Token);
                var fresh = Parse(region, json);
                var old = have.Region == region ? have.Posts : new List<Post>();
                var ids = new HashSet<long>(fresh.Select(p => p.Id));
                res = new Result { Region = region, Fetched = DateTime.UtcNow, Posts = fresh.Concat(old.Where(p => !ids.Contains(p.Id))).OrderByDescending(p => p.Posted).Take(Keep).ToList() };
                Save(res);
                Util.Log("blizzard news: " + fresh.Count + " Forever posts by Blizzard on the " + region.ToUpperInvariant() + " forum");
            }
            catch (Exception e)
            {
                Util.Log("blizzard news: the forum could not be read: " + e.Message);
                var keep = have.Region == region ? have.Posts : new List<Post>();
                res = new Result { Region = region, Posts = keep, Fetched = DateTime.UtcNow, Problem = "Blizzard's forum could not be reached (" + Plain(e) + ")." + (keep.Count > 0 ? " This is the news saved before." : "") };
            }
            lock (gate) saved = res;
            try { Changed?.Invoke(); } catch { }
            return res;
        }

        static string Plain(Exception e) => e is OperationCanceledException ? "no answer in time" : e is HttpRequestException ? e.Message : e is FormatException ? "an answer it did not understand" : e.GetType().Name;

        // the tracker's posts that are about Forever, as plain text
        public static List<Post> Parse(string region, string json)
        {
            var root = Json.Obj(json) ?? throw new FormatException("not the tracker");
            if (!root.ContainsKey("posts")) throw new FormatException("no posts");
            var cats = Categories.TryGetValue(region, out var c) ? c : new HashSet<int>();
            var res = new List<Post>();
            foreach (var d in root.List("posts").OfType<Dictionary<string, object>>())
            {
                var topicTitle = Clean(d.Str("topic_title") ?? d.Child("topic")?.Str("title") ?? "");
                if (!cats.Contains(d.Int("category_id")) && topicTitle.IndexOf("Forever", StringComparison.OrdinalIgnoreCase) < 0) continue;
                var path = d.Str("url") ?? "";
                if (!Regex.IsMatch(path, @"^/t/[a-z0-9-]*/\d+(/\d+)?$")) path = "/t/" + d.Int("topic_id").ToString(CultureInfo.InvariantCulture) + "/" + d.Int("post_number").ToString(CultureInfo.InvariantCulture);
                var user = d.Child("user");
                var p = new Post
                {
                    Id = d.Long("id"), Topic = d.Int("topic_id"), Number = d.Int("post_number"), Region = region, Path = path, TopicTitle = topicTitle,
                    Author = Clean(d.Str("username") ?? user?.Str("username") ?? "").Split('-')[0], Title = Clean(d.Str("user_title") ?? user?.Str("title") ?? ""),
                    Excerpt = Clean(d.Str("excerpt") ?? ""), Posted = Date(d.Str("created_at")),
                };
                if (p.Id <= 0 || p.TopicTitle.Length == 0) continue;
                if (d.Bool("truncated") && p.Excerpt.Length > 0 && !p.Excerpt.EndsWith("…")) p.Excerpt += "…";
                res.Add(p);
            }
            return res.OrderByDescending(p => p.Posted).ToList();
        }

        // the excerpt is short HTML-ish text: tags out, entities decoded, white space folded, capped
        public static string Clean(string s)
        {
            s = Regex.Replace(s ?? "", @"<(script|style)[^>]*>.*?</\1>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<[^>]*>", " ");
            s = WebUtility.HtmlDecode(s);
            s = Regex.Replace(s, @"\s+", " ").Trim();
            return s.Length > 400 ? s.Substring(0, 399).TrimEnd() + "…" : s;
        }

        // Server: trouble with the servers or logins (louder); Hotfix: a hotfix list; News: the rest
        static readonly Regex ServerWords = new Regex(@"\b(servers?|maintenance|outages?|downtime|disconnect\w*|log ?in|login|restart\w*|realms? (down|offline|unavailable)|queues?|rollback|progression loss|connection)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        public static Kind KindOf(string topicTitle, string excerpt)
        {
            if (ServerWords.IsMatch(topicTitle ?? "")) return Kind.Server;
            if (Regex.IsMatch(topicTitle ?? "", @"\bhotfix", RegexOptions.IgnoreCase)) return Kind.Hotfix;
            return Kind.News;
        }

        static DateTime Date(string s) => DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : DateTime.MinValue;

        // ---- posts not seen yet: newer than the newest one the reader saw (Settings.BlizzardNewsSeen, a post id; 0 = none seen yet)
        public static List<Post> Unseen(Result r, long seenId) => seenId <= 0 ? new List<Post>() : r.Posts.Where(p => p.Id > seenId).ToList();
        public static long Newest(Result r) => r.Posts.Count == 0 ? 0 : r.Posts.Max(p => p.Id);

        // ---- the saved copy
        static Result Load()
        {
            var r = new Result();
            try
            {
                if (!File.Exists(CacheFile)) return r;
                var root = Json.Obj(File.ReadAllText(CacheFile));
                r.Fetched = Date(root.Str("fetched"));
                r.Region = root.Str("region") == "us" ? "us" : "eu";
                foreach (var d in root.List("posts").OfType<Dictionary<string, object>>())
                    r.Posts.Add(new Post
                    {
                        Id = d.Long("id"), Topic = d.Int("topic"), Number = d.Int("number"), Region = r.Region, Author = d.Str("author") ?? "", Title = d.Str("title") ?? "",
                        TopicTitle = d.Str("topic_title") ?? "", Excerpt = d.Str("excerpt") ?? "", Path = Regex.IsMatch(d.Str("path") ?? "", @"^/t/[a-z0-9-]*/\d+(/\d+)?$") ? d.Str("path") : "/t/" + d.Int("topic").ToString(CultureInfo.InvariantCulture),
                        Posted = Date(d.Str("posted")),
                    });
                r.Posts = r.Posts.Where(p => p.Id > 0).OrderByDescending(p => p.Posted).ToList();
            }
            catch (Exception e) { Util.Log("blizzard news: " + CacheFile + " could not be read (" + e.Message + ")"); r = new Result(); }
            return r;
        }

        static void Save(Result r)
        {
            string D(DateTime d) => d.ToString("o", CultureInfo.InvariantCulture);
            var posts = r.Posts.Select(p => (object)new Dictionary<string, object>
            {
                ["id"] = (double)p.Id, ["topic"] = p.Topic, ["number"] = p.Number, ["author"] = p.Author, ["title"] = p.Title, ["topic_title"] = p.TopicTitle,
                ["excerpt"] = p.Excerpt, ["path"] = p.Path, ["posted"] = D(p.Posted),
            }).ToList();
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(CacheFile));
                var tmp = CacheFile + ".tmp";
                File.WriteAllText(tmp, Json.Write(new Dictionary<string, object> { ["version"] = 1, ["region"] = r.Region, ["fetched"] = D(r.Fetched), ["posts"] = posts }));
                File.Copy(tmp, CacheFile, true); File.Delete(tmp);
            }
            catch (Exception e) { Util.Log("blizzard news: saving: " + e.Message); }
        }

        public static void ResetForTest() { lock (gate) { saved = null; running = null; } GetsForTest = 0; }
    }
}
