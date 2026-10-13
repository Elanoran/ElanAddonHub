using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // ELANSHUB_DESIGN_SHOTS=inputs with --selftest <dir>: visits every page that has a text input, and for each visible TextBox / PasswordBox / ComboBox draws a
    // copy of it (same style, size, padding and font, so nothing on the page is changed) with "lodge gjpqy" at 100 % and 125 % and checks the pixels: the ink of
    // the descenders must not be cut and must not touch the box's inner edge. Writes inputs-audit.txt and inputs-sheet-100.png / -125.png (every copy, for looking at);
    // result.txt says PASSED or FAILED. Quits when done.
    public partial class MainWindow
    {
        sealed class InputRow { public string Where, Name, Kind; public double W, H; public InkReport Ink; public string Problem; public FrameworkElement Copy; }

        static IEnumerable<DependencyObject> InputWalk(DependencyObject root)
        {
            if (root == null) yield break;
            yield return root;
            var seen = new HashSet<DependencyObject>();
            var kids = new List<DependencyObject>();
            if (root is Visual || root is System.Windows.Media.Media3D.Visual3D)
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) kids.Add(VisualTreeHelper.GetChild(root, i));
            foreach (var o in LogicalTreeHelper.GetChildren(root)) if (o is DependencyObject d) kids.Add(d);
            if (root is Popup p && p.IsOpen && p.Child != null) kids.Add(p.Child);
            foreach (var k in kids)
            {
                if (!seen.Add(k)) continue;
                foreach (var d in InputWalk(k)) yield return d;
            }
        }

        // every visible text input under `root`, audited at `scale`
        // the plain look of a TextBox that has no style of its own (the chat composer, the browser address box): a border and the content host, nothing else
        static ControlTemplate PlainTextBoxTemplate()
        {
            return (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
                "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='TextBox'><Border Background='{TemplateBinding Background}'><ScrollViewer x:Name='PART_ContentHost' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' /></Border></ControlTemplate>");
        }

        List<InputRow> AuditInputs(DependencyObject root, string where, double scale, HashSet<DependencyObject> seen)
        {
            var rows = new List<InputRow>();
            foreach (var d in InputWalk(root))
            {
                var c = d as Control;
                if (!(c is TextBox || c is PasswordBox || c is ComboBox) || !c.IsVisible || c.ActualHeight < 8 || !seen.Add(c)) continue;
                if (c is TextBox rt && rt.IsReadOnly && rt.BorderThickness.Top == 0 && rt.Background == Brushes.Transparent && !(rt.Name ?? "").Contains("Box")) continue;   // chat message text
                var row = new InputRow { Where = where, Name = string.IsNullOrEmpty(c.Name) ? "(" + c.GetType().Name + ")" : c.Name, Kind = c.GetType().Name, W = c.ActualWidth, H = c.ActualHeight };
                try
                {
                    Control copy;
                    if (c is TextBox t)
                    {
                        var nt = new TextBox { Style = t.Style, Text = InkSample, FontFamily = t.FontFamily, FontSize = t.FontSize, Padding = t.Padding, MinHeight = t.MinHeight, Height = t.Height,
                                               AcceptsReturn = t.AcceptsReturn, TextWrapping = t.TextWrapping, VerticalContentAlignment = t.VerticalContentAlignment, BorderThickness = t.BorderThickness,
                                               Background = t.Background, Foreground = t.Foreground };
                        if (t.Style == null) nt.Template = PlainTextBoxTemplate();
                        copy = nt;
                    }
                    else if (c is PasswordBox pb)
                        copy = new PasswordBox { Style = pb.Style, FontFamily = pb.FontFamily, FontSize = pb.FontSize, Padding = pb.Padding, MinHeight = pb.MinHeight, Height = pb.Height };
                    else
                    {
                        var cb = (ComboBox)c;
                        var n = new ComboBox { Style = cb.Style, MinHeight = cb.MinHeight, Height = cb.Height, FontSize = cb.FontSize, FontFamily = cb.FontFamily };
                        n.Items.Add(InkSample); n.SelectedIndex = 0;
                        copy = n;
                    }
                    if (double.IsNaN(copy.Height)) copy.Height = c.ActualHeight;   // a box that sizes itself keeps the height the page gave it
                    double h = copy.Height;
                    LayOut(copy, Math.Max(40, c.ActualWidth), h);
                    row.Copy = copy;
                    if (copy is PasswordBox)
                    {
                        var host = ContentHost(copy);
                        row.Problem = host != null && host.ActualHeight >= 18 ? null : "content area " + (host?.ActualHeight ?? 0) + " px";
                    }
                    else
                    {
                        row.Ink = MeasureInk(copy, scale, 0, copy is ComboBox ? 0.7 : 0.9);
                        var tb = copy as TextBox;
                        int refRows = ReferenceRows(copy.FontFamily, copy.FontSize, scale, 240);
                        row.Problem = copy is ComboBox
                            ? (row.Ink.Any && row.Ink.Rows >= refRows - (int)Math.Ceiling(scale) && row.Ink.ClearBottom > (int)Math.Ceiling(scale) ? null : "dropdown text cut")
                            : JudgeInk(copy, row.Ink, refRows, scale, ContentHost(copy));
                    }
                }
                catch (Exception e) { row.Problem = "audit crashed: " + e.Message; }
                rows.Add(row);
            }
            return rows;
        }

        // all the copies on one picture, 100 % or 125 %
        void InputSheet(List<InputRow> rows, string file, double scale)
        {
            var list = rows.Where(r => r.Copy != null).ToList();
            double colW = 260, rowH = 0;
            foreach (var r in list) rowH = Math.Max(rowH, r.Copy.ActualHeight + 22);
            int cols = 3, n = list.Count, rowsN = (n + cols - 1) / cols;
            var panel = new Canvas { Width = cols * colW + 16, Height = rowsN * rowH + 16, Background = (Brush)FindResource("Brush.Surface1") };
            for (int i = 0; i < n; i++)
            {
                var r = list[i];
                var host = new Border { Width = colW - 12, Height = r.Copy.ActualHeight + 16 };
                var sp = new StackPanel();
                sp.Children.Add(new TextBlock { Text = r.Where + " / " + r.Name, FontSize = 11, Foreground = (Brush)FindResource("Brush.Text.Tertiary"), Margin = new Thickness(0, 0, 0, 2) });
                var c = r.Copy; c.Width = Math.Min(colW - 16, Math.Max(40, r.W)); sp.Children.Add(c);
                host.Child = sp;
                Canvas.SetLeft(host, 8 + (i % cols) * colW); Canvas.SetTop(host, 8 + (i / cols) * rowH);
                panel.Children.Add(host);
            }
            LayOut(panel, panel.Width, panel.Height);
            var dv = new DrawingVisual();
            int pw = (int)Math.Round(panel.Width * scale), ph = (int)Math.Round(panel.Height * scale);
            using (var dc = dv.RenderOpen())
            {
                dc.PushTransform(new ScaleTransform(scale, scale));
                dc.DrawRectangle(new VisualBrush(panel) { Viewbox = new Rect(0, 0, panel.Width, panel.Height), ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill }, null, new Rect(0, 0, panel.Width, panel.Height));
            }
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(dv);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using (var fs = File.Create(file)) enc.Save(fs);
        }

        async Task InputShots(string dir)
        {
            var s = settings;
            var saved = new { s.LodgeUrl, s.LodgeCodeProtected, s.LodgeTokenProtected, s.LodgeDeviceName, s.LodgeName };
            var sendWas = InviteExchange.Send;
            var report = new List<string>();
            bool ok = true;
            try
            {
                Width = 1100; Height = 900;
                Session.Disconnect();
                Session.NoNetworkForTest = true;
                Session.SentForTest = o => { };
                InviteForm.ClipboardSeam = t => { };
                InviteExchange.Send = req => Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
                s.LodgeUrl = "https://nasferatu.dk/lodge";
                LodgeCredentials.Clear(s);
                s.LodgeName = "Cleo"; s.LodgeDeviceName = "Cleo-PC";
                Session.IsShownToUser = () => false;
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                bool warmDone = false;
                foreach (var scale in new[] { 1.0, 1.0, 1.25 })   // the first pass only warms the pages up (lazy sections) and is thrown away
                {
                    bool warm = !warmDone; warmDone = true;
                    string tag = (int)(scale * 100) + "%";
                    var all = new List<InputRow>();
                    var audited = new HashSet<DependencyObject>();
                    async Task Visit(string where, Action setup, Func<DependencyObject> root = null)
                    {
                        try { setup?.Invoke(); } catch (Exception e) { report.Add("setup of " + where + " failed: " + e.Message); }
                        await Task.Delay(500);
                        UpdateLayout();
                        var found = AuditInputs(root != null ? root() : this, where, scale, audited);
                        all.AddRange(found);
                        if (!warm && scale == 1.0 && (where == "Settings profile" || where == "Settings guild master")) Snapshot(Path.Combine(dir, where == "Settings profile" ? "settings-profile.png" : "settings-guild-master.png"));
                        if (found.Count == 0) report.Add($"[{tag}] {where}: no text input visible");
                    }

                    ShowTab("addons");
                    await Visit("Addons page", null);
                    ShowTab("settings");
                    foreach (var sec in new[] { "general", "addons", "lodge", "profile", "privacy", "voice", "overlay", "about" })
                        await Visit("Settings " + sec, () => SettingsPage.Show(sec));
                    SettingsPage.GmDesignForTest();
                    Session.FeedForTest(Inv2Welcome("owner", true));
                    await Visit("Settings guild master", () => SettingsPage.Show("gm"));
                    ShowTab("inventory");
                    await Visit("Characters page", null);
                    ShowTab("browser");
                    await Visit("Browser page", null);
                    await Visit("Browser add-a-tile form", () => BrowserView.OpenAddFormForTest());

                    ShowTab("lodge");
                    LodgePage.SignOut();
                    await Visit("Lodge join", null, () => LodgePage);
                    await Visit("Lodge join (opened by a link)", () => OpenInviteLink("elanshub://lodge/join#join.example.test/lodge|" + InvA), () => LodgePage);
                    LodgePage.CancelEditForTest();
                    LodgePage.SignOut();
                    LodgePage.CreateLodgeClickForTest();
                    var g = LodgePage.GuideForTest; g.NoClipboardForTest = true;
                    g.NextForTest();
                    await Visit("Create-a-lodge guide, step 2", null, () => LodgePage);
                    g.NextForTest();
                    await Visit("Create-a-lodge guide, step 3", null, () => LodgePage);
                    g.Close();
                    Session.FeedForTest(Inv2Welcome("owner", true));
                    LodgePage.ShowChatForTest();
                    await Visit("Lodge chat", null, () => LodgePage);
                    await Visit("Lodge invite sheet", () => LodgePage.InviteSheetForTest.Open(), () => LodgePage);
                    LodgePage.InviteSheetForTest.Close();
                    await Visit("Lodge looking-for-group form", () => LodgePage.OpenLfgFormForTest(), () => LodgePage);
                    LodgePage.CloseLfgFormForTest();
                    await Visit("Lodge status popup", () => LodgePage.OpenStatusForTest(), () => LodgePage);

                    // inputs that only exist in a state the harness cannot reach (the Addons search box needs more than eight addons, the link box lives in an expanded
                    // card, the typed-name box in a dialog, the calendar fields in its forms): the same style, size and font drawn as a sample
                    foreach (var smp in new[] { ("Addons page (sample)", "SearchBox", 13.0, 32.0, 176.0), ("Addons card (sample)", "link box", 12.0, 32.0, 520.0), ("Dialog (sample)", "TypedBox", 13.0, 32.0, 400.0),
                                                ("Calendar form (sample)", "FTitle", 13.0, double.NaN, 400.0), ("Calendar form (sample)", "DetailComment", 13.0, double.NaN, 400.0) })
                    {
                        var tb = new TextBox { Style = (Style)FindResource("Input"), Text = InkSample, FontSize = smp.Item3, Height = smp.Item4 };
                        LayOut(tb, smp.Item5, smp.Item4);
                        var row = new InputRow { Where = smp.Item1, Name = smp.Item2, Kind = "TextBox", W = tb.ActualWidth, H = tb.ActualHeight, Copy = tb };
                        row.Ink = MeasureInk(tb, scale, 0, 0.9);
                        row.Problem = JudgeInk(tb, row.Ink, ReferenceRows(tb.FontFamily, tb.FontSize, scale, 240), scale, ContentHost(tb));
                        all.Add(row);
                    }
                    if (warm) { report.Clear(); continue; }
                    var problems = all.Where(r => r.Problem != null).ToList();
                    report.Add($"[{tag}] audited {all.Count} inputs, {problems.Count} problem(s)");
                    foreach (var r in all)
                        report.Add($"[{tag}] {(r.Problem == null ? "ok  " : "FAIL")} {r.Where} / {r.Name} ({r.Kind}, {r.W:0} x {r.H:0})" + (r.Ink != null && r.Ink.Any ? $" ink {r.Ink.Rows} px, gap above {r.Ink.ClearTop} below {r.Ink.ClearBottom}" : "") + (r.Problem != null ? "  -> " + r.Problem : ""));
                    ok &= problems.Count == 0 && all.Count >= 15;
                    InputSheet(all, Path.Combine(dir, "inputs-sheet-" + (int)(scale * 100) + ".png"), scale);
                }
            }
            catch (Exception e) { ok = false; report.Add("crashed: " + e); }
            finally
            {
                InviteExchange.Send = sendWas;
                InviteForm.ClipboardSeam = null;
                Session.SentForTest = null;
                Session.NoNetworkForTest = false;
                s.LodgeUrl = saved.LodgeUrl; s.LodgeCodeProtected = saved.LodgeCodeProtected; s.LodgeTokenProtected = saved.LodgeTokenProtected; s.LodgeDeviceName = saved.LodgeDeviceName; s.LodgeName = saved.LodgeName;
                Session.Disconnect();
            }
            File.WriteAllText(Path.Combine(dir, "inputs-audit.txt"), string.Join("\r\n", report));
            File.WriteAllText(Path.Combine(dir, "result.txt"), "inputs design shots: " + (ok ? "PASSED" : "FAILED") + "\r\n" + string.Join("\r\n", report));
            Quit();
        }
    }
}
