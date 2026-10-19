using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text;
using ElansAddonHub.Services;
using Elans.Shared;

namespace ElansAddonHub.Lodge
{
    // Shared flight times (Lodge server 2.13, feature "flights"). The Hunter Helper addon learns how long each flight leg takes; this uploads what
    // changed (times only, at most one batch of 500 legs per 10 minutes, and only while "Share flight times with the lodge" is on), downloads the
    // lodge's merged table and writes it into the companion addon as SharedData.lua (Services/FlightShare.cs does the reading, validating and
    // writing). On a server without the feature nothing is sent and nothing is written. Downloading works with sharing off.
    public partial class LodgeSession
    {
        public static readonly Version FlightServer = FlightShare.ServerVersion;
        public bool SupportsFlights { get; private set; }

        FlightShare.Local flightLocal;                       // last read of the Hunter Helper saved variables
        Dictionary<string, string> flightSent;               // key -> signature of what the lodge already has from me (persisted per server)
        FlightShare.Batch flightInflight;                    // a flights.share waiting for its answer
        DateTime flightNextShare = DateTime.MinValue;        // not before (the server allows one per 10 minutes)
        DateTime flightLastGet = DateTime.MinValue;
        bool flightGuest;                                    // the server said guests can't contribute
        bool flightBacklog;                                  // changed legs are waiting for the next slot
        int flightPollTick;
        public FlightShare.Merged FlightData { get; private set; }
        public int FlightFilesWritten { get; private set; }

        string FlightSentPath => Path.Combine(Util.DataDir, "flights-sent.txt");

        void ReadFlightFeatures(List<string> features)
        {
            var v = ParseVersion(ServerVersion);
            SupportsFlights = features.Contains("flights") && (v == null || v >= FlightServer);
            flightInflight = null; flightGuest = false;
            ReadFlightRouteFeatures(features);           // 2.88: learned flight paths (LodgeSession.FlightRoutes.cs)
        }

        void ResetFlights()
        {
            SupportsFlights = false; flightInflight = null; flightGuest = false; flightBacklog = false;
            flightLocal = null; flightSent = null; flightNextShare = DateTime.MinValue; flightLastGet = DateTime.MinValue;
            ResetFlightRoutes();
        }

        // what the lodge already has from me. The file names the server, so another lodge starts from nothing.
        Dictionary<string, string> FlightSent()
        {
            if (flightSent != null) return flightSent;
            flightSent = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                if (File.Exists(FlightSentPath))
                {
                    var lines = File.ReadAllLines(FlightSentPath);
                    if (lines.Length > 0 && lines[0] == FlightServerTag())
                        foreach (var l in lines.Skip(1))
                        {
                            var p = l.Split('\t');
                            if (p.Length == 2 && FlightShare.ValidKey(p[0])) flightSent[p[0]] = p[1];
                        }
                }
            }
            catch (Exception e) { Util.Log("flights: could not read the upload record (" + e.GetType().Name + ")"); }
            return flightSent;
        }

        string FlightServerTag() => "server\t" + (Settings?.LodgeUrl ?? "");

        void SaveFlightSent()
        {
            try
            {
                var tmp = FlightSentPath + ".tmp";
                File.WriteAllLines(tmp, new[] { FlightServerTag() }.Concat(FlightSent().Select(kv => kv.Key + "\t" + kv.Value)), new UTF8Encoding(false));
                if (File.Exists(FlightSentPath)) File.Replace(tmp, FlightSentPath, null); else File.Move(tmp, FlightSentPath);
            }
            catch (Exception e) { Util.Log("flights: could not save the upload record (" + e.GetType().Name + ")"); }
        }

        bool FlightMayShare => Settings != null && !Settings.FlightShareOff && !flightGuest && MyRole != "guest" && !(Me != null && Me.Guest);

        // After the welcome, after a changed saved-variables file, and when a waiting batch may go: upload what changed (if allowed), then ask for the table.
        // `connect` = just signed in (always asks for the table).
        public void FlightSync(bool connect = false)
        {
            FlightRouteSync(connect);
            if (!SupportsFlights) return;
            bool changedFile = false;
            try
            {
                flightLocal = FlightShare.ReadCached(Settings?.WowRoot, out changedFile);
            }
            catch (Exception e) { Util.Log("flights: " + e.GetType().Name); flightLocal = new FlightShare.Local(); }
            bool shared = FlightMayShare && SendFlightBatch();
            if (!shared && (changedFile || (connect && Clock() - flightLastGet > TimeSpan.FromSeconds(30)))) RequestFlights();
        }

        bool SendFlightBatch()
        {
            if (flightInflight != null) return true;                                   // an answer is pending: the table follows it
            var batch = FlightShare.MakeBatch(flightLocal, FlightSent());
            flightBacklog = batch != null;
            if (batch == null) return false;
            if (Clock() < flightNextShare) return false;                               // the next slot is not here yet
            flightInflight = batch;
            flightNextShare = Clock() + FlightShare.ShareEvery + TimeSpan.FromSeconds(5);
            _ = Send(batch.Frame);
            return true;
        }

        void RequestFlights()
        {
            if (!SupportsFlights) return;
            flightLastGet = Clock();
            _ = Send(LodgeJson.Build(new FlightsGetMsg().Write));
        }

        void OnFlightShared(JsonObject m)
        {
            var batch = flightInflight;
            flightInflight = null;
            var answer = FlightsSharedMsg.Read(new LodgeJsonFields(m));
            if (answer.Ok && batch != null)
            {
                var sent = FlightSent();
                foreach (var kv in batch.Sigs) sent[kv.Key] = kv.Value;                // refused entries are not retried forever either
                SaveFlightSent();
                flightBacklog = batch.Remaining > 0;
            }
            else
            {
                var why = answer.Why;
                if (why == "guest") flightGuest = true;
                else if (why == "rate") flightNextShare = Clock() + FlightShare.ShareEvery;
                else flightNextShare = Clock() + TimeSpan.FromMinutes(2);
            }
            RequestFlights();
        }

        void OnFlightData(JsonObject m)
        {
            if (!SupportsFlights) return;
            var data = FlightShare.ParseServer(m);
            FlightData = data;
            var res = SharedDataFile.Write(Settings?.WowRoot, data, SharedDataFile.Current, Clock());
            FlightFilesWritten = res.Written.Count;
            Util.Log("flights: " + data.Legs.Count + " legs from the lodge, " + res.Written.Count + " file(s) written, " + res.Unchanged.Count + " unchanged");
            Changed?.Invoke();
        }

        // After Elan's Hub was installed or updated its SharedData.lua is the empty shipped one: write the last lodge data back (the same validated BuildLua / WriteShared
        // path as a download). Nothing is written when no flight data has arrived in this session. Returns the number of files written.
        public int RewriteSharedData()
        {
            var data = FlightData;
            if (data == null) return 0;
            var res = SharedDataFile.Write(Settings?.WowRoot, data, SharedDataFile.Current, Clock());
            Util.Log("flights: SharedData.lua restored after the Hub install, " + res.Written.Count + " file(s) written, " + res.Unchanged.Count + " unchanged");
            return res.Written.Count;
        }

        // every 2 s with the other ticks: look for a changed saved-variables file about every 30 s; send a waiting batch when its slot comes
        void FlightTick()
        {
            FlightRouteTick();
            if (!SupportsFlights) return;
            if (flightPollTick++ % 15 == 0 || (flightBacklog && flightInflight == null && Clock() >= flightNextShare)) FlightSync();
        }

        // "Share flight times with the lodge" was switched
        public void FlightSettingsChanged()
        {
            FlightSync();                                // also the learned routes (a no-op without either feature)
        }

        // test hooks
        public bool FlightBacklogForTest => flightBacklog;
        public bool FlightInflightForTest => flightInflight != null;
        public void FlightResetForTest() { FlightData = null; flightSent = null; flightInflight = null; flightNextShare = DateTime.MinValue; flightGuest = false; flightBacklog = false; flightLocal = null; FlightShare.ResetCache(); }
        public Dictionary<string, string> FlightSentForTest => FlightSent();
    }
}
