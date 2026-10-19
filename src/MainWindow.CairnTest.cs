using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Elans.Shared;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;

namespace ElansAddonHub
{
    // --selftest "cairn": the Cairn's data in the Outpost - give to get (2.87). There is no Cairn address in the app: the data comes only from the
    // user's own Lodge (feature "cairn2"), only while Atlas sharing is on. A fake Lodge on 127.0.0.1 serves GET /cairn/manifest and /cairn/dataset
    // (same headers and refusals as the server; the real server is tested by server/Lodge.Tests) plus synthetic welcome / cairn.status frames.
    // Covers: no address / no Lodge = nothing; sharing on = atlas.sharing {on} is told and the data arrives (both checksums, ETag 304, the ES256
    // signature once pinned, a damaged file refused with the copy kept); "not yet" keeps the copy; sharing off = the copy is deleted, the Lodge is
    // told, nothing is fetched; the Lodge says "share" / "lodge" (lapsed) = the copy is deleted; guests and an older Lodge = nothing asked or fetched;
    // the copy on disk after a restart; the self-test keeps the automatic loop off; the dataset index; the item / creature pages (rows labelled
    // "Cairn"); Settings > Privacy (no switch of its own, the words tie it to the sharing switch, a calm status line); only GETs.
    public partial class MainWindow
    {
        // a stand-in for the Lodge's /cairn/* routes
        sealed class FakeLodgeCairn
        {
            readonly HttpListener listener = new HttpListener();
            public readonly string Url;
            public const string Code = "synthetic-cairn-code";
            public byte[] Manifest, Gz, CleanGz;
            public string ETag = "";
            public bool Corrupt;
            public int RefuseStatus; public string RefuseReason = "";
            public readonly List<string> Asked = new List<string>();     // "GET /cairn/manifest <If-None-Match>"
            public readonly List<string> Methods = new List<string>();

            public FakeLodgeCairn()
            {
                var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); l.Start(); int port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
                Url = "http://127.0.0.1:" + port;
                listener.Prefixes.Add(Url + "/");
                listener.Start();
                Task.Run(async () =>
                {
                    while (listener.IsListening)
                    {
                        HttpListenerContext c;
                        try { c = await listener.GetContextAsync(); } catch { return; }
                        try { Serve(c); } catch { try { c.Response.Abort(); } catch { } }
                    }
                });
            }

            public void Stop() { try { listener.Stop(); listener.Close(); } catch { } }
            public int Count(string path) { lock (Asked) return Asked.Count(a => a.StartsWith("GET " + path, StringComparison.Ordinal)); }

            static void Send(HttpListenerContext c, int status, byte[] body, string type)
            {
                c.Response.StatusCode = status; c.Response.ContentType = type; c.Response.ContentLength64 = body.Length;
                c.Response.OutputStream.Write(body, 0, body.Length); c.Response.Close();
            }

            static void Refuse(HttpListenerContext c, int status, string reason, string text) =>
                Send(c, status, Encoding.UTF8.GetBytes("{\"error\":" + Json.Write(text) + ",\"reason\":" + Json.Write(reason) + "}"), "application/json");

            void Serve(HttpListenerContext c)
            {
                var req = c.Request;
                var path = req.Url.AbsolutePath;
                lock (Asked) { Methods.Add(req.HttpMethod); Asked.Add(req.HttpMethod + " " + path + " " + (req.Headers["If-None-Match"] ?? "")); }
                if (req.Headers["X-Lodge-Code"] != Code) { Refuse(c, 401, "auth", "Wrong invite code"); return; }
                if (req.HttpMethod != "GET") { Refuse(c, 405, "method", "No"); return; }
                if (RefuseStatus != 0) { Refuse(c, RefuseStatus, RefuseReason, "refused for the test (" + RefuseReason + ")"); return; }
                if (path == "/cairn/manifest")
                {
                    if (Manifest == null) { Refuse(c, 503, "not-yet", "This Lodge has no copy of the Cairn's data yet - try again later"); return; }
                    if ((req.Headers["If-None-Match"] ?? "") == ETag) { c.Response.StatusCode = 304; c.Response.Close(); return; }
                    c.Response.Headers["ETag"] = ETag;
                    Send(c, 200, Manifest, "application/json");
                    return;
                }
                if (path == "/cairn/dataset")
                {
                    if (Gz == null) { Refuse(c, 503, "not-yet", "Not yet"); return; }
                    var d = (byte[])Gz.Clone();
                    if (Corrupt) d[d.Length / 2] ^= 0x55;
                    Send(c, 200, d, "application/gzip");
                    return;
                }
                Refuse(c, 404, "unknown", "No such route");
            }
        }

        static string CairnDatasetJson(long seq, long kills) =>
            "{\"format\":\"elans-cairn\",\"v\":1,\"license\":\"contributors\",\"day\":\"2026-10-17\",\"seq\":" + seq + ",\"minLodges\":2,\"lodges\":3,\"builds\":[57689],"
            + "\"drops\":{\"kill\":{\"1\":{\"n\":" + kills + ",\"m\":3,\"b\":57689,\"i\":{\"100\":[120,120,3]}},\"449\":{\"n\":500,\"m\":2,\"b\":57689,\"i\":{\"100\":[50,60,2],\"102\":[10,10,2,400]}}},"
            + "\"skinning\":{\"449\":{\"n\":80,\"m\":2,\"i\":{\"2318\":[70,140,2]}}}},"
            + "\"vendors\":{\"2\":{\"m\":2,\"i\":{\"100\":[12000,1,-1,0,2]}},\"77\":{\"m\":2,\"i\":{\"100\":[11500,1,-1,0,2]}}},"
            + "\"spawns\":{\"449\":[[11,0.3,0.4,9,2]]},\"nodes\":{\"1731\":[[11,0.25,0.5,12,2]]},\"flights\":{\"Horde|11>23\":[95.5,14,2]}}";

        // the manifest + file the fake Lodge hands on; key = the Cairn's signing key (null = no signature)
        static void CairnServe(FakeLodgeCairn f, long seq, long kills, ECDsa key)
        {
            var json = Encoding.UTF8.GetBytes(CairnDatasetJson(seq, kills));
            byte[] gz;
            using (var ms = new MemoryStream()) { using (var z = new GZipStream(ms, CompressionLevel.Optimal, true)) z.Write(json, 0, json.Length); gz = ms.ToArray(); }
            var csha = Es256.Sha256Hex(json);
            var file = "cairn-2026-10-17-" + seq + ".json.gz";
            var sig = key == null ? "" : Convert.ToBase64String(Es256.Sign(key, CairnFormats.Dataset("2026-10-17", seq, csha)));
            f.Manifest = Encoding.UTF8.GetBytes("{\"format\":\"elans-cairn-manifest\",\"v\":1,\"license\":\"contributors\",\"latest\":{\"day\":\"2026-10-17\",\"seq\":" + seq + ",\"file\":\"" + file + "\",\"bytes\":" + gz.Length
                + ",\"sha256\":\"" + Es256.Sha256Hex(gz) + "\",\"contentSha256\":\"" + csha + "\",\"sig\":\"" + sig + "\",\"kid\":\"test\"}}");
            f.Gz = gz; f.CleanGz = gz;
            f.ETag = "\"" + csha.Substring(0, 32) + "\"";
        }

        string CairnWelcome(string role, bool cairn2) =>
            FunWelcome(role, FunPeople(false), "", "\"reply\",\"react\",\"react2\",\"pin\",\"profile\",\"displayname\",\"lfg\",\"history2\",\"search\",\"atlas\",\"cairn\"" + (cairn2 ? ",\"cairn2\"" : ""), "2.27.0");

        static async Task<bool> CairnWait(Func<bool> cond, int ms = 4000)
        {
            for (int i = 0; i < ms / 25; i++) { if (cond()) return true; await Task.Delay(25); }
            return cond();
        }

        async Task<string> CairnTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            void Check(string what, bool cond, string detail = null) { SelfTestClock.Mark(what); notes.Add((cond ? "ok   " : "FAIL ") + what + (!cond && detail != null ? " [" + detail + "]" : "")); ok &= cond; }
            var work = Path.Combine(dir, "cairn");
            var savedFetch = CairnAtlas.Fetch; var savedCache = CairnAtlas.CacheDirOverride;
            var savedX = CairnAtlas.PinnedKeyX; var savedY = CairnAtlas.PinnedKeyY; var savedLodge = AtlasQuery.Context.Lodge;
            {
                bool pinned; using (var ec = Es256.FromPoint(B64.FromUrl(savedX), B64.FromUrl(savedY))) pinned = ec != null;
                Check("the live Cairn's signing key is pinned in the app (two 32-byte P-256 coordinates), so datasets are checked against it", pinned);
            }
            var savedShareOff = settings.AtlasShareOff; var savedUrl = settings.LodgeUrl; var savedRoot = settings.WowRoot;
            var sent = new List<Dictionary<string, object>>();
            FakeLodgeCairn fake = null;
            bool Sharing(bool on) { lock (sent) return sent.Any(f => f.Str("t") == "atlas.sharing" && f.Bool("on") == on); }
            bool AnySharing() { lock (sent) return sent.Any(f => f.Str("t") == "atlas.sharing"); }
            try
            {
                if (Directory.Exists(work)) Util.DeleteDir(work);
                Directory.CreateDirectory(Path.Combine(work, "wow"));
                fake = new FakeLodgeCairn();
                CairnAtlas.CacheDirOverride = work;
                CairnAtlas.PinnedKeyX = ""; CairnAtlas.PinnedKeyY = "";
                CairnAtlas.ResetForTest();
                Session.Disconnect();
                Session.SentForTest = o => { lock (sent) sent.Add(o); };
                Session.CairnClientForTest(new LodgeClient(fake.Url, FakeLodgeCairn.Code, "Elan"));
                CairnAtlas.Fetch = (p, e) => Session.CairnFetchAsync(p, e);       // the app's own wiring (MainWindow.Atlas.cs)
                settings.WowRoot = Path.Combine(work, "wow"); settings.AtlasShareOff = false;
                var key = Es256.NewKey();
                CairnServe(fake, 1, 200, key);

                // ---- no Cairn address anywhere in the app: no URL field, no Url / DefaultUrl / UrlOverride member
                var urlish = typeof(CairnAtlas).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    .Where(fi => fi.FieldType == typeof(string))
                    .Select(fi => { try { return fi.GetValue(null) as string ?? ""; } catch { return ""; } })
                    .Where(v => v.StartsWith("http", StringComparison.OrdinalIgnoreCase)).ToList();
                Check("no Cairn address in the app: no URL in CairnAtlas, no Url / DefaultUrl / UrlOverride member",
                    urlish.Count == 0 && typeof(CairnAtlas).GetMember("Url").Length == 0 && typeof(CairnAtlas).GetMember("DefaultUrl").Length == 0 && typeof(CairnAtlas).GetMember("UrlOverride").Length == 0, string.Join(", ", urlish));

                // ---- without a Lodge: nothing (said calmly in Privacy)
                settings.LodgeUrl = null;
                var r0 = await CairnAtlas.Refresh(CairnReachable, force: true);
                Check("no Lodge: nothing is fetched", r0 == "off" && fake.Asked.Count == 0, r0 + " / " + fake.Asked.Count);
                Check("no Lodge: the Privacy line says the data comes only through a Lodge", CairnAtlas.StatusText(false, false, false, false, true).Contains("not in a Lodge"));
                settings.LodgeUrl = "http://cairn.test/lodge";

                // ---- an older Lodge (no "cairn2"): nothing told, nothing fetched
                Session.FeedForTest(CairnWelcome("member", false));
                await Task.Delay(50);
                var rOld = await CairnAtlas.Refresh(CairnReachable, force: true);
                Check("an older Lodge (no cairn2): nothing is told, nothing fetched", !Session.SupportsCairnData && !AnySharing() && rOld == "off" && fake.Asked.Count == 0, rOld);
                Session.Disconnect();

                // ---- a member who shares, on a Lodge that hands it out: the Lodge is told, its "yes" brings the data
                lock (sent) sent.Clear();
                Session.CairnClientForTest(new LodgeClient(fake.Url, FakeLodgeCairn.Code, "Elan"));
                Session.FeedForTest(CairnWelcome("member", true));
                await Task.Delay(50);
                Check("sharing on: after the welcome the Lodge is told atlas.sharing {on: true}", Session.SupportsCairnData && Sharing(true) && !Sharing(false));
                Session.FeedForTest("{\"t\":\"cairn.status\",\"entitled\":true,\"sharing\":true,\"day\":\"2026-10-17\",\"seq\":1}");
                await CairnWait(() => CairnAtlas.Current?.Seq == 1);
                var c = CairnAtlas.Current;
                Check("entitled: the Lodge's manifest then its dataset, with the member's credential, both checksums checked, indexed",
                    c != null && c.Seq == 1 && fake.Count("/cairn/manifest") == 1 && fake.Count("/cairn/dataset") == 1, string.Join(" | ", fake.Asked));
                Check("index: drops by item (with the per-item kills when a Lodge was left out), skinning, vendors, spawns, nodes, flights, lodges",
                    c != null && c.Loot("kill", 449)?.N == 500 && c.DropsOf(100).Count == 2 && c.DropsOf(102).Single().Kills == 400 && c.Loot("skinning", 449)?.Items.Single().Item == 2318
                    && c.VendorsOf(100).Count == 2 && c.Stock(77).Single().Price == 11500 && c.Spawns(449).Single().Map == 11 && c.Nodes(1731).Single().N == 12
                    && c.Flight("Horde|11>23", out var secs, out var fm) && secs == 95.5 && fm == 2 && c.Lodges == 3);
                Check("the copy is kept on disk", File.Exists(Path.Combine(work, "cairn.json.gz")) && File.Exists(Path.Combine(work, "meta.json")));

                // ---- nothing new: the ETag, 304
                var r2 = await CairnAtlas.Refresh(CairnReachable, force: true);
                Check("nothing new: the manifest is asked with its ETag, 304 costs no download", r2 == "unchanged" && fake.Asked.Last().EndsWith(fake.ETag, StringComparison.Ordinal) && fake.Count("/cairn/dataset") == 1, r2);
                var r2b = await CairnAtlas.Refresh(CairnReachable);
                Check("checked at most every 12 hours (a second ask within that does nothing)", r2b == "fresh");

                // ---- a damaged file is refused, the copy on hand stays; "not yet" keeps it too
                CairnServe(fake, 2, 300, key);
                fake.Corrupt = true;
                var r3 = await CairnAtlas.Refresh(CairnReachable, force: true);
                Check("a damaged download is refused (checksum) and the data on hand stays", r3.StartsWith("failed", StringComparison.Ordinal) && r3.Contains("checksum") && CairnAtlas.Current?.Seq == 1, r3);
                fake.Corrupt = false;
                fake.RefuseStatus = 503; fake.RefuseReason = "not-yet";
                var r3b = await CairnAtlas.Refresh(CairnReachable, force: true);
                Check("the Lodge has no copy yet (503 not-yet): the copy on hand stays", r3b.StartsWith("failed", StringComparison.Ordinal) && CairnAtlas.Current?.Seq == 1, r3b);
                fake.RefuseStatus = 0;

                // ---- with a pinned key: the Cairn's signature is checked (the Lodge in between can't change the numbers unseen)
                var pub = key.ExportParameters(false);
                CairnAtlas.PinnedKeyX = B64.Url(pub.Q.X); CairnAtlas.PinnedKeyY = B64.Url(pub.Q.Y);
                CairnServe(fake, 3, 400, Es256.NewKey());
                int dl = fake.Count("/cairn/dataset");
                var r4 = await CairnAtlas.Refresh(CairnReachable, force: true);
                Check("pinned key: a file signed by another key is refused before it is downloaded", r4.Contains("signature") && CairnAtlas.Current?.Seq == 1 && fake.Count("/cairn/dataset") == dl, r4);
                CairnServe(fake, 3, 400, null);
                var r4b = await CairnAtlas.Refresh(CairnReachable, force: true);
                Check("pinned key: an unsigned file is refused too", r4b.Contains("signature") && CairnAtlas.Current?.Seq == 1, r4b);
                CairnServe(fake, 4, 500, key);
                var r5 = await CairnAtlas.Refresh(CairnReachable, force: true);
                Check("pinned key: the Cairn's own signature is taken", r5 == "updated" && CairnAtlas.Current?.Seq == 4 && CairnAtlas.Current.Loot("kill", 1).N == 500, r5);
                // 3.1.1 (L10): an older dataset is never taken, even with a valid signature; the same day + seq is no change
                CairnServe(fake, 3, 999, key);
                dl = fake.Count("/cairn/dataset");
                var rOlder = await CairnAtlas.Refresh(CairnReachable, force: true);
                Check("rollback: an older, validly signed dataset is refused before it is downloaded; the newer one stays",
                    rOlder.StartsWith("failed", StringComparison.Ordinal) && rOlder.Contains("older") && CairnAtlas.Current?.Seq == 4 && CairnAtlas.Current.Loot("kill", 1).N == 500 && fake.Count("/cairn/dataset") == dl, rOlder);
                CairnServe(fake, 4, 777, key);
                var rSame = await CairnAtlas.Refresh(CairnReachable, force: true);
                Check("rollback: the same day and seq with other content is no change", rSame == "unchanged" && CairnAtlas.Current?.Seq == 4 && CairnAtlas.Current.Loot("kill", 1).N == 500 && fake.Count("/cairn/dataset") == dl, rSame);
                Check("rollback: newer means a later day, then a higher seq", CairnAtlas.CompareVersion("2026-10-18", 1, "2026-10-17", 9) > 0 && CairnAtlas.CompareVersion("2026-10-17", 9, "2026-10-18", 1) < 0
                    && CairnAtlas.CompareVersion("2026-10-17", 5, "2026-10-17", 4) > 0 && CairnAtlas.CompareVersion("2026-10-17", 4, "2026-10-17", 4) == 0);

                CairnAtlas.PinnedKeyX = ""; CairnAtlas.PinnedKeyY = "";

                // ---- the Lodge announces a newer dataset: fetched at once
                CairnServe(fake, 5, 600, key);
                Session.FeedForTest("{\"t\":\"cairn.status\",\"entitled\":true,\"sharing\":true,\"day\":\"2026-10-17\",\"seq\":5}");
                Check("the Lodge announces a newer dataset (cairn.status seq 5): it is fetched at once", await CairnWait(() => CairnAtlas.Current?.Seq == 5));

                // ---- after a restart: the copy on disk (checked again); a damaged copy is dropped
                CairnAtlas.ResetForTest();
                CairnAtlas.LoadCached();
                Check("restart: the copy on disk is read back", CairnAtlas.Current?.Seq == 5);
                var gzPath = Path.Combine(work, "cairn.json.gz");
                CairnServe(fake, 6, 700, key);
                File.WriteAllBytes(gzPath, fake.CleanGz);      // another (valid gzip) file under the saved checksums
                CairnAtlas.ResetForTest();
                CairnAtlas.LoadCached();
                Check("restart: a copy that does not match its saved checksum is dropped", CairnAtlas.Current == null && !File.Exists(gzPath));

                // ---- the self-test never starts the automatic loop
                int before = fake.Asked.Count;
                CairnAtlas.Start(() => true, () => true);
                await Task.Delay(150);
                Check("the self-test keeps the automatic download off", LinkOpen.SelfTestRun && fake.Asked.Count == before);

                // ---- the Atlas pages: the Lodge's own rows first, the Cairn's below them, labelled "Cairn"
                CairnServe(fake, 7, 200, key);
                await CairnAtlas.Refresh(CairnReachable, force: true);
                var lodge = AtlasTestLodge();          // npc 1 drops item 100 (41 of 340), vendor 2 sells it
                AtlasQuery.Context.Lodge = () => lodge;
                var m = AtlasQuery.Build("item:100");
                var srcRows = m.Sections.FirstOrDefault(s => s.Key == "sources");
                var all = srcRows == null ? new List<AtlasRow>() : srcRows.Rows.Concat(srcRows.Hidden).ToList();
                var lodgeRow = all.FirstOrDefault(r => r.Key == "npc:1");
                var cairnRow = all.FirstOrDefault(r => r.Key == "npc:449");
                Check("item page: the Lodge's own drop row stays on top, with the Cairn's figure for the same creature as its second line",
                    lodgeRow != null && lodgeRow.Label == AtlasLabel.Forever && lodgeRow.Extra.Contains("Cairn: 120 of 200 kills") && lodgeRow.Extra.Contains("3 lodges"), lodgeRow?.Extra);
                Check("item page: a creature only the Cairn knows is a row labelled \"Cairn\" (tier 1) below the Lodge's rows",
                    cairnRow != null && cairnRow.Label == "Cairn" && cairnRow.Tier == 1 && cairnRow.Detail.Contains("50 of 500 kills") && srcRows.Rows.IndexOf(cairnRow) > srcRows.Rows.IndexOf(lodgeRow), cairnRow?.Label + " " + cairnRow?.Detail);
                var v2 = all.FirstOrDefault(r => r.Key == "vendor:2"); var v77 = all.FirstOrDefault(r => r.Key == "vendor:77");
                Check("item page: vendors - the Lodge's vendor carries the Cairn's price, a vendor only the Cairn knows gets a Cairn row; the page names the layer",
                    v2 != null && v2.Label == AtlasLabel.Forever && v2.Extra.Contains("Cairn:") && v77 != null && v77.Label == "Cairn" && m.Layers.Contains("cairn"));
                var npc = AtlasQuery.Build("npc:449");
                var loot = npc.Sections.FirstOrDefault(s => s.Key == "loot");
                var lootRows = loot == null ? new List<AtlasRow>() : loot.Rows.Concat(loot.Hidden).ToList();
                Check("creature page: the Cairn's loot table with rates and the lodges behind them, labelled Cairn",
                    lootRows.Count(r => r.Label == "Cairn") == 2 && lootRows.Any(r => r.Detail.Contains("10 of 400 kills")) && loot.Coverage.Contains("the Cairn's Lodges"), loot?.Coverage);
                var skin = npc.Sections.FirstOrDefault(s => s.Key == "skin");
                Check("creature page: gathering from the corpse (skinning) from the Cairn too, also when neither the Lodge nor the Classic baseline has it",
                    skin != null && skin.Rows.Concat(skin.Hidden).Any(r => r.Label == "Cairn" && r.Type == "Skinning" && r.Detail.Contains("70 of 80 corpses")), skin?.ToString());
                if (SelfTestClock.ShotsOn)
                {
                    ShowTab("atlas");
                    await AtlasView.Open("item:100"); await AtlasView.WaitForTest(); await ShotWait(400); Snapshot(Path.Combine(dir, "cairn-itempage.png"));
                    await AtlasView.Open("npc:449"); await AtlasView.WaitForTest(); await ShotWait(400); Snapshot(Path.Combine(dir, "cairn-npcpage.png"));
                    ResetAtlasForTest();
                    ShowTab("settings");
                }
                AtlasQuery.Context.Lodge = savedLodge;

                // ---- Settings > Privacy: no switch of its own; the words tie it to the sharing switch; a calm status line
                SettingsPage.Show("privacy");
                await Task.Delay(100);
                var row = (HeaderedContentControl)SettingsPage.FindName("PvCairnRow");
                var tag = row?.Tag as string ?? "";
                Check("privacy: no Cairn switch of its own any more", SettingsPage.FindName("PvCairn") == null && row != null);
                Check("privacy: the text says the Cairn's shared data is for those who share, names the sharing switch, says it comes from the Lodge",
                    tag.Contains("for those who share") && tag.Contains("Share my Atlas observations with this Lodge") && tag.Contains("your Lodge also gives you the Cairn's numbers") && !tag.Contains("CC0") && tag.Contains("never from the internet"), tag);
                Check("privacy: the status line says where the data came from", SettingsPage.CairnStatusForTest.StartsWith("From your Lodge", StringComparison.Ordinal), SettingsPage.CairnStatusForTest);
                if (SelfTestClock.ShotsOn) { row?.BringIntoView(); await ShotWait(300); Snapshot(Path.Combine(dir, "cairn-privacy.png")); }

                // ---- the Lodge says "share" (it has the member's switch as off): the copy goes
                fake.RefuseStatus = 403; fake.RefuseReason = "share";
                var r6 = await CairnAtlas.Refresh(CairnReachable, force: true);
                Check("the Lodge answers \"share to receive\": the copy is deleted", r6 == "refused: share" && CairnAtlas.Current == null && !File.Exists(Path.Combine(work, "cairn.json.gz")), r6);
                fake.RefuseStatus = 0;

                // ---- the Lodge's own standing lapsed: it says so (cairn.status why lodge, or a refused fetch) and the copy goes
                await CairnAtlas.Refresh(CairnReachable, force: true);
                bool had = CairnAtlas.Current != null;
                Session.FeedForTest("{\"t\":\"cairn.status\",\"entitled\":false,\"sharing\":true,\"why\":\"lodge\",\"text\":\"This Lodge isn't receiving the Cairn's data right now\"}");
                Check("the Lodge lapsed (cairn.status why lodge): the copy is deleted on the spot", had && await CairnWait(() => CairnAtlas.Current == null));
                await CairnAtlas.Refresh(CairnReachable, force: true);
                fake.RefuseStatus = 403; fake.RefuseReason = "lodge";
                var r7 = await CairnAtlas.Refresh(CairnReachable, force: true);
                Check("the Lodge lapsed (a fetch refused with lodge): the copy is deleted and Privacy says why",
                    r7 == "refused: lodge" && CairnAtlas.Current == null && CairnAtlas.StatusText(true, true, true, false, true).Contains("contribute to receive"), r7);
                fake.RefuseStatus = 0;

                // ---- sharing switched off: the copy goes at once, the Lodge is told, nothing more is fetched
                await CairnAtlas.Refresh(CairnReachable, force: true);
                had = CairnAtlas.Current != null;
                lock (sent) sent.Clear();
                int asked = fake.Asked.Count;
                settings.AtlasShareOff = true;
                Session.AtlasSettingsChanged();
                Check("sharing off: the copy is deleted at once and the Lodge is told atlas.sharing {on: false}",
                    had && CairnAtlas.Current == null && !File.Exists(Path.Combine(work, "cairn.json.gz")) && Sharing(false));
                var r8 = await CairnAtlas.Refresh(CairnReachable, force: true);
                Check("sharing off: nothing is fetched", r8 == "off" && fake.Asked.Count == asked, r8);
                Session.FeedForTest("{\"t\":\"cairn.status\",\"entitled\":false,\"sharing\":false,\"why\":\"share\",\"text\":\"Share to receive\"}");
                await Task.Delay(100);
                Check("sharing off: the Lodge's \"share to receive\" keeps it empty, and Privacy says so",
                    CairnAtlas.Current == null && fake.Asked.Count == asked && CairnAtlas.StatusText(true, true, true, false, false).StartsWith("Sharing is off", StringComparison.Ordinal));
                settings.AtlasShareOff = false;
                Session.Disconnect();

                // ---- a guest: never told, never fetched
                lock (sent) sent.Clear();
                Session.CairnClientForTest(new LodgeClient(fake.Url, FakeLodgeCairn.Code, "Elan"));
                Session.FeedForTest(CairnWelcome("guest", true));
                await Task.Delay(50);
                asked = fake.Asked.Count;
                var r9 = await CairnAtlas.Refresh(CairnReachable, force: true);
                Check("guest: nothing is told, nothing fetched, Privacy says it is for members who share",
                    !AnySharing() && r9 == "off" && fake.Asked.Count == asked && CairnAtlas.StatusText(true, true, true, true, true).StartsWith("Guests", StringComparison.Ordinal), r9);
                Session.Disconnect();

                // ---- only GETs, only the two Lodge routes
                Check("only GETs, only the Lodge's /cairn/manifest and /cairn/dataset", fake.Methods.All(x => x == "GET") && fake.Asked.All(a => a.StartsWith("GET /cairn/manifest", StringComparison.Ordinal) || a.StartsWith("GET /cairn/dataset", StringComparison.Ordinal)));

                await SharingGuideTest(Check, dir);
            }
            catch (Exception e) { Check("cairn test ran without an exception", false, e.ToString()); }
            finally
            {
                fake?.Stop();
                Session.Disconnect();
                Session.SentForTest = null; Session.CairnClientForTest(null);
                CairnAtlas.Fetch = savedFetch; CairnAtlas.CacheDirOverride = savedCache;
                CairnAtlas.PinnedKeyX = savedX; CairnAtlas.PinnedKeyY = savedY; AtlasQuery.Context.Lodge = savedLodge;
                settings.AtlasShareOff = savedShareOff; settings.LodgeUrl = savedUrl; settings.WowRoot = savedRoot;
                CairnAtlas.ResetForTest();
                SettingsPage.Show("general");
            }
            return "== cairn (the Cairn's data from the Lodge, give to get) " + (ok ? "ok" : "FAILED") + "\r\n" + string.Join("\r\n", notes);
        }
    }
}
