"""Turn Unicode's confusables.txt (UTS #39) into the compact C# table shared/Elans.Shared/Confusables.Data.cs.

    python -I Tools/unicode/make_confusables.py            write the table
    python -I Tools/unicode/make_confusables.py --check    change nothing; exit 1 if the table is not what this script makes

Input: Tools/unicode/confusables.txt (kept as downloaded, see Tools/unicode/README.md). Only the "MA" lines are read (the only
type the file has had since Unicode 9): source code point -> prototype sequence. The output depends on that file alone.

The table, in source code point order (one entry per mapping):
  Deltas    source code point i minus source code point i - 1 (the first one as itself)
  Lengths   one character per entry: the length of its target in UTF-16 units, base 36 ("1".."9", "a".."z")
  Pool      the targets one after another, UTF-16 (entry i starts where entry i - 1 ended)

Small capitals: UTS #39 keeps e.g. LATIN LETTER SMALL CAPITAL L as a prototype of its own, so "Eʟan" and "Elan" have different
skeletons. For the Lodge's name key (which folds case anyway) every "<script> LETTER SMALL CAPITAL <x>" named in confusables.txt
maps to "<script> SMALL LETTER <x>" when the file names that letter too: the table SmallCaps (pairs). The file names no small
capital F or Q, so those two are added by hand (EXTRA_CAPS).
"""
import os, re, sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
SOURCE = os.path.join(HERE, "confusables.txt")
TARGET = os.path.join(REPO, "shared", "Elans.Shared", "Confusables.Data.cs")
DIGITS = "0123456789abcdefghijklmnopqrstuvwxyz"
EXTRA_CAPS = {0xA730: 0x66, 0xA7AF: 0x71}   # LATIN LETTER SMALL CAPITAL F -> f, LATIN LETTER SMALL CAPITAL Q -> q


def parse():
    text = open(SOURCE, "rb").read().decode("utf-8-sig")
    date, ver = re.search(r"^# Date: (.+)$", text, re.M), re.search(r"^# Version: (.+)$", text, re.M)
    if not date or not ver:
        sys.exit("make_confusables: confusables.txt has no Date/Version header")
    version = "%s (%s)" % (ver.group(1).strip(), date.group(1).strip())
    maps, names = {}, {}
    for line in text.splitlines():
        if not line or line.startswith("#"):
            continue
        data, _, comment = line.partition("#")
        cols = [c.strip() for c in data.split(";")]
        if len(cols) < 3 or cols[2] != "MA":
            continue
        src = int(cols[0], 16)
        dst = [int(x, 16) for x in cols[1].split()]
        if src in maps:
            sys.exit("make_confusables: %04X is mapped twice" % src)
        maps[src] = dst
        # comment: "( a → b ) SOURCE NAME → TARGET NAME, TARGET NAME"
        m = re.search(r"\)\s*(.+?)\s*→\s*(.+?)\s*(?:#|$)", comment)
        if m:
            names.setdefault(src, m.group(1))
            tn = m.group(2).split(", ")
            if len(tn) == len(dst):
                for cp, n in zip(dst, tn):
                    names.setdefault(cp, n)
    if not maps:
        sys.exit("make_confusables: no MA lines in confusables.txt")
    return version, maps, names


def small_caps(names):
    by_name = {}
    for cp, n in sorted(names.items()):
        by_name.setdefault(n, cp)
    out = {}
    for cp, n in sorted(names.items()):
        m = re.fullmatch(r"(\w+) LETTER SMALL CAPITAL (.+)", n)
        if m and ("%s SMALL LETTER %s" % (m.group(1), m.group(2))) in by_name:
            out[cp] = by_name["%s SMALL LETTER %s" % (m.group(1), m.group(2))]
    for cp, low in EXTRA_CAPS.items():
        out.setdefault(cp, low)
    return dict(sorted(out.items()))


def utf16_units(cps):
    out = []
    for c in cps:
        if c >= 0x10000:
            c -= 0x10000
            out += [0xD800 | c >> 10, 0xDC00 | c & 0x3FF]
        else:
            out.append(c)
    return out


def literal_lines(units, width=110):
    # a C# string literal over several lines; printable ASCII as is, everything else as \uXXXX (no raw marks or bidi in the source)
    lines, cur = [], ""
    for u in units:
        c = chr(u)
        e = c if 0x20 <= u < 0x7F and c not in "\\\"" else "\\u%04X" % u
        if len(cur) + len(e) > width:
            lines.append(cur)
            cur = ""
        cur += e
    lines.append(cur)
    return "\n".join('            "%s"%s' % (l, " +" if k < len(lines) - 1 else ";") for k, l in enumerate(lines))


def int_lines(values, width=120):
    lines, cur = [], ""
    for v in values:
        e = "%d," % v
        if len(cur) + len(e) + 1 > width:
            lines.append(cur)
            cur = ""
        cur += (" " if cur else "") + e
    lines.append(cur)
    return "\n".join("            " + l for l in lines)


def render(version, maps, caps):
    srcs = sorted(maps)
    deltas = [srcs[0]] + [srcs[i] - srcs[i - 1] for i in range(1, len(srcs))]
    pool, lengths = [], []
    for s in srcs:
        u = utf16_units(maps[s])
        if not 1 <= len(u) < len(DIGITS):
            sys.exit("make_confusables: the target of %04X is too long" % s)
        lengths.append(DIGITS[len(u)])
        pool += u
    return """// <auto-generated>
// GENERATED by Tools/unicode/make_confusables.py from Unicode's confusables.txt (UTS #39), Version %(version)s.
// Do not edit: change the script, or refresh the data (Tools/unicode/README.md), and run it again.
// The data is (c) Unicode, Inc., under the Unicode License v3 (Tools/unicode/LICENSE-unicode.txt).
// %(entries)d mappings, a pool of %(pool)d UTF-16 units, %(caps)d small capitals.
// </auto-generated>

namespace Elans.Shared
{
    public static partial class Confusables
    {
        public const string DataVersion = "%(version)s";
        public const int DataEntries = %(entries)d;

        // source code points in ascending order, each as the difference to the one before
        static readonly int[] Deltas =
        {
%(deltas)s
        };

        // the length of each entry's target in UTF-16 units, one base-36 character per entry
        const string Lengths =
%(lengths)s

        // the targets one after another, UTF-16
        const string Pool =
%(pool_src)s

        // small capital -> its small letter, as pairs (only NameKey uses them: UTS #39 keeps small capitals as prototypes)
        static readonly int[] SmallCaps =
        {
%(caps_src)s
        };
    }
}
""" % dict(version=version, entries=len(srcs), pool=len(pool), caps=len(caps), deltas=int_lines(deltas),
           lengths=literal_lines([ord(c) for c in lengths]), pool_src=literal_lines(pool),
           caps_src=int_lines([v for kv in caps.items() for v in kv]))


def main(argv):
    version, maps, names = parse()
    out = render(version, maps, small_caps(names)).replace("\n", "\r\n").encode("utf-8")
    if "--check" in argv:
        have = open(TARGET, "rb").read() if os.path.exists(TARGET) else b""
        if have.replace(b"\r\n", b"\n") != out.replace(b"\r\n", b"\n"):
            print("Confusables.Data.cs is stale: run python -I Tools/unicode/make_confusables.py")
            return 1
        print("Confusables.Data.cs is up to date (%d mappings)" % len(maps))
        return 0
    open(TARGET, "wb").write(out)
    print("wrote %s: %d mappings, %d bytes" % (os.path.relpath(TARGET, REPO), len(maps), len(out)))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
