using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ElansAddonHub
{
    // ELANSHUB_README_SHOTS=<folder>: the pictures in README.md (docs/readme), all of the same frame - the window's own surface at 1280 x 800,
    // without the shadow margin. The sections and design modes that already show those pages call ReadmeShot at the right moment; without the
    // variable it does nothing (and costs nothing). Two things a window render can't see are laid over it: the world map's 3D view (a WebView,
    // its own capture) and an open popup (its own window). The Lodge's "server update" notice that the fixtures' old server versions raise is
    // hidden for the picture.
    public partial class MainWindow
    {
        public const int ReadmeW = 1280, ReadmeH = 800;
        static string ReadmeDir => Environment.GetEnvironmentVariable("ELANSHUB_README_SHOTS");
        static bool ReadmeOn => !string.IsNullOrEmpty(ReadmeDir);

        async Task ReadmeShot(string name, bool withMap = false, Popup popup = null)
        {
            if (!ReadmeOn) return;
            var savedState = WindowState; var savedW = Width; var savedH = Height;
            var notice = LodgePage?.ServerNotice; var noticeWas = notice?.Visibility ?? Visibility.Collapsed;
            FrameworkElement pcard = null; var pcardMargin = new Thickness(0);
            const double margin = 14;   // WindowFrame's shadow margin
            try
            {
                WindowState = WindowState.Normal;
                Width = ReadmeW + 2 * margin; Height = ReadmeH + 2 * margin;
                if (notice != null) notice.Visibility = Visibility.Collapsed;
                // a popup card keeps a transparent margin for its shadow, which a render would shift the card by: the card itself, as SnapshotElement does
                pcard = popup?.IsOpen == true ? popup.Child as FrameworkElement : null;
                if (pcard != null) { pcardMargin = pcard.Margin; pcard.Margin = new Thickness(0); }
                await Task.Delay(700); UpdateLayout();
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                byte[] mapPng = null; Rect mapRect = Rect.Empty;
                var web = withMap ? WorldMapView.WebForTest : null;
                if (web != null && web.IsVisible)
                {
                    await Task.Delay(1500);
                    mapPng = await WorldMapView.CaptureForTest();
                    mapRect = new Rect(web.TranslatePoint(new Point(0, 0), this), new Size(web.ActualWidth, web.ActualHeight));
                }
                int w = (int)Math.Round(ActualWidth), h = (int)Math.Round(ActualHeight);
                var whole = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                whole.Render(this);
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    dc.DrawImage(whole, new Rect(0, 0, w, h));
                    if (mapPng != null)
                    {
                        var map = new BitmapImage(); map.BeginInit(); map.StreamSource = new MemoryStream(mapPng); map.CacheOption = BitmapCacheOption.OnLoad; map.EndInit();
                        dc.DrawImage(map, mapRect);
                    }
                    if (popup?.IsOpen == true && popup.Child is FrameworkElement pc && PresentationSource.FromVisual(this) is PresentationSource ps)
                    {
                        // where the popup sits on screen, in the window's own units
                        var fromDevice = ps.CompositionTarget.TransformFromDevice;
                        var at = fromDevice.Transform(pc.PointToScreen(new Point(0, 0))) - fromDevice.Transform(PointToScreen(new Point(0, 0)));
                        var pb = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(pc.ActualWidth)), Math.Max(1, (int)Math.Ceiling(pc.ActualHeight)), 96, 96, PixelFormats.Pbgra32);
                        pb.Render(pc);
                        dc.DrawImage(pb, new Rect(at.X + pcardMargin.Left, at.Y + pcardMargin.Top, pb.PixelWidth, pb.PixelHeight));   // where the card shows with its margin
                    }
                }
                var pic = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32); pic.Render(dv);
                var crop = new CroppedBitmap(pic, new Int32Rect((int)margin, (int)margin, Math.Min(ReadmeW, w - 2 * (int)margin), Math.Min(ReadmeH, h - 2 * (int)margin)));
                Directory.CreateDirectory(ReadmeDir);
                var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(crop));
                using (var fs = File.Create(Path.Combine(ReadmeDir, name + ".png"))) enc.Save(fs);
            }
            catch (Exception e) { Services.Util.Log("readme shot " + name + ": " + e.Message); }
            finally
            {
                if (notice != null) notice.Visibility = noticeWas;
                if (pcard != null) pcard.Margin = pcardMargin;
                Width = savedW; Height = savedH; WindowState = savedState; await Task.Delay(300); UpdateLayout();
            }
        }
    }
}
