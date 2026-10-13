using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ElansAddonHub.Services.Atlas.Classic
{
    public sealed class ClassicDownloadException : Exception
    {
        public ClassicDownloadException(string msg, Exception inner = null) : base(msg, inner) { }
    }

    public static class ClassicPaths
    {
        public static string Dir => Path.Combine(Util.DataDir, "atlas", "classic");
        public static string IndexFile(string dir = null) => Path.Combine(dir ?? Dir, "index.bin");
        public static string SourceFile(string dir = null) => Path.Combine(dir ?? Dir, "source.json");
        public static string UpdatesDir(string dir = null) => Path.Combine(dir ?? Dir, "updates");
        public static string DumpFile(string name, string dir = null) => Path.Combine(dir ?? Dir, name);
    }

    // What GitHub currently offers, with sizes: shown to the user BEFORE anything is downloaded.
    public sealed class ClassicRemote
    {
        public string DumpName;
        public long DumpSize;
        public readonly List<KeyValuePair<string, long>> Updates = new List<KeyValuePair<string, long>>();   // name, size
        public string CommitSha, CommitDate;
        public string License = "GPL-3.0";
        public string Repo = "https://github.com/cmangos/classic-db";
        public long UpdatesSize => Updates.Sum(u => u.Value);
        public long TotalSize => DumpSize + UpdatesSize + LicenseEstimate;
        public const long LicenseEstimate = 40 * 1024;   // LICENSE.md + COPYRIGHT.md + AUTHORS
        public string Notice =>
            "cMaNGOS classic-db is public data under the GPL-3.0 licence, for non-commercial use (see COPYRIGHT.md). It is downloaded to this PC only, "
            + "is not shared with anyone and is labelled \"" + ClassicLabel.Source + "\" wherever it is shown.";
    }

    public sealed class ClassicDownloadProgress
    {
        public string Phase;
        public string File;
        public long BytesDone, BytesTotal;
        public double Fraction => BytesTotal > 0 ? Math.Min(1.0, (double)BytesDone / BytesTotal) : 0;
    }

    public sealed class ClassicDownloadResult
    {
        public string Dir, DumpPath, DumpName, Sha256, CommitSha, CommitDate;
        public long DumpSize;
        public int UpdateFiles;
    }

    static class Crc32
    {
        static readonly uint[] T = Make();
        static uint[] Make()
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++) { uint c = i; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; t[i] = c; }
            return t;
        }
        public static uint Update(uint crc, byte[] b, int n)
        {
            for (int i = 0; i < n; i++) crc = T[(crc ^ b[i]) & 0xFF] ^ (crc >> 8);
            return crc;
        }
    }

    // Downloads the cMaNGOS classic-db dump on request into Util.DataDir\atlas\classic\ (HTTPS only; plain http only for a loopback test server).
    // Everything is staged in a temp folder, checked (size, gzip integrity incl. CRC, looks like a SQL dump) and only then moved into place, so a
    // cancelled, failed or corrupt download never replaces a good earlier one. source.json is written last: it marks a complete set.
    public static class ClassicDownloader
    {
        // overridable for the self-test (a local fake server); the defaults are the real repository
        public static string ApiBase = "https://api.github.com/repos/cmangos/classic-db";
        public static string RawBase = "https://raw.githubusercontent.com/cmangos/classic-db/master";

        static readonly Regex DumpRx = new Regex(@"^ClassicDB_[A-Za-z0-9_.\-]+\.sql\.gz$", RegexOptions.CultureInvariant);
        static readonly Regex UpdateRx = new Regex(@"^[A-Za-z0-9_.,+\-]+\.sql$", RegexOptions.CultureInvariant);   // some real scripts have commas in their names
        public static readonly string[] LicenseFiles = { "LICENSE.md", "COPYRIGHT.md", "AUTHORS" };

        static void RequireSafe(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) throw new ClassicDownloadException("Bad address: " + url);
            if (u.Scheme == Uri.UriSchemeHttps) return;
            if (u.Scheme == Uri.UriSchemeHttp && (u.IsLoopback || u.Host == "localhost")) return;
            throw new ClassicDownloadException("Only https is allowed: " + url);
        }

        static async Task<string> GetString(string url, CancellationToken ct)
        {
            RequireSafe(url);
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
                    using (var resp = await Net.Http.SendAsync(req, ct).ConfigureAwait(false))
                    {
                        if (resp.StatusCode == HttpStatusCode.Forbidden || (int)resp.StatusCode == 429)
                        {
                            bool limited = (int)resp.StatusCode == 429 || (resp.Headers.TryGetValues("X-RateLimit-Remaining", out var rl) && rl.FirstOrDefault() == "0");
                            throw new ClassicDownloadException(limited ? "GitHub is limiting requests from this connection right now. Try again in a while." : "GitHub refused the request (403).");
                        }
                        if (!resp.IsSuccessStatusCode) throw new ClassicDownloadException("GitHub answered " + (int)resp.StatusCode + " for " + url);
                        return await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    }
                }
            }
            catch (HttpRequestException e) { throw new ClassicDownloadException("Could not reach GitHub: " + e.Message, e); }
        }

        // The current dump and update scripts with their sizes (one cheap API round, nothing downloaded).
        public static async Task<ClassicRemote> Query(CancellationToken ct)
        {
            var r = new ClassicRemote();
            var full = Json.Parse(await GetString(ApiBase + "/contents/Full_DB", ct).ConfigureAwait(false)) as List<object>;
            string best = null; long bestSize = 0;
            foreach (var o in full ?? new List<object>())
            {
                var d = o as Dictionary<string, object>;
                var name = d.Str("name");
                if (name == null || !DumpRx.IsMatch(name) || !string.Equals(d.Str("type") ?? "file", "file", StringComparison.OrdinalIgnoreCase)) continue;
                if (best == null || string.CompareOrdinal(name, best) > 0) { best = name; bestSize = d.Long("size"); }
            }
            if (best == null) throw new ClassicDownloadException("No ClassicDB_*.sql.gz dump found in the repository's Full_DB folder.");
            r.DumpName = best; r.DumpSize = bestSize;

            var ups = Json.Parse(await GetString(ApiBase + "/contents/Updates", ct).ConfigureAwait(false)) as List<object>;
            foreach (var o in ups ?? new List<object>())
            {
                var d = o as Dictionary<string, object>;
                var name = d.Str("name");
                if (name == null || !UpdateRx.IsMatch(name) || (d.Str("type") ?? "file") != "file") continue;
                r.Updates.Add(new KeyValuePair<string, long>(name, d.Long("size")));
            }
            try
            {
                var c = Json.Parse(await GetString(ApiBase + "/commits/master", ct).ConfigureAwait(false)) as Dictionary<string, object>;
                r.CommitSha = c.Str("sha");
                r.CommitDate = c.Child("commit")?.Child("committer")?.Str("date");
            }
            catch (ClassicDownloadException) { /* the commit is only a label */ }
            return r;
        }

        // gzip integrity: decompresses everything (the footer CRC and length are checked by the stream), and the head must look like a MySQL dump.
        public static bool IsGzipValid(string path, out string error)
        {
            error = null;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var gz = new GZipStream(fs, CompressionMode.Decompress))
                {
                    var buf = new byte[1 << 16];
                    int n; long total = 0; uint crc = 0xFFFFFFFF;
                    var head = new StringBuilder();
                    while ((n = gz.Read(buf, 0, buf.Length)) > 0)
                    {
                        if (head.Length < 1 << 16) head.Append(Encoding.UTF8.GetString(buf, 0, Math.Min(n, (1 << 16) - head.Length)));
                        total += n;
                        crc = Crc32.Update(crc, buf, n);
                    }
                    // the footer is checked here as well, not only by the framework: CRC-32 and length (mod 2^32) of the uncompressed data
                    if (fs.Length < 18) { error = "damaged download (no gzip footer)"; return false; }
                    var foot = new byte[8];
                    fs.Seek(-8, SeekOrigin.End);
                    int got = 0, r;
                    while (got < 8 && (r = fs.Read(foot, got, 8 - got)) > 0) got += r;
                    uint wantCrc = BitConverter.ToUInt32(foot, 0), wantLen = BitConverter.ToUInt32(foot, 4);
                    if (got != 8 || wantCrc != ~crc || wantLen != (uint)total) { error = "damaged download (checksum does not match)"; return false; }
                    if (total == 0) { error = "the file is empty"; return false; }
                    var h = head.ToString();
                    if (h.IndexOf("INSERT INTO", StringComparison.Ordinal) < 0 && h.IndexOf("CREATE TABLE", StringComparison.Ordinal) < 0)
                    { error = "the file is not an SQL dump"; return false; }
                }
                return true;
            }
            catch (Exception e) when (e is InvalidDataException || e is IOException || e is EndOfStreamException)
            {
                error = "damaged download (" + e.Message + ")";
                return false;
            }
        }

        // Downloads the dump, the update scripts and the licence files for `remote` into `dir` (default Util.DataDir\atlas\classic).
        public static async Task<ClassicDownloadResult> Download(ClassicRemote remote, IProgress<ClassicDownloadProgress> progress, CancellationToken ct, string dir = null)
        {
            if (remote == null) throw new ArgumentNullException(nameof(remote));
            RequireSafe(RawBase);
            dir = dir ?? ClassicPaths.Dir;
            if (!DumpRx.IsMatch(remote.DumpName ?? "")) throw new ClassicDownloadException("Unexpected dump name.");
            foreach (var u in remote.Updates) if (!UpdateRx.IsMatch(u.Key)) throw new ClassicDownloadException("Unexpected update file name.");
            Directory.CreateDirectory(dir);
            var stage = Path.Combine(dir, ".staging");
            if (Directory.Exists(stage)) Util.DeleteDir(stage);
            Directory.CreateDirectory(stage);
            long total = remote.TotalSize, done = 0;
            var res = new ClassicDownloadResult { Dir = dir, DumpName = remote.DumpName, CommitSha = remote.CommitSha, CommitDate = remote.CommitDate };

            void Report(string phase, string file) => progress?.Report(new ClassicDownloadProgress { Phase = phase, File = file, BytesDone = Interlocked.Read(ref done), BytesTotal = total });

            try
            {
                // dump
                Report("Downloading the dump", remote.DumpName);
                var dumpTmp = Path.Combine(stage, remote.DumpName);
                string sha;
                long size = await Fetch(RawBase + "/Full_DB/" + remote.DumpName, dumpTmp, remote.DumpSize, n => { Interlocked.Add(ref done, n); Report("Downloading the dump", remote.DumpName); }, ct, true).ConfigureAwait(false);
                sha = Sha256Of(dumpTmp);
                res.DumpSize = size; res.Sha256 = sha;
                Report("Checking the download", remote.DumpName);
                ct.ThrowIfCancellationRequested();
                if (!IsGzipValid(dumpTmp, out var gerr)) throw new ClassicDownloadException("The dump is not usable: " + gerr);

                // licence files (small; a missing one is an error: we do not keep the data without its licence next to it)
                foreach (var f in LicenseFiles)
                {
                    await Fetch(RawBase + "/" + f, Path.Combine(stage, f), -1, n => Interlocked.Add(ref done, n), ct, false).ConfigureAwait(false);
                }

                // updates, a few at a time
                var upDir = Path.Combine(stage, "updates");
                Directory.CreateDirectory(upDir);
                int finished = 0;
                using (var gate = new SemaphoreSlim(6))
                {
                    var tasks = remote.Updates.Select(async u =>
                    {
                        await gate.WaitAsync(ct).ConfigureAwait(false);
                        try
                        {
                            ct.ThrowIfCancellationRequested();
                            long got = 0;
                            for (int attempt = 1; ; attempt++)
                            {
                                try
                                {
                                    await Fetch(RawBase + "/Updates/" + u.Key, Path.Combine(upDir, u.Key), u.Value, n => { got += n; Interlocked.Add(ref done, n); }, ct, false).ConfigureAwait(false);
                                    break;
                                }
                                catch (ClassicDownloadException) when (attempt < 2)
                                {
                                    Interlocked.Add(ref done, -got); got = 0;   // one more try for a hiccup; the bytes of the failed try are taken back
                                    await Task.Delay(500, ct).ConfigureAwait(false);
                                }
                            }
                            var k = Interlocked.Increment(ref finished);
                            Report("Downloading updates " + k + "/" + remote.Updates.Count, u.Key);
                        }
                        finally { gate.Release(); }
                    }).ToList();
                    await Task.WhenAll(tasks).ConfigureAwait(false);
                }
                res.UpdateFiles = remote.Updates.Count;

                // swap into place: all files of the set are valid, so replace the old set
                ct.ThrowIfCancellationRequested();
                Report("Saving", null);
                foreach (var old in Directory.GetFiles(dir, "ClassicDB_*.sql.gz")) TryDelete(old);
                Place(dumpTmp, Path.Combine(dir, remote.DumpName));
                foreach (var f in LicenseFiles) Place(Path.Combine(stage, f), Path.Combine(dir, f));
                var finalUp = ClassicPaths.UpdatesDir(dir);
                if (Directory.Exists(finalUp)) Util.DeleteDir(finalUp);
                Directory.Move(upDir, finalUp);
                res.DumpPath = Path.Combine(dir, remote.DumpName);

                var meta = new Dictionary<string, object>
                {
                    ["repo"] = remote.Repo, ["license"] = remote.License, ["credit"] = ClassicLabel.Credit, ["label"] = ClassicLabel.Source,
                    ["dump"] = remote.DumpName, ["dumpSize"] = (double)size, ["sha256"] = sha,
                    ["commit"] = remote.CommitSha, ["commitDate"] = remote.CommitDate, ["updates"] = (double)remote.Updates.Count,
                    ["downloadedUtc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                };
                var metaTmp = Path.Combine(stage, "source.json");
                File.WriteAllText(metaTmp, Json.Write(meta), new UTF8Encoding(false));
                Place(metaTmp, ClassicPaths.SourceFile(dir));
                Report("Done", null);
                return res;
            }
            finally
            {
                try { Util.DeleteDir(stage); } catch { }
            }
        }

        // reads source.json (null when no complete download exists)
        public static Dictionary<string, object> ReadSource(string dir = null)
        {
            try
            {
                var f = ClassicPaths.SourceFile(dir);
                return File.Exists(f) ? Json.Obj(File.ReadAllText(f, Encoding.UTF8)) : null;
            }
            catch (Exception e) when (e is IOException || e is FormatException || e is UnauthorizedAccessException) { return null; }
        }

        static void TryDelete(string f) { try { File.Delete(f); } catch { } }

        static void Place(string from, string to)
        {
            if (File.Exists(to)) File.Replace(from, to, null);
            else File.Move(from, to);
        }

        static string Sha256Of(string path)
        {
            using (var s = File.OpenRead(path))
            using (var h = SHA256.Create())
                return BitConverter.ToString(h.ComputeHash(s)).Replace("-", "").ToLowerInvariant();
        }

        // one file to a path; returns its size. expected > 0: the size must match (when the server also sends a length, both must agree).
        static async Task<long> Fetch(string url, string to, long expected, Action<long> onBytes, CancellationToken ct, bool mustMatch)
        {
            RequireSafe(url);
            try
            {
                using (var resp = await Net.Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode) throw new ClassicDownloadException("GitHub answered " + (int)resp.StatusCode + " for " + Path.GetFileName(to));
                    long declared = resp.Content.Headers.ContentLength ?? -1;
                    long written = 0;
                    using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var dst = new FileStream(to, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        var buf = new byte[81920];
                        int n;
                        while ((n = await src.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false)) > 0)
                        {
                            ct.ThrowIfCancellationRequested();
                            await dst.WriteAsync(buf, 0, n, ct).ConfigureAwait(false);
                            written += n;
                            onBytes?.Invoke(n);
                        }
                    }
                    if (declared >= 0 && written != declared) throw new ClassicDownloadException("The download of " + Path.GetFileName(to) + " was cut short (" + written + " of " + declared + " bytes).");
                    if (mustMatch && expected > 0 && written != expected) throw new ClassicDownloadException("The size of " + Path.GetFileName(to) + " is not what GitHub listed (" + written + " of " + expected + " bytes).");
                    return written;
                }
            }
            catch (Exception) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }   // a cancelled read surfaces as an IO / Http error: report it as the cancel it is
            catch (HttpRequestException e) { throw new ClassicDownloadException("Could not download " + Path.GetFileName(to) + ": " + e.Message, e); }
            catch (IOException e) when (!(e is FileNotFoundException)) { throw new ClassicDownloadException("Could not download " + Path.GetFileName(to) + ": " + e.Message, e); }
        }
    }
}
