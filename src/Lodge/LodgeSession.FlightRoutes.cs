using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ElansAddonHub.Services;
using ElansAddonHub.Services.WorldMap;
using Elans.Shared;

namespace ElansAddonHub.Lodge
{
    // Learned flight paths shared with the Lodge (feature "flightroutes", Lodge server after 2.26). The same switch as the flight times ("Share flight times with the
    // lodge") decides whether my learned routes go up: what changed, at most one batch of 100 routes per 10 minutes. The Lodge's pooled routes come down
    // either way (downloading works with sharing off) and LearnedFlights checks them again before the map or the elevation solver uses one. On a server
    // without the feature nothing is sent. Services/WorldMap/LearnedFlights(.Lodge).cs does the validating and the batching.
    public partial class LodgeSession
    {
        public bool SupportsFlightRoutes { get; private set; }

        Dictionary<string, string> routeSent;                // key -> signature of what the lodge already has from me (persisted per server)
        LearnedFlights.ShareBatch routeInflight;
        DateTime routeNextShare = DateTime.MinValue;
        DateTime routeLastGet = DateTime.MinValue;
        bool routeGuest, routeBacklog;
        int routeSeenVersion = -1;
        public int FlightRoutesReceived { get; private set; }

        // tests swap the store (null = LearnedFlights.Shared)
        public LearnedFlights RouteStoreForTest;
        LearnedFlights RouteStore => RouteStoreForTest ?? LearnedFlights.Shared;

        string RouteSentPath => Path.Combine(Util.DataDir, "flightroutes-sent.txt");

        void ReadFlightRouteFeatures(List<string> features)
        {
            // the feature itself is the gate: only a server that pools learned routes lists it (the version a release gives it is not known here yet)
            SupportsFlightRoutes = features.Contains("flightroutes");
            routeInflight = null; routeGuest = false;
        }

        void ResetFlightRoutes()
        {
            SupportsFlightRoutes = false; routeInflight = null; routeGuest = false; routeBacklog = false;
            routeSent = null; routeNextShare = DateTime.MinValue; routeLastGet = DateTime.MinValue; routeSeenVersion = -1;
        }

        Dictionary<string, string> RouteSent()
        {
            if (routeSent != null) return routeSent;
            routeSent = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                if (File.Exists(RouteSentPath))
                {
                    var lines = File.ReadAllLines(RouteSentPath);
                    if (lines.Length > 0 && lines[0] == FlightServerTag())
                        foreach (var l in lines.Skip(1))
                        {
                            var p = l.Split('\t');
                            if (p.Length == 2 && LearnedFlights.TryKey(p[0], out _, out _) && p[1].Length <= 64) routeSent[p[0]] = p[1];
                        }
                }
            }
            catch (Exception e) { Util.Log("flight routes: could not read the upload record (" + e.GetType().Name + ")"); }
            return routeSent;
        }

        void SaveRouteSent()
        {
            try
            {
                var tmp = RouteSentPath + ".tmp";
                File.WriteAllLines(tmp, new[] { FlightServerTag() }.Concat(RouteSent().Select(kv => kv.Key + "\t" + kv.Value)), new UTF8Encoding(false));
                if (File.Exists(RouteSentPath)) File.Replace(tmp, RouteSentPath, null); else File.Move(tmp, RouteSentPath);
            }
            catch (Exception e) { Util.Log("flight routes: could not save the upload record (" + e.GetType().Name + ")"); }
        }

        bool RouteMayShare => Settings != null && !Settings.FlightShareOff && !routeGuest && MyRole != "guest" && !(Me != null && Me.Guest);

        // after the welcome, when the store changed (a new flight, Clear) and when a waiting batch may go: upload what changed (if allowed), then ask for the pool
        public void FlightRouteSync(bool connect = false)
        {
            if (!SupportsFlightRoutes) return;
            int ver = 0; try { ver = RouteStore.Version; } catch (Exception) { }
            bool changed = ver != routeSeenVersion; routeSeenVersion = ver;
            bool shared = RouteMayShare && SendRouteBatch();
            if (!shared && (connect && Clock() - routeLastGet > TimeSpan.FromSeconds(30))) RequestFlightRoutes();
            else if (!shared && changed && !connect && Clock() - routeLastGet > TimeSpan.FromMinutes(10)) RequestFlightRoutes();
        }

        bool SendRouteBatch()
        {
            if (routeInflight != null) return true;
            LearnedFlights.ShareBatch batch = null;
            try { batch = RouteStore.MakeBatch(RouteSent()); } catch (Exception e) { Util.Log("flight routes: " + e.GetType().Name); }
            routeBacklog = batch != null;
            if (batch == null) return false;
            if (Clock() < routeNextShare) return false;
            routeInflight = batch;
            routeNextShare = Clock() + FlightShare.ShareEvery + TimeSpan.FromSeconds(5);
            _ = Send(batch.Frame);
            return true;
        }

        void RequestFlightRoutes()
        {
            if (!SupportsFlightRoutes) return;
            routeLastGet = Clock();
            _ = Send(new Dictionary<string, object> { ["t"] = LodgeMsg.FlightsRoutesGet });
        }

        void OnFlightRoutesShared(Dictionary<string, object> m)
        {
            var batch = routeInflight;
            routeInflight = null;
            if (m.Bool("ok") && batch != null)
            {
                var sent = RouteSent();
                foreach (var kv in batch.Sigs) sent[kv.Key] = kv.Value;
                SaveRouteSent();
                routeBacklog = batch.Remaining > 0;
            }
            else
            {
                var why = m.Str("why");
                if (why == "guest") routeGuest = true;
                else if (why == "rate") routeNextShare = Clock() + FlightShare.ShareEvery;
                else routeNextShare = Clock() + TimeSpan.FromMinutes(2);
            }
            RequestFlightRoutes();
        }

        void OnFlightRoutesData(Dictionary<string, object> m)
        {
            if (!SupportsFlightRoutes) return;
            var data = LearnedFlights.ParseServer(m);
            FlightRoutesReceived = data.Routes.Count;
            try { RouteStore.SetLodge(data); } catch (Exception e) { Util.Log("flight routes: " + e.GetType().Name); }
            Util.Log("flight routes: " + data.Routes.Count + " learned routes from the lodge" + (data.Dropped > 0 ? ", " + data.Dropped + " refused" : ""));
            Changed?.Invoke();
        }

        // with the flight-time tick (every 30 s or when a slot comes)
        void FlightRouteTick()
        {
            if (!SupportsFlightRoutes) return;
            int ver = -2; try { ver = RouteStore.Version; } catch (Exception) { }
            if (ver != routeSeenVersion || (routeBacklog && routeInflight == null && Clock() >= routeNextShare) || Clock() - routeLastGet > TimeSpan.FromHours(1)) FlightRouteSync();
            if (Clock() - routeLastGet > TimeSpan.FromHours(1)) RequestFlightRoutes();
        }

        // test hooks
        public bool RouteBacklogForTest => routeBacklog;
        public bool RouteInflightForTest => routeInflight != null;
        public Dictionary<string, string> RouteSentForTest => RouteSent();
        public void FlightRoutesResetForTest() { routeSent = null; routeInflight = null; routeNextShare = DateTime.MinValue; routeLastGet = DateTime.MinValue; routeGuest = false; routeBacklog = false; routeSeenVersion = -1; }
    }
}
