using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace ElansAddonHub.Services
{
    // ============================================================================================================================
    // Interface\AddOns\ElansHub\SharedData.lua is the Outpost's ONLY permitted write into the WoW folder. It is one composed file with ONE writer (this class, which
    // hands the actual writing to FlightShare.WriteShared):
    //
    //   ElansHubShared = {
    //     flights    = { ... }          the Lodge's merged flight times (FlightShare.BuildLua writes them, unchanged)
    //     profGuides = { ... }          the leveling guides the user chose to follow in game (this file, contract "profguide" part D)
    //   }
    //
    // More sections can follow: Compose takes extra, already validated fragments (the Talents hand-off in CharacterTalents.cs is deliberately not wired yet).
    // Nothing from a file or from the server reaches Lua as code: ids and counts are re-formatted from parsed numbers, every string is cleaned and escaped
    // with FlightShare.LuaEscape, everything has a cap, and the whole file stays under MaxFileBytes (the biggest parts are dropped first).
    // ============================================================================================================================
    public sealed class GuideStepOut
    {
        public string K = "note";                          // craft | train | gather | note
        public int From, To;                               // the skill range the step covers (train: From = the skill where it must be trained, no To)
        public int Spell, Item, N;                         // craft: the recipe spell, the product, suggested crafts; gather: Item = what it gives
        public List<int[]> Reagents;                       // craft: { itemId, count } for those N crafts
        public string Text = "";
        public List<int> Objects, Zones;                   // gather: the object ids and the zone map ids
    }

    public sealed class GuideRouteOut
    {
        public string Src = "f";                           // f = Forever nodes only, c = the Classic baseline's positions are part of it
        public List<double[]> Points = new List<double[]>();
    }

    public sealed class GuideOut
    {
        public int SkillId, Max;
        public string Name = "", Title = "";
        public List<GuideStepOut> Steps = new List<GuideStepOut>();
        public Dictionary<int, Dictionary<int, GuideRouteOut>> Routes = new Dictionary<int, Dictionary<int, GuideRouteOut>>();            // step index (1-based) -> zone -> loop
        public Dictionary<int, Dictionary<int, List<double[]>>> Nodes = new Dictionary<int, Dictionary<int, List<double[]>>>();           // object -> zone -> { x, y, n } (n = 0: a Classic baseline position)
    }

    public sealed class ProfGuidesOut
    {
        public long At;                                    // when the guides were made (unix seconds): only changes when their content does
        public Dictionary<string, List<GuideOut>> Chars = new Dictionary<string, List<GuideOut>>(StringComparer.Ordinal);   // "Realm-Name" as ElansHub.CharKey() makes it
        public bool IsEmpty => Chars.Values.All(l => l.Count == 0);
    }

    public static class SharedDataFile
    {
        // the guides the user follows, as the Professions page last made them; the Lodge session writes them next to the flights (null / empty = none)
        public static ProfGuidesOut Current;

        public const int Version = 1;
        public const int MaxGuidesPerChar = 6, MaxSteps = 60, MaxReagents = 20, MaxRoutePoints = 120, MaxNodesPerObjectMap = 300, MaxObjectsPerStep = 12, MaxZonesPerStep = 8;
        public const int MaxChars = 40, MaxCharKey = 80, MaxText = 120, MaxId = 9_999_999;
        public const long MaxFileBytes = 400L * 1024;

        // ---------------------------------------------------------------- composing

        // The whole file text. With no guides this is exactly FlightShare.BuildLua (byte for byte), so a PC that follows nothing gets what it always got.
        public static string Compose(FlightShare.Merged flights, DateTime nowUtc, ProfGuidesOut guides, IEnumerable<string> extraSections = null)
        {
            var extras = (extraSections ?? Enumerable.Empty<string>()).Where(s => !string.IsNullOrEmpty(s)).ToList();
            if ((guides == null || guides.IsEmpty) && extras.Count == 0) return FlightShare.BuildLua(flights, nowUtc);
            string lua = null;
            // under the size cap: the nodes are cut first, then the routes, then whole guides from the end
            for (int level = 0; level <= 5; level++)
            {
                var parts = new List<string>();
                if (guides != null && !guides.IsEmpty) { var g = BuildProfGuides(guides, level); if (g.Length > 0) parts.Add(g); }
                parts.AddRange(extras);
                lua = FlightShare.BuildLua(flights, nowUtc, string.Concat(parts));
                if (Encoding.UTF8.GetByteCount(lua) <= MaxFileBytes) break;
            }
            return lua;
        }

        // the `profGuides = { ... },` text (one tab deep, newline last); "" when there is nothing to write. level 0 = everything, 1 = at most 100 nodes per object and zone,
        // 2 = no node pins, 3 = no routes, 4 = three guides per character, 5 = one.
        public static string BuildProfGuides(ProfGuidesOut g, int level = 0)
        {
            if (g == null) return "";
            var inv = CultureInfo.InvariantCulture;
            int maxGuides = level >= 5 ? 1 : level >= 4 ? 3 : MaxGuidesPerChar;
            var chars = g.Chars.Where(kv => ValidCharKey(kv.Key) && kv.Value != null && kv.Value.Any(Usable)).OrderBy(kv => kv.Key, StringComparer.Ordinal).Take(MaxChars).ToList();
            if (chars.Count == 0) return "";
            var sb = new StringBuilder();
            sb.Append("\tprofGuides = {\n");
            sb.Append("\t\tv = ").Append(Version.ToString(inv)).Append(", at = ").Append(Math.Max(0, g.At).ToString(inv)).Append(",\n");
            sb.Append("\t\tchars = {\n");
            foreach (var c in chars)
            {
                sb.Append("\t\t\t[\"").Append(FlightShare.LuaEscape(c.Key)).Append("\"] = {\n");
                var seen = new HashSet<int>();
                foreach (var guide in c.Value.Where(Usable).Where(x => seen.Add(x.SkillId)).OrderBy(x => x.SkillId).Take(maxGuides))
                    AppendGuide(sb, guide, level);
                sb.Append("\t\t\t},\n");
            }
            sb.Append("\t\t},\n\t},\n");
            return sb.ToString();
        }

        static bool Usable(GuideOut g) => g != null && g.SkillId >= 1 && g.SkillId <= MaxId && g.Steps != null && g.Steps.Count > 0;

        public static bool ValidCharKey(string k)
        {
            if (string.IsNullOrWhiteSpace(k) || k.Length > MaxCharKey) return false;
            foreach (var ch in k) if (ch < 0x20 || (ch >= 0x7f && ch <= 0x9f) || ch == 0x2028 || ch == 0x2029) return false;
            return true;
        }

        // display text: control characters out, whitespace collapsed, capped; escaped for a Lua string
        public static string Text(string s, int max = MaxText)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder();
            bool space = false;
            foreach (var ch in s)
            {
                if (char.IsWhiteSpace(ch)) { space = sb.Length > 0; continue; }
                if (ch < 0x20 || (ch >= 0x7f && ch <= 0x9f) || ch == 0x2028 || ch == 0x2029 || char.IsSurrogate(ch)) continue;
                if (space) { sb.Append(' '); space = false; }
                sb.Append(ch);
                if (sb.Length >= max) break;
            }
            return FlightShare.LuaEscape(sb.ToString().TrimEnd());
        }

        static string I(long v) => v.ToString(CultureInfo.InvariantCulture);
        static string F(double v) => Math.Round(v, 4).ToString("0.####", CultureInfo.InvariantCulture);
        static bool Id(int v) => v >= 1 && v <= MaxId;
        static bool Skill(int v) => v >= 0 && v <= 1000;
        static bool Frac(double v) => !double.IsNaN(v) && !double.IsInfinity(v) && v >= 0 && v <= 1;

        static void AppendGuide(StringBuilder sb, GuideOut g, int level)
        {
            string ind = "\t\t\t\t";
            sb.Append(ind).Append('[').Append(I(g.SkillId)).Append("] = {\n");
            sb.Append(ind).Append("\tname = \"").Append(Text(g.Name, 60)).Append("\", title = \"").Append(Text(g.Title, 80)).Append("\", max = ").Append(I(Math.Max(0, Math.Min(1000, g.Max)))).Append(",\n");
            sb.Append(ind).Append("\tsteps = {\n");
            var steps = g.Steps.Take(MaxSteps).ToList();
            foreach (var s in steps)
            {
                string k = s.K == "craft" || s.K == "train" || s.K == "gather" ? s.K : "note";
                sb.Append(ind).Append("\t\t{ k = \"").Append(k).Append("\", from = ").Append(I(Skill(s.From) ? s.From : 0));
                if (k != "train" && Skill(s.To) && s.To > 0) sb.Append(", to = ").Append(I(s.To));
                if (Id(s.Spell)) sb.Append(", spell = ").Append(I(s.Spell));
                if (Id(s.Item)) sb.Append(", item = ").Append(I(s.Item));
                if (k == "craft" && s.N >= 1 && s.N <= 1_000_000) sb.Append(", n = ").Append(I(s.N));
                if (s.Reagents != null)
                {
                    var rs = s.Reagents.Where(r => r != null && r.Length == 2 && Id(r[0]) && r[1] >= 1 && r[1] <= 100_000).Take(MaxReagents).ToList();
                    if (rs.Count > 0) sb.Append(", r = { ").Append(string.Join(", ", rs.Select(r => "{ " + I(r[0]) + ", " + I(r[1]) + " }"))).Append(" }");
                }
                if (s.Objects != null)
                {
                    var os = s.Objects.Where(Id).Distinct().Take(MaxObjectsPerStep).ToList();
                    if (os.Count > 0) sb.Append(", obj = { ").Append(string.Join(", ", os.Select(o => I(o)))).Append(" }");
                }
                if (s.Zones != null)
                {
                    var zs = s.Zones.Where(z => z >= 1 && z <= 99_999).Distinct().Take(MaxZonesPerStep).ToList();
                    if (zs.Count > 0) sb.Append(", z = { ").Append(string.Join(", ", zs.Select(z => I(z)))).Append(" }");
                }
                sb.Append(", t = \"").Append(Text(s.Text)).Append("\" },\n");
            }
            sb.Append(ind).Append("\t},\n");
            if (level < 3)
            {
                var routes = g.Routes.Where(r => r.Key >= 1 && r.Key <= steps.Count && steps[r.Key - 1].K == "gather").OrderBy(r => r.Key).ToList();
                var lines = new List<string>();
                foreach (var r in routes)
                {
                    var zs = r.Value.Where(z => z.Key >= 1 && z.Key <= 99_999 && z.Value != null).OrderBy(z => z.Key).Select(z =>
                    {
                        var pts = z.Value.Points.Where(p => p != null && p.Length == 2 && Frac(p[0]) && Frac(p[1])).Take(MaxRoutePoints).ToList();
                        if (pts.Count == 0) return null;
                        return "[" + I(z.Key) + "] = { src = \"" + (z.Value.Src == "c" ? "c" : "f") + "\", pts = { " + string.Join(", ", pts.Select(p => "{ " + F(p[0]) + ", " + F(p[1]) + " }")) + " } }";
                    }).Where(t => t != null).ToList();
                    if (zs.Count > 0) lines.Add(ind + "\t\t[" + I(r.Key) + "] = { " + string.Join(", ", zs) + " },\n");
                }
                if (lines.Count > 0) { sb.Append(ind).Append("\troutes = {\n"); foreach (var l in lines) sb.Append(l); sb.Append(ind).Append("\t},\n"); }
            }
            if (level < 2)
            {
                int cap = level >= 1 ? 100 : MaxNodesPerObjectMap;
                var lines = new List<string>();
                var wanted = new HashSet<int>(steps.Where(s => s.K == "gather" && s.Objects != null).SelectMany(s => s.Objects));
                foreach (var o in g.Nodes.Where(o => Id(o.Key) && wanted.Contains(o.Key)).OrderBy(o => o.Key))
                {
                    var zs = o.Value.Where(z => z.Key >= 1 && z.Key <= 99_999 && z.Value != null).OrderBy(z => z.Key).Select(z =>
                    {
                        var pts = z.Value.Where(p => p != null && p.Length == 3 && Frac(p[0]) && Frac(p[1]) && p[2] >= 0 && p[2] <= 100_000_000).Take(cap).ToList();
                        if (pts.Count == 0) return null;
                        return "[" + I(z.Key) + "] = { " + string.Join(", ", pts.Select(p => "{ " + F(p[0]) + ", " + F(p[1]) + ", " + I((long)Math.Round(p[2])) + " }")) + " }";
                    }).Where(t => t != null).ToList();
                    if (zs.Count > 0) lines.Add(ind + "\t\t[" + I(o.Key) + "] = { " + string.Join(", ", zs) + " },\n");
                }
                if (lines.Count > 0) { sb.Append(ind).Append("\tnodes = {\n"); foreach (var l in lines) sb.Append(l); sb.Append(ind).Append("\t},\n"); }
            }
            sb.Append(ind).Append("},\n");
        }

        // ---------------------------------------------------------------- reading the file that is there (to keep the flights when only the guides change)

        // The flight times a SharedData.lua of this WoW folder holds (the first flavor that has the file), re-validated like a download. null: no such file.
        public static FlightShare.Merged ReadFlights(string root)
        {
            try
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return null;
                foreach (var flavor in Directory.GetDirectories(root, "_*_"))
                {
                    var f = Path.Combine(flavor, "Interface", "AddOns", "ElansHub", FlightShare.SharedFileName);
                    if (!File.Exists(f) || new FileInfo(f).Length > 8L * 1024 * 1024) continue;
                    var globals = LuaData.ReadGlobals(SavedVarsFiles.SafeRead(f) ?? "");
                    if (globals != null && globals.TryGetValue("ElansHubShared", out var v) && v is Dictionary<string, object> shared && shared.Child("flights") is Dictionary<string, object> fl)
                        return FlightShare.ParseServer(fl);
                }
            }
            catch (Exception e) { Util.Log("shared data: could not read the file that is there (" + e.GetType().Name + ")"); }
            return null;
        }

        // ---------------------------------------------------------------- writing

        // Composes and writes. `flights` = the Lodge's merged times of this session; null = keep what the file already holds.
        // Returns null when there is nothing to write (no flights known, no file, no guides): the file is then left alone.
        public static FlightShare.WriteResult Write(string root, FlightShare.Merged flights, ProfGuidesOut guides, DateTime nowUtc, IEnumerable<string> extraSections = null)
        {
            bool haveGuides = guides != null && !guides.IsEmpty;
            if (flights == null && !haveGuides && !(extraSections ?? Enumerable.Empty<string>()).Any(s => !string.IsNullOrEmpty(s)) && !FileHasGuides(root)) return null;
            // guides removed ("Stop following") while no Lodge data is in this session: the guides leave the file, the flights in it stay
            var keep = flights ?? ReadFlights(root);
            var lua = Compose(keep, nowUtc, guides, extraSections);
            return FlightShare.WriteShared(root, lua);
        }

        // true when SharedData.lua of any flavor under the WoW folder holds a profGuides section (so "Stop following" knows whether it has something to remove)
        public static bool FileHasGuides(string root)
        {
            try
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return false;
                foreach (var flavor in Directory.GetDirectories(root, "_*_"))
                {
                    var f = Path.Combine(flavor, "Interface", "AddOns", "ElansHub", FlightShare.SharedFileName);
                    if (File.Exists(f) && (SavedVarsFiles.SafeRead(f) ?? "").Contains("profGuides")) return true;
                }
            }
            catch (Exception) { }
            return false;
        }
    }
}
