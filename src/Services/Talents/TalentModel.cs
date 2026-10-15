using System;
using System.Collections.Generic;
using System.Linq;

namespace ElansAddonHub.Services.Talents
{
    // The talent layer's model (Atlas layer G). Plain data, no game or UI types: TalentData fills it from the client tables, the self-test fills it from
    // synthetic ones, TalentRules / BuildCode / the page only read it.

    // one rank-scaled effect of a talent: rank n of the talent has `ByRank[n-1]` as the base amount of the spell effect `EffectIndex` (0-based)
    public sealed class EffectCurve
    {
        public int EffectIndex;
        public float[] ByRank = new float[0];
    }

    // what one node can show: the trait definition behind it (spell, optional overrides)
    public sealed class TalentEntry
    {
        public int Id, DefinitionId, SpellId, MaxRanks = 1, IconOverride;
        public string NameOverride = "", DescriptionOverride = "";
        public List<EffectCurve> Curves = new List<EffectCurve>();
    }

    // TraitCond (type Available): a requirement of a node (through its group, its own conditions, or an edge-like node requirement)
    public sealed class TalentCond
    {
        public int Id, Type, GroupId, NodeId, Spent, Flags;
    }

    public sealed class TalentEdge
    {
        public int From, To, Type;          // TraitEdge: 0 visual only, 2 sufficient for availability, 3 required, 4 mutually exclusive
        public const int Visual = 0, Sufficient = 2, Required = 3, Exclusive = 4;
    }

    public sealed class TalentNode
    {
        public int Id, Spec, Col, Row;                 // Spec 0..2 (the tree of the three side by side), Col / Row 0-based within it
        public double X, Y;                            // the normalised position (see TalentNormalise)
        public int Type;                               // 0 single, 1 tiered, 2 selection (only the first entry is used by the calculator)
        public List<TalentEntry> Entries = new List<TalentEntry>();
        public List<TalentCond> Conds = new List<TalentCond>();
        public int MaxRanks => Entries.Count == 0 ? 0 : Entries[0].MaxRanks;
        public TalentEntry Entry => Entries.Count == 0 ? null : Entries[0];
        public int SpellId => Entry?.SpellId ?? 0;
    }

    // a point source of the currency: `Amount` points at player level `Level`; EntryRank > 0 = only once the legacy "Talented" talent has that many ranks
    public sealed class PointSource
    {
        public int Level, Amount = 1, EntryRank;
    }

    public sealed class TalentTreeDef
    {
        public int Id, ClassId, CurrencyId, CostPerRank = 1, MaxPoints;
        public string ClassName = "", ClassFile = "";
        public int ClassIconId;
        public string[] SpecNames = new string[0];
        public int[] SpecIcons = new int[0];                                     // the icon of each tree's class skill line (SkillLine.SpellIconFileID), 0 = none
        public List<TalentNode> Nodes = new List<TalentNode>();
        public List<TalentEdge> Edges = new List<TalentEdge>();
        public Dictionary<int, int[]> Groups = new Dictionary<int, int[]>();      // group id -> node ids
        public List<PointSource> Sources = new List<PointSource>();
        public Dictionary<int, int> Aliases = new Dictionary<int, int>();         // a dropped duplicate node id -> the node that stays
        public List<string> Notes = new List<string>();                           // what was fixed up while reading (bogus positions, duplicates)
        public int SpecCount => Math.Max(1, Nodes.Count == 0 ? 1 : Nodes.Max(n => n.Spec) + 1);

        Dictionary<int, TalentNode> byId;
        Dictionary<int, List<TalentEdge>> incoming;
        public TalentNode Node(int id)
        {
            if (byId == null || byId.Count != Nodes.Count) byId = Nodes.ToDictionary(n => n.Id);
            return byId.TryGetValue(id, out var n) ? n : null;
        }
        public int Resolve(int id) => Aliases.TryGetValue(id, out var k) ? k : id;
        public List<TalentEdge> Incoming(int id)
        {
            if (incoming == null) incoming = Edges.GroupBy(e => e.To).ToDictionary(g => g.Key, g => g.ToList());
            return incoming.TryGetValue(id, out var l) ? l : NoEdges;
        }
        static readonly List<TalentEdge> NoEdges = new List<TalentEdge>();
        public void Reindex() { byId = null; incoming = null; }
    }

    // everything read from the game: trees by class
    public sealed class TalentSet
    {
        public List<TalentTreeDef> Trees = new List<TalentTreeDef>();
        public Dictionary<string, string> Problems = new Dictionary<string, string>();
        public List<string> Notes = new List<string>();
        public Dictionary<string, uint> LayoutsSeen = new Dictionary<string, uint>();
        public TalentTreeDef ForClass(int classId) => Trees.FirstOrDefault(t => t.ClassId == classId);
        public TalentTreeDef ForClassFile(string file) => string.IsNullOrEmpty(file) ? null : Trees.FirstOrDefault(t => string.Equals(t.ClassFile, file, StringComparison.OrdinalIgnoreCase));
    }

    // ============================================================================================================================
    // The positions in this client's data are not all clean: two nodes carry an extra digit (102800, 39300, 21300), others are a few units off the grid
    // (5740 for 5730, x 9280 for 9080). Hunter Helper's rule is "divide by 10 while above 20000"; this does that and then snaps to the 600-unit grid of
    // the side-by-side trees when it is within 150 units of it. Duplicates of one talent (same spell twice in a tree, e.g. Lightning Reflexes) keep the
    // node whose position needed no repair.
    // ============================================================================================================================
    public static class TalentNormalise
    {
        public const double Step = 600, Snap = 150;
        public static readonly double[] Bases = { 1020, 5020, 9080 };      // x of the first column of each of the three trees
        public const double FirstRow = 2130;

        public static double Divide(double v) { int guard = 0; while (v > 20000 && guard++ < 4) v /= 10; return v; }

        public static void Position(double rawX, double rawY, out double x, out double y, out int spec, out int col, out int row, out bool repaired)
        {
            x = Divide(rawX); y = Divide(rawY);
            repaired = x != rawX || y != rawY;
            spec = x < 4000 ? 0 : x < 8000 ? 1 : 2;
            double bx = Bases[spec];
            double c = Math.Round((x - bx) / Step), r = Math.Round((y - FirstRow) / Step);
            if (Math.Abs(bx + c * Step - x) <= Snap) { if (bx + c * Step != x) repaired = true; x = bx + c * Step; }
            if (Math.Abs(FirstRow + r * Step - y) <= Snap) { if (FirstRow + r * Step != y) repaired = true; y = FirstRow + r * Step; }
            col = (int)Math.Max(0, Math.Round((x - bx) / Step));
            row = (int)Math.Max(0, Math.Round((y - FirstRow) / Step));
        }
    }
}
