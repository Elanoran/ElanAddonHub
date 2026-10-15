using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ElansAddonHub
{
    // The global search (Atlas refresh, step 2): Ctrl+K opens the Atlas palette over ANY page of the Outpost, and so does the box at the left of the title bar. Choosing a result switches
    // the rail to the Atlas page and opens it there. The palette's layer lives in AtlasPage (AtlasPage.Search.cs) but is hosted by this window (PaletteHost, over the page area).
    //   - the box is a launcher (read-only): a click, Enter, Space, Down or the first typed letter opens the palette with it; it is the first stop of the Tab order
    //   - it is a hit-test island in the caption: a press on it never starts the window drag (the caption's MouseLeftButtonDown is DragMove); the empty caption around it still drags
    //   - under 900 px of window it shrinks to an icon button (the same launcher); the update banner moves with it
    //   - a web view (the Browser page) is a window of its own and would be drawn above the layer: it steps aside while the palette is open (BrowserPage.HoldForOverlay)
    public partial class MainWindow
    {
        public const double TitleSearchNarrowBelow = 900, TitleSearchWidth = 240;
        bool titleSearchNarrow, searchWired;
        public ModifierKeys? GlobalModifiersForTest;                  // self-test: the modifier keys held (the real keyboard cannot be pressed from a test)

        public TextBox TitleSearchForTest => TitleSearch;
        public Button TitleSearchIconForTest => TitleSearchIcon;
        public bool TitleSearchIsIconForTest => titleSearchNarrow;
        public FrameworkElement PaletteHostForTest => PaletteHost;
        public FrameworkElement TitleBarForTest => TitleSearch.Parent as FrameworkElement;

        void InitGlobalSearch()
        {
            if (searchWired) return;
            searchWired = true;
            AtlasView.UseOverlayHost(PaletteHost);
            AtlasView.ShowAtlas = () => ShowTab("atlas");
            AtlasView.PaletteOpened += () => { if (TabBrowser.IsChecked == true) BrowserView.HoldForOverlay(true); };
            AtlasView.PaletteClosed += () => BrowserView.HoldForOverlay(false);
            // going to another page (the rail) while the palette is open closes it: the layer belongs to the page it was opened over
            foreach (var rb in new[] { TabAddons, TabLodge, TabInventory, TabBrowser, TabAtlas, TabSettings }) rb.Checked += (s, e) => { if (AtlasView.PaletteOpen) AtlasView.ClosePalette(false); };
            PreviewKeyDown += GlobalSearch_PreviewKeyDown;
            SizeChanged += (s, e) => ApplyTitleSearch();
            ApplyTitleSearch();
        }

        // Ctrl+K (not Ctrl+Shift+K, not Alt): the palette over the page you are on
        void GlobalSearch_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var mods = GlobalModifiersForTest ?? Keyboard.Modifiers;
            if (e.Key != Key.K || (mods & ModifierKeys.Control) == 0 || (mods & (ModifierKeys.Alt | ModifierKeys.Shift)) != 0) return;
            e.Handled = true;
            _ = AtlasView.OpenPalette();
        }

        // the box, or the icon when the window is narrow; the update banner keeps clear of whichever is there
        void ApplyTitleSearch()
        {
            bool narrow = ActualWidth > 0 && ActualWidth < TitleSearchNarrowBelow;
            titleSearchNarrow = narrow;
            TitleSearch.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
            TitleSearchIcon.Visibility = narrow ? Visibility.Visible : Visibility.Collapsed;
            double left = 16 + (narrow ? 32 : TitleSearchWidth) + 8;
            HubBanner.Margin = new Thickness(left, 0, 112, 0);
        }

        // a press on the box is its own: no window drag
        void TitleSearch_Down(object sender, MouseButtonEventArgs e) { e.Handled = true; _ = AtlasView.OpenPalette(); }
        void TitleSearchIcon_Click(object sender, RoutedEventArgs e) { _ = AtlasView.OpenPalette(); }

        void TitleSearch_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter || e.Key == Key.Down || e.Key == Key.Space) { e.Handled = true; _ = AtlasView.OpenPalette(); }
        }

        void TitleSearch_TextInput(object sender, TextCompositionEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.Text)) return;
            e.Handled = true;
            _ = AtlasView.OpenPalette(e.Text);
        }
    }
}
