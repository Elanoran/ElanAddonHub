using System;
using System.Collections.Generic;
using System.Globalization;
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

namespace ElansAddonHub.Services.Atlas
{
    // ============================================================================================================================
    // The Cairn (self-test section "cairn"): an open commons that merges the pooled Atlas numbers of many Lodges into one daily
    // dataset (CC0) - drop rates per creature / object / fishing zone / gathering kind, vendor items, spawn points, gathering nodes,
    // flight times; ids and counts only, no names. Every Outpost may DOWNLOAD it (Settings > Privacy, on by default because it holds
    // no personal data); the Outpost never uploads anything to it. Its numbers are labelled "Cairn" in the Atlas, below the user's
    // own Lodge's. Wire format: server/PROTOCOL.md "The Cairn".
    //   <base>/v1/manifest.json  -> latest { day, seq, file, bytes, sha256, contentSha256, sig, kid }
    //   <base>/v1/datasets/<file> -> the .json.gz
    // The download is checked: the file's SHA-256, the JSON's SHA-256, and - once a public key is pinned below - the ES256 signature.
    // A file that fails any check is thrown away and the copy already on disk stays. Checked at most every 12 hours.
    // ============================================================================================================================
    public static class CairnAtlas
    {
        public const string DefaultUrl = "https://nasferatu.dk/cairn";
        // the Cairn's signing key (ES256, the x / y of its GET /v1/key, base64url). Empty = not pinned yet: the checksums are still checked
        public static string PinnedKeyX = "", PinnedKeyY = "";
        public const long MaxGzBytes = 64L * 1024 * 1024, MaxJsonBytes = 256L * 1024 * 1024, MaxManifestBytes = 1024 * 1024;
        public static readonly TimeSpan Every = TimeSpan.FromHours(12);

        public static bool AutoStart = true;                       // the self-test switches the automatic download off (it never contacts the real Cairn)
        public static string UrlOverride;                          // tests: a fake Cairn; null = ELANSHUB_CAIRN_URL or DefaultUrl
        public static string Url => UrlOverride ?? (Environment.GetEnvironmentVariable("ELANSHUB_CAIRN_URL") is string e && e.Length > 0 ? e : DefaultUrl);
        public static string CacheDirOverride;
        public static string CacheDir => CacheDirOverride ?? Path.Combine(Util.DataDir, "cairn");

        public static CairnSet Current { get; private set; }
        public static bool IsReady => Current != null;
        public static event Action Changed;                       // the data on hand changed (arrived, replaced, cleared)
        public static string LastProblem { get; private set; } = "";
        public static DateTime LastCheck { get; private set; } = DateTime.MinValue;

        // ---- fetching (replaceable for the self-test: a fake Cairn, no network)
        public sealed class Response { public int Status; public byte[] Body; public string ETag; }
        public static Func<string, string, Task<Response>> Fetch = DefaultFetch;   // (url, If-None-Match) -> answer; only GET, ever

        static async Task<Response> DefaultFetch(string url, string etag)
        {
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                if (!string.IsNullOrEmpty(etag)) req.Headers.TryAddWithoutValidation("If-None-Match", etag);
                using (var resp = await Net.Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token))
                {
                    var r = new Response { Status = (int)resp.StatusCode, ETag = resp.Headers.ETag?.ToString() };
                    if (resp.StatusCode != HttpStatusCode.OK) return r;
                    long cap = url.EndsWith(".json.gz", StringComparison.Ordinal) ? MaxGzBytes : MaxManifestBytes;
                    if (resp.Content.Headers.ContentLength > cap) throw new InvalidDataException("too large");
                    using (var s = await resp.Content.ReadAsStreamAsync()) r.Body = ReadCapped(s, cap);
                    return r;
                }
            }
        }

        static byte[] ReadCapped(Stream s, long cap)
        {
            using (var ms = new MemoryStream())
            {
                var buf = new byte[81920]; int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    if (ms.Length + n > cap) throw new InvalidDataException("too large");
                    ms.Write(buf, 0, n);
                }
                return ms.ToArray();
            }
        }

        // https only (http just to this PC, for a local test Cairn); nothing with a user, query or fragment
        public static bool ValidBase(string url, out string clean)
        {
            clean = null;
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim().TrimEnd('/'), UriKind.Absolute, out var u)) return false;
            if (!string.IsNullOrEmpty(u.UserInfo) || !string.IsNullOrEmpty(u.Query) || !string.IsNullOrEmpty(u.Fragment)) return false;
            if (u.Scheme != Uri.UriSchemeHttps && !(u.Scheme == Uri.UriSchemeHttp && u.IsLoopback)) return false;
            clean = u.GetLeftPart(UriPartial.Path).TrimEnd('/');
            return true;
        }

        static readonly Regex FileRx = new Regex(@"^cairn-[0-9]{4}-[0-9]{2}-[0-9]{2}-[0-9]{1,9}\.json\.gz$", RegexOptions.CultureInvariant);

        public sealed class Entry
        {
            public string Day, File, Sha256, ContentSha256, Sig, Kid; public long Seq, Bytes;
            public static Entry From(Dictionary<string, object> o)
            {
                if (o == null) return null;
                var e = new Entry { Day = o.Str("day"), File = o.Str("file"), Sha256 = o.Str("sha256"), ContentSha256 = o.Str("contentSha256"), Sig = o.Str("sig"), Kid = o.Str("kid"), Seq = o.Long("seq"), Bytes = o.Long("bytes") };
                if (e.File == null || !FileRx.IsMatch(e.File) || !Net.IsSha256(e.Sha256) || !Net.IsSha256(e.ContentSha256) || e.Seq < 1) return null;
                return e;
            }
        }

        static string Hex(byte[] b) { var sb = new StringBuilder(b.Length * 2); foreach (var x in b) sb.Append(x.ToString("x2", CultureInfo.InvariantCulture)); return sb.ToString(); }
        public static string Sha256Hex(byte[] b) { using (var h = SHA256.Create()) return Hex(h.ComputeHash(b)); }
        static byte[] B64Url(string s)
        {
            s = (s ?? "").Replace('-', '+').Replace('_', '/');
            return Convert.FromBase64String(s + new string('=', (4 - s.Length % 4) % 4));
        }

        public static string SignedMessage(string day, long seq, string contentSha) => "elans-cairn/1\n" + day + "\n" + seq.ToString(CultureInfo.InvariantCulture) + "\n" + contentSha;

        // the ES256 check against the pinned key (x / y base64url); false for anything malformed
        public static bool SignatureOk(Entry e, string keyX, string keyY)
        {
            try
            {
                if (string.IsNullOrEmpty(e?.Sig)) return false;
                var p = new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = B64Url(keyX), Y = B64Url(keyY) } };
                using (var ec = ECDsa.Create(p))
                    return ec.VerifyData(Encoding.UTF8.GetBytes(SignedMessage(e.Day, e.Seq, e.ContentSha256)), Convert.FromBase64String(e.Sig), HashAlgorithmName.SHA256);
            }
            catch { return false; }
        }

        public static byte[] Gunzip(byte[] gz, long cap = MaxJsonBytes)
        {
            using (var z = new GZipStream(new MemoryStream(gz), CompressionMode.Decompress)) return ReadCapped(z, cap);
        }

        // ---- state on disk: <cache>/cairn.json.gz + <cache>/meta.json { day, seq, file, sha256, contentSha256, etag, checked }
        static string GzPath => Path.Combine(CacheDir, "cairn.json.gz");
        static string MetaPath => Path.Combine(CacheDir, "meta.json");
        static readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);

        static Dictionary<string, object> ReadMeta()
        {
            try { return File.Exists(MetaPath) ? Json.Obj(File.ReadAllText(MetaPath)) : null; } catch { return null; }
        }

        static void WriteAtomic(string path, byte[] data)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, data);
            if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
        }

        // what is on disk from an earlier run (checked again: a damaged file is dropped)
        public static void LoadCached()
        {
            try
            {
                var meta = ReadMeta();
                if (meta == null || !File.Exists(GzPath)) return;
                var json = Gunzip(File.ReadAllBytes(GzPath));
                if (Sha256Hex(json) != meta.Str("contentSha256")) { Util.Log("cairn: the saved copy does not match its checksum - dropped"); Delete(); return; }
                var set = CairnSet.Parse(Encoding.UTF8.GetString(json));
                if (set == null) return;
                Current = set;
                LastCheck = long.TryParse(meta.Str("checked"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ck) ? new DateTime(ck, DateTimeKind.Utc) : DateTime.MinValue;
                Changed?.Invoke();
            }
            catch (Exception ex) { Util.Log("cairn: saved copy unreadable: " + ex.Message); }
        }

        static void Delete()
        {
            try { if (File.Exists(GzPath)) File.Delete(GzPath); if (File.Exists(MetaPath)) File.Delete(MetaPath); } catch { }
        }

        // switched off in Settings > Privacy: nothing is fetched any more and the copy on disk is deleted
        public static void Clear()
        {
            gate.Wait();
            try { Delete(); var had = Current != null; Current = null; LastCheck = DateTime.MinValue; LastProblem = ""; if (had) Changed?.Invoke(); }
            finally { gate.Release(); }
        }

        // Asks the Cairn for its newest file (at most every 12 hours unless forced). Returns what happened, in a word:
        // "off", "fresh", "unchanged", "updated", or "failed: <why>" (the copy on hand stays).
        public static async Task<string> Refresh(Func<bool> enabled, bool force = false)
        {
            if (enabled != null && !enabled()) return "off";
            await gate.WaitAsync();
            try
            {
                if (!force && DateTime.UtcNow - LastCheck < Every) return "fresh";
                if (!ValidBase(Url, out var baseUrl)) return Fail("the Cairn address is not https");
                var meta = ReadMeta();
                var m = await Fetch(baseUrl + "/v1/manifest.json", Current != null ? meta?.Str("etag") : null);
                LastCheck = DateTime.UtcNow;
                if (m.Status == 304) { SaveChecked(meta); return "unchanged"; }
                if (m.Status != 200 || m.Body == null) return Fail("the Cairn answered " + m.Status);
                var mo = Json.Obj(Encoding.UTF8.GetString(m.Body));
                if (mo?.Str("format") != "elans-cairn-manifest") return Fail("not a Cairn manifest");
                var e = Entry.From(mo.Child("latest"));
                if (e == null) return Fail("the manifest names no valid file");
                if (Current != null && meta?.Str("contentSha256") == e.ContentSha256) { meta["etag"] = m.ETag; SaveChecked(meta); return "unchanged"; }
                if (!string.IsNullOrEmpty(PinnedKeyX) && !SignatureOk(e, PinnedKeyX, PinnedKeyY)) return Fail("the signature does not match the Cairn's key");
                var g = await Fetch(baseUrl + "/v1/datasets/" + e.File, null);
                if (g.Status != 200 || g.Body == null) return Fail("the file answered " + g.Status);
                if (Sha256Hex(g.Body) != e.Sha256) return Fail("the file does not match its checksum");
                var json = Gunzip(g.Body);
                if (Sha256Hex(json) != e.ContentSha256) return Fail("the data does not match its checksum");
                var set = CairnSet.Parse(Encoding.UTF8.GetString(json));
                if (set == null) return Fail("not a Cairn dataset");
                WriteAtomic(GzPath, g.Body);
                var nm = new Dictionary<string, object>
                {
                    ["day"] = e.Day, ["seq"] = (double)e.Seq, ["file"] = e.File, ["sha256"] = e.Sha256, ["contentSha256"] = e.ContentSha256, ["etag"] = m.ETag,
                    ["checked"] = LastCheck.Ticks.ToString(CultureInfo.InvariantCulture),
                };
                WriteAtomic(MetaPath, Encoding.UTF8.GetBytes(Json.Write(nm)));
                Current = set;
                LastProblem = "";
                Changed?.Invoke();
                return "updated";
            }
            catch (Exception ex) { return Fail(ex.GetType().Name); }
            finally { gate.Release(); }
        }

        static void SaveChecked(Dictionary<string, object> meta)
        {
            if (meta == null) return;
            meta["checked"] = LastCheck.Ticks.ToString(CultureInfo.InvariantCulture);
            try { WriteAtomic(MetaPath, Encoding.UTF8.GetBytes(Json.Write(meta))); } catch { }
        }

        static string Fail(string why)
        {
            LastProblem = why;
            Util.Log("cairn: " + why);
            return "failed: " + why;
        }

        // the background loop: the copy on disk at once, the Cairn 30 s after start and then every 12 hours (while switched on)
        static int started;
        public static void Start(Func<bool> enabled)
        {
            if (!AutoStart || LinkOpen.SelfTestRun || Interlocked.Exchange(ref started, 1) == 1) return;
            Task.Run(async () =>
            {
                if (enabled()) LoadCached();
                await Task.Delay(TimeSpan.FromSeconds(30));
                while (true)
                {
                    try { await Refresh(enabled); } catch { }
                    await Task.Delay(TimeSpan.FromHours(1));
                }
            });
        }

        public static void SetForTest(CairnSet s) { Current = s; Changed?.Invoke(); }
        public static void ResetForTest() { Current = null; LastCheck = DateTime.MinValue; LastProblem = ""; }

        // one line for Settings > Privacy
        public static string StatusText(bool on)
        {
            if (!on) return "Off: nothing is downloaded.";
            var c = Current;
            if (c == null) return LastProblem.Length > 0 ? "Not downloaded yet (" + LastProblem + ")." : "Not downloaded yet.";
            return "Data of " + c.Day + " from " + c.Lodges.ToString("N0", CultureInfo.InvariantCulture) + " lodge" + (c.Lodges == 1 ? "" : "s") + ".";
        }
    }

    // The open dataset, indexed for the Atlas pages. Numbers only.
    public sealed class CairnSet
    {
        public string Day = ""; public long Seq; public int Lodges, MinLodges; public List<int> Builds = new List<int>();

        public sealed class Source { public string Kind; public int Id; public long N; public int M; public int Build; public List<Drop> Items = new List<Drop>(); }
        public sealed class Drop { public int Item; public long Times, Count, Kills; public int M; public Source From; public double Chance => Kills > 0 ? 100.0 * Times / Kills : 0; }
        public sealed class VendorItem { public int Npc, Item, Stack, Limit, M; public long Price; public bool Extended; }
        public struct Spot { public int Map; public double X, Y; public long N; public int M; }

        public static readonly string[] Kinds = { "kill", "object", "fishing", "pickpocket", "skinning", "mining", "herbalism", "salvage", "contained" };
        readonly Dictionary<string, Source> sources = new Dictionary<string, Source>(StringComparer.Ordinal);    // "kill:448"
        readonly Dictionary<int, List<Drop>> byItem = new Dictionary<int, List<Drop>>();
        readonly Dictionary<int, List<VendorItem>> vendorsByItem = new Dictionary<int, List<VendorItem>>();
        readonly Dictionary<int, List<VendorItem>> vendorsByNpc = new Dictionary<int, List<VendorItem>>();
        readonly Dictionary<int, List<Spot>> spawns = new Dictionary<int, List<Spot>>();
        readonly Dictionary<int, List<Spot>> nodes = new Dictionary<int, List<Spot>>();
        readonly Dictionary<string, (double T, long N, int M)> flights = new Dictionary<string, (double, long, int)>(StringComparer.Ordinal);

        static readonly IReadOnlyList<Drop> NoDrops = new Drop[0];
        static readonly IReadOnlyList<VendorItem> NoVendors = new VendorItem[0];
        static readonly IReadOnlyList<Spot> NoSpots = new Spot[0];

        public int SourceCount => sources.Count;
        public int VendorNpcCount => vendorsByNpc.Count;
        public int FlightCount => flights.Count;
        public Source Loot(string kind, int id) => sources.TryGetValue(kind + ":" + id.ToString(CultureInfo.InvariantCulture), out var s) ? s : null;
        public IReadOnlyList<Drop> DropsOf(int item) => byItem.TryGetValue(item, out var l) ? (IReadOnlyList<Drop>)l : NoDrops;
        public IReadOnlyList<VendorItem> VendorsOf(int item) => vendorsByItem.TryGetValue(item, out var l) ? (IReadOnlyList<VendorItem>)l : NoVendors;
        public IReadOnlyList<VendorItem> Stock(int npc) => vendorsByNpc.TryGetValue(npc, out var l) ? (IReadOnlyList<VendorItem>)l : NoVendors;
        public IReadOnlyList<Spot> Spawns(int npc) => spawns.TryGetValue(npc, out var l) ? (IReadOnlyList<Spot>)l : NoSpots;
        public IReadOnlyList<Spot> Nodes(int obj) => nodes.TryGetValue(obj, out var l) ? (IReadOnlyList<Spot>)l : NoSpots;
        public bool Flight(string leg, out double seconds, out int lodges) { seconds = 0; lodges = 0; if (!flights.TryGetValue(leg ?? "", out var f)) return false; seconds = f.T; lodges = f.M; return true; }

        static bool Id(string k, int max, out int id) => int.TryParse(k, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id >= 1 && id <= max;
        static double D(object o) => o is double d ? d : 0;
        static long L(object o) => o is double d && d >= 0 && d < 9e15 ? (long)d : 0;
        static int I(object o) => o is double d && d >= -1 && d < 2e9 ? (int)d : 0;
        static double Frac(object o) { var d = D(o); return d < 0 ? 0 : d > 1 ? 1 : d; }

        // null = not a Cairn dataset (format / version). Bad records are skipped, not fatal.
        public static CairnSet Parse(string json)
        {
            var o = Json.Obj(json);
            if (o == null || o.Str("format") != "elans-cairn" || o.Int("v") != 1) return null;
            var s = new CairnSet { Day = o.Str("day") ?? "", Seq = o.Long("seq"), Lodges = o.Int("lodges"), MinLodges = o.Int("minLodges") };
            foreach (var b in o.List("builds")) if (b is double bd) s.Builds.Add((int)bd);
            var drops = o.Child("drops");
            foreach (var kind in Kinds)
            {
                var sec = drops.Child(kind);
                if (sec == null) continue;
                int max = kind == "fishing" ? 99999 : kind == "contained" ? 999999 : 9999999;
                foreach (var kv in sec)
                {
                    if (!Id(kv.Key, max, out var id) || !(kv.Value is Dictionary<string, object> r)) continue;
                    var src = new Source { Kind = kind, Id = id, N = r.Long("n"), M = r.Int("m"), Build = r.Int("b") };
                    if (src.N <= 0) continue;
                    var items = r.Child("i");
                    if (items != null)
                        foreach (var iv in items)
                        {
                            if (!Id(iv.Key, 999999, out var item) || !(iv.Value is List<object> a) || a.Count < 3) continue;
                            var d = new Drop { Item = item, Times = L(a[0]), Count = L(a[1]), M = I(a[2]), Kills = a.Count > 3 ? L(a[3]) : src.N, From = src };
                            if (d.Times <= 0 || d.Kills <= 0) continue;
                            src.Items.Add(d);
                            if (!s.byItem.TryGetValue(item, out var l)) s.byItem[item] = l = new List<Drop>();
                            l.Add(d);
                        }
                    s.sources[kind + ":" + kv.Key] = src;
                }
            }
            var vendors = o.Child("vendors");
            if (vendors != null)
                foreach (var kv in vendors)
                {
                    if (!Id(kv.Key, 9999999, out var npc) || !(kv.Value is Dictionary<string, object> r) || r.Child("i") == null) continue;
                    foreach (var iv in r.Child("i"))
                    {
                        if (!Id(iv.Key, 999999, out var item) || !(iv.Value is List<object> a) || a.Count < 5) continue;
                        var v = new VendorItem { Npc = npc, Item = item, Price = L(a[0]), Stack = Math.Max(1, I(a[1])), Limit = I(a[2]), Extended = I(a[3]) == 1, M = I(a[4]) };
                        if (!s.vendorsByItem.TryGetValue(item, out var li)) s.vendorsByItem[item] = li = new List<VendorItem>();
                        li.Add(v);
                        if (!s.vendorsByNpc.TryGetValue(npc, out var ln)) s.vendorsByNpc[npc] = ln = new List<VendorItem>();
                        ln.Add(v);
                    }
                }
            ReadSpots(o.Child("spawns"), s.spawns);
            ReadSpots(o.Child("nodes"), s.nodes);
            var fl = o.Child("flights");
            if (fl != null)
                foreach (var kv in fl)
                    if (kv.Value is List<object> a && a.Count >= 3 && D(a[0]) > 0) s.flights[kv.Key] = (D(a[0]), L(a[1]), I(a[2]));
            return s;
        }

        static void ReadSpots(Dictionary<string, object> sec, Dictionary<int, List<Spot>> into)
        {
            if (sec == null) return;
            foreach (var kv in sec)
            {
                if (!Id(kv.Key, 9999999, out var id) || !(kv.Value is List<object> pts)) continue;
                var l = new List<Spot>();
                foreach (var p in pts)
                    if (p is List<object> a && a.Count >= 5 && I(a[0]) > 0) l.Add(new Spot { Map = I(a[0]), X = Frac(a[1]), Y = Frac(a[2]), N = L(a[3]), M = I(a[4]) });
                if (l.Count > 0) into[id] = l;
            }
        }
    }
}
