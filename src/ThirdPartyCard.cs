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
    public class ThirdPartyCard : INotifyPropertyChanged
    {
        public AddonEntry Entry { get; private set; }
        public AddonLink Link { get; private set; }
        public RemoteInfo Remote { get; set; }
        public TpState State { get; private set; } = TpState.Unknown;

        public string Name => Entry.Title;
        public string Letter => string.IsNullOrEmpty(Entry.Title) ? "?" : Entry.Title.Substring(0, 1).ToUpperInvariant();
        public string Author => string.IsNullOrEmpty(Entry.Author) ? "unknown author" : Entry.Author;
        public string Subtitle => $"{(string.IsNullOrEmpty(Entry.Version) ? "no version" : Entry.Version)}  ·  {Author}";
        public string Notes => Entry.Notes;
        public Visibility NotesVisibility => string.IsNullOrEmpty(Entry.Notes) ? Visibility.Collapsed : Visibility.Visible;
        public string FolderList => (Entry.Folders.Count == 1 ? "Folder: " : $"{Entry.Folders.Count} folders: ") + string.Join(", ", Entry.Folders);
        public string Source { get; private set; }          // badge text
        public Brush SourceFg { get; private set; }
        public Brush SourceBg { get; private set; }
        public string VersionLine { get; private set; }
        public string PillText { get; private set; }
        public Brush PillFg { get; private set; }
        public Brush PillBg { get; private set; }
        public Visibility PillVisibility => string.IsNullOrEmpty(PillText) ? Visibility.Collapsed : Visibility.Visible;
        public string ButtonText { get; private set; }
        public bool ButtonEnabled { get; private set; }
        public Visibility ButtonVisibility => State == TpState.UpdateAvailable || State == TpState.ChooseFile || State == TpState.Busy ? Visibility.Visible : Visibility.Collapsed;
        public string WebsiteUrl { get; private set; }
        public string WebsiteText { get; private set; }
        public Visibility WebsiteVisibility => WebsiteUrl == null ? Visibility.Collapsed : Visibility.Visible;
        public string CurseUrl { get; private set; }
        public Visibility CurseVisibility => CurseUrl == null ? Visibility.Collapsed : Visibility.Visible;
        public string WagoUrl { get; private set; }
        public Visibility WagoVisibility => WagoUrl == null ? Visibility.Collapsed : Visibility.Visible;
        public string DevNote => "This folder is a git checkout (a development copy), so the hub never updates it.";
        public Visibility DevVisibility => Entry.IsDev ? Visibility.Visible : Visibility.Collapsed;
        public Visibility LinkVisibility => Entry.IsDev ? Visibility.Collapsed : Visibility.Visible;
        public string LinkedTo { get; private set; }
        public Visibility UnlinkVisibility => Link.Github != null || Link.Wowi != null ? Visibility.Visible : Visibility.Collapsed;
        public Visibility RollbackVisibility => !Entry.IsDev && AddonSources.Backups(Entry.Key).Count > 0 ? Visibility.Visible : Visibility.Collapsed;
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
        public string Message { get => message; set { message = value; Notify(); Notify(nameof(MessageVisibility)); } }
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
            if (on) { before = State; State = TpState.Busy; ButtonText = text ?? "Working..."; ButtonEnabled = false; Progress = 0; Notify(string.Empty); }
            else { Refresh(); }
        }

        public void Refresh()
        {
            var r = Remote;
            var hasLink = !string.IsNullOrEmpty(Link.Github ?? Entry.GithubRepo) || !string.IsNullOrEmpty(Link.Wowi ?? Entry.WowiId);
            if (Entry.IsDev) State = TpState.DevCopy;
            else if (r == null) State = hasLink ? TpState.Unknown : TpState.Local;
            else if (r.NeedsChoice) State = TpState.ChooseFile;
            else if (AddonSources.IsNewer(Entry, Link, r)) State = TpState.UpdateAvailable;
            else State = TpState.UpToDate;

            var gh = Link.Github ?? Entry.GithubRepo;
            var wid = Link.Wowi ?? Entry.WowiId;
            if (!string.IsNullOrEmpty(gh)) { Source = "GitHub"; SetBadge("Accent"); WebsiteUrl = "https://github.com/" + gh; WebsiteText = "Open GitHub"; LinkedTo = "Updates from github.com/" + gh; }
            else if (!string.IsNullOrEmpty(wid)) { Source = "WoWInterface"; SetBadge("Accent"); WebsiteUrl = $"https://www.wowinterface.com/downloads/info{wid}"; WebsiteText = "Open WoWInterface"; LinkedTo = "Updates from WoWInterface #" + wid; }
            else if (!string.IsNullOrEmpty(Entry.CurseId)) { Source = "CurseForge"; SetBadge("TextDim"); WebsiteUrl = null; LinkedTo = "No automatic updates (CurseForge) - paste a GitHub or WoWInterface link to enable them"; }
            else if (!string.IsNullOrEmpty(Entry.WagoId)) { Source = "Wago"; SetBadge("TextDim"); WebsiteUrl = null; LinkedTo = "No automatic updates (Wago) - paste a GitHub or WoWInterface link to enable them"; }
            else { Source = "Local"; SetBadge("TextDim"); WebsiteUrl = Entry.Website != null && Entry.Website.StartsWith("https://") ? Entry.Website : null; WebsiteText = "Open website"; LinkedTo = "No source linked - paste a GitHub or WoWInterface link below"; }
            if (WebsiteUrl == null && Entry.Website != null && Entry.Website.StartsWith("https://") && Source != "Local") { WebsiteUrl = Entry.Website; WebsiteText = "Open website"; }
            CurseUrl = string.IsNullOrEmpty(Entry.CurseId) ? null : "https://www.curseforge.com/projects/" + Entry.CurseId;
            WagoUrl = string.IsNullOrEmpty(Entry.WagoId) ? null : "https://addons.wago.io/addons/" + Entry.WagoId;

            var local = string.IsNullOrEmpty(Entry.Version) ? "" : Entry.Version;
            switch (State)
            {
                case TpState.DevCopy: Pill("Dev copy", "TextDim"); VersionLine = "Managed by git"; ButtonText = ""; ButtonEnabled = false; break;
                case TpState.UpdateAvailable:
                    Pill("Update available", "Gold");
                    VersionLine = $"{(Link.InstalledRemote ?? local)}  →  {r.Version}";
                    ButtonText = "Update"; ButtonEnabled = true; break;
                case TpState.ChooseFile: Pill("Choose file", "Gold"); VersionLine = $"Latest {r.Version} - no WoW Forever build found, pick a download"; ButtonText = "Update"; ButtonEnabled = Link.Asset != null; break;
                case TpState.UpToDate: Pill("Up to date", "Accent"); VersionLine = "Installed " + (local == "" ? (Link.InstalledRemote ?? "?") : local); ButtonText = ""; ButtonEnabled = false; break;
                case TpState.Unknown: Pill(null, "TextDim"); VersionLine = "Installed " + (local == "" ? "?" : local) + "  ·  checking..."; ButtonText = ""; ButtonEnabled = false; break;
                default: Pill(null, "TextDim"); VersionLine = "Installed " + (local == "" ? "?" : local); ButtonText = ""; ButtonEnabled = false; break;
            }
            Notify(string.Empty);
        }

        void SetBadge(string color)
        {
            var c = ((SolidColorBrush)Application.Current.Resources[color]).Color;
            SourceFg = new SolidColorBrush(c);
            SourceBg = new SolidColorBrush(Color.FromArgb(0x26, c.R, c.G, c.B));
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
