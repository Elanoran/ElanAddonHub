using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using ElansAddonHub.Services.Atlas;

namespace ElansAddonHub
{
    // "Where this comes from" (2.91): the plain explainer of the Atlas's three kinds of data (your game files / seen on Forever by players /
    // the Classic baseline) with how to help, at the top of the Data sources page and as a calm card on Atlas Home that is dismissed once
    // (the main window remembers it: settings.AtlasSourcesCardDismissed, MainWindow.DataHonesty.cs). The texts are DataHonesty's.
    // Self-test section "datahonesty".
    public partial class AtlasPage
    {
        public Func<bool> SourcesCardDismissed = () => false;     // the main window: was the Home card dismissed already?
        public Action DismissSourcesCard;                         // the main window: remember that it was

        public const string ExplainerTag = "sources-explainer";
        Border sourcesCard;
        public bool SourcesCardShownForTest => sourcesCard != null && sourcesCard.Visibility == Visibility.Visible && sourcesCard.Child != null && Body.Children.Count > 0 && IsInBody(sourcesCard);
        public Button SourcesCardDismissForTest { get; private set; }
        public Button SourcesCardMoreForTest { get; private set; }
        public IReadOnlyList<string> ExplainerTextsForTest => AtlasWalk(Body).OfType<FrameworkElement>().Where(f => Equals(f.Tag, ExplainerTag)).SelectMany(f => AtlasWalk(f).OfType<TextBlock>()).Select(t => t.Inlines.Count > 0 ? string.Concat(t.Inlines.OfType<Run>().Select(r => r.Text)) : t.Text).ToList();

        bool IsInBody(DependencyObject d)
        {
            for (var p = d; p != null; p = System.Windows.Media.VisualTreeHelper.GetParent(p)) if (ReferenceEquals(p, Body)) return true;
            return false;
        }

        // the three kinds and how to help; the same words on both pages
        StackPanel ExplainerBody()
        {
            var sp = new StackPanel { Tag = ExplainerTag };
            sp.Children.Add(Tb(DataHonesty.Title, "Text.Section"));
            void Kind(string lead, string text, double top)
            {
                var t = new TextBlock { Style = (Style)FindResource("Text.Secondary"), TextWrapping = TextWrapping.Wrap, MaxWidth = ReadWidth, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, top, 0, 0) };
                t.Inlines.Add(new Run(lead + ". ") { FontWeight = FontWeights.SemiBold, Foreground = Res("Brush.Text.Primary") });
                t.Inlines.Add(new Run(text));
                sp.Children.Add(t);
            }
            Kind(DataHonesty.GameLead, DataHonesty.GameText, 8);
            Kind(DataHonesty.ForeverLead, DataHonesty.ForeverText, 6);
            Kind(DataHonesty.ClassicLead, DataHonesty.ClassicText, 6);
            var help = Tb(DataHonesty.HelpText, "Text.Secondary", Res("Brush.Text.Primary"), 10); help.TextWrapping = TextWrapping.Wrap; help.MaxWidth = ReadWidth; help.HorizontalAlignment = HorizontalAlignment.Left;
            sp.Children.Add(help);
            return sp;
        }

        // the Data sources page: first, before the cards of each source
        FrameworkElement SourcesExplainerCard()
        {
            var c = Card(ExplainerBody());
            System.Windows.Automation.AutomationProperties.SetName(c, DataHonesty.Title);
            return c;
        }

        // Atlas Home: the same words with "Data sources" and "Got it"; once dismissed it stays away
        FrameworkElement SourcesHomeCard()
        {
            sourcesCard = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(0, 0, 0, 24) };
            sourcesCard.SetResourceReference(Border.BackgroundProperty, "Brush.Surface1");
            System.Windows.Automation.AutomationProperties.SetName(sourcesCard, DataHonesty.Title);
            FillSourcesHomeCard();
            return sourcesCard;
        }

        void FillSourcesHomeCard()
        {
            if (sourcesCard == null) return;
            SourcesCardDismissForTest = SourcesCardMoreForTest = null;
            bool gone = false;
            try { gone = SourcesCardDismissed?.Invoke() == true; } catch { }
            sourcesCard.Visibility = gone ? Visibility.Collapsed : Visibility.Visible;
            if (gone) { sourcesCard.Child = null; return; }
            var sp = ExplainerBody();
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            var more = new Button { Content = DataHonesty.MoreLink, Style = (Style)FindResource("Button.M"), MinWidth = 96 };
            var ok = new Button { Content = DataHonesty.DismissText, Style = (Style)FindResource("GhostButton"), Height = 32, Margin = new Thickness(8, 0, 0, 0) };
            more.Click += (a, e) => { _ = Open(AtlasNav.DataId); };
            ok.Click += (a, e) => { DismissSourcesCard?.Invoke(); FillSourcesHomeCard(); };
            row.Children.Add(more); row.Children.Add(ok);
            SourcesCardMoreForTest = more; SourcesCardDismissForTest = ok;
            sp.Children.Add(row);
            sourcesCard.Child = sp;
        }
    }
}
