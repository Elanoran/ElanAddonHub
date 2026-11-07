using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ElansAddonHub.Services;
using Elans.Shared;

namespace ElansAddonHub.Lodge
{
    // Who runs an old Outpost or old addons (Lodge server 2.38, feature "versions"). Every Outpost already tells the Lodge its version
    // (user.app, from /ws?client=hub/x.y.z); a newer Outpost (from the release with this change) also sends its update channel and the Elan's addons installed in WoW Forever
    // ("versions" -> user.versions). Each member is compared with the newest release of THEIR channel: someone on Stable is never "behind"
    // for not having a test build. An Outpost that says no channel (older ones) is compared with Stable. An addon a member doesn't
    // have installed is not behind - it's just not there.
    public static class MemberVersions
    {
        // the newest versions of one channel: the Outpost and each addon by its wire id, with a name to show
        public sealed class Newest
        {
            public string App;
            public readonly Dictionary<string, string> Addons = new Dictionary<string, string>(StringComparer.Ordinal);
            public readonly Dictionary<string, string> Names = new Dictionary<string, string>(StringComparer.Ordinal);

            public static Newest From(Manifest m)
            {
                if (m == null) return null;
                var n = new Newest { App = m.Hub?.Version };
                foreach (var a in m.Addons ?? new List<AddonInfo>())
                {
                    var id = WireId(a.Id);
                    if (id == null || string.IsNullOrEmpty(a.Version)) continue;
                    n.Addons[id] = a.Version;
                    n.Names[id] = string.IsNullOrWhiteSpace(a.Name) ? a.Id : a.Name;
                }
                return n;
            }
        }

        // one component a member is behind on: "Toolkit 0.22.0 -> 0.22.1"
        public sealed class Behind
        {
            public string Name, Have, Newest;
            public override string ToString() => Name + " " + Have + " → " + Newest;
        }

        // the manifest id (an addon folder like "ElansToolkit") -> the short id on the wire ("toolkit"): lower case, letters and digits,
        // without the "elans" prefix, at most 16 characters (the server's rule). Null when nothing is left.
        public static string WireId(string manifestId)
        {
            if (string.IsNullOrEmpty(manifestId)) return null;
            var s = new string(manifestId.ToLowerInvariant().Where(c => c is >= 'a' and <= 'z' or >= '0' and <= '9').ToArray());
            if (s.StartsWith("elans", StringComparison.Ordinal) && s.Length > 5) s = s.Substring(5);
            if (s.Length > 16) s = s.Substring(0, 16);
            return s.Length == 0 ? null : s;
        }

        // what this Outpost says about itself: its channel and the manifest's addons that are installed (a readable version only)
        public static LodgeVersions Mine(Settings s, Manifest m, Func<AddonInfo, string> installed)
        {
            var v = new LodgeVersions { Channel = UpdateChannel.IsTest(s) ? "test" : "stable" };
            foreach (var a in m?.Addons ?? new List<AddonInfo>())
            {
                var id = WireId(a.Id);
                if (id == null || v.Addons.ContainsKey(id) || v.Addons.Count >= LodgeVersions.MaxAddons) continue;
                string have;
                try { have = installed(a); } catch { have = null; }
                if (!IsPlainVersion(have)) continue;
                v.Addons[id] = have;
            }
            return v;
        }

        public static bool IsPlainVersion(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length > 23) return false;
            var parts = s.Split('.');
            return parts.Length <= 4 && parts.All(p => p.Length is > 0 and <= 5 && p.All(char.IsAsciiDigit));
        }

        // Pure: what a member is behind on. app = user.app (null = unknown), v = user.versions (null = an older Outpost),
        // stable / test = the newest of each channel (test null = not known yet: then Stable is used for a test member too)
        public static List<Behind> Compare(string app, LodgeVersions v, Newest stable, Newest test)
        {
            var list = new List<Behind>();
            var want = v?.Channel == "test" ? test ?? stable : stable;
            if (want == null) return list;
            if (!string.IsNullOrEmpty(app) && !string.IsNullOrEmpty(want.App) && Util.CompareVersions(app, want.App) < 0)
                list.Add(new Behind { Name = "Outpost", Have = app, Newest = want.App });
            if (v != null)
                foreach (var kv in v.Addons)
                    if (want.Addons.TryGetValue(kv.Key, out var newest) && Util.CompareVersions(kv.Value, newest) < 0)
                        list.Add(new Behind { Name = want.Names.TryGetValue(kv.Key, out var n) ? n : kv.Key, Have = kv.Value, Newest = newest });
            return list;
        }

        // ---------------------------------------------------------------- the newest release of each channel (shared by the whole app)

        public static Newest Stable { get; private set; }
        public static Newest Test { get; private set; }
        public static event Action Changed;
        static DateTime stableAt, testAt;
        static bool fetchingStable, fetchingTest;
        static readonly TimeSpan Fresh = TimeSpan.FromMinutes(30);

        // the update check feeds the manifest it accepted (it is already signed and checked). pre = it came from a pre-release.
        // A test Outpost whose newest release is a stable one learns both channels from it.
        public static void Feed(Manifest m, bool test, bool pre)
        {
            var n = Newest.From(m);
            if (n == null) return;
            if (!pre) { Stable = n; stableAt = DateTime.UtcNow; }
            if (test) { Test = n; testAt = DateTime.UtcNow; }
            Changed?.Invoke();
        }

        // the self-test sets both channels by hand
        internal static void SetForTest(Newest stable, Newest test)
        {
            Stable = stable; Test = test;
            stableAt = stable != null ? DateTime.UtcNow : DateTime.MinValue; testAt = test != null ? DateTime.UtcNow : DateTime.MinValue;
            Changed?.Invoke();
        }

        // replaceable in the self-test: reads one channel's newest manifest (signature checked: an invalid one is not used)
        public static Func<bool, Task<Manifest>> FetchChannel = DefaultFetch;

        static async Task<Manifest> DefaultFetch(bool test)
        {
            var url = SettingsStore.DefaultManifestUrl;
            if (test)
            {
                var ch = await UpdateChannel.Resolve(new Settings { Channel = "test", ManifestUrl = SettingsStore.DefaultManifestUrl });
                url = ch.Url;
            }
            var bytes = await Net.GetBytes(url);
            var sig = await Net.GetTextOrNull(ReleaseSignature.SigUrl(url));
            if (ReleaseSignature.Check(bytes, sig, out var why) == ReleaseSignature.Result.Invalid) { Util.Log("member versions: " + why); return null; }
            int bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            return Util.FromJson<Manifest>(System.Text.Encoding.UTF8.GetString(bytes, bom, bytes.Length - bom));
        }

        // a member on a channel this Outpost doesn't follow showed up: fetch that channel's newest (at most every 30 minutes)
        public static async void Ensure(bool test)
        {
            if (test ? fetchingTest || DateTime.UtcNow - testAt < Fresh : fetchingStable || DateTime.UtcNow - stableAt < Fresh) return;
            if (test) fetchingTest = true; else fetchingStable = true;
            try
            {
                var n = Newest.From(await FetchChannel(test));
                if (n != null)
                {
                    if (test) { Test = n; testAt = DateTime.UtcNow; } else { Stable = n; stableAt = DateTime.UtcNow; }
                    Changed?.Invoke();
                }
                else if (test) testAt = DateTime.UtcNow; else stableAt = DateTime.UtcNow;   // nothing usable: don't ask again right away
            }
            catch (Exception e)
            {
                Util.Log("member versions: couldn't read the " + (test ? "test" : "stable") + " release: " + e.Message);
                if (test) testAt = DateTime.UtcNow; else stableAt = DateTime.UtcNow;
            }
            finally { if (test) fetchingTest = false; else fetchingStable = false; }
        }
    }
}
