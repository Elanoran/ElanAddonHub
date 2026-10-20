using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ElansAddonHub.Services;

namespace ElansAddonHub.Lodge
{
    // A YouTube link found in a chat message. Pure client side: the id is parsed from the text; the thumbnail and the
    // title come from YouTube only after a click on "Load preview", or by themselves when "Load YouTube previews automatically" is on.
    public sealed class YouTubeLink
    {
        public string Id { get; set; }
        public string Host { get; set; } = "youtube.com";   // the link's domain as shown on the click-to-load card (www. / m. dropped)
        public int StartSeconds { get; set; }           // from t= / start=, 0 = from the beginning
        public string WatchUrl => "https://www.youtube.com/watch?v=" + Id + (StartSeconds > 0 ? "&t=" + StartSeconds + "s" : "");
        public string PlainWatchUrl => "https://www.youtube.com/watch?v=" + Id;   // what oEmbed gets (no time)
    }

    [DataContract]
    class OEmbedDto
    {
        [DataMember(Name = "title")] public string Title { get; set; }
        [DataMember(Name = "author_name")] public string Author { get; set; }
    }

    public sealed class VideoMeta { public string Title, Channel; }

    public static class YouTube
    {
        static readonly Regex UrlRx = new Regex(@"https?://[^\s<>""']+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex IdRx = new Regex(@"^[A-Za-z0-9_-]{11}$", RegexOptions.Compiled);
        static readonly Regex TimeRx = new Regex(@"^(?:(\d+)h)?(?:(\d+)m)?(?:(\d+)s?)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // "Load YouTube previews automatically" (Settings > Privacy): set by the Lodge session. Off (the default) = a link gets a small
        // card with its domain and a "Load preview" button and nothing is fetched until that click; on = every card loads by itself.
        public static Func<bool> AutoLoad = () => false;
        public static bool AutoLoadOn => AutoLoad != null && AutoLoad();

        // the network, replaceable so the selftest runs offline: thumbnail bytes for an id, title / channel for an id (null = not available)
        public static Func<string, Task<byte[]>> ThumbSource = FetchThumb;
        public static Func<string, Task<VideoMeta>> MetaSource = FetchMeta;

        static readonly ConcurrentDictionary<string, Task<ImageSource>> thumbs = new ConcurrentDictionary<string, Task<ImageSource>>();
        static readonly ConcurrentDictionary<string, Task<VideoMeta>> metas = new ConcurrentDictionary<string, Task<VideoMeta>>();

        public static void ClearCaches() { thumbs.Clear(); metas.Clear(); }
        // this id's preview was already asked for in this session (a click on another card of the same video): showing it again costs no request
        public static bool Requested(string id) => thumbs.ContainsKey(id);

        // ---------------------------------------------------------------- parsing

        // the first YouTube link in a chat message, or null
        public static YouTubeLink Find(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            foreach (Match m in UrlRx.Matches(text))
            {
                var l = Parse(m.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}', '>'));
                if (l != null) return l;
            }
            return null;
        }

        // the YouTube link whose text covers this character index of a message (for a click on the URL), or null
        public static YouTubeLink FindAt(string text, int index)
        {
            if (string.IsNullOrEmpty(text) || index < 0) return null;
            foreach (Match m in UrlRx.Matches(text))
            {
                var url = m.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}', '>');
                if (index >= m.Index && index < m.Index + url.Length) return Parse(url);
            }
            return null;
        }

        // one URL: youtube.com/watch?v=, youtu.be/, youtube.com/shorts/ (+ www. and m.), with an optional t= / start=; anything else is null
        public static YouTubeLink Parse(string url)
        {
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u)) return null;
            if (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp) return null;
            if (!string.IsNullOrEmpty(u.UserInfo)) return null;
            var host = u.Host.ToLowerInvariant();
            string id = null;
            var segs = u.AbsolutePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            var query = ParseQuery(u.Query);
            if (host == "youtu.be" || host == "www.youtu.be")
            {
                if (segs.Length >= 1) id = segs[0];
            }
            else if (host == "youtube.com" || host == "www.youtube.com" || host == "m.youtube.com")
            {
                if (segs.Length == 1 && segs[0] == "watch") query.TryGetValue("v", out id);
                else if (segs.Length == 2 && segs[0] == "shorts") id = segs[1];
            }
            if (id == null || !IdRx.IsMatch(id)) return null;
            string t = null;
            if (!query.TryGetValue("t", out t) && !query.TryGetValue("start", out t))
            {
                // a time can also ride in the fragment: ...#t=1m30s
                var frag = ParseQuery(u.Fragment.TrimStart('#'));
                if (!frag.TryGetValue("t", out t)) frag.TryGetValue("start", out t);
            }
            var shown = host.StartsWith("www.") ? host.Substring(4) : host.StartsWith("m.") ? host.Substring(2) : host;
            return new YouTubeLink { Id = id, StartSeconds = ParseTime(t), Host = shown };
        }

        // 90, 90s, 1m30s, 1h2m3s -> seconds (0 when it makes no sense)
        public static int ParseTime(string t)
        {
            if (string.IsNullOrWhiteSpace(t)) return 0;
            var m = TimeRx.Match(t.Trim());
            if (!m.Success || t.Trim().Length == 0) return 0;
            if (!long.TryParse(m.Groups[1].Success ? m.Groups[1].Value : "0", out var h) || !long.TryParse(m.Groups[2].Success ? m.Groups[2].Value : "0", out var mi)
                || !long.TryParse(m.Groups[3].Success ? m.Groups[3].Value : "0", out var s)) return 0;
            if (h > 200 || mi > 100000 || s > 10000000) return 0;
            long total = h * 3600 + mi * 60 + s;
            return total > 24 * 3600 * 7 ? 0 : (int)total;
        }

        static System.Collections.Generic.Dictionary<string, string> ParseQuery(string q)
        {
            var d = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(q)) return d;
            foreach (var part in q.TrimStart('?').Split('&'))
            {
                if (part.Length == 0) continue;
                var i = part.IndexOf('=');
                var k = Uri.UnescapeDataString(i < 0 ? part : part.Substring(0, i));
                var v = i < 0 ? "" : Uri.UnescapeDataString(part.Substring(i + 1).Replace('+', ' '));
                if (!d.ContainsKey(k)) d[k] = v;
            }
            return d;
        }

        // ---------------------------------------------------------------- fetching (cached per id for the session)

        public static Task<ImageSource> Thumb(string id) => thumbs.GetOrAdd(id, i => LoadThumb(i));
        public static Task<VideoMeta> Meta(string id) => metas.GetOrAdd(id, i => LoadMeta(i));

        static async Task<ImageSource> LoadThumb(string id)
        {
            try
            {
                var bytes = await Task.Run(() => ThumbSource(id)).ConfigureAwait(false);
                if (bytes == null || bytes.Length < 100) return null;
                return await Task.Run(() =>
                {
                    using (var ms = new MemoryStream(bytes))
                    {
                        var b = new BitmapImage();
                        b.BeginInit();
                        b.CacheOption = BitmapCacheOption.OnLoad;
                        b.DecodePixelWidth = 720;
                        b.StreamSource = ms;
                        b.EndInit();
                        b.Freeze();
                        return (ImageSource)b;
                    }
                }).ConfigureAwait(false);
            }
            catch (Exception e) { Util.Log("youtube thumb " + id + ": " + e.Message); return null; }
        }

        static async Task<VideoMeta> LoadMeta(string id)
        {
            try { return await Task.Run(() => MetaSource(id)).ConfigureAwait(false); }
            catch (Exception e) { Util.Log("youtube oembed " + id + ": " + e.Message); return null; }
        }

        static async Task<byte[]> FetchThumb(string id)
        {
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12)))
            {
                var resp = await Net.Http.GetAsync("https://i.ytimg.com/vi/" + id + "/hqdefault.jpg", cts.Token).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return null;   // unavailable video: no card
                return await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            }
        }

        static async Task<VideoMeta> FetchMeta(string id)
        {
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12)))
            {
                var url = "https://www.youtube.com/oembed?url=" + Uri.EscapeDataString("https://www.youtube.com/watch?v=" + id) + "&format=json";
                var resp = await Net.Http.GetAsync(url, cts.Token).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return null;
                var dto = Util.FromJson<OEmbedDto>(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
                return string.IsNullOrWhiteSpace(dto?.Title) ? null : new VideoMeta { Title = dto.Title.Trim(), Channel = dto.Author?.Trim() };
            }
        }
    }

    // the preview card under a chat message
    public class VideoVM : Bindable
    {
        public YouTubeLink Link { get; }
        public string Id => Link.Id;
        ImageSource thumb;
        string title, channel;
        bool failed, started;
        public VideoVM(YouTubeLink link) { Link = link; }

        public ImageSource Thumb { get => thumb; private set { if (Set(ref thumb, value)) { Raise(nameof(CardVisibility)); Raise(nameof(PromptVisibility)); } } }
        public string Title { get => title; private set { if (Set(ref title, value)) { Raise(nameof(InfoVisibility)); Raise(nameof(ThumbRadius)); } } }
        public string Channel { get => channel; private set => Set(ref channel, value); }
        public bool Failed => failed;
        public bool Started => started;
        // click to load: before the click only the domain and a "Load preview" button show (nothing fetched); it stays while loading
        // ("Loading..."), and goes once the thumbnail is there or the video turned out unavailable (then the plain link stays)
        public Visibility PromptVisibility => thumb == null && !failed ? Visibility.Visible : Visibility.Collapsed;
        public string Domain => Link.Host;
        public string LoadText => started ? "Loading..." : "Load preview";
        public bool LoadEnabled => !started;
        // the card shows once the thumbnail is there; an unavailable video never gets one (plain link only)
        public Visibility CardVisibility => thumb != null ? Visibility.Visible : Visibility.Collapsed;
        public Visibility InfoVisibility => !string.IsNullOrEmpty(title) ? Visibility.Visible : Visibility.Collapsed;
        public CornerRadius ThumbRadius => string.IsNullOrEmpty(title) ? new CornerRadius(12) : new CornerRadius(12, 12, 0, 0);
        public Visibility ChannelVisibility => string.IsNullOrEmpty(channel) ? Visibility.Collapsed : Visibility.Visible;
        public string ToolTipText => string.IsNullOrEmpty(title) ? "Play the video" : title;

        // thumbnail and title load independently; both come from the per-id caches, off the UI thread
        // (once per card: a second call is ignored)
        public async void Load()
        {
            if (started) return;
            started = true;
            Raise(nameof(Started)); Raise(nameof(LoadText)); Raise(nameof(LoadEnabled));
            var t = YouTube.Thumb(Id);
            var m = YouTube.Meta(Id);
            var img = await t;
            if (img == null) { OnUi(() => { failed = true; Raise(nameof(PromptVisibility)); }); return; }   // unavailable: no card, the plain link stays
            OnUi(() => Thumb = img);
            var meta = await m;
            if (meta != null) OnUi(() => { Title = meta.Title; Channel = meta.Channel; Raise(nameof(ChannelVisibility)); Raise(nameof(ToolTipText)); });
        }

        static void OnUi(Action a)
        {
            var d = Application.Current?.Dispatcher;
            if (d == null || d.CheckAccess()) a(); else d.BeginInvoke(a);
        }
    }
}
