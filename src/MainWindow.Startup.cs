using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // Start-up layout: the Addons page keeps its shape while the update check and the scan of Interface\AddOns run (DESIGN.md, "Loading").
    // Both lists start as skeleton cards, as many as the last successful load had (Settings.LastAddonCount / LastOtherCount; sensible
    // defaults on a first run), each exactly as tall as the real card (Size.Card.Addon / Size.Row.Other). The real cards then take their
    // places with a quick crossfade, and if the real count differs only the bottom of that list changes. Nothing above moves.
    public partial class MainWindow
    {
        const int DefaultAddonSlots = 3, DefaultOtherSlots = 5, MaxAddonSlots = 8, MaxOtherSlots = 40;

        bool othersLoaded;      // the first scan of Interface\AddOns has finished: until then the Other addons list shows skeleton rows
        bool othersExpected;    // there is a WoW client folder, so an Other addons section is coming

        int AddonSlots => Math.Min(MaxAddonSlots, Math.Max(0, settings.LastAddonCount ?? DefaultAddonSlots));
        int OtherSlots => Math.Min(MaxOtherSlots, Math.Max(0, settings.LastOtherCount ?? DefaultOtherSlots));
        bool OthersSkeletonWanted => !othersLoaded && othersExpected;

        // build the placeholders from the remembered counts (called before the window is first shown, and by the self-test for a cold start)
        void PrepareStartupLayout()
        {
            othersLoaded = false;
            othersExpected = !string.IsNullOrEmpty(settings.WowRoot) && ClientFlavor() != null;
            FillSkeleton(CardsSkeleton, AddonSlots, AddonSkeletonCard);
            FillSkeleton(OtherSkeleton, othersExpected ? OtherSlots : 0, OtherSkeletonRow);
            UpdateCardsSkeleton();
            UpdateOthersHeader();
        }

        // the page as it is before the first update check has answered (self-test cold start)
        void ResetColdState()
        {
            cards.Clear(); others.Clear(); manifest = null; lastError = null; checking = false;
            HubBanner.Visibility = Visibility.Collapsed;
            Cards.BeginAnimation(UIElement.OpacityProperty, null); Cards.Opacity = 1;
            OtherCards.BeginAnimation(UIElement.OpacityProperty, null); OtherCards.Opacity = 1;
            PrepareStartupLayout();
        }

        static void FillSkeleton(Panel host, int count, Func<int, FrameworkElement> make)
        {
            host.Children.Clear();
            for (int i = 0; i < count; i++) host.Children.Add(make(i));
        }

        static readonly double[] TitleW = { 168, 136, 152, 120, 144, 160 }, SubW = { 240, 208, 224, 192, 232, 216 }, LineW = { 136, 120, 128, 104, 144, 112 };

        // same height as a real addon card: 80 px art, title / subtitle / version / stats lines centred beside it
        FrameworkElement AddonSkeletonCard(int i)
        {
            var card = new ContentControl { Style = (Style)FindResource("Card.Raised"), Height = (double)FindResource("Size.Card.Addon") };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16) };
            row.Children.Add(new Skeleton { Width = 80, Height = 80, CornerRadius = (CornerRadius)FindResource("Radius.ControlLg"), VerticalAlignment = VerticalAlignment.Top });
            var col = new StackPanel { Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            col.Children.Add(new Skeleton { Height = 16, Width = TitleW[i % TitleW.Length], CornerRadius = new CornerRadius(8), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 2) });
            col.Children.Add(new Skeleton { Height = 12, Width = SubW[i % SubW.Length], CornerRadius = new CornerRadius(6), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 2) });
            col.Children.Add(new Skeleton { Height = 12, Width = LineW[i % LineW.Length], CornerRadius = new CornerRadius(6), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 16, 0, 2) });
            row.Children.Add(col);
            card.Content = row;
            return card;
        }

        // same height as a collapsed row of the Other addons list: 36 px tile, two lines
        FrameworkElement OtherSkeletonRow(int i)
        {
            var card = new ContentControl { Style = (Style)FindResource("Card.Raised"), Height = (double)FindResource("Size.Row.Other") };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 12, 16, 12) };
            row.Children.Add(new Skeleton { Width = 36, Height = 36, CornerRadius = (CornerRadius)FindResource("Radius.ControlLg"), VerticalAlignment = VerticalAlignment.Center });
            var col = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            col.Children.Add(new Skeleton { Height = 12, Width = TitleW[(i + 2) % TitleW.Length] - 24, CornerRadius = new CornerRadius(6), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 3, 0, 3) });
            col.Children.Add(new Skeleton { Height = 10, Width = LineW[(i + 1) % LineW.Length], CornerRadius = new CornerRadius(5), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 2) });
            row.Children.Add(col);
            card.Content = row;
            return card;
        }

        // skeletons stand in for a list until its data has arrived (or failed); then the real cards fade in over them, in place
        void UpdateCardsSkeleton()
        {
            SetSkeleton(CardsSkeleton, Cards, cards.Count == 0 && lastError == null && (checking || manifest == null));
            SetSkeleton(OtherSkeleton, OtherCards, OthersSkeletonWanted);
        }

        public bool CardsSkeletonForTest => CardsSkeleton.Visibility == Visibility.Visible;

        void SetSkeleton(Panel skeleton, UIElement real, bool want)
        {
            if (want)
            {
                skeleton.BeginAnimation(UIElement.OpacityProperty, null); skeleton.Opacity = 1; skeleton.Tag = null;
                real.BeginAnimation(UIElement.OpacityProperty, null); real.Opacity = 1;
                skeleton.Visibility = skeleton.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                return;
            }
            if (skeleton.Visibility != Visibility.Visible || skeleton.Tag != null) return;
            if (!Motion.Enabled || !skeleton.IsLoaded || !skeleton.IsVisible) { skeleton.Visibility = Visibility.Collapsed; return; }
            var token = new object();
            skeleton.Tag = token;
            real.Opacity = 0;
            var inA = new DoubleAnimation(0, 1, Motion.Fast) { EasingFunction = Motion.EaseOut, FillBehavior = FillBehavior.HoldEnd };
            var outA = new DoubleAnimation(1, 0, Motion.Fast) { EasingFunction = Motion.EaseOut, FillBehavior = FillBehavior.HoldEnd };
            outA.Completed += (s, e) =>
            {
                if (skeleton.Tag != token) return;
                skeleton.BeginAnimation(UIElement.OpacityProperty, null); skeleton.Opacity = 1; skeleton.Tag = null;
                skeleton.Visibility = Visibility.Collapsed;
                real.BeginAnimation(UIElement.OpacityProperty, null); real.Opacity = 1;
            };
            real.BeginAnimation(UIElement.OpacityProperty, inA);
            skeleton.BeginAnimation(UIElement.OpacityProperty, outA);
        }

        // remember how many cards each list had, so the next start shows exactly that many placeholders
        void RememberLayout(int? addons, int? others)
        {
            bool changed = false;
            if (addons != null && settings.LastAddonCount != addons) { settings.LastAddonCount = addons; changed = true; }
            if (others != null && settings.LastOtherCount != others) { settings.LastOtherCount = others; changed = true; }
            if (changed) SettingsStore.Save(settings);
        }
    }
}
