using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ElansAddonHub.Lodge;

namespace ElansAddonHub
{
    // --iconsheet <png>: the three card icons at 36/48/84/128 px on the card background (for design review)
    public static class IconSheet
    {
        public static void Render(string file)
        {
            var root = new Border { Background = new SolidColorBrush(Color.FromRgb(0x24, 0x27, 0x2E)), Padding = new Thickness(20) };
            var grid = new Grid();
            var sizes = new[] { 36, 48, 84, 128 };
            for (int i = 0; i < 4; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int i = 0; i < sizes.Length; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var keys = new[] { "Icon.Hunter", "Icon.Paladin", "Icon.Hub", "Icon.Bags" };
            for (int r = 0; r < 4; r++)
                for (int c = 0; c < sizes.Length; c++)
                {
                    var img = new Image { Source = (ImageSource)Application.Current.FindResource(keys[r]), Width = sizes[c], Height = sizes[c], Margin = new Thickness(12), VerticalAlignment = VerticalAlignment.Center };
                    RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                    Grid.SetRow(img, r); Grid.SetColumn(img, c);
                    grid.Children.Add(img);
                }
            var stack = new StackPanel();
            stack.Children.Add(grid);
            // the 8 reaction badges at pill (17), picker (28) and large (48) size
            foreach (var px in new[] { 17, 28, 48 })
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 6, 0, 0) };
                foreach (var id in new[] { "ready", "notready", "lol", "love", "fight", "loot", "wipe", "epic" })
                {
                    var img = new Image { Source = ReactionArt.Icon(id), Width = px, Height = px, Margin = new Thickness(4) };
                    RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                    row.Children.Add(img);
                }
                stack.Children.Add(row);
            }
            root.Child = stack;
            root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            root.Arrange(new Rect(root.DesiredSize));
            root.UpdateLayout();
            var bmp = new RenderTargetBitmap((int)root.DesiredSize.Width, (int)root.DesiredSize.Height, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(root);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using (var fs = System.IO.File.Create(file)) enc.Save(fs);
            AvatarSheet(System.IO.Path.ChangeExtension(file, null) + "-avatars.png");
        }

        // the 24 preset avatars at 96 / 48 / 34 / 22 px (and tinted with the 8 accent colours), on the Lodge's card colour
        public static void AvatarSheet(string file)
        {
            var root = new Border { Background = new SolidColorBrush(Color.FromRgb(0x16, 0x19, 0x1C)), Padding = new Thickness(18) };
            var stack = new StackPanel();
            var wrap = new UniformGrid { Columns = 6 };
            foreach (var id in AvatarCatalog.Ids)
            {
                var cell = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6) };
                cell.Children.Add(Pic(id, null, 96));
                var col = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
                col.Children.Add(Pic(id, null, 48)); col.Children.Add(Pic(id, null, 34)); col.Children.Add(Pic(id, null, 22));
                cell.Children.Add(col);
                var name = new TextBlock { Text = AvatarCatalog.Name(id), Foreground = Brushes.Gray, FontSize = 10, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(4, 0, 0, 2) };
                cell.Children.Add(name);
                wrap.Children.Add(cell);
            }
            stack.Children.Add(wrap);
            // every avatar in every accent colour at 48 px
            foreach (var accent in AvatarCatalog.AccentIds)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 4, 0, 0) };
                foreach (var id in AvatarCatalog.Ids) row.Children.Add(Pic(id, accent, 40, 1));
                stack.Children.Add(row);
            }
            root.Child = stack;
            Save(root, file);

            // large view (192 px) for art review
            var big = new Border { Background = new SolidColorBrush(Color.FromRgb(0x16, 0x19, 0x1C)), Padding = new Thickness(12) };
            var grid = new UniformGrid { Columns = 6 };
            foreach (var id in AvatarCatalog.Ids) grid.Children.Add(Pic(id, null, 192, 4));
            big.Child = grid;
            Save(big, System.IO.Path.ChangeExtension(file, null) + "-large.png");
        }

        static Image Pic(string id, string accent, int px, int margin = 3)
        {
            var img = new Image { Source = AvatarCatalog.Image(id, accent), Width = px, Height = px, Margin = new Thickness(margin) };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            return img;
        }

        public static void Save(FrameworkElement root, string file)
        {
            root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            root.Arrange(new Rect(root.DesiredSize));
            root.UpdateLayout();
            var bmp = new RenderTargetBitmap((int)root.DesiredSize.Width, (int)root.DesiredSize.Height, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(root);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using (var fs = System.IO.File.Create(file)) enc.Save(fs);
        }
    }
}
