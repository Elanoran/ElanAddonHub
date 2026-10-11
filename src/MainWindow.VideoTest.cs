using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // YouTube previews in the Lodge chat: id parser, navigation allowlist, the card (fake thumbnail + oEmbed source, no network) and the
    // Privacy switch. ELANSHUB_VIDEO_SHOTS=<dir> (with --selftest) instead runs against the real YouTube with NDNTLFXB7xo and saves
    // chat-video-chat.png / chat-video-overlay.png there.
    public partial class MainWindow
    {
        // a 480x360 fake hqdefault: 16:9 picture between black bars, like the real thing
        static byte[] FakeThumb(byte seed)
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, 480, 360));
                dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(seed, 120, 200), Color.FromRgb(230, 90, seed), 30), null, new Rect(0, 45, 480, 270));
            }
            var rt = new RenderTargetBitmap(480, 360, 96, 96, PixelFormats.Pbgra32);
            rt.Render(dv);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rt));
            using (var ms = new MemoryStream()) { enc.Save(ms); return ms.ToArray(); }
        }

        async Task<string> VideoTest(string dir)
        {
            var shots = Environment.GetEnvironmentVariable("ELANSHUB_VIDEO_SHOTS");
            if (!string.IsNullOrEmpty(shots)) return await VideoShots(shots);
            var notes = new List<string>();
            bool ok = true;
            void Check(string what, bool cond) { notes.Add((cond ? "ok   " : "FAIL ") + what); ok &= cond; }
            var oldThumb = YouTube.ThumbSource; var oldMeta = YouTube.MetaSource;
            bool oldOff = settings.VideoPreviewsOff;
            try
            {
                const string id = "dQw4w9WgXcQ";
                // ---- id parser
                YouTubeLink P(string u) => YouTube.Parse(u);
                Check("parse: watch?v=", P("https://www.youtube.com/watch?v=" + id)?.Id == id);
                Check("parse: bare youtube.com, http", P("http://youtube.com/watch?v=" + id)?.Id == id);
                Check("parse: m.youtube.com", P("https://m.youtube.com/watch?v=" + id)?.Id == id);
                Check("parse: youtu.be", P("https://youtu.be/" + id)?.Id == id);
                Check("parse: youtu.be with si= and t=", P("https://youtu.be/" + id + "?si=abc&t=30")?.StartSeconds == 30);
                Check("parse: shorts", P("https://www.youtube.com/shorts/" + id)?.Id == id);
                Check("parse: v= after other params", P("https://www.youtube.com/watch?list=PLx&v=" + id + "&index=2")?.Id == id);
                Check("parse: t=90", P("https://www.youtube.com/watch?v=" + id + "&t=90")?.StartSeconds == 90);
                Check("parse: t=1m30s", P("https://www.youtube.com/watch?v=" + id + "&t=1m30s")?.StartSeconds == 90);
                Check("parse: t=1h2m3s", P("https://youtu.be/" + id + "?t=1h2m3s")?.StartSeconds == 3723);
                Check("parse: t=45s", P("https://youtu.be/" + id + "?t=45s")?.StartSeconds == 45);
                Check("parse: start=45", P("https://www.youtube.com/watch?v=" + id + "&start=45")?.StartSeconds == 45);
                Check("parse: #t= in the fragment", P("https://www.youtube.com/watch?v=" + id + "#t=2m")?.StartSeconds == 120);
                Check("parse: no / bad time = 0", P("https://youtu.be/" + id)?.StartSeconds == 0 && P("https://youtu.be/" + id + "?t=abc")?.StartSeconds == 0
                    && P("https://youtu.be/" + id + "?t=99999999999999999999")?.StartSeconds == 0);
                Check("find: link inside text, trailing dot trimmed", YouTube.Find("look at this https://youtu.be/" + id + ". so good")?.Id == id);
                Check("find: first YouTube link wins over other links", YouTube.Find("https://example.com/a https://youtu.be/" + id)?.Id == id);
                Check("watch url keeps the time", P("https://youtu.be/" + id + "?t=90").WatchUrl == "https://www.youtube.com/watch?v=" + id + "&t=90s");
                Check("reject: look-alike host youtube.com.evil.net", P("https://youtube.com.evil.net/watch?v=" + id) == null);
                Check("reject: youtu.be.evil.net", P("https://youtu.be.evil.net/" + id) == null);
                Check("reject: notyoutube.com / evilyoutu.be", P("https://notyoutube.com/watch?v=" + id) == null && P("https://evilyoutu.be/" + id) == null);
                Check("reject: user-info trick youtube.com@evil.net", P("https://youtube.com@evil.net/watch?v=" + id) == null);
                Check("reject: youtube as a path or query of another host", P("https://evil.net/youtube.com/watch?v=" + id) == null && P("https://evil.net/?u=youtu.be/" + id) == null);
                Check("reject: not a video page (channel, feed)", P("https://www.youtube.com/@someone") == null && P("https://www.youtube.com/") == null && P("https://www.youtube.com/feed/subscriptions?v=" + id) == null);
                Check("reject: id of 10 / 12 chars or bad chars", P("https://youtu.be/dQw4w9WgXc") == null && P("https://youtu.be/dQw4w9WgXcQx") == null && P("https://youtu.be/dQw4w9WgX!Q") == null
                    && P("https://www.youtube.com/watch?v=dQw4w9WgXc%20") == null);
                Check("reject: non-http schemes", P("javascript:alert(1)//youtu.be/" + id) == null && P("ftp://youtu.be/" + id) == null && P("file:///youtu.be/" + id) == null);
                Check("find: plain text without a link", YouTube.Find("youtu.be/" + id) == null && YouTube.Find("") == null && YouTube.Find(null) == null);

                // ---- the player's navigation allowlist (pure)
                Check("allow: nocookie embed", VideoViewer.IsAllowedNavigation("https://www.youtube-nocookie.com/embed/" + id + "?autoplay=1"));
                Check("allow: youtube.com/embed, bare nocookie host", VideoViewer.IsAllowedNavigation("https://www.youtube.com/embed/" + id) && VideoViewer.IsAllowedNavigation("https://youtube-nocookie.com/embed/" + id));
                Check("allow: ytimg and googlevideo media hosts", VideoViewer.IsAllowedNavigation("https://i.ytimg.com/vi/" + id + "/hqdefault.jpg")
                    && VideoViewer.IsAllowedNavigation("https://rr3---sn-abc.googlevideo.com/videoplayback?x=1") && VideoViewer.IsAllowedNavigation("about:blank"));
                Check("allow: only our own page on the virtual host; look-alikes and other files denied", VideoViewer.IsAllowedNavigation("https://outpost.local/player.html")
                    && !VideoViewer.IsAllowedNavigation("https://outpost.local/other.html") && !VideoViewer.IsAllowedNavigation("https://outpost.local.evil.net/player.html")
                    && !VideoViewer.IsAllowedNavigation("http://outpost.local/player.html") && !VideoViewer.IsAllowedNavigation("data:text/html,<p>x</p>"));
                Check("deny: youtube.com outside /embed (watch page, accounts)", !VideoViewer.IsAllowedNavigation("https://www.youtube.com/watch?v=" + id)
                    && !VideoViewer.IsAllowedNavigation("https://www.youtube-nocookie.com/watch?v=" + id) && !VideoViewer.IsAllowedNavigation("https://accounts.google.com/"));
                Check("deny: look-alike hosts", !VideoViewer.IsAllowedNavigation("https://youtube-nocookie.com.evil.net/embed/" + id)
                    && !VideoViewer.IsAllowedNavigation("https://www.youtube.com.evil.net/embed/" + id) && !VideoViewer.IsAllowedNavigation("https://evilytimg.com/x.jpg")
                    && !VideoViewer.IsAllowedNavigation("https://ytimg.com.evil.net/x.jpg") && !VideoViewer.IsAllowedNavigation("https://googlevideo.com.evil.net/x")
                    && !VideoViewer.IsAllowedNavigation("https://youtube.com@evil.net/embed/" + id) && !VideoViewer.IsAllowedNavigation("https://evil.net/ytimg.com"));
                Check("deny: http, other schemes, junk", !VideoViewer.IsAllowedNavigation("http://www.youtube-nocookie.com/embed/" + id) && !VideoViewer.IsAllowedNavigation("file:///c:/x")
                    && !VideoViewer.IsAllowedNavigation("javascript:alert(1)") && !VideoViewer.IsAllowedNavigation("") && !VideoViewer.IsAllowedNavigation(null) && !VideoViewer.IsAllowedNavigation("not a url"));
                var html = VideoViewer.BuildHtml(P("https://youtu.be/" + id + "?t=90"));
                Check("player page: nocookie iframe, autoplay, start=90, CSP, no script", html.Contains("https://www.youtube-nocookie.com/embed/" + id + "?autoplay=1&start=90")
                    && html.Contains("Content-Security-Policy") && !html.Contains("<script"));
                bool threw = false; try { VideoViewer.BuildHtml(new YouTubeLink { Id = "x\"><script>" }); } catch (ArgumentException) { threw = true; }
                Check("player page: refuses an id that is not 11 safe chars", threw);

                // ---- the card, with a fake thumbnail + oEmbed source (no network)
                var thumbCalls = new List<string>(); var metaCalls = new List<string>();
                const string idNoMeta = "AAAAAAAAAA1", idGone = "BBBBBBBBBB2", idShort = "CCCCCCCCCC3";
                YouTube.ClearCaches();
                YouTube.ThumbSource = i => { lock (thumbCalls) thumbCalls.Add(i); return Task.FromResult(i == idGone ? null : FakeThumb((byte)(i[0] * 3))); };
                YouTube.MetaSource = i => { lock (metaCalls) metaCalls.Add(i); return Task.FromResult(i == id || i == idShort ? new VideoMeta { Title = "Never Gonna Give You Up (Official Video)", Channel = "Rick Astley" } : null); };
                settings.VideoPreviewsOff = false;
                Session.Disconnect();
                TabLodge.IsChecked = true;
                LodgePage.ShowChatForTest();
                var gen = Session.TextChannels.FirstOrDefault(c => c.Id == "general");
                if (gen == null) throw new InvalidOperationException("no general channel");
                Session.Select(gen);
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                string Frame(string mid, string text) => "{\"t\":\"msg\",\"channel\":\"general\",\"id\":" + Q(mid) + ",\"at\":" + now + ",\"from\":\"Bob\",\"fromId\":2,\"text\":" + Q(text) + "}";
                Session.FeedForTest(Frame("yt1", "this boss fight guide is great https://www.youtube.com/watch?v=" + id + "&t=90"));
                Session.FeedForTest(Frame("yt2", "no title for this one https://youtu.be/" + idNoMeta));
                Session.FeedForTest(Frame("yt3", "deleted video https://youtu.be/" + idGone));
                Session.FeedForTest(Frame("yt4", "plain message without a link"));
                var list = Session.MessagesOf("general");
                var m1 = list.First(m => m.Id == "yt1"); var m2 = list.First(m => m.Id == "yt2"); var m3 = list.First(m => m.Id == "yt3"); var m4 = list.First(m => m.Id == "yt4");
                for (int i = 0; i < 40 && (m1.Video?.Title == null || m2.Video?.Thumb == null || thumbCalls.Count < 3); i++) await Task.Delay(100);
                await Task.Delay(300);
                Check("card: thumbnail + title + channel arrive (async)", m1.VideoVisibility == Visibility.Visible && m1.Video.Thumb != null && m1.Video.Title.StartsWith("Never Gonna") && m1.Video.Channel == "Rick Astley");
                Check("card: the start time is kept for the player", m1.Video.Link.StartSeconds == 90);
                Check("card: oEmbed failed -> thumbnail only, no title", m2.VideoVisibility == Visibility.Visible && m2.Video.Thumb != null && string.IsNullOrEmpty(m2.Video.Title) && m2.Video.InfoVisibility == Visibility.Collapsed);
                Check("card: unavailable video -> no card, link text stays", m3.VideoVisibility == Visibility.Collapsed && m3.Text.Contains(idGone) && m3.Video?.Thumb == null);
                Check("card: message without a link has none", m4.Video == null && m4.VideoVisibility == Visibility.Collapsed);
                Session.FeedForTest(Frame("yt5", "again https://youtu.be/" + id));
                await Task.Delay(300);
                Check("cache: the same id is fetched once per session (thumbnail + oEmbed)", thumbCalls.Count(x => x == id) == 1 && metaCalls.Count(x => x == id) == 1
                    && list.First(m => m.Id == "yt5").Video.Thumb != null);
                await Task.Delay(500);
                Snapshot(Path.Combine(dir, "11-video-card.png"));

                // an edit that adds / removes the link rebuilds the card
                m4.Text = "now with a short https://www.youtube.com/shorts/" + idShort;
                for (int i = 0; i < 30 && m4.Video?.Title == null; i++) await Task.Delay(100);
                Check("edit: adding a link makes a card (shorts)", m4.VideoVisibility == Visibility.Visible && m4.Video.Id == idShort);
                m4.Text = "never mind";
                Check("edit: removing it drops the card", m4.Video == null && m4.VideoVisibility == Visibility.Collapsed);

                // ---- Settings > Privacy > Show video previews
                ShowTab("settings"); SettingsPage.Show("privacy");
                await Task.Delay(300);
                Snapshot(Path.Combine(dir, "11-video-privacy.png"));
                Check("setting: default on, the switch shows it", !new Settings().VideoPreviewsOff && ((System.Windows.Controls.CheckBox)SettingsPage.FindName("PvVideo")).IsChecked == true);
                SettingsPage.ToggleForTest("PvVideo");
                Check("setting: switching it off is stored", settings.VideoPreviewsOff);
                Check("setting off: existing cards are gone, links stay as text", list.Where(m => m.Text != null && m.Text.Contains("youtu")).All(m => m.Video == null && m.VideoVisibility == Visibility.Collapsed)
                    && m1.Text.Contains(id));
                int before = thumbCalls.Count + metaCalls.Count;
                YouTube.ClearCaches();
                Session.FeedForTest(Frame("yt6", "off: https://youtu.be/" + idShort));
                await Task.Delay(500);
                Check("setting off: a new message fetches nothing from YouTube", thumbCalls.Count + metaCalls.Count == before && list.First(m => m.Id == "yt6").Video == null);
                Check("setting off: the link text is still detected for a click (opens the browser)", list.First(m => m.Id == "yt6").VideoLink?.Id == idShort);
                SettingsPage.ToggleForTest("PvVideo");
                for (int i = 0; i < 30 && m1.Video?.Title == null; i++) await Task.Delay(100);
                Check("setting on again: cards come back", !settings.VideoPreviewsOff && m1.VideoVisibility == Visibility.Visible && m1.Video.Title != null);
                ShowTab("lodge");
            }
            catch (Exception e) { ok = false; notes.Add("crashed: " + e); }
            finally { YouTube.ThumbSource = oldThumb; YouTube.MetaSource = oldMeta; settings.VideoPreviewsOff = oldOff; YouTube.ClearCaches(); }
            return "video preview selftest: " + (ok ? "PASSED" : "FAILED") + "\r\n  " + string.Join("\r\n  ", notes);
        }

        // real network, real WebView2: a chat message with the card, then the overlay (screen capture, WebView2 is not in RenderTargetBitmap)
        async Task<string> VideoShots(string outDir)
        {
            try
            {
                Directory.CreateDirectory(outDir);
                Session.Disconnect();
                TabLodge.IsChecked = true;
                LodgePage.ShowChatForTest();
                Session.FeedForTest("{\"t\":\"welcome\",\"you\":1,\"name\":\"Elan\",\"role\":\"owner\",\"server\":\"2.4.0\",\"channels\":[{\"id\":\"general\",\"name\":\"General\",\"type\":\"text\",\"minRole\":\"guest\"}],\"users\":[{\"id\":1,\"name\":\"Elan\",\"role\":\"owner\"},{\"id\":2,\"name\":\"Bob\",\"role\":\"officer\"}],\"history\":{\"general\":[]},\"pins\":{}}");
                var gen = Session.TextChannels.First(c => c.Id == "general");
                Session.Select(gen);
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                string Frame(string mid, string text) => "{\"t\":\"msg\",\"channel\":\"general\",\"id\":" + Q(mid) + ",\"at\":" + now + ",\"from\":\"Bob\",\"fromId\":2,\"text\":" + Q(text) + "}";
                Session.FeedForTest(Frame("ys1", "Found this guide for the Wailing Caverns run https://www.youtube.com/watch?v=NDNTLFXB7xo"));
                var m = Session.MessagesOf("general").First(x => x.Id == "ys1");
                for (int i = 0; i < 100 && (m.Video?.Title == null); i++) await Task.Delay(200);
                await Task.Delay(800);
                Snapshot(Path.Combine(outDir, "chat-video-chat.png"));
                var info = $"card={m.VideoVisibility} title='{m.Video?.Title}' channel='{m.Video?.Channel}'";
                LodgePage.VideoPlayerForTest.Open(m.Video.Link, m.Video.Title, m.Video.Channel);
                await Task.Delay(9000);
                var shot = Path.Combine(outDir, "chat-video-overlay.png");
                Snapshot(shot);
                Rect pr = Rect.Empty;
                var png = await LodgePage.VideoPlayerForTest.CapturePlayerForTest(this, r => pr = r);
                if (png != null)
                {
                    var dev = PresentationSource.FromVisual(this).CompositionTarget.TransformToDevice;
                    var tmp = Path.Combine(outDir, "tmp-overlay.png");
                    using (var baseImg = new System.Drawing.Bitmap(shot)) using (var web = new System.Drawing.Bitmap(new MemoryStream(png)))
                    {
                        using (var g = System.Drawing.Graphics.FromImage(baseImg))
                            g.DrawImage(web, (float)(pr.X * dev.M11), (float)(pr.Y * dev.M22), (float)(pr.Width * dev.M11), (float)(pr.Height * dev.M22));
                        baseImg.Save(tmp, System.Drawing.Imaging.ImageFormat.Png);
                    }
                    File.Delete(shot); File.Move(tmp, shot);
                }
                info += $" player={LodgePage.VideoPlayerForTest.HasPlayerForTest}";
                LodgePage.VideoPlayerForTest.Close();
                await Task.Delay(600);
                info += $" afterClose player={LodgePage.VideoPlayerForTest.HasPlayerForTest} open={LodgePage.VideoPlayerForTest.IsOpen}";
                return "video shots: " + info;
            }
            catch (Exception e) { return "video shots crashed: " + e; }
        }
    }
}
