using System;
using System.Windows;
using System.Windows.Controls;

namespace ElansAddonHub
{
    // The panel of the Addons page lists: as many columns as fit the width (each at least MinColumnWidth wide, at most MaxColumns), the cards
    // placed row by row in the order of the list. Used by the real cards (ItemsPanel) and by the skeleton cards, so both sit at the same
    // places. The rows are as tall as their tallest card; every card is top-aligned at its OWN height (never stretched to the row), so opening
    // one card's details (or the animation closing them again) only makes its row taller - its neighbours keep their size and place.
    // (CardGrid.Open is still set by the item containers; the layout no longer needs it.)
    // Each card brings its own bottom margin (Card.Raised: Gap.Card); the gap between the columns is Gap.
    public class CardGrid : Panel
    {
        public const double DefaultMinColumnWidth = 420, DefaultGap = 12;
        public const int DefaultMaxColumns = 3;

        public static readonly DependencyProperty MinColumnWidthProperty = DependencyProperty.Register(nameof(MinColumnWidth), typeof(double), typeof(CardGrid),
            new FrameworkPropertyMetadata(DefaultMinColumnWidth, FrameworkPropertyMetadataOptions.AffectsMeasure));
        public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(nameof(MaxColumns), typeof(int), typeof(CardGrid),
            new FrameworkPropertyMetadata(DefaultMaxColumns, FrameworkPropertyMetadataOptions.AffectsMeasure));
        public static readonly DependencyProperty GapProperty = DependencyProperty.Register(nameof(Gap), typeof(double), typeof(CardGrid),
            new FrameworkPropertyMetadata(DefaultGap, FrameworkPropertyMetadataOptions.AffectsMeasure));
        // set on the page (inherited by every grid in it): List view = one column
        public static readonly DependencyProperty ListModeProperty = DependencyProperty.RegisterAttached("ListMode", typeof(bool), typeof(CardGrid),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure));
        public static bool GetListMode(DependencyObject d) => (bool)d.GetValue(ListModeProperty);
        public static void SetListMode(DependencyObject d, bool v) => d.SetValue(ListModeProperty, v);
        // set on a card (through the item container style) while it shows its details
        public static readonly DependencyProperty OpenProperty = DependencyProperty.RegisterAttached("Open", typeof(bool), typeof(CardGrid),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentArrange));
        public static bool GetOpen(DependencyObject d) => (bool)d.GetValue(OpenProperty);
        public static void SetOpen(DependencyObject d, bool v) => d.SetValue(OpenProperty, v);

        public double MinColumnWidth { get => (double)GetValue(MinColumnWidthProperty); set => SetValue(MinColumnWidthProperty, value); }
        public int MaxColumns { get => (int)GetValue(MaxColumnsProperty); set => SetValue(MaxColumnsProperty, value); }
        public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }

        // how many columns the last layout used (the self-test reads it)
        public int Columns { get; private set; } = 1;

        public static int ColumnsFor(double width, double minColumn, int maxColumns, double gap)
        {
            if (double.IsInfinity(width) || double.IsNaN(width) || width <= 0) return 1;
            return Math.Max(1, Math.Min(Math.Max(1, maxColumns), (int)Math.Floor((width + gap) / (minColumn + gap))));
        }

        double colWidth;
        double[] rowHeights = new double[0];

        protected override Size MeasureOverride(Size available)
        {
            double w = available.Width;
            if (double.IsInfinity(w)) w = MinColumnWidth;
            int cols = GetListMode(this) ? 1 : ColumnsFor(w, MinColumnWidth, MaxColumns, Gap);
            Columns = cols;
            colWidth = Math.Max(0, (w - Gap * (cols - 1)) / cols);
            int n = InternalChildren.Count, rows = (n + cols - 1) / cols;
            rowHeights = new double[rows];
            for (int i = 0; i < n; i++)
            {
                var c = InternalChildren[i];
                c.Measure(new Size(colWidth, double.PositiveInfinity));
                rowHeights[i / cols] = Math.Max(rowHeights[i / cols], c.DesiredSize.Height);
            }
            double h = 0;
            foreach (var r in rowHeights) h += r;
            return new Size(w, h);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            int cols = Columns, n = InternalChildren.Count;
            double y = 0;
            for (int row = 0; row * cols < n; row++)
            {
                double rh = row < rowHeights.Length ? rowHeights[row] : 0;
                for (int i = row * cols; i < Math.Min(n, row * cols + cols); i++)
                {
                    var c = InternalChildren[i];
                    double x = (i - row * cols) * (colWidth + Gap);
                    c.Arrange(new Rect(x, y, colWidth, c.DesiredSize.Height));      // top-aligned at its own height, never stretched to the row
                }
                y += rh;
            }
            return finalSize;
        }
    }
}
