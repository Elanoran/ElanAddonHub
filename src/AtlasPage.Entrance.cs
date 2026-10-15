using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Maps;

namespace ElansAddonHub
{
    // The "Entrance" card of a dungeon or raid page: where its way in is, on the map of the zone it stands in (a door marker), the zone and the position, a button to the
    // world map and a "Navigate here" that copies the /etk way command for it. The numbers are the game's own (Map.db2: the spot where a ghost of the instance is put).
    public partial class AtlasPage
    {
        public readonly List<AtlasMap> entranceMaps = new List<AtlasMap>();
        public readonly List<Button> entranceButtons = new List<Button>();     // "Show on world map", "Navigate here" (tests click them)
        public readonly List<TextBlock> entranceTexts = new List<TextBlock>();
        public string LastNavigateCopiedForTest { get; private set; }
        public IReadOnlyList<AtlasMap> EntranceMapsForTest => entranceMaps;
        public IReadOnlyList<Button> EntranceButtonsForTest => entranceButtons;
        public IReadOnlyList<TextBlock> EntranceTextsForTest => entranceTexts;

        void ClearEntrance() { foreach (var m in entranceMaps) { try { m.ClearMarkers(); } catch { } } entranceMaps.Clear(); entranceButtons.Clear(); entranceTexts.Clear(); }

        FrameworkElement EntranceCard(AtlasPageModel m)
        {
            var en = m.Entrance;
            var sp = new StackPanel();
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.Children.Add(Tb("Entrance", "Text.Section"));
            sp.Children.Add(head);

            // the zone (a link to its page) and the position, then where the number comes from
            var line = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            if (en.UiMapId > 0)
            {
                var z = GhostButton(en.ZoneName, () => { _ = Open("zone:" + en.UiMapId); }, "Open the page of " + en.ZoneName);
                z.Margin = new Thickness(-8, 0, 4, 0); z.Tag = "entrance-zone";
                line.Children.Add(z); entranceButtons.Add(z);
                var pos = Tb(en.Coords, "Text.Body"); pos.VerticalAlignment = VerticalAlignment.Center; pos.ToolTip = "Position on the zone map, in percent from the left / top (x " + Math.Round(en.X) + ", y " + Math.Round(en.Y) + " in the world)";
                line.Children.Add(pos); entranceTexts.Add(pos);
            }
            else
            {
                var pos = Tb("x " + Math.Round(en.X) + ", y " + Math.Round(en.Y), "Text.Body"); pos.VerticalAlignment = VerticalAlignment.Center;
                line.Children.Add(pos); entranceTexts.Add(pos);
            }
            sp.Children.Add(line);
            sp.Children.Add(SourceChip(en.Label, 4));
            var src = Tb(en.Source, "Text.Caption", Res("Brush.Text.Tertiary"), 2); src.TextWrapping = TextWrapping.Wrap; sp.Children.Add(src);
            if (en.Wings.Length > 0) { var w = Tb("Wings: " + en.Wings + " (all through this entrance area)", "Text.Secondary", null, 6); w.TextWrapping = TextWrapping.Wrap; sp.Children.Add(w); }

            if (en.UiMapId > 0)
            {
                var map = new AtlasMap { Height = 240, Margin = new Thickness(0, 12, 0, 0) };
                entranceMaps.Add(map);
                map.LinkClicked += id => { _ = Open(id); };
                var marker = new AtlasMarker(en.Fx, en.Fy, MarkerKind.Entrance, en.InstanceName, "Entrance · " + en.Label, null);
                Defer(() => { map.ShowMap(en.UiMapId); map.AddMarkers(new List<AtlasMarker> { marker }); });
                sp.Children.Add(map);
            }

            var btns = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            if (en.WorldMapId != null && AtlasCategories.Find(Services.WorldMap.WorldMapIds.Category) != null)
            {
                var wb = GhostButton("Show on world map", () => { _ = Open(en.WorldMapId); }, "Open the world map of the continent, ringed at the entrance");
                wb.Margin = new Thickness(-8, 0, 8, 0); wb.Tag = "entrance-worldmap";
                btns.Children.Add(wb); entranceButtons.Add(wb);
            }
            if (en.Command != null)
            {
                var nb = GhostButton("Navigate here", () => CopyNavigate(en), "Copy " + en.Command + " - paste it in WoW chat (Elan's Toolkit sets the waypoint)");
                nb.Tag = "entrance-navigate"; nb.Margin = new Thickness(en.WorldMapId != null ? 0 : -8, 0, 8, 0);
                btns.Children.Add(nb); entranceButtons.Add(nb);
            }
            if (btns.Children.Count > 0) sp.Children.Add(btns);
            return Card(sp);
        }

        // copy the chat command (the same clipboard seam as the page link)
        void CopyNavigate(AtlasEntrance en)
        {
            bool ok = true;
            try { if (ClipboardSeamForTest != null) ClipboardSeamForTest(en.Command); else Clipboard.SetText(en.Command); }
            catch (Exception)
            {
                try { System.Threading.Thread.Sleep(80); if (ClipboardSeamForTest != null) ClipboardSeamForTest(en.Command); else Clipboard.SetText(en.Command); }
                catch (Exception) { ok = false; }
            }
            CopiedText.Text = ok ? "Navigate command copied" : "Could not reach the clipboard";
            CopiedText.SetResourceReference(TextBlock.ForegroundProperty, ok ? "Brush.Success" : "Brush.Danger");
            CopiedText.Visibility = Visibility.Visible;
            copiedTimer.Stop(); copiedTimer.Start();
            if (ok) LastNavigateCopiedForTest = en.Command;
        }
    }
}
