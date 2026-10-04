using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ElansAddonHub.Services
{
    // What the hub remembers per third-party addon (keyed by its primary folder).
    [DataContract]
    public class AddonLink
    {
        [DataMember(Name = "key")] public string Key { get; set; }
        [DataMember(Name = "github")] public string Github { get; set; }       // owner/repo the user (or the toc) pointed to
        [DataMember(Name = "wowi")] public string Wowi { get; set; }           // WoWInterface file id
        [DataMember(Name = "asset")] public string Asset { get; set; }         // zip the user chose when it wasn't clear
        [DataMember(Name = "installed")] public string InstalledRemote { get; set; } // remote version the hub installed last
    }

    [DataContract]
    class LinkFile { [DataMember(Name = "links")] public List<AddonLink> Links { get; set; } = new List<AddonLink>(); }

    public class RemoteInfo
    {
        public string Source;            // "GitHub" | "WoWInterface"
        public string Version;
        public DateTime? Date;
        public string Url;               // zip download (GitHub); WoWInterface resolves it at install time
        public string Asset;             // chosen zip name
        public List<string> Choices = new List<string>();
        public bool NeedsChoice;
        public string Page;
        public string WowiId;
    }

    // Keyless update sources: GitHub releases (unauthenticated API) and WoWInterface's public API.
    // CurseForge/Wago are links only (their APIs need keys, scraping is not allowed).
    public static class AddonSources
    {
        static readonly string LinkPath = Path.Combine(Util.DataDir, "addonlinks.json");
        static readonly string CacheDir = Path.Combine(Util.DataDir, "cache");
        static readonly TimeSpan Ttl = TimeSpan.FromHours(1);
        static DateTime ghBlockedUntil = DateTime.MinValue;
        static Dictionary<string, Dictionary<string, object>> wowiList;
        static DateTime wowiLoaded = DateTime.MinValue;

        // ------------------------------------------------------------------ saved links
        static List<AddonLink> links;

        static List<AddonLink> Links()
        {
            if (links != null) return links;
            try { if (File.Exists(LinkPath)) links = Util.FromJson<LinkFile>(File.ReadAllText(LinkPath)).Links; } catch (Exception e) { Util.Log("links load failed: " + e.Message); }
            return links = links ?? new List<AddonLink>();
        }

        public static AddonLink LinkFor(string key)
        {
            var l = Links().FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
            if (l == null) { l = new AddonLink { Key = key }; Links().Add(l); }
            return l;
        }

        public static void SaveLinks()
        {
            try { File.WriteAllText(LinkPath, Util.ToJson(new LinkFile { Links = Links().Where(l => l.Github != null || l.Wowi != null || l.Asset != null || l.InstalledRemote != null).ToList() })); }
            catch (Exception e) { Util.Log("links save failed: " + e.Message); }
        }

        // ------------------------------------------------------------------ http helpers
        static string CacheFile(string url)
        {
            Directory.CreateDirectory(CacheDir);
            return Path.Combine(CacheDir, Util.Sha256Text(url).Substring(0, 24) + ".json");
        }

        // cached for an hour on disk; null on any failure (offline, rate limited) unless a stale copy exists
        public static bool Force;   // "Check for updates" button: only trust cache younger than 2 minutes
        static async Task<string> CachedGet(string url, bool github)
        {
            var cf = CacheFile(url);
            string stale = null;
            try
            {
                if (File.Exists(cf))
                {
                    stale = File.ReadAllText(cf);
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(cf) < (Force ? TimeSpan.FromMinutes(2) : Ttl)) return stale;
                }
            }
            catch { }
            if (github && DateTime.UtcNow < ghBlockedUntil) return stale;
            try
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25)))
                using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    if (github) req.Headers.Accept.ParseAdd("application/vnd.github+json");
                    using (var resp = await Net.Http.SendAsync(req, cts.Token))
                    {
                        if (github && resp.Headers.TryGetValues("X-RateLimit-Remaining", out var rem) && rem.FirstOrDefault() == "0")
                        {
                            var until = DateTime.UtcNow.AddMinutes(30);
                            if (resp.Headers.TryGetValues("X-RateLimit-Reset", out var rs) && long.TryParse(rs.FirstOrDefault(), out var epoch))
                                until = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(epoch);
                            ghBlockedUntil = until;
                            Util.Log("github rate limit reached, pausing until " + until.ToLocalTime());
                        }
                        if (!resp.IsSuccessStatusCode)
                        {
                            if (github && (int)resp.StatusCode == 403) ghBlockedUntil = DateTime.UtcNow.AddMinutes(30);
                            return stale;
                        }
                        var text = await resp.Content.ReadAsStringAsync();
                        try { File.WriteAllText(cf, text); } catch { }
                        return text;
                    }
                }
            }
            catch (Exception e) { Util.Log("fetch failed " + url + ": " + e.Message); return stale; }
        }

        // ------------------------------------------------------------------ lookups
        public static async Task<RemoteInfo> Lookup(AddonEntry e, AddonLink link, int clientInterface)
        {
            try
            {
                var gh = link.Github ?? e.GithubRepo;
                if (!string.IsNullOrEmpty(gh))
                {
                    var r = await LookupGithub(gh, link, clientInterface);
                    if (r != null) return r;
                }
                var wid = link.Wowi ?? e.WowiId;
                if (!string.IsNullOrEmpty(wid)) return await LookupWowi(wid);
            }
            catch (Exception ex) { Util.Log("lookup failed " + e.Key + ": " + ex.Message); }
            return null;
        }

        static async Task<RemoteInfo> LookupGithub(string repo, AddonLink link, int iface)
        {
            var json = await CachedGet($"https://api.github.com/repos/{repo}/releases?per_page=8", true);
            if (json == null) return null;
            var arr = Json.Parse(json) as List<object>;
            if (arr == null) return null;
            var rels = arr.OfType<Dictionary<string, object>>().Where(r => !r.Bool("draft")).ToList();
            var rel = rels.FirstOrDefault(r => !r.Bool("prerelease")) ?? rels.FirstOrDefault();
            if (rel == null) return null;

            var assets = rel.List("assets").OfType<Dictionary<string, object>>()
                .Select(a => new { Name = a.Str("name") ?? "", Url = a.Str("browser_download_url") }).Where(a => a.Url != null).ToList();
            var r2 = new RemoteInfo { Source = "GitHub", Version = rel.Str("tag_name"), Page = rel.Str("html_url") ?? $"https://github.com/{repo}/releases" };
            if (DateTime.TryParse(rel.Str("published_at"), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d)) r2.Date = d;
            var zips = assets.Where(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).ToList();
            r2.Choices = zips.Select(z => z.Name).ToList();
            if (zips.Count == 0) return r2;

            string pick = null;
            if (!string.IsNullOrEmpty(link.Asset) && zips.Any(z => z.Name == link.Asset)) pick = link.Asset;   // the user decided earlier
            var rj = assets.FirstOrDefault(a => a.Name.Equals("release.json", StringComparison.OrdinalIgnoreCase));
            if (pick == null && rj != null)
            {
                var rjText = await CachedGet(rj.Url, false);
                if (rjText != null) pick = PickFromReleaseJson(rjText, iface, zips.Select(z => z.Name).ToList());
            }
            if (pick == null) pick = PickAssetByName(zips.Select(z => z.Name).ToList(), iface);
            if (pick != null) { r2.Asset = pick; r2.Url = zips.First(z => z.Name == pick).Url; }
            else r2.NeedsChoice = true;
            return r2;
        }

        // BigWigs packager release.json: releases[].metadata[].flavor/interface
        public static string PickFromReleaseJson(string text, int iface, List<string> zipNames)
        {
            var root = Json.Obj(text);
            if (root == null) return null;
            string best = null; int bestScore = 0; bool bestLib = false;
            var wantFlavors = TargetFlavors(iface);
            foreach (var rel in root.List("releases").OfType<Dictionary<string, object>>())
            {
                var file = rel.Str("filename");
                if (file == null || !zipNames.Contains(file)) continue;
                int score = 0;
                foreach (var md in rel.List("metadata").OfType<Dictionary<string, object>>())
                {
                    var mi = md.Int("interface");
                    var fl = (md.Str("flavor") ?? "").ToLowerInvariant();
                    int s = 0;
                    if (iface > 0 && mi == iface) s = 3;
                    else if (wantFlavors.Contains(fl)) s = 2;
                    else if (iface > 0 && mi > 0 && !AddonScanner.IsForever(iface) && AddonScanner.Major(mi) == AddonScanner.Major(iface) && fl != "forever" && fl != "camelot") s = 1;   // Classic/Era never counts for WoW Forever
                    score = Math.Max(score, s);
                }
                var lib = !rel.Bool("nolib");
                if (score > bestScore || (score == bestScore && score > 0 && lib && !bestLib)) { best = file; bestScore = score; bestLib = lib; }
            }
            return bestScore > 0 ? best : null;
        }

        static HashSet<string> TargetFlavors(int iface)
        {
            var h = new HashSet<string>();
            if (AddonScanner.IsForever(iface)) { h.Add("forever"); h.Add("camelot"); return h; }   // only Forever builds, never classic
            switch (AddonScanner.Major(iface))
            {
                case 1: h.Add("classic"); break;
                case 2: h.Add("bcc"); h.Add("tbc"); break;
                case 3: h.Add("wrath"); break;
                case 4: h.Add("cata"); break;
                case 5: h.Add("mists"); break;
                case 99: h.Add("mainline"); break;
            }
            return h;
        }

        static string FlavorOfName(string name)
        {
            var n = name.ToLowerInvariant();
            if (Regex.IsMatch(n, @"wrath|wotlk")) return "wrath";
            if (Regex.IsMatch(n, @"cata")) return "cata";
            if (Regex.IsMatch(n, @"bcc|tbc|burning")) return "tbc";
            if (Regex.IsMatch(n, @"mists|[-_.]mop")) return "mists";
            if (Regex.IsMatch(n, @"mainline|retail")) return "mainline";
            if (Regex.IsMatch(n, @"forever|camelot")) return "forever";
            if (Regex.IsMatch(n, @"classic|vanilla|[-_.]era[-_.]")) return "vanilla";
            return null;
        }

        // a download made for Classic / Era / another expansion: offered only by hand, with a warning, for WoW Forever
        public static bool IsClassicAsset(string name) { var f = FlavorOfName(name ?? ""); return f != null && f != "forever"; }

        static string TargetFlavorName(int iface)
        {
            if (AddonScanner.IsForever(iface)) return "forever";
            switch (AddonScanner.Major(iface))
            {
                case 1: return "vanilla";
                case 2: return "tbc";
                case 3: return "wrath";
                case 4: return "cata";
                case 5: return "mists";
                case 99: return "mainline";
                default: return null;
            }
        }

        // no release.json: choose by the file names, but only when it's clearly unambiguous
        public static string PickAssetByName(List<string> zips, int iface)
        {
            var nonSrc = zips.Where(z => !Regex.IsMatch(z, @"source|nolib", RegexOptions.IgnoreCase)).ToList();
            if (nonSrc.Count == 0) nonSrc = zips;
            var target = TargetFlavorName(iface);
            var flavored = nonSrc.ToDictionary(z => z, FlavorOfName);
            if (target != null)
            {
                var match = nonSrc.Where(z => flavored[z] == target).ToList();
                if (match.Count == 1) return match[0];
                if (match.Count > 1) return null;
            }
            var plain = nonSrc.Where(z => flavored[z] == null).ToList();
            if (target == "forever") return plain.Count == 1 && nonSrc.All(z => flavored[z] == null) ? plain[0] : null;   // Classic/Era builds are only offered via "Choose file"
            bool otherFlavors = nonSrc.Any(z => flavored[z] != null);
            if (plain.Count == 1 && (!otherFlavors || target == "mainline")) return plain[0];
            if (nonSrc.Count == 1 && flavored[nonSrc[0]] == null) return nonSrc[0];
            return null;
        }

        static async Task<RemoteInfo> LookupWowi(string id)
        {
            if (wowiList == null || DateTime.UtcNow - wowiLoaded > Ttl)
            {
                var json = await CachedGet("https://api.mmoui.com/v3/game/WOW/filelist.json", false);
                if (json == null) return null;
                var parsed = await Task.Run(() =>
                {
                    var d = new Dictionary<string, Dictionary<string, object>>();
                    foreach (var o in (Json.Parse(json) as List<object> ?? new List<object>()).OfType<Dictionary<string, object>>())
                        if (o.Str("UID") is string uid) d[uid] = o;
                    return d;
                });
                wowiList = parsed; wowiLoaded = DateTime.UtcNow;
            }
            if (!wowiList.TryGetValue(id, out var e)) return null;
            var r = new RemoteInfo { Source = "WoWInterface", Version = e.Str("UIVersion"), Page = e.Str("UIFileInfoURL") ?? $"https://www.wowinterface.com/downloads/info{id}", WowiId = id };
            var ms = e.Long("UIDate");
            if (ms > 0) r.Date = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ms);
            return r;
        }

        // ------------------------------------------------------------------ comparing
        static string Numeric(string v)
        {
            var m = Regex.Match(v ?? "", @"\d+(\.\d+)*");
            return m.Success ? m.Value : null;
        }

        // Is the remote release newer than what is installed?
        public static bool IsNewer(AddonEntry e, AddonLink link, RemoteInfo r)
        {
            if (r == null || string.IsNullOrEmpty(r.Version)) return false;
            var rv = r.Version.Trim();
            if (!string.IsNullOrEmpty(link.InstalledRemote))
            {
                // we installed it: the toc may not carry a comparable version, but we know which release we put there
                if (string.Equals(link.InstalledRemote, rv, StringComparison.OrdinalIgnoreCase)) return false;
                var a = Numeric(rv); var b = Numeric(link.InstalledRemote);
                if (a != null && b != null) return Util.CompareVersions(a, b) > 0;
                return true;
            }
            var lv = (e.Version ?? "").Trim();
            var ln = Numeric(lv); var rn = Numeric(rv);
            if (ln != null && rn != null)
            {
                var c = Util.CompareVersions(rn, ln);
                if (c != 0) return c > 0;
                return false;
            }
            if (lv.TrimStart('v', 'V') == rv.TrimStart('v', 'V')) return false;
            // versions can't be compared: the remote must be clearly newer by date than the installed files
            return r.Date != null && r.Date.Value > e.Modified.AddDays(1);
        }

        // ------------------------------------------------------------------ install / rollback
        static readonly string BackupRoot = Path.Combine(Util.DataDir, "backups", "addons");

        public static string SafeName(string s) => Regex.Replace(s, @"[^A-Za-z0-9_.-]", "_");

        public static List<string> Backups(string key)
        {
            var dir = Path.Combine(BackupRoot, SafeName(key));
            if (!Directory.Exists(dir)) return new List<string>();
            return new DirectoryInfo(dir).GetDirectories().OrderByDescending(d => d.Name).Select(d => d.FullName).ToList();
        }

        public class InstallResult { public string Warning; public List<string> Folders = new List<string>(); }

        // Download (or take a local zip), verify, back up, swap folders. Never touches WTF.
        public static async Task<InstallResult> Install(string addOnsDir, AddonEntry e, AddonLink link, RemoteInfo r, int iface, IProgress<double> progress)
        {
            if (e.IsDev) throw new InvalidOperationException("This is a development copy (git) - not touching it.");
            var work = Path.Combine(Path.GetTempPath(), "ElansAddonHub-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            try
            {
                var zip = Path.Combine(work, "addon.zip");
                var url = r.Url; string md5 = null;
                if (r.Source == "WoWInterface")
                {
                    using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25)))
                    {
                        var resp = await Net.Http.GetAsync($"https://api.mmoui.com/v3/game/WOW/filedetails/{r.WowiId}.json", cts.Token);
                        resp.EnsureSuccessStatusCode();
                        var d = (Json.Parse(await resp.Content.ReadAsStringAsync()) as List<object>)?.OfType<Dictionary<string, object>>().FirstOrDefault();
                        url = d?.Str("UIDownload"); md5 = d?.Str("UIMD5");
                        if (url == null) throw new InvalidDataException("WoWInterface gave no download for this addon.");
                    }
                }
                if (url == null) throw new InvalidDataException("No file to download - choose one first.");
                await Net.Download(url, zip, new Progress<double>(p => progress?.Report(p * 0.7)));
                if (!string.IsNullOrEmpty(md5) && !string.Equals(Util.Md5(zip), md5, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Download is damaged (checksum mismatch) - try again.");
                return await Task.Run(() => InstallZip(addOnsDir, e, link, r.Version, iface, zip, progress));
            }
            finally { try { Util.DeleteDir(work); } catch { } }
        }

        // the part after the download; also what the self-test drives with a synthetic zip
        public static InstallResult InstallZip(string addOnsDir, AddonEntry e, AddonLink link, string remoteVersion, int iface, string zipFile, IProgress<double> progress)
        {
            var res = new InstallResult();
            var tmp = Path.Combine(addOnsDir, ".elanshub-new-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var oldRoot = Path.Combine(addOnsDir, ".elanshub-old-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var swapped = new List<string>();
            try
            {
                Directory.CreateDirectory(tmp);
                var tops = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var tocFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    using (var za = ZipFile.OpenRead(zipFile))
                    {
                        foreach (var en in za.Entries)
                        {
                            var name = en.FullName.Replace('\\', '/');
                            if (name.StartsWith("/") || name.Split('/').Any(p => p == "..") || name.Contains(":"))
                                throw new InvalidDataException("The zip contains unsafe paths - not installing it.");
                            var parts = name.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length == 0) continue;
                            if (parts.Length == 1 && !name.EndsWith("/")) continue;      // loose file in the zip root: ignored
                            tops.Add(parts[0]);
                            if (parts.Length == 2 && parts[1].EndsWith(".toc", StringComparison.OrdinalIgnoreCase)) tocFolders.Add(parts[0]);
                        }
                        if (tocFolders.Count == 0) throw new InvalidDataException("This zip has no addon folder with a .toc file.");
                        foreach (var en in za.Entries)
                        {
                            var name = en.FullName.Replace('\\', '/');
                            var parts = name.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length < 2 || !tocFolders.Contains(parts[0])) continue;
                            var target = Path.GetFullPath(Path.Combine(tmp, name));
                            if (!target.StartsWith(Path.GetFullPath(tmp) + Path.DirectorySeparatorChar)) throw new InvalidDataException("Unsafe path in zip.");
                            if (name.EndsWith("/")) { Directory.CreateDirectory(target); continue; }
                            Directory.CreateDirectory(Path.GetDirectoryName(target));
                            en.ExtractToFile(target, true);
                        }
                    }
                }
                catch (InvalidDataException) { throw; }
                catch (Exception ex) when (ex is IOException == false) { throw new InvalidDataException("That download isn't a valid zip file."); }
                progress?.Report(0.8);

                var folders = tocFolders.ToList();
                // never overwrite a folder that belongs to another addon or is a git copy
                foreach (var f in folders)
                {
                    var dest = Path.Combine(addOnsDir, f);
                    if (!Directory.Exists(dest)) continue;
                    if (Directory.Exists(Path.Combine(dest, ".git"))) throw new InvalidOperationException($"{f} is a development copy (git) - not touching it.");
                    if (!e.Folders.Contains(f, StringComparer.OrdinalIgnoreCase))
                        throw new InvalidOperationException($"The update would replace {f}, which belongs to a different addon.");
                }

                // Interface check (a warning only)
                if (iface > 0)
                {
                    var nums = new List<int>();
                    foreach (var f in folders)
                        foreach (var toc in Directory.GetFiles(Path.Combine(tmp, f), "*.toc"))
                            try { nums.AddRange(AddonScanner.AllInterfaces(AddonScanner.ParseToc(toc).Get("Interface"))); } catch { }
                    if (nums.Count > 0 && !nums.Any(n => AddonScanner.Major(n) == AddonScanner.Major(iface)))
                        res.Warning = $"Heads up: this download is made for a different game version (Interface {string.Join("/", nums.Distinct().Take(3))}, your client is {iface}). It may not load.";
                }

                // back up what is there now
                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "__" + SafeName(e.Version ?? "unknown");
                var bdir = Path.Combine(BackupRoot, SafeName(e.Key), stamp);
                foreach (var f in folders)
                {
                    var dest = Path.Combine(addOnsDir, f);
                    if (Directory.Exists(dest)) Util.CopyDir(dest, Path.Combine(bdir, f));
                }
                progress?.Report(0.88);

                // swap: move the old one aside, move the new one in; undo everything if one step fails (e.g. files locked by WoW)
                try
                {
                    Directory.CreateDirectory(oldRoot);
                    foreach (var f in folders)
                    {
                        var dest = Path.Combine(addOnsDir, f);
                        if (Directory.Exists(dest)) Directory.Move(dest, Path.Combine(oldRoot, f));
                        swapped.Add(f);
                        Directory.Move(Path.Combine(tmp, f), dest);
                    }
                }
                catch (Exception ex)
                {
                    foreach (var f in swapped.AsEnumerable().Reverse())
                    {
                        var dest = Path.Combine(addOnsDir, f);
                        var old = Path.Combine(oldRoot, f);
                        try
                        {
                            if (Directory.Exists(old)) { if (Directory.Exists(dest)) Util.DeleteDir(dest); Directory.Move(old, dest); }
                            else if (Directory.Exists(dest) && !Directory.Exists(Path.Combine(bdir, f))) Util.DeleteDir(dest);
                        }
                        catch { }
                    }
                    try { Util.DeleteDir(bdir); } catch { }
                    throw new IOException("Couldn't replace the addon folders (is WoW running and holding files open? close it and try again). " + ex.Message);
                }
                res.Folders = folders;
                link.InstalledRemote = remoteVersion;
                SaveLinks();
                Prune(e.Key, 2);
                progress?.Report(1);
                Util.Log($"installed third-party {e.Key} {remoteVersion} ({string.Join(",", folders)})");
                return res;
            }
            finally
            {
                try { Util.DeleteDir(tmp); } catch { }
                try { Util.DeleteDir(oldRoot); } catch { }
            }
        }

        static void Prune(string key, int keep)
        {
            foreach (var old in Backups(key).Skip(keep)) try { Util.DeleteDir(old); } catch { }
        }

        // put the newest backup back (and drop it, so a second click goes one version further back)
        public static void Rollback(string addOnsDir, AddonEntry e, AddonLink link)
        {
            if (e.IsDev) throw new InvalidOperationException("This is a development copy (git) - not touching it.");
            var b = Backups(e.Key).FirstOrDefault();
            if (b == null) throw new InvalidOperationException("No backup to go back to.");
            var oldRoot = Path.Combine(addOnsDir, ".elanshub-old-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var moved = new List<string>();
            try
            {
                Directory.CreateDirectory(oldRoot);
                foreach (var dir in Directory.GetDirectories(b))
                {
                    var f = Path.GetFileName(dir);
                    var dest = Path.Combine(addOnsDir, f);
                    if (Directory.Exists(dest)) { Directory.Move(dest, Path.Combine(oldRoot, f)); moved.Add(f); }
                    Util.CopyDir(dir, dest);
                }
            }
            catch (Exception ex)
            {
                foreach (var f in moved)
                    try { var dest = Path.Combine(addOnsDir, f); if (Directory.Exists(dest)) Util.DeleteDir(dest); Directory.Move(Path.Combine(oldRoot, f), dest); } catch { }
                throw new IOException("Couldn't roll back (is WoW running?). " + ex.Message);
            }
            finally { try { Util.DeleteDir(oldRoot); } catch { } }
            Util.DeleteDir(b);
            link.InstalledRemote = null;
            SaveLinks();
        }
    }
}
