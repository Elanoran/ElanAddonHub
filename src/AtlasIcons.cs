using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ElansAddonHub
{
    // The Atlas category icons: own line art, one per category, drawn on a 24 x 24 grid with the same stroke (1.6, round caps and joins, no fill) so the set reads
    // as one family. Used by the Atlas Home tiles, the Atlas side navigation and the Ctrl+K palette's page rows. The stroke takes the colour it is given
    // (a theme brush), so an icon follows the text colour of its row. A category without an icon keeps its font glyph.
    public static class AtlasIcons
    {
        public const double Grid = 24, Stroke = 1.6;

        // a circle as path data (two arcs), for the round parts of the drawings
        static string C(double x, double y, double r) => string.Format(CultureInfo.InvariantCulture, "M{0},{1} A{2},{2} 0 1 0 {3},{1} A{2},{2} 0 1 0 {0},{1} Z", x - r, y, r, x + r);
        // a dot: a zero-length line drawn with the round cap
        static string D(double x, double y) => string.Format(CultureInfo.InvariantCulture, "M{0},{1} L{0},{2}", x, y, y + 0.01);

        static readonly Dictionary<string, string> data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // a sword, point up-left
            ["items"] = "M4,4 L7.5,4 L16.5,13 L13,16.5 L4,7.5 Z M11.5,18.5 L18.5,11.5 M15.6,15.6 L19,19 " + C(20.2, 20.2, 1.2),
            // a paw: the pad and four toes
            ["creatures"] = "M7.5,17.2 C7.5,14.4 9.7,12.4 12,12.4 C14.3,12.4 16.5,14.4 16.5,17.2 C16.5,19.4 14.6,20.4 12,19.5 C9.4,20.4 7.5,19.4 7.5,17.2 Z "
                + C(5.2, 11.2, 1.7) + " " + C(9, 6.6, 1.9) + " " + C(15, 6.6, 1.9) + " " + C(18.8, 11.2, 1.7),
            // a chest with its lock
            ["objects"] = "M3.5,11 L20.5,11 L20.5,19.5 L3.5,19.5 Z M3.5,11 L3.5,8.5 C3.5,6.3 5.3,4.5 7.5,4.5 L16.5,4.5 C18.7,4.5 20.5,6.3 20.5,8.5 L20.5,11 M10.5,9.5 L13.5,9.5 L13.5,13.5 L10.5,13.5 Z",
            // a scroll with an exclamation mark
            ["quests"] = "M6.5,4 L17.5,4 C18.9,4 20,5.1 20,6.5 L20,7.5 L17,7.5 M6.5,4 C7.9,4 9,5.1 9,6.5 L9,17.5 C9,18.9 10.1,20 11.5,20 L4,20 C2.6,20 2,18.9 2,17.5 L2,16.5 L6.5,16.5 M11.5,20 L14.5,20 C15.9,20 17,18.9 17,17.5 L17,7.5 M6.5,4 C5.1,4 4,5.1 4,6.5 L4,16.5 "
                + "M13,8 L13,12.6 " + D(13, 15.4),
            // a globe
            ["worldmap"] = C(12, 12, 8.5) + " M12,3.5 C8.6,6.5 8.6,17.5 12,20.5 C15.4,17.5 15.4,6.5 12,3.5 M3.5,12 L20.5,12 M5.1,7.5 L18.9,7.5 M5.1,16.5 L18.9,16.5",
            // a map pin over hills
            ["zones"] = "M15,12 C15,12 11.5,8.7 11.5,6.5 C11.5,4.6 13.1,3 15,3 C16.9,3 18.5,4.6 18.5,6.5 C18.5,8.7 15,12 15,12 Z " + C(15, 6.5, 1.1)
                + " M2.5,20.5 L8,14.5 L11.3,18 M9.5,20.5 L14.5,15.5 L20,20.5 M2.5,20.5 L21.5,20.5",
            // an archway with a portcullis
            ["instances"] = "M4,20.5 L4,10.5 C4,6.1 7.6,3 12,3 C16.4,3 20,6.1 20,10.5 L20,20.5 M8.5,20.5 L8.5,12.5 C8.5,10.6 10.1,9 12,9 C13.9,9 15.5,10.6 15.5,12.5 L15.5,20.5 M12,9 L12,20.5 M8.5,15 L15.5,15 M2.5,20.5 L21.5,20.5",
            // a spark and two small ones
            ["spells"] = "M11,4 L12.6,10.4 L19,12 L12.6,13.6 L11,20 L9.4,13.6 L3,12 L9.4,10.4 Z M18.5,3 L18.5,7 M16.5,5 L20.5,5 M18,17.5 L18,20.5 M16.5,19 L19.5,19",
            // a crest: a shield with a chevron
            ["classes"] = "M12,3 L19.5,5.5 L19.5,11 C19.5,15.8 16.3,19.3 12,21 C7.7,19.3 4.5,15.8 4.5,11 L4.5,5.5 Z M8.5,10 L12,13.2 L15.5,10",
            // two people, one in front of the other
            ["races"] = C(9, 8, 3) + " M3,20 C3,16.4 5.7,14 9,14 C12.3,14 15,16.4 15,20 M14.4,4.6 C15,4.2 15.7,4 16.4,4 C18.1,4 19.4,5.4 19.4,7 C19.4,8.7 18.1,10 16.4,10 M16.6,13 C19.2,13.2 21,15.3 21,18.5",
            // a talent tree: one root, branching to three
            ["talents"] = C(12, 19, 2) + " " + C(12, 11.5, 2) + " " + C(5.5, 5, 2) + " " + C(18.5, 5, 2) + " M12,17 L12,13.5 M10.6,10.1 L6.9,6.4 M13.4,10.1 L17.1,6.4",
            // an anvil
            ["skills"] = "M3.5,7.5 L17.5,7.5 C17.5,9.6 19,10.8 21,10.8 L21,12.5 L15,12.5 L15,15 L17,17 L17,19.5 L7,19.5 L7,17 L9,15 L9,12.5 L7,12.5 C5,12.5 3.5,11 3.5,9 Z M10,4 L14,4",
            // a book with a flask on its cover
            ["recipes"] = "M4.5,5 C4.5,4.2 5.2,3.5 6,3.5 L19.5,3.5 L19.5,20.5 L6,20.5 C5.2,20.5 4.5,19.8 4.5,19 Z M4.5,17.5 C4.5,16.7 5.2,16 6,16 L19.5,16 "
                + "M10.5,6.5 L14,6.5 M11.2,6.5 L11.2,8.6 L9.1,12.3 C8.8,12.9 9.2,13.5 9.8,13.5 L14.7,13.5 C15.3,13.5 15.7,12.9 15.4,12.3 L13.3,8.6 L13.3,6.5",
            // a hammer and a pick, crossed
            ["professions"] = "M4.5,19.5 L15.4,8.6 M19.7,8.6 L17.6,10.7 L13.3,6.4 L15.4,4.3 Z M19.5,19.5 L6.8,6.8 M3,12 Q6,6 12,3",
            // a hanging banner with an emblem
            ["factions"] = "M4,3.5 L20,3.5 M6,3.5 L6,20 L12,16.5 L18,20 L18,3.5 M12,7.5 L14,10 L12,12.5 L10,10 Z",
            // a cut gem
            ["finds"] = "M7,4 L17,4 L21,9 L12,20.5 L3,9 Z M3,9 L21,9 M9.5,9 L12,20.5 L14.5,9 M7,4 L9.5,9 L12,4 L14.5,9 L17,4",
        };
        static readonly Dictionary<string, string> alias = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["map"] = "worldmap" };
        static readonly Dictionary<string, Geometry> cache = new Dictionary<string, Geometry>(StringComparer.OrdinalIgnoreCase);

        public static IEnumerable<string> Keys => data.Keys;

        // the drawing of a category id (or "finds"), frozen; null when it has none
        public static Geometry For(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            if (alias.TryGetValue(id, out var a)) id = a;
            lock (cache)
            {
                if (cache.TryGetValue(id, out var g)) return g;
                g = null;
                if (data.TryGetValue(id, out var d))
                {
                    try { g = Geometry.Parse(d); g.Freeze(); } catch (Exception) { g = null; }
                }
                cache[id] = g;
                return g;
            }
        }

        public static bool Has(string id) => For(id) != null;

        // the icon as an element: the 24 x 24 drawing scaled to `size` (strokes scale with it, so every size keeps the same weight relative to the drawing).
        // brushKey is a theme brush (follows the theme); null = the element's inherited Foreground is not used, pass a brush with SetStroke instead.
        public static FrameworkElement Make(string id, double size, string brushKey)
        {
            var g = For(id);
            if (g == null) return null;
            var path = new Path { Data = g, StrokeThickness = Stroke, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round, SnapsToDevicePixels = false };
            if (brushKey != null) path.SetResourceReference(Shape.StrokeProperty, brushKey);
            var canvas = new Canvas { Width = Grid, Height = Grid };
            canvas.Children.Add(path);
            var box = new Viewbox { Width = size, Height = size, Stretch = Stretch.Uniform, Child = canvas, Tag = "atlas-icon|" + id, IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            return box;
        }

        // the stroke of an icon made by Make (for rows that colour it themselves)
        public static void SetStroke(FrameworkElement icon, Brush b)
        {
            if (icon is Viewbox v && v.Child is Canvas c && c.Children.Count > 0 && c.Children[0] is Path p) p.Stroke = b;
        }

        public static void SetStrokeKey(FrameworkElement icon, string key)
        {
            if (icon is Viewbox v && v.Child is Canvas c && c.Children.Count > 0 && c.Children[0] is Path p) p.SetResourceReference(Shape.StrokeProperty, key);
        }
    }
}
