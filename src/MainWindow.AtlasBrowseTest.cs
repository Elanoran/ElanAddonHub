using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Atlas.Classic;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Maps;

namespace ElansAddonHub
{
    // --selftest "atlasbrowse": the Atlas browse pages on the invented world of the Atlas page test (AtlasPageTest.Data.cs) plus a few more items, objects and a hunter:
    // the category registry (and a hosted category), browse ids, every category listing, the item sub-categories, the filters, the sorts, "how to get it", usable-by-character,
    // the start page tiles and presets, history through browse ids, missing layers, the page in the window, 24 000 items, the player's own game files when present.
    public partial class MainWindow
    {
        static ClassicIndex AtlasBrowseClassic()
        {
            var idx = AtlasTestClassic();
            void It(int id, string name, int q, int il, int rl) => idx.Items[id] = new ClassicItemInfo { Id = id, Name = name, Quality = q, ItemLevel = il, RequiredLevel = rl };
            It(300, "Classic Only Hammer", 2, 18, 15);
            void Src(int item, ClassicSourceKind k, int src, double chance, int stock = 0)
                => idx.Add(item, new ClassicItemSource { Kind = k, SourceId = src, Chance = chance, MinCount = 1, MaxCount = stock });
            Src(200, ClassicSourceKind.Vendor, 2, 0); Src(207, ClassicSourceKind.Vendor, 2, 0); Src(209, ClassicSourceKind.Vendor, 2, 0, 5); Src(212, ClassicSourceKind.Vendor, 2, 0); Src(213, ClassicSourceKind.Vendor, 2, 0);
            Src(201, ClassicSourceKind.NpcDrop, 4, 8); Src(205, ClassicSourceKind.QuestChoice, 9, 0); Src(300, ClassicSourceKind.NpcDrop, 6, 2);
            Src(210, ClassicSourceKind.ObjectLoot, 60, 100); Src(211, ClassicSourceKind.ObjectLoot, 61, 100); Src(218, ClassicSourceKind.ObjectLoot, 62, 40);
            idx.Objects[60] = new ClassicObject { Id = 60, Name = "Peacebloom Patch", Type = 3, LootId = 2, Spawns = new[] { Sp(0, 700, 900) } };
            idx.Objects[61] = new ClassicObject { Id = 61, Name = "Copper Vein", Type = 3, LootId = 3, Spawns = new[] { Sp(0, -400, -500) } };
            idx.Objects[62] = new ClassicObject { Id = 62, Name = "School of Fish", Type = 25, LootId = 4, Spawns = new[] { Sp(0, 800, 1700) } };
            idx.Quests[9].RewardItems = new[] { 205 };
            idx.Finish();
            return idx;
        }

        static ItemSnapshot AtlasBrowseItems(int extra = 0)
        {
            var list = new List<ClientItem>
            {
                CItem(100, "Gnarlfang's Claw", 3, 14, 10, 13, 2, 7, 12050, 0, "Still warm."), CItem(101, "Wolf Pelt", 1, 5, 1, 0, 7, 0, 30), CItem(102, "Linen Cloth", 1, 5, 1, 0, 7, 5, 13),
                CItem(103, "Dusty Cloak", 0, 3, 1, 16, 4, 1), CItem(104, "Crypt Blade", 3, 26, 22, 13, 2, 7), CItem(105, "Ruby Ring", 4, 28, 24, 11, 4, 0),
                CItem(108, "Heavy Linen Bandage", 1, 20, 1, 0, 0, 1),
                CItem(200, "Iron Sword", 1, 10, 5, 13, 2, 7), CItem(201, "Great Axe", 2, 30, 25, 17, 2, 1), CItem(202, "Short Bow", 1, 8, 1, 15, 2, 2),
                CItem(203, "Silk Robe", 2, 14, 10, 5, 4, 1), CItem(204, "Leather Vest", 1, 12, 10, 5, 4, 2), CItem(205, "Mail Hauberk", 3, 32, 30, 5, 4, 3),
                CItem(206, "Plate Helm", 3, 44, 40, 1, 4, 4), CItem(207, "Wooden Shield", 1, 9, 8, 14, 4, 6), CItem(208, "Silver Ring", 2, 15, 12, 11, 4, 0),
                CItem(209, "Healing Potion", 1, 10, 16, 0, 0, 1), CItem(210, "Peacebloom", 1, 1, 1, 0, 7, 9), CItem(211, "Copper Ore", 1, 1, 1, 0, 7, 7),
                CItem(212, "Pattern: Linen Bag", 2, 10, 5, 0, 9, 2), CItem(213, "Small Bag", 1, 5, 1, 18, 1, 0), CItem(214, "Quest Letter", 1, 1, 1, 0, 12, 0),
                CItem(215, "Rough Arrow", 1, 1, 1, 24, 6, 2), CItem(216, "Ruby", 2, 20, 1, 0, 3, 0), CItem(217, "Odd Trinket", 1, 1, 1, 0, 15, 4), CItem(218, "Raw Trout", 1, 1, 1, 0, 0, 5),
            };
            // big lists: invented items spread over every class, for the speed check
            var rnd = new Random(7);
            for (int i = 0; i < extra; i++)
            {
                int cls = new[] { 0, 1, 2, 4, 4, 4, 7, 9, 12, 15 }[rnd.Next(10)];
                int sub = cls == 2 ? new[] { 0, 1, 2, 4, 7, 8, 10, 15 }[rnd.Next(8)] : cls == 4 ? rnd.Next(1, 5) : rnd.Next(0, 4);
                list.Add(CItem(10000 + i, "Invented Item " + i, rnd.Next(0, 5), 1 + rnd.Next(60), 1 + rnd.Next(60), cls == 4 ? 1 + rnd.Next(10) : cls == 2 ? 13 : 0, cls, sub, 0, 0));
            }
            return new ItemSnapshot(list, new ClientItem[0]);
        }

        async Task<string> AtlasBrowseTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            void Check(string what, bool cond, string detail = null) { SelfTestClock.Mark(what); notes.Add((cond ? "ok   " : "FAIL ") + what + (!cond && detail != null ? " [" + detail + "]" : "")); ok &= cond; }
            var work = Path.Combine(dir, "atlasbrowse");
            var savedLodge = AtlasQuery.Context.Lodge; var savedNow = AtlasQuery.Context.NowUtc; var savedOwned = AtlasQuery.Context.Owned;
            var savedTiles = MapRender.TileSourceForTest; var savedMapDir = MapRender.DirOverride; var savedHook = Dialog.TestHook;
            var savedLodgeState = AtlasView.LodgeState; var savedWidth = Width; var h0 = Height;
            Action<string> onNav = null;
            try
            {
                if (Directory.Exists(work)) Util.DeleteDir(work);
                Directory.CreateDirectory(work);
                MapRender.DirOverride = Path.Combine(work, "maps");
                MapRender.TileSourceForTest = fdid => Solid(256, 256, 70, 96, 60);
                var lodge = AtlasTestLodge();
                var classic = AtlasBrowseClassic();
                var hunter = new InvChar { Key = "Forever-Elan", Name = "Elan", Realm = "Forever", Class = "HUNTER", Level = 20 };
                var chars = new List<InvChar> { hunter };
                void AllLayers(int extraItems = 0)
                {
                    ItemData.SetForTest(AtlasBrowseItems(extraItems), ItemData.State.Ready);
                    SpellData.SetForTest(AtlasTestSpells());
                    SeenData.SetForTest(AtlasTestSeen());
                    MapData.SetForTest(new MapIndex(AtlasTestMapSet()));
                    ClassicAtlas.Use(classic);
                    AtlasQuery.Context.Lodge = () => lodge;
                    AtlasView.OwnedOverrideForTest = AtlasTestOwned;
                    AtlasView.CharsOverrideForTest = () => chars;
                    AtlasBrowse.ForgetForTest();
                }
                AtlasQuery.Context.NowUtc = () => AtUtc(AtNow);
                AtlasView.LodgeState = () => "Connected · sharing your observations and receiving the Lodge's";
                AllLayers();

                List<string> T(BrowseResult r) => r.Rows.Select(x => x.Title).ToList();
                BrowseResult Run(string id) => AtlasBrowse.Run(id);

                // ============================================================================================================ registry and ids
                var cats = AtlasCategories.All.Where(c => !c.Hosted).ToList();      // the window registers Talents and Professions as hosted categories too (atlashosts checks them)
                Check("registry: the twelve built-in categories in start page order (Classes and Races first in Character, then Spells, Recipes, Skills), each with a title, a glyph and the id of its browse page",
                    cats.Select(c => c.Id).SequenceEqual(new[] { "items", "creatures", "objects", "quests", "zones", "instances", "classes", "races", "spells", "recipes", "skills", "factions" }) && cats.All(c => c.Title.Length > 0 && c.Glyph.Length > 0 && c.OpenId == "browse:" + c.Id && !c.Hosted), string.Join(",", cats.Select(c => c.Id)));
                var labs = new AtlasCategory { Id = "labs", Title = "Labs", Glyph = AtlasKinds.Glyph("skill"), Blurb = "A hosted page", Order = 200, Host = sub => new TextBlock { Text = "hosted labs: " + sub } };
                AtlasCategories.Register(labs);
                Check("registry: a hosted category registers with an id prefix: 'labs:tree/a' routes to it with the sub-path 'tree/a'; ids of other kinds do not", AtlasCategories.TryHosted("labs:tree/a", out var hc, out var hs) && hc.Id == "labs" && hs == "tree/a"
                    && AtlasCategories.IsRouted("labs:") && AtlasQuery.NormalizeId("labs:tree/a") == "labs:tree/a" && !AtlasCategories.IsRouted("item:5") && !AtlasCategories.IsRouted("npc:1") && labs.OpenId == "labs:");
                Check("registry: a hosted id is not a search (Search returns nothing for it) and the hosted category shows on the start page list", AtlasQuery.Search("labs:tree").Total == 0 && AtlasCategories.Visible.Any(c => c.Id == "labs"));
                AtlasCategories.Register(new AtlasCategory { Id = "labs", Title = "Labs 2", Order = 200, Host = sub => new TextBlock() });
                Check("registry: registering the same id again replaces it, unregistering removes it", AtlasCategories.All.Count(c => c.Id == "labs") == 1 && AtlasCategories.Find("labs").Title == "Labs 2" && AtlasCategories.Unregister("labs") && AtlasCategories.Find("labs") == null && !AtlasCategories.IsRouted("labs:"));
                AtlasCategories.Register(labs);   // kept for the window checks below
                var q1 = BrowseQuery.TryParse("browse:Items/Weapons/one-handed-swords?minlvl=20&maxlvl=10&q=3&name=Iron%20Sword&how=VENDOR&bogus=1&src=nonsense", out var pq) ? pq : null;
                Check("ids: a browse id parses to a category path and filters; the canonical text is lower case, in a fixed order, without unknown or invalid parts, with the swapped level range put right",
                    q1 != null && q1.Cat == "items" && q1.Path.SequenceEqual(new[] { "weapons", "one-handed-swords" }) && q1.MinLevel == 10 && q1.MaxLevel == 20 && q1.Quality == 3 && q1.Name == "Iron Sword" && q1.How == "vendor" && q1.Src == ""
                    && q1.ToId() == "browse:items/weapons/one-handed-swords?name=Iron%20Sword&minlvl=10&maxlvl=20&q=3&how=vendor", q1?.ToId());
                Check("ids: NormalizeId passes browse ids through (canonical), a plain 'browse:' means the item list, and the text before it is not mistaken for a search",
                    AtlasQuery.NormalizeId("browse:items?q=3&minlvl=20") == "browse:items?minlvl=20&q=3" && AtlasQuery.NormalizeId("BROWSE:") == "browse:items" && AtlasBrowse.IsBrowseId("browse:zones") && !AtlasBrowse.IsBrowseId("zone:11") && AtlasQuery.Search("browse:items").Total == 0);
                var hist = new AtlasHistory(); hist.Go("browse:items"); hist.Go("browse:items?name=a"); hist.Replace("browse:items?name=ab");
                Check("ids: History.Replace swaps the id of the page shown without a new step (typing in a filter box)", hist.Count == 2 && hist.Current == "browse:items?name=ab" && hist.Back() == "browse:items");

                // ============================================================================================================ every category lists something
                var bad = new List<string>();
                foreach (var c in AtlasCategories.All.Where(c => !c.Hosted && c.Id != "classes" && c.Id != "races"))      // Classes and Races need the class tables: the atlasclasses section has them
                {
                    var r = Run(c.OpenId);
                    // the fake skills are all professions: since 2.71.1 they live on the Professions page, so Skills may be empty here
                    if (c.Id == "skills" && r.Total == 0 && !r.NotFound && (SpellData.Spells?.Skills.Values.All(s => s.Category == SkillInfo.CatProfession || AtlasNav.IsProfessionName(s.Name)) ?? true)) continue;
                    if (r.NotFound || r.Rows.Count == 0 || r.Total != r.Rows.Count || r.InCategory != r.Total || r.Title.Length == 0 || r.Rows.Any(x => string.IsNullOrEmpty(x.Link) || string.IsNullOrEmpty(x.Title) || string.IsNullOrEmpty(x.Label))) bad.Add(c.Id + " " + r + (r.NotFound ? " " + r.Message : ""));
                }
                Check("every category opens and has rows (fake data): every row has a title, a link and a source label", bad.Count == 0, string.Join("; ", bad));
                var items = Run("browse:items");
                Check("items: the count is every item of the game data plus the Classic-only one ('Classic Only Hammer'), shown as '28 items'; rows keep the icon, the quality colour and a link",
                    items.Total == 28 && items.CountText == "28 items" && T(items).Contains("Classic Only Hammer") && items.Rows.First(x => x.Title == "Ruby Ring").Quality == 4 && items.Rows.First(x => x.Title == "Ruby Ring").Link == "item:105", items.CountText);
                Check("items: top-level sub-categories (with counts) mirror the item classes: Weapons, Armor, Consumables, Trade goods, Recipes, Containers, Quest items, Projectiles, Gems, Miscellaneous",
                    new[] { "Weapons", "Armor", "Consumables", "Trade goods", "Recipes", "Containers", "Quest items", "Projectiles", "Gems", "Miscellaneous" }.All(t => items.Children.Any(k => k.Title == t && k.Count > 0)) && items.Children.First(k => k.Title == "Weapons").Count == 5 && items.Children.First(k => k.Title == "Armor").Count == 8, string.Join(",", items.Children.Select(k => k.Title + k.Count)));
                var wp = Run("browse:items/weapons");
                Check("weapons: sub-categories use the client's weapon names (one and two handed told apart): One-Handed Swords (3), Two-Handed Axes, Bows",
                    wp.Children.Any(k => k.Title == "One-Handed Swords" && k.Count == 3) && wp.Children.Any(k => k.Title == "Two-Handed Axes") && wp.Children.Any(k => k.Title == "Bows") && wp.Crumbs.Select(c => c.Title).SequenceEqual(new[] { "Atlas", "Items", "Weapons" }), string.Join(",", wp.Children.Select(k => k.Title)));
                var swords = Run("browse:items/weapons/one-handed-swords");
                Check("weapons: 'One-Handed Swords' lists exactly the three swords, with the neighbours as chips (the page is a leaf), a breadcrumb up to Weapons and the item detail line (level, item level, slot, kind)",
                    T(swords).OrderBy(x => x).SequenceEqual(new[] { "Crypt Blade", "Gnarlfang's Claw", "Iron Sword" }) && swords.ChildrenAreSiblings && swords.Children.Any(k => k.Current && k.Title == "One-Handed Swords") && swords.Crumbs.Count == 4 && swords.Crumbs[2].Link == "browse:items/weapons"
                    && swords.Rows.First(x => x.Title == "Iron Sword").Detail == "Level 5 · Item level 10 · One-Hand · One-Handed Swords", swords.Rows.First(x => x.Title == "Iron Sword").Detail);
                var armor = Run("browse:items/armor");
                Check("armor: Cloth, Leather, Mail, Plate, Shields and the jewelry/cloak group are the sub-categories, from the client's armor names", new[] { "Cloth", "Leather", "Mail", "Plate", "Shields", "Jewelry, cloaks and other" }.All(t => armor.Children.Any(k => k.Title == t)), string.Join(",", armor.Children.Select(k => k.Title)));
                Check("armor: slots are a filter (Head -> only the Plate Helm), the slot list offers what the data has", T(Run("browse:items/armor?slot=Head")).SequenceEqual(new[] { "Plate Helm" }) && armor.Controls.Slots.Any(s => s.Value == "Head") && armor.Controls.Slots.Any(s => s.Value == "Finger"));
                Check("other classes: Consumables, Trade goods (Herb, Metal and stone), Recipes (Tailoring), Containers (Bag), Projectiles (Arrow) use the client's names",
                    Run("browse:items/trade-goods").Children.Any(k => k.Title == "Herb") && Run("browse:items/trade-goods").Children.Any(k => k.Title == "Metal and stone") && Run("browse:items/recipes").Children.Any(k => k.Title == "Tailoring") && Run("browse:items/containers").Children.Any(k => k.Title == "Bag")
                    && Run("browse:items/projectiles").Children.Any(k => k.Title == "Arrow") && T(Run("browse:items/containers")).SequenceEqual(new[] { "Small Bag" }));
                Check("a path that does not exist falls back to the nearest page with a note; a category that does not exist is a calm NotFound", Run("browse:items/nonsense").Notes.Any(n => n.Contains("nonsense")) && Run("browse:items/nonsense").Total == 28 && Run("browse:nothing").NotFound && Run("browse:nothing").Message.Contains("nothing"));

                // ============================================================================================================ filters
                Check("filter: name contains narrows (every word must be in the name): 'linen' = Linen Cloth, Heavy Linen Bandage, Pattern: Linen Bag; 'linen bag' = the pattern; case does not matter",
                    T(Run("browse:items?name=linen")).OrderBy(x => x).SequenceEqual(new[] { "Heavy Linen Bandage", "Linen Cloth", "Pattern: Linen Bag" }) && T(Run("browse:items?name=LINEN%20bag")).SequenceEqual(new[] { "Pattern: Linen Bag" }));
                var lv = Run("browse:items?minlvl=22&maxlvl=30");
                Check("filter: level range (required level) = Crypt Blade 22, Ruby Ring 24, Great Axe 25, Mail Hauberk 30; an item with no level does not pass a minimum, a range with only a maximum passes level-1 items",
                    T(lv).OrderBy(x => x).SequenceEqual(new[] { "Crypt Blade", "Great Axe", "Mail Hauberk", "Ruby Ring" }) && Run("browse:items?maxlvl=1").Rows.Any(x => x.Title == "Wolf Pelt"));
                Check("filter: quality is exact (Epic = the Ruby Ring; Rare = Gnarlfang's Claw, Crypt Blade, Mail Hauberk, Plate Helm)", T(Run("browse:items?q=4")).SequenceEqual(new[] { "Ruby Ring" }) && T(Run("browse:items?q=3")).OrderBy(x => x).SequenceEqual(new[] { "Crypt Blade", "Gnarlfang's Claw", "Mail Hauberk", "Plate Helm" }));
                var lodgeOnly = Run("browse:items?src=lodge"); var gameOnly = Run("browse:items?src=game"); var classicOnly = Run("browse:items?src=classic");
                Check("filter: data source - 'Seen by the Lodge' keeps the items the Lodge saw (drops, vendors, finds), 'Your game data' leaves out the Classic-only hammer, 'Classic baseline' = items the baseline lists",
                    T(lodgeOnly).Contains("Gnarlfang's Claw") && T(lodgeOnly).Contains("Linen Cloth") && !T(lodgeOnly).Contains("Iron Sword") && gameOnly.Total == 26 && !T(gameOnly).Contains("Classic Only Hammer") && T(classicOnly).Contains("Classic Only Hammer") && T(classicOnly).Contains("Gnarlfang's Claw") && !T(classicOnly).Contains("Silk Robe"), string.Join(",", T(lodgeOnly)));
                Check("filter: labels - client items are Forever, the Classic-only hammer is 'Classic baseline'", items.Rows.First(x => x.Title == "Iron Sword").Label == AtlasLabel.Forever && items.Rows.First(x => x.Title == "Classic Only Hammer").Label == AtlasLabel.Classic);

                // how to get it
                var vend = Run("browse:items?how=vendor"); var drop = Run("browse:items?how=drop"); var quest = Run("browse:items?how=quest"); var crafted = Run("browse:items?how=crafted"); var fishing = Run("browse:items?how=fishing");
                var cont = Run("browse:items?how=container"); var gath = Run("browse:items?how=gathered");
                Check("how to get it: Vendor = what the baseline's vendor sells plus the Lodge's vendor (Linen Cloth, Gnarlfang's Claw, Iron Sword, Wooden Shield, Healing Potion, Pattern: Linen Bag, Small Bag)",
                    T(vend).OrderBy(x => x).SequenceEqual(new[] { "Gnarlfang's Claw", "Healing Potion", "Iron Sword", "Linen Cloth", "Pattern: Linen Bag", "Small Bag", "Wooden Shield" }), string.Join(",", T(vend)));
                Check("how to get it: Drops (creatures; world drops under 0.1 % do not count), Quest reward, Crafted (the client's recipes), Fishing (Lodge catches and fishing holes), Containers (chests), Gathered (herb and ore nodes)",
                    T(drop).Contains("Gnarlfang's Claw") && T(drop).Contains("Wolf Pelt") && T(drop).Contains("Great Axe") && T(drop).Contains("Ruby Ring") && !T(drop).Contains("Iron Sword")
                    && T(quest).OrderBy(x => x).SequenceEqual(new[] { "Gnarlfang's Claw", "Linen Cloth", "Mail Hauberk", "Wolf Pelt" }) && T(crafted).SequenceEqual(new[] { "Heavy Linen Bandage" })
                    && T(fishing).OrderBy(x => x).SequenceEqual(new[] { "Raw Trout", "Wolf Pelt" }) && T(cont).Contains("Ruby Ring") && T(gath).OrderBy(x => x).SequenceEqual(new[] { "Copper Ore", "Peacebloom" }),
                    "drop " + string.Join(",", T(drop)) + " | quest " + string.Join(",", T(quest)) + " | fishing " + string.Join(",", T(fishing)) + " | cont " + string.Join(",", T(cont)) + " | gath " + string.Join(",", T(gath)));
                var linenRow = vend.Rows.First(x => x.Title == "Linen Cloth"); var clawRow = vend.Rows.First(x => x.Title == "Gnarlfang's Claw"); var bandage = crafted.Rows[0];
                Check("how to get it: the row says how - 'Vendor · 1 vendor' (the same vendor seen by the Lodge and the baseline is one), 'Drop · 12% Lodge', 'Crafted · First Aid 40', and the matching way comes first when it is the filter",
                    linenRow.Extra.StartsWith("Vendor · 1 vendor") && linenRow.Extra.Contains("Quest reward · 1 quest") && clawRow.Extra.StartsWith("Vendor · 1 vendor") && clawRow.Extra.Contains("Drop · 12% Lodge") && bandage.Extra == "Crafted · First Aid 40"
                    && items.Rows.First(x => x.Title == "Gnarlfang's Claw").Extra.StartsWith("Drop · 12% Lodge"), clawRow.Extra + " / " + linenRow.Extra);
                Check("how to get it: the baseline's best chance is used when the Lodge saw nothing ('Drop · 8% Classic' for the Great Axe), and Drops shows the chance on the right", drop.Rows.First(x => x.Title == "Great Axe").Extra == "Drop · 8% Classic" && drop.Rows.First(x => x.Title == "Great Axe").Right == "8%" && drop.Rows.First(x => x.Title == "Gnarlfang's Claw").Right == "12%");

                // usable by my character
                var forElan = Run("browse:items?for=Forever-Elan");
                Check("usable by my character: a level 20 hunter gets the items of level 20 or less in cloth, leather, bows, swords, daggers ... - no mail (from 40), no plate, no shields, no Great Axe (level 25), nothing above level 20",
                    forElan.Controls.Chars.Count == 1 && forElan.Controls.Chars[0].Text == "Elan (Hunter, level 20)" && T(forElan).Contains("Short Bow") && T(forElan).Contains("Leather Vest") && T(forElan).Contains("Silk Robe") && T(forElan).Contains("Iron Sword")
                    && !T(forElan).Contains("Mail Hauberk") && !T(forElan).Contains("Plate Helm") && !T(forElan).Contains("Wooden Shield") && !T(forElan).Contains("Great Axe") && !T(forElan).Contains("Crypt Blade") && T(forElan).Contains("Healing Potion"), string.Join(",", T(forElan)));
                hunter.Level = 45;
                var forElan45 = Run("browse:items?for=Forever-Elan");
                Check("usable by my character: at level 45 the hunter wears mail (learned at 40) and wields the Great Axe, but still no plate or shield", T(forElan45).Contains("Mail Hauberk") && T(forElan45).Contains("Great Axe") && !T(forElan45).Contains("Plate Helm") && !T(forElan45).Contains("Wooden Shield"));
                hunter.Level = 20;
                Check("usable by my character: a note says what the filter did; an unknown character key is told so and filters nothing", forElan.Notes.Any(n => n.Contains("Elan") && n.Contains("up to 20")) && Run("browse:items?for=nobody").Notes.Any(n => n.Contains("not found")) && Run("browse:items?for=nobody").Total == 28);
                chars.Clear();
                Check("usable by my character: with no characters the control is not offered", !Run("browse:items").Controls.For && Run("browse:items").Total == 28);
                chars.Add(hunter);
                Check("usable by my character: the proficiency table - a warrior wields axes, a mage does not; a mage wears cloth only; an unknown class is only held to the level", ItemProficiency.CanUse("WARRIOR", 60, new BEntry { Cls = 2, Sub = 1, Level = 5 }) && !ItemProficiency.CanUse("MAGE", 60, new BEntry { Cls = 2, Sub = 1, Level = 5 })
                    && ItemProficiency.CanUse("MAGE", 10, new BEntry { Cls = 4, Sub = 1, Level = 5 }) && !ItemProficiency.CanUse("MAGE", 60, new BEntry { Cls = 4, Sub = 2, Level = 5 }) && ItemProficiency.CanUse("DEATHKNIGHT", 60, new BEntry { Cls = 4, Sub = 4, Level = 5 }) && !ItemProficiency.CanUse("DEATHKNIGHT", 4, new BEntry { Cls = 4, Sub = 4, Level = 5 }));

                // sort
                bool Sorted<T2>(IEnumerable<T2> l, Func<T2, T2, bool> inOrder) { var a = l.ToList(); for (int i = 1; i < a.Count; i++) if (!inOrder(a[i - 1], a[i])) return false; return true; }
                ClientItem CI(AtlasRow r) => ItemData.Get(int.Parse(r.Link.Substring(5)));
                var byName = Run("browse:items?sort=name"); var byLvl = Run("browse:items?sort=level"); var byQ = Run("browse:items?sort=quality"); var byIl = Run("browse:items?sort=ilvl"); var byChance = Run("browse:items?sort=chance");
                Check("sort: by name (the default, case blind), by required level (lowest first), by quality (best first), by item level (highest first)",
                    Sorted(byName.Rows, (a, b) => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase) <= 0) && byName.Sort == "name"
                    && Sorted(byLvl.Rows.Where(r => CI(r) != null), (a, b) => Math.Max(1, CI(a).RequiredLevel) <= Math.Max(1, CI(b).RequiredLevel)) && byLvl.Sort == "level"
                    && Sorted(byQ.Rows.Where(r => CI(r) != null), (a, b) => CI(a).Quality >= CI(b).Quality) && Sorted(byIl.Rows.Where(r => CI(r) != null), (a, b) => CI(a).ItemLevel >= CI(b).ItemLevel) && byIl.Rows[0].Title == "Plate Helm");
                var chanceRows = byChance.Rows.Where(r => r.Sort > 0).ToList();
                Check("sort: by best observed chance - the best first (Wolf Pelt 45 % Classic ... down), items without a chance last", byChance.Sort == "chance" && chanceRows.Count >= 4 && byChance.Rows[0].Title == "Wolf Pelt" && Sorted(chanceRows, (a, b) => a.Sort >= b.Sort) && byChance.Rows.SkipWhile(r => r.Sort > 0).All(r => r.Sort == 0), string.Join(",", byChance.Rows.Take(5).Select(r => r.Title + r.Sort)));
                Check("sort: an unknown sort is the category's own; the list order is always the same for the same id", Run("browse:items?sort=bogus").Sort == "name" && T(Run("browse:items?q=1&sort=ilvl")).SequenceEqual(T(Run("browse:items?q=1&sort=ilvl"))));

                // ============================================================================================================ the other categories
                var npcs = Run("browse:creatures");
                Check("creatures: Seen and Classic creatures together, a seen one is labelled Forever and keeps its title and rank; the sub-categories are Vendors, Trainers-less, Quest givers and the creature types",
                    npcs.Total == 37 && npcs.Rows.First(x => x.Title == "Gnarlfang").Label == AtlasLabel.Forever && npcs.Rows.First(x => x.Title == "Gnarlfang").Detail == "Level 12 · Boss · <Fanglord> · Beast" && npcs.Rows.First(x => x.Title == "Plains Wolf").Label == AtlasLabel.Classic
                    && npcs.Children.Any(k => k.Title == "Vendors" && k.Count == 1) && npcs.Children.Any(k => k.Title == "Quest givers") && !npcs.Children.Any(k => k.Title == "Trainers") && npcs.Children.Any(k => k.Title == "Beast") && npcs.Children.Any(k => k.Title == "Undead"), string.Join(",", npcs.Children.Select(k => k.Title + k.Count)));
                Check("creatures: Vendors lists the innkeeper; rank filters: Boss = Gnarlfang, Crypt Lord, Deep Hall Warden; Rare / Elite = none in this world", T(Run("browse:creatures/vendors")).SequenceEqual(new[] { "Innkeeper Hale" }) && T(Run("browse:creatures?rank=boss")).OrderBy(x => x).SequenceEqual(new[] { "Crypt Lord", "Deep Hall Warden", "Gnarlfang" }) && Run("browse:creatures?rank=rare").Total == 0 && Run("browse:creatures?rank=elite").Total == 0);
                var lvl = Run("browse:creatures?name=crypt&minlvl=20&maxlvl=25&sort=level");
                Check("creatures: level range 20-25 overlaps (Crypt Skeleton 20-22, Crypt Lord 24), sorted by level; creatures with no level (seen only) are left out of a level filter", T(lvl).SequenceEqual(new[] { "Crypt Skeleton", "Crypt Lord" }) && Run("browse:creatures?minlvl=20&maxlvl=25").Rows.All(r => r.Title != "Forever Only Boar"), string.Join(",", T(lvl)));
                Check("creatures: the zone comes from the first spawn point (Gnarlfang in Greenvale, the innkeeper in Highkeep) and filters; the zone list offers only zones that have creatures", npcs.Rows.First(x => x.Title == "Gnarlfang").Extra == "Greenvale" && T(Run("browse:creatures?zone=11")).Contains("Gnarlfang") && !T(Run("browse:creatures?zone=11")).Contains("Innkeeper Hale") && T(Run("browse:creatures?zone=13")).SequenceEqual(new[] { "Innkeeper Hale" }) && npcs.Controls.Zones.Any(z => z.Text == "Greenvale") && !npcs.Controls.Zones.Any(z => z.Text == "Barren"));
                Check("creatures: sort by zone groups them by zone name, then name", Sorted(Run("browse:creatures?zone=11&sort=zone").Rows, (a, b) => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase) <= 0));
                var objs = Run("browse:objects");
                Check("objects: Herbs (Peacebloom Patch), Ore and mining (Copper Vein), Chests and containers (Ancient Chest), Fishing (School of Fish) - sorted by what they give, filterable by zone",
                    T(Run("browse:objects/herbs")).SequenceEqual(new[] { "Peacebloom Patch" }) && T(Run("browse:objects/ore")).SequenceEqual(new[] { "Copper Vein" }) && T(Run("browse:objects/chests")).SequenceEqual(new[] { "Ancient Chest" }) && T(Run("browse:objects/fishing")).SequenceEqual(new[] { "School of Fish" })
                    && objs.Children.Count == 4 && Run("browse:objects/herbs").Rows[0].Extra == "Greenvale" && T(Run("browse:objects?zone=12")).SequenceEqual(new[] { "Copper Vein" }), string.Join(",", objs.Children.Select(k => k.Title)));
                var qs = Run("browse:quests");
                Check("quests: Seen and Classic quests (3), the level from the game's cache first, the zone through the area; level range and zone filters", qs.Total == 3 && qs.Rows.First(x => x.Title == "The Lost Pup").Detail == "Level 6 · from level 4" && qs.Rows.First(x => x.Title == "The Lost Pup").Extra == "Greenvale"
                    && T(Run("browse:quests?minlvl=7")).SequenceEqual(new[] { "A Forever Only Errand" }) && Run("browse:quests?zone=11").Total == 3 && Run("browse:quests?zone=12").Total == 0);
                var zones = Run("browse:zones");
                Check("zones: only zones (not the continent, the dungeon floors, the orphan map or the alternative copy), each with its continent and the creature level range from the baseline; by continent as sub-categories; sort by level",
                    T(zones).OrderBy(x => x).SequenceEqual(new[] { "Barren", "Greenvale", "Highkeep", "Loop A", "Loop B", "Redmoor" }) && zones.Rows.First(x => x.Title == "Greenvale").Detail.StartsWith("Northland · Levels ") && zones.Children.Count == 1 && zones.Children[0].Title == "Northland" && Run("browse:zones?sort=level").Rows[0].Detail.Contains("Levels"), string.Join(" | ", zones.Rows.Select(r => r.Detail)));
                var inst = Run("browse:instances");
                Check("dungeons and raids: sorted by level by default (Crypt of Bones from the creatures that stand in it, the raid without any level last), with Dungeons and Raids as sub-categories, filtered by level",
                    inst.Sort == "level" && inst.Rows[0].Title.Contains("Crypt") && inst.Rows[0].Detail.Contains("Levels 21-24") && inst.Children.Any(k => k.Title == "Dungeons") && inst.Children.Any(k => k.Title == "Raids") && Run("browse:instances?minlvl=22&maxlvl=30").Total == 1 && Run("browse:instances/raids").Total == 1, string.Join(" | ", inst.Rows.Select(r => r.Title + ":" + r.Detail)));
                var sp = Run("browse:spells"); var sk = Run("browse:skills"); var rc = Run("browse:recipes"); var fc = Run("browse:factions");
                Check("spells, skills, recipes, factions: Fireball and the bandage recipe; Skills without First Aid and Alchemy when the Professions page is there (else with their groups); the bandage recipe at First Aid 40 with its reagents; Stormwind in the Alliance",
                    T(sp).Contains("Fireball") && sp.Children.Any(k => k.Title == "Secondary skills")
                    && (AtlasCategories.Find(AtlasHosts.Professions)?.Hosted == true ? T(sk).Count == 0 && !sk.Children.Any(k => k.Title == "Professions") : T(sk).OrderBy(x => x).SequenceEqual(new[] { "Alchemy", "First Aid" }) && sk.Children.Any(k => k.Title == "Professions")) && T(rc).SequenceEqual(new[] { "Heavy Linen Bandage" }) && rc.Rows[0].Detail == "First Aid 40 · grey at 80"
                    && rc.Rows[0].Extra == "Uses Linen Cloth ×2" && rc.Rows[0].Link == "item:108" && fc.Rows.First(x => x.Title == "Stormwind").Detail == "in Alliance" && Run("browse:recipes?minlvl=50").Total == 0 && Run("browse:recipes?minlvl=30&maxlvl=45").Total == 1, string.Join(",", T(sp)));

                // ============================================================================================================ presets
                var pre = AtlasBrowse.Presets();
                Check("presets: the ones this data can answer - vendor items for my level (with the character), bags and containers, recipes sold by vendors, crafted items, fishing catches, best Lodge drops, herbs / ore / fishing spots by zone, dungeons by level",
                    new[] { "Vendor items for my level", "Bags and containers", "Recipes sold by vendors", "Crafted items", "Caught by fishing", "Best drops seen by the Lodge", "Herbs by zone", "Ore by zone", "Fishing spots", "Dungeons by level" }.All(t => pre.Any(p => p.Title == t && p.Count > 0)), string.Join(" | ", pre.Select(p => p.Title)));
                Check("presets: 'Rare creatures' is left out when nothing is rare; every preset id opens a non-empty list; the level preset carries the character and a level window", !pre.Any(p => p.Title == "Rare creatures") && pre.All(p => Run(p.Id).Total == p.Count && p.Count > 0)
                    && pre.First(p => p.Title == "Vendor items for my level").Id == "browse:items?minlvl=15&how=vendor&for=Forever-Elan&sort=level", pre.First(p => p.Title == "Vendor items for my level").Id);

                // ============================================================================================================ layers missing
                ClassicAtlas.Use(null); AtlasBrowse.ForgetForTest();
                var noC = Run("browse:items?how=vendor"); var noCnpc = Run("browse:creatures"); var noCobj = Run("browse:objects"); var noCzone = Run("browse:zones");
                Check("without the Classic baseline: the item list still works (the game data), 'how to get' shows only the Lodge's and the recipes, and a quiet note says to download it",
                    Run("browse:items").Total == 26 && T(noC).OrderBy(x => x).SequenceEqual(new[] { "Gnarlfang's Claw", "Linen Cloth" }) && noC.Notes.Any(n => n.Contains("Download the Classic baseline for drop and vendor sources")) && T(Run("browse:items?how=crafted")).Count == 1 && !Run("browse:items").Rows.Any(x => x.Title == "Classic Only Hammer"), noC.Notes.FirstOrDefault());
                Check("without the baseline: creatures are the ones the game saw (no levels, so a level filter finds none), objects have no herbs or ore (they say why), zones have no level range and say how to get it",
                    noCnpc.Rows.Any(x => x.Title == "Gnarlfang") && !noCnpc.Rows.Any(x => x.Title == "Plains Wolf") && Run("browse:creatures?minlvl=1").Total == 0 && noCnpc.Notes.Any(n => n.Contains("Classic baseline")) && noCobj.Notes.Any(n => n.Contains("Other"))
                    && noCzone.Rows.All(r => !r.Detail.Contains("Levels")) && noCzone.Notes.Any(n => n.Contains("Classic baseline")), noCnpc.Notes.FirstOrDefault());
                var preNC = AtlasBrowse.Presets();
                Check("without the baseline: presets that need it are left out (no herbs by zone, no dungeon levels), the Lodge's and the recipes' stay", !preNC.Any(p => p.Title == "Herbs by zone") && preNC.Any(p => p.Title == "Crafted items") && preNC.Any(p => p.Title == "Best drops seen by the Lodge"), string.Join(" | ", preNC.Select(p => p.Title)));
                AtlasQuery.Context.Lodge = () => null; AtlasBrowse.ForgetForTest();
                Check("without a Lodge: the Lodge data source selects nothing and says so; the rest is as it was", Run("browse:items?src=lodge").Total == 0 && Run("browse:items?src=lodge").Notes.Any(n => n.Contains("No Lodge observations")) && Run("browse:items").Total == 26);
                ItemData.SetForTest(null, ItemData.State.Disabled, "off"); SpellData.SetForTest(null, SpellData.State.Disabled); SeenData.SetForTest(null, SeenData.State.Disabled); MapData.SetForTest(null, null, MapData.State.Disabled); AtlasBrowse.ForgetForTest();
                var bareIds = AtlasCategories.All.Where(c => !c.Hosted).Select(c => Run(c.OpenId)).ToList();
                Check("with nothing at all every category is a calm NotFound with a message that says what is missing; no preset", bareIds.All(r => r.NotFound && r.Message.Length > 10) && AtlasBrowse.Presets().Count == 0 && bareIds[0].Message.Contains("switched off"), string.Join("; ", bareIds.Where(r => !r.NotFound).Select(r => r.Id)));
                ClassicAtlas.Use(classic);
                AllLayers();
                var itemsNoGame = Run("browse:items");
                Check("with the game data back the lists return (the index follows the data layers: 28 items again)", itemsNoGame.Total == 28 && Run("browse:creatures").Total == 37);

                // ============================================================================================================ the page in the window
                var shown = new List<string>();
                onNav = id => shown.Add(id);
                AtlasView.Navigated += onNav;
                AtlasView.FilterDebounceMsForTest = 20;
                ShowTab("atlas");
                AtlasView.History.Clear();
                await AtlasView.GoStart(); await AtlasView.StartFillForTest; await Task.Delay(100); UpdateLayout();
                var wStart0 = Width;
                FrameworkElement Tile(string openId) { var en = AtlasNav.Entries().FirstOrDefault(x => x.OpenId == openId); return en == null ? null : AtlasView.NavRowForTest(en.Key); }   // Home has no tiles since 2.74: the side menu row of the category
                var tx = AtlasTexts(AtlasView);
                Check("start page (2.74): no category tiles - each category ('Items', 'Creatures', ... 'Factions', and the hosted 'Labs') is a row of the side menu under its group (Database, World, Character), with a count from the data; no intro card",
                    cats.All(c => Tile(c.OpenId) != null) && Tile("labs:") != null && !tx.Contains("Browse") && new[] { "Database", "World", "Character" }.All(AtlasView.NavGroupsForTest.Contains) && !tx.Contains("Browse or search") && !tx.Any(t => t.Contains("Alt+Left goes back")) && AtlasBrowse.Run("browse:items").Total == 28
                    && !AtlasWalk(AtlasView.BodyForTest).OfType<ContentControl>().Any(c => c.Tag is string t && cats.Any(k => k.OpenId == t)),
                    "missing rows: " + string.Join(",", cats.Where(c => Tile(c.OpenId) == null).Select(c => c.Id)) + " labs " + (Tile("labs:") != null) + " groups " + string.Join(",", AtlasView.NavGroupsForTest) + " items row: " + string.Join("|", AtlasTexts(AtlasView.NavRowForTest("items") ?? new ContentControl())) + " tile in body " + AtlasWalk(AtlasView.BodyForTest).OfType<ContentControl>().Any(c => c.Tag is string t && cats.Any(k => k.OpenId == t)));
                Check("start page: the shortcuts - 'Recommended for' and the character chips (or 'Where to get things') with Vendor items for my level, Herbs by zone, Dungeons by level ... as buttons, none for Rare creatures", (tx.Contains("Where to get things") || tx.Contains("Recommended for")) && tx.Contains("Vendor items for my level") && tx.Contains("Herbs by zone") && tx.Contains("Dungeons by level") && !tx.Contains("Rare creatures"));
                Check("start page: no search box of its own (2.71.1); the title bar box is the search and its tooltip says you can paste a link", !AtlasTexts(AtlasView.BodyForTest).Any(t => t.StartsWith("Search items, creatures")) && TitleSearchForTest.Tag.ToString().Contains("Search") && TitleSearchForTest.ToolTip.ToString().Contains("item:2589"), TitleSearchForTest.Tag.ToString());
                Check("start page: one status line for the data (game data, Classic baseline, Lodge, world map) and no data cards on the page itself", AtlasView.StatusLineForTest != null && AtlasTexts(AtlasView.StatusLineForTest).Count >= 5 && !tx.Contains("Your game data") && !tx.Contains("Lodge pool") && AtlasView.SourceStates().Count == 4, string.Join(" | ", AtlasTexts(AtlasView.StatusLineForTest)));
                {
                    // the start page uses the width: the nav is a strip of icons when it is narrow; the same page is kept (no redraw) as the width changes
                    var wStart = Width;
                    var page0 = ((Panel)AtlasView.BodyForTest).Children.Count > 0 ? ((Panel)AtlasView.BodyForTest).Children[0] : null;
                    Width = 760; await Task.Delay(300); UpdateLayout();
                    bool stripNarrow = AtlasView.NavCollapsedForTest; double navNarrow = AtlasView.NavWidthForTest;
                    Check("start page, narrow (760): the nav is the 56 px strip of icons", stripNarrow && Math.Abs(navNarrow - 56) < 1, "nav " + navNarrow);
                    Width = 1300; await Task.Delay(300); UpdateLayout();
                    Check("start page, 1300: the 240 px nav with names", !AtlasView.NavCollapsedForTest && Math.Abs(AtlasView.NavWidthForTest - 240) < 1, "nav " + AtlasView.NavWidthForTest);
                    Width = 1700; Height = 1000; await Task.Delay(300); UpdateLayout();
                    Snapshot(Path.Combine(dir, "atlas-start-wide.png"));
                    Width = 760; await Task.Delay(300); UpdateLayout();
                    Check("start page: changing the width keeps the same page (no redraw)", page0 != null && ((Panel)AtlasView.BodyForTest).Children.Count > 0 && ReferenceEquals(page0, ((Panel)AtlasView.BodyForTest).Children[0]));
                    Snapshot(Path.Combine(dir, "atlas-start-narrow.png"));
                    Check("start page: no link chip on the start page", AtlasView.LinkShown == "");

                    // the Data sources page: the info cards side by side when the window is wide, stacked when it is narrow
                    await AtlasView.Open(AtlasNav.DataId); await Task.Delay(150); UpdateLayout();
                    FrameworkElement CardOf(string title) => AtlasWalk(AtlasView.BodyForTest).OfType<Border>().FirstOrDefault(b => b.CornerRadius.TopLeft == 12 && AtlasWalk(b).OfType<TextBlock>().Any(t => t.Text == title));
                    double CardX(string title) { var c = CardOf(title); return c == null ? -1 : c.TranslatePoint(new Point(0, 0), AtlasView.BodyForTest).X; }
                    double CardY(string title) { var c = CardOf(title); return c == null ? -1 : c.TranslatePoint(new Point(0, 0), AtlasView.BodyForTest).Y; }
                    int infoNarrow = AtlasView.InfoColumnsForTest; double gameXn = CardX("Your game data"), lodgeXn = CardX("Lodge pool"), lodgeYn = CardY("Lodge pool"), gameYn = CardY("Your game data");
                    Check("data sources, narrow (760): the four cards (game data, Classic baseline, Lodge pool, world map cache) in one stacked column", infoNarrow == 1 && gameXn >= 0 && Math.Abs(gameXn - lodgeXn) < 1 && lodgeYn > gameYn && CardOf("Classic baseline") != null && CardOf("World map cache") != null, infoNarrow + " " + gameXn + " " + lodgeXn);
                    Width = 1300; await Task.Delay(300); UpdateLayout();
                    int infoMid = AtlasView.InfoColumnsForTest; double gameXm = CardX("Your game data"), lodgeXm = CardX("Lodge pool");
                    Check("data sources, 1300: two info columns side by side (Lodge pool to the right of Your game data)", infoMid == 2 && lodgeXm > gameXm + 100, infoMid + " " + gameXm + " " + lodgeXm);
                    Width = 1900; Height = 1000; await Task.Delay(300); UpdateLayout();
                    Check("data sources, very wide (1900): three info columns", AtlasView.InfoColumnsForTest == 3, AtlasView.InfoColumnsForTest.ToString());
                    Snapshot(Path.Combine(dir, "atlas-data-wide.png"));
                    Width = wStart; Height = h0; await Task.Delay(300); UpdateLayout();
                    AtlasView.History.Clear(); await AtlasView.GoStart(); await AtlasView.StartFillForTest; await Task.Delay(100); UpdateLayout();
                }
                {
                    // ---- "Recommended for": the characters as chips, the shortcuts for the selected one, the choice kept in the settings
                    var mira = new InvChar { Key = "Forever-Mira", Name = "Mira", Realm = "Forever", Class = "HUNTER", Level = 18, Updated = 1_790_000_500 };
                    var ghost = new InvChar { Key = "Forever-Ghost", Name = "Ghost", Realm = "Forever", Class = "ROGUE", Level = 60, Hidden = true, Updated = 1_790_000_900 };
                    hunter.Updated = 1_790_000_000;
                    chars.Add(mira); chars.Add(ghost);
                    var ns = new Settings(); int nsSaves = 0;
                    AtlasView.UseNavSettings(ns, () => nsSaves++);
                    string VendorId() => AtlasView.PresetIdsForTest.FirstOrDefault(i => i.Contains("how=vendor") && i.Contains("for=")) ?? "";
                    async Task Home() { AtlasView.History.Clear(); await AtlasView.GoStart(); await AtlasView.StartFillForTest; await Task.Delay(100); UpdateLayout(); }
                    await Home();
                    var keys = AtlasView.HomeCharChipsForTest;
                    Check("recommended for: a chip per character (Elan, Mira), the hidden one (Ghost) left out, each with its name and level",
                        keys.Count == 2 && keys.Contains("Forever-Elan") && keys.Contains("Forever-Mira") && !keys.Contains("Forever-Ghost") && AtlasView.HomeCharChipTextsForTest("Forever-Mira").Contains("Mira") && AtlasView.HomeCharChipTextsForTest("Forever-Mira").Contains("Level 18")
                        && AtlasTexts(AtlasView).Contains("Recommended for") && !AtlasTexts(AtlasView).Any(t => t.StartsWith("For ")), string.Join(",", keys) + " / " + string.Join("|", AtlasView.HomeCharChipTextsForTest("Forever-Mira")));
                    Check("recommended for: nothing chosen yet - the character played last (Mira, level 18, not the highest) is selected and the shortcuts are hers (from level 13, for=Forever-Mira)",
                        AtlasView.HomeCharForTest == "Forever-Mira" && AtlasView.HomeCharChipSelectedForTest("Forever-Mira") && !AtlasView.HomeCharChipSelectedForTest("Forever-Elan") && VendorId().Contains("minlvl=13") && VendorId().Contains("for=Forever-Mira"), VendorId());
                    Width = 1300; await Task.Delay(200); UpdateLayout();
                    async Task ShowChips() { (AtlasWalk(AtlasView).OfType<ScrollViewer>().FirstOrDefault(v => v.Tag as string == "home-chars")?.Parent as FrameworkElement)?.BringIntoView(); await Task.Delay(250); UpdateLayout(); }
                    await ShowChips();
                    Snapshot(Path.Combine(dir, "atlas-home-chars-mira.png"));
                    await AtlasView.SelectHomeCharForTest("Forever-Elan"); await Task.Delay(50); UpdateLayout();
                    Check("recommended for: picking Elan's chip selects it and rebuilds the shortcuts for him (level 20: from 15, for=Forever-Elan); the choice goes to the settings",
                        AtlasView.HomeCharForTest == "Forever-Elan" && AtlasView.HomeCharChipSelectedForTest("Forever-Elan") && !AtlasView.HomeCharChipSelectedForTest("Forever-Mira") && VendorId() == "browse:items?minlvl=15&how=vendor&for=Forever-Elan&sort=level" && ns.AtlasHomeChar == "Forever-Elan",
                        VendorId() + " / " + ns.AtlasHomeChar);
                    await ShowChips();
                    Snapshot(Path.Combine(dir, "atlas-home-chars-elan.png"));
                    AtlasView.FlushNav();
                    await Home();
                    Check("recommended for: the choice is remembered - Home opens with Elan selected again (and the settings were written)", AtlasView.HomeCharForTest == "Forever-Elan" && AtlasView.HomeCharChipSelectedForTest("Forever-Elan") && nsSaves >= 1, AtlasView.HomeCharForTest + " saves " + nsSaves);
                    ns.AtlasHomeChar = "Forever-Gone";
                    await Home();
                    Check("recommended for: a remembered character that is gone falls back to the one played last (Mira)", AtlasView.HomeCharForTest == "Forever-Mira" && VendorId().Contains("for=Forever-Mira"), AtlasView.HomeCharForTest);
                    var chip = AtlasWalk(AtlasView).OfType<ContentControl>().FirstOrDefault(c => c.Tag as string == "char|Forever-Elan|off");
                    chip?.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(chip), 0, System.Windows.Input.Key.Enter) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
                    await AtlasView.SelectHomeCharForTest(AtlasView.HomeCharForTest); UpdateLayout();
                    Check("recommended for: Enter on a chip selects it (keyboard)", chip != null && chip.Focusable && AtlasView.HomeCharForTest == "Forever-Elan" && ns.AtlasHomeChar == "Forever-Elan", AtlasView.HomeCharForTest);
                    chars.Remove(mira); chars.Remove(ghost); hunter.Updated = 0;
                    AtlasView.UseNavSettings(settings, () => SettingsStore.Save(settings));
                    Width = wStart0; await Home();
                }
                {
                    // ---- every category browse page with data opens with its banner (ArtLibrary.Banners) behind the header: the category's icon, title and a line about it
                    var missing = new List<string>();
                    Width = 1300; await Task.Delay(200);
                    foreach (var c in cats)
                    {
                        await AtlasView.Open(c.OpenId); await AtlasView.WaitForTest(); await Task.Delay(60); UpdateLayout();
                        var b = AtlasView.BannerForTest;
                        bool good = AtlasView.IsBrowse && !AtlasView.BrowseModel.NotFound && b != null && b.Tag as string == "browse-banner|" + c.Id && b.IsVisible && b.ActualHeight > ElansAddonHub.AtlasPage.BannerHeight
                            && AtlasWalk(b).OfType<Viewbox>().Any(v => v.Tag as string == "atlas-icon|" + c.Id) && AtlasTexts(b).Contains(AtlasView.BrowseModel.Title);
                        if (!good && AtlasView.BrowseModel?.NotFound != true) missing.Add(c.Id + (AtlasView.BrowseModel?.NotFound == true ? "(empty)" : "") + "=" + (b?.Tag ?? "none"));
                        if (c.Id == "items" || c.Id == "creatures") { await Task.Delay(300); UpdateLayout(); Snapshot(Path.Combine(dir, "atlas-browse-banner-" + c.Id + ".png")); }
                    }
                    Check("banners: every category browse page with data (" + cats.Count + " categories; an empty one is the plain empty card) opens with its banner behind a header with the category's icon and the page title", missing.Count == 0, string.Join(", ", missing));
                    await AtlasView.Open("browse:items/weapons?name=sword"); await AtlasView.WaitForTest(); await Task.Delay(60); UpdateLayout();
                    Check("banners: a sub-category with a filter keeps its category's banner, titled by the page", AtlasView.BannerForTest?.Tag as string == "browse-banner|items" && AtlasTexts(AtlasView.BannerForTest).Contains(AtlasView.BrowseModel.Title), AtlasView.BrowseModel?.Title);
                    await AtlasView.Open(AtlasNav.FindsId); await AtlasView.WaitForTest(); await Task.Delay(60); UpdateLayout();
                    Check("banners: the Lodge's finds page has its own banner and icon (not the items one)", AtlasView.BrowseModel.NotFound || AtlasView.BannerForTest?.Tag as string == "browse-banner|finds", AtlasView.BannerForTest?.Tag as string);
                    await AtlasView.Open("labs:"); await Task.Delay(60); UpdateLayout();
                    Check("banners: a hosted page (no header) and the data sources page have none", AtlasView.BannerForTest == null);
                    await AtlasView.Open(AtlasNav.DataId); await Task.Delay(60); UpdateLayout();
                    Check("banners: the data sources page has none", AtlasView.BannerForTest == null);
                    Width = wStart0; AtlasView.History.Clear(); await AtlasView.GoStart(); await AtlasView.StartFillForTest; await Task.Delay(100); UpdateLayout();
                }
                Snapshot(Path.Combine(dir, "atlas-browse-start.png"));
                ClickRow(Tile("browse:items"));
                await AtlasView.WaitForTest(); await Task.Delay(150); UpdateLayout();
                Check("page: a tile opens its browse page - title Items, '28 items', the sub-category chips, the filter bar, a virtualised list with the rows", AtlasView.CurrentId == "browse:items" && AtlasView.IsBrowse && AtlasView.BrowseModel.Total == 28 && AtlasView.BrowseCountTextForTest == "28 items"
                    && AtlasTexts(AtlasView).Contains("Weapons") && AtlasTexts(AtlasView).Contains("Items") && AtlasView.BrowseListForTest != null && AtlasView.BrowseListForTest.Items.Count == 28 && VirtualizingPanel.GetIsVirtualizing(AtlasView.BrowseListForTest)
                    && new[] { "name", "minlvl", "maxlvl", "q", "slot", "for", "src", "how", "sort" }.All(k => AtlasView.BrowseControlForTest(k) != null) && AtlasView.BrowseControlForTest("zone") == null && AtlasView.BrowseControlForTest("rank") == null, AtlasView.CurrentId);
                Check("page: browse is an id the history knows - the start page, then Items; Back goes to the start page, Forward comes back", AtlasView.History.Count == 2 && AtlasView.BackEnabledForTest);
                ClickButton(AtlasWalk(AtlasView).OfType<Button>().First(b => AtlasTexts(b).Contains("Weapons")));
                await AtlasView.WaitForTest(); await Task.Delay(150); UpdateLayout();
                Check("page: a sub-category chip opens 'browse:items/weapons' with the breadcrumb Atlas > Items > Weapons (the links go up)", AtlasView.CurrentId == "browse:items/weapons" && AtlasView.BrowseModel.Total == 5 && AtlasTexts(AtlasView).Contains("›"));
                var name = (TextBox)AtlasView.BrowseControlForTest("name");
                int histCount = AtlasView.History.Count; shown.Clear();
                name.Text = "sword";
                bool narrowed = await WaitFor(() => AtlasView.BrowseModel != null && AtlasView.BrowseModel.Query.Name == "sword" && AtlasView.BrowseListForTest.Items.Count == 1, 5000);
                await AtlasView.BrowseWaitForTest();
                Check("page: typing in the name box narrows the list without drawing the page again (the box keeps its focus and text); the id follows with no new history step",
                    narrowed && AtlasView.CurrentId == "browse:items/weapons?name=sword" && AtlasView.History.Count == histCount && ReferenceEquals(name, AtlasView.BrowseControlForTest("name")) && name.Text == "sword" && AtlasView.BrowseCountTextForTest == "1 item" && shown.Contains("browse:items/weapons?name=sword"), AtlasView.CurrentId + " " + AtlasView.BrowseCountTextForTest);
                { var got = AtlasClickCopy(); Check("link: a browse page shows its id with the filters (browse:items/weapons?name=sword) and Copy puts exactly that on the clipboard; the link follows the filters as they change", AtlasView.LinkShown == "browse:items/weapons?name=sword" && got == "browse:items/weapons?name=sword", AtlasView.LinkShown + " / " + got); }
                name.Text = "";
                var how = (ComboBox)AtlasView.BrowseControlForTest("how");
                how.SelectedIndex = 1 + Array.IndexOf(AtlasBrowse.Hows, "vendor");
                await WaitFor(() => AtlasView.BrowseModel.Query.How == "vendor" && AtlasView.BrowseModel.Query.Name == "", 5000); await AtlasView.BrowseWaitForTest(); await Task.Delay(100); UpdateLayout();
                Check("page: the 'how to get it' dropdown filters (vendor weapons: Gnarlfang's Claw, Iron Sword), the rows show the way in their second line, the clear button appears", AtlasView.CurrentId == "browse:items/weapons?how=vendor" && AtlasView.BrowseListForTest.Items.Count == 2
                    && ((AtlasRow)AtlasView.BrowseListForTest.Items[0]).Extra.StartsWith("Vendor") && AtlasTexts(AtlasView).Contains("Clear filters"), AtlasView.CurrentId);
                var sortBox = (ComboBox)AtlasView.BrowseControlForTest("sort");
                sortBox.SelectedIndex = 3;   // item level
                await WaitFor(() => AtlasView.BrowseModel.Query.Sort == "ilvl", 5000); await AtlasView.BrowseWaitForTest();
                Check("page: the sort dropdown reorders (item level, highest first: Gnarlfang's Claw 14 before Iron Sword 10)", ((AtlasRow)AtlasView.BrowseListForTest.Items[0]).Title == "Gnarlfang's Claw" && AtlasView.CurrentId == "browse:items/weapons?how=vendor&sort=ilvl", AtlasView.CurrentId);
                Snapshot(Path.Combine(dir, "atlas-browse-weapons.png"));
                await AtlasView.Back(); await Task.Delay(150); UpdateLayout();
                Check("page: Back leaves the browse page (to Items, the page before this one), Forward returns to the page with its filters in the boxes, as they were", AtlasView.CurrentId == "browse:items");
                await AtlasView.Forward(); await Task.Delay(150); UpdateLayout();
                Check("page: ... and Forward restores 'weapons' with the vendor and item level sort set in the controls", AtlasView.CurrentId == "browse:items/weapons?how=vendor&sort=ilvl" && ((ComboBox)AtlasView.BrowseControlForTest("how")).SelectedIndex == 1 + Array.IndexOf(AtlasBrowse.Hows, "vendor") && ((ComboBox)AtlasView.BrowseControlForTest("sort")).SelectedIndex == 3 && AtlasView.BrowseListForTest.Items.Count == 2);
                ClickButton(AtlasWalk(AtlasView).OfType<Button>().First(b => AtlasTexts(b).Contains("Clear filters")));
                await AtlasView.WaitForTest(); await Task.Delay(150); UpdateLayout();
                Check("page: Clear filters opens the same category without any (the sort stays)", AtlasView.CurrentId == "browse:items/weapons?sort=ilvl" && AtlasView.BrowseListForTest.Items.Count == 5, AtlasView.CurrentId);
                await AtlasView.Open("browse:items?for=Forever-Elan&minlvl=15&how=vendor"); UpdateLayout();
                Check("page: Open('browse:...') works from outside (a link, a preset): the character dropdown shows Elan selected, the level box 15", ((ComboBox)AtlasView.BrowseControlForTest("for")).SelectedIndex == 1 && ((TextBox)AtlasView.BrowseControlForTest("minlvl")).Text == "15" && AtlasView.BrowseModel.Total == AtlasBrowse.Run("browse:items?for=Forever-Elan&minlvl=15&how=vendor").Total);
                await AtlasView.Open("browse:creatures"); UpdateLayout();
                Check("page: the creature page has a zone and a rank dropdown and no quality or slot", AtlasView.BrowseControlForTest("zone") != null && AtlasView.BrowseControlForTest("rank") != null && AtlasView.BrowseControlForTest("q") == null && AtlasView.BrowseControlForTest("slot") == null);
                await AtlasView.Open("browse:items/weapons"); await Task.Delay(200); UpdateLayout();
                await AtlasView.Open("item:100"); UpdateLayout();
                Check("page: from a list row to the item page and back to the list (row link and history)", AtlasView.Model != null && AtlasView.Model.Id == "item:100" && !AtlasView.IsBrowse);
                await AtlasView.Back(); await Task.Delay(150); UpdateLayout();
                Check("page: Back to the list shows the browse page again", AtlasView.IsBrowse && AtlasView.CurrentId == "browse:items/weapons");
                await AtlasView.Open("browse:nothing"); UpdateLayout();
                Check("page: a browse id with no such category says so calmly (no list, a hint)", AtlasView.BrowseModel.NotFound && AtlasTexts(AtlasView).Any(t => t.Contains("nothing")) && AtlasView.BrowseListForTest == null);
                await AtlasView.OpenPalette("browse:zones");
                AtlasView.PressPaletteForTest(Key.Enter);
                await WaitFor(() => AtlasView.CurrentId == "browse:zones", 5000); await AtlasView.WaitForTest();
                Check("page: typing a browse id in the palette opens it (instead of searching)", AtlasView.CurrentId == "browse:zones" && !AtlasView.PaletteOpen);

                // a hosted category: the control inside the page, through the same history
                await AtlasView.Open("labs:tree/hunter"); await Task.Delay(100); UpdateLayout();
                Check("hosted category: 'labs:tree/hunter' shows the registered control, handed the sub-path 'tree/hunter', under the category's title; it is a step in the history",
                    AtlasView.IsHosted && AtlasView.CurrentId == "labs:tree/hunter" && AtlasTexts(AtlasView).Contains("hosted labs: tree/hunter") && AtlasView.BrowseModel == null && AtlasView.Model == null && AtlasView.History.Items.Contains("labs:tree/hunter"));
                await AtlasView.Open("labs:"); await Task.Delay(100);
                await AtlasView.Back(); await Task.Delay(100); UpdateLayout();
                bool backOk = AtlasTexts(AtlasView).Contains("hosted labs: tree/hunter");
                await AtlasView.Forward(); await Task.Delay(100); UpdateLayout();
                Check("hosted category: Back and Forward re-open it with the right sub-path each time", backOk && AtlasView.CurrentId == "labs:" && AtlasTexts(AtlasView).Contains("hosted labs: "));
                await AtlasView.GoStart(); await AtlasView.StartFillForTest; UpdateLayout();
                ClickRow(Tile("labs:")); await AtlasView.WaitForTest(); await Task.Delay(100);
                Check("hosted category: its tile on the start page opens it", AtlasView.IsHosted && AtlasView.CurrentId == "labs:");
                AtlasCategories.Register(new AtlasCategory { Id = "labs", Title = "Broken", Host = sub => throw new InvalidOperationException("no") });
                await AtlasView.Open("labs:x"); UpdateLayout();
                Check("hosted category: a host that throws gives a calm page, not an exception", !AtlasView.IsHosted && AtlasTexts(AtlasView).Any(t => t.Contains("could not be opened")));
                AtlasCategories.Unregister("labs");

                // the notes that say the Classic baseline is missing carry their own "Get Classic data" button
                {
                    var fakeC = new FakeClassic { Dir = Path.Combine(work, "classicdl"), Idx = classic };
                    Directory.CreateDirectory(fakeC.Dir);
                    var setupC = new ClassicSetup(fakeC);
                    AtlasView.UseClassicSetup(setupC); AtlasView.ConfirmOverrideForTest = r => false;
                    ClassicAtlas.Use(null); AtlasBrowse.ForgetForTest();
                    await AtlasView.Open("browse:items?how=vendor"); await Task.Delay(300); UpdateLayout();
                    var gets = AtlasWalk(AtlasView).OfType<Button>().Where(b => b.Tag as string == "classic-get").ToList();
                    Check("browse notes: without the Classic baseline the note that says so has a 'Get Classic data' button of its own", AtlasView.BrowseNotesForTest.Any(n => n.Contains("Download the Classic baseline for drop and vendor sources")) && gets.Count == 1 && gets[0].Content.ToString() == "Get Classic data", gets.Count + " " + string.Join(" | ", AtlasView.BrowseNotesForTest));
                    if (gets.Count == 1) ClickButton(gets[0]);
                    await WaitFor(() => fakeC.Queries == 1 && !setupC.Busy, 5000);
                    Check("browse notes: the button starts the download (the question is asked first; 'no' downloads nothing)", fakeC.Queries == 1 && fakeC.Downloads == 0 && setupC.State == ClassicCardState.NotDownloaded);
                    await AtlasView.Open("browse:creatures"); await Task.Delay(300); UpdateLayout();
                    Check("browse notes: the creature page's note (levels, zones and spawn data are missing) has the button too", AtlasWalk(AtlasView).OfType<Button>().Count(b => b.Tag as string == "classic-get") == 1);
                    ClassicAtlas.Use(classic); AtlasBrowse.ForgetForTest(); AtlasView.ConfirmOverrideForTest = null;
                    await AtlasView.Open("browse:items?how=vendor"); await Task.Delay(300); UpdateLayout();
                    Check("browse notes: with the baseline installed there is no such note and no button", !AtlasWalk(AtlasView).OfType<Button>().Any(b => b.Tag as string == "classic-get"));
                }

                // screenshots of the lists
                Height = 1000; Width = 1100; await Task.Delay(200);
                await AtlasView.Open("browse:items/armor?minlvl=8&maxlvl=45&sort=ilvl"); await Task.Delay(300); UpdateLayout();
                Snapshot(Path.Combine(dir, "atlas-browse-armor.png"));
                await AtlasView.Open("browse:objects/herbs?sort=zone"); await Task.Delay(300); UpdateLayout();
                Snapshot(Path.Combine(dir, "atlas-browse-herbs.png"));
                Width = savedWidth; Height = h0; await Task.Delay(100);

                await AtlasFindsStripTest(dir, Check);
                // ============================================================================================================ 24 000 items
                AllLayers(24000);
                var sw = Stopwatch.StartNew();
                var big = await Task.Run(() => AtlasBrowse.Run("browse:items"));
                long first = sw.ElapsedMilliseconds; sw.Restart();
                var big2 = await Task.Run(() => AtlasBrowse.Run("browse:items/armor?minlvl=30&q=2&sort=ilvl&name=item"));
                long again = sw.ElapsedMilliseconds;
                Check("speed: 24 000 items - the first list builds the index off the UI thread in under 15 s (" + first + " ms), later filters answer from it in under 1.5 s (" + again + " ms)", big.Total == 24028 && first < 15000 && again < 1500 && big2.Total > 0, big.Total + " " + first + " " + again);
                await AtlasView.Open("browse:items"); await Task.Delay(500); UpdateLayout();
                var lb = AtlasView.BrowseListForTest;
                int realized = lb == null ? -1 : AtlasWalk(lb).OfType<ListBoxItem>().Count();
                Check("speed: the 24 028 rows are a virtualising list - only the rows in view are real controls (" + realized + ")", lb != null && lb.Items.Count == 24028 && realized > 0 && realized < 80 && AtlasView.BrowseCountTextForTest == "24,028 items", realized + " " + AtlasView.BrowseCountTextForTest);
                int ticks = 0; var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) }; timer.Tick += (s, e) => ticks++; timer.Start();
                AtlasBrowse.ForgetForTest();
                var t0 = Stopwatch.StartNew();
                var slow = AtlasView.Open("browse:items/weapons?name=item");
                await Task.Delay(300); int ticked = ticks; bool skeleton = AtlasWalk(AtlasView.BodyForTest).OfType<Skeleton>().Any();
                await slow; timer.Stop();
                Check("speed: while the index is built again (data changed) the window keeps running (timer ticks " + ticked + "), and the page is drawn after it", ticked >= 10 && AtlasView.IsBrowse && AtlasView.BrowseModel.Total > 0, ticked + " " + skeleton);
                AllLayers();

                // ============================================================================================================ the player's own game files
                await AtlasBrowseRealChecks(dir, Check);
            }
            catch (Exception e) { Check("atlasbrowse test ran to the end", false, e.ToString()); }
            finally
            {
                try
                {
                    if (onNav != null) AtlasView.Navigated -= onNav;
                    AtlasView.FilterDebounceMsForTest = 220;
                    AtlasCategories.Unregister("labs");
                    AtlasQuery.BuildHookForTest = null;
                    AtlasQuery.Context.Lodge = savedLodge; AtlasQuery.Context.NowUtc = savedNow; AtlasQuery.Context.Owned = savedOwned;
                    AtlasView.OwnedOverrideForTest = null; AtlasView.CharsOverrideForTest = null; AtlasView.LodgeState = savedLodgeState;
                    ClassicAtlas.Use(null); ItemData.ResetForTest(); SpellData.ResetForTest(); SeenData.ResetForTest(); MapData.ResetForTest(); AtlasBrowse.ForgetForTest();
                    MapRender.TileSourceForTest = savedTiles; MapRender.DirOverride = savedMapDir; MapRender.ForgetFailuresForTest();
                    Dialog.TestHook = savedHook;
                    Width = savedWidth; Height = h0;
                    ShowTab("addons");
                }
                catch { }
            }
            return string.Join("\r\n", notes) + "\r\natlasbrowse: " + (ok ? "ok" : "FAILED");
        }

        // the browse lists on the real game data (read-only), when this PC has the Forever client
        async Task AtlasBrowseRealChecks(string dir, CheckFn Check)
        {
            var real = Environment.GetEnvironmentVariable("ELANSHUB_WOW");
            if (string.IsNullOrEmpty(real)) real = WowLocator.Find();
            if (string.IsNullOrEmpty(real) || !Directory.Exists(Path.Combine(real, "Data", "data")) || !Directory.Exists(Path.Combine(real, "_classic_beta_")))
            {
                Check("real game files: skipped (no WoW install with the Forever beta client on this PC)", true);
                return;
            }
            try
            {
                var rawI = ItemData.ReadCasc(real, "_classic_beta_");
                var iset = ItemData.ParseTables(rawI.Sparse, rawI.Item, rawI.Budget);
                if (!iset.Ok) { Check("real game files: skipped (" + iset.Problem + ")", true); return; }
                ItemData.ReadCacheRows(iset, ItemData.AdbFile(real, "_classic_beta_"));
                ItemData.SetForTest(new ItemSnapshot(iset.Client, iset.Cache), ItemData.State.Ready);
                SpellData.SetForTest(SpellParse.All(SpellData.ReadCasc(real, "_classic_beta_")));
                SeenData.SetForTest(null, SeenData.State.NotStarted);
                ClassicAtlas.Use(null);
                AtlasQuery.Context.Lodge = () => null;
                AtlasBrowse.ForgetForTest();
                var sw = Stopwatch.StartNew();
                var all = await Task.Run(() => AtlasBrowse.Run("browse:items"));
                var ms = sw.ElapsedMilliseconds;
                Check("real game files: the item list has the game's items (" + all.Total + ", " + ms + " ms) and the item classes are all there", all.Total > 10000 && ms < 20000 && new[] { "Weapons", "Armor", "Consumables", "Trade goods", "Containers", "Quest items" }.All(t => all.Children.Any(k => k.Title == t && k.Count > 0)), all.Total + " " + string.Join(",", all.Children.Select(k => k.Title + k.Count)));
                var sw1 = Run1("browse:items/weapons"); var ar = Run1("browse:items/armor"); var bag = Run1("browse:items/containers"); var rec = Run1("browse:items/recipes");
                Check("real game files: weapons, armor, containers and recipes are not empty; weapons have swords and bows; armor has cloth, leather, mail and plate", sw1.Total > 500 && ar.Total > 1000 && bag.Total > 20 && rec.Total > 100
                    && sw1.Children.Any(k => k.Title == "One-Handed Swords") && sw1.Children.Any(k => k.Title == "Bows") && new[] { "Cloth", "Leather", "Mail", "Plate" }.All(t => ar.Children.Any(k => k.Title == t)), sw1.Total + " " + ar.Total + " " + bag.Total + " " + rec.Total + " " + string.Join(",", sw1.Children.Select(k => k.Title)));
                var linen = Run1("browse:items?name=linen%20cloth");
                Check("real game files: the name filter finds Linen Cloth (item:2589), and a level range / quality narrows the armor list", linen.Rows.Any(r => r.Link == "item:2589") && Run1("browse:items/armor?minlvl=40&maxlvl=45&q=3").Total > 0 && Run1("browse:items/armor?minlvl=40&maxlvl=45&q=3").Total < ar.Total);
                var recipes = Run1("browse:recipes");
                Check("real game files: recipes and professions are listed (" + recipes.Total + ") with First Aid / Tailoring among the sub-categories", recipes.Total > 100 && recipes.Children.Any(k => k.Title == "Tailoring"), string.Join(",", recipes.Children.Take(12).Select(k => k.Title)));
                var sk = Run1("browse:skills");
                Check("real game files: the Skills list has weapon skills, languages and secondary skills (riding, lockpicking), and no professions when the Professions page is there (" + sk.Total + " skill lines)",
                    sk.Children.Any(k => k.Title == "Weapon skills") && sk.Children.Any(k => k.Title == "Languages") && sk.Total > 20
                    && (AtlasCategories.Find(AtlasHosts.Professions)?.Hosted != true || (!sk.Children.Any(k => k.Title == "Professions") && !sk.Rows.Any(r => AtlasNav.IsProfessionName(r.Title)))), string.Join(",", sk.Children.Select(k => k.Title + " " + k.Count)));
            }
            catch (Exception e) { Check("real game files: the browse lists ran to the end", false, e.ToString()); }
        }

        static BrowseResult Run1(string id) => AtlasBrowse.Run(id);
    }
}
