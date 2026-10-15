using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace ElansAddonHub.Services.Atlas
{
    // ============================================================================================================================
    // The Atlas categories: the tiles of the start page and the roots of the browse ids. A registry, not a switch:
    //   * a LIST category ("items", "creatures" ...) opens a browse page: id "browse:items/weapons/swords?minlvl=20" (AtlasBrowse builds it);
    //   * a HOSTED category (Host != null) opens a UserControl inside the Atlas page instead of a list. Its ids are "<id>:" + a sub-path
    //     ("talents:", "talents:tree/hunter"), they go through the same history, back / forward and Open(id) as every other id, and the
    //     page hands the sub-path to Host each time it is shown.
    // Register / Unregister are the hook points: another page moves into the Atlas by registering one hosted category, nothing else changes.
    // ============================================================================================================================
    public sealed class AtlasCategory
    {
        public string Id = "";                 // "items", "talents" ...: also the id prefix of a hosted category
        public string Title = "", Glyph = "", Blurb = "";
        public string Kind = "";               // list categories: the AtlasKinds kind the rows open ("item", "npc" ...)
        public int Order = 100;                // start page order (built-in categories use 10, 20 ...)
        public Func<string, FrameworkElement> Host;     // hosted: builds the control for a sub-path ("" = the root); runs on the UI thread
        public Func<string, string> Canonical;          // hosted: the sub-path the control really shows after Host(sub) ran (the id then follows it), null = as asked
        public Func<string, string> SubTitle;           // hosted: the last crumb for a sub-path ("Hunter" for "tree/hunter"), null / "" = none
        public Func<string, IEnumerable<AtlasHit>> Search;   // hosted: the pages of this category a search text names ("hunter talents", "cooking"); runs on a worker
        public Func<bool> Available = () => true;       // a category without its data hides itself
        public string Group = "";                       // the nav group (AtlasNav.Database / World / Character / Lodge); "" = Database
        public string NavKey = "";                      // categories with the same key share ONE nav row ("Spells and talents"); "" = the category's own id
        public string NavTitle = "";                    // the title of that shared row; "" = this category's title
        public bool NavPrimary;                         // in a shared nav row: this category is where the row opens (default: the first by Order)
        public string Keywords = "";                    // extra words the Ctrl+K palette matches the page by ("herbs ore fishing")
        public bool Hosted => Host != null;
        public string OpenId => Hosted ? Id + ":" : "browse:" + Id;
    }

    public static class AtlasCategories
    {
        static readonly List<AtlasCategory> list = new List<AtlasCategory>();
        static readonly object gate = new object();
        public static event Action Changed;      // the start page redraws its tiles

        static AtlasCategories()
        {
            void Add(string id, string title, string kind, string glyphKind, string blurb, int order, string group, string navKey = "", string navTitle = "", string keywords = "")
                => list.Add(new AtlasCategory { Id = id, Title = title, Kind = kind, Glyph = AtlasKinds.Glyph(glyphKind), Blurb = blurb, Order = order, Group = group, NavKey = navKey, NavTitle = navTitle, Keywords = keywords });
            Add("items", "Items", "item", "item", "Weapons, armor, potions, bags, recipes and more", 10, AtlasNav.Database, keywords: "weapons armor potions bags gear");
            Add("creatures", "Creatures", "npc", "npc", "Vendors, trainers, rares, bosses and everything that walks", 20, AtlasNav.Database, keywords: "npc monsters mobs bosses rares vendors trainers");
            Add("objects", "Objects", "obj", "obj", "Herbs, ore, chests, fishing holes", 30, AtlasNav.Database, keywords: "herbs ore chests fishing nodes gathering");
            Add("quests", "Quests", "quest", "quest", "By level and zone", 40, AtlasNav.Database);
            Add("zones", "Zones", "zone", "zone", "By continent, with their level range", 50, AtlasNav.World, keywords: "areas places");
            Add("instances", "Dungeons and raids", "instance", "instance", "Sorted by level", 60, AtlasNav.World, keywords: "dungeons raids instances");
            Add("spells", "Spells", "spell", "spell", "Class abilities, weapon and armor skills, recipes", 70, AtlasNav.Character, "spells", "Spells and talents", "abilities");
            Add("skills", "Skills and professions", "skill", "skill", "Professions, weapon skills, languages", 80, AtlasNav.Character, "professions", "Professions", "weapon skills languages");
            Add("recipes", "Recipes", "spell", "spell", "What each profession makes, by skill level", 90, AtlasNav.Character, "professions", "Professions", "crafting patterns");
            Add("factions", "Factions", "faction", "faction", "Reputations", 100, AtlasNav.Character, keywords: "reputation");
            Add("classes", "Classes", "class", "class", "Every class: spells by level, proficiencies, races", 72, AtlasNav.Character, keywords: "warrior paladin hunter rogue priest shaman mage warlock druid spellbook");
            Add("races", "Races", "race", "race", "Every playable race: racial abilities, classes, side", 74, AtlasNav.Character, keywords: "human orc dwarf night elf undead tauren gnome troll skyborne racial");
        }

        public static IReadOnlyList<AtlasCategory> All { get { lock (gate) return list.OrderBy(c => c.Order).ThenBy(c => c.Title, StringComparer.OrdinalIgnoreCase).ToList(); } }
        public static IReadOnlyList<AtlasCategory> Visible { get { return All.Where(c => { try { return c.Available == null || c.Available(); } catch (Exception) { return false; } }).ToList(); } }

        public static AtlasCategory Find(string id)
        {
            lock (gate) return list.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        // adds a category, or replaces the one with the same id
        public static void Register(AtlasCategory c)
        {
            if (c == null || string.IsNullOrWhiteSpace(c.Id)) throw new ArgumentException("A category needs an id.");
            c.Id = c.Id.Trim().ToLowerInvariant();
            if (c.Host != null && c.Id.IndexOf(':') >= 0) throw new ArgumentException("A hosted category id is the prefix before the colon.");
            lock (gate) { list.RemoveAll(x => x.Id == c.Id); list.Add(c); }
            Changed?.Invoke();
        }

        public static bool Unregister(string id)
        {
            bool gone;
            lock (gate) gone = list.RemoveAll(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase)) > 0;
            if (gone) Changed?.Invoke();
            return gone;
        }

        // "talents:tree/hunter" -> the hosted category "talents" and "tree/hunter"; false for anything else
        public static bool TryHosted(string id, out AtlasCategory cat, out string sub)
        {
            cat = null; sub = "";
            if (string.IsNullOrEmpty(id)) return false;
            int c = id.IndexOf(':');
            if (c <= 0) return false;
            var c0 = Find(id.Substring(0, c));
            if (c0 == null || !c0.Hosted) return false;
            cat = c0; sub = id.Substring(c + 1).Trim('/');
            return true;
        }

        public static bool IsHosted(string id) => TryHosted(id, out _, out _);

        // the pages of the hosted categories a search text names: the top group of the search overlay
        public static List<AtlasHit> SearchHits(string text)
        {
            var hits = new List<AtlasHit>();
            if (string.IsNullOrWhiteSpace(text)) return hits;
            List<AtlasCategory> cats;
            lock (gate) cats = list.Where(c => c.Hosted && c.Search != null).ToList();
            foreach (var c in cats)
            {
                try { hits.AddRange(c.Search(text.Trim()) ?? Enumerable.Empty<AtlasHit>()); }
                catch (Exception) { }
            }
            return hits;
        }

        // an id the Atlas page opens itself instead of building an entity page: a browse page or a hosted category
        public static bool IsRouted(string id) => AtlasBrowse.IsBrowseId(id) || IsHosted(id) || AtlasNav.IsPage(id);
    }
}
