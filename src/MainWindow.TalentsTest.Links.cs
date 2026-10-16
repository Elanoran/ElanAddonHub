using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ElansAddonHub.Services.Talents;

namespace ElansAddonHub
{
    // --selftest "talents", the build LINK part: links from the online calculators (Seemeta, WoW Forever Talent, Talents Forever) built here from the formats
    // (never copied from a site) and decoded against the fixture tree and, when there is a WoW install, against the real trees of all nine classes.
    public partial class MainWindow
    {
        // ---- the test's own encoders (written from the sites' formats; the nodes of a tree in the order the sites count them: row, then column)
        static List<TalentNode> TalSiteOrder(TalentTreeDef t, int spec, bool hideLeftover) =>
            t.Nodes.Where(n => n.Spec == spec && !(hideLeftover && t.ClassFile == "HUNTER" && n.SpellId == 19464)).OrderBy(n => n.Row).ThenBy(n => n.Col).ToList();

        static string[] TalDigits(TalentTreeDef t, TalentState s, bool hideLeftover, bool trim) =>
            Enumerable.Range(0, t.SpecCount).Select(sp =>
            {
                var d = string.Concat(TalSiteOrder(t, sp, hideLeftover).Select(n => s.Rank(n.Id).ToString()));
                return trim ? d.TrimEnd('0') : d;
            }).ToArray();

        static string TalSeemetaLink(TalentTreeDef t, TalentState s) =>
            "https://seemeta.com/en/wow-forever/talent-calculator/" + t.ClassFile.ToLowerInvariant() + "?build=" + string.Join("-", TalDigits(t, s, false, true)).TrimEnd('-') + "&level=60";

        static string TalWftLink(TalentTreeDef t, TalentState s) =>
            "https://wowforevertalent.com/" + t.ClassFile.ToLowerInvariant() + "/#b=1~beta-20261008-cc66e694~" + t.ClassFile.ToLowerInvariant() + "~60~" + string.Join("-", TalDigits(t, s, true, false));

        const string TalSyms = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz056789";

        // talentsforever.com: ranks, then (when an order is given) the order code: a symbol per talent, a run length when the run is not all of its ranks, then -7 (the trees of today's game)
        static string TalTfLink(TalentTreeDef t, TalentState s, List<TalentNode> order)
        {
            var flat = Enumerable.Range(0, t.SpecCount).SelectMany(sp => TalSiteOrder(t, sp, true)).ToList();
            var left = flat.ToDictionary(n => n.Id, n => s.Rank(n.Id));
            var code = new StringBuilder();
            for (int k = 0; order != null && k < order.Count;)
            {
                var node = order[k]; int run = 1;
                while (k + run < order.Count && order[k + run].Id == node.Id) run++;
                code.Append(TalSyms[flat.IndexOf(node)]);
                if (run < left[node.Id]) code.Append(run);
                left[node.Id] -= run; k += run;
            }
            return "https://talentsforever.com/" + t.ClassFile.ToLowerInvariant() + "/60/" + string.Join("-", TalDigits(t, s, true, true)) + (code.Length > 0 ? "-" + code : "") + "-7";
        }

        // a legal layout of `points` points, spent one point at a time over every talent that the rules allow, in tree order; `order` gets the points in that order
        static TalentState TalGenerate(TalentTreeDef t, int points, bool hideLeftover, List<TalentNode> order)
        {
            var s = new TalentState();
            var nodes = t.Nodes.Where(n => !(hideLeftover && t.ClassFile == "HUNTER" && n.SpellId == 19464)).OrderBy(n => n.Spec).ThenBy(n => n.Row).ThenBy(n => n.Col).ToList();
            bool moved = true;
            while (s.Total < points && moved)
            {
                moved = false;
                foreach (var n in nodes)
                {
                    if (s.Total >= points) break;
                    if (TalentRules.Add(t, s, n.Id, int.MaxValue) == Why.Ok) { moved = true; order?.Add(n); }
                }
            }
            return s;
        }

        // ============================================================================================================================ fixture tree
        void TalLinkChecks(CheckFn Check, TalentSet set, TalentTreeDef t, TalentsPage page)
        {
            Func<TalentNode, string> nm = n => TalSpellName(n.SpellId);
            // fixture Hunter: spec 0 = Alpha 0/5, Beta 0/2 (row 0), Gamma 0/3 (row 1), Delta 0/1 (row 2); spec 1 = Epsilon 0/1, Eta 0/5; spec 2 = Zeta 0/2
            var want = new TalentState(); foreach (var (id, r) in new[] { (10, 5), (11, 2), (12, 3), (13, 1), (14, 1), (17, 2) }) want.Set(id, r);

            // ---- Seemeta
            var sm = BuildLinks.Decode("https://seemeta.com/en/wow-forever/talent-calculator/hunter?build=5231-1-2&level=60&race=orc", set, nm);
            Check("links: Seemeta '...?build=5231-1-2' (digits per tree in row / column order, extra parameters ignored) gives Alpha 5, Beta 2, Gamma 3, Delta 1, Epsilon 1, Zeta 2 and names the site",
                sm.Ok && sm.Site == "Seemeta" && sm.Tree == t && sm.State.SameAs(want) && sm.Order == null && sm.Recognised, sm.Error);
            var sm2 = BuildLinks.Decode("look: www.seemeta.com/de/wow-forever/talent-calculator/hunter?build=5231-1-2. nice", set, nm);
            Check("links: Seemeta inside a sentence, with www, another language and no https, still decodes", sm2.Ok && sm2.State.SameAs(want), sm2.Error);
            var sm3 = BuildLinks.Decode("https://seemeta.com/en/wow-forever/talent-calculator/hunter?build=5", set, nm);
            Check("links: Seemeta with the zeros cut off at the end ('build=5') is Alpha 5 only", sm3.Ok && sm3.State.Total == 5 && sm3.State.Rank(10) == 5, sm3.Error);
            var sm4 = BuildLinks.Decode("https://seemeta.com/en/wow-forever/talent-calculator/hunter?build=--2", set, nm);
            Check("links: Seemeta '--2' (nothing in the first two trees) is Zeta 2", sm4.Ok && sm4.State.Total == 2 && sm4.State.Rank(17) == 2, sm4.Error);

            // ---- WoW Forever Talent
            var wf = BuildLinks.Decode("https://wowforevertalent.com/hunter/#b=1~beta-20261008-cc66e694~hunter~60~5231-10-2", set, nm);
            Check("links: WoW Forever Talent '#b=1~...~hunter~60~5231-10-2' (one digit per talent, nothing cut) gives the same build and names the site",
                wf.Ok && wf.Site == "WoW Forever Talent" && wf.State.SameAs(want) && wf.Order == null, wf.Error);
            var wf2 = BuildLinks.Decode("#b=1~beta-20261008-cc66e694~hunter~60~5231-10-2", set, nm);
            Check("links: the hash part alone (copied without the address) is found too", wf2.Ok && wf2.Site == "WoW Forever Talent" && wf2.State.SameAs(want), wf2.Error);
            var wf3 = BuildLinks.Decode("https://wowforevertalent.com/hunter/#b=1~beta-20261008-cc66e694~hunter~60~5231-1-2", set, nm);
            Check("links: WoW Forever Talent with a tree of the wrong length (the site writes every talent) is refused as 'different talent trees'", !wf3.Ok && wf3.Error.Contains("different talent trees"), wf3.Error);
            var wf4 = BuildLinks.Decode("https://wowforevertalent.com/hunter/#b=2~beta-20261008-cc66e694~hunter~60~5231-10-2", set, nm);
            Check("links: a newer layout number of WoW Forever Talent says to update the Outpost", !wf4.Ok && wf4.Error.Contains("newer"), wf4.Error);
            var wf5 = BuildLinks.Decode("https://wowforevertalent.com/hunter/#b=1~beta-20261008-cc66e694~warrior~60~5231-10-2", set, nm);
            Check("links: WoW Forever Talent whose address says hunter and whose build says warrior is called damaged", !wf5.Ok && wf5.Error.Contains("damaged"), wf5.Error);

            // ---- Talents Forever
            var tf = BuildLinks.Decode("https://talentsforever.com/hunter/60/5231-1-2-6", set, nm);
            Check("links: Talents Forever '/hunter/60/5231-1-2-6' gives the same build, the site named, no order in it", tf.Ok && tf.Site == "Talents Forever" && tf.State.SameAs(want) && tf.Order == null && tf.OrderNote == null, tf.Error);
            var tf2 = BuildLinks.Decode("https://talentsforever.com/hunter/60/5231-1-2-ABCDEG-6", set, nm);
            Check("links: Talents Forever with an order code: 14 steps, the first point at level 10, the order replayed through the rules",
                tf2.Ok && tf2.Order != null && tf2.Order.Steps.Count == 14 && tf2.Order.Steps[0].Level == 10 && tf2.Order.Steps[0].Name == "Alpha" && tf2.Order.State.SameAs(want) && tf2.OrderNote == null, tf2.Error + " " + tf2.OrderNote);
            var tfLines = tf2.Ok && tf2.Order != null ? BuildsRecommended.StepLines(tf2.Order) : new List<string>();
            Check("links: the steps read like a recommended build: 'levels 10-14: Alpha 1-5/5', 'levels 15-16: Beta 1-2/2' ...", tfLines.Count >= 4 && tfLines[0] == "levels 10-14: Alpha 1-5/5" && tfLines[1] == "levels 15-16: Beta 1-2/2" && tfLines.Contains("level 20: Delta 1/1"), string.Join(" | ", tfLines));
            var tf3 = BuildLinks.Decode("https://talentsforever.com/hunter/60/5231-1-2-A3BACDEG-6", set, nm);
            var tf3Lines = tf3.Ok && tf3.Order != null ? BuildsRecommended.StepLines(tf3.Order) : new List<string>();
            Check("links: a run length in the order code ('A3' = three points of Alpha, then Beta, then the rest of Alpha) is followed", tf3.Ok && tf3.Order != null && tf3Lines.Count > 2 && tf3Lines[0] == "levels 10-12: Alpha 1-3/5" && tf3Lines[2] == "levels 15-16: Alpha 4-5/5", string.Join(" | ", tf3Lines));
            var tf4 = BuildLinks.Decode("https://talentsforever.com/hunter/60/5231-1-2-DABCEG-6", set, nm);
            Check("links: an order the rules do not allow (Delta first) keeps the final talents, drops the order and says so", tf4.Ok && tf4.Order == null && tf4.State.SameAs(want) && tf4.OrderNote != null, tf4.Error + " " + tf4.OrderNote);
            var tf5 = BuildLinks.Decode("https://talentsforever.com/hunter/60/5231-1-2-AB-6", set, nm);
            Check("links: an order that does not cover every point is not used (final talents kept, a note says so)", tf5.Ok && tf5.Order == null && tf5.OrderNote != null && tf5.State.SameAs(want));
            var tf6 = BuildLinks.Decode("https://talentsforever.com/hunter/60/5231-1-2-5", set, nm);
            Check("links: a link made on older trees of that site (ending -5) is refused with words, not guessed", !tf6.Ok && tf6.Error.Contains("older talent trees"), tf6.Error);
            var tf7 = BuildLinks.Decode("https://talentsforever.com/hunter/60/5231-1-2", set, nm);
            Check("links: a Talents Forever link with no version at the end is refused the same way", !tf7.Ok && tf7.Error.Contains("older talent trees"), tf7.Error);
            var tf8 = BuildLinks.Decode("https://talentsforever.com/index.html#hunter/60/5231-1-2-6", set, nm);
            Check("links: the index.html#class/level/... form of the same site decodes too", tf8.Ok && tf8.State.SameAs(want), tf8.Error);
            var tf9 = BuildLinks.Decode("https://talentsforever.com/hunter/60/5231-1-2-0-0-0-6", set, nm);
            Check("links: Talents Forever legacy segments (three after the trees) are skipped, not mistaken for an order", tf9.Ok && tf9.State.SameAs(want) && tf9.Order == null, tf9.Error);
            // since 15 Oct 2026 the site writes -7 (the trees of build 70425); the Hunter's trees did not change, so -6 and -7 are the same build
            var tf10 = BuildLinks.Decode("https://talentsforever.com/hunter/60/5231-1-2-7", set, nm);
            var tf11 = BuildLinks.Decode("https://talentsforever.com/hunter/60/5231-1-2-ABCDEG-7?b=70425&d=261016", set, nm);
            Check("links: Talents Forever '-7' links decode to the same build as '-6' (with and without an order; the ?b=70425&d=261016 the site adds is ignored)",
                tf10.Ok && tf10.Site == "Talents Forever" && tf10.State.SameAs(want) && tf10.Order == null && tf11.Ok && tf11.State.SameAs(want) && tf11.Order != null && tf2.Order != null
                && tf11.Order.Steps.Select(x => x.NodeId).SequenceEqual(tf2.Order.Steps.Select(x => x.NodeId)), tf10.Error + " " + tf11.Error);
            var tf12 = BuildLinks.Decode("https://talentsforever.com/hunter/60/5231-1-2-8", set, nm);
            Check("links: a Talents Forever link on newer trees than this Outpost knows (-8) says to update the Outpost", !tf12.Ok && tf12.Error.Contains("newer") && tf12.Error.Contains("Update"), tf12.Error);

            // ---- not readable, not known, not a link
            var z = BuildLinks.Decode("https://www.zockify.com/forever/talents/#IQEBAQ", set, nm);
            Check("links: a Zockify link is recognised and refused plainly ('Links from Zockify cannot be read yet')", !z.Ok && z.Recognised && z.Site == "Zockify" && z.Error.StartsWith("Links from Zockify cannot be read yet"), z.Error);
            var wb = BuildLinks.Decode("https://wowforeverbuilds.com/talents/hunter?b=5231-1-2", set, nm);
            Check("links: a WoW Forever Builds link is recognised and refused plainly with the reason", !wb.Ok && wb.Recognised && wb.Error.StartsWith("Links from WoW Forever Builds cannot be read yet") && wb.Error.Contains("differ"), wb.Error);
            var iv = BuildLinks.Decode("https://www.icy-veins.com/wow-forever/hunter-talent-calculator#abc", set, nm);
            Check("links: an Icy Veins link is recognised and refused plainly", !iv.Ok && iv.Recognised && iv.Error.StartsWith("Links from Icy Veins cannot be read yet"), iv.Error);
            var unk = BuildLinks.Decode("https://example.com/talents/hunter?build=5231-1-2", set, nm);
            Check("links: a link of a site this Outpost does not know says so and names the sites it can read", !unk.Ok && !unk.Recognised && unk.Error.Contains("not from a talent calculator") && unk.Error.Contains("Seemeta"), unk.Error);
            var junk = BuildLinks.Decode("hello world", set, nm);
            Check("links: plain words are refused (not a code, not a link); empty and null are refused", !junk.Ok && !junk.Recognised && junk.Error.Contains("not a build code") && !BuildLinks.Decode("", set, nm).Ok && !BuildLinks.Decode(null, set, nm).Ok);
            var game = BuildLinks.Decode(new string('A', 60) + "BCDEFGHIJKLMNOPQRSTUVWXYZ0123456789abcdef", set, nm);
            Check("links: a long string like the game's own talent string is recognised and refused plainly (not guessed)", !game.Ok && game.Recognised && game.Error.Contains("copied from the game"), game.Error);

            // ---- damaged, wrong class, a talent that is not in this tree
            Check("links: Seemeta with characters that do not belong in the build is damaged", BuildLinks.Decode("https://seemeta.com/en/wow-forever/talent-calculator/hunter?build=5x31", set, nm) is var d1 && !d1.Ok && d1.Error.Contains("damaged"), d1.Error);
            Check("links: Seemeta with no build in the address says to pick talents and copy again", BuildLinks.Decode("https://seemeta.com/en/wow-forever/talent-calculator/hunter", set, nm) is var d2 && !d2.Ok && d2.Error.Contains("no build"), d2.Error);
            Check("links: Seemeta that is not the WoW Forever calculator is refused", BuildLinks.Decode("https://seemeta.com/en/wow/talent-calculator/hunter?build=5", set, nm) is var d3 && !d3.Ok && d3.Error.Contains("WoW Forever"), d3.Error);
            Check("links: rank 6 of a 5 rank talent is refused ('does not fit this game version')", BuildLinks.Decode("https://seemeta.com/en/wow-forever/talent-calculator/hunter?build=6", set, nm) is var d4 && !d4.Ok && d4.Error.Contains("does not fit") && d4.Error.Contains("rank 6"), d4.Error);
            Check("links: a layout the rules do not allow (Delta alone) is refused", BuildLinks.Decode("https://seemeta.com/en/wow-forever/talent-calculator/hunter?build=0001", set, nm) is var d5 && !d5.Ok && d5.Error.Contains("not a legal"), d5.Error);
            Check("links: a talent that is not in this tree (a 5th digit in a tree of 4 talents) refuses the whole link", BuildLinks.Decode("https://seemeta.com/en/wow-forever/talent-calculator/hunter?build=52311", set, nm) is var d6 && !d6.Ok && d6.Error.Contains("different talent trees"), d6.Error);
            Check("links: a fourth tree is refused", BuildLinks.Decode("https://seemeta.com/en/wow-forever/talent-calculator/hunter?build=5-1-2-3", set, nm) is var d7 && !d7.Ok && d7.Error.Contains("damaged"), d7.Error);
            Check("links: a class that is not in the game data on this PC is refused with words (Warrior, the fixture has the Hunter only)",
                BuildLinks.Decode("https://seemeta.com/en/wow-forever/talent-calculator/warrior?build=5", set, nm) is var d8 && !d8.Ok && d8.Error.Contains("not in the game data") && BuildLinks.Decode("https://talentsforever.com/mage/60/5-6", set, nm).Error.Contains("not in the game data"), d8.Error);
            Check("links: a class name that is none of the nine is refused", BuildLinks.Decode("https://seemeta.com/en/wow-forever/talent-calculator/dancer?build=5", set, nm) is var d9 && !d9.Ok && d9.Error.Contains("class"), d9.Error);
            Check("links: letters among Talents Forever's ranks are damaged", BuildLinks.Decode("https://talentsforever.com/hunter/60/5a31-1-2-6", set, nm) is var d10 && !d10.Ok && d10.Error.Contains("damaged"), d10.Error);
            Check("links: no tree set (game data not loaded) says so instead of failing", BuildLinks.Decode("https://seemeta.com/en/wow-forever/talent-calculator/hunter?build=5", new TalentSet(), nm).Error.Contains("not loaded"));
            Check("links: a very long text is refused without being read", !BuildLinks.Decode(new string('x', 5000), set, nm).Ok);

            // ---- the page: import tries the code first, then links; offers to save with the site in the notes
            page.SelectClass("HUNTER");
            page.Reset_ClickForTest();
            Check("page: a Seemeta link imports (14 points), the status names the site and offers to save", page.Import("https://seemeta.com/en/wow-forever/talent-calculator/hunter?build=5231-1-2") && page.SessionForTest.State.SameAs(want) && page.StatusForTest.Contains("from a Seemeta link") && page.StatusForTest.Contains("Save build"), page.StatusForTest);
            Check("page: the save boxes are filled in ('Hunter build from Seemeta' / 'From a Seemeta link.') and the detail names the site and says the link holds no order",
                page.SaveNameForTest == "Hunter build from Seemeta" && page.SaveNotesForTest.Contains("Seemeta") && page.DetailTextsForTest().Any(x => x.Contains("Seemeta link")) && page.DetailTextsForTest().Any(x => x.Contains("final talents only")), string.Join(" | ", page.DetailTextsForTest()));
            var before = page.SavedForTest.Items.Count;
            page.SaveForTest(page.SaveNameForTest, page.SaveNotesForTest);
            var kept = page.SavedForTest.Items.FirstOrDefault(b => b.Name == "Hunter build from Seemeta");
            Check("page: Save build keeps it: the notes carry the site, the code decodes to the same 14 points", kept != null && kept.Notes.Contains("Seemeta") && BuildCode.Decode(kept.Code, id => set.ForClass(id)).State.SameAs(want) && page.SavedForTest.Items.Count == before + 1);
            if (kept != null) { page.SavedForTest.Delete(kept); }
            page.Reset_ClickForTest();
            Check("page: a Talents Forever link with an order shows the leveling order under the build ('levels 10-14: Alpha 1-5/5')",
                page.Import("https://talentsforever.com/hunter/60/5231-1-2-ABCDEG-6") && page.DetailTextsForTest().Contains("levels 10-14: Alpha 1-5/5") && page.StatusForTest.Contains("leveling order") && page.SessionForTest.State.SameAs(want), page.StatusForTest);
            Check("page: a link that cannot be read is a red message and changes nothing", !page.Import("https://www.zockify.com/forever/talents/#IQEBAQ") && page.StatusIsErrorForTest && page.StatusForTest.Contains("Zockify") && page.SessionForTest.State.SameAs(want), page.StatusForTest);
            Check("page: our own code still works first, and a broken code keeps the code's own message (not 'not a link')",
                !page.Import("ETB1-hello") && page.StatusForTest.Contains("build code") && !page.StatusForTest.Contains("talent calculator"), page.StatusForTest);
            Check("page: plain words give one message that names both kinds (code or link)", !page.Import("banana") && page.StatusForTest.Contains("build code") && page.StatusForTest.Contains("link"), page.StatusForTest);

            // ---- the other way: Open in Seemeta (the format checked on the live site; the test opens nothing, a fake catches the address)
            const string sbase = "https://seemeta.com/en/wow-forever/talent-calculator/hunter";
            Check("seemeta out: the build on screen (5231-1-2) gives '" + sbase + "?build=5231-1-2', which decodes back to the same build",
                page.SeemetaUrlNow == sbase + "?build=5231-1-2" && BuildLinks.Decode(page.SeemetaUrlNow, set, nm) is var rt && rt.Ok && rt.State.SameAs(want), page.SeemetaUrlNow);
            var only2 = new TalentState(); only2.Set(17, 2);
            var onlyFirst = new TalentState(); onlyFirst.Set(10, 5);
            var gap = new TalentState(); gap.Set(10, 5); gap.Set(17, 2);
            Check("seemeta out: zeros at the end cut, empty trees at the end left out, an empty middle tree kept ('--2', '5', '5--2'), nothing spent = the class page alone",
                BuildLinks.SeemetaUrl(t, only2) == sbase + "?build=--2" && BuildLinks.SeemetaUrl(t, onlyFirst) == sbase + "?build=5" && BuildLinks.SeemetaUrl(t, gap) == sbase + "?build=5--2"
                && BuildLinks.SeemetaUrl(t, new TalentState()) == sbase && BuildLinks.SeemetaUrl(null, want) == null,
                BuildLinks.SeemetaUrl(t, only2) + " " + BuildLinks.SeemetaUrl(t, gap));
            var oldOpen = TalentsPage.OpenWebLink; string opened = null;
            TalentsPage.OpenWebLink = u => opened = u;
            try { page.SeemetaClickForTest(); } finally { TalentsPage.OpenWebLink = oldOpen; }
            Check("seemeta out: the 'Open in Seemeta' button hands that address to the Outpost browser (class and ranks only, no character or account in it)",
                opened == sbase + "?build=5231-1-2" && !opened.Contains("&"), opened);
            page.Reset_ClickForTest();
        }

        // ============================================================================================================================ the real trees
        void TalLinkReal(CheckFn Check, List<string> notes, TalentSet set, Func<TalentNode, string> nameOf)
        {
            var classes = set.Trees.OrderBy(t => t.ClassFile).ToList();
            int fails = 0, builds = 0;
            var failed = new List<string>();
            foreach (var t in classes)
            {
                foreach (var points in new[] { 12, 31, 51 })
                {
                    // seemeta counts every talent of the game's tree; the other two leave out the Hunter's leftover node
                    var sFull = TalGenerate(t, points, false, null);
                    var ord = new List<TalentNode>();
                    var sHide = TalGenerate(t, points, true, ord);
                    var r1 = BuildLinks.Decode(TalSeemetaLink(t, sFull), set, nameOf);
                    var r2 = BuildLinks.Decode(TalWftLink(t, sHide), set, nameOf);
                    var r3 = BuildLinks.Decode(TalTfLink(t, sHide, null), set, nameOf);
                    var r4 = BuildLinks.Decode(TalTfLink(t, sHide, ord), set, nameOf);
                    builds += 4;
                    if (!(r1.Ok && r1.State.SameAs(sFull) && r1.Tree == t)) { fails++; failed.Add(t.ClassFile + " " + points + " Seemeta: " + r1.Error); }
                    if (!(r2.Ok && r2.State.SameAs(sHide))) { fails++; failed.Add(t.ClassFile + " " + points + " WoW Forever Talent: " + r2.Error); }
                    if (!(r3.Ok && r3.State.SameAs(sHide) && r3.Order == null)) { fails++; failed.Add(t.ClassFile + " " + points + " Talents Forever: " + r3.Error); }
                    if (!(r4.Ok && r4.State.SameAs(sHide) && r4.Order != null && r4.Order.Steps.Count == sHide.Total && r4.Order.Steps.Select(x => x.NodeId).SequenceEqual(ord.Select(x => x.Id)))) { fails++; failed.Add(t.ClassFile + " " + points + " Talents Forever order: " + r4.Error + " " + r4.OrderNote); }
                }
            }
            foreach (var f in failed.Take(6)) notes.Add("note real links: " + f);
            Check("real game files, links: " + classes.Count + " classes x 3 sizes (12, 31, 51 points) x Seemeta / WoW Forever Talent / Talents Forever (with and without the order) = " + builds + " links read back to exactly the same talents", fails == 0 && classes.Count == 9, fails + " failed");

            var h = set.ForClassFile("HUNTER");
            if (h != null)
            {
                var mm = h.Nodes.Where(n => n.Spec == 1).ToList();
                var leftover = mm.FirstOrDefault(n => n.SpellId == 19464);
                Check("real game files, links: the Hunter's Marksmanship has 17 talents in the game's data; Seemeta counts all 17, the other two sites count 16 (without the leftover Improved Serpent Sting node)",
                    mm.Count == 17 && leftover != null && TalSiteOrder(h, 1, false).Count == 17 && BuildLinks.SiteNodes(h, 1, false).Count == 17 && BuildLinks.SiteNodes(h, 1, true).Count == 16 && !BuildLinks.SiteNodes(h, 1, true).Contains(leftover), mm.Count + " leftover " + (leftover != null));
                // a talent after the leftover in the row / column order lands on the right node on the sites that leave it out
                var big = TalGenerate(h, 51, true, null);
                var withLast = BuildLinks.Decode(TalWftLink(h, big), set, nameOf);
                Check("real game files, links: on the Hunter a link from a site that leaves the leftover out puts every Marksmanship rank on the node it was made for", withLast.Ok && TalSiteOrder(h, 1, true).All(n => withLast.State.Rank(n.Id) == big.Rank(n.Id)) && withLast.State.Rank(leftover.Id) == 0);
            }
            // ---- Talents Forever -7 and -6 on the real trees (build 70425). Links copied from the site's own Popular builds (October 2026); the -6 forms are the
            // same builds written in the site's -6 order (its TALENT_ORDER_V6), the order codes the same points in the same sequence. No network.
            if (classes.Count == 9)
            {
                var siteV7 = new[]
                {
                    "warrior/60/0502-05-250533120321021351-7", "paladin/60/50003-503-05225131201331321-7", "hunter/60/5-005005001-500230230250222151-7",
                    "rogue/60/00530310501-32300320200515231--7", "priest/60/525335031030001001-30515100202--7", "shaman/60/3500331023121351-050002-053200001-7",
                    "mage/60/0552205023100311531-03--7", "warlock/60/05520323035201351-0050203001--7", "druid/60/050022-55000032021032212051-05003-7",
                };
                var bad7 = siteV7.Select(c => (c, d: BuildLinks.Decode("https://talentsforever.com/" + c + "?b=70425&d=261016", set, nameOf))).Where(x => !(x.d.Ok && x.d.State.Total == x.c.Split('/')[2].Where(char.IsDigit).Sum(ch => ch - '0') - 7 && x.d.State.Total > 30)).Select(x => x.c + ": " + x.d.Error).ToList();
                Check("real game files, links: a -7 Talents Forever build of each of the nine classes (from the site) reads as a legal build with every point on the game's trees", bad7.Count == 0, string.Join(" | ", bad7));

                // the talents build 70425 added sit where the site counts them: a -7 rank lands on the talent the site names
                Func<string, string, int, string> at = (c, link, tr) =>
                {
                    var d = BuildLinks.Decode("https://talentsforever.com/" + link, set, nameOf); var t = set.ForClassFile(c);
                    return d.Ok ? string.Join(",", BuildLinks.SiteNodes(t, tr, true).Where(n => d.State.Rank(n.Id) > 0).Select(n => nameOf(n) + "=" + d.State.Rank(n.Id))) : d.Error;
                };
                var pShadow = at("PRIEST", "priest/60/--50052201251312251-7", 2);
                var mArcane = at("MAGE", "mage/60/0552205023100311531-03--7", 0);
                Check("real game files, links: -7 ranks land on the talents the site shows (Priest Shadow 'Dark Inspiration=5', Mage Arcane 'Missile Barrage=2' and 'Arcane Wrath=1')",
                    pShadow.Contains("Dark Inspiration=5") && pShadow.StartsWith("Shadow Focus=5,Improved Mind Blast=5,") && mArcane.Contains("Missile Barrage=2") && mArcane.Contains("Arcane Wrath=1"), pShadow + " / " + mArcane);

                // -6 links (made before 15 Oct) still open: the Priest, Shaman, Mage and Warlock talents that moved are carried to where they sit now
                var pairs = new[]
                {
                    ("priest/60/525335031030001001-30515100202--7", "priest/60/525330031305001001-30515100202--6"),
                    ("shaman/60/0530031-055030031005112251-05-7", "shaman/60/0500331-055030031005112251-00005-6"),
                    ("mage/60/2551225223122301531-13--7", "mage/60/255225223122311531-13--6"),
                    ("warlock/60/05500320035201351-0050203001--7", "warlock/60/05500320035201351-0050203001--6"),
                    ("hunter/60/5-005005001-500230230250222151-7", "hunter/60/5-005005001-500230230250222151-6"),
                    ("shaman/60/0530031-055030031005112251-05-BCFGRSUXYbcdefghj-7", "shaman/60/0500331-055030031005112251-00005-BFEGRSUXYbcdefghm-6"),
                    ("priest/60/525335031030001001-30515100202--ABCDEFHIKORSUVWXac-7", "priest/60/525330031305001001-30515100202--ABCDELHIJORSUVWXac-6"),
                };
                var badPairs = new List<string>();
                foreach (var (v7, v6) in pairs)
                {
                    var a = BuildLinks.Decode("https://talentsforever.com/" + v7, set, nameOf); var b = BuildLinks.Decode("https://talentsforever.com/" + v6, set, nameOf);
                    bool same = a.Ok && b.Ok && a.State.Total > 0 && a.State.SameAs(b.State) && (a.Order == null) == (b.Order == null) && b.OrderNote == a.OrderNote
                        && (a.Order == null || a.Order.Steps.Select(x => x.NodeId).SequenceEqual(b.Order.Steps.Select(x => x.NodeId)));
                    if (!same) badPairs.Add(v6 + ": " + a.Error + b.Error + " " + a.OrderNote + " " + b.OrderNote);
                }
                Check("real game files, links: the same build as a -6 and as a -7 Talents Forever link imports to the same talents (Priest, Shaman, Mage and Warlock moved; with the leveling order too)",
                    badPairs.Count == 0, string.Join(" | ", badPairs));
                var withOrder = BuildLinks.Decode("https://talentsforever.com/" + pairs[5].Item2, set, nameOf);
                Check("real game files, links: the -6 Shaman link's leveling order is used (no note)", withOrder.Ok && withOrder.Order != null && withOrder.OrderNote == null, withOrder.Error + " " + withOrder.OrderNote);
                var gone = BuildLinks.Decode("https://talentsforever.com/priest/60/--00000000002-6", set, nameOf);
                Check("real game files, links: a -6 link with points in a talent build 70425 removed (Improved Fade) is refused with its name, not guessed", !gone.Ok && gone.Error.Contains("Improved Fade") && gone.Error.Contains("no longer"), gone.Error);
            }

            // a link with one more digit than a real tree has is refused on the real trees too
            var warrior = set.ForClassFile("WARRIOR");
            if (warrior != null)
            {
                var four = TalDigits(warrior, new TalentState(), true, false); four[0] += "0";
                var tooLong = BuildLinks.Decode("https://wowforevertalent.com/warrior/#b=1~beta-20261008-cc66e694~warrior~60~" + string.Join("-", four), set, nameOf);
                Check("real game files, links: a link with one talent more than the real tree has is refused ('different talent trees')", !tooLong.Ok && tooLong.Error.Contains("different talent trees"), tooLong.Error);
                var wrongClass = BuildLinks.Decode("https://wowforevertalent.com/warrior/#b=1~beta-20261008-cc66e694~mage~60~" + string.Join("-", TalDigits(warrior, new TalentState(), true, false)), set, nameOf);
                Check("real game files, links: a warrior address with a mage build is refused as damaged", !wrongClass.Ok && wrongClass.Error.Contains("damaged"), wrongClass.Error);
            }
        }
    }
}
