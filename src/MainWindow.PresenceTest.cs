using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // --selftest: rich presence + Appear offline + automatic status, rendered/checked from synthetic protocol frames (no server)
    public partial class MainWindow
    {
        async Task<string> PresenceTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            void Check(string what, bool cond) { notes.Add((cond ? "ok   " : "FAIL ") + what); ok &= cond; }
            var s = settings;
            bool hadInvisible = s.LodgeInvisible, hadHint = s.InvisibleHintShown, hadZoneOff = s.ShareZoneOff, hadXpOff = s.ShareXpOff, hadAutoOff = s.AutoStatusOff;
            try
            {
                Session.Disconnect();
                s.LodgeInvisible = false; s.InvisibleHintShown = false; s.ShareZoneOff = false; s.ShareXpOff = false; s.AutoStatusOff = false;
                TabLodge.IsChecked = true;
                LodgePage.ShowChatForTest();
                string G(string name, string cls, string file, string race, string raceFile, int level, string zone, int flags, int xp, bool rested, int group, bool inst = false, string instName = null) =>
                    "\"game\":{\"playing\":true,\"name\":" + Q(name) + ",\"class\":" + Q(cls) + ",\"classFile\":" + Q(file) + ",\"race\":" + Q(race) + ",\"raceFile\":" + Q(raceFile)
                    + ",\"level\":" + level + ",\"zone\":" + Q(zone) + ",\"flags\":" + flags + (xp >= 0 ? ",\"xpPct\":" + xp : "") + ",\"rested\":" + (rested ? "true" : "false")
                    + ",\"groupSize\":" + group + ",\"inInstance\":" + (inst ? "true" : "false") + (instName != null ? ",\"instanceName\":" + Q(instName) : "") + ",\"sex\":2}";
                var users = string.Join(",",
                    "{\"id\":1,\"name\":\"Elan\",\"role\":\"owner\"," + G("Elan", "Hunter", "HUNTER", "Orc", "Orc", 60, "The Barrens", 0, 62, true, 0) + "}",
                    "{\"id\":2,\"name\":\"Bob\",\"role\":\"officer\",\"status\":\"busy\",\"note\":\"pulling the next pack\"," + G("Measley", "Paladin", "PALADIN", "Human", "Human", 25, "Wailing Caverns", 16 + 64 + 128, -1, false, 5, true, "Wailing Caverns") + "}",
                    "{\"id\":3,\"name\":\"Tess\",\"role\":\"member\"," + G("Tess", "Mage", "MAGE", "Gnome", "Gnome", 33, "Elwynn Forest", 1 + 128, 20, true, 3) + "}",
                    "{\"id\":4,\"name\":\"Dax\",\"role\":\"member\",\"status\":\"away\"," + G("Dax", "Rogue", "ROGUE", "Night Elf", "NightElf", 48, "Stormwind City", 4 + 8, 71, false, 0) + "}",
                    "{\"id\":5,\"name\":\"Kor\",\"role\":\"veteran\"," + G("Kor", "Warrior", "WARRIOR", "Tauren", "Tauren", 41, "The Hinterlands", 2, 5, false, 0) + "}",
                    "{\"id\":6,\"name\":\"Mira\",\"role\":\"member\",\"invisible\":true}",
                    "{\"id\":7,\"name\":\"Zed\",\"role\":\"member\",\"room\":\"hangout\",\"voice\":true,\"muted\":true," + G("Zed", "Priest", "PRIEST", "Dwarf", "Dwarf", 29, "Ironforge", 0, 44, false, 0) + "}");
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                Session.FeedForTest("{\"t\":\"welcome\",\"you\":1,\"name\":\"Elan\",\"role\":\"owner\",\"server\":\"2.5.0\",\"maxFileMb\":25,\"canShareFiles\":true,\"canModerate\":true,\"canManage\":true,\"canPin\":true,"
                    + "\"features\":[\"reply\",\"react\",\"react2\",\"pin\"],\"reactions\":[\"ready\",\"notready\",\"lol\",\"love\",\"fight\",\"loot\",\"wipe\",\"epic\"],"
                    + "\"channels\":[{\"id\":\"general\",\"name\":\"General\",\"type\":\"text\",\"minRole\":\"guest\"},{\"id\":\"hangout\",\"name\":\"Hangout\",\"type\":\"voice\",\"minRole\":\"guest\"}],"
                    + "\"users\":[" + users + "],\"history\":{\"general\":[{\"t\":\"msg\",\"channel\":\"general\",\"id\":\"p1\",\"at\":" + (now - 120000) + ",\"from\":\"Bob\",\"fromId\":2,\"text\":\"Wailing Caverns in ten minutes, who is in?\"},"
                    + "{\"t\":\"msg\",\"channel\":\"general\",\"id\":\"p2\",\"at\":" + (now - 60000) + ",\"from\":\"Tess\",\"fromId\":3,\"text\":\"On my way, finishing a quest in Elwynn\"}]},\"pins\":{}}");
                await Task.Delay(500);
                var by = Session.Members.ToDictionary(m => m.Name);
                Check("Barrens line: place · XP · Rested", by["Elan"].PlaceLine == "The Barrens · 62% · Rested");
                Check("dungeon line: instance · group, no XP", by["Bob"].PlaceLine == "Wailing Caverns · group 5");
                Check("class line: level race class, character name when it differs from the Lodge name", by["Bob"].CardClassLine == "Level 25 Human Paladin · Measley" && by["Elan"].CardClassLine == "Level 60 Orc Hunter");
                Check("in combat -> state text + fight icon; dead -> wipe icon; AFK -> moon", by["Tess"].StateText == "In combat" && by["Tess"].StateIcon != null && by["Kor"].StateText == "Dead"
                    && by["Kor"].StateIconVisibility == Visibility.Visible && by["Dax"].StateText == "AFK" && by["Dax"].AfkVisibility == Visibility.Visible && by["Elan"].StateText == "");
                Check("second line falls back to the status note, hidden when empty", by["Zed"].SecondLine.StartsWith("Ironforge") && by["Mira"].SecondLine == "invisible");
                Check("owner view: invisible member listed with the marker, hollow dot, dim", by["Mira"].Invisible && by["Mira"].InvisibleVisibility == Visibility.Visible && by["Mira"].NameOpacity < 1 && by["Mira"].CardStatusLine == "invisible");
                Check("card status line: chosen status and note", by["Bob"].CardStatusLine == "Busy · pulling the next pack");
                Check("voice line: room and muted", by["Zed"].CardVoiceLine == "Hangout · muted" && by["Zed"].CardVoiceVisibility == Visibility.Visible);
                Check("my card names me", by["Elan"].CardNameText == "Elan (you)");
                Check("chat name -> the same member (hover card)", Session.MessagesOf("general").First().Author == by["Bob"]);
                // an older server sends none of the new fields: nothing breaks, no place line beyond the zone
                Session.FeedForTest("{\"t\":\"join\",\"user\":{\"id\":9,\"name\":\"Old\",\"role\":\"member\",\"game\":{\"playing\":true,\"name\":\"Old\",\"class\":\"Druid\",\"classFile\":\"DRUID\",\"level\":12,\"zone\":\"Teldrassil\"}}}");
                var old = Session.Members.First(m => m.Name == "Old");
                Check("2.4 server user (no new fields): zone only, no XP", old.PlaceLine == "Teldrassil" && old.XpPercent == -1 && !old.InCombat);
                Session.FeedForTest("{\"t\":\"leave\",\"id\":9}");

                // ---- screenshots
                Snapshot(System.IO.Path.Combine(dir, "12-presence.png"));
                SnapshotElement(LodgePage.SidebarForTest, System.IO.Path.Combine(dir, "12-sidebar.png"));
                var cards = new[] { ("elan", by["Elan"]), ("bob", by["Bob"]), ("tess", by["Tess"]), ("dax", by["Dax"]), ("kor", by["Kor"]), ("mira", by["Mira"]), ("zed", by["Zed"]) };
                foreach (var (name, m) in cards)
                {
                    var tip = LodgePage.ShowCardForTest(m, LodgePage.SidebarForTest);
                    await Task.Delay(250);
                    SnapshotElement(tip, System.IO.Path.Combine(dir, "12-card-" + name + ".png"));
                    tip.IsOpen = false;
                }
                // the chat name opens the same card
                var chatTip = LodgePage.ShowCardForTest(Session.MessagesOf("general").First().Author, LodgePage.ChatListForTest);
                await Task.Delay(250);
                SnapshotElement(chatTip, System.IO.Path.Combine(dir, "12-card-chat.png"));
                chatTip.IsOpen = false;

                // ---- game message: what is sent, with the sharing switches
                var c = new GamePresence.Character { Name = "Elan", Realm = "Realm", Class = "Hunter", ClassFile = "HUNTER", Race = "Orc", RaceFile = "Orc", Sex = 2, Level = 60, Zone = "Wailing Caverns", Guild = "G", HasLive = true,
                    CombatKnown = true, Flags = 1 + 16 + 64 + 128, XpPercent = 41, Rested = true, GroupSize = 5, InInstance = true, InstanceName = "Wailing Caverns" };
                Session.Presence.SetForTest(c, true);
                var g = Session.BuildGameMessage();
                Check("game message: zone, instance, flags, xp, rested, group, visibility", (string)g["zone"] == "Wailing Caverns" && (string)g["instanceName"] == "Wailing Caverns" && (int)g["flags"] == 1 + 16 + 64 + 128
                    && (int)g["xpPct"] == 41 && (bool)g["rested"] && (int)g["groupSize"] == 5 && (bool)g["inInstance"] && (string)g["visibility"] == "visible");
                s.ShareZoneOff = true;
                g = Session.BuildGameMessage();
                Check("Share my zone off: no zone and no instance name sent (flags still are)", !g.ContainsKey("zone") && !g.ContainsKey("instanceName") && g.ContainsKey("flags") && g.ContainsKey("xpPct"));
                s.ShareXpOff = true;
                g = Session.BuildGameMessage();
                Check("Share XP off: no xpPct and no rested sent", !g.ContainsKey("xpPct") && !g.ContainsKey("rested") && g.ContainsKey("groupSize"));
                s.ShareZoneOff = false; s.ShareXpOff = false;
                c.CombatKnown = false;
                g = Session.BuildGameMessage();
                Check("combat flag unknown (secret): the combat bit is not sent", ((int)g["flags"] & 1) == 0);
                c.CombatKnown = true;
                s.ShareGameOff = true;
                g = Session.BuildGameMessage();
                Check("sharing off: only share:false", g.ContainsKey("share") && !g.ContainsKey("zone"));
                s.ShareGameOff = false;

                // ---- Appear offline
                var errors = new List<string>();
                Action<string> onErr = t => errors.Add(t);
                Session.Error += onErr;
                Session.SetInvisible(true);
                g = Session.BuildGameMessage();
                Check("appear offline: my own entry shows the hollow dot, and 'Invisible' in my row", Session.Me.Invisible && (string)g["visibility"] == "invisible");
                Check("... joining a voice room is warned about on every room", Session.VoiceRooms.All(r => r.InvisibleHintVisibility == Visibility.Visible));
                await Session.SendMessage("first message while invisible");
                await Session.SendMessage("second message");
                Check("... the 'others can see your messages' note shows once", errors.Count(e => e.Contains("Others can see messages")) == 1);
                await Task.Delay(500);
                LodgePage.UpdateMeForTest();
                Snapshot(System.IO.Path.Combine(dir, "12-invisible.png"));
                LodgePage.OpenStatusForTest();
                await Task.Delay(400);
                SnapshotElement(LodgePage.StatusBoxForTest, System.IO.Path.Combine(dir, "12-status-menu.png"));
                LodgePage.CloseStatusForTest();
                var meCard = LodgePage.ShowCardForTest(Session.Me, LodgePage.SidebarForTest);
                await Task.Delay(250);
                SnapshotElement(meCard, System.IO.Path.Combine(dir, "12-card-me-invisible.png"));
                meCard.IsOpen = false;
                Session.FeedForTest("{\"t\":\"user\",\"user\":{\"id\":1,\"name\":\"Elan\",\"role\":\"owner\",\"invisible\":true}}");
                Check("the server's echo keeps me invisible", Session.Me.Invisible);
                Session.SetInvisible(false);
                Check("... and back to visible", !Session.Me.Invisible && Session.VoiceRooms.All(r => r.InvisibleHintVisibility == Visibility.Collapsed));
                var url = new LodgeClient("https://x.invalid/lodge", "synthetic", "n") { Invisible = () => true };
                Check("client asks for &vis=invisible when connecting invisible", (typeof(LodgeClient).GetMethod("WsUrl", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(url, null) as string).EndsWith("&vis=invisible"));
                Session.Error -= onErr;

                // ---- automatic status
                var a = new AutoStatus();
                var t0 = new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
                a.Evaluate(t0, true, false, false, false, "online", "");
                Check("auto: quiet -> nothing to send", !a.TrySend(t0, out _, out _));
                a.Evaluate(t0.AddSeconds(10), true, true, false, false, "online", "");
                string st, note;
                Check("auto: combat -> Busy 'In combat'", a.TrySend(t0.AddSeconds(10), out st, out note) && st == "busy" && note == "In combat" && a.IsAuto);
                a.Evaluate(t0.AddSeconds(14), true, false, false, false, "online", "");
                a.Evaluate(t0.AddSeconds(17), true, false, false, false, "online", "");
                Check("auto: still 'In combat' 3 s after the fight ended", a.Status == "busy" && !a.TrySend(t0.AddSeconds(17), out _, out _));
                a.Evaluate(t0.AddSeconds(19.5), true, false, false, false, "online", "");
                Check("auto: restored to Online ~5 s after combat, sent once", a.Status == "online" && a.TrySend(t0.AddSeconds(19.5), out st, out note) && st == "online" && note == "");
                a.Evaluate(t0.AddSeconds(40), true, true, false, false, "busy", "raiding");
                Check("auto: a chosen status (Busy + text) is never overridden by combat", a.Status == "busy" && a.Note == "raiding" && !a.IsAuto);
                a.Evaluate(t0.AddSeconds(41), true, true, false, false, "dungeon", "");
                Check("auto: ...nor a chosen 'In a dungeon'", a.Status == "dungeon" && !a.IsAuto);
                a.Evaluate(t0.AddSeconds(42), true, false, false, false, "online", "brb");
                Check("auto: ...nor a custom text on Online", a.Status == "online" && a.Note == "brb" && !a.IsAuto);
                var b = new AutoStatus();
                b.Evaluate(t0, true, false, true, false, "online", "");
                Check("auto: the game's AFK flag -> AFK", b.Status == "away" && b.TrySend(t0, out st, out note) && st == "away");
                b.Evaluate(t0.AddSeconds(3), true, true, true, false, "online", "");
                Check("auto: combat wins over AFK", b.Status == "busy");
                var d = new AutoStatus();
                d.Evaluate(t0, false, true, true, false, "online", "");
                Check("auto: switched off -> never overrides", d.Status == "online" && !d.TrySend(t0, out _, out _));
                var f = new AutoStatus();
                int sent = 0;
                for (int i = 0; i < 300; i++)   // 30 s of combat flapping every 300 ms, evaluated every 100 ms
                {
                    var t = t0.AddMilliseconds(i * 100);
                    f.Evaluate(t, true, (i / 3) % 2 == 0, false, false, "online", "");
                    if (f.TrySend(t, out _, out _)) sent++;
                }
                Check("auto: flapping combat sends at most one update per 2 s (" + sent + " in 30 s)", sent <= 15 && sent >= 1);
                // through the session: a character in combat shows as an automatic Busy
                Session.Presence.SetForTest(new GamePresence.Character { Name = "Elan", ClassFile = "HUNTER", Level = 60, HasLive = true, CombatKnown = true, Flags = 1 }, true);
                Check("session: in combat -> Busy 'In combat' (automatic)", Session.MyStatus == "busy" && Session.MyNote == "In combat" && Session.MyStatusIsAuto);
                Session.Clock = () => DateTime.UtcNow.AddSeconds(30);
                Session.Presence.SetForTest(new GamePresence.Character { Name = "Elan", ClassFile = "HUNTER", Level = 60, HasLive = true, CombatKnown = true, Flags = 4 }, true);
                var lingering = Session.MyStatus;                       // combat just ended: the 5 s linger starts
                Check("session: Busy lingers right after combat", lingering == "busy");
                Session.Clock = () => DateTime.UtcNow.AddSeconds(40);   // ... and has run out
                Check("session: AFK flag -> AFK", Session.MyStatus == "away");
                Session.Presence.SetForTest(null, false);
                Session.Clock = () => DateTime.UtcNow;
            }
            catch (Exception e) { ok = false; notes.Add("crashed: " + e); }
            finally
            {
                s.LodgeInvisible = hadInvisible; s.InvisibleHintShown = hadHint; s.ShareZoneOff = hadZoneOff; s.ShareXpOff = hadXpOff; s.AutoStatusOff = hadAutoOff;
                SettingsStore.Save(s);
            }
            return "presence selftest: " + (ok ? "PASSED" : "FAILED") + "\r\n  " + string.Join("\r\n  ", notes);
        }
    }
}
