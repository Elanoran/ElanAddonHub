using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Atlas.Classic;

namespace ElansAddonHub
{
    // --selftest "atlasclassiclodge": the Classic baseline from the Lodge (server 2.17, feature "classic"). Every file is synthetic (a few lines of invented SQL, invented licence
    // texts); the Lodge is a small fake of the server's /datasets endpoints on 127.0.0.1 plus synthetic WebSocket frames (the real server is tested by server/Lodge.Tests).
    // Covers: the bundle (pack, unpack with safe names, release ordering), a member's automatic fetch (missing / same release / newer release / damaged / busy), the switch,
    // "Remove from this PC", the banner as the fallback, the owner's and officers' share button and quiet offer, what a member and a guest see, and an old server.
    public partial class MainWindow
    {
        // a stand-in for GET/PUT /datasets/classic: same URL, headers and refusals, an in-memory store
        sealed class FakeDatasetServer
        {
            readonly HttpListener listener = new HttpListener();
            public readonly string Url;
            public const string Code = "synthetic-test-code";
            public byte[] Bundle;
            public string Version = "", Sha = "";
            public bool Corrupt;                        // flip a byte on download: the SHA-256 will not match
            public int ForceStatus;                     // answer every request with this status (429 = busy)
            public string Role = "officer";
            public int Gets, Puts;

            public FakeDatasetServer()
            {
                int port;
                var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); l.Start(); port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
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

            public void Host(byte[] bundle, string version) { Bundle = bundle; Version = version; Sha = Hex(bundle); }

            public static string Hex(byte[] b) { using (var h = SHA256.Create()) return LodgeFiles.Hex(h.ComputeHash(b)); }

            static void Refuse(HttpListenerContext c, int status, string reason, string text)
            {
                var b = Encoding.UTF8.GetBytes("{\"error\":" + Json.Write(text) + ",\"reason\":" + Json.Write(reason) + "}");
                c.Response.StatusCode = status; c.Response.ContentType = "application/json"; c.Response.ContentLength64 = b.Length;
                c.Response.OutputStream.Write(b, 0, b.Length); c.Response.Close();
            }

            void Serve(HttpListenerContext c)
            {
                var req = c.Request;
                if (req.Headers["X-Lodge-Code"] != Code) { Refuse(c, 401, "auth", "Wrong invite code"); return; }
                if (req.Url.AbsolutePath.Trim('/') != "datasets/classic") { Refuse(c, 404, "unknown", "That dataset doesn't exist"); return; }
                if (ForceStatus != 0) { Refuse(c, ForceStatus, "rate", "You already fetched this a few times - try again in a while"); return; }
                if (req.HttpMethod == "GET")
                {
                    Interlocked.Increment(ref Gets);
                    if (Bundle == null) { Refuse(c, 404, "missing", "The lodge doesn't hold that"); return; }
                    var data = (byte[])Bundle.Clone();
                    if (Corrupt) data[data.Length / 2] ^= 0xFF;
                    c.Response.ContentType = "application/octet-stream"; c.Response.ContentLength64 = data.Length;
                    c.Response.Headers["X-Sha256"] = Sha; c.Response.Headers["X-Version"] = Uri.EscapeDataString(Version);
                    c.Response.OutputStream.Write(data, 0, data.Length); c.Response.Close();
                    return;
                }
                if (req.HttpMethod != "PUT") { Refuse(c, 405, "method", "No"); return; }
                if (Role != "officer" && Role != "owner") { Refuse(c, 403, "forbidden", "Only officers and the lodge owner can share this with the lodge"); return; }
                var ms = new MemoryStream();
                req.InputStream.CopyTo(ms);
                var bytes = ms.ToArray();
                var sha = (req.Headers["X-Sha256"] ?? "").ToLowerInvariant();
                if (req.ContentLength64 < 0) { Refuse(c, 411, "length", "The upload needs a Content-Length"); return; }
                if (bytes.Length != req.ContentLength64) { Refuse(c, 400, "length", "The upload is shorter than it announced"); return; }
                if (Hex(bytes) != sha) { Refuse(c, 400, "sha-mismatch", "The file arrived damaged (checksum differs) - try again"); return; }
                Interlocked.Increment(ref Puts);
                Host(bytes, Uri.UnescapeDataString(req.Headers["X-Version"] ?? ""));
                var ok = Encoding.UTF8.GetBytes("{\"name\":\"classic\",\"available\":true}");
                c.Response.StatusCode = 200; c.Response.ContentType = "application/json"; c.Response.ContentLength64 = ok.Length;
                c.Response.OutputStream.Write(ok, 0, ok.Length); c.Response.Close();
            }
        }

        // the Classic folder on a PC, as the downloader leaves it: the dump, the licence files, update scripts and source.json
        static void AclWriteSet(string folder, string dumpName, string commitDate, string commit, bool updates = true)
        {
            if (Directory.Exists(folder)) Util.DeleteDir(folder);
            Directory.CreateDirectory(folder);
            var sql = new StringBuilder("-- synthetic\nCREATE TABLE t (a int);\n");
            for (int i = 0; i < 300; i++) sql.Append("INSERT INTO t VALUES (" + i + ",'row " + (i * 7919 % 1013) + " of " + commit + "');\n");
            using (var fs = File.Create(Path.Combine(folder, dumpName)))
            using (var gz = new GZipStream(fs, CompressionMode.Compress))
            { var b = Encoding.UTF8.GetBytes(sql.ToString()); gz.Write(b, 0, b.Length); }
            File.WriteAllText(Path.Combine(folder, "LICENSE.md"), "synthetic licence text");
            File.WriteAllText(Path.Combine(folder, "COPYRIGHT.md"), "synthetic copyright text");
            File.WriteAllText(Path.Combine(folder, "AUTHORS"), "synthetic authors");
            if (updates)
            {
                Directory.CreateDirectory(Path.Combine(folder, "updates"));
                File.WriteAllText(Path.Combine(folder, "updates", "z1_one.sql"), "UPDATE t SET a = 1;");
                File.WriteAllText(Path.Combine(folder, "updates", "z2_two.sql"), "UPDATE t SET a = 2;");
            }
            File.WriteAllText(Path.Combine(folder, "source.json"), Json.Write(new Dictionary<string, object>
            {
                ["repo"] = "https://example.invalid/classic-db", ["license"] = "GPL-3.0", ["dump"] = dumpName, ["commit"] = commit, ["commitDate"] = commitDate, ["updates"] = updates ? 2.0 : 0.0,
            }), new UTF8Encoding(false));
        }

        // a fake of the downloader + importer behind the card that really keeps its files in `Dir` (so the Lodge's install can be inspected)
        sealed class AclBackend : IClassicBackend
        {
            public string Dir;
            public bool Indexed;
            public int Builds, Queries, Downloads, Deletes;
            public string ManualDump = "ClassicDB_manual.sql.gz", ManualDate = "2024-03-01T10:00:00Z", ManualCommit = "manual1";
            public ClassicState State() => !File.Exists(Path.Combine(Dir, "source.json")) ? ClassicState.NotDownloaded : Indexed ? ClassicState.Ready : ClassicState.Downloaded;
            public ClassicIndex Index()
            {
                if (State() != ClassicState.Ready) return null;
                var src = ClassicDownloader.ReadSource(Dir);
                var idx = new ClassicIndex();
                idx.Provenance.DumpName = src?.Str("dump"); idx.Provenance.CommitDate = src?.Str("commitDate"); idx.Provenance.CommitSha = src?.Str("commit");
                return idx;
            }
            public Task<ClassicRemote> Query(CancellationToken ct)
            {
                Queries++;
                var r = new ClassicRemote { DumpName = ManualDump, DumpSize = 1_000_000, CommitSha = ManualCommit, CommitDate = ManualDate };
                return Task.FromResult(r);
            }
            public async Task<ClassicDownloadResult> Download(ClassicRemote remote, IProgress<ClassicDownloadProgress> progress, CancellationToken ct)
            {
                Downloads++;
                progress?.Report(new ClassicDownloadProgress { Phase = "Downloading the dump", File = remote.DumpName, BytesDone = 500, BytesTotal = 1000 });
                await Task.Delay(20, ct).ConfigureAwait(false);
                AclWriteSet(Dir, remote.DumpName, remote.CommitDate, remote.CommitSha);
                return new ClassicDownloadResult { Dir = Dir, DumpName = remote.DumpName };
            }
            public async Task<ClassicImportReport> BuildIndex(IProgress<ClassicProgress> progress, CancellationToken ct)
            {
                Builds++;
                progress?.Report(new ClassicProgress { Phase = "Resolving loot", Fraction = 0.5 });
                await Task.Delay(20, ct).ConfigureAwait(false);
                Indexed = true;
                return new ClassicImportReport();
            }
            public void Delete() { Deletes++; Indexed = false; if (Directory.Exists(Dir)) Util.DeleteDir(Dir); }
            public string Folder() => Dir;
        }

        string AclWelcome(string role, string server, bool feature, bool canShare)
        {
            var features = "\"reply\",\"react\",\"react2\",\"pin\",\"profile\",\"displayname\",\"lfg\",\"history2\",\"search\"" + (feature ? ",\"classic\"" : "");
            var w = FunWelcome(role, FunPeople(false), "", features, server);
            return w.Substring(0, w.Length - 1) + ",\"canShareClassic\":" + (canShare ? "true" : "false") + "}";
        }

        static string AclData(string type, FakeDatasetServer s, bool canManage, bool available = true) =>
            "{\"t\":" + Q(type) + (type == "dataset.changed" ? ",\"action\":\"replace\"" : "") + ",\"name\":\"classic\",\"available\":" + (available ? "true" : "false")
            + (available ? ",\"version\":" + Q(s.Version) + ",\"sha256\":" + Q(s.Sha) + ",\"size\":" + s.Bundle.Length + ",\"uploadedBy\":\"Elan\",\"at\":1790000000000" : "")
            + ",\"canManage\":" + (canManage ? "true" : "false") + ",\"maxMb\":40}";

        async Task<string> AtlasClassicLodgeTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            void Check(string what, bool cond, string detail = null) { notes.Add((cond ? "ok   " : "FAIL ") + what + (!cond && detail != null ? " [" + detail + "]" : "")); ok &= cond; }
            string P(string n) => Path.Combine(dir, n);
            var work = Path.Combine(dir, "atlasclassiclodge");
            var sent = new List<Dictionary<string, object>>();
            FakeDatasetServer fake = null;
            ClassicLodgeSync mine = null;
            var realSync = ClassicSync;
            var hadSetup = AtlasView.ClassicSetup;
            var hadOff = settings.ClassicFromLodgeOff; var hadPaused = settings.ClassicLodgePaused; var hadOffered = settings.ClassicShareOffered; var hadUrl = settings.LodgeUrl;
            var hadClock = Session.Clock;
            var hadHook = Dialog.TestHook;
            var clock = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);
            try
            {
                if (Directory.Exists(work)) Util.DeleteDir(work);
                Directory.CreateDirectory(work);
                Check("fixture: the work folder is under the scratch folder (never the real data folder)", Path.GetFullPath(work).StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase));
                if (realSync != null) realSync.Suspended = true;
                Session.Disconnect();
                Session.Clock = () => clock;
                Session.SentForTest = o => sent.Add(Json.Obj(Json.Write(o)));
                settings.LodgeUrl = "http://classic.test/lodge";
                settings.ClassicFromLodgeOff = false; settings.ClassicLodgePaused = false; settings.ClassicShareOffered = null;
                fake = new FakeDatasetServer();
                Session.ClassicClientForTest(new LodgeClient(fake.Url, FakeDatasetServer.Code, "Mia"));
                ElansAddonHub.AtlasPage.ResetClassicBannerForTest();

                // ================================================================ the bundle: release A (older) and B (newer)
                var setA = Path.Combine(work, "setA"); var setB = Path.Combine(work, "setB");
                AclWriteSet(setA, "ClassicDB_a.sql.gz", "2024-01-01T10:00:00Z", "aaa111");
                AclWriteSet(setB, "ClassicDB_b.sql.gz", "2024-06-01T10:00:00Z", "bbb222");
                var packA = ClassicBundle.Pack(setA, Path.Combine(work, "A.zip"), out var verA); var packB = ClassicBundle.Pack(setB, Path.Combine(work, "B.zip"), out var verB);
                Check("bundle: pack puts the original files into one zip", packA == null && packB == null && File.Exists(Path.Combine(work, "A.zip")));
                var bytesA = File.ReadAllBytes(Path.Combine(work, "A.zip")); var bytesB = File.ReadAllBytes(Path.Combine(work, "B.zip"));
                using (var z = ZipFile.OpenRead(Path.Combine(work, "A.zip")))
                    Check("bundle: it holds exactly the dump, the three licence files, source.json and the update scripts - nothing else", z.Entries.Select(e => e.FullName).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(
                        new[] { "AUTHORS", "COPYRIGHT.md", "ClassicDB_a.sql.gz", "LICENSE.md", "source.json", "updates/z1_one.sql", "updates/z2_two.sql" }.OrderBy(x => x, StringComparer.Ordinal)));
                Check("bundle: the version reads '<commit date>|<dump>|<commit>' and the newer release is the newer one", verA == "2024-01-01T10:00:00Z|ClassicDB_a.sql.gz|aaa111" && ClassicBundle.IsNewer(verB, verA) && !ClassicBundle.IsNewer(verA, verB) && !ClassicBundle.IsNewer(verA, verA)
                    && ClassicBundle.IsNewer("|ClassicDB_z.sql.gz|", "|ClassicDB_a.sql.gz|") && ClassicBundle.IsNewer(verA, null) && !ClassicBundle.IsNewer(null, verA), verA);
                Check("bundle: a release is described by its dump name and date", ClassicBundle.Describe(verB) == "ClassicDB_b (1 Jun 2024)", ClassicBundle.Describe(verB));
                var unp = Path.Combine(work, "unpacked");
                Check("bundle: unpack gives back the same bytes", ClassicBundle.Unpack(Path.Combine(work, "A.zip"), unp) == null && File.ReadAllBytes(Path.Combine(unp, "ClassicDB_a.sql.gz")).SequenceEqual(File.ReadAllBytes(Path.Combine(setA, "ClassicDB_a.sql.gz")))
                    && File.ReadAllText(Path.Combine(unp, "LICENSE.md")) == "synthetic licence text" && File.Exists(Path.Combine(unp, "updates", "z2_two.sql")));
                Func<string, Action<ZipArchive>, string, string> evilZip = (name, add, omit) =>
                {
                    var f = Path.Combine(work, name + ".zip");
                    using (var z = ZipFile.Open(f, ZipArchiveMode.Create))
                    {
                        foreach (var n in new[] { "ClassicDB_a.sql.gz", "LICENSE.md", "COPYRIGHT.md", "AUTHORS", "source.json" }.Where(x => x != omit)) { var e = z.CreateEntry(n); using (var s = e.Open()) { var b = File.ReadAllBytes(Path.Combine(setA, n)); s.Write(b, 0, b.Length); } }
                        add(z);
                    }
                    return f;
                };
                var outside = Path.Combine(work, "evil.txt");
                foreach (var bad in new[] { "../evil.txt", "setup.exe", "updates/sub/x.sql", "ClassicDB_second.sql.gz", "updates/run.ps1" })
                {
                    var f = evilZip("bad" + Math.Abs(bad.GetHashCode()), z => { var e = z.CreateEntry(bad); using (var s = e.Open()) s.WriteByte(65); }, null);
                    var stage = Path.Combine(work, "stage-" + Math.Abs(bad.GetHashCode()));
                    Check("bundle: unpack refuses an entry named '" + bad + "' and nothing lands outside the staging folder", ClassicBundle.Unpack(f, stage) != null && !File.Exists(outside) && !File.Exists(Path.Combine(work, "setup.exe")));
                }
                Check("bundle: a zip without the licence is refused", ClassicBundle.Unpack(evilZip("nolic", z => { }, "LICENSE.md"), Path.Combine(work, "stage-nolic")) is string why1 && why1.Contains("LICENSE.md"));
                File.WriteAllText(Path.Combine(work, "junk.zip"), "not a zip at all");
                Check("bundle: something that is not a zip is refused", ClassicBundle.Unpack(Path.Combine(work, "junk.zip"), Path.Combine(work, "stage-junk")) != null);

                // a setup on a fresh PC: the files really live in `folder`, the importer is a stand-in
                AclBackend NewPc(string name, out ClassicSetup setup)
                {
                    var be = new AclBackend { Dir = Path.Combine(work, name) };
                    setup = new ClassicSetup(be);
                    AtlasView.UseClassicSetup(setup);
                    return be;
                }
                ClassicLodgeSync NewSync()
                {
                    mine?.Dispose();
                    mine = new ClassicLodgeSync(Session, () => AtlasView.ClassicSetup, settings, () => { });
                    AtlasView.UseLodgeSync(mine);
                    SettingsPage.UseClassicSync(mine);
                    return mine;
                }
                async Task<bool> Idle(ClassicSetup s, int ms = 10000) => await WaitFor(() => !s.Busy && !mine.Fetching && !mine.Sharing, ms);
                ShowTab("atlas");
                await AtlasView.GoStart();

                // ================================================================ a server older than 2.17: the banner only
                var sync = NewSync();
                var be1 = NewPc("pc-old", out var setup1);
                fake.Host(bytesA, verA);
                sent.Clear();
                Session.FeedForTest(AclWelcome("member", "2.16.0", false, false));
                Session.FeedForTest(AclData("dataset.data", fake, false));
                await Task.Delay(300);
                Check("old server (2.16): nothing is asked or fetched, the Lodge can't provide, the card is as before", !Session.SupportsClassic && !sync.CanProvide && fake.Gets == 0 && sent.All(o => !(o.Str("t") ?? "").StartsWith("dataset.")) && setup1.State == ClassicCardState.NotDownloaded && !Directory.Exists(be1.Dir));
                await AtlasView.GoStart(); await Task.Delay(100); UpdateLayout();
                Check("old server: the banner offers the public download (no mention of the Lodge), no share offer", AtlasView.ClassicBannerShownForTest && AtlasView.ClassicBannerGetForTest != null && !AtlasTexts(AtlasView.ClassicBannerForTest).Any(t => t.Contains("Your Lodge has it")) && !AtlasView.ClassicOfferShownForTest);
                ShowTab("settings"); SettingsPage.Show("lodge"); SettingsPage.ClassicRefreshForTest(); await Task.Delay(100);
                Check("old server: Settings says the server is too old, no share row (a member can't update it)", SettingsPage.ClassicStatusForTest.Contains("older than 2.17") && !SettingsPage.ClassicLodgeRowVisibleForTest && SettingsPage.ClassicAutoForTest, SettingsPage.ClassicStatusForTest);
                Session.Disconnect();
                Session.FeedForTest(AclWelcome("owner", "2.16.0", false, false));
                SettingsPage.ClassicRefreshForTest(); await Task.Delay(100);
                Check("old server, as the owner: the line says to update the server", SettingsPage.ClassicStatusForTest.Contains("Update it") && !SettingsPage.ClassicShareVisibleForTest, SettingsPage.ClassicStatusForTest);
                Session.Disconnect();

                // ================================================================ a guest on 2.17: nothing is asked or offered
                sent.Clear();
                Session.FeedForTest(AclWelcome("guest", "2.17.0", true, false));
                await Task.Delay(200);
                Check("guest: no dataset message is sent, nothing is fetched, the Lodge can't provide", sent.All(o => !(o.Str("t") ?? "").StartsWith("dataset.")) && fake.Gets == 0 && !sync.CanProvide);
                Session.FeedForTest(AclData("dataset.data", fake, false));
                await Task.Delay(300);
                Check("guest: even if a frame arrives nothing is fetched and the settings line sends them to the Atlas page", fake.Gets == 0 && !Directory.Exists(be1.Dir) && sync.StatusText.Contains("Atlas page"), sync.StatusText);
                Session.Disconnect();

                // ================================================================ a member without Classic data: it arrives by itself
                var be2 = NewPc("pc-member", out var setup2);
                sync = NewSync();
                sent.Clear(); fake.Gets = 0;
                Session.FeedForTest(AclWelcome("member", "2.17.0", true, false));
                Check("member: the Outpost asks the Lodge what it hosts (dataset.info for 'classic')", sent.Count(o => o.Str("t") == "dataset.info" && o.Str("name") == "classic") == 1);
                var states = new List<ClassicCardState>(); var phases = new List<string>();
                setup2.Changed += () => { lock (states) { states.Add(setup2.State); phases.Add(setup2.Phase); } };
                Session.FeedForTest(AclData("dataset.data", fake, false));
                bool got = await WaitFor(() => setup2.State == ClassicCardState.Ready, 10000); await Idle(setup2);
                await Task.Delay(150); UpdateLayout();
                List<ClassicCardState> seq; lock (states) seq = states.Distinct().ToList();
                Check("member: with no Classic data the Lodge's is fetched at once, unpacked, read and ready - no question, no click", got && fake.Gets == 1 && be2.Builds == 1 && be2.Queries == 0 && be2.Downloads == 0);
                Check("member: it went Downloading -> Importing -> Ready, the phase said 'from your Lodge'", seq.SequenceEqual(new[] { ClassicCardState.Downloading, ClassicCardState.Importing, ClassicCardState.Ready }) && phases.Contains("Getting Classic data from your Lodge"), string.Join(">", seq));
                Check("member: the original files are in the Classic folder (dump, the three licence files, the updates) and source.json says via the Lodge",
                    File.Exists(Path.Combine(be2.Dir, "ClassicDB_a.sql.gz")) && File.ReadAllText(Path.Combine(be2.Dir, "LICENSE.md")) == "synthetic licence text" && File.Exists(Path.Combine(be2.Dir, "COPYRIGHT.md")) && File.Exists(Path.Combine(be2.Dir, "AUTHORS"))
                    && Directory.GetFiles(Path.Combine(be2.Dir, "updates")).Length == 2 && ClassicDownloader.ReadSource(be2.Dir)?.Str("via") == "lodge" && ClassicDownloader.ReadSource(be2.Dir)?.Str("commit") == "aaa111");
                Check("member: the dump is byte for byte the original (the Lodge changes nothing)", File.ReadAllBytes(Path.Combine(be2.Dir, "ClassicDB_a.sql.gz")).SequenceEqual(File.ReadAllBytes(Path.Combine(setA, "ClassicDB_a.sql.gz"))));
                Check("member: no staging files are left behind", !Directory.Exists(Path.Combine(be2.Dir, ".lodge-staging")));
                await AtlasView.Open(AtlasNav.DataId); await Task.Delay(100); UpdateLayout();       // the card lives on the Data sources page
                Check("member: the card says Ready, the source line says 'via your Lodge' and the credit says it came unchanged from the Lodge, labelled Classic baseline",
                    setup2.ViaLodge && setup2.ReadyText.StartsWith("Ready") && setup2.ReadyText.Contains("via your Lodge") && setup2.CreditText.Contains("from your Lodge") && setup2.CreditText.Contains("Classic baseline") && ClassicLabel.Source == "Classic baseline"
                    && AtlasTexts(AtlasView.ClassicCardForTest).Any(t => t.Contains("via your Lodge")) && AtlasTexts(AtlasView.ClassicCardForTest).Any(t => t.Contains("received unchanged from your Lodge")) && AtlasTexts(AtlasView.ClassicCardForTest).Contains("Licence (file on this PC)"),
                    setup2.ReadyText);
                Check("member: the banner is gone, there is no share offer and the share button is not there", !AtlasView.ClassicBannerShownForTest && !AtlasView.ClassicOfferShownForTest && !sync.ShareAvailable && !sync.ShareOfferPending);
                ShowTab("settings"); SettingsPage.Show("lodge"); SettingsPage.ClassicScrollForTest(); SettingsPage.ClassicRefreshForTest(); await Task.Delay(150); UpdateLayout();
                Check("member: Settings > Lodge shows the switch (on), no 'Classic data for the Lodge' row, and says the Lodge hosts it and this PC got it from there",
                    SettingsPage.ClassicAutoForTest && !SettingsPage.ClassicLodgeRowVisibleForTest && !SettingsPage.ClassicShareVisibleForTest && SettingsPage.ClassicStatusForTest.Contains("Your Lodge hosts the Classic data") && SettingsPage.ClassicStatusForTest.Contains("ClassicDB_a (1 Jan 2024)")
                    && SettingsPage.ClassicStatusForTest.Contains("shared by Elan") && SettingsPage.ClassicStatusForTest.Contains("got it from the Lodge"), SettingsPage.ClassicStatusForTest);
                Snapshot(P("classic-lodge-settings-member.png"));
                ShowTab("atlas"); await AtlasView.GoStart(); await Task.Delay(150); UpdateLayout();
                Snapshot(P("classic-lodge-atlas-ready.png"));

                // ---- the same release again (a reconnect): nothing is downloaded
                Session.Disconnect(); sent.Clear();
                Session.FeedForTest(AclWelcome("member", "2.17.0", true, false));
                Session.FeedForTest(AclData("dataset.data", fake, false));
                await Task.Delay(400); await Idle(setup2);
                Check("same release installed: no download at all after a reconnect", fake.Gets == 1 && be2.Builds == 1 && setup2.State == ClassicCardState.Ready);

                // ---- an older release at the Lodge never replaces newer data
                // ---- a newer release at the Lodge replaces the older one on this PC
                fake.Host(bytesB, verB);
                Session.FeedForTest(AclData("dataset.changed", fake, false));
                got = await WaitFor(() => fake.Gets == 2, 10000); await Idle(setup2);
                await Task.Delay(150);
                Check("newer release at the Lodge: this PC gets it, the old dump is gone, only the new one stays, source.json is the new one",
                    got && setup2.State == ClassicCardState.Ready && File.Exists(Path.Combine(be2.Dir, "ClassicDB_b.sql.gz")) && !File.Exists(Path.Combine(be2.Dir, "ClassicDB_a.sql.gz")) && ClassicDownloader.ReadSource(be2.Dir)?.Str("commit") == "bbb222" && be2.Builds == 2);
                Check("newer release: the card shows the new dump date and still 'via your Lodge'", setup2.ReadyText.Contains("via your Lodge") && ClassicBundle.LocalVersion(be2.Dir).StartsWith("2024-06-01"), setup2.ReadyText);
                // the Lodge now only has the older one: this PC keeps its newer data
                fake.Host(bytesA, verA);
                Session.FeedForTest(AclData("dataset.changed", fake, false));
                await Task.Delay(400); await Idle(setup2);
                Check("older release at the Lodge: this PC keeps its newer data, nothing is downloaded", fake.Gets == 2 && File.Exists(Path.Combine(be2.Dir, "ClassicDB_b.sql.gz")) && setup2.State == ClassicCardState.Ready);
                Session.Disconnect();

                // ================================================================ a damaged download is refused; the banner is the fallback; no loop
                var be3 = NewPc("pc-damaged", out var setup3);
                sync = NewSync();
                fake.Host(bytesA, verA); fake.Corrupt = true; fake.Gets = 0;
                Session.FeedForTest(AclWelcome("member", "2.17.0", true, false));
                Session.FeedForTest(AclData("dataset.data", fake, false));
                got = await WaitFor(() => setup3.State == ClassicCardState.Failed, 10000); await Idle(setup3);
                await Task.Delay(150); UpdateLayout();
                Check("damaged download: the SHA-256 differs, so it is refused - nothing is installed, no files left, the card says what happened in plain words",
                    got && fake.Gets == 1 && !File.Exists(Path.Combine(be3.Dir, "source.json")) && !Directory.Exists(Path.Combine(be3.Dir, ".lodge-staging")) && setup3.Error.Contains("Couldn't get the Classic data from your Lodge") && setup3.Error.Contains("checksum") && setup3.Error.Contains("public source"), setup3.Error);
                await AtlasView.GoStart(); await Task.Delay(150); UpdateLayout();
                Check("damaged download: the banner is back with the reason and 'Try again'", AtlasView.ClassicBannerShownForTest && AtlasTexts(AtlasView.ClassicBannerForTest).Any(t => t.Contains("Couldn't get the Classic data from your Lodge")) && AtlasView.ClassicBannerGetForTest?.Content.ToString() == "Try again");
                Snapshot(P("classic-lodge-failed-banner.png"));
                Session.FeedForTest(AclData("dataset.data", fake, false)); Session.RequestClassicInfo();
                await Task.Delay(500); await Idle(setup3);
                Check("damaged download: the same release is not tried again in a loop (still one try)", fake.Gets == 1);
                // 'Try again' after a Lodge failure goes to the public source, asking first (the question is answered no here)
                AtlasView.ConfirmOverrideForTest = r => false;
                ClickButton(AtlasView.ClassicBannerGetForTest);
                await WaitFor(() => be3.Queries == 1 && !setup3.Busy, 5000);
                AtlasView.ConfirmOverrideForTest = null;
                Check("damaged download: 'Try again' falls back to the public source (it asks for the size and licence first) instead of the Lodge again", be3.Queries == 1 && fake.Gets == 1 && be3.Downloads == 0);
                // later (after the back-off) a new try with an intact Lodge works
                clock = clock.AddHours(7); fake.Corrupt = false;
                Session.FeedForTest(AclData("dataset.data", fake, false));
                got = await WaitFor(() => setup3.State == ClassicCardState.Ready, 10000); await Idle(setup3);
                Check("damaged download: after the back-off time the Lodge is tried again and it works", got && fake.Gets == 2 && File.Exists(Path.Combine(be3.Dir, "ClassicDB_a.sql.gz")));
                Session.Disconnect();

                // ---- a failure while data is installed leaves it untouched and doesn't alarm
                fake.Host(bytesB, verB); fake.Corrupt = true; fake.Gets = 0;
                be3.Indexed = true;
                sync = NewSync();
                Session.FeedForTest(AclWelcome("member", "2.17.0", true, false));
                Session.FeedForTest(AclData("dataset.data", fake, false));
                await WaitFor(() => fake.Gets == 1, 10000); await Idle(setup3); await Task.Delay(150);
                Check("a newer release that arrives damaged: the data on this PC stays as it was, the card stays Ready, the reason is kept for Settings", setup3.State == ClassicCardState.Ready && File.Exists(Path.Combine(be3.Dir, "ClassicDB_a.sql.gz")) && !File.Exists(Path.Combine(be3.Dir, "ClassicDB_b.sql.gz")) && sync.Problem.Contains("checksum"), sync.Problem);
                Session.Disconnect();
                fake.Corrupt = false;

                // ---- the Lodge is busy (429): a plain reason, not a loop
                var be4 = NewPc("pc-busy", out var setup4);
                sync = NewSync();
                fake.Host(bytesA, verA); fake.ForceStatus = 429; fake.Gets = 0;
                Session.FeedForTest(AclWelcome("member", "2.17.0", true, false));
                Session.FeedForTest(AclData("dataset.data", fake, false));
                await WaitFor(() => setup4.State == ClassicCardState.Failed, 10000); await Idle(setup4);
                Check("the Lodge answers 'too many requests': the reason is the Lodge's own words", setup4.State == ClassicCardState.Failed && setup4.Error.Contains("try again in a while"), setup4.Error);
                fake.ForceStatus = 0;
                Session.Disconnect();

                // ================================================================ the switch in Settings
                var be5 = NewPc("pc-switch", out var setup5);
                sync = NewSync();
                settings.ClassicFromLodgeOff = true; fake.Gets = 0;
                Session.FeedForTest(AclWelcome("member", "2.17.0", true, false));
                Session.FeedForTest(AclData("dataset.data", fake, false));
                await Task.Delay(500);
                ShowTab("settings"); SettingsPage.Show("lodge"); SettingsPage.ClassicRefreshForTest(); await Task.Delay(100);
                Check("switch off: nothing is fetched; Settings says the switch is off and where to get it", fake.Gets == 0 && !File.Exists(Path.Combine(be5.Dir, "source.json")) && !SettingsPage.ClassicAutoForTest && SettingsPage.ClassicStatusForTest.Contains("Automatic download is off"), SettingsPage.ClassicStatusForTest);
                ShowTab("atlas"); await AtlasView.GoStart(); await Task.Delay(150); UpdateLayout();
                Check("switch off: the banner says the Lodge has it and its button comes from the Lodge", AtlasView.ClassicBannerShownForTest && AtlasTexts(AtlasView.ClassicBannerForTest).Any(t => t.Contains("Your Lodge has it")));
                ClickButton(AtlasView.ClassicBannerGetForTest);
                got = await WaitFor(() => setup5.State == ClassicCardState.Ready, 10000); await Idle(setup5);
                Check("switch off: the person's own click on 'Get Classic data' fetches it from the Lodge (not from the internet)", got && fake.Gets == 1 && be5.Queries == 0 && be5.Downloads == 0);
                // switching it on in Settings fetches what is missing / newer
                be5.Delete(); setup5.Refresh();
                ShowTab("settings"); SettingsPage.Show("lodge");
                SettingsPage.ClassicAutoSwitchForTest(true);
                got = await WaitFor(() => setup5.State == ClassicCardState.Ready, 10000); await Idle(setup5);
                Check("switch on again: this PC gets the Lodge's data at once and the setting is saved on", got && !settings.ClassicFromLodgeOff && fake.Gets == 2);
                SettingsPage.ClassicAutoSwitchForTest(false);
                Check("switch: turning it off is saved", settings.ClassicFromLodgeOff);
                settings.ClassicFromLodgeOff = false;
                Session.Disconnect();

                // ================================================================ 'Remove from this PC' is respected
                var be6 = NewPc("pc-remove", out var setup6);
                sync = NewSync();
                fake.Host(bytesA, verA); fake.Gets = 0;
                Session.FeedForTest(AclWelcome("member", "2.17.0", true, false));
                Session.FeedForTest(AclData("dataset.data", fake, false));
                await WaitFor(() => setup6.State == ClassicCardState.Ready, 10000); await Idle(setup6);
                ShowTab("atlas"); await AtlasView.GoStart(); await Task.Delay(100);
                AtlasView.ConfirmRemoveOverrideForTest = () => true;
                await AtlasView.RemoveClassic();
                AtlasView.ConfirmRemoveOverrideForTest = null;
                await Task.Delay(200);
                Check("remove: the data and the index are gone and the person's choice is remembered", setup6.State == ClassicCardState.NotDownloaded && !File.Exists(Path.Combine(be6.Dir, "source.json")) && settings.ClassicLodgePaused);
                Session.Disconnect();
                Session.FeedForTest(AclWelcome("member", "2.17.0", true, false));
                Session.FeedForTest(AclData("dataset.data", fake, false));
                await Task.Delay(500);
                Check("remove: after a reconnect it is NOT fetched again behind their back", fake.Gets == 1 && setup6.State == ClassicCardState.NotDownloaded);
                ShowTab("settings"); SettingsPage.Show("lodge"); SettingsPage.ClassicRefreshForTest(); await Task.Delay(100);
                Check("remove: Settings says so", SettingsPage.ClassicStatusForTest.Contains("You removed it from this PC"), SettingsPage.ClassicStatusForTest);
                ShowTab("atlas"); await AtlasView.GoStart(); await Task.Delay(150); UpdateLayout();
                ClickButton(AtlasView.ClassicBannerGetForTest);
                got = await WaitFor(() => setup6.State == ClassicCardState.Ready, 10000); await Idle(setup6);
                Check("remove: asking again on the Atlas page gets it from the Lodge and clears the choice", got && fake.Gets == 2 && !settings.ClassicLodgePaused);
                Session.Disconnect();

                // ================================================================ owner / officers: share
                // (1) the Lodge holds nothing, this PC has data -> the button in Settings
                foreach (var role in new[] { "owner", "officer" })
                {
                    fake.Bundle = null; fake.Version = ""; fake.Sha = ""; fake.Puts = 0; fake.Gets = 0; fake.Role = role;
                    var be7 = NewPc("pc-" + role, out var setup7);
                    AclWriteSet(be7.Dir, "ClassicDB_manual.sql.gz", "2024-03-01T10:00:00Z", "manual1"); be7.Indexed = true; setup7.Refresh();
                    sync = NewSync();
                    sent.Clear();
                    Session.FeedForTest(AclWelcome(role, "2.17.0", true, true));
                    Session.FeedForTest("{\"t\":\"dataset.data\",\"name\":\"classic\",\"available\":false,\"canManage\":true,\"maxMb\":40}");
                    ShowTab("settings"); SettingsPage.Show("lodge"); SettingsPage.ClassicScrollForTest(); SettingsPage.ClassicRefreshForTest(); await Task.Delay(150); UpdateLayout();
                    Check(role + ": Settings > Lodge shows 'Classic data for the Lodge' with the share button, and says the Lodge doesn't host it yet",
                        SettingsPage.ClassicLodgeRowVisibleForTest && SettingsPage.ClassicShareVisibleForTest && !SettingsPage.ClassicUnshareVisibleForTest && SettingsPage.ClassicStatusForTest.Contains("doesn't host the Classic data yet") && fake.Gets == 0, SettingsPage.ClassicStatusForTest);
                    if (role == "owner") Snapshot(P("classic-lodge-settings-owner.png"));
                    Check(role + ": nothing is uploaded without a click", fake.Puts == 0 && !sync.Sharing);
                    var shareTask = SettingsPage.ClassicShareClickForTest();
                    await shareTask;
                    Check(role + ": the click uploads the bundle once: the original files in one zip with the release as its version", fake.Puts == 1 && fake.Version == "2024-03-01T10:00:00Z|ClassicDB_manual.sql.gz|manual1" && fake.Bundle != null && FakeDatasetServer.Hex(fake.Bundle) == fake.Sha && sync.ShareProblem.Length == 0);
                    using (var z = new ZipArchive(new MemoryStream(fake.Bundle), ZipArchiveMode.Read))
                        Check(role + ": the uploaded bundle holds the dump, the licence files, source.json and the updates", z.Entries.Select(e => e.FullName).Contains("ClassicDB_manual.sql.gz") && z.Entries.Select(e => e.FullName).Contains("LICENSE.md") && z.Entries.Select(e => e.FullName).Contains("COPYRIGHT.md") && z.Entries.Select(e => e.FullName).Contains("AUTHORS") && z.Entries.Select(e => e.FullName).Contains("source.json") && z.Entries.Count == 7);
                    Check(role + ": the temporary bundle is cleaned up", !Directory.Exists(be7.Dir.TrimEnd('\\', '/') + ".share"));
                    Check(role + ": the Lodge's answer is asked for again after an upload", sent.Any(o => o.Str("t") == "dataset.info"));
                    Session.FeedForTest(AclData("dataset.changed", fake, true));
                    SettingsPage.ClassicRefreshForTest(); await Task.Delay(100);
                    Check(role + ": afterwards the Lodge hosts it: the share button is gone, 'Stop hosting' is there, the line names the release", !SettingsPage.ClassicShareVisibleForTest && SettingsPage.ClassicUnshareVisibleForTest && SettingsPage.ClassicStatusForTest.Contains("Your Lodge hosts the Classic data") && SettingsPage.ClassicStatusForTest.Contains("ClassicDB_manual (1 Mar 2024)"), SettingsPage.ClassicStatusForTest);
                    Check(role + ": the owner's own PC does not download what it just shared", fake.Gets == 0);
                    Session.Disconnect();
                }

                // (2) the Lodge holds an OLDER release: the button says "newer"; the same or a newer one: no button
                {
                    fake.Role = "officer"; fake.Puts = 0; fake.Gets = 0;
                    fake.Host(bytesA, verA);
                    var be8 = NewPc("pc-newer", out var setup8);
                    AclWriteSet(be8.Dir, "ClassicDB_b.sql.gz", "2024-06-01T10:00:00Z", "bbb222"); be8.Indexed = true; setup8.Refresh();
                    sync = NewSync();
                    Session.FeedForTest(AclWelcome("officer", "2.17.0", true, true));
                    Session.FeedForTest(AclData("dataset.data", fake, true));
                    ShowTab("settings"); SettingsPage.Show("lodge"); SettingsPage.ClassicRefreshForTest(); await Task.Delay(150);
                    Check("officer with newer data than the Lodge's: the share button is offered (newer release), no automatic download of the older one", SettingsPage.ClassicShareVisibleForTest && fake.Gets == 0, SettingsPage.ClassicStatusForTest);
                    fake.Host(bytesB, verB);
                    Session.FeedForTest(AclData("dataset.changed", fake, true));
                    SettingsPage.ClassicRefreshForTest(); await Task.Delay(100);
                    Check("officer with the same release as the Lodge: no share button", !SettingsPage.ClassicShareVisibleForTest && SettingsPage.ClassicUnshareVisibleForTest);
                    // 'Stop hosting' asks first, then asks the server to forget it
                    sent.Clear();
                    Dialog.TestHook = c => false;
                    SettingsPage.ClassicUnshareClickForTest();
                    Check("officer: 'Stop hosting' asks first - answered no, nothing is sent", !sent.Any(o => o.Str("t") == "dataset.delete"));
                    Dialog.TestHook = c => true;
                    SettingsPage.ClassicUnshareClickForTest();
                    Dialog.TestHook = hadHook;
                    Check("officer: ... answered yes, dataset.delete for 'classic' goes to the Lodge", sent.Count(o => o.Str("t") == "dataset.delete" && o.Str("name") == "classic") == 1);
                    Session.FeedForTest("{\"t\":\"dataset.changed\",\"action\":\"delete\",\"name\":\"classic\",\"available\":false}");
                    SettingsPage.ClassicRefreshForTest(); await Task.Delay(100);
                    Check("officer: when the Lodge says it forgot it, the share button is back", SettingsPage.ClassicShareVisibleForTest && !SettingsPage.ClassicUnshareVisibleForTest && Session.ClassicHosted == null);
                    Session.Disconnect();
                }

                // (3) a refusal at the Lodge is told in plain words
                {
                    fake.Role = "member"; fake.Bundle = null; fake.Version = ""; fake.Sha = ""; fake.Puts = 0;
                    var be9 = NewPc("pc-refused", out var setup9);
                    AclWriteSet(be9.Dir, "ClassicDB_manual.sql.gz", "2024-03-01T10:00:00Z", "manual1"); be9.Indexed = true; setup9.Refresh();
                    sync = NewSync();
                    Session.FeedForTest(AclWelcome("officer", "2.17.0", true, true));
                    Session.FeedForTest("{\"t\":\"dataset.data\",\"name\":\"classic\",\"available\":false,\"canManage\":true,\"maxMb\":40}");
                    var okShare = await sync.ShareAsync();
                    Check("a refused upload: false, nothing hosted, the reason in the Lodge's words, the files on this PC untouched", !okShare && fake.Bundle == null && sync.ShareProblem.Contains("Only officers") && File.Exists(Path.Combine(be9.Dir, "ClassicDB_manual.sql.gz")) && setup9.State == ClassicCardState.Ready, sync.ShareProblem);
                    Session.Disconnect();
                }

                // (4) the quiet offer right after the owner's own download
                foreach (var answer in new[] { "no", "yes" })
                {
                    fake.Role = "owner"; fake.Bundle = null; fake.Version = ""; fake.Sha = ""; fake.Puts = 0; fake.Gets = 0;
                    settings.ClassicShareOffered = null;
                    var be10 = NewPc("pc-offer-" + answer, out var setup10);
                    sync = NewSync();
                    Session.FeedForTest(AclWelcome("owner", "2.17.0", true, true));
                    Session.FeedForTest("{\"t\":\"dataset.data\",\"name\":\"classic\",\"available\":false,\"canManage\":true,\"maxMb\":40}");
                    ShowTab("atlas"); await AtlasView.GoStart(); await Task.Delay(150); UpdateLayout();
                    Check("offer (" + answer + "): before any download the owner sees only the normal banner and no offer", AtlasView.ClassicBannerShownForTest && !AtlasView.ClassicOfferShownForTest && !sync.ShareOfferPending);
                    AtlasView.ConfirmOverrideForTest = r => true;
                    ClickButton(AtlasView.ClassicBannerGetForTest);
                    got = await WaitFor(() => setup10.State == ClassicCardState.Ready, 10000); await Idle(setup10);
                    AtlasView.ConfirmOverrideForTest = null;
                    await Task.Delay(200); UpdateLayout();
                    Check("offer (" + answer + "): their own public download is not replaced by the Lodge (nothing hosted: the banner's button went to the public source after asking)", got && be10.Queries == 1 && be10.Downloads == 1 && fake.Gets == 0);
                    Check("offer (" + answer + "): once it is ready a quiet strip asks 'Share the Classic data with your Lodge so members get it automatically?' with Yes and Not now; nothing is sent yet",
                        AtlasView.ClassicOfferShownForTest && AtlasView.ClassicOfferTextForTest.StartsWith("Share the Classic data with your Lodge so members get it automatically?") && AtlasView.ClassicOfferYesForTest != null && AtlasView.ClassicOfferNoForTest != null && fake.Puts == 0 && !AtlasView.ClassicBannerShownForTest, AtlasView.ClassicOfferTextForTest);
                    if (answer == "no")
                    {
                        Snapshot(P("classic-lodge-offer.png"));
                        ClickButton(AtlasView.ClassicOfferNoForTest);
                        await Task.Delay(150);
                        Check("offer (no): 'Not now' hides the strip, uploads nothing and remembers this release was asked about", !AtlasView.ClassicOfferShownForTest && fake.Puts == 0 && settings.ClassicShareOffered == ClassicBundle.LocalVersion(be10.Dir) && !sync.ShareOfferPending);
                        // downloading the same release again does not ask a second time
                        setup10.Delete(); setup10.Refresh();
                        AtlasView.ConfirmOverrideForTest = r => true;
                        await setup10.StartAsync();
                        AtlasView.ConfirmOverrideForTest = null;
                        await Task.Delay(150);
                        Check("offer (no): the same release is not offered again", setup10.State == ClassicCardState.Ready && !sync.ShareOfferPending && !AtlasView.ClassicOfferShownForTest);
                    }
                    else
                    {
                        ClickButton(AtlasView.ClassicOfferYesForTest);
                        await WaitFor(() => fake.Puts == 1 || sync.ShareProblem.Length > 0, 10000); await Idle(setup10); await Task.Delay(150);
                        Check("offer (yes): the bundle is uploaded unchanged (one PUT, version = this release) and the strip goes away", fake.Puts == 1 && fake.Version.StartsWith("2024-03-01") && !AtlasView.ClassicOfferShownForTest && !sync.ShareOfferPending && settings.ClassicShareOffered == fake.Version);
                    }
                    Session.Disconnect();
                }

                // (5) a member who downloads from the public source is never offered anything
                {
                    fake.Role = "member"; fake.Bundle = null; fake.Puts = 0;
                    settings.ClassicShareOffered = null;
                    var be11 = NewPc("pc-memberoffer", out var setup11);
                    sync = NewSync();
                    Session.FeedForTest(AclWelcome("member", "2.17.0", true, false));
                    Session.FeedForTest("{\"t\":\"dataset.data\",\"name\":\"classic\",\"available\":false,\"canManage\":false,\"maxMb\":40}");
                    AtlasView.ConfirmOverrideForTest = r => true;
                    await setup11.StartAsync();
                    AtlasView.ConfirmOverrideForTest = null;
                    await Task.Delay(150);
                    Check("member who downloads it themselves: no offer, no share button, nothing uploaded", setup11.State == ClassicCardState.Ready && !sync.ShareOfferPending && !sync.ShareAvailable && !AtlasView.ClassicOfferShownForTest && fake.Puts == 0);
                    var refusedLocally = await sync.ShareAsync();
                    Check("member: even a forced share attempt is refused locally", !refusedLocally && fake.Puts == 0);
                    Session.Disconnect();
                }

                // ================================================================ the Atlas page notes use the same door
                {
                    var be12 = NewPc("pc-notes", out var setup12);
                    sync = NewSync();
                    fake.Host(bytesA, verA); fake.Gets = 0; settings.ClassicFromLodgeOff = true;
                    Session.FeedForTest(AclWelcome("member", "2.17.0", true, false));
                    Session.FeedForTest(AclData("dataset.data", fake, false));
                    await Task.Delay(300);
                    await AtlasView.Open(AtlasNav.DataId); UpdateLayout();
                    Check("the card's own button also comes from the Lodge when the Lodge has it", AtlasView.ClassicCardForTest != null && sync.CanProvide);
                    settings.ClassicFromLodgeOff = false;
                    Session.Disconnect();
                }
            }
            catch (Exception ex) { ok = false; notes.Add("FAIL exception: " + ex); }
            finally
            {
                try { Session.Disconnect(); } catch { }
                Session.SentForTest = null; Session.Clock = hadClock;
                Session.ClassicClientForTest(null);
                Dialog.TestHook = hadHook;
                AtlasView.ConfirmOverrideForTest = null; AtlasView.ConfirmRemoveOverrideForTest = null;
                mine?.Dispose();
                if (realSync != null) { realSync.Suspended = false; AtlasView.UseLodgeSync(realSync); SettingsPage.UseClassicSync(realSync); }
                if (hadSetup != null) AtlasView.UseClassicSetup(hadSetup);
                settings.ClassicFromLodgeOff = hadOff; settings.ClassicLodgePaused = hadPaused; settings.ClassicShareOffered = hadOffered; settings.LodgeUrl = hadUrl;
                fake?.Stop();
                ElansAddonHub.AtlasPage.ResetClassicBannerForTest();
                ShowTab("addons");
            }
            notes.Insert(0, "atlasclassiclodge (2.49.0): " + (ok ? "all ok" : "FAILURES"));
            return string.Join("\r\n", notes);
        }
    }
}
