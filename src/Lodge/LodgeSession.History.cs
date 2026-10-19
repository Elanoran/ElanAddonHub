using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using ElansAddonHub.Services;
using Elans.Shared;

namespace ElansAddonHub.Lodge
{
    public sealed class PageResult
    {
        public int Added;
        public bool HasMore;
        public bool Rate;        // the server asked me to slow down
        public bool Failed;      // no answer / not supported
    }

    // one search hit; the snippet is split around the match so the panel can highlight it
    public class SearchHitVM
    {
        public string ChannelId { get; set; }
        public string ChannelName { get; set; }
        public string Id { get; set; }
        public string From { get; set; }
        public long AtMs { get; set; }
        public string Snippet { get; set; }
        public string Query { get; set; }
        public string FromShown => DisplayNames.Shown(From);
        public Brush FromBrush => Avatar.ColorFor(From);
        public DateTime At => DateTimeOffset.FromUnixTimeMilliseconds(AtMs).UtcDateTime;
        public string Time
        {
            get
            {
                var local = At.ToLocalTime();
                if (local.Date == DateTime.Today) return local.ToString("HH:mm");
                if (local.Date == DateTime.Today.AddDays(-1)) return "Yesterday " + local.ToString("HH:mm");
                return local.ToString("d MMM yyyy HH:mm", CalendarRules.English);
            }
        }
        int MatchAt => string.IsNullOrEmpty(Query) || Snippet == null ? -1 : Snippet.IndexOf(Query, StringComparison.OrdinalIgnoreCase);
        public string Before => MatchAt < 0 ? Snippet : Snippet.Substring(0, MatchAt);
        public string Match => MatchAt < 0 ? "" : Snippet.Substring(MatchAt, Query.Length);
        public string After => MatchAt < 0 ? "" : Snippet.Substring(MatchAt + Query.Length);
    }

    public class SearchGroupVM
    {
        public string ChannelId { get; set; }
        public string Title { get; set; }
        public int Count => Hits.Count;
        public List<SearchHitVM> Hits { get; } = new List<SearchHitVM>();
    }

    public sealed class SearchResults
    {
        public string Query;
        public List<SearchHitVM> Hits = new List<SearchHitVM>();
        public bool More;          // there were more than the server returns
        public bool Rate;          // too many searches this minute
        public bool Failed;        // no answer / not supported
        // grouped by channel, groups in the order of their newest hit (hits arrive newest first)
        public List<SearchGroupVM> Groups
        {
            get
            {
                var g = new List<SearchGroupVM>();
                foreach (var h in Hits)
                {
                    var grp = g.FirstOrDefault(x => x.ChannelId == h.ChannelId);
                    if (grp == null) g.Add(grp = new SearchGroupVM { ChannelId = h.ChannelId, Title = "#" + (h.ChannelName ?? h.ChannelId) });
                    grp.Hits.Add(h);
                }
                return g;
            }
        }
    }

    // Older messages and search (Lodge server 2.9). A server without them simply never advertises the features: nothing is asked, and the
    // views fall back (no search button, "older than what is loaded").
    public partial class LodgeSession
    {
        public const int WelcomeHistory = 100;                          // how many messages per channel the server sends in the welcome
        public bool SupportsPaging { get; private set; }                 // "history2"
        public bool SupportsSearch { get; private set; }                 // "search" and a 2.9+ server
        public TimeSpan PageRetryDelay = TimeSpan.FromSeconds(1.2);      // after "slow down" while loading towards a message (tests shorten it)
        public TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

        readonly Dictionary<string, bool> hasMoreOlder = new Dictionary<string, bool>();
        readonly Dictionary<string, TaskCompletionSource<PageResult>> pagePending = new Dictionary<string, TaskCompletionSource<PageResult>>();
        TaskCompletionSource<SearchResults> searchPending;
        string searchQuery;

        // the older page of a channel arrived: (channel, the messages that were added at the top, more older ones exist)
        public event Action<string, List<MessageVM>, bool> OlderLoaded;
        // a request for an older page is about to go out (the view notes where the list is, to keep it still when the page lands)
        public event Action<string> OlderRequested;

        void ReadHistoryFeatures(List<string> features)
        {
            SupportsPaging = features.Contains("history2");
            var v = ParseVersion(ServerVersion);
            SupportsSearch = features.Contains("search") && (v == null || v >= new Version(2, 9, 0));
        }

        // after the welcome: a channel with a full welcome page may have older messages; one with fewer has everything the server keeps
        void NoteWelcomeHistory(string channel, int received)
        {
            if (!SupportsPaging) { hasMoreOlder[channel] = false; return; }
            if (hasMoreOlder.TryGetValue(channel, out var known) && !known) return;   // already reached the start (the window only slides on)
            hasMoreOlder[channel] = received >= WelcomeHistory || (hasMoreOlder.TryGetValue(channel, out var had) && had);
        }

        void ResetHistoryState()
        {
            hasMoreOlder.Clear();
            foreach (var p in pagePending.Values.ToList()) p.TrySetResult(new PageResult { Failed = true });
            pagePending.Clear();
            searchPending?.TrySetResult(new SearchResults { Failed = true });
            searchPending = null;
        }

        public bool HasMoreOlder(string channel) => SupportsPaging && channel != null && hasMoreOlder.TryGetValue(channel, out var m) && m;
        public bool IsLoadingOlder(string channel) => channel != null && pagePending.ContainsKey(channel);
        public bool CanLoadOlder(string channel) => HasMoreOlder(channel) && !IsLoadingOlder(channel) && (Online || TestFed) && MessagesOf(channel).Count > 0;

        // asks for the page of messages older than the oldest one shown; one request per channel at a time (a second caller shares the answer)
        public async Task<PageResult> LoadOlder(string channel)
        {
            if (channel == null || !SupportsPaging || !(Online || TestFed)) return new PageResult { Failed = true };
            if (pagePending.TryGetValue(channel, out var running)) return await running.Task;
            if (!HasMoreOlder(channel)) return new PageResult { HasMore = false };
            var list = MessagesOf(channel);
            var oldest = list.FirstOrDefault();
            if (oldest == null) return new PageResult { HasMore = false };
            var tcs = new TaskCompletionSource<PageResult>();
            pagePending[channel] = tcs;
            OlderRequested?.Invoke(channel);
            _ = Send(LodgeFieldsDict.Build(new HistoryBeforeMsg { Channel = channel, Before = oldest.Id, BeforeAt = Ms(oldest.At) }.Write));
            if (!tcs.Task.IsCompleted && await Task.WhenAny(tcs.Task, Task.Delay(RequestTimeout)) != tcs.Task)
            {
                pagePending.Remove(channel);
                tcs.TrySetResult(new PageResult { Failed = true });
            }
            return await tcs.Task;
        }

        // loads older pages until `id` is among the messages (or, with `atMs`, until the oldest one shown is older than that time), the history
        // runs out, or the server stops answering. A "slow down" is waited out. Returns true when the message is there.
        public async Task<bool> LoadUntil(string channel, string id, long atMs = 0)
        {
            var list = MessagesOf(channel);
            int rateWaits = 0;
            while (list.All(m => m.Id != id))
            {
                if (atMs > 0 && list.Count > 0 && Ms(list[0].At) < atMs - 1) return false;   // already past where it would have been: it is gone
                if (!HasMoreOlder(channel)) return false;
                var r = await LoadOlder(channel);
                if (r.Rate) { if (++rateWaits > 8) return false; await Task.Delay(PageRetryDelay); continue; }
                if (r.Failed) return false;
                if (r.Added == 0 && !r.HasMore) return false;
            }
            return true;
        }

        void OnHistoryPage(Dictionary<string, object> m)
        {
            var page = HistoryPageMsg.Read(new LodgeFieldsDict(m));
            var channel = page.Channel;
            if (channel == null) return;
            var list = MessagesOf(channel);
            var have = new HashSet<string>(list.Select(x => x.Id));
            var added = new List<MessageVM>();
            foreach (var x in page.Items.Where(x => x.Id != null && !have.Contains(x.Id)).OrderBy(x => x.At))
            {
                x.Channel = channel;                       // the page's channel, whatever the entry says
                added.Add(BuildMessage(x, out _));
            }
            // they are older than everything shown: in front, in time order
            for (int i = 0; i < added.Count; i++) list.Insert(i, added[i]);
            if (added.Count > 0)
            {
                var pinned = new HashSet<string>(PinsOf(channel).Select(p => p.Id));
                foreach (var a in added) { a.Pinned = pinned.Contains(a.Id); a.Unread = false; }
                for (int i = 0; i <= added.Count && i < list.Count; i++) list[i].Continuation = i > 0 && IsContinuation(list[i - 1], list[i]);   // incl. the old first message
            }
            bool more = page.HasMore;
            hasMoreOlder[channel] = more;
            RefreshItemsOf(added);
            if (pagePending.TryGetValue(channel, out var tcs))
            {
                pagePending.Remove(channel);
                tcs.TrySetResult(new PageResult { Added = added.Count, HasMore = more });
            }
            OlderLoaded?.Invoke(channel, added, more);
        }

        static void RefreshItemsOf(IEnumerable<MessageVM> l) { foreach (var m in l) m.RefreshItems(); }

        // ---- search
        public async Task<SearchResults> SearchAsync(string query)
        {
            query = (query ?? "").Trim();
            if (!SupportsSearch || !(Online || TestFed) || query.Length < 2) return new SearchResults { Query = query, Failed = !SupportsSearch };
            searchPending?.TrySetResult(new SearchResults { Query = searchQuery, Failed = true });   // a newer search replaces the one still waiting
            var tcs = searchPending = new TaskCompletionSource<SearchResults>();
            searchQuery = query;
            _ = Send(LodgeFieldsDict.Build(new SearchMsg { Q = query }.Write));
            if (!tcs.Task.IsCompleted && await Task.WhenAny(tcs.Task, Task.Delay(RequestTimeout)) != tcs.Task)
            {
                if (searchPending == tcs) searchPending = null;
                tcs.TrySetResult(new SearchResults { Query = query, Failed = true });
            }
            return await tcs.Task;
        }

        void OnSearchResult(Dictionary<string, object> m)
        {
            var r = SearchResultMsg.Read(new LodgeFieldsDict(m));
            var q = r.Q ?? searchQuery;
            var res = new SearchResults { Query = q, More = r.More };
            foreach (var h in r.Hits)
            {
                var ch = h.Channel;
                if (h.Id == null || ch == null) continue;
                res.Hits.Add(new SearchHitVM
                {
                    ChannelId = ch, ChannelName = TextChannels.FirstOrDefault(c => c.Id == ch)?.Name, Id = h.Id, From = h.From, AtMs = h.At,
                    Snippet = SafeFile.Text(h.Snippet), Query = q,
                });
            }
            var tcs = searchPending;
            searchPending = null;
            tcs?.TrySetResult(res);
        }

        // "slow down" answers: (true when it was for a request of this kind and has been handled)
        bool HistoryRateError(string text)
        {
            text = text ?? "";
            if (searchPending != null && text.StartsWith("You can search", StringComparison.OrdinalIgnoreCase))
            {
                var t = searchPending; searchPending = null;
                t.TrySetResult(new SearchResults { Query = searchQuery, Rate = true });
                return true;
            }
            if (pagePending.Count > 0 && text.StartsWith("You are loading history", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var kv in pagePending.ToList()) { pagePending.Remove(kv.Key); kv.Value.TrySetResult(new PageResult { Rate = true }); }
                return true;
            }
            return false;
        }
    }
}
