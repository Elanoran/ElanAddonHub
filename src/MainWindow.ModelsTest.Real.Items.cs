using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Models3d;

namespace ElansAddonHub
{
    // --selftest "modelsreal", 3.7: item models on the real game files, read-only. The five item tables in the layouts this Outpost knows (layout, table hash
    // and field count from the real WDC5 headers), the game's own item data (Item.db2 / ItemSparse through ItemData) for the inventory types, then real
    // items end to end: Thunderfury, a common sword, a shield, a bow, a gun, a staff, a wand resolve (item -> appearance -> display -> model file + skin),
    // build into M2B2 with their skins packed (no stand-ins), load in the viewer (the mesh in, textures read, a frame that is not blank, inside the view at
    // every turn); a Hearthstone gets no card; Thunderfury's and the shield's Atlas pages show the card at the top of the rail.
    // Pictures with ELANSHUB_SHOTS=1 or ELANSHUB_MODELS_SHOTS=<folder>.
    public partial class MainWindow
    {
        // the items looked at: a well-known id first (checked by its class, subclass and inventory type), else the first item of that kind with a model
        static readonly (string label, string slug, int[] ids, int cls, int sub, int inv)[] RealItems =
        {
            ("Thunderfury", "thunderfury", new[] { 19019 }, 2, 7, 13),
            ("a common sword", "sword", new[] { 25, 2488, 2131 }, 2, 7, 13),
            ("a shield", "shield", new[] { 2129, 2133, 1201 }, 4, 6, 14),
            ("a bow", "bow", new[] { 2504, 2506, 3036 }, 2, 2, 15),
            ("a gun", "gun", new[] { 2508, 2509, 2099 }, 2, 3, 26),
            ("a staff", "staff", new[] { 35, 1515, 2077 }, 2, 10, 17),
            ("a wand", "wand", new[] { 5069, 5071, 11287 }, 2, 19, 26),
            // 3.7 framing: another staff and a polearm (held in the middle: the heavier end up), a held-in-off-hand item (faces the camera, sways)
            ("another staff", "staff2", new int[0], 2, 10, 17),
            ("a polearm", "polearm", new int[0], 2, 6, 17),
            ("a held-in-off-hand item", "offhand", new int[0], 4, 0, 23),
        };

        // 3.7: the heavier end of a long item worked out here from the built file, apart from viewer.js: along the longest side, the outer quarter at each end;
        // the larger cross-section (the two other extents added) wins, within 10 % the one with more vertices. +1 = the max end of that axis.
        static (int axis, int sign, double hiCross, double loCross, int hiN, int loN) MvHeadOf(byte[] file)
        {
            var meta = CreatureModel.ReadMeta(file, out int ds);
            var mn = meta.List("min").Select(x => Convert.ToDouble(x, CultureInfo.InvariantCulture)).ToArray(); var mx = meta.List("max").Select(x => Convert.ToDouble(x, CultureInfo.InvariantCulture)).ToArray();
            int a = 0; for (int k = 1; k < 3; k++) if (mx[k] - mn[k] > mx[a] - mn[a]) a = k;
            int b = (a + 1) % 3, c = (a + 2) % 3, nv = meta.Int("nv"), at = ds + meta.Child("at").Int("pos");
            double cut = 0.25 * (mx[a] - mn[a]);
            var lo = new double[2, 4]; var n = new int[2];
            for (int e = 0; e < 2; e++) { lo[e, 0] = lo[e, 2] = double.MaxValue; lo[e, 1] = lo[e, 3] = double.MinValue; }
            for (int i = 0; i < nv; i++)
            {
                double v = BitConverter.ToSingle(file, at + (i * 3 + a) * 4), pb = BitConverter.ToSingle(file, at + (i * 3 + b) * 4), pc = BitConverter.ToSingle(file, at + (i * 3 + c) * 4);
                int e = v >= mx[a] - cut ? 0 : v <= mn[a] + cut ? 1 : -1;
                if (e < 0) continue;
                n[e]++; lo[e, 0] = Math.Min(lo[e, 0], pb); lo[e, 1] = Math.Max(lo[e, 1], pb); lo[e, 2] = Math.Min(lo[e, 2], pc); lo[e, 3] = Math.Max(lo[e, 3], pc);
            }
            double X(int e) => n[e] == 0 ? 0 : lo[e, 1] - lo[e, 0] + lo[e, 3] - lo[e, 2];
            double h = X(0), l = X(1);
            int sign = h > l * 1.1 ? 1 : l > h * 1.1 ? -1 : n[0] >= n[1] ? 1 : -1;
            return (a, sign, h, l, n[0], n[1]);
        }

        async Task ModelsRealItemChecks(string real, string flv, string work, SpellRaw raw, ModelSet set, CheckFn Check, List<string> notes)
        {
            var I = CultureInfo.InvariantCulture;
            // ---- the tables as the game has them
            var fields = new Dictionary<string, int> { ["ItemModifiedAppearance"] = ModelTables.ImaFields, ["ItemAppearance"] = ModelTables.IaFields, ["ItemDisplayInfo"] = ModelTables.IdFields, ["ModelFileData"] = ModelTables.MfFields, ["TextureFileData"] = ModelTables.TfFields };
            var heads = ModelTables.ItemTables.Select(d => (d, o: SpellTables.Open(raw.Get(d), d))).ToList();
            Check("real item tables: ItemModifiedAppearance, ItemAppearance, ItemDisplayInfo, ModelFileData and TextureFileData in the layouts this Outpost knows, with the field counts of their LAYOUT blocks (7, 5, 16, 5, 3) ("
                + string.Join(", ", heads.Select(h => h.d.Name + " " + (h.o.Ok ? h.o.Table.Count.ToString("N0", I) + " rows" : "-"))) + ")",
                heads.All(h => h.o.Ok && h.o.Table.FieldCount == fields[h.d.Name] && h.o.Layout == h.d.Layouts[0]) && ModelTables.ItemTables.All(d => !set.Problems.ContainsKey(d.Name)),
                string.Join(" | ", heads.Select(h => h.d.Name + ": " + (h.o.Ok ? "layout " + h.o.Layout.ToString("X8", I) + " fields " + h.o.Table.FieldCount + " id index " + h.o.Table.IdIndex : h.o.Problem))));
            if (set.ItemLooks == null || set.ItemDisplays == null || set.ModelFiles == null) return;
            int multiFile = set.ModelFiles.Values.Count(l => l.Count > 1), twoModels = set.ItemDisplays.Values.Count(d => d.Models[0] > 0 && d.Models[1] > 0);
            notes.Add("info real item tables: " + set.ItemLooks.Count.ToString("N0", I) + " items with appearances, " + set.AppearanceDisplay.Count.ToString("N0", I) + " appearances, " + set.ItemDisplays.Count.ToString("N0", I)
                + " displays with a model (" + twoModels.ToString("N0", I) + " with two), " + set.ModelFiles.Count.ToString("N0", I) + " model resources (" + multiFile.ToString("N0", I) + " with more than one file), "
                + set.TextureFiles.Count.ToString("N0", I) + " materials with textures; notes: " + string.Join("; ", set.Notes));

            // ---- the game's item data (the inventory types)
            var savedItems = ItemData.Snapshot; var savedItemSt = ItemData.Status;
            var savedRoot = CreatureModels.RootOverride; var savedSrc = CreatureModels.SourceForTest; var savedBuild = CreatureModels.BuildForTest;
            double w0 = Width, h0 = Height;
            Window host = null;
            try
            {
                var rawI = await Task.Run(() => ItemData.ReadCasc(real, flv));
                var iset = ItemData.ParseTables(rawI.Sparse, rawI.Item, rawI.Budget);
                if (!iset.Ok) { Check("real items: the game's item data reads", false, iset.Problem); return; }
                ItemData.ReadCacheRows(iset, ItemData.AdbFile(real, flv));
                ItemData.SetForTest(new ItemSnapshot(iset.Client, iset.Cache), ItemData.State.Ready);

                var picked = new List<(string label, string slug, ClientItem it, ItemModel im)>();
                var why = new List<string>();
                foreach (var r in RealItems)
                {
                    ClientItem it = null;
                    foreach (var id in r.ids) { var c = ItemData.Get(id); if (c != null && c.ClassId == r.cls && c.SubclassId == r.sub && c.InventoryType == r.inv) { it = c; break; } }
                    if (it == null)
                    {
                        if (r.ids.Length > 0) why.Add(r.label + ": none of " + string.Join("/", r.ids) + " is a " + r.cls + "/" + r.sub + " slot " + r.inv + " (" + string.Join(", ", r.ids.Select(id => { var c = ItemData.Get(id); return c == null ? id + " unknown" : id + " " + c.Name + " " + c.ClassId + "/" + c.SubclassId + " slot " + c.InventoryType; })) + "); the first such item with a model is used");
                        it = ItemData.Snapshot.Items.Values.Where(c => c.ClassId == r.cls && c.SubclassId == r.sub && c.InventoryType == r.inv && c.Quality >= 1 && !picked.Any(p => p.it.Id == c.Id)).OrderBy(c => c.Quality > 2 ? 1 : 0).ThenBy(c => c.Id)
                            .FirstOrDefault(c => ModelData.ForItem(c.Id).Ok);
                    }
                    if (it == null) { why.Add(r.label + ": no item of that kind with a model"); continue; }
                    picked.Add((r.label, r.slug, it, ModelData.ForItem(it.Id)));
                }
                foreach (var w in why) notes.Add("info real items: " + w);
                var tf = picked.FirstOrDefault(p => p.slug == "thunderfury");
                var hearth = ItemData.Get(6948);
                Check("real items: the inventory types are the numbers the card uses - Thunderfury (19019) 13 one-hand, the shield 14, the bow 15, the staff 17, the gun and the wand 26; the Hearthstone (6948) 0 - and it gets no card",
                    tf.it?.Id == 19019 && picked.Count == RealItems.Length && picked.All(p => ModelData.ItemWanted(p.it.Id)) && hearth != null && hearth.InventoryType == 0 && !ModelData.ItemWanted(6948)
                    && ModelData.ForItem(6948).Reason == ModelData.NoItemCard,
                    string.Join(" | ", picked.Select(p => p.label + " " + p.it.Id + " " + p.it.Name + " slot " + p.it.InventoryType)) + " | hearthstone " + (hearth == null ? "unknown" : hearth.Name + " slot " + hearth.InventoryType) + " | " + string.Join("; ", why));
                Check("real items: each resolves through the chain to a model file and its skin (" + string.Join("; ", picked.Select(p => p.label + " " + p.it.Id + " -> display " + p.im.DisplayInfoId + " -> file " + p.im.ModelFileId + " skin " + p.im.TextureFileId)) + ")",
                    picked.All(p => p.im.Ok && p.im.TextureFileId > 0 && p.im.Modifier >= 0), string.Join(" | ", picked.Select(p => p.it.Name + ": " + p.im)));
                foreach (var p in picked) notes.Add("info real item chain: " + p.label + " " + p.it.Id + " " + p.it.Name + ": " + p.im + (p.im.Notes.Count > 0 ? "; " + string.Join("; ", p.im.Notes) : ""));
                // how many items of the shown kinds resolve at all (an overview, not a pass condition beyond "most")
                var kinds = ItemData.Snapshot.Items.Values.Where(c => ModelTables.ItemSlotShown(c.InventoryType)).ToList();
                int okKinds = kinds.Count(c => ModelData.ForItem(c.Id).Ok), multiDefault = kinds.Count(c => ModelData.ForItem(c.Id).DefaultLooks > 1);
                var fails = kinds.Select(c => ModelData.ForItem(c.Id)).Where(m => !m.Ok).GroupBy(m => System.Text.RegularExpressions.Regex.Replace(m.Reason ?? "", @"\d+", "#")).OrderByDescending(g => g.Count()).Take(4);
                notes.Add("info real items: " + okKinds.ToString("N0", I) + " of " + kinds.Count.ToString("N0", I) + " weapons / shields / off-hand items resolve to a model; " + multiDefault + " have several default displays (noted, not switched); the rest: "
                    + string.Join("; ", fails.Select(g => g.Count() + " x " + g.Key)));
                Check("real items: nearly every weapon, shield and off-hand item of the game resolves to a model (" + okKinds + " of " + kinds.Count + ")", kinds.Count > 1000 && okKinds >= kinds.Count * 0.9, okKinds + "/" + kinds.Count);

                // ---- built into the scratch folder
                CreatureModels.ResetForTest();
                CreatureModels.RootOverride = Path.Combine(work, "items", "atlas", "models");
                CreatureModels.SourceForTest = () => new CascModelSource(CascShared.Open(real, flv));
                CreatureModels.BuildForTest = () => ItemData.GameBuild(real, flv);
                var key = CreatureModels.KeyFor(ItemData.GameBuild(real, flv));
                var built = new List<(string label, string slug, ClientItem it, ItemModel im, ModelBuildResult r, long bytes, long texBytes, long ms)>();
                foreach (var p in picked)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var r = await CreatureModels.EnsureItemAsync(p.im.DisplayInfoId);
                    long tex = r.Model?.Textures.Sum(t => File.Exists(CreatureModels.TexturePath(key, t)) ? new FileInfo(CreatureModels.TexturePath(key, t)).Length : 0) ?? 0;
                    built.Add((p.label, p.slug, p.it, p.im, r, r.Ok ? new FileInfo(r.Path).Length : 0, tex, sw.ElapsedMilliseconds));
                }
                string Info(CreatureModel m) => m == null ? "-" : m.Name + " v" + m.Version + ": " + m.Nv + " vertices, " + m.Idx.Count / 3 + " triangles, sub-meshes " + m.ShownSubs + "/" + m.RawSubs + " (ids " + string.Join("/", m.ShownIds) + (m.HiddenIds.Count > 0 ? ", hidden " + string.Join("/", m.HiddenIds) : "") + "), "
                    + m.Batches.Count + " batches (types " + string.Join("/", m.Batches.Select(b => b.TexType).Distinct()) + ", blends " + string.Join("/", m.Batches.Select(b => b.Blend).Distinct()) + "), " + m.Bones.Count + " bones"
                    + (m.Bones.Count > 0 ? ", Stand " + (m.AnimIndex >= 0 ? m.AnimDuration + " ms " + m.AnimKeys + " keys" : "none") : "") + ", textures " + string.Join("/", m.Textures) + ", size " + string.Join(" x ", Enumerable.Range(0, 3).Select(k => (m.Max[k] - m.Min[k]).ToString("0.00", I)))
                    + (m.Notes.Count > 0 ? "; notes: " + string.Join("; ", m.Notes) : "");
                foreach (var b in built) notes.Add("info real item build: " + b.label + " " + b.it.Id + " (display " + b.im.DisplayInfoId + "): " + (b.r.Ok ? Info(b.r.Model) + "; file " + (b.bytes / 1024.0).ToString("0.0", I) + " KB + textures " + (b.texBytes / 1024.0).ToString("0", I) + " KB, " + b.ms + " ms" : b.r.Reason));
                Check("real item build: all " + built.Count + " are built (" + string.Join(", ", built.Select(b => b.label + " " + (b.bytes / 1024.0).ToString("0", I) + " KB + " + (b.texBytes / 1024.0).ToString("0", I) + " KB textures")) + ")",
                    built.Count == picked.Count && built.All(b => b.r.Ok && b.r.Built && b.r.Model != null && b.r.Model.Kind == "item" && File.Exists(b.r.Path) && b.r.Path.EndsWith("item-" + b.im.DisplayInfoId.ToString(I) + ".m2b2")),
                    string.Join(" | ", built.Select(b => b.label + ": " + b.r)));
                var ms = built.Where(b => b.r.Model != null).ToList();
                Check("real item build: every skin is the item's own, packed (MTX1) - each batch of texture type 2 has the display's texture, type 0 its TXID file, nothing missing",
                    ms.Count > 0 && ms.All(b => b.r.MissingTextures.Count == 0 && b.r.Model.Textures.Count > 0 && b.r.Model.Textures.All(t => File.Exists(CreatureModels.TexturePath(key, t)))
                        && b.r.Model.Batches.Where(x => x.TexType == 2).All(x => x.Tex == b.im.TextureFileId) && b.r.Model.Batches.Where(x => x.TexType == 0 || x.TexType == 2).All(x => x.Tex > 0)),
                    string.Join(" | ", ms.Select(b => b.label + " types " + string.Join("/", b.r.Model.Batches.Select(x => x.TexType + ":" + x.Tex)) + " missing " + string.Join("/", b.r.MissingTextures))));
                Check("real item build: sizes are reasonable (each file under 2 MB, each texture under 2 MB, the model a few units long, not empty)",
                    ms.All(b => b.bytes > 500 && b.bytes < (2 << 20) && b.r.Model.Textures.All(t => new FileInfo(CreatureModels.TexturePath(key, t)).Length < (2 << 20))
                        && Enumerable.Range(0, 3).Max(k => b.r.Model.Max[k] - b.r.Model.Min[k]) is float L && L > 0.1 && L < 20),
                    string.Join(" | ", ms.Select(b => b.label + " " + b.bytes + " B " + string.Join(" x ", Enumerable.Range(0, 3).Select(k => (b.r.Model.Max[k] - b.r.Model.Min[k]).ToString("0.00", I))))));
                var again = built.Count > 0 ? await CreatureModels.EnsureItemAsync(built[0].im.DisplayInfoId) : null;
                Check("real item build: asked again, Thunderfury comes from the cache folder", again != null && again.Ok && !again.Built && again.Path == built[0].r.Path, again?.ToString());

                // ---- the viewer alone, off the screen
                var shots = ModelShots(work);
                var v = new ModelView { Width = 340, Height = 200 };
                host = new Window { Width = 360, Height = 220, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Content = v, Title = "item model test" };
                host.Show();
                var viewRows = new List<string>(); bool allShown = true, allTex = true, allLit = true, allFit = true;
                var upRows = new List<string>(); var faceRows = new List<string>(); bool headOk = true, gripOk = true, facedOk = true; int headCount = 0, faced = 0;
                var turnRows = new List<string>(); bool turnedOk = true, offhandTurned = false, shieldFaced = false;
                foreach (var b in ms)
                {
                    var meta = CreatureModel.ReadMeta(File.ReadAllBytes(b.r.Path), out _);
                    v.Show(b.r.Path, ModelData.ItemUpRule(b.it.Id));
                    bool ok = await Until(() => v.State == ModelView.ViewState.Shown || v.State == ModelView.ViewState.Failed || v.State == ModelView.ViewState.NoWebView, 30000) && v.State == ModelView.ViewState.Shown;
                    if (!ok) { allShown = false; viewRows.Add(b.label + ": " + v.State + " " + v.Reason); continue; }
                    var tw = System.Diagnostics.Stopwatch.StartNew(); Dictionary<string, object> st = null;
                    while (tw.ElapsedMilliseconds < 15000) { st = await MvState(v); if (st?.Str("model") == v.ModelForTest && D(st, "texLoaded") + D(st, "texFailed") >= D(st, "texWanted")) break; await Task.Delay(100); }
                    bool shownOk = st?.Str("state") == "shown" && st.Str("kind") == "item" && D(st, "nv") == meta.Int("nv") && D(st, "ni") == meta.Int("ni") && D(st, "bones") == meta.List("bones").Count && D(st, "batches") > 0;
                    bool texOk = D(st, "texWanted") >= 1 && D(st, "texLoaded") == D(st, "texWanted") && D(st, "texFailed") == 0;
                    var boxes = new List<Dictionary<string, object>>();
                    foreach (var yaw in new[] { 0.0, 0.8, 1.6, 2.4, 3.2, 4.0, 4.8, 5.6 }) boxes.Add(Json.Obj(await v.EvalForTest("mv.screenBox(" + yaw.ToString(I) + ")") ?? "null"));
                    bool fit = boxes.All(x => x != null && D(x, "x0") >= -1.02 && D(x, "x1") <= 1.02 && D(x, "y0") >= -1.02 && D(x, "y1") <= 1.02);
                    double span = boxes.Min(x => x == null ? 0 : Math.Max(D(x, "x1") - D(x, "x0"), D(x, "y1") - D(x, "y0")));
                    await v.EvalForTest("mv.screenBox(0.7)");
                    var pr = Json.Obj(await v.EvalForTest("mv.probe()"));
                    bool lit = D(pr, "w") > 100 && D(pr, "frac") > 0.01 && D(pr, "frac") < 0.95;
                    allShown &= shownOk; allTex &= texOk; allLit &= lit; allFit &= fit && span > 0.8;
                    var vw = Json.Obj(await v.EvalForTest("mv.viewOf()") ?? "null");
                    viewRows.Add(b.label + ": " + D(st, "nv") + " vertices, " + D(st, "bones") + " bones" + (st.Bool("still") ? " (still)" : "") + (st.Bool("thin") ? ", thin, leaned " + (D(vw, "tilt") * 180 / Math.PI).ToString("0", I) + " deg" : "") + ", textures " + D(st, "texLoaded") + "/" + D(st, "texWanted")
                        + ", lit " + (D(pr, "frac") * 100).ToString("0.0", I) + " %, smallest span " + span.ToString("0.00", I) + (fit ? "" : " NOT IN VIEW " + string.Join(" ", boxes.Select(x => Json.Write(x)))));
                    // 3.7 framing, at the opening pose: which end is up (a staff / polearm: the heavier end, worked out here apart from the page; anything else: the far
                    // end from the grip), and a shield / off-hand item facing the camera and swaying, not turning round
                    var ends = Json.Obj(await v.EvalForTest("(function(){ mv.opening(true); return mv.ends(); })()") ?? "null");
                    if (st.Bool("thin"))
                    {
                        var file = File.ReadAllBytes(b.r.Path); var hd = MvHeadOf(file);
                        var mmin = meta.List("min").Select(x => Convert.ToDouble(x, I)).ToArray(); var mmax = meta.List("max").Select(x => Convert.ToDouble(x, I)).ToArray();
                        bool head = ModelData.ItemUpRule(b.it.Id) == "head";
                        int want = head ? hd.sign : Math.Abs(mmax[hd.axis]) >= Math.Abs(mmin[hd.axis]) ? 1 : -1;
                        double yHi = D(ends?.Child("hi"), "y"), yLo = D(ends?.Child("lo"), "y");
                        bool upOk = ends != null && (int)D(ends, "axis") == hd.axis && ends.Str("rule") == (head ? "head" : "grip") && (want > 0 ? yHi > yLo : yLo > yHi);
                        upRows.Add(b.label + " " + b.it.Id + " (" + b.it.Name + ", " + (head ? "head" : "grip") + " rule): the " + (want > 0 ? "max" : "min") + " end up - screen y " + (want > 0 ? yHi : yLo).ToString("0.00", I) + " over " + (want > 0 ? yLo : yHi).ToString("0.00", I)
                            + "; outer quarters cross-section " + hd.hiCross.ToString("0.00", I) + " (max end, " + hd.hiN + " vertices) / " + hd.loCross.ToString("0.00", I) + " (min end, " + hd.loN + "); grip at " + (Math.Min(Math.Abs(mmax[hd.axis]), Math.Abs(mmin[hd.axis])) / (mmax[hd.axis] - mmin[hd.axis]) * 100).ToString("0", I) + " % from an end" + (upOk ? "" : " WRONG " + Json.Write(ends)));
                        if (head) headOk &= upOk; else gripOk &= upOk;
                        if (head) headCount++;
                    }
                    else if (!st.Bool("sway"))
                    {
                        // 3.7: not flat (an orb, a totem): no front is guessed - the usual 3/4 opening and the slow full turn
                        var vw0 = Json.Obj(await v.EvalForTest("mv.viewOf()") ?? "null");
                        double y1 = await MvNum(v, "(function(){ var t = 0; for (var k = 0; k < 40; k++) t += mv.idle(250); return t; })()");
                        await v.EvalForTest("mv.opening(true)");
                        bool turnOk = (st.List("front")?.Count ?? 0) == 0 && Math.Abs(D(vw0, "yaw0") - 0.61) < 1e-6 && y1 > 3;
                        turnRows.Add(b.label + " " + b.it.Id + " (" + b.it.Name + "): not flat - no front, opens at 3/4 (camera yaw " + D(vw0, "yaw0").ToString("0.00", I) + "), turned " + y1.ToString("0.00", I) + " rad in 10 s (always one way)" + (turnOk ? "" : " WRONG"));
                        turnedOk &= turnOk; if (b.slug == "offhand") offhandTurned = turnOk;
                    }
                    else
                    {
                        double f0 = await MvNum(v, "mv.facing()");
                        var offs = new List<double>(); var faces = new List<double>();
                        await v.EvalForTest("mv.opening(true)");
                        for (int k = 0; k < 32; k++) { offs.Add(await MvNum(v, "mv.sway(250)")); faces.Add(await MvNum(v, "mv.facing()")); }
                        await v.EvalForTest("mv.opening(true)");
                        bool faceOk = st.Bool("sway") && f0 > 0.97 && offs.Max() <= 0.611 && offs.Min() >= -0.611 && offs.Max() > 0.55 && faces.Min() > 0.78;
                        faceRows.Add(b.label + " " + b.it.Id + " (" + b.it.Name + "): front " + Json.Write(st.List("front")) + ", facing " + f0.ToString("0.000", I) + " at the opening, sway " + offs.Min().ToString("0.00", I) + " .. " + offs.Max().ToString("0.00", I)
                            + " rad, facing never under " + faces.Min().ToString("0.00", I) + (faceOk ? "" : " WRONG"));
                        facedOk &= faceOk; faced++; if (b.slug == "shield") shieldFaced = faceOk;
                    }
                    if (SelfTestClock.ShotsOn) { await Task.Delay(150); var png = await v.CaptureForTest(); if (png != null) File.WriteAllBytes(Path.Combine(shots, "models-item-viewer-" + b.slug + ".png"), png); }
                    await v.EvalForTest("mv.opening(false)");
                }
                foreach (var r in viewRows) notes.Add("info real item viewer: " + r);
                Check("real item viewer: each is shown - the mesh as in the file (vertices, indices, bones), kind item", allShown, string.Join(" | ", viewRows));
                Check("real item viewer: every texture fetched and read", allTex, string.Join(" | ", viewRows));
                Check("real item viewer: a drawn frame is not blank for any of them", allLit, string.Join(" | ", viewRows));
                Check("real item viewer: each stays inside the view at every turn and fills it (staves and bows included: framed by the longest side)", allFit, string.Join(" | ", viewRows));
                foreach (var r in upRows.Concat(faceRows).Concat(turnRows)) notes.Add("info real item framing: " + r);
                Check("real item framing: the staves and the polearm (" + headCount + ") stand with their heavier end (the head) up - the upper end on the screen at the opening pose", headCount >= 3 && headOk, string.Join(" | ", upRows));
                Check("real item framing: swords, the bow, the gun and the wand keep the grip rule - the far end from the grip is the upper end", gripOk, string.Join(" | ", upRows));
                Check("real item framing: the shield (flat) opens facing the camera and sways +-35 degrees, never edge-on", shieldFaced && facedOk, string.Join(" | ", faceRows));
                Check("real item framing: the held-in-off-hand totem (not flat) gets no front guess - the 3/4 opening and the slow full turn", offhandTurned && turnedOk, string.Join(" | ", turnRows));
                v.Dispose(); host.Close(); host = null;

                // ---- on their Atlas pages
                ResetAtlasForTest();
                ShowTab("atlas");
                await AtlasView.WaitForTest();
                Width = 1800; Height = 1000;
                foreach (var b in ms.Where(x => x.slug == "thunderfury" || x.slug == "shield" || x.slug == "staff" || x.slug == "offhand"))
                {
                    await AtlasView.Open("item:" + b.it.Id.ToString(I)); UpdateLayout();
                    await AtlasView.ModelResolveForTest;
                    bool ok = await MvShown(() => AtlasView.ModelViewForTest, 30000);
                    var pv = AtlasView.ModelViewForTest;
                    if (!ok || pv == null) { Check("real item page: " + b.label + " (item " + b.it.Id + ") shows its model at the top of the rail", false, pv?.State + " " + pv?.Reason + " / " + AtlasView.ModelStatusForTest); continue; }
                    var tw = System.Diagnostics.Stopwatch.StartNew(); Dictionary<string, object> st = null;
                    while (tw.ElapsedMilliseconds < 15000) { st = await MvState(pv); if (D(st, "texLoaded") + D(st, "texFailed") >= D(st, "texWanted")) break; await Task.Delay(100); }
                    double f0 = D(st, "frames");
                    await Until(() => false, 600);
                    var st2 = await MvState(pv);
                    Check("real item page: " + b.label + " (item " + b.it.Id + ", " + b.it.Name + ") - the model at the top of the rail, its textures in, drawing (frames " + f0 + " -> " + D(st2, "frames") + "), no look switch",
                        AtlasView.ModelCardInRailForTest && AtlasView.ModelItemForTest == b.it.Id && D(st, "texFailed") == 0 && D(st, "texLoaded") >= 1 && D(st2, "frames") > f0 && AtlasView.LookCountForTest == 0 && pv.ModelForTest.EndsWith("/m/item-" + b.im.DisplayInfoId.ToString(I) + ".m2b2"),
                        Json.Write(st2));
                    if (SelfTestClock.ShotsOn)
                    {
                        await pv.EvalForTest("mv.opening(true)"); await Task.Delay(300);
                        var png = await pv.CaptureForTest(); if (png != null) File.WriteAllBytes(Path.Combine(shots, "models-item-page-" + b.slug + "-viewer.png"), png);
                        SnapshotWithViewer(Path.Combine(shots, "models-item-page-" + b.slug + ".png"), pv, png);
                    }
                }
                await AtlasView.Open("item:6948"); UpdateLayout();
                Check("real item page: the Hearthstone (6948) has no Model card", AtlasView.ModelCardForTest == null && AtlasView.PageTitleForTest.Length > 0, AtlasView.PageTitleForTest);
            }
            finally
            {
                try { host?.Close(); } catch (Exception) { }
                Width = w0; Height = h0;
                try { ResetAtlasForTest(); } catch (Exception) { }
                CreatureModels.RootOverride = savedRoot; CreatureModels.SourceForTest = savedSrc; CreatureModels.BuildForTest = savedBuild; CreatureModels.ResetForTest();
                ItemData.SetForTest(savedItems, savedItemSt);
            }
        }
    }
}
