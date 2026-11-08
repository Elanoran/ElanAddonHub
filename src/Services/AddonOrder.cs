using System;
using System.Collections.Generic;
using System.Linq;

namespace ElansAddonHub.Services
{
    // 3.8.2: the order our own addons are shown in (the Addons page, the welcome guide's picker, the news in Settings > About) and what each
    // one needs before it can load.
    //
    // The order is a rule, not a list: 1 required addons (installed for everyone: Elan's Hub), 2 addons another addon in the list needs
    // (Elan's Toolkit: the Hunter Helper won't load without it), 3 class addons (the class helpers, by name), 4 the rest (Elan's Bags, Elan's
    // Profession Helper, then any new addon by name). Inside groups 1, 2 and 4 the known ids are the tie-breaker, so the order is the same whatever
    // order the manifest lists them in. Today's seven: Elan's Hub, Elan's Toolkit, Hunter, Paladin and Priest Helper, Elan's Bags, Elan's Profession Helper.
    //
    // Needs: the manifest's "needs" (addon ids). Until the manifests carry it, a small built-in map stands in - ONLY for an entry that has
    // no "needs" field at all; "needs": [] in a manifest means "needs nothing" and wins over the map. Only hard dependencies belong here
    // (the TOC's "## Dependencies:"), never the OptionalDeps.
    public static class AddonOrder
    {
        public static readonly string[] Known = { "ElansHub", "ElansToolkit", "ElansHunterHelper", "ElansPaladinHelper", "ElansPriestHelper", "ElansBags", "ElansProfessionHelper" };

        // 3.9.4: their names, for a card of one that is installed but missing from the release information in use (an older one, e.g. stable
        // after the test release couldn't be read): it stays among our own cards instead of dropping into "Other addons"
        public static readonly Dictionary<string, string> KnownNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ElansHub"] = "Elan's Hub", ["ElansToolkit"] = "Elan's Toolkit", ["ElansHunterHelper"] = "Elan's Hunter Helper",
            ["ElansPaladinHelper"] = "Elan's Paladin Helper", ["ElansPriestHelper"] = "Elan's Priest Helper", ["ElansBags"] = "Elan's Bags",
            ["ElansProfessionHelper"] = "Elan's Profession Helper",
        };

        // the manifest's addons plus, for each known addon it doesn't list but that is installed, a stand-in at the installed version (nothing to
        // download: it shows Up to date until a release information that lists it comes back). installedVersion: id -> version or null.
        public static List<AddonInfo> WithInstalledOwn(IList<AddonInfo> listed, Func<AddonInfo, string> installedVersion)
        {
            var o = (listed ?? new List<AddonInfo>()).Where(a => a != null).ToList();
            var proto = o.FirstOrDefault(a => !string.IsNullOrEmpty(a.Flavor));
            if (proto == null || installedVersion == null) return o;
            foreach (var id in Known)
            {
                if (o.Any(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase))) continue;
                var stub = new AddonInfo { Id = id, Name = KnownNames.TryGetValue(id, out var n) ? n : id, Flavor = proto.Flavor, FlavorName = proto.FlavorName, Folders = new List<string> { id } };
                var v = installedVersion(stub);
                if (string.IsNullOrEmpty(v)) continue;
                stub.Version = v;
                o.Add(stub);
            }
            return o;
        }

        // from the addons' TOC files: ElansHunterHelper has "## Dependencies: ElansToolkit"; the others only have OptionalDeps
        public static readonly Dictionary<string, string[]> FallbackNeeds = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["ElansHunterHelper"] = new[] { "ElansToolkit" },
        };

        // the manifest names no class for the Hunter Helper (it never gets "Recommended for your ..."); for the order it is a class addon
        static readonly Dictionary<string, string[]> FallbackClasses = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["ElansHunterHelper"] = new[] { "HUNTER" },
        };

        // the ids this addon needs (never itself, no blanks, no doubles)
        public static List<string> Needs(AddonInfo a)
        {
            if (a == null) return new List<string>();
            IEnumerable<string> src = a.Needs ?? (a.Id != null && FallbackNeeds.TryGetValue(a.Id, out var fb) ? fb : Enumerable.Empty<string>());
            return src.Where(s => !string.IsNullOrWhiteSpace(s) && !string.Equals(s, a.Id, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static bool NeedsId(AddonInfo a, string id) => id != null && Needs(a).Contains(id, StringComparer.OrdinalIgnoreCase);

        public static bool IsClassAddon(AddonInfo a) =>
            a != null && (a.Classes != null ? a.Classes.Count > 0 : a.Id != null && FallbackClasses.ContainsKey(a.Id));

        // 0 required, 1 needed by another addon of the list, 2 class addon, 3 the rest
        public static int Group(AddonInfo a, IEnumerable<AddonInfo> all)
        {
            if (a.Required) return 0;
            if (all.Any(o => o != null && o != a && NeedsId(o, a.Id))) return 1;
            if (IsClassAddon(a)) return 2;
            return 3;
        }

        static int KnownIndex(string id)
        {
            var i = Array.FindIndex(Known, k => string.Equals(k, id, StringComparison.OrdinalIgnoreCase));
            return i < 0 ? int.MaxValue : i;
        }

        public static List<AddonInfo> Sort(IEnumerable<AddonInfo> list)
        {
            var all = (list ?? Enumerable.Empty<AddonInfo>()).Where(a => a != null).ToList();
            return all.OrderBy(a => Group(a, all))
                // the class helpers by name; elsewhere the known ids first (their own order), then the new ones by name
                .ThenBy(a => Group(a, all) == 2 ? 0 : KnownIndex(a.Id))
                .ThenBy(a => a.Name ?? a.Id ?? "", StringComparer.OrdinalIgnoreCase)
                .ThenBy(a => a.Id ?? "", StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // a bulk install ("Install" in the welcome guide): what each picked addon needs (when the list has it) comes first, each id once
        public static List<string> WithNeedsFirst(IEnumerable<string> ids, IList<AddonInfo> all)
        {
            var o = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(string id, int depth)
            {
                if (id == null || depth > 8 || seen.Contains(id)) return;
                var a = all?.FirstOrDefault(x => string.Equals(x?.Id, id, StringComparison.OrdinalIgnoreCase));
                seen.Add(id);
                if (a != null) foreach (var n in Needs(a)) if (all.Any(x => string.Equals(x?.Id, n, StringComparison.OrdinalIgnoreCase))) Add(n, depth + 1);
                o.Add(a?.Id ?? id);
            }
            foreach (var id in ids ?? Enumerable.Empty<string>()) Add(id, 0);
            return o;
        }

        // the needs of 'a' that are NOT installed in the same WoW client (its flavor). find: the manifest entry of an id (its folders), or null
        // for an addon the Outpost doesn't know - then the folder named like the id is looked for.
        public static List<string> Missing(AddonInfo a, string wowRoot, Func<string, AddonInfo> find)
        {
            var o = new List<string>();
            if (a == null || wowRoot == null) return o;
            foreach (var id in Needs(a))
            {
                var dep = find?.Invoke(id);
                var probe = new AddonInfo { Id = id, Name = dep?.Name ?? id, Flavor = a.Flavor, Folders = dep?.Folders?.Count > 0 ? dep.Folders : new List<string> { id } };
                if (Installer.InstalledVersion(wowRoot, probe) == null) o.Add(id);
            }
            return o;
        }

        // "Elan's Toolkit" / "Elan's Toolkit and Elan's Hub" / "A, B and C"
        public static string Names(IEnumerable<string> names)
        {
            var l = names.ToList();
            return l.Count <= 1 ? l.FirstOrDefault() ?? "" : string.Join(", ", l.Take(l.Count - 1)) + " and " + l[l.Count - 1];
        }
    }
}
