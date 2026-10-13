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
            PreviewKeyDown += (s, e) =>
            {
                if (e.Key != Key.Escape) return;
                if (FormOverlay.Visibility == Visibility.Visible) { CloseForm(); e.Handled = true; }
                else if (VersionsOverlay.Visibility == Visibility.Visible) { CloseVersions(); e.Handled = true; }
            };
        }

        public void Init(LodgeSession session)
        {
            S = session;
            SlotList.ItemsSource = S.Slots;
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
            EmptyState.Visibility = S.Slots.Count == 0 && S.FilesLoaded ? Visibility.Visible : Visibility.Collapsed;
            EmptyHint.Text = S.CanManageFiles ? "Press Upload new to share a pack, a guide or a build with everyone." : "Officers and the lodge owner share files here; you can save them.";
            UsageText.Text = S.FilesLoaded && S.Slots.Count > 0
                ? LodgeFiles.Size(S.FilesUsedBytes) + " of " + S.FilesTotalMb + " MB used - the newest " + LodgeFiles.KeptVersions + " versions of each file are kept"
                : "";
            if (versionsSlot != null && VersionsOverlay.Visibility == Visibility.Visible)
            {
                if (!S.Slots.Contains(versionsSlot)) CloseVersions(); else FillVersions();
            }
        }

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

        // ---- Versions
        void Versions_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is SlotVM slot) OpenVersions(slot);
        }

        public void OpenVersions(SlotVM slot)
        {
            versionsSlot = slot;
            VersionsError.Text = "";
            FillVersions();
            VersionsOverlay.Visibility = Visibility.Visible;
            if (IsLoaded && IsVisible) Motion.PageIn(VersionsOverlay);
        }

        void FillVersions()
        {
            var s = versionsSlot;
            VersionsHeading.Text = s.Name;
            VersionsSub.Text = "The newest " + LodgeFiles.KeptVersions + " versions are kept. Save any of them" + (S.CanManageFiles ? ", or roll back to an older one." : ".");
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
            FormSub.Text = slot == null ? "Everyone in the lodge can see it and save it. Programs and scripts are not allowed; up to " + S.FilesMaxMb + " MB."
                                        : "Replaces what everyone gets; the older version stays available under Versions.";
            FName.Text = slot?.Name ?? ""; FName.IsEnabled = slot == null;
            FNote.Text = ""; FFile.Text = "No file chosen"; FError.Text = "";
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
            var refusal = formPath == null ? "Choose the file to share" : LodgeFiles.CheckUpload(formPath, S.FilesMaxMb);
            if (refusal != null) { FError.Text = refusal; return refusal; }
            uploading = true; FGo.IsEnabled = false; FPick.IsEnabled = false; FError.Text = "";
            try
            {
                var err = await S.UploadSlotAsync(formSlot?.Id, FName.Text, formPath, FNote.Text, p => Dispatcher.BeginInvoke(new Action(() => FGo.Content = "Uploading " + (int)(p * 100) + "%")));
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
            if (FormOverlay.Visibility == Visibility.Visible) CloseForm(); else CloseVersions();
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
        public void CloseFormForTest() => CloseForm();
        public void CloseVersionsForTest() => CloseVersions();
        public int VersionRowsForTest => VersionList.Items.Count;
        public System.Collections.Generic.IEnumerable<VersionVM> VersionRowItemsForTest => VersionList.Items.OfType<VersionVM>();
    }
}
