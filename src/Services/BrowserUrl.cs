using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ElansAddonHub.Services
{
    public enum PageSecurity { Internal, Secure, NotSecure }

    // The Browser page's pure rules (no UI, no network): typed text -> address or search, which schemes may load, how a tab shows
    // its security, the default quick-link tiles. Tested offline by the "browser" selftest.
    public static class BrowserUrl
    {
        public sealed class SearchEngine
        {
            public string Id, Name, Url;   // Url has {0} where the escaped query goes
        }

        public const string DefaultEngine = "duckduckgo";
        public static readonly SearchEngine[] Engines =
        {
            new SearchEngine { Id = "duckduckgo", Name = "DuckDuckGo", Url = "https://duckduckgo.com/?q={0}" },
            new SearchEngine { Id = "brave", Name = "Brave Search", Url = "https://search.brave.com/search?q={0}" },
            new SearchEngine { Id = "startpage", Name = "Startpage", Url = "https://www.startpage.com/do/search?q={0}" },
            new SearchEngine { Id = "ecosia", Name = "Ecosia", Url = "https://www.ecosia.org/search?q={0}" },
            new SearchEngine { Id = "bing", Name = "Bing", Url = "https://www.bing.com/search?q={0}" },
            new SearchEngine { Id = "google", Name = "Google", Url = "https://www.google.com/search?q={0}" },
        };

        public static SearchEngine EngineById(string id) =>
            Engines.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Engines[0];

        // the tiles on the new-tab page until the user edits them
        public static List<QuickLink> DefaultTiles() => new List<QuickLink>
        {
            new QuickLink { Name = "Wowhead", Url = "https://www.wowhead.com/classic" },
            new QuickLink { Name = "Talent calculator", Url = "https://www.wowhead.com/classic/talent-calc" },
            new QuickLink { Name = "Forever Tools", Url = "https://wowforevertools.com" },
            new QuickLink { Name = "Petopia Forever", Url = "https://www.wow-petopia.com/forever/" },
            new QuickLink { Name = "Radio Paradise", Url = "https://radioparadise.com" },
        };

        // ---------------------------------------------------------------- typed text -> address

        static readonly Regex SchemeRx = new Regex(@"^[a-z][a-z0-9+.\-]*:(?!\d+(?:[/?#]|$))", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex WebRx = new Regex(@"^https?://\S+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex DomainRx = new Regex(@"^(?:[\p{L}\p{N}](?:[\p{L}\p{N}\-]*[\p{L}\p{N}])?\.)+\p{L}{2,}(?::\d{1,5})?$", RegexOptions.Compiled);
        static readonly Regex LocalRx = new Regex(@"^(?:localhost|\d{1,3}(?:\.\d{1,3}){3}|\[[0-9a-f:.]+\])(?::\d{1,5})?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // text that is an address: "https://..." as typed, or "example.com/x", "localhost:3000", "192.168.0.1" with a scheme added
        static string AsAddress(string t)
        {
            if (WebRx.IsMatch(t)) return Uri.TryCreate(t, UriKind.Absolute, out var u) && !string.IsNullOrEmpty(u.Host) ? u.AbsoluteUri : null;
            if (t.Any(char.IsWhiteSpace) || SchemeRx.IsMatch(t)) return null;
            var cut = t.IndexOfAny(new[] { '/', '?', '#' });
            var host = cut < 0 ? t : t.Substring(0, cut);
            bool local = LocalRx.IsMatch(host);
            if (!local && !DomainRx.IsMatch(host)) return null;
            return Uri.TryCreate((local ? "http://" : "https://") + t, UriKind.Absolute, out var v) && !string.IsNullOrEmpty(v.Host) ? v.AbsoluteUri : null;
        }

        // What Enter in the address field does: a URL to load, or a search for the text. null = nothing typed.
        // Other schemes (javascript:, file:, data:, mailto:, edge:...) never become addresses: they are searched for as plain text.
        public static string Resolve(string input, string engineId)
        {
            var t = (input ?? "").Trim();
            if (t.Length == 0) return null;
            if (string.Equals(t, "about:blank", StringComparison.OrdinalIgnoreCase)) return "about:blank";
            return AsAddress(t) ?? EngineById(engineId).Url.Replace("{0}", Uri.EscapeDataString(t));
        }

        // the schemes a tab may load: web pages and the blank page. Everything else (javascript:, file:, data:, blob:, ftp:, view-source:,
        // edge:, custom app schemes) is blocked.
        public static bool IsAllowedScheme(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            if (string.Equals(url, "about:blank", StringComparison.OrdinalIgnoreCase)) return true;
            return Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps) && !string.IsNullOrEmpty(u.Host);
        }

        public static PageSecurity SecurityOf(string url)
        {
            if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var u)) return PageSecurity.Internal;
            return u.Scheme == Uri.UriSchemeHttps ? PageSecurity.Secure : u.Scheme == Uri.UriSchemeHttp ? PageSecurity.NotSecure : PageSecurity.Internal;
        }

        public static string HostOf(string url) =>
            Uri.TryCreate(url ?? "", UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps) ? u.Host.ToLowerInvariant() : "";

        // the address as shown when the field is not being edited: no scheme, no lone trailing slash
        public static string Display(string url)
        {
            if (string.IsNullOrEmpty(url) || string.Equals(url, "about:blank", StringComparison.OrdinalIgnoreCase)) return "";
            var s = Regex.Replace(url, @"^https?://", "", RegexOptions.IgnoreCase);
            return s.EndsWith("/") && s.IndexOf('/') == s.Length - 1 ? s.TrimEnd('/') : s;
        }

        // one capital letter for a letter tile ("Wowhead" -> W, "www.example.com" -> E)
        public static string Initial(string name, string url)
        {
            var src = !string.IsNullOrWhiteSpace(name) ? name.Trim() : HostOf(url);
            if (src.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) src = src.Substring(4);
            foreach (var ch in src) if (char.IsLetterOrDigit(ch)) return char.ToUpperInvariant(ch).ToString();
            return "?";
        }

        // ---------------------------------------------------------------- downloads

        static readonly string[] Reserved = { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };
        static readonly string[] RiskyExt = { ".exe", ".msi", ".bat", ".cmd", ".com", ".scr", ".ps1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".jar", ".lnk", ".reg", ".hta", ".dll", ".msix", ".appx", ".pif" };

        // a file name from a website made safe to create in the Downloads folder: no path, no illegal characters, no device names
        public static string SafeFileName(string name)
        {
            var raw = name ?? "";
            var n = raw.Substring(Math.Max(raw.LastIndexOf((char)92), raw.LastIndexOf('/')) + 1);   // after the last / or backslash
            foreach (var c in System.IO.Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
            n = n.Trim().TrimEnd('.', ' ');
            if (n.Length > 120) { var ext = System.IO.Path.GetExtension(n); n = n.Substring(0, 120 - Math.Min(ext.Length, 20)) + (ext.Length <= 20 ? ext : ""); }
            var stem = System.IO.Path.GetFileNameWithoutExtension(n);
            if (n.Length == 0 || Array.IndexOf(Reserved, stem.ToUpperInvariant()) >= 0) n = "download" + (n.Length > 0 ? "-" + n : "");
            return n;
        }

        // programs and scripts: the download question warns about them
        public static bool IsRiskyFile(string name) => Array.IndexOf(RiskyExt, System.IO.Path.GetExtension(name ?? "").ToLowerInvariant()) >= 0;

        // folder/name, or folder/name (2).ext when that exists already (never overwrites)
        public static string UniquePath(string folder, string name, Func<string, bool> exists = null)
        {
            exists = exists ?? System.IO.File.Exists;
            var stem = System.IO.Path.GetFileNameWithoutExtension(name); var ext = System.IO.Path.GetExtension(name);
            var p = System.IO.Path.Combine(folder, name);
            for (int i = 2; exists(p) && i < 1000; i++) p = System.IO.Path.Combine(folder, stem + " (" + i + ")" + ext);
            return p;
        }

        // a tile needs a web address; "wowhead.com" is accepted and gets https://. null = not an address (a search would not be a tile).
        public static string TileUrl(string input)
        {
            var t = (input ?? "").Trim();
            var r = t.Length == 0 ? null : AsAddress(t);
            return r != null && IsAllowedScheme(r) ? r : null;
        }
    }

    // Links in chat message text: http(s) only, found anywhere in the text. javascript:, file:, data:, mailto: ... never match.
    public static class ChatLinks
    {
        static readonly Regex UrlRx = new Regex(@"https?://[^\s<>""']+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        static string Clean(string raw)
        {
            var s = raw;
            while (s.Length > 0)
            {
                char c = s[s.Length - 1];
                if (".,;:!?'\"".IndexOf(c) >= 0) { s = s.Substring(0, s.Length - 1); continue; }
                // a closing bracket belongs to the link only when the link opened one ("wiki/Foo_(bar)")
                if (c == ')' && s.Count(x => x == ')') > s.Count(x => x == '(')) { s = s.Substring(0, s.Length - 1); continue; }
                if (c == ']' && s.Count(x => x == ']') > s.Count(x => x == '[')) { s = s.Substring(0, s.Length - 1); continue; }
                if (c == '}' || c == '>') { s = s.Substring(0, s.Length - 1); continue; }
                break;
            }
            return s;
        }

        static string Valid(string s) =>
            Uri.TryCreate(s, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps) && !string.IsNullOrEmpty(u.Host) ? s : null;

        public static IEnumerable<string> All(string text)
        {
            if (string.IsNullOrEmpty(text)) yield break;
            foreach (Match m in UrlRx.Matches(text))
            {
                var v = Valid(Clean(m.Value));
                if (v != null) yield return v;
            }
        }

        // the link whose text covers this character index (for a click on it), or null
        public static string At(string text, int index)
        {
            if (string.IsNullOrEmpty(text) || index < 0) return null;
            foreach (Match m in UrlRx.Matches(text))
            {
                var url = Clean(m.Value);
                if (index >= m.Index && index < m.Index + url.Length) return Valid(url);
            }
            return null;
        }
    }
}
