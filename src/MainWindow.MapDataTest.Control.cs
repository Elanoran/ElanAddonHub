using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Maps;

namespace ElansAddonHub
{
    // --selftest "mapdata", part 2: the AtlasMap control (on pictures made in the test) and the optional read-only check of the real game files.
    public partial class MainWindow
    {
        // a 400 x 200 picture of a landscape-ish pattern: two flat halves with a diagonal band, so a marker is easy to tell from the ground
        static BitmapSource SynthPicture(int w = 400, int h = 200)
        {
            var px = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    bool left = x < w / 2, band = Math.Abs((x - y * 2) % 80) < 6;
                    byte r = (byte)(left ? 40 : 52), g = (byte)(left ? 62 : 84), b = (byte)(left ? 88 : 58);
                    if (band) { r += 10; g += 10; b += 10; }
                    int i = (y * w + x) * 4;
                    px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = 255;
                }
            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
            bmp.Freeze();
            return bmp;
        }

        static RenderTargetBitmap RenderHost(FrameworkElement host)
        {
            host.Measure(new Size(host.Width, host.Height));
            host.Arrange(new Rect(0, 0, host.Width, host.Height));
            host.UpdateLayout();
            var bmp = new RenderTargetBitmap((int)host.Width, (int)host.Height, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(host);
            return bmp;
        }

        static byte[] PixelOf(BitmapSource b, int x, int y)
        {
            var buf = new byte[4];
            b.CopyPixels(new Int32Rect(Math.Max(0, Math.Min(b.PixelWidth - 1, x)), Math.Max(0, Math.Min(b.PixelHeight - 1, y)), 1, 1), buf, 4, 0);
            return buf;   // B, G, R, A
        }

        static bool IsColor(byte[] bgra, Color c, int tol = 3) => Math.Abs(bgra[0] - c.B) <= tol && Math.Abs(bgra[1] - c.G) <= tol && Math.Abs(bgra[2] - c.R) <= tol;

        static async Task<bool> WaitFor(Func<bool> cond, int ms = 20000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!cond()) { if (sw.ElapsedMilliseconds > ms) return false; await Task.Delay(20); }
            return true;
        }

        async Task MapControlChecks(string dir, string work, MapIndex idx, CheckFn Check, List<string> notes, Func<string, string> P)
        {
            MapData.SetForTest(idx, "wow_classic_beta|1.60.1.70338|0123456789abcdef");
            MapRender.ForgetFailuresForTest();
            MapRender.TileSourceForTest = fdid => { Thread.Sleep(25); return Solid(256, 256, (byte)(40 + fdid % 30), (byte)(70 + fdid % 40), (byte)(90 + fdid % 20)); };

            var map = new AtlasMap();
            var host = new Border { Width = 892, Height = 632, Padding = new Thickness(16) };
            host.SetResourceReference(Border.BackgroundProperty, "Brush.Surface1");
            host.Child = map;
            RenderHost(host);
            var links = new List<string>(); var changed = new List<int>();
            map.LinkClicked += id => links.Add(id); map.MapChanged += id => changed.Add(id);
            Check("control: an empty AtlasMap says what to do (empty state), has zoom buttons, no markers", map.State == AtlasMap.ViewState.Empty && map.StateText.Contains("Pick a place") && map.MarkerCount == 0 && map.ZoomInButton != null && map.ZoomOutButton != null && map.FitButton != null);

            // ---- a picture, fitted
            var pic = SynthPicture();
            map.SetPicture(11, pic);
            RenderHost(host);
            double vw = map.Viewport.ActualWidth, vh = map.Viewport.ActualHeight;
            double fitScale = Math.Min(vw / 400, vh / 200);
            Check("control: the picture is scaled to fit the stage and centred (" + vw + " x " + vh + " stage, scale " + map.Scale.ToString("0.###", CultureInfo.InvariantCulture) + ")",
                map.State == AtlasMap.ViewState.Ready && map.PictureSize.Width == 400 && map.PictureSize.Height == 200 && Math.Abs(map.Scale - fitScale) < 1e-9 && Math.Abs(map.Zoom - 1) < 1e-9
                && Math.Abs(map.Offset.X - (vw - 400 * fitScale) / 2) < 1e-6 && Math.Abs(map.Offset.Y - (vh - 200 * fitScale) / 2) < 1e-6, map.Scale + " " + map.Offset);
            Check("control: breadcrumb up the tree (World > Northland > Greenvale), no floor selector for a single-floor map", string.Join(" > ", map.BreadcrumbForTest) == "World > Northland > Greenvale" && map.FloorNamesForTest.Count == 0, string.Join(" > ", map.BreadcrumbForTest));
            var fc = map.FractionToViewport(0.25, 0.75); var back = map.ViewportToFraction(fc);
            Check("control: fraction <-> stage point: (0.25, 0.75) is at x = offset + 0.25 x width, y = offset + 0.75 x height, and back", Math.Abs(fc.X - (map.Offset.X + 0.25 * 400 * map.Scale)) < 1e-9 && Math.Abs(fc.Y - (map.Offset.Y + 0.75 * 200 * map.Scale)) < 1e-9 && Math.Abs(back.Value.X - 0.25) < 1e-9 && Math.Abs(back.Value.Y - 0.75) < 1e-9);

            // ---- markers at the right pixel
            var kinds = new[]
            {
                new AtlasMarker(0.25, 0.75, MarkerKind.Spawn, "Gnarlfang", "Level 12 · 4 % drop", "npc:100"),
                new AtlasMarker(0.80, 0.20, MarkerKind.Find, "Ancient Chest", "Found by 3 members", "obj:200"),
                new AtlasMarker(0.50, 0.50, MarkerKind.Vendor, "Innkeeper Hale", "Sells food and drink", "npc:300"),
                new AtlasMarker(0.10, 0.10, MarkerKind.Node, "Copper Vein", "", "obj:400"),
                new AtlasMarker(0.90, 0.90, MarkerKind.Quest, "The Lost Pup", "Level 6", "quest:500"),
            };
            map.AddMarkers(kinds);
            Check("control: five markers added, none merged at the fitted size", map.MarkerCount == 5 && map.Clusters.Count == 5);
            List<string> PixelProblems(string when)
            {
                var shot = RenderHost(host);
                var origin = map.Viewport.TranslatePoint(new Point(0, 0), host);
                var bad = new List<string>();
                foreach (var m in kinds)
                {
                    var p = map.FractionToViewport(m.Fx, m.Fy);
                    if (p.X < 14 || p.Y < 14 || p.X > vw - 14 || p.Y > vh - 14) continue;      // off the stage: nothing to see
                    int dx = m.Kind == MarkerKind.Quest ? 4 : 2;
                    var px = PixelOf(shot, (int)Math.Floor(origin.X + p.X) + dx, (int)Math.Floor(origin.Y + p.Y));
                    var want = ((SolidColorBrush)AtlasMap.KindBrush(m.Kind)).Color;
                    if (!IsColor(px, want, 12)) bad.Add(m.Label + " at " + p + " is " + px[2] + "," + px[1] + "," + px[0] + " not " + want.R + "," + want.G + "," + want.B + " (" + when + ")");
                }
                return bad;
            }
            var bad0 = PixelProblems("fit");
            Check("control: every marker is drawn on the pixel its fractions give, in its kind's colour (spawn, find, vendor, node, quest), fitted", bad0.Count == 0, string.Join("; ", bad0));
            var shotFit = RenderHost(host); var originFit = map.Viewport.TranslatePoint(new Point(0, 0), host);
            var vendorAt = map.FractionToViewport(0.5, 0.5);
            Check("control: next to a marker the picture shows (a marker is a small shape, not a wash over the map)", !IsColor(PixelOf(shotFit, (int)(originFit.X + vendorAt.X) + 22, (int)(originFit.Y + vendorAt.Y)), ((SolidColorBrush)AtlasMap.KindBrush(MarkerKind.Vendor)).Color, 10));

            // ---- zoom and pan
            var anchor = new Point(vw / 2 + 80, vh / 2 - 40);
            var fracBefore = map.ViewportToFraction(anchor).Value;
            map.ZoomAt(anchor, 2);
            var fracAfter = map.ViewportToFraction(anchor).Value;
            Check("control: zoom x2 around a point keeps the map under that point where it was, and the zoom is 2x the fitted size", Math.Abs(map.Zoom - 2) < 1e-9 && Math.Abs(fracAfter.X - fracBefore.X) < 1e-9 && Math.Abs(fracAfter.Y - fracBefore.Y) < 1e-9, map.Zoom + "");
            var bad1 = PixelProblems("zoom 2");
            Check("control: after the zoom the markers that are still in view are on the pixel their fractions give", bad1.Count == 0, string.Join("; ", bad1));
            var before = map.FractionToViewport(0.5, 0.5);
            map.PanBy(-90, -40);
            var after = map.FractionToViewport(0.5, 0.5);
            Check("control: panning by (-90, -40) moves everything by exactly that", Math.Abs(after.X - before.X - -90) < 1e-9 && Math.Abs(after.Y - before.Y - -40) < 1e-9, before + " -> " + after);
            var bad2 = PixelProblems("pan");
            Check("control: after zoom and pan the markers in view are still on their pixel", bad2.Count == 0, string.Join("; ", bad2));
            map.PanBy(1e6, 1e6);
            double mw = 400 * map.Scale, mh = 200 * map.Scale;
            Check("control: panning cannot take the map off the stage: pushed hard to the bottom right the top left of the picture sits on the stage's corner (or the map is centred on an axis it does not fill)",
                Math.Abs(map.Offset.X - (mw > vw ? 0 : (vw - mw) / 2)) < 1e-6 && Math.Abs(map.Offset.Y - (mh > vh ? 0 : (vh - mh) / 2)) < 1e-6, map.Offset + "");
            map.PanBy(-1e6, -1e6);
            Check("control: ... and to the top left the bottom right of the picture sits on the corner", Math.Abs(map.Offset.X - (mw > vw ? vw - mw : (vw - mw) / 2)) < 1e-6 && Math.Abs(map.Offset.Y - (mh > vh ? vh - mh : (vh - mh) / 2)) < 1e-6, map.Offset + "");
            map.ZoomAt(anchor, 1000);
            Check("control: the zoom stops at " + map.MaxZoom + "x the fitted size; zooming out stops at fitted (never smaller)", Math.Abs(map.Zoom - map.MaxZoom) < 1e-9);
            map.ZoomAt(anchor, 1e-6);
            Check("control: zooming out all the way is the fitted view again", Math.Abs(map.Zoom - 1) < 1e-9 && Math.Abs(map.Offset.X - (vw - 400 * fitScale) / 2) < 1e-6);
            map.ZoomAt(anchor, 3); map.ResetView();
            Check("control: the fit button / ResetView returns to the fitted, centred view", Math.Abs(map.Zoom - 1) < 1e-9 && Math.Abs(map.Offset.X - (vw - 400 * fitScale) / 2) < 1e-6 && Math.Abs(map.Offset.Y - (vh - 200 * fitScale) / 2) < 1e-6);
            map.ZoomInButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            double z1 = map.Zoom;
            map.ZoomOutButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("control: the zoom buttons zoom by 1.5x around the middle of the stage", Math.Abs(z1 - 1.5) < 1e-9 && Math.Abs(map.Zoom - 1) < 1e-9, z1 + " " + map.Zoom);
            var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120) { RoutedEvent = UIElement.MouseWheelEvent, Source = map.Viewport };
            map.Viewport.RaiseEvent(wheel);
            double zWheel = map.Zoom;
            map.Viewport.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = UIElement.MouseWheelEvent, Source = map.Viewport });
            Check("control: the mouse wheel zooms in by 1.25x a notch and out again", Math.Abs(zWheel - 1.25) < 1e-9 && Math.Abs(map.Zoom - 1) < 1e-9, zWheel + " " + map.Zoom);

            // ---- drag and click
            map.ZoomToFraction(0.5, 0.5, 3);
            var o0 = map.Offset;
            links.Clear();
            map.SimulateDragForTest(new Point(vw / 2 + 200, vh / 2 + 120), new Point(vw / 2 + 100, vh / 2 + 70));
            Check("control: dragging pans the map by the distance dragged, and a drag is not a click (no link raised)", Math.Abs(map.Offset.X - (o0.X - 100)) < 1e-6 && Math.Abs(map.Offset.Y - (o0.Y - 50)) < 1e-6 && map.WasDragForTest && links.Count == 0, o0 + " -> " + map.Offset);
            map.ResetView();
            var vendorPt = map.FractionToViewport(0.5, 0.5);
            map.SimulateDragForTest(vendorPt, vendorPt);
            Check("control: a click on a marker raises LinkClicked with its link id (npc:300)", links.SequenceEqual(new[] { "npc:300" }) && !map.WasDragForTest, string.Join(",", links));
            links.Clear();
            map.ClickAt(map.FractionToViewport(0.25, 0.75)); map.ClickAt(map.FractionToViewport(0.9, 0.9)); map.ClickAt(map.FractionToViewport(0.4, 0.3));
            Check("control: each other marker raises its own id (npc:100, quest:500); a click on empty ground raises nothing", links.SequenceEqual(new[] { "npc:100", "quest:500" }), string.Join(",", links));
            links.Clear();
            map.AddMarkers(new[] { new AtlasMarker(0.35, 0.15, MarkerKind.Find, "Unlinked thing") });
            map.ClickAt(map.FractionToViewport(0.35, 0.15));
            Check("control: a marker without a link id raises nothing", links.Count == 0 && map.MarkerCount == 6);
            links.Clear();

            // ---- hover text
            map.HoverAtForTest(map.FractionToViewport(0.25, 0.75));
            Check("control: hovering a marker shows its name and tooltip text", map.TipTextForTest == "Gnarlfang|Level 12 · 4 % drop", map.TipTextForTest);
            map.HoverAtForTest(map.FractionToViewport(0.10, 0.10));
            Check("control: a marker without tooltip text shows its name and kind", map.TipTextForTest == "Copper Vein|Node", map.TipTextForTest);
            map.HoverAtForTest(new Point(5, 5));
            Check("control: moving off the marker hides the text", map.TipTextForTest == null);

            // ---- clusters
            map.ClearMarkers();
            Check("control: ClearMarkers empties the layer", map.MarkerCount == 0 && map.Clusters.Count == 0);
            var group = new List<AtlasMarker>
            {
                new AtlasMarker(0.50, 0.50, MarkerKind.Spawn, "Boar A"), new AtlasMarker(0.504, 0.50, MarkerKind.Spawn, "Boar B"), new AtlasMarker(0.50, 0.504, MarkerKind.Spawn, "Boar C"), new AtlasMarker(0.502, 0.502, MarkerKind.Find, "Chest"),
                new AtlasMarker(0.10, 0.80, MarkerKind.Vendor, "Far away", "alone", "npc:9"),
            };
            map.AddMarkers(group);
            var cl = map.Clusters;
            Check("control: markers that overlap on the screen become one: 4 close ones = 1 cluster of 4 (mixed kinds), the far one stays alone", cl.Count == 2 && cl.Max(c => c.Count) == 4 && cl.Count(c => c.Single) == 1 && !cl.First(c => c.Count == 4).SameKind, string.Join(",", cl.Select(c => c.Count)));
            var cpt = new Point(cl.First(c => c.Count == 4).X, cl.First(c => c.Count == 4).Y);
            map.HoverAtForTest(cpt);
            Check("control: hovering a cluster lists what is in it", map.TipTextForTest != null && map.TipTextForTest.StartsWith("4 places here|Boar A|Boar B|Boar C|Chest") && map.TipTextForTest.EndsWith("Click to zoom in"), map.TipTextForTest);
            double zc0 = map.Zoom; links.Clear();
            for (int i = 0; i < 8 && map.Clusters.Count < 5; i++)
            {
                var c4 = map.Clusters.OrderByDescending(c => c.Count).First();
                if (c4.Count < 2) break;
                map.ClickAt(new Point(c4.X, c4.Y));
            }
            Check("control: clicking a cluster zooms in on it (no link); a few clicks and the four are separate markers", map.Zoom > zc0 && links.Count == 0 && map.Clusters.Count == 5 && map.Clusters.All(c => c.Single), "zoom " + map.Zoom + " clusters " + map.Clusters.Count);
            map.ResetView();
            var rnd = new Random(7); var many = new List<AtlasMarker>();
            for (int i = 0; i < 3000; i++) many.Add(new AtlasMarker(rnd.NextDouble(), rnd.NextDouble(), (MarkerKind)(i % 5), "m" + i));
            map.ClearMarkers(); map.AddMarkers(many);
            var swc = System.Diagnostics.Stopwatch.StartNew();
            var manyCl = map.Clusters;
            long clMs = swc.ElapsedMilliseconds;
            Check("control: 3,000 markers cluster fast (" + clMs + " ms), every marker is in exactly one cluster, far fewer clusters than markers", manyCl.Sum(c => c.Count) == 3000 && manyCl.Count < 1500 && clMs < 120, manyCl.Count + " clusters " + clMs + " ms");
            var swr = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 5; i++) { map.PanBy(1, 1); RenderHost(host); }
            Check("control: redrawing the map with 3,000 markers is quick (" + swr.ElapsedMilliseconds / 5 + " ms a frame)", swr.ElapsedMilliseconds / 5 < 250, swr.ElapsedMilliseconds / 5 + " ms");
            map.ClearMarkers(); map.ResetView();

            // the pictures of this part: the invented map with all five kinds, a cluster, and the hover text
            map.AddMarkers(kinds);
            map.AddMarkers(new[] { new AtlasMarker(0.60, 0.30, MarkerKind.Spawn, "Boar A"), new AtlasMarker(0.605, 0.30, MarkerKind.Spawn, "Boar B"), new AtlasMarker(0.60, 0.305, MarkerKind.Spawn, "Boar C"), new AtlasMarker(0.2, 0.4, MarkerKind.Spawn, "Boar D"), new AtlasMarker(0.2015, 0.401, MarkerKind.Spawn, "Boar E") });
            map.HoverAtForTest(map.FractionToViewport(0.50, 0.50));
            RenderHost(host);
            SnapshotSheet(host, P("23-mapdata-control.png"));
            map.HoverAtForTest(map.FractionToViewport(0.60, 0.30));
            RenderHost(host);
            SnapshotSheet(host, P("23-mapdata-control-cluster.png"));
            map.ClearMarkers();

            // ---- showing a real map of the index: the picture is made in the background, the states along the way
            changed.Clear();
            map.ShowMap(20);
            bool loading = map.State == AtlasMap.ViewState.Loading;
            RenderHost(host);
            SnapshotSheet(host, P("23-mapdata-control-loading.png"));
            bool got = await WaitFor(() => map.State == AtlasMap.ViewState.Ready);
            Check("control: ShowMap of a map whose picture is not made yet shows the loading state, then the picture (256 x 256), the map changed event fired", loading && got && map.PictureSize.Width == 256 && map.UiMapId == 20 && changed.SequenceEqual(new[] { 20 }), loading + " " + got + " " + string.Join(",", changed));
            Check("control: a map with floors offers them: Cellar, Hall, Tower in floor order, the first selected", map.FloorNamesForTest.SequenceEqual(new[] { "Cellar", "Hall", "Tower" }) && map.SelectedFloorForTest == 0, string.Join(",", map.FloorNamesForTest) + " " + map.SelectedFloorForTest);
            RenderHost(host);
            SnapshotSheet(host, P("23-mapdata-control-floors.png"));
            map.SelectFloorForTest(2);
            got = await WaitFor(() => map.State == AtlasMap.ViewState.Ready && map.UiMapId == 22);
            Check("control: choosing the Tower floor shows that floor's map (22) and selects it in the strip", got && map.SelectedFloorForTest == 2 && map.FloorNamesForTest.Count == 3, map.UiMapId + " " + map.SelectedFloorForTest);
            map.ShowInstance(99);
            got = await WaitFor(() => map.State == AtlasMap.ViewState.Ready && map.UiMapId == 20);
            Check("control: ShowInstance(99) shows the Crypt's first floor", got && map.SelectedFloorForTest == 0);
            map.ShowInstance(98);
            Check("control: an instance the game has no picture of says so (Deep Hall) instead of showing nothing", map.State == AtlasMap.ViewState.NoArt && map.StateText.Contains("Deep Hall") && map.FloorNamesForTest.Count == 0, map.StateText);
            RenderHost(host);
            SnapshotSheet(host, P("23-mapdata-control-noart.png"));
            map.ShowMap(12);
            bool redmoor = map.State == AtlasMap.ViewState.NoArt && map.StateText.Contains("Redmoor");
            map.ShowMap(9999);
            Check("control: a map whose art has no tiles (Redmoor) or a map that is not in the files says so", redmoor && map.State == AtlasMap.ViewState.NoArt);
            map.ShowMap(11);
            got = await WaitFor(() => map.State == AtlasMap.ViewState.Ready && map.UiMapId == 11);
            Check("control: Greenvale again (its picture is on disk now: no waiting for the renderer)", got && map.PictureSize.Width == 600 && map.PictureSize.Height == 300);
            map.ClickBreadcrumbForTest(1);
            got = await WaitFor(() => map.State == AtlasMap.ViewState.Ready && map.UiMapId == 10);
            Check("control: a click on Northland in the breadcrumb goes up the tree to the continent map (400 x 200)", got && map.PictureSize.Width == 400 && string.Join(" > ", map.BreadcrumbForTest) == "World > Northland");
            map.ShowMap(30); map.ShowMap(20);
            got = await WaitFor(() => map.State == AtlasMap.ViewState.Ready);
            await Task.Delay(400);
            Check("control: a late answer for an older map is dropped: asked for Frostfall (512 x 256) and at once the Crypt cellar, the cellar's 256 x 256 stays", got && map.UiMapId == 20 && map.PictureSize.Width == 256 && map.State == AtlasMap.ViewState.Ready, map.UiMapId + " " + map.PictureSize);
            MapData.ResetForTest();
            map.ShowMap(11);
            Check("control: without map data it says so (unavailable) and keeps working", map.State == AtlasMap.ViewState.Unavailable && map.StateText.Contains("not available"), map.StateText);
            MapData.Enabled = () => false; MapData.Start();
            map.ShowMap(11);
            Check("control: with the Game data switch off it points at Settings", map.State == AtlasMap.ViewState.Unavailable && map.StateText.Contains("Settings"), map.StateText);
            MapData.Enabled = () => true;
            MapData.SetForTest(idx, "wow_classic_beta|1.60.1.70338|0123456789abcdef");
            // world positions meet the map
            map.ShowMap(11);
            await WaitFor(() => map.State == AtlasMap.ViewState.Ready && map.UiMapId == 11);
            var wf = map.WorldToFraction(0, 500, 1000);
            Check("control: a world position (map 0, x 500, y 1000) is placed on the shown map at (0.5, 0.5); one it does not show gives null", wf != null && Math.Abs(wf.Value.X - 0.5) < 1e-6 && Math.Abs(wf.Value.Y - 0.5) < 1e-6 && map.WorldToFraction(0, -900, -1900) == null);
            map.ShowMap(20); await WaitFor(() => map.State == AtlasMap.ViewState.Ready && map.UiMapId == 20);

            // ---- in a real window: keyboard, the tooltip delay, letting go of the picture when the control leaves the screen, map data arriving late
            var host2 = new Border { Width = 892, Height = 632, Padding = new Thickness(16) };
            host2.SetResourceReference(Border.BackgroundProperty, "Brush.Surface1");
            var map2 = new AtlasMap();
            host2.Child = map2;
            var win = new Window { Width = 900, Height = 660, WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false, Left = -4000, Top = -4000, Content = host2, ResizeMode = ResizeMode.NoResize };
            try
            {
                win.Show();
                await Task.Delay(150);
                map2.ShowMap(11);
                bool r2 = await WaitFor(() => map2.State == AtlasMap.ViewState.Ready && map2.UiMapId == 11);
                Check("window: the control in a window shows a map from the render cache (600 x 300) and holds its picture", r2 && map2.PictureHeldForTest && map2.PictureSize.Width == 600);
                var src = PresentationSource.FromVisual(win);
                void Key(Key k) => map2.Viewport.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, src, 0, k) { RoutedEvent = Keyboard.KeyDownEvent });
                double zk0 = map2.Zoom;
                Key(System.Windows.Input.Key.OemPlus);
                double zk1 = map2.Zoom;
                var offK = map2.Offset;
                Key(System.Windows.Input.Key.Left);
                Check("window: the keyboard works the map: + zooms in 1.5x, the left arrow pans right by up to 64 (the map stays on the stage)", Math.Abs(zk1 / zk0 - 1.5) < 1e-9 && map2.Offset.X > offK.X - 1e-9 && map2.Offset.X <= 1e-9 && (map2.Offset.X - offK.X <= 64 + 1e-6), zk0 + " " + zk1 + " " + offK + " " + map2.Offset);
                Key(System.Windows.Input.Key.D0);
                Check("window: ... 0 returns to the fitted view", Math.Abs(map2.Zoom - 1) < 1e-9);
                map2.AddMarkers(new[] { new AtlasMarker(0.5, 0.5, MarkerKind.Vendor, "Delayed", "after 300 ms", "npc:1") });
                map2.HoverDelayedForTest(map2.FractionToViewport(0.5, 0.5));
                string tipNow = map2.TipTextForTest;
                await WaitFor(() => map2.TipTextForTest != null, 2000);
                Check("window: the hover text of a marker waits (300 ms, DESIGN.md) and then appears", tipNow == null && map2.TipTextForTest == "Delayed|after 300 ms", tipNow + " / " + map2.TipTextForTest + " / " + map2.TipStateForTest);
                map2.HoverAtForTest(new Point(2, 2));
                host2.Child = null;
                await Task.Delay(100);
                Check("window: taken off the screen the control lets go of its picture (nothing of a map is kept for a control nobody sees)", !map2.PictureHeldForTest && map2.ReleasedForTest);
                host2.Child = map2;
                bool r3 = await WaitFor(() => map2.PictureHeldForTest && map2.State == AtlasMap.ViewState.Ready);
                Check("window: ... and back on the screen it loads it again from the cache, markers kept", r3 && map2.UiMapId == 11 && map2.MarkerCount == 1);
                MapData.ResetForTest();
                map2.ShowMap(11);
                bool was = map2.State == AtlasMap.ViewState.Unavailable;
                MapData.SetForTest(idx, "wow_classic_beta|1.60.1.70338|0123456789abcdef");
                map2.MapDataChangedForTest();
                bool r4 = await WaitFor(() => map2.State == AtlasMap.ViewState.Ready && map2.UiMapId == 11);
                Check("window: a control that said 'not available' shows its map by itself when the map data arrives", was && r4);
            }
            finally { try { win.Close(); } catch { } }            MapRender.TileSourceForTest = null;
        }

        // ---------------------------------------------------------------------------------------------------------------------
        async Task RealMapChecks(string dir, string work, CheckFn Check, List<string> notes, Func<string, string> P)
        {
            var real = Environment.GetEnvironmentVariable("ELANSHUB_WOW");
            if (string.IsNullOrEmpty(real)) real = WowLocator.Find();
            if (string.IsNullOrEmpty(real) || !Directory.Exists(Path.Combine(real, "Data", "data")) || !Directory.Exists(Path.Combine(real, "_classic_beta_")))
            {
                notes.Add("skip real game files: no WoW install with the Forever beta client on this PC");
                return;
            }
            try
            {
                var rw = Path.Combine(work, "real");
                MapData.ResetForTest();
                MapData.WowRoot = () => real; MapData.Enabled = () => true;
                MapData.CacheFile = Path.Combine(rw, "cache", "mapdata.bin");
                MapRender.DirOverride = Path.Combine(rw, "maps");
                MapRender.TileSourceForTest = null;
                MapRender.ForgetFailuresForTest();
                long m0 = GC.GetTotalMemory(true);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                MapData.Start();
                await WaitFor(() => MapData.Status == MapData.State.Ready || MapData.Status == MapData.State.Unavailable, 120000);
                long loadMs = sw.ElapsedMilliseconds;
                if (!MapData.Ready)
                {
                    notes.Add("skip real game files: " + MapData.StatusLine + " (this Outpost does not know that game build's map tables; nothing was guessed)");
                    return;
                }
                var idx = MapData.Index;
                long memIdx = GC.GetTotalMemory(true) - m0;
                string from1 = MapData.LastLoadFrom;
                Check("real game files: the map tables are read from the game files (" + MapData.StatusLine + ")", from1 == "game files" && idx.UiMapCount >= 50 && idx.MapsWithArt >= 50 && MapData.StatusLine.StartsWith("Maps: "), from1 + " " + MapData.StatusLine);
                Check("real game files: the saved index is written", File.Exists(MapData.CacheFile));
                MapData.ResetForTest(); MapData.CacheFile = Path.Combine(rw, "cache", "mapdata.bin");
                var sw2 = System.Diagnostics.Stopwatch.StartNew();
                MapData.Start();
                await WaitFor(() => MapData.Status == MapData.State.Ready || MapData.Status == MapData.State.Unavailable, 60000);
                long cacheMs = sw2.ElapsedMilliseconds;
                Check("real game files: the next start comes from the saved index (not the game files)", MapData.Ready && MapData.LastLoadFrom == "cache file" && MapData.Index.UiMapCount == idx.UiMapCount, MapData.LastLoadFrom);
                idx = MapData.Index;
                var dur = idx.Get(1411);
                idx.PictureSize(1411, out int dw, out int dh);
                Check("real game files: Durotar is uiMap 1411 under Kalimdor, a zone with a 1002 x 668 picture of 12 tiles", dur != null && dur.Name == "Durotar" && dur.ParentId == 1414 && dur.Type == UiMapInfo.Zone && dw == 1002 && dh == 668 && idx.ArtFor(1411).Tiles.Length == 12 && string.Join(" > ", idx.Breadcrumb(1411).Select(m => m.Name)) == "Azeroth > Kalimdor > Durotar", dur?.Name + " " + dw + "x" + dh);
                var rh = idx.WorldToMap(1, 340, -4686);
                Check("real game files: Razor Hill (Kalimdor x 340, y -4686) is Durotar at about 51 / 42", rh != null && rh.Value.UiMapId == 1411 && Math.Abs(rh.Value.Fx - 0.515) < 0.02 && Math.Abs(rh.Value.Fy - 0.4165) < 0.02, rh?.ToString());
                Check("real game files: Stormwind (map 0) is Stormwind City, Orgrimmar (map 1) Orgrimmar, the Crossroads is in The Barrens, a Durotar spot in Durotar (the city and neighbouring-zone rules)",
                    idx.Name(idx.WorldToMap(0, -8900, 640).Value.UiMapId) == "Stormwind City" && idx.Name(idx.WorldToMap(1, 1600, -4400).Value.UiMapId) == "Orgrimmar" && idx.Name(idx.WorldToMap(1, -441, -2596).Value.UiMapId) == "The Barrens" && idx.Name(idx.WorldToMap(1, 340, -4686).Value.UiMapId) == "Durotar");
                var w = idx.MapToWorld(1411, rh.Value.Fx, rh.Value.Fy);
                Check("real game files: map -> world gives Razor Hill back (within a yard)", w != null && w.Value.MapId == 1 && Math.Abs(w.Value.X - 340) < 1 && Math.Abs(w.Value.Y - -4686) < 1, w?.ToString());
                var kal = idx.Convert(1411, rh.Value.Fx, rh.Value.Fy, 1414);
                Check("real game files: the same spot on the Kalimdor map is inside the picture, Durotar is in the eastern half and the Azeroth world map shows Kalimdor in its left part", kal != null && kal.Value.Fx > 0.4 && kal.Value.Fx < 0.8 && idx.Convert(1414, 0.5, 0.5, 947) != null && idx.Convert(1414, 0.5, 0.5, 947).Value.Fx < 0.5, kal?.ToString());
                var probeWas = MapIndex.AreaAt; MapIndex.AreaAt = null; int bad = 0; foreach (var m in idx.UiMaps.Where(m => m.System == 0 && m.Type == UiMapInfo.Zone)) { var c = idx.MapToWorld(m.Id, 0.5, 0.5); if (c == null) continue; var p = idx.WorldToMap(c.Value.MapId, c.Value.X, c.Value.Y); if (p == null || p.Value.UiMapId != m.Id) bad++; } MapIndex.AreaAt = probeWas;
                Check("real game files: the centre of every zone map is shown by that zone (no other map wins on its own middle; the rectangles alone - the terrain's area rule is checked on its own)", bad == 0, bad + " zones");
                var inst = idx.Instances.Where(i => !i.Placeholder).ToList();
                var dm = idx.Instance(36); var rfc = idx.Instance(389); var av = idx.Instance(30);
                Check("real game files: instances listed from the Map table: the Deadmines and Ragefire Chasm are dungeons, Alterac Valley is a battleground with its map as floor, test leftovers are flagged", dm != null && dm.Name == "Deadmines" && dm.Kind == "Dungeon" && rfc != null && rfc.Name == "Ragefire Chasm" && av != null && av.Kind == "Battleground" && av.Floors.Any(f => f.Id == 1459) && idx.Instances.Any(i => i.Placeholder) && inst.Count >= 20, inst.Count + "");
                notes.Add("note real game files: " + idx.UiMapCount + " UiMaps (" + idx.MapsWithArt + " with art), " + idx.Set.Assignments.Count + " assignments, " + idx.Set.Arts.Count + " art sets with " + idx.Set.Arts.Sum(a => a.Tiles.Length) + " tiles, " + idx.Set.Maps.Count + " Map rows (" + idx.Set.LockedMapRows + " key-locked), " + idx.Set.Areas.Count + " areas, " + idx.Set.Floors.Count + " floor-group rows; " + inst.Count + " instances (+" + (idx.Instances.Count - inst.Count) + " test leftovers), " + idx.Instances.Count(i => i.HasMap) + " with a map in this client: " + string.Join(", ", idx.Instances.Where(i => i.HasMap).Select(i => i.Name + " [" + string.Join("/", i.Floors.Select(f => f.Name)) + "]")) + "; without art: " + string.Join(", ", idx.UiMaps.Where(m => !idx.HasArt(m.Id)).Select(m => m.Name)));
                notes.Add("note real game files: map tables read " + loadMs + " ms from the game files, " + cacheMs + " ms from the saved index (" + new FileInfo(MapData.CacheFile).Length / 1024 + " KB), about " + memIdx / 1024 + " KB in memory");
                notes.Add("note real game files: instances without a map in this client (the game ships UiMap art only for the world, continents, zones, cities and battlegrounds): " + string.Join(", ", inst.Where(i => !i.HasMap).Take(40).Select(i => i.Name)));

                // ---- render Durotar and the biggest map
                long mb = GC.GetTotalMemory(true);
                var swr = System.Diagnostics.Stopwatch.StartNew();
                var path = await RequestPath(1411);
                long durMs = swr.ElapsedMilliseconds;
                var img = path == null || path == "TIMEOUT" ? null : MapRender.Load(path) as BitmapSource;
                bool sensible = false; string stat = "";
                if (img != null)
                {
                    var px = new byte[img.PixelWidth * img.PixelHeight * 4];
                    img.CopyPixels(px, img.PixelWidth * 4, 0);
                    long opaque = 0, sum = 0; for (int i = 0; i < px.Length; i += 4) { if (px[i + 3] > 200) opaque++; sum += px[i] + px[i + 1] + px[i + 2]; }
                    double cover = opaque / (double)(img.PixelWidth * img.PixelHeight), mean = sum / (3.0 * img.PixelWidth * img.PixelHeight);
                    sensible = cover > 0.95 && mean > 25 && mean < 230;
                    stat = "opaque " + cover.ToString("0.00", CultureInfo.InvariantCulture) + ", mean " + mean.ToString("0", CultureInfo.InvariantCulture);
                }
                Check("real game files: Durotar's art renders: a 1002 x 668 PNG in the cache folder, the whole picture covered, neither black nor white (" + stat + ")", img != null && img.PixelWidth == 1002 && img.PixelHeight == 668 && sensible && path.EndsWith("_1411.png"), path + " " + stat);
                notes.Add("note real game files: Durotar rendered in " + durMs + " ms (CASC opened, 12 BLP tiles decoded, PNG " + (path != null && File.Exists(path) ? new FileInfo(path).Length / 1024 : 0) + " KB), composed bitmap " + MapRender.LastPeakBytes / 1024 + " KB, managed memory +" + (GC.GetTotalMemory(true) - mb) / 1024 + " KB afterwards");
                // the explored-area overlays on the real files: Durotar has them, and its picture is not the base art alone
                var durArt = idx.ArtFor(1411);
                Check("real game files: the overlay tables are read in the verified layouts (" + (MapData.OverlayProblem ?? "no problem") + "): Durotar's art has overlays with tiles, and so do most arts",
                    MapData.OverlayProblem == null && idx.Set.HasOverlays && durArt.Overlays.Length >= 5 && durArt.Overlays.All(o => o.Tiles.Length > 0 && o.Width > 0 && o.Height > 0) && idx.Set.Arts.Count(a => a.Overlays.Length > 0) >= 20, durArt.Overlays.Length + " overlays");
                if (img != null && durArt.Overlays.Length > 0)
                {
                    using (var casc = CascStorage.Open(real, MapData.Flavor() ?? "_classic_beta_"))
                    {
                        var want = new HashSet<int>(durArt.Tiles.Select(t => t.FileDataId)); foreach (var o in durArt.Overlays) foreach (var t in o.Tiles) want.Add(t.FileDataId);
                        var keys = casc.ResolveFileIds(want);
                        MapRender.TileImage Real(int fid) { if (!keys.TryGetValue(fid, out var ck)) return null; var b = casc.ReadByContentKey(ck); if (b == null) return null; var q = Blp.Decode(b, out int tw, out int th); return new MapRender.TileImage { Bgra = q, Width = tw, Height = th }; }
                        var baseOnly = MapRender.Compose(new MapArtInfo { Id = durArt.Id, Layers = durArt.Layers, Tiles = durArt.Tiles }, Real, out int bw1, out int bh1, out _);
                        var full = MapRender.Compose(durArt, Real, out _, out _, out _, out int ovMissing);
                        long diff = 0; for (int i = 0; i < baseOnly.Length; i += 4) if (Math.Abs(baseOnly[i] - full[i]) + Math.Abs(baseOnly[i + 1] - full[i + 1]) + Math.Abs(baseOnly[i + 2] - full[i + 2]) > 12) diff++;
                        double share = diff / (double)(bw1 * bh1);
                        var cached = new byte[img.PixelWidth * img.PixelHeight * 4]; img.CopyPixels(cached, img.PixelWidth * 4, 0);
                        long same = 0; for (int i = 0; i < cached.Length; i += 4) if (cached[i] == full[i] && cached[i + 1] == full[i + 1] && cached[i + 2] == full[i + 2]) same++;
                        Check("real game files: Durotar's composed picture is not the base art alone (" + (share * 100).ToString("0", CultureInfo.InvariantCulture) + " % of its pixels change: the explored areas), no overlay tile missing, and the picture in the cache folder is that composition",
                            share > 0.10 && ovMissing == 0 && same > 0.99 * (img.PixelWidth * img.PixelHeight), share.ToString("0.00", CultureInfo.InvariantCulture) + " missing " + ovMissing + " same " + same);
                    }
                }
                var biggest = idx.UiMaps.Where(m => idx.HasArt(m.Id)).OrderByDescending(m => { idx.PictureSize(m.Id, out int a, out int b); return (long)a * b; }).First();
                idx.PictureSize(biggest.Id, out int bw, out int bh);
                var swb = System.Diagnostics.Stopwatch.StartNew();
                var bpath = await RequestPath(biggest.Id);
                notes.Add("note real game files: the biggest map, " + biggest.Name + " (" + bw + " x " + bh + ", " + idx.ArtFor(biggest.Id).Tiles.Count(t => t.Layer == idx.ArtFor(biggest.Id).BaseLayer.Index) + " tiles) rendered in " + swb.ElapsedMilliseconds + " ms, composed bitmap " + MapRender.LastPeakBytes / 1024 / 1024 + " MB");
                Check("real game files: the biggest map renders too (" + biggest.Name + " " + bw + " x " + bh + ")", bpath != null && bpath != "TIMEOUT" && File.Exists(bpath));
                int renderedAll = 0;
                var swAll = System.Diagnostics.Stopwatch.StartNew();
                var allPaths = await Task.WhenAll(idx.UiMaps.Where(m => idx.HasArt(m.Id) && m.System == 0).Select(m => RequestPath(m.Id)));   // one batch: the game's file list is read once for all of them
                renderedAll = allPaths.Count(pp => pp != null && pp != "TIMEOUT");
                Check("real game files: every map of the index with art renders (" + renderedAll + " of " + idx.UiMaps.Count(m => idx.HasArt(m.Id) && m.System == 0) + " in " + swAll.ElapsedMilliseconds + " ms; a map whose tiles are not in the files is reported, not a failure of the test)", renderedAll >= idx.UiMaps.Count(m => idx.HasArt(m.Id) && m.System == 0) - 2, renderedAll + "");

                // ---- the control on the real map, with markers placed from world coordinates
                var map = new AtlasMap();
                var host = new Border { Width = 892, Height = 632, Padding = new Thickness(16) };
                host.SetResourceReference(Border.BackgroundProperty, "Brush.Surface1");
                host.Child = map;
                RenderHost(host);
                map.ShowMap(1411);
                bool ready = await WaitFor(() => map.State == AtlasMap.ViewState.Ready);
                RenderHost(host);
                var spots = new (int map, double x, double y, MarkerKind k, string label, string tip, string link)[]
                {
                    (1, 340, -4686, MarkerKind.Vendor, "Razor Hill", "Innkeeper Grosk, Trader and Weaponsmith", "npc:3321"),
                    (1, -840, -4950, MarkerKind.Vendor, "Sen'jin Village", "Innkeeper Kimlya", "npc:6928"),
                    (1, 1560, -4420, MarkerKind.Quest, "Orgrimmar gate", "Lazy Peons waits here", "quest:5441"),
                    (1, -620, -4180, MarkerKind.Node, "Copper Vein", "Mining 1", "obj:1731"),
                    (1, 20, -3820, MarkerKind.Find, "Battered Chest", "Opened 14 times", "obj:2744"),
                };
                var ms = new List<AtlasMarker>();
                foreach (var s in spots) { var f = map.WorldToFraction(s.map, s.x, s.y); if (f != null) ms.Add(new AtlasMarker(f.Value.X, f.Value.Y, s.k, s.label, s.tip, s.link)); }
                var rnd = new Random(11);
                var camp = map.WorldToFraction(1, -320, -4310).Value;      // a camp of scouts: the spawns overlap into one numbered marker
                for (int i = 0; i < 9; i++) ms.Add(new AtlasMarker(camp.X + (rnd.NextDouble() - 0.5) * 0.012, camp.Y + (rnd.NextDouble() - 0.5) * 0.012, MarkerKind.Spawn, "Razormane Scout " + (i + 1), "Level 7 · patrols the camp", "npc:3114"));
                for (int i = 0; i < 6; i++) ms.Add(new AtlasMarker(0.2 + rnd.NextDouble() * 0.6, 0.5 + rnd.NextDouble() * 0.4, MarkerKind.Spawn, "Dire Mottled Boar", "Level 3", "npc:3100"));
                map.AddMarkers(ms);
                Check("real game files: the control shows Durotar with the markers converted from world coordinates (5 places + a camp + boars), Razor Hill at 51 / 42 of the picture", ready && map.MarkerCount == ms.Count && ms.Count >= 16 && Math.Abs(ms[0].Fx - 0.515) < 0.02 && Math.Abs(ms[0].Fy - 0.4165) < 0.02 && string.Join(" > ", map.BreadcrumbForTest) == "Azeroth > Kalimdor > Durotar", ms.Count + " " + ms[0].Fx);
                map.HoverAtForTest(map.FractionToViewport(ms[0].Fx, ms[0].Fy));
                RenderHost(host);
                SnapshotSheet(host, P("23-mapdata-durotar.png"));
                map.ZoomToFraction(camp.X, camp.Y, 3);
                map.HoverAtForTest(new Point(1, 1));
                RenderHost(host);
                SnapshotSheet(host, P("23-mapdata-durotar-zoomed.png"));
                map.ClearMarkers();
                map.ShowMap(1414);
                ready = await WaitFor(() => map.State == AtlasMap.ViewState.Ready && map.UiMapId == 1414);
                var kms = new List<AtlasMarker>();
                foreach (var s in spots) { var f = map.WorldToFraction(s.map, s.x, s.y); if (f != null) kms.Add(new AtlasMarker(f.Value.X, f.Value.Y, s.k, s.label, s.tip, s.link)); }
                map.AddMarkers(kms);
                RenderHost(host);
                SnapshotSheet(host, P("23-mapdata-kalimdor.png"));
                Check("real game files: the same world positions on the Kalimdor map (a continent) are all on the picture", ready && kms.Count == spots.Length && kms.All(k => k.Fx > 0 && k.Fx < 1 && k.Fy > 0 && k.Fy < 1), kms.Count + "");
                map.ClearMarkers();
                map.ShowInstance(30);
                ready = await WaitFor(() => map.State == AtlasMap.ViewState.Ready);
                map.AddMarkers(new[] { new AtlasMarker(0.42, 0.38, MarkerKind.Spawn, "Drek'Thar", "Horde general", "npc:11946"), new AtlasMarker(0.45, 0.22, MarkerKind.Vendor, "Armorer", "", "npc:13176") });
                RenderHost(host);
                SnapshotSheet(host, P("23-mapdata-alterac.png"));
                Check("real game files: a battleground (Alterac Valley, map 30) opens through ShowInstance", ready && map.UiMapId == 1459);
                map.ShowInstance(36);
                RenderHost(host);
                SnapshotSheet(host, P("23-mapdata-deadmines.png"));
                Check("real game files: the Deadmines has no picture in this client: the control says so", map.State == AtlasMap.ViewState.NoArt && map.StateText.Contains("Deadmines"), map.StateText);
            }
            catch (Exception e) { notes.Add("FAIL real game files: " + e); Check("real game files: no exception", false, e.Message); }
            finally
            {
                MapData.ResetForTest();
            }
        }
    }
}
