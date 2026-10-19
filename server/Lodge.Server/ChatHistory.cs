using System.Text.Json.Nodes;
using Elans.Shared;

namespace Lodge;

// Older messages and search (server 2.9). Both only ever look at the channels the asker's rank may see - a channel they can't see
// answers exactly like one that does not exist.
public partial class ChatModule
{
    public const int PageMax = 100;       // messages per "history:before" answer
    public const int SearchMax = 50;      // hits per "search" answer
    public const int SearchQueryMax = 64; // characters; longer queries are cut
    public const int SearchQueryMin = 2;

    // "history:before": { channel, before: <message id>, beforeAt?: <ms, used when that message is gone>, limit? (1-100) } (HistoryBeforeMsg)
    // answer:           { t: "history:page", channel, before, messages: [oldest ... newest], hasMore } (HistoryPageMsg)
    async Task HistoryBefore(Member me, JsonObject m)
    {
        var req = HistoryBeforeMsg.Read(new LodgeJsonFields(m));
        var ch = hub.Channels.Get(req.Channel);
        var before = req.Before;
        if (!me.Budget.Page.TryTake()) { me.CountDrop(hub.Clock); await hub.Error(me, "You are loading history too fast - try again in a moment", "rate"); return; }
        var page = new JsonArray();
        bool more = false;
        if (ch != null && ch.Type == "text" && ch.VisibleTo(me.Role))
        {
            int limit = Math.Clamp(req.Limit ?? PageMax, 1, PageMax);
            long beforeAt = m["beforeAt"] is JsonValue bv && bv.TryGetValue<long>(out var l) ? l : 0;   // raw on purpose: only a whole JSON number
            var s = Store(ch.Id);
            lock (s)
            {
                int end = -1;
                if (!string.IsNullOrEmpty(before)) end = s.Messages.FindIndex(x => (string)x["id"] == before);
                if (end < 0 && beforeAt > 0)
                {
                    // the message they scrolled up from is gone: everything older than its time
                    end = s.Messages.FindIndex(x => ((long?)x["at"] ?? 0) >= beforeAt);
                    if (end < 0) end = s.Messages.Count;
                }
                if (end > 0)
                {
                    int start = Math.Max(0, end - limit);
                    for (int i = start; i < end; i++) page.Add(s.Messages[i].DeepClone());
                    more = start > 0;
                }
            }
        }
        // the stored records go out as they are (deep copies), in the stored msg shape (ChatMessage)
        await hub.Send(me, LodgeJson.Build(new HistoryPageMsg { Channel = ch?.Id ?? req.Channel ?? "", Before = before ?? "", Messages = page, HasMore = more }.Write));
    }

    // "search": { q, channel? } - case-insensitive substring over the text (and attachment names) of the channels I may see, newest first
    // answer: { t: "search:result", q, hits: [ { channel, id, from, at, snippet } ] (at most 50), more } (SearchMsg, SearchResultMsg)
    async Task Search(Member me, JsonObject m)
    {
        if (!me.Budget.Search.TryTake()) { me.CountDrop(hub.Clock); await hub.Error(me, "You can search a few times a minute - try again in a moment", "rate"); return; }
        var req = SearchMsg.Read(new LodgeJsonFields(m));
        var q = Names.Text(req.Q ?? "", SearchQueryMax);
        var hits = new List<(long at, SearchHit hit)>();
        bool more = false;
        if (q.Length >= SearchQueryMin)
        {
            var only = req.Channel;
            foreach (var ch in hub.Channels.All.Where(c => c.Type == "text" && c.VisibleTo(me.Role) && (only == null || c.Id == only)))
            {
                var s = Store(ch.Id);
                int found = 0;
                lock (s)
                {
                    for (int i = s.Messages.Count - 1; i >= 0; i--)
                    {
                        var msg = s.Messages[i];
                        var flat = Flat((string)msg["text"]);
                        int at = flat.IndexOf(q, StringComparison.OrdinalIgnoreCase);
                        if (at < 0 && msg["file"] is JsonObject fo && (string)fo["name"] is string file && file.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            flat = "[" + file + "]";
                            at = flat.IndexOf(q, StringComparison.OrdinalIgnoreCase);
                        }
                        if (at < 0) continue;
                        if (++found > SearchMax) { more = true; break; }
                        hits.Add(((long?)msg["at"] ?? 0, new SearchHit
                        {
                            Channel = ch.Id, Id = (string)msg["id"], From = (string)msg["from"], At = (long?)msg["at"] ?? 0, Snippet = SnippetAround(flat, at),
                        }));
                    }
                }
            }
        }
        var ordered = hits.OrderByDescending(h => h.at).ToList();
        if (ordered.Count > SearchMax) { ordered = ordered.Take(SearchMax).ToList(); more = true; }
        await hub.Send(me, LodgeJson.Build(new SearchResultMsg { Q = q, Hits = ordered.Select(h => h.hit).ToList(), More = more }.Write));
    }

    static readonly System.Text.RegularExpressions.Regex Spaces = new(@"\s+", System.Text.RegularExpressions.RegexOptions.Compiled);
    static string Flat(string text) => string.IsNullOrEmpty(text) ? "" : Spaces.Replace(text, " ").Trim();

    // about 120 characters around the match (`flat` has its whitespace collapsed), with an ellipsis where it was cut
    public static string SnippetAround(string flat, int at)
    {
        const int Width = 120;
        int start = Math.Max(0, at - Width / 3);
        int end = Math.Min(flat.Length, start + Width);
        start = Math.Max(0, Math.Min(start, end - Width));
        var s = Names.Cut(flat.Substring(start, end - start), Width);
        if (s.Length > 0 && char.IsLowSurrogate(s[0]) && start > 0) s = s.Substring(1);
        if (s.Length > 0 && char.IsHighSurrogate(s[^1])) s = s[..^1];
        return (start > 0 ? "…" : "") + s + (end < flat.Length ? "…" : "");
    }
}
