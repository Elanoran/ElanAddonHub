using System;
using System.Collections.Generic;
using System.Linq;
using ElansAddonHub.Services.Db2;

namespace ElansAddonHub.Services
{
    // Faction icons (Outpost 2.77): the game has no icon field for a faction, so each well-known reputation faction names the game icons that stand for it
    // (the capital's mage portal, the race's portrait, the faction's insignia or tabard), best first. A faction row gets a made-up icon id (Base + faction id,
    // far above any FileDataID, next to RaceIcons' range) that IconStore turns into a picture: the names are looked up in the player's own game files (the
    // root's name hashes -> FileDataID, read-only) and the first one the game has is used. A faction of a side without its own entry falls back to that side's
    // PvP banner; anything else keeps the faction glyph. Nothing is downloaded and nothing is guessed at runtime: a name the game does not have is skipped.
    public static class FactionIcons
    {
        public const int Base = 2110000000;
        public const int MaxFaction = 1000000;
        public const string Dir = @"Interface\Icons\";

        public const string AllianceBanner = "INV_BannerPVP_02", HordeBanner = "INV_BannerPVP_01";

        // faction id -> candidates, best first: an icon name under Interface\Icons, or "race:<ChrRaces.ClientFileString>" (the race portrait, RaceIcons)
        static readonly Dictionary<int, string[]> table = new Dictionary<int, string[]>
        {
            // the two sides and their battleground groups
            [469] = new[] { AllianceBanner },                                                  // Alliance
            [67] = new[] { HordeBanner },                                                      // Horde
            [891] = new[] { AllianceBanner },                                                  // Alliance Forces
            [892] = new[] { HordeBanner },                                                     // Horde Forces
            // capitals and races
            [72] = new[] { "Spell_Arcane_TeleportStormWind", "race:Human" },                  // Stormwind
            [47] = new[] { "Spell_Arcane_TeleportIronForge", "race:Dwarf" },                  // Ironforge
            [69] = new[] { "Spell_Arcane_TeleportDarnassus", "race:NightElf" },               // Darnassus
            [54] = new[] { "race:Gnome" },                                                     // Gnomeregan Exiles
            [76] = new[] { "Spell_Arcane_TeleportOrgrimmar", "race:Orc" },                    // Orgrimmar
            [81] = new[] { "Spell_Arcane_TeleportThunderBluff", "race:Tauren" },              // Thunder Bluff
            [68] = new[] { "Spell_Arcane_TeleportUnderCity", "race:Scourge" },                // Undercity
            [530] = new[] { "race:Troll" },                                                    // Darkspear Trolls
            // battlegrounds
            [730] = new[] { "INV_Jewelry_StormPikeTrinket_01", AllianceBanner },              // Stormpike Guard
            [729] = new[] { "INV_Jewelry_FrostwolfTrinket_01", HordeBanner },                 // Frostwolf Clan
            [890] = new[] { "INV_Misc_Rune_07", AllianceBanner },                             // Silverwing Sentinels (the Warsong Gulch mark)
            [889] = new[] { "INV_Misc_Rune_07", HordeBanner },                                // Warsong Outriders
            [509] = new[] { "INV_Jewelry_Amulet_07", AllianceBanner },                        // The League of Arathor (the Arathi Basin mark)
            [510] = new[] { "INV_Jewelry_Amulet_07", HordeBanner },                           // The Defilers
            // the rest of the classic reputations
            [529] = new[] { "INV_Jewelry_Talisman_07" },                                      // Argent Dawn (its commission)
            [59] = new[] { "INV_Ingot_Thorium" },                                              // Thorium Brotherhood
            [576] = new[] { "INV_Misc_Horn_01" },                                              // Timbermaw Hold
            [609] = new[] { "Spell_Nature_Regeneration" },                                     // Cenarion Circle
            [749] = new[] { "Spell_Frost_SummonWaterElemental" },                              // Hydraxian Waterlords
            [270] = new[] { "INV_Misc_Coin_08" },                                              // Zandalar Tribe (its coins)
            [910] = new[] { "INV_Misc_Head_Dragon_Bronze" },                                   // Brood of Nozdormu
            [87] = new[] { "INV_Misc_Bandana_03" },                                            // Bloodsail Buccaneers
            [909] = new[] { "INV_Misc_Ticket_Darkmoon_01" },                                   // Darkmoon Faire
            [92] = new[] { "INV_Misc_Head_Centaur_01" },                                       // Gelkis Clan Centaur
            [93] = new[] { "INV_Misc_Head_Centaur_01" },                                       // Magram Clan Centaur
            [349] = new[] { "Ability_Stealth" },                                               // Ravenholdt
            [809] = new[] { "INV_Misc_Book_11" },                                              // Shen'dralar
            [589] = new[] { "Ability_Mount_PinkTiger", "Ability_Mount_WhiteTiger" },           // Wintersaber Trainers
            [169] = new[] { "INV_Misc_Coin_01" },                                              // Steamwheedle Cartel
            [21] = new[] { "INV_Misc_Coin_01" },                                               // Booty Bay
            [577] = new[] { "INV_Misc_Coin_01" },                                              // Everlook
            [369] = new[] { "INV_Misc_Coin_01" },                                              // Gadgetzan
            [470] = new[] { "INV_Misc_Coin_01" },                                              // Ratchet
        };

        public static IReadOnlyDictionary<int, string[]> Table => table;
        public static bool IsIcon(int id) => id >= Base && id <= Base + MaxFaction;
        public static int FactionOf(int iconId) => IsIcon(iconId) ? iconId - Base : 0;

        // the icon id of a faction row; 0 = it keeps the glyph (no entry of its own and no reputation of a side)
        public static int For(int factionId)
        {
            if (factionId <= 0 || factionId > MaxFaction) return 0;
            return Candidates(factionId).Count > 0 ? Base + factionId : 0;
        }

        // what to try for a faction, best first
        public static List<string> Candidates(int factionId)
        {
            var list = new List<string>();
            if (table.TryGetValue(factionId, out var own)) list.AddRange(own);
            var f = FactionData.Faction(factionId);
            if (f != null && f.ReputationIndex >= 0)
            {
                var side = FactionData.SideOf(factionId);
                if (side == FactionData.Alliance && !list.Contains(AllianceBanner)) list.Add(AllianceBanner);
                else if (side == FactionData.Horde && !list.Contains(HordeBanner)) list.Add(HordeBanner);
            }
            return list;
        }

        public static string PathOf(string iconName) => NamedIcons.PathOf(iconName);
    }

    // Dungeon and raid icons (Outpost 2.77): the game's own dungeon-finder pictures (Interface\LFGFrame\LFGIcon-<name>.blp) are in the client. The name is the
    // instance's own name without spaces and punctuation ("Blackrock Depths" -> BlackrockDepths, "The Deadmines" -> Deadmines), a few the game names differently
    // are listed here; when none exists, the game's generic dungeon or raid picture is used. Battlegrounds and others without a picture keep the glyph.
    public static class InstanceIcons
    {
        public const int Base = 2112000000;
        public const int MaxMap = 100000;
        public const string Lfg = @"Interface\LFGFrame\LFGIcon-";
        public static string Dungeon => Lfg + "Dungeon.blp";
        public static string Raid => Lfg + "Raid.blp";

        // the squashed instance name -> the game's own picture names (checked against build 1.60.1: --iconprobe names:...)
        static readonly Dictionary<string, string[]> alias = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["ZulFarrak"] = new[] { "ZulFarak" },
            ["OnyxiasLair"] = new[] { "OnyxiaEncounter" },
            ["AhnQirajTemple"] = new[] { "AQTemple" }, ["TempleofAhnQiraj"] = new[] { "AQTemple" },
            ["RuinsofAhnQiraj"] = new[] { "AQRuins" },
            ["Stockade"] = new[] { "StormwindStockades" }, ["StormwindStockade"] = new[] { "StormwindStockades" },
            ["LowerBlackrockSpire"] = new[] { "BlackrockSpire" },
            ["TempleofAtalHakkar"] = new[] { "SunkenTemple" }, ["TheTempleofAtalHakkar"] = new[] { "SunkenTemple" },
        };

        static readonly System.Collections.Concurrent.ConcurrentDictionary<int, string[]> known = new System.Collections.Concurrent.ConcurrentDictionary<int, string[]>();

        public static string Squash(string name) => new string((name ?? "").Where(char.IsLetterOrDigit).ToArray());

        // the pictures to try for an instance, best first (full paths)
        public static List<string> Candidates(string name, int instanceType)
        {
            var list = new List<string>();
            void Add(string n) { var p = Lfg + n + ".blp"; if (n.Length > 0 && !list.Contains(p)) list.Add(p); }
            var full = Squash(name);
            var bare = Squash(System.Text.RegularExpressions.Regex.Replace(name ?? "", @"^\s*The\s+", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
            foreach (var k in new[] { bare, full }) if (alias.TryGetValue(k, out var a)) foreach (var x in a) Add(x);
            Add(bare); Add(full);
            if (instanceType == 1) list.Add(Dungeon);
            else if (instanceType == 2) list.Add(Raid);
            return list;
        }

        // the icon id of an instance row (and its page); remembers what to try for it so IconStore can find the picture
        public static int For(int mapId, string name, int instanceType)
        {
            if (mapId < 0 || mapId > MaxMap || string.IsNullOrEmpty(name)) return 0;
            var c = Candidates(name, instanceType);
            if (c.Count == 0) return 0;
            known[mapId] = c.ToArray();
            return Base + mapId;
        }

        public static bool IsIcon(int id) => id >= Base && id <= Base + MaxMap;
        public static List<string> CandidatesOf(int iconId) => IsIcon(iconId) && known.TryGetValue(iconId - Base, out var c) ? c.ToList() : new List<string>();
    }

    // Skill lines the game gives no icon (2.77): the racial lines ("Racial - Troll", "Racial - Gnome", "Racial - Human") take the race's portrait (RaceIcons);
    // every other skill keeps its own icon or, without one, the glyph.
    public static class SkillIcons
    {
        public static int For(Db2.SkillInfo s)
        {
            if (s == null) return 0;
            if (s.IconId > 0) return s.IconId;
            var n = (s.Name ?? "").Trim();
            if (!n.StartsWith("Racial - ", StringComparison.OrdinalIgnoreCase)) return 0;
            var race = new string(n.Substring(9).Where(char.IsLetter).ToArray());
            return RaceIcons.For(race, 2);
        }
    }

    // The made-up icon ids of FactionIcons and InstanceIcons: what to try for each, and the pick of the first one the game has
    public static class NamedIcons
    {
        public static bool IsIcon(int id) => FactionIcons.IsIcon(id) || InstanceIcons.IsIcon(id);

        public static List<string> Candidates(int iconId)
            => FactionIcons.IsIcon(iconId) ? FactionIcons.Candidates(FactionIcons.FactionOf(iconId)) : InstanceIcons.CandidatesOf(iconId);

        // an icon name under Interface\Icons, or a full path (with a backslash) as it is
        public static string PathOf(string iconName) => iconName.IndexOf('\\') >= 0 ? iconName : FactionIcons.Dir + iconName + ".blp";

        // made-up icon id -> the real id IconStore reads (a FileDataID, or a RaceIcons crop id), for the ones the game has. `resolve` turns name hashes into
        // FileDataIDs (CascStorage.ResolveNameHashesToFileIds); an id none of whose candidates exists is left out (IconStore then marks it missing).
        public static Dictionary<int, int> Pick(IEnumerable<int> iconIds, Func<HashSet<ulong>, Dictionary<ulong, int>> resolve)
        {
            var res = new Dictionary<int, int>();
            var wanted = iconIds.Where(IsIcon).Distinct().ToDictionary(i => i, Candidates);
            if (wanted.Count == 0) return res;
            var names = new HashSet<ulong>(wanted.Values.SelectMany(l => l).Where(n => !n.StartsWith("race:", StringComparison.Ordinal)).Select(n => CascStorage.NameHash(PathOf(n))));
            Dictionary<ulong, int> fids;
            try { fids = names.Count == 0 ? new Dictionary<ulong, int>() : resolve(names) ?? new Dictionary<ulong, int>(); }
            catch (Exception e) { Util.Log("named icons: " + e.Message); fids = new Dictionary<ulong, int>(); }
            foreach (var kv in wanted)
                foreach (var c in kv.Value)
                {
                    int real = 0;
                    if (c.StartsWith("race:", StringComparison.Ordinal)) real = RaceIcons.For(c.Substring(5), 2);
                    else if (fids.TryGetValue(CascStorage.NameHash(PathOf(c)), out var fd) && fd > 0) real = fd;
                    if (real > 0) { res[kv.Key] = real; break; }
                }
            return res;
        }
    }
}
