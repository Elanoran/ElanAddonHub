using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ElansAddonHub.Lodge
{
    // "Create a lodge" (the join screen's link): 1) what you need, 2) the install commands, 3) paste the owner link the installer printed.
    // Step 3 hands the link to the normal join (LodgeView.ShowInviteLink), where the person types the name they want.
    public partial class CreateLodgeGuide : UserControl
    {
        public const string ReadmeUrl = "https://github.com/Elanoran/ElanAddonHub/blob/main/server/deploy/README.md";
        // the two commands of server/deploy/README.md, "One-time install"
        public const string InstallCommands =
            "sudo git clone https://github.com/Elanoran/ElanAddonHub /opt/lodge-src\n" +
            "sudo /opt/lodge-src/server/deploy/install.sh";

        const int Steps = 3;
        readonly OverlayController overlay;
        int step = 1;

        // the page was opened / the link accepted: tests and the view listen
        public event Action<string> LinkAccepted;
        public Action<string> OpenUrl = url => { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { } };

        public CreateLodgeGuide()
        {
            InitializeComponent();
            overlay = new OverlayController(Overlay, Box);
            Commands.Text = InstallCommands;
            for (int i = 0; i < Steps; i++) Dots.Children.Add(new Border { Height = 8, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 8, 0) });
            ShowStep(1);
        }

        public bool IsOpen => overlay.IsOpen;
        public int StepForTest => step;
        public string CommandsForTest => Commands.Text;
        public string LinkProblemForTest => LinkProblem.Visibility == Visibility.Visible ? LinkProblem.Text : null;
        public string NextTextForTest => NextButton.Content as string;
        public bool BackVisibleForTest => BackButton.Visibility == Visibility.Visible;
        public string LinkBoxForTest { get => LinkBox.Text; set => LinkBox.Text = value; }
        public string CopiedForTest;   // what the Copy button put on the clipboard (the clipboard itself is not touched under test)
        public bool NoClipboardForTest;

        public void Open()
        {
            LinkBox.Text = "";
            LinkProblem.Visibility = Visibility.Collapsed;
            ShowStep(1);
            overlay.Open();
        }

        public void Close() => overlay.Close();

        void ShowStep(int n)
        {
            step = Math.Max(1, Math.Min(Steps, n));
            Step1.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
            Step2.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
            Step3.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;
            BackButton.Visibility = step > 1 ? Visibility.Visible : Visibility.Collapsed;
            NextButton.Content = step == Steps ? "Use this link" : "Next";
            for (int i = 0; i < Dots.Children.Count; i++)
            {
                var d = (Border)Dots.Children[i];
                bool current = i + 1 == step;
                d.Width = current ? 24 : 8;
                d.Background = (Brush)Application.Current.Resources[current ? "Brush.Accent" : i + 1 < step ? "Brush.Text.Secondary" : "Brush.Text.Disabled"];
            }
            if (step == Steps) LinkBox.Focus();
        }

        public void CopyCommandsForTest() => CopyCommands_Click(null, null);
        public string CopyNoteForTest => CopyNote.Text;
        public void ReadmeClickForTest() => Readme_Click(null, null);
        public void NextForTest() => Next_Click(null, null);
        public void BackForTest() => Back_Click(null, null);

        void Next_Click(object sender, RoutedEventArgs e)
        {
            if (step < Steps) { ShowStep(step + 1); return; }
            UseLink();
        }

        void Back_Click(object sender, RoutedEventArgs e) => ShowStep(step - 1);

        // the pasted owner link must be a real invite link with a real address (an elanshub:// link, or the web form)
        void UseLink()
        {
            var link = InviteLinks.Parse(LinkBox.Text);
            string problem = null;
            if (string.IsNullOrWhiteSpace(LinkBox.Text)) problem = "Paste the owner link the installer printed.";
            else if (link.MissingAddress) problem = "This link has no lodge address (it shows a placeholder). Set LODGE_PUBLIC_URL on the server and run: sudo lodge-admin claim-owner";
            else if (link.Insecure) problem = InviteLinks.InsecureText;
            else if (!link.IsInvite || link.Url == null) problem = "That does not look like the owner link. It starts with elanshub://lodge/join# and holds a code starting with i1.";
            if (problem != null) { LinkProblem.Text = problem; LinkProblem.Visibility = Visibility.Visible; return; }
            LinkProblem.Visibility = Visibility.Collapsed;
            var text = LinkBox.Text.Trim();
            LinkBox.Text = "";                  // the secret does not stay in the guide
            overlay.Close();
            LinkAccepted?.Invoke(text);
        }

        void Readme_Click(object sender, RoutedEventArgs e) => OpenUrl(ReadmeUrl);

        void CopyCommands_Click(object sender, RoutedEventArgs e)
        {
            CopiedForTest = InstallCommands;
            if (!NoClipboardForTest) { try { Clipboard.SetText(InstallCommands); } catch { } }
            CopyNote.Text = "Copied. Paste them into your server's terminal.";
        }

        void Close_Click(object sender, RoutedEventArgs e) => Close();
        void Scrim_Down(object sender, MouseButtonEventArgs e) { Close(); e.Handled = true; }
        void Box_Down(object sender, MouseButtonEventArgs e) { e.Handled = true; }
        void Guide_KeyDown(object sender, KeyEventArgs e)
        {
            if (!overlay.IsOpen) return;
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            else if (e.Key == Key.Enter && step == Steps && LinkBox.IsKeyboardFocused) { UseLink(); e.Handled = true; }
        }
    }
}
