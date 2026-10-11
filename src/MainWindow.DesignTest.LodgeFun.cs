using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // ELANSHUB_DESIGN_SHOTS=lodgefun with --selftest <dir>: renders the Lodge 2.37 additions from synthetic frames - the grouped online list,
    // a "Looking for group" card, item chips and the search panel (lodge-fun-*.png). Item icons are synthetic squares written to the
    // scratch icon cache (nothing is read from or written to the WoW folder). Quits when done.
    public partial class MainWindow
    {
        async Task LodgeFunShots(string dir)
        {
            var s = settings;
            var hadCollapsed = s.LodgeGroupsCollapsed;
            try
            {
                Width = 1180; Height = 760;
                s.LodgeGroupsCollapsed = null;
                Session.Disconnect();
                TabLodge.IsChecked = true;
                LodgePage.ShowChatForTest();
                Session.IsShownToUser = () => false;
                await FunShotGroups(dir);
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "result.txt"), "lodge fun design shots done");
            }
            catch (Exception e) { System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "result.txt"), "design shots crashed: " + e); }
            finally { s.LodgeGroupsCollapsed = hadCollapsed; Session.Disconnect(); }
            Quit();
        }

        async Task FunShotGroups(string dir)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string M(string id, string from, int fromId, string text, int minsAgo) =>
                "{\"t\":\"msg\",\"channel\":\"general\",\"id\":" + Q(id) + ",\"at\":" + (now - minsAgo * 60000L) + ",\"from\":" + Q(from) + ",\"fromId\":" + fromId + ",\"text\":" + Q(text) + "}";
            var history = string.Join(",", M("h1", "Bob", 2, "Deadmines tonight? We need one more DPS.", 12), M("h2", "Mia", 3, "I can heal, give me ten minutes to finish a quest", 10),
                M("h3", "Kor", 6, "Molten Core is on for Saturday, bring fire resistance", 4));
            Session.FeedForTest(FunWelcome("member", FunPeople(false), history));
            await Task.Delay(700);
            SnapshotElement(LodgePage.SidebarForTest, System.IO.Path.Combine(dir, "lodge-fun-groups.png"));
            Snapshot(System.IO.Path.Combine(dir, "lodge-fun-groups-window.png"));
        }
    }
}
