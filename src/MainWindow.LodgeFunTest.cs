using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // --selftest "lodgefun": the Lodge 2.37 additions - grouped online list, "Looking for group" cards, item links, history paging, search -
    // all from synthetic protocol frames (no server).
    public partial class MainWindow
    {
        // a game object of a presence frame
        static string FunGame(string cls, string file, int level, string zone, string name = null, int flags = 0, int group = 0, bool inst = false, string instName = null) =>
            "\"game\":{\"playing\":true,\"name\":" + Q(name ?? "X") + ",\"class\":" + Q(cls) + ",\"classFile\":" + Q(file) + ",\"race\":\"Human\",\"raceFile\":\"Human\",\"level\":" + level
            + ",\"zone\":" + Q(zone) + ",\"flags\":" + flags + ",\"groupSize\":" + group + ",\"inInstance\":" + (inst ? "true" : "false") + (instName != null ? ",\"instanceName\":" + Q(instName) : "") + "}";

        // a welcome frame of a 2.9 server: `me` is user 1
        static string FunWelcome(string role, string users, string history = "", string features = "\"reply\",\"react\",\"react2\",\"pin\",\"profile\",\"displayname\",\"lfg\",\"history2\",\"search\"", string server = "2.9.0") =>
            "{\"t\":\"welcome\",\"you\":1,\"name\":\"Elan\",\"role\":" + Q(role) + ",\"server\":" + Q(server) + ",\"maxFileMb\":25,\"canShareFiles\":true,\"canModerate\":" + (role == "owner" || role == "officer" ? "true" : "false")
            + ",\"canManage\":" + (role == "owner" ? "true" : "false") + ",\"canPin\":" + (role == "owner" || role == "officer" ? "true" : "false") + ","
            + "\"features\":[" + features + "],\"reactions\":[\"ready\",\"notready\",\"lol\",\"love\",\"fight\",\"loot\",\"wipe\",\"epic\"],"
            + "\"channels\":[{\"id\":\"general\",\"name\":\"General\",\"type\":\"text\",\"minRole\":\"guest\"},{\"id\":\"loot\",\"name\":\"Loot\",\"type\":\"text\",\"minRole\":\"guest\"},"
            + "{\"id\":\"hangout\",\"name\":\"Hangout\",\"type\":\"voice\",\"minRole\":\"guest\"}],"
            + "\"users\":[" + users + "],\"history\":{\"general\":[" + history + "],\"loot\":[]},\"pins\":{}}";

        async Task<string> LodgeFunTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            Action<string, bool> check = (what, cond) => { notes.Add((cond ? "ok   " : "FAIL ") + what); ok &= cond; };
            var s = settings;
            var hadCollapsed = s.LodgeGroupsCollapsed; var hadInvisible = s.LodgeInvisible;
            try
            {
                Session.Disconnect();
                s.LodgeGroupsCollapsed = null; s.LodgeInvisible = false;
                TabLodge.IsChecked = true;
                LodgePage.ShowChatForTest();
                await FunGrouping(dir, check);
                await FunLfg(dir, check);
            }
            catch (Exception e) { ok = false; notes.Add("crashed: " + e); }
            finally
            {
                s.LodgeGroupsCollapsed = hadCollapsed; s.LodgeInvisible = hadInvisible;
                SettingsStore.Save(s);
                Session.Disconnect();
            }
            return "lodge fun selftest: " + (ok ? "PASSED" : "FAILED") + "\r\n  " + string.Join("\r\n  ", notes);
        }

        // the people of the grouped-list frames: Elan (me), Bob, Mia ...
        string FunPeople(bool withInvisible = true) => string.Join(",",
            "{\"id\":1,\"name\":\"Elan\",\"role\":\"owner\"," + FunGame("Hunter", "HUNTER", 27, "Stranglethorn Vale", "Elanoran") + "}",
            "{\"id\":2,\"name\":\"Bob\",\"role\":\"officer\"}",
            "{\"id\":3,\"name\":\"Mia\",\"role\":\"member\"," + FunGame("Priest", "PRIEST", 24, "Wailing Caverns", "Miaheals", 0, 5, true, "Wailing Caverns") + "}",
            "{\"id\":4,\"name\":\"Tess\",\"role\":\"member\",\"status\":\"away\"}",
            "{\"id\":5,\"name\":\"Zed\",\"role\":\"member\",\"status\":\"busy\",\"note\":\"pulling\"," + FunGame("Warrior", "WARRIOR", 41, "The Hinterlands", "Zedwar") + "}",
            "{\"id\":6,\"name\":\"Kor\",\"role\":\"veteran\"," + FunGame("Mage", "MAGE", 60, "Molten Core", "Korfire", 32, 12, true, "Molten Core") + "}",
            "{\"id\":7,\"name\":\"Lee\",\"role\":\"member\",\"room\":\"hangout\",\"voice\":true," + FunGame("Rogue", "ROGUE", 33, "Ironforge", "Leestab") + "}",
            "{\"id\":8,\"name\":\"Dax\",\"role\":\"member\"," + FunGame("Druid", "DRUID", 19, "Teldrassil", "Daxbear", 0, 3) + "}"
            + (withInvisible ? ",{\"id\":9,\"name\":\"Pat\",\"role\":\"member\",\"invisible\":true}" : ""));

        async Task FunGrouping(string dir, Action<string, bool> check)
        {
            // ---- the owner's view: sees the invisible member in a separate group
            Session.FeedForTest(FunWelcome("owner", FunPeople()));
            await Task.Delay(400);
            var g = Session.Groups;
            string J(MemberGroup x) => string.Join(",", g.NamesIn(x));
            check("groups: In game = people with WoW running (Elan, Kor, Mia, Dax), by name", J(MemberGroup.InGame) == "Dax,Elan,Kor,Mia");
            check("groups: Online = in the Outpost, not playing (Bob)", J(MemberGroup.Online) == "Bob");
            check("groups: Away = away and Do not disturb, even in game (Tess, Zed)", J(MemberGroup.Away) == "Tess,Zed");
            check("groups: the owner sees the invisible member in their own Invisible group", J(MemberGroup.Invisible) == "Pat");
            check("groups: a voice-room member is not listed in any group (shown under the room)", !g.Groups.Any(x => x.Members.Any(m => m.Name == "Lee"))
                && Session.VoiceRooms.First().Members.Any(m => m.Name == "Lee"));
            check("groups: four headers, in order, with counts", string.Join("|", g.Groups.Select(x => x.Title + " " + x.Count)) == "In game 4|Online 1|Away 2|Invisible 1");

            var by = Session.Members.ToDictionary(m => m.Name);
            check("in game row: character in class colour, Lv + class, the place", by["Elan"].CharName == "Elanoran" && by["Elan"].InGameClassLine == "Lv 27 Hunter" && by["Elan"].ZoneLine == "Stranglethorn Vale"
                && by["Elan"].CharOwnerText.Contains("Elan"));
            check("in game row: badge for dungeon / raid / group, none in the open world", by["Mia"].InstanceTag == "Dungeon" && by["Kor"].InstanceTag == "Raid" && by["Dax"].InstanceTag == "Group 3" && by["Elan"].InstanceTag == ""
                && by["Mia"].InstanceTagTip.Contains("Wailing Caverns"));
            check("in game row: inside an instance the place is the instance", by["Mia"].ZoneLine == "Wailing Caverns" && by["Kor"].ZoneLine == "Molten Core");

            // ---- the real list: headers and rows are drawn
            await Task.Delay(300);
            var list = LodgePage.GroupListForTest;
            list.UpdateLayout();
            var rows = new List<TextBlock>();
            void Walk(DependencyObject d)
            {
                for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i++)
                {
                    var c = System.Windows.Media.VisualTreeHelper.GetChild(d, i);
                    if (c is TextBlock tb) rows.Add(tb);
                    Walk(c);
                }
            }
            Walk(list);
            var texts = rows.Select(r => r.Text).Where(t => !string.IsNullOrEmpty(t)).ToList();
            check("list: the headers are drawn quietly (Text.Caption), not gold capitals", texts.Contains("In game") && texts.Contains("Away") && !texts.Any(t => t == "ONLINE" || t == "IN GAME")
                && rows.First(r => r.Text == "In game").FontSize == 11);
            check("list: in game rows show Lv 27 Hunter and the zone [" + string.Join("/", texts.Take(25)) + "]", texts.Contains("Lv 27 Hunter") && texts.Contains("Stranglethorn Vale"));
            check("list: the dungeon / raid badges are drawn", texts.Contains("Dungeon") && texts.Contains("Raid") && texts.Contains("Group 3"));

            // ---- collapse is remembered
            g.Of(MemberGroup.Away).Expanded = false;
            check("collapse: the Away group is collapsed and remembered in the settings", !g.Of(MemberGroup.Away).Expanded && (settings.LodgeGroupsCollapsed ?? "").Contains("away") && g.Of(MemberGroup.Away).Count == 2);
            g.Of(MemberGroup.Away).Expanded = true;
            check("collapse: expanding again clears it", !(settings.LodgeGroupsCollapsed ?? "").Contains("away"));

            // ---- live changes move people between groups
            Session.FeedForTest("{\"t\":\"user\",\"user\":{\"id\":2,\"name\":\"Bob\",\"role\":\"officer\",\"status\":\"busy\"}}");
            check("live: Bob goes Do not disturb -> Away", J(MemberGroup.Away) == "Bob,Tess,Zed" && J(MemberGroup.Online) == "");
            check("live: a group with nobody disappears", !g.Groups.Any(x => x.Group == MemberGroup.Online));
            Session.FeedForTest("{\"t\":\"user\",\"user\":{\"id\":2,\"name\":\"Bob\",\"role\":\"officer\"," + FunGame("Paladin", "PALADIN", 55, "Un'Goro Crater", "Bobtank") + "}}");
            check("live: Bob starts WoW -> In game", J(MemberGroup.InGame) == "Bob,Dax,Elan,Kor,Mia" && by["Bob"].CharName == "Bobtank");
            Session.FeedForTest("{\"t\":\"user\",\"user\":{\"id\":2,\"name\":\"Bob\",\"role\":\"officer\"}}");
            check("live: Bob quits WoW -> Online again", J(MemberGroup.Online) == "Bob" && J(MemberGroup.InGame) == "Dax,Elan,Kor,Mia");
            Session.FeedForTest("{\"t\":\"user\",\"user\":{\"id\":7,\"name\":\"Lee\",\"role\":\"member\"," + FunGame("Rogue", "ROGUE", 33, "Ironforge", "Leestab") + "}}");
            check("live: leaving the voice room lists Lee in game", J(MemberGroup.InGame).Contains("Lee"));
            Session.FeedForTest("{\"t\":\"leave\",\"id\":3}");
            check("live: someone leaving is gone from the list", !g.Groups.Any(x => x.Members.Any(m => m.Name == "Mia")));

            // ---- a member (not the owner) never gets the invisible group; if a frame ever carried one it stays unlisted
            Session.FeedForTest(FunWelcome("member", FunPeople()));
            await Task.Delay(300);
            check("invisible: a member's list has no Invisible group and does not list Pat", !g.Groups.Any(x => x.Group == MemberGroup.Invisible) && !g.Groups.Any(x => x.Members.Any(m => m.Name == "Pat")));
            check("invisible: the pure rule - hidden unless it is me or the viewer is the owner", MemberGrouping.Of(new MemberVM { Name = "P", Invisible = true }, false) == MemberGroup.Hidden
                && MemberGrouping.Of(new MemberVM { Name = "P", Invisible = true }, true) == MemberGroup.Invisible
                && MemberGrouping.Of(new MemberVM { Name = "P", Invisible = true, IsMe = true }, false) == MemberGroup.Invisible);
            Session.FeedForTest(FunWelcome("member", FunPeople(false)));
            Session.FeedForTest("{\"t\":\"join\",\"user\":{\"id\":10,\"name\":\"Quin\",\"role\":\"member\"}}");
            check("invisible: a member who is not shown to a member simply is not there", !Session.Members.Any(m => m.Name == "Pat") && g.NamesIn(MemberGroup.Online).Contains("Quin"));

            // me, invisible: shown (only to me) in the dim group
            Session.SetInvisible(true);
            await Task.Delay(200);
            check("invisible: my own row sits in the Invisible group while I appear offline", g.NamesIn(MemberGroup.Invisible).Contains("Elan") && !g.NamesIn(MemberGroup.InGame).Contains("Elan"));
            Session.SetInvisible(false);
            check("invisible: and comes back when I show again", !g.NamesIn(MemberGroup.Invisible).Contains("Elan"));

            // ---- 2.8 server (no 2.9 features): the list works the same
            Session.FeedForTest(FunWelcome("owner", FunPeople(false), "", "\"reply\",\"react\",\"pin\",\"profile\",\"displayname\"", "2.8.0"));
            check("2.8 server: grouping is client-side, so it works the same", g.Groups.Count >= 3);
        }
    }
}
