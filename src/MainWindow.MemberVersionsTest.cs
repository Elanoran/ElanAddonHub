using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;
using Elans.Shared;

namespace ElansAddonHub
{
    // --selftest "memberversions": who runs an old Outpost or old addons (Lodge 2.38). The rules (MemberVersions: wire ids, what this Outpost
    // says, behind = older than the newest of the member's OWN channel), then a lodge from synthetic frames: the old mark on the rows, the card's
    // lines, a member catching up, a new release making someone behind. Nothing is fetched (FetchChannel is replaced) and nothing is sent.
    public partial class MainWindow
    {
        static MemberVersions.Newest MvNewest(string app, params (string Id, string Name, string Ver)[] addons)
        {
            var n = new MemberVersions.Newest { App = app };
            foreach (var a in addons) { n.Addons[a.Id] = a.Ver; n.Names[a.Id] = a.Name; }
            return n;
        }

        static string MvUser(int id, string name, string app, string channel, string addons) =>
            "{\"id\":" + id + ",\"name\":" + Q(name) + ",\"role\":\"member\",\"game\":null" + (app != null ? ",\"app\":" + Q(app) : "")
            + (channel != null || addons != null ? ",\"versions\":{\"channel\":" + (channel == null ? "null" : Q(channel)) + ",\"addons\":{" + (addons ?? "") + "}}" : "") + "}";

        async Task<string> MemberVersionsTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            void Ck(string what, bool cond, string info = null) { notes.Add((cond ? "ok   " : "FAIL ") + what + (cond || info == null ? "" : " (" + info + ")")); ok &= cond; }
            var savedStable = MemberVersions.Stable; var savedTest = MemberVersions.Test; var savedFetch = MemberVersions.FetchChannel;
            int fetches = 0;
            try
            {
                MemberVersions.FetchChannel = t => { fetches++; return Task.FromResult<Manifest>(null); };

                // ---- the rules
                Ck("wire ids: the folder name without \"Elans\", lower case, letters and digits, at most 16",
                    MemberVersions.WireId("ElansToolkit") == "toolkit" && MemberVersions.WireId("ElansHub") == "hub" && MemberVersions.WireId("ElansHunterHelper") == "hunterhelper"
                    && MemberVersions.WireId("Elans") == "elans" && MemberVersions.WireId("") == null && MemberVersions.WireId("Some_Very-Long.AddonNameHere") == "someverylongaddo");
                Ck("plain versions only", MemberVersions.IsPlainVersion("0.22.1") && MemberVersions.IsPlainVersion("3") && !MemberVersions.IsPlainVersion("?")
                    && !MemberVersions.IsPlainVersion("1.2-beta") && !MemberVersions.IsPlainVersion("1.2.3.4.5") && !MemberVersions.IsPlainVersion(null));

                var stable = MvNewest("3.9.0", ("hub", "Elan's Hub", "1.18.4"), ("toolkit", "Elan's Toolkit", "0.22.0"), ("bags", "Elan's Bags", "0.4.29"));
                var test = MvNewest("3.9.1", ("hub", "Elan's Hub", "1.19.0"), ("toolkit", "Elan's Toolkit", "0.22.1"), ("bags", "Elan's Bags", "0.4.30"));
                LodgeVersions V(string ch, params (string, string)[] a) { var v = new LodgeVersions { Channel = ch }; foreach (var (k, x) in a) v.Addons[k] = x; return v; }
                string Str(List<MemberVersions.Behind> l) => string.Join("; ", l);

                var b = MemberVersions.Compare("3.9.0", V("stable", ("hub", "1.18.4"), ("toolkit", "0.22.0")), stable, test);
                Ck("someone on Stable with everything from Stable is not behind (no test build needed)", b.Count == 0, Str(b));
                b = MemberVersions.Compare("3.9.0", V("test", ("hub", "1.18.4"), ("toolkit", "0.22.0")), stable, test);
                Ck("the same on Test is behind on the Outpost, the Hub and the Toolkit", b.Count == 3 && b[0].Name == "Outpost" && b[0].Have == "3.9.0" && b[0].Newest == "3.9.1"
                    && b.Any(x => x.Name == "Elan's Toolkit" && x.Have == "0.22.0" && x.Newest == "0.22.1") && b.Any(x => x.Name == "Elan's Hub"), Str(b));
                b = MemberVersions.Compare("3.8.0", null, stable, test);
                Ck("an older Outpost that says no channel is compared with Stable (only its Outpost)", b.Count == 1 && b[0].Newest == "3.9.0", Str(b));
                b = MemberVersions.Compare("3.9.1", null, stable, test);
                Ck("a test build that says no channel is never behind Stable", b.Count == 0, Str(b));
                b = MemberVersions.Compare("3.9.0", V("stable", ("toolkit", "0.21.0")), stable, test);
                Ck("an addon they don't have isn't behind; an old one is", b.Count == 1 && b[0].Name == "Elan's Toolkit", Str(b));
                b = MemberVersions.Compare("3.9.1", V("test", ("toolkit", "0.21.0")), stable, null);
                Ck("a test member while Test isn't known yet: compared with Stable", b.Count == 1 && b[0].Newest == "0.22.0", Str(b));
                Ck("nothing known about the releases: nobody is behind", MemberVersions.Compare("1.0.0", V("stable", ("hub", "0.1")), null, null).Count == 0);
                Ck("wire ids: Elan's Profession Helper is \"professionhelper\" (16 characters, the server's limit, nothing cut)", MemberVersions.WireId("ElansProfessionHelper") == "professionhelper", MemberVersions.WireId("ElansProfessionHelper"));
                Ck("an unknown addon id is ignored", MemberVersions.Compare("3.9.0", V("stable", ("other", "0.0.1")), stable, test).Count == 0);

                // ---- what this Outpost says about itself
                var m = new Manifest { Hub = new HubInfo { Version = "3.9.1" }, Addons = new List<AddonInfo> {
                    new AddonInfo { Id = "ElansHub", Name = "Elan's Hub", Version = "1.19.0" }, new AddonInfo { Id = "ElansToolkit", Name = "Elan's Toolkit", Version = "0.22.1" },
                    new AddonInfo { Id = "ElansBags", Name = "Elan's Bags", Version = "0.4.30" }, new AddonInfo { Id = "ElansPriestHelper", Name = "Priest Helper", Version = "0.4.2" } } };
                var have = new Dictionary<string, string> { ["ElansHub"] = "1.19.0", ["ElansToolkit"] = "0.22.0", ["ElansBags"] = "?" };
                var mine = MemberVersions.Mine(new Settings { Channel = "test" }, m, a => have.TryGetValue(a.Id, out var x) ? x : null);
                Ck("mine: my channel, the installed addons with a readable version (not \"?\", not missing)", mine.Channel == "test" && mine.Addons.Count == 2
                    && mine.Addons["hub"] == "1.19.0" && mine.Addons["toolkit"] == "0.22.0", string.Join(",", mine.Addons.Select(kv => kv.Key + "=" + kv.Value)));
                Ck("mine on Stable says stable", MemberVersions.Mine(new Settings(), m, a => null).Channel == "stable");
                var newest = MemberVersions.Newest.From(m);
                Ck("the newest of a channel from its manifest", newest.App == "3.9.1" && newest.Addons["priesthelper"] == "0.4.2" && newest.Names["toolkit"] == "Elan's Toolkit");

                // ---- a lodge from synthetic frames
                MemberVersions.SetForTest(stable, test);
                Session.Disconnect();
                TabLodge.IsChecked = true;
                LodgePage.ShowChatForTest();
                var users = string.Join(",",
                    "{\"id\":1,\"name\":\"Elan\",\"role\":\"owner\",\"app\":\"3.9.1\"}",
                    MvUser(2, "Bob", "3.9.0", "stable", "\"hub\":\"1.18.4\",\"toolkit\":\"0.22.0\""),          // up to date on Stable
                    MvUser(3, "Mia", "3.9.1", "test", "\"hub\":\"1.18.4\",\"toolkit\":\"0.22.0\""),            // on Test, addons behind
                    MvUser(4, "Zed", "3.8.0", null, null),                                                     // an old Outpost: nothing but app
                    MvUser(5, "Tess", null, null, null));                                                      // an old server or client: nothing at all
                Session.FeedForTest(FunWelcome("owner", users, "", "\"reply\",\"react\",\"react2\",\"pin\",\"profile\",\"displayname\",\"lfg\",\"history2\",\"search\",\"versions\"", "2.38.0"));
                await Task.Delay(400);
                MemberVM Who(string n) => Session.Members.FirstOrDefault(x => x.Name == n);
                var bob = Who("Bob"); var mia = Who("Mia"); var zed = Who("Zed"); var tess = Who("Tess");
                Ck("the welcome's members are there", bob != null && mia != null && zed != null && tess != null);
                if (bob == null || mia == null || zed == null || tess == null) return "memberversions selftest: FAILED\r\n  " + string.Join("\r\n  ", notes);
                Ck("the server feature is read", Session.SupportsVersions);
                Ck("Bob (Stable, everything from Stable): no mark, \"Outpost 3.9.0 · Stable\"", !bob.IsBehind && bob.OldVisibility == Visibility.Collapsed && bob.CardVersionLine == "Outpost 3.9.0 · Stable", bob.CardVersionLine);
                Ck("Mia (Test): marked, behind on the Hub and the Toolkit, not the Outpost", mia.IsBehind && mia.CardVersionLine == "Outpost 3.9.1 · Test channel"
                    && mia.CardBehindLine.Contains("Elan's Toolkit 0.22.0 → 0.22.1") && mia.CardBehindLine.Contains("Elan's Hub 1.18.4 → 1.19.0") && !mia.CardBehindLine.Contains("Outpost"), mia.CardBehindLine);
                Ck("Mia's tooltip and the mark's tip say it too", mia.Tip.Contains("Behind: ") && mia.OldTip.StartsWith("Not up to date: "), mia.OldTip);
                Ck("Zed (an older Outpost): behind on the Outpost against Stable, no channel shown", zed.IsBehind && zed.Behind.Count == 1 && zed.Behind[0].Newest == "3.9.0" && zed.CardVersionLine == "Outpost 3.8.0", zed.CardVersionLine + " / " + zed.CardBehindLine);
                Ck("Tess (nothing known): no mark and no version line", !tess.IsBehind && tess.CardVersionVisibility == Visibility.Collapsed);
                Ck("nothing was fetched (both channels were known)", fetches == 0, fetches.ToString());

                // the mark on the rows: as many visible "Old version" marks as members who are behind (each once per list it is in)
                await Task.Delay(200);
                int Marks() => Descendants<TextBlock>(LodgePage).Count(t => AutomationProperties.GetName(t) == "Old version" && t.IsVisible);
                var marks = Marks();
                Ck("the old mark shows on the rows of Mia and Zed only", marks >= 2 && Descendants<TextBlock>(LodgePage).Where(t => AutomationProperties.GetName(t) == "Old version" && t.IsVisible)
                    .All(t => t.DataContext is MemberVM x && (x.Name == "Mia" || x.Name == "Zed")), marks.ToString());

                // pictures (ELANSHUB_SHOTS=1): the list with the marks, and Mia's and Zed's hover cards
                ShotOf(LodgePage, System.IO.Path.Combine(dir, "memberversions-list.png"), Rect.Empty, 1);
                foreach (var who in new[] { mia, zed, bob })
                {
                    var card = new Border
                    {
                        Background = (System.Windows.Media.Brush)FindResource("Brush.Surface1"), Padding = new Thickness(12),
                        Child = new ContentControl { ContentTemplate = (DataTemplate)LodgePage.FindResource("MemberCard"), Content = who },
                    };
                    card.Measure(new Size(330, double.PositiveInfinity));
                    card.Arrange(new Rect(card.DesiredSize));
                    ShotOf(card, System.IO.Path.Combine(dir, "memberversions-card-" + who.Name + ".png"), Rect.Empty, 2);
                }

                // Mia updates: the user frame clears her mark
                Session.FeedForTest("{\"t\":\"user\",\"user\":" + MvUser(3, "Mia", "3.9.1", "test", "\"hub\":\"1.19.0\",\"toolkit\":\"0.22.1\"") + "}");
                await Task.Delay(200);
                Ck("Mia catches up: the mark goes", !mia.IsBehind && mia.OldVisibility == Visibility.Collapsed, mia.CardBehindLine);

                // a new stable release: Bob (on Stable) is behind now, Mia (on Test, already newer) isn't
                MemberVersions.SetForTest(MvNewest("3.9.1", ("hub", "Elan's Hub", "1.19.0"), ("toolkit", "Elan's Toolkit", "0.22.1")), test);
                await Task.Delay(200);
                Ck("a new Stable release: Bob is behind on all three, Mia still isn't", bob.IsBehind && bob.Behind.Count == 3 && !mia.IsBehind, bob.CardBehindLine);
                Ck("the rows follow", Descendants<TextBlock>(LodgePage).Any(t => AutomationProperties.GetName(t) == "Old version" && t.IsVisible && t.DataContext == bob));

                // a member on Test while Test isn't known yet: the session asks for it (once), and compares with Stable meanwhile
                MemberVersions.SetForTest(stable, null);
                fetches = 0;
                Session.FeedForTest("{\"t\":\"join\",\"user\":" + MvUser(6, "Kor", "3.9.1", "test", "\"toolkit\":\"0.21.0\"") + "}");
                await Task.Delay(300);
                var kor = Who("Kor");
                Ck("a test member while Test isn't known: Test is read (once) and Stable is used meanwhile", kor != null && kor.IsBehind && kor.Behind[0].Newest == "0.22.0" && fetches == 1, fetches + " / " + kor?.CardBehindLine);

                // what I send: only when connected and the server has the feature (fed frames are not a connection)
                Session.SetMyVersions(mine);
                Ck("my versions are kept for the next connection (nothing is sent while offline)", Session.MyVersionsJsonForTest != null && Session.MyVersionsJsonForTest.Contains("\"t\":\"versions\"")
                    && Session.MyVersionsJsonForTest.Contains("\"toolkit\":\"0.22.0\""), Session.MyVersionsJsonForTest);
            }
            catch (Exception e) { ok = false; notes.Add("crashed: " + e); }
            finally
            {
                MemberVersions.FetchChannel = savedFetch;
                MemberVersions.SetForTest(savedStable, savedTest);
                Session.Disconnect();
            }
            return "memberversions selftest: " + (ok ? "PASSED" : "FAILED") + "\r\n  " + string.Join("\r\n  ", notes);
        }
    }
}
