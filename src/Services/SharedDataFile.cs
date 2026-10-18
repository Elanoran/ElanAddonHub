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
    //     fieldNotes = { ... }          what the Lodge's hunters noted about tameable beasts, for Hunter Helper's Beast Finder (BuildFieldNotes below)
    //     beasts     = { ... }          what the server's own creature cache says about beasts: tameable, pet family, boss (BuildBeasts below)
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

        // the `fieldNotes = { ... },` section (BuildFieldNotes) of the Lodge's hunter notes; the Lodge session sets it when they change and every write of the file carries it ("" = none)
        public static string FieldNotesText = "";
        public const int MaxFieldNoteNpcs = 500, MaxFieldNotePoints = 30;
        public const long MaxFieldNotesBytes = 150L * 1024;

        // the `beasts = { ... },` section (BuildBeasts) of what the game's creature cache (this PC's and the Lodge's) says; set when the seen data changes, every write of the file carries it ("" = none)
        public static string BeastsText = "";
        public const int MaxBeasts = 3000;
        public const long MaxBeastsBytes = 120L * 1024;

        public const int Version = 1;
        public const int MaxGuidesPerChar = 6, MaxSteps = 60, MaxReagents = 20, MaxRoutePoints = 120, MaxNodesPerObjectMap = 300, MaxObjectsPerStep = 12, MaxZonesPerStep = 8;
        public const int MaxChars = 40, MaxCharKey = 80, MaxText = 120, MaxId = 9_999_999;
        public const long MaxFileBytes = 400L * 1024;

        // ---------------------------------------------------------------- composing

        // The whole file text. With no guides this is exactly FlightShare.BuildLua (byte for byte), so a PC that follows nothing gets what it always got.
        public static string Compose(FlightShare.Merged flights, DateTime nowUtc, ProfGuidesOut guides, IEnumerable<string> extraSections = null)
        {
            var extras = (extraSections ?? Enumerable.Empty<string>()).Where(s => !string.IsNullOrEmpty(s)).ToList();
            var notes = FieldNotesText;
            if (!string.IsNullOrEmpty(notes) && !extras.Contains(notes)) extras.Add(notes);
            var beasts = BeastsText;
            if (!string.IsNullOrEmpty(beasts) && !extras.Contains(beasts)) extras.Add(beasts);
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

        // The `fieldNotes = { ... },` text (one tab deep, newline last); "" when there are no notes. The Lodge's merge of the hunters' notes, at most MaxFieldNoteNpcs beasts
        // (the best supported and the newest first) and about MaxFieldNotesBytes. Built from numbers and whitelisted words only; every name goes through Text() (cleaned, escaped).
        //   fieldNotes = { v = 1, at = <newest sighting>, npcs = { [<npcId>] = { name, min, max, n, m, last, class, fam, taught = { ["bite4"] = <members> }, pts = { [<uiMapId>] = { { x, y }, ... } } } } }
        // x, y are fractions of the map; n = sightings in the Lodge, m = hunters.
        public static string BuildFieldNotes(IEnumerable<AtlasObs.FieldNote> notes)
        {
            if (notes == null) return "";
            var inv = CultureInfo.InvariantCulture;
            var list = notes.Where(n => n != null && Id(n.Id) && (n.N > 0 || n.Min > 0 || n.Taught.Count > 0 || n.Pts.Count > 0))
                .OrderByDescending(n => n.Members).ThenByDescending(n => n.Last).ThenBy(n => n.Id).Take(MaxFieldNoteNpcs).OrderBy(n => n.Id).ToList();
            if (list.Count == 0) return "";
            var body = new StringBuilder();
            long newest = 0;
            foreach (var n in list)
            {
                if (body.Length > MaxFieldNotesBytes) break;
                newest = Math.Max(newest, n.Last);
                body.Append("\t\t\t[").Append(I(n.Id)).Append("] = { ");
                if (n.Name.Length > 0) body.Append("name = \"").Append(Text(n.Name, 80)).Append("\", ");
                if (n.Min > 0 && n.Min <= 100) body.Append("min = ").Append(I(n.Min)).Append(", max = ").Append(I(Math.Max(n.Min, Math.Min(100, n.Max)))).Append(", ");
                body.Append("n = ").Append(I(Math.Max(0, Math.Min(2_000_000_000L, n.N)))).Append(", m = ").Append(I(Math.Max(0, Math.Min(100_000, n.Members))));
                if (n.Last > 0) body.Append(", last = ").Append(I(n.Last));
                if (Array.IndexOf(AtlasObs.NoteClasses, n.Class) >= 0) body.Append(", class = \"").Append(n.Class).Append('"');
                if (n.Fam.Length > 0) body.Append(", fam = \"").Append(Text(n.Fam, 32)).Append('"');
                var taught = n.Taught.Where(t => t.Key.Length > 0 && t.Key.Length <= 26 && t.Key.All(ch => (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9'))).OrderBy(t => t.Key, StringComparer.Ordinal).Take(AtlasObs.MaxNoteTaught).ToList();
                if (taught.Count > 0) body.Append(", taught = { ").Append(string.Join(", ", taught.Select(t => "[\"" + t.Key + "\"] = " + I(Math.Max(1, Math.Min(100_000, t.Value)))))).Append(" }");
                int left = MaxFieldNotePoints;
                var maps = new List<string>();
                foreach (var m in n.Pts.Where(m => m.Key >= 1 && m.Key <= 99_999).OrderBy(m => m.Key))
                {
                    if (left <= 0) break;
                    var pts = m.Value.Where(p => Frac(p.X) && Frac(p.Y)).Take(left).ToList();
                    if (pts.Count == 0) continue;
                    left -= pts.Count;
                    maps.Add("[" + I(m.Key) + "] = { " + string.Join(", ", pts.Select(p => "{ " + F(p.X) + ", " + F(p.Y) + " }")) + " }");
                }
                if (maps.Count > 0) body.Append(", pts = { ").Append(string.Join(", ", maps)).Append(" }");
                body.Append(" },\n");
            }
            var sb = new StringBuilder();
            sb.Append("\tfieldNotes = {\n\t\tv = ").Append(Version.ToString(inv)).Append(", at = ").Append(I(Math.Max(0, newest))).Append(",\n\t\tnpcs = {\n");
            sb.Append(body).Append("\t\t},\n\t},\n");
            return sb.ToString();
        }

        // The `beasts = { ... },` text (one tab deep, newline last); "" when there is nothing. What the server's creature cache says about every creature that is a beast (type 1) or has a pet family,
        // at most MaxBeasts and about MaxBeastsBytes (the tameable ones first, then the ones with a family, then the rest; by id within each):
        //   beasts = { [<npcId>] = { tame = 1|0, fam = <creature family id>, boss = true, build = "<game build of the record>", name = "<name, tameable ones only>" } }
        // tame is the creature type flag 0x1; fam is the game's CreatureFamily id (1 wolf, 2 cat, 3 spider ...; absent = none); name is there for the tameable ones so Hunter Helper can list a beast
        // its own data lacks. Numbers are re-formatted, the name goes through Text() (cleaned, escaped).
        public static string BuildBeasts(IDictionary<int, Atlas.SeenView.NpcFacts> facts)
        {
            if (facts == null) return "";
            int Rank(Atlas.SeenView.NpcFacts f) => f.Tameable ? 0 : f.Family > 0 ? 1 : 2;
            var list = facts.Where(kv => kv.Value != null && Id(kv.Key) && (kv.Value.Type == 1 || kv.Value.Family > 0))
                .OrderBy(kv => Rank(kv.Value)).ThenBy(kv => kv.Key).Take(MaxBeasts).ToList();
            var lines = new List<KeyValuePair<int, string>>();
            long bytes = 0;
            foreach (var kv in list)
            {
                var f = kv.Value;
                var sb = new StringBuilder();
                sb.Append("\t\t[").Append(I(kv.Key)).Append("] = { tame = ").Append(f.Tameable ? "1" : "0");
                if (f.Family >= 1 && f.Family <= 100_000) sb.Append(", fam = ").Append(I(f.Family));
                if (f.BossLevel) sb.Append(", boss = true");
                if (f.Build > 0 && f.Build <= 99_999_999) sb.Append(", build = \"").Append(I(f.Build)).Append('"');
                if (f.Tameable) { var nm = Text(f.Name ?? "", 60); if (nm.Length > 0) sb.Append(", name = \"").Append(nm).Append('"'); }
                sb.Append(" },\n");
                if (bytes + sb.Length > MaxBeastsBytes) break;
                bytes += sb.Length;
                lines.Add(new KeyValuePair<int, string>(kv.Key, sb.ToString()));
            }
            if (lines.Count == 0) return "";
            var res = new StringBuilder("\tbeasts = {\n");
            foreach (var l in lines.OrderBy(l => l.Key)) res.Append(l.Value);
            res.Append("\t},\n");
            return res.ToString();
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
            using (Activity.Begin("Writing data for your add-ons", Activity.Kind.Save)) return WriteNow(root, flights, guides, nowUtc, extraSections);
        }

        static FlightShare.WriteResult WriteNow(string root, FlightShare.Merged flights, ProfGuidesOut guides, DateTime nowUtc, IEnumerable<string> extraSections)
        {
            bool haveGuides = guides != null && !guides.IsEmpty;
            if (flights == null && !haveGuides && !(extraSections ?? Enumerable.Empty<string>()).Any(s => !string.IsNullOrEmpty(s)) && string.IsNullOrEmpty(FieldNotesText) && string.IsNullOrEmpty(BeastsText) && !FileHasGuides(root) && !FileHasText(root, "fieldNotes") && !FileHasText(root, "	beasts = {")) return null;
            // guides removed ("Stop following") while no Lodge data is in this session: the guides leave the file, the flights in it stay
            var keep = flights ?? ReadFlights(root);
            var lua = Compose(keep, nowUtc, guides, extraSections);
            return FlightShare.WriteShared(root, lua);
        }

        // true when SharedData.lua of any flavor under the WoW folder holds a profGuides section (so "Stop following" knows whether it has something to remove)
        public static bool FileHasGuides(string root) { return FileHasText(root, "profGuides"); }

        // true when SharedData.lua of any flavor under the WoW folder mentions `text` (a section name)
        public static bool FileHasText(string root, string text)
        {
            try
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return false;
                foreach (var flavor in Directory.GetDirectories(root, "_*_"))
                {
                    var f = Path.Combine(flavor, "Interface", "AddOns", "ElansHub", FlightShare.SharedFileName);
                    if (File.Exists(f) && (SavedVarsFiles.SafeRead(f) ?? "").Contains(text)) return true;
                }
            }
            catch (Exception) { }
            return false;
        }
    }
}
