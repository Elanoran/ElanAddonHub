using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ElansAddonHub.Services
{
    // Updates a CurseForge-managed addon WITHOUT the CurseForge app, so its "Where would you like to install your addon? / Select game
    // instance" modal never appears. Why it can't be avoided through the app: CurseForge 1.3x's curseforge://install handler reads only
    // addonId, fileId, gameId, code and utm parameters - no instance id (verified read-only in its app.asar, see CurseForgeLocal.RequestInstall).
    //
    // What is used: only CurseForge's OWN local data (AddonGameInstance.json, read by CurseForgeLocal.Load): latestFile.downloadUrl,
    // fileLength, hashes and modules[].foldername. Nothing is asked from CurseForge's web API.
    // Safety rules (every one has a self-test below):
    //   - only https and a *.forgecdn.net host, checked on the address in the file AND on the address the download really came from
    //   - the download must have exactly fileLength bytes and match every sha1 / md5 CurseForge lists (at least one is required),
    //     checked BEFORE anything is extracted
    //   - the zip's top-level folders must be exactly latestFile.modules[].foldername; no "..", rooted or drive paths, no loose files
    //   - a folder that exists but belongs to another addon, or is a git checkout, is never replaced
    //   - the current folders are backed up (the same backups\addons\<key> folders as the other addon updates, newest 2 kept),
    //     then swapped by renames inside AddOns; any failure puts every folder back. WTF / SavedVariables are never touched.
    // Respecting the author and CurseForge: allowModDistribution false, isLocked, preferenceIsIgnored, isWorkingCopy, or a required
    // dependency that isn't installed = no direct update, the old deep link (CurseForge handles those) - see Decide.
    // Keeping CurseForge in sync: CurseForge's JSON is never written. CurseForge fingerprints the AddOns folders when it scans (on
    // start and on its update checks) and matches them to the file; until it has, the Outpost remembers what it installed
    // (cfdirect.txt in its data folder) so the card shows the new version straight away. NOT verified against a running CurseForge
    // (the tests never start it): whether it lists the new file as installed or marks the addon "modified" until its next check.
    public static class CurseForgeDirect
    {
        // Settings > Addons > CurseForge "Update CurseForge addons directly (recommended)" - MainWindow wires it to settings (!CfDirectOff)
        public static Func<bool> IsOn = () => true;

        public class Plan
        {
            public bool Direct;
            public string Reason;        // why it goes through CurseForge instead (null = the setting is off / nothing to say)
            public bool AuthorOnly;      // allowModDistribution == false
        }

        public const string AuthorOnlyText = "This author only allows updates through CurseForge";

        public static Plan Decide(CfAddon a, CfInstance inst, bool on)
        {
            if (!on || a == null) return new Plan();
            if (!a.AllowDistribution) return new Plan { AuthorOnly = true, Reason = AuthorOnlyText + "." };
            if (a.IsLocked) return new Plan { Reason = "This addon is locked in CurseForge, so CurseForge installs the update." };
            if (a.IsIgnored) return new Plan { Reason = "CurseForge is set to ignore this addon's updates, so the update goes through CurseForge." };
            if (a.IsWorkingCopy) return new Plan { Reason = "CurseForge marks this addon as a working copy, so the update goes through CurseForge." };
            var missing = inst != null ? a.RequiredDeps.Where(d => !inst.InstalledIds.Contains(d)).ToList() : a.MissingDeps;
            if (missing.Count > 0) return new Plan { Reason = $"This update needs {(missing.Count == 1 ? "another addon" : missing.Count + " other addons")} that isn't installed yet - CurseForge installs {(missing.Count == 1 ? "it" : "them")} too." };
            var why = UrlProblem(a.LatestUrl);
            if (why != null) return new Plan { Reason = why };
            if (a.LatestLength <= 0 || !a.LatestHashes.Any(h => h.Key == "sha1" || h.Key == "md5"))
                return new Plan { Reason = "CurseForge has no size or checksum for this file, so the update goes through CurseForge." };
            if (a.LatestModules.Count == 0 || a.LatestModules.Any(m => !SafeFolder(m)))
                return new Plan { Reason = "CurseForge's folder list for this file looks wrong, so the update goes through CurseForge." };
            return new Plan { Direct = true };
        }

        // null = fine: https, a forgecdn.net host (edge.forgecdn.net, mediafilez.forgecdn.net ...), the default port, no user info
        public static string UrlProblem(string url)
        {
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u)) return "CurseForge has no download address for this file, so the update goes through CurseForge.";
            return UrlProblem(u);
        }

        static string UrlProblem(Uri u)
        {
            if (u.Scheme != Uri.UriSchemeHttps || !u.IsDefaultPort || !string.IsNullOrEmpty(u.UserInfo)) return "The download address isn't a plain https address, so it was refused.";
            var host = u.Host.ToLowerInvariant().TrimEnd('.');
            if (host == "forgecdn.net" || host.EndsWith(".forgecdn.net", StringComparison.Ordinal)) return null;
            return "The download comes from " + host + ", not CurseForge's download server (forgecdn.net), so it was refused.";
        }

        // a single folder name directly in AddOns
        static bool SafeFolder(string f) =>
            !string.IsNullOrWhiteSpace(f) && f == f.Trim() && f.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && f != "." && f != ".." && !f.StartsWith(".")
            && f.IndexOfAny(new[] { '/', '\\', ':' }) < 0;

        public class Result { public bool Ok; public string Error; public string BackupDir; public List<string> Folders = new List<string>(); }

        // ---- seams for the self-test (never set in the app)
        internal static Func<Uri, Uri> RewriteForTest;      // the CHECKED forgecdn address is fetched from a local test server instead
        internal static Action<string> SwapHook;            // called before each new folder is moved in (throws = a failure mid-swap)

        public static async Task<Result> Update(string addOnsDir, string key, CfAddon a, IProgress<double> progress = null)
        {
            var res = new Result();
            var work = Path.Combine(Path.GetTempPath(), "ElansAddonHub-cf-" + Guid.NewGuid().ToString("N"));
            try
            {
                if (string.IsNullOrEmpty(addOnsDir) || !Directory.Exists(addOnsDir)) throw new InvalidOperationException("The AddOns folder wasn't found.");
                var why = UrlProblem(a.LatestUrl);
                if (why != null) throw new InvalidOperationException(why);
                if (a.LatestLength <= 0 || !a.LatestHashes.Any(h => h.Key == "sha1" || h.Key == "md5")) throw new InvalidOperationException("CurseForge has no size or checksum for this file.");
                Directory.CreateDirectory(work);
                var zip = Path.Combine(work, "addon.zip");
                var uri = new Uri(a.LatestUrl.Trim());
                var req = RewriteForTest != null ? RewriteForTest(uri) : uri;
                await Fetch(req, zip, a.LatestLength, final =>
                {
                    if (RewriteForTest != null && final == req) final = uri;      // self-test: the local server stands in for the checked address
                    return UrlProblem(final);
                }, new Progress<double>(p => progress?.Report(p * 0.7)));
                var bad = VerifyFile(zip, a);
                if (bad != null) throw new InvalidDataException(bad);
                return await Task.Run(() => InstallVerified(addOnsDir, key, a, zip, progress));
            }
            catch (Exception e)
            {
                Util.Log($"curseforge direct update {a?.Id} failed: {e.Message}");
                res.Error = e.Message;
                return res;
            }
            finally { try { Util.DeleteDir(work); } catch { } }
        }

        static async Task Fetch(Uri uri, string toFile, long size, Func<Uri, string> checkFinal, IProgress<double> progress)
        {
            using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5)))
            using (var resp = await Net.Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cts.Token))
            {
                resp.EnsureSuccessStatusCode();
                var why = checkFinal(resp.RequestMessage?.RequestUri ?? uri);          // after redirects, before reading the body
                if (why != null) throw new InvalidOperationException(why);
                var len = resp.Content.Headers.ContentLength;
                if (len.HasValue && len.Value != size) throw new InvalidDataException($"The download has the wrong size ({len.Value} bytes, CurseForge says {size}) - nothing was changed.");
                using (var src = await resp.Content.ReadAsStreamAsync())
                using (var dst = File.Create(toFile))
                {
                    var buf = new byte[81920];
                    long done = 0; int n;
                    while ((n = await src.ReadAsync(buf, 0, buf.Length, cts.Token)) > 0)
                    {
                        done += n;
                        if (done > size) throw new InvalidDataException("The download is bigger than CurseForge says - nothing was changed.");
                        await dst.WriteAsync(buf, 0, n);
                        progress?.Report((double)done / size);
                    }
                }
            }
        }

        // null = the file is exactly what CurseForge lists (size and every sha1 / md5)
        internal static string VerifyFile(string file, CfAddon a)
        {
            var len = new FileInfo(file).Length;
            if (len != a.LatestLength) return $"The download has the wrong size ({len} bytes, CurseForge says {a.LatestLength}) - nothing was changed.";
            int checkedN = 0;
            foreach (var h in a.LatestHashes)
            {
                string got;
                if (h.Key == "sha1") using (var s = SHA1.Create()) using (var fs = File.OpenRead(file)) got = string.Concat(s.ComputeHash(fs).Select(b => b.ToString("x2")));
                else if (h.Key == "md5") got = Util.Md5(file);
                else continue;
                checkedN++;
                if (!string.Equals(got, h.Value, StringComparison.OrdinalIgnoreCase)) return $"The download is damaged ({h.Key} checksum doesn't match CurseForge's) - nothing was changed.";
            }
            return checkedN == 0 ? "CurseForge has no checksum for this file - nothing was changed." : null;
        }

        // the part after the download and the checks: zip layout, backup, swap, roll back on failure
        internal static Result InstallVerified(string addOnsDir, string key, CfAddon a, string zipFile, IProgress<double> progress)
        {
            var res = new Result();
            var want = new HashSet<string>(a.LatestModules, StringComparer.OrdinalIgnoreCase);
            if (want.Count == 0 || want.Any(m => !SafeFolder(m))) throw new InvalidDataException("CurseForge's folder list for this file looks wrong - nothing was changed.");
            var tag = Guid.NewGuid().ToString("N").Substring(0, 8);
            var tmp = Path.Combine(addOnsDir, ".elanshub-new-" + tag);
            var oldRoot = Path.Combine(addOnsDir, ".elanshub-old-" + tag);
            var moved = new List<string>();          // folders moved aside (old) - put back on failure
            var placed = new List<string>();         // new folders moved in - removed on failure
            string bdir = null;
            try
            {
                Directory.CreateDirectory(tmp);
                var tmpFull = Path.GetFullPath(tmp) + Path.DirectorySeparatorChar;
                try
                {
                    using (var za = ZipFile.OpenRead(zipFile))
                    {
                        var tops = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var en in za.Entries)
                        {
                            var name = en.FullName.Replace('\\', '/');
                            var parts = name.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                            if (name.StartsWith("/") || name.Contains(":") || parts.Any(p => p == ".." || p == "."))
                                throw new InvalidDataException("The zip contains unsafe paths - nothing was changed.");
                            if (parts.Length == 0) continue;
                            if (parts.Length == 1 && !name.EndsWith("/")) throw new InvalidDataException("The zip has files outside the addon's folders - nothing was changed.");
                            if (!want.Contains(parts[0])) throw new InvalidDataException($"The zip has a folder CurseForge doesn't list for this addon ({parts[0]}) - nothing was changed.");
                            tops.Add(parts[0]);
                        }
                        var missing = want.Where(m => !tops.Contains(m)).ToList();
                        if (missing.Count > 0) throw new InvalidDataException($"The zip is missing {string.Join(", ", missing)} - nothing was changed.");
                        foreach (var en in za.Entries)
                        {
                            var name = en.FullName.Replace('\\', '/');
                            var target = Path.GetFullPath(Path.Combine(tmp, name));
                            if (!target.StartsWith(tmpFull, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unsafe path in the zip - nothing was changed.");
                            if (name.EndsWith("/")) { Directory.CreateDirectory(target); continue; }
                            Directory.CreateDirectory(Path.GetDirectoryName(target));
                            en.ExtractToFile(target, true);
                        }
                    }
                }
                catch (InvalidDataException) { throw; }
                catch (Exception ex) when (!(ex is IOException)) { throw new InvalidDataException("The download isn't a valid zip file - nothing was changed."); }
                progress?.Report(0.8);

                var folders = want.Select(w => Directory.GetDirectories(tmp).Select(Path.GetFileName).First(d => string.Equals(d, w, StringComparison.OrdinalIgnoreCase))).ToList();
                foreach (var f in folders)
                {
                    var dest = Path.Combine(addOnsDir, f);
                    if (!Directory.Exists(dest)) continue;
                    if (Directory.Exists(Path.Combine(dest, ".git"))) throw new InvalidOperationException($"{f} is a development copy (git) - not touching it.");
                    if (!a.Folders.Contains(f, StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException($"The update would replace {f}, which belongs to a different addon - nothing was changed.");
                }
                // folders the installed version had and the new one doesn't (CurseForge removes those too); only ones this addon owns
                var retired = a.InstalledModules.Where(m => SafeFolder(m) && !want.Contains(m) && a.Folders.Contains(m, StringComparer.OrdinalIgnoreCase)
                    && Directory.Exists(Path.Combine(addOnsDir, m)) && !Directory.Exists(Path.Combine(addOnsDir, m, ".git"))).ToList();

                // back up what is there now
                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "__" + AddonSources.SafeName(Path.GetFileNameWithoutExtension(a.InstalledFileName ?? "unknown"));
                bdir = Path.Combine(AddonSources.BackupRoot, AddonSources.SafeName(key), stamp);
                foreach (var f in folders.Concat(retired))
                {
                    var dest = Path.Combine(addOnsDir, f);
                    if (Directory.Exists(dest)) Util.CopyDir(dest, Path.Combine(bdir, f));
                }
                progress?.Report(0.88);

                // swap: renames inside AddOns (same volume), undone completely on any failure (e.g. files locked by WoW)
                try
                {
                    Directory.CreateDirectory(oldRoot);
                    foreach (var f in retired) { Directory.Move(Path.Combine(addOnsDir, f), Path.Combine(oldRoot, f)); moved.Add(f); }
                    foreach (var f in folders)
                    {
                        var dest = Path.Combine(addOnsDir, f);
                        if (Directory.Exists(dest)) { Directory.Move(dest, Path.Combine(oldRoot, f)); moved.Add(f); }
                        SwapHook?.Invoke(f);
                        Directory.Move(Path.Combine(tmp, f), dest);
                        placed.Add(f);
                    }
                }
                catch (Exception ex)
                {
                    foreach (var f in placed.AsEnumerable().Reverse()) try { Util.DeleteDir(Path.Combine(addOnsDir, f)); } catch { }
                    foreach (var f in moved.AsEnumerable().Reverse())
                        try { var dest = Path.Combine(addOnsDir, f); if (!Directory.Exists(dest)) Directory.Move(Path.Combine(oldRoot, f), dest); } catch (Exception rex) { Util.Log("curseforge direct: couldn't put " + f + " back: " + rex.Message); }
                    try { Util.DeleteDir(bdir); } catch { }
                    bdir = null;
                    throw new IOException("Couldn't replace the addon folders (is WoW running and holding files open? close it and try again). Everything was put back. " + ex.Message);
                }
                res.Ok = true; res.Folders = folders; res.BackupDir = bdir;
                RememberLocalInstall(addOnsDir, a);
                AddonSources.Prune(key, 2);
                progress?.Report(1);
                Util.Log($"curseforge direct: installed {a.Id} file {a.LatestId} ({string.Join(",", folders)}), backup {bdir}");
                return res;
            }
            finally
            {
                try { Util.DeleteDir(tmp); } catch { }
                try { Util.DeleteDir(oldRoot); } catch { }
            }
        }

        // ---- what the Outpost installed until CurseForge has re-scanned: "<addOnsDir>|<addonId>|<fileId>|<fileName>" per line
        static readonly object recLock = new object();
        static string RecordFile => Path.Combine(Util.DataDir, "cfdirect.txt");

        static string NormDir(string p) { try { return Path.GetFullPath(p ?? "").TrimEnd('\\', '/').ToLowerInvariant(); } catch { return (p ?? "").ToLowerInvariant(); } }

        static List<string[]> ReadRecords()
        {
            try { return File.Exists(RecordFile) ? File.ReadAllLines(RecordFile).Select(l => l.Split('|')).Where(p => p.Length == 4).ToList() : new List<string[]>(); }
            catch { return new List<string[]>(); }
        }

        static void WriteRecords(List<string[]> recs)
        {
            try { if (recs.Count == 0) { if (File.Exists(RecordFile)) File.Delete(RecordFile); } else File.WriteAllLines(RecordFile, recs.Select(r => string.Join("|", r))); }
            catch (Exception e) { Util.Log("cfdirect record: " + e.Message); }
        }

        static void RememberLocalInstall(string addOnsDir, CfAddon a)
        {
            lock (recLock)
            {
                var dir = NormDir(addOnsDir);
                var recs = ReadRecords().Where(r => !(r[0] == dir && r[1] == a.Id.ToString())).ToList();
                recs.Add(new[] { dir, a.Id.ToString(), a.LatestId.ToString(), (a.LatestFileName ?? "").Replace("|", "_") });
                WriteRecords(recs);
            }
        }

        // CurseForgeLocal.Load: the file the Outpost installed counts as installed; once CurseForge itself lists it, the note is dropped
        public static void ApplyLocalInstall(CfAddon add, string addOnsDir)
        {
            try
            {
                lock (recLock)
                {
                    if (!File.Exists(RecordFile)) return;
                    var dir = NormDir(addOnsDir);
                    var recs = ReadRecords();
                    var r = recs.FirstOrDefault(x => x[0] == dir && x[1] == add.Id.ToString());
                    if (r == null || !long.TryParse(r[2], out var file)) return;
                    if (add.InstalledId == file) { recs.Remove(r); WriteRecords(recs); return; }   // CurseForge caught up
                    add.InstalledId = file;
                    if (r[3].Length > 0) add.InstalledFileName = r[3];
                }
            }
            catch (Exception e) { Util.Log("cfdirect apply: " + e.Message); }
        }

        internal static void ForgetLocalInstalls(string addOnsDir)
        {
            lock (recLock) { var dir = NormDir(addOnsDir); WriteRecords(ReadRecords().Where(r => r[0] != dir).ToList()); }
        }

        // ---- self-test: own temp AddOns + WTF folders, a local http server standing in for forgecdn; never CurseForge, never the WoW folder
        public static async Task<string> SelfTest()
        {
            var sb = new StringBuilder();
            void Check(string what, bool c, string info = null) => sb.AppendLine($"curseforge direct {what}: {(c ? "ok" : "FAIL")}{(info == null ? "" : " (" + info + ")")}");
            var root = Path.Combine(Path.GetTempPath(), "elanshub-cfdirect-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var ao = Path.Combine(root, "_classic_beta_", "Interface", "AddOns");
            var wtf = Path.Combine(root, "_classic_beta_", "WTF", "Account", "X", "SavedVariables", "CfDirA.lua");
            var files = new Dictionary<string, byte[]>();
            int hits = 0;
            HttpListener listener = null;
            var oldRewrite = RewriteForTest; var oldHook = SwapHook;
            const string key = "CfDirA";
            try
            {
                // tiny local server: /files/... = the registered bytes, /redirect = 302 to a non-forgecdn address on the same server
                int port = 0;
                for (int i = 0; i < 20 && listener == null; i++)
                {
                    var t = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); t.Start(); port = ((IPEndPoint)t.LocalEndpoint).Port; t.Stop();
                    var l = new HttpListener(); l.Prefixes.Add("http://127.0.0.1:" + port + "/");
                    try { l.Start(); listener = l; } catch { l.Close(); }
                }
                if (listener == null) { Check("local test server", false); return sb.ToString(); }
                var srv = listener;
                _ = Task.Run(async () =>
                {
                    while (srv.IsListening)
                    {
                        HttpListenerContext c;
                        try { c = await srv.GetContextAsync(); } catch { return; }
                        try
                        {
                            Interlocked.Increment(ref hits);
                            var path = c.Request.Url.AbsolutePath;
                            if (path.StartsWith("/redirect")) { c.Response.StatusCode = 302; c.Response.RedirectLocation = "http://127.0.0.1:" + port + path.Substring("/redirect".Length); c.Response.Close(); continue; }
                            if (!files.TryGetValue(path, out var body)) { c.Response.StatusCode = 404; c.Response.Close(); continue; }
                            c.Response.ContentType = "application/zip"; c.Response.ContentLength64 = body.Length;
                            c.Response.OutputStream.Write(body, 0, body.Length); c.Response.Close();
                        }
                        catch { try { c.Response.Abort(); } catch { } }
                    }
                });
                RewriteForTest = u => new Uri("http://127.0.0.1:" + port + u.AbsolutePath);

                byte[] Zip(params string[] entries)       // "path=text"
                {
                    using (var ms = new MemoryStream())
                    {
                        using (var za = new ZipArchive(ms, ZipArchiveMode.Create, true))
                            foreach (var e in entries) { var i = e.IndexOf('='); var en = za.CreateEntry(e.Substring(0, i)); using (var w = new StreamWriter(en.Open())) w.Write(e.Substring(i + 1)); }
                        return ms.ToArray();
                    }
                }
                string Hex(HashAlgorithm h, byte[] b) { using (h) return string.Concat(h.ComputeHash(b).Select(x => x.ToString("x2"))); }
                CfAddon Addon(string path, byte[] body, bool goodHash = true)
                {
                    files[path] = body;
                    var a = new CfAddon
                    {
                        Id = 5001, Name = "CfDirA", InstalledId = 1, LatestId = 2, InstalledFileName = "CfDirA-1.0.zip", LatestFileName = "CfDirA-2.0.zip",
                        Folders = new List<string> { "CfDirA", "CfDirA_Opts", "CfDirA_Old" }, AllowDistribution = true,
                        LatestUrl = "https://edge.forgecdn.net" + path, LatestLength = body.Length,
                        LatestModules = new List<string> { "CfDirA", "CfDirA_Opts" }, InstalledModules = new List<string> { "CfDirA", "CfDirA_Opts", "CfDirA_Old" },
                    };
                    a.LatestHashes.Add(new KeyValuePair<string, string>("sha1", goodHash ? Hex(SHA1.Create(), body) : new string('0', 40)));
                    a.LatestHashes.Add(new KeyValuePair<string, string>("md5", Hex(MD5.Create(), body)));
                    return a;
                }
                void Reset()
                {
                    if (Directory.Exists(ao)) Util.DeleteDir(ao);
                    foreach (var f in new[] { "CfDirA", "CfDirA_Opts", "CfDirA_Old" }) { Directory.CreateDirectory(Path.Combine(ao, f)); File.WriteAllText(Path.Combine(ao, f, f + ".toc"), "## Interface: 11508\n## Version: 1.0\n"); }
                    Directory.CreateDirectory(Path.Combine(ao, "Neighbour")); File.WriteAllText(Path.Combine(ao, "Neighbour", "Neighbour.toc"), "## Version: 7\n");
                    Directory.CreateDirectory(Path.GetDirectoryName(wtf)); File.WriteAllText(wtf, "CfDirADB = { keep = true }");
                    foreach (var b in AddonSources.Backups(key)) Util.DeleteDir(b);
                    ForgetLocalInstalls(ao);
                }
                string State() => string.Join(";", Directory.GetDirectories(ao).OrderBy(x => x).Select(d => Path.GetFileName(d) + ":" + string.Join(",", Directory.GetFiles(d, "*", SearchOption.AllDirectories).OrderBy(x => x).Select(f => f.Substring(d.Length) + "=" + File.ReadAllText(f)))));
                bool Clean() => Directory.GetDirectories(ao, ".elanshub-*").Length == 0;
                var v2 = Zip("CfDirA/CfDirA.toc=## Interface: 11508\n## Version: 2.0\n", "CfDirA/Libs/x.lua=-- v2", "CfDirA_Opts/CfDirA_Opts.toc=## Version: 2.0\n");

                // 1) hash ok -> installed, backup kept, retired folder gone, SavedVariables untouched, card counts it as installed
                Reset();
                var a1 = Addon("/files/1/2/CfDirA-2.0.zip", v2);
                var plan = Decide(a1, new CfInstance(), true);
                Check("allowed addon goes direct", plan.Direct, plan.Reason);
                var r = await Update(ao, key, a1);
                var toc = File.Exists(Path.Combine(ao, "CfDirA", "CfDirA.toc")) ? File.ReadAllText(Path.Combine(ao, "CfDirA", "CfDirA.toc")) : "";
                var bk = AddonSources.Backups(key).FirstOrDefault();
                Check("hash ok -> installed", r.Ok && toc.Contains("2.0") && File.Exists(Path.Combine(ao, "CfDirA", "Libs", "x.lua")), r.Error);
                Check("hash ok -> backup of the old folders", bk != null && File.ReadAllText(Path.Combine(bk, "CfDirA", "CfDirA.toc")).Contains("1.0") && Directory.Exists(Path.Combine(bk, "CfDirA_Old")), bk);
                Check("folder the new file dropped is removed (kept in the backup)", !Directory.Exists(Path.Combine(ao, "CfDirA_Old")));
                Check("SavedVariables and other addons untouched", File.ReadAllText(wtf) == "CfDirADB = { keep = true }" && File.ReadAllText(Path.Combine(ao, "Neighbour", "Neighbour.toc")) == "## Version: 7\n" && Clean());
                var again = new CfAddon { Id = 5001, InstalledId = 1, LatestId = 2, LatestFileName = "CfDirA-2.0.zip" };
                ApplyLocalInstall(again, ao);
                Check("card shows the new version until CurseForge re-scans", !again.UpdateAvailable && again.InstalledFileName == "CfDirA-2.0.zip");
                var caught = new CfAddon { Id = 5001, InstalledId = 2, LatestId = 2 };
                ApplyLocalInstall(caught, ao);
                var later = new CfAddon { Id = 5001, InstalledId = 1, LatestId = 2 };
                ApplyLocalInstall(later, ao);
                Check("note dropped once CurseForge lists the file", later.InstalledId == 1);
                var otherDir = new CfAddon { Id = 5001, InstalledId = 1, LatestId = 2 };
                RememberLocalInstall(ao, a1); ApplyLocalInstall(otherDir, Path.Combine(root, "_retail_", "Interface", "AddOns"));
                Check("note is per WoW folder", otherDir.InstalledId == 1);
                ForgetLocalInstalls(ao);

                // 2) hash mismatch -> nothing changed
                Reset(); var before = State();
                r = await Update(ao, key, Addon("/files/1/3/CfDirA-2.0.zip", v2, goodHash: false));
                Check("hash mismatch -> nothing changed", !r.Ok && r.Error.Contains("checksum") && State() == before && AddonSources.Backups(key).Count == 0 && Clean(), r.Error);
                // size mismatch
                var aSize = Addon("/files/1/4/CfDirA-2.0.zip", v2); aSize.LatestLength += 5;
                r = await Update(ao, key, aSize);
                Check("size mismatch -> nothing changed", !r.Ok && r.Error.Contains("size") && State() == before, r.Error);

                // 3) hosts: only https *.forgecdn.net, also after redirects
                Check("forgecdn hosts accepted", UrlProblem("https://edge.forgecdn.net/files/1/2/a.zip") == null && UrlProblem("https://mediafilez.forgecdn.net/files/1/2/a.zip") == null);
                Check("other hosts refused", new[] { "https://evil.example.com/a.zip", "https://forgecdn.net.evil.com/a.zip", "https://evilforgecdn.net/a.zip", "http://edge.forgecdn.net/a.zip",
                    "https://user@edge.forgecdn.net/a.zip", "https://edge.forgecdn.net:8443/a.zip", "file:///C:/a.zip", "C:\\a.zip", "" }.All(u => UrlProblem(u) != null));
                var hits0 = hits;
                var aEvil = Addon("/files/9/9/evil.zip", v2); aEvil.LatestUrl = "https://downloads.example.com/files/9/9/evil.zip";
                plan = Decide(aEvil, new CfInstance(), true);
                r = await Update(ao, key, aEvil);
                Check("non-forgecdn host refused (nothing downloaded)", !plan.Direct && !r.Ok && hits == hits0 && State() == before, r.Error);
                var aRedir = Addon("/files/1/5/CfDirA-2.0.zip", v2); aRedir.LatestUrl = "https://edge.forgecdn.net/redirect/files/1/5/CfDirA-2.0.zip";
                r = await Update(ao, key, aRedir);
                Check("redirect to another host refused", !r.Ok && r.Error.Contains("refused") && State() == before, r.Error);

                // 4) zip layout
                r = await Update(ao, key, Addon("/files/1/6/slip.zip", Zip("CfDirA/../../evil.txt=x", "CfDirA/CfDirA.toc=## Version: 9\n", "CfDirA_Opts/CfDirA_Opts.toc=x")));
                Check("zip with a ../ path refused", !r.Ok && r.Error.Contains("unsafe") && State() == before && !File.Exists(Path.Combine(root, "_classic_beta_", "Interface", "evil.txt")) && Clean(), r.Error);
                r = await Update(ao, key, Addon("/files/1/7/extra.zip", Zip("CfDirA/CfDirA.toc=x", "CfDirA_Opts/CfDirA_Opts.toc=x", "Neighbour/Neighbour.toc=hijack")));
                Check("zip with a folder CurseForge doesn't list refused", !r.Ok && State() == before && Clean(), r.Error);
                r = await Update(ao, key, Addon("/files/1/8/loose.zip", Zip("CfDirA/CfDirA.toc=x", "CfDirA_Opts/CfDirA_Opts.toc=x", "readme.txt=x")));
                Check("zip with loose files refused", !r.Ok && State() == before, r.Error);
                r = await Update(ao, key, Addon("/files/1/9/notzip.zip", Encoding.UTF8.GetBytes("not a zip")));
                Check("damaged zip refused", !r.Ok && State() == before && Clean(), r.Error);

                // 5) the author / CurseForge say no -> the deep link
                var inst = new CfInstance(); inst.InstalledIds.Add(77);
                var aNo = Addon("/files/2/1/a.zip", v2); aNo.AllowDistribution = false;
                plan = Decide(aNo, inst, true);
                Check("allowModDistribution false -> deep link", !plan.Direct && plan.AuthorOnly && plan.Reason.StartsWith(AuthorOnlyText), plan.Reason);
                var aLock = Addon("/files/2/1/a.zip", v2); aLock.IsLocked = true;
                var aIgn = Addon("/files/2/1/a.zip", v2); aIgn.IsIgnored = true;
                Check("locked / ignored in CurseForge -> deep link", !Decide(aLock, inst, true).Direct && !Decide(aIgn, inst, true).Direct);
                var aDep = Addon("/files/2/1/a.zip", v2); aDep.RequiredDeps.Add(88);
                plan = Decide(aDep, inst, true);
                Check("missing required dependency -> deep link", !plan.Direct && plan.Reason.Contains("isn't installed"), plan.Reason);
                aDep.RequiredDeps[0] = 77;
                Check("required dependency installed -> direct", Decide(aDep, inst, true).Direct);
                Check("setting off -> deep link", !Decide(Addon("/files/2/1/a.zip", v2), inst, false).Direct && IsOn != null);
                var aNoHash = Addon("/files/2/1/a.zip", v2); aNoHash.LatestHashes.Clear();
                Check("no checksum -> deep link", !Decide(aNoHash, inst, true).Direct);

                // 6) failure mid-swap -> everything put back
                Reset(); before = State();
                SwapHook = f => { if (f.Equals("CfDirA_Opts", StringComparison.OrdinalIgnoreCase)) throw new IOException("simulated lock"); };
                r = await Update(ao, key, Addon("/files/3/1/CfDirA-2.0.zip", v2));
                SwapHook = oldHook;
                Check("failure mid-swap -> rolled back", !r.Ok && r.Error.Contains("put back") && State() == before && AddonSources.Backups(key).Count == 0 && Clean(), r.Error + " | " + State());

                // 7) the format reader: CurseForge's own field shapes
                var json = "[{\"name\":\"T\",\"installPath\":" + Q(Path.Combine(root, "_classic_beta_") + "\\") + ",\"installedAddons\":[{\"addonID\":5001,\"name\":\"CfDirA\",\"thumbnailUrl\":\"https://media.forgecdn.net/avatars/thumbnails/1/2/256/256/x.png\",\"allowModDistribution\":true,\"isLocked\":false,"
                    + "\"preferenceIsIgnored\":false,\"modFolderPath\":" + Q(ao) + ",\"filePaths\":[" + Q(Path.Combine(ao, "CfDirA")) + "],"
                    + "\"installedFile\":{\"id\":1,\"fileName\":\"CfDirA-1.0.zip\",\"modules\":[{\"foldername\":\"CfDirA\",\"fingerprint\":1},{\"foldername\":\"CfDirA_Old\",\"fingerprint\":2}]},"
                    + "\"latestFile\":{\"id\":2,\"fileName\":\"CfDirA-2.0.zip\",\"fileLength\":1234,\"downloadUrl\":\"https://edge.forgecdn.net/files/1/2/CfDirA-2.0.zip\","
                    + "\"hashes\":[{\"type\":1,\"value\":\"ABCDEF\"},{\"type\":2,\"value\":\"0123\"}],\"modules\":[{\"foldername\":\"CfDirA\",\"fingerprint\":3}],"
                    + "\"dependencies\":[{\"addonId\":42,\"type\":3},{\"addonId\":43,\"type\":1},{\"addonId\":44,\"type\":2}]}},{\"addonID\":42,\"name\":\"Lib\",\"filePaths\":[]}]}]";
                var jf = Path.Combine(root, "AddonGameInstance.json"); File.WriteAllText(jf, json);
                var ci = CurseForgeLocal.Load(ao, jf);
                var ca = ci?.Addons.FirstOrDefault(x => x.Id == 5001);
                Check("reads CurseForge's fields", ca != null && ca.AllowDistribution && ca.ThumbnailUrl == "https://media.forgecdn.net/avatars/thumbnails/1/2/256/256/x.png" && ca.LatestLength == 1234 && ca.LatestUrl.StartsWith("https://edge.forgecdn.net/")
                    && ca.LatestHashes.Any(h => h.Key == "sha1" && h.Value == "abcdef") && ca.LatestHashes.Any(h => h.Key == "md5" && h.Value == "0123")
                    && string.Join(",", ca.LatestModules) == "CfDirA" && string.Join(",", ca.InstalledModules) == "CfDirA,CfDirA_Old"
                    && string.Join(",", ca.RequiredDeps) == "42" && ci.InstalledIds.Contains(42), ca == null ? "missing" : string.Join(",", ca.RequiredDeps));
                Check("required dependency present in the instance -> direct", ca != null && Decide(ca, ci, true).Direct && Decide(ca, null, true).Direct && ca.MissingDeps.Count == 0);
                File.WriteAllText(jf, json.Replace("\"allowModDistribution\":true,", ""));
                ca = CurseForgeLocal.Load(ao, jf)?.Addons.FirstOrDefault(x => x.Id == 5001);
                Check("allowModDistribution missing counts as not allowed", ca != null && !ca.AllowDistribution);
            }
            catch (Exception e) { sb.AppendLine("curseforge direct selftest crashed: " + e); sb.AppendLine("curseforge direct: FAIL"); }
            finally
            {
                RewriteForTest = oldRewrite; SwapHook = oldHook;
                try { listener?.Stop(); listener?.Close(); } catch { }
                try { ForgetLocalInstalls(ao); foreach (var b in AddonSources.Backups(key)) Util.DeleteDir(b); } catch { }
                try { Util.DeleteDir(root); } catch { }
            }
            return sb.ToString();
        }

        static string Q(string s) => "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
