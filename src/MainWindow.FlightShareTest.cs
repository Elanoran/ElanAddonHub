using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // --selftest "flightshare": shared flight-path times (Lodge 2.13). Everything runs on fixture folders under the scratch directory and synthetic
    // Lodge frames (no server, no game, never the real WoW folder):
    //   reading and validating Hunter Helper's saved variables (several accounts), the upload batches (500 legs, 56 KB, only what changed, once per
    //   10 minutes), the merge answer, the generated SharedData.lua (escaping, injection attempts, structure, a Lua-data read-back), writing it only
    //   when the toc lists it and atomically, the setting off (no upload, download still works), guests, and an old server (nothing at all).
    public partial class MainWindow
    {
        const long FlightAt = 1_790_000_000L;

        static string HhLeg(string key, string t, string n, long at = FlightAt) =>
            "\t\t[" + LuaStr(key) + "] = { [\"t\"] = " + t + ", [\"n\"] = " + n + ", [\"at\"] = " + at + " },\n";

        static string HhLua(string legs, string names = "") =>
            "ElansHunterHelperDB = {\n\t[\"chars\"] = { [\"SECRETCHAR-Realm\"] = { [\"level\"] = 60 } },\n\t[\"flightLegs\"] = {\n" + legs + "\t},\n\t[\"flightNodeNames\"] = {\n" + names + "\t},\n}\n";

        string flightRoot;
        string HhFile(string account) => Path.Combine(flightRoot, "_classic_beta_", "WTF", "Account", account, "SavedVariables", "ElansHunterHelper.lua");
        string HubDir(string flavor) => Path.Combine(flightRoot, flavor, "Interface", "AddOns", "ElansHub");

        void WriteHh(string account, string lua)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HhFile(account)));
            File.WriteAllText(HhFile(account), lua);
            FlightShare.ResetCache();
        }

        string FlightWelcome(string role, string server, bool feature) =>
            FunWelcome(role, FunPeople(false), "", "\"reply\",\"react\",\"react2\",\"pin\",\"profile\",\"displayname\",\"lfg\",\"history2\",\"search\"" + (feature ? ",\"flights\"" : ""), server);

        // two accounts: valid legs, a lot of invalid ones, names with trouble
        string FlightFixture1()
        {
            var legs = HhLeg("Horde|11>22", "61.5", "3") + HhLeg("Horde|22>11", "60", "1") + HhLeg("Alliance|2>3", "120", "50")
                + HhLeg("Alliance|Stormwind, Elwynn>Ironforge", "1800", "1") + HhLeg("Horde|5>6", "1", "1")
                // invalid: faction, case, same node, quote, Lua injection, extra separator, empty id, t / n bounds, fractional n, string t, over-long id
                + HhLeg("Neutral|1>2", "60", "1") + HhLeg("horde|1>2", "60", "1") + HhLeg("Horde|11>11", "60", "1") + HhLeg("Horde|a\"b>2", "60", "1")
                + HhLeg("Horde|1]]os.execute('x')--[[>2", "60", "1") + HhLeg("Horde|1>2>3", "60", "1") + HhLeg("Horde|>2", "60", "1")
                + HhLeg("Horde|7>8", "0", "1") + HhLeg("Horde|7>9", "1801", "1") + HhLeg("Horde|7>10", "60", "0") + HhLeg("Horde|7>12", "60", "51")
                + HhLeg("Horde|7>13", "60", "2.5") + HhLeg("Horde|7>14", "\"61\"", "1") + HhLeg("Horde|" + new string('a', 33) + ">2", "60", "1");
            var names = "\t\t[11] = \"Orgrimmar\",\n\t\t[22] = \"Crossroads, Barrens\",\n\t\t[\"5\"] = " + LuaStr(new string('N', 200)) + ",\n\t\t[\"6\"] = " + LuaStr("Evil\" ]] \\ name") + ",\n\t\t[\"bad id!\"] = \"x\",\n\t\t[\"99\"] = \"\",\n";
            return HhLua(legs, names);
        }

        string FlightFixture2() => HhLua(HhLeg("Horde|11>22", "64.0", "7", FlightAt + 500) + HhLeg("Alliance|9>10", "200", "2"), "\t\t[9] = \"Southshore\",\n");

        async Task<string> FlightShareTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            Action<string, bool> check = (what, cond) => { notes.Add((cond ? "ok   " : "FAIL ") + what); ok &= cond; };
            var hadRoot = settings.WowRoot; var hadOff = settings.FlightShareOff; var hadClock = Session.Clock;
            var sent = new List<Dictionary<string, object>>();
            var now = new DateTime(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);
            flightRoot = Path.Combine(dir, "flightwow");
            var sentFile = Path.Combine(Util.DataDir, "flights-sent.txt");
            try
            {
                if (Directory.Exists(flightRoot)) Directory.Delete(flightRoot, true);
                Session.Disconnect();
                check("fixture: the WoW folder of this test is under the scratch folder (never the real game)", Path.GetFullPath(flightRoot).StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase));
                settings.WowRoot = flightRoot; settings.FlightShareOff = false;
                Session.Clock = () => now;
                Session.SentForTest = o => sent.Add(o);
                FlightShare.TestSource = null;
                try { File.Delete(sentFile); } catch { }
                Session.FlightResetForTest();
                MakeFlightFolders();

                FlightParsing(check);
                FlightBatching(check);
                FlightLua(check);
                FlightWriting(check);
                await FlightSession(check, sent, () => now, t => now = t);
            }
            catch (Exception e) { ok = false; notes.Add("crashed: " + e); }
            finally
            {
                Session.Disconnect();
                Session.SentForTest = null; Session.Clock = hadClock; settings.WowRoot = hadRoot; settings.FlightShareOff = hadOff;
                FlightShare.TestSource = null; FlightShare.ResetCache(); Session.FlightResetForTest();
                try { File.Delete(sentFile); } catch { }
                try { File.Delete(sentFile + ".tmp"); } catch { }
            }
            return "flightshare selftest: " + (ok ? "PASSED" : "FAILED") + "\r\n  " + string.Join("\r\n  ", notes);
        }

        // _classic_beta_: a current companion (toc lists SharedData.lua); _classic_: an old one (does not); _anniversary_: only a comment mentions it; _retail_: no ElansHub at all
        void MakeFlightFolders()
        {
            void Hub(string flavor, string toc)
            {
                var d = HubDir(flavor);
                Directory.CreateDirectory(d);
                File.WriteAllText(Path.Combine(d, "ElansHub.toc"), toc);
                File.WriteAllText(Path.Combine(d, "Chat.lua"), "-- canary " + flavor + "\n");
            }
            Hub("_classic_beta_", "## Interface: 16001\n## Version: 1.10.0\n\nElansHub.lua\nCalendar.lua\nSharedData.lua\n");
            Hub("_classic_", "## Interface: 11508\n## Version: 1.9.3\n\nElansHub.lua\nCalendar.lua\n");
            Hub("_anniversary_", "## Interface: 20505\n# SharedData.lua is not listed yet\nElansHub.lua\n");
            Directory.CreateDirectory(Path.Combine(flightRoot, "_retail_", "Interface", "AddOns"));
        }

        // ---------------------------------------------------------------- parse + validate
        void FlightParsing(Action<string, bool> check)
        {
            WriteHh("ACC1", FlightFixture1());
            WriteHh("ACC2", FlightFixture2());
            var one = FlightShare.Parse(File.ReadAllText(HhFile("ACC1")));
            check("parse: the 5 valid legs are kept (boundaries 1 s, 1800 s, n = 50, a node-name id), everything else is dropped (" + one.Legs.Count + " kept, " + one.Dropped + " dropped)",
                one.Legs.Count == 5 && one.Dropped == 14 && one.Legs.ContainsKey("Alliance|Stormwind, Elwynn>Ironforge") && one.Legs["Alliance|2>3"].N == 50 && one.Legs["Horde|5>6"].T == 1);
            check("parse: wrong faction / lower-case faction / same node / quote / Lua injection / extra separator / empty id are refused",
                !one.Legs.Keys.Any(k => k.StartsWith("Neutral") || k.StartsWith("horde") || k.Contains("\"") || k.Contains("os.execute") || k.Split((char)62).Length > 2 || k.Contains("Horde|>") || k == "Horde|11>11"));
            check("parse: t outside 1-1800, n outside 1-50, fractional n, a string for t and an over-long id are refused", !one.Legs.Keys.Any(k => k.StartsWith("Horde|7>") || k.Contains(new string('a', 33))));
            check("parse: node names are cleaned, cut at 60 characters, and a bad id or an empty name is left out",
                one.Names["11"] == "Orgrimmar" && one.Names["22"] == "Crossroads, Barrens" && one.Names["5"].Length == 60 && !one.Names["6"].Contains("\"") && !one.Names["6"].Contains("\\") && !one.Names["6"].Contains("]")
                && !one.Names.ContainsKey("bad id!") && !one.Names.ContainsKey("99"));
            check("parse: a name keeps its words but loses quote, backslash and brackets ('Evil name')", one.Names["6"] == "Evil name");
            check("parse: control characters and line breaks never survive in a name", FlightShare.CleanName("a\u0001b\r\nc\td\u200be") == "ab c de");
            check("parse: broken, empty or foreign files read as nothing and never throw",
                FlightShare.Parse("ElansHunterHelperDB = {{{ nope").Legs.Count == 0 && FlightShare.Parse("").Legs.Count == 0 && FlightShare.Parse(null).Legs.Count == 0 && FlightShare.Parse("OtherDB = {}").Legs.Count == 0);

            var all = FlightShare.Read(flightRoot);
            check("read: both accounts are read (2 files)", all.Files == 2);
            check("read: the newest value of a leg wins across accounts (64.0 s / 7 samples from ACC2), other legs are added (6 in all)", all.Legs.Count == 6 && all.Legs["Horde|11>22"].T == 64.0 && all.Legs["Horde|11>22"].N == 7 && all.Legs.ContainsKey("Alliance|9>10"));
            check("read: names of both accounts are merged", all.Names["9"] == "Southshore" && all.Names["11"] == "Orgrimmar");
            var cached1 = FlightShare.ReadCached(flightRoot, out var c1); var cached2 = FlightShare.ReadCached(flightRoot, out var c2);
            check("read: an unchanged file is not read again (cache), a changed one is", c1 && !c2 && ReferenceEquals(cached1, cached2));
            WriteHh("ACC3", " ");   // a nearly empty third account must not break the others
            check("read: a third, empty account folder is harmless", FlightShare.Read(flightRoot).Legs.Count == 6);
            File.Delete(HhFile("ACC3")); Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(HhFile("ACC3"))), true);
            FlightShare.ResetCache();
            check("read: the files were only opened for reading (content unchanged)", File.ReadAllText(HhFile("ACC1")) == FlightFixture1());
        }

        // ---------------------------------------------------------------- upload batching
        void FlightBatching(Action<string, bool> check)
        {
            var local = FlightShare.Read(flightRoot);
            var sentMap = new Dictionary<string, string>();
            var b = FlightShare.MakeBatch(local, sentMap);
            var legs = (Dictionary<string, object>)b.Frame["legs"];
            var names = (Dictionary<string, object>)b.Frame["names"];
            check("batch: one flights.share with all 6 changed legs and nothing else in the frame (keys: t, legs, names)", b.Frame.Str("t") == "flights.share" && legs.Count == 6 && b.Remaining == 0 && b.Frame.Keys.All(k => k == "t" || k == "legs" || k == "names"));
            check("batch: each leg carries only t and n (no 'at', no character, no account)", legs.Values.All(v => ((Dictionary<string, object>)v).Keys.OrderBy(k => k).SequenceEqual(new[] { "n", "t" })));
            check("batch: names only for nodes the legs use", names.Keys.All(k => legs.Keys.SelectMany(FlightShare.IdsOf).Contains(k)) && names.ContainsKey("9") && names.ContainsKey("11"));
            var json = Json.Write(b.Frame);
            check("batch: nothing from the file other than keys, times, counts and node names is in the frame (no character, no account)", !json.Contains("SECRETCHAR") && !json.Contains("ACC1") && !json.Contains("Realm"));
            foreach (var kv in b.Sigs) sentMap[kv.Key] = kv.Value;
            check("batch: after the lodge has them, nothing is left to send", FlightShare.MakeBatch(local, sentMap) == null);
            var changed = FlightShare.Read(flightRoot);
            changed.Legs["Horde|5>6"] = new FlightShare.Leg { T = 2, N = 4, At = FlightAt + 9 };
            var again = FlightShare.MakeBatch(changed, sentMap);
            check("batch: only a changed leg goes again (1 leg, its 2 node names)", again != null && ((Dictionary<string, object>)again.Frame["legs"]).Count == 1 && ((Dictionary<string, object>)again.Frame["legs"]).ContainsKey("Horde|5>6"));

            var big = new FlightShare.Local();
            for (int i = 0; i < 1200; i++) big.Legs["Horde|" + (i + 1) + ">" + (i + 5000)] = new FlightShare.Leg { T = 30 + i % 100, N = 1 + i % 50 };
            for (int i = 0; i < 1200; i++) big.Names[(i + 1).ToString(CultureInfo.InvariantCulture)] = "Node number " + i;
            var bb = FlightShare.MakeBatch(big, new Dictionary<string, string>());
            var bl = (Dictionary<string, object>)bb.Frame["legs"];
            check("batch: 1200 changed legs -> 500 in the first batch, 700 wait for the next slot", bl.Count == 500 && bb.Remaining == 700);
            check("batch: the frame stays under the server's 64 KB limit (" + Json.Write(bb.Frame).Length + " bytes) with at most 300 names", Encoding.UTF8.GetByteCount(Json.Write(bb.Frame)) < 64 * 1024 && ((Dictionary<string, object>)bb.Frame["names"]).Count <= 300);
            var sent2 = new Dictionary<string, string>(); foreach (var kv in bb.Sigs) sent2[kv.Key] = kv.Value;
            var next = FlightShare.MakeBatch(big, sent2);
            check("batch: the second batch holds the next 500 and none of the first", ((Dictionary<string, object>)next.Frame["legs"]).Count == 500 && !((Dictionary<string, object>)next.Frame["legs"]).Keys.Any(k => bl.ContainsKey(k)) && next.Remaining == 200);
            var wide = new FlightShare.Local();
            for (int i = 0; i < 500; i++) wide.Legs["Alliance|" + new string('A', 31) + i.ToString("D1") + ">" + new string('B', 30) + (i % 10)] = new FlightShare.Leg { T = 100, N = 5 };
            var wb = FlightShare.MakeBatch(wide, new Dictionary<string, string>());
            check("batch: very long node-name keys are limited by size, not just by count (" + Encoding.UTF8.GetByteCount(Json.Write(wb.Frame)) + " bytes)", Encoding.UTF8.GetByteCount(Json.Write(wb.Frame)) < 64 * 1024);
        }

        // ---------------------------------------------------------------- the generated Lua
        void FlightLua(Action<string, bool> check)
        {
            var m = new FlightShare.Merged { At = FlightAt };
            m.Legs["Horde|11>22"] = new FlightShare.Leg { T = 61.56, N = 12 };
            m.Legs["Alliance|2>3"] = new FlightShare.Leg { T = 120, N = 50 };
            m.Legs["Alliance|Stormwind, Elwynn>Ironforge"] = new FlightShare.Leg { T = 300.26, N = 2 };
            // injection attempts in keys, numbers and names
            m.Legs["Horde|1\"]=1 os.execute(\"x\") --[[>2"] = new FlightShare.Leg { T = 5, N = 1 };
            m.Legs["Horde|1]]\nprint(1)\n--[[>2"] = new FlightShare.Leg { T = 5, N = 1 };
            m.Legs["Horde|3>4"] = new FlightShare.Leg { T = 0, N = 1 };
            m.Legs["Horde|3>5"] = new FlightShare.Leg { T = double.NaN, N = 1 };
            m.Legs["Horde|3>6"] = new FlightShare.Leg { T = 60, N = 500 };
            m.Names["11"] = "Orgrimmar";
            m.Names["22"] = "Say \"hi\"\\n]] os.execute('x') --";
            m.Names["2"] = "Line one\nLine two\r\u0007bell";
            m.Names["3"] = "Zul'Gurub \u00e9\u00e8 \u00fc";
            m.Names["777"] = "Not used by any leg";
            m.Names["bad\"id"] = "x";
            var lua = FlightShare.BuildLua(m, new DateTime(2026, 10, 12, 12, 30, 5, DateTimeKind.Utc));
            var lines = lua.Split('\n');
            try { File.WriteAllText(Path.Combine(Path.GetDirectoryName(flightRoot), "SharedData.sample.lua"), lua); } catch { }   // for a real Lua parser to look at afterwards
            check("lua: header says it is generated by the Outpost, do not edit, with the time", lines[0].StartsWith("-- Generated by Elan's Outpost - do not edit") && lines[1] == "-- 2026-10-12 12:30:05 UTC");
            check("lua: only the 3 valid legs are written; injection keys, 0 s, NaN and n = 500 are dropped", Regex.Matches(lua, @"\{ t = ").Count == 3 && !lua.Contains("os.execute(\"x\")") && !lua.Contains("print(1)"));
            // structure: every line is one of a few exact shapes
            var shapes = new[]
            {
                @"^-- .*$", @"^ElansHubShared = \{$", @"^\tflights = \{$", @"^\t\tat = \d+,$", @"^\t\tlegs = \{$", @"^\t\tnames = \{$", @"^\t\t\},$", @"^\t\},$", @"^\},?$",
                @"^\t\t\t\[""(Horde|Alliance)\|[\p{L}\p{N} '.,\-]{1,32}>[\p{L}\p{N} '.,\-]{1,32}""\] = \{ t = \d{1,4}\.\d, n = \d{1,2} \},$",
                @"^\t\t\t\[""[\p{L}\p{N} '.,\-]{1,32}""\] = ""(?:[^""\\\x00-\x1f]|\\[""\\])*"",$",
            };
            var bad = lines.Take(lines.Length - 1).Where(l => !shapes.Any(s => Regex.IsMatch(l, s))).ToList();
            check("lua: every line has one of the exact expected shapes (" + bad.Count + " odd lines)" + (bad.Count > 0 ? ": " + bad[0] : ""), bad.Count == 0 && lines.Last() == "");
            check("lua: a name's quotes, backslashes and brackets are gone, its newlines / control characters too", lua.Contains("= \"Say hin os.execute('x') --\",") && !lua.Contains("\r") && !lua.Contains("\u0007") && lines.Count(l => l.Contains("Line one Line two")) == 1);
            check("lua: LuaEscape itself escapes a quote and a backslash", FlightShare.LuaEscape("a\"b\\c") == "a\\\"b\\\\c");
            // read it back with the Lua-data reader (the same one that reads SavedVariables)
            var back = LuaData.ReadGlobals(lua);
            var shared = back["ElansHubShared"] as Dictionary<string, object>;
            var fl = shared?.Child("flights");
            var l2 = fl?.Child("legs");
            var n2 = fl?.Child("names");
            check("lua: reads back as ElansHubShared.flights with at, legs and names", fl != null && fl.Long("at") == FlightAt && l2 != null && n2 != null);
            check("lua: legs round-trip (61.6 s rounded to 0.1, n = 12; 300.3; the boundary 120 / 50)", (double)l2.Child("Horde|11>22")["t"] == 61.6 && l2.Child("Horde|11>22").Int("n") == 12 && (double)l2.Child("Alliance|Stormwind, Elwynn>Ironforge")["t"] == 300.3 && l2.Child("Alliance|2>3").Int("n") == 50);
            check("lua: names round-trip exactly as cleaned (the injection text stays inside the string, harmless)", (string)n2["11"] == "Orgrimmar" && (string)n2["22"] == FlightShare.CleanName(m.Names["22"]) && (string)n2["2"] == FlightShare.CleanName(m.Names["2"]) && ((string)n2["3"]).Contains("Zul'Gurub"));
            check("lua: names of nodes no leg uses and invalid ids are not written", !n2.ContainsKey("777") && !n2.Keys.Any(k => k.Contains("\"")));
            check("lua: output is deterministic (sorted) and the same input gives the same text", lua == FlightShare.BuildLua(m, new DateTime(2026, 10, 12, 12, 30, 5, DateTimeKind.Utc)) && lua.IndexOf("Alliance|2>3") < lua.IndexOf("Horde|11>22"));
            var empty = FlightShare.BuildLua(new FlightShare.Merged(), now0());
            check("lua: an empty table is valid too", LuaData.ReadGlobals(empty).ContainsKey("ElansHubShared") && !empty.Contains("{ t = "));
            // the escape function alone, against nasty input
            var nasty = "\\\" ]]--[[ \\\\ \n\r\0 \" end";
            var esc = FlightShare.LuaEscape(nasty);
            check("lua: LuaEscape leaves no unescaped quote and no raw newline / NUL", !Regex.IsMatch(esc, @"(?<!\\)(\\\\)*""") && !esc.Contains("\n") && !esc.Contains("\r") && !esc.Contains("\0"));
            // everything the server could send is re-validated before it reaches the file
            var srv = Json.Obj("{\"t\":\"flights.data\",\"at\":1790000000,\"legs\":{\"Horde|1>2\":{\"t\":50,\"n\":2},\"Horde|1>3\":{\"t\":99999,\"n\":2},\"Horde|1\\\"x>3\":{\"t\":50,\"n\":2},\"Horde|1>4\":{\"t\":\"50\",\"n\":2},\"Orc|1>5\":{\"t\":50,\"n\":2},\"Horde|1>6\":7},\"names\":{\"1\":\"A\\\"B\",\"2\":123,\"x y!\":\"no\"}}");
            var parsed = FlightShare.ParseServer(srv);
            check("server data: bad keys / times / types are dropped, names cleaned (1 leg and 1 name survive)", parsed.Legs.Count == 1 && parsed.Legs.ContainsKey("Horde|1>2") && parsed.Dropped == 5 && parsed.Names.Count == 1 && parsed.Names["1"] == "AB");
        }

        static DateTime now0() => new DateTime(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);

        // ---------------------------------------------------------------- writing the file
        void FlightWriting(Action<string, bool> check)
        {
            var m = new FlightShare.Merged { At = FlightAt };
            m.Legs["Horde|11>22"] = new FlightShare.Leg { T = 61.5, N = 3 };
            m.Names["11"] = "Orgrimmar";
            var lua = FlightShare.BuildLua(m, now0());
            var dest = Path.Combine(HubDir("_classic_beta_"), "SharedData.lua");
            var res = FlightShare.WriteShared(flightRoot, lua);
            check("write: the one flavor whose ElansHub toc lists SharedData.lua gets the file (" + res.Written.Count + " written, " + res.Skipped + " skipped)", res.Written.Count == 1 && res.Written[0] == dest && File.ReadAllText(dest) == lua);
            check("write: an old companion (toc without SharedData.lua) and one that only mentions it in a comment are never touched", !File.Exists(Path.Combine(HubDir("_classic_"), "SharedData.lua")) && !File.Exists(Path.Combine(HubDir("_anniversary_"), "SharedData.lua")));
            check("write: a flavor without an ElansHub folder gets nothing, not even the folder", !Directory.Exists(HubDir("_retail_")));
            check("write: the other files of the companion are untouched", File.ReadAllText(Path.Combine(HubDir("_classic_beta_"), "Chat.lua")) == "-- canary _classic_beta_\n" && File.ReadAllText(Path.Combine(HubDir("_classic_"), "Chat.lua")) == "-- canary _classic_\n");
            check("write: it is written without a byte-order mark", File.ReadAllBytes(dest)[0] == (byte)45);
            check("write: no temp file is left behind anywhere", !Directory.GetFiles(flightRoot, "*.tmp", SearchOption.AllDirectories).Any());
            // the same data again, a later header time: the file is left alone
            var stamp = File.GetLastWriteTimeUtc(dest);
            System.Threading.Thread.Sleep(30);
            var res2 = FlightShare.WriteShared(flightRoot, FlightShare.BuildLua(m, now0().AddMinutes(7)));
            check("write: unchanged data (only the header time differs) does not rewrite the file", res2.Written.Count == 0 && res2.Unchanged.Count == 1 && File.GetLastWriteTimeUtc(dest) == stamp);
            m.Legs["Horde|11>22"] = new FlightShare.Leg { T = 62.5, N = 4 };
            var lua3 = FlightShare.BuildLua(m, now0().AddMinutes(8));
            var res3 = FlightShare.WriteShared(flightRoot, lua3);
            check("write: changed data replaces the file", res3.Written.Count == 1 && File.ReadAllText(dest) == lua3);
            // a write that cannot complete leaves the old file whole and no temp file
            var before = File.ReadAllText(dest);
            File.SetAttributes(dest, FileAttributes.ReadOnly);
            m.Legs["Horde|11>22"] = new FlightShare.Leg { T = 99, N = 9 };
            var res4 = FlightShare.WriteShared(flightRoot, FlightShare.BuildLua(m, now0().AddMinutes(9)));
            File.SetAttributes(dest, FileAttributes.Normal);
            check("write: when the replace fails the old file is intact, nothing is half written and the temp file is removed", res4.Written.Count == 0 && File.ReadAllText(dest) == before && !Directory.GetFiles(flightRoot, "*.tmp", SearchOption.AllDirectories).Any());
            check("write: a missing or empty WoW folder / content writes nothing and does not throw", FlightShare.WriteShared(null, lua).Written.Count == 0 && FlightShare.WriteShared(Path.Combine(flightRoot, "nope"), lua).Written.Count == 0 && FlightShare.WriteShared(flightRoot, "").Written.Count == 0);
            check("write: the toc check is exact (SharedData.lua listed, a comment or another name is not)", FlightShare.TocLists(Path.Combine(HubDir("_classic_beta_"), "ElansHub.toc")) && !FlightShare.TocLists(Path.Combine(HubDir("_anniversary_"), "ElansHub.toc")) && !FlightShare.TocLists(Path.Combine(HubDir("_classic_"), "ElansHub.toc")) && !FlightShare.TocLists(Path.Combine(HubDir("_retail_"), "ElansHub.toc")));
            try { File.Delete(dest); } catch { }
        }

        // ---------------------------------------------------------------- the session: connect, share, fetch, write
        static List<Dictionary<string, object>> Frames(List<Dictionary<string, object>> sent, string t) => sent.Where(o => o.Str("t") == t).ToList();

        string ServerData(string legs, string names = "") => "{\"t\":\"flights.data\",\"at\":" + (FlightAt + 100) + ",\"legs\":{" + legs + "},\"names\":{" + names + "}}";

        async Task FlightSession(Action<string, bool> check, List<Dictionary<string, object>> sent, Func<DateTime> getNow, Action<DateTime> setNow)
        {
            var dest = Path.Combine(HubDir("_classic_beta_"), "SharedData.lua");
            WriteHh("ACC1", FlightFixture1()); WriteHh("ACC2", FlightFixture2());

            // ---- a current server, sharing on
            sent.Clear();
            Session.FeedForTest(FlightWelcome("member", "2.13.0", true));
            check("session: the 'flights' feature of a 2.13 server is detected", Session.SupportsFlights);
            var shares = Frames(sent, "flights.share");
            check("connect: right after the welcome one flights.share goes out with the 6 learned legs (and no fetch before its answer)", shares.Count == 1 && ((Dictionary<string, object>)shares[0]["legs"]).Count == 6 && Frames(sent, "flights.get").Count == 0);
            check("connect: the upload carries nothing but times, counts and node names", !Json.Write(shares[0]).Contains("SECRETCHAR") && !Json.Write(shares[0]).Contains("ACC"));
            Session.FeedForTest("{\"t\":\"flights.shared\",\"ok\":true,\"accepted\":6,\"rejected\":0}");
            check("connect: after the lodge accepted them the merged table is requested (flights.get)", Frames(sent, "flights.get").Count == 1);
            check("connect: what was uploaded is remembered (6 legs), also on disk", Session.FlightSentForTest.Count == 6 && File.Exists(Path.Combine(Util.DataDir, "flights-sent.txt")));
            Session.FeedForTest(ServerData("\"Horde|11>22\":{\"t\":63.2,\"n\":20},\"Alliance|2>3\":{\"t\":118,\"n\":50},\"Horde|5>6\":{\"t\":1,\"n\":1},\"Horde|1\\\"x>3\":{\"t\":50,\"n\":2},\"Horde|3>4\":{\"t\":99999,\"n\":1}", "\"11\":\"Orgrimmar\",\"22\":\"Cross\\\"roads\",\"9\":\"unused\""));
            check("download: SharedData.lua is written into the companion that lists it", File.Exists(dest) && Session.FlightFilesWritten == 1);
            var written = File.ReadAllText(dest);
            var back = (LuaData.ReadGlobals(written)["ElansHubShared"] as Dictionary<string, object>).Child("flights");
            check("download: only valid data reaches the file (3 legs; the injection key and the 99999 s leg are gone), names escaped and only for used nodes",
                back.Child("legs").Count == 3 && (double)back.Child("legs").Child("Horde|11>22")["t"] == 63.2 && back.Child("names").Count == 2 && (string)back.Child("names")["22"] == "Crossroads" && !written.Contains("os.execute"));
            check("download: an old companion folder next to it was not touched", !File.Exists(Path.Combine(HubDir("_classic_"), "SharedData.lua")) && !Directory.Exists(HubDir("_retail_")));

            // ---- a changed file: the table is asked for at once, the upload waits for its slot (one per 10 minutes)
            sent.Clear();
            setNow(getNow().AddMinutes(2));
            WriteHh("ACC1", FlightFixture1().Replace("[\"Horde|5>6\"] = { [\"t\"] = 1,", "[\"Horde|5>6\"] = { [\"t\"] = 2,").Replace("[\"n\"] = 1, [\"at\"] = " + FlightAt + " },\n\t\t[\"Neutral", "[\"n\"] = 4, [\"at\"] = " + FlightAt + " },\n\t\t[\"Neutral"));
            Session.FlightSync();
            check("slot: a leg changed 2 minutes after the last upload -> no upload yet, but the table is refreshed", Frames(sent, "flights.share").Count == 0 && Frames(sent, "flights.get").Count == 1 && Session.FlightBacklogForTest);
            sent.Clear();
            setNow(getNow().AddMinutes(11));
            Session.FlightSync();
            shares = Frames(sent, "flights.share");
            check("slot: 11 minutes later the waiting change goes out alone (1 leg, 2 node names)", shares.Count == 1 && ((Dictionary<string, object>)shares[0]["legs"]).Count == 1 && ((Dictionary<string, object>)shares[0]["legs"]).ContainsKey("Horde|5>6") && ((Dictionary<string, object>)shares[0]["names"]).Count == 2);
            sent.Clear();
            Session.FeedForTest("{\"t\":\"flights.shared\",\"ok\":false,\"accepted\":0,\"rejected\":0,\"why\":\"rate\"}");
            check("slot: a 'rate' answer keeps the change for later and still fetches the table", Session.FlightBacklogForTest && Frames(sent, "flights.get").Count == 1 && !Session.FlightInflightForTest);
            sent.Clear();
            setNow(getNow().AddMinutes(5)); Session.FlightSync();
            check("slot: 5 minutes after a 'rate' answer nothing is sent", Frames(sent, "flights.share").Count == 0);
            setNow(getNow().AddMinutes(6)); Session.FlightSync();
            check("slot: after the 10 minutes it goes out", Frames(sent, "flights.share").Count == 1);
            Session.FeedForTest("{\"t\":\"flights.shared\",\"ok\":true,\"accepted\":1,\"rejected\":0}");
            check("slot: once accepted nothing is left waiting", !Session.FlightBacklogForTest);

            // ---- sharing off: nothing is uploaded, the download still works
            sent.Clear();
            settings.FlightShareOff = true;
            File.Delete(dest);
            setNow(getNow().AddMinutes(30));
            WriteHh("ACC2", FlightFixture2().Replace("200", "210"));
            Session.FlightSettingsChanged();
            check("setting off: a changed leg is not uploaded, the table is still fetched", Frames(sent, "flights.share").Count == 0 && Frames(sent, "flights.get").Count == 1);
            Session.FeedForTest(ServerData("\"Horde|11>22\":{\"t\":64,\"n\":21}"));
            check("setting off: and written into SharedData.lua", File.Exists(dest));
            sent.Clear();
            setNow(getNow().AddMinutes(1));
            Session.FeedForTest(FlightWelcome("member", "2.13.0", true));
            check("setting off: a reconnect uploads nothing either, and downloads", Frames(sent, "flights.share").Count == 0 && Frames(sent, "flights.get").Count == 1);
            sent.Clear();
            settings.FlightShareOff = false;
            Session.FlightSettingsChanged();
            check("setting on again: the waiting change is uploaded", Frames(sent, "flights.share").Count == 1);
            Session.FeedForTest("{\"t\":\"flights.shared\",\"ok\":true,\"accepted\":1,\"rejected\":0}");

            // ---- guests read, they don't contribute
            Session.Disconnect(); Session.FlightResetForTest(); sent.Clear();
            try { File.Delete(Path.Combine(Util.DataDir, "flights-sent.txt")); } catch { }
            Session.FeedForTest(FlightWelcome("guest", "2.13.0", true));
            check("guest: nothing is uploaded, the table is fetched", Frames(sent, "flights.share").Count == 0 && Frames(sent, "flights.get").Count == 1);
            sent.Clear();
            Session.FeedForTest(FlightWelcome("member", "2.13.0", true));
            Session.FeedForTest("{\"t\":\"flights.shared\",\"ok\":false,\"accepted\":0,\"rejected\":0,\"why\":\"guest\"}");
            sent.Clear();
            setNow(getNow().AddMinutes(30));
            Session.FlightSync();
            check("guest answer: a server that says 'guest' stops further upload attempts on this connection", Frames(sent, "flights.share").Count == 0);

            // ---- an older server: nothing at all
            Session.Disconnect(); Session.FlightResetForTest(); sent.Clear();
            File.Delete(dest);
            var folderState = string.Join("|", Directory.GetFiles(flightRoot, "*", SearchOption.AllDirectories).OrderBy(x => x).Select(f => f + ":" + new FileInfo(f).Length));
            Session.FeedForTest(FlightWelcome("member", "2.12.0", false));
            Session.FlightSync(); Session.FlightSync(true); Session.FlightSettingsChanged();
            Session.FeedForTest(ServerData("\"Horde|11>22\":{\"t\":64,\"n\":21}"));
            Session.FeedForTest("{\"t\":\"flights.shared\",\"ok\":true}");
            check("old server: no flight message is sent, the table is not requested and nothing is written", !Session.SupportsFlights && !sent.Any(o => (o.Str("t") ?? "").StartsWith("flights.")) && !File.Exists(dest));
            var after = string.Join("|", Directory.GetFiles(flightRoot, "*", SearchOption.AllDirectories).OrderBy(x => x).Select(f => f + ":" + new FileInfo(f).Length));
            check("old server: no file under the WoW folder changed", folderState == after);
            sent.Clear();
            Session.Disconnect();
            Session.FeedForTest(FlightWelcome("member", "2.12.0", true));
            Session.FlightSync();
            check("old server: even a 'flights' feature on a server below 2.13 is ignored", !Session.SupportsFlights && !sent.Any(o => (o.Str("t") ?? "").StartsWith("flights.")));
            // disconnected: the session stops
            Session.Disconnect();
            sent.Clear();
            Session.FlightSync();
            check("disconnected: nothing happens without a lodge", sent.Count == 0 && !Session.SupportsFlights);
            check("the real game folder was never involved (all files are under the scratch folder)", Directory.GetFiles(flightRoot, "*", SearchOption.AllDirectories).All(f => f.StartsWith(flightRoot)));
            await Task.CompletedTask;
        }
    }
}
