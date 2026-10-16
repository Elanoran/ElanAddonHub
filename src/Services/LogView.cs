using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ElansAddonHub.Services
{
    // --selftest "about": the model behind Settings > About > View log (LogViewerWindow). hub.log (Util.Log) is "yyyy-MM-dd HH:mm:ss  message"
    // lines; a crash or an exception continues on lines without a time (the stack). Here: the lines -> entries with a level (the log has no
    // level column, so it is read from the words), personal details hidden for the view (and for Copy), and the coloured spans of a line.
    public enum LogLevel { Info, Warning, Error }

    public enum LogSpan { Text, Time, Category, Error, Warning, Exception, StackAt, StackMethod, StackFile, Number, Redacted }

    public class LogRedaction { public int Start, Length; public string Why; }

    public class LogLine
    {
        public int Number;            // 1-based line in the file (as read)
        public int Entry;             // the entry it belongs to (a stack shares its first line's)
        public string Time;           // "2026-10-16 10:40:38" on an entry's first line, null on its continuation lines
        public string Text;           // the message (redacted), without the time
        public LogLevel Level;        // the entry's level (continuation lines share it)
        public bool Continuation;     // a stack / exception line under the entry's first line
        public List<LogRedaction> Redactions = new List<LogRedaction>();
        public string Plain => Time != null ? Time + "  " + Text : Text;   // what Copy puts on the clipboard (already redacted)
    }

    public class LogParser
    {
        static readonly Regex Stamp = new Regex(@"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\s{1,2}(.*)$", RegexOptions.CultureInvariant);
        readonly LogRedactor redactor;
        int number, entry;
        LogLevel level = LogLevel.Info;
        bool any;
        readonly List<LogLine> current = new List<LogLine>();   // the lines of the entry being read (an exception under them raises them all to Error)

        public LogParser(LogRedactor redactor) { this.redactor = redactor ?? new LogRedactor(); }

        public List<LogLine> Feed(IEnumerable<string> lines)
        {
            var o = new List<LogLine>();
            foreach (var raw in lines) { var l = Line(raw ?? ""); if (l != null) o.Add(l); }
            return o;
        }

        public LogLine Line(string raw)
        {
            number++;
            raw = raw.TrimEnd('\r');
            var m = Stamp.Match(raw);
            string time = null, text;
            bool first = !any;
            if (m.Success) { time = m.Groups[1].Value; text = m.Groups[2].Value; entry++; level = LevelOf(text); any = true; current.Clear(); }
            else
            {
                if (raw.Trim().Length == 0) return null;
                text = raw;
                if (!any) { entry++; level = LevelOf(text); any = true; }
                else if (level != LogLevel.Error && LevelOf(text) == LogLevel.Error)   // an exception under a plain line still makes the entry an error
                {
                    level = LogLevel.Error;
                    foreach (var c in current) c.Level = level;
                }
            }
            var line = new LogLine { Number = number, Entry = entry, Time = time, Continuation = !m.Success && !first, Level = level };
            line.Text = redactor.Redact(text, line.Redactions);
            current.Add(line);
            return line;
        }

        public void Reset() { number = 0; entry = 0; level = LogLevel.Info; any = false; current.Clear(); }

        static readonly Regex ErrorWords = new Regex(@"\b(crash\w*|exception|error|errors|failed|failure|fatal|unhandled|undtagelse|fejl|could ?n[o']t start)\b|\w+Exception\b|^\s*(at|ved|bei|à|en)\s+[\w.`<>\[\]]+\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly Regex WarnWords = new Regex(@"\b(warn\w*|retry\w*|will resend|resend|rate limit\w*|time ?out|timed out|unreadable|fallback|falling back|using stable|not found|stale|refused|rejected [1-9]|could ?n[o']t|cannot|can't|declined|gave up|giving up|aborted|cancel+ed|annulleret)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // "rejected 0" and "0 missing" are counts, not trouble
        static readonly Regex ZeroCounts = new Regex(@"\b(0 (missing|skipped|rejected|failed|errors?)|(missing|skipped|rejected|failed|errors?):? 0)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static LogLevel LevelOf(string text)
        {
            if (string.IsNullOrEmpty(text)) return LogLevel.Info;
            var t = ZeroCounts.Replace(text, "");
            if (ErrorWords.IsMatch(t)) return LogLevel.Error;
            if (WarnWords.IsMatch(t)) return LogLevel.Warning;
            return LogLevel.Info;
        }

        // ---------------------------------------------------------------- the coloured spans of one line (already redacted)

        static readonly Regex StackFrame = new Regex(@"^(\s*)(at|ved|bei|à|en)(\s+)([^\s(]+(?:\([^)]*\))?)(.*)$", RegexOptions.CultureInvariant);
        static readonly Regex StackFileTail = new Regex(@"\s(in|i|dans|en)\s+(.+:(line|linje|Zeile|ligne|línea)\s+\d+)\s*$", RegexOptions.CultureInvariant);
        static readonly Regex ExceptionName = new Regex(@"\b[\w.]*(Exception|Error)\b(?=[:\s']|$)", RegexOptions.CultureInvariant);
        static readonly Regex Category = new Regex(@"^([a-z][\w .'-]{0,40}?):\s", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly Regex Numbers = new Regex(@"(?<![\w.%<])\d+(?:[.,]\d+)*(?:\s?(?:ms|s|KB|MB|GB|%))?(?![\w>])", RegexOptions.CultureInvariant);

        public static List<KeyValuePair<LogSpan, string>> Spans(LogLine line)
        {
            var text = line.Text ?? "";
            var kinds = new LogSpan[text.Length];
            void Mark(int s, int len, LogSpan k) { for (int i = Math.Max(0, s); i < Math.Min(text.Length, s + len); i++) kinds[i] = k; }

            var sf = StackFrame.Match(text);
            if (line.Continuation && sf.Success)
            {
                Mark(sf.Groups[2].Index, sf.Groups[2].Length, LogSpan.StackAt);
                Mark(sf.Groups[4].Index, sf.Groups[4].Length, LogSpan.StackMethod);
                var ft = StackFileTail.Match(text);
                if (ft.Success) Mark(ft.Groups[2].Index, ft.Groups[2].Length, LogSpan.StackFile);
            }
            else
            {
                var c = line.Continuation ? Match.Empty : Category.Match(text);
                if (c.Success) Mark(c.Groups[1].Index, c.Groups[1].Length, LogSpan.Category);
                foreach (Match n in Numbers.Matches(text)) if (kinds[n.Index] == LogSpan.Text) Mark(n.Index, n.Length, LogSpan.Number);
                // the words that made the level, in the level's colour
                var lvlRx = line.Level == LogLevel.Error ? new Regex(@"\b(crash\w*|exception|error|failed|failure|fatal|unhandled)\b", RegexOptions.IgnoreCase)
                          : line.Level == LogLevel.Warning ? new Regex(@"\b(warn\w*|retry\w*|will resend|rate limit|timed out|unreadable|fallback|using stable|not found|stale|refused|could ?n[o']t|cannot|can't)\b", RegexOptions.IgnoreCase) : null;
                if (lvlRx != null && !ZeroCounts.IsMatch(text)) foreach (Match w in lvlRx.Matches(text)) Mark(w.Index, w.Length, line.Level == LogLevel.Error ? LogSpan.Error : LogSpan.Warning);
                foreach (Match e in ExceptionName.Matches(text)) Mark(e.Index, e.Length, LogSpan.Exception);
            }
            foreach (var r in line.Redactions) Mark(r.Start, r.Length, LogSpan.Redacted);

            var o = new List<KeyValuePair<LogSpan, string>>();
            int start = 0;
            for (int i = 1; i <= text.Length; i++)
                if (i == text.Length || kinds[i] != kinds[start]) { o.Add(new KeyValuePair<LogSpan, string>(kinds[start], text.Substring(start, i - start))); start = i; }
            return o;
        }

        // ---------------------------------------------------------------- reading the file (shared with the writer: Util.Log appends while we read)

        public static string ReadShared(string path, long from, out long end)
        {
            end = from;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (from > fs.Length) from = 0;
                    fs.Seek(from, SeekOrigin.Begin);
                    var buf = new byte[fs.Length - from];
                    int got = 0;
                    while (got < buf.Length) { int n = fs.Read(buf, got, buf.Length - got); if (n <= 0) break; got += n; }
                    end = from + got;
                    return Encoding.UTF8.GetString(buf, 0, got);
                }
            }
            catch { return null; }
        }
    }

    // Personal details are hidden in the view and in what Copy puts on the clipboard: the Windows user folder (-> %USERPROFILE%), the
    // Windows user and PC names, WoW account folder names, e-mail addresses, IP addresses (not 127.x) and anything that looks like a token.
    public class LogRedactor
    {
        readonly List<(Regex rx, Func<Match, string> to, string why)> rules = new List<(Regex, Func<Match, string>, string)>();

        public LogRedactor(string userName = null, string machineName = null, string userProfile = null)
        {
            userName = userName ?? SafeEnv(() => Environment.UserName);
            machineName = machineName ?? SafeEnv(() => Environment.MachineName);
            userProfile = userProfile ?? SafeEnv(() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            const RegexOptions I = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
            if (!string.IsNullOrEmpty(userProfile) && userProfile.Length > 3)
                rules.Add((new Regex(Regex.Escape(userProfile.TrimEnd('\\')) + @"(?=[\\/]|$|[\s""':;,)])", I), m => "%USERPROFILE%", "your Windows user folder"));
            rules.Add((new Regex(@"\b[A-Za-z]:[\\/]+Users[\\/]+(?!Public\b|Default\b|All Users\b)[^\\/\s""':;,)]+", I), m => "%USERPROFILE%", "a Windows user folder"));
            rules.Add((new Regex(@"(WTF[\\/]+Account[\\/]+)([^\\/\s""':;,)]+)", I), m => m.Groups[1].Value + "<account>", "a WoW account name"));
            rules.Add((new Regex(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b", RegexOptions.CultureInvariant), m => "<e-mail>", "an e-mail address"));
            rules.Add((new Regex(@"\b(?!127\.)(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)\b", RegexOptions.CultureInvariant), m => "<ip>", "an IP address"));
            rules.Add((new Regex(@"\b(token|password|passwd|secret|api[_-]?key|code|invite)(\s*[=:]\s*)([^\s,;&""']{6,})", I), m => m.Groups[1].Value + m.Groups[2].Value + "<hidden>", "a secret value"));
            rules.Add((new Regex(@"\bBearer\s+[A-Za-z0-9._\-]{12,}", RegexOptions.CultureInvariant), m => "Bearer <hidden>", "a token"));
            rules.Add((new Regex(@"\b(?:ghp_|gho_|github_pat_|xox[abposr]-)[A-Za-z0-9_\-]+", RegexOptions.CultureInvariant), m => "<hidden>", "a token"));
            if (!string.IsNullOrEmpty(userName) && userName.Length >= 3)
                rules.Add((new Regex(@"(?<![\w%<])" + Regex.Escape(userName) + @"(?![\w>])", I), m => "<user>", "your Windows user name"));
            if (!string.IsNullOrEmpty(machineName) && machineName.Length >= 3)
                rules.Add((new Regex(@"(?<![\w%<])" + Regex.Escape(machineName) + @"(?![\w>])", I), m => "<pc>", "your PC's name"));
        }

        static string SafeEnv(Func<string> f) { try { return f(); } catch { return null; } }

        // the text with each hit replaced, and where the replacements are in the result (for the view's marks). All rules look at the
        // original text; where two hits overlap the earlier rule wins (the user folder before the user name inside it).
        public string Redact(string text, List<LogRedaction> marks)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            var hits = new List<(int s, int e, string rep, string why)>();
            foreach (var (rx, to, why) in rules)
                foreach (Match m in rx.Matches(text))
                {
                    if (hits.Any(h => m.Index < h.e && h.s < m.Index + m.Length)) continue;
                    hits.Add((m.Index, m.Index + m.Length, to(m), why));
                }
            if (hits.Count == 0) return text;
            hits.Sort((x, y) => x.s.CompareTo(y.s));
            var sb = new StringBuilder();
            int last = 0;
            foreach (var h in hits)
            {
                sb.Append(text, last, h.s - last);
                // a rule that keeps a prefix (WTF\Account\) marks only what it replaced
                var orig = text.Substring(h.s, h.e - h.s);
                int keep = 0; while (keep < h.rep.Length && keep < orig.Length && h.rep[keep] == orig[keep]) keep++;
                marks?.Add(new LogRedaction { Start = sb.Length + keep, Length = h.rep.Length - keep, Why = h.why });
                sb.Append(h.rep);
                last = h.e;
            }
            sb.Append(text, last, text.Length - last);
            return sb.ToString();
        }

        public string Redact(string text) => Redact(text, null);
    }

    // the filter of the viewer: a search text (every word must be in the line, any case) and "errors only" (whole error entries,
    // their stack included); "warnings and errors" for the middle step
    public static class LogFilter
    {
        public static bool Keep(LogLine l, string query, LogLevel min)
        {
            if (l.Level < min) return false;
            if (string.IsNullOrWhiteSpace(query)) return true;
            var plain = l.Plain;
            foreach (var w in query.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                if (plain.IndexOf(w, StringComparison.OrdinalIgnoreCase) < 0) return false;
            return true;
        }

        public static List<LogLine> Apply(IEnumerable<LogLine> lines, string query, LogLevel min) => lines.Where(l => Keep(l, query, min)).ToList();
    }
}
