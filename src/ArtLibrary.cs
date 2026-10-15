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
        // the window background (MainWindow.xaml has it as a plain ImageBrush; listed here so the self-test checks it loads)
        public static readonly ArtPiece Background = new ArtPiece("background.jpg", 0.75, 0.5);

        // zone pages: the uiMap id -> its banner
        public static readonly Dictionary<int, ArtPiece> Zones = new Dictionary<int, ArtPiece>
        {
            [1411] = new ArtPiece("zone-durotar.jpg", 0.5, 0.6),        // Durotar: the two towers and the sun
            [1438] = new ArtPiece("zone-teldrassil.jpg", 0.58, 0.66),   // Teldrassil: the white fawn under the great tree
        };

        // Atlas Home: the category id (AtlasCategories / AtlasHosts) -> its card art
        public static readonly Dictionary<string, ArtPiece> Categories = new Dictionary<string, ArtPiece>(StringComparer.OrdinalIgnoreCase)
        {
            ["instances"] = new ArtPiece("card-dungeons.jpg", 0.5, 0.5, 640),
            ["professions"] = new ArtPiece("card-professions.jpg", 0.5, 0.5, 640),
        };

        // talent trees: "CLASSFILE|spec index" (0-based, the trees left to right as the game lays them out) -> the spec art
        public static readonly Dictionary<string, ArtPiece> Specs = new Dictionary<string, ArtPiece>(StringComparer.OrdinalIgnoreCase)
        {
            ["HUNTER|0"] = new ArtPiece("talent-hunter-bm.jpg", 0.5, 0.72, 720),     // Beast Mastery: the wolf by the fire
            ["HUNTER|1"] = new ArtPiece("talent-hunter-mm.jpg", 0.5, 0.72, 720),     // Marksmanship
            ["HUNTER|2"] = new ArtPiece("talent-hunter-sv.jpg", 0.5, 0.72, 720),     // Survival
        };

        public static ArtPiece Zone(int uiMapId) => Zones.TryGetValue(uiMapId, out var a) ? a : null;
        public static ArtPiece Category(string id) => !string.IsNullOrEmpty(id) && Categories.TryGetValue(id, out var a) ? a : null;
        public static ArtPiece Spec(string classFile, int spec) => !string.IsNullOrEmpty(classFile) && spec >= 0 && Specs.TryGetValue(classFile + "|" + spec, out var a) ? a : null;

        public static IEnumerable<ArtPiece> All()
        {
            yield return Background;
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
