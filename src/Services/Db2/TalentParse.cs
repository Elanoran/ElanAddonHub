using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using ElansAddonHub.Services.Talents;

namespace ElansAddonHub.Services.Db2
{
    public sealed class TalentRaw
    {
        public Dictionary<string, byte[]> Tables = new Dictionary<string, byte[]>();
        public byte[] Get(SpellTables.Def d) => Tables.TryGetValue(d.Name, out var b) ? b : null;
    }

    // ============================================================================================================================
    // the Trait* tables -> trees per class
    // ============================================================================================================================
    public static class TalentParse
    {
        sealed class Tab { public Wdc5 T; public SpellTables.Def Def; public bool Ok => T != null; }

        static Tab Open(TalentRaw raw, SpellTables.Def def, TalentSet set, bool required)
        {
            var o = SpellTables.Open(raw.Get(def), def);
            if (o.Layout != 0) set.LayoutsSeen[def.Name] = o.Layout;
            if (!o.Ok)
            {
                set.Problems[required ? "all" : def.Name] = o.Problem;
                if (required) return new Tab();
                Util.Log("talent data: " + def.Name + ": " + o.Problem);
                return new Tab();
            }
            return new Tab { T = o.Table, Def = def };
        }

        static int I(Wdc5 t, int row, int f) => SpellTables.Int(t, row, f);

        public static TalentSet All(TalentRaw raw)
        {
            var set = new TalentSet();
            // required tables: the first one that is missing or has an unknown layout makes the whole layer unavailable, with its reason
            var req = new Dictionary<string, Tab>();
            foreach (var d in TalentTables.Required)
            {
                var tab = Open(raw, d, set, true);
                if (!tab.Ok) { if (!set.Problems.ContainsKey("all")) set.Problems["all"] = "talent tables: " + d.Name; return set; }
                req[d.Name] = tab;
            }
            var opt = new Dictionary<string, Tab>();
            foreach (var d in TalentTables.Optional) opt[d.Name] = Open(raw, d, set, false);
            Tab O(SpellTables.Def d) => opt[d.Name];

            // ---- classes, skill lines
            var classes = new Dictionary<int, string[]>();
            var cls = req["ChrClasses"].T;
            for (int i = 0; i < cls.Count; i++) classes[cls.IdAt(i)] = new[] { cls.String(i, TalentTables.ClassName), cls.String(i, TalentTables.ClassFile) };
            var sl = req["SkillLine"].T;
            var skillName = new Dictionary<int, string>(); var skillCat = new Dictionary<int, int>(); var skillIcon = new Dictionary<int, int>();
            for (int i = 0; i < sl.Count; i++) { skillName[sl.IdAt(i)] = sl.String(i, 0); skillCat[sl.IdAt(i)] = I(sl, i, 6); skillIcon[sl.IdAt(i)] = sl.FieldCount > 7 ? I(sl, i, 7) : 0; }
            var sla = req["SkillLineAbility"].T;
            var skillMask = new Dictionary<int, long>();                  // skill -> OR of the class masks of its abilities
            var spellSkills = new Dictionary<int, List<int>>();            // spell -> skill lines
            for (int i = 0; i < sla.Count; i++)
            {
                int skill = I(sla, i, 3), spell = I(sla, i, 4); long mask = sla.Field(i, 6);
                if (mask != 0) skillMask[skill] = (skillMask.TryGetValue(skill, out var m) ? m : 0) | mask;
                if (!spellSkills.TryGetValue(spell, out var l)) spellSkills[spell] = l = new List<int>(2);
                if (!l.Contains(skill)) l.Add(skill);
            }
            var treeSkills = new Dictionary<int, List<int>>();             // tree -> skill lines
            var st = req["SkillLineXTraitTree"].T;
            for (int i = 0; i < st.Count; i++)
            {
                int tree = I(st, i, 1), skill = I(st, i, 0);
                if (!treeSkills.TryGetValue(tree, out var l)) treeSkills[tree] = l = new List<int>();
                l.Add(skill);
            }

            // ---- definitions, entries, nodes
            var dt = req["TraitDefinition"].T;
            var defs = new Dictionary<int, int>(dt.Count);
            for (int i = 0; i < dt.Count; i++) defs[dt.IdAt(i)] = i;
            var et = req["TraitNodeEntry"].T;
            var entries = new Dictionary<int, int>(et.Count);
            for (int i = 0; i < et.Count; i++) entries[et.IdAt(i)] = i;
            var lk = req["TraitNodeXTraitNodeEntry"].T;
            var nodeEntries = new Dictionary<int, List<KeyValuePair<int, int>>>();     // node -> (index, entry)
            for (int i = 0; i < lk.Count; i++)
            {
                int n = I(lk, i, 1);
                if (!nodeEntries.TryGetValue(n, out var l)) nodeEntries[n] = l = new List<KeyValuePair<int, int>>();
                l.Add(new KeyValuePair<int, int>(I(lk, i, 3), I(lk, i, 2)));
            }

            // rank curves per definition
            var curveOf = new Dictionary<int, List<KeyValuePair<int, int>>>();         // definition -> (effect index, curve)
            var eff = O(TalentTables.EffectPoints);
            var curvePts = new Dictionary<int, SortedDictionary<int, float>>();
            if (eff.Ok)
            {
                for (int i = 0; i < eff.T.Count; i++)
                {
                    int def = I(eff.T, i, 1), idx = I(eff.T, i, 2), op = I(eff.T, i, 3), curve = I(eff.T, i, 4);
                    if (op != 0 || curve == 0) continue;                  // 0 = the value comes from the curve (the only operation in this build's data)
                    if (!curveOf.TryGetValue(def, out var l)) curveOf[def] = l = new List<KeyValuePair<int, int>>();
                    l.Add(new KeyValuePair<int, int>(idx, curve));
                }
                var cp = O(TalentTables.CurvePoint);
                if (cp.Ok)
                {
                    var want = new HashSet<int>(curveOf.Values.SelectMany(v => v.Select(k => k.Value)));
                    for (int i = 0; i < cp.T.Count; i++)
                    {
                        int curve = I(cp.T, i, 3);
                        if (curve == 0) curve = cp.T.Foreign(i);
                        if (!want.Contains(curve)) continue;
                        float x = SpellTables.FltArr(cp.T, i, 0, 0, 2), y = SpellTables.FltArr(cp.T, i, 0, 1, 2);
                        if (x < 1 || x > 20 || Math.Abs(x - Math.Round(x)) > 0.001) continue;
                        if (!curvePts.TryGetValue(curve, out var m)) curvePts[curve] = m = new SortedDictionary<int, float>();
                        m[(int)Math.Round(x)] = y;
                    }
                }
            }

            var nt = req["TraitNode"].T;
            var treesWanted = new HashSet<int>(treeSkills.Keys);
            var nodesOfTree = new Dictionary<int, List<int>>();                        // tree -> node rows
            for (int i = 0; i < nt.Count; i++)
            {
                int tree = I(nt, i, 1);
                if (!treesWanted.Contains(tree)) continue;
                if (!nodesOfTree.TryGetValue(tree, out var l)) nodesOfTree[tree] = l = new List<int>();
                l.Add(i);
            }

            // ---- edges, groups, conditions
            var edgeT = req["TraitEdge"].T;
            var edgesByNode = new Dictionary<int, List<TalentEdge>>();                 // by the node on either end
            for (int i = 0; i < edgeT.Count; i++)
            {
                var e = new TalentEdge { From = I(edgeT, i, 2), To = I(edgeT, i, 3), Type = I(edgeT, i, 4) };
                foreach (var key in new[] { e.From, e.To })
                {
                    if (!edgesByNode.TryGetValue(key, out var l)) edgesByNode[key] = l = new List<TalentEdge>();
                    l.Add(e);
                }
            }
            var grp = req["TraitNodeGroup"].T;
            var groupTree = new Dictionary<int, int>();
            for (int i = 0; i < grp.Count; i++) groupTree[grp.IdAt(i)] = I(grp, i, 1);
            var gn = req["TraitNodeGroupXTraitNode"].T;
            var groupNodes = new Dictionary<int, List<KeyValuePair<int, int>>>();      // group -> (index, node)
            var nodeGroups = new Dictionary<int, List<int>>();
            for (int i = 0; i < gn.Count; i++)
            {
                int g = I(gn, i, 1), n = I(gn, i, 2);
                if (!groupNodes.TryGetValue(g, out var l)) groupNodes[g] = l = new List<KeyValuePair<int, int>>();
                l.Add(new KeyValuePair<int, int>(I(gn, i, 3), n));
                if (!nodeGroups.TryGetValue(n, out var l2)) nodeGroups[n] = l2 = new List<int>();
                l2.Add(g);
            }
            var cd = req["TraitCond"].T;
            var conds = new Dictionary<int, TalentCond>();
            for (int i = 0; i < cd.Count; i++)
                conds[cd.IdAt(i)] = new TalentCond { Id = cd.IdAt(i), Type = I(cd, i, 1), GroupId = I(cd, i, 7), NodeId = I(cd, i, 8), Spent = I(cd, i, 11), Flags = I(cd, i, 12) };
            var gc = req["TraitNodeGroupXTraitCond"].T;
            var groupConds = new Dictionary<int, List<int>>();
            for (int i = 0; i < gc.Count; i++)
            {
                int g = I(gc, i, 2);
                if (!groupConds.TryGetValue(g, out var l)) groupConds[g] = l = new List<int>();
                l.Add(I(gc, i, 1));
            }
            var nodeConds = new Dictionary<int, List<int>>();
            var nc = O(TalentTables.NodeCond);
            if (nc.Ok)
                for (int i = 0; i < nc.T.Count; i++)
                {
                    int n = I(nc.T, i, 2);
                    if (!nodeConds.TryGetValue(n, out var l)) nodeConds[n] = l = new List<int>();
                    l.Add(I(nc.T, i, 1));
                }

            // ---- currencies and point sources
            var treeCurrency = new Dictionary<int, int>();
            var tc = O(TalentTables.TreeCurrency);
            if (tc.Ok) for (int i = 0; i < tc.T.Count; i++) treeCurrency[I(tc.T, i, 2)] = I(tc.T, i, 3);
            var costAmount = new Dictionary<int, int>();
            var cost = O(TalentTables.Cost);
            if (cost.Ok) for (int i = 0; i < cost.T.Count; i++) costAmount[cost.T.IdAt(i)] = I(cost.T, i, 2);
            var treeCost = new Dictionary<int, int>();
            var tcost = O(TalentTables.TreeCost);
            if (tcost.Ok) for (int i = 0; i < tcost.T.Count; i++) treeCost[I(tcost.T, i, 1)] = I(tcost.T, i, 2);
            var curMax = new Dictionary<int, int>();
            var cur = O(TalentTables.Currency);
            if (cur.Ok) for (int i = 0; i < cur.T.Count; i++) curMax[cur.T.IdAt(i)] = I(cur.T, i, 7);
            var sourcesOf = new Dictionary<int, List<PointSource>>();
            var csrc = O(TalentTables.CurrencySource);
            if (csrc.Ok)
            {
                var bound = new Dictionary<int, List<PointSource>>();
                for (int i = 0; i < csrc.T.Count; i++)
                {
                    int c = I(csrc.T, i, 2), amount = I(csrc.T, i, 3), quest = I(csrc.T, i, 4), ach = I(csrc.T, i, 5), level = I(csrc.T, i, 6), entry = I(csrc.T, i, 7);
                    if (quest != 0 || ach != 0 || level <= 0 || amount <= 0 || amount > 10) continue;     // quest / achievement sources are not level based
                    var ps = new PointSource { Level = level, Amount = amount };
                    var dict = entry != 0 ? bound : sourcesOf;
                    if (!dict.TryGetValue(c, out var l)) dict[c] = l = new List<PointSource>();
                    l.Add(ps);
                }
                // the sources that need a node entry are "Talented Rank n (Level 10-n)" of the Legacy talent: rank n = the n-th earliest level
                foreach (var kv in bound)
                {
                    int rank = 0;
                    foreach (var ps in kv.Value.OrderByDescending(p => p.Level)) { ps.EntryRank = ++rank; }
                    if (!sourcesOf.TryGetValue(kv.Key, out var l)) sourcesOf[kv.Key] = l = new List<PointSource>();
                    l.AddRange(kv.Value);
                }
            }

            // ---- one tree per class
            foreach (var kv in nodesOfTree.OrderBy(k => k.Key))
            {
                int treeId = kv.Key;
                var tree = new TalentTreeDef { Id = treeId };
                var spellsOf = new Dictionary<int, int>();                              // node -> spell id (first entry)
                var repairedNode = new HashSet<int>();
                foreach (int row in kv.Value)
                {
                    var node = new TalentNode { Id = nt.IdAt(row), Type = I(nt, row, 4) };
                    TalentNormalise.Position(nt.Field(row, 2), nt.Field(row, 3), out node.X, out node.Y, out node.Spec, out node.Col, out node.Row, out bool repaired);
                    if (repaired) repairedNode.Add(node.Id);
                    if (nodeEntries.TryGetValue(node.Id, out var ents))
                        foreach (var ek in ents.OrderBy(k => k.Key))
                        {
                            if (!entries.TryGetValue(ek.Value, out int er)) continue;
                            var entry = new TalentEntry { Id = ek.Value, DefinitionId = I(et, er, 1), MaxRanks = Math.Max(1, I(et, er, 2)) };
                            if (defs.TryGetValue(entry.DefinitionId, out int dr))
                            {
                                entry.SpellId = I(dt, dr, 4); entry.IconOverride = I(dt, dr, 5);
                                entry.NameOverride = dt.String(dr, 0) ?? ""; entry.DescriptionOverride = dt.String(dr, 2) ?? "";
                                if (curveOf.TryGetValue(entry.DefinitionId, out var cl))
                                    foreach (var ck in cl)
                                        if (curvePts.TryGetValue(ck.Value, out var pts) && pts.Count > 0)
                                        {
                                            var by = new float[entry.MaxRanks];
                                            for (int r = 1; r <= entry.MaxRanks; r++) by[r - 1] = pts.TryGetValue(r, out var y) ? y : (pts.Keys.Where(x => x <= r).Select(x => pts[x]).DefaultIfEmpty(0f).Last());
                                            entry.Curves.Add(new EffectCurve { EffectIndex = ck.Key, ByRank = by });
                                        }
                            }
                            node.Entries.Add(entry);
                        }
                    if (node.Entries.Count == 0) { tree.Notes.Add("node " + node.Id + " has no talent and is left out"); continue; }
                    tree.Nodes.Add(node);
                    spellsOf[node.Id] = node.SpellId;
                }

                // duplicates: the same talent (spell) twice in one tree. The node whose position needed no repair stays (HH keeps "the learned one";
                // here nothing is learned, so the clean one), else the lower id.
                foreach (var g in tree.Nodes.Where(n => n.SpellId > 0).GroupBy(n => n.SpellId).Where(g => g.Count() > 1).ToList())
                {
                    var keep = g.OrderBy(n => repairedNode.Contains(n.Id) ? 1 : 0).ThenBy(n => n.Id).First();
                    foreach (var n in g.Where(n => n != keep)) { tree.Aliases[n.Id] = keep.Id; tree.Nodes.Remove(n); tree.Notes.Add("node " + n.Id + " duplicates node " + keep.Id + " (spell " + g.Key + ") and is left out"); }
                }
                foreach (var id in repairedNode) if (tree.Node(id) != null) tree.Notes.Add("node " + id + ": position repaired");
                tree.Reindex();

                // edges between the tree's nodes (ends moved onto the node that stays), no self edges, no doubles
                var seen = new HashSet<long>();
                foreach (var n in tree.Nodes)
                    if (edgesByNode.TryGetValue(n.Id, out var el))
                        foreach (var e in el)
                        {
                            int f = tree.Resolve(e.From), t = tree.Resolve(e.To);
                            if (f == t || tree.Node(f) == null || tree.Node(t) == null) continue;
                            if (!seen.Add(((long)f << 34) ^ ((long)t << 4) ^ (long)e.Type)) continue;
                            tree.Edges.Add(new TalentEdge { From = f, To = t, Type = e.Type });
                        }
                // also edges from aliases that point at a node of this tree
                foreach (var alias in tree.Aliases.Keys.ToList())
                    if (edgesByNode.TryGetValue(alias, out var el))
                        foreach (var e in el)
                        {
                            int f = tree.Resolve(e.From), t = tree.Resolve(e.To);
                            if (f == t || tree.Node(f) == null || tree.Node(t) == null) continue;
                            if (!seen.Add(((long)f << 34) ^ ((long)t << 4) ^ (long)e.Type)) continue;
                            tree.Edges.Add(new TalentEdge { From = f, To = t, Type = e.Type });
                        }

                // groups (only the nodes that stay) and the conditions of each node
                var needGroups = new HashSet<int>();
                foreach (var n in tree.Nodes)
                {
                    var cl = new List<int>();
                    if (nodeGroups.TryGetValue(n.Id, out var gl)) foreach (var g in gl) if (groupConds.TryGetValue(g, out var gcl)) cl.AddRange(gcl);
                    foreach (var a in tree.Aliases.Where(a => a.Value == n.Id).Select(a => a.Key))
                        if (nodeGroups.TryGetValue(a, out var gl2)) foreach (var g in gl2) if (groupConds.TryGetValue(g, out var gcl)) cl.AddRange(gcl);
                    if (nodeConds.TryGetValue(n.Id, out var ncl)) cl.AddRange(ncl);
                    foreach (var cid in cl.Distinct())
                        if (conds.TryGetValue(cid, out var c))
                        {
                            var cc = new TalentCond { Id = c.Id, Type = c.Type, GroupId = c.GroupId, NodeId = tree.Resolve(c.NodeId), Spent = c.Spent, Flags = c.Flags };
                            n.Conds.Add(cc);
                            if (c.GroupId != 0) needGroups.Add(c.GroupId);
                        }
                }
                foreach (var g in needGroups)
                    if (groupNodes.TryGetValue(g, out var gl))
                        tree.Groups[g] = gl.OrderBy(k => k.Key).Select(k => tree.Resolve(k.Value)).Where(id => tree.Node(id) != null).Distinct().ToArray();

                // currency, cost per rank, point sources
                if (treeCurrency.TryGetValue(treeId, out int curId))
                {
                    tree.CurrencyId = curId;
                    if (sourcesOf.TryGetValue(curId, out var srcs)) tree.Sources = srcs.OrderBy(p => p.Level).Select(p => new PointSource { Level = p.Level, Amount = p.Amount, EntryRank = p.EntryRank }).ToList();
                    if (curMax.TryGetValue(curId, out int mx) && mx > 0) tree.MaxPoints = mx;
                }
                if (treeCost.TryGetValue(treeId, out int costId) && costAmount.TryGetValue(costId, out int amt) && amt > 0) tree.CostPerRank = amt;

                // class: the single class bit of the tree's skill lines, else of its talents' skill lines
                long mask = 0;
                foreach (var sk in treeSkills[treeId]) if (skillMask.TryGetValue(sk, out var m)) mask |= m;
                if (mask == 0 || (mask & (mask - 1)) != 0)
                {
                    var votes = new Dictionary<long, int>();
                    foreach (var n in tree.Nodes)
                        if (spellSkills.TryGetValue(n.SpellId, out var sks))
                            foreach (var sk in sks) if (skillMask.TryGetValue(sk, out var m) && m != 0 && (m & (m - 1)) == 0) votes[m] = (votes.TryGetValue(m, out var v) ? v : 0) + 1;
                    mask = votes.Count == 0 ? 0 : votes.OrderByDescending(v => v.Value).First().Key;
                }
                int classId = 0;
                if (mask != 0 && (mask & (mask - 1)) == 0) { classId = 1; while ((mask >>= 1) != 0) classId++; }
                if (classId == 0 || !classes.TryGetValue(classId, out var cn)) { set.Notes.Add("tree " + treeId + " could not be matched to a class and is left out"); continue; }
                tree.ClassId = classId; tree.ClassName = cn[0]; tree.ClassFile = (cn[1] ?? "").ToUpperInvariant();

                // names of the three trees side by side: the class skill line most of a band's talents belong to
                int specs = tree.Nodes.Count == 0 ? 0 : tree.Nodes.Max(n => n.Spec) + 1;
                var names = new string[specs];
                var icons = new int[specs];
                var usedSkill = new HashSet<int>();
                for (int s = 0; s < specs; s++)
                {
                    var votes = new Dictionary<int, int>();
                    foreach (var n in tree.Nodes.Where(n => n.Spec == s))
                        if (spellSkills.TryGetValue(n.SpellId, out var sks))
                            foreach (var sk in sks) if (skillCat.TryGetValue(sk, out int cat) && cat == 7 && !(skillName.TryGetValue(sk, out var nm) && nm.StartsWith("Pet", StringComparison.Ordinal))) votes[sk] = (votes.TryGetValue(sk, out var v) ? v : 0) + 1;
                    var best = votes.OrderByDescending(v => v.Value).ThenBy(v => v.Key).Select(v => v.Key).FirstOrDefault(k => !usedSkill.Contains(k));
                    if (best != 0) { usedSkill.Add(best); names[s] = skillName[best]; icons[s] = skillIcon.TryGetValue(best, out var ic) && ic > 0 ? ic : 0; }
                    else names[s] = "Tree " + (s + 1);
                }
                tree.SpecNames = names;
                tree.SpecIcons = icons;
                tree.Nodes = tree.Nodes.OrderBy(n => n.Spec).ThenBy(n => n.Row).ThenBy(n => n.Col).ThenBy(n => n.Id).ToList();
                tree.Reindex();
                set.Trees.Add(tree);
            }
            set.Trees = set.Trees.OrderBy(t => t.ClassId).ToList();
            return set;
        }
    }

    // ============================================================================================================================
    // the compact copy of the parsed trees in the Outpost's data folder (gzip of a small binary format), keyed by the game build
    // ============================================================================================================================
    public static class TalentIndexFile
    {
        public sealed class Saved { public int Format; public string Build; public TalentSet Set; }
        public const int FormatVersion = 2;       // bump when the parsing, the table layouts or the cache file change meaning
        const int Magic = 0x4C544554;       // "TETL"

        public static bool TryWrite(string path, string build, TalentSet set)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var tmp = path + ".tmp";
                var mem = new MemoryStream(1 << 18);
                using (var w = new BinaryWriter(mem, Encoding.UTF8, true))
                {
                    w.Write(Magic); w.Write(TalentIndexFile.FormatVersion); w.Write(build ?? "");
                    w.Write(set.Problems.Count); foreach (var kv in set.Problems) { w.Write(kv.Key); w.Write(kv.Value ?? ""); }
                    w.Write(set.Notes.Count); foreach (var n in set.Notes) w.Write(n);
                    w.Write(set.LayoutsSeen.Count); foreach (var kv in set.LayoutsSeen) { w.Write(kv.Key); w.Write(kv.Value); }
                    w.Write(set.Trees.Count);
                    foreach (var t in set.Trees) WriteTree(w, t);
                }
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var gz = new GZipStream(fs, CompressionMode.Compress))
                    gz.Write(mem.GetBuffer(), 0, (int)mem.Length);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                return true;
            }
            catch (Exception e) { Util.Log("talent data: cannot save the index: " + e.Message); return false; }
        }

        public static Saved TryRead(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var mem = new MemoryStream(1 << 18);
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var gz = new GZipStream(fs, CompressionMode.Decompress))
                    gz.CopyTo(mem);
                mem.Position = 0;
                using (var r = new BinaryReader(mem, Encoding.UTF8))
                {
                    if (r.ReadInt32() != Magic) return null;
                    var s = new Saved { Format = r.ReadInt32(), Build = r.ReadString(), Set = new TalentSet() };
                    if (s.Format != TalentIndexFile.FormatVersion) return null;
                    int n = r.ReadInt32(); Count(n, 1000);
                    for (int i = 0; i < n; i++) s.Set.Problems[r.ReadString()] = r.ReadString();
                    n = r.ReadInt32(); Count(n, 100000);
                    for (int i = 0; i < n; i++) s.Set.Notes.Add(r.ReadString());
                    n = r.ReadInt32(); Count(n, 1000);
                    for (int i = 0; i < n; i++) s.Set.LayoutsSeen[r.ReadString()] = r.ReadUInt32();
                    n = r.ReadInt32(); Count(n, 64);
                    for (int i = 0; i < n; i++) s.Set.Trees.Add(ReadTree(r));
                    return s;
                }
            }
            catch (Exception) { return null; }
        }

        static void Count(int n, int max) { if (n < 0 || n > max) throw new InvalidDataException("count"); }

        static void WriteTree(BinaryWriter w, TalentTreeDef t)
        {
            w.Write(t.Id); w.Write(t.ClassId); w.Write(t.CurrencyId); w.Write(t.CostPerRank); w.Write(t.MaxPoints); w.Write(t.ClassName); w.Write(t.ClassFile); w.Write(t.ClassIconId);
            w.Write(t.SpecNames.Length); foreach (var s in t.SpecNames) w.Write(s ?? "");
            w.Write(t.SpecIcons.Length); foreach (var i in t.SpecIcons) w.Write(i);
            w.Write(t.Notes.Count); foreach (var s in t.Notes) w.Write(s);
            w.Write(t.Aliases.Count); foreach (var kv in t.Aliases) { w.Write(kv.Key); w.Write(kv.Value); }
            w.Write(t.Sources.Count); foreach (var s in t.Sources) { w.Write(s.Level); w.Write(s.Amount); w.Write(s.EntryRank); }
            w.Write(t.Edges.Count); foreach (var e in t.Edges) { w.Write(e.From); w.Write(e.To); w.Write(e.Type); }
            w.Write(t.Groups.Count); foreach (var kv in t.Groups) { w.Write(kv.Key); w.Write(kv.Value.Length); foreach (var id in kv.Value) w.Write(id); }
            w.Write(t.Nodes.Count);
            foreach (var n in t.Nodes)
            {
                w.Write(n.Id); w.Write(n.Spec); w.Write(n.Col); w.Write(n.Row); w.Write(n.X); w.Write(n.Y); w.Write(n.Type);
                w.Write(n.Conds.Count); foreach (var c in n.Conds) { w.Write(c.Id); w.Write(c.Type); w.Write(c.GroupId); w.Write(c.NodeId); w.Write(c.Spent); w.Write(c.Flags); }
                w.Write(n.Entries.Count);
                foreach (var e in n.Entries)
                {
                    w.Write(e.Id); w.Write(e.DefinitionId); w.Write(e.SpellId); w.Write(e.MaxRanks); w.Write(e.IconOverride); w.Write(e.NameOverride ?? ""); w.Write(e.DescriptionOverride ?? "");
                    w.Write(e.Curves.Count);
                    foreach (var c in e.Curves) { w.Write(c.EffectIndex); w.Write(c.ByRank.Length); foreach (var v in c.ByRank) w.Write(v); }
                }
            }
        }

        static TalentTreeDef ReadTree(BinaryReader r)
        {
            var t = new TalentTreeDef { Id = r.ReadInt32(), ClassId = r.ReadInt32(), CurrencyId = r.ReadInt32(), CostPerRank = r.ReadInt32(), MaxPoints = r.ReadInt32(), ClassName = r.ReadString(), ClassFile = r.ReadString(), ClassIconId = r.ReadInt32() };
            int n = r.ReadInt32(); Count(n, 16); t.SpecNames = new string[n]; for (int i = 0; i < n; i++) t.SpecNames[i] = r.ReadString();
            n = r.ReadInt32(); Count(n, 16); t.SpecIcons = new int[n]; for (int i = 0; i < n; i++) t.SpecIcons[i] = r.ReadInt32();
            n = r.ReadInt32(); Count(n, 10000); for (int i = 0; i < n; i++) t.Notes.Add(r.ReadString());
            n = r.ReadInt32(); Count(n, 10000); for (int i = 0; i < n; i++) t.Aliases[r.ReadInt32()] = r.ReadInt32();
            n = r.ReadInt32(); Count(n, 10000); for (int i = 0; i < n; i++) t.Sources.Add(new PointSource { Level = r.ReadInt32(), Amount = r.ReadInt32(), EntryRank = r.ReadInt32() });
            n = r.ReadInt32(); Count(n, 100000); for (int i = 0; i < n; i++) t.Edges.Add(new TalentEdge { From = r.ReadInt32(), To = r.ReadInt32(), Type = r.ReadInt32() });
            n = r.ReadInt32(); Count(n, 100000);
            for (int i = 0; i < n; i++) { int g = r.ReadInt32(); int c = r.ReadInt32(); Count(c, 10000); var ids = new int[c]; for (int k = 0; k < c; k++) ids[k] = r.ReadInt32(); t.Groups[g] = ids; }
            n = r.ReadInt32(); Count(n, 10000);
            for (int i = 0; i < n; i++)
            {
                var node = new TalentNode { Id = r.ReadInt32(), Spec = r.ReadInt32(), Col = r.ReadInt32(), Row = r.ReadInt32(), X = r.ReadDouble(), Y = r.ReadDouble(), Type = r.ReadInt32() };
                int c = r.ReadInt32(); Count(c, 1000);
                for (int k = 0; k < c; k++) node.Conds.Add(new TalentCond { Id = r.ReadInt32(), Type = r.ReadInt32(), GroupId = r.ReadInt32(), NodeId = r.ReadInt32(), Spent = r.ReadInt32(), Flags = r.ReadInt32() });
                c = r.ReadInt32(); Count(c, 100);
                for (int k = 0; k < c; k++)
                {
                    var e = new TalentEntry { Id = r.ReadInt32(), DefinitionId = r.ReadInt32(), SpellId = r.ReadInt32(), MaxRanks = r.ReadInt32(), IconOverride = r.ReadInt32(), NameOverride = r.ReadString(), DescriptionOverride = r.ReadString() };
                    int cc = r.ReadInt32(); Count(cc, 100);
                    for (int j = 0; j < cc; j++) { var cv = new EffectCurve { EffectIndex = r.ReadInt32() }; int len = r.ReadInt32(); Count(len, 100); cv.ByRank = new float[len]; for (int q = 0; q < len; q++) cv.ByRank[q] = r.ReadSingle(); e.Curves.Add(cv); }
                    node.Entries.Add(e);
                }
                t.Nodes.Add(node);
            }
            t.Reindex();
            return t;
        }
    }
}
