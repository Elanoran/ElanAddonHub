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
        public List<AddonFolder> Addon = new List<AddonFolder>();      // 2.35: the addon folders as their tocs say

        // a files.data slot's version (typed: shared/Elans.Shared/Lodge/FileMessages.cs FileVersion)
        public static SlotVersionInfo From(FileVersion d) => d == null ? null : new SlotVersionInfo
        {
            V = d.V, FileName = SafeFile.Name(d.FileName), Size = d.Size, Sha256 = (d.Sha256 ?? "").ToLowerInvariant(),
            UploadedBy = d.UploadedBy ?? "", At = d.At, Note = d.Note ?? "", Addon = d.Addon ?? new List<AddonFolder>(),
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

        public void Update(FileSlot d, bool canManage, SavedRecord saved, InstalledRecord installed = null, bool addonsOn = false, bool textOn = false)
        {
            Id = d.Id; Name = d.Name ?? ""; CreatedBy = d.CreatedBy ?? "";
            Versions = d.Versions.Select(SlotVersionInfo.From).Where(v => v != null).OrderBy(v => v.V).ToList();
            CanManage = canManage;
            CanUpdate = canManage || d.CanUpdate;
            Desc = d.Desc ?? ""; See = d.See; EditRank = d.Edit; Cat = d.Cat;
            if (CoverV != d.Cover) { CoverV = d.Cover; if (d.Cover <= 0) CoverImage = null; }
            IsAddon = addonsOn && d.IsAddon;
            IsText = textOn && d.IsText;
            HaveNewest = d.HaveNewest; HaveOlder = d.HaveOlder;
            Badge = LodgeFiles.Badge(Newest, saved);
            SetInstalled(IsAddon ? installed : null, Newest);
            RaiseAll();
        }

        // ---- 2.33: description, who sees / updates it, the cover picture
        public bool CanUpdate { get; private set; }
        public string Desc { get; private set; } = "";
        public string See { get; private set; }
        public string EditRank { get; private set; }
        public string Cat { get; private set; }
        // the filter a card belongs to: addons by their kind, texts by what they are (3.5.4: a macro, a WeakAura or profile, a talent build, other
        // import strings), the rest by the category the Lodge says ("" = none)
        public string Filter => IsAddon ? "addon" : IsText ? ImportText.Group(TextKind) : Cat ?? "";
        public static string CatLabel(string c) => c == "addon" ? "Addons" : c == "profile" ? "WeakAuras & profiles" : c == "macro" ? "Macros" : c == "text" ? "Import strings" : c == "ui" ? "UI packs"
            : c == "guide" ? "Guides" : c == "map" ? "Maps" : c == "build" ? "Talent builds" : "Other";
        // 3.5.4: the Library's groups, in this order (a slot without a category is Other)
        public static readonly string[] GroupOrder = { "addon", "profile", "macro", "build", "text", "ui", "guide", "map", "other" };
        public string GroupKey => Filter.Length == 0 ? "other" : Filter;
        public int GroupIndex => Math.Max(0, Array.IndexOf(GroupOrder, GroupKey));
        public string GroupLabel => CatLabel(GroupKey);

        // 3.5.4: what the text is, once its newest version has been read (Copy, the preview, or the page's look-ahead); null until then
        string kindOf, kindSha;
        public string TextKind => IsText && Newest != null && kindSha == Newest.Sha256 ? kindOf : null;
        public bool NeedsTextKind => IsText && Newest != null && TextKind == null;
        public void SetText(string text)
        {
            if (Newest == null) return;
            kindOf = ImportText.Kind(text); kindSha = Newest.Sha256;
            RaiseAll();
        }
        public int CoverV { get; private set; }
        System.Windows.Media.ImageSource cover;
        public System.Windows.Media.ImageSource CoverImage { get => cover; set { cover = value; Raise(nameof(CoverImage)); Raise(nameof(CoverVisibility)); Raise(nameof(PlaceholderVisibility)); Raise(nameof(CoverHeight)); } }
        public double CoverHeight => cover != null ? 132 : 56;            // a slim strip with the kind's icon when there is no picture
        public Visibility CoverVisibility => cover != null ? Visibility.Visible : Visibility.Collapsed;
        public Visibility PlaceholderVisibility => cover == null ? Visibility.Visible : Visibility.Collapsed;
        public bool IsText { get; private set; }

        // ---- the Details view's columns (3.5.2)
        public string KindText => IsAddon ? "Addon" : IsText ? ImportText.Label(TextKind) : Cat != null ? CatLabel(Cat).TrimEnd('s') : Newest == null ? "" : (Elans.Shared.DangerousFiles.Extension(Newest.FileName).ToUpperInvariant() + " file").Trim();
        public string VersionText => Newest == null ? "" : "v" + Newest.V;
        public string SizeText => Newest == null ? "" : LodgeFiles.Size(Newest.Size);
        public long SizeBytes => Newest?.Size ?? 0;
        public string UpdatedText => Newest == null ? "" : LodgeFiles.Stamp(Newest.At);
        public long UpdatedAt => Newest?.At ?? 0;
        public string ByText => Newest == null ? "" : DisplayNames.Shown(Newest.UploadedBy);
        public string InWowText => !IsAddon ? "" : InstalledV == 0 ? "-" : Newest != null && Newest.V > InstalledV ? "v" + InstalledV + " → v" + Newest.V : "v" + InstalledV + (AutoUpdate ? " · auto" : "");
        bool selected;
        public bool IsSelected { get => selected; set { if (Set(ref selected, value)) Raise(nameof(RowBackground)); } }
        public System.Windows.Media.Brush RowBackground => (System.Windows.Media.Brush)Application.Current.Resources[selected ? "Brush.Surface3" : "Brush.Surface2"];

        // ---- 2.35: the newest addon version's folders against this PC (the client's Interface, the AddOns folder), and who has it
        public int HaveNewest { get; private set; }
        public int HaveOlder { get; private set; }
        public string AddonInfo { get; private set; } = "";
        public string AddonWarn { get; private set; } = "";
        public Visibility AddonInfoVisibility => IsAddon && AddonInfo.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility AddonWarnVisibility => IsAddon && AddonWarn.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        public string HaveLine => !IsAddon || HaveNewest + HaveOlder == 0 ? "" : (HaveNewest == 1 ? "1 member has" : HaveNewest + " members have") + " the newest" + (HaveOlder > 0 ? " · " + HaveOlder + " an older version" : "");
        public Visibility HaveVisibility => HaveLine.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        public void SetAddonContext(int clientInterface, Func<string, bool> haveFolder)
        {
            var folders = Newest?.Addon ?? new List<AddonFolder>();
            var lines = folders.Select(f => f.F + (f.V.Length > 0 ? " " + f.V : "") + (f.I.Count > 0 ? " · for " + string.Join(", ", f.I) : ""));
            AddonInfo = string.Join("\n", lines);
            var warn = new List<string>();
            if (clientInterface > 0)
            {
                var off = folders.Where(f => f.I.Count > 0 && !f.I.Contains(clientInterface)).Select(f => f.F + " (" + string.Join(", ", f.I) + ")").ToList();
                if (off.Count > 0) warn.Add("Built for another game version: " + string.Join(", ", off) + ". WoW Forever is " + clientInterface + " - it may show as out of date in game (tick \"Load out of date AddOns\").");
            }
            if (haveFolder != null)
            {
                var own = new HashSet<string>(folders.Select(f => f.F), StringComparer.OrdinalIgnoreCase);
                var missing = folders.SelectMany(f => f.D).Where(d => !own.Contains(d) && !haveFolder(d)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (missing.Count > 0) warn.Add("Needs " + string.Join(", ", missing) + ", which " + (missing.Count == 1 ? "isn't" : "aren't") + " in your AddOns folder.");
            }
            AddonWarn = string.Join(" ", warn);
            foreach (var n in new[] { nameof(AddonInfo), nameof(AddonWarn), nameof(AddonInfoVisibility), nameof(AddonWarnVisibility), nameof(HaveLine), nameof(HaveVisibility) }) Raise(n);
        }
        public string KindGlyph => IsAddon ? "\uE74C" : IsText ? "\uE8C8" : "\uE8A5";        // a puzzle piece for addons, copy for import text, a page for files
        public string KindChip => IsAddon ? "Addon" : IsText ? ImportText.Label(TextKind) : "";
        public string KindChipTip => IsAddon ? "A WoW addon: it can be installed in WoW Forever from here" : "Text to copy: Copy puts it on the clipboard, then " + ImportText.PasteHint(TextKind);
        public Visibility KindChipVisibility => KindChip.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility CopyVisibility => IsText ? Visibility.Visible : Visibility.Collapsed;
        string preview = "";
        public string TextPreview { get => preview; set { preview = value ?? ""; Raise(nameof(TextPreview)); Raise(nameof(PreviewVisibility)); } }
        public Visibility PreviewVisibility => IsText && preview.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        public string DescShort => Desc.Split('\n')[0];
        public Visibility DescVisibility => Desc.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        public static string SeeText(string r) => r == "member" ? "Members and up" : r == "veteran" ? "Veterans and up" : r == "officer" ? "Officers only" : "Everyone, guests too";
        public static string EditText(string r) => r == "member" ? "Members and up" : r == "veteran" ? "Veterans and up" : "Officers";
        public string SeeLine => SeeText(See);
        public string EditLine => EditText(EditRank);
        public string SeeChip => See == "member" ? "Members" : See == "veteran" ? "Veterans" : See == "officer" ? "Officers" : "";
        public Visibility SeeChipVisibility => SeeChip.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility UpdateVisibility => CanUpdate ? Visibility.Visible : Visibility.Collapsed;
        public Visibility SaveCardVisibility => IsAddon || IsText ? Visibility.Collapsed : Visibility.Visible;

        // ---- addons (Lodge 2.32): installed in WoW from this slot, and whether it keeps itself updated
        public bool IsAddon { get; private set; }
        public int InstalledV { get; private set; }
        public bool AutoUpdate { get; private set; }
        public void SetInstalled(InstalledRecord r, SlotVersionInfo newest)
        {
            InstalledV = r?.V ?? 0; AutoUpdate = r?.Auto ?? false;
            if (InstalledV > 0 && newest != null && newest.V > InstalledV) Badge = "Update";
            else if (InstalledV > 0 && Badge == "New") Badge = "";
            foreach (var n in new[] { nameof(IsAddon), nameof(InstalledV), nameof(AutoUpdate), nameof(InstallVisibility), nameof(InstallLabel), nameof(InstallTip), nameof(InstalledLine), nameof(InWowText),
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
                nameof(UpdateVisibility), nameof(SaveCardVisibility), nameof(KindGlyph), nameof(CoverVisibility), nameof(PlaceholderVisibility),
                nameof(IsText), nameof(KindChip), nameof(KindChipTip), nameof(KindChipVisibility), nameof(CopyVisibility), nameof(PreviewVisibility),
                nameof(KindText), nameof(VersionText), nameof(SizeText), nameof(SizeBytes), nameof(UpdatedText), nameof(UpdatedAt), nameof(ByText), nameof(InWowText) }) Raise(n);
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

    // 2.36: a kit - a named set of files; its addons can be installed and kept updated together
    public sealed class KitVM : Bindable
    {
        public string Id { get; private set; }
        public string Name { get; private set; } = "";
        public string Desc { get; private set; } = "";
        public string See { get; private set; }
        public bool CanManage { get; private set; }
        public List<SlotVM> Slots { get; private set; } = new List<SlotVM>();
        string error = "";

        public void Update(FileKit k, IEnumerable<SlotVM> all, bool canManage)
        {
            Id = k.Id; Name = k.Name ?? ""; Desc = k.Desc ?? ""; See = k.See; CanManage = canManage;
            var byId = all.ToDictionary(s => s.Id, s => s);
            Slots = k.Slots.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
            Refresh();
        }

        public IEnumerable<SlotVM> Addons => Slots.Where(s => s.IsAddon);
        public string CountLine => Slots.Count == 1 ? "1 file" : Slots.Count + " files" + (Addons.Any() ? " · " + Addons.Count() + " addon" + (Addons.Count() == 1 ? "" : "s") : "");
        public string StateLine
        {
            get
            {
                var a = Addons.ToList();
                if (a.Count == 0) return "";
                int have = a.Count(s => s.InstalledV > 0), old = a.Count(s => s.InstalledV > 0 && s.Newest != null && s.Newest.V > s.InstalledV);
                if (have == 0) return "None of its addons is installed yet";
                return have + " of " + a.Count + " addons installed" + (old > 0 ? " · " + old + " can be updated" : have == a.Count ? " · up to date" : "") + (AutoAll ? " · keeps itself updated" : "");
            }
        }
        public string InstallAllLabel => Addons.Any(s => s.InstalledV == 0) ? "Install all" : Addons.Any(s => s.Newest != null && s.Newest.V > s.InstalledV) ? "Update all" : "Reinstall all";
        public Visibility InstallAllVisibility => Addons.Any() ? Visibility.Visible : Visibility.Collapsed;
        public bool AutoAll => Addons.Any() && Addons.All(s => s.InstalledV > 0 && s.AutoUpdate);
        public Visibility AutoVisibility => Addons.Any(s => s.InstalledV > 0) ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ManageVisibility => CanManage ? Visibility.Visible : Visibility.Collapsed;
        public Visibility DescVisibility => Desc.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        public string SeeChip => See == "member" ? "Members" : See == "veteran" ? "Veterans" : See == "officer" ? "Officers" : "";
        public Visibility SeeChipVisibility => SeeChip.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        public string Error { get => error; set { if (Set(ref error, value ?? "")) Raise(nameof(ErrorVisibility)); } }
        public Visibility ErrorVisibility => error.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        public string FileNames => string.Join(", ", Slots.Select(s => s.Name));

        public void Refresh()
        {
            foreach (var n in new[] { nameof(Name), nameof(Desc), nameof(CountLine), nameof(StateLine), nameof(InstallAllLabel), nameof(InstallAllVisibility), nameof(AutoAll), nameof(AutoVisibility),
                nameof(ManageVisibility), nameof(DescVisibility), nameof(SeeChip), nameof(SeeChipVisibility), nameof(FileNames) }) Raise(n);
        }
    }

    // 3.5.2: the Details view's column widths - shared by the header and every row (they bind to one instance), so a narrow window drops the
    // least needed columns everywhere at once: In WoW, then Shared by, Updated, Size, Version (Name, Kind and the buttons stay)
    public sealed class FileColumns : Bindable
    {
        public static readonly (string Key, double Width)[] Order = { ("Kind", 110), ("Version", 60), ("Size", 80), ("Updated", 110), ("By", 110), ("InWow", 100) };
        readonly Dictionary<string, GridLength> w = Order.ToDictionary(o => o.Key, o => new GridLength(o.Width));
        public GridLength Kind => w["Kind"];
        public GridLength Version => w["Version"];
        public GridLength Size => w["Size"];
        public GridLength Updated => w["Updated"];
        public GridLength By => w["By"];
        public GridLength InWow => w["InWow"];
        public bool Shown(string key) => w[key].Value > 0;

        // the room the list has: keep columns in Order while the name keeps at least NameMin
        public const double NameMin = 200, Actions = 210;
        public void Fit(double width)
        {
            double left = width - NameMin - Actions - 32;
            foreach (var (key, cw) in Order)
            {
                bool fits = key == "Kind" || left >= cw;
                if (fits && key != "Kind") left -= cw; else if (key == "Kind") left -= cw;
                var g = new GridLength(fits ? cw : 0);
                if (w[key] != g) { w[key] = g; Raise(key); }
            }
        }
    }
}
