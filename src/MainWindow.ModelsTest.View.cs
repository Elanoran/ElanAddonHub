using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Atlas.Classic;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Models3d;

namespace ElansAddonHub
{
    // --selftest "models", part 3 (3.6, Atlas 3D models steps 3 + 4): the model viewer (ModelView, src\ModelViewer) and the NPC page's Model card, on the
    // small test model of part 2 and the test's Classic baseline: the card at the top of the rail (wide) and right after the tooltip card in the Overview
    // (narrow, the same viewer moved), no card for a character model or a creature without a display id, the calm lines (model data unavailable / off /
    // being read, no model in this build, no WebView2), the viewer (mesh, bones, textures, the Stand clock, the bone matrices against this file's own C#
    // working of the M2 rule), pausing while hidden, letting the browser go when the page is left, the sandbox; 3.6 step 5: the look switch (shown only
    // for two or more usable looks, the highest chance first, each chance a tooltip, another look loaded into the same web view by a "load" message,
    // the view kept, a late answer about the look before dropped). The real models are modelsreal's.
    public partial class MainWindow
    {
        // ---- the M2 bone rule worked out here, independently of viewer.js, from the built file: local = T(pivot) T R S T(-pivot), model = parent * local.
        // System.Numerics is row-vector, so the product is written the other way round; its M11..M44 order is three.js's column-major element order.
        static double[] MvPose(byte[] file, double ms)
        {
            var meta = CreatureModel.ReadMeta(file, out int ds);
            if (meta == null) return null;
            var bones = meta.List("bones").Cast<Dictionary<string, object>>().ToList();
            var loops = meta.List("globalLoops").Select(x => Convert.ToDouble(x, CultureInfo.InvariantCulture)).ToList();
            double dur = meta.Child("anim")?.Long("duration") ?? 0;
            float[] Sample(object tr, int w, float[] def)
            {
                if (!(tr is List<object> a) || a.Count < 4) return def;
                int off = Convert.ToInt32(a[0], CultureInfo.InvariantCulture), n = Convert.ToInt32(a[1], CultureInfo.InvariantCulture), interp = Convert.ToInt32(a[2], CultureInfo.InvariantCulture), gs = Convert.ToInt32(a[3], CultureInfo.InvariantCulture);
                if (n <= 0) return def;
                double t = gs >= 0 ? (gs < loops.Count && loops[gs] > 0 ? ms % loops[gs] : 0) : (dur > 0 ? ms % dur : 0);
                uint T(int k) => BitConverter.ToUInt32(file, ds + off + k * 4);
                float[] Vv(int k) => Enumerable.Range(0, w).Select(j => BitConverter.ToSingle(file, ds + off + n * 4 + (k * w + j) * 4)).ToArray();
                if (t <= T(0)) return Vv(0);
                if (t >= T(n - 1)) return Vv(n - 1);
                int i = 0; while (i + 1 < n && T(i + 1) <= t) i++;
                var x = Vv(i); var y = Vv(i + 1);
                if (interp == 0) return x;
                float f = (float)((t - T(i)) / Math.Max(1.0, (double)T(i + 1) - T(i)));
                if (w == 4) { var q = Quaternion.Normalize(Quaternion.Slerp(new Quaternion(x[0], x[1], x[2], x[3]), new Quaternion(y[0], y[1], y[2], y[3]), f)); return new[] { q.X, q.Y, q.Z, q.W }; }
                return Enumerable.Range(0, w).Select(j => x[j] + (y[j] - x[j]) * f).ToArray();
            }
            var world = new Matrix4x4?[bones.Count];
            Matrix4x4 W(int i, int depth)
            {
                if (world[i] is Matrix4x4 have) return have;
                var b = bones[i];
                var pv = b.List("pivot").Select(o => Convert.ToSingle(o, CultureInfo.InvariantCulture)).ToArray();
                var t = Sample(b.GetValueOrDefault("t"), 3, new float[] { 0, 0, 0 }); var r = Sample(b.GetValueOrDefault("r"), 4, new float[] { 0, 0, 0, 1 }); var s = Sample(b.GetValueOrDefault("s"), 3, new float[] { 1, 1, 1 });
                var p = new Vector3(pv[0], pv[1], pv[2]);
                var local = Matrix4x4.CreateTranslation(-p) * Matrix4x4.CreateScale(s[0], s[1], s[2]) * Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(new Quaternion(r[0], r[1], r[2], r[3]))) * Matrix4x4.CreateTranslation(t[0], t[1], t[2]) * Matrix4x4.CreateTranslation(p);
                int par = b.Int("p");
                var m = par >= 0 && par < bones.Count && par != i && depth < 256 ? local * W(par, depth + 1) : local;
                world[i] = m; return m;
            }
            var outp = new List<double>();
            for (int i = 0; i < bones.Count; i++)
            {
                var m = W(i, 0);
                outp.AddRange(new double[] { m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44 });
            }
            return outp.ToArray();
        }

        // the page's pose at ms against MvPose: the largest difference (relative to the size of the value), the bone count both have
        static async Task<(double worst, int bones, string detail)> MvComparePose(ModelView v, byte[] file, double ms)
        {
            var js = await v.EvalForTest("mv.pose(" + ms.ToString(CultureInfo.InvariantCulture) + ")");
            var got = Json.Parse(js) as List<object>;
            var want = MvPose(file, ms);
            if (got == null || want == null || got.Count != want.Length) return (double.MaxValue, -1, "page " + (got?.Count ?? -1) + " values, C# " + (want?.Length ?? -1));
            double worst = 0; int at = -1;
            for (int i = 0; i < want.Length; i++)
            {
                double g = Convert.ToDouble(got[i], CultureInfo.InvariantCulture), d = Math.Abs(g - want[i]) / Math.Max(1, Math.Abs(want[i]));
                if (d > worst) { worst = d; at = i; }
            }
            return (worst, want.Length / 16, at < 0 ? "" : "bone " + at / 16 + " element " + at % 16 + ": page " + Convert.ToDouble(got[at], CultureInfo.InvariantCulture) + " C# " + want[at]);
        }

        static async Task<Dictionary<string, object>> MvState(ModelView v) => v == null ? null : Json.Obj(await v.EvalForTest("mv.st") ?? "null");
        static async Task<double> MvNum(ModelView v, string js) { var r = await v.EvalForTest(js); return double.TryParse(r, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN; }
        static double D(Dictionary<string, object> d, string k) => d != null && d.TryGetValue(k, out var o) && o != null ? Convert.ToDouble(o, CultureInfo.InvariantCulture) : double.NaN;

        async Task<bool> MvShown(Func<ModelView> v, int ms = 20000) => await Until(() => v()?.State == ModelView.ViewState.Shown, ms);

        // ============================================================================================================================ the unit part
        async Task ModelViewChecks(string work, Action<string, bool, string> Check, ClassicIndex baseline, List<string> notes)
        {
            var savedRoot = CreatureModels.RootOverride; var savedSrc = CreatureModels.SourceForTest; var savedBuild = CreatureModels.BuildForTest;
            var savedSeen = SeenData.Snapshot; var savedSeenSt = SeenData.Status;
            double w0 = Width, h0 = Height;
            try
            {
                CreatureModels.ResetForTest();
                CreatureModels.RootOverride = Path.Combine(work, "view", "atlas", "models");
                var (root, skin, _) = CmModel();
                var blp = ObjBlp();
                CreatureModels.SourceForTest = () => new FuncModelSource(f => f == 126487 ? root : f == 3001 ? skin : f == 5001 || f == 6001 ? blp : null);
                CreatureModels.BuildForTest = () => "wowf|1.60.1.70707|2417084bcafe";
                var set = new ModelSet { Displays = new Dictionary<int, CreatureDisplay>(), Models = new Dictionary<int, CreatureModelInfo>() };
                set.Displays[604] = new CreatureDisplay { Id = 604, ModelId = 43, Scale = 0.71f, Textures = new[] { 6001, 0, 0, 0 } };
                set.Displays[605] = new CreatureDisplay { Id = 605, ModelId = 43, Scale = 0.9f, Textures = new[] { 5001, 0, 0, 0 } };      // 3.6 step 5: another look
                set.Displays[3167] = new CreatureDisplay { Id = 3167, ModelId = 43, ExtendedId = 9, Scale = 1, Textures = new int[4] };
                set.Models[43] = new CreatureModelInfo { Id = 43, FileId = 126487, Scale = 1 };
                ClassicAtlas.Use(baseline);
                SeenData.SetForTest(null, SeenData.State.NotStarted);
                ModelData.SetForTest(set);

                ResetAtlasForTest();
                ShowTab("atlas");
                await AtlasView.WaitForTest();
                Width = 1800; Height = 1000;
                await AtlasView.Open("npc:69"); UpdateLayout();
                await Until(() => AtlasView.WideForTest);
                Check("model card: an NPC page with a display id (Timber Wolf 69, display 604 from the Classic baseline) has a Model card, 260 high",
                    AtlasView.ModelCardForTest != null && Math.Abs(AtlasView.ModelCardForTest.Height - ElansAddonHub.AtlasPage.ModelCardHeight) < 0.5 && AtlasView.ModelEntryForTest == 69, AtlasView.ModelStatusForTest);
                Check("model card: wide (1800) - at the top of the rail, the rail beside the tab", AtlasView.ModelCardInRailForTest && AtlasView.ColumnsForTest == 2 && AtlasView.RailShownForTest,
                    "columns " + AtlasView.ColumnsForTest + " in rail " + AtlasView.ModelCardInRailForTest);
                await AtlasView.ModelResolveForTest;
                Check("looks: a creature with one look (Timber Wolf 69) has no look switch", AtlasView.LookCountForTest == 0 && AtlasView.LooksForTest.Count == 1, "looks " + AtlasView.LooksForTest.Count + " buttons " + AtlasView.LookCountForTest);
                var mv = AtlasView.ModelViewForTest;
                bool shown = await MvShown(() => AtlasView.ModelViewForTest);
                var st = await MvState(mv);
                Check("viewer: the model is built and shown (\"Loading model...\" first, then the page's own state 'shown'; no status line over it)",
                    shown && st?.Str("state") == "shown" && AtlasView.ModelStatusForTest == "", mv?.State + " " + mv?.Reason + " status '" + AtlasView.ModelStatusForTest + "' page " + st?.Str("state") + " " + st?.Str("why"));
                if (!shown || mv == null || st == null) return;
                var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(mv);
                int fullW = (int)Math.Ceiling(mv.ActualWidth * dpi.DpiScaleX), fullH = (int)Math.Ceiling(mv.ActualHeight * dpi.DpiScaleY);
                await Until(() => mv.ClipForTest.Height > 0);
                Check("viewer: the HWND WebView2 (see ModelView: the composition control needs the Windows SDK projection); fully in view its window region is the whole view",
                    mv.WebForTest is Microsoft.Web.WebView2.Wpf.WebView2 && mv.ClipForTest.Y == 0 && Math.Abs(mv.ClipForTest.Height - fullH) <= 2 && Math.Abs(mv.ClipForTest.Width - fullW) <= 2,
                    "region " + mv.ClipForTest + " view " + fullW + " x " + fullH);
                Check("viewer: the mesh - 6 vertices, 6 indices, 2 bones (1 animated by the Stand track, 1 by a global sequence), 3 batches, the billboard bone counted",
                    D(st, "nv") == 6 && D(st, "ni") == 6 && D(st, "bones") == 2 && D(st, "animated") == 2 && D(st, "batches") == 3 && D(st, "billboards") == 1, Json.Write(st));
                await Until(() => false, 600);
                st = await MvState(mv);
                Check("viewer: both textures (5001 wrap 3, 6001 wrap 1) fetched from the models host and read (MTX1); the character skin batch is the stand-in colour",
                    D(st, "texWanted") == 2 && D(st, "texLoaded") == 2 && D(st, "texFailed") == 0, Json.Write(st));
                Check("viewer: the Stand loop - duration 2000 ms", D(st, "duration") == 2000, Json.Write(st));
                double changed = await MvNum(mv, "(function(){ mv.snap(); mv.step(250); return mv.changed(); })()");
                double adv = await MvNum(mv, "(function(){ var a = mv.st.animMs; mv.step(100); return mv.st.animMs - a; })()");
                Check("viewer: the clock moves on (100 ms step = 100 ms) and bone 0's matrix changes between two frames 250 ms apart", Math.Abs(adv - 100) < 1e-6 && changed >= 1, "advanced " + adv + " changed " + changed);
                Check("viewer: the shader's bone matrices are the page's own (identity bind pose and inverses)", (await mv.EvalForTest("mv.boneTexOk()")) == "true", null);
                var file = File.ReadAllBytes(CreatureModels.ModelPath(CreatureModels.KeyFor("wowf|1.60.1.70707|2417084bcafe"), 604));
                var cmp = new List<string>(); double worst = 0;
                foreach (var t in new[] { 0.0, 250, 730, 1999, 2600 }) { var c = await MvComparePose(mv, file, t); worst = Math.Max(worst, c.worst); cmp.Add(t + ": " + c.worst.ToString("0.0e0", CultureInfo.InvariantCulture) + " " + c.detail); }
                Check("viewer: the bone matrices at 0, 250, 730, 1999 and 2600 ms are the M2 rule (pivot, T R S, parent chain, slerp, the global sequence's own loop) worked out in C# from the same file",
                    worst < 1e-4, string.Join(" | ", cmp));
                double f0 = D(st, "frames"); await Until(() => false, 500); var stf = await MvState(mv);
                Check("viewer: frames are drawn while the card is visible", D(stf, "frames") > f0 && stf.Bool("paused") == false, f0 + " -> " + D(stf, "frames"));

                // ---- airspace: scrolled partly out of the page, the web view's window is cut to what is inside the page; all out = nothing and paused
                {
                    var sc = AtlasView.ScrollerForTest;
                    double minH = MinHeight; MinHeight = 300; Height = 360; await Settle(); await Until(() => false, 200);
                    double top = mv.TransformToAncestor(sc).Transform(new Point(0, 0)).Y + sc.VerticalOffset;
                    double cutDip = Math.Min(60, sc.ScrollableHeight - top);
                    if (cutDip < 20) notes.Add("skip airspace scroll: the page scrolls only " + sc.ScrollableHeight.ToString("0", CultureInfo.InvariantCulture) + " px (the card starts at " + top.ToString("0", CultureInfo.InvariantCulture) + ")");
                    else
                    {
                        sc.ScrollToVerticalOffset(top + cutDip); await Settle();
                        await Until(() => mv.ClipForTest.Y > 0, 2000);
                        var cut = mv.ClipForTest; int cutPx = (int)Math.Round(cutDip * dpi.DpiScaleY);
                        Check("airspace: scrolled " + cutDip.ToString("0", CultureInfo.InvariantCulture) + " px past the card's top, the web view's window region starts that far down (the part above the page's top edge is cut away), the rest stays",
                            Math.Abs(cut.Y - cutPx) <= 2 && Math.Abs(cut.Y + cut.Height - fullH) <= 2 && mv.InViewForTest && !mv.PausedForTest, "region " + cut + " expected y " + cutPx + " of " + fullH);
                        if (SelfTestClock.ShotsOn) { await Task.Delay(300); Snapshot(Path.Combine(ModelShots(work), "models-airspace-scrolled.png")); ScreenGrab(Path.Combine(ModelShots(work), "models-airspace-scrolled-screen.png")); }
                        if (sc.ScrollableHeight >= top + mv.ActualHeight + 2)
                        {
                            sc.ScrollToVerticalOffset(top + mv.ActualHeight + 2); await Settle();
                            bool outAll = await Until(() => mv.ClipForTest.Height == 0 && mv.PausedForTest, 2000);
                            Check("airspace: scrolled all the way past it - an empty region and the page paused", outAll, "region " + mv.ClipForTest + " paused " + mv.PausedForTest);
                        }
                        else notes.Add("skip airspace all-out: the page cannot scroll the card all the way out (" + sc.ScrollableHeight.ToString("0", CultureInfo.InvariantCulture) + " px)");
                    }
                    MinHeight = minH;
                    sc.ScrollToTop(); Height = 1000; await Settle();
                    await Until(() => mv.ClipForTest.Height >= fullH - 2 && !mv.PausedForTest, 2000);
                    // the palette is a WPF layer: the live view steps aside for its snapshot while it is open
                    await AtlasView.OpenPalette(); await AtlasView.PaletteOpenWaitForTest();
                    bool frozen = mv.FrozenForTest && mv.WebHiddenForTest && mv.FrozenImageShownForTest && await Until(() => mv.PausedForTest, 1000);
                    if (SelfTestClock.ShotsOn) { await Task.Delay(300); Snapshot(Path.Combine(ModelShots(work), "models-airspace-palette.png")); ScreenGrab(Path.Combine(ModelShots(work), "models-airspace-palette-screen.png")); }
                    AtlasView.ClosePalette(); await Settle();
                    bool thawed = await Until(() => !mv.FrozenForTest && !mv.WebHiddenForTest && !mv.PausedForTest, 2000);
                    Check("airspace: the Ctrl+K palette over the card - the live view is swapped for its snapshot (and paused) while it is open, live again after", frozen && thawed, "frozen " + frozen + " thawed " + thawed);
                }

                // ---- narrow: the same card (and viewer) moves into the Overview after the tooltip card
                Width = 1100;
                await Until(() => !AtlasView.WideForTest && AtlasView.ColumnsForTest == 1);
                await Until(() => AtlasView.ModelCardOverviewIndexForTest >= 0);
                Check("model card: narrow (1100) - in the Overview right after the tooltip card (index 1), the rail gone, the same viewer",
                    !AtlasView.WideForTest && AtlasView.ModelCardOverviewIndexForTest == 1 && !AtlasView.RailShownForTest && ReferenceEquals(AtlasView.ModelViewForTest, mv),
                    "index " + AtlasView.ModelCardOverviewIndexForTest + " wide " + AtlasView.WideForTest);
                await Until(() => false, 400);
                var st2 = await MvState(mv); double fr2 = D(st2, "frames"); await Until(() => false, 400); var st3 = await MvState(mv);
                Check("viewer: moved into the Overview it keeps its page (still shown, still drawing - one viewer per page)", st3?.Str("state") == "shown" && D(st3, "frames") > fr2 && mv.HasWebForTest, Json.Write(st3));
                if (SelfTestClock.ShotsOn) { await Task.Delay(400); Snapshot(Path.Combine(ModelShots(work), "models-card-narrow.png")); }
                Width = 1800; await Until(() => AtlasView.ModelCardInRailForTest);
                Check("model card: wide again - back at the top of the rail", AtlasView.ModelCardInRailForTest && ReferenceEquals(AtlasView.ModelViewForTest, mv), null);
                if (SelfTestClock.ShotsOn) { await Task.Delay(400); Snapshot(Path.Combine(ModelShots(work), "models-card-wide.png")); }

                // ---- pause while not visible
                ShowTab("inventory"); await Settle();
                bool pausedHidden = await Until(() => mv.PausedForTest, 2000);
                var sp = await MvState(mv); await Until(() => false, 300); var sp2 = await MvState(mv);
                Check("viewer: another app page shown = the viewer pauses (the page's loop stops: no frames)", pausedHidden && sp.Bool("paused") && D(sp2, "frames") == D(sp, "frames"), "paused " + pausedHidden + " frames " + D(sp, "frames") + " -> " + D(sp2, "frames"));
                ShowTab("atlas"); await Settle();
                bool resumed = await Until(() => !mv.PausedForTest, 2000);
                var sr = await MvState(mv); await Until(() => false, 300); var sr2 = await MvState(mv);
                Check("viewer: back on the Atlas = it draws again", resumed && !sr2.Bool("paused") && D(sr2, "frames") > D(sr, "frames"), "resumed " + resumed);

                // ---- 3.6 step 5: the look switch
                await MvLookChecks(work, Check, notes);
                // ---- 3.7: the item pages' Model card (MainWindow.ModelsTest.Items.cs)
                try { await MvItemChecks(work, set, Check, notes); } catch (Exception e) { Check("item view checks crashed", false, e.ToString()); }
                mv = await MvBackToWolf(mv);
                if (mv == null) { Check("looks: back on the Timber Wolf's page its model is shown again", false, null); return; }

                // ---- the sandbox
                await mv.EvalForTest("window.__fx = 'pending'; fetch('https://example.com/x').then(function () { window.__fx = 'fetched'; }, function () { window.__fx = 'refused'; }); 1");
                await Until(() => false, 600);
                var fx = await mv.EvalForTest("window.__fx");
                await mv.EvalForTest("try { location.href = 'https://example.com/'; } catch (e) { }");
                await Until(() => false, 400);
                var host = await mv.EvalForTest("location.host");
                int ref0 = mv.RefusedForTest.Count;
                mv.Handle("{\"t\":\"navigate\",\"url\":\"https://example.com\"}"); mv.Handle("[1,2]"); mv.Handle(new string('x', 5000));
                Check("sandbox: another host is refused (fetch fails), navigating away is cancelled (still models.outpost.local), unknown / malformed / oversized messages are refused",
                    fx == "\"refused\"" && host == "\"" + ModelViewerPage.Host + "\"" && mv.RefusedForTest.Count == ref0 + 3 && !ModelView.IsOwnUrl("https://worldmap.outpost.local/_page/index.html") && ModelView.IsOwnUrl(ModelViewerPage.PageUrl)
                    && mv.CoreForTest.Settings.AreDevToolsEnabled == false && mv.CoreForTest.Settings.AreHostObjectsAllowed == false,
                    "fetch " + fx + " host " + host + " refused " + string.Join(",", mv.RefusedForTest) + " blocked " + string.Join(", ", mv.BlockedUrlsForTest));
                Check("files: the page and three.js are written once under <models>\\_page (one three.min.js resource in the exe), and a new game build's clean-up leaves _page alone",
                    File.Exists(Path.Combine(ModelViewerPage.Dir, "viewer.html")) && File.Exists(Path.Combine(ModelViewerPage.Dir, "three.min.js")) && ModelViewerPage.Resource("ModelViewer.three.min.js") == null
                    && CreatureModels.RemoveOtherBuilds("nothing_m1") >= 1 && Directory.Exists(ModelViewerPage.Dir), null);
                CreatureModels.ResetForTest();

                // ---- leaving the page lets the browser go
                await AtlasView.Open("npc:71"); UpdateLayout();
                Check("model card: leaving the page releases the viewer's browser; a creature with no display id (71) has no card", !mv.HasWebForTest && AtlasView.ModelCardForTest == null, mv.HasWebForTest + " " + (AtlasView.ModelCardForTest != null));

                // ---- a character model: no card
                await AtlasView.Open("npc:68"); UpdateLayout();
                Check("model card: a character model (Stormwind City Guard 68, display 3167 with ExtendedDisplayInfoID) has no card", AtlasView.ModelCardForTest == null && AtlasView.ModelViewForTest == null, null);

                // ---- the calm lines
                await AtlasView.Open("npc:70"); UpdateLayout(); await AtlasView.ModelResolveForTest;
                Check("model card: displays the game does not know (70: 11, 12, 13) - one calm line, no viewer started", AtlasView.ModelStatusForTest == "There is no model for this creature in this game build." && AtlasView.ModelViewForTest?.HasWebForTest == false, AtlasView.ModelStatusForTest);
                ModelData.SetForTest(null, ModelData.State.Unavailable, "game updated (test)");
                await AtlasView.Open("npc:69"); UpdateLayout(); await AtlasView.ModelResolveForTest;
                Check("model card: model data unavailable - a short reason with the why", AtlasView.ModelStatusForTest.StartsWith("The game's model data is not available") && AtlasView.ModelStatusForTest.Contains("game updated (test)"), AtlasView.ModelStatusForTest);
                ModelData.SetForTest(null, ModelData.State.Disabled);
                await AtlasView.Open("npc:71"); await AtlasView.Open("npc:69"); UpdateLayout(); await AtlasView.ModelResolveForTest;
                Check("model card: game data switched off - says so", AtlasView.ModelStatusForTest.Contains("switched off"), AtlasView.ModelStatusForTest);
                ModelData.SetForTest(null, ModelData.State.Loading);
                await AtlasView.Open("npc:68"); UpdateLayout();
                bool waiting = AtlasView.ModelCardForTest != null && AtlasView.ModelStatusForTest == "Loading model...";
                ModelData.SetForTest(set); ModelData.RaiseChangedForTest();
                await AtlasView.ModelResolveForTest; UpdateLayout();
                Check("model card: while the tables are read a card says \"Loading model...\"; once they say character model, the card goes", waiting && AtlasView.ModelCardForTest == null, "waiting " + waiting);
                ModelView.ForceNoWebViewForTest = true;
                await AtlasView.Open("npc:69"); UpdateLayout(); await AtlasView.ModelResolveForTest;
                await Until(() => AtlasView.ModelStatusForTest.Contains("WebView2"), 5000);
                Check("model card: no WebView2 runtime - one line that the viewer needs it", AtlasView.ModelStatusForTest.Contains("needs Microsoft Edge WebView2"), AtlasView.ModelStatusForTest);
                ModelView.ForceNoWebViewForTest = false;
            }
            finally
            {
                ModelView.ForceNoWebViewForTest = false;
                Width = w0; Height = h0;
                try { ResetAtlasForTest(); } catch (Exception) { }
                CreatureModels.RootOverride = savedRoot; CreatureModels.SourceForTest = savedSrc; CreatureModels.BuildForTest = savedBuild; CreatureModels.ResetForTest();
                SeenData.SetForTest(savedSeen, savedSeenSt);
            }
        }

        // 3.6 step 5: "Two Looks" (73: 604 at 30 %, 605 at 70 %) and "One Look" (74: 604 beside the character model 3167)
        async Task MvLookChecks(string work, Action<string, bool, string> Check, List<string> notes)
        {
            await AtlasView.Open("npc:73"); UpdateLayout();
            await AtlasView.ModelResolveForTest;
            bool shown = await MvShown(() => AtlasView.ModelViewForTest);
            var v = AtlasView.ModelViewForTest;
            var st = await MvState(v);
            string m605 = v?.ModelForTest ?? "";
            Check("looks: two usable looks = a switch in the card header, labelled 1 and 2, each with its chance as a tooltip (\"Seen 30 % of the time\", \"Seen 70 % of the time\")",
                shown && AtlasView.LookCountForTest == 2 && AtlasView.LookSwitchShownForTest && AtlasView.LookLabelsForTest.SequenceEqual(new[] { "1", "2" })
                && AtlasView.LookTipsForTest.SequenceEqual(new[] { "Seen 30 % of the time", "Seen 70 % of the time" }),
                "shown " + shown + " buttons " + AtlasView.LookCountForTest + " tips " + string.Join(" / ", AtlasView.LookTipsForTest) + " " + v?.State + " " + v?.Reason);
            Check("looks: the first look shown is the highest chance (look 2, display 605) - chosen in the switch and loaded in the viewer",
                AtlasView.LookIndexForTest == 1 && AtlasView.LookCheckedForTest == 1 && m605.EndsWith("/m/605.m2b2") && st?.Str("model") == m605 && st?.Str("state") == "shown",
                "index " + AtlasView.LookIndexForTest + " checked " + AtlasView.LookCheckedForTest + " model " + m605 + " page " + st?.Str("model"));
            if (!shown || v == null) return;
            if (SelfTestClock.ShotsOn) { await Task.Delay(400); var png = await v.CaptureForTest(); SnapshotWithViewer(Path.Combine(ModelShots(work), "models-card-looks.png"), v, png); }
            var web0 = v.WebForTest; int made0 = v.WebCreatedForTest, loads0 = v.LoadPostsForTest;
            double zoomSet = await MvNum(v, "mv.zoom(1.6)");
            v.ReceivedForTest.Clear();
            AtlasView.PickLookForTest(0);
            await AtlasView.ModelSwitchForTest;
            bool switched = await Until(() => v.State == ModelView.ViewState.Shown && v.ModelForTest.EndsWith("/m/604.m2b2"), 15000);
            await Until(() => false, 200);
            var st2 = await MvState(v);
            var vw = Json.Obj(await v.EvalForTest("mv.viewOf()") ?? "null");
            Check("looks: choosing look 1 builds display 604 and loads that file into the same web view through a \"load\" message (no new web view, the page's loads count 1, its state names the new file)",
                switched && ReferenceEquals(v.WebForTest, web0) && v.WebCreatedForTest == made0 && v.LoadPostsForTest == loads0 + 1 && D(st2, "loads") == 1 && st2?.Str("model") == v.ModelForTest && st2?.Str("state") == "shown"
                && File.Exists(CreatureModels.ModelPath(CreatureModels.KeyFor("wowf|1.60.1.70707|2417084bcafe"), 604)),
                "switched " + switched + " model " + v.ModelForTest + " web same " + ReferenceEquals(v.WebForTest, web0) + " made " + v.WebCreatedForTest + " loads " + v.LoadPostsForTest + " page " + Json.Write(st2));
            Check("looks: the switch keeps the view (zoom 1.6 stays) and the mesh is the new file's; no status line over it, look 1 chosen",
                zoomSet == 1.6 && Math.Abs(D(vw, "zoom") - 1.6) < 1e-9 && D(st2, "nv") == 6 && AtlasView.ModelStatusForTest == "" && AtlasView.LookIndexForTest == 0 && AtlasView.LookCheckedForTest == 0,
                "zoom " + zoomSet + " -> " + D(vw, "zoom") + " status '" + AtlasView.ModelStatusForTest + "' index " + AtlasView.LookIndexForTest);
            int refused0 = v.RefusedForTest.Count;
            v.Handle("{\"t\":\"state\",\"state\":\"error\",\"why\":\"late\",\"model\":" + Json.Write(m605) + "}");
            v.Handle("{\"t\":\"state\",\"state\":\"shown\",\"model\":\"../../x.m2b2\"}");
            Check("looks: a late answer about the look before is dropped (stale) and a malformed model name is refused; the view stays shown",
                v.RefusedForTest.Count == refused0 + 2 && v.RefusedForTest.Skip(refused0).SequenceEqual(new[] { "stale", "model" }) && v.State == ModelView.ViewState.Shown, string.Join(",", v.RefusedForTest.Skip(refused0)) + " " + v.State);
            AtlasView.PickLookForTest(1);
            await AtlasView.ModelSwitchForTest;
            bool back = await Until(() => v.State == ModelView.ViewState.Shown && v.ModelForTest == m605, 15000);
            var st3 = await MvState(v);
            Check("looks: and back to look 2 (the page's loads count 2, still the same web view)", back && D(st3, "loads") == 2 && st3?.Str("model") == m605 && v.WebCreatedForTest == made0, Json.Write(st3));

            // a creature with one usable look beside a character model: no switch
            await AtlasView.Open("npc:74"); UpdateLayout();
            await AtlasView.ModelResolveForTest;
            bool one = await MvShown(() => AtlasView.ModelViewForTest);
            Check("looks: one usable look beside a character model (74: 604 and 3167) = the model, no switch; and the card remembers nothing (look state reset)",
                one && AtlasView.LookCountForTest == 0 && AtlasView.LooksForTest.Count == 1 && AtlasView.ModelViewForTest.ModelForTest.EndsWith("/m/604.m2b2") && AtlasView.LookIndexForTest == 0,
                "shown " + one + " buttons " + AtlasView.LookCountForTest + " looks " + AtlasView.LooksForTest.Count);
        }

        // after the look checks: the Timber Wolf's page again (a new viewer: the sandbox checks use it)
        async Task<ModelView> MvBackToWolf(ModelView old)
        {
            await AtlasView.Open("npc:69"); UpdateLayout();
            await AtlasView.ModelResolveForTest;
            return await MvShown(() => AtlasView.ModelViewForTest) ? AtlasView.ModelViewForTest : null;
        }

        async Task ModelsRealLookChecks(string shots, CheckFn Check, List<string> notes)
        {
            var I = CultureInfo.InvariantCulture;
            int npc = LookNpc;
            await AtlasView.Open("npc:" + npc.ToString(I)); UpdateLayout();
            await AtlasView.ModelResolveForTest;
            bool ok = await MvShown(() => AtlasView.ModelViewForTest, 30000);
            var pv = AtlasView.ModelViewForTest;
            var looks = AtlasView.LooksForTest;
            if (!ok || pv == null) { Check("real looks: Reef Shark (npc " + npc + ") shows its model", false, pv?.State + " " + pv?.Reason + " / " + AtlasView.ModelStatusForTest); return; }
            var st = await MvState(pv);
            Check("real looks: Reef Shark (npc " + npc + ") - a switch of " + AtlasView.LookCountForTest + " looks in the card header, each \"Seen 25 % of the time\", the first chosen (equal chances) and shown",
                AtlasView.LookCountForTest == looks.Count && looks.Count >= 2 && AtlasView.LookSwitchShownForTest && AtlasView.LookTipsForTest.All(t => t == "Seen 25 % of the time") && AtlasView.LookIndexForTest == 0
                && pv.ModelForTest.EndsWith("/m/" + looks[0].DisplayId.ToString(I) + ".m2b2") && st?.Str("state") == "shown",
                "looks " + string.Join(", ", looks) + " buttons " + AtlasView.LookCountForTest + " tips " + string.Join(" / ", AtlasView.LookTipsForTest) + " model " + pv.ModelForTest);
            if (SelfTestClock.ShotsOn)
            {
                await Task.Delay(600);
                var png = await pv.CaptureForTest();
                SnapshotWithViewer(Path.Combine(shots, "models-page-looks.png"), pv, png);
            }
            // another look: one with another model file if there is one (another mesh), else the next
            int pick = 1;
            for (int i = 1; i < looks.Count; i++) if (looks[i].Model.ModelFileId != looks[0].Model.ModelFileId) { pick = i; break; }
            var web0 = pv.WebForTest; int made0 = pv.WebCreatedForTest;
            string m0 = pv.ModelForTest; double nv0 = D(st, "nv");
            AtlasView.PickLookForTest(pick);
            await AtlasView.ModelSwitchForTest;
            string want = "/m/" + looks[pick].DisplayId.ToString(I) + ".m2b2";
            bool switched = await Until(() => pv.State == ModelView.ViewState.Shown && pv.ModelForTest.EndsWith(want), 30000);
            Dictionary<string, object> st2 = null;
            var tw = System.Diagnostics.Stopwatch.StartNew();
            while (tw.ElapsedMilliseconds < 15000) { st2 = await MvState(pv); if (st2?.Str("model") == pv.ModelForTest && D(st2, "texLoaded") + D(st2, "texFailed") >= D(st2, "texWanted")) break; await Task.Delay(100); }
            var path = Path.Combine(CreatureModels.Root, pv.ModelForTest.Replace('/', Path.DirectorySeparatorChar));
            var meta = File.Exists(path) ? CreatureModel.ReadMeta(File.ReadAllBytes(path), out _) : null;
            Check("real looks: look " + (pick + 1) + " (display " + looks[pick].DisplayId + ", model file " + looks[pick].Model.ModelFileId + " vs " + looks[0].Model.ModelFileId + ") is built and loaded into the same web view - the page's mesh is the new file's ("
                + D(st2, "nv") + " vertices, was " + nv0 + "), its textures in",
                switched && looks[pick].Model.ModelFileId != looks[0].Model.ModelFileId && st2?.Str("model") == pv.ModelForTest && pv.ModelForTest != m0 && meta != null && D(st2, "nv") == meta.Int("nv") && D(st2, "ni") == meta.Int("ni") && D(st2, "loads") == 1
                && ReferenceEquals(pv.WebForTest, web0) && pv.WebCreatedForTest == made0 && D(st2, "texFailed") == 0 && AtlasView.ModelStatusForTest == "",
                "switched " + switched + " " + m0 + " -> " + pv.ModelForTest + " page " + Json.Write(st2));
            notes.Add("info real looks: Reef Shark " + string.Join(", ", looks) + "; switched to look " + (pick + 1) + ": " + Json.Write(st2));
            if (SelfTestClock.ShotsOn)
            {
                await Task.Delay(600);
                var png = await pv.CaptureForTest();
                SnapshotWithViewer(Path.Combine(shots, "models-page-looks-switched.png"), pv, png);
            }
        }

        // the screen itself under the window (only meaningful with -Visible: the self-test's own desktop is not drawn) - the airspace as the user sees it
        void ScreenGrab(string file)
        {
            try
            {
                var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
                int x = (int)(Left * dpi.DpiScaleX), y = (int)(Top * dpi.DpiScaleY), w = (int)(ActualWidth * dpi.DpiScaleX), h = (int)(ActualHeight * dpi.DpiScaleY);
                using (var bmp = new System.Drawing.Bitmap(Math.Max(1, w), Math.Max(1, h)))
                {
                    using (var g = System.Drawing.Graphics.FromImage(bmp)) g.CopyFromScreen(x, y, 0, 0, bmp.Size);
                    bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
                }
            }
            catch (Exception e) { Util.Log("models test screen grab: " + e.Message); }
        }

        // a window picture with the viewer drawn in: RenderTargetBitmap cannot see the web view's own window, so its capture is laid over its place
        void SnapshotWithViewer(string file, ModelView v, byte[] png)
        {
            UpdateLayout();
            if (!SelfTestClock.ShotsOn) { SelfTestClock.ShotsSkipped++; return; }
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(ActualWidth * dpi.DpiScaleX), (int)(ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(this);
            var dv = new System.Windows.Media.DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawImage(rtb, new Rect(0, 0, ActualWidth, ActualHeight));
                if (png != null && v != null && v.IsVisible)
                {
                    var img = new System.Windows.Media.Imaging.BitmapImage();
                    img.BeginInit(); img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; img.StreamSource = new MemoryStream(png); img.EndInit();
                    var at = v.TransformToAncestor(this).TransformBounds(new Rect(0, 0, v.ActualWidth, v.ActualHeight));
                    dc.DrawImage(img, at);
                }
            }
            var outBmp = new System.Windows.Media.Imaging.RenderTargetBitmap(rtb.PixelWidth, rtb.PixelHeight, dpi.PixelsPerInchX, dpi.PixelsPerInchY, System.Windows.Media.PixelFormats.Pbgra32);
            outBmp.Render(dv);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(outBmp));
            using (var fs = File.Create(file)) enc.Save(fs);
            SelfTestClock.ShotsTaken++;
        }

        // 3.7 (format 2): Aiden's boy model (262), HUMSCitizenMid (21), the Scholomance students' HighElfFemale_Mage (11161), DeathGuard (1021) and the Twin Emperors (15761, 6.8 units
        // tall) in a 340 x 200 viewer: every vertex of the drawn mesh, posed at four times of the Stand loop and turned to eight angles, inside the view; the
        // model's bottom is its feet. Pictures (models-feet-*-after.png; with the format-1 rule, models-feet-*-before.png) when pictures are on.
        async Task ModelsRealFeetViewChecks(string shots, CheckFn Check, List<string> notes)
        {
            var I = CultureInfo.InvariantCulture;
            var feet = new[] { (262, "aiden"), (21, "citizen"), (11161, "scholomance"), (1021, "deathguard"), (15761, "twinemperor") };
            var v = new ModelView { Width = 340, Height = 200 };
            var host = new Window { Width = 360, Height = 220, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Content = v, Title = "feet test" };
            host.Show();
            try
            {
                var rows = new List<string>(); bool allFit = true, allShown = true;
                foreach (var (display, slug) in feet)
                {
                    var r = await CreatureModels.EnsureAsync(display);
                    if (!r.Ok) { allShown = false; rows.Add(slug + ": " + r.Reason); continue; }
                    v.Show(r.Path);
                    bool ok = await Until(() => v.State == ModelView.ViewState.Shown || v.State == ModelView.ViewState.Failed || v.State == ModelView.ViewState.NoWebView, 30000) && v.State == ModelView.ViewState.Shown;
                    Dictionary<string, object> st = null; var tw = System.Diagnostics.Stopwatch.StartNew();
                    while (ok && tw.ElapsedMilliseconds < 15000) { st = await MvState(v); if (st?.Str("model") == v.ModelForTest && D(st, "texLoaded") + D(st, "texFailed") >= D(st, "texWanted")) break; await Task.Delay(100); }
                    if (!ok || st?.Str("state") != "shown") { allShown = false; rows.Add(slug + ": " + v.State + " " + v.Reason); continue; }
                    double dur = D(st, "duration"), x0 = 9, x1 = -9, y0 = 9, y1 = -9, bx0 = 9, by0 = 9, by1 = -9;
                    foreach (var t in new[] { 0.0, dur * 0.25, dur * 0.5, dur * 0.75 })
                    {
                        await v.EvalForTest("mv.pose(" + t.ToString("0", I) + ")");
                        foreach (var yaw in new[] { 0.0, 0.8, 1.6, 2.4, 3.2, 4.0, 4.8, 5.6 })
                        {
                            var b = Json.Obj(await v.EvalForTest("mv.screenMesh(" + yaw.ToString(I) + ")") ?? "null");
                            var bb = Json.Obj(await v.EvalForTest("mv.screenBox(" + yaw.ToString(I) + ")") ?? "null");
                            if (b == null || bb == null) { x0 = -99; continue; }
                            x0 = Math.Min(x0, D(b, "x0")); x1 = Math.Max(x1, D(b, "x1")); y0 = Math.Min(y0, D(b, "y0")); y1 = Math.Max(y1, D(b, "y1"));
                            bx0 = Math.Min(bx0, D(bb, "x0")); by0 = Math.Min(by0, D(bb, "y0")); by1 = Math.Max(by1, D(bb, "y1"));
                        }
                    }
                    bool fit = x0 >= -1 && x1 <= 1 && y0 >= -1 && y1 <= 1 && y1 - y0 > 1.0;
                    allFit &= fit;
                    rows.Add(slug + " " + display + ": drawn mesh x " + x0.ToString("0.00", I) + " .. " + x1.ToString("0.00", I) + ", y " + y0.ToString("0.00", I) + " .. " + y1.ToString("0.00", I)
                        + " (bind box y " + by0.ToString("0.00", I) + " .. " + by1.ToString("0.00", I) + ")" + (fit ? "" : " NOT IN VIEW"));
                    if (SelfTestClock.ShotsOn)
                    {
                        await v.EvalForTest("mv.pose(0)"); await v.EvalForTest("mv.opening(true)");
                        await Task.Delay(300);
                        var png = await v.CaptureForTest(); if (png != null) File.WriteAllBytes(Path.Combine(shots, "models-feet-" + slug + "-after.png"), png);
                    }
                }
                Check("real viewer (format 2): tall character-like creatures and a child (Aiden, citizen, Scholomance student, Death Guard, Twin Emperor) in the card's 340 x 200 - every drawn vertex inside the view at 8 turns x 4 Stand times",
                    allShown && allFit, string.Join(" | ", rows));
                foreach (var r in rows) notes.Add("info real viewer (feet framing): " + r);

                // the "before" pictures: the same displays built with the format-1 rule into a folder of their own
                if (SelfTestClock.ShotsOn)
                {
                    var savedRoot = CreatureModels.RootOverride;
                    try
                    {
                        CreatureModel.NoFeetForTest = true;
                        CreatureModels.ResetForTest();
                        CreatureModels.RootOverride = savedRoot + "-before";
                        // a web view of its own: a view maps its folder (CreatureModels.Root) once, when it starts
                        try { v.Dispose(); host.Close(); } catch (Exception) { }
                        v = new ModelView { Width = 340, Height = 200 };
                        host = new Window { Width = 360, Height = 220, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Content = v, Title = "feet test (before)" };
                        host.Show();
                        foreach (var (display, slug) in feet.Take(2))
                        {
                            var r = await CreatureModels.EnsureAsync(display);
                            if (!r.Ok) continue;
                            v.Show(r.Path);
                            if (!await Until(() => v.State == ModelView.ViewState.Shown || v.State == ModelView.ViewState.Failed, 30000)) continue;
                            var tw = System.Diagnostics.Stopwatch.StartNew();
                            while (tw.ElapsedMilliseconds < 15000) { var st = await MvState(v); if (st?.Str("model") == v.ModelForTest && D(st, "texLoaded") + D(st, "texFailed") >= D(st, "texWanted")) break; await Task.Delay(100); }
                            await v.EvalForTest("mv.pose(0)"); await v.EvalForTest("mv.opening(true)");
                            await Task.Delay(300);
                            var bm = Json.Obj(await v.EvalForTest("mv.st") ?? "null");
                            notes.Add("info real viewer (feet, before): " + slug + " " + bm?.Str("model") + " nv " + D(bm, "nv"));
                            var png = await v.CaptureForTest(); if (png != null) File.WriteAllBytes(Path.Combine(shots, "models-feet-" + slug + "-before.png"), png);
                        }
                    }
                    finally { CreatureModel.NoFeetForTest = false; CreatureModels.ResetForTest(); CreatureModels.RootOverride = savedRoot; }
                }
            }
            finally { try { v.Dispose(); host.Close(); } catch (Exception) { } }
        }

        // where the model pictures go: ELANSHUB_MODELS_SHOTS=<folder>, else the section's scratch folder
        static string ModelShots(string work)
        {
            var env = Environment.GetEnvironmentVariable("ELANSHUB_MODELS_SHOTS");
            var d = string.IsNullOrEmpty(env) ? Path.Combine(work, "shots") : env;
            Directory.CreateDirectory(d);
            return d;
        }

        // ============================================================================================================================ the real part (modelsreal)
        // the Timber Wolf (604) and Ragnaros (11121) built from the real files: first in a viewer of its own in a small window off the screen, then on their
        // NPC pages (69, 11502) in the Atlas with the Classic baseline; pictures with ELANSHUB_SHOTS=1 / ELANSHUB_MODELS_SHOTS=<folder>
        async Task ModelsRealViewChecks(string real, string flv, string work, CheckFn Check, List<string> notes)
        {
            var I = CultureInfo.InvariantCulture;
            var savedRoot = CreatureModels.RootOverride; var savedSrc = CreatureModels.SourceForTest; var savedBuild = CreatureModels.BuildForTest;
            double w0 = Width, h0 = Height;
            Window host = null;
            try
            {
                CreatureModels.ResetForTest();
                CreatureModels.RootOverride = Path.Combine(work, "atlas", "models");
                CreatureModels.SourceForTest = () => new CascModelSource(CascShared.Open(real, flv));
                CreatureModels.BuildForTest = () => ItemData.GameBuild(real, flv);
                var shots = ModelShots(work);
                var two = new[] { (604, 69, "Timber Wolf", "wolf"), (11121, 11502, "Ragnaros", "ragnaros") };

                // ---- the viewer alone, off the screen
                var v = new ModelView { Width = 340, Height = 220 };
                host = new Window { Width = 360, Height = 240, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Content = v, Title = "model test" };
                host.Show();
                foreach (var (display, npc, name, slug) in two)
                {
                    var r = await CreatureModels.EnsureAsync(display);
                    if (!r.Ok) { Check("real viewer: " + name + " " + display + " has a model file", false, r.Reason); continue; }
                    var file = File.ReadAllBytes(r.Path);
                    var meta = CreatureModel.ReadMeta(file, out _);
                    int nb = meta.List("bones").Count, ntex = meta.List("textures").Count;
                    v.Show(r.Path);
                    bool ok = await Until(() => v.State == ModelView.ViewState.Shown || v.State == ModelView.ViewState.Failed || v.State == ModelView.ViewState.NoWebView, 30000) && v.State == ModelView.ViewState.Shown;
                    if (!ok) { Check("real viewer: " + name + " is shown", false, v.State + " " + v.Reason); continue; }
                    await Until(() => false, 50);
                    var tw = System.Diagnostics.Stopwatch.StartNew(); Dictionary<string, object> st = null;
                    while (tw.ElapsedMilliseconds < 15000) { st = await MvState(v); if (D(st, "texLoaded") + D(st, "texFailed") >= D(st, "texWanted")) break; await Task.Delay(100); }
                    Check("real viewer: " + name + " " + display + " - the mesh (" + D(st, "nv") + " vertices, " + D(st, "ni") / 3 + " triangles, " + D(st, "batches") + " batches), bones as in the file (" + nb + ")",
                        st?.Str("state") == "shown" && D(st, "nv") == meta.Int("nv") && D(st, "ni") == meta.Int("ni") && D(st, "bones") == nb && D(st, "batches") > 0, Json.Write(st));
                    Check("real viewer: " + name + " - every texture fetched and read (" + D(st, "texLoaded") + " of " + D(st, "texWanted") + ", " + ntex + " in the file" + (D(st, "texDecoded") > 0 ? ", decoded without s3tc" : "") + ")",
                        D(st, "texWanted") >= 1 && D(st, "texLoaded") == D(st, "texWanted") && D(st, "texFailed") == 0, Json.Write(st));
                    double adv = await MvNum(v, "(function(){ var a = mv.st.animMs; mv.step(120); return mv.st.animMs - a; })()");
                    double changed = await MvNum(v, "(function(){ mv.snap(); mv.step(200); return mv.changed(); })()");
                    var cmp = new List<string>(); double worst = 0; int nbp = -1;
                    double dur = D(st, "duration");
                    foreach (var t in new[] { 0.0, 333, dur * 0.5, dur - 1, dur * 1.7 + 41 }) { var c = await MvComparePose(v, file, t); worst = Math.Max(worst, c.worst); nbp = c.bones; cmp.Add(t.ToString("0", I) + ": " + c.worst.ToString("0.0e0", I) + " " + c.detail); }
                    Check("real viewer: " + name + " - the Stand clock moves (120 ms), " + changed + " of " + nb + " bone matrices change between two frames 200 ms apart, the shader's matrices are the page's",
                        Math.Abs(adv - 120) < 1e-6 && changed >= 1 && (await v.EvalForTest("mv.boneTexOk()")) == "true", "advanced " + adv + " changed " + changed);
                    Check("real viewer: " + name + " - every bone's matrix at five times of the Stand loop (" + dur.ToString("0", I) + " ms) is the M2 rule worked out in C# from the file (worst " + worst.ToString("0.0e0", I) + ")",
                        nbp == nb && worst < 1e-3, string.Join(" | ", cmp));
                    await v.EvalForTest("mv.pose(" + (dur * 0.3).ToString("0", I) + ")");
                    var pr = Json.Obj(await v.EvalForTest("mv.probe()"));
                    Check("real viewer: " + name + " - a drawn frame is not blank (" + (D(pr, "frac") * 100).ToString("0.0", I) + " % of " + D(pr, "w") + " x " + D(pr, "h") + " px not the card colour)",
                        D(pr, "w") > 100 && D(pr, "frac") > 0.03 && D(pr, "frac") < 0.98, Json.Write(pr));
                    notes.Add("info real viewer: " + name + ": " + Json.Write(st) + (await v.EvalForTest("mv.hermite()") == "true" ? "; hermite / bezier keys read as linear" : ""));
                    if (SelfTestClock.ShotsOn) { var png = await v.CaptureForTest(); if (png != null) File.WriteAllBytes(Path.Combine(shots, "models-viewer-" + slug + ".png"), png); }
                }
                v.Dispose(); host.Close(); host = null;

                // ---- 3.7 (format 2): tall character-like creatures in the card's own size (340 x 200): the whole drawn mesh on the screen at every turn and
                // through the Stand loop (hypothesis "the framing cuts the feet off" - checked, the feet were missing geosets); pictures with their feet
                try { await ModelsRealFeetViewChecks(shots, Check, notes); } catch (Exception e) { Check("real feet view checks crashed", false, e.ToString()); }

                // ---- on their NPC pages
                ResetAtlasForTest();
                ShowTab("atlas");
                await AtlasView.WaitForTest();
                Width = 1800; Height = 1000;
                foreach (var (display, npc, name, slug) in two)
                {
                    await AtlasView.Open("npc:" + npc.ToString(I)); UpdateLayout();
                    await AtlasView.ModelResolveForTest;
                    bool ok = await MvShown(() => AtlasView.ModelViewForTest, 30000);
                    var pv = AtlasView.ModelViewForTest;
                    if (!ok || pv == null) { Check("real page: " + name + " (npc " + npc + ") shows its model at the top of the rail", false, pv?.State + " " + pv?.Reason + " / " + AtlasView.ModelStatusForTest); continue; }
                    var tw = System.Diagnostics.Stopwatch.StartNew(); Dictionary<string, object> st = null;
                    while (tw.ElapsedMilliseconds < 15000) { st = await MvState(pv); if (D(st, "texLoaded") + D(st, "texFailed") >= D(st, "texWanted")) break; await Task.Delay(100); }
                    double f0 = D(st, "frames"), a0 = D(st, "animMs");
                    await Until(() => false, 700);
                    var st2 = await MvState(pv);
                    Check("real page: " + name + " (npc " + npc + ") - the model at the top of the rail, its textures in, drawing and animating on its own (frames " + f0 + " -> " + D(st2, "frames") + ")",
                        AtlasView.ModelCardInRailForTest && D(st, "texFailed") == 0 && D(st2, "frames") > f0 && D(st2, "animMs") > a0, Json.Write(st2));
                    if (SelfTestClock.ShotsOn)
                    {
                        await Task.Delay(500);
                        var png = await pv.CaptureForTest(); if (png != null) File.WriteAllBytes(Path.Combine(shots, "models-page-" + slug + "-viewer.png"), png);
                        SnapshotWithViewer(Path.Combine(shots, "models-page-" + slug + ".png"), pv, png);
                    }
                }

                // ---- 3.6 step 5: the look switch on a real page (Reef Shark, four looks at 25 %)
                await ModelsRealLookChecks(shots, Check, notes);
            }
            finally
            {
                try { host?.Close(); } catch (Exception) { }
                Width = w0; Height = h0;
                try { ResetAtlasForTest(); } catch (Exception) { }
                CreatureModels.RootOverride = savedRoot; CreatureModels.SourceForTest = savedSrc; CreatureModels.BuildForTest = savedBuild; CreatureModels.ResetForTest();
            }
        }
    }
}
