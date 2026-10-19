using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ElansAddonHub.Services
{
    // What is playing, with its cover (--selftest "radio"; 2.73). Everything comes from the stream itself: the ICY metadata block that names the song
    // (StreamTitle='Artist - Title') also names its cover (StreamUrl='http://img.radioparadise.com/covers/l/<id>.jpg'), so the cover always belongs
    // to the song you hear and nothing else is asked (2.72.2's api.radioparadise.com/now_playing requests are gone). The picture:
    //   - only img.radioparadise.com, only over https (the stream names http://img.radioparadise.com: that one host is upgraded, anything else on
    //     http or another host is refused), only a picture path (.jpg/.jpeg/.png/.webp), no query, no redirects, a small timeout, at most 512 KB;
    //   - the size Radio Paradise publishes next to the stream's (covers/s/ ~1 KB for the 24 px tile, covers/l/ - the stream's own - for the
    //     pop-out, fetched only when the pop-out is open), each once per song, while it plays;
    //   - kept in memory and in cache\radio-covers (the newest 200 files); a picture that failed is not asked again this session;
    //   - anything that fails just means the music note; Settings can turn it off (then no picture is fetched at all).
    public sealed class RadioSong
    {
        public RadioChannel Channel;
        public string StreamTitle;                 // "Artist - Title" from the stream
        public string Artist, Title;               // split at the first " - " (no dash: Title only)
        public string SmallUrl, LargeUrl;          // the allowed https addresses (null: no cover / refused / covers off)
        public ImageSource CoverSmall, CoverLarge;
    }

    public sealed class RadioNowPlaying
    {
        public const string ImageHost = "img.radioparadise.com";
        public const int CacheFiles = 200, TimeoutMs = 6000, MaxImageBytes = 512 * 1024, SmallPx = 48, LargePx = 240;

        // ---- self-test: pictures come from a local fake server (the allow-list is still checked on the real address first); a self-test
        // without that address fetches nothing
        public string TestBaseUrl;
        public int ImageRequests;                                       // requests sent
        public readonly List<string> Requested = new List<string>();     // the real addresses those requests stand for
        public readonly List<string> Refused = new List<string>();       // addresses the allow-list stopped
        public string LastOutcome;                                       // "fetched", "cached", "refused", "failed", "none"
        public string CoverDirOverride;

        readonly SynchronizationContext ui;
        readonly object gate = new object();
        readonly Dictionary<string, ImageSource> images = new Dictionary<string, ImageSource>();
        readonly Queue<string> imageOrder = new Queue<string>();
        readonly HashSet<string> busy = new HashSet<string>(), failed = new HashSet<string>();
        string currentKey;
        bool playing, enabled, largeWanted;

        public event Action Changed;                     // on the UI thread
        public RadioSong Current { get; private set; }

        public RadioNowPlaying() { ui = SynchronizationContext.Current; }

        public string CoverDir => CoverDirOverride ?? Path.Combine(Util.DataDir, "cache", "radio-covers");

        // a cover: https://img.radioparadise.com/<path>.jpg|.jpeg|.png|.webp, nothing else
        public static bool IsAllowedCoverUrl(string url)
        {
            if (!Uri.TryCreate(url ?? "", UriKind.Absolute, out var u)) return false;
            if (u.Scheme != Uri.UriSchemeHttps || !u.IsDefaultPort || u.UserInfo.Length > 0 || u.Query.Length > 0 || u.Fragment.Length > 0) return false;
            if (!string.Equals(u.Host, ImageHost, StringComparison.OrdinalIgnoreCase)) return false;
            var x = Path.GetExtension(u.AbsolutePath).ToLowerInvariant();
            return x == ".jpg" || x == ".jpeg" || x == ".png" || x == ".webp";
        }

        // the stream's StreamUrl as https: http://img.radioparadise.com/... (default port, nothing else changed) becomes https://; any other address
        // is returned as it is (and then refused by IsAllowedCoverUrl)
        public static string Https(string url)
        {
            if (!Uri.TryCreate(url ?? "", UriKind.Absolute, out var u)) return url;
            if (u.Scheme == Uri.UriSchemeHttp && u.IsDefaultPort && string.Equals(u.Host, ImageHost, StringComparison.OrdinalIgnoreCase))
                return "https://" + ImageHost + u.PathAndQuery + u.Fragment;
            return url;
        }

        // the two sizes from the stream's address: covers/<size>/<id>.<ext> -> covers/s/ (the tile) and the stream's own (the pop-out); another path
        // on the host is used as it is for both. False when the address may not be fetched.
        static readonly Regex CoverPath = new Regex(@"^/covers/[a-z]/(\d+\.(?:jpg|jpeg|png|webp))$", RegexOptions.IgnoreCase);
        public static bool CoverUrls(string streamUrl, out string small, out string large)
        {
            small = large = null;
            var url = Https(streamUrl);
            if (!IsAllowedCoverUrl(url)) return false;
            large = url;
            var m = CoverPath.Match(new Uri(url).AbsolutePath);
            small = m.Success ? "https://" + ImageHost + "/covers/s/" + m.Groups[1].Value : url;
            return true;
        }

        public static void SplitTitle(string streamTitle, out string artist, out string title)
        {
            artist = null; title = streamTitle?.Trim();
            if (string.IsNullOrEmpty(title)) return;
            int d = title.IndexOf(" - ", StringComparison.Ordinal);
            if (d <= 0 || d + 3 >= title.Length) return;
            artist = title.Substring(0, d).Trim(); title = title.Substring(d + 3).Trim();
        }

        // ---- called by the window whenever the player changes (and when the setting changes)
        public void Update(RadioChannel channel, string streamTitle, string streamUrl, bool isPlaying, bool isEnabled)
        {
            bool was = enabled;
            playing = isPlaying; enabled = isEnabled;
            string key = channel == null || string.IsNullOrEmpty(streamTitle) ? null : channel.Id + "\n" + streamTitle + "\n" + streamUrl;
            if (key != currentKey || was != enabled)
            {
                currentKey = key;
                Show(key == null ? null : Make(channel, streamTitle, streamUrl));
            }
            Consider();
        }

        // the pop-out is open: its larger cover is wanted (fetched once per song, only then)
        public void WantLarge(bool on) { largeWanted = on; Consider(); }

        RadioSong Make(RadioChannel ch, string streamTitle, string streamUrl)
        {
            SplitTitle(streamTitle, out var artist, out var title);
            var s = new RadioSong { Channel = ch, StreamTitle = streamTitle, Artist = artist, Title = title };
            if (!enabled) return s;
            if (string.IsNullOrEmpty(streamUrl)) { LastOutcome = "none"; return s; }
            if (!CoverUrls(streamUrl, out s.SmallUrl, out s.LargeUrl)) { Refuse(streamUrl); return s; }
            lock (gate)
            {
                images.TryGetValue(SmallPx + "|" + s.SmallUrl, out s.CoverSmall);
                images.TryGetValue(LargePx + "|" + s.LargeUrl, out s.CoverLarge);
            }
            if (s.CoverSmall != null) LastOutcome = "cached";
            return s;
        }

        void Show(RadioSong s) { Current = s; Changed?.Invoke(); }

        void Refuse(string url)
        {
            lock (Refused) { if (!Refused.Contains(url)) Refused.Add(url); while (Refused.Count > 50) Refused.RemoveAt(0); }
            LastOutcome = "refused";
        }

        void Consider()
        {
            var s = Current;
            if (s == null || !playing || !enabled) return;
            if (s.CoverSmall == null && s.SmallUrl != null) Get(s, s.SmallUrl, SmallPx, false);
            if (largeWanted && s.CoverLarge == null && s.LargeUrl != null) Get(s, s.LargeUrl, LargePx, true);
        }

        void Get(RadioSong s, string url, int px, bool large)
        {
            var mkey = px + "|" + url;
            lock (gate) { if (failed.Contains(mkey) || !busy.Add(mkey)) return; }
            var key = currentKey;
            var testBase = TestBaseUrl;
            Task.Run(() => Load(url, px, testBase)).ContinueWith(t =>
            {
                var img = t.Status == TaskStatus.RanToCompletion ? t.Result : null;
                Post(() =>
                {
                    lock (gate) { busy.Remove(mkey); if (img == null) { failed.Add(mkey); if (failed.Count > 500) failed.Clear(); } }
                    var cur = Current;
                    if (img != null && cur != null && key == currentKey && enabled) { if (large) cur.CoverLarge = img; else cur.CoverSmall = img; }
                    Changed?.Invoke();
                });
            });
        }

        void Post(Action a) { if (ui != null) ui.Post(_ => a(), null); else a(); }

        // ---- the picture (a worker thread): memory, then cache\radio-covers, then one GET
        ImageSource Load(string url, int px, string testBase)
        {
            var mkey = px + "|" + url;
            lock (gate) if (images.TryGetValue(mkey, out var img)) { LastOutcome = "cached"; return img; }
            byte[] bytes = null;
            var file = Path.Combine(CoverDir, Hash(url) + ".img");
            try { if (File.Exists(file)) { bytes = File.ReadAllBytes(file); File.SetLastWriteTimeUtc(file, DateTime.UtcNow); LastOutcome = "cached"; } } catch { bytes = null; }
            if (bytes == null)
            {
                bytes = Download(url, testBase);
                if (bytes == null) return null;
                LastOutcome = "fetched";
                try { Directory.CreateDirectory(CoverDir); File.WriteAllBytes(file, bytes); Prune(CoverDir, CacheFiles); } catch { }
            }
            try
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bi.DecodePixelWidth = px;
                bi.StreamSource = new MemoryStream(bytes);
                bi.EndInit();
                bi.Freeze();
                lock (gate)
                {
                    if (!images.ContainsKey(mkey)) { imageOrder.Enqueue(mkey); while (imageOrder.Count > CacheFiles) images.Remove(imageOrder.Dequeue()); }
                    images[mkey] = bi;
                }
                return bi;
            }
            catch { LastOutcome = "failed"; try { File.Delete(file); } catch { } return null; }
        }

        // keeps the newest `keep` files of the cover cache
        public static void Prune(string dir, int keep)
        {
            try
            {
                var old = new DirectoryInfo(dir).GetFiles("*.img").OrderByDescending(f => f.LastWriteTimeUtc).Skip(keep).ToList();
                foreach (var f in old) try { f.Delete(); } catch { }
            }
            catch { }
        }

        static string Hash(string s)
        {
            using (var sha = SHA1.Create()) return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(s)).Select(b => b.ToString("x2")));
        }

        // one GET: the allow-list on the real address, then (in a self-test) the same path on the fake server; no redirects, 200 + image/* only, small
        byte[] Download(string realUrl, string testBase)
        {
            if (!IsAllowedCoverUrl(realUrl)) { Refuse(realUrl); return null; }
            string url = realUrl;
            if (testBase != null) url = testBase.TrimEnd('/') + new Uri(realUrl).PathAndQuery;
            else if (RadioPlayer.SelfTestRun) { Refuse(realUrl); return null; }   // a self-test never reaches Radio Paradise
            if (testBase != null && !(Uri.TryCreate(url, UriKind.Absolute, out var tu) && tu.IsLoopback)) { Refuse(url); return null; }
            Interlocked.Increment(ref ImageRequests);
            lock (Requested) { Requested.Add(realUrl); while (Requested.Count > 200) Requested.RemoveAt(0); }
            try
            {
                // the radio's HttpClient (no redirects, Connection: close); connect + headers, then each read, within TimeoutMs
                using (var cts = new CancellationTokenSource())
                using (var req = RadioPlayer.NewGet(url))
                {
                    req.Headers.TryAddWithoutValidation("Accept", "image/*");
                    using (var resp = RadioPlayer.Send(req, cts, TimeoutMs))
                    {
                        if ((int)resp.StatusCode != 200) { LastOutcome = "failed"; return null; }
                        if (!(RadioPlayer.Header(resp, "Content-Type") ?? "").StartsWith("image/", StringComparison.OrdinalIgnoreCase)) { LastOutcome = "failed"; return null; }
                        if (resp.Content.Headers.ContentLength > MaxImageBytes) { LastOutcome = "failed"; return null; }
                        using (var s = new ReadTimeoutStream(resp.Content.ReadAsStream(cts.Token), cts, TimeoutMs))
                        using (var ms = new MemoryStream())
                        {
                            var buf = new byte[16384];
                            int n;
                            while ((n = s.Read(buf, 0, buf.Length)) > 0)
                            {
                                ms.Write(buf, 0, n);
                                if (ms.Length > MaxImageBytes) { LastOutcome = "failed"; return null; }
                            }
                            return ms.ToArray();
                        }
                    }
                }
            }
            catch (Exception e) { LastOutcome = "failed"; Util.Log("radio cover: " + e.Message); return null; }
        }

        // ---- for the self-test
        public void ForgetForTest() { lock (gate) { images.Clear(); imageOrder.Clear(); failed.Clear(); } }
    }
}
