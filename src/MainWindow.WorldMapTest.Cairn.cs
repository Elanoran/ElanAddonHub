using System;
using Elans.Shared;
using System.Collections.Generic;
using System.Linq;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Atlas.Classic;
using ElansAddonHub.Services.WorldMap;

namespace ElansAddonHub
{
    // --selftest "worldmap" (2.92): the Cairn's numbers on the maps - its spawn points, gathering nodes and flight times on the world map's layers and on
    // the Atlas pages' maps, labelled "Cairn", after your own Lodge's (a spot the Lodge already has is not drawn twice), and nothing of it for a member who
    // does not share (CairnAtlas.Current is null then). Runs on the invented continent (Northland, Greenvale = UiMap 11) with the Classic test baseline.
    public partial class MainWindow
    {
        void WorldMapCairnMapsTest(CheckFn Check)
        {
            var savedLodge = AtlasQuery.Context.Lodge;
            try
            {
                ClassicAtlas.Use(AtlasTestClassic()); AtlasBrowse.ForgetForTest();
                long at = AtNow - 3 * 3600;
                // your Lodge: npc 1 killed (item 100) and seen at 30 / 40 % of Greenvale, a node of the chest (object 50) at the middle, vendor 77 in Greenvale
                var lj = "{\"t\":\"atlas.data\",\"epoch\":\"e1\",\"rev\":7,\"at\":" + (AtNow - 600) + ",\"full\":true,"
                    + "\"kills\":{\"1\":{\"n\":340,\"m\":3,\"items\":{\"100\":{\"n\":41,\"c\":41}},\"z\":{\"11\":340}}},"
                    + "\"vendors\":{\"77\":{\"at\":" + at + ",\"z\":11,\"x\":0.45,\"y\":0.65,\"items\":{\"102\":{\"p\":25,\"s\":20,\"l\":-1}}}},"
                    + "\"spawns\":{\"1\":[{\"z\":11,\"x\":0.3,\"y\":0.4}]},"
                    + "\"nodes\":{\"50\":{\"11\":[{\"x\":0.5,\"y\":0.5,\"n\":7}]}}}";
                var lodge = new LodgeAtlas();
                Check("cairn maps: the invented Lodge page is taken", lodge.Apply(LodgeJson.Parse(lj)));
                AtlasQuery.Context.Lodge = () => lodge;
                // the Cairn: npc 1 at the Lodge's spot (the same place) and at a new one, the innkeeper (npc 2, a Classic vendor) and vendor 77 somewhere,
                // the chest at the Lodge's spot and a new one, and the Stormwind - Orgrimmar flight
                var cj = "{\"format\":\"elans-cairn\",\"v\":1,\"day\":\"2026-10-17\",\"seq\":3,\"minLodges\":2,\"lodges\":3,\"builds\":[57689],\"drops\":{\"kill\":{\"1\":{\"n\":900,\"m\":3,\"i\":{\"100\":[90,90,3]}}}},"
                    + "\"vendors\":{\"2\":{\"m\":2,\"i\":{\"102\":[24,20,-1,0,2]}},\"77\":{\"m\":2,\"i\":{\"102\":[25,20,-1,0,2]}}},"
                    + "\"spawns\":{\"1\":[[11,0.3002,0.4003,9,2],[11,0.7,0.6,4,2]],\"2\":[[11,0.5,0.2,6,2]],\"77\":[[11,0.9,0.9,5,2]]},"
                    + "\"nodes\":{\"50\":[[11,0.5004,0.4998,12,2],[11,0.25,0.75,3,2]]},"
                    + "\"flights\":{\"Alliance|2>23\":[150.2,6,2]}}";
                var cairn = CairnSet.Parse(cj);
                CairnAtlas.ResetForTest();

                // ---- a member who does not share: no Cairn data on hand, nothing labelled Cairn anywhere
                string Tips(LayerResult r) { var o = Json.Obj(r.Json); return o == null ? "" : string.Join("\n", (o.List("tips") ?? new List<object>()).Cast<object>()) + "\n" + string.Join("\n", (o.List("items") ?? new List<object>()).OfType<Dictionary<string, object>>().Select(i => i.Str("tip"))); }
                var n0 = WorldMapLayers.Build("nodes", 0); var v0 = WorldMapLayers.Build("vendors", 0); var r0 = WorldMapLayers.Build("routes", 0);
                var npc0 = AtlasQuery.Build("npc:1"); var obj0 = AtlasQuery.Build("obj:50");
                bool NoCairnMarks(AtlasPageModel m) => m.Map == null || m.Map.Zones.SelectMany(z => z.Markers).All(mk => !(mk.Tooltip ?? "").Contains(AtlasLabel.Cairn));
                Check("cairn maps: without the Cairn's data (a member who doesn't share) the world map's nodes, vendors and flight routes and the creature / object maps show nothing labelled Cairn",
                    !Tips(n0).Contains("Cairn") && !Tips(v0).Contains("Cairn") && !Tips(r0).Contains("Cairn") && NoCairnMarks(npc0) && NoCairnMarks(obj0) && n0.Count > 0, Tips(n0) + " | " + Tips(v0));

                CairnAtlas.SetForTest(cairn);
                AtlasBrowse.ForgetForTest();
                // ---- the world map: gathering nodes
                var nl = WorldMapLayers.Build("nodes", 0); var nd = Json.Obj(nl.Json);
                var styles = nd.List("styles").OfType<Dictionary<string, object>>().ToList(); var pts = nd.List("pts").Select(o => Convert.ToDouble(o)).ToList();
                var tips = nd.List("tips").Cast<string>().ToList();
                var marks = Enumerable.Range(0, pts.Count / 6).Select(i => (tip: pts[i * 6 + 3] >= 0 ? tips[(int)pts[i * 6 + 3]] : "", st: (int)pts[i * 6 + 4], i)).ToList();
                var cm = marks.Where(mk => mk.tip.Contains("Cairn")).ToList(); var fm = marks.Where(mk => mk.tip.EndsWith("· Forever", StringComparison.Ordinal)).ToList();
                Check("cairn maps: world map nodes - the Cairn's new node (looted 3 times by 2 lodges) is drawn labelled Cairn in its group's own Cairn style, after your Lodge's node; its node at the Lodge's spot is not drawn twice",
                    cm.Count == 1 && cm[0].tip == "Looted 3 times · 2 lodges · Cairn" && fm.Count == 1 && fm[0].tip == "Looted 7 times · Forever" && cm[0].i > fm[0].i
                    && styles[cm[0].st].Str("l").EndsWith("· Cairn", StringComparison.Ordinal) && styles[cm[0].st].Str("g") == styles[fm[0].st].Str("g") && (bool)styles[cm[0].st]["f"],
                    string.Join(" | ", marks.Select(mk => mk.tip + "@" + mk.st)) + " styles " + string.Join(",", styles.Select(s => s.Str("l"))));
                // vendors
                var vl = WorldMapLayers.Build("vendors", 0); var vd = Json.Obj(vl.Json);
                var vnames = vd.List("names").Cast<string>().ToList(); var vtips = vd.List("tips").Cast<string>().ToList(); var vp = vd.List("pts").Select(o => Convert.ToDouble(o)).ToList(); var vst = vd.List("styles").OfType<Dictionary<string, object>>().ToList();
                var vm = Enumerable.Range(0, vp.Count / 6).Select(i => (name: vnames[(int)vp[i * 6 + 2]], tip: vp[i * 6 + 3] >= 0 ? vtips[(int)vp[i * 6 + 3]] : "", st: (int)vp[i * 6 + 4])).ToList();
                var inn = vm.Where(x => x.name == "Innkeeper Hale").ToList(); var v77 = vm.Where(x => x.name == AtlasQuery.NpcName(77)).ToList();
                Check("cairn maps: world map vendors - the innkeeper stands where the Cairn's Lodges saw it (labelled Cairn, style \"Vendor · Cairn\") instead of the Classic baseline's spot; vendor 77 stays where your Lodge saw it (no Cairn copy)",
                    inn.Count == 1 && inn[0].tip == "Vendor · Seen 6 times · 2 lodges · Cairn" && vst[inn[0].st].Str("l") == "Vendor · Cairn" && v77.Count == 1 && !v77[0].tip.Contains("Cairn"),
                    string.Join(" | ", vm.Select(x => x.name + ": " + x.tip + "@" + x.st)));
                // flight times
                var rl = WorldMapLayers.Build("routes", 0); var ri = Json.Obj(rl.Json).List("items").OfType<Dictionary<string, object>>().ToList();
                var sw = ri.FirstOrDefault(i => i.Str("name") == "Stormwind ↔ Orgrimmar"); var og = ri.FirstOrDefault(i => i.Str("name") == "Orgrimmar ↔ Ratchet");
                Check("cairn maps: world map flight routes - a leg only the Cairn has timed shows its time (2m 30s) with \"2 lodges · Cairn\" in the tooltip; the leg your Lodge timed keeps the Lodge's time (2m 10s) and no Cairn label; the layer's note says the order",
                    sw != null && sw.Str("label") == "2m 30s" && sw.Str("tip") == "Flight time 2m 30s · 2 lodges · Cairn" && og != null && og.Str("label") == "2m 10s" && !og.Str("tip").Contains("Cairn") && rl.Note.Contains("Cairn"),
                    sw?.Str("tip") + " | " + og?.Str("tip") + " | " + rl.Note);

                // ---- the Atlas pages' maps
                List<(string tip, double x, double y)> Marks(AtlasPageModel m) => m.Map == null ? new List<(string, double, double)>() : m.Map.Zones.Where(z => z.UiMapId == 11).SelectMany(z => z.Markers).Select(mk => (mk.Tooltip ?? "", mk.Fx, mk.Fy)).ToList();
                var npc = AtlasQuery.Build("npc:1"); var nm = Marks(npc);
                int li = nm.FindIndex(x => x.tip.Contains("Seen by the Lodge")), ci = nm.FindIndex(x => x.tip.Contains("Cairn"));
                Check("cairn maps: creature page map - your Lodge's spot first, then the Cairn's new one (\"Seen 4 times · 2 lodges · Cairn\"); the Cairn's spot at the Lodge's place is not drawn twice; the page names the Cairn layer",
                    li >= 0 && ci > li && nm.Count(x => x.tip.Contains("Cairn")) == 1 && nm[ci].tip == "Seen 4 times · 2 lodges · Cairn" && Math.Abs(nm[ci].x - 0.7) < 1e-9 && npc.Layers.Contains("cairn"),
                    string.Join(" | ", nm.Select(x => x.tip)));
                var obj = AtlasQuery.Build("obj:50"); var om = Marks(obj);
                Check("cairn maps: object page map - your Lodge's node, then the Cairn's new node; its node at the Lodge's spot is left out",
                    om.Count(x => x.tip == "Looted 7 times · Forever") == 1 && om.Count(x => x.tip.Contains("Cairn")) == 1 && om.FindIndex(x => x.tip.Contains("Cairn")) > om.FindIndex(x => x.tip == "Looted 7 times · Forever"),
                    string.Join(" | ", om.Select(x => x.tip)));
                var item = AtlasQuery.Build("item:100"); var im = Marks(item);
                Check("cairn maps: item page map - the dropping creature's places: your Lodge's, then the Cairn's new one",
                    im.Any(x => x.tip.EndsWith("· Forever", StringComparison.Ordinal) && Math.Abs(x.x - 0.3) < 1e-9) && im.Count(x => x.tip.Contains("Cairn") && Math.Abs(x.x - 0.7) < 1e-9) == 1 && !im.Any(x => x.tip.Contains("Cairn") && Math.Abs(x.x - 0.3) < 0.01),
                    string.Join(" | ", im.Select(x => x.tip)));
                var zone = AtlasQuery.Build("zone:11"); var zm = Marks(zone);
                Check("cairn maps: zone page map - the gathering nodes: your Lodge's and the Cairn's new one",
                    zm.Count(x => x.tip == "Looted 7 times · Forever") == 1 && zm.Count(x => x.tip == "Looted 3 times · 2 lodges · Cairn") == 1, string.Join(" | ", zm.Select(x => x.tip)));
            }
            catch (Exception e) { Check("cairn maps test crashed", false, e.ToString()); }
            finally
            {
                CairnAtlas.ResetForTest(); ClassicAtlas.Use(null); AtlasBrowse.ForgetForTest();
                AtlasQuery.Context.Lodge = savedLodge;
            }
        }
    }
}
