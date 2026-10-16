using System;
using System.Collections.Generic;
using System.Linq;
using ElansAddonHub.Services.Atlas.Classic;
using ElansAddonHub.Services.Db2;

namespace ElansAddonHub.Services.Atlas
{
    // Quest chains for the quest page (2.77): where a quest sits in its chain ("step 3 of 7"), what has to be done before it, what it opens, breadcrumbs,
    // and exclusive groups. Pure: a table of links in, a chain out, so the self-test runs it on fixture data.
    //
    // The links (cMaNGOS quest_template semantics, the Classic baseline):
    //   Prev > 0          that quest must be done before this one;  Prev < 0  that quest must be in the quest log
    //   Next > 0          doing this one opens that one             Next < 0  (only while this one is in the log)
    //   NextInChain       the quest the ender offers right after this one (Forever's own answer, WdbQuest.NextQuest, wins over the baseline's)
    //   Group > 0         only one quest of the group can be done;  Group < 0 all of the group must be done before their Next opens
    //   BreadcrumbFor     this quest only leads you to that one (optional)
    // A chain: walk back from the quest over its single predecessor and forward over its single successor; a quest seen twice stops the walk (a cycle in
    // the data never hangs the page), so does MaxSteps. Forever's own quest lines (QuestLine.db2 via WorldLookups) give the chain and its name when the
    // quest is in one.
    public sealed class QuestLink
    {
        public int Id; public string Title = "";
        public int Prev, Next, NextInChain, Group, BreadcrumbFor;
        public bool FromForever;                 // NextInChain came from the player's own game
    }

    public sealed class QuestChainStep
    {
        public int Id, Step; public string Title = ""; public bool Current;
        public string Link => "quest:" + Id;
        public override string ToString() => Step + ". " + Title + (Current ? " *" : "");
    }

    public sealed class QuestChainInfo
    {
        public int QuestId;
        public string Name = "";                                     // Forever's quest line name, "" when the chain has none
        public bool FromLine;                                        // the steps are Forever's quest line (else worked out from the links)
        public bool AnyForever, AnyClassic;                          // which layers the links came from
        public List<QuestChainStep> Steps = new List<QuestChainStep>();
        public int Position => Steps.FindIndex(s => s.Current) + 1;  // 1-based, 0 = the quest is not on the line
        public int Total => Steps.Count;
        public bool Cut;                                             // a cycle or the step limit stopped a walk
        public List<int> Requires = new List<int>();                 // must be done first (Prev > 0)
        public List<int> RequiresActive = new List<int>();           // must be in the quest log (Prev < 0)
        public List<int> OpenedBy = new List<int>();                 // quests whose Next is this one
        public bool OpenedByAll;                                     // ... and all of them are needed (their group is < 0)
        public List<int> ExclusiveWith = new List<int>();            // Group > 0: the others of the group (only one can be done)
        public List<int> AllOfGroup = new List<int>();               // Group < 0: the others that must be done too
        public List<int> Unlocks = new List<int>();                  // follow-ups it opens (other than the chain's next step)
        public List<int> WhileActive = new List<int>();              // quests that can be taken while this one is in the log (their Prev = -this)
        public int BreadcrumbFor;                                    // this quest leads to that one
        public List<int> Breadcrumbs = new List<int>();              // quests that lead here
        public bool HasChain => Steps.Count > 1;
        public bool Any => HasChain || Requires.Count + RequiresActive.Count + OpenedBy.Count + ExclusiveWith.Count + AllOfGroup.Count + Unlocks.Count + WhileActive.Count + Breadcrumbs.Count > 0 || BreadcrumbFor > 0;
        public string StepText => Position > 0 && Total > 1 ? "Step " + Position + " of " + Total : "";
    }

    public static class QuestChains
    {
        public const int MaxSteps = 40;

        public sealed class Table
        {
            public readonly Dictionary<int, QuestLink> Q = new Dictionary<int, QuestLink>();
            internal readonly Dictionary<int, List<int>> ByNextInChain = new Dictionary<int, List<int>>(), ByPrev = new Dictionary<int, List<int>>(), ByPrevActive = new Dictionary<int, List<int>>(),
                ByNext = new Dictionary<int, List<int>>(), ByGroup = new Dictionary<int, List<int>>(), ByBreadcrumb = new Dictionary<int, List<int>>();
            public QuestLink Get(int id) => Q.TryGetValue(id, out var q) ? q : null;
        }

        static void Push(Dictionary<int, List<int>> d, int k, int v) { if (!d.TryGetValue(k, out var l)) d[k] = l = new List<int>(); l.Add(v); }
        static List<int> Of(Dictionary<int, List<int>> d, int k) => d.TryGetValue(k, out var l) ? l : new List<int>();

        public static Table Build(IEnumerable<QuestLink> links)
        {
            var t = new Table();
            foreach (var q in links ?? Enumerable.Empty<QuestLink>()) if (q != null && q.Id > 0) t.Q[q.Id] = q;
            foreach (var q in t.Q.Values.OrderBy(x => x.Id))
            {
                if (q.NextInChain > 0 && q.NextInChain != q.Id) Push(t.ByNextInChain, q.NextInChain, q.Id);
                if (q.Prev > 0 && q.Prev != q.Id) Push(t.ByPrev, q.Prev, q.Id);
                if (q.Prev < 0 && -q.Prev != q.Id) Push(t.ByPrevActive, -q.Prev, q.Id);
                if (q.Next > 0 && q.Next != q.Id) Push(t.ByNext, q.Next, q.Id);
                if (q.Group != 0) Push(t.ByGroup, q.Group, q.Id);
                if (q.BreadcrumbFor > 0 && q.BreadcrumbFor != q.Id) Push(t.ByBreadcrumb, q.BreadcrumbFor, q.Id);
            }
            return t;
        }

        // the one quest that comes right before `id` (0 = none or not one): the quest whose NextInChain is this one, else this one's Prev, else the quest whose Next opens it
        public static int Before(Table t, int id)
        {
            var q = t.Get(id);
            var nic = Of(t.ByNextInChain, id);
            if (nic.Count > 0) return q != null && q.Prev > 0 && nic.Contains(q.Prev) ? q.Prev : nic.Min();
            if (q != null && q.Prev > 0) return q.Prev;
            var nx = Of(t.ByNext, id).Where(o => t.Get(o)?.Group >= 0).ToList();      // an "all of" group is a fork in, not a step
            return nx.Count == 1 ? nx[0] : 0;
        }

        // the one quest that comes right after `id` (0 = none, or a fork): NextInChain, else Next, else the single quest whose Prev is this one
        public static int After(Table t, int id)
        {
            var q = t.Get(id);
            if (q != null && q.NextInChain > 0 && q.NextInChain != id) return q.NextInChain;
            if (q != null && q.Next > 0 && q.Next != id && q.Group >= 0) return q.Next;
            var pv = Of(t.ByPrev, id);
            return pv.Count == 1 ? pv[0] : 0;
        }

        // the chain and the needs of one quest. `line` = Forever's quest line holding it (null = none); `title` names a quest the table does not have.
        public static QuestChainInfo For(Table t, int id, QuestLineInfo line = null, Func<int, string> title = null)
        {
            var info = new QuestChainInfo { QuestId = id };
            string Title(int qid) { var s = t.Get(qid)?.Title; if (!string.IsNullOrEmpty(s)) return s; return title?.Invoke(qid) ?? "Quest " + qid; }
            var q = t.Get(id);
            if (line != null && line.Quests != null && Array.IndexOf(line.Quests, id) >= 0 && line.Quests.Length > 1)
            {
                info.FromLine = true; info.AnyForever = true; info.Name = line.Name ?? "";
                var seenIds = new HashSet<int>();
                foreach (var qid in line.Quests.Take(MaxSteps))
                    if (seenIds.Add(qid)) info.Steps.Add(new QuestChainStep { Id = qid, Title = Title(qid), Current = qid == id });
                if (line.Quests.Length > MaxSteps) info.Cut = true;
            }
            else
            {
                var visited = new HashSet<int> { id };
                var back = new List<int>();
                int cur = id;
                while (true)
                {
                    int b = Before(t, cur);
                    if (b <= 0) break;
                    if (!visited.Add(b) || back.Count >= MaxSteps) { info.Cut = true; break; }
                    back.Add(b); cur = b;
                }
                var fwd = new List<int>();
                cur = id;
                while (true)
                {
                    int a = After(t, cur);
                    if (a <= 0) break;
                    if (!visited.Add(a) || fwd.Count >= MaxSteps) { info.Cut = true; break; }
                    fwd.Add(a); cur = a;
                }
                back.Reverse();
                foreach (var qid in back.Concat(new[] { id }).Concat(fwd))
                {
                    info.Steps.Add(new QuestChainStep { Id = qid, Title = Title(qid), Current = qid == id });
                    var l = t.Get(qid);
                    if (l != null) { if (l.FromForever) info.AnyForever = true; else info.AnyClassic = true; }
                }
                if (info.Steps.Count < 2) info.Steps.Clear();
            }
            for (int i = 0; i < info.Steps.Count; i++) info.Steps[i].Step = i + 1;

            if (q != null)
            {
                if (q.Prev > 0) info.Requires.Add(q.Prev);
                if (q.Prev < 0) info.RequiresActive.Add(-q.Prev);
                var opened = Of(t.ByNext, id).Where(o => o != q.Prev).ToList();
                info.OpenedBy.AddRange(opened);
                info.OpenedByAll = opened.Count > 1 && opened.All(o => t.Get(o)?.Group < 0);
                if (q.Group > 0) info.ExclusiveWith.AddRange(Of(t.ByGroup, q.Group).Where(o => o != id));
                if (q.Group < 0) info.AllOfGroup.AddRange(Of(t.ByGroup, q.Group).Where(o => o != id));
                if (q.BreadcrumbFor > 0) info.BreadcrumbFor = q.BreadcrumbFor;
            }
            var chainNext = info.Steps.FindIndex(s => s.Current) is int ci && ci >= 0 && ci + 1 < info.Steps.Count ? info.Steps[ci + 1].Id : 0;
            var unlocks = new List<int>();
            if (q != null && q.NextInChain > 0) unlocks.Add(q.NextInChain);
            if (q != null && q.Next > 0) unlocks.Add(q.Next);
            unlocks.AddRange(Of(t.ByPrev, id));
            info.Unlocks.AddRange(unlocks.Where(u => u != id && u != chainNext).Distinct());
            info.WhileActive.AddRange(Of(t.ByPrevActive, id));
            info.Breadcrumbs.AddRange(Of(t.ByBreadcrumb, id));
            return info;
        }

        // ---- the live table: the Classic baseline's quest_template links, with Forever's own "next quest" answers (the client's cache) laid over them
        static readonly object gate = new object();
        static Table cached; static object cachedClassic, cachedSeen;

        public static Table Live()
        {
            var ci = ClassicAtlas.Current; var snap = SeenData.Snapshot;
            lock (gate)
            {
                if (cached != null && ReferenceEquals(cachedClassic, ci) && ReferenceEquals(cachedSeen, snap)) return cached;
                var links = new Dictionary<int, QuestLink>();
                if (ci != null)
                    foreach (var c in ci.Quests.Values)
                        links[c.Id] = new QuestLink { Id = c.Id, Title = c.Title ?? "", Prev = c.PrevQuestId, Next = c.NextQuestId, NextInChain = c.NextQuestInChain, Group = c.ExclusiveGroup, BreadcrumbFor = c.BreadcrumbFor };
                if (snap != null)
                    foreach (var s in snap.Quests.Values)
                    {
                        if (!links.TryGetValue(s.Id, out var l)) links[s.Id] = l = new QuestLink { Id = s.Id };
                        if (!string.IsNullOrEmpty(s.Title)) l.Title = s.Title;
                        if (s.Full && s.NextQuest > 0) { l.NextInChain = s.NextQuest; l.FromForever = true; }
                    }
                cached = Build(links.Values); cachedClassic = ci; cachedSeen = snap;
                return cached;
            }
        }

        public static QuestChainInfo LiveFor(int id) => For(Live(), id, SpellData.World?.LineOf(id), AtlasQuery.QuestTitle);

        // "Honored with Timbermaw Hold" from reputation points (the standing boundaries of the game)
        public static string Standing(int points)
        {
            if (points >= 42000) return "Exalted";
            if (points >= 21000) return "Revered";
            if (points >= 9000) return "Honored";
            if (points >= 3000) return "Friendly";
            if (points >= 0) return "Neutral";
            if (points >= -3000) return "Unfriendly";
            if (points >= -6000) return "Hostile";
            return "Hated";
        }
    }
}
