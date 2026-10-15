using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace ElansAddonHub.Services.WorldMap
{
    // ============================================================================================================================
    // The link between the 3D page's Buildings switch / card / progress line and BuildingsService (the control WorldMapView owns one):
    //   page -> app   buildings {mode} | bbuild {mode} | bpause | bresume | bcancel      (validated in WorldMapProtocol)
    //   app -> page   bstate {...}   mode, phase (idle | scanning | ask | building | paused | done | error), counts, the estimate for the card, the shapes finished since
    //                 the last message, whether the placements file exists; and  init.buildings  with the same facts at start
    // The page asks nothing of the disk itself: it fetches placements.bin and the finished models from the virtual host, and only the ones this host listed.
    // ============================================================================================================================
    public sealed class WorldMapBuildingsHost
    {
        readonly Action<string> post;
        readonly Func<Action, bool> ui;                   // runs on the UI thread (false = gone)
        readonly Func<int> mapOf;
        readonly Func<Settings> settings;
        readonly Action save;
        public string Mode { get; private set; } = "off";
        public readonly List<string> PostedForTest = new List<string>();
        DateTime last = DateTime.MinValue; string lastPhase = ""; bool pending;
        System.Windows.Threading.DispatcherTimer timer;
        bool attached;
        int attachedMap = -1;

        public WorldMapBuildingsHost(Action<string> post, Func<Action, bool> ui, Func<int> mapOf, Func<Settings> settings, Action save)
        {
            this.post = post; this.ui = ui; this.mapOf = mapOf; this.settings = settings; this.save = save;
        }

        public void Attach() { if (attached) return; attached = true; BuildingsService.Changed += OnChanged; }
        public void Detach() { if (!attached) return; attached = false; BuildingsService.Changed -= OnChanged; try { timer?.Stop(); } catch (Exception) { } }

        public static string ModeOf(Settings s) => WorldMapBuildings.Normalize(s?.WorldMap3dBuildings);

        // what the page's init message carries
        public Dictionary<string, object> InitInfo(int mapId, string baseUrl)
        {
            attachedMap = mapId;
            Mode = ModeOf(settings());
            var built = BuildingsService.Built(mapId);
            return new Dictionary<string, object>
            {
                ["mode"] = Mode, ["base"] = baseUrl + "/" + WorldMapBuildings.Folder, ["models"] = built.OrderBy(i => i).ToList(),
                ["placements"] = System.IO.File.Exists(WorldMapBuildings.PlacementsPath(BuildingsService.DirOf(mapId))),
            };
        }

        // the page started: if buildings are switched on, ask the same question a click on the switch asks
        public void OnInited(int mapId)
        {
            attachedMap = mapId;
            Mode = ModeOf(settings());
            if (Mode != "off") Select(Mode, mapId);
        }

        public void Focus(double lat, double lng, int mapId)
        {
            // the page's ground plane: x = lng / 4 x 533.33, z = -lat / 4 x 533.33
            BuildingsService.Focus(mapId, lng / 4.0 * (1600.0 / 3), -lat / 4.0 * (1600.0 / 3));
        }

        public void Handle(WorldMapMessage m, int mapId)
        {
            if (mapId < 0) return;
            switch (m.Type)
            {
                case WorldMapMessageType.Buildings: Select(m.Detail, mapId); break;
                case WorldMapMessageType.BBuild:
                    SetMode(m.Detail);
                    BuildingsService.Start(mapId, m.Detail); SendState(true);
                    break;
                case WorldMapMessageType.BPause: BuildingsService.Pause(mapId); SendState(true); break;
                case WorldMapMessageType.BResume: BuildingsService.Resume(mapId); SendState(true); break;
                case WorldMapMessageType.BCancel:
                    BuildingsService.Cancel(mapId); SetMode("off"); SendState(true);
                    break;
            }
        }

        void SetMode(string mode)
        {
            Mode = WorldMapBuildings.Normalize(mode);
            var s = settings();
            if (s != null && WorldMapBuildings.Normalize(s.WorldMap3dBuildings) != Mode) { s.WorldMap3dBuildings = Mode == "off" ? null : Mode; save?.Invoke(); }
        }

        // the Buildings switch: Off hides them (a build under way is paused), Bridges / All look at what is on disk and ask when something has to be made
        void Select(string mode, int mapId)
        {
            SetMode(mode);
            if (Mode == "off")
            {
                if (BuildingsService.IsRunning(mapId)) BuildingsService.Pause(mapId);
                SendState(true);
                return;
            }
            if (BuildingsService.IsRunning(mapId)) { BuildingsService.Resume(mapId); BuildingsService.Prepare(mapId, Mode); SendState(true); return; }
            BuildingsService.Prepare(mapId, Mode);
            SendState(true);
        }

        void OnChanged(int mapId)
        {
            if (mapId != attachedMap) return;
            ui(() =>
            {
                var st = BuildingsService.StateOf(mapId);
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
            var st = BuildingsService.StateOf(mapId);
            bool full = force || (st != null && st.Phase != lastPhase);
            var json = StateJson(mapId, Mode, st, BuildingsService.TakeFresh(mapId), full && Mode != "off" ? BuildingsService.Built(mapId) : null);
            last = DateTime.UtcNow; lastPhase = st?.Phase ?? "";
            if (PostedForTest.Count < 400) PostedForTest.Add(json);
            post(json);
        }

        // the message (a static function of its inputs, for the tests)
        public static string StateJson(int mapId, string mode, BuildingsState st, IList<int> fresh, ICollection<int> all)
        {
            var d = new Dictionary<string, object>
            {
                ["t"] = "bstate", ["map"] = mapId, ["mode"] = mode, ["phase"] = st?.Phase ?? "idle",
                ["done"] = st?.Done ?? 0, ["total"] = st?.Total ?? 0, ["left"] = Math.Round(st?.SecondsLeft ?? 0), ["text"] = st?.Text ?? "",
                ["error"] = st?.Error ?? "", ["name"] = st?.Name ?? "", ["bytes"] = st?.BytesOnDisk ?? 0,
                ["placements"] = System.IO.File.Exists(WorldMapBuildings.PlacementsPath(BuildingsService.DirOf(mapId))),
            };
            var e = st?.Estimate;
            if (st?.Estimate != null) d["card"] = CardText(st.Estimate, st.Name.Length > 0 ? st.Name : "this continent", st.Estimate.ModelsDone);
            if (e != null)
                d["est"] = new Dictionary<string, object> { ["buildings"] = e.Buildings, ["models"] = e.Models, ["modelsDone"] = e.ModelsDone, ["minutes"] = Math.Round(e.Minutes, 1), ["mb"] = Math.Round(e.Bytes / 1048576.0, 1) };
            if (fresh != null && fresh.Count > 0) d["fresh"] = fresh.ToList();
            if (all != null) d["all"] = all.OrderBy(i => i).ToList();
            return Json.Write(d);
        }

        // the card's sentence (one place for the page's text and the tests)
        public static string CardText(BuildingEstimate e, string name, int modelsDone)
        {
            string t = e.Mode == "bridges" ? "bridges" : "buildings";
            string time = e.Minutes < 0.75 ? "less than a minute" : e.Minutes < 1.5 ? "about a minute" : "about " + Math.Round(e.Minutes).ToString("0", CultureInfo.InvariantCulture) + " minutes";
            string mb = e.Bytes < 1048576 ? "under 1 MB" : "about " + Math.Round(e.Bytes / 1048576.0).ToString("0", CultureInfo.InvariantCulture) + " MB";
            var main = "Show " + (e.Mode == "bridges" ? "bridges" : "buildings") + " in 3D. Reads the shapes of about " + e.Buildings.ToString("N0", CultureInfo.InvariantCulture) + " " + t + " in " + name + " from your own game files once: " + time + " in the background, " + mb + " on disk.";
            if (modelsDone > 0 && e.Models > 0) main += " " + modelsDone.ToString("N0", CultureInfo.InvariantCulture) + " of " + e.Models.ToString("N0", CultureInfo.InvariantCulture) + " shapes are made already.";
            return main;
        }
    }
}
