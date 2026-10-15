using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services.Atlas.Classic;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Maps;

namespace ElansAddonHub.Services.Atlas
{
    // ============================================================================================================================
    // Atlas browse: lists by category with filters and sort, so nothing has to be searched for.
    //   id:  "browse:items/weapons/swords?minlvl=20&q=3&how=vendor&sort=ilvl"   (category path, then the filters; the same text is the history entry)
    //   AtlasBrowse.Run(id)  -> a BrowseResult (rows, counts, the filter controls that apply, notes); runs on a worker thread
    // The per-category indexes (AtlasBrowse.Index.cs) are built on first use, cached, and rebuilt when any layer underneath changed.
    // ============================================================================================================================

    // the parsed id of a browse page
    public sealed class BrowseQuery
    {
        public string Cat = "items";
        public List<string> Path = new List<string>();      // slugs below the category: ["weapons", "one-handed-swords"]
        public string Name = "";                            // name contains
        public int MinLevel, MaxLevel;                      // 0 = open
        public int Quality = -1;                            // items: exactly this quality
        public string Slot = "";                            // items: a slot name ("Head")
        public string Src = "";                             // "game" | "classic" | "lodge"
        public string How = "";                             // items: drop | vendor | quest | crafted | fishing | container | gathered
        public string For = "";                             // items: a character key (level and proficiency)
        public string Zone = "";                            // creatures, objects, quests: a UiMap id
        public string Rank = "";                            // creatures: elite | rare | boss
        public string Learn = "";                           // recipes: "can" = recipes of my characters' professions that they have not learned yet and have the skill for (needs the Hub's profession-window reads)
        public string Sort = "";                            // "" = the category's own order

        public const string Prefix = "browse:";
        public BrowseQuery Clone() { var q = (BrowseQuery)MemberwiseClone(); q.Path = new List<string>(Path); return q; }

        public static bool TryParse(string id, out BrowseQuery q)
        {
            q = null;
            if (!AtlasBrowse.IsBrowseId(id)) return false;
            var rest = id.Trim().Substring(Prefix.Length);
            string query = "";
            int qm = rest.IndexOf('?');
            if (qm >= 0) { query = rest.Substring(qm + 1); rest = rest.Substring(0, qm); }
            var parts = rest.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim().ToLowerInvariant()).Where(p => p.Length > 0).ToList();
            q = new BrowseQuery { Cat = parts.Count > 0 ? parts[0] : "items" };
            q.Path.AddRange(parts.Skip(1));
            foreach (var kv in query.Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int e = kv.IndexOf('=');
                var k = (e < 0 ? kv : kv.Substring(0, e)).ToLowerInvariant();
                string v; try { v = e < 0 ? "" : Uri.UnescapeDataString(kv.Substring(e + 1).Replace('+', ' ')); } catch (Exception) { v = ""; }
                int n;
                switch (k)
                {
                    case "name": q.Name = v.Trim(); break;
                    case "minlvl": if (int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out n)) q.MinLevel = n; break;
                    case "maxlvl": if (int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out n)) q.MaxLevel = n; break;
                    case "q": if (int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out n) && n < 8) q.Quality = n; break;
                    case "slot": q.Slot = v.Trim(); break;
                    case "src": v = v.ToLowerInvariant(); if (v == "game" || v == "classic" || v == "lodge") q.Src = v; break;
                    case "how": v = v.ToLowerInvariant(); if (AtlasBrowse.Hows.Contains(v)) q.How = v; break;
                    case "for": q.For = v.Trim(); break;
                    case "zone": if (int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out n) && n > 0) q.Zone = n.ToString(CultureInfo.InvariantCulture); break;
                    case "rank": v = v.ToLowerInvariant(); if (v == "elite" || v == "rare" || v == "boss") q.Rank = v; break;
                    case "learn": v = v.ToLowerInvariant(); if (v == "can") q.Learn = v; break;
                    case "sort": q.Sort = v.Trim().ToLowerInvariant(); break;
                }
            }
            if (q.MinLevel > 0 && q.MaxLevel > 0 && q.MaxLevel < q.MinLevel) { var t = q.MinLevel; q.MinLevel = q.MaxLevel; q.MaxLevel = t; }
            return true;
        }

        // the canonical id: the same query always gives the same text (so back / forward and "already shown" compare right)
        public string ToId()
        {
            var sb = new StringBuilder(Prefix).Append(Cat);
            foreach (var p in Path) sb.Append('/').Append(p);
            var qs = new List<string>();
            void Add(string k, string v) { if (!string.IsNullOrEmpty(v)) qs.Add(k + "=" + Uri.EscapeDataString(v)); }
            Add("name", Name);
            if (MinLevel > 0) Add("minlvl", MinLevel.ToString(CultureInfo.InvariantCulture));
            if (MaxLevel > 0) Add("maxlvl", MaxLevel.ToString(CultureInfo.InvariantCulture));
            if (Quality >= 0) Add("q", Quality.ToString(CultureInfo.InvariantCulture));
            Add("slot", Slot); Add("src", Src); Add("how", How); Add("for", For); Add("zone", Zone); Add("rank", Rank); Add("learn", Learn); Add("sort", Sort);
            if (qs.Count > 0) sb.Append('?').Append(string.Join("&", qs));
            return sb.ToString();
        }

        public BrowseQuery WithPath(params string[] slugs) { var q = Clone(); q.Path = slugs.ToList(); return q; }
        public bool HasFilters => Name.Length > 0 || MinLevel > 0 || MaxLevel > 0 || Quality >= 0 || Slot.Length > 0 || Src.Length > 0 || How.Length > 0 || For.Length > 0 || Zone.Length > 0 || Rank.Length > 0 || Learn.Length > 0;
        public override string ToString() => ToId();
    }

    public sealed class BrowseChild
    {
        public string Slug = "", Title = "", Id = "";
        public int Count;
        public bool Current;
    }

    public sealed class BrowseChoice
    {
        public string Value = "", Text = "";
        public BrowseChoice(string value, string text) { Value = value; Text = text; }
    }

    // which filter controls the page shows for this category, and what they offer
    public sealed class BrowseControls
    {
        public bool Level, Quality, Slot, Src, How, For, Zone, Rank, Learn;
        public string LevelLabel = "Level";
        public List<BrowseChoice> Slots = new List<BrowseChoice>(), Zones = new List<BrowseChoice>(), Chars = new List<BrowseChoice>(), Sorts = new List<BrowseChoice>();
        public string DefaultSort = "name";
    }

    public sealed class BrowseResult
    {
        public BrowseQuery Query = new BrowseQuery();
        public string Id = "";
        public string Title = "", Noun = "", Subtitle = "";
        public bool NotFound;
        public string Message = "";
        public bool Hosted;                                          // set by the page when the id was a hosted category
        public List<AtlasCrumb> Crumbs = new List<AtlasCrumb>();
        public List<BrowseChild> Children = new List<BrowseChild>();
        public bool ChildrenAreSiblings;                             // the page has no children of its own: these are its neighbours
        public List<AtlasRow> Rows = new List<AtlasRow>();
        public int Total;                                            // rows after the filters
        public int InCategory;                                       // rows before the filters
        public bool Capped;
        public List<string> Notes = new List<string>();
        public BrowseControls Controls = new BrowseControls();
        public string Sort = "name";                                 // the sort in use
        public string CountText => Capped ? AtlasFormat.Count(Total, Noun, Noun + "s") + ", the first " + Rows.Count.ToString("N0", CalendarRules.English) + " shown" : AtlasFormat.Count(Total, Noun, Plural(Noun));
        static string Plural(string noun) => noun.EndsWith("ss", StringComparison.Ordinal) ? noun + "es" : noun.EndsWith("s", StringComparison.Ordinal) ? noun : noun + "s";
        public override string ToString() => Id + " [" + Total + "/" + InCategory + "]";
    }

    public static partial class AtlasBrowse
    {
        public static readonly string[] Hows = { "drop", "vendor", "quest", "crafted", "fishing", "container", "gathered" };
        public const int MaxRows = 30000;
        public const string HomeLink = "atlas:";           // the first breadcrumb: the start page

        public static bool IsBrowseId(string id) => !string.IsNullOrEmpty(id) && id.TrimStart().StartsWith(BrowseQuery.Prefix, StringComparison.OrdinalIgnoreCase);

        public static string HowText(string how)
        {
            switch (how)
            {
                case "drop": return "Drops";
                case "vendor": return "Vendor";
                case "quest": return "Quest reward";
                case "crafted": return "Crafted";
                case "fishing": return "Fishing";
                case "container": return "Containers";
                case "gathered": return "Gathered";
                default: return "";
            }
        }

        // how many entries a category has in all (the tiles of the start page); builds the index when it is not there yet
        public static int Count(AtlasCategory c)
        {
            if (c == null || c.Hosted) return -1;
            try { return Index(c.Id)?.All.Count ?? 0; } catch (Exception e) { Util.Log("atlas browse count " + c.Id + ": " + e.Message); return 0; }
        }

        // the count of a category if its index is already built (never builds), -1 when not
        public static int CountIfBuilt(string cat)
        {
            lock (cacheLock) return cache.TryGetValue(cat, out var ix) && ix.Token == Token() ? ix.All.Count : -1;
        }

        // ============================================================================================================================ run
        public static BrowseResult Run(string id)
        {
            AtlasQuery.BuildHookForTest?.Invoke(id);
            if (!BrowseQuery.TryParse(id, out var q)) return new BrowseResult { Id = id ?? "", NotFound = true, Title = "Browse", Message = "\"" + id + "\" is not a browse link." };
            return Run(q);
        }

        public static BrowseResult Run(BrowseQuery q)
        {
            var cat = AtlasCategories.Find(q.Cat);
            var r = new BrowseResult { Query = q, Id = q.ToId() };
            if (cat == null || cat.Hosted)
            {
                r.NotFound = true; r.Title = "Browse"; r.Message = "There is no category called \"" + q.Cat + "\".";
                return r;
            }
            r.Title = cat.Title;
            r.Crumbs.Add(new AtlasCrumb("Atlas", HomeLink));
            BIndex ix;
            try { ix = Index(cat.Id); }
            catch (Exception e) { Util.Log("atlas browse " + q + ": " + e); ix = null; }
            if (ix == null)
            {
                r.NotFound = true; r.Message = UnavailableText(cat.Id);
                return r;
            }
            r.Noun = ix.Noun;
            r.Controls = ControlsFor(ix, q);
            var path = new List<BNode>();
            var node = ix.Root;
            foreach (var slug in q.Path)
            {
                var kid = node.Kids.FirstOrDefault(k => k.Slug == slug);
                if (kid == null) { r.Notes.Add("There is no \"" + slug + "\" here; showing " + node.Title + "."); break; }
                node = kid; path.Add(node);
            }
            r.Query = q = q.WithPath(path.Select(p => p.Slug).ToArray()).WithFiltersOf(q);
            r.Id = q.ToId();
            r.Crumbs.Add(new AtlasCrumb(cat.Title, path.Count == 0 ? "" : "browse:" + cat.Id));
            string acc = "browse:" + cat.Id;
            for (int i = 0; i < path.Count; i++)
            {
                acc += "/" + path[i].Slug;
                r.Crumbs.Add(new AtlasCrumb(path[i].Title, i == path.Count - 1 ? "" : acc));
            }
            if (path.Count > 0) r.Title = path[path.Count - 1].Title;

            // children (or the neighbours when this node is a leaf), with the counts of the category without the filters
            var holder = node.Kids.Count > 0 ? node : path.Count >= 2 ? path[path.Count - 2] : path.Count == 1 ? ix.Root : null;
            if (holder != null)
            {
                r.ChildrenAreSiblings = node.Kids.Count == 0;
                var basePath = holder == node ? path.Select(p => p.Slug).ToList() : path.Take(path.Count - 1).Select(p => p.Slug).ToList();
                foreach (var k in holder.Kids)
                {
                    int n = ix.CountOf(k);
                    if (n == 0) continue;
                    var cq = q.Clone(); cq.Path = basePath.Concat(new[] { k.Slug }).ToList();
                    r.Children.Add(new BrowseChild { Slug = k.Slug, Title = k.Title, Count = n, Id = cq.ToId(), Current = ReferenceEquals(k, node) });
                }
            }

            // filter, sort, rows
            var chars = AtlasQuery.Characters();
            var chr = string.IsNullOrEmpty(q.For) ? null : chars.FirstOrDefault(c => c.Key == q.For);
            var inNode = node == ix.Root ? ix.All : ix.All.Where(node.Pred).ToList();
            r.InCategory = inNode.Count;
            var hits = Filter(inNode, q, chr, ix);
            r.Sort = string.IsNullOrEmpty(q.Sort) || !r.Controls.Sorts.Any(s => s.Value == q.Sort) ? r.Controls.DefaultSort : q.Sort;
            hits = Sort(hits, r.Sort, ix);
            r.Total = hits.Count;
            if (hits.Count > MaxRows) { r.Capped = true; hits = hits.Take(MaxRows).ToList(); }
            foreach (var e in hits) r.Rows.Add(RowOf(e, q, ix));
            r.Subtitle = r.CountText + (node != ix.Root && path.Count > 0 ? "" : "");
            AddNotes(r, ix, q, chr, chars);
            return r;
        }

        static BrowseQuery WithFiltersOf(this BrowseQuery to, BrowseQuery from)
        {
            to.Name = from.Name; to.MinLevel = from.MinLevel; to.MaxLevel = from.MaxLevel; to.Quality = from.Quality; to.Slot = from.Slot; to.Src = from.Src; to.How = from.How;
            to.For = from.For; to.Zone = from.Zone; to.Rank = from.Rank; to.Learn = from.Learn; to.Sort = from.Sort;
            return to;
        }

        static string UnavailableText(string cat)
        {
            switch (cat)
            {
                case "items": return ItemData.Status == ItemData.State.Disabled ? "Game data is switched off (Settings > General), so there is no item list. The Classic baseline fills it when it is downloaded." : "The item list is read from your game files and is not ready yet.";
                case "zones": case "instances": return MapData.Status == MapData.State.Disabled ? "Game data is switched off (Settings > General), so there are no maps." : "The maps are read from your game files and are not ready yet.";
                case "spells": case "skills": case "recipes": case "factions": return SpellData.Status == SpellData.State.Disabled ? "Game data is switched off (Settings > General), so there are no spells." : "Spells, skills and recipes are read from your game files and are not ready yet.";
                case "classes": case "races": return ClassData.Status == ClassData.State.Disabled ? "Game data is switched off (Settings > General), so there are no classes or races." : "Classes and races are read from your game files and are not ready yet.";
                default: return "Nothing is known yet. Creatures, objects and quests come from your game's cache and the Classic baseline (get it from the Atlas start page).";
            }
        }

        // ---- filters
        static List<BEntry> Filter(List<BEntry> src, BrowseQuery q, InvChar chr, BIndex ix)
        {
            var words = q.Name.Length == 0 ? null : q.Name.ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            int zone = q.Zone.Length > 0 ? int.Parse(q.Zone, CultureInfo.InvariantCulture) : 0;
            int how = q.How.Length > 0 ? 1 << Array.IndexOf(Hows, q.How) : 0;
            var res = new List<BEntry>();
            var canLearn = q.Learn == "can" ? LearnFilter() : null;
            foreach (var e in src)
            {
                if (words != null && !words.All(w => e.Lower.Contains(w))) continue;
                if (canLearn != null && !canLearn(e)) continue;
                if (q.MinLevel > 0 || q.MaxLevel > 0)
                {
                    if (e.Level <= 0 && e.Level2 <= 0) continue;
                    int hi = Math.Max(e.Level, e.Level2);
                    if (q.MinLevel > 0 && hi < q.MinLevel) continue;
                    if (q.MaxLevel > 0 && e.Level > q.MaxLevel) continue;
                }
                if (q.Quality >= 0 && e.Quality != q.Quality) continue;
                if (q.Slot.Length > 0 && !string.Equals(e.SlotName, q.Slot, StringComparison.OrdinalIgnoreCase)) continue;
                if (q.Src == "game" && !e.Client) continue;
                if (q.Src == "classic" && !e.Classic) continue;
                if (q.Src == "lodge" && !e.Lodge) continue;
                if (how != 0 && (e.Src & how) == 0) continue;
                if (zone != 0 && e.Zone != zone) continue;
                if (q.Rank.Length > 0 && !RankMatches(e, q.Rank)) continue;
                if (chr != null && e.Kind == "item" && !ItemProficiency.CanUse(chr.Class, chr.Level, e)) continue;
                res.Add(e);
            }
            return res;
        }

        // ---- "Recipes you can learn": recipes of the professions my characters have, that a character whose profession window the Hub read has not learned yet and has the skill for
        static bool HasLearnData()
        {
            foreach (var c in AtlasQuery.Characters())
                if (c.Hub != null) { var pm = HubModules.ProfessionsOf(c.Hub); if (pm != null && pm.Known.Count > 0) return true; }
            return false;
        }

        static Func<BEntry, bool> LearnFilter()
        {
            var mods = new List<HubProfessionsModule>();
            foreach (var c in AtlasQuery.Characters())
            {
                if (c.Hub == null) continue;
                var pm = HubModules.ProfessionsOf(c.Hub);
                if (pm != null && pm.Known.Count > 0) mods.Add(pm);        // never-read characters are unknown, not "has not learned"
            }
            return e =>
            {
                if (e.Spell <= 0) return false;
                foreach (var pm in mods)
                {
                    var prof = pm.List.FirstOrDefault(p => (p.Line > 0 && p.Line == e.Skill) || string.Equals(p.Name, e.SubLabel, StringComparison.OrdinalIgnoreCase));
                    if (prof == null) continue;
                    var scan = pm.Known.Values.FirstOrDefault(k => k.Line == e.Skill) ?? pm.Known.Values.FirstOrDefault(k => string.Equals(k.Name, e.SubLabel, StringComparison.OrdinalIgnoreCase));
                    if (scan == null) continue;
                    if (prof.Skill >= e.Start && !scan.Ids.Contains(e.Spell)) return true;
                }
                return false;
            };
        }

        static bool RankMatches(BEntry e, string rank)
        {
            switch (rank)
            {
                case "elite": return e.Rank == 1 || e.Rank == 2;
                case "rare": return e.Rank == 2 || e.Rank == 4;
                case "boss": return e.Rank == 3;
                default: return true;
            }
        }

        // ---- sort: a stable total order (the id breaks every tie), so the same query always lists the same rows
        static List<BEntry> Sort(List<BEntry> list, string sort, BIndex ix)
        {
            IOrderedEnumerable<BEntry> o;
            switch (sort)
            {
                case "level": o = list.OrderBy(e => e.Level <= 0 && e.Level2 <= 0 ? int.MaxValue : e.Level).ThenBy(e => e.Level2); break;
                case "quality": o = list.OrderByDescending(e => e.Quality); break;
                case "ilvl": o = list.OrderByDescending(e => e.ILvl); break;
                case "chance": o = list.OrderByDescending(e => e.Chance).ThenByDescending(e => e.ChanceLodge ? 1 : 0); break;
                case "zone": o = list.OrderBy(e => e.Zone == 0 ? 1 : 0).ThenBy(e => e.ZoneName, StringComparer.OrdinalIgnoreCase); break;
                case "continent": o = list.OrderBy(e => e.Group, StringComparer.OrdinalIgnoreCase); break;
                case "price": o = list.OrderBy(e => e.Price); break;
                default: o = list.OrderBy(e => 0); break;
            }
            return o.ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Id).ToList();
        }

        // ---- the controls of each category
        static BrowseControls ControlsFor(BIndex ix, BrowseQuery q)
        {
            var c = new BrowseControls();
            void S(string v, string t) => c.Sorts.Add(new BrowseChoice(v, t));
            switch (ix.Cat)
            {
                case "items":
                    c.Level = true; c.LevelLabel = "Required level"; c.Quality = true; c.Slot = true; c.Src = true; c.How = true; c.For = true;
                    S("name", "Name"); S("level", "Required level"); S("quality", "Quality"); S("ilvl", "Item level"); S("chance", "Best drop chance");
                    break;
                case "creatures":
                    c.Level = true; c.LevelLabel = "Creature level"; c.Src = true; c.Zone = true; c.Rank = true;
                    S("name", "Name"); S("level", "Level"); S("zone", "Zone");
                    break;
                case "objects":
                    c.Src = true; c.Zone = true;
                    S("name", "Name"); S("zone", "Zone");
                    break;
                case "quests":
                    c.Level = true; c.LevelLabel = "Quest level"; c.Src = true; c.Zone = true;
                    S("name", "Name"); S("level", "Level"); S("zone", "Zone");
                    break;
                case "zones":
                    c.Level = true; c.LevelLabel = "Zone level";
                    S("name", "Name"); S("level", "Level"); S("continent", "Continent");
                    break;
                case "instances":
                    c.Level = true; c.LevelLabel = "Level";
                    S("level", "Level"); S("name", "Name"); c.DefaultSort = "level";
                    break;
                case "recipes":
                    c.Level = true; c.LevelLabel = "Skill level"; c.Learn = HasLearnData();
                    S("level", "Skill level"); S("name", "Name"); c.DefaultSort = "level";
                    break;
                default:
                    S("name", "Name");
                    break;
            }
            if (c.Slot) foreach (var s in ix.Slots) c.Slots.Add(new BrowseChoice(s, s));
            if (c.Zone) foreach (var z in ix.Zones) c.Zones.Add(new BrowseChoice(z.Key.ToString(CultureInfo.InvariantCulture), z.Value));
            if (c.For)
                foreach (var ch in AtlasQuery.Characters())
                    c.Chars.Add(new BrowseChoice(ch.Key, ch.Name + " (" + (string.IsNullOrEmpty(ch.Class) ? "level " : Capital(ch.Class) + ", level ") + ch.Level + ")"));
            if (c.For && c.Chars.Count == 0) c.For = false;
            return c;
        }

        static string Capital(string s) => string.IsNullOrEmpty(s) ? "" : char.ToUpperInvariant(s[0]) + s.Substring(1).ToLowerInvariant();

        // ---- notes: what is missing and what would add it
        static void AddNotes(BrowseResult r, BIndex ix, BrowseQuery q, InvChar chr, List<InvChar> chars)
        {
            if (ix.Cat == "recipes" && q.Learn == "can")
                r.Notes.Add(HasLearnData()
                    ? "Recipes of your characters' professions that the character has not learned and has the skill for (the estimated start of the recipe). Only characters whose profession window Elan's Hub has read are counted: open each profession window in game once."
                    : "Nothing to compare yet: open each profession window in game once (Elan's Hub 1.14.0 or newer) so the Outpost knows which recipes your characters have learned.");
            bool classic = ClassicAtlas.IsReady, lodge = AtlasQuery.LodgeHasData();
            if (ix.Cat == "items")
            {
                if (!classic && (q.How.Length > 0 || r.Sort == "chance")) r.Notes.Add("Download the Classic baseline for drop and vendor sources (Atlas start page).");
                else if (!classic) r.Notes.Add("The Classic baseline is not on this PC: sources (drops, vendors, quests) show only what your game's recipes and the Lodge know.");
                if (q.Src == "lodge" && !lodge) r.Notes.Add("No Lodge observations yet: items the Lodge has seen will show here.");
                if (q.For.Length > 0 && chr == null) r.Notes.Add("That character is not found any more, so the list is not narrowed to what they can use.");
                if (chr != null) r.Notes.Add("Showing what " + chr.Name + " can use: required level up to " + chr.Level + (ItemProficiency.Known(chr.Class) ? " and the armor and weapons the class can wear or wield" : "") + ". Skills learned later (mail and plate at 40) are applied by level.");
                if (!ItemData.Ready) r.Notes.Add("Item details come from your game files and are not ready yet; only the Classic baseline's items are listed.");
            }
            else if (ix.Cat == "creatures" || ix.Cat == "objects" || ix.Cat == "quests")
            {
                if (!classic) r.Notes.Add("The Classic baseline is not on this PC, so levels, zones and spawn data are missing. Download it from the Atlas start page.");
                if (ix.Cat == "objects" && classic == false) r.Notes.Add("Herbs and ore are told apart by what they give; without the Classic baseline or Lodge drops they are listed under Other.");
                if (q.Src == "lodge" && !lodge) r.Notes.Add("No Lodge observations yet.");
            }
            else if ((ix.Cat == "zones" || ix.Cat == "instances") && !classic)
                r.Notes.Add("Level ranges come from the Classic baseline's creatures. Download it from the Atlas start page.");
            if (r.Capped) r.Notes.Add("Only the first " + MaxRows.ToString("N0", CalendarRules.English) + " are listed. Narrow it with the filters.");
        }
    }
}
