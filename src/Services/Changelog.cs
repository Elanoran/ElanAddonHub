using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ElansAddonHub.Services
{
    // --selftest "about": the public version history shown in Settings > About > What's new.
    // The Outpost's own CHANGELOG.md (the same file Tools/release.py reads for the GitHub release notes) and the Elan's Hub companion's
    // CHANGELOG.txt ride inside the exe (EmbeddedResource Changelog.outpost.md / Changelog.companion.txt), so every build shows its own notes.
    // The other addons' notes come from the update manifest (AddonInfo.Changelog). Everything here is public text: PrivacyProblems() is the
    // check the self-test (and a release) runs over it.
    public class ChangelogItem
    {
        public string Lead;   // the bold lead-in ("**A smaller radio player.**"), without the stars; null when the line has none
        public string Text;   // the rest of the line (markdown `code` spans and **bold** kept for the renderer)
    }

    public class ChangelogRelease
    {
        public string Version;
        public string Date;    // "2026-10-16" when the header carries one ("## 2.77.0 - 2026-10-16" / "## 2.77.0 (2026-10-16)"), else null
        public List<ChangelogItem> Items = new List<ChangelogItem>();
    }

    public static class Changelog
    {
        public const string OutpostResource = "Changelog.outpost.md";
        public const string CompanionResource = "Changelog.companion.txt";

        static List<ChangelogRelease> outpost, companion;

        public static List<ChangelogRelease> Outpost => outpost ?? (outpost = ParseMarkdown(ReadResource(OutpostResource)));
        public static List<ChangelogRelease> Companion => companion ?? (companion = ParseCompanion(ReadResource(CompanionResource)));

        public static string ReadResource(string name)
        {
            try
            {
                using (var s = typeof(Changelog).Assembly.GetManifestResourceStream(name))
                {
                    if (s == null) return null;
                    using (var r = new StreamReader(s, Encoding.UTF8)) return r.ReadToEnd();
                }
            }
            catch { return null; }
        }

        static readonly Regex Header = new Regex(@"^##\s+v?(\d+(?:\.\d+)+)\s*(?:[-–(]\s*(\d{4}-\d{2}-\d{2})\)?)?", RegexOptions.CultureInvariant);
        static readonly Regex LeadIn = new Regex(@"^\*\*(.+?)\*\*\s*(.*)$", RegexOptions.CultureInvariant | RegexOptions.Singleline);

        // CHANGELOG.md: "## x.y.z" sections (optionally with a date) of "- " lines, newest first; indented lines continue the bullet above.
        // The same rule release.py's hub_changelog() uses, so what the release notes say is what this page says.
        public static List<ChangelogRelease> ParseMarkdown(string text)
        {
            var list = new List<ChangelogRelease>();
            if (string.IsNullOrEmpty(text)) return list;
            ChangelogRelease cur = null;
            ChangelogItem last = null;
            foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                var line = raw.TrimEnd();
                var m = Header.Match(line);
                if (m.Success)
                {
                    cur = new ChangelogRelease { Version = m.Groups[1].Value, Date = m.Groups[2].Success ? m.Groups[2].Value : null };
                    list.Add(cur); last = null; continue;
                }
                if (line.StartsWith("#")) { cur = null; last = null; continue; }   // a heading that is no version ends the section
                if (cur == null) continue;
                var t = line.TrimStart();
                if (t.StartsWith("- ") || t.StartsWith("* "))
                {
                    last = Item(t.Substring(2).Trim());
                    cur.Items.Add(last);
                }
                else if (t.Length > 0 && last != null && raw.StartsWith(" ")) last.Text = (last.Text + " " + t).Trim();
            }
            return list;
        }

        public static ChangelogItem Item(string text)
        {
            var m = LeadIn.Match(text ?? "");
            return m.Success ? new ChangelogItem { Lead = m.Groups[1].Value.Trim(), Text = m.Groups[2].Value.Trim() } : new ChangelogItem { Text = (text ?? "").Trim() };
        }

        static readonly Regex CompanionLine = new Regex(@"^v?(\d+(?:\.\d+)+)\s+(.+)$", RegexOptions.CultureInvariant);

        // the companion's CHANGELOG.txt: one paragraph per version, "1.9.1 Fixes the ...", oldest first -> newest first
        public static List<ChangelogRelease> ParseCompanion(string text)
        {
            var list = new List<ChangelogRelease>();
            if (string.IsNullOrEmpty(text)) return list;
            ChangelogRelease cur = null;
            foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                var m = CompanionLine.Match(line);
                if (m.Success) { cur = new ChangelogRelease { Version = m.Groups[1].Value }; cur.Items.Add(new ChangelogItem { Text = m.Groups[2].Value.Trim() }); list.Add(cur); }
                else if (cur != null) cur.Items[cur.Items.Count - 1].Text += " " + line;
            }
            return Newest(list);
        }

        // the manifest's per-addon entries ({version, text}, one or more per version, any order) -> releases newest first;
        // a text with line breaks or "- " bullets becomes one item per line
        public static List<ChangelogRelease> FromEntries(IEnumerable<ChangeEntry> entries)
        {
            var by = new Dictionary<string, ChangelogRelease>();
            var order = new List<ChangelogRelease>();
            foreach (var e in entries ?? Enumerable.Empty<ChangeEntry>())
            {
                if (e == null || string.IsNullOrWhiteSpace(e.Version)) continue;
                var v = e.Version.Trim().TrimStart('v', 'V');
                if (!by.TryGetValue(v, out var rel)) { rel = new ChangelogRelease { Version = v }; by[v] = rel; order.Add(rel); }
                foreach (var part in (e.Text ?? "").Replace("\r\n", "\n").Split('\n'))
                {
                    var p = part.Trim();
                    if (p.StartsWith("- ") || p.StartsWith("* ")) p = p.Substring(2).Trim();
                    if (p.Length > 0) rel.Items.Add(Item(p));
                }
            }
            return Newest(order);
        }

        static List<ChangelogRelease> Newest(List<ChangelogRelease> list) =>
            list.Select((r, i) => new { r, i }).OrderByDescending(x => x.r.Version, Comparer<string>.Create(Util.CompareVersions)).ThenBy(x => x.i).Select(x => x.r).ToList();

        // ---------------------------------------------------------------- inline markdown for the renderer

        public enum SpanKind { Text, Bold, Code }

        // "**bold**" and "`code`" inside a line; anything unbalanced stays as text
        public static List<KeyValuePair<SpanKind, string>> Spans(string text)
        {
            var o = new List<KeyValuePair<SpanKind, string>>();
            if (string.IsNullOrEmpty(text)) return o;
            var sb = new StringBuilder();
            void Flush() { if (sb.Length > 0) { o.Add(new KeyValuePair<SpanKind, string>(SpanKind.Text, sb.ToString())); sb.Clear(); } }
            int i = 0;
            while (i < text.Length)
            {
                if (text[i] == '`') { int j = text.IndexOf('`', i + 1); if (j > i + 1) { Flush(); o.Add(new KeyValuePair<SpanKind, string>(SpanKind.Code, text.Substring(i + 1, j - i - 1))); i = j + 1; continue; } }
                if (i + 1 < text.Length && text[i] == '*' && text[i + 1] == '*') { int j = text.IndexOf("**", i + 2, StringComparison.Ordinal); if (j > i + 2) { Flush(); o.Add(new KeyValuePair<SpanKind, string>(SpanKind.Bold, text.Substring(i + 2, j - i - 2))); i = j + 2; continue; } }
                sb.Append(text[i]); i++;
            }
            Flush();
            return o;
        }

        // ---------------------------------------------------------------- privacy: the notes are public, keep them so

        static readonly (string why, Regex rx)[] Private =
        {
            ("a Windows user folder", new Regex(@"[A-Za-z]:[\\/]+Users[\\/]+(?!Public\b)[^\\/\s""'<>]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
            ("a local path", new Regex(@"\b[A-Za-z]:\\[^\s""'<>]+", RegexOptions.CultureInvariant)),
            ("a WoW account folder name", new Regex(@"WTF[\\/]+Account[\\/]+(?!<|ACCOUNT\b|ACCOUNTNAME\b|\.\.\.)[^\\/\s""'<>]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
            ("an e-mail address", new Regex(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b", RegexOptions.CultureInvariant)),
            ("an IP address", new Regex(@"\b(?!127\.|0\.0\.0\.0\b)(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)\b", RegexOptions.CultureInvariant)),
            ("an access token", new Regex(@"\b(?:ghp_|gho_|ghs_|github_pat_|xox[abposr]-|sk-[A-Za-z0-9]{8}|AKIA[0-9A-Z]{12})[A-Za-z0-9_\-]*", RegexOptions.CultureInvariant)),
            ("a secret value", new Regex(@"\b(?:token|password|passwd|secret|api[_-]?key|invite[_-]?code)\s*[=:]\s*[^\s,;]{6,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
            ("a bearer token", new Regex(@"\bBearer\s+[A-Za-z0-9._\-]{12,}", RegexOptions.CultureInvariant)),
            ("a link with a user name or password", new Regex(@"\b[a-z][a-z0-9+.-]*://[^\s/@]+:[^\s/@]*@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
            ("a link to a home-network host", new Regex(@"\bhttps?://[^\s/]+\.(?:lan|local|home|internal|localdomain)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
        };

        // what would leak, one line per hit ("line 12: an e-mail address (a@b.dk)"); empty = fine to publish
        public static List<string> PrivacyProblems(string text)
        {
            var o = new List<string>();
            if (string.IsNullOrEmpty(text)) return o;
            var lines = text.Replace("\r\n", "\n").Split('\n');
            for (int n = 0; n < lines.Length; n++)
                foreach (var (why, rx) in Private)
                    foreach (Match m in rx.Matches(lines[n]))
                        o.Add($"line {n + 1}: {why} ({(m.Value.Length > 40 ? m.Value.Substring(0, 40) + "..." : m.Value)})");
            return o;
        }
    }

    // the one-time "What's new in x.y.z" note after an update (Settings.WhatsNewSeen = the version it was shown for)
    public static class WhatsNew
    {
        // seen = the version the note last showed for (null: never); fresh = a first start (no settings file): no note, only remember
        public static bool ShouldShow(string seen, string current, bool fresh)
        {
            if (string.IsNullOrEmpty(current) || fresh) return false;
            if (string.IsNullOrEmpty(seen)) return true;   // an update from a version that predates the note
            return Util.CompareVersions(current, seen) > 0;
        }
    }
}
