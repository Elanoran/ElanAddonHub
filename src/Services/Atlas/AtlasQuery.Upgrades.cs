using System;
using System.Collections.Generic;
using System.Linq;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Upgrades;

namespace ElansAddonHub.Services.Atlas
{
    public static partial class AtlasQuery
    {
        // "Upgrade for: Paehaen (Hunter) +14%": the characters of mine (with their gear saved by the Hub) that this item would improve, by the same engine as the
        // Characters page's Upgrades panel. Nothing is added for an item nobody can use or that is no better than what they wear.
        static void AddUpgradeFact(AtlasPageModel m, ClientItem ci)
        {
            if (ci == null || !ci.Equippable || ci.Quality < 1) return;
            try
            {
                var parts = new List<string>();
                foreach (var c in Characters())
                {
                    if (parts.Count >= 6) break;
                    var h = UpgradeEngine.HintFor(UpgradeChar.From(c), ci);
                    if (h == null) continue;
                    parts.Add(h.Who.Display + " " + (h.Pct == null ? "fills an empty " + UpgradeRules.SlotName(h.Slot).ToLowerInvariant() + " slot" : UpgradeScore.PctText(h.Pct)));
                }
                if (parts.Count > 0) m.Facts.Add(new AtlasFact("Upgrade for", string.Join(", ", parts), AtlasLabel.Mine));
            }
            catch (Exception e) { Util.Log("atlas: upgrade fact: " + e.GetType().Name); }
        }
    }
}
