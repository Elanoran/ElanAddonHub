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

    // small icon + value shown in a card (tooltip explains it)
    public class StatChip
    {
        public string Glyph { get; }
        public string Text { get; }
        public string Tip { get; }
        public StatChip(string glyph, string text, string tip) { Glyph = glyph; Text = text; Tip = tip; }
        public static bool IsTransient(string m) => m != null && (m.StartsWith("Done!") || m.StartsWith("Installed!") || m.StartsWith("Rolled back"));
    }

    // Version strings that are really build ids ("Details.20260929.15300.172", "#Details.2026...") -> short readable form.
    public static class VersionText
    {
        public static string Pretty(string v, out string full)
        {
            full = v;
            if (string.IsNullOrWhiteSpace(v)) return "";
            var s = v.Trim().TrimStart('#').Trim();
            if (s.Length <= 14) return s;
            var m = System.Text.RegularExpressions.Regex.Match(s, @"(20\d{2})(\d{2})(\d{2})");
            var parts = s.Split('.');
            if (m.Success && parts.Length >= 3)
            {
                return m.Groups[1].Value + "-" + m.Groups[2].Value + "-" + m.Groups[3].Value;
            }
            return s.Substring(0, 12) + "…";
        }
    }

    // One addon card in the window.
    public class AddonCard : PillBase
    {
        public AddonInfo Info { get; private set; }
        public CardState State { get; private set; }
        public string Name => Info.Name;
        // per-addon artwork on the card (the paw stays as the source chip on the right)
        // card artwork: vector icons (default, Theme/AddonIcons.xaml) or the painted PNGs (setting "Card artwork")
        public static bool Painted;
        static object Res(string key) { try { return Application.Current.TryFindResource(key); } catch { return null; } }
        public object Logo
        {
            get
            {
                var folder = Info.Folders != null && Info.Folders.Count > 0 ? Info.Folders[0] : "";
                bool ehh = string.Equals(folder, "ElansHunterHelper", System.StringComparison.OrdinalIgnoreCase);
                bool hub = string.Equals(folder, "ElansHub", System.StringComparison.OrdinalIgnoreCase);
                bool pal = string.Equals(folder, "ElansPaladinHelper", System.StringComparison.OrdinalIgnoreCase);
                bool bags = string.Equals(folder, "ElansBags", System.StringComparison.OrdinalIgnoreCase);
                if (!Painted)
                {
                    var icon = Res(ehh ? "Icon.Hunter" : hub ? "Icon.Hub" : pal ? "Icon.Paladin" : bags ? "Icon.Bags" : "Icon.Hub");
                    if (icon != null) return icon;
                }
                if (ehh) return "pack://application:,,,/Assets/logo-ehh.png";
                if (hub) return "pack://application:,,,/Assets/logo-hubaddon.png";
                if (pal && PaladinLogoExists()) return "pack://application:,,,/Assets/logo-paladin.png";
                return "pack://application:,,,/Assets/hub.png";
            }
        }
        // the small source chip next to the status pill
        public object ChipArt => "pack://application:,,,/Assets/hub.png";   // the 16px chip stays the hub paw (brand)
        public void RefreshArt() { Notify(nameof(Logo)); Notify(nameof(ChipArt)); }
        // no paladin artwork yet: the card falls back to the hub icon until Assets\logo-paladin.png exists
        static bool? paladinLogo;
        static bool PaladinLogoExists()
        {
            if (paladinLogo == null)
            {
                try { paladinLogo = Application.GetResourceStream(new System.Uri("pack://application:,,,/Assets/logo-paladin.png")) != null; }
                catch { paladinLogo = false; }
            }
            return paladinLogo.Value;
        }

        // class of the character you play (from GamePresence), set by the main window; used for "Recommended for your Paladin"
        public static string ClassHint;
        public bool RecommendedForYou => Info.Classes != null && ClassHint != null
            && Info.Classes.Any(c => string.Equals(c, ClassHint, System.StringComparison.OrdinalIgnoreCase));
        string ClassName => ClassHint == null ? "" : char.ToUpperInvariant(ClassHint[0]) + ClassHint.Substring(1).ToLowerInvariant();
        public string Subtitle => State == CardState.NotInstalled && RecommendedForYou ? "Recommended for your " + ClassName
            : Info.Required && Info.Description != null ? Info.Description : Info.FlavorName ?? Info.Description ?? "";
        public string Installed { get; private set; }
        // hover the client name to see exactly where it installs
        public string InstallPath { get; private set; }
        public string Latest => Info.Version;
        public string ButtonText { get; private set; }
        public bool ButtonEnabled { get; private set; }
        public string ChangesTitle { get; private set; }
        public List<ChangeEntry> Changes { get; private set; }
        // compact one-liner: "1.17.0  >  1.18.0" / "Installed 1.18.0" / "Latest 1.18.0"
        public string VersionLine { get; private set; }
        public string DevNote { get; private set; }
        public string ReleasesUrl => GitHubStats.ReleasesUrl;

        // GitHub stats (filled in later, silently absent when GitHub can't be reached)
        public List<StatChip> Stats { get; private set; } = new List<StatChip>();
        public Visibility StatsVisibility => Stats.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        public void SetStats(AddonStats st)
        {
            var l = new List<StatChip>();
            if (st != null)
            {
                // Segoe MDL2 Assets: E121 clock, E896 download, E1CB tag
                if (st.Released != null) l.Add(new StatChip("\uE121", GitHubStats.ShortAgo(st.Released.Value),
                    "Released " + GitHubStats.Ago(st.Released.Value) + ", " + st.Released.Value.ToLocalTime().ToString("d MMM yyyy")));
                if (st.Downloads > 0) l.Add(new StatChip("\uE896", st.Downloads.ToString(), st.Downloads + (st.Downloads == 1 ? " download" : " downloads")));
                if (st.Releases > 0) l.Add(new StatChip("\uE1CB", st.Releases.ToString(), st.Releases + (st.Releases == 1 ? " release" : " releases")));
            }
            Stats = l;
            Notify(nameof(Stats)); Notify(nameof(StatsVisibility));
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
        System.Windows.Threading.DispatcherTimer fade;
        public string Message
        {
            get => message;
            set
            {
                message = value; Notify(); Notify(nameof(MessageVisibility));
                Failed = value != null && value.StartsWith("Something went wrong");
                if (fade != null) fade.Stop();
                if (StatChip.IsTransient(value))   // success hints fade after ~8 s, errors stay
                {
                    fade = new System.Windows.Threading.DispatcherTimer { Interval = System.TimeSpan.FromSeconds(8) };
                    var mine = value;
                    fade.Tick += (s, e) => { fade.Stop(); if (message == mine) { message = null; Notify(nameof(Message)); Notify(nameof(MessageVisibility)); } };
                    fade.Start();
                }
            }
        }
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
                case CardState.UpdateAvailable: ButtonText = $"Update to {info.Version}"; ButtonEnabled = true;
                    SetPill(ButtonText, PillKind.Action, $"Install {Name} {info.Version} (you have {installed}). Your settings are kept.", true); break;
                case CardState.NotInstalled: ButtonText = "Install"; ButtonEnabled = true;
                    SetPill("Install", PillKind.Action, $"Download and install {Name} {info.Version}", true); break;
                case CardState.UpToDate: ButtonText = "Up to date"; ButtonEnabled = false;
                    SetPill("Up to date", PillKind.Quiet, "You have the latest version", false); break;
                case CardState.DevCopy: ButtonText = "Managed by git"; ButtonEnabled = false;
                    SetPill("Dev copy", PillKind.Quiet, "A git checkout: the hub never overwrites it. Update it with git, or replace it from the details below.", false); break;
                case CardState.NoClient: ButtonText = $"{info.FlavorName ?? info.Flavor} not found"; ButtonEnabled = false;
                    SetPill("No client", PillKind.Danger, $"{info.FlavorName ?? info.Flavor} wasn't found in your WoW folder", false); break;
                default: ButtonText = "Choose your WoW folder in Settings"; ButtonEnabled = false;
                    SetPill("No WoW folder", PillKind.Danger, "Choose your WoW folder in Settings > General", false); break;
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
                SetPill(ButtonText, PillKind.Busy, "Working on it...", false);
                Progress = 0;
                Notify(string.Empty);
            }
        }
    }
}
