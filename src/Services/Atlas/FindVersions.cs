using System;
using System.Globalization;
using ElansAddonHub.Services.Db2;

namespace ElansAddonHub.Services.Atlas
{
    // Which game version a find was made in (2.77). Elan's Toolkit stamps every find with the game build (GetBuildInfo, the "b" of a find in atlas.obs);
    // the Outpost keeps it (AtlasObs.Find.Build), shares it with the Lodge as "gb" and shows it quietly on the find lists: "game 1.60.1" for the version
    // the player runs now, "build 70338 (older game version)" for one before it, "unknown version" for a find recorded before builds were stamped.
    // The Toolkit records only the build number, so an older build's version name is not known unless it is the running one.
    public static class FindVersions
    {
        public const string Unknown = "unknown version";

        // the running game: "wow_classic_beta|1.60.1.70374|key" (SpellData's stamp); the self-test swaps it
        public static Func<string> CurrentStamp = () => SpellData.Build ?? "";

        public static int CurrentBuild() { try { return ServerHotfixes.BuildNumber(CurrentStamp()); } catch (Exception) { return 0; } }

        // "1.60.1" of "…|1.60.1.70374|…" (null when the stamp is not a version)
        public static string CurrentVersion()
        {
            string s; try { s = CurrentStamp() ?? ""; } catch (Exception) { return null; }
            var p = s.Split('|');
            var ver = p.Length >= 2 ? p[1] : "";
            int dot = ver.LastIndexOf('.');
            if (dot <= 0) return null;
            var head = ver.Substring(0, dot);
            foreach (var part in head.Split('.')) if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _)) return null;
            return head;
        }

        public static bool IsOlder(int build) { int cur = CurrentBuild(); return build > 0 && cur > 0 && build < cur; }

        // the short words for a find's line: "game 1.60.1" | "build 70338 (older game version)" | "build 70400" | "unknown version"
        public static string Text(int build)
        {
            if (build <= 0) return Unknown;
            int cur = CurrentBuild();
            if (build == cur && CurrentVersion() is string v) return "game " + v;
            var b = "build " + build.ToString(CultureInfo.InvariantCulture);
            return build < cur && cur > 0 ? b + " (older game version)" : b;
        }

        // the tooltip of that text
        public static string Tip(int build)
        {
            if (build <= 0) return "Found before Elan's Toolkit stamped finds with the game build, so the game version is not known.";
            int cur = CurrentBuild();
            var b = build.ToString(CultureInfo.InvariantCulture);
            if (build == cur) return "Found in the game version you run now (build " + b + ").";
            if (cur > 0 && build < cur) return "Found in game build " + b + ", before your game's build " + cur.ToString(CultureInfo.InvariantCulture) + ": drops may have changed since.";
            return "Found in game build " + b + ".";
        }
    }
}
