using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace ElansAddonHub.Services.Atlas
{
    // ============================================================================================================================
    // The search behind the Ctrl+K palette (no controls, runs on a worker): AtlasQuery.Search across the kinds, narrowed to a scope, plus the pages of
    // the Atlas itself (categories, hosted pages, the data sources). Typing "i:", "n:", "z:", "q:", "s:" or "p:" first picks a scope. When nothing
    // matches, a tolerant second pass looks again: every word on its own ("sword iron"), then without the last one or two letters ("gnarlfnag").
    // ============================================================================================================================
    public static class AtlasPaletteSearch
    {
        public static readonly string[] Scopes = { "all", "items", "creatures", "quests", "zones", "spells", "pages" };
        public static string ScopeTitle(string scope)
        {
            switch (scope)
            {
                case "items": return "Items";
                case "creatures": return "Creatures";
                case "quests": return "Quests";
                case "zones": return "Zones";
                case "spells": return "Spells";
                case "pages": return "Pages";
                default: return "All";
            }
        }

        // the kinds of hit a scope shows ("all" = every kind)
        public static bool InScope(string scope, string kind)
        {
            switch (scope)
            {
                case "items": return kind == "item";
                case "creatures": return kind == "npc";
                case "quests": return kind == "quest";
                case "zones": return kind == "zone" || kind == "instance";
                case "spells": return kind == "spell" || kind == "skill";
                case "pages": return kind == "page" || kind == "class" || kind == "race";
                default: return true;
            }
        }

        // "i:sword" -> scope "items", text "sword". Anything else is left as it is (item:100 is a link, not a prefix: a prefix is one letter and a colon).
        public static bool ParsePrefix(string text, out string scope, out string rest)
        {
            scope = null; rest = text ?? "";
            if (rest.Length >= 2 && rest[1] == ':' && char.IsLetter(rest[0]))
            {
                switch (char.ToLowerInvariant(rest[0]))
                {
                    case 'i': scope = "items"; break;
                    case 'n': scope = "creatures"; break;
                    case 'q': scope = "quests"; break;
                    case 'z': scope = "zones"; break;
                    case 's': scope = "spells"; break;
                    case 'p': scope = "pages"; break;
                }
                if (scope != null) rest = rest.Substring(2).TrimStart();
            }
            return scope != null;
        }

        public sealed class Result
        {
            public string Query = "", Scope = "all", Note = "";
            public List<AtlasGroup> Groups = new List<AtlasGroup>();
            public bool ById, Fuzzy;
            public string OpenId = "";                          // the text was a link to a page the Atlas page opens itself (browse, hosted, data sources)
            public int Total => Groups.Sum(g => g.Hits.Count);
            public IEnumerable<AtlasHit> All => Groups.SelectMany(g => g.Hits);
        }

        public static Result Run(string text, string scope, CancellationToken ct)
        {
            var res = new Result { Query = (text ?? "").Trim(), Scope = scope ?? "all" };
            if (res.Query.Length == 0) return res;
            ct.ThrowIfCancellationRequested();
            var routed = AtlasQuery.NormalizeId(res.Query);
            if (routed != null && AtlasCategories.IsRouted(routed))
            {
                res.OpenId = routed;
                var d = AtlasNav.Describe(routed);
                var g = new AtlasGroup { Kind = "page", Title = "Pages", Total = 1 };
                g.Hits.Add(new AtlasHit { Id = routed, Kind = "page", Name = d?.Title ?? routed, Detail = d?.Detail ?? "Page" });
                res.Groups.Add(g);
                return res;
            }
            int per = res.Scope == "all" ? 6 : 30;
            Fill(res, AtlasQuery.Search(res.Query, per, ct), ct);
            if (res.Total == 0 && !res.ById) Tolerant(res, per, ct);
            return res;
        }

        static void Fill(Result res, AtlasSearchResult r, CancellationToken ct)
        {
            res.ById = r.ById;
            var groups = new List<AtlasGroup>();
            foreach (var g in r.Groups)
            {
                if (g.Kind == "page") continue;
                if (!InScope(res.Scope, g.Kind)) continue;
                groups.Add(g);
            }
            // the pages: hosted sub-pages by name (from the search) and the categories / data sources
            ct.ThrowIfCancellationRequested();
            if (res.Scope == "all" || res.Scope == "pages")
            {
                var pages = new List<AtlasHit>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var pg in r.Groups.Where(x => x.Kind == "page"))
                    foreach (var h in pg.Hits) if (seen.Add(h.Id)) pages.Add(h);
                foreach (var h in AtlasNav.PageHits(res.Query)) if (seen.Add(h.Id)) pages.Add(h);
                if (pages.Count > 0)
                {
                    var pgroup = new AtlasGroup { Kind = "page", Title = "Pages", Total = pages.Count };
                    pgroup.Hits.AddRange(pages.Take(res.Scope == "pages" ? 30 : 4));
                    groups.Add(pgroup);
                }
            }
            res.Groups = groups.OrderBy(g => g.Kind == "page" ? 0 : 1).ThenBy(g => g.Hits[0].Rank).ThenBy(g => Array.IndexOf(AtlasKinds.Order, g.Kind)).ToList();
        }

        static void Tolerant(Result res, int per, CancellationToken ct)
        {
            var q = res.Query;
            // every word on its own: search the longest, keep what has all of them
            var words = q.Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 2).ToList();
            if (words.Count > 1)
            {
                var longest = words.OrderByDescending(w => w.Length).First();
                var r = AtlasQuery.Search(longest, 40, ct);
                var keep = new AtlasSearchResult { Query = q };
                foreach (var g in r.Groups)
                {
                    var hits = g.Hits.Where(h => words.All(w => (h.Name + " " + h.Detail).IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0)).Take(per).ToList();
                    if (hits.Count > 0) { var ng = new AtlasGroup { Kind = g.Kind, Title = g.Title, Total = hits.Count }; ng.Hits.AddRange(hits); keep.Groups.Add(ng); }
                }
                Fill(res, keep, ct);
                if (res.Total > 0) { res.Fuzzy = true; res.Note = "Showing names that have all of: " + string.Join(", ", words); return; }
            }
            // a slip at the end of the word: look again without the last one or two letters
            for (int cut = 1; cut <= 2 && q.Length - cut >= 3; cut++)
            {
                ct.ThrowIfCancellationRequested();
                var shorter = q.Substring(0, q.Length - cut);
                Fill(res, AtlasQuery.Search(shorter, per, ct), ct);
                if (res.Total > 0) { res.Fuzzy = true; res.Note = "Nothing is called \"" + q + "\". Showing \"" + shorter + "\" instead."; return; }
            }
        }
    }
}
