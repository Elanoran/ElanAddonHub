using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services.Atlas.Classic;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Maps;

namespace ElansAddonHub.Services.Atlas
{
    // What the page can ask beyond the static services: the Lodge pool of the current session, the user's own characters, the clock.
    // Everything else is read from the layers' own static entry points (ItemData, SpellData, SeenData, ClassicAtlas, MapData).
    public sealed class AtlasContext
    {
        public Func<LodgeAtlas> Lodge = () => null;
        public Func<Dictionary<int, InvItemTotals>> Owned = () => null;       // item id -> what each of the user's characters holds
        public Func<IReadOnlyCollection<int>, LodgeCrafters> Crafters = null;  // "who in the Lodge can craft these recipe spells" (answers from the Armory; asks the server when it does not know yet)
        public Func<List<InvChar>> Characters = () => null;                  // the user's own characters (for "usable by my character" in the browse lists)
        public Func<DateTime> NowUtc = () => DateTime.UtcNow;
        public Func<Dictionary<int, List<AtlasObs.FishSpot>>> OwnFishSpots = () => null;   // fishing spots this PC's Hub observed: uiMapId -> spots
        public Func<Dictionary<int, Dictionary<int, List<AtlasObs.NodePt>>>> OwnNodes = () => null;   // gathering nodes this PC's Hub observed: object -> uiMapId -> nodes
    }

    // ============================================================================================================================
    // Atlas layer F: combines every layer into search results and page models. No UI here, and nothing in here blocks on the network;
    // the first call may load the Classic index from disk, so the page calls it from a worker thread.
    //   AtlasQuery.Search(text)   -> results grouped by kind (items, NPCs, objects, quests, zones, instances, spells, skills, factions)
    //   AtlasQuery.Build(id)      -> the page of "item:123" / "npc:5" / "obj:7" / "quest:9" / "zone:1429" / "instance:36" / "spell:6150" / "skill:171" / "faction:72"
    // A layer that is not there (no Classic download, no Lodge, game data off) only makes its rows absent, and a note says what would turn it on.
    // ============================================================================================================================
    public static partial class AtlasQuery
    {
        public static AtlasContext Context = new AtlasContext();
        public const double TinyChance = 0.1;            // Classic loot below this many percent is folded away
        public const int MaxMarkers = 600, MaxSpawnsPerSource = 40;

        static LodgeAtlas Lodge() { try { return Context.Lodge?.Invoke(); } catch (Exception) { return null; } }
        static bool LodgeHas(LodgeAtlas l) => l != null && l.HasData;
        public static LodgeAtlas LodgeOrNull() => Lodge();
        public static bool LodgeHasData() => LodgeHas(Lodge());
        public static List<InvChar> Characters()
        {
            try { return (Context.Characters?.Invoke() ?? new List<InvChar>()).Where(c => c != null && !c.Hidden && !string.IsNullOrEmpty(c.Key)).OrderByDescending(c => c.Level).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList(); }
            catch (Exception) { return new List<InvChar>(); }
        }
        static readonly Regex IdText = new Regex(@"^\s*(item|npc|obj|object|quest|zone|instance|spell|skill|faction)\s*[:=# ]\s*(\d{1,9})\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // "item:123", also "Item 123" / "npc=5" -> the canonical id; null when the text is not an id
        public static string NormalizeId(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (AtlasBrowse.IsBrowseId(text)) return BrowseQuery.TryParse(text, out var bq) ? bq.ToId() : null;
            if (AtlasCategories.TryHosted(text.Trim(), out var hc, out var hsub)) return hc.Id + ":" + hsub;
            var m = IdText.Match(text);
            if (!m.Success) return null;
            var kind = m.Groups[1].Value.ToLowerInvariant();
            if (kind == "object") kind = "obj";
            var id = kind + ":" + m.Groups[2].Value.TrimStart('0');
            if (id.EndsWith(":")) return null;
            return AtlasIds.TryParse(id, out _, out _) ? id : null;
        }

        public static bool TryParse(string id, out string kind, out int n) => AtlasIds.TryParse(id, out kind, out n);

        // ---- names (the first layer that knows the thing wins: this client's data, then what it has seen, then the Lodge, then the Classic baseline)
        public static string ItemName(int id)
        {
            var ci = ItemData.Get(id);
            if (ci != null && !string.IsNullOrEmpty(ci.Name)) return ci.Name;
            var rev = Lodge()?.Revealed(id);
            if (rev != null && !string.IsNullOrEmpty(rev.Name)) return rev.Name;
            var cl = ClassicAtlas.ItemInfo(id);
            if (cl != null && !string.IsNullOrEmpty(cl.Name)) return cl.Name;
            return "Item " + id.ToString(CultureInfo.InvariantCulture);
        }
        public static int ItemQuality(int id)
        {
            var ci = ItemData.Get(id);
            if (ci != null) return ci.Quality;
            var rev = Lodge()?.Revealed(id);
            if (rev != null) return rev.Quality;
            var cl = ClassicAtlas.ItemInfo(id);
            return cl != null ? cl.Quality : -1;
        }
        public static int ItemIcon(int id) => ItemData.Get(id)?.IconId ?? 0;
        public static string NpcName(int id)
        {
            var s = SeenData.Npc(id);
            if (s != null && !string.IsNullOrEmpty(s.Name)) return s.Name;
            var c = ClassicAtlas.NpcInfo(id);
            return c != null && !string.IsNullOrEmpty(c.Name) ? c.Name : "NPC " + id.ToString(CultureInfo.InvariantCulture);
        }
        public static string ObjectName(int id)
        {
            var s = SeenData.Object(id);
            if (s != null && !string.IsNullOrEmpty(s.Name)) return s.Name;
            var c = ClassicAtlas.ObjectInfo(id);
            return c != null && !string.IsNullOrEmpty(c.Name) ? c.Name : "Object " + id.ToString(CultureInfo.InvariantCulture);
        }
        public static string QuestTitle(int id)
        {
            var s = SeenData.Quest(id);
            if (s != null && !string.IsNullOrEmpty(s.Title)) return s.Title;
            var c = ClassicAtlas.QuestInfo(id);
            return c != null && !string.IsNullOrEmpty(c.Title) ? c.Title : "Quest " + id.ToString(CultureInfo.InvariantCulture);
        }
        public static string ZoneName(int uiMapId)
        {
            var n = MapData.Index?.Name(uiMapId);
            return string.IsNullOrEmpty(n) ? "Zone " + uiMapId.ToString(CultureInfo.InvariantCulture) : n;
        }
        public static string SpellName(int id) => SpellData.Spell(id)?.Name is string n && n.Length > 0 ? n : "Spell " + id.ToString(CultureInfo.InvariantCulture);
        public static string SkillName(int id) => SpellData.Skill(id)?.Name is string n && n.Length > 0 ? n : "Skill " + id.ToString(CultureInfo.InvariantCulture);

        public static AtlasRow ItemRow(int id, string detail = "", string label = "")
            => new AtlasRow { Link = AtlasIds.Item(id), Title = ItemName(id), Quality = ItemQuality(id), IconId = ItemIcon(id), Detail = detail, Label = label, Key = AtlasIds.Item(id) };

        // ---- layer availability (for notes and the start page)
        public static bool HasClassic => ClassicAtlas.IsReady;

        static void LayerNotes(AtlasPageModel m, bool needsClassic, bool needsLodge, bool needsItems = false)
        {
            if (needsItems && !ItemData.Ready)
                m.Notes.Add(ItemData.Status == ItemData.State.Disabled ? "Game data is switched off (Settings > General), so item details are not shown." : "Item details come from your game files and are not ready yet.");
            if (needsClassic && !ClassicAtlas.IsReady)
                m.Notes.Add("The Classic baseline is not on this PC, so only what your game and the Lodge know is listed. Get it from the Atlas start page.");
            if (needsLodge && !LodgeHas(Lodge()))
                m.Notes.Add("No Lodge observations yet: drops and finds you and your Lodge record will show here.");
        }

        // ============================================================================================================================ search

        // 0 exact, 1 prefix, 2 a word starts with it, 3 somewhere inside; -1 no match. q is lower case.
        public static int MatchRank(string name, string q)
        {
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(q)) return -1;
            var n = name.ToLowerInvariant();
            int at = n.IndexOf(q, StringComparison.Ordinal);
            if (at < 0) return -1;
            if (n.Length == q.Length) return 0;
            if (at == 0) return 1;
            char p = n[at - 1];
            return p == ' ' || p == '-' || p == '\'' || p == ':' || p == '(' ? 2 : 3;
        }

        public static AtlasSearchResult Search(string text, int perGroup = 6)
        {
            var res = new AtlasSearchResult { Query = (text ?? "").Trim() };
            if (res.Query.Length == 0) return res;
            var idText = NormalizeId(res.Query);
            if (idText != null && AtlasCategories.IsRouted(idText)) return res;       // the page opens browse and hosted ids itself
            if (idText != null)
            {
                res.ById = true;
                AtlasIds.TryParse(idText, out var k, out var n);
                var g = new AtlasGroup { Kind = k, Title = AtlasKinds.Plural(k) };
                g.Hits.Add(Describe(idText) ?? new AtlasHit { Id = idText, Kind = k, Name = AtlasKinds.Singular(k) + " " + n, Detail = "not in the data on this PC", Rank = 4 });
                g.Total = 1;
                res.Groups.Add(g);
                return res;
            }
            var q = res.Query.ToLowerInvariant();
            if (q.Length < 2) return res;
            var groups = new List<AtlasGroup>();
            void Add(string kind, List<AtlasHit> hits)
            {
                if (hits.Count == 0) return;
                hits.Sort(CompareHits);
                var g = new AtlasGroup { Kind = kind, Title = AtlasKinds.Plural(kind), Total = hits.Count };
                g.Hits.AddRange(hits.Take(perGroup));
                groups.Add(g);
            }
            Add("item", SearchItems(q));
            Add("npc", SearchNpcs(q));
            Add("obj", SearchObjects(q));
            Add("quest", SearchQuests(q));
            Add("zone", SearchZones(q));
            Add("instance", SearchInstances(q));
            Add("spell", SearchSpells(q));
            Add("skill", SearchSkills(q));
            Add("faction", SearchFactions(q));
            // the group with the best match first (an exact name beats a prefix beats a word...), the fixed order of kinds breaks ties
            res.Groups = groups.OrderBy(g => g.Hits[0].Rank).ThenBy(g => Array.IndexOf(AtlasKinds.Order, g.Kind)).ToList();
            // the hosted categories (Talents, Professions) by name: their pages are the first group, before any entity of the same name
            var pages = AtlasCategories.SearchHits(res.Query);
            if (pages.Count > 0)
            {
                pages.Sort(CompareHits);
                var pg = new AtlasGroup { Kind = "page", Title = "Atlas pages", Total = pages.Count };
                pg.Hits.AddRange(pages.Take(perGroup));
                res.Groups.Insert(0, pg);
            }
            return res;
        }

        static int CompareHits(AtlasHit a, AtlasHit b)
        {
            int c = a.Rank.CompareTo(b.Rank);
            if (c != 0) return c;
            c = a.Tier.CompareTo(b.Tier);                  // Forever above Classic baseline
            if (c != 0) return c;
            c = a.Name.Length.CompareTo(b.Name.Length);
            if (c != 0) return c;
            c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : string.CompareOrdinal(a.Id, b.Id);
        }

        static List<AtlasHit> SearchItems(string q)
        {
            var hits = new Dictionary<int, AtlasHit>();
            foreach (var ci in ItemData.Search(q, 40))
            {
                int r = MatchRank(ci.Name, q);
                hits[ci.Id] = new AtlasHit { Id = AtlasIds.Item(ci.Id), Kind = "item", Name = ci.Name, Quality = ci.Quality, IconId = ci.IconId, Detail = ItemDetail(ci), Label = AtlasLabel.Forever, Rank = r < 0 ? 3 : r, Tier = 0 };
            }
            foreach (var h in ClassicAtlas.Search(q, 40, ClassicEntityKind.Item))
            {
                if (hits.ContainsKey(h.Id)) continue;
                var info = ClassicAtlas.ItemInfo(h.Id);
                int r = MatchRank(h.Name, q);
                hits[h.Id] = new AtlasHit { Id = AtlasIds.Item(h.Id), Kind = "item", Name = h.Name, Quality = info?.Quality ?? -1, Detail = h.Detail, Label = AtlasLabel.Classic, Rank = r < 0 ? 3 : r, Tier = 1 };
            }
            return hits.Values.ToList();
        }

        static string ItemDetail(ClientItem ci)
        {
            var parts = new List<string>();
            if (ci.RequiredLevel > 1) parts.Add("Level " + ci.RequiredLevel);
            else if (ci.ItemLevel > 0) parts.Add("Item level " + ci.ItemLevel);
            var slot = ItemNames.Slot(ci.InventoryType);
            var sub = ItemNames.Subclass(ci.ClassId, ci.SubclassId);
            if (ci.Equippable && slot != null) parts.Add(slot);
            if (!string.IsNullOrEmpty(sub)) parts.Add(sub);
            else { var cls = ItemNames.Class(ci.ClassId); if (!string.IsNullOrEmpty(cls) && !parts.Contains(cls)) parts.Add(cls); }
            if (ci.Revealed) parts.Add("revealed");
            return string.Join(" · ", parts);
        }

        static List<AtlasHit> SearchNpcs(string q)
        {
            var hits = new Dictionary<int, AtlasHit>();
            foreach (var s in SeenData.Search(q, 40, "npc"))
            {
                int r = MatchRank(s.Name, q);
                var h = new AtlasHit { Id = "npc:" + s.Id, Kind = "npc", Name = s.Name, Detail = s.Detail ?? "", Label = AtlasLabel.Forever, Rank = r < 0 ? 3 : r, Tier = 0 };
                var c = ClassicAtlas.NpcInfo(s.Id);
                if (c != null) { var lv = AtlasFormat.Levels(c.MinLevel, c.MaxLevel); if (lv.Length > 0) h.Detail = (h.Detail.Length > 0 ? h.Detail + ", " : "") + "level " + lv; }
                hits[s.Id] = h;
            }
            foreach (var c in ClassicAtlas.Search(q, 40, ClassicEntityKind.Npc))
            {
                if (hits.ContainsKey(c.Id)) continue;
                int r = MatchRank(c.Name, q);
                hits[c.Id] = new AtlasHit { Id = "npc:" + c.Id, Kind = "npc", Name = c.Name, Detail = c.Detail, Label = AtlasLabel.Classic, Rank = r < 0 ? 3 : r, Tier = 1 };
            }
            return hits.Values.ToList();
        }

        static List<AtlasHit> SearchObjects(string q)
        {
            var hits = new Dictionary<int, AtlasHit>();
            foreach (var s in SeenData.Search(q, 30, "obj"))
            {
                int r = MatchRank(s.Name, q);
                hits[s.Id] = new AtlasHit { Id = "obj:" + s.Id, Kind = "obj", Name = s.Name, Detail = ObjectKind(SeenData.Object(s.Id)?.Type ?? 0), Label = AtlasLabel.Forever, Rank = r < 0 ? 3 : r, Tier = 0 };
            }
            foreach (var c in ClassicAtlas.Search(q, 30, ClassicEntityKind.Object))
            {
                if (hits.ContainsKey(c.Id)) continue;
                int r = MatchRank(c.Name, q);
                hits[c.Id] = new AtlasHit { Id = "obj:" + c.Id, Kind = "obj", Name = c.Name, Detail = ObjectKind(ClassicAtlas.ObjectInfo(c.Id)?.Type ?? 0), Label = AtlasLabel.Classic, Rank = r < 0 ? 3 : r, Tier = 1 };
            }
            return hits.Values.ToList();
        }

        public static string ObjectKind(int type)
        {
            switch (type)
            {
                case 3: return "chest or node";
                case 25: return "fishing hole";
                case 2: return "quest object";
                case 19: return "mailbox";
                case 7: return "chair";
                case 5: return "decoration";
                case 50: return "gathering node";
                case 0: return "";
                default: return "object";
            }
        }

        static List<AtlasHit> SearchQuests(string q)
        {
            var hits = new Dictionary<int, AtlasHit>();
            foreach (var s in SeenData.Search(q, 40, "quest"))
            {
                int r = MatchRank(s.Name, q);
                hits[s.Id] = new AtlasHit { Id = "quest:" + s.Id, Kind = "quest", Name = s.Name, Detail = s.Detail ?? "", Label = AtlasLabel.Forever, Rank = r < 0 ? 3 : r, Tier = 0 };
            }
            foreach (var c in ClassicAtlas.Search(q, 40, ClassicEntityKind.Quest))
            {
                if (hits.ContainsKey(c.Id)) continue;
                int r = MatchRank(c.Name, q);
                hits[c.Id] = new AtlasHit { Id = "quest:" + c.Id, Kind = "quest", Name = c.Name, Detail = c.Detail, Label = AtlasLabel.Classic, Rank = r < 0 ? 3 : r, Tier = 1 };
            }
            return hits.Values.ToList();
        }

        static List<AtlasHit> SearchZones(string q)
        {
            var res = new List<AtlasHit>();
            var idx = MapData.Index;
            if (idx == null) return res;
            foreach (var m in idx.UiMaps)
            {
                if (m.System != 0 || m.Type == UiMapInfo.Cosmic || m.Type == UiMapInfo.Dungeon) continue;
                int r = MatchRank(m.Name, q);
                if (r < 0) continue;
                var parent = m.ParentId > 0 ? idx.Name(m.ParentId) : "";
                res.Add(new AtlasHit { Id = "zone:" + m.Id, Kind = "zone", Name = m.Name, Detail = m.TypeName + (parent.Length > 0 && m.Type == UiMapInfo.Zone ? " in " + parent : ""), Label = AtlasLabel.Forever, Rank = r, Tier = 0 });
            }
            return res;
        }

        static List<AtlasHit> SearchInstances(string q)
        {
            var res = new List<AtlasHit>();
            var idx = MapData.Index;
            if (idx == null) return res;
            foreach (var i in idx.Instances)
            {
                if (i.Placeholder) continue;
                int r = MatchRank(i.Name, q);
                if (r < 0) continue;
                res.Add(new AtlasHit { Id = "instance:" + i.MapId, Kind = "instance", Name = i.Name, Detail = InstanceDetail(i), Label = AtlasLabel.Forever, Rank = r, Tier = 0 });
            }
            return res;
        }

        public static string InstanceDetail(InstanceInfo i)
            => string.Join(" · ", new[] { i.Kind, i.MaxPlayers > 0 ? i.MaxPlayers + " players" : "" }.Where(s => !string.IsNullOrEmpty(s)));

        static List<AtlasHit> SearchSpells(string q)
        {
            var res = new List<AtlasHit>();
            var sn = SpellData.Spells;
            if (sn == null) return res;
            foreach (var s in sn.Spells.Values)
            {
                if (string.IsNullOrEmpty(s.Name)) continue;
                int r = MatchRank(s.Name, q);
                if (r < 0) continue;
                bool recipe = sn.RecipeOf(s.Id) != null;
                res.Add(new AtlasHit
                {
                    Id = "spell:" + s.Id, Kind = "spell", Name = s.Name, IconId = s.IconId, Label = AtlasLabel.Forever, Rank = r, Tier = recipe ? 0 : 1 - 1,
                    Detail = string.Join(" · ", new[] { s.Rank, recipe ? "recipe" : "" }.Where(x => !string.IsNullOrEmpty(x))),
                });
                if (res.Count > 4000) break;
            }
            // recipes before plain spells of the same match, then the lower rank first
            return res.OrderBy(h => h.Rank).ThenBy(h => h.Detail.Contains("recipe") ? 0 : 1).ThenBy(h => h.Name.Length).ThenBy(h => h.Name, StringComparer.OrdinalIgnoreCase).ThenBy(h => h.Id, StringComparer.Ordinal).Take(60).ToList();
        }

        static List<AtlasHit> SearchSkills(string q)
        {
            var res = new List<AtlasHit>();
            var sn = SpellData.Spells;
            if (sn == null) return res;
            foreach (var s in sn.Skills.Values)
            {
                int r = MatchRank(s.Name, q);
                if (r < 0) continue;
                res.Add(new AtlasHit { Id = "skill:" + s.Id, Kind = "skill", Name = s.Name, IconId = s.IconId, Label = AtlasLabel.Forever, Rank = r, Tier = s.IsProfession ? 0 : 1, Detail = s.IsProfession ? "profession" : SkillCategory(s.Category) });
            }
            return res;
        }

        public static string SkillCategory(int c)
        {
            switch (c)
            {
                case SkillInfo.CatAttributes: return "attribute";
                case SkillInfo.CatWeapon: return "weapon skill";
                case SkillInfo.CatClass: return "class skill";
                case SkillInfo.CatArmor: return "armor skill";
                case SkillInfo.CatSecondary: return "secondary skill";
                case SkillInfo.CatLanguage: return "language";
                case SkillInfo.CatProfession: return "profession";
                default: return "skill";
            }
        }

        static List<AtlasHit> SearchFactions(string q)
        {
            var res = new List<AtlasHit>();
            var fs = SpellData.Factions;
            if (fs == null) return res;
            foreach (var f in fs.Factions.Values)
            {
                int r = MatchRank(f.Name, q);
                if (r < 0) continue;
                res.Add(new AtlasHit { Id = "faction:" + f.Id, Kind = "faction", Name = f.Name, Label = AtlasLabel.Forever, Rank = r, Tier = 0, Detail = f.ParentId > 0 ? "in " + (FactionData.Name(f.ParentId) ?? "a group") : "" });
            }
            return res;
        }

        // ============================================================================================================================ describe / build

        // the search hit of one id (the name and a line of detail), null when no layer knows it
        public static AtlasHit Describe(string id)
        {
            if (!AtlasIds.TryParse(id, out var kind, out var n)) return null;
            switch (kind)
            {
                case "item":
                    {
                        var ci = ItemData.Get(n);
                        if (ci != null) return new AtlasHit { Id = id, Kind = kind, Name = ci.Name, Quality = ci.Quality, IconId = ci.IconId, Detail = ItemDetail(ci), Label = AtlasLabel.Forever };
                        var cl = ClassicAtlas.ItemInfo(n);
                        var rev = Lodge()?.Revealed(n);
                        if (rev != null) return new AtlasHit { Id = id, Kind = kind, Name = rev.Name ?? ("Item " + n), Quality = rev.Quality, Detail = "revealed by the Lodge", Label = AtlasLabel.Forever };
                        return cl == null ? null : new AtlasHit { Id = id, Kind = kind, Name = cl.Name, Quality = cl.Quality, Detail = "Item level " + cl.ItemLevel, Label = AtlasLabel.Classic, Tier = 1 };
                    }
                case "npc":
                    {
                        var s = SeenData.Npc(n); var c = ClassicAtlas.NpcInfo(n);
                        if (s != null) return new AtlasHit { Id = id, Kind = kind, Name = s.Name, Detail = SeenSnapshot.Hit(s).Detail, Label = AtlasLabel.Forever };
                        return c == null ? null : new AtlasHit { Id = id, Kind = kind, Name = c.Name, Detail = "level " + AtlasFormat.Levels(c.MinLevel, c.MaxLevel), Label = AtlasLabel.Classic, Tier = 1 };
                    }
                case "obj":
                    {
                        var s = SeenData.Object(n); var c = ClassicAtlas.ObjectInfo(n);
                        if (s != null) return new AtlasHit { Id = id, Kind = kind, Name = s.Name, Detail = ObjectKind(s.Type), Label = AtlasLabel.Forever };
                        return c == null ? null : new AtlasHit { Id = id, Kind = kind, Name = c.Name, Detail = ObjectKind(c.Type), Label = AtlasLabel.Classic, Tier = 1 };
                    }
                case "quest":
                    {
                        var s = SeenData.Quest(n); var c = ClassicAtlas.QuestInfo(n);
                        if (s != null) return new AtlasHit { Id = id, Kind = kind, Name = s.Title, Detail = s.Level > 0 ? "level " + s.Level : "", Label = AtlasLabel.Forever };
                        return c == null ? null : new AtlasHit { Id = id, Kind = kind, Name = c.Title, Detail = "quest level " + c.QuestLevel, Label = AtlasLabel.Classic, Tier = 1 };
                    }
                case "zone":
                    {
                        var z = MapData.Index?.Get(n);
                        return z == null ? null : new AtlasHit { Id = id, Kind = kind, Name = z.Name, Detail = z.TypeName, Label = AtlasLabel.Forever };
                    }
                case "instance":
                    {
                        var i = MapData.Index?.Instance(n);
                        return i == null ? null : new AtlasHit { Id = id, Kind = kind, Name = i.Name, Detail = InstanceDetail(i), Label = AtlasLabel.Forever };
                    }
                case "spell":
                    {
                        var s = SpellData.Spell(n);
                        return s == null ? null : new AtlasHit { Id = id, Kind = kind, Name = s.Name, IconId = s.IconId, Detail = s.Rank ?? "", Label = AtlasLabel.Forever };
                    }
                case "skill":
                    {
                        var s = SpellData.Skill(n);
                        return s == null ? null : new AtlasHit { Id = id, Kind = kind, Name = s.Name, IconId = s.IconId, Detail = s.IsProfession ? "profession" : SkillCategory(s.Category), Label = AtlasLabel.Forever };
                    }
                case "faction":
                    {
                        var f = FactionData.Faction(n);
                        return f == null ? null : new AtlasHit { Id = id, Kind = kind, Name = f.Name, Label = AtlasLabel.Forever };
                    }
            }
            return null;
        }

        public static Action<string> BuildHookForTest;       // self-test: runs on the building thread before a page is built (to make it slow)

        // The page of an id. Never throws: a layer that fails leaves a note and the page shows what the others gave.
        public static AtlasPageModel Build(string id)
        {
            BuildHookForTest?.Invoke(id);
            if (!AtlasIds.TryParse(id, out var kind, out var n))
                return new AtlasPageModel { Id = id ?? "", NotFound = true, Title = "Not an Atlas link", Message = "\"" + (id ?? "") + "\" is not something Atlas can open. Try a name, or a link like item:2589." };
            // the Lodge pool is filled on another thread: a page built while a frame lands can trip over it once, so it is built again
            for (int attempt = 1; ; attempt++)
            {
                try { return BuildOnce(id, kind, n); }
                catch (InvalidOperationException) when (attempt < 3) { System.Threading.Thread.Sleep(20); }
                catch (Exception e)
                {
                    Util.Log("atlas page " + id + ": " + e);
                    return new AtlasPageModel { Id = id, Kind = kind, NotFound = true, Title = AtlasKinds.Singular(kind) + " " + n, Message = "This page could not be built (" + e.Message + ")." };
                }
            }
        }

        static AtlasPageModel BuildOnce(string id, string kind, int n)
        {
            {
                AtlasPageModel m;
                switch (kind)
                {
                    case "item": m = BuildItem(n); break;
                    case "npc": m = BuildNpc(n); break;
                    case "obj": m = BuildObject(n); break;
                    case "quest": m = BuildQuest(n); break;
                    case "zone": m = BuildZone(n); break;
                    case "instance": m = BuildInstance(n); break;
                    case "spell": m = BuildSpell(n); break;
                    case "skill": m = BuildSkill(n); break;
                    case "faction": m = BuildFaction(n); break;
                    default: m = new AtlasPageModel { NotFound = true, Title = id, Message = "Atlas cannot open " + kind + " links yet." }; break;
                }
                m.Id = id; m.Kind = kind;
                return m;
            }
        }

        static AtlasPageModel NotFoundPage(string kind, int n, string why)
            => new AtlasPageModel { Kind = kind, NotFound = true, Title = AtlasKinds.Singular(kind) + " " + n, Message = why };

        static AtlasSection Section(AtlasPageModel m, string key, string title, string empty = "")
        {
            var s = new AtlasSection { Key = key, Title = title, Empty = empty };
            m.Sections.Add(s);
            return s;
        }

        static void AddLayer(AtlasPageModel m, string layer) { if (!m.Layers.Contains(layer)) m.Layers.Add(layer); }

        // row order inside a tier: drops, skinning, vendors, quests, recipes
        static int GroupOrder(string type)
        {
            switch (type)
            {
                case "Drop": case "Object": case "Fishing": return 0;
                case "Skinning": case "Pickpocketing": return 1;
                case "Vendor": return 2;
                case "Quest": return 3;
                case "Recipe": return 4;
                default: return 5;
            }
        }

        static void SortRows(List<AtlasRow> rows)
        {
            var sorted = rows.OrderBy(r => r.Tier).ThenBy(r => GroupOrder(r.Type)).ThenByDescending(r => r.Sort).ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Key, StringComparer.Ordinal).ToList();
            rows.Clear(); rows.AddRange(sorted);
        }

        // folds the Classic loot rows under TinyChance. With nothing else to show, the best few stay visible so the table is not empty.
        static void FoldTiny(AtlasSection s, List<AtlasRow> rows, List<AtlasRow> tiny, int showIfAlone = 6)
        {
            SortRows(rows);
            tiny = tiny.OrderByDescending(r => r.Sort).ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Key, StringComparer.Ordinal).ToList();
            s.Rows.AddRange(rows);
            if (tiny.Count == 0) return;
            if (rows.Count == 0)
            {
                s.Rows.AddRange(tiny.Take(showIfAlone));
                s.Hidden.AddRange(tiny.Skip(showIfAlone));
                if (s.Hidden.Count > 0) s.HiddenText = "+ " + s.Hidden.Count.ToString("N0", CalendarRules.English) + " more world-drop sources under " + AtlasFormat.Percent(TinyChance);
            }
            else
            {
                s.Hidden.AddRange(tiny);
                s.HiddenText = "+ " + s.Hidden.Count.ToString("N0", CalendarRules.English) + " world-drop source" + (s.Hidden.Count == 1 ? "" : "s") + " under " + AtlasFormat.Percent(TinyChance);
            }
        }
    }
}
