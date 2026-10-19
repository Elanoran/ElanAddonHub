"""Copy the shared code (shared/Elans.Shared/*.cs) into the Cairn, which lives in its own repo (<cairn repo>/cairn).

    python shared/sync_cairn.py [--ops PATH]           copy into <ops>/cairn/Cairn.Server/Shared/
    python shared/sync_cairn.py --check [--ops PATH]   change nothing; exit 1 if the Cairn's copy is missing, differs or has stray files

The source of truth is shared/Elans.Shared/ in the Outpost repo. NEVER edit the copy inside the Cairn. The Outpost and the Lodge server
reference the project directly; only the Cairn needs a copy (its repo is built on its own server by cairn-update).
Only the top-level .cs files are copied: subfolders (e.g. Lodge/, the Lodge message types) are not, the Cairn doesn't need them.
<ops> = the Cairn's repo: --ops PATH or $ELANS_OPS_REPO (no default: it is another, private repo).
"""
import os, sys

HERE = os.path.dirname(os.path.abspath(__file__))
SOURCE = os.path.join(HERE, "Elans.Shared")
HEADER = "// COPY of shared/Elans.Shared/{name} in the Outpost repo (ElansAddonHub), made by shared/sync_cairn.py. Do not edit here.\n"


def ops_repo(argv):
    if "--ops" in argv:
        return argv[argv.index("--ops") + 1]
    path = os.environ.get("ELANS_OPS_REPO")
    if not path:
        sys.exit("sync_cairn: say where the Cairn's repo is: --ops PATH or ELANS_OPS_REPO")
    return path


def wanted():
    out = {}
    for f in sorted(os.listdir(SOURCE)):
        if f.endswith(".cs"):
            text = open(os.path.join(SOURCE, f), "rb").read().decode("utf-8").replace("\r\n", "\n")
            out[f] = (HEADER.format(name=f) + text).replace("\n", "\r\n").encode("utf-8")
    return out


def main(argv):
    dest = os.path.join(ops_repo(argv), "cairn", "Cairn.Server", "Shared")
    want = wanted()
    if "--check" in argv:
        problems = []
        have = set(os.listdir(dest)) if os.path.isdir(dest) else None
        if have is None:
            problems.append(dest + ": missing")
        else:
            for f, data in want.items():
                p = os.path.join(dest, f)
                if not os.path.exists(p):
                    problems.append(f + ": missing in the Cairn")
                elif open(p, "rb").read().replace(b"\r\n", b"\n") != data.replace(b"\r\n", b"\n"):   # line ends follow the checkout (LF on the Linux server)
                    problems.append(f + ": differs from shared/Elans.Shared/" + f)
            for f in sorted(have - set(want)):
                problems.append(f + ": is not in shared/Elans.Shared/")
        if problems:
            print("the Cairn's copy of Elans.Shared is stale (run: python shared/sync_cairn.py):\n  " + "\n  ".join(problems))
            return 1
        print("the Cairn's copy of Elans.Shared is in sync (%d files)" % len(want))
        return 0
    os.makedirs(dest, exist_ok=True)
    for f in os.listdir(dest):
        if f not in want:
            os.remove(os.path.join(dest, f))
    for f, data in want.items():
        open(os.path.join(dest, f), "wb").write(data)
    print("copied %d files into %s" % (len(want), dest))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
