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
    // ELANSHUB_DESIGN_SHOTS=invites with --selftest <dir>: renders the one-time-invite UI from synthetic frames - the Guild Master invite panel
    // (invites-gm.png), Settings > Lodge > Your devices (invites-devices.png) and the join box with the "used or expired" message
    // (invites-join-error.png). No server, no network; quits when done.
    public partial class MainWindow
    {
        async Task InvitesShots(string dir)
        {
            var s = settings;
            var saved = new { s.LodgeUrl, s.LodgeCodeProtected, s.LodgeTokenProtected, s.LodgeDeviceName, s.LodgeName };
            var sendWas = InviteExchange.Send;
            try
            {
                Width = 1100; Height = 900;
                Session.Disconnect();
                s.LodgeUrl = "https://nasferatu.dk/lodge";
                LodgeCredentials.Clear(s);
                s.LodgeName = "Cleo"; s.LodgeDeviceName = "Cleo-PC";   // no real user or PC name in a committed picture
                TabLodge.IsChecked = true;
                LodgePage.ShowChatForTest();
                Session.IsShownToUser = () => false;
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                // ---- the Guild Master panel
                Session.FeedForTest(InvWelcome("owner", "2.10.0", true, true, true, "Elan-PC"));
                Session.FeedForTest("{\"t\":\"admin.members\",\"members\":[{\"name\":\"Elan\",\"role\":\"owner\",\"online\":true,\"hasCode\":false,\"devices\":2},{\"name\":\"Bob\",\"role\":\"officer\",\"online\":true,\"hasCode\":true,\"devices\":1},"
                    + "{\"name\":\"Dax\",\"role\":\"member\",\"online\":false,\"hasCode\":true,\"devices\":0}],\"channels\":[{\"id\":\"general\",\"name\":\"General\",\"type\":\"text\",\"minRole\":\"guest\",\"max\":0}]}");
                Session.FeedForTest(InvInvitesFrame(now));
                Session.FeedForTest(InvDevicesFrame("admin.devices", now, true));
                Session.FeedForTest(InvDevicesFrame("devices", now, false));
                Session.FeedForTest("{\"t\":\"admin.invite.created\",\"id\":\"zz\",\"kind\":\"person\",\"name\":\"Cleo\",\"role\":\"member\",\"by\":\"Elan\",\"created\":" + now + ",\"expires\":" + (now + 7L * 86400000)
                    + ",\"max\":1,\"uses\":0,\"used\":false,\"expired\":false,\"revoked\":false,\"pending\":true,\"secret\":\"i1.XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX\"}");
                ShowTab("settings");
                SettingsPage.Show("gm");
                await Task.Delay(900);
                Snapshot(System.IO.Path.Combine(dir, "invites-gm.png"));

                // ---- Your devices
                SettingsPage.Show("lodge");
                await Task.Delay(700);
                Snapshot(System.IO.Path.Combine(dir, "invites-devices.png"));

                // ---- the join box with an old link
                InviteExchange.Send = req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Gone) { Content = new StringContent("{\"error\":\"x\",\"reason\":\"used-or-expired\"}") });
                Session.Disconnect();
                ShowTab("lodge");
                LodgePage.SignOut();
                LodgePage.JoinWithLink("elanshub://lodge/join#nasferatu.dk/lodge|i1.XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX");
                await Task.Delay(900);
                Snapshot(System.IO.Path.Combine(dir, "invites-join-error.png"));
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "result.txt"), "invites design shots done; join status: " + LodgePage.JoinStatusForTest);
            }
            catch (Exception e) { System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "result.txt"), "design shots crashed: " + e); }
            finally
            {
                InviteExchange.Send = sendWas;
                s.LodgeUrl = saved.LodgeUrl; s.LodgeCodeProtected = saved.LodgeCodeProtected; s.LodgeTokenProtected = saved.LodgeTokenProtected; s.LodgeDeviceName = saved.LodgeDeviceName; s.LodgeName = saved.LodgeName;
                Session.Disconnect();
            }
            Quit();
        }
    }
}
