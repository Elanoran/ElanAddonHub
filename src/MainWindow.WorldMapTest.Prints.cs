using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Maps;
using ElansAddonHub.Services.WorldMap;

namespace ElansAddonHub
{
    // --selftest "worldmap" (2.90): the paw-print trail. The app side (how you moved per trail point, the odometer, the foot by race, the "me" message,
    // the layer row) without a page; then the flat page (the layout of made-up trails: spacing, left / right, facing the way you went, one kind per
    // mode, the hunter's pet line, the zoom threshold; then a real trail: the class colour, fading, the row switch, animations on / off) and the 3D
    // page (prints on the ground at its height and tilted to the slope, flight dots in the air, the far camera keeps the line). Pictures with
    // ELANSHUB_SHOTS=1: worldmap-prints-*.png.
    public partial class MainWindow
    {
        static readonly CultureInfo PI = CultureInfo.InvariantCulture;
        async Task<object> WpJs(string js) { var r = await WmEval(js); var s = Json.Parse(r ?? "null"); return s is string str ? Json.Parse(str) : s; }
        static double PD(Dictionary<string, object> d, string k) => d != null && d.TryGetValue(k, out var v) && v != null ? Convert.ToDouble(v, PI) : double.NaN;
        static List<Dictionary<string, object>> PObjs(object o) => (o as List<object>)?.OfType<Dictionary<string, object>>().ToList() ?? new List<Dictionary<string, object>>();
        static string PTxt(Dictionary<string, object> d) => d == null ? "null" : string.Join(" ", d.Where(kv => kv.Key != "list").Select(kv => kv.Key + "=" + (kv.Value is Dictionary<string, object> c ? "{" + string.Join(",", c.Select(x => x.Key + ":" + x.Value)) + "}" : kv.Value is List<object> l ? "[" + string.Join(",", l) + "]" : kv.Value)));

        static GamePresence.Character PawChar(double fx, double fy, int move = 0, bool v6 = true, bool dead = false, string race = null, string cls = "HUNTER")
        {
            var c = LiveChar(fx: fx, fy: fy); c.ClassFile = cls; c.RaceFile = race;
            c.PosMove = move; c.PosMoveKnown = true; c.PosMountKnown = v6; if (dead) c.Flags |= 2;
            return c;
        }

        // ------------------------------------------------------------------ the app side (no page)
        void WorldMapPrintsUnitTest(CheckFn Check)
        {
            // how you moved, per trail point
            var v5 = PawChar(0.5, 0.5, LiveMove.Taxi | LiveMove.Mounted | LiveMove.PetOut, v6: false);
            var v6 = PawChar(0.5, 0.5, LiveMove.Mounted | LiveMove.PetOut);
            var dead = PawChar(0.5, 0.5, 0, dead: true);
            var old = LiveChar(); old.PosMove = LiveMove.Swimming; old.PosMoveKnown = false;
            Check("prints: a trail point keeps how you moved - from a 1.17 Hub mounted + pet out (32 + 64), from a 1.16.1 Hub only flight / indoors / swimming (its high bits masked off), dead / ghost from the presence flags (128), nothing from an older Hub",
                TrailPrints.MoveOf(v6) == 96 && TrailPrints.MoveOf(v5) == LiveMove.Taxi && TrailPrints.MoveOf(dead) == LiveMove.Dead && TrailPrints.MoveOf(old) == 0 && TrailPrints.MoveOf(null) == 0,
                TrailPrints.MoveOf(v6) + " " + TrailPrints.MoveOf(v5) + " " + TrailPrints.MoveOf(dead) + " " + TrailPrints.MoveOf(old));
            // the odometer: yards along the trail, a jump or another map adds nothing, and the values stay when old points drop off
            var t = new LiveTrail(); var t0 = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
            t.Add("R-Elan", new LiveTrail.Point { At = t0, MapId = 1, X = 0, Y = 0 });
            t.Add("R-Elan", new LiveTrail.Point { At = t0.AddSeconds(1), MapId = 1, X = 6, Y = 8 });          // 10 yards
            t.Add("R-Elan", new LiveTrail.Point { At = t0.AddSeconds(2), MapId = 1, X = 6, Y = 13 });         // 5
            t.Add("R-Elan", new LiveTrail.Point { At = t0.AddSeconds(3), MapId = 1, X = 6, Y = 913 });        // a 900 yard jump (a hearthstone)
            t.Add("R-Elan", new LiveTrail.Point { At = t0.AddSeconds(4), MapId = 0, X = 50, Y = 50 });        // another continent
            t.Add("R-Elan", new LiveTrail.Point { At = t0.AddSeconds(5), MapId = 0, X = 50, Y = 54 });        // 4
            var od = t.Points(t0.AddSeconds(5)).Select(p => p.Odo).ToList();
            Check("prints: the odometer counts the yards walked (0, 10, 15), a jump of 900 yards and another map add nothing (15, 15), then on (19)",
                od.Count == 6 && od[0] == 0 && Math.Abs(od[1] - 10) < 1e-9 && Math.Abs(od[2] - 15) < 1e-9 && od[3] == 15 && od[4] == 15 && Math.Abs(od[5] - 19) < 1e-9, string.Join(",", od));
            var later = t.Points(t0.AddSeconds(602.5)).Select(p => p.Odo).ToList();
            Check("prints: the odometer values stay when the oldest points drop off (so the prints do not jump)", later.Count == 3 && later.SequenceEqual(od.Skip(3)), string.Join(",", later));
            // the kind of foot
            Check("prints: hooves for tauren, bony prints for the undead, tiny prints for gnomes, paws for everyone else",
                TrailPrints.FootOf("Tauren") == "hoof" && TrailPrints.FootOf("Scourge") == "bone" && TrailPrints.FootOf("Gnome") == "tiny" && TrailPrints.FootOf("Orc") == "paw" && TrailPrints.FootOf("NightElf") == "paw" && TrailPrints.FootOf(null) == "paw");
            // through LivePosition: the points carry the bits, the owner says race / hunter / whether the pet is known
            var lp = new LivePosition { ToWorld = (m, fx, fy) => new WorldPoint { MapId = 1, X = fx * 1000, Y = fy * 1000 }, ElevationWorld = new FixtureWorld { G = (x, y) => double.NaN } };
            lp.Update(true, true, PawChar(0.50, 0.5, 0, race: "Tauren"), t0);
            lp.Update(true, true, PawChar(0.51, 0.5, LiveMove.Mounted | LiveMove.PetOut, race: "Tauren"), t0.AddSeconds(2));
            lp.Update(true, true, PawChar(0.52, 0.5, LiveMove.Swimming, race: "Tauren"), t0.AddSeconds(4));
            var lpp = lp.Trail.Points(t0.AddSeconds(4));
            Check("prints: the live trail's points keep how you moved there (on foot, mounted with the pet, swimming) and their odometer (0, 10, 20 yards)",
                lpp.Count == 3 && lpp[0].Move == 0 && lpp[1].Move == 96 && lpp[2].Move == LiveMove.Swimming && Math.Abs(lpp[1].Odo - 10) < 1e-6 && Math.Abs(lpp[2].Odo - 20) < 1e-6,
                string.Join(" ", lpp.Select(p => p.Move + "@" + p.Odo.ToString("0.0", PI))));
            var st = TrailPrints.Style(lp.Prints);
            Check("prints: the style for the page - a tauren hunter with a Hub that says if the pet is out", (string)st["foot"] == "hoof" && (bool)st["hunter"] && (bool)st["petKnown"], Json.Write(st));
            var lp5 = new LivePosition { ToWorld = lp.ToWorld, ElevationWorld = lp.ElevationWorld };
            lp5.Update(true, true, PawChar(0.5, 0.5, 0, v6: false, cls: "MAGE", race: "Gnome"), t0);
            var st5 = TrailPrints.Style(lp5.Prints);
            Check("prints: ... a gnome mage with an older Hub: tiny prints, no pet line, the pet unknown", (string)st5["foot"] == "tiny" && !(bool)st5["hunter"] && !(bool)st5["petKnown"], Json.Write(st5));
            // the me message
            var me = Json.Obj(WorldMapProtocol.Me(LiveState.Ok, "Elan", "#ABD473", lp.Here, lpp, 1, t0.AddSeconds(4), null, null, st));
            var mv = me.List("mv"); var ods = me.List("od"); var pr = me.Child("prints");
            Check("prints: the me message carries one move value and one odometer value per trail point, here's bits, and the style",
                mv != null && mv.Count == (me.List("trail").Count / 4) && mv.Select(o => Convert.ToInt32(o)).SequenceEqual(new[] { 0, 96, 16 }) && ods.Select(o => Convert.ToDouble(o, PI)).SequenceEqual(new[] { 0.0, 10.0, 20.0 })
                && Convert.ToInt32(me["hmv"]) == 16 && pr != null && pr.Str("foot") == "hoof" && pr.Bool("hunter"), Json.Write(me));
            var plain = Json.Obj(WorldMapProtocol.Me(LiveState.Ok, "Elan", "#ABD473", lp.Here, lpp, 1, t0.AddSeconds(4)));
            Check("prints: without a style the message is the old shape (no mv / od / prints)", !plain.ContainsKey("mv") && !plain.ContainsKey("od") && !plain.ContainsKey("prints"));
            // the layer row
            var cat = WorldMapLayers.Catalog(); var row = cat.FirstOrDefault(d => (string)d["id"] == "trailprints");
            Check("prints: the layer list has \"Paw prints\" (drawn from the live position, on by default, switched on once for a saved list from before 2.90)",
                row != null && (string)row["title"] == "Paw prints" && row.ContainsKey("live") && WorldMapLayers.DefaultOn.Contains("trailprints") && WorldMapIds.IsLayerId("trailprints") && WorldMapLayers.LayersVersion == 6
                && WorldMapLayers.WithNewDefaults(new List<string> { "zones" }, 5).Contains("trailprints") && !WorldMapLayers.WithNewDefaults(new List<string> { "zones" }, 6).Contains("trailprints")
                && WorldMapLayers.Build("trailprints", 0).Note == "Drawn from your live position.", row == null ? "no row" : Json.Write(row));
        }

        // a walk on the invented continent: `secs` points, one a second, `speed` yards a second, a gentle curve, with the move bits from `move(i)`
        (double fx, double fy) PawWalk(LivePosition lp, DateTime start, double fx, double fy, int secs, double speed, Func<int, GamePresence.Character> who, double heading = 0, double bend = 0.04, double dt = 1)
        {
            WorldPoint? W(double x, double y) { try { return MapData.Index?.MapToWorld(11, x, y); } catch (Exception) { return null; } }
            var o = W(0.5, 0.5); var ax = W(0.51, 0.5); var ay = W(0.5, 0.51);
            double yx = o == null || ax == null ? 3000 : LiveTrail.Dist(o.Value.X, o.Value.Y, ax.Value.X, ax.Value.Y) / 0.01, yy = o == null || ay == null ? 3000 : LiveTrail.Dist(o.Value.X, o.Value.Y, ay.Value.X, ay.Value.Y) / 0.01;
            for (int i = 0; i < secs; i++)
            {
                var c = who(i); c.PosFx = fx; c.PosFy = fy;
                lp.Update(true, true, c, start.AddSeconds(i * dt));
                double h = heading + Math.Sin(i * 0.12) * bend * 6;
                fx += Math.Cos(h) * speed / yx; fy += Math.Sin(h) * speed / yy;
            }
            return (fx, fy);
        }

        // ------------------------------------------------------------------ the flat page
        async Task WorldMapPrints2dTest(CheckFn Check, Func<string, string> P)
        {
            bool savedOn = settings.WorldMapLivePos, savedFollow = settings.WorldMapFollow;
            var lp = LivePos; var view = WorldMapView;
            try
            {
                lp.ToWorld = null; view.Live = lp; settings.WorldMapLivePos = true; settings.WorldMapFollow = false;
                bool have = (await WmEval("typeof window.WmPrints")) == "\"object\"";
                Check("prints 2D: the page loads trailprints.js (its own file, next to icons.js)", have);
                if (!have) return;
                // ---- the layout of made-up trails (planar yards: x east, y south)
                string Pts(IEnumerable<(double x, double y, double od, int bits)> pts) => "[" + string.Join(",", pts.Select(p => string.Format(PI, "{{x:{0},y:{1},od:{2},bits:{3}}}", p.x, p.y, p.od, p.bits))) + "]";
                async Task<List<Dictionary<string, object>>> Lay(string pts, double s, string style) => PObjs(await WpJs("window.WmPrints.layoutForTest(" + pts + "," + s.ToString(PI) + "," + style + ")")).OrderBy(d => PD(d, "od")).ToList();
                var east = Enumerable.Range(0, 11).Select(i => (i * 10.0, 0.0, i * 10.0, 0));
                var walk = await Lay(Pts(east), 5, "{foot:'paw'}");
                var gaps = walk.Zip(walk.Skip(1), (a, b) => PD(b, "od") - PD(a, "od")).ToList();
                bool alt = walk.Zip(walk.Skip(1), (a, b) => PD(a, "side") == -PD(b, "side")).All(x => x);
                Check("prints 2D layout: walking 100 yards east with a 5 yard step puts 20 prints, one every 5 yards of the way (fixed odometer marks 5, 10 ... 100)",
                    walk.Count == 20 && gaps.All(g => Math.Abs(g - 5) < 1e-6) && Math.Abs(PD(walk[0], "od") - 5) < 1e-6 && Math.Abs(PD(walk.Last(), "od") - 100) < 1e-6, walk.Count + " " + string.Join(",", gaps));
                Check("prints 2D layout: they alternate left / right (one yard either side of the way: south = +y, north = -y), all paws",
                    alt && walk.All(d => Math.Abs(Math.Abs(PD(d, "y")) - 1) < 1e-6 && Math.Sign(PD(d, "y")) == (int)PD(d, "side")) && walk.All(d => d.Str("k") == "paw"), string.Join(" ", walk.Take(4).Select(d => PD(d, "y") + "/" + PD(d, "side"))));
                Check("prints 2D layout: each faces the way you went (east = 90 degrees clockwise from north)", walk.All(d => Math.Abs(PD(d, "a") - Math.PI / 2) < 1e-3), walk[0]["a"] + "");
                var north = await Lay(Pts(new[] { (0.0, 0.0, 0.0, 0), (0.0, -30.0, 30.0, 0), (30.0, -30.0, 60.0, 0) }), 5, "{foot:'paw'}");
                Check("prints 2D layout: around a corner (north, then east) the prints turn with the way: 0 degrees, then 90",
                    north.Count == 12 && north.Take(6).All(d => Math.Abs(PD(d, "a")) < 1e-3) && north.Skip(6).All(d => Math.Abs(PD(d, "a") - Math.PI / 2) < 1e-3), string.Join(",", north.Select(d => d["a"])));
                // one kind per mode, per segment: on foot 0-40, mounted 40-80, swimming 80-110, a flight 110-170, a ghost 170-200
                var mixed = new List<(double, double, double, int)> { (0, 0, 0, 0), (40, 0, 40, 0), (80, 0, 80, LiveMove.Mounted), (110, 0, 110, LiveMove.Swimming), (170, 0, 170, LiveMove.Taxi), (200, 0, 200, LiveMove.Dead) };
                var mx = await Lay(Pts(mixed), 5, "{foot:'paw'}");
                List<Dictionary<string, object>> In(double a, double b) => mx.Where(d => PD(d, "od") > a + 1e-6 && PD(d, "od") <= b + 1e-6).ToList();
                var wk = In(0, 40); var mt = In(40, 80); var sw = In(80, 110); var fl = In(110, 170); var gh = In(170, 200);
                Check("prints 2D layout: the mode switches per segment - paws on foot (8, every 5 yards), hoofprints mounted (4, every 10, bigger), ripples swimming (4, every 7.5), dots on the flight (16, every 3.75) with a feather every tenth, wisps as a ghost (4, faint)",
                    wk.Count == 8 && wk.All(d => d.Str("k") == "paw") && mt.Count == 4 && mt.All(d => d.Str("k") == "shoe" && PD(d, "sz") > PD(wk[0], "sz")) && sw.Count == 4 && sw.All(d => d.Str("k") == "ripple")
                    && fl.Count == 16 && fl.Count(d => d.Str("k") == "feather") == 2 && fl.Count(d => d.Str("k") == "dot") == 14 && gh.Count == 4 && gh.All(d => d.Str("k") == "wisp" && d.Bool("ghost") && PD(d, "al") < 0.6),
                    string.Join(" ", new[] { wk, mt, sw, fl, gh }.Select(l => l.Count + ":" + string.Join("/", l.Select(d => d.Str("k")).Distinct()))));
                Check("prints 2D layout: hoofprints mounted are twice as far apart as paws (10 yards)", mt.Zip(mt.Skip(1), (a, b) => PD(b, "od") - PD(a, "od")).All(g => Math.Abs(g - 10) < 1e-6));
                // the hunter's pet line: beside you on your right, on foot and mounted only; with a Hub that tells, only while the pet is out
                var hu = await Lay(Pts(mixed), 5, "{foot:'paw',hunter:true,petKnown:false}");
                var pets = hu.Where(d => d.Bool("pet")).ToList();
                Check("prints 2D layout: a hunter gets a second, smaller line of pet paws about 3 yards to the right (south when walking east), on foot and mounted only (an older Hub can't say if the pet is out: always)",
                    pets.Count > 0 && pets.All(d => d.Str("k") == "paw" && PD(d, "y") > 2.5 && PD(d, "y") < 4.2 && PD(d, "sz") < PD(wk[0], "sz") && (d.Str("mode") == "walk" || d.Str("mode") == "mount")) && pets.Any(d => d.Str("mode") == "mount")
                    && hu.Count(d => !d.Bool("pet")) == mx.Count, pets.Count + " " + string.Join(",", pets.Take(3).Select(d => d["y"])));
                var noPet = await Lay(Pts(mixed), 5, "{foot:'paw',hunter:true,petKnown:true}");
                var petOut = await Lay(Pts(mixed.Select(p => (p.Item1, p.Item2, p.Item3, p.Item4 | LiveMove.PetOut))), 5, "{foot:'paw',hunter:true,petKnown:true}");
                var mage = await Lay(Pts(mixed), 5, "{foot:'paw',hunter:false}");
                Check("prints 2D layout: with a Hub that tells, the pet line is only there while the pet is out; never for other classes",
                    noPet.Count(d => d.Bool("pet")) == 0 && petOut.Count(d => d.Bool("pet")) == pets.Count && mage.Count(d => d.Bool("pet")) == 0, noPet.Count(d => d.Bool("pet")) + " " + petOut.Count(d => d.Bool("pet")));
                var hoof = await Lay(Pts(east), 5, "{foot:'hoof'}"); var bone = await Lay(Pts(east), 5, "{foot:'bone'}"); var tiny = await Lay(Pts(east), 5, "{foot:'tiny'}");
                Check("prints 2D layout: the race's foot - cloven hooves (tauren), bony feet (undead), tiny paws a little closer together (gnomes: 28 in 100 yards, smaller)",
                    hoof.All(d => d.Str("k") == "hoof") && bone.All(d => d.Str("k") == "bone") && tiny.All(d => d.Str("k") == "paw") && tiny.Count == 28 && PD(tiny[0], "sz") < PD(walk[0], "sz"), hoof.Count + "/" + bone.Count + "/" + tiny.Count);
                // stable: old points dropping off do not move the rest
                var cut = await Lay(Pts(east.Skip(3)), 5, "{foot:'paw'}");
                Check("prints 2D layout: when the oldest points drop off, the remaining prints stay exactly where they were",
                    cut.Count == 14 && cut.All(c => walk.Any(w => Math.Abs(PD(w, "x") - PD(c, "x")) < 1e-6 && Math.Abs(PD(w, "y") - PD(c, "y")) < 1e-6 && PD(w, "side") == PD(c, "side"))), cut.Count + "");
                var brk = await Lay("[{x:0,y:0,od:0},{x:20,y:0,od:20},{x:500,y:0,od:20,brk:1},{x:520,y:0,od:40}]", 5, "{foot:'paw'}");
                Check("prints 2D layout: no prints across a break in the trail (a teleport)", brk.Count == 8 && brk.All(d => PD(d, "x") <= 20 || PD(d, "x") >= 500), brk.Count + "");
                var big = await Lay("[" + string.Join(",", Enumerable.Range(0, 600).Select(i => string.Format(PI, "{{x:{0},y:0,od:{0}}}", i * 30))) + "]", 2.5, "{foot:'paw',hunter:true}");
                Check("prints 2D layout: at most 5000 prints (a long ride zoomed far in), the newest kept", big.Count == 5000 && Math.Abs(big.Max(d => PD(d, "od")) - 599 * 30) < 1e-6, big.Count + "");
                // the zoom: a print every 16-32 pixels, steps of 2.5 / 5 / 10 / 20 yards; zoomed out further the trail stays a clean line
                var zs = new List<double>(); foreach (var z in new[] { 9.0, 8.0, 7.0, 6.75, 6.5, 5.0 }) zs.Add(Convert.ToDouble(await WpJs("window.WmPrints.zoomForTest(" + z.ToString(PI) + ")"), PI));
                Check("prints 2D: the step follows the zoom (zoom 9: 5 yards, 8: 10, 7: 20, 6.75: 20) and below about zoom 6.7 there are no prints, only the line",
                    zs.SequenceEqual(new[] { 5.0, 10, 20, 20, 0, 0 }), string.Join(",", zs));
                var fades = new List<double>(); foreach (var a in new[] { 0, 300, 600, 900 }) fades.Add(Convert.ToDouble(await WpJs("window.WmPrints.fadeForTest(" + a + ")"), PI));
                Check("prints 2D: they fade like the trail line (0.9 new, 0.45 after 5 minutes, 0.12 at 10)", Math.Abs(fades[0] - 0.9) < 1e-9 && Math.Abs(fades[1] - 0.45) < 1e-9 && Math.Abs(fades[2] - 0.12) < 1e-9 && Math.Abs(fades[3] - 0.12) < 1e-9, string.Join(",", fades));

                // ---- a real trail through the app: a hunter walking for 50 s (an older Hub: the pet line always)
                await WpJs("window.WmPrints.setAnimForTest(false)");
                lp.Update(false, true, LiveChar(), DateTime.UtcNow); lp.Trail.Clear();
                var t0 = DateTime.UtcNow.AddSeconds(-60);
                var end = PawWalk(lp, t0, 0.46, 0.5, 50, 7, i => PawChar(0, 0, 0, v6: false));
                view.PushLive(true);
                await WmMeWait(m => m.Str("state") == "ok" && m.Int("points") >= 40);
                async Task<Dictionary<string, object>> S2(bool list = false) => await WpJs("window.WmPrints.state2d(" + (list ? "true" : "") + ")") as Dictionary<string, object>;
                async Task Goto(double z) { var h = lp.Here.Value; WorldMapMath.WorldToLatLng(h.X, h.Y, out var la, out var ln); await WmEval(string.Format(PI, "window.wm.feed({{t:'goto',lat:{0},lng:{1},z:{2}}})", la, ln, z)); await Task.Delay(350); }
                async Task Shot(string name) { if (!SelfTestClock.ShotsOn) return; await Task.Delay(300); var png = await view.CaptureForTest(); if (png != null) File.WriteAllBytes(P(name), png); }
                await Goto(9);
                var s9 = await S2();
                Check("prints 2D: zoomed in (zoom 9) your walk is drawn as paw prints (a 5 yard step) with the pet's paws beside them, on the canvas between the line and the marks (pane 450), the line a faint guide",
                    s9 != null && s9.Bool("on") && PD(s9, "s") == 5 && s9.Int("n") > 40 && s9.Int("painted") > 20 && s9.Child("kinds")?.Int("paw") > 0 && s9.Int("pets") > 0 && PD(s9, "lineK") < 0.5 && s9.Int("paneZ") == 450, PTxt(s9));
                await Shot("worldmap-prints-walk-z9.png");
                await Goto(8); await Shot("worldmap-prints-walk-z8.png");
                var s8 = await S2();
                Check("prints 2D: one zoom step out the step doubles (10 yards) - about the same spacing on screen", s8 != null && PD(s8, "s") == 10 && s8.Int("n") > 15 && s8.Int("n") < s9.Int("n"), PTxt(s8));
                await Goto(6); await Shot("worldmap-prints-walk-z6-line.png");
                var s6 = await S2(); var me6 = await WmMe();
                Check("prints 2D: zoomed out (zoom 6) no prints, the trail is the clean line again (full opacity, with its casing)", s6 != null && s6.Int("n") == 0 && s6.Int("painted") == 0 && PD(s6, "lineK") == 1 && me6 != null && me6.Int("lines") >= 2, PTxt(s6));
                await Goto(9);
                // the class colour: the newest print is painted in the hunter green, at the newest opacity; an old one fainter
                var px0 = await WpJs("window.WmPrints.pixelOf2d(0)") as Dictionary<string, object>;
                var rgba = px0?.List("rgba")?.Select(o => Convert.ToDouble(o, PI)).ToList();
                bool green = rgba != null && rgba.Count == 4 && Math.Abs(rgba[0] - 0xAB) <= 8 && Math.Abs(rgba[1] - 0xD4) <= 8 && Math.Abs(rgba[2] - 0x73) <= 8 && rgba[3] > 150;
                Check("prints 2D: the prints are in your class colour, the same as your dot (#ABD473 for a hunter)", green && (await S2()).Str("color") == "#ABD473", px0 == null ? "null" : Json.Write(px0));
                var sl = await S2(true); var lst = PObjs(sl?["list"]);
                // the oldest paw that is on screen
                Dictionary<string, object> pxO = null;
                foreach (var d in lst.Select((d, i) => (d, i)).Where(x => !x.d.Bool("pet")).OrderByDescending(x => PD(x.d, "age")))
                {
                    var q = await WpJs("window.WmPrints.pixelOf2d(" + d.i + ")") as Dictionary<string, object>;
                    if (q != null && PD(q, "x") > 20 && PD(q, "y") > 20 && PD(q, "x") < 400 && PD(q, "y") < 400) { pxO = q; break; }
                }
                var aOld = pxO?.List("rgba") is List<object> lo && lo.Count == 4 ? Convert.ToDouble(lo[3], PI) : double.NaN;
                Check("prints 2D: older prints are fainter (the oldest, " + (pxO == null ? "?" : PD(pxO, "age").ToString("0", PI)) + " s old, at alpha " + aOld + " against " + (rgba == null ? "?" : rgba[3].ToString(PI)) + ")",
                    !double.IsNaN(aOld) && rgba != null && aOld < rgba[3] - 3 && aOld > 0, pxO == null ? "null" : Json.Write(pxO));
                // fading over the 10 minutes: a walk with a point every 20 seconds (the oldest almost 10 minutes ago) - the oldest print on screen is far fainter
                lp.Trail.Clear();
                var endF = PawWalk(lp, DateTime.UtcNow.AddSeconds(-590), 0.46, 0.5, 30, 7, i => PawChar(0, 0, 0, v6: false), dt: 20);
                view.PushLive(true); await WmMeWait(m => m.Int("points") >= 28); await Goto(9);
                var lf = PObjs((await S2(true))?["list"]); Dictionary<string, object> pxNew = null, pxOld = null;
                foreach (var d in lf.Select((d, i) => (d, i)).Where(x => !x.d.Bool("pet")).OrderBy(x => PD(x.d, "age")))
                {
                    var q = await WpJs("window.WmPrints.pixelOf2d(" + d.i + ")") as Dictionary<string, object>;
                    if (q == null || !(PD(q, "x") > 10 && PD(q, "y") > 10 && PD(q, "x") < 410 && PD(q, "y") < 450)) continue;
                    if (pxNew == null) pxNew = q; pxOld = q;
                }
                double AOf(Dictionary<string, object> q) => q?.List("rgba") is List<object> l && l.Count == 4 ? Convert.ToDouble(l[3], PI) : double.NaN;
                Check("prints 2D: a print from minutes ago is much fainter than a new one (" + (pxOld == null ? "?" : PD(pxOld, "age").ToString("0", PI)) + " s old: alpha " + AOf(pxOld) + "; " + (pxNew == null ? "?" : PD(pxNew, "age").ToString("0", PI)) + " s old: " + AOf(pxNew) + ")",
                    pxOld != null && pxNew != null && PD(pxOld, "age") > 150 && AOf(pxOld) < AOf(pxNew) * 0.8 && AOf(pxOld) > 10, (pxOld == null ? "null" : Json.Write(pxOld)) + " | " + (pxNew == null ? "null" : Json.Write(pxNew)));
                await Shot("worldmap-prints-fading.png");
                lp.Trail.Clear();
                end = PawWalk(lp, DateTime.UtcNow.AddSeconds(-51), 0.46, 0.5, 50, 7, i => PawChar(0, 0, 0, v6: false));
                view.PushLive(true); await WmMeWait(m => m.Int("points") >= 40); await Goto(9);
                // the row switch
                bool rowShown = (await WmEval("(function(){var r=document.querySelectorAll('.wm-row');for(var i=0;i<r.length;i++){if((r[i].textContent||'').indexOf('Paw prints')>=0)return r[i].style.display!=='none'&&r[i].offsetParent!==null;}return false})()")) == "true";
                await WmEval("window.wm.setOn('trailprints', false)"); await Task.Delay(300);
                var off = await S2(); var meOff = await WmMe();
                await WmEval("window.wm.setOn('trailprints', true)"); await Task.Delay(300);
                var back = await S2();
                Check("prints 2D: the \"Paw prints\" row is in the panel while the live layer is; switched off the trail is the plain line at any zoom, on again the prints are back",
                    rowShown && off != null && off.Int("n") == 0 && off.Int("painted") == 0 && PD(off, "lineK") == 1 && meOff.Int("lines") >= 2 && back != null && back.Int("n") > 40, PTxt(off) + " | " + PTxt(back));
                // animations: on = new prints fade in (a few frames), off = static, drawn at once
                await WpJs("window.WmPrints.setAnimForTest(true)");
                double fr0 = PD(await S2(), "frames");
                end = PawWalk(lp, DateTime.UtcNow.AddSeconds(-2), end.fx, end.fy, 2, 7, i => PawChar(0, 0, 0, v6: false));
                view.PushLive(true); await Task.Delay(900);
                double fr1 = PD(await S2(), "frames");
                await WpJs("window.WmPrints.setAnimForTest(false)");
                end = PawWalk(lp, DateTime.UtcNow.AddSeconds(-1), end.fx, end.fy, 2, 7, i => PawChar(0, 0, 0, v6: false));
                view.PushLive(true); await Task.Delay(900);
                var sa = await S2();
                Check("prints 2D: with animations on new prints fade in (" + (fr1 - fr0) + " frames); with Windows animations off nothing moves - no frames, the prints are drawn at once",
                    fr1 - fr0 >= 3 && PD(sa, "frames") == fr1 && !sa.Bool("anim") && sa.Int("painted") > 20, fr0 + " " + fr1 + " " + PTxt(sa));
                await WpJs("window.WmPrints.setAnimForTest(null)");

                // ---- each mode through the app (a 1.17 Hub) and its picture
                async Task<Dictionary<string, object>> Mode(string name, Func<int, GamePresence.Character> who, int secs, double speed, double z = 9)
                {
                    lp.Trail.Clear();
                    PawWalk(lp, DateTime.UtcNow.AddSeconds(-secs - 1), 0.47, 0.52, secs, speed, who);
                    view.PushLive(true); await WmMeWait(m => m.Int("points") >= secs - 3); await Goto(z);
                    await Shot("worldmap-prints-" + name + ".png");
                    return await S2();
                }
                var sm = await Mode("mounted", i => PawChar(0, 0, LiveMove.Mounted | LiveMove.PetOut), 30, 14);
                Check("prints 2D: mounted (Hub 1.17 says so) - hoofprints 10 yards apart, the pet's paws trotting beside them", sm != null && sm.Child("kinds")?.Int("shoe") > 5 && sm.Child("modes")?.Int("mount") > 5 && sm.Int("pets") > 0, PTxt(sm));
                var sn = await Mode("mounted-nopet", i => PawChar(0, 0, LiveMove.Mounted), 30, 14);
                Check("prints 2D: mounted with the pet not out (a Hub that tells) - no pet line", sn != null && sn.Child("kinds")?.Int("shoe") > 5 && sn.Int("pets") == 0, PTxt(sn));
                var ss = await Mode("swim", i => PawChar(0, 0, LiveMove.Swimming), 40, 4.7);
                Check("prints 2D: swimming - little ripple rings", ss != null && ss.Child("kinds")?.Int("ripple") > 5 && ss.Int("pets") == 0, PTxt(ss));
                var sf = await Mode("flight", i => PawChar(0, 0, LiveMove.Taxi), 30, 25, 8);
                Check("prints 2D: on a flight - a light dotted line with now and then a feather", sf != null && sf.Child("kinds")?.Int("dot") > 20 && sf.Child("kinds")?.Int("feather") > 0, PTxt(sf));
                var sg = await Mode("ghost", i => PawChar(0, 0, 0, dead: true), 40, 9);
                Check("prints 2D: as a ghost - faint wisps in a paler colour", sg != null && sg.Child("kinds")?.Int("wisp") > 5 && sg.Str("ghost") != sg.Str("color"), PTxt(sg));
                var sx = await Mode("mixed", i => PawChar(0, 0, i < 15 ? 0 : i < 28 ? LiveMove.Mounted | LiveMove.PetOut : i < 40 ? LiveMove.Swimming : 0, race: "Tauren"), 52, 8);
                Check("prints 2D: one trail, the mode changing on the way (a tauren: hooves on foot, horseshoes mounted, ripples in the water, hooves again)",
                    sx != null && sx.Child("kinds")?.Int("hoof") > 5 && sx.Child("kinds")?.Int("shoe") > 3 && sx.Child("kinds")?.Int("ripple") > 3, PTxt(sx));
                var su = await Mode("undead", i => PawChar(0, 0, 0, race: "Scourge", cls: "WARLOCK"), 40, 7);
                Check("prints 2D: an undead warlock - bony prints in the warlock's colour, no pet line (not a hunter)", su != null && su.Child("kinds")?.Int("bone") > 10 && su.Int("pets") == 0 && su.Str("color") == "#9482C9", PTxt(su));
                var sgn = await Mode("gnome", i => PawChar(0, 0, 0, race: "Gnome", cls: "MAGE"), 40, 7);
                Check("prints 2D: a gnome mage - tiny prints", sgn != null && sgn.Child("kinds")?.Int("paw") > 10 && sgn.Int("pets") == 0, PTxt(sgn));
                if (SelfTestClock.ShotsOn) { await Mode("mixed-z85", i => PawChar(0, 0, i < 15 ? 0 : i < 28 ? LiveMove.Mounted | LiveMove.PetOut : i < 40 ? LiveMove.Swimming : 0), 52, 8, 8.5); }
            }
            finally
            {
                try { await WpJs("window.WmPrints && window.WmPrints.setAnimForTest(null)"); } catch (Exception) { }
                settings.WorldMapLivePos = savedOn; settings.WorldMapFollow = savedFollow;
                lp.Trail.Clear();
                try { UpdateLive(); } catch (Exception) { }
            }
        }

        // ------------------------------------------------------------------ the 3D page
        async Task WorldMapPrints3dTest(CheckFn Check, Func<string, string> P)
        {
            bool savedOn = settings.WorldMapLivePos, savedFollow = settings.WorldMapFollow;
            var lp = LivePos; var view = WorldMapView; var savedWorld = lp.ElevationWorld;
            try
            {
                lp.ToWorld = null; view.Live = lp; settings.WorldMapLivePos = true; settings.WorldMapFollow = false;
                async Task<Dictionary<string, object>> S3() => await WpJs("window.WmPrints.state3d()") as Dictionary<string, object>;
                async Task View(double dist, double tilt = 52, double az = 20) { var h = lp.Here.Value; WorldMapMath.WorldToLatLng(h.X, h.Y, out var la, out var ln); await WmEval(string.Format(PI, "window.wm3.setView({0},{1},{2},{3},{4})", la, ln, dist, az, tilt)); await Task.Delay(700); }
                async Task Shot(string name) { if (!SelfTestClock.ShotsOn) return; await Task.Delay(500); var png = await view.CaptureForTest(); if (png != null) File.WriteAllBytes(P(name), png); }
                async Task<Dictionary<string, object>> Walk(Func<int, GamePresence.Character> who, int secs, double speed, double heading = 0.6)
                {
                    lp.Trail.Clear();
                    PawWalk(lp, DateTime.UtcNow.AddSeconds(-secs - 1), 0.47, 0.47, secs, speed, who, heading);
                    view.PushLive(true); await Wm3MeWait(m => m.Int("points") >= secs - 3);
                    return null;
                }
                await WpJs("window.WmPrints.setAnimForTest(false)");
                await Walk(i => PawChar(0, 0, 0, v6: false), 50, 7);
                await View(140);
                var w3 = await S3();
                Check("prints 3D: close to you the walk is paw prints (and the pet's) as flat quads in one instanced mesh",
                    w3 != null && w3.Bool("on") && w3.Bool("visible") && w3.Bool("inScene") && w3.Int("n") > 30 && w3.Int("count") == w3.Int("n") && w3.Child("kinds")?.Int("paw") > 0 && w3.Int("pets") > 0, PTxt(w3));
                Check("prints 3D: each lies on the ground at its height (" + PD(w3, "worstY") + " yards off at worst) and is tilted to the slope under it (up vector within " + PD(w3, "worstNormalDeg") + " degrees of the ground's normal; the steepest " + PD(w3, "maxTiltDeg") + " degrees)",
                    w3 != null && PD(w3, "worstY") < 0.01 && PD(w3, "worstNormalDeg") < 4 && w3.Int("ground") == w3.Int("n"), PTxt(w3));
                var col = w3?.List("color")?.Select(o => Convert.ToInt32(o)).ToList();
                Check("prints 3D: in the class colour", col != null && col.Count == 3 && col[0] == 0xAB && col[1] == 0xD4 && col[2] == 0x73, col == null ? "null" : string.Join(",", col));
                await Shot("worldmap-prints-3d-walk.png");
                await View(70, 35); await Shot("worldmap-prints-3d-walk-close.png");
                await View(3500);
                var far = await S3(); var meFar = await Wm3Me();
                Check("prints 3D: from far away no prints, the trail is the ribbon as before", far != null && far.Int("n") == 0 && !far.Bool("visible") && meFar != null && meFar.Int("lines") >= 2, PTxt(far));
                await View(140);
                // a slope: the invented landscape's hills - find a stretch that is not flat and walk across it
                await Walk(i => PawChar(0, 0, LiveMove.Mounted | LiveMove.PetOut), 40, 14, 2.2);
                await View(200, 60, 200);
                var m3 = await S3();
                Check("prints 3D: mounted - hoofprints on the ground, the pet beside", m3 != null && m3.Child("kinds")?.Int("shoe") > 5 && m3.Int("pets") > 0 && PD(m3, "worstY") < 0.01 && PD(m3, "worstNormalDeg") < 4, PTxt(m3));
                await Shot("worldmap-prints-3d-mounted.png");
                await Walk(i => PawChar(0, 0, 0, dead: true, race: "Scourge", cls: "PRIEST"), 40, 9);
                await View(160);
                var g3 = await S3();
                Check("prints 3D: a ghost - faint wisps", g3 != null && g3.Child("kinds")?.Int("wisp") > 5, PTxt(g3));
                await Shot("worldmap-prints-3d-ghost.png");
                await Walk(i => PawChar(0, 0, LiveMove.Swimming), 40, 4.7);
                await View(120);
                var s3 = await S3();
                Check("prints 3D: swimming - ripple rings", s3 != null && s3.Child("kinds")?.Int("ripple") > 5, PTxt(s3));
                await Shot("worldmap-prints-3d-swim.png");
                // a flight: the dots in the air, along the flight's own height (a route 120 yards up)
                var fw = new FixtureWorld { G = (x, y) => double.NaN };
                WorldPoint? W(double x, double y) { try { return MapData.Index?.MapToWorld(11, x, y); } catch (Exception) { return null; } }
                var a = W(0.40, 0.50); var b = W(0.70, 0.50);
                if (a != null && b != null)
                {
                    var mid = new[] { (a.Value.X + b.Value.X) / 2, (a.Value.Y + b.Value.Y) / 2 };
                    fw.R.Add(Route(902, 1, 2, a.Value.X, a.Value.Y, 140, mid[0], mid[1], 260, b.Value.X, b.Value.Y, 140));
                    lp.ElevationWorld = fw; lp.Trail.Clear();
                    var st = DateTime.UtcNow.AddSeconds(-40);
                    lp.Update(true, true, PawChar(0.40, 0.50), st);
                    for (int i = 1; i <= 30; i++) lp.Update(true, true, PawChar(0.40 + i * 0.004, 0.50, LiveMove.Taxi), st.AddSeconds(i));
                    view.PushLive(true); await Wm3MeWait(m => m.Int("points") >= 25);
                    await View(240, 70);
                    var f3 = await S3();
                    Check("prints 3D: on a flight the dots and feathers are in the air along the flight's height (" + (f3?.Int("air") ?? 0) + " of " + (f3?.Int("n") ?? 0) + " over 20 yards above the ground)",
                        f3 != null && f3.Child("kinds")?.Int("dot") > 5 && f3.Int("air") >= f3.Int("n") * 0.75 && f3.Int("n") > 5 && PD(f3, "worstY") < 0.01, PTxt(f3));
                    await Shot("worldmap-prints-3d-flight.png");
                }
                else Check("prints 3D: the invented continent has a place for the test flight", false);
                lp.ElevationWorld = savedWorld;
                // the row switch
                await Walk(i => PawChar(0, 0, 0, v6: false), 30, 7);
                await View(140);
                await WmEval("window.wm3.setOnForTest('trailprints', false)"); await Task.Delay(400);
                var off = await S3();
                await WmEval("window.wm3.setOnForTest('trailprints', true)"); await Task.Delay(400);
                var on = await S3();
                Check("prints 3D: \"Paw prints\" off hides them (the ribbon stays), on again they are back", off != null && off.Int("count") == 0 && !off.Bool("visible") && on != null && on.Int("n") > 10 && on.Bool("visible"), PTxt(off) + " | " + PTxt(on));
                // animations: off = no frames at all
                double fr0 = PD(on, "frames");
                await Walk(i => PawChar(0, 0, 0, v6: false), 32, 7); await Task.Delay(700);
                var an = await S3();
                Check("prints 3D: with animations off nothing fades in (no animation frames), the prints are simply there", an != null && PD(an, "frames") == fr0 && an.Int("n") > 10 && !an.Bool("anim"), PTxt(an));
            }
            finally
            {
                try { await WpJs("window.WmPrints && window.WmPrints.setAnimForTest(null)"); } catch (Exception) { }
                lp.ElevationWorld = savedWorld;
                settings.WorldMapLivePos = savedOn; settings.WorldMapFollow = savedFollow;
                lp.Trail.Clear();
                try { UpdateLive(); } catch (Exception) { }
            }
        }
    }
}
