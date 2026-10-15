using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // --selftest "badges": the Lodge's badges and ladder (server feature "badges"). A fake server: synthetic frames in, the messages the Outpost sends out are recorded. Covers: the Ladder
    // view (rows, your own row highlighted, this month | all time asks for the right season), the badges card and the Settings > Profile list (earned with date, unearned with how to
    // earn and a progress line, secret ones as ???), the strip of the best badges on a profile card, the quiet "new" dot that goes when you look, "Hide me from the ladder" sending the
    // flag, the Guild Master's switch, a server without the feature (everything hidden, nothing sent), a switched-off feature (quiet) and refusals that never show a message.
    // The real server is tested by server/Lodge.Tests.
    public partial class MainWindow
    {
        static string BJ(string id, string family, string familyName, int tier, string name, string flavour, string how, bool hidden, bool earned, long at = 0, long value = 0, long target = 0) =>
            "{\"id\":" + Q(id) + ",\"family\":" + Q(family) + ",\"familyName\":" + Q(familyName) + ",\"tier\":" + tier + ",\"name\":" + Q(name) + ",\"flavour\":" + Q(flavour) + ",\"how\":" + Q(how)
            + ",\"hidden\":" + (hidden ? "true" : "false") + ",\"earned\":" + (earned ? "true" : "false") + (at > 0 ? ",\"at\":" + at : "") + (target > 0 ? ",\"value\":" + value + ",\"target\":" + target : "") + "}";

        static string BadgesMineFrame(bool hide, int news, string season = null)
        {
            var list = new[]
            {
                BJ("loot.bronze", "loot", "Loot Ledger", 1, "Pocket Change", "A hundred corpses, a hundred small surprises.", "100 kills recorded", false, true, 1_790_000_000, 100, 100),
                BJ("loot.silver", "loot", "Loot Ledger", 2, "Fat Purse", "The ledger needs a second page.", "1,000 kills recorded", false, true, 1_791_000_000, 1000, 1000),
                BJ("loot.gold", "loot", "Loot Ledger", 3, "Dragon's Hoard", "Somewhere a vendor is nervous.", "10,000 kills recorded", false, false, 0, 3412, 10000),
                BJ("flight.bronze", "flight", "Flight Clerk", 1, "First Flight", "Timetable pages, one by one.", "5 flight legs shared", false, true, 1_790_500_000, 5, 5),
                BJ("flight.silver", "flight", "Flight Clerk", 2, "Flight Clerk", "Stamps on a lot of tickets.", "25 flight legs shared", false, false, 0, 9, 25),
                BJ("veteran.gold", "veteran", "Veteran", 3, "Veteran", "A full year.", "365 days in the lodge", false, true, 1_791_200_000, 365, 365),
                BJ("host.gold", "host", "Host", 3, "Keeper of the Hall", "You host the Classic data for the whole lodge.", "1 Classic data hosted", false, false, 0, 0, 1),
                BJ("lucky", "secret", "Secrets", 2, "Lucky Strike", "You were the first in the lodge to see a drop rarer than one in a hundred.", "1 rare drops recorded first", true, true, 1_791_300_000, 1, 1),
                BJ("nightowl", "secret", "Secrets", 1, "???", "", "???", true, false),
            };
            return "{\"t\":\"badges.mine\",\"ok\":true,\"you\":\"Elan\",\"hide\":" + (hide ? "true" : "false") + ",\"new\":" + news + ",\"points\":742,\"rank\":2,\"season\":" + Q(season ?? DateTime.UtcNow.ToString("yyyy-MM"))
                + ",\"seasonPoints\":131,\"seasonRank\":3,\"earned\":5,\"total\":46,\"badges\":[" + string.Join(",", list) + "]}";
        }

        static string LadderFrame(string season, bool youHidden = false, bool includeYou = true)
        {
            string Top(params (string id, string family, int tier, string name)[] t) =>
                "[" + string.Join(",", t.Select(x => "{\"id\":" + Q(x.id) + ",\"family\":" + Q(x.family) + ",\"tier\":" + x.tier + ",\"name\":" + Q(x.name) + ",\"at\":1790000000}")) + "]";
            var rows = new List<string>
            {
                "{\"rank\":1,\"name\":\"Mia\",\"points\":1204,\"top\":" + Top(("loot.gold", "loot", 3, "Dragon's Hoard"), ("fisher.silver", "fisher", 2, "Patient Angler"), ("veteran.gold", "veteran", 3, "Veteran")) + "}",
            };
            if (includeYou) rows.Add("{\"rank\":2,\"name\":\"Elan\",\"points\":742,\"you\":true" + (youHidden ? ",\"hidden\":true" : "") + ",\"top\":" + Top(("veteran.gold", "veteran", 3, "Veteran"), ("loot.silver", "loot", 2, "Fat Purse"), ("lucky", "secret", 2, "Lucky Strike")) + "}");
            rows.Add("{\"rank\":3,\"name\":\"Kor\",\"points\":530,\"top\":" + Top(("herald.bronze", "herald", 1, "Town Crier")) + "}");
            rows.Add("{\"rank\":4,\"name\":\"Zed\",\"points\":211,\"top\":[]}");
            return "{\"t\":\"ladder.data\",\"ok\":true,\"season\":" + Q(season) + ",\"current\":" + Q(DateTime.UtcNow.ToString("yyyy-MM")) + ",\"seasons\":[" + Q(DateTime.UtcNow.ToString("yyyy-MM")) + ",\"2000-01\"],\"rows\":[" + string.Join(",", rows) + "]}";
        }

        static string BadgesMemberFrame(string name, int count)
        {
            var all = new[]
            {
                ("veteran.gold", "veteran", 3, "Veteran"), ("loot.silver", "loot", 2, "Fat Purse"), ("flight.bronze", "flight", 1, "First Flight"), ("herald.gold", "herald", 3, "Master of Ceremonies"),
                ("fisher.bronze", "fisher", 1, "Bait and Wait"), ("hearthside.silver", "hearthside", 2, "Regular"), ("armorer.bronze", "armorer", 1, "Show and Tell"),
            };
            return "{\"t\":\"badges.member\",\"ok\":true,\"name\":" + Q(name) + ",\"count\":" + count + ",\"badges\":[" + string.Join(",", all.Take(count).Select((x, i) =>
                "{\"id\":" + Q(x.Item1) + ",\"family\":" + Q(x.Item2) + ",\"tier\":" + x.Item3 + ",\"name\":" + Q(x.Item4) + ",\"at\":" + (1_790_000_000 + i * 1000) + "}")) + "]}";
        }

        string BadgesWelcome(string role, bool feature, string state = "{\"enabled\":true,\"locked\":false}", string server = "2.17.0")
        {
            var features = "\"reply\",\"react\",\"react2\",\"pin\",\"profile\",\"displayname\",\"lfg\",\"history2\",\"search\"" + (feature ? ",\"badges\"" : "");
            var w = FunWelcome(role, FunPeople(false), "", features, server);
            return state == null ? w : w.Substring(0, w.Length - 1) + ",\"badges\":" + state + "}";
        }

        async Task<string> BadgesTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            void Check(string what, bool cond, string detail = null) { SelfTestClock.Mark(what); notes.Add((cond ? "ok   " : "FAIL ") + what + (!cond && detail != null ? " [" + detail + "]" : "")); ok &= cond; }
            string P(string n) => Path.Combine(dir, n);
            var sent = new List<Dictionary<string, object>>();
            var errors = new List<string>();
            Action<string> onErr = t => errors.Add(t);
            Func<string, int> count = t => sent.Count(o => o.Str("t") == t);
            Func<string, Dictionary<string, object>> last = t => sent.LastOrDefault(o => o.Str("t") == t);
            try
            {
                Session.Disconnect();
                TabLodge.IsChecked = true;
                LodgePage.ShowChatForTest();
                Session.SentForTest = o => sent.Add(o);
                Session.Error += onErr;

                // ---- a server without the feature: nothing shows, nothing is asked
                Session.FeedForTest(BadgesWelcome("member", false, null));
                Check("old server: no Ladder row, nothing supported", !LodgePage.LadderRowVisibleForTest && !Session.SupportsBadges);
                Check("old server: no badges or ladder message is sent", !sent.Any(o => (o.Str("t") ?? "").StartsWith("badges.") || (o.Str("t") ?? "").StartsWith("ladder.")));
                LodgePage.ShowLadderForTest();
                Check("old server: the Ladder page cannot be opened", !LodgePage.LadderShownForTest);
                Session.RequestLadder("all"); Session.RequestMyBadges(); Session.SetHideFromLadder(true);
                Check("old server: asking anyway sends nothing", sent.Count(o => (o.Str("t") ?? "").StartsWith("badges.") || (o.Str("t") ?? "").StartsWith("ladder.")) == 0);
                SettingsPage.Show("lodge");
                Check("old server: Settings > Lodge has no ladder switch and Guild Master has no switch", !SettingsPage.LadderSettingsVisibleForTest && !SettingsPage.BadgesLodgeVisibleForTest);
                SettingsPage.Show("profile");
                Check("old server: Settings > Profile has no badges area", !SettingsPage.BadgesCardVisibleForTest);
                var bobOld = Session.Members.First(m => m.Name == "Bob");
                LodgePage.ShowProfileCard(bobOld, LodgePage);
                Check("old server: a profile card has no badges strip, no Badges button and asks nothing", LodgePage.PopCardForTest.Badges.Count == 0 && !LodgePage.PopBadgesVisibleForTest && count("badges.member") == 0);
                LodgePage.HideCardForTest();

                // ---- a lodge with badges: the welcome asks for my badges, once
                Session.Disconnect(); sent.Clear(); errors.Clear();
                Session.FeedForTest(BadgesWelcome("member", true));
                Check("badges: the feature is supported and the Ladder row shows", Session.SupportsBadges && LodgePage.LadderRowVisibleForTest);
                Check("badges: my badges were asked for once after the welcome", count("badges.mine") == 1);
                Session.FeedForTest(BadgesMineFrame(false, 2));
                Check("badges: my list holds 9 (earned, unearned, secret)", Session.MyBadges.Count == 9 && Session.EarnedCount == 5 && Session.TotalBadges == 46);
                Check("badges: 2 new, shown as a dot on the profile's Badges area and on the Ladder's button, nowhere else", Session.NewBadges == 2);

                // ---- quiet: no message, no toast, no chat line for any of this
                Session.FeedForTest("{\"t\":\"error\",\"text\":\"Badges are switched off on this lodge\",\"code\":\"disabled\"}");
                Session.FeedForTest("{\"t\":\"error\",\"text\":\"Guests have no badges\",\"code\":\"guest\"}");
                Session.RequestLadder("all");
                Session.FeedForTest("{\"t\":\"error\",\"text\":\"Slow down a little\",\"code\":\"rate\"}");
                Check("quiet: refusals about badges show no message anywhere", errors.Count == 0, string.Join("|", errors));
                Session.FeedForTest("{\"t\":\"error\",\"text\":\"Some other problem\"}");
                Check("quiet: other errors still show", errors.Count == 1);
                errors.Clear(); sent.Clear();

                // ---- the Ladder page
                LodgePage.ShowLadderForTest();
                Check("ladder: the page opens in the chat's place and asks for this month", LodgePage.LadderShownForTest && last("ladder.get")?.Str("season") == DateTime.UtcNow.ToString("yyyy-MM"), last("ladder.get")?.Str("season"));
                // the page asked with the UTC month of the clock; the server answers with the same key, as it does for the current season
                var month = DateTime.UtcNow.ToString("yyyy-MM");
                Session.FeedForTest(LadderFrame(month));
                var rows = LodgePage.LadderForTest.RowsForTest.ToList();
                Check("ladder: 4 rows in rank order", rows.Count == 4 && rows.Select(r => r.Rank).SequenceEqual(new[] { 1, 2, 3, 4 }) && rows[0].Login == "Mia");
                Check("ladder: your own row is the highlighted one", rows[1].IsYou && rows.Count(r => r.IsYou) == 1 && rows[1].NameText == "Elan (you)");
                Check("ladder: the points and the best badges are on the row", rows[0].PointsText == 1204L.ToString("N0") && rows[0].Top.Count == 3 && rows[3].Top.Count == 0);
                Check("ladder: a line says where you are", LodgePage.LadderForTest.YouLineForTest.Contains("number 2") && LodgePage.LadderForTest.YouLineForTest.Contains("this month"), LodgePage.LadderForTest.YouLineForTest);
                Check("ladder: the dot shows on 'Your badges' while there are new badges", LodgePage.LadderForTest.NewDotVisibleForTest);
                await ShotWait(300);
                Snapshot(P("badges-ladder.png"));

                // ---- this month | all time
                sent.Clear();
                LodgePage.LadderForTest.PickAllForTest();
                Check("season switch: All time asks for 'all'", last("ladder.get")?.Str("season") == "all" && LodgePage.LadderForTest.AllTimeForTest);
                Check("season switch: the old month's rows are not shown for all time", !LodgePage.LadderForTest.RowsForTest.Any());
                Session.FeedForTest(LadderFrame("all").Replace("\"points\":1204", "\"points\":9204"));
                Check("season switch: the all-time rows show, with the other points", LodgePage.LadderForTest.RowsForTest.First().PointsText == 9204L.ToString("N0") && LodgePage.LadderForTest.YouLineForTest.Contains("of all time"));
                sent.Clear();
                LodgePage.LadderForTest.PickMonthForTest();
                Check("season switch: This month asks for the month again", last("ladder.get")?.Str("season") == month, last("ladder.get")?.Str("season"));

                // ---- an empty month
                Session.FeedForTest("{\"t\":\"ladder.data\",\"ok\":true,\"season\":" + Q(month) + ",\"current\":" + Q(month) + ",\"seasons\":[" + Q(month) + "],\"rows\":[]}");
                Check("ladder: an empty month says so, calmly", LodgePage.LadderForTest.EmptyVisibleForTest && LodgePage.LadderForTest.YouLineForTest.Contains("no points"));
                Session.FeedForTest(LadderFrame(month));

                // ---- the badges card: earned with date, unearned with how to earn and progress, secret as ???
                sent.Clear();
                LodgePage.LadderForTest.BadgesClickForTest();
                Check("badges card: opens over the lodge with my badges", LodgePage.BadgesCardVisibleForTest && LodgePage.BadgesHeadingForTest == "Your badges");
                var items = LodgePage.BadgesListForTest.ItemsForTest.ToList();
                var bronze = items.First(b => b.Id == "loot.bronze"); var gold = items.First(b => b.Id == "loot.gold"); var host = items.First(b => b.Id == "host.gold");
                var lucky = items.First(b => b.Id == "lucky"); var owl = items.First(b => b.Id == "nightowl");
                Check("badges card: an earned badge has its name, tier and the date it was earned", bronze.Earned && bronze.Name == "Pocket Change" && bronze.TierLabel == "Bronze" && bronze.EarnedLine.StartsWith("Earned ") && bronze.EarnedLine.Contains("2026"), bronze.EarnedLine);
                Check("badges card: an unearned one shows how to earn it", !gold.Earned && gold.How == "10,000 kills recorded" && gold.EarnedLine == "Not earned yet");
                Check("badges card: ... with a progress line '3,412 / 10,000 kills recorded'", gold.HasProgress && gold.ProgressText == 3412L.ToString("N0") + " / 10,000 kills recorded" && Math.Abs(gold.Progress - 0.3412) < 0.0001, gold.ProgressText);
                Check("badges card: an earned badge has no progress line", !bronze.HasProgress);
                Check("badges card: a one-off badge reads 0 / 1 with its how-to", host.HasProgress && host.ProgressText == "0 / 1 Classic data hosted", host.ProgressText);
                Check("badges card: an earned secret shows in full", lucky.Earned && lucky.Name == "Lucky Strike" && lucky.How.Length > 3);
                Check("badges card: an unearned secret is ???", owl.Name == "???" && owl.How == "???" && owl.EarnedLine == "A secret" && !owl.HasProgress && owl.Flavour.Length == 0);
                Check("badges card: grouped by family, in the server's order", LodgeSession.BadgeGroups(items).Select(g => g.Name).SequenceEqual(new[] { "Loot Ledger", "Flight Clerk", "Veteran", "Host", "Secrets" }));
                Check("badges card: the draw is only an icon on a tier-coloured disc (no game art)", bronze.Glyph.Length == 1 && bronze.DiscBrush != gold.DiscBrush && bronze.Glyph != host.Glyph);
                Check("badges card: looking tells the server (the dot goes) and sends nothing else", count("badges.seen") == 1 && Session.NewBadges == 0 && !LodgePage.LadderForTest.NewDotVisibleForTest);
                await ShotWait(300);
                Snapshot(P("badges-panel.png"));
                LodgePage.CloseBadgesForTest();
                Check("badges card: closes", !LodgePage.BadgesCardVisibleForTest);

                // ---- the art: every family on every tier disc (no game art: an icon glyph on a tier-coloured disc), earned and unearned
                var sheet = new System.Windows.Controls.StackPanel { Width = 560, Margin = new System.Windows.Thickness(16) };
                var disc = (System.Windows.DataTemplate)System.Windows.Application.Current.Resources["BadgeDisc.M"];
                foreach (var fam in BadgeArt.KnownFamilies)
                {
                    var row = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new System.Windows.Thickness(0, 0, 0, 8) };
                    row.Children.Add(new System.Windows.Controls.TextBlock { Text = fam, Width = 120, VerticalAlignment = System.Windows.VerticalAlignment.Center, Foreground = BadgeArt.TierBrush(0) });
                    for (int t = 1; t <= 4; t++)
                        row.Children.Add(new System.Windows.Controls.ContentControl { Content = new BadgeVM { Id = fam + t, Family = fam, Tier = t, Earned = true, Name = fam }, ContentTemplate = disc, Margin = new System.Windows.Thickness(0, 0, 8, 0) });
                    row.Children.Add(new System.Windows.Controls.ContentControl { Content = new BadgeVM { Id = fam + "0", Family = fam, Tier = 1, Earned = false, Name = fam }, ContentTemplate = disc, Margin = new System.Windows.Thickness(8, 0, 0, 0) });
                    sheet.Children.Add(row);
                }
                sheet.Measure(new System.Windows.Size(560, double.PositiveInfinity));
                sheet.Arrange(new System.Windows.Rect(sheet.DesiredSize));
                SnapshotSheet(sheet, P("badges-families.png"));
                Check("art: every family has its own glyph", BadgeArt.KnownFamilies.Select(BadgeArt.Glyph).Distinct().Count() == BadgeArt.KnownFamilies.Count());
                // ---- the strip on someone's profile card
                sent.Clear();
                var bob = Session.Members.First(m => m.Name == "Bob");
                LodgePage.ShowProfileCard(bob, LodgePage);
                Check("profile card: asks for their earned badges", last("badges.member")?.Str("name") == "Bob");
                Check("profile card: no strip while nothing has arrived, a Badges button is there", LodgePage.PopCardForTest.Badges.Count == 0 && LodgePage.PopBadgesVisibleForTest);
                Session.FeedForTest(BadgesMemberFrame("Bob", 7));
                var strip = LodgePage.PopCardForTest.Badges.ToList();
                Check("profile card: the best 5 of 7 show, highest tier first, '+2' for the rest", strip.Count == 5 && strip[0].Tier == 3 && strip[1].Tier == 3 && strip[2].Tier == 2 && LodgePage.PopCardForTest.BadgesMoreText == "+2",
                    string.Join(",", strip.Select(b => b.Id)));
                Check("profile card: the strip is visible", LodgePage.PopCardForTest.BadgesVisibility == System.Windows.Visibility.Visible);
                await ShotWait(300);
                SnapshotSheet(LodgePage.PopCardElementForTest, P("badges-profile-card.png"));
                LodgePage.PopBadgesClickForTest();
                Check("profile card: Badges opens their earned badges only", LodgePage.BadgesCardVisibleForTest && LodgePage.BadgesHeadingForTest == "Bob's badges" && LodgePage.BadgesListForTest.ItemsForTest.Count() == 7 && LodgePage.BadgesListForTest.ItemsForTest.All(b => b.Earned && !b.HasProgress));
                LodgePage.CloseBadgesForTest();
                LodgePage.HideCardForTest();
                Session.FeedForTest(BadgesMemberFrame("Tess", 0));
                var tess = Session.Members.First(m => m.Name == "Tess");
                LodgePage.ShowProfileCard(tess, LodgePage);
                Session.FeedForTest(BadgesMemberFrame("Tess", 0));
                Check("profile card: someone without badges has no strip at all", LodgePage.PopCardForTest.Badges.Count == 0 && LodgePage.PopCardForTest.BadgesVisibility == System.Windows.Visibility.Collapsed);
                LodgePage.HideCardForTest();

                // ---- Settings: Profile (the badges area with its dot), Lodge (hide me from the ladder)
                SettingsPage.Show("profile");
                Session.FeedForTest(BadgesMineFrame(false, 2));
                SettingsPage.FillBadgesForTest();
                Check("settings profile: the Badges area is there with the dot for new badges", SettingsPage.BadgesCardVisibleForTest && SettingsPage.BadgesDotVisibleForTest);
                sent.Clear();
                SettingsPage.BadgesToggleForTest();
                var listed = SettingsPage.BadgesPanelForTest.ItemsForTest.ToList();
                Check("settings profile: Show my badges lists them all with progress, and the dot goes", listed.Count == 9 && listed.Any(b => b.HasProgress) && !SettingsPage.BadgesDotVisibleForTest && count("badges.seen") == 1);
                Check("settings profile: the live preview card shows my best badges too", ProfileCardOfEditor()?.Badges.Count == 5, ProfileCardOfEditor()?.Badges.Count.ToString());

                SettingsPage.Show("lodge"); SettingsPage.FillBadgesForTest();
                Check("settings lodge: 'Hide me from the ladder' is there, off by default", SettingsPage.LadderSettingsVisibleForTest && !SettingsPage.HideLadderForTest);
                sent.Clear();
                SettingsPage.HideLadderSwitchForTest(true);
                var hideMsg = last("badges.hide");
                Check("settings lodge: switching it on sends hide=true", hideMsg != null && hideMsg.Bool("hide") && SettingsPage.HideLadderForTest);
                Session.FeedForTest(BadgesMineFrame(true, 0));
                Session.FeedForTest(LadderFrame(month, true));
                LodgePage.ShowLadderForTest();
                Check("hidden: your row says only you see it", LodgePage.LadderForTest.RowsForTest.First(r => r.IsYou).HiddenNote == "Only you see this row" && LodgePage.LadderForTest.YouLineForTest.Contains("Only you"));
                Session.FeedForTest(LadderFrame(month, false, false));
                Check("hidden: with no row of yours the line says you are hidden and the badges stay", LodgePage.LadderForTest.YouLineForTest.Contains("hidden") && Session.EarnedCount == 5);
                SettingsPage.Show("lodge");
                sent.Clear();
                SettingsPage.HideLadderSwitchForTest(false);
                Check("settings lodge: switching it off sends hide=false", last("badges.hide") != null && !last("badges.hide").Bool("hide"));
                LodgePage.HideLadderForTest();

                // ---- a guest: no badges of their own
                Session.Disconnect(); sent.Clear();
                Session.FeedForTest(FunWelcome("guest", "{\"id\":1,\"name\":\"Pat\",\"guest\":true,\"role\":\"guest\"},{\"id\":2,\"name\":\"Bob\",\"role\":\"officer\"}", "",
                    "\"reply\",\"react\",\"react2\",\"pin\",\"profile\",\"displayname\",\"lfg\",\"history2\",\"search\",\"badges\"", "2.17.0"));
                Check("guest: my badges are never asked for", count("badges.mine") == 0);
                SettingsPage.Show("lodge");
                Check("guest: no hide switch, no badges area", !SettingsPage.LadderSettingsVisibleForTest);

                // ---- the Guild Master: the whole feature on / off
                Session.Disconnect(); sent.Clear(); errors.Clear();
                Session.FeedForTest(BadgesWelcome("owner", true));
                Session.FeedForTest(BadgesMineFrame(false, 0));
                SettingsPage.Show("gm");
                Check("guild master: the switch is there, on, and can be changed", SettingsPage.BadgesLodgeVisibleForTest && SettingsPage.BadgesLodgeOnForTest && SettingsPage.BadgesLodgeEnabledForTest);
                SettingsPage.BadgesLodgeSwitchForTest(false);
                Check("guild master: switching off sends admin.badges on=false", last("admin.badges") != null && !last("admin.badges").Bool("on"));
                Session.FeedForTest("{\"t\":\"badges.state\",\"enabled\":false,\"locked\":false}");
                Check("guild master: when the server says it is off, the Ladder row, the page and every badge UI disappear quietly", !LodgePage.LadderRowVisibleForTest && !Session.SupportsBadges && Session.MyBadges.Count == 0 && errors.Count == 0);
                Check("guild master: ... and the switch shows off", !SettingsPage.BadgesLodgeOnForTest && SettingsPage.BadgesLodgeVisibleForTest);
                sent.Clear();
                Session.FeedForTest("{\"t\":\"badges.state\",\"enabled\":true,\"locked\":false}");
                Check("guild master: switched on again, the row is back and my badges are asked for", LodgePage.LadderRowVisibleForTest && Session.SupportsBadges && count("badges.mine") == 1);
                // the owner of an officer-less lodge: an officer or member never sees the switch
                Session.Disconnect();
                Session.FeedForTest(BadgesWelcome("officer", true));
                SettingsPage.Show("gm");
                Check("guild master: officers do not get the switch", !SettingsPage.BadgesLodgeVisibleForTest || !Session.CanSwitchBadges);

                // ---- switched off in the server's own settings: the owner can't turn it on
                Session.Disconnect(); sent.Clear();
                Session.FeedForTest(BadgesWelcome("owner", false, "{\"enabled\":true,\"locked\":true}"));
                SettingsPage.Show("gm");
                Check("locked: no Ladder row, the switch is shown but disabled", !LodgePage.LadderRowVisibleForTest && SettingsPage.BadgesLodgeVisibleForTest && !SettingsPage.BadgesLodgeEnabledForTest && !Session.CanSwitchBadges);
                Session.SetBadgesForLodge(true);
                Check("locked: asking to switch it on sends nothing", count("admin.badges") == 0);

                // ---- leaving a lodge starts clean
                Session.Disconnect(); sent.Clear();
                Session.FeedForTest(BadgesWelcome("member", true));
                Session.FeedForTest(BadgesMineFrame(false, 1));
                Session.FeedForTest(LadderFrame(month));
                Session.Disconnect();
                Check("disconnect: the badges and the ladder are cleared", !Session.SupportsBadges && Session.MyBadges.Count == 0 && Session.LadderRows.Count == 0 && !LodgePage.LadderRowVisibleForTest && Session.NewBadges == 0);
            }
            catch (Exception ex)
            {
                notes.Add("FAIL exception: " + ex);
                ok = false;
            }
            finally
            {
                Session.Error -= onErr;
                Session.SentForTest = null;
                try { LodgePage.CloseBadgesForTest(); LodgePage.HideCardForTest(); } catch { }
                try { Session.Disconnect(); } catch { }
            }
            return "badges selftest: " + (ok ? "PASSED" : "FAILED") + "\r\n  " + string.Join("\r\n  ", notes);
        }

        ProfileCardVM ProfileCardOfEditor() => SettingsPage.ProfileModel?.Card;
    }
}
