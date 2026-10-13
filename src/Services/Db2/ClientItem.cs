using System;
using System.Collections.Generic;

namespace ElansAddonHub.Services.Db2
{
    // What the player's own game files say about one item. Nothing here is shipped with the Outpost: it is read from the user's WoW install.
    public sealed class ClientItem
    {
        public static readonly int[] NoStats = new int[0];

        public int Id;
        public string Name = "", Description = "";
        public int Quality, ItemLevel, RequiredLevel, InventoryType;
        public int ClassId = -1, SubclassId = -1;     // from Item.db2 (or the cache's Item rows); -1 = unknown
        public int IconId;                            // FileDataID of the icon (Item.db2), 0 = unknown
        public int Bonding, Stackable, DelayMs, ContainerSlots;
        public long SellPrice, BuyPrice;              // copper
        // stat type / amount pairs, flattened: [type0, value0, type1, value1 ...]. null = the amounts could not be worked out
        // (the game stores them as a share of an item-level budget; without that table nothing is guessed), empty = no stats.
        public int[] Stats = NoStats;
        public string Source = "client";              // "client" (shipped item table) or "cache" (a row the server pushed to this client: a revealed item)
        public int[] StatsRaw;                        // while loading: [type, share of the budget (1/10000)] pairs, turned into Stats by ItemTables.ResolveStats

        public bool Revealed => Source == "cache";
        public int StatCount => Stats == null ? 0 : Stats.Length / 2;
        public bool Equippable => InventoryType > 0 && InventoryType != 18 && InventoryType != 24 && InventoryType != 27;
    }

    // human text for the numbers in the tables
    public static class ItemNames
    {
        static readonly string[] Slots =
        {
            null, "Head", "Neck", "Shoulder", "Shirt", "Chest", "Waist", "Legs", "Feet", "Wrist", "Hands", "Finger", "Trinket",
            "One-Hand", "Off Hand", "Ranged", "Back", "Two-Hand", "Bag", "Tabard", "Chest", "Main Hand", "Off Hand", "Held In Off-hand",
            "Ammo", "Thrown", "Ranged", "Quiver", "Relic",
        };

        public static string Slot(int inventoryType) => inventoryType > 0 && inventoryType < Slots.Length ? Slots[inventoryType] : null;

        static readonly string[] Classes =
        {
            "Consumable", "Container", "Weapon", "Gem", "Armor", "Reagent", "Projectile", "Trade Goods", "Item Enhancement", "Recipe", "Money",
            "Quiver", "Quest", "Key", "Permanent", "Miscellaneous",
        };

        public static string Class(int classId) => classId >= 0 && classId < Classes.Length ? Classes[classId] : null;

        static readonly string[] Weapons =
        {
            "Axe", "Axe", "Bow", "Gun", "Mace", "Mace", "Polearm", "Sword", "Sword", "Warglaives", "Staff", "Bear Claw", "Cat Claw", "Fist Weapon",
            "Miscellaneous", "Dagger", "Thrown", "Spear", "Crossbow", "Wand", "Fishing Pole",
        };
        static readonly string[] Armors = { "Miscellaneous", "Cloth", "Leather", "Mail", "Plate", "Cosmetic", "Shield", "Libram", "Idol", "Totem", "Sigil", "Relic" };

        // the right-hand word of the slot line: "Sword", "Cloth", "Shield" ... (null when there is nothing useful)
        public static string Subclass(int classId, int subclassId)
        {
            if (subclassId < 0) return null;
            if (classId == 2) return subclassId < Weapons.Length ? Weapons[subclassId] : null;
            if (classId == 4) return subclassId > 0 && subclassId < Armors.Length ? Armors[subclassId] : null;
            return null;
        }

        public static string Bonding(int b)
        {
            switch (b)
            {
                case 1: return "Binds when picked up";
                case 2: return "Binds when equipped";
                case 3: return "Binds when used";
                case 4: case 5: return "Quest Item";
                default: return null;
            }
        }

        // the ITEM_MOD_* numbers of the item tables. Resistances and the like included; unknown numbers are shown as such, never guessed.
        public static string Stat(int type)
        {
            switch (type)
            {
                case 0: return "Mana";
                case 1: return "Health";
                case 3: return "Agility";
                case 4: return "Strength";
                case 5: return "Intellect";
                case 6: return "Spirit";
                case 7: return "Stamina";
                case 12: return "Defense Rating";
                case 13: return "Dodge Rating";
                case 14: return "Parry Rating";
                case 15: return "Block Rating";
                case 16: return "Melee Hit Rating";
                case 17: return "Ranged Hit Rating";
                case 18: return "Spell Hit Rating";
                case 19: return "Melee Critical Strike Rating";
                case 20: return "Ranged Critical Strike Rating";
                case 21: return "Spell Critical Strike Rating";
                case 31: return "Hit Rating";
                case 32: return "Critical Strike Rating";
                case 35: return "Resilience";
                case 36: return "Haste Rating";
                case 37: return "Expertise Rating";
                case 38: return "Attack Power";
                case 39: return "Ranged Attack Power";
                case 41: return "Healing Power";
                case 42: return "Spell Damage";
                case 43: return "Mana per 5 sec.";
                case 44: return "Armor Penetration";
                case 45: return "Spell Power";
                case 46: return "Health per 5 sec.";
                case 47: return "Spell Penetration";
                case 48: return "Block Value";
                case 50: return "Bonus Armor";
                case 51: return "Fire Resistance";
                case 52: return "Frost Resistance";
                case 53: return "Holy Resistance";
                case 54: return "Shadow Resistance";
                case 55: return "Nature Resistance";
                case 56: return "Arcane Resistance";
                default: return "other bonus (" + type + ")";
            }
        }
    }

    // The lines of an item tooltip, built from the client data. Pure text so the self-test can check them; the Characters page draws them.
    public sealed class TipLine
    {
        public string Kind;          // ilvl | slot | speed | bag | bind | stat | req | desc | price | unrevealed
        public string Text;          // the line (left part)
        public string Right;         // right-hand part of a "slot" line
        public long Copper;          // price lines
        public override string ToString() => Kind + ": " + Text + (Right != null ? " | " + Right : "") + (Kind == "price" ? " | " + Copper : "");
    }

    public static class ItemText
    {
        public const string NotRevealed = "Not revealed yet on WoW Forever";

        public static List<TipLine> Lines(ClientItem it)
        {
            var l = new List<TipLine>();
            if (it == null) return l;
            if (it.ItemLevel > 0 && (it.Equippable || it.ClassId == 2 || it.ClassId == 4))
                l.Add(new TipLine { Kind = "ilvl", Text = "Item Level " + it.ItemLevel });
            var slot = ItemNames.Slot(it.InventoryType);
            var sub = ItemNames.Subclass(it.ClassId, it.SubclassId);
            if (it.Equippable && slot != null)
                l.Add(new TipLine { Kind = "slot", Text = slot, Right = sub });
            else if (it.ClassId >= 0)
            {
                var cls = ItemNames.Class(it.ClassId);
                if (cls != null && it.ClassId != 15) l.Add(new TipLine { Kind = "slot", Text = cls, Right = null });
            }
            if (it.ClassId == 2 && it.DelayMs > 0)
                l.Add(new TipLine { Kind = "speed", Text = "Speed " + (it.DelayMs / 1000.0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) });
            if (it.ContainerSlots > 0)
                l.Add(new TipLine { Kind = "bag", Text = it.ContainerSlots + " Slot " + (it.ClassId == 11 ? "Quiver" : "Bag") });
            var bind = ItemNames.Bonding(it.Bonding);
            if (bind != null) l.Add(new TipLine { Kind = "bind", Text = bind });
            if (it.Stats != null)
                for (int i = 0; i + 1 < it.Stats.Length; i += 2)
                {
                    int v = it.Stats[i + 1];
                    if (v == 0) continue;
                    l.Add(new TipLine { Kind = "stat", Text = (v > 0 ? "+" : "-") + Math.Abs(v).ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + ItemNames.Stat(it.Stats[i]) });
                }
            if (it.RequiredLevel > 1) l.Add(new TipLine { Kind = "req", Text = "Requires Level " + it.RequiredLevel });
            if (!string.IsNullOrWhiteSpace(it.Description)) l.Add(new TipLine { Kind = "desc", Text = "\"" + it.Description.Trim() + "\"" });
            if (it.SellPrice > 0) l.Add(new TipLine { Kind = "price", Text = "Sell Price:", Copper = it.SellPrice });
            return l;
        }

        // gold / silver / copper parts of an amount (zero parts left out except a lone 0c)
        public static List<KeyValuePair<long, char>> Coins(long copper)
        {
            var r = new List<KeyValuePair<long, char>>();
            long g = copper / 10000, s = copper / 100 % 100, c = copper % 100;
            if (g > 0) r.Add(new KeyValuePair<long, char>(g, 'g'));
            if (s > 0) r.Add(new KeyValuePair<long, char>(s, 's'));
            if (c > 0 || r.Count == 0) r.Add(new KeyValuePair<long, char>(c, 'c'));
            return r;
        }
    }
}
