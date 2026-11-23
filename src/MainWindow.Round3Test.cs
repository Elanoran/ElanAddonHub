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
    // 2.27.0 self-test: Lodge server notice, first-run guide, Privacy page, idle pause/resume (the CPU measurement is the "idlecpu" section), CurseForge status. Screenshots 20-* .. 24-*
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
            void Check(string what, bool cond, string detail = null) { SelfTestClock.Mark(what); notes.Add((cond ? "ok   " : "FAIL ") + what + (!cond && detail != null ? " [" + detail + "]" : "")); ok &= cond; }
            string P(string n) => Path.Combine(dir, n);
            var s = settings;
            var dismissed0 = s.LodgeUpdateDismissed; bool welcome0 = s.WelcomeDone, cfAuto0 = s.CfAutoCheck, shareZoneOff0 = s.ShareZoneOff, forget0 = s.InvisibleForget, inv0 = s.LodgeInvisible;
            var detector0 = WowWatch.Shared.Detector;
            var opt0 = (s.OptInsAnswered, s.AutoUpdate, s.ShareLivePosWithLodge, s.LivePosScope);
            var pv0 = (s.AtlasShareOff, s.FlightShareOff, s.ArmoryShareOn, s.ArmoryShareReps, s.ArmoryChars, s.BlizzardNewsLoad, s.WowEventsOff, s.LodgeManualConnect);
            var asked0 = (s.SharingInviteAnswered, s.CairnJoinCardDismissed, s.ClassicShareOffered, s.InvisibleHintShown, s.DisplayNameHintShown, s.AtlasSourcesCardDismissed, s.LodgeUpgradeDeclined);
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
                // 3.5.10: a Lodge in its Docker image says so in the welcome; the notice and Copy give the Docker command instead
                Session.Disconnect();
                Session.FeedForTest(Welcome("owner", "2.5.0", true).Replace("\"server\":\"2.5.0\",", "\"server\":\"2.5.0\",\"install\":\"docker\","));
                await Task.Delay(300);
                Check("notice (Docker Lodge): names the Docker update command", LodgePage.ServerNoticeVisibleForTest && LodgePage.ServerNoticeTextForTest.EndsWith("On the server run: docker compose pull && docker compose up -d"), LodgePage.ServerNoticeTextForTest);
                try { LodgePage.ServerNoticeCopyForTest(); copied = Clipboard.GetText(); } catch (Exception e) { copied = "clipboard: " + e.Message; }
                Check("notice (Docker Lodge): Copy puts the Docker command on the clipboard", copied == "docker compose pull && docker compose up -d", copied);
                Session.Disconnect();
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
                    new GuideAddon { Id = "hunter", Name = "Elan's Hunter Helper", Description = "Pet, ammo, trap and aspect helpers for hunters, with a built-in pet and talent guide.", Reason = "For hunters" },
                    new GuideAddon { Id = "paladin", Name = "Elan's Paladin Helper", Description = "Auras, blessings and seals at a glance for paladins.", Reason = "For paladins" },
                    new GuideAddon { Id = "bags", Name = "Elan's Bags", Description = "One bag window for all bags, bank and guild bank - also readable from the Outpost's Characters page.", Installed = true },
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
                Check("guide: a good link goes on to step 4 (sharing choices, 3.11)", g.Step == WelcomeGuide.SharingStep && g.ErrorText == "" && g.PresetButtonsForTest?.Count == 3);
                Snapshot(P("21-guide-4-sharing.png"));
                g.Next();
                await Task.Delay(250);
                Check("guide: step 5 is the tips", g.Step == 4);
                Snapshot(P("21-guide-5-done.png"));
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
                await ShotWait(250);
                Snapshot(P("21-guide-1-nofolder.png"));
                Check("guide: step 1 offers to choose the folder when none is found", guide != null && guide.Step == 0);
                guide.Skip();
                s.WowRoot = root0;
                GuideAddonsOverride = null; GuideInstalledForTest = null; GuideJoinedForTest = null;

                // ------------------------------------------------ 2b. 3.11: the addon step's starting ticks and reasons (AddonPicks)
                {
                    AddonInfo A(string id, bool req = false, params string[] cls) => new AddonInfo { Id = id, Name = id, Required = req, Classes = cls.Length > 0 ? cls.ToList() : null };
                    var infos = new[] { A("ElansHub", true), A("ElansToolkit", true), A("ElansHunterHelper"), A("ElansPaladinHelper", false, "PALADIN"), A("ElansPriestHelper", false, "PRIEST"), A("ElansBags"), A("ElansProfessionHelper"), A("SomethingElse") };
                    var hunter = GuideAddonsFrom(infos.Select(i => (i, i.Id == "ElansBags")), AddonPicks.Played(new[] { "Hunter" }));
                    GuideAddon G(List<GuideAddon> l, string id) => l.First(x => x.Id == id);
                    Check("addon picks: a hunter's PC starts with the required ones, the Hunter Helper, Bags and Profession Helper ticked",
                        string.Join(",", hunter.Where(x => x.Checked).Select(x => x.Id)) == "ElansHub,ElansToolkit,ElansHunterHelper,ElansBags,ElansProfessionHelper", string.Join(",", hunter.Where(x => x.Checked).Select(x => x.Id)));
                    Check("addon picks: each tick says why, the other class helpers say who they are for",
                        G(hunter, "ElansHunterHelper").Reason == "Recommended: you play a Hunter" && G(hunter, "ElansPaladinHelper").Reason == "For paladins" && !G(hunter, "ElansPaladinHelper").Checked
                        && G(hunter, "ElansProfessionHelper").Reason == "Recommended for everyone" && G(hunter, "SomethingElse").Reason == null && !G(hunter, "SomethingElse").Checked && G(hunter, "ElansHub").Reason == null, G(hunter, "ElansHunterHelper").Reason);
                    var fresh = GuideAddonsFrom(infos.Select(i => (i, false)), AddonPicks.Played(null));
                    Check("addon picks: with no character known (a fresh install) no class helper is ticked", !G(fresh, "ElansHunterHelper").Checked && G(fresh, "ElansHunterHelper").Reason == "For hunters" && G(fresh, "ElansBags").Checked, null);
                    Check("addon picks: class names from the Hub or the game become class files", AddonPicks.Played(new[] { "Hunter", "PRIEST", null, "" }).SetEquals(new[] { "HUNTER", "PRIEST" }), null);
                }

                // ------------------------------------------------ 3. privacy page
                Session.FeedForTest(Welcome("owner", "2.7.0", true));
                ShowTab("settings");
                SettingsPage.Show("privacy");
                await Task.Delay(400);
                Check("privacy: the page shows", SettingsPage.PrivacyShowsForTest);
                Snapshot(P("22-privacy-1.png"));
                SettingsPage.ScrollEndForTest();
                await ShotWait(300);
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

                // ------------------------------------------------ 3b. 3.11: every sharing switch on Privacy (same settings as Lodge / General), Ask me again
                SetAtlasSharing(true); s.FlightShareOff = false; s.ArmoryShareOn = false; s.ArmoryShareReps = false; s.ArmoryChars = null; s.BlizzardNewsLoad = false; s.WowEventsOff = false;
                SettingsPage.Show("privacy");
                Check("privacy 3.11: Atlas, flight, armory and news switches show the settings", SettingsPage.BoxCheckedForTest("PvAtlasShare") && SettingsPage.BoxCheckedForTest("PvFlightShare")
                    && !SettingsPage.BoxCheckedForTest("PvArmory") && !SettingsPage.BoxCheckedForTest("PvBlizzNews"));
                SettingsPage.ToggleForTest("PvAtlasShare");
                Check("privacy 3.11: Atlas sharing off on Privacy = off on the Lodge page too", s.AtlasShareOff && !SettingsPage.BoxCheckedForTest("AtlasShareBox"));
                SetAtlasSharing(true);   // as the welcome guide or the Atlas card does
                SettingsPage.Show("lodge");
                Check("privacy 3.11: the Lodge page shows a change made elsewhere", SettingsPage.BoxCheckedForTest("AtlasShareBox"));
                SettingsPage.LodgeClickForTest("FlightShareBox", false);
                Check("privacy 3.11: a click on another Lodge switch no longer writes Atlas sharing back", !s.AtlasShareOff && s.FlightShareOff, "atlasOff=" + s.AtlasShareOff + " flightOff=" + s.FlightShareOff);
                s.WowEventsOff = true;   // as the Calendar's own switch does
                SettingsPage.LodgeClickForTest("AutoConnectBox", !s.LodgeManualConnect);
                Check("privacy 3.11: ...nor the Calendar's in-game events switch", s.WowEventsOff);
                s.WowEventsOff = false;
                SettingsPage.Show("privacy");
                Check("privacy 3.11: flight sharing set on the Lodge page shows on Privacy", !SettingsPage.BoxCheckedForTest("PvFlightShare"));
                SettingsPage.ToggleForTest("PvFlightShare");
                Check("privacy 3.11: ...and switches back on from Privacy", !s.FlightShareOff && SettingsPage.BoxCheckedForTest("FlightShareBox"));
                SettingsPage.ToggleForTest("PvArmory");
                Check("privacy 3.11: Share my characters on = the Lodge page's switch", s.ArmoryShareOn && SettingsPage.ArmoryShareForTest);
                Check("privacy 3.11: with nobody chosen it says so", SettingsPage.PrivacyRowTagForTest("PvArmoryRow").Contains("no character is chosen"), SettingsPage.PrivacyRowTagForTest("PvArmoryRow"));
                s.ArmoryChars = new List<string> { "Test Realm-Elan" };
                SettingsPage.Show("privacy");
                Check("privacy 3.11: ...and names the chosen ones", SettingsPage.PrivacyRowTagForTest("PvArmoryRow").StartsWith("Shared: Elan.", StringComparison.Ordinal), SettingsPage.PrivacyRowTagForTest("PvArmoryRow"));
                SettingsPage.ToggleForTest("PvArmory");
                Check("privacy 3.11: ...and off again", !s.ArmoryShareOn && !SettingsPage.ArmoryShareForTest);
                SettingsPage.ToggleForTest("PvBlizzNews");
                Check("privacy 3.11: Blizzard news can be switched on", s.BlizzardNewsLoad);
                SettingsPage.ToggleForTest("PvBlizzNews");
                Check("privacy 3.11: ...and off again (it could not be before)", !s.BlizzardNewsLoad);
                s.SharingInviteAnswered = "atlas"; s.CairnJoinCardDismissed = true; s.ClassicShareOffered = "1.0"; s.InvisibleHintShown = true; s.DisplayNameHintShown = true; s.AtlasSourcesCardDismissed = true;
                SettingsPage.AskAgainForTest();
                Check("privacy 3.11: Ask me again brings back the one-time questions and tips, switches untouched",
                    s.SharingInviteAnswered == null && !s.CairnJoinCardDismissed && s.ClassicShareOffered == null && !s.InvisibleHintShown && !s.DisplayNameHintShown && !s.AtlasSourcesCardDismissed && !s.AtlasShareOff);
                SettingsPage.ScrollEndForTest();
                await ShotWait(300);
                Snapshot(P("22-privacy-3.png"));

                // ------------------------------------------------ 3c. 3.11: "New things you can turn on" (OptIns)
                {
                    var o = new Settings();
                    Check("opt-ins: an older install that never saw them gets all three", OptIns.Due(o, null).Count == 3);
                    OptIns.MarkGuideAnswered(o);
                    Check("opt-ins: after the welcome guide only the one it did not ask about is left", string.Join(",", OptIns.Due(o, null).Select(x => x.Id)) == "autoupdate");
                    o.AutoUpdate = true;
                    Check("opt-ins: nothing that is on already", OptIns.Due(o, null).Count == 0);
                    var o2 = new Settings();
                    Check("opt-ins: the Lodge ones wait while they are not possible", string.Join(",", OptIns.Due(o2, x => !x.NeedsLodge).Select(x => x.Id)) == "autoupdate");

                    s.OptInsAnswered = null; s.AutoUpdate = false; s.ArmoryShareOn = false; s.ArmoryChars = null; s.ShareLivePosWithLodge = false;
                    ShowTab("addons");
                    ShowOptInsForTest();
                    await ShotWait(300);
                    Snapshot(P("22-optins.png"));
                    Check("opt-ins: the card shows what is possible here, every switch off (this Lodge has no Armory or map position)",
                        OptInsShowingForTest && OptInSwitchesForTest.Keys.SequenceEqual(new[] { "autoupdate" }) && OptInSwitchesForTest.Values.All(x => x.IsChecked != true), string.Join(",", OptInSwitchesForTest?.Keys ?? Enumerable.Empty<string>()));
                    OptInsDoneForTest();
                    Check("opt-ins: Done closes it, remembers what was shown and changes nothing", !OptInsShowingForTest && GuideHost.Visibility == Visibility.Collapsed
                        && s.OptInsAnswered.SequenceEqual(new[] { "autoupdate" }) && !s.AutoUpdate, string.Join(",", s.OptInsAnswered ?? new List<string>()));
                    ShowOptInsForTest();
                    Check("opt-ins: not again", !OptInsShowingForTest);
                    GuideMainKeyOverride = "Test Realm-Elan";
                    SetOptIn("armory", true);
                    Check("opt-ins: turning on the armory shares the highest character, like the guide", s.ArmoryShareOn && s.ArmoryChars.SequenceEqual(new[] { "Test Realm-Elan" }));
                    SetOptIn("armory", false); SetOptIn("livepos", true);
                    Check("opt-ins: ...off again; map position goes to officers only", !s.ArmoryShareOn && s.ShareLivePosWithLodge && s.LivePosScope == "officers");
                    SetOptIn("livepos", false);
                    GuideMainKeyOverride = null;
                }

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

                // the CPU of the idle hub is measured in its own section: "idlecpu" (MainWindow.IdleCpuTest.cs)
            }            catch (Exception e) { ok = false; notes.Add("FAIL exception: " + e); }
            finally
            {
                WowWatch.Shared.Detector = detector0; WowWatch.Shared.Poll();
                s.LodgeUpdateDismissed = dismissed0; s.WelcomeDone = welcome0; s.CfAutoCheck = cfAuto0; s.ShareZoneOff = shareZoneOff0; s.InvisibleForget = forget0; s.LodgeInvisible = inv0;
                (s.AtlasShareOff, s.FlightShareOff, s.ArmoryShareOn, s.ArmoryShareReps, s.ArmoryChars, s.BlizzardNewsLoad, s.WowEventsOff, s.LodgeManualConnect) = pv0;
                (s.OptInsAnswered, s.AutoUpdate, s.ShareLivePosWithLodge, s.LivePosScope) = opt0;
                (s.SharingInviteAnswered, s.CairnJoinCardDismissed, s.ClassicShareOffered, s.InvisibleHintShown, s.DisplayNameHintShown, s.AtlasSourcesCardDismissed, s.LodgeUpgradeDeclined) = asked0;
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
