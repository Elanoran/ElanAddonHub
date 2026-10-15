using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ElansAddonHub.Services.Db2;

namespace ElansAddonHub.Services.Atlas
{
    // What an Atlas page of any kind looks like as a game tooltip: the lines the game itself would show, taken only from what the page model really
    // knows (nothing is invented), and the rest of the facts for the footer. Pure data so the self-test can check it; GameTooltip.FromAtlas draws it.
    public sealed class AtlasTipPlan
    {
        public string Kind = "", Title = "", Rank = null;
        public string Tone = "white";                 // hostile | neutral | friendly | gold | white | quality
        public int Quality = -1;
        public List<TipLine> Lines = new List<TipLine>();
        public List<string> Footer = new List<string>();
    }

    public static class AtlasTip
    {
        static string Fact(AtlasPageModel m, string label) => m.Facts.FirstOrDefault(f => f.Label == label)?.Value;
        static string Text(AtlasPageModel m, string title) => m.Texts.FirstOrDefault(t => t.Title == title)?.Body;

        // hostile / friendly / neutral from a faction description ("Stormwind (friendly to the Alliance, hostile to the Horde)"); "" = mixed or unknown
        public static string Reaction(string factionText)
        {
            if (string.IsNullOrEmpty(factionText)) return "";
            bool hostA = factionText.Contains("hostile to the Alliance"), hostH = factionText.Contains("hostile to the Horde");
            bool friA = factionText.Contains("friendly to the Alliance"), friH = factionText.Contains("friendly to the Horde");
            if (hostA && hostH) return "hostile";
            if (friA && friH) return "friendly";
            if (!hostA && !hostH && !friA && !friH) return "neutral";
            return "";
        }

        public static AtlasTipPlan For(AtlasPageModel m)
        {
            var p = new AtlasTipPlan { Kind = m.Kind, Title = m.Title ?? "", Quality = m.Quality };
            var used = new HashSet<string>();
            string Take(string label) { used.Add(label); return Fact(m, label); }
            void Line(string kind, string text, string right = null) { if (!string.IsNullOrWhiteSpace(text)) p.Lines.Add(new TipLine { Kind = kind, Text = text, Right = right }); }
            switch (m.Kind)
            {
                case "item": p.Tone = "quality"; break;
                case "npc":
                    {
                        var title = Take("Title");
                        var lv = Take("Level"); var rank = Take("Rank"); var type = Take("Type");
                        var faction = Take("Faction");
                        var rea = Reaction(faction);
                        p.Tone = rea.Length > 0 ? rea : "white";
                        Line("plain", title);
                        bool boss = rank == "Boss";
                        var parts = new List<string>();
                        if (!string.IsNullOrEmpty(lv)) parts.Add("Level " + lv); else if (boss) parts.Add("Level ??");
                        if (!string.IsNullOrEmpty(rank) && rank != "Normal") parts.Add(rank);
                        if (!string.IsNullOrEmpty(type)) parts.Add(type);
                        Line("plain", string.Join(" ", parts));
                        if (!string.IsNullOrEmpty(faction)) Line("grey", faction);
                        break;
                    }
                case "obj":
                    {
                        var kind = Take("Kind");
                        if (!string.IsNullOrEmpty(kind)) Line("plain", char.ToUpperInvariant(kind[0]) + kind.Substring(1));
                        break;
                    }
                case "quest":
                    {
                        p.Tone = "gold";
                        var lv = Take("Level"); var min = Take("Requires level");
                        if (!string.IsNullOrEmpty(lv)) Line("plain", "Level " + lv);
                        if (!string.IsNullOrEmpty(min) && int.TryParse(min, out var ml)) p.Lines.Add(new TipLine { Kind = "req", Text = "Requires Level " + ml, Level = ml });
                        Line("plain", Text(m, "Objectives"));
                        foreach (var key in new[] { "rewards", "choices" })
                        {
                            var s = m.Section(key);
                            if (s == null || s.Rows.Count == 0) continue;
                            Line("header", key == "rewards" ? "You will receive:" : "Choose one of:");
                            foreach (var r in s.Rows) p.Lines.Add(new TipLine { Kind = "item", Text = r.Title, Quality = r.Quality });
                        }
                        break;
                    }
                case "spell":
                    {
                        var rank = Take("Rank"); p.Rank = rank;
                        var range = Take("Range"); var cast = Take("Cast time"); var dur = Take("Duration"); var cost = Take("Cost"); var cd = Take("Cooldown");
                        if (!string.IsNullOrEmpty(cost)) Line("slot", cost, string.IsNullOrEmpty(range) ? null : range.Replace(" yards", " yd range"));       // "50 Mana      30 yd range", the game's own two-column line
                        else if (!string.IsNullOrEmpty(range)) Line("range", range.Replace(" yards", " yd range"));
                        Line("cast", cast == "Instant" ? "Instant" : cast + " cast", string.IsNullOrEmpty(cd) ? null : cd + " cooldown");
                        Line("spelldesc", Text(m, "Description"));
                        Line("spelldesc", Text(m, "While it lasts"));
                        if (!string.IsNullOrEmpty(dur)) Line("plain", "Duration: " + dur);
                        break;
                    }
                case "skill": { var k = Take("Kind"); Line("plain", k); break; }
                case "zone":
                    {
                        p.Tone = "gold";
                        var lv = Take("Creature levels"); var type = Take("Type"); var inn = Take("In");
                        if (!string.IsNullOrEmpty(lv)) Line("plain", "Level " + lv);
                        Line("plain", type);
                        if (!string.IsNullOrEmpty(inn)) Line("grey", inn);
                        break;
                    }
                case "instance":
                    {
                        p.Tone = "gold";
                        var lv = Take("Creature levels"); var type = Take("Type"); var pl = Take("Players");
                        if (!string.IsNullOrEmpty(lv)) Line("plain", "Level " + lv);
                        Line("plain", string.IsNullOrEmpty(pl) ? type : type + " (" + pl + " players)");
                        break;
                    }
                case "class":
                    {
                        Line("plain", Take("Roles"));
                        var res = Take("Resource");
                        if (!string.IsNullOrEmpty(res)) Line("grey", res);
                        break;
                    }
                case "race":
                    {
                        Line("plain", Take("Side"));
                        var also = Take("Also called");
                        if (!string.IsNullOrEmpty(also)) Line("grey", also);
                        break;
                    }
                case "faction":
                    {
                        p.Tone = "gold";
                        Line("plain", Text(m, "About"));
                        break;
                    }
            }
            foreach (var f in m.Facts) if (!used.Contains(f.Label) && f.Link == null) p.Footer.Add(f.Label + ": " + f.Value);       // what the game would not show; a linked fact stays clickable on the page
            if (m.Kind == "quest") { var qd = Text(m, "Description"); if (!string.IsNullOrWhiteSpace(qd)) p.Footer.Add(qd.Trim()); }
            return p;
        }
    }
}
