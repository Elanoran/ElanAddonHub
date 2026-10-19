using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ElansAddonHub.Services;
using Elans.Shared;

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
        public bool MissingAddress;   // an elanshub:// link whose server part is empty, a placeholder like <your-lodge-address>, or not a real host
        public bool Insecure;         // the address is plain http:// to a host that is not this PC: refused (Url is kept so the message can name it)
    }

    // A sign-in that has not been proven yet. It lives in memory only; the session writes it to the settings when the server's welcome arrives,
    // and drops it when the connect fails. The stored sign-in is untouched until then.
    public sealed class PendingCredential
    {
        public string Url, Secret, DeviceName;
        public bool IsToken;
        public bool UrlOnly;          // only the address is new: the stored credential stays as it is
        public string Name;           // the lodge name to keep once accepted (from an invite exchange)
        public Action OnCommitted;
    }

    public static class InviteLinks
    {
        public const string Prefix = "i1.";
        public const string LinkStart = "elanshub://lodge/join";
        static readonly Regex SecretChars = new Regex(@"^[A-Za-z0-9._-]+", RegexOptions.CultureInvariant);

        // a one-time invite: "i1." and at least 22 URL-safe characters (the server makes 32: 192 random bits)
        public static bool LooksLikeInvite(string s) =>
            s != null && s.StartsWith(Prefix, StringComparison.Ordinal) && s.Length >= Prefix.Length + 16 && Regex.IsMatch(s, @"^[A-Za-z0-9._-]+$");

        public const string InsecureText = "This lodge address starts with http://, so your invite and sign-in would travel across the internet unencrypted. The Outpost refuses that. Ask the Guild Master for the https:// address (http is only accepted for localhost / 127.0.0.1, for testing).";

        // null when the address may be used; the sentence for the person when it is plain http:// to anything but this PC. An address with no scheme means https.
        public static string InsecureReason(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            var t = url.Trim();
            if (!t.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return null;
            return Uri.TryCreate(t, UriKind.Absolute, out var u) && u.IsLoopback ? null : InsecureText;
        }

        // a real lodge address: no template brackets, a host that can exist (a dot, localhost, an IP, or an explicit port)
        public static bool ValidAddress(string server)
        {
            if (string.IsNullOrWhiteSpace(server) || server.IndexOfAny(new[] { '<', '>', '{', '}', ' ', '"', (char)39 }) >= 0) return false;
            var norm = LodgeSession.NormalizeUrl(server);
            if (!Uri.TryCreate(norm, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https") || string.IsNullOrEmpty(u.Host)) return false;
            if (Uri.CheckHostName(u.Host) == UriHostNameType.Unknown) return false;
            return u.Host.Contains(".") || u.HostNameType == UriHostNameType.IPv4 || u.HostNameType == UriHostNameType.IPv6
                || u.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || !u.IsDefaultPort;
        }

        // a whole link pasted where a code belongs
        public static bool LooksLikeLink(string s) =>
            s != null && (s.IndexOf("elanshub:", StringComparison.OrdinalIgnoreCase) >= 0 || s.IndexOf("#invite=", StringComparison.OrdinalIgnoreCase) >= 0
                          || s.IndexOf("#join=", StringComparison.OrdinalIgnoreCase) >= 0);

        public static string Make(string baseUrl, string invite)
        {
            return LinkStart + "#" + ServerPart(baseUrl) + "|" + invite;
        }

        // "host/lodge" (https is implied), or "http://host:5280/lodge" for a lodge without TLS
        static string ServerPart(string baseUrl)
        {
            var server = (baseUrl ?? "").Trim().TrimEnd('/');
            if (server.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) server = server.Substring(8);
            return server;
        }

        // The same invite as a link that works in a browser and in any chat app:  https://host/lodge/#join=host/lodge|<invite>
        // The server's page at GET / reads the part after # in the browser only (it never reaches the server) and offers an
        // "Open in the Outpost" button that builds the elanshub:// link from it.
        public static string MakeWeb(string baseUrl, string invite)
        {
            var server = ServerPart(baseUrl);
            var page = server.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? server : "https://" + server;
            return page + "/#join=" + server + "|" + invite;
        }

        // the part after "join=" of a web link, or the part after "#" of an elanshub:// link: "server|invite" (| may arrive as %7C)
        static void ReadJoin(string value, JoinLink r)
        {
            value = value.Replace("%7C", "|").Replace("%7c", "|");
            var bar = value.IndexOf('|');
            string server = null;
            if (bar >= 0)
            {
                server = value.Substring(0, bar).Trim();
                var m = SecretChars.Match(value.Substring(bar + 1).Trim());
                if (m.Success) r.Secret = m.Value;
            }
            else if (value.Length > 0) server = value.Trim();   // elanshub://lodge/join#server only
            if (!string.IsNullOrEmpty(server) && ValidAddress(server)) r.Url = LodgeSession.NormalizeUrl(server);
            else r.MissingAddress = true;                      // never guess: a placeholder is not an address
        }

        // https://host/lodge/#join=host/lodge|invite (the web form of a link)
        static readonly Regex WebJoin = new Regex(@"(?:^|&)join=([^&\s]+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

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
                ReadJoin(frag, r);
            }
            else if (WebJoin.Match(frag) is Match web && web.Success)
            {
                // the web form: only the fragment is read; the page address and any query are ignored (the join= value names the lodge)
                string v;
                try { v = Uri.UnescapeDataString(web.Groups[1].Value); } catch (UriFormatException) { v = web.Groups[1].Value; }
                ReadJoin(v, r);
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
            r.Insecure = InsecureReason(r.Url) != null;
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
        public static async Task<ExchangeResult> Exchange(string baseUrl, string invite, string oldCode, string deviceName, string deviceId, string name = null)
        {
            var res = new ExchangeResult();
            var insecure = InviteLinks.InsecureReason(baseUrl);
            if (insecure != null) { res.Status = 0; res.Message = insecure; return res; }   // nothing is sent: the invite never goes over plain http to a host that is not this PC
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + "/exchange"))
                {
                    if (!string.IsNullOrEmpty(invite)) req.Headers.Add("X-Lodge-Invite", invite);
                    else req.Headers.Add("X-Lodge-Code", oldCode ?? "");
                    var body0 = new Dictionary<string, object> { ["device"] = deviceName ?? "", ["deviceId"] = deviceId ?? "" };
                    if (!string.IsNullOrWhiteSpace(name)) body0["name"] = name.Trim();   // only an owner link uses it: the name the new Guild Master wants
                    req.Content = new StringContent(Json.Write(body0), Encoding.UTF8, "application/json");
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

    // Who may be offered which rank in the invite sheet and on the Guild Master page: the Guild Master any rank, officers only ranks below their own
    // (the server enforces the same rule). An old server without invite links makes permanent codes, which any manager may give at any rank.
    public static class InviteRanks
    {
        public static readonly string[] All = { "owner", "officer", "veteran", "member" };

        public static List<string> Offered(string myRole, bool serverHasLinks) =>
            myRole == "owner" || !serverHasLinks ? All.ToList() : All.Where(r => MemberVM.RoleLevel(r) < MemberVM.RoleLevel(myRole)).ToList();

        // may I send this member a new device link? (officers: only people below them)
        public static bool CanSendDeviceLink(string myRole, bool canInvite, bool serverHasLinks, string theirRole, bool guest) =>
            canInvite && serverHasLinks && !guest && (myRole == "owner" || MemberVM.RoleLevel(theirRole) < MemberVM.RoleLevel(myRole));
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

        // admin.invites' rows and admin.invite.created (typed: shared/Elans.Shared/Lodge/AdminMessages.cs InviteView)
        public static InviteVM From(InviteView v, long now) => new InviteVM
        {
            Id = v.Id, Kind = v.Kind, Name = v.Name, Role = v.Role, By = v.By,
            Created = v.Created, Expires = v.Expires, Max = Math.Max(1, v.Max), Uses = v.Uses,
            Used = v.Used, Expired = v.Expired, Revoked = v.Revoked, Pending = v.Pending, Now = now,
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
