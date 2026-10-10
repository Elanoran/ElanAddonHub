using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    public partial class MainWindow
    {
        // a tiny Lua writer (WoW's SavedVariables style: tabs, ["key"] = value, "-- [n]" on array entries)
        static string LuaText(object o, int depth = 1)
        {
            string pad = new string('\t', depth), padEnd = new string('\t', depth - 1);
            switch (o)
            {
                case null: return "nil";
                case bool b: return b ? "true" : "false";
                case string s: return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"";
                case int i: return i.ToString();
                case long l: return l.ToString();
                case double d: return d.ToString(System.Globalization.CultureInfo.InvariantCulture);
                case List<object> list:
                {
                    var sb = new StringBuilder("{\n");
                    for (int n = 0; n < list.Count; n++) sb.Append(pad + LuaText(list[n], depth + 1) + ", -- [" + (n + 1) + "]\n");
                    return sb.Append(padEnd + "}").ToString();
                }
                case Dictionary<string, object> dict:
                {
                    var sb = new StringBuilder("{\n");
                    foreach (var kv in dict) sb.Append(pad + "[\"" + kv.Key + "\"] = " + LuaText(kv.Value, depth + 1) + ",\n");
                    return sb.Append(padEnd + "}").ToString();
                }
            }
            return "nil";
        }

        static Dictionary<string, object> D(params object[] kv)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
            return d;
        }
        static List<object> Lst(params object[] items) => items.ToList();
        static string SvFile(string db, Dictionary<string, object> body) => db + " = " + LuaText(body) + "\n";

        static Dictionary<string, object> HealthBugLua(string addon, string msg, string stack, int count, string version, long at) =>
            D("msg", msg, "stack", stack, "count", count, "time", DateTimeOffset.FromUnixTimeSeconds(at).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), "at", at, "version", version, "addon", addon);

        // Addon health from synthetic SavedVariables of two addons in a fake WoW folder (2.26.0)
        async Task<string> HealthTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            void Check(string what, bool cond, string detail = null) { notes.Add((cond ? "ok   " : "FAIL ") + what + (!cond && detail != null ? " [" + detail + "]" : "") + "  (t+" + (clock.ElapsedMilliseconds / 1000.0).ToString("0.0") + "s)"); ok &= cond; }
            string P(string n) => Path.Combine(dir, n);
            var s = settings;
            var seen0 = s.HealthSeen; var toasted0 = s.HealthToasted; bool off0 = s.ToastHealthOff, toastsOff0 = s.ToastsOff;
            var root0 = health?.Root;
            Stream hold = null;
            try
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var root = Path.Combine(dir, "healthwow");
                if (Directory.Exists(root)) Directory.Delete(root, true);
                var flavor = Path.Combine(root, "_classic_beta_");
                var sv = Path.Combine(flavor, "WTF", "Account", "TESTACC", "SavedVariables");
                Directory.CreateDirectory(sv);
                void Toc(string folder, string version)
                {
                    var d = Path.Combine(flavor, "Interface", "AddOns", folder);
                    Directory.CreateDirectory(d);
                    File.WriteAllText(Path.Combine(d, folder + "_Camelot.toc"), "## Interface: 16001\n## Title: " + folder + "\n## Version: " + version + "\n");
                }
                Toc("ElansBags", "0.3.1"); Toc("ElansHunterHelper", "1.23.1");

                string stack = "ElansBags/Window.lua:88: in function `Refresh'\nElansBags/Core.lua:140: in function <ElansBags/Core.lua:131>\n(tail call): ?";
                string msg1 = "Interface/AddOns/ElansBags/Window.lua:88: attempt to index field 'slots' (a nil value) while Aldrin was selling junk";
                var run = D("ctx", D("label", "out of combat (auto, first login of v0.3.1)", "inCombat", false, "time", "2026-10-10 14:30:12", "build", "1.60.1 12345 Oct 1 2026 toc 16001", "class", "str:\"HUNTER\""),
                    "res", D("api:issecretvalue", "yes", "api:C_GuildBank", "NO", "api:SortBags", "NO", "unit:player:UnitHealth", "S", "unit:player:UnitName", "str:\"Aldrin\"", "unit:party1:UnitName", "str:\"Brenna\"",
                             "bags:item(0,1)", "itemID=num:2589 stackCount=S quality=num:1", "bags:GetMoney", "ERR attempt to compare a secret value (Aldrin)"),
                    "ok", 38, "secret", 3, "err", 1, "at", now - 900, "version", "0.3.1", "auto", true);
                var runCombat = D("ctx", D("label", "in combat", "inCombat", true, "time", "2026-10-10 13:10:00", "build", "1.60.1 12345 Oct 1 2026 toc 16001"),
                    "res", D("api:issecretvalue", "yes", "unit:player:UnitHealth", "S"), "ok", 20, "secret", 1, "err", 0, "at", now - 5000, "version", "0.3.0");
                var chars = D(
                    "Forever-Aldrin", D("name", "Aldrin", "realm", "Forever", "class", "HUNTER", "guild", "Iron Wolves"),
                    "Forever-Brenna", D("name", "Brenna", "realm", "Forever", "class", "PALADIN"),
                    "Forever-Elan", D("name", "Elan", "realm", "Forever", "class", "MAGE"));
                File.WriteAllText(Path.Combine(sv, "ElansBags.lua"), SvFile("ElansBagsDB", D("lastVersion", "0.3.1", "hintVersion", "0.3.1", "chars", chars,
                    "bugs", Lst(HealthBugLua("ElansBags", msg1, stack, 3, "0.3.1", now - 600),
                                HealthBugLua("ElansBags", "Interface/AddOns/ElansBags/Junk.lua:12: bad argument #1 to 'ipairs' (table expected, got nil)", "ElansBags/Junk.lua:12: in function `Sell'", 1, "0.3.0", now - 86400 * 2)),
                    "diag", D("autoVersion", "0.3.1", "runs", Lst(run, runCombat)))));
                File.WriteAllText(Path.Combine(sv, "ElansHunterHelper.lua"), SvFile("ElansHunterHelperDB", D("lastVersion", "1.23.1",
                    "bugs", Lst(HealthBugLua("ElansHunterHelper", "Interface/AddOns/ElansHunterHelper/UI/ShotHUD.lua:310: attempt to perform arithmetic on a secret value", "ElansHunterHelper/UI/ShotHUD.lua:310: in function `Step'", 5, "1.23.0", now - 7200),
                                HealthBugLua("Questie", "Interface/AddOns/Questie/Modules/Tracker.lua:5: other people's problem", "Questie/Tracker.lua:5: x", 1, "9.9", now - 100),
                                HealthBugLua("ElansBags", msg1, stack, 2, "0.3.1", now - 900)),
                    "diag", D("runs", Lst(D("ctx", D("label", "in combat, 3 s after start", "inCombat", true, "time", "2026-10-09 20:00:00", "build", "1.60.1 12345 Oct 1 2026 toc 16001"),
                        "res", D("api:issecretvalue", "yes", "unit:target:UnitHealth", "S", "spell:C_Spell.GetSpellCooldown", "startTime=S duration=S"), "ok", 50, "secret", 4, "err", 0, "at", now - 86400, "version", "1.23.0"))))));
                File.WriteAllText(Path.Combine(sv, "ElansHub.lua"), "ElansHubDB = {{{ this file is not valid\n");   // must not break anything

                // ---- read it
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var rep = HealthReader.Read(root);
                sw.Stop();
                Check("reader: a damaged file is skipped quickly (" + sw.ElapsedMilliseconds + " ms) and flagged unreadable", sw.ElapsedMilliseconds < 3000 && rep.Addons.First(x => x.Def.Id == "ElansHub").Unreadable);
                var bags = rep.Addons.FirstOrDefault(a => a.Def.Id == "ElansBags");
                var hunter = rep.Addons.FirstOrDefault(a => a.Def.Id == "ElansHunterHelper");
                Check("reader: both addons found (+ the broken companion file listed without data)", bags != null && hunter != null && rep.Addons.Count == 3 && !rep.Addons.First(a => a.Def.Id == "ElansHub").Bugs.Any());
                Check("reader: installed version from the toc", bags?.Installed == "0.3.1" && hunter?.Installed == "1.23.1");
                Check("reader: Bags diag version 0.3.1 matches, Hunter diag 1.23.0 is behind", bags?.DiagVersion == "0.3.1" && !bags.DiagBehind && hunter?.DiagVersion == "1.23.0" && hunter.DiagBehind);
                Check("reader: bug entries parsed (msg, count, version, stack)", bags != null && bags.Bugs.Count == 2 && bags.Bugs[0].Count == 3 && bags.Bugs[0].Version == "0.3.1" && bags.Bugs[0].FirstLine.Contains("Window.lua:88"));
                Check("reader: the same error caught in two files counts once; the older-version one is marked", bags.Bugs.Count == 2 && bags.Bugs.Single(b => b.Version == "0.3.0").Older && !bags.Bugs[0].Older);
                Check("reader: other people's addon errors are not listed", hunter.Bugs.Count == 1 && hunter.OtherErrors == 1 && hunter.Bugs[0].Count == 5);
                Check("reader: in/out of combat runs", bags.Runs.Count == 2 && !bags.Runs[0].InCombat && bags.Runs[0].Auto && bags.Runs[1].InCombat);
                var facts = string.Join("\n", HealthReader.Facts(bags));
                Check("facts: secret values, probe errors and missing APIs summarized", facts.Contains("Secret values:") && facts.Contains("unit:player:UnitHealth") && facts.Contains("Probe errors:") && facts.Contains("Missing APIs:") && facts.Contains("C_GuildBank") && facts.Contains("1 out of combat, 1 in combat"), facts);

                // ---- the app: monitor + chip + rail badge + panel
                s.ToastsOff = false; s.ToastHealthOff = false; s.HealthSeen = null; s.HealthToasted = null;
                health.Root = () => root;
                var changes = 0; health.Changed += () => changes++;
                ShowTab("addons");
                health.ReloadNow();
                await Task.Delay(300);
                // 3 errors, but 2 come from an older version than the installed one (fixed by the update): only 1 is new
                Check("monitor: 3 errors listed, only the current-version one is new", health.NewCount == 1 && health.ErrorCount == 3, "new " + health.NewCount + " total " + health.ErrorCount);
                Check("chip says 'Health ⚠ 1'", HealthChipText.Text == "Health ⚠ 1", HealthChipText.Text);
                Check("rail: red count on the Addons item", HealthBadge.Visibility == Visibility.Visible && HealthBadgeText.Text == "1" && ((string)TabAddons.ToolTip).Contains("1 new addon error"));
                Check("the old errors at first start give no toast (baseline)", Session.Toasts.Visible.Count == 0 && Session.Toasts.HeldCount == 0);
                Snapshot(P("19-health-chip.png"));

                var dlg = HealthDialog.ShowFor(this, health, App.Version);
                await Task.Delay(500);
                SnapshotOn(dlg.RootForTest, P("19-health-panel.png"), Color.FromRgb(0x0F, 0x11, 0x13));
                dlg.ExpandForTest(true);
                await Task.Delay(300);
                SnapshotOn(dlg.RootForTest, P("19-health-panel-expanded.png"), Color.FromRgb(0x0F, 0x11, 0x13));
                dlg.ExpandForTest(false);

                // copy report
                var text = dlg.Report();
                dlg.CopyForTest();
                string clip = null; try { clip = Clipboard.GetText(); } catch { }
                Check("copy report: the clipboard has the report (" + text.Length + " chars)", dlg.LastCopied == text && (clip == null || clip == text));
                File.WriteAllText(P("19-health-report.txt"), text);
                Check("report: no character names (Aldrin, Brenna) or guild", !text.Contains("Aldrin") && !text.Contains("Brenna") && !text.Contains("Iron Wolves"), text);
                Check("report: names replaced by char1 / char2", text.Contains("char1") && text.Contains("char2"));
                Check("report: the brand 'Elan's Bags' survives a character called Elan", text.Contains("Elan's Bags") && text.Contains("Elan's Hunter Helper") && !text.Contains("char3's"));
                Check("report: no account folder, no Windows user", !text.Contains("TESTACC") && !text.Contains(Environment.UserName + "\\"));
                Check("report: errors, stack and diag results are in it", text.Contains("attempt to index field 'slots'") && text.Contains("Window.lua:88") && text.Contains("unit:player:UnitHealth = S") && text.Contains("x3") && text.Contains("(older version)"));
                Check("report: third-party addon errors are not in it", !text.Contains("Questie") && !text.Contains("other people's problem"));
                Check("report: both addons with installed vs diag version", text.Contains("Installed: v0.3.1 · diag: v0.3.1") && text.Contains("Installed: v1.23.1 · diag: v1.23.0"));

                // the file being written by WoW (open for writing, shared) is still readable
                hold = new FileStream(Path.Combine(sv, "ElansBags.lua"), FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                health.ReloadNow();
                Check("read-only: a file open for writing by the game still reads", health.ErrorCount == 3);
                hold.Dispose(); hold = null;

                // mark as seen
                health.MarkSeen();
                Check("mark as seen: nothing new, chip 'Health ✓', badge gone", health.NewCount == 0 && HealthChipText.Text == "Health ✓" && HealthBadge.Visibility == Visibility.Collapsed);
                Check("mark as seen keeps the errors in the list", health.ErrorCount == 3);

                // a new error appears after a /reload: file change -> watcher + debounce -> toast
                var T = Session.Toasts;
                T.Visible.Clear();
                var before = changes;
                var more = Lst(HealthBugLua("ElansBags", msg1, stack, 3, "0.3.1", now - 600),
                               HealthBugLua("ElansBags", "Interface/AddOns/ElansBags/Window.lua:101: attempt to call method 'Foo' (a nil value)", "ElansBags/Window.lua:101: in function `Paint'", 1, "0.3.1", now - 30),
                               HealthBugLua("ElansBags", "Interface/AddOns/ElansBags/Junk.lua:12: bad argument #1 to 'ipairs' (table expected, got nil)", "ElansBags/Junk.lua:12: in function `Sell'", 1, "0.3.0", now - 86400 * 2),
                               HealthBugLua("ElansBags", "Interface/AddOns/ElansBags/Items.lua:7: second new one", "ElansBags/Items.lua:7: x", 1, "0.3.1", now - 20));
                // combat first: the toast waits
                Session.Presence.SetForTest(new GamePresence.Character { Name = "Aldrin", ClassFile = "HUNTER", Level = 60, HasLive = true, CombatKnown = true, Flags = 1 + 16 + 64 + 128 }, true);
                health.EnsureWatchers();
                File.WriteAllText(Path.Combine(sv, "ElansBags.lua"), SvFile("ElansBagsDB", D("lastVersion", "0.3.1", "hintVersion", "0.3.1", "chars", chars, "bugs", more,
                    "diag", D("autoVersion", "0.3.1", "runs", Lst(run, runCombat)))));
                for (int i = 0; i < 40 && changes == before; i++) await Task.Delay(150);
                await Task.Delay(300);
                Check("watcher: a changed SavedVariables file is re-read by itself", changes > before && health.ErrorCount == 5, "changes " + (changes - before) + ", errors " + health.ErrorCount);
                Check("new errors: counted (2 new)", health.NewCount == 2);
                Check("toast in combat: held, not shown", T.Visible.Count == 0 && T.HeldCount == 1, "visible " + T.Visible.Count + " held " + T.HeldCount);
                Session.Presence.SetForTest(new GamePresence.Character { Name = "Aldrin", ClassFile = "HUNTER", Level = 60, HasLive = true, CombatKnown = true, Flags = 16 + 64 + 128 }, true);
                T.Tick();
                Check("toast after combat: \"Elan's Bags: 2 new errors\"", T.Visible.Count == 1 && T.Visible[0].Kind == ToastKind.Health && T.Visible[0].Title == "Elan's Bags: 2 new errors" && T.Visible[0].Text.Contains("Health"),
                    T.Visible.Count > 0 ? T.Visible[0].Title : "none");
                LodgePage.ForceToastsForTest = true;
                await Task.Delay(900); T.Tick(); await Task.Delay(300); T.Tick();
                LodgePage.SnapshotToasts(P("19-health-toast.png"));
                LodgePage.ForceToastsForTest = false;
                Snapshot(P("19-health-chip-new.png"));
                Session.Presence.SetForTest(null, false);

                // the setting turns the toast off; the global switch too
                T.Visible.Clear();
                s.ToastHealthOff = true;
                File.AppendAllText(Path.Combine(sv, "ElansBags.lua"), "-- touched\n");
                var more2 = new List<object>(more) { HealthBugLua("ElansBags", "Interface/AddOns/ElansBags/Alts.lua:3: third new one", "x", 1, "0.3.1", now - 5) };
                File.WriteAllText(Path.Combine(sv, "ElansBags.lua"), SvFile("ElansBagsDB", D("lastVersion", "0.3.1", "chars", chars, "bugs", more2, "diag", D("autoVersion", "0.3.1", "runs", Lst(run)))));
                health.ReloadNow();
                Check("setting 'New addon errors' off: no toast, chip still counts", T.Visible.Count == 0 && T.HeldCount == 0 && health.NewCount == 3);
                s.ToastHealthOff = false;

                // no data at all: the empty state
                health.Root = () => Path.Combine(dir, "does-not-exist");
                health.ReloadNow();
                await Task.Delay(300);
                Check("no WoW data: chip is quiet, rail badge gone", HealthChipText.Text.StartsWith("Health") && HealthBadge.Visibility == Visibility.Collapsed && health.Report.Addons.Count == 0);
                SnapshotOn(dlg.RootForTest, P("19-health-panel-empty.png"), Color.FromRgb(0x0F, 0x11, 0x13));
                dlg.Close();
            }
            catch (Exception e) { ok = false; notes.Add("FAIL exception: " + e); }
            finally
            {
                try { hold?.Dispose(); } catch { }
                s.HealthSeen = seen0; s.HealthToasted = toasted0; s.ToastHealthOff = off0; s.ToastsOff = toastsOff0;
                SettingsStore.Save(s);
                if (health != null) { health.Root = root0 ?? (() => settings.WowRoot); health.ReloadNow(); }
                Session.Presence.SetForTest(null, false);
                Session.Toasts.Visible.Clear();
                ShowTab("addons");
            }
            notes.Insert(0, "health (2.26.0): " + (ok ? "all ok" : "FAILURES"));
            return string.Join("\r\n", notes);
        }
    }
}
