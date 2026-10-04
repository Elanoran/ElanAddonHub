using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ElansAddonHub.Services
{
    // One WoWInterface file, reduced to what matching needs (the cache on disk holds only these).
    [DataContract]
    public class WowiFile
    {
        [DataMember(Name = "i")] public string Id { get; set; }
        [DataMember(Name = "n")] public string Name { get; set; }
        [DataMember(Name = "a")] public string Author { get; set; }
        [DataMember(Name = "v")] public string Version { get; set; }
        [DataMember(Name = "d")] public long Date { get; set; }          // ms since 1970
        [DataMember(Name = "t")] public long Downloads { get; set; }
        [DataMember(Name = "c")] public List<string> Compat { get; set; } // game versions, "1.15.2"
        [DataMember(Name = "f")] public List<string> Dirs { get; set; }   // addon folders in the zip
    }

    [DataContract]
    class WowiCache { [DataMember(Name = "fetched")] public long Fetched { get; set; } [DataMember(Name = "files")] public List<WowiFile> Files { get; set; } }

    [DataContract]
    class RejectFile { [DataMember(Name = "rejected")] public List<string> Rejected { get; set; } = new List<string>(); }   // "key|wowiId"

    public class Suggestion
    {
        public WowiFile File;
        public double Score;
        public bool Exact;           // the file's folder set equals the installed one
        public bool FlavorFits;      // marked for this client's game version
        public string Hint;          // "exact folder match" / "main folder match" / "similar name"
        public string Text => $"{File.Name} on WoWInterface (by {(string.IsNullOrEmpty(File.Author) ? "unknown" : File.Author)}, {(string.IsNullOrEmpty(File.Version) ? "?" : File.Version)})";
    }

    // Finds a likely WoWInterface file for addons that have no update source. Only ever SUGGESTS: nothing is
    // linked or installed until the user confirms. Keyless: uses the public filelist (cached 24 h in compact form).
    public static class AddonSuggest
    {
        static readonly string CachePath = Path.Combine(Util.DataDir, "cache", "wowi-index.json");
        static readonly string RejectPath = Path.Combine(Util.DataDir, "suggest-rejected.json");
        static readonly TimeSpan Ttl = TimeSpan.FromHours(24);
        static List<WowiFile> files;
        static Index index;
        static readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        static HashSet<string> rejected;

        public class Index
        {
            public Dictionary<string, List<WowiFile>> ByDir = new Dictionary<string, List<WowiFile>>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, List<WowiFile>> ByName = new Dictionary<string, List<WowiFile>>();
        }

        // ------------------------------------------------------------ filelist -> compact list
        public static List<WowiFile> ParseFilelist(string json)
        {
            var l = new List<WowiFile>();
            foreach (var o in (Json.Parse(json) as List<object> ?? new List<object>()).OfType<Dictionary<string, object>>())
            {
                var dirs = o.List("UIDir").OfType<string>().Where(s => s.Length > 0).ToList();
                var id = o.Str("UID");
                if (dirs.Count == 0 || id == null) continue;
                long.TryParse(o.Str("UIDownloadTotal") ?? "0", out var dl);
                l.Add(new WowiFile
                {
                    Id = id, Name = o.Str("UIName") ?? "", Author = o.Str("UIAuthorName") ?? "", Version = o.Str("UIVersion") ?? "",
                    Date = o.Long("UIDate"), Downloads = dl,
                    Compat = o.List("UICompatibility").OfType<Dictionary<string, object>>().Select(c => c.Str("version")).Where(s => s != null).ToList(),
                    Dirs = dirs,
                });
            }
            return l;
        }

        public static Index BuildIndex(IEnumerable<WowiFile> list)
        {
            var ix = new Index();
            foreach (var f in list)
            {
                foreach (var d in f.Dirs)
                {
                    if (!ix.ByDir.TryGetValue(d, out var l)) ix.ByDir[d] = l = new List<WowiFile>();
                    l.Add(f);
                }
                var n = Norm(f.Name);
                if (n.Length > 0) { if (!ix.ByName.TryGetValue(n, out var l)) ix.ByName[n] = l = new List<WowiFile>(); l.Add(f); }
            }
            return ix;
        }

        // The index, loaded once: memory, then disk (24 h), then one download. Null when offline and nothing cached.
        public static async Task<Index> LoadIndex()
        {
            await gate.WaitAsync();
            try
            {
                if (index != null) return index;
                List<WowiFile> stale = null;
                try
                {
                    if (File.Exists(CachePath))
                    {
                        var c = Util.FromJson<WowiCache>(File.ReadAllText(CachePath));
                        if (c?.Files != null)
                        {
                            stale = c.Files;
                            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(CachePath) < Ttl) { files = stale; return index = BuildIndex(files); }
                        }
                    }
                }
                catch (Exception e) { Util.Log("suggest cache read failed: " + e.Message); }
                try
                {
                    string text;
                    using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90)))
                    using (var resp = await Net.Http.GetAsync("https://api.mmoui.com/v3/game/WOW/filelist.json", cts.Token))
                    {
                        if (!resp.IsSuccessStatusCode) throw new InvalidDataException("HTTP " + (int)resp.StatusCode);
                        text = await resp.Content.ReadAsStringAsync();
                    }
                    files = await Task.Run(() => ParseFilelist(text));
                    text = null;
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(CachePath));
                        File.WriteAllText(CachePath, Util.ToJson(new WowiCache { Fetched = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Files = files }));
                    }
                    catch { }
                }
                catch (Exception e) { Util.Log("filelist download failed: " + e.Message); files = stale; }
                return files == null ? null : (index = BuildIndex(files));
            }
            finally { gate.Release(); }
        }

        // ------------------------------------------------------------ rejections
        static HashSet<string> Rejected()
        {
            if (rejected != null) return rejected;
            rejected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try { if (File.Exists(RejectPath)) foreach (var s in Util.FromJson<RejectFile>(File.ReadAllText(RejectPath)).Rejected ?? new List<string>()) rejected.Add(s); } catch { }
            return rejected;
        }

        public static void Reject(string key, string wowiId)
        {
            Rejected().Add(key + "|" + wowiId);
            try { File.WriteAllText(RejectPath, Util.ToJson(new RejectFile { Rejected = Rejected().ToList() })); } catch (Exception e) { Util.Log("reject save failed: " + e.Message); }
        }

        public static bool IsRejected(string key, string wowiId) => Rejected().Contains(key + "|" + wowiId);

        // ------------------------------------------------------------ matching
        public static string Norm(string s)
        {
            s = AddonScanner.StripColors(s) ?? "";
            return Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9]+", "");
        }

        static HashSet<string> Tokens(string s)
        {
            s = AddonScanner.StripColors(s) ?? "";
            s = Regex.Replace(s, @"([a-z])([A-Z])", "$1 $2");       // BigWigs -> Big Wigs
            return new HashSet<string>(Regex.Split(s.ToLowerInvariant(), @"[^a-z0-9]+").Where(t => t.Length > 1));
        }

        // true: marked for the client's game family; false: marked for something else; null: unknown
        public static bool? FlavorFit(WowiFile f, int iface)
        {
            if (f.Compat == null || f.Compat.Count == 0 || iface <= 0) return null;
            int major = AddonScanner.Major(iface);
            foreach (var v in f.Compat)
            {
                var m = Regex.Match(v ?? "", @"^(\d+)");
                if (!m.Success) continue;
                int n = int.Parse(m.Groups[1].Value);
                if (major == 99 ? n >= 6 : n == major) return true;
            }
            return false;
        }

        public static Suggestion Suggest(AddonEntry e, Index ix, int iface, Func<string, bool> isRejected = null)
        {
            if (ix == null || e == null || e.IsDev) return null;
            var folders = new HashSet<string>(e.Folders, StringComparer.OrdinalIgnoreCase);
            var cands = new HashSet<WowiFile>();
            foreach (var f in e.Folders) if (ix.ByDir.TryGetValue(f, out var l)) foreach (var c in l) cands.Add(c);
            var nt = Norm(e.Title);
            if (nt.Length > 0 && ix.ByName.TryGetValue(nt, out var byName)) foreach (var c in byName) cands.Add(c);
            Suggestion best = null;
            var myTokens = Tokens(e.Title);
            foreach (var c in cands)
            {
                if (isRejected != null && isRejected(c.Id)) continue;
                var dirs = new HashSet<string>(c.Dirs, StringComparer.OrdinalIgnoreCase);
                bool exact = dirs.SetEquals(folders);
                bool main = dirs.Contains(e.Key);
                int overlap = folders.Count(dirs.Contains);
                double s = 0;
                if (exact) s += 100;
                else if (main) s += 60 + 10.0 * overlap / Math.Max(dirs.Count, folders.Count);
                else if (overlap > 0) s += 20;
                // title similarity
                var cn = Norm(c.Name);
                double sim = 0;
                if (cn.Length > 0 && cn == nt) sim = 1;
                else
                {
                    var ct = Tokens(c.Name);
                    if (ct.Count > 0 && myTokens.Count > 0) sim = (double)ct.Intersect(myTokens).Count() / ct.Union(myTokens).Count();
                    if (cn.Length > 3 && nt.Length > 3 && (cn.Contains(nt) || nt.Contains(cn))) sim = Math.Max(sim, 0.6);
                }
                s += 30 * sim;
                bool nameOnly = !exact && !main && overlap == 0;
                if (nameOnly && sim < 0.99) continue;            // name alone must be (nearly) identical
                // author
                var a1 = Norm(e.Author); var a2 = Norm(c.Author);
                if (a1.Length > 1 && a2.Length > 1 && (a1.Contains(a2) || a2.Contains(a1))) s += 10;
                // game version
                var fit = FlavorFit(c, iface);
                if (fit == true) s += 25; else if (fit == false) s -= 40;
                // tie-breakers: recent and popular
                if (c.Date > 0 && DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(c.Date) < TimeSpan.FromDays(730)) s += 3;
                s += Math.Min(3, Math.Log10(Math.Max(1, c.Downloads)) * 0.5);
                if (nameOnly) s += 0;                              // sim (30) + flavor is all it has
                var hint = exact ? "exact folder match" : main ? "main folder match" : "similar name";
                if (fit == false) hint += ", not marked for your game version";
                if (s < 45) continue;
                if (best == null || s > best.Score) best = new Suggestion { File = c, Score = s, Exact = exact, FlavorFits = fit != false, Hint = hint };
            }
            return best;
        }

        // ------------------------------------------------------------ self-test (synthetic filelist, no network)
        public static string SelfTest()
        {
            var sb = new StringBuilder();
            string F(string id, string name, string author, string ver, string compat, string dirs, long dl = 1000, long date = 1700000000000) =>
                "{\"UID\":\"" + id + "\",\"UIVersion\":\"" + ver + "\",\"UIDate\":" + date + ",\"UIName\":\"" + name + "\",\"UIAuthorName\":\"" + author + "\",\"UIDownloadTotal\":\"" + dl + "\",\"UICompatibility\":[" + compat + "],\"UIDir\":[" + dirs + "]}";
            const string classic = "{\"version\":\"1.15.2\",\"name\":\"Classic\"}";
            const string retail = "{\"version\":\"11.0.2\",\"name\":\"The War Within\"}";
            var json = "[" + string.Join(",", new[]
            {
                F("1", "CT Mod", "DahkCeles", "1.0", classic, "\"CT_Core\",\"CT_BarMod\",\"CT_Library\""),            // exact set
                F("2", "Some Suite", "Zed", "2.0", classic, "\"SuiteCore\",\"SuiteExtra\",\"SuiteMore\""),            // main folder only
                F("3", "Fancy Bars Lite", "Quux", "1.1", classic, "\"Other\""),                                       // name only
                F("4", "Wrongflavor", "Ann", "5.0", retail, "\"WrongFlavor\""),                                       // wrong flavour only
                F("5", "Twin", "Alice", "1.0", classic, "\"Twin\"", 500),                                             // author tie-break
                F("6", "Twin", "Bob", "1.0", classic, "\"Twin\"", 500),
                F("7", "Twin Retail", "Bob", "9.0", retail, "\"Twin\"", 90000),                                      // same folder, retail
            }) + "]";
            var ix = BuildIndex(ParseFilelist(json));
            AddonEntry E(string key, string title, string author, params string[] more) =>
                new AddonEntry { Key = key, Title = title, Author = author, Folders = new[] { key }.Concat(more).ToList() };
            bool ok = true;
            void Check(string what, bool cond, Suggestion s) { sb.AppendLine($"suggest {what}: {(cond ? "ok" : "FAIL")} ({(s == null ? "none" : s.File.Name + " " + s.Score.ToString("0") + " " + s.Hint)})"); ok &= cond; }

            var s1 = Suggest(E("CT_Core", "CT_Core", "DahkCeles", "CT_BarMod", "CT_Library"), ix, 16001);
            Check("exact folder set", s1?.File.Id == "1" && s1.Exact, s1);
            var s2 = Suggest(E("SuiteCore", "Suite Core", "Zed"), ix, 16001);
            Check("main folder only", s2?.File.Id == "2" && !s2.Exact && s2.Hint == "main folder match", s2);
            var s3 = Suggest(E("FancyBars", "|cff00ff00Fancy|r Bars Lite", "Quux"), ix, 16001);
            Check("name only (colour codes stripped)", s3?.File.Id == "3" && s3.Hint == "similar name", s3);
            var s3b = Suggest(E("FancyBars", "Fancy Bars", "Quux"), ix, 16001);
            Check("name only, too different -> no suggestion", s3b == null, s3b);
            var s4 = Suggest(E("WrongFlavor", "Wrongflavor", "Ann"), ix, 16001);
            Check("wrong flavour flagged", s4?.File.Id == "4" && !s4.FlavorFits && s4.Hint.Contains("not marked"), s4);
            var s5 = Suggest(E("Twin", "Twin", "Bob"), ix, 16001);
            Check("author tie-break + flavour beats downloads", s5?.File.Id == "6", s5);
            var s6 = Suggest(E("Twin", "Twin", "Alice"), ix, 16001);
            Check("author tie-break (Alice)", s6?.File.Id == "5", s6);
            var s7 = Suggest(E("Twin", "Twin", "Bob"), ix, 16001, id => id == "6");
            Check("rejected not re-offered (next best)", s7 != null && s7.File.Id != "6", s7);
            // rejection persistence
            Reject("TestKey", "42");
            bool persisted = IsRejected("TestKey", "42") && !IsRejected("TestKey", "43");
            sb.AppendLine("reject remembered: " + (persisted ? "ok" : "FAIL")); ok &= persisted;
            Check("unknown addon -> none", Suggest(E("Nothing", "Nothing At All", "x"), ix, 16001) == null, null);
            sb.AppendLine("suggest selftest " + (ok ? "PASSED" : "FAILED"));
            return sb.ToString();
        }
    }
}
