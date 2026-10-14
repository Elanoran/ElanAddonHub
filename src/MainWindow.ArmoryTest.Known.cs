using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // The "armory" section, learned recipes (Outpost 2.52.0, server 2.19.0, feature `armory2`): what goes into the snapshot (the skill line and the recipe ids of the characters the
    // member ticked, capped at 2,000, only for a server that has armory2), the signature, the way back through ToChar, and the question "who can craft this" (one frame per
    // question, answers kept, junk rows dropped, nothing sent to an older server, a guest or while offline).
    public partial class MainWindow
    {
        void ArmoryKnownChecks(string dir, CheckFn Check, List<Dictionary<string, object>> sent)
        {
            long at = 1_790_000_000;
            var bob = KnownInv("Bob", "WARRIOR", 80, new[] { 1003, 1001 }, false, at);
            bob.Hub.Modules["core"] = new HubModule { Name = "core", V = 1, At = at, Data = D("name", "Bob", "level", 40.0, "class", "Warrior", "classFile", "WARRIOR") };

            // ---- the snapshot
            var plainWire = Json.Obj(Json.Write(ArmoryShare.Build(bob, true, false)));
            var pPlain = plainWire.Child("professions");
            Check("snapshot for a server without armory2: no skill line and no recipes (as before)", pPlain != null && !pPlain.ContainsKey("known") && !pPlain.List("list").OfType<Dictionary<string, object>>().Any(p => p.ContainsKey("line")));
            var wire = Json.Obj(Json.Write(ArmoryShare.Build(bob, true, true)));
            var pro = wire.Child("professions");
            var kn = pro?.Child("known")?.Child("197");
            Check("snapshot for armory2: the profession carries its skill line, and `known` holds the recipe ids sorted, the read's time, partial = false and the profession name",
                pro != null && pro.List("list").OfType<Dictionary<string, object>>().First().Int("line") == 197 && kn != null && kn.List("ids").Select(x => (int)(double)x).SequenceEqual(new[] { 1001, 1003 }) && kn.Long("at") == at && kn.TryGetValue("partial", out var pv) && pv is bool pb && !pb && kn.Str("n") == "Tailoring");
            var noTimes = Json.Write(ArmoryShare.Build(bob, false, true));
            Check("snapshot without times (for the signature) has no `at` inside `known`", !noTimes.Contains("\"at\""));
            var sigOff = ArmoryShare.Signature(ArmoryShare.Build(bob, false, false));
            var sigOn = ArmoryShare.Signature(ArmoryShare.Build(bob, false, true));
            var bobLater = KnownInv("Bob", "WARRIOR", 80, new[] { 1001, 1003 }, false, at + 600);
            bobLater.Hub.Modules["core"] = bob.Hub.Modules["core"];
            var bobMore = KnownInv("Bob", "WARRIOR", 80, new[] { 1001, 1003, 1004 }, false, at);
            bobMore.Hub.Modules["core"] = bob.Hub.Modules["core"];
            Check("signature: the recipes are part of it for armory2 (a new recipe re-uploads), a later read of the same recipes is not, and a server without armory2 is unaffected by them",
                sigOn != sigOff && ArmoryShare.Signature(ArmoryShare.Build(bobLater, false, true)) == sigOn && ArmoryShare.Signature(ArmoryShare.Build(bobMore, false, true)) != sigOn && ArmoryShare.Signature(ArmoryShare.Build(bobMore, false, false)) == sigOff);

            // caps: 3000 ids in one profession -> 2000; two professions of 1500 -> 2000 in all
            var huge = KnownInv("Huge", "MAGE", 80, Enumerable.Range(1, 3000).ToArray(), false, at);
            var capWire = Json.Obj(Json.Write(ArmoryShare.Build(huge, true, true)));
            Check("snapshot: at most 2,000 recipe ids per character (3000 in one profession)", capWire.Child("professions").Child("known").Child("197").List("ids").Count == 2000);
            var two = KnownInv("Two", "MAGE", 80, Enumerable.Range(1, 1500).ToArray(), false, at);
            var prof2 = (Dictionary<string, object>)two.Hub.Modules["professions"].Data["known"];
            prof2["185"] = D("at", (double)at, "partial", false, "n", "Cooking", "ids", KL(Enumerable.Range(5000, 1500).Select(i => (object)(double)i).ToArray()));
            var cap2 = Json.Obj(Json.Write(ArmoryShare.Build(two, true, true))).Child("professions").Child("known");
            Check("snapshot: the 2,000 are spread over the professions in order (the lowest skill line first: 1500 + 500)", cap2.Count == 2 && cap2.Child("185").List("ids").Count == 1500 && cap2.Child("197").List("ids").Count == 500, string.Join(",", cap2.Select(k => k.Key)));
            var back = ArmoryShare.ToChar("mia", wire);
            var bm = back == null ? null : HubModules.ProfessionsOf(back.Hub);
            Check("round trip: a member's shared character reads back with its skill line and recipes (the Characters page's Lodge view shows 'on record')", bm != null && bm.List[0].Line == 197 && bm.Known.ContainsKey(197) && bm.Known[197].Ids.SetEquals(new[] { 1001, 1003 }) && !bm.Known[197].Partial);

            // ---- the session
            var session = Session;
            var now = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);
            var hadClock = session.Clock; var hadOn = settings.ArmoryShareOn; var hadChars = settings.ArmoryChars; var hadRoot = settings.WowRoot;
            int changes = 0; Action onChange = () => changes++;
            session.CraftersChanged += onChange;
            try
            {
                session.Clock = () => now;
                session.ArmoryResetForTest();
                settings.ArmoryShareOn = true; settings.ArmoryChars = new List<string> { bob.Key };
                session.ArmorySourceForTest = () => new List<InvChar> { bob };

                // an older server (2.18: armory, no armory2)
                sent.Clear();
                session.FeedForTest(ArmoryWelcome("member", "2.18.0", true));
                var oldPut = AryFrames(sent, "armory.put");
                Check("server 2.18 (no armory2): the put goes without recipes and lines; the question is not available and nothing is sent",
                    !session.SupportsArmory2 && oldPut.Count == 1 && !Json.Write(oldPut[0]).Contains("\"known\"") && !Json.Write(oldPut[0]).Contains("\"line\""));
                sent.Clear();
                var cOld = session.CraftersFor(new[] { 1004 });
                Check("server 2.18: CraftersFor says why (older than 2.19) and sends nothing", !cOld.Available && cOld.Problem != null && cOld.Problem.Contains("2.19") && AryFrames(sent, "armory.crafters").Count == 0);

                // a server that has armory2
                session.ArmoryResetForTest();
                session.ArmorySourceForTest = () => new List<InvChar> { bob };
                sent.Clear();
                session.FeedForTest(FunWelcome("member", FunPeople(false), "", "\"reply\",\"react\",\"react2\",\"pin\",\"profile\",\"displayname\",\"lfg\",\"history2\",\"search\",\"armory\",\"armory2\"", "2.19.0"));
                var puts = AryFrames(sent, "armory.put");
                var sentKnown = puts.Count == 1 ? puts[0].Child("char")?.Child("professions")?.Child("known")?.Child("197") : null;
                Check("server 2.19 with armory2: the put carries the recipes of the ticked character (sorted ids, partial flag) and the skill line", session.SupportsArmory2 && sentKnown != null && sentKnown.List("ids").Select(x => (int)(double)x).SequenceEqual(new[] { 1001, 1003 })
                    && puts[0].Child("char").Child("professions").List("list").OfType<Dictionary<string, object>>().First().Int("line") == 197);
                Check("the frame stays under the 64 KB limit with 2,000 recipes", Json.Write(new Dictionary<string, object> { ["t"] = "armory.put", ["key"] = "Realm-Huge", ["char"] = ArmoryShare.Build(huge, true, true) }).Length < 60 * 1024);

                // asking
                sent.Clear();
                var c1 = session.CraftersFor(new[] { 1004, 1002, 1004 });
                var q1 = AryFrames(sent, "armory.crafters");
                Check("crafters: one question for the (sorted, distinct) spells, answer pending", c1.Available && c1.Pending && c1.Rows.Count == 0 && q1.Count == 1 && q1[0].List("spells").Select(x => (int)(double)x).SequenceEqual(new[] { 1002, 1004 }));
                session.CraftersFor(new[] { 1002, 1004 });
                Check("crafters: asking again before an answer does not repeat the question (retried after 10 seconds)", AryFrames(sent, "armory.crafters").Count == 1);
                now = now.AddSeconds(11);
                session.CraftersFor(new[] { 1002, 1004 });
                Check("crafters: after 10 seconds without an answer it is asked once more", AryFrames(sent, "armory.crafters").Count == 2);

                // the answer: junk dropped
                changes = 0;
                string row(object spell, string name, string extra = "") => "{\"spell\":" + spell + ",\"owner\":\"mia\",\"by\":\"Mia\",\"key\":\"Forever-Aria\",\"name\":" + (name == null ? "null" : "\"" + name + "\"") + ",\"class\":\"HUNTER\",\"level\":60,\"profession\":\"Tailoring\",\"skill\":150,\"max\":300" + extra + "}";
                session.FeedForTest("{\"t\":\"armory.crafters\",\"spells\":[1002,1004],\"who\":[" + row(1004, "Aria") + "," + row(999, "Elsewhere") + "," + row(1002, null) + ",5,\"x\"," + row(1002, "Bobby", ",\"mine\":true,\"partial\":true").Replace("\"level\":60", "\"level\":9999") + "," + row(0, "Zero") + "]}");
                var c2 = session.CraftersFor(new[] { 1004, 1002 });
                Check("crafters: the answer keeps the valid rows only (a spell that was not asked, no name, a spell of 0 and non-objects are dropped); level is capped at 100; the event fired once",
                    changes == 1 && !c2.Pending && c2.Rows.Count == 2 && c2.Rows.Any(r => r.Name == "Aria" && r.Spell == 1004 && r.By == "Mia" && r.Skill == 150 && r.Detail == "Tailoring 150/300 · level 60") && c2.Rows.Any(r => r.Name == "Bobby" && r.Mine && r.Partial && r.Level == 100),
                    changes + " " + string.Join(",", c2.Rows.Select(r => r.Name)));
                int before = AryFrames(sent, "armory.crafters").Count;
                session.CraftersFor(new[] { 1002, 1004 });
                Check("crafters: a fresh answer is used for two minutes without asking again", AryFrames(sent, "armory.crafters").Count == before);
                now = now.AddMinutes(3);
                var c3 = session.CraftersFor(new[] { 1002, 1004 });
                Check("crafters: after the answer got old the question is sent again, and the old rows are shown meanwhile", AryFrames(sent, "armory.crafters").Count == before + 1 && c3.Rows.Count == 2 && !c3.Pending);
                changes = 0;
                session.FeedForTest("{\"t\":\"armory.crafters\",\"spells\":[5],\"who\":[" + row(5, "Nobody") + "]}");
                Check("crafters: an answer to a question that was never asked is ignored", changes == 0 && session.CraftersFor(new[] { 5 }).Rows.Count == 0);

                // a guest and an offline session never ask
                sent.Clear();
                session.FeedForTest(FunWelcome("guest", FunPeople(false), "", "\"reply\",\"react\",\"react2\",\"pin\",\"profile\",\"displayname\",\"lfg\",\"history2\",\"search\",\"armory\",\"armory2\"", "2.19.0"));
                sent.Clear();
                var cg = session.CraftersFor(new[] { 1004 });
                Check("crafters: a guest gets the plain line about members and nothing is sent", !cg.Available && cg.Problem != null && cg.Problem.Contains("members") && AryFrames(sent, "armory.crafters").Count == 0, cg.Problem);
                Check("crafters: no recipe at all is a calm 'no recipe'", !session.CraftersFor(new int[0]).Available && !session.CraftersFor(null).Available);
            }
            finally
            {
                session.CraftersChanged -= onChange;
                session.ForgetCraftersForTest();
                session.Clock = hadClock; settings.ArmoryShareOn = hadOn; settings.ArmoryChars = hadChars; settings.WowRoot = hadRoot;
                session.ArmorySourceForTest = null;
            }
        }
    }
}
