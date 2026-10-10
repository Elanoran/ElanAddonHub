using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // Addon health: the chip next to the update check on the Addons page, the red count on the rail's Addons item,
    // the in-game toast for new errors, and the panel (HealthDialog).
    public partial class MainWindow
    {
        HealthMonitor health;
        public HealthMonitor HealthForTest => health;

        void InitHealth()
        {
            health = new HealthMonitor(settings, () => settings.WowRoot, Dispatcher);
            health.Changed += () => { RefreshRailBadges(); };
            health.NewErrors += (title, n) =>
            {
                Session?.Toasts.Push(new ToastItem
                {
                    Kind = ToastKind.Health,
                    Sender = $"{title}: {n} new error{(n == 1 ? "" : "s")}",
                    Text = "See Outpost > Health",
                });
            };
            health.Start();
            RefreshHealthUi();
        }

        void HealthChip_Click(object sender, MouseButtonEventArgs e) { if (health != null) HealthDialog.ShowFor(this, health, App.Version); }

        // chip + rail count (called from RefreshRailBadges)
        void RefreshHealthUi()
        {
            if (health == null) return;
            var rep = health.Report;
            int n = rep.NewTotal;
            var res = Application.Current.Resources;
            Brush B(string k) => (Brush)res[k];
            string text; Brush fore, edge;
            if (!rep.AnyData) { text = "Health –"; fore = B("TextDim"); edge = B("Line"); }
            else if (n > 0) { text = "Health ⚠ " + n; fore = B("Danger"); edge = B("Danger"); }
            else { text = "Health ✓"; fore = B("Accent"); edge = B("Line"); }
            HealthChipText.Text = text;
            HealthChipText.Foreground = fore;
            HealthChip.BorderBrush = edge;
            var tip = new System.Text.StringBuilder("Addon health - click for details");
            if (!rep.AnyData) tip.Append("\nNo saved variables yet: log in with an Elan addon, then /reload once.");
            foreach (var h in rep.Addons)
            {
                HealthReader.Status(h, out _);
                tip.Append("\n" + h.Title + ": " + HealthReader.Status(h, out _));
            }
            HealthChip.ToolTip = tip.ToString();
            HealthBadge.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
            HealthBadgeText.Text = n > 9 ? "9+" : n.ToString();
            if (n > 0) TabAddons.ToolTip = (TabAddons.ToolTip as string ?? "Addons  (Ctrl+1)") + $"\n{n} new addon error{(n == 1 ? "" : "s")} - see Health";
        }
    }
}
