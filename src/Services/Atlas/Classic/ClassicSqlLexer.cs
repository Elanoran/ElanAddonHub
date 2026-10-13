using System;
using System.Globalization;
using System.Text;

namespace ElansAddonHub.Services.Atlas.Classic
{
    enum SqlTok { End, Ident, Number, String, Var, Punct }

    // Tokens of one statement (the statement reader already removed comments). MySQL flavour: `quoted` identifiers, 'strings' with \ escapes and
    // '' doubling, @user variables, 0x hex numbers, integers kept as long and decimals/exponents as double.
    sealed class ClassicSqlLexer
    {
        readonly string s;
        int i;

        public SqlTok Kind;
        string text;
        bool lazy; int ls, le; char lq;
        // identifier (unquoted, original case), string value (built on first use: values we skip cost no allocation), variable name (lower case, without @), punctuation
        public string Text { get { if (lazy) { text = Unescape(ls, le, lq); lazy = false; } return text; } private set { text = value; lazy = false; } }
        public bool Quoted;        // identifier was in backticks
        public long Int;
        public double Dbl;
        public bool IsInt;

        public ClassicSqlLexer(string statement) { s = statement; Next(); }

        public bool AtEnd => Kind == SqlTok.End;
        public int Position => i;

        public bool IsWord(string w) => Kind == SqlTok.Ident && !Quoted && string.Equals(Text, w, StringComparison.OrdinalIgnoreCase);
        public bool IsPunct(string p) => Kind == SqlTok.Punct && Text == p;

        public bool AcceptWord(string w) { if (IsWord(w)) { Next(); return true; } return false; }
        public bool AcceptPunct(string p) { if (IsPunct(p)) { Next(); return true; } return false; }
        public void ExpectPunct(string p) { if (!AcceptPunct(p)) throw new ClassicSqlUnsupported("expected '" + p + "' near '" + Near() + "'"); }
        public void ExpectWord(string w) { if (!AcceptWord(w)) throw new ClassicSqlUnsupported("expected " + w + " near '" + Near() + "'"); }

        public string Near()
        {
            int a = Math.Max(0, i - 20);
            return s.Substring(a, Math.Min(40, s.Length - a)).Replace('\n', ' ');
        }

        public void Next()
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            Quoted = false; Text = null;
            if (i >= s.Length) { Kind = SqlTok.End; return; }
            char c = s[i];
            if (c == '`')
            {
                int j = s.IndexOf('`', i + 1);
                if (j < 0) throw new ClassicSqlUnsupported("unterminated identifier");
                Text = s.Substring(i + 1, j - i - 1); Quoted = true; Kind = SqlTok.Ident; i = j + 1; return;
            }
            if (c == '\'' || c == '"') { Kind = SqlTok.String; ScanString(c); return; }
            if (char.IsDigit(c) || (c == '.' && i + 1 < s.Length && char.IsDigit(s[i + 1]))) { ReadNumber(); return; }
            if (c == '@')
            {
                int j = i + 1;
                if (j < s.Length && s[j] == '@') j++;   // @@system variables: treated like user variables (unknown unless set)
                int st = j;
                while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] == '_' || s[j] == '$' || s[j] == '.')) j++;
                Text = s.Substring(st, j - st).ToLowerInvariant(); Kind = SqlTok.Var; i = j; return;
            }
            if (char.IsLetter(c) || c == '_' || c == '$')
            {
                int j = i + 1;
                while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] == '_' || s[j] == '$')) j++;
                Text = s.Substring(i, j - i); Kind = SqlTok.Ident; i = j; return;
            }
            // punctuation, longest first
            string two = i + 1 < s.Length ? s.Substring(i, 2) : null;
            string three = i + 2 < s.Length ? s.Substring(i, 3) : null;
            if (three == "<=>") { Text = three; Kind = SqlTok.Punct; i += 3; return; }
            if (two == "<=" || two == ">=" || two == "<>" || two == "!=" || two == ":=" || two == "<<" || two == ">>" || two == "||" || two == "&&")
            { Text = two; Kind = SqlTok.Punct; i += 2; return; }
            Text = c.ToString(); Kind = SqlTok.Punct; i++;
        }

        void ReadNumber()
        {
            int st = i;
            if (s[i] == '0' && i + 1 < s.Length && (s[i + 1] == 'x' || s[i + 1] == 'X'))
            {
                int j = i + 2;
                while (j < s.Length && Uri.IsHexDigit(s[j])) j++;
                if (j > i + 2) { Int = (long)ulong.Parse(s.Substring(i + 2, j - i - 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture); IsInt = true; Kind = SqlTok.Number; i = j; return; }
            }
            bool real = false;
            while (i < s.Length && char.IsDigit(s[i])) i++;
            if (i < s.Length && s[i] == '.') { real = true; i++; while (i < s.Length && char.IsDigit(s[i])) i++; }
            if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
            {
                int j = i + 1;
                if (j < s.Length && (s[j] == '+' || s[j] == '-')) j++;
                if (j < s.Length && char.IsDigit(s[j])) { real = true; while (j < s.Length && char.IsDigit(s[j])) j++; i = j; }
            }
            var t = s.Substring(st, i - st);
            Kind = SqlTok.Number; Text = t;
            if (!real && long.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var l)) { Int = l; IsInt = true; Dbl = l; }
            else { Dbl = double.Parse(t, NumberStyles.Float, CultureInfo.InvariantCulture); IsInt = false; }
        }

        void ScanString(char q)
        {
            int p = i + 1;
            while (p < s.Length)
            {
                char c = s[p++];
                if (c == '\\') { p++; continue; }
                if (c == q)
                {
                    if (p < s.Length && s[p] == q) { p++; continue; }
                    ls = i + 1; le = p - 1; lq = q; lazy = true; text = null; i = p; return;
                }
            }
            throw new ClassicSqlUnsupported("unterminated string");
        }

        string Unescape(int from, int to, char q)
        {
            var sb = new StringBuilder(to - from);
            int i = from;
            while (i < to)
            {
                char c = s[i++];
                if (c == '\\' && i < to)
                {
                    char e = s[i++];
                    switch (e)
                    {
                        case '0': sb.Append('\0'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'Z': sb.Append('\u001a'); break;
                        case '%': sb.Append("\\%"); break;   // MySQL keeps the backslash for \% and \_ (they only matter in LIKE)
                        case '_': sb.Append("\\_"); break;
                        default: sb.Append(e); break;
                    }
                    continue;
                }
                if (c == q && i < to && s[i] == q) { i++; }
                sb.Append(c);
            }
            return sb.ToString();
        }
        // a plain literal (number, string, NULL) directly followed by , or ) is skipped without being evaluated: the fast path for columns we do not keep
        public bool TrySkipLiteral()
        {
            if (Kind != SqlTok.Number && Kind != SqlTok.String && !IsWord("NULL")) return false;
            int j = i;
            while (j < s.Length && char.IsWhiteSpace(s[j])) j++;
            if (j < s.Length && (s[j] == ',' || s[j] == ')')) { Next(); return true; }
            return false;
        }

        public ClassicSqlLexer Clone() => (ClassicSqlLexer)MemberwiseClone();

        public void CopyFrom(ClassicSqlLexer o)
        {
            i = o.i; Kind = o.Kind; text = o.text; lazy = o.lazy; ls = o.ls; le = o.le; lq = o.lq;
            Quoted = o.Quoted; Int = o.Int; Dbl = o.Dbl; IsInt = o.IsInt;
        }

        // like TrySkipLiteral, but hands the value over (null for NULL)
        public bool TryLiteral(out object v)
        {
            v = null;
            if (Kind != SqlTok.Number && Kind != SqlTok.String && !IsWord("NULL")) return false;
            int j = i;
            while (j < s.Length && char.IsWhiteSpace(s[j])) j++;
            if (!(j < s.Length && (s[j] == ',' || s[j] == ')'))) return false;
            if (Kind == SqlTok.Number) v = IsInt ? (object)Int : Dbl;
            else if (Kind == SqlTok.String) v = Text;
            Next();
            return true;
        }
    }
}
