using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // --selftest "realms": the realm status dots at the top (Lodge server feature "realms"). Synthetic frames in, the dots and tooltips out: colours and labels, the details in the tooltip
    // (ruleset, population, queue, "checked N min ago", "via your Lodge"), hidden without a Lodge / without the feature / when the Forever realms were not found, the Settings switch
    // (on by default), the "tell me when they are back" note (off by default, a quiet toast, never a dialog), and that nothing is ever sent to the server for any of it.
    // The real server (with a fake Blizzard) is tested by server/Lodge.Tests/RealmsTests.cs.
    public partial class MainWindow
    {
        static string RealmJ(int id, string name, string ruleset, string status, string pop = null, bool? queue = null) =>
            "{\"id\":" + id + ",\"name\":" + Q(name) + ",\"slug\":" + Q(name.ToLowerInvariant().Replace(' ', '-')) + ",\"ruleset\":" + Q(ruleset) + ",\"status\":" + Q(status)
            + (pop != null ? ",\"population\":" + Q(pop) : "") + (queue != null ? ",\"hasQueue\":" + (queue.Value ? "true" : "false") : "") + ",\"namespace\":\"dynamic-classic1x-eu\"}";

        static string RealmsFrame(long checkedAt, params string[] realms) =>
            "{\"t\":\"realms.status\",\"available\":true,\"region\":\"eu\",\"checkedAt\":" + checkedAt + ",\"realms\":[" + string.Join(",", realms) + "]}";

        string RealmsWelcome(bool feature) =>
            FunWelcome("member", FunPeople(false), "", "\"reply\",\"react\",\"react2\",\"pin\",\"profile\",\"displayname\",\"lfg\",\"history2\",\"search\"" + (feature ? ",\"realms\"" : ""), "2.20.0");

        async Task<string> RealmsTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            void Check(string what, bool cond, string detail = null) { notes.Add((cond ? "ok   " : "FAIL ") + what + (!cond && detail != null ? " [" + detail + "]" : "")); ok &= cond; }
            var sent = new List<Dictionary<string, object>>();
            var hadOff = settings.RealmStatusOff; var hadBack = settings.RealmsBackNotice; var hadToastsOff = settings.ToastsOff;
            var hadClock = Session.Clock;
            var now = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
            long unix(DateTime t) => (long)(t - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            try
            {
                Session.Disconnect();
                settings.RealmStatusOff = false; settings.RealmsBackNotice = false; settings.ToastsOff = false;
                Session.Clock = () => now;
                TabLodge.IsChecked = true;
                Session.SentForTest = o => sent.Add(o);
                var four = new[] { RealmJ(1, "WoW Forever", "Normal", "UP", "Full", false), RealmJ(2, "Forever PvP", "PvP", "UP", "Medium", true), RealmJ(3, "Forever RP", "RP", "DOWN"), RealmJ(4, "Forever Hardcore", "Hardcore", "UNKNOWN") };

                // ---- defaults
                Check("defaults: the strip is on and the back-up note is off", !new Settings().RealmStatusOff && !new Settings().RealmsBackNotice);

                // ---- a server without the feature
                Session.FeedForTest(RealmsWelcome(false));
                Session.FeedForTest(RealmsFrame(unix(now), four));
                Check("old server: nothing supported, nothing shown even if a frame arrives", !Session.SupportsRealms && !RealmStripVisibleForTest && RealmChipsForTest.Count == 0);

                // ---- the feature, but no answer yet
                Session.Disconnect();
                Session.FeedForTest(RealmsWelcome(true));
                Check("feature: supported, hidden until the Lodge says something", Session.SupportsRealms && !RealmStripVisibleForTest);

                // ---- the dots
                Session.FeedForTest(RealmsFrame(unix(now), four));
                var chips = RealmChipsForTest;
                Check("dots: the strip shows with one chip per realm", RealmStripVisibleForTest && chips.Count == 4);
                Check("dots: labelled by ruleset", chips.Select(c => c.Label).SequenceEqual(new[] { "Normal", "PvP", "RP", "Hardcore" }), string.Join(",", chips.Select(c => c.Label)));
                Check("dots: green up, red down, grey unknown", chips.Select(c => c.Status).SequenceEqual(new[] { "UP", "UP", "DOWN", "UNKNOWN" }), string.Join(",", chips.Select(c => c.Status)));
                Check("tooltip: name, ruleset and status", chips[0].Tip.StartsWith("WoW Forever\nNormal · Up\n"), chips[0].Tip);
                Check("tooltip: population, and the queue when there is one", chips[0].Tip.Contains("Population: Full") && !chips[0].Tip.Contains("Queue") && chips[1].Tip.Contains("Population: Medium · Queue"), chips[1].Tip);
                Check("tooltip: down and unknown say so, with no population line", chips[2].Tip.Contains("RP · Down") && !chips[2].Tip.Contains("Population") && chips[3].Tip.Contains("Hardcore · Unknown"));
                Check("tooltip: checked just now, via your Lodge", chips[0].Tip.EndsWith("Checked just now · via your Lodge"), chips[0].Tip);
                now = now.AddMinutes(1).AddSeconds(30);
                Check("tooltip: a minute and a half later it says 2 min ago", RealmChipsForTest[0].Tip.Contains("Checked 2 min ago"), RealmChipsForTest[0].Tip);
                now = now.AddHours(3);
                Check("tooltip: hours later it says so", RealmChipsForTest[0].Tip.Contains("Checked 3 h ago"), RealmChipsForTest[0].Tip);
                now = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
                await Task.Delay(200);
                Snapshot(Path.Combine(dir, "realms-topbar.png"));

                // ---- a change
                Session.FeedForTest(RealmsFrame(unix(now), RealmJ(1, "WoW Forever", "Normal", "DOWN", "Full", false), RealmJ(2, "Forever PvP", "PvP", "UP"), RealmJ(3, "Forever RP", "RP", "UP"), RealmJ(4, "Forever Hardcore", "Hardcore", "UP")));
                chips = RealmChipsForTest;
                Check("change: the first dot turned red and the others green", chips.Select(c => c.Status).SequenceEqual(new[] { "DOWN", "UP", "UP", "UP" }), string.Join(",", chips.Select(c => c.Status)));
                await Task.Delay(200);
                Snapshot(Path.Combine(dir, "realms-topbar-down.png"));

                // ---- the same ruleset twice: the names tell them apart
                Session.FeedForTest(RealmsFrame(unix(now), RealmJ(1, "Forever One", "Normal", "UP"), RealmJ(2, "Forever Two", "Normal", "UP"), RealmJ(3, "Forever PvP", "PvP", "UP")));
                Check("labels: two realms of one ruleset use their names", RealmChipsForTest.Select(c => c.Label).SequenceEqual(new[] { "Forever One", "Forever Two", "PvP" }), string.Join(",", RealmChipsForTest.Select(c => c.Label)));

                // ---- the Forever realms were not found: calm, nothing shown
                Session.FeedForTest("{\"t\":\"realms.status\",\"available\":false,\"region\":\"eu\",\"realms\":[],\"checkedAt\":" + unix(now) + ",\"why\":\"Forever realms not found in the Blizzard API\"}");
                Check("not found: hidden, the reason is kept for whoever asks", !RealmStripVisibleForTest && Session.RealmsWhy.Contains("not found"));
                Session.FeedForTest(RealmsFrame(unix(now), four));
                Check("not found: the dots return when the realms are found", RealmStripVisibleForTest && RealmChipsForTest.Count == 4);

                // ---- the Settings switch
                Check("settings: both switches mirror the defaults", SettingsPage.RealmStatusBoxForTest && !SettingsPage.RealmsBackBoxForTest);
                SettingsPage.SetRealmStatusForTest(false, false);
                Check("settings: switched off, the strip is gone at once", settings.RealmStatusOff && !RealmStripVisibleForTest);
                Session.FeedForTest(RealmsFrame(unix(now), four));
                Check("settings: and stays gone when new data arrives", !RealmStripVisibleForTest);
                SettingsPage.SetRealmStatusForTest(true, false);
                Check("settings: switched on, the dots are back", !settings.RealmStatusOff && RealmStripVisibleForTest && RealmChipsForTest.Count == 4);

                // ---- "tell me when they are back"
                int shown = Session.Toasts.Shown;
                Session.FeedForTest("{\"t\":\"realms.back\",\"realms\":[\"Forever RP\"],\"checkedAt\":" + unix(now) + "}");
                Check("back: off by default - no toast", Session.Toasts.Shown == shown && !settings.RealmsBackNotice);
                SettingsPage.SetRealmStatusForTest(true, true);
                Check("back: the setting is saved", settings.RealmsBackNotice);
                Session.FeedForTest("{\"t\":\"realms.back\",\"realms\":[\"Forever RP\"],\"checkedAt\":" + unix(now) + "}");
                var toast = Session.Toasts.Visible.LastOrDefault();
                Check("back: on - one quiet toast, in the toast mechanism", Session.Toasts.Shown == shown + 1 && toast != null && toast.Kind == ToastKind.Realms && toast.Title.Contains("Forever RP is back up"), toast?.Title);
                shown = Session.Toasts.Shown;
                settings.ToastsOff = true;
                Session.FeedForTest("{\"t\":\"realms.back\",\"realms\":[\"Forever RP\"],\"checkedAt\":" + unix(now) + "}");
                Check("back: the global toast switch silences it too", Session.Toasts.Shown == shown);
                settings.ToastsOff = false;
                SettingsPage.SetRealmStatusForTest(false, true);
                Session.FeedForTest("{\"t\":\"realms.back\",\"realms\":[\"Forever RP\"],\"checkedAt\":" + unix(now) + "}");
                Check("back: with the strip switched off there is no note either", Session.Toasts.Shown == shown);
                SettingsPage.SetRealmStatusForTest(true, false);

                // ---- leaving the Lodge
                Session.Disconnect();
                Check("disconnect: the dots go", !RealmStripVisibleForTest && Session.Realms.Count == 0);

                // ---- nothing is ever sent
                Check("nothing was sent to the server for any of it", !sent.Any(o => (o.Str("t") ?? "").Contains("realm")), string.Join(",", sent.Select(o => o.Str("t"))));
            }
            catch (Exception ex)
            {
                notes.Add("FAIL exception: " + ex);
                ok = false;
            }
            finally
            {
                settings.RealmStatusOff = hadOff; settings.RealmsBackNotice = hadBack; settings.ToastsOff = hadToastsOff;
                Session.Clock = hadClock;
                Session.SentForTest = null;
                try { SettingsPage.SetRealmStatusForTest(!settings.RealmStatusOff, settings.RealmsBackNotice); } catch { }
                try { Session.Disconnect(); } catch { }
            }
            return "realms selftest: " + (ok ? "PASSED" : "FAILED") + "\r\n  " + string.Join("\r\n  ", notes);
        }
    }
}
