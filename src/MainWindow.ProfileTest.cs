using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // --selftest: avatars and profiles (Lodge server 2.6), rendered and checked from synthetic protocol frames (no server)
    public partial class MainWindow
    {
        static Dictionary<string, object> Game(string name, string cls, string file, string race, string raceFile, int level, string zone, int flags = 0, int xp = -1, int group = 0)
        {
            var g = new Dictionary<string, object>
            {
                ["playing"] = true, ["name"] = name, ["class"] = cls, ["classFile"] = file, ["race"] = race, ["raceFile"] = raceFile, ["level"] = level, ["zone"] = zone,
                ["flags"] = flags, ["rested"] = false, ["groupSize"] = group, ["inInstance"] = false, ["sex"] = 2,
            };
            if (xp >= 0) g["xpPct"] = xp;
            return g;
        }

        static Dictionary<string, object> User(int id, string name, string role, string avatar, string accent, Dictionary<string, object> game = null, string room = null, string status = null, string note = null)
        {
            var u = new Dictionary<string, object> { ["id"] = id, ["name"] = name, ["role"] = role };
            if (avatar != null) u["avatarId"] = avatar;
            if (accent != null) u["accent"] = accent;
            if (game != null) u["game"] = game;
            if (room != null) { u["room"] = room; u["voice"] = true; }
            if (status != null) u["status"] = status;
            if (note != null) u["note"] = note;
            return u;
        }

        static Dictionary<string, object> Char(string name, string cls, string file, string race, int level, bool main = false, bool hidden = false, bool self = false)
        {
            var c = new Dictionary<string, object> { ["name"] = name, ["class"] = cls, ["classFile"] = file, ["race"] = race, ["raceFile"] = race.Replace(" ", ""), ["level"] = level, ["seen"] = 1790000000, ["main"] = main };
            if (self) c["hidden"] = hidden;
            return c;
        }

        // a card or popup content on a dark backdrop (what you see over the game for the overlay)
        void SnapshotOn(FrameworkElement el, string file, Color back)
        {
            el.UpdateLayout();
            int w = Math.Max(1, (int)Math.Ceiling(el.ActualWidth)), h = Math.Max(1, (int)Math.Ceiling(el.ActualHeight));
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(w * 2, h * 2, 192, 192, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(back), null, new Rect(0, 0, w, h));
                dc.DrawRectangle(new VisualBrush(el), null, new Rect(0, 0, w, h));
            }
            bmp.Render(dv);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using (var fs = System.IO.File.Create(file)) enc.Save(fs);
        }

        async Task<string> ProfileTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            void Check(string what, bool cond) { notes.Add((cond ? "ok   " : "FAIL ") + what); ok &= cond; }
            string P(string name) => System.IO.Path.Combine(dir, name);
            try
            {
                Session.Disconnect();
                TabLodge.IsChecked = true;
                LodgePage.ShowChatForTest();

                // ---- the avatar sheet: every preset at 96 / 48 / 34 / 22 px and in all accent colours
                IconSheet.AvatarSheet(P("14-avatars.png"));
                Check("24 preset avatars, all drawn", AvatarCatalog.Ids.Length == 24 && AvatarCatalog.Ids.All(id => AvatarCatalog.Image(id, null) != null && AvatarCatalog.Image(id, "gold") != null));
                Check("8 accent colours", AvatarCatalog.AccentIds.Length == 8 && AvatarCatalog.AccentIds.All(AvatarCatalog.IsAccent));
                Check("unknown ids are refused", AvatarCatalog.Image("av.dragon", null) == null && !AvatarCatalog.IsAvatar("../x") && !AvatarCatalog.IsAccent("#ff0000"));

                // ---- a lodge with avatars: wolf+badge (in game), no badge when not playing, default look without an avatar
                var users = new List<object>
                {
                    User(1, "Elan", "owner", "av.wolf", "gold", Game("Elan", "Hunter", "HUNTER", "Orc", "Orc", 60, "The Barrens", 0, 62)),
                    User(2, "Bob", "officer", "av.shield", "azure", Game("Measley", "Paladin", "PALADIN", "Human", "Human", 25, "Wailing Caverns", 16 + 64 + 128, -1, 5), "hangout", "busy", "pulling the next pack"),
                    User(3, "Tess", "member", "av.staff", "violet", Game("Tess", "Mage", "MAGE", "Gnome", "Gnome", 33, "Elwynn Forest", 1 + 128, 20, 3)),
                    User(4, "Dax", "member", "av.fish", "teal"),
                    User(5, "Kor", "veteran", "av.axe", "crimson", Game("Kor", "Warrior", "WARRIOR", "Tauren", "Tauren", 41, "The Hinterlands", 2, 5)),
                    User(6, "Zed", "member", "av.moon", "slate", null, "hangout"),
                    User(7, "Mira", "member", null, null, Game("Mira", "Rogue", "ROGUE", "Night Elf", "NightElf", 48, "Stormwind City", 8, 71)),
                    User(8, "Pip", "member", "av.chicken", "rose", null, null, "away"),
                    User(9, "Ghost", "member", "av.skull", null),
                };
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                Func<string, int, string, string, Dictionary<string, object>> Msg = (id, fromId, from, text) => new Dictionary<string, object>
                    { ["t"] = "msg", ["channel"] = "general", ["id"] = id, ["at"] = (double)(now - 600000 + int.Parse(id.Substring(1)) * 20000), ["from"] = from, ["fromId"] = fromId, ["text"] = text };
                var history = new List<object>
                {
                    Msg("m1", 2, "Bob", "Wailing Caverns in ten minutes, who is in?"),
                    Msg("m2", 3, "Tess", "On my way, finishing a quest in Elwynn"),
                    Msg("m3", 4, "Dax", "I am not in game right now, but count me in for later"),
                    Msg("m4", 7, "Mira", "No avatar picked yet, so the default look is shown"),
                    Msg("m5", 9, "Ghost", "Left the lodge, but my picture stays in the history"),
                    Msg("m6", 8, "Pip", "cluck"),
                };
                var own = new Dictionary<string, object>
                {
                    ["name"] = "Elan", ["displayName"] = "", ["found"] = true, ["guest"] = false, ["role"] = "owner", ["online"] = true, ["restricted"] = false, ["avatarId"] = "av.wolf", ["accent"] = "gold",
                    ["about"] = "Hunter main since 2005. Pet wrangler, bad at fishing, good at snacks.", ["playTimes"] = "evenings CET, weekends", ["main"] = "Elan",
                    ["showChars"] = "all", ["visibleTo"] = "everyone",
                    ["chars"] = new List<object> { Char("Elan", "Hunter", "HUNTER", "Orc", 60, true, false, true), Char("Elanmage", "Mage", "MAGE", "Gnome", 33, false, false, true), Char("Elanpriest", "Priest", "PRIEST", "Dwarf", 12, false, true, true) },
                };
                var welcome = new Dictionary<string, object>
                {
                    ["t"] = "welcome", ["you"] = 1, ["name"] = "Elan", ["role"] = "owner", ["server"] = "2.7.0", ["maxFileMb"] = 25, ["canShareFiles"] = true, ["canModerate"] = true, ["canManage"] = true, ["canPin"] = true,
                    ["features"] = new List<object> { "reply", "react", "react2", "pin", "profile", "displayname" },
                    ["reactions"] = new List<object> { "ready", "notready", "lol", "love", "fight", "loot", "wipe", "epic" },
                    ["channels"] = new List<object>
                    {
                        new Dictionary<string, object> { ["id"] = "general", ["name"] = "General", ["type"] = "text", ["minRole"] = "guest" },
                        new Dictionary<string, object> { ["id"] = "hangout", ["name"] = "Hangout", ["type"] = "voice", ["minRole"] = "guest" },
                    },
                    ["users"] = users, ["history"] = new Dictionary<string, object> { ["general"] = history }, ["pins"] = new Dictionary<string, object>(), ["profile"] = own,
                };
                Session.FeedForTest(Json.Write(welcome));
                Session.FeedForTest("{\"t\":\"leave\",\"id\":9}");   // Ghost goes: their picture stays on their old message
                await Task.Delay(500);
                var by = Session.Members.ToDictionary(m => m.Name);
                var msgs = Session.MessagesOf("general");

                Check("welcome carries each member's avatar and accent", by["Bob"].AvatarId == "av.shield" && by["Bob"].Accent == "azure" && by["Dax"].AvatarId == "av.fish" && by["Mira"].AvatarId == null);
                Check("my own profile came with the welcome", Session.MyProfile != null && Session.MyProfile.About.StartsWith("Hunter main") && Session.MyProfile.Chars.Count == 3 && Session.SupportsProfile);
                Check("avatar + in game -> big picture and a class badge (bottom-right)", by["Bob"].PersonalVisibility == Visibility.Visible && by["Bob"].DefaultVisibility == Visibility.Collapsed && by["Bob"].BadgeVisibility == Visibility.Visible);
                Check("avatar, not in game -> picture, NO badge", by["Dax"].PersonalVisibility == Visibility.Visible && by["Dax"].BadgeVisibility == Visibility.Collapsed && by["Pip"].BadgeVisibility == Visibility.Collapsed);
                Check("no avatar chosen -> today's class-colour look, no badge", by["Mira"].PersonalVisibility == Visibility.Collapsed && by["Mira"].DefaultVisibility == Visibility.Visible && by["Mira"].BadgeVisibility == Visibility.Collapsed);
                Check("badge ring is the class colour", by["Bob"].BadgeRing is SolidColorBrush rb && rb.Color == (Color)ColorConverter.ConvertFromString("#F58CBA"));
                Check("chat rows: author's picture and badge", msgs.First(m => m.Id == "m1").AvatarImage != null && msgs.First(m => m.Id == "m1").BadgeVisibility == Visibility.Visible
                    && msgs.First(m => m.Id == "m3").AvatarImage != null && msgs.First(m => m.Id == "m3").BadgeVisibility == Visibility.Collapsed
                    && msgs.First(m => m.Id == "m4").AvatarImage == null);
                Check("someone who left keeps their picture in the history", msgs.First(m => m.Id == "m5").AvatarImage != null && msgs.First(m => m.Id == "m5").BadgeVisibility == Visibility.Collapsed);

                Snapshot(P("14-lodge.png"));
                SnapshotElement(LodgePage.SidebarForTest, P("14-sidebar.png"));

                // ---- live updates: a profile broadcast and presence changes refresh the lists and the chat rows
                Session.FeedForTest("{\"t\":\"profile\",\"id\":3,\"name\":\"Tess\",\"avatarId\":\"av.owl\",\"accent\":\"emerald\"}");
                Check("profile broadcast: member and chat rows update live", by["Tess"].AvatarId == "av.owl" && by["Tess"].Accent == "emerald" && msgs.First(m => m.Id == "m2").AvatarImage != null);
                Session.FeedForTest("{\"t\":\"profile\",\"id\":7,\"name\":\"Mira\",\"avatarId\":\"av.raptor\",\"accent\":\"azure\"}");
                Check("... from the default look to a picture (class badge appears at once, she is in game)", by["Mira"].HasAvatar && by["Mira"].BadgeVisibility == Visibility.Visible && msgs.First(m => m.Id == "m4").PersonalVisibility == Visibility.Visible
                    && msgs.First(m => m.Id == "m4").BadgeVisibility == Visibility.Visible);
                Session.FeedForTest(Json.Write(new Dictionary<string, object> { ["t"] = "user", ["user"] = User(4, "Dax", "member", "av.fish", "teal", Game("Dax", "Druid", "DRUID", "Tauren", "Tauren", 30, "Mulgore"))}));
                Check("going into the game adds the badge (list and chat), logging out removes it", by["Dax"].BadgeVisibility == Visibility.Visible && msgs.First(m => m.Id == "m3").BadgeVisibility == Visibility.Visible);
                Session.FeedForTest(Json.Write(new Dictionary<string, object> { ["t"] = "user", ["user"] = User(4, "Dax", "member", "av.fish", "teal") }));
                Check("... and gone again", by["Dax"].BadgeVisibility == Visibility.Collapsed && msgs.First(m => m.Id == "m3").BadgeVisibility == Visibility.Collapsed);
                Session.FeedForTest("{\"t\":\"profile\",\"id\":3,\"name\":\"Tess\",\"avatarId\":null,\"accent\":null}");
                Check("an owner reset (avatar null) returns to the default look", !by["Tess"].HasAvatar && by["Tess"].DefaultVisibility == Visibility.Visible);
                Session.FeedForTest("{\"t\":\"profile\",\"id\":3,\"name\":\"Tess\",\"avatarId\":\"av.staff\",\"accent\":\"violet\"}");
                // an old server (2.5): user entries without any avatar fields
                Session.FeedForTest("{\"t\":\"join\",\"user\":{\"id\":20,\"name\":\"Old\",\"role\":\"member\",\"game\":{\"playing\":true,\"name\":\"Old\",\"class\":\"Druid\",\"classFile\":\"DRUID\",\"level\":12,\"zone\":\"Teldrassil\"}}}");
                var old = Session.Members.First(m => m.Name == "Old");
                Check("2.5 server entry (no avatar fields): default look, no badge, nothing breaks", !old.HasAvatar && old.BadgeVisibility == Visibility.Collapsed && old.DefaultVisibility == Visibility.Visible);
                Session.FeedForTest("{\"t\":\"leave\",\"id\":20}");
                Session.FeedForTest("{\"t\":\"profile\",\"id\":999,\"name\":\"Nobody\",\"avatarId\":\"av.wolf\"}");
                Check("a broadcast about an unknown member is ignored", Session.Members.All(m => m.Name != "Nobody"));
                Session.FeedForTest("{\"t\":\"profile\",\"id\":2,\"name\":\"Bob\",\"avatarId\":\"av.dragon\",\"accent\":\"#00ff00\"}");
                Check("a non-whitelisted id from a server is not drawn", !by["Bob"].HasAvatar && by["Bob"].AvatarImage == null);
                Session.FeedForTest("{\"t\":\"profile\",\"id\":2,\"name\":\"Bob\",\"avatarId\":\"av.shield\",\"accent\":\"azure\"}");
                await Task.Delay(300);

                // ---- hover card with the avatar and badge
                foreach (var who in new[] { "Bob", "Dax", "Mira" })
                {
                    var tip = LodgePage.ShowCardForTest(by[who], LodgePage.SidebarForTest);
                    await Task.Delay(250);
                    SnapshotElement(tip, P("14-hovercard-" + who.ToLowerInvariant() + ".png"));
                    tip.IsOpen = false;
                }

                // ---- the profile card (click a person): online with a server profile, offline, restricted
                LodgePage.ShowProfileCard(by["Bob"], LodgePage.SidebarForTest);
                await Task.Delay(300);
                var card = LodgePage.PopCardForTest;
                Check("card opens at once with what we know, and asks the server for the rest", card != null && card.Member == by["Bob"] && card.Loading && card.NoteText == "Loading profile...");
                Session.FeedForTest(Json.Write(new Dictionary<string, object>
                {
                    ["t"] = "profile:data",
                    ["profile"] = new Dictionary<string, object>
                    {
                        ["name"] = "Bob", ["found"] = true, ["guest"] = false, ["role"] = "officer", ["online"] = true, ["restricted"] = false, ["avatarId"] = "av.shield", ["accent"] = "azure",
                        ["about"] = "Paladin tank, officer of the lodge. Ask me about dungeon runs - I will pull too much.", ["playTimes"] = "weekday evenings CET",
                        ["main"] = "Measley", ["game"] = Game("Measley", "Paladin", "PALADIN", "Human", "Human", 25, "Wailing Caverns"),
                        ["chars"] = new List<object> { Char("Measley", "Paladin", "PALADIN", "Human", 25, true), Char("Bobhunter", "Hunter", "HUNTER", "Dwarf", 40), Char("Bobwarlock", "Warlock", "WARLOCK", "Undead", 18) },
                    },
                }));
                await Task.Delay(300);
                Check("server profile fills the card: about, play times, characters (main starred), now playing", card.About.StartsWith("Paladin tank") && card.PlayTimes.Contains("evenings") && card.Chars.Count == 3
                    && card.Chars[0].IsMain && card.NowVisibility == Visibility.Visible && card.NowLine.StartsWith("Wailing Caverns") && card.NoteVisibility == Visibility.Collapsed);
                SnapshotElement(LodgePage.PopupCardForTest, P("14-card-bob.png"));
                LodgePage.CloseProfileForTest();

                LodgePage.ShowProfileCardByNameForTest("Ghost", LodgePage.SidebarForTest);
                await Task.Delay(300);
                var gcard = LodgePage.PopCardForTest;
                Session.FeedForTest("{\"t\":\"profile:data\",\"profile\":{\"name\":\"Ghost\",\"found\":true,\"guest\":false,\"role\":\"veteran\",\"online\":false,\"restricted\":false,\"avatarId\":\"av.skull\",\"accent\":\"crimson\",\"about\":\"Back in a while.\",\"playTimes\":\"nights\",\"chars\":[{\"name\":\"Ghostie\",\"class\":\"Rogue\",\"classFile\":\"ROGUE\",\"race\":\"Human\",\"level\":60,\"seen\":1790000000,\"main\":true}],\"main\":\"Ghostie\"}}");
                await Task.Delay(300);
                Check("offline member: card from the server's data, no badge, no now playing, offline text", gcard.Member.Offline && gcard.Member.AvatarId == "av.skull" && gcard.Member.RoleLabel == "Veteran" && gcard.NowVisibility == Visibility.Collapsed
                    && gcard.OnlineText == "Offline" && gcard.Member.BadgeVisibility == Visibility.Collapsed && gcard.Chars.Count == 1);
                SnapshotElement(LodgePage.PopupCardForTest, P("14-card-offline.png"));
                LodgePage.CloseProfileForTest();

                LodgePage.ShowProfileCardByNameForTest("Kor", LodgePage.SidebarForTest);
                await Task.Delay(200);
                Session.FeedForTest("{\"t\":\"profile:data\",\"profile\":{\"name\":\"Kor\",\"found\":true,\"role\":\"veteran\",\"online\":true,\"restricted\":true,\"avatarId\":\"av.axe\",\"accent\":\"crimson\"}}");
                await Task.Delay(200);
                Check("profile kept to officers: note shown, no details", LodgePage.PopCardForTest.NoteText.Contains("officers") && LodgePage.PopCardForTest.About == "");
                SnapshotElement(LodgePage.PopupCardForTest, P("14-card-restricted.png"));
                LodgePage.CloseProfileForTest();

                // ---- Settings > Profile (the edit page; every "Edit profile" entry point opens it)
                bool wentToProfile = false;
                LodgePage.EditProfileRequested += () => wentToProfile = true;
                LodgePage.OpenProfileEditor();
                Check("every Edit profile entry point asks for Settings > Profile (no separate window any more)", wentToProfile);
                ShowTab("settings");
                SettingsPage.Show("profile");
                await Task.Delay(500);
                var vm = SettingsPage.ProfileModel;
                Check("Settings has a Profile entry; the page is the edit form", vm != null && SettingsPage.ProfileSectionForTest.Visibility == Visibility.Visible);
                Check("editor: 24 pictures + the default tile, 8 swatches + none, current choices selected", vm.Avatars.Count == 25 && vm.Swatches.Count == 9 && vm.Avatars.Single(t => t.Selected).Id == "av.wolf" && vm.Swatches.Single(t => t.Selected).Id == "gold");
                Check("editor: preview card shows what others see (hidden character left out)", vm.Card.Chars.Count == 2 && vm.Card.About.StartsWith("Hunter main"));
                Snapshot(P("14-edit-profile.png"));
                vm.SelectAvatar("av.owl"); vm.SelectAccent("violet"); vm.About = "Now an owl person.\nWith two lines"; vm.PlayTimes = "nights";
                await Task.Delay(200);
                Check("editor: choosing updates the preview live; about is one line, counter shown", vm.Preview.AvatarId == "av.owl" && vm.Card.Member.Accent == "violet" && !vm.About.Contains("\n") && vm.AboutCounter == vm.About.Length + " / 140");
                vm.About = new string('x', 200);
                Check("editor: about is capped at 140 characters", vm.About.Length == 140 && vm.AboutCounter == "140 / 140");
                vm.About = "Now an owl person. Ask me about hunters.";
                vm.ShowMain = true; vm.MainChoice = "Elanmage"; vm.VisOfficers = true;
                var elanPriest = vm.Chars.First(c => c.Name == "Elanpriest"); elanPriest.Hidden = true; vm.HideChanged(elanPriest);
                Check("editor: main only -> the preview lists just the main character", vm.Card.Chars.Count == 1 && vm.Card.Chars[0].Name == "Elanmage" && vm.Hint.Contains("officers"));
                var msg = vm.ToMessage();
                Check("editor: the message carries whitelisted ids, the choices and the hidden list (never the main)", (string)msg["avatarId"] == "av.owl" && (string)msg["accent"] == "violet" && (string)msg["showChars"] == "main"
                    && (string)msg["visibleTo"] == "officers" && (string)msg["main"] == "Elanmage" && ((List<string>)msg["hidden"]).SequenceEqual(new[] { "Elanpriest" }) && ((string)msg["about"]).Length <= 140);
                vm.ShowAll = true; vm.VisEveryone = true; vm.SelectAvatar("av.wolf"); vm.SelectAccent("gold"); vm.MainChoice = "Elan"; elanPriest.Hidden = true; vm.HideChanged(elanPriest);
                vm.MainChoice = "Elanpriest";
                Check("editor: hiding the main character is not possible (choosing it as main unhides it)", !elanPriest.Hidden && elanPriest.IsMain);
                vm.MainChoice = "Elan"; vm.Refresh();

                // display name: live validation, live preview, the server's "taken" answer
                Check("display name box is there (server 2.7), empty to start with", vm.SupportsDisplayName && vm.DisplayName == "" && vm.DisplayHint.Contains("@Elan"));
                vm.DisplayName = "H";
                Check("... too short: hint says so, Save is off", vm.DisplayHint.Contains("At least 2") && !vm.CanSave);
                vm.DisplayName = "Hunter <Elan>";
                Check("... odd characters: hint says so, Save is off", vm.DisplayHint.Contains("Only letters") && !vm.CanSave);
                vm.DisplayName = "Hunter  Elan";
                Check("... valid: preview card shows the display name with the login name next to it, Save is on", vm.Card.Name == "Hunter Elan" && vm.Card.LoginText == "  ·  @Elan" && vm.CanSave
                    && (string)vm.ToMessage()["displayName"] == "Hunter Elan");
                vm.BeginSave();
                vm.ServerError("That display name is taken");
                Check("... the server says taken: shown under the box, nothing saved", vm.DisplayHint == "That display name is taken" && vm.StatusText == "Not saved." && vm.CanSave);
                SettingsPage.ScrollForTest(430);
                await Task.Delay(300);
                Snapshot(P("15-settings-profile-taken.png"));
                vm.DisplayName = "Hunter Elan 2"; vm.DisplayName = "Hunter Elan";
                SettingsPage.ScrollForTest(2000);
                await Task.Delay(300);
                Snapshot(P("14-edit-profile-bottom.png"));
                SettingsPage.ScrollForTest(0);
                vm.DisplayName = "Hunter Elan";
                await Task.Delay(300);
                Snapshot(P("15-settings-profile-form.png"));
                // a "profile" answer from the server with the saved name
                vm.BeginSave();
                Session.FeedForTest("{\"t\":\"profile:data\",\"profile\":{\"name\":\"Elan\",\"found\":true,\"guest\":false,\"role\":\"owner\",\"online\":true,\"restricted\":false,\"avatarId\":\"av.wolf\",\"accent\":\"gold\",\"displayName\":\"Hunter Elan\",\"about\":\"x\",\"playTimes\":\"\",\"showChars\":\"all\",\"visibleTo\":\"everyone\",\"chars\":[]}}");
                Session.FeedForTest("{\"t\":\"profile\",\"id\":1,\"name\":\"Elan\",\"avatarId\":\"av.wolf\",\"accent\":\"gold\",\"displayName\":\"Hunter Elan\"}");
                await Task.Delay(200);
                Check("saved: my own name is shown as the display name everywhere (member, status line), page says Saved", by["Elan"].Display == "Hunter Elan" && Session.MyDisplayName == "Hunter Elan" && vm.StatusText.StartsWith("Saved") && SettingsPage.ProfileModel == vm);

                Snapshot(P("16-profile-saved.png"));
                vm.About = "edited again"; await Task.Delay(200);
                Snapshot(P("16-profile-dirty.png"));
                // the Lodge sub-entry in Settings is gone
                SettingsPage.Show("lodge");
                await Task.Delay(300);
                Snapshot(P("14-settings-profile.png"));
                ShowTab("lodge");

                // ---- display names across the lodge (server 2.7): member list, chat incl. old messages, replies, reactions, typing, mentions, hover card
                Session.FeedForTest("{\"t\":\"profile\",\"id\":2,\"name\":\"Bob\",\"avatarId\":\"av.shield\",\"accent\":\"azure\",\"displayName\":\"Measley the Bold\"}");
                Session.FeedForTest("{\"t\":\"profile\",\"id\":3,\"name\":\"Tess\",\"avatarId\":\"av.staff\",\"accent\":\"violet\",\"displayName\":\"Tessa Frostweaver\"}");
                var m7 = new Dictionary<string, object> { ["t"] = "msg", ["channel"] = "general", ["id"] = "m7", ["at"] = (double)(now - 120000), ["from"] = "Dax", ["fromId"] = 4, ["text"] = "Agreed!",
                    ["replyTo"] = new Dictionary<string, object> { ["id"] = "m1", ["by"] = "Bob", ["snippet"] = "Wailing Caverns in ten minutes, who is in?" } };
                Session.FeedForTest(Json.Write(m7));
                var m8 = new Dictionary<string, object> { ["t"] = "msg", ["channel"] = "general", ["id"] = "m8", ["at"] = (double)(now - 60000), ["from"] = "Bob", ["fromId"] = 2, ["text"] = "@Hunter Elan we need a hunter for the Caverns" };
                Session.FeedForTest(Json.Write(m8));
                var m9 = new Dictionary<string, object> { ["t"] = "msg", ["channel"] = "general", ["id"] = "m9", ["at"] = (double)(now - 30000), ["from"] = "Tess", ["fromId"] = 3, ["text"] = "@elan or just @Elan works too" };
                Session.FeedForTest(Json.Write(m9));
                Session.FeedForTest("{\"t\":\"react\",\"channel\":\"general\",\"id\":\"m1\",\"reaction\":\"ready\",\"users\":[\"Tess\",\"Dax\"],\"by\":\"Tess\",\"on\":true}");
                Session.FeedForTest("{\"t\":\"typing\",\"channel\":\"general\",\"id\":3}");
                await Task.Delay(300);
                Check("member list shows display names; the login name is still the identity", by["Bob"].Display == "Measley the Bold" && by["Bob"].Name == "Bob" && by["Tess"].Display == "Tessa Frostweaver" && by["Dax"].Display == "Dax");
                Check("a message from before the rename shows the new name (history, reply quote)", msgs.First(m => m.Id == "m1").FromShown == "Measley the Bold" && msgs.First(m => m.Id == "m1").From == "Bob"
                    && msgs.First(m => m.Id == "m2").FromShown == "Tessa Frostweaver" && msgs.First(m => m.Id == "m7").ReplyShown == "Measley the Bold" && msgs.First(m => m.Id == "m7").ReplyFrom == "Bob");
                Check("reaction tooltip names the people by display name", msgs.First(m => m.Id == "m1").Reactions.Any(r => r.Tip.Contains("Tessa Frostweaver") && r.Tip.Contains("Dax")));
                Check("typing indicator uses the display name", Session.TypingText("general") == "Tessa Frostweaver is typing...");
                Check("mentions: @DisplayName and @LoginName (any case) both mention me", msgs.First(m => m.Id == "m8").Mentioned && msgs.First(m => m.Id == "m9").Mentioned && !msgs.First(m => m.Id == "m7").Mentioned);
                Check("hover card text: Display Name, then the dim login name", by["Bob"].CardNameText == "Measley the Bold" && by["Bob"].LoginText == "  ·  @Bob" && by["Dax"].LoginText == "" && by["Elan"].CardNameText == "Hunter Elan (you)");
                Check("someone who left keeps their display name in old messages", msgs.First(m => m.Id == "m5").FromShown == "Ghost");
                Session.FeedForTest("{\"t\":\"profile\",\"id\":3,\"name\":\"Tess\",\"avatarId\":\"av.staff\",\"accent\":\"violet\",\"displayName\":\"Tessa Frostweaver\"}");
                Session.FeedForTest("{\"t\":\"leave\",\"id\":3}");
                Check("... and so does a display name after the person left (known-names cache)", msgs.First(m => m.Id == "m2").FromShown == "Tessa Frostweaver");
                Session.FeedForTest(Json.Write(new Dictionary<string, object> { ["t"] = "join", ["user"] = User(3, "Tess", "member", "av.staff", "violet", Game("Tess", "Mage", "MAGE", "Gnome", "Gnome", 33, "Elwynn Forest", 1 + 128, 20, 3)) }));
                by = Session.Members.ToDictionary(m => m.Name);
                Check("... a join entry without displayName keeps it gone (server said none), a 2.6 server entry shows the login name", by["Tess"].Display == "Tess");
                Session.FeedForTest("{\"t\":\"profile\",\"id\":3,\"name\":\"Tess\",\"avatarId\":\"av.staff\",\"accent\":\"violet\",\"displayName\":\"Tessa Frostweaver\"}");
                await Task.Delay(300);
                Snapshot(P("15-lodge-displaynames.png"));
                SnapshotElement(LodgePage.SidebarForTest, P("15-sidebar-displaynames.png"));
                foreach (var who in new[] { "Bob", "Dax" })
                {
                    var tip = LodgePage.ShowCardForTest(by[who], LodgePage.SidebarForTest);
                    await Task.Delay(250);
                    SnapshotElement(tip, P("15-hovercard-" + who.ToLowerInvariant() + ".png"));
                    tip.IsOpen = false;
                }
                LodgePage.ShowProfileCard(by["Bob"], LodgePage.SidebarForTest);
                await Task.Delay(300);
                Session.FeedForTest("{\"t\":\"profile:data\",\"profile\":{\"name\":\"Bob\",\"found\":true,\"guest\":false,\"role\":\"officer\",\"online\":true,\"restricted\":false,\"avatarId\":\"av.shield\",\"accent\":\"azure\",\"displayName\":\"Measley the Bold\",\"about\":\"Paladin tank.\",\"playTimes\":\"evenings\",\"chars\":[]}}");
                await Task.Delay(300);
                Check("profile card: Display Name with the dim @LoginName", LodgePage.PopCardForTest.Name == "Measley the Bold" && LodgePage.PopCardForTest.LoginText == "  ·  @Bob" && LodgePage.PopCardForTest.LoginVisibility == Visibility.Visible);
                SnapshotElement(LodgePage.PopupCardForTest, P("15-card-bob.png"));
                LodgePage.CloseProfileForTest();
                // an owner reset clears it
                Session.FeedForTest("{\"t\":\"profile\",\"id\":2,\"name\":\"Bob\",\"avatarId\":null,\"accent\":null,\"displayName\":\"\"}");
                Check("owner reset (displayName empty): everything shows the login name again, old messages too", by["Bob"].Display == "Bob" && msgs.First(m => m.Id == "m1").FromShown == "Bob");
                Session.FeedForTest("{\"t\":\"profile\",\"id\":2,\"name\":\"Bob\",\"avatarId\":\"av.shield\",\"accent\":\"azure\",\"displayName\":\"Measley the Bold\"}");

                // ---- toasts and overlay with avatars
                var s = settings;
                bool o1 = s.ToastsOff, o2 = s.ToastMentionsOff; s.ToastsOff = false; s.ToastMentionsOff = false;
                Session.IsShownToUser = () => false;
                Session.Presence.SetForTest(null, true);
                var T = Session.Toasts;
                T.Visible.Clear(); T.Clock = () => DateTime.UtcNow;
                T.Push(new ToastItem { Kind = ToastKind.Mention, Sender = DisplayNames.Shown("Bob"), Channel = "General", ChannelId = "g", Text = "@Elan the Wailing Caverns group needs a hunter, are you in?", Who = by["Bob"] });
                T.Push(new ToastItem { Kind = ToastKind.Reply, Sender = "Dax", Channel = "General", ChannelId = "g2", Text = "Count me in for later", Who = by["Dax"] });
                T.Push(new ToastItem { Kind = ToastKind.Mention, Sender = "Mira", Channel = "General", ChannelId = "g3", Text = "@Elan default look, class colour circle", Who = by["Mira"] });
                LodgePage.ForceToastsForTest = true;
                await Task.Delay(1200); T.Tick();
                LodgePage.SnapshotToasts(P("14-toasts.png"));
                LodgePage.ForceToastsForTest = false;
                T.Visible.Clear();
                s.ToastsOff = o1; s.ToastMentionsOff = o2;
                Session.Presence.SetForTest(null, false);
                foreach (var m in Session.Members.Where(m => m.Room != null || m.Name == "Elan" || m.Name == "Mira" || m.Name == "Kor")) { m.InMyRoom = true; if (m.Name == "Kor") m.Speaking = true; }
                var ow = new OverlayWindow(Session.Members);
                ow.Show();
                await Task.Delay(500);
                SnapshotOn((FrameworkElement)ow.Content, P("14-overlay.png"), Color.FromRgb(0x3A, 0x4A, 0x3A));
                ow.Close();
                foreach (var m in Session.Members) { m.InMyRoom = false; m.Speaking = false; }

                // ---- Settings > Profile when not connected: a short explanation instead of the form
                Session.Disconnect();
                ShowTab("settings");
                SettingsPage.Show("profile");
                await Task.Delay(500);
                Check("not connected: the Profile page shows an explanation, no form", SettingsPage.ProfileModel == null && SettingsPage.ProfileSectionForTest.Visibility == Visibility.Visible);
                Snapshot(P("15-settings-profile-empty.png"));
                ShowTab("lodge");
            }
            catch (Exception e) { ok = false; notes.Add("crashed: " + e); }
            DisplayNames.Forget();
            return "profile selftest: " + (ok ? "PASSED" : "FAILED") + "\r\n  " + string.Join("\r\n  ", notes);
        }
    }
}
