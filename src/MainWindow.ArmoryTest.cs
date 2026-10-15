using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // --selftest "armory": the Lodge Armory (server 2.16, Outpost 2.48.1). A fake Lodge: the frames the server sends are fed in, the frames the Outpost sends are collected.
    //   - sharing: OFF by default, one switch per character, the snapshot holds gear / talents / professions / pets and NOTHING of bags, bank, gold, zone or experience,
    //     the signature ignores the game's saved-at times, put / delete frames (only what changed, after a quiet moment, unticked = deleted, switch off = delete all)
    //   - the server's answers: list rows (junk skipped), one character, refusals (rate, full, guest), the upload record on disk
    //   - Settings > Lodge: the switch and the per-character checklist
    //   - the Characters page: the "My characters" | "Lodge" switch, the list with class / level / profession filters and the search, opening a member's character in the
    //     same Overview / Gear / Talents / Professions / Pet views (no bags, bank, gold, zone), offline / old server / guest / switched-off lines
    // Fixtures live under the scratch folder; the real WoW folder is never touched.
    public partial class MainWindow
    {
        string ArmoryWelcome(string role, string server, bool feature) =>
            FunWelcome(role, FunPeople(false), "", "\"reply\",\"react\",\"react2\",\"pin\",\"profile\",\"displayname\",\"lfg\",\"history2\",\"search\"" + (feature ? ",\"armory\"" : ""), server);

        // the list row the server would send for a snapshot
        static string ArmoryRowJson(string owner, string by, string key, Dictionary<string, object> snap, long up)
        {
            var o = new Dictionary<string, object> { ["owner"] = owner, ["by"] = by, ["key"] = key, ["up"] = up };
            foreach (var k in new[] { "name", "realm", "class", "classText", "race", "raceFile", "sex", "faction", "guild", "level", "ilvl", "at" }) if (snap.ContainsKey(k)) o[k] = snap[k];
            var profs = new List<object>();
            var pro = snap.Child("professions");
            if (pro != null)
                foreach (var p in pro.List("list").OfType<Dictionary<string, object>>())
                    profs.Add(new Dictionary<string, object> { ["name"] = p.Str("name"), ["skill"] = (double)p.Int("skill"), ["max"] = (double)p.Int("max") });
            o["professions"] = profs;
            return Json.Write(o);
        }

        static List<Dictionary<string, object>> AryFrames(List<Dictionary<string, object>> sent, string t) => sent.Where(o => o.Str("t") == t).ToList();

        async Task<string> ArmoryTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            void Check(string what, bool cond, string detail = null) { SelfTestClock.Mark(what); notes.Add((cond ? "ok   " : "FAIL ") + what + (!cond && detail != null ? " [" + detail + "]" : "")); ok &= cond; }
            string P(string n) => Path.Combine(dir, n);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var root = Path.Combine(dir, "armorywow");
            var sent = new List<Dictionary<string, object>>();
            var clock = new DateTime(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
            var hadRoot = settings.WowRoot; var hadOn = settings.ArmoryShareOn; var hadChars = settings.ArmoryChars; var hadClock = Session.Clock;
            string sentFile = null;
            try
            {
                if (Directory.Exists(root)) Util.DeleteDir(root);
                HubData.ResetCache();
                Check("fixture: the WoW folder of this test is under the scratch folder (never the real game)", Path.GetFullPath(root).StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase));
                var sv = Path.Combine(root, "_classic_beta_", "WTF", "Account", "ACC1", "SavedVariables");
                Directory.CreateDirectory(sv);
                Directory.CreateDirectory(Path.Combine(root, "_classic_beta_", "Interface", "AddOns"));
                var hubFile = Path.Combine(sv, "ElansHub.lua");
                File.WriteAllText(hubFile, HubCharsLua(now, now - 60), new UTF8Encoding(false));
                void Touch(int secs) => File.SetLastWriteTimeUtc(hubFile, DateTime.UtcNow.AddSeconds(secs));
                Touch(1);

                Session.Disconnect();
                settings.WowRoot = root; settings.ArmoryShareOn = false; settings.ArmoryChars = null;
                Session.Clock = () => clock;
                Session.SentForTest = o => sent.Add(Json.Obj(Json.Write(o)));          // what the server would parse
                Session.ArmoryResetForTest();
                Session.ArmorySourceForTest = null;
                sentFile = Session.ArmorySentPathForTest;
                try { File.Delete(sentFile); } catch { }

                // ================================================================ what is shared, and what is not
                var fresh = new Settings();
                Check("sharing is OFF by default and no character is ticked", !fresh.ArmoryShareOn && (fresh.ArmoryChars == null || fresh.ArmoryChars.Count == 0));
                var local = ArmoryShare.Local(root);
                Check("the characters of this PC come from the Hub (6 here), with the legacy-only ones left out", local.Count == 6 && local.All(c => c.Hub != null) && local.Any(c => c.Key == "Forever-Elan"), string.Join(",", local.Select(c => c.Key)));
                var elan = local.First(c => c.Key == "Forever-Elan");
                var text = Json.Write(ArmoryShare.Build(elan));
                var snap = Json.Obj(text);          // as the server and the other side see it: parsed JSON
                Check("snapshot: identity - name, realm, class file, race, faction, guild, level", snap != null && snap.Str("name") == "Elan" && snap.Str("realm") == "Forever" && snap.Str("class") == "HUNTER" && snap.Str("race") == "Orc"
                    && snap.Str("raceFile") == "Orc" && snap.Str("faction") == "Horde" && snap.Str("guild") == "Test Guild" && snap.Int("level") == 60 && snap.Int("sex") == 2);
                var gear = snap?.Child("gear");
                Check("snapshot: what is worn - 4 slots with item id, quality, icon and item level, the average item level 58.5", gear != null && gear.Child("slots").Count == 4 && gear.Child("slots").Child("1").Int("i") == 200 && gear.Child("slots").Child("1").Int("il") == 62
                    && gear.Child("slots").Child("16").Int("i") == 202 && snap.TryGetValue("ilvl", out var ilv) && Math.Abs((double)ilv - 58.5) < 0.01);
                Check("snapshot: the item links of the worn items travel with them (enchant and suffix live in the link); links of other items do not", gear != null && gear.Child("links").Count == 4 && gear.Child("links").Str("200").Contains("Helm of Testing") && !gear.Child("links").ContainsKey("100"));
                Check("snapshot: the stats summary", gear?.Child("stats") != null && gear.Child("stats").Int("health") == 2400 && gear.Child("stats").Child("melee").Int("max") == 62);
                var tal = snap?.Child("talents");
                Check("snapshot: talents - points spent / unspent and only the nodes that have points (20 of the 29)", tal != null && tal.Int("spent") == 35 && tal.Int("unspent") == 2 && tal.List("nodes").Count == 20 && tal.List("nodes").OfType<Dictionary<string, object>>().All(n => n.Int("r") > 0),
                    tal == null ? "null" : tal.List("nodes").Count.ToString());
                var pro = snap?.Child("professions");
                Check("snapshot: professions with skill levels (Mining 225/300, Herbalism 300/300, Cooking 75/150 secondary)", pro != null && pro.List("list").Count == 3 && pro.List("list").OfType<Dictionary<string, object>>().Select(p => p.Str("name") + p.Int("skill") + p.Str("kind")).SequenceEqual(new[] { "Mining225primary", "Herbalism300primary", "Cooking75secondary" }));
                var pets = snap?.Child("pets");
                Check("snapshot: hunter pets (2, the active one named) and the abilities the hunter can teach", pets != null && pets.List("pets").Count == 2 && pets.Str("active") == "Rex:Bear" && pets.Child("known").Int("bite") == 6);
                var forbidden = new[] { "bags", "bank", "\"money\"", "gold", "mail", "\"xp\"", "rested", "zone", "Orgrimmar", "Valley of Strength", "123456789", "Linen Cloth", "Minor Healing Potion", "Iron Sword", "Rough Arrow", "Bright Gem", "Linen Bag", "Rare Satchel", "guildbank", "Consumables", "999000" };
                Check("snapshot: NOTHING of bags, bank, guild bank, gold, mail, experience or the zone is in it", forbidden.All(w => text.IndexOf(w, StringComparison.OrdinalIgnoreCase) < 0), string.Join(",", forbidden.Where(w => text.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0)));
                Check("snapshot: small enough for one frame (well under 48 KB)", text.Length < 12000, text.Length.ToString());
                Check("a character with only a core record (nothing to show) is not shareable; one without talents shares what it has", ArmoryShare.Build(local.First(c => c.Key == "Forever-Newbie")) == null && ArmoryShare.Build(local.First(c => c.Key == "Forever-Measley")) is Dictionary<string, object> ms && ms.ContainsKey("gear") && !ms.ContainsKey("talents") && !ms.ContainsKey("pets"));
                var plain = ArmoryShare.Build(elan, false);
                var sig = ArmoryShare.Signature(plain);
                Check("signature: the same character read twice has the same signature, and the game's saved-at times are not in it", sig == ArmoryShare.Signature(ArmoryShare.Build(elan, false)) && !Json.Write(plain).Contains("\"at\""));
                var elan61 = ArmoryShare.LocalFrom(HubData.Parse(HubCharsLua(now, now - 60, "61"))).First(c => c.Key == "Forever-Elan");
                var elanLater = ArmoryShare.LocalFrom(HubData.Parse(HubCharsLua(now + 500, now + 440))).First(c => c.Key == "Forever-Elan");
                Check("signature: a new level changes it; a re-save with later times and nothing else new does not", ArmoryShare.Signature(ArmoryShare.Build(elan61, false)) != sig && ArmoryShare.Signature(ArmoryShare.Build(elanLater, false)) == sig);

                // the snapshot comes back as the model the Characters page draws
                var wire = Json.Obj(text);
                var back = ArmoryShare.ToChar("mia", wire);
                var bg = back == null ? null : HubModules.GearOf(back.Hub);
                Check("round trip: the shared snapshot reads back through the Hub readers - class, level, 4 worn items with names, 20 talent nodes, 3 professions, 2 pets",
                    back != null && back.Name == "Elan" && back.Class == "HUNTER" && back.Level == 60 && back.Core.Race == "Orc" && bg != null && bg.Slots.Count == 4 && bg.Slots[1].Name == "Helm of Testing" && bg.Stats?.Health == 2400
                    && HubModules.TalentsOf(back.Hub).Nodes.Count == 20 && HubModules.ProfessionsOf(back.Hub).List.Count == 3 && HubModules.PetsOf(back.Hub).Pets.Count == 2 && HubModules.PetsOf(back.Hub).Active == "Rex:Bear");
                Check("round trip: nothing of bags or gold exists on the other side", back != null && HubModules.BagsOf(back.Hub) == null && HubModules.BankOf(back.Hub) == null && back.Money == 0 && back.Bags.Count == 0 && back.Core.Xp == 0 && string.IsNullOrEmpty(back.Core.Zone));

                // the list rows from the server: every field checked again
                var junkRow = ArmoryEntry.From(Json.Obj("{\"owner\":\"mia\",\"key\":\"Forever-X\",\"name\":\"" + new string('N', 80) + "\",\"class\":\"MAGE\",\"level\":9999,\"ilvl\":-5,\"professions\":[5,{\"name\":\"\"},{\"name\":\"Mining\",\"skill\":-4,\"max\":\"x\"}]}"));
                Check("list row: a long name is cut at 24, level is capped at 100, a negative item level is dropped, junk professions are skipped", junkRow != null && junkRow.Name.Length == 24 && junkRow.Level == 100 && junkRow.Ilvl == 0 && junkRow.Professions.Count == 1 && junkRow.Professions[0].Skill == 0 && junkRow.By == "mia");
                Check("list row: no owner / key / name / class = no row", ArmoryEntry.From(Json.Obj("{\"key\":\"A-B\",\"name\":\"A\",\"class\":\"MAGE\"}")) == null && ArmoryEntry.From(Json.Obj("{\"owner\":\"a\",\"key\":\"A-B\",\"class\":\"MAGE\"}")) == null && ArmoryEntry.From(null) == null);

                // ================================================================ the session: upload, delete, answers
                sent.Clear();
                Session.FeedForTest(ArmoryWelcome("member", "2.16.0", true));
                Check("session: the 'armory' feature of a 2.16 server is detected", Session.SupportsArmory && Session.ArmoryAvailable && Session.ArmoryOfflineLine == null);
                Check("connect with sharing OFF: nothing is uploaded and nothing is deleted; only the list is asked for", AryFrames(sent, "armory.put").Count == 0 && AryFrames(sent, "armory.delete").Count == 0 && AryFrames(sent, "armory.list").Count == 1);

                sent.Clear();
                settings.ArmoryShareOn = true; settings.ArmoryChars = new List<string> { "Forever-Elan" };
                Session.ArmorySettingsChanged();
                var puts = AryFrames(sent, "armory.put");
                Check("switch on + Elan ticked: exactly one armory.put for Forever-Elan, and only that one", puts.Count == 1 && puts[0].Str("key") == "Forever-Elan" && puts[0].Child("char").Str("name") == "Elan");
                var frameText = puts.Count == 1 ? Json.Write(puts[0]) : "";
                Check("the frame holds nothing of bags, bank, gold, zone or experience", puts.Count == 1 && forbidden.All(w => frameText.IndexOf(w, StringComparison.OrdinalIgnoreCase) < 0));
                Check("the frame is under the server's 64 KB limit", frameText.Length > 500 && frameText.Length < 60 * 1024, frameText.Length.ToString());
                Check("the put is in flight; nothing is recorded as shared before the server says so", Session.ArmoryInflightForTest == 1 && Session.ArmorySentForTest.Count == 0);
                sent.Clear();
                Session.FeedForTest("{\"t\":\"armory.saved\",\"ok\":true,\"key\":\"Forever-Elan\",\"stored\":true}");
                Check("saved: the signature is remembered (also on disk) and the list is asked for again", Session.ArmorySentForTest.Count == 1 && Session.ArmorySentForTest.ContainsKey("Forever-Elan") && File.Exists(sentFile) && File.ReadAllText(sentFile).StartsWith("Forever-Elan\t")
                    && AryFrames(sent, "armory.list").Count == 1);
                Check("the upload record holds a key and a signature, no character data", File.ReadAllText(sentFile).Length < 60 && !File.ReadAllText(sentFile).Contains("Helm"));

                // unchanged: the game re-saves the file (later times), nothing goes out
                sent.Clear();
                File.WriteAllText(hubFile, HubCharsLua(now + 500, now + 440), new UTF8Encoding(false)); Touch(3);
                clock = clock.AddMinutes(2); Session.ArmorySync();
                clock = clock.AddMinutes(2); Session.ArmorySync();
                Check("a re-save of the same character (later times only) is not an upload", AryFrames(sent, "armory.put").Count == 0);

                // changed: waits for a quiet moment (20 s), then goes
                File.WriteAllText(hubFile, HubCharsLua(now + 600, now + 540, "61"), new UTF8Encoding(false)); Touch(5);
                clock = clock.AddSeconds(30); Session.ArmorySync();
                Check("changed (level 61): not sent at once - the game rewrites its file on every /reload", AryFrames(sent, "armory.put").Count == 0 && Session.ArmoryBacklogForTest);
                clock = clock.AddSeconds(10); Session.ArmorySync();
                Check("still inside the quiet moment: still waiting", AryFrames(sent, "armory.put").Count == 0);
                clock = clock.AddSeconds(25); Session.ArmorySync();
                puts = AryFrames(sent, "armory.put");
                Check("changed and quiet for 20 s: one put with level 61", puts.Count == 1 && puts[0].Child("char").Int("level") == 61);
                Session.FeedForTest("{\"t\":\"armory.saved\",\"ok\":true,\"key\":\"Forever-Elan\",\"stored\":true}");
                sent.Clear(); clock = clock.AddMinutes(2); Session.ArmorySync();
                Check("after the answer nothing is waiting any more", AryFrames(sent, "armory.put").Count == 0 && !Session.ArmoryBacklogForTest);

                // a second character, then untick the first
                sent.Clear();
                settings.ArmoryChars = new List<string> { "Forever-Elan", "Forever-Measley" };
                Session.ArmorySettingsChanged();
                puts = AryFrames(sent, "armory.put");
                Check("ticking a second character uploads that one only (Measley), at once", puts.Count == 1 && puts[0].Str("key") == "Forever-Measley" && !puts[0].Child("char").ContainsKey("talents"));
                Session.FeedForTest("{\"t\":\"armory.saved\",\"ok\":true,\"key\":\"Forever-Measley\",\"stored\":true}");
                sent.Clear();
                settings.ArmoryChars = new List<string> { "Forever-Elan" };
                Session.ArmorySettingsChanged();
                var dels = AryFrames(sent, "armory.delete");
                Check("unticking Measley deletes exactly that character on the server and forgets it", dels.Count == 1 && dels[0].Str("key") == "Forever-Measley" && !dels[0].ContainsKey("all") && AryFrames(sent, "armory.put").Count == 0
                    && !Session.ArmorySentForTest.ContainsKey("Forever-Measley") && Session.ArmorySentForTest.ContainsKey("Forever-Elan"));
                sent.Clear();
                settings.ArmoryShareOn = false;
                Session.ArmorySettingsChanged();
                dels = AryFrames(sent, "armory.delete");
                Check("switching sharing off deletes ALL of my characters on the server and clears the record (disk too)", dels.Count == 1 && dels[0].Bool("all") && Session.ArmorySentForTest.Count == 0 && (!File.Exists(sentFile) || File.ReadAllText(sentFile).Trim().Length == 0));
                sent.Clear(); clock = clock.AddMinutes(3); Session.ArmorySync(); Session.ArmoryTickForTest();
                Check("with sharing off a later sync sends no put and no second delete", AryFrames(sent, "armory.put").Count == 0 && AryFrames(sent, "armory.delete").Count == 0);

                // refusals
                settings.ArmoryShareOn = true; settings.ArmoryChars = new List<string> { "Forever-Elan" };
                Session.ArmoryResetForTest(); sent.Clear();
                Session.ArmorySettingsChanged();
                Session.FeedForTest("{\"t\":\"armory.saved\",\"ok\":false,\"key\":\"Forever-Elan\",\"why\":\"rate\"}");
                sent.Clear(); clock = clock.AddSeconds(10); Session.ArmorySync(true);
                Check("'rate': not retried for 30 seconds ...", AryFrames(sent, "armory.put").Count == 0);
                clock = clock.AddSeconds(30); Session.ArmorySync(true);
                Check("... then tried again", AryFrames(sent, "armory.put").Count == 1);
                Session.FeedForTest("{\"t\":\"armory.saved\",\"ok\":false,\"key\":\"Forever-Elan\",\"why\":\"full\"}");
                Check("'full': says so in plain words (the Lodge keeps 30) and the same character is not sent again", (Session.ArmoryProblem ?? "").Contains("at most 30"), Session.ArmoryProblem);
                sent.Clear(); clock = clock.AddMinutes(5); Session.ArmorySync(true);
                Check("'full': no retry loop", AryFrames(sent, "armory.put").Count == 0);
                Session.ArmoryResetForTest();

                // the list and one character from the server
                Session.FeedForTest(ArmoryWelcome("member", "2.16.0", true));
                sent.Clear();
                var elanRow = ArmoryRowJson("mia", "Mia the Bold", "Forever-Elan", wire, now - 100);
                var measleyWire = Json.Obj(Json.Write(ArmoryShare.Build(local.First(c => c.Key == "Forever-Measley"))));
                var measleyRow = ArmoryRowJson("bob", "Bob", "Forever-Measley", measleyWire, now - 5000);
                string Row(string owner, string by, string key, string name, string cls, string clsText, int level, double ilvl, string profs, string extra = "") =>
                    "{\"owner\":" + Q(owner) + (by == null ? "" : ",\"by\":" + Q(by)) + ",\"key\":" + Q(key) + ",\"name\":" + Q(name) + ",\"realm\":\"Forever\",\"class\":" + Q(cls) + ",\"classText\":" + Q(clsText) + ",\"level\":" + level
                    + ",\"ilvl\":" + ilvl.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"at\":" + (now - 600) + ",\"up\":" + (now - 600) + ",\"professions\":[" + profs + "]" + extra + "}";
                var list = "{\"t\":\"armory.chars\",\"chars\":[" + elanRow + "," + measleyRow + ","
                    + Row("bob", "Bob", "Forever-Brak", "Brak", "WARRIOR", "Warrior", 42, 30, "{\"name\":\"Mining\",\"skill\":210,\"max\":300},{\"name\":\"Blacksmithing\",\"skill\":180,\"max\":300}") + ","
                    + Row("bob", "Bob", "Forever-Bina", "Bina", "MAGE", "Mage", 25, 0, "{\"name\":\"Alchemy\",\"skill\":40,\"max\":150}") + ","
                    + Row("zed", "Zed", "Other-Zorn", "Zorn", "PRIEST", "Priest", 60, 0, "{\"name\":\"Alchemy\",\"skill\":300,\"max\":300},{\"name\":\"Herbalism\",\"skill\":280,\"max\":300}", ",\"mine\":true") + ","
                    + "{\"owner\":\"x\"},5,\"junk\",{\"owner\":\"q\",\"key\":\"A-B\",\"name\":\"NoClass\",\"level\":3}]}";
                Session.FeedForTest(list);
                Check("list: 5 rows kept, the 4 junk entries skipped; the member's display name is the one the server sent", Session.ArmoryEntries.Count == 5 && Session.ArmoryListed && Session.ArmoryEntries[0].By == "Mia the Bold" && Session.ArmoryEntries.Count(e => e.Mine) == 1, Session.ArmoryEntries.Count.ToString());
                sent.Clear();
                Session.RequestArmoryChar(Session.ArmoryEntries[0]);
                var gets = AryFrames(sent, "armory.get");
                Check("opening a character asks for exactly that one (owner + key), once", gets.Count == 1 && gets[0].Str("owner") == "mia" && gets[0].Str("key") == "Forever-Elan");
                Session.RequestArmoryChar(Session.ArmoryEntries[0]);
                Check("asking again while the answer is on its way does not ask twice", AryFrames(sent, "armory.get").Count == 1);
                Session.FeedForTest("{\"t\":\"armory.char\",\"owner\":\"mia\",\"by\":\"Mia the Bold\",\"key\":\"Forever-Elan\",\"char\":" + Json.Write(wire) + "}");
                Check("the character that came back is kept", Session.ArmoryChar("mia|Forever-Elan")?.Str("name") == "Elan");
                Session.FeedForTest("{\"t\":\"armory.char\",\"owner\":\"evil\",\"key\":\"Forever-Never\",\"char\":" + Json.Write(wire) + "}");
                Check("a character nobody asked for is ignored", Session.ArmoryChar("evil|Forever-Never") == null);

                // ================================================================ lines for the Lodge view
                Session.Disconnect();
                Check("offline: a plain line says so", (Session.ArmoryOfflineLine ?? "").StartsWith("You are offline"), Session.ArmoryOfflineLine);
                Session.FeedForTest(ArmoryWelcome("member", "2.15.0", false));
                Check("a server older than 2.16: a plain line says the Lodge has no Armory yet", !Session.SupportsArmory && (Session.ArmoryOfflineLine ?? "").Contains("older than 2.16"), Session.ArmoryOfflineLine);
                sent.Clear();
                settings.ArmoryShareOn = true; settings.ArmoryChars = new List<string> { "Forever-Elan" };
                Session.ArmorySettingsChanged(); Session.ArmorySync(true);
                Check("on an old server nothing is sent at all", sent.Count(o => (o.Str("t") ?? "").StartsWith("armory.")) == 0);
                Session.Disconnect();
                Session.FeedForTest(ArmoryWelcome("guest", "2.16.0", true));
                Check("a guest: the Armory is for members; no upload, no list", (Session.ArmoryOfflineLine ?? "").Contains("Guests") && !Session.ArmoryAvailable);
                Session.Disconnect();
                Session.FeedForTest(ArmoryWelcome("member", "2.16.0", true));
                Session.FeedForTest("{\"t\":\"error\",\"text\":\"The Armory is switched off on this lodge\",\"code\":\"disabled\"}");
                Check("the owner switched the Armory off: the line says so", Session.ArmoryDisabledByServer && (Session.ArmoryOfflineLine ?? "").Contains("switched the Armory off"), Session.ArmoryOfflineLine);

                // ================================================================ Settings > Lodge
                settings.ArmoryShareOn = false; settings.ArmoryChars = null;
                Session.Disconnect();
                Session.FeedForTest(ArmoryWelcome("member", "2.16.0", true));
                ShowTab("settings"); SettingsPage.Show("lodge");
                SettingsPage.ArmoryRefreshForTest();
                await Task.Delay(150);
                Check("settings: 'Share my characters with the Lodge' is off at the start and no list is offered", !SettingsPage.ArmoryShareForTest && !SettingsPage.ArmoryPanelVisibleForTest);
                sent.Clear();
                SettingsPage.ArmorySwitchForTest(true);
                var rowsOn = SettingsPage.ArmoryRowsForTest();
                Check("settings: switched on, every character of this PC has its own switch, none ticked (6 rows)", settings.ArmoryShareOn && SettingsPage.ArmoryPanelVisibleForTest && rowsOn.Count == 6 && rowsOn.All(r => r.EndsWith("|False")), string.Join(", ", rowsOn));
                Check("settings: switched on with nobody ticked uploads nothing", AryFrames(sent, "armory.put").Count == 0 && (SettingsPage.ArmoryNoteForTest ?? "").Contains("Nothing is shared yet"), SettingsPage.ArmoryNoteForTest);
                SettingsPage.ArmoryTickForTest("Elan", true);
                Check("settings: ticking Elan is saved and uploads Elan", settings.ArmoryChars != null && settings.ArmoryChars.Contains("Forever-Elan") && AryFrames(sent, "armory.put").Count >= 1 && (SettingsPage.ArmoryNoteForTest ?? "").StartsWith("1 character is shared"), SettingsPage.ArmoryNoteForTest);
                SettingsPage.ArmoryScrollForTest();
                await ShotWait(300);
                Snapshot(P("armory-settings.png"));
                SettingsPage.ArmorySwitchForTest(false);
                Check("settings: switching off hides the list and asks the Lodge to delete everything", !settings.ArmoryShareOn && !SettingsPage.ArmoryPanelVisibleForTest && AryFrames(sent, "armory.delete").Any(d => d.Bool("all")));
                settings.ArmoryChars = null; Session.ArmoryResetForTest();
                ShowTab("addons");

                // ================================================================ the Characters page
                Session.Disconnect();
                Session.ArmoryResetForTest();
                HubData.ResetCache();
                File.WriteAllText(hubFile, HubCharsLua(now, now - 60), new UTF8Encoding(false)); Touch(9);
                InventoryView.Init(() => root);
                InventoryView.ResetViewForTest();
                TabInventory.IsChecked = true;
                for (int i = 0; i < 40 && InventoryView.CharCountForTest != 6; i++) await Task.Delay(200);
                Check("characters page: 'My characters' | 'Lodge' switch at the top, My characters first", InventoryView.ModeStripVisibleForTest && InventoryView.ModeLabelsForTest == "My characters|Lodge" && !InventoryView.ArmoryModeForTest && InventoryView.CharCountForTest == 6 && InventoryView.OwnListVisibleForTest);

                InventoryView.ModeForTest(true);
                await Task.Delay(150);
                Check("Lodge view while offline: a plain line instead of a list", InventoryView.ArmoryModeForTest && (InventoryView.ArmoryNoteForTest ?? "").StartsWith("You are offline|"), InventoryView.ArmoryNoteForTest);
                Check("Lodge view: my own list is not shown, the search says what it searches", !InventoryView.OwnListVisibleForTest && InventoryView.SearchHintForTest.Contains("members"), InventoryView.SearchHintForTest);
                Session.FeedForTest(ArmoryWelcome("member", "2.15.0", false));
                await Task.Delay(150);
                Check("Lodge view on a 2.15 server: 'no Armory yet', and it names version 2.16", (InventoryView.ArmoryNoteForTest ?? "").StartsWith("This Lodge has no Armory yet|") && InventoryView.ArmoryNoteForTest.Contains("2.16"), InventoryView.ArmoryNoteForTest);
                Snapshot(P("armory-old-server.png"));
                Session.Disconnect();
                Session.FeedForTest(ArmoryWelcome("guest", "2.16.0", true));
                await Task.Delay(150);
                Check("Lodge view as a guest: the Armory is for members", (InventoryView.ArmoryNoteForTest ?? "").StartsWith("The Armory is for members|"), InventoryView.ArmoryNoteForTest);
                Session.Disconnect();
                Session.FeedForTest(ArmoryWelcome("member", "2.16.0", true));
                await Task.Delay(150);
                Check("Lodge view, list asked for but not here yet: 'loading'", (InventoryView.ArmoryNoteForTest ?? "").StartsWith("Loading the Lodge's characters|"), InventoryView.ArmoryNoteForTest);
                Session.FeedForTest("{\"t\":\"armory.chars\",\"chars\":[]}");
                await Task.Delay(150);
                Check("Lodge view, nobody shares yet: says where members switch it on", (InventoryView.ArmoryNoteForTest ?? "").StartsWith("No characters shared yet|") && InventoryView.ArmoryNoteForTest.Contains("Settings > Lodge > Armory"), InventoryView.ArmoryNoteForTest);

                sent.Clear();
                Session.FeedForTest(list);
                await Task.Delay(200);
                var rows = InventoryView.ArmoryRowsForTest();
                Check("Lodge view: 5 characters, highest level first (Elan and Zorn at 60, then Brak 42, Bina 25, Measley 22), with the member's name on the second line",
                    InventoryView.ArmoryListVisibleForTest && InventoryView.ArmoryNoteForTest == null && rows.Count == 5 && rows[0].StartsWith("Elan|Lv 60 Hunter · Mia the Bold|") && rows[1].StartsWith("Zorn|Lv 60 Priest · Zed|") && rows[2].StartsWith("Brak|Lv 42 Warrior · Bob|")
                    && rows[3].StartsWith("Bina|Lv 25 Mage · Bob|") && rows[4].StartsWith("Measley|Lv 22 Paladin · Bob|"), string.Join(" ; ", rows));
                Check("Lodge view: the count line (5 characters from 3 members)", InventoryView.ArmoryCountForTest == "5 characters from 3 members", InventoryView.ArmoryCountForTest);
                Check("Lodge view: the first one is opened and asked for (armory.get mia / Forever-Elan)", InventoryView.ArmoryOpenKeyForTest == "mia|Forever-Elan" && AryFrames(sent, "armory.get").Any(g => g.Str("owner") == "mia" && g.Str("key") == "Forever-Elan"));
                Check("filter options come from the list: classes, level brackets, professions",
                    InventoryView.ArmoryOptionsForTest("class").SequenceEqual(new[] { "All classes", "Hunter", "Mage", "Paladin", "Priest", "Warrior" })
                    && InventoryView.ArmoryOptionsForTest("level").SequenceEqual(new[] { "Any level", "Level 60", "40-49", "20-29" })
                    && InventoryView.ArmoryOptionsForTest("profession").First() == "Any profession" && InventoryView.ArmoryOptionsForTest("profession").Contains("Alchemy") && InventoryView.ArmoryOptionsForTest("profession").Contains("Mining"),
                    string.Join("/", InventoryView.ArmoryOptionsForTest("class")) + " ; " + string.Join("/", InventoryView.ArmoryOptionsForTest("level")) + " ; " + string.Join("/", InventoryView.ArmoryOptionsForTest("profession")));
                InventoryView.ArmoryFilterForTest("class", "Mage");
                Check("filter by class: only the mage", InventoryView.ArmoryRowsForTest().Select(r => r.Split('|')[0]).SequenceEqual(new[] { "Bina" }) && InventoryView.ArmoryCountForTest == "1 of 5 characters", string.Join(";", InventoryView.ArmoryRowsForTest()));
                InventoryView.ArmoryFilterForTest("class", null);
                InventoryView.ArmoryFilterForTest("level", "Level 60");
                Check("filter by level: the two level 60s", InventoryView.ArmoryRowsForTest().Select(r => r.Split('|')[0]).SequenceEqual(new[] { "Elan", "Zorn" }), string.Join(";", InventoryView.ArmoryRowsForTest()));
                InventoryView.ArmoryFilterForTest("level", "40-49");
                Check("filter by level bracket 40-49: Brak", InventoryView.ArmoryRowsForTest().Select(r => r.Split('|')[0]).SequenceEqual(new[] { "Brak" }));
                InventoryView.ArmoryFilterForTest("level", null);
                InventoryView.ArmoryFilterForTest("profession", "Alchemy");
                var alch = InventoryView.ArmoryRowsForTest();
                Check("filter by profession (who has Alchemy): the highest skill first, and the second line shows the skill instead of the member", alch.Count == 2 && alch[0].StartsWith("Zorn|Lv 60 Priest · Alchemy 300|") && alch[1].StartsWith("Bina|Lv 25 Mage · Alchemy 40|"), string.Join(";", alch));
                InventoryView.ArmoryFilterForTest("class", "Priest");
                Check("filters combine (Alchemy + Priest)", InventoryView.ArmoryRowsForTest().Select(r => r.Split('|')[0]).SequenceEqual(new[] { "Zorn" }));
                await ShotWait(200);
                Snapshot(P("armory-list-filtered.png"));
                InventoryView.ArmoryFilterForTest("class", null); InventoryView.ArmoryFilterForTest("profession", null);
                InventoryView.ArmorySearchForTest("bob");
                Check("search by member name (case-insensitive): Bob's three", InventoryView.ArmoryRowsForTest().Select(r => r.Split('|')[0]).OrderBy(x => x).SequenceEqual(new[] { "Bina", "Brak", "Measley" }), string.Join(";", InventoryView.ArmoryRowsForTest()));
                InventoryView.ArmorySearchForTest("zor");
                Check("search by character name", InventoryView.ArmoryRowsForTest().Select(r => r.Split('|')[0]).SequenceEqual(new[] { "Zorn" }));
                InventoryView.ArmorySearchForTest("nobody-has-this");
                Check("a search with no match says so", InventoryView.ArmoryRowsForTest().Count == 0 && InventoryView.ArmoryCountForTest == "0 of 5 characters");
                InventoryView.ArmorySearchForTest("");

                // open Elan: the same views as my own characters, read-only
                InventoryView.ArmoryOpenForTest("mia|Forever-Elan");
                Session.FeedForTest("{\"t\":\"armory.char\",\"owner\":\"mia\",\"by\":\"Mia the Bold\",\"key\":\"Forever-Elan\",\"char\":" + Json.Write(wire) + "}");
                await Task.Delay(300);
                Check("opening a member's character: Overview, Gear, Talents, Professions and Pet (hunter) tabs - no Bags, Bank or Guild bank", new[] { "overview", "gear", "talents", "professions", "pet" }.All(InventoryView.TabVisibleForTest) && !new[] { "bags", "bank", "guild" }.Any(InventoryView.TabVisibleForTest));
                Check("the title is the character in its class colour", InventoryView.ArmoryTitleForTest == "Elan");
                var ov = InventoryView.CharTabLinesForTest();
                Check("overview: level, race, class; realm and faction; guild; 'Shared by <member>'", ov.Contains("identity|Level 60 Orc Hunter") && ov.Any(l => l.StartsWith("where|Forever · Horde|Test Guild|")), string.Join(" | ", ov));
                Check("overview: NO gold, experience, zone, bag space or bank", !ov.Any(l => l.StartsWith("gold|") || l.StartsWith("xp|") || l.StartsWith("stat|Bag space") || l.StartsWith("stat|Bank") || l.Contains("Orgrimmar")) && ov.Contains("where|Forever · Horde|Test Guild|"), string.Join(" | ", ov));
                Check("overview: item level 58.5, talents and professions summary", ov.Contains("stat|Item level|58.5") && ov.Contains("stat|Talents|35 spent · 2 unspent") && ov.Contains("stat|Professions|Mining, Herbalism, Cooking"), string.Join(" | ", ov));
                Check("overview: the age line says who shared it", InventoryView.ViewInfoForTest.StartsWith("Updated ") && InventoryView.ViewInfoForTest.EndsWith("shared by Mia the Bold"), InventoryView.ViewInfoForTest);
                await ShotWait(200);
                Snapshot(P("armory-overview.png"));

                InventoryView.ArmoryTabForTest("gear");
                await Task.Delay(200);
                var gl = InventoryView.CharTabLinesForTest();
                Check("gear: the paper doll with the same 4 worn items, names from the links, item levels", gl.Count(x => x.StartsWith("slot|")) == 4 && gl.Contains("slot|1|Head|Helm of Testing|62") && gl.Contains("slot|15|Back|White Cloak|10") && gl.Contains("slot|5|Chest|Plain Chestguard|55"), string.Join(" | ", gl.Where(x => x.StartsWith("slot|"))));
                Check("gear: average item level in the age line, and the stats panel (health, strength, armor, damage)", InventoryView.ViewInfoForTest.StartsWith("Average item level 58.5 · 4 items worn") && InventoryView.ViewInfoForTest.EndsWith("shared by Mia the Bold")
                    && gl.Any(x => x.StartsWith("gstat|Health|")) && gl.Contains("gstat|Strength|55") && gl.Contains("gstat|Armor|650") && gl.Contains("gstat|Main hand damage|40 - 62"), InventoryView.ViewInfoForTest);
                var dl = InventoryView.DollLayoutForTest();
                Check("gear: same doll layout as my own characters (19 slots, left / right columns, weapons along the bottom)", dl.Count == 19 && dl.Contains("1|L|0") && dl.Contains("10|R|0") && dl.Contains("16|B|8"), string.Join(" ", dl));
                var tipOk = InventoryView.DollTipForTest(15);
                Check("gear hover: item name, slot and item level - and none of MY counts ('Other characters', 'All together')", tipOk.Count >= 3 && tipOk[0] == "White Cloak" && tipOk[1] == "Back · Item level 10" && !tipOk.Any(t => t == "Other characters" || t == "Characters" || t == "All together"), string.Join(" | ", tipOk));
                await ShotWait(200);
                Snapshot(P("armory-gear.png"));
                Check("gear: Ctrl+click on a worn item opens it in the Atlas (the same hook as my own gear)", InventoryView.AtlasClickForTest(15));

                InventoryView.ArmoryTabForTest("talents");
                await Task.Delay(200);
                var tl = InventoryView.CharTabLinesForTest();
                Check("talents: 35 spent / 2 unspent and the nodes with points laid out in trees", tl.Any(x => x.StartsWith("talentinfo|35|2|")) && tl.Any(x => x.StartsWith("tree|")) && InventoryView.ViewInfoForTest.EndsWith("shared by Mia the Bold"), string.Join(" | ", tl));
                Check("talents: no build code card when the sender had none", !tl.Any(x => x.StartsWith("build|")));
                var withBuild = Json.Obj(Json.Write(wire)); withBuild.Child("talents")["build"] = "ETB1-AbCdEf123456";
                Session.FeedForTest("{\"t\":\"armory.char\",\"owner\":\"mia\",\"by\":\"Mia the Bold\",\"key\":\"Forever-Elan\",\"char\":" + Json.Write(withBuild) + "}");
                await Task.Delay(200);
                InventoryView.ArmoryRefreshForTest();
                Check("talents: a shared build code is shown with a Copy button", InventoryView.CharTabLinesForTest().Contains("build|ETB1-AbCdEf123456"), string.Join(" | ", InventoryView.CharTabLinesForTest()));
                await ShotWait(200);
                Snapshot(P("armory-talents.png"));

                InventoryView.ArmoryTabForTest("professions");
                await Task.Delay(200);
                var pl = InventoryView.CharTabLinesForTest();
                Check("professions: Mining 225 / 300, Herbalism 300 / 300, Cooking 75 / 150 (secondary)", pl.Where(x => x.StartsWith("prof|")).SequenceEqual(new[] { "prof|Mining|225|300|primary", "prof|Herbalism|300|300|primary", "prof|Cooking|75|150|secondary" }), string.Join(" | ", pl));
                Check("professions: the page says plainly that learned recipes are not recorded (so no 'who can make X'), and shows no recipe list of its own", pl.Contains("profnote|recipes unknown") && !pl.Any(x => x.StartsWith("recipes|")));
                await ShotWait(200);
                Snapshot(P("armory-professions.png"));

                InventoryView.ArmoryTabForTest("pet");
                await Task.Delay(200);
                var pt = InventoryView.CharTabLinesForTest();
                Check("pet: Rex with you, Spot in the stable, three abilities the hunter can teach", pt.Contains("petinfo|1|1|3") && pt.Any(x => x.StartsWith("pet|with you|Rex|Bear|40|Faithful")) && InventoryView.ViewInfoForTest.EndsWith("shared by Mia the Bold"), string.Join(" | ", pt));

                // a character that shared less: Measley has gear and nothing else
                InventoryView.ArmoryOpenForTest("bob|Forever-Measley");
                Session.FeedForTest("{\"t\":\"armory.char\",\"owner\":\"bob\",\"by\":\"Bob\",\"key\":\"Forever-Measley\",\"char\":" + Json.Write(measleyWire) + "}");
                await Task.Delay(300);
                Check("a paladin has no Pet tab and the page falls back to Overview", !InventoryView.TabVisibleForTest("pet") && InventoryView.TabForTest == "overview");
                InventoryView.ArmoryTabForTest("talents");
                Check("a tab the member did not share says so in plain words", InventoryView.CharTabLinesForTest().SequenceEqual(new[] { "empty|Measley did not share talents." }), string.Join(" | ", InventoryView.CharTabLinesForTest()));
                InventoryView.ArmoryTabForTest("gear");
                Check("the same member's gear shows", InventoryView.CharTabLinesForTest().Contains("slot|1|Head|Leather Cap|20"), string.Join(" | ", InventoryView.CharTabLinesForTest().Take(8)));

                // not opened yet: a calm line while it loads
                InventoryView.ArmoryOpenForTest("bob|Forever-Brak");
                await Task.Delay(150);
                Check("a character that has not arrived yet: 'Opening Brak...' and it was asked for", InventoryView.CharTabLinesForTest().Any(x => x.StartsWith("empty|Opening Brak")) && AryFrames(sent, "armory.get").Any(g => g.Str("key") == "Forever-Brak"));

                // back to my own characters
                InventoryView.ModeForTest(false);
                await Task.Delay(300);
                Check("back to 'My characters': my list and my tabs (Bags and Bank) are back, my search is mine again", !InventoryView.ArmoryModeForTest && InventoryView.OwnListVisibleForTest && InventoryView.CharCountForTest == 6 && InventoryView.TabVisibleForTest("bags") && InventoryView.TabVisibleForTest("bank")
                    && InventoryView.SearchHintForTest.Contains("bag"), InventoryView.SearchHintForTest);
                InventoryView.FilterForTest("Forever-Elan");
                InventoryView.SelectTabForTest("overview");
                var mine = InventoryView.CharTabLinesForTest();
                Check("my own overview still has gold, experience and bag space", mine.Contains("gold|" + InventoryReader.Money(123456789)) && mine.Any(l => l.StartsWith("xp|")) && mine.Contains("stat|Bag space|17 free of 22"), string.Join(" | ", mine));
                Check("my own gear tooltip still has the counts across my characters", InventoryView.DollTipForTest(15).Any(t => t.Contains("Elan")), string.Join(" | ", InventoryView.DollTipForTest(15)));
                Check("the Outpost wrote nothing into the WoW folder", Directory.GetFiles(root, "*", SearchOption.AllDirectories).Select(f => Path.GetFileName(f)).All(n => n == "ElansHub.lua"));

                // learned recipes (2.52.0, server 2.19.0 `armory2`): the snapshot, the signature, "who can craft this"
                ArmoryKnownChecks(dir, Check, sent);
            }
            catch (Exception ex) { ok = false; notes.Add("FAIL exception: " + ex); }
            finally
            {
                Session.Disconnect();
                Session.SentForTest = null; Session.Clock = hadClock; Session.ArmorySourceForTest = null; Session.ArmoryResetForTest();
                settings.WowRoot = hadRoot; settings.ArmoryShareOn = hadOn; settings.ArmoryChars = hadChars;
                if (sentFile != null) { try { File.Delete(sentFile); File.Delete(sentFile + ".tmp"); } catch { } }
                HubData.ResetCache();
                IconStore.ForgetMemory();
                InventoryView.ModeForTest(false);
                InventoryView.ResetViewForTest();
                InventoryView.Init(() => settings.WowRoot);
                ShowTab("addons");
            }
            notes.Insert(0, "armory (2.48.1): " + (ok ? "all ok" : "FAILURES"));
            return string.Join("\r\n", notes);
        }
    }
}
