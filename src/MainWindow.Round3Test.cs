using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // 2.27.0 self-test: Lodge server notice, first-run guide, Privacy page, idle pause/resume (+ CPU), CurseForge status. Screenshots 20-* .. 24-*
    public partial class MainWindow
    {
        static string Welcome(string role, string server, bool canManage) =>
            "{\"t\":\"welcome\",\"you\":1,\"name\":\"Elan\",\"role\":\"" + role + "\",\"server\":\"" + server + "\",\"maxFileMb\":25,\"canShareFiles\":true,\"canModerate\":" + (canManage ? "true" : "false") + ",\"canManage\":" + (canManage ? "true" : "false") + ","
            + "\"features\":[\"reply\",\"react\",\"react2\",\"pin\",\"profile\"],\"channels\":[{\"id\":\"general\",\"name\":\"General\",\"type\":\"text\",\"minRole\":\"guest\"},{\"id\":\"hangout\",\"name\":\"Hangout\",\"type\":\"voice\",\"minRole\":\"guest\"}],"
            + "\"users\":[{\"id\":1,\"name\":\"Elan\",\"role\":\"" + role + "\"},{\"id\":2,\"name\":\"Bob\",\"role\":\"officer\"}],\"history\":{\"general\":[]},\"pins\":{}}";

        async Task<string> Round3Test(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            var clock = Stopwatch.StartNew();
            void Check(string what, bool cond, string detail = null) { notes.Add((cond ? "ok   " : "FAIL ") + what + (!cond && detail != null ? " [" + detail + "]" : "")); ok &= cond; }
            string P(string n) => Path.Combine(dir, n);
            var s = settings;
            var dismissed0 = s.LodgeUpdateDismissed; bool welcome0 = s.WelcomeDone, cfAuto0 = s.CfAutoCheck, shareZoneOff0 = s.ShareZoneOff, forget0 = s.InvisibleForget, inv0 = s.LodgeInvisible;
            var detector0 = WowWatch.Shared.Detector;
            try
            {
                Check("version is a valid x.y.z", System.Text.RegularExpressions.Regex.IsMatch(App.Version, @"^\d+\.\d+\.\d+$"), App.Version);
                Check("the self-test never starts the first-run guide by itself", !SettingsStore.Fresh && guide == null);

                // ------------------------------------------------ 1. Lodge server notice
                Session.Disconnect();
                s.LodgeUpdateDismissed = null;
                TabLodge.IsChecked = true;
                LodgePage.ShowChatForTest();
                Session.FeedForTest(Welcome("owner", "2.5.0", true));
                await Task.Delay(300);
                Check("notice: owner on server 2.5 sees it", LodgePage.ServerNoticeVisibleForTest, LodgePage.ServerNoticeTextForTest);
                Check("notice: wording", LodgePage.ServerNoticeTextForTest == "Your Lodge server is on 2.5 - " + LodgeSession.ShortVersion(Brand.LatestLodgeServer) + " is available. On the server run: sudo lodge-update", LodgePage.ServerNoticeTextForTest);
                Snapshot(P("20-server-notice-owner.png"));
                string copied = null;
                try { LodgePage.ServerNoticeCopyForTest(); copied = Clipboard.GetText(); } catch (Exception e) { copied = "clipboard: " + e.Message; }
                Check("notice: Copy puts the command on the clipboard", copied == "sudo lodge-update", copied);
                Session.FeedForTest(Welcome("member", "2.5.0", false));
                await Task.Delay(200);
                Check("notice: a normal member never sees it", !LodgePage.ServerNoticeVisibleForTest);
                Session.FeedForTest(Welcome("officer", "2.5.0", false));
                await Task.Delay(200);
                Check("notice: an officer sees it", LodgePage.ServerNoticeVisibleForTest);
                Session.FeedForTest(Welcome("owner", Brand.LatestLodgeServer, true));
                await Task.Delay(200);
                Check("notice: owner on the current server sees nothing", !LodgePage.ServerNoticeVisibleForTest);
                Session.FeedForTest(Welcome("owner", "2.2.0", true));
                await Task.Delay(200);
                Check("notice: owner on a much older server sees it", LodgePage.ServerNoticeVisibleForTest);
                Check("notice: version helpers (2.7 / 2.7.1 / 2.10 vs 2.9)", LodgeSession.ShortVersion("2.7.0") == "2.7" && LodgeSession.ShortVersion("2.7.1") == "2.7.1"
                    && LodgeSession.ParseVersion("2.10.0") > LodgeSession.ParseVersion("2.9.0") && LodgeSession.ParseVersion("junk") == null);
                LodgePage.ServerNoticeCloseForTest();
                await Task.Delay(200);
                Check("notice: dismiss hides it and remembers (for this server version)", !LodgePage.ServerNoticeVisibleForTest && s.LodgeUpdateDismissed == Brand.LatestLodgeServer);
                Session.FeedForTest(Welcome("owner", "2.5.0", true));
                await Task.Delay(200);
                Check("notice: stays dismissed after a reconnect", !LodgePage.ServerNoticeVisibleForTest);

                // ------------------------------------------------ 2. first-run guide
                var fake = new List<GuideAddon>
                {
                    new GuideAddon { Id = "elanshub", Name = "Elan's Hub", Description = "The small companion: your character, class and level for the Lodge and the Outpost. Needed for the other addons.", Required = true, Checked = true },
                    new GuideAddon { Id = "hunter", Name = "Elan's Hunter Helper", Description = "Pet, ammo, trap and aspect helpers for hunters, with a built-in pet and talent guide." },
                    new GuideAddon { Id = "paladin", Name = "Elan's Paladin Helper", Description = "Auras, blessings and seals at a glance for paladins." },
                    new GuideAddon { Id = "bags", Name = "Elan's Bags", Description = "One bag window for all bags, bank and guild bank - also readable from the Outpost's Inventory page.", Installed = true },
                };
                s.WelcomeDone = false;
                GuideAddonsOverride = fake; GuideInstalledForTest = new List<string>(); GuideJoinedForTest = new List<string>();
                ShowTab("addons");
                ShowWelcomeGuide();
                await Task.Delay(300);
                var g = guide;
                Check("guide: opens on step 1 over the window", g != null && g.Step == 0 && GuideHost.Visibility == Visibility.Visible);
                Snapshot(P("21-guide-1-folder.png"));
                g.Next();
                await Task.Delay(250);
                Check("guide: step 2 lists the addons; the companion is pre-ticked as required", g.Step == 1 && g.TickedIds.SequenceEqual(new[] { "elanshub" }));
                Snapshot(P("21-guide-2-addons.png"));
                fake[1].Checked = true;   // tick Hunter Helper (what a click on its switch does)
                g.Next();
                await Task.Delay(250);
                Check("guide: step 3 (Lodge) - 'Skip this step' while the link is empty", g.Step == 2);
                g.LinkText = "not a link";
                g.Next();
                await Task.Delay(250);
                Check("guide: a bad link is refused with a hint and stays on step 3", g.Step == 2 && g.ErrorText.Length > 0);
                Snapshot(P("21-guide-3-lodge-error.png"));
                g.LinkText = "https://example.com/lodge#invite=ABCDEF123456";
                g.Next();
                await Task.Delay(250);
                Check("guide: a good link goes on to step 4", g.Step == 3 && g.ErrorText == "");
                Snapshot(P("21-guide-4-done.png"));
                g.Back(); g.Next();
                g.Next();   // Get started
                await Task.Delay(200);
                Check("guide: finishing installs the ticked addons (companion + Hunter Helper, not the one already installed)", GuideInstalledForTest.SequenceEqual(new[] { "elanshub", "hunter" }), string.Join(",", GuideInstalledForTest));
                Check("guide: ...joins with the pasted link", GuideJoinedForTest.Count == 1 && GuideJoinedForTest[0].EndsWith("#invite=ABCDEF123456"));
                Check("guide: closes and is remembered as done", GuideHost.Visibility == Visibility.Collapsed && guide == null && s.WelcomeDone);

                s.WelcomeDone = false;
                GuideInstalledForTest.Clear(); GuideJoinedForTest.Clear();
                SettingsPage.Show("about");
                SettingsPage.WelcomeClickForTest();
                await Task.Delay(200);
                Check("guide: re-opened from Settings > About", guide != null && guide.Step == 0 && GuideHost.Visibility == Visibility.Visible);
                guide.Next(); guide.Skip();
                Check("guide: skip closes it without installing or joining anything", guide == null && GuideInstalledForTest.Count == 0 && GuideJoinedForTest.Count == 0 && s.WelcomeDone);
                // no WoW folder found: step 1 says so
                var root0 = s.WowRoot;
                s.WowRoot = Path.Combine(dir, "nowhere");
                ShowWelcomeGuide();
                await Task.Delay(250);
                Snapshot(P("21-guide-1-nofolder.png"));
                Check("guide: step 1 offers to choose the folder when none is found", guide != null && guide.Step == 0);
                guide.Skip();
                s.WowRoot = root0;
                GuideAddonsOverride = null; GuideInstalledForTest = null; GuideJoinedForTest = null;

                // ------------------------------------------------ 3. privacy page
                Session.FeedForTest(Welcome("owner", "2.7.0", true));
                ShowTab("settings");
                SettingsPage.Show("privacy");
                await Task.Delay(400);
                Check("privacy: the page shows", SettingsPage.PrivacyShowsForTest);
                Snapshot(P("22-privacy-1.png"));
                SettingsPage.ScrollEndForTest();
                await Task.Delay(300);
                Snapshot(P("22-privacy-2.png"));
                SettingsPage.ToggleForTest("PvShareZone");
                Check("privacy: 'My zone' switch is the same setting as in Settings > Lodge", s.ShareZoneOff);
                SettingsPage.Show("lodge"); SettingsPage.Show("privacy");
                SettingsPage.ToggleForTest("PvShareZone");
                Check("privacy: ...and it switches back", !s.ShareZoneOff);
                SettingsPage.ToggleForTest("PvCfAuto");
                Check("privacy: CurseForge refresh switch writes cfAutoCheck", s.CfAutoCheck != cfAuto0);
                SettingsPage.ToggleForTest("PvCfAuto");
                SettingsPage.ToggleForTest("PvInvisible");
                Check("privacy: Appear offline switch hides me (same state as the Lodge tab)", Session.Invisible && s.LodgeInvisible);
                SettingsPage.ToggleForTest("PvInvisible");
                Check("privacy: ...and shows me again", !Session.Invisible);
                SettingsPage.ToggleForTest("PvRememberInvis");
                Check("privacy: 'Remember Appear offline' writes invisibleForget", s.InvisibleForget != forget0);
                SettingsPage.ToggleForTest("PvRememberInvis");

                // ------------------------------------------------ 5. CurseForge status (before the idle test: that one takes a while)
                var flavor = ClientFlavor();
                var ao = WowLocator.AddOnsDir(s.WowRoot, flavor);
                var tmp = Path.Combine(dir, "cfstatus"); Directory.CreateDirectory(tmp);
                var missing = Path.Combine(tmp, "none.json");
                var st = CurseForgeLocal.Probe(ao, missing, false);
                Check("cf status: not installed", st.Kind == CurseForgeLocal.CfStatusKind.NotInstalled && st.Text == "CurseForge not installed", st.Text);
                st = CurseForgeLocal.Probe(ao, missing, true);
                Check("cf status: installed but no data file yet", st.Kind == CurseForgeLocal.CfStatusKind.Unreadable && st.Text.StartsWith("Couldn't read CurseForge data - "), st.Text);
                var bad = Path.Combine(tmp, "bad.json"); File.WriteAllText(bad, "{ this is not json");
                var bad2 = Path.Combine(tmp, "bad2.json"); File.WriteAllText(bad2, "{\"a\":1}");
                var st1 = CurseForgeLocal.Probe(ao, bad, true); var st2 = CurseForgeLocal.Probe(ao, bad2, true);
                Check("cf status: garbage file -> 'Couldn't read CurseForge data - <reason>'", st1.Kind == CurseForgeLocal.CfStatusKind.Unreadable && st1.Text.StartsWith("Couldn't read CurseForge data - ") && st2.Kind == CurseForgeLocal.CfStatusKind.Unreadable, st1.Text + " | " + st2.Text);
                var other = Path.Combine(tmp, "other.json"); File.WriteAllText(other, "[{\"name\":\"Other\",\"installPath\":\"C:/Nope/\",\"installedAddons\":[]}]");
                st = CurseForgeLocal.Probe(ao, other, true);
                Check("cf status: valid file without our WoW folder", st.Kind == CurseForgeLocal.CfStatusKind.NoInstance, st.Text);
                st = CurseForgeLocal.Probe(ao);
                Check("cf status: OK with name, addon count and last check (fixture)", st.Kind == CurseForgeLocal.CfStatusKind.Ok && st.Text.StartsWith("CurseForge integration: OK (found Forever, ") && st.Text.Contains(" addon") && st.Text.Contains("checked "), st.Text);
                Check("cf status: no WoW folder", CurseForgeLocal.Probe(null).Kind == CurseForgeLocal.CfStatusKind.NoWowFolder);
                Check("cf status: the tooltip says local files only", CurseForgeLocal.StatusTip.Contains("Local files only") && CurseForgeLocal.StatusTip.Contains("ever written"));
                ShowTab("settings");
                SettingsPage.Show("addons");
                await Task.Delay(300);
                SettingsPage.ScrollEndForTest(); await Task.Delay(200);
                Check("cf status: row shows the live status", SettingsPage.CfStatusTextForTest.StartsWith("CurseForge integration: OK"), SettingsPage.CfStatusTextForTest);
                Snapshot(P("23-cf-status-ok.png"));
                await SettingsPage.CfRecheckForTest();
                Check("cf status: Re-check re-reads and keeps the status", SettingsPage.CfStatusTextForTest.StartsWith("CurseForge integration: OK"), SettingsPage.CfStatusTextForTest);
                foreach (var (name, status) in new[] { ("notinstalled", CurseForgeLocal.Probe(ao, missing, false)), ("unreadable", st1), ("noinstance", CurseForgeLocal.Probe(ao, other, true)) })
                {
                    SettingsPage.CfOverrideForTest = status;
                    SettingsPage.Show("lodge"); SettingsPage.Show("addons");
                    SettingsPage.ScrollEndForTest(); await Task.Delay(250);
                    Snapshot(P("23-cf-status-" + name + ".png"));
                }
                SettingsPage.CfOverrideForTest = null;

                // ------------------------------------------------ 4. idle: pause / resume with a fake process detector
                ShowTab("addons");
                bool wow = false;
                WowWatch.Shared.Detector = () => wow;
                WowWatch.Shared.Poll();
                var pres = Session.Presence;
                Check("idle: WoW closed -> pixel-strip capture and the presence poll are paused", !WowWatch.Shared.Running && !pres.Strip.Capturing && !pres.Active);
                Check("idle: ...and the strip says why", pres.Strip.Status == "paused", pres.Strip.Status);
                wow = true;
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 5000 && !WowWatch.Shared.Running) await Task.Delay(50);
                var resumeMs = sw.ElapsedMilliseconds;
                await Task.Delay(100);
                Check("idle: WoW starts -> capture and presence resume within ~2 s (" + resumeMs + " ms)", WowWatch.Shared.Running && pres.Strip.Capturing && pres.Active && resumeMs <= 2600, "resume " + resumeMs + " ms");
                wow = false;
                sw.Restart();
                while (sw.ElapsedMilliseconds < 5000 && WowWatch.Shared.Running) await Task.Delay(50);
                var pauseMs = sw.ElapsedMilliseconds;
                await Task.Delay(100);
                Check("idle: WoW exits -> paused again (" + pauseMs + " ms)", !WowWatch.Shared.Running && !pres.Strip.Capturing && !pres.Active, "pause " + pauseMs + " ms");

                // CPU of the idle hub: "old" = the detector claims WoW runs (everything polls like before 2.27), "new" = paused
                int secs = int.TryParse(Environment.GetEnvironmentVariable("ELANSHUB_IDLE_SECS"), out var sv) && sv >= 2 ? sv : 6;
                Session.Disconnect();
                async Task<(double cpuMs, int polls)> Measure(bool running)
                {
                    wow = running; WowWatch.Shared.Poll();
                    await Task.Delay(1500);   // settle: timers (re)start
                    var proc = Process.GetCurrentProcess();
                    var before = proc.TotalProcessorTime; var polls0 = WowWatch.Shared.Polls;
                    await Task.Delay(secs * 1000);
                    proc.Refresh();
                    return ((proc.TotalProcessorTime - before).TotalMilliseconds, WowWatch.Shared.Polls - polls0);
                }
                await Measure(true);   // warm-up (JIT, first timer ticks), not counted
                var runCpu = (cpuMs: 0.0, polls: 0); var idleCpu = (cpuMs: 0.0, polls: 0);
                for (int round = 0; round < 2; round++)   // alternate, so a busy moment on the PC hits both the same
                {
                    var r = await Measure(true); runCpu = (runCpu.cpuMs + r.cpuMs, runCpu.polls + r.polls);
                    var i = await Measure(false); idleCpu = (idleCpu.cpuMs + i.cpuMs, idleCpu.polls + i.polls);
                }
                secs *= 2;
                wow = false; WowWatch.Shared.Poll();
                notes.Add($"idle CPU over {secs} s (hub window shown, nothing happening): WoW-running timers {runCpu.cpuMs:0} ms ({runCpu.cpuMs / secs / 10:0.00} % of a core), paused {idleCpu.cpuMs:0} ms ({idleCpu.cpuMs / secs / 10:0.00} % of a core); watcher polls {idleCpu.polls} in {secs} s");
                Check("idle: paused costs no more CPU than running (" + idleCpu.cpuMs.ToString("0") + " vs " + runCpu.cpuMs.ToString("0") + " ms)", idleCpu.cpuMs <= runCpu.cpuMs * 1.25 + 80);
            }
            catch (Exception e) { ok = false; notes.Add("FAIL exception: " + e); }
            finally
            {
                WowWatch.Shared.Detector = detector0; WowWatch.Shared.Poll();
                s.LodgeUpdateDismissed = dismissed0; s.WelcomeDone = welcome0; s.CfAutoCheck = cfAuto0; s.ShareZoneOff = shareZoneOff0; s.InvisibleForget = forget0; s.LodgeInvisible = inv0;
                SettingsStore.Save(s);
                if (guide != null) CloseGuide(false);
                GuideAddonsOverride = null; GuideInstalledForTest = null; GuideJoinedForTest = null;
                SettingsPage.CfOverrideForTest = null;
                Session.Disconnect();
                ShowTab("addons");
            }
            notes.Insert(0, "round 3 (2.27.0): " + (ok ? "all ok" : "FAILURES") + $"  ({clock.Elapsed.TotalSeconds:0} s)");
            return string.Join("\r\n", notes);
        }
    }
}
