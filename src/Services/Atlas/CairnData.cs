using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ElansAddonHub.Services.Atlas
{
    // ============================================================================================================================
    // The Cairn (self-test section "cairn"): a commons that merges the pooled Atlas numbers of many Lodges into one daily dataset -
    // drop rates per creature / object / fishing zone / gathering kind, vendor items, spawn points, gathering nodes, flight times; ids
    // and counts only, no names. Give to get (2.87): the data is for those who share. The Outpost has NO Cairn address and never talks
    // to the Cairn: it gets the data from the user's own Lodge (feature "cairn2", server/PROTOCOL.md "The Cairn"), and only while the
    // user's Atlas sharing ("Share my Atlas observations with this Lodge") is on. Its numbers are labelled "Cairn" in the Atlas, below
    // the Lodge's own.
    //   <lodge>/cairn/manifest -> { format, v, latest { day, seq, file, bytes, sha256, contentSha256, sig, kid } } (ETag)
    //   <lodge>/cairn/dataset  -> the .json.gz
    // The download is checked: the file's SHA-256, the JSON's SHA-256, and - once a public key is pinned below - the ES256 signature
    // (made by the Cairn, so the Lodge in between can't change the numbers unseen). A file that fails a check is thrown away and the
    // copy already on disk stays. The copy is deleted when sharing is switched off, when the user has no Lodge, when the Lodge doesn't
    // hand it out (older Lodge, guest, not contributing) and when the Lodge says the user isn't entitled. Checked at most every 12 hours,
    // and when the Lodge announces a new dataset.
    // ============================================================================================================================
    public static class CairnAtlas
    {
        // the Cairn's signing key (ES256, the x / y of its GET /v1/key, base64url). Empty = not pinned yet: the checksums are still checked
        // pinned 2026-10-17 from the live Cairn (kid 17140e4dd7ef6d82); a new signing key on the Cairn needs a new Outpost release
        public static string PinnedKeyX = "sazP6BaFfEX2YPMeU0-1YOukbcgY5emt857_qyDqz5k", PinnedKeyY = "zU5yG5u2VqavW6jo-ZUFlGSQjB7DPMTtTMyzvDLw9UA";
        public const long MaxGzBytes = 64L * 1024 * 1024, MaxJsonBytes = 256L * 1024 * 1024, MaxManifestBytes = 1024 * 1024;
        public static readonly TimeSpan Every = TimeSpan.FromHours(12);
        public const string ManifestPath = "/cairn/manifest", DatasetPath = "/cairn/dataset";   // on the user's Lodge

        public static bool AutoStart = true;                       // the self-test switches the automatic download off
        public static string CacheDirOverride;
        public static string CacheDir => CacheDirOverride ?? Path.Combine(Util.DataDir, "cairn");

        public static CairnSet Current { get; private set; }
        public static bool IsReady => Current != null;
        public static event Action Changed;                       // the data on hand changed (arrived, replaced, cleared)
        public static string LastProblem { get; private set; } = "";
        public static string RefusedWhy { get; private set; } = "";      // the Lodge's last "not for you" reason: share, guest, lodge, disabled
        public static DateTime LastCheck { get; private set; } = DateTime.MinValue;

        // ---- fetching from the user's Lodge (LodgeSession.CairnFetchAsync; the self-test uses a fake Lodge)
        public sealed class Response { public int Status; public byte[] Body; public string ETag; public string Reason, Error; }
        public static Func<string, string, Task<Response>> Fetch;   // (path on the Lodge, If-None-Match) -> answer; only GET, ever. null = no Lodge

        // the Lodge's refusals that mean "this is not for you (any more)": the copy is deleted. Anything else (busy, not yet, offline) keeps it.
        public static bool NotEntitled(string reason) => reason == "share" || reason == "guest" || reason == "lodge" || reason == "disabled" || reason == "auth";

        public static byte[] ReadCapped(Stream s, long cap)
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

        static void DropLocked(string why)
        {
            var had = Current != null || File.Exists(GzPath);
            Delete(); Current = null; LastCheck = DateTime.MinValue; LastProblem = "";
            if (why != null) RefusedWhy = why;
            if (had) { Util.Log("cairn: the copy was deleted (" + (why ?? "off") + ")"); Changed?.Invoke(); }
        }

        // sharing switched off, no Lodge, or the Lodge doesn't hand it out to this user: the copy on disk goes. `why` = the Lodge's reason (or null)
        public static void Clear(string why = null)
        {
            gate.Wait();
            try { DropLocked(why); }
            finally { gate.Release(); }
        }

        // Asks the user's Lodge for the newest copy (at most every 12 hours unless forced). Returns what happened, in a word:
        // "off", "fresh", "unchanged", "updated", "refused: <reason>" (not for this user: the copy is deleted) or "failed: <why>" (the copy stays).
        public static async Task<string> Refresh(Func<bool> enabled, bool force = false)
        {
            if (enabled != null && !enabled()) return "off";
            var fetch = Fetch;
            if (fetch == null) return "off";
            await gate.WaitAsync();
            try
            {
                if (!force && DateTime.UtcNow - LastCheck < Every) return "fresh";
                var meta = ReadMeta();
                var m = await fetch(ManifestPath, Current != null ? meta?.Str("etag") : null);
                LastCheck = DateTime.UtcNow;
                if (m.Status == 304) { SaveChecked(meta); RefusedWhy = ""; return "unchanged"; }
                if (NotEntitled(m.Reason) || m.Status == 401) return Refused(m.Status == 401 ? "auth" : m.Reason);
                if (m.Status != 200 || m.Body == null) return Fail("the Lodge answered " + m.Status + (string.IsNullOrEmpty(m.Reason) ? "" : " (" + m.Reason + ")"));
                var mo = Json.Obj(Encoding.UTF8.GetString(m.Body));
                if (mo?.Str("format") != "elans-cairn-manifest") return Fail("not a Cairn manifest");
                var e = Entry.From(mo.Child("latest"));
                if (e == null) return Fail("the manifest names no valid file");
                RefusedWhy = "";
                if (Current != null && meta?.Str("contentSha256") == e.ContentSha256) { meta["etag"] = m.ETag; SaveChecked(meta); return "unchanged"; }
                if (!string.IsNullOrEmpty(PinnedKeyX) && !SignatureOk(e, PinnedKeyX, PinnedKeyY)) return Fail("the signature does not match the Cairn's key");
                var g = await fetch(DatasetPath, null);
                if (NotEntitled(g.Reason) || g.Status == 401) return Refused(g.Status == 401 ? "auth" : g.Reason);
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

        static string Refused(string reason)
        {
            DropLocked(reason ?? "");
            return "refused: " + reason;
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

        // the background loop: the copy on disk at once (while sharing is on and there is a Lodge), the Lodge 30 s after start and then
        // hourly (Refresh itself asks at most every 12 hours). `wanted` = sharing on and a Lodge set; `reachable` = and the Lodge hands it out now.
        static int started;
        public static void Start(Func<bool> wanted, Func<bool> reachable)
        {
            if (!AutoStart || LinkOpen.SelfTestRun || Interlocked.Exchange(ref started, 1) == 1) return;
            Task.Run(async () =>
            {
                if (wanted()) LoadCached(); else Clear();
                await Task.Delay(TimeSpan.FromSeconds(30));
                while (true)
                {
                    try { if (!wanted()) Clear(); else await Refresh(reachable); } catch { }
                    await Task.Delay(TimeSpan.FromHours(1));
                }
            });
        }

        public static void SetForTest(CairnSet s) { Current = s; Changed?.Invoke(); }
        public static void ResetForTest() { Current = null; LastCheck = DateTime.MinValue; LastProblem = ""; RefusedWhy = ""; }

        // one line for Settings > Privacy (where the Cairn would show): calm, and honest about who gets it
        public static string StatusText(bool hasLodge, bool online, bool lodgeHandsOut, bool guest, bool sharing)
        {
            if (!hasLodge) return "You are not in a Lodge, so there is no Cairn data: it comes only through a Lodge, to members who share.";
            if (!sharing) return "Sharing is off, so your Lodge doesn't give you the Cairn's numbers (and any copy on this PC was deleted).";
            if (guest) return "Guests don't get the Cairn's data - it is for Lodge members who share.";
            if (RefusedWhy == "lodge") return "Your Lodge isn't receiving the Cairn's data right now (a Lodge has to contribute to receive).";
            var c = Current;
            if (c != null) return "From your Lodge: the Cairn's data of " + c.Day + " from " + c.Lodges.ToString("N0", CultureInfo.InvariantCulture) + " lodge" + (c.Lodges == 1 ? "" : "s") + ".";
            if (online && !lodgeHandsOut) return "Your Lodge doesn't hand out the Cairn's data.";
            return LastProblem.Length > 0 ? "Not received yet (" + LastProblem + ")." : "Not received yet - your Lodge sends it when it has a copy.";
        }
    }

    // The Cairn's dataset, indexed for the Atlas pages. Numbers only.
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
        // 2.92: what the Data sources page counts (ids only)
        public IEnumerable<Source> AllSources => sources.Values;
        public IEnumerable<int> VendorNpcIds => vendorsByNpc.Keys;
        public IEnumerable<int> SpawnNpcIds => spawns.Keys;
        public IEnumerable<int> NodeObjectIds => nodes.Keys;
        public IEnumerable<string> FlightLegs => flights.Keys;
        public Source Loot(string kind, int id) => sources.TryGetValue(kind + ":" + id.ToString(CultureInfo.InvariantCulture), out var s) ? s : null;
        public IReadOnlyList<Drop> DropsOf(int item) => byItem.TryGetValue(item, out var l) ? (IReadOnlyList<Drop>)l : NoDrops;
        public IReadOnlyList<VendorItem> VendorsOf(int item) => vendorsByItem.TryGetValue(item, out var l) ? (IReadOnlyList<VendorItem>)l : NoVendors;
        public IReadOnlyList<VendorItem> Stock(int npc) => vendorsByNpc.TryGetValue(npc, out var l) ? (IReadOnlyList<VendorItem>)l : NoVendors;
        public IReadOnlyList<Spot> Spawns(int npc) => spawns.TryGetValue(npc, out var l) ? (IReadOnlyList<Spot>)l : NoSpots;
        public IReadOnlyList<Spot> Nodes(int obj) => nodes.TryGetValue(obj, out var l) ? (IReadOnlyList<Spot>)l : NoSpots;
        // for the maps (2.93): every creature / object with places, and the vendors (their place is in Spawns)
        public IEnumerable<KeyValuePair<int, List<Spot>>> AllSpawns => spawns;
        public IEnumerable<KeyValuePair<int, List<Spot>>> AllNodes => nodes;
        public IEnumerable<int> VendorNpcIds => vendorsByNpc.Keys;
        public IEnumerable<KeyValuePair<string, (double T, long N, int M)>> AllFlights => flights;
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
