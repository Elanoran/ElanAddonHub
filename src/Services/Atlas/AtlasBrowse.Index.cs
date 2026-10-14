using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services.Atlas.Classic;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Maps;

namespace ElansAddonHub.Services.Atlas
{
    // One thing in a browse list, with everything the filters and the sorts need (so a filter never goes back to the layers).
    public sealed class BEntry
    {
        public string Kind = "", Name = "", Lower = "", Detail = "", Extra = "", Group = "", SlotName = "", ZoneName = "", SubLabel = "";
        public string Link = "";                  // where a row opens when it is not the page of the entry itself (a profession opens the Professions page)
        public int Id;
        public int Level, Level2;                 // the range the level filter looks at (an item's required level, a creature's levels, a zone's creature levels, a recipe's skill)
        public int Quality = -1, ILvl, Cls = -1, Sub = -1, Inv, Icon, Zone, Rank, Type, Flags, Skill;
        public bool Client, Classic, Lodge;       // which layers know it: the player's game, the Classic baseline, the Lodge's observations
        public int Src;                           // items: bit per AtlasBrowse.Hows
        public double Chance; public bool ChanceLodge;
        public long Price;
        public string[] SrcParts;                 // items: the text of each way to get it (by Hows index), null where there is none
        public int Tier => Client ? 0 : Lodge && !Classic ? 0 : 2;
    }

    // a node of the category tree: "items" > "weapons" > "one-handed-swords". Pred is absolute (it already includes the parents' conditions).
    public sealed class BNode
    {
        public string Slug = "", Title = "";
        public Func<BEntry, bool> Pred = e => true;
        public List<BNode> Kids = new List<BNode>();
    }

    public sealed class BIndex
    {
        public string Cat = "", Noun = "", Token = "";
        public List<BEntry> All = new List<BEntry>();
        Dictionary<int, BEntry> byId;
        public BEntry ById(int id)
        {
            lock (All) { if (byId == null) { byId = new Dictionary<int, BEntry>(); foreach (var e in All) byId[e.Id] = e; } return byId.TryGetValue(id, out var x) ? x : null; }
        }
        public BNode Root = new BNode();
        public List<string> Slots = new List<string>();
        public List<KeyValuePair<int, string>> Zones = new List<KeyValuePair<int, string>>();
        readonly Dictionary<BNode, int> counts = new Dictionary<BNode, int>();
        public int CountOf(BNode n)
        {
            lock (counts)
            {
                if (!counts.TryGetValue(n, out var c)) { c = ReferenceEquals(n, Root) ? All.Count : All.Count(n.Pred); counts[n] = c; }
                return c;
            }
        }
    }

    public static partial class AtlasBrowse
    {
        static readonly object cacheLock = new object(), buildLock = new object();
        static readonly Dictionary<string, BIndex> cache = new Dictionary<string, BIndex>();

        // what every index was built from: when any of it is another object (or the Lodge pool moved on), the indexes are built again
        static string Token()
        {
            string R(object o) => o == null ? "-" : RuntimeHelpers.GetHashCode(o).ToString(CultureInfo.InvariantCulture);
            var L = AtlasQuery.LodgeOrNull();
            return string.Join("|", GatherNodes.OwnToken(), R(ItemData.Snapshot), R(SpellData.Data), R(SeenData.Snapshot), R(MapData.Index), R(ClassicAtlas.Current), L == null ? "-" : R(L) + "," + L.Rev + "," + L.Updated + "," + L.FindCount);
        }

        public static void ForgetForTest() { lock (cacheLock) cache.Clear(); }

        static BIndex Index(string cat)
        {
            var tok = Token();
            lock (cacheLock) if (cache.TryGetValue(cat, out var hit) && hit.Token == tok) return hit;
            lock (buildLock)
            {
                lock (cacheLock) if (cache.TryGetValue(cat, out var hit) && hit.Token == tok) return hit;
                var ix = Build(cat, tok);
                lock (cacheLock) { if (ix != null) cache[cat] = ix; else cache.Remove(cat); }
                return ix;
            }
        }

        sealed class Ctx
        {
            public ClassicIndex Classic; public AtlasDerived Derived; public LodgeAtlas L; public MapIndex Map;
            public readonly Dictionary<int, int> ObjCats = new Dictionary<int, int>();
            public readonly Dictionary<int, string> ContinentOf = new Dictionary<int, string>();
            public const int Herb = 1, Ore = 2, Chest = 3, Fishing = 4, Other = 5;
        }

        static BIndex Build(string cat, string tok)
        {
            var x = new Ctx { Classic = ClassicAtlas.Current, L = AtlasQuery.LodgeOrNull(), Map = MapData.Index };
            x.Derived = AtlasDerived.For(x.Classic);
            BIndex ix;
            switch (cat)
            {
                case "items": if (ItemData.Snapshot == null && x.Classic == null) return null; ix = BuildItems(x); break;
                case "creatures": if (SeenData.Snapshot == null && x.Classic == null) return null; ix = BuildCreatures(x); break;
                case "objects": if (SeenData.Snapshot == null && x.Classic == null) return null; ix = BuildObjects(x); break;
                case "quests": if (SeenData.Snapshot == null && x.Classic == null) return null; ix = BuildQuests(x); break;
                case "zones": if (x.Map == null) return null; ix = BuildZones(x); break;
                case "instances": if (x.Map == null) return null; ix = BuildInstances(x); break;
                case "spells": if (SpellData.Spells == null) return null; ix = BuildSpells(x); break;
                case "skills": if (SpellData.Spells == null) return null; ix = BuildSkills(x); break;
                case "recipes": if (SpellData.Spells == null) return null; ix = BuildRecipes(x); break;
                case "factions": if (SpellData.Factions == null) return null; ix = BuildFactions(x); break;
                default: return null;
            }
            ix.Cat = cat; ix.Token = tok;
            ix.Zones = ix.All.Where(e => e.Zone > 0).Select(e => e.Zone).Distinct().Select(z => new KeyValuePair<int, string>(z, x.Map?.Name(z) ?? "Zone " + z)).OrderBy(z => z.Value, StringComparer.OrdinalIgnoreCase).ToList();
            return ix;
        }

        static string Slugify(string s)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var ch in (s ?? "").ToLowerInvariant()) { if (char.IsLetterOrDigit(ch)) sb.Append(ch); else if (sb.Length > 0 && sb[sb.Length - 1] != '-') sb.Append('-'); }
            return sb.ToString().Trim('-');
        }

        static BEntry Entry(string kind, int id, string name)
            => new BEntry { Kind = kind, Id = id, Name = name ?? "", Lower = (name ?? "").ToLowerInvariant() };

        // kids by a label: one node per distinct label that some entry has
        static void KidsBy(BNode parent, IEnumerable<BEntry> entries, Func<BEntry, string> label, Func<BEntry, bool> parentPred = null)
        {
            parentPred = parentPred ?? parent.Pred;
            foreach (var g in entries.Where(parentPred).GroupBy(label).Where(g => !string.IsNullOrEmpty(g.Key)).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                var key = g.Key;
                parent.Kids.Add(new BNode { Slug = Slugify(key), Title = key, Pred = e => parentPred(e) && label(e) == key });
            }
        }

        // ============================================================================================================================ items
        static string Plural(string n)
        {
            switch (n)
            {
                case "Staff": return "Staves";
                case "Cloth": case "Leather": case "Mail": case "Plate": case "Thrown": case "Miscellaneous": return n;
                default: return n + "s";
            }
        }

        // "One-Handed Swords", "Bows", "Cloth", "Potion" ... from the names the client's tables give (ItemNames)
        public static string SubLabel(int cls, int sub)
        {
            if (sub < 0) return "";
            if (cls == 2)
            {
                var n = ItemNames.Subclass(2, sub);
                if (n == null) return "Other weapons";
                if (sub == 14) return "Other weapons";
                if (sub == 0 || sub == 4 || sub == 7) return "One-Handed " + Plural(n);
                if (sub == 1 || sub == 5 || sub == 8) return "Two-Handed " + Plural(n);
                return Plural(n);
            }
            if (cls == 4)
            {
                if (sub == 0) return "Jewelry, cloaks and other";
                var n = ItemNames.Subclass(4, sub);
                return n == null ? "Other armor" : Plural(n);
            }
            return ItemNames.BrowseSubclass(cls, sub) ?? "";
        }

        static BIndex BuildItems(Ctx x)
        {
            var ix = new BIndex { Noun = "item" };
            var have = new HashSet<int>();
            var snap = ItemData.Snapshot;
            if (snap != null)
                foreach (var ci in snap.Items.Values)
                {
                    if (string.IsNullOrEmpty(ci.Name) || ItemSnapshot.IsPlaceholder(ci.Name)) continue;
                    have.Add(ci.Id);
                    var e = Entry("item", ci.Id, ci.Name);
                    e.Client = true; e.Quality = ci.Quality; e.ILvl = ci.ItemLevel; e.Level = Math.Max(1, ci.RequiredLevel); e.Level2 = e.Level;
                    e.Cls = ci.ClassId; e.Sub = ci.SubclassId; e.Inv = ci.InventoryType; e.Icon = ci.IconId; e.Price = ci.SellPrice;
                    e.Lodge = ci.Revealed;
                    e.SlotName = ci.Equippable ? ItemNames.Slot(ci.InventoryType) ?? "" : "";
                    e.SubLabel = SubLabel(ci.ClassId, ci.SubclassId);
                    var kindText = ci.ClassId == 2 || ci.ClassId == 4 ? e.SubLabel : string.Join(" · ", new[] { ItemNames.Class(ci.ClassId), e.SubLabel }.Where(t => !string.IsNullOrEmpty(t)));
                    e.Detail = string.Join(" · ", new[] { ci.RequiredLevel > 1 ? "Level " + ci.RequiredLevel : "", ci.ItemLevel > 0 ? "Item level " + ci.ItemLevel : "", e.SlotName, kindText }.Where(t => !string.IsNullOrEmpty(t)));
                    ix.All.Add(e);
                }
            if (x.Classic != null)
                foreach (var kv in x.Classic.Items)
                {
                    var ci = kv.Value;
                    if (have.Contains(kv.Key) || string.IsNullOrEmpty(ci.Name)) continue;
                    var e = Entry("item", ci.Id, ci.Name);
                    e.Classic = true; e.Quality = ci.Quality; e.ILvl = ci.ItemLevel; e.Level = Math.Max(1, ci.RequiredLevel); e.Level2 = e.Level;
                    e.Detail = string.Join(" · ", new[] { ci.RequiredLevel > 1 ? "Level " + ci.RequiredLevel : "", ci.ItemLevel > 0 ? "Item level " + ci.ItemLevel : "", "only in the Classic baseline" }.Where(p => p.Length > 0));
                    ix.All.Add(e);
                }
            foreach (var e in ix.All)
            {
                if (x.Classic != null && !e.Classic && x.Classic.ItemInfo(e.Id) != null) e.Classic = true;
                FillSources(e, x);
            }

            var root = ix.Root; root.Title = "Items";
            void Top(string slug, string title, Func<BEntry, bool> pred, bool kids = true)
            {
                var n = new BNode { Slug = slug, Title = title, Pred = pred };
                if (kids) KidsBy(n, ix.All, e => e.SubLabel);
                if (ix.All.Any(pred)) root.Kids.Add(n);
            }
            Top("weapons", "Weapons", e => e.Cls == 2);
            Top("armor", "Armor", e => e.Cls == 4);
            Top("consumables", "Consumables", e => e.Cls == 0);
            Top("trade-goods", "Trade goods", e => e.Cls == 7);
            Top("recipes", "Recipes", e => e.Cls == 9);
            Top("containers", "Containers", e => e.Cls == 1 || e.Cls == 11);
            Top("quest-items", "Quest items", e => e.Cls == 12, false);
            Top("projectiles", "Projectiles", e => e.Cls == 6);
            Top("gems", "Gems", e => e.Cls == 3, false);
            Top("miscellaneous", "Miscellaneous", e => e.Cls >= 0 && e.Cls != 0 && e.Cls != 1 && e.Cls != 2 && e.Cls != 3 && e.Cls != 4 && e.Cls != 6 && e.Cls != 7 && e.Cls != 9 && e.Cls != 11 && e.Cls != 12, false);
            Top("unrevealed", "Only in the Classic baseline", e => !e.Client, false);
            ix.Slots = ix.All.Select(e => e.SlotName).Where(s => s.Length > 0).Distinct().OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
            return ix;
        }

        // ---- where an item comes from (the Lodge's observations, the Classic baseline, the client's recipes)
        static int ObjectCat(Ctx x, int id)
        {
            if (x.ObjCats.TryGetValue(id, out var c)) return c;
            var seen = SeenData.Object(id); var cl = x.Classic?.ObjectInfo(id);
            int type = seen?.Type ?? cl?.Type ?? 0;
            string name = seen?.Name ?? cl?.Name ?? "";
            var items = new List<int>();
            if (x.Derived != null) foreach (var le in x.Derived.LootOf(ClassicSourceKind.ObjectLoot, id)) items.Add(le.ItemId);
            var lc = x.L?.Loot("obj", id);
            if (lc != null) items.AddRange(lc.Items.Keys);
            bool herb = false, ore = false;
            foreach (var it in items)
            {
                var ci = ItemData.Get(it);
                if (ci != null && ci.ClassId == 7 && ci.SubclassId == 9) herb = true;
                else if (ci != null && ci.ClassId == 7 && ci.SubclassId == 7) ore = true;
            }
            if (type == 25) c = Ctx.Fishing;
            else if (herb) c = Ctx.Herb;
            else if (ore || OreName.IsMatch(name)) c = Ctx.Ore;
            else if (type == 3 && (ChestName.IsMatch(name) || items.Count > 0)) c = Ctx.Chest;
            else if (ChestName.IsMatch(name)) c = Ctx.Chest;
            else c = Ctx.Other;
            x.ObjCats[id] = c;
            return c;
        }
        static readonly Regex OreName = new Regex(@"\b(vein|deposit|ore|mineral)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly Regex ChestName = new Regex(@"\b(chest|crate|barrel|box|cache|footlocker|trunk|coffer|strongbox|lockbox|basket|sack|cask|urn)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        static void FillSources(BEntry e, Ctx x)
        {
            int id = e.Id, bits = 0;
            var drops = new HashSet<long>(); var vend = new HashSet<int>(); var quests = new HashSet<int>();
            int containers = 0, gathered = 0, tiny = 0;
            double lodgeChance = 0, classicChance = 0;
            void Obj(int objId)
            {
                switch (ObjectCat(x, objId))
                {
                    case Ctx.Fishing: bits |= 1 << 4; break;
                    case Ctx.Herb: case Ctx.Ore: bits |= 1 << 6; gathered++; break;
                    default: bits |= 1 << 5; containers++; break;
                }
            }
            var L = x.L;
            if (L != null)
            {
                foreach (var d in L.Drops(id))
                {
                    e.Lodge = true;
                    if (d.Kind == "fish") bits |= 1 << 4;
                    else if (d.Kind == "obj") Obj(d.Id);
                    else { bits |= 1; drops.Add(d.Id); if (d.Chance > lodgeChance) lodgeChance = d.Chance; }
                }
                foreach (var v in L.Vendors(id)) { e.Lodge = true; vend.Add(v.NpcId); }
                foreach (var q in L.QuestsRewarding(id)) { e.Lodge = true; quests.Add(q.QuestId); }
                if (L.Finds(id).Count > 0 || L.Revealed(id) != null) e.Lodge = true;
            }
            if (x.Classic != null)
                foreach (var s in x.Classic.SourcesOf(id))
                {
                    switch (s.Kind)
                    {
                        case ClassicSourceKind.NpcDrop: case ClassicSourceKind.NpcSkin: case ClassicSourceKind.NpcPickpocket:
                            if (s.Chance < AtlasQuery.TinyChance) { tiny++; break; }
                            bits |= 1; drops.Add(s.SourceId); if (!s.QuestOnly && s.Chance > classicChance) classicChance = s.Chance;
                            break;
                        case ClassicSourceKind.ObjectLoot: Obj(s.SourceId); break;
                        case ClassicSourceKind.Vendor: vend.Add(s.SourceId); break;
                        case ClassicSourceKind.QuestReward: case ClassicSourceKind.QuestChoice: quests.Add(s.SourceId); break;
                    }
                }
            bool worldDrop = false;
            if ((bits & 1) == 0 && tiny > 0) { bits |= 1; worldDrop = true; }
            if (vend.Count > 0) bits |= 1 << 1;
            if (quests.Count > 0) bits |= 1 << 2;
            string crafted = null;
            var recipes = SpellData.RecipesProducing(id);
            if (recipes.Count > 0)
            {
                bits |= 1 << 3;
                var r = recipes.OrderBy(a => a.MinRank).First();
                crafted = "Crafted · " + (SpellData.Skill(r.SkillId)?.Name ?? "Profession") + (r.MinRank > 0 ? " " + r.MinRank : "") + (recipes.Count > 1 ? " (+" + (recipes.Count - 1) + ")" : "");
            }
            e.Src = bits;
            e.Chance = lodgeChance > 0 ? lodgeChance : classicChance; e.ChanceLodge = lodgeChance > 0;
            if (bits == 0) return;
            var p = new string[7];
            if ((bits & 1) != 0)
                p[0] = worldDrop ? "Drop · world drop" : "Drop · " + (e.Chance > 0 ? AtlasFormat.Percent(e.Chance) + " " + (e.ChanceLodge ? "Lodge" : "Classic") : AtlasFormat.Count(drops.Count, "source", "sources"));
            if ((bits & 2) != 0) p[1] = "Vendor · " + AtlasFormat.Count(vend.Count, "vendor", "vendors");
            if ((bits & 4) != 0) p[2] = "Quest reward · " + AtlasFormat.Count(quests.Count, "quest", "quests");
            p[3] = crafted;
            if ((bits & 16) != 0) p[4] = "Fishing";
            if ((bits & 32) != 0) p[5] = "Container" + (containers > 1 ? " · " + containers + " kinds" : "");
            if ((bits & 64) != 0) p[6] = "Gathered" + (gathered > 1 ? " · " + gathered + " kinds" : "");
            e.SrcParts = p;
        }

        // ============================================================================================================================ creatures
        static int ZoneOfSpawns(Ctx x, IReadOnlyList<ClassicSpawn> spawns)
        {
            if (x.Map == null || spawns == null) return 0;
            for (int i = 0; i < spawns.Count && i < 3; i++)
            {
                var p = x.Map.WorldToMap(spawns[i].Map, spawns[i].X, spawns[i].Y);
                if (p != null) return p.Value.UiMapId;
            }
            return 0;
        }

        static BIndex BuildCreatures(Ctx x)
        {
            var ix = new BIndex { Noun = "creature" };
            var seen = SeenData.Snapshot;
            var ids = new HashSet<int>();
            if (seen != null) foreach (var k in seen.Npcs.Keys) ids.Add(k);
            if (x.Classic != null) foreach (var k in x.Classic.Npcs.Keys) ids.Add(k);
            foreach (var id in ids.OrderBy(i => i))
            {
                var s = SeenData.Npc(id); var c = x.Classic?.NpcInfo(id);
                var name = !string.IsNullOrEmpty(s?.Name) ? s.Name : c?.Name;
                if (string.IsNullOrEmpty(name)) continue;
                var e = Entry("npc", id, name);
                e.Client = s != null; e.Classic = c != null;
                e.Rank = s?.Rank ?? c?.Rank ?? 0; e.Type = s?.Type ?? c?.Type ?? 0; e.Flags = s != null ? (int)s.Flags0 : c?.NpcFlags ?? 0;
                if (c != null) { e.Level = c.MinLevel; e.Level2 = c.MaxLevel; }
                e.Zone = ZoneOfSpawns(x, c?.Spawns);
                if (e.Zone == 0 && x.L != null) { var sp = x.L.Spawns(id); if (sp.Count > 0) e.Zone = sp[0].Zone; }
                if (x.L != null && (x.L.Npc(id) != null || x.L.Spawns(id).Count > 0 || x.L.Loot("npc", id) != null)) e.Lodge = true;
                e.ZoneName = e.Zone > 0 ? x.Map?.Name(e.Zone) ?? "" : "";
                var title = !string.IsNullOrEmpty(s?.Title) ? s.Title : c?.SubName ?? "";
                e.Detail = string.Join(" · ", new[] { e.Level > 0 ? "Level " + AtlasFormat.Levels(e.Level, e.Level2) : "", AtlasFormat.RankName(e.Rank), title.Length > 0 ? "<" + title + ">" : "", AtlasFormat.CreatureType(e.Type) }.Where(t => t.Length > 0));
                e.Extra = e.ZoneName;
                ix.All.Add(e);
            }
            var root = ix.Root; root.Title = "Creatures";
            void Add(string slug, string title, Func<BEntry, bool> pred) { if (ix.All.Any(pred)) root.Kids.Add(new BNode { Slug = slug, Title = title, Pred = pred }); }
            Add("vendors", "Vendors", e => (e.Flags & 0x80) != 0);
            Add("trainers", "Trainers", e => (e.Flags & 0x10) != 0);
            Add("quest-givers", "Quest givers", e => (e.Flags & 0x2) != 0);
            foreach (var t in ix.All.Select(e => e.Type).Distinct().OrderBy(t => t))
            {
                var n = AtlasFormat.CreatureType(t);
                if (n.Length == 0) continue;
                int tt = t;
                Add("type-" + Slugify(n), n, e => e.Type == tt);
            }
            return ix;
        }

        // ============================================================================================================================ objects
        static string ObjCatTitle(int c)
        {
            switch (c) { case Ctx.Herb: return "Herb"; case Ctx.Ore: return "Ore"; case Ctx.Chest: return "Chest or container"; case Ctx.Fishing: return "Fishing hole"; default: return "Other"; }
        }

        // herbs | ore | chests | fishing | other for an object id ("" when the objects index cannot be built); builds the index the first time, so not on the UI thread
        public static string ObjectGroupOf(int id)
        {
            var e = Index("objects")?.ById(id);
            if (e == null) return "";
            switch (e.Skill) { case Ctx.Herb: return "herbs"; case Ctx.Ore: return "ore"; case Ctx.Chest: return "chests"; case Ctx.Fishing: return "fishing"; default: return "other"; }
        }

        static BIndex BuildObjects(Ctx x)
        {
            var ix = new BIndex { Noun = "object" };
            var ids = new HashSet<int>();
            if (SeenData.Snapshot != null) foreach (var k in SeenData.Snapshot.Objects.Keys) ids.Add(k);
            if (x.Classic != null) foreach (var k in x.Classic.Objects.Keys) ids.Add(k);
            var nodeObjs = GatherNodes.ObjectsWithNodes().ToList();
            foreach (var k in nodeObjs) ids.Add(k);                  // objects only players looted (Forever nodes) are listed too
            var foreverZone = new LiveNodeSource();
            foreach (var id in ids.OrderBy(i => i))
            {
                var s = SeenData.Object(id); var c = x.Classic?.ObjectInfo(id);
                var name = !string.IsNullOrEmpty(s?.Name) ? s.Name : c?.Name;
                if (string.IsNullOrEmpty(name)) continue;
                var e = Entry("obj", id, name);
                e.Client = s != null; e.Classic = c != null;
                e.Type = s?.Type ?? c?.Type ?? 0;
                e.Skill = ObjectCat(x, id);
                e.Zone = ZoneOfSpawns(x, c?.Spawns);
                // the Forever nodes (what players looted) say where it really is: they come before the Classic baseline's positions
                bool forever = false;
                if (nodeObjs.Contains(id)) { var fm = foreverZone.ForeverMaps(id); if (fm.Count > 0) { e.Zone = fm[0]; forever = true; e.Lodge = true; } }
                e.ZoneName = e.Zone > 0 ? x.Map?.Name(e.Zone) ?? "" : "";
                if (x.L != null && (x.L.Object(id) != null || x.L.Loot("obj", id) != null)) e.Lodge = true;
                e.Detail = ObjCatTitle(e.Skill);
                e.Extra = e.ZoneName + (forever && e.ZoneName.Length > 0 ? " (" + AtlasLabel.Forever + ")" : "");
                ix.All.Add(e);
            }
            var root = ix.Root; root.Title = "Objects";
            void Add(string slug, string title, int cat) { if (ix.All.Any(e => e.Skill == cat)) root.Kids.Add(new BNode { Slug = slug, Title = title, Pred = e => e.Skill == cat }); }
            Add("herbs", "Herbs", Ctx.Herb); Add("ore", "Ore and mining", Ctx.Ore); Add("chests", "Chests and containers", Ctx.Chest); Add("fishing", "Fishing", Ctx.Fishing); Add("other", "Other", Ctx.Other);
            return ix;
        }

        // ============================================================================================================================ quests
        static BIndex BuildQuests(Ctx x)
        {
            var ix = new BIndex { Noun = "quest" };
            var areas = new Dictionary<int, int>();
            int MapOf(int area) { if (area <= 0 || x.Map == null) return 0; if (!areas.TryGetValue(area, out var u)) areas[area] = u = x.Map.UiMapForArea(area); return u; }
            var ids = new HashSet<int>();
            if (SeenData.Snapshot != null) foreach (var k in SeenData.Snapshot.Quests.Keys) ids.Add(k);
            if (x.Classic != null) foreach (var k in x.Classic.Quests.Keys) ids.Add(k);
            foreach (var id in ids.OrderBy(i => i))
            {
                var s = SeenData.Quest(id); var c = x.Classic?.QuestInfo(id);
                var name = !string.IsNullOrEmpty(s?.Title) ? s.Title : c?.Title;
                if (string.IsNullOrEmpty(name)) continue;
                var e = Entry("quest", id, name);
                e.Client = s != null; e.Classic = c != null;
                int lvl = s != null && s.Level > 0 ? s.Level : c?.QuestLevel ?? 0;
                int min = s != null && s.MinLevel > 0 ? s.MinLevel : c?.MinLevel ?? 0;
                e.Level = lvl; e.Level2 = lvl;
                e.Zone = MapOf(s != null && s.Sort > 0 ? s.Sort : c != null && c.ZoneOrSort > 0 ? c.ZoneOrSort : 0);
                e.ZoneName = e.Zone > 0 ? x.Map?.Name(e.Zone) ?? "" : "";
                if (x.L != null && (x.L.QuestInfo(id) != null || x.L.Quest(id) != null)) e.Lodge = true;
                e.Detail = string.Join(" · ", new[] { lvl > 0 ? "Level " + lvl : "", min > 1 ? "from level " + min : "" }.Where(t => t.Length > 0));
                e.Extra = e.ZoneName;
                ix.All.Add(e);
            }
            ix.Root.Title = "Quests";
            return ix;
        }

        // ============================================================================================================================ zones and instances
        static string ContinentOf(Ctx x, int uiMap)
        {
            if (x.ContinentOf.TryGetValue(uiMap, out var n)) return n;
            n = "";
            var seen = new HashSet<int>();
            for (var m = x.Map.Get(uiMap); m != null && seen.Add(m.Id); m = m.ParentId > 0 ? x.Map.Get(m.ParentId) : null)
                if (m.Type == UiMapInfo.Continent) { n = m.Name; break; }
            x.ContinentOf[uiMap] = n;
            return n;
        }

        static BIndex BuildZones(Ctx x)
        {
            var ix = new BIndex { Noun = "zone" };
            foreach (var m in x.Map.UiMaps)
            {
                if (m.System != 0 || m.Type != UiMapInfo.Zone || string.IsNullOrEmpty(m.Name) || MapIndex.IsPlaceholderName(m.Name)) continue;
                var e = Entry("zone", m.Id, m.Name);
                e.Client = true; e.Group = ContinentOf(x, m.Id);
                var zc = x.Derived?.Zone(x.Map, m.Id);
                if (zc != null && zc.MinLevel > 0) { e.Level = zc.MinLevel; e.Level2 = zc.MaxLevel; e.Classic = true; }
                e.Detail = string.Join(" · ", new[] { e.Group, e.Level > 0 ? "Levels " + AtlasFormat.Levels(e.Level, e.Level2) : "" }.Where(t => t.Length > 0));
                ix.All.Add(e);
            }
            ix.Root.Title = "Zones";
            foreach (var g in ix.All.Select(e => e.Group).Where(g => g.Length > 0).Distinct().OrderBy(g => g, StringComparer.OrdinalIgnoreCase))
            {
                var gg = g;
                ix.Root.Kids.Add(new BNode { Slug = Slugify(g), Title = g, Pred = e => e.Group == gg });
            }
            return ix;
        }

        static BIndex BuildInstances(Ctx x)
        {
            var ix = new BIndex { Noun = "dungeon or raid" };
            foreach (var i in x.Map.Instances)
            {
                if (i.Placeholder || string.IsNullOrEmpty(i.Name)) continue;
                var e = Entry("instance", i.MapId, i.Name);
                e.Client = true; e.Type = i.InstanceType;
                if (x.Derived != null)
                {
                    var lv = new List<double>();
                    foreach (var nid in x.Derived.NpcsOnMap(i.MapId))
                    {
                        var n = x.Classic.NpcInfo(nid);
                        if (n == null || n.MaxLevel <= 0 || n.Type == 8 || n.Type == 11) continue;
                        int w = n.Spawns.Count(s => s.Map == i.MapId);
                        for (int k = 0; k < Math.Min(w, 20); k++) lv.Add((n.MinLevel + n.MaxLevel) / 2.0);
                    }
                    if (lv.Count >= 3)
                    {
                        lv.Sort();
                        e.Level = (int)Math.Round(lv[(int)(lv.Count * 0.1)]); e.Level2 = Math.Max(e.Level, (int)Math.Round(lv[Math.Min(lv.Count - 1, (int)(lv.Count * 0.9))]));
                        e.Classic = true;
                    }
                }
                e.Detail = string.Join(" · ", new[] { AtlasQuery.InstanceDetail(i), e.Level > 0 ? "Levels " + AtlasFormat.Levels(e.Level, e.Level2) : "" }.Where(t => t.Length > 0));
                ix.All.Add(e);
            }
            ix.Root.Title = "Dungeons and raids";
            void Add(string slug, string title, Func<BEntry, bool> pred) { if (ix.All.Any(pred)) ix.Root.Kids.Add(new BNode { Slug = slug, Title = title, Pred = pred }); }
            Add("dungeons", "Dungeons", e => e.Type == 1); Add("raids", "Raids", e => e.Type == 2); Add("battlegrounds", "Battlegrounds", e => e.Type == 3);
            Add("other", "Other", e => e.Type != 1 && e.Type != 2 && e.Type != 3);
            return ix;
        }

        // ============================================================================================================================ spells, skills, recipes, factions
        static string SkillGroup(int category)
        {
            switch (category)
            {
                case SkillInfo.CatClass: return "Class abilities";
                case SkillInfo.CatWeapon: return "Weapon skills";
                case SkillInfo.CatArmor: return "Armor skills";
                case SkillInfo.CatProfession: return "Professions";
                case SkillInfo.CatSecondary: return "Secondary skills";
                case SkillInfo.CatLanguage: return "Languages";
                case SkillInfo.CatAttributes: return "Attributes";
                default: return "";
            }
        }

        static BIndex BuildSpells(Ctx x)
        {
            var ix = new BIndex { Noun = "spell" };
            var sn = SpellData.Spells;
            var skillOf = new Dictionary<int, SkillInfo>();
            foreach (var a in sn.Abilities) if (!skillOf.ContainsKey(a.Spell) && sn.Skills.TryGetValue(a.Skill, out var sk)) skillOf[a.Spell] = sk;
            foreach (var s in sn.Spells.Values)
            {
                if (string.IsNullOrEmpty(s.Name)) continue;
                var e = Entry("spell", s.Id, s.Name);
                e.Client = true; e.Icon = s.IconId;
                skillOf.TryGetValue(s.Id, out var sk);
                e.SubLabel = sk != null ? SkillGroup(sk.Category) : "";
                e.Detail = string.Join(" · ", new[] { s.Rank, sk?.Name }.Where(t => !string.IsNullOrEmpty(t)));
                ix.All.Add(e);
            }
            ix.Root.Title = "Spells";
            KidsBy(ix.Root, ix.All, e => e.SubLabel, e => true);
            return ix;
        }

        static BIndex BuildSkills(Ctx x)
        {
            var ix = new BIndex { Noun = "skill" };
            foreach (var s in SpellData.Spells.Skills.Values)
            {
                if (string.IsNullOrEmpty(s.Name)) continue;
                var e = Entry("skill", s.Id, s.Name);
                e.Client = true; e.Icon = s.IconId; e.SubLabel = SkillGroup(s.Category);
                e.Detail = AtlasQuery.SkillCategory(s.Category);
                if (s.IsProfession) e.Link = AtlasHosts.RecipesLink(s.Name) ?? "";       // the professions open the Professions page
                ix.All.Add(e);
            }
            ix.Root.Title = "Skills and professions";
            KidsBy(ix.Root, ix.All, e => e.SubLabel, e => true);
            return ix;
        }

        static BIndex BuildRecipes(Ctx x)
        {
            var ix = new BIndex { Noun = "recipe" };
            var sn = SpellData.Spells;
            foreach (var r in sn.Recipes)
            {
                var sp = SpellData.Spell(r.SpellId);
                var skill = SpellData.Skill(r.SkillId);
                string name = r.ItemId > 0 ? AtlasQuery.ItemName(r.ItemId) : sp?.Name;
                if (string.IsNullOrEmpty(name)) continue;
                var e = Entry(r.ItemId > 0 ? "item" : "spell", r.ItemId > 0 ? r.ItemId : r.SpellId, name);
                e.Client = true; e.Skill = r.SkillId; e.SubLabel = skill?.Name ?? "";
                e.Level = Math.Max(1, r.MinRank); e.Level2 = e.Level;
                if (r.ItemId > 0) { e.Quality = AtlasQuery.ItemQuality(r.ItemId); e.Icon = AtlasQuery.ItemIcon(r.ItemId); } else e.Icon = sp?.IconId ?? 0;
                e.Detail = (skill?.Name ?? "Profession") + (r.MinRank > 0 ? " " + r.MinRank : "") + (r.Gray > 0 ? " · grey at " + r.Gray : "");
                e.Extra = r.ReagentCount > 0 ? "Uses " + ReagentLine(r) : "";
                ix.All.Add(e);
            }
            ix.Root.Title = "Recipes";
            KidsBy(ix.Root, ix.All, e => e.SubLabel, e => true);
            return ix;
        }

        static string ReagentLine(Recipe r)
        {
            var parts = new List<string>();
            for (int i = 0; i + 1 < r.Reagents.Length && parts.Count < 3; i += 2)
                if (r.Reagents[i] > 0) parts.Add(AtlasQuery.ItemName(r.Reagents[i]) + (r.Reagents[i + 1] > 1 ? " ×" + r.Reagents[i + 1] : ""));
            if (r.ReagentCount > 3) parts.Add("…");
            return string.Join(", ", parts);
        }

        static BIndex BuildFactions(Ctx x)
        {
            var ix = new BIndex { Noun = "faction" };
            foreach (var f in SpellData.Factions.Factions.Values)
            {
                if (string.IsNullOrEmpty(f.Name)) continue;
                var e = Entry("faction", f.Id, f.Name);
                e.Client = true; e.Type = f.ReputationIndex >= 0 ? 1 : 0;
                e.Detail = f.ParentId > 0 ? "in " + (FactionData.Name(f.ParentId) ?? "a group") : "";
                ix.All.Add(e);
            }
            ix.Root.Title = "Factions";
            if (ix.All.Any(e => e.Type == 1) && ix.All.Any(e => e.Type == 0)) ix.Root.Kids.Add(new BNode { Slug = "reputations", Title = "With a reputation bar", Pred = e => e.Type == 1 });
            return ix;
        }

        // ============================================================================================================================ rows
        static AtlasRow RowOf(BEntry e, BrowseQuery q, BIndex ix)
        {
            var r = new AtlasRow
            {
                Key = e.Kind + ":" + e.Id, Link = e.Link.Length > 0 ? e.Link : e.Kind + ":" + e.Id, Title = e.Name, Detail = e.Detail, Extra = e.Extra, IconId = e.Icon, Quality = e.Quality, Tier = e.Tier,
                Label = e.Client || (e.Lodge && !e.Classic) ? AtlasLabel.Forever : AtlasLabel.Classic, Zone = e.ZoneName, Sort = e.Chance,
            };
            if (e.Kind == "item" && e.SrcParts != null && ix.Cat == "items")
            {
                int first = q.How.Length > 0 ? Array.IndexOf(Hows, q.How) : -1;
                var parts = new List<string>();
                if (first >= 0 && e.SrcParts[first] != null) parts.Add(e.SrcParts[first]);
                for (int i = 0; i < e.SrcParts.Length; i++) if (i != first && e.SrcParts[i] != null) parts.Add(e.SrcParts[i]);
                r.Extra = string.Join(" | ", parts);
                if (e.Chance > 0 && (q.Sort == "chance" || q.How == "drop")) r.Right = AtlasFormat.Percent(e.Chance);
            }
            if (ix.Cat == "creatures" || ix.Cat == "objects" || ix.Cat == "quests") { }
            return r;
        }
    }

    // ============================================================================================================================
    // "Usable by my character": the class's armor and weapon proficiencies (the classic rules) and the item's required level. Items that are not
    // armor or weapons are usable by anyone of the level. An unknown class is only held to the level.
    // ============================================================================================================================
    public static class ItemProficiency
    {
        // class -> armor subclasses with the level they are learned at (cloth 1, leather 2, mail 3, plate 4, shield 6, libram 7, idol 8, totem 9, sigil 10)
        static readonly Dictionary<string, int[]> Armor = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["WARRIOR"] = new[] { 1, 1, 2, 1, 3, 1, 4, 40, 6, 1 },
            ["PALADIN"] = new[] { 1, 1, 2, 1, 3, 1, 4, 40, 6, 1, 7, 1 },
            ["HUNTER"] = new[] { 1, 1, 2, 1, 3, 40 },
            ["ROGUE"] = new[] { 1, 1, 2, 1 },
            ["PRIEST"] = new[] { 1, 1 },
            ["SHAMAN"] = new[] { 1, 1, 2, 1, 3, 40, 6, 1, 9, 1 },
            ["MAGE"] = new[] { 1, 1 },
            ["WARLOCK"] = new[] { 1, 1 },
            ["DRUID"] = new[] { 1, 1, 2, 1, 8, 1 },
        };

        // weapon subclasses: 0 axe, 1 2h axe, 2 bow, 3 gun, 4 mace, 5 2h mace, 6 polearm, 7 sword, 8 2h sword, 10 staff, 13 fist, 15 dagger, 16 thrown, 18 crossbow, 19 wand
        static readonly Dictionary<string, int[]> Weapons = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["WARRIOR"] = new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 10, 13, 15, 16, 18 },
            ["PALADIN"] = new[] { 0, 1, 4, 5, 6, 7, 8 },
            ["HUNTER"] = new[] { 0, 1, 2, 3, 6, 7, 8, 10, 13, 15, 16, 18 },
            ["ROGUE"] = new[] { 2, 3, 4, 7, 13, 15, 16, 18 },
            ["PRIEST"] = new[] { 4, 10, 15, 19 },
            ["SHAMAN"] = new[] { 0, 1, 4, 5, 10, 13, 15 },
            ["MAGE"] = new[] { 7, 10, 15, 19 },
            ["WARLOCK"] = new[] { 7, 10, 15, 19 },
            ["DRUID"] = new[] { 4, 5, 6, 10, 13, 15 },
        };

        public static bool Known(string cls) => !string.IsNullOrEmpty(cls) && Armor.ContainsKey(cls);

        public static bool CanUse(string cls, int level, BEntry e)
        {
            if (level > 0 && e.Level > level) return false;
            if (!Known(cls) || e.Cls < 0) return true;
            if (e.Cls == 4 && e.Sub > 0 && e.Sub != 5)
            {
                var a = Armor[cls];
                for (int i = 0; i + 1 < a.Length; i += 2) if (a[i] == e.Sub) return level <= 0 || level >= a[i + 1];
                return false;
            }
            if (e.Cls == 2) return Array.IndexOf(Weapons[cls], e.Sub) >= 0;
            return true;
        }
    }
}
