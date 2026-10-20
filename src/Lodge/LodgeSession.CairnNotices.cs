using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Elans.Shared;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;

namespace ElansAddonHub.Lodge
{
    // Notices from the Cairn's operator (3.0, Lodge 2.31 feature "cairnnotices"): a security issue, maintenance, news. The Lodge passes on what the
    // Cairn signed - the Guild Master and officers get every notice, other members only critical ones, guests none - as
    // `cairn.notices { list:[{id, created, expires, level, to, text, sig, kid}] }`; the Outpost asks once after the welcome (`cairn.notices.get`).
    // Each one is checked against the Cairn key the Outpost pins (CairnAtlas.PinnedKeyX/Y), so a Lodge can't make one up; anything unsigned, odd
    // or expired is dropped. Cairn 1.6.4 signs v2 notices ("v":2, CairnFormats.NoticeV2) that name the Lodge they are for: "*" or its normalized
    // public address, which must be the address of the Lodge this Outpost is connected to - so a Lodge can't replay a notice the Cairn signed for
    // another one. A v1 notice (no "v") only counts when it is for every Lodge ("*"): its target is the Lodge's id at the Cairn, which can't be
    // tied to this Lodge. A withdrawn notice's signature stays valid until it expires; withdrawing works by the Cairn no longer sending it. Closing the banner remembers the id on this PC and, for an officer, tells the Lodge (`cairn.notice.seen { ids }`), which
    // tells the Cairn once. Self-test section "cairnnotices".
    public partial class LodgeSession
    {
        public sealed class CairnNotice
        {
            public long Id, Created, Expires;
            public string Level, To, Text;
            public int V = 1;      // the signed format (1 = CairnFormats.Notice, 2 = CairnFormats.NoticeV2)
        }

        public bool SupportsCairnNotices { get; private set; }
        public List<CairnNotice> CairnNotices { get; private set; } = new List<CairnNotice>();

        // the one the banner shows: the most urgent, then the newest, that this PC has not closed
        public CairnNotice ShownCairnNotice
        {
            get
            {
                var gone = Settings?.CairnNoticesDismissed ?? new List<long>();
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                return CairnNotices.Where(n => n.Expires > now && !gone.Contains(n.Id))
                    .OrderByDescending(n => LevelRank(n.Level)).ThenByDescending(n => n.Created).FirstOrDefault();
            }
        }
        static int LevelRank(string l) => l == "critical" ? 2 : l == "warning" ? 1 : 0;

        void ReadCairnNoticeFeatures(List<string> features)
        {
            SupportsCairnNotices = features.Contains("cairnnotices");
            CairnNotices = new List<CairnNotice>();
            if (SupportsCairnNotices && !IsGuestNow) _ = Send(LodgeJson.Build(new CairnNoticesGetMsg().Write));
        }

        void ResetCairnNotices() { SupportsCairnNotices = false; CairnNotices = new List<CairnNotice>(); }

        // ids of notices not shown that were logged already (one line per id), bounded
        readonly HashSet<long> noticesLogged = new HashSet<long>();

        // the connected Lodge's address in the form the Cairn signs a v2 target in (PublicUrl.TryNormalize); the saved address when a frame is
        // fed without a socket (self-test). null = none that could be a Cairn Lodge's: then only notices for every Lodge count.
        string NoticeTargetHere()
        {
            var here = NormalizeUrl(BaseUrl ?? Settings?.LodgeUrl);
            return PublicUrl.TryNormalize(here, out var norm, out _) ? norm : null;
        }

        void OnCairnNotices(JsonObject m)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var here = NoticeTargetHere();
            var shown = new List<CairnNotice>();
            foreach (var e in CairnNoticesMsg.Read(new LodgeJsonFields(m)).List)
            {
                var n = ParseCairnNotice(e, here, out var why);
                if (n != null) { if (n.Expires > now) shown.Add(n); continue; }
                if (e.Id > 0 && noticesLogged.Count < 500 && noticesLogged.Add(e.Id)) Util.Log("cairn: notice #" + e.Id + " not shown: " + why);
            }
            CairnNotices = shown.Take(20).ToList();
            // ids the Cairn no longer sends are forgotten, so the setting doesn't grow
            if (Settings?.CairnNoticesDismissed is List<long> gone && gone.RemoveAll(id => CairnNotices.All(n => n.Id != id)) > 0)
                SettingsStore.Save(Settings);
            Changed?.Invoke();
        }

        // null unless every field is sane, the signature matches the pinned Cairn key and the notice is for this Lodge. here = the connected
        // Lodge's normalized address (NoticeTargetHere), null when there is none: then only a notice for every Lodge passes.
        public static CairnNotice ParseCairnNotice(JsonObject o, string here = null) => ParseCairnNotice(CairnNoticeEntry.Read(new LodgeJsonFields(o)), here, out _);

        public static CairnNotice ParseCairnNotice(CairnNoticeEntry o, string here, out string why)
        {
            var n = new CairnNotice { Id = o.Id, Created = o.Created, Expires = o.Expires, Level = o.Level, To = o.To ?? "*", Text = o.Text, V = o.V == 0 ? 1 : o.V };
            why = null;
            if (n.Id <= 0 || n.Created <= 0 || n.Expires <= n.Created || LevelRank(n.Level) == 0 && n.Level != "info"
                || string.IsNullOrEmpty(n.Text) || n.Text.Length > 500) { why = "malformed"; return null; }
            if (n.V != 1 && n.V != 2) { why = "unknown format v" + n.V; return null; }
            if (!NoticeSignatureOk(n, o.Sig, CairnAtlas.PinnedKeyX, CairnAtlas.PinnedKeyY)) { why = "not signed by the Cairn"; return null; }
            if (n.To == "*") return n;
            // a v1 notice for one Lodge names it by its id at the Cairn: nothing here can prove that is this Lodge
            if (n.V == 1) { why = "an old-format notice for one Lodge (it can't be checked to be this one)"; return null; }
            // v2: the target is the Lodge's normalized public address. Both sides normalized (host case, a trailing '/'); the Cairn keeps no two
            // Lodges whose addresses differ only in case, so the comparison ignores case.
            if (here == null || !PublicUrl.TryNormalize(n.To, out var target, out _) || !string.Equals(target, here, StringComparison.OrdinalIgnoreCase))
            { why = "meant for another Lodge"; return null; }
            return n;
        }

        // the signed text and the ES256 check are Elans.Shared (CairnFormats.Notice / NoticeV2, Es256): the same code the Lodge and the Cairn use.
        // n.To is already `to ?? "*"` (ParseCairnNotice), as the Cairn signs it.
        public static string NoticeMessage(CairnNotice n) => n.V == 2
            ? CairnFormats.NoticeV2(n.Id, n.Created, n.Expires, n.Level, n.To, n.Text)
            : CairnFormats.Notice(n.Id, n.Created, n.Expires, n.Level, n.To, n.Text);

        // the pinned key's x / y are base64url, the signature plain base64 (r||s)
        public static bool NoticeSignatureOk(CairnNotice n, string sig, string keyX, string keyY)
        {
            if (n == null || string.IsNullOrEmpty(sig) || string.IsNullOrEmpty(keyX)) return false;
            using (var ec = Es256.FromPoint(B64.FromUrl(keyX), B64.FromUrl(keyY)))
                return Es256.Verify(ec, NoticeMessage(n), B64.FromStd(sig));
        }

        public void DismissCairnNotice()
        {
            var n = ShownCairnNotice;
            if (n == null) return;
            if (Settings.CairnNoticesDismissed == null) Settings.CairnNoticesDismissed = new List<long>();
            Settings.CairnNoticesDismissed.Add(n.Id);
            SettingsStore.Save(Settings);
            if (CanModerate) _ = Send(LodgeJson.Build(new CairnNoticeSeenMsg { Ids = { n.Id } }.Write));
            Changed?.Invoke();
        }

        // self-test: feed a notice list as if the Lodge had sent it
        public void CairnNoticesForTest(JsonObject m) => OnCairnNotices(m);
    }
}
