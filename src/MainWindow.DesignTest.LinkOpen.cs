using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // ELANSHUB_DESIGN_SHOTS=linkopen with --selftest <dir>: the join panel opened by a clicked elanshub:// link - first on a PC with no lodge,
    // then on a PC already signed in to another lodge. Synthetic server names and a synthetic invite; the secret is masked on screen.
    // Writes linkopen-join.png and linkopen-connected.png; quits when done.
    public partial class MainWindow
    {
        async Task LinkOpenShots(string dir)
        {
            var s = settings;
            var saved = new { s.LodgeUrl, s.LodgeCodeProtected, s.LodgeTokenProtected, s.LodgeDeviceName, s.LodgeName };
            var sendWas = InviteExchange.Send;
            try
            {
                Width = 1100; Height = 900;
                InviteExchange.Send = req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Gone) { Content = new StringContent("{}") });
                Session.NoNetworkForTest = true;
                Session.Disconnect();
                LodgeCredentials.Clear(s);
                s.LodgeUrl = null;
                s.LodgeName = "Cleo"; s.LodgeDeviceName = "Cleo-PC";   // no real user or PC name in a committed picture
                TabLodge.IsChecked = true;
                Session.IsShownToUser = () => false;

                LodgePage.NameBoxForTest = "Cleo";
                OpenInviteLink("elanshub://lodge/join#join.example.test/lodge|" + InvA);
                await Task.Delay(900);
                Snapshot(System.IO.Path.Combine(dir, "linkopen-join.png"));

                LodgePage.CancelEditForTest();
                LodgeCredentials.StoreToken(s, "https://oldlodge.example.test/lodge", TokA, "Cleo-PC");
                LodgePage.ShowChatForTest();
                OpenInviteLink("elanshub://lodge/join#join.example.test/lodge|" + InvA);
                await Task.Delay(900);
                Snapshot(System.IO.Path.Combine(dir, "linkopen-connected.png"));
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "result.txt"), "linkopen design shots done; banner: " + LodgePage.IncomingTitleForTest + " | " + LodgePage.IncomingTextForTest);
            }
            catch (Exception e) { System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "result.txt"), "design shots crashed: " + e); }
            finally
            {
                InviteExchange.Send = sendWas;
                Session.NoNetworkForTest = false;
                s.LodgeUrl = saved.LodgeUrl; s.LodgeCodeProtected = saved.LodgeCodeProtected; s.LodgeTokenProtected = saved.LodgeTokenProtected; s.LodgeDeviceName = saved.LodgeDeviceName; s.LodgeName = saved.LodgeName;
                Session.Disconnect();
            }
            Quit();
        }
    }
}
