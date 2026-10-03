using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    public enum CardState { NotInstalled, UpdateAvailable, UpToDate, DevCopy, NoClient, NoFolder, Busy }

    // One addon card in the window.
    public class AddonCard : INotifyPropertyChanged
    {
        public AddonInfo Info { get; private set; }
        public CardState State { get; private set; }
        public string Name => Info.Name;
        public string Subtitle => Info.Required && Info.Description != null ? Info.Description : Info.FlavorName ?? Info.Description ?? "";
        public string Installed { get; private set; }
        // hover the client name to see exactly where it installs
        public string InstallPath { get; private set; }
        public string Latest => Info.Version;
        public string PillText { get; private set; }
        public Brush PillBg { get; private set; }
        public Brush PillFg { get; private set; }
        public string ButtonText { get; private set; }
        public bool ButtonEnabled { get; private set; }
        public string ChangesTitle { get; private set; }
        public List<ChangeEntry> Changes { get; private set; }
        // compact one-liner: "1.17.0  >  1.18.0" / "Installed 1.18.0" / "Latest 1.18.0"
        public string VersionLine { get; private set; }
        public string DevNote { get; private set; }
        public string ReleasesUrl => GitHubStats.ReleasesUrl;

        // GitHub stats (filled in later, silently absent when GitHub can't be reached)
        string statsText;
        public string StatsText => statsText;
        public Visibility StatsVisibility => string.IsNullOrEmpty(statsText) ? Visibility.Collapsed : Visibility.Visible;
        public void SetStats(AddonStats st)
        {
            var parts = new List<string>();
            if (st != null)
            {
                if (st.Released != null) parts.Add("released " + GitHubStats.Ago(st.Released.Value));
                if (st.Downloads > 0) parts.Add(st.Downloads + (st.Downloads == 1 ? " download" : " downloads"));
                if (st.Releases > 0) parts.Add(st.Releases + (st.Releases == 1 ? " release" : " releases"));
            }
            statsText = string.Join("  \u00B7  ", parts);
            Notify(nameof(StatsText)); Notify(nameof(StatsVisibility));
        }

        // expanded = details (version history, dev-copy notes, links)
        bool expanded;
        public bool Expanded { get => expanded; set { expanded = value; Notify(); Notify(nameof(ExpandedVisibility)); Notify(nameof(Chevron)); } }
        public Visibility ExpandedVisibility => expanded ? Visibility.Visible : Visibility.Collapsed;
        public string Chevron => expanded ? "\uE70E" : "\uE70D";
        public Visibility DevNoteVisibility => State == CardState.DevCopy ? Visibility.Visible : Visibility.Collapsed;

        double progress;
        public double Progress { get => progress; set { progress = value; Notify(); } }
        bool busy;
        public bool Busy { get => busy; private set { busy = value; Notify(); Notify(nameof(BusyVisibility)); } }
        public Visibility BusyVisibility => busy ? Visibility.Visible : Visibility.Collapsed;
        string message;
        public string Message { get => message; set { message = value; Notify(); Notify(nameof(MessageVisibility)); } }
        public Visibility MessageVisibility => string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        // a git checkout is never replaced by the big button - only via this small link + a warning
        public Visibility ReplaceDevVisibility => State == CardState.DevCopy ? Visibility.Visible : Visibility.Collapsed;

        public AddonCard(AddonInfo info) { Info = info; }

        public void Update(AddonInfo info, string wowRoot)
        {
            Info = info;
            var installed = wowRoot == null ? null : Installer.InstalledVersion(wowRoot, info);
            Installed = installed ?? "Not installed";
            InstallPath = wowRoot == null || info.Folders == null || info.Folders.Count == 0 ? null
                : "Installs to " + System.IO.Path.Combine(WowLocator.AddOnsDir(wowRoot, info.Flavor), info.Folders[0]);

            if (wowRoot == null) State = CardState.NoFolder;
            else if (!WowLocator.HasFlavor(wowRoot, info.Flavor)) State = CardState.NoClient;
            else if (Installer.IsDevCopy(wowRoot, info)) State = CardState.DevCopy;
            else if (installed == null) State = CardState.NotInstalled;
            else if (Util.CompareVersions(info.Version, installed) > 0) State = CardState.UpdateAvailable;
            else State = CardState.UpToDate;

            VersionLine = installed == null ? $"Latest {info.Version}"
                : Util.CompareVersions(info.Version, installed) > 0 ? $"{installed}  \u2192  {info.Version}"
                : $"Installed {installed}";
            DevNote = "This folder is a git checkout (a development copy), so the hub never overwrites it. Update it with git, "
                + "or replace it with the release below (your changes would be lost - the hub keeps a backup).";

            switch (State)
            {
                case CardState.UpdateAvailable: Pill("Update available", "Gold"); ButtonText = $"Update to {info.Version}"; ButtonEnabled = true; break;
                case CardState.NotInstalled: Pill(info.Required ? "Recommended" : "Not installed", info.Required ? "Gold" : "TextDim"); ButtonText = "Install"; ButtonEnabled = true; break;
                case CardState.UpToDate: Pill("Up to date", "Accent"); ButtonText = "Up to date"; ButtonEnabled = false; break;
                case CardState.DevCopy: Pill("Dev copy", "TextDim"); ButtonText = "Managed by git"; ButtonEnabled = false; break;
                case CardState.NoClient: Pill("No client", "Danger"); ButtonText = $"{info.FlavorName ?? info.Flavor} not found"; ButtonEnabled = false; break;
                default: Pill("No WoW folder", "Danger"); ButtonText = "Choose your WoW folder in Settings"; ButtonEnabled = false; break;
            }

            // expanded view: the whole version history (the newer-than-yours ones first anyway)
            var log = info.Changelog ?? new List<ChangeEntry>();
            var hasNew = State == CardState.UpdateAvailable && installed != null && log.Any(c => Util.CompareVersions(c.Version, installed) > 0);
            ChangesTitle = hasNew ? "WHAT'S NEW  \u00B7  VERSION HISTORY" : "VERSION HISTORY";
            Changes = log.ToList();
            Notify(string.Empty); // refresh everything
        }

        public void SetBusy(bool on, string text = null)
        {
            Busy = on;
            if (on)
            {
                State = CardState.Busy;
                ButtonText = text ?? "Working...";
                ButtonEnabled = false;
                Progress = 0;
                Notify(nameof(ButtonText));
                Notify(nameof(ButtonEnabled));
            }
        }

        void Pill(string text, string color)
        {
            PillText = text;
            var c = ((SolidColorBrush)Application.Current.Resources[color]).Color;
            PillFg = new SolidColorBrush(c);
            PillBg = new SolidColorBrush(Color.FromArgb(0x26, c.R, c.G, c.B));
        }

        public event PropertyChangedEventHandler PropertyChanged;
        void Notify([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
