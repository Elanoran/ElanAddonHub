using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    public class GuideAddon
    {
        public string Id, Name, Description;
        public bool Required, Installed, Checked;
    }

    // First-run guide: a short overlay in the main window. 1 WoW folder, 2 addons, 3 join a Lodge (optional), 4 tips.
    // Skippable at any step, re-openable from Settings > About. Built in code; the host (MainWindow) supplies what it needs as callbacks.
    public class WelcomeGuide : Border
    {
        public const int Steps = 4;

        public class Host
        {
            public Func<string> WowRoot;                         // the detected folder (null = not found)
            public Action PickFolder;                            // opens the folder picker; the guide re-reads WowRoot afterwards
            public Func<List<GuideAddon>> Addons;                // what can be installed (empty when the update server wasn't reachable)
            public Action<List<string>> Install;                 // ids of the ticked addons that are not installed yet
            public Action<string> Join;                          // an invite link (already validated)
            public Action<bool> Finished;                        // true = done, false = skipped
        }

        readonly Host host;
        readonly Border card = new Border();
        readonly StackPanel body = new StackPanel();
        readonly StackPanel dots = new StackPanel { Orientation = Orientation.Horizontal };
        readonly TextBlock title = new TextBlock { FontSize = 20, FontWeight = FontWeights.SemiBold };
        readonly TextBlock sub = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 14) };
        readonly Button back, next, skip;
        readonly TextBox linkBox;
        readonly TextBlock linkError = new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 6, 0, 0) };
        List<GuideAddon> addons = new List<GuideAddon>();

        public int Step { get; private set; }
        public bool Done { get; private set; }
        public string LinkText { get => linkBox.Text; set => linkBox.Text = value; }
        public string ErrorText => linkError.Text;
        public IEnumerable<string> TickedIds => addons.Where(a => a.Checked && !a.Installed).Select(a => a.Id);
        public FrameworkElement CardForTest => card;

        static Brush Res(string key) => (Brush)Application.Current.Resources[key];
        static Style Sty(string key) => (Style)Application.Current.FindResource(key);

        public WelcomeGuide(Host host)
        {
            this.host = host;
            Background = new SolidColorBrush(Color.FromArgb(0xD0, 0x0B, 0x0D, 0x0F));
            CornerRadius = new CornerRadius(13);
            UseLayoutRounding = true;

            card.Background = Res("Surface"); card.BorderBrush = Res("Line"); card.BorderThickness = new Thickness(1); card.CornerRadius = new CornerRadius(14);
            card.Effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 6, Direction = 270, Opacity = 0.6 };
            card.Width = 560; card.MaxHeight = 560; card.Padding = new Thickness(28, 24, 28, 20);
            card.HorizontalAlignment = HorizontalAlignment.Center; card.VerticalAlignment = VerticalAlignment.Center;
            card.Margin = new Thickness(20);
            Child = card;

            title.Foreground = Res("Text"); sub.Foreground = Res("TextDim");
            linkError.Foreground = Res("Danger");
            linkBox = new TextBox { Style = Sty("Input"), Tag = "https://example.com/lodge#invite=...", MaxLength = 400 };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            card.Child = grid;

            var head = new StackPanel();
            head.Children.Add(dots);
            dots.Margin = new Thickness(0, 0, 0, 14);
            head.Children.Add(title); head.Children.Add(sub);
            grid.Children.Add(head);

            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body, Padding = new Thickness(0, 0, 6, 0) };
            Grid.SetRow(scroll, 1);
            grid.Children.Add(scroll);

            var foot = new DockPanel { Margin = new Thickness(0, 16, 0, 0), LastChildFill = false };
            Button Btn(string text, string style, double minW)
            {
                var b = new Button { Content = text, Style = Sty(style), Height = 36, Padding = new Thickness(18, 0, 18, 0), FontSize = 13, MinWidth = minW };
                return b;
            }
            next = Btn("Next", "PrimaryButton", 110); next.Click += (s, e) => Next();
            back = Btn("Back", "SecondaryButton", 80); back.Margin = new Thickness(0, 0, 8, 0); back.Click += (s, e) => Back();
            skip = Btn("Skip the guide", "GhostButton", 0); skip.ToolTip = "Close this. You can open it again in Settings > About."; skip.Click += (s, e) => Skip();
            DockPanel.SetDock(next, Dock.Right); DockPanel.SetDock(back, Dock.Right); DockPanel.SetDock(skip, Dock.Left);
            foot.Children.Add(next); foot.Children.Add(back); foot.Children.Add(skip);
            Grid.SetRow(foot, 2);
            grid.Children.Add(foot);

            Show(0);
        }

        public void Show(int step)
        {
            Step = Math.Max(0, Math.Min(Steps - 1, step));
            dots.Children.Clear();
            for (int i = 0; i < Steps; i++)
                dots.Children.Add(new Border
                {
                    Width = i == Step ? 26 : 8, Height = 6, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 6, 0),
                    Background = i == Step ? Res("Accent") : i < Step ? Res("AccentDark") : Res("Line"),
                });
            body.Children.Clear();
            back.Visibility = Step == 0 ? Visibility.Collapsed : Visibility.Visible;
            next.Content = Step == Steps - 1 ? "Get started" : Step == 2 && string.IsNullOrWhiteSpace(linkBox.Text) ? "Skip this step" : "Next";
            skip.Visibility = Step == Steps - 1 ? Visibility.Collapsed : Visibility.Visible;
            switch (Step)
            {
                case 0: BuildFolder(); break;
                case 1: BuildAddons(); break;
                case 2: BuildLodge(); break;
                default: BuildDone(); break;
            }
        }

        // ---- 1: the WoW folder
        void BuildFolder()
        {
            title.Text = "Welcome to " + Brand.Name;
            sub.Text = "A few quick steps and you're set. First: where is World of Warcraft?";
            var root = host.WowRoot?.Invoke();
            var box = new Border { Background = Res("SurfaceHi"), BorderBrush = Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), Padding = new Thickness(14, 12, 14, 12) };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = root != null ? "Found your game" : "Couldn't find the game on its own", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = root != null ? Res("Accent") : Res("Gold") });
            sp.Children.Add(new TextBlock { Text = root ?? "Pick any Wow .exe inside your World of Warcraft folder.", FontSize = 12.5, Foreground = Res("Text"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
            box.Child = sp;
            body.Children.Add(box);
            var change = new Button { Content = root != null ? "That's not it - change" : "Choose the folder...", Style = Sty("SecondaryButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0), Height = 32, Padding = new Thickness(16, 0, 16, 0) };
            change.Click += (s, e) => { host.PickFolder?.Invoke(); Show(0); };
            body.Children.Add(change);
            body.Children.Add(new TextBlock { Text = "The Outpost only reads and writes inside Interface\\AddOns of this folder (installing and updating our addons), and reads saved variables. Nothing else.", FontSize = 11.5, Foreground = Res("TextDim"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 0) });
        }

        // ---- 2: addons
        void BuildAddons()
        {
            title.Text = "Pick your addons";
            sub.Text = "Tick what you want installed. You can always add or remove addons later on the Addons page.";
            var fresh = host.Addons?.Invoke() ?? new List<GuideAddon>();
            foreach (var f in fresh) { var old = addons.FirstOrDefault(a => a.Id == f.Id); if (old != null && !f.Required && !f.Installed) f.Checked = old.Checked; }
            addons = fresh;
            if (addons.Count == 0)
            {
                body.Children.Add(new TextBlock { Text = "The update server couldn't be reached, so the list of addons isn't available right now. Carry on - the Addons page will offer them as soon as the Outpost is online.", FontSize = 12.5, Foreground = Res("TextDim"), TextWrapping = TextWrapping.Wrap });
                return;
            }
            foreach (var a in addons.OrderByDescending(x => x.Required).ThenBy(x => x.Name))
            {
                var row = new Border { Background = Res("SurfaceHi"), BorderBrush = Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), Padding = new Thickness(14, 10, 12, 10), Margin = new Thickness(0, 0, 0, 8) };
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var texts = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
                var line = new StackPanel { Orientation = Orientation.Horizontal };
                line.Children.Add(new TextBlock { Text = a.Name, FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Res("Text") });
                var tag = a.Required ? "Required" : a.Installed ? "Installed" : null;
                if (tag != null)
                    line.Children.Add(new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(7, 1, 7, 2), Margin = new Thickness(8, 1, 0, 0), Background = Res("Card"), VerticalAlignment = VerticalAlignment.Center,
                        Child = new TextBlock { Text = tag, FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = Res("TextDim") } });
                texts.Children.Add(line);
                if (!string.IsNullOrWhiteSpace(a.Description))
                    texts.Children.Add(new TextBlock { Text = a.Description, FontSize = 11.5, Foreground = Res("TextDim"), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 34, Margin = new Thickness(0, 2, 0, 0) });
                g.Children.Add(texts);
                var cb = new CheckBox { Style = Sty("Switch"), IsChecked = a.Checked || a.Required || a.Installed, IsEnabled = !a.Required && !a.Installed, VerticalAlignment = VerticalAlignment.Center };
                if (a.Required || a.Installed) a.Checked = true;
                cb.ToolTip = a.Required ? "Needed for the other addons to talk to the Outpost" : a.Installed ? "Already installed" : null;
                var item = a;
                cb.Click += (s, e) => item.Checked = cb.IsChecked == true;
                Grid.SetColumn(cb, 1);
                g.Children.Add(cb);
                row.Child = g;
                body.Children.Add(row);
            }
        }

        // ---- 3: join a Lodge
        void BuildLodge()
        {
            title.Text = "Join a Lodge";
            sub.Text = "A Lodge is a private chat, voice and files place for you and your friends. Got an invite link? Paste it here - or skip, you can do it any time from the Lodge tab.";
            body.Children.Add(new TextBlock { Text = "INVITE LINK", Style = Sty("Caption"), Margin = new Thickness(0, 0, 0, 5) });
            body.Children.Add(linkBox);
            linkBox.TextChanged -= LinkChanged; linkBox.TextChanged += LinkChanged;
            body.Children.Add(linkError);
            body.Children.Add(new TextBlock { Text = "The invite code inside the link is stored encrypted for this Windows user, never as plain text.", FontSize = 11.5, Foreground = Res("TextDim"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) });
        }

        void LinkChanged(object sender, TextChangedEventArgs e)
        {
            linkError.Text = "";
            next.Content = string.IsNullOrWhiteSpace(linkBox.Text) ? "Skip this step" : "Join and continue";
        }

        // ---- 4: done
        void BuildDone()
        {
            title.Text = "You're all set";
            sub.Text = "A few things worth knowing:";
            void Tip(string glyph, string head, string text)
            {
                var g = new Grid { Margin = new Thickness(0, 0, 0, 12) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.Children.Add(new TextBlock { Text = glyph, FontFamily = (FontFamily)Application.Current.Resources["Icons"], FontSize = 18, Foreground = Res("Accent"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0) });
                var sp = new StackPanel();
                sp.Children.Add(new TextBlock { Text = head, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Res("Text") });
                sp.Children.Add(new TextBlock { Text = text, FontSize = 12, Foreground = Res("TextDim"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 0) });
                Grid.SetColumn(sp, 1);
                g.Children.Add(sp);
                body.Children.Add(g);
            }
            Tip("", "It lives in the tray", "Closing the window keeps the Outpost running quietly next to the clock: updates are checked and you stay online in your Lodge. Right-click the icon to quit.");
            Tip("", "Quick keys", "Ctrl+1 Addons, Ctrl+2 Lodge, Ctrl+3 Inventory, Ctrl+, Settings.");
            Tip("", "You're in control", "Settings > Privacy lists everything that is shared and lets you switch it off.");
            Tip("", "Open this guide again", "Settings > About > Show welcome guide.");
        }

        // ---- navigation
        public void Back() { if (Step > 0) Show(Step - 1); }

        public void Next()
        {
            if (Step == 2 && !string.IsNullOrWhiteSpace(linkBox.Text))
            {
                var url = LodgeSession.ParseInvite(linkBox.Text, out var code);
                if (!Uri.TryCreate(url ?? "", UriKind.Absolute, out var u) || (u.Scheme != "https" && u.Scheme != "http") || string.IsNullOrEmpty(code) || code.Length < 6)
                {
                    linkError.Text = "That doesn't look like an invite link. It should look like https://...#invite=... - paste the whole link, or skip this step.";
                    return;
                }
            }
            if (Step < Steps - 1) { Show(Step + 1); return; }
            Finish();
        }

        public void Finish()
        {
            if (Done) return;
            Done = true;
            var ids = TickedIds.ToList();
            if (ids.Count > 0) host.Install?.Invoke(ids);
            if (!string.IsNullOrWhiteSpace(linkBox.Text)) host.Join?.Invoke(linkBox.Text.Trim());
            host.Finished?.Invoke(true);
        }

        public void Skip()
        {
            if (Done) return;
            Done = true;
            host.Finished?.Invoke(false);
        }
    }
}
