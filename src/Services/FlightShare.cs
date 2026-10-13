using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ElansAddonHub.Services
{
    // Shared flight-path times (Lodge server 2.13). Elan's Hunter Helper learns how long each flight leg takes and saves it in its own SavedVariables:
    //   ElansHunterHelperDB.flightLegs[ "<Horde|Alliance>|<idA>><idB>" ] = { t = seconds, n = samples, at = unix }
    //   ElansHunterHelperDB.flightNodeNames[ id ] = name
    // This file (1) reads and validates those from every account under the WoW folder (read-only), (2) builds the upload batches, (3) validates what
    // the Lodge sends back and (4) writes it as a tiny Lua DATA file, Interface\AddOns\ElansHub\SharedData.lua, that the addon reads as ElansHubShared.
    // Nothing from any file or from the server ever reaches Lua as code: keys must match a whitelist regex, numbers are re-formatted from parsed
    // values, names are cleaned and escaped. The only file ever written is that one, and only when the toc of the ElansHub folder lists it.
    public static class FlightShare
    {
        public const int MaxLegsPerShare = 500;        // the server's per-message limit
        public const int MaxNamesPerShare = 300;
        public const int MaxShareBytes = 56 * 1024;    // the server drops frames over 64 KB
        public const int MaxLegs = 5000;               // what the server keeps; also what is written
        public const int MaxNames = 3000;
        public const int MaxNameLength = 60;
        public const double MinSeconds = 1, MaxSeconds = 1800;
        public const int MaxSamples = 50;
        public const long MaxFileBytes = 32L * 1024 * 1024;
        public const string HunterHelperFile = "ElansHunterHelper.lua";
        public const string SharedFileName = "SharedData.lua";
        public static readonly TimeSpan ShareEvery = TimeSpan.FromMinutes(10);
        public static readonly Version ServerVersion = new Version(2, 13, 0);

        public struct Leg
        {
            public double T; public int N; public long At;
            public string Sig => T.ToString("0.0", CultureInfo.InvariantCulture) + "|" + N.ToString(CultureInfo.InvariantCulture);
        }

        public class Local
        {
            public Dictionary<string, Leg> Legs = new Dictionary<string, Leg>(StringComparer.Ordinal);
            public Dictionary<string, string> Names = new Dictionary<string, string>(StringComparer.Ordinal);
            public int Files, Dropped;     // files read, entries that failed validation
        }

        // tests replace the reading (null = the WoW folder's SavedVariables)
        public static Func<Local> TestSource;

        // ---------------------------------------------------------------- validation (the same rules as the server's Flights class)

        // an id is a taxi node id ("11") or a node name: letters, digits, space and ' . , - only; 1-32 characters; no leading/trailing space
        const string Id = @"[\p{L}\p{N}][\p{L}\p{N} '.,\-]{0,30}[\p{L}\p{N}.]|[\p{L}\p{N}]";
        static readonly Regex KeyRx = new Regex(@"^(Horde|Alliance)\|(" + Id + @")>(" + Id + @")\z", RegexOptions.CultureInvariant);
        static readonly Regex IdRx = new Regex("^(" + Id + @")\z", RegexOptions.CultureInvariant);

        public static bool ValidKey(string key)
        {
            if (key == null || key.Length > 80) return false;
            var m = KeyRx.Match(key);
            return m.Success && m.Groups[2].Value != m.Groups[3].Value;
        }

        public static bool ValidId(string id) => id != null && id.Length <= 32 && IdRx.IsMatch(id);

        public static bool ValidLeg(double t, double n) =>
            !double.IsNaN(t) && !double.IsInfinity(t) && !double.IsNaN(n) && !double.IsInfinity(n)
            && t >= MinSeconds && t <= MaxSeconds && n >= 1 && n <= MaxSamples && n == Math.Floor(n);

        static double RoundT(double t) => Math.Round(t, 1, MidpointRounding.AwayFromZero);

        // a node name for display: control / format / line-separator characters out, no quote, backslash, angle bracket, pipe or square bracket,
        // whitespace collapsed, at most 60 characters (never half a surrogate pair)
        public static string CleanName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(Math.Min(s.Length, MaxNameLength * 2));
            bool space = false;
            for (int i = 0; i < s.Length && sb.Length < MaxNameLength * 2; i++)
            {
                char c = s[i];
                var cat = char.GetUnicodeCategory(c);
                if (char.IsWhiteSpace(c) || cat == UnicodeCategory.SpaceSeparator) { space = sb.Length > 0; continue; }
                if (cat == UnicodeCategory.Control || cat == UnicodeCategory.Format || cat == UnicodeCategory.LineSeparator || cat == UnicodeCategory.ParagraphSeparator
                    || cat == UnicodeCategory.PrivateUse || cat == UnicodeCategory.OtherNotAssigned) continue;
                if (cat == UnicodeCategory.Surrogate)
                {
                    if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { if (space) { sb.Append(' '); space = false; } sb.Append(c).Append(s[++i]); }
                    continue;
                }
                if (c == '"' || c == '\\' || c == '<' || c == '>' || c == '|' || c == '[' || c == ']') continue;
                if (space) { sb.Append(' '); space = false; }
                sb.Append(c);
            }
            var r = sb.ToString().Trim();
            if (r.Length > MaxNameLength)
            {
                int cut = MaxNameLength;
                if (char.IsHighSurrogate(r[cut - 1])) cut--;
                r = r.Substring(0, cut).TrimEnd();
            }
            return r;
        }

        // the ids a key names ("Horde|11>22" -> 11, 22); the key must be valid
        public static IEnumerable<string> IdsOf(string key)
        {
            var m = KeyRx.Match(key ?? "");
            if (!m.Success) yield break;
            yield return m.Groups[2].Value;
            yield return m.Groups[3].Value;
        }

        // ---------------------------------------------------------------- reading Hunter Helper's saved variables

        static IEnumerable<Dictionary<string, object>> NamedChildren(Dictionary<string, object> t) =>
            t == null ? Enumerable.Empty<Dictionary<string, object>>() : t.Values.OfType<Dictionary<string, object>>();

        // validated legs and names of one SavedVariables text; anything that fails validation is counted and left out. Never throws.
        public static Local Parse(string luaText)
        {
            Dictionary<string, object> globals;
            try { globals = LuaData.ReadGlobals(luaText ?? ""); }
            catch (Exception e) { Util.Log("flights: unreadable saved variables (" + e.GetType().Name + ")"); return new Local(); }
            return FromGlobals(globals);
        }

        // the same from an already parsed file (SavedVarsCache); the tables are shared, read only
        public static Local FromGlobals(Dictionary<string, object> globals)
        {
            var db = globals != null && globals.TryGetValue("ElansHunterHelperDB", out var v) ? v as Dictionary<string, object> : null;
            return FromTables(db?.Child("flightLegs"), db?.Child("flightNodeNames"));
        }

        // the validation shared by Hunter Helper's saved variables and the Hub's `flight` module (same table shapes): legs { [key] = { t, n, at } }, names { [id] = name }
        public static Local FromTables(Dictionary<string, object> legs, Dictionary<string, object> names)
        {
            var r = new Local();
            if (legs != null)
                foreach (var kv in legs)
                {
                    if (r.Legs.Count >= MaxLegs) { r.Dropped++; continue; }
                    var d = kv.Value as Dictionary<string, object>;
                    if (!ValidKey(kv.Key) || d == null || !(d.TryGetValue("t", out var tv) && tv is double t) || !(d.TryGetValue("n", out var nv) && nv is double n) || !ValidLeg(t, n)) { r.Dropped++; continue; }
                    long at = d.TryGetValue("at", out var av) && av is double a && a > 0 && a < 4e12 ? (long)a : 0;
                    r.Legs[kv.Key] = new Leg { T = RoundT(t), N = (int)n, At = at };
                }
            if (names != null)
                foreach (var kv in names)
                {
                    if (r.Names.Count >= MaxNames) break;
                    var nm = kv.Value is string s ? CleanName(s) : "";
                    if (!ValidId(kv.Key) || nm.Length == 0) continue;
                    r.Names[kv.Key] = nm;
                }
            return r;
        }

        // every account's ElansHunterHelper.lua under the WoW folder (read only): per key the newest value wins (then the larger sample count)
        public static Local Read(string root)
        {
            if (TestSource != null) return TestSource() ?? new Local();
            var all = new Local();
            foreach (var f in SavedVarsFiles.For(root, HunterHelperFile))
            {
                try
                {
                    if (new FileInfo(f).Length > MaxFileBytes) continue;   // bigger than any real file: not read
                    var globals = SavedVarsCache.Get(f);                   // parsed once per change, shared with the health reader
                    if (globals == null) continue;
                    var one = FromGlobals(globals);
                    all.Files++; all.Dropped += one.Dropped;
                    foreach (var kv in one.Legs)
                        if (!all.Legs.TryGetValue(kv.Key, out var have) || kv.Value.At > have.At || (kv.Value.At == have.At && kv.Value.N > have.N)) all.Legs[kv.Key] = kv.Value;
                    foreach (var kv in one.Names) all.Names[kv.Key] = kv.Value;
                }
                catch (Exception e) { Util.Log("flights: read failed (" + e.GetType().Name + ")"); }
            }
            // Elan's Hub `flight` module (account): per leg the entry with more samples wins, a tie goes to the newer one (then the Hub's);
            // names are the union and the Hub wins. A missing or too-new module leaves the Hunter Helper data as it is.
            try
            {
                var hub = HubData.FlightOf(HubData.Read(root));
                if (hub != null)
                {
                    all.Files++; all.Dropped += hub.Dropped;
                    foreach (var kv in hub.Legs)
                        if (!all.Legs.TryGetValue(kv.Key, out var have) || kv.Value.N > have.N || (kv.Value.N == have.N && kv.Value.At >= have.At)) all.Legs[kv.Key] = kv.Value;
                    foreach (var kv in hub.Names) all.Names[kv.Key] = kv.Value;
                }
            }
            catch (Exception e) { Util.Log("flights: hub read failed (" + e.GetType().Name + ")"); }
            return all;
        }

        static string cacheKey = ""; static Local cached;

        // cheap enough to call every few seconds: the files are only read again when one changed
        public static Local ReadCached(string root, out bool changed)
        {
            if (TestSource != null) { changed = true; return Read(root); }
            string key = "";
            try { key = string.Join("|", SavedVarsFiles.For(root, HunterHelperFile).Concat(SavedVarsFiles.For(root, HubData.FileName)).Select(f => f + ":" + File.GetLastWriteTimeUtc(f).Ticks + ":" + new FileInfo(f).Length)); }
            catch { }
            if (cached != null && key == cacheKey) { changed = false; return cached; }
            cached = Read(root); cacheKey = key; changed = true;
            return cached;
        }

        public static void ResetCache() { cached = null; cacheKey = ""; }

        // ---------------------------------------------------------------- uploading

        public class Batch
        {
            public Dictionary<string, object> Frame;                                       // the flights.share message
            public Dictionary<string, string> Sigs = new Dictionary<string, string>();       // key -> signature, what to mark as sent after the answer
            public int Remaining;                                                          // changed legs that did not fit (next time)
        }

        // The legs whose value differs from what was uploaded before, as one flights.share frame: at most 500 legs and 56 KB; names only for the
        // nodes those legs use (at most 300). Null when nothing changed.
        public static Batch MakeBatch(Local local, IDictionary<string, string> sent)
        {
            if (local == null) return null;
            var changed = local.Legs.Where(kv => !sent.TryGetValue(kv.Key, out var s) || s != kv.Value.Sig).OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();
            if (changed.Count == 0) return null;
            var b = new Batch();
            var legs = new Dictionary<string, object>();
            int bytes = 64;
            foreach (var kv in changed)
            {
                int cost = kv.Key.Length + 32;
                if (legs.Count >= MaxLegsPerShare || bytes + cost > MaxShareBytes) break;
                legs[kv.Key] = new Dictionary<string, object> { ["t"] = kv.Value.T, ["n"] = kv.Value.N };
                b.Sigs[kv.Key] = kv.Value.Sig;
                bytes += cost;
            }
            b.Remaining = changed.Count - legs.Count;
            var names = new Dictionary<string, object>();
            foreach (var id in legs.Keys.SelectMany(IdsOf).Distinct())
            {
                if (names.Count >= MaxNamesPerShare) break;
                if (!local.Names.TryGetValue(id, out var nm)) continue;
                int cost = id.Length + nm.Length + 8;
                if (bytes + cost > MaxShareBytes) break;
                names[id] = nm; bytes += cost;
            }
            b.Frame = new Dictionary<string, object> { ["t"] = "flights.share", ["legs"] = legs };
            if (names.Count > 0) b.Frame["names"] = names;
            return b;
        }

        // ---------------------------------------------------------------- what the server returns

        public class Merged
        {
            public Dictionary<string, Leg> Legs = new Dictionary<string, Leg>(StringComparer.Ordinal);
            public Dictionary<string, string> Names = new Dictionary<string, string>(StringComparer.Ordinal);
            public long At;
            public int Dropped;
        }

        // flights.data: everything is re-validated here, whatever the server says
        public static Merged ParseServer(Dictionary<string, object> m)
        {
            var r = new Merged();
            if (m == null) return r;
            r.At = m.Long("at"); if (r.At < 0 || r.At > 4_000_000_000L) r.At = 0;
            var legs = m.Child("legs");
            if (legs != null)
                foreach (var kv in legs)
                {
                    if (r.Legs.Count >= MaxLegs) { r.Dropped++; continue; }
                    var d = kv.Value as Dictionary<string, object>;
                    if (!ValidKey(kv.Key) || d == null || !(d.TryGetValue("t", out var tv) && tv is double t) || !(d.TryGetValue("n", out var nv) && nv is double n) || !ValidLeg(t, n)) { r.Dropped++; continue; }
                    r.Legs[kv.Key] = new Leg { T = RoundT(t), N = (int)n };
                }
            var names = m.Child("names");
            if (names != null)
                foreach (var kv in names)
                {
                    if (r.Names.Count >= MaxNames) break;
                    var nm = kv.Value is string s ? CleanName(s) : "";
                    if (ValidId(kv.Key) && nm.Length > 0) r.Names[kv.Key] = nm;
                }
            return r;
        }

        // ---------------------------------------------------------------- SharedData.lua

        // ElansHubShared = { flights = { at = <unix>, legs = { ["<key>"] = { t = <seconds>, n = <samples> } }, names = { ["<id>"] = "<name>" } } }
        // The text is built only from validated parts: keys and ids matched the whitelist regexes (nothing in them needs escaping), numbers are
        // formatted from parsed doubles / ints, names went through CleanName and are escaped again here.
        public static string BuildLua(Merged data, DateTime nowUtc)
        {
            var sb = new StringBuilder();
            sb.Append("-- Generated by Elan's Outpost - do not edit. The Outpost overwrites this file.\n");
            sb.Append("-- ").Append(nowUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(" UTC\n");
            sb.Append("ElansHubShared = {\n\tflights = {\n");
            sb.Append("\t\tat = ").Append(Math.Max(0, data?.At ?? 0).ToString(CultureInfo.InvariantCulture)).Append(",\n");
            sb.Append("\t\tlegs = {\n");
            var used = new HashSet<string>(StringComparer.Ordinal);
            if (data != null)
                foreach (var kv in data.Legs.OrderBy(k => k.Key, StringComparer.Ordinal).Take(MaxLegs))
                {
                    if (!ValidKey(kv.Key) || !ValidLeg(kv.Value.T, kv.Value.N)) continue;
                    sb.Append("\t\t\t[\"").Append(kv.Key).Append("\"] = { t = ").Append(RoundT(kv.Value.T).ToString("0.0", CultureInfo.InvariantCulture))
                      .Append(", n = ").Append(kv.Value.N.ToString(CultureInfo.InvariantCulture)).Append(" },\n");
                    foreach (var id in IdsOf(kv.Key)) used.Add(id);
                }
            sb.Append("\t\t},\n\t\tnames = {\n");
            if (data != null)
                foreach (var kv in data.Names.OrderBy(k => k.Key, StringComparer.Ordinal).Where(k => used.Contains(k.Key)).Take(MaxNames))
                {
                    var nm = CleanName(kv.Value);
                    if (!ValidId(kv.Key) || nm.Length == 0) continue;
                    sb.Append("\t\t\t[\"").Append(kv.Key).Append("\"] = \"").Append(LuaEscape(nm)).Append("\",\n");
                }
            sb.Append("\t\t},\n\t},\n}\n");
            return sb.ToString();
        }

        // inside a double-quoted Lua string: backslash and quote escaped, newlines / control characters dropped
        public static string LuaEscape(string s)
        {
            var sb = new StringBuilder(s.Length + 4);
            foreach (var c in s)
            {
                if (c == '\\') sb.Append("\\\\");
                else if (c == '"') sb.Append("\\\"");
                else if (c < 0x20 || (c >= 0x7f && c <= 0x9f) || c == 0x2028 || c == 0x2029) continue;
                else sb.Append(c);
            }
            return sb.ToString();
        }

        // everything after the two header comment lines: used to leave an unchanged file alone
        static string Body(string lua)
        {
            if (lua == null) return null;
            var lines = lua.Replace("\r\n", "\n").Split('\n');
            return string.Join("\n", lines.SkipWhile(l => l.StartsWith("--")));
        }

        // the tocs of an ElansHub folder that list SharedData.lua (an old companion without it is never touched)
        public static bool TocLists(string tocPath)
        {
            try
            {
                foreach (var line in File.ReadAllLines(tocPath))
                {
                    var l = line.Trim();
                    if (l.Length == 0 || l.StartsWith("#")) continue;
                    if (string.Equals(l, SharedFileName, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            catch { }
            return false;
        }

        static bool IsReparse(string path)
        {
            try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; } catch { return true; }
        }

        public class WriteResult { public List<string> Written = new List<string>(), Unchanged = new List<string>(); public int Skipped; }

        // Writes SharedData.lua into <root>\_<flavor>_\Interface\AddOns\ElansHub\ for each flavor whose ElansHub folder exists and whose toc lists it.
        // Atomic: a temp file in the same folder, then a replace. Nothing else is created, changed or deleted (a leftover temp of ours is removed).
        public static WriteResult WriteShared(string root, string lua)
        {
            var res = new WriteResult();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root) || string.IsNullOrEmpty(lua)) return res;
            string[] flavors;
            try { flavors = Directory.GetDirectories(root, "_*_"); } catch { return res; }
            foreach (var flavor in flavors)
            {
                try
                {
                    var dir = Path.Combine(flavor, "Interface", "AddOns", "ElansHub");
                    if (!Directory.Exists(dir) || IsReparse(dir) || !TocLists(Path.Combine(dir, "ElansHub.toc"))) { res.Skipped++; continue; }
                    var dest = Path.Combine(dir, SharedFileName);
                    var tmp = dest + ".tmp";
                    if (File.Exists(dest) && IsReparse(dest)) { res.Skipped++; continue; }
                    if (File.Exists(dest) && Body(SavedVarsFiles.SafeRead(dest)) == Body(lua)) { res.Unchanged.Add(dest); continue; }
                    try
                    {
                        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            var bytes = new UTF8Encoding(false).GetBytes(lua);
                            fs.Write(bytes, 0, bytes.Length);
                            fs.Flush(true);
                        }
                        if (File.Exists(dest)) File.Replace(tmp, dest, null); else File.Move(tmp, dest);
                        res.Written.Add(dest);
                    }
                    catch { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } throw; }
                }
                catch (Exception e) { Util.Log("flights: could not write " + SharedFileName + " (" + e.GetType().Name + ")"); res.Skipped++; }
            }
            return res;
        }
    }
}
