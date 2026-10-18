using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;

namespace ElansAddonHub.Lodge
{
    // Notices from the Cairn's operator (3.0, Lodge 2.31 feature "cairnnotices"): a security issue, maintenance, news. The Lodge passes on what the
    // Cairn signed - the Guild Master and officers get every notice, other members only critical ones, guests none - as
    // `cairn.notices { list:[{id, created, expires, level, to, text, sig, kid}] }`; the Outpost asks once after the welcome (`cairn.notices.get`).
    // Each one is checked against the Cairn key the Outpost pins (CairnAtlas.PinnedKeyX/Y), so a Lodge can't make one up; anything unsigned, odd
    // or expired is dropped. Closing the banner remembers the id on this PC and, for an officer, tells the Lodge (`cairn.notice.seen { ids }`), which
    // tells the Cairn once. Self-test section "cairnnotices".
    public partial class LodgeSession
    {
        public sealed class CairnNotice
        {
            public long Id, Created, Expires;
            public string Level, To, Text;
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
            if (SupportsCairnNotices && !IsGuestNow) _ = Send(new Dictionary<string, object> { ["t"] = "cairn.notices.get" });
        }

        void ResetCairnNotices() { SupportsCairnNotices = false; CairnNotices = new List<CairnNotice>(); }

        void OnCairnNotices(Dictionary<string, object> m)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            CairnNotices = m.List("list").OfType<Dictionary<string, object>>().Select(ParseCairnNotice)
                .Where(n => n != null && n.Expires > now).Take(20).ToList();
            // ids the Cairn no longer sends are forgotten, so the setting doesn't grow
            if (Settings?.CairnNoticesDismissed is List<long> gone && gone.RemoveAll(id => CairnNotices.All(n => n.Id != id)) > 0)
                SettingsStore.Save(Settings);
            Changed?.Invoke();
        }

        // null unless every field is sane and the signature matches the pinned Cairn key
        public static CairnNotice ParseCairnNotice(Dictionary<string, object> o)
        {
            var n = new CairnNotice { Id = o.Long("id"), Created = o.Long("created"), Expires = o.Long("expires"), Level = o.Str("level"), To = o.Str("to") ?? "*", Text = o.Str("text") };
            if (n.Id <= 0 || n.Created <= 0 || n.Expires <= n.Created || LevelRank(n.Level) == 0 && n.Level != "info"
                || string.IsNullOrEmpty(n.Text) || n.Text.Length > 500) return null;
            return NoticeSignatureOk(n, o.Str("sig"), CairnAtlas.PinnedKeyX, CairnAtlas.PinnedKeyY) ? n : null;
        }

        public static string NoticeMessage(CairnNotice n) =>
            "elans-cairn-notice/1\n" + n.Id.ToString(CultureInfo.InvariantCulture) + "\n" + n.Created.ToString(CultureInfo.InvariantCulture) + "\n"
            + n.Expires.ToString(CultureInfo.InvariantCulture) + "\n" + n.Level + "\n" + n.To + "\n" + n.Text;

        public static bool NoticeSignatureOk(CairnNotice n, string sig, string keyX, string keyY)
        {
            try
            {
                if (string.IsNullOrEmpty(sig) || string.IsNullOrEmpty(keyX)) return false;
                var p = new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = B64Url(keyX), Y = B64Url(keyY) } };
                using (var ec = ECDsa.Create(p))
                    return ec.VerifyData(Encoding.UTF8.GetBytes(NoticeMessage(n)), Convert.FromBase64String(sig), HashAlgorithmName.SHA256);
            }
            catch { return false; }
        }
        static byte[] B64Url(string s)
        {
            s = (s ?? "").Replace('-', '+').Replace('_', '/');
            return Convert.FromBase64String(s + new string('=', (4 - s.Length % 4) % 4));
        }

        public void DismissCairnNotice()
        {
            var n = ShownCairnNotice;
            if (n == null) return;
            if (Settings.CairnNoticesDismissed == null) Settings.CairnNoticesDismissed = new List<long>();
            Settings.CairnNoticesDismissed.Add(n.Id);
            SettingsStore.Save(Settings);
            if (CanModerate) _ = Send(new Dictionary<string, object> { ["t"] = "cairn.notice.seen", ["ids"] = new List<object> { n.Id } });
            Changed?.Invoke();
        }

        // self-test: feed a notice list as if the Lodge had sent it
        public void CairnNoticesForTest(Dictionary<string, object> m) => OnCairnNotices(m);
    }
}
