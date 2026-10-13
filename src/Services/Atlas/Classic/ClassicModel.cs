using System;
using System.Collections.Generic;

namespace ElansAddonHub.Services.Atlas.Classic
{
    public static class ClassicLabel
    {
        // every value from this layer carries this label: it is the public cMaNGOS classic-db, not what this server's client or Lodge saw
        public const string Source = "Classic baseline";
        public const string Credit = "cMaNGOS classic-db (GPL-3.0)";
    }

    public enum ClassicSourceKind : byte
    {
        NpcDrop = 1,          // creature_loot_template
        NpcSkin = 2,          // skinning_loot_template
        NpcPickpocket = 3,    // pickpocketing_loot_template
        ObjectLoot = 4,       // gameobject_loot_template: chests, herb and ore nodes, fishing holes
        Vendor = 5,           // npc_vendor (+ npc_vendor_template)
        QuestReward = 6,      // fixed quest reward
        QuestChoice = 7,      // one of the quest's choices
        QuestObjective = 8,   // the quest asks for it
    }

    // Where one item comes from. SourceId is an npc id (NpcDrop, NpcSkin, NpcPickpocket, Vendor), an object id (ObjectLoot) or a quest id.
    public struct ClassicItemSource
    {
        public ClassicSourceKind Kind;
        public int SourceId;
        public double Chance;       // percent 0..100 for the loot kinds (references and groups resolved); 0 for the others
        public bool QuestOnly;      // loot that only drops while the player has the quest
        public int MinCount, MaxCount;   // drop count, quest count, or for a vendor MaxCount = stock limit (0 = unlimited)
        public int RestockSeconds;  // vendor only (incrtime)
        public string Source => ClassicLabel.Source;
        public bool IsLoot => Kind <= ClassicSourceKind.ObjectLoot;
    }

    // World position of a spawn. Classic positions are WORLD coordinates of a map (a continent or an instance), exactly as the server stores them:
    //   Map  = the map id (0 Eastern Kingdoms, 1 Kalimdor, 30 Alterac Valley, 33 Shadowfang Keep ...)
    //   X    = north-south axis, grows towards the NORTH
    //   Y    = east-west axis, grows towards the WEST
    // Mind that this is not the (x = right, y = down) of a picture. A later Maps layer turns it into a uiMap fraction with the zone's world bounds
    // (north/south = X edges, west/east = Y edges) through ToFraction.
    public struct ClassicSpawn
    {
        public int Map;
        public float X, Y, Z;
        public int RespawnSeconds;   // spawntimesecsmin (0 if the dump has none)

        // Fractions 0..1 on a map picture (0,0 = top-left = north-west corner) for the given world bounds: northX > southX and westY > eastY. Outside = outside 0..1.
        public void ToFraction(double northX, double southX, double westY, double eastY, out double fx, out double fy)
        {
            fx = (westY - Y) / (westY - eastY);
            fy = (northX - X) / (northX - southX);
        }
    }

    public sealed class ClassicNpc
    {
        public int Id;
        public string Name, SubName;
        public int MinLevel, MaxLevel;
        public int Rank;            // 0 normal, 1 elite, 2 rare elite, 3 boss, 4 rare
        public int Type;            // creature type (1 beast, 2 dragonkin, 3 demon, 4 elemental, 5 giant, 6 undead, 7 humanoid, 8 critter, 9 mechanical, 10 not specified, 11 totem)
        public int Family;          // beast family (pets)
        public int FactionId;       // FactionTemplate id: the Outpost resolves friendly / hostile from the client's own FactionTemplate data
        public int NpcFlags;        // 0x2 quest giver, 0x80 vendor, 0x10 trainer ... (a service npc)
        public ClassicSpawn[] Spawns = new ClassicSpawn[0];
        public string Source => ClassicLabel.Source;
        public bool IsVendor => (NpcFlags & 0x80) != 0;
        public bool IsQuestGiver => (NpcFlags & 0x2) != 0;
        public string RankName => Rank == 1 ? "Elite" : Rank == 2 ? "Rare Elite" : Rank == 3 ? "Boss" : Rank == 4 ? "Rare" : "";
    }

    public sealed class ClassicObject
    {
        public int Id;
        public string Name;
        public int Type;            // 3 chest (also herb and ore nodes), 25 fishing hole, ...
        public int LootId;          // loot table of chests and nodes (data1), 0 if none
        public ClassicSpawn[] Spawns = new ClassicSpawn[0];
        public string Source => ClassicLabel.Source;
    }

    public sealed class ClassicVendorItem
    {
        public int ItemId;
        public int MaxCount;        // stock limit, 0 = unlimited
        public int RestockSeconds;
        public string Source => ClassicLabel.Source;
    }

    public sealed class ClassicQuestGiver
    {
        public bool IsObject;       // false = npc
        public int Id;
    }

    public sealed class ClassicQuest
    {
        public int Id;
        public string Title;
        public int MinLevel, QuestLevel;
        public int ZoneOrSort;      // > 0 a zone (AreaTable id), < 0 a quest sort (profession, class ...)
        public int RequiredRaces, RequiredClasses;   // bit masks, 0 = any
        public int[] RewardItems = new int[0], RewardCounts = new int[0];
        public int[] ChoiceItems = new int[0], ChoiceCounts = new int[0];
        public int[] ObjectiveItems = new int[0], ObjectiveCounts = new int[0];
        public ClassicQuestGiver[] Givers = new ClassicQuestGiver[0];
        public string Source => ClassicLabel.Source;
    }

    public sealed class ClassicItemInfo
    {
        public int Id;
        public string Name;
        public int Quality, ItemLevel, RequiredLevel;
        public string Source => ClassicLabel.Source;
    }

    public enum ClassicEntityKind : byte { Item = 1, Npc = 2, Object = 3, Quest = 4 }

    public sealed class ClassicSearchHit
    {
        public ClassicEntityKind Kind;
        public int Id;
        public string Name;
        public string Detail;       // e.g. "level 12-14 Elite", "quest level 10"
        public string Source => ClassicLabel.Source;
    }

    // Where the index came from (source.json next to the dump, copied into the index so the page can credit it).
    public sealed class ClassicProvenance
    {
        public string Repo = "https://github.com/cmangos/classic-db";
        public string DumpName;
        public string CommitSha;
        public string CommitDate;       // ISO date of the repo commit the files were fetched at
        public int UpdatesApplied;
        public string BuiltUtc;
        public int FormatVersion;
    }
}
