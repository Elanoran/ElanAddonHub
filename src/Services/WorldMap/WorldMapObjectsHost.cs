using System;
using System.Collections.Generic;
using System.Linq;

namespace ElansAddonHub.Services.WorldMap
{
    // ============================================================================================================================
    // The link between the 3D page's "Trees & objects (experimental)" switch / card / progress line and ObjectsService (3.2; WorldMapView owns one):
    //   page -> app   objects {level} | obuild | opause | oresume | ocancel       (validated in WorldMapProtocol; level off | low | medium | high)
    //   app -> page   ostate {...}   level, phase (idle | scanning | ask | building | paused | done | error), counts, the card's sentence, the models finished
    //                 since the last message (fresh) or all of them (all), the tiles that have placements; and  init.objects  with the same facts at start
    // The page steps the level down by itself when the frames get slow or the graphics card cannot do it; it says so with the same objects {level} message,
    // and the choice is saved like a click. The page asks nothing of the disk itself: it fetches the files the host listed from the virtual host.
    // ============================================================================================================================
    public sealed class WorldMapObjectsHost
    {
        readonly Action<string> post;
        readonly Func<Action, bool> ui;
        readonly Func<Settings> settings;
        readonly Action save;
        public string Level { get; private set; } = "off";
        public readonly List<string> PostedForTest = new List<string>();
        DateTime last = DateTime.MinValue; string lastPhase = ""; bool pending;
        System.Windows.Threading.DispatcherTimer timer;
        bool attached;
        int attachedMap = -1;

        public WorldMapObjectsHost(Action<string> post, Func<Action, bool> ui, Func<Settings> settings, Action save)
        {
            this.post = post; this.ui = ui; this.settings = settings; this.save = save;
        }

        public void Attach() { if (attached) return; attached = true; ObjectsService.Changed += OnChanged; }
        public void Detach() { if (!attached) return; attached = false; ObjectsService.Changed -= OnChanged; try { timer?.Stop(); } catch (Exception) { } }

        public static string LevelOf(Settings s) => WorldMapObjects.Normalize(s?.WorldMap3dObjects);

        public Dictionary<string, object> InitInfo(int mapId, string baseUrl)
        {
            attachedMap = mapId;
            Level = LevelOf(settings());
            var dir = ObjectsService.DirOf(mapId);
            return new Dictionary<string, object>
            {
                ["level"] = Level, ["base"] = baseUrl + "/" + WorldMapObjects.Folder, ["models"] = ObjectsService.Built(mapId).OrderBy(i => i).ToList(), ["tiles"] = Tiles(dir),
            };
        }

        static List<int> Tiles(string dir)
        {
            try
            {
                var p = WorldMapObjects.TilesPath(dir);
                var t = System.IO.File.Exists(p) ? WorldMapObjects.TilesFromBytes(System.IO.File.ReadAllBytes(p)) : null;
                return t == null ? new List<int>() : t.Keys.OrderBy(k => k).ToList();
            }
            catch (Exception) { return new List<int>(); }
        }

        public void OnInited(int mapId)
        {
            attachedMap = mapId;
            Level = LevelOf(settings());
            if (Level != "off") Select(Level, mapId);
        }

        public void Focus(double lat, double lng, int mapId) => ObjectsService.Focus(mapId, lng / 4.0 * (1600.0 / 3), -lat / 4.0 * (1600.0 / 3));

        public void Handle(WorldMapMessage m, int mapId)
        {
            if (mapId < 0) return;
            switch (m.Type)
            {
                case WorldMapMessageType.Objects: Select(m.Detail, mapId); break;
                case WorldMapMessageType.OBuild: ObjectsService.Start(mapId); SendState(true); break;
                case WorldMapMessageType.OPause: ObjectsService.Pause(mapId); SendState(true); break;
                case WorldMapMessageType.OResume: ObjectsService.Resume(mapId); SendState(true); break;
                case WorldMapMessageType.OCancel: ObjectsService.Cancel(mapId); SetLevel("off"); SendState(true); break;
            }
        }

        void SetLevel(string level)
        {
            Level = WorldMapObjects.Normalize(level);
            var s = settings();
            if (s != null && WorldMapObjects.Normalize(s.WorldMap3dObjects) != Level) { s.WorldMap3dObjects = Level == "off" ? null : Level; save?.Invoke(); }
        }

        // Off hides them (a build under way is paused); a level looks at what is on disk and asks when something has to be read. A change between
        // Low / Medium / High only changes how far the page draws: nothing is asked again.
        void Select(string level, int mapId)
        {
            SetLevel(level);
            if (Level == "off")
            {
                if (ObjectsService.IsRunning(mapId)) ObjectsService.Pause(mapId);
                SendState(true);
                return;
            }
            if (ObjectsService.IsRunning(mapId)) { ObjectsService.Resume(mapId); SendState(true); return; }
            var st = ObjectsService.StateOf(mapId);
            if (st == null || st.Phase == "idle" || st.Phase == "error") ObjectsService.Prepare(mapId);
            SendState(true);
        }

        void OnChanged(int mapId)
        {
            if (mapId != attachedMap) return;
            ui(() =>
            {
                var st = ObjectsService.StateOf(mapId);
                bool phaseChanged = st != null && st.Phase != lastPhase;
                var now = DateTime.UtcNow;
                if (phaseChanged || (now - last).TotalMilliseconds >= 450) SendState(false);
                else if (!pending)
                {
                    pending = true;
                    if (timer == null) { timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) }; timer.Tick += (s, e) => { timer.Stop(); SendState(false); }; }
                    timer.Stop(); timer.Start();
                }
            });
        }

        public void SendState(bool force)
        {
            pending = false;
            int mapId = attachedMap; if (mapId < 0) return;
            var st = ObjectsService.StateOf(mapId);
            bool full = force || (st != null && st.Phase != lastPhase);
            var dir = ObjectsService.DirOf(mapId);
            var json = StateJson(mapId, Level, st, ObjectsService.TakeFresh(mapId), full && Level != "off" ? ObjectsService.Built(mapId) : null, full ? Tiles(dir) : null);
            last = DateTime.UtcNow; lastPhase = st?.Phase ?? "";
            if (PostedForTest.Count < 400) PostedForTest.Add(json);
            post(json);
        }

        public static string StateJson(int mapId, string level, ObjectsState st, IList<int> fresh, ICollection<int> all, IList<int> tiles)
        {
            var d = new Dictionary<string, object>
            {
                ["t"] = "ostate", ["map"] = mapId, ["level"] = level, ["phase"] = st?.Phase ?? "idle",
                ["done"] = st?.Done ?? 0, ["total"] = st?.Total ?? 0, ["left"] = Math.Round(st?.SecondsLeft ?? 0), ["text"] = st?.Text ?? "",
                ["error"] = st?.Error ?? "", ["name"] = st?.Name ?? "", ["bytes"] = st?.BytesOnDisk ?? 0, ["placements"] = st?.Placements ?? 0,
            };
            if (st != null && st.Phase == "ask") d["card"] = WorldMapObjects.CardText(st);
            if (fresh != null && fresh.Count > 0) d["fresh"] = fresh.ToList();
            if (all != null) d["all"] = all.OrderBy(i => i).ToList();
            if (tiles != null) d["tiles"] = tiles.ToList();
            return Json.Write(d);
        }
    }
}
