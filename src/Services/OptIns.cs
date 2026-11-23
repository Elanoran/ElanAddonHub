using System;
using System.Collections.Generic;
using System.Linq;

namespace ElansAddonHub.Services
{
    // 3.11: "New things you can turn on" - features that are off until the user turns them on. Someone who installed before a feature
    // existed (or before this card existed) hears about it once, in one calm card after the Outpost starts: every switch starts off, one
    // "Done" closes it, and nothing is asked again. A new opt-in feature only needs an entry here.
    public class OptIn
    {
        public string Id, Title, Text;
        public bool NeedsLodge;           // only for a Lodge member (not a guest); kept for later while not in one
        public bool InGuide;              // the welcome guide already asks (its sharing choices): a fresh install never sees it here
        public Func<Settings, bool> IsOn;
    }

    public static class OptIns
    {
        public static readonly List<OptIn> All = new List<OptIn>
        {
            new OptIn { Id = "autoupdate", Title = "Update addons automatically",
                Text = "Our addons update by themselves when a new version is out, so you never play with an old one. Downloads come from GitHub, like the Update button.",
                IsOn = s => s.AutoUpdate },
            new OptIn { Id = "armory", Title = "Share my characters with the Lodge", NeedsLodge = true, InGuide = true,
                Text = "Friends see your highest character's gear, talents and professions on the Characters page, and can find who crafts what. Never bags, bank or gold. Pick more characters in Settings > Privacy.",
                IsOn = s => s.ArmoryShareOn },
            new OptIn { Id = "livepos", Title = "Show my map position to the Lodge's officers", NeedsLodge = true, InGuide = true,
                Text = "Officers see your dot on their world map while you play, never saved. It also needs \"/ehub position on\" in game once.",
                IsOn = s => s.ShareLivePosWithLodge },
        };

        public static bool Answered(Settings s, string id) => s.OptInsAnswered != null && s.OptInsAnswered.Contains(id);

        // what the card shows now: not shown before, not on already, and possible here (available: the Lodge part, decided by the caller)
        public static List<OptIn> Due(Settings s, Func<OptIn, bool> available) =>
            All.Where(o => !Answered(s, o.Id) && !o.IsOn(s) && (available == null || available(o))).ToList();

        public static void MarkAnswered(Settings s, IEnumerable<string> ids)
        {
            var list = s.OptInsAnswered ?? new List<string>();
            foreach (var id in ids) if (!list.Contains(id)) list.Add(id);
            s.OptInsAnswered = list;
        }

        // the welcome guide asked about these already
        public static void MarkGuideAnswered(Settings s) => MarkAnswered(s, All.Where(o => o.InGuide).Select(o => o.Id));
    }
}
