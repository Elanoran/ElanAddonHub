using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;

namespace ElansAddonHub
{
    // --selftest "cairn": the Cairn's open Atlas dataset in the Outpost, against a fake Cairn (no network): the address rule, off = nothing is
    // fetched, the download (manifest, file, both checksums, the ES256 signature once a key is pinned), 304 / the same file = nothing new,
    // a damaged or wrongly signed file is refused and the copy on hand stays, the copy on disk after a restart (and a damaged one dropped),
    // switching off deletes it, the self-test flag keeps the automatic download off, the dataset index (drops, vendors, spawns, nodes,
    // flights), the item page (a Cairn row labelled "Cairn" below the Lodge's own, the Lodge's row carrying the Cairn's figure as its second
    // line, vendors), the creature page's loot table, Settings > Privacy (the switch, its text, the lodge's contribution line), only GETs.
    public partial class MainWindow
    {
        sealed class FakeCairn
        {
            public readonly Dictionary<string, CairnAtlas.Response> Answers = new Dictionary<string, CairnAtlas.Response>(StringComparer.Ordinal);
            public readonly List<string> Asked = new List<string>();
            public readonly List<string> Etags = new List<string>();
            public Task<CairnAtlas.Response> Fetch(string url, string etag)
            {
                Asked.Add(url); Etags.Add(etag ?? "");
                if (Answers.TryGetValue(url, out var r))
                {
                    if (r.Status == 200 && etag != null && r.ETag == etag && url.EndsWith("manifest.json", StringComparison.Ordinal)) return Task.FromResult(new CairnAtlas.Response { Status = 304, ETag = etag });
                    return Task.FromResult(r);
                }
                return Task.FromResult(new CairnAtlas.Response { Status = 404 });
            }
        }

        const string FakeCairnBase = "https://cairn.example.test/cairn";

        static string CairnDatasetJson(long seq, long kills) =>
            "{\"format\":\"elans-cairn\",\"v\":1,\"license\":\"CC0-1.0\",\"day\":\"2026-10-17\",\"seq\":" + seq + ",\"minLodges\":2,\"lodges\":3,\"builds\":[57689],"
            + "\"drops\":{\"kill\":{\"1\":{\"n\":" + kills + ",\"m\":3,\"b\":57689,\"i\":{\"100\":[120,120,3]}},\"449\":{\"n\":500,\"m\":2,\"b\":57689,\"i\":{\"100\":[50,60,2],\"102\":[10,10,2,400]}}},"
            + "\"skinning\":{\"449\":{\"n\":80,\"m\":2,\"i\":{\"2318\":[70,140,2]}}}},"
            + "\"vendors\":{\"2\":{\"m\":2,\"i\":{\"100\":[12000,1,-1,0,2]}},\"77\":{\"m\":2,\"i\":{\"100\":[11500,1,-1,0,2]}}},"
            + "\"spawns\":{\"449\":[[11,0.3,0.4,9,2]]},\"nodes\":{\"1731\":[[11,0.25,0.5,12,2]]},\"flights\":{\"Horde|11>23\":[95.5,14,2]}}";

        // the manifest + file the fake Cairn serves; key = signing key (null = no signature)
        static (string manifest, byte[] gz, string contentSha) CairnFiles(long seq, long kills, ECDsa key)
        {
            var json = Encoding.UTF8.GetBytes(CairnDatasetJson(seq, kills));
            byte[] gz;
            using (var ms = new MemoryStream()) { using (var z = new GZipStream(ms, CompressionLevel.Optimal, true)) z.Write(json, 0, json.Length); gz = ms.ToArray(); }
            var csha = CairnAtlas.Sha256Hex(json);
            var file = "cairn-2026-10-17-" + seq + ".json.gz";
            var sig = key == null ? "" : Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(CairnAtlas.SignedMessage("2026-10-17", seq, csha)), HashAlgorithmName.SHA256));
            var manifest = "{\"format\":\"elans-cairn-manifest\",\"v\":1,\"latest\":{\"day\":\"2026-10-17\",\"seq\":" + seq + ",\"file\":\"" + file + "\",\"bytes\":" + gz.Length
                + ",\"sha256\":\"" + CairnAtlas.Sha256Hex(gz) + "\",\"contentSha256\":\"" + csha + "\",\"sig\":\"" + sig + "\",\"kid\":\"test\"}}";
            return (manifest, gz, csha);
        }

        static void Serve(FakeCairn f, (string manifest, byte[] gz, string contentSha) files, string etag, byte[] gzOverride = null)
        {
            var file = (string)Json.Obj(files.manifest).Child("latest")["file"];
            f.Answers[FakeCairnBase + "/v1/manifest.json"] = new CairnAtlas.Response { Status = 200, Body = Encoding.UTF8.GetBytes(files.manifest), ETag = etag };
            f.Answers[FakeCairnBase + "/v1/datasets/" + file] = new CairnAtlas.Response { Status = 200, Body = gzOverride ?? files.gz };
        }

        static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        async Task<string> CairnTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            void Check(string what, bool cond, string detail = null) { SelfTestClock.Mark(what); notes.Add((cond ? "ok   " : "FAIL ") + what + (!cond && detail != null ? " [" + detail + "]" : "")); ok &= cond; }
            var work = Path.Combine(dir, "cairn");
            var savedFetch = CairnAtlas.Fetch; var savedUrl = CairnAtlas.UrlOverride; var savedCache = CairnAtlas.CacheDirOverride;
            var savedX = CairnAtlas.PinnedKeyX; var savedY = CairnAtlas.PinnedKeyY; var savedLodge = AtlasQuery.Context.Lodge; var savedOff = settings.CairnOff;
            try
            {
                if (Directory.Exists(work)) Util.DeleteDir(work);
                Directory.CreateDirectory(work);
                var fake = new FakeCairn();
                CairnAtlas.Fetch = fake.Fetch; CairnAtlas.UrlOverride = FakeCairnBase; CairnAtlas.CacheDirOverride = work;
                CairnAtlas.PinnedKeyX = ""; CairnAtlas.PinnedKeyY = "";
                CairnAtlas.ResetForTest();

                // ---- the address rule (the Outpost only downloads, and only over https)
                Check("address: https is taken, plain http only to this PC, nothing with a user / query; the built-in source is https",
                    CairnAtlas.ValidBase(FakeCairnBase, out _) && CairnAtlas.ValidBase("http://127.0.0.1:5290/cairn", out _) && !CairnAtlas.ValidBase("http://cairn.example.test/cairn", out _)
                    && !CairnAtlas.ValidBase("https://u:p@cairn.example.test/", out _) && !CairnAtlas.ValidBase("https://cairn.example.test/?x=1", out _) && CairnAtlas.ValidBase(CairnAtlas.DefaultUrl, out _));

                // ---- off: nothing at all is fetched
                bool on = false;
                var r0 = await CairnAtlas.Refresh(() => on, force: true);
                Check("switched off: nothing is fetched", r0 == "off" && fake.Asked.Count == 0, r0 + " / " + fake.Asked.Count);

                // ---- the first download
                on = true;
                var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                var f1 = CairnFiles(1, 200, key);
                Serve(fake, f1, "\"e1\"");
                var r1 = await CairnAtlas.Refresh(() => on, force: true);
                var c = CairnAtlas.Current;
                Check("download: manifest then the file, both checksums checked, the dataset indexed", r1 == "updated" && c != null && c.Seq == 1 && fake.Asked.Count == 2
                    && fake.Asked[0] == FakeCairnBase + "/v1/manifest.json" && fake.Asked[1].EndsWith("/v1/datasets/cairn-2026-10-17-1.json.gz", StringComparison.Ordinal), r1 + " / " + string.Join(", ", fake.Asked));
                Check("index: drops by item (with the per-item kills when a Lodge was left out), skinning, vendors, spawns, nodes, flights, lodges",
                    c != null && c.Loot("kill", 449)?.N == 500 && c.DropsOf(100).Count == 2 && c.DropsOf(102).Single().Kills == 400 && c.Loot("skinning", 449)?.Items.Single().Item == 2318
                    && c.VendorsOf(100).Count == 2 && c.Stock(77).Single().Price == 11500 && c.Spawns(449).Single().Map == 11 && c.Nodes(1731).Single().N == 12
                    && c.Flight("Horde|11>23", out var secs, out var fm) && secs == 95.5 && fm == 2 && c.Lodges == 3);
                Check("the copy is kept on disk (the file as downloaded and its checksums)", File.Exists(Path.Combine(work, "cairn.json.gz")) && File.ReadAllText(Path.Combine(work, "meta.json")).Contains(f1.contentSha));

                // ---- nothing new: 304 (etag), or the same file again
                fake.Asked.Clear();
                var r2 = await CairnAtlas.Refresh(() => on, force: true);
                Check("nothing new: the manifest is asked with its ETag, the answer 304 costs no download", r2 == "unchanged" && fake.Asked.Count == 1 && fake.Etags.Last() == "\"e1\"", r2 + " / " + fake.Asked.Count);
                var r2b = await CairnAtlas.Refresh(() => on);
                Check("checked at most every 12 hours (a second ask within that does nothing)", r2b == "fresh" && fake.Asked.Count == 1);

                // ---- a damaged file is refused, the copy on hand stays
                var f2 = CairnFiles(2, 300, key);
                var broken = (byte[])f2.gz.Clone(); broken[broken.Length / 2] ^= 0x55;
                Serve(fake, f2, "\"e2\"", broken);
                var r3 = await CairnAtlas.Refresh(() => on, force: true);
                Check("a damaged download is refused (checksum) and the data on hand stays", r3.StartsWith("failed", StringComparison.Ordinal) && r3.Contains("checksum") && CairnAtlas.Current?.Seq == 1, r3);

                // ---- with a pinned key: a good signature is taken, a wrong one refused
                var pub = key.ExportParameters(false);
                CairnAtlas.PinnedKeyX = B64Url(pub.Q.X); CairnAtlas.PinnedKeyY = B64Url(pub.Q.Y);
                var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                var f3 = CairnFiles(3, 400, other);
                Serve(fake, f3, "\"e3\"");
                var r4 = await CairnAtlas.Refresh(() => on, force: true);
                Check("pinned key: a file signed by another key is refused before it is downloaded", r4.Contains("signature") && CairnAtlas.Current?.Seq == 1 && !fake.Asked.Last().EndsWith("-3.json.gz", StringComparison.Ordinal), r4);
                var unsigned = CairnFiles(3, 400, null);
                Serve(fake, unsigned, "\"e3b\"");
                var r4b = await CairnAtlas.Refresh(() => on, force: true);
                Check("pinned key: an unsigned file is refused too", r4b.Contains("signature") && CairnAtlas.Current?.Seq == 1, r4b);
                var f4 = CairnFiles(4, 500, key);
                Serve(fake, f4, "\"e4\"");
                var r5 = await CairnAtlas.Refresh(() => on, force: true);
                Check("pinned key: the Cairn's own signature is taken", r5 == "updated" && CairnAtlas.Current?.Seq == 4 && CairnAtlas.Current.Loot("kill", 1).N == 500, r5);
                CairnAtlas.PinnedKeyX = ""; CairnAtlas.PinnedKeyY = "";

                // ---- only ever GET, never an upload: the fetch interface has no body, and every address was a manifest or a dataset file
                Check("the Outpost never uploads to the Cairn: only the manifest and dataset files were asked for",
                    fake.Asked.All(u => u == FakeCairnBase + "/v1/manifest.json" || u.StartsWith(FakeCairnBase + "/v1/datasets/cairn-", StringComparison.Ordinal)));

                // ---- after a restart: the copy on disk (checked again); a damaged copy is dropped
                CairnAtlas.ResetForTest();
                CairnAtlas.LoadCached();
                Check("restart: the copy on disk is read back", CairnAtlas.Current?.Seq == 4);
                var gzPath = Path.Combine(work, "cairn.json.gz");
                var keep = File.ReadAllBytes(gzPath);
                File.WriteAllBytes(gzPath, f2.gz);      // another (valid gzip) file under the saved checksums
                CairnAtlas.ResetForTest();
                CairnAtlas.LoadCached();
                Check("restart: a copy that does not match its saved checksum is dropped", CairnAtlas.Current == null && !File.Exists(gzPath));
                File.WriteAllBytes(gzPath, keep);
                Serve(fake, f4, "\"e4\"");
                await CairnAtlas.Refresh(() => on, force: true);

                // ---- the self-test never starts the automatic download (no real Cairn is ever contacted)
                int before = fake.Asked.Count;
                CairnAtlas.Start(() => true);
                await Task.Delay(150);
                Check("the self-test keeps the automatic download off", LinkOpen.SelfTestRun && fake.Asked.Count == before);

                // ---- the Atlas pages: the Lodge's own rows first, the Cairn's below them, labelled "Cairn"
                var lodge = AtlasTestLodge();          // npc 1 drops item 100 (41 of 340), vendor 2 sells it
                AtlasQuery.Context.Lodge = () => lodge;
                var m = AtlasQuery.Build("item:100");
                var srcRows = m.Sections.FirstOrDefault(s => s.Key == "sources");
                var all = srcRows == null ? new List<AtlasRow>() : srcRows.Rows.Concat(srcRows.Hidden).ToList();
                var lodgeRow = all.FirstOrDefault(r => r.Key == "npc:1");
                var cairnRow = all.FirstOrDefault(r => r.Key == "npc:449");
                Check("item page: the Lodge's own drop row stays on top, with the Cairn's figure for the same creature as its second line",
                    lodgeRow != null && lodgeRow.Label == AtlasLabel.Forever && lodgeRow.Extra.Contains("Cairn: 120 of 500 kills") && lodgeRow.Extra.Contains("3 lodges"), lodgeRow?.Extra);
                Check("item page: a creature only the Cairn knows is a row labelled \"Cairn\" (tier 1) below the Lodge's rows",
                    cairnRow != null && cairnRow.Label == "Cairn" && cairnRow.Tier == 1 && cairnRow.Detail.Contains("50 of 500 kills") && srcRows.Rows.IndexOf(cairnRow) > srcRows.Rows.IndexOf(lodgeRow), cairnRow?.Label + " " + cairnRow?.Detail);
                var v2 = all.FirstOrDefault(r => r.Key == "vendor:2"); var v77 = all.FirstOrDefault(r => r.Key == "vendor:77");
                Check("item page: vendors - the Lodge's vendor carries the Cairn's price, a vendor only the Cairn knows gets a Cairn row; the page names the layer",
                    v2 != null && v2.Label == AtlasLabel.Forever && v2.Extra.Contains("Cairn:") && v77 != null && v77.Label == "Cairn" && m.Layers.Contains("cairn"));
                var npc = AtlasQuery.Build("npc:449");
                var loot = npc.Sections.FirstOrDefault(s => s.Key == "loot");
                var lootRows = loot == null ? new List<AtlasRow>() : loot.Rows.Concat(loot.Hidden).ToList();
                Check("creature page: the Cairn's loot table with rates and the lodges behind them, labelled Cairn",
                    lootRows.Count(r => r.Label == "Cairn") == 2 && lootRows.Any(r => r.Detail.Contains("10 of 400 kills")) && loot.Note.Contains("The Cairn"), loot?.Note);
                var skin = npc.Sections.FirstOrDefault(s => s.Key == "skin");
                Check("creature page: gathering from the corpse (skinning) from the Cairn too, also when neither the Lodge nor the Classic baseline has it",
                    skin != null && skin.Rows.Concat(skin.Hidden).Any(r => r.Label == "Cairn" && r.Type == "Skinning" && r.Detail.Contains("70 of 80 corpses")), skin?.ToString());

                // ---- pictures (ELANSHUB_SHOTS=1): the item page with Lodge + Cairn rows, the creature page's loot from the Cairn
                if (SelfTestClock.ShotsOn)
                {
                    ShowTab("atlas");
                    await AtlasView.Open("item:100"); await AtlasView.WaitForTest(); await ShotWait(400); Snapshot(Path.Combine(dir, "cairn-itempage.png"));
                    await AtlasView.Open("npc:449"); await AtlasView.WaitForTest(); await ShotWait(400); Snapshot(Path.Combine(dir, "cairn-npcpage.png"));
                    ResetAtlasForTest();
                    ShowTab("settings");
                }

                // ---- Settings > Privacy: the switch (on by default), its words, the lodge's contribution line; off deletes the copy
                SettingsPage.Show("privacy");
                await Task.Delay(100);
                if (SelfTestClock.ShotsOn) { ((FrameworkElement)SettingsPage.FindName("PvCairnRow"))?.BringIntoView(); await ShotWait(300); Snapshot(Path.Combine(dir, "cairn-privacy.png")); }
                var sw = (CheckBox)SettingsPage.FindName("PvCairn");
                var row = (HeaderedContentControl)SettingsPage.FindName("PvCairnRow");
                var tag = row?.Tag as string ?? "";
                Check("privacy: the Cairn switch is on by default and says what it downloads and that nothing is uploaded",
                    sw != null && sw.IsChecked == true && !new Settings().CairnOff && tag.Contains("ids and counts") && tag.Contains("never uploaded") && tag.Contains("CC0"), tag);
                settings.CairnOff = false;
                SettingsPage.ToggleForTest("PvCairn");
                Check("privacy: switching it off stops the download and deletes the copy", settings.CairnOff && CairnAtlas.Current == null && !File.Exists(Path.Combine(work, "cairn.json.gz")));
                var r6 = await CairnAtlas.Refresh(() => !settings.CairnOff, force: true);
                Check("privacy: while off, nothing is fetched", r6 == "off");
                settings.CairnOff = savedOff;
            }
            catch (Exception e) { Check("cairn test ran without an exception", false, e.ToString()); }
            finally
            {
                CairnAtlas.Fetch = savedFetch; CairnAtlas.UrlOverride = savedUrl; CairnAtlas.CacheDirOverride = savedCache;
                CairnAtlas.PinnedKeyX = savedX; CairnAtlas.PinnedKeyY = savedY; AtlasQuery.Context.Lodge = savedLodge;
                settings.CairnOff = savedOff;
                CairnAtlas.ResetForTest();
                SettingsPage.Show("general");
            }
            return "== cairn (the Cairn's open Atlas data) " + (ok ? "ok" : "FAILED") + "\r\n" + string.Join("\r\n", notes);
        }
    }
}
