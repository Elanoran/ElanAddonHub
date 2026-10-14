using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ElansAddonHub.Services
{
    // Reads WoW SavedVariables files ("Name = { ["key"] = value, ... }"): tables become
    // Dictionary<string, object> (array parts get keys "1", "2", ...), plus string/double/bool/null.
    public static class LuaData
    {
        // Limits (a SavedVariables file is written by the game from addon data that can include text from other players, so it is not trusted):
        // tables nested deeper than MaxDepth would overflow the stack (which cannot be caught and kills the process), so they are refused; so are
        // files over MaxChars and files with more than MaxNodes values. All three throw FormatException, which every caller already handles.
        public const int MaxDepth = 64;
        public const int MaxChars = 96 * 1024 * 1024;
        public const int MaxNodes = 8_000_000;

        sealed class Budget { public int Nodes; }

        public static Dictionary<string, object> ReadGlobals(string text)
        {
            if (text != null && text.Length > MaxChars) { Util.Log("lua: refused a SavedVariables text of " + text.Length + " characters (limit " + MaxChars + ")"); throw new FormatException("file too large"); }
            var result = new Dictionary<string, object>();
            var budget = new Budget();
            int i = 0;
            try
            {
                while (true)
                {
                    Skip(text, ref i);
                    if (i >= text.Length) break;
                    var name = Ident(text, ref i);
                    Skip(text, ref i);
                    if (name.Length == 0 || i >= text.Length || text[i] != '=') break;
                    i++;
                    result[name] = Value(text, ref i, 0, budget);
                }
            }
            catch (IndexOutOfRangeException) { throw new FormatException("unexpected end of file"); }   // a cut-off file: the same error as any other damaged file
            return result;
        }

        // a value of a parsed table as text whatever its Lua type ("5" for the number 5, "true"); null when absent. (Json.Str gives null for a non-string.)
        // For numbers use Json.cs's Int / Long / Bool / Child on the same tables.
        public static string Text(this Dictionary<string, object> t, string k) =>
            t != null && t.TryGetValue(k, out var v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;

        static void Skip(string s, ref int i)
        {
            while (i < s.Length)
            {
                if (char.IsWhiteSpace(s[i]) || s[i] == ',' || s[i] == ';') { i++; continue; }
                if (s[i] == '-' && i + 1 < s.Length && s[i + 1] == '-') { while (i < s.Length && s[i] != '\n') i++; continue; }
                break;
            }
        }

        static string Ident(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
            return s.Substring(start, i - start);
        }

        static object Value(string s, ref int i, int depth, Budget b)
        {
            if (++b.Nodes > MaxNodes) { Util.Log("lua: refused a SavedVariables file with more than " + MaxNodes + " values"); throw new FormatException("too many values"); }
            Skip(s, ref i);
            if (i >= s.Length) throw new FormatException("unexpected end");
            char c = s[i];
            if (c == '{') return Table(s, ref i, depth + 1, b);
            if (c == '"' || c == '\'') return Str(s, ref i);
            var word = Ident(s, ref i);
            if (word == "true") return true;
            if (word == "false") return false;
            if (word == "nil") return null;
            // a number (Ident stopped at '.', '-' or 'e+' - read the rest)
            int start = i - word.Length;
            i = start;
            while (i < s.Length && "+-0123456789.eExX".IndexOf(s[i]) >= 0) i++;
            var num = s.Substring(start, i - start);
            if (num.Length == 0) throw new FormatException("unexpected '" + s[start] + "' at " + start);   // never loop on garbage (a half-written or damaged file)
            return double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : (object)num;
        }

        static Dictionary<string, object> Table(string s, ref int i, int depth, Budget b)
        {
            var t = new Dictionary<string, object>();
            if (depth > MaxDepth) { Util.Log("lua: refused a SavedVariables file with tables nested deeper than " + MaxDepth); throw new FormatException("tables nested too deep"); }
            i++; // {
            int index = 1;
            while (true)
            {
                Skip(s, ref i);
                if (i >= s.Length) throw new FormatException("unterminated table");
                if (s[i] == '}') { i++; return t; }
                if (s[i] == '[')
                {
                    i++;
                    var key = Value(s, ref i, depth, b);
                    Skip(s, ref i);
                    i++; // ]
                    Skip(s, ref i);
                    i++; // =
                    var k = key is double d ? d.ToString(CultureInfo.InvariantCulture) : key?.ToString() ?? "";
                    t[k] = Value(s, ref i, depth, b);
                }
                else
                {
                    // name = value, or a plain array value
                    int save = i;
                    var name = Ident(s, ref i);
                    Skip(s, ref i);
                    if (name.Length > 0 && i < s.Length && s[i] == '=' && (i + 1 >= s.Length || s[i + 1] != '='))
                    {
                        i++;
                        t[name] = Value(s, ref i, depth, b);
                    }
                    else
                    {
                        i = save;
                        t[(index++).ToString(CultureInfo.InvariantCulture)] = Value(s, ref i, depth, b);
                    }
                }
            }
        }

        static string Str(string s, ref int i)
        {
            char q = s[i++];
            var sb = new StringBuilder();
            while (i < s.Length && s[i] != q)
            {
                char c = s[i++];
                if (c != '\\') { sb.Append(c); continue; }
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case '\n': sb.Append('\n'); break;
                    default:
                        if (char.IsDigit(e))
                        {
                            // \ddd decimal escape
                            int start = i - 1, n = 0;
                            while (i < s.Length && n < 2 && char.IsDigit(s[i])) { i++; n++; }
                            sb.Append((char)int.Parse(s.Substring(start, i - start)));
                        }
                        else sb.Append(e);
                        break;
                }
            }
            i++; // closing quote
            return sb.ToString();
        }
    }
}
