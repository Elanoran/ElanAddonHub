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
    // Look (DESIGN 4 / 8): the page dims behind Brush.Scrim, the guide is an overlay card (Surface3, hairline, elevation 2, radius 14,
    // padding 24); steps are Surface2 cards (radius 12, 16 padding, 12 apart); one primary button (Next / Get started); the card opens
    // with Motion.DialogIn and every step fades + slides in with Motion.PageIn.
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
        readonly TextBlock title = new TextBlock { TextWrapping = TextWrapping.Wrap };
        readonly TextBlock sub = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 16) };
        readonly Button back, next, skip;
        readonly TextBox linkBox;
        readonly TextBlock linkError = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        List<GuideAddon> addons = new List<GuideAddon>();

        public int Step { get; private set; }
        public bool Done { get; private set; }
        public string LinkText { get => linkBox.Text; set => linkBox.Text = value; }
        public string ErrorText => linkError.Text;
        public IEnumerable<string> TickedIds => addons.Where(a => a.Checked && !a.Installed).Select(a => a.Id);
        public FrameworkElement CardForTest => card;

        static Brush Res(string key) => (Brush)Application.Current.Resources[key];
        static Style Sty(string key) => (Style)Application.Current.FindResource(key);
        static CornerRadius Rad(string key) => (CornerRadius)Application.Current.Resources[key];
        static Thickness Pad(string key) => (Thickness)Application.Current.Resources[key];
        static TextBlock Txt(string text, string style, Brush color = null, Thickness? margin = null)
        {
            var t = new TextBlock { Text = text, Style = Sty(style), TextWrapping = TextWrapping.Wrap };
            if (color != null) t.Foreground = color;
            if (margin != null) t.Margin = margin.Value;
            return t;
        }

        public WelcomeGuide(Host host)
        {
            this.host = host;
            Background = Res("Brush.Scrim");
            CornerRadius = Rad("Radius.Window");
            UseLayoutRounding = true;

            card.Background = Res("Brush.Surface3"); card.BorderBrush = Res("Brush.Divider"); card.BorderThickness = new Thickness(1); card.CornerRadius = Rad("Radius.Window");
            card.Effect = (Effect)Application.Current.Resources["Elevation.2"];
            card.Width = 560; card.MaxHeight = 640; card.Padding = new Thickness(24);
            card.HorizontalAlignment = HorizontalAlignment.Center; card.VerticalAlignment = VerticalAlignment.Center;
            card.Margin = new Thickness(24);
            Child = card;
            Loaded += (s, e) => Motion.DialogIn(card);

            title.Style = Sty("Text.Title");
            sub.Style = Sty("Text.Body"); sub.Foreground = Res("Brush.Text.Secondary");
            linkError.Style = Sty("Text.Caption"); linkError.Foreground = Res("Brush.Danger");
            linkBox = new TextBox { Style = Sty("Input"), Tag = "https://example.com/lodge#invite=...", MaxLength = 400 };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            card.Child = grid;

            var head = new StackPanel();
            head.Children.Add(dots);
            dots.Margin = new Thickness(0, 0, 0, 16);
            System.Windows.Automation.AutomationProperties.SetName(dots, "Progress");
            head.Children.Add(title); head.Children.Add(sub);
            grid.Children.Add(head);

            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body };
            Grid.SetRow(scroll, 1);
            grid.Children.Add(scroll);

            var foot = new DockPanel { Margin = new Thickness(0, 24, 0, 0), LastChildFill = false };
            // buttons: Button.M (32); the one primary is Next / Get started
            Button Btn(string text, string style, double minW) => new Button { Content = text, Style = Sty(style), MinWidth = minW };
            next = Btn("Next", "Button.Primary.M", 96); next.Click += (s, e) => Next();
            back = Btn("Back", "Button.M", 72); back.Margin = new Thickness(0, 0, 8, 0); back.Click += (s, e) => Back();
            skip = Btn("Skip the guide", "GhostButton", 0); skip.Height = 32; skip.ToolTip = "Close this. You can open it again in Settings > About."; skip.Click += (s, e) => Skip();
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
            for (int i = 0; i < Steps; i++)   // progress: the current step is an accent pill (selection), done = Secondary, to come = Disabled
                dots.Children.Add(new Border
                {
                    Width = i == Step ? 24 : 8, Height = 8, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 8, 0),
                    Background = i == Step ? Res("Brush.Accent") : i < Step ? Res("Brush.Text.Secondary") : Res("Brush.Text.Disabled"),
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
            Motion.PageIn(body);
        }

        // ---- 1: the WoW folder
        void BuildFolder()
        {
            title.Text = "Welcome to " + Brand.Name;
            sub.Text = "A few quick steps and you're set. First: where is World of Warcraft?";
            var root = host.WowRoot?.Invoke();
            var box = new Border { Background = Res("Brush.Surface2"), CornerRadius = Rad("Radius.Card"), Padding = Pad("Pad.Card") };
            var sp = new StackPanel();
            sp.Children.Add(Txt(root != null ? "Found your game" : "Couldn't find the game on its own", "Text.RowTitle", root != null ? Res("Brush.Success") : Res("Brush.Warn")));
            if (root != null) sp.Children.Add(Txt(root, "Text.Mono", null, new Thickness(0, 4, 0, 0)));
            else sp.Children.Add(Txt("Pick any Wow .exe inside your World of Warcraft folder.", "Text.Secondary", null, new Thickness(0, 4, 0, 0)));
            box.Child = sp;
            body.Children.Add(box);
            var change = new Button { Content = root != null ? "That's not it - change" : "Choose the folder...", Style = Sty("Button.M"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
            change.Click += (s, e) => { host.PickFolder?.Invoke(); Show(0); };
            body.Children.Add(change);
            body.Children.Add(Txt("The Outpost only reads and writes inside Interface\\AddOns of this folder (installing and updating our addons), and reads saved variables. Nothing else.", "Text.Caption", null, new Thickness(0, 16, 0, 0)));
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
                body.Children.Add(Txt("The update server couldn't be reached, so the list of addons isn't available right now. Carry on - the Addons page will offer them as soon as the Outpost is online.", "Text.Secondary"));
                return;
            }
            foreach (var a in addons.OrderByDescending(x => x.Required).ThenBy(x => x.Name))
            {
                var row = new Border { Background = Res("Brush.Surface2"), CornerRadius = Rad("Radius.Card"), Padding = Pad("Pad.Row"), Margin = new Thickness(0, 0, 0, 12) };
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var texts = new StackPanel { Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
                var line = new StackPanel { Orientation = Orientation.Horizontal };
                line.Children.Add(new TextBlock { Text = a.Name, Style = Sty("Text.RowTitle"), VerticalAlignment = VerticalAlignment.Center });
                var tag = a.Required ? "Required" : a.Installed ? "Installed" : null;
                if (tag != null)   // chip: 24 high, full round, quiet veil
                    line.Children.Add(new Border { Height = 24, CornerRadius = Rad("Radius.Chip"), Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(8, 0, 0, 0), Background = Res("Brush.Overlay.Chip"), VerticalAlignment = VerticalAlignment.Center,
                        Child = new TextBlock { Text = tag, Style = Sty("Text.Caption"), VerticalAlignment = VerticalAlignment.Center } });
                texts.Children.Add(line);
                if (!string.IsNullOrWhiteSpace(a.Description))
                    texts.Children.Add(new TextBlock { Text = a.Description, Style = Sty("Text.Secondary"), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 32, Margin = new Thickness(0, 2, 0, 0) });
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
            body.Children.Add(Txt("Invite link", "Text.Caption", null, new Thickness(0, 0, 0, 4)));
            body.Children.Add(linkBox);
            linkBox.TextChanged -= LinkChanged; linkBox.TextChanged += LinkChanged;
            body.Children.Add(linkError);
            body.Children.Add(Txt("The invite code inside the link is stored encrypted for this Windows user, never as plain text.", "Text.Caption", null, new Thickness(0, 12, 0, 0)));
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
            // one Surface2 card, four rows split by hairlines; icons Tertiary (accent is for action / selection only)
            var list = new StackPanel();
            body.Children.Add(new Border { Background = Res("Brush.Surface2"), CornerRadius = Rad("Radius.Card"), Padding = new Thickness(16, 0, 16, 0), Child = list });
            void Tip(string glyph, string head, string text)
            {
                var g = new Grid { Margin = new Thickness(0, 12, 0, 12) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.Children.Add(new TextBlock { Text = glyph, FontFamily = (FontFamily)Application.Current.Resources["Icons"], FontSize = 16, Foreground = Res("Brush.Text.Tertiary"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0) });
                var sp = new StackPanel();
                sp.Children.Add(Txt(head, "Text.RowTitle"));
                sp.Children.Add(Txt(text, "Text.Secondary", null, new Thickness(0, 2, 0, 0)));
                Grid.SetColumn(sp, 1);
                g.Children.Add(sp);
                list.Children.Add(list.Children.Count == 0 ? (UIElement)g : new Border { BorderBrush = Res("Brush.Divider"), BorderThickness = new Thickness(0, 1, 0, 0), Child = g });
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
