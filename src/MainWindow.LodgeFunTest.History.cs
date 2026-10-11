using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // "lodgefun", part 4: older messages (paging with the view kept still, jumps that load what they need) and the search panel, against a
    // synthetic server that answers history:before and search from a list of 260 messages
    public partial class MainWindow
    {
        sealed class FunServer
        {
            public readonly List<(string id, string from, int fromId, long at, string text)> All = new List<(string, string, int, long, string)>();
            public readonly List<string> Requests = new List<string>();   // "before:<id>" / "search:<q>"
            public readonly List<Dictionary<string, object>> Sent = new List<Dictionary<string, object>>();
            public bool Silent, RateOnce, SearchRate;
            public int Limit = 100;
            readonly MainWindow w;
            public FunServer(MainWindow w, int n, long now, Func<int, string> text = null)
            {
                this.w = w;
                for (int i = 1; i <= n; i++)
                {
                    // groups of three from the same person: the boundary between pages falls inside a group
                    bool bob = ((i - 1) / 3) % 2 == 0;
                    All.Add(("m" + i.ToString("D4"), bob ? "Bob" : "Tess", bob ? 2 : 3, now - (n + 1 - i) * 60000L,
                        text != null ? text(i) : i % 25 == 0 ? "Deadmines run number " + i : "chatter number " + i));
                }
            }
            public string Json(int i) => "{\"t\":\"msg\",\"channel\":\"general\",\"id\":" + Q(All[i].id) + ",\"at\":" + All[i].at + ",\"from\":" + Q(All[i].from) + ",\"fromId\":" + All[i].fromId + ",\"text\":" + Q(All[i].text) + "}";
            public string Window(int from, int to) => string.Join(",", Enumerable.Range(from, to - from).Select(Json));
            public void Handle(Dictionary<string, object> o)
            {
                Sent.Add(o);
                var t = o.Str("t");
                if (t == "history:before")
                {
                    Requests.Add("before:" + o.Str("before"));
                    if (Silent) return;
                    if (RateOnce) { RateOnce = false; w.Session.FeedForTest("{\"t\":\"error\",\"text\":\"You are loading history too fast - try again in a moment\",\"code\":\"rate\"}"); return; }
                    int end = All.FindIndex(x => x.id == o.Str("before"));
                    if (end < 0) end = 0;
                    int start = Math.Max(0, end - Limit);
                    w.Session.FeedForTest("{\"t\":\"history:page\",\"channel\":" + Q(o.Str("channel")) + ",\"before\":" + Q(o.Str("before")) + ",\"messages\":[" + Window(start, end) + "],\"hasMore\":" + (start > 0 ? "true" : "false") + "}");
                }
                else if (t == "search")
                {
                    var q = o.Str("q");
                    Requests.Add("search:" + q);
                    if (Silent) return;
                    if (SearchRate) { w.Session.FeedForTest("{\"t\":\"error\",\"text\":\"You can search a few times a minute - try again in a moment\",\"code\":\"rate\"}"); return; }
                    var hits = All.Where(x => x.text.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).OrderByDescending(x => x.at).Take(50).ToList();
                    var json = hits.Select(h => "{\"channel\":\"general\",\"id\":" + Q(h.id) + ",\"from\":" + Q(h.from) + ",\"at\":" + h.at + ",\"snippet\":" + Q(h.text) + "}").ToList();
                    // plus one hit in the loot channel, newer than the 3rd general hit: groups follow their newest hit
                    if (q.StartsWith("dead", StringComparison.OrdinalIgnoreCase) && hits.Count >= 3)
                        json.Insert(2, "{\"channel\":\"loot\",\"id\":\"L1\",\"from\":\"Tess\",\"at\":" + (hits[2].at + 1000) + ",\"snippet\":\"selling a Deadmines recipe\"}");
                    w.Session.FeedForTest("{\"t\":\"search:result\",\"q\":" + Q(q) + ",\"hits\":[" + string.Join(",", json) + "],\"more\":" + (All.Count(x => x.text.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) > 50 ? "true" : "false") + "}");
                }
            }
        }

        string FunHistoryWelcome(FunServer srv, int last, string lootJson = "", string features = null, string server = "2.9.0")
        {
            var json = features == null ? FunWelcome("owner", FunPeople(false), srv.Window(srv.All.Count - last, srv.All.Count)) : FunWelcome("owner", FunPeople(false), srv.Window(srv.All.Count - last, srv.All.Count), features, server);
            return json.Replace("\"loot\":[]}", "\"loot\":[" + lootJson + "]}");
        }

        async Task FunHistory(string dir, Action<string, bool> check)
        {
            var hadRetry = Session.PageRetryDelay; var hadTimeout = Session.RequestTimeout; var hadShown = Session.IsShownToUser;
            try
            {
                Session.IsShownToUser = () => false;
                Session.PageRetryDelay = TimeSpan.FromMilliseconds(30);
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var srv = new FunServer(this, 260, now);
                Session.SentForTest = srv.Handle;
                var general = new Func<ChannelVM>(() => Session.TextChannels.First(c => c.Id == "general"));

                // ---- what the welcome says decides whether there is more
                Session.FeedForTest(FunHistoryWelcome(srv, 40));
                Session.Select(general());
                await Task.Delay(300);
                check("paging: a welcome with fewer than 100 messages has everything: nothing to load, the note says it is the start", !Session.CanLoadOlder("general") && !Session.HasMoreOlder("general")
                    && LodgePage.OlderNoteForTest == "This is the start of #General");
                Session.Disconnect(); Session.SentForTest = srv.Handle; LodgePage.ShowChatForTest();
                Session.FeedForTest(FunHistoryWelcome(srv, 100));
                Session.Select(general());
                await Task.Delay(500);
                check("paging: a full welcome page may have older messages: a quiet hint at the top", Session.SupportsPaging && Session.HasMoreOlder("general") && Session.CanLoadOlder("general") && LodgePage.OlderNoteForTest == "Scroll up for earlier messages");
                check("paging: the messages shown are the newest 100", Session.MessagesOf("general").Count == 100 && Session.MessagesOf("general")[0].Id == "m0161" && Session.MessagesOf("general")[99].Id == "m0260");

                // ---- scrolling to the top loads the page; the view stays where it was
                await Task.Delay(300);
                LodgePage.AutoLoadOlder = false;      // (the scroll below must not start the load by itself: this part measures the anchoring)
                LodgePage.ScrollToVerticalForTest(0);
                var anchor0 = LodgePage.FirstVisibleForTest(out var y0);
                srv.Requests.Clear();
                await LodgePage.LoadOlderForTest();
                var y1 = LodgePage.OffsetOfForTest(anchor0);
                var l1 = Session.MessagesOf("general");
                check("paging: scrolling to the top asks for the page before the oldest message (with its id and time) [" + string.Join(",", srv.Requests) + "]", srv.Requests.SequenceEqual(new[] { "before:m0161" }) && srv.Sent.Last().Long("beforeAt") == srv.All[160].at);
                check("paging: 100 older messages arrive in front, oldest first, none twice [" + l1.Count + " " + (l1.Count > 0 ? l1[0].Id : "") + "]", l1.Count == 200 && l1[0].Id == "m0061" && l1[99].Id == "m0160" && l1[100].Id == "m0161" && l1.Select(m => m.Id).Distinct().Count() == 200
                    && l1.Zip(l1.Skip(1), (a, b) => a.At <= b.At).All(x => x));
                check("paging: the message that was at the top of the view is still exactly there (anchored, " + y0.ToString("0.0") + " -> " + y1.ToString("0.0") + ")", anchor0 != null && Math.Abs(y0 - y1) < 2 && LodgePage.ScrollOffsetForTest > 100);
                check("paging: the boundary between pages keeps its grouping (same person, within minutes: no second header)", l1[100].Continuation && l1[100].HeaderVisibility == Visibility.Collapsed && !l1[99].Continuation && !l1[0].Continuation);
                check("paging: the hint says there is more [" + LodgePage.OlderNoteForTest + "]", LodgePage.OlderNoteForTest == "Scroll up for earlier messages" && Session.HasMoreOlder("general"));

                // the same thing from the scroll position itself: somewhere down the list, then up to the top
                LodgePage.AutoLoadOlder = true;
                LodgePage.ScrollToVerticalForTest(600);
                var mid0 = LodgePage.FirstVisibleForTest(out var my0);
                srv.Requests.Clear();
                LodgePage.ScrollToVerticalForTest(0);        // ScrollChanged with a negative change at the top: the page is requested
                await Task.Delay(500);
                check("paging: reaching the top by scrolling loads the next page by itself [" + srv.Requests.Count + " " + Session.MessagesOf("general").Count + " " + LodgePage.ScrollOffsetForTest + "]", srv.Requests.Count == 1 && Session.MessagesOf("general").Count == 260 && !Session.HasMoreOlder("general"));
                check("paging: the last page says there is no more: the note names the start, no further request", LodgePage.OlderNoteForTest == "This is the start of #General" && !Session.CanLoadOlder("general"));
                srv.Requests.Clear();
                await LodgePage.LoadOlderForTest(); LodgePage.FireWheelUpForTest(); await Task.Delay(200);
                check("paging: at the start of the history nothing more is asked", srv.Requests.Count == 0 && Session.MessagesOf("general")[0].Id == "m0001");

                // a page that repeats known messages adds nothing; the same welcome again keeps what was loaded
                Session.FeedForTest("{\"t\":\"history:page\",\"channel\":\"general\",\"before\":\"m0100\",\"messages\":[" + srv.Window(10, 20) + "],\"hasMore\":false}");
                check("paging: a page of messages already shown adds nothing", Session.MessagesOf("general").Count == 260);
                Session.FeedForTest(FunHistoryWelcome(srv, 100));
                await Task.Delay(300);
                check("paging: a reconnect (second welcome) keeps the older messages that were loaded", Session.MessagesOf("general").Count == 260 && !Session.HasMoreOlder("general") && Session.MessagesOf("general")[0].Id == "m0001");

                // ---- jumps load what they need
                // (a reply to a message that is not loaded)
                Session.Disconnect();
                Session.SentForTest = srv.Handle;
                LodgePage.ShowChatForTest();
                Session.FeedForTest(FunHistoryWelcome(srv, 100));
                Session.Select(general());
                await Task.Delay(400);
                srv.Requests.Clear();
                var found = await LodgePage.JumpToAsync("general", "m0005");
                await Task.Delay(250);
                var target = Session.MessagesOf("general").FirstOrDefault(m => m.Id == "m0005");
                check("jump: a reply to an old message loads older pages until it is there, then scrolls to it and flashes it (" + string.Join(",", srv.Requests) + ")", found && target != null && target.Flash
                    && srv.Requests.SequenceEqual(new[] { "before:m0161", "before:m0061" }));
                check("jump: the 'loading' note is gone afterwards", LodgePage.ChatNoteTextForTest == "");
                // (a pinned message from long ago, found by time)
                Session.Disconnect(); Session.SentForTest = srv.Handle; LodgePage.ShowChatForTest();
                Session.FeedForTest(FunHistoryWelcome(srv, 100)); Session.Select(general());
                await Task.Delay(300);
                srv.Requests.Clear();
                found = await LodgePage.JumpToAsync("general", "m0003", srv.All[2].at);
                await Task.Delay(250);
                check("jump: a pinned message from long ago is found too", found && Session.MessagesOf("general").Any(m => m.Id == "m0003" && m.Flash));
                // (a message that is gone: stops as soon as it is clear, says so)
                Session.Disconnect(); Session.SentForTest = srv.Handle; LodgePage.ShowChatForTest();
                Session.FeedForTest(FunHistoryWelcome(srv, 100)); Session.Select(general());
                await Task.Delay(300);
                srv.Requests.Clear();
                found = await LodgePage.JumpToAsync("general", "gone-id", srv.All[120].at);
                check("jump: a deleted message with a known time stops after the page that passes it, and says so", !found && srv.Requests.Count == 1 && LodgePage.ChatNoteTextForTest == "That message is no longer in the history");
                srv.Requests.Clear();
                found = await LodgePage.JumpToAsync("general", "gone-id");
                check("jump: without a time it looks through everything the server has (the second page is the last) and says so", !found && srv.Requests.Count == 1 && Session.MessagesOf("general").Count == 260 && LodgePage.ChatNoteTextForTest == "That message is no longer in the history");
                // (slow down, then it works)
                Session.Disconnect(); Session.SentForTest = srv.Handle; LodgePage.ShowChatForTest();
                Session.FeedForTest(FunHistoryWelcome(srv, 100)); Session.Select(general());
                await Task.Delay(300);
                srv.Requests.Clear(); srv.RateOnce = true;
                found = await LodgePage.JumpToAsync("general", "m0100");
                check("jump: when the server says 'slow down' the jump waits and asks again", found && srv.Requests.Count == 2 && srv.Requests[0] == srv.Requests[1]);
                // (no answer)
                Session.Disconnect(); Session.SentForTest = srv.Handle; LodgePage.ShowChatForTest();
                Session.FeedForTest(FunHistoryWelcome(srv, 100)); Session.Select(general());
                await Task.Delay(300);
                Session.RequestTimeout = TimeSpan.FromMilliseconds(150); srv.Silent = true;
                found = await LodgePage.JumpToAsync("general", "m0100");
                check("jump: a server that doesn't answer ends the jump with a note, nothing hangs", !found && LodgePage.ChatNoteTextForTest.Length > 0 && !Session.IsLoadingOlder("general"));
                srv.Silent = false; Session.RequestTimeout = hadTimeout;
                // (two asks at once share one request)
                srv.Requests.Clear(); srv.Silent = true;
                var t1 = Session.LoadOlder("general"); var t2 = Session.LoadOlder("general");
                bool oneRequest = srv.Requests.Count == 1 && Session.IsLoadingOlder("general");
                Session.FeedForTest("{\"t\":\"history:page\",\"channel\":\"general\",\"before\":\"m0161\",\"messages\":[" + srv.Window(60, 160) + "],\"hasMore\":true}");
                await Task.WhenAll(t1, t2);
                srv.Silent = false;
                check("paging: two loads at the same time share one request and one answer", oneRequest && t1.Result.Added == 100 && t2.Result.Added == 100 && !Session.IsLoadingOlder("general"));

                // ---- an older server: nothing is asked, the old wording stays
                Session.Disconnect(); Session.SentForTest = srv.Handle; LodgePage.ShowChatForTest();
                Session.FeedForTest(FunHistoryWelcome(srv, 100, "", "\"reply\",\"react\",\"pin\",\"profile\",\"displayname\"", "2.8.0"));
                Session.Select(general());
                await Task.Delay(300);
                srv.Requests.Clear();
                found = await LodgePage.JumpToAsync("general", "m0005");
                await LodgePage.LoadOlderForTest();
                check("2.8 server: no paging - a jump to an old message says so, nothing is requested, no hint at the top", !found && srv.Requests.Count == 0 && LodgePage.ChatNoteTextForTest == "That message is older than what is loaded here"
                    && !Session.SupportsPaging && !Session.CanLoadOlder("general") && LodgePage.OlderNoteForTest == "");

                await FunSearch(dir, check, srv, general);
            }
            finally { Session.SentForTest = null; Session.PageRetryDelay = hadRetry; Session.RequestTimeout = hadTimeout; Session.IsShownToUser = hadShown; }
        }

        async Task FunSearch(string dir, Action<string, bool> check, FunServer srv, Func<ChannelVM> general)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string Loot(string id, string text, int minsAgo) => "{\"t\":\"msg\",\"channel\":\"loot\",\"id\":" + Q(id) + ",\"at\":" + (now - minsAgo * 60000L) + ",\"from\":\"Tess\",\"fromId\":3,\"text\":" + Q(text) + "}";
            Session.Disconnect(); Session.SentForTest = srv.Handle; LodgePage.ShowChatForTest();
            Session.FeedForTest(FunHistoryWelcome(srv, 100, Loot("L1", "selling a Deadmines recipe", 3)));
            Session.Select(general());
            await Task.Delay(400);

            check("search: a 2.9 server shows the magnifier in the channel header", Session.SupportsSearch && LodgePage.SearchButtonVisibleForTest);
            check("search: the box takes at most 64 characters", LodgePage.SearchBoxMaxLengthForTest == 64);
            LodgePage.OpenSearch();
            await Task.Delay(300);
            check("search: the panel opens", LodgePage.SearchOpenForTest);
            srv.Requests.Clear();
            LodgePage.SearchBoxTextForTest = "d";
            await LodgePage.RunSearchForTest();
            check("search: one character is too short - nothing is sent", srv.Requests.Count == 0 && LodgePage.SearchHintForTest == "Type at least 2 characters");
            LodgePage.SearchBoxTextForTest = "  dead ";
            await LodgePage.RunSearchForTest();
            var groups = LodgePage.SearchGroupsForTest;
            check("search: the query is sent trimmed", srv.Requests.SequenceEqual(new[] { "search:dead" }));
            check("search: results are grouped by channel, in the order of their newest hit, with counts", groups.Select(g => g.Title + ":" + g.Count).SequenceEqual(new[] { "#General:10", "#Loot:1" }));
            var first = groups.First(g => g.ChannelId == "general").Hits[0];
            check("search: a hit shows author, time and the snippet with the match picked out", first.Id == "m0250" && first.FromShown == "Tess" && first.Match == "Dead" && first.Before == "" && first.After == "mines run number 250" && first.Time.Length > 0);
            check("search: the hint counts the results", LodgePage.SearchHintForTest == "11 results");

            // clicking a hit that is already loaded; one in another channel; one that is older than what is loaded
            var hitLoaded = first;
            srv.Requests.Clear();
            var okLoaded = await LodgePage.ClickHitForTest(hitLoaded);
            await Task.Delay(250);
            check("search: clicking a hit jumps to it and flashes it", okLoaded && Session.MessagesOf("general").First(m => m.Id == "m0250").Flash && srv.Requests.Count == 0);
            var lootHit = groups.First(g => g.ChannelId == "loot").Hits[0];
            var okLoot = await LodgePage.ClickHitForTest(lootHit);
            await Task.Delay(250);
            check("search: a hit in another channel switches to that channel and finds the message", okLoot && Session.Selected.Id == "loot" && Session.MessagesOf("loot").First(m => m.Id == "L1").Flash);
            Session.Select(general());
            var old = groups.First(g => g.ChannelId == "general").Hits.Last();   // m0025: older than the 100 loaded
            srv.Requests.Clear();
            var okOld = await LodgePage.ClickHitForTest(old);
            await Task.Delay(250);
            check("search: a hit older than what is loaded loads history until it is there (" + string.Join(",", srv.Requests) + ")", okOld && old.Id == "m0025" && srv.Requests.Count == 2 && Session.MessagesOf("general").Any(m => m.Id == "m0025" && m.Flash));

            // other answers
            LodgePage.SearchBoxTextForTest = "nothing like this";
            await LodgePage.RunSearchForTest();
            check("search: nothing found says so", LodgePage.SearchHintForTest.StartsWith("Nothing found") && LodgePage.SearchGroupsForTest.Count == 0);
            LodgePage.SearchBoxTextForTest = "chatter";
            await LodgePage.RunSearchForTest();
            check("search: more than the server returns says the newest are shown", LodgePage.SearchGroupsForTest.Sum(g => g.Count) == 50 && LodgePage.SearchHintForTest.Contains("newest"));
            srv.SearchRate = true;
            LodgePage.SearchBoxTextForTest = "dead";
            await LodgePage.RunSearchForTest();
            check("search: 'too many searches' is said calmly and the old results stay", LodgePage.SearchHintForTest.Contains("try again") && LodgePage.SearchGroupsForTest.Sum(g => g.Count) == 50);
            srv.SearchRate = false;
            srv.Silent = true; Session.RequestTimeout = TimeSpan.FromMilliseconds(150);
            await LodgePage.RunSearchForTest();
            check("search: a server that doesn't answer is said, not waited on forever", LodgePage.SearchHintForTest.Contains("didn't answer"));
            srv.Silent = false; Session.RequestTimeout = TimeSpan.FromSeconds(10);
            LodgePage.CloseSearchForTest();

            // the card of the panel, drawn
            LodgePage.SearchBoxTextForTest = "dead";
            await LodgePage.RunSearchForTest();
            LodgePage.OpenSearch();
            await Task.Delay(400);
            var texts = VisualsOf<TextBlock>(LodgePage.SearchCardForTest).Select(t => !string.IsNullOrEmpty(t.Text) ? t.Text : string.Concat(t.Inlines.OfType<System.Windows.Documents.Run>().Select(r => r.Text))).Where(t => !string.IsNullOrEmpty(t)).ToList();
            check("search: the panel draws the channel headers (quiet), counts and the hit rows [" + string.Join("/", texts.Take(12)) + "]", texts.Contains("#General") && texts.Contains("#Loot") && texts.Contains("11 results") && texts.Any(t => t.Contains("mines run number")));
            LodgePage.CloseSearchForTest();

            // ---- older servers: no search at all
            Session.Disconnect(); Session.SentForTest = srv.Handle; LodgePage.ShowChatForTest();
            Session.FeedForTest(FunHistoryWelcome(srv, 100, "", "\"reply\",\"react\",\"pin\",\"profile\",\"displayname\"", "2.8.0"));
            Session.Select(general());
            await Task.Delay(300);
            srv.Requests.Clear();
            LodgePage.OpenSearch();
            var res = await Session.SearchAsync("dead");
            check("2.8 server: the magnifier is hidden, the panel does not open, no search is ever sent", !Session.SupportsSearch && !LodgePage.SearchButtonVisibleForTest && !LodgePage.SearchOpenForTest && res.Failed && srv.Requests.Count == 0);
            Session.FeedForTest(FunHistoryWelcome(srv, 100, "", "\"reply\",\"react\",\"pin\",\"profile\",\"displayname\",\"search\"", "2.8.0"));
            check("2.8 server that claimed the feature anyway: the version rule wins, search stays hidden", !Session.SupportsSearch && !LodgePage.SearchButtonVisibleForTest);
            Session.FeedForTest(FunHistoryWelcome(srv, 100));
            check("a 2.9 server again: search is back", Session.SupportsSearch && LodgePage.SearchButtonVisibleForTest);
        }
    }
}
