using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ElansAddonHub.Services.Talents
{
    // "Load from my character": the real talents of a character (the Hub's `talents` module: node id, rank, spell id, name) laid onto the tree of its class.
    public static class CharacterTalents
    {
        public sealed class Loaded
        {
            public TalentState State = new TalentState();
            public int Placed, Unplaced;          // ranks put on a node / ranks of nodes this tree does not have
            public bool Valid;                    // the layout follows the rules (a character in the middle of a respec may not)
            public int Level, LegacyRanks;
            public int Points => Placed;
        }

        // a node is found by its id (a duplicate that was left out counts as the node that stays), else by spell id, else by name
        public static Loaded Load(TalentTreeDef tree, HubTalentsModule mod, int level, Func<TalentNode, string> nameOf)
        {
            var res = new Loaded { Level = level };
            if (tree == null || mod == null) return res;
            Dictionary<string, TalentNode> byName = null;
            foreach (var hn in mod.Nodes)
            {
                if (hn.R <= 0) continue;
                var node = tree.Node(tree.Resolve(hn.Id));
                if (node == null && hn.S > 0) node = tree.Nodes.FirstOrDefault(n => n.SpellId == hn.S);
                if (node == null && !string.IsNullOrEmpty(hn.N))
                {
                    if (byName == null) byName = BuildsRecommended.NameIndex(tree, nameOf ?? (n => null));
                    byName.TryGetValue(BuildsRecommended.Key(hn.N), out node);
                }
                if (node == null) { res.Unplaced += hn.R; continue; }
                int rank = Math.Min(hn.R, node.MaxRanks);
                res.State.Set(node.Id, Math.Max(res.State.Rank(node.Id), rank));
                res.Placed += rank; res.Unplaced += hn.R - rank;
            }
            res.Placed = res.State.Total;
            res.Valid = TalentRules.Valid(tree, res.State, out _);
            res.LegacyRanks = InferLegacyRanks(tree, level, Math.Max(mod.Spent + mod.Unspent, res.State.Total));
            return res;
        }

        // The Legacy talent "Talented" moves points earlier (rank n: one point at level 10-n). A character's own numbers tell how many ranks it has: the
        // smallest count whose points at its level are enough for what it has spent and has left to spend (Hunter Helper's TalentLevelOffset, from data).
        public static int InferLegacyRanks(TalentTreeDef tree, int level, int totalPoints)
        {
            if (level <= 0 || totalPoints <= 0) return 0;
            int max = TalentRules.MaxLegacyRanks(tree);
            for (int r = 0; r <= max; r++) if (TalentRules.PointsAtLevel(tree, level, r) >= totalPoints) return r;
            return max;
        }
    }

    // Hand-off to the game. The Outpost never writes SavedVariables; the one file it may write is the Hub's SharedData.lua, and that file has ONE writer
    // (FlightShare.WriteShared writes the whole file from the flight data). Adding a second key means changing that writer, so it is NOT wired here: this
    // builds the validated Lua text of the key, ready for the day the writer takes it:
    //   ElansHubShared = { flights = { ... }, talentBuild = { class = "HUNTER", name = "Pack Leader", order = { { "Deadly Aspects", 5 }, ... } } }
    // Same safety rules as the flight text: only whitelisted characters, everything escaped, sizes capped, numbers formatted from ints.
    public static class TalentHandoff
    {
        public const int MaxSteps = 120, MaxName = 60;

        static string Safe(string s, int max)
        {
            var sb = new StringBuilder();
            foreach (var c in s ?? "")
            {
                if (c < ' ' || c == '\u007f' || c == (char)0x2028 || c == (char)0x2029) continue;
                if (c == '\\') sb.Append("\\\\"); else if (c == '"') sb.Append("\\\""); else sb.Append(c);
                if (sb.Length >= max) break;
            }
            return sb.ToString().Trim();
        }

        // the `talentBuild = { ... }` text (one table, trailing comma), or null when there is nothing sensible to hand over
        public static string Fragment(string classFile, string name, IEnumerable<KeyValuePair<string, int>> order)
        {
            var cf = new string((classFile ?? "").Where(char.IsLetter).Take(20).ToArray()).ToUpperInvariant();
            if (cf.Length == 0 || order == null) return null;
            var steps = order.Where(o => !string.IsNullOrWhiteSpace(o.Key) && o.Value >= 1 && o.Value <= 5).Take(MaxSteps).ToList();
            if (steps.Count == 0) return null;
            var sb = new StringBuilder();
            sb.Append("talentBuild = { class = \"").Append(cf).Append("\", name = \"").Append(Safe(name, MaxName)).Append("\", order = {");
            foreach (var s in steps) sb.Append(" { \"").Append(Safe(s.Key, MaxName)).Append("\", ").Append(s.Value.ToString(CultureInfo.InvariantCulture)).Append(" },");
            sb.Append(" } },");
            return sb.ToString();
        }
    }
}
