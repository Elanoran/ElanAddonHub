using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // --selftest "atlaslodge": the Atlas pool of a Lodge (server 2.15, feature "atlas"). Everything runs on fixture folders under the scratch directory and
    // synthetic Lodge frames (no server, no game, never the real WoW folder):
    //   the Hub module `atlas.obs` (parsing, validation of every field, too new / absent), the delta computation (signatures, batch limits, the 56 KB frame,
    //   the revealed-items hook), the persisted record of what was uploaded, the upload batches of a session (once per 3 minutes, only what changed),
    //   the aggregate (paged answers, validation of what the server says, the cache file, the read API: drops, vendors, finds, spawns, revealed items),
    //   sharing switched off, guests, a server that disabled the feature, and an older server (nothing at all).
    public partial class MainWindow
    {
        const long AtlasAt = 1_790_000_000L;
        string atlasRoot;
        int atlasStamp;
        string AtlasHubFile(string account) => Path.Combine(atlasRoot, "_classic_beta_", "WTF", "Account", account, "SavedVariables", "ElansHub.lua");

        static string AtlasLua(string data, int v = 1) =>
            "ElansHubData = { schema = 1, chars = { [\"SECRETCHAR-Realm\"] = { at = 1, mods = {} } }, account = { mods = { [\"atlas.obs\"] = { v = " + v + ", at = 1, by = \"ElansToolkit\", ver = \"0.3.0\", data = { since = " + AtlasAt + ", " + data + " } } } } }\n";

        void WriteAtlasHub(string account, string lua)
        {
            var f = AtlasHubFile(account);
            Directory.CreateDirectory(Path.GetDirectoryName(f));
            File.WriteAllText(f, lua);
            File.SetLastWriteTimeUtc(f, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(++atlasStamp));
            AtlasObs.ResetCache();
        }

        string AtlasFixture()
        {
            long A = AtlasAt;
            return "kills = { [100] = { n = 10, items = { [7] = { n = 3, c = 4 }, [8] = { n = 1, c = 1 } }, z = { [1429] = 10 } }, [101] = { n = 5 },"
                + " [102] = { n = 5, items = { [7] = { n = 51, c = 51 } } }, [103] = { n = 10, z = { [1429] = 7, [1430] = 7 } },"
                + " [\"abc\"] = { n = 1 }, [0] = { n = 1 }, [104] = { n = -1 }, [105] = { n = 100001 }, [106] = \"text\" },\n"
                + "objects = { [50] = { n = 4, items = { [11] = { n = 4, c = 9 } }, z = { [1429] = 4 } } },\n"
                + "fishing = { [1429] = { n = 20, items = { [13] = { n = 6, c = 6 } } } },\n"
                + "vendors = { [300] = { at = " + A + ", z = 1429, x = 0.5, y = 0.5, items = { [4540] = { p = 25, s = 1, l = -1 }, [4541] = { p = -1, s = 1, l = -1 } } },"
                + " [301] = { at = " + A + ", z = 1429, x = 1.5, y = 0.5, items = {} } },\n"
                + "quests = { [900] = { at = " + A + ", rewards = { 1, 2 }, choices = { 3 } }, [901] = { at = 5, rewards = { 1 } } },\n"
                + "spawns = { [100] = { { z = 1429, x = 0.5, y = 0.5 }, { z = 1429, x = 0.505, y = 0.505 }, { z = 1429, x = 0.7, y = 0.7 }, { z = 0, x = 0.1, y = 0.1 } } },\n"
                + "finds = { { i = 7, c = 1, src = \"npc:100\", z = 1429, x = 0.5, y = 0.5, at = " + A + " }, { i = 8, c = 2, src = \"fish\", at = " + (A + 1) + " },"
                + " { i = 0, c = 1, src = \"fish\", at = " + A + " }, { i = 7, c = 1, src = \"mob:5\", at = " + A + " } }";
        }

        string AtlasWelcome(string role, string server, bool feature, bool seen = false, bool details = false) =>
            FunWelcome(role, FunPeople(false), "", "\"reply\",\"react\",\"react2\",\"pin\",\"profile\",\"displayname\",\"lfg\",\"history2\",\"search\"" + (feature ? ",\"atlas\"" : "") + (seen ? ",\"atlas2\"" : "") + (details ? ",\"atlas7\"" : ""), server);

        sealed class FakeRevealed : AtlasObs.IRevealedItemsSource
        {
            public List<AtlasObs.Revealed> List = new List<AtlasObs.Revealed>();
            public IEnumerable<AtlasObs.Revealed> Items() { return List; }
        }

        async Task<string> AtlasLodgeTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            Action<string, bool> check = (what, cond) => { notes.Add((cond ? "ok   " : "FAIL ") + what); ok &= cond; };
            var hadRoot = settings.WowRoot; var hadOff = settings.AtlasShareOff; var hadClock = Session.Clock; var hadSource = AtlasObs.RevealedSource; var hadSeen = AtlasObs.SeenSource;
            var sent = new List<Dictionary<string, object>>();
            var now = new DateTime(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);
            atlasRoot = Path.Combine(dir, "atlaswow");
            string sentFile = null, cacheFile = null;
            try
            {
                if (Directory.Exists(atlasRoot)) Directory.Delete(atlasRoot, true);
                Session.Disconnect();
                check("fixture: the WoW folder of this test is under the scratch folder (never the real game)", Path.GetFullPath(atlasRoot).StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase));
                settings.WowRoot = atlasRoot; settings.AtlasShareOff = false;
                Session.Clock = () => now;
                Session.SentForTest = o => sent.Add(o);
                AtlasObs.TestSource = null; AtlasObs.RevealedSource = AtlasObs.Empty;
                Session.AtlasResetForTest();
                sentFile = Session.AtlasSentPathForTest; cacheFile = Session.AtlasCachePathForTest;
                try { File.Delete(sentFile); File.Delete(cacheFile); } catch { }
                Directory.CreateDirectory(Path.Combine(atlasRoot, "_classic_beta_", "WTF", "Account", "ACC1", "SavedVariables"));

                AtlasModuleRead(check);
                AtlasBatching(check);
                AtlasFindsRegression(check);
                AtlasSectionsRegression(check);
                await AtlasSession(check, sent, () => now, t => now = t);
                await AtlasSeenTest(check, sent, () => now, t => now = t);
                await AtlasSeenDetailsTest(check, sent);
                await AtlasNotesTest(check, sent, () => now, t => now = t);
                AtlasNpcSpotsTest(check, sent);
                AtlasFindsAccuracyTest(check);
            }
            catch (Exception e) { ok = false; notes.Add("crashed: " + e); }
            finally
            {
                Session.Disconnect();
                Session.SentForTest = null; Session.Clock = hadClock; settings.WowRoot = hadRoot; settings.AtlasShareOff = hadOff;
                AtlasObs.TestSource = null; AtlasObs.RevealedSource = hadSource; AtlasObs.SeenSource = hadSeen; Session.AtlasResetForTest();
                foreach (var f in new[] { sentFile, cacheFile })
                {
                    if (f == null) continue;
                    try { File.Delete(f); } catch { }
                    try { File.Delete(f + ".tmp"); } catch { }
                }
            }
            return "atlaslodge selftest: " + (ok ? "PASSED" : "FAILED") + "\r\n  " + string.Join("\r\n  ", notes);
        }

        // ---------------------------------------------------------------- the Hub module: parse + validate
        void AtlasModuleRead(Action<string, bool> check)
        {
            check("hub: the supported table lists atlas.obs at v1", HubData.Supported.TryGetValue("atlas.obs", out var av) && av == 1);
            check("hub: no ElansHub.lua -> no snapshot", AtlasObs.Read(atlasRoot) == null);
            WriteAtlasHub("ACC1", AtlasLua(AtlasFixture()));
            var s = AtlasObs.Read(atlasRoot);
            check("hub: the module is read", s != null && s.Since == AtlasAt);
            if (s == null) return;
            check("hub: kills: 4 valid sources kept (100, 101, 102, 103); text keys, id 0, n < 0, n over the cap and a non-table are left out", s.Kills.Count == 4 && s.Kills.ContainsKey(100) && !s.Kills.ContainsKey(0) && !s.Kills.ContainsKey(104) && !s.Kills.ContainsKey(105) && !s.Kills.ContainsKey(106));
            check("hub: kills: counts, drops and zones are read", s.Kills[100].N == 10 && s.Kills[100].Items[7][0] == 3 && s.Kills[100].Items[7][1] == 4 && s.Kills[100].Items[8][0] == 1 && s.Kills[100].Zones[1429] == 10);
            check("hub: kills: 51 drops from 5 kills is impossible -> that item is left out, the source stays", s.Kills[102].N == 5 && s.Kills[102].Items.Count == 0);
            check("hub: kills: zones that add up to more than the kills are left out, the source stays", s.Kills[103].N == 10 && s.Kills[103].Zones.Count == 0);
            check("hub: objects and fishing zones", s.Objects[50].Items[11][1] == 9 && s.Fishing[1429].N == 20 && s.Fishing[1429].Items[13][0] == 6);
            check("hub: vendors: a negative price drops the item, an x above 1 drops the vendor", s.Vendors.Count == 1 && s.Vendors[300].Items.Count == 1 && s.Vendors[300].Items[4540][0] == 25 && s.Vendors[300].Items[4540][2] == -1);
            check("hub: quests: an observation from before 2001 is left out", s.Quests.Count == 1 && s.Quests[900].Rewards.SequenceEqual(new[] { 1, 2 }) && s.Quests[900].Choices.SequenceEqual(new[] { 3 }));
            check("hub: spawn points: a point next to another is the same, a zone 0 point is left out", s.Spawns[100].Count == 2);
            check("hub: finds: item 0 and an unknown source are left out", s.Finds.Count == 2 && s.Finds[0].Src == "npc:100" && s.Finds[1].Zone == 0);
            check("hub: the validation counted what it left out", s.Dropped >= 12);

            // too new / another version / absent
            WriteAtlasHub("ACC1", AtlasLua(AtlasFixture(), 2));
            check("hub: a module newer than the app understands is not read (TooNew)", AtlasObs.Read(atlasRoot) == null && HubData.Read(atlasRoot).AccountModules["atlas.obs"].TooNew);
            WriteAtlasHub("ACC1", "ElansHubData = { schema = 1, chars = {}, account = { mods = {} } }\n");
            check("hub: a Hub file without the module -> no snapshot", AtlasObs.Read(atlasRoot) == null);
            WriteAtlasHub("ACC1", AtlasLua("kills = 5, vendors = \"x\", finds = { 3, \"x\", { } }, spawns = { [1] = 7 }"));
            var junk = AtlasObs.Read(atlasRoot);
            check("hub: garbage tables give an empty snapshot, never an exception", junk != null && junk.IsEmpty && junk.Dropped > 0);

            // limits: more entries than the server keeps
            var many = new StringBuilder("kills = { ");
            for (int i = 1; i <= AtlasObs.MaxKillSources + 10; i++) many.Append("[" + i + "] = { n = 1 }, ");
            many.Append("}");
            WriteAtlasHub("ACC1", AtlasLua(many.ToString()));
            var big = AtlasObs.Read(atlasRoot);
            check("hub: at most 6000 kill sources are read", big != null && big.Kills.Count == AtlasObs.MaxKillSources && big.Dropped == 10);

            // the cached conversion: the same Hub parse gives the same snapshot object
            check("hub: an unchanged file is converted once", ReferenceEquals(AtlasObs.Read(atlasRoot), AtlasObs.Read(atlasRoot)));
            WriteAtlasHub("ACC1", AtlasLua(AtlasFixture()));
        }

        // ---------------------------------------------------------------- deltas and batches
        void AtlasBatching(Action<string, bool> check)
        {
            var s = AtlasObs.Read(atlasRoot);
            var sent = new Dictionary<string, string>();
            var b = AtlasObs.MakeBatch(s, null, sent);
            check("batch: the first batch carries all 11 entries (4 kills, 1 object, 1 fishing zone, 1 vendor, 1 quest, 1 spawn list, 2 finds)", b != null && b.Entries == 11 && b.Remaining == 0 && b.Sigs.Count == 11);
            var f = b.Frame;
            check("batch: it is an atlas.share with the architecture's sections", f.Str("t") == "atlas.share" && f.Child("kills").Count == 4 && f.Child("objects").Count == 1 && f.Child("fishing").Count == 1 && f.Child("vendors").Count == 1
                && f.Child("quests").Count == 1 && f.Child("spawns").Count == 1 && f.List("finds").Count == 2);
            var json = Json.Write(f);
            check("batch: nothing from the Hub file but numbers: no character name, no account", !json.Contains("SECRETCHAR") && !json.Contains("ACC1") && !json.Contains("Realm"));
            var kill100 = f.Child("kills").Child("100");
            check("batch: counters are cumulative totals (n, items {n, c}, zones)", kill100.NLong("n") == 10 && kill100.Child("items").Child("7").NLong("n") == 3 && kill100.Child("items").Child("7").NLong("c") == 4 && kill100.Child("z").NLong("1429") == 10);
            check("batch: once recorded as sent nothing is left to send", AtlasObs.MakeBatch(s, null, b.Sigs) == null);

            // one counter grows -> only that entry; a new find -> only that find
            WriteAtlasHub("ACC1", AtlasLua(AtlasFixture().Replace("[101] = { n = 5 }", "[101] = { n = 6 }")));
            var s2 = AtlasObs.Read(atlasRoot);
            var d = AtlasObs.MakeBatch(s2, null, b.Sigs);
            check("delta: a source that grew is the only entry", d != null && d.Entries == 1 && d.Frame.Child("kills").Count == 1 && d.Frame.Child("kills").Child("101").NLong("n") == 6 && !d.Frame.ContainsKey("finds"));
            WriteAtlasHub("ACC1", AtlasLua(AtlasFixture().Replace("finds = { {", "finds = { { i = 9, c = 1, src = \"obj:50\", at = " + (AtlasAt + 50) + " }, {")));
            var d2 = AtlasObs.MakeBatch(AtlasObs.Read(atlasRoot), null, b.Sigs);
            check("delta: a new find is the only entry (finds are keyed by what they are, not by position)", d2 != null && d2.Entries == 1 && d2.Frame.List("finds").Count == 1 && ((Dictionary<string, object>)d2.Frame.List("finds")[0]).NLong("i") == 9);
            WriteAtlasHub("ACC1", AtlasLua(AtlasFixture()));

            // limits per message and the 56 KB frame
            var snap = new AtlasObs.Snapshot();
            for (int i = 1; i <= 250; i++) snap.Kills[i] = new AtlasObs.Counter { N = 1 };
            var big = AtlasObs.MakeBatch(snap, null, new Dictionary<string, string>());
            check("limits: 250 changed kill sources -> 200 in the batch, 50 wait", big.Entries == AtlasObs.MaxKillsPerShare && big.Remaining == 50 && big.Frame.Child("kills").Count == 200);
            var heavy = new AtlasObs.Snapshot();
            for (int i = 1; i <= 190; i++)
            {
                var c = new AtlasObs.Counter { N = 100 };
                for (int k = 1; k <= 150; k++) c.Items[k + i * 1000] = new long[] { 5, 5 };
                heavy.Kills[i] = c;
            }
            var hb = AtlasObs.MakeBatch(heavy, null, new Dictionary<string, string>());
            check("limits: a heavy batch stays under 56 KB and leaves the rest for later", Json.Write(hb.Frame).Length <= AtlasObs.MaxShareBytes && hb.Remaining > 0 && hb.Entries > 0);
            var rest = AtlasObs.MakeBatch(heavy, null, hb.Sigs);
            check("limits: the rest follows in the next batch", rest != null && rest.Entries + hb.Entries <= 190 && rest.Entries > 0);

            // the hook for revealed items (another layer implements it later)
            check("revealed: the default source reveals nothing", !AtlasObs.Empty.Items().Any() && ReferenceEquals(AtlasObs.RevealedSource, AtlasObs.Empty));
            var rev = new[]
            {
                new AtlasObs.Revealed { Id = 2589, Name = "Linen Cloth", Quality = 1, ItemLevel = 5, Stats = new[] { new[] { 3, 5 } } },
                new AtlasObs.Revealed { Id = 0, Name = "Zero", Quality = 1 },
                new AtlasObs.Revealed { Id = 2592, Name = "Bad \"quality\"", Quality = 9 },
                new AtlasObs.Revealed { Id = 2593, Name = "", Quality = 1 },
                new AtlasObs.Revealed { Id = 2594, Name = "Evil\" [[x]] \\ name" + new string('N', 200), Quality = 2 },
            };
            var rb = AtlasObs.MakeBatch(null, rev, new Dictionary<string, string>());
            var items = rb?.Frame.Child("items");
            check("revealed: valid details go out as 'items' (2 of 5; bad id, quality 9 and an empty name are left out)", rb != null && items.Count == 2 && items.Child("2589").Str("n") == "Linen Cloth" && items.Child("2589").NLong("q") == 1);
            var nm = items.Child("2594").Str("n");
            check("revealed: a name is cleaned (no quote, backslash or bracket) and cut at 80", nm.Length <= 80 && !nm.Contains("\"") && !nm.Contains("\\") && !nm.Contains("["));
            check("revealed: sent details are not sent again", AtlasObs.MakeBatch(null, rev, rb.Sigs) == null);
        }

        // ---------------------------------------------------------------- finds: a long list shaped like a real one (regression: new finds must reach the pool)
        static string FindsLua(int count, long first, int start = 0)
        {
            var sb = new StringBuilder("finds = { ");
            for (int n = 0; n < count; n++)
            {
                int k = start + n; long at = first + k * 37;
                switch (k % 9)
                {
                    case 0: case 1: case 2: sb.Append("{ i = " + (1000 + k) + ", c = 1, src = \"fish\", z = 1456, x = 0.416, y = 0.594, at = " + at + " }, "); break;
                    case 3: case 4: case 5: sb.Append("{ i = " + (2000 + k) + ", c = " + (1 + k % 3) + ", src = \"npc:" + (3100 + k % 40) + "\", z = 1411, x = 0." + (100 + k % 800) + ", y = 0." + (100 + k % 700) + ", at = " + at + " }, "); break;
                    case 6: sb.Append("{ i = " + (3000 + k) + ", c = 1, src = \"obj:" + (1731 + k % 5) + "\", z = 1411, x = 0.5, y = 0.5, at = " + at + " }, "); break;
                    case 7: sb.Append("{ i = " + (4000 + k) + ", c = 1, src = \"quest:" + (500 + k) + "\", at = " + at + " }, "); break;          // no position (instance)
                    default: sb.Append("{ i = " + (5000 + k) + ", c = 1, src = \"npc:" + (7000 + k % 9) + "\", at = " + at + " }, "); break;        // no position (instance)
                }
            }
            return sb.Append("}").ToString();
        }

        void AtlasFindsRegression(Action<string, bool> check)
        {
            long t0 = AtlasAt + 100000;
            WriteAtlasHub("ACC1", AtlasLua(FindsLua(344, t0)));
            var s = AtlasObs.Read(atlasRoot);
            check("finds: 344 mixed finds (fish, npc, obj, quest, with and without a position) are all read, none left out", s != null && s.Finds.Count == 344 && s.Dropped == 0 && s.FindsOverCap == 0);
            check("finds: the newest find is among them", s.Finds.Max(f => f.At) == t0 + 343 * 37 && s.Finds.Count(f => f.Zone == 0) > 60);
            var sent = new Dictionary<string, string>();
            var b = AtlasObs.MakeBatch(s, null, sent);
            check("finds: all 344 go out in one share (cap 500), oldest first", b != null && b.Finds == 344 && b.Frame.List("finds").Count == 344 && b.Remaining == 0 && ((Dictionary<string, object>)b.Frame.List("finds")[343]).NLong("at") == t0 + 343 * 37);
            // 12 new finds appended later (the Toolkit appends at the end): exactly those go out next
            WriteAtlasHub("ACC1", AtlasLua(FindsLua(356, t0)));
            var s2 = AtlasObs.Read(atlasRoot);
            var d = AtlasObs.MakeBatch(s2, null, b.Sigs);
            check("finds: 12 finds recorded later are the only entries of the next share", s2.Finds.Count == 356 && d != null && d.Finds == 12 && d.Entries == 12);
            // the Toolkit drops its oldest at 500; a longer list than that keeps the newest 500
            WriteAtlasHub("ACC1", AtlasLua(FindsLua(620, t0)));
            var s3 = AtlasObs.Read(atlasRoot);
            check("finds: a list of 620 keeps the NEWEST 500 and counts the 120 oldest as left out", s3.Finds.Count == AtlasObs.MaxHubFinds && s3.FindsOverCap == 120 && s3.Dropped == 120 && s3.Finds.Max(f => f.At) == t0 + 619 * 37 && s3.Finds.Min(f => f.At) == t0 + 120 * 37);
            // a find the validation refuses is counted, the rest stay
            WriteAtlasHub("ACC1", AtlasLua("finds = { { i = 5, c = 1, src = \"fishspot\", at = " + t0 + " }, { i = 6, c = 1, src = \"fish\", z = 1456, at = " + t0 + " }, { i = 7, c = 1, src = \"fish\", at = " + (t0 + 1) + " } }"));
            var s4 = AtlasObs.Read(atlasRoot);
            check("finds: an unknown source and a half position are counted as left out, a find without position stays", s4.Finds.Count == 1 && s4.Dropped == 2 && s4.Finds[0].Item == 7);

            // finds marked as sent that the Lodge pool does not hold are sent again (once), the ones it holds are not
            Session.AtlasResetForTest();
            var local = new AtlasObs.Snapshot();
            for (int i = 0; i < 5; i++) local.Finds.Add(new AtlasObs.Find { Item = 10 + i, Count = 1, Src = "npc:1", Zone = 11, X = 0.5, Y = 0.5, At = AtNow + i });
            var sentRec = Session.AtlasSentForTest;
            foreach (var f in local.Finds) sentRec[AtlasObs.FindKey(f)] = "1";
            Session.AtlasData.Apply(Json.Obj("{\"t\":\"atlas.data\",\"epoch\":\"ex\",\"rev\":3,\"at\":" + AtNow + ",\"full\":true,\"finds\":["
                + "{\"i\":10,\"c\":1,\"at\":" + AtNow + ",\"src\":\"npc:1\",\"z\":11,\"x\":0.5,\"y\":0.5,\"by\":\"Me\",\"s\":1},{\"i\":12,\"c\":1,\"at\":" + (AtNow + 2) + ",\"src\":\"npc:1\",\"z\":11,\"x\":0.5,\"y\":0.5,\"by\":\"Me\",\"s\":2}]}"));
            int again = Session.AtlasResendMissingForTest(local);
            check("resend: 3 of 5 finds were marked as sent but are not in the pool -> forgotten (sent again), the 2 stored ones stay", again == 3 && Session.AtlasBacklogForTest && sentRec.Count == 2);
            var re = AtlasObs.MakeBatch(local, null, sentRec);
            check("resend: the next share holds exactly those 3 finds", re != null && re.Finds == 3);
            check("resend: asked only once per find (nothing again when the pool still lacks them)", Session.AtlasResendMissingForTest(local) == 0);
            Session.AtlasResetForTest();
            WriteAtlasHub("ACC1", AtlasLua(AtlasFixture()));
        }

        // ---------------------------------------------------------------- every section, shaped like the Toolkit writes it (regression: what is read, what is shared, what is counted as left out)
        void AtlasSectionsRegression(Action<string, bool> check)
        {
            long A = AtlasAt + 5000;
            string data =
                "kills = { [3100] = { n = 79, items = { [769] = { n = 67, c = 67 }, [771] = { n = 18, c = 18 } }, z = { [1411] = 79 } }, [3101] = { n = 2, items = { [5] = { n = 9, c = 9 } } } },\n"
                + "objects = { [1731] = { n = 16, items = { [2770] = { n = 12, c = 14 } }, z = { [1411] = 16 } } },\n"
                + "fishing = { [1456] = { n = 115, items = { [6289] = { n = 64, c = 64 } } } },\n"
                + "vendors = { [7683] = { at = " + A + ", z = 1458, x = 0.582, y = 0.55, items = { [8523] = { p = 0, s = 1, l = -1 }, [8524] = { p = 25000, s = 1, l = -1 }, [8525] = { p = 0, s = 1, l = -1, x = 1 }, [8526] = { p = 5, s = 1, l = -1, x = 7 } } },"
                + " [7684] = { at = " + A + ", items = { [1] = { p = 1, s = 1, l = -1 } } } },\n"
                + "quests = { [1144] = { at = " + A + ", rewards = { }, choices = { 6748, 6750, 6749, 274078 } } },\n"
                + "spawns = { [3100] = { { z = 1411, x = 0.5, y = 0.5 }, { z = 1411, x = 0.6, y = 0.6 } } },\n"
                + "nodes = { [1731] = { [1411] = { { x = 0.4123, y = 0.5531, n = 3, at = " + A + " }, { x = 0.4125, y = 0.5533, n = 5, at = " + A + " }, { x = 2, y = 0.5, n = 1 } } } },\n"
                + "fishspots = { [1456] = { { x = 0.4161, y = 0.5942, n = 12, at = " + A + ", items = { [6289] = 7, [6291] = 5, [0] = 1 } }, { x = 0.4163, y = 0.5944, n = 20, items = { [6289] = 9 } }, { x = 0.9, y = 0.9, n = 0 } }, [0] = { { x = 0.1, y = 0.1, n = 1 } } },\n"
                + FindsLua(40, A) + ",\n"
                + "mystery = { 1, 2 }";
            WriteAtlasHub("ACC1", AtlasLua(data));
            var s = AtlasObs.Read(atlasRoot);
            int[] St(string k) => s.Stats[k];
            check("sections: kills / objects / fishing: all entries read, nothing left out", St("kills")[0] == 2 && St("kills")[1] == 2 && St("kills")[2] == 0 && St("objects")[1] == 1 && St("fishing")[1] == 1 && St("fishing")[2] == 0);
            check("sections: vendors: the one without a position is left out (1 entry + the x = 7 item), the other is read", St("vendors")[0] == 2 && St("vendors")[1] == 1 && St("vendors")[2] == 2);
            check("sections: vendor item flag x = 1 is read as extended cost, absent is 0, price 0 is kept", s.Vendors[7683].Items[8525][3] == 1 && s.Vendors[7683].Items[8524][3] == 0 && s.Vendors[7683].Items[8524][0] == 25000 && s.Vendors[7683].Items[8523][0] == 0 && !s.Vendors[7683].Items.ContainsKey(8526));
            check("sections: quests with empty rewards and 4 choices, spawns", St("quests")[1] == 1 && s.Quests[1144].Choices.Length == 4 && St("spawns")[1] == 1 && s.Spawns[3100].Count == 2);
            check("sections: nodes: two points inside the merge radius are one, an x of 2 is left out", St("nodes")[1] == 1 && s.Nodes[1731][1411].Count == 1 && s.Nodes[1731][1411][0].N == 5 && St("nodes")[2] == 1);
            check("sections: fishspots: read (4 decimals), two spots within 0.01 are one with the larger n and both items, a bad item / n = 0 / map 0 are left out",
                St("fishspots")[0] == 2 && St("fishspots")[1] == 1 && s.FishSpots[1456].Count == 1 && s.FishSpots[1456][0].N == 20 && s.FishSpots[1456][0].Items[6289] == 9 && s.FishSpots[1456][0].Items[6291] == 5 && !s.FishSpots[1456][0].Items.ContainsKey(0)
                && St("fishspots")[2] == 3);
            check("sections: finds 40 read, and a section this app does not read is named", St("finds")[1] == 40 && s.UnknownSections.SequenceEqual(new[] { "mystery" }));

            // what is shared: fishspots only to a server that takes them; the x flag and the corrected price are in the frame
            var withFish = AtlasObs.MakeBatch(s, null, new Dictionary<string, string>(), false, true, true);
            var noFish = AtlasObs.MakeBatch(s, null, new Dictionary<string, string>(), false, true, false);
            check("share: fishspots go out only when the server has atlas4", withFish.Frame.ContainsKey("fishspots") && withFish.Counts["fishspots"] == 1 && !noFish.Frame.ContainsKey("fishspots"));
            var vend = withFish.Frame.Child("vendors").Child("7683").Child("items");
            check("share: the vendor items carry x only when 1, and price 0 stays 0", vend.Child("8525").NLong("x") == 1 && !vend.Child("8524").ContainsKey("x") && vend.Child("8524").NLong("p") == 25000);
            var fs = withFish.Frame.Child("fishspots").List("1456");
            check("share: a fishing spot is { x, y, n, items }", fs.Count == 1 && ((Dictionary<string, object>)fs[0]).NLong("n") == 20 && ((Dictionary<string, object>)fs[0]).Child("items").NLong("6289") == 9);
            check("share: the upload round-trips through the server's rules (same parser) and is not sent twice", AtlasObs.MakeBatch(s, null, withFish.Sigs, false, true, true) == null);

            // Toolkit 0.5.1 wrote every price as 0; 0.5.2 writes the real price (newer scan): the vendor is an entry that changed -> sent again
            string fixedData = data.Replace("[8523] = { p = 0, s = 1, l = -1 }", "[8523] = { p = 4400, s = 1, l = -1 }").Replace("at = " + A + ", z = 1458", "at = " + (A + 600) + ", z = 1458");
            WriteAtlasHub("ACC1", AtlasLua(fixedData));
            var d = AtlasObs.MakeBatch(AtlasObs.Read(atlasRoot), null, withFish.Sigs, false, true, true);
            check("share: corrected vendor prices (newer scan) are sent again as the only entry", d != null && d.Entries == 1 && d.Frame.Child("vendors").Child("7683").Child("items").Child("8523").NLong("p") == 4400);
            // the record from before the x flag existed (hash without x) still matches an unchanged vendor: no resend storm
            var oldVendor = AtlasObs.Sig(new AtlasObs.Vendor { At = A, Zone = 1, X = 0.1, Y = 0.1, Items = { [1] = new long[] { 5, 1, -1 } } });
            var newVendor = AtlasObs.Sig(new AtlasObs.Vendor { At = A, Zone = 1, X = 0.1, Y = 0.1, Items = { [1] = new long[] { 5, 1, -1, 0 } } });
            check("share: a vendor without any extended-cost item has the same signature as before the flag (the upload record stays valid)", oldVendor == newVendor);
            // the pool: the server's merged fishing spots are read, kept in the cache and handed to the maps
            var lodge = new ElansAddonHub.Lodge.LodgeAtlas();
            lodge.Apply(Json.Obj("{\"t\":\"atlas.data\",\"epoch\":\"e9\",\"rev\":5,\"at\":" + (AtlasAt + 9) + ",\"full\":true,\"fishspots\":{\"1456\":[{\"x\":0.4161,\"y\":0.5942,\"n\":32,\"items\":{\"6289\":16}},{\"x\":5,\"y\":0.1,\"n\":1}],\"0\":[{\"x\":0.1,\"y\":0.1,\"n\":1}]},"
                + "\"vendors\":{\"7683\":{\"at\":" + AtlasAt + ",\"z\":1458,\"x\":0.5,\"y\":0.5,\"items\":{\"8525\":{\"p\":0,\"s\":1,\"l\":-1,\"x\":1},\"8524\":{\"p\":7,\"s\":1,\"l\":-1}}}}}"));
            check("pool: fishing spots of an answer are read (a bad spot and map 0 left out, 2 dropped), also after the cache round trip",
                lodge.FishSpotsOn(1456).Count == 1 && lodge.FishSpotsOn(1456)[0].N == 32 && lodge.FishSpotsOn(1456)[0].Items[6289] == 16 && lodge.Dropped == 2
                && Json.Write(lodge.ToCache()).Contains("\"fishspots\":{\"1456\":[{\"x\":0.416"));
            check("pool: the extended-cost flag of a vendor item is kept and offered by the item rows", lodge.Vendors(8525).Count == 1 && lodge.Vendors(8525)[0].Extended && !lodge.Vendors(8524)[0].Extended && lodge.Vendors(8524)[0].Price == 7);
            // the maps: the Lodge's spots plus this PC's own that no Lodge spot covers
            var hadLodge = ElansAddonHub.Services.Atlas.AtlasQuery.Context.Lodge; var hadOwnFish = ElansAddonHub.Services.Atlas.AtlasQuery.Context.OwnFishSpots;
            try
            {
                ElansAddonHub.Services.Atlas.AtlasQuery.Context.Lodge = () => lodge;
                ElansAddonHub.Services.Atlas.AtlasQuery.Context.OwnFishSpots = () => new Dictionary<int, List<AtlasObs.FishSpot>>
                {
                    [1456] = new List<AtlasObs.FishSpot> { new AtlasObs.FishSpot { X = 0.4165, Y = 0.5945, N = 3 }, new AtlasObs.FishSpot { X = 0.7, Y = 0.7, N = 2, Items = { [6291] = 2 } } },
                };
                var on = ElansAddonHub.Services.Atlas.GatherNodes.FishSpotsOn(1456);
                check("maps: fishing spots = the Lodge's merged spot + this PC's own spot that the Lodge's does not cover (the one next to it is the same spot)", on.Count == 2 && on[0].N == 32 && on[1].N == 2 && ElansAddonHub.Services.Atlas.GatherNodes.FishSpotMaps().SequenceEqual(new[] { 1456 }));
                check("maps: the tip names the catches and the most caught items", ElansAddonHub.Services.Atlas.AtlasQuery.FishSpotTip(on[0]).StartsWith("32 catches") && ElansAddonHub.Services.Atlas.AtlasQuery.FishSpotTip(on[1]).Contains("×2"));
            }
            finally { ElansAddonHub.Services.Atlas.AtlasQuery.Context.Lodge = hadLodge; ElansAddonHub.Services.Atlas.AtlasQuery.Context.OwnFishSpots = hadOwnFish; }
            WriteAtlasHub("ACC1", AtlasLua(AtlasFixture()));
        }

        // ---------------------------------------------------------------- the session
        static List<Dictionary<string, object>> AFrames(List<Dictionary<string, object>> sent, string t) => sent.Where(o => o.Str("t") == t).ToList();

        string AtlasPage(string epoch, long rev, string body, bool full = false, bool more = false) =>
            "{\"t\":\"atlas.data\",\"epoch\":\"" + epoch + "\",\"rev\":" + rev + ",\"at\":" + (AtlasAt + 100) + (full ? ",\"full\":true" : "") + (more ? ",\"more\":true" : "") + "," + body + "}";

        async Task AtlasSession(Action<string, bool> check, List<Dictionary<string, object>> sent, Func<DateTime> getNow, Action<DateTime> setNow)
        {
            WriteAtlasHub("ACC1", AtlasLua(AtlasFixture()));
            var sentFile = Session.AtlasSentPathForTest; var cacheFile = Session.AtlasCachePathForTest;

            // ---- a current server, sharing on
            sent.Clear();
            Session.FeedForTest(AtlasWelcome("member", "2.15.0", true));
            check("session: the 'atlas' feature of a 2.15 server is detected", Session.SupportsAtlas);
            var shares = AFrames(sent, "atlas.share");
            check("connect: right after the welcome one atlas.share goes out with the 11 observed entries (and no fetch before its answer)", shares.Count == 1 && AFrames(sent, "atlas.get").Count == 0 && shares[0].Child("kills").Count == 4);
            check("connect: the upload carries no character name and no account", !Json.Write(shares[0]).Contains("SECRETCHAR") && !Json.Write(shares[0]).Contains("ACC"));
            Session.FeedForTest("{\"t\":\"atlas.shared\",\"ok\":true,\"accepted\":11,\"unchanged\":0,\"rejected\":0,\"rev\":40,\"epoch\":\"ep1\"}");
            var gets = AFrames(sent, "atlas.get");
            check("connect: after the lodge accepted them the aggregate is requested (atlas.get, first time without 'since')", gets.Count == 1 && !gets[0].ContainsKey("since"));
            check("connect: what was uploaded is remembered (11 entries), also on disk, and the file holds no names", Session.AtlasSentForTest.Count == 11 && File.Exists(sentFile) && !File.ReadAllText(sentFile).Contains("SECRET"));

            // ---- the aggregate: two pages (more = true), the second asked for with since + epoch
            sent.Clear();
            var page1 = AtlasPage("ep1", 30, "\"kills\":{\"100\":{\"n\":40,\"m\":2,\"items\":{\"7\":{\"n\":8,\"c\":9},\"8\":{\"n\":1,\"c\":1}},\"z\":{\"1429\":30,\"1430\":10}},\"101\":{\"n\":6,\"m\":1},\"999999999\":{\"n\":5},\"102\":{\"n\":\"x\"}},"
                + "\"objects\":{\"50\":{\"n\":9,\"m\":2,\"items\":{\"11\":{\"n\":9,\"c\":12}}}},\"fishing\":{\"1429\":{\"n\":25,\"m\":2,\"items\":{\"13\":{\"n\":7,\"c\":7}}}}", full: true, more: true);
            Session.FeedForTest(page1);
            gets = AFrames(sent, "atlas.get");
            check("download: a page with more = true is followed by the next request at once, with since and epoch", gets.Count == 1 && gets[0].NLong("since") == 30 && gets[0].Str("epoch") == "ep1");
            check("download: invalid sources of the answer are left out (huge id, string n)", Session.AtlasData.KillSources == 2 && Session.AtlasData.Dropped >= 2);
            sent.Clear();
            var page2 = AtlasPage("ep1", 41, "\"vendors\":{\"300\":{\"at\":" + AtlasAt + ",\"z\":1429,\"x\":0.4,\"y\":0.6,\"items\":{\"4540\":{\"p\":30,\"s\":1,\"l\":-1},\"4541\":{\"p\":80,\"s\":5,\"l\":10}}},\"301\":{\"at\":" + AtlasAt + ",\"z\":1429,\"x\":0.1,\"y\":0.1,\"items\":{\"4540\":{\"p\":20,\"s\":1,\"l\":-1}}}},"
                + "\"quests\":{\"900\":{\"at\":" + AtlasAt + ",\"rewards\":[1,2],\"choices\":[3]}},\"spawns\":{\"100\":[{\"z\":1429,\"x\":0.5,\"y\":0.5},{\"z\":1429,\"x\":0.7,\"y\":0.7},{\"z\":0,\"x\":1,\"y\":1}]},"
                + "\"items\":{\"2589\":{\"n\":\"Linen Cloth\",\"q\":1,\"il\":5,\"rl\":0,\"cls\":7,\"sub\":5,\"slot\":0,\"st\":[[3,5]]},\"2590\":{\"n\":\"x\",\"q\":9}},"
                + "\"finds\":[{\"s\":38,\"i\":7,\"c\":1,\"src\":\"npc:100\",\"at\":" + AtlasAt + ",\"by\":\"Amy the Bold\",\"z\":1429,\"x\":0.5,\"y\":0.5},{\"s\":40,\"i\":7,\"c\":2,\"src\":\"fish\",\"at\":" + (AtlasAt + 9) + ",\"by\":\"Bob\"},{\"s\":41,\"i\":8,\"c\":1,\"src\":\"bad\",\"at\":" + AtlasAt + ",\"by\":\"x\"},{\"s\":42,\"i\":8,\"c\":2,\"src\":\"fish\",\"at\":" + (AtlasAt + 1) + ",\"by\":\"Amy the Bold\"}]");
            Session.FeedForTest(page2);
            check("download: the last page asks for nothing more and the revision is kept", AFrames(sent, "atlas.get").Count == 0 && Session.AtlasData.Rev == 41 && Session.AtlasData.Epoch == "ep1");
            var a = Session.AtlasData;
            var drops = a.Drops(7);
            check("read API: Drops(item) -> source, kills, drops, chance in percent, zones (20 % of 40 kills = 8 drops)", drops.Count == 1 && drops[0].Source == "npc:100" && drops[0].Kills == 40 && drops[0].Drops == 8 && drops[0].Count == 9 && drops[0].Chance == 20 && drops[0].Members == 2
                && drops[0].Zones.Count == 2 && drops[0].Zones[0].Key == 1429 && drops[0].Zones[0].Value == 30);
            var fishDrops = a.Drops(13);
            check("read API: fishing and objects are sources too (fish:1429 28 %, obj:50 100 % for item 11)", fishDrops.Count == 1 && fishDrops[0].Source == "fish:1429" && fishDrops[0].Chance == 28 && a.Drops(11).Count == 1 && a.Drops(11)[0].Source == "obj:50" && a.Drops(11)[0].Chance == 100);
            check("read API: an item nobody saw drop has no rows", a.Drops(424242).Count == 0 && a.Vendors(424242).Count == 0 && a.Finds(424242).Count == 0);
            var vs = a.Vendors(4540);
            check("read API: Vendors(item) lists the vendors cheapest first", vs.Count == 2 && vs[0].NpcId == 301 && vs[0].Price == 20 && vs[1].NpcId == 300 && vs[1].Price == 30 && a.Vendors(4541)[0].Stack == 5 && a.Vendors(4541)[0].Limit == 10);
            var fs = a.Finds(7);
            check("read API: Finds(item) is newest first with the member's display name", fs.Count == 2 && fs[0].By == "Bob" && fs[0].Count == 2 && fs[1].By == "Amy the Bold" && fs[1].Zone == 1429 && a.FindCount == 3);
            check("read API: Spawns(npc) -> the valid points", a.Spawns(100).Count == 2 && a.Spawns(5).Count == 0);
            var rv = a.Revealed(2589);
            check("read API: Revealed(item) -> the validated details, invalid ones are left out", rv != null && rv.Name == "Linen Cloth" && rv.Quality == 1 && rv.ItemLevel == 5 && rv.Stats.Length == 1 && rv.Stats[0][1] == 5 && a.Revealed(2590) == null);
            check("read API: quests that reward an item", a.QuestsRewarding(2).Count == 1 && a.QuestsRewarding(3)[0].Choice && a.QuestsRewarding(99).Count == 0);
            check("download: the aggregate is cached on disk", File.Exists(cacheFile) && new FileInfo(cacheFile).Length > 200);

            // ---- a restart: the cache comes back, nothing is uploaded again, the request continues from the cached revision
            Session.Disconnect();
            Session.AtlasResetForTest();                    // forget everything in memory (the files stay)
            sent.Clear();
            Session.FeedForTest(AtlasWelcome("member", "2.15.0", true));
            check("restart: the cache is loaded (revision, epoch and data) before the lodge answers", Session.AtlasData.Rev == 41 && Session.AtlasData.Epoch == "ep1" && Session.AtlasData.Drops(7).Count == 1 && Session.AtlasData.Finds(7).Count == 2);
            check("restart: the upload record is read from disk: nothing to upload, the aggregate is asked for since the cached revision",
                AFrames(sent, "atlas.share").Count == 0 && AFrames(sent, "atlas.get").Count == 1 && AFrames(sent, "atlas.get")[0].NLong("since") == 41 && AFrames(sent, "atlas.get")[0].Str("epoch") == "ep1");
            Session.FeedForTest(AtlasPage("ep1", 45, "\"kills\":{\"100\":{\"n\":42,\"m\":2,\"items\":{\"7\":{\"n\":8,\"c\":9}},\"z\":{\"1429\":32,\"1430\":10}}},\"finds\":[{\"s\":44,\"i\":9,\"c\":1,\"src\":\"obj:50\",\"at\":" + AtlasAt + ",\"by\":\"Cat\"}]"));
            check("download: an incremental answer replaces the source's record and adds the new find", Session.AtlasData.Drops(7)[0].Kills == 42 && Session.AtlasData.Drops(7)[0].Chance == 19.05 && Session.AtlasData.Finds(9).Count == 1 && Session.AtlasData.Finds(7).Count == 2 && Session.AtlasData.Rev == 45);
            Session.FeedForTest(AtlasPage("otherepoch", 3, "\"kills\":{\"555\":{\"n\":1}}"));
            check("download: an incremental page of another data set is ignored", Session.AtlasData.Loot("npc", 555) == null && Session.AtlasData.Rev == 45);
            Session.FeedForTest(AtlasPage("ep2", 3, "\"kills\":{\"555\":{\"n\":1}}", full: true));
            check("download: a 'full' answer replaces everything held", Session.AtlasData.Loot("npc", 555) != null && Session.AtlasData.Loot("npc", 100) == null && Session.AtlasData.Epoch == "ep2" && Session.AtlasData.Rev == 3 && Session.AtlasData.FindCount == 0);
            Session.FeedForTest("{\"t\":\"atlas.data\",\"epoch\":\"bad epoch!\",\"rev\":9,\"full\":true,\"kills\":{\"1\":{\"n\":1}}}");
            Session.FeedForTest("{\"t\":\"atlas.data\",\"rev\":9,\"full\":true,\"kills\":{\"1\":{\"n\":1}}}");
            check("download: an answer without a proper epoch is ignored", Session.AtlasData.Loot("npc", 1) == null && Session.AtlasData.Epoch == "ep2");

            // ---- a changed Hub file: the upload waits for its slot (one per 3 minutes), the aggregate is not asked for again at once
            Session.Disconnect(); Session.AtlasResetForTest(); try { File.Delete(sentFile); } catch { }
            sent.Clear();
            Session.FeedForTest(AtlasWelcome("member", "2.15.0", true));
            Session.FeedForTest("{\"t\":\"atlas.shared\",\"ok\":true,\"accepted\":11,\"unchanged\":0,\"rejected\":0,\"rev\":50,\"epoch\":\"ep2\"}");
            sent.Clear();
            setNow(getNow().AddMinutes(1));
            WriteAtlasHub("ACC1", AtlasLua(AtlasFixture().Replace("[101] = { n = 5 }", "[101] = { n = 9 }")));
            Session.AtlasSync();
            check("slot: a source changed 1 minute after the last upload -> no upload yet, it waits", AFrames(sent, "atlas.share").Count == 0 && Session.AtlasBacklogForTest);
            sent.Clear();
            setNow(getNow().AddMinutes(4));
            Session.AtlasSync();
            shares = AFrames(sent, "atlas.share");
            check("slot: 4 minutes later the waiting change goes out alone (1 kill source, cumulative n = 9)", shares.Count == 1 && shares[0].Child("kills").Count == 1 && shares[0].Child("kills").Child("101").NLong("n") == 9 && shares[0].Child("objects") == null);
            sent.Clear();
            Session.FeedForTest("{\"t\":\"atlas.shared\",\"ok\":false,\"accepted\":0,\"unchanged\":0,\"rejected\":0,\"why\":\"rate\"}");
            check("slot: a 'rate' answer keeps the change for later and still fetches the aggregate", Session.AtlasBacklogForTest && AFrames(sent, "atlas.get").Count == 1 && !Session.AtlasInflightForTest);
            sent.Clear();
            setNow(getNow().AddMinutes(2)); Session.AtlasSync();
            check("slot: 2 minutes after a 'rate' answer nothing is sent", AFrames(sent, "atlas.share").Count == 0);
            setNow(getNow().AddMinutes(3)); Session.AtlasSync();
            check("slot: after the 3 minutes it goes out", AFrames(sent, "atlas.share").Count == 1);
            Session.FeedForTest("{\"t\":\"atlas.shared\",\"ok\":true,\"accepted\":1,\"unchanged\":0,\"rejected\":0,\"rev\":51,\"epoch\":\"ep2\"}");
            check("slot: once accepted nothing is left waiting, and the record on disk has the new signature", !Session.AtlasBacklogForTest && Session.AtlasSentForTest.Count == 11);

            // ---- sharing off: nothing is uploaded, the download still works
            sent.Clear();
            settings.AtlasShareOff = true;
            setNow(getNow().AddMinutes(30));
            WriteAtlasHub("ACC1", AtlasLua(AtlasFixture().Replace("[101] = { n = 5 }", "[101] = { n = 12 }")));
            Session.AtlasSettingsChanged();
            check("setting off: a changed source is not uploaded, the aggregate is still fetched", AFrames(sent, "atlas.share").Count == 0 && AFrames(sent, "atlas.get").Count == 1);
            sent.Clear();
            Session.FeedForTest(AtlasWelcome("member", "2.15.0", true));
            check("setting off: a reconnect uploads nothing either, and downloads", AFrames(sent, "atlas.share").Count == 0 && AFrames(sent, "atlas.get").Count == 1);
            sent.Clear();
            settings.AtlasShareOff = false;
            Session.AtlasSettingsChanged();
            check("setting on again: the waiting change is uploaded", AFrames(sent, "atlas.share").Count == 1 && AFrames(sent, "atlas.share")[0].Child("kills").Child("101").NLong("n") == 12);
            Session.FeedForTest("{\"t\":\"atlas.shared\",\"ok\":true,\"accepted\":1,\"unchanged\":0,\"rejected\":0,\"rev\":52,\"epoch\":\"ep2\"}");

            // ---- guests read, they don't contribute
            Session.Disconnect(); Session.AtlasResetForTest(); sent.Clear();
            try { File.Delete(sentFile); } catch { }
            Session.FeedForTest(AtlasWelcome("guest", "2.15.0", true));
            check("guest: nothing is uploaded, the aggregate is fetched", AFrames(sent, "atlas.share").Count == 0 && AFrames(sent, "atlas.get").Count == 1);
            sent.Clear();
            Session.FeedForTest(AtlasWelcome("member", "2.15.0", true));
            Session.FeedForTest("{\"t\":\"atlas.shared\",\"ok\":false,\"accepted\":0,\"unchanged\":0,\"rejected\":0,\"why\":\"guest\"}");
            sent.Clear();
            setNow(getNow().AddMinutes(30));
            Session.AtlasSync();
            check("guest answer: a server that says 'guest' stops further upload attempts on this connection", AFrames(sent, "atlas.share").Count == 0 && Session.AtlasGuestForTest);

            // ---- the owner switched the feature off while connected
            Session.Disconnect(); Session.AtlasResetForTest(); sent.Clear();
            try { File.Delete(sentFile); } catch { }
            Session.FeedForTest(AtlasWelcome("member", "2.15.0", true));
            sent.Clear();
            Session.FeedForTest("{\"t\":\"atlas.shared\",\"ok\":false,\"accepted\":0,\"unchanged\":0,\"rejected\":0,\"why\":\"disabled\"}");
            setNow(getNow().AddMinutes(30));
            Session.AtlasSync(); Session.AtlasSettingsChanged();
            check("disabled answer: nothing more is uploaded or asked for on this connection", AFrames(sent, "atlas.share").Count == 0 && AFrames(sent, "atlas.get").Count == 0);

            // ---- an older server: nothing at all
            Session.Disconnect(); Session.AtlasResetForTest(); sent.Clear();
            try { File.Delete(sentFile); File.Delete(cacheFile); } catch { }
            var folderState = string.Join("|", Directory.GetFiles(atlasRoot, "*", SearchOption.AllDirectories).OrderBy(x => x).Select(f => f + ":" + new FileInfo(f).Length));
            Session.FeedForTest(AtlasWelcome("member", "2.14.0", false));
            Session.AtlasSync(); Session.AtlasSync(true); Session.AtlasSettingsChanged();
            Session.FeedForTest(AtlasPage("ep1", 3, "\"kills\":{\"1\":{\"n\":1}}", full: true));
            Session.FeedForTest("{\"t\":\"atlas.shared\",\"ok\":true}");
            check("old server: no atlas message is sent, nothing is requested, stored or written", !Session.SupportsAtlas && !sent.Any(o => (o.Str("t") ?? "").StartsWith("atlas.")) && !Session.AtlasData.HasData && !File.Exists(sentFile) && !File.Exists(cacheFile));
            var after = string.Join("|", Directory.GetFiles(atlasRoot, "*", SearchOption.AllDirectories).OrderBy(x => x).Select(f => f + ":" + new FileInfo(f).Length));
            check("old server: no file under the WoW folder changed", folderState == after);
            sent.Clear();
            Session.Disconnect();
            Session.FeedForTest(AtlasWelcome("member", "2.14.0", true));
            Session.AtlasSync();
            check("old server: even an 'atlas' feature on a server below 2.15 is ignored", !Session.SupportsAtlas && !sent.Any(o => (o.Str("t") ?? "").StartsWith("atlas.")));
            Session.Disconnect();
            sent.Clear();
            Session.AtlasSync();
            check("disconnected: nothing happens without a lodge", sent.Count == 0 && !Session.SupportsAtlas);
            check("the real game folder was never involved (all files are under the scratch folder)", Directory.GetFiles(atlasRoot, "*", SearchOption.AllDirectories).All(f => f.StartsWith(atlasRoot)));
            await Task.CompletedTask;
        }
        // ---------------------------------------------------------------- seen details (feature "atlas7", server 2.25): the verified fields, the build, the merge, the member count
        async Task AtlasSeenDetailsTest(Action<string, bool> check, List<Dictionary<string, object>> sent)
        {
            var src = new FakeSeen();
            src.N.Add(new ElansAddonHub.Services.Db2.WdbCreature { Id = 2958, Name = "Prairie Wolf", Type = 1, Family = 1, Flags0 = 0x08000001, KillCredit1 = 0 });
            src.N.Add(new ElansAddonHub.Services.Db2.WdbCreature { Id = 253272, Name = "Magram Necromancer", Type = 7, Flags0 = 0x08000004, KillCredit1 = 253664 });
            src.O.Add(new ElansAddonHub.Services.Db2.WdbObject { Id = 1621, Name = "Briarthorn", Type = 3, Data = Enumerable.Range(0, ElansAddonHub.Services.Db2.WdbObject.Count).Select(i => i == 0 ? 31 : 0).ToArray() });
            var full = new ElansAddonHub.Services.Db2.WdbQuest
            {
                Id = 498, Title = "The Rescue", Level = 22, MinLevel = 17, Sort = 267, Type = 2, Objectives = "Free them.", Description = "Me and some other warriors...", Full = true, Flags = 8, NextQuest = 533, Money = 1800,
                Races = 0xaa2aaaaa4e0ab3b2UL, Rewards = new[] { new ElansAddonHub.Services.Db2.WdbItemCount { Id = 3734, Count = 1 } }, Choices = new[] { new ElansAddonHub.Services.Db2.WdbItemCount { Id = 3752, Count = 1 }, new ElansAddonHub.Services.Db2.WdbItemCount { Id = 3733, Count = 1 } },
                Provided = new[] { new ElansAddonHub.Services.Db2.WdbItemCount { Id = 3467, Count = 1 } }, RepFactions = new[] { 76 },
                Goals = new[] { new ElansAddonHub.Services.Db2.WdbObjective { Type = 2, ObjectId = 1721, Amount = 1, Text = "Rescue Drull" }, new ElansAddonHub.Services.Db2.WdbObjective { Type = 0, ObjectId = 3917, Amount = 12, Text = "" } },
                CompletionLog = "Return to Krusk.",
            };
            src.Q.Add(full);
            src.Q.Add(new ElansAddonHub.Services.Db2.WdbQuest { Id = 900, Title = "Wolves", Level = 5, MinLevel = 3, Objectives = "Kill." });     // text-only record: no details
            var savedBuild = AtlasObs.SeenBuild;
            AtlasObs.SeenSource = src; AtlasObs.SeenBuild = () => 70374;
            try
            {
                // ---- batches
                var plain = AtlasObs.MakeBatch(null, null, new Dictionary<string, string>(), true);
                var det = AtlasObs.MakeBatch(null, null, new Dictionary<string, string>(), true, seenDetails: true);
                var pq = plain.Frame.Child("seenQuests").Child("498"); var dq = det.Frame.Child("seenQuests").Child("498");
                check("seen details: without atlas7 the old shape goes out (title, levels, objectives only; no build)", pq.Keys.OrderBy(k => k).SequenceEqual(new[] { "level", "minLevel", "objectives", "title" }) && !plain.Frame.Child("seenNpcs").Child("2958").ContainsKey("tf"));
                check("seen details: with atlas7 a quest carries the build, sort, flags, next, money, the race mask as two 32-bit halves, rewards / choices / provided as [item, count], reputation, goals and the texts",
                    dq.NLong("build") == 70374 && dq.NLong("sort") == 267 && dq.NLong("flags") == 8 && dq.NLong("next") == 533 && dq.NLong("money") == 1800 && dq.List("races").Count == 2 && Convert.ToInt64(dq.List("races")[0]) == 0x4e0ab3b2L && Convert.ToInt64(dq.List("races")[1]) == 0xaa2aaaaaL
                    && dq.List("rewards").Count == 1 && dq.List("choices").Count == 2 && dq.List("provided").Count == 1 && dq.List("rep").Count == 1 && dq.List("goals").Count == 2 && dq.Str("done") == "Return to Krusk." && dq.Str("desc") == "Me and some other warriors...");
                check("seen details: a text-only record goes without details, and nothing personal is in the frame", !det.Frame.Child("seenQuests").Child("900").ContainsKey("build") && !Json.Write(det.Frame).Contains("Elan"));
                var dn = det.Frame.Child("seenNpcs");
                check("seen details: npcs carry their type flags and kill credits, objects their lock", dn.Child("2958").NLong("tf") == 0x08000001 && !dn.Child("2958").ContainsKey("kc") && dn.Child("253272").List("kc").Count == 2 && Convert.ToInt64(dn.Child("253272").List("kc")[0]) == 253664
                    && det.Frame.Child("seenObjects").Child("1621").NLong("lock") == 31 && dn.Child("2958").NLong("build") == 70374);
                check("seen details: what was sent without details is sent again once the lodge takes them (the signature includes them), then not again", AtlasObs.MakeBatch(null, null, plain.Sigs, true, seenDetails: true)?.Entries == 5 && AtlasObs.MakeBatch(null, null, det.Sigs, true, seenDetails: true) == null
                    && AtlasObs.MakeBatch(null, null, plain.Sigs, true) == null);
                AtlasObs.SeenBuild = () => 70400;
                check("seen details: a new game build sends every detailed record again (the lodge keeps the newest build's); the text-only quest stays sent", AtlasObs.MakeBatch(null, null, det.Sigs, true, seenDetails: true)?.Entries == 4);
                AtlasObs.SeenBuild = () => 70374;
                // the parser takes what the batch builds and refuses what the server would refuse
                int dropped = 0;
                var back = AtlasObs.ParseSeenQuest(498, ToParsedForTest(dq), ref dropped);
                check("seen details: the wire shape parses back to the same quest (races, goals, lists)", back != null && back.Detailed && back.Races == 0xaa2aaaaa4e0ab3b2UL && back.Goals[0].ObjectId == 1721 && back.Goals[0].Text == "Rescue Drull" && back.Choices[1].Id == 3733 && back.Done == "Return to Krusk." && back.ToWdb().Full && back.ToWdb().RaceLimited);
                var badGoals = (Dictionary<string, object>)ToParsedForTest(dq); badGoals["goals"] = new List<object> { new List<object> { 1.0, 2.0 } };
                var badRaces = (Dictionary<string, object>)ToParsedForTest(dq); badRaces["races"] = new List<object> { -1.0, 0.0 };
                var badRew = (Dictionary<string, object>)ToParsedForTest(dq); badRew["rewards"] = Enumerable.Range(1, 5).Select(i => (object)new List<object> { (double)i, 1.0 }).ToList();
                int d2 = 0;
                check("seen details: a goal of the wrong shape, a negative race half or five reward items = the record is refused", AtlasObs.ParseSeenQuest(498, badGoals, ref d2) == null && AtlasObs.ParseSeenQuest(498, badRaces, ref d2) == null && AtlasObs.ParseSeenQuest(498, badRew, ref d2) == null && d2 == 3);

                // ---- the session: atlas7 only from a 2.25 server
                var sentFile = Session.AtlasSentPathForTest; var cacheFile = Session.AtlasCachePathForTest;
                Session.Disconnect(); Session.AtlasResetForTest(); sent.Clear();
                try { File.Delete(sentFile); File.Delete(cacheFile); } catch { }
                WriteAtlasHub("ACC1", AtlasLua(AtlasFixture()));
                Session.FeedForTest(AtlasWelcome("member", "2.24.0", true, true, true));
                check("seen details session: a 2.24 server that names atlas7 is not trusted with it", Session.SupportsAtlasSeen && !Session.SupportsAtlasSeenDetails && !Json.Write(AFrames(sent, "atlas.share")[0]).Contains("\"build\""));
                Session.Disconnect(); Session.AtlasResetForTest(); sent.Clear();
                try { File.Delete(sentFile); File.Delete(cacheFile); } catch { }
                Session.FeedForTest(AtlasWelcome("member", "2.25.0", true, true, true));
                var sh = AFrames(sent, "atlas.share");
                check("seen details session: a 2.25 server with atlas7 gets the details", Session.SupportsAtlasSeenDetails && sh.Count == 1 && sh[0].Child("seenQuests").Child("498").NLong("build") == 70374 && sh[0].Child("seenObjects").Child("1621").NLong("lock") == 31);

                // ---- the read API: details, the member count, the cache
                sent.Clear();
                Session.FeedForTest(AtlasPage("ed1", 80, "\"seenMembers\":3,\"seenNpcs\":{\"2958\":{\"name\":\"Prairie Wolf\",\"type\":1,\"family\":1,\"build\":70374,\"tf\":134217729},\"7\":{\"name\":\"Bad\",\"tf\":-1}},"
                    + "\"seenObjects\":{\"1621\":{\"name\":\"Briarthorn\",\"type\":3,\"build\":70374,\"lock\":31}},"
                    + "\"seenQuests\":{\"777\":{\"title\":\"Skyborne Welcome\",\"level\":1,\"minLevel\":1,\"build\":70374,\"sort\":78,\"races\":[0,1],\"goals\":[[0,500,3,\"Talk\"]],\"rewards\":[[100,2]]},\"778\":{\"title\":\"Bad\",\"build\":70374,\"goals\":[[1]]}}", full: true));
                var a = Session.AtlasData;
                var q7 = a.QuestInfo(777);
                check("seen details read API: a quest with its details (race bit 32 from the high half, a goal, a reward), a broken one is left out; npc type flags; object lock; the member count",
                    q7 != null && q7.Detailed && q7.Races == 1UL << 32 && q7.Goals.Single().ObjectId == 500 && q7.Rewards.Single().Count == 2 && a.QuestInfo(778) == null && a.Npc(2958)?.Tameable == true && a.Npc(7) == null && a.Object(1621)?.Lock == 31 && a.SeenMembers == 3);
                Session.AtlasSaveCacheForTest();
                Session.Disconnect(); Session.AtlasResetForTest(); sent.Clear();
                Session.FeedForTest(AtlasWelcome("member", "2.25.0", true, true, true));
                check("seen details read API: the cache brings the details and the member count back", Session.AtlasData.QuestInfo(777)?.Races == 1UL << 32 && Session.AtlasData.Object(1621)?.Lock == 31 && Session.AtlasData.SeenMembers == 3);
                Session.FeedForTest(AtlasPage("ed1", 81, "\"seenNpcs\":{\"5\":{\"name\":\"Five\"}}"));
                check("seen details read API: a page without the count keeps it", Session.AtlasData.SeenMembers == 3);

                // ---- SeenView: own cache and Lodge merged (the Atlas pages read this)
                var savedLodge = ElansAddonHub.Services.Atlas.AtlasQuery.Context.Lodge;
                ElansAddonHub.Services.Atlas.AtlasQuery.Context.Lodge = () => Session.AtlasData;
                try
                {
                    SeenData.SetForTest(new SeenSnapshot(null, null, new[] { new ElansAddonHub.Services.Db2.WdbQuest { Id = 777, Title = "Skyborne Welcome (old)", Level = 1 } }));
                    var v = ElansAddonHub.Services.Atlas.SeenView.Quest(777, out bool fromLodge);
                    check("SeenView: the Lodge's detailed record beats this PC's text-only one; Lodge-only quests are listed for the start zones", v != null && fromLodge && v.Full && v.RaceLimited && ElansAddonHub.Services.Atlas.SeenView.AllQuests().Any(x => x.Id == 777 && x.Full));
                    check("SeenView: start zone from a quest only the Lodge has seen (race bit 32 -> area 78)", ElansAddonHub.Services.Atlas.AtlasQuery.StartAreaSeen(ElansAddonHub.Services.Atlas.SeenView.AllQuests(), 1UL << 32, 1, out _) == 78);
                    check("SeenView: an object's lock comes from the Lodge when this PC has not seen it", ElansAddonHub.Services.Atlas.SeenView.LockOf(1621) == 31 && ElansAddonHub.Services.Atlas.SeenView.LockOf(1) == 0);
                    check("SeenView: a creature's type flags come from the Lodge (tameable)", ElansAddonHub.Services.Atlas.SeenView.Npc(2958)?.Tameable == true && ElansAddonHub.Services.Atlas.SeenView.Npc(2958).FromLodge);
                    // the beasts of both sides for Hunter Helper: this PC's cache says 2958 is not tameable, the Lodge's newer record says it is; untouched ones stay; non-beasts without a family are out
                    SeenData.SetForTest(new SeenSnapshot(new[] {
                        new ElansAddonHub.Services.Db2.WdbCreature { Id = 2958, Name = "Prairie Wolf", Type = 1, Family = 1, Flags0 = 0 },
                        new ElansAddonHub.Services.Db2.WdbCreature { Id = 3000, Name = "Grinning Dog", Type = 1, Family = 25, Flags0 = 0 },
                        new ElansAddonHub.Services.Db2.WdbCreature { Id = 3001, Name = "Guard", Type = 7, Family = 0, Flags0 = 0 },
                        new ElansAddonHub.Services.Db2.WdbCreature { Id = 3002, Name = "Odd Demon", Type = 3, Family = 15, Flags0 = 5 } }, null, null));
                    var bf = ElansAddonHub.Services.Atlas.SeenView.BeastFacts();
                    check("SeenView beasts: the Lodge's newer record wins (tameable), this PC's own stay, a non-beast without a pet family is left out, a family counts without the beast type",
                        bf.Count == 3 && bf[2958].Tameable && bf[2958].FromLodge && bf[2958].Build == 70374 && bf[2958].Family == 1 && !bf[3000].Tameable && bf[3000].Family == 25 && !bf.ContainsKey(3001) && bf[3002].Tameable && bf[3002].BossLevel && bf[3002].Family == 15);
                }
                finally { ElansAddonHub.Services.Atlas.AtlasQuery.Context.Lodge = savedLodge; SeenData.SetForTest(null, SeenData.State.NotStarted); }
                Session.Disconnect(); Session.AtlasResetForTest();
                try { File.Delete(sentFile); File.Delete(cacheFile); } catch { }
            }
            finally { AtlasObs.SeenBuild = savedBuild; }
            await Task.CompletedTask;
        }

        static object ToParsedForTest(object v)
        {
            switch (v)
            {
                case int n: return (double)n;
                case long n: return (double)n;
                case double n: return n;
                case Dictionary<string, object> d: return d.ToDictionary(kv => kv.Key, kv => ToParsedForTest(kv.Value));
                case List<object> l: return l.Select(ToParsedForTest).ToList();
                default: return v;
            }
        }

        sealed class FakeSeen : AtlasObs.ISeenSource
        {
            public List<ElansAddonHub.Services.Db2.WdbCreature> N = new List<ElansAddonHub.Services.Db2.WdbCreature>();
            public List<ElansAddonHub.Services.Db2.WdbObject> O = new List<ElansAddonHub.Services.Db2.WdbObject>();
            public List<ElansAddonHub.Services.Db2.WdbQuest> Q = new List<ElansAddonHub.Services.Db2.WdbQuest>();
            public IEnumerable<ElansAddonHub.Services.Db2.WdbCreature> Npcs() { return N; }
            public IEnumerable<ElansAddonHub.Services.Db2.WdbObject> Objects() { return O; }
            public IEnumerable<ElansAddonHub.Services.Db2.WdbQuest> Quests() { return Q; }
        }

        // ---------------------------------------------------------------- seen details (feature "atlas2"): batches, session gating, read API
        async Task AtlasSeenTest(Action<string, bool> check, List<Dictionary<string, object>> sent, Func<DateTime> getNow, Action<DateTime> setNow)
        {
            var src = new FakeSeen();
            src.N.Add(new ElansAddonHub.Services.Db2.WdbCreature { Id = 100, Name = "Hogger", Title = "", Type = 7, Family = 0, Rank = 1 });
            src.N.Add(new ElansAddonHub.Services.Db2.WdbCreature { Id = 101, Name = "Evil\u0001\nName", Title = "Smith", Type = 7 });
            src.N.Add(new ElansAddonHub.Services.Db2.WdbCreature { Id = 102, Name = "" });                       // no name
            src.N.Add(new ElansAddonHub.Services.Db2.WdbCreature { Id = 0, Name = "Zero" });                     // bad id
            src.N.Add(new ElansAddonHub.Services.Db2.WdbCreature { Id = 103, Name = new string('L', 200), Title = new string('T', 200) });   // cut
            src.O.Add(new ElansAddonHub.Services.Db2.WdbObject { Id = 50, Name = "Copper Vein", Type = 25 });
            src.Q.Add(new ElansAddonHub.Services.Db2.WdbQuest { Id = 900, Title = "Wolves", Level = 5, MinLevel = 3, Objectives = "Kill 8 wolves.\r\nReturn." });
            src.Q.Add(new ElansAddonHub.Services.Db2.WdbQuest { Id = 901, Title = "Long", Level = -1, Objectives = new string('o', 3000) });
            AtlasObs.SeenSource = src;

            // ---- batches
            check("seen: without the sub-feature nothing of it goes out", AtlasObs.MakeBatch(null, null, new Dictionary<string, string>()) == null);
            var b = AtlasObs.MakeBatch(null, null, new Dictionary<string, string>(), true);
            var sn = b?.Frame.Child("seenNpcs");
            check("seen: with it, valid npcs / objects / quests go out (3 of 5 npcs: no name and id 0 are left out)", b != null && sn != null && sn.Count == 3 && b.Frame.Child("seenObjects").Count == 1 && b.Frame.Child("seenQuests").Count == 2);
            check("seen: npc fields", sn.Child("100").Str("name") == "Hogger" && sn.Child("100").NLong("type") == 7 && sn.Child("100").NLong("rank") == 1 && sn.Child("101").Str("title") == "Smith");
            check("seen: control characters never leave", !Json.Write(b.Frame).Contains("\\u0001") && sn.Child("101").Str("name").IndexOf('\u0001') < 0 && sn.Child("101").Str("name").IndexOf('\n') < 0);
            check("seen: names and titles are cut (80), objectives at 1000 with line breaks kept", sn.Child("103").Str("name").Length == 80 && sn.Child("103").Str("title").Length == 80
                && b.Frame.Child("seenQuests").Child("901").Str("objectives").Length == 1000 && b.Frame.Child("seenQuests").Child("900").Str("objectives") == "Kill 8 wolves.\nReturn." && b.Frame.Child("seenQuests").Child("901").NLong("level") == -1);
            check("seen: the frame carries no key but the section names", b.Frame.Keys.All(k => k == "t" || k == "seenNpcs" || k == "seenObjects" || k == "seenQuests"));
            check("seen: sent ids are not sent again (deltas)", AtlasObs.MakeBatch(null, null, b.Sigs, true) == null);
            src.N[0].Title = "Gnoll Boss"; src.N.Add(new ElansAddonHub.Services.Db2.WdbCreature { Id = 104, Name = "New" });
            var d = AtlasObs.MakeBatch(null, null, b.Sigs, true);
            check("seen: a changed or new id goes out alone", d != null && d.Frame.Child("seenNpcs").Count == 2 && d.Frame.Child("seenNpcs").Child("100").Str("title") == "Gnoll Boss" && d.Frame.Child("seenObjects") == null && d.Frame.Child("seenQuests") == null);
            src.N.Clear();
            for (int i = 1; i <= 350; i++) src.N.Add(new ElansAddonHub.Services.Db2.WdbCreature { Id = i, Name = "N" + i });
            src.O.Clear(); src.Q.Clear();
            var big = AtlasObs.MakeBatch(null, null, new Dictionary<string, string>(), true);
            check("seen: at most 300 npcs per message, the rest waits", big != null && big.Frame.Child("seenNpcs").Count == AtlasObs.MaxSeenNpcsPerShare && big.Remaining == 50);
            var rest = AtlasObs.MakeBatch(null, null, big.Sigs, true);
            check("seen: the next message carries the rest", rest != null && rest.Frame.Child("seenNpcs").Count == 50 && rest.Remaining == 0);
            AtlasObs.SeenSource = new FakeSeen { N = null };
            check("seen: a source with no lists shares nothing and never throws", AtlasObs.MakeBatch(null, null, new Dictionary<string, string>(), true) == null);

            // ---- the session: only against a server that advertises atlas2
            src.N.Clear(); src.O.Clear(); src.Q.Clear();
            src.N.Add(new ElansAddonHub.Services.Db2.WdbCreature { Id = 100, Name = "Hogger", Type = 7, Rank = 1 });
            src.O.Add(new ElansAddonHub.Services.Db2.WdbObject { Id = 50, Name = "Copper Vein", Type = 25 });
            src.Q.Add(new ElansAddonHub.Services.Db2.WdbQuest { Id = 900, Title = "Wolves", Level = 5, MinLevel = 3, Objectives = "Kill." });
            AtlasObs.SeenSource = src;
            var sentFile = Session.AtlasSentPathForTest; var cacheFile = Session.AtlasCachePathForTest;
            Session.Disconnect(); Session.AtlasResetForTest(); sent.Clear();
            try { File.Delete(sentFile); File.Delete(cacheFile); } catch { }
            WriteAtlasHub("ACC1", AtlasLua(AtlasFixture()));
            Session.FeedForTest(AtlasWelcome("member", "2.15.0", true));
            var shares = AFrames(sent, "atlas.share");
            check("seen session: a server with 'atlas' but not 'atlas2' gets no seen sections", Session.SupportsAtlas && !Session.SupportsAtlasSeen && shares.Count == 1 && shares[0].Child("seenNpcs") == null && shares[0].Child("seenObjects") == null && shares[0].Child("seenQuests") == null && !Json.Write(shares[0]).Contains("Hogger"));
            Session.Disconnect(); Session.AtlasResetForTest(); sent.Clear();
            try { File.Delete(sentFile); File.Delete(cacheFile); } catch { }
            Session.FeedForTest(AtlasWelcome("member", "2.15.0", true, true));
            shares = AFrames(sent, "atlas.share");
            check("seen session: with 'atlas2' the first upload carries them next to the observations", Session.SupportsAtlasSeen && shares.Count == 1 && shares[0].Child("kills").Count == 4 && shares[0].Child("seenNpcs").Child("100").Str("name") == "Hogger"
                && shares[0].Child("seenObjects").Child("50").Str("name") == "Copper Vein" && shares[0].Child("seenQuests").Child("900").Str("title") == "Wolves");
            Session.FeedForTest("{\"t\":\"atlas.shared\",\"ok\":true,\"accepted\":14,\"unchanged\":0,\"rejected\":0,\"rev\":60,\"epoch\":\"es1\"}");
            check("seen session: what was sent is remembered on disk (11 + 3 entries)", Session.AtlasSentForTest.Count == 14 && Session.AtlasSentForTest.ContainsKey("N100") && Session.AtlasSentForTest.ContainsKey("G50") && Session.AtlasSentForTest.ContainsKey("W900"));
            // a restart: nothing is sent again, a new npc is
            Session.Disconnect(); Session.AtlasResetForTest(); sent.Clear();
            src.N.Add(new ElansAddonHub.Services.Db2.WdbCreature { Id = 105, Name = "Fresh" });
            Session.FeedForTest(AtlasWelcome("member", "2.15.0", true, true));
            shares = AFrames(sent, "atlas.share");
            check("seen session: after a restart only the id not sent before goes out", shares.Count == 1 && shares[0].Child("kills") == null && shares[0].Child("seenNpcs").Count == 1 && shares[0].Child("seenNpcs").Child("105") != null && shares[0].Child("seenObjects") == null);
            Session.FeedForTest("{\"t\":\"atlas.shared\",\"ok\":true,\"accepted\":1,\"unchanged\":0,\"rejected\":0,\"rev\":61,\"epoch\":\"es1\"}");

            // ---- the read API
            sent.Clear();
            Session.FeedForTest(AtlasPage("es1", 70, "\"seenNpcs\":{\"100\":{\"name\":\"Hogger\",\"title\":\"Gnoll\",\"type\":7,\"family\":0,\"rank\":1},\"7\":{\"name\":\"\"},\"8\":{\"name\":\"Bad\\u0001\"},\"9\":{\"name\":\"X\",\"type\":-5},\"99999999\":{\"name\":\"Huge\"}},"
                + "\"seenObjects\":{\"50\":{\"name\":\"Copper Vein\",\"type\":25},\"51\":{\"name\":5}},"
                + "\"seenQuests\":{\"900\":{\"title\":\"Wolves\",\"level\":5,\"minLevel\":3,\"objectives\":\"Kill 8.\\nBack.\"},\"901\":{\"title\":\"L\",\"level\":9999},\"902\":{\"title\":\"Any\",\"level\":-1}}", full: true));
            var a = Session.AtlasData;
            var npc = a.Npc(100); var obj = a.Object(50); var q = a.QuestInfo(900);
            check("read API: Npc(id) -> name, title, type, family, rank", npc != null && npc.Name == "Hogger" && npc.Title == "Gnoll" && npc.Type == 7 && npc.Rank == 1 && a.Npc(101) == null);
            check("read API: invalid npcs of the answer are left out (empty name, control character, negative type, huge id)", a.SeenNpcCount == 1 && a.Dropped >= 4);
            check("read API: Object(id) -> name and type; a non-text name is left out", obj != null && obj.Name == "Copper Vein" && obj.Type == 25 && a.Object(51) == null && a.SeenObjectCount == 1);
            check("read API: QuestInfo(id) -> title, levels, objectives; level 9999 is left out, level -1 (any) is kept", q != null && q.Title == "Wolves" && q.Level == 5 && q.MinLevel == 3 && q.Objectives == "Kill 8.\nBack." && a.QuestInfo(901) == null && a.QuestInfo(902).Level == -1);
            check("read API: the seen details count as data", a.HasData);
            // the cache file round trip
            Session.AtlasSaveCacheForTest();
            Session.Disconnect(); Session.AtlasResetForTest(); sent.Clear();
            Session.FeedForTest(AtlasWelcome("member", "2.15.0", true, true));
            check("read API: the cache brings the seen details back after a restart", Session.AtlasData.Npc(100)?.Title == "Gnoll" && Session.AtlasData.Object(50)?.Type == 25 && Session.AtlasData.QuestInfo(900)?.Objectives == "Kill 8.\nBack." && Session.AtlasData.SeenNpcCount == 1);
            // an incremental answer adds records; a full one drops what the lodge no longer has
            Session.FeedForTest(AtlasPage("es1", 75, "\"seenNpcs\":{\"101\":{\"name\":\"Brann\"}}"));
            check("read API: an incremental answer adds records", Session.AtlasData.Npc(101)?.Name == "Brann" && Session.AtlasData.Npc(100) != null);
            Session.FeedForTest(AtlasPage("es2", 3, "\"seenNpcs\":{\"5\":{\"name\":\"Only\"}}", full: true));
            check("read API: a 'full' answer replaces them", Session.AtlasData.Npc(5) != null && Session.AtlasData.Npc(100) == null && Session.AtlasData.Object(50) == null && Session.AtlasData.QuestInfo(900) == null);
            Session.Disconnect(); Session.AtlasResetForTest();
            try { File.Delete(sentFile); File.Delete(cacheFile); } catch { }
            await Task.CompletedTask;
        }
    }
}

namespace ElansAddonHub
{
    // frames the session builds carry int / long values (the parsed Json of a server answer carries doubles)
    static class AtlasTestExt
    {
        public static long NLong(this System.Collections.Generic.Dictionary<string, object> d, string k) =>
            d != null && d.TryGetValue(k, out var v) && v != null ? System.Convert.ToInt64(v) : 0;
    }
}
