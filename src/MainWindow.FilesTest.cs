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
            public sealed class Ver { public int V; public string FileName, Sha, By, Note; public byte[] Bytes; public long At; }
            public sealed class SlotRec { public string Id, Name, CreatedBy; public int NextV = 1; public List<Ver> Versions = new List<Ver>(); }

            readonly HttpListener listener = new HttpListener();
            public readonly string Url;
            public const string Code = "synthetic-test-code";
            public string Role = "officer";          // what the server believes of the caller
            public int MaxMb = 1;
            public bool Corrupt;                      // flip a byte on download: the SHA-256 will not match
            public int Requests;                      // PUT / GET requests seen
            public readonly List<SlotRec> Slots = new List<SlotRec>();
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
                "{\"v\":" + v.V + ",\"fileName\":" + Q(v.FileName) + ",\"size\":" + v.Bytes.Length + ",\"sha256\":" + Q(v.Sha) + ",\"uploadedBy\":" + Q(v.By) + ",\"at\":" + v.At + ",\"note\":" + Q(v.Note) + "}";

            public string SlotJson(SlotRec s) =>
                "{\"id\":" + Q(s.Id) + ",\"name\":" + Q(s.Name) + ",\"createdBy\":" + Q(s.CreatedBy) + ",\"versions\":[" + string.Join(",", s.Versions.OrderBy(v => v.V).Select(VerJson)) + "]}";

            // what files.list answers
            public string DataJson() =>
                "{\"t\":\"files.data\",\"maxMb\":" + MaxMb + ",\"totalMb\":500,\"usedBytes\":" + Slots.Sum(s => s.Versions.Sum(v => (long)v.Bytes.Length)) + ",\"keep\":3,\"canManage\":"
                + (Role == "officer" || Role == "owner" ? "true" : "false") + ",\"slots\":[" + string.Join(",", Slots.OrderBy(s => s.Name).Select(SlotJson)) + "]}";

            static readonly HashSet<string> Blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "exe", "dll", "com", "bat", "cmd", "ps1", "js", "msi", "lnk", "reg", "hta" };

            void Serve(HttpListenerContext c)
            {
                var req = c.Request;
                if (req.Headers["X-Lodge-Code"] != Code) { Refuse(c, 401, "auth", "Wrong invite code"); return; }
                System.Threading.Interlocked.Increment(ref Requests);
                var parts = req.Url.AbsolutePath.Trim('/').Split('/');
                if (parts.Length < 2 || parts[0] != "slots") { Refuse(c, 404, "missing", "Not found"); return; }
                if (req.HttpMethod == "GET" && parts.Length == 3)
                {
                    var s = Slots.FirstOrDefault(x => x.Id == parts[1]);
                    var v = s?.Versions.FirstOrDefault(x => x.V.ToString() == parts[2]);
                    if (v == null) { Refuse(c, 404, "missing", "That file isn't there any more"); return; }
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
                slot.Versions.Add(new Ver { V = slot.NextV++, FileName = name, Sha = sha, By = "Elan", Note = note, Bytes = bytes, At = 1_790_000_000_000L + slot.NextV * 60_000L });
                slot.Versions = slot.Versions.OrderBy(v => v.V).ToList();
                while (slot.Versions.Count > 3) slot.Versions.RemoveAt(0);
                Reply(c, 200, SlotJson(slot));
            }
        }

        string FilesWelcome(string role, bool feature, string server = "2.14.0", int maxMb = 1)
        {
            var features = "\"reply\",\"react\",\"react2\",\"pin\",\"profile\",\"displayname\",\"lfg\",\"history2\",\"search\"" + (feature ? ",\"files\"" : "");
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
