using System.Windows;
using System.Windows.Controls;

namespace ElansAddonHub.Lodge
{
    public partial class AvatarView : UserControl
    {
        // rank ring, speaking ring and status dot (the sidebar lists); off for chat, cards and toasts
        public static readonly DependencyProperty DecorProperty = DependencyProperty.Register(nameof(Decor), typeof(bool), typeof(AvatarView),
            new PropertyMetadata(false, (d, e) => ((AvatarView)d).DecorLayer.Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed));

        public bool Decor { get => (bool)GetValue(DecorProperty); set => SetValue(DecorProperty, value); }

        // the rank ring around a Decor avatar (off where the surrounding already shows the rank, e.g. the overlay pill)
        public static readonly DependencyProperty RankRingProperty = DependencyProperty.Register(nameof(RankRing), typeof(bool), typeof(AvatarView),
            new PropertyMetadata(true, (d, e) => ((AvatarView)d).RankLayer.Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed));

        public bool RankRing { get => (bool)GetValue(RankRingProperty); set => SetValue(RankRingProperty, value); }

        public AvatarView() { InitializeComponent(); }
    }
}
