using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;

namespace ElansAddonHub
{
    // Part of --selftest "cairn" (2.87): the honest way into sharing - the welcome guide's Sharing step (what / with whom / why, the switch, "Share"
    // and "Not now" of equal weight), the calm card on Atlas Home (once per offer, remembered; back only when the Lodge offers more; "Turn on"
    // changes the setting; "What is shared?" opens Privacy; never for guests or without a Lodge), and the quiet line on item / creature pages.
    public partial class MainWindow
    {
        static void Press(ButtonBase b) => b?.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

        async Task SharingGuideTest(Action<string, bool, string> Check, string dir)
        {
            var savedAnswered = settings.SharingInviteAnswered; var savedOff = settings.AtlasShareOff;
            try
            {
                // ---- the welcome guide's Sharing step
                bool on = false; int sets = 0;
                var g = new WelcomeGuide(new WelcomeGuide.Host { SharingOn = () => on, SetSharing = v => { on = v; sets++; }, Addons = () => new List<GuideAddon>(), WowRoot = () => null });
                g.Show(WelcomeGuide.SharingStep);
                var txt = g.SharingTextForTest;
                Check("guide: a Sharing step right after the Lodge step says what is shared, with whom and why, exactly",
                    WelcomeGuide.SharingStep == 3 && WelcomeGuide.Steps == 5 && g.Step == 3 && txt.Contains("What is shared") && txt.Contains("With whom") && txt.Contains("Why")
                    && txt.Contains("Ids and counts") && txt.Contains("no character names") && txt.Contains("rare finds show your Lodge name") && txt.Contains("only if you choose")
                    && txt.Contains("Cairn") && txt.Contains("give to get") && txt.Contains("Flight times have their own switch"), txt);
                Check("guide: the switch is right there and shows the setting as it is (off)", g.SharingSwitchForTest != null && g.SharingSwitchForTest.IsChecked == false, null);
                var y = g.ShareYesForTest; var n = g.ShareNotNowForTest;
                Check("guide: \"Share\" and \"Not now\" weigh the same (same style, same width, neither is the primary)",
                    y != null && n != null && y.Style == n.Style && y.MinWidth == n.MinWidth && y.Style == (Style)FindResource("Button.M"), null);
                Press(n);
                Check("guide: \"Not now\" keeps sharing off and moves on", !on && g.Step == 4, "on=" + on + " step=" + g.Step);
                g.Show(WelcomeGuide.SharingStep);
                Press(g.ShareYesForTest);
                Check("guide: \"Share\" turns it on and moves on", on && g.Step == 4, null);
                g.Show(WelcomeGuide.SharingStep);
                Check("guide: when sharing is on already, the switch shows it on", g.SharingSwitchForTest.IsChecked == true, null);
                g.SharingSwitchForTest.IsChecked = false; Press(g.SharingSwitchForTest);
                Check("guide: the switch itself changes the setting", !on, null);

                // ---- the card on Atlas Home: a member whose sharing is off, on a Lodge without the Cairn yet
                settings.AtlasShareOff = true; settings.SharingInviteAnswered = null; settings.LodgeUrl = "http://cairn.test/lodge";
                Session.Disconnect();
                Session.FeedForTest(CairnWelcome("member", false));
                ShowTab("atlas");
                async Task Home() { AtlasView.History.Clear(); await AtlasView.GoStart(); await (AtlasView.StartFillForTest ?? Task.CompletedTask); await Task.Delay(80); }
                await Home();
                Check("card: a member whose sharing is off sees one calm card on Atlas Home",
                    AtlasView.SharingInviteShownForTest && AtlasView.SharingInviteTextForTest.Contains("Turn on sharing to add yours") && !AtlasView.SharingInviteTextForTest.Contains("Cairn"), AtlasView.SharingInviteTextForTest);
                var cOn = AtlasView.SharingInviteOnForTest; var cNo = AtlasView.SharingInviteNotNowForTest;
                Check("card: \"Turn on\" and \"Not now\" weigh the same; \"What is shared?\" is there", cOn != null && cNo != null && cOn.Style == cNo.Style && AtlasView.SharingInviteWhatForTest != null, null);
                Press(cNo);
                Check("card: \"Not now\" hides it, remembers the answer and changes nothing", !AtlasView.SharingInviteShownForTest && settings.SharingInviteAnswered == "atlas" && settings.AtlasShareOff, settings.SharingInviteAnswered);
                await Home();
                Session.FeedForTest(CairnWelcome("member", false));
                await Home();
                Check("card: not again after \"Not now\" (a new visit, a reconnect with the same offer)", !AtlasView.SharingInviteShownForTest, null);

                // ---- something new is offered: the Lodge starts handing out the Cairn
                Session.FeedForTest(CairnWelcome("member", true));
                await Task.Delay(80);
                await Home();
                Check("card: back once when the Lodge offers more (the Cairn's numbers)", AtlasView.SharingInviteShownForTest && AtlasView.SharingInviteTextForTest.Contains("get the Cairn's numbers from other Lodges"), AtlasView.SharingInviteTextForTest);
                Press(AtlasView.SharingInviteWhatForTest);
                await Task.Delay(100);
                Check("card: \"What is shared?\" opens Settings > Privacy", SettingsPage.PrivacyShowsForTest, null);
                ShowTab("atlas");
                await Home();
                Press(AtlasView.SharingInviteOnForTest);
                Check("card: \"Turn on\" switches Atlas sharing on (the same setting as Settings > Lodge) and the card goes",
                    !settings.AtlasShareOff && settings.SharingInviteAnswered == "atlas+cairn" && !AtlasView.SharingInviteShownForTest, null);
                settings.AtlasShareOff = true;
                await Home();
                Check("card: switching sharing off later does not bring it back for the same offer", !AtlasView.SharingInviteShownForTest, null);

                // ---- never for guests, never without a Lodge
                settings.SharingInviteAnswered = null;
                Session.Disconnect();
                Session.FeedForTest(CairnWelcome("guest", true));
                Check("card: never for a guest", SharingInviteText() == null, null);
                Session.Disconnect();
                Check("card: never without a Lodge", SharingInviteText() == null, null);

                // ---- the quiet line where the Cairn's rows would be
                CairnAtlas.ResetForTest();
                Session.FeedForTest(CairnWelcome("member", true));
                settings.AtlasShareOff = true;
                var tl = AtlasTestLodge(); var hadLodge = AtlasQuery.Context.Lodge;
                AtlasQuery.Context.Lodge = () => tl;                   // npc 1 is a creature the Lodge knows (a real page)
                var item = AtlasQuery.Build("item:100");
                var npc = AtlasQuery.Build("npc:1");
                AtlasQuery.Context.Lodge = hadLodge;
                Check("quiet line: a member who doesn't share gets ONE line on an item and a creature page (not one per row)",
                    item.Notes.Count(x => x == AtlasQuery.CairnShareNote) == 1 && npc.Notes.Count(x => x == AtlasQuery.CairnShareNote) == 1, "npc notfound=" + npc.NotFound + " kind=" + npc.Kind + ": " + string.Join(" / ", npc.Notes));
                await AtlasView.Open("item:100"); await AtlasView.WaitForTest(); await Task.Delay(80);
                var link = Descendants<Button>(AtlasView).FirstOrDefault(b => (b.Tag as string) == "cairn-share");
                Check("quiet line: it carries the way to the sharing switch", link != null && (link.Content as string) == "Turn on sharing", null);
                Press(link);
                await Task.Delay(100);
                Check("quiet line: the link opens Settings > Lodge (where the switch is)", SettingsPage.Visibility == Visibility.Visible && settings.AtlasShareOff, null);
                ShowTab("atlas");
                settings.AtlasShareOff = false;
                Check("quiet line: gone while sharing is on", !AtlasQuery.Build("item:100").Notes.Contains(AtlasQuery.CairnShareNote), null);
                Session.Disconnect();
                Session.FeedForTest(CairnWelcome("guest", true));
                settings.AtlasShareOff = true;
                Check("quiet line: not for guests (the data isn't for them either way)", !AtlasQuery.Build("item:100").Notes.Contains(AtlasQuery.CairnShareNote), null);
                Session.Disconnect();
                ResetAtlasForTest();
            }
            finally
            {
                settings.SharingInviteAnswered = savedAnswered; settings.AtlasShareOff = savedOff;
                SettingsStore.Save(settings);
            }
        }
    }
}
