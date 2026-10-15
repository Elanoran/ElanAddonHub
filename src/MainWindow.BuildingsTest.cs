using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Maps;
using ElansAddonHub.Services.WorldMap;

namespace ElansAddonHub
{
    // --selftest "buildings": 3D buildings and bridges on the world map (WmoFile, WmoMesh, WorldMapBuildings*, the Buildings switch of the 3D page).
    // Part 1 (this file): the section, the synthetic files and the formats. Part 2 (.Service.cs): the build service. Part 3 (.Page.cs): the switch and the card in the 3D page.
    // Part 4 (.Real.cs): the real game files when this PC has them.
    public partial class MainWindow
    {
        async Task<string> BuildingsTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            void Check(string what, bool cond, string detail = null) { notes.Add((cond ? "ok   " : "FAIL ") + what + (!cond && detail != null ? " [" + detail + "]" : "")); ok &= cond; Util.Log("buildings test: " + (cond ? "ok   " : "FAIL ") + what); }
            string P(string n) => Path.Combine(dir, n);
            var work = Path.Combine(dir, "buildings");
            var savedRoot = WorldMapCache.RootOverride; var savedCache = MapData.CacheFile; var savedWowRoot = MapData.WowRoot; var savedEnabled = MapData.Enabled; var savedWdt = WorldMapService.WdtSourceForTest;
            var savedAdt = WorldMapService.AdtSourceForTest; var savedMode = settings.WorldMap3dBuildings; var savedLayers = settings.WorldMapLayers; var savedDetail = settings.WorldMap3dDetail; bool saved3d = settings.WorldMap3d; var savedExag = settings.WorldMapExag;
            try
            {
                if (Directory.Exists(work)) Util.DeleteDir(work);
                Directory.CreateDirectory(work);
                Check("fixture: everything of this test is under the scratch folder (never the real game, never the real data folder)", Path.GetFullPath(work).StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase));
                BuildingsFormatChecks(Check);
                await BuildingsServiceChecks(work, Check, notes);
                await BuildingsPageChecks(work, Check, notes, P);
            }
            catch (Exception e) { Check("buildings test crashed", false, e.ToString()); }
            finally
            {
                WorldMapCache.RootOverride = savedRoot; MapData.CacheFile = savedCache; MapData.WowRoot = savedWowRoot; MapData.Enabled = savedEnabled; WorldMapService.WdtSourceForTest = savedWdt; WorldMapService.AdtSourceForTest = savedAdt;
                BuildingsService.SourceForTest = null; BuildingsService.GroundForTest = null; BuildingsService.ModelDelayMsForTest = 0; BuildingsService.ResetForTest();
                settings.WorldMap3dBuildings = savedMode; settings.WorldMapLayers = savedLayers; settings.WorldMap3dDetail = savedDetail; settings.WorldMap3d = saved3d; settings.WorldMapExag = savedExag;
            }
            try { await BuildingsRealChecks(dir, work, Check, P, notes); }
            catch (Exception e) { Check("buildings real-data test crashed", false, e.ToString()); }
            return (ok ? "buildings: ok" : "buildings: FAILED") + "\r\n" + string.Join("\r\n", notes);
        }
    }
}
