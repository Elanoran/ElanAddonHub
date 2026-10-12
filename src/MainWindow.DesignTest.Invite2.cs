using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // ELANSHUB_DESIGN_SHOTS=invite2 with --selftest <dir>: the in-app invite UI (Lodge 2.12) from synthetic frames - the Invite sheet with a made link and the
    // open invites (invite2-sheet.png, invite2-sheet-open.png before the link is made), the Create-a-lodge guide (invite2-guide.png, invite2-guide-install.png), Settings > Lodge > Danger zone
    // (invite2-danger.png) and the transfer-ownership prompt (invite2-transfer.png). Synthetic names, masked secrets (XXXX), no server, no network,
    // no clipboard. The server's own page (invite2-webpage.png) is rendered by Tools-free means: the page's HTML from InvitePage.Render loaded in a browser.
    public partial class MainWindow
    {
        async Task Invite2Shots(string dir)
        {
            var s = settings;
            var saved = new { s.LodgeUrl, s.LodgeCodeProtected, s.LodgeTokenProtected, s.LodgeDeviceName, s.LodgeName };
            var sendWas = InviteExchange.Send;
            const string masked = "i1.XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX";
            try
            {
                Width = 1100; Height = 900;
                Session.Disconnect();
                Session.NoNetworkForTest = true;
                Session.SentForTest = o => { };
                InviteForm.ClipboardSeam = t => { };
                InviteExchange.Send = req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                s.LodgeUrl = "https://nasferatu.dk/lodge";
                LodgeCredentials.Clear(s);
                s.LodgeName = "Cleo"; s.LodgeDeviceName = "Cleo-PC";   // no real user or PC name in a committed picture
                TabLodge.IsChecked = true;
                LodgePage.ShowChatForTest();
                Session.IsShownToUser = () => false;
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                // ---- the Invite sheet, owner, one link just made and two open ones
                Session.FeedForTest(Inv2Welcome("owner", true));
                Session.FeedForTest(Inv2Devices(now, 2));
                ShowTab("lodge");
                LodgePage.InviteSheetForTest.Open();
                Session.FeedForTest(InvInvitesFrame(now));
                await Task.Delay(700);
                Snapshot(System.IO.Path.Combine(dir, "invite2-sheet-open.png"));
                Session.FeedForTest(Inv2Created(now, "person", "Cleo", "member", 1, masked));
                await Task.Delay(900);
                Snapshot(System.IO.Path.Combine(dir, "invite2-sheet.png"));
                LodgePage.InviteSheetForTest.Close();
                await Task.Delay(300);

                // ---- the Create-a-lodge guide
                LodgePage.SignOut();
                var g = LodgePage.GuideForTest;
                g.NoClipboardForTest = true;
                LodgePage.CreateLodgeClickForTest();
                g.NextForTest();
                await Task.Delay(700);
                Snapshot(System.IO.Path.Combine(dir, "invite2-guide-install.png"));
                g.NextForTest();
                g.LinkBoxForTest = "elanshub://lodge/join#your.site/lodge|" + masked;
                await Task.Delay(700);
                Snapshot(System.IO.Path.Combine(dir, "invite2-guide.png"));
                g.Close();
                await Task.Delay(300);

                // ---- Settings > Lodge > Danger zone, and the transfer prompt
                Session.Disconnect();
                s.LodgeUrl = "https://nasferatu.dk/lodge";
                LodgeCredentials.StoreToken(s, s.LodgeUrl, "t1.SyntheticTokenSyntheticTokenSyntheticToken1", "Cleo-PC");
                LodgePage.ShowChatForTest();
                Session.FeedForTest(Inv2Welcome("owner", true));
                Session.FeedForTest(Inv2Devices(now, 1));
                ShowTab("settings");
                SettingsPage.Show("lodge");
                await Task.Delay(700);
                SettingsPage.ScrollEndForTest();
                await Task.Delay(500);
                Snapshot(System.IO.Path.Combine(dir, "invite2-danger.png"));

                SettingsPage.ShowTransferForTest("Transfer ownership first");
                Session.FeedForTest(Inv2Members("Elan:owner", "Bob:officer", "Zed:officer", "Dax:member"));
                await Task.Delay(500);
                SettingsPage.ScrollEndForTest();
                await Task.Delay(500);
                Snapshot(System.IO.Path.Combine(dir, "invite2-transfer.png"));
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "result.txt"), "invite2 design shots done");
            }
            catch (Exception e) { System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "result.txt"), "design shots crashed: " + e); }
            finally
            {
                InviteForm.ClipboardSeam = null;
                InviteExchange.Send = sendWas;
                Session.SentForTest = null;
                s.LodgeUrl = saved.LodgeUrl; s.LodgeCodeProtected = saved.LodgeCodeProtected; s.LodgeTokenProtected = saved.LodgeTokenProtected; s.LodgeDeviceName = saved.LodgeDeviceName; s.LodgeName = saved.LodgeName;
                Session.Disconnect();
                Session.NoNetworkForTest = false;
            }
            Quit();
        }
    }
}
