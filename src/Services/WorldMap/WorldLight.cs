using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ElansAddonHub.Services.Db2;

namespace ElansAddonHub.Services.WorldMap
{
    // ============================================================================================================================
    // 3.8: sky, fog and light per zone on the 3D world map (--selftest "worldlight" and "worldlightreal"). The game's own light tables, read from the player's
    // OWN WoW install, read-only, in the background, like ModelData:
    //   * Light (the map's default light and the local light spheres), LightParams (skybox id, water alphas), LightData (the colours and fog of each light
    //     at up to ~12 times of day), ZoneLight + ZoneLightPoint (zone light polygons) and LightSkybox (only the ids and files for now: skyboxes are a later step)
    //     come out of the local CASC storage in the layouts this version knows (LightTables); a table with a layout it does not know is unavailable on its own,
    //     with its reason; without Light or LightData the feature is off and the 3D map keeps its fixed look
    //   * the parsed result is kept in the Outpost's data folder (cache\worldlight.bin), keyed by the game build
    //   * LightAt(map, x, y, z, time) works out the light at a spot: the map's default light, the zone light polygons the spot is in, the local light spheres
    //     around it, each LightData interpolated between its time keys
    // The idea and the blend rules are ported from wow.export's map viewer (FogDataProvider.js, MIT, (c) Kruithne and Marlamin): the sphere falloff between
    // GameFalloffStart and GameFalloffEnd, the zone polygon's 50-yard blend band and Zmin / Zmax, the time keys in half-minutes (0-2880) with the wrap at
    // midnight, LightParamsID[0] as the clear-weather light. One difference: wow.export adds every light's colours on top of each other (a spot inside a
    // sphere gets the default light AND the sphere's); here each light is laid OVER what is below it by its weight (default, then the zone polygons
    // largest first, then the spheres largest first), so a spot deep inside Duskwood's sphere gets exactly Duskwood's colours.
    // This build names the water colours RiverCloseColor / OceanCloseColor (wow.export reads CloseRiverColor / CloseOceanColor).
    // Nothing here touches the network. The world map host (WorldMapView) starts it when the 3D page opens.
    // ============================================================================================================================
    public static class LightTables
    {
        static SpellTables.Def D(string name, int fileId, uint table, uint layout) => new SpellTables.Def { Name = name, FileId = fileId, TableHash = table, Layouts = new[] { layout }, Build = "1.60.1.70707" };

        // Source: wowdev/WoWDBDefs definitions/*.dbd, the LAYOUT blocks that list 1.60.1.70707 (WoW Forever); table hashes and FileDataIDs from its manifest.json;
        // checked against the real files of that build (table hash, layout hash and field count from the WDC5 headers: 5, 39, 63, 8, 2 and 4 fields).
        // Fields counted as Wdc5 does: a $noninline$ column is not a field, an array is one field.
        // Light (layout 5F16BC84, the id not stored): GameCoords[3](0) GameFalloffStart(1) GameFalloffEnd(2) ContinentID(3) LightParamsID[8](4)
        public static readonly SpellTables.Def Light = D("Light", 1375579, 0x5CCAA0BA, 0x5F16BC84);
        public const int LCoords = 0, LStart = 1, LEnd = 2, LMap = 3, LParams = 4, LFields = 5, ParamSlots = 8;
        // LightParams (layout A7F31923, the id not stored): OverrideCelestialSphere[3](0) OverrideSunPosition[3](1) HighlightSky(2) LightSkyboxID(3) CloudTypeID(4)
        // Glow(5) WaterShallowAlpha(6) WaterDeepAlpha(7) OceanShallowAlpha(8) OceanDeepAlpha(9) Flags(10) ... Field_1_60_1_69876_038(38)
        public static readonly SpellTables.Def Params = D("LightParams", 1334669, 0xC67F0D98, 0xA7F31923);
        public const int PSkybox = 3, PGlow = 5, PWaterShallow = 6, PWaterDeep = 7, POceanShallow = 8, POceanDeep = 9, PFields = 39;
        // LightData (layout 360DA016, the id stored as field 0, LightParamID an inline relation): ID(0) LightParamID(1) Time(2) DirectColor(3) AmbientColor(4)
        // SkyTopColor(5) SkyMiddleColor(6) SkyBand1Color(7) SkyBand2Color(8) SkySmogColor(9) SkyFogColor(10) SunColor(11) CloudSunColor(12) CloudEmissiveColor(13)
        // CloudLayer1AmbientColor(14) CloudLayer2AmbientColor(15) OceanCloseColor(16) OceanFarColor(17) RiverCloseColor(18) RiverFarColor(19) ShadowOpacity(20)
        // FogEnd(21) FogScaler(22) FogDensity(23) FogHeight(24) ... HorizonAmbientColor(34) GroundAmbientColor(35) EndFogColor(36) ... HeightDensityFogCoeff[4](62)
        public static readonly SpellTables.Def Data = D("LightData", 1375580, 0x0AD67EBF, 0x360DA016);
        public const int DParam = 1, DTime = 2, DDirect = 3, DAmbient = 4, DSkyTop = 5, DSkyMiddle = 6, DBand1 = 7, DBand2 = 8, DSmog = 9, DSkyFog = 10, DSun = 11,
            DOceanClose = 16, DOceanFar = 17, DRiverClose = 18, DRiverFar = 19, DFogEnd = 21, DFogScaler = 22, DFogDensity = 23, DHorizonAmbient = 34, DGroundAmbient = 35, DFields = 63;
        // ZoneLight (layout 94CE95E0, the id not stored): Name(0) MapID(1) LightID(2) Flags(3) Zmin(4) Zmax(5) TransitionType(6) PlayerConditionID(7)
        public static readonly SpellTables.Def Zone = D("ZoneLight", 1310253, 0x59F2EB50, 0x94CE95E0);
        public const int ZName = 0, ZMap = 1, ZLight = 2, ZMin = 4, ZMax = 5, ZFields = 8;
        // ZoneLightPoint (layout DE2377FB, the id not stored, ZoneLightID = the relationship column): Pos[2](0) PointOrder(1)
        public static readonly SpellTables.Def ZonePoint = D("ZoneLightPoint", 1310256, 0x1CF9216D, 0xDE2377FB);
        public const int ZpPos = 0, ZpOrder = 1, ZpFields = 2;
        // LightSkybox (layout 9D4956FF, the id not stored): Name(0) Flags(1) SkyboxFileDataID(2) CelestialSkyboxFileDataID(3)
        public static readonly SpellTables.Def Skybox = D("LightSkybox", 1308501, 0xD364D557, 0x9D4956FF);
        public const int SbFlags = 1, SbFile = 2, SbFields = 4;

        public static readonly SpellTables.Def[] All = { Light, Params, Data, Zone, ZonePoint, Skybox };
    }

    // one Light row: a map's default light (all coordinates 0) or a local light sphere (world yards: x north, y west, z up)
    public sealed class LightRow
    {
        public int Id, Map; public float X, Y, Z, Start, End; public int[] Params = new int[LightTables.ParamSlots];
        public bool IsDefault => X == 0 && Y == 0 && Z == 0;
    }

    // one LightData row: the colours (0xRRGGBB) and fog of a light at one time of day (Time in half-minutes, 0-2880)
    public sealed class LightKey
    {
        public int Time;
        public int Direct, Ambient, SkyTop, SkyMiddle, Band1, Band2, Smog, SkyFog, Sun, OceanClose, OceanFar, RiverClose, RiverFar, HorizonAmbient, GroundAmbient;
        public float FogEnd, FogScaler, FogDensity;
    }

    public sealed class LightParamRow { public int Id, Skybox; public float Glow, WaterShallow, WaterDeep, OceanShallow, OceanDeep; }

    // one ZoneLight polygon with its points (world x, y) in PointOrder
    public sealed class ZoneLightRow
    {
        public int Id, Map, LightId; public string Name = ""; public float Zmin, Zmax;
        public List<double[]> Points = new List<double[]>();
        public double Area { get { double a = 0; for (int i = 0, j = Points.Count - 1; i < Points.Count; j = i++) a += (Points[j][0] + Points[i][0]) * (Points[j][1] - Points[i][1]); return Math.Abs(a) / 2; } }
    }

    public sealed class LightSet
    {
        public Dictionary<int, LightRow> Lights;                      // null = Light is unavailable (Problems says why)
        public Dictionary<int, List<LightKey>> Keys;                  // LightParamID -> its LightData rows by Time; null = LightData is unavailable
        public Dictionary<int, LightParamRow> Params;                 // null = LightParams is unavailable (default water alphas)
        public List<ZoneLightRow> Zones;                              // null = ZoneLight / ZoneLightPoint is unavailable (no zone polygons)
        public Dictionary<int, int> Skyboxes;                         // 3.8: LightSkybox id -> SkyboxFileDataID (only the ids for now)
        public readonly Dictionary<string, string> Problems = new Dictionary<string, string>();     // table name (or "all") -> why it is unavailable
        public readonly List<string> Notes = new List<string>();
        public readonly Dictionary<string, uint> LayoutsSeen = new Dictionary<string, uint>();
        public bool Usable => Lights != null && Keys != null;

        static Wdc5 OpenOne(SpellRaw raw, SpellTables.Def def, LightSet s)
        {
            var o = SpellTables.Open(raw.Get(def), def);
            if (o.Layout != 0) s.LayoutsSeen[def.Name] = o.Layout;
            if (!o.Ok) { s.Problems[def.Name] = o.Problem; return null; }
            if (o.Table.EncryptedRecords > 0) s.Notes.Add(def.Name + ": " + o.Table.EncryptedRecords + " rows are key-locked and not there");
            return o.Table;
        }

        public static LightSet Parse(SpellRaw raw)
        {
            var s = new LightSet();
            var t = OpenOne(raw, LightTables.Light, s);
            if (t != null)
            {
                s.Lights = new Dictionary<int, LightRow>(t.Count);
                for (int i = 0; i < t.Count; i++)
                {
                    var l = new LightRow
                    {
                        Id = t.IdAt(i), Map = SpellTables.Int(t, i, LightTables.LMap),
                        X = SpellTables.FltArr(t, i, LightTables.LCoords, 0, 3), Y = SpellTables.FltArr(t, i, LightTables.LCoords, 1, 3), Z = SpellTables.FltArr(t, i, LightTables.LCoords, 2, 3),
                        Start = SpellTables.Flt(t, i, LightTables.LStart), End = SpellTables.Flt(t, i, LightTables.LEnd),
                    };
                    for (int k = 0; k < LightTables.ParamSlots; k++) l.Params[k] = SpellTables.IntArr(t, i, LightTables.LParams, k, LightTables.ParamSlots) & 0xFFFF;
                    s.Lights[l.Id] = l;
                }
            }
            t = OpenOne(raw, LightTables.Data, s);
            if (t != null)
            {
                s.Keys = new Dictionary<int, List<LightKey>>();
                int C(int i, int f) => SpellTables.Int(t, i, f) & 0xFFFFFF;
                for (int i = 0; i < t.Count; i++)
                {
                    int p = SpellTables.Int(t, i, LightTables.DParam) & 0xFFFF;
                    if (p <= 0) continue;
                    var k = new LightKey
                    {
                        Time = SpellTables.Int(t, i, LightTables.DTime) & 0xFFFF,
                        Direct = C(i, LightTables.DDirect), Ambient = C(i, LightTables.DAmbient), SkyTop = C(i, LightTables.DSkyTop), SkyMiddle = C(i, LightTables.DSkyMiddle),
                        Band1 = C(i, LightTables.DBand1), Band2 = C(i, LightTables.DBand2), Smog = C(i, LightTables.DSmog), SkyFog = C(i, LightTables.DSkyFog), Sun = C(i, LightTables.DSun),
                        OceanClose = C(i, LightTables.DOceanClose), OceanFar = C(i, LightTables.DOceanFar), RiverClose = C(i, LightTables.DRiverClose), RiverFar = C(i, LightTables.DRiverFar),
                        HorizonAmbient = C(i, LightTables.DHorizonAmbient), GroundAmbient = C(i, LightTables.DGroundAmbient),
                        FogEnd = SpellTables.Flt(t, i, LightTables.DFogEnd), FogScaler = SpellTables.Flt(t, i, LightTables.DFogScaler), FogDensity = SpellTables.Flt(t, i, LightTables.DFogDensity),
                    };
                    if (!s.Keys.TryGetValue(p, out var l)) s.Keys[p] = l = new List<LightKey>(12);
                    l.Add(k);
                }
                foreach (var l in s.Keys.Values) l.Sort((a, b) => a.Time.CompareTo(b.Time));
            }
            t = OpenOne(raw, LightTables.Params, s);
            if (t != null)
            {
                s.Params = new Dictionary<int, LightParamRow>(t.Count);
                for (int i = 0; i < t.Count; i++)
                    s.Params[t.IdAt(i)] = new LightParamRow
                    {
                        Id = t.IdAt(i), Skybox = SpellTables.Int(t, i, LightTables.PSkybox) & 0xFFFF, Glow = SpellTables.Flt(t, i, LightTables.PGlow),
                        WaterShallow = SpellTables.Flt(t, i, LightTables.PWaterShallow), WaterDeep = SpellTables.Flt(t, i, LightTables.PWaterDeep),
                        OceanShallow = SpellTables.Flt(t, i, LightTables.POceanShallow), OceanDeep = SpellTables.Flt(t, i, LightTables.POceanDeep),
                    };
            }
            var zt = OpenOne(raw, LightTables.Zone, s);
            var pt = OpenOne(raw, LightTables.ZonePoint, s);
            if (zt != null && pt != null)
            {
                var pts = new Dictionary<int, List<(int order, int idx, double x, double y)>>();
                for (int i = 0; i < pt.Count; i++)
                {
                    int z = pt.Foreign(i);
                    if (z <= 0) continue;
                    if (!pts.TryGetValue(z, out var l)) pts[z] = l = new List<(int, int, double, double)>();
                    l.Add((SpellTables.Int(pt, i, LightTables.ZpOrder) & 0xFF, i, SpellTables.FltArr(pt, i, LightTables.ZpPos, 0, 2), SpellTables.FltArr(pt, i, LightTables.ZpPos, 1, 2)));
                }
                s.Zones = new List<ZoneLightRow>();
                for (int i = 0; i < zt.Count; i++)
                {
                    var z = new ZoneLightRow
                    {
                        Id = zt.IdAt(i), Name = (zt.String(i, LightTables.ZName) ?? "").Trim(), Map = SpellTables.Int(zt, i, LightTables.ZMap), LightId = SpellTables.Int(zt, i, LightTables.ZLight),
                        Zmin = SpellTables.Flt(zt, i, LightTables.ZMin), Zmax = SpellTables.Flt(zt, i, LightTables.ZMax),
                    };
                    if (pts.TryGetValue(z.Id, out var l)) z.Points = l.OrderBy(p => p.order).ThenBy(p => p.idx).Select(p => new[] { p.x, p.y }).ToList();
                    s.Zones.Add(z);
                }
            }
            else if (zt != null || pt != null) s.Notes.Add("the zone light polygons need both ZoneLight and ZoneLightPoint; without one of them there are none");
            t = OpenOne(raw, LightTables.Skybox, s);
            if (t != null)
            {
                s.Skyboxes = new Dictionary<int, int>(t.Count);
                for (int i = 0; i < t.Count; i++) s.Skyboxes[t.IdAt(i)] = SpellTables.Int(t, i, LightTables.SbFile);
            }
            if (!s.Usable) s.Problems["all"] = s.Problems.TryGetValue(LightTables.Light.Name, out var a) ? a : s.Problems.TryGetValue(LightTables.Data.Name, out var b) ? b : "the light tables could not be read";
            return s;
        }
    }

    // what LightAt worked out: the colours (0xRRGGBB, as the game stores them; -1 = no light there sets it: the water colours and the horizon / ground
    // ambients of the Classic-era lights) and fog (FogEnd 0 = not set) of one spot at one time of day
    public sealed class LightResult
    {
        public int SkyTop, SkyMiddle, Band1, Band2, Smog, SkyFog, Ambient, Direct, Sun, HorizonAmbient, GroundAmbient, RiverClose, RiverFar, OceanClose, OceanFar;
        public double FogEnd, FogScaler;
        public double DefaultFogEnd;          // the map's default light's FogEnd at this time (the page scales its fog density by FogEnd against it)
        public int DefaultLightId, Skybox;    // Skybox: the LightSkybox id of the strongest light that has one (0 = none; not drawn yet)
        public double Local = 1;              // how much the local lights counted (ForView: less when zoomed far out)
        public readonly List<string> From = new List<string>();      // what contributed: "default 1", "zone 3197 Elwynn Forest Zone Light 1.00", "sphere 15 0.85"
        public string Source => string.Join(", ", From);
        public static string Hex(int c) => c < 0 ? null : "#" + (c & 0xFFFFFF).ToString("X6", CultureInfo.InvariantCulture);      // -1 = not set
        public override string ToString() => "fog " + Hex(SkyFog) + " end " + FogEnd.ToString("0", CultureInfo.InvariantCulture) + " ambient " + Hex(Ambient) + " direct " + Hex(Direct) + " sky " + Hex(SkyTop) + " river " + (Hex(RiverClose) ?? "not set") + " ocean " + (Hex(OceanClose) ?? "not set") + " (" + Source + ")";
    }

    public static class WorldLight
    {
        public enum State { NotStarted, Disabled, Loading, Ready, Unavailable }

        public const int FormatVersion = WorldLightIndexFile.FormatVersion;
        public const int Noon = 1440, DayLength = 2880;      // LightData.Time: half-minutes since midnight
        public const double ZoneBlend = 50;                   // yards: a zone polygon fades in over 50 yards either side of its border (wow.export's ZONE_BLEND_DIST)
        // the overview map is not the player's eye: zoomed out past NearDist the local lights fade, past FarDist only the map's default light counts
        public const double NearDist = 4000, FarDist = 12000;

        public static bool AutoStart = true;         // off in the self-test (it must not read the real game)
        public static string CacheFile = Path.Combine(Util.DataDir, "cache", "worldlight.bin");
        public static event Action Changed;          // on a worker thread, whenever the data or the state changed

        static readonly object gate = new object();
        static volatile LightSet data;
        static volatile State state = State.NotStarted;
        static string reason, builtFor;
        static bool running, again;
        public static long LastLoadMs { get; private set; }
        public static string LastLoadFrom { get; private set; }
        public static string Build { get; private set; }

        public static State Status => state;
        public static string Reason => reason;
        public static LightSet Data => data;
        public static bool Ready => state == State.Ready && data != null && data.Usable;

        // ---------------------------------------------------------------------------------------------------------------- the resolver
        public static LightResult LightAt(int map, double x, double y, double z, double time = Noon) => LightAt(data, map, x, y, z, time);

        // the map's default light: its own row with all coordinates 0 (the lowest id when there are several), else the Eastern Kingdoms' (as the game falls back)
        public static LightRow DefaultOf(LightSet s, int map)
        {
            if (s?.Lights == null) return null;
            LightRow best = null;
            foreach (var l in s.Lights.Values) if (l.Map == map && l.IsDefault && (best == null || l.Id < best.Id)) best = l;
            if (best == null && map != 0) return DefaultOf(s, 0);
            return best;
        }

        // The light at world (x, y, z) of map `map` at `time` (half-minutes, 1440 = noon). z = NaN: the height is not known (the spheres count by horizontal
        // distance, a zone polygon's Zmin / Zmax are not checked). `local` (0..1) weighs every zone polygon and sphere (ForView: zoomed far out). null = no
        // light could be worked out (the tables are unavailable, or the map has no default light with time keys).
        public static LightResult LightAt(LightSet s, int map, double x, double y, double z, double time, double local = 1)
        {
            if (s == null || !s.Usable) return null;
            var def = DefaultOf(s, map);
            if (def == null) return null;
            var acc = Eval(s, def.Params[0], time);
            if (acc == null) return null;
            var r = new LightResult { DefaultLightId = def.Id, DefaultFogEnd = acc.Set[ColorCount] > 0 ? acc.V[FogEndAt] : 0, Local = Clamp(local, 0, 1) };
            r.From.Add("default " + def.Id);
            int skyFrom = SkyboxOf(s, def.Params[0]); double skyW = skyFrom > 0 ? 1 : -1;
            void Over(int param, double w, string what)
            {
                if (w <= 0) return;
                var v = Eval(s, param, time);
                if (v == null) return;
                Lay(acc, v, w);
                r.From.Add(what + " " + w.ToString("0.00", CultureInfo.InvariantCulture));
                int sb = SkyboxOf(s, param); if (sb > 0 && w >= skyW) { skyFrom = sb; skyW = w; }
            }
            if (r.Local > 0)
            {
                // the zone light polygons the spot is in (or within 50 yards of), the largest first so a smaller one inside it wins
                if (s.Zones != null)
                    foreach (var zl in s.Zones.Where(q => q.Map == map && q.Points.Count >= 3).OrderByDescending(q => q.Area).ThenBy(q => q.Id))
                    {
                        double w = ZoneWeight(zl, x, y, z);
                        if (w <= 0 || !s.Lights.TryGetValue(zl.LightId, out var zlight)) continue;
                        Over(zlight.Params[0], w * r.Local, "zone " + zl.Id + " " + zl.Name);
                    }
                // the light spheres around it, the largest first (an inner, smaller sphere is laid over an outer one)
                foreach (var l in s.Lights.Values.Where(q => q.Map == map && !q.IsDefault && q.End > 0).OrderByDescending(q => q.End).ThenBy(q => q.Id))
                {
                    double w = SphereWeight(l, x, y, z);
                    if (w > 0) Over(l.Params[0], w * r.Local, "sphere " + l.Id);
                }
            }
            Fill(r, acc);
            r.Skybox = skyFrom;
            return r;
        }

        // the light for the 3D map's camera: the spot it looks at (x, y, ground height z) seen from `dist` yards; the local lights fade out between NearDist and FarDist
        public static double LocalWeight(double dist) => dist <= NearDist ? 1 : dist >= FarDist ? 0 : 1 - Smooth((dist - NearDist) / (FarDist - NearDist));
        public static LightResult ForView(LightSet s, int map, double x, double y, double z, double dist, double time = Noon) => LightAt(s, map, x, y, z, time, LocalWeight(dist));

        // the sphere's weight at a point: 1 inside GameFalloffStart, 0 beyond GameFalloffEnd, linear between (wow.export)
        public static double SphereWeight(LightRow l, double x, double y, double z)
        {
            double dx = x - l.X, dy = y - l.Y, dz = double.IsNaN(z) ? 0 : z - l.Z;
            double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (d >= l.End) return 0;
            if (l.End <= l.Start) return 1;
            return Clamp((l.Start - d) / (l.End - l.Start) + 1, 0, 1);
        }

        // a zone polygon's weight: 0.5 on the border, 1 from 50 yards inside, 0 from 50 yards outside; Zmin / Zmax the same way when the height is known (wow.export)
        public static double ZoneWeight(ZoneLightRow zl, double x, double y, double z)
        {
            if (zl.Points.Count < 3) return 0;
            bool inside = Inside(zl.Points, x, y);
            double border = BorderDist(zl.Points, x, y);
            double polyDist = inside ? -border : border;
            double zDist = double.NegativeInfinity;
            if (!double.IsNaN(z))
            {
                bool zIn = z >= zl.Zmin && z <= zl.Zmax;
                zDist = zIn ? -Math.Min(z - zl.Zmin, zl.Zmax - z) : Math.Min(Math.Abs(z - zl.Zmin), Math.Abs(z - zl.Zmax));
            }
            double blend = Math.Max(polyDist, zDist) - ZoneBlend;
            return blend >= 0 ? 0 : Clamp(-blend / (2 * ZoneBlend), 0, 1);
        }

        public static bool Inside(List<double[]> p, double x, double y)
        {
            bool inside = false;
            for (int i = 0, j = p.Count - 1; i < p.Count; j = i++)
            {
                double xi = p[i][0], yi = p[i][1], xj = p[j][0], yj = p[j][1];
                if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi) inside = !inside;
            }
            return inside;
        }

        static double BorderDist(List<double[]> p, double x, double y)
        {
            double best = double.MaxValue;
            for (int i = 0; i < p.Count; i++)
            {
                var a = p[i]; var b = p[(i + 1) % p.Count];
                double dx = b[0] - a[0], dy = b[1] - a[1], len = dx * dx + dy * dy;
                double t = len == 0 ? 0 : Clamp(((x - a[0]) * dx + (y - a[1]) * dy) / len, 0, 1);
                double px = a[0] + t * dx - x, py = a[1] + t * dy - y;
                best = Math.Min(best, Math.Sqrt(px * px + py * py));
            }
            return best;
        }

        static int SkyboxOf(LightSet s, int param) => s.Params != null && s.Params.TryGetValue(param, out var p) ? p.Skybox : 0;

        // ---- one light's values at a time of day, as a vector (the colours as r, g, b in 0..1, then FogEnd and FogScaler) so lights blend channel by channel.
        // The optional fields (the horizon / ground ambients, the four water colours and FogEnd) are 0 in many rows of this build: 0 there means "not set".
        // Such a field is interpolated only between the keys that set it, and a light that does not set it leaves what is below it (Weights: 0 = not set).
        const int ColorCount = 15, FirstOptional = 9, FogEndAt = ColorCount * 3, FogScalerAt = FogEndAt + 1, Size = FogScalerAt + 1, Fields = ColorCount + 2;
        static int[] Colors(LightKey k) => new[] { k.SkyTop, k.SkyMiddle, k.Band1, k.Band2, k.Smog, k.SkyFog, k.Ambient, k.Direct, k.Sun, k.HorizonAmbient, k.GroundAmbient, k.RiverClose, k.RiverFar, k.OceanClose, k.OceanFar };
        static bool Optional(int field) => (field >= FirstOptional && field < ColorCount) || field == ColorCount;      // ColorCount = FogEnd
        static int FieldOf(int index) => index < FogEndAt ? index / 3 : index == FogEndAt ? ColorCount : ColorCount + 1;

        public sealed class LightValues { public double[] V = new double[Size]; public double[] Set = new double[Fields]; }

        // a LightParam at `time`: the two keys around it (the last one at or before it and the next, round midnight), interpolated; null = no keys
        public static LightValues Eval(LightSet s, int param, double time)
        {
            if (param <= 0 || s?.Keys == null || !s.Keys.TryGetValue(param, out var keys) || keys.Count == 0) return null;
            time = ((time % DayLength) + DayLength) % DayLength;
            var e = new LightValues();
            var cols = keys.Select(Colors).ToArray();
            for (int f = 0; f < Fields; f++)
            {
                bool opt = Optional(f);
                double Val(int k, int ch) => f < ColorCount ? (cols[k][f] >> (16 - 8 * ch)) & 255 : f == ColorCount ? keys[k].FogEnd : keys[k].FogScaler;
                bool Has(int k) => !opt || (f < ColorCount ? cols[k][f] != 0 : keys[k].FogEnd > 0);
                if (!Bracket(keys, time, Has, out int a, out int b, out double t)) continue;
                e.Set[f] = 1;
                if (f < ColorCount) for (int ch = 0; ch < 3; ch++) e.V[f * 3 + ch] = (Val(a, ch) + (Val(b, ch) - Val(a, ch)) * t) / 255.0;
                else e.V[f == ColorCount ? FogEndAt : FogScalerAt] = Val(a, 0) + (Val(b, 0) - Val(a, 0)) * t;
            }
            return e;
        }

        // the keys around `time` among those `use` accepts: the last at or before it and the next one (round midnight), and how far between them it is
        static bool Bracket(List<LightKey> keys, double time, Func<int, bool> use, out int a, out int b, out double t)
        {
            a = -1; b = -1; t = 0;
            int first = -1, last = -1;
            for (int i = 0; i < keys.Count; i++) { if (!use(i)) continue; if (first < 0) first = i; last = i; if (keys[i].Time <= time) a = i; }
            if (first < 0) return false;
            if (a < 0) a = last;                                  // before the first key: from the last one of the day before
            b = first;
            for (int i = a + 1; i < keys.Count; i++) if (use(i)) { b = i; break; }
            if (a == b) return true;
            double ta = keys[a].Time, tb = keys[b].Time;
            double span = tb > ta ? tb - ta : DayLength - ta + tb, at = time >= ta ? time - ta : DayLength - ta + time;
            t = span > 0 ? Clamp(at / span, 0, 1) : 0;
            return true;
        }

        // lay light `v` over `acc` by weight w; a field `v` does not set leaves `acc` as it is
        static void Lay(LightValues acc, LightValues v, double w)
        {
            for (int i = 0; i < Size; i++)
            {
                int f = FieldOf(i);
                if (v.Set[f] <= 0) continue;
                double have = acc.Set[f] * (1 - w), total = have + w;
                acc.V[i] = total > 0 ? (acc.V[i] * have + v.V[i] * w) / total : v.V[i];
            }
            for (int f = 0; f < Fields; f++) if (v.Set[f] > 0) acc.Set[f] = acc.Set[f] * (1 - w) + w;
        }

        static void Fill(LightResult r, LightValues e)
        {
            int C(int i) { if (e.Set[i] <= 0) return -1; int R = Byte(e.V[i * 3]), G = Byte(e.V[i * 3 + 1]), B = Byte(e.V[i * 3 + 2]); return (R << 16) | (G << 8) | B; }
            r.SkyTop = C(0); r.SkyMiddle = C(1); r.Band1 = C(2); r.Band2 = C(3); r.Smog = C(4); r.SkyFog = C(5); r.Ambient = C(6); r.Direct = C(7); r.Sun = C(8);
            r.HorizonAmbient = C(9); r.GroundAmbient = C(10); r.RiverClose = C(11); r.RiverFar = C(12); r.OceanClose = C(13); r.OceanFar = C(14);
            r.FogEnd = e.Set[ColorCount] > 0 ? e.V[FogEndAt] : 0; r.FogScaler = e.V[FogScalerAt];
        }

        static int Byte(double x) => (int)Math.Round(Clamp(x, 0, 1) * 255);
        static double Clamp(double v, double a, double b) => v < a ? a : v > b ? b : v;
        static double Smooth(double t) { t = Clamp(t, 0, 1); return t * t * (3 - 2 * t); }

        // ---- when the host works the light out again: the camera's spot moved more than MoveYards, the zoom crossed into another band of the fade, or another map
        public const double MoveYards = 300;
        public static bool ShouldRecompute(int lastMap, double lastX, double lastY, double lastDist, int map, double x, double y, double dist)
        {
            if (lastMap != map || double.IsNaN(lastX)) return true;
            if (Math.Sqrt((x - lastX) * (x - lastX) + (y - lastY) * (y - lastY)) > MoveYards) return true;
            return Math.Abs(LocalWeight(dist) - LocalWeight(lastDist)) > 0.05;
        }

        // ---------------------------------------------------------------------------------------------------------------- the service
        static bool SafeEnabled() { try { return ItemData.Enabled(); } catch { return true; } }

        public static void Start()
        {
            if (!AutoStart) return;
            if (!SafeEnabled()) { Disable(); return; }
            lock (gate)
            {
                again = true;
                if (running) return;
                running = true;
            }
            if (data == null) SetState(State.Loading, null);
            Task.Factory.StartNew(Worker, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        public static void Disable()
        {
            data = null; Build = null; builtFor = null;
            SetState(State.Disabled, null);
        }

        static void Worker()
        {
            try { Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; } catch { }
            while (true)
            {
                lock (gate) { if (!again) { running = false; return; } again = false; }
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    if (!SafeEnabled()) { Disable(); continue; }
                    GameDataProgress.Begin(JobId, "World map light", "Loading the sky and fog of the zones", 0.05);
                    LoadOnce();
                }
                catch (Exception e)
                {
                    Util.Log("world light: " + e);
                    Publish(null, State.Unavailable, "could not be read: " + e.Message, null);
                }
                LastLoadMs = sw.ElapsedMilliseconds;
                GameDataProgress.End(JobId, state == State.Ready, state == State.Ready ? null : reason);
            }
        }

        static void Publish(LightSet s, State st, string why, string build)
        {
            lock (gate) { data = st == State.Ready ? s : null; reason = why; state = st; Build = st == State.Ready ? build : null; builtFor = Build; }
            try { Changed?.Invoke(); } catch { }
        }

        static void SetState(State st, string why)
        {
            lock (gate) { state = st; reason = why; }
            try { Changed?.Invoke(); } catch { }
        }

        public static void SetForTest(LightSet s, State st = State.Ready, string why = null)
        {
            lock (gate) { data = st == State.Ready ? s : null; state = st; reason = why; Build = st == State.Ready ? "test" : null; builtFor = Build; }
        }
        public static void RaiseChangedForTest() { try { Changed?.Invoke(); } catch { } }

        public static void ResetForTest()
        {
            lock (gate) { data = null; state = State.NotStarted; reason = null; running = false; again = false; Build = null; builtFor = null; }
        }

        static void LoadOnce()
        {
            var root = ItemData.WowRoot();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) { Publish(null, State.Unavailable, "the WoW folder is not set", null); return; }
            var flavor = ItemData.Flavor() ?? "_classic_beta_";
            var build = ItemData.GameBuild(root, flavor);
            if (build == null) { Publish(null, State.Unavailable, "no .build.info in the WoW folder", null); return; }
            if (state == State.Ready && data != null && builtFor == build) return;
            var saved = WorldLightIndexFile.TryRead(CacheFile);
            var set = Obtain(saved, build, CacheFile, CascShared.OneAtATime(JobId, () =>
            {
                GameDataProgress.Rebuilding(JobId, GameDataProgress.ReasonFor(CacheFile, saved?.Build, build), "Reading the sky and fog of the zones", 0.05, "tables", false);
                try { return LightSet.Parse(ReadCasc(root, flavor, (n, t) => GameDataProgress.Report(JobId, n, t, 0, 0.8))); }
                catch (Exception e) { Util.Log("world light: game files: " + e.Message); var f = new LightSet(); f.Problems["all"] = "the game files could not be read: " + e.Message; return f; }
            }), out var from);
            if (set.Problems.ContainsKey("all")) { Util.Log("world light: unavailable: " + set.Problems["all"]); Publish(null, State.Unavailable, set.Problems["all"], null); return; }
            foreach (var kv in set.Problems) Util.Log("world light: " + kv.Key + ": " + kv.Value);
            foreach (var n in set.Notes) Util.Log("world light: " + n);
            LastLoadFrom = from;
            Publish(set, State.Ready, null, build);
        }

        // the saved index when it is of this game build, otherwise the game files (which are then saved)
        public static LightSet Obtain(WorldLightIndexFile.Saved saved, string build, string cacheFile, Func<LightSet> readGame, out string from)
        {
            if (saved != null && saved.Build == build && saved.Format == FormatVersion) { from = "cache file"; return saved.Set; }
            var fresh = readGame();
            if (!fresh.Problems.ContainsKey("all")) WorldLightIndexFile.TryWrite(cacheFile, build, fresh);
            from = "game files";
            return fresh;
        }

        public const string JobId = "worldlight";

        public static SpellRaw ReadCasc(string root, string flavor, Action<int, int> progress = null)
        {
            var raw = new SpellRaw();
            using (var lease = CascShared.Open(root, flavor))     // shared with the other readers; key-locked parts read as zeros (the table reader skips those sections)
            {
                var casc = lease.Casc;
                var all = LightTables.All;
                var keys = casc.ResolveFileIds(new HashSet<int>(all.Select(d => d.FileId)));
                int i = 0;
                foreach (var def in all)
                {
                    progress?.Invoke(i++, all.Length);
                    try { if (keys.TryGetValue(def.FileId, out var ck)) raw.Tables[def.Name] = casc.ReadByContentKey(ck); }
                    catch (Exception e) { Util.Log("world light: " + def.Name + ".db2: " + e.Message); }
                }
            }
            return raw;
        }
    }

    // ============================================================================================================================
    // the compact copy of the light tables in the Outpost's data folder (gzip of a small binary format), keyed by the game build
    // ============================================================================================================================
    public static class WorldLightIndexFile
    {
        public sealed class Saved { public int Format; public string Build; public LightSet Set; }
        public const int FormatVersion = 1;       // bump when the parsing, the table layouts or the cache file change meaning
        const int Magic = 0x54474C45;       // "ELGT"

        public static bool TryWrite(string path, string build, LightSet set)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var tmp = path + ".tmp";
                var mem = new MemoryStream(1 << 18);
                using (var w = new BinaryWriter(mem, Encoding.UTF8, true))
                {
                    w.Write(Magic); w.Write(FormatVersion); w.Write(build ?? "");
                    w.Write(set.Problems.Count); foreach (var kv in set.Problems) { w.Write(kv.Key); w.Write(kv.Value ?? ""); }
                    w.Write(set.Notes.Count); foreach (var n in set.Notes) w.Write(n);
                    w.Write(set.LayoutsSeen.Count); foreach (var kv in set.LayoutsSeen) { w.Write(kv.Key); w.Write(kv.Value); }
                    w.Write(set.Lights != null);
                    if (set.Lights != null)
                    {
                        w.Write(set.Lights.Count);
                        foreach (var l in set.Lights.Values) { w.Write(l.Id); w.Write(l.Map); w.Write(l.X); w.Write(l.Y); w.Write(l.Z); w.Write(l.Start); w.Write(l.End); foreach (var p in l.Params) w.Write(p); }
                    }
                    w.Write(set.Keys != null);
                    if (set.Keys != null)
                    {
                        w.Write(set.Keys.Count);
                        foreach (var kv in set.Keys)
                        {
                            w.Write(kv.Key); w.Write(kv.Value.Count);
                            foreach (var k in kv.Value)
                            {
                                w.Write(k.Time);
                                foreach (var c in new[] { k.Direct, k.Ambient, k.SkyTop, k.SkyMiddle, k.Band1, k.Band2, k.Smog, k.SkyFog, k.Sun, k.OceanClose, k.OceanFar, k.RiverClose, k.RiverFar, k.HorizonAmbient, k.GroundAmbient }) w.Write(c);
                                w.Write(k.FogEnd); w.Write(k.FogScaler); w.Write(k.FogDensity);
                            }
                        }
                    }
                    w.Write(set.Params != null);
                    if (set.Params != null)
                    {
                        w.Write(set.Params.Count);
                        foreach (var p in set.Params.Values) { w.Write(p.Id); w.Write(p.Skybox); w.Write(p.Glow); w.Write(p.WaterShallow); w.Write(p.WaterDeep); w.Write(p.OceanShallow); w.Write(p.OceanDeep); }
                    }
                    w.Write(set.Zones != null);
                    if (set.Zones != null)
                    {
                        w.Write(set.Zones.Count);
                        foreach (var z in set.Zones) { w.Write(z.Id); w.Write(z.Map); w.Write(z.LightId); w.Write(z.Name ?? ""); w.Write(z.Zmin); w.Write(z.Zmax); w.Write(z.Points.Count); foreach (var p in z.Points) { w.Write(p[0]); w.Write(p[1]); } }
                    }
                    w.Write(set.Skyboxes != null);
                    if (set.Skyboxes != null) { w.Write(set.Skyboxes.Count); foreach (var kv in set.Skyboxes) { w.Write(kv.Key); w.Write(kv.Value); } }
                }
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var gz = new GZipStream(fs, CompressionMode.Compress))
                    gz.Write(mem.GetBuffer(), 0, (int)mem.Length);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                return true;
            }
            catch (Exception e) { Util.Log("world light: cannot save the index: " + e.Message); return false; }
        }

        // null for a missing, damaged, foreign or older file (the caller then reads the game files)
        public static Saved TryRead(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var mem = new MemoryStream(1 << 18);
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var gz = new GZipStream(fs, CompressionMode.Decompress))
                    gz.CopyTo(mem);
                mem.Position = 0;
                using (var r = new BinaryReader(mem, Encoding.UTF8))
                {
                    if (r.ReadInt32() != Magic) return null;
                    var s = new Saved { Format = r.ReadInt32(), Build = r.ReadString(), Set = new LightSet() };
                    if (s.Format != FormatVersion) return null;
                    int n = r.ReadInt32(); Count(n, 1000);
                    for (int i = 0; i < n; i++) s.Set.Problems[r.ReadString()] = r.ReadString();
                    n = r.ReadInt32(); Count(n, 100000);
                    for (int i = 0; i < n; i++) s.Set.Notes.Add(r.ReadString());
                    n = r.ReadInt32(); Count(n, 1000);
                    for (int i = 0; i < n; i++) s.Set.LayoutsSeen[r.ReadString()] = r.ReadUInt32();
                    if (r.ReadBoolean())
                    {
                        n = r.ReadInt32(); Count(n, 1000000);
                        s.Set.Lights = new Dictionary<int, LightRow>(n);
                        for (int i = 0; i < n; i++)
                        {
                            var l = new LightRow { Id = r.ReadInt32(), Map = r.ReadInt32(), X = r.ReadSingle(), Y = r.ReadSingle(), Z = r.ReadSingle(), Start = r.ReadSingle(), End = r.ReadSingle() };
                            for (int k = 0; k < LightTables.ParamSlots; k++) l.Params[k] = r.ReadInt32();
                            s.Set.Lights[l.Id] = l;
                        }
                    }
                    if (r.ReadBoolean())
                    {
                        n = r.ReadInt32(); Count(n, 1000000);
                        s.Set.Keys = new Dictionary<int, List<LightKey>>(n);
                        for (int i = 0; i < n; i++)
                        {
                            int id = r.ReadInt32(), c = r.ReadInt32(); Count(c, 10000);
                            var l = new List<LightKey>(c);
                            for (int k = 0; k < c; k++)
                                l.Add(new LightKey
                                {
                                    Time = r.ReadInt32(), Direct = r.ReadInt32(), Ambient = r.ReadInt32(), SkyTop = r.ReadInt32(), SkyMiddle = r.ReadInt32(), Band1 = r.ReadInt32(), Band2 = r.ReadInt32(),
                                    Smog = r.ReadInt32(), SkyFog = r.ReadInt32(), Sun = r.ReadInt32(), OceanClose = r.ReadInt32(), OceanFar = r.ReadInt32(), RiverClose = r.ReadInt32(), RiverFar = r.ReadInt32(),
                                    HorizonAmbient = r.ReadInt32(), GroundAmbient = r.ReadInt32(), FogEnd = r.ReadSingle(), FogScaler = r.ReadSingle(), FogDensity = r.ReadSingle(),
                                });
                            s.Set.Keys[id] = l;
                        }
                    }
                    if (r.ReadBoolean())
                    {
                        n = r.ReadInt32(); Count(n, 1000000);
                        s.Set.Params = new Dictionary<int, LightParamRow>(n);
                        for (int i = 0; i < n; i++)
                        {
                            var p = new LightParamRow { Id = r.ReadInt32(), Skybox = r.ReadInt32(), Glow = r.ReadSingle(), WaterShallow = r.ReadSingle(), WaterDeep = r.ReadSingle(), OceanShallow = r.ReadSingle(), OceanDeep = r.ReadSingle() };
                            s.Set.Params[p.Id] = p;
                        }
                    }
                    if (r.ReadBoolean())
                    {
                        n = r.ReadInt32(); Count(n, 100000);
                        s.Set.Zones = new List<ZoneLightRow>(n);
                        for (int i = 0; i < n; i++)
                        {
                            var z = new ZoneLightRow { Id = r.ReadInt32(), Map = r.ReadInt32(), LightId = r.ReadInt32(), Name = r.ReadString(), Zmin = r.ReadSingle(), Zmax = r.ReadSingle() };
                            int c = r.ReadInt32(); Count(c, 100000);
                            for (int k = 0; k < c; k++) z.Points.Add(new[] { r.ReadDouble(), r.ReadDouble() });
                            s.Set.Zones.Add(z);
                        }
                    }
                    if (r.ReadBoolean())
                    {
                        n = r.ReadInt32(); Count(n, 100000);
                        s.Set.Skyboxes = new Dictionary<int, int>(n);
                        for (int i = 0; i < n; i++) { int k = r.ReadInt32(); s.Set.Skyboxes[k] = r.ReadInt32(); }
                    }
                    return s;
                }
            }
            catch (Exception) { return null; }
        }

        static void Count(int n, int max) { if (n < 0 || n > max) throw new InvalidDataException("count"); }
    }
}
