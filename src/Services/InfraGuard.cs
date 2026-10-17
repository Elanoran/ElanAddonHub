using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace ElansAddonHub.Services
{
    // Always-run self-test check (MainWindow's self-test driver, next to the scope check): this repository is public, so the details of the
    // maintainers' own hosting - private addresses of the server network, host and VM names, the hypervisor, the reverse proxy box, the private
    // ops repository - must never land in a tracked file. Generic how-to text (e.g. "a reverse proxy such as Nginx Proxy Manager") is allowed
    // where it is listed below. The markers are built from parts so this file does not match itself.
    public static class InfraGuard
    {
        static readonly string[] Markers =
        {
            "192.168." + "30.", "neo" + ".lab", "srv" + "-web", "prox" + "mox", "proxy " + "manager", "npm " + "192", "elans" + "cairn",
        };

        // (path, marker) pairs that are fine: generic documentation that names a product, not our setup
        static readonly HashSet<string> Allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "server/deploy/reverse-proxy.md|" + "proxy " + "manager",   // "Proxy running in Docker (Nginx Proxy Manager, Traefik, ...)" - generic
        };

        public sealed class Hit { public string Path, Marker; public int Line; public override string ToString() => Path + ":" + Line + " (" + Marker + ")"; }

        // pure: the hits in these files' text (case-insensitive), minus the allowed pairs
        public static List<Hit> Scan(IEnumerable<KeyValuePair<string, string>> files)
        {
            var hits = new List<Hit>();
            foreach (var f in files)
            {
                var path = (f.Key ?? "").Replace('\\', '/');
                var lines = (f.Value ?? "").Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    var low = lines[i].ToLowerInvariant();
                    foreach (var mk in Markers)
                        if (low.Contains(mk) && !Allowed.Contains(path + "|" + mk)) hits.Add(new Hit { Path = path, Marker = mk, Line = i + 1 });
                }
            }
            return hits;
        }

        static readonly HashSet<string> Binary = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".gif", ".ico", ".blp", ".bin", ".zip", ".gz", ".exe", ".dll", ".pdb", ".ttf", ".otf", ".woff", ".woff2", ".wav", ".ogg", ".mp3", ".flac", ".webp", ".db2", ".pak" };

        // the tracked files of the repository (git ls-files from --repo or the current folder); null + why when git is not there
        public static List<KeyValuePair<string, string>> TrackedFiles(string repo, out string why)
        {
            why = null;
            try
            {
                var psi = new ProcessStartInfo("git", "ls-files -z") { WorkingDirectory = repo, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 };
                string output;
                using (var p = Process.Start(psi))
                {
                    var err = p.StandardError.ReadToEndAsync();
                    output = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(30000)) { why = "git timed out"; return null; }
                    if (p.ExitCode != 0) { why = "git ls-files: " + err.Result.Trim(); return null; }
                }
                var res = new List<KeyValuePair<string, string>>();
                foreach (var rel in output.Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (Binary.Contains(Path.GetExtension(rel))) continue;
                    var full = Path.Combine(repo, rel.Replace('/', '\\'));
                    if (!File.Exists(full) || new FileInfo(full).Length > 8 * 1024 * 1024) continue;
                    var bytes = File.ReadAllBytes(full);
                    if (Array.IndexOf(bytes, (byte)0) >= 0) continue;   // a binary file under a text-like name
                    res.Add(new KeyValuePair<string, string>(rel, Encoding.UTF8.GetString(bytes)));
                }
                return res;
            }
            catch (Exception e) { why = e.Message; return null; }
        }

        public static string SelfTest(string[] args)
        {
            var n = new List<string>();
            bool ok = true;
            void Ck(string w, bool c) { n.Add((c ? "ok   " : "FAIL ") + w); ok &= c; }
            // the rule itself, on invented files
            var fake = new Dictionary<string, string>
            {
                ["docs/a.md"] = "Our box is at 192.168." + "30.207 behind the " + "proxy " + "manager",
                ["src/B.cs"] = "// see " + "Elans" + "Cairn" + "\\ops",
                ["server/deploy/reverse-proxy.md"] = "Nginx " + "Proxy " + "Manager works too",
                ["src/C.cs"] = "var lan = \"192.168.2.54\"; // a test fixture placeholder, fine",
            };
            var hits = Scan(fake);
            Ck("infra guard: an address of the server network, the proxy box and the private ops repo are caught; generic docs and test placeholders are not",
                hits.Count(h => h.Path == "docs/a.md") == 2 && hits.Any(h => h.Path == "src/B.cs") && !hits.Any(h => h.Path == "server/deploy/reverse-proxy.md" || h.Path == "src/C.cs"));
            var repo = TestScope.ArgValue(args ?? new string[0], "--repo") ?? Directory.GetCurrentDirectory();
            var files = TrackedFiles(repo, out var why);
            if (files == null) { n.Add("ok   infra guard: no git repository here (" + why + "), the tracked-file scan is skipped"); return string.Join("\r\n", n); }
            var real = Scan(files);
            Ck("infra guard: no tracked file of this repository names our own hosting (" + files.Count + " files scanned" + (real.Count > 0 ? "; " + string.Join(", ", real.Take(8)) : "") + ")", real.Count == 0 && files.Count > 100);
            return string.Join("\r\n", n);
        }
    }
}
