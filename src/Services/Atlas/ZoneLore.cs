using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace ElansAddonHub.Services.Atlas
{
    // Short write-ups of the zones (the "About <zone>" card on a zone page's Overview): what the zone is, its history, who starts there, notable
    // creatures, places worth knowing, what Forever changed, and where the facts come from. Written for the Outpost in its own words and shipped in
    // the exe (Assets\Data\zonelore.json, embedded resource "Data.zonelore.json"); nothing is downloaded. Labelled "Outpost notes".
    // The creatures were checked once against the Classic baseline's spawn points (Tools: the "zonelorereal" self-test section): a creature the
    // baseline puts elsewhere was dropped; one the check could not decide (no spawn points, inside an instance, a capital's leader standing in the
    // city next door) is kept with "unchecked": true.
    public sealed class ZoneLoreMob
    {
        public string Name = "", Source = "", Why = "";
        public int Id;
        public bool Unchecked;
        public bool Linked => Id > 0 && Source != "unverified";
        public string Link => Linked ? "npc:" + Id.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        public override string ToString() => Name + " (" + Id + ", " + Source + (Unchecked ? ", unchecked" : "") + ")";
    }

    public sealed class ZoneLoreStart
    {
        public string Area = "", Note = "";
        public List<string> Races = new List<string>();
    }

    public sealed class ZoneLoreEntry
    {
        public int ZoneId;
        public string Name = "", Levels = "", Faction = "", Summary = "", History = "", Forever = "";
        public List<ZoneLoreStart> Starts = new List<ZoneLoreStart>();
        public List<ZoneLoreMob> Mobs = new List<ZoneLoreMob>();
        public List<string> Mentions = new List<string>();
        public List<string> Sources = new List<string>();

        // "Levels 1-10 · Horde", "City · Alliance", "Level 60 · Contested"
        public string FactsLine
        {
            get
            {
                var lv = (Levels ?? "").Trim();
                if (Regex.IsMatch(lv, @"^\d+\s*-\s*\d+")) lv = "Levels " + lv;
                else if (Regex.IsMatch(lv, @"^\d+$")) lv = "Level " + lv;
                return string.Join(" · ", new[] { lv, (Faction ?? "").Trim() }.Where(s => s.Length > 0));
            }
        }
    }

    public static class ZoneLore
    {
        public const string Label = "Outpost notes";
        public const string ResourceName = "Data.zonelore.json";
        static readonly object gate = new object();
        static Dictionary<int, ZoneLoreEntry> entries;
        static Dictionary<int, int> aliases;
        static string problem;

        // the entry shown on a zone's page (an alias - another map id of the same zone - gives the zone's own entry); null = none
        public static ZoneLoreEntry For(int zoneId)
        {
            Ensure();
            lock (gate) return entries.TryGetValue(Resolve(zoneId), out var e) ? e : null;
        }

        public static int Resolve(int zoneId)
        {
            Ensure();
            lock (gate) return aliases.TryGetValue(zoneId, out var to) ? to : zoneId;
        }

        public static IReadOnlyList<ZoneLoreEntry> All { get { Ensure(); lock (gate) return entries.Values.OrderBy(e => e.ZoneId).ToList(); } }
        public static IReadOnlyDictionary<int, int> Aliases { get { Ensure(); lock (gate) return new Dictionary<int, int>(aliases); } }
        public static string Problem { get { Ensure(); return problem; } }

        // the starting areas that name this race (its name or lore name), with their zone
        public static List<KeyValuePair<ZoneLoreEntry, ZoneLoreStart>> StartsFor(params string[] raceNames)
        {
            var names = raceNames.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).ToList();
            var l = new List<KeyValuePair<ZoneLoreEntry, ZoneLoreStart>>();
            if (names.Count == 0) return l;
            foreach (var e in All)
                foreach (var s in e.Starts)
                    if (s.Races.Any(r => names.Any(n => string.Equals(r, n, StringComparison.OrdinalIgnoreCase)))) l.Add(new KeyValuePair<ZoneLoreEntry, ZoneLoreStart>(e, s));
            return l;
        }

        // "warcraft.wiki.gg" for https://warcraft.wiki.gg/wiki/Durotar
        public static string SourceName(string url)
        {
            if (Uri.TryCreate(url ?? "", UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp))
                return u.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? u.Host.Substring(4) : u.Host;
            return "";
        }

        // the words a source link shows: the site; when the notes cite the same site twice, the page too ("warcraft.wiki.gg: Zephras Isle")
        public static string SourceLabel(string url, IEnumerable<string> all)
        {
            var site = SourceName(url);
            if (site.Length == 0 || (all ?? Enumerable.Empty<string>()).Count(u => SourceName(u) == site) < 2) return site;
            var path = Uri.TryCreate(url, UriKind.Absolute, out var u2) ? u2.AbsolutePath.TrimEnd('/') : "";
            var last = path.Substring(path.LastIndexOf('/') + 1);
            last = Uri.UnescapeDataString(last).Replace('_', ' ').Replace('-', ' ').Trim();
            if (last.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) last = last.Substring(0, last.Length - 5);
            return last.Length == 0 ? site : site + ": " + (last.Length > 40 ? last.Substring(0, 39).TrimEnd() + "…" : last);
        }

        // ---- loading
        static void Ensure()
        {
            lock (gate)
            {
                if (entries != null) return;
                string json = null;
                try
                {
                    using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName))
                        if (s != null) using (var r = new StreamReader(s)) json = r.ReadToEnd();
                }
                catch (Exception e) { problem = e.Message; }
                Use(json);
            }
        }

        static void Use(string json)
        {
            if (json == null) { entries = new Dictionary<int, ZoneLoreEntry>(); aliases = new Dictionary<int, int>(); problem = problem ?? "no zone notes in this build"; return; }
            if (!Parse(json, out entries, out aliases, out var why)) { problem = why; Util.Log("zone notes: " + why); }
        }

        public static void SetForTest(string json) { lock (gate) { entries = null; problem = null; Use(json); } }
        public static void ResetForTest() { lock (gate) { entries = null; aliases = null; problem = null; } }

        public static bool Parse(string json, out Dictionary<int, ZoneLoreEntry> byZone, out Dictionary<int, int> alias, out string why)
        {
            byZone = new Dictionary<int, ZoneLoreEntry>(); alias = new Dictionary<int, int>(); why = null;
            Dictionary<string, object> root;
            try { root = Json.Obj(json); }
            catch (Exception e) { why = "the zone notes do not read: " + e.Message; return false; }
            if (root == null) { why = "the zone notes are not an object"; return false; }
            var al = root.Child("aliases");
            if (al != null)
                foreach (var kv in al)
                    if (int.TryParse(kv.Key, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var from) && kv.Value is double to && to > 0) alias[from] = (int)to;
            foreach (var o in root.List("zones").OfType<Dictionary<string, object>>())
            {
                var e = new ZoneLoreEntry
                {
                    ZoneId = o.Int("zoneId"), Name = o.Str("name") ?? "", Levels = o.Str("levels") ?? "", Faction = o.Str("faction") ?? "",
                    Summary = o.Str("summary") ?? "", History = o.Str("history") ?? "", Forever = o.Str("foreverNotes") ?? "",
                };
                if (e.ZoneId <= 0) continue;
                foreach (var s in o.List("startingFor").OfType<Dictionary<string, object>>())
                    e.Starts.Add(new ZoneLoreStart { Area = s.Str("area") ?? "", Note = s.Str("note") ?? "", Races = s.List("races").OfType<string>().Where(r => r.Trim().Length > 0).ToList() });
                foreach (var m in o.List("mobs").OfType<Dictionary<string, object>>())
                {
                    var mob = new ZoneLoreMob { Name = m.Str("name") ?? "", Id = m.Int("id"), Source = m.Str("source") ?? "", Why = m.Str("why") ?? "", Unchecked = m.Bool("unchecked") };
                    if (mob.Name.Trim().Length > 0) e.Mobs.Add(mob);
                }
                e.Mentions = o.List("mentions").OfType<string>().Where(x => x.Trim().Length > 0).ToList();
                e.Sources = o.List("sources").OfType<string>().Where(x => SourceName(x).Length > 0).ToList();
                byZone[e.ZoneId] = e;
            }
            foreach (var k in alias.Keys.ToList()) if (!byZone.ContainsKey(alias[k]) || byZone.ContainsKey(k)) alias.Remove(k);     // an alias must point at an entry and not hide one
            if (byZone.Count == 0) { why = "the zone notes hold no zones"; return false; }
            return true;
        }
    }
}
