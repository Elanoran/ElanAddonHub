using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;

namespace ElansAddonHub
{
    // The "latest finds" strip on the Finds page: the newest finds of the Lodge as icons that scroll sideways (Controls\FindsStrip.cs). Hidden when there are none.
    public partial class AtlasPage
    {
        public const int FindsStripMax = 40;
        FindsStrip findsStrip;
        FrameworkElement findsStripBox;
        public FindsStrip FindsStripForTest => findsStrip;

        bool IsFindsPage => browse != null && !browse.NotFound && AtlasNav.KeyFor(browse.Query.ToId()) == AtlasNav.FindsKey;

        // "Found by Tess · 2× from Gnarlfang · Greenvale · 10 minutes ago"; a guest (no name sent) sees "Found by a member"
        public static string FindFoot(AtlasObs.Find f)
        {
            var parts = new List<string> { "Found by " + (string.IsNullOrEmpty(f.By) ? "a member" : f.By) };
            var from = FindFrom(f);
            parts.Add(f.Count + "×" + (from.Length > 0 ? " " + from : ""));
            if (f.Zone > 0) parts.Add(AtlasQuery.ZoneName(f.Zone));
            var ago = AtlasFormat.Ago(f.At, AtlasQuery.Context.NowUtc());
            if (ago.Length > 0) parts.Add(ago);
            return string.Join(" · ", parts);
        }

        static string FindFrom(AtlasObs.Find f)
        {
            var src = f.Src ?? "";
            int c = src.IndexOf(':'); int.TryParse(c > 0 ? src.Substring(c + 1) : "0", out var sid);
            var kind = c > 0 ? src.Substring(0, c) : src;
            return kind == "npc" ? "from " + AtlasQuery.NpcName(sid) : kind == "obj" ? "from " + AtlasQuery.ObjectName(sid) : kind == "vendor" ? "bought from " + AtlasQuery.NpcName(sid)
                : kind == "quest" ? "from quest " + AtlasQuery.QuestTitle(sid) : kind == "fish" ? "from fishing" : "";
        }

        List<FindsStrip.Entry> FindsEntries()
        {
            var L = AtlasQuery.Context.Lodge?.Invoke();
            var res = new List<FindsStrip.Entry>();
            if (L == null) return res;
            foreach (var f in L.AllFinds.OrderByDescending(f => f.Seq).Take(FindsStripMax))
                res.Add(new FindsStrip.Entry { Seq = f.Seq, Row = AtlasQuery.ItemRow(f.Item), Count = Math.Max(1, f.Count), Foot = FindFoot(f) });
            return res;
        }

        FrameworkElement FindsStripBlock()
        {
            findsStrip = null; findsStripBox = null;
            var entries = FindsEntries();
            if (entries.Count == 0) return null;
            var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            sp.Children.Add(Tb("Latest finds", "Text.Caption", Res("Brush.Text.Tertiary"), 0));
            var strip = new FindsStrip { Margin = new Thickness(0, 4, 0, 0) };
            strip.TipFor = en => en.Row.Link != null ? RowTipFor(en.Row) : null;
            strip.OpenLink = id => { _ = Open(id); };
            strip.Update(entries);
            sp.Children.Add(strip);
            findsStrip = strip; findsStripBox = sp;
            return sp;
        }

        // the Lodge pool got a page while the Finds list is open: new finds are put at the front of the strip (it stays where it is unless it is at the start)
        void UpdateFindsStrip()
        {
            if (!IsFindsPage) return;
            var entries = FindsEntries();
            if (findsStrip == null)
            {
                if (entries.Count == 0 || Body.Children.Count == 0 || !(Body.Children[0] is StackPanel col)) return;
                var blk = FindsStripBlock(); if (blk != null) col.Children.Insert(0, blk);
                return;
            }
            findsStrip.Update(entries);
            findsStripBox.Visibility = entries.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
