using System.Collections.Generic;

namespace ElansAddonHub.Services
{
    // Names for the ids the companion's status strip sends. Open-world zones come as a uiMapID
    // (C_Map.GetBestMapForUnit) - the table is taken from the Questie Forever snapshot (areaId -> uiMapId, zone rows only).
    // Dungeons, raids and battlegrounds come as the instance (map) id of GetInstanceInfo, which is stable and well known.
    // An id that isn't listed gives no text - the hub then simply shows nothing for the zone.
    public static class ZoneNames
    {
        public static readonly Dictionary<int, string> Maps = new Dictionary<int, string>
        {
            [1411] = "Durotar", [1412] = "Mulgore", [1413] = "The Barrens", [1414] = "Kalimdor", [1415] = "Eastern Kingdoms",
            [1416] = "Alterac Mountains", [1417] = "Arathi Highlands", [1418] = "Badlands", [1419] = "Blasted Lands",
            [1420] = "Tirisfal Glades", [1421] = "Silverpine Forest", [1422] = "Western Plaguelands", [1423] = "Eastern Plaguelands",
            [1424] = "Hillsbrad Foothills", [1425] = "The Hinterlands", [1426] = "Dun Morogh", [1427] = "Searing Gorge",
            [1428] = "Burning Steppes", [1429] = "Elwynn Forest", [1430] = "Deadwind Pass", [1431] = "Duskwood", [1432] = "Loch Modan",
            [1433] = "Redridge Mountains", [1434] = "Stranglethorn Vale", [1435] = "Swamp of Sorrows", [1436] = "Westfall",
            [1437] = "Wetlands", [1438] = "Teldrassil", [1439] = "Darkshore", [1440] = "Ashenvale", [1441] = "Thousand Needles",
            [1442] = "Stonetalon Mountains", [1443] = "Desolace", [1444] = "Feralas", [1445] = "Dustwallow Marsh", [1446] = "Tanaris",
            [1447] = "Azshara", [1448] = "Felwood", [1449] = "Un'Goro Crater", [1450] = "Moonglade", [1451] = "Silithus",
            [1452] = "Winterspring", [1453] = "Stormwind City", [1454] = "Orgrimmar", [1455] = "Ironforge", [1456] = "Thunder Bluff",
            [1457] = "Darnassus", [1458] = "Undercity", [1459] = "Alterac Valley", [1460] = "Warsong Gulch", [1461] = "Arathi Basin",
            // WoW Forever additions
            [2482] = "Mount Hyjal", [2521] = "Zephras Isle", [2524] = "Darkspear Islands", [2548] = "Riverglades", [2652] = "Shen'dralas",
        };

        // GetInstanceInfo's instance id (the 8th return value)
        public static readonly Dictionary<int, string> Instances = new Dictionary<int, string>
        {
            [30] = "Alterac Valley", [33] = "Shadowfang Keep", [34] = "The Stockade", [36] = "The Deadmines", [43] = "Wailing Caverns",
            [47] = "Razorfen Kraul", [48] = "Blackfathom Deeps", [70] = "Uldaman", [90] = "Gnomeregan", [109] = "Sunken Temple",
            [129] = "Razorfen Downs", [189] = "Scarlet Monastery", [209] = "Zul'Farrak", [229] = "Blackrock Spire", [230] = "Blackrock Depths",
            [249] = "Onyxia's Lair", [269] = "The Black Morass", [289] = "Scholomance", [309] = "Zul'Gurub", [329] = "Stratholme",
            [349] = "Maraudon", [389] = "Ragefire Chasm", [409] = "Molten Core", [429] = "Dire Maul", [469] = "Blackwing Lair",
            [489] = "Warsong Gulch", [509] = "Ruins of Ahn'Qiraj", [529] = "Arathi Basin", [531] = "Temple of Ahn'Qiraj",
            [532] = "Karazhan", [533] = "Naxxramas", [534] = "Hyjal Summit", [540] = "The Shattered Halls", [542] = "The Blood Furnace",
            [543] = "Hellfire Ramparts", [544] = "Magtheridon's Lair", [545] = "The Steamvault", [546] = "The Underbog", [547] = "The Slave Pens",
            [548] = "Serpentshrine Cavern", [550] = "Tempest Keep", [552] = "The Arcatraz", [553] = "The Botanica", [554] = "The Mechanar",
            [555] = "Shadow Labyrinth", [556] = "Sethekk Halls", [557] = "Mana-Tombs", [558] = "Auchenai Crypts", [560] = "Old Hillsbrad Foothills",
            [564] = "Black Temple", [565] = "Gruul's Lair", [566] = "Eye of the Storm", [568] = "Zul'Aman", [580] = "Sunwell Plateau",
            [585] = "Magisters' Terrace",
        };

        public static string Map(int id) => id > 0 && Maps.TryGetValue(id, out var n) ? n : null;
        public static string Instance(int id) => id > 0 && Instances.TryGetValue(id, out var n) ? n : null;
    }
}
