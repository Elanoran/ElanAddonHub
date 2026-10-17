using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ElansAddonHub.Services.Atlas
{
    // Where the Atlas's data comes from, said the same way everywhere (2.91): the explainer on the Data sources page and the Atlas Home card,
    // the one coverage line of a creature / object / item section and of a map, and the quiet line under a browse list's count. Three kinds:
    //   1. the player's own game files (items, spells, talents, quests, maps, zones, flight paths, the creatures and objects the client has met):
    //      complete for what the client holds;
    //   2. what only the server knows (drops and rates, vendors, spawn points, nodes, flight times), learned by observation: the player's own
    //      game, their Lodge's pool and the Cairn - incomplete and growing;
    //   3. the Classic baseline (cMaNGOS classic-db, optional download): old Classic numbers as a fallback, may differ on Forever.
    // Plain, friendly, no blame. Self-test section "datahonesty".
    public static class DataHonesty
    {
        // ---- the explainer (Data sources page, Atlas Home card)
        public const string Title = "Where this comes from";
        public const string GameLead = "From your game";
        public const string GameText = "Items, spells, talents, quests, maps, zones, flight paths and the creatures and objects your game has met are read from your own game files, so they are complete for everything your game contains.";
        public const string ForeverLead = "Seen on Forever";
        public const string ForeverText = "What only the server knows - drops and drop rates, vendors, spawn points, gathering nodes and flight times - is learned as people play: from your own game, your Lodge and the Cairn. That part is still growing and has gaps.";
        public const string ClassicLead = "Classic baseline";
        public const string ClassicText = "Where nobody has seen something on Forever yet, the Atlas falls back on the Classic baseline (old Classic numbers you can download), and Forever may differ from it.";
        public const string HelpText = "You can help: share what you see with your Lodge (Settings > Lodge), and every member's Atlas fills in faster.";
        public const string MoreLink = "Data sources";
        public const string DismissText = "Got it";

        public static readonly string[] Explainer = { GameText, ForeverText, ClassicText };

        // ---- one line per section: no Forever data
        public const string NotSeenYet = "Not seen on Forever yet - showing the Classic baseline, which may differ.";
        public const string NobodyDrop = "Nobody has seen this drop on Forever yet.";
        public const string NobodyLoot = "Nobody has seen what this drops on Forever yet.";

        // ---- one line per section: what the Forever data is based on
        public const string BasedOnLead = "Seen on Forever: ";

        // "340 looted kills by 3 members of your Lodge"
        public static string ByLodge(string what, int members) =>
            what + " by " + (members > 1 ? members.ToString("N0", CultureInfo.InvariantCulture) + " members of your Lodge" : "your Lodge");
        // "200 looted kills by 3 of the Cairn's Lodges"
        public static string ByCairn(string what, int lodges) =>
            what + " by " + (lodges > 1 ? lodges.ToString("N0", CultureInfo.InvariantCulture) + " of the Cairn's Lodges" : "the Cairn's Lodges");

        // "Seen on Forever: 340 looted kills by 3 members of your Lodge and 200 looted kills by 3 of the Cairn's Lodges."
        public static string BasedOn(params string[] parts)
        {
            var p = (parts ?? new string[0]).Where(x => !string.IsNullOrEmpty(x)).ToList();
            return p.Count == 0 ? "" : BasedOnLead + string.Join(" and ", p) + ".";
        }

        // a creature / object / container loot table
        public static string LootLine(long lodgeLooted, int lodgeMembers, long cairnLooted, int cairnLodges, string word, bool classicRows)
        {
            if (lodgeLooted <= 0 && cairnLooted <= 0) return classicRows ? NotSeenYet : NobodyLoot;
            string n(long v) => AtlasFormat.Count(v, "looted " + word, "looted " + word + "s");
            return BasedOn(lodgeLooted > 0 ? ByLodge(n(lodgeLooted), lodgeMembers) : null, cairnLooted > 0 ? ByCairn(n(cairnLooted), cairnLodges) : null);
        }

        // an item's "Where it comes from": the drop and vendor rows by where they come from (recipes and quests are the game's own, not counted)
        public static string ItemSourcesLine(int lodgeSources, int cairnSources, int classicSources, int otherRows)
        {
            if (lodgeSources > 0 || cairnSources > 0)
            {
                string n(int v) => AtlasFormat.Count(v, "source", "sources");
                return BasedOn(lodgeSources > 0 ? n(lodgeSources) + " by your Lodge" : null, cairnSources > 0 ? n(cairnSources) + " by the Cairn's Lodges" : null);
            }
            if (classicSources > 0) return NotSeenYet;
            return otherRows > 0 ? "" : NobodyDrop;          // crafted / quest only: nothing to drop, nothing to say
        }

        // a vendor's "Sells"
        public static string VendorLine(bool lodge, string ago, bool classicRows) =>
            lodge ? BasedOnLead + "prices from a Lodge member's game" + (string.IsNullOrEmpty(ago) ? "" : ", " + ago) + "." : classicRows ? NotSeenYet : "";

        // a map's places (spawn points, nodes)
        public static string PlacesLine(int foreverPlaces, int classicPlaces) =>
            foreverPlaces > 0 ? BasedOn(AtlasFormat.Count(foreverPlaces, "place", "places") + " by you and your Lodge") : classicPlaces > 0 ? NotSeenYet : "";

        // ---- under a browse list's count: how much of this list Forever players have seen so far ("" = not meaningful for the list)
        public static string BrowseLine(string cat, int withForever, int total)
        {
            if (total <= 0) return "";
            string what;
            switch (cat)
            {
                case "creatures": what = "drop data from Forever players"; break;
                case "items": what = "a drop or vendor seen on Forever"; break;
                case "objects": what = "loot or places seen on Forever"; break;
                default: return "";
            }
            if (withForever <= 0) return (total == 1 ? "This one has no " : "None of these has ") + what.Replace("a drop or vendor", "drop or vendor") + " yet.";
            if (total == 1) return "This one has " + what + ".";
            return withForever.ToString("N0", CultureInfo.InvariantCulture) + " of these " + (withForever == 1 ? "has " : "have ") + what + " so far.";
        }
    }
}
