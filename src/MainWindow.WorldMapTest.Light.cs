using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ElansAddonHub.Services;
using ElansAddonHub.Services.WorldMap;

namespace ElansAddonHub
{
    // --selftest "worldmap", the 3D page's sky, fog and light per zone (3.8) on the invented continent: a light set handed to the service (a default light and a
    // Duskwood-like sphere over the invented lake), the host sending the light where the camera looks (throttled), the page applying the colours (fog, the sky
    // gradient, the lights' tints at today's strengths, the water), the fog distance still the camera's (a little nearer for the sphere's nearer FogEnd), the
    // continent view on the default light, junk light messages refused by the page, and the Settings switch "World map game lighting" giving the fixed look back.
    // The light tables and the resolver are the worldlight section's; the real zones are worldlightreal's.
    public partial class MainWindow
    {
        async Task<Dictionary<string, object>> Wm3Light()
        {
            var r = await WorldMapView.EvalForTest("window.wm3 && window.wm3.light ? window.wm3.light() : null");
            if (string.IsNullOrEmpty(r) || r == "null") return null;
            var s = Json.Parse(r) as string;
            return s == null || s == "null" ? null : Json.Obj(s);
        }

        // the page's light once a message it was sent has been applied and the blend is over (`until` true), at most `ms`
        async Task<Dictionary<string, object>> Wm3LightUntil(Func<Dictionary<string, object>, bool> until, int ms = 10000)
        {
            Dictionary<string, object> l = null; var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms) { l = await Wm3Light(); if (l != null && !l.Bool("anim") && until(l)) return l; await Task.Delay(150); }
            return l;
        }

        // today's fixed look (view3d.js LBASE): what the switch off must give back
        static bool WlFixedLook(Dictionary<string, object> l) => l != null && !l.Bool("on") && l.Str("fog") == "0b1c20" && l.Str("bg") == "0b1c20" && !l.Bool("skyTex") && l.Str("amb") == "ffffff" && l.Str("sun") == "ffffff"
            && l.Str("hemi") == "cfe8ff" && l.Str("gnd") == "403a30" && l.Str("w1") == "3a93c4" && l.Str("w2") == "103f6e" && l.Num("dens") == 1;

        async Task WorldLightPage3d(CheckFn Check, Func<string, string> P)
        {
            var I = CultureInfo.InvariantCulture;
            bool savedOff = settings.WorldMapLightOff;
            try
            {
                var l0 = await Wm3LightUntil(l => true, 3000);
                Check("light: without the light tables (not read in a self-test) the 3D page keeps its fixed look: fog and background #0b1c20, white ambient and sun, today's water",
                    WlFixedLook(l0) && WorldMapView.LastLightForTest != null && WorldMapView.LastLightForTest.Contains("\"on\":false"), l0 == null ? "null" : Json.Write(l0));

                // the invented lake (as the liquids check above): a Duskwood-like light sphere over it, the default light everywhere else
                WorldMapMath.GridToWorld(29 + (6 + 4.5 / 8.0) / 16.0, 31 + (6 + 4.5 / 8.0) / 16.0, out var lx, out var ly);
                var set = new LightSet { Lights = new Dictionary<int, LightRow>(), Keys = new Dictionary<int, List<LightKey>>(), Params = new Dictionary<int, LightParamRow>(), Zones = new List<ZoneLightRow>(), Skyboxes = new Dictionary<int, int>() };
                set.Lights[1] = new LightRow { Id = 1, Map = 0 }; set.Lights[1].Params[0] = 100;
                set.Lights[2] = new LightRow { Id = 2, Map = 0, X = (float)lx, Y = (float)ly, Z = 0, Start = 250, End = 400 }; set.Lights[2].Params[0] = 200;
                set.Keys[100] = new List<LightKey> { new LightKey { Time = 1440, SkyTop = 0x00265F, SkyMiddle = 0x2A6FA8, Band1 = 0x4F8FC0, Band2 = 0x6FA6CC, Smog = 0x7FB0D0, SkyFog = 0x659EBB, Ambient = 0x68829A, Direct = 0xECB374, Sun = 0xFFF0D0, FogEnd = 18000 } };
                set.Keys[200] = new List<LightKey> { new LightKey { Time = 1440, SkyTop = 0x002335, SkyMiddle = 0x00405F, Band1 = 0x00607F, Band2 = 0x00809F, Smog = 0x0090B0, SkyFog = 0x009ACD, Ambient = 0x406589, Direct = 0x54A0D3, Sun = 0xC0D8FF, RiverClose = 0x2F5F3F, OceanClose = 0x1F3F5F, FogEnd = 6500 } };
                WorldLight.SetForTest(set);
                int posts0 = WorldMapView.LightPostsForTest;
                async Task View(double x, double y, double dist) { WorldMapMath.WorldToLatLng(x, y, out var la, out var ln); await WmEval("window.wm3.setView(" + la.ToString("R", I) + "," + ln.ToString("R", I) + "," + dist.ToString("R", I) + ",20,40)"); }
                await View(lx, ly, 700);
                WorldLight.RaiseChangedForTest();       // as the service does when it has read the tables: the host works the light out again
                var la1 = await Wm3LightUntil(l => l.Bool("on") && l.Str("fog") == "009acd");
                Check("light: over the lake inside the sphere the page shows the sphere's game colours: fog #009acd, the sky gradient (top #002335 ... bottom = the fog) as the background",
                    la1 != null && la1.Bool("on") && la1.Str("fog") == "009acd" && la1.Bool("skyTex") && la1.Str("bg") == null && (string)la1.List("sky")[0] == "002335" && (string)la1.List("sky")[5] == "009acd" && la1.Str("src").Contains("sphere 2"),
                    la1 == null ? "null" : Json.Write(la1));
                Check("light: the lights take the game's tints at today's strengths (ambient 0.66, sun 0.5, hemisphere 0.18), so the map stays readable; the ambient is bluish, not dark",
                    la1 != null && la1.Num("ambI") == 0.66 && la1.Num("sunI") == 0.5 && la1.Num("hemiI") == 0.18 && la1.Str("amb") != "ffffff" && la1.Str("sun") != "ffffff"
                    && Convert.ToInt32(la1.Str("amb").Substring(4, 2), 16) == 255 && Convert.ToInt32(la1.Str("amb").Substring(0, 2), 16) > 0x80, la1 == null ? "" : la1.Str("amb") + " " + la1.Str("sun"));
                await View(0, 1000, 700);
                var lb1 = await Wm3LightUntil(l => l.Bool("on") && l.Str("fog") == "659ebb");
                Check("light: over the hills 500 yards away (outside the sphere) the default light's: fog #659ebb, another sky; the camera's spot moved, so the host sent it",
                    lb1 != null && lb1.Str("fog") == "659ebb" && (string)lb1.List("sky")[0] == "00265f" && WorldMapView.LightPostsForTest >= posts0 + 2, lb1 == null ? "null" : Json.Write(lb1));
                Check("light: the water takes the zone's colour (the sphere's river colour over the lake; the default's fog where the game sets none), never today's fixed blue",
                    la1 != null && lb1 != null && la1.Str("w1") != "3a93c4" && lb1.Str("w1") != "3a93c4" && la1.Str("w1") != lb1.Str("w1") && la1.Str("w2") != "103f6e", la1?.Str("w1") + " / " + lb1?.Str("w1") + " / " + la1?.Str("w2"));
                Check("light: the fog distance is still the camera's; the sphere's nearer FogEnd (6500 of the default's 18000) draws it a little nearer (" + la1?.Num("fogNear") + " against " + lb1?.Num("fogNear") + " yd at the same distance)",
                    la1 != null && lb1 != null && la1.Num("dens") < 0.85 && la1.Num("dens") >= 0.8 && lb1.Num("dens") == 1 && Math.Abs(la1.Num("fogNear") / lb1.Num("fogNear") - la1.Num("dens")) < 0.01 && lb1.Num("fogNear") >= lb1.Num("camDist") * 1.2,
                    la1 == null ? "" : la1.Num("dens") + " " + la1.Num("fogNear") + " " + lb1?.Num("fogNear"));
                int posts1 = WorldMapView.LightPostsForTest;
                await View(80, 1060, 700);
                await Task.Delay(1500);
                Check("light: a small pan (100 yards) does not work the light out again (no message)", WorldMapView.LightPostsForTest == posts1, (WorldMapView.LightPostsForTest - posts1) + " sent");
                await View(lx, ly, 30000);
                var lc = await Wm3LightUntil(l => l.Bool("on") && l.Str("fog") == "659ebb" && l.Num("camDist") > 20000);
                Check("light: zoomed out to the continent over the lake the map default's light is used", lc != null && lc.Str("fog") == "659ebb" && lc.Num("dens") == 1, lc == null ? "null" : Json.Write(lc));
                await View(lx, ly, 700);
                var la2 = await Wm3LightUntil(l => l.Bool("on") && l.Str("fog") == "009acd");
                if (SelfTestClock.ShotsOn) { await Task.Delay(600); var png = await WorldMapView.CaptureForTest(); if (png != null) File.WriteAllBytes(P("worldmap-3d-light-synthetic.png"), png); }

                // the page checks a light message again: junk changes nothing
                int ref0 = (int)la2.Num("refused");
                await WmEval("window.wm3.feed({t:'light',on:true,sky:['#zzzzzz','#000000','#000000','#000000','#000000','#ff0000'],fog:'#ff0000',amb:'#ffffff',dir:'#ffffff',sun:'#ffffff',fogEnd:1,fogDef:1,local:1})");
                await WmEval("window.wm3.feed({t:'light',on:true,sky:['#000000'],fog:'#ff0000',amb:'#ffffff',dir:'#ffffff',sun:'#ffffff',fogEnd:1,fogDef:1,local:1})");
                await WmEval("window.wm3.feed({t:'light',on:true,sky:['#000000','#000000','#000000','#000000','#000000','#ff0000'],fog:'#ff0000',amb:'#ffffff',dir:'#ffffff',sun:'#ffffff',fogEnd:-5,fogDef:1,local:1})");
                await WmEval("window.wm3.feed({t:'light',on:true,sky:['#000000','#000000','#000000','#000000','#000000','#ff0000'],fog:'#ff0000',amb:'#ffffff',dir:'#ffffff',sun:'#ffffff',river:'red',fogEnd:1,fogDef:1,local:1})");
                await WmEval("window.wm3.feed({t:'light',on:'yes'})");
                var lj = await Wm3LightUntil(l => true, 2000);
                Check("light: the page refuses light messages that are not the shape (a colour that is not #rrggbb, five sky colours, a negative FogEnd, a water colour 'red', on 'yes') and keeps what it shows",
                    lj != null && (int)lj.Num("refused") == ref0 + 5 && lj.Str("fog") == "009acd" && lj.Bool("on"), lj == null ? "null" : lj.Num("refused") + " " + lj.Str("fog"));

                // the switch in Settings
                SettingsPage.WorldMapLightClickForTest(false);
                var loff = await Wm3LightUntil(l => !l.Bool("on"));
                Check("switch: Settings > World map game lighting off gives the fixed look back (fog / background #0b1c20, white lights, today's water, the camera's fog distance unscaled) and is saved",
                    WlFixedLook(loff) && settings.WorldMapLightOff && !SettingsPage.WorldMapLightShownForTest && WorldMapView.LastLightForTest.Contains("off in Settings"), loff == null ? "null" : Json.Write(loff));
                SettingsPage.WorldMapLightClickForTest(true);
                var lon = await Wm3LightUntil(l => l.Bool("on") && l.Str("fog") == "009acd");
                Check("switch: on again (the default) and the zone's light comes back at once", lon != null && lon.Str("fog") == "009acd" && !settings.WorldMapLightOff && SettingsPage.WorldMapLightShownForTest, lon == null ? "null" : Json.Write(lon));
                var st = await Wm3State();
                Check("light: no script error on the page while the light changes (" + (st?.List("errs")?.Count ?? -1) + " errors)", st != null && (st.List("errs") ?? new List<object>()).Count == 0, st == null ? "?" : Json.Write(st.List("errs")));
            }
            finally
            {
                settings.WorldMapLightOff = savedOff;
                WorldLight.ResetForTest();
                WorldLight.RaiseChangedForTest();      // the page goes back to its fixed look for the checks after this one
                await Wm3LightUntil(l => !l.Bool("on"), 4000);
            }
        }
    }
}
