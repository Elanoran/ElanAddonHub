using System;
using System.Collections.Generic;
using System.Linq;

namespace ElansAddonHub.Services.Talents
{
    // The ranks someone has put into one tree (node id -> rank, only ranks > 0)
    public sealed class TalentState
    {
        public Dictionary<int, int> Ranks = new Dictionary<int, int>();
        public int Rank(int node) => Ranks.TryGetValue(node, out var r) ? r : 0;
        public TalentState Clone() => new TalentState { Ranks = new Dictionary<int, int>(Ranks) };
        public void Set(int node, int rank) { if (rank > 0) Ranks[node] = rank; else Ranks.Remove(node); }
        public int Total => Ranks.Values.Sum();
        public bool SameAs(TalentState o) => o != null && Ranks.Count == o.Ranks.Count && Ranks.All(kv => o.Rank(kv.Key) == kv.Value);
    }

    public enum Why { Ok, NoNode, Maxed, NoPoints, Blocked, NoRank, WouldBreak }

    // The game's rules for a talent tree, worked out from the tree's own data (conditions, groups and edges) - no per-class code:
    //  * a node can be bought when it is AVAILABLE: every condition of it is met (a group condition = "at least N points spent in that group of nodes",
    //    which is how the 5-points-per-row gates of the classic trees are written: the group is the rows above), and its edges allow it (every
    //    "required" edge's source node is full; when it has "sufficient" edges, at least one source is full; an "exclusive" edge blocks it while the other
    //    node holds a point);
    //  * a rank can be given back when the tree stays valid afterwards - every node that still has a point is still available. That is what stops a
    //    refund that would strand a node below it or drop a row under its 5-point gate.
    // A node's own points never count towards its own condition (the game asks for the points spent BEFORE it).
    // HOOK: EdgeNeedsFull - the classic rule is that a prerequisite talent must be maxed (the arrows in the trees); set false to ask for one rank only.
    public static class TalentRules
    {
        public static bool EdgeNeedsFull = true;

        static bool EdgeOk(TalentTreeDef t, TalentState s, TalentEdge e)
        {
            var from = t.Node(e.From);
            if (from == null) return true;                  // an edge to a node that is not there cannot hold anything back
            int r = s.Rank(e.From);
            return EdgeNeedsFull ? r >= from.MaxRanks && from.MaxRanks > 0 : r > 0;
        }

        public static int GroupSpent(TalentTreeDef t, TalentState s, int group, int exceptNode)
        {
            if (!t.Groups.TryGetValue(group, out var ids)) return 0;
            int sum = 0;
            foreach (var id in ids) if (id != exceptNode) sum += s.Rank(id);
            return sum * t.CostPerRank;
        }

        public static int Spent(TalentTreeDef t, TalentState s) => s.Total * t.CostPerRank;

        static bool CondOk(TalentTreeDef t, TalentState s, TalentNode n, TalentCond c)
        {
            if (c.Type != 0) return true;                    // only "available" conditions gate buying
            if (c.GroupId != 0 && c.Spent > 0) return GroupSpent(t, s, c.GroupId, n.Id) >= c.Spent;
            if (c.NodeId != 0 && c.NodeId != n.Id) { var other = t.Node(t.Resolve(c.NodeId)); return other == null || s.Rank(other.Id) >= Math.Max(1, other.MaxRanks); }
            if (c.Spent > 0) return (Spent(t, s) - s.Rank(n.Id) * t.CostPerRank) >= c.Spent;
            return true;
        }

        // the node can take its next rank as far as conditions and edges go (points and the rank cap are not looked at here)
        public static bool Available(TalentTreeDef t, TalentState s, TalentNode n)
        {
            foreach (var c in n.Conds) if (!CondOk(t, s, n, c)) return false;
            var inc = t.Incoming(n.Id);
            bool anyRequired = false, anySufficient = false, sufficientMet = false;
            foreach (var e in inc)
            {
                if (e.Type == TalentEdge.Required) { anyRequired = true; if (!EdgeOk(t, s, e)) return false; }
                else if (e.Type == TalentEdge.Sufficient) { anySufficient = true; if (EdgeOk(t, s, e)) sufficientMet = true; }
                else if (e.Type == TalentEdge.Exclusive && s.Rank(e.From) > 0 && s.Rank(n.Id) == 0) return false;
            }
            _ = anyRequired;
            if (anySufficient && !sufficientMet) return false;
            return true;
        }

        public static Why CanAdd(TalentTreeDef t, TalentState s, int nodeId, int pointsLeft)
        {
            var n = t.Node(nodeId);
            if (n == null) return Why.NoNode;
            if (s.Rank(nodeId) >= n.MaxRanks) return Why.Maxed;
            if (pointsLeft < t.CostPerRank) return Why.NoPoints;
            return Available(t, s, n) ? Why.Ok : Why.Blocked;
        }

        // every node holding a point is available (its own points left out of its own condition)
        public static bool Valid(TalentTreeDef t, TalentState s, out int badNode)
        {
            badNode = 0;
            foreach (var kv in s.Ranks)
            {
                var n = t.Node(kv.Key);
                if (n == null || kv.Value > n.MaxRanks) { badNode = kv.Key; return false; }
                if (!Available(t, s, n)) { badNode = kv.Key; return false; }
            }
            return true;
        }

        public static Why CanRemove(TalentTreeDef t, TalentState s, int nodeId)
        {
            var n = t.Node(nodeId);
            if (n == null) return Why.NoNode;
            if (s.Rank(nodeId) <= 0) return Why.NoRank;
            var after = s.Clone(); after.Set(nodeId, s.Rank(nodeId) - 1);
            return Valid(t, after, out _) ? Why.Ok : Why.WouldBreak;
        }

        public static Why Add(TalentTreeDef t, TalentState s, int nodeId, int pointsLeft)
        {
            var w = CanAdd(t, s, nodeId, pointsLeft);
            if (w == Why.Ok) s.Set(nodeId, s.Rank(nodeId) + 1);
            return w;
        }

        public static Why Remove(TalentTreeDef t, TalentState s, int nodeId)
        {
            var w = CanRemove(t, s, nodeId);
            if (w == Why.Ok) s.Set(nodeId, s.Rank(nodeId) - 1);
            return w;
        }

        public static string Explain(Why w) =>
            w == Why.Maxed ? "Already at its highest rank" : w == Why.NoPoints ? "No points left" : w == Why.Blocked ? "Not unlocked yet" :
            w == Why.WouldBreak ? "Other talents depend on this point" : w == Why.NoRank ? "No points in this talent" : w == Why.NoNode ? "Not a talent of this tree" : "";

        // ---- gates, for the page: the tier gate a node sits behind ("5 points in the rows above"), or null
        public static int GateNeeded(TalentTreeDef t, TalentNode n)
        {
            int need = 0;
            foreach (var c in n.Conds) if (c.Type == 0 && c.GroupId != 0 && c.Spent > need) need = c.Spent;
            return need;
        }

        // points the node's gate still wants ("needs 3 more points above"); 0 = open
        public static int GateMissing(TalentTreeDef t, TalentState s, TalentNode n)
        {
            int miss = 0;
            foreach (var c in n.Conds)
                if (c.Type == 0 && c.GroupId != 0 && c.Spent > 0) miss = Math.Max(miss, c.Spent - GroupSpent(t, s, c.GroupId, n.Id));
            return Math.Max(0, miss);
        }

        // ---- points and levels
        // HOOK (clearly named): the standard rule, one point per level from level 10 (51 points at 60), used when the client's currency sources cannot be read.
        public static List<PointSource> StandardSources()
        {
            var l = new List<PointSource>();
            for (int lv = 10; lv <= 60; lv++) l.Add(new PointSource { Level = lv });
            return l;
        }

        // the levels at which points arrive, ascending; `legacyRanks` = ranks of the Forever Legacy talent "Talented" (each rank moves one point earlier: the
        // data lists sources "Talented Rank n (Level 10-n)"); capped at the currency's maximum
        public static List<int> PointLevels(TalentTreeDef t, int legacyRanks)
        {
            var src = t.Sources != null && t.Sources.Count > 0 ? t.Sources : StandardSources();
            var list = new List<int>();
            foreach (var s in src)
            {
                if (s.EntryRank > legacyRanks) continue;
                for (int i = 0; i < Math.Max(1, s.Amount); i++) list.Add(Math.Max(1, s.Level));
            }
            list.Sort();
            int cap = t.MaxPoints > 0 ? t.MaxPoints : list.Count;
            if (list.Count > cap) list.RemoveRange(cap, list.Count - cap);
            return list;
        }

        public static int PointsAtLevel(TalentTreeDef t, int level, int legacyRanks) => PointLevels(t, legacyRanks).Count(l => l <= level);

        // the level at which the n-th point arrives (n >= 1); 0 when n is 0, int.MaxValue when the currency never gives that many
        public static int LevelForPoints(TalentTreeDef t, int n, int legacyRanks)
        {
            if (n <= 0) return 0;
            var l = PointLevels(t, legacyRanks);
            return n <= l.Count ? l[n - 1] : int.MaxValue;
        }

        public static int MaxLegacyRanks(TalentTreeDef t) => t.Sources == null || t.Sources.Count == 0 ? 0 : t.Sources.Max(s => s.EntryRank);

        // the spent points of each of the side-by-side trees
        public static int[] SpentPerSpec(TalentTreeDef t, TalentState s)
        {
            var r = new int[t.SpecCount];
            foreach (var kv in s.Ranks) { var n = t.Node(kv.Key); if (n != null && n.Spec < r.Length) r[n.Spec] += kv.Value; }
            return r;
        }
    }

    // the undo history of one calculator session
    public sealed class TalentSession
    {
        public TalentTreeDef Tree;
        public TalentState State = new TalentState();
        public int Level = 60, LegacyRanks;
        readonly Stack<TalentState> undo = new Stack<TalentState>();

        public TalentSession(TalentTreeDef tree) { Tree = tree; Level = 60; }

        public int Budget => TalentRules.PointsAtLevel(Tree, Level, LegacyRanks);
        public int Spent => TalentRules.Spent(Tree, State);
        public int Left => Math.Max(0, Budget - Spent);
        public int RequiredLevel => TalentRules.LevelForPoints(Tree, State.Total, LegacyRanks);
        public bool CanUndo => undo.Count > 0;

        public Why Add(int node)
        {
            var before = State.Clone();
            var w = TalentRules.Add(Tree, State, node, Left);
            if (w == Why.Ok) undo.Push(before);
            return w;
        }

        public Why Remove(int node)
        {
            var before = State.Clone();
            var w = TalentRules.Remove(Tree, State, node);
            if (w == Why.Ok) undo.Push(before);
            return w;
        }

        public void Reset() { if (State.Ranks.Count > 0) undo.Push(State.Clone()); State = new TalentState(); }
        public bool Undo() { if (undo.Count == 0) return false; State = undo.Pop(); return true; }

        // replace the whole state (a build, a character, an import): undoable
        public void Load(TalentState s) { undo.Push(State.Clone()); State = s.Clone(); }
        public void ClearHistory() => undo.Clear();
    }
}
