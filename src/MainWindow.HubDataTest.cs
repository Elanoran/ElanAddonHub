using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // --selftest "hubdata": the Outpost side of Elan's Hub data layer (Services/HubData.cs, Services/SavedVarsFiles.cs). Fixture folders under the scratch
    // directory only (never the real WoW folder): parsing ElansHubData (core v1, account modules, a module newer than this app understands = TooNew with no data,
    // unknown modules exposed, no ElansHubData at all, garbage that must not throw), the newest file winning, the parse cache, a locked or damaged file keeping
    // the last good read, and the shared SavedVariables finder (several flavors / accounts, a link is skipped, a path is not a file name).
    public partial class MainWindow
    {
        const string HubSample = @"
ElansHubDB = { [""current""] = ""Realm-Elan"", [""chars""] = { } }
ElansHubData = {
	[""schema""] = 1,
	[""chars""] = {
		[""Realm-Elan""] = {
			[""at""] = 1790000500,
			[""mods""] = {
				[""core""] = { [""v""] = 1, [""at""] = 1790000500, [""by""] = ""ElansHub"", [""ver""] = ""1.11.0"", [""data""] = {
					[""name""] = ""Elan"", [""realm""] = ""Realm"", [""class""] = ""Hunter"", [""classFile""] = ""HUNTER"", [""race""] = ""Orc"", [""raceFile""] = ""Orc"", [""sex""] = 2,
					[""faction""] = ""Horde"", [""level""] = 60, [""xp""] = 1500, [""xpMax""] = 4500, [""rested""] = 300, [""money""] = 123456789, [""guild""] = ""The Lodge"",
					[""zone""] = ""Orgrimmar"", [""subzone""] = ""Valley of Strength"", [""updated""] = 1790000499, [""somethingNew""] = true,
				} },
				[""hunter.pets""] = { [""v""] = 3, [""at""] = 1790000400, [""by""] = ""ElansHunterHelper"", [""ver""] = ""1.26.0"", [""data""] = { [""count""] = 4 } },
			},
		},
		[""Realm-Old""] = {
			[""at""] = 1780000000,
			[""mods""] = {
				[""core""] = { [""v""] = 2, [""at""] = 1780000000, [""by""] = ""ElansHub"", [""ver""] = ""9.0.0"", [""data""] = { [""name""] = ""Old"", [""level""] = 12 } },
			},
		},
	},
	[""account""] = { [""mods""] = { [""hunter.flight""] = { [""v""] = 1, [""at""] = 1790000300, [""by""] = ""ElansHunterHelper"", [""ver""] = ""1.26.0"", [""data""] = { [""legs""] = { 1, 2 } } } } },
}
";

        string HubFile(string account, string flavor = "_classic_beta_") => Path.Combine(hubRoot, flavor, "WTF", "Account", account, "SavedVariables", "ElansHub.lua");
        string hubRoot;

        void WriteHubFile(string account, string text, DateTime? stamp = null, string flavor = "_classic_beta_")
        {
            var f = HubFile(account, flavor);
            Directory.CreateDirectory(Path.GetDirectoryName(f));
            File.WriteAllText(f, text);
            if (stamp != null) File.SetLastWriteTimeUtc(f, stamp.Value);
        }

        async Task<string> HubDataTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            Action<string, bool> check = (what, cond) => { notes.Add((cond ? "ok   " : "FAIL ") + what); ok &= cond; };
            hubRoot = Path.Combine(dir, "hubdatawow");
            try
            {
                if (Directory.Exists(hubRoot)) Util.DeleteDir(hubRoot);
                Directory.CreateDirectory(hubRoot);
                check("fixture: the WoW folder of this test is under the scratch folder (never the real game)", Path.GetFullPath(hubRoot).StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase));
                HubData.ResetCache();

                // ---- parsing
                var m = HubData.Parse(HubSample);
                check("parse: ElansHubData found, schema 1, two characters, newest first", m.Found && m.Schema == 1 && m.Characters.Count == 2 && m.Characters[0].Key == "Realm-Elan" && m.Characters[1].Key == "Realm-Old");
                var elan = m.Char("realm-elan");
                var core = elan?.Core;
                check("parse: core v1 gives a typed HubCore (identity, level, xp, rested, money, guild, zone)",
                    core != null && core.Name == "Elan" && core.Realm == "Realm" && core.Class == "Hunter" && core.ClassFile == "HUNTER" && core.Race == "Orc" && core.RaceFile == "Orc" && core.Sex == 2
                    && core.Faction == "Horde" && core.Level == 60 && core.Xp == 1500 && core.XpMax == 4500 && core.Rested == 300 && core.Money == 123456789L && core.Guild == "The Lodge"
                    && core.Zone == "Orgrimmar" && core.Subzone == "Valley of Strength" && core.Updated == 1790000499L);
                var cm = elan.Modules["core"];
                check("parse: module metadata (v, at, by, ver) is kept and an unknown extra field is ignored", cm.V == 1 && cm.At == 1790000500L && cm.By == "ElansHub" && cm.Ver == "1.11.0" && !cm.TooNew);
                check("parse: a module this app has no table entry for is exposed as stored, not TooNew", elan.Modules["hunter.pets"].V == 3 && !elan.Modules["hunter.pets"].TooNew && elan.Modules["hunter.pets"].Data != null && elan.Modules["hunter.pets"].Data.Int("count") == 4);
                var old = m.Char("Realm-Old");
                check("parse: core v2 is newer than Supported[core]=1: marked TooNew, Data not exposed, no typed core", HubData.Supported["core"] == 1 && old.Modules["core"].TooNew && old.Modules["core"].V == 2 && old.Modules["core"].Data == null && old.Core == null);
                check("parse: account modules are read", m.AccountModules.ContainsKey("hunter.flight") && m.AccountModules["hunter.flight"].V == 1 && m.AccountModules["hunter.flight"].Data.Child("legs").Count == 2);
                check("parse: the legacy ElansHubDB next to it does not get in the way", m.Characters.All(c => c.Key.StartsWith("Realm-")));

                var none = HubData.Parse("ElansHubDB = { [\"current\"] = \"Realm-Elan\" }\n");
                check("parse: a file without ElansHubData gives an empty model (nothing found, no characters)", !none.Found && none.Characters.Count == 0 && none.AccountModules.Count == 0);
                var threw = false; HubModel g1 = null, g2 = null, g3 = null, g4 = null;
                try
                {
                    g1 = HubData.Parse("\0\0\u0001\u0002 garbage {{{ = = ]]] \"unterminated");
                    g2 = HubData.Parse("ElansHubData = { [\"chars\"] = { [\"A-B\"] = { [\"mods\"] = { core = 5, x = { v = \"no\" }, y = { v = 1e999 } } } } ");
                    g3 = HubData.Parse("ElansHubData = 7");
                    g4 = HubData.Parse(null);
                }
                catch (Exception) { threw = true; }
                check("parse: garbage, a half file, a number instead of a table and null do not throw", !threw && g1 != null && g2 != null && g3 != null && g4 != null);
                check("parse: garbage gives an empty model, a non-table ElansHubData is not found", !g1.Found && g1.Characters.Count == 0 && !g3.Found && !g4.Found);
                check("parse: a module entry that is not a table or has no integer v is skipped, not guessed", g2.Characters.All(c => !c.Modules.ContainsKey("core") && !c.Modules.ContainsKey("x")));
                var big = new StringBuilder("ElansHubData = { [\"schema\"] = 1, [\"chars\"] = {");
                for (int i = 0; i < 300; i++) big.Append("[\"R-C" + i + "\"] = { [\"at\"] = " + i + ", [\"mods\"] = { core = { v = 1, at = 1, data = { name = \"C" + i + "\", level = " + (i % 60) + " } } } },");
                big.Append("} }");
                var bm = HubData.Parse(big.ToString());
                check("parse: 300 characters, newest first", bm.Characters.Count == 300 && bm.Characters[0].Key == "R-C299" && bm.Characters[0].Core.Name == "C299");

                // ---- reading from the WoW folder
                check("read: no WoW folder / no file gives an empty model", !HubData.Read(null).Found && !HubData.Read(hubRoot).Found && HubData.Read(hubRoot).Characters.Count == 0);
                var t0 = new DateTime(2026, 10, 12, 10, 0, 0, DateTimeKind.Utc);
                WriteHubFile("ACC1", HubSample.Replace("Elan", "FromOld"), t0);
                WriteHubFile("ACC2", HubSample, t0.AddHours(2), "_classic_");
                WriteHubFile("ACC3", "ElansHubDB = { }\n", t0.AddHours(-5));                 // older and without ElansHubData: not the newest anyway
                HubData.ResetCache();
                var r1 = HubData.Read(hubRoot);
                check("read: the newest ElansHub.lua among all flavors and accounts wins", r1.Found && r1.SourceFile == HubFile("ACC2", "_classic_") && r1.Char("Realm-Elan").Core.Name == "Elan");
                check("read: the same file is not parsed again (same model instance)", ReferenceEquals(HubData.Read(hubRoot), r1));
                WriteHubFile("ACC2", HubSample.Replace("[\"level\"] = 60", "[\"level\"] = 61"), t0.AddHours(3), "_classic_");
                var r2 = HubData.Read(hubRoot);
                check("read: a changed file (mtime / size) is parsed again", !ReferenceEquals(r1, r2) && r2.Char("Realm-Elan").Core.Level == 61);

                // WoW may still hold the file: read with FileShare.ReadWrite works; a file locked for good keeps the last good read
                var path = HubFile("ACC2", "_classic_");
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
                {
                    HubData.ResetCache();
                    check("read: a file the game still has open for writing (shared) is readable", HubData.Read(hubRoot).Char("Realm-Elan")?.Core?.Level == 61);
                }
                var good = HubData.Read(hubRoot);
                File.WriteAllText(path, HubSample.Replace("[\"level\"] = 60", "[\"level\"] = 62") + "-- padding\n");
                File.SetLastWriteTimeUtc(path, t0.AddHours(4));
                HubModel locked;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) locked = HubData.Read(hubRoot);
                check("read: a file locked exclusively keeps the last good model, no exception", ReferenceEquals(locked, good) && locked.Char("Realm-Elan").Core.Level == 61);
                check("read: once the lock is gone the new content is picked up", HubData.Read(hubRoot).Char("Realm-Elan").Core.Level == 62);
                File.WriteAllText(path, "\0\0 not lua at all {{{");
                File.SetLastWriteTimeUtc(path, t0.AddHours(5));
                check("read: a damaged newest file gives an empty model without throwing", !HubData.Read(hubRoot).Found);

                // ---- the shared SavedVariables finder
                HubData.ResetCache();
                var all = SavedVarsFiles.For(hubRoot, "ElansHub.lua");
                check("finder: every flavor and account with the file, none without it (3 files)", all.Count == 3 && all.All(f => f.EndsWith("ElansHub.lua")));
                check("finder: another addon's file name finds nothing here; a path or empty name is refused", SavedVarsFiles.For(hubRoot, "Other.lua").Count == 0 && SavedVarsFiles.For(hubRoot, "..\\ElansHub.lua").Count == 0 && SavedVarsFiles.For(hubRoot, "a/b.lua").Count == 0 && SavedVarsFiles.For(hubRoot, "").Count == 0 && SavedVarsFiles.For(hubRoot + "-nope", "ElansHub.lua").Count == 0);
                var outside = Path.Combine(dir, "hubdata-outside");
                Directory.CreateDirectory(Path.Combine(outside, "SavedVariables"));
                File.WriteAllText(Path.Combine(outside, "SavedVariables", "ElansHub.lua"), HubSample);
                var junction = Path.Combine(hubRoot, "_retail_", "WTF", "Account", "LINK");
                Directory.CreateDirectory(Path.GetDirectoryName(junction));
                bool made = false;
                try
                {
                    var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + junction + "\" \"" + outside + "\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true });
                    p.WaitForExit(10000); made = p.ExitCode == 0 && Directory.Exists(junction);
                }
                catch { }
                if (made)
                {
                    check("finder: an account folder that is a junction (link) is skipped", SavedVarsFiles.For(hubRoot, "ElansHub.lua").Count == 3 && !SavedVarsFiles.For(hubRoot, "ElansHub.lua").Any(f => f.Contains("LINK")));
                    try { Directory.Delete(junction); } catch { }
                }
                else notes.Add("skip finder junction check (could not create a junction here)");
                try { Util.DeleteDir(outside); } catch { }
            }
            catch (Exception e) { ok = false; notes.Add("crashed: " + e); }
            finally { HubData.ResetCache(); }
            await Task.CompletedTask;
            return "hubdata selftest: " + (ok ? "PASSED" : "FAILED") + "\r\n  " + string.Join("\r\n  ", notes);
        }
    }
}
