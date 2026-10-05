using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    public enum TpState { Unknown, Local, UpToDate, UpdateAvailable, ChooseFile, DevCopy, Busy }

    // A card for an addon the user installed from somewhere else.
    public class ThirdPartyCard : PillBase
    {
        public AddonEntry Entry { get; private set; }
        public AddonLink Link { get; private set; }
        public RemoteInfo Remote { get; set; }
        public TpState State { get; private set; } = TpState.Unknown;

        public string Name => Entry.Title;
        public string Letter => string.IsNullOrEmpty(Entry.Title) ? "?" : Entry.Title.Substring(0, 1).ToUpperInvariant();
        static bool KnownAuthor(string a) => !string.IsNullOrWhiteSpace(a) && !a.Trim().Equals("unknown", StringComparison.OrdinalIgnoreCase);
        public string Author => KnownAuthor(Entry.Author) ? Entry.Author : null;
        string PrettyLocal => VersionText.Pretty(Entry.Version, out _);
        public string Subtitle => string.Join("  ·  ", new[] { PrettyLocal, Author }.Where(x => !string.IsNullOrEmpty(x)));
        // full raw version on hover (only when it was shortened)
        public string SubtitleTip => string.IsNullOrEmpty(Entry.Version) || PrettyLocal == Entry.Version ? null : "Version: " + Entry.Version;
        public string Notes => Entry.Notes;
        public Visibility NotesVisibility => string.IsNullOrEmpty(Entry.Notes) ? Visibility.Collapsed : Visibility.Visible;
        public string FolderList => (Entry.Folders.Count == 1 ? "Folder: " : $"{Entry.Folders.Count} folders: ") + string.Join(", ", Entry.Folders);
        public string Source { get; private set; }          // badge text
        public Brush SourceFg { get; private set; }
        public Brush SourceBg { get; private set; }
        public string VersionLine { get; private set; }
        public string ButtonText { get; private set; }
        public bool ButtonEnabled { get; private set; }
        public Visibility VersionVisibility => State == TpState.UpdateAvailable || State == TpState.ChooseFile || State == TpState.Busy ? Visibility.Visible : Visibility.Collapsed;
        public string WebsiteUrl { get; private set; }
        public string WebsiteText { get; private set; }
        public Visibility WebsiteVisibility => WebsiteUrl == null ? Visibility.Collapsed : Visibility.Visible;
        public string CurseUrl { get; private set; }
        public Visibility CurseVisibility => CurseUrl == null ? Visibility.Collapsed : Visibility.Visible;
        public string WagoUrl { get; private set; }
        public Visibility WagoVisibility => WagoUrl == null ? Visibility.Collapsed : Visibility.Visible;
        public string DevNote => "This folder is a git checkout (a development copy), so the hub never updates it.";
        public Visibility DevVisibility => Entry.IsDev ? Visibility.Visible : Visibility.Collapsed;
        public Visibility LinkVisibility => Entry.IsDev || Entry.Cf != null ? Visibility.Collapsed : Visibility.Visible;
        public string LinkedTo { get; private set; }
        public Visibility UnlinkVisibility => Entry.Cf == null && (Link.Github != null || Link.Wowi != null) ? Visibility.Visible : Visibility.Collapsed;
        public Visibility RollbackVisibility => !Entry.IsDev && Entry.Cf == null && AddonSources.Backups(Entry.Key).Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility CfVisibility => Entry.Cf != null ? Visibility.Visible : Visibility.Collapsed;
        public DateTime CfChecked { get; set; } = DateTime.MinValue;   // when CurseForge last refreshed its update info (UTC)
        public List<string> Choices => Remote?.Choices ?? new List<string>();
        public Visibility ChoiceVisibility => State == TpState.ChooseFile ? Visibility.Visible : Visibility.Collapsed;
        public string SearchText => (Entry.Title + " " + string.Join(" ", Entry.Folders) + " " + Entry.Author).ToLowerInvariant();

        // automatic match for an unlinked addon (confirmed by the user with one click)
        public Suggestion Suggested { get; set; }
        public bool ShowSuggestion => Suggested != null && !Entry.IsDev && State == TpState.Local
            && string.IsNullOrEmpty(Link.Github) && string.IsNullOrEmpty(Link.Wowi);
        public Visibility SuggestVisibility => ShowSuggestion ? Visibility.Visible : Visibility.Collapsed;
        public string SuggestText => Suggested == null ? "" : "Suggested: " + Suggested.Text;
        public string SuggestHint => Suggested == null ? "" : "Confidence: " + Suggested.Hint + ". Nothing is installed until you click Use this.";

        // text typed into "Link source"
        public string LinkInput { get; set; }

        bool expanded;
        public bool Expanded { get => expanded; set { expanded = value; Notify(); Notify(nameof(ExpandedVisibility)); Notify(nameof(Chevron)); } }
        public Visibility ExpandedVisibility => expanded ? Visibility.Visible : Visibility.Collapsed;
        public string Chevron => expanded ? "" : "";

        double progress;
        public double Progress { get => progress; set { progress = value; Notify(); } }
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
                if (StatChip.IsTransient(value))
                {
                    fade = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
                    var mine = value;
                    fade.Tick += (s, e) => { fade.Stop(); if (message == mine) { message = null; Notify(nameof(Message)); Notify(nameof(MessageVisibility)); } };
                    fade.Start();
                }
            }
        }
        public Visibility MessageVisibility => string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        public Visibility BusyVisibility => State == TpState.Busy ? Visibility.Visible : Visibility.Collapsed;

        public ThirdPartyCard(AddonEntry e) { Entry = e; Link = AddonSources.LinkFor(e.Key); Refresh(); }

        public void SetEntry(AddonEntry e) { Entry = e; Refresh(); }

        public void SetRemote(RemoteInfo r, int iface)
        {
            Remote = r;
            Refresh();
        }

        public bool IsBusy => State == TpState.Busy;
        TpState before;
        public void SetBusy(bool on, string text = null)
        {
            if (on) { before = State; State = TpState.Busy; ButtonText = text ?? "Working..."; ButtonEnabled = false; SetPill(ButtonText, PillKind.Busy, "Working on it...", false); Progress = 0; Notify(string.Empty); }
            else { Refresh(); }
        }

        public void Refresh()
        {
            var r = Remote;
            var hasLink = !string.IsNullOrEmpty(Link.Github ?? Entry.GithubRepo) || !string.IsNullOrEmpty(Link.Wowi ?? Entry.WowiId);
            if (Entry.IsDev) State = TpState.DevCopy;
            else if (Entry.Cf != null) State = Entry.Cf.UpdateAvailable ? TpState.UpdateAvailable : TpState.UpToDate;
            else if (r == null) State = hasLink ? TpState.Unknown : TpState.Local;
            else if (r.NeedsChoice) State = TpState.ChooseFile;
            else if (AddonSources.IsNewer(Entry, Link, r)) State = TpState.UpdateAvailable;
            else State = TpState.UpToDate;

            var gh = Link.Github ?? Entry.GithubRepo;
            var wid = Link.Wowi ?? Entry.WowiId;
            if (Entry.Cf != null)
            {
                Source = "CurseForge"; SetBadge("Accent"); WebsiteText = "CurseForge page";
                WebsiteUrl = Entry.Cf.Url != null && Entry.Cf.Url.StartsWith("https://") ? Entry.Cf.Url : null;
                LinkedTo = "Managed by the CurseForge app: updates are installed by CurseForge, the hub never changes these folders.";
            }
            else if (!string.IsNullOrEmpty(gh)) { Source = "GitHub"; SetBadge("Accent"); WebsiteUrl = "https://github.com/" + gh; WebsiteText = "Open GitHub"; LinkedTo = "Updates from github.com/" + gh; }
            else if (!string.IsNullOrEmpty(wid)) { Source = "WoWInterface"; SetBadge("Accent"); WebsiteUrl = $"https://www.wowinterface.com/downloads/info{wid}"; WebsiteText = "Open WoWInterface"; LinkedTo = "Updates from WoWInterface #" + wid; }
            else if (!string.IsNullOrEmpty(Entry.CurseId)) { Source = "CurseForge"; SetBadge("TextDim"); WebsiteUrl = null; LinkedTo = "No automatic updates (CurseForge) - paste a GitHub or WoWInterface link to enable them"; }
            else if (!string.IsNullOrEmpty(Entry.WagoId)) { Source = "Wago"; SetBadge("TextDim"); WebsiteUrl = null; LinkedTo = "No automatic updates (Wago) - paste a GitHub or WoWInterface link to enable them"; }
            else { Source = "Local"; SetBadge("Gold"); WebsiteUrl = Entry.Website != null && Entry.Website.StartsWith("https://") ? Entry.Website : null; WebsiteText = "Open website"; LinkedTo = "No source linked - paste a GitHub or WoWInterface link below"; }
            if (WebsiteUrl == null && Entry.Website != null && Entry.Website.StartsWith("https://") && Source != "Local") { WebsiteUrl = Entry.Website; WebsiteText = "Open website"; }
            CurseUrl = Entry.Cf != null || string.IsNullOrEmpty(Entry.CurseId) ? null : "https://www.curseforge.com/projects/" + Entry.CurseId;
            WagoUrl = string.IsNullOrEmpty(Entry.WagoId) ? null : "https://addons.wago.io/addons/" + Entry.WagoId;

            var local = PrettyLocal;
            if (Entry.Cf != null && !Entry.IsDev)
            {
                var cf = Entry.Cf;
                var seen = $"checked {CurseForgeLocal.Ago(CfChecked)}" + (CurseForgeLocal.IsStale(CfChecked) ? " - old, press Check CurseForge now" : "");
                if (State == TpState.UpdateAvailable)
                {
                    VersionLine = $"Update available: {cf.LatestFileName}" + (cf.LatestDate > DateTime.MinValue.AddDays(2) ? ", " + cf.LatestDate.ToLocalTime().ToString("d MMM yyyy") : "") + $" ({seen})";
                    ButtonText = "Update via CurseForge"; ButtonEnabled = true;
                    SetPill("Update via CurseForge", PillKind.Action, "Ask the CurseForge app to install the update (the hub never changes CurseForge-managed folders itself)", true);
                }
                else { SetPill("Up to date", PillKind.Quiet, "You have the latest version", false); VersionLine = $"Up to date ({seen})"; ButtonText = ""; ButtonEnabled = false; }
                Notify(string.Empty);
                return;
            }
            switch (State)
            {
                case TpState.DevCopy: SetPill("Dev copy", PillKind.Quiet, "A git checkout: the hub never updates it. Use git.", false); VersionLine = "Managed by git"; ButtonText = ""; ButtonEnabled = false; break;
                case TpState.UpdateAvailable:
                    VersionLine = $"{VersionText.Pretty(Link.InstalledRemote ?? local, out _)}  →  {VersionText.Pretty(r.Version, out _)}";
                    ButtonText = "Update"; ButtonEnabled = true;
                    SetPill("Update", PillKind.Action, $"Install {VersionText.Pretty(r.Version, out _)} over your version. A backup is kept so you can roll back.", true); break;
                case TpState.ChooseFile:
                    VersionLine = $"Latest {r.Version} - no WoW Forever build found, pick a download"; ButtonText = "Update"; ButtonEnabled = Link.Asset != null;
                    if (Link.Asset != null) SetPill("Update", PillKind.Action, $"Install {Link.Asset}", true);
                    else SetPill("Choose file", PillKind.Action, "No download is marked for WoW Forever. Click to pick one from the list.", true);
                    break;
                case TpState.UpToDate: SetPill("Up to date", PillKind.Quiet, "You have the latest version", false); VersionLine = "Installed " + (local == "" ? (Link.InstalledRemote ?? "?") : local); ButtonText = ""; ButtonEnabled = false; break;
                case TpState.Unknown: SetPill(null, PillKind.Quiet, null, false); VersionLine = "Installed " + (local == "" ? "?" : local) + "  ·  checking..."; ButtonText = ""; ButtonEnabled = false; break;
                default: SetPill(null, PillKind.Quiet, null, false); VersionLine = "Installed " + (local == "" ? "?" : local); ButtonText = ""; ButtonEnabled = false; break;
            }
            Notify(string.Empty);
        }

        // Source icon: CurseForge = icon of the user's installed app, GitHub = Octicons mark, Local = hand-installed glyph (gold); WoWInterface/Wago = letter chips.
        public ImageSource SourceImage => Source == "CurseForge" ? SourceIcons.CurseForge() : null;
        public Geometry SourceGeometry => Source == "GitHub" ? SourceIcons.GitHub : Source == "Local" ? SourceIcons.Manual : null;
        public string SourceChip => Source == "WoWInterface" ? "WoWI" : Source == "Wago" ? "Wago" : (SourceImage == null && SourceGeometry == null ? Source : null);
        public Visibility SourceImageVis => SourceImage != null ? Visibility.Visible : Visibility.Collapsed;
        public Visibility SourceGeoVis => SourceGeometry != null ? Visibility.Visible : Visibility.Collapsed;
        public Visibility SourceChipVis => SourceChip != null ? Visibility.Visible : Visibility.Collapsed;
        public Brush SourceGlyphBrush => (Brush)Application.Current.Resources[Source == "Local" ? "Gold" : "Text"];
        public string SourceTip => Source == "Local" ? "Installed manually - link a GitHub or WoWInterface page to get updates" : "Source: " + Source;

        void SetBadge(string color)
        {
            var c = ((SolidColorBrush)Application.Current.Resources[color]).Color;
            SourceFg = new SolidColorBrush(c);
            SourceBg = new SolidColorBrush(Color.FromArgb(0x26, c.R, c.G, c.B));
        }

    }
}
