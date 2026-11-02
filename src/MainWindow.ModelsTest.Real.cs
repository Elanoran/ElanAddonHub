using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas.Classic;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Models3d;

namespace ElansAddonHub
{
    // --selftest "modelsreal" (3.6), the real game files, read-only (skipped when this PC has no Forever client): CreatureDisplayInfo and CreatureModelData
    // of the installed build in the layouts this Outpost knows, the sample creatures of the data check resolving to their models (Timber Wolf, Stonetusk Boar,
    // Rock Elemental, Scalding Whelp, Ragnaros) and a humanoid flagged; then the same through npc entries with the Classic baseline: the data folder's index
    // when it is format 4 (only read), else the dump on this PC read again into the scratch folder (only read). 3.6 steps 3 + 4: the Timber Wolf and Ragnaros
    // in the model viewer, alone and on their NPC pages (MainWindow.ModelsTest.View.cs). 3.6 step 5: update z2823's display chances on the real dump (how
    // many creatures they changed, a pair now 0 / 100, a four-look beast at 25 % each with its looks) and the look switch on that beast's real page.
    public partial class MainWindow
    {
        async Task ModelsRealChecks(string dir, string work, CheckFn Check, Func<string, string> P, List<string> notes)
        {
            var I = CultureInfo.InvariantCulture;
            var real = Environment.GetEnvironmentVariable("ELANSHUB_WOW");
            if (string.IsNullOrEmpty(real)) real = WowLocator.Find();
            var flv = string.IsNullOrEmpty(real) ? WowLocator.ForeverBetaFolder : WowLocator.Resolve(real, WowLocator.ForeverBetaFolder);   // _forever_ once released
            if (string.IsNullOrEmpty(real) || !Directory.Exists(Path.Combine(real, "Data", "data")) || !Directory.Exists(Path.Combine(real, flv)))
            {
                notes.Add("skip real game files: no WoW install with the Forever client on this PC");
                return;
            }
            var savedSeen = SeenData.Snapshot; var savedSeenSt = SeenData.Status;
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var set = await Task.Run(() => ModelSet.Parse(ModelData.ReadCasc(real, flv)));
                long ms = sw.ElapsedMilliseconds;
                var build = ItemData.GameBuild(real, flv);
                if (set.Problems.Values.Any(p => p.StartsWith("game updated")))
                {
                    notes.Add("skip real game files: " + string.Join("; ", set.Problems.Values) + " (build " + build + "; this Outpost does not know that layout - nothing was guessed; Tools\\layoutwatch.ps1 shows the new fields)");
                    return;
                }
                Check("real game files: CreatureDisplayInfo and CreatureModelData read in the layouts this Outpost knows (" + (set.Displays?.Count ?? 0).ToString("N0", I) + " displays, " + (set.Models?.Count ?? 0).ToString("N0", I) + " models, " + ms + " ms)",
                    set.Problems.Count == 0 && set.Displays?.Count > 10000 && set.Models?.Count > 1000 && set.LayoutsSeen.GetValueOrDefault("CreatureDisplayInfo") == 0x7275F5F6 && set.LayoutsSeen.GetValueOrDefault("CreatureModelData") == 0x0F5449F8,
                    string.Join("; ", set.Problems.Values) + " " + string.Join(",", set.LayoutsSeen.Select(kv => kv.Key + "=" + kv.Value.ToString("X8", I))));
                if (set.Displays == null || set.Models == null) return;
                foreach (var n in set.Notes) notes.Add("info real models: " + n);
                ModelData.SetForTest(set);
                string D(NpcModel m) => "display " + m.DisplayId + " model " + m.ModelId + " file " + m.ModelFileId + " skins " + string.Join("/", m.Textures) + " scale " + m.Scale.ToString("0.###", I) + (m.Reason == null ? "" : " (" + m.Reason + ")");
                var wolf = ModelData.ForDisplay(604);
                Check("real game files: Timber Wolf display 604 -> model file 126487 with 2 texture variations (126498, 126499), not humanoid", wolf.Ok && wolf.ModelFileId == 126487 && wolf.TextureCount == 2 && wolf.Textures[0] == 126498 && wolf.Textures[1] == 126499 && !wolf.Humanoid, D(wolf));
                var samples = new[] { (503, 123090, "Stonetusk Boar"), (171, 123747, "Rock Elemental"), (715, 123610, "Scalding Whelp"), (11121, 125519, "Ragnaros") };
                var got = samples.Select(s => (s, m: ModelData.ForDisplay(s.Item1))).ToList();
                Check("real game files: Stonetusk Boar 503 -> 123090, Rock Elemental 171 -> 123747, Scalding Whelp 715 -> 123610, Ragnaros 11121 -> 125519 (each with a skin, none humanoid)",
                    got.All(g => g.m.Ok && g.m.ModelFileId == g.s.Item2 && g.m.TextureCount >= 1 && !g.m.Humanoid), string.Join(" | ", got.Select(g => g.s.Item3 + ": " + D(g.m))));
                var guard = ModelData.ForDisplay(3167);
                Check("real game files: Stormwind City Guard display 3167 is a humanoid (character model, not yet shown)", guard.Humanoid && guard.Reason == ModelData.HumanoidReason && guard.ModelFileId > 0, D(guard));
                notes.Add("info real models: " + D(wolf) + "; " + string.Join("; ", got.Select(g => g.s.Item3 + " " + g.m.Scale.ToString("0.###", I))) + "; guard " + D(guard));
                int withExt = set.Displays.Values.Count(d => d.ExtendedId != 0), withModel = set.Displays.Values.Count(d => set.Models.TryGetValue(d.ModelId, out var md) && md.FileId > 0);
                notes.Add("info real models: " + withExt.ToString("N0", I) + " of " + set.Displays.Count.ToString("N0", I) + " displays are character models; " + withModel.ToString("N0", I) + " name a model row with a file");

                // ---- 3.6 step 2: the five built into M2B2 from the real files (into the scratch folder)
                try { await ModelsRealBuildChecks(real, flv, work, set, Check, notes); } catch (Exception e) { Check("real model build checks crashed", false, e.ToString()); }

                // ---- npc entries through the Classic baseline (the client's cache left out: these check the baseline path)
                var cdir = Environment.GetEnvironmentVariable("ELANSHUB_CLASSICDB");
                if (string.IsNullOrEmpty(cdir)) cdir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ElansAddonHub", "atlas", "classic");
                var ifile = ClassicPaths.IndexFile(cdir);
                var ci = File.Exists(ifile) ? ClassicIndex.Load(ifile) : null;
                string fromWhat;
                ClassicImportReport irep = null;
                if (ci != null && ci.Provenance.FormatVersion >= ClassicIndex.FormatVersion) fromWhat = "the data folder's index (format " + ci.Provenance.FormatVersion + ", only read)";
                else
                {
                    var src = ClassicDownloader.ReadSource(cdir);
                    var dump = src?.Str("dump") is string dn ? ClassicPaths.DumpFile(dn, cdir) : null;
                    if (dump == null || !File.Exists(dump)) { notes.Add("skip real Classic baseline: no format-" + ClassicIndex.FormatVersion + " index and no dump on this PC"); return; }
                    var ipath = Path.Combine(work, "classic-index.bin");
                    sw.Restart();
                    irep = await Task.Run(() => ClassicImporter.Import(dump, ClassicPaths.UpdatesDir(cdir), ipath, new ClassicProvenance { DumpName = Path.GetFileName(dump) }, null, CancellationToken.None));
                    ci = ClassicIndex.Load(ipath);
                    fromWhat = "the dump read again into the scratch folder (the data folder's index is format " + (File.Exists(ifile) ? ClassicIndex.Load(ifile)?.Provenance.FormatVersion.ToString(I) ?? "?" : "none") + "; " + sw.ElapsedMilliseconds + " ms)";
                }
                if (ci == null) { Check("real Classic baseline: an index could be read", false); return; }
                ClassicAtlas.Use(ci);
                SeenData.SetForTest(null, SeenData.State.NotStarted);
                var npcs = new[] { (69, 604, 126487, "Timber Wolf"), (113, 503, 123090, "Stonetusk Boar"), (92, 171, 123747, "Rock Elemental"), (2725, 715, 123610, "Scalding Whelp"), (11502, 11121, 125519, "Ragnaros") };
                var res = npcs.Select(n => (n, m: ModelData.ForNpc(n.Item1))).ToList();
                Check("real Classic baseline: npc entries resolve through their display ids - Timber Wolf 69, Stonetusk Boar 113, Rock Elemental 92, Scalding Whelp 2725, Ragnaros 11502 (" + fromWhat + ")",
                    res.All(r => r.m.Ok && r.m.From == "classic" && r.m.DisplayId == r.n.Item2 && r.m.ModelFileId == r.n.Item3), string.Join(" | ", res.Select(r => r.n.Item4 + ": " + D(r.m))));
                var g68 = ModelData.ForNpc(68);
                Check("real Classic baseline: Stormwind City Guard (68) is flagged humanoid", g68.Humanoid && g68.Reason == ModelData.HumanoidReason, D(g68) + " considered " + string.Join(", ", g68.Considered));

                // ---- 3.6 step 5: update z2823's display chances (the importer's own rule) on the real dump
                {
                    string C(ClassicNpc n) => n == null ? "missing" : n.Id + " " + n.Name + " " + string.Join("/", n.DisplayIds) + " @ " + string.Join("/", n.DisplayChances);
                    // 2 or 3 displays: before the rule every one of these read 100 / 0 (only the first UPDATE ran), unless a later update set chances itself
                    var few = ci.Npcs.Values.Where(n => n.DisplayIds.Length >= 2 && n.DisplayIds.Length <= 3).ToList();
                    int moved = few.Count(n => n.DisplayChances.Sum() > 0 && n.DisplayChances[0] != 100), still = few.Count(n => n.DisplayChances.Length > 0 && n.DisplayChances[0] == 100);
                    int fours = ci.Npcs.Values.Count(n => n.DisplayIds.Length == 4 && n.DisplayChances.All(c => c == 25));
                    int halves = ci.Npcs.Values.Count(n => n.DisplayChances.Count(c => c == 50) == 2 || n.DisplayChances.Count(c => c == 33) == 3);
                    notes.Add("info real display chances: " + (irep != null ? "the rule matched " + irep.DisplayChanceRows.ToString("N0", I) + " creatures and changed " + irep.DisplayChanceChanged.ToString("N0", I) + " (from 100 / 0); " : "")
                        + few.Count.ToString("N0", I) + " creatures with 2-3 displays: " + moved.ToString("N0", I) + " no longer 100 / 0, " + still.ToString("N0", I) + " still 100 first; " + fours.ToString("N0", I) + " with four displays at 25 % each; "
                        + halves.ToString("N0", I) + " at 50 / 50 or 33 / 33 / 33 (those need an other-team model in creature_model_info)");
                    Check("real display chances: update z2823's rule changed " + (irep != null ? irep.DisplayChanceChanged : moved).ToString("N0", I) + " creatures from 100 / 0 (" + moved.ToString("N0", I) + " of " + few.Count.ToString("N0", I) + " with 2-3 displays now read otherwise)",
                        moved > 300 && (irep == null || (irep.DisplayChanceRuleRan && irep.DisplayChanceChanged == moved)), irep == null ? "from the data folder's index" : "rule rows " + irep.DisplayChanceRows + " changed " + irep.DisplayChanceChanged + " vs " + moved);
                    var mixed = ci.Npcs.Values.Where(n => n.Type == 1 && n.DisplayIds.Length > 1).Select(n => (n, l: ModelData.LooksOf(n.Id))).Where(x => x.l.Count > 1 && x.l.Select(y => y.Model.ModelFileId).Distinct().Count() > 1).ToList();
                    notes.Add("info real display chances: " + mixed.Count + " beasts have looks with different model files (" + string.Join("; ", mixed.Take(8).Select(x => x.n.Id + " " + x.n.Name + ": " + string.Join(", ", x.l))) + ")");
                    var dimetro = ci.NpcInfo(13323);
                    var dm = ModelData.ForNpc(13323);
                    Check("real display chances: Subterranean Diemetradon (13323, displays 13209 / 13210, no other-team model) now reads 0 / 100 - the model is display 13210's and it has one look (no switch)",
                        dimetro != null && dimetro.DisplayIds.SequenceEqual(new[] { 13209, 13210 }) && dimetro.DisplayChances.SequenceEqual(new[] { 0, 100 }) && dm.Ok && dm.DisplayId == 13210 && ModelData.LooksOf(13323).Count == 1,
                        C(dimetro) + " | " + D(dm) + " looks " + string.Join(", ", ModelData.LooksOf(13323)));
                    var shark = ci.NpcInfo(LookNpc);
                    var looks = ModelData.LooksOf(LookNpc);
                    Check("real display chances: Reef Shark (" + LookNpc + ", four displays) reads 25 % each and has " + looks.Count + " looks the viewer can show, of " + looks.Select(l => l.Model.ModelFileId).Distinct().Count() + " model files (" + string.Join(", ", looks) + ")",
                        shark != null && shark.DisplayIds.Length == 4 && shark.DisplayChances.All(c => c == 25) && looks.Count >= 2 && looks.All(l => l.Chance == 25 && !l.Model.Humanoid)
                        && looks.Select(l => l.Model.ModelFileId).Distinct().Count() >= 2, C(shark));
                }
                // how the whole baseline fares (an overview for the viewer step, not a pass condition beyond "most beasts resolve")
                int shown = 0, human = 0, noDisp = 0, unknown = 0, other = 0, multi = 0;
                foreach (var n in ci.Npcs.Values)
                {
                    if (n.DisplayIds.Length > 1) multi++;
                    var m = ModelData.ForNpc(n.Id);
                    if (m.Ok) shown++; else if (m.Humanoid) human++; else if (m.Considered.Count == 0) noDisp++; else if (m.Reason != null && m.Reason.Contains("not in the game's CreatureDisplayInfo")) unknown++; else other++;
                }
                var beasts = ci.Npcs.Values.Where(n => n.Type == 1 && n.DisplayIds.Length > 0).ToList();
                int beastsOk = beasts.Count(n => ModelData.ForNpc(n.Id).Ok);
                notes.Add("info real Classic baseline: " + ci.Npcs.Count.ToString("N0", I) + " npcs: " + shown.ToString("N0", I) + " showable, " + human.ToString("N0", I) + " humanoid, " + noDisp.ToString("N0", I) + " without a display id, "
                    + unknown.ToString("N0", I) + " with displays the game lacks, " + other.ToString("N0", I) + " other; " + multi.ToString("N0", I) + " have more than one display (first by chance: " + string.Join(", ", ci.Npcs.Values.Where(n => n.DisplayIds.Length > 1).Take(3).Select(n => n.Id + " " + string.Join("/", n.DisplayIds) + " @ " + string.Join("/", n.DisplayChances))) + ")");
                Check("real Classic baseline: nearly every beast with a display id resolves to a model (" + beastsOk + " of " + beasts.Count + ")", beasts.Count > 500 && beastsOk >= beasts.Count * 0.95, beastsOk + "/" + beasts.Count);

                // ---- 3.6 steps 3 + 4: the wolf and Ragnaros in the viewer, alone and on their NPC pages (MainWindow.ModelsTest.View.cs)
                try { await ModelsRealViewChecks(real, flv, work, Check, notes); } catch (Exception e) { Check("real model view checks crashed", false, e.ToString()); }
            }
            finally
            {
                ModelData.ResetForTest();
                SeenData.SetForTest(savedSeen, savedSeenSt);
                ClassicAtlas.Use(null);
            }
        }

        // 3.6 step 2: Timber Wolf, Stonetusk Boar, Rock Elemental, Scalding Whelp and Ragnaros built from the real files into the scratch folder (CreatureModels
        // with the real storage), each file read back; then a model whose Stand data is in an .anim file, found by looking through the creature models
        async Task ModelsRealBuildChecks(string real, string flv, string work, ModelSet set, CheckFn Check, List<string> notes)
        {
            var I = CultureInfo.InvariantCulture;
            var savedRoot = CreatureModels.RootOverride; var savedSrc = CreatureModels.SourceForTest; var savedBuild = CreatureModels.BuildForTest;
            try
            {
                CreatureModels.ResetForTest();
                CreatureModels.RootOverride = Path.Combine(work, "atlas", "models");
                CreatureModels.SourceForTest = () => new CascModelSource(CascShared.Open(real, flv));
                CreatureModels.BuildForTest = () => ItemData.GameBuild(real, flv);
                var five = new[] { (604, "Timber Wolf"), (503, "Stonetusk Boar"), (171, "Rock Elemental"), (715, "Scalding Whelp"), (11121, "Ragnaros") };
                var built = new List<(int id, string name, ModelBuildResult r, long bytes, long ms)>();
                foreach (var (id, name) in five)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var r = await CreatureModels.EnsureAsync(id);
                    built.Add((id, name, r, r.Ok ? new FileInfo(r.Path).Length : 0, sw.ElapsedMilliseconds));
                }
                var key = CreatureModels.KeyFor(ItemData.GameBuild(real, flv));
                long TexBytes(CreatureModel m) => m.Textures.Sum(t => File.Exists(CreatureModels.TexturePath(key, t)) ? new FileInfo(CreatureModels.TexturePath(key, t)).Length : 0);
                string Info((int id, string name, ModelBuildResult r, long bytes, long ms) b)
                {
                    var m = b.r.Model;
                    if (m == null) return b.name + ": " + b.r.Reason;
                    return b.name + " " + b.id + " (" + m.Name + " v" + m.Version + "): " + m.Nv + " of " + m.RawVertices + " vertices, " + m.Idx.Count / 3 + " triangles, sub-meshes " + m.ShownSubs + " of " + m.RawSubs
                        + " (shown " + string.Join("/", m.ShownIds) + ", hidden " + string.Join("/", m.HiddenIds) + "), " + m.Batches.Count + " batches (types " + string.Join("/", m.Batches.Select(x => x.TexType).Distinct()) + ", blends " + string.Join("/", m.Batches.Select(x => x.Blend).Distinct()) + "), "
                        + m.Bones.Count + " bones, Stand = sequence " + m.AnimIndex + " (" + m.AnimDuration + " ms, flags 0x" + m.AnimFlags.ToString("X", I) + (m.AnimExternal > 0 ? ", .anim " + m.AnimExternal : ", in the .m2") + ") " + m.AnimKeys + " keys, "
                        + "textures " + string.Join("/", m.Textures) + ", weights put right " + m.FixedWeights + ", bad bone indices " + m.BadBoneIndices + ", lookup " + m.LookupChecked + "/" + m.LookupDisagree + " differ; "
                        + "file " + (b.bytes / 1024.0).ToString("0.0", I) + " KB + textures " + (TexBytes(m) / 1024.0).ToString("0", I) + " KB, " + b.ms + " ms" + (m.Notes.Count > 0 ? "; notes: " + string.Join("; ", m.Notes) : "");
                }
                foreach (var b in built) notes.Add("info real model build: " + Info(b));
                Check("real model build: all five are built (" + string.Join(", ", built.Select(b => b.name + " " + (b.bytes / 1024.0).ToString("0", I) + " KB")) + ")",
                    built.All(b => b.r.Ok && b.r.Built && b.r.Model != null && File.Exists(b.r.Path)), string.Join(" | ", built.Select(b => b.name + ": " + b.r)));
                var ms = built.Where(b => b.r.Model != null).Select(b => (b.name, m: b.r.Model)).ToList();
                if (ms.Count == 0) return;
                bool BonesOk(CreatureModel m) { for (int i = 0; i < m.Bi.Length; i++) if (m.Bi[i] >= Math.Max(1, m.Bones.Count)) return false; return true; }
                bool WeightsOk(CreatureModel m) { for (int i = 0; i < m.Nv; i++) if (m.Bw[i * 4] + m.Bw[i * 4 + 1] + m.Bw[i * 4 + 2] + m.Bw[i * 4 + 3] != 255) return false; return true; }
                Check("real model build: vertices and triangles in each, every bone index < the bone count with no index put right, weights sum to 255 per vertex",
                    ms.All(x => x.m.Nv > 0 && x.m.Idx.Count >= 3 && x.m.Bones.Count > 0 && BonesOk(x.m) && x.m.BadBoneIndices == 0 && WeightsOk(x.m)),
                    string.Join(" | ", ms.Select(x => x.name + " nv " + x.m.Nv + " bones " + x.m.Bones.Count + " bad " + x.m.BadBoneIndices + " fixed weights " + x.m.FixedWeights)));
                Check("real model build: the vertices' bone indices are the model's own bones - the skin's per-section lists through the bone lookup table name the same bones",
                    ms.All(x => x.m.LookupChecked > 0 && x.m.LookupDisagree == 0), string.Join(" | ", ms.Select(x => x.name + " " + x.m.LookupChecked + "/" + x.m.LookupDisagree)));
                Check("real model build: the Stand sequence (id 0) is chosen and has keys in each; the rotation keys are unit quaternions",
                    ms.All(x => x.m.AnimId == 0 && x.m.AnimProblem == null && x.m.AnimKeys > 0 && x.m.Bones.All(b => b.R.Keys == 0 || Enumerable.Range(0, b.R.Keys).All(k => Math.Abs(Math.Sqrt(Enumerable.Range(0, 4).Sum(j => (double)b.R.Values[k * 4 + j] * b.R.Values[k * 4 + j])) - 1) < 1e-3))),
                    string.Join(" | ", ms.Select(x => x.name + " id " + x.m.AnimId + " keys " + x.m.AnimKeys + " " + x.m.AnimProblem)));
                var wolf = ms.FirstOrDefault(x => x.name == "Timber Wolf").m;
                Check("real model build: textures are packed (MTX1) for every model; the wolf shows its display's 2 skins (126498, 126499)",
                    ms.All(x => x.m.Textures.Count > 0 && x.m.Textures.All(t => File.Exists(CreatureModels.TexturePath(key, t)))) && built.All(b => b.r.MissingTextures.Count == 0)
                    && wolf != null && wolf.Textures.Contains(126498) && wolf.Textures.Contains(126499),
                    string.Join(" | ", ms.Select(x => x.name + " " + string.Join("/", x.m.Textures))) + " missing " + string.Join("/", built.SelectMany(b => b.r.MissingTextures)));
                // the five have one geoset only (every sub-mesh id 0): the filter must leave them whole; a model with variants is looked for below
                Check("real model build: a model without geoset variants keeps every sub-mesh and vertex, one with variants loses some (" + string.Join(", ", ms.Select(x => x.name + " " + x.m.ShownSubs + "/" + x.m.RawSubs)) + ")",
                    ms.All(x => x.m.HiddenIds.Count == 0 ? x.m.ShownSubs == x.m.RawSubs && x.m.Nv == x.m.RawVertices : x.m.ShownSubs < x.m.RawSubs), string.Join(" | ", ms.Select(x => x.name + " ids " + string.Join("/", x.m.ShownIds) + " hidden " + string.Join("/", x.m.HiddenIds))));
                Check("real model build: sizes are reasonable (each model file under 4 MB, each texture under 2 MB, the bounds a few units, not empty)",
                    built.All(b => b.bytes > 1000 && b.bytes < (4 << 20)) && ms.All(x => x.m.Textures.All(t => new FileInfo(CreatureModels.TexturePath(key, t)).Length < (2 << 20)))
                    && ms.All(x => Enumerable.Range(0, 3).All(k => x.m.Max[k] > x.m.Min[k] && x.m.Max[k] - x.m.Min[k] < 200)),
                    string.Join(" | ", ms.Select(x => x.name + " " + string.Join(",", x.m.Min.Select(f => f.ToString("0.0", I))) + " .. " + string.Join(",", x.m.Max.Select(f => f.ToString("0.0", I))))));
                var again = await CreatureModels.EnsureAsync(604);
                Check("real model build: asked again, the wolf comes from the cache folder", again.Ok && !again.Built && again.Path == built[0].r.Path, again.ToString());
                var back = built.Where(b => b.r.Ok).Select(b => CreatureModel.ReadMeta(File.ReadAllBytes(b.r.Path), out _)).ToList();
                Check("real model build: each file reads back (M2B2, its counts as built)", back.All(x => x != null) && back.Zip(ms, (x, y) => x.Int("nv") == y.m.Nv && x.Int("ni") == y.m.Idx.Count && x.List("bones").Count == y.m.Bones.Count).All(b => b), null);

                // ---- .anim files and geoset variants: the wolf's own sequences, then a look through every creature (non-character) model of the install (a budget
                // of models and time): a Stand in an .anim, a skin with geoset variants
                int wolfExt = 0, wolfSeq = 0, wolfAnimSeq = -1, wolfAnimFdid = 0; bool wolfStandExt = false;
                (int display, int fdid, int afid)? found = null;
                (int display, int fdid, string ids)? variants = null;
                int looked = 0, withAfid = 0, withVariants = 0, extSeqs = 0; var sw2 = System.Diagnostics.Stopwatch.StartNew();
                CreatureModel wa = null; bool waChunked = false;
                using (var lease = CascShared.Open(real, flv))
                {
                    var src = new CascModelSource(lease);
                    var wr = src.Read(126487); var wc = CreatureModel.Chunks.Parse(wr); var wseq = CreatureModel.Sequences(wr, wc);
                    wolfSeq = wseq.Count; wolfExt = wseq.Count(s => (s.Flags & 0x20) == 0);
                    int wi = CreatureModel.StandIndex(wseq); wolfStandExt = wi >= 0 && (wseq[wi].Flags & 0x20) == 0;
                    // the wolf's first own sequence (not an alias) in an .anim, read as if it were the one to play: the .anim path on a real file
                    for (int k = 0; k < wseq.Count && wolfAnimSeq < 0; k++)
                        if ((wseq[k].Flags & 0x60) == 0) { var a = wc.Anims.FirstOrDefault(x => x.Id == wseq[k].Id && x.Sub == wseq[k].Sub); if (a.Fdid > 0) { wolfAnimSeq = k; wolfAnimFdid = a.Fdid; } }
                    if (wolfAnimSeq >= 0)
                    {
                        wa = CreatureModel.Parse(wr, src.Read(wc.Skins[0]), new[] { 126498, 126499, 0, 0 }, 0.71f, src.Read, 604, wolfAnimSeq);
                        CreatureModel.AnimData(src.Read(wolfAnimFdid), out waChunked);
                    }
                    var seen = new HashSet<int>();
                    foreach (var d in set.Displays.Values.Where(x => x.ExtendedId == 0).OrderBy(x => x.Id))
                    {
                        if (looked >= 4000 || sw2.ElapsedMilliseconds > 90000) break;
                        if (!set.Models.TryGetValue(d.ModelId, out var mi) || mi.FileId <= 0 || !seen.Add(mi.FileId)) continue;
                        looked++;
                        var rb = src.Read(mi.FileId); var c = CreatureModel.Chunks.Parse(rb);
                        if (!c.Ok) continue;
                        var sq = CreatureModel.Sequences(rb, c);
                        if (c.Anims.Count > 0) { withAfid++; extSeqs += sq.Count(s => (s.Flags & 0x20) == 0); }
                        int si = CreatureModel.StandIndex(sq);
                        if (found == null && si >= 0 && (sq[si].Flags & 0x20) == 0 && sq[si].Id == 0)
                        {
                            var af = c.Anims.FirstOrDefault(a => a.Id == sq[si].Id && a.Sub == sq[si].Sub);
                            if (af.Fdid > 0) found = (d.Id, mi.FileId, af.Fdid);
                        }
                        var sk = src.Read(c.Skins[0]);
                        if (sk == null || sk.Length < 48) continue;
                        int nsub = BitConverter.ToInt32(sk, 28), subO = BitConverter.ToInt32(sk, 32);
                        if (nsub <= 0 || subO < 0 || (long)subO + nsub * 48L > sk.Length) continue;
                        var ids = Enumerable.Range(0, nsub).Select(s => (int)BitConverter.ToUInt16(sk, subO + s * 48)).ToList();
                        if (ids.Any(x => !CreatureModel.DefaultGeoset(x))) { withVariants++; if (variants == null) variants = (d.Id, mi.FileId, string.Join("/", ids.Distinct())); }
                    }
                }
                notes.Add("info real model build: the wolf has " + wolfSeq + " sequences, " + wolfExt + " with their data in .anim files; its Stand is " + (wolfStandExt ? "one of them" : "in the .m2")
                    + "; looked through " + looked + " creature models in " + sw2.ElapsedMilliseconds + " ms: " + withAfid + " name .anim files (" + extSeqs + " sequences), a Stand in an .anim: " + (found == null ? "none" : "display " + found.Value.display + " (model " + found.Value.fdid + ", .anim " + found.Value.afid + ")")
                    + "; " + withVariants + " have geoset variants (first: " + (variants == null ? "none" : "display " + variants.Value.display + ", model " + variants.Value.fdid + ", ids " + variants.Value.ids) + ")");
                Check("real model build: the .anim path on a real file - the wolf's sequence " + wolfAnimSeq + " (.anim " + wolfAnimFdid + ", " + (waChunked ? "chunked" : "raw") + ") read in place of Stand gives keys, unit quaternions, times within its duration",
                    wa != null && wa.Ok && wa.AnimExternal == wolfAnimFdid && wa.AnimProblem == null && wa.AnimKeys > 0 && wa.Bones.Any(b => b.R.Keys > 1)
                    && wa.Bones.All(b => b.R.Keys == 0 || b.R.Gs >= 0 || (Enumerable.Range(0, b.R.Keys).All(k => Math.Abs(Math.Sqrt(Enumerable.Range(0, 4).Sum(j => (double)b.R.Values[k * 4 + j] * b.R.Values[k * 4 + j])) - 1) < 1e-3) && b.R.Times.All(t => t <= wa.AnimDuration))),
                    wa == null ? "no sequence in an .anim" : "id " + wa.AnimId + " keys " + wa.AnimKeys + " duration " + wa.AnimDuration + " " + wa.AnimProblem + " | " + string.Join("; ", wa.Notes));
                if (wa != null) notes.Add("info real model build: wolf sequence " + wolfAnimSeq + " (id " + wa.AnimId + "." + wa.AnimSub + ", " + wa.AnimDuration + " ms) from .anim " + wolfAnimFdid + " (" + (waChunked ? "chunked" : "raw") + "): " + wa.AnimKeys + " keys");
                if (variants == null) notes.Add("skip real geoset variants: no creature model with geoset variants within the budget (" + looked + " models)");
                else
                {
                    var vr = await CreatureModels.EnsureAsync(variants.Value.display);
                    var vm = vr.Model;
                    Check("real model build: a model with geoset variants (display " + variants.Value.display + ", ids " + variants.Value.ids + ") is built with fewer sub-meshes than its skin has, only default ids shown",
                        vr.Ok && vm != null && vm.ShownSubs < vm.RawSubs && vm.HiddenIds.Count > 0 && vm.ShownIds.All(CreatureModel.DefaultGeoset) && vm.Subs.All(s => CreatureModel.DefaultGeoset(s.Id)) && vm.Nv < vm.RawVertices,
                        vr.Ok ? Info((variants.Value.display, "variants", vr, new FileInfo(vr.Path).Length, 0)) : vr.Reason);
                    if (vm != null) notes.Add("info real model build: " + Info((variants.Value.display, "geoset variants", vr, new FileInfo(vr.Path).Length, 0)));
                }
                if (found == null) notes.Add("skip real .anim Stand: no creature model of this install has its Stand in an .anim file (" + looked + " models looked at); the .anim path is checked on the wolf's other sequence above");
                else
                {
                    var er = await CreatureModels.EnsureAsync(found.Value.display);
                    var em = er.Model;
                    Check("real model build: a model whose Stand is in an .anim file (display " + found.Value.display + ", .anim " + found.Value.afid + ") is built with that file's keys",
                        er.Ok && em != null && em.AnimExternal == found.Value.afid && em.AnimProblem == null && em.AnimKeys > 0 && em.Bones.Any(b => b.R.Keys > 1 || b.T.Keys > 1),
                        er.Ok ? Info((found.Value.display, "anim", er, new FileInfo(er.Path).Length, 0)) : er.Reason);
                    if (em != null) notes.Add("info real model build: " + Info((found.Value.display, ".anim Stand", er, new FileInfo(er.Path).Length, 0)));
                }
            }
            finally
            {
                CreatureModels.RootOverride = savedRoot; CreatureModels.SourceForTest = savedSrc; CreatureModels.BuildForTest = savedBuild; CreatureModels.ResetForTest();
            }
        }

        // 3.6 step 5: the beast whose looks the real checks switch between (Reef Shark: four displays at 25 % each in the Classic dump, two shark models)
        const int LookNpc = 12123;

        Task<string> ModelsRealTest(string dir) => RealSection("modelsreal", dir, (d, work, Check, P, notes) => ModelsRealChecks(d, work, Check, P, notes));
    }
}
