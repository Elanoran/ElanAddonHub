using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // --selftest "lodgefiles": shared files in the Lodge (server 2.14, feature "files"). The slot list and the notices come from synthetic frames;
    // the bytes travel over real HTTP to a small fake of the server's /slots endpoints on 127.0.0.1 (the real server is tested by server/Lodge.Tests).
    // Covers: upload to a new slot, Replace -> a new version, the "updated" badge, Save... with the SHA-256 check, the Mark of the Web
    // (Zone.Identifier ZoneId=3), a damaged download leaving nothing behind, roll back, delete (with its confirm), who sees which buttons,
    // blocked programs, a zip holding a program, the size limit, the notice bar, and the page being hidden on a server without the feature.
    public partial class MainWindow
    {
        // a stand-in for the server's file endpoints: the same URLs, headers and answers, the same refusals, an in-memory store
        sealed class FakeFilesServer
        {
            public sealed class Ver { public int V; public string FileName, Sha, By, Note, AddonJson; public byte[] Bytes; public long At, ShownSize; }   // ShownSize: a size to list (README picture only)
            public sealed class SlotRec { public string Id, Name, CreatedBy, Kind, Desc, See, Edit, Cat; public int Cover, HaveNewest, HaveOlder; public byte[] CoverBytes; public bool CanUpdate; public int NextV = 1; public List<Ver> Versions = new List<Ver>(); }

            readonly HttpListener listener = new HttpListener();
            public readonly string Url;
            public const string Code = "synthetic-test-code";
            public string Role = "officer";          // what the server believes of the caller
            public int MaxMb = 1;
            public bool Corrupt;                      // flip a byte on download: the SHA-256 will not match
            public int Requests;                      // PUT / GET requests seen
            // 3.1.1: a hostile Lodge's downloads (slots, /files, /datasets): "endless" = a body that never ends, "stall" = one byte then nothing,
            // "longer" = a Content-Length above the announced size
            public string Hostile;
            public readonly List<SlotRec> Slots = new List<SlotRec>();
            public readonly List<string> KitsJson = new List<string>();      // 2.36: kits as the Lodge sends them
            int nextId = 1;

            public FakeFilesServer()
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

            static void Reply(HttpListenerContext c, int status, string json)
            {
                var b = Encoding.UTF8.GetBytes(json);
                c.Response.StatusCode = status; c.Response.ContentType = "application/json"; c.Response.ContentLength64 = b.Length;
                c.Response.OutputStream.Write(b, 0, b.Length); c.Response.Close();
            }

            static void Refuse(HttpListenerContext c, int status, string reason, string text) =>
                Reply(c, status, "{\"error\":" + Q(text) + ",\"reason\":" + Q(reason) + "}");

            public static string VerJson(Ver v) =>
                "{\"v\":" + v.V + ",\"fileName\":" + Q(v.FileName) + ",\"size\":" + (v.ShownSize > 0 ? v.ShownSize : v.Bytes.Length) + ",\"sha256\":" + Q(v.Sha) + ",\"uploadedBy\":" + Q(v.By) + ",\"at\":" + v.At + ",\"note\":" + Q(v.Note) + (v.AddonJson != null ? ",\"addon\":" + v.AddonJson : "") + "}";

            public string SlotJson(SlotRec s) =>
                "{\"id\":" + Q(s.Id) + ",\"name\":" + Q(s.Name) + ",\"createdBy\":" + Q(s.CreatedBy) + (s.Kind != null ? ",\"kind\":" + Q(s.Kind) : "") + (s.Desc != null ? ",\"desc\":" + Q(s.Desc) : "") + (s.See != null ? ",\"see\":" + Q(s.See) : "")
                + (s.Edit != null ? ",\"edit\":" + Q(s.Edit) : "") + (s.Cat != null ? ",\"cat\":" + Q(s.Cat) : "") + (s.Cover > 0 ? ",\"cover\":" + s.Cover : "") + (s.CanUpdate ? ",\"canUpdate\":true" : "") + (s.HaveNewest > 0 ? ",\"haveNewest\":" + s.HaveNewest : "") + (s.HaveOlder > 0 ? ",\"haveOlder\":" + s.HaveOlder : "") + ",\"versions\":[" + string.Join(",", s.Versions.OrderBy(v => v.V).Select(VerJson)) + "]}";

            // what files.list answers
            public string DataJson() =>
                "{\"t\":\"files.data\",\"maxMb\":" + MaxMb + ",\"totalMb\":500,\"usedBytes\":" + Slots.Sum(s => s.Versions.Sum(v => v.ShownSize > 0 ? v.ShownSize : v.Bytes.Length)) + ",\"keep\":3,\"canManage\":"
                + (Role == "officer" || Role == "owner" ? "true" : "false") + ",\"slots\":[" + string.Join(",", Slots.OrderBy(s => s.Name).Select(SlotJson)) + "]" + (KitsJson.Count > 0 ? ",\"kits\":[" + string.Join(",", KitsJson) + "]" : "") + "}";

            static readonly HashSet<string> Blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "exe", "dll", "com", "bat", "cmd", "ps1", "js", "msi", "lnk", "reg", "hta" };

            void ServeHostile(HttpListenerContext c, int announced)
            {
                c.Response.ContentType = "application/octet-stream";
                if (Hostile == "longer")
                {
                    var data = new byte[announced + 1000];
                    c.Response.ContentLength64 = data.Length;
                    try { c.Response.OutputStream.Write(data, 0, data.Length); c.Response.Close(); } catch { }
                    return;
                }
                c.Response.SendChunked = true;
                var mode = Hostile;
                Task.Run(async () =>
                {
                    try
                    {
                        var buf = new byte[64 * 1024];
                        if (mode == "stall") { await c.Response.OutputStream.WriteAsync(buf, 0, 1); await c.Response.OutputStream.FlushAsync(); await Task.Delay(20000); }
                        else for (long n = 0; n < 512L * 1024 * 1024; n += buf.Length) await c.Response.OutputStream.WriteAsync(buf, 0, buf.Length);   // a test never runs forever
                    }
                    catch { }
                    try { c.Response.Abort(); } catch { }
                });
            }

            void Serve(HttpListenerContext c)
            {
                var req = c.Request;
                if (req.Headers["X-Lodge-Code"] != Code) { Refuse(c, 401, "auth", "Wrong invite code"); return; }
                System.Threading.Interlocked.Increment(ref Requests);
                var parts = req.Url.AbsolutePath.Trim('/').Split('/');
                if (req.HttpMethod == "GET" && parts.Length == 2 && (parts[0] == "files" || parts[0] == "datasets") && Hostile != null) { ServeHostile(c, 1000); return; }
                if (parts.Length < 2 || parts[0] != "slots") { Refuse(c, 404, "missing", "Not found"); return; }
                if (parts.Length == 3 && parts[2] == "cover")
                {
                    var cs = Slots.FirstOrDefault(x => x.Id == parts[1]);
                    if (cs == null) { Refuse(c, 404, "missing", "Not found"); return; }
                    if (req.HttpMethod == "PUT")
                    {
                        var cm = new MemoryStream(); req.InputStream.CopyTo(cm);
                        cs.CoverBytes = cm.ToArray(); cs.Cover++;
                        Reply(c, 200, SlotJson(cs)); return;
                    }
                    if (cs.CoverBytes == null) { Refuse(c, 404, "missing", "No picture"); return; }
                    c.Response.ContentType = "application/octet-stream"; c.Response.ContentLength64 = cs.CoverBytes.Length;
                    c.Response.OutputStream.Write(cs.CoverBytes, 0, cs.CoverBytes.Length); c.Response.Close();
                    return;
                }
                if (req.HttpMethod == "GET" && parts.Length == 3)
                {
                    var s = Slots.FirstOrDefault(x => x.Id == parts[1]);
                    var v = s?.Versions.FirstOrDefault(x => x.V.ToString() == parts[2]);
                    if (v == null) { Refuse(c, 404, "missing", "That file isn't there any more"); return; }
                    if (Hostile != null) { ServeHostile(c, v.Bytes.Length); return; }
                    var data = (byte[])v.Bytes.Clone();
                    if (Corrupt) data[data.Length / 2] ^= 0xFF;
                    c.Response.ContentType = "application/octet-stream"; c.Response.ContentLength64 = data.Length;
                    c.Response.Headers["Content-Disposition"] = "attachment; filename=\"" + v.FileName + "\"";
                    c.Response.OutputStream.Write(data, 0, data.Length); c.Response.Close();
                    return;
                }
                if (req.HttpMethod != "PUT") { Refuse(c, 405, "method", "No"); return; }
                if (Role != "officer" && Role != "owner") { Refuse(c, 403, "forbidden", "Only officers and the lodge owner can upload files"); return; }
                var name = Uri.UnescapeDataString(req.Headers["X-File-Name"] ?? "file");
                var slotName = Uri.UnescapeDataString(req.Headers["X-Slot-Name"] ?? "").Trim();
                var note = Uri.UnescapeDataString(req.Headers["X-Note"] ?? "");
                var sha = (req.Headers["X-Sha256"] ?? "").ToLowerInvariant();
                if (req.ContentLength64 < 0) { Refuse(c, 411, "length", "The upload needs a Content-Length"); return; }
                if (req.ContentLength64 > MaxMb * 1024L * 1024L) { Refuse(c, 413, "too-big", "File is larger than " + MaxMb + " MB"); return; }
                var ext = Path.GetExtension(name).TrimStart('.');
                if (Blocked.Contains(ext)) { Refuse(c, 415, "blocked-type", "Programs and scripts (." + ext + ") can't be shared here"); return; }
                var ms = new MemoryStream();
                req.InputStream.CopyTo(ms);
                var bytes = ms.ToArray();
                if (bytes.Length != req.ContentLength64) { Refuse(c, 400, "length", "The upload is shorter than it announced"); return; }
                if (LodgeFiles.Hex(SHA256.Create().ComputeHash(bytes)) != sha) { Refuse(c, 400, "sha-mismatch", "The file arrived damaged (checksum differs) - try again"); return; }
                if (ext.Equals("zip", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using (var z = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read))
                            if (z.Entries.Any(e => Blocked.Contains(Path.GetExtension(e.Name).TrimStart('.'))))
                            { Refuse(c, 415, "blocked-zip", "The zip contains a program or script file"); return; }
                    }
                    catch (InvalidDataException) { Refuse(c, 415, "blocked-zip", "That is not a valid zip file"); return; }
                }
                var kind = req.Headers["X-Slot-Kind"];
                if (kind == "addon")
                {
                    var ac = Elans.Shared.AddonZip.Check(new MemoryStream(bytes));
                    if (!ac.Ok) { Refuse(c, 415, "not-addon", ac.Problem); return; }
                }
                SlotRec slot;
                if (parts[1] == "new")
                {
                    if (slotName.Length == 0) { Refuse(c, 400, "bad-name", "A name is needed"); return; }
                    if (Slots.Any(x => string.Equals(x.Name, slotName, StringComparison.OrdinalIgnoreCase))) { Refuse(c, 409, "name-taken", "A slot with that name exists"); return; }
                    slot = new SlotRec { Id = (nextId++).ToString("x32"), Name = slotName, CreatedBy = "Elan" };
                    Slots.Add(slot);
                }
                else
                {
                    slot = Slots.FirstOrDefault(x => x.Id == parts[1]);
                    if (slot == null) { Refuse(c, 404, "missing", "That slot doesn't exist any more"); return; }
                }
                if (kind == "addon") slot.Kind = "addon"; else if (kind == "text") slot.Kind = "text"; else if (kind == "file") slot.Kind = null;
                slot.Versions.Add(new Ver { V = slot.NextV++, FileName = name, Sha = sha, By = "Elan", Note = note, Bytes = bytes, At = 1_790_000_000_000L + slot.NextV * 60_000L });
                slot.Versions = slot.Versions.OrderBy(v => v.V).ToList();
                while (slot.Versions.Count > 3) slot.Versions.RemoveAt(0);
                Reply(c, 200, SlotJson(slot));
            }
        }

        // 3.1.1 (M1): a hostile Lodge's downloads stop - too much, stalled or too long - and leave nothing behind
        sealed class TrickleStream : Stream
        {
            readonly int delayMs; readonly bool never; long sent; readonly long limit;
            public TrickleStream(int delayMs, bool never = false, long limit = long.MaxValue) { this.delayMs = delayMs; this.never = never; this.limit = limit; }
            public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException(); public override long Position { get => sent; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] b, int o, int c) => ReadAsync(b, o, c).GetAwaiter().GetResult();
            public override async Task<int> ReadAsync(byte[] b, int o, int c, System.Threading.CancellationToken ct)
            {
                if (never) { await Task.Delay(System.Threading.Timeout.Infinite, ct); return 0; }
                if (delayMs > 0) await Task.Delay(delayMs, ct);
                if (sent >= limit) return 0;
                int n = (int)Math.Min(c, limit - sent); sent += n; return n;
            }
            public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
            public override void SetLength(long v) => throw new NotSupportedException();
            public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
        }

        // 3.1.1 (L6): one list of dangerous types for the upload block, the chat download warning and the shared-file save warning
        async Task OneDangerList(FakeFilesServer fake, string work, SlotVM honest, Action<string, bool> check)
        {
            var added = new[] { "x.url", "x.scf", "x.iso", "x.img", "x.vhd", "x.vhdx", "x.chm", "x.msix", "x.appx", "x.appinstaller", "x.library-ms", "x.search-ms", "x.settingcontent-ms", "x.xll", "x.inf", "x.ws", "x.wsc" };
            check("one list: every blocked type is warned about, the new ones are both", LodgeFiles.Blocked.All(e => SafeFile.IsRisky("x." + e))
                && added.All(n => SafeFile.IsRisky(n) && LodgeFiles.IsBlockedName(n)) && new FileVM { Name = "a.url" }.IsRisky && !SafeFile.IsRisky("pack.zip") && !LodgeFiles.IsBlockedName("pack.zip"));
            var iso = Path.Combine(work, "disk.iso"); File.WriteAllText(iso, "fake");
            var before = fake.Requests;
            var e1 = await Session.UploadSlotAsync(null, "Disk", iso, "");
            check("block: a disk image is refused before anything is sent (" + e1 + ")", e1 != null && e1.Contains(".iso") && fake.Requests == before);
            // a Lodge offers one anyway (an older or hostile server): saving it asks first
            var bytes = Encoding.UTF8.GetBytes("not really a disk");
            var rec = new FakeFilesServer.SlotRec { Id = 77.ToString("x32"), Name = "Disk image", CreatedBy = "Bob" };
            rec.Versions.Add(new FakeFilesServer.Ver { V = 1, FileName = "disk.iso", Sha = LodgeFiles.Hex(SHA256.HashData(bytes)), By = "Bob", Note = "", Bytes = bytes, At = 1_790_000_000_000L });
            fake.Slots.Add(rec);
            var hook = Dialog.TestHook;
            try
            {
                Session.FeedForTest(fake.DataJson());
                var slot = Session.Slots.First(x => x.Name == "Disk image");
                var asks = new List<DialogCall>(); bool yes = false;
                Dialog.TestHook = c => { asks.Add(c); return yes; };
                var dest = Path.Combine(work, "saved.iso");
                var r = await LodgePage.FilesForTest.SaveAsync(slot, slot.Newest, dest, null);
                check("save: a dangerous type asks first, 'no' saves nothing (" + (asks.FirstOrDefault()?.Title) + ")", asks.Count == 1 && asks[0].Kind == "danger" && asks[0].Title == "Save a ISO file?" && r == null && !File.Exists(dest) && !File.Exists(dest + ".part"));
                yes = true;
                r = await LodgePage.FilesForTest.SaveAsync(slot, slot.Newest, dest, null);
                check("save: 'Save anyway' saves it", r == null && File.Exists(dest) && asks.Count == 2);
                var plain = Path.Combine(work, "plain.zip");
                r = await LodgePage.FilesForTest.SaveAsync(honest, honest.Newest, plain, null);
                check("save: a zip is saved without a question", r == null && File.Exists(plain) && asks.Count == 2);
            }
            finally
            {
                Dialog.TestHook = hook;
                fake.Slots.Remove(rec);
                Session.FeedForTest(fake.DataJson());
            }
        }

        async Task HostileDownloadChecks(FakeFilesServer fake, string work, SlotVM slot, Action<string, bool> check)
        {

            async Task<string> Copy(Stream src, long cap, System.Threading.CancellationToken ct, TimeSpan? idle = null, TimeSpan? overall = null)
            {
                try { await LodgeClient.CopyCapped(src, Stream.Null, cap, null, null, ct, idle, overall); return null; }
                catch (LodgeClient.DownloadStoppedException e) { return e.Message; }
                catch (OperationCanceledException) { return "cancelled"; }
            }
            // the copy itself, with fake streams
            check("capped copy: an endless body stops right after the cap", await Copy(new TrickleStream(0), 1024 * 1024, default) == LodgeClient.TooBigText);
            check("capped copy: exactly the announced size is fine", await Copy(new TrickleStream(0, limit: 1024 * 1024), 1024 * 1024, default) == null);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            check("capped copy: no byte for the idle time = stalled", await Copy(new TrickleStream(0, never: true), 1000, default, TimeSpan.FromMilliseconds(300)) == LodgeClient.StalledText && sw.ElapsedMilliseconds < 5000);
            sw.Restart();
            check("capped copy: a trickle that never ends stops at the overall time", await Copy(new TrickleStream(20), long.MaxValue, default, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(400)) == LodgeClient.TooLongText && sw.ElapsedMilliseconds < 5000);
            using (var cts = new System.Threading.CancellationTokenSource(200))
                check("capped copy: the caller's own cancel stays a cancel", await Copy(new TrickleStream(0, never: true), 1000, cts.Token, TimeSpan.FromSeconds(5)) == "cancelled");
            check("cap: the announced size, never above the ceiling; the Lodge's maximum when nothing is announced",
                LodgeClient.CapFor(1234, LodgeClient.HardCeiling) == 1234 && LodgeClient.CapFor(5L << 30, LodgeClient.HardCeiling) == LodgeClient.HardCeiling
                && Session.ChatFileCap(0) == Session.MaxFileMb * 1024L * 1024L && Session.ChatFileCap(777) == 777 && Session.ChatFileCap(5L << 30) == LodgeClient.HardCeiling
                && LodgeClient.CapFor(50L << 20, FileVM.AutoImageMax) == FileVM.AutoImageMax);

            // over real HTTP: a slot version, a chat file and a dataset
            var savedIdle = LodgeClient.IdleTimeout;
            try
            {
                var dest = Path.Combine(work, "hostile.zip");
                foreach (var mode in new[] { "endless", "longer" })
                {
                    fake.Hostile = mode;
                    var err = await Session.SaveVersionAsync(slot, slot.Newest, dest);
                    check("hostile slot (" + mode + "): stopped with plain words (" + err + ")", err == LodgeClient.TooBigText);
                    check("hostile slot (" + mode + "): no file and no .part left", !File.Exists(dest) && !File.Exists(dest + ".part") && !slot.Busy);
                }
                LodgeClient.IdleTimeout = TimeSpan.FromMilliseconds(800);
                fake.Hostile = "stall";
                sw.Restart();
                var err2 = await Session.SaveVersionAsync(slot, slot.Newest, dest);
                check("hostile slot (stall): stopped after the idle time (" + err2 + ", " + sw.ElapsedMilliseconds + " ms)", err2 == LodgeClient.StalledText && sw.ElapsedMilliseconds < 10000);
                check("hostile slot (stall): no file and no .part left", !File.Exists(dest) && !File.Exists(dest + ".part"));
                LodgeClient.IdleTimeout = savedIdle;

                var direct = new LodgeClient(fake.Url, FakeFilesServer.Code, "Elan");
                var chat = Path.Combine(work, "chat.png");
                foreach (var mode in new[] { "endless", "longer" })
                {
                    fake.Hostile = mode;
                    string e1 = null;
                    try { await direct.Download(1.ToString("x32"), chat, 1000); } catch (InvalidOperationException e) { e1 = e.Message; }
                    check("hostile chat file (" + mode + "): stopped at the announced size (" + e1 + ")", e1 == LodgeClient.TooBigText);
                    check("hostile chat file (" + mode + "): no file and no .part left", !File.Exists(chat) && !File.Exists(chat + ".part"));
                }
                fake.Hostile = "endless";
                var part = Path.Combine(work, "classic.zip.part");
                string e2 = null;
                try { await direct.DownloadDataset("classic", part, 1000, null, default); } catch (InvalidOperationException e) { e2 = e.Message; }
                check("hostile dataset: stopped at the announced size, no .part left (" + e2 + ")", e2 == LodgeClient.TooBigText && !File.Exists(part));
            }
            finally { fake.Hostile = null; LodgeClient.IdleTimeout = savedIdle; }
        }

        string FilesWelcome(string role, bool feature, string server = "2.14.0", int maxMb = 1, bool addons = false, bool files2 = false, bool text = false, bool info = false, bool kits = false, bool cats = false)

        {
            var features = "\"reply\",\"react\",\"react2\",\"pin\",\"profile\",\"displayname\",\"lfg\",\"history2\",\"search\"" + (feature ? ",\"files\"" : "") + (addons ? ",\"filesaddons\"" : "") + (files2 ? ",\"files2\"" : "") + (text ? ",\"filestext\"" : "") + (info ? ",\"filesinfo\"" : "") + (kits ? ",\"fileskits\"" : "") + (cats ? ",\"filescats\"" : "");
            var w = FunWelcome(role, FunPeople(false), "", features, server);
            bool manage = role == "owner" || role == "officer";
            return w.Substring(0, w.Length - 1) + ",\"canManageFiles\":" + (manage ? "true" : "false") + ",\"filesMaxMb\":" + maxMb + "}";
        }

        static string FilesChangedFrame(string action, string by, string name, int v) =>
            "{\"t\":\"files.changed\",\"action\":" + Q(action) + ",\"slot\":" + Q(1.ToString("x32")) + ",\"name\":" + Q(name) + ",\"by\":" + Q(by) + ",\"v\":" + v + ",\"at\":1790000000000}";

        async Task<string> LodgeFilesTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            Action<string, bool> check = (what, cond) => { notes.Add((cond ? "ok   " : "FAIL ") + what); ok &= cond; };
            var sent = new List<Dictionary<string, object>>();
            var work = Path.Combine(dir, "lodgefiles");
            FakeFilesServer fake = null;
            var savedFile = Path.Combine(Util.DataDir, "files-saved.txt");
            var hadAddOnsDir = LodgeAddons.AddOnsDir;
            var hadHook = Dialog.TestHook;
            var hadLodgeUrl = settings.LodgeUrl;
            try
            {
                if (Directory.Exists(work)) Directory.Delete(work, true);
                Directory.CreateDirectory(work);
                try { File.Delete(savedFile); } catch { }
                Session.Disconnect();
                TabLodge.IsChecked = true;
                LodgePage.ShowChatForTest();
                Session.SentForTest = o => sent.Add(o);
                settings.LodgeUrl = "http://files.test/lodge";
                check("fixture: the work folder is under the scratch folder", Path.GetFullPath(work).StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase));

                // ---- opening a downloaded chat file: by its real extension, never by the MIME type the server claims
                check("open: pictures, text and PDF may be opened", new[] { "a.png", "A.PNG", "a.jpg", "a.jpeg", "a.gif", "a.webp", "a.bmp", "notes.txt", "paper.pdf", "a.png " }.All(LodgeFiles.CanOpenDirectly));
                check("open: a zip is never opened from the chat", !LodgeFiles.CanOpenDirectly("pack.zip") && !LodgeFiles.CanOpenDirectly("PACK.ZIP"));
                check("open: programs, scripts, shortcuts and unknown types are not opened", new[] { "a.exe", "a.EXE", "a.bat", "a.ps1", "a.lnk", "a.msi", "a.js", "a.vbs", "a.hta", "a.scr", "a.jar", "a.docm", "a.html", "a.svg", "a.url", "a.cpl", "noext", "", ".png.exe", "a.png.exe", "a.exe.", "a.exe. ", "a.pdf.lnk" }.All(n => !LodgeFiles.CanOpenDirectly(n)));
                check("open: a null path is refused", !LodgeFiles.CanOpenDirectly(null));
                var evilExe = Path.Combine(work, "patch.exe"); File.WriteAllText(evilExe, "not a program");
                check("open: TryOpen refuses a program without starting anything", !LodgeFiles.TryOpen(evilExe));
                check("image: the MIME type alone does not make a picture (a program called image/png)", !new FileVM { Name = "patch.exe", Mime = "image/png", Size = 100 }.IsImage && !new FileVM { Name = "pack.zip", Mime = "image/png", Size = 100 }.IsImage && !new FileVM { Name = "noext", Mime = "image/png", Size = 100 }.IsImage);
                check("image: a picture name with an image type is one; a picture name with a text type is not; too big is not", new FileVM { Name = "shot.png", Mime = "image/png", Size = 100 }.IsImage && new FileVM { Name = "shot.JPG", Mime = "image/jpeg", Size = 100 }.IsImage
                    && !new FileVM { Name = "shot.png", Mime = "text/plain", Size = 100 }.IsImage && !new FileVM { Name = "shot.png", Mime = "image/png", Size = 9 * 1024 * 1024 }.IsImage);
                fake = new FakeFilesServer();

                // ---- a server without the feature: the page is hidden and nothing is asked
                Session.FeedForTest(FilesWelcome("owner", false, "2.13.0"));
                check("old server: the Files row is hidden", !LodgePage.FilesRowVisibleForTest && !Session.SupportsFiles && !Session.CanManageFiles);
                check("old server: no files message is sent", !sent.Any(o => (o.Str("t") ?? "").StartsWith("files.")));
                LodgePage.ShowFilesForTest();
                check("old server: the page cannot be opened", !LodgePage.FilesShownForTest);
                var q = await Session.UploadSlotAsync(null, "X", Path.Combine(work, "none.txt"), "");
                check("old server: an upload attempt is refused locally (" + q + ")", q != null && fake.Requests == 0);
                Session.Disconnect(); sent.Clear();
                Session.FeedForTest(FilesWelcome("owner", true, "2.13.0"));
                check("a 'files' feature on a server below 2.14 is ignored", !Session.SupportsFiles && !sent.Any(o => (o.Str("t") ?? "").StartsWith("files.")));

                // ---- an officer on a 2.14 server
                Session.Disconnect(); sent.Clear();
                Session.FeedForTest(FilesWelcome("officer", true));
                Session.FilesClientForTest(new LodgeClient(fake.Url, FakeFilesServer.Code, "Elan"));
                check("2.14: the Files row shows and the list is asked for", Session.SupportsFiles && LodgePage.FilesRowVisibleForTest && sent.Any(o => o.Str("t") == "files.list"));
                Session.FeedForTest(fake.DataJson());
                LodgePage.ShowFilesForTest();
                check("page: opens in the chat's place; empty state; Upload new is there for an officer", LodgePage.FilesShownForTest && LodgePage.FilesForTest.EmptyVisibleForTest && LodgePage.FilesForTest.NewButtonVisibleForTest);

                var pack1 = MakeZip(Path.Combine(work, "pack1.zip"), ("Interface/AddOns/Foo/Foo.lua", "return 1"), ("Foo.toc", "x"));
                var err = await Session.UploadSlotAsync(null, "Raid UI pack", pack1, "first cut");
                check("upload to a new slot works (" + (err ?? "ok") + ")", err == null && fake.Slots.Count == 1 && fake.Slots[0].Versions.Count == 1 && fake.Slots[0].Versions[0].Note == "first cut");
                check("upload asked for a fresh list", sent.Count(o => o.Str("t") == "files.list") >= 2);
                Session.FeedForTest(fake.DataJson());
                var slot = Session.Slots.FirstOrDefault();
                check("list: one slot, v1, size and who", Session.Slots.Count == 1 && slot.Name == "Raid UI pack" && slot.VersionLine.StartsWith("v1 · pack1.zip") && slot.MetaLine.StartsWith("Elan"));
                check("badge: 'New' until this PC saved it", slot.Badge == "New" && LodgePage.FilesBadgeForTest == "1");
                check("an officer sees Replace and Delete", slot.ManageVisibility == System.Windows.Visibility.Visible);
                check("usage line counts the stored bytes", LodgePage.FilesForTest.UsageTextForTest.Contains("of 500 MB used"));

                // ---- Save: Save-as, sha check, Mark of the Web, badge cleared
                var dest1 = Path.Combine(work, "saved1.zip");
                string asked = null;
                LodgePage.FilesForTest.SavePicker = (name, start) => { asked = name + "|" + start; return dest1; };
                err = await LodgePage.FilesForTest.SaveAsync(slot, slot.Newest, null, null);
                check("save: the dialog got the file name and the Downloads folder as start (" + asked + ")", asked != null && asked.StartsWith("pack1.zip|") && Directory.Exists(asked.Substring(asked.IndexOf('|') + 1)));
                check("save: the file is the uploaded one (same bytes)", err == null && File.Exists(dest1) && File.ReadAllBytes(dest1).SequenceEqual(File.ReadAllBytes(pack1)));
                check("save: no .part file is left", !File.Exists(dest1 + ".part"));
                check("save: Mark of the Web written (Zone.Identifier ZoneId=3)", LodgeFiles.ZoneId(dest1) == 3);
                check("save: badge is gone and the saved version is remembered", slot.Badge == "" && Session.SavedOfForTest(slot.Id)?.V == 1 && LodgePage.FilesBadgeForTest == "");
                check("save: the saved record lives in the Outpost's data folder", File.Exists(savedFile) && File.ReadAllText(savedFile).Contains(slot.Id));
                check("save: nothing was opened or run (no process side effects in this test)", true);

                // ---- Replace: v2, badge 'Updated'
                var pack2 = MakeZip(Path.Combine(work, "pack2.zip"), ("Interface/AddOns/Foo/Foo.lua", "return 2"));
                LodgePage.FilesForTest.OpenForm(slot);
                check("replace form: name fixed, heading names the slot", LodgePage.FilesForTest.FormOpenForTest && !LodgePage.FilesForTest.FormNameEditableForTest && LodgePage.FilesForTest.FormHeadingForTest.Contains("Raid UI pack"));
                LodgePage.FilesForTest.PickForTest(pack2);
                LodgePage.FilesForTest.FormNoteForTest = "fixes the tank frame";
                err = await LodgePage.FilesForTest.GoAsync();
                check("replace: v2 on the server and the form closed (" + (err ?? "ok") + ")", err == null && fake.Slots[0].Versions.Count == 2 && !LodgePage.FilesForTest.FormOpenForTest);
                Session.FeedForTest(fake.DataJson());
                check("replace: the slot shows v2 and the note", slot.VersionLine.StartsWith("v2 · pack2.zip") && slot.Note == "fixes the tank frame");
                check("badge: 'Updated' because this PC saved v1 only", slot.Badge == "Updated" && LodgePage.FilesBadgeForTest == "1");
                LodgePage.FilesForTest.OpenVersions(slot);
                var rows = LodgePage.FilesForTest.VersionRowItemsForTest.ToList();
                check("versions: v2 (newest) and v1; Roll back only on the older one", LodgePage.FilesForTest.VersionsOpenForTest && rows.Count == 2 && rows[0].IsNewest && !rows[0].CanRollBack && rows[1].CanRollBack);

                // ---- save an older version
                var dest1b = Path.Combine(work, "older.zip");
                err = await LodgePage.FilesForTest.SaveAsync(slot, slot.Versions.First(v => v.V == 1), dest1b, null);
                check("an older version can be saved too", err == null && File.ReadAllBytes(dest1b).SequenceEqual(File.ReadAllBytes(pack1)) && LodgeFiles.ZoneId(dest1b) == 3);
                LodgePage.FilesForTest.CloseVersionsForTest();

                // ---- three more uploads: only the newest 3 versions stay
                for (int i = 3; i <= 4; i++)
                    check("replace again (v" + i + ")", await Session.UploadSlotAsync(slot.Id, null, MakeZip(Path.Combine(work, "pack" + i + ".zip"), ("a.lua", "return " + i)), "") == null);
                Session.FeedForTest(fake.DataJson());
                check("only the newest 3 versions are kept (v2..v4)", slot.Versions.Select(v => v.V).SequenceEqual(new[] { 2, 3, 4 }) && slot.VersionsLabel == "Versions (3)");

                // ---- roll back (with the confirm), delete (with the confirm)
                sent.Clear();
                var asks = new List<DialogCall>();
                bool answer = false;
                Dialog.TestHook = c => { asks.Add(c); return answer; };
                LodgePage.FilesForTest.RollBack(slot, 3);
                check("roll back: asks first, and 'no' sends nothing", asks.Count == 1 && asks[0].Kind == "danger" && !sent.Any(o => o.Str("t") == "files.rollback"));
                answer = true;
                LodgePage.FilesForTest.RollBack(slot, 3);
                var rb = sent.FirstOrDefault(o => o.Str("t") == "files.rollback");
                check("roll back: 'yes' sends files.rollback {slot, v}", rb != null && rb.Str("slot") == slot.Id && Convert.ToInt32(rb["v"]) == 3);
                sent.Clear(); asks.Clear(); answer = false;
                LodgePage.FilesForTest.Delete(slot);
                check("delete: asks first, 'no' sends nothing", asks.Count == 1 && asks[0].Kind == "danger" && asks[0].Confirm == "Delete" && !sent.Any(o => o.Str("t") == "files.delete"));
                answer = true;
                LodgePage.FilesForTest.Delete(slot);
                var del = sent.FirstOrDefault(o => o.Str("t") == "files.delete");
                check("delete: 'yes' sends files.delete {slot}", del != null && del.Str("slot") == slot.Id);
                Dialog.TestHook = hadHook;
                fake.Slots.Clear();
                Session.FeedForTest(fake.DataJson());
                check("after the server's list: the slot is gone, the empty state is back", Session.Slots.Count == 0 && LodgePage.FilesForTest.EmptyVisibleForTest);
                sent.Clear();
                Session.FeedForTest("{\"t\":\"files.result\",\"op\":\"delete\",\"ok\":false,\"why\":\"forbidden\",\"slot\":" + Q("x") + "}");
                check("a refusal from the server is turned into words and the list is asked for again", sent.Any(o => o.Str("t") == "files.list"));

                // ---- a damaged download leaves nothing behind
                var pk = MakeZip(Path.Combine(work, "ok.zip"), ("a.lua", "return 9"));
                check("setup: a slot to damage", await Session.UploadSlotAsync(null, "Damaged", pk, "") == null);
                Session.FeedForTest(fake.DataJson());
                var dmg = Session.Slots.First(s => s.Name == "Damaged");
                var dest2 = Path.Combine(work, "damaged.zip");
                fake.Corrupt = true;
                err = await Session.SaveVersionAsync(dmg, dmg.Newest, dest2);
                fake.Corrupt = false;
                check("damaged download: refused with a clear message (" + err + ")", err != null && err.Contains("damaged"));
                check("damaged download: no file and no .part left, nothing remembered", !File.Exists(dest2) && !File.Exists(dest2 + ".part") && Session.SavedOfForTest(dmg.Id) == null && dmg.Badge == "New");
                check("damaged download: the progress state is idle again", !dmg.Busy);
                err = await Session.SaveVersionAsync(dmg, dmg.Newest, dest2);
                check("and a clean second try saves it", err == null && File.Exists(dest2) && dmg.Badge == "");
                await HostileDownloadChecks(fake, work, dmg, check);
                await OneDangerList(fake, work, dmg, check);

                // ---- refusals: programs, zips with programs, size, empty
                var before = fake.Requests;
                var exe = Path.Combine(work, "tool.exe"); File.WriteAllText(exe, "MZ fake");
                err = await Session.UploadSlotAsync(null, "Tool", exe, "");
                check("blocked: a .exe is refused before anything is sent (" + err + ")", err != null && err.Contains(".exe") && fake.Requests == before);
                var bad = MakeZip(Path.Combine(work, "bad.zip"), ("docs/readme.txt", "hi"), ("bin/run.exe", "MZ"));
                err = await Session.UploadSlotAsync(null, "Bad zip", bad, "");
                check("blocked: a zip holding a .exe is refused before anything is sent (" + err + ")", err != null && err.Contains(".exe") && fake.Requests == before);
                var evil = MakeZip(Path.Combine(work, "evil.zip"), ("..\\..\\evil.txt", "x"));
                err = await Session.UploadSlotAsync(null, "Evil zip", evil, "");
                check("blocked: a zip with an unsafe path is refused (" + err + ")", err != null && err.Contains("unsafe") && fake.Requests == before);
                var big = Path.Combine(work, "big.bin");
                using (var fs = new FileStream(big, FileMode.Create)) fs.SetLength(1024 * 1024 + 1);
                err = await Session.UploadSlotAsync(null, "Big", big, "");
                check("size limit: over the lodge's maximum is refused before sending (" + err + ")", err != null && err.Contains("1 MB") && fake.Requests == before);
                var empty = Path.Combine(work, "empty.txt"); File.WriteAllBytes(empty, new byte[0]);
                err = await Session.UploadSlotAsync(null, "Empty", empty, "");
                check("an empty file is refused (" + err + ")", err != null && fake.Requests == before);
                err = await Session.UploadSlotAsync(null, "   ", pk, "");
                check("a slot needs a name", err != null && fake.Requests == before);
                err = await Session.UploadSlotAsync(null, "damaged", pk, "");
                check("a name that exists (any case) points to Replace", err != null && err.Contains("Replace") && fake.Requests == before);
                err = await Session.UploadSlotAsync(null, "Long note", pk, new string('n', 201));
                check("a note over 200 characters is refused", err != null && fake.Requests == before);
                // the server says no as well (a client that skips its own checks): its words come through
                var direct = new LodgeClient(fake.Url, FakeFilesServer.Code, "Elan");
                string e1 = null, e2 = null, e3 = null;
                try { await direct.UploadSlot(null, "Tool", exe, "", LodgeFiles.Sha256(exe)); } catch (InvalidOperationException ex) { e1 = ex.Message; }
                try { await direct.UploadSlot(null, "Bad", bad, "", LodgeFiles.Sha256(bad)); } catch (InvalidOperationException ex) { e2 = ex.Message; }
                try { await direct.UploadSlot(null, "Big", big, "", LodgeFiles.Sha256(big)); } catch (InvalidOperationException ex) { e3 = ex.Message; }
                check("server side: .exe, zip with .exe and too big are refused with its own text (" + e1 + " / " + e2 + " / " + e3 + ")", e1 != null && e1.Contains("Programs") && e2 != null && e2.Contains("zip") && e3 != null && e3.Contains("too big"));
                string e4 = null;
                try { await direct.UploadSlot(null, "Wrong sum", pk, "", new string('0', 64)); } catch (InvalidOperationException ex) { e4 = ex.Message; }
                check("server side: a wrong checksum is refused", e4 != null && e4.Contains("damaged"));
                check("none of the refused uploads created a slot", fake.Slots.Count == 1);

                // ---- who sees what
                Session.Disconnect(); sent.Clear();
                fake.Role = "member";
                Session.FeedForTest(FilesWelcome("member", true));
                Session.FilesClientForTest(new LodgeClient(fake.Url, FakeFilesServer.Code, "Elan"));
                Session.FeedForTest(fake.DataJson());
                LodgePage.ShowFilesForTest();
                var mslot = Session.Slots.FirstOrDefault();
                check("member: sees the page and the file, can save, no Upload new, no Replace / Delete / Roll back", LodgePage.FilesShownForTest && mslot != null && !LodgePage.FilesForTest.NewButtonVisibleForTest
                    && mslot.ManageVisibility == System.Windows.Visibility.Collapsed && !Session.CanManageFiles);
                LodgePage.FilesForTest.OpenVersions(mslot);
                check("member: no Roll back button on any version", LodgePage.FilesForTest.VersionRowItemsForTest.All(r => !r.CanRollBack));
                LodgePage.FilesForTest.CloseVersionsForTest();
                before = fake.Requests;
                err = await Session.UploadSlotAsync(null, "Member try", pk, "");
                check("member: upload is refused locally with the reason (" + err + ")", err != null && err.Contains("officers") && fake.Requests == before);
                check("member: replace is refused locally", (await Session.UploadSlotAsync(mslot.Id, null, pk, "")).Contains("officers"));
                check("member: delete and roll back send nothing", Session.DeleteSlot(mslot) != null && Session.RollBackSlot(mslot, 1) != null && !sent.Any(o => (o.Str("t") ?? "").StartsWith("files.d") || (o.Str("t") ?? "").StartsWith("files.r")));
                // a stale client that thinks it may: the server still says no, in words
                Session.Disconnect();
                Session.FeedForTest(FilesWelcome("officer", true));
                Session.FilesClientForTest(new LodgeClient(fake.Url, FakeFilesServer.Code, "Elan"));
                Session.FeedForTest(fake.DataJson().Replace("\"canManage\":false", "\"canManage\":true"));
                err = await Session.UploadSlotAsync(null, "Sneaky", pk, "");
                check("server says no to a member whose client thought it could (" + err + ")", err != null && err.Contains("officers") && fake.Slots.Count == 1);
                var dest3 = Path.Combine(work, "member-save.zip");
                Session.Disconnect(); fake.Role = "member";
                Session.FeedForTest(FilesWelcome("member", true));
                Session.FilesClientForTest(new LodgeClient(fake.Url, FakeFilesServer.Code, "Elan"));
                Session.FeedForTest(fake.DataJson());
                var ms = Session.Slots.First();
                err = await Session.SaveVersionAsync(ms, ms.Newest, dest3);
                check("member: Save works", err == null && File.Exists(dest3) && LodgeFiles.ZoneId(dest3) == 3);
                fake.Role = "guest";
                Session.Disconnect();
                Session.FeedForTest(FilesWelcome("guest", true));
                Session.FilesClientForTest(new LodgeClient(fake.Url, FakeFilesServer.Code, "Elan"));
                Session.FeedForTest(fake.DataJson());
                var gdest = Path.Combine(work, "guest-save.zip");
                err = await Session.SaveVersionAsync(Session.Slots.First(), Session.Slots.First().Newest, gdest);
                check("guest: sees the files and can save them, cannot change them", err == null && File.Exists(gdest) && !Session.CanManageFiles && !LodgePage.FilesForTest.NewButtonVisibleForTest);

                // ---- the notice above the chat
                Session.Disconnect(); sent.Clear();
                fake.Role = "officer";
                Session.FeedForTest(FilesWelcome("owner", true));
                Session.FeedForTest(fake.DataJson());
                LodgePage.HideFilesForTest();
                Session.FeedForTest(FilesChangedFrame("version", "Wing", "Raid UI pack", 3));
                check("notice: 'Wing updated Raid UI pack (v3)' above the chat", LodgePage.FilesBarVisibleForTest && LodgePage.FilesBarTextForTest == "Wing updated Raid UI pack (v3)");
                check("notice: the list is asked for again", sent.Any(o => o.Str("t") == "files.list"));
                LodgePage.CloseFilesBarForTest();
                check("notice: dismissible", !LodgePage.FilesBarVisibleForTest);
                Session.FeedForTest(FilesChangedFrame("new", "Wing", "Boss guide", 1));
                check("notice: a new slot reads 'Wing added Boss guide (v1)'", LodgePage.FilesBarTextForTest == "Wing added Boss guide (v1)");
                LodgePage.OpenFilesBarForTest();
                check("notice: Open Files shows the page and clears the notice", LodgePage.FilesShownForTest && !LodgePage.FilesBarVisibleForTest);
                Session.FeedForTest(FilesChangedFrame("rollback", "Wing", "Boss guide", 2));
                check("notice: not shown while the page itself is open", !LodgePage.FilesBarVisibleForTest);
                LodgePage.HideFilesForTest();
                Session.FeedForTest(FilesChangedFrame("delete", "Wing", "Boss guide", 0));
                check("notice: delete reads 'Wing removed Boss guide'", LodgePage.FilesBarTextForTest == "Wing removed Boss guide");
                LodgePage.CloseFilesBarForTest();
                Session.FeedForTest(FilesChangedFrame("version", "Elan", "Mine", 2));
                check("notice: my own change gives no notice", !LodgePage.FilesBarVisibleForTest);

                // ---- the page and the calendar share the chat's place; leaving a lodge starts clean
                LodgePage.ShowFilesForTest();
                check("page: Files is shown", LodgePage.FilesShownForTest);
                LodgePage.HideFilesForTest();
                check("page: closing it brings the chat back", !LodgePage.FilesShownForTest);
                Session.Disconnect();
                check("disconnect: the files state is cleared", !Session.SupportsFiles && Session.Slots.Count == 0 && !LodgePage.FilesRowVisibleForTest);

                // ---- addons (Lodge 2.32 "filesaddons"): marked on upload, installed in WoW from the Files page, updated, kept updated
                {
                    var addOns = Path.Combine(work, "wow", "_forever_", "Interface", "AddOns");
                    Directory.CreateDirectory(addOns);
                    LodgeAddons.AddOnsDir = () => addOns;
                    try { File.Delete(Path.Combine(Util.DataDir, "files-installed.txt")); } catch { }
                    fake.Slots.Clear();
                    Session.FeedForTest(FilesWelcome("officer", true, "2.32.0", 1, addons: true));
                    Session.FilesClientForTest(new LodgeClient(fake.Url, FakeFilesServer.Code, "Elan"));
                    Session.FeedForTest(fake.DataJson());
                    check("addons: a 2.32 Lodge with 'filesaddons' turns the feature on", Session.SupportsAddonFiles);
                    LodgePage.ShowFilesForTest();
                    var fv = LodgePage.FilesForTest;

                    var a1 = MakeZip(Path.Combine(work, "rxp1.zip"), ("RXPGuides/RXPGuides.toc", "## Interface: 16001\n## Version: 1.0\n"), ("RXPGuides/core.lua", "v1"), ("RXPGuides_Old/RXPGuides_Old.toc", ""));
                    fv.OpenForm(null);
                    fv.PickForTest(Path.Combine(work, "notes.txt"));
                    check("addons form: a file that isn't a zip can't be marked (" + fv.FormAddonWhyForTest + ")", fv.FormAddonForTest == false && fv.FormAddonWhyForTest.StartsWith("Only a .zip"));
                    fv.PickForTest(a1);
                    check("addons form: an addon zip ticks 'This is a WoW addon' by itself and names its folders (" + fv.FormAddonWhyForTest + ")", fv.FormAddonForTest == true && fv.FormAddonWhyForTest.Contains("RXPGuides 1.0") && fv.FormAddonWhyForTest.Contains("RXPGuides_Old"));
                    fv.FormNameForTest = "RXP Guides";
                    var aerr = await fv.GoAsync();
                    check("addons upload: the slot is marked as an addon on the Lodge (" + (aerr ?? "ok") + ")", aerr == null && fake.Slots.Count == 1 && fake.Slots[0].Kind == "addon");
                    var looseZip = MakeZip(Path.Combine(work, "loose.zip"), ("readme.txt", "x"));
                    var berr = await Session.UploadSlotAsync(null, "Loose", looseZip, "", null, true);
                    check("addons upload: a zip that isn't an addon can't be shared as one - this PC says why first (" + berr + ")", berr != null && berr.Contains("outside an addon folder") && fake.Slots.Count == 1);
                    Session.FeedForTest(fake.DataJson());
                    var rs = Session.Slots.First(s => s.Name == "RXP Guides");
                    check("addons list: the slot shows the Addon chip and Install in WoW", rs.IsAddon && rs.InstallVisibility == System.Windows.Visibility.Visible && rs.InstallLabel == "Install in WoW" && rs.InstalledV == 0);

                    // the member already has their own copy of RXPGuides: it is backed up, then replaced
                    Directory.CreateDirectory(Path.Combine(addOns, "RXPGuides"));
                    File.WriteAllText(Path.Combine(addOns, "RXPGuides", "core.lua"), "mine");
                    File.WriteAllText(Path.Combine(addOns, "RXPGuides", "RXPGuides.toc"), "## Version: 0.9\n");
                    Elans.Shared.AddonZip.Result askedChk = null;
                    fv.ConfirmInstall = (chk, s) => { askedChk = chk; return true; };
                    aerr = await fv.InstallAsync(rs);
                    var bkRoot = Path.Combine(AddonSources.BackupRoot, "lodge-" + AddonSources.SafeName("RXP Guides"));
                    bool backedUp = Directory.Exists(bkRoot) && Directory.GetDirectories(bkRoot).Any(d => File.Exists(Path.Combine(d, "RXPGuides", "core.lua")) && File.ReadAllText(Path.Combine(d, "RXPGuides", "core.lua")) == "mine");
                    check("addons install: asked first (both folders), installed into AddOns, the member's own copy backed up, remembered as v1 (" + (aerr ?? "ok") + ")",
                        aerr == null && askedChk != null && askedChk.Folders.Count == 2 && File.ReadAllText(Path.Combine(addOns, "RXPGuides", "core.lua")) == "v1" && Directory.Exists(Path.Combine(addOns, "RXPGuides_Old"))
                        && backedUp && rs.InstalledV == 1 && rs.InstallLabel == "Reinstall" && Session.InstalledOfForTest(rs.Id)?.Folders.Count == 2 && Session.FilesNoticeText.Contains("/reload"));
                    check("addons install: nothing is left behind in AddOns (no temporary folders)", !Directory.GetDirectories(addOns).Any(d => Path.GetFileName(d).StartsWith(".elanshub")));
                    fv.ConfirmInstall = (chk, s) => false;
                    var coreBefore = File.ReadAllText(Path.Combine(addOns, "RXPGuides", "core.lua"));
                    aerr = await fv.InstallAsync(rs);
                    check("addons install: No in the question changes nothing", aerr == "" && coreBefore == "v1");

                    // keep it updated: v2 (the same folders) goes in by itself
                    fv.ConfirmInstall = (chk, s) => true;
                    Session.SetAutoUpdate(rs, true);
                    var a2 = MakeZip(Path.Combine(work, "rxp2.zip"), ("RXPGuides/RXPGuides.toc", "## Version: 1.1\n"), ("RXPGuides/core.lua", "v2"), ("RXPGuides_Old/RXPGuides_Old.toc", ""));
                    aerr = await Session.UploadSlotAsync(rs.Id, null, a2, "fixes", null, true);
                    Session.FeedForTest(fake.DataJson());
                    for (int i = 0; i < 100 && rs.InstalledV != 2; i++) await Task.Delay(50);
                    check("addons keep it updated: v2 went into WoW by itself and the notice says to /reload (" + (aerr ?? "ok") + ")",
                        rs.InstalledV == 2 && File.ReadAllText(Path.Combine(addOns, "RXPGuides", "core.lua")) == "v2" && Session.FilesNoticeText.Contains("updated to v2") && rs.InstalledLine.Contains("keeps itself updated"));

                    // v3 drops a folder: not without asking; the click then installs it and takes the dropped folder out (backed up)
                    var a3 = MakeZip(Path.Combine(work, "rxp3.zip"), ("RXPGuides/RXPGuides.toc", "## Version: 1.2\n"), ("RXPGuides/core.lua", "v3"));
                    aerr = await Session.UploadSlotAsync(rs.Id, null, a3, "", null, true);
                    Session.FeedForTest(fake.DataJson());
                    for (int i = 0; i < 40 && rs.Error.Length == 0; i++) await Task.Delay(50);
                    check("addons keep it updated: a version that changes the folders waits for a click (" + rs.Error + ")",
                        rs.InstalledV == 2 && rs.Error.Contains("changes which addon folders") && rs.InstallLabel == "Update in WoW" && rs.Badge == "Update");
                    aerr = await fv.InstallAsync(rs);
                    check("addons update: the click installs v3 and takes out the folder it no longer has (" + (aerr ?? "ok") + ")",
                        aerr == null && rs.InstalledV == 3 && File.ReadAllText(Path.Combine(addOns, "RXPGuides", "core.lua")) == "v3" && !Directory.Exists(Path.Combine(addOns, "RXPGuides_Old")));

                    // WoW holding a file open mid-swap: everything is put back
                    LodgeAddons.SwapHook = f => throw new IOException("locked");
                    aerr = await fv.InstallAsync(rs);
                    LodgeAddons.SwapHook = null;
                    check("addons install: a failure mid-swap puts everything back (" + aerr + ")", aerr != null && aerr.Contains("put back") && File.ReadAllText(Path.Combine(addOns, "RXPGuides", "core.lua")) == "v3"
                        && !Directory.GetDirectories(addOns).Any(d => Path.GetFileName(d).StartsWith(".elanshub")));

                    // a git checkout is someone's work: never replaced
                    var dev = MakeZip(Path.Combine(work, "dev.zip"), ("DevA/DevA.toc", ""), ("DevA/a.lua", "new"));
                    aerr = await Session.UploadSlotAsync(null, "Dev addon", dev, "", null, true);
                    Directory.CreateDirectory(Path.Combine(addOns, "DevA", ".git"));
                    File.WriteAllText(Path.Combine(addOns, "DevA", "a.lua"), "work in progress");
                    Session.FeedForTest(fake.DataJson());
                    aerr = await fv.InstallAsync(Session.Slots.First(s => s.Name == "Dev addon"));
                    check("addons install: a git checkout (development copy) is never replaced (" + aerr + ")", aerr != null && aerr.Contains("development copy") && File.ReadAllText(Path.Combine(addOns, "DevA", "a.lua")) == "work in progress");

                    // a Lodge that marks something as an addon that isn't one: this PC checks again and refuses
                    var hostile = new FakeFilesServer.SlotRec { Id = 99.ToString("x32"), Name = "Not really", CreatedBy = "Elan", Kind = "addon" };
                    var hb = File.ReadAllBytes(MakeZip(Path.Combine(work, "hostile.zip"), ("evil.txt", "x"), ("A/A.toc", "")));
                    hostile.Versions.Add(new FakeFilesServer.Ver { V = 1, FileName = "a.zip", Sha = LodgeFiles.Hex(System.Security.Cryptography.SHA256.Create().ComputeHash(hb)), By = "Elan", Note = "", Bytes = hb, At = 1_790_000_000_000L });
                    fake.Slots.Add(hostile);
                    Session.FeedForTest(fake.DataJson());
                    aerr = await fv.InstallAsync(Session.Slots.First(s => s.Name == "Not really"));
                    check("addons install: a slot the Lodge calls an addon but isn't one is refused on this PC (" + aerr + ")", aerr != null && aerr.StartsWith("Not installed") && !File.Exists(Path.Combine(addOns, "evil.txt")) && !Directory.Exists(Path.Combine(addOns, "A")));

                    LodgeAddons.AddOnsDir = () => null;
                    aerr = await fv.InstallAsync(rs);
                    check("addons install: no WoW Forever on this PC says so", aerr != null && aerr.Contains("WoW Forever wasn't found"));
                    LodgeAddons.AddOnsDir = () => addOns;
                    fv.ConfirmInstall = null;
                    LodgePage.HideFilesForTest();

                    // an older Lodge (no 'filesaddons'): no checkbox, no install button, the upload says nothing about kinds
                    Session.Disconnect();
                    Session.FeedForTest(FilesWelcome("officer", true, "2.31.0", 1, addons: false));
                    Session.FeedForTest(fake.DataJson());
                    var old = Session.Slots.FirstOrDefault(s => s.Name == "RXP Guides");
                    fv.OpenForm(null);
                    check("addons on an older Lodge: no checkbox in the form and no Install button", !Session.SupportsAddonFiles && fv.FormAddonForTest == null && old != null && !old.IsAddon && old.InstallVisibility == System.Windows.Visibility.Collapsed);
                    fv.CloseFormForTest();
                    Session.Disconnect();
                }

                // ---- 2.33 "files2": who sees / updates a file, its description and picture, the Edit dialog, the details, search
                {
                    fake.Slots.Clear();
                    FakeFilesServer.SlotRec Rec(string name, string desc = null, string see = null, string ed = null, bool canUpdate = false)
                    {
                        var data = System.Text.Encoding.UTF8.GetBytes(name);
                        var r = new FakeFilesServer.SlotRec { Id = (fake.Slots.Count + 500).ToString("x32"), Name = name, CreatedBy = "Wing", Desc = desc, See = see, Edit = ed, CanUpdate = canUpdate };
                        r.Versions.Add(new FakeFilesServer.Ver { V = 1, FileName = name + ".txt", Sha = LodgeFiles.Hex(System.Security.Cryptography.SHA256.Create().ComputeHash(data)), By = "Wing", Note = "first", Bytes = data, At = 1_790_000_000_000L });
                        fake.Slots.Add(r);
                        return r;
                    }
                    var loot = Rec("Loot rules", "How we hand out loot.\nRead before raid night.", "officer");
                    var wa = Rec("Raid WeakAuras", "Import the newest one.", null, "veteran", canUpdate: true);
                    for (int i = 0; i < 5; i++) Rec("Guide " + i);

                    // a member on a 2.33 Lodge: sees what the Lodge sent (the Lodge filters), may update the one set for them, no ranks in Edit
                    Session.Disconnect(); sent.Clear();
                    fake.Role = "member";
                    Session.FeedForTest(FilesWelcome("member", true, "2.33.0", 1, addons: true, files2: true));
                    Session.FilesClientForTest(new LodgeClient(fake.Url, FakeFilesServer.Code, "Elan"));
                    Session.FeedForTest(fake.DataJson());
                    LodgePage.ShowFilesForTest();
                    var fv2 = LodgePage.FilesForTest;
                    var sLoot = Session.Slots.First(s => s.Name == "Loot rules"); var sWa = Session.Slots.First(s => s.Name == "Raid WeakAuras");
                    check("files2: the card says who sees it (lock chip 'Officers') and the description's first line; the details say who sees and updates it",
                        Session.SupportsFiles2 && sLoot.SeeChip == "Officers" && sLoot.DescShort == "How we hand out loot." && sLoot.SeeLine == "Officers only" && sLoot.EditLine == "Officers" && sWa.EditLine == "Veterans and up");
                    check("files2: a member may update the file set for veterans when the Lodge says so - and only that one",
                        sWa.CanUpdate && sWa.UpdateVisibility == System.Windows.Visibility.Visible && !sLoot.CanUpdate && sLoot.UpdateVisibility == System.Windows.Visibility.Collapsed);
                    fv2.OpenVersions(sWa);
                    check("files2 details: the panel opens on the file and shows who can see and update it", fv2.VersionsOpenForTest && fv2.DetailsShowsRanksForTest);
                    fv2.CloseVersionsForTest();
                    fv2.OpenEdit(sLoot);
                    check("files2 edit: no Edit for a file you may not update", !fv2.EditOpenForTest);
                    fv2.OpenEdit(sWa);
                    check("files2 edit: a member's Edit has no name and no ranks (officers' part)", fv2.EditOpenForTest && !fv2.EditRanksShownForTest);
                    sent.Clear();
                    fv2.EditSetForTest(desc: "Import the newest one.\nUpdated for MC.", note: "MC pack");
                    var eerr = await fv2.SaveEditAsync();
                    var em = sent.FirstOrDefault(o => o.Str("t") == "files.edit");
                    check("files2 edit: only what changed is sent - the description and the newest version's note (" + (eerr ?? "ok") + ")",
                        eerr == null && em != null && em.Str("slot") == sWa.Id && em.Str("desc") == "Import the newest one.\nUpdated for MC." && em.Str("note") == "MC pack" && em.Int("noteV") == 1 && em.Str("see") == null && em.Str("name") == null && !fv2.EditOpenForTest);

                    // the picture: chosen in Edit, checked here, uploaded, then fetched and shown on the card
                    var pic = Path.Combine(work, "cover.png");
                    var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(64, 36, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    var enc = new System.Windows.Media.Imaging.PngBitmapEncoder(); enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                    using (var fs = File.Create(pic)) enc.Save(fs);
                    File.WriteAllText(Path.Combine(work, "fake.png"), "not a picture at all");
                    fv2.OpenEdit(sWa);
                    fv2.SetEditPicture(Path.Combine(work, "fake.png"));
                    check("files2 picture: a file that isn't a picture is refused in the dialog (" + fv2.EditErrorForTest + ")", fv2.EditErrorForTest.Contains("isn't a picture"));
                    fv2.SetEditPicture(pic);
                    eerr = await fv2.SaveEditAsync();
                    Session.FeedForTest(fake.DataJson());
                    for (int i = 0; i < 60 && sWa.CoverImage == null; i++) await Task.Delay(50);
                    check("files2 picture: uploaded to the Lodge, fetched back and shown on the card (" + (eerr ?? "ok") + ")",
                        eerr == null && wa.CoverBytes != null && sWa.CoverV == 1 && sWa.CoverImage != null && sWa.CoverVisibility == System.Windows.Visibility.Visible && File.Exists(Path.Combine(LodgeSession.CoverDir, sWa.Id + "-1.img")));

                    // search: over 6 files the box shows; it matches name and description
                    fv2.SearchForTest("raid");
                    check("files2 search: 'raid' finds the WeakAuras (name) and the loot rules (description)", fv2.CardsShownForTest == 2);
                    fv2.SearchForTest("nothing like this");
                    check("files2 search: no match = 'Nothing matches'", fv2.CardsShownForTest == 0 && fv2.EmptyVisibleForTest);
                    fv2.SearchForTest("");
                    // pictures (ELANSHUB_SHOTS=1): the card grid, the details, the Edit dialog
                    ShowTab("lodge"); LodgePage.ShowFilesForTest(); await Task.Delay(500);
                    Snapshot(Path.Combine(dir, "files-grid.png"));
                    fv2.OpenVersions(sWa); await Task.Delay(400);
                    Snapshot(Path.Combine(dir, "files-details.png"));
                    fv2.CloseVersionsForTest();
                    fv2.OpenEdit(sWa); await Task.Delay(400);
                    Snapshot(Path.Combine(dir, "files-edit-member.png"));
                    fv2.CloseEditForTest();

                    // an officer: the whole Edit, ranks included
                    Session.Disconnect(); sent.Clear();
                    fake.Role = "officer";
                    Session.FeedForTest(FilesWelcome("officer", true, "2.33.0", 1, addons: true, files2: true));
                    Session.FeedForTest(fake.DataJson());
                    var oLoot = Session.Slots.First(s => s.Name == "Loot rules");
                    fv2.OpenEdit(oLoot);
                    check("files2 edit: an officer's Edit has the name and the ranks", fv2.EditOpenForTest && fv2.EditRanksShownForTest);
                    fv2.EditSetForTest(name: "Loot rules 2026", see: "member", editRank: "veteran");
                    eerr = await fv2.SaveEditAsync();
                    em = sent.FirstOrDefault(o => o.Str("t") == "files.edit");
                    check("files2 edit: the officer's rename and ranks are sent (" + (eerr ?? "ok") + ")",
                        eerr == null && em != null && em.Str("name") == "Loot rules 2026" && em.Str("see") == "member" && em.Str("edit") == "veteran" && em.Str("desc") == null);
                    LodgePage.HideFilesForTest();

                    // an older Lodge (no files2): no Edit, no ranks in the details
                    Session.Disconnect();
                    Session.FeedForTest(FilesWelcome("officer", true, "2.32.0", 1, addons: true, files2: false));
                    Session.FeedForTest(fake.DataJson());
                    fv2.OpenEdit(Session.Slots.First());
                    check("files2 on an older Lodge: no Edit dialog", !Session.SupportsFiles2 && !fv2.EditOpenForTest);
                    Session.Disconnect();
                    fake.Slots.Clear();
                }

                // ---- 2.34 "filestext": import strings - shared as text, a Copy button, a preview, new versions
                {
                    fake.Slots.Clear();
                    Session.Disconnect(); sent.Clear();
                    fake.Role = "officer";
                    Session.FeedForTest(FilesWelcome("officer", true, "2.34.0", 1, addons: true, files2: true, text: true));
                    Session.FilesClientForTest(new LodgeClient(fake.Url, FakeFilesServer.Code, "Elan"));
                    Session.FeedForTest(fake.DataJson());
                    LodgePage.ShowFilesForTest();
                    var fv3 = LodgePage.FilesForTest;
                    fv3.OpenForm(null);
                    check("text: the share form offers 'A file' or 'Text to copy'", Session.SupportsFilesText && fv3.FormModesShownForTest);
                    var wa1 = "!WA:2!S3xtrjoe raid pack \u2713";
                    fv3.FormNameForTest = "Raid WeakAura";
                    fv3.FormTextForTest(wa1);
                    var terr = await fv3.GoAsync();
                    check("text: pasted text is shared as an import string (kind text, the text itself) (" + (terr ?? "ok") + ")",
                        terr == null && fake.Slots.Count == 1 && fake.Slots[0].Kind == "text" && System.Text.Encoding.UTF8.GetString(fake.Slots[0].Versions[0].Bytes) == wa1);
                    Session.FeedForTest(fake.DataJson());
                    var ts = Session.Slots.First();
                    check("text: the card says Import and offers Copy (no Save on the card)", ts.IsText && ts.KindChip == "Import" && ts.CopyVisibility == System.Windows.Visibility.Visible && ts.SaveCardVisibility == System.Windows.Visibility.Collapsed);
                    string clip = null;
                    var hadClip = FilesView.SetClipboard;
                    FilesView.SetClipboard = t => clip = t;
                    try
                    {
                        terr = await fv3.CopyAsync(ts);
                        check("text: Copy puts the newest text on the clipboard and shows a preview (" + (terr ?? "ok") + ")", terr == null && clip == wa1 && ts.TextPreview == wa1 && Session.FilesNoticeText.Contains("clipboard"));
                        fv3.OpenForm(ts);
                        check("text: a new version of a text file is text again (the form opens in text mode, no mode choice)", !fv3.FormModesShownForTest);
                        fv3.FormTextForTest("!WA:2!newer");
                        terr = await fv3.GoAsync();
                        Session.FeedForTest(fake.DataJson());
                        terr = terr ?? await fv3.CopyAsync(ts);
                        check("text: Copy after a new version gives the new text (" + (terr ?? "ok") + ")", terr == null && clip == "!WA:2!newer" && fake.Slots[0].Versions.Count == 2);
                    }
                    finally { FilesView.SetClipboard = hadClip; }
                    LodgePage.HideFilesForTest();
                    Session.Disconnect();
                    Session.FeedForTest(FilesWelcome("officer", true, "2.33.0", 1, addons: true, files2: true, text: false));
                    Session.FeedForTest(fake.DataJson());
                    fv3.OpenForm(null);
                    check("text on an older Lodge: no text mode, a text slot is a plain file", !fv3.FormModesShownForTest && !Session.Slots.First().IsText);
                    fv3.CloseFormForTest();
                    Session.Disconnect();
                    fake.Slots.Clear();
                    try { if (Directory.Exists(LodgeSession.TextDir)) Directory.Delete(LodgeSession.TextDir, true); } catch { }
                }

                // ---- 2.35 "filesinfo": an addon version's folders against this PC (Interface, what it needs), who has it, and telling the Lodge what is installed
                {
                    fake.Slots.Clear();
                    var aoDir = Path.Combine(work, "wow2", "_forever_", "Interface", "AddOns");
                    Directory.CreateDirectory(Path.Combine(aoDir, "Ace3"));
                    var hadIface = LodgeAddons.ClientInterface;
                    LodgeAddons.AddOnsDir = () => aoDir; LodgeAddons.ClientInterface = () => 16001;
                    try
                    {
                        var dz = MakeZip(Path.Combine(work, "dbm.zip"), ("DBM-Raid/DBM-Raid.toc", "## Interface: 11509\n## Version: 2.1\n## Dependencies: DBM-Core, Ace3\n"), ("DBM-Raid/x.lua", "x"));
                        var db = File.ReadAllBytes(dz);
                        var rec = new FakeFilesServer.SlotRec { Id = 777.ToString("x32"), Name = "DBM raid", CreatedBy = "Wing", Kind = "addon", HaveNewest = 4, HaveOlder = 2 };
                        rec.Versions.Add(new FakeFilesServer.Ver { V = 1, FileName = "dbm.zip", Sha = LodgeFiles.Hex(System.Security.Cryptography.SHA256.Create().ComputeHash(db)), By = "Wing", Note = "", Bytes = db, At = 1_790_000_000_000L,
                            AddonJson = "[{\"f\":\"DBM-Raid\",\"v\":\"2.1\",\"i\":\"11509\",\"d\":\"DBM-Core,Ace3\"}]" });
                        fake.Slots.Add(rec);
                        Session.Disconnect(); sent.Clear();
                        Session.FeedForTest(FilesWelcome("member", true, "2.35.0", 1, addons: true, files2: true, text: true, info: true));
                        Session.FilesClientForTest(new LodgeClient(fake.Url, FakeFilesServer.Code, "Elan"));
                        Session.FeedForTest(fake.DataJson());
                        var ds = Session.Slots.First();
                        check("addon info: the details list the folder with its version and game version (" + ds.AddonInfo + ")", ds.AddonInfo == "DBM-Raid 2.1 · for 11509");
                        check("addon info: built for another game version and a missing addon are warned about; one you have (Ace3) is not (" + ds.AddonWarn + ")",
                            ds.AddonWarn.Contains("Built for another game version: DBM-Raid (11509)") && ds.AddonWarn.Contains("WoW Forever is 16001") && ds.AddonWarn.Contains("Needs DBM-Core,") && !ds.AddonWarn.Contains("Ace3"));
                        check("addon info: who has it, in counts (" + ds.HaveLine + ")", ds.HaveLine == "4 members have the newest · 2 an older version");
                        var fv4 = LodgePage.FilesForTest;
                        fv4.ConfirmInstall = (chk, s) => true;
                        var ierr = await fv4.InstallAsync(ds);
                        var told = sent.Where(o => o.Str("t") == "files.installed").ToList();
                        check("addon info: after installing, the Lodge is told which version this PC has - once (" + (ierr ?? "ok") + ")",
                            ierr == null && told.Count == 1 && told[0].Str("slot") == ds.Id && told[0].Int("v") == 1);
                        Session.FeedForTest(fake.DataJson());
                        check("addon info: a fresh list doesn't tell it again", sent.Count(o => o.Str("t") == "files.installed") == 1);
                        fv4.ConfirmInstall = null;
                    }
                    finally { LodgeAddons.ClientInterface = hadIface; LodgeAddons.AddOnsDir = hadAddOnsDir; }
                    Session.Disconnect();
                    fake.Slots.Clear();
                }

                // ---- 2.36 "fileskits": kits - made by officers, installed and kept updated together
                {
                    fake.Slots.Clear(); fake.KitsJson.Clear();
                    var kDir = Path.Combine(work, "wow3", "_forever_", "Interface", "AddOns");
                    Directory.CreateDirectory(kDir);
                    LodgeAddons.AddOnsDir = () => kDir;
                    try { File.Delete(Path.Combine(Util.DataDir, "files-installed.txt")); } catch { }
                    FakeFilesServer.SlotRec Addon(string name, string folder)
                    {
                        var b = File.ReadAllBytes(MakeZip(Path.Combine(work, folder + ".zip"), (folder + "/" + folder + ".toc", "## Version: 1\n"), (folder + "/a.lua", "x")));
                        var r = new FakeFilesServer.SlotRec { Id = (fake.Slots.Count + 900).ToString("x32"), Name = name, CreatedBy = "Wing", Kind = "addon" };
                        r.Versions.Add(new FakeFilesServer.Ver { V = 1, FileName = folder + ".zip", Sha = LodgeFiles.Hex(System.Security.Cryptography.SHA256.Create().ComputeHash(b)), By = "Wing", Note = "", Bytes = b, At = 1_790_000_000_000L });
                        fake.Slots.Add(r); return r;
                    }
                    var k1 = Addon("DBM raid", "DBMRaid"); var k2 = Addon("WeakAuras", "WeakAuras");
                    Session.Disconnect(); sent.Clear();
                    fake.Role = "officer";
                    Session.FeedForTest(FilesWelcome("officer", true, "2.36.0", 1, addons: true, files2: true, text: true, info: true, kits: true));
                    Session.FilesClientForTest(new LodgeClient(fake.Url, FakeFilesServer.Code, "Elan"));
                    Session.FeedForTest(fake.DataJson());
                    LodgePage.ShowFilesForTest();
                    var fk = LodgePage.FilesForTest;
                    check("kits: an officer sees the Kits row (with New kit) once there are files to put together", Session.SupportsFilesKits && fk.KitsShownForTest);
                    fk.OpenKit(null);
                    fk.KitSetForTest("Raid night kit", "Everything for raid night", "member");
                    check("kits: a kit without files is refused (" + fk.SaveKit() + ")", fk.KitErrorForTest.Contains("Pick the files"));
                    fk.KitSetForTest("Raid night kit", "Everything for raid night", "member", "DBM raid", "WeakAuras");
                    sent.Clear();
                    var kerr = fk.SaveKit();
                    var km = sent.FirstOrDefault(o => o.Str("t") == "files.kit");
                    check("kits: Save kit sends its name, description, who sees it and its files (" + (kerr ?? "ok") + ")",
                        kerr == null && km != null && km.Str("name") == "Raid night kit" && km.Str("see") == "member" && km.Str("slots").Split(',').OrderBy(x => x).SequenceEqual(new[] { k1.Id, k2.Id }.OrderBy(x => x)) && !fk.KitOpenForTest);
                    fake.KitsJson.Add("{\"id\":" + Q(950.ToString("x32")) + ",\"name\":\"Raid night kit\",\"desc\":\"Everything for raid night\",\"see\":\"member\",\"createdBy\":\"Wing\",\"slots\":" + Q(k1.Id + "," + k2.Id) + "}");
                    Session.FeedForTest(fake.DataJson());
                    var kit = Session.Kits.Single();
                    check("kits: the kit card counts its files and addons and offers Install all (" + kit.CountLine + ")", kit.Slots.Count == 2 && kit.CountLine == "2 files · 2 addons" && kit.InstallAllLabel == "Install all" && kit.StateLine.StartsWith("None"));
                    List<SlotVM> askedFor = null;
                    fk.ConfirmKit = (todo, kv) => { askedFor = todo; return true; };
                    kerr = await fk.InstallKitAsync(kit);
                    check("kits: Install all asks once for both addons and installs them (" + (kerr ?? "ok") + " / " + kit.StateLine + ")",
                        kerr == null && askedFor != null && askedFor.Count == 2 && Directory.Exists(Path.Combine(kDir, "DBMRaid")) && Directory.Exists(Path.Combine(kDir, "WeakAuras")) && kit.StateLine.StartsWith("2 of 2 addons installed · up to date"));
                    Session.SetKitAuto(kit, true);
                    check("kits: Keep all updated sets every installed addon of the kit to update itself", kit.AutoAll && kit.Slots.All(s => s.AutoUpdate) && kit.StateLine.Contains("keeps itself updated"));
                    fk.ConfirmKit = null;
                    LodgePage.HideFilesForTest();
                }
                LodgeAddons.AddOnsDir = hadAddOnsDir;
                Session.Disconnect();
                fake.Slots.Clear(); fake.KitsJson.Clear();

                // ---- 2.37 "filescats": category chips, the category in Edit; 3.5: the update card above the chat
                {
                    fake.Slots.Clear(); fake.KitsJson.Clear();
                    var cDir = Path.Combine(work, "wow4", "_forever_", "Interface", "AddOns");
                    Directory.CreateDirectory(cDir);
                    LodgeAddons.AddOnsDir = () => cDir;
                    try { File.Delete(Path.Combine(Util.DataDir, "files-installed.txt")); } catch { }
                    FakeFilesServer.SlotRec R(string id, string name, string kind = null, string cat = null, byte[] bytes = null, string file = null)
                    {
                        var b = bytes ?? System.Text.Encoding.UTF8.GetBytes(name);
                        var r = new FakeFilesServer.SlotRec { Id = id, Name = name, CreatedBy = "Wing", Kind = kind, Cat = cat };
                        r.Versions.Add(new FakeFilesServer.Ver { V = 1, FileName = file ?? name + ".txt", Sha = LodgeFiles.Hex(System.Security.Cryptography.SHA256.Create().ComputeHash(b)), By = "Wing", Note = "", Bytes = b, At = 1_790_000_000_000L });
                        fake.Slots.Add(r); return r;
                    }
                    var az = File.ReadAllBytes(MakeZip(Path.Combine(work, "boss.zip"), ("BossMods/BossMods.toc", "## Version: 3\n"), ("BossMods/a.lua", "x")));
                    var boss = R(1.ToString("x32"), "Boss mods", "addon", null, az, "boss.zip");
                    R(2.ToString("x32"), "Nameplates", "addon"); R(3.ToString("x32"), "Raid WA", "text");
                    R(4.ToString("x32"), "Leveling guide", null, "guide"); R(5.ToString("x32"), "Dungeon guide", null, "guide");
                    R(6.ToString("x32"), "Molten Core map", null, "map"); var plain = R(7.ToString("x32"), "Loot list");
                    Session.Disconnect(); sent.Clear();
                    Session.FeedForTest(FilesWelcome("officer", true, "2.37.0", 1, addons: true, files2: true, text: true, info: true, kits: true, cats: true));
                    Session.FilesClientForTest(new LodgeClient(fake.Url, FakeFilesServer.Code, "Elan"));
                    Session.FeedForTest(fake.DataJson());
                    LodgePage.ShowFilesForTest();
                    var fc = LodgePage.FilesForTest;
                    var chips = fc.ChipsForTest;
                    check("cats: over 6 files the chips show All and every kind the list has (" + string.Join(", ", chips) + ")", chips.SequenceEqual(new[] { "All", "Addons", "Import strings", "Guides", "Maps" }));
                    fc.ChipForTest("Guides");
                    check("cats: Guides shows the two guides", fc.CardsShownForTest == 2);
                    fc.ChipForTest("All");
                    check("details view: the list (Details) is the default view", fc.DetailsViewForTest);
                    fc.SortBy(nameof(SlotVM.Name));
                    var zToA = fc.RowOrderForTest;
                    fc.SortBy(nameof(SlotVM.Name));
                    check("details view: the list starts A to Z; clicking Name flips it, like Explorer (" + string.Join(",", zToA.Take(2)) + " / " + string.Join(",", fc.RowOrderForTest.Take(2)) + ")",
                        zToA.First() == "Raid WA" && fc.RowOrderForTest.First() == "Boss mods");
                    fc.SortBy(nameof(SlotVM.SizeBytes));
                    check("details view: Size sorts biggest first", fc.RowOrderForTest.First() == "Boss mods");
                    fc.SortBy(nameof(SlotVM.Name)); fc.SortBy(nameof(SlotVM.Name));
                    ShowTab("lodge"); await Task.Delay(400); Snapshot(Path.Combine(dir, "files-chips.png"));
                    if (ReadmeOn)
                    {
                        // the README picture lists sizes like real files have (the test's files are a few bytes)
                        var shown = new Dictionary<string, long> { ["Raid WA"] = 4900, ["Nameplates"] = 1260000, ["Molten Core map"] = 2450000, ["Loot list"] = 3100, ["Leveling guide"] = 184000, ["Dungeon guide"] = 96500, ["Boss mods"] = 3240000 };
                        void Sizes(bool on) { foreach (var sr in fake.Slots) foreach (var v in sr.Versions) v.ShownSize = on && shown.TryGetValue(sr.Name, out var n) ? n : 0; Session.FeedForTest(fake.DataJson()); }
                        Sizes(true); await Task.Delay(300);
                        await ReadmeShot("files");
                        Sizes(false); await Task.Delay(300);
                    }
                    check("details view: a narrow list drops In WoW, Shared by, Updated first; a wide one shows all (" + string.Join(",", fc.ShownColumnsForTest(700)) + " / " + string.Join(",", fc.ShownColumnsForTest(1200)) + ")",
                        fc.ShownColumnsForTest(700).SequenceEqual(new[] { "Kind", "Version", "Size" }) && fc.ShownColumnsForTest(1200).Count == 6 && fc.ShownColumnsForTest(400).SequenceEqual(new[] { "Kind" }));
                    await Task.Delay(200);
                    fc.DetailsViewForTest = false; await Task.Delay(300); Snapshot(Path.Combine(dir, "files-tiles.png"));
                    check("details view: Tiles switches to the cards and the choice is kept", !fc.DetailsViewForTest && settings.FilesView == "tiles");
                    fc.DetailsViewForTest = true;
                    fc.OpenEdit(Session.Slots.First(s => s.Name == "Loot list"));
                    check("cats: Edit of a plain file offers 'What it is'", fc.EditCatShownForTest);
                    fc.EditCatForTest("other");
                    sent.Clear();
                    var cerr = await fc.SaveEditAsync();
                    check("cats: the category is sent (" + (cerr ?? "ok") + ")", cerr == null && sent.Any(o => o.Str("t") == "files.edit" && o.Str("cat") == "other"));
                    fc.OpenEdit(Session.Slots.First(s => s.Name == "Boss mods"));
                    check("cats: an addon has no category to pick (its kind says it)", !fc.EditCatShownForTest);
                    fc.CloseEditForTest();
                    var rar = R(8.ToString("x32"), "RXPGuides", null, null, new byte[] { 0x52, 0x61, 0x72, 0x21 }, "RXPGuides-v4.11.29.rar");
                    Session.FeedForTest(fake.DataJson());
                    fc.OpenEdit(Session.Slots.First(s => s.Name == "RXPGuides"));
                    check("edit: a .rar can't be ticked as an addon, and the dialog says why and what to do (" + fc.EditAddonWhyForTest + ")", !fc.EditAddonEnabledForTest && fc.EditAddonWhyForTest.Contains("This is a .rar") && fc.EditAddonWhyForTest.Contains("Compress to ZIP file"));
                    await ShotWait(300); Snapshot(Path.Combine(dir, "files-edit-rar.png"));
                    fc.CloseEditForTest();
                    var rarFile = Path.Combine(work, "RXPGuides-v4.11.29.rar");
                    File.WriteAllBytes(rarFile, new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00 });
                    fc.OpenForm(null); fc.PickForTest(rarFile);
                    check("share form: a .rar says to zip the addon folder instead (" + fc.FormAddonWhyForTest + ")", fc.FormAddonForTest == false && fc.FormAddonWhyForTest.Contains("Compress to ZIP file"));
                    await ShotWait(300); Snapshot(Path.Combine(dir, "files-form-rar.png"));
                    fc.CloseFormForTest();
                    fc.OpenEdit(Session.Slots.First(s => s.Name == "RXPGuides"));
                    check("refusals have words: not-addon, name-taken, not-image; an unknown one names its reason",
                        LodgeSession.FilesWhy("not-addon").Contains(".zip of addon folders") && LodgeSession.FilesWhy("name-taken").Contains("already shared") && LodgeSession.FilesWhy("not-image").Contains("picture")
                        && LodgeSession.FilesWhy("zzz") == "The lodge refused that (zzz)");
                    fc.CloseEditForTest();
                    // the update card: Wing uploads v2 of the boss mods while I'm in the chat
                    LodgePage.HideFilesForTest();
                    Session.FeedForTest(FilesChangedFrame("version", "Wing", "Boss mods", 1));
                    Session.FeedForTest(fake.DataJson());
                    check("update card: the notice above the chat carries the file's version and its action - Install in WoW (" + LodgePage.FilesBarActionForTest + " / " + LodgePage.FilesBarSubForTest + ")",
                        LodgePage.FilesBarVisibleForTest && LodgePage.FilesBarActionForTest == "Install in WoW" && LodgePage.FilesBarSubForTest.StartsWith("v1 · boss.zip"));
                    await Task.Delay(300); Snapshot(Path.Combine(dir, "files-updatecard.png"));
                    fc.ConfirmInstall = (chk, s) => true;
                    LodgePage.FilesBarActionClickForTest();
                    var bs = Session.Slots.First(s => s.Name == "Boss mods");
                    for (int i = 0; i < 200 && (bs.InstalledV == 0 || !LodgePage.FilesBarTextForTest.Contains("/reload")); i++) await Task.Delay(50);
                    check("update card: its button installs the addon right from the chat (" + LodgePage.FilesBarTextForTest + " | v" + bs.InstalledV + " err=" + bs.Error + " busy=" + bs.Busy + " notice=" + Session.FilesNoticeText + ")",
                        bs.InstalledV == 1 && Directory.Exists(Path.Combine(cDir, "BossMods")) && LodgePage.FilesBarTextForTest.Contains("/reload"));
                    fc.ConfirmInstall = null;
                    LodgePage.CloseFilesBarForTest();
                    LodgeAddons.AddOnsDir = hadAddOnsDir;
                    Session.Disconnect();
                    fake.Slots.Clear();
                }

                // ---- the badge rules on their own
                var s1 = new SlotVersionInfo { V = 2, Sha256 = "aa" };
                check("badge rules: never saved = New; same content = none; other content = Updated; no version = none",
                    LodgeFiles.Badge(s1, null) == "New" && LodgeFiles.Badge(s1, new SavedRecord { V = 2, Sha = "AA" }) == "" && LodgeFiles.Badge(s1, new SavedRecord { V = 1, Sha = "bb" }) == "Updated" && LodgeFiles.Badge(null, null) == "");
                check("notice texts", LodgeFiles.NoticeText("version", "Wing", "Raid UI pack", 3) == "Wing updated Raid UI pack (v3)" && LodgeFiles.NoticeText("zzz", "a", "b", 1) == null);
            }
            catch (Exception ex)
            {
                notes.Add("FAIL exception: " + ex);
                ok = false;
            }
            finally
            {
                Dialog.TestHook = hadHook;
                Session.SentForTest = null;
                Session.FilesClientForTest(null);
                settings.LodgeUrl = hadLodgeUrl;
                try { Session.Disconnect(); } catch { }
                try { fake?.Stop(); } catch { }
                try { File.Delete(savedFile); } catch { }
                try { File.Delete(Path.Combine(Util.DataDir, "files-installed.txt")); } catch { }
                LodgeAddons.AddOnsDir = hadAddOnsDir; LodgeAddons.SwapHook = null;
                try { if (Directory.Exists(LodgeSession.CoverDir)) Directory.Delete(LodgeSession.CoverDir, true); } catch { }
            }
            return "lodgefiles selftest: " + (ok ? "PASSED" : "FAILED") + "\r\n  " + string.Join("\r\n  ", notes);
        }

        static string MakeZip(string path, params (string name, string text)[] entries)
        {
            if (File.Exists(path)) File.Delete(path);
            using (var z = ZipFile.Open(path, ZipArchiveMode.Create))
                foreach (var e in entries)
                {
                    var en = z.CreateEntry(e.name);
                    using (var w = new StreamWriter(en.Open())) w.Write(e.text);
                }
            return path;
        }
    }
}
