using System;
using System.Collections.Generic;
using System.Globalization;
using ElansAddonHub.Services;
using Elans.Shared;

namespace ElansAddonHub.Lodge
{
    // "Join the Cairn" (2.90, Lodge 2.28 feature "cairnjoin"): the Guild Master switches the Lodge's contribution to the Cairn on from here - no keys
    // to hand around, no server settings. The Lodge makes its own key pair, asks the Cairn, answers its check, waits or is admitted, then
    // contributes and receives. Frames: cairn.join.get -> cairn.join (officers and the Guild Master), admin.cairn {on, publicUrl, code?} (the Guild
    // Master). The status line is CairnJoinText. An older Lodge (no "cairnjoin") shows nothing. Self-test section "cairnjoin".
    public sealed class CairnJoinStatus
    {
        public bool On, Env, Atlas = true, HasCode, Settled, Contributing, Signed, Serving;
        public string State = "", Reason = "", Error = "", Mode = "", EstDay = "", PublicUrl = "", SuggestedUrl = "", CairnHost = "", Standing = "";
        public int Place, Of; public int? BetaOpenSlots;
        public long? ProbationUntil, ReceiveUntil, LastPost, Admitted;

        static long? L(Dictionary<string, object> m, string k) => m.ContainsKey(k) && m[k] != null ? m.Long(k) : (long?)null;

        public static CairnJoinStatus Parse(Dictionary<string, object> m) => new CairnJoinStatus
        {
            On = m.Bool("on"), Env = m.Bool("env"), Atlas = !m.ContainsKey("atlas") || m.Bool("atlas"), HasCode = m.Bool("hasCode"), Settled = m.Bool("settled"),
            Contributing = m.Bool("contributing"), Signed = m.Bool("signed"), Serving = m.Bool("serving"),
            State = m.Str("state") ?? "", Reason = m.Str("reason") ?? "", Error = m.Str("error") ?? "", Mode = m.Str("mode") ?? "", EstDay = m.Str("estDay") ?? "",
            PublicUrl = m.Str("publicUrl") ?? "", SuggestedUrl = m.Str("suggestedUrl") ?? "", CairnHost = m.Str("cairnHost") ?? "", Standing = m.Str("standing") ?? "",
            Place = m.Int("place"), Of = m.Int("of"), BetaOpenSlots = m.ContainsKey("betaOpenSlots") && m["betaOpenSlots"] != null ? m.Int("betaOpenSlots") : (int?)null,
            ProbationUntil = L(m, "probationUntil"), ReceiveUntil = L(m, "receiveUntil"), LastPost = L(m, "lastPost"), Admitted = L(m, "admitted"),
        };

        // the invite code field: in the beta, or when the Cairn just said something about a code
        public bool WantsCode => !Env && State != "admitted" && (Mode == "beta" || Reason == "invite-needed" || Reason.StartsWith("code-", StringComparison.Ordinal) || Reason == "beta-full");
    }

    public static class CairnJoinText
    {
        static string Day(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime().ToString("d MMMM", CultureInfo.InvariantCulture);
        static string Ago(long unix, DateTime nowUtc)
        {
            var d = nowUtc - DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
            if (d.TotalMinutes < 2) return "just now";
            if (d.TotalHours < 2) return (int)d.TotalMinutes + " minutes ago";
            if (d.TotalDays < 2) return (int)d.TotalHours + " hours ago";
            return (int)d.TotalDays + " days ago";
        }

        // one calm line for the Guild Master panel
        public static string Status(CairnJoinStatus s, DateTime nowUtc)
        {
            if (s == null) return "";
            if (!s.Atlas) return "The Atlas is switched off on this Lodge server, so there is nothing to share with the Cairn.";
            if (s.Env) return "On through the Lodge server's own settings (lodge.env)." + Standing(s, nowUtc);
            if (!s.On) return s.State == "admitted" ? "Off. Your Lodge is admitted and can switch back on any time." : "Off. Your Lodge doesn't share anything with the Cairn.";
            switch (s.Reason)
            {
                case "invite-needed": return "The Cairn is in its beta: joining needs an invite code from the Cairn's operator. Enter it below.";
                case "beta-full": return "The Cairn's beta is full right now. Your Lodge keeps the code and tries again later.";
                case "code-invalid": return "That invite code is not valid. Check it, or ask the Cairn's operator for one.";
                case "code-expired": return "That invite code has expired. Ask the Cairn's operator for a new one.";
                case "code-used": return "That invite code was already used. Ask the Cairn's operator for a new one.";
                case "code-revoked": return "That invite code was withdrawn. Ask the Cairn's operator for a new one.";
                case "code-blocked": return "Too many wrong invite codes were tried. Your Lodge tries again in an hour.";
                case "unverified": return "The Cairn could not check your Lodge at its public address. " + (s.Error.Length > 0 ? s.Error + "." : "") + " Your Lodge tries again soon.";
                case "duplicate-url": return "Another Lodge already asked the Cairn with this address.";
                case "address": return "Enter your Lodge's public address (the one members connect to).";
                case "network": return "The Cairn could not be reached. Your Lodge tries again soon.";
                case "waiting-full": return "The Cairn's waiting list is full. Your Lodge tries again in a few hours.";
                case "unsupported": return "The Cairn doesn't take requests to join yet.";
            }
            switch (s.State)
            {
                case "waiting":
                    return "Waiting: place " + s.Place + (s.Of > 0 ? " of " + s.Of : "") + " in the Cairn's list."
                        + (s.EstDay.Length == 10 && DateTime.TryParseExact(s.EstDay, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? " Expected around " + d.ToString("d MMMM", CultureInfo.InvariantCulture) + "." : "");
                case "admitted":
                    return "Admitted." + Standing(s, nowUtc);
                case "refused": return "The Cairn's operator didn't admit this Lodge.";
                case "revoked": return "The Cairn revoked this Lodge. It no longer shares or receives.";
                case "error": /* not-a-lodge-type: a joining state */ return s.Error.Length > 0 ? s.Error + "." : "Something went wrong asking the Cairn. Your Lodge tries again later.";
            }
            return "Asking the Cairn" + (s.CairnHost.Length > 0 ? " (" + s.CairnHost + ")" : "") + "...";
        }

        static string Standing(CairnJoinStatus s, DateTime nowUtc)
        {
            var nowS = new DateTimeOffset(nowUtc).ToUnixTimeSeconds();
            string r;
            if (s.Standing == "lapsed") r = " Lapsed: its last contribution is too old - it receives again with the next one.";
            else if (s.Standing == "good") r = " In good standing" + (s.LastPost is long lp ? ", last post " + Ago(lp, nowUtc) : "") + ".";
            else if (s.Standing == "new" || !s.LastPost.HasValue) r = " The first contribution goes out soon; the Cairn's data comes right after it.";
            else r = "";
            if (!s.Settled && s.ProbationUntil is long pu && pu > nowS) r += " On probation until " + Day(pu) + ": until then its numbers don't count toward the Cairn's agreement rule.";
            else if (!s.Settled && !s.ProbationUntil.HasValue && !s.Env) r += " On probation for its first days of contributing.";
            return r;
        }
    }

    public partial class LodgeSession
    {
        public bool SupportsCairnJoin { get; private set; }               // the Lodge offers "Join the Cairn" (feature "cairnjoin", Lodge 2.28)
        public CairnJoinStatus CairnJoin { get; private set; }            // the last cairn.join (officers and the Guild Master), null = not known yet
        public event Action CairnJoinChanged;
        public bool CanSwitchCairn => SupportsCairnJoin && MyRole == "owner";
        public bool CanSeeCairnJoin => SupportsCairnJoin && (MyRole == "owner" || MyRole == "officer" || CanModerate);

        void ReadCairnJoinFeatures(List<string> features)
        {
            SupportsCairnJoin = features.Contains("cairnjoin");
            CairnJoin = null;
            if (CanSeeCairnJoin) _ = Send(new Dictionary<string, object> { ["t"] = LodgeMsg.CairnJoinGet });
            CairnJoinChanged?.Invoke();
        }

        void ResetCairnJoin() { SupportsCairnJoin = false; CairnJoin = null; }

        void OnCairnJoin(Dictionary<string, object> m)
        {
            if (!SupportsCairnJoin) return;
            CairnJoin = CairnJoinStatus.Parse(m);
            CairnJoinChanged?.Invoke();
        }

        public void RequestCairnJoin() { if (CanSeeCairnJoin) _ = Send(new Dictionary<string, object> { ["t"] = LodgeMsg.CairnJoinGet }); }

        // the Guild Master's switch; publicUrl / code only when given (the Lodge keeps what it has otherwise). The code goes to the Lodge once.
        public void SetCairnJoin(bool on, string publicUrl = null, string code = null)
        {
            if (!CanSwitchCairn) return;
            var o = new Dictionary<string, object> { ["t"] = LodgeMsg.AdminCairn, ["on"] = on };
            if (!string.IsNullOrWhiteSpace(publicUrl)) o["publicUrl"] = publicUrl.Trim();
            if (!string.IsNullOrWhiteSpace(code)) o["code"] = code.Trim();
            _ = Send(o);
        }

        // the address to suggest: the Lodge's own setting (LODGE_PUBLIC_URL), else the one this Outpost is connected to (https only)
        public string CairnSuggestedUrl()
        {
            var s = CairnJoin?.PublicUrl;
            if (!string.IsNullOrEmpty(s)) return s;
            s = CairnJoin?.SuggestedUrl;
            if (!string.IsNullOrEmpty(s)) return s;
            var b = (BaseUrl ?? Settings?.LodgeUrl ?? "").Trim().TrimEnd('/');
            return b.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? b : "";
        }
    }
}
