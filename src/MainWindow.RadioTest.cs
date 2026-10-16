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
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // --selftest "radio" (Outpost 2.72): the Radio Paradise player against a local fake ICY server (127.0.0.1, MP3 frames in a loop, icy-metaint, a
    // StreamTitle that changes). Never the real stream: a self-test player only opens loopback addresses (RadioPlayer.UrlAllowed) and plays into a
    // NullOutput. Covers the published-link allow-list, the User-Agent and Icy-MetaData header, one connection on Play and none at start, Pause closing
    // it, the title, the reconnect waits and Retry, the favourite channel, the ducking curve, the Settings switch, the title bar layout. 2.73: the
    // cover from the stream's own StreamUrl against a local fake picture server (FakeCoverServer, never img.radioparadise.com, and no
    // api.radioparadise.com request ever): https only, the host allow-list, the small / large sizes, once per song, the caches, failures; Latin-1
    // titles; the pill (marquee only when the title overflows, the equaliser only while it plays, animations off), the pop-out (channel chips,
    // volume + mute), focus rings only from the keyboard. Pictures with ELANSHUB_SHOTS=1.
    public partial class MainWindow
    {
        async Task<string> RadioTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            void Check(string what, bool cond, string detail = null) { SelfTestClock.Mark(what); notes.Add((cond ? "ok   " : "FAIL ") + what + (!cond && detail != null ? " [" + detail + "]" : "")); ok &= cond; }
            string P(string n) => Path.Combine(dir, n);
            string Shown(string t) { RadioNowPlaying.SplitTitle(t, out var a, out var ti); return a != null ? a + " – " + ti : ti; }
            var hadOff = settings.RadioOff; var hadFav = settings.RadioChannel; var hadDuck = settings.RadioDuck; var hadVol = settings.RadioVolume; var hadNoCovers = settings.RadioNoCovers;
            var hadLink = RadioPlayerBar.OpenLink;
            var w0 = Width; var h0 = Height;
            FakeIcyServer srv = null;
            FakeCoverServer np = null;
            string coverCode = "";
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

                // ---- covers from the stream: the rules (2.73)
                {
                    const string rp = "http://img.radioparadise.com/covers/l/11774.jpg";   // what the stream sends (seen on mellow-192)
                    Check("covers: the stream's http://img.radioparadise.com address is fetched over https (that host only)",
                        RadioNowPlaying.Https(rp) == "https://img.radioparadise.com/covers/l/11774.jpg" && RadioNowPlaying.Https("http://evil.example/covers/l/1.jpg") == "http://evil.example/covers/l/1.jpg"
                        && RadioNowPlaying.Https("http://img.radioparadise.com:8080/covers/l/1.jpg") == "http://img.radioparadise.com:8080/covers/l/1.jpg", RadioNowPlaying.Https(rp));
                    bool got = RadioNowPlaying.CoverUrls(rp, out var sm, out var lg);
                    Check("covers: the 24 px tile uses Radio Paradise's small size (covers/s/), the pop-out the stream's own (covers/l/)",
                        got && sm == "https://img.radioparadise.com/covers/s/11774.jpg" && lg == "https://img.radioparadise.com/covers/l/11774.jpg", sm + " " + lg);
                    bool other = RadioNowPlaying.CoverUrls("https://img.radioparadise.com/art/7.png", out var sm2, out var lg2);
                    Check("covers: another picture path on the host is used as it is (downscaled for the tile)", other && sm2 == lg2 && lg2 == "https://img.radioparadise.com/art/7.png", sm2 + " " + lg2);
                    var goodImg = new[] { "https://img.radioparadise.com/covers/s/24351.jpg", "https://img.radioparadise.com/covers/m/24351.jpg", "https://img.radioparadise.com/covers/l/1.png" };
                    var badImg = new[]
                    {
                        "http://img.radioparadise.com/covers/s/1.jpg", "https://img.radioparadise.com.evil.example/covers/s/1.jpg", "https://evil.example/img.radioparadise.com/covers/s/1.jpg",
                        "https://img.radioparadise.com:444/covers/s/1.jpg", "https://img.radioparadise.com/covers/s/1.jpg?x=1", "https://img.radioparadise.com/covers/s/1.exe",
                        "https://api.radioparadise.com/covers/s/1.jpg", "https://user@img.radioparadise.com/covers/s/1.jpg", "file:///C:/covers/1.jpg", "", null
                    };
                    var letImg = badImg.Where(RadioNowPlaying.IsAllowedCoverUrl).ToList();
                    Check("covers: only https://img.radioparadise.com/... pictures are fetched", goodImg.All(RadioNowPlaying.IsAllowedCoverUrl) && letImg.Count == 0, string.Join(" ", letImg));
                    var badStream = new[] { "http://img.radioparadise.com.evil.example/covers/l/1.jpg", "https://evil.example/covers/l/1.jpg", "http://api.radioparadise.com/api/now_playing?chan=1",
                        "ftp://img.radioparadise.com/covers/l/1.jpg", "http://img.radioparadise.com/covers/l/1.svg", "javascript:alert(1)", "" };
                    var letStream = badStream.Where(u => RadioNowPlaying.CoverUrls(u, out _, out _)).ToList();
                    Check("covers: a StreamUrl on another host / scheme / type is refused", letStream.Count == 0, string.Join(" ", letStream));
                    RadioNowPlaying.SplitTitle("Guns N' Roses - Paradise City - Live", out var a1, out var t1);
                    RadioNowPlaying.SplitTitle("Radio Paradise", out var a2, out var t2);
                    Check("covers: artist and title split at the first \" - \" (no dash: the title only)", a1 == "Guns N' Roses" && t1 == "Paradise City - Live" && a2 == null && t2 == "Radio Paradise", a1 + "|" + t1 + "|" + a2 + "|" + t2);
                    Check("covers: on by default", !new Settings().RadioNoCovers);
                    // every address the cover code knows (its string constants and static strings): img.radioparadise.com only, no api.radioparadise.com
                    var consts = typeof(RadioNowPlaying).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                        .Where(f => f.FieldType == typeof(string)).Select(f => (f.IsLiteral ? f.GetRawConstantValue() : f.GetValue(null)) as string ?? "").ToList();
                    coverCode = string.Join(" ", consts);
                    var lone = new RadioNowPlaying();
                    lone.Update(RadioParadise.Channel("main"), "Miles Davis - So What", rp, true, true);
                    Check("covers: a self-test without the fake server fetches nothing (img.radioparadise.com is never reached)",
                        await Until(() => lone.LastOutcome == "refused", 3000) && lone.ImageRequests == 0 && lone.Refused.Contains("https://img.radioparadise.com/covers/s/11774.jpg"), lone.LastOutcome);
                    var none = new RadioNowPlaying();
                    none.Update(RadioParadise.Channel("radio2050"), "Radio 2050 - Episode", null, true, true);
                    await Task.Delay(100);
                    Check("covers: a song without a StreamUrl gets the music note and asks nothing", none.LastOutcome == "none" && none.ImageRequests == 0 && none.Refused.Count == 0 && none.Current?.SmallUrl == null, none.LastOutcome);
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
                Check("cover: StreamUrl='...'; is read (empty = none; the words inside a title are not the key)",
                    RadioParadise.ParseStreamUrl("StreamTitle='Jose Gonzalez - Heartbeats';StreamUrl='http://img.radioparadise.com/covers/l/11774.jpg';\0\0") == "http://img.radioparadise.com/covers/l/11774.jpg"
                    && RadioParadise.ParseStreamUrl("StreamTitle='A - B';StreamUrl='';") == null && RadioParadise.ParseStreamUrl("StreamTitle='StreamUrl='x' - B';") == null);
                {
                    var latin = Encoding.GetEncoding(1252).GetBytes("StreamTitle='José González - Heartbeats';StreamUrl='';\0\0\0");
                    var utf = Encoding.UTF8.GetBytes("StreamTitle='Bjørk - Jóga';\0");
                    var asUtf8 = Encoding.UTF8.GetString(latin);
                    Check("encoding: Latin-1 / Windows-1252 metadata is read right (as UTF-8 it came out garbled); UTF-8 stays UTF-8",
                        RadioParadise.ParseStreamTitle(RadioParadise.DecodeMeta(latin, latin.Length)) == "José González - Heartbeats" && !asUtf8.Contains("José")
                        && RadioParadise.ParseStreamTitle(RadioParadise.DecodeMeta(utf, utf.Length)) == "Bjørk - Jóga"
                        && RadioParadise.DecodeMeta(Encoding.GetEncoding(1252).GetBytes("Café – “quoted”"), 15) == "Café – “quoted”", RadioParadise.DecodeMeta(latin, latin.Length));
                }
                {
                    var ms = new MemoryStream();
                    void Audio(int n, byte v) { for (int i = 0; i < n; i++) ms.WriteByte(v); }
                    void Meta(byte[] b) { int blocks = (b.Length + 15) / 16; ms.WriteByte((byte)blocks); ms.Write(b, 0, b.Length); for (int i = b.Length; i < blocks * 16; i++) ms.WriteByte(0); }
                    Audio(8, 1); Meta(Encoding.UTF8.GetBytes("StreamTitle='A - One';StreamUrl='http://img.radioparadise.com/covers/l/1.jpg';")); Audio(8, 2); ms.WriteByte(0); Audio(8, 3);
                    Meta(Encoding.GetEncoding(1252).GetBytes("StreamTitle='José González - Heartbeats';StreamUrl='';")); Audio(5, 4);
                    ms.Position = 0;
                    var metas = new List<string>();
                    var icy = new IcyStream(ms, 8);
                    icy.MetaChanged += (t, u) => metas.Add(t + "@" + (u ?? "-"));
                    var outb = new byte[64]; int got = icy.Read(outb, 0, 64);
                    var audio = outb.Take(got).ToArray();
                    Check("ICY stream: the metadata blocks are taken out every icy-metaint bytes, the audio passes whole, title + cover raised once per song (UTF-8 and Latin-1)",
                        got == 29 && audio.Take(8).All(x => x == 1) && audio.Skip(8).Take(8).All(x => x == 2) && audio.Skip(16).Take(8).All(x => x == 3) && audio.Skip(24).All(x => x == 4)
                        && string.Join("|", metas) == "A - One@http://img.radioparadise.com/covers/l/1.jpg|José González - Heartbeats@-", got + " " + string.Join("|", metas));
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
                    var full = new RadioDucker { Level = 1 }; for (int t = 0; t <= 400; t += 10) full.Step(true, t);
                    Check("ducking: 0 % is silent, 100 % is no ducking (never silent unless the user sets 0 %)", zero.Factor == 0 && full.Factor == 1);
                }

                // ============================================================================================ the favourite channel, the chips
                SettingsPage.SetRadioFavouriteForTest("mellow");
                await Settle();
                Check("favourite: set in Settings, saved, and shown at once while nothing plays (its chip lit)", settings.RadioChannel == "mellow" && Radio.Channel.Id == "mellow"
                    && RadioPlayerBar.CheckedChipForTest?.Id == "mellow", settings.RadioChannel + " " + Radio.Channel.Id + " chip " + RadioPlayerBar.CheckedChipForTest?.Id);
                {
                    var fresh = new RadioPlayer();
                    var s2 = new Settings { RadioChannel = "kfat" };
                    fresh.SetChannel(RadioParadise.Channel(s2.RadioChannel).Id);
                    Check("favourite: a new start begins on the favourite (as InitRadio does), stopped", fresh.Channel.Id == "kfat" && fresh.State == RadioState.Stopped && fresh.ConnectionsOpened == 0);
                    Check("favourite: an unknown or missing id falls back to Main Mix", RadioParadise.Channel("nope").Id == "main" && RadioParadise.Channel(null).Id == "main");
                }
                Check("pop-out: the 8 channels as chips, exactly the current one highlighted",
                    RadioPlayerBar.ChipsForTest.Count == 8 && string.Join(",", RadioPlayerBar.ChipsForTest.Select(c => (string)c.Content)) == names && RadioPlayerBar.ChipsForTest.Count(c => c.IsChecked == true) == 1);
                RadioPlayerBar.ChipsForTest[2].IsChecked = true;   // Rock
                await Settle();
                Check("chips: one click switches the channel, for this session only (the favourite stays)", Radio.Channel.Id == "rock" && settings.RadioChannel == "mellow"
                    && SettingsPage.RadioFavouriteForTest == "mellow" && RadioPlayerBar.CheckedChipForTest?.Id == "rock" && RadioPlayerBar.ChipsForTest.Count(c => c.IsChecked == true) == 1, Radio.Channel.Id + " fav " + settings.RadioChannel);

                // ============================================================================================ play against the fake server
                srv = new FakeIcyServer();
                Radio.TestBaseUrl = "http://127.0.0.1:" + srv.Port + "/";
                Radio.Rnd = new Random(3);
                np = new FakeCoverServer();
                RadioSongs.TestBaseUrl = "http://127.0.0.1:" + np.Port + "/";
                RadioSongs.CoverDirOverride = Path.Combine(dir, "radio-covers");
                RadioSongs.ForgetForTest();
                Check("covers: nothing is fetched before Play", np.ImageRequests == 0 && RadioSongs.ImageRequests == 0);
                RadioPlayerBar.PlayButtonForTest.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check("Play: connects, decodes and plays", await Until(() => Radio.State == RadioState.Playing && Radio.DecodedBytes > 0, 8000), Radio.State + " " + Radio.LastError);
                Check("Play: one connection", srv.Connections == 1 && Radio.ConnectionsOpened == 1, srv.Connections + "");
                var req = srv.Requests.FirstOrDefault() ?? "";
                Check("request: GET of the channel's published path", req.StartsWith("GET /rock-192 HTTP/1.1"), req.Split('\n')[0]);
                Check("request: User-Agent ElansOutpost/<version> (+github) and Icy-MetaData: 1",
                    req.IndexOf("\nUser-Agent: " + RadioParadise.UserAgent + "\r", StringComparison.Ordinal) >= 0 && req.IndexOf("\nIcy-MetaData: 1\r", StringComparison.OrdinalIgnoreCase) >= 0, req.Replace("\r\n", " | "));
                Check("request: Connection: close (nothing kept open after Pause)", req.IndexOf("Connection: Close", StringComparison.OrdinalIgnoreCase) >= 0);
                Check("title: read from the stream's metadata and shown in the pill as \"Artist – Title\"", await Until(() => Radio.Title != null && Radio.Title.StartsWith("Test Artist - Song ") && RadioPlayerBar.NowTextForTest == Shown(Radio.Title), 6000),
                    Radio.Title + " / " + RadioPlayerBar.NowTextForTest);
                var firstTitle = Radio.Title;
                Check("title: follows the stream when the song changes", await Until(() => Radio.Title != null && Radio.Title != firstTitle, 8000), firstTitle + " -> " + Radio.Title);
                Check("equaliser: shown and moving while it plays", RadioPlayerBar.EqualiserShownForTest && RadioPlayerBar.EqualiserAnimatingForTest == Motion.Enabled, $"shown {RadioPlayerBar.EqualiserShownForTest} moving {RadioPlayerBar.EqualiserAnimatingForTest}");

                // ============================================================================================ covers from the stream (fake picture server)
                {
                    const string pf = "Pink Floyd - Wish You Were Here";
                    // the fake stream at real-time pace on a fresh connection: a title reaches the player within a moment (as on the real stream)
                    Radio.Stop(); await Until(() => srv.Open == 0, 4000);
                    srv.RealTime = true; srv.FixedTitle = pf;
                    int img0 = np.ImageRequests;
                    Radio.Play();
                    await Until(() => Radio.Title == pf, 12000);
                    Check("covers: the stream's StreamUrl gives the playing song its cover in the pill",
                        await Until(() => RadioPlayerBar.CoverShownForTest && RadioSongs.Current?.StreamTitle == pf && RadioSongs.Current.CoverSmall != null, 8000),
                        RadioSongs.LastOutcome + " / " + RadioSongs.Current?.StreamTitle + " / " + Radio.CoverUrl);
                    Check("covers: the stream named http://img.radioparadise.com/covers/l/...; the player keeps that address", Radio.CoverUrl == "http://img.radioparadise.com/covers/l/" + FakeIcyServer.Id(pf) + ".jpg", Radio.CoverUrl);
                    List<string> asked; lock (RadioSongs.Requested) asked = RadioSongs.Requested.ToList();
                    Check("covers: fetched from https://img.radioparadise.com only (the http address upgraded), the small size for the pill",
                        asked.Count > 0 && asked.All(u => u.StartsWith("https://img.radioparadise.com/covers/")) && asked.Contains("https://img.radioparadise.com/covers/s/" + FakeIcyServer.Id(pf) + ".jpg"), string.Join(" ", asked));
                    var pfImg = np.Images.LastOrDefault(r => r.Path.EndsWith("/" + FakeIcyServer.Id(pf) + ".jpg"));
                    Check("covers: the picture request carries the Outpost's User-Agent", pfImg != null && pfImg.Head.IndexOf("\nUser-Agent: " + RadioParadise.UserAgent + "\r", StringComparison.Ordinal) >= 0, pfImg?.Path);
                    Check("covers: the title still comes from the stream, next to the cover", RadioPlayerBar.NowTextForTest == "Pink Floyd – Wish You Were Here", RadioPlayerBar.NowTextForTest);
                    int imgAt = np.ImageRequests, blocksAt = srv.Blocks;
                    await Task.Delay(2500);
                    Check("covers: one picture per song, not per metadata block (same song for many blocks: no more requests)",
                        np.ImageRequests == imgAt && srv.Blocks - blocksAt >= 5 && np.Images.Count(r => r.Path == "/covers/s/" + FakeIcyServer.Id(pf) + ".jpg") == 1, $"img {imgAt}->{np.ImageRequests} blocks {srv.Blocks - blocksAt}");
                    Check("covers: the larger picture waits until the pop-out opens", !np.Images.Any(r => r.Path.StartsWith("/covers/l/")), string.Join(" ", np.Images.Select(r => r.Path)));
                    // the pop-out: larger cover, title, artist
                    RadioPlayerBar.FlyoutForTest.IsOpen = true; await Settle();
                    Check("pop-out: the larger cover (the stream's covers/l/), the title and the artist (no album or year: the stream has none)",
                        await Until(() => RadioPlayerBar.FlyCoverShownForTest && RadioSongs.Current?.CoverLarge != null, 6000) && RadioPlayerBar.FlyTitleForTest == "Wish You Were Here" && RadioPlayerBar.FlyArtistForTest == "Pink Floyd"
                        && np.Images.Count(r => r.Path == "/covers/l/" + FakeIcyServer.Id(pf) + ".jpg") == 1 && RadioPlayerBar.RpLinkTextForTest == "Radio Paradise · Rock",
                        $"'{RadioPlayerBar.FlyTitleForTest}' '{RadioPlayerBar.FlyArtistForTest}' cover {RadioPlayerBar.FlyCoverShownForTest} {string.Join(" ", np.Images.Select(r => r.Path))}");
                    RadioPlayerBar.FlyoutForTest.IsOpen = false;
                    await Settle();
                    RadioPlayerBar.SongButtonForTest.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Check("pop-out: a click on the pill's cover / title opens it", RadioPlayerBar.FlyoutForTest.IsOpen);
                    RadioPlayerBar.FlyoutForTest.IsOpen = false;

                    // the next song gets its own cover
                    const string md = "Miles Davis - So What";
                    var pfCover = RadioPlayerBar.CoverForTest;
                    srv.FixedTitle = md; await Until(() => Radio.Title == md, 12000);
                    Check("covers: the next song gets its own cover", await Until(() => RadioSongs.Current?.StreamTitle == md && RadioPlayerBar.CoverShownForTest && RadioPlayerBar.CoverForTest != pfCover, 6000), RadioSongs.LastOutcome);

                    // a song heard before: no request at all
                    int img1 = np.ImageRequests;
                    srv.FixedTitle = pf; await Until(() => Radio.Title == pf, 8000);
                    Check("cache: a song heard before shows its cover at once", await Until(() => RadioPlayerBar.CoverShownForTest && RadioSongs.Current?.StreamTitle == pf, 1000), RadioSongs.LastOutcome);
                    await Task.Delay(800);
                    Check("cache: ... without fetching the picture again", np.ImageRequests == img1, $"img {img1}->{np.ImageRequests}");

                    // no StreamUrl: the music note
                    srv.CoverMode = "none";
                    const string air = "Air - La Femme d'Argent";
                    int img2 = np.ImageRequests;
                    srv.FixedTitle = air; await Until(() => Radio.Title == air, 8000);
                    await Task.Delay(600);
                    Check("no StreamUrl: the music note, the stream's title, nothing fetched", !RadioPlayerBar.CoverShownForTest && RadioPlayerBar.NowTextForTest == "Air – La Femme d'Argent"
                        && np.ImageRequests == img2 && Radio.CoverUrl == null, RadioSongs.LastOutcome + " " + Radio.CoverUrl);
                    RadioPlayerBar.FlyoutForTest.IsOpen = true; await Settle();
                    Check("no StreamUrl: the pop-out shows title and artist with the note instead of a cover",
                        RadioPlayerBar.FlyTitleForTest == "La Femme d'Argent" && RadioPlayerBar.FlyArtistForTest == "Air" && !RadioPlayerBar.FlyCoverShownForTest);
                    if (SelfTestClock.ShotsOn) { await ShotWait(400); SnapshotElement(RadioPlayerBar.FlyCardForTest, P("radio-popout-nocover.png")); }
                    RadioPlayerBar.FlyoutForTest.IsOpen = false;
                    srv.CoverMode = "rp";

                    // failures: an error, a redirect, a StreamUrl on another host
                    np.Mode = "fail";
                    const string bon = "Bonobo - Kerala";
                    int img3 = np.ImageRequests;
                    srv.FixedTitle = bon; await Until(() => Radio.Title == bon, 8000);
                    Check("failure: the picture server answers 503 -> quietly the music note, the music plays on", await Until(() => np.ImageRequests > img3 && RadioSongs.LastOutcome == "failed", 6000)
                        && !RadioPlayerBar.CoverShownForTest && RadioPlayerBar.NowTextForTest == "Bonobo – Kerala" && Radio.State == RadioState.Playing, RadioSongs.LastOutcome);
                    np.Mode = "redirect";
                    const string ka = "Khruangbin - Maria También";
                    int img4 = np.ImageRequests;
                    srv.FixedTitle = ka; await Until(() => Radio.Title == ka, 8000);
                    Check("failure: a redirect is never followed -> the music note", await Until(() => np.ImageRequests > img4 && RadioSongs.LastOutcome == "failed", 6000)
                        && !RadioPlayerBar.CoverShownForTest && !np.Paths.Any(p => p.StartsWith("/elsewhere")), RadioSongs.LastOutcome + " " + string.Join(" ", np.Paths.Skip(Math.Max(0, np.Paths.Count - 3))));
                    np.Mode = "notimage";
                    const string ni = "Nils Frahm - Says";
                    int img4b = np.ImageRequests;
                    srv.FixedTitle = ni; await Until(() => Radio.Title == ni, 8000);
                    Check("failure: an answer that is not a picture is dropped -> the music note", await Until(() => np.ImageRequests > img4b && RadioSongs.LastOutcome == "failed", 6000) && !RadioPlayerBar.CoverShownForTest, RadioSongs.LastOutcome);
                    np.Mode = "ok";
                    srv.CoverMode = "evil";
                    const string tt = "Tame Impala - The Less I Know the Better";
                    int img5 = np.ImageRequests, ref5 = RadioSongs.Refused.Count;
                    srv.FixedTitle = tt; await Until(() => Radio.Title == tt, 8000);
                    Check("allow-list: a StreamUrl on another host is refused (title yes, picture no, nothing fetched)",
                        await Until(() => RadioSongs.Current?.StreamTitle == tt && RadioSongs.LastOutcome == "refused", 6000) && np.ImageRequests == img5 && RadioSongs.Refused.Count > ref5 && !RadioPlayerBar.CoverShownForTest
                        && RadioPlayerBar.NowTextForTest == "Tame Impala – The Less I Know the Better",
                        $"img {img5}->{np.ImageRequests} refused {string.Join(" ", RadioSongs.Refused.Skip(ref5))}");
                    srv.CoverMode = "rp";

                    // a Latin-1 title over the stream (as Radio Paradise sends it)
                    srv.Latin1 = true;
                    const string jg = "José González - Heartbeats";
                    srv.FixedTitle = jg;
                    Check("encoding: a Latin-1 title from the stream shows right (José González, not mojibake)",
                        await Until(() => Radio.Title == jg && RadioPlayerBar.NowTextForTest == "José González – Heartbeats", 8000) && await Until(() => RadioPlayerBar.CoverShownForTest, 6000), Radio.Title + " / " + RadioPlayerBar.NowTextForTest);
                    srv.Latin1 = false;

                    // the disk cache: a new session (memory forgotten) loads the picture from cache\radio-covers
                    RadioSongs.ForgetForTest();
                    int img6 = np.ImageRequests;
                    srv.FixedTitle = pf; await Until(() => Radio.Title == pf, 8000);
                    Check("disk cache: the cover comes from cache\\radio-covers (no picture request)",
                        await Until(() => RadioPlayerBar.CoverShownForTest && RadioSongs.Current?.StreamTitle == pf, 6000) && np.ImageRequests == img6
                        && Directory.GetFiles(RadioSongs.CoverDir, "*.img").Length >= 2, $"img {img6}->{np.ImageRequests}");

                    // never the undocumented API
                    lock (RadioSongs.Requested) asked = RadioSongs.Requested.ToList();
                    Check("no api.radioparadise.com request ever (no now_playing, nothing but pictures; the code has no such address)",
                        !np.Paths.Any(p => p.StartsWith("/api")) && asked.All(RadioNowPlaying.IsAllowedCoverUrl) && !asked.Any(u => u.Contains("api.radioparadise.com"))
                        && !srv.Requests.Any(r => r.Contains("/api/")) && coverCode.IndexOf("api.radioparadise", StringComparison.OrdinalIgnoreCase) < 0 && coverCode.Contains(RadioNowPlaying.ImageHost)
                        && typeof(RadioNowPlaying).GetMethods().All(m => m.Name.IndexOf("Api", StringComparison.Ordinal) < 0),
                        string.Join(" ", np.Paths.Where(p => p.StartsWith("/api"))) + " code: " + coverCode);
                    // the connection counts below are about the play / pause checks: this block's own reconnect does not count
                    srv.RealTime = false;
                    srv.Connections = 1;
                    lock (srv.Requests) if (srv.Requests.Count > 1) srv.Requests.RemoveRange(1, srv.Requests.Count - 1);
                }
                Check("play button: shows Pause while playing", (string)RadioPlayerBar.PlayButtonForTest.Content == "\uE769" && (string)RadioPlayerBar.PlayButtonForTest.ToolTip == "Pause");
                Check("ducking runs only while it plays", DuckTimerRunningForTest);
                Check("the self-test plays into a NullOutput, never a sound device", RadioPlayer.SelfTestRun);

                // ============================================================================================ the pill: marquee, equaliser, animations off
                {
                    srv.RealTime = true;
                    Width = 1280; Height = 760; await Settle();
                    await Until(() => RadioPlayerBar.Mode == 0, 2000);
                    RadioPlayerBar.AnimationsOnForTest = () => true;
                    srv.FixedTitle = "Air - Sexy Boy";
                    await Until(() => Radio.Title == srv.FixedTitle && RadioPlayerBar.NowTextForTest == "Air – Sexy Boy", 8000); await Settle();
                    Check("pill: a short title sits still (no marquee) and the pill is only as wide as it needs",
                        RadioPlayerBar.TitleShownForTest && !RadioPlayerBar.MarqueeRunningForTest && RadioPlayerBar.TitleWidthForTest < 120 && RadioPlayerBar.MarqueeOffsetForTest == 0, $"w {RadioPlayerBar.TitleWidthForTest} marquee {RadioPlayerBar.MarqueeRunningForTest}");
                    if (SelfTestClock.ShotsOn) { await Until(() => RadioPlayerBar.CoverShownForTest, 4000); await ShotWait(400); SnapshotElement(RadioPlayerBar.ChipForTest, P("radio-pill-short.png")); }
                    const string longT = "Godspeed You! Black Emperor - Storm (Lift Your Skinny Fists Like Antennas to Heaven)";
                    srv.FixedTitle = longT;
                    await Until(() => Radio.Title == longT, 8000); await Settle();
                    double off0 = RadioPlayerBar.MarqueeOffsetForTest, fl0 = RadioPlayerBar.FadeLeftForTest, fr0 = RadioPlayerBar.FadeRightForTest;
                    Check("pill: resting at its start the title has no fade on the left (the first word reads fully), only on the right where it runs on",
                        fl0 < 0.001 && fr0 < 0.95, $"left {fl0:0.000} right {fr0:0.000}");
                    await Task.Delay(3200);   // the 2 s rest at the start, then it moves
                    double off1 = RadioPlayerBar.MarqueeOffsetForTest;
                    Check("pill: while it scrolls both edges fade", RadioPlayerBar.FadeLeftForTest > 0.03 && RadioPlayerBar.FadeRightForTest < 0.97,
                        $"left {RadioPlayerBar.FadeLeftForTest:0.000} right {RadioPlayerBar.FadeRightForTest:0.000}");
                    Check("pill: a title that does not fit scrolls slowly (after a rest at the start), clipped to 240",
                        RadioPlayerBar.MarqueeRunningForTest && Math.Abs(RadioPlayerBar.TitleWidthForTest - 240) < 0.5 && off0 > -1 && off1 < -5 && !RadioPlayerBar.TitleTrimmedForTest,
                        $"marquee {RadioPlayerBar.MarqueeRunningForTest} w {RadioPlayerBar.TitleWidthForTest} offset {off0:0.0} -> {off1:0.0}");
                    if (SelfTestClock.ShotsOn)
                    {
                        await Until(() => RadioPlayerBar.CoverShownForTest, 4000);
                        SnapshotElement(RadioPlayerBar.ChipForTest, P("radio-pill-long.png"));
                        Snapshot(P("radio-playing.png"));
                        RadioPlayerBar.FlyoutForTest.IsOpen = true; await Until(() => RadioPlayerBar.FlyCoverShownForTest, 4000); await ShotWait(500);
                        SnapshotElement(RadioPlayerBar.FlyCardForTest, P("radio-popout.png"));
                        RadioPlayerBar.FlyoutForTest.IsOpen = false;
                    }
                    RadioPlayerBar.AnimationsOnForTest = () => false;
                    RadioPlayerBar.Refresh(); await Settle();
                    Check("animations off: the long title stands still, trimmed with ... (full text in the tooltip); the equaliser shows still bars",
                        !RadioPlayerBar.MarqueeRunningForTest && RadioPlayerBar.MarqueeOffsetForTest == 0 && RadioPlayerBar.TitleTrimmedForTest && RadioPlayerBar.EqualiserShownForTest && !RadioPlayerBar.EqualiserAnimatingForTest,
                        $"marquee {RadioPlayerBar.MarqueeRunningForTest} trimmed {RadioPlayerBar.TitleTrimmedForTest} eq {RadioPlayerBar.EqualiserShownForTest}/{RadioPlayerBar.EqualiserAnimatingForTest}");
                    RadioPlayerBar.AnimationsOnForTest = () => true;
                    RadioPlayerBar.Refresh(); await Settle();
                    Check("animations on again: the marquee and the equaliser move again", RadioPlayerBar.MarqueeRunningForTest && RadioPlayerBar.EqualiserAnimatingForTest);
                    RadioPlayerBar.PlayButtonForTest.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    await Until(() => !Radio.WantsPlayback && srv.Open == 0, 4000); await Settle();
                    Check("equaliser: gone and still when paused", !RadioPlayerBar.EqualiserShownForTest && !RadioPlayerBar.EqualiserAnimatingForTest, $"{RadioPlayerBar.EqualiserShownForTest}/{RadioPlayerBar.EqualiserAnimatingForTest}");
                    Radio.Play();
                    Check("equaliser: back with the music", await Until(() => Radio.State == RadioState.Playing && RadioPlayerBar.EqualiserShownForTest && RadioPlayerBar.EqualiserAnimatingForTest, 8000));
                    RadioPlayerBar.AnimationsOnForTest = null;
                    srv.FixedTitle = null; srv.RealTime = false;
                    srv.Connections = 1;
                    lock (srv.Requests) if (srv.Requests.Count > 1) srv.Requests.RemoveRange(1, srv.Requests.Count - 1);
                }

                // the pop-out's links: the attribution and Support Radio Paradise (the Outpost browser in the app; captured here, nothing is opened)
                var opened = new List<string>();
                RadioPlayerBar.OpenLink = u => opened.Add(u);
                RadioPlayerBar.ClickRpLinkForTest(); RadioPlayerBar.ClickSupportForTest();
                Check("pop-out: 'Radio Paradise · <channel>' links to radioparadise.com, 'Support Radio Paradise' to /donate",
                    string.Join(" ", opened) == "https://radioparadise.com https://radioparadise.com/donate" && RadioPlayerBar.RpLinkTextForTest == "Radio Paradise · Rock" && hadLink != null, string.Join(" ", opened));
                RadioPlayerBar.OpenLink = hadLink;

                // ---- the volume lives in the pop-out
                RadioPlayerBar.FlyoutForTest.IsOpen = true; await Settle();
                Check("volume: the slider and the speaker are in the pop-out (the pill has neither)", RadioPlayerBar.VolumeSliderForTest.IsVisible && RadioPlayerBar.MuteButtonForTest.IsVisible
                    && !IsDescendant(RadioPlayerBar.ChipForTest, RadioPlayerBar.VolumeSliderForTest) && RadioPlayerBar.VolumeSliderForTest.ActualWidth >= 150, "w " + RadioPlayerBar.VolumeSliderForTest.ActualWidth);
                RadioPlayerBar.VolumeSliderForTest.Value = 0.5;
                Check("volume: the slider sets the gain (squared curve) and is remembered", Math.Abs(Radio.Volume - 0.5) < 0.001 && Math.Abs(Radio.Gain - 0.25) < 0.001 && settings.RadioVolume == 0.5);
                RadioPlayerBar.MuteButtonForTest.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check("volume: the speaker mutes (gain 0, the volume is kept)", Radio.Muted && Radio.Gain == 0 && Math.Abs(Radio.Volume - 0.5) < 0.001 && (string)RadioPlayerBar.MuteButtonForTest.ToolTip == "Unmute" && (string)RadioPlayerBar.MuteButtonForTest.Content == "\uE74F");
                RadioPlayerBar.MuteButtonForTest.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check("volume: and again unmutes", !Radio.Muted && Math.Abs(Radio.Gain - 0.25) < 0.001 && (string)RadioPlayerBar.MuteButtonForTest.ToolTip == "Mute");
                RadioPlayerBar.MuteButtonForTest.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                RadioPlayerBar.VolumeSliderForTest.Value = 0.7; RadioPlayerBar.VolumeSliderForTest.Value = 0.5;
                Check("volume: moving the slider unmutes", !Radio.Muted && Math.Abs(Radio.Volume - 0.5) < 0.001);

                // ---- focus rings: only from the keyboard (FocusVisualStyle), never after a mouse click
                {
                    var mri = typeof(InputManager).GetProperty("MostRecentInputDevice");
                    void Recent(InputDevice dev) => mri.GetSetMethod(true).Invoke(InputManager.Current, new object[] { dev });
                    bool Visual(UIElement el) { var layer = AdornerLayer.GetAdornerLayer(el); var ads = layer?.GetAdorners(el); return ads != null && ads.Any(a => a.GetType().Name.Contains("FocusVisual")); }
                    var controls = new FrameworkElement[] { RadioPlayerBar.SongButtonForTest, RadioPlayerBar.PlayButtonForTest, RadioPlayerBar.ChipsForTest[0], RadioPlayerBar.VolumeSliderForTest,
                        RadioPlayerBar.MuteButtonForTest, RadioPlayerBar.RpLinkForTest, RadioPlayerBar.RetryButtonForTest };
                    var noStyle = controls.Where(c => c.FocusVisualStyle == null).Select(c => c.Name).ToList();
                    var drawn = controls.Where(c => FindVisible(c, "ring")).Select(c => c.Name).ToList();
                    Check("focus: every radio control draws its ring through FocusVisualStyle (none of them has a ring of its own on keyboard focus)", noStyle.Count == 0 && drawn.Count == 0,
                        "no style: " + string.Join(",", noStyle) + " own ring: " + string.Join(",", drawn));
                    // a mouse click: each control takes the keyboard focus (as ButtonBase / Slider / RadioButton do on a press): no ring on any of them
                    Activate();
                    bool cues = SystemParameters.KeyboardCues;
                    var ringed = new List<string>();
                    int focused = 0;
                    foreach (var c in controls.Where(c => c.IsVisible))
                    {
                        Recent(Mouse.PrimaryDevice);
                        c.Focus(); await Settle();
                        if (c.IsKeyboardFocusWithin) focused++;
                        if (FindVisible(c, "ring") || (!cues && Visual(c))) ringed.Add(c.Name);
                    }
                    Check("focus: no ring after a mouse click on the pill, a chip, the slider, the speaker or the link" + (cues ? " (Windows always shows focus here: the own rings are checked)" : ""),
                        focused >= 6 && ringed.Count == 0, $"focused {focused} ringed {string.Join(",", ringed)} cues {cues}");
                    RadioPlayerBar.FlyoutForTest.IsOpen = false; await Settle();
                    var pb = RadioPlayerBar.PlayButtonForTest;
                    // Tab: the next control gets the ring
                    Recent(Keyboard.PrimaryDevice);
                    RadioPlayerBar.SongButtonForTest.Focus(); await Settle();
                    var tab = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this), 0, Key.Tab) { RoutedEvent = Keyboard.KeyDownEvent, Source = RadioPlayerBar.SongButtonForTest };
                    InputManager.Current.ProcessInput(tab);
                    await Settle();
                    var now = Keyboard.FocusedElement as UIElement;
                    Check("focus: Tab moves to the next control and its ring shows", now == pb && Visual(pb), $"focused {(now as FrameworkElement)?.Name ?? now?.GetType().Name} visual {Visual(pb)}");
                    Recent(Mouse.PrimaryDevice);
                    Keyboard.ClearFocus();
                }

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

                // ---- covers: nothing is fetched while paused / stopped
                {
                    int a0 = np.ImageRequests;
                    RadioSongs.Update(Radio.Channel, "Paused Artist - Paused Song", "http://img.radioparadise.com/covers/l/424242.jpg", false, true);
                    await Task.Delay(800);
                    Check("covers: a new song while paused / stopped fetches nothing", np.ImageRequests == a0, (np.ImageRequests - a0) + " requests");
                    RadioSongs.Update(Radio.Channel, Radio.Title, Radio.CoverUrl, false, true);
                }

                // ---- channel change while playing (a chip): the old connection goes before the new one opens
                Radio.Play();
                await Until(() => Radio.State == RadioState.Playing, 8000);
                RadioPlayerBar.ChipsForTest.First(c => ((RadioChannel)c.Tag).Id == "global").IsChecked = true;
                Check("chip while playing: a new connection to the new channel", await Until(() => Radio.State == RadioState.Playing && srv.Requests.Count == 3 && srv.Requests[2].StartsWith("GET /global-192 "), 8000), srv.Requests.Count + "");
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
                Check("gave up: the button and the pop-out offer Retry, nothing else connects", RadioPlayerBar.RetryShownForTest && (string)RadioPlayerBar.PlayButtonForTest.Content == "\uE72C" && !Radio.WantsPlayback
                    && (RadioPlayerBar.Mode == 2 || RadioPlayerBar.NowTextForTest == "Couldn't connect") && !DuckTimerRunningForTest && !RadioPlayerBar.EqualiserShownForTest,
                    $"retry {RadioPlayerBar.RetryShownForTest} glyph {(int)((string)RadioPlayerBar.PlayButtonForTest.Content)[0]:X} wants {Radio.WantsPlayback} now '{RadioPlayerBar.NowTextForTest}' timer {DuckTimerRunningForTest}");
                await Task.Delay(400);
                Check("gave up: no more tries in the background", srv.Connections == 11, srv.Connections + "");
                if (SelfTestClock.ShotsOn) { RadioPlayerBar.FlyoutForTest.IsOpen = true; await ShotWait(400); SnapshotElement(RadioPlayerBar.FlyCardForTest, P("radio-popout-retry.png")); RadioPlayerBar.FlyoutForTest.IsOpen = false; }
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
                var psrc = PresentationSource.FromVisual(this);
                void Media(Key k) { var a = new KeyEventArgs(Keyboard.PrimaryDevice, psrc, 0, k) { RoutedEvent = Keyboard.PreviewKeyDownEvent }; RaiseEvent(a); }
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
                var titleWs = new List<double>();
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
                        detail += $" title {RadioPlayerBar.TitleShownForTest} {RadioPlayerBar.TitleWidthForTest:0} eq {RadioPlayerBar.EqualiserShownForTest} song {RadioPlayerBar.SongButtonForTest.IsVisible}";
                        bool compactOk = RadioPlayerBar.Mode != 2 || (!RadioPlayerBar.TitleShownForTest && !RadioPlayerBar.EqualiserShownForTest);
                        return compactOk && RadioPlayerBar.SongButtonForTest.IsVisible && RadioPlayerBar.PlayButtonForTest.IsVisible
                            && radio.Left >= search.Right + 4 && radio.Right <= buttons.Left - 8
                            && (RealmStrip.Visibility != Visibility.Visible || radio.Right <= R(RealmStrip).Left - 8)
                            && (!GameDataBar.IsVisible || (!clipped && chip.Left >= radio.Right + 8 && chip.Right <= buttons.Left - 8));
                    }
                    Check($"title bar at {wd}: search, radio pill, game data chip (whole, not cut), window buttons in a row without overlap", await Until(Fine, 1500), detail + " mode " + RadioPlayerBar.Mode);
                    titleWs.Add(RadioPlayerBar.TitleWidthForTest);
                    if (SelfTestClock.ShotsOn && wd == 740) { await ShotWait(500); Snapshot(P("radio-narrow.png")); RadioPlayerBar.FlyoutForTest.IsOpen = true; await ShotWait(500); SnapshotElement(RadioPlayerBar.FlyCardForTest, P("radio-popout-narrow.png")); RadioPlayerBar.FlyoutForTest.IsOpen = false; }
                }
                // (below about 820 px the search box becomes an icon and gives some room back, so the title may come back for a while)
                Check("narrow: the pill's title shrinks with the room (240 at 1400, less at 1100, less again at 1000) before the pill goes compact",
                    Math.Abs(titleWs[0] - 240) < 0.5 && titleWs[1] < titleWs[0] && titleWs[2] < titleWs[1], string.Join(", ", titleWs.Select(x => x.ToString("0"))));
                Check("narrowest (740 with the game data chip): only the cover and play / pause are left; the pop-out still has the channels and the volume",
                    RadioPlayerBar.Mode == 2 && !RadioPlayerBar.TitleShownForTest && !RadioPlayerBar.EqualiserShownForTest && RadioPlayerBar.SongButtonForTest.IsVisible && RadioPlayerBar.PlayButtonForTest.IsVisible
                    && RadioPlayerBar.ChipForTest.ActualWidth <= 72, $"mode {RadioPlayerBar.Mode} w {RadioPlayerBar.ChipForTest.ActualWidth:0}");
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
                    int i0 = np.ImageRequests;
                    await Task.Delay(1500);
                    Check("covers off: no picture is fetched at all; the stream's title, the music note",
                        np.ImageRequests == i0 && RadioSongs.ImageRequests == np.ImageRequests && !RadioPlayerBar.CoverShownForTest && Radio.Title == talkTalk && RadioSongs.Current?.SmallUrl == null, $"img {i0}->{np.ImageRequests}");
                    SettingsPage.SetRadioCoversForTest(true);
                    Check("covers on again: the playing song's cover is fetched once", await Until(() => RadioPlayerBar.CoverShownForTest && RadioSongs.Current?.StreamTitle == talkTalk, 6000)
                        && np.ImageRequests == i0 + 1, $"img {i0}->{np.ImageRequests} {RadioSongs.LastOutcome}");
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
                RadioPlayerBar.OpenLink = hadLink; RadioPlayerBar.AnimationsOnForTest = null;
                try { GameDataProgress.ResetForTest(); } catch { }
                settings.RadioOff = hadOff; settings.RadioChannel = hadFav; settings.RadioDuck = hadDuck; settings.RadioVolume = hadVol; settings.RadioNoCovers = hadNoCovers;
                try { RadioSongs.TestBaseUrl = null; RadioSongs.CoverDirOverride = null; Radio.Muted = false; } catch { }
                np?.Dispose();
                try { SettingsPage.SetRadioForTest(!hadOff); } catch { }
                Width = w0; Height = h0;
                srv?.Dispose();
            }
            return "radio selftest: " + (ok ? "PASSED" : "FAILED") + "\r\n  " + string.Join("\r\n  ", notes);
        }

        // the minimise / close buttons' panel (right end of the title bar)
        object VisualTreeHelperFind() => ((System.Windows.Controls.Panel)TitleSearch.Parent).Children.OfType<System.Windows.Controls.StackPanel>().First();

        static bool IsDescendant(DependencyObject root, DependencyObject el)
        {
            for (var d = el; d != null; d = VisualTreeHelper.GetParent(d)) if (d == root) return true;
            return false;
        }

        // a visible element with this name inside the control's template (a ring of its own)
        static bool FindVisible(DependencyObject root, string name)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var c = VisualTreeHelper.GetChild(root, i);
                if (c is FrameworkElement fe && fe.Name == name && fe.IsVisible) return true;
                if (FindVisible(c, name)) return true;
            }
            return false;
        }

        // A tiny ICY server on 127.0.0.1: answers any GET with an endless MP3 stream (silent MPEG-1 Layer III frames, 44.1 kHz, 128 kbit/s) with a
        // metadata block every MetaInt bytes; the title changes every few blocks. Each title block names a cover the way Radio Paradise does
        // (StreamUrl='http://img.radioparadise.com/covers/l/<id>.jpg'; CoverMode none = empty, evil = another host), in UTF-8 or (Latin1) Windows-1252.
        // Counts connections, keeps the request headers, sees a client go.
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
            public volatile string CoverMode = "rp";
            public volatile bool Latin1;
            public int Blocks;                    // metadata blocks sent (with or without a title)
            public volatile bool RealTime;        // send at the stream's own pace (new data reaches the player at once) instead of as fast as it reads
            public int MetaInt = 4096;
            static readonly byte[] Frame = MakeFrame();

            public static int Id(string s) { int h = 17; foreach (var ch in s ?? "") h = unchecked(h * 31 + ch); return Math.Abs(h % 90000) + 10000; }

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
                                Interlocked.Increment(ref Blocks);
                                var mode = CoverMode;
                                var url = mode == "none" ? "" : mode == "evil" ? "http://img.radioparadise.com.evil.example/covers/l/" + Id(t) + ".jpg" : "http://img.radioparadise.com/covers/l/" + Id(t) + ".jpg";
                                var enc = Latin1 ? Encoding.GetEncoding(1252) : Encoding.UTF8;
                                var b = enc.GetBytes(withTitle ? "StreamTitle='" + t + "';StreamUrl='" + url + "';" : "");
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

        // A fake of img.radioparadise.com on 127.0.0.1 (RadioNowPlaying.TestBaseUrl): /covers/{s,m,l}/<id>.jpg answer with one of three made-up
        // covers; anything else is 404 (and kept, so the test can show that nothing else - no /api/now_playing - was ever asked). Modes: ok, fail
        // (503), redirect (302 to /elsewhere), notimage (text/html). Counts and keeps every request.
        sealed class FakeCoverServer : IDisposable
        {
            public sealed class Req { public DateTime At; public string Path, Head; }
            readonly TcpListener listener;
            volatile bool stop;
            public readonly int Port;
            public volatile string Mode = "ok";
            public int ImageRequests;
            public readonly List<Req> Images = new List<Req>();
            public readonly List<string> Paths = new List<string>();
            readonly List<byte[]> art = new List<byte[]>();

            public FakeCoverServer()
            {
                // three made-up "covers" (soft gradients with a disc), drawn here on the UI thread; PNG bytes behind .jpg names like a real CDN might
                var palettes = new[]
                {
                    new[] { Color.FromRgb(0x2B, 0x4C, 0x7E), Color.FromRgb(0xC9, 0x7B, 0x63) },
                    new[] { Color.FromRgb(0x1F, 0x5C, 0x4D), Color.FromRgb(0xE3, 0xC5, 0x67) },
                    new[] { Color.FromRgb(0x5B, 0x2A, 0x6E), Color.FromRgb(0xF2, 0x8C, 0x8C) },
                };
                foreach (var p in palettes)
                {
                    var dv = new DrawingVisual();
                    using (var dc = dv.RenderOpen())
                    {
                        dc.DrawRectangle(new LinearGradientBrush(p[0], p[1], 45), null, new Rect(0, 0, 256, 256));
                        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0)), null, new Point(150, 140), 72, 72);
                        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF)), null, new Point(150, 140), 10, 10);
                    }
                    var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(256, 256, 96, 96, PixelFormats.Pbgra32);
                    bmp.Render(dv);
                    var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                    using (var ms = new MemoryStream()) { enc.Save(ms); art.Add(ms.ToArray()); }
                }
                listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                Port = ((IPEndPoint)listener.LocalEndpoint).Port;
                new Thread(Accept) { IsBackground = true, Name = "Fake covers" }.Start();
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
                        if (path.StartsWith("/covers/"))
                        {
                            Interlocked.Increment(ref ImageRequests);
                            lock (Images) Images.Add(new Req { At = DateTime.UtcNow, Path = path, Head = h });
                            var mode = Mode;
                            if (mode == "fail") { Send("503 Service Unavailable", "text/plain", new byte[0]); return; }
                            if (mode == "redirect") { Send("302 Found", "text/plain", new byte[0], "Location: http://127.0.0.1:" + Port + "/elsewhere/cover.jpg\r\n"); return; }
                            if (mode == "notimage") { Send("200 OK", "text/html", Encoding.ASCII.GetBytes("<html>not a picture</html>")); return; }
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
