using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Upgrades;

namespace ElansAddonHub
{
    // --selftest "upgrades", part 4: items the character cannot use (class, race, profession, reputation, PvP rank) are listed greyed, below the usable ones, and are
    // never the best upgrade. Synthetic items on the Hunter Aela (Night Elf, Alliance, Leatherworking 120, Enchanting 150, Cooking 60); the real files are looked at in UpgradesUsableReal.
    public partial class MainWindow
    {
        // a leather head piece for Aela (level 30): inventory type 1, armor, leather; stats Agility
        static ClientItem UsableHead(int id, string name, int agi, Action<ClientItem> restrict = null)
        {
            var it = UpI(id, name, 3, 28, 25, 1, 4, 2, 0, Agi, agi, Sta, 5);
            restrict?.Invoke(it);
            return it;
        }

        // the bit mask of races by name (Alliance / Horde of the classic client: race id - 1)
        const long HordeBits = (1L << 1) | (1L << 4) | (1L << 5) | (1L << 7) | (1L << 9), AllianceBits = (1L << 0) | (1L << 2) | (1L << 3) | (1L << 6) | (1L << 10);

        void UpgradesUsableChecks(CheckFn Check, UpgradeChar aela, List<string> notes)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            // ---- the sparse record: the new fields are read from their offsets, and the saved index keeps them
            var L = ItemTables.SparseFor(0x6FCC3191);
            var rec = new byte[5 + 320];
            int f0 = 5;
            void P16(int off, int v) => BitConverter.GetBytes((short)v).CopyTo(rec, f0 + off);
            BitConverter.GetBytes(6).CopyTo(rec, f0 + 164);                       // MinReputation
            BitConverter.GetBytes(-6184943489809468494L).CopyTo(rec, f0 + 172);   // AllowableRace
            P16(274, 529); P16(276, 245); P16(278, 202); P16(282, 8);             // MinFactionID, RequiredSkillRank, RequiredSkill, AllowableClass
            rec[f0 + 297] = 14; rec[f0 + 298] = 30; rec[f0 + 299] = 1; rec[f0 + 300] = 3;
            var parsed = ItemTables.ParseSparse(77, rec, 0, rec.Length, L, null, "client");
            Check("usable: an item record gives its class, race, skill, reputation and PvP limits from their offsets (164, 172, 274, 276, 278, 282, 297)",
                parsed != null && parsed.AllowableClass == 8 && parsed.AllowableRace == -6184943489809468494L && parsed.RequiredSkill == 202 && parsed.RequiredSkillRank == 245 && parsed.MinFactionId == 529 && parsed.MinReputation == 6 && parsed.RequiredPvpRank == 14 && parsed.RequiredLevel == 30,
                parsed == null ? "null" : parsed.AllowableClass + " " + parsed.AllowableRace + " " + parsed.RequiredSkill + " " + parsed.RequiredSkillRank + " " + parsed.MinFactionId + " " + parsed.MinReputation + " " + parsed.RequiredPvpRank);
            var tmp = Path.Combine(Path.GetTempPath(), "elans-usable-" + Guid.NewGuid().ToString("N") + ".bin");
            try
            {
                var set = new ItemSet { SparseHash = 0x6FCC3191, ItemHash = 0x9A2A4834, BudgetHash = 0x4FD22743, Client = new List<ClientItem> { parsed }, Cache = new List<ClientItem>() };
                var back = ItemIndexFile.TryWrite(tmp, "b", 1, 2, set) ? ItemIndexFile.TryRead(tmp) : null;
                var b0 = back?.Set.Client.FirstOrDefault();
                Check("usable: the saved item index keeps the limits (format " + ItemData.FormatVersion + "; an older file is read from the game again)",
                    ItemData.FormatVersion >= 3 && b0 != null && b0.AllowableClass == 8 && b0.AllowableRace == parsed.AllowableRace && b0.RequiredSkill == 202 && b0.RequiredSkillRank == 245 && b0.MinFactionId == 529 && b0.MinReputation == 6 && b0.RequiredPvpRank == 14);
            }
            finally { try { File.Delete(tmp); } catch (Exception) { } }

            // ---- the character knows its professions
            Check("usable: Aela's professions come from the Hub - Leatherworking 120, Enchanting 150, Cooking 60 by skill line; Engineering is not there",
                aela.SkillsKnown && aela.Skills[165] == 120 && aela.Skills[333] == 150 && aela.Skills[185] == 60 && !aela.Skills.ContainsKey(202) && aela.SkillRank(202, "Engineering", true) == 0 && aela.SkillRank(165, "Leatherworking", true) == 120);

            // ---- each rule on its own
            var mage = new UpgradeChar { Key = "m", Name = "Mira", ClassFile = "MAGE", Race = "Human", Level = 30 };
            var rogueOrc = new UpgradeChar { Key = "r", Name = "Rok", ClassFile = "ROGUE", Race = "Orc", Level = 30 };
            var weird = new UpgradeChar { Key = "w", Name = "Wyn", ClassFile = "MAGE", Race = "Zorp", Level = 30 };
            var rogueOnly = UsableHead(9002, "Rogue Tier Cap", 30, i => i.AllowableClass = 8);
            var mageRogue = UsableHead(9010, "Shared Tier Cap", 30, i => i.AllowableClass = 8 | 128);
            var everyone = UsableHead(9011, "Open Cap", 30, i => i.AllowableClass = 32767);
            var v = UpgradeRules.Restriction(mage, rogueOnly);
            Check("usable: a class-only item is blocked for another class with 'Rogue only' and fine for the Rogue", v.Blocked && v.Reason == "Rogue only" && !UpgradeRules.Restriction(rogueOrc, rogueOnly).Blocked, v.Reason);
            v = UpgradeRules.Restriction(aela, mageRogue);
            Check("usable: two classes read 'Rogue, Mage only' (Hunter blocked); a mask for every class (32767, -1, 0) blocks nobody", v.Blocked && v.Reason == "Rogue, Mage only" && !UpgradeRules.Restriction(aela, everyone).Blocked
                && !UpgradeRules.Restriction(aela, UsableHead(9012, "x", 1, i => i.AllowableClass = -1)).Blocked && UpgradeRules.Restriction(aela, UsableHead(9013, "x", 1)).Reason == "", v.Reason);

            var horde = UsableHead(9003, "Orc Helm", 30, i => i.AllowableRace = HordeBits);
            var alliance = UsableHead(9014, "Stormwind Helm", 30, i => i.AllowableRace = AllianceBits);
            var orcs = UsableHead(9015, "Orc and Troll Helm", 30, i => i.AllowableRace = (1L << 1) | (1L << 7));
            v = UpgradeRules.Restriction(aela, horde);
            Check("usable: a Horde item is blocked for a Night Elf with 'Horde only', fine for the Orc; an Alliance item the other way round", v.Blocked && v.Reason == "Horde only" && !UpgradeRules.Restriction(rogueOrc, horde).Blocked
                && UpgradeRules.Restriction(rogueOrc, alliance).Reason == "Alliance only" && !UpgradeRules.Restriction(aela, alliance).Blocked, v.Reason);
            v = UpgradeRules.Restriction(aela, orcs);
            Check("usable: a mask that is not one whole side names the races ('Orc, Troll only'); the Orc is fine", v.Reason == "Orc, Troll only" && !UpgradeRules.Restriction(rogueOrc, orcs).Blocked, v.Reason);
            v = UpgradeRules.Restriction(weird, orcs);
            Check("usable: a race the Outpost does not know is never guessed - the race limit is a note, the item is not blocked", !v.Blocked && v.Note.Contains("Orc, Troll only") && v.Note.Contains("not known"), v.Note);
            var weirdHorde = new UpgradeChar { Key = "z", Name = "Zed", ClassFile = "MAGE", Race = "Zorp", Faction = "Horde", Level = 30 };
            Check("usable: an unknown race on a known side still meets a one-side item ('Horde only' for the Alliance, none for the Horde)", UpgradeRules.Restriction(new UpgradeChar { Key = "z2", ClassFile = "MAGE", Race = "Zorp", Faction = "Alliance", Level = 30 }, horde).Reason == "Horde only" && !UpgradeRules.Restriction(weirdHorde, horde).Blocked);

            var lens = UsableHead(9004, "Green Lens Helm", 30, i => { i.RequiredSkill = 202; i.RequiredSkillRank = 245; });
            var lw100 = UsableHead(9005, "Leather Cap 100", 30, i => { i.RequiredSkill = 165; i.RequiredSkillRank = 100; });
            var lw150 = UsableHead(9006, "Leather Cap 150", 30, i => { i.RequiredSkill = 165; i.RequiredSkillRank = 150; });
            v = UpgradeRules.Restriction(aela, lens);
            Check("usable: an Engineering item for a character without it is blocked with 'Needs Engineering 245'", v.Blocked && v.Reason == "Needs Engineering 245", v.Reason);
            Check("usable: Leatherworking 100 is fine at skill 120; 150 is blocked with 'Needs Leatherworking 150'", !UpgradeRules.Restriction(aela, lw100).Blocked && UpgradeRules.Restriction(aela, lw150).Reason == "Needs Leatherworking 150");
            v = UpgradeRules.Restriction(mage, lens);
            Check("usable: when the professions are not known the requirement is only a note ('Needs Engineering 245'), the item is not blocked", !v.Blocked && v.Note == "Needs Engineering 245" && v.Reason == "", v.Note);
            var oldHub = new UpgradeChar { Key = "o", ClassFile = "MAGE", Race = "Human", Level = 30, SkillsKnown = true, SkillsUnlined = true };
            oldHub.SkillsByName["Tailoring"] = 50;
            Check("usable: a Hub that saved no skill line ids is matched by profession name, and a name it cannot match stays a note rather than a block",
                UpgradeRules.Restriction(oldHub, UsableHead(9016, "x", 1, i => { i.RequiredSkill = 197; i.RequiredSkillRank = 60; })).Reason == "Needs Tailoring 60" && !UpgradeRules.Restriction(oldHub, lens).Blocked && UpgradeRules.Restriction(oldHub, lens).Note == "Needs Engineering 245");

            var rep = UsableHead(9007, "Dawn Helm", 30, i => { i.MinFactionId = 529; i.MinReputation = 6; });
            v = UpgradeRules.Restriction(aela, rep);
            Check("usable: a reputation limit is a note while no reputations are known ('Needs Revered ...'), never a block", !v.Blocked && v.Note.StartsWith("Needs Revered") && (v.Note.Contains("529") || v.Note.Contains("Argent Dawn")), v.Note);
            var repChar = new UpgradeChar { Key = "p", ClassFile = "MAGE", Race = "Human", Level = 30, ReputationKnown = true };
            repChar.Reputation[529] = 5;
            Check("usable: with reputations known, Honored is too low for Revered (blocked), Revered is enough", UpgradeRules.Restriction(repChar, rep).Blocked && UpgradeRules.Restriction(repChar, rep).Reason.StartsWith("Needs Revered") && (repChar.Reputation[529] = 6) == 6 && !UpgradeRules.Restriction(repChar, rep).Blocked);
            Check("usable: a standing index of 3 or less (Neutral) and a missing faction id add nothing; the standing words run Hated .. Exalted",
                !UpgradeRules.Restriction(aela, UsableHead(9017, "x", 1, i => { i.MinFactionId = 529; i.MinReputation = 3; })).Blocked && UpgradeRules.Restriction(aela, UsableHead(9017, "x", 1, i => { i.MinFactionId = 529; i.MinReputation = 3; })).Note == ""
                && UpgradeRules.Restriction(aela, UsableHead(9018, "x", 1, i => i.MinReputation = 7)).Note == "" && UpgradeRules.Standings[3] == "Neutral" && UpgradeRules.Standings[7] == "Exalted");

            var pvp = UsableHead(9008, "General's Helm", 30, i => i.RequiredPvpRank = 8);
            v = UpgradeRules.Restriction(aela, pvp);
            Check("usable: a PvP rank limit is a note while the rank is not known ('Needs PvP rank 8'), a block only when the character's rank is known and lower", !v.Blocked && v.Note == "Needs PvP rank 8 (Master Sergeant)"
                && UpgradeRules.Restriction(new UpgradeChar { Key = "q", ClassFile = "MAGE", Race = "Human", Level = 30, PvpRank = 5 }, pvp).Reason == "Needs PvP rank 8 (Master Sergeant)"
                && UpgradeRules.Restriction(new UpgradeChar { Key = "q2", ClassFile = "MAGE", Race = "Orc", Level = 30 }, pvp).Note == "Needs PvP rank 8 (Senior Sergeant)" && UpgradeRules.Restriction(new UpgradeChar { Key = "q3", ClassFile = "MAGE", Race = "Zorp", Level = 30 }, pvp).Note == "Needs PvP rank 8 (Master Sergeant / Senior Sergeant)"
                && UpgradeRules.PvpRankText(3, "") == "rank 3" && UpgradeRules.PvpRankText(18, "Horde") == "rank 18 (High Warlord)", v.Note);

            // ---- the engine: blocked candidates are marked, kept apart, sorted below, never the best
            var ev = new UpgradeEngine.Evaluator(aela);
            var usable = UsableHead(9001, "Plain Leather Cap", 10);
            var cands = new[] { usable, rogueOnly, horde, lens, lw150, lw100, rep }.Select(i => ev.Evaluate(i, 1)).ToList();
            Check("usable: Evaluate marks the blocked ones (class, race, skill) with their reason and the note ones without blocking; the usable ones carry neither",
                cands.All(c => c != null) && !cands[0].Blocked && cands[0].BlockReason == "" && cands[1].Blocked && cands[1].BlockReason == "Rogue only" && cands[2].BlockReason == "Horde only" && cands[3].BlockReason == "Needs Engineering 245" && cands[4].Blocked
                && !cands[5].Blocked && !cands[6].Blocked && cands[6].Requirement.StartsWith("Needs Revered"), string.Join(" / ", cands.Select(c => c == null ? "null" : c.Blocked + ":" + c.BlockReason + ":" + c.Requirement)));
            var src = new List<UpgradeSource> { new UpgradeSource { Kind = "vendor", What = "Test Vendor", Lodge = true } };
            var bySlot = new UpgradeSet { Who = aela };
            foreach (var c in cands) c.Sources = src;
            bySlot.BySlot[1] = cands;
            var res = UpgradeEngine.Pick(bySlot, new UpgradeFilters { PerSlot = 5 }).First(r => r.Slot == 1);
            Check("usable: Pick lists the usable ones in Upgrades and the blocked ones apart in Blocked (the biggest gains are the blocked ones, yet they do not lead)",
                res.Upgrades.All(c => !c.Blocked) && res.Upgrades.Count == 3 && res.Upgrades.Select(c => c.Item.Id).OrderBy(x => x).SequenceEqual(new[] { 9001, 9005, 9007 }) && res.Blocked.Count == UpgradeEngine.ShowBlockedPerSlot && res.Blocked.All(c => c.Blocked)
                && cands[1].Gain > cands[0].Gain, Joined(Ids(res)) + " | " + string.Join(",", res.Blocked.Select(c => c.Item.Id)));
            Check("usable: the blocked ones are sorted by the same rank among themselves (the biggest gain first)", res.Blocked.Zip(res.Blocked.Skip(1), (a, b) => a.Rank >= b.Rank).All(x => x));
            var off = UpgradeEngine.Pick(bySlot, new UpgradeFilters { PerSlot = 5, ShowBlocked = false }).First(r => r.Slot == 1);
            Check("usable: with 'Show items I can't use' off the blocked ones are left out and counted; the usable list is the same; the setting is saved and read back",
                off.Blocked.Count == 0 && off.BlockedHidden == 4 && Joined(Ids(off)) == Joined(Ids(res)) && UpgradeFilters.Parse(new UpgradeFilters { ShowBlocked = false }.Serialize()).ShowBlocked == false && UpgradeFilters.Parse(new UpgradeFilters().Serialize()).ShowBlocked && UpgradeFilters.Parse("").ShowBlocked,
                off.BlockedHidden + " " + new UpgradeFilters { ShowBlocked = false }.Serialize());
            Check("usable: a slot with only blocked items has no usable upgrade (nothing counts as the best)", UpgradeEngine.Pick(new UpgradeSet { Who = aela, BySlot = { { 1, new List<UpgradeCandidate> { cands[1], cands[2] } } } }, new UpgradeFilters()).First(r => r.Slot == 1) is UpgradeSlotResult only && only.Upgrades.Count == 0 && only.Soon.Count == 0 && only.Blocked.Count == 2);
            Check("usable: the Atlas item hint never offers a blocked item as an upgrade for the character", UpgradeEngine.HintFor(aela, rogueOnly) == null && UpgradeEngine.HintFor(aela, lens) == null && UpgradeEngine.HintFor(aela, usable) != null);

            // the cache key separates characters that differ only in what they may use
            var clone = new UpgradeChar { Key = aela.Key, Name = aela.Name, ClassFile = aela.ClassFile, Race = aela.Race, Faction = aela.Faction, Level = aela.Level, Spec = aela.Spec };
            foreach (var kv in aela.Worn) clone.Worn[kv.Key] = kv.Value;
            Check("usable: the cache key changes with the professions", clone.Fingerprint != aela.Fingerprint);
        }

        // the real files: the limits are in the shipped table (read-only)
        void UpgradesUsableReal(CheckFn Check, List<string> notes, ItemSnapshot snap)
        {
            var hunterNoProf = new UpgradeChar { Key = "u1", Name = "U", ClassFile = "HUNTER", Race = "NightElf", Level = 60, SkillsKnown = true };
            var hunterUnknown = new UpgradeChar { Key = "u2", Name = "V", ClassFile = "MAGE", Race = "Human", Level = 60 };
            var lens = snap.Get(10504);          // Green Lens: Engineering 245
            var helmet = snap.Get(10588);        // Goblin Rocket Helmet: Engineering 235
            Check("real game files: Green Lens (10504) needs Engineering 245 - a character without it is blocked with 'Needs Engineering 245', one whose professions are unknown only gets the note",
                lens != null && lens.RequiredSkill == 202 && lens.RequiredSkillRank == 245 && UpgradeRules.Restriction(hunterNoProf, lens).Reason == "Needs Engineering 245" && UpgradeRules.Restriction(hunterUnknown, lens).Note == "Needs Engineering 245" && !UpgradeRules.Restriction(hunterUnknown, lens).Blocked,
                lens == null ? "missing" : lens.RequiredSkill + "/" + lens.RequiredSkillRank + " " + UpgradeRules.Restriction(hunterNoProf, lens).Reason);
            Check("real game files: Goblin Rocket Helmet (10588) needs Engineering 235", helmet != null && helmet.RequiredSkill == 202 && helmet.RequiredSkillRank == 235 && UpgradeRules.Restriction(hunterNoProf, helmet).Reason == "Needs Engineering 235");
            var horn = snap.Get(1041);
            Check("real game files: Horn of the Black Wolf (1041) is Horde only - blocked for a Night Elf, not for an Orc", horn != null && UpgradeRules.Restriction(hunterNoProf, horn).Reason == "Horde only" && !UpgradeRules.Restriction(new UpgradeChar { Key = "o", ClassFile = "HUNTER", Race = "Orc", Level = 60 }, horn).Blocked, horn == null ? "missing" : horn.AllowableRace + " " + UpgradeRules.Restriction(hunterNoProf, horn).Reason);
            var caretaker = snap.Get(19533);
            Check("real game files: Caretaker's Cape (19533) has a reputation value but no faction, so it adds no requirement", caretaker != null && caretaker.MinFactionId == 0 && UpgradeRules.Restriction(hunterNoProf, caretaker).Note == "", caretaker == null ? "missing" : caretaker.MinFactionId + "/" + caretaker.MinReputation);
            // a class-only armor piece: blocked for another class, usable for its own
            var tier = snap.Items.Values.Where(i => i.Equippable && i.ClassId == 4 && i.Quality >= 3 && i.RequiredLevel > 0 && i.RequiredLevel <= 60 && (i.AllowableClass == 8 || i.AllowableClass == 128)).OrderBy(i => i.Id).ToList();
            var tr = tier.FirstOrDefault(i => i.AllowableClass == 8);
            var mageC = new UpgradeChar { Key = "t1", ClassFile = "MAGE", Race = "Human", Level = 60 };
            var rogueC = new UpgradeChar { Key = "t2", ClassFile = "ROGUE", Race = "Human", Level = 60 };
            Check("real game files: a Rogue-only armor piece (" + (tr?.Name ?? "none") + ", " + tier.Count + " class-only pieces found) is blocked for a Mage with 'Rogue only' and not for a Rogue", tr != null && UpgradeRules.Restriction(mageC, tr).Reason == "Rogue only" && !UpgradeRules.Restriction(rogueC, tr).Blocked);
            // the PvP rank byte: every piece of rank gear carries the rank its title says (Knight-Lieutenant's .. = 11 ... Grand Marshal's .. = 18, and the Horde names)
            var rankBad = new List<string>(); int ranksSeen = 0;
            for (int i = 0; i < UpgradeRules.AllianceRanks.Length; i++)
                foreach (var title in new[] { UpgradeRules.AllianceRanks[i], UpgradeRules.HordeRanks[i] }.Distinct())
                {
                    if (i < 5) continue;                                                   // below Knight / Stone Guard no gear is made
                    var gear = snap.Items.Values.Where(x => x.Equippable && x.Name.StartsWith(title + "'s ", StringComparison.Ordinal) && x.RequiredPvpRank > 0).ToList();
                    if (gear.Count > 0) ranksSeen++;
                    foreach (var g in gear) if (g.RequiredPvpRank != UpgradeRules.FirstPvpRank + i) rankBad.Add(g.Name + "=" + g.RequiredPvpRank + " (want " + (UpgradeRules.FirstPvpRank + i) + ")");
                }
            Check("real game files: the PvP rank byte (offset 297) matches the title on the rank gear - " + ranksSeen + " titles checked, High Warlord's Shield Wall and Grand Marshal's Handaxe are rank 18",
                rankBad.Count == 0 && ranksSeen >= 10 && snap.Get(18826)?.RequiredPvpRank == 18 && snap.Get(18827)?.RequiredPvpRank == 18, string.Join("; ", rankBad.Take(5)));
            // the PvP rank: the values of the high ranks' gear
            string Rk(int id) { var i = snap.Get(id); return i == null ? "missing" : i.Name + " rank " + i.RequiredPvpRank; }
            notes.Add("note real game files: PvP rank byte @297: " + Rk(18826) + "; " + Rk(18827));
            int withPvp = snap.Items.Values.Count(i => i.RequiredPvpRank > 0), withSkill = snap.Items.Values.Count(i => i.RequiredSkill > 0), withClass = snap.Items.Values.Count(i => i.AllowableClass > 0 && i.AllowableClass != 32767), withRace = snap.Items.Values.Count(i => i.AllowableRace != 0 && i.AllowableRace != -1), withRep = snap.Items.Values.Count(i => i.MinFactionId > 0 && i.MinReputation > 0);
            notes.Add("note real game files: limits found - class " + withClass + ", race " + withRace + ", skill " + withSkill + ", reputation " + withRep + ", PvP rank " + withPvp + " of " + snap.Count + " items");
        }
    }
}
