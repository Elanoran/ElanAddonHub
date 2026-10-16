using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ElansAddonHub.Services
{
    // Money as the game writes it (2.77): an amount in copper as gold / silver / copper parts, "1,234g 5s 6c". The ONE place that splits copper into
    // coins: every page, tooltip and copied text goes through here (the plain text) or through Controls\MoneyText (the same parts with the game's coin
    // icons instead of the letters). Zero parts are left out (1g 5c), a nothing is a lone "0c", a negative amount is the same with a minus in front.
    // Compact (for tight spots): from 1 gold up the copper is left out, from 1,000 gold the silver too ("1,234g") - like the game's short money lines.
    public static class MoneyFormat
    {
        static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

        public struct Part
        {
            public long Amount;
            public char Coin;        // 'g', 's' or 'c'
            public Part(long amount, char coin) { Amount = amount; Coin = coin; }
            public override string ToString() => Amount.ToString("N0", En) + Coin;
        }

        // the coins of an amount, biggest first (the sign is not part of them: see Negative)
        public static List<Part> Parts(long copper, bool compact = false)
        {
            var r = new List<Part>();
            ulong a = copper < 0 ? (ulong)(-(copper + 1)) + 1 : (ulong)copper;      // long.MinValue safe
            long g = (long)(a / 10000), s = (long)(a / 100 % 100), c = (long)(a % 100);
            if (compact && g > 0) { c = 0; if (g >= 1000) s = 0; }
            if (g > 0) r.Add(new Part(g, 'g'));
            if (s > 0) r.Add(new Part(s, 's'));
            if (c > 0 || r.Count == 0) r.Add(new Part(c, 'c'));
            return r;
        }

        public static bool Negative(long copper) => copper < 0;

        // "1,234g 5s 6c", "0c", "-25s"
        public static string Text(long copper, bool compact = false)
        {
            var parts = Parts(copper, compact);
            var l = new List<string>(parts.Count);
            foreach (var p in parts) l.Add(p.ToString());
            var t = string.Join(" ", l);
            return copper < 0 && t != "0c" ? "-" + t : t;
        }

        // ---- reading the text back (for drawing coins into a line of text that already says "Sold for 1g 5s")
        // one amount: the parts in the game's order (g, then s, then c), silver and copper 0-99, separated by one space; not inside a word or a
        // number ("1.5s", "x5c", "5cm" are not money). Seconds are written "sec" everywhere in the app, so "30s" is 30 silver.
        public static readonly Regex Amount = new Regex(
            @"(?<![\w.,])(?<neg>-)?(?:(?<g>\d{1,3}(?:,\d{3})*|\d+)g(?: (?<s>\d{1,2})s)?(?: (?<c>\d{1,2})c)?|(?<s>\d{1,2})s(?: (?<c>\d{1,2})c)?|(?<c>\d{1,2})c)(?![\w])",
            RegexOptions.CultureInvariant);

        // the copper of one match of Amount
        public static long CopperOf(Match m)
        {
            long V(string name) => m.Groups[name].Success ? long.Parse(m.Groups[name].Value.Replace(",", ""), NumberStyles.None, CultureInfo.InvariantCulture) : 0;
            long v = V("g") * 10000 + V("s") * 100 + V("c");
            return m.Groups["neg"].Success ? -v : v;
        }

        // the text split into plain pieces and money amounts, in order (Copper == null = plain text)
        public static List<KeyValuePair<string, long?>> Split(string text)
        {
            var res = new List<KeyValuePair<string, long?>>();
            if (string.IsNullOrEmpty(text)) return res;
            int at = 0;
            foreach (Match m in Amount.Matches(text))
            {
                if (m.Index > at) res.Add(new KeyValuePair<string, long?>(text.Substring(at, m.Index - at), null));
                res.Add(new KeyValuePair<string, long?>(m.Value, CopperOf(m)));
                at = m.Index + m.Length;
            }
            if (at < text.Length) res.Add(new KeyValuePair<string, long?>(text.Substring(at), null));
            return res;
        }

        public static bool HasMoney(string text) => !string.IsNullOrEmpty(text) && Amount.IsMatch(text);
    }
}
