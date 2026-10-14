using System;
using System.Windows;
using System.Windows.Controls;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // The Addons page layout (DESIGN.md, "Addons page"): the cards of both lists sit in a CardGrid, as many columns as the width allows
    // (Grid), or in one column up to a comfortable width (List). The choice is remembered in the settings.
    public partial class MainWindow
    {
        public const double AddonsListMaxWidth = 960;
        bool addonsViewReady;     // false while the window is built: the radio buttons' first Checked must not write the settings

        public bool AddonsListMode => settings.AddonsView == "list";

        void InitAddonsView()
        {
            ApplyAddonsView(AddonsListMode);
            addonsViewReady = true;
        }

        void AddonsView_Checked(object sender, RoutedEventArgs e)
        {
            if (!addonsViewReady) return;
            SetAddonsView((sender as FrameworkElement)?.Tag as string == "list");
        }

        // list = true: one column; false: the grid. save: the user chose it (not a restore)
        public void SetAddonsView(bool list, bool save = true)
        {
            if (save && AddonsListMode != list) { settings.AddonsView = list ? "list" : "grid"; SettingsStore.Save(settings); }
            ApplyAddonsView(list);
        }

        void ApplyAddonsView(bool list)
        {
            CardGrid.SetListMode(AddonsRoot, list);
            AddonsRoot.MaxWidth = list ? AddonsListMaxWidth : double.PositiveInfinity;
            AddonsRoot.HorizontalAlignment = list ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
            (list ? AddonsViewList : AddonsViewGrid).IsChecked = true;
            AddonsRoot.InvalidateMeasure();
        }

        // columns the lists are laid out in right now (0 when a list is not on screen)
        public int CardColumnsForTest => Cards.IsVisible ? FindDown<CardGrid>(Cards)?.Columns ?? 0 : CardsSkeleton.IsVisible ? CardsSkeleton.Columns : 0;
        public int OtherColumnsForTest => OtherCards.IsVisible ? FindDown<CardGrid>(OtherCards)?.Columns ?? 0 : OtherSkeleton.IsVisible ? OtherSkeleton.Columns : 0;
    }
}
