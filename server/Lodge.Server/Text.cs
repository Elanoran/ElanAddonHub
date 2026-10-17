using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Lodge;

// Text cleaning shared by names, chat text and file names (see Names in Security.cs for the basics).
public static partial class Names
{
    // message snippets (reply quotes): the same cleaning as names, bounded to `max` characters with an ellipsis
    public static string Snippet(string text, int max)
    {
        var s = Clean(text);
        return s.Length > max ? Cut(s, max).TrimEnd() + "…" : s;
    }

    // free text (profile about / play times / character names): the same cleaning as names, hard-cut at `max` characters
    public static string Text(string text, int max)
    {
        var r = Clean(text);
        return r.Length > max ? Cut(r, max).TrimEnd() : r;
    }

    public static string Normalize(string name)
    {
        var r = Clean(name);
        return r.Length > Max ? Cut(r, Max).TrimEnd() : r;
    }

    // the cleaned form without the length cut (display names are checked against 2-24 instead of being truncated)
    public static string Full(string text) => Clean(text);

    // cut at `max` UTF-16 units without leaving half of a surrogate pair (emoji) behind
    public static string Cut(string s, int max)
    {
        if (s.Length <= max) return s;
        if (max > 0 && char.IsHighSurrogate(s[max - 1])) max--;
        return s[..max];
    }

    // NFKC, whitespace runs -> one space, control/format/private characters out. A valid surrogate pair (emoji) stays;
    // only an unpaired surrogate is dropped.
    static string Clean(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        string s;
        try { s = name.Normalize(NormalizationForm.FormKC); } catch (ArgumentException) { s = name; } // invalid surrogates
        var sb = new StringBuilder(s.Length);
        bool space = false;
        for (int i = 0; i < s.Length; i++)
        {
            var c = s[i];
            var cat = char.GetUnicodeCategory(c);
            if (char.IsWhiteSpace(c) || cat == UnicodeCategory.SpaceSeparator) { space = sb.Length > 0; continue; }
            if (cat == UnicodeCategory.Surrogate)
            {
                if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    if (space) { sb.Append(' '); space = false; }
                    sb.Append(c).Append(s[i + 1]);
                    i++;
                }
                continue;
            }
            if (cat is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned) continue;
            if (space) { sb.Append(' '); space = false; }
            sb.Append(c);
        }
        return sb.ToString();
    }

    // Common Cyrillic / Greek look-alikes of Latin letters. Only for COMPARISON keys, never shown.
    public static char Latin(char c) => c switch
    {
        'а' => 'a', 'е' => 'e', 'о' => 'o', 'р' => 'p', 'с' => 'c', 'у' => 'y', 'х' => 'x', 'і' => 'i', 'ј' => 'j', 'ѕ' => 's',
        'А' => 'A', 'В' => 'B', 'Е' => 'E', 'К' => 'K', 'М' => 'M', 'Н' => 'H', 'О' => 'O', 'Р' => 'P', 'С' => 'C', 'Т' => 'T', 'Х' => 'X', 'У' => 'Y',
        'Α' => 'A', 'Β' => 'B', 'Ε' => 'E', 'Η' => 'H', 'Ι' => 'I', 'Κ' => 'K', 'Μ' => 'M', 'Ν' => 'N', 'Ο' => 'O', 'Ρ' => 'P', 'Τ' => 'T',
        'Χ' => 'X', 'Υ' => 'Y', 'Ζ' => 'Z', 'ο' => 'o', 'ι' => 'i',
        _ => c,
    };

    // case-, NFKC- and look-alike-insensitive key: a Cyrillic "о" in "Bоb" gives the same key as "bob"
    public static string Key(string name)
    {
        var n = Normalize(name);
        var sb = new StringBuilder(n.Length);
        foreach (var c in n) sb.Append(char.ToLowerInvariant(Latin(c)));
        return sb.ToString();
    }

    public static bool Alike(string a, string b) => string.Equals(Key(a), Key(b), StringComparison.Ordinal);

    // ---- message text

    static bool IsBidi(char c) => (c >= '‪' && c <= '‮') || (c >= '⁦' && c <= '⁩');

    static readonly Regex BlankRun = new(@"\n(?:[ \t]*\n){4,}", RegexOptions.Compiled);
    static readonly Regex GroupPing = new(@"(?<![\p{L}\p{N}_])@(?=(?:everyone|here)\b)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // chat text: bidi overrides/isolates out (they can reverse the text that follows), control characters other than
    // newline and tab out, more than 3 blank lines in a row collapse to 3
    public static string MessageText(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (IsBidi(c)) continue;
            if (char.IsControl(c) && c != '\n' && c != '\r' && c != '\t') continue;
            sb.Append(c);
        }
        return BlankRun.Replace(sb.ToString().Replace("\r\n", "\n").Replace('\r', '\n'), "\n\n\n\n").Trim();
    }

    // @everyone / @here from someone below officer: a zero-width space after the @ stops every client's ping
    public static string DefangGroupPings(string text) => GroupPing.Replace(text, "@​");

    // ---- file names

    static readonly string[] Devices = { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$" };

    // A file name that is safe on any filesystem the file may land on (Windows clients write it to Downloads):
    // path parts, drive letters and the Windows-invalid characters out, control and format characters (bidi, zero
    // width) out, leading/trailing dots and spaces dropped, device names (CON, NUL, COM1...) defused, bounded.
    public static string FileName(string name, int max = 120)
    {
        name ??= "";
        try { name = name.Normalize(NormalizationForm.FormKC); } catch (ArgumentException) { }
        name = name.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        var sb = new StringBuilder(name.Length);
        bool space = false;
        for (int i = 0; i < name.Length; i++)
        {
            var c = name[i];
            var cat = char.GetUnicodeCategory(c);
            if (char.IsWhiteSpace(c) || cat == UnicodeCategory.SpaceSeparator) { space = sb.Length > 0; continue; }
            if (cat is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator) continue;
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
            sb.Append(c is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : c);
        }
        var r = sb.ToString().Trim(' ', '.');
        if (r.Length > max)
        {
            var dot = r.LastIndexOf('.');
            var ext = dot > 0 && r.Length - dot <= 16 ? r[dot..] : "";
            r = Cut(r, max - ext.Length).TrimEnd(' ', '.') + ext;
        }
        if (r.Length == 0) return "file";
        var stem = (r.Contains('.') ? r[..r.IndexOf('.')] : r).TrimEnd();
        bool device = Enumerable.Contains(Devices, stem, StringComparer.OrdinalIgnoreCase)
            || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && char.IsDigit(stem[3]));
        return device ? "_" + r : r;
    }
}

// Safe readers for client JSON: a field of the wrong type is "missing", never an exception.
public static class J
{
    public static string Str(JsonObject m, string key) => m != null && m[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static bool Bool(JsonObject m, string key, bool fallback = false) =>
        m != null && m[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;

    // null when absent / not a bool
    public static bool? BoolOrNull(JsonObject m, string key) => m != null && m[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    public static int Int(JsonObject m, string key, int fallback = 0)
    {
        try
        {
            if (m != null && m[key] is JsonValue v)
            {
                if (v.TryGetValue<int>(out var i)) return i;
                if (v.TryGetValue<double>(out var d) && !double.IsNaN(d)) return (int)Math.Clamp(d, int.MinValue, int.MaxValue);
            }
        }
        catch { }
        return fallback;
    }

    public static JsonObject Obj(JsonObject m, string key) => m != null && m[key] is JsonObject o ? o : null;
}
