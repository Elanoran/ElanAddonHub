using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas.Classic;
using ElansAddonHub.Services.Db2;

namespace ElansAddonHub
{
    // --selftest "upgrades": the invented world of the upgrade engine and the Upgrades panel. Items, a Classic baseline, a Lodge pool, recipes, factions and a Hub file
    // with five characters. Nothing is read from the real game here.
    //   Aela   level 30 Night Elf (Alliance) Hunter, Marksmanship by her talents, Leatherworking 120 and she knows the vest recipe
    //   Borg   level 45 Orc (Horde) Hunter, nothing worn but a cap
    //   Cass   level 50 Human Paladin wearing a two-hand mace
    //   Dru    level 25 Human Rogue
    //   Wren   level 30 Human Warrior with two one-hand swords
    public partial class MainWindow
    {
        // item: quality, item level, required level, inventory type, class (2 weapon, 4 armor), subclass, binding, stats [type, value ...]
        static ClientItem UpI(int id, string name, int q, int il, int rl, int inv, int cls, int sub, int bond, params int[] stats)
            => new ClientItem { Id = id, Name = name, Quality = q, ItemLevel = il, RequiredLevel = rl, InventoryType = inv, ClassId = cls, SubclassId = sub, Bonding = bond, Stats = stats, IconId = 0, DelayMs = cls == 2 ? 2500 : 0 };

        const int Agi = 3, Str = 4, Int = 5, Spi = 6, Sta = 7;

        static ItemSnapshot UpItems()
        {
            var l = new List<ClientItem>
            {
                // head
                UpI(100, "Frayed Cap", 1, 10, 8, 1, 4, 2, 0, Agi, 3, Sta, 2),
                UpI(101, "Ranger's Cowl", 2, 22, 20, 1, 4, 2, 0, Agi, 9, Sta, 5),
                UpI(102, "Chain Coif", 2, 30, 28, 1, 4, 3, 0, Agi, 20, Sta, 10),
                UpI(103, "Cloth Hood of Wit", 2, 24, 20, 1, 4, 1, 0, Int, 8, Spi, 2),
                UpI(104, "Deepmaw Helm", 3, 28, 26, 1, 4, 2, 1, Agi, 15, Sta, 9),
                UpI(105, "Crypt Crown", 3, 27, 25, 1, 4, 2, 1, Agi, 13, Sta, 7),
                UpI(106, "Warchief's Cap", 3, 26, 22, 1, 4, 2, 1, Agi, 11, Sta, 6),
                UpI(107, "Cap of the Coming Day", 2, 35, 32, 1, 4, 2, 0, Agi, 20, Sta, 10),
                UpI(108, "Cap of the Far Day", 2, 38, 34, 1, 4, 2, 0, Agi, 24, Sta, 12),
                UpI(109, "Grosh's Vendor Cap", 3, 26, 22, 1, 4, 2, 1, Agi, 12, Sta, 6),
                // chest, back, rings, trinkets
                UpI(110, "Padded Vest", 1, 15, 12, 5, 4, 2, 0, Agi, 4, Sta, 4),
                UpI(111, "Studded Leather Vest", 2, 20, 18, 5, 4, 2, 2, Agi, 8, Sta, 5),
                UpI(112, "Plate Cuirass", 2, 36, 32, 5, 4, 4, 0, Str, 30, Sta, 20),
                UpI(113, "Gilded Mail Hauberk", 3, 44, 42, 5, 4, 3, 1, Agi, 30, Sta, 20),
                UpI(125, "Cloak of Shadows", 2, 18, 15, 16, 4, 1, 0, Sta, 4),
                UpI(120, "Copper Band", 2, 8, 5, 11, 4, 0, 0, Sta, 3),
                UpI(121, "Silver Band", 2, 12, 10, 11, 4, 0, 0, Agi, 4),
                UpI(122, "Gold Band of Agility", 2, 20, 15, 11, 4, 0, 0, Agi, 8),
                UpI(130, "Lucky Charm", 2, 18, 10, 12, 4, 0, 0, Agi, 3, Sta, 3),
                // weapons
                UpI(140, "Hunter's Knife", 1, 10, 8, 13, 2, 15, 0, Agi, 1),
                UpI(141, "Twin Axe", 2, 20, 18, 13, 2, 0, 0, Agi, 5, Sta, 3),
                UpI(142, "Great Staff", 2, 22, 20, 17, 2, 10, 0, Agi, 14, Sta, 8),
                UpI(143, "Round Shield", 2, 20, 18, 14, 4, 6, 0, Sta, 10),
                UpI(150, "Short Bow", 1, 10, 8, 15, 2, 2, 0),
                UpI(151, "Long Bow", 2, 20, 18, 15, 2, 2, 0, Agi, 2),
                UpI(152, "Wand of Sparks", 2, 20, 18, 26, 2, 19, 0, Spi, 3),
                // paladin
                UpI(160, "Heavy Mace", 2, 40, 38, 17, 2, 5, 0, Str, 20, Sta, 10),
                UpI(161, "Light Mace", 2, 40, 38, 13, 2, 4, 0, Str, 10, Sta, 5),
                UpI(162, "Tower Shield", 2, 40, 38, 14, 4, 6, 0, Sta, 12),
                UpI(163, "Great Maul", 3, 48, 46, 17, 2, 5, 0, Str, 28, Sta, 14),
                UpI(164, "Libram of Truth", 2, 40, 38, 28, 4, 7, 0, Str, 6),
                UpI(165, "Idol of the Wild", 2, 40, 38, 28, 4, 8, 0, Agi, 6),
                // warrior
                UpI(170, "Iron Blade", 2, 20, 18, 13, 2, 7, 0, Str, 8),
                UpI(171, "Iron Parrier", 2, 20, 18, 13, 2, 7, 0, Str, 6),
                UpI(172, "Greatmace of Might", 2, 25, 20, 17, 2, 5, 0, Str, 60, Sta, 20),
                UpI(173, "Weak Greatmace", 1, 25, 20, 17, 2, 5, 0, Str, 8),
                // rogue
                UpI(175, "Stiletto", 2, 20, 18, 13, 2, 15, 0, Agi, 6),
                UpI(176, "Rogue's Greatsword", 2, 20, 18, 17, 2, 8, 0, Agi, 20),
                UpI(177, "Dagger of the Night", 2, 14, 12, 13, 2, 15, 0, Agi, 4),
                // things the engine must never offer
                UpI(190, "Linen Cloth", 1, 5, 1, 0, 7, 0, 0),
                UpI(191, "Poor Rag Hat", 0, 5, 1, 1, 4, 1, 0, Agi, 99),
                UpI(192, "Test Helm 100", 3, 50, 1, 1, 4, 2, 0, Agi, 99),
                // enchants, food (no weapons or armor): kits and scopes are consumables of the item-enhancement kind
                UpI(114, "Soft Boots", 1, 12, 8, 8, 4, 2, 0, Agi, 2),
                UpI(600, "Rough Armor Kit", 1, 10, 1, 0, 0, 8, 0),
                UpI(601, "Crude Scope", 2, 20, 18, 0, 0, 8, 0),
                UpI(300, "Roasted Boar Meat", 1, 5, 1, 0, 0, 5, 0), UpI(301, "Spiced Wolf Ribs", 1, 20, 20, 0, 0, 5, 0), UpI(302, "Heavy Crocolisk Stew", 1, 40, 40, 0, 0, 5, 0),
                UpI(303, "Smoked Sagefish", 1, 15, 15, 0, 0, 5, 0), UpI(304, "Dried Bread", 1, 1, 1, 0, 0, 5, 0), UpI(305, "Roast Raptor", 1, 25, 25, 0, 0, 5, 0),
                UpI(306, "Conjured Water", 1, 5, 5, 0, 0, 5, 0), UpI(307, "Fresh Spring Water", 1, 25, 25, 0, 0, 5, 0), UpI(308, "Melon Juice", 1, 12, 12, 0, 0, 5, 0),
                UpI(309, "Cactus Apple Surprise", 1, 22, 22, 0, 0, 5, 0), UpI(310, "Grilled Squid", 1, 26, 26, 0, 0, 5, 0), UpI(320, "Recipe: Spiced Wolf Ribs", 1, 20, 20, 0, 9, 0, 0),
                // consumables: elixirs (2), flask (3), potions (1), scrolls (4), bandages (7), item enhancements (6); ammo is class 6 (arrow 2, bullet 3), inventory type 24
                UpI(330, "Elixir of Agility", 1, 20, 20, 0, 0, 2, 0), UpI(331, "Elixir of Giants", 1, 22, 22, 0, 0, 2, 0), UpI(332, "Elixir of the Mongoose", 1, 40, 40, 0, 0, 2, 0), UpI(333, "Flask of Titans", 1, 28, 28, 0, 0, 3, 0),
                UpI(340, "Healing Potion", 1, 20, 20, 0, 0, 1, 0), UpI(341, "Lesser Healing Potion", 1, 5, 5, 0, 0, 1, 0), UpI(342, "Mana Potion", 1, 22, 22, 0, 0, 1, 0), UpI(343, "Swiftness Potion", 1, 15, 15, 0, 0, 1, 0),
                UpI(350, "Scroll of Agility", 1, 10, 10, 0, 0, 4, 0), UpI(351, "Scroll of Strength", 1, 12, 12, 0, 0, 4, 0),
                UpI(360, "Linen Bandage", 1, 1, 1, 0, 0, 7, 0), UpI(361, "Silk Bandage", 1, 20, 20, 0, 0, 7, 0), UpI(362, "Wool Bandage", 1, 10, 10, 0, 0, 7, 0), UpI(363, "Heavy Mageweave Bandage", 1, 40, 40, 0, 0, 7, 0),
                UpI(370, "Rough Arrow", 1, 10, 1, 24, 6, 2, 0), UpI(371, "Razor Arrow", 1, 25, 25, 24, 6, 2, 0), UpI(372, "Jagged Arrow", 1, 40, 40, 24, 6, 2, 0), UpI(373, "Lead Shot", 1, 20, 15, 24, 6, 3, 0),
                UpI(380, "Rough Sharpening Stone", 1, 5, 1, 0, 0, 6, 0), UpI(382, "Instant Poison", 1, 10, 10, 0, 0, 6, 0),
            };
            return new ItemSnapshot(l, new ClientItem[0]);
        }

        // item levels 1-100: armor = item level x 1 / 2 / 3 / 4 (cloth / leather / mail / plate); one-hand 0.4, two-hand 0.45, ranged 0.5 damage per second per item level
        static ItemDetails UpDetails()
        {
            var d = new ItemDetails { ArmorQuality = new Dictionary<int, float[]>(), ArmorTotal = new Dictionary<int, float[]>(), ArmorLocation = new Dictionary<int, float[]>(), ArmorShield = new Dictionary<int, float[]>() };
            for (int il = 1; il <= 100; il++)
            {
                d.ArmorQuality[il] = Enumerable.Repeat(1f, 8).ToArray();
                d.ArmorTotal[il] = new float[] { il, il * 2f, il * 3f, il * 4f };
                d.ArmorShield[il] = Enumerable.Repeat(il * 5f, 8).ToArray();
            }
            for (int inv = 1; inv <= 28; inv++) d.ArmorLocation[inv] = new[] { 1f, 1f, 1f, 1f };
            void Dmg(int kind, float per) { d.Damage[kind] = new Dictionary<int, float[]>(); for (int il = 1; il <= 100; il++) d.Damage[kind][il] = Enumerable.Repeat(il * per, 8).ToArray(); }
            Dmg(ItemDetails.KindAmmo, 0.1f); Dmg(ItemDetails.KindOneHand, 0.4f); Dmg(ItemDetails.KindTwoHand, 0.45f); Dmg(ItemDetails.KindRanged, 0.5f); Dmg(ItemDetails.KindWand, 0.3f);
            d.Effects = new Dictionary<int, int[]>
            {
                [600] = new[] { 0, 7101 }, [601] = new[] { 0, 7102 }, [300] = new[] { 0, 7301 }, [301] = new[] { 0, 7302 }, [303] = new[] { 0, 7311 }, [304] = new[] { 0, 7312 }, [305] = new[] { 0, 7313 },
                [306] = new[] { 0, 7321 }, [307] = new[] { 0, 7322 }, [308] = new[] { 0, 7323 }, [309] = new[] { 0, 7330 }, [310] = new[] { 0, 7340 }, [302] = new[] { 0, 7350 }, [320] = new[] { 6, 7401 },
                [330] = new[] { 0, 7801 }, [331] = new[] { 0, 7802 }, [332] = new[] { 0, 7803 }, [333] = new[] { 0, 7804 }, [340] = new[] { 0, 7811 }, [341] = new[] { 0, 7812 }, [342] = new[] { 0, 7813 }, [343] = new[] { 0, 7814 },
                [350] = new[] { 0, 7821 }, [351] = new[] { 0, 7822 }, [360] = new[] { 0, 7831 }, [361] = new[] { 0, 7832 }, [362] = new[] { 0, 7833 }, [363] = new[] { 0, 7834 }, [380] = new[] { 0, 7701 }, [382] = new[] { 0, 7702 },
            };
            return d;
        }

        static ClassicIndex UpClassic()
        {
            var idx = new ClassicIndex();
            idx.Provenance = new ClassicProvenance { DumpName = "ClassicDB_up.sql.gz", CommitSha = "up1", CommitDate = "2024-05-02T10:00:00Z", BuiltUtc = "2024-06-01T00:00:00Z", FormatVersion = 1 };
            void Npc(int id, string name, int min, int max, int rank, int faction, int flags, params ClassicSpawn[] sp)
                => idx.Npcs[id] = new ClassicNpc { Id = id, Name = name, MinLevel = min, MaxLevel = max, Rank = rank, FactionId = faction, NpcFlags = flags, Spawns = sp };
            Npc(201, "Quartermaster Voss", 25, 25, 0, 14, 0x80, Sp(0, 500, 1000));                 // an Alliance vendor in Greenvale
            Npc(202, "Quartermaster Grosh", 25, 25, 0, 15, 0x80, Sp(0, 450, 1000));                // a Horde vendor
            Npc(203, "Peddler Nim", 20, 20, 0, 35, 0x80, Sp(0, 520, 1100));                        // sells to everyone
            Npc(204, "Crypt Lord", 24, 24, 3, 35, 0, Sp(99, 50, 50));                              // dungeon boss
            Npc(205, "Deep Hall Warden", 40, 40, 3, 35, 0, Sp(98, 10, 10));                        // raid boss
            Npc(206, "Plains Stalker", 18, 20, 0, 35, 0, Sp(0, 300, 700), Sp(0, 320, 720));        // a world creature in Greenvale
            Npc(207, "Plains Raider", 28, 29, 0, 35, 0, Sp(0, 350, 900));
            Npc(208, "Ironhide Brute", 52, 52, 1, 35, 0, Sp(0, 400, 800));                         // far above a level 30 character
            Npc(209, "Elder Moss", 30, 30, 0, 14, 0x2, Sp(0, 505, 1005));                          // Alliance quest giver
            Npc(210, "Orc Taskmaster", 30, 30, 0, 15, 0x2, Sp(0, 455, 1005));                      // Horde quest giver
            idx.Quests[60] = new ClassicQuest { Id = 60, Title = "Scout the Vale", MinLevel = 10, QuestLevel = 12, ZoneOrSort = 78, ChoiceItems = new[] { 101 }, ChoiceCounts = new[] { 1 }, Givers = new[] { new ClassicQuestGiver { Id = 209 } } };
            idx.Quests[61] = new ClassicQuest { Id = 61, Title = "Warchief's Errand", MinLevel = 15, QuestLevel = 20, ZoneOrSort = 78, RequiredRaces = 2 | 16 | 32 | 128, RewardItems = new[] { 106 }, RewardCounts = new[] { 1 }, Givers = new[] { new ClassicQuestGiver { Id = 210 } } };
            idx.Quests[62] = new ClassicQuest { Id = 62, Title = "The Paladin's Trial", MinLevel = 40, QuestLevel = 45, ZoneOrSort = 78, RequiredClasses = 2, RewardItems = new[] { 163 }, RewardCounts = new[] { 1 }, Givers = new[] { new ClassicQuestGiver { Id = 209 } } };
            idx.Quests[63] = new ClassicQuest { Id = 63, Title = "Too Early", MinLevel = 40, QuestLevel = 41, ZoneOrSort = 78, RewardItems = new[] { 122 }, RewardCounts = new[] { 1 }, Givers = new[] { new ClassicQuestGiver { Id = 209 } } };
            void Src(int item, ClassicSourceKind k, int src, double chance = 0, int stock = 0) => idx.Add(item, new ClassicItemSource { Kind = k, SourceId = src, Chance = chance, MaxCount = stock, MinCount = 1 });
            Src(101, ClassicSourceKind.Vendor, 201); Src(101, ClassicSourceKind.QuestChoice, 60);
            Src(102, ClassicSourceKind.Vendor, 203);
            Src(104, ClassicSourceKind.NpcDrop, 205, 8);
            Src(105, ClassicSourceKind.NpcDrop, 204, 12);
            Src(106, ClassicSourceKind.QuestReward, 61);
            Src(107, ClassicSourceKind.Vendor, 201);
            Src(108, ClassicSourceKind.Vendor, 201);
            Src(109, ClassicSourceKind.Vendor, 202);
            Src(110, ClassicSourceKind.Vendor, 203);
            Src(112, ClassicSourceKind.Vendor, 203);
            Src(113, ClassicSourceKind.NpcDrop, 208, 30);
            Src(125, ClassicSourceKind.Vendor, 203);
            Src(130, ClassicSourceKind.Vendor, 201);
            Src(122, ClassicSourceKind.NpcDrop, 207, 0.5); Src(122, ClassicSourceKind.QuestReward, 63);
            Src(141, ClassicSourceKind.Vendor, 203);
            Src(142, ClassicSourceKind.Vendor, 203);
            Src(151, ClassicSourceKind.NpcDrop, 206, 25);
            Src(163, ClassicSourceKind.QuestReward, 62); Src(163, ClassicSourceKind.Vendor, 203);
            Src(160, ClassicSourceKind.Vendor, 203); Src(161, ClassicSourceKind.Vendor, 203); Src(162, ClassicSourceKind.Vendor, 203);
            Src(172, ClassicSourceKind.Vendor, 203); Src(173, ClassicSourceKind.Vendor, 203);
            Src(175, ClassicSourceKind.Vendor, 203); Src(176, ClassicSourceKind.Vendor, 203); Src(177, ClassicSourceKind.Vendor, 203);
            Src(120, ClassicSourceKind.Vendor, 203); Src(121, ClassicSourceKind.Vendor, 203);
            Src(190, ClassicSourceKind.Vendor, 203);
            foreach (var fid in new[] { 300, 301, 302, 303, 304, 305, 306, 307, 308, 309, 310, 320, 600, 601, 330, 331, 332, 333, 340, 341, 342, 343, 350, 351, 360, 362, 363, 370, 371, 372, 373, 380, 382 }) Src(fid, ClassicSourceKind.Vendor, 203);
            idx.Finish();
            return idx;
        }


        // the Lodge saw: Crypt Lord (in the crypt) drop the crown at 15 %, Plains Stalker drop the gold band 9 % and the long bow 30 %, the mystery ring, a vendor with the cowl
        static LodgeAtlas UpLodge()
        {
            var json = "{\"t\":\"atlas.data\",\"epoch\":\"u1\",\"rev\":3,\"at\":" + (AtNow - 600) + ",\"full\":true,"
                + "\"kills\":{\"204\":{\"n\":200,\"m\":3,\"items\":{\"105\":{\"n\":30,\"c\":30}},\"z\":{\"20\":200}},\"206\":{\"n\":100,\"m\":2,\"items\":{\"122\":{\"n\":9,\"c\":9},\"151\":{\"n\":30,\"c\":30},\"555\":{\"n\":6,\"c\":6}},\"z\":{\"11\":100}}},"
                + "\"vendors\":{\"201\":{\"at\":" + (AtNow - 3600) + ",\"z\":11,\"x\":0.5,\"y\":0.5,\"items\":{\"101\":{\"p\":12050,\"s\":1,\"l\":-1}}}},"
                + "\"items\":{\"555\":{\"n\":\"Mystery Ring\",\"q\":4,\"il\":22,\"rl\":15,\"cls\":4,\"sub\":0,\"slot\":11,\"st\":[[3,20],[7,10]]}}}";
            var l = new LodgeAtlas();
            if (!l.Apply(Json.Obj(json))) throw new InvalidOperationException("the invented atlas.data page was refused");
            return l;
        }

        // spells for the recipe: Leatherworking (skill 165) makes the vest (item 111) from spell 5001 at skill 110
        static AtlasSet UpSpells()
        {
            var spells = UpSpellList(); var skills = UpSkillList(); var abilities = UpAbilityList();
            var factions = new FactionSnapshot(
                new[] { new FactionInfo { Id = 1, Name = "Alliance Players" }, new FactionInfo { Id = 2, Name = "Horde Players" }, new FactionInfo { Id = 3, Name = "Stormwind" }, new FactionInfo { Id = 4, Name = "Orgrimmar" } },
                new[]
                {
                    new FactionTemplateInfo { Id = 1, FactionId = 1, Group = FactionTemplateInfo.GroupPlayer | FactionTemplateInfo.GroupAlliance },
                    new FactionTemplateInfo { Id = 2, FactionId = 2, Group = FactionTemplateInfo.GroupPlayer | FactionTemplateInfo.GroupHorde },
                    new FactionTemplateInfo { Id = 14, FactionId = 3, Group = FactionTemplateInfo.GroupMonster, FriendGroup = FactionTemplateInfo.GroupAlliance, EnemyGroup = FactionTemplateInfo.GroupHorde },
                    new FactionTemplateInfo { Id = 15, FactionId = 4, Group = FactionTemplateInfo.GroupMonster, FriendGroup = FactionTemplateInfo.GroupHorde, EnemyGroup = FactionTemplateInfo.GroupAlliance },
                    new FactionTemplateInfo { Id = 35, FactionId = 5, Group = FactionTemplateInfo.GroupMonster },
                });
            return new AtlasSet { Spells = new SpellSnapshot(spells, skills, abilities, UpReagents()), Factions = factions, Details = UpDetails() };
        }

        // ElansHub.lua with the five characters (core, gear with links, talents, professions)
        static string UpHubLua(long now)
        {
            string Link(int id, string name) => "[" + id + "]=\"|cffffffff|Hitem:" + id + "::::::::|h[" + name + "]|h|r\"";
            string Mod(string name, string data) => "['" + name + "']={v=1,at=" + (now - 60) + ",by='ElansHub',ver='1.14.0',data={" + data + "}},";
            string Core(string name, string cls, string clsFile, string race, string faction, int level) =>
                Mod("core", "name='" + name + "',realm='Forever',class='" + cls + "',classFile='" + clsFile + "',race='" + race + "',raceFile='" + race.Replace(" ", "") + "',sex=2,faction='" + faction + "',level=" + level + ",xp=1,xpMax=2,rested=0,money=10000,guild='',zone='Greenvale',subzone='Mill',updated=" + (now - 60));
            var items = UpItems();
            string Gear(params int[][] slots)
            {
                var sb = new StringBuilder("at=" + (now - 60) + ",slots={");
                var links = new StringBuilder();
                foreach (var s in slots)
                {
                    var it = items.Get(s[1]);
                    sb.Append("[" + s[0] + "]={i=" + s[1] + ",c=1,q=" + it.Quality + ",ic=0,il=" + it.ItemLevel + "},");
                    links.Append(Link(s[1], it.Name) + ",");
                }
                return sb + "},links={" + links + "}";
            }
            // three trees side by side; the points per tree decide the build
            string Talents(int p1, int p2, int p3)
            {
                var nodes = new StringBuilder();
                int nid = 1;
                int[] pts = { p1, p2, p3 }, x0 = { 1800, 5400, 9000 };
                for (int t = 0; t < 3; t++)
                    for (int col = 0; col < 3; col++)
                        for (int row = 0; row < 3; row++, nid++)
                            nodes.Append("[" + nid + "]={id=" + nid + ",r=" + ((nid - 1) % 9 < pts[t] / 3 ? 3 : 0) + ",m=3,x=" + (x0[t] + col * 600) + ",y=" + (600 + row * 600) + ",s=" + (1000 + nid) + "},\n");
                return "at=" + (now - 60) + ",api='traits',spent=" + (p1 + p2 + p3) + ",unspent=0,nodes={\n" + nodes + "}";
            }
            var s2 = new StringBuilder("ElansHubData = {\n['schema']=1,\n['chars']={\n");
            s2.Append("['Forever-Aela']={at=" + (now - 60) + ",mods={\n");
            s2.Append(Core("Aela", "Hunter", "HUNTER", "Night Elf", "Alliance", 30));
            s2.Append(Mod("gear", Gear(new[] { 1, 100 }, new[] { 11, 120 }, new[] { 12, 121 }, new[] { 16, 140 }, new[] { 18, 150 })));
            s2.Append(Mod("talents", Talents(3, 27, 3)));
s2.Append(Mod("professions", "at=" + (now - 60) + ",list={{name='Leatherworking',skill=120,max=150,ic=1,kind='primary',line=165},{name='Enchanting',skill=150,max=225,ic=1,kind='primary',line=333},{name='Cooking',skill=60,max=75,ic=1,kind='secondary',line=185}},known={[165]={n='Leatherworking',at=" + (now - 60) + ",partial=false,ids={5001}},[333]={n='Enchanting',at=" + (now - 60) + ",partial=false,ids={7001}},[185]={n='Cooking',at=" + (now - 60) + ",partial=false,ids={7401}}}"));
            s2.Append("}},\n");
            s2.Append("['Forever-Borg']={at=" + (now - 60) + ",mods={\n");
            s2.Append(Core("Borg", "Hunter", "HUNTER", "Orc", "Horde", 45));
            s2.Append(Mod("gear", Gear(new[] { 1, 100 })));
            s2.Append("}},\n");
            s2.Append("['Forever-Cass']={at=" + (now - 60) + ",mods={\n");
            s2.Append(Core("Cass", "Paladin", "PALADIN", "Human", "Alliance", 50));
            s2.Append(Mod("gear", Gear(new[] { 16, 160 })));
            s2.Append("}},\n");
            s2.Append("['Forever-Dru']={at=" + (now - 60) + ",mods={\n");
            s2.Append(Core("Dru", "Rogue", "ROGUE", "Human", "Alliance", 25));
            s2.Append(Mod("gear", Gear(new[] { 16, 177 })));
            s2.Append("}},\n");
            s2.Append("['Forever-Wren']={at=" + (now - 60) + ",mods={\n");
            s2.Append(Core("Wren", "Warrior", "WARRIOR", "Human", "Alliance", 30));
            s2.Append(Mod("gear", Gear(new[] { 16, 170 }, new[] { 17, 171 })));
            s2.Append("}},\n");
            s2.Append("},\n['account']={['mods']={}}}\n");
            return s2.ToString();
        }

        // the characters as the Atlas sees them (the Hub's record on an InvChar)
        static List<InvChar> UpInvChars(HubModel hub)
        {
            var l = new List<InvChar>();
            foreach (var hc in hub.Characters)
            {
                var core = hc.Core;
                l.Add(new InvChar { Key = hc.Key, Name = core?.Name ?? hc.Key, Realm = "Forever", Class = core?.ClassFile, Level = core?.Level ?? 0, RaceFile = core?.RaceFile, Faction = core?.Faction, Hub = hc, Core = core });
            }
            return l;
        }
    }
}
