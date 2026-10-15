using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;

namespace ElansAddonHub
{
    // --selftest "atlaslodge" (part): hunter field notes (Hunter Helper 1.26.11 module hunter.fieldnotes, Lodge server 2.23 feature "atlas5"). On fixture folders under
    // the scratch directory and synthetic Lodge frames: the Hub module (validation of every field, too new, wiped = empty), the upload (only to a server with atlas5,
    // only what changed, caps, no character names, none of the module's own bookkeeping), the Lodge's merge (validation again, the cache), the words on the Atlas
    // ("Seen by Lodge hunters: level 35, last seen 2 h ago, 3 members" + this PC's own), and SharedData.lua's fieldNotes section for Hunter Helper (escaping, caps, removal).
    public partial class MainWindow
    {
        static string NotesLua(string data, int v = 1) =>
            "ElansHubData = { schema = 1, chars = { [\"SECRETCHAR-Realm\"] = { at = 1, mods = {} } }, account = { mods = { [\"hunter.fieldnotes\"] = { v = " + v + ", at = 1, by = \"ElansHunterHelper\", ver = \"1.26.11\", data = { at = " + AtlasAt + ", " + data + " } } } } }\n";

        async Task AtlasNotesTest(Action<string, bool> check, List<Dictionary<string, object>> sent, Func<DateTime> getNow, Action<DateTime> setNow)
        {
            var now = getNow();
            long nowU = new DateTimeOffset(now).ToUnixTimeSeconds();
            const string AllAtlas = "\"reply\",\"react\",\"react2\",\"pin\",\"profile\",\"displayname\",\"lfg\",\"history2\",\"search\",\"atlas\",\"atlas2\",\"atlas3\",\"atlas4\"";
            var sentFile = Session.AtlasSentPathForTest;
            var hadNow = AtlasQuery.Context.NowUtc; var hadLodge = AtlasQuery.Context.Lodge; var hadOwn = AtlasQuery.Context.OwnFieldNotes; var hadText = SharedDataFile.FieldNotesText;
            string sharedFile = Path.Combine(atlasRoot, "_classic_beta_", "Interface", "AddOns", "ElansHub", "SharedData.lua");
            try
            {
                Session.Disconnect(); Session.AtlasResetForTest(); try { File.Delete(sentFile); } catch { }
                SharedDataFile.FieldNotesText = "";
                AtlasQuery.Context.NowUtc = () => now;

                // ---- the Hub module
                string data = "notes = { "
                    + "[\"3100\"] = { name = \"Spot\", min = 35, max = 35, n = 7, first = " + (nowU - 9000) + ", last = " + (nowU - 600) + ", hist = { " + (nowU - 9000) + ", " + (nowU - 600) + " }, map = 1445, x = 68.0, y = 46.8,"
                    + " pts = { { map = 1445, p = { 68.0, 46.8, 68.4, 46.9, 30.0, 40.0 } } }, class = \"rare\", fam = \"Wolf\", dead = " + nowU + ", taught = { \"bite4\", \"claw3\" }, dmin = 20, dmax = 20, dclass = \"rare\", dfam = \"Wolf\" },"
                    + " [\"3101\"] = { name = \"Bad\\\"Name<b>\", n = 2, min = 90 },"
                    + " [\"3102\"] = { n = -1 }, [\"3103\"] = { n = 0 }, [\"abc\"] = { n = 1 }, [\"0\"] = { n = 1 },"
                    + " [\"3104\"] = { n = 1, class = \"mythic\", taught = { \"Bite 4\", \"dash2\" }, pts = { { map = 0, p = { 1, 2 } }, { map = 15, p = { 150, 5, 20, 20 } } } } }";
                WriteAtlasHub("ACC1", NotesLua(data));
                var s = AtlasObs.Read(atlasRoot);
                check("notes: hunter.fieldnotes alone (no Toolkit module) gives a snapshot", s != null && s.FieldNotes.Count == 3 && s.Kills.Count == 0 && !s.IsEmpty);
                var n0 = s.FieldNotes[3100];
                check("notes: fields read - name, level, sightings, last seen, class, family", n0.Name == "Spot" && n0.Min == 35 && n0.Max == 35 && n0.N == 7 && n0.Last == nowU - 600 && n0.Class == "rare" && n0.Fam == "Wolf" && n0.IsRare);
                check("notes: abilities taught are a set", n0.Taught.Keys.OrderBy(k => k).SequenceEqual(new[] { "bite4", "claw3" }));
                check("notes: spots in percent become map fractions, two spots within 1 % are one", n0.Pts.Count == 1 && n0.Pts[1445].Count == 2 && Math.Abs(n0.Pts[1445][0].X - 0.68) < 1e-9 && Math.Abs(n0.Pts[1445][0].Y - 0.468) < 1e-9 && Math.Abs(n0.Pts[1445][1].X - 0.3) < 1e-9);
                var n1 = s.FieldNotes[3101];
                check("notes: a name loses quotes and brackets, a level over 80 is left out but the sightings stay", n1.Name == "BadNameb" && n1.Min == 0 && n1.N == 2);
                var n4 = s.FieldNotes[3104];
                check("notes: an unknown class, a token with a space, map 0 and an out-of-range spot are left out", n4.Class == "" && n4.Taught.Keys.SequenceEqual(new[] { "dash2" }) && n4.Pts.Count == 1 && n4.Pts[15].Count == 1 && Math.Abs(n4.Pts[15][0].X - 0.2) < 1e-9);
                check("notes: a bad sighting count, an empty note, a text key and id 0 are dropped (no note, counted)", !s.FieldNotes.ContainsKey(3102) && !s.FieldNotes.ContainsKey(3103) && s.FieldNotes.Count == 3);
                check("notes: a module of another version is not read (too new), and it is flagged", AtlasObs.Read(atlasRoot) != null && HubData.Parse(NotesLua(data, 2)).AccountModules["hunter.fieldnotes"].TooNew && AtlasObs.NotesOf(HubData.Parse(NotesLua(data, 2))) == null);
                WriteAtlasHub("ACC1", NotesLua("notes = {}"));
                var wiped = AtlasObs.Read(atlasRoot);
                check("notes: a wiped list is an empty snapshot, not absent", wiped != null && wiped.FieldNotes.Count == 0 && wiped.IsEmpty);
                WriteAtlasHub("ACC1", NotesLua(data));
                s = AtlasObs.Read(atlasRoot);

                // ---- what is shared
                var none = AtlasObs.MakeBatch(s, null, new Dictionary<string, string>());
                var b = AtlasObs.MakeBatch(s, null, new Dictionary<string, string>(), false, false, false, true);
                check("share: field notes go out only to a server that takes them", none == null && b != null && b.Counts["fieldnotes"] == 3);
                var f3100 = b.Frame.Child("fieldnotes").Child("3100");
                check("share: a note is { n, name, min, max, last, class, fam, taught[], pts{map:[{x,y}]} }", f3100.NLong("n") == 7 && f3100.Str("name") == "Spot" && f3100.NLong("min") == 35 && f3100.NLong("max") == 35 && f3100.NLong("last") == nowU - 600
                    && f3100.Str("class") == "rare" && f3100.Str("fam") == "Wolf" && f3100.List("taught").Count == 2 && f3100.Child("pts").List("1445").Count == 2);
                var wire = Json.Write(b.Frame);
                check("share: nothing of the module's own bookkeeping and no names of characters leave (first, hist, dead, map, x, y, dmin, dclass, SECRETCHAR)",
                    new[] { "first", "hist", "dead", "dmin", "dmax", "dclass", "dfam", "SECRET", "ElansHunterHelper" }.All(k => !wire.Contains(k)) && !f3100.ContainsKey("map") && !f3100.ContainsKey("x"));
                check("share: the same notes are not sent twice", AtlasObs.MakeBatch(s, null, b.Sigs, false, false, false, true) == null);
                WriteAtlasHub("ACC1", NotesLua(data.Replace("n = 7,", "n = 8,")));
                var d = AtlasObs.MakeBatch(AtlasObs.Read(atlasRoot), null, b.Sigs, false, false, false, true);
                check("share: a note that grew is the only entry", d != null && d.Entries == 1 && d.Frame.Child("fieldnotes").Child("3100").NLong("n") == 8);
                var big = new AtlasObs.Snapshot();
                for (int i = 1; i <= 100; i++) big.FieldNotes[i] = new AtlasObs.FieldNote { Id = i, N = i };
                var bb = AtlasObs.MakeBatch(big, null, new Dictionary<string, string>(), false, false, false, true);
                check("share: at most 60 notes per message, the rest waits", bb.Counts["fieldnotes"] == AtlasObs.MaxNotesPerShare && bb.Remaining == 40);
                var pts = new AtlasObs.Snapshot();
                for (int i = 1; i <= 40; i++)
                {
                    var fn = new AtlasObs.FieldNote { Id = i, N = 1 };
                    fn.Pts[15] = Enumerable.Range(0, 24).Select(k => new AtlasObs.NotePoint { X = 0.02 * k + 0.01, Y = 0.02 * (i % 40) + 0.005 }).ToList();
                    pts.FieldNotes[i] = fn;
                }
                var pb = AtlasObs.MakeBatch(pts, null, new Dictionary<string, string>(), false, false, false, true);
                check("share: at most 600 spots per message (25 notes of 24), the frame stays under 56 KB", pb.Counts["fieldnotes"] == 25 && Json.Write(pb.Frame).Length < 56 * 1024 && pb.Remaining == 15);

                // ---- the Lodge's merge
                var lodge = new LodgeAtlas();
                lodge.Apply(Json.Obj("{\"t\":\"atlas.data\",\"epoch\":\"nn1\",\"rev\":5,\"at\":" + (AtlasAt + 9) + ",\"full\":true,\"fieldnotes\":{\"3100\":{\"name\":\"Spot\",\"min\":35,\"max\":35,\"n\":21,\"m\":3,\"last\":" + (nowU - 7200)
                    + ",\"class\":\"rare\",\"fam\":\"Wolf\",\"taught\":{\"bite4\":3,\"claw3\":1,\"Bad Token\":1},\"pts\":{\"1445\":[{\"x\":0.68,\"y\":0.468,\"m\":2},{\"x\":9,\"y\":0.1}]},\"by\":[\"Amy the Bold\",\"Bob\"]},"
                    + "\"3101\":{\"n\":\"x\"},\"0\":{\"n\":1},\"3105\":{\"n\":0},\"3106\":{\"n\":4,\"class\":\"mythic\",\"min\":99}}}"));
                var ln = lodge.Note(3100);
                check("pool: the merged note is read (level, sightings, members, last seen, class, family, abilities with member counts, spots with counts, who)",
                    ln != null && ln.Min == 35 && ln.N == 21 && ln.Members == 3 && ln.Last == nowU - 7200 && ln.Class == "rare" && ln.Fam == "Wolf" && ln.Taught["bite4"] == 3 && ln.Taught["claw3"] == 1
                    && ln.Pts[1445].Count == 1 && ln.Pts[1445][0].M == 2 && ln.By.SequenceEqual(new[] { "Amy the Bold", "Bob" }));
                check("pool: what does not hold together is left out and counted (bad token, spot x = 9, n = \"x\", id 0, an empty note); an odd class / level only loses that field",
                    !ln.Taught.ContainsKey("Bad Token") && lodge.Note(3101) == null && lodge.Note(3105) == null && lodge.Note(0) == null && lodge.Note(3106) != null && lodge.Note(3106).Class == "" && lodge.Note(3106).Min == 0 && lodge.Dropped >= 4);
                var round = new LodgeAtlas();
                round.Apply(Json.Obj(Json.Write(lodge.ToCache())));
                var rn = round.Note(3100);
                check("pool: the cache round trip keeps the note", rn != null && rn.N == 21 && rn.Members == 3 && rn.Taught["bite4"] == 3 && rn.Pts[1445][0].M == 2 && rn.By.Count == 2 && round.FieldNoteCount == 2);
                check("pool: the words - level, last seen, members", AtlasObs.NoteLine(ln, now) == "Seen by Lodge hunters: level 35, last seen 2 h ago, 3 members");

                // ---- on the Atlas
                AtlasQuery.Context.Lodge = () => lodge;
                AtlasQuery.Context.OwnFieldNotes = () => s.FieldNotes;
                check("atlas: the tip for a rare puts the Lodge's words first, then this PC's own", AtlasQuery.NoteTip(3100) == "Seen by Lodge hunters: level 35, last seen 2 h ago, 3 members · Seen by you: level 35, last seen 10 min ago, 7 sightings");
                check("atlas: no note, no tip", AtlasQuery.NoteTip(99999) == "");
                check("atlas: rare notes with spots feed the world map's rare layer (the Lodge's first)", AtlasQuery.RareNotes().Select(n => n.Id).SequenceEqual(new[] { 3100 }));
                var page = AtlasQuery.Build("npc:3100");
                var fLodge = page.Facts.FirstOrDefault(f => f.Label == "Seen by Lodge hunters");
                var fOwn = page.Facts.FirstOrDefault(f => f.Label == "Seen by you");
                check("atlas: the creature page says 'Seen by Lodge hunters: level 35, last seen 2 h ago, 3 members' and 'Seen by you' with the sightings", !page.NotFound && fLodge != null && fLodge.Value == "level 35, last seen 2 h ago, 3 members" && fOwn != null && fOwn.Value == "level 35, last seen 10 min ago, 7 sightings");
                var secN = page.Section("lodgenotes");
                var rBy = secN?.Rows.FirstOrDefault(r => r.Key == "notes:by");
                var rTaught = secN?.Rows.FirstOrDefault(r => r.Key == "notes:taught");
                check("atlas: the Lodge tab names the members (display names) and the abilities taught, with how many of the hunters saw each", rBy != null && rBy.Title == "Amy the Bold, Bob" && rBy.Detail == "and 1 more" && rTaught != null && rTaught.Title == "Bite 4 (3/3), Claw 3 (1/3)" && AtlasTabs.OfSection("lodgenotes") == AtlasTabs.Lodge);
                check("atlas: a creature only the notes know is not 'not found'", !AtlasQuery.Build("npc:3106").NotFound);

                // ---- the session
                var features = AllAtlas + ",\"atlas5\"";
                Session.Disconnect(); Session.AtlasResetForTest(); try { File.Delete(sentFile); } catch { }
                sent.Clear();
                Session.FeedForTest(FunWelcome("member", FunPeople(false), "", AllAtlas, "2.22.0"));
                check("session: a 2.22 server (no atlas5) gets no field notes - nothing at all to upload from a notes-only Hub file", !Session.SupportsAtlasNotes && AFrames(sent, "atlas.share").Count == 0);
                Session.Disconnect(); Session.AtlasResetForTest(); sent.Clear();
                Session.FeedForTest(FunWelcome("guest", FunPeople(false), "", features, "2.23.0"));
                check("session: a guest uploads nothing", Session.SupportsAtlasNotes && AFrames(sent, "atlas.share").Count == 0);
                Session.Disconnect(); Session.AtlasResetForTest(); sent.Clear();
                var hadOff = settings.AtlasShareOff;
                settings.AtlasShareOff = true;
                Session.FeedForTest(FunWelcome("member", FunPeople(false), "", features, "2.23.0"));
                check("session: with 'Share my Atlas observations' off nothing is uploaded (the aggregate is still asked for)", AFrames(sent, "atlas.share").Count == 0 && AFrames(sent, "atlas.get").Count == 1);
                settings.AtlasShareOff = hadOff;
                Session.Disconnect(); Session.AtlasResetForTest(); sent.Clear(); try { File.Delete(sentFile); } catch { }
                Directory.CreateDirectory(Path.GetDirectoryName(sharedFile));
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(sharedFile), "ElansHub.toc"), "## Title: Elan's Hub\nElansHub.lua\nSharedData.lua\n");
                File.WriteAllText(sharedFile, "ElansHubShared = ElansHubShared or {}\n");
                Session.FeedForTest(FunWelcome("member", FunPeople(false), "", features, "2.23.0"));
                var shares = AFrames(sent, "atlas.share");
                check("session: a 2.23 server with atlas5 gets the three notes right after the welcome", Session.SupportsAtlasNotes && shares.Count == 1 && shares[0].Child("fieldnotes") != null && shares[0].Child("fieldnotes").Count == 3 && shares[0].Count == 2);
                check("session: the upload carries no character name and nothing of the account", !Json.Write(shares[0]).Contains("SECRET") && !Json.Write(shares[0]).Contains("ACC"));
                Session.FeedForTest("{\"t\":\"atlas.shared\",\"ok\":true,\"accepted\":3,\"unchanged\":0,\"rejected\":0,\"rev\":40,\"epoch\":\"nn1\"}");
                check("session: what was uploaded is remembered (3 notes, key b<npc>)", Session.AtlasSentForTest.Count(k => k.Key[0] == 'b') == 3);

                // ---- SharedData.lua: the Lodge's merge for Hunter Helper
                sent.Clear();
                Session.FeedForTest(AtlasPage("nn1", 41, "\"fieldnotes\":{\"3100\":{\"name\":\"Spot\",\"min\":35,\"max\":36,\"n\":21,\"m\":3,\"last\":" + (nowU - 7200)
                    + ",\"class\":\"rare\",\"fam\":\"Wolf\",\"taught\":{\"bite4\":3,\"claw3\":1},\"pts\":{\"1445\":[{\"x\":0.68,\"y\":0.468,\"m\":2},{\"x\":0.3,\"y\":0.4,\"m\":1}]},\"by\":[\"Amy the Bold\"]},"
                    + "\"3107\":{\"name\":\"Plain\",\"n\":2,\"m\":1}}", full: true));
                check("sharedData: the Lodge's notes are written next to the flights when the answer arrives", File.Exists(sharedFile) && File.ReadAllText(sharedFile).Contains("fieldNotes") && File.ReadAllText(sharedFile).Contains("flights"));
                var g = LuaData.ReadGlobals(File.ReadAllText(sharedFile));
                var hub = g != null && g.TryGetValue("ElansHubShared", out var hv) ? hv as Dictionary<string, object> : null;
                var fnTab = hub?.Child("fieldNotes");
                var npc = fnTab?.Child("npcs")?.Child("3100");
                check("sharedData: structure - v, at (newest sighting), the beast with name, level range, sightings, hunters, last seen, class, family",
                    fnTab != null && fnTab.Int("v") == 1 && fnTab.Long("at") == nowU - 7200 && npc != null && npc.Str("name") == "Spot" && npc.Int("min") == 35 && npc.Int("max") == 36 && npc.Int("n") == 21 && npc.Int("m") == 3
                    && npc.Long("last") == nowU - 7200 && npc.Str("class") == "rare" && npc.Str("fam") == "Wolf");
                double D(Dictionary<string, object> t, string k) => t != null && t.TryGetValue(k, out var v) && v is double x ? x : -1;
                var spot1 = npc?.Child("pts")?.Child("1445")?.Child("1");
                check("sharedData: abilities taught with the number of hunters, spots as { x, y } per map, a beast with only a name and a count", npc != null && npc.Child("taught").Int("bite4") == 3 && npc.Child("taught").Int("claw3") == 1
                    && Math.Abs(D(spot1, "1") - 0.68) < 1e-6 && Math.Abs(D(spot1, "2") - 0.468) < 1e-6 && npc.Child("pts").Child("1445").Count == 2 && fnTab.Child("npcs").Child("3107")?.Int("n") == 2);
                check("sharedData: the file holds no member name and no character name", !File.ReadAllText(sharedFile).Contains("Amy") && !File.ReadAllText(sharedFile).Contains("SECRET"));
                // an unrelated write (flights, guides) keeps the section
                SharedDataFile.Write(atlasRoot, null, null, now);
                check("sharedData: another writer of the file (flights, guides) keeps the fieldNotes section", File.ReadAllText(sharedFile).Contains("fieldNotes"));
                // the Lodge forgets them (revert / a new epoch without notes): the section leaves the file
                Session.FeedForTest(AtlasPage("nn2", 5, "\"kills\":{\"1\":{\"n\":1}}", full: true));
                check("sharedData: a full answer without notes takes the section out of the file again", !File.ReadAllText(sharedFile).Contains("fieldNotes") && SharedDataFile.FieldNotesText == "");

                // ---- the writer itself: escaping, caps
                var evil = new AtlasObs.FieldNote { Id = 5, Name = "a\"b\\c\nd]] os.execute(\"x\") --", Fam = "W\"olf", N = 3, Members = 1, Class = "rare", Min = 5, Max = 6 };
                evil.Taught["bite4"] = 1; evil.Taught["x\"] = 1 os.execute('y') --"] = 2; evil.Taught["Bite"] = 1;
                evil.Pts[7] = new List<AtlasObs.NotePoint> { new AtlasObs.NotePoint { X = 0.5, Y = 0.5, M = 1 }, new AtlasObs.NotePoint { X = 2, Y = 0.1, M = 1 }, new AtlasObs.NotePoint { X = double.NaN, Y = 0.1, M = 1 } };
                var lua = SharedDataFile.BuildFieldNotes(new[] { evil, null, new AtlasObs.FieldNote { Id = 0, N = 1 }, new AtlasObs.FieldNote { Id = 6 } });
                var gl = LuaData.ReadGlobals("ElansHubShared = {\n" + lua + "}\n");
                var en = gl?["ElansHubShared"] is Dictionary<string, object> eh ? eh.Child("fieldNotes")?.Child("npcs") : null;
                check("writer: a hostile name stays a string (quote, backslash, newline, ]] and a comment cannot end it), only the whitelisted ability tokens and valid spots are written",
                    en != null && en.Count == 1 && en.Child("5").Str("name") == "a\"b\\c d]] os.execute(\"x\") --" && en.Child("5").Str("fam") == "W\"olf" && en.Child("5").Child("taught").Count == 1 && en.Child("5").Child("taught").ContainsKey("bite4")
                    && en.Child("5").Child("pts").Child("7").Count == 1 && !lua.Contains("os.execute('y')"));
                var many = Enumerable.Range(1, 700).Select(i => new AtlasObs.FieldNote { Id = i, N = i, Members = i % 50, Last = i, Name = "Beast " + i }).ToList();
                var cap = SharedDataFile.BuildFieldNotes(many);
                var capG = LuaData.ReadGlobals("ElansHubShared = {\n" + cap + "}\n");
                var capNpcs = ((Dictionary<string, object>)capG["ElansHubShared"]).Child("fieldNotes").Child("npcs");
                check("writer: at most 500 beasts, the best supported and newest kept (no hunter-less beast over one that has 49)", capNpcs.Count == SharedDataFile.MaxFieldNoteNpcs && capNpcs.ContainsKey("49") && cap.Length < SharedDataFile.MaxFieldNotesBytes);
                check("writer: nothing to write is the empty string", SharedDataFile.BuildFieldNotes(null) == "" && SharedDataFile.BuildFieldNotes(new AtlasObs.FieldNote[0]) == "");
                check("writer: without notes Compose is byte for byte what it was (flights only)", SharedDataFile.Compose(null, now, null) == FlightShare.BuildLua(null, now));
            }
            finally
            {
                Session.Disconnect(); Session.AtlasResetForTest();
                AtlasQuery.Context.NowUtc = hadNow; AtlasQuery.Context.Lodge = hadLodge; AtlasQuery.Context.OwnFieldNotes = hadOwn; SharedDataFile.FieldNotesText = hadText;
                try { File.Delete(sentFile); } catch { }
                try { if (File.Exists(sharedFile)) File.Delete(sharedFile); } catch { }
                WriteAtlasHub("ACC1", AtlasLua(AtlasFixture()));
            }
            await Task.CompletedTask;
        }
    }
}
