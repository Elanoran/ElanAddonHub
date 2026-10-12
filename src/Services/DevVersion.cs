using System;
using System.Text.RegularExpressions;

namespace ElansAddonHub.Services
{
    // git-describe versions ("v4.11.23-7-g9bc5970", "1.2.3-12-gabcdef0-dirty"): the last release plus N commits, an addon author's development build.
    public static class DevVersion
    {
        static readonly Regex Rx = new Regex(@"^(?<base>[vV]?\d+(?:\.\d+)*(?:[-+.]?[A-Za-z][\w.]*)?)-(?<n>\d{1,6})-g(?<h>[0-9a-fA-F]{4,40})(?<dirty>-dirty)?$", RegexOptions.CultureInvariant);

        // true for any git-describe shape (also "-0-g" and "-dirty"); baseVer keeps the author's spelling (with its "v")
        public static bool TryParse(string v, out string baseVer, out int commits, out bool dirty)
        {
            baseVer = null; commits = 0; dirty = false;
            var m = Rx.Match((v ?? "").Trim());
            if (!m.Success || !int.TryParse(m.Groups["n"].Value, out commits)) return false;
            baseVer = m.Groups["base"].Value; dirty = m.Groups["dirty"].Success;
            return true;
        }

        // a development build proper: commits after a release, or a modified checkout
        public static bool IsDev(string v) => TryParse(v, out _, out var n, out var d) && (n > 0 || d);

        // "v4.11.23 · development build (+7)"; null when v is not a development build
        public static string Label(string v)
        {
            if (!TryParse(v, out var b, out var n, out var d) || (n == 0 && !d)) return null;
            var plus = n > 0 ? "+" + n + (d ? ", modified" : "") : "modified";
            return b + " · development build (" + plus + ")";
        }

        // the longer sentence for the tooltip
        public static string Tip(string v)
        {
            if (!TryParse(v, out var b, out var n, out var d) || (n == 0 && !d)) return null;
            return "Development build: " + (n > 0 ? n + (n == 1 ? " commit" : " commits") + " after " + b : "a modified copy of " + b) + (n > 0 && d ? ", with local changes" : "") + " (" + v.Trim() + ")";
        }

        static string Numeric(string v)
        {
            var m = Regex.Match(v ?? "", @"\d+(\.\d+)*");
            return m.Success ? m.Value : null;
        }

        // The version's number and how many commits it is past that number (0 for a release). null when there is no number at all.
        public static string Number(string v, out int commits)
        {
            commits = 0;
            if (TryParse(v, out var b, out var n, out _)) { commits = n; return Numeric(b); }
            return Numeric(v);
        }

        // <0, 0, >0 like CompareTo. A development build is its base release plus N commits: newer than that release, older than the next one,
        // and compared by commit count against another build of the same base. null when either has no number.
        public static int? Compare(string a, string b)
        {
            var na = Number(a, out var ca); var nb = Number(b, out var cb);
            if (na == null || nb == null) return null;
            var c = Util.CompareVersions(na, nb);
            return c != 0 ? c : ca.CompareTo(cb);
        }
    }
}
