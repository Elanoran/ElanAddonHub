using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ElansAddonHub
{
    // ELANSHUB_DESIGN_SHOTS=<scale> with --selftest <dir>: renders the Addons page in its main states (rest, hover, expanded, scrolled)
    // at the given UI scale (1 = 100 %, 1.25 = 125 %) so design passes can be compared before / after. Quits when done.
    public partial class MainWindow
    {
        void SnapshotScaled(string file, double scale)
        {
            UpdateLayout();
            int w = (int)Math.Round(ActualWidth * scale), h = (int)Math.Round(ActualHeight * scale);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.PushTransform(new ScaleTransform(scale, scale));
                dc.DrawRectangle(new VisualBrush(this) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, ActualWidth, ActualHeight));
                dc.Pop();
            }
            var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(dv);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using (var fs = System.IO.File.Create(file)) enc.Save(fs);
        }

        static T FindDown<T>(DependencyObject root, Func<T, bool> pick = null) where T : DependencyObject
        {
            if (root == null) return null;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var c = VisualTreeHelper.GetChild(root, i);
                if (c is T t && (pick == null || pick(t))) return t;
                var r = FindDown(c, pick);
                if (r != null) return r;
            }
            return null;
        }

        // the element of the n-th card of an ItemsControl (our DataTemplate root)
        static FrameworkElement CardVisual(System.Windows.Controls.ItemsControl ic, int n)
        {
            var cp = ic.ItemContainerGenerator.ContainerFromIndex(n) as DependencyObject;
            return cp == null ? null : FindDown<FrameworkElement>(cp);
        }

        async Task DesignShots(string dir, string scaleText)
        {
            double scale = 1;
            double.TryParse(scaleText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out scale);
            if (scale < 0.5) scale = 1;
            string tag = scale == 1 ? "100" : ((int)(scale * 100)).ToString();
            for (int i = 0; i < 60 && (cards.Count < 4 || others.Count < 4); i++) await Task.Delay(200);
            ShowTab("addons");
            Height = 760;
            if (cards.Count >= 4)
            {
                cards[0].SetPillForTest("Update to 1.23.0", PillKind.Action, "x", true);
                cards[1].SetPillForTest("Install", PillKind.Action, "x", true);
                cards[2].SetPillForTest("Up to date", PillKind.Quiet, "x", false);
            }
            if (others.Count >= 4)
            {
                others[0].SetPillForTest("Update", PillKind.Action, "x", true);
                others[1].SetPillForTest("Up to date", PillKind.Quiet, "x", false);
                others[2].SetPillForTest("Choose file", PillKind.Action, "x", true);
                others[3].SetPillForTest("Dev copy", PillKind.Quiet, "x", false);
            }
            RefreshRailBadges();
            await Task.Delay(700);
            AddonsPage.ScrollToTop();
            await Task.Delay(300);
            SnapshotScaled(System.IO.Path.Combine(dir, $"d1-rest-{tag}.png"), scale);

            // hover: the second own card and the second other card
            //AFTER
            Motion.SetIsHot(CardVisual(Cards, 1), true);
            //AFTER
            await Task.Delay(500);
            SnapshotScaled(System.IO.Path.Combine(dir, $"d2-hover-{tag}.png"), scale);
            //AFTER
            Motion.SetIsHot(CardVisual(Cards, 1), false);
            //AFTER

            // expanded own card
            cards[0].Expanded = true;
            await Task.Delay(700);
            SnapshotScaled(System.IO.Path.Combine(dir, $"d3-expanded-{tag}.png"), scale);
            cards[0].Expanded = false;
            await Task.Delay(500);

            // the other addons, one expanded and one hovered
            others[1].Expanded = true;
            await Task.Delay(700);
            var y = OtherPanel.TranslatePoint(new Point(0, 0), AddonsPage).Y + AddonsPage.VerticalOffset - 12;
            AddonsPage.ScrollToVerticalOffset(Math.Max(0, y));
            await Task.Delay(400);
            //AFTER
            Motion.SetIsHot(CardVisual(OtherCards, 3), true);
            //AFTER
            await Task.Delay(500);
            SnapshotScaled(System.IO.Path.Combine(dir, $"d4-others-{tag}.png"), scale);
            //AFTER
            Motion.SetIsHot(CardVisual(OtherCards, 3), false);
            //AFTER
            others[1].Expanded = false;
            await Task.Delay(300);
            ShowTab("settings");
            foreach (var sec in new[] { "general", "addons", "lodge", "privacy", "voice", "overlay", "notify", "about" })
            {
                SettingsPage.Show(sec);
                await Task.Delay(sec == "about" ? 1200 : 450);
                SnapshotScaled(System.IO.Path.Combine(dir, $"s-{sec}-{tag}.png"), scale);
            }
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "result.txt"), "design shots done " + tag);
            Quit();
        }
    }
}
