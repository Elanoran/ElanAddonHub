using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;
using Elans.Shared;

namespace ElansAddonHub.Lodge
{
    // Give to get (2.87, Lodge feature "cairn2"): the Cairn's data comes only from the user's own Lodge, and only while the user's Atlas sharing is on.
    // After the welcome (and whenever the sharing switch changes) the Outpost tells the Lodge `atlas.sharing { on }`; the Lodge answers - and later
    // announces - `cairn.status { entitled, why?, text?, day?, seq? }`. The bytes come over HTTP (LodgeClient.GetCairn), checked in CairnData.cs.
    // A Lodge without "cairn2" (older, or not contributing) and guests: nothing is asked, nothing is fetched. Self-test section "cairn".
    public partial class LodgeSession
    {
        public bool SupportsCairnData { get; private set; }               // the Lodge hands the Cairn's data to members who share (feature "cairn2")
        public bool CairnEntitled { get; private set; }                   // the Lodge's last cairn.status said: yes, for you
        public string CairnWhy { get; private set; } = "";                // ... or why not: share, guest, lodge, disabled, not-yet
        public long CairnSeq { get; private set; }                        // the dataset the Lodge holds (when entitled)
        public event Action CairnStatusChanged;                           // a cairn.status arrived, or the welcome changed what this Lodge can do
        LodgeClient cairnClientOverride;                                  // the self-test points this at a fake Lodge

        bool CairnSharingOn => Settings != null && !Settings.AtlasShareOff;

        void ReadCairnFeatures(List<string> features)
        {
            SupportsCairnData = SupportsAtlas && features.Contains("cairn2");
            CairnEntitled = false; CairnSeq = 0;
            CairnWhy = !SupportsCairnData ? "none" : IsGuestNow ? "guest" : "";
            if (SupportsCairnData && !IsGuestNow) SendCairnSharing();
            ReadCairnJoinFeatures(features);              // 2.90: the Guild Master's "Join the Cairn" (LodgeSession.CairnJoin.cs)
            ReadCairnNoticeFeatures(features);            // 3.0: the Cairn operator's signed notices (LodgeSession.CairnNotices.cs)
            CairnStatusChanged?.Invoke();
        }

        void ResetCairn()
        {
            SupportsCairnData = false; CairnEntitled = false; CairnSeq = 0; CairnWhy = "";
            ResetCairnJoin();
            ResetCairnNotices();
        }

        // the Lodge can't see the switch otherwise (sharing off simply means nothing is uploaded): say it, so it knows whom the data is for
        void SendCairnSharing()
        {
            if (!SupportsCairnData || IsGuestNow) return;
            _ = Send(new Dictionary<string, object> { ["t"] = LodgeMsg.AtlasSharing, ["on"] = CairnSharingOn });
        }

        void OnCairnStatus(Dictionary<string, object> m)
        {
            if (!SupportsCairnData) return;
            CairnEntitled = m.Bool("entitled");
            CairnWhy = CairnEntitled ? "" : (m.Str("why") ?? "");
            CairnSeq = CairnEntitled ? m.Long("seq") : 0;
            CairnStatusChanged?.Invoke();
        }

        // switching sharing off deletes the copy at once (the Lodge is told too, and would refuse the next fetch anyway)
        void CairnSharingChanged()
        {
            if (!CairnSharingOn) CairnAtlas.Clear("share");
            SendCairnSharing();
            CairnStatusChanged?.Invoke();                 // the sharing card and Privacy follow the switch at once
        }

        LodgeClient CairnHttp => cairnClientOverride ?? client;
        public void CairnClientForTest(LodgeClient c) { cairnClientOverride = c; }

        // CairnAtlas.Fetch: only to a Lodge that hands it out, only for a member who shares
        public Task<CairnAtlas.Response> CairnFetchAsync(string path, string etag)
        {
            var http = CairnHttp;
            if (!SupportsCairnData || IsGuestNow || !CairnSharingOn || http == null || (!Online && cairnClientOverride == null))
                return Task.FromResult(new CairnAtlas.Response { Status = 0, Reason = !SupportsCairnData ? "disabled" : IsGuestNow ? "guest" : !CairnSharingOn ? "share" : "offline" });
            return http.GetCairn(path, etag);
        }
    }
}
