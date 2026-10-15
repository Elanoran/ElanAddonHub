using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Atlas.Classic;
using ElansAddonHub.Services.DungeonMaps;
using ElansAddonHub.Services.Maps;
using ElansAddonHub.Services.WorldMap;

namespace ElansAddonHub
{
    // The "<name>real" sections (see SelfTestCache): the checks that read the player's real WoW install, run apart from their synthetic parent so that a
    // run without a change to anything that reads game data does not pay for them. Each one gets what its parent's finally-block used to give: the
    // seams it sets are put back, and the page / atlas state is reset.
    public partial class MainWindow
    {
        async Task<string> RealSection(string name, string dir, Func<string, string, CheckFn, Func<string, string>, List<string>, Task> body)
        {
            var notes = new List<string>();
            bool ok = true;
            void Check(string what, bool cond, string detail = null) { SelfTestClock.Mark(what); notes.Add((cond ? "ok   " : "FAIL ") + what + (!cond && detail != null ? " [" + detail + "]" : "")); ok &= cond; Util.Log(name + " test " + (cond ? "ok " : "FAIL ") + what.Substring(0, Math.Min(80, what.Length))); }
            string P(string n) => Path.Combine(dir, n);
            var work = Path.Combine(dir, name);
            var savedRoot = WorldMapCache.RootOverride; var savedWdt = WorldMapService.WdtSourceForTest; var savedAdt = WorldMapService.AdtSourceForTest; var savedTile = WorldMapService.TileSourceForTest; var savedWr = WorldMapService.WowRoot;
            var savedMCache = MapData.CacheFile; var savedMRoot = MapData.WowRoot; var savedMEnabled = MapData.Enabled; var savedLodge = AtlasQuery.Context.Lodge;
            var savedLayers = settings.WorldMapLayers; var savedLayersV = settings.WorldMapLayersVersion; var savedLast = settings.WorldMapLast;
            var savedMode = settings.WorldMap3dBuildings; var savedDetail = settings.WorldMap3dDetail; bool saved3d = settings.WorldMap3d; var savedExag = settings.WorldMapExag;
            var savedPRoot = DungeonPlans.RootOverride; var savedPSrc = DungeonPlans.SourceForTest; var savedPAuto = DungeonPlans.AutoSeconds; var savedPEn = DungeonPlans.Enabled; var savedPWow = DungeonPlans.WowRoot;
            var savedMapDir = MapRender.DirOverride; var savedMapTiles = MapRender.TileSourceForTest; var savedIcon = IconStore.WowRoot;
            double savedW = Width, savedH = Height;
            try
            {
                if (Directory.Exists(work)) Util.DeleteDir(work);
                Directory.CreateDirectory(work);
                MapRender.DirOverride = Path.Combine(work, "maps");
                await body(dir, work, Check, P, notes);
            }
            catch (Exception e) { Check(name + " test crashed", false, e.ToString()); }
            finally
            {
                Width = savedW; Height = savedH;
                try { WorldMapView.Dispose(); } catch (Exception) { }
                try { WorldMapService.CancelAll(); WorldMapService.ResetForTest(); } catch (Exception) { }
                WorldMapCache.RootOverride = savedRoot; WorldMapService.WdtSourceForTest = savedWdt; WorldMapService.AdtSourceForTest = savedAdt; WorldMapService.TileSourceForTest = savedTile; WorldMapService.WowRoot = savedWr;
                MapData.ResetForTest(); MapData.CacheFile = savedMCache; MapData.WowRoot = savedMRoot; MapData.Enabled = savedMEnabled; AtlasQuery.Context.Lodge = savedLodge;
                settings.WorldMapLayers = savedLayers; settings.WorldMapLayersVersion = savedLayersV; settings.WorldMapLast = savedLast;
                settings.WorldMap3dBuildings = savedMode; settings.WorldMap3dDetail = savedDetail; settings.WorldMap3d = saved3d; settings.WorldMapExag = savedExag;
                DungeonPlans.RootOverride = savedPRoot; DungeonPlans.SourceForTest = savedPSrc; DungeonPlans.AutoSeconds = savedPAuto; DungeonPlans.Enabled = savedPEn; DungeonPlans.WowRoot = savedPWow; DungeonPlans.ResetForTest();
                MapRender.DirOverride = savedMapDir; MapRender.TileSourceForTest = savedMapTiles; MapRender.ForgetFailuresForTest(); IconStore.WowRoot = savedIcon;
                BuildingsService.SourceForTest = null; BuildingsService.GroundForTest = null; BuildingsService.ModelDelayMsForTest = 0; BuildingsService.ResetForTest();
                ClassicAtlas.Use(null);
                try { ResetAtlasForTest(); } catch (Exception) { }
            }
            return (ok ? name + ": ok" : name + ": FAILED") + "\r\n" + string.Join("\r\n", notes);
        }

        Task<string> BuildingsRealTest(string dir) => RealSection("buildingsreal", dir, (d, work, Check, P, notes) => BuildingsRealChecks(d, work, Check, P, notes));
        Task<string> InstancesRealTest(string dir) => RealSection("instancesreal", dir, (d, work, Check, P, notes) => InstancesRealChecks(d, work, Check, P, notes));
        Task<string> DungeonMapsRealTest(string dir) => RealSection("dungeonmapsreal", dir, (d, work, Check, P, notes) => DmRealChecks(d, work, Check, notes));
        Task<string> MapDataRealTest(string dir) => RealSection("mapdatareal", dir, (d, work, Check, P, notes) => RealMapChecks(d, work, Check, notes, P));
        Task<string> AtlasPageRealTest(string dir) => RealSection("atlaspagereal", dir, (d, work, Check, P, notes) => AtlasRealChecks(d, work, Check));
    }
}
