using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ElansAddonHub
{
    // --selftest "inputs": the Input / SecretInput / Dropdown styles must not cut the descenders of g, j, p, q and y.
    // The check draws a sample control with "lodge gjpqy" (at 100 % and 125 %) and looks at the pixels: the ink of the text has to be as tall as the same text in a
    // box with room to spare, and no ink may sit on the inner bottom (or top) edge of the box. ELANSHUB_DESIGN_SHOTS=inputs runs the same audit on every input of
    // the real pages (see MainWindow.DesignTest.Inputs.cs).
    public partial class MainWindow
    {
        public const string InkSample = "lodge gjpqy";

        public sealed class InkReport
        {
            public int Top = -1, Bottom = -1, Rows, BoxPx, ClearBottom, ClearTop;
            public double HostHeight, LineHeight;
            public bool Any => Top >= 0;
        }

        // The ink (pixels clearly different from the Surface2 fill) of the left part of a laid-out element, drawn at `scale`. colFrom / colTo are fractions of the width.
        static InkReport MeasureInk(FrameworkElement el, double scale, double colFrom = 0, double colTo = 1)
        {
            double w = el.ActualWidth, h = el.ActualHeight;
            int pw = Math.Max(1, (int)Math.Round(w * scale)), ph = Math.Max(1, (int)Math.Round(h * scale));
            var back = ((SolidColorBrush)Application.Current.FindResource("Brush.Surface2")).Color;
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(back), null, new Rect(0, 0, pw, ph));
                dc.PushTransform(new ScaleTransform(scale, scale));
                dc.DrawRectangle(new VisualBrush(el) { Viewbox = new Rect(0, 0, w, h), ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill }, null, new Rect(0, 0, w, h));
                dc.Pop();
            }
            var bmp = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(dv);
            var px = new byte[pw * ph * 4];
            bmp.CopyPixels(px, pw * 4, 0);
            var r = new InkReport { BoxPx = ph };
            int x0 = (int)(pw * colFrom), x1 = (int)(pw * colTo);
            for (int y = 0; y < ph; y++)
            {
                bool row = false;
                for (int x = x0; x < x1 && !row; x++)
                {
                    int i = (y * pw + x) * 4;
                    int d = Math.Max(Math.Abs(px[i] - back.B), Math.Max(Math.Abs(px[i + 1] - back.G), Math.Abs(px[i + 2] - back.R)));
                    if (d > 60) row = true;
                }
                if (row) { if (r.Top < 0) r.Top = y; r.Bottom = y; }
            }
            if (r.Any) { r.Rows = r.Bottom - r.Top + 1; r.ClearTop = r.Top; r.ClearBottom = ph - 1 - r.Bottom; }
            return r;
        }

        static void LayOut(FrameworkElement el, double width, double height)
        {
            el.Measure(new Size(width, double.IsNaN(height) ? double.PositiveInfinity : height));
            el.Arrange(new Rect(0, 0, width, double.IsNaN(height) ? el.DesiredSize.Height : height));
            el.UpdateLayout();
        }

        static FrameworkElement ContentHost(Control c) => c.Template?.FindName("PART_ContentHost", c) as FrameworkElement;

        // the same text in a box with plenty of room: how tall the ink is when nothing is cut
        static int ReferenceRows(FontFamily family, double size, double scale, double width)
        {
            var tb = new TextBox { Style = (Style)Application.Current.FindResource("Input"), Text = InkSample, Width = width, Height = 80, FontFamily = family, FontSize = size };
            LayOut(tb, width, 80);
            return MeasureInk(tb, scale, 0, 0.8).Rows;
        }

        // null = fine, otherwise what is wrong
        static string JudgeInk(Control el, InkReport k, int refRows, double scale, FrameworkElement host)
        {
            int tol = (int)Math.Ceiling(scale);
            int border = (int)Math.Ceiling(1 * scale);
            if (!k.Any) return "no text drawn";
            if (k.Rows < refRows - tol) return $"descenders cut: ink {k.Rows} px tall, uncut text is {refRows} px";
            if (k.ClearBottom <= border) return $"ink touches the inner bottom edge (gap {k.ClearBottom} px, border {border} px)";
            if (k.ClearTop <= border) return $"ink touches the inner top edge (gap {k.ClearTop} px, border {border} px)";
            if (host != null && host.ActualHeight + 0.01 < el.FontSize * 1.3) return $"content area {host.ActualHeight:0.0} px is shorter than a line of {el.FontSize} px text";
            return null;
        }

        async Task<string> InputsTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            void Check(string what, bool cond, string extra = null) { SelfTestClock.Mark(what); notes.Add((cond ? "ok   " : "FAIL ") + what + (cond || extra == null ? "" : "  -> " + extra)); ok &= cond; }
            try
            {
                var input = (Style)FindResource("Input");
                var secret = (Style)FindResource("SecretInput");
                var dropdown = (Style)FindResource("Dropdown");
                // 100 % and 125 %, the heights the app uses (auto, 32 and 36) and a multi-line box
                foreach (var scale in new[] { 1.0, 1.25 })
                {
                    string tag = (int)(scale * 100) + "%";
                    var reference = ReferenceRows(SystemFonts.MessageFontFamily, 13, scale, 240);
                    Check($"ink: the reference text ('{InkSample}') has ink at {tag}", reference > 8, reference.ToString());
                    foreach (var height in new[] { double.NaN, 32, 36 })
                    {
                        var tb = new TextBox { Style = input, Text = InkSample, Width = 240 };
                        if (!double.IsNaN(height)) tb.Height = height;
                        LayOut(tb, 240, height);
                        var k = MeasureInk(tb, scale, 0, 0.8);
                        var why = JudgeInk(tb, k, ReferenceRows(tb.FontFamily, tb.FontSize, scale, 240), scale, ContentHost(tb));
                        Check($"input {(double.IsNaN(height) ? "auto" : height.ToString())} high at {tag}: 'gjpqy' is not clipped (box {tb.ActualHeight:0} px, ink {k.Rows} px, gap below {k.ClearBottom} px)", why == null, why);
                    }
                    var multi = new TextBox { Style = input, Text = InkSample, Width = 240, Height = 72, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
                    LayOut(multi, 240, 72);
                    var km = MeasureInk(multi, scale, 0, 0.8);
                    Check($"input multi-line at {tag}: first line not clipped at the top (gap {km.ClearTop} px)", km.Any && km.ClearTop > (int)Math.Ceiling(scale), km.ClearTop.ToString());
                    var mono = new TextBox { Style = input, Text = InkSample, Width = 240, Height = 32, FontFamily = (FontFamily)FindResource("Font.Mono"), FontSize = 12 };
                    LayOut(mono, 240, 32);
                    var kn = MeasureInk(mono, scale, 0, 0.8);
                    var whyMono = JudgeInk(mono, kn, ReferenceRows(mono.FontFamily, 12, scale, 240), scale, ContentHost(mono));
                    Check($"input mono 12 px, 32 high at {tag}: not clipped", whyMono == null, whyMono);
                    var cb = new ComboBox { Style = dropdown, Width = 240, Height = 32 };
                    cb.Items.Add(InkSample); cb.SelectedIndex = 0;
                    LayOut(cb, 240, 32);
                    var kc = MeasureInk(cb, scale, 0, 0.7);
                    Check($"dropdown 32 high at {tag}: 'gjpqy' is not clipped (gap below {kc.ClearBottom} px)", kc.Any && kc.Rows >= reference - (int)Math.Ceiling(scale) && kc.ClearBottom > (int)Math.Ceiling(scale), $"rows {kc.Rows} ref {reference} gap {kc.ClearBottom}");
                }
                var pw = new PasswordBox { Style = secret, Width = 240, Height = 32 };
                LayOut(pw, 240, 32);
                var pwHost = ContentHost(pw);
                Check("secret input: the content area holds a line of text (descender room)", pwHost != null && pwHost.ActualHeight >= 18, pwHost?.ActualHeight.ToString());
                var tbHost = new TextBox { Style = input, Height = 32, Width = 240 };
                LayOut(tbHost, 240, 32);
                Check("input: a 32 px box has a content area of at least 18 px (13 px text on a 18 px line)", ContentHost(tbHost) != null && ContentHost(tbHost).ActualHeight >= 18, ContentHost(tbHost)?.ActualHeight.ToString());
                var auto = new TextBox { Style = input, Width = 240, Text = "x" };
                LayOut(auto, 240, double.NaN);
                Check("input: an auto-height box is at least 32 high", auto.ActualHeight >= 32, auto.ActualHeight.ToString());
            }
            catch (Exception e) { ok = false; notes.Add("crashed: " + e); }
            await Task.Delay(1);
            return "inputs selftest: " + (ok ? "PASSED" : "FAILED") + "\r\n  " + string.Join("\r\n  ", notes);
        }
    }
}
