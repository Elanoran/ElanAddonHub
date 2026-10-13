using System;
using System.IO;
using System.Text;

namespace ElansAddonHub.Services.Atlas.Classic
{
    // The statement was understood but is something this importer does not do (a SELECT, a join, a function, a column it does not keep).
    // The importer logs it and goes on: an update we cannot apply must never stop the import.
    public sealed class ClassicSqlUnsupported : Exception
    {
        public ClassicSqlUnsupported(string msg) : base(msg) { }
    }

    // Splits a MySQL script into statements without ever holding the whole script: reads a TextReader in blocks, tracks quotes
    // ('...' with \ escapes and '' doubling, "...", `...`) and comments (-- , #, /* */ incl. the /*!40101 ... */ conditional ones, all dropped),
    // and ends a statement at a ; outside quotes. A statement the caller does not want (decided from its first ~120 characters) is
    // skipped without being stored, so a 70 MB dump costs memory only for the tables we keep.
    public sealed class ClassicSqlReader
    {
        const int HeadLen = 120;
        readonly TextReader r;
        readonly char[] buf = new char[1 << 16];
        int pos, len;
        bool eof;
        readonly StringBuilder sb = new StringBuilder(1 << 12);

        public long Statements, Skipped;

        public ClassicSqlReader(TextReader reader) { r = reader; }

        int Peek()
        {
            if (pos >= len)
            {
                if (eof) return -1;
                len = r.Read(buf, 0, buf.Length); pos = 0;
                if (len <= 0) { eof = true; len = 0; return -1; }
            }
            return buf[pos];
        }
        int Take() { int c = Peek(); if (c >= 0) pos++; return c; }

        // Next statement text (comments removed, no trailing ;). `want` sees the head of the statement; when it says no the statement is
        // swallowed and `skipped` is true (stmt then holds only the head). Returns false at the end of the input.
        public bool Next(Func<string, bool> want, out string stmt, out bool skipped)
        {
            while (true)
            {
                if (!NextCore(want, out stmt, out skipped, out bool empty)) return false;
                if (!empty) return true;
            }
        }

        bool NextCore(Func<string, bool> want, out string stmt, out bool skipped, out bool empty)
        {
            sb.Clear();
            skipped = false; stmt = null; empty = false;
            bool decided = want == null, swallow = false, any = false;
            while (true)
            {
                int c = Take();
                if (c < 0) break;
                if (c == ';')
                {
                    any = true;
                    break;
                }
                // comments
                if (c == '-' && Peek() == '-')
                {
                    int n2 = PeekAt(1);
                    if (n2 < 0 || n2 == ' ' || n2 == '\t' || n2 == '\r' || n2 == '\n') { SkipLine(); AppendSpace(swallow); continue; }
                }
                if (c == '#') { SkipLine(); AppendSpace(swallow); continue; }
                if (c == '/' && Peek() == '*') { Take(); SkipBlock(); AppendSpace(swallow); continue; }
                if (c == '\'' || c == '"' || c == '`')
                {
                    if (swallow) SkipQuoted((char)c, null);
                    else { sb.Append((char)c); SkipQuoted((char)c, sb); }
                }
                else if (!swallow)
                {
                    if (c == '\r' || c == '\n' || c == '\t') c = ' ';
                    if (c == ' ' && (sb.Length == 0 || sb[sb.Length - 1] == ' ')) { }
                    else sb.Append((char)c);
                }
                if (!decided && !swallow && sb.Length >= HeadLen)
                {
                    decided = true;
                    if (!want(sb.ToString())) { swallow = true; stmt = sb.ToString(); sb.Clear(); }
                }
                any = true;
            }
            if (!any && sb.Length == 0 && !swallow) return false;
            if (swallow) { skipped = true; Skipped++; Statements++; return true; }
            var text = sb.ToString().Trim();
            if (text.Length == 0) { empty = true; return true; }
            if (!decided && want != null && !want(text)) { stmt = text.Length > HeadLen ? text.Substring(0, HeadLen) : text; skipped = true; Skipped++; Statements++; return true; }
            stmt = text; Statements++;
            return true;
        }

        void AppendSpace(bool swallow)
        {
            if (!swallow && sb.Length > 0 && sb[sb.Length - 1] != ' ') sb.Append(' ');
        }

        int PeekAt(int off)
        {
            // pos points at the char after the first '-' (Peek() was the second '-'); look one further
            if (pos + off < len) return buf[pos + off];
            // crossing a block boundary: refill keeping the pending chars
            if (eof) return -1;
            int rest = len - pos;
            Array.Copy(buf, pos, buf, 0, rest);
            int n = r.Read(buf, rest, buf.Length - rest);
            len = rest + Math.Max(n, 0); pos = 0;
            if (n <= 0) eof = true;
            return pos + off < len ? buf[pos + off] : -1;
        }

        void SkipLine()
        {
            int c;
            while ((c = Take()) >= 0 && c != '\n') { }
        }

        void SkipBlock()
        {
            int c, prev = 0;
            while ((c = Take()) >= 0)
            {
                if (prev == '*' && c == '/') return;
                prev = c;
            }
        }

        // after the opening quote: copies (when into != null) up to and including the closing quote; \ escapes and '' kept as written
        void SkipQuoted(char q, StringBuilder into)
        {
            int c;
            while ((c = Take()) >= 0)
            {
                into?.Append((char)c);
                if (c == '\\' && q != '`')
                {
                    int e = Take();
                    if (e >= 0) into?.Append((char)e);
                    continue;
                }
                if (c == q)
                {
                    if (Peek() == q) { int d = Take(); into?.Append((char)d); continue; }
                    return;
                }
            }
        }
    }
}
