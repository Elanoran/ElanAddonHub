using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using ElansAddonHub.Services.Atlas;

namespace ElansAddonHub
{
    // A page whose top stays put (2.81: the class and race pages). The breadcrumb row is outside the page's Scroller anyway; here the painted hero and the
    // tab strip stay too, and only the tab's content (with the rail) scrolls, in a scroller of its own under them - like the Spells list under its filters.
    //   - the page's Scroller does not scroll at all on such a page (VerticalScrollBarVisibility Disabled): it never shows a bar, not even for a frame
    //   - its one child is a grid exactly as tall as the Scroller's viewport (a binding, so it is right in the same layout pass, before the first frame):
    //     the head (hero + tabs) in an Auto row, the content scroller in the star row (overlay bar, fading edges like the page's own)
    //   - the mouse wheel over the head and the page keys (Page Up / Down, Home / End, the arrows when nothing in the page has them) scroll the content;
    //     an inner list at its end hands the wheel to the content scroller (OuterScroll)
    // Only the class and race pages use it; every other page scrolls as a whole as before.
    public partial class AtlasPage
    {
        ScrollViewer contentScroller;
        FrameworkElement fixedHead;
        bool fixedWheelWired;
        static readonly Thickness PagePadding = new Thickness(0, 0, 8, 0);

        public ScrollViewer ContentScrollerForTest => contentScroller;
        public FrameworkElement FixedHeadForTest => fixedHead;
        public bool FixedHeadOnForTest => contentScroller != null;
        // the width the page's top spans (the hero, the banner): the Scroller's width less the room its bar takes (on a fixed-head page the content scroller keeps that room)
        public double PageWidthForTest => fixedHead != null ? fixedHead.ActualWidth : Body.ActualWidth;

        public static bool FixedHeadFor(AtlasPageModel m) => m != null && !m.NotFound && (m.Kind == "class" || m.Kind == "race");

        // the scroller that moves the page's content: the content scroller on a fixed-head page, else the page's own
        ScrollViewer OuterScroll => contentScroller ?? Scroller;

        void PlaceFixedHead(StackPanel col, FrameworkElement content)
        {
            var top = col.Children.Cast<UIElement>().ToList();
            col.Children.Clear();
            var head = new StackPanel { Margin = PagePadding, Tag = "fixed-head" };
            foreach (var e in top) head.Children.Add(e);
            var sv = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false, Padding = PagePadding, Content = content, Tag = "fixed-content" };
            Motion.SetEdgeFade(sv, true);
            var frame = new Grid { Tag = "fixed-frame" };
            frame.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            frame.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            frame.Children.Add(head);
            Grid.SetRow(sv, 1); frame.Children.Add(sv);
            frame.SetBinding(HeightProperty, new Binding("ViewportHeight") { Source = Scroller });
            col.Children.Add(frame);
            Scroller.Padding = new Thickness(0);
            Scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            contentScroller = sv; fixedHead = head;
            if (!fixedWheelWired)
            {
                fixedWheelWired = true;
                Scroller.PreviewMouseWheel += FixedHead_Wheel;
            }
        }

        void LeaveFixedHead()
        {
            if (contentScroller == null) return;
            contentScroller = null; fixedHead = null;
            Scroller.Padding = PagePadding;
            Scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        }

        // the wheel over the hero or the tabs scrolls the content (over the content the content scroller has it already)
        void FixedHead_Wheel(object sender, MouseWheelEventArgs e)
        {
            var sv = contentScroller;
            if (sv == null || e.Handled || IsInside(e.OriginalSource as DependencyObject, sv)) return;
            e.Handled = true;
            sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta);
        }

        // the page keys scroll the content when the focus is not in it (the content scroller handles them itself when it is) and not in a text box
        bool FixedHeadKey(KeyEventArgs e, ModifierKeys mods)
        {
            var sv = contentScroller;
            if (sv == null || mods != ModifierKeys.None || PaletteOpen) return false;
            var focus = Keyboard.FocusedElement as DependencyObject;
            if (focus is TextBox || (focus != null && IsInside(focus, sv))) return false;
            bool page = focus == null || ReferenceEquals(focus, Scroller) || ReferenceEquals(focus, this) || !IsInside(focus, this);
            switch (e.Key)
            {
                case Key.PageDown: sv.PageDown(); return true;
                case Key.PageUp: sv.PageUp(); return true;
                case Key.Home: if (!page) return false; sv.ScrollToTop(); return true;
                case Key.End: if (!page) return false; sv.ScrollToBottom(); return true;
                case Key.Down: if (!page) return false; sv.LineDown(); return true;
                case Key.Up: if (!page) return false; sv.LineUp(); return true;
            }
            return false;
        }

        public bool PressFixedHeadKeyForTest(Key key)
        {
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this) ?? new FakeSource(), 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            return FixedHeadKey(args, ModifierKeys.None);
        }
    }
}
