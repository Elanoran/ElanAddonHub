using System.Collections.Generic;

namespace ElansAddonHub.Services
{
    // 3.11: the welcome guide's three sharing choices. Each one only sets switches that also live in Settings > Privacy, so a choice
    // is never more than a shortcut. Equal weight in the guide: "Recommended" is a chip, not a pre-selection, and none is picked for you.
    public static class SharingPresets
    {
        public const string Lodge = "lodge", Basics = "basics", Private = "private";
        public static readonly string[] All = { Lodge, Basics, Private };

        public static string Title(string id) => id == Lodge ? "Share with my Lodge" : id == Basics ? "Just the basics" : "Keep everything on my PC";

        public static string Summary(string id) => id == Lodge ? "Friends see what you play, and everyone's Atlas and maps get better from what you see."
            : id == Basics ? "Friends see that you're online, which character you play and in which zone. Nothing else."
            : "Nothing from your game is sent. Friends still see you online while the Outpost is connected.";

        // what the choice turns on, one short line each (mainName: the character the armory would share, null = none found on this PC)
        public static List<string> Lines(string id, string mainName)
        {
            if (id == Lodge) return new List<string>
            {
                "Your status: character, level, zone and XP",
                "What you see in the world: Atlas finds and flight times (ids and counts, no names)",
                mainName != null ? "Gear and talents of " + mainName + ", your highest character" : "Your characters: none found yet - choose them later in Settings > Privacy",
                "Your position on the world map, to officers only",
            };
            if (id == Basics) return new List<string> { "Your status: character, level and zone", "Off: XP, Atlas, flight times, characters, map position" };
            return new List<string> { "Off: status details, Atlas, flight times, characters, map position", "Appear offline (Settings > Privacy) hides that you are online too" };
        }

        public static void Apply(Settings s, string id, string mainKey)
        {
            bool lodge = id == Lodge, basics = id == Basics;
            s.ShareGameOff = !(lodge || basics);
            s.ShareZoneOff = !(lodge || basics);
            s.ShareXpOff = !lodge;
            s.AutoStatusOff = !(lodge || basics);
            s.AutoAwayOff = !(lodge || basics);
            s.AtlasShareOff = !lodge;
            s.FlightShareOff = !lodge;
            s.ShareLivePosWithLodge = lodge;
            if (lodge) s.LivePosScope = "officers";
            if (lodge)
            {
                // characters already chosen stay chosen; otherwise the highest one of this PC
                if ((s.ArmoryChars == null || s.ArmoryChars.Count == 0) && mainKey != null) s.ArmoryChars = new List<string> { mainKey };
                s.ArmoryShareOn = s.ArmoryChars != null && s.ArmoryChars.Count > 0;
            }
            else s.ArmoryShareOn = false;
        }

        // the choice the settings match now, or null (a mix of switches, or a fresh install's defaults): the guide then shows none picked
        public static string Current(Settings s)
        {
            bool status = !s.ShareGameOff && !s.ShareZoneOff, noneElse = s.AtlasShareOff && s.FlightShareOff && !s.ArmoryShareOn && !s.ShareLivePosWithLodge;
            if (status && !s.ShareXpOff && !s.AtlasShareOff && !s.FlightShareOff && s.ShareLivePosWithLodge && s.LivePosScope == "officers") return Lodge;
            if (status && s.ShareXpOff && noneElse) return Basics;
            if (s.ShareGameOff && s.ShareZoneOff && s.ShareXpOff && noneElse) return Private;
            return null;
        }
    }
}
