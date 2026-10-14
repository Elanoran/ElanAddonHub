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
        public const string CreditLineLodge = "Classic baseline: cMaNGOS classic-db (GPL-3.0), received unchanged from your Lodge with its licence, and labelled \"Classic baseline\" wherever it is shown.";
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
        public event Action DownloadedFromSource;          // a full download from the public source finished and was read (not a copy from a Lodge): the moment an owner or officer may be offered to share it

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
            bool downloaded = false;
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
                    downloaded = true;
                }
                ct.ThrowIfCancellationRequested();
                Set(ClassicCardState.Importing, "Reading the data", 0);
                var rep = await Backend.BuildIndex(new Prog<ClassicProgress>(p => Set(ClassicCardState.Importing, p.Phase, p.Fraction)), ct).ConfigureAwait(false);
                if (rep == null) { Error = "The download is not complete."; Set(ClassicCardState.Failed); return; }
                if (rep.Cancelled) { Set(FromBackend()); return; }
                Set(FromBackend());
                if (downloaded && State == ClassicCardState.Ready) DownloadedFromSource?.Invoke();
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

        // The same flow with the files coming from somewhere else (the Lodge): `acquire` puts a complete, checked set into Backend.Folder() (and throws a
        // ClassicDownloadException with a plain reason when it can't), then the index is built as usual. Returns true when the data is ready afterwards.
        // When data was already installed, a failure leaves it as it was (no alarm on the page); when there was none, the card shows the plain reason.
        public async Task<bool> StartFromAsync(string phase, Func<IProgress<ClassicDownloadProgress>, CancellationToken, Task> acquire)
        {
            if (Busy) return false;
            Error = ""; Remote = null; LastFailure = "";
            bool hadData = Backend.State() != ClassicState.NotDownloaded;
            cts = new CancellationTokenSource();
            var ct = cts.Token;
            try
            {
                Set(ClassicCardState.Downloading, phase);
                await acquire(new Prog<ClassicDownloadProgress>(p => Set(ClassicCardState.Downloading, phase, p.Fraction)), ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                Set(ClassicCardState.Importing, "Reading the data", 0);
                var rep = await Backend.BuildIndex(new Prog<ClassicProgress>(p => Set(ClassicCardState.Importing, p.Phase, p.Fraction)), ct).ConfigureAwait(false);
                if (rep == null) { Error = "The data is not complete."; Set(ClassicCardState.Failed); return false; }
                if (rep.Cancelled) { Set(FromBackend()); return false; }
                Set(FromBackend());
                return State == ClassicCardState.Ready;
            }
            catch (OperationCanceledException) { Set(FromBackend()); return false; }
            catch (ClassicDownloadException e)
            {
                if (hadData) { Util.Log("atlas classic setup: " + e.Message); Set(FromBackend()); }
                else { Error = e.Message; Set(ClassicCardState.Failed); }
                LastFailure = e.Message;
                return false;
            }
            catch (Exception e)
            {
                Util.Log("atlas classic setup: " + e);
                LastFailure = "Something went wrong: " + e.Message;
                if (hadData) Set(FromBackend()); else { Error = LastFailure; Set(ClassicCardState.Failed); }
                return false;
            }
            finally { cts?.Dispose(); cts = null; }
        }

        public string LastFailure { get; private set; } = "";
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
                return "Ready" + (date.Length > 0 ? " · dump of " + date : "") + (ViaLodge ? " · via your Lodge" : "") + " · " + idx.Npcs.Count.ToString("N0", Lodge.CalendarRules.English) + " creatures, "
                    + idx.Items.Count.ToString("N0", Lodge.CalendarRules.English) + " items, " + idx.Quests.Count.ToString("N0", Lodge.CalendarRules.English) + " quests";
            }
        }

        // the data on this PC came from the Lodge (source.json says "via": "lodge")
        public bool ViaLodge
        {
            get
            {
                try { return string.Equals(ClassicDownloader.ReadSource(Backend.Folder())?.Str("via"), "lodge", StringComparison.Ordinal); }
                catch (Exception) { return false; }
            }
        }

        public string CreditText => ViaLodge ? CreditLineLodge : CreditLine;
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
