using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Atlas.Classic;
using ElansAddonHub.Services.Maps;
using ElansAddonHub.Services.WorldMap;

namespace ElansAddonHub
{
    // --selftest "instances", the marker audit on the real game files: every layer of both continents is built and every marker is looked at - on the plane, on a zone map, in the zone the
    // terrain says, of the faction the flight tables say. The numbers go into the notes; the verified flight masters, the zone rule and "nothing at 0, 0" are checks.
    public partial class MainWindow
    {
        sealed class AuditPoint { public string Layer, Name, Tip, Kind; public double X, Y; public int Map; }

        static IEnumerable<AuditPoint> AuditPoints(string layer, int mapId, LayerResult r)
        {
            var o = Json.Obj(r.Json); if (o == null) yield break;
            string kind = (string)o["kind"];
            if (kind == "points")
            {
                var pts = o.List("pts"); var names = o.List("names"); var tips = o.List("tips");
                for (int i = 0; i + 5 < pts.Count; i += 6)
                {
                    WorldMapMath.LatLngToWorld(Convert.ToDouble(pts[i], CultureInfo.InvariantCulture), Convert.ToDouble(pts[i + 1], CultureInfo.InvariantCulture), out var x, out var y);
                    int ni = Convert.ToInt32(pts[i + 2], CultureInfo.InvariantCulture), ti = Convert.ToInt32(pts[i + 3], CultureInfo.InvariantCulture);
                    yield return new AuditPoint { Layer = layer, Map = mapId, X = x, Y = y, Name = ni >= 0 && ni < names.Count ? names[ni].ToString() : "", Tip = ti >= 0 && ti < tips.Count ? tips[ti].ToString() : "" };
                }
            }
            else if (kind == "markers" || kind == "labels")
            {
                foreach (var it in o.List("items").OfType<Dictionary<string, object>>())
                {
                    WorldMapMath.LatLngToWorld(Convert.ToDouble(it["lat"], CultureInfo.InvariantCulture), Convert.ToDouble(it["lng"], CultureInfo.InvariantCulture), out var x, out var y);
                    yield return new AuditPoint { Layer = layer, Map = mapId, X = x, Y = y, Name = (string)it["name"], Tip = it.ContainsKey("tip") ? (string)it["tip"] : "", Kind = it.ContainsKey("f") ? (string)it["f"] : it.ContainsKey("ic") ? (string)it["ic"] : "" };
                }
            }
            else if (kind == "lines")
            {
                foreach (var it in o.List("items").OfType<Dictionary<string, object>>())
                {
                    var pts = (List<object>)it["pts"];
                    for (int i = 0; i + 1 < pts.Count; i += 2)
                    {
                        WorldMapMath.LatLngToWorld(Convert.ToDouble(pts[i], CultureInfo.InvariantCulture), Convert.ToDouble(pts[i + 1], CultureInfo.InvariantCulture), out var x, out var y);
                        yield return new AuditPoint { Layer = layer, Map = mapId, X = x, Y = y, Name = (string)it["name"] };
                    }
                }
            }
        }

        void InstancesAudit(MapIndex idx, CheckFn Check, List<string> notes)
        {
            var classicIdx = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ElansAddonHub", "atlas", "classic", "index.bin");
            ClassicIndex ci = null; try { if (File.Exists(classicIdx)) ci = ClassicIndex.Load(classicIdx); } catch (Exception) { }
            if (ci != null) ClassicAtlas.Use(ci);
            var savedProbe = MapIndex.AreaAt;
            try
            {
                var layers = new[] { "zones", "fm", "routes", "instances", "nodes", "vendors", "trainers", "quests", "rares", "seen", "finds" };
                int total = 0, outside = 0, atOrigin = 0, noMap = 0, zoneDiff = 0, wrongCont = 0, noArea = 0; var worst = new List<string>();
                var perLayer = new List<string>();
                foreach (int mapId in new[] { 0, 1 })
                {
                    foreach (var layer in layers)
                    {
                        var r = WorldMapLayers.Build(layer, mapId);
                        var pts = AuditPoints(layer, mapId, r).ToList();
                        int n = pts.Count; total += n;
                        int lOut = 0, lOrigin = 0, lNoMap = 0, lDiff = 0, lCont = 0, lNoArea = 0;
                        foreach (var p in pts)
                        {
                            if (!WorldMapMath.InWorld(p.X, p.Y)) { lOut++; continue; }
                            if (Math.Abs(p.X) < 1 && Math.Abs(p.Y) < 1) { lOrigin++; if (worst.Count < 8) worst.Add(layer + " at 0,0: " + p.Name); continue; }
                            MapIndex.AreaAt = null; var rect = idx.WorldToMap(mapId, p.X, p.Y);
                            MapIndex.AreaAt = AreaProbe.AreaAt; var real = idx.WorldToMap(mapId, p.X, p.Y);
                            if (real == null) { lNoMap++; if (worst.Count < 8 && layer != "routes") worst.Add(layer + " outside every zone map: " + p.Name + " (" + (int)p.X + ", " + (int)p.Y + ")"); continue; }
                            if (rect != null && rect.Value.UiMapId != real.Value.UiMapId) lDiff++;
                            if (AreaProbe.AreaAt(mapId, p.X, p.Y) == 0) lNoArea++;
                            var crumb = idx.Breadcrumb(real.Value.UiMapId).Select(c => c.Name).ToList();
                            if (crumb.Count > 0 && ((mapId == 1 && crumb.Any(c => c == "Eastern Kingdoms")) || (mapId == 0 && crumb.Any(c => c == "Kalimdor")))) { lCont++; if (worst.Count < 8) worst.Add(layer + " on the wrong continent: " + p.Name); }
                        }
                        outside += lOut; atOrigin += lOrigin; noMap += lNoMap; zoneDiff += lDiff; wrongCont += lCont; noArea += lNoArea;
                        if (n > 0) perLayer.Add("map " + mapId + " " + layer + " " + n + (lOut + lOrigin + lNoMap + lCont > 0 ? " (outside " + lOut + ", 0/0 " + lOrigin + ", no zone " + lNoMap + ", wrong continent " + lCont + ")" : "") + (lDiff > 0 ? " zone fixed by terrain " + lDiff : ""));
                    }
                }
                AreaProbe.Flush();
                notes.Add("note marker audit (real files" + (ci != null ? ", Classic baseline on" : ", no Classic baseline here") + "): " + total + " points over both continents - " + string.Join("; ", perLayer));
                notes.Add("note marker audit: outside the world " + outside + ", at 0 / 0 " + atOrigin + ", on no zone map " + noMap + ", on the wrong continent " + wrongCont + ", with no terrain area (the rectangles answered) " + noArea + ", zone changed by the terrain's area " + zoneDiff + (worst.Count > 0 ? "; samples: " + string.Join(" | ", worst) : ""));
                Check("marker audit: across every layer of both continents (" + total + " points) nothing is outside the world, nothing sits at 0 / 0, nothing is on the other continent, and at most a handful lie on no zone map (" + noMap + ")",
                    total > 500 && outside == 0 && atOrigin == 0 && wrongCont == 0 && noMap <= Math.Max(20, total / 50), "outside " + outside + " origin " + atOrigin + " cont " + wrongCont + " nomap " + noMap + " " + string.Join(" | ", worst));

                // ---- the flight masters, against what is known
                var fmItems = new List<Dictionary<string, object>>();
                foreach (int mapId in new[] { 0, 1 }) fmItems.AddRange((Json.Obj(WorldMapLayers.Build("fm", mapId).Json)?.List("items") ?? new List<object>()).OfType<Dictionary<string, object>>());
                string F(string namePrefix) => string.Join("", fmItems.Where(i => ((string)i["name"]).StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase)).Select(i => (string)i["f"]).OrderBy(x => x));
                var alliance = new[] { "Stormwind", "Ironforge", "Sentinel Hill", "Lakeshire", "Rut'theran", "Thelsamar", "Menethil", "Auberdine", "Darkshire" };
                var horde = new[] { "Orgrimmar", "Thunder Bluff", "Crossroads", "Undercity", "Kargath", "Hammerfall", "Brackenwall", "Camp Taurajo" };
                var both = new[] { "Booty Bay", "Gadgetzan", "Everlook" };
                var badA = alliance.Where(a => F(a) != "a").ToList(); var badH = horde.Where(h => F(h) != "h").ToList();
                var split = both.Where(b => !(F(b).Contains("a") && F(b).Contains("h"))).ToList();
                var ratchet = fmItems.First(i => ((string)i["name"]).StartsWith("Ratchet"));
                var north = fmItems.FirstOrDefault(i => ((string)i["name"]).StartsWith("Northshire Abbey"));
                Check("flight masters, verified on the real TaxiNodes: Stormwind, Ironforge, Sentinel Hill, Lakeshire, Rut'theran, Thelsamar, Menethil, Auberdine, Darkshire are Alliance; Orgrimmar, Thunder Bluff, the Crossroads, the Undercity, Kargath, Hammerfall, Brackenwall, Camp Taurajo are Horde; Ratchet (flags 1027) is neutral; Booty Bay, Gadgetzan and Everlook are listed as an Alliance node and a Horde node each",
                    badA.Count == 0 && badH.Count == 0 && (string)ratchet["f"] == "n" && split.Count == 0, "alliance wrong: " + string.Join(",", badA) + " horde wrong: " + string.Join(",", badH) + " split: " + string.Join(",", split));
                Check("flight masters: Northshire Abbey carries only the developers' placeholder mount (308, also on the test island), which names no faction, so it is neutral - not Horde - and its hover text says Elwynn Forest, not Stormwind City",
                    north != null && (string)north["f"] == "n" && ((string)north["extra"]).StartsWith("Both factions · Elwynn Forest") && ((string)north["g"]) == "fm.neutral", north == null ? "missing" : (string)north["f"] + " " + (string)north["extra"]);
                int nodes = fmItems.Count;
                notes.Add("note flight masters: " + nodes + " on the two continents - " + fmItems.Count(i => (string)i["f"] == "a") + " Alliance, " + fmItems.Count(i => (string)i["f"] == "h") + " Horde, " + fmItems.Count(i => (string)i["f"] == "n") + " neutral");
            }
            finally { MapIndex.AreaAt = savedProbe; if (ci != null) ClassicAtlas.Use(null); }
        }
    }
}
