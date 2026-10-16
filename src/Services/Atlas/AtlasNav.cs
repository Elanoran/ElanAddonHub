using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ElansAddonHub.Services.Db2;

namespace ElansAddonHub.Services.Atlas
{
    // ============================================================================================================================
    // The Atlas shell's navigation model (no controls): the sub-navigation is built from the AtlasCategories registry, grouped (Database, World,
    // Character, Lodge); categories that share a NavKey are ONE row ("Spells and talents", "Professions"), but every id and route still works.
    // KeyFor(id) says which row a page belongs to, for every kind of page (browse, entity, hosted, the data sources page), and Describe(id)
    // turns an id into a title, glyph and icon for the Recent / Pinned lists, the Home cards and the Ctrl+K palette.
    // ============================================================================================================================

    public sealed class AtlasNavEntry
    {
        public string Key = "", Title = "", Glyph = "", Group = "", OpenId = "", Blurb = "";
        public string Icon = "";                                  // the AtlasIcons drawing (a category id); "" = the glyph
        public int Order;
        public bool Extra;                                        // not a registry category (the Lodge's finds)
        public List<AtlasCategory> Members = new List<AtlasCategory>();
        public override string ToString() => Group + "/" + Title;
    }

    // an id as the Recent / Pinned lists show it
    public sealed class AtlasNavItem
    {
        public string Id = "", Title = "", Detail = "", Glyph = "", Kind = "";
        public string Icon = "";                // a category page: its AtlasIcons drawing (the category id); "" = the glyph
        public int Quality = -1, IconId;
    }

    public static class AtlasNav
    {
        public const string Database = "Database", World = "World", Character = "Character", Lodge = "Lodge";
        public static readonly string[] Groups = { Database, World, Character, Lodge };

        public const string DataId = "atlas:data";                // the Data sources page
        public const string FindsKey = "finds";
        public static readonly string FindsId = new BrowseQuery { Cat = "items", Src = "lodge", How = "drop" }.ToId();

        public static bool IsPage(string id) => string.Equals((id ?? "").Trim(), DataId, StringComparison.OrdinalIgnoreCase);
        public static string PageTitle(string id) => IsPage(id) ? "Data sources" : "";

        static readonly Dictionary<string, string> kindCategory = new Dictionary<string, string>
        {
            ["item"] = "items", ["npc"] = "creatures", ["obj"] = "objects", ["quest"] = "quests", ["zone"] = "zones",
            ["instance"] = "instances", ["spell"] = "spells", ["skill"] = "skills", ["faction"] = "factions", ["class"] = "classes", ["race"] = "races",
        };

        public static string CategoryOfKind(string kind) => kind != null && kindCategory.TryGetValue(kind, out var c) ? c : "";

        static string GroupOf(AtlasCategory c) => string.IsNullOrEmpty(c.Group) ? Database : c.Group;
        static string KeyOfCategory(AtlasCategory c) => string.IsNullOrEmpty(c.NavKey) ? c.Id : c.NavKey;

        // the rows of the nav, group by group, in the registry's order; only categories whose data is there (AtlasCategories.Visible)
        public static List<AtlasNavEntry> Entries()
        {
            var res = new List<AtlasNavEntry>();
            foreach (var c in AtlasCategories.Visible)
            {
                var key = KeyOfCategory(c);
                var e = res.FirstOrDefault(x => x.Key == key);
                if (e == null)
                {
                    e = new AtlasNavEntry { Key = key, Group = GroupOf(c), Order = c.Order };
                    res.Add(e);
                }
                e.Members.Add(c);
            }
            foreach (var e in res)
            {
                var first = e.Members.OrderBy(m => m.Order).First();
                var main = e.Members.FirstOrDefault(m => m.NavPrimary) ?? first;
                e.Order = first.Order;
                e.Title = e.Members.Select(m => m.NavTitle).FirstOrDefault(t => !string.IsNullOrEmpty(t)) ?? main.Title;
                e.Glyph = main.Glyph; e.Blurb = main.Blurb; e.OpenId = main.OpenId; e.Icon = main.Id;
            }
            bool lodge = false;
            try { lodge = AtlasQuery.LodgeHasData(); } catch (Exception) { }
            if (lodge && AtlasCategories.Find("items") != null)
                res.Add(new AtlasNavEntry { Key = FindsKey, Title = "Finds", Glyph = "", Group = Lodge, OpenId = FindsId, Order = 1000, Extra = true, Icon = FindsKey, Blurb = "What the Lodge's members found and saw drop" });
            return res.OrderBy(e => Array.IndexOf(Groups, e.Group) < 0 ? 99 : Array.IndexOf(Groups, e.Group)).ThenBy(e => e.Order).ThenBy(e => e.Title, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // the nav row a page belongs to: "home", "data", an entry key, or "" when it belongs to none
        public static string KeyFor(string id)
        {
            id = (id ?? "").Trim();
            if (id.Length == 0 || id == AtlasBrowse.HomeLink) return "home";
            if (IsPage(id)) return "data";
            if (AtlasBrowse.IsBrowseId(id))
            {
                if (!BrowseQuery.TryParse(id, out var q)) return "";
                if (q.Cat == "items" && q.Src == "lodge" && q.How == "drop" && q.Path.Count == 0) return FindsKey;
                return KeyOfCategoryId(q.Cat);
            }
            if (AtlasCategories.TryHosted(id, out var hc, out _)) return KeyOfCategory(hc);
            if (AtlasIds.TryParse(id, out var kind, out _)) return KeyOfCategoryId(CategoryOfKind(kind));
            return "";
        }

        static string KeyOfCategoryId(string cat)
        {
            var c = AtlasCategories.Find(cat);
            return c == null ? "" : KeyOfCategory(c);
        }

        // the category crumb of an entity page ("Items" for item:100), null when the kind has none
        public static AtlasCrumb CategoryCrumb(string kind)
        {
            var c = AtlasCategories.Find(CategoryOfKind(kind));
            return c == null ? null : new AtlasCrumb(c.Title, c.OpenId);
        }

        // ---- the pages of the Atlas itself, for the palette: every visible category (lists and hosted pages) and the data sources
        public static List<AtlasHit> AllPages()
        {
            var l = new List<AtlasHit>();
            foreach (var c in AtlasCategories.Visible)
                l.Add(new AtlasHit { Id = c.OpenId, Kind = "page", Name = c.Title, Detail = c.Blurb, Label = "" });
            l.Add(new AtlasHit { Id = DataId, Kind = "page", Name = "Data sources", Detail = "Your game data, the Classic baseline, the Lodge pool and the world map cache", Label = "" });
            return l;
        }

        static readonly string[] dataWords = { "data", "source", "sources", "status", "cache", "classic", "baseline", "lodge", "pool" };

        // the pages a search text names (by title, keywords or what they hold): rank 0 exact, 1 prefix, 2 word start, 3 inside
        public static List<AtlasHit> PageHits(string text)
        {
            var res = new List<AtlasHit>();
            var q = (text ?? "").Trim().ToLowerInvariant();
            if (q.Length < 2) return res;
            foreach (var c in AtlasCategories.Visible)
            {
                int r = AtlasQuery.MatchRank(c.Title, q);
                if (r < 0 && !string.IsNullOrEmpty(c.NavTitle)) r = AtlasQuery.MatchRank(c.NavTitle, q);
                if (r < 0 && !string.IsNullOrEmpty(c.Keywords)) { var kw = c.Keywords.ToLowerInvariant().Split(' ').Where(k => k.StartsWith(q, StringComparison.Ordinal)).Any(); if (kw) r = 3; }
                if (r < 0) continue;
                res.Add(new AtlasHit { Id = c.OpenId, Kind = "page", Name = c.Title, Detail = c.Blurb, Rank = r });
            }
            int dr = AtlasQuery.MatchRank("Data sources", q);
            if (dr < 0 && dataWords.Any(w => w.StartsWith(q, StringComparison.Ordinal))) dr = 3;
            if (dr >= 0) res.Add(new AtlasHit { Id = DataId, Kind = "page", Name = "Data sources", Detail = "Your game data, the Classic baseline, the Lodge pool and the world map cache", Rank = dr });
            res.Sort((a, b) => { int c = a.Rank.CompareTo(b.Rank); return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); });
            return res;
        }

        static string Pretty(string slug)
        {
            if (string.IsNullOrEmpty(slug)) return "";
            var t = slug.Replace('-', ' ');
            return char.ToUpperInvariant(t[0]) + t.Substring(1);
        }

        // the title, glyph and icon of an id for the lists; from the caches only (never builds a page). Null for the start page.
        public static AtlasNavItem Describe(string id)
        {
            id = (id ?? "").Trim();
            if (id.Length == 0 || id == AtlasBrowse.HomeLink) return null;
            if (IsPage(id)) return new AtlasNavItem { Id = id, Title = "Data sources", Detail = "Page", Glyph = "", Kind = "page" };
            if (AtlasBrowse.IsBrowseId(id))
            {
                if (!BrowseQuery.TryParse(id, out var q)) return new AtlasNavItem { Id = id, Title = id, Detail = "List", Kind = "browse" };
                var c = AtlasCategories.Find(q.Cat);
                var title = (c?.Title ?? Pretty(q.Cat)) + (q.Path.Count > 0 ? " · " + Pretty(q.Path[q.Path.Count - 1]) : "");
                if (q.HasFilters) title += " (filtered)";
                return new AtlasNavItem { Id = id, Title = title, Detail = "List", Glyph = c?.Glyph ?? "", Kind = "browse", Icon = KeyFor(id) == FindsKey ? FindsKey : c?.Id ?? "" };
            }
            if (AtlasCategories.TryHosted(id, out var hc, out var sub))
            {
                string last = "";
                try { last = hc.SubTitle?.Invoke(sub) ?? ""; } catch (Exception) { }
                return new AtlasNavItem { Id = id, Title = hc.Title + (last.Length > 0 ? " · " + last : ""), Detail = "Page", Glyph = hc.Glyph, Kind = "page", Icon = hc.Id };
            }
            if (AtlasIds.TryParse(id, out var kind, out var n))
            {
                AtlasHit h = null;
                try { h = AtlasQuery.Describe(id); } catch (Exception) { }
                if (h == null) return new AtlasNavItem { Id = id, Title = AtlasKinds.Singular(kind) + " " + n.ToString(CultureInfo.InvariantCulture), Detail = AtlasKinds.Singular(kind), Glyph = AtlasKinds.Glyph(kind), Kind = kind };
                return new AtlasNavItem { Id = id, Title = h.Name, Detail = AtlasKinds.Singular(kind) + (h.Detail.Length > 0 && !h.Detail.Equals(AtlasKinds.Singular(kind), StringComparison.OrdinalIgnoreCase) ? ", " + h.Detail : ""), Glyph = AtlasKinds.Glyph(kind), Kind = kind, Quality = h.Quality, IconId = h.IconId };
            }
            return new AtlasNavItem { Id = id, Title = id, Glyph = "" };
        }
    }

    // ============================================================================================================================
    // Recents and pins: ids only (names and icons are looked up again when shown). The two lists belong to the settings (Settings.AtlasRecents /
    // AtlasPins); this class edits them in place and tells the owner after every change so the owner can save (debounced).
    // An entity id may carry a tab ("item:1?tab=sources", see AtlasTabs): for the lists it is the same page as "item:1" - one entry, one pin - and the entry keeps the form
    // it was last seen in, so opening it again lands on the tab you left it on. A pin is a bookmark: it keeps the form it was pinned in (changing tab on the page does not move it).
    // ============================================================================================================================
    public sealed class AtlasNavState
    {
        public const int MaxRecents = 8, MaxPins = 40;
        readonly List<string> recents, pins;
        readonly Action changed;

        public AtlasNavState(List<string> recents, List<string> pins, Action changed)
        {
            this.recents = recents ?? new List<string>(); this.pins = pins ?? new List<string>(); this.changed = changed;
            Clean(this.recents, MaxRecents); Clean(this.pins, MaxPins);
        }

        public IReadOnlyList<string> Recents => recents;
        public IReadOnlyList<string> Pins => pins;

        static string B(string id) => AtlasTabs.BaseId(id);
        static int IndexOf(List<string> l, string id) { var b = B(id); return l.FindIndex(s => B(s) == b); }

        static void Clean(List<string> l, int max)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            l.RemoveAll(s => !Recordable(s) || !seen.Add(B(s)));
            if (l.Count > max) l.RemoveRange(max, l.Count - max);
        }

        // the start page and empty ids are never remembered; every other page is
        public static bool Recordable(string id) => !string.IsNullOrWhiteSpace(id) && id != AtlasBrowse.HomeLink && id.Length <= 300;

        void Changed() { try { changed?.Invoke(); } catch (Exception) { } }

        // a page was opened: it goes to the front (the same page on another tab is the same entry)
        public void Visit(string id)
        {
            if (!Recordable(id)) return;
            int at = IndexOf(recents, id);
            if (at == 0) { if (recents[0] != id) { recents[0] = id; Changed(); } return; }
            if (at > 0) recents.RemoveAt(at);
            recents.Insert(0, id);
            if (recents.Count > MaxRecents) recents.RemoveRange(MaxRecents, recents.Count - MaxRecents);
            Changed();
        }

        // the page shown became another id by itself (a filter changed, a tab was picked): the newest recent follows, no second entry
        public void Follow(string oldId, string newId)
        {
            if (!Recordable(newId) || oldId == newId) return;
            int at = IndexOf(recents, oldId);
            if (at < 0) { Visit(newId); return; }
            var keep = recents[at];
            recents.RemoveAll(s => !ReferenceEquals(s, keep) && B(s) == B(newId));
            at = recents.IndexOf(keep);
            recents[at] = newId;
            Changed();
        }

        public void RemoveRecent(string id) { var b = B(id); if (recents.RemoveAll(s => B(s) == b) > 0) Changed(); }
        public void ClearRecents() { if (recents.Count == 0) return; recents.Clear(); Changed(); }

        public bool IsPinned(string id) => IndexOf(pins, id ?? "") >= 0;

        public bool Pin(string id)
        {
            if (!Recordable(id) || IsPinned(id)) return false;
            if (pins.Count >= MaxPins) return false;
            pins.Add(id); Changed();
            return true;
        }

        public bool Unpin(string id) { var b = B(id ?? ""); if (pins.RemoveAll(s => B(s) == b) == 0) return false; Changed(); return true; }
        public bool TogglePin(string id) { if (IsPinned(id)) { Unpin(id); return false; } return Pin(id); }


        // moves a pinned page up (-1) or down (+1) in the list
        public bool MovePin(string id, int delta)
        {
            int at = IndexOf(pins, id ?? "");
            if (at < 0) return false;
            return MovePinTo(id, at + delta);
        }

        public bool MovePinTo(string id, int index)
        {
            int at = IndexOf(pins, id ?? "");
            if (at < 0) return false;
            index = Math.Max(0, Math.Min(pins.Count - 1, index));
            if (index == at) return false;
            var stored = pins[at];
            pins.RemoveAt(at); pins.Insert(index, stored);
            Changed();
            return true;
        }
    }
}
