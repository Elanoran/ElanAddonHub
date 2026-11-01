using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace ElansAddonHub.Lodge
{
    // 3.5.8: colours for a WoW macro in the Library (the preview of a shared macro). A macro is lines: #show / #showtooltip, then slash commands
    // whose arguments may carry [conditions] (units @x / target=x, modifiers, states), alternatives split by ; and , and /run or /script Lua.
    // Known are the commands and conditions of the Classic-era client WoW Forever is built on; anything else is shown plainly, never as an error
    // (an addon can add its own slash commands). The macro window takes 255 characters at most (Limit).
    public static class MacroSyntax
    {
        public const int Limit = 255;

        public enum Kind { Plain, Meta, Command, OtherCommand, Bracket, Condition, Unit, Punct, Option, LuaKeyword, LuaString, LuaNumber }

        static readonly HashSet<string> Commands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // casting and items
            "cast", "castrandom", "castsequence", "use", "userandom", "equip", "equipslot", "stopcasting", "stopspelltarget", "cancelaura", "cancelform", "dismount",
            // targeting
            "target", "tar", "targetenemy", "targetfriend", "targetparty", "targetraid", "targetlasttarget", "targetlastenemy", "targetlastfriend", "targetexact",
            "targetenemyplayer", "targetfriendplayer", "cleartarget", "assist", "a", "focus", "clearfocus", "startattack", "stopattack", "follow", "f",
            // the pet
            "petattack", "petfollow", "petstay", "petpassive", "petdefensive", "petaggressive", "petautocaston", "petautocastoff", "petautocasttoggle", "petdismiss",
            // macro control and scripts
            "stopmacro", "click", "run", "script", "console", "reload", "dump",
            // chat
            "say", "s", "yell", "y", "party", "p", "raid", "ra", "raidwarning", "rw", "guild", "g", "officer", "o", "whisper", "w", "tell", "t", "reply", "r",
            "emote", "e", "me", "instance", "i", "readycheck", "roll", "random", "rnd", "invite", "inv", "uninvite", "kick", "ignore", "who", "afk", "dnd",
            "combatlog", "logout", "camp", "quit", "played", "time", "help",
        };

        static readonly HashSet<string> Conditions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "help", "harm", "exists", "dead", "combat", "mod", "modifier", "btn", "button", "stance", "form", "stealth", "mounted", "swimming", "flying", "flyable",
            "indoors", "outdoors", "channeling", "group", "party", "raid", "pet", "equipped", "worn", "actionbar", "bar", "bonusbar", "known", "resting",
        };

        static readonly HashSet<string> LuaKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "and", "break", "do", "else", "elseif", "end", "false", "for", "function", "if", "in", "local", "nil", "not", "or", "repeat", "return", "then", "true", "until", "while",
        };

        public static bool IsKnownCommand(string name) => Commands.Contains((name ?? "").TrimStart('/'));

        // the macro as coloured pieces, line by line (line breaks are "\n" pieces)
        public static List<(string Text, Kind Kind)> Tokens(string macro)
        {
            var o = new List<(string, Kind)>();
            var lines = (macro ?? "").Replace("\r\n", "\n").Split('\n');
            for (int li = 0; li < lines.Length; li++)
            {
                if (li > 0) o.Add(("\n", Kind.Plain));
                Line(lines[li], o);
            }
            return o;
        }

        static void Line(string line, List<(string, Kind)> o)
        {
            int lead = 0; while (lead < line.Length && char.IsWhiteSpace(line[lead])) lead++;
            if (lead > 0) o.Add((line.Substring(0, lead), Kind.Plain));
            var rest = line.Substring(lead);
            if (rest.Length == 0) return;
            if (rest.StartsWith("#")) { var m = Regex.Match(rest, @"^#\S+"); o.Add((m.Value, Kind.Meta)); Args(rest.Substring(m.Length), o); return; }
            if (!rest.StartsWith("/")) { o.Add((rest, Kind.Plain)); return; }
            var cm = Regex.Match(rest, @"^/\S+");
            var name = cm.Value.Substring(1);
            o.Add((cm.Value, IsKnownCommand(name) ? Kind.Command : Kind.OtherCommand));
            var args = rest.Substring(cm.Length);
            if (name.Equals("run", StringComparison.OrdinalIgnoreCase) || name.Equals("script", StringComparison.OrdinalIgnoreCase)) Lua(args, o);
            else Args(args, o);
        }

        // [conditions] spell or item ; [conditions] ... , reset=... for castsequence
        static void Args(string s, List<(string, Kind)> o)
        {
            int i = 0;
            var plain = new System.Text.StringBuilder();
            void Flush() { if (plain.Length > 0) { o.Add((plain.ToString(), Kind.Plain)); plain.Clear(); } }
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '[')
                {
                    Flush();
                    int end = s.IndexOf(']', i);
                    if (end < 0) end = s.Length - 1;
                    Bracket(s.Substring(i, end - i + 1), o);
                    i = end + 1;
                }
                else if (c == ';' || c == ',') { Flush(); o.Add((c.ToString(), Kind.Punct)); i++; }
                else if (string.Compare(s, i, "reset=", 0, 6, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    Flush();
                    int end = i; while (end < s.Length && !char.IsWhiteSpace(s[end])) end++;
                    o.Add((s.Substring(i, end - i), Kind.Option)); i = end;
                }
                else { plain.Append(c); i++; }
            }
            Flush();
        }

        static void Bracket(string b, List<(string, Kind)> o)
        {
            o.Add(("[", Kind.Bracket));
            var inner = b.TrimStart('[').TrimEnd(']');
            var parts = Regex.Split(inner, @"(,)");
            foreach (var p in parts)
            {
                if (p == ",") { o.Add((",", Kind.Punct)); continue; }
                if (p.Length == 0) continue;
                var t = p.Trim();
                var lead = p.Substring(0, p.IndexOf(t, StringComparison.Ordinal) < 0 ? 0 : p.IndexOf(t, StringComparison.Ordinal));
                if (lead.Length > 0) o.Add((lead, Kind.Plain));
                if (t.StartsWith("@") || t.StartsWith("target=", StringComparison.OrdinalIgnoreCase)) o.Add((t, Kind.Unit));
                else
                {
                    var word = Regex.Match(t, @"^(no)?([a-z]+)", RegexOptions.IgnoreCase);
                    var known = word.Success && (Conditions.Contains(word.Groups[2].Value) || Conditions.Contains(word.Value));
                    o.Add((t, known ? Kind.Condition : Kind.Plain));
                }
                var tail = p.Substring(lead.Length + t.Length);
                if (tail.Length > 0) o.Add((tail, Kind.Plain));
            }
            if (b.EndsWith("]")) o.Add(("]", Kind.Bracket));
        }

        static void Lua(string s, List<(string, Kind)> o)
        {
            foreach (Match m in Regex.Matches(s, @"(""(?:\\.|[^""\\])*""?|'(?:\\.|[^'\\])*'?)|(\b\d+(?:\.\d+)?\b)|([A-Za-z_][A-Za-z0-9_]*)|(\s+|.)"))
            {
                if (m.Groups[1].Success) o.Add((m.Value, Kind.LuaString));
                else if (m.Groups[2].Success) o.Add((m.Value, Kind.LuaNumber));
                else if (m.Groups[3].Success) o.Add((m.Value, LuaKeywords.Contains(m.Value) ? Kind.LuaKeyword : Kind.Plain));
                else o.Add((m.Value, Kind.Plain));
            }
        }

        static string BrushOf(Kind k) =>
            k == Kind.Meta ? "Brush.Code.Meta" : k == Kind.Command ? "Brush.Code.Command" : k == Kind.OtherCommand ? "Brush.Code.OtherCommand"
            : k == Kind.Bracket || k == Kind.Punct ? "Brush.Code.Punct" : k == Kind.Condition ? "Brush.Code.Condition" : k == Kind.Unit ? "Brush.Code.Unit"
            : k == Kind.Option ? "Brush.Code.Condition" : k == Kind.LuaKeyword ? "Brush.Code.Meta" : k == Kind.LuaString ? "Brush.Code.String" : k == Kind.LuaNumber ? "Brush.Code.Number"
            : "Brush.Text.Primary";

        // TextBlock.(MacroSyntax.Code): the text, coloured when it is a macro (Kind "macro"), else plain
        public static readonly DependencyProperty CodeProperty = DependencyProperty.RegisterAttached("Code", typeof(string), typeof(MacroSyntax), new PropertyMetadata(null, Changed));
        public static readonly DependencyProperty IsMacroProperty = DependencyProperty.RegisterAttached("IsMacro", typeof(bool), typeof(MacroSyntax), new PropertyMetadata(false, Changed));
        public static string GetCode(DependencyObject d) => (string)d.GetValue(CodeProperty);
        public static void SetCode(DependencyObject d, string v) => d.SetValue(CodeProperty, v);
        public static bool GetIsMacro(DependencyObject d) => (bool)d.GetValue(IsMacroProperty);
        public static void SetIsMacro(DependencyObject d, bool v) => d.SetValue(IsMacroProperty, v);

        static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is TextBlock tb)) return;
            var text = GetCode(tb) ?? "";
            tb.Inlines.Clear();
            if (!GetIsMacro(tb)) { tb.Inlines.Add(new Run(text)); return; }
            foreach (var (t, k) in Tokens(text))
            {
                if (t == "\n") { tb.Inlines.Add(new LineBreak()); continue; }
                var r = new Run(t);
                r.SetResourceReference(TextElement.ForegroundProperty, BrushOf(k));
                tb.Inlines.Add(r);
            }
        }
    }
}
