using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ElansAddonHub.Services
{
    // Addon health: what our addons wrote into their SavedVariables (Bug Trap entries + diag runs). Read-only.
    // Shape in every addon: db.bugs = { {msg, stack, count, time, at, version, addon}, ... } and db.diag.runs = { {ctx, res, ok, secret, err, at, version, auto}, ... }.
    // WoW writes the files only on /reload and logout, so this is always "as of the last reload".
    public class HealthAddonDef
    {
        public string Id, Title, DbName, Slash;
        public string File => Id + ".lua";
    }

    public class HealthBug
    {
        public string Addon, Msg, Stack, Version, Time;
        public int Count = 1;
        public long At;
        public bool IsNew, Older;                 // Older: seen with an older version than the installed one (probably fixed)
        public string Key => Addon + "|" + Msg;
        public string FirstLine
        {
            get
            {
                var s = (Stack ?? "").Replace("\r", "").Split('\n').Select(x => x.Trim()).FirstOrDefault(x => x.Length > 0);
                return s ?? "";
            }
        }
    }

    public class HealthRun
    {
        public long At;
        public string Label, Version, Build, Time;
        public bool InCombat, Auto;
        public int Ok, Secret, Err;
        public Dictionary<string, string> Res = new Dictionary<string, string>();   // probe name -> short text ("S" = secret value)
        public List<string> ProbeErrors = new List<string>();                        // errors the probe itself saw (Bug Trap)
    }

    public class AddonHealth
    {
        public HealthAddonDef Def;
        public string Installed;                  // version in the toc of the installed addon (null: not installed)
        public bool HasFile;
        public bool Unreadable;                   // the file exists but could not be parsed (damaged or caught half-written)
        public DateTime FileWritten;              // newest SavedVariables write (UTC)
        public string SavedWith;                  // db.lastVersion: the version at the last login that was saved
        public List<HealthRun> Runs = new List<HealthRun>();
        public List<HealthBug> Bugs = new List<HealthBug>();
        public int OtherErrors;                   // entries about other people's addons (not shown)
        public HealthRun Last => Runs.Count > 0 ? Runs[0] : null;
        public string DiagVersion => Last?.Version;
        public int NewCount => Bugs.Count(b => b.IsNew);
        public bool DiagBehind => Installed != null && Last != null && Last.Version != null && Util.CompareVersions(Last.Version, Installed) < 0;
        public bool NoDiag => Last == null;
        public string Title => Def.Title;
    }

    public class HealthReport
    {
        public List<AddonHealth> Addons = new List<AddonHealth>();
        public DateTime ReadAt = DateTime.Now;
        public List<string> Folders = new List<string>();          // the SavedVariables folders that exist
        public List<string> Names = new List<string>();            // character names found in the files (the anonymous report calls them char1, char2 ...)
        public List<string> Guilds = new List<string>();           // guild names (guild1 ...)
        public int NewTotal => Addons.Sum(a => a.NewCount);
        public int ErrorTotal => Addons.Sum(a => a.Bugs.Count);
        public bool AnyData => Addons.Any(a => a.HasFile);
    }

    public static class HealthReader
    {
        public static readonly HealthAddonDef[] Defs =
        {
            new HealthAddonDef { Id = "ElansHunterHelper", Title = "Elan's Hunter Helper", DbName = "ElansHunterHelperDB", Slash = "/ehh diag" },
            new HealthAddonDef { Id = "ElansPaladinHelper", Title = "Elan's Paladin Helper", DbName = "ElansPaladinHelperDB", Slash = "/eph diag" },
            new HealthAddonDef { Id = "ElansBags", Title = "Elan's Bags", DbName = "ElansBagsDB", Slash = "/ebags diag" },
            new HealthAddonDef { Id = "ElansHub", Title = "Elan's Hub companion", DbName = "ElansHubDB", Slash = "/ehub diag" },
        };

        // WTF\Account\*\SavedVariables of every client folder
        public static List<string> Dirs(string root)
        {
            var dirs = new List<string>();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return dirs;
            try
            {
                foreach (var flavor in Directory.GetDirectories(root, "_*_"))
                {
                    var accounts = Path.Combine(flavor, "WTF", "Account");
                    if (!Directory.Exists(accounts)) continue;
                    foreach (var acc in Directory.GetDirectories(accounts))
                    {
                        var d = Path.Combine(acc, "SavedVariables");
                        if (Directory.Exists(d)) dirs.Add(d);
                    }
                }
            }
            catch { }
            return dirs;
        }

        static string ReadShared(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs, new UTF8Encoding(false), true))
                return sr.ReadToEnd();
        }

        // version from the toc of the installed addon (first flavor that has it)
        static string InstalledVersion(string root, string folder)
        {
            try
            {
                foreach (var flavor in Directory.GetDirectories(root, "_*_"))
                {
                    var dir = Path.Combine(flavor, "Interface", "AddOns", folder);
                    if (!Directory.Exists(dir)) continue;
                    foreach (var name in new[] { folder + "_Camelot.toc", folder + ".toc" })
                    {
                        var f = Path.Combine(dir, name);
                        if (!File.Exists(f)) continue;
                        foreach (var line in File.ReadAllLines(f))
                        {
                            var m = Regex.Match(line.TrimStart('﻿'), @"^##\s*Version\s*:\s*(.+?)\s*$", RegexOptions.IgnoreCase);
                            if (m.Success) return m.Groups[1].Value;
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        public static HealthReport Read(string root)
        {
            var rep = new HealthReport();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var guilds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            rep.Folders = Dirs(root);
            foreach (var def in Defs)
            {
                var h = new AddonHealth { Def = def, Installed = string.IsNullOrEmpty(root) ? null : InstalledVersion(root, def.Id) };
                DateTime bestWritten = DateTime.MinValue;
                bool parsedOk = false;
                var bugs = new Dictionary<string, HealthBug>(StringComparer.Ordinal);
                foreach (var dir in rep.Folders)
                {
                    var file = Path.Combine(dir, def.File);
                    if (!File.Exists(file)) continue;
                    try
                    {
                        var written = File.GetLastWriteTimeUtc(file);
                        h.HasFile = true;
                        if (written > h.FileWritten) h.FileWritten = written;
                        var globals = LuaData.ReadGlobals(ReadShared(file));
                        parsedOk = true;
                        if (!globals.TryGetValue(def.DbName, out var dbObj) || !(dbObj is Dictionary<string, object> db)) continue;
                        // runs and "saved with" from the newest file only
                        if (written >= bestWritten)
                        {
                            bestWritten = written;
                            h.SavedWith = Str(db, "lastVersion") ?? Str(db, "version");
                            h.Runs = ReadRuns(db);
                        }
                        foreach (var b in ReadBugs(db, def.Id, out var other))
                        {
                            h.OtherErrors += other;
                            other = 0;
                            if (bugs.TryGetValue(b.Key, out var old))
                            {
                                old.Count = Math.Max(old.Count, b.Count);
                                if (b.At > old.At) { old.At = b.At; old.Time = b.Time; old.Version = b.Version ?? old.Version; }
                            }
                            else bugs[b.Key] = b;
                        }
                        CollectNames(db, names, guilds);
                    }
                    catch (Exception e) { Util.Log("health read failed: " + file + ": " + e.Message); }
                }
                h.Unreadable = h.HasFile && !parsedOk;
                h.Bugs = bugs.Values.OrderByDescending(b => b.At).ThenBy(b => b.Msg, StringComparer.Ordinal).ToList();
                // entries that other Elan addons caught belong to the addon they name; the same error in two files counts once
                rep.Addons.Add(h);
            }
            // errors that an addon caught for another one of ours (the Hunter Helper traps "all addons" by default): move them over
            foreach (var h in rep.Addons.ToList())
            {
                foreach (var b in h.Bugs.Where(x => !string.Equals(x.Addon, h.Def.Id, StringComparison.OrdinalIgnoreCase)).ToList())
                {
                    var target = rep.Addons.FirstOrDefault(a => string.Equals(a.Def.Id, b.Addon, StringComparison.OrdinalIgnoreCase));
                    h.Bugs.Remove(b);
                    if (target == null) continue;
                    var same = target.Bugs.FirstOrDefault(x => x.Key == b.Key);
                    if (same == null) target.Bugs.Add(b);
                    else { same.Count = Math.Max(same.Count, b.Count); if (b.At > same.At) { same.At = b.At; same.Time = b.Time; } }
                }
            }
            foreach (var h in rep.Addons)
            {
                h.Bugs = h.Bugs.OrderByDescending(b => b.At).ThenBy(b => b.Msg, StringComparer.Ordinal).ToList();
                foreach (var b in h.Bugs) b.Older = h.Installed != null && b.Version != null && Util.CompareVersions(b.Version, h.Installed) < 0;
            }
            // only addons that exist (installed or with data) are listed
            rep.Addons = rep.Addons.Where(a => a.Installed != null || a.HasFile).ToList();
            rep.Names = names.Where(n => n != null && n.Length >= 3).ToList();
            rep.Guilds = guilds.Where(n => n != null && n.Length >= 3).ToList();
            return rep;
        }

        static List<HealthBug> ReadBugs(Dictionary<string, object> db, string fileAddon, out int other)
        {
            other = 0;
            var res = new List<HealthBug>();
            if (!db.TryGetValue("bugs", out var o) || !(o is Dictionary<string, object> list)) return res;
            foreach (var kv in list.OrderBy(k => int.TryParse(k.Key, out var n) ? n : int.MaxValue))
            {
                if (!(kv.Value is Dictionary<string, object> t)) continue;
                var addon = Str(t, "addon");
                string id = Defs.FirstOrDefault(d => string.Equals(d.Id, addon, StringComparison.OrdinalIgnoreCase))?.Id;
                if (id == null)
                {
                    if (!string.IsNullOrEmpty(addon)) { other++; continue; }   // somebody else's addon
                    id = fileAddon;                                            // no addon named: the file's own
                }
                var b = new HealthBug
                {
                    Addon = id, Msg = Str(t, "msg") ?? "?", Stack = Str(t, "stack") ?? "", Version = Str(t, "version"),
                    Count = Math.Max(1, (int)Num(t, "count")), Time = Str(t, "time"), At = (long)Num(t, "at"),
                };
                if (b.At <= 0) b.At = ParseLocal(b.Time);
                res.Add(b);
            }
            return res;
        }

        static List<HealthRun> ReadRuns(Dictionary<string, object> db)
        {
            var res = new List<HealthRun>();
            if (!db.TryGetValue("diag", out var d) || !(d is Dictionary<string, object> diag)) return res;
            var autoVersion = Str(diag, "autoVersion");
            if (!diag.TryGetValue("runs", out var r) || !(r is Dictionary<string, object> runs)) return res;
            foreach (var kv in runs.OrderBy(k => int.TryParse(k.Key, out var n) ? n : int.MaxValue))
            {
                if (!(kv.Value is Dictionary<string, object> t)) continue;
                var run = new HealthRun { Version = Str(t, "version"), At = (long)Num(t, "at"), Auto = t.TryGetValue("auto", out var a) && a is bool ab && ab,
                    Ok = (int)Num(t, "ok"), Secret = (int)Num(t, "secret"), Err = (int)Num(t, "err") };
                if (t.TryGetValue("ctx", out var c) && c is Dictionary<string, object> ctx)
                {
                    run.Label = Str(ctx, "label");
                    run.Time = Str(ctx, "time");
                    run.Build = Str(ctx, "build");
                    run.InCombat = ctx.TryGetValue("inCombat", out var ic) && ic is bool icb && icb;
                }
                else
                {
                    // the Hub companion's flat run: plain fields
                    run.Label = run.Auto ? "auto" : "manual";
                    run.InCombat = t.TryGetValue("inCombat", out var ic) && ic is bool icb ? icb : string.Equals(Str(t, "combatLockdown"), "true", StringComparison.OrdinalIgnoreCase);
                    run.Time = null;
                    foreach (var f in t) if (!(f.Value is Dictionary<string, object>) && f.Key != "time" && f.Key != "at" && f.Key != "auto" && f.Key != "version")
                        run.Res[f.Key] = Convert.ToString(f.Value is bool bb ? (bb ? "true" : "false") : f.Value, CultureInfo.InvariantCulture) ?? "";
                }
                if (t.TryGetValue("res", out var rs) && rs is Dictionary<string, object> res2)
                    foreach (var f in res2) run.Res[f.Key] = Convert.ToString(f.Value, CultureInfo.InvariantCulture) ?? "";
                if (t.TryGetValue("errors", out var er) && er is Dictionary<string, object> errs)
                    foreach (var f in errs.OrderBy(k => int.TryParse(k.Key, out var n) ? n : int.MaxValue)) run.ProbeErrors.Add(Convert.ToString(f.Value, CultureInfo.InvariantCulture));
                if (run.At <= 0) run.At = ParseLocal(run.Time ?? Str(t, "time"));
                if (run.At <= 0 && Str(t, "time") != null && long.TryParse(Str(t, "time"), out var unix)) run.At = unix;
                if (run.Version == null && run.Auto) run.Version = autoVersion;
                res.Add(run);
            }
            return res;
        }

        // character and guild names that appear in the files (the anonymous report replaces them; realms are not personal)
        static void CollectNames(Dictionary<string, object> db, HashSet<string> names, HashSet<string> guildNames)
        {
            if (db.TryGetValue("chars", out var o) && o is Dictionary<string, object> chars)
                foreach (var kv in chars)
                {
                    if (!(kv.Value is Dictionary<string, object> c)) continue;
                    var n = Str(c, "name"); if (!string.IsNullOrEmpty(n)) names.Add(n);
                    var g0 = Str(c, "guild"); if (!string.IsNullOrEmpty(g0)) guildNames.Add(g0);
                    var dash = kv.Key.IndexOf('-');
                    if (dash > 0 && string.IsNullOrEmpty(n)) names.Add(kv.Key.Substring(dash + 1));
                }
            if (db.TryGetValue("guilds", out var g) && g is Dictionary<string, object> guilds)
                foreach (var kv in guilds) if (kv.Value is Dictionary<string, object> gt) { var v = Str(gt, "name"); if (!string.IsNullOrEmpty(v)) guildNames.Add(v); }
        }

        public static long ParseLocal(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            if (DateTime.TryParseExact(s, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                return new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Local)).ToUnixTimeSeconds();
            return 0;
        }

        static string Str(Dictionary<string, object> t, string k) => t.TryGetValue(k, out var v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;
        static double Num(Dictionary<string, object> t, string k) => t.TryGetValue(k, out var v) && v is double d ? d : 0;

        // ================================================================ seen / new

        // marks which errors are new: a message never seen, or seen fewer times than now
        public static void Evaluate(HealthReport rep, Dictionary<string, int> seen)
        {
            foreach (var h in rep.Addons)
                foreach (var b in h.Bugs)
                    b.IsNew = seen == null || !seen.TryGetValue(b.Key, out var n) || b.Count > n;
        }

        public static Dictionary<string, int> CountsOf(HealthReport rep)
        {
            var d = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var h in rep.Addons) foreach (var b in h.Bugs) d[b.Key] = Math.Max(d.TryGetValue(b.Key, out var o) ? o : 0, b.Count);
            return d;
        }

        // ================================================================ facts from the diag

        static readonly Regex SecretToken = new Regex(@"(^|\| )S( \||$)", RegexOptions.Compiled);

        public static string RunKind(HealthRun r) => (r.InCombat ? "in combat" : "out of combat") + (r.Auto ? " (auto)" : "");

        public static List<string> Facts(AddonHealth h)
        {
            var facts = new List<string>();
            var r = h.Last;
            if (r == null) return facts;
            int inC = h.Runs.Count(x => x.InCombat), outC = h.Runs.Count - inC;
            facts.Add($"{h.Runs.Count} diag run{(h.Runs.Count == 1 ? "" : "s")} saved ({outC} out of combat, {inC} in combat)");
            if (r.Ok + r.Secret + r.Err > 0) facts.Add($"Latest run: {r.Ok} ok, {r.Secret} secret, {r.Err} errors");
            if (!string.IsNullOrEmpty(r.Build)) facts.Add("Client: " + r.Build);
            var secret = r.Res.Where(kv => SecretToken.IsMatch(kv.Value ?? "") || (kv.Value ?? "").EndsWith(" is S", StringComparison.Ordinal)).Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
            if (secret.Count > 0) facts.Add("Secret values: " + Join(secret, 8));
            var errs = r.Res.Where(kv => (kv.Value ?? "").StartsWith("ERR", StringComparison.Ordinal)).OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + " - " + Cut(kv.Value, 90)).ToList();
            if (errs.Count > 0) facts.Add("Probe errors: " + Join(errs, 4));
            var missing = r.Res.Where(kv => kv.Key.StartsWith("api:", StringComparison.Ordinal) && kv.Value == "NO").Select(kv => kv.Key.Substring(4)).OrderBy(k => k, StringComparer.Ordinal).ToList();
            if (missing.Count > 0) facts.Add("Missing APIs: " + Join(missing, 8));
            if (r.ProbeErrors.Count > 0) facts.Add("Errors the probe saw: " + r.ProbeErrors.Count);
            // the Hub companion's flat run: its few plain fields
            if (r.Res.Count > 0 && !r.Res.Keys.Any(k => k.Contains(":")))
                facts.Add(string.Join(", ", r.Res.Where(kv => !string.IsNullOrEmpty(kv.Value)).OrderBy(kv => kv.Key, StringComparer.Ordinal).Take(8).Select(kv => kv.Key + "=" + Cut(kv.Value, 30))));
            return facts;
        }

        static string Join(List<string> l, int max) => string.Join(", ", l.Take(max)) + (l.Count > max ? $" (+{l.Count - max} more)" : "");
        static string Cut(string s, int n) => s != null && s.Length > n ? s.Substring(0, n) + "..." : s;

        // one line for the panel: how current the data is
        public static string Status(AddonHealth h, out int level)   // 0 ok, 1 info / waiting, 2 errors
        {
            level = 0;
            if (h.Unreadable) { level = 1; return "The saved variables could not be read right now (damaged, or caught while the game was writing them)"; }
            if (!h.HasFile) { level = 1; return h.Installed != null ? "No data yet - log in with the addon, then /reload once" : "Not installed"; }
            if (h.NewCount > 0) { level = 2; return $"{h.NewCount} new error{(h.NewCount == 1 ? "" : "s")}"; }
            if (h.NoDiag) { level = 1; return "No diag saved yet - it runs by itself on the first login after an update, then /reload"; }
            if (h.DiagBehind) { level = 1; return $"Diag is from v{h.DiagVersion}, v{h.Installed} is installed - log in, wait 10 s, then /reload"; }
            return h.Bugs.Count == 0 ? "Healthy" : $"{h.Bugs.Count} error{(h.Bugs.Count == 1 ? "" : "s")} seen";
        }

        // ================================================================ the report (plain text, anonymous)

        public static string ReportText(HealthReport rep, string outpostVersion, bool anonymize = true)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Elan's Outpost - addon health report");
            sb.AppendLine($"Outpost v{outpostVersion} · {rep.ReadAt:yyyy-MM-dd HH:mm} · as of the last /reload (SavedVariables are written then)");
            var build = rep.Addons.Select(a => a.Last?.Build).FirstOrDefault(b => !string.IsNullOrEmpty(b));
            if (build != null) sb.AppendLine("WoW client: " + build);
            if (rep.Addons.Count == 0) sb.AppendLine("\nNo Elan addons found.");
            foreach (var h in rep.Addons)
            {
                sb.AppendLine();
                sb.AppendLine("== " + h.Title + " ==");
                var status = Status(h, out _);
                sb.AppendLine($"Installed: {(h.Installed != null ? "v" + h.Installed : "?")} · diag: {(h.Last == null ? "none" : "v" + (h.DiagVersion ?? "?"))} · saved variables {(h.HasFile ? "written " + h.FileWritten.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "not found")}");
                sb.AppendLine("Status: " + status);
                if (h.Last != null)
                {
                    var r = h.Last;
                    sb.AppendLine($"Last diag: {Stamp(r.At, r.Time)} · {RunKind(r)}");
                    foreach (var f in Facts(h)) sb.AppendLine("  " + f);
                }
                sb.AppendLine($"Errors: {h.Bugs.Count} ({h.NewCount} new){(h.OtherErrors > 0 ? $" · {h.OtherErrors} entries about other addons not listed" : "")}");
                int i = 0;
                foreach (var b in h.Bugs)
                {
                    sb.AppendLine($" [{++i}] x{b.Count} · v{b.Version ?? "?"}{(b.Older ? " (older version)" : "")} · {Stamp(b.At, b.Time)}{(b.IsNew ? " · NEW" : "")}");
                    sb.AppendLine("     " + b.Msg.Replace("\r", "").Replace("\n", "\n     "));
                    foreach (var line in (b.Stack ?? "").Replace("\r", "").Split('\n').Where(l => l.Trim().Length > 0).Take(8)) sb.AppendLine("       " + line.Trim());
                }
                if (h.Last != null)
                {
                    sb.AppendLine("Diag results (latest run):");
                    foreach (var kv in h.Last.Res.OrderBy(k => k.Key, StringComparer.Ordinal).Take(120)) sb.AppendLine($"  {kv.Key} = {kv.Value}");
                }
            }
            var text = sb.ToString();
            return anonymize ? Anonymize(text, rep.Names, rep.Guilds) : text;
        }

        static string Stamp(long at, string fallback)
        {
            if (at > 0) return DateTimeOffset.FromUnixTimeSeconds(at).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            return fallback ?? "?";
        }

        // character names -> char1, char2 ... ; guild names -> guild1 ... ; account folder -> <account>; Windows user -> <user>.
        // "Elan's ..." (the brand) is shielded so a character called Elan does not turn "Elan's Bags" into "char1's Bags".
        public static string Anonymize(string text, IEnumerable<string> names, IEnumerable<string> guilds = null)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var shield = new[] { "Elan's", "Elan\u2019s" };
            for (int i = 0; i < shield.Length; i++) text = text.Replace(shield[i], "\u0001" + i + "\u0002");
            text = ReplaceAll(text, names, "char");
            text = ReplaceAll(text, guilds, "guild");
            for (int i = 0; i < shield.Length; i++) text = text.Replace("\u0001" + i + "\u0002", shield[i]);
            text = Regex.Replace(text, @"([A-Za-z]:[\\/]Users[\\/])[^\\/\s""']+", "$1<user>", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"([\\/]Account[\\/])[^\\/\s""']+", "$1<account>", RegexOptions.IgnoreCase);
            try
            {
                var user = Environment.UserName;
                if (!string.IsNullOrEmpty(user) && user.Length >= 3)
                    text = Regex.Replace(text, @"(?<![\p{L}\p{N}_])" + Regex.Escape(user) + @"(?![\p{L}\p{N}_])", "<user>", RegexOptions.IgnoreCase);
            }
            catch { }
            return text;
        }

        // stable numbering by name (alphabetical); longer names first so "Elanoran" is not eaten by "Elan"
        static string ReplaceAll(string text, IEnumerable<string> names, string prefix)
        {
            var list = (names ?? Enumerable.Empty<string>()).Where(n => !string.IsNullOrWhiteSpace(n) && n.Trim().Length >= 3).Select(n => n.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            int c = 0;
            var map = list.Select(n => new KeyValuePair<string, string>(n, prefix + (++c))).ToList();
            foreach (var kv in map.OrderByDescending(k => k.Key.Length))
                text = Regex.Replace(text, @"(?<![\p{L}\p{N}_])" + Regex.Escape(kv.Key) + @"(?![\p{L}\p{N}_])", kv.Value, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return text;
        }
    }
}
