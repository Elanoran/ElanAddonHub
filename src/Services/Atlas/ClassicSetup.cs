using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ElansAddonHub.Services.Atlas.Classic;

namespace ElansAddonHub.Services.Atlas
{
    // What the Classic baseline card does, with the real downloader / importer behind an interface so the self-test can use a fake.
    public interface IClassicBackend
    {
        ClassicState State();
        ClassicIndex Index();                                  // the loaded index (provenance, counts); null when there is none
        Task<ClassicRemote> Query(CancellationToken ct);
        Task<ClassicDownloadResult> Download(ClassicRemote remote, IProgress<ClassicDownloadProgress> progress, CancellationToken ct);
        Task<ClassicImportReport> BuildIndex(IProgress<ClassicProgress> progress, CancellationToken ct);
        void Delete();
        string Folder();                                       // where the files and the licence live (for the credit line)
    }

    public sealed class RealClassicBackend : IClassicBackend
    {
        public ClassicState State() => ClassicAtlas.State();
        public ClassicIndex Index() => ClassicAtlas.Current;
        public Task<ClassicRemote> Query(CancellationToken ct) => ClassicDownloader.Query(ct);
        public Task<ClassicDownloadResult> Download(ClassicRemote remote, IProgress<ClassicDownloadProgress> progress, CancellationToken ct) => ClassicDownloader.Download(remote, progress, ct);
        public Task<ClassicImportReport> BuildIndex(IProgress<ClassicProgress> progress, CancellationToken ct) => ClassicAtlas.BuildIndex(progress, ct);
        public void Delete() => ClassicAtlas.Delete();
        public string Folder() => ClassicPaths.Dir;
    }

    public enum ClassicCardState { NotDownloaded, Asking, Downloading, Importing, Downloaded, Ready, Failed }

    // The Classic baseline card: not downloaded -> (button) asking the user with the exact size and the licence -> downloading -> importing -> ready.
    // Never downloads without a "yes" from Confirm. A failure or a cancel leaves what was there before untouched (the downloader stages everything).
    public sealed class ClassicSetup
    {
        public static string ButtonText => "Get Classic data (about 19 MB, cMaNGOS, GPL-3.0)";
        public const string CreditLine = "Classic baseline: cMaNGOS classic-db (GPL-3.0), downloaded to this PC on request and labelled \"Classic baseline\" wherever it is shown.";
        public const string RepoUrl = "https://github.com/cmangos/classic-db";
        public const string BannerText = "Drop chances, vendors, spawns and quests from the Classic database: about 19 MB, downloaded from its public source and credited.";
        public const string GetButtonText = "Get Classic data";

        // a note of a page that says the Classic baseline is missing and where to get it (gets a "Get Classic data" button of its own)
        public static bool IsGetPrompt(string note)
        {
            if (string.IsNullOrEmpty(note) || note.IndexOf("Classic baseline", StringComparison.Ordinal) < 0) return false;
            return note.IndexOf("not on this PC", StringComparison.Ordinal) >= 0 || note.IndexOf("Download the Classic baseline", StringComparison.Ordinal) >= 0
                || note.IndexOf("Download it from", StringComparison.Ordinal) >= 0 || note.IndexOf("Get it from", StringComparison.Ordinal) >= 0;
        }

        public IClassicBackend Backend;
        public ClassicSetup(IClassicBackend backend = null) { Backend = backend ?? new RealClassicBackend(); State = FromBackend(); }

        public ClassicCardState State { get; private set; }
        public string Phase { get; private set; } = "";
        public double Fraction { get; private set; }
        public string Error { get; private set; } = "";
        public ClassicRemote Remote { get; private set; }
        public bool Busy => State == ClassicCardState.Asking || State == ClassicCardState.Downloading || State == ClassicCardState.Importing;
        public event Action Changed;                       // may fire on a worker thread: the page marshals

        // asked once, after the sizes are known and before any byte is downloaded: false = the user said no
        public Func<ClassicRemote, bool> Confirm = r => true;

        CancellationTokenSource cts;

        ClassicCardState FromBackend()
        {
            switch (Backend.State())
            {
                case ClassicState.Ready: return ClassicCardState.Ready;
                case ClassicState.Downloaded: return ClassicCardState.Downloaded;
                default: return ClassicCardState.NotDownloaded;
            }
        }

        void Set(ClassicCardState s, string phase = "", double fraction = 0)
        {
            State = s; Phase = phase ?? ""; Fraction = fraction;
            Changed?.Invoke();
        }

        // the backend's state again (a file appeared or went away); a failure stays on screen until the user tries again or removes the files
        public void Refresh() { if (Busy || State == ClassicCardState.Failed) return; State = FromBackend(); Changed?.Invoke(); }

        // the whole flow; completes when it is ready, failed or cancelled
        public async Task StartAsync()
        {
            if (Busy) return;
            Error = ""; Remote = null;
            cts = new CancellationTokenSource();
            var ct = cts.Token;
            try
            {
                if (Backend.State() == ClassicState.NotDownloaded)
                {
                    Set(ClassicCardState.Asking, "Asking GitHub what is there");
                    Remote = await Backend.Query(ct).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    if (!Confirm(Remote)) { Remote = null; Set(FromBackend()); return; }
                    Set(ClassicCardState.Downloading, "Starting the download");
                    await Backend.Download(Remote, new Prog<ClassicDownloadProgress>(p => Set(ClassicCardState.Downloading, p.Phase + (string.IsNullOrEmpty(p.File) ? "" : " · " + p.File), p.Fraction)), ct).ConfigureAwait(false);
                }
                ct.ThrowIfCancellationRequested();
                Set(ClassicCardState.Importing, "Reading the data", 0);
                var rep = await Backend.BuildIndex(new Prog<ClassicProgress>(p => Set(ClassicCardState.Importing, p.Phase, p.Fraction)), ct).ConfigureAwait(false);
                if (rep == null) { Error = "The download is not complete."; Set(ClassicCardState.Failed); return; }
                if (rep.Cancelled) { Set(FromBackend()); return; }
                Set(FromBackend());
            }
            catch (OperationCanceledException) { Set(FromBackend()); }
            catch (ClassicDownloadException e) { Error = e.Message; Set(ClassicCardState.Failed); }
            catch (Exception e)
            {
                Util.Log("atlas classic setup: " + e);
                Error = "Something went wrong: " + e.Message; Set(ClassicCardState.Failed);
            }
            finally { cts?.Dispose(); cts = null; }
        }

        public void Cancel() { try { cts?.Cancel(); } catch (ObjectDisposedException) { } }

        public void Delete()
        {
            if (Busy) return;
            try { Backend.Delete(); Error = ""; Set(FromBackend()); }
            catch (Exception e) { Error = "Could not remove the files: " + e.Message; Set(ClassicCardState.Failed); }
        }

        // "Ready · data from 2 May 2024 · 3,100 creatures, 9,000 items, 4,500 quests"
        public string ReadyText
        {
            get
            {
                var idx = Backend.Index();
                if (idx == null) return "";
                var p = idx.Provenance;
                var date = DumpDate(p);
                return "Ready" + (date.Length > 0 ? " · dump of " + date : "") + " · " + idx.Npcs.Count.ToString("N0", Lodge.CalendarRules.English) + " creatures, "
                    + idx.Items.Count.ToString("N0", Lodge.CalendarRules.English) + " items, " + idx.Quests.Count.ToString("N0", Lodge.CalendarRules.English) + " quests";
            }
        }

        public static string DumpDate(ClassicProvenance p)
        {
            foreach (var s in new[] { p?.CommitDate, p?.BuiltUtc })
                if (!string.IsNullOrEmpty(s) && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d))
                    return d.ToString("d MMM yyyy", Lodge.CalendarRules.English);
            return "";
        }

        public string LicensePath()
        {
            var dir = Backend.Folder();
            foreach (var n in ClassicDownloader.LicenseFiles)
            {
                var f = Path.Combine(dir ?? "", n);
                if (File.Exists(f)) return f;
            }
            return null;
        }

        sealed class Prog<T> : IProgress<T>
        {
            readonly Action<T> a;
            public Prog(Action<T> a) { this.a = a; }
            public void Report(T value) { a(value); }
        }
    }
}
