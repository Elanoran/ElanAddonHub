using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ElansAddonHub.Services.Atlas.Classic
{
    delegate object SqlEv(object[] row);

    // SQL values are null, long, double or string (booleans are 1 / 0). Comparisons and arithmetic follow MySQL closely enough for the
    // WHERE / SET / VALUES expressions found in the cMaNGOS update scripts: three-valued logic, numbers before strings, case-insensitive text.
    static class SqlVal
    {
        public static bool IsNum(object o) => o is long || o is double;

        public static double ToDouble(object o)
        {
            switch (o)
            {
                case null: return 0;
                case long l: return l;
                case double d: return d;
                case string s:
                    {
                        // MySQL reads the numeric prefix of a string ("12abc" = 12, "abc" = 0)
                        int i = 0; s = s.Trim();
                        while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == '-' || s[i] == '+' || s[i] == 'e' || s[i] == 'E')) i++;
                        for (int n = i; n > 0; n--)
                            if (double.TryParse(s.Substring(0, n), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
                        return 0;
                    }
            }
            return 0;
        }

        public static long ToLong(object o)
        {
            if (o is long l) return l;
            var d = ToDouble(o);
            if (double.IsNaN(d)) return 0;
            if (d >= long.MaxValue) return long.MaxValue;
            if (d <= long.MinValue) return long.MinValue;
            return (long)Math.Round(d, MidpointRounding.AwayFromZero);
        }

        public static string ToStr(object o)
        {
            switch (o)
            {
                case null: return null;
                case string s: return s;
                case long l: return l.ToString(CultureInfo.InvariantCulture);
                case double d: return d.ToString("R", CultureInfo.InvariantCulture);
            }
            return o.ToString();
        }

        // null = unknown
        public static bool? Truth(object o)
        {
            if (o == null) return null;
            if (o is string s) return ToDouble(s) != 0;
            return ToDouble(o) != 0;
        }

        public static readonly object True = 1L, False = 0L;
        public static object Bool(bool? b) => b == null ? null : b.Value ? True : False;

        public static int? Compare(object a, object b)
        {
            if (a == null || b == null) return null;
            if (a is long x && b is long y) return x.CompareTo(y);
            if (a is string s1 && b is string s2) return string.Compare(s1, s2, StringComparison.OrdinalIgnoreCase);
            return ToDouble(a).CompareTo(ToDouble(b));
        }

        public static object Arith(string op, object a, object b)
        {
            if (a == null || b == null) return null;
            bool ints = !(a is double) && !(b is double) && !(a is string && ToDouble(a) != Math.Floor(ToDouble(a))) && !(b is string && ToDouble(b) != Math.Floor(ToDouble(b)));
            switch (op)
            {
                case "+": return ints ? (object)(ToLong(a) + ToLong(b)) : ToDouble(a) + ToDouble(b);
                case "-": return ints ? (object)(ToLong(a) - ToLong(b)) : ToDouble(a) - ToDouble(b);
                case "*": return ints ? (object)(ToLong(a) * ToLong(b)) : ToDouble(a) * ToDouble(b);
                case "/": { var d = ToDouble(b); return d == 0 ? null : (object)(ToDouble(a) / d); }
                case "DIV": { var d = ToDouble(b); return d == 0 ? null : (object)(long)Math.Truncate(ToDouble(a) / d); }
                case "%": case "MOD": { var d = ToLong(b); return d == 0 ? null : (object)(ToLong(a) % d); }
                case "|": return ToLong(a) | ToLong(b);
                case "&": return ToLong(a) & ToLong(b);
                case "^": return ToLong(a) ^ ToLong(b);
                case "<<": return ToLong(a) << (int)ToLong(b);
                case ">>": return ToLong(a) >> (int)ToLong(b);
            }
            throw new ClassicSqlUnsupported("operator " + op);
        }

        public static bool Like(string text, string pattern)
        {
            if (text == null || pattern == null) return false;
            var sb = new StringBuilder("^");
            for (int i = 0; i < pattern.Length; i++)
            {
                char c = pattern[i];
                if (c == '\\' && i + 1 < pattern.Length) { sb.Append(Regex.Escape(pattern[++i].ToString())); continue; }
                if (c == '%') sb.Append("(?s:.*)");
                else if (c == '_') sb.Append("(?s:.)");
                else sb.Append(Regex.Escape(c.ToString()));
            }
            sb.Append('$');
            return Regex.IsMatch(text, sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
    }

    // Recursive-descent expression compiler: turns the tokens into a closure that evaluates against a row (object[] of the stored columns).
    // Anything it cannot evaluate faithfully (functions, sub-selects, columns we do not keep) throws ClassicSqlUnsupported so the caller skips the statement.
    sealed class ClassicSqlExpr
    {
        readonly ClassicSqlLexer lx;
        readonly Func<string, int> column;                 // stored index of a column; -1 = not kept
        readonly Dictionary<string, object> vars;
        readonly string table;
        public static readonly object UnknownVar = new object();

        public ClassicSqlExpr(ClassicSqlLexer lexer, string tableName, Func<string, int> columnIndex, Dictionary<string, object> variables)
        { lx = lexer; table = tableName; column = columnIndex; vars = variables; }

        public SqlEv Parse() => Or();

        sealed class ConstEv { public readonly object V; public ConstEv(object v) { V = v; } public object Eval(object[] row) => V; }
        static SqlEv Const(object v) => new ConstEv(v).Eval;

        SqlEv Or()
        {
            var l = And();
            while (lx.IsWord("OR") || lx.IsPunct("||"))
            {
                lx.Next();
                var a = l; var b = And();
                l = row =>
                {
                    var x = SqlVal.Truth(a(row)); if (x == true) return SqlVal.True;
                    var y = SqlVal.Truth(b(row)); if (y == true) return SqlVal.True;
                    return x == null || y == null ? null : SqlVal.False;
                };
            }
            return l;
        }

        SqlEv And()
        {
            var l = Not();
            while (lx.IsWord("AND") || lx.IsPunct("&&"))
            {
                lx.Next();
                var a = l; var b = Not();
                l = row =>
                {
                    var x = SqlVal.Truth(a(row)); if (x == false) return SqlVal.False;
                    var y = SqlVal.Truth(b(row)); if (y == false) return SqlVal.False;
                    return x == null || y == null ? null : SqlVal.True;
                };
            }
            return l;
        }

        SqlEv Not()
        {
            if (lx.IsWord("NOT") || lx.IsPunct("!"))
            {
                lx.Next();
                var a = Not();
                return row => { var t = SqlVal.Truth(a(row)); return t == null ? null : SqlVal.Bool(!t.Value); };
            }
            return Cmp();
        }

        SqlEv Cmp()
        {
            var l = BitOr();
            while (true)
            {
                if (lx.IsWord("IS"))
                {
                    lx.Next();
                    bool neg = lx.AcceptWord("NOT");
                    lx.ExpectWord("NULL");
                    var a = l;
                    l = row => SqlVal.Bool((a(row) == null) != neg);
                    continue;
                }
                bool not = false;
                if (lx.IsWord("NOT")) { lx.Next(); not = true; if (!(lx.IsWord("IN") || lx.IsWord("BETWEEN") || lx.IsWord("LIKE"))) throw new ClassicSqlUnsupported("NOT near '" + lx.Near() + "'"); }
                if (lx.AcceptWord("IN"))
                {
                    lx.ExpectPunct("(");
                    if (lx.IsWord("SELECT")) throw new ClassicSqlUnsupported("sub-select");
                    var items = new List<SqlEv>();
                    do { items.Add(Or()); } while (lx.AcceptPunct(","));
                    lx.ExpectPunct(")");
                    var a = l; bool n = not;
                    // a list of integer constants (the usual `item IN (1, 2, 3)`): one hash lookup instead of a walk through closures
                    if (items.All(it => it.Target is ConstEv ce && ce.V is long))
                    {
                        var set = new HashSet<long>(items.Select(it => (long)((ConstEv)it.Target).V));
                        l = row =>
                        {
                            var v = a(row);
                            if (v == null) return null;
                            if (v is long lv) return SqlVal.Bool(set.Contains(lv) != n);
                            var d = SqlVal.ToDouble(v);
                            return SqlVal.Bool((d == Math.Floor(d) && set.Contains((long)d)) != n);
                        };
                        continue;
                    }
                    l = row =>
                    {
                        var v = a(row);
                        if (v == null) return null;
                        bool sawNull = false;
                        foreach (var it in items)
                        {
                            var c = SqlVal.Compare(v, it(row));
                            if (c == null) sawNull = true;
                            else if (c == 0) return SqlVal.Bool(!n);
                        }
                        return sawNull ? null : SqlVal.Bool(n);
                    };
                    continue;
                }
                if (lx.AcceptWord("BETWEEN"))
                {
                    var lo = BitOr(); lx.ExpectWord("AND"); var hi = BitOr();
                    var a = l; bool n = not;
                    l = row =>
                    {
                        var v = a(row);
                        var c1 = SqlVal.Compare(v, lo(row)); var c2 = SqlVal.Compare(v, hi(row));
                        if (c1 == null || c2 == null) return null;
                        return SqlVal.Bool((c1 >= 0 && c2 <= 0) != n);
                    };
                    continue;
                }
                if (lx.AcceptWord("LIKE"))
                {
                    var p = BitOr(); var a = l; bool n = not;
                    l = row => { var t = a(row); var pat = p(row); if (t == null || pat == null) return null; return SqlVal.Bool(SqlVal.Like(SqlVal.ToStr(t), SqlVal.ToStr(pat)) != n); };
                    continue;
                }
                if (lx.Kind == SqlTok.Punct)
                {
                    string op = lx.Text;
                    if (op == "=" || op == "<=>" || op == "<>" || op == "!=" || op == "<" || op == ">" || op == "<=" || op == ">=")
                    {
                        lx.Next();
                        var a = l; var b = BitOr();
                        l = row =>
                        {
                            var x = a(row); var y = b(row);
                            if (op == "<=>") return SqlVal.Bool(x == null || y == null ? x == null && y == null : SqlVal.Compare(x, y) == 0);
                            var c = SqlVal.Compare(x, y);
                            if (c == null) return null;
                            switch (op)
                            {
                                case "=": return SqlVal.Bool(c == 0);
                                case "<>": case "!=": return SqlVal.Bool(c != 0);
                                case "<": return SqlVal.Bool(c < 0);
                                case ">": return SqlVal.Bool(c > 0);
                                case "<=": return SqlVal.Bool(c <= 0);
                                default: return SqlVal.Bool(c >= 0);
                            }
                        };
                        continue;
                    }
                }
                return l;
            }
        }

        SqlEv Binary(Func<SqlEv> next, params string[] ops)
        {
            var l = next();
            while (true)
            {
                string found = null;
                foreach (var o in ops)
                    if (o.Length > 1 && char.IsLetter(o[0]) ? lx.IsWord(o) : lx.IsPunct(o)) { found = o; break; }
                if (found == null) return l;
                lx.Next();
                var a = l; var b = next(); var op = found;
                l = row => SqlVal.Arith(op, a(row), b(row));
            }
        }

        SqlEv BitOr() => Binary(BitAnd, "|");
        SqlEv BitAnd() => Binary(Shift, "&");
        SqlEv Shift() => Binary(Add, "<<", ">>");
        SqlEv Add() => Binary(Mul, "+", "-");
        SqlEv Mul() => Binary(Unary, "*", "/", "%", "DIV", "MOD");

        SqlEv Unary()
        {
            if (lx.IsPunct("-"))
            {
                lx.Next();
                var a = Unary();
                return row => { var v = a(row); if (v == null) return null; if (v is long l) return -l; return -SqlVal.ToDouble(v); };
            }
            if (lx.IsPunct("+")) { lx.Next(); return Unary(); }
            if (lx.IsPunct("~")) { lx.Next(); var a = Unary(); return row => { var v = a(row); return v == null ? null : (object)(~SqlVal.ToLong(v)); }; }
            return Primary();
        }

        SqlEv Primary()
        {
            switch (lx.Kind)
            {
                case SqlTok.Number:
                    {
                        object v = lx.IsInt ? (object)lx.Int : lx.Dbl;
                        lx.Next();
                        return Const(v);
                    }
                case SqlTok.String:
                    {
                        var v = lx.Text; lx.Next();
                        return Const(v);
                    }
                case SqlTok.Var:
                    {
                        var name = lx.Text; lx.Next();
                        if (lx.IsPunct(":="))
                        {
                            // @v := expr inside a value list: evaluated now (constants only), the variable keeps the value for the statements that follow
                            if (column != null) throw new ClassicSqlUnsupported("assignment inside an expression");
                            lx.Next();
                            var val = Or()(null);
                            vars[name] = val;
                            return Const(val);
                        }
                        if (vars.TryGetValue(name, out var v))
                        {
                            if (ReferenceEquals(v, UnknownVar)) throw new ClassicSqlUnsupported("variable @" + name + " has no known value");
                            return Const(v);
                        }
                        return Const(null);
                    }
                case SqlTok.Punct:
                    if (lx.IsPunct("("))
                    {
                        lx.Next();
                        if (lx.IsWord("SELECT")) throw new ClassicSqlUnsupported("sub-select");
                        var e = Or();
                        lx.ExpectPunct(")");
                        return e;
                    }
                    break;
                case SqlTok.Ident:
                    {
                        if (!lx.Quoted)
                        {
                            if (lx.IsWord("NULL")) { lx.Next(); return Const(null); }
                            if (lx.IsWord("TRUE")) { lx.Next(); return Const(SqlVal.True); }
                            if (lx.IsWord("FALSE")) { lx.Next(); return Const(SqlVal.False); }
                            if (lx.IsWord("SELECT")) throw new ClassicSqlUnsupported("sub-select");
                        }
                        var name = lx.Text; bool quoted = lx.Quoted; lx.Next();
                        if (!quoted && lx.IsPunct("(")) throw new ClassicSqlUnsupported("function " + name.ToUpperInvariant());
                        if (lx.IsPunct("."))
                        {
                            // table.column (or db.table.column): the last part is the column; the table part must be this statement's table
                            lx.Next();
                            if (lx.Kind != SqlTok.Ident) throw new ClassicSqlUnsupported("expression near '" + lx.Near() + "'");
                            var second = lx.Text; lx.Next();
                            if (lx.IsPunct(".")) { lx.Next(); if (lx.Kind != SqlTok.Ident) throw new ClassicSqlUnsupported("expression"); second = lx.Text; lx.Next(); name = null; }
                            if (name != null && !string.Equals(name, table, StringComparison.OrdinalIgnoreCase)) throw new ClassicSqlUnsupported("column of another table " + name + "." + second);
                            name = second;
                        }
                        if (column == null) throw new ClassicSqlUnsupported("column " + name + " in a value list");
                        int idx = column(name);
                        if (idx < 0) throw new ClassicSqlUnsupported("column " + name + " is not kept");
                        return row => row[idx];
                    }
            }
            throw new ClassicSqlUnsupported("expression near '" + lx.Near() + "'");
        }
    }
}
