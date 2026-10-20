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
    // --selftest "buildings", the real game files (read-only; skipped when this PC has no Forever beta client): the placements of both continents, the bridge rule against the
    // community's names (offline, in the author's check), the build times and sizes, and the 3D page with the real shapes: Stormwind, Orgrimmar and Lakeshire's bridge, frame rates
    // and memory at Medium detail, Off removes the shapes. Needs the pictures and the terrain of the continents (a few minutes the first time: ELANSHUB_WORLDMAP_FULL=1 builds them in the
    // scratch folder; ELANSHUB_BUILDINGS_CACHE=<folder> keeps and reuses them). Pictures with ELANSHUB_BUILDINGS_SHOTS=<folder>.
    public partial class MainWindow
    {
        async Task<Dictionary<string, object>> Wm3Bld()
        {
            var r = await WorldMapView.EvalForTest("window.wm3 && window.wm3.buildings ? window.wm3.buildings() : null");
            if (string.IsNullOrEmpty(r) || r == "null") return null;
            var s = Json.Parse(r) as string;
            return s == null ? null : Json.Obj(s);
        }

        // 3.1.1, the root's ties on the real files: the game lists many files twice with the same score and only one copy is on this disk. The same counts
        // with the old choice (the first copy) and the new one (the local copy): the minimap tiles of both continents and Zephras Isle, the terrain textures
        // (the tex0 files' MDID) and the WMO material textures of both continents; and the root index's size (only the ties' other copies are added)
        void RootTieCounts(string real, List<WorldContinent> conts, CheckFn Check, List<string> notes)
        {
            var I = CultureInfo.InvariantCulture;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using (var now = CascStorage.Open(real, "_classic_beta_"))
            using (var was = CascStorage.Open(real, "_classic_beta_"))
            {
                now.UseRootIndex = true; was.UseRootIndex = true; was.PreferLocalOnTie = false;
                now.ZeroFillEncrypted = true;
                now.ResolveFileIds(new HashSet<int> { 1 }); was.ResolveFileIds(new HashSet<int> { 1 });
                var swd = System.Diagnostics.Stopwatch.StartNew(); now.DecideAllTies(); long decideMs = swd.ElapsedMilliseconds;
                string Mb(long b) => (b / 1048576.0).ToString("0.0", I) + " MB";
                long oldBytes = was.RootIndexCount * 20L;     // what 3.1.0 kept: 4 bytes of id + 16 of key per id
                notes.Add("note real root index: before (3.1.0: ids and keys) " + was.RootIndexCount.ToString("N0", I) + " ids, " + Mb(oldBytes) + ", built in " + was.RootIndexMs + " ms; after " + now.RootIndexCount.ToString("N0", I) + " ids, " + Mb(now.RootIndexBytes) + ", built in " + now.RootIndexMs
                    + " ms; " + now.RootTies.ToString("N0", I) + " ids listed twice or more with the same best score, " + now.RootTiesTookLater.ToString("N0", I) + " of them take a later copy because the first is not on this disk (deciding all of them at once: " + decideMs + " ms; a reader decides only the ids it asks for)");
                Check("real root ties (3.1.1): the root index keeps its ids (" + now.RootIndexCount.ToString("N0", I) + "), grows by its ties' other copies only (" + Mb(oldBytes) + " -> " + Mb(now.RootIndexBytes) + ") and only changes the choice of tied ids (" + now.RootTiesTookLater.ToString("N0", I) + " of " + now.RootTies.ToString("N0", I) + ")",
                    now.RootIndexCount == was.RootIndexCount && now.RootIndexBytes < oldBytes + 8L * 1048576 && now.RootIndexCount > 100000 && now.RootTiesTookLater <= now.RootTies && was.RootTiesTookLater == 0);
                bool Local(CascStorage c, Dictionary<int, byte[]> k, int id) { if (!k.TryGetValue(id, out var ck)) return false; var e = c.EncodingKeyFor(ck); return e != null && c.TryFind(e, out _); }
                // a set of ids: how many resolve to a local copy with the old and with the new choice
                Tuple<int, int, int> Count(IEnumerable<int> ids)
                {
                    var set = new HashSet<int>(ids.Where(i => i > 0));
                    var kn = now.ResolveFileIds(set); var kw = was.ResolveFileIds(set);
                    return Tuple.Create(set.Count, set.Count(i => Local(was, kw, i)), set.Count(i => Local(now, kn, i)));
                }
                byte[] Read(int id) { var k = now.ResolveFileIds(new HashSet<int> { id }); if (!k.TryGetValue(id, out var ck)) return null; try { return now.ReadByContentKey(ck); } catch (Exception) { return null; } }
                string Tag(byte[] d, int p) => new string(new[] { (char)d[p + 3], (char)d[p + 2], (char)d[p + 1], (char)d[p] });
                var lines = new List<string>(); bool ok = true; var fails = new List<string>();
                foreach (var mid in new[] { 0, 1, 2991 })
                {
                    var c = conts.FirstOrDefault(x => x.MapId == mid);
                    if (c == null || c.WdtFileDataId <= 0) { lines.Add("map " + mid + ": not in this game's world maps"); continue; }
                    var wdt = WdtFile.Parse(Read(c.WdtFileDataId));
                    if (!wdt.Ok) { lines.Add(c.Name + ": world table " + wdt.Problem); ok = false; continue; }
                    var mm = Count(wdt.Tiles.Select(t => t.MinimapFdid));
                    string line = c.Name + " (map " + mid + "): minimap tiles " + mm.Item2 + " -> " + mm.Item3 + " of " + mm.Item1;
                    if (mm.Item3 < mm.Item2 || mm.Item3 < mm.Item1 * 9 / 10) { ok = false; fails.Add(c.Name + " minimaps"); }
                    if (mid == 0 || mid == 1)
                    {
                        // the terrain textures: every tile's tex0 (its MDID chunk); the WMO material textures: every building placed in an obj0
                        var tex = new HashSet<int>(); var wmos = new HashSet<int>(); byte[] buf = null; int tex0Read = 0;
                        var tk = now.ResolveFileIds(new HashSet<int>(wdt.Tiles.SelectMany(t => new[] { t.Tex0Fdid, t.Obj0Fdid }).Where(i => i > 0)));
                        foreach (var t in wdt.Tiles)
                        {
                            if (t.Tex0Fdid > 0 && tk.TryGetValue(t.Tex0Fdid, out var ck))
                            {
                                int n; try { n = now.ReadByContentKey(ck, ref buf); } catch (Exception) { n = -1; }
                                if (n > 0) tex0Read++;
                                for (int p = 0; n > 0 && p + 8 <= n;)
                                {
                                    int size = BitConverter.ToInt32(buf, p + 4); if (size < 0 || (long)p + 8 + size > n) break;
                                    if (Tag(buf, p) == "MDID") for (int i = 0; i + 4 <= size; i += 4) tex.Add(BitConverter.ToInt32(buf, p + 8 + i));
                                    p += 8 + size;
                                }
                            }
                            if (t.Obj0Fdid > 0 && tk.TryGetValue(t.Obj0Fdid, out var ock))
                            {
                                byte[] ob; try { ob = now.ReadByContentKey(ock); } catch (Exception) { ob = null; }
                                if (ob != null) foreach (var pl in AdtObjects.ParsePlacements(ob, out _)) if (!pl.HasNameIndex && pl.NameId > 0 && pl.NameId <= int.MaxValue) wmos.Add((int)pl.NameId);
                            }
                        }
                        var wtex = new HashSet<int>();
                        var wk = now.ResolveFileIds(wmos);
                        foreach (var w in wmos)
                        {
                            if (!wk.TryGetValue(w, out var wck)) continue;
                            byte[] wb; try { wb = now.ReadByContentKey(wck); } catch (Exception) { wb = null; }
                            var r = wb == null ? null : WmoRoot.Parse(wb);
                            if (r == null || !r.Ok) continue;
                            foreach (var f in r.TextureFdid) if (f != 0 && f <= int.MaxValue) wtex.Add((int)f);
                        }
                        var tc = Count(tex); var wc = Count(wtex);
                        line += "; terrain textures " + tc.Item2 + " -> " + tc.Item3 + " of " + tc.Item1 + " (from " + tex0Read + " tex0 files); WMO material textures " + wc.Item2 + " -> " + wc.Item3 + " of " + wc.Item1 + " (" + wmos.Count + " WMOs)";
                        if (tc.Item3 < tc.Item2 || tc.Item1 < 100 || tc.Item3 < tc.Item1 * 9 / 10) { ok = false; fails.Add(c.Name + " terrain textures"); }
                        if (wc.Item3 < wc.Item2 || wc.Item1 < 500 || wc.Item3 < wc.Item1 * 9 / 10) { ok = false; fails.Add(c.Name + " WMO textures"); }
                    }
                    lines.Add(line);
                }
                foreach (var l in lines) notes.Add("note real root ties (resolved and on disk, before -> after): " + l);
                Check("real root ties (3.1.1): with the local copy of a tie, at least as many files resolve as before for every kind, and at least 90 % of the minimap tiles, terrain textures and WMO material textures of both continents (" + sw.ElapsedMilliseconds / 1000 + " s)",
                    ok && lines.Count >= 2, string.Join(" | ", lines) + (fails.Count > 0 ? " | short: " + string.Join(", ", fails) : ""));
            }
        }

        async Task BuildingsRealChecks(string dir, string work, CheckFn Check, Func<string, string> P, List<string> notes)
        {
            var I = CultureInfo.InvariantCulture;
            var real = Environment.GetEnvironmentVariable("ELANSHUB_WOW");
            if (string.IsNullOrEmpty(real)) real = WowLocator.Find();
            if (string.IsNullOrEmpty(real) || !Directory.Exists(Path.Combine(real, "Data", "data")) || !Directory.Exists(Path.Combine(real, "_classic_beta_")))
            {
                notes.Add("skip real game files: no WoW install with the Forever beta client on this PC");
                return;
            }
            string cacheEnv = Environment.GetEnvironmentVariable("ELANSHUB_BUILDINGS_CACHE"), shotEnv = Environment.GetEnvironmentVariable("ELANSHUB_BUILDINGS_SHOTS");
            bool full = Environment.GetEnvironmentVariable("ELANSHUB_WORLDMAP_FULL") == "1" || !string.IsNullOrEmpty(cacheEnv);
            var rw = Path.Combine(work, "real"); Directory.CreateDirectory(rw);
            var shots = string.IsNullOrEmpty(shotEnv) ? Path.Combine(rw, "shots") : shotEnv; Directory.CreateDirectory(shots);
            string Shot(string n) => Path.Combine(shots, "buildings-" + n + ".png");
            var savedRoot = WorldMapCache.RootOverride; var savedWowRoot = MapData.WowRoot; var savedEnabled = MapData.Enabled; var savedCache = MapData.CacheFile; var savedWdt = WorldMapService.WdtSourceForTest;
            var savedAdt = WorldMapService.AdtSourceForTest; var savedTile = WorldMapService.TileSourceForTest; var savedWr = WorldMapService.WowRoot;
            var savedLayers = settings.WorldMapLayers; var savedMode = settings.WorldMap3dBuildings; var savedDetail = settings.WorldMap3dDetail; bool saved3d = settings.WorldMap3d; var savedExag = settings.WorldMapExag;
            double savedW = Width, savedH = Height;
            try
            {
                WorldMapService.WdtSourceForTest = null; WorldMapService.AdtSourceForTest = null; WorldMapService.TileSourceForTest = null; BuildingsService.SourceForTest = null; BuildingsService.GroundForTest = null;
                WorldMapService.WowRoot = () => real;
                MapData.ResetForTest();
                MapData.WowRoot = () => real; MapData.Enabled = () => true; MapData.CacheFile = Path.Combine(rw, "mapdata.bin");
                WorldMapCache.RootOverride = string.IsNullOrEmpty(cacheEnv) ? Path.Combine(rw, "cache") : cacheEnv;
                BuildingsService.ResetForTest(); WorldMapService.ResetForTest();
                MapData.Start();
                await WaitFor(() => MapData.Status == MapData.State.Ready || MapData.Status == MapData.State.Unavailable, 180000);
                if (!MapData.Ready) { notes.Add("skip real game files: " + MapData.StatusLine + " (nothing was guessed)"); return; }
                var idx = MapData.Index;
                var conts = WorldMapService.Continents(idx);
                var kal = conts.FirstOrDefault(c => c.MapId == 1); var ek = conts.FirstOrDefault(c => c.MapId == 0);
                if (kal == null || ek == null) { notes.Add("skip real game files: no Kalimdor / Eastern Kingdoms world map in the tables"); return; }
                RootTieCounts(real, conts, Check, notes);

                // ---------------------------------------------------------------- the pictures and terrain the 3D page needs (the scratch folder builds them when asked to, or finds them)
                foreach (var c in new[] { ek, kal })
                {
                    var sw0 = System.Diagnostics.Stopwatch.StartNew();
                    if (WorldMapCache.TerrainBuilt(c.MapId) == null || WorldMapCache.Built(c.MapId) == null)
                    {
                        if (!full) { notes.Add("skip real game files: the pictures and terrain of " + c.Name + " are not built (ELANSHUB_WORLDMAP_FULL=1 builds them in the scratch folder, ELANSHUB_BUILDINGS_CACHE=<folder> keeps them)"); return; }
                        bool b1 = await WorldMapService.EnsureBuilt(c.MapId); bool b2 = await WorldMapService.EnsureTerrain(c.MapId);
                        notes.Add("note real game files: " + c.Name + " pictures " + b1 + ", terrain " + b2 + " built in " + sw0.ElapsedMilliseconds / 1000 + " s");
                        if (!b1 || !b2) { Check("real game files: " + c.Name + " pictures and terrain could be built for the 3D page", false); return; }
                    }
                }

                // ---------------------------------------------------------------- the build: bridges first, then everything, with times and sizes
                var perCont = new Dictionary<int, string>();
                var scanOf = new Dictionary<int, BuildingScan>();
                foreach (var c in new[] { ek, kal })
                {
                    BuildingsService.Remove(c.MapId); BuildingsService.ResetForTest();
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    await BuildingsService.Prepare(c.MapId, "bridges");
                    var stB = BuildingsService.StateOf(c.MapId); long scanMs = sw.ElapsedMilliseconds;
                    var scan = BuildingsService.ScanOf(c.MapId);
                    scanOf[c.MapId] = scan;
                    var estB = stB?.Estimate;
                    Check("real " + c.Name + ": the scan finds " + scan?.Placements.Count + " placed buildings of " + scan?.Models.Count + " different shapes in " + scanMs + " ms (" + scan?.Duplicates + " repeats at tile edges dropped) and " + scan?.Bridge.Count(b => b) + " bridges; the card would say: " + (estB == null ? "?" : WorldMapBuildingsHost.CardText(estB, c.Name, 0)),
                        stB != null && stB.Phase == "ask" && scan != null && scan.Placements.Count > 1000 && scan.Models.Count > 300 && scan.NameIndexSkipped == 0 && scan.Models.Values.All(m => !m.RootMissing) && scan.Bridge.Count(b => b) >= 20 && estB != null && estB.Buildings == scan.Bridge.Count(b => b), stB?.Phase + " " + stB?.Error);
                    sw.Restart();
                    await BuildingsService.Start(c.MapId, "bridges");
                    long msB = sw.ElapsedMilliseconds; var stB2 = BuildingsService.StateOf(c.MapId); long bytesB = WorldMapBuildings.SizeBytes(BuildingsService.DirOf(c.MapId));
                    var estAll = WorldMapBuildings.Estimate(scan, "all", BuildingsService.DirOf(c.MapId));
                    sw.Restart();
                    await BuildingsService.Prepare(c.MapId, "all");
                    await BuildingsService.Start(c.MapId, "all");
                    long msA = sw.ElapsedMilliseconds; var stA = BuildingsService.StateOf(c.MapId); long bytesA = WorldMapBuildings.SizeBytes(BuildingsService.DirOf(c.MapId));
                    var man = WorldMapBuildings.ReadManifest(BuildingsService.DirOf(c.MapId));
                    int stubs = scan.Models.Values.Count(m => { try { var b = File.ReadAllBytes(WorldMapBuildings.ModelFile(BuildingsService.DirOf(c.MapId), m.Fdid)); return b.Length < 40 || b[5] == 0; } catch (Exception) { return true; } });
                    notes.Add("note real " + c.Name + ": bridges " + msB + " ms, " + WorldMapCache.SizeText(bytesB) + "; then all buildings " + msA + " ms, " + WorldMapCache.SizeText(bytesA) + " on disk (" + man?.ModelsBuilt + " shapes, " + stubs + " empty), measured " + Math.Round((man?.SecPerGroup ?? 0) * 1000, 2) + " ms and " + Math.Round((man?.BytesPerGroup ?? 0) / 1024.0, 1) + " KB per group; the card had said " + Math.Round(estAll.Minutes * 60) + " s and " + WorldMapCache.SizeText(estAll.Bytes));
                    perCont[c.MapId] = c.Name + ": " + scan.Placements.Count + " buildings, " + scan.Models.Count + " shapes, " + scan.Bridge.Count(b => b) + " bridges; scan " + scanMs + " ms, bridges " + msB + " ms, all " + msA + " ms, " + WorldMapCache.SizeText(bytesA);
                    Check("real " + c.Name + ": the bridges build (" + msB + " ms) and the whole continent after them (" + msA + " ms, " + WorldMapCache.SizeText(bytesA) + "); the manifest says every shape is made and nothing is half-written",
                        stB2?.Phase == "done" && stA?.Phase == "done" && man != null && man.Valid && man.AllDone && man.BridgesDone && man.ModelsBuilt >= scan.Models.Count && !Directory.EnumerateFiles(BuildingsService.DirOf(c.MapId), "*.tmp", SearchOption.AllDirectories).Any() && bytesA > bytesB && bytesA > 2 * 1024 * 1024, stA?.Phase + " " + stA?.Error);
                }
                foreach (var kv in perCont) notes.Add("note real buildings: " + kv.Value);
                // 2.89, the capitals: the biggest building placed over each city's middle (game x, y), its groups drawn and the shape made of it. Orgrimmar and Stormwind are one WMO each
                // whose streets are flagged interior + exterior lit; before 2.89 only exterior groups were drawn (Orgrimmar: 2 of 138, a gate and a canyon wall - 7,000 triangles)
                {
                    var cityLines = new List<string>(); var city = new Dictionary<string, Tuple<BuildingModelInfo, WmoModel, WmoPlacement>>();
                    foreach (var cc in new[] { new { n = "Orgrimmar", m = 1, x = 1630.0, y = -4380.0 }, new { n = "Thunder Bluff", m = 1, x = -1280.0, y = 120.0 }, new { n = "Darnassus", m = 1, x = 9950.0, y = 2280.0 },
                        new { n = "Stormwind", m = 0, x = -8860.0, y = 540.0 }, new { n = "Ironforge", m = 0, x = -4900.0, y = -950.0 }, new { n = "Undercity", m = 0, x = 1800.0, y = 240.0 } })
                    {
                        GameElevationWorld.ToScene(cc.x, cc.y, out var csx, out var csz);
                        var sc = scanOf[cc.m]; var cd = BuildingsService.DirOf(cc.m);
                        var big = sc.Placements.Where(p => Math.Abs((p.MinX + p.MaxX) / 2 - csx) < 900 && Math.Abs((p.MinZ + p.MaxZ) / 2 - csz) < 900).OrderByDescending(p => (p.MaxX - p.MinX) * (p.MaxZ - p.MinZ)).FirstOrDefault();
                        if (big == null) { cityLines.Add(cc.n + ": nothing placed"); continue; }
                        var mi = sc.Models[(int)big.NameId]; var mf = WorldMapBuildings.ModelFile(cd, mi.Fdid);
                        var wm = File.Exists(mf) ? WmoModel.FromBytes(File.ReadAllBytes(mf)) : null;
                        city[cc.n] = Tuple.Create(mi, wm, big);
                        cityLines.Add(cc.n + ": WMO " + mi.Fdid + " (" + Math.Round(big.MaxX - big.MinX) + " x " + Math.Round(big.MaxZ - big.MinZ) + " yards), " + mi.OutsideGroups + " of " + mi.Groups + " groups drawn (" + mi.ExteriorLitGroups + " open-air ones flagged interior), "
                            + (wm == null ? "no shape" : wm.Stub ? "an empty shape" : wm.Lods[0].TriCount.ToString("N0", I) + " triangles near, " + Math.Round(wm.Half[0] * 2) + " x " + Math.Round(wm.Half[2] * 2) + " yards"));
                    }
                    foreach (var l in cityLines) notes.Add("note real cities: " + l);
                    Func<string, int> Tris = n => city.TryGetValue(n, out var t) && t.Item2 != null && !t.Item2.Stub ? t.Item2.Lods[0].TriCount : 0;
                    var org = city.TryGetValue("Orgrimmar", out var ot) ? ot : null;
                    Check("real Orgrimmar: the city's one big WMO is placed over Durotar's capital and drawn with its open-air groups - " + (org == null ? "nothing placed" : org.Item1.OutsideGroups + " groups (" + org.Item1.ExteriorLitGroups + " exterior lit), " + Tris("Orgrimmar").ToString("N0", I) + " triangles, a shape " + Math.Round(org.Item2?.Half[0] * 2 ?? 0) + " yards across"),
                        org != null && (org.Item3.MaxX - org.Item3.MinX) > 1000 && org.Item1.OutsideGroups >= 15 && org.Item1.ExteriorLitGroups >= 15 && Tris("Orgrimmar") > 60000 && org.Item2 != null && Math.Max(org.Item2.Half[0], org.Item2.Half[2]) * 2 > 900 && Math.Min(org.Item2.Half[0], org.Item2.Half[2]) * 2 > 500, string.Join(" | ", cityLines));
                    Check("real capitals: Stormwind is drawn with its streets too (" + (city.TryGetValue("Stormwind", out var swt) ? swt.Item1.OutsideGroups : 0) + " groups, " + Tris("Stormwind").ToString("N0", I) + " triangles), Thunder Bluff and Darnassus still show, and every city's shape keeps to the 150,000-triangle cap",
                        swt != null && swt.Item1.OutsideGroups >= 60 && Tris("Stormwind") > 60000 && Tris("Thunder Bluff") > 1000 && Tris("Darnassus") > 20000 && city.Values.All(t => t.Item2 == null || t.Item2.Stub || t.Item2.Lods[0].TriCount <= WorldMapBuildings.MaxLod0Tris), string.Join(" | ", cityLines));
                }
                // 2.82, the live dot's floors (LiveElevation): the real buildings' walkable surfaces against an independent height, the game's own for its capital flight
                // masters (TaxiNodes z). Within 30 yards of each, the floor nearest to that height; only the WMOs' outside groups are made, so a master standing indoors
                // (Ironforge, the Undercity) or on a doodad has none - the solver then uses the ground.
                {
                    var gw = new GameElevationWorld(); var fl = new List<double>(); var lines = new List<string>(); int masters = 0, matched = 0, withFloors = 0;
                    foreach (var nm in new[] { "Orgrimmar, Durotar", "Thunder Bluff, Mulgore", "Stormwind, Elwynn", "Ironforge, Dun Morogh", "Undercity, Tirisfal" })
                    {
                        var nd = idx.Set.TaxiNodes.FirstOrDefault(t => t.Name == nm); if (nd == null) continue;
                        masters++; double best = double.NaN, bestD = 0; int hits = 0;
                        for (double dx = -30; dx <= 30; dx += 3) for (double dy = -30; dy <= 30; dy += 3)
                        {
                            fl.Clear(); gw.Floors(nd.MapId, nd.X + dx, nd.Y + dy, fl); hits += fl.Count;
                            foreach (var f in fl) if (double.IsNaN(best) || Math.Abs(f - nd.Z) < Math.Abs(best - nd.Z)) { best = f; bestD = Math.Sqrt(dx * dx + dy * dy); }
                        }
                        if (hits > 0) withFloors++; if (!double.IsNaN(best) && Math.Abs(best - nd.Z) < 1.5) matched++;
                        lines.Add(nm + ": node z " + Math.Round(nd.Z, 1) + ", ground " + Math.Round(gw.Ground(nd.MapId, nd.X, nd.Y), 1) + ", " + hits + " floor hits within 30 yards" + (double.IsNaN(best) ? "" : ", the nearest to the node's height " + Math.Round(best, 1) + " (" + Math.Round(bestD) + " yards away)"));
                    }
                    foreach (var l in lines) notes.Add("note real floors: " + l);
                    Check("real floors: the buildings' walkable surfaces meet the game's own height of its flight masters - " + matched + " of " + masters + " capitals have a floor within 1.5 yards of it nearby (" + withFloors + " have floors at all; indoor halls are not in the 3D buildings)",
                        masters >= 4 && matched >= 2, string.Join(" | ", lines));
                }
                // the placement transform against the game's own extents: the box of each building, turned and moved by our rule, is the box the file stores for it
                {
                    var scanE = scanOf[0];
                    using (var casc2 = CascStorage.Open(real, "_classic_beta_"))
                    using (var src2 = new CascBuildingSource(casc2))
                    {
                        var sample = scanE.Placements.Where(p => Math.Abs(p.RotX) > 0.5 || Math.Abs(p.RotZ) > 0.5).Take(150).Concat(scanE.Placements.Where(p => p.RotX == 0 && p.RotZ == 0 && p.RotY != 0).Take(150)).ToList();
                        src2.Prefetch(sample.Select(p => (int)p.NameId).Distinct());
                        var roots2 = new Dictionary<uint, WmoRoot>(); double tot = 0, mxe = 0; int cnt = 0, tilted = 0;
                        foreach (var p in sample)
                        {
                            if (!roots2.TryGetValue(p.NameId, out var r)) roots2[p.NameId] = r = WmoRoot.Parse(src2.Read((int)p.NameId));
                            if (r == null || !r.Ok) continue;
                            double a0 = 1e9, a1 = -1e9, b0 = 1e9, b1 = -1e9, c0 = 1e9, c1 = -1e9;
                            for (int k = 0; k < 8; k++)
                            {
                                WmoTransform.ToScene(p, (k & 1) == 0 ? r.MinX : r.MaxX, (k & 2) == 0 ? r.MinY : r.MaxY, (k & 4) == 0 ? r.MinZ : r.MaxZ, out var x, out var y, out var z);
                                a0 = Math.Min(a0, x); a1 = Math.Max(a1, x); b0 = Math.Min(b0, y); b1 = Math.Max(b1, y); c0 = Math.Min(c0, z); c1 = Math.Max(c1, z);
                            }
                            double e = Math.Abs(a0 - p.MinX) + Math.Abs(a1 - p.MaxX) + Math.Abs(b0 - p.MinY) + Math.Abs(b1 - p.MaxY) + Math.Abs(c0 - p.MinZ) + Math.Abs(c1 - p.MaxZ);
                            tot += e; mxe = Math.Max(mxe, e); cnt++; if (Math.Abs(p.RotX) > 0.5 || Math.Abs(p.RotZ) > 0.5) tilted++;
                        }
                        notes.Add("note real buildings: transform vs the files' own extents over " + cnt + " placements (" + tilted + " tilted): mean error " + Math.Round(tot / Math.Max(1, cnt), 3) + " yards (sum of the six box sides), worst " + Math.Round(mxe, 2));
                        Check("real transform: the building's box turned by Ry Rx Rz, scaled and moved by our rule is the box the game stores for it (" + cnt + " placements, " + tilted + " of them tilted: mean error " + Math.Round(tot / Math.Max(1, cnt), 3) + " yards over the six sides, worst " + Math.Round(mxe, 2) + ") - so nothing is mirrored or turned the wrong way",
                            cnt >= 200 && tilted >= 30 && tot / cnt < 0.5 && mxe < 6);
                    }
                }

                // ---------------------------------------------------------------- the 3D page with the real shapes
                Width = 1500; Height = 950; await Task.Delay(400);
                settings.WorldMap3d = true; settings.WorldMapLayers = new List<string> { "fm" }; settings.WorldMap3dDetail = "medium"; settings.WorldMapExag = 1.5; settings.WorldMap3dBuildings = "all";
                ShowTab("atlas"); AtlasView.History.Clear();
                async Task<bool> OpenAt(int mapId, double x, double y)
                {
                    var c = mapId == 0 ? ek : kal;
                    await AtlasView.Open("worldmap:" + mapId + "?x=" + x.ToString("R", I) + "&y=" + y.ToString("R", I) + "&z=6&m=3");
                    return await WaitFor(() => WorldMapView.PageReady && WorldMapView.LoadedPage == "3d" && WorldMapView.MapId == mapId, 150000);
                }
                async Task<Dictionary<string, object>> SettleB(int maxMs = 30000)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew(); int calm = 0; Dictionary<string, object> b = null; int last = -1;
                    while (sw.ElapsedMilliseconds < maxMs)
                    {
                        await Task.Delay(500);
                        var st = await Wm3State(); b = await Wm3Bld();
                        if (st != null && b != null && st.Int("inFlight") == 0 && st.Int("heightsLoaded") > 600 && b.Int("modelsLoading") == 0 && b.Int("drawn") == last && b.Int("drawn") > 0) { if (++calm >= 3) break; } else calm = 0;
                        last = b?.Int("drawn") ?? -1;
                    }
                    return b;
                }
                async Task View(double x, double y, double dist, double az, double tilt)
                {
                    WorldMapMath.WorldToLatLng(x, y, out var lat, out var lng);
                    await WmEval("window.wm3.setView(" + lat.ToString("R", I) + "," + lng.ToString("R", I) + "," + dist.ToString("R", I) + "," + az.ToString("R", I) + "," + tilt.ToString("R", I) + ")");
                }
                if (!await OpenAt(0, -8913, 554) || !(WorldMapView.Is3D && WorldMapView.GlOk)) { notes.Add("note real 3D buildings: no 3D here (" + WorldMapView.GlFallbackReason + ")"); return; }
                var results = new List<string>();
                var views = new[]
                {
                    new { map = 0, name = "stormwind", x = -8860.0, y = 540.0, dist = 420.0, az = 160.0, tilt = 30.0 },
                    new { map = 0, name = "stormwind-far", x = -8870.0, y = 560.0, dist = 2600.0, az = 200.0, tilt = 42.0 },
                    new { map = 1, name = "orgrimmar", x = 1630.0, y = -4380.0, dist = 700.0, az = 0.0, tilt = 68.0 },
                    new { map = 1, name = "orgrimmar-far", x = 1630.0, y = -4380.0, dist = 2600.0, az = 15.0, tilt = 42.0 },
                    new { map = 1, name = "orgrimmar-city", x = 1600.0, y = -4400.0, dist = 1300.0, az = 0.0, tilt = 25.0 },      // 2.89: from above, into the canyon (the city is one WMO)
                    new { map = 0, name = "lakeshire", x = -9347.0, y = -2281.0, dist = 220.0, az = 35.0, tilt = 30.0 },
                };
                int curMap = 0; var fpsBy = new Dictionary<string, double>(); var drawnBy = new Dictionary<string, int>(); Func<string, int> DB = n => drawnBy.TryGetValue(n, out var q) ? q : 0;
                foreach (var v in views)
                {
                    if (v.map != curMap) { if (!await OpenAt(v.map, v.x, v.y)) { Check("real 3D buildings: the page for map " + v.map + " opened", false); continue; } curMap = v.map; }
                    await View(v.x, v.y, v.dist, v.az, v.tilt);
                    var b = await SettleB();
                    await WmEval("window.__bench = null; window.wm3.bench(3500).then(function (r) { window.__bench = r; }); 1"); await Task.Delay(3900);
                    string bjson = null; for (int w = 0; w < 40 && bjson == null; w++) { var br = await WmEval("window.__bench"); if (!string.IsNullOrEmpty(br) && br != "null") bjson = Json.Parse(br) as string; else await Task.Delay(250); }
                    var bench = bjson == null ? null : Json.Obj(bjson);
                    fpsBy[v.name] = bench == null ? 0 : (bench.TryGetValue("avg", out var av) && av is double ad ? ad : 0);
                    var st = await Wm3State(); b = await Wm3Bld() ?? b;
                    await Task.Delay(300);
                    var png = SelfTestClock.ShotsOn ? await WorldMapView.CaptureForTest() : null; if (png != null) File.WriteAllBytes(Shot(v.name), png);
                    var mem = WorldMapView.ProcessMemoryMbForTest(out var det);
                    drawnBy[v.name] = b?.Int("drawn") ?? 0;
                    results.Add(v.name + ": " + b?.Int("drawn") + " buildings drawn (" + string.Join("/", (b?.List("byLod") ?? new List<object>()).Select(o => Convert.ToString(o, I))) + " by level), " + b?.Int("tris") + " triangles, " + (b != null && b.TryGetValue("memMb", out var mmv) ? Convert.ToDouble(mmv, I) : 0) + " MB of shapes, " + st?.Int("drawCalls") + " calls, bench " + Json.Write(bench) + ", WebView2 " + mem + " MB (" + det + ")");
                    notes.Add("note real 3D buildings at Medium: " + results.Last());
                    // the same view with Buildings Off: what the buildings cost in frame rate and memory
                    await WmEval("window.wm3.buildingMode('off')"); await Task.Delay(900); await WmEval("window.wm3.step(4,130)"); await Task.Delay(500);
                    await WmEval("window.__bench = null; window.wm3.bench(3000).then(function (r) { window.__bench = r; }); 1"); await Task.Delay(3400);
                    string bj0 = null; for (int w = 0; w < 40 && bj0 == null; w++) { var br = await WmEval("window.__bench"); if (!string.IsNullOrEmpty(br) && br != "null") bj0 = Json.Parse(br) as string; else await Task.Delay(250); }
                    var bench0 = bj0 == null ? null : Json.Obj(bj0); var mem0 = WorldMapView.ProcessMemoryMbForTest(out var det0);
                    notes.Add("note real 3D buildings, " + v.name + " with Buildings Off: orbit " + (bench0 != null && bench0.TryGetValue("avg", out var a0) && a0 is double a0d ? Math.Round(a0d, 1) : 0) + " fps average, WebView2 " + mem0 + " MB (" + det0 + ") - the buildings add " + (mem - mem0) + " MB");
                    await WmEval("window.wm3.buildingMode('all')"); await Task.Delay(900); await WmEval("window.wm3.step(4,130)");
                }
                Check("real 3D buildings: Stormwind (" + DB("stormwind") + " drawn near, " + DB("stormwind-far") + " far), Orgrimmar (" + DB("orgrimmar") + " / " + DB("orgrimmar-far") + ") and Lakeshire (" + DB("lakeshire") + ") have building shapes on screen",
                    DB("stormwind") > 3 && DB("orgrimmar") > 3 && DB("lakeshire") >= 1);

                // Lakeshire's bridge: the deck is above the water under it
                await OpenAt(0, -9347, -2281);
                await View(-9347, -2281, 220, 35, 30); await SettleB();
                WorldMapMath.WorldToLatLng(-9347, -2281, out var llat, out var llng);
                var bridgesNear = Json.Parse(await WmEval("window.wm3.bridges(" + llat.ToString("R", I) + "," + llng.ToString("R", I) + ",6)")) as string;
                var bl = Json.Parse(bridgesNear) as List<object>;
                var nearest = bl?.OfType<Dictionary<string, object>>().FirstOrDefault();
                bool deckOk = nearest != null && nearest.TryGetValue("water", out var wv) && wv is double wd && (double)nearest["deck"] > wd + 0.5;
                notes.Add("note real 3D buildings: bridges near Lakeshire: " + bridgesNear);
                Check("real 3D buildings: the bridge at Lakeshire (Redridge) has its deck above the water surface in the 3D scene (deck " + (nearest == null ? "?" : Convert.ToDouble(nearest["deck"], I).ToString("0.0", I)) + " vs water " + (nearest == null || !(nearest["water"] is double) ? "none" : Convert.ToDouble(nearest["water"], I).ToString("0.0", I)) + ")", deckOk, bridgesNear);

                // the switch: Bridges only shows fewer than All; Off removes every one
                var bAll = await Wm3Bld();
                await WmEval("window.wm3.buildingMode('bridges')"); await Task.Delay(700); await WmEval("window.wm3.step(3,120)"); await SettleB(8000);
                var bBr = await Wm3Bld();
                await WmEval("window.wm3.buildingMode('off')"); await Task.Delay(500); await WmEval("window.wm3.step(3,120)");
                var bOff = await Wm3Bld();
                Check("real 3D buildings: Bridges only draws " + bBr?.Int("drawn") + " of the " + bAll?.Int("drawn") + " that All buildings draws here, and Off removes them all (" + bOff?.Int("drawn") + " drawn, " + bOff?.Int("visibleMeshes") + " meshes visible)",
                    bAll != null && bBr != null && bOff != null && bBr.Int("drawn") < bAll.Int("drawn") && bBr.Int("drawn") >= 1 && bOff.Int("drawn") == 0 && bOff.Int("visibleMeshes") == 0, Json.Write(bBr) + " | " + Json.Write(bOff));
                var png2 = SelfTestClock.ShotsOn ? await WorldMapView.CaptureForTest() : null; if (png2 != null) File.WriteAllBytes(Shot("lakeshire-off"), png2);
            }
            finally
            {
                Width = savedW; Height = savedH;
                settings.WorldMapLayers = savedLayers; settings.WorldMap3dBuildings = savedMode; settings.WorldMap3dDetail = savedDetail; settings.WorldMap3d = saved3d; settings.WorldMapExag = savedExag;
                try { WorldMapView.Dispose(); } catch (Exception) { }
                WorldMapCache.RootOverride = savedRoot; MapData.WowRoot = savedWowRoot; MapData.Enabled = savedEnabled; MapData.CacheFile = savedCache; WorldMapService.WdtSourceForTest = savedWdt; WorldMapService.AdtSourceForTest = savedAdt;
                WorldMapService.TileSourceForTest = savedTile; WorldMapService.WowRoot = savedWr;
                try { ResetAtlasForTest(); } catch (Exception) { }
            }
        }
    }
}
