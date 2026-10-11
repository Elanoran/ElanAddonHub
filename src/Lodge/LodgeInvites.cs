using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ElansAddonHub.Services;

namespace ElansAddonHub.Lodge
{
    // One-time invite links and per-device tokens (Lodge server 2.10; protocol in server/PROTOCOL.md).
    //   elanshub://lodge/join#<server>|<invite>      the secret lives in the #fragment only - never in a path or query
    //   https://site/lodge#invite=<code or invite>   the older link format, still understood
    // The pure parts (parsing, wording, the upgrade rule, the stored credential) live here so the self-test can drive them without a window.

    public sealed class JoinLink
    {
        public string Url;            // the lodge address (https://site/lodge), null when the text holds none
        public string Secret;         // an invite ("i1....") or an old permanent code, null when the text holds none
        public bool IsInvite;         // Secret is a one-time invite (to be exchanged for a device token)
        public bool IsOldCode => Secret != null && !IsInvite;
    }

    public static class InviteLinks
    {
        public const string Prefix = "i1.";
        public const string LinkStart = "elanshub://lodge/join";
        static readonly Regex SecretChars = new Regex(@"^[A-Za-z0-9._-]+", RegexOptions.CultureInvariant);

        // a one-time invite: "i1." and at least 22 URL-safe characters (the server makes 32: 192 random bits)
        public static bool LooksLikeInvite(string s) =>
            s != null && s.StartsWith(Prefix, StringComparison.Ordinal) && s.Length >= Prefix.Length + 16 && Regex.IsMatch(s, @"^[A-Za-z0-9._-]+$");

        public static string Make(string baseUrl, string invite)
        {
            var server = (baseUrl ?? "").Trim().TrimEnd('/');
            if (server.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) server = server.Substring(8);
            return LinkStart + "#" + server + "|" + invite;
        }

        // Accepts: elanshub://lodge/join#server|invite (| may arrive as %7C), https://site/lodge#invite=SECRET, a bare address.
        // A secret is only ever read from the #fragment: "?invite=" / "?code=" in a query are ignored, so a link that sends one to a
        // server by mistake (or on purpose) is not honoured.
        public static JoinLink Parse(string text)
        {
            var r = new JoinLink();
            text = (text ?? "").Trim().Trim('"', '\'', '<', '>', ' ');
            if (text.Length == 0) return r;
            var at = text.IndexOf("elanshub:", StringComparison.OrdinalIgnoreCase);   // pasted inside a sentence: start at the link
            if (at > 0) text = text.Substring(at);
            var hash = text.IndexOf('#');
            var before = hash >= 0 ? text.Substring(0, hash) : text;
            var frag = hash >= 0 ? text.Substring(hash + 1) : "";
            if (before.StartsWith("elanshub:", StringComparison.OrdinalIgnoreCase))
            {
                frag = frag.Replace("%7C", "|").Replace("%7c", "|");
                var bar = frag.IndexOf('|');
                if (bar >= 0)
                {
                    var server = frag.Substring(0, bar).Trim();
                    var m = SecretChars.Match(frag.Substring(bar + 1).Trim());
                    if (server.Length > 0) r.Url = LodgeSession.NormalizeUrl(server);
                    if (m.Success) r.Secret = m.Value;
                }
                else if (frag.Length > 0) r.Url = LodgeSession.NormalizeUrl(frag);   // elanshub://lodge/join#server only
            }
            else
            {
                var q = before.IndexOf('?');
                if (q >= 0) before = before.Substring(0, q);                           // whatever a query holds is dropped
                var m = Regex.Match(frag, @"(?:^|&)invite=([A-Za-z0-9._-]+)");
                if (m.Success) r.Secret = m.Groups[1].Value;
                r.Url = LodgeSession.NormalizeUrl(before);
            }
            r.IsInvite = LooksLikeInvite(r.Secret);
            return r;
        }
    }

    public sealed class ExchangeResult
    {
        public int Status;                       // 200 ok, 0 = could not reach the server
        public bool Ok => Status == 200;
        public string Token, Name, Role, DeviceId, DeviceName, ServerError, Reason;
        public bool Guest;
        public string Message;                   // a calm sentence for the person, set when !Ok
    }

    public static class InviteExchange
    {
        // test seam: the self-test answers from a canned server
        public static Func<HttpRequestMessage, Task<HttpResponseMessage>> Send = req => Net.Http.SendAsync(req);

        // POST {base}/exchange. The invite (or, to upgrade a device, the old code) goes in a header, the device in the body - never in the URL.
        public static async Task<ExchangeResult> Exchange(string baseUrl, string invite, string oldCode, string deviceName, string deviceId)
        {
            var res = new ExchangeResult();
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + "/exchange"))
                {
                    if (!string.IsNullOrEmpty(invite)) req.Headers.Add("X-Lodge-Invite", invite);
                    else req.Headers.Add("X-Lodge-Code", oldCode ?? "");
                    req.Content = new StringContent(Json.Write(new Dictionary<string, object> { ["device"] = deviceName ?? "", ["deviceId"] = deviceId ?? "" }), Encoding.UTF8, "application/json");
                    using (var resp = await Send(req))
                    {
                        res.Status = (int)resp.StatusCode;
                        var text = await resp.Content.ReadAsStringAsync();
                        Dictionary<string, object> body = null;
                        try { body = Json.Obj(text); } catch { }          // a proxy's 404/502 page is not JSON
                        res.ServerError = body.Str("error"); res.Reason = body.Str("reason");
                        if (res.Ok && body != null)
                        {
                            res.Token = body.Str("token"); res.Name = body.Str("name"); res.Role = body.Str("role");
                            res.DeviceId = body.Str("deviceId"); res.DeviceName = body.Str("device"); res.Guest = body.Bool("guest");
                            if (string.IsNullOrEmpty(res.Token)) { res.Status = 502; }
                        }
                    }
                }
            }
            catch (Exception e) { res.Status = 0; Util.Log("lodge exchange: " + e.GetBaseException().Message); }
            if (!res.Ok) res.Message = Friendly(res.Status, res.Reason, res.ServerError, string.IsNullOrEmpty(invite));
            return res;
        }

        // the sentence shown when the exchange did not work
        public static string Friendly(int status, string reason, string serverError, bool codeUpgrade = false)
        {
            switch (status)
            {
                case 0: return "Can't reach the lodge - check the address and your connection";
                case 410: return "This invite has been used or expired - ask for a new one";
                case 401: return codeUpgrade ? "That invite code isn't valid any more" : "This invite link isn't valid - check that you pasted all of it";
                case 429: return "Too many wrong tries from your address - wait 15 minutes and try again";
                case 404: case 405: case 501:
                    return "This lodge runs an older server that can't take invite links yet - ask the Guild Master to run: " + Brand.ServerUpdateCommand;
                case 409: case 403: return string.IsNullOrEmpty(serverError) ? "The lodge couldn't add this device" : serverError;
                default: return string.IsNullOrEmpty(serverError) ? "The lodge answered with an error (" + status + ") - try again in a moment" : serverError;
            }
        }
    }

    // The sign-in this PC keeps for one lodge. A device token (DPAPI-protected) replaces the old permanent code.
    public static class LodgeCredentials
    {
        public static string DeviceId(Settings s)
        {
            if (string.IsNullOrEmpty(s.LodgeDeviceId)) { s.LodgeDeviceId = Guid.NewGuid().ToString("N"); }
            return s.LodgeDeviceId;
        }

        public static string DeviceName(Settings s) =>
            string.IsNullOrWhiteSpace(s.LodgeDeviceName) ? SafeMachineName() : s.LodgeDeviceName.Trim();

        static string SafeMachineName() { try { return Environment.MachineName; } catch { return "This PC"; } }

        // the token wins; otherwise the old code (older server, or not upgraded yet)
        public static bool Load(Settings s, out string credential, out bool isToken)
        {
            var token = SettingsStore.Unprotect(s.LodgeTokenProtected);
            if (!string.IsNullOrEmpty(token)) { credential = token; isToken = true; return true; }
            var code = SettingsStore.Unprotect(s.LodgeCodeProtected);
            credential = code; isToken = false;
            return !string.IsNullOrEmpty(code);
        }

        public static void StoreToken(Settings s, string url, string token, string deviceName)
        {
            s.LodgeUrl = url;
            s.LodgeTokenProtected = SettingsStore.Protect(token);
            s.LodgeCodeProtected = null;                    // the permanent code is no longer kept on this PC
            if (!string.IsNullOrWhiteSpace(deviceName)) s.LodgeDeviceName = deviceName.Trim();
            s.LodgeUpgradeDeclined = null;
        }

        public static void StoreCode(Settings s, string url, string code)
        {
            s.LodgeUrl = url;
            s.LodgeCodeProtected = SettingsStore.Protect(code);
            s.LodgeTokenProtected = null;
        }

        public static void Clear(Settings s) { s.LodgeCodeProtected = null; s.LodgeTokenProtected = null; }

        public static bool HasToken(Settings s) => !string.IsNullOrEmpty(s.LodgeTokenProtected);
        public static bool HasCode(Settings s) => !string.IsNullOrEmpty(s.LodgeCodeProtected);
    }

    // "Upgrade this device": offered quietly, once, to someone who still signs in with a permanent code on a server that can do tokens
    public static class UpgradePolicy
    {
        public static bool ShouldOffer(bool haveToken, bool haveCode, bool serverTokens, bool guest, string declinedUrl, string url) =>
            !haveToken && haveCode && serverTokens && !guest && (string.IsNullOrEmpty(declinedUrl) || !string.Equals(declinedUrl, url ?? "local", StringComparison.OrdinalIgnoreCase));
    }

    // ---- what the panels show (built from the server's frames)

    public static class AgoText
    {
        public static string In(long targetMs, long nowMs)
        {
            var span = TimeSpan.FromMilliseconds(targetMs - nowMs);
            if (span <= TimeSpan.Zero) return "expired";
            if (span.TotalDays >= 2) return "in " + (int)span.TotalDays + " days";
            if (span.TotalHours >= 2) return "in " + (int)span.TotalHours + " hours";
            if (span.TotalHours >= 1) return "in 1 hour";
            return "in " + Math.Max(1, (int)span.TotalMinutes) + " min";
        }

        public static string Ago(long thenMs, long nowMs)
        {
            var span = TimeSpan.FromMilliseconds(nowMs - thenMs);
            if (span < TimeSpan.FromMinutes(2)) return "just now";
            if (span.TotalHours < 1) return (int)span.TotalMinutes + " min ago";
            if (span.TotalHours < 2) return "1 hour ago";
            if (span.TotalDays < 2) return (int)span.TotalHours + " hours ago";
            return (int)span.TotalDays + " days ago";
        }
    }

    public sealed class InviteVM
    {
        public string Id, Kind, Name, Role, By;
        public long Created, Expires; public int Max, Uses;
        public bool Used, Expired, Revoked, Pending;
        public long Now;
        public bool IsGuest => Kind == "guest";
        public string Title => IsGuest ? (Max > 1 ? "Guest link (" + Max + " uses)" : "Guest link") : Name;
        public string RoleText => IsGuest ? "Guest" : MemberVM.RoleName(Role);
        public string State => Revoked ? "Revoked" : Pending ? "Pending" : Uses >= Max ? "Used" : "Expired";
        public string Detail => Revoked ? "Revoked" + (By != null ? " · made by " + By : "")
            : Pending ? "Made by " + By + " · expires " + AgoText.In(Expires, Now) + (Max > 1 ? " · used " + Uses + " of " + Max : "")
            : Uses >= Max ? "Used" + (Max > 1 ? " " + Uses + " of " + Max : "") + " · made by " + By
            : "Expired · made by " + By;
        public bool CanRevoke => Pending;
        public double RowOpacity => Pending ? 1.0 : 0.6;

        public static InviteVM Parse(Dictionary<string, object> d, long now) => new InviteVM
        {
            Id = d.Str("id"), Kind = d.Str("kind"), Name = d.Str("name"), Role = d.Str("role"), By = d.Str("by"),
            Created = d.Long("created"), Expires = d.Long("expires"), Max = Math.Max(1, d.Int("max")), Uses = d.Int("uses"),
            Used = d.Bool("used"), Expired = d.Bool("expired"), Revoked = d.Bool("revoked"), Pending = d.Bool("pending"), Now = now,
        };
    }

    public sealed class DeviceVM
    {
        public string Id, Owner, DeviceName, Ip, Role;
        public long Created, LastSeen, Now;
        public bool Current, Online;
        public string Title => string.IsNullOrEmpty(DeviceName) ? "Unknown device" : DeviceName;
        public string Detail => (Online ? "Online now" : "Last seen " + AgoText.Ago(LastSeen, Now)) + (string.IsNullOrEmpty(Ip) ? "" : " · " + Ip);
        public string Who => string.IsNullOrEmpty(Owner) ? "Guest" : Owner;

        public static DeviceVM Parse(Dictionary<string, object> d, long now) => new DeviceVM
        {
            Id = d.Str("id"), Owner = d.Str("name"), DeviceName = d.Str("device"), Ip = d.Str("ip"), Role = d.Str("role"),
            Created = d.Long("created"), LastSeen = d.Long("lastSeen"), Current = d.Bool("current"), Online = d.Bool("online"), Now = now,
        };
    }
}
