using System;
using System.Windows;
using System.Windows.Markup;

namespace ElansAddonHub
{
    // The one width of the side menus (2.77): the Atlas navigation, the Settings sections, the Characters list and the Lodge sidebar all show their menu fill
    // Size.SideMenu wide (Theme.xaml), from the same left edge, and the title bar search box ends where they end (MainWindow.Search.cs).
    // A page's menu fill may reach past its column (the Atlas and Characters fills start 8 px left of their rows; the Atlas one also 4 px into the gap):
    // the column is the width less that overhang, written in the page as  <ColumnDefinition Width="{app:SideMenuColumn Bleed=8}" />.
    public static class SideMenu
    {
        public const double Fallback = 252;
        // the title bar box starts 8 px left of the menus (16 px from the window's edge against the page's 24): this much wider, it ends where they end
        public const double SearchLead = 8;

        public static double Width
        {
            get
            {
                try { if (Application.Current?.TryFindResource("Size.SideMenu") is double d && d > 0) return d; } catch (Exception) { }
                return Fallback;
            }
        }

        public static double Column(double bleed) => Math.Max(0, Width - bleed);
        public static double SearchWidth => Width + SearchLead;
    }

    // XAML: the width of a side menu's column (Size.SideMenu less the fill's overhang)
    public sealed class SideMenuColumnExtension : MarkupExtension
    {
        public double Bleed { get; set; }
        public override object ProvideValue(IServiceProvider serviceProvider) => new GridLength(SideMenu.Column(Bleed));
    }

    // XAML: the title bar search box's width (Size.SideMenu + SearchLead)
    public sealed class SideMenuSearchWidthExtension : MarkupExtension
    {
        public override object ProvideValue(IServiceProvider serviceProvider) => SideMenu.SearchWidth;
    }
}
