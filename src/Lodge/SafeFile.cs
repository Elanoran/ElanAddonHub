using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ElansAddonHub.Lodge
{
    // Everything that comes from the network and ends up in a file name or in chat text is cleaned here, on this side too:
    // an older Lodge server (before 2.8) sends names as they were uploaded. Mirrors the server's Names.FileName / MessageText.
    public static class SafeFile
    {
        static readonly string[] Devices = { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$" };

        // A name that is safe to write into a folder on Windows: no path parts, no drive letters, no | < > " : * ?, no control /
        // format characters (bidi overrides, zero width), no leading/trailing dots or spaces, no device names, bounded.
        public static string Name(string name, int max = 120)
        {
            name = name ?? "";
            try { name = name.Normalize(NormalizationForm.FormKC); } catch (ArgumentException) { }
            name = name.Replace('\\', '/');
            name = name.Substring(name.LastIndexOf('/') + 1);
            var sb = new StringBuilder(name.Length);
            bool space = false;
            for (int i = 0; i < name.Length; i++)
            {
                var c = name[i];
                var cat = char.GetUnicodeCategory(c);
                if (char.IsWhiteSpace(c) || cat == UnicodeCategory.SpaceSeparator) { space = sb.Length > 0; continue; }
                if (cat == UnicodeCategory.Control || cat == UnicodeCategory.Format || cat == UnicodeCategory.PrivateUse || cat == UnicodeCategory.OtherNotAssigned
                    || cat == UnicodeCategory.LineSeparator || cat == UnicodeCategory.ParagraphSeparator) continue;
                if (cat == UnicodeCategory.Surrogate)
                {
                    if (char.IsHighSurrogate(c) && i + 1 < name.Length && char.IsLowSurrogate(name[i + 1]))
                    {
                        if (space) { sb.Append(' '); space = false; }
                        sb.Append(c).Append(name[i + 1]); i++;
                    }
                    continue;
                }
                if (space) { sb.Append(' '); space = false; }
                sb.Append(c == '\\' || c == '/' || c == ':' || c == '*' || c == '?' || c == '"' || c == '<' || c == '>' || c == '|' ? '_' : c);
            }
            var r = sb.ToString().Trim(' ', '.');
            if (r.Length > max)
            {
                var dot = r.LastIndexOf('.');
                var ext = dot > 0 && r.Length - dot <= 16 ? r.Substring(dot) : "";
                var stem = r.Substring(0, Math.Max(1, max - ext.Length));
                if (stem.Length > 0 && char.IsHighSurrogate(stem[stem.Length - 1])) stem = stem.Substring(0, stem.Length - 1);
                r = stem.TrimEnd(' ', '.') + ext;
            }
            if (r.Length == 0) return "file";
            var first = (r.Contains('.') ? r.Substring(0, r.IndexOf('.')) : r).TrimEnd();
            bool device = Devices.Contains(first, StringComparer.OrdinalIgnoreCase)
                || (first.Length == 4 && (first.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || first.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && char.IsDigit(first[3]));
            return device ? "_" + r : r;
        }

        // a file id that is safe in a cache file name (the server's are 32 hex characters); null = don't use it
        public static string Id(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 64) return null;
            foreach (var c in id) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'))) return null;
            return id;
        }

        // folder + name, and the result is verified to be inside the folder (never trust a join). Null = refused.
        public static string PathIn(string dir, string name)
        {
            try
            {
                var root = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var full = Path.GetFullPath(Path.Combine(root, Name(name)));
                return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && !string.Equals(full, root, StringComparison.OrdinalIgnoreCase) ? full : null;
            }
            catch (Exception) { return null; }
        }

        // "name (2).ext" until it doesn't exist; always inside `dir`
        public static string FreePath(string dir, string name)
        {
            var target = PathIn(dir, name);
            if (target == null) return null;
            var safe = Path.GetFileName(target);
            for (int i = 2; File.Exists(target); i++)
                target = PathIn(dir, Path.GetFileNameWithoutExtension(safe) + " (" + i + ")" + Path.GetExtension(safe));
            return target;
        }

        // ---- files that can run something when opened. The warning names the real extension (after the cleaning above).
        static readonly HashSet<string> Risky = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".scr", ".bat", ".cmd", ".com", ".msi", ".ps1", ".vbs", ".js", ".jar", ".lnk", ".reg", ".hta", ".dll",
        };

        public static string Ext(string name) => Path.GetExtension(name ?? "") ?? "";
        public static bool IsRisky(string name) => Risky.Contains(Ext(name));

        // ---- chat text: bidi overrides and isolates reverse the text that follows (a fake "gnp.exe"), so they never reach the screen
        static bool IsBidi(char c) => (c >= '‪' && c <= '‮') || (c >= '⁦' && c <= '⁩');
        static readonly Regex BlankRun = new Regex(@"\n(?:[ \t]*\n){4,}", RegexOptions.Compiled);

        public static string Text(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var sb = new StringBuilder(text.Length);
            bool any = false;
            foreach (var c in text) { if (IsBidi(c)) { any = true; continue; } sb.Append(c); }
            var s = any ? sb.ToString() : text;
            return s.IndexOf("\n\n\n\n\n", StringComparison.Ordinal) >= 0 || s.IndexOf('\r') >= 0 ? BlankRun.Replace(s.Replace("\r\n", "\n"), "\n\n\n\n") : s;
        }

        // ---- the picture cache: files older than 30 days go, then the oldest until the folder is under the size cap
        public static int PruneCache(string dir, TimeSpan maxAge, long maxBytes, DateTime now)
        {
            int removed = 0;
            try
            {
                if (!Directory.Exists(dir)) return 0;
                var files = new DirectoryInfo(dir).GetFiles().OrderBy(f => f.LastWriteTimeUtc).ToList();
                foreach (var f in files.ToList())
                    if (now - f.LastWriteTimeUtc > maxAge) { try { f.Delete(); removed++; files.Remove(f); } catch { } }
                long total = files.Sum(f => f.Length);
                foreach (var f in files)
                {
                    if (total <= maxBytes) break;
                    try { total -= f.Length; f.Delete(); removed++; } catch { }
                }
            }
            catch { }
            return removed;
        }

        // %TEMP%\elanshub-paste-*: pasted pictures wait there for their upload; leftovers of a crash are removed at start
        public static int DeletePasteLeftovers(string tempDir)
        {
            int n = 0;
            try
            {
                foreach (var d in Directory.GetDirectories(tempDir, "elanshub-paste-*"))
                    try { Directory.Delete(d, true); n++; } catch { }
            }
            catch { }
            return n;
        }
    }
}
