using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    public partial class MainWindow
    {
        // Addon health through Elan's Hub (module `health` v1, Outpost 2.46.0): Hub only, legacy only, mixed, TooNew -> legacy,
        // "other" errors, names from the Hub core module for the anonymizer, and ElansHub.lua changes re-reading the report.
        // Returns the check lines; the caller adds them to the health section and fails the section on any FAIL.
        async Task<List<string>> HealthHubTest(string dir)
        {
            var notes = new List<string>();
            var s = settings;
            var seen0 = s.HealthSeen; var toasted0 = s.HealthToasted; bool toastsOff0 = s.ToastsOff;
            var root0 = health?.Root;
            void Check(string what, bool cond, string detail = null) => notes.Add((cond ? "ok   " : "FAIL ") + what + (!cond && detail != null ? " [" + detail + "]" : ""));
            try
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                string stack = "ElansBags/Window.lua:88: in function `Refresh'\nElansBags/Core.lua:140: in function <ElansBags/Core.lua:131>";
                string msgBags = "Interface/AddOns/ElansBags/Window.lua:88: attempt to index field 'slots' (a nil value) while Zephyra was selling junk";
                string msgOld = "Interface/AddOns/ElansBags/Junk.lua:12: bad argument #1 to 'ipairs' (table expected, got nil)";
                string msgHunter = "Interface/AddOns/ElansHunterHelper/UI/ShotHUD.lua:310: attempt to perform arithmetic on a secret value";
                string msgQuestie = "Interface/AddOns/Questie/Modules/Tracker.lua:5: other people's problem";
                Func<List<object>> bagsBugs = () => Lst(HealthBugLua("ElansBags", msgBags, stack, 3, "0.3.1", now - 600), HealthBugLua("ElansBags", msgOld, "ElansBags/Junk.lua:12: in function `Sell'", 1, "0.3.0", now - 86400 * 2));
                Func<List<object>> hunterBugs = () => Lst(HealthBugLua("ElansHunterHelper", msgHunter, "ElansHunterHelper/UI/ShotHUD.lua:310: in function `Step'", 5, "1.23.0", now - 7200));
                var bagsRun = D("ctx", D("label", "out of combat (auto, first login of v0.3.1)", "inCombat", false, "time", "2026-10-10 14:30:12", "build", "1.60.1 12345 Oct 1 2026 toc 16001"),
                    "res", D("api:issecretvalue", "yes", "api:C_GuildBank", "NO", "unit:player:UnitHealth", "S", "bags:GetMoney", "ERR attempt to compare a secret value"),
                    "ok", 38, "secret", 3, "err", 1, "at", now - 900, "version", "0.3.1", "auto", true);
                var hunterRun = D("ctx", D("label", "in combat", "inCombat", true, "time", "2026-10-09 20:00:00", "build", "1.60.1 12345 Oct 1 2026 toc 16001"),
                    "res", D("api:issecretvalue", "yes", "unit:target:UnitHealth", "S"), "ok", 50, "secret", 4, "err", 0, "at", now - 86400, "version", "1.23.0");
                Func<string, string, string, string, string, Dictionary<string, object>> entry = (version, last, auto, bugsName, runName) =>
                    D("version", version, "bugs", bugsName == "bags" ? bagsBugs() : hunterBugs(),
                      "diag", D("runs", runName == "bags" ? Lst(bagsRun) : Lst(hunterRun), "autoVersion", auto, "lastVersion", last));

                string NewRoot(string name, bool toolkit, out string sv)
                {
                    var root = Path.Combine(dir, name);
                    if (Directory.Exists(root)) Directory.Delete(root, true);
                    var flavor = Path.Combine(root, "_classic_beta_");
                    sv = Path.Combine(flavor, "WTF", "Account", "TESTACC", "SavedVariables");
                    Directory.CreateDirectory(sv);
                    foreach (var kv in new[] { ("ElansBags", "0.3.1"), ("ElansHunterHelper", "1.23.1"), ("ElansToolkit", "0.3.1") })
                    {
                        if (kv.Item1 == "ElansToolkit" && !toolkit) continue;
                        var d = Path.Combine(flavor, "Interface", "AddOns", kv.Item1);
                        Directory.CreateDirectory(d);
                        File.WriteAllText(Path.Combine(d, kv.Item1 + "_Camelot.toc"), "## Interface: 16001\n## Title: " + kv.Item1 + "\n## Version: " + kv.Item2 + "\n");
                    }
                    return root;
                }
                // ElansHub.lua: one character with a core module (names for the anonymizer) + the account module `health`
                void WriteHub(string sv, int v, Dictionary<string, object> addons, List<object> otherBugs)
                {
                    var body = addons == null ? null : D("addons", addons);
                    if (body != null && otherBugs != null) body["other"] = D("bugs", otherBugs);
                    var mods = D();
                    if (body != null) mods["health"] = D("v", v, "at", now, "by", "ElansToolkit", "ver", "0.3.1", "data", body);
                    File.WriteAllText(Path.Combine(sv, "ElansHub.lua"), SvFile("ElansHubData", D("schema", 1,
                        "chars", D("Forever-Zephyra", D("at", now, "mods", D("core", D("v", 1, "at", now, "by", "ElansHub", "ver", "1.13.0",
                            "data", D("name", "Zephyra", "realm", "Forever", "class", "MAGE", "guild", "Moon Cats", "level", 60))))),
                        "account", D("mods", mods))));
                }
                void WriteLegacyBags(string sv, List<object> bugs) =>
                    File.WriteAllText(Path.Combine(sv, "ElansBags.lua"), SvFile("ElansBagsDB", D("lastVersion", "0.3.1", "bugs", bugs, "diag", D("autoVersion", "0.3.1", "runs", Lst(bagsRun)))));
                void WriteLegacyHunter(string sv, List<object> bugs) =>
                    File.WriteAllText(Path.Combine(sv, "ElansHunterHelper.lua"), SvFile("ElansHunterHelperDB", D("lastVersion", "1.23.1", "bugs", bugs, "diag", D("runs", Lst(hunterRun)))));
                string Norm(HealthReport r) => Regex.Replace(Regex.Replace(HealthReader.ReportText(r, "x", false), @"^Outpost v.*$", "", RegexOptions.Multiline), @" written \d{4}-\d\d-\d\d \d\d:\d\d", " written T");

                // ---- Hub only (no addon file at all) and legacy only: the same data gives the same report text
                var hubOnlyRoot = NewRoot("healthhub-hubonly", false, out var svA);
                WriteHub(svA, 1, D("ElansBags", entry("0.3.1", "0.3.1", "0.3.1", "bags", "bags"), "ElansHunterHelper", entry("1.23.1", "1.23.1", "1.23.0", "hunter", "hunter")), null);
                var legacyRoot = NewRoot("healthhub-legacy", false, out var svB);
                WriteHub(svB, 1, null, null);   // the Hub's file exists (it always does) but has no health module
                File.WriteAllText(Path.Combine(svB, "ElansBags.lua"), SvFile("ElansBagsDB", D("lastVersion", "0.3.1", "bugs", bagsBugs(), "diag", D("autoVersion", "0.3.1", "runs", Lst(bagsRun)))));
                File.WriteAllText(Path.Combine(svB, "ElansHunterHelper.lua"), SvFile("ElansHunterHelperDB", D("lastVersion", "1.23.1", "bugs", hunterBugs(), "diag", D("autoVersion", "1.23.0", "runs", Lst(hunterRun)))));
                var hubRep = HealthReader.Read(hubOnlyRoot);
                var legRep = HealthReader.Read(legacyRoot);
                var hb = hubRep.Addons.FirstOrDefault(a => a.Def.Id == "ElansBags");
                var hh = hubRep.Addons.FirstOrDefault(a => a.Def.Id == "ElansHunterHelper");
                Check("hub only: both addons come from the Hub module (no addon file exists; the companion's own file is listed as before)", hb != null && hh != null && hb.FromHub && hh.FromHub && hubRep.Addons.Count == 3, hubRep.Addons.Count.ToString());
                Check("hub only: installed version from the toc, diag version, saved-with", hb.Installed == "0.3.1" && hb.DiagVersion == "0.3.1" && hb.SavedWith == "0.3.1" && hh.Installed == "1.23.1" && hh.DiagBehind);
                Check("hub only: bugs (msg, count, version, stack) and the older-version mark", hb.Bugs.Count == 2 && hb.Bugs[0].Count == 3 && hb.Bugs[0].Version == "0.3.1" && hb.Bugs[0].FirstLine.Contains("Window.lua:88") && hb.Bugs.Single(b => b.Version == "0.3.0").Older && !hb.Bugs[0].Older);
                Check("hub only: runs (auto flag, in combat) and the facts", hb.Runs.Count == 1 && hb.Runs[0].Auto && hh.Runs[0].InCombat && string.Join("\n", HealthReader.Facts(hb)).Contains("Secret values: unit:player:UnitHealth"));
                var legB = legRep.Addons.First(a => a.Def.Id == "ElansBags");
                Check("legacy only: same addons without the Hub", !legB.FromHub && legRep.Addons.Count == 3 && legB.Bugs.Count == 2);
                Check("report text is identical for Hub-only and legacy-only data", Norm(hubRep) == Norm(legRep), "hub " + Norm(hubRep).Length + " legacy " + Norm(legRep).Length);

                // ---- mixed: Bags in the Hub, Hunter Helper only in its own file (an older addon); the Hub wins for Bags even when its own file has other data
                var mixRoot = NewRoot("healthhub-mixed", true, out var svM);
                WriteHub(svM, 1, D("ElansBags", entry("0.3.1", "0.3.1", "0.3.1", "bags", "bags"), "ElansToolkit", D("version", "0.3.1", "bugs", Lst(), "diag", D("runs", Lst(), "lastVersion", "0.3.1"))), Lst(HealthBugLua("Questie", msgQuestie, "Questie/Tracker.lua:5: x", 1, "9.9", now - 100)));
                WriteLegacyBags(svM, Lst(HealthBugLua("ElansBags", "Interface/AddOns/ElansBags/Legacy.lua:1: only in the old file", "x", 1, "0.3.1", now - 50)));
                WriteLegacyHunter(svM, hunterBugs());
                var mix = HealthReader.Read(mixRoot);
                var mb = mix.Addons.First(a => a.Def.Id == "ElansBags");
                var mh = mix.Addons.First(a => a.Def.Id == "ElansHunterHelper");
                var mt = mix.Addons.First(a => a.Def.Id == "ElansToolkit");
                Check("mixed: Bags from the Hub (its own file's other error is not used)", mb.FromHub && mb.Bugs.Count == 2 && mb.Bugs.All(b => !b.Msg.Contains("only in the old file")));
                Check("mixed: Hunter Helper from its own saved variables", !mh.FromHub && mh.Bugs.Count == 1 && mh.Bugs[0].Count == 5 && mh.Runs.Count == 1 && mh.SavedWith == "1.23.1");
                Check("mixed: the Toolkit's entry is read, installed version from the toc", mt.FromHub && mt.Bugs.Count == 0 && mt.Installed == "0.3.1");

                // ---- "other": a non-Elan addon's error is counted on the host (the Toolkit), never listed or put in the report
                Check("other: counted as 'other addons' on the Toolkit, not listed", mt.OtherErrors == 1 && mix.Addons.SelectMany(a => a.Bugs).All(b => !b.Msg.Contains("Questie")));
                var mixText = HealthReader.ReportText(mix, "x", false);
                Check("other: not in the report text either, but the count is", !mixText.Contains("Questie") && !mixText.Contains("other people's problem") && mixText.Contains("1 entries about other addons not listed"));
                // an error of another Elan addon that the host kept (e.g. the companion in mode "all") is moved to that addon, like before
                WriteHub(svM, 1, D("ElansBags", entry("0.3.1", "0.3.1", "0.3.1", "bags", "bags"), "ElansToolkit", D("version", "0.3.1", "bugs", Lst(), "diag", D("runs", Lst(), "lastVersion", "0.3.1"))),
                    Lst(HealthBugLua("ElansHub", "Interface/AddOns/ElansHub/Core.lua:9: companion trouble", "ElansHub/Core.lua:9: x", 2, "1.13.0", now - 80), HealthBugLua("Plater", "Interface/AddOns/Plater/x.lua:1: nope", "x", 1, "1", now - 70)));
                var mix2 = HealthReader.Read(mixRoot);
                var comp = mix2.Addons.FirstOrDefault(a => a.Def.Id == "ElansHub");
                Check("other: an Elan addon's error in 'other' is filed under that addon, a third party's is only counted",
                    comp != null && comp.Bugs.Count == 1 && comp.Bugs[0].Count == 2 && mix2.Addons.First(a => a.Def.Id == "ElansToolkit").OtherErrors == 1 && !mix2.Addons.First(a => a.Def.Id == "ElansToolkit").Bugs.Any());

                // ---- a newer schema than this Outpost understands: not parsed, the addons' own files are used
                Check("Supported: health is v1", HubData.Supported.TryGetValue("health", out var hv) && hv == 1);
                var newRoot = NewRoot("healthhub-toonew", true, out var svN);
                WriteHub(svN, 2, D("ElansBags", entry("0.3.1", "0.3.1", "0.3.1", "bags", "bags")), null);
                WriteLegacyBags(svN, Lst(HealthBugLua("ElansBags", "Interface/AddOns/ElansBags/Legacy.lua:1: from the old file", "x", 1, "0.3.1", now - 50)));
                var nw = HealthReader.Read(newRoot);
                var nb = nw.Addons.First(a => a.Def.Id == "ElansBags");
                Check("too new: the Hub module is ignored and the addon's own tables are read", !nb.FromHub && nb.Bugs.Count == 1 && nb.Bugs[0].Msg.Contains("from the old file"));
                Check("too new: no 'other' errors are taken either", nw.Addons.All(a => a.OtherErrors == 0));

                // ---- names from the Hub core module (character, guild) are hidden in the anonymous report, with no addon file naming them
                var anon = HealthReader.ReportText(hubRep, "x", true);
                var plain = HealthReader.ReportText(hubRep, "x", false);
                Check("anonymizer: the character name reaches the plain report (test is meaningful)", plain.Contains("Zephyra"));
                Check("anonymizer: Hub core names are replaced (Zephyra -> charN)", !anon.Contains("Zephyra") && anon.Contains("char1 was selling junk"), anon);
                Check("anonymizer: Hub core names list on the report", hubRep.Names.Contains("Zephyra") && hubRep.Guilds.Contains("Moon Cats"));
                Check("anonymizer: the guild name is hidden too", HealthReader.Anonymize("bank of Moon Cats", hubRep.Names, hubRep.Guilds) == "bank of guild1");

                // ---- an ElansHub.lua change re-reads the report by itself (the monitor watches Elans*.lua)
                s.ToastsOff = true; s.HealthSeen = null; s.HealthToasted = null;
                var watchRoot = NewRoot("healthhub-watch", true, out var svW);
                WriteHub(svW, 1, D("ElansBags", entry("0.3.1", "0.3.1", "0.3.1", "bags", "bags")), null);
                health.Root = () => watchRoot;
                health.EnsureWatchers();
                health.ReloadNow();
                int first = health.ErrorCount, changes = 0;
                Action onChanged = () => changes++;
                health.Changed += onChanged;
                try
                {
                    var more = bagsBugs(); more.Add(HealthBugLua("ElansBags", "Interface/AddOns/ElansBags/Items.lua:7: new through the hub", "x", 1, "0.3.1", now - 20));
                    var e2 = entry("0.3.1", "0.3.1", "0.3.1", "bags", "bags"); e2["bugs"] = more;
                    WriteHub(svW, 1, D("ElansBags", e2), null);
                    for (int i = 0; i < 40 && changes == 0; i++) await Task.Delay(150);
                    await Task.Delay(300);
                    Check("watcher: a changed ElansHub.lua is re-read by itself", changes > 0 && first == 2 && health.ErrorCount == 3, "changes " + changes + ", errors " + first + " -> " + health.ErrorCount);
                }
                finally { health.Changed -= onChanged; }
            }
            catch (Exception e) { notes.Add("FAIL exception in the Hub health checks: " + e); }
            finally
            {
                s.HealthSeen = seen0; s.HealthToasted = toasted0; s.ToastsOff = toastsOff0;
                SettingsStore.Save(s);
                if (health != null) { health.Root = root0 ?? (() => settings.WowRoot); health.ReloadNow(); }
            }
            return notes;
        }
    }
}
