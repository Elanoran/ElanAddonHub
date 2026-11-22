using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Shapes;
using ElansAddonHub.Services.Atlas;

namespace ElansAddonHub
{
    // 3.10.3: the quick answers at the top of an entity page (docs: one layout for every page). One tile per answer, each with its evidence mark:
    // a dot in the source's colour and its name (the DataSources vocabulary the rows and the Data sources page use), or an empty ring for "not
    // known yet". Four across on a wide page, two on a narrow one.
    public partial class AtlasPage
    {
        UniformGrid answerGrid;

        FrameworkElement AnswerStrip(AtlasPageModel m)
        {
            answerGrid = null;
            if (m.Answers.Count == 0) return null;
            var g = new UniformGrid { Rows = 0, Columns = AnswerColumns(m.Answers.Count), Margin = new Thickness(-4, 0, -4, 8), Tag = "answers" };
            foreach (var a in m.Answers) g.Children.Add(AnswerTile(a));
            answerGrid = g;
            return g;
        }

        // 3.10.4: three answers (an object page) stay in one row down to 560 px instead of wrapping 2 + 1 with a gap
        int AnswerColumns(int n) => wide || Scroller.ActualWidth >= 760 || (n == 3 && Scroller.ActualWidth >= 560) ? n : System.Math.Min(n, 2);

        void LayoutAnswers() { if (answerGrid != null && model != null) answerGrid.Columns = AnswerColumns(model.Answers.Count); }

        FrameworkElement AnswerTile(AtlasAnswer a)
        {
            var sp = new StackPanel();
            sp.Children.Add(Tb(a.Label, "Text.Caption", Res("Brush.Text.Tertiary")));
            var v = Tb(a.Value, "Text.Body", a.Known ? null : Res("Brush.Text.Secondary"), 2); v.FontWeight = FontWeights.SemiBold; v.TextTrimming = TextTrimming.CharacterEllipsis; v.ToolTip = a.Value;
            sp.Children.Add(v);
            if (a.Detail.Length > 0) { var d = Tb(a.Detail, "Text.Caption", Res("Brush.Text.Secondary"), 2); d.TextWrapping = TextWrapping.Wrap; d.MaxHeight = 36; d.TextTrimming = TextTrimming.CharacterEllipsis; sp.Children.Add(d); }
            if (a.Known) sp.Children.Add(EvidenceMark(a.Source, 6));     // "Not known yet" is already the value: no second line saying it
            var b = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(4), Child = sp, Tag = "answer" };
            b.SetResourceReference(Border.BackgroundProperty, "Brush.Surface1");
            System.Windows.Automation.AutomationProperties.SetName(b, a.Label + ": " + a.Value + (a.Detail.Length > 0 ? ", " + a.Detail : "") + ". " + EvidenceWords(a.Source));
            if (!string.IsNullOrEmpty(a.Link))
            {
                var go = a.Link;
                b.Cursor = Cursors.Hand; b.ToolTip = "Open " + a.Value;
                b.MouseLeftButtonUp += (s, e) => { e.Handled = true; _ = Open(go); };
            }
            return b;
        }

        // the mark shared by the answers (and meant for every place that says where something comes from): a filled dot in the source's colour and
        // its name, or an empty ring and "not known yet"
        FrameworkElement EvidenceMark(string source, double top = 0)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, top, 0, 0), Tag = "evidence" };
            var dot = new Ellipse { Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 6, 0) };
            bool known = !string.IsNullOrEmpty(source);
            if (known) dot.SetResourceReference(Shape.FillProperty, source == AtlasAnswer.Notes ? "Brush.Text.Secondary" : DataSources.BrushKey(source));
            else { dot.StrokeThickness = 1; dot.SetResourceReference(Shape.StrokeProperty, "Brush.Text.Tertiary"); }
            row.Children.Add(dot);
            var t = Tb(EvidenceWords(source), "Text.Caption", Res("Brush.Text.Tertiary"));
            row.Children.Add(t);
            return row;
        }

        static string EvidenceWords(string source) => string.IsNullOrEmpty(source) ? "Not known yet" : source == DataSources.Classic ? "Classic baseline only" : source == AtlasAnswer.Notes ? ZoneLore.Label : DataSources.Title(source);
    }
}
