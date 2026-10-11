using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ElansAddonHub
{
    // ELANSHUB_DESIGN_SHOTS=skeleton with --selftest <dir>: puts the pages that load data into their "waiting" state and snapshots them
    // (sk-addons.png, sk-lodge.png, sk-bags.png) - what the skeleton placeholders replace is blank space. Quits when done.
    // `BEFORE` is defined when this file is built into the pre-skeleton tree (docs/design/compare-shimmer.png), where those hooks do not exist yet.
    public partial class MainWindow
    {
        async Task SkeletonShots(string dir)
        {
            try
            {
                Width = 1180; Height = 760;

                // Addons: the first update check is running and there is no list yet
                ShowTab("addons");
                cards.Clear(); manifest = null; lastError = null; checking = true;
                OtherPanel.Visibility = Visibility.Collapsed;
#if !BEFORE
                UpdateCardsSkeleton();
#endif
                await Task.Delay(500);
                Snapshot(Path.Combine(dir, "sk-addons.png"));

                // Lodge: connecting, the welcome frame (channels + history) has not arrived
                TabLodge.IsChecked = true;
                Session.Disconnect();
                LodgePage.ShowChatForTest();
                LodgePage.UpdateMeForTest();
                await Task.Delay(500);
                Snapshot(Path.Combine(dir, "sk-lodge.png"));

                // Bags: the saved bags are being read
                TabInventory.IsChecked = true;
                await Task.Delay(300);
                InventoryView.LoadingStateForTest();
                await Task.Delay(500);
                Snapshot(Path.Combine(dir, "sk-bags.png"));
                File.WriteAllText(Path.Combine(dir, "result.txt"), "skeleton design shots done");
            }
            catch (Exception e) { File.WriteAllText(Path.Combine(dir, "result.txt"), "design shots crashed: " + e); }
            Quit();
        }
    }
}
