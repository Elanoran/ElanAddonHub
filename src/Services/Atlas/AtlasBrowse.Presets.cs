using System;
using System.Collections.Generic;
using System.Linq;
using ElansAddonHub.Services.Atlas.Classic;

namespace ElansAddonHub.Services.Atlas
{
    // a one-click answer to a common "where do I get ..." question: a browse id with the filters already set
    public sealed class BrowsePreset
    {
        public string Title = "", Tip = "", Id = "";
        public int Count;
    }

    public static partial class AtlasBrowse
    {
        // how many entries a query lists, without building the rows
        public static int CountMatching(BrowseQuery q)
        {
            var ix = Index(q.Cat);
            if (ix == null) return 0;
            var node = ix.Root;
            foreach (var slug in q.Path) { var k = node.Kids.FirstOrDefault(n => n.Slug == slug); if (k == null) return 0; node = k; }   // a sub-category the data does not have: nothing to show
            var chr = string.IsNullOrEmpty(q.For) ? null : AtlasQuery.Characters().FirstOrDefault(c => c.Key == q.For);
            return Filter(node == ix.Root ? ix.All : ix.All.Where(node.Pred).ToList(), q, chr, ix).Count;
        }

        // The presets the data can answer right now: one that would list nothing is left out (no vendor data = no "vendor items" button).
        // Runs on a worker thread (it builds the indexes the first time).
        public static List<BrowsePreset> Presets() => Presets(DefaultCharacter(AtlasQuery.Characters()));

        // the character Atlas Home recommends for when nothing else is chosen: the one played last (saved last), the highest level on a tie
        public static InvChar DefaultCharacter(IList<InvChar> chars) => chars == null ? null : chars.OrderByDescending(c => c.Updated).ThenByDescending(c => c.Level).FirstOrDefault();

        // the chosen character (its key) while it is still there, else the default
        public static InvChar ChosenCharacter(IList<InvChar> chars, string key) => chars?.FirstOrDefault(c => !string.IsNullOrEmpty(key) && string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase)) ?? DefaultCharacter(chars);

        // the shortcuts for one character (me = null: the ones that need no character): its level and class pick the vendor items and quest rewards, its professions the recipes
        public static List<BrowsePreset> Presets(InvChar me)
        {
            var list = new List<BrowsePreset>();
            void Add(string title, string tip, string id)
            {
                if (!BrowseQuery.TryParse(id, out var q)) return;
                int n = 0;
                try { n = CountMatching(q); } catch (Exception e) { Util.Log("atlas preset " + id + ": " + e.Message); }
                if (n > 0) list.Add(new BrowsePreset { Title = title, Tip = tip, Id = q.ToId(), Count = n });
            }
            string my = me == null ? "" : "&for=" + Uri.EscapeDataString(me.Key) + "&minlvl=" + Math.Max(1, me.Level - 5);
            string who = me == null ? "" : " (" + me.Name + ", level " + me.Level + ")";
            Add(me == null ? "Items sold by vendors" : "Vendor items for my level", "Items a vendor sells" + who + ", from the Classic baseline and the Lodge's observations", "browse:items?how=vendor&sort=level" + my);
            Add(me == null ? "Quest rewards" : "Quest rewards for my level", "Items quests give" + who, "browse:items?how=quest&sort=level" + my);
            Add("Bags and containers", "Every bag, quiver and pouch in your game data", "browse:items/containers?sort=quality");
            Add("Recipes sold by vendors", "Recipe items a vendor sells (the patterns, plans and formulae)", "browse:items/recipes?how=vendor");
            Add("Recipes you can learn", me == null ? "Recipes of your characters' professions that they have not learned yet and have the skill for (needs the profession windows read by Elan's Hub)"
                : "Recipes of " + me.Name + "'s professions not learned yet that " + me.Name + " has the skill for (needs the profession windows read by Elan's Hub)", "browse:recipes?learn=can" + (me == null ? "" : "&for=" + Uri.EscapeDataString(me.Key)));
            Add("Crafted items", "Items a profession makes, with the skill they need", "browse:items?how=crafted&sort=ilvl");
            Add("Caught by fishing", "Items from fishing holes and open water", "browse:items?how=fishing");
            Add("Best drops seen by the Lodge", "What members saw drop, best chance first", "browse:items?how=drop&src=lodge&sort=chance");
            Add("Herbs by zone", "Herb nodes, grouped by the zone they stand in", "browse:objects/herbs?sort=zone");
            Add("Ore by zone", "Mining nodes, grouped by the zone they stand in", "browse:objects/ore?sort=zone");
            Add("Fishing spots", "Fishing holes by zone", "browse:objects/fishing?sort=zone");
            Add("Rare creatures", "Rare and rare elite creatures, by level", "browse:creatures?rank=rare&sort=level");
            Add("Dungeons by level", "Dungeons and raids, lowest level first", "browse:instances?sort=level");
            return list;
        }
    }
}
