using System;
using System.Collections.Generic;

namespace ElansAddonHub.Services
{
    // Which game icon stands for a character: the race + sex portrait (Interface\Icons\Achievement_Character_<Race>_<Male|Female>)
    // when both are known, else the class icon (Interface\Icons\ClassIcon_<Class>), else nothing (the page draws a class coloured initial).
    // The FileDataIDs are the ones of the Classic client (found by listing the local game data, see IconStore.Probe); the pixels are
    // read from the player's own install by IconStore, nothing is shipped or downloaded.
    public static class CharPortrait
    {
        // UnitRace "raceFile" -> first id of its pair (female, then male)
        static readonly Dictionary<string, int> RaceFemale = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["BloodElf"] = 236439, ["Draenei"] = 236441, ["Dwarf"] = 236443, ["Gnome"] = 236445, ["Human"] = 236447,
            ["NightElf"] = 236449, ["Orc"] = 236451, ["Tauren"] = 236453, ["Troll"] = 236455, ["Scourge"] = 236457, ["Undead"] = 236457,
        };

        static readonly Dictionary<string, int> ClassIcons = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["DEATHKNIGHT"] = 625998, ["DRUID"] = 625999, ["HUNTER"] = 626000, ["MAGE"] = 626001, ["PALADIN"] = 626003,
            ["PRIEST"] = 626004, ["ROGUE"] = 626005, ["SHAMAN"] = 626006, ["WARLOCK"] = 626007, ["WARRIOR"] = 626008,
        };

        // sex as UnitSex returns it: 2 male, 3 female
        public static int RaceIcon(string raceFile, int sex)
        {
            if (string.IsNullOrEmpty(raceFile) || sex < 2 || sex > 3) return 0;
            if (RaceFemale.TryGetValue(raceFile, out var f)) return sex == 3 ? f : f + 1;
            return RaceIcons.For(raceFile, sex);       // a race with no achievement portrait (Skyborne): its character-creation portrait from the UI atlas
        }

        public static int ClassIcon(string classFile) =>
            !string.IsNullOrEmpty(classFile) && ClassIcons.TryGetValue(classFile, out var id) ? id : 0;

        // race portrait, else class icon, else 0
        public static int IconFor(string raceFile, int sex, string classFile)
        {
            var r = RaceIcon(raceFile, sex);
            return r > 0 ? r : ClassIcon(classFile);
        }
    }
}
