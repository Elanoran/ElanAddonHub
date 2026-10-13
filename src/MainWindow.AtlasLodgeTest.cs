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

        string AtlasWelcome(string role, string server, bool feature) =>
            FunWelcome(role, FunPeople(false), "", "\"reply\",\"react\",\"react2\",\"pin\",\"profile\",\"displayname\",\"lfg\",\"history2\",\"search\"" + (feature ? ",\"atlas\"" : ""), server);

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
            var hadRoot = settings.WowRoot; var hadOff = settings.AtlasShareOff; var hadClock = Session.Clock; var hadSource = AtlasObs.RevealedSource;
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
                await AtlasSession(check, sent, () => now, t => now = t);
            }
            catch (Exception e) { ok = false; notes.Add("crashed: " + e); }
            finally
            {
                Session.Disconnect();
                Session.SentForTest = null; Session.Clock = hadClock; settings.WowRoot = hadRoot; settings.AtlasShareOff = hadOff;
                AtlasObs.TestSource = null; AtlasObs.RevealedSource = hadSource; Session.AtlasResetForTest();
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
                + "\"finds\":[{\"s\":38,\"i\":7,\"c\":1,\"src\":\"npc:100\",\"at\":" + AtlasAt + ",\"by\":\"Amy the Bold\",\"z\":1429,\"x\":0.5,\"y\":0.5},{\"s\":40,\"i\":7,\"c\":2,\"src\":\"fish\",\"at\":" + (AtlasAt + 9) + ",\"by\":\"Bob\"},{\"s\":41,\"i\":8,\"c\":1,\"src\":\"bad\",\"at\":" + AtlasAt + ",\"by\":\"x\"}]");
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
            check("read API: Finds(item) is newest first with the member's display name", fs.Count == 2 && fs[0].By == "Bob" && fs[0].Count == 2 && fs[1].By == "Amy the Bold" && fs[1].Zone == 1429 && a.FindCount == 2);
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
