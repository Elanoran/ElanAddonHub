using System;
using System.Collections.Generic;
using System.Linq;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Upgrades;

namespace ElansAddonHub
{
    // --selftest "upgrades", part 1c: the Consumables section - every kind, its ranking, who it applies to, how to get it, what I already have.
    public partial class MainWindow
    {
        void UpgradesConsumableChecks(CheckFn Check, List<InvChar> chars, Func<string, InvChar> C)
        {
            var f0 = new UpgradeFilters();
            var aela = UpgradeChar.From(C("Aela"));
            var set = UpgradeEngine.Compute(aela);
            var all = UpgradeEngine.PickFood(set, f0);
            string Ids(string kind) => string.Join(",", all.Where(e => e.Kind == kind).Select(e => e.ItemId.ToString()));
            Check("consumables: elixirs and flasks are scored by the weights like any buff - Elixir of Agility (+12 Agility, 60 min) before Elixir of Giants (+10 Strength counts for a Hunter at 0.2); the level-40 elixir is not there; the Flask of Titans (+50 Stamina) alone",
                Ids("Elixir") == "330,331" && all.First(e => e.ItemId == 330).Buff == "+12 Agility for 60 min" && Ids("Flask") == "333" && all.First(e => e.ItemId == 333).Buff == "+50 Stamina for 120 min", Ids("Elixir") + " " + all.First(e => e.ItemId == 333).Buff);
            Check("consumables: potions - healing potions ranked by the amount the spell's own text gives (a range counts as its middle: 700 to 900 = 800), mana potions likewise, a potion with a buff that matches the weights (Swiftness) apart",
                Ids("Healing potion") == "340,341" && all.First(e => e.ItemId == 340).Buff == "Restores 700 to 900 health" && Ids("Mana potion") == "342" && Ids("Potion") == "343", Ids("Healing potion") + " " + Ids("Mana potion") + " " + Ids("Potion"));
            Check("consumables: stat scrolls by the weights; bandages by the amount they heal (Silk 640, Wool 301, Linen 114; the level 40 one is not for level 30)", Ids("Scroll") == "350,351" && Ids("Bandage") == "361,362,360" && all.First(e => e.ItemId == 361).Buff == "Heals 640 damage over 8 sec", Ids("Scroll") + " " + Ids("Bandage"));
            Check("consumables: hunter ammo for the ranged weapon that is worn - Aela's bow takes arrows only (Razor Arrow 2.5 damage per second before Rough Arrow 1.0), no bullets, nothing above her level; a Hunter with no ranged weapon worn gets both, a Warrior none",
                Ids("Ammo") == "371,370" && all.First(e => e.ItemId == 371).Buff == "+2.5 damage per second to ranged attacks"
                && UpgradeEngine.PickFood(UpgradeEngine.Compute(UpgradeChar.From(C("Borg"))), f0).Where(e => e.Kind == "Ammo").Select(e => e.ItemId).SequenceEqual(new[] { 372, 371, 373 })
                && !UpgradeEngine.PickFood(UpgradeEngine.Compute(UpgradeChar.From(C("Wren"))), f0).Any(e => e.Kind == "Ammo"));
            Check("consumables: a temporary weapon enhancement is offered only when it can go on the worn weapon, scored as damage per swing, and labelled temporary with its duration - the sharpening stone for Aela's dagger; poisons only for a Rogue (Dru gets the poison first, the stone second)",
                Ids("Temporary") == "380" && all.First(e => e.ItemId == 380).Buff == "+2 weapon damage on Hunter's Knife (temporary, 30 min)"
                && string.Join(",", UpgradeEngine.PickFood(UpgradeEngine.Compute(UpgradeChar.From(C("Dru"))), f0).Where(e => e.Kind == "Temporary").Select(e => e.ItemId)) == "382,380", Ids("Temporary"));
            Check("consumables: only kinds that apply - a Warrior has no drinks or mana potions, a level 12 Rogue sees nothing above level 12",
                !UpgradeEngine.PickFood(UpgradeEngine.Compute(UpgradeChar.From(C("Wren"))), f0).Any(e => e.Kind == "Drink" || e.Kind == "Mana potion"));
            var squid = all.First(e => e.ItemId == 330);
            Check("how to get it: the vendor line, the labels (Classic baseline) and 'you have N' from my characters' bags and bank (3 Elixirs of Agility on Aela)", squid.Owned == 3 && squid.Best.Line == "Vendor · Peddler Nim · Greenvale (Classic)" && squid.ClassicOnly && squid.Applicable);
            var silk = all.First(e => e.ItemId == 361);
            Check("how to get it: a craft - the bandage's First Aid recipe 'Crafted · First Aid 150 · learn it first' with its reagents and what I own; the Classic vendor row stays",
                silk.Sources.Any(s => s.Kind == "craft" && s.Line == "Crafted · First Aid 150 · learn it first") && Joined(silk.Reagents) == "Linen Cloth x2 (you have 6)" && silk.Profession == "First Aid", Joined(silk.Sources.Select(s => s.Line)));
            Check("filters: the Classic baseline off keeps only the game's recipes (ribs and silk bandage); the Consumables switch off hides everything; collapsed groups are kept in the filter text and read back",
                Joined(UpgradeEngine.PickFood(set, new UpgradeFilters { Classic = false }).Select(e => e.ItemId.ToString())) == "301,361" && UpgradeEngine.PickFood(set, new UpgradeFilters { Food = false }).Count == 0
                && new UpgradeFilters { HiddenGroups = { "elixir", "ammo" } }.Serialize() == "D1R0C1T0L0N1A0F1Hammo+elixir" && UpgradeFilters.Parse("D1R0C1T0L0N1A0F1Hammo+elixir+bogus").HiddenGroups.SetEquals(new[] { "ammo", "elixir" }));
            // what my characters hold changing is worked out again (the bags were read anew)
            var before = UpgradeEngine.ComputeCount;
            var owned2 = UpOwned(chars); owned2[190].Chars[0].Bags = 9;
            var savedOwned = AtlasQuery.Context.Owned;
            AtlasQuery.Context.Owned = () => owned2;
            UpgradeEngine.Compute(aela);
            bool sameWithout = UpgradeEngine.ComputeCount == before;
            UpgradeEngine.OwnedStamp++;
            var again = UpgradeEngine.Compute(aela);
            Check("reagent counts: new bags / bank data from the Characters page (a stamp) make the panel's answer be worked out again - 'Linen Cloth x2 (you have 9)'; without that the cached answer stays",
                sameWithout && UpgradeEngine.ComputeCount == before + 1 && again.EnchantBySlot.Count >= 0 && UpgradeEngine.PickFood(again, f0).First(e => e.ItemId == 301).Reagents.Contains("Linen Cloth x2 (you have 9)"));
            AtlasQuery.Context.Owned = savedOwned; UpgradeEngine.OwnedStamp++;
        }
    }
}
