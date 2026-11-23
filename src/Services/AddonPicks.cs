using System;
using System.Collections.Generic;
using System.Linq;

namespace ElansAddonHub.Services
{
    // 3.11: what the welcome guide ticks on its addon step, and the one-line reason it shows. Required addons stay required (their own chip);
    // addons every player gets something from are ticked; a class helper is ticked only for a class found on this PC (Elan's Hub's
    // characters, or the class the Outpost saw in game). Everything can be unticked - this only decides the starting ticks.
    public static class AddonPicks
    {
        public static readonly string[] ForEveryone = { "ElansBags", "ElansProfessionHelper" };

        // addons whose manifest entry has no "classes" yet (the Hunter Helper is older than the field)
        static readonly Dictionary<string, string> KnownClass = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["ElansHunterHelper"] = "HUNTER" };

        public static List<string> ClassesOf(string id, List<string> classes)
        {
            if (classes != null && classes.Count > 0) return classes;
            return id != null && KnownClass.TryGetValue(id, out var c) ? new List<string> { c } : new List<string>();
        }

        public static string Nice(string classFile) => string.IsNullOrEmpty(classFile) ? "" : char.ToUpperInvariant(classFile[0]) + classFile.Substring(1).ToLowerInvariant();

        // (tick at the start, reason line or null)
        public static (bool tick, string reason) Recommend(string id, bool required, List<string> classes, ISet<string> played)
        {
            if (required) return (true, null);
            var cls = ClassesOf(id, classes);
            if (cls.Count > 0)
            {
                var match = cls.FirstOrDefault(c => played != null && played.Contains(c));
                return match != null ? (true, "Recommended: you play a " + Nice(match)) : (false, "For " + string.Join(" and ", cls.Select(c => Nice(c).ToLowerInvariant() + "s")));
            }
            if (ForEveryone.Contains(id, StringComparer.OrdinalIgnoreCase)) return (true, "Recommended for everyone");
            return (false, null);
        }

        // the class files of the characters this PC knows (upper case, letters only: "HUNTER")
        public static HashSet<string> Played(IEnumerable<string> classFiles)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in classFiles ?? Enumerable.Empty<string>())
            {
                var c = new string((f ?? "").Where(char.IsLetter).ToArray()).ToUpperInvariant();
                if (c.Length > 0) set.Add(c);
            }
            return set;
        }
    }
}
