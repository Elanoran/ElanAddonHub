using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ElansAddonHub.Services;

namespace ElansAddonHub.Lodge
{
    // The Files page of the Lodge (server 2.14): the slots, Save..., Versions, and - for officers and the owner - Upload new, Replace, Roll back and
    // Delete. All state is in LodgeSession (Slots, FilesChanged). Nothing is ever opened or run from here.
    public partial class FilesView : UserControl
    {
        LodgeSession S;
        SlotVM formSlot;                // null = a new slot
        string formPath;
        SlotVM versionsSlot;
        bool uploading;

        // test seams: the Windows file dialogs (modal, they would block the self-test)
        public Func<string> OpenPicker;                     // returns the file to upload, null = cancelled
        public Func<string, string, string> SavePicker;     // (suggested name, start folder) -> where to save, null = cancelled

        public FilesView()
        {
            InitializeComponent();
            foreach (var r in Elans.Shared.FileSlot.SeeRanks) ESee.Items.Add(new ComboBoxItem { Content = SlotVM.SeeText(r), Tag = r });
            foreach (var r in Elans.Shared.FileSlot.EditRanks.Reverse()) EEdit.Items.Add(new ComboBoxItem { Content = SlotVM.EditText(r), Tag = r });
            PreviewKeyDown += (s, e) =>
            {
                if (e.Key != Key.Escape) return;
                if (FormOverlay.Visibility == Visibility.Visible) { CloseForm(); e.Handled = true; }
                else if (EditOverlay.Visibility == Visibility.Visible) { CloseEdit(); e.Handled = true; }
                else if (VersionsOverlay.Visibility == Visibility.Visible) { CloseVersions(); e.Handled = true; }
            };
        }

        public void Init(LodgeSession session)
        {
            S = session;
            SlotList.ItemsSource = S.Slots;
            var view = System.Windows.Data.CollectionViewSource.GetDefaultView(S.Slots);
            view.Filter = o => Matches(o as SlotVM);
            S.FilesChanged += Refresh;
            Refresh();
        }

        public void Show()
        {
            Refresh();
            S.RequestFiles();
        }

        void Refresh()
        {
            if (S == null) return;
            NewButton.Visibility = S.CanManageFiles ? Visibility.Visible : Visibility.Collapsed;
            SearchBox.Visibility = S.Slots.Count > 6 || SearchBox.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            System.Windows.Data.CollectionViewSource.GetDefaultView(S.Slots).Refresh();
            int shown = S.Slots.Count(Matches);
            EmptyState.Visibility = shown == 0 && S.FilesLoaded ? Visibility.Visible : Visibility.Collapsed;
            EmptyTitle.Text = S.Slots.Count == 0 ? "No files shared yet" : "Nothing matches \"" + SearchBox.Text.Trim() + "\"";
            EmptyHint.Text = S.Slots.Count > 0 ? "Try another word, or clear the search." : S.CanManageFiles ? "Press Share a file to share an addon, a UI pack, a guide or a build with everyone." : "Officers and the Guild Master share files here; you can save and install them.";
            UsageText.Text = S.FilesLoaded && S.Slots.Count > 0
                ? LodgeFiles.Size(S.FilesUsedBytes) + " of " + S.FilesTotalMb + " MB used - the newest " + LodgeFiles.KeptVersions + " versions of each file are kept"
                : "";
            if (versionsSlot != null && VersionsOverlay.Visibility == Visibility.Visible)
            {
                if (!S.Slots.Contains(versionsSlot)) CloseVersions(); else FillVersions();
            }
            if (editSlot != null && EditOverlay.Visibility == Visibility.Visible && !S.Slots.Contains(editSlot)) CloseEdit();
        }

        // ---- search: name and description, any word order
        bool Matches(SlotVM s)
        {
            if (s == null) return false;
            var q = (SearchBox?.Text ?? "").Trim();
            if (q.Length == 0) return true;
            var hay = (s.Name + " " + s.Desc + " " + (s.IsAddon ? "addon" : "")).ToLowerInvariant();
            return q.ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).All(hay.Contains);
        }

        void Search_Changed(object sender, TextChangedEventArgs e) => Refresh();

        // ---- Save...
        void Save_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is SlotVM slot) _ = SaveAsync(slot, slot.Newest, null, null);
        }

        void VersionSave_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is VersionVM row) _ = SaveAsync(row.Slot, row.Info, null, VersionsError);
        }

        // dest = where to save; null asks with the Save-as dialog (which starts in Downloads)
        public async Task<string> SaveAsync(SlotVM slot, SlotVersionInfo ver, string dest, TextBlock errorSink)
        {
            if (slot == null || ver == null) return null;
            // 3.1.1: a type that can run something when opened is warned about first, like a chat file before its download
            if (Elans.Shared.DangerousFiles.IsDangerous(ver.FileName))
            {
                var shown = SafeFile.Name(ver.FileName);
                var ext = SafeFile.Ext(shown);
                if (!Dialog.Confirm(Window.GetWindow(this), $"Save a {ext.TrimStart('.').ToUpperInvariant()} file?",
                        $"\"{shown}\" is a {ext} file. Files like this can run programs on your PC when opened. Only save it if you know who shared it and expected it.", "Save anyway", danger: true))
                    return null;
            }
            if (dest == null)

            {
                var name = SafeFile.Name(ver.FileName);
                var start = LodgeFiles.DownloadsFolder();
                if (SavePicker != null) dest = SavePicker(name, start);
                else
                {
                    var dlg = new Microsoft.Win32.SaveFileDialog { FileName = name, InitialDirectory = start, Filter = "All files|*.*", OverwritePrompt = true, Title = "Save " + slot.Name };
                    dest = dlg.ShowDialog(Window.GetWindow(this)) == true ? dlg.FileName : null;
                }
                if (dest == null) return null;
            }
            var err = await S.SaveVersionAsync(slot, ver, dest);
            if (err != null) { slot.Error = err; if (errorSink != null) errorSink.Text = err; }
            else if (errorSink != null) errorSink.Text = "";
            return err;
        }

        // ---- Install in WoW (addons, Lodge 2.32)
        public Func<Elans.Shared.AddonZip.Result, SlotVM, bool> ConfirmInstall;     // test seam: the confirm dialog (modal)

        void Install_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is SlotVM slot) _ = InstallAsync(slot);
        }

        public async Task<string> InstallAsync(SlotVM slot)
        {
            var err = await S.InstallSlotAsync(slot, chk => ConfirmInstall != null ? ConfirmInstall(chk, slot) : AskInstall(chk, slot));
            if (!string.IsNullOrEmpty(err)) slot.Error = err;
            else if (err == null) S.NoteFiles(slot.Name + " v" + slot.InstalledV + " is installed in WoW - type /reload in the game (or start it)", slot.Id);
            Refresh();
            return err;
        }

        bool AskInstall(Elans.Shared.AddonZip.Result chk, SlotVM slot)
        {
            var dir = LodgeAddons.AddOnsDir() ?? "";
            var lines = chk.Folders.Select(f =>
            {
                var have = System.IO.Directory.Exists(System.IO.Path.Combine(dir, f));
                var v = chk.Versions.TryGetValue(f, out var x) && x.Length > 0 ? " " + x : "";
                return "  " + f + v + (have ? "  (replaces the one installed - it is backed up first)" : "  (new)");
            });
            var by = slot.Newest == null ? "" : DisplayNames.Shown(slot.Newest.UploadedBy);
            return Dialog.Confirm(Window.GetWindow(this), (slot.InstalledV == 0 ? "Install " : "Update ") + slot.Name + " in WoW?",
                "Shared by " + by + ". These addon folders go into WoW Forever's AddOns folder:\n" + string.Join("\n", lines)
                + "\n\nAddons run inside the game: only install addons from people you trust. Type /reload in WoW afterwards.", slot.InstalledV == 0 ? "Install" : "Update");
        }

        void Auto_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as CheckBox)?.Tag is SlotVM slot) S.SetAutoUpdate(slot, (sender as CheckBox).IsChecked == true);
        }

        async Task LoadPreviewAsync(SlotVM slot)
        {
            var (text, err) = await S.FetchTextAsync(slot);
            if (err == null) slot.TextPreview = Preview(text);
        }

        // ---- Copy (text slots, 2.34)
        public static Action<string> SetClipboard = t => Clipboard.SetText(t);          // test seam: the real clipboard
        void Copy_Click(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.Tag is SlotVM slot) _ = CopyAsync(slot); }

        public async Task<string> CopyAsync(SlotVM slot)
        {
            var (text, err) = await S.FetchTextAsync(slot);
            if (err != null) { slot.Error = err; return err; }
            try { SetClipboard(text); }
            catch (Exception) { slot.Error = "Couldn't reach the clipboard - try again"; return slot.Error; }
            S.NoteFiles(slot.Name + " is on the clipboard - paste it in the game", slot.Id);
            slot.TextPreview = Preview(text);
            return null;
        }

        static string Preview(string t) => t.Length > 600 ? t.Substring(0, 600) + " ..." : t;

        // ---- Versions
        void Versions_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is SlotVM slot) OpenVersions(slot);
        }

        public void OpenVersions(SlotVM slot)
        {
            versionsSlot = slot;
            if (slot != null && slot.IsText && slot.TextPreview.Length == 0) _ = LoadPreviewAsync(slot);
            VersionsError.Text = "";
            FillVersions();
            VersionsOverlay.Visibility = Visibility.Visible;
            if (IsLoaded && IsVisible) Motion.PageIn(VersionsOverlay);
        }

        void FillVersions()
        {
            var s = versionsSlot;
            DetailsCard.DataContext = s;
            DSee.Visibility = DEdit.Visibility = S.SupportsFiles2 ? Visibility.Visible : Visibility.Collapsed;
            VersionsSub.Text = "The newest " + LodgeFiles.KeptVersions + " versions are kept. Save any of them" + (s.CanUpdate ? ", or roll back to an older one." : ".");
            VersionList.ItemsSource = s.VersionRows();
        }

        void VersionRollBack_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.Tag is VersionVM row)) return;
            RollBack(row.Slot, row.Info.V);
        }

        public void RollBack(SlotVM slot, int v)
        {
            if (!Dialog.Confirm(Window.GetWindow(this), "Roll back " + slot.Name + "?",
                    "v" + v + " becomes the newest version again. The newer versions are removed from the lodge and can't be brought back.", "Roll back", true)) return;
            var err = S.RollBackSlot(slot, v);
            VersionsError.Text = err ?? "";
            if (err == null) CloseVersions();
        }

        void VersionsClose_Click(object sender, RoutedEventArgs e) => CloseVersions();

        void CloseVersions()
        {
            VersionsOverlay.Visibility = Visibility.Collapsed;
            versionsSlot = null;
        }

        // ---- Edit (2.33): what changed is sent; the rest is left as it is
        SlotVM editSlot;
        string editPicture;               // a picture chosen in the dialog (uploaded on Save), "" = remove it, null = unchanged
        public Func<string> PicturePicker;                  // test seam: the Windows file dialog

        void Edit_Click(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.Tag is SlotVM slot) OpenEdit(slot); }

        public void OpenEdit(SlotVM slot)
        {
            if (slot == null || !S.SupportsFiles2 || !slot.CanUpdate) return;
            editSlot = slot; editPicture = null;
            EHeading.Text = "Edit " + slot.Name;
            ENamePanel.Visibility = ERanksPanel.Visibility = S.CanManageFiles ? Visibility.Visible : Visibility.Collapsed;
            EName.Text = slot.Name;
            EDesc.Text = slot.Desc;
            ENote.Text = slot.Newest?.Note ?? "";
            EAddon.Visibility = S.SupportsAddonFiles ? Visibility.Visible : Visibility.Collapsed;
            EAddon.IsChecked = slot.IsAddon;
            ESee.SelectedItem = ESee.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == (slot.See ?? "guest"));
            EEdit.SelectedItem = EEdit.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == (slot.EditRank ?? "officer"));
            ShowEditPicture(slot.CoverImage);
            EError.Text = ""; ESave.IsEnabled = true;
            EditOverlay.Visibility = Visibility.Visible;
            if (IsLoaded && IsVisible) Motion.PageIn(EditOverlay);
        }

        void ShowEditPicture(System.Windows.Media.ImageSource img)
        {
            EPicture.Source = img;
            ENoPicture.Visibility = img == null ? Visibility.Visible : Visibility.Collapsed;
            EPicRemove.Visibility = img == null ? Visibility.Collapsed : Visibility.Visible;
        }

        void EPicPick_Click(object sender, RoutedEventArgs e)
        {
            string path;
            if (PicturePicker != null) path = PicturePicker();
            else
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Choose a picture", Filter = "Pictures|*.png;*.jpg;*.jpeg;*.gif;*.webp|All files|*.*" };
                path = dlg.ShowDialog(Window.GetWindow(this)) == true ? dlg.FileName : null;
            }
            if (path == null) return;
            SetEditPicture(path);
        }

        public void SetEditPicture(string path)
        {
            EError.Text = "";
            try
            {
                if (new FileInfo(path).Length > LodgeClient.MaxCoverBytes) { EError.Text = "A picture can be 2 MB at most - pick a smaller one."; return; }
                if (LodgeFiles.PictureType(path) == null) { EError.Text = "That isn't a picture (png, jpg, gif or webp)."; return; }
            }
            catch (IOException) { EError.Text = "That file can't be read."; return; }
            editPicture = path;
            ShowEditPicture(LodgeFiles.LoadPicture(path, 256));
        }

        void EPicRemove_Click(object sender, RoutedEventArgs e) { editPicture = ""; ShowEditPicture(null); }
        void ECancel_Click(object sender, RoutedEventArgs e) => CloseEdit();
        void CloseEdit() { EditOverlay.Visibility = Visibility.Collapsed; editSlot = null; }

        void ESave_Click(object sender, RoutedEventArgs e) => _ = SaveEditAsync();

        public async Task<string> SaveEditAsync()
        {
            var s = editSlot;
            if (s == null) return null;
            var m = new Elans.Shared.FilesEditMsg();
            bool any = false;
            if (S.CanManageFiles)
            {
                var name = EName.Text.Trim();
                if (name.Length == 0) { EError.Text = "Give it a name."; return EError.Text; }
                if (name != s.Name) { m.Name = name; any = true; }
                var see = (ESee.SelectedItem as ComboBoxItem)?.Tag as string ?? "guest";
                if (see != (s.See ?? "guest")) { m.See = see; any = true; }
                var ed = (EEdit.SelectedItem as ComboBoxItem)?.Tag as string ?? "officer";
                if (ed != (s.EditRank ?? "officer")) { m.Edit = ed; any = true; }
            }
            var desc = EDesc.Text.Trim();
            if (desc != s.Desc) { m.Desc = desc; any = true; }
            if (s.Newest != null && ENote.Text.Trim() != (s.Newest.Note ?? "")) { m.NoteV = s.Newest.V; m.Note = ENote.Text.Trim(); any = true; }
            if (S.SupportsAddonFiles && (EAddon.IsChecked == true) != s.IsAddon) { m.Kind = EAddon.IsChecked == true ? "addon" : "file"; any = true; }
            if (editPicture == "" && s.CoverV > 0) { m.Cover = false; any = true; }
            ESave.IsEnabled = false;
            try
            {
                if (any)
                {
                    var err = S.EditSlot(s, m);
                    if (err != null) { EError.Text = err; return err; }
                }
                if (!string.IsNullOrEmpty(editPicture))
                {
                    var err = await S.SetCoverAsync(s, editPicture);
                    if (err != null) { EError.Text = err; return err; }
                }
                CloseEdit();
                return null;
            }
            finally { ESave.IsEnabled = true; }
        }

        // ---- Delete
        void Delete_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is SlotVM slot) Delete(slot);
        }

        public void Delete(SlotVM slot)
        {
            if (!Dialog.Confirm(Window.GetWindow(this), "Delete " + slot.Name + "?",
                    "This removes the file and all its versions for everyone in the lodge. It can't be undone.", "Delete", true)) return;
            var err = S.DeleteSlot(slot);
            if (err != null) slot.Error = err;
        }

        // ---- the upload form
        void New_Click(object sender, RoutedEventArgs e) => OpenForm(null);
        void Replace_Click(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.Tag is SlotVM slot) OpenForm(slot); }

        public void OpenForm(SlotVM slot)
        {
            formSlot = slot; formPath = null; uploading = false;
            FormHeading.Text = slot == null ? "Upload a file" : "New version of " + slot.Name;
            FormSub.Text = slot == null ? (S.SupportsFiles2 ? "Everyone in the lodge can see it until you choose who under Edit. " : "Everyone in the lodge can see it and save it. ") + "Programs and scripts are not allowed; up to " + S.FilesMaxMb + " MB."
                                        : "Replaces what everyone gets; the older version stays available under Versions.";
            FName.Text = slot?.Name ?? ""; FName.IsEnabled = slot == null;
            FNote.Text = ""; FFile.Text = "No file chosen"; FError.Text = "";
            FAddon.Visibility = S.SupportsAddonFiles ? Visibility.Visible : Visibility.Collapsed;
            FModePanel.Visibility = S.SupportsFilesText && slot == null ? Visibility.Visible : Visibility.Collapsed;
            FModeText.IsChecked = slot?.IsText == true; FModeFile.IsChecked = slot?.IsText != true; FText.Text = "";
            ApplyMode();
            FAddon.IsChecked = slot?.IsAddon == true; FAddon.IsEnabled = true; addonTouched = false;
            FAddonWhy.Text = "";
            FGo.Content = "Upload"; FGo.IsEnabled = true; FPick.IsEnabled = true;
            FormOverlay.Visibility = Visibility.Visible;
            if (IsLoaded && IsVisible) Motion.PageIn(FormOverlay);
            (slot == null ? FName : (Control)FNote).Focus();
        }

        void FPick_Click(object sender, RoutedEventArgs e)
        {
            string path;
            if (OpenPicker != null) path = OpenPicker();
            else
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Choose the file to share" };
                path = dlg.ShowDialog(Window.GetWindow(this)) == true ? dlg.FileName : null;
            }
            if (path == null) return;
            SetFormFile(path);
        }

        public void SetFormFile(string path)
        {
            formPath = path;
            FFile.Text = Path.GetFileName(path);
            if (formSlot == null && FName.Text.Trim().Length == 0)
            {
                var stem = Path.GetFileNameWithoutExtension(path);
                FName.Text = stem.Length > LodgeFiles.MaxNameChars ? stem.Substring(0, LodgeFiles.MaxNameChars) : stem;
            }
            FError.Text = LodgeFiles.CheckUpload(path, S.FilesMaxMb) ?? "";
            CheckAddonChoice();
        }

        // the addon checkbox follows the file: an addon zip ticks it (unless the uploader decided), anything else can't be marked
        bool addonTouched;
        void FAddon_Click(object sender, RoutedEventArgs e) { addonTouched = true; CheckAddonChoice(); }
        void CheckAddonChoice()
        {
            if (!S.SupportsAddonFiles || formPath == null) { FAddonWhy.Text = ""; return; }
            var chk = LodgeFiles.IsZipName(formPath) ? Elans.Shared.AddonZip.Check(formPath) : null;
            if (chk == null) { FAddon.IsChecked = false; FAddon.IsEnabled = false; FAddonWhy.Text = "Only a .zip of addon folders can be marked as an addon."; return; }
            FAddon.IsEnabled = true;
            if (!addonTouched) FAddon.IsChecked = chk.Ok || formSlot?.IsAddon == true;
            FAddonWhy.Text = chk.Ok ? "Addon folders: " + string.Join(", ", chk.Folders.Select(f => f + (chk.Versions.TryGetValue(f, out var v) && v.Length > 0 ? " " + v : ""))) + "."
                : FAddon.IsChecked == true ? "Not an addon zip: " + chk.Problem + "." : "";
        }

        bool TextMode => S.SupportsFilesText && FModeText.IsChecked == true;
        void FMode_Click(object sender, RoutedEventArgs e) => ApplyMode();
        void ApplyMode()
        {
            FFilePanel.Visibility = TextMode ? Visibility.Collapsed : Visibility.Visible;
            FTextPanel.Visibility = TextMode ? Visibility.Visible : Visibility.Collapsed;
            if (TextMode) { FAddon.Visibility = Visibility.Collapsed; FAddonWhy.Text = ""; }
            else FAddon.Visibility = S.SupportsAddonFiles ? Visibility.Visible : Visibility.Collapsed;
        }

        void Form_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { FGo_Click(null, null); e.Handled = true; } }
        void FCancel_Click(object sender, RoutedEventArgs e) => CloseForm();

        void CloseForm()
        {
            if (uploading) return;      // the upload is under way; it closes the form itself
            FormOverlay.Visibility = Visibility.Collapsed;
        }

        void FGo_Click(object sender, RoutedEventArgs e) => _ = GoAsync();

        public async Task<string> GoAsync()
        {
            if (uploading) return null;
            if (TextMode)
            {
                if (formSlot == null && FName.Text.Trim().Length == 0) { FError.Text = "Give it a name"; return FError.Text; }
                if (FText.Text.Trim().Length == 0) { FError.Text = "Paste the text to share"; return FError.Text; }
                uploading = true; FGo.IsEnabled = false; FError.Text = "";
                try
                {
                    var terr = await S.UploadTextAsync(formSlot?.Id, FName.Text, FText.Text, FNote.Text);
                    if (terr != null) { FError.Text = terr; return terr; }
                    uploading = false;
                    FormOverlay.Visibility = Visibility.Collapsed;
                    return null;
                }
                finally { uploading = false; FGo.IsEnabled = true; }
            }
            var refusal = formPath == null ? "Choose the file to share" : LodgeFiles.CheckUpload(formPath, S.FilesMaxMb);
            if (refusal != null) { FError.Text = refusal; return refusal; }
            uploading = true; FGo.IsEnabled = false; FPick.IsEnabled = false; FError.Text = "";
            try
            {
                bool? addon = S.SupportsAddonFiles ? FAddon.IsChecked == true : (bool?)null;
                var err = await S.UploadSlotAsync(formSlot?.Id, FName.Text, formPath, FNote.Text, p => Dispatcher.BeginInvoke(new Action(() => FGo.Content = "Uploading " + (int)(p * 100) + "%")), addon);
                if (err != null) { FError.Text = err; return err; }
                uploading = false;
                FormOverlay.Visibility = Visibility.Collapsed;
                return null;
            }
            finally { uploading = false; FGo.IsEnabled = true; FPick.IsEnabled = true; FGo.Content = "Upload"; }
        }

        // ---- overlay plumbing
        void Scrim_Down(object sender, MouseButtonEventArgs e)
        {
            if (FormOverlay.Visibility == Visibility.Visible) CloseForm();
            else if (EditOverlay.Visibility == Visibility.Visible) CloseEdit();
            else CloseVersions();
        }
        void Box_Down(object sender, MouseButtonEventArgs e) => e.Handled = true;

        // ---- test hooks
        public bool NewButtonVisibleForTest => NewButton.Visibility == Visibility.Visible;
        public bool EmptyVisibleForTest => EmptyState.Visibility == Visibility.Visible;
        public string UsageTextForTest => UsageText.Text;
        public bool FormOpenForTest => FormOverlay.Visibility == Visibility.Visible;
        public bool VersionsOpenForTest => VersionsOverlay.Visibility == Visibility.Visible;
        public string FormErrorForTest => FError.Text;
        public string FormNameForTest { get => FName.Text; set => FName.Text = value; }
        public string FormNoteForTest { get => FNote.Text; set => FNote.Text = value; }
        public bool FormNameEditableForTest => FName.IsEnabled;
        public string FormHeadingForTest => FormHeading.Text;
        public void PickForTest(string path) => SetFormFile(path);
        public void FormTextForTest(string text) { FModeText.IsChecked = true; ApplyMode(); FText.Text = text; }
        public bool FormModesShownForTest => FModePanel.Visibility == Visibility.Visible;
        public bool EditOpenForTest => EditOverlay.Visibility == Visibility.Visible;
        public void CloseEditForTest() => CloseEdit();
        public bool EditRanksShownForTest => ERanksPanel.Visibility == Visibility.Visible;
        public string EditErrorForTest => EError.Text;
        public void EditSetForTest(string name = null, string desc = null, string see = null, string editRank = null, string note = null, bool? addon = null)
        {
            if (name != null) EName.Text = name;
            if (desc != null) EDesc.Text = desc;
            if (note != null) ENote.Text = note;
            if (addon != null) EAddon.IsChecked = addon;
            if (see != null) ESee.SelectedItem = ESee.Items.OfType<ComboBoxItem>().First(i => (string)i.Tag == see);
            if (editRank != null) EEdit.SelectedItem = EEdit.Items.OfType<ComboBoxItem>().First(i => (string)i.Tag == editRank);
        }
        public void SearchForTest(string q) => SearchBox.Text = q;
        public int CardsShownForTest => S.Slots.Count(Matches);
        public bool DetailsShowsRanksForTest => DSee.Visibility == Visibility.Visible;
        public bool? FormAddonForTest { get => FAddon.Visibility == Visibility.Visible ? FAddon.IsChecked : null; set { FAddon.IsChecked = value; addonTouched = true; CheckAddonChoice(); } }
        public string FormAddonWhyForTest => FAddonWhy.Text;
        public void CloseFormForTest() => CloseForm();
        public void CloseVersionsForTest() => CloseVersions();
        public int VersionRowsForTest => VersionList.Items.Count;
        public System.Collections.Generic.IEnumerable<VersionVM> VersionRowItemsForTest => VersionList.Items.OfType<VersionVM>();
    }
}
