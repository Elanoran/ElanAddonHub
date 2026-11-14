using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // --selftest "blizzardnews": Settings > About > Blizzard news (3.10) against a local fixture of the forum's tracker JSON - never the internet.
    // The feed kept to Forever (its categories or a title naming Forever), plain text only, the server / hotfix kinds, the address rule (exactly
    // the EU or US tracker over https), no cookies / redirects, the saved copy, at most one fetch per half hour, a failed fetch keeping the saved
    // posts, older posts kept when they drop out of the feed; the tab (nothing fetched before the one-time click, the cards, the EU / US switch);
    // and new posts: the dot on the tab, the corner note once per post (not on the first load, not when turned off), "Read it" opening the tab.
    public partial class MainWindow
    {
        static string TrackerPost(long id, int cat, string topic, string excerpt, string created, string url = null, bool truncated = false) =>
            "{\"id\":" + id + ",\"topic_id\":" + (id / 10) + ",\"post_number\":" + (id % 10 + 1) + ",\"category_id\":" + cat + ",\"created_at\":\"" + created + "\",\"topic_title\":" + Json.Write(topic)
            + ",\"excerpt\":" + Json.Write(excerpt) + ",\"truncated\":" + (truncated ? "true" : "false") + ",\"url\":" + Json.Write(url ?? "/t/some-topic/" + (id / 10) + "/" + (id % 10 + 1))
            + ",\"username\":\"Kaivax\",\"user_title\":\"Community Manager\",\"user\":{\"username\":\"Kaivax\",\"title\":\"Community Manager\"}}";

        async Task<string> BlizzardNewsTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            void Check(string what, bool cond, string detail = null) { SelfTestClock.Mark(what); notes.Add((cond ? "ok   " : "FAIL ") + what + (!cond && detail != null ? " [" + detail + "]" : "")); ok &= cond; }
            var work = Path.Combine(dir, "blizzardnews");
            var savedGet = BlizzardNews.Get; var savedFile = BlizzardNews.CacheFile;
            var saved = (settings.BlizzardNewsLoad, settings.BlizzardNewsRegion, settings.BlizzardNewsSeen, settings.BlizzardNewsNoted, settings.BlizzardNewsAlertsOff);
            var asked = new List<string>();
            bool fail = false;
            string feed = null;
            string Feed(params string[] posts) => "{\"posts\":[" + string.Join(",", posts) + "]}";
            var hotfix = TrackerPost(6502386, 360, "WoW: Forever Hotfixes - November 11, 2026", "<p>November 13, 2026 &amp; more</p>\n\n Classes  <img src=\"https://example.invalid/e.png\"> fixed", "2026-11-14T01:55:46.295Z", null, true);
            var refunds = TrackerPost(6502300, 360, "Lost Training Purchase Refunds - 13 November", "We are refunding <b>gold</b> for lost training.", "2026-11-14T01:36:00.000Z");
            var midnight = TrackerPost(6502250, 229, "Raid Encounter Design in Season 3", "Not about Forever.", "2026-11-13T21:56:00.000Z");
            var titled = TrackerPost(6502200, 235, "Watch the WoW: Forever Short Story", "A short story.", "2026-11-13T18:02:00.000Z", "/t/../../evil/1");
            var server = TrackerPost(6502500, 360, "Forever: Login issues and server restarts - 14 November", "We are aware of players stuck on the loading screen.", "2026-11-14T19:40:00.000Z");
            try
            {
                if (Directory.Exists(work)) Util.DeleteDir(work);
                Directory.CreateDirectory(work);
                BlizzardNews.CacheFile = Path.Combine(work, "blizzardnews.json"); BlizzardNews.ResetForTest();
                settings.BlizzardNewsLoad = false; settings.BlizzardNewsRegion = null; settings.BlizzardNewsSeen = 0; settings.BlizzardNewsNoted = 0; settings.BlizzardNewsAlertsOff = false;
                BlizzardNews.Get = (url, ct) =>
                {
                    lock (asked) asked.Add(url);
                    if (fail) throw new HttpRequestException("the forum answered 503");
                    if (!BlizzardNews.UrlAllowed(url)) throw new InvalidOperationException("refused " + url);
                    return Task.FromResult(feed);
                };
                Check("fixture: the saved news is under the scratch folder (never the real data folder)", Path.GetFullPath(BlizzardNews.CacheFile).StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase));

                // ---- the feed as Forever posts
                var parsed = BlizzardNews.Parse("eu", Feed(hotfix, refunds, midnight, titled));
                Check("feed: posts in the Forever categories and topics naming Forever are kept, others (a Midnight raid post) left out, newest first",
                    parsed.Select(p => p.Id).SequenceEqual(new long[] { 6502386, 6502300, 6502200 }), string.Join(",", parsed.Select(p => p.Id)));
                var h = parsed[0];
                Check("feed: the excerpt as plain text (tags and pictures out, entities decoded, spaces folded), a cut excerpt ends in …; author without the forum's number",
                    h.Excerpt == "November 13, 2026 & more Classes fixed…" && h.Author == "Kaivax" && h.Title == "Community Manager" && h.Posted == new DateTime(2026, 11, 14, 1, 55, 46, 295, DateTimeKind.Utc), h.Excerpt + " | " + h.Posted.ToString("o"));
                Check("feed: a post's link is the forum's own path on the same forum; an odd path falls back to the topic number",
                    h.Url == "https://eu.forums.blizzard.com/en/wow/t/some-topic/650238/7" && parsed[2].Url == "https://eu.forums.blizzard.com/en/wow/t/650220/1", parsed[2].Url);
                Check("feed: kinds - a hotfix list, server trouble, the rest news",
                    h.Kind == BlizzardNews.Kind.Hotfix && BlizzardNews.Parse("eu", Feed(server))[0].Kind == BlizzardNews.Kind.Server && parsed[1].Kind == BlizzardNews.Kind.News);
                Check("feed: not the tracker = an error, not an empty list", Throws(() => BlizzardNews.Parse("eu", "[]")) && Throws(() => BlizzardNews.Parse("eu", "{\"x\":1}")));

                // ---- the address rule and the connection
                Check("address: exactly the EU and US tracker feeds over https",
                    BlizzardNews.UrlAllowed("https://eu.forums.blizzard.com/en/wow/groups/blizzard-tracker/posts.json") && BlizzardNews.UrlAllowed("https://us.forums.blizzard.com/en/wow/groups/blizzard-tracker/posts.json"));
                Check("address: anything else is refused (http, another host, another page, a query, a login, a port)",
                    new[] { "http://eu.forums.blizzard.com/en/wow/groups/blizzard-tracker/posts.json", "https://evil.example/en/wow/groups/blizzard-tracker/posts.json", "https://eu.forums.blizzard.com/session/current.json",
                            "https://eu.forums.blizzard.com/en/wow/groups/blizzard-tracker/posts.json?user=me", "https://me:pw@eu.forums.blizzard.com/en/wow/groups/blizzard-tracker/posts.json",
                            "https://eu.forums.blizzard.com:8443/en/wow/groups/blizzard-tracker/posts.json", "https://kr.forums.blizzard.com/en/wow/groups/blizzard-tracker/posts.json" }.All(u => !BlizzardNews.UrlAllowed(u)));
                Check("connection: no cookies, no redirects, no Windows login sent", !BlizzardNews.Handler.UseCookies && !BlizzardNews.Handler.AllowAutoRedirect && !BlizzardNews.Handler.UseDefaultCredentials);

                // ---- a fetch, the saved copy
                feed = Feed(hotfix, refunds, midnight, titled);
                var r = await BlizzardNews.Refresh("eu");
                Check("fetch: one request to the EU feed; the Forever posts", asked.Count == 1 && asked[0] == BlizzardNews.FeedUrl("eu") && r.Posts.Count == 3 && r.Problem == null, string.Join(", ", asked));
                var file = File.ReadAllText(BlizzardNews.CacheFile);
                Check("saved: plain text on disk, no picture addresses or markup", file.Contains("Lost Training Purchase Refunds") && !file.Contains("example.invalid") && !file.Contains("<b>"));
                await BlizzardNews.Refresh("eu");
                Check("fetch: fresh news is not fetched again (at most every half hour)", asked.Count == 1);
                BlizzardNews.ResetForTest();
                var again = BlizzardNews.Saved();
                Check("saved: read back after a restart (posts, links, dates)", again.Posts.Count == 3 && again.Posts[0].Url == h.Url && again.Posts[0].Posted == h.Posted && !BlizzardNews.Stale(again, "eu"));
                Check("saved: another forum's news counts as stale", BlizzardNews.Stale(again, "us"));
                feed = Feed(server, hotfix);
                r = await BlizzardNews.Refresh("eu", force: true);
                Check("fetch: posts that dropped out of the feed's 20 are kept, the new one first", r.Posts.Select(p => p.Id).SequenceEqual(new long[] { 6502500, 6502386, 6502300, 6502200 }), string.Join(",", r.Posts.Select(p => p.Id)));
                fail = true;
                var bad = await BlizzardNews.Refresh("eu", force: true);
                Check("fetch: the forum unreachable = a plain problem line, the saved posts stay", bad.Problem != null && bad.Problem.Contains("503") && bad.Posts.Count == 4, bad.Problem);
                fail = false;
                Check("unseen: none before the reader saw anything; newer ids after", BlizzardNews.Unseen(r, 0).Count == 0 && BlizzardNews.Unseen(r, 6502386).Select(p => p.Id).SequenceEqual(new long[] { 6502500 }));

                // ============================================================================ the tab
                File.Delete(BlizzardNews.CacheFile); BlizzardNews.ResetForTest(); asked.Clear();
                feed = Feed(hotfix, refunds, midnight, titled);
                ShowTab("settings"); SettingsPage.Show("about"); SettingsPage.ShowAboutTab("blizzard");
                await Task.Delay(200); UpdateLayout();
                FrameworkElement Body() => SettingsPage.BlizzardBodyForTest;
                Button Btn(string tag) => Walk(Body()).OfType<Button>().FirstOrDefault(b => (string)b.Tag == tag);
                string Text() => string.Join(" ", Walk(Body()).OfType<TextBlock>().Select(t => new TextRange(t.ContentStart, t.ContentEnd).Text));
                ContentControl Card(long id) => Walk(Body()).OfType<ContentControl>().FirstOrDefault(c => (string)c.Tag == "post:" + id);
                Check("tab: before the one-time click it explains (and that X-only notices won't show) and offers the button; nothing fetched",
                    Btn("load") != null && Btn("forum") != null && asked.Count == 0 && Text().Contains("only posts on X"));
                Btn("load").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                await Until(() => Card(6502386) != null);
                UpdateLayout();
                Check("tab: the click is remembered and fetches once; a card per Forever post with its topic, excerpt and 'Read on the forum'",
                    settings.BlizzardNewsLoad && asked.Count == 1 && Card(6502386) != null && Card(6502300) != null && Card(6502250) == null
                    && Text().Contains("WoW: Forever Hotfixes - November 11, 2026") && Walk(Card(6502386)).OfType<TextBlock>().SelectMany(t => t.Inlines.OfType<Hyperlink>()).Any(l => (string)l.Tag == h.Url), "load=" + settings.BlizzardNewsLoad + " asked=" + asked.Count);
                Check("tab: a hotfix post wears the Hotfixes pill; the first load marks nothing New and remembers the newest post as seen",
                    Walk(Card(6502386)).OfType<Border>().Any(b => (string)b.Tag == "Hotfixes") && !Walk(Body()).OfType<Border>().Any(b => (string)b.Tag == "New") && settings.BlizzardNewsSeen == 6502386, "seen=" + settings.BlizzardNewsSeen);
                Check("tab: EU forum picked by default, the alerts switch on", Walk(Body()).OfType<RadioButton>().Any(b => (string)b.Tag == "region:eu" && b.IsChecked == true) && Walk(Body()).OfType<CheckBox>().Any(c => (string)c.Tag == "alerts" && c.IsChecked == true));
                if (SelfTestClock.ShotsOn) { await ShotWait(300); Snapshot(Path.Combine(dir, "blizzardnews-tab.png")); }

                // ============================================================================ new posts
                SettingsPage.ShowAboutTab("about"); ShowTab("addons");
                await Task.Delay(100);
                feed = Feed(server, hotfix, refunds);
                await BlizzardNews.Refresh("eu", force: true);
                await Until(() => SettingsPage.BlizzardNoteForTest != null);
                var noteText = SettingsPage.BlizzardNoteForTest == null ? "" : string.Join(" | ", Walk(SettingsPage.BlizzardNoteForTest.CardForTest).OfType<TextBlock>().Select(t => t.Text));
                Check("new post: the tab gets its dot and the corner note names it, louder for server trouble",
                    SettingsPage.BlizzardDotForTest && noteText.Contains("Blizzard posted about the servers") && noteText.Contains("Forever: Login issues and server restarts") && noteText.Contains("loading screen"), noteText);
                Check("new post: noted once (remembered for a restart)", settings.BlizzardNewsNoted == 6502500 && settings.BlizzardNewsSeen == 6502386);
                if (SelfTestClock.ShotsOn) { await ShotWait(400); Snapshot(Path.Combine(dir, "blizzardnews-note.png")); }
                SettingsPage.BlizzardNoteForTest.DismissForTest();
                SettingsPage.CheckBlizzardUnseen();
                Check("new post: after Dismiss the note doesn't come back for the same post; the dot stays until the tab is opened", SettingsPage.BlizzardNoteForTest == null && SettingsPage.BlizzardDotForTest);
                settings.BlizzardNewsNoted = 6502386;   // as if it had not been noted
                SettingsPage.CheckBlizzardUnseen();
                await Until(() => SettingsPage.BlizzardNoteForTest != null);
                SettingsPage.BlizzardNoteForTest?.OpenForTest();
                await Until(() => Card(6502500) != null);
                UpdateLayout();
                Check("new post: 'Read it' opens Settings > About > Blizzard news with the post marked New and the Servers pill; seen now, dot gone, note closed",
                    Card(6502500) != null && Walk(Card(6502500)).OfType<Border>().Any(b => (string)b.Tag == "New") && Walk(Card(6502500)).OfType<Border>().Any(b => (string)b.Tag == "Servers")
                    && settings.BlizzardNewsSeen == 6502500 && !SettingsPage.BlizzardDotForTest && SettingsPage.BlizzardNoteForTest == null, "seen=" + settings.BlizzardNewsSeen + " dot=" + SettingsPage.BlizzardDotForTest);

                // turned off: the dot, no note
                SettingsPage.ShowAboutTab("about"); ShowTab("addons");
                settings.BlizzardNewsAlertsOff = true;
                feed = Feed(TrackerPost(6502600, 360, "WoW: Forever Hotfixes - November 14, 2026", "More fixes.", "2026-11-14T22:00:00.000Z"), server);
                await BlizzardNews.Refresh("eu", force: true);
                await Until(() => SettingsPage.BlizzardDotForTest);
                await Task.Delay(200);
                Check("alerts off: a new post shows the dot on the tab but no note", SettingsPage.BlizzardDotForTest && SettingsPage.BlizzardNoteForTest == null);
                settings.BlizzardNewsAlertsOff = false;

                // the US forum: its own feed, "seen" starts over
                ShowTab("settings"); SettingsPage.Show("about"); SettingsPage.ShowAboutTab("blizzard");
                await Task.Delay(100); UpdateLayout();
                asked.Clear();
                feed = Feed(TrackerPost(9100000, 352, "WoW: Forever Hotfixes - November 13, 2026", "US fixes.", "2026-11-14T01:54:00.000Z"));
                var us = Walk(Body()).OfType<RadioButton>().First(b => (string)b.Tag == "region:us");
                us.IsChecked = true;
                await Until(() => Card(9100000) != null);
                UpdateLayout();
                Check("US forum: the switch asks the US feed once, shows its posts (Bug Report category), says which forum; 'seen' restarted on that forum",
                    asked.Count == 1 && asked[0] == BlizzardNews.FeedUrl("us") && Card(9100000) != null && Card(6502600) == null && settings.BlizzardNewsRegion == "us" && settings.BlizzardNewsSeen == 9100000 && Text().Contains("US forum"),
                    string.Join(",", asked) + " seen=" + settings.BlizzardNewsSeen);

                SettingsPage.ShowAboutTab("about");
                Check("tab: back to About hides Blizzard news", await Until(() => SettingsPage.AboutPaneForTest.IsVisible && Card(9100000) != null && !Card(9100000).IsVisible));
            }
            catch (Exception e) { Check("no exception", false, e.ToString()); }
            finally
            {
                SettingsPage.CloseBlizzardNote();
                BlizzardNews.Get = savedGet; BlizzardNews.CacheFile = savedFile; BlizzardNews.ResetForTest();
                (settings.BlizzardNewsLoad, settings.BlizzardNewsRegion, settings.BlizzardNewsSeen, settings.BlizzardNewsNoted, settings.BlizzardNewsAlertsOff) = saved;
                SettingsPage.SetBlizzardDot(false);
            }
            return (ok ? "PASS" : "FAIL") + " blizzardnews\r\n" + string.Join("\r\n", notes);
        }
    }
}
