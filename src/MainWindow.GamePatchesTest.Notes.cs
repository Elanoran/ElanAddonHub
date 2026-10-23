using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // --selftest "gamepatches", part 2: Blizzard's notes (PatchNotes) against a local fixture of the forum's JSON - never the internet. The forum
    // HTML as plain blocks (headings, nested lists, entities; no images, quotes or scripts), staff posts only, a long topic read in pages, the
    // address rule (https, the forum's host, a topic or its posts, nothing else), no cookies / redirects, the saved copy, at most one fetch an
    // hour, a failed fetch keeping the saved notes, which post belongs to a build, and the card: nothing fetched before the one-time click.
    public partial class MainWindow
    {
        // a made-up topic in the forum's shape: two staff posts (one with a quote, an emoji, a link, a nested list, entities), one player post,
        // a staff post with nothing but a picture, and a stream naming two more posts the first answer does not carry
        const string NotesTopicJson = @"{""id"":4242,""title"":""Test Development Notes"",""post_stream"":{""posts"":[
 {""id"":1,""post_number"":1,""username"":""Kaivax-0001"",""user_title"":""Community Manager"",""staff"":true,""created_at"":""2026-10-01T22:00:00.000Z"",""updated_at"":""2026-10-02T01:00:00.000Z"",
  ""cooked"":""<p>Today, we updated the test realm with a new build.</p><h2><a name=\""change-log\"" class=\""anchor\"" href=\""#change-log\""></a>Change Log</h2><h3>Bug Fixes</h3><ul><li>Fixed rain &amp; fog <img src=\""https://example.invalid/emoji.png\"" class=\""emoji\"" alt=\"":smile:\""> visibility.</li><li>Shaman<ul><li>Totems last 10% longer.</li></ul></li></ul><aside class=\""quote\""><blockquote><p>a quoted player</p></blockquote></aside><script>alert(1)</script><p>See <a href=\""https://example.invalid/x\"">the guide</a> for more &#39;details&#39;.</p>""},
 {""id"":2,""post_number"":2,""username"":""Player-1234"",""staff"":false,""created_at"":""2026-10-02T10:00:00.000Z"",""updated_at"":""2026-10-02T10:00:00.000Z"",""cooked"":""<p>When is the next patch?</p>""},
 {""id"":3,""post_number"":3,""username"":""Kaivax-0001"",""user_title"":""Community Manager"",""staff"":true,""created_at"":""2026-10-03T10:00:00.000Z"",""updated_at"":""2026-10-03T10:00:00.000Z"",""cooked"":""<p><img src=\""https://example.invalid/pic.png\""></p>""}
],""stream"":[1,2,3,7,8]}}";
        const string NotesPostsJson = @"{""post_stream"":{""posts"":[
 {""id"":7,""post_number"":7,""username"":""Kaivax-0001"",""user_title"":""Community Manager"",""staff"":true,""created_at"":""2026-10-15T23:00:00.000Z"",""updated_at"":""2026-10-15T23:00:00.000Z"",""cooked"":""<p>We updated the test realm again.</p><ul><li>Racial abilities have been updated.</li></ul>""},
 {""id"":8,""post_number"":8,""username"":""Someone-9"",""staff"":false,""created_at"":""2026-10-16T08:00:00.000Z"",""updated_at"":""2026-10-16T08:00:00.000Z"",""cooked"":""<p>thanks</p>""}
]}}";

        async Task GamePatchesNotesTest(string work, Action<string, bool, string> check)
        {
            void Check(string what, bool cond, string detail = null) => check(what, cond, detail);
            var savedGet = PatchNotes.Get; var savedFile = PatchNotes.CacheFile; var savedTopics = PatchNotes.Topics.ToArray(); var savedLoad = settings.PatchNotesLoad;
            var asked = new List<string>();
            bool fail = false;
            try
            {
                PatchNotes.CacheFile = Path.Combine(work, "patchnotes.json"); PatchNotes.ResetForTest();
                PatchNotes.Topics[0] = new PatchNotes.Topic { Id = 4242, Title = "Test Development Notes" };
                PatchNotes.Get = (url, ct) =>
                {
                    lock (asked) asked.Add(url);
                    if (fail) throw new HttpRequestException("the forum answered 503");
                    if (!PatchNotes.UrlAllowed(url)) throw new InvalidOperationException("refused " + url);
                    return Task.FromResult(url.Contains("/posts.json") ? NotesPostsJson : NotesTopicJson);
                };

                // ---- the HTML as text
                var bl = PatchNotes.Blocks(@"<p>Hello &amp; welcome</p><h2>Change Log</h2><ul><li>One <img src=""x.png""></li><li>Two<ul><li>Deep</li></ul></li></ul><blockquote>quoted</blockquote><script>evil()</script><p>A <a href=""https://example.invalid"">link</a> &#39;here&#39;</p>");
                var shape = string.Join(" | ", bl.Select(b => b.Kind.ToString() + b.Level + ":" + b.Text));
                Check("notes text: paragraphs, headings and nested list items become plain blocks; entities decoded; images, quotes and scripts gone; links keep their words",
                    shape == "Paragraph0:Hello & welcome | Heading0:Change Log | Bullet0:One | Bullet0:Two | Bullet1:Deep | Paragraph0:A link 'here'", shape);

                // ---- the address rule and the connection
                Check("notes address: a topic and its posts on the forum's host over https are allowed",
                    PatchNotes.UrlAllowed(PatchNotes.TopicUrl(4242)) && PatchNotes.UrlAllowed(PatchNotes.PostsUrl(4242, new[] { 7, 8 })) && PatchNotes.TopicUrl(4242) == "https://us.forums.blizzard.com/en/wow/t/4242.json");
                Check("notes address: anything else is refused (http, another host, another page, an extra query, a login in the address, another port)",
                    new[] { "http://us.forums.blizzard.com/en/wow/t/1.json", "https://evil.example/en/wow/t/1.json", "https://us.forums.blizzard.com.evil.example/en/wow/t/1.json", "https://us.forums.blizzard.com/session/current.json",
                            "https://us.forums.blizzard.com/en/wow/t/1.json?user=me", "https://me:pw@us.forums.blizzard.com/en/wow/t/1.json", "https://us.forums.blizzard.com:8443/en/wow/t/1.json" }.All(u => !PatchNotes.UrlAllowed(u)));
                Check("notes connection: no cookies, no redirects, no Windows login sent", !PatchNotes.Handler.UseCookies && !PatchNotes.Handler.AllowAutoRedirect && !PatchNotes.Handler.UseDefaultCredentials);

                // ---- a fetch
                var r = await PatchNotes.Refresh();
                Check("notes fetch: the topic, then its missing posts in one page (2 requests, both allowed addresses with no user data)",
                    asked.Count == 2 && asked[0] == PatchNotes.TopicUrl(4242) && asked[1] == PatchNotes.PostsUrl(4242, new[] { 7, 8 }) && r.Problem == null, string.Join(", ", asked));
                Check("notes fetch: staff posts only, newest first; a post with nothing but a picture is left out; names without the forum's number",
                    r.Posts.Select(p => p.Number).SequenceEqual(new[] { 7, 1 }) && r.Posts.All(p => p.Author == "Kaivax" && p.Title == "Community Manager"), string.Join(",", r.Posts.Select(p => p.Number + " " + p.Author)));
                var p1 = r.Posts.First(p => p.Number == 1);
                Check("notes fetch: the post's text as blocks (its heading, its sub-heading, the nested Shaman item), no quote, no script",
                    p1.Blocks.Any(b => b.Kind == PatchNotes.BlockKind.Heading && b.Text == "Change Log") && p1.Blocks.Any(b => b.Kind == PatchNotes.BlockKind.Bullet && b.Level == 1 && b.Text == "Totems last 10% longer.")
                    && p1.Blocks.Any(b => b.Text == "Fixed rain & fog visibility.") && !p1.Blocks.Any(b => b.Text.Contains("quoted") || b.Text.Contains("alert")), string.Join(" | ", p1.Blocks.Select(b => b.Text)));
                var file = File.ReadAllText(PatchNotes.CacheFile);
                Check("notes saved: the text is kept on disk, without image or link addresses", file.Contains("Totems last 10% longer.") && !file.Contains("example.invalid") && !file.Contains("<"));
                await PatchNotes.Refresh();
                Check("notes: fresh notes are not fetched again (at most once an hour)", asked.Count == 2);
                PatchNotes.ResetForTest();
                var again = PatchNotes.Saved();
                Check("notes saved: read back after a restart (posts, blocks, dates)", again.Posts.Count == 2 && again.Posts[1].Blocks.Count == p1.Blocks.Count && again.Posts[0].Posted == r.Posts[0].Posted && !PatchNotes.Stale(again));
                fail = true;
                var bad = await PatchNotes.Refresh(force: true);
                Check("notes: the forum unreachable = a plain problem line, the saved notes stay", bad.Problem != null && bad.Problem.Contains("503") && bad.Posts.Count == 2, bad.Problem);
                fail = false;

                // ---- which post belongs to a build (local days)
                string Day(int m, int d) => new DateTime(2026, m, d).ToString("yyyy-MM-dd");
                Check("notes for a build: the newest post out by the day after the build was first seen; none when it came before any post",
                    PatchNotes.NewestBy(r, Day(10, 23))?.Number == 7 && PatchNotes.NewestBy(r, Day(10, 9))?.Number == 1 && PatchNotes.NewestBy(r, Day(9, 20)) == null);

                // ---- the card: nothing is fetched until the click
                File.Delete(PatchNotes.CacheFile); PatchNotes.ResetForTest(); asked.Clear();
                settings.PatchNotesLoad = false;
                GameBuilds.Seen("wow_classic_beta|1.60.1.70532|ad1802f4455e427ed8b7769aab25b0c0");
                SettingsPage.CurrentGameBuild = () => "wow_classic_beta|1.60.1.70532|ad1802f4455e427ed8b7769aab25b0c0";
                ShowTab("settings"); SettingsPage.Show("about"); SettingsPage.ShowAboutTab("patches"); SettingsPage.PickPatchForTest(null);
                await Task.Delay(200); UpdateLayout();
                ContentControl Notes() => SettingsPage.PatchBodyForTest.Children.OfType<ContentControl>().FirstOrDefault(c => (string)c.Tag == "notes");
                Button Btn(string tag) => Walk(Notes()).OfType<Button>().FirstOrDefault(b => (string)b.Tag == tag);
                var load = Btn("load");
                Check("notes card: before the one-time click it explains and offers 'Load Blizzard's notes' and 'Open on the forum'; nothing was fetched",
                    load != null && asked.Count == 0 && Walk(Notes()).OfType<Button>().Any(b => b.Content as string == "Open on the forum"));
                load.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                await Until(() => Notes() != null && Walk(Notes()).OfType<StackPanel>().Any(s => (string)s.Tag == "post:7"));
                UpdateLayout();
                var cardText = string.Join(" ", Walk(Notes()).OfType<TextBlock>().Select(t => new TextRange(t.ContentStart, t.ContentEnd).Text));
                Check("notes card: the click is remembered and fetches once; the newest post shows in full, labelled, the older one folded with 'Show the notes'",
                    settings.PatchNotesLoad && asked.Count == 2 && cardText.Contains("Racial abilities have been updated.") && cardText.Contains("The newest notes when the Outpost first saw this build")
                    && Walk(Notes()).OfType<TextBlock>().SelectMany(t => t.Inlines.OfType<Hyperlink>()).Any(h => h.Inlines.OfType<Run>().Any(x => x.Text == "Show the notes")) && !cardText.Contains("Totems last 10% longer."), "load=" + settings.PatchNotesLoad + " asked=" + asked.Count + " totems=" + cardText.Contains("Totems"));
                var show = Walk(Notes()).OfType<TextBlock>().SelectMany(t => t.Inlines.OfType<Hyperlink>()).First();
                show.DoClick(); UpdateLayout();
                cardText = string.Join(" ", Walk(Notes()).OfType<TextBlock>().Select(t => new TextRange(t.ContentStart, t.ContentEnd).Text));
                Check("notes card: 'Show the notes' unfolds the older post (its nested list item)", cardText.Contains("Totems last 10% longer.") && Walk(Notes()).OfType<Button>().Any(b => b.Content as string == "Fold"));
                if (SelfTestClock.ShotsOn) { await ShotWait(300); Snapshot(Path.Combine(Path.GetDirectoryName(work), "gamepatches-notes.png")); }
                fail = true;
                Btn("refresh").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                await Until(() => Notes() != null && Walk(Notes()).OfType<Border>().Any(b => (string)b.Tag == "problem"));
                Check("notes card: Refresh with the forum down shows the problem and keeps the notes", Walk(Notes()).OfType<Border>().Any(b => (string)b.Tag == "problem") && Walk(Notes()).OfType<StackPanel>().Any(s => (string)s.Tag == "post:7"));
                fail = false;
            }
            finally
            {
                PatchNotes.Get = savedGet; PatchNotes.CacheFile = savedFile; for (int i = 0; i < savedTopics.Length; i++) PatchNotes.Topics[i] = savedTopics[i];
                PatchNotes.ResetForTest(); settings.PatchNotesLoad = savedLoad;
            }
        }
    }
}
