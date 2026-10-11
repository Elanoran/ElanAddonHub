using System;
using System.Collections.Generic;

namespace ElansAddonHub.Services
{
    // The Browser page's small built-in blocklist of well-known ad and tracker hosts (Settings > Privacy > Outpost browser).
    // Only sub-requests (scripts, images, frames, fetches) to these hosts are refused; a page you open yourself always loads.
    // A host matches when it IS an entry or a real subdomain of one: "ads.doubleclick.net" yes, "notdoubleclick.net" and
    // "doubleclick.net.evil.com" no. Edit the list here, it is the only place.
    public static class AdBlock
    {
        static readonly string[] Hosts =
        {
            // Google advertising / measurement (not Google Tag Manager, Fonts or the APIs: too many pages break without them)
            "doubleclick.net", "googlesyndication.com", "googleadservices.com", "adservice.google.com", "pagead2.googlesyndication.com",
            "google-analytics.com", "analytics.google.com", "2mdn.net", "admob.com",
            // ad exchanges and networks
            "adnxs.com", "adsrvr.org", "amazon-adsystem.com", "advertising.com", "adform.net", "casalemedia.com", "criteo.com", "criteo.net",
            "openx.net", "pubmatic.com", "rubiconproject.com", "smartadserver.com", "sharethrough.com", "bidswitch.net", "contextweb.com",
            "yieldmo.com", "indexww.com", "lijit.com", "media.net", "33across.com", "triplelift.com", "gumgum.com", "adsafeprotected.com",
            "serving-sys.com", "zedo.com", "adition.com", "teads.tv", "mookie1.com", "everesttech.net", "mathtag.com", "tapad.com",
            // content recommendation and pop ads
            "taboola.com", "outbrain.com", "revcontent.com", "mgid.com", "popads.net", "exoclick.com", "propellerads.com", "adsterra.com",
            // gaming-site ad stacks
            "venatusmedia.com", "nitropay.com", "adinplay.com", "playwire.com", "cdn.intergient.com",
            // trackers and session recorders
            "scorecardresearch.com", "quantserve.com", "quantcount.com", "imrworldwide.com", "moatads.com", "krxd.net", "bluekai.com",
            "demdex.net", "rlcdn.com", "agkn.com", "omtrdc.net", "hotjar.com", "mouseflow.com", "fullstory.com", "crazyegg.com",
            "luckyorange.com", "chartbeat.net", "newrelic.com", "branch.io", "adjust.com", "appsflyer.com", "segment.io",
        };

        static readonly HashSet<string> Set = new HashSet<string>(Hosts, StringComparer.Ordinal);

        public static int Count => Set.Count;

        // selftest only: one more host that counts as listed (the engine's own Strict tracking prevention may stop the real ones before this list is asked)
        public static string ExtraHostForTest;

        // host name (any case, optional trailing dot): is it one of the listed hosts or a subdomain of one?
        public static bool IsBlockedHost(string host)
        {
            if (string.IsNullOrEmpty(host)) return false;
            var h = host.Trim().TrimEnd('.').ToLowerInvariant();
            if (h.Length == 0) return false;
            while (true)
            {
                if (Set.Contains(h) || (ExtraHostForTest != null && h == ExtraHostForTest)) return true;
                var dot = h.IndexOf('.');
                if (dot < 0) return false;
                h = h.Substring(dot + 1);
                if (h.IndexOf('.') < 0) return false;   // never compare a bare top-level domain
            }
        }

        public static bool IsBlockedUrl(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps) && IsBlockedHost(u.Host);
    }
}
