using System;
using System.Collections.Generic;
using System.Linq;

namespace ElansAddonHub.Services.Talents
{
    // Where a layout stands against a build's leveling order (BuildApplication.Steps, the points the tree allowed, in order):
    //   * Followed: the build's points the layout already has (a step counts once the layout holds that talent at that rank or more);
    //   * Prefix: how many of the build's points, from the first one on, are in place without a gap - the layout is "on track" when it holds exactly those
    //     and nothing else (then stepping back and forth along the order is safe);
    //   * Next: the first point of the order the layout does not have yet (null = the build is complete);
    //   * Off: ranks the build never spends (a talent it does not take, or more ranks than it takes) - a character that went its own way.
    public sealed class GuideProgress
    {
        public int Followed, Prefix, Total;
        public BuildStep Next;
        public List<BuildStep> Upcoming = new List<BuildStep>();             // the order's points still missing, Next first
        public List<KeyValuePair<int, int>> Off = new List<KeyValuePair<int, int>>();   // node -> ranks over the build
        public int OffPoints => Off.Sum(o => o.Value);
        public bool Complete => Next == null;
        public bool OnTrack => Off.Count == 0 && Prefix == Followed;
    }

    public static class BuildGuide
    {
        public static List<BuildStep> Applied(BuildApplication app) => app?.Steps.Where(s => s.Applied).ToList() ?? new List<BuildStep>();

        public static GuideProgress Progress(BuildApplication app, TalentState have)
        {
            var g = new GuideProgress();
            have = have ?? new TalentState();
            var steps = Applied(app);
            g.Total = steps.Count;
            bool gap = false;
            foreach (var s in steps)
            {
                if (have.Rank(s.NodeId) >= s.Rank) { g.Followed++; if (!gap) g.Prefix++; }
                else { gap = true; g.Upcoming.Add(s); }
            }
            g.Next = g.Upcoming.FirstOrDefault();
            foreach (var kv in have.Ranks.OrderBy(k => k.Key))
            {
                int over = kv.Value - (app?.State.Rank(kv.Key) ?? 0);
                if (over > 0) g.Off.Add(new KeyValuePair<int, int>(kv.Key, over));
            }
            return g;
        }

        // the layout after the build's first `points` points
        public static TalentState StateAt(BuildApplication app, int points)
        {
            var st = new TalentState();
            foreach (var s in Applied(app).Take(Math.Max(0, points))) st.Set(s.NodeId, s.Rank);
            return st;
        }

        // how many of the build's points have arrived by this level
        public static int PointsBy(BuildApplication app, int level) => Applied(app).Count(s => s.Level > 0 && s.Level <= level);

        // the level that holds the build's first `points` points (the level of the last of them); 0 points = the level the first point comes at
        public static int LevelFor(BuildApplication app, int points)
        {
            var steps = Applied(app);
            if (steps.Count == 0) return 0;
            if (points <= 0) return steps[0].Level;
            return steps[Math.Min(points, steps.Count) - 1].Level;
        }

        // the talents the order still has to take, with the level of their next point: the tree marks them with it
        public static Dictionary<int, int> NextLevels(GuideProgress g)
        {
            var d = new Dictionary<int, int>();
            foreach (var s in g.Upcoming) if (!d.ContainsKey(s.NodeId)) d[s.NodeId] = s.Level;
            return d;
        }
    }

    // a run of consecutive points of one talent in the order: one line "levels 10-14: Alpha 1-5/5"; First / Last are 1-based positions among the
    // points the tree allowed (BuildGuide.Applied)
    public sealed class StepGroup
    {
        public string Text;
        public int First, Last, NodeId;
    }
}
