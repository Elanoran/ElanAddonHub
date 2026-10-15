using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ElansAddonHub
{
    // ELANSHUB_DESIGN_SHOTS=selfupdated with --selftest <dir>: the Addons page with the RXPGuides-like row (its own logo, the "Self" source icon and,
    // drawn next to it, that icon's tooltip) -> selfupdated.png. Quits when done.
    public partial class MainWindow
    {
        async Task SelfUpdatedShots(string dir)
        {
            try
            {
                Width = 1180; Height = 760;
                for (int i = 0; i < 60 && (cards.Count < 4 || others.Count < 4); i++) await Task.Delay(200);
                ShowTab("addons");
                BuildRxpLike(addOnsDir);
                await RefreshOthers();
                var rxp = others.First(c => c.Entry.Key == "RXPGuides");
                for (int i = 0; i < 50 && rxp.Logo == null; i++) await Task.Delay(100);
                await Task.Delay(700);
                var at = others.IndexOf(rxp);
                // bring the row into view: scroll the page until the row is mid-window
                var vis = CardVisual(OtherCards, at);
                vis?.BringIntoView();
                await Task.Delay(500);
                vis = CardVisual(OtherCards, at);
                var icon = FindDown<Border>(vis, b => (b.ToolTip as string) == ThirdPartyCard.SelfTip);
                if (vis != null && icon != null)
                {
                    var top = vis.TranslatePoint(new Point(0, 0), this);
                    AddonsPage.ScrollToVerticalOffset(AddonsPage.VerticalOffset + top.Y - 260);
                    await Task.Delay(500);
                }
                var file = Path.Combine(dir, "selfupdated.png");
                vis = CardVisual(OtherCards, at); icon = FindDown<Border>(vis, b => (b.ToolTip as string) == ThirdPartyCard.SelfTip);
                SnapshotWithTip(icon, ThirdPartyCard.SelfTip, file);
                File.WriteAllText(Path.Combine(dir, "result.txt"), "selfupdated design shot done" + (icon == null ? " (no icon found)" : ""));
            }
            catch (Exception e) { File.WriteAllText(Path.Combine(dir, "result.txt"), "design shots crashed: " + e); }
            Quit();
        }

        // a tooltip popup can't be captured: draw one with the same style under the anchor, on top of the window
        void SnapshotWithTip(FrameworkElement anchor, object text, string file)
        {
            UpdateLayout();
            var shell = new ToolTip { Content = text };
            shell.Resources.MergedDictionaries.Add(Application.Current.Resources);
            var at = anchor == null ? new Point(100, 100) : anchor.TranslatePoint(new Point(anchor.ActualWidth * 0.5, anchor.ActualHeight + 4), this);
            shell.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = shell.DesiredSize;
            if (at.X + size.Width + 8 > ActualWidth) at.X = ActualWidth - size.Width - 8;
            shell.Arrange(new Rect(at, size));
            shell.UpdateLayout();
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(new VisualBrush(this) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, ActualWidth, ActualHeight));
                dc.PushOpacity(0.35);
                dc.DrawRoundedRectangle(Brushes.Black, null, new Rect(at.X - 4, at.Y + 2, size.Width + 8, size.Height + 8), 14, 14);
                dc.Pop();
                dc.DrawRectangle(new VisualBrush(shell) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(at, size));
            }
            var bmp = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(dv);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using (var fs = File.Create(file)) enc.Save(fs);
        }
    }
}
