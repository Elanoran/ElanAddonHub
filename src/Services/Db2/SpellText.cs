using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ElansAddonHub.Services.Db2
{
    // Spell descriptions hold tokens that the client fills in from the spell's effects: "Deals $s1 damage over $d." This fills in the common ones from the
    // spell's own data and leaves every token it cannot work out EXACTLY as written (so the line stays readable and nothing wrong is shown):
    //   $s1 $s2 $s3 / $m1 / $M1   the amount of effect n          $o1   the total of a periodic effect over its duration ($s1 x duration / period)
    //   $t1                       seconds between ticks            $d    the duration ("10 sec", "2 min")
    //   $a1 radius (yards), $x1 chain targets, $q1 misc value
    //   $12345s1 / $12345d        the same, of another spell       $@spellname123  the name of spell 123
    //   ${$m1/1000}               arithmetic over the above (+ - * / and brackets), an optional ".1" for the decimals
    //   $l singular:plural;       by the number just before it     $g he:she;  (the first form)
    //   $?s123[yes][no]           what the character knows is not known here: the last (else) branch is shown
    // An amount that the game scales with the character (a coefficient and no base value) is not known either and stays a token.
    public static class SpellText
    {
        const int MaxDepth = 6;
        sealed class Ctx { public double Last; public bool Has; }

        public static string Expand(string text, SpellInfo self, Func<int, SpellInfo> lookup)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf('$') < 0) return text ?? "";
            try { return Run(text, self, lookup, 0, new Ctx()); }
            catch (Exception) { return text; }
        }

        static string Run(string t, SpellInfo self, Func<int, SpellInfo> lookup, int depth, Ctx cx)
        {
            var sb = new StringBuilder(t.Length + 16);
            int i = 0;
            while (i < t.Length)
            {
                char c = t[i];
                if (c != '$') { sb.Append(c); i++; continue; }
                if (i + 1 >= t.Length) { sb.Append(c); i++; continue; }
                char n = t[i + 1];
                if (n == '$') { sb.Append('$'); i += 2; continue; }
                if (n == '{') { i = Arith(t, i, sb, self, lookup, depth, cx); continue; }
                if (n == '?') { i = Conditional(t, i, sb, self, lookup, depth, cx); continue; }
                if (n == '@') { i = Named(t, i, sb, lookup); continue; }
                if (n == 'g' || n == 'G') { i = Pair(t, i, sb, false, cx); continue; }
                if (n == 'l' || n == 'L') { i = Pair(t, i, sb, true, cx); continue; }
                int start = i; int p = i + 1;
                // $/1000;s1 and $*100;s1: the token after the ';' divided / multiplied
                char op = ' '; double operand = 1;
                if (n == '/' || n == '*')
                {
                    int semi = t.IndexOf(';', p + 1);
                    if (semi > p + 1 && double.TryParse(t.Substring(p + 1, semi - p - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out operand) && operand != 0) { op = n; p = semi + 1; }
                    else { sb.Append(c); i++; continue; }
                }
                int spellDigits = 0, spellId = 0;
                while (p < t.Length && char.IsDigit(t[p]) && spellDigits < 9) { spellId = spellId * 10 + (t[p] - '0'); p++; spellDigits++; }
                if (p >= t.Length || !char.IsLetter(t[p])) { sb.Append(t, start, p - start); i = p; continue; }
                char kind = t[p++];
                int idx = 0;
                if (p < t.Length && char.IsDigit(t[p])) { idx = t[p] - '0'; p++; }
                string value = null;
                SpellInfo sp = self;
                if (spellDigits > 0) sp = lookup == null ? null : lookup(spellId);
                if (sp != null) value = Value(kind, idx, sp);
                if (value != null && op != ' ')
                {
                    if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double basis)) value = Num(op == '/' ? basis / operand : basis * operand);
                    else value = null;
                }
                if (value == null) sb.Append(t, start, p - start);
                else
                {
                    sb.Append(value);
                    if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double nv)) { cx.Last = nv; cx.Has = true; }
                }
                i = p;
            }
            return sb.ToString();
        }

        // the text of one token, or null = unknown (the token stays as it was written)
        static string Value(char kind, int idx, SpellInfo s)
        {
            switch (kind)
            {
                case 'd': case 'D': return s.DurationMs == 0 ? null : Duration(s.DurationMs);
                case 's': case 'S': case 'm': case 'M':
                    {
                        var e = s.Effect(idx == 0 ? 1 : idx);
                        if (e == null) return null;
                        if (Math.Abs(e.Points) < 0.5f) return null;      // scaled by the character (or no base amount): not known here
                        return Num(Math.Abs(Math.Round(e.Points, MidpointRounding.AwayFromZero)));
                    }
                case 'o': case 'O':
                    {
                        var e = s.Effect(idx == 0 ? 1 : idx);
                        if (e == null || Math.Abs(e.Points) < 0.5f || e.PeriodMs <= 0 || s.DurationMs <= 0) return null;
                        return Num(Math.Abs(Math.Round(e.Points * (s.DurationMs / (double)e.PeriodMs), MidpointRounding.AwayFromZero)));
                    }
                case 't': case 'T':
                    {
                        var e = s.Effect(idx == 0 ? 1 : idx);
                        return e == null || e.PeriodMs <= 0 ? null : Num(e.PeriodMs / 1000.0);
                    }
                case 'a': case 'A':
                    {
                        var e = s.Effect(idx == 0 ? 1 : idx);
                        return e == null || e.Radius <= 0 ? null : Num(e.Radius);
                    }
                case 'x': case 'X':
                    {
                        var e = s.Effect(idx == 0 ? 1 : idx);
                        return e == null || e.Chain <= 0 ? null : Num(e.Chain);
                    }
                case 'q': case 'Q':
                    {
                        var e = s.Effect(idx == 0 ? 1 : idx);
                        return e == null || e.Misc == 0 ? null : Num(Math.Abs(e.Misc));
                    }
                case 'z': return "your home location";     // $z: the place the Hearthstone is bound to (not known here)
                default: return null;
            }
        }

        public static string Num(double v)
        {
            if (Math.Abs(v - Math.Round(v)) < 0.0005) return Math.Round(v).ToString("0", CultureInfo.InvariantCulture);
            return v.ToString("0.##", CultureInfo.InvariantCulture);
        }

        public static string Duration(int ms)
        {
            if (ms < 0) return "until cancelled";
            long s = (ms + 500) / 1000;
            if (s < 60) return s + " sec";
            if (s < 3600) return s % 60 == 0 ? s / 60 + " min" : s + " sec";
            if (s < 86400) return s % 3600 == 0 ? s / 3600 + (s / 3600 == 1 ? " hour" : " hours") : s / 60 + " min";
            return s / 86400 + (s / 86400 == 1 ? " day" : " days");
        }

        // ${ ... }[.n]
        static int Arith(string t, int i, StringBuilder sb, SpellInfo self, Func<int, SpellInfo> lookup, int depth, Ctx cx)
        {
            int close = Matching(t, i + 1, '{', '}');
            if (close < 0) { sb.Append(t[i]); return i + 1; }
            string inner = t.Substring(i + 2, close - i - 2);
            int end = close + 1, decimals = -1;
            if (end + 1 < t.Length && t[end] == '.' && char.IsDigit(t[end + 1])) { decimals = t[end + 1] - '0'; end += 2; }
            string filled = depth >= MaxDepth ? inner : Run(inner, self, lookup, depth + 1, new Ctx());
            if (TryEval(filled, out double v))
            {
                if (decimals >= 0) sb.Append(Math.Round(v, Math.Min(decimals, 6)).ToString("0." + new string('0', Math.Min(decimals, 6)), CultureInfo.InvariantCulture).TrimEnd('.'));
                else sb.Append(Num(v));
                cx.Last = v; cx.Has = true;
            }
            else sb.Append(t, i, end - i);
            return end;
        }

        // index of the bracket that closes the one at `open`, or -1
        static int Matching(string t, int open, char o, char c)
        {
            int depth = 0;
            for (int k = open; k < t.Length; k++)
            {
                if (t[k] == o) depth++;
                else if (t[k] == c && --depth == 0) return k;
            }
            return -1;
        }

        // $?s123[a][b]  /  $?a1[a]?s2[b][c]: the character is not known, so the branch without a condition (the last one) is shown
        static int Conditional(string t, int i, StringBuilder sb, SpellInfo self, Func<int, SpellInfo> lookup, int depth, Ctx cx)
        {
            int p = i + 2;
            string elseText = "";
            bool any = false;
            while (p < t.Length)
            {
                // a condition runs up to the '['
                int br = t.IndexOf('[', p);
                if (br < 0) break;
                int close = Matching(t, br, '[', ']');
                if (close < 0) break;
                any = true;
                p = close + 1;
                // a following '[' is the else branch; a following '?' is another condition
                if (p < t.Length && t[p] == '[')
                {
                    int c2 = Matching(t, p, '[', ']');
                    if (c2 < 0) break;
                    elseText = t.Substring(p + 1, c2 - p - 1);
                    p = c2 + 1;
                    break;
                }
                if (p < t.Length && t[p] == '?') { p++; continue; }
                break;
            }
            if (!any) { sb.Append(t[i]); return i + 1; }
            sb.Append(depth >= MaxDepth ? elseText : Run(elseText, self, lookup, depth + 1, cx));
            return p;
        }

        // $@spellname123
        static int Named(string t, int i, StringBuilder sb, Func<int, SpellInfo> lookup)
        {
            const string name = "$@spellname";
            if (string.CompareOrdinal(t, i, name, 0, name.Length) == 0)
            {
                int p = i + name.Length, id = 0, digits = 0;
                while (p < t.Length && char.IsDigit(t[p]) && digits < 9) { id = id * 10 + (t[p] - '0'); p++; digits++; }
                var sp = digits > 0 && lookup != null ? lookup(id) : null;
                if (sp != null && !string.IsNullOrEmpty(sp.Name)) { sb.Append(sp.Name); return p; }
                sb.Append(t, i, p - i);
                return p;
            }
            sb.Append(t[i]);
            return i + 1;
        }

        // "$l sec:secs;" picks by the number written just before (1 = first form); "$g he:she;" always takes the first form
        static int Pair(string t, int i, StringBuilder sb, bool plural, Ctx cx)
        {
            int p = i + 2;
            int semi = t.IndexOf(';', p);
            if (semi < 0) { sb.Append(t[i]); return i + 1; }
            string body = t.Substring(p, semi - p).Trim();
            int colon = body.IndexOf(':');
            if (colon < 0) { sb.Append(t, i, semi + 1 - i); return semi + 1; }
            string one = body.Substring(0, colon).Trim(), many = body.Substring(colon + 1).Trim();
            if (!plural) { sb.Append(one); return semi + 1; }
            // the number just written by a token ($s3 ...) decides; else the digits at the end of what is written so far
            if (cx.Has) { sb.Append(Math.Abs(cx.Last - 1) < 0.0001 ? one : many); return semi + 1; }
            int e = sb.Length;
            while (e > 0 && sb[e - 1] == ' ') e--;
            int s = e;
            while (s > 0 && (char.IsDigit(sb[s - 1]) || sb[s - 1] == '.')) s--;
            bool isOne = false;
            if (s < e && double.TryParse(sb.ToString(s, e - s), NumberStyles.Float, CultureInfo.InvariantCulture, out double num)) isOne = Math.Abs(num - 1) < 0.0001;
            sb.Append(isOne ? one : many);
            return semi + 1;
        }

        // ---- a tiny arithmetic evaluator: numbers, + - * /, brackets, unary minus
        public static bool TryEval(string s, out double v)
        {
            v = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            int pos = 0;
            try
            {
                v = Sum(s, ref pos);
                SkipWs(s, ref pos);
                return pos == s.Length && !double.IsNaN(v) && !double.IsInfinity(v);
            }
            catch (FormatException) { return false; }
        }

        static void SkipWs(string s, ref int p) { while (p < s.Length && s[p] == ' ') p++; }

        static double Sum(string s, ref int p)
        {
            double v = Prod(s, ref p);
            while (true)
            {
                SkipWs(s, ref p);
                if (p < s.Length && s[p] == '+') { p++; v += Prod(s, ref p); }
                else if (p < s.Length && s[p] == '-') { p++; v -= Prod(s, ref p); }
                else return v;
            }
        }

        static double Prod(string s, ref int p)
        {
            double v = Atom(s, ref p);
            while (true)
            {
                SkipWs(s, ref p);
                if (p < s.Length && s[p] == '*') { p++; v *= Atom(s, ref p); }
                else if (p < s.Length && s[p] == '/') { p++; double d = Atom(s, ref p); if (d == 0) throw new FormatException("division by zero"); v /= d; }
                else return v;
            }
        }

        static double Atom(string s, ref int p)
        {
            SkipWs(s, ref p);
            if (p >= s.Length) throw new FormatException("end");
            if (s[p] == '-') { p++; return -Atom(s, ref p); }
            if (s[p] == '(') { p++; double v = Sum(s, ref p); SkipWs(s, ref p); if (p >= s.Length || s[p] != ')') throw new FormatException(")"); p++; return v; }
            int st = p;
            while (p < s.Length && (char.IsDigit(s[p]) || s[p] == '.')) p++;
            if (p == st) throw new FormatException("number");
            return double.Parse(s.Substring(st, p - st), NumberStyles.Float, CultureInfo.InvariantCulture);
        }
    }
}
