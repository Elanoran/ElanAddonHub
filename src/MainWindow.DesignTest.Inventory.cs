using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // ELANSHUB_DESIGN_SHOTS=inventory with --selftest <dir>: renders the Inventory page (characters on the left, bags on the right) from a synthetic
    // ElansBags.lua with five characters, plus a hovered item tooltip, so the Inventory design pass can be compared before / after.
    // Item icons and race portraits are read (read-only) from the local game install into the test cache. Quits when done.
    public partial class MainWindow
    {
        async Task InventoryShots(string dir)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string Extra(string key, string name, string cls, int lvl, string faction, long money, long ago, int cloth) =>
                "\t\t[\"" + key + "\"] = {\n\t\t\t[\"name\"] = \"" + name + "\",\n\t\t\t[\"realm\"] = \"Forever\",\n\t\t\t[\"class\"] = \"" + cls + "\",\n\t\t\t[\"level\"] = " + lvl + ",\n\t\t\t[\"faction\"] = \"" + faction + "\",\n\t\t\t[\"money\"] = " + money
                + ",\n\t\t\t[\"updated\"] = " + (now - ago) + ",\n\t\t\t[\"bagsAt\"] = " + (now - ago) + ",\n\t\t\t[\"bankAt\"] = " + (now - ago) + ",\n"
                + "\t\t\t[\"bags\"] = {\n\t\t\t\t{ [\"i\"] = 100, [\"c\"] = " + cloth + ", [\"q\"] = 1, [\"ic\"] = 132593, [\"l\"] = \"|cffffffff|Hitem:100::::::::|h[Linen Cloth]|h|r\" },\n\t\t\t},\n"
                + "\t\t\t[\"bank\"] = {\n\t\t\t\t{ [\"i\"] = 107, [\"c\"] = 3, [\"q\"] = 2, [\"ic\"] = 133784, [\"l\"] = \"|cff1eff00|Hitem:107::::::::|h[Bright Gem]|h|r\" },\n\t\t\t},\n\t\t\t[\"equipped\"] = {\n\t\t\t},\n\t\t},\n";
            var lua = BagsLua(3, true);
            var cut = lua.LastIndexOf("\t},\n}");
            lua = lua.Substring(0, cut)
                + Extra("Forever-Tessa", "Tessa", "MAGE", 33, "Alliance", 8723400, 5 * 3600, 40)
                + Extra("Forever-Grunk", "Grunk", "WARRIOR", 18, "Horde", 45300, 2 * 86400, 6)
                + Extra("Forever-Lunaria", "Lunaria", "PRIEST", 41, "Alliance", 25120000, 9 * 3600, 12)
                + lua.Substring(cut);
            var root = Path.Combine(dir, "invwow");
            if (Directory.Exists(root)) Directory.Delete(root, true);
            var sv = Path.Combine(root, "_classic_beta_", "WTF", "Account", "TESTACC", "SavedVariables");
            Directory.CreateDirectory(sv);
            File.WriteAllText(Path.Combine(sv, "ElansBags.lua"), lua, new UTF8Encoding(false));
            // race + sex of the characters (the Outpost companion saves them; the app uses them for the portraits)
            string Hub(string name, string cls, string race, int sex, int lvl) =>
                "\t\t[\"Forever-" + name + "\"] = { [\"name\"] = \"" + name + "\", [\"realm\"] = \"Forever\", [\"classFile\"] = \"" + cls + "\", [\"raceFile\"] = \"" + race + "\", [\"sex\"] = " + sex + ", [\"level\"] = " + lvl + " },\n";
            File.WriteAllText(Path.Combine(sv, "ElansHub.lua"), "ElansHubDB = {\n\t[\"current\"] = \"Forever-Elan\",\n\t[\"chars\"] = {\n"
                + Hub("Elan", "HUNTER", "Orc", 2, 25) + Hub("Measley", "PALADIN", "Scourge", 3, 22) + Hub("Tessa", "MAGE", "Human", 3, 33)
                + Hub("Grunk", "WARRIOR", "Tauren", 2, 18) + Hub("Lunaria", "PRIEST", "NightElf", 3, 41) + "\t},\n}\n", new UTF8Encoding(false));

            Height = 760;
            InventoryView.Init(() => root);
            IconStore.WowRoot = () => WowLocator.Find();   // the real game data, read-only; icons are cached in the test data folder
            TabInventory.IsChecked = true;
            InventoryView.FilterForTest("Forever-Elan");
            InventoryView.SelectTabForTest("bags");
            for (int i = 0; i < 60 && InventoryView.SlotsPlaceholderForTest > 0; i++) await Task.Delay(250);
            await Task.Delay(1500);   // portraits
            InventoryView.FilterForTest("Forever-Elan");
            await Task.Delay(600);
            SnapshotScaled(Path.Combine(dir, "i1-bags-100.png"), 1);

            // bank tab, then the tooltip of the White Cloak (worn by Elan and Measley, in Elan's bank bag and in the guild bank)
            InventoryView.SelectTabForTest("bank");
            await Task.Delay(1500);
            SnapshotScaled(Path.Combine(dir, "i3-bank-100.png"), 1);
            var slot = FindDown<Border>(InventoryView, b => b.ToolTip is ToolTip tt && tt.Content is StackPanel sp && sp.Children.Count > 0 && (sp.Children[0] as TextBlock)?.Text == "White Cloak");
            if (slot != null) ShotTip(slot, (ToolTip)slot.ToolTip, Path.Combine(dir, "i2-tooltip-100.png"));
            var row = FindDown<RadioButton>(InventoryView, r => r.ToolTip is ToolTip && (r.Tag as string) != null);
            if (row != null) ShotTip(row, (ToolTip)row.ToolTip, Path.Combine(dir, "i6-chartip-100.png"));
            InventoryView.FilterForTest(null);
            await Task.Delay(600);
            SnapshotScaled(Path.Combine(dir, "i4-all-100.png"), 1);
            InventoryView.SetSearchForTest("cloak");
            await Task.Delay(600);
            SnapshotScaled(Path.Combine(dir, "i5-search-100.png"), 1);
            File.WriteAllText(Path.Combine(dir, "result.txt"), "design shots done inventory" + (slot == null ? " (no tooltip slot found)" : ""));
            Quit();
        }

        // a tooltip popup can't be captured: draw its content next to the anchor on top of the window
        void ShotTip(FrameworkElement anchor, ToolTip tip, string file)
        {
            var content = (UIElement)tip.Content;
            tip.Content = null;
            var shell = new ToolTip { Style = tip.Style, Content = content };
            var at = anchor.TranslatePoint(new Point(anchor.ActualWidth * 0.6, anchor.ActualHeight * 0.8), this);
            shell.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = shell.DesiredSize;
            if (at.X + size.Width + 8 > ActualWidth) at.X = ActualWidth - size.Width - 8;
            if (at.Y + size.Height + 8 > ActualHeight) at.Y = ActualHeight - size.Height - 8;
            shell.Arrange(new Rect(at, size));
            shell.UpdateLayout();
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(new VisualBrush(this) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, ActualWidth, ActualHeight));
                dc.PushOpacity(0.35);
                dc.DrawRoundedRectangle(Brushes.Black, null, new Rect(at.X - 4, at.Y + 2, size.Width + 8, size.Height + 8), 14, 14);
                dc.Pop();
                dc.DrawRectangle(new VisualBrush(shell) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(at, size));
            }
            var bmp = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(dv);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using (var fs = File.Create(file)) enc.Save(fs);
        }
    }
}
