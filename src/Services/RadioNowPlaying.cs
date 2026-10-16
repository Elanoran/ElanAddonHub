using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ElansAddonHub.Services
{
    // What is playing, with the cover (--selftest "radio", Outpost 2.72.2). The title always comes from the stream (ICY); this adds the cover, album
    // and year from Radio Paradise's now_playing answer (the one their own site uses: api.radioparadise.com/api/now_playing?chan=N). Used gently:
    //   - one request when the stream's title changes (never per metadata block, never polled), only while it plays, at least 15 s apart (a newer
    //     title waits for the rest of the 15 s; titles that came and went meanwhile are never asked for);
    //   - a song heard before this session needs no request at all (kept in memory, the last 200);
    //   - https only, exactly api.radioparadise.com (that one path) and img.radioparadise.com (covers), no redirects, a small timeout, small answers;
    //   - the API can be a little ahead of or behind the stream: its fields are used only when its artist + title match the stream's title
    //     (letters and digits, ignoring case and accents), otherwise the stream's own text shows, without a cover;
    //   - covers are kept in memory and in cache\radio-covers (the newest 200 files);
    //   - anything that fails just means no cover; Settings can turn it off (then nothing is asked at all).
    public sealed class RadioSong
    {
        public RadioChannel Channel;
        public string StreamTitle;                 // "Artist - Title" from the stream
        public bool Matched;                       // the API's details belong to this song
        public string Artist, Title, Album, Year;  // only when Matched
        public ImageSource CoverSmall, CoverMed;   // only when Matched and the cover could be had
        public string AlbumLine => !Matched ? null : string.Join(" · ", new[] { Album, Year }.Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    public sealed class RadioNowPlaying
    {
        public const string ApiHost = "api.radioparadise.com", ImageHost = "img.radioparadise.com";
        public const int CacheFiles = 200, CacheSongs = 200;
        public const int TimeoutMs = 6000, MaxJsonBytes = 64 * 1024, MaxImageBytes = 1024 * 1024;
        public static readonly TimeSpan DefaultGap = TimeSpan.FromSeconds(15);

        // ---- self-test: the API and the covers go to a local fake server (the allow-list is still checked on the real address first); a self-test
        // without that address asks nobody
        public string TestBaseUrl;
        public TimeSpan Gap = DefaultGap;               // the self-test shrinks it (the rule stays the same)
        public int ApiRequests, ImageRequests;           // requests sent
        public readonly List<string> Refused = new List<string>();   // addresses the allow-list stopped
        public string LastOutcome;                       // "matched", "mismatch", "failed", "cached", "refused", "no-channel"
        public string CoverDirOverride;

        readonly SynchronizationContext ui;
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly object gate = new object();
        readonly Dictionary<string, RadioSong> songs = new Dictionary<string, RadioSong>();
        readonly Queue<string> songOrder = new Queue<string>();
        readonly Dictionary<string, ImageSource> images = new Dictionary<string, ImageSource>();
        readonly Queue<string> imageOrder = new Queue<string>();
        string currentKey;
        bool asked, playing, enabled, timerSet;
        double lastAskMs = double.NegativeInfinity;

        public event Action Changed;                     // on the UI thread
        public RadioSong Current { get; private set; }

        public RadioNowPlaying() { ui = SynchronizationContext.Current; }

        public string CoverDir => CoverDirOverride ?? Path.Combine(Util.DataDir, "cache", "radio-covers");

        public static string ApiUrl(int chan) => "https://" + ApiHost + "/api/now_playing?chan=" + chan.ToString(CultureInfo.InvariantCulture);

        // exactly https://api.radioparadise.com/api/now_playing?chan=<number>
        public static bool IsAllowedApiUrl(string url)
        {
            if (!Uri.TryCreate(url ?? "", UriKind.Absolute, out var u)) return false;
            if (u.Scheme != Uri.UriSchemeHttps || !u.IsDefaultPort || u.UserInfo.Length > 0 || u.Fragment.Length > 0) return false;
            if (!string.Equals(u.Host, ApiHost, StringComparison.OrdinalIgnoreCase) || u.AbsolutePath != "/api/now_playing") return false;
            return System.Text.RegularExpressions.Regex.IsMatch(u.Query, @"^\?chan=\d{1,4}$");
        }

        // a cover: https://img.radioparadise.com/<path>.jpg|.jpeg|.png|.webp, nothing else
        public static bool IsAllowedCoverUrl(string url)
        {
            if (!Uri.TryCreate(url ?? "", UriKind.Absolute, out var u)) return false;
            if (u.Scheme != Uri.UriSchemeHttps || !u.IsDefaultPort || u.UserInfo.Length > 0 || u.Query.Length > 0 || u.Fragment.Length > 0) return false;
            if (!string.Equals(u.Host, ImageHost, StringComparison.OrdinalIgnoreCase)) return false;
            var x = Path.GetExtension(u.AbsolutePath).ToLowerInvariant();
            return x == ".jpg" || x == ".jpeg" || x == ".png" || x == ".webp";
        }

        // letters and digits only, lower case, no accents: "Guns N' Roses - Paradise City" == "Guns N’ Roses" + "Paradise City"
        public static string Norm(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (var ch in s.Normalize(NormalizationForm.FormD))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
                if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            }
            return sb.ToString();
        }

        public static bool Matches(string streamTitle, string artist, string title)
        {
            var a = Norm(artist); var t = Norm(title);
            return t.Length > 0 && Norm(streamTitle) == a + t;
        }

        // ---- called by the window whenever the player changes (and when the setting changes)
        public void Update(RadioChannel channel, string streamTitle, bool isPlaying, bool isEnabled)
        {
            playing = isPlaying; enabled = isEnabled;
            string key = channel == null || string.IsNullOrEmpty(streamTitle) ? null : channel.Id + "\n" + Norm(streamTitle);
            RadioSong hit = null;
            bool cached;
            lock (gate) cached = key != null && songs.TryGetValue(key, out hit);
            if (key != currentKey)
            {
                currentKey = key; asked = false;
                if (key == null) Show(null);
                else if (enabled && cached) { asked = true; LastOutcome = "cached"; Show(hit); }
                else Show(new RadioSong { Channel = channel, StreamTitle = streamTitle });
            }
            else if (key != null && Current != null)
            {
                if (enabled && !Current.Matched && cached) { asked = true; Show(hit); }                                     // switched back on
                else if (!enabled && Current.Matched) Show(new RadioSong { Channel = channel, StreamTitle = streamTitle });   // switched off
            }
            Consider();
        }

        void Show(RadioSong s) { Current = s; Changed?.Invoke(); }

        double Now => clock.Elapsed.TotalMilliseconds;

        // ask now, or when the 15 s since the last request are over (whatever title is current then)
        void Consider()
        {
            var s = Current;
            if (currentKey == null || asked || !playing || !enabled || s == null) return;
            if (s.Channel.ApiChan == null) { asked = true; LastOutcome = "no-channel"; return; }
            double wait = lastAskMs + Gap.TotalMilliseconds - Now;
            if (wait <= 0) { Ask(s); return; }
            if (timerSet) return;
            timerSet = true;
            Task.Delay(TimeSpan.FromMilliseconds(wait + 1)).ContinueWith(_ => Post(() => { timerSet = false; Consider(); }));
        }

        void Ask(RadioSong s)
        {
            asked = true; lastAskMs = Now;
            var key = currentKey;
            var testBase = TestBaseUrl;
            Task.Run(() => Fetch(s.Channel, s.StreamTitle, testBase)).ContinueWith(t =>
            {
                var got = t.Status == TaskStatus.RanToCompletion ? t.Result : null;
                Post(() =>
                {
                    if (got != null)
                    {
                        lock (gate)
                        {
                            if (!songs.ContainsKey(key)) { songOrder.Enqueue(key); while (songOrder.Count > CacheSongs) songs.Remove(songOrder.Dequeue()); }
                            songs[key] = got;
                        }
                    }
                    if (got != null && key == currentKey && enabled) Show(got);
                    else Changed?.Invoke();   // the outcome (the self-test watches it); the view stays as it is
                });
            });
        }

        void Post(Action a) { if (ui != null) ui.Post(_ => a(), null); else a(); }

        // ---- the network part (a worker thread)
        RadioSong Fetch(RadioChannel ch, string streamTitle, string testBase)
        {
            try
            {
                var api = ApiUrl(ch.ApiChan.Value);
                var bytes = Download(api, MaxJsonBytes, false, testBase);
                if (bytes == null) return null;
                var d = Json.Obj(Encoding.UTF8.GetString(bytes));
                if (d == null) { LastOutcome = "failed"; return null; }
                string artist = d.Str("artist"), title = d.Str("title"), album = d.Str("album");
                string year = d.TryGetValue("year", out var y) ? (y is double n ? n.ToString("0", CultureInfo.InvariantCulture) : y as string) : null;
                if (!Matches(streamTitle, artist, title)) { LastOutcome = "mismatch"; return null; }
                var song = new RadioSong { Channel = ch, StreamTitle = streamTitle, Matched = true, Artist = artist.Trim(), Title = title.Trim(), Album = album?.Trim(), Year = year?.Trim() };
                song.CoverSmall = Cover(d.Str("cover_small"), 48, testBase);
                song.CoverMed = Cover(d.Str("cover_med"), 192, testBase);
                LastOutcome = "matched";
                return song;
            }
            catch (Exception e) { LastOutcome = "failed"; Util.Log("radio now playing: " + e.Message); return null; }
        }

        ImageSource Cover(string url, int px, string testBase)
        {
            if (string.IsNullOrEmpty(url)) return null;
            if (!IsAllowedCoverUrl(url)) { lock (Refused) Refused.Add(url); return null; }
            var mkey = px + "|" + url;
            lock (gate) if (images.TryGetValue(mkey, out var img)) return img;
            byte[] bytes = null;
            var file = Path.Combine(CoverDir, Hash(url) + ".img");
            try { if (File.Exists(file)) { bytes = File.ReadAllBytes(file); File.SetLastWriteTimeUtc(file, DateTime.UtcNow); } } catch { bytes = null; }
            if (bytes == null)
            {
                bytes = Download(url, MaxImageBytes, true, testBase);
                if (bytes == null) return null;
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
            catch { try { File.Delete(file); } catch { } return null; }
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

        // one GET: the allow-list on the real address, then (in a self-test) the same path on the fake server; no redirects, 200 only, small
        byte[] Download(string realUrl, int max, bool image, string testBase)
        {
            if (!(image ? IsAllowedCoverUrl(realUrl) : IsAllowedApiUrl(realUrl))) { lock (Refused) Refused.Add(realUrl); LastOutcome = "refused"; return null; }
            string url = realUrl;
            if (testBase != null) url = testBase.TrimEnd('/') + new Uri(realUrl).PathAndQuery;
            else if (RadioPlayer.SelfTestRun) { lock (Refused) Refused.Add(realUrl); LastOutcome = "refused"; return null; }   // a self-test never reaches Radio Paradise
            if (testBase != null && !(Uri.TryCreate(url, UriKind.Absolute, out var tu) && tu.IsLoopback)) { lock (Refused) Refused.Add(url); LastOutcome = "refused"; return null; }
            if (image) Interlocked.Increment(ref ImageRequests); else Interlocked.Increment(ref ApiRequests);
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.UserAgent = RadioParadise.UserAgent;
                req.Accept = image ? "image/*" : "application/json";
                req.AllowAutoRedirect = false;
                req.KeepAlive = false;
                req.Timeout = TimeoutMs; req.ReadWriteTimeout = TimeoutMs;
                req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    if ((int)resp.StatusCode != 200) { LastOutcome = "failed"; return null; }
                    if (image && !(resp.ContentType ?? "").StartsWith("image/", StringComparison.OrdinalIgnoreCase)) { LastOutcome = "failed"; return null; }
                    if (resp.ContentLength > max) { LastOutcome = "failed"; return null; }
                    using (var s = resp.GetResponseStream())
                    using (var ms = new MemoryStream())
                    {
                        var buf = new byte[16384];
                        int n;
                        while ((n = s.Read(buf, 0, buf.Length)) > 0)
                        {
                            ms.Write(buf, 0, n);
                            if (ms.Length > max) { LastOutcome = "failed"; return null; }
                        }
                        return ms.ToArray();
                    }
                }
            }
            catch (Exception e) { LastOutcome = "failed"; Util.Log("radio now playing: " + e.Message); return null; }
        }

        // ---- for the self-test
        public void ForgetForTest(bool songsToo) { lock (gate) { images.Clear(); imageOrder.Clear(); if (songsToo) { songs.Clear(); songOrder.Clear(); } } }
        public void ResetGapForTest() => lastAskMs = double.NegativeInfinity;
    }
}
