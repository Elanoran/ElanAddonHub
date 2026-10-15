using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ElansAddonHub.Services.Atlas;

namespace ElansAddonHub
{
    // the Atlas kinds (creature, object, quest, spell, skill, zone, instance, faction, item) as game tooltips
    public sealed partial class GameTooltip
    {
        public void SetTitleBrush(Brush b)
        {
            var first = body.Children[0];
            if (first is TextBlock tb) tb.Foreground = b;
            else if (first is Grid g && g.Children.Count > 0 && g.Children[0] is TextBlock t0) t0.Foreground = b;
        }

        public static Brush ToneBrush(string tone, int quality)
        {
            switch (tone)
            {
                case "hostile": return Brush("Brush.GameTip.Red");
                case "neutral": return Brush("Brush.GameTip.Yellow");
                case "friendly": return Brush("Brush.GameTip.Green");
                case "gold": return Brush("Brush.GameTip.Gold");
                case "quality": return QualityBrush(quality);
                default: return Brush("Brush.GameTip.Text");
            }
        }

        // the game tooltip of an Atlas page: the game's lines in the panel, the other facts in the footer. `playerLevel` > 0 turns an unmet "Requires Level" red.
        public static GameTooltip FromAtlas(AtlasPageModel m, int playerLevel = 0)
        {
            var plan = AtlasTip.For(m);
            var gt = new GameTooltip(plan.Title, plan.Quality, plan.Rank, playerLevel);
            gt.SetTitleBrush(ToneBrush(plan.Tone, plan.Quality));
            if (!string.IsNullOrEmpty(m.Accent)) { try { gt.SetTitleBrush(ElansAddonHub.Lodge.Avatar.Frozen(m.Accent)); } catch (Exception) { } }
            if (m.Kind == "item") gt.AddLines(m.Tooltip); else gt.AddLines(plan.Lines);
            if (m.Kind != "item")
                foreach (var f in plan.Footer) gt.Footer.Children.Add(Muted(f, gt.Footer.Children.Count == 0 ? 0 : 2));
            return gt;
        }
    }
}
