using System;
using System.Collections.Generic;
using System.Linq;
using ElansAddonHub.Services;
using Elans.Shared;

namespace ElansAddonHub.Lodge
{
    // Reputations in the Lodge Armory (feature "reputations"). SENDING: only while the member has "Share my characters with the Lodge" on AND "Also share their
    // reputations" on (Settings > Lodge > Armory, both off by default), only for the ticked characters, and only to a server that lists the feature: the snapshot then
    // carries faction id, standing and progress per faction (ArmoryShare.Build). Switching it off re-uploads the characters without them, so the Lodge forgets them.
    // READING: the Atlas faction page asks "where does the Lodge stand with this faction" (armory.standing); answers are kept two minutes per faction.
    // On a server without the feature (or with it switched off by its owner) nothing is sent and the page shows no Lodge section.
    public partial class LodgeSession
    {
        public bool SupportsReps { get; private set; }
        bool repsOffByServer;
        public event Action StandingsChanged;
        public static readonly TimeSpan StandingsKeep = TimeSpan.FromMinutes(2), StandingsRetry = TimeSpan.FromSeconds(10);

        sealed class StandingsAnswer { public DateTime Asked, Answered; public List<LodgeStanding> Rows = new List<LodgeStanding>(); }
        readonly Dictionary<int, StandingsAnswer> standingsAnswers = new Dictionary<int, StandingsAnswer>();

        // the member's reputations go into the snapshots: both switches on and a server that takes them
        bool RepsShared => SupportsReps && !repsOffByServer && Settings != null && Settings.ArmoryShareOn && Settings.ArmoryShareReps;

        public bool StandingsAvailable => SupportsReps && !repsOffByServer && ArmoryAvailable;

        void ResetStandings() { standingsAnswers.Clear(); repsOffByServer = false; }

        void OnRepsOff()
        {
            repsOffByServer = true;
            standingsAnswers.Clear();
            StandingsChanged?.Invoke();
        }

        // The cached answer, or Pending after the question was sent. Never blocks; StandingsChanged fires when an answer arrives.
        public LodgeStandings StandingsFor(int faction)
        {
            var res = new LodgeStandings();
            if (faction < 1 || faction > 1000000) { res.Problem = "No faction."; return res; }
            if (!SupportsReps || repsOffByServer) { res.Problem = SupportsArmory ? "This Lodge does not share reputations." : ArmoryOfflineLine; return res; }
            var off = ArmoryOfflineLine;
            if (off != null) { res.Problem = off; return res; }
            res.Available = true;
            var now = Clock();
            standingsAnswers.TryGetValue(faction, out var a);
            bool fresh = a != null && a.Answered != default(DateTime) && now - a.Answered < StandingsKeep;
            if (a != null && a.Answered != default(DateTime)) res.Rows.AddRange(a.Rows);
            if (!fresh && (a == null || a.Asked == default(DateTime) || now - a.Asked > StandingsRetry))
            {
                if (a == null) standingsAnswers[faction] = a = new StandingsAnswer();
                a.Asked = now;
                _ = Send(new Dictionary<string, object> { ["t"] = LodgeMsg.ArmoryStanding, ["faction"] = faction });
            }
            res.Pending = !fresh && a != null && a.Answered == default(DateTime);
            return res;
        }

        void OnArmoryStanding(Dictionary<string, object> m)
        {
            if (!SupportsReps) return;
            int faction = m.Int("faction");
            if (!standingsAnswers.TryGetValue(faction, out var a)) return;           // only what was asked for
            var rows = new List<LodgeStanding>();
            foreach (var o in m.List("who"))
            {
                if (rows.Count >= 80) break;
                var r = LodgeStanding.From(o as Dictionary<string, object>);
                if (r != null && r.Faction == faction) rows.Add(r);
            }
            a.Rows = rows; a.Answered = Clock();
            StandingsChanged?.Invoke();
        }

        // ---- test hooks
        public void ForgetStandingsForTest() => standingsAnswers.Clear();
        public bool RepsSharedForTest => RepsShared;
    }
}
