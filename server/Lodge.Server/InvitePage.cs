using System.Net;
using System.Security.Cryptography;

namespace Lodge;

// The public page at GET / (what opening a web invite link shows). A web link looks like
//   https://host/lodge/#join=host/lodge|i1.xxxxxxxx
// Everything after # stays in the browser: this page is the same for every visitor, never reads the URL, and its one inline script
// (allowed by a per-request nonce) builds the elanshub:// link from location.hash. No fetch, no analytics, no logging; a strict CSP
// (default-src none) makes any network call from the page impossible. There is no automatic redirect: a person clicks the button.
public static class InvitePage
{
    public const string DownloadUrl = "https://github.com/Elanoran/ElanAddonHub/releases/latest";

    public static string NewNonce() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));

    public static string ContentSecurityPolicy(string nonce) =>
        $"default-src 'none'; script-src 'nonce-{nonce}'; style-src 'nonce-{nonce}'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    // the headers every response of the page carries
    public static IReadOnlyDictionary<string, string> Headers(string nonce) => new Dictionary<string, string>
    {
        ["Content-Security-Policy"] = ContentSecurityPolicy(nonce),
        ["X-Content-Type-Options"] = "nosniff",
        ["Referrer-Policy"] = "no-referrer",
        ["X-Frame-Options"] = "DENY",
        ["Cache-Control"] = "no-store",
    };

    static string H(string s) => WebUtility.HtmlEncode(s ?? "");

    // lodgeName / welcome come from the owner (already validated); they are HTML-encoded here
    public static string Render(string nonce, string lodgeName, string welcome)
    {
        var name = string.IsNullOrWhiteSpace(lodgeName) ? LodgeInfoStore.DefaultName : lodgeName;
        var welcomeHtml = string.IsNullOrWhiteSpace(welcome) ? "" : "<p class=\"welcome\">" + H(welcome).Replace("\n", "<br>") + "</p>";
        return $$"""
<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<meta name="referrer" content="no-referrer"><meta name="robots" content="noindex">
<title>{{H(name)}} - invitation</title>
<style nonce="{{nonce}}">
:root{color-scheme:dark}
body{margin:0;min-height:100vh;display:grid;place-items:center;background:#0f1113;color:#e8eaed;font:15px/1.6 'Segoe UI',system-ui,sans-serif}
main{box-sizing:border-box;width:100%;max-width:460px;margin:16px;padding:32px;background:#16191c;border-radius:14px}
h1{margin:0 0 8px;font-size:22px;line-height:28px;font-weight:600}
p{color:#8a9199;margin:0 0 16px}
p.welcome{color:#e8eaed;background:#1e2226;border-radius:10px;padding:12px 16px}
ol{padding-left:20px;margin:0 0 24px;color:#8a9199}li{margin:8px 0}li b{color:#e8eaed;font-weight:600}
.btn{display:inline-block;text-decoration:none;font-weight:600;padding:10px 20px;border-radius:10px;margin:0 8px 8px 0}
.primary{background:#abd473;color:#111315}.secondary{background:rgba(255,255,255,.08);color:#e8eaed}
.note{font-size:12px;line-height:16px;margin:16px 0 0}
.hidden{display:none}
</style></head><body><main>
<section id="invited" class="hidden">
<h1>You're invited to <span id="lodge">{{H(name)}}</span></h1>
{{welcomeHtml}}
<p>Chat, voice and shared files with your friends, inside Elan's Outpost.</p>
<ol><li><b>Install the Outpost</b> (once), then start it.</li><li>Press <b>Open in the Outpost</b> below. If nothing happens, open the Lodge tab and paste this page's address into the join box.</li><li>Pick your device name and you are in.</li></ol>
<a class="btn primary" id="open" href="#">Open in the Outpost</a><a class="btn secondary" href="{{DownloadUrl}}">Download the Outpost</a>
<p class="note">This invite works once. The code in the address never leaves your browser: this page does not send it anywhere.</p>
</section>
<section id="plain">
<h1>{{H(name)}}</h1>
{{welcomeHtml}}
<p>This is a private lodge for Elan's Outpost. You need an invite link from the lodge owner to join.</p>
<a class="btn secondary" href="{{DownloadUrl}}">Download the Outpost</a>
</section>
</main>
<script nonce="{{nonce}}">
(function () {
  var h = location.hash || "";
  var m = /^#join=(.+)$/.exec(h);
  if (!m) return;
  var v; try { v = decodeURIComponent(m[1]); } catch (e) { return; }
  var bar = v.indexOf("|");
  if (bar < 1) return;
  var server = v.slice(0, bar).trim(), invite = v.slice(bar + 1).trim();
  if (!/^[A-Za-z0-9._:\/-]+$/.test(server) || !/^i1\.[A-Za-z0-9._-]{16,}$/.test(invite)) return;
  document.getElementById("open").setAttribute("href", "elanshub://lodge/join#" + server + "|" + invite);
  document.getElementById("invited").className = "";
  document.getElementById("plain").className = "hidden";
})();
</script></body></html>
""";
    }
}
