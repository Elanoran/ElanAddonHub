using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using ElansAddonHub.Services;
using Elans.Shared;

namespace ElansAddonHub.Lodge
{
    // one kept version of a slot, as the server lists it
    public sealed class SlotVersionInfo
    {
        public int V;
        public string FileName, Sha256, UploadedBy, Note;
        public long Size, At;

        // a files.data slot's version (typed: shared/Elans.Shared/Lodge/FileMessages.cs FileVersion)
        public static SlotVersionInfo From(FileVersion d) => d == null ? null : new SlotVersionInfo
        {
            V = d.V, FileName = SafeFile.Name(d.FileName), Size = d.Size, Sha256 = (d.Sha256 ?? "").ToLowerInvariant(),
            UploadedBy = d.UploadedBy ?? "", At = d.At, Note = d.Note ?? "",
        };
    }

    // one row of the versions list
    public sealed class VersionVM
    {
        public SlotVM Slot;
        public SlotVersionInfo Info;
        public bool IsNewest, CanRollBack;
        public string Title => "v" + Info.V + (IsNewest ? " (newest)" : "");
        public string Line => Info.FileName + " · " + LodgeFiles.Size(Info.Size);
        public string By => DisplayNames.Shown(Info.UploadedBy) + " · " + LodgeFiles.Stamp(Info.At);
        public string Note => Info.Note;
        public Visibility NoteVisibility => string.IsNullOrEmpty(Info.Note) ? Visibility.Collapsed : Visibility.Visible;
        public Visibility RollBackVisibility => CanRollBack ? Visibility.Visible : Visibility.Collapsed;
    }

    // one slot of the Files page
    public sealed class SlotVM : Bindable
    {
        public string Id { get; private set; }
        public string Name { get; private set; }
        public string CreatedBy { get; private set; }
        public List<SlotVersionInfo> Versions { get; private set; } = new List<SlotVersionInfo>();
        public bool CanManage { get; set; }
        string badge = "", error = "";
        double progress = -1;           // -1 = idle, 0..1 = an upload / download is running for this slot
        string busyText = "";

        public SlotVersionInfo Newest => Versions.OrderBy(v => v.V).LastOrDefault();

        public void Update(FileSlot d, bool canManage, SavedRecord saved, InstalledRecord installed = null, bool addonsOn = false)
        {
            Id = d.Id; Name = d.Name ?? ""; CreatedBy = d.CreatedBy ?? "";
            Versions = d.Versions.Select(SlotVersionInfo.From).Where(v => v != null).OrderBy(v => v.V).ToList();
            CanManage = canManage;
            CanUpdate = canManage || d.CanUpdate;
            Desc = d.Desc ?? ""; See = d.See; EditRank = d.Edit;
            if (CoverV != d.Cover) { CoverV = d.Cover; if (d.Cover <= 0) CoverImage = null; }
            IsAddon = addonsOn && d.IsAddon;
            Badge = LodgeFiles.Badge(Newest, saved);
            SetInstalled(IsAddon ? installed : null, Newest);
            RaiseAll();
        }

        // ---- 2.33: description, who sees / updates it, the cover picture
        public bool CanUpdate { get; private set; }
        public string Desc { get; private set; } = "";
        public string See { get; private set; }
        public string EditRank { get; private set; }
        public int CoverV { get; private set; }
        System.Windows.Media.ImageSource cover;
        public System.Windows.Media.ImageSource CoverImage { get => cover; set { cover = value; Raise(nameof(CoverImage)); Raise(nameof(CoverVisibility)); Raise(nameof(PlaceholderVisibility)); Raise(nameof(CoverHeight)); } }
        public double CoverHeight => cover != null ? 132 : 56;            // a slim strip with the kind's icon when there is no picture
        public Visibility CoverVisibility => cover != null ? Visibility.Visible : Visibility.Collapsed;
        public Visibility PlaceholderVisibility => cover == null ? Visibility.Visible : Visibility.Collapsed;
        public string KindGlyph => IsAddon ? "\uE74C" : "\uE8A5";        // a puzzle piece for addons, a page for files
        public string DescShort => Desc.Split('\n')[0];
        public Visibility DescVisibility => Desc.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        public static string SeeText(string r) => r == "member" ? "Members and up" : r == "veteran" ? "Veterans and up" : r == "officer" ? "Officers only" : "Everyone, guests too";
        public static string EditText(string r) => r == "member" ? "Members and up" : r == "veteran" ? "Veterans and up" : "Officers";
        public string SeeLine => SeeText(See);
        public string EditLine => EditText(EditRank);
        public string SeeChip => See == "member" ? "Members" : See == "veteran" ? "Veterans" : See == "officer" ? "Officers" : "";
        public Visibility SeeChipVisibility => SeeChip.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility UpdateVisibility => CanUpdate ? Visibility.Visible : Visibility.Collapsed;
        public Visibility SaveCardVisibility => IsAddon ? Visibility.Collapsed : Visibility.Visible;

        // ---- addons (Lodge 2.32): installed in WoW from this slot, and whether it keeps itself updated
        public bool IsAddon { get; private set; }
        public int InstalledV { get; private set; }
        public bool AutoUpdate { get; private set; }
        public void SetInstalled(InstalledRecord r, SlotVersionInfo newest)
        {
            InstalledV = r?.V ?? 0; AutoUpdate = r?.Auto ?? false;
            if (InstalledV > 0 && newest != null && newest.V > InstalledV) Badge = "Update";
            else if (InstalledV > 0 && Badge == "New") Badge = "";
            foreach (var n in new[] { nameof(IsAddon), nameof(InstalledV), nameof(AutoUpdate), nameof(InstallVisibility), nameof(InstallLabel), nameof(InstallTip), nameof(InstalledLine),
                nameof(InstalledLineVisibility), nameof(AutoVisibility), nameof(AddonChipVisibility) }) Raise(n);
        }
        public Visibility InstallVisibility => IsAddon ? Visibility.Visible : Visibility.Collapsed;
        public Visibility AddonChipVisibility => IsAddon ? Visibility.Visible : Visibility.Collapsed;
        public string InstallLabel => InstalledV == 0 ? "Install in WoW" : Newest != null && Newest.V > InstalledV ? "Update in WoW" : "Reinstall";
        public string InstallTip => "Puts this addon into WoW Forever's AddOns folder (what is there now is backed up first)";
        public string InstalledLine => InstalledV == 0 ? "" : "In WoW: v" + InstalledV + (Newest != null && Newest.V > InstalledV ? " - v" + Newest.V + " is out" : "") + (AutoUpdate ? " · keeps itself updated" : "");
        public Visibility InstalledLineVisibility => InstalledV > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility AutoVisibility => InstalledV > 0 ? Visibility.Visible : Visibility.Collapsed;

        public void RaiseAll()
        {
            foreach (var n in new[] { nameof(Name), nameof(VersionLine), nameof(MetaLine), nameof(Note), nameof(NoteVisibility), nameof(ManageVisibility), nameof(HasOlderVisibility),
                nameof(VersionsLabel), nameof(ProgressVisibility), nameof(Progress), nameof(BusyText), nameof(IdleEnabled),
                nameof(CanUpdate), nameof(Desc), nameof(DescShort), nameof(DescVisibility), nameof(SeeLine), nameof(EditLine), nameof(SeeChip), nameof(SeeChipVisibility),
                nameof(UpdateVisibility), nameof(SaveCardVisibility), nameof(KindGlyph), nameof(CoverVisibility), nameof(PlaceholderVisibility) }) Raise(n);
        }

        public string Badge { get => badge; set { if (Set(ref badge, value ?? "")) { Raise(nameof(BadgeVisibility)); Raise(nameof(BadgeTip)); } } }
        public Visibility BadgeVisibility => badge.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        public string BadgeTip => badge == "New" ? "You haven't saved this one on this PC yet" : badge == "Update" ? "A newer version than the one installed in WoW" : "A newer version than the one you saved on this PC";

        public string VersionLine => Newest == null ? "" : "v" + Newest.V + " · " + Newest.FileName + " · " + LodgeFiles.Size(Newest.Size);
        public string MetaLine => Newest == null ? "" : DisplayNames.Shown(Newest.UploadedBy) + " · " + LodgeFiles.Stamp(Newest.At);
        public string Note => Newest?.Note ?? "";
        public Visibility NoteVisibility => Note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ManageVisibility => CanManage ? Visibility.Visible : Visibility.Collapsed;
        public Visibility HasOlderVisibility => Versions.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        public string VersionsLabel => Versions.Count > 1 ? "Versions (" + Versions.Count + ")" : "Versions";

        public double Progress { get => progress < 0 ? 0 : progress; set { if (Set(ref progress, value)) { Raise(nameof(ProgressVisibility)); Raise(nameof(IdleEnabled)); Raise(nameof(Progress)); } } }
        public bool Busy => progress >= 0;
        public Visibility ProgressVisibility => progress >= 0 ? Visibility.Visible : Visibility.Collapsed;
        public bool IdleEnabled => progress < 0;
        public string BusyText { get => busyText; set => Set(ref busyText, value ?? ""); }
        public void SetIdle() { progress = -1; busyText = ""; RaiseAll(); }

        public string Error { get => error; set { if (Set(ref error, value ?? "")) Raise(nameof(ErrorVisibility)); } }
        public Visibility ErrorVisibility => error.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        public List<VersionVM> VersionRows() =>
            Versions.OrderByDescending(v => v.V).Select(v => new VersionVM { Slot = this, Info = v, IsNewest = v.V == Newest?.V, CanRollBack = CanUpdate && v.V != Newest?.V }).ToList();
    }
}
