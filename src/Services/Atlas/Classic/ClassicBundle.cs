using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ElansAddonHub.Services.Atlas.Classic
{
    // The Classic baseline as ONE file that can travel through a Lodge: a zip of the ORIGINAL upstream files, unchanged
    // (ClassicDB_*.sql.gz, LICENSE.md, COPYRIGHT.md, AUTHORS, source.json and updates/*.sql). The Lodge server stores it as is; every Outpost unpacks it and
    // builds its own index with its own importer, so an index change never needs a server change and the licence and credit always travel with the data.
    // Mirrors the checks of server/Lodge.Server/DatasetsModule.cs (ClassicBundle.Check).
    public static class ClassicBundle
    {
        public const string DatasetName = "classic";
        public const int MaxEntries = 2000;
        public const long MaxUnpackedBytes = 400L * 1024 * 1024;
        public const long MaxSourceJsonBytes = 16 * 1024;

        static readonly Regex DumpRx = new Regex(@"^ClassicDB_[A-Za-z0-9_.\-]+\.sql\.gz$", RegexOptions.CultureInvariant);
        static readonly Regex UpdateRx = new Regex(@"^updates/[A-Za-z0-9_.,+\-]+\.sql$", RegexOptions.CultureInvariant);
        static readonly string[] Licence = { "LICENSE.md", "COPYRIGHT.md", "AUTHORS" };

        // ---- versions. The Lodge keeps one opaque string; "2024-05-02T10:00:00Z|ClassicDB_1_12_1_z2815.sql.gz|<commit>" sorts by date first.

        public static string VersionOf(Dictionary<string, object> source)
        {
            if (source == null) return null;
            var dump = source.Str("dump");
            if (string.IsNullOrEmpty(dump)) return null;
            return (source.Str("commitDate") ?? "") + "|" + dump + "|" + (source.Str("commit") ?? "");
        }

        // the version of the data in `dir` (default: the real folder); null when there is no complete set
        public static string LocalVersion(string dir = null) => VersionOf(ClassicDownloader.ReadSource(dir));

        static void Split(string v, out DateTime? date, out string dump)
        {
            date = null; dump = "";
            var p = (v ?? "").Split('|');
            if (p.Length > 0 && DateTime.TryParse(p[0], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)) date = d;
            if (p.Length > 1) dump = p[1];
        }

        // true when `candidate` is a newer upstream release than `current`: by commit date when both have one, else by dump name; equal = not newer
        public static bool IsNewer(string candidate, string current)
        {
            if (string.IsNullOrEmpty(candidate)) return false;
            if (string.IsNullOrEmpty(current)) return true;
            if (candidate == current) return false;
            Split(candidate, out var da, out var na); Split(current, out var db, out var nb);
            if (da.HasValue && db.HasValue && da.Value != db.Value) return da.Value > db.Value;
            return string.CompareOrdinal(na, nb) > 0;
        }

        // "ClassicDB_1_12_1_z2815 (2 May 2024)"
        public static string Describe(string version)
        {
            Split(version, out var date, out var dump);
            if (dump.EndsWith(".sql.gz", StringComparison.Ordinal)) dump = dump.Substring(0, dump.Length - ".sql.gz".Length);
            if (dump.Length == 0) dump = "Classic data";
            return dump + (date.HasValue ? " (" + date.Value.ToString("d MMM yyyy", Lodge.CalendarRules.English) + ")" : "");
        }

        // ---- pack: the set in `dir` (what the downloader wrote) into one zip. null = fine, otherwise the plain reason.
        public static string Pack(string dir, string zipPath, out string version)
        {
            version = null;
            try
            {
                var src = ClassicDownloader.ReadSource(dir);
                if (src == null) return "There is no complete Classic download on this PC to share.";
                version = VersionOf(src);
                var dumpName = src.Str("dump") ?? "";
                if (!DumpRx.IsMatch(dumpName) || !File.Exists(Path.Combine(dir, dumpName))) return "The Classic dump is missing on this PC.";
                foreach (var n in Licence) if (!File.Exists(Path.Combine(dir, n))) return n + " is missing on this PC, so the data can't be shared with its licence.";
                if (File.Exists(zipPath)) File.Delete(zipPath);
                Directory.CreateDirectory(Path.GetDirectoryName(zipPath));
                using (var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
                {
                    Add(zip, Path.Combine(dir, dumpName), dumpName, CompressionLevel.NoCompression);   // already gzip
                    foreach (var n in Licence) Add(zip, Path.Combine(dir, n), n, CompressionLevel.Optimal);
                    Add(zip, ClassicPaths.SourceFile(dir), "source.json", CompressionLevel.Optimal);
                    var up = ClassicPaths.UpdatesDir(dir);
                    if (Directory.Exists(up))
                        foreach (var f in Directory.GetFiles(up, "*.sql").OrderBy(x => x, StringComparer.Ordinal))
                        {
                            var name = "updates/" + Path.GetFileName(f);
                            if (UpdateRx.IsMatch(name)) Add(zip, f, name, CompressionLevel.Optimal);
                        }
                }
                return null;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is InvalidDataException)
            {
                return "Could not prepare the Classic data for sharing: " + e.Message;
            }
        }

        static void Add(ZipArchive zip, string file, string name, CompressionLevel level)
        {
            var e = zip.CreateEntry(name, level);
            using (var s = e.Open())
            using (var f = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                f.CopyTo(s);
        }

        // ---- unpack into an empty staging folder. Only entries with the expected names are written, always under names built here (never the
        // entry's own path). null = fine, otherwise the plain reason.
        public static string Unpack(string zipPath, string stage)
        {
            try
            {
                Directory.CreateDirectory(stage);
                using (var zip = ZipFile.OpenRead(zipPath))
                {
                    if (zip.Entries.Count > MaxEntries) return "The bundle has too many files.";
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    string dump = null;
                    long total = 0;
                    foreach (var e in zip.Entries)
                    {
                        var n = e.FullName;
                        if (n == "updates/") continue;
                        if (!seen.Add(n)) return "The bundle lists a file twice.";
                        total += e.Length;
                        if (total > MaxUnpackedBytes) return "The bundle is too big when unpacked.";
                        string target;
                        if (DumpRx.IsMatch(n)) { if (dump != null) return "The bundle holds more than one dump."; dump = n; target = Path.Combine(stage, n); }
                        else if (UpdateRx.IsMatch(n)) { Directory.CreateDirectory(Path.Combine(stage, "updates")); target = Path.Combine(stage, "updates", n.Substring("updates/".Length)); }
                        else if (Licence.Contains(n) || n == "source.json")
                        {
                            if (n == "source.json" && (e.Length == 0 || e.Length > MaxSourceJsonBytes)) return "source.json has an unexpected size.";
                            target = Path.Combine(stage, n);
                        }
                        else return "The bundle holds a file that does not belong in it.";
                        long written = 0;
                        using (var s = e.Open())
                        using (var d = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            var buf = new byte[81920];
                            int r;
                            while ((r = s.Read(buf, 0, buf.Length)) > 0)
                            {
                                written += r;
                                if (written > e.Length + 1024) return "The bundle is damaged.";   // an entry that grows beyond what it announced
                                d.Write(buf, 0, r);
                            }
                        }
                    }
                    if (dump == null) return "The bundle has no Classic dump.";
                    foreach (var l in Licence) if (!seen.Contains(l)) return "The bundle is missing " + l + ".";
                    if (!seen.Contains("source.json")) return "The bundle is missing source.json.";
                }
                return null;
            }
            catch (Exception e) when (e is InvalidDataException || e is IOException || e is NotSupportedException || e is UnauthorizedAccessException)
            {
                return "The bundle is damaged (" + e.Message + ").";
            }
        }

        // ---- install the unpacked set into `dir` like the downloader does: the dump, the licence files, the updates, source.json last (it marks a complete set).
        // source.json gets "via": "lodge" (and the Lodge's name) added: the sole change, so the page can say where it came from. Returns the version installed.
        public static string Install(string stage, string dir, string lodgeName)
        {
            Directory.CreateDirectory(dir);
            var srcText = File.ReadAllText(Path.Combine(stage, "source.json"), Encoding.UTF8);
            var src = Json.Obj(srcText) ?? new Dictionary<string, object>();
            var dumpName = Directory.GetFiles(stage, "ClassicDB_*.sql.gz").Select(Path.GetFileName).FirstOrDefault();
            if (dumpName == null) throw new ClassicDownloadException("The bundle has no dump.");
            src["dump"] = dumpName;     // what is really there wins over whatever source.json says
            src["via"] = "lodge";
            if (!string.IsNullOrEmpty(lodgeName)) src["viaName"] = lodgeName;
            src["receivedUtc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            foreach (var old in Directory.GetFiles(dir, "ClassicDB_*.sql.gz")) { try { File.Delete(old); } catch { } }
            Place(Path.Combine(stage, dumpName), Path.Combine(dir, dumpName));
            foreach (var f in Licence) Place(Path.Combine(stage, f), Path.Combine(dir, f));
            var finalUp = ClassicPaths.UpdatesDir(dir);
            if (Directory.Exists(finalUp)) Util.DeleteDir(finalUp);
            var stageUp = Path.Combine(stage, "updates");
            if (Directory.Exists(stageUp)) Directory.Move(stageUp, finalUp); else Directory.CreateDirectory(finalUp);
            var metaTmp = Path.Combine(stage, "source.out.json");
            File.WriteAllText(metaTmp, Json.Write(src), new UTF8Encoding(false));
            Place(metaTmp, ClassicPaths.SourceFile(dir));
            return VersionOf(src);
        }

        static void Place(string from, string to)
        {
            if (File.Exists(to)) File.Replace(from, to, null);
            else File.Move(from, to);
        }
    }
}
