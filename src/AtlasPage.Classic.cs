using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Atlas.Classic;

namespace ElansAddonHub
{
    // The "Classic baseline" card of the start page: not downloaded -> Get (asks with the exact size and the licence first) -> progress -> reading -> ready (dump date),
    // Remove, the credit line and a link to the licence file next to the data. Nothing is fetched without the user's click on the button and on the question.
    public partial class AtlasPage
    {
        public Func<string, bool> OpenFileOverrideForTest;     // the self-test must not start another program
        public Action<string> OpenLinkOverride;                // the licence page on the web (the main window sends it to the browser)
        Border classicCard;
        StackPanel classicBody;
        public StackPanel ClassicBodyForTest => classicBody;
        public Border ClassicCardForTest => classicCard;
        public Button ClassicPrimaryForTest { get; private set; }
        public Button ClassicCancelForTest { get; private set; }
        public Button ClassicRemoveForTest { get; private set; }

        public void UseClassicSetup(ClassicSetup s)
        {
            if (ClassicSetup != null) ClassicSetup.Changed -= ClassicChanged;
            ClassicSetup = s;
            ClassicSetup.Changed += ClassicChanged;
            ClassicSetup.Confirm = AskDownload;
        }

        void EnsureSetup()
        {
            if (ClassicSetup == null) UseClassicSetup(new ClassicSetup());
        }

        void ClassicChanged()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                FillClassic();
                // the layers that read the Classic baseline show their rows once it is ready: redraw the page that is open
                if (ClassicSetup.State == ClassicCardState.Ready || ClassicSetup.State == ClassicCardState.NotDownloaded) { if (!IsStart) _ = Redraw(); }
            }));
        }

        public Func<ClassicRemote, bool> ConfirmOverrideForTest;

        // called on the worker that queried GitHub: the question is asked on the UI thread
        bool AskDownload(ClassicRemote r)
        {
            if (ConfirmOverrideForTest != null) return ConfirmOverrideForTest(r);
            bool yes = false;
            Dispatcher.Invoke(new Action(() =>
            {
                var mb = (r.TotalSize / 1048576.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
                var text = "This downloads " + mb + " MB from GitHub (" + r.DumpName + " and " + r.Updates.Count + " update files, repository cmangos/classic-db).\n\n" + r.Notice;
                yes = Dialog.Confirm(Window.GetWindow(this), "Get the Classic baseline?", text, "Download");
            }));
            return yes;
        }

        FrameworkElement ClassicCardView()
        {
            EnsureSetup();
            classicBody = new StackPanel();
            classicCard = Card(classicBody);
            FillClassic();
            return classicCard;
        }

        string StateWord(ClassicCardState s)
        {
            switch (s)
            {
                case ClassicCardState.Ready: return "Ready";
                case ClassicCardState.Downloaded: return "Downloaded";
                case ClassicCardState.Downloading: return "Downloading";
                case ClassicCardState.Importing: return "Reading";
                case ClassicCardState.Asking: return "Asking";
                case ClassicCardState.Failed: return "Problem";
                default: return "Not downloaded";
            }
        }

        public void FillClassic()
        {
            if (classicBody == null || ClassicSetup == null) return;
            var s = ClassicSetup;
            classicBody.Children.Clear();
            ClassicPrimaryForTest = ClassicCancelForTest = ClassicRemoveForTest = null;

            var head = new DockPanel();
            var chip = new Border { Style = (Style)FindResource("Chip"), Padding = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            if (s.State == ClassicCardState.Ready) chip.SetResourceReference(Border.BackgroundProperty, "Brush.Success.Tint");
            else if (s.State == ClassicCardState.Failed) chip.SetResourceReference(Border.BackgroundProperty, "Brush.Danger.Tint");
            var ct = Tb(StateWord(s.State), "Text.Secondary", s.State == ClassicCardState.Ready ? Res("Brush.Success") : s.State == ClassicCardState.Failed ? Res("Brush.Danger") : null);
            ct.VerticalAlignment = VerticalAlignment.Center; chip.Child = ct;
            DockPanel.SetDock(chip, Dock.Right); head.Children.Add(chip);
            head.Children.Add(Tb("Classic baseline", "Text.Section"));
            classicBody.Children.Add(head);

            var d = Tb("Where things drop, who sells them and what quests give them, from the original game's public data. It is the original game, not WoW Forever: every row from it says \"" + ClassicLabel.Source + "\", and what your Lodge saw on Forever is listed above it.", "Text.Secondary", null, 4);
            d.TextWrapping = TextWrapping.Wrap; classicBody.Children.Add(d);

            switch (s.State)
            {
                case ClassicCardState.NotDownloaded:
                    {
                        var b = new Button { Content = ClassicSetup.ButtonText, Style = (Style)FindResource("Button.Primary.M"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
                        b.Click += (a, e) => { _ = s.StartAsync(); };
                        classicBody.Children.Add(b); ClassicPrimaryForTest = b;
                        var n = Tb("You will see the exact size and the licence before anything is downloaded.", "Text.Caption", Res("Brush.Text.Tertiary"), 8);
                        classicBody.Children.Add(n);
                        break;
                    }
                case ClassicCardState.Asking:
                case ClassicCardState.Downloading:
                case ClassicCardState.Importing:
                    {
                        var bar = new ProgressBar { Style = (Style)FindResource("ThinProgress"), Minimum = 0, Maximum = 100, Value = Math.Round(s.Fraction * 100), Margin = new Thickness(0, 12, 0, 0), IsIndeterminate = s.State == ClassicCardState.Asking };
                        classicBody.Children.Add(bar);
                        var ph = Tb((s.State == ClassicCardState.Importing ? "Reading · " : "") + s.Phase + (s.State == ClassicCardState.Asking ? "" : " · " + Math.Round(s.Fraction * 100) + " %"), "Text.Secondary", null, 8);
                        ph.TextTrimming = TextTrimming.CharacterEllipsis; classicBody.Children.Add(ph);
                        var c = new Button { Content = "Cancel", Style = (Style)FindResource("Button.M"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
                        c.Click += (a, e) => s.Cancel();
                        classicBody.Children.Add(c); ClassicCancelForTest = c;
                        break;
                    }
                case ClassicCardState.Downloaded:
                    {
                        classicBody.Children.Add(Tb("The data is on this PC but not read yet.", "Text.Body", null, 8));
                        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
                        var b = new Button { Content = "Read the data", Style = (Style)FindResource("Button.Primary.M") };
                        b.Click += (a, e) => { _ = s.StartAsync(); };
                        row.Children.Add(b); ClassicPrimaryForTest = b;
                        row.Children.Add(RemoveButton(s));
                        classicBody.Children.Add(row);
                        break;
                    }
                case ClassicCardState.Ready:
                    {
                        classicBody.Children.Add(Tb(s.ReadyText, "Text.Body", null, 8));
                        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
                        row.Children.Add(RemoveButton(s));
                        classicBody.Children.Add(row);
                        break;
                    }
                case ClassicCardState.Failed:
                    {
                        var e = Tb(s.Error.Length > 0 ? s.Error : "Something went wrong.", "Text.Body", Res("Brush.Danger"), 8); e.TextWrapping = TextWrapping.Wrap; classicBody.Children.Add(e);
                        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
                        var b = new Button { Content = "Try again", Style = (Style)FindResource("Button.Primary.M") };
                        b.Click += (a, ev) => { _ = s.StartAsync(); };
                        row.Children.Add(b); ClassicPrimaryForTest = b;
                        if (s.Backend.State() != ClassicState.NotDownloaded) row.Children.Add(RemoveButton(s));
                        classicBody.Children.Add(row);
                        break;
                    }
            }

            // credit and licence, always
            var credit = Tb(ClassicSetup.CreditLine, "Text.Caption", Res("Brush.Text.Tertiary"), 12);
            credit.TextWrapping = TextWrapping.Wrap; classicBody.Children.Add(credit);
            var links = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            var lic = s.LicensePath();
            if (lic != null) links.Children.Add(GhostButton("Licence (file on this PC)", () => OpenLocal(lic), lic));
            links.Children.Add(GhostButton("cMaNGOS classic-db on GitHub", () => OpenWeb(ClassicSetup.RepoUrl), ClassicSetup.RepoUrl));
            classicBody.Children.Add(links);
        }

        Button RemoveButton(ClassicSetup s)
        {
            var b = new Button { Content = "Remove from this PC", Style = (Style)FindResource("Button.M"), Margin = new Thickness(8, 0, 0, 0) };
            if (s.State == ClassicCardState.Ready || s.State == ClassicCardState.Failed || s.State == ClassicCardState.Downloaded) b.Margin = s.State == ClassicCardState.Ready ? new Thickness(0) : new Thickness(8, 0, 0, 0);
            b.Click += (a, e) => { _ = RemoveClassic(); };
            ClassicRemoveForTest = b;
            return b;
        }

        public Func<bool> ConfirmRemoveOverrideForTest;

        // asks first (danger confirm), then deletes the data and the index; the pages that used it are drawn again without it
        public Task RemoveClassic()
        {
            bool yes = ConfirmRemoveOverrideForTest != null ? ConfirmRemoveOverrideForTest()
                : Dialog.Confirm(Window.GetWindow(this), "Remove the Classic baseline?", "This deletes the downloaded data and its index from this PC. Atlas keeps working without it, and you can download it again later.", "Remove", true);
            if (!yes) return Task.CompletedTask;
            ClassicSetup.Delete();
            return Task.CompletedTask;
        }

        void OpenLocal(string path)
        {
            if (OpenFileOverrideForTest != null) { OpenFileOverrideForTest(path); return; }
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception e) { Util.Log("atlas: open licence: " + e.Message); }
        }

        void OpenWeb(string url)
        {
            if (OpenLinkOverride != null) { OpenLinkOverride(url); return; }
            WebEngine.OpenExternal(url);
        }
    }
}
