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
        public static Dictionary<string, object> ReadGlobals(string text)
        {
            var result = new Dictionary<string, object>();
            int i = 0;
            while (true)
            {
                Skip(text, ref i);
                if (i >= text.Length) break;
                var name = Ident(text, ref i);
                Skip(text, ref i);
                if (name.Length == 0 || i >= text.Length || text[i] != '=') break;
                i++;
                result[name] = Value(text, ref i);
            }
            return result;
        }

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

        static object Value(string s, ref int i)
        {
            Skip(s, ref i);
            char c = s[i];
            if (c == '{') return Table(s, ref i);
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
            return double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : (object)num;
        }

        static Dictionary<string, object> Table(string s, ref int i)
        {
            var t = new Dictionary<string, object>();
            i++; // {
            int index = 1;
            while (true)
            {
                Skip(s, ref i);
                if (s[i] == '}') { i++; return t; }
                if (s[i] == '[')
                {
                    i++;
                    var key = Value(s, ref i);
                    Skip(s, ref i);
                    i++; // ]
                    Skip(s, ref i);
                    i++; // =
                    var k = key is double d ? d.ToString(CultureInfo.InvariantCulture) : key?.ToString() ?? "";
                    t[k] = Value(s, ref i);
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
                        t[name] = Value(s, ref i);
                    }
                    else
                    {
                        i = save;
                        t[(index++).ToString(CultureInfo.InvariantCulture)] = Value(s, ref i);
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
