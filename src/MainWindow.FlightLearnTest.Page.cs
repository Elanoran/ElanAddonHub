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
    // Learned flight paths (2.88) on the world map page: run from "worldmap" (the 2D page test and the 3D page test on the invented continent) and
    // "worldmapreal" (the real Crossroads -> Ratchet route). The pure checks are in MainWindow.FlightLearnTest.cs ("flightlearn").
    public partial class MainWindow
    {
        async Task<List<Dictionary<string, object>>> FlPageLines(string fn)
        {
            var raw = await WmEval(fn);
            var js = Json.Parse(raw) as string; var arr = js == null ? null : Json.Parse(js) as List<object>;
            return arr == null ? new List<Dictionary<string, object>>() : arr.OfType<Dictionary<string, object>>().ToList();
        }

        // two agreeing flights of the fixture's Orgrimmar > Ratchet (23 > 80) that bend 250 yards off the straight line: a store whose game routes are the page's own
        static LearnedFlights FlPageStore()
        {
            var s = new LearnedFlights(null);
            s.Add(new[] { FlOn(0, 23, 80, FlFixtureBent(250, 91), 95) }); s.Add(new[] { FlOn(0, 23, 80, FlFixtureBent(240, 92), 99) });
            return s;
        }

        async Task FlightLearnPage2d(CheckFn Check, Func<string, string> P)
        {
            var saved = WorldMapLayers.Learned;
            try
            {
                int r0 = WorldMapView.RebuildsForTest;
                var s = new LearnedFlights(null);
                WorldMapLayers.Learned = () => s;
                // adding the flights fires LearnedFlights.AnyChanged: the window rebuilds the Flight routes layer on its own (no page reload)
                s.Add(new[] { FlOn(0, 23, 80, FlFixtureBent(250, 91), 95) }); s.Add(new[] { FlOn(0, 23, 80, FlFixtureBent(240, 92), 99) });
                bool rebuilt = await WaitFor(() => WorldMapView.RebuildsForTest > r0, 5000);
                List<Dictionary<string, object>> lines = null;
                for (int i = 0; i < 40; i++) { lines = await FlPageLines("window.wm.lines('routes')"); if (lines.Any(l => l.Str("learned") == "mine")) break; await Task.Delay(150); }
                var lr = lines.FirstOrDefault(l => l.Str("learned") == "mine");
                Check("page 2D (learned flights): two agreeing flights of a leg rebuild the Flight routes layer by themselves and the page draws that leg as learned: solid (no dash), teal (" + lr?.Str("color") + "); the other routes stay dashed",
                    rebuilt && lr != null && lr.Str("dash") == "" && string.Equals(lr.Str("color"), WorldMapLayers.LearnedColor, StringComparison.OrdinalIgnoreCase) && lines.Where(l => l != lr).All(l => l.Str("dash") == "6 5"), Json.Write(lines));
                if (SelfTestClock.ShotsOn)
                {
                    // the route's middle a little left of the centre, so the layer panel on the right does not cover it; the toast of an earlier check fades first
                    await AtlasView.Open("worldmap:0?x=-50&y=620&z=5.4");
                    await WaitFor(() => WorldMapView.PageReady, 15000);
                    await Task.Delay(3500);
                    var png = await WorldMapView.CaptureForTest(); if (png != null) File.WriteAllBytes(P("worldmap-learned-route-2d.png"), png);
                    Check("page 2D (learned flights): the picture of the learned route is taken (worldmap-learned-route-2d.png)", png != null && png.Length > 2000);
                }
            }
            finally
            {
                WorldMapLayers.Learned = saved;
                WorldMapView.LayerDataChanged("routes");
                await Task.Delay(400);
            }
        }

        async Task FlightLearnPage3d(CheckFn Check, Func<string, string> P)
        {
            var saved = WorldMapLayers.Learned;
            try
            {
                var s = FlPageStore();
                WorldMapLayers.Learned = () => s;
                await WmEval("window.wm3.setOnForTest('routes', true); window.wm3.setOnForTest('routes.horde', true); window.wm3.setOnForTest('routes.alliance', true); window.wm3.setOnForTest('routes.neutral', true)");
                await Task.Delay(600);
                WorldMapView.LayerDataChanged("routes");
                List<Dictionary<string, object>> lines = null;
                for (int i = 0; i < 40; i++) { lines = await FlPageLines("(function(){try{return window.wm3.lines('routes')}catch(e){return '[]'}})()"); if (lines.Any(l => l.Str("learned") == "mine")) break; await Task.Delay(150); }
                var lr = lines.FirstOrDefault(l => l.Str("learned") == "mine");
                Check("page 3D (learned flights): the learned leg is drawn in the 3D view too, in its own colour (" + lr?.Str("color") + "), the others in their faction's",
                    lr != null && string.Equals(lr.Str("color"), WorldMapLayers.LearnedColor, StringComparison.OrdinalIgnoreCase) && (bool)lr["shown"] && lines.Where(l => l != lr).All(l => !string.Equals(l.Str("color"), WorldMapLayers.LearnedColor, StringComparison.OrdinalIgnoreCase)), Json.Write(lines));
                if (SelfTestClock.ShotsOn)
                {
                    // the camera goes back where it was afterwards (the 3D test goes on from that view)
                    await WmEval("window.__flcam = JSON.parse(window.wm3.state()).cam; 1");
                    await Shot3d(WorldMapView, P("worldmap-learned-route-3d.png"), -50, 700, 1500, 0, 62);
                    await WmEval("(function(){ var c = window.__flcam, ll = window.wm3.xz2ll(c.tx, c.tz); window.wm3.setView(ll[0], ll[1], c.dist, c.az, c.tilt); return 1; })()");
                    await Task.Delay(1200);
                    Check("page 3D (learned flights): the picture of the learned route in 3D is taken (worldmap-learned-route-3d.png)", File.Exists(P("worldmap-learned-route-3d.png")));
                }
            }
            finally
            {
                WorldMapLayers.Learned = saved;
                WorldMapView.LayerDataChanged("routes");
                await Task.Delay(400);
            }
        }

        // ================================================================================================================ the real game files (worldmapreal)
        // The game's own Crossroads -> Ratchet route (TaxiPathNode), flown once as the files have it and twice along a copy whose middle is pushed 300 yards to one
        // side (what Forever's server does to some routes): the flight on the files' line matches, the two pushed ones agree and become the learned route, and the
        // elevation solver then follows it. Read-only, nothing leaves the PC.
        void FlightLearnReal(MapSet set, CheckFn Check, List<string> notes)
        {
            var xr = set.TaxiNodes.FirstOrDefault(n => n.MapId == 1 && (n.Name ?? "").StartsWith("Crossroads") && WorldMapLayers.IsFlightMaster(n));
            var ra = set.TaxiNodes.FirstOrDefault(n => n.MapId == 1 && (n.Name ?? "").StartsWith("Ratchet") && WorldMapLayers.IsFlightMaster(n));
            var routes = TaxiRoute.FromSet(set);
            var kal = routes.TryGetValue(1, out var kr) ? kr : new List<TaxiRoute>();
            var rt = xr != null && ra != null ? kal.FirstOrDefault(r => r.From == xr.Id && r.To == ra.Id) : null;
            if (rt == null) { Check("real learned flights: the route Crossroads -> Ratchet is in the flight tables", false, (xr?.Name ?? "no Crossroads") + " / " + (ra?.Name ?? "no Ratchet")); return; }
            var masters = set.TaxiNodes.Where(n => n.MapId == 1 && WorldMapLayers.IsFlightMaster(n)).ToList();
            var rec = new FlightRecorder { Masters = m => m == 1 ? masters : new List<TaxiNodeInfo>(), Routes = m => m == 1 ? kal : new List<TaxiRoute>(), Build = () => "real" };
            var store = new LearnedFlights(null) { GameRoutes = m => m == 1 ? kal : new List<TaxiRoute>(), Node = (m, id) => masters.FirstOrDefault(n => n.Id == id) };
            var game = FlightGeo.OfRoute(rt);
            // a copy of the route whose middle bulges `push` yards to the left of the way it flies
            List<double[]> Pushed(double push, int seed)
            {
                var rs = FlightGeo.Resample(game, 200); var rnd = new Random(seed); var o = new List<double[]>();
                for (int i = 0; i < rs.Count; i++)
                {
                    int a = Math.Max(0, i - 1), b = Math.Min(rs.Count - 1, i + 1); double dx = rs[b].X - rs[a].X, dy = rs[b].Y - rs[a].Y, l = Math.Sqrt(dx * dx + dy * dy) + 1e-9;
                    double f = i / (double)(rs.Count - 1), bump = push * Math.Pow(Math.Sin(Math.PI * f), 2);
                    o.Add(new[] { rs[i].X - dy / l * bump + (i == 0 || i == rs.Count - 1 ? 0 : (rnd.NextDouble() - 0.5) * 6), rs[i].Y + dx / l * bump + (i == 0 || i == rs.Count - 1 ? 0 : (rnd.NextDouble() - 0.5) * 6) });
                }
                // at one sample a second at ~32 yards a second
                var line = o.Select(p => new FlightGeo.P(p[0], p[1])).ToList(); int n = (int)Math.Ceiling(FlightGeo.Length(line) / 32) + 1;
                return FlightGeo.Resample(line, n).Select(p => new[] { p.X, p.Y }).ToList();
            }
            var t0 = DateTime.UtcNow.AddHours(-2);
            var onFiles = FlFly(rec, Pushed(0, 1), t0)?.FirstOrDefault();
            var p1 = FlFly(rec, Pushed(300, 2), t0.AddMinutes(10))?.FirstOrDefault();
            var p2 = FlFly(rec, Pushed(310, 3), t0.AddMinutes(20))?.FirstOrDefault();
            var dFiles = onFiles == null ? new FlightGeo.Dev() : FlightGeo.Deviation(onFiles.Pts, game);
            var dPush = p1 == null ? new FlightGeo.Dev() : FlightGeo.Deviation(p1.Pts, game);
            notes.Add("note real learned flights: Crossroads -> Ratchet is TaxiPath " + rt.Id + " (" + rt.X.Length + " points, " + Math.Round(rt.Length) + " yards); a flight on it is " + dFiles + " yards off after recording and simplifying (" + (onFiles?.Pts.Count ?? 0) + " points), one pushed 300 yards aside " + dPush + " (" + (p1?.Pts.Count ?? 0) + " points)");
            Check("real learned flights: flights between the real Crossroads and Ratchet masters are recorded as that leg (" + (onFiles?.Key ?? rec.LastResult) + "), not cut (the files have the direct route)",
                onFiles != null && p1 != null && p2 != null && onFiles.From == xr.Id && onFiles.To == ra.Id && p1.Key == onFiles.Key && p2.Key == onFiles.Key, rec.LastResult);
            if (onFiles == null || p1 == null || p2 == null) return;
            Check("real learned flights: the flight on the files' line matches the game's route (" + dFiles + "), the pushed one clearly differs (" + dPush + ")",
                LearnedFlights.Classify(onFiles.Pts, rt, out _) == FlightFit.Matches && LearnedFlights.Classify(p1.Pts, rt, out _) == FlightFit.Deviates);
            store.Add(new[] { onFiles, p1 });
            bool oneNotEnough = store.Get(1, xr.Id, ra.Id) == null;
            store.Add(new[] { p2 });
            var lr = store.Get(1, xr.Id, ra.Id);
            var bulge = lr == null ? 0 : FlightGeo.Resample(lr.Pts, 101).Max(p => FlightGeo.DistToLine(p.X, p.Y, game));
            Check("real learned flights: one pushed flight is not enough, the second agreeing one makes it the learned route (" + (lr?.Pts.Count ?? 0) + " points, bulging " + Math.Round(bulge) + " yards off the files' line)",
                oneNotEnough && lr != null && lr.Flights == 2 && bulge > 250 && bulge < 360);
            var merged = store.MergeRoutes(1, kal);
            var el = new LiveElevation(new FixtureWorld { G = (x, y) => 0, R = merged.ToList() }); var at = DateTime.UtcNow; int n = 0, on = 0;
            el.Solve(1, xr.X + 2, xr.Y - 2, 0, at);
            foreach (var p in Pushed(305, 4).Skip(3).Take(400)) { at = at.AddSeconds(1); var r = el.Solve(1, p[0], p[1], LiveMove.Taxi, at); n++; if (r.RouteId == rt.Id) on++; }
            Check("real learned flights: the 3D dot flying the pushed line is matched to the learned route on " + on + " of " + n + " points", n > 20 && on >= n - 3);
        }
    }
}
