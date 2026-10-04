using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using ElansAddonHub.Services;

namespace ElansAddonHub.Lodge
{
    // Discord-style in-app picture viewer: dark overlay over the Lodge, fit-to-window, wheel zoom around the cursor,
    // drag to pan, double-click fit/100%, arrows between the channel's pictures. Loads from the already-downloaded cache file.
    public partial class ImageViewer : UserControl
    {
        const int MaxDecode = 4096;
        List<MessageVM> items = new List<MessageVM>();
        int index;
        BitmapSource bitmap;
        int loadToken;
        double zoom = 1;
        bool dragging; Point dragStart; double startX, startY;

        public bool IsOpen => Visibility == Visibility.Visible;
        public string CaptionForTest => CapName.Text + " | " + CapInfo.Text;
        public string ErrorForTest => ErrorText.Visibility == Visibility.Visible ? ErrorText.Text : null;

        public double ZoomForTest { get => zoom; set => ZoomAt(value, new Point(Hit.ActualWidth / 2, Hit.ActualHeight / 2)); }
        public int IndexForTest => index;

        public ImageViewer() { InitializeComponent(); }

        // all the channel's pictures that are downloaded, in order
        public static List<MessageVM> PicturesOf(IEnumerable<MessageVM> messages) =>
            messages.Where(m => m.File != null && m.File.IsImage && m.File.LocalPath != null).ToList();

        public void Open(IEnumerable<MessageVM> messages, MessageVM current)
        {
            items = PicturesOf(messages);
            index = Math.Max(0, items.IndexOf(current));
            if (items.Count == 0) return;
            if (!IsOpen)
            {
                Visibility = Visibility.Visible;
                BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
            }
            Keyboard.Focus(this); Focus();
            _ = ShowCurrent();
        }

        public void Close()
        {
            if (!IsOpen) return;
            loadToken++;
            Visibility = Visibility.Collapsed;
            BeginAnimation(OpacityProperty, null);
            Img.Source = null; bitmap = null; items = new List<MessageVM>(); // release the decoded picture
            ResetView();
        }

        MessageVM Current => index >= 0 && index < items.Count ? items[index] : null;

        async Task ShowCurrent()
        {
            var m = Current; if (m == null) return;
            var f = m.File;
            var token = ++loadToken;
            ResetView();
            ErrorText.Visibility = Visibility.Collapsed;
            CapName.Text = f.Name;
            CapInfo.Text = $"{f.SizeText}  ·  {m.From}  ·  {m.Time}" + (items.Count > 1 ? $"  ·  {index + 1} / {items.Count}" : "");
            PrevBtn.Visibility = NextBtn.Visibility = items.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            try
            {
                var path = f.LocalPath;
                var bmp = await Task.Run(() =>
                {
                    int w = BitmapFrame.Create(new Uri(path), BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).PixelWidth;
                    var b = new BitmapImage();
                    b.BeginInit();
                    b.CacheOption = BitmapCacheOption.OnLoad;
                    if (w > MaxDecode) b.DecodePixelWidth = MaxDecode;
                    b.UriSource = new Uri(path);
                    b.EndInit();
                    b.Freeze();
                    return b;
                });
                if (token != loadToken) return;
                bitmap = bmp;
                Img.Source = bmp;
                Img.Visibility = Visibility.Visible;
            }
            catch (Exception e)
            {
                if (token != loadToken) return;
                Util.Log("viewer: " + e.Message);
                Img.Source = null; bitmap = null;
                ErrorText.Text = "Couldn't show this picture (" + e.Message + ")";
                ErrorText.Visibility = Visibility.Visible;
            }
        }

        // ---- zoom / pan
        void ResetView() { zoom = 1; Scale.ScaleX = Scale.ScaleY = 1; Move.X = Move.Y = 0; Hit.Cursor = Cursors.Arrow; }

        void ZoomAt(double newZoom, Point p)
        {
            newZoom = Math.Max(1, Math.Min(8, newZoom));
            if (Math.Abs(newZoom - zoom) < 1e-6) return;
            if (newZoom <= 1) { ResetView(); return; }
            // keep the point under the cursor fixed (origin of the transform is the picture's centre = viewport centre)
            var c = new Point(Hit.ActualWidth / 2, Hit.ActualHeight / 2);
            double k = newZoom / zoom, dx = p.X - c.X, dy = p.Y - c.Y;
            Move.X = dx - k * (dx - Move.X);
            Move.Y = dy - k * (dy - Move.Y);
            zoom = newZoom;
            Scale.ScaleX = Scale.ScaleY = zoom;
            Hit.Cursor = Cursors.SizeAll;
        }

        void OnWheel(object sender, MouseWheelEventArgs e)
        {
            if (bitmap == null) return;
            ZoomAt(zoom * (e.Delta > 0 ? 1.18 : 1 / 1.18), e.GetPosition(Hit));
            e.Handled = true;
        }

        bool OverPicture(Point p)
        {
            if (Img.Visibility != Visibility.Visible || Img.ActualWidth <= 0) return false;
            var r = Img.TransformToAncestor(Hit).TransformBounds(new Rect(0, 0, Img.ActualWidth, Img.ActualHeight));
            return r.Contains(p);
        }

        void Hit_Down(object sender, MouseButtonEventArgs e)
        {
            var p = e.GetPosition(Hit);
            if (!OverPicture(p)) return; // bubbles to the backdrop: close
            e.Handled = true;
            if (e.ClickCount == 2)
            {
                if (zoom > 1) ResetView();
                else if (bitmap != null && Img.ActualWidth > 0)
                {
                    double fit = Img.ActualWidth / bitmap.Width; // fit scale, <= 1
                    if (fit < 0.999) ZoomAt(1 / fit, p);
                }
                return;
            }
            if (zoom > 1)
            {
                dragging = true; dragStart = e.GetPosition(this); startX = Move.X; startY = Move.Y;
                Hit.CaptureMouse();
            }
        }

        void Hit_Move(object sender, MouseEventArgs e)
        {
            if (!dragging) return;
            var p = e.GetPosition(this);
            Move.X = startX + (p.X - dragStart.X);
            Move.Y = startY + (p.Y - dragStart.Y);
        }

        void Hit_Up(object sender, MouseButtonEventArgs e)
        {
            if (!dragging) return;
            dragging = false; Hit.ReleaseMouseCapture();
        }

        void OnSizeChanged(object sender, SizeChangedEventArgs e) { if (zoom <= 1) ResetView(); }

        // ---- navigation / close
        void Backdrop_Down(object sender, MouseButtonEventArgs e) { if (!e.Handled) Close(); }
        void Close_Click(object sender, RoutedEventArgs e) => Close();
        void Prev_Click(object sender, RoutedEventArgs e) => Step(-1);
        void Next_Click(object sender, RoutedEventArgs e) => Step(1);

        public void Step(int d)
        {
            if (items.Count < 2) return;
            index = (index + d + items.Count) % items.Count;
            _ = ShowCurrent();
        }

        void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (!IsOpen) return;
            if (e.Key == Key.Escape) Close();
            else if (e.Key == Key.Left) Step(-1);
            else if (e.Key == Key.Right) Step(1);
            else return;
            e.Handled = true;
        }

        // ---- actions
        void OpenOriginal_Click(object sender, RoutedEventArgs e)
        {
            var f = Current?.File;
            if (f?.LocalPath != null)
                try { Process.Start(new ProcessStartInfo(f.LocalPath) { UseShellExecute = true }); } catch { }
        }

        void SaveAs_Click(object sender, RoutedEventArgs e)
        {
            var f = Current?.File; if (f?.LocalPath == null) return;
            var dlg = new Microsoft.Win32.SaveFileDialog { FileName = f.Name, Filter = "All files|*.*" };
            if (dlg.ShowDialog() != true) return;
            try { File.Copy(f.LocalPath, dlg.FileName, true); }
            catch (Exception ex) { ErrorText.Text = "Couldn't save: " + ex.Message; ErrorText.Visibility = Visibility.Visible; }
        }

        void Copy_Click(object sender, RoutedEventArgs e)
        {
            if (bitmap == null) return;
            try { Clipboard.SetImage(bitmap); } catch (Exception ex) { Util.Log("viewer copy: " + ex.Message); }
        }
    }
}
