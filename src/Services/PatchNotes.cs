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
    // Blizzard's own notes for the WoW Forever builds (Settings > About > Game patches, 3.3). The game files carry none (UISplashScreen is empty,
    // GlobalStrings only has old retail splash text), so they come from the forum topics Blizzard's community managers post them in - the list
    // ships in the exe (Topics). Privacy: nothing is fetched until the reader clicks "Load Blizzard's notes" once (Settings.PatchNotesLoad); then
    // one anonymous GET per topic to the public forum's JSON: https only, that one host, no cookies, no redirects, only the Outpost's user agent,
    // nothing about the user in the address. Only posts by Blizzard staff are kept, as plain text (no images, no links followed), and saved to
    // patchnotes.json so the tab shows them offline and the forum is asked at most once an hour.
    public static class PatchNotes
    {
        public const string Host = "us.forums.blizzard.com";
        public sealed class Topic { public int Id; public string Title; public string Page => "https://" + Host + "/en/wow/t/" + Id.ToString(CultureInfo.InvariantCulture); }

        // the topics with notes (add the launch notes here when Blizzard posts them)
        public static readonly Topic[] Topics =
        {
            new Topic { Id = 2360696, Title = "WoW Forever Beta Development Notes" },
        };

        // the forum topic the "Open on the forum" button shows
        public static string ForumPage => Topics[0].Page;

        public sealed class Post
        {
            public int Topic, Number;
            public string Author = "", Title = "";        // the poster's name and forum title ("Community Manager")
            public DateTime Posted, Updated;             // UTC
            public List<Block> Blocks = new List<Block>();
            public string Url => "https://" + Host + "/en/wow/t/" + Topic.ToString(CultureInfo.InvariantCulture) + "/" + Number.ToString(CultureInfo.InvariantCulture);
        }

        public enum BlockKind { Heading, Paragraph, Bullet }
        public sealed class Block { public BlockKind Kind; public int Level; public string Text = ""; }   // Level: the list depth of a bullet (0 = top)

        public sealed class Result
        {
            public List<Post> Posts = new List<Post>();   // newest first
            public DateTime Fetched = DateTime.MinValue;  // UTC; MinValue = never
            public string Problem;                        // the last fetch failed (the saved posts stay)
        }

        public static string CacheFile = Path.Combine(Util.DataDir, "patchnotes.json");
        public static TimeSpan MaxAge = TimeSpan.FromHours(1);
        public const int MaxBytes = 4 << 20;

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

        // only https on the forum's host, a topic or its posts: anything else is refused before a connection is made
        public static bool UrlAllowed(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
            return u.Scheme == Uri.UriSchemeHttps && string.Equals(u.Host, Host, StringComparison.OrdinalIgnoreCase) && u.UserInfo.Length == 0 && u.IsDefaultPort
                && Regex.IsMatch(u.AbsolutePath, @"^/en/wow/t/\d+(/posts)?\.json$") && Regex.IsMatch(u.Query, @"^(\?post_ids%5B%5D=\d+(&post_ids%5B%5D=\d+)*)?$");
        }

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

        public static string TopicUrl(int id) => "https://" + Host + "/en/wow/t/" + id.ToString(CultureInfo.InvariantCulture) + ".json";
        public static string PostsUrl(int id, IEnumerable<int> postIds) =>
            "https://" + Host + "/en/wow/t/" + id.ToString(CultureInfo.InvariantCulture) + "/posts.json?" + string.Join("&", postIds.Select(p => "post_ids%5B%5D=" + p.ToString(CultureInfo.InvariantCulture)));

        // ---------------------------------------------------------------------------------------------------------------------
        // the saved notes and a refresh

        static readonly object gate = new object();
        static Result saved;
        static Task<Result> running;

        public static Result Saved() { lock (gate) return saved ?? (saved = Load()); }
        public static bool Busy { get { lock (gate) return running != null && !running.IsCompleted; } }
        public static bool Stale(Result r) => r.Fetched == DateTime.MinValue || DateTime.UtcNow - r.Fetched > MaxAge;

        // fetch every topic (one at a time) unless the saved notes are fresh (force: the Refresh button); the saved posts stay when the forum can't be reached
        public static Task<Result> Refresh(bool force = false)
        {
            lock (gate)
            {
                if (running != null && !running.IsCompleted) return running;
                var have = saved ?? (saved = Load());
                if (!force && !Stale(have)) return Task.FromResult(have);
                return running = Task.Run(() => Fetch(have));
            }
        }

        static async Task<Result> Fetch(Result have)
        {
            Result res;
            try
            {
                var posts = new List<Post>();
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45)))
                    foreach (var t in Topics) posts.AddRange(await FetchTopic(t.Id, cts.Token));
                res = new Result { Posts = posts.OrderByDescending(p => p.Posted).ToList(), Fetched = DateTime.UtcNow };
                Save(res);
                Util.Log("patch notes: " + res.Posts.Count + " posts by Blizzard from " + Topics.Length + " forum topic(s)");
            }
            catch (Exception e)
            {
                Util.Log("patch notes: the forum could not be read: " + e.Message);
                res = new Result { Posts = have.Posts, Fetched = DateTime.UtcNow, Problem = "Blizzard's forum could not be reached (" + Plain(e) + ")." + (have.Posts.Count > 0 ? " These are the notes saved before." : "") };
            }
            lock (gate) saved = res;
            try { Changed?.Invoke(); } catch { }
            return res;
        }

        static string Plain(Exception e) => e is OperationCanceledException ? "no answer in time" : e is HttpRequestException ? e.Message : e is FormatException ? "an answer it did not understand" : e.GetType().Name;

        static async Task<List<Post>> FetchTopic(int id, CancellationToken ct)
        {
            Interlocked.Increment(ref GetsForTest);
            var topic = Json.Obj(await Get(TopicUrl(id), ct)) ?? throw new FormatException("not a topic");
            var stream = topic.Child("post_stream") ?? throw new FormatException("no posts");
            var posts = stream.List("posts").OfType<Dictionary<string, object>>().ToList();
            // a long topic: the first answer carries 20 posts and the ids of the rest
            var have = new HashSet<int>(posts.Select(p => p.Int("id")));
            var rest = stream.List("stream").OfType<double>().Select(x => (int)x).Where(x => !have.Contains(x)).Take(400).ToList();
            for (int i = 0; i < rest.Count; i += 20)
            {
                Interlocked.Increment(ref GetsForTest);
                var more = Json.Obj(await Get(PostsUrl(id, rest.Skip(i).Take(20)), ct));
                posts.AddRange(more.Child("post_stream").List("posts").OfType<Dictionary<string, object>>());
            }
            return Parse(id, posts);
        }

        // Blizzard's posts of a topic (staff only), their HTML as plain blocks; posts with no text left are dropped
        public static List<Post> Parse(int topic, IEnumerable<Dictionary<string, object>> posts)
        {
            var res = new List<Post>();
            foreach (var p in posts)
            {
                if (!p.Bool("staff")) continue;
                var post = new Post { Topic = topic, Number = p.Int("post_number"), Author = (p.Str("username") ?? "").Split('-')[0], Title = p.Str("user_title") ?? "", Posted = Date(p.Str("created_at")), Updated = Date(p.Str("updated_at")) };
                post.Blocks = Blocks(p.Str("cooked") ?? "");
                if (post.Blocks.Count > 0) res.Add(post);
            }
            return res;
        }

        static DateTime Date(string s) => DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : DateTime.MinValue;

        // ---- forum HTML -> blocks. A small tag walk, no HTML engine: headings, paragraphs and list items become text; images, scripts, styles and
        // quotes of other posts are dropped; links keep their words only; entities are decoded.
        static readonly Regex Tag = new Regex(@"<(/?)([a-zA-Z][a-zA-Z0-9]*)[^>]*?(/?)>|([^<]+)", RegexOptions.Compiled | RegexOptions.Singleline);

        public static List<Block> Blocks(string html)
        {
            var res = new List<Block>();
            var sb = new StringBuilder();
            var kind = BlockKind.Paragraph; int depth = -1, skip = 0;
            void Flush()
            {
                var t = Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
                sb.Clear();
                if (t.Length > 0) res.Add(new Block { Kind = kind, Level = Math.Max(0, depth), Text = t });
            }
            html = Regex.Replace(html ?? "", @"<!--.*?-->", "", RegexOptions.Singleline);
            foreach (Match m in Tag.Matches(html))
            {
                if (m.Groups[4].Success) { if (skip == 0) sb.Append(WebUtility.HtmlDecode(m.Groups[4].Value)); continue; }
                bool close = m.Groups[1].Value == "/", self = m.Groups[3].Value == "/";
                switch (m.Groups[2].Value.ToLowerInvariant())
                {
                    case "script": case "style": case "aside": case "blockquote": case "svg": case "noscript":
                        if (!self) skip = Math.Max(0, skip + (close ? -1 : 1));
                        break;
                    case "h1": case "h2": case "h3": case "h4": case "h5": case "h6":
                        if (skip > 0) break;
                        Flush(); kind = close ? (depth >= 0 ? BlockKind.Bullet : BlockKind.Paragraph) : BlockKind.Heading;
                        break;
                    case "ul": case "ol":
                        if (skip > 0) break;
                        Flush(); depth = Math.Max(-1, depth + (close ? -1 : 1)); kind = depth >= 0 ? BlockKind.Bullet : BlockKind.Paragraph;
                        break;
                    case "li":
                        if (skip > 0) break;
                        Flush(); kind = depth >= 0 ? BlockKind.Bullet : BlockKind.Paragraph;
                        break;
                    case "p": case "div": case "br": case "hr": case "tr":
                        if (skip == 0) Flush();
                        break;
                    case "td": case "th":
                        if (skip == 0 && close) sb.Append("  ");
                        break;
                    // img (emoji, pictures), a (links: their words stay), strong, em ...: no block of their own; no image is ever loaded
                }
            }
            Flush();
            return res;
        }

        // ---- the saved copy
        static Result Load()
        {
            var r = new Result();
            try
            {
                if (!File.Exists(CacheFile)) return r;
                var root = Json.Obj(File.ReadAllText(CacheFile));
                r.Fetched = Date(root.Str("fetched"));
                foreach (var d in root.List("posts").OfType<Dictionary<string, object>>())
                {
                    var p = new Post { Topic = d.Int("topic"), Number = d.Int("number"), Author = d.Str("author") ?? "", Title = d.Str("title") ?? "", Posted = Date(d.Str("posted")), Updated = Date(d.Str("updated")) };
                    foreach (var b in d.List("blocks").OfType<Dictionary<string, object>>())
                        if (Enum.TryParse<BlockKind>(b.Str("kind"), out var k)) p.Blocks.Add(new Block { Kind = k, Level = b.Int("level"), Text = b.Str("text") ?? "" });
                    r.Posts.Add(p);
                }
                r.Posts = r.Posts.OrderByDescending(p => p.Posted).ToList();
            }
            catch (Exception e) { Util.Log("patch notes: " + CacheFile + " could not be read (" + e.Message + ")"); r = new Result(); }
            return r;
        }

        static void Save(Result r)
        {
            string D(DateTime d) => d.ToString("o", CultureInfo.InvariantCulture);
            var posts = r.Posts.Select(p => (object)new Dictionary<string, object>
            {
                ["topic"] = p.Topic, ["number"] = p.Number, ["author"] = p.Author, ["title"] = p.Title, ["posted"] = D(p.Posted), ["updated"] = D(p.Updated),
                ["blocks"] = p.Blocks.Select(b => (object)new Dictionary<string, object> { ["kind"] = b.Kind.ToString(), ["level"] = b.Level, ["text"] = b.Text }).ToList(),
            }).ToList();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CacheFile));
                var tmp = CacheFile + ".tmp";
                File.WriteAllText(tmp, Json.Write(new Dictionary<string, object> { ["version"] = 1, ["fetched"] = D(r.Fetched), ["posts"] = posts }));
                File.Copy(tmp, CacheFile, true); File.Delete(tmp);
            }
            catch (Exception e) { Util.Log("patch notes: saving: " + e.Message); }
        }

        public static void ResetForTest() { lock (gate) { saved = null; running = null; } GetsForTest = 0; }

        // the newest post that was out when a build was first seen ("yyyy-MM-dd", local day; the day after counts, the Outpost may see a build
        // only once the notes are up) - the notes that most likely describe that build. Null = none yet.
        public static Post NewestBy(Result r, string seen)
        {
            if (!DateTime.TryParseExact(seen ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var s)) return r.Posts.FirstOrDefault();
            var until = s.AddDays(2);
            return r.Posts.Where(p => p.Posted.ToLocalTime() < until).OrderByDescending(p => p.Posted).FirstOrDefault();
        }
    }
}
