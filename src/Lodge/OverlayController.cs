using System;
using System.Windows;
using System.Windows.Media.Animation;
using ElansAddonHub.Services;

namespace ElansAddonHub.Lodge
{
    // Opens and closes an overlay card (DESIGN.md: Surface3, hairline, radius 14, padding 24, Elevation.2) over a Scrim, the way the
    // Looking-for-group form does: the scrim fades in (Standard), the card opens with Motion.DialogIn, closing is Motion.DialogOut (Fast).
    // Everything is instant with Windows animations off. Used by the Invite sheet and the "Create a lodge" guide.
    public sealed class OverlayController
    {
        readonly FrameworkElement overlay, box;
        bool closing;
        int token;

        public OverlayController(FrameworkElement overlay, FrameworkElement box) { this.overlay = overlay; this.box = box; }

        public bool IsOpen => overlay.Visibility == Visibility.Visible && !closing;

        public void Open()
        {
            closing = false; token++;
            overlay.BeginAnimation(UIElement.OpacityProperty, null);
            overlay.Visibility = Visibility.Visible;
            if (Motion.Enabled) overlay.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Motion.Standard) { EasingFunction = Motion.EaseOut });
            Motion.DialogIn(box);
        }

        public void Close(Action done = null)
        {
            if (overlay.Visibility != Visibility.Visible || closing) return;
            if (!Motion.Enabled) { overlay.Visibility = Visibility.Collapsed; done?.Invoke(); return; }
            closing = true;
            var my = ++token;
            overlay.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0, Motion.Fast) { EasingFunction = Motion.EaseIn, FillBehavior = FillBehavior.HoldEnd });
            Motion.DialogOut(box, () =>
            {
                if (my != token) return;   // opened again meanwhile
                closing = false;
                overlay.Visibility = Visibility.Collapsed;
                overlay.BeginAnimation(UIElement.OpacityProperty, null);
                box.BeginAnimation(UIElement.OpacityProperty, null); box.Opacity = 1; box.RenderTransform = System.Windows.Media.Transform.Identity;
                done?.Invoke();
            });
        }
    }
}
