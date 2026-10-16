using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ElansAddonHub.Services.Talents
{
    // Build LINKS from the online talent calculators: a link a person pastes is decoded from the text alone (never fetched) and laid onto OUR trees, which
    // come from the person's own game files. Each site writes the ranks of a build as digits in a fixed talent order; that order is the one the sites show:
    // the talents of a tree sorted by row, then column (what the game's tree looks like read left to right, top to bottom). Verified against each site's own
    // public tree data (the same talents in the same positions as the game's trees), which is also why a link only decodes when it lines up with our tree.
    //
    //   seemeta.com            .../wow-forever/talent-calculator/<class>?build=<tree1>-<tree2>-<tree3>   (digits, trailing zeros cut; &level= and &race= ignored)
    //   wowforevertalent.com   .../<class>/#b=1~<tree version>~<class>~<level>~<tree1>-<tree2>-<tree3>   (digits, one per talent, nothing cut)
    //   talentsforever.com     .../<class>/<level>/<tree1>-<tree2>-<tree3>[-legacy x3][-order]-7        (digits, trailing zeros cut; the order is a symbol per talent; -6 = older trees)
    //
    // Only the talentsforever.com link holds the order the points were spent in; it is replayed through the rules like a recommended build. The other links hold
    // the final ranks only. Sites seen but NOT readable are named with the reason (Zockify numbers talents with its own ids; WoW Forever Builds lists different
    // talents than the game for several classes, so its digits would land on the wrong talents). Nothing is guessed: a link that does not line up is refused with words.
    // a build opened from a link, as the page shows it
    public sealed class LinkBuild
    {
        public string Site = "", ClassFile = "", Name = "", OrderNote;
        public BuildApplication Order;
    }

    public static class BuildLinks
    {
        public sealed class Decoded
        {
            public bool Ok;
            public bool Recognised;            // the text is a link (or string) of a site this class knows, even when it cannot be read
            public string Site = "";           // plain name of the site, "" when not recognised
            public string Error;               // plain words for the person
            public TalentTreeDef Tree;
            public TalentState State;
            public BuildApplication Order;     // the order the points were spent in (null = the link holds the final ranks only)
            public string OrderNote;           // why an order in the link was not used, or null
            public int Dropped;                // always 0 for links: a talent that does not exist refuses the whole link
        }

        sealed class Site
        {
            public string Name, Unreadable;
            public string[] Hosts;
            public Func<Uri, Func<string, TalentTreeDef>, Func<TalentNode, string>, int, Decoded> Read;
        }

        const string SeemetaName = "Seemeta", WftName = "WoW Forever Talent", TfName = "Talents Forever";

        static readonly Site[] Sites =
        {
            new Site { Name = SeemetaName, Hosts = new[] { "seemeta.com" }, Read = ReadSeemeta },
            new Site { Name = WftName, Hosts = new[] { "wowforevertalent.com" }, Read = ReadWft },
            new Site { Name = TfName, Hosts = new[] { "talentsforever.com" }, Read = ReadTf },
            new Site { Name = "Zockify", Hosts = new[] { "zockify.com" }, Unreadable = "it numbers the talents with its own ids, which cannot be matched to the game's talents" },
            new Site { Name = "WoW Forever Builds", Hosts = new[] { "wowforeverbuilds.com" }, Unreadable = "its lists of talents differ from the game's for several classes, so the points could land on the wrong talents" },
            new Site { Name = "WoW Forever Talents", Hosts = new[] { "wowforevertalents.org" }, Unreadable = "its link format is not known" },
            new Site { Name = "Icy Veins", Hosts = new[] { "icy-veins.com" }, Unreadable = "its link format is not known" },
            new Site { Name = "RankedBoost", Hosts = new[] { "rankedboost.com" }, Unreadable = "its link format is not known" },
            new Site { Name = "Wowhead", Hosts = new[] { "wowhead.com" }, Unreadable = "its link format is not known" },
            new Site { Name = "WoW Forever", Hosts = new[] { "wowforever.gg" }, Unreadable = "its link format is not known" },
        };

        // class names in links -> class file
        static readonly Dictionary<string, string> ClassFiles = new[] { "warrior", "paladin", "hunter", "rogue", "priest", "shaman", "mage", "warlock", "druid" }
            .ToDictionary(c => c, c => c.ToUpperInvariant());

        // Talents the game's data has but the calculators leave out (a leftover with a bogus position in the game's tables): by class file and spell id.
        // Seemeta's data includes it, the other calculators' do not, and their digit strings count without it.
        static readonly HashSet<string> LeftOut = new HashSet<string> { "HUNTER:19464" };

        const string Syms = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz056789";     // talentsforever.com: one symbol per talent, flat across the trees

        // the talents of one tree in the order the calculators count them
        public static List<TalentNode> SiteNodes(TalentTreeDef tree, int spec, bool leftOut)
        {
            return tree.Nodes.Where(n => n.Spec == spec && !(leftOut && LeftOut.Contains(tree.ClassFile + ":" + n.SpellId)))
                .OrderBy(n => n.Row).ThenBy(n => n.Col).ToList();
        }

        // ---- the entry point: `text` is what the person pasted; nameOf names a node for the leveling steps (null = "talent <id>")
        public static Decoded Decode(string text, TalentSet set, Func<TalentNode, string> nameOf = null, int legacyRanks = 0)
        {
            var d = new Decoded();
            text = (text ?? "").Trim();
            if (text.Length == 0) { d.Error = "Nothing to import."; return d; }
            if (text.Length > 4000) { d.Error = "That is too long to be a build link."; return d; }
            Func<string, TalentTreeDef> find = file => set?.ForClassFile(file);
            var uri = FindLink(text, out var site);
            if (uri == null || site == null)
            {
                if (LooksLikeLoadout(text))
                {
                    d.Recognised = true; d.Site = "the game";
                    d.Error = "That looks like a talent string copied from the game. Those cannot be read yet. Paste a build link from a talent calculator or an ETB1- code instead.";
                    return d;
                }
                if (uri != null) { d.Error = "That link is not from a talent calculator this Outpost knows. Links from Seemeta, WoW Forever Talent and Talents Forever can be read."; return d; }
                d.Error = "That is not a build code (ETB1-...) or a link from a talent calculator. Links from Seemeta, WoW Forever Talent and Talents Forever can be read.";
                return d;
            }
            d.Recognised = true; d.Site = site.Name;
            if (site.Unreadable != null) { d.Error = "Links from " + site.Name + " cannot be read yet: " + site.Unreadable + "."; return d; }
            if (set == null || set.Trees.Count == 0) { d.Error = "The talent trees are not loaded yet."; return d; }
            try
            {
                var r = site.Read(uri, find, nameOf, legacyRanks);
                r.Recognised = true; r.Site = site.Name;
                return r;
            }
            catch (Exception)
            {
                d.Error = "The " + site.Name + " link is damaged."; return d;
            }
        }

        // a link anywhere in a text: the first http(s) link of a known site, or a bare "host/path" of one; also the hash part alone of a wowforevertalent.com link
        static Uri FindLink(string text, out Site site)
        {
            site = null;
            Uri first = null;
            foreach (Match m in Regex.Matches(text, @"(?:(?:https?://)|(?<![A-Za-z0-9.-]))(?:[A-Za-z0-9-]+\.)+[A-Za-z]{2,}(?:[:/?#][^\s""'<>]*)?", RegexOptions.IgnoreCase))
            {
                var t = m.Value.TrimEnd('.', ',', ';', ')', ']', '}');
                if (!t.StartsWith("http", StringComparison.OrdinalIgnoreCase)) t = "https://" + t;
                if (!Uri.TryCreate(t, UriKind.Absolute, out var u) || (u.Scheme != "https" && u.Scheme != "http")) continue;
                if (first == null) first = u;
                var host = u.Host.ToLowerInvariant(); if (host.StartsWith("www.")) host = host.Substring(4);
                var s = Sites.FirstOrDefault(x => x.Hosts.Any(h => host == h || host.EndsWith("." + h)));
                if (s != null) { site = s; return u; }
            }
            // "#b=1~beta-...~hunter~60~0000-..." pasted without the address
            var hm = Regex.Match(text, @"#?b=1~[a-z0-9-]+~[a-z]+~\d+~[0-9-]+");
            if (hm.Success && Uri.TryCreate("https://wowforevertalent.com/#" + hm.Value.TrimStart('#'), UriKind.Absolute, out var hu)) { site = Sites[1]; return hu; }
            return first;
        }

        // a loadout string of the game: one long token of base64 characters (the client has C_Traits.GenerateImportString), never a word or a sentence
        static bool LooksLikeLoadout(string text) => Regex.IsMatch(text, @"^[A-Za-z0-9+/=]{70,600}$") && !text.StartsWith(BuildCode.Prefix, StringComparison.OrdinalIgnoreCase);

        // ============================================================================================================================ shared pieces
        static Decoded Fail(string site, string words) => new Decoded { Site = site, Error = words };

        static string LastPart(string path) => path.Trim('/').Split('/').LastOrDefault() ?? "";

        static Decoded NoTree(string site, string cls) => Fail(site, "This build is for a class (" + cls + ") that is not in the game data on this PC.");

        // digit strings per tree -> ranks on our nodes. `exact`: every tree's string must have exactly one digit per talent (wowforevertalent.com)
        static Decoded Place(string site, TalentTreeDef tree, string[] trees, bool leftOut, bool exact, out List<List<TalentNode>> lists)
        {
            lists = new List<List<TalentNode>>();
            for (int s = 0; s < tree.SpecCount; s++) lists.Add(SiteNodes(tree, s, leftOut));
            if (trees.Length > lists.Count) return Fail(site, "The " + site + " link has more talent trees than this game has (" + trees.Length + " of " + lists.Count + ").");
            var st = new TalentState();
            for (int s = 0; s < lists.Count; s++)
            {
                var digits = s < trees.Length ? trees[s] : "";
                if (digits.Length > lists[s].Count || exact && digits.Length != lists[s].Count)
                    return Fail(site, "The " + site + " link was made for different talent trees than this game has (the " + TreeName(tree, s) + " tree has " + lists[s].Count + " talents, the link has " + digits.Length + ").");
                for (int i = 0; i < digits.Length; i++)
                {
                    int rank = digits[i] - '0';
                    if (digits[i] < '0' || digits[i] > '9') return Fail(site, "The " + site + " link is damaged (it has characters that do not belong).");
                    if (rank == 0) continue;
                    var node = lists[s][i];
                    if (rank > node.MaxRanks) return Fail(site, "The " + site + " link does not fit this game version (" + NodeName(node) + " cannot have rank " + rank + ").");
                    st.Set(node.Id, rank);
                }
            }
            if (st.Total > tree.MaxPoints && tree.MaxPoints > 0) return Fail(site, "The " + site + " link spends more points than this game gives.");
            if (!TalentRules.Valid(tree, st, out var bad)) return Fail(site, "That " + site + " build is not a legal talent layout for this game version.");
            return new Decoded { Ok = true, Tree = tree, State = st };
        }

        static string TreeName(TalentTreeDef t, int spec) => spec < t.SpecNames.Length ? t.SpecNames[spec] : "tree " + (spec + 1);
        static string NodeName(TalentNode n) => n.Entry != null && !string.IsNullOrEmpty(n.Entry.NameOverride) ? n.Entry.NameOverride : "talent " + n.Id;

        static string[] SplitTrees(string s) => s.Split('-');

        // ============================================================================================================================ seemeta.com
        // https://seemeta.com/en/wow-forever/talent-calculator/hunter?build=5000-1-23&level=60
        static Decoded ReadSeemeta(Uri u, Func<string, TalentTreeDef> find, Func<TalentNode, string> nameOf, int legacy)
        {
            if (u.AbsolutePath.IndexOf("/wow-forever/talent-calculator", StringComparison.OrdinalIgnoreCase) < 0)
                return Fail(SeemetaName, "Only Seemeta's WoW Forever talent calculator links can be read.");
            var slug = LastPart(Uri.UnescapeDataString(u.AbsolutePath)).ToLowerInvariant();
            if (!ClassFiles.TryGetValue(slug, out var file)) return Fail(SeemetaName, "That Seemeta link does not name a class (it should end in /hunter, /mage ...).");
            var q = Query(u);
            if (!q.TryGetValue("build", out var build) || build.Length == 0) return Fail(SeemetaName, "That Seemeta link has no build in it (open the calculator, pick talents, and copy the address again).");
            if (!Regex.IsMatch(build, @"^[0-9-]{1,80}$")) return Fail(SeemetaName, "The Seemeta link is damaged (the build has characters that do not belong).");
            var tree = find(file);
            if (tree == null) return NoTree(SeemetaName, slug);
            var parts = SplitTrees(build);
            if (parts.Length > 3) return Fail(SeemetaName, "The Seemeta link is damaged (too many trees).");
            var d = Place(SeemetaName, tree, parts, false, false, out _);
            return d;
        }

        // the other way: a Seemeta link for a build on OUR trees, in the format ReadSeemeta reads (checked on the live site, October 2026: ?build=1 is one
        // point in the first Beast Mastery talent, ?build=55-05-5 shows "20 of 51, 10/5/5"). Digits in SiteNodes order, trailing zeros cut, empty trees at the
        // end left out; no build at all gives the class page alone. Only the class and the ranks go into the address.
        public const string SeemetaBase = "https://seemeta.com/en/wow-forever/talent-calculator/";

        public static string SeemetaUrl(TalentTreeDef tree, TalentState state)
        {
            if (tree == null || string.IsNullOrEmpty(tree.ClassFile)) return null;
            var slug = tree.ClassFile.ToLowerInvariant();
            if (!ClassFiles.ContainsKey(slug)) return null;
            var parts = new List<string>();
            for (int s = 0; s < tree.SpecCount; s++)
            {
                var sb = new StringBuilder();
                foreach (var n in SiteNodes(tree, s, false)) sb.Append((char)('0' + Math.Max(0, Math.Min(9, state?.Rank(n.Id) ?? 0))));
                parts.Add(sb.ToString().TrimEnd('0'));
            }
            while (parts.Count > 0 && parts[parts.Count - 1].Length == 0) parts.RemoveAt(parts.Count - 1);
            var url = SeemetaBase + slug;
            return parts.Count == 0 ? url : url + "?build=" + string.Join("-", parts);
        }

        // ============================================================================================================================ wowforevertalent.com
        // https://wowforevertalent.com/hunter/#b=1~beta-20261008-cc66e694~hunter~60~0000000000000000-0000000000000000-000000000000000000
        static readonly Regex WftHash = new Regex(@"^#?b=(\d+)~([a-z0-9-]+)~([a-z]+)~(\d{2})~([0-9]+)-([0-9]+)-([0-9]+)$", RegexOptions.IgnoreCase);

        static Decoded ReadWft(Uri u, Func<string, TalentTreeDef> find, Func<TalentNode, string> nameOf, int legacy)
        {
            var frag = Uri.UnescapeDataString(u.Fragment ?? "");
            if (frag.Length <= 1) return Fail(WftName, "That WoW Forever Talent link has no build in it (pick talents, and copy the address again).");
            var m = WftHash.Match(frag);
            if (!m.Success) return Fail(WftName, "The WoW Forever Talent link is damaged or has a layout this Outpost does not know.");
            if (m.Groups[1].Value != "1") return Fail(WftName, "This WoW Forever Talent link has a newer layout than this Outpost knows. Update the Outpost to open it.");
            var cls = m.Groups[3].Value.ToLowerInvariant();
            var pathSlug = LastPart(u.AbsolutePath).ToLowerInvariant();
            if (ClassFiles.ContainsKey(pathSlug) && pathSlug != cls) return Fail(WftName, "The WoW Forever Talent link is damaged (the address says " + pathSlug + ", the build says " + cls + ").");
            if (!ClassFiles.TryGetValue(cls, out var file)) return Fail(WftName, "The WoW Forever Talent link does not name a class this game has.");
            var tree = find(file);
            if (tree == null) return NoTree(WftName, cls);
            return Place(WftName, tree, new[] { m.Groups[5].Value, m.Groups[6].Value, m.Groups[7].Value }, true, true, out _);
        }

        // ============================================================================================================================ talentsforever.com
        // https://talentsforever.com/hunter/60/5555-0-2-7      or with the order: .../hunter/60/55-5-23-AB2C-7   (legacy segments are three more between)
        // The last segment is the generation of the site's trees the digits count on. -7 (15 Oct 2026) counts on the trees of build 70425; -6 on the trees
        // before it, which differ for the Priest, Shaman, Mage and Warlock only. A ?b=<build>&d=<date> query may follow; it is not part of the build.
        static readonly Regex TfPath = new Regex(@"([a-z]+)/(\d+)/([0-9A-Za-z-]*)$", RegexOptions.IgnoreCase);
        const int TfNow = 7;

        // -6 -> -7: where each talent of a -6 tree sits in the -7 trees (tree * 100 + index in the site's order), or the name of a talent build 70425 removed;
        // "=" is a tree that did not change. Read from the site's own tables (TALENT_ORDER_V6 against its current trees, renames followed), October 2026.
        static readonly Dictionary<string, string> TfV6Moves = new Dictionary<string, string>
        {
            { "PRIEST", "0,1,2,3,4,9,6,7,8,10,11,5,13,12,14,15,16,17|=|200,201,202,Shadow Affinity,204,205,203,206,207,208,Improved Fade,210,211,212,213,214,215,216" },
            { "SHAMAN", "0,1,7,3,5,2,6,4,8,9,Call of Thunder,11,13,10,14,12|=|200,204,202,203,201,205,206,207,208,209,210,211,212,213,214,215" },
            { "MAGE", "0,1,2,4,5,6,7,8,9,10,11,12,13,3,15,16,17,18|=|=" },
            { "WARLOCK", "0,1,2,Malediction,4,5,6,7,8,9,10,11,12,13,14,15,16|=|=" },
        };

        // a -6 link's tree digits and order code carried to the -7 positions; null + words when a point sits on a talent that is gone
        static string[] TfFromV6(TalentTreeDef tree, string[] trees, List<List<TalentNode>> lists, ref string order, out string error)
        {
            error = null;
            if (!TfV6Moves.TryGetValue(tree.ClassFile, out var spec)) return trees;
            var moves = spec.Split('|').Select((t, s) => t == "=" ? Enumerable.Range(0, lists[s].Count).Select(i => (s * 100 + i).ToString()).ToArray() : t.Split(',')).ToArray();
            var flatOld = moves.SelectMany(t => t).ToList();
            var ranks = lists.Select(l => Enumerable.Repeat('0', l.Count).ToArray()).ToArray();
            for (int s = 0; s < trees.Length && s < moves.Length; s++)
            {
                if (trees[s].Length > moves[s].Length) { error = "The " + TfName + " link was made for different talent trees than this game has (the " + TreeName(tree, s) + " tree has more talents than the site had)."; return null; }
                for (int i = 0; i < trees[s].Length; i++)
                {
                    if (trees[s][i] == '0') continue;
                    if (!int.TryParse(moves[s][i], out var at)) { error = "This " + TfName + " link was made on older talent trees and has points in " + moves[s][i] + ", which the game no longer has. Open it on the site, then copy the address again."; return null; }
                    if (at / 100 >= ranks.Length || at % 100 >= ranks[at / 100].Length) { error = "The " + TfName + " link was made for different talent trees than this game has."; return null; }
                    ranks[at / 100][at % 100] = trees[s][i];
                }
            }
            if (order.Length > 0)
            {
                var sb = new StringBuilder();
                for (int k = 0; k < order.Length; k++)
                {
                    int f = Syms.IndexOf(order[k]);
                    bool run = k + 1 < order.Length && order[k + 1] >= '1' && order[k + 1] <= '4';
                    if (f >= 0 && f < flatOld.Count && int.TryParse(flatOld[f], out var at))
                    {
                        int nf = lists.Take(at / 100).Sum(l => l.Count) + at % 100;
                        if (nf < Syms.Length) { sb.Append(Syms[nf]); if (run) sb.Append(order[k + 1]); }
                    }
                    if (run) k++;
                }
                order = sb.ToString();
            }
            return ranks.Select(r => new string(r).TrimEnd('0')).ToArray();
        }

        static Decoded ReadTf(Uri u, Func<string, TalentTreeDef> find, Func<TalentNode, string> nameOf, int legacy)
        {
            var path = Uri.UnescapeDataString(u.AbsolutePath + (u.Fragment ?? "")).Replace("/index.html", "/").Replace("#", "/");
            path = Regex.Replace(path, "/+", "/").Trim('/');
            var m = TfPath.Match(path);
            if (!m.Success) return Fail(TfName, "That Talents Forever link has no build in it (pick talents, and copy the address again).");
            var cls = m.Groups[1].Value.ToLowerInvariant();
            if (!ClassFiles.TryGetValue(cls, out var file)) return Fail(TfName, "The Talents Forever link does not name a class this game has.");
            var tree = find(file);
            if (tree == null) return NoTree(TfName, cls);
            var all = m.Groups[3].Value.Split('-').ToList();
            int nt = tree.SpecCount;
            // the last segment is the version of the site's trees when there is one more segment than trees
            string last = all[all.Count - 1];
            int ver = all.Count > nt && last.Length == 1 && last[0] >= '2' && last[0] <= '9' ? last[0] - '0' : 1;
            if (ver > TfNow) return Fail(TfName, "This Talents Forever link was made on newer talent trees than this Outpost knows (version " + ver + "). Update the Outpost to open it.");
            if (ver < 6) return Fail(TfName, "This Talents Forever link was made on older talent trees (" + (ver == 1 ? "before the site marked its versions" : "version " + ver) + "). Open it on the site, then copy the address again.");
            all.RemoveAt(all.Count - 1);
            if (all.Count < nt) all.AddRange(Enumerable.Repeat("", nt - all.Count));
            var trees = all.Take(nt).ToArray();
            var extra = all.Skip(nt).ToList();
            var order = extra.Count >= 4 ? extra[3] : extra.Count == 1 ? extra[0] : "";
            if (ver == 6)
            {
                if (trees.Any(s => !Regex.IsMatch(s, "^[0-9]*$"))) return Fail(TfName, "The " + TfName + " link is damaged (it has characters that do not belong).");
                var now = Enumerable.Range(0, nt).Select(s => SiteNodes(tree, s, true)).ToList();
                trees = TfFromV6(tree, trees, now, ref order, out var why);
                if (trees == null) return Fail(TfName, why);
            }
            var d = Place(TfName, tree, trees, true, false, out var lists);
            if (!d.Ok || order.Length == 0) return d;
            ReplayOrder(d, tree, lists, order, nameOf, legacy);
            return d;
        }

        // the order code: a symbol per talent (flat across the trees, in the site's order), optionally followed by a run length 1-4; no run length = all the
        // remaining ranks of that talent. Used only when it accounts for every point of the build and the rules allow each point where it stands.
        static void ReplayOrder(Decoded d, TalentTreeDef tree, List<List<TalentNode>> lists, string code, Func<TalentNode, string> nameOf, int legacy)
        {
            var flat = lists.SelectMany(l => l).ToList();
            var left = flat.ToDictionary(n => n.Id, n => d.State.Rank(n.Id));
            var points = new List<TalentNode>();
            for (int k = 0; k < code.Length; k++)
            {
                int f = Syms.IndexOf(code[k]);
                if (f < 0 || f >= flat.Count) continue;
                var node = flat[f];
                int n = left[node.Id];
                if (k + 1 < code.Length && code[k + 1] >= '1' && code[k + 1] <= '4') { n = Math.Min(n, code[k + 1] - '0'); k++; }
                for (int x = 0; x < n; x++) points.Add(node);
                left[node.Id] -= n;
            }
            if (points.Count != d.State.Total) { d.OrderNote = "The link's leveling order does not cover every point, so only the final talents were used."; return; }
            var app = new BuildApplication();
            var levels = TalentRules.PointLevels(tree, legacy);
            int number = 0;
            foreach (var node in points)
            {
                var why = TalentRules.Add(tree, app.State, node.Id, int.MaxValue);
                if (why != Why.Ok) { d.OrderNote = "The link's leveling order does not fit this game's rules, so only the final talents were used."; return; }
                number++;
                int placed = app.State.Total;
                app.Steps.Add(new BuildStep { Number = number, Level = placed - 1 < levels.Count ? levels[placed - 1] : 0, Name = nameOf?.Invoke(node) ?? NodeName(node), Rank = app.State.Rank(node.Id), Max = node.MaxRanks, NodeId = node.Id, Applied = true });
            }
            if (!app.State.SameAs(d.State)) { d.OrderNote = "The link's leveling order does not match its talents, so only the final talents were used."; return; }
            d.Order = app;
        }

        // ---- a query string as a dictionary (first value wins)
        static Dictionary<string, string> Query(Uri u)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var q = u.Query ?? "";
            if (q.StartsWith("?")) q = q.Substring(1);
            foreach (var part in q.Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                var k = Uri.UnescapeDataString((eq < 0 ? part : part.Substring(0, eq)).Replace('+', ' '));
                var v = eq < 0 ? "" : Uri.UnescapeDataString(part.Substring(eq + 1).Replace('+', ' '));
                if (!d.ContainsKey(k)) d[k] = v;
            }
            return d;
        }
    }
}
