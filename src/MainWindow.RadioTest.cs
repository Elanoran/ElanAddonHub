using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // --selftest "radio" (Outpost 2.72): the Radio Paradise player against a local fake ICY server (127.0.0.1, MP3 frames in a loop, icy-metaint, a
    // StreamTitle that changes). Never the real stream: a self-test player only opens loopback addresses (RadioPlayer.UrlAllowed) and plays into a
    // NullOutput. Covers the published-link allow-list, the User-Agent and Icy-MetaData header, one connection on Play and none at start, Pause closing
    // it, the title, the reconnect waits and Retry, the favourite channel, the ducking curve, the Settings switch, the title bar layout. 2.72.2: the
    // covers and song details against a local fake now_playing + cover server (FakeNowPlayingServer, never api.radioparadise.com): once per song,
    // only while playing, 15 s apart, the host allow-list, a mismatch / failure / redirect shows no cover, the memory and disk caches, the Settings
    // switch; the inline volume slider and mute. Pictures with ELANSHUB_SHOTS=1.
    public partial class MainWindow
    {
        async Task<string> RadioTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            void Check(string what, bool cond, string detail = null) { SelfTestClock.Mark(what); notes.Add((cond ? "ok   " : "FAIL ") + what + (!cond && detail != null ? " [" + detail + "]" : "")); ok &= cond; }
            string P(string n) => Path.Combine(dir, n);
            var hadOff = settings.RadioOff; var hadFav = settings.RadioChannel; var hadDuck = settings.RadioDuck; var hadVol = settings.RadioVolume; var hadNoCovers = settings.RadioNoCovers;
            var hadLink = RadioPlayerBar.OpenLink;
            var w0 = Width; var h0 = Height;
            FakeIcyServer srv = null;
            FakeNowPlayingServer np = null;
            try
            {
                // ============================================================================================ before anything: nothing connected
                Check("start: the player is there, stopped, and nothing was connected (no autoplay)",
                    RadioPlayerBar.IsVisible && Radio.State == RadioState.Stopped && !Radio.WantsPlayback && Radio.ConnectionsOpened == 0 && !DuckTimerRunningForTest,
                    Radio.State + " conns " + Radio.ConnectionsOpened);
                Check("defaults: on, favourite Main Mix, volume 60 %, music at 30 % while people talk",
                    !new Settings().RadioOff && RadioParadise.Channel(new Settings().RadioChannel).Id == "main" && new Settings().RadioVolume == null && new Settings().RadioDuck == null && Math.Abs(radioDucker.Level - (settings.RadioDuck ?? 0.3)) < 0.001);

                // ============================================================================================ the rules from Radio Paradise's pages
                var names = string.Join(",", RadioParadise.Channels.Select(c => c.Name));
                Check("channels: the 8 published channels", names == "Main Mix,Mellow,Rock,Global,Beyond,Radio 2050,KFAT,Serenity", names);
                Check("every channel plays one of its own published links (MP3 192k; Serenity its 64k stream)",
                    RadioParadise.Channels.All(c => c.Published.Contains(c.Path) && RadioParadise.IsAllowedStreamUrl(RadioParadise.StreamUrl(c))) && RadioParadise.Channel("serenity").Path == "serenity" && RadioParadise.Channel("main").Path == "mp3-192");
                Check("every published link is allowed (aac / mp3 / flac of all channels)", RadioParadise.Channels.SelectMany(c => c.Published).All(p => RadioParadise.IsAllowedStreamUrl("https://stream.radioparadise.com/" + p)));
                var bad = new[]
                {
                    "http://stream.radioparadise.com/mp3-192", "https://stream.radioparadise.com:8443/mp3-192", "https://stream.radioparadise.com/mp3-192?x=1",
                    "https://api.radioparadise.com/api/get_block?bitrate=4&info=true", "https://audio.radioparadise.stream/mp3-192", "https://api.radioparadise.com/api/now_playing",
                    "https://stream.radioparadise.com/now_playing", "https://stream.radioparadise.com/radio2050-flacbg", "https://stream.radioparadise.com/serenity-128",
                    "https://stream.radioparadise.com.evil.example/mp3-192", "https://evil.example/stream.radioparadise.com/mp3-192", "https://user@stream.radioparadise.com/mp3-192",
                    "https://radioparadise.com/mp3-192", "", null, "stream.radioparadise.com/mp3-192"
                };
                var let = bad.Where(RadioParadise.IsAllowedStreamUrl).ToList();
                Check("anything else is refused (http, other ports, query, the API / get_block / now_playing, audio.radioparadise.stream, unpublished paths, look-alike hosts)", let.Count == 0, string.Join(" ", let));
                Check("User-Agent: ElansOutpost/<version> (+https://github.com/Elanoran/ElanAddonHub)",
                    System.Text.RegularExpressions.Regex.IsMatch(RadioParadise.UserAgent, @"^ElansOutpost/\d+\.\d+\.\d+ \(\+https://github\.com/Elanoran/ElanAddonHub\)$") && RadioParadise.UserAgent.Contains(App.Version), RadioParadise.UserAgent);
                Check("a self-test player opens only loopback addresses (never the real stream)",
                    !Radio.UrlAllowed("https://stream.radioparadise.com/mp3-192") && Radio.UrlAllowed("http://127.0.0.1:5000/mp3-192") && RadioPlayer.SelfTestRun);

                // ---- covers and song details: the rules (2.72.2)
                {
                    var map = string.Join(",", RadioParadise.Channels.Select(c => c.Id + "=" + (c.ApiChan?.ToString() ?? "-")));
                    Check("covers: API channel numbers as radioparadise.com's own list_chan (Radio 2050 has none)", map == "main=0,mellow=1,rock=2,global=3,beyond=5,radio2050=-,kfat=945,serenity=42", map);
                    Check("covers: the API address is https://api.radioparadise.com/api/now_playing?chan=N", RadioNowPlaying.ApiUrl(1) == "https://api.radioparadise.com/api/now_playing?chan=1" && RadioNowPlaying.IsAllowedApiUrl(RadioNowPlaying.ApiUrl(945)));
                    var badApi = new[]
                    {
                        "http://api.radioparadise.com/api/now_playing?chan=1", "https://api.radioparadise.com:8443/api/now_playing?chan=1", "https://api.radioparadise.com/api/get_block?bitrate=4&info=true",
                        "https://api.radioparadise.com/api/now_playing?chan=1&x=2", "https://api.radioparadise.com/api/now_playing", "https://api.radioparadise.com.evil.example/api/now_playing?chan=1",
                        "https://audio.radioparadise.stream/api/now_playing?chan=1", "https://user@api.radioparadise.com/api/now_playing?chan=1", "https://img.radioparadise.com/api/now_playing?chan=1", "", null
                    };
                    var letApi = badApi.Where(RadioNowPlaying.IsAllowedApiUrl).ToList();
                    Check("covers: any other API address is refused (http, ports, get_block, other paths / hosts, look-alikes)", letApi.Count == 0, string.Join(" ", letApi));
                    var goodImg = new[] { "https://img.radioparadise.com/covers/s/24351.jpg", "https://img.radioparadise.com/covers/m/24351.jpg", "https://img.radioparadise.com/covers/l/1.png" };
                    var badImg = new[]
                    {
                        "http://img.radioparadise.com/covers/s/1.jpg", "https://img.radioparadise.com.evil.example/covers/s/1.jpg", "https://evil.example/img.radioparadise.com/covers/s/1.jpg",
                        "https://img.radioparadise.com:444/covers/s/1.jpg", "https://img.radioparadise.com/covers/s/1.jpg?x=1", "https://img.radioparadise.com/covers/s/1.exe",
                        "https://api.radioparadise.com/covers/s/1.jpg", "https://user@img.radioparadise.com/covers/s/1.jpg", "file:///C:/covers/1.jpg", "", null
                    };
                    var letImg = badImg.Where(RadioNowPlaying.IsAllowedCoverUrl).ToList();
                    Check("covers: only https://img.radioparadise.com/... pictures are fetched", goodImg.All(RadioNowPlaying.IsAllowedCoverUrl) && letImg.Count == 0, string.Join(" ", letImg));
                    Check("covers: the API's artist + title must match the stream's title (case, accents, punctuation ignored)",
                        RadioNowPlaying.Matches("Guns N' Roses - Paradise City", "Guns N’ Roses", "Paradise City") && RadioNowPlaying.Matches("Bjork - Joga", "Björk", "Jóga")
                        && !RadioNowPlaying.Matches("Air - La femme d'argent", "Air", "Sexy Boy") && !RadioNowPlaying.Matches("Air - Sexy Boy", "Moby", "Sexy Boy") && !RadioNowPlaying.Matches("Air - ", "Air", ""));
                    Check("covers: at most one request every 15 s by default", RadioNowPlaying.DefaultGap == TimeSpan.FromSeconds(15) && new RadioNowPlaying().Gap == TimeSpan.FromSeconds(15));
                    Check("covers: on by default", !new Settings().RadioNoCovers);
                    var lone = new RadioNowPlaying();
                    lone.Update(RadioParadise.Channel("main"), "Miles Davis - So What", true, true);
                    Check("covers: a self-test without the fake server asks nobody (api.radioparadise.com is never reached)",
                        await Until(() => lone.LastOutcome == "refused", 3000) && lone.ApiRequests == 0 && lone.Refused.Contains(RadioNowPlaying.ApiUrl(0)), lone.LastOutcome);
                    var r2050 = new RadioNowPlaying();
                    r2050.Update(RadioParadise.Channel("radio2050"), "Radio 2050 - Episode", true, true);
                    await Task.Delay(100);
                    Check("covers: Radio 2050 (no song details from the API) asks nothing", r2050.LastOutcome == "no-channel" && r2050.ApiRequests == 0 && r2050.Refused.Count == 0, r2050.LastOutcome);
                    // the disk cache keeps the newest 200 pictures
                    var pd = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "radio-prune-" + Guid.NewGuid().ToString("N"))).FullName;
                    try
                    {
                        for (int i = 0; i < 205; i++) { var f = Path.Combine(pd, i.ToString("000") + ".img"); File.WriteAllBytes(f, new byte[] { 1 }); File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddMinutes(-1000 + i)); }
                        RadioNowPlaying.Prune(pd, RadioNowPlaying.CacheFiles);
                        var left = Directory.GetFiles(pd).Select(Path.GetFileName).OrderBy(x => x).ToList();
                        Check("covers: the disk cache keeps the newest 200 pictures (the oldest go)", left.Count == 200 && left[0] == "005.img" && RadioNowPlaying.CacheFiles == 200, left.Count + " " + left.FirstOrDefault());
                    }
                    finally { try { Directory.Delete(pd, true); } catch { } }
                }

                // ---- ICY metadata
                Check("title: StreamTitle='Artist - Title'; is read", RadioParadise.ParseStreamTitle("StreamTitle='Air - La femme d''argent';StreamUrl='';") != null
                    && RadioParadise.ParseStreamTitle("StreamTitle='Miles Davis - So What';StreamUrl='';\0\0\0") == "Miles Davis - So What");
                Check("title: a ' or ; inside the title survives", RadioParadise.ParseStreamTitle("StreamTitle='Guns N' Roses - Paradise City; Live';") == "Guns N' Roses - Paradise City; Live",
                    RadioParadise.ParseStreamTitle("StreamTitle='Guns N' Roses - Paradise City; Live';"));
                Check("title: empty / missing is no title", RadioParadise.ParseStreamTitle("StreamTitle='';") == null && RadioParadise.ParseStreamTitle("StreamUrl='x';") == null && RadioParadise.ParseStreamTitle("") == null);
                {
                    var ms = new MemoryStream();
                    void Audio(int n, byte v) { for (int i = 0; i < n; i++) ms.WriteByte(v); }
                    void Meta(string t) { var b = Encoding.UTF8.GetBytes(t); int blocks = (b.Length + 15) / 16; ms.WriteByte((byte)blocks); ms.Write(b, 0, b.Length); for (int i = b.Length; i < blocks * 16; i++) ms.WriteByte(0); }
                    Audio(8, 1); Meta("StreamTitle='A - One';"); Audio(8, 2); ms.WriteByte(0); Audio(8, 3); Meta("StreamTitle='Bjørk - Jóga';"); Audio(5, 4);
                    ms.Position = 0;
                    var titles = new List<string>();
                    var icy = new IcyStream(ms, 8);
                    icy.TitleChanged += t => titles.Add(t);
                    var outb = new byte[64]; int got = icy.Read(outb, 0, 64);
                    var audio = outb.Take(got).ToArray();
                    Check("ICY stream: the metadata blocks are taken out every icy-metaint bytes, the audio passes whole, titles (UTF-8) are raised once each",
                        got == 29 && audio.Take(8).All(x => x == 1) && audio.Skip(8).Take(8).All(x => x == 2) && audio.Skip(16).Take(8).All(x => x == 3) && audio.Skip(24).All(x => x == 4)
                        && string.Join("|", titles) == "A - One|Bjørk - Jóga", got + " " + string.Join("|", titles));
                }

                // ---- backoff
                {
                    var rnd = new Random(7);
                    var exp = new[] { 2, 4, 8, 16, 32, 64, 128, 256, 300, 300 };
                    var waits = Enumerable.Range(1, 10).Select(a => RadioParadise.Backoff(a, rnd).TotalSeconds).ToList();
                    bool inBand = waits.Select((w, i) => w >= exp[i] * 0.8 - 0.001 && w <= Math.Min(300, exp[i] * 1.2) + 0.001).All(b => b);
                    Check("reconnect waits: 2, 4, 8 ... s up to 5 min, each with +-20 % jitter, never over 5 min; 10 tries", inBand && RadioParadise.MaxTries == 10 && waits.Distinct().Count() > 5,
                        string.Join(", ", waits.Select(w => w.ToString("0.0"))));
                }

                // ---- the ducking curve (simulated time)
                {
                    var d = new RadioDucker { Level = 0.3 };
                    double At(bool? speaking, double from, double to) { double f = d.Factor; for (double t = from; t <= to + 0.001; t += 10) f = d.Step(speaking, t); return f; }
                    var idle = At(null, 0, 500);
                    var half = At(true, 510, 635);          // 125 ms into the fade down
                    var down = At(true, 645, 770);          // 250 ms
                    var talking = At(true, 780, 1500);
                    var held = At(false, 1510, 2280);        // < 800 ms after the last speech
                    var up1 = At(false, 2290, 2600);         // hold over at 2300, fading up
                    var up = At(false, 2610, 3000);
                    Check("ducking: someone talks -> the music fades to 30 % in about 250 ms", idle == 1 && half > 0.55 && half < 0.75 && Math.Abs(down - 0.3) < 0.03 && Math.Abs(talking - 0.3) < 0.001,
                        $"idle {idle:0.00} 125ms {half:0.00} 250ms {down:0.00} talking {talking:0.00}");
                    Check("ducking: it holds for 800 ms after the last word, then comes back up over about 600 ms", Math.Abs(held - 0.3) < 0.001 && up1 > 0.55 && up1 < 0.8 && up >= 0.999,
                        $"held {held:0.00} 300ms up {up1:0.00} then {up:0.00}");
                    d.Reset(); d.Level = 0.3;
                    var notInRoom = At(true, 0, 400); var leave = At(null, 410, 1100);
                    Check("ducking: leaving the voice room brings the music back (no hold, nothing ducks outside a room)", notInRoom < 0.31 && leave >= 0.999 && new Func<bool>(() => { var x = new RadioDucker(); for (int t = 0; t < 2000; t += 10) x.Step(null, t); return x.Factor == 1; })());
                    var zero = new RadioDucker { Level = 0 }; for (int t = 0; t <= 400; t += 10) zero.Step(true, t);
                    var none = new RadioDucker { Level = 1 }; for (int t = 0; t <= 400; t += 10) none.Step(true, t);
                    Check("ducking: 0 % is silent, 100 % is no ducking (never silent unless the user sets 0 %)", zero.Factor == 0 && none.Factor == 1);
                }

                // ============================================================================================ the favourite channel
                SettingsPage.SetRadioFavouriteForTest("mellow");
                await Settle();
                Check("favourite: set in Settings, saved, and shown at once while nothing plays", settings.RadioChannel == "mellow" && Radio.Channel.Id == "mellow"
                    && (RadioPlayerBar.ChannelBoxForTest.SelectedItem as RadioChannel)?.Id == "mellow", settings.RadioChannel + " " + Radio.Channel.Id);
                {
                    var fresh = new RadioPlayer();
                    var s2 = new Settings { RadioChannel = "kfat" };
                    fresh.SetChannel(RadioParadise.Channel(s2.RadioChannel).Id);
                    Check("favourite: a new start begins on the favourite (as InitRadio does), stopped", fresh.Channel.Id == "kfat" && fresh.State == RadioState.Stopped && fresh.ConnectionsOpened == 0);
                    Check("favourite: an unknown or missing id falls back to Main Mix", RadioParadise.Channel("nope").Id == "main" && RadioParadise.Channel(null).Id == "main");
                }
                RadioPlayerBar.ChannelBoxForTest.SelectedItem = RadioParadise.Channel("rock");
                await Settle();
                Check("favourite: another channel picked in the player is for this session only (the favourite stays)", Radio.Channel.Id == "rock" && settings.RadioChannel == "mellow"
                    && SettingsPage.RadioFavouriteForTest == "mellow", Radio.Channel.Id + " fav " + settings.RadioChannel);

                // ============================================================================================ play against the fake server
                srv = new FakeIcyServer();
                Radio.TestBaseUrl = "http://127.0.0.1:" + srv.Port + "/";
                Radio.Rnd = new Random(3);
                var fakeSrv = srv;
                np = new FakeNowPlayingServer(() => fakeSrv.LastTitle);
                RadioSongs.TestBaseUrl = "http://127.0.0.1:" + np.Port + "/";
                RadioSongs.Gap = TimeSpan.FromMilliseconds(1500);   // the 15 s rule, faster (the same logic)
                RadioSongs.CoverDirOverride = Path.Combine(dir, "radio-covers");
                RadioSongs.ForgetForTest(true); RadioSongs.ResetGapForTest();
                Check("covers: nothing is asked before Play", np.ApiRequests == 0 && RadioSongs.ApiRequests == 0);
                RadioPlayerBar.PlayButtonForTest.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check("Play: connects, decodes and plays", await Until(() => Radio.State == RadioState.Playing && Radio.DecodedBytes > 0, 8000), Radio.State + " " + Radio.LastError);
                Check("Play: one connection", srv.Connections == 1 && Radio.ConnectionsOpened == 1, srv.Connections + "");
                var req = srv.Requests.FirstOrDefault() ?? "";
                Check("request: GET of the channel's published path", req.StartsWith("GET /rock-192 HTTP/1.1"), req.Split('\n')[0]);
                Check("request: User-Agent ElansOutpost/<version> (+github) and Icy-MetaData: 1",
                    req.IndexOf("\nUser-Agent: " + RadioParadise.UserAgent + "\r", StringComparison.Ordinal) >= 0 && req.IndexOf("\nIcy-MetaData: 1\r", StringComparison.OrdinalIgnoreCase) >= 0, req.Replace("\r\n", " | "));
                Check("request: Connection: close (nothing kept open after Pause)", req.IndexOf("Connection: Close", StringComparison.OrdinalIgnoreCase) >= 0);
                Check("title: read from the stream's metadata and shown in the title bar", await Until(() => Radio.Title != null && Radio.Title.StartsWith("Test Artist - Song ") && RadioPlayerBar.NowTextForTest == Radio.Title, 6000),
                    Radio.Title + " / " + RadioPlayerBar.NowTextForTest);
                var firstTitle = Radio.Title;
                Check("title: follows the stream when the song changes", await Until(() => Radio.Title != null && Radio.Title != firstTitle, 8000), firstTitle + " -> " + Radio.Title);

                // ============================================================================================ covers and song details (fake now_playing)
                {
                    const string pf = "Pink Floyd - Wish You Were Here";
                    // the fake stream at real-time pace on a fresh connection: a title reaches the player within a moment (as on the real stream),
                    // not after the seconds of audio a faster-than-real-time server queues up
                    Radio.Stop(); await Until(() => srv.Open == 0, 4000);
                    srv.RealTime = true; srv.FixedTitle = pf;
                    Radio.Play();
                    await Until(() => Radio.Title == pf, 12000);
                    Check("covers: the playing song gets its cover in the title bar (from the API, matched to the stream's title)",
                        await Until(() => RadioPlayerBar.CoverShownForTest && RadioSongs.Current?.Matched == true && RadioSongs.Current.StreamTitle == pf, 8000),
                        RadioSongs.LastOutcome + " / " + RadioSongs.Current?.StreamTitle);
                    Check("covers: the title still comes from the stream, next to the cover", RadioPlayerBar.NowTextForTest == pf, RadioPlayerBar.NowTextForTest);
                    var pfReq = np.Api.LastOrDefault(r => r.Answered == pf);
                    Check("covers: GET /api/now_playing?chan=2 (Rock) with the stream's User-Agent",
                        pfReq != null && pfReq.Path == "/api/now_playing?chan=2" && pfReq.Head.IndexOf("\nUser-Agent: " + RadioParadise.UserAgent + "\r", StringComparison.Ordinal) >= 0, pfReq?.Path);
                    int apiAt = np.ApiRequests, imgAt = np.ImageRequests, blocksAt = srv.Blocks;
                    await Task.Delay(2500);
                    Check("covers: one request per song, not per metadata block (same title for many blocks: no more requests)",
                        np.ApiRequests == apiAt && np.ImageRequests == imgAt && np.Api.Count(r => r.Answered == pf) == 1 && srv.Blocks - blocksAt >= 5, $"api {apiAt}->{np.ApiRequests} blocks {srv.Blocks - blocksAt}");
                    Check("covers: the small and the larger cover, from img.radioparadise.com paths", np.Images.Count(p => p.StartsWith("/covers/s/")) >= 1 && np.Images.Count(p => p.StartsWith("/covers/m/")) >= 1, string.Join(" ", np.Images));
                    var apiTimes = np.Api.Select(r => r.At).ToList();
                    var gaps = apiTimes.Zip(apiTimes.Skip(1), (a, b) => (b - a).TotalMilliseconds).ToList();
                    Check("covers: requests are never closer than the gap (15 s in the app; 1.5 s here), even while titles change fast", gaps.All(g => g >= 1450),
                        string.Join(", ", gaps.Select(g => g.ToString("0"))));
                    // the flyout: larger cover, title, artist, album and year
                    RadioPlayerBar.FlyoutForTest.IsOpen = true; await Settle();
                    Check("flyout: the larger cover, the title, the artist, album and year",
                        RadioPlayerBar.FlyCoverShownForTest && RadioPlayerBar.FlyTitleForTest == "Wish You Were Here" && RadioPlayerBar.FlyArtistForTest == "Pink Floyd" && RadioPlayerBar.FlyAlbumForTest == "Wish You Were Here · 1975"
                        && RadioPlayerBar.RpLinkTextForTest == "Radio Paradise · Rock",
                        $"'{RadioPlayerBar.FlyTitleForTest}' '{RadioPlayerBar.FlyArtistForTest}' '{RadioPlayerBar.FlyAlbumForTest}' cover {RadioPlayerBar.FlyCoverShownForTest}");
                    RadioPlayerBar.FlyoutForTest.IsOpen = false;
                    RadioPlayerBar.SongButtonForTest.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Check("flyout: a click on the song (cover / title) opens it", RadioPlayerBar.FlyoutForTest.IsOpen);
                    RadioPlayerBar.FlyoutForTest.IsOpen = false;

                    // the next song gets its own details
                    const string md = "Miles Davis - So What";
                    srv.FixedTitle = md; await Until(() => Radio.Title == md, 12000);
                    Check("covers: the next song gets its own cover and details", await Until(() => RadioSongs.Current?.Matched == true && RadioSongs.Current.StreamTitle == md && RadioPlayerBar.CoverShownForTest, 6000)
                        && RadioSongs.Current.Album == "Kind of Blue", RadioSongs.LastOutcome);

                    // a song heard before: no request at all
                    await Task.Delay(1600);
                    int api1 = np.ApiRequests, img1 = np.ImageRequests;
                    srv.FixedTitle = pf; await Until(() => Radio.Title == pf, 8000);
                    Check("cache: a song heard before shows its cover at once", await Until(() => RadioPlayerBar.CoverShownForTest && RadioSongs.Current?.StreamTitle == pf, 1000) && RadioSongs.LastOutcome == "cached", RadioSongs.LastOutcome);
                    await Task.Delay(1800);
                    Check("cache: ... without asking the API or fetching the picture again", np.ApiRequests == api1 && np.ImageRequests == img1, $"api {api1}->{np.ApiRequests} img {img1}->{np.ImageRequests}");

                    // the API is ahead of / behind the stream: no cover, the stream's text
                    np.Mode = "mismatch";
                    const string air = "Air - La Femme d'Argent";
                    int api2 = np.ApiRequests;
                    srv.FixedTitle = air; await Until(() => Radio.Title == air, 8000);
                    Check("mismatch: the API names another song -> no cover, the stream's title", await Until(() => np.ApiRequests > api2 && RadioSongs.LastOutcome == "mismatch", 6000)
                        && !RadioPlayerBar.CoverShownForTest && RadioPlayerBar.NowTextForTest == air && RadioSongs.Current?.Matched == false, RadioSongs.LastOutcome);
                    RadioPlayerBar.FlyoutForTest.IsOpen = true; await Settle();
                    Check("mismatch: the flyout shows the stream's text, no album, the music note instead of a cover",
                        RadioPlayerBar.FlyTitleForTest == air && RadioPlayerBar.FlyArtistForTest == "" && RadioPlayerBar.FlyAlbumForTest == "" && !RadioPlayerBar.FlyCoverShownForTest);
                    if (SelfTestClock.ShotsOn) { await ShotWait(400); SnapshotElement(RadioPlayerBar.FlyCardForTest, P("radio-flyout-nocover.png")); }
                    RadioPlayerBar.FlyoutForTest.IsOpen = false;

                    // failures: an error, a redirect, covers on other hosts
                    np.Mode = "fail";
                    const string bon = "Bonobo - Kerala";
                    int api3 = np.ApiRequests;
                    srv.FixedTitle = bon; await Until(() => Radio.Title == bon, 8000);
                    Check("failure: the API answers 503 -> quietly no cover (the music note)", await Until(() => np.ApiRequests > api3 && RadioSongs.LastOutcome == "failed", 6000)
                        && !RadioPlayerBar.CoverShownForTest && RadioPlayerBar.NowTextForTest == bon && Radio.State == RadioState.Playing, RadioSongs.LastOutcome);
                    np.Mode = "redirect";
                    const string ka = "Khruangbin - Maria También";
                    int api4 = np.ApiRequests;
                    srv.FixedTitle = ka; await Until(() => Radio.Title == ka, 8000);
                    Check("failure: a redirect is never followed -> no cover", await Until(() => np.ApiRequests > api4 && RadioSongs.LastOutcome == "failed", 6000)
                        && !RadioPlayerBar.CoverShownForTest && !np.Paths.Any(p => p.StartsWith("/elsewhere")), RadioSongs.LastOutcome + " " + string.Join(" ", np.Paths.Skip(Math.Max(0, np.Paths.Count - 3))));
                    np.Mode = "evilcover";
                    const string tt = "Tame Impala - The Less I Know the Better";
                    int api5 = np.ApiRequests, img5 = np.ImageRequests, ref5 = RadioSongs.Refused.Count;
                    srv.FixedTitle = tt; await Until(() => Radio.Title == tt, 8000);
                    Check("allow-list: covers on other hosts / plain http are not fetched (details yes, picture no)",
                        await Until(() => np.ApiRequests > api5 && RadioSongs.Current?.Matched == true && RadioSongs.Current.StreamTitle == tt, 6000)
                        && np.ImageRequests == img5 && RadioSongs.Refused.Count - ref5 == 2 && !RadioPlayerBar.CoverShownForTest,
                        $"img {img5}->{np.ImageRequests} refused {string.Join(" ", RadioSongs.Refused.Skip(ref5))}");
                    np.Mode = "match";

                    // the disk cache: a new session (memory forgotten) asks the API again but loads the picture from cache\radio-covers
                    RadioSongs.ForgetForTest(true);
                    int api6 = np.ApiRequests, img6 = np.ImageRequests;
                    await Task.Delay(1600);
                    srv.FixedTitle = pf; await Until(() => Radio.Title == pf, 8000);
                    Check("disk cache: the cover comes from cache\\radio-covers (one API request, no picture request)",
                        await Until(() => RadioPlayerBar.CoverShownForTest && RadioSongs.Current?.StreamTitle == pf, 6000) && np.ApiRequests == api6 + 1 && np.ImageRequests == img6
                        && Directory.GetFiles(RadioSongs.CoverDir, "*.img").Length >= 2, $"api {api6}->{np.ApiRequests} img {img6}->{np.ImageRequests}");
                    // the connection counts below are about the play / pause checks: this block's own reconnect does not count
                    srv.RealTime = false;
                    srv.Connections = 1;
                    lock (srv.Requests) if (srv.Requests.Count > 1) srv.Requests.RemoveRange(1, srv.Requests.Count - 1);
                }
                Check("play button: shows Pause while playing", (string)RadioPlayerBar.PlayButtonForTest.Content == "" && (string)RadioPlayerBar.PlayButtonForTest.ToolTip == "Pause");
                Check("ducking runs only while it plays", DuckTimerRunningForTest);
                Check("the self-test plays into a NullOutput, never a sound device", RadioPlayer.SelfTestRun);
                // pictures: playing with a title, the flyout
                if (SelfTestClock.ShotsOn)
                {
                    srv.FixedTitle = "Pink Floyd - Wish You Were Here";
                    await Until(() => Radio.Title == srv.FixedTitle, 8000);
                    await Until(() => RadioPlayerBar.CoverShownForTest, 6000);
                    Width = 1280; Height = 760; await ShotWait(600);
                    Snapshot(P("radio-playing.png"));
                    RadioPlayerBar.FlyoutForTest.IsOpen = true; await ShotWait(500);
                    SnapshotElement(RadioPlayerBar.FlyCardForTest, P("radio-flyout.png"));
                    RadioPlayerBar.FlyoutForTest.IsOpen = false;
                    // with the game data chip in the title bar too
                    GameDataProgress.Begin("radiotest", "Spells", "Reading spells");
                    GameDataProgress.Rebuilding("radiotest", GameDataProgress.Why.WowUpdated, "Reading spells");
                    GameDataProgress.Report("radiotest", 40, 100);
                    await Until(() => GameDataBar.ShownForTest, 3000); await ShotWait(500);
                    Snapshot(P("radio-and-progress.png"));
                    Width = 760; await ShotWait(600);
                    Snapshot(P("radio-narrow.png"));
                    RadioPlayerBar.FlyoutForTest.IsOpen = true; await ShotWait(500);
                    SnapshotElement(RadioPlayerBar.FlyCardForTest, P("radio-flyout-narrow.png"));
                    RadioPlayerBar.FlyoutForTest.IsOpen = false;
                    Width = 1280; await ShotWait(300);
                    GameDataProgress.End("radiotest"); GameDataProgress.ResetForTest();
                    srv.FixedTitle = null;
                }
                // the flyout's links: the attribution and Support Radio Paradise (the Outpost browser in the app; captured here, nothing is opened)
                var opened = new List<string>();
                RadioPlayerBar.OpenLink = u => opened.Add(u);
                RadioPlayerBar.ClickRpLinkForTest(); RadioPlayerBar.ClickSupportForTest();
                Check("flyout: 'Radio Paradise · <channel>' links to radioparadise.com, 'Support Radio Paradise' to /donate",
                    string.Join(" ", opened) == "https://radioparadise.com https://radioparadise.com/donate" && RadioPlayerBar.RpLinkTextForTest == "Radio Paradise · Rock" && hadLink != null, string.Join(" ", opened));
                RadioPlayerBar.OpenLink = hadLink;
                Width = Math.Max(Width, 1280); await Settle();
                await Until(() => RadioPlayerBar.Mode != 2, 2000);
                Check("volume: a slider right in the title bar (no button, no flyout needed)", RadioPlayerBar.InlineVolumeShownForTest && !RadioPlayerBar.FlyVolumeShownForTest
                    && RadioPlayerBar.VolumeSliderForTest.ActualWidth >= 72 && RadioPlayerBar.VolumeSliderForTest.ActualWidth <= 88, "mode " + RadioPlayerBar.Mode + " w " + RadioPlayerBar.VolumeSliderForTest.ActualWidth);
                RadioPlayerBar.VolumeSliderForTest.Value = 0.5;
                Check("volume: the slider sets the gain (squared curve) and is remembered", Math.Abs(Radio.Volume - 0.5) < 0.001 && Math.Abs(Radio.Gain - 0.25) < 0.001 && settings.RadioVolume == 0.5
                    && Math.Abs(RadioPlayerBar.FlyVolumeSliderForTest.Value - 0.5) < 0.001);
                RadioPlayerBar.MuteButtonForTest.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check("volume: the speaker mutes (gain 0, the volume is kept)", Radio.Muted && Radio.Gain == 0 && Math.Abs(Radio.Volume - 0.5) < 0.001 && (string)RadioPlayerBar.MuteButtonForTest.ToolTip == "Unmute");
                RadioPlayerBar.MuteButtonForTest.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check("volume: and again unmutes", !Radio.Muted && Math.Abs(Radio.Gain - 0.25) < 0.001 && (string)RadioPlayerBar.MuteButtonForTest.ToolTip == "Mute");
                RadioPlayerBar.MuteButtonForTest.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                RadioPlayerBar.FlyVolumeSliderForTest.Value = 0.7; RadioPlayerBar.VolumeSliderForTest.Value = 0.5;
                Check("volume: moving a slider unmutes; the flyout's slider (narrow window) is the same volume", !Radio.Muted && Math.Abs(Radio.Volume - 0.5) < 0.001 && Math.Abs(RadioPlayerBar.FlyVolumeSliderForTest.Value - 0.5) < 0.001);

                // ---- ducking with the player (simulated speaking state of the voice room)
                bool? talk = false;
                RadioSpeechForTest = () => talk;
                talk = true;
                Check("voice room: someone talks -> the radio's gain goes to 30 % of the volume", await Until(() => Math.Abs(Radio.Duck - 0.3) < 0.01 && Math.Abs(Radio.Gain - 0.25 * 0.3) < 0.005, 3000), Radio.Duck.ToString("0.00"));
                talk = false;
                var quiet = DateTime.UtcNow;
                await Task.Delay(500);
                Check("voice room: still low right after they stop (hold)", Radio.Duck < 0.35, Radio.Duck.ToString("0.00"));
                Check("voice room: back to the full volume after the hold and the fade", await Until(() => Radio.Duck >= 0.999, 4000) && (DateTime.UtcNow - quiet).TotalMilliseconds >= 1100, (DateTime.UtcNow - quiet).TotalMilliseconds.ToString("0") + " ms");
                talk = null;
                RadioSpeechForTest = null;
                Check("not in a voice room: the real source says so (no ducking)", Session.MyRoom != null || RoomSpeech() == null);

                // ---- Pause closes the connection
                RadioPlayerBar.PlayButtonForTest.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check("Pause: the HTTP connection is closed (the server sees it go)", await Until(() => srv.Open == 0, 4000) && Radio.State == RadioState.Stopped && !Radio.WantsPlayback, "open " + srv.Open);
                await Task.Delay(700);
                Check("Pause: no reconnect, no muted streaming in the background", srv.Connections == 1 && srv.Open == 0 && !DuckTimerRunningForTest, srv.Connections + "");

                // ---- covers: nothing is asked while paused / stopped, and a request still waiting for the gap is dropped when the music stops
                {
                    int a0 = np.ApiRequests;
                    RadioSongs.Update(Radio.Channel, "Paused Artist - Paused Song", false, true);
                    await Task.Delay(1800);
                    Check("covers: a new title while paused / stopped asks nothing", np.ApiRequests == a0, (np.ApiRequests - a0) + " requests");
                    RadioSongs.ResetGapForTest();
                    RadioSongs.Update(Radio.Channel, "Queued Artist - First", true, true);       // asked at once
                    RadioSongs.Update(Radio.Channel, "Queued Artist - Second", true, true);      // waits for the gap ...
                    RadioSongs.Update(Radio.Channel, "Queued Artist - Second", false, true);     // ... and the music stops meanwhile
                    await Task.Delay(2200);
                    Check("covers: a waiting request is dropped when the music stops (only the first was asked)", np.ApiRequests == a0 + 1, (np.ApiRequests - a0) + " requests");
                    // three songs in quick succession while playing: the first is asked at once, the third after the rest of the gap, the second never
                    RadioSongs.ResetGapForTest();
                    int r0 = np.ApiRequests; DateTime t0 = DateTime.UtcNow;
                    RadioSongs.Update(Radio.Channel, "Miles Davis - Freddie Freeloader", true, true);
                    await Task.Delay(300);
                    RadioSongs.Update(Radio.Channel, "Nina Simone - Sinnerman", true, true);
                    await Task.Delay(300);
                    RadioSongs.Update(Radio.Channel, "Nina Simone - Feeling Good", true, true);
                    await Until(() => np.ApiRequests >= r0 + 2, 4000);
                    await Task.Delay(1800);
                    List<FakeNowPlayingServer.Req> rs; lock (np.Api) rs = np.Api.Where(r => r.At >= t0.AddMilliseconds(-50)).ToList();
                    double apart = rs.Count == 2 ? (rs[1].At - rs[0].At).TotalMilliseconds : -1;
                    Check("rate limit: songs changing fast -> the first at once, the latest after the gap (15 s in the app, 1.5 s here), the one between never",
                        np.ApiRequests == r0 + 2 && apart >= 1450 && apart < 2500 && (rs[0].At - t0).TotalMilliseconds < 800, $"{np.ApiRequests - r0} requests, {apart:0} ms apart");
                    RadioSongs.Update(Radio.Channel, "Paused Artist - Paused Song", false, true);
                    RadioSongs.Update(Radio.Channel, Radio.Title, false, true);
                }

                // ---- channel change while playing: the old connection goes before the new one opens
                Radio.Play();
                await Until(() => Radio.State == RadioState.Playing, 8000);
                RadioPlayerBar.ChannelBoxForTest.SelectedItem = RadioParadise.Channel("global");
                Check("channel change while playing: a new connection to the new channel", await Until(() => Radio.State == RadioState.Playing && srv.Requests.Count == 3 && srv.Requests[2].StartsWith("GET /global-192 "), 8000), srv.Requests.Count + "");
                Check("channel change: never two connections at once", srv.PeakOpen == 1 && srv.Open == 1, "peak " + srv.PeakOpen);
                Radio.Stop();
                await Until(() => srv.Open == 0, 4000);

                // ---- drops and reconnects
                Radio.BackoffScale = 0.002;          // 2 s -> 4 ms ... 300 s -> 0.6 s: the same sequence, fast
                srv.Connections = 0; srv.Requests.Clear();
                srv.DropAfterBytes = 30000;
                Radio.Play();
                Check("server drops the stream: the player reconnects by itself while you want to listen", await Until(() => srv.Connections >= 3, 10000) && Radio.WantsPlayback, srv.Connections + " " + Radio.State);
                Radio.Stop();
                await Until(() => srv.Open == 0, 4000);
                srv.DropAfterBytes = 0;
                srv.Refuse = true;
                srv.Connections = 0;
                Radio.Play();
                Check("server down: 10 tries with the growing waits, then it gives up", await Until(() => Radio.State == RadioState.GaveUp, 15000) && srv.Connections == 11 && Radio.ConnectionsOpened > 0,
                    Radio.State + " conns " + srv.Connections);
                var exp2 = new[] { 2, 4, 8, 16, 32, 64, 128, 256, 300, 300 };
                List<TimeSpan> ws; lock (Radio.Waits) ws = Radio.Waits.ToList();
                Check("server down: the waits were 2, 4, 8 ... 300 s (+-20 %)", ws.Count == 10 && ws.Select((w, i) => w.TotalSeconds >= exp2[i] * 0.8 - 0.001 && w.TotalSeconds <= Math.Min(300, exp2[i] * 1.2) + 0.001).All(b => b),
                    string.Join(", ", ws.Select(w => w.TotalSeconds.ToString("0.0"))));
                await Settle();
                Check("gave up: the button and the flyout offer Retry, nothing else connects", RadioPlayerBar.RetryShownForTest && (string)RadioPlayerBar.PlayButtonForTest.Content == "" && !Radio.WantsPlayback
                    && (RadioPlayerBar.Mode == 2 || RadioPlayerBar.NowTextForTest == "Couldn't connect") && !DuckTimerRunningForTest,
                    $"retry {RadioPlayerBar.RetryShownForTest} glyph {(int)((string)RadioPlayerBar.PlayButtonForTest.Content)[0]:X} wants {Radio.WantsPlayback} now '{RadioPlayerBar.NowTextForTest}' timer {DuckTimerRunningForTest}");
                await Task.Delay(400);
                Check("gave up: no more tries in the background", srv.Connections == 11, srv.Connections + "");
                if (SelfTestClock.ShotsOn) { RadioPlayerBar.FlyoutForTest.IsOpen = true; await ShotWait(400); SnapshotElement(RadioPlayerBar.FlyCardForTest, P("radio-flyout-retry.png")); RadioPlayerBar.FlyoutForTest.IsOpen = false; }
                srv.Refuse = false;
                RadioPlayerBar.PlayButtonForTest.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check("Retry: plays again", await Until(() => Radio.State == RadioState.Playing, 8000), Radio.State + "");
                Radio.Stop();
                await Until(() => srv.Open == 0, 4000);

                // ---- a stream that is not MP3: no retries
                srv.ContentType = "audio/aac"; srv.Connections = 0;
                Radio.Play();
                Check("a format it can't play: says so and does not retry", await Until(() => Radio.State == RadioState.Unsupported, 5000) && srv.Connections == 1 && !Radio.WantsPlayback, Radio.State + " " + srv.Connections);
                srv.ContentType = "audio/mpeg";

                // ---- without the fake server address the self-test player refuses the real one (nothing reaches the network)
                Radio.TestBaseUrl = null;
                int before = Radio.ConnectionsOpened;
                Radio.Play();
                Check("the real address is never opened by a self-test", await Until(() => Radio.State == RadioState.Unsupported, 3000) && Radio.ConnectionsOpened == before);
                Radio.TestBaseUrl = "http://127.0.0.1:" + srv.Port + "/";

                // ---- media keys while the Outpost has the keyboard
                srv.Connections = 0;
                var src = PresentationSource.FromVisual(this);
                void Media(Key k) { var a = new KeyEventArgs(Keyboard.PrimaryDevice, src, 0, k) { RoutedEvent = Keyboard.PreviewKeyDownEvent }; RaiseEvent(a); }
                Media(Key.MediaPlayPause);
                Check("media key Play/Pause: plays", await Until(() => Radio.State == RadioState.Playing, 8000));
                Media(Key.MediaPlayPause);
                Check("media key Play/Pause again: pauses (connection closed)", await Until(() => !Radio.WantsPlayback && srv.Open == 0, 4000));

                // ---- a press on the player is its own (the empty title bar around it still drags the window)
                var press = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent };
                RadioPlayerBar.ChipForTest.RaiseEvent(press);
                Check("a press on the player never starts the window drag (it stops at the player)", press.Handled);

                // ============================================================================================ the title bar at every width
                srv.FixedTitle = "Godspeed You! Black Emperor - Storm (Lift Your Skinny Fists Like Antennas to Heaven)";   // the longest kind of title, playing
                Radio.Play();
                await Until(() => Radio.Title == srv.FixedTitle, 8000);
                Width = 1400; await Settle(); await Until(() => RadioPlayerBar.Mode == 0);
                GameDataProgress.Begin("radiotest", "Spells", "Reading spells");
                GameDataProgress.Rebuilding("radiotest", GameDataProgress.Why.WowUpdated, "Reading spells");
                await Until(() => GameDataBar.ShownForTest, 3000);
                foreach (var wd in new[] { 1400.0, 1100, 1000, 900, 820, 740 })
                {
                    Width = wd; await Settle(); await Settle();
                    Rect R(FrameworkElement e) => e.IsVisible ? new Rect(e.TranslatePoint(new Point(0, 0), this), new Size(e.ActualWidth, e.ActualHeight)) : Rect.Empty;
                    string detail = "";
                    bool Fine()
                    {
                        var search = R(TitleSearchIsIconForTest ? (FrameworkElement)TitleSearchIcon : TitleSearch);
                        var radio = R(RadioPlayerBar.ChipForTest);
                        var chip = R(GameDataBar);
                        var buttons = R((FrameworkElement)VisualTreeHelperFind());
                        bool clipped = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutClip(GameDataBar) != null;
                        detail = $"search {search.Left:0}-{search.Right:0} radio {radio.Left:0}-{radio.Right:0} chip {chip.Left:0}-{chip.Right:0} (wants {GameDataBar.DesiredSize.Width:0}, margin {GameDataBar.Margin}) clipped {clipped} buttons {buttons.Left:0}";
                        detail += $" slider {RadioPlayerBar.InlineVolumeShownForTest} flyslider {RadioPlayerBar.FlyVolumeShownForTest} song {RadioPlayerBar.SongButtonForTest.IsVisible}";
                        bool volumeOk = RadioPlayerBar.Mode == 2 ? !RadioPlayerBar.InlineVolumeShownForTest && RadioPlayerBar.FlyVolumeShownForTest : RadioPlayerBar.InlineVolumeShownForTest && !RadioPlayerBar.FlyVolumeShownForTest;
                        return volumeOk && RadioPlayerBar.SongButtonForTest.IsVisible
                            && radio.Left >= search.Right + 4 && radio.Right <= buttons.Left - 8
                            && (RealmStrip.Visibility != Visibility.Visible || radio.Right <= R(RealmStrip).Left - 8)
                            && (!GameDataBar.IsVisible || (!clipped && chip.Left >= radio.Right + 8 && chip.Right <= buttons.Left - 8));
                    }
                    Check($"title bar at {wd}: search, radio, game data chip (whole, not cut), window buttons in a row without overlap", await Until(Fine, 1500), detail + " mode " + RadioPlayerBar.Mode);
                }
                Check("narrow: the channel and the volume slider move into the song flyout, the title is hidden, the cover stays (it opens the flyout)",
                    RadioPlayerBar.Mode == 2 && !RadioPlayerBar.ChannelBoxForTest.IsVisible && !RadioPlayerBar.InlineVolumeShownForTest && RadioPlayerBar.FlyVolumeShownForTest
                    && RadioPlayerBar.SongButtonForTest.IsVisible && RadioPlayerBar.NowTextForTest == "");
                RadioPlayerBar.FlyVolumeSliderForTest.Value = 0.4;
                Check("narrow: the flyout's slider sets the volume", Math.Abs(Radio.Volume - 0.4) < 0.001 && Math.Abs(RadioPlayerBar.VolumeSliderForTest.Value - 0.4) < 0.001);
                Radio.Stop(); srv.FixedTitle = null;
                await Until(() => srv.Open == 0, 4000);
                GameDataProgress.End("radiotest"); GameDataProgress.ResetForTest();
                Width = w0; Height = h0; await Settle();
                if (SelfTestClock.ShotsOn) { Width = 1280; await ShotWait(500); Snapshot(P("radio-stopped.png")); Width = w0; }

                // ============================================================================================ covers off in Settings
                {
                    SettingsPage.SetRadioCoversForTest(false);
                    Check("Settings: the covers switch is saved", settings.RadioNoCovers);
                    const string talkTalk = "Talk Talk - Life's What You Make It";
                    srv.FixedTitle = talkTalk;
                    Radio.Play();
                    await Until(() => Radio.State == RadioState.Playing && Radio.Title == talkTalk, 8000);
                    int a0 = np.ApiRequests, i0 = np.ImageRequests;
                    await Task.Delay(2000);
                    Check("covers off: no request to radioparadise.com for song details at all; the stream's title, the music note",
                        np.ApiRequests == a0 && np.ImageRequests == i0 && RadioSongs.ApiRequests + RadioSongs.ImageRequests == np.ApiRequests + np.ImageRequests
                        && !RadioPlayerBar.CoverShownForTest && Radio.Title == talkTalk && RadioSongs.Current?.Matched == false, $"api {a0}->{np.ApiRequests}");
                    SettingsPage.SetRadioCoversForTest(true);
                    Check("covers on again: the playing song is asked for once", await Until(() => RadioSongs.Current?.Matched == true && RadioSongs.Current.StreamTitle == talkTalk, 6000)
                        && np.ApiRequests == a0 + 1, $"api {a0}->{np.ApiRequests} {RadioSongs.LastOutcome}");
                    Radio.Stop(); srv.FixedTitle = null;
                    await Until(() => srv.Open == 0, 4000);
                }

                // ============================================================================================ off in Settings
                Radio.Play();
                await Until(() => Radio.State == RadioState.Playing, 8000);
                SettingsPage.SetRadioForTest(false);
                Check("Settings off: the player is hidden and the stream is closed", await Until(() => !RadioPlayerBar.IsVisible && srv.Open == 0 && !Radio.WantsPlayback, 4000) && settings.RadioOff);
                Media(Key.MediaPlayPause);
                await Task.Delay(300);
                Check("Settings off: the media key does nothing", !Radio.WantsPlayback && srv.Open == 0);
                SettingsPage.SetRadioForTest(true);
                Check("Settings on again: the player is back, stopped (no autoplay)", await Until(() => RadioPlayerBar.IsVisible) && Radio.State == RadioState.Stopped && srv.Open == 0);
                SettingsPage.SetRadioDuckForTest(0.5);
                Check("Settings: 'Music while people talk' sets the ducking level", Math.Abs(radioDucker.Level - 0.5) < 0.001 && settings.RadioDuck == 0.5);
                Radio.Dispose();
                Check("closing (Dispose, as Quit does) leaves no connection", srv.Open == 0 && !Radio.WantsPlayback);
            }
            catch (Exception ex) { ok = false; notes.Add("FAIL exception: " + ex); }
            finally
            {
                try { Radio.Stop(); } catch { }
                Radio.TestBaseUrl = null; Radio.BackoffScale = 1; RadioSpeechForTest = null;
                RadioPlayerBar.OpenLink = hadLink;
                try { GameDataProgress.ResetForTest(); } catch { }
                settings.RadioOff = hadOff; settings.RadioChannel = hadFav; settings.RadioDuck = hadDuck; settings.RadioVolume = hadVol; settings.RadioNoCovers = hadNoCovers;
                try { RadioSongs.TestBaseUrl = null; RadioSongs.Gap = RadioNowPlaying.DefaultGap; RadioSongs.CoverDirOverride = null; Radio.Muted = false; } catch { }
                np?.Dispose();
                try { SettingsPage.SetRadioForTest(!hadOff); } catch { }
                Width = w0; Height = h0;
                srv?.Dispose();
            }
            return "radio selftest: " + (ok ? "PASSED" : "FAILED") + "\r\n  " + string.Join("\r\n  ", notes);
        }

        // the minimise / close buttons' panel (right end of the title bar)
        object VisualTreeHelperFind() => ((System.Windows.Controls.Panel)TitleSearch.Parent).Children.OfType<System.Windows.Controls.StackPanel>().First();

        // A tiny ICY server on 127.0.0.1: answers any GET with an endless MP3 stream (silent MPEG-1 Layer III frames, 44.1 kHz, 128 kbit/s) with a
        // metadata block every MetaInt bytes; the title changes every few blocks. Counts connections, keeps the request headers, sees a client go.
        sealed class FakeIcyServer : IDisposable
        {
            readonly TcpListener listener;
            volatile bool stop;
            public readonly int Port;
            public volatile int Connections, Open, PeakOpen;
            public readonly List<string> Requests = new List<string>();
            public volatile bool Refuse;          // answer 503 Service Unavailable (a server that is down)
            public volatile int DropAfterBytes;   // end the stream after this many audio bytes (0 = never)
            public volatile string ContentType = "audio/mpeg";
            public volatile string FixedTitle;
            public volatile string LastTitle;     // the title of the last metadata block that had one (the fake now_playing answers with it)
            public int Blocks;                    // metadata blocks sent (with or without a title)
            public volatile bool RealTime;        // send at the stream's own pace (new data reaches the player at once) instead of as fast as it reads
            public int MetaInt = 4096;
            static readonly byte[] Frame = MakeFrame();

            static byte[] MakeFrame()
            {
                var f = new byte[417];      // 144 * 128000 / 44100, no padding
                f[0] = 0xFF; f[1] = 0xFB; f[2] = 0x90; f[3] = 0x00;   // MPEG-1 Layer III, no CRC, 128 kbit/s, 44100 Hz, stereo; all-zero side info = silence
                return f;
            }

            public FakeIcyServer()
            {
                listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                Port = ((IPEndPoint)listener.LocalEndpoint).Port;
                new Thread(Accept) { IsBackground = true, Name = "Fake ICY" }.Start();
            }

            void Accept()
            {
                while (!stop)
                {
                    TcpClient c;
                    try { c = listener.AcceptTcpClient(); } catch { return; }
                    new Thread(() => Serve(c)) { IsBackground = true }.Start();
                }
            }

            void Serve(TcpClient c)
            {
                Interlocked.Increment(ref Connections);
                int open = Interlocked.Increment(ref Open);
                lock (this) if (open > PeakOpen) PeakOpen = open;
                try
                {
                    using (c)
                    using (var s = c.GetStream())
                    {
                        var head = new StringBuilder();
                        var one = new byte[1];
                        while (!head.ToString().EndsWith("\r\n\r\n") && s.Read(one, 0, 1) == 1) head.Append((char)one[0]);
                        lock (Requests) Requests.Add(head.ToString());
                        if (Refuse) { var no = Encoding.ASCII.GetBytes("HTTP/1.0 503 Service Unavailable\r\nContent-Length: 0\r\n\r\n"); s.Write(no, 0, no.Length); return; }
                        bool meta = head.ToString().IndexOf("\nIcy-MetaData: 1", StringComparison.OrdinalIgnoreCase) >= 0;
                        var resp = "HTTP/1.0 200 OK\r\nContent-Type: " + ContentType + "\r\nicy-name: Fake\r\n" + (meta ? "icy-metaint: " + MetaInt + "\r\n" : "") + "\r\n";
                        var hb = Encoding.ASCII.GetBytes(resp);
                        s.Write(hb, 0, hb.Length);
                        long sent = 0; int pos = 0, blocks = 0;
                        var chunk = new byte[MetaInt];
                        while (!stop)
                        {
                            for (int i = 0; i < chunk.Length; i++) { chunk[i] = Frame[pos]; pos = (pos + 1) % Frame.Length; }
                            s.Write(chunk, 0, meta ? MetaInt : chunk.Length);
                            sent += chunk.Length;
                            if (meta)
                            {
                                var t = FixedTitle ?? "Test Artist - Song " + (1 + blocks / 6);
                                bool withTitle = blocks % 6 == 0 || FixedTitle != null;
                                if (withTitle) LastTitle = t;
                                Interlocked.Increment(ref Blocks);
                                var b = Encoding.UTF8.GetBytes(withTitle ? "StreamTitle='" + t + "';StreamUrl='';" : "");
                                int n = (b.Length + 15) / 16;
                                var m = new byte[1 + n * 16]; m[0] = (byte)n; Array.Copy(b, 0, m, 1, b.Length);
                                s.Write(m, 0, m.Length);
                                blocks++;
                            }
                            if (DropAfterBytes > 0 && sent >= DropAfterBytes) return;
                            if (c.Client.Poll(0, SelectMode.SelectRead) && c.Client.Available == 0) return;   // the client went away
                            Thread.Sleep(RealTime ? 250 : 20);   // 4096 bytes per 250 ms = 128 kbit/s, just ahead of the player
                        }
                    }
                }
                catch { }
                finally { Interlocked.Decrement(ref Open); }
            }

            public void Dispose() { stop = true; try { listener.Stop(); } catch { } }
        }

        // A fake of Radio Paradise's now_playing and its covers on 127.0.0.1 (RadioNowPlaying.TestBaseUrl): /api/now_playing?chan=N answers with the
        // title the fake ICY stream sends right now (like the real API, which can be a moment ahead or behind), covers at
        // https://img.radioparadise.com/covers/{s,m}/<id>.jpg that the client fetches from here by path. Modes: match, mismatch (another song),
        // fail (503), redirect (302 to /elsewhere), evilcover (covers on other hosts / plain http). Counts and keeps every request.
        sealed class FakeNowPlayingServer : IDisposable
        {
            public sealed class Req { public DateTime At; public string Path, Head, Answered; }
            readonly TcpListener listener;
            readonly Func<string> titleNow;
            volatile bool stop;
            public readonly int Port;
            public volatile string Mode = "match";
            public int ApiRequests, ImageRequests;
            public readonly List<Req> Api = new List<Req>();
            public readonly List<string> Images = new List<string>();
            public readonly List<string> Paths = new List<string>();
            readonly List<byte[]> art = new List<byte[]>();
            static readonly Dictionary<string, string[]> Albums = new Dictionary<string, string[]>
            {
                ["Pink Floyd - Wish You Were Here"] = new[] { "Wish You Were Here", "1975" },
                ["Miles Davis - So What"] = new[] { "Kind of Blue", "1959" },
                ["Nina Simone - Feeling Good"] = new[] { "I Put a Spell on You", "1965" },
            };

            public FakeNowPlayingServer(Func<string> title)
            {
                titleNow = title;
                // three made-up "covers" (soft gradients with a disc), drawn here on the UI thread; PNG bytes behind .jpg names like a real CDN might
                var palettes = new[]
                {
                    new[] { System.Windows.Media.Color.FromRgb(0x2B, 0x4C, 0x7E), System.Windows.Media.Color.FromRgb(0xC9, 0x7B, 0x63) },
                    new[] { System.Windows.Media.Color.FromRgb(0x1F, 0x5C, 0x4D), System.Windows.Media.Color.FromRgb(0xE3, 0xC5, 0x67) },
                    new[] { System.Windows.Media.Color.FromRgb(0x5B, 0x2A, 0x6E), System.Windows.Media.Color.FromRgb(0xF2, 0x8C, 0x8C) },
                };
                foreach (var p in palettes)
                {
                    var dv = new System.Windows.Media.DrawingVisual();
                    using (var dc = dv.RenderOpen())
                    {
                        dc.DrawRectangle(new System.Windows.Media.LinearGradientBrush(p[0], p[1], 45), null, new Rect(0, 0, 256, 256));
                        dc.DrawEllipse(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x55, 0, 0, 0)), null, new Point(150, 140), 72, 72);
                        dc.DrawEllipse(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF)), null, new Point(150, 140), 10, 10);
                    }
                    var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(256, 256, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    bmp.Render(dv);
                    var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                    using (var ms = new MemoryStream()) { enc.Save(ms); art.Add(ms.ToArray()); }
                }
                listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                Port = ((IPEndPoint)listener.LocalEndpoint).Port;
                new Thread(Accept) { IsBackground = true, Name = "Fake now_playing" }.Start();
            }

            void Accept()
            {
                while (!stop)
                {
                    TcpClient c;
                    try { c = listener.AcceptTcpClient(); } catch { return; }
                    new Thread(() => Serve(c)) { IsBackground = true }.Start();
                }
            }

            static string Esc(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("/", "\\/");
            static int Id(string s) { int h = 17; foreach (var ch in s ?? "") h = unchecked(h * 31 + ch); return Math.Abs(h % 90000) + 10000; }

            void Serve(TcpClient c)
            {
                try
                {
                    using (c)
                    using (var s = c.GetStream())
                    {
                        var head = new StringBuilder();
                        var one = new byte[1];
                        while (!head.ToString().EndsWith("\r\n\r\n") && s.Read(one, 0, 1) == 1) head.Append((char)one[0]);
                        var h = head.ToString();
                        var path = h.Split(' ').Skip(1).FirstOrDefault() ?? "";
                        lock (Paths) Paths.Add(path);
                        void Send(string status, string type, byte[] body, string extra = "")
                        {
                            var hb = Encoding.ASCII.GetBytes("HTTP/1.0 " + status + "\r\nContent-Type: " + type + "\r\nContent-Length: " + body.Length + "\r\n" + extra + "Connection: close\r\n\r\n");
                            s.Write(hb, 0, hb.Length); s.Write(body, 0, body.Length);
                        }
                        if (path.StartsWith("/api/now_playing"))
                        {
                            Interlocked.Increment(ref ApiRequests);
                            var t = titleNow() ?? "";
                            lock (Api) Api.Add(new Req { At = DateTime.UtcNow, Path = path, Head = h, Answered = t });
                            var mode = Mode;
                            if (mode == "fail") { Send("503 Service Unavailable", "text/plain", new byte[0]); return; }
                            if (mode == "redirect") { Send("302 Found", "text/plain", new byte[0], "Location: http://127.0.0.1:" + Port + "/elsewhere/now_playing\r\n"); return; }
                            int dash = t.IndexOf(" - ", StringComparison.Ordinal);
                            string artist = dash > 0 ? t.Substring(0, dash) : t, title = dash > 0 ? t.Substring(dash + 3) : "";
                            if (mode == "mismatch") { artist = "Someone Else"; title = "Another Song"; }
                            var alb = Albums.TryGetValue(t, out var a) ? a : new[] { "Fake Album", "2001" };
                            int id = Id(artist + title);
                            string sm = "https://img.radioparadise.com/covers/s/" + id + ".jpg", md = "https://img.radioparadise.com/covers/m/" + id + ".jpg";
                            if (mode == "evilcover") { sm = "https://img.radioparadise.com.evil.example/covers/s/" + id + ".jpg"; md = "http://img.radioparadise.com/covers/m/" + id + ".jpg"; }
                            var json = "{\"time\":120,\"artist\":\"" + Esc(artist) + "\",\"title\":\"" + Esc(title) + "\",\"album\":\"" + Esc(alb[0]) + "\",\"year\":\"" + alb[1]
                                + "\",\"cover\":\"" + Esc(md.Replace("/m/", "/l/")) + "\",\"cover_med\":\"" + Esc(md) + "\",\"cover_small\":\"" + Esc(sm) + "\"}";
                            Send("200 OK", "application/json", Encoding.UTF8.GetBytes(json));
                        }
                        else if (path.StartsWith("/covers/"))
                        {
                            Interlocked.Increment(ref ImageRequests);
                            lock (Images) Images.Add(path);
                            int.TryParse(System.IO.Path.GetFileNameWithoutExtension(path), out var id);
                            Send("200 OK", "image/png", art[id % art.Count]);
                        }
                        else Send("404 Not Found", "text/plain", new byte[0]);
                    }
                }
                catch { }
            }

            public void Dispose() { stop = true; try { listener.Stop(); } catch { } }
        }
    }
}
