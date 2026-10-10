using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // First-run guide host: shown once on a fresh install, again from Settings > About.
    public partial class MainWindow
    {
        WelcomeGuide guide;
        public WelcomeGuide GuideForTest => guide;
        // tests: what the guide records instead of really installing / joining
        public List<string> GuideInstalledForTest, GuideJoinedForTest;
        public List<GuideAddon> GuideAddonsOverride;

        void InitWelcome()
        {
            Loaded += (s, e) => { if (SettingsStore.Fresh && !settings.WelcomeDone && guide == null) ShowWelcomeGuide(); };
        }

        public void ShowWelcomeGuide()
        {
            if (guide != null) return;
            var host = new WelcomeGuide.Host
            {
                WowRoot = () => WowLocator.IsWowRoot(settings.WowRoot) ? settings.WowRoot : null,
                PickFolder = () => { PickWowFolder(); },
                Addons = () => GuideAddonsOverride ?? cards.Select(c => new GuideAddon
                {
                    Id = c.Info.Id, Name = c.Info.Name, Description = c.Info.Description, Required = c.Info.Required,
                    Installed = c.State != CardState.NotInstalled, Checked = c.Info.Required,
                }).ToList(),
                Install = ids => { if (GuideInstalledForTest != null) GuideInstalledForTest.AddRange(ids); else _ = GuideInstall(ids); },
                Join = link => { if (GuideJoinedForTest != null) GuideJoinedForTest.Add(link); else { ShowTab("lodge"); LodgePage.JoinWithLink(link); } },
                Finished = done => CloseGuide(done),
            };
            guide = new WelcomeGuide(host);
            GuideHost.Child = guide;
            GuideHost.Visibility = Visibility.Visible;
            // the page dims in with a fade (Standard); the card itself opens with Motion.DialogIn (see WelcomeGuide)
            if (Motion.Enabled) GuideHost.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, Motion.Standard) { EasingFunction = Motion.EaseOut });
        }

        void CloseGuide(bool finished)
        {
            settings.WelcomeDone = true;
            SettingsStore.Save(settings);
            SettingsStore.Fresh = false;
            GuideHost.BeginAnimation(OpacityProperty, null);
            GuideHost.Visibility = Visibility.Collapsed;
            GuideHost.Child = null;
            guide = null;
        }

        async Task GuideInstall(List<string> ids)
        {
            foreach (var id in ids)
            {
                var card = cards.FirstOrDefault(c => c.Info.Id == id);
                if (card != null && !card.Busy && card.State == CardState.NotInstalled) await Install(card);
            }
        }
    }
}
