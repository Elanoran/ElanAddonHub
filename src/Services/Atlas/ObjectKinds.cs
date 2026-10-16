using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ElansAddonHub.Services.Db2;

namespace ElansAddonHub.Services.Atlas
{
    // What kind of object a game object is for the Objects browse lists, the gathering guide and the world map's node layers: herbs | ore | chests |
    // fishing | other. Pure (facts in, kind out), so the self-test can run a table through it.
    //
    // The order of the evidence (2.77; before, any herb in an object's loot made it a herb, so an Alliance chest with a Peacebloom in it was listed
    // under Herbs):
    //   1. the game object type: 25 = a fishing hole;
    //   2. the Lock it opens with (Lock.db2, the player's own game files): a lock that needs Herbalism (a skill level above 0) is a herb, Mining is
    //      ore. A lock that is known and needs neither (Lockpicking, "Open", or Herbalism 0 = a quest object anyone can gather) is never a herb or ore,
    //      whatever drops from it: a named container, or a chest-type object whose loot is more than quest items, herbs and ore, is a chest; anything
    //      else is other;
    //   3. only when the lock is unknown: the loot, but strictly. A herb needs herbs (Trade Goods / Herb) to be at least half of the distinct items
    //      and nothing a chest gives (weapons, armor, containers, recipes, money bags); ore the same with Trade Goods / Metal & Stone (gems may ride
    //      along). A name that says chest / crate / box ... is never a herb or ore;
    //   4. the name: "vein / deposit / ore / mineral" is ore, a container word is a chest; a chest-type object with loot is a chest;
    //   5. anything else is other.
    public static class ObjectKinds
    {
        public const string Herbs = GatherNodes.Herbs, Ore = GatherNodes.Ore, Chests = GatherNodes.Chests, Fishing = GatherNodes.Fishing, Other = GatherNodes.Other;
        public const int TypeQuestGiver = 2, TypeChest = 3, TypeGoober = 10, TypeFishingHole = 25;

        public sealed class Facts
        {
            public int Type;                                  // game object type (0 = unknown)
            public string Name = "";
            public LockNeed[] Lock;                           // the lock's skill entries (null = the lock is unknown)
            public List<int[]> Loot = new List<int[]>();      // { class, subclass } per distinct item (unknown items left out)
        }

        public sealed class Verdict
        {
            public string Kind;
            public string Why;                                 // "lock: Herbalism", "loot: 3 of 3 items are herbs", "name", "type" ... (the audit list)
            public override string ToString() => Kind + " (" + Why + ")";
        }

        static readonly Regex OreName = new Regex(@"\b(vein|deposit|ore|mineral)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly Regex ChestName = new Regex(@"\b(chest|crate|barrel|box|cache|footlocker|trunk|coffer|strongbox|lockbox|basket|sack|cask|urn|stash|locker|satchel)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static bool IsChestName(string name) => ChestName.IsMatch(name ?? "");
        public static bool IsOreName(string name) => OreName.IsMatch(name ?? "") && !IsChestName(name);

        static bool IsHerbItem(int[] cs) => cs[0] == 7 && cs[1] == 9;
        static bool IsOreItem(int[] cs) => cs[0] == 7 && cs[1] == 7;
        static bool IsGem(int[] cs) => cs[0] == 3;
        // what a chest gives and a herb or a vein never does: weapons, armor, bags, recipes, quivers, money / keys / junk (class 15 holds the junk and the lockboxes)
        static bool IsChestLoot(int[] cs) => cs[0] == 2 || cs[0] == 4 || cs[0] == 1 || cs[0] == 9 || cs[0] == 11 || cs[0] == 13;

        public static Verdict Classify(Facts f)
        {
            var name = f?.Name ?? "";
            int type = f?.Type ?? 0;
            if (type == TypeFishingHole) return new Verdict { Kind = Fishing, Why = "type: fishing hole" };
            bool chestNamed = IsChestName(name) && (type == 0 || type == TypeChest);   // a container's name counts on a lootable object (a shop sign "Bag Vendor" is not one)
            var loot = f?.Loot ?? new List<int[]>();
            if (f?.Lock != null)
            {
                if (f.Lock.Any(n => n.LockType == WorldLookups.LockTypeHerbalism && n.Skill > 0)) return new Verdict { Kind = Herbs, Why = "lock: Herbalism" };
                if (f.Lock.Any(n => n.LockType == WorldLookups.LockTypeMining && n.Skill > 0)) return new Verdict { Kind = Ore, Why = "lock: Mining" };
                bool questGather = f.Lock.Any(n => (n.LockType == WorldLookups.LockTypeHerbalism || n.LockType == WorldLookups.LockTypeMining) && n.Skill <= 0);
                var why = questGather ? "lock: gathered without a skill (a quest object)" : "lock: no gathering skill";
                // a container gives things a chest gives: not only quest items, herbs and ore (a quest dig site or a quest plant is not a chest)
                bool containerLoot = loot.Any(c => c[0] != 12 && !IsHerbItem(c) && !IsOreItem(c));
                if (chestNamed) return new Verdict { Kind = Chests, Why = why + ", container name" };
                if (type == TypeChest && containerLoot) return new Verdict { Kind = Chests, Why = why + ", a chest with loot" };
                return new Verdict { Kind = Other, Why = why };
            }
            int herbs = loot.Count(IsHerbItem), ores = loot.Count(IsOreItem), gems = loot.Count(IsGem), chestish = loot.Count(IsChestLoot);
            if (!chestNamed && chestish == 0 && loot.Count > 0)
            {
                if (herbs > 0 && herbs * 2 >= loot.Count && herbs >= ores) return new Verdict { Kind = Herbs, Why = "loot: " + herbs + " of " + loot.Count + " items are herbs" };
                if (ores > 0 && (ores + gems) * 2 >= loot.Count + gems && (ores + gems) * 2 > loot.Count) return new Verdict { Kind = Ore, Why = "loot: " + ores + " of " + loot.Count + " items are ore or stone" };
            }
            if (IsOreName(name) && chestish == 0 && (type == 0 || type == TypeChest)) return new Verdict { Kind = Ore, Why = "name" };
            if (chestNamed) return new Verdict { Kind = Chests, Why = "name" };
            if (type == TypeChest && loot.Count > 0) return new Verdict { Kind = Chests, Why = "type chest with loot" };
            return new Verdict { Kind = Other, Why = loot.Count > 0 ? "loot tells nothing" : "nothing known" };
        }

        // ---- the facts of a real object id: the client's cache and the Lodge (SeenView.LockOf), the Classic baseline's data0 lock, the loot of every layer
        public static int LockOf(int objectId, out bool fromClassic)
        {
            fromClassic = false;
            int lk = SeenView.LockOf(objectId);
            if (lk > 0) return lk;
            var co = Classic.ClassicAtlas.ObjectInfo(objectId);
            if (co != null && co.LockId > 0) { fromClassic = true; return co.LockId; }
            return 0;
        }

        // the lock's skill entries from the player's game files; null when the lock or the lock table is unknown
        public static LockNeed[] LockNeeds(int lockId)
        {
            if (lockId <= 0) return null;
            var w = SpellData.World;
            return w != null && w.Locks.TryGetValue(lockId, out var a) ? a : null;
        }
    }
}
