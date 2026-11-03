using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.WorldMap;

namespace ElansAddonHub
{
    // --selftest "worldlight" (3.8, sky, fog and light per zone on the 3D world map): the six light tables (Light, LightParams, LightData, ZoneLight,
    // ZoneLightPoint, LightSkybox) on synthetic WDC5 tables in their 1.60.1.70707 layouts (an unknown layout, a foreign or missing table = that table off with
    // a reason; without Light or LightData the feature is off), the resolver (WorldLight.LightAt: the map's default light, the zone polygons with their 50-yard
    // band and Zmin / Zmax, the spheres between falloff start and end, inner over outer, the time keys interpolated round midnight, the fields this build
    // leaves 0 = not set), the camera's fade to the default light when zoomed out, when the host works it out again, the saved index, the "light" message
    // (built and checked) and view3's ground height. The page applying it is the worldmap section's (MainWindow.WorldMapTest.Page3d.cs); the real tables
    // and the real page are worldlightreal's.
    public partial class MainWindow
    {
        // ---- the tables, in the layouts of 1.60.1.70707 (LightTables)
        // Light: GameCoords[3] Start End ContinentID LightParamsID[8]; the id in the id list
        static byte[] WlLights(uint layout = 0x5F16BC84, uint table = 0x5CCAA0BA)
        {
            var fs = new[] { SdFl(3), SdFl(), SdFl(), SdI(), SdI(8) };
            object[] R(float x, float y, float z, float s, float e, int map, int p) => new object[] { new[] { x, y, z }, s, e, map, new[] { p, p + 1, 0, 0, 0, 0, 0, 0 } };
            var rows = new[]
            {
                R(0, 0, 0, 0, 0, 0, 100),                // 1: the default light of map 0
                R(1000, 1000, 0, 100, 300, 0, 200),      // 2: a sphere
                R(1000, 1000, 0, 20, 50, 0, 300),        // 3: a small sphere inside it
                R(5000, 5000, 0, 1, 2, 0, 400),          // 4: the zone polygon's light (a tiny sphere far away)
                R(0, 0, 0, 0, 0, 0, 900),                // 5: a second default of map 0 (the lowest id counts)
                R(-2000, -2000, 0, 50, 40, 0, 300),      // 6: end below start: full weight inside its end
            };
            return SdBuild(table, layout, fs, rows, new[] { 1, 2, 3, 4, 5, 6 });
        }

        // LightData: 63 fields, the id inline (0), LightParamID (1), Time (2), colours (3-19, 34-36), FogEnd / FogScaler / FogDensity floats (21-23)
        static readonly HashSet<int> WlDataFloats = new HashSet<int> { 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 37, 38, 40 };
        static byte[] WlData(uint layout = 0x360DA016)
        {
            var fs = Enumerable.Range(0, 63).Select(i => i >= 60 ? SdFl(4) : WlDataFloats.Contains(i) ? SdFl() : SdI()).ToArray();
            int n = 22000;
            object[] R(int param, int time, int fog, float fogEnd, int direct = 0x808080, int river = 0, int top = 0x00265F)
            {
                var r = new object[63]; r[0] = ++n; r[1] = param; r[2] = time;
                r[3] = direct; r[4] = 0x68829A; r[5] = top; r[6] = 0x2080C0; r[7] = 0x3090D0; r[8] = 0x40A0E0; r[9] = 0x50B0F0; r[10] = fog; r[11] = 0xFFEEDD;
                r[16] = 0; r[18] = river; r[21] = fogEnd; r[22] = 0.25f; r[23] = 1.5f;
                for (int k = 60; k < 63; k++) r[k] = new float[4];
                return r;
            }
            var rows = new[]
            {
                R(100, 1440, 0x659EBB, 18000), R(100, 0, 0x102030, 0), R(100, 2160, 0x204060, 0),       // out of time order on purpose; FogEnd set only at noon
                R(200, 1440, 0x009ACD, 6500, 0x54A0D3, 0x112233), R(200, 600, 0x000000, 6500, 0x54A0D3, 0x112233),
                R(300, 1440, 0xFF0000, 1000),
                R(400, 1440, 0x659EBB, 18000, 0xECB374),
                R(900, 1440, 0x00FF00, 9),
            };
            return SdBuild(LightTables.Data.TableHash, layout, fs, rows, rows.Select(r => (int)r[0]).ToArray(), idIndex: 0, idList: false);
        }

        // LightParams: 39 fields, LightSkyboxID (3), the four water alphas (6-9)
        static byte[] WlParams()
        {
            var fs = Enumerable.Range(0, 39).Select(i => i <= 1 ? SdFl(3) : i >= 5 && i <= 9 ? SdFl() : SdI()).ToArray();
            object[] R(int skybox) { var r = new object[39]; r[0] = new float[3]; r[1] = new float[3]; r[3] = skybox; r[5] = 0.2f; r[6] = 0.5f; r[7] = 1f; r[8] = 0.75f; r[9] = 1f; return r; }
            return SdBuild(LightTables.Params.TableHash, 0xA7F31923, fs, new[] { R(0), R(7), R(0), R(0) }, new[] { 100, 200, 300, 400 });
        }

        // ZoneLight: Name MapID LightID Flags Zmin Zmax TransitionType PlayerConditionID; ZoneLightPoint: Pos[2] PointOrder + ZoneLightID (relationship)
        static byte[] WlZones(uint layout = 0x94CE95E0)
        {
            var fs = new[] { SdS(), SdI(), SdI(), SdI(), SdFl(), SdFl(), SdI(), SdI() };
            var rows = new[] { new object[] { "Square Zone Light", 0, 4, 0, -100f, 200f, 0, 0 }, new object[] { "Too Few Points", 0, 4, 0, -64000f, 64000f, 0, 0 } };
            return SdBuild(LightTables.Zone.TableHash, layout, fs, rows, new[] { 10, 11 });
        }
        static byte[] WlZonePoints()
        {
            var fs = new[] { SdFl(2), SdI() };
            // the square (-500..-100, -500..-100), its points out of PointOrder; two points of "Too Few Points"
            var rows = new[] { new object[] { new[] { -100f, -500f }, 2 }, new object[] { new[] { -500f, -500f }, 1 }, new object[] { new[] { -500f, -100f }, 4 }, new object[] { new[] { -100f, -100f }, 3 },
                new object[] { new[] { 0f, 0f }, 1 }, new object[] { new[] { 10f, 10f }, 2 } };
            return SdBuild(LightTables.ZonePoint.TableHash, 0xDE2377FB, fs, rows, new[] { 31, 32, 33, 34, 35, 36 }, foreign: new[] { 10, 10, 10, 10, 11, 11 });
        }
        static byte[] WlSkyboxes()
        {
            var fs = new[] { SdS(), SdI(), SdI(), SdI() };
            return SdBuild(LightTables.Skybox.TableHash, 0x9D4956FF, fs, new[] { new object[] { @"Environments\Stars\Test.mdx", 0, 130636, 0 } }, new[] { 7 });
        }

        static SpellRaw WlRaw(byte[] lights = null, byte[] data = null, byte[] zones = null, bool noParams = false, bool noPoints = false)
        {
            var raw = new SpellRaw();
            raw.Tables[LightTables.Light.Name] = lights ?? WlLights();
            raw.Tables[LightTables.Data.Name] = data ?? WlData();
            if (!noParams) raw.Tables[LightTables.Params.Name] = WlParams();
            raw.Tables[LightTables.Zone.Name] = zones ?? WlZones();
            if (!noPoints) raw.Tables[LightTables.ZonePoint.Name] = WlZonePoints();
            raw.Tables[LightTables.Skybox.Name] = WlSkyboxes();
            return raw;
        }

        async Task<string> WorldLightTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            void Check(string what, bool cond, string detail = null) { SelfTestClock.Mark(what); notes.Add((cond ? "ok   " : "FAIL ") + what + (!cond && detail != null ? " [" + detail + "]" : "")); ok &= cond; }
            var work = Path.Combine(dir, "worldlight");
            var savedCache = WorldLight.CacheFile; var savedAuto = WorldLight.AutoStart;
            try
            {
                if (Directory.Exists(work)) Util.DeleteDir(work);
                Directory.CreateDirectory(work);
                WorldLight.AutoStart = false; WorldLight.ResetForTest(); WorldLight.CacheFile = Path.Combine(work, "cache", "worldlight.bin");
                Check("fixture: everything of this test is under the scratch folder (never the real game)", Path.GetFullPath(work).StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase));
                Check("service: with AutoStart off Start() reads nothing", await Task.Run(() => { WorldLight.Start(); return WorldLight.Status == WorldLight.State.NotStarted; }));

                // ---- the tables
                var set = LightSet.Parse(WlRaw());
                Check("parse: the six tables in the 1.60.1.70707 layouts, no problems (6 lights, 5 light params with keys, 4 params, 2 zone polygons, 1 skybox)",
                    set.Usable && set.Problems.Count == 0 && set.Lights?.Count == 6 && set.Keys?.Count == 5 && set.Params?.Count == 4 && set.Zones?.Count == 2 && set.Skyboxes?.Count == 1
                    && LightTables.All.All(d => set.LayoutsSeen.GetValueOrDefault(d.Name) == d.Layouts[0]), string.Join("; ", set.Problems.Values));
                var l2 = set.Lights?.GetValueOrDefault(2); var k100 = set.Keys?.GetValueOrDefault(100); var sq = set.Zones?.FirstOrDefault(z => z.Id == 10);
                Check("parse: a sphere's coordinates, falloff and LightParamsID[0..1]; LightData's inline id and LightParamID with its keys sorted by Time; a zone's name, light, Zmin / Zmax and its points in PointOrder",
                    l2 != null && l2.X == 1000 && l2.Y == 1000 && l2.Start == 100 && l2.End == 300 && l2.Params[0] == 200 && l2.Params[1] == 201 && !l2.IsDefault && set.Lights[1].IsDefault
                    && k100 != null && k100.Select(k => k.Time).SequenceEqual(new[] { 0, 1440, 2160 }) && k100[1].SkyFog == 0x659EBB && k100[1].FogEnd == 18000 && Math.Abs(k100[1].FogScaler - 0.25f) < 1e-6
                    && sq != null && sq.Name == "Square Zone Light" && sq.LightId == 4 && sq.Zmin == -100 && sq.Zmax == 200 && sq.Points.Count == 4 && sq.Points[0][0] == -500 && sq.Points[0][1] == -500 && sq.Points[1][0] == -100 && sq.Points[1][1] == -500
                    && set.Params[200].Skybox == 7 && Math.Abs(set.Params[100].OceanShallow - 0.75f) < 1e-6 && set.Skyboxes[7] == 130636,
                    string.Join(" ", k100?.Select(k => k.Time) ?? new int[0]) + " | " + string.Join(" ", sq?.Points.Select(p => p[0] + "/" + p[1]) ?? new string[0]));

                // ---- the time keys
                var ev = WorldLight.Eval(set, 100, 1440); var e720 = WorldLight.Eval(set, 100, 720); var e2500 = WorldLight.Eval(set, 100, 2500);
                LightResult At(double x, double y, double z = double.NaN, double t = WorldLight.Noon, int map = 0) => WorldLight.LightAt(set, map, x, y, z, t);
                var noon = At(-5000, 5000); var r720 = At(-5000, 5000, double.NaN, 720); var r2500 = At(-5000, 5000, double.NaN, 2500); var r60 = At(-5000, 5000, double.NaN, 60 + 2880);
                bool Near(int a, int b) => a >= 0 && b >= 0 && Math.Abs(((a >> 16) & 255) - ((b >> 16) & 255)) <= 1 && Math.Abs(((a >> 8) & 255) - ((b >> 8) & 255)) <= 1 && Math.Abs((a & 255) - (b & 255)) <= 1;
        int Mix(int a, int b, double t) { int C(int sh) => (int)Math.Round(((a >> sh) & 255) + (((b >> sh) & 255) - ((a >> sh) & 255)) * t); return (C(16) << 16) | (C(8) << 8) | C(0); }
                Check("time: noon (1440) is the noon key; 720 is half-way between the 0 and 1440 keys; 2500 lies between 2160 and the 0 key of the next day (the wrap at midnight); a time past 2880 wraps",
                    ev != null && noon.SkyFog == 0x659EBB && Near(r720.SkyFog, Mix(0x102030, 0x659EBB, 0.5)) && Near(r2500.SkyFog, Mix(0x204060, 0x102030, 340.0 / 720)) && Near(r60.SkyFog, Mix(0x102030, 0x659EBB, 60.0 / 1440)),
                    LightResult.Hex(noon.SkyFog) + " " + LightResult.Hex(r720.SkyFog) + " " + LightResult.Hex(r2500.SkyFog) + " " + LightResult.Hex(r60.SkyFog));
                var w300 = At(1000, 1080, double.NaN, 300);      // inside sphere 2's falloff start, outside sphere 3: sphere 2's light alone
                Check("time: before a light's first key (300 < 600) it comes from its last key of the day before (1440 -> 600 over midnight: 1740 of 2040 half-minutes)",
                    w300 != null && Near(w300.SkyFog, Mix(0x009ACD, 0x000000, 1740.0 / 2040)) && w300.From.Any(f => f == "sphere 2 1.00"), LightResult.Hex(w300?.SkyFog ?? -1) + " " + w300?.Source);
                Check("time: FogEnd is set only on the noon key here (0 = not set): it is 18000 at every time, never pulled to 0 by the keys that leave it out",
                    noon.FogEnd == 18000 && r720.FogEnd == 18000 && r2500.FogEnd == 18000 && noon.DefaultFogEnd == 18000, noon.FogEnd + " " + r720.FogEnd + " " + r2500.FogEnd);

                // ---- default, spheres, zones
                Check("default: far from every light only the map's default light counts (the lowest id of map 0's defaults: 1, not 5); another map without a default of its own uses map 0's",
                    noon.From.Count == 1 && noon.DefaultLightId == 1 && noon.Direct == 0x808080 && noon.SkyTop == 0x00265F && At(-5000, 5000, double.NaN, WorldLight.Noon, 1)?.DefaultLightId == 1 && At(0, 0, double.NaN, WorldLight.Noon, 2) != null, noon.Source);
                Check("default: the water colours this light leaves 0 are not set (-1, left out of the message), the ones it sets are kept", noon.RiverClose == -1 && noon.OceanClose == -1 && noon.Ambient == 0x68829A);
                var c0 = At(1000, 1000); var c40 = At(1000, 1040); var c200 = At(1000, 1200); var c290 = At(1000, 1290); var c320 = At(1000, 1320);
                double SphereW(double x, double y) { double d = Math.Sqrt((x - 1000) * (x - 1000) + (y - 1000) * (y - 1000)); return d >= 300 ? 0 : Math.Min(1, Math.Max(0, (100 - d) / 200 + 1)); }
                Check("spheres: at the centre the small inner sphere wins (laid over the outer one); 40 yd out it is 1/3 of the way to the outer sphere's colour; the falloff is linear from start to end (200 yd: half), 0 beyond the end",
                    c0.SkyFog == 0xFF0000 && Near(c40.SkyFog, Mix(0x009ACD, 0xFF0000, 1 - (40 - 20) / 30.0)) && Near(c200.SkyFog, Mix(0x659EBB, 0x009ACD, 0.5)) && Near(c290.SkyFog, Mix(0x659EBB, 0x009ACD, SphereW(1000, 1290))) && c320.SkyFog == 0x659EBB && c320.From.Count == 1,
                    LightResult.Hex(c0.SkyFog) + " " + LightResult.Hex(c40.SkyFog) + " " + LightResult.Hex(c200.SkyFog) + " " + LightResult.Hex(c320.SkyFog) + " | " + c200.Source);
                Check("spheres: FogEnd blends like a colour (half-way 18000 -> 6500 = 12250); a water colour only the sphere sets is the sphere's (#112233) even half-way, where the default sets none",
                    Math.Abs(c200.FogEnd - 12250) < 0.5 && c200.RiverClose == 0x112233 && c320.RiverClose == -1 && c200.DefaultFogEnd == 18000, c200.FogEnd + " " + LightResult.Hex(c200.RiverClose));
                var high = At(1000, 1000, 1000); var low = At(1000, 1000, 0);
                Check("spheres: with the height known the distance is 3D (1000 yd above the centre is outside), without it (NaN) horizontal", high.SkyFog == 0x659EBB && low.SkyFog == 0xFF0000);
                var end = At(-2000, -1965);
                Check("spheres: falloff end below its start = full weight inside the end", end.SkyFog == 0xFF0000 && end.From.Any(f => f == "sphere 6 1.00"), end.Source);
                var zIn = At(-300, -300); var zEdge = At(-100, -300); var z25 = At(-75, -300); var zOut = At(-40, -300); var zAbove = At(-300, -300, 400); var zOk = At(-300, -300, 100);
                Check("zones: deep inside the polygon its light counts fully (warm direct #ECB374), on the border half, 25 yd outside a quarter, 60 yd outside not at all",
                    zIn.Direct == 0xECB374 && zIn.From.Any(f => f == "zone 10 Square Zone Light 1.00") && Near(zEdge.Direct, Mix(0x808080, 0xECB374, 0.5)) && Near(z25.Direct, Mix(0x808080, 0xECB374, 0.25)) && zOut.Direct == 0x808080,
                    LightResult.Hex(zIn.Direct) + " " + LightResult.Hex(zEdge.Direct) + " " + LightResult.Hex(z25.Direct) + " " + LightResult.Hex(zOut.Direct) + " | " + zIn.Source);
                Check("zones: Zmin / Zmax count when the height is known (400 above Zmax 200: out; 100: in), a polygon of fewer than 3 points is left out",
                    zAbove.Direct == 0x808080 && zOk.Direct == 0xECB374 && !At(5, 5).From.Any(f => f.Contains("Too Few")), zAbove.Source);
                Check("zones: the point-in-polygon rule (inside, outside, a corner's far side)", WorldLight.Inside(sq.Points, -300, -300) && !WorldLight.Inside(sq.Points, -50, -300) && !WorldLight.Inside(sq.Points, -600, -600));
                Check("skybox: the strongest light that has one names it (sphere 2's params: skybox 7), none elsewhere (not drawn yet)", c200.Skybox == 7 && noon.Skybox == 0 && zIn.Skybox == 0);

                // ---- the camera: zoomed out the zone lights fade to the map's default
                var near = WorldLight.ForView(set, 0, 1000, 1000, double.NaN, 1500); var mid = WorldLight.ForView(set, 0, 1000, 1000, double.NaN, 8000); var far = WorldLight.ForView(set, 0, 1000, 1000, double.NaN, 30000);
                Check("view: close in (1500 yd) the spot's own light; at 8000 yd every local light at half weight; from 12000 yd (the continent) only the default light",
                    near.SkyFog == 0xFF0000 && near.Local == 1 && Math.Abs(mid.Local - 0.5) < 1e-9 && Near(mid.SkyFog, Mix(Mix(0x659EBB, 0x009ACD, 0.5), 0xFF0000, 0.5)) && far.SkyFog == 0x659EBB && far.Local == 0 && far.From.Count == 1,
                    LightResult.Hex(mid.SkyFog) + " " + mid.Local + " " + far.Source);
                Check("host: the light is worked out again after a move of more than 300 yd, another map, a zoom that changes the fade, the first time; not for a small pan or a zoom inside the close band",
                    WorldLight.ShouldRecompute(0, double.NaN, 0, 0, 0, 1, 1, 1000) && WorldLight.ShouldRecompute(0, 0, 0, 2000, 0, 301, 0, 2000) && WorldLight.ShouldRecompute(0, 0, 0, 2000, 1, 0, 0, 2000)
                    && WorldLight.ShouldRecompute(0, 0, 0, 2000, 0, 0, 0, 8000) && !WorldLight.ShouldRecompute(0, 0, 0, 2000, 0, 200, 100, 2000) && !WorldLight.ShouldRecompute(0, 0, 0, 1000, 0, 0, 0, 3500));

                // ---- unknown layout / foreign / missing tables
                var badData = LightSet.Parse(WlRaw(data: WlData(0x12345678)));
                Check("layout: an unknown LightData layout switches the feature off ('all', 'game updated', the hash named): no light is worked out, the map keeps its fixed look",
                    !badData.Usable && badData.Problems.TryGetValue("all", out var ba) && ba.StartsWith("game updated") && ba.Contains("12345678") && WorldLight.LightAt(badData, 0, 0, 0, double.NaN, 1440) == null, string.Join("; ", badData.Problems.Values));
                var badLights = LightSet.Parse(WlRaw(lights: WlLights(0x5F16BC84, 0x11111111)));
                Check("layout: a foreign Light table is off with its reason and so is the feature", !badLights.Usable && badLights.Problems["Light"].Contains("not the table") && badLights.Problems.ContainsKey("all"));
                var badZones = LightSet.Parse(WlRaw(zones: WlZones(0xCAFEBABE)));
                var noPoints = LightSet.Parse(WlRaw(noPoints: true)); var noParams = LightSet.Parse(WlRaw(noParams: true));
                Check("layout: an unknown ZoneLight layout or a missing ZoneLightPoint = no zone polygons (the rest works); a missing LightParams = no skybox ids (the rest works)",
                    badZones.Usable && badZones.Zones == null && badZones.Problems["ZoneLight"].StartsWith("game updated") && WorldLight.LightAt(badZones, 0, -300, -300, double.NaN, 1440)?.Direct == 0x808080
                    && noPoints.Usable && noPoints.Zones == null && noPoints.Problems["ZoneLightPoint"].Contains("not in this game install") && noPoints.Notes.Any(n => n.Contains("both"))
                    && noParams.Usable && noParams.Params == null && WorldLight.LightAt(noParams, 0, 1000, 1200, double.NaN, 1440)?.Skybox == 0,
                    string.Join("; ", badZones.Problems.Values.Concat(noPoints.Problems.Values)));
                Check("layout: nothing at all = 'all' unavailable", LightSet.Parse(new SpellRaw()).Problems.ContainsKey("all") && WorldLight.LightAt(null, 0, 0, 0, 0, 0) == null);

                // ---- the saved index
                var cf = WorldLight.CacheFile;
                bool wrote = WorldLightIndexFile.TryWrite(cf, "wowf|1.60.1.70707|abc", set);
                var back = WorldLightIndexFile.TryRead(cf);
                var bz = back?.Set.Zones?.FirstOrDefault(z => z.Id == 10);
                Check("index: the tables survive a save (lights, keys, params, zone polygons, skyboxes, layouts) and the resolver gives the same answers from it",
                    wrote && back != null && back.Build == "wowf|1.60.1.70707|abc" && back.Format == WorldLight.FormatVersion && back.Set.Lights.Count == 6 && back.Set.Keys.Count == 5 && bz != null && bz.Points.Count == 4 && bz.Name == "Square Zone Light"
                    && back.Set.LayoutsSeen.Count == LightTables.All.Length && WorldLight.LightAt(back.Set, 0, 1000, 1200, double.NaN, 1440).ToString() == c200.ToString() && WorldLight.LightAt(back.Set, 0, -300, -300, double.NaN, 1440).Direct == 0xECB374 && back.Set.Skyboxes[7] == 130636);
                int reads = 0;
                var same = WorldLight.Obtain(back, "wowf|1.60.1.70707|abc", cf, () => { reads++; return set; }, out var from1);
                var other = WorldLight.Obtain(back, "wowf|1.60.1.70800|def", cf, () => { reads++; return set; }, out var from2);
                Check("index: the same build reads the saved file, another build reads the game files (and saves them)", reads == 1 && from1 == "cache file" && from2 == "game files" && WorldLightIndexFile.TryRead(cf)?.Build == "wowf|1.60.1.70800|def");
                File.WriteAllBytes(cf, new byte[] { 1, 2, 3 });
                Check("index: a damaged file reads as none", WorldLightIndexFile.TryRead(cf) == null);

                // ---- the message
                var lj = WorldMapProtocol.Light(c200); var lo = lj == null ? null : Json.Obj(lj);
                Check("message: light {on, sky[6], fog, amb, dir, sun, river (only where set), fogEnd, fogDef, local, src} with #rrggbb colours",
                    lo != null && lo.Str("t") == "light" && lo.Bool("on") && lo.List("sky")?.Count == 6 && (string)lo.List("sky")[5] == LightResult.Hex(c200.SkyFog) && lo.Str("fog") == LightResult.Hex(c200.SkyFog)
                    && lo.Str("river") == "#112233" && !lo.ContainsKey("ocean") && !lo.ContainsKey("hzn") && lo.Num("fogEnd") == 12250 && lo.Num("fogDef") == 18000 && lo.Num("local") == 1 && lo.Str("src").Contains("sphere 2"), lj);
                var broken = WorldLight.LightAt(set, 0, 1000, 1200, double.NaN, 1440); broken.SkyFog = -5;
                var nan = WorldLight.LightAt(set, 0, 1000, 1200, double.NaN, 1440); nan.FogEnd = double.NaN;
                var huge = WorldLight.LightAt(set, 0, 1000, 1200, double.NaN, 1440); huge.FogEnd = 9e9; huge.From.Add(new string('x', 400) + "\u0007");
                var hj = Json.Obj(WorldMapProtocol.Light(huge));
                var off = Json.Obj(WorldMapProtocol.LightOff("Game lighting is off in Settings"));
                Check("message: a missing colour or a NaN is not sent (null); FogEnd is clamped to 100000 and the source text cut and cleaned; off = {on:false, why}",
                    WorldMapProtocol.Light(broken) == null && WorldMapProtocol.Light(nan) == null && WorldMapProtocol.Light(null) == null && hj.Num("fogEnd") == 100000 && hj.Str("src").Length <= 200 && !hj.Str("src").Contains("\u0007")
                    && off.Str("t") == "light" && !off.Bool("on") && off.Str("why") == "Game lighting is off in Settings");
                var g1 = WorldMapProtocol.Parse("{\"t\":\"view3\",\"lat\":-120,\"lng\":130,\"dist\":2400,\"az\":10,\"tilt\":45,\"gz\":62.5}", out _);
                var g2 = WorldMapProtocol.Parse("{\"t\":\"view3\",\"lat\":-120,\"lng\":130,\"dist\":2400,\"az\":10,\"tilt\":45}", out _);
                var g3 = WorldMapProtocol.Parse("{\"t\":\"view3\",\"lat\":-120,\"lng\":130,\"dist\":2400,\"az\":10,\"tilt\":45,\"gz\":99999}", out _);
                var g4 = WorldMapProtocol.Parse("{\"t\":\"view3\",\"lat\":-120,\"lng\":130,\"dist\":2400,\"az\":10,\"tilt\":45,\"gz\":\"high\"}", out _);
                Check("protocol: view3's ground height gz (yards) is read when it is a number in -20000..20000; missing, out of range or not a number = not known (the message still counts)",
                    g1 != null && g1.Ground == 62.5 && g2 != null && double.IsNaN(g2.Ground) && g3 != null && double.IsNaN(g3.Ground) && g4 != null && double.IsNaN(g4.Ground));

                // ---- the service's state for the host
                WorldLight.SetForTest(set);
                Check("service: a set given to the service answers LightAt; Disabled / Unavailable answer none", WorldLight.Ready && WorldLight.LightAt(0, -300, -300, double.NaN).Direct == 0xECB374);
                WorldLight.SetForTest(null, WorldLight.State.Unavailable, "game updated: LightData layout 12345678 is not known to this Outpost yet");
                Check("service: unavailable = no light and the reason kept", !WorldLight.Ready && WorldLight.LightAt(0, 0, 0, 0) == null && WorldLight.Reason.StartsWith("game updated"));
            }
            catch (Exception e) { Check("worldlight test crashed", false, e.ToString()); }
            finally
            {
                WorldLight.ResetForTest(); WorldLight.CacheFile = savedCache; WorldLight.AutoStart = savedAuto;
            }
            return (ok ? "worldlight: ok" : "worldlight: FAILED") + "\r\n" + string.Join("\r\n", notes);
        }
    }
}
