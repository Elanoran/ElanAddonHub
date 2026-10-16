using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Talents;

namespace ElansAddonHub
{
    // Full-size talent trees for the self-test (sections art, characters, talents): the Hunter's and the Warrior's three trees with the shape of this
    // game's data (4 columns x 7 rows, the node ids, the cells, the max ranks and the prerequisite pairs as TalentData reads them from the client of
    // build 1.60.1.70374; Hunter talent names, spell and icon ids as Elan's Hub saves them), the 5-points-per-row gates, 51 points at level 60, and a
    // 51-point build for each that follows the game's rules. Plus the Hub's talents module of such a character with the game's two odd positions:
    // the hidden copy of Lightning Reflexes (node 104982, x 102800) and Improved Serpent Sting's y 39300.
    public partial class MainWindow
    {
        // id,spec,col,row,maxRanks,spellId,iconId,name
        static readonly string FullHunterLayout =
            "104960,0,1,0,5,19552,136076,Deadly Aspects;104976,0,2,0,5,19583,136080,Endurance Training;104975,0,0,1,2,1223755,135539,Focused Fire;"
            + "104974,0,1,1,3,19549,132159,Improved Aspect of the Monkey;104973,0,2,1,2,19559,132242,Pathfinding;104972,0,3,1,2,24443,132163,Improved Revive Pet;"
            + "104970,0,1,2,1,19596,132120,Bestial Swiftness;104969,0,2,2,5,19616,132091,Unleashed Fury;104968,0,0,3,2,19572,132179,Improved Mend Pet;"
            + "104967,0,2,3,5,19598,134297,Ferocity;104966,0,3,3,1,1293241,132158,Summon Hawk;104965,0,0,4,2,19578,132121,Spirit Bond;"
            + "104964,0,1,4,1,19577,132111,Intimidation;104963,0,3,4,2,19590,136006,Bestial Discipline;104962,0,2,5,5,19621,134296,Frenzy;"
            + "104961,0,1,6,1,19574,132127,Bestial Wrath;105013,1,0,0,3,19498,132327,Hawk Eye;105012,1,1,0,5,19407,135860,Improved Concussive Shot;"
            + "105011,1,2,0,5,19426,132312,Lethal Attacks;110870,1,0,1,3,1310661,132204,Improved Stings;105009,1,1,1,5,19416,135865,Efficiency;"
            + "105008,1,2,1,5,1223984,132217,Careful Aim;105005,1,0,2,2,415405,132205,Rapid Killing;105006,1,1,2,5,19454,132218,Improved Arcane Shot;"
            + "105007,1,3,2,1,415370,132266,Lone Wolf;105004,1,1,3,1,1299346,132329,Trueshot Aura;105002,1,2,3,5,19485,132271,Mortal Shots;"
            + "105003,1,3,3,5,19464,132204,Improved Serpent Sting;104999,1,0,4,2,1223987,236201,Rapid Recuperation;105001,1,2,4,3,19461,132330,Barrage;"
            + "105000,1,3,4,1,19503,132153,Scatter Shot;104998,1,2,5,5,19507,135615,Ranged Weapon Specialization;104997,1,1,6,1,1310687,1412205,Sniper Shot;"
            + "104996,2,1,0,5,24293,134154,Improved Tracking;104995,2,2,0,5,19295,132269,Deflection;104994,2,0,1,5,19184,136100,Entrapment;"
            + "104993,2,1,1,2,19159,132277,Savage Strikes;104992,2,2,1,5,19255,136223,Survivalist;104990,2,3,1,3,19228,132309,Improved Wing Clip;"
            + "104991,2,0,2,2,19239,136106,Clever Traps;104987,2,1,2,3,19290,132219,Surefooted;104986,2,2,2,1,19263,132369,Deterrence;"
            + "104988,2,0,3,2,19376,132149,Survival Tactics;110861,2,1,3,5,1310627,132147,Predator's Edge;104989,2,2,3,1,19306,132336,Counterattack;"
            + "104983,2,0,4,2,440529,132207,Resourcefulness;104985,2,1,4,2,1310532,132215,Expose Prey;110860,2,2,4,2,1310496,132178,Survivalist's Discipline;"
            + "104981,2,3,4,1,1317257,132198,Strider Kick;110859,2,2,5,5,19168,136047,Lightning Reflexes;104984,2,1,6,1,1310533,132155,Lacerating Strikes;";
        static readonly string FullWarriorLayout =
            "105958,0,0,0,3,905958,0,Arms 1;105957,0,1,0,5,905957,0,Arms 2;105956,0,2,0,3,905956,0,Arms 3;105955,0,0,1,2,905955,0,Arms 4;"
            + "105954,0,1,1,5,905954,0,Arms 5;105952,0,3,1,2,905952,0,Arms 6;105951,0,1,2,1,905951,0,Arms 7;105950,0,2,2,3,905950,0,Arms 8;"
            + "105949,0,0,3,1,905949,0,Arms 9;105948,0,1,3,3,905948,0,Arms 10;105947,0,2,3,2,905947,0,Arms 11;110524,0,0,4,5,910524,0,Arms 12;"
            + "105945,0,1,4,1,905945,0,Arms 13;105944,0,2,4,5,905944,0,Arms 14;110858,0,0,5,2,910858,0,Arms 15;105942,0,2,5,3,905942,0,Arms 16;"
            + "105941,0,1,6,1,905941,0,Arms 17;105938,1,1,0,5,905938,0,Fury 1;105939,1,2,0,5,905939,0,Fury 2;110857,1,1,1,5,910857,0,Fury 3;"
            + "105937,1,2,1,5,905937,0,Fury 4;105953,1,0,2,3,905953,0,Fury 5;105935,1,1,2,1,905935,0,Fury 6;105934,1,2,2,3,905934,0,Fury 7;"
            + "105933,1,0,3,5,905933,0,Fury 8;105977,1,1,3,1,905977,0,Fury 9;105931,1,2,3,5,905931,0,Fury 10;105932,1,3,3,2,905932,0,Fury 11;"
            + "105978,1,0,4,2,905978,0,Fury 12;105927,1,1,4,1,905927,0,Fury 13;105926,1,3,4,2,905926,0,Fury 14;105928,1,1,5,5,905928,0,Fury 15;"
            + "113569,1,2,5,2,913569,0,Fury 16;105930,1,1,6,1,905930,0,Fury 17;105974,2,0,0,2,905974,0,Protection 1;105976,2,1,0,5,905976,0,Protection 2;"
            + "113570,2,2,0,5,913570,0,Protection 3;105975,2,0,1,5,905975,0,Protection 4;105969,2,2,1,3,905969,0,Protection 5;105972,2,3,1,3,905972,0,Protection 6;"
            + "105970,2,0,2,1,905970,0,Protection 7;105971,2,1,2,2,905971,0,Protection 8;105967,2,2,2,3,905967,0,Protection 9;110856,2,3,2,3,910856,0,Protection 10;"
            + "105968,2,0,3,3,905968,0,Protection 11;105966,2,1,3,1,905966,0,Protection 12;105963,2,2,3,2,905963,0,Protection 13;"
            + "105964,2,0,4,2,905964,0,Protection 14;105965,2,1,4,1,905965,0,Protection 15;105961,2,2,4,3,905961,0,Protection 16;"
            + "105962,2,2,5,5,905962,0,Protection 17;105959,2,1,6,1,905959,0,Protection 18;";
        static readonly int[][] FullHunterEdges = { new[] { 104961, 104964 }, new[] { 104964, 104961 }, new[] { 104967, 104962 }, new[] { 104970, 104964 }, new[] { 104985, 104984 }, new[] { 104986, 104989 }, new[] { 105004, 104997 }, new[] { 105005, 104999 }, new[] { 105008, 105002 } };
        static readonly int[][] FullWarriorEdges = { new[] { 105927, 105928 }, new[] { 105931, 113569 }, new[] { 105945, 105941 }, new[] { 105950, 105947 }, new[] { 105956, 105950 }, new[] { 105954, 105951 }, new[] { 105965, 105959 }, new[] { 105976, 105971 } };
        // Beast Mastery 31 / Marksmanship 20
        static readonly Dictionary<int, int> FullHunterBuild = new Dictionary<int, int>
        {
            [104960] = 5, [104976] = 5, [104974] = 3, [104973] = 2, [104970] = 1, [104969] = 5, [104967] = 5, [104964] = 1, [104965] = 2, [104962] = 1, [104961] = 1,
            [105011] = 5, [105009] = 5, [105008] = 5, [105005] = 2, [105002] = 3,
        };
        // Arms 31 / Fury 20
        static readonly Dictionary<int, int> FullWarriorBuild = new Dictionary<int, int>
        {
            [105957] = 5, [105956] = 3, [105954] = 5, [105951] = 1, [105950] = 3, [105947] = 2, [105948] = 3, [105945] = 1, [105944] = 5, [105942] = 2, [105941] = 1,
            [105938] = 5, [105939] = 5, [110857] = 5, [105935] = 1, [105934] = 3, [105931] = 1,
        };

        static TalentTreeDef FullTree(int id, int classId, string name, string file, string[] specs, int[] icons, string layout, int[][] edges)
        {
            var t = new TalentTreeDef { Id = id, ClassId = classId, ClassName = name, ClassFile = file, SpecNames = specs, SpecIcons = icons, MaxPoints = 51, CostPerRank = 1, CurrencyId = id };
            foreach (var row in layout.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var p = row.Split(',');
                int I(int k) => int.Parse(p[k], CultureInfo.InvariantCulture);
                var n = new TalentNode { Id = I(0), Spec = I(1), Col = I(2), Row = I(3), X = TalentNormalise.Bases[I(1)] + I(2) * TalentNormalise.Step, Y = TalentNormalise.FirstRow + I(3) * TalentNormalise.Step };
                n.Entries.Add(new TalentEntry { Id = I(0), DefinitionId = I(0), MaxRanks = I(4), SpellId = I(5), IconOverride = I(6), NameOverride = p[7] });
                t.Nodes.Add(n);
            }
            foreach (var e in edges) t.Edges.Add(new TalentEdge { From = e[0], To = e[1], Type = TalentEdge.Sufficient });
            // the tier gates: row r of a tree needs 5 x r points in the rows above it (a group of those nodes and an "available" condition on it)
            for (int s = 0; s < 3; s++)
                for (int r = 1; r < 7; r++)
                {
                    int gid = 10000 + s * 100 + r;
                    t.Groups[gid] = t.Nodes.Where(n => n.Spec == s && n.Row < r).Select(n => n.Id).ToArray();
                    foreach (var n in t.Nodes.Where(n => n.Spec == s && n.Row == r)) n.Conds.Add(new TalentCond { Id = gid, Type = 0, GroupId = gid, Spent = 5 * r });
                }
            for (int lv = 10; lv <= 60; lv++) t.Sources.Add(new PointSource { Level = lv, Amount = 1 });
            t.Reindex();
            return t;
        }

        static TalentSet FullTalents()
        {
            var set = new TalentSet();
            set.Trees.Add(FullTree(2, 1, "Warrior", "WARRIOR", new[] { "Arms", "Fury", "Protection" }, new[] { 132355, 132292, 132341 }, FullWarriorLayout, FullWarriorEdges));
            var h = FullTree(1, 3, "Hunter", "HUNTER", new[] { "Beast Mastery", "Marksmanship", "Survival" }, new[] { 132164, 132222, 132215 }, FullHunterLayout, FullHunterEdges);
            h.Aliases[104982] = 110859;            // the hidden copy of Lightning Reflexes, as TalentData leaves it out
            set.Trees.Add(h);
            return set;
        }

        // the data of the Hub's talents module for a character of this class with `build`: every talent of the tree (rank 0 where none), at the game's
        // raw positions; the Hunter's also with the game's odd ones (the copy 104982 at x 102800, Improved Serpent Sting 105003 at y 39300)
        static string FullHubTalents(TalentTreeDef t, Dictionary<int, int> build, long at)
        {
            var sb = new StringBuilder();
            int i = 1, spent = 0;
            string Q(string v) => "\"" + (v ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
            void Node(int id, int r, int m, double x, double y, int s, string n, int ic) => sb.Append("[" + (i++) + "]={id=" + id + ",r=" + r + ",m=" + m + ",x=" + x.ToString(CultureInfo.InvariantCulture) + ",y=" + y.ToString(CultureInfo.InvariantCulture) + ",s=" + s + ",n=" + Q(n) + ",ic=" + ic + "},\n");
            foreach (var n in t.Nodes)
            {
                int r = build.TryGetValue(n.Id, out var v) ? v : 0;
                spent += r;
                double y = n.Id == 105003 ? 39300 : n.Y;
                Node(n.Id, r, n.MaxRanks, n.X, y, n.SpellId, n.Entry.NameOverride, n.Entry.IconOverride);
            }
            if (t.ClassFile == "HUNTER") Node(104982, 0, 5, 102800, 5740, 19168, "Lightning Reflexes", 136047);
            return "at=" + at + ",api='traits',spent=" + spent + ",unspent=0,nodes={\n" + sb + "}";
        }

        // the shared renderer against the game data: one panel per tree, every talent of the tree on its game cell (the model, the laid-out box and the
        // arranged rect agree), the columns filling the grid's width evenly, icons 36-48 px, every arrow starting outside its prerequisite's icon and
        // ending outside (above / beside) the talent's icon
        static bool GridMatches(IReadOnlyList<TalentTreePanel> panels, TalentTreeDef def, out string why)
        {
            why = "";
            if (panels.Count != def.SpecCount) { why = "panels " + panels.Count; return false; }
            foreach (var p in panels)
            {
                var g = p.Grid;
                double cw = g.CellWidth, rh = g.RowHeight, gut = g.Gutter ? TalentGridPanel.GateGutter : 0;
                if (g.ActualWidth <= 0) { why = "not laid out"; return false; }
                if (Math.Abs(gut + g.Cols * cw - g.ActualWidth) > 1) { why = "columns do not fill the width: " + (gut + g.Cols * cw) + " of " + g.ActualWidth; return false; }
                if (g.IconSize < TalentGridPanel.IconMin || g.IconSize > TalentGridPanel.IconMax) { why = "icon " + g.IconSize; return false; }
                var nodes = def.Nodes.Where(n => n.Spec == p.Model.Spec).ToList();
                if (p.Model.Cells.Count != nodes.Count || g.Cols != def.Nodes.Max(n => n.Col) + 1 || g.Rows != def.Nodes.Max(n => n.Row) + 1) { why = "cells " + p.Model.Cells.Count + " grid " + g.Cols + "x" + g.Rows; return false; }
                foreach (var n in nodes)
                {
                    var c = p.Model.Cell(n.Id);
                    if (c == null || c.Col != n.Col || c.Row != n.Row) { why = "model cell of " + n.Id; return false; }
                    var r = g.NodeRect(n.Id);
                    double cx = gut + (n.Col + 0.5) * cw, cy = (n.Row + 0.5) * rh;
                    if (r.IsEmpty || Math.Abs(r.X + r.Width / 2 - cx) > 0.5 || Math.Abs(r.Y + r.Height / 2 - cy) > 0.5) { why = "rect of " + n.Id; return false; }
                    var box = p.Views[n.Id].Box;
                    var pt = box.TranslatePoint(new Point(box.ActualWidth / 2, box.ActualHeight / 2), g);
                    if (Math.Abs(pt.X - cx) > 1 || Math.Abs(pt.Y - cy) > 1 || Math.Abs(box.ActualWidth - g.IconSize) > 0.5) { why = "box of " + n.Id + " at " + pt + " size " + box.ActualWidth; return false; }
                }
                foreach (var a in p.Model.Arrows)
                {
                    var pts = g.ArrowPoints(a);
                    var rf = g.NodeRect(a.From); var rt = g.NodeRect(a.To);
                    if (pts == null || pts.Count < 2) { why = "arrow " + a.From + ">" + a.To + " not drawn"; return false; }
                    Point s = pts[0], e = pts[pts.Count - 1];
                    var tc = new Point(rt.X + rt.Width / 2, rt.Y + rt.Height / 2);
                    bool vertical = def.Node(a.From).Col == def.Node(a.To).Col;
                    if (rf.Contains(s) || rt.Contains(e) || (e - tc).Length > g.IconSize || (vertical && (Math.Abs(s.X - e.X) > 0.01 || s.Y < rf.Bottom || e.Y > rt.Top)))
                    { why = "arrow " + a.From + ">" + a.To + " " + s + " -> " + e; return false; }
                }
            }
            return true;
        }

        // the ranks of a build on the panels: badge "r/m", rank-0 talents dimmed unless the game's rules would let them take a point
        static bool RanksMatch(IReadOnlyList<TalentTreePanel> panels, TalentTreeDef def, Dictionary<int, int> build, out string why)
        {
            why = "";
            var st = new TalentState();
            foreach (var kv in build) st.Set(kv.Key, kv.Value);
            foreach (var p in panels)
                foreach (var c in p.Model.Cells)
                {
                    var n = def.Node(c.Id); var v = p.Views[c.Id];
                    int r = st.Rank(c.Id);
                    double op = r > 0 || TalentRules.Available(def, st, n) ? 1 : 0.45;
                    if (c.Rank != r || v.Rank.Text != r + "/" + n.MaxRanks || Math.Abs(v.Box.Opacity - op) > 0.01) { why = c.Id + ": " + v.Rank.Text + " opacity " + v.Box.Opacity; return false; }
                }
            return true;
        }

        // design pictures only: the icons of the fixture trees from this PC's own Outpost icon cache (read-only), so the pictures show real icons
        static void CopyShotIcons(params TalentTreeDef[] defs)
        {
            try
            {
                var from = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ElansAddonHub", "cache", "icons");
                if (!Directory.Exists(from) || string.Equals(Path.GetFullPath(from), Path.GetFullPath(IconStore.Dir), StringComparison.OrdinalIgnoreCase)) return;
                Directory.CreateDirectory(IconStore.Dir);
                var ids = defs.SelectMany(d => d.Nodes.Select(n => n.Entry.IconOverride).Concat(d.SpecIcons)).Where(i => i > 0).Distinct();
                foreach (var id in ids)
                {
                    var src = Path.Combine(from, id + ".png");
                    if (File.Exists(src) && !File.Exists(IconStore.PathFor(id))) File.Copy(src, IconStore.PathFor(id));
                }
            }
            catch { }
        }
    }
}
