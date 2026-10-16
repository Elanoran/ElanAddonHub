using System;
using System.Collections.Generic;
using System.Linq;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services.Db2;

namespace ElansAddonHub.Services.Atlas
{
    // Faction pages: where the user's own characters stand with the faction (the reputations Elan's Hub 1.16.0 saves) and where the Lodge's members stand
    // (the Armory, feature "reputations": only characters whose member chose to share their reputations). Each section only appears when its layer can say
    // something: no Hub data yet = a line on how to get it; an older Lodge server, a guest or offline = no Lodge section at all.
    public static partial class AtlasQuery
    {
        public const string StandingMineKey = "standing-mine", StandingLodgeKey = "standing-lodge";

        static int ClassIcon(string classFile)
        {
            if (string.IsNullOrEmpty(classFile)) return 0;
            try { return ClassData.Data?.ClassByFile(classFile)?.IconId ?? 0; } catch (Exception) { return 0; }
        }

        static string Cap(string s) => string.IsNullOrEmpty(s) ? "" : char.ToUpperInvariant(s[0]) + s.Substring(1).ToLowerInvariant();

        // "Your characters": every own character whose Hub data has a reputation with the faction, best standing first
        static void AddOwnStandings(AtlasPageModel m, int faction)
        {
            var chars = Characters();
            int withData = 0;
            bool partial = false;
            var rows = new List<(HubRep r, InvChar c)>();
            foreach (var c in chars)
            {
                if (c.Hub == null) continue;
                var reps = HubModules.RepsOf(c.Hub);
                if (reps == null) continue;
                withData++;
                if (reps.Partial) partial = true;
                if (reps.ById.TryGetValue(faction, out var r)) rows.Add((r, c));
            }
            if (chars.Count == 0) return;                 // nobody to talk about: no section
            var s = Section(m, StandingMineKey, "Your characters");
            s.Note = "Your standing as Elan's Hub last saw it in your reputation list."
                + (partial ? " A collapsed header in the game's reputation window hides its factions, so some may be older." : "");
            s.Empty = withData == 0
                ? "Elan's Hub 1.16.0 saves your reputations: log in on a character (or /reload) and its standing shows here."
                : "None of your characters has a reputation with this faction yet.";
            foreach (var (r, c) in rows.OrderByDescending(x => x.r.Standing).ThenByDescending(x => x.r.Value).ThenBy(x => x.c.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var cls = c.Core?.Class ?? Cap(c.Class);
                s.Rows.Add(new AtlasRow
                {
                    Key = "standing:" + c.Key, Title = c.Name, Detail = Reputation.Line(r.Standing, r.Value, r.Min, r.Max),
                    Extra = string.Join(" · ", new[] { c.Level > 0 ? "Level " + c.Level : null, string.IsNullOrEmpty(cls) ? null : cls, c.Realm }.Where(x => !string.IsNullOrEmpty(x))),
                    Progress = Reputation.Progress(r.Value, r.Min, r.Max), IconId = ClassIcon(c.Core?.ClassFile ?? c.Class), Label = AtlasLabel.Mine, Type = "Character",
                    Tier = 0, Sort = r.Standing * 1e9 + r.Value,
                });
            }
        }

        // "Your lodge": the other members' shared characters (the user's own are in the section above)
        static void AddLodgeStandings(AtlasPageModel m, int faction)
        {
            var ask = Context.Standings;
            if (ask == null) return;
            LodgeStandings r;
            try { r = ask(faction); } catch (Exception) { return; }
            if (r == null || !r.Available) return;                       // an older server, reputations off, a guest or offline: no section
            var s = Section(m, StandingLodgeKey, "Your lodge");
            s.Note = "Members who chose to share their characters' reputations with the Lodge (Settings > Lodge > Armory). Members who don't share are not listed.";
            var rows = r.Rows.Where(x => !x.Mine).ToList();
            s.Empty = r.Pending ? "Asking the Lodge..." : "No other member has shared a reputation with this faction yet.";
            foreach (var x in rows.OrderByDescending(x => x.Standing).ThenByDescending(x => x.Value).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).Take(60))
                s.Rows.Add(new AtlasRow
                {
                    Key = "standing:" + x.Owner + "|" + x.Key, Title = x.Name + " (" + x.By + ")", Detail = Reputation.Line(x.Standing, x.Value, x.Min, x.Max),
                    Extra = string.Join(" · ", new[] { x.Level > 0 ? "Level " + x.Level : null, string.IsNullOrEmpty(x.ClassName) ? null : x.ClassName }.Where(t => !string.IsNullOrEmpty(t))),
                    Progress = Reputation.Progress(x.Value, x.Min, x.Max), IconId = ClassIcon(x.Class), Type = "Character", Label = AtlasLabel.Forever, Tier = 0, Sort = x.Standing * 1e9 + x.Value,
                });
        }

        // a group page lists its factions: the best own standing with each ("Revered (Aria)"), "" when no character has one
        static string BestOwnStanding(int faction)
        {
            HubRep best = null; string who = null;
            foreach (var c in Characters())
            {
                var reps = c.Hub == null ? null : HubModules.RepsOf(c.Hub);
                if (reps == null || !reps.ById.TryGetValue(faction, out var r)) continue;
                if (best == null || r.Standing > best.Standing || (r.Standing == best.Standing && r.Value > best.Value)) { best = r; who = c.Name; }
            }
            return best == null ? "" : Reputation.StandingName(best.Standing) + " (" + who + ")";
        }
    }
}
