using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ElansAddonHub
{
    // The painted art (src\Assets\Art, JPEG resources): which picture goes where, and the brushes that show it. Everything is data: a zone, a
    // category or a talent tree without an entry keeps its plain look. Focal points (0..1 of the picture) say what stays in view when a slot
    // crops the picture (ArtLibrary.Focus keeps that point as close to the centre as the crop allows).
    public sealed class ArtPiece
    {
        public string File = "";              // under Assets/Art
        public double FocusX = 0.5, FocusY = 0.5;
        public int DecodeWidth;               // 0 = full size; smaller for pictures only ever shown small
        public ArtPiece(string file, double fx, double fy, int decode = 0) { File = file; FocusX = fx; FocusY = fy; DecodeWidth = decode; }
    }

    public static class ArtLibrary
    {
        // no window background picture since 2.70: the window is the Brush.Window gradient and the pages are solid panels (Theme.xaml)

        // zone pages: the uiMap id -> its banner
        public static readonly Dictionary<int, ArtPiece> Zones = new Dictionary<int, ArtPiece>
        {
            [1411] = new ArtPiece("zone-1411.jpg", 0.5, 0.36),    // Durotar
            [1412] = new ArtPiece("zone-1412.jpg", 0.5, 0.36),    // Mulgore
            [1413] = new ArtPiece("zone-1413.jpg", 0.5, 0.36),    // The Barrens
            [1416] = new ArtPiece("zone-1416.jpg", 0.5, 0.36),    // Alterac Mountains
            [1417] = new ArtPiece("zone-1417.jpg", 0.5, 0.36),    // Arathi Highlands
            [1418] = new ArtPiece("zone-1418.jpg", 0.5, 0.36),    // Badlands
            [1419] = new ArtPiece("zone-1419.jpg", 0.5, 0.36),    // Blasted Lands
            [1420] = new ArtPiece("zone-1420.jpg", 0.5, 0.36),    // Tirisfal Glades
            [1421] = new ArtPiece("zone-1421.jpg", 0.5, 0.36),    // Silverpine Forest
            [1422] = new ArtPiece("zone-1422.jpg", 0.5, 0.36),    // Western Plaguelands
            [1423] = new ArtPiece("zone-1423.jpg", 0.5, 0.36),    // Eastern Plaguelands
            [1424] = new ArtPiece("zone-1424.jpg", 0.5, 0.36),    // Hillsbrad Foothills
            [1425] = new ArtPiece("zone-1425.jpg", 0.5, 0.36),    // The Hinterlands
            [1426] = new ArtPiece("zone-1426.jpg", 0.5, 0.36),    // Dun Morogh
            [1427] = new ArtPiece("zone-1427.jpg", 0.5, 0.36),    // Searing Gorge
            [1428] = new ArtPiece("zone-1428.jpg", 0.5, 0.36),    // Burning Steppes
            [1429] = new ArtPiece("zone-1429.jpg", 0.5, 0.36),    // Elwynn Forest
            [1430] = new ArtPiece("zone-1430.jpg", 0.5, 0.36),    // Deadwind Pass
            [1431] = new ArtPiece("zone-1431.jpg", 0.5, 0.36),    // Duskwood
            [1432] = new ArtPiece("zone-1432.jpg", 0.5, 0.36),    // Loch Modan
            [1433] = new ArtPiece("zone-1433.jpg", 0.5, 0.36),    // Redridge Mountains
            [1434] = new ArtPiece("zone-1434.jpg", 0.5, 0.36),    // Stranglethorn Vale
            [1435] = new ArtPiece("zone-1435.jpg", 0.5, 0.36),    // Swamp of Sorrows
            [1436] = new ArtPiece("zone-1436.jpg", 0.5, 0.36),    // Westfall
            [1437] = new ArtPiece("zone-1437.jpg", 0.5, 0.36),    // Wetlands
            [1438] = new ArtPiece("zone-1438.jpg", 0.5, 0.36),    // Teldrassil
            [1439] = new ArtPiece("zone-1439.jpg", 0.5, 0.36),    // Darkshore
            [1440] = new ArtPiece("zone-1440.jpg", 0.5, 0.36),    // Ashenvale
            [1441] = new ArtPiece("zone-1441.jpg", 0.5, 0.36),    // Thousand Needles
            [1442] = new ArtPiece("zone-1442.jpg", 0.5, 0.36),    // Stonetalon Mountains
            [1443] = new ArtPiece("zone-1443.jpg", 0.5, 0.36),    // Desolace
            [1444] = new ArtPiece("zone-1444.jpg", 0.5, 0.36),    // Feralas
            [1445] = new ArtPiece("zone-1445.jpg", 0.5, 0.36),    // Dustwallow Marsh
            [1446] = new ArtPiece("zone-1446.jpg", 0.5, 0.36),    // Tanaris
            [1447] = new ArtPiece("zone-1447.jpg", 0.5, 0.36),    // Azshara
            [1448] = new ArtPiece("zone-1448.jpg", 0.5, 0.36),    // Felwood
            [1449] = new ArtPiece("zone-1449.jpg", 0.5, 0.36),    // Un'Goro Crater
            [1450] = new ArtPiece("zone-1450.jpg", 0.5, 0.36),    // Moonglade
            [1451] = new ArtPiece("zone-1451.jpg", 0.5, 0.36),    // Silithus
            [1452] = new ArtPiece("zone-1452.jpg", 0.5, 0.36),    // Winterspring
            [1453] = new ArtPiece("zone-1453.jpg", 0.5, 0.36),    // Stormwind City
            [1454] = new ArtPiece("zone-1454.jpg", 0.5, 0.36),    // Orgrimmar
            [1455] = new ArtPiece("zone-1455.jpg", 0.5, 0.36),    // Ironforge
            [1456] = new ArtPiece("zone-1456.jpg", 0.5, 0.36),    // Thunder Bluff
            [1457] = new ArtPiece("zone-1457.jpg", 0.5, 0.36),    // Darnassus
            [1458] = new ArtPiece("zone-1458.jpg", 0.5, 0.36),    // Undercity
            [2482] = new ArtPiece("zone-2482.jpg", 0.5, 0.36),    // Mount Hyjal
            [2521] = new ArtPiece("zone-2521.jpg", 0.5, 0.36),    // Zephras Isle
            [2548] = new ArtPiece("zone-2548.jpg", 0.5, 0.36),    // Riverglades
            [2652] = new ArtPiece("zone-2652.jpg", 0.5, 0.36),    // Shen'dralas
            [2665] = new ArtPiece("zone-2521.jpg", 0.5, 0.36),    // Zephras Isle, the flight-map copy of 2521
        };

        // Atlas Home: the category id (AtlasCategories / AtlasHosts) -> its card art
        public static readonly Dictionary<string, ArtPiece> Categories = new Dictionary<string, ArtPiece>(StringComparer.OrdinalIgnoreCase)
        {
            ["items"] = new ArtPiece("card-items.jpg", 0.5, 0.5, 640),
            ["creatures"] = new ArtPiece("card-npcs.jpg", 0.5, 0.5, 640),
            ["objects"] = new ArtPiece("card-objects.jpg", 0.5, 0.5, 640),
            ["quests"] = new ArtPiece("card-quests.jpg", 0.5, 0.5, 640),
            ["zones"] = new ArtPiece("card-zones.jpg", 0.5, 0.5, 640),
            ["instances"] = new ArtPiece("card-dungeons.jpg", 0.5, 0.5, 640),
            ["spells"] = new ArtPiece("card-spells.jpg", 0.5, 0.5, 640),
            ["skills"] = new ArtPiece("card-professions.jpg", 0.5, 0.5, 640),
            ["recipes"] = new ArtPiece("card-recipes.jpg", 0.5, 0.5, 640),
            ["factions"] = new ArtPiece("card-factions.jpg", 0.5, 0.5, 640),
            ["classes"] = new ArtPiece("card-classes.jpg", 0.5, 0.5, 640),
            ["races"] = new ArtPiece("card-races.jpg", 0.5, 0.5, 640),
            ["talents"] = new ArtPiece("card-talents.jpg", 0.5, 0.5, 640),
            ["professions"] = new ArtPiece("card-professions.jpg", 0.5, 0.5, 640),
            ["worldmap"] = new ArtPiece("card-worldmap.jpg", 0.5, 0.5, 640),
            ["map"] = new ArtPiece("card-worldmap.jpg", 0.5, 0.5, 640),
            ["finds"] = new ArtPiece("card-finds.jpg", 0.5, 0.5, 640),
        };

        // talent trees: "CLASSFILE|spec index" (0-based, the trees left to right as the game lays them out) -> the spec art
        public static readonly Dictionary<string, ArtPiece> Specs = new Dictionary<string, ArtPiece>(StringComparer.OrdinalIgnoreCase)
        {
            ["WARRIOR|0"] = new ArtPiece("talent-warrior-0.jpg", 0.5, 0.72, 720),
            ["WARRIOR|1"] = new ArtPiece("talent-warrior-1.jpg", 0.5, 0.72, 720),
            ["WARRIOR|2"] = new ArtPiece("talent-warrior-2.jpg", 0.5, 0.72, 720),
            ["PALADIN|0"] = new ArtPiece("talent-paladin-0.jpg", 0.5, 0.72, 720),
            ["PALADIN|1"] = new ArtPiece("talent-paladin-1.jpg", 0.5, 0.72, 720),
            ["PALADIN|2"] = new ArtPiece("talent-paladin-2.jpg", 0.5, 0.72, 720),
            ["HUNTER|0"] = new ArtPiece("talent-hunter-bm.jpg", 0.5, 0.72, 720),
            ["HUNTER|1"] = new ArtPiece("talent-hunter-1.jpg", 0.5, 0.72, 720),
            ["HUNTER|2"] = new ArtPiece("talent-hunter-sv.jpg", 0.5, 0.72, 720),
            ["ROGUE|0"] = new ArtPiece("talent-rogue-0.jpg", 0.5, 0.72, 720),
            ["ROGUE|1"] = new ArtPiece("talent-rogue-1.jpg", 0.5, 0.72, 720),
            ["ROGUE|2"] = new ArtPiece("talent-rogue-2.jpg", 0.5, 0.72, 720),
            ["PRIEST|0"] = new ArtPiece("talent-priest-0.jpg", 0.5, 0.72, 720),
            ["PRIEST|1"] = new ArtPiece("talent-priest-1.jpg", 0.5, 0.72, 720),
            ["PRIEST|2"] = new ArtPiece("talent-priest-2.jpg", 0.5, 0.72, 720),
            ["SHAMAN|0"] = new ArtPiece("talent-shaman-0.jpg", 0.5, 0.72, 720),
            ["SHAMAN|1"] = new ArtPiece("talent-shaman-1.jpg", 0.5, 0.72, 720),
            ["SHAMAN|2"] = new ArtPiece("talent-shaman-2.jpg", 0.5, 0.72, 720),
            ["MAGE|0"] = new ArtPiece("talent-mage-0.jpg", 0.5, 0.72, 720),
            ["MAGE|1"] = new ArtPiece("talent-mage-1.jpg", 0.5, 0.72, 720),
            ["MAGE|2"] = new ArtPiece("talent-mage-2.jpg", 0.5, 0.72, 720),
            ["WARLOCK|0"] = new ArtPiece("talent-warlock-0.jpg", 0.5, 0.72, 720),
            ["WARLOCK|1"] = new ArtPiece("talent-warlock-1.jpg", 0.5, 0.72, 720),
            ["WARLOCK|2"] = new ArtPiece("talent-warlock-2.jpg", 0.5, 0.72, 720),
            ["DRUID|0"] = new ArtPiece("talent-druid-0.jpg", 0.5, 0.72, 720),
            ["DRUID|1"] = new ArtPiece("talent-druid-1.jpg", 0.5, 0.72, 720),
            ["DRUID|2"] = new ArtPiece("talent-druid-2.jpg", 0.5, 0.72, 720),
        };

        public static ArtPiece Zone(int uiMapId) => Zones.TryGetValue(uiMapId, out var a) ? a : null;
        public static ArtPiece Category(string id) => !string.IsNullOrEmpty(id) && Categories.TryGetValue(id, out var a) ? a : null;
        public static ArtPiece Spec(string classFile, int spec) => !string.IsNullOrEmpty(classFile) && spec >= 0 && Specs.TryGetValue(classFile + "|" + spec, out var a) ? a : null;

        public static IEnumerable<ArtPiece> All()
        {
            foreach (var a in Zones.Values) yield return a;
            foreach (var a in Categories.Values) yield return a;
            foreach (var a in Specs.Values) yield return a;
        }

        // ---- loading: once per picture and decode size, frozen; null (and the slot stays plain) when the resource cannot be read
        static readonly Dictionary<string, BitmapSource> cache = new Dictionary<string, BitmapSource>();

        public static BitmapSource Load(ArtPiece a)
        {
            if (a == null) return null;
            var key = a.File + "|" + a.DecodeWidth;
            lock (cache)
            {
                if (cache.TryGetValue(key, out var have)) return have;
                BitmapSource bmp = null;
                try
                {
                    var b = new BitmapImage();
                    b.BeginInit();
                    b.UriSource = new Uri("pack://application:,,,/Assets/Art/" + a.File, UriKind.Absolute);
                    b.CacheOption = BitmapCacheOption.OnLoad;
                    if (a.DecodeWidth > 0) b.DecodePixelWidth = a.DecodeWidth;
                    b.EndInit();
                    b.Freeze();
                    bmp = b;
                }
                catch (Exception) { bmp = null; }
                cache[key] = bmp;
                return bmp;
            }
        }

        // ---- a brush that fills `target` like UniformToFill but keeps the piece's focal point in view (the crop slides towards it)
        public static ImageBrush Focus(FrameworkElement target, ArtPiece a)
        {
            var img = Load(a);
            if (img == null || target == null) return null;
            var brush = new ImageBrush(img) { Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.RelativeToBoundingBox };
            RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.HighQuality);
            void Fit()
            {
                double w = target.ActualWidth, h = target.ActualHeight;
                if (w < 1 || h < 1 || img.PixelWidth < 1 || img.PixelHeight < 1) return;
                brush.Viewbox = Crop(img.PixelWidth / (double)img.PixelHeight, w / h, a.FocusX, a.FocusY);
            }
            target.SizeChanged += (s, e) => Fit();
            Fit();
            return brush;
        }

        // the part of a picture (relative 0..1) with the target's aspect, as large as it can be, centred on the focal point where the edges allow
        public static Rect Crop(double imageAspect, double targetAspect, double fx, double fy)
        {
            if (imageAspect <= 0 || targetAspect <= 0) return new Rect(0, 0, 1, 1);
            if (targetAspect >= imageAspect)
            {
                double hf = imageAspect / targetAspect;
                double y = Math.Max(0, Math.Min(1 - hf, fy - hf / 2));
                return new Rect(0, y, 1, hf);
            }
            double wf = targetAspect / imageAspect;
            double x = Math.Max(0, Math.Min(1 - wf, fx - wf / 2));
            return new Rect(x, 0, wf, 1);
        }

        // a rounded picture layer for the back of a card: the art (focused), then a veil brush over it. Not hit-testable; the caller puts it first in a Grid.
        public static FrameworkElement Backdrop(ArtPiece a, string veilKey, CornerRadius radius, string tag)
        {
            var img = Load(a);
            if (img == null) return null;
            var g = new Grid { IsHitTestVisible = false, Tag = tag };
            var pic = new Border { CornerRadius = radius };
            pic.Background = Focus(pic, a);
            g.Children.Add(pic);
            var veil = new Border { CornerRadius = radius };
            veil.SetResourceReference(Border.BackgroundProperty, veilKey);
            g.Children.Add(veil);
            return g;
        }
    }
}
