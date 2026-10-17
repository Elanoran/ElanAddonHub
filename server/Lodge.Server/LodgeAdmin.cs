using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

namespace Lodge;

// read-only numbers for the server's operator (GET /admin/stats; `sudo lodge-admin stats`; an operator's tool through an ssh tunnel).
// The gate: the TCP connection itself comes from loopback (checked before the forwarded headers are applied), carries no proxy header
// (X-Forwarded-For / Forwarded / X-Real-IP - so nothing the reverse proxy forwards ever qualifies, even when the proxy runs on this machine),
// and X-Lodge-Admin equals LODGE_ADMIN_TOKEN (>= 24 characters, compared in constant time; empty = off). Anything else gets 404, like an
// unknown path. The answer has counts only: no names, no codes, no tokens, no IPs, no message text.
public static class LodgeAdminApi
{
    public const int MinTokenLength = 24;
    public const string DirectKey = "lodge.direct", ProxiedKey = "lodge.proxied";

    public static bool HasProxyHeaders(IHeaderDictionary h) => h.ContainsKey("X-Forwarded-For") || h.ContainsKey("Forwarded") || h.ContainsKey("X-Real-IP");

    public static bool Allowed(string token, IPAddress direct, bool proxied, string given)
    {
        if (string.IsNullOrEmpty(token) || token.Length < MinTokenLength) return false;
        if (direct == null || !IPAddress.IsLoopback(direct) || proxied) return false;
        var a = Encoding.UTF8.GetBytes(given ?? "");
        var b = Encoding.UTF8.GetBytes(token);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    static long Unix(DateTime t) => new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc)).ToUnixTimeSeconds();

    public static JsonObject Stats(LodgeHub hub)
    {
        var now = hub.Clock.UtcNow;
        var personal = hub.Auth.ListPersonal();
        var online = hub.Members.ToList();
        var onlineNames = new HashSet<string>(online.Where(m => m.Personal && m.Name != null).Select(m => Names.Normalize(m.Name).ToLowerInvariant()), StringComparer.Ordinal);
        var devices = hub.Auth.ListDevices();
        var active = new HashSet<string>(onlineNames, StringComparer.Ordinal);
        foreach (var d in devices)
            if (!d.Guest && now - d.LastSeen <= TimeSpan.FromDays(7)) active.Add(Names.Normalize(d.Name).ToLowerInvariant());
        // only people who are still members count as active
        var members = new HashSet<string>(personal.Select(p => Names.Normalize(p.Name).ToLowerInvariant()), StringComparer.Ordinal);
        active.IntersectWith(members);

        var roles = new JsonObject();
        foreach (var g in personal.GroupBy(p => p.Role ?? "member").OrderBy(g => g.Key, StringComparer.Ordinal)) roles[g.Key] = g.Count();

        var (channels, messages) = hub.Chat.Counts();
        var rx = hub.CairnIn.Receiver;
        var held = rx.Current;
        var cairn = new JsonObject
        {
            ["enabled"] = hub.Cairn.Enabled,
            ["standing"] = rx.Standing.Length > 0 ? rx.Standing : (hub.Cairn.Enabled ? "unknown" : "off"),
            ["serving"] = rx.Serving,
            ["receiveUntil"] = rx.ReceiveUntil,
            ["dataset"] = held == null ? null : new JsonObject { ["day"] = held.Day, ["seq"] = held.Seq, ["bytes"] = held.Bytes },
            ["lastCheck"] = rx.LastCheck > DateTime.MinValue ? Unix(rx.LastCheck) : null,
            ["problem"] = rx.Problem.Length > 0 ? rx.Problem : null,
            ["sharingOn"] = hub.CairnIn.Sharing.CountOn(),
            ["outposts"] = hub.CairnIn.Weekly.Report(),
        };
        return new JsonObject
        {
            ["server"] = LodgeHub.Version,
            ["now"] = Unix(now),
            ["startedAt"] = Unix(hub.StartedAt),
            ["uptimeSeconds"] = (long)Math.Max(0, (now - hub.StartedAt).TotalSeconds),
            ["members"] = new JsonObject
            {
                ["total"] = personal.Count, ["roles"] = roles, ["online"] = onlineNames.Count, ["guestsOnline"] = online.Count(m => !m.Personal),
                ["connections"] = online.Count, ["active7d"] = active.Count, ["maxUsers"] = hub.Cfg.MaxUsers,
            },
            ["devices"] = devices.Count,
            ["invitesPending"] = hub.Auth.ListInvites().Count(i => i.Pending(now) && i.Kind != Auth.ClaimKind),
            ["messages"] = new JsonObject { ["channels"] = channels, ["kept"] = messages },
            ["files"] = new JsonObject
            {
                ["uploads"] = hub.Files.FileCount, ["uploadBytes"] = hub.Files.UsedBytes,
                ["slots"] = hub.SharedFiles.SlotCount, ["slotBytes"] = hub.SharedFiles.UsedBytes,
            },
            ["features"] = new JsonArray(hub.FeatureList().Select(f => (JsonNode)f).ToArray()),
            ["cairn"] = cairn,
        };
    }
}
