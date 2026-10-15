using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Atlas.Classic;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Maps;
using ElansAddonHub.Services.Professions;

namespace ElansAddonHub
{
    // --selftest "profguide": the leveling guide in game, gathering nodes and routes (contract "profguide", parts A to D). Everything runs on invented data
    // under the scratch folder (a fake WoW folder, hand-made nodes, the synthetic book of the professions test); never the real game folder.
    //   routes       the route builder: cluster, nearest neighbour, 2-opt, closed loop, the Classic fallback rule, yards and minutes, caps
    //   nodes        Hub `atlas.obs` nodes (parsing, caps), batches (deltas), the atlas3 gate of the session, the Lodge's merged nodes (read API, cache)
    //   composer     SharedData.lua composed from flights + profGuides (flights-only is byte-identical, escaping, caps, size, removal, extra sections)
    //   follow       "Follow in game" / "Stop following" on the Guide tab, gathering guides with routes and pins, a saved route chosen for the guide, rebuild on change
    //   atlas        Forever nodes on object and zone pages (labelled, above the Classic positions), the Routes panel (combined loop, save, use for guide, delete)
    public partial class MainWindow
    {
        static double PgD(Dictionary<string, object> d, string k) => Convert.ToDouble(d[k], CultureInfo.InvariantCulture);
        static GNode PgN(double x, double y, long n = 1, bool forever = true, int obj = 0) => new GNode(x, y, n, forever, obj);

        static ProfChar PgChar(string name, string cls, params object[] stuff) { var c = PChar(name, cls, stuff); c.FromHub = true; return c; }

        async Task<string> ProfGuideTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            void Check(string what, bool cond, string detail = null) { notes.Add((cond ? "ok   " : "FAIL ") + what + (!cond && detail != null ? " [" + detail + "]" : "")); ok &= cond; }
            var work = Path.Combine(dir, "profguide");
            var hadRoot = settings.WowRoot; var hadOff = settings.AtlasShareOff; var hadClock = Session.Clock;
            var hadFollowed = settings.FollowedGuides; var hadSaved = settings.SavedRoutes; var hadChoice = settings.RouteForGuide;
            var savedLodge = AtlasQuery.Context.Lodge; var savedOwn = AtlasQuery.Context.OwnNodes; var savedNow = AtlasQuery.Context.NowUtc; var savedOwned = AtlasQuery.Context.Owned;
            var savedTiles = MapRender.TileSourceForTest; var savedMapDir = MapRender.DirOverride; var savedCurrent = SharedDataFile.Current; var savedPending = GatherNodes.Pending;
            var savedSpellFile = SpellData.CacheFile; var savedAuto = SpellData.AutoStart; var savedLink = ProfessionLinks.Last;
            try
            {
                if (Directory.Exists(work)) Util.DeleteDir(work);
                Directory.CreateDirectory(work);
                Check("fixture: everything of this test is under the scratch folder (never the real game)", Path.GetFullPath(work).StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase));
                SpellData.ResetForTest(); ItemData.ResetForTest(); SpellData.AutoStart = false;
                ProfGuideRoutes(Check);
                await ProfGuideNodes(Check, work);
                ProfGuideComposer(Check, work);
                await ProfGuideFollow(Check, work, dir);
                await ProfGuideAtlas(Check, work, dir);
            }
            catch (Exception ex) { ok = false; notes.Add("FAIL exception: " + ex); }
            finally
            {
                try
                {
                    Session.Disconnect(); Session.SentForTest = null; Session.Clock = hadClock; Session.AtlasResetForTest();
                    settings.WowRoot = hadRoot; settings.AtlasShareOff = hadOff;
                    settings.FollowedGuides = hadFollowed; settings.SavedRoutes = hadSaved; settings.RouteForGuide = hadChoice;
                    SharedDataFile.Current = savedCurrent; GatherNodes.Pending = savedPending;
                    AtlasQuery.Context.Lodge = savedLodge; AtlasQuery.Context.OwnNodes = savedOwn; AtlasQuery.Context.NowUtc = savedNow; AtlasQuery.Context.Owned = savedOwned;
                    AtlasView.OwnedOverrideForTest = null; AtlasView.NodeSourceForTest = null;
                    ProfessionsView.NodeSourceForTest = null; ProfessionsView.FollowDebounce = TimeSpan.FromSeconds(3);
                    ClassicAtlas.Use(null); ItemData.ResetForTest(); SpellData.ResetForTest(); SeenData.ResetForTest(); MapData.ResetForTest(); AtlasBrowse.ForgetForTest();
                    MapRender.TileSourceForTest = savedTiles; MapRender.DirOverride = savedMapDir; MapRender.ForgetFailuresForTest();
                    SpellData.CacheFile = savedSpellFile; SpellData.AutoStart = savedAuto;
                    ProfessionsView.ResetForTest(); ProfessionLinks.Last = savedLink;
                    AtlasObs.ResetCache();
                    ShowTab("addons"); ResetAtlasForTest();
                }
                catch (Exception e) { notes.Add("cleanup: " + e.Message); }
            }
            notes.Insert(0, "profguide (the leveling guide in game, gathering nodes, routes): " + (ok ? "all ok" : "FAILURES"));
            return string.Join("\r\n", notes);
        }

        // ================================================================================================================ routes
        void ProfGuideRoutes(Action<string, bool, string> Check0)
        {
            void Check(string what, bool cond, string detail = null) => Check0(what, cond, detail);
            // a square given in a scrambled order: the loop is the perimeter
            var sq = new[] { PgN(0.8, 0.8), PgN(0.2, 0.2), PgN(0.2, 0.8), PgN(0.8, 0.2) };
            var r = RouteBuilder.Build(sq, null);
            double edge(int i) { var a = r.Points[i]; var b = r.Points[(i + 1) % r.Points.Count]; return Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y)); }
            Check("route: four corners of a square -> the perimeter (every edge 0.6, length 2.4, closed loop of 4 stops)", r.Count == 4 && Math.Abs(r.Length - 2.4) < 1e-9 && Enumerable.Range(0, 4).All(i => Math.Abs(edge(i) - 0.6) < 1e-9), r.Length.ToString(CultureInfo.InvariantCulture));
            Check("route: a Forever-only set has src f, no Classic note", r.Src == "f" && !r.UsedClassic && r.Note == "");
            // 2-opt removes a crossing: A(0,0) B(1,1) C(1,0) D(0,1) visited A-B-C-D crosses itself
            var bow = new List<GNode> { PgN(0, 0), PgN(1, 1), PgN(1, 0), PgN(0, 1) };
            var order = new List<int> { 0, 1, 2, 3 };
            double before = RouteBuilder.LoopLength(order, bow, 1, 1);
            int work = 0; int swaps = RouteBuilder.TwoOpt(bow, order, 1, 1, 10000, ref work);
            double after = RouteBuilder.LoopLength(order, bow, 1, 1);
            Check("2-opt: the self-crossing loop (4.83) becomes the square (4.0) with one swap, and the first stop stays first", Math.Abs(before - (2 * Math.Sqrt(2) + 2)) < 1e-9 && Math.Abs(after - 4.0) < 1e-9 && swaps >= 1 && order[0] == 0, before + " -> " + after);
            // clusters: closer than 0.02 is one stop (looted-weighted mean, counts added); 0.03 away is another
            var cl = RouteBuilder.Cluster(new[] { PgN(0.5, 0.5, 5), PgN(0.51, 0.5, 3), PgN(0.6, 0.5, 1) }, RouteBuilder.ClusterDistance);
            Check("cluster: two nodes 0.01 apart are one stop at their looted-weighted mean (0.50375), with the counts added (8); a node 0.1 away is its own",
                cl.Count == 2 && cl.Any(c => c.N == 8 && Math.Abs(c.X - 0.5038) < 1e-4) && cl.Any(c => c.N == 1 && Math.Abs(c.X - 0.6) < 1e-9), string.Join(" | ", cl.Select(c => c.X + ":" + c.N)));
            Check("cluster: exactly 0.02 apart is NOT merged (closer than 0.02 is)", RouteBuilder.Cluster(new[] { PgN(0.3, 0.5), PgN(0.32, 0.5) }, 0.02).Count == 2);
            // the tour starts at the stop closest to the centre
            var near = RouteBuilder.Build(new[] { PgN(0.1, 0.1), PgN(0.52, 0.49), PgN(0.9, 0.9) }, null);
            Check("tour: starts at the stop closest to the zone's centre", near.Points[0].X == 0.52 && near.Points[0].Y == 0.49);
            // the Classic fallback rule
            var f3 = Enumerable.Range(0, 3).Select(i => PgN(0.1 + i * 0.1, 0.2, 2)).ToList();
            var c10 = Enumerable.Range(0, 10).Select(i => PgN(0.05 + i * 0.09, 0.7, 0, false)).ToList();
            var mixed = RouteBuilder.Build(f3, c10);
            Check("classic fallback: 3 Forever nodes (fewer than 8) -> the Classic positions fill the loop, the note says so, src is c, 13 stops", mixed.UsedClassic && mixed.Src == "c" && mixed.Count == 13 && mixed.Note.Contains("Only 3 Forever nodes") && mixed.ForeverNodes == 3 && mixed.ClassicNodes == 10, mixed.Note);
            var f8 = Enumerable.Range(0, 8).Select(i => PgN(0.1 + i * 0.1, 0.2, 2)).ToList();
            var enough = RouteBuilder.Build(f8, c10);
            Check("classic fallback: 8 Forever nodes -> Forever only (8 stops), the Classic positions are left out, src f", !enough.UsedClassic && enough.Src == "f" && enough.Count == 8 && enough.Points.All(p => p.Forever) && enough.Note == "");
            var none = RouteBuilder.Build(null, c10);
            Check("classic fallback: no Forever node -> the Classic positions alone, the note says none are known", none.UsedClassic && none.Count == 10 && none.Note.StartsWith("No Forever nodes known here yet") && none.Points.All(p => !p.Forever));
            var nothing = RouteBuilder.Build(null, null);
            Check("route: nothing known -> an empty route that says so", nothing.Count == 0 && nothing.Summary() == "No nodes known for this selection.");
            // yards and minutes
            var yd = RouteBuilder.Build(sq, null, 1000, 500);
            Check("yards: a map of 1000 x 500 yards: the square's loop is 2 x 600 + 2 x 300 = 1800 yards, about 4.3 minutes at 7 yards a second, the summary says so",
                yd.Yards && Math.Abs(yd.Length - 1800) < 1e-6 && Math.Abs(yd.Minutes - 1800 / 7.0 / 60) < 1e-9 && yd.Summary() == "4 nodes, about 4 minutes at run speed (1,800 yards)", yd.Summary());
            Check("yards: a map without a world size gives the length in map fractions only and says it is unknown", !r.Yards && r.Minutes == 0 && r.Summary().Contains("length unknown"));
            // caps
            var rnd = new Random(7); var many = Enumerable.Range(0, 300).Select(i => PgN(rnd.NextDouble(), rnd.NextDouble(), 1 + rnd.Next(5))).ToList();
            var big = RouteBuilder.Build(many, null);
            Check("caps: 300 scattered nodes -> at most 120 stops, and the note says the most looted were kept", big.Count <= RouteBuilder.MaxStops && big.Note.Contains("120"), big.Count + " " + big.Note);
            var pts = Enumerable.Range(0, 100).Select(i => PgN(rnd.NextDouble(), rnd.NextDouble())).ToList();
            var capped = RouteBuilder.Build(pts, null, 0, 0, 0.5, 0.5, 120, 500);
            Check("caps: the 2-opt work is capped (500 distance evaluations at most), the loop is still complete", capped.Work <= 500 && capped.Count == RouteBuilder.Cluster(pts, 0.02).Count, capped.Work.ToString());
            var full = RouteBuilder.Build(pts, null);
            Check("2-opt: a full run is never longer than the nearest-neighbour tour alone", full.Length <= RouteBuilder.LoopLength(RouteBuilder.NearestNeighbour(RouteBuilder.Cluster(pts, 0.02), 1, 1), RouteBuilder.Cluster(pts, 0.02), 1, 1) + 1e-9);
            // invalid nodes are ignored
            Check("route: nodes outside the map (x = 1.5, NaN) are ignored", RouteBuilder.Build(new[] { PgN(1.5, 0.5), PgN(double.NaN, 0.5), PgN(0.5, 0.5) }, null).Count == 1);
            // several kinds in one loop
            var fx = new FixedNodeSource();
            fx.AddForever(1731, 1411, PgN(0.2, 0.2, 3), PgN(0.3, 0.2, 2)); fx.AddForever(1732, 1411, PgN(0.4, 0.3, 1), PgN(0.5, 0.5, 4)); fx.AddForever(1618, 1411, PgN(0.8, 0.8, 2)); fx.AddForever(1731, 1412, PgN(0.9, 0.9, 2));
            fx.Yards[1411] = (2000, 1000);
            var combo = GatherRoutes.Build(fx, new[] { 1731, 1732 }, 1411);
            Check("routes: Copper Vein + Tin Vein in one loop (4 stops, both kinds, nothing from the other zone or the other plant)", combo.Count == 4 && combo.Points.Select(p => p.Object).Distinct().OrderBy(i => i).SequenceEqual(new[] { 1731, 1732 }) && combo.Yards);
            Check("routes: the zones of a set are listed, the one with the most nodes first", GatherRoutes.ZonesOf(fx, new[] { 1731, 1732 }).SequenceEqual(new[] { 1411, 1412 }));
            // saved routes
            var sr = SavedRoute.From("  Mine\u0001 loop " + new string('x', 80), 1411, new[] { 1732, 1731, 1731 }, combo, 1790000000);
            Check("saved route: the name is cleaned and shortened, objects sorted and distinct, the points frozen (4 stops) and read back as nodes", sr.Name.Length <= SavedRoute.MaxNameLength && !sr.Name.Contains("\u0001") && sr.Objects.SequenceEqual(new[] { 1731, 1732 }) && sr.Points.Count == 8 && sr.ToNodes().Count == 4 && sr.Src == "f" && sr.Zone == 1411);
            Check("saved route: an empty name becomes 'Route', bad points are skipped on reading", SavedRoute.CleanName("  ") == "Route" && new SavedRoute { Points = new List<double> { 0.1, 0.2, 1.5, 0.2, double.NaN, 0.1, 0.3, 0.4 } }.ToNodes().Count == 2);
        }

        // ================================================================================================================ nodes: Hub, batches, session
        static string PgNodesLua() =>
            ",\nnodes = { [1731] = { [1411] = { { x = 0.41234, y = 0.5531, n = 3, at = " + AtlasAt + " }, { x = 0.4125, y = 0.5533, n = 5 }, { x = 1.5, y = 0.2, n = 1 }, { x = 0.7, y = 0.7, n = 0 }, { x = 0.1, y = 0.1, n = 2.5 }, { x = 0.9, y = 0.9, n = 2 } },"
            + " [1412] = { { x = 0.3, y = 0.3, n = 1 } }, [\"x\"] = { { x = 0.3, y = 0.3, n = 1 } } }, [\"abc\"] = { [1411] = { { x = 0.3, y = 0.3, n = 1 } } }, [1732] = 5, [0] = { [1411] = { { x = 0.1, y = 0.1, n = 1 } } } }";

        async Task ProfGuideNodes(Action<string, bool, string> Check0, string work)
        {
            void Check(string what, bool cond, string detail = null) => Check0(what, cond, detail);
            atlasRoot = Path.Combine(work, "wow");
            Directory.CreateDirectory(Path.Combine(atlasRoot, "_classic_beta_", "WTF", "Account", "ACC1", "SavedVariables"));
            Session.Disconnect(); Session.AtlasResetForTest();
            WriteAtlasHub("ACC1", AtlasLua(AtlasFixture() + PgNodesLua()));
            var s = AtlasObs.Read(atlasRoot);
            Check("nodes (A): the Hub's atlas.obs `nodes` are read next to the other fields", s != null && s.Nodes.Count == 1 && s.Nodes.ContainsKey(1731));
            if (s == null || !s.Nodes.ContainsKey(1731)) return;
            var m1 = s.Nodes[1731];
            Check("nodes (A): positions are rounded to 4 decimals; two looted spots within 0.008 are one node (the first position, the larger count 5); a bad x, n = 0 and a fractional n are dropped",
                m1.ContainsKey(1411) && m1[1411].Count == 2 && m1[1411].Any(p => Math.Abs(p.X - 0.4123) < 1e-9 && Math.Abs(p.Y - 0.5531) < 1e-9 && p.N == 5 && p.At == AtlasAt) && m1[1411].Any(p => p.X == 0.9 && p.N == 2), string.Join(" | ", m1[1411].Select(p => p.X + "," + p.Y + ":" + p.N)));
            Check("nodes (A): another map of the object is kept, a text map key, a text object key, object 0 and a non-table are dropped", m1.Count == 2 && m1.ContainsKey(1412) && !s.Nodes.ContainsKey(0) && !s.Nodes.ContainsKey(1732) && s.Dropped >= 6, s.Dropped.ToString());
            Check("nodes (A): the snapshot is not empty because of the nodes alone", !new AtlasObs.Snapshot().IsEmpty == false || new AtlasObs.Snapshot { Nodes = { [1] = new Dictionary<int, List<AtlasObs.NodePt>>() } }.IsEmpty == false);

            // caps: 200 spread-out nodes on one map keep 150; 450 objects keep 400
            var sb = new StringBuilder(",\nnodes = { [5] = { [1] = { ");
            for (int i = 0; i < 200; i++) sb.Append("{ x = ").Append((0.01 + (i % 20) * 0.045).ToString(CultureInfo.InvariantCulture)).Append(", y = ").Append((0.01 + (i / 20) * 0.09).ToString(CultureInfo.InvariantCulture)).Append(", n = 1 }, ");
            sb.Append("} }");
            for (int o = 100; o < 560; o++) sb.Append(", [").Append(o).Append("] = { [1] = { { x = 0.5, y = 0.5, n = 1 } } }");
            sb.Append(" }");
            WriteAtlasHub("ACC1", AtlasLua(AtlasFixture() + sb));
            var big = AtlasObs.Read(atlasRoot);
            Check("nodes (A): at most 150 nodes per object and map and 400 objects are read", big != null && big.Nodes[5][1].Count == AtlasObs.MaxNodesPerMap && big.Nodes.Count == AtlasObs.MaxNodeObjects, big?.Nodes[5][1].Count + " / " + big?.Nodes.Count);

            // batches
            WriteAtlasHub("ACC1", AtlasLua(AtlasFixture() + PgNodesLua()));
            s = AtlasObs.Read(atlasRoot);
            var sent = new Dictionary<string, string>();
            var off = AtlasObs.MakeBatch(s, null, sent, true, false);
            Check("nodes (B): without atlas3 the batch has no nodes section", off != null && off.Frame.Child("nodes") == null);
            var on = AtlasObs.MakeBatch(s, null, sent, true, true);
            var nodes = on?.Frame.Child("nodes");
            var pts = nodes?.Child("1731")?.List("1411");
            Check("nodes (B): with atlas3 the batch carries nodes { object: { map: [ { x, y, n } ] } } with the cumulative counts, no times", pts != null && pts.Count == 2 && pts.OfType<Dictionary<string, object>>().Any(p => Math.Abs((double)p["x"] - 0.4123) < 1e-9 && Convert.ToInt64(p["n"]) == 5 && !p.ContainsKey("at")) && nodes.Child("1731").List("1412").Count == 1);
            Check("nodes (B): what was sent is remembered by signature: a second batch has no nodes; a bigger count sends that object again", AtlasObs.MakeBatch(s, null, on.Sigs.Concat(sent).GroupBy(k => k.Key).ToDictionary(g => g.Key, g => g.First().Value), true, true)?.Frame.Child("nodes") == null);
            s.Nodes[1731][1411][0] = new AtlasObs.NodePt { X = s.Nodes[1731][1411][0].X, Y = s.Nodes[1731][1411][0].Y, N = s.Nodes[1731][1411][0].N + 1, At = AtlasAt };
            var again = AtlasObs.MakeBatch(s, null, on.Sigs, true, true);
            Check("nodes (B): a changed count goes out alone", again != null && again.Frame.Child("nodes") != null && again.Frame.Child("nodes").Count == 1 && again.Sigs.Count == 1 && again.Sigs.ContainsKey("n1731"));
            var lots = new AtlasObs.Snapshot();
            for (int o = 1; o <= 70; o++) lots.Nodes[o] = new Dictionary<int, List<AtlasObs.NodePt>> { [1] = new List<AtlasObs.NodePt> { new AtlasObs.NodePt { X = 0.5, Y = 0.5, N = 1 } } };
            var b1 = AtlasObs.MakeBatch(lots, null, new Dictionary<string, string>(), false, true);
            Check("nodes (B): at most 50 objects in one message, the rest waits for the next", b1 != null && b1.Frame.Child("nodes").Count == AtlasObs.MaxNodeObjectsPerShare && b1.Remaining == 20);
            var heavy = new AtlasObs.Snapshot();
            for (int o = 1; o <= 12; o++) heavy.Nodes[o] = new Dictionary<int, List<AtlasObs.NodePt>> { [1] = Enumerable.Range(0, 150).Select(i => new AtlasObs.NodePt { X = 0.005 + (i % 30) * 0.03, Y = 0.005 + (i / 30) * 0.03, N = 1 }).ToList() };
            var b2 = AtlasObs.MakeBatch(heavy, null, new Dictionary<string, string>(), false, true);
            Check("nodes (B): at most 1000 points in one message (6 objects of 150), within the 56 KB", b2 != null && b2.Frame.Child("nodes").Count == 6 && b2.Remaining == 6 && Encoding.UTF8.GetByteCount(Json.Write(b2.Frame)) <= AtlasObs.MaxShareBytes);

            // the session: only to a server that advertises atlas3
            var sentFrames = new List<Dictionary<string, object>>();
            var now = new DateTime(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);
            settings.WowRoot = atlasRoot; settings.AtlasShareOff = false;
            Session.Clock = () => now; Session.SentForTest = o => sentFrames.Add(o);
            AtlasObs.TestSource = null; AtlasObs.RevealedSource = AtlasObs.Empty;
            string F(string extra) => "\"reply\",\"react\",\"react2\",\"pin\",\"profile\",\"displayname\",\"lfg\",\"history2\",\"search\",\"atlas\",\"atlas2\"" + extra;
            Session.AtlasResetForTest();
            var sentFile = Session.AtlasSentPathForTest; var cacheFile = Session.AtlasCachePathForTest;
            try { File.Delete(sentFile); File.Delete(cacheFile); } catch { }
            WriteAtlasHub("ACC1", AtlasLua(AtlasFixture() + PgNodesLua()));
            Session.FeedForTest(FunWelcome("member", FunPeople(false), "", F(""), "2.17.0"));
            var shares = AFrames(sentFrames, "atlas.share");
            Check("nodes (session): a server with atlas2 but not atlas3 gets no nodes", Session.SupportsAtlas && !Session.SupportsAtlasNodes && shares.Count == 1 && shares[0].Child("nodes") == null && shares[0].Child("kills") != null);
            Session.Disconnect(); Session.AtlasResetForTest(); sentFrames.Clear();
            try { File.Delete(sentFile); File.Delete(cacheFile); } catch { }
            Session.FeedForTest(FunWelcome("member", FunPeople(false), "", F(",\"atlas3\""), "2.17.0"));
            Check("nodes (session): a server that lists atlas3 but is older than 2.18.0 gets none either", !Session.SupportsAtlasNodes && AFrames(sentFrames, "atlas.share").All(f => f.Child("nodes") == null));
            Session.Disconnect(); Session.AtlasResetForTest(); sentFrames.Clear();
            try { File.Delete(sentFile); File.Delete(cacheFile); } catch { }
            Session.FeedForTest(FunWelcome("member", FunPeople(false), "", F(",\"atlas3\""), "2.18.0"));
            shares = AFrames(sentFrames, "atlas.share");
            Check("nodes (session): with atlas3 on 2.18.0 the first upload carries the nodes next to the observations", Session.SupportsAtlasNodes && shares.Count == 1 && shares[0].Child("nodes")?.Child("1731")?.Child("1411") == null && shares[0].Child("nodes")?.Child("1731")?.List("1411")?.Count == 2 && shares[0].Child("kills") != null);
            Session.FeedForTest("{\"t\":\"atlas.shared\",\"ok\":true,\"accepted\":13,\"unchanged\":0,\"rejected\":0,\"rev\":60,\"epoch\":\"pg1\"}");
            Check("nodes (session): the upload record remembers the object (key n1731)", Session.AtlasSentForTest.ContainsKey("n1731"));
            settings.AtlasShareOff = true; Session.Disconnect(); Session.AtlasResetForTest(); sentFrames.Clear();
            try { File.Delete(sentFile); File.Delete(cacheFile); } catch { }
            Session.FeedForTest(FunWelcome("member", FunPeople(false), "", F(",\"atlas3\""), "2.18.0"));
            Check("nodes (session): sharing switched off sends nothing (nodes included)", AFrames(sentFrames, "atlas.share").Count == 0);
            settings.AtlasShareOff = false;

            // the Lodge's merged nodes: read API, validation, cache
            Session.Disconnect(); Session.AtlasResetForTest(); sentFrames.Clear();
            try { File.Delete(sentFile); File.Delete(cacheFile); } catch { }
            Session.FeedForTest(FunWelcome("member", FunPeople(false), "", F(",\"atlas3\""), "2.18.0"));
            Session.FeedForTest("{\"t\":\"atlas.shared\",\"ok\":true,\"accepted\":1,\"unchanged\":0,\"rejected\":0,\"rev\":61,\"epoch\":\"pg1\"}");
            long changed0 = Session.AtlasData.NodesChanged;
            Session.FeedForTest(AtlasPage("pg1", 70, "\"nodes\":{\"1731\":{\"1411\":[{\"x\":0.4125,\"y\":0.5533,\"n\":7},{\"x\":0.7,\"y\":0.7,\"n\":1},{\"x\":2,\"y\":0.1,\"n\":1},{\"x\":0.2,\"y\":0.2,\"n\":0}],\"x\":[{\"x\":0.1,\"y\":0.1,\"n\":1}]},\"abc\":{\"1\":[{\"x\":0.1,\"y\":0.1,\"n\":1}]},\"1732\":{\"1411\":[{\"x\":0.5,\"y\":0.5,\"n\":4}]}}", true));
            var L = Session.AtlasData;
            Check("nodes (lodge): the merged nodes are read per object and map; a bad x, n = 0 and bad keys are dropped; the change counter moved", L.Nodes(1731, 1411).Count == 2 && L.Nodes(1731, 1411).Any(p => p.N == 7) && L.Nodes(1732, 1411).Count == 1 && L.NodeObjectCount == 2 && L.Nodes(9, 9).Count == 0 && L.NodesOf(1731).Count == 1 && L.NodesChanged > changed0);
            Check("nodes (lodge): the Atlas data counts as data with nodes alone", new LodgeAtlas().HasData == false && L.HasData);
            Session.AtlasSaveCacheForTest();
            var again2 = new LodgeAtlas();
            var cachePage = Json.Obj(File.ReadAllText(cacheFile)); cachePage["full"] = true;
            Check("nodes (lodge): the cache file holds the nodes and reads back the same", again2.Apply(cachePage) && again2.Nodes(1731, 1411).Count == 2 && again2.Nodes(1731, 1411).Any(p => p.N == 7 && Math.Abs(p.X - 0.4125) < 1e-9) && again2.Nodes(1732, 1411).Count == 1);
            Session.FeedForTest(AtlasPage("pg1", 71, "\"nodes\":{\"1731\":{\"1411\":[{\"x\":0.4125,\"y\":0.5533,\"n\":9}]}}"));
            Check("nodes (lodge): an incremental page replaces that object's nodes as a whole and leaves the others", L.Nodes(1731, 1411).Count == 1 && L.Nodes(1731, 1411)[0].N == 9 && L.Nodes(1732, 1411).Count == 1);
            Check("nodes (lodge): own nodes and the Lodge's merge: a node of mine on a Lodge node is the same node (larger count), one elsewhere is added",
                GatherNodes.Merge(new[] { new AtlasObs.NodePt { X = 0.4126, Y = 0.5533, N = 12 }, new AtlasObs.NodePt { X = 0.1, Y = 0.1, N = 2 } }, L.Nodes(1731, 1411)).Count == 2
                && GatherNodes.Merge(new[] { new AtlasObs.NodePt { X = 0.4126, Y = 0.5533, N = 12 } }, L.Nodes(1731, 1411)).Single().N == 12 && GatherNodes.Merge(null, L.Nodes(1731, 1411)).Count == 1);
            Session.Disconnect(); Session.AtlasResetForTest();
            try { File.Delete(sentFile); File.Delete(cacheFile); } catch { }
            await Task.CompletedTask;
        }

        // ================================================================================================================ composer
        static GuideOut PgSample()
        {
            var g = new GuideOut { SkillId = 129, Name = "First Aid", Title = "First Aid 1-300", Max = 300 };
            g.Steps.Add(new GuideStepOut { K = "craft", From = 1, To = 40, Spell = 3275, Item = 1251, N = 30, Reagents = new List<int[]> { new[] { 2589, 30 } }, Text = "Linen Bandage" });
            g.Steps.Add(new GuideStepOut { K = "train", From = 75, Text = "Journeyman First Aid" });
            g.Steps.Add(new GuideStepOut { K = "gather", From = 1, To = 65, Item = 2770, Objects = new List<int> { 1731 }, Zones = new List<int> { 1411, 1412 }, Text = "Copper Ore" });
            g.Steps.Add(new GuideStepOut { K = "note", From = 1, To = 300, Text = "free \"text\" with \\ and\nnewline\u0007 ]] os.execute('x') --" });
            g.Routes[3] = new Dictionary<int, GuideRouteOut> { [1411] = new GuideRouteOut { Src = "f", Points = { new[] { 0.41, 0.55 }, new[] { 0.44, 0.61 } } } };
            g.Nodes[1731] = new Dictionary<int, List<double[]>> { [1411] = new List<double[]> { new[] { 0.4123, 0.5531, 3.0 }, new[] { 0.5, 0.5, 0.0 } } };
            return g;
        }

        static string PgFlightsFile(DateTime now, string root)
        {
            var m = new FlightShare.Merged { At = FlightAt };
            m.Legs["Horde|11>22"] = new FlightShare.Leg { T = 61.5, N = 3 }; m.Names["11"] = "Orgrimmar"; m.Names["22"] = "Thunder Bluff";
            return FlightShare.BuildLua(m, now);
        }

        static string PgHubDir(string root, string flavor = "_classic_beta_") => Path.Combine(root, flavor, "Interface", "AddOns", "ElansHub");

        void ProfGuideComposer(Action<string, bool, string> Check0, string work)
        {
            void Check(string what, bool cond, string detail = null) => Check0(what, cond, detail);
            var now = now0();
            var m = new FlightShare.Merged { At = FlightAt };
            m.Legs["Horde|11>22"] = new FlightShare.Leg { T = 61.5, N = 3 }; m.Legs["Alliance|2>3"] = new FlightShare.Leg { T = 120, N = 50 };
            m.Names["11"] = "Orgrimmar"; m.Names["22"] = "Thunder Bluff"; m.Names["2"] = "Say \"hi\"\\";
            Check("composer: no guides -> exactly the flights text (byte for byte), whether the guides are null, empty or a character without guides",
                SharedDataFile.Compose(m, now, null) == FlightShare.BuildLua(m, now) && SharedDataFile.Compose(m, now, new ProfGuidesOut()) == FlightShare.BuildLua(m, now)
                && SharedDataFile.Compose(m, now, new ProfGuidesOut { Chars = { ["Realm-Nobody"] = new List<GuideOut>() } }) == FlightShare.BuildLua(m, now) && SharedDataFile.Compose(null, now, null) == FlightShare.BuildLua(null, now));
            Check("composer: FlightShare.BuildLua without the extra argument is unchanged (the old call sites)", FlightShare.BuildLua(m, now) == FlightShare.BuildLua(m, now, null) && FlightShare.BuildLua(m, now, "") == FlightShare.BuildLua(m, now));

            const string Key = "Realm-Ali\"ce\\";
            var guides = new ProfGuidesOut { At = 1760000000 };
            guides.Chars[Key] = new List<GuideOut> { PgSample() };
            var lua = SharedDataFile.Compose(m, now, guides);
            Check("composer: with a guide the flights part is the same text up to the closing brace (flights unchanged) and profGuides follows inside ElansHubShared",
                lua.StartsWith(FlightShare.BuildLua(m, now).Substring(0, FlightShare.BuildLua(m, now).Length - 2)) && lua.TrimEnd().EndsWith("}") && lua.Contains("\tprofGuides = {\n"));
            var back = LuaData.ReadGlobals(lua);
            var shared = back.ContainsKey("ElansHubShared") ? back["ElansHubShared"] as Dictionary<string, object> : null;
            var pgd = shared?.Child("profGuides");
            Check("composer: reads back as ElansHubShared with flights AND profGuides (v = 1, at)", shared != null && shared.Child("flights")?.Child("legs")?.Count == 2 && pgd != null && pgd.Int("v") == 1 && pgd.Long("at") == 1760000000);
            var g129 = pgd?.Child("chars")?.FirstOrDefault().Value as Dictionary<string, object>;
            var gd = g129?.Child("129");
            var steps = gd?.Child("steps");
            Check("composer: the character key is escaped (quote and backslash) and read back exactly", pgd?.Child("chars")?.Keys.Single() == Key, pgd?.Child("chars")?.Keys.FirstOrDefault());
            Check("composer: the guide has name, title, max and the four steps in order (craft, train, gather, note)", gd != null && gd.Str("name") == "First Aid" && gd.Str("title") == "First Aid 1-300" && gd.Int("max") == 300 && steps != null && steps.Count == 4
                && steps.Child("1").Str("k") == "craft" && steps.Child("2").Str("k") == "train" && steps.Child("3").Str("k") == "gather" && steps.Child("4").Str("k") == "note");
            var s1 = steps?.Child("1"); var s2 = steps?.Child("2"); var s3 = steps?.Child("3");
            Check("composer: a craft step has from, to, spell, item, n, the reagents { itemId, count } and its text", s1 != null && s1.Int("from") == 1 && s1.Int("to") == 40 && s1.Int("spell") == 3275 && s1.Int("item") == 1251 && s1.Int("n") == 30 && s1.Child("r").Child("1").Int("1") == 2589 && s1.Child("r").Child("1").Int("2") == 30 && s1.Str("t") == "Linen Bandage");
            Check("composer: a train step has `from` and no `to`; a gather step has item, obj and z lists", s2 != null && s2.Int("from") == 75 && !s2.ContainsKey("to") && s2.Str("t") == "Journeyman First Aid" && s3 != null && s3.Int("item") == 2770 && s3.Child("obj").Int("1") == 1731 && s3.Child("z").Int("1") == 1411 && s3.Child("z").Int("2") == 1412);
            Check("composer: free text is escaped and cleaned - quotes, backslash and brackets stay inside the string, the newline and the bell are gone, injection text is harmless",
                steps?.Child("4").Str("t") == "free \"text\" with \\ and newline ]] os.execute('x') --" && !lua.Contains("\u0007") && lua.Split('\n').All(l => l.Length < 400) && !lua.Contains("with \\ and\n"));
            var rt = gd?.Child("routes")?.Child("3")?.Child("1411");
            Check("composer: routes are keyed by the gather step's index (3) then the zone: src and the points", rt != null && rt.Str("src") == "f" && rt.Child("pts").Count == 2 && Math.Abs(PgD(rt.Child("pts").Child("1"), "1") - 0.41) < 1e-9 && Math.Abs(PgD(rt.Child("pts").Child("2"), "2") - 0.61) < 1e-9);
            var np = gd?.Child("nodes")?.Child("1731")?.Child("1411");
            Check("composer: nodes are { x, y, n }: a Forever node with its count (3), a Classic baseline position with n = 0", np != null && np.Count == 2 && np.Child("1").Int("3") == 3 && Math.Abs(PgD(np.Child("1"), "1") - 0.4123) < 1e-9 && np.Child("2").Int("3") == 0);
            Check("composer: the same input gives the same text (deterministic) and the header comment is the only thing that differs with the time", lua == SharedDataFile.Compose(m, now, guides) && SharedDataFile.Compose(m, now.AddMinutes(5), guides).Replace("12:05:00", "12:00:00") == lua);
            // every line has a known shape (nothing else can be written)
            var shapes = new[] { @"^-- .*$", @"^ElansHubShared = \{$", @"^\}$", @"^\t[a-zA-Z]+ = \{$", @"^\t\},$", @"^\t\t[a-z]+ = \{$", @"^\t\t\},$", @"^\t\tv = 1, at = \d+,$", @"^\t\tat = \d+,$",
                @"^\t\t\t\[""(?:[^""\\\x00-\x1f]|\\[""\\])*""\] = \{$", @"^\t\t\t\},$", @"^\t\t\t\t\[\d+\] = \{$", @"^\t\t\t\t\},$", @"^\t\t\t\t\t[a-z]+ = \{$", @"^\t\t\t\t\t\},$", @"^\t\t\t\t\tname = "".*"", title = "".*"", max = \d+,$",
                @"^\t\t\t\t\t\t\{ k = ""(craft|train|gather|note)"", from = \d+.*, t = "".*"" \},$", @"^\t\t\t\t\t\t\[\d+\] = \{ .* \},$", @"^\t\t\t\[""[\p{L}\p{N} '.,\-]{1,32}"".*$", @"^\t\t\t\[""(Horde|Alliance)\|.*$", @"^\t\t\t\[""[\p{L}\p{N} '.,\-]{1,32}""\] = "".*"",$" };
            var odd = lua.Split('\n').Take(lua.Split('\n').Length - 1).Where(l => !shapes.Any(sh => System.Text.RegularExpressions.Regex.IsMatch(l, sh))).ToList();
            Check("composer: every line has one of the expected shapes (" + odd.Count + " odd)" + (odd.Count > 0 ? ": " + odd[0] : ""), odd.Count == 0);

            // caps and validation
            var big = new GuideOut { SkillId = 171, Name = "Alchemy", Title = "Alchemy 1-300", Max = 300 };
            for (int i = 0; i < 70; i++) big.Steps.Add(new GuideStepOut { K = "craft", From = i, To = i + 1, Spell = 100 + i, Item = 200 + i, N = 5, Text = "Step " + i, Reagents = Enumerable.Range(1, 25).Select(r => new[] { r, 2 }).ToList() });
            big.Steps.Add(new GuideStepOut { K = "gather", From = 1, To = 2, Objects = new List<int> { 5 }, Zones = new List<int> { 1 } });
            big.Routes[2] = new Dictionary<int, GuideRouteOut> { [1] = new GuideRouteOut { Src = "c", Points = Enumerable.Range(0, 150).Select(i => new[] { i / 150.0, 0.5 }).Concat(new[] { new[] { 1.5, 0.5 }, new[] { double.NaN, 0.5 } }).ToList() } };
            big.Routes[99] = new Dictionary<int, GuideRouteOut> { [1] = new GuideRouteOut { Points = { new[] { 0.1, 0.1 } } } };
            var caps = new ProfGuidesOut { At = 5 };
            caps.Chars["Realm-Caps"] = Enumerable.Range(1, 9).Select(i => new GuideOut { SkillId = 100 + i, Name = "P" + i, Title = "T", Max = 300, Steps = { new GuideStepOut { K = "craft", From = 1, To = 2, Spell = 5, Text = "x" } } }).ToList();
            caps.Chars["Realm-Big"] = new List<GuideOut> { big, new GuideOut { SkillId = 0, Steps = { new GuideStepOut() } }, new GuideOut { SkillId = 171, Steps = { new GuideStepOut() } } };
            var cl = LuaData.ReadGlobals(SharedDataFile.Compose(null, now, caps))["ElansHubShared"] as Dictionary<string, object>;
            var chars = cl.Child("profGuides").Child("chars");
            var bigG = chars.Child("Realm-Big").Child("171");
            Check("caps: at most 6 guides per character (9 given), a guide without a skill id or steps is dropped, the second guide of the same skill too", chars.Child("Realm-Caps").Count == 6 && chars.Child("Realm-Big").Count == 1);
            Check("caps: 60 steps per guide (71 given), 20 reagents per step (25 given)", bigG.Child("steps").Count == 60 && bigG.Child("steps").Child("1").Child("r").Count == 20);
            Check("caps: a route belongs to a gather step inside the 60 (step 2 here is no gather step, 99 does not exist) -> no routes", bigG.Child("routes") == null);
            big.Steps = big.Steps.Take(5).ToList(); big.Steps.Add(new GuideStepOut { K = "gather", From = 1, To = 2, Objects = new List<int> { 5 }, Zones = new List<int> { 1 } });
            big.Routes.Clear(); big.Routes[6] = new Dictionary<int, GuideRouteOut> { [1] = new GuideRouteOut { Src = "c", Points = Enumerable.Range(0, 150).Select(i => new[] { i / 150.0, 0.5 }).Concat(new[] { new[] { 1.5, 0.5 }, new[] { double.NaN, 0.5 } }).ToList() } };
            big.Nodes[5] = new Dictionary<int, List<double[]>> { [1] = Enumerable.Range(0, 400).Select(i => new[] { (i % 100) / 100.0, (i / 100) / 10.0, 1.0 + i }).Concat(new[] { new[] { 2.0, 0.5, 1.0 } }).ToList() };
            var cl2 = LuaData.ReadGlobals(SharedDataFile.Compose(null, now, caps))["ElansHubShared"] as Dictionary<string, object>;
            var big2 = cl2.Child("profGuides").Child("chars").Child("Realm-Big").Child("171");
            Check("caps: a route keeps 120 points per zone (150 given; x = 1.5 and NaN dropped) and src c", big2.Child("routes").Child("6").Child("1").Child("pts").Count == 120 && big2.Child("routes").Child("6").Child("1").Str("src") == "c");
            Check("caps: nodes keep 300 per object and zone (400 given, x = 2 dropped)", big2.Child("nodes").Child("5").Child("1").Count == 300);
            // the file stays under 400 KB: six guides full of everything are cut down (nodes first, then routes, then guides)
            var huge = new ProfGuidesOut { At = 9 };
            for (int c = 0; c < 6; c++)
            {
                var list = new List<GuideOut>();
                for (int gi = 0; gi < 6; gi++)
                {
                    var hg = new GuideOut { SkillId = 182 + gi, Name = "Herbalism " + gi, Title = "Herbalism", Max = 300 };
                    for (int si = 0; si < 60; si++) hg.Steps.Add(new GuideStepOut { K = "gather", From = si, To = si + 5, Item = 2770, Objects = Enumerable.Range(1000, 12).ToList(), Zones = Enumerable.Range(1400, 8).ToList(), Text = new string('t', 110) });
                    for (int si = 1; si <= 60; si++) hg.Routes[si] = Enumerable.Range(1400, 8).ToDictionary(z => z, z => new GuideRouteOut { Points = Enumerable.Range(0, 120).Select(i => new[] { i / 120.0, 0.25 }).ToList() });
                    foreach (var o in Enumerable.Range(1000, 12)) hg.Nodes[o] = Enumerable.Range(1400, 8).ToDictionary(z => z, z => Enumerable.Range(0, 300).Select(i => new[] { i / 300.0, 0.5, 1.0 + i }).ToList());
                    list.Add(hg);
                }
                huge.Chars["Realm-Huge" + c] = list;
            }
            var hl = SharedDataFile.Compose(null, now, huge);
            Check("size: guides that would be megabytes are cut to under 400 KB and the file is still valid Lua data (" + Encoding.UTF8.GetByteCount(hl) / 1024 + " KB)", Encoding.UTF8.GetByteCount(hl) <= SharedDataFile.MaxFileBytes && LuaData.ReadGlobals(hl).ContainsKey("ElansHubShared") && hl.Contains("profGuides"));
            // extra sections (the Talents hand-off will use this)
            var withExtra = SharedDataFile.Compose(m, now, guides, new[] { "\ttalentBuild = { class = \"HUNTER\", name = \"Pack\", order = { { \"Deadly\", 5 }, }, },\n" });
            var ex = LuaData.ReadGlobals(withExtra)["ElansHubShared"] as Dictionary<string, object>;
            Check("composer: more sections can be added later (an extra fragment lands inside the table next to flights and profGuides)", ex.Child("talentBuild") != null && ex.Child("profGuides") != null && ex.Child("flights") != null);
            Check("composer: the Talents hand-off stays unwired (nothing in the composer or the writer mentions it)", !SharedDataFile.Compose(m, now, guides).Contains("talentBuild"));

            // writing: the one file, only where the toc lists it, flights kept when only the guides change, guides removed on Stop
            var root = Path.Combine(work, "composerwow");
            Directory.CreateDirectory(PgHubDir(root)); Directory.CreateDirectory(PgHubDir(root, "_classic_")); Directory.CreateDirectory(Path.Combine(root, "_retail_"));
            File.WriteAllText(Path.Combine(PgHubDir(root), "ElansHub.toc"), "## Interface: 11500\nChat.lua\nSharedData.lua\n");
            File.WriteAllText(Path.Combine(PgHubDir(root, "_classic_"), "ElansHub.toc"), "## Interface: 11500\nChat.lua\n");       // an old companion: never touched
            File.WriteAllText(Path.Combine(PgHubDir(root), "Chat.lua"), "-- canary\n");
            var dest = Path.Combine(PgHubDir(root), "SharedData.lua");
            Check("write: nothing at all to write (no flights, no guides, no file) -> null and no file", SharedDataFile.Write(root, null, null, now) == null && !File.Exists(dest));
            var w1 = SharedDataFile.Write(root, m, null, now);
            Check("write: flights only -> the same file FlightShare always wrote", w1.Written.Count == 1 && File.ReadAllText(dest) == FlightShare.BuildLua(m, now));
            var w2 = SharedDataFile.Write(root, null, guides, now.AddMinutes(1));
            var onDisk = LuaData.ReadGlobals(File.ReadAllText(dest))["ElansHubShared"] as Dictionary<string, object>;
            Check("write: guides added while no Lodge data is in this session -> the flights already in the file are kept", w2.Written.Count == 1 && onDisk.Child("flights").Child("legs").Count == 2 && onDisk.Child("flights").Child("names").Str("11") == "Orgrimmar" && onDisk.Child("profGuides") != null);
            var w3 = SharedDataFile.Write(root, null, guides, now.AddMinutes(9));
            Check("write: the same guides again (only the header time differs) leave the file alone", w3.Written.Count == 0 && w3.Unchanged.Count == 1);
            var w4 = SharedDataFile.Write(root, null, new ProfGuidesOut(), now.AddMinutes(10));
            var onDisk2 = LuaData.ReadGlobals(File.ReadAllText(dest))["ElansHubShared"] as Dictionary<string, object>;
            Check("write: Stop following (no guides left, no Lodge data in the session) takes the profGuides out and keeps the flights; the text is the flights-only text again",
                w4 != null && w4.Written.Count == 1 && onDisk2.Child("profGuides") == null && onDisk2.Child("flights").Child("legs").Count == 2 && File.ReadAllText(dest).Contains("Horde|11>22"));
            Check("write: with nothing left to remove (no profGuides in the file) nothing is written", SharedDataFile.Write(root, null, new ProfGuidesOut(), now.AddMinutes(11)) == null);
            Check("write: the old companion (toc without SharedData.lua) and the flavor without a folder get nothing; the companion's other files are untouched; no temp file is left",
                !File.Exists(Path.Combine(PgHubDir(root, "_classic_"), "SharedData.lua")) && !Directory.Exists(PgHubDir(root, "_retail_")) && File.ReadAllText(Path.Combine(PgHubDir(root), "Chat.lua")) == "-- canary\n" && !Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories).Any()
                && Directory.GetFiles(PgHubDir(root)).Select(Path.GetFileName).OrderBy(x => x).SequenceEqual(new[] { "Chat.lua", "ElansHub.toc", "SharedData.lua" }));
            Check("write: FileHasGuides sees the section (and not after it was taken out)", !SharedDataFile.FileHasGuides(root) && SharedDataFile.Write(root, null, guides, now.AddMinutes(12)) != null && SharedDataFile.FileHasGuides(root));
            Check("write: a missing or empty WoW folder writes nothing and does not throw", SharedDataFile.Write(null, m, guides, now) != null && SharedDataFile.Write(Path.Combine(work, "nope"), m, guides, now).Written.Count == 0);
            Check("read: ReadFlights re-validates what is in the file (2 legs, names kept); none for a folder without the file", SharedDataFile.ReadFlights(root).Legs.Count == 2 && SharedDataFile.ReadFlights(Path.Combine(work, "nope")) == null);
            Check("keys: a character key must be 1-80 printable characters", SharedDataFile.ValidCharKey("Realm-Name") && !SharedDataFile.ValidCharKey("") && !SharedDataFile.ValidCharKey(new string('a', 81)) && !SharedDataFile.ValidCharKey("a\nb"));
        }

        // ================================================================================================================ follow in game
        static FixedGatherWorld PgWorld()
        {
            var w = new FixedGatherWorld();
            w.Zones[1429] = new ZoneInfo { Id = 1429, Name = "Elwynn Forest", Median = 7, MinLevel = 1, MaxLevel = 10 };
            w.Zones[1436] = new ZoneInfo { Id = 1436, Name = "Westfall", Median = 12, MinLevel = 10, MaxLevel = 15 };
            w.ByProfession["Herbalism"] = new List<GatherEntity>
            {
                new GatherEntity { Kind = "obj", Id = 1618, Name = "Peacebloom", Items = { 104 }, ClassicZones = { [1429] = 12 }, LodgeZones = { [1429] = 9 } },
                new GatherEntity { Kind = "obj", Id = 1617, Name = "Silverleaf", Items = { 103 }, ClassicZones = { [1429] = 4, [1436] = 6 }, LodgeZones = { [1429] = 3, [1436] = 2 } },
                new GatherEntity { Kind = "obj", Id = 1620, Name = "Mageroyal", Items = { 102 }, ClassicZones = { [1436] = 8 } },
            };
            return w;
        }

        static FixedNodeSource PgNodeSource()
        {
            var f = new FixedNodeSource();
            f.AddForever(1618, 1429, Enumerable.Range(0, 9).Select(i => PgN(0.1 + 0.09 * i, 0.2 + 0.05 * (i % 3), 2 + i)).ToArray());
            f.AddForever(1617, 1429, PgN(0.15, 0.7, 4), PgN(0.5, 0.75, 1), PgN(0.85, 0.7, 2));
            f.AddForever(1617, 1436, PgN(0.3, 0.3, 5), PgN(0.7, 0.4, 3));
            f.AddClassic(1620, 1436, PgN(0.2, 0.6), PgN(0.4, 0.65), PgN(0.6, 0.7), PgN(0.8, 0.75), PgN(0.5, 0.9));
            f.AddClassic(1618, 1429, PgN(0.1, 0.2), PgN(0.95, 0.95));
            f.Names[1618] = "Peacebloom"; f.Names[1617] = "Silverleaf"; f.Names[1620] = "Mageroyal";
            foreach (var o in new[] { 1618, 1617, 1620 }) f.GroupsOf[o] = GatherNodes.Herbs;
            f.Yards[1429] = (2000, 1500); f.Yards[1436] = (2400, 1600);
            return f;
        }

        static Dictionary<string, object> PgShared(string file) => LuaData.ReadGlobals(File.ReadAllText(file))["ElansHubShared"] as Dictionary<string, object>;

        async Task ProfGuideFollow(Action<string, bool, string> Check0, string work, string dir)
        {
            void Check(string what, bool cond, string detail = null) => Check0(what, cond, detail);
            var syn = ProfBuild();
            SpellData.SetForTest(syn.Set);
            ItemData.SetForTest(new ItemSnapshot(syn.Items, null), ItemData.State.Ready);
            var page = ProfessionsView;
            page.ResetForTest();
            var root = Path.Combine(work, "followwow");
            Directory.CreateDirectory(PgHubDir(root));
            File.WriteAllText(Path.Combine(PgHubDir(root), "ElansHub.toc"), "## Interface: 11500\nSharedData.lua\n");
            var dest = Path.Combine(PgHubDir(root), "SharedData.lua");
            File.WriteAllText(dest, PgFlightsFile(now0(), root));
            settings.WowRoot = root;
            settings.FollowedGuides = new List<FollowedGuide>(); settings.SavedRoutes = new List<SavedRoute>(); settings.RouteForGuide = new Dictionary<string, string>();
            SharedDataFile.Current = null; Session.FlightResetForTest();
            var nodes = PgNodeSource();
            page.NodeSourceForTest = nodes;
            var alice = PgChar("Alice", "MAGE", "Tailoring", 20, 150, "Cooking", 60, 75, 100, 30, 101, 2);
            var dora = PgChar("Dora", "DRUID", "Herbalism", 1, 150);
            var eve = PChar("Eve", "ROGUE", "Tailoring", 10, 75);           // not from the Hub: her key is not the game's
            page.SetProfCharsForTest(new[] { alice, dora, eve });
            page.SetSourcesForTest(PSources(), PgWorld());
            ShowTab("professions");
            await Task.Delay(100);

            // ---- crafting guide
            page.ShowForTest("guide", "Tailoring", "Alice");
            page.GuideRangeForTest(null, null, "char");
            var btn = page.FollowButtonForTest;
            Check("follow: the Guide tab has a 'Follow in game' button for a character from the Hub, enabled, and no status line about the game yet", btn != null && (string)btn.Content == "Follow in game" && btn.IsEnabled && !page.FollowStatusForTest.Contains("Shows in game"));
            btn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await page.FollowTaskForTest;
            await Task.Delay(50);
            Check("follow: the guide is remembered in the settings (character key, skill line id)", settings.FollowedGuides.Count == 1 && settings.FollowedGuides[0].CharKey == "Realm-Alice" && settings.FollowedGuides[0].SkillId == 197);
            var sh = PgShared(dest);
            var ag = sh.Child("profGuides")?.Child("chars")?.Child("Realm-Alice")?.Child("197");
            Check("follow: SharedData.lua now holds Alice's Tailoring guide next to the flights (which stay)", ag != null && ag.Str("name") == "Tailoring" && ag.Int("max") == 150 && ag.Str("title").StartsWith("Tailoring 20-") && sh.Child("flights").Child("legs").Count == 1);
            var asteps = ag?.Child("steps");
            var firstCraft = asteps?.Select(kv => kv.Value).OfType<Dictionary<string, object>>().FirstOrDefault(s => s.Str("k") == "craft");
            Check("follow: the steps are the page's guide: craft steps with the recipe spell, suggested crafts, reagents; a train step where the rank cap is reached (75)",
                firstCraft != null && firstCraft.Int("spell") > 0 && firstCraft.Int("n") >= 1 && firstCraft.Child("r") != null && firstCraft.Child("r").Count >= 1 && asteps.Select(kv => kv.Value).OfType<Dictionary<string, object>>().Any(s => s.Str("k") == "train" && s.Int("from") == 75 && s.Str("t") == "Journeyman Tailoring"),
                asteps == null ? "no steps" : string.Join(",", asteps.Select(kv => ((Dictionary<string, object>)kv.Value).Str("k") + ((Dictionary<string, object>)kv.Value).Int("from"))));
            Check("follow: the page says when it shows in game, and which Toolkit it needs", page.FollowStatusForTest.Contains("Shows in game after /reload or your next login") && page.FollowStatusForTest.Contains("Toolkit 0.4.0"), page.FollowStatusForTest);
            Check("follow: the button now says 'Stop following'", page.FollowButtonForTest != null && (string)page.FollowButtonForTest.Content == "Stop following");
            // a second profession of the same character; a skill change of a followed character remakes the guide
            Check("follow: the file keeps changes small - rebuilding with the same data leaves SharedData.lua alone", await PgRebuildUnchanged(page, dest));
            alice.Skills["Tailoring"] = new KeyValuePair<int, int>(60, 150);
            page.FollowRebuildNowForTest(); await page.FollowTaskForTest;
            var ag2 = PgShared(dest).Child("profGuides").Child("chars").Child("Realm-Alice").Child("197");
            Check("follow: when the character's skill changed the guide is made again from the new skill (title Tailoring 60-)", ag2.Str("title").StartsWith("Tailoring 60-"), ag2.Str("title"));
            // stop
            page.FollowButtonForTest.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await page.FollowTaskForTest; await Task.Delay(50);
            var sh2 = PgShared(dest);
            Check("stop: 'Stop following' takes the guide out of the file (the flights stay) and out of the settings", sh2.Child("profGuides") == null && sh2.Child("flights").Child("legs").Count == 1 && settings.FollowedGuides.Count == 0 && SharedDataFile.Current == null);
            Check("stop: the button is 'Follow in game' again; the status says the game shows the change after /reload", (string)page.FollowButtonForTest.Content == "Follow in game" && page.FollowStatusForTest.Contains("/reload"), page.FollowStatusForTest);
            // a character the Hub never saved cannot be followed
            page.ShowForTest("guide", "Tailoring", "Eve");
            Check("follow: a character without Elan's Hub data gets a disabled button and says why", page.FollowButtonForTest != null && !page.FollowButtonForTest.IsEnabled && page.FollowStatusForTest.Contains("Elan's Hub data"));
            // the file is only written where the toc lists it
            File.WriteAllText(Path.Combine(PgHubDir(root), "ElansHub.toc"), "## Interface: 11500\nChat.lua\n");
            File.Delete(dest);
            page.ShowForTest("guide", "Tailoring", "Alice");
            page.FollowButtonForTest.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await page.FollowTaskForTest; await Task.Delay(50);
            Check("follow: an old Elan's Hub (toc without SharedData.lua) is never written to; the page says no Hub folder was found", !File.Exists(dest) && page.FollowStatusForTest.Contains("no Elan's Hub addon folder was found"), page.FollowStatusForTest);
            page.FollowButtonForTest.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await page.FollowTaskForTest;
            File.WriteAllText(Path.Combine(PgHubDir(root), "ElansHub.toc"), "## Interface: 11500\nSharedData.lua\n");
            File.WriteAllText(dest, PgFlightsFile(now0(), root));

            // ---- gathering guide: steps, routes, pins
            page.ShowForTest("guide", "Herbalism", "Dora");
            await page.WaitGatherForTest();
            page.FollowButtonForTest.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await page.FollowTaskForTest; await Task.Delay(50);
            var dg = PgShared(dest).Child("profGuides")?.Child("chars")?.Child("Realm-Dora")?.Child("182");
            var dsteps = dg?.Child("steps");
            string Kinds() => dsteps == null ? "-" : string.Join(",", dsteps.Select(kv => ((Dictionary<string, object>)kv.Value).Str("k") + ((Dictionary<string, object>)kv.Value).Int("from")));
            Check("gather: Herbalism for Dora is train@1, gather 1-50, gather 51-100, train@75 (train before gather at the same skill, then skill order)", dsteps != null && Kinds() == "train1,gather1,gather51,train75", Kinds());
            var g1 = dsteps?.Child("2"); var g2 = dsteps?.Child("3");
            Check("gather: step 2 (skill 1-50): the item it gives, both plants (obj), Elwynn Forest (z), a text naming them",
                g1 != null && g1.Int("to") == 50 && g1.Int("item") == 104 && g1.Child("obj").Values.Select(v => Convert.ToInt32(v)).OrderBy(i => i).SequenceEqual(new[] { 1617, 1618 }) && g1.Child("z").Int("1") == 1429 && g1.Str("t").Contains("Peacebloom") && g1.Str("t").Contains("Elwynn Forest"), g1?.Str("t"));
            var rt1 = dg?.Child("routes")?.Child("2")?.Child("1429");
            Check("gather: the loop for Elwynn Forest has the 12 Forever nodes of both plants (8 or more -> Forever only, src f, the 2 Classic positions are left out)", rt1 != null && rt1.Str("src") == "f" && rt1.Child("pts").Count == 12, rt1 == null ? "no route" : rt1.Str("src") + " " + rt1.Child("pts").Count);
            var rt2 = dg?.Child("routes")?.Child("3")?.Child("1436");
            Check("gather: Westfall has only 2 Forever nodes -> the 5 Classic positions fill the loop: 7 stops, src c", rt2 != null && rt2.Str("src") == "c" && rt2.Child("pts").Count == 7, rt2 == null ? "no route" : rt2.Str("src") + " " + rt2.Child("pts").Count);
            var pin = dg?.Child("nodes")?.Child("1618")?.Child("1429");
            Check("gather: the pins of Peacebloom in Elwynn: 9 Forever nodes with their real counts (n >= 1) first, then the Classic baseline position that is not on one of them (n = 0)", pin != null && pin.Count == 10 && Enumerable.Range(1, 9).All(i => pin.Child(i.ToString()).Int("3") >= 1) && pin.Child("10").Int("3") == 0, pin == null ? "none" : pin.Count.ToString());
            var pin2 = dg?.Child("nodes")?.Child("1620")?.Child("1436");
            Check("gather: Classic-only Mageroyal gets pins with n = 0 (5), shown fainter in game", pin2 != null && pin2.Count == 5 && Enumerable.Range(1, 5).All(i => pin2.Child(i.ToString()).Int("3") == 0));
            Check("gather: the pins are only for the objects the guide's steps use, never an object it does not mention", dg.Child("nodes").Keys.All(k => k == "1617" || k == "1618" || k == "1620"));
            // a saved route chosen for the guide replaces the automatic one for that profession and zone
            settings.SavedRoutes.Add(new SavedRoute { Id = "r1", Name = "My Elwynn loop", Zone = 1429, Objects = new List<int> { 1618 }, Src = "f", Points = new List<double> { 0.1, 0.1, 0.9, 0.9, 0.5, 0.2 } });
            settings.RouteForGuide["herbalism:1429"] = "r1";
            page.RouteChoiceChanged();
            page.FollowRebuildNowForTest(); await page.FollowTaskForTest;
            var rt1b = PgShared(dest).Child("profGuides").Child("chars").Child("Realm-Dora").Child("182").Child("routes").Child("2").Child("1429");
            var rt2b = PgShared(dest).Child("profGuides").Child("chars").Child("Realm-Dora").Child("182").Child("routes").Child("3").Child("1436");
            Check("saved route: 'Use for guide' on Elwynn gives the saved 3-stop loop for that zone; Westfall stays the automatic loop", rt1b.Child("pts").Count == 3 && Math.Abs(PgD(rt1b.Child("pts").Child("2"), "1") - 0.9) < 1e-9 && rt2b.Child("pts").Count == 7);
            settings.RouteForGuide["herbalism:1429"] = "gone";
            page.FollowRebuildNowForTest(); await page.FollowTaskForTest;
            Check("saved route: a choice that points to a route that no longer exists falls back to the automatic loop", PgShared(dest).Child("profGuides").Child("chars").Child("Realm-Dora").Child("182").Child("routes").Child("2").Child("1429").Child("pts").Count == 12);
            settings.RouteForGuide.Clear(); settings.SavedRoutes.Clear();
            // two followed guides at once, at most six per character
            page.ShowForTest("guide", "Tailoring", "Alice");
            page.SetFollow(page.WhoForTest, ProfessionBookFor("Tailoring"), true); await page.FollowTaskForTest; await Task.Delay(50);
            var both = PgShared(dest).Child("profGuides").Child("chars");
            Check("follow: two characters' guides can be followed at once and are both in the file", both.Child("Realm-Alice") != null && both.Child("Realm-Dora") != null && settings.FollowedGuides.Count == 2);

            // ---- rebuild when the nodes change (debounced) and when the Lodge pool changes
            page.FollowDebounce = TimeSpan.FromMilliseconds(80);
            int builds0 = page.FollowBuildsForTest;
            var lodgeNow = Session.AtlasData;
            lodgeNow.Clear();
            page.FollowStopDebounceForTest();
            page.FollowRebuildNowForTest(); await page.FollowTaskForTest;                       // takes the current nodes as the starting point
            builds0 = page.FollowBuildsForTest;
            Check("rebuild: nothing changed -> the watch does not schedule a rebuild", !CheckSig(page));
            lodgeNow.Apply(Json.Obj("{\"t\":\"atlas.data\",\"epoch\":\"pgf\",\"rev\":1,\"at\":" + AtlasAt + ",\"full\":true,\"nodes\":{\"1618\":{\"1429\":[{\"x\":0.5,\"y\":0.5,\"n\":2}]}}}"));
            page.FollowSignatureCheckForTest();
            Check("rebuild: new Lodge nodes schedule a (debounced) rebuild - several changes in a row make one", page.FollowDebounceRunningForTest);
            lodgeNow.Apply(Json.Obj("{\"t\":\"atlas.data\",\"epoch\":\"pgf\",\"rev\":2,\"at\":" + AtlasAt + ",\"nodes\":{\"1618\":{\"1429\":[{\"x\":0.5,\"y\":0.5,\"n\":3}]}}}"));
            page.FollowSignatureCheckForTest();
            await WaitFor(() => page.FollowBuildsForTest > builds0, 5000);
            await page.FollowTaskForTest;
            Check("rebuild: after the debounce exactly one rebuild ran for the two changes", page.FollowBuildsForTest == builds0 + 1, (page.FollowBuildsForTest - builds0).ToString());
            page.FollowStopDebounceForTest(); lodgeNow.Clear();
            // unfollow all
            foreach (var f in settings.FollowedGuides.ToList()) page.SetFollow(f.CharKey == "Realm-Dora" ? dora : alice, f.CharKey == "Realm-Dora" ? ProfessionBookFor("Herbalism") : ProfessionBookFor("Tailoring"), false);
            await page.FollowTaskForTest; await Task.Delay(50);
            Check("stop: stopping the last guide leaves a plain flights file again", PgShared(dest).Child("profGuides") == null && settings.FollowedGuides.Count == 0);

            // ---- a screenshot of the Guide tab with the follow row
            ShowTab("professions"); page.ShowForTest("guide", "Tailoring", "Alice"); page.GuideRangeForTest(null, null, "char");
            page.SetFollow(alice, ProfessionBookFor("Tailoring"), true); await page.FollowTaskForTest; await Task.Delay(300); UpdateLayout();
            Snapshot(Path.Combine(dir, "profguide-follow.png"));
            page.SetFollow(alice, ProfessionBookFor("Tailoring"), false); await page.FollowTaskForTest;
            page.NodeSourceForTest = null;
        }

        bool CheckSig(ProfessionsPage page) { page.FollowSignatureCheckForTest(); bool running = page.FollowDebounceRunningForTest; return running; }

        Profession ProfessionBookFor(string name) => ProfessionsView.BookForTest?.ByName(name);

        async Task<bool> PgRebuildUnchanged(ProfessionsPage page, string dest)
        {
            var before = File.ReadAllText(dest); var stamp = File.GetLastWriteTimeUtc(dest);
            await Task.Delay(30);
            page.FollowRebuildNowForTest(); await page.FollowTaskForTest;
            return File.ReadAllText(dest) == before && File.GetLastWriteTimeUtc(dest) == stamp;
        }

        // ================================================================================================================ the Atlas: nodes, routes
        async Task ProfGuideAtlas(Action<string, bool, string> Check0, string work, string dir)
        {
            void Check(string what, bool cond, string detail = null) => Check0(what, cond, detail);
            MapRender.DirOverride = Path.Combine(work, "maps");
            MapRender.TileSourceForTest = fdid => Solid(256, 256, (byte)(70 + fdid % 40), (byte)(96 + fdid % 30), (byte)(60 + fdid % 25));
            var classic = AtlasTestClassic();
            classic.Objects[51] = new ClassicObject { Id = 51, Name = "Tin Vein", Type = 3, LootId = 2, Spawns = new[] { Sp(0, 300, 1000), Sp(0, 320, 1100), Sp(0, 500, 400) } };
            classic.Objects[52] = new ClassicObject { Id = 52, Name = "Mossy Stump", Type = 5, Spawns = new[] { Sp(0, 100, 100) } };
            ItemData.SetForTest(AtlasTestItems(), ItemData.State.Ready);
            SpellData.SetForTest(AtlasTestSpells());
            SeenData.SetForTest(AtlasTestSeen());
            MapData.SetForTest(new MapIndex(AtlasTestMapSet()));
            MapRender.ForgetFailuresForTest();
            ClassicAtlas.Use(classic);
            AtlasBrowse.ForgetForTest();
            var lodge = new LodgeAtlas();
            lodge.Apply(Json.Obj("{\"t\":\"atlas.data\",\"epoch\":\"pga\",\"rev\":1,\"at\":" + AtlasAt + ",\"full\":true,\"nodes\":{"
                + "\"50\":{\"11\":[{\"x\":0.20,\"y\":0.30,\"n\":6},{\"x\":0.40,\"y\":0.35,\"n\":2},{\"x\":0.60,\"y\":0.30,\"n\":1}],\"13\":[{\"x\":0.5,\"y\":0.5,\"n\":3}]},"
                + "\"51\":{\"11\":[{\"x\":0.25,\"y\":0.60,\"n\":4},{\"x\":0.55,\"y\":0.65,\"n\":2},{\"x\":0.80,\"y\":0.70,\"n\":9}]}}}"));
            var own = new Dictionary<int, Dictionary<int, List<AtlasObs.NodePt>>>
            {
                [50] = new Dictionary<int, List<AtlasObs.NodePt>> { [11] = new List<AtlasObs.NodePt> { new AtlasObs.NodePt { X = 0.2005, Y = 0.3, N = 8 }, new AtlasObs.NodePt { X = 0.85, Y = 0.2, N = 1 } } },
            };
            AtlasQuery.Context.Lodge = () => lodge; AtlasQuery.Context.OwnNodes = () => own; AtlasQuery.Context.NowUtc = () => AtUtc(AtNow);
            AtlasQuery.Context.Owned = AtlasTestOwned; AtlasView.OwnedOverrideForTest = AtlasTestOwned;
            AtlasView.UseRouteSettings(settings, null);
            settings.SavedRoutes = new List<SavedRoute>(); settings.RouteForGuide = new Dictionary<string, string>();

            // ---- the page model: Forever nodes with labels, above the Classic positions
            var obj = AtlasQuery.Build("obj:50");
            var z11 = obj.Map.Zones.FirstOrDefault(z => z.UiMapId == 11);
            var fnodes = z11?.Markers.Where(m => m.Kind == MarkerKind.Node && m.Tooltip.Contains(AtlasLabel.Forever)).ToList();
            var cnodes = z11?.Markers.Where(m => m.Kind == MarkerKind.Node && m.Tooltip.Contains(AtlasLabel.Classic)).ToList();
            Check("object page: the Forever nodes of Ancient Chest on Greenvale: 3 from the Lodge + 1 of mine that is not on one (my 0.2005 spot is the Lodge's 0.20 node) = 4, labelled, with the object's name and 'Looted n times'",
                fnodes != null && fnodes.Count == 4 && fnodes.All(m => m.Label == "Ancient Chest" && m.LinkId == "obj:50") && fnodes.Any(m => m.Tooltip.StartsWith("Looted 8 times")) && fnodes.Any(m => m.Tooltip.StartsWith("Looted once")), fnodes == null ? "no zone" : string.Join(" | ", fnodes.Select(m => m.Tooltip)));
            Check("object page: the Classic baseline's position is there too, labelled Classic baseline, and the Forever markers come after it (drawn above)", cnodes != null && cnodes.Count == 1 && z11.Markers.IndexOf(cnodes[0]) < z11.Markers.IndexOf(fnodes[0]));
            Check("object page: the zone with the Forever nodes comes first (Greenvale 4 Forever); Highkeep has the Lodge's one node of that object", obj.Map.Zones[0].UiMapId == 11 && obj.Map.Zones.Any(z => z.UiMapId == 13 && z.Markers.Count(m => m.Tooltip.Contains(AtlasLabel.Forever)) == 1));
            var zone = AtlasQuery.Build("zone:11");
            var nsec = zone.Section("nodes");
            Check("zone page: a 'Gathering nodes' section lists the objects that have Forever nodes on the map (Ancient Chest 4, Tin Vein 3) and the map shows their nodes",
                nsec != null && nsec.Rows.Count == 2 && nsec.Rows.All(r => r.Label == AtlasLabel.Forever) && nsec.Rows.Any(r => r.Title == "Ancient Chest" && r.Detail == "4 nodes") && nsec.Rows.Any(r => r.Title == "Tin Vein" && r.Detail == "3 nodes")
                && zone.Map.Zones.Any(z => z.UiMapId == 11 && z.Markers.Count(m => m.Label == "Tin Vein") == 3), nsec == null ? "no section" : string.Join(",", nsec.Rows.Select(r => r.Title + ":" + r.Detail)));
            var onlyForever = AtlasQuery.Build("obj:51");
            Check("object page: Tin Vein has 3 Forever and 3 Classic positions", onlyForever.Map.Zones[0].Markers.Count(m => m.Tooltip.Contains(AtlasLabel.Forever)) == 3 && onlyForever.Map.Zones[0].Markers.Count(m => m.Tooltip.Contains(AtlasLabel.Classic)) >= 1);
            AtlasQuery.Context.OwnNodes = () => null; AtlasQuery.Context.Lodge = () => null;
            Check("object page: without nodes (no Lodge, no own) the page is the Classic one as before", AtlasQuery.Build("obj:50").Map.Zones.SelectMany(z => z.Markers).All(m => !m.Tooltip.Contains(AtlasLabel.Forever)) && AtlasQuery.Build("zone:11").Section("nodes") == null);
            AtlasQuery.Context.OwnNodes = () => own; AtlasQuery.Context.Lodge = () => lodge;

            // ---- browse: Forever nodes decide the zone of an object, and an object only players looted is listed
            AtlasBrowse.ForgetForTest();
            var listing = AtlasBrowse.Run("browse:objects?sort=zone");
            var chestRow = listing?.Rows.FirstOrDefault(r => r.Title == "Ancient Chest");
            Check("browse 'by zone': Ancient Chest is listed under Greenvale with the Forever mark (its Forever nodes decide the zone before the Classic baseline's)", chestRow != null && chestRow.Extra.Contains("Greenvale") && chestRow.Extra.Contains(AtlasLabel.Forever), chestRow == null ? "no row" : chestRow.Detail + " / " + chestRow.Extra);

            // ---- the page in the window: the Routes panel
            var nodeSrc = new LiveNodeSource();
            Height = 1000; ShowTab("atlas");
            await AtlasView.Open("obj:50"); UpdateLayout(); await SwitchTab("map");
            Check("window: the object page has a 'Show route' button on the map card", await MapReady() && AtlasView.routeButtons.Count == 1 && (string)AtlasView.routeButtons[0].Content == "Show route");
            AtlasView.routeButtons[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var ui = AtlasView.LastRoutesForTest;
            await ui.Loading; await Task.Delay(100); UpdateLayout();
            Check("routes panel: opened from an object page it lists the zone's gathering objects (Ancient Chest under Chests, Tin Vein under Ore; Mossy Stump is plain scenery and not listed), ticks the page's object and builds at once",
                ui.IsOpen && ui.Checks.ContainsKey(50) && ui.Checks.ContainsKey(51) && !ui.Checks.ContainsKey(52) && ui.Checks[50].IsChecked == true && ui.Checks[51].IsChecked != true && ui.Last != null, string.Join(",", ui.Checks.Keys));
            Check("routes panel: the loop on the map: numbered stops (the 4 Forever nodes; fewer than 8 so the Classic position is added = 5), a line, the summary with nodes / minutes / yards and the Classic note",
                AtlasView.MapsForTest[0].RouteCount == 5 && ui.Last.Count == 5 && ui.Last.UsedClassic && ui.SummaryText.Contains("5 nodes") && ui.SummaryText.Contains("minute") && ui.SummaryText.Contains("yards") && ui.StatusText.Contains("Only 4 Forever nodes"), ui.SummaryText + " / " + ui.StatusText);
            // several kinds in one loop
            ui.Check(51, true);
            ui.Build();
            Check("routes panel: Ancient Chest + Tin Vein in one loop: 4 + 3 Forever nodes = 7 stops (still fewer than 8, so the Classic baseline's positions join)", ui.Last.Points.Select(p => p.Object).Distinct().Count() == 2 && ui.Last.ForeverNodes == 7 && ui.Last.UsedClassic && AtlasView.MapsForTest[0].RouteCount == ui.Last.Count, ui.Last.Count + " " + ui.Last.ForeverNodes);
            own[50][11].AddRange(new[] { new AtlasObs.NodePt { X = 0.1, Y = 0.9, N = 1 }, new AtlasObs.NodePt { X = 0.3, Y = 0.9, N = 1 } });
            ui.Build();
            Check("routes panel: with 9 Forever nodes (8 or more) the loop is Forever only: the Classic positions are left out and the line says all Forever", ui.Last.ForeverNodes == 9 && !ui.Last.UsedClassic && ui.Last.Points.All(p => p.Forever) && ui.SummaryText.Contains("all " + AtlasLabel.Forever + " nodes"), ui.SummaryText);
            Snapshot(Path.Combine(dir, "profguide-routes.png"));
            ui.Check(50, false); ui.Check(51, false);
            ui.Build();
            Check("routes panel: nothing ticked -> a calm message, no route", ui.SummaryText == "Tick at least one object." && AtlasView.MapsForTest[0].RouteCount == 0 && ui.Last == null);
            ui.Check(51, true); ui.Build();
            // save, show, use for guide, delete
            ui.NameBox.Text = "Tin run";
            Check("routes panel: Save is enabled once there is a route", ui.SaveButton.IsEnabled);
            var saved = ui.Save(ui.NameBox.Text);
            Check("save: a named route per zone goes into the settings (zone, objects, frozen stops, src) and is listed under the map", saved != null && settings.SavedRoutes.Count == 1 && settings.SavedRoutes[0].Name == "Tin run" && settings.SavedRoutes[0].Zone == 11 && settings.SavedRoutes[0].Objects.SequenceEqual(new[] { 51 }) && ui.SavedButtons.Count == 1);
            ui.Check(51, false); ui.Check(50, true); ui.Build(); ui.NameBox.Text = "Chests"; ui.Save("Chests");
            Check("save: several routes can be kept for a zone", settings.SavedRoutes.Count == 2 && ui.SavedButtons.Count == 2);
            int changedCalls = 0;
            AtlasView.UseRouteSettings(settings, () => changedCalls++);
            ui.ShowSaved(saved.Id);
            Check("show: a saved route comes back on the map with its summary", AtlasView.MapsForTest[0].RouteCount == saved.ToNodes().Count && ui.SummaryText.StartsWith("\"Tin run\":"));
            var useBtn = ui.SavedButtons[0];
            Check("use for guide: an ore route can be used for the Mining guide, a chest route cannot (chests are no profession)", useBtn.IsEnabled && ui.SavedButtons[1].IsEnabled == false);
            ui.UseForGuide(saved.Id);
            var key = GuideHandoff.RouteKey("Mining", 11);
            Check("use for guide: the choice is remembered per profession and zone (mining:11) and the in-game guide is told to be made again", settings.RouteForGuide.TryGetValue(key, out var chosen) && chosen == saved.Id && changedCalls == 1 && ui.SavedButtons[0].Content.ToString().StartsWith("Used for the Mining guide"), string.Join(",", settings.RouteForGuide.Keys));
            ui.UseForGuide(saved.Id);
            Check("use for guide: pressing it again goes back to the automatic loop", !settings.RouteForGuide.ContainsKey(key) && changedCalls == 2);
            ui.UseForGuide(saved.Id);
            ui.Delete(saved.Id);
            Check("delete: removes the route and any guide choice that pointed at it (and tells the guide)", settings.SavedRoutes.Count == 1 && !settings.RouteForGuide.ContainsKey(key) && ui.SavedButtons.Count == 1 && changedCalls == 4);
            // "Show route" from the gathering guide: the zone page opens with the panel, the objects ticked, the loop built
            GatherNodes.Pending = new GatherNodes.RouteRequest { Zone = 11, Objects = new List<int> { 51 } };
            await AtlasView.Open("zone:11"); UpdateLayout();
            await MapReady();
            var ui2 = AtlasView.LastRoutesForTest;
            await WaitFor(() => ui2.IsOpen && ui2.Last != null, 8000);
            Check("guide link: 'Show route' of the gathering guide opens the zone with the Routes panel open, Tin Vein ticked and its loop built", ui2.IsOpen && ui2.Selected().SequenceEqual(new[] { 51 }) && ui2.Last != null && ui2.Last.ForeverNodes == 3 && GatherNodes.Pending == null);
            Check("zone page: the map card has a Routes button", AtlasView.routeButtons.Count == 1 && (string)AtlasView.routeButtons[0].Content == "Routes");
            // the gathering guide page links to it
            var pg = ProfessionsView;
            pg.ResetForTest();
            pg.SetProfCharsForTest(new[] { PgChar("Dora", "DRUID", "Herbalism", 1, 150) });
            var syn = ProfBuild();
            SpellData.SetForTest(syn.Set);
            ItemData.SetForTest(new ItemSnapshot(syn.Items, null), ItemData.State.Ready);
            pg.SetSourcesForTest(PSources(), PgWorld());
            pg.ShowForTest("guide", "Herbalism", "Dora");
            await pg.WaitGatherForTest();
            await Task.Delay(100); UpdateLayout();
            var routeLinks = new List<TextBlock>();
            void Walk(DependencyObject o) { if (o is TextBlock tb && tb.Tag as string == "route") routeLinks.Add(tb); foreach (var c in LogicalTreeHelper.GetChildren(o).OfType<DependencyObject>()) Walk(c); }
            Walk(pg.BodyForTest);
            string opened = null; Action<string> hook = id => opened = id;
            ProfessionLinks.OpenRequested -= OnProfessionLink; ProfessionLinks.OpenRequested += hook;
            if (routeLinks.Count > 0) routeLinks[0].RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
            ProfessionLinks.OpenRequested -= hook; ProfessionLinks.OpenRequested += OnProfessionLink;
            Check("gathering guide: every zone of a bracket has a 'Show route' link that asks for the zone page and passes the zone's objects", routeLinks.Count >= 2 && opened == "zone:1429" && GatherNodes.Pending != null && GatherNodes.Pending.Zone == 1429 && GatherNodes.Pending.Objects.OrderBy(i => i).SequenceEqual(new[] { 1617, 1618 }), opened + " links " + routeLinks.Count);
            GatherNodes.Pending = null;
            Height = 800;
        }
    }
}
