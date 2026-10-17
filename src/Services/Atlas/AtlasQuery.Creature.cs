using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services.Atlas.Classic;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Maps;

namespace ElansAddonHub.Services.Atlas
{
    public static partial class AtlasQuery
    {
        static readonly string[] Families =
        {
            "", "Wolf", "Cat", "Spider", "Bear", "Boar", "Crocolisk", "Carrion Bird", "Crab", "Gorilla", "", "Raptor", "Tallstrider", "", "", "Felhunter", "Voidwalker", "Succubus", "", "Doomguard",
            "Scorpid", "Turtle", "", "Imp", "Bat", "Hyena", "Owl", "Wind Serpent",
        };
        static string FamilyName(int f) => f > 0 && f < Families.Length ? Families[f] : "";

        static string Services(uint flags)
        {
            var l = new List<string>();
            if ((flags & 0x2) != 0) l.Add("Quest giver");
            if ((flags & 0x80) != 0) l.Add("Vendor");
            if ((flags & 0x10) != 0) l.Add("Trainer");
            if ((flags & 0x1000) != 0) l.Add("Repairs");
            if ((flags & 0x2000) != 0) l.Add("Flight master");
            if ((flags & 0x10000) != 0) l.Add("Innkeeper");
            if ((flags & 0x20000) != 0) l.Add("Banker");
            if ((flags & 0x200000) != 0) l.Add("Auctioneer");
            if ((flags & 0x400000) != 0) l.Add("Stable master");
            return string.Join(", ", l);
        }

        // ---- loot of one source (an npc, an object): what the Lodge saw drop with the chance from kills, merged with the Classic baseline's table
        // Lodge rows come first; a Classic figure for the same item rides on the same row; Classic chances under TinyChance are folded.
        // the word a source's count is in: "kill", "open", "pickpocket", "corpse" (gathered), "opening" (a clam, a lockbox)
        internal static string LootWord(string lodgeKind) => lodgeKind == "obj" ? "open" : lodgeKind == "pick" ? "pickpocket" : lodgeKind == "item" ? "opening" : lodgeKind == "fish" ? "cast"
            : lodgeKind == "skin" || lodgeKind == "mine" || lodgeKind == "herb" || lodgeKind == "salvage" ? "corpse" : "kill";
        internal static string LootTypeOf(string lodgeKind) => lodgeKind == "obj" ? "Object" : lodgeKind == "fish" ? "Fishing" : lodgeKind == "pick" ? "Pickpocketing" : lodgeKind == "skin" ? "Skinning"
            : lodgeKind == "mine" ? "Mining" : lodgeKind == "herb" ? "Herbalism" : lodgeKind == "salvage" ? "Salvage" : lodgeKind == "item" ? "Contained in" : "Drop";

        // one observed item of a source, in words: "4 of 37 kills (10.8 %)" (each corpse once; a quest item over the kills while its quest wanted it; under
        // AtlasObs.LowSampleKills kills no rate is claimed), plus its random suffixes and the rate since the last patch when the source spans builds
        internal static void ObservedRate(AtlasObs.Counter c, int itemId, string what, out string detail, out string extra, out double chance, out bool low)
        {
            var it = c.Items[itemId];
            var ix = c.X != null && c.X.It.TryGetValue(itemId, out var xx) ? xx : null;
            long kills = AtlasObs.UniqueKills(c), drops = AtlasObs.UniqueDrops(c, it[0]);
            bool quest = ix != null && ix.Q > 0 && c.X.Q.TryGetValue(ix.Q, out var qk) && qk > 0;
            if (quest) { kills = c.X.Q[ix.Q]; drops = Math.Min(it[0], kills * AtlasObs.MaxDropsPerKill); }   // the quest kills are not deduplicated: neither are their drops
            string w = quest ? what + " while on the quest" : what, ws = quest ? what + "s while on the quest" : what + "s";
            chance = kills > 0 ? Math.Min(100, drops * 100.0 / kills) : 0;
            low = AtlasObs.LowSample(kills);
            detail = AtlasObs.RateText(drops, kills, w, ws) + (it[1] > it[0] ? " · " + it[1] + " items" : "");
            var parts = new List<string>();
            var sx = AtlasObs.SuffixText(ix);
            if (sx.Length > 0) parts.Add(sx);
            int build = c.X?.LatestBuild ?? 0;
            if (build > 0 && c.X.B.Count > 1 && c.X.B.TryGetValue(build, out var kb) && kb > 0)
            {
                long db = ix != null && ix.B.TryGetValue(build, out var d) ? d : 0;
                parts.Add("Since the last patch: " + AtlasObs.RateText(db, kb, what, what + "s"));
            }
            extra = string.Join(" · ", parts);
        }

        static void LootSection(AtlasPageModel m, string key, string title, string lodgeKind, int id, ClassicSourceKind? ck, string empty)
        {
            var s = Section(m, key, title, empty);
            var L = Lodge();
            var rows = new List<AtlasRow>(); var tiny = new List<AtlasRow>(); var by = new Dictionary<int, AtlasRow>();
            var counter = lodgeKind != null ? L?.Loot(lodgeKind, id) : null;
            string what = LootWord(lodgeKind);
            if (counter != null && counter.N > 0)
            {
                foreach (var kv in counter.Items)
                {
                    ObservedRate(counter, kv.Key, what, out var detail, out var extra, out var chance, out var low);
                    var r = ItemRow(kv.Key, detail, AtlasLabel.Forever);
                    r.Type = LootTypeOf(lodgeKind); r.Tier = 0; r.Sort = low ? chance / 1000 : chance; r.Extra = extra;
                    rows.Add(r); by[kv.Key] = r;
                }
                long unique = AtlasObs.UniqueKills(counter), dup = counter.X?.Dup ?? 0;
                s.Note = AtlasFormat.Count(unique, "looted " + what, "looted " + what + "s") + " seen by " + AtlasFormat.Count(Math.Max(1, counter.Members), "member", "members") + " of the Lodge"
                    + (dup > 0 ? " (" + AtlasFormat.Count(dup, "corpse", "corpses") + " that several members looted counted once)" : "") + "."
                    + (AtlasObs.LowSample(unique) ? " Fewer than " + AtlasObs.LowSampleKills + " so far: these are counts, not drop rates yet." : "");
                AddLayer(m, "lodge");
            }
            AddCairnLoot(m, s, lodgeKind, id, rows, tiny, by);   // the Cairn's numbers (tier 1) between the Lodge's and the Classic baseline's
            AddCairnShareNote(m);                              // a member who does not share: one quiet line instead
            var dv = ck != null ? AtlasDerived.For(ClassicAtlas.Current) : null;
            if (dv != null)
            {
                foreach (var e in dv.LootOf(ck.Value, id))
                {
                    var src = e.Source;
                    var txt = AtlasFormat.Percent(src.Chance) + (src.QuestOnly ? " · only while on the quest" : "") + CountText(src);
                    if (by.TryGetValue(e.ItemId, out var seen)) { seen.Extra = (seen.Extra.Length > 0 ? seen.Extra + " · " : "") + AtlasLabel.Classic + ": " + txt; continue; }
                    var r = ItemRow(e.ItemId, txt, AtlasLabel.Classic);
                    r.Type = "Drop"; r.Tier = 2; r.Sort = src.Chance;
                    if (src.Chance < TinyChance) tiny.Add(r); else rows.Add(r);
                }
                if (s.Note.Length == 0) s.Note = "From the Classic baseline's loot table.";
                else s.Note += " Classic baseline figures ride along on the same row.";
                AddLayer(m, "classic");
            }
            FoldTiny(s, rows, tiny, 8);
            if (s.Hidden.Count > 0) s.HiddenText = s.HiddenText.Replace("world-drop sources", "drops").Replace("world-drop source", "drop");
        }

        static void VendorSection(AtlasPageModel m, int npcId)
        {
            var L = Lodge();
            var lv = L?.Vendor(npcId);
            var cv = ClassicAtlas.VendorItems(npcId);
            if (lv == null && cv.Count == 0) return;
            var s = Section(m, "vendor", "Sells");
            var by = new Dictionary<int, AtlasRow>();
            var rows = new List<AtlasRow>();
            if (lv != null)
            {
                foreach (var kv in lv.Items)
                {
                    var p = kv.Value;
                    var r = ItemRow(kv.Key, (p[0] > 0 ? AtlasFormat.Money(p[0]) : "free") + (p[1] > 1 ? " · stack of " + p[1] : "") + (p[2] >= 0 ? " · limit " + p[2] : ""), AtlasLabel.Forever);
                    r.Type = "Vendor"; r.Tier = 0; r.Sort = 0;
                    rows.Add(r); by[kv.Key] = r;
                }
                s.Note = "Prices were seen in a Lodge member's game " + AtlasFormat.Ago(lv.At, Context.NowUtc()) + ".";
                AddLayer(m, "lodge");
            }
            foreach (var v in cv)
            {
                var stock = v.MaxCount > 0 ? "limited stock (" + v.MaxCount + ")" : "unlimited stock";
                if (by.TryGetValue(v.ItemId, out var seen)) { seen.Extra = AtlasLabel.Classic + ": " + stock; continue; }
                var r = ItemRow(v.ItemId, "Sold here · " + stock, AtlasLabel.Classic);
                r.Type = "Vendor"; r.Tier = 2;
                rows.Add(r);
            }
            if (cv.Count > 0) { AddLayer(m, "classic"); if (s.Note.Length == 0) s.Note = "Prices are not in the Classic baseline."; }
            SortRows(rows);
            s.Rows.AddRange(rows);
        }

        static AtlasPageModel BuildNpc(int id)
        {
            var m = new AtlasPageModel { Id = "npc:" + id, Kind = "npc", MapFirst = true };
            var L = Lodge();
            var seen = SeenData.Npc(id);
            var cn = ClassicAtlas.NpcInfo(id);
            var loot = L?.Loot("npc", id);
            var spots = L?.Spawns(id);
            var seenAt = NpcSpots(id);                                  // where members (and you) met it, killed it, saw it as a rare (Toolkit npcspots)
            bool lodgeKnows = (loot != null && loot.N > 0) || (spots != null && spots.Count > 0) || L?.Vendor(id) != null || L?.Note(id) != null || seenAt.Count > 0;
            var cs = CairnAtlas.Current;
            bool cairnKnows = cs != null && (cs.Spawns(id).Count > 0 || cs.Stock(id).Count > 0 || new[] { "kill", "pickpocket", "skinning", "mining", "herbalism", "salvage" }.Any(k => cs.Loot(k, id) != null));   // the Cairn's open data
            if (seen == null && cn == null && !lodgeKnows && !cairnKnows)
            {
                var nf = NotFoundPage("npc", id, SeenData.Ready || ClassicAtlas.IsReady ? "No layer knows this NPC. Your game has not met it yet." : "Nothing is read yet that could know this NPC (the game's creature cache and the Classic baseline are both missing).");
                LayerNotes(nf, true, false);
                return nf;
            }
            m.Title = seen != null && seen.Name.Length > 0 ? seen.Name : cn?.Name ?? "NPC " + id;
            string F = AtlasLabel.Forever, C = AtlasLabel.Classic;
            string title = seen != null && seen.Title.Length > 0 ? seen.Title : cn?.SubName ?? "";
            if (seen != null) AddLayer(m, "seen");
            if (cn != null) AddLayer(m, "classic");
            if (title.Length > 0) m.Facts.Add(new AtlasFact("Title", "<" + title + ">", seen != null && seen.Title.Length > 0 ? F : C));
            if (cn != null) { var lv = AtlasFormat.Levels(cn.MinLevel, cn.MaxLevel); if (lv.Length > 0) m.Facts.Add(new AtlasFact("Level", lv, C)); }
            int rank = seen != null ? seen.Rank : cn?.Rank ?? 0;
            m.Facts.Add(new AtlasFact("Rank", rank > 0 ? AtlasFormat.RankName(rank) : "Normal", seen != null ? F : C));
            // what Lodge members' games showed when they looted it (Toolkit 0.5.10): the level range and elite / rare class
            var lx = loot?.X;
            if (lx != null && lx.Lo > 0) m.Facts.Add(new AtlasFact("Level seen", AtlasFormat.Levels(lx.Lo, lx.Hi) + (lx.Cl.Count > 0 ? " · " + AtlasObs.ClassText(lx) : ""), F));
            int type = seen != null && seen.Type > 0 ? seen.Type : cn?.Type ?? 0;
            if (AtlasFormat.CreatureType(type).Length > 0) m.Facts.Add(new AtlasFact("Type", AtlasFormat.CreatureType(type), seen != null && seen.Type > 0 ? F : C));
            int family = seen != null && seen.Family > 0 ? seen.Family : cn?.Family ?? 0;
            if (FamilyName(family).Length > 0) m.Facts.Add(new AtlasFact("Family", FamilyName(family), seen != null && seen.Family > 0 ? F : C));
            if (cn != null && cn.FactionId > 0)
            {
                var tpl = FactionData.Template(cn.FactionId);
                var desc = FactionData.Describe(cn.FactionId);
                // the template id is the baseline's; when the server's hotfix rows changed that template, its standing is the hotfix's
                m.Facts.Add(new AtlasFact("Faction", desc ?? "Faction template " + cn.FactionId, SpellData.Hotfixes.Templates.Contains(cn.FactionId) ? AtlasLabel.Hotfix : C, tpl != null && FactionData.Name(tpl.FactionId) != null ? AtlasIds.Faction(tpl.FactionId) : null));
            }
            // the client cache holds the creature's TYPE flags, not its npc flags (WdbCreature): services come from the Classic baseline only
            var svc = Services((uint)(cn?.NpcFlags ?? 0));
            if (svc.Length > 0) m.Facts.Add(new AtlasFact("Services", svc, C));
            var sf = SeenView.Npc(id);
            if (sf != null)
            {
                if (sf.Tameable) m.Facts.Add(new AtlasFact("Tameable", FamilyName(sf.Family).Length > 0 ? "Yes, as a " + FamilyName(sf.Family).ToLowerInvariant() : "Yes", F));
                else if (sf.Type == 1 && sf.Family > 0) m.Facts.Add(new AtlasFact("Tameable", "No (Forever does not let hunters tame this one)", F));
                if (sf.BossLevel) m.Facts.Add(new AtlasFact("Shown level", "?? (boss)", F));
                foreach (var kc in new[] { sf.Kc1, sf.Kc2 }.Where(k => k > 0 && k != id).Distinct())
                    m.Facts.Add(new AtlasFact("Counts as", NpcName(kc), F, "npc:" + kc));
                if (sf.FromLodge) AddLayer(m, "lodge");
            }
            m.Subtitle = string.Join(" · ", new[] { "NPC " + id, title.Length > 0 ? "<" + title + ">" : "", cn != null && AtlasFormat.Levels(cn.MinLevel, cn.MaxLevel).Length > 0 ? "Level " + AtlasFormat.Levels(cn.MinLevel, cn.MaxLevel) : "", rank > 0 ? AtlasFormat.RankName(rank) : "", AtlasFormat.CreatureType(type) }.Where(s => s.Length > 0));

            bool vendor = (cn != null && cn.IsVendor) || L?.Vendor(id) != null;
            // where it stands
            var plan = new PlanBuilder();
            string tip = (cn != null ? "Level " + AtlasFormat.Levels(cn.MinLevel, cn.MaxLevel) + " · " : "");
            // Forever first: the spots members saw it at; the older kill positions (spawns) only when there are none; the Classic baseline after, minus its
            // spawn points in the zones where members fixed its place
            foreach (var sp in seenAt) plan.Spot(sp.UiMapId, sp.X, sp.Y, vendor ? MarkerKind.Vendor : MarkerKind.Spawn, m.Title, NpcSpotTip(sp), "npc:" + id);
            if (spots != null && seenAt.Count == 0) foreach (var sp in spots) plan.Spot(sp.Zone, sp.X, sp.Y, vendor ? MarkerKind.Vendor : MarkerKind.Spawn, m.Title, "Seen by the Lodge · " + F, "npc:" + id);
            int classicDropped = 0;
            var classicSpawns = cn != null ? ClassicOutside(cn.Spawns, FixedZones(seenAt), out classicDropped) : null;
            if (cn != null) plan.Classic(classicSpawns, vendor ? MarkerKind.Vendor : MarkerKind.Spawn, m.Title, tip + C, "npc:" + id);
            NoteFacts(m, id, L, plan);                                  // hunter field notes: Seen by Lodge hunters / by you, abilities taught, places seen
            m.Map = plan.Build();
            AddFoundIn(m, cn?.Spawns, spots);
            NpcSpotSection(m, seenAt, classicDropped);

            LootSection(m, "loot", "Loot", "npc", id, ClassicSourceKind.NpcDrop, "No loot is known for this NPC.");
            // what members got from it beyond the kill (Toolkit 0.5.10: pickpocketing, gathering the corpse), beside the Classic baseline's tables
            var dvc = ClassicAtlas.Current != null ? AtlasDerived.For(ClassicAtlas.Current) : null;
            foreach (var (gk, gt, gc) in new (string, string, ClassicSourceKind?)[] { ("skin", "Skinning", ClassicSourceKind.NpcSkin), ("pick", "Pickpocketing", ClassicSourceKind.NpcPickpocket), ("mine", "Mining", null), ("herb", "Herbalism", null), ("salvage", "Salvage", null) })
            {
                bool lodgeHas = (L?.Loot(gk, id)?.N ?? 0) > 0, classicHas = gc != null && dvc != null && dvc.LootOf(gc.Value, id).Count > 0;
                bool cairnHas = CairnAtlas.Current?.Loot(CairnKindOf(gk), id) != null;
                if (lodgeHas || classicHas || cairnHas) LootSection(m, gk, gt, gk, id, gc, "");
            }
            if (vendor) VendorSection(m, id);
            var dv = AtlasDerived.For(ClassicAtlas.Current);
            if (dv != null)
            {
                var qs = dv.QuestsGivenBy(false, id);
                if (qs.Count > 0)
                {
                    var s = Section(m, "quests", "Quests it gives");
                    s.Note = "From the Classic baseline.";
                    foreach (var q in qs.OrderBy(x => x.QuestLevel).ThenBy(x => x.Id).Take(60))
                        s.Rows.Add(new AtlasRow { Key = "quest:" + q.Id, Link = "quest:" + q.Id, Label = C, Tier = 2, Type = "Quest", Title = QuestTitle(q.Id), Detail = "Level " + q.QuestLevel + (q.MinLevel > 0 ? " · from level " + q.MinLevel : "") });
                    if (qs.Count > 60) s.HiddenText = (qs.Count - 60) + " more quests not shown";
                }
            }
            SeenGoalQuests(m, 0, id, sf != null ? new[] { sf.Kc1, sf.Kc2 } : null);
            LayerNotes(m, !ClassicAtlas.IsReady, !LodgeHas(L));
            if (!SeenData.Ready) m.Notes.Add("What your game has seen (names, ranks) is not read yet.");
            AddCairnShareNote(m);                              // 2.87: also when no loot table was drawn (the line, once)
            return m;
        }

        // "Quests that ask for it": the quests Forever described (to this PC or a Lodge member) whose goals name this creature (type 0, or one it counts as)
        // or this object (type 2)
        static void SeenGoalQuests(AtlasPageModel m, int goalType, int id, int[] alsoIds)
        {
            var ids = new HashSet<int> { id };
            if (alsoIds != null) foreach (var a in alsoIds) if (a > 0) ids.Add(a);
            var qs = SeenView.AllQuests().Where(q => q.Full && q.Goals.Any(g => g.Type == goalType && ids.Contains(g.ObjectId))).OrderBy(q => q.Level).ThenBy(q => q.Id).ToList();
            if (qs.Count == 0) return;
            var s = Section(m, "goalquests", "Quests that ask for it");
            s.Note = "From the quests Forever described to your game or a Lodge member's.";
            foreach (var q in qs.Take(60))
            {
                var g = q.Goals.First(x => x.Type == goalType && ids.Contains(x.ObjectId));
                s.Rows.Add(new AtlasRow { Key = "quest:" + q.Id, Link = "quest:" + q.Id, Label = AtlasLabel.Forever, Tier = 1, Type = "Quest", Title = q.Title, Detail = (q.Level > 0 ? "Level " + q.Level : "") + (g.Amount > 1 ? (q.Level > 0 ? " · " : "") + "×" + g.Amount : "") });
            }
            if (qs.Count > 60) s.HiddenText = (qs.Count - 60) + " more quests not shown";
        }

        static void AddSimpleLoot(AtlasPageModel m, string key, string title, IReadOnlyList<ClassicLootEntry> list)
        {
            var s = Section(m, key, title);
            s.Note = "From the Classic baseline.";
            var rows = new List<AtlasRow>(); var tiny = new List<AtlasRow>();
            foreach (var e in list)
            {
                var r = ItemRow(e.ItemId, AtlasFormat.Percent(e.Source.Chance) + CountText(e.Source), AtlasLabel.Classic);
                r.Type = "Drop"; r.Tier = 2; r.Sort = e.Source.Chance;
                if (e.Source.Chance < TinyChance) tiny.Add(r); else rows.Add(r);
            }
            FoldTiny(s, rows, tiny, 8);
            if (s.Hidden.Count > 0) s.HiddenText = s.HiddenText.Replace("world-drop sources", "drops").Replace("world-drop source", "drop");
        }

        // "Found in": the zones of a thing's spawn points, each a link
        static void AddFoundIn(AtlasPageModel m, IReadOnlyList<ClassicSpawn> spawns, IReadOnlyList<AtlasObs.Spot> spots)
        {
            var zones = new Dictionary<int, int>();
            foreach (var z in m.Map.Zones) zones[z.UiMapId] = z.Markers.Count;
            if (zones.Count == 0 && m.Map.InInstances.Count == 0) return;
            foreach (var z in zones.OrderByDescending(k => k.Value).ThenBy(k => k.Key).Take(4))
                m.Facts.Add(new AtlasFact(m.Facts.Any(f => f.Label == "Found in") ? "Also in" : "Found in", ZoneName(z.Key), m.Map.Zones.First(x => x.UiMapId == z.Key).Forever > 0 ? AtlasLabel.Forever : AtlasLabel.Classic, AtlasIds.TryParse("zone:" + z.Key, out _, out _) ? "zone:" + z.Key : null));
            foreach (var i in m.Map.InInstances.Take(2))
            {
                var inst = MapData.Index?.Instance(i.Key);
                if (inst != null) m.Facts.Add(new AtlasFact("Inside", inst.Name, AtlasLabel.Classic, "instance:" + i.Key));
            }
        }

        static AtlasPageModel BuildObject(int id)
        {
            var m = new AtlasPageModel { Id = "obj:" + id, Kind = "obj", MapFirst = true };
            var L = Lodge();
            var seen = SeenData.Object(id);
            var co = ClassicAtlas.ObjectInfo(id);
            var loot = L?.Loot("obj", id);
            if (seen == null && co == null && (loot == null || loot.N == 0) && CairnAtlas.Current?.Loot("object", id) == null && !new LiveNodeSource().ForeverMaps(id).Any())
            {
                var nf = NotFoundPage("obj", id, "No layer knows this object. Your game has not met it yet.");
                LayerNotes(nf, true, false);
                return nf;
            }
            string F = AtlasLabel.Forever, C = AtlasLabel.Classic;
            m.Title = seen != null && seen.Name.Length > 0 ? seen.Name : co?.Name ?? "Object " + id;
            if (seen != null) AddLayer(m, "seen");
            if (co != null) AddLayer(m, "classic");
            int type = seen != null ? seen.Type : co?.Type ?? 0;
            var kind = ObjectKind(type);
            if (kind.Length > 0) m.Facts.Add(new AtlasFact("Kind", kind, seen != null ? F : C));
            var need = SeenView.SkillNeedOf(id);
            if (need.HasValue && SeenView.SkillNeedText(need.Value) is string needText) m.Facts.Add(new AtlasFact("Needs", needText, F));
            else if (!need.HasValue && SeenView.LockOf(id) <= 0 && co != null && co.LockId > 0 && SpellData.World?.SkillNeed(co.LockId) is LockNeed cn && SeenView.SkillNeedText(cn) is string cText)
                m.Facts.Add(new AtlasFact("Needs", cText, C));          // the client has not told its lock: the baseline's (the lock's skill still comes from the game files)
            m.Subtitle = string.Join(" · ", new[] { "Object " + id, kind }.Where(s => s.Length > 0));
            var plan = new PlanBuilder();
            if (co != null) plan.Classic(co.Spawns, MarkerKind.Node, m.Title, kind + " · " + C, "obj:" + id);
            AddObjectNodes(plan, id, m.Title);                         // Forever nodes (players looted here) above the Classic baseline's positions
            m.Map = plan.Build();
            AddFoundIn(m, co?.Spawns, null);
            LootSection(m, "loot", "Loot", "obj", id, ClassicSourceKind.ObjectLoot, "No loot is known for this object.");
            SeenGoalQuests(m, 2, id, null);
            LayerNotes(m, !ClassicAtlas.IsReady, !LodgeHas(L));
            return m;
        }
    }
}
