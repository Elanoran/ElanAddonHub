using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using ElansAddonHub.Lodge;
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
                Addons = () => GuideAddonsOverride ?? GuideAddonsFrom(cards.Select(c => (c.Info, c.State != CardState.NotInstalled)), PlayedClasses()),
                // 3.8.2: what a picked addon needs is installed first (also when it wasn't ticked)
                Install = ids => { ids = AddonOrder.WithNeedsFirst(ids, manifest?.Addons); if (GuideInstalledForTest != null) GuideInstalledForTest.AddRange(ids); else _ = GuideInstall(ids); },
                Join = link => { if (GuideJoinedForTest != null) GuideJoinedForTest.Add(link); else { ShowTab("lodge"); LodgePage.JoinWithLink(link); } },
                Finished = done => CloseGuide(done),
                // 3.11: three sharing choices; each sets the same switches as Settings > Privacy
                CurrentPreset = () => SharingPresets.Current(settings),
                ApplyPreset = ApplySharingPreset,
                MainCharacter = () => GuideMainOverride ?? MainCharacter()?.Name,
                OpenPrivacy = () => { ShowTab("settings"); SettingsPage.Show("privacy"); },
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
            if (finished) OptIns.MarkGuideAnswered(settings);   // its sharing choices covered those
            SettingsStore.Save(settings);
            SettingsStore.Fresh = false;
            GuideHost.BeginAnimation(OpacityProperty, null);
            GuideHost.Visibility = Visibility.Collapsed;
            GuideHost.Child = null;
            guide = null;
        }

        // the character "Share with my Lodge" shares when none is chosen yet: the highest one of this PC (the newest on a tie)
        public string GuideMainOverride, GuideMainKeyOverride;   // tests
        InvChar MainCharacter() => ArmoryShare.Local(settings.WowRoot).OrderByDescending(c => c.Level).ThenByDescending(c => c.Updated).FirstOrDefault();

        public void ApplySharingPreset(string id)
        {
            var before = (settings.ShareGameOff, settings.ShareZoneOff, settings.ShareXpOff, settings.AtlasShareOff, settings.FlightShareOff, settings.ArmoryShareOn, settings.ShareLivePosWithLodge, settings.LivePosScope);
            SharingPresets.Apply(settings, id, id == SharingPresets.Lodge ? GuideMainKeyOverride ?? MainCharacter()?.Key : null);
            SettingsStore.Save(settings);
            if (Session == null) return;
            if (before.Item1 != settings.ShareGameOff || before.Item2 != settings.ShareZoneOff || before.Item3 != settings.ShareXpOff) Session.SendGame();
            if (before.Item4 != settings.AtlasShareOff) { Session.AtlasSettingsChanged(); AtlasView?.FillSharingInvite(); }
            if (before.Item5 != settings.FlightShareOff) Session.FlightSettingsChanged();
            if (before.Item6 != settings.ArmoryShareOn) Session.ArmorySettingsChanged();
            Session.SendPosition();
        }

        // 3.11: the addon step's starting ticks and reasons (AddonPicks)
        public static List<GuideAddon> GuideAddonsFrom(IEnumerable<(AddonInfo info, bool installed)> addons, ISet<string> played) => addons.Select(a =>
        {
            var (tick, reason) = AddonPicks.Recommend(a.info.Id, a.info.Required, a.info.Classes, played);
            return new GuideAddon { Id = a.info.Id, Name = a.info.Name, Description = a.info.Description, Required = a.info.Required, Installed = a.installed, Checked = tick, Reason = reason };
        }).ToList();

        // the classes of the characters this PC knows: Elan's Hub's records (empty on a fresh install) and the class seen in game
        HashSet<string> PlayedClasses()
        {
            var files = ArmoryShare.Local(settings.WowRoot).Select(c => c.Core?.ClassFile ?? c.Class).ToList();
            if (AddonCard.ClassHint != null) files.Add(AddonCard.ClassHint);
            return AddonPicks.Played(files);
        }

        async Task GuideInstall(List<string> ids)
        {
            foreach (var id in ids)
            {
                var card = cards.FirstOrDefault(c => c.Info.Id == id);
                if (card != null && !card.Busy && card.State == CardState.NotInstalled) await InstallWithNeeds(card);
            }
        }
    }
}
