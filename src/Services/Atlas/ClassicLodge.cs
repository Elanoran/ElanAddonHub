using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services.Atlas.Classic;

namespace ElansAddonHub.Services.Atlas
{
    // Where the Classic baseline comes from when a Lodge hosts it. Only the SOURCE of the files changes: the files are put into the same folder the public download uses and
    // read by the same importer, so ClassicAtlas, ClassicIndex and every page stay source-agnostic.
    //  Members: when the connected Lodge hosts the data and this PC has none (or an older release), it is fetched in the background, checked against the SHA-256 the Lodge
    //  announced, unpacked, the dump verified, installed and read. A failure is told in plain words (the banner falls back to the public download), never retried in a loop.
    //  Owner and officers: can share the data they have with the Lodge (always on a click: the one in Settings, or the quiet offer after their own download).
    public sealed class ClassicLodgeSync
    {
        public static TimeSpan Backoff = TimeSpan.FromHours(6);   // after a failure the same release is not tried again for this long (a new release, a restart or a click tries at once)

        readonly LodgeSession session;
        readonly Func<ClassicSetup> getSetup;
        readonly Settings settings;
        readonly Action save;
        ClassicSetup attached;
        readonly object gate = new object();
        bool fetching, sharing, offerArmed;
        string failedKey = "";
        DateTime failedAt;

        public event Action Changed;                   // may fire on a worker thread: the page marshals
        public string Problem { get; private set; } = "";        // the last failed automatic / requested fetch, in plain words
        public string ShareProblem { get; private set; } = "";
        public double ShareFraction { get; private set; }

        public ClassicLodgeSync(LodgeSession session, Func<ClassicSetup> setup, Settings settings, Action save = null)
        {
            this.session = session; getSetup = setup; this.settings = settings;
            this.save = save ?? (() => SettingsStore.Save(settings));
            session.ClassicChanged += OnSessionChanged;
        }

        // the self-test pauses the app's own instance while it drives another one
        public bool Suspended;

        public void Dispose()
        {
            session.ClassicChanged -= OnSessionChanged;
            if (attached != null) { attached.Changed -= OnSetupChanged; attached.DownloadedFromSource -= OnDownloaded; attached = null; }
        }

        void OnSessionChanged() { if (Suspended) return; Changed?.Invoke(); _ = EvaluateAsync(); }

        ClassicSetup Attach()
        {
            var s = getSetup();
            if (!ReferenceEquals(s, attached))
            {
                if (attached != null) { attached.Changed -= OnSetupChanged; attached.DownloadedFromSource -= OnDownloaded; }
                attached = s;
                if (s != null) { s.Changed += OnSetupChanged; s.DownloadedFromSource += OnDownloaded; }
            }
            return s;
        }

        void OnSetupChanged() => Changed?.Invoke();

        // the person just downloaded the data from the public source: an owner or officer is offered to share it (once per release)
        void OnDownloaded() { offerArmed = true; Changed?.Invoke(); }

        // ---- what is where

        public bool Auto => !settings.ClassicFromLodgeOff;
        public ClassicHosted Hosted => session.ClassicHosted;
        public bool Fetching => fetching;
        public bool Sharing => sharing;

        bool Usable => session.SupportsClassic && !session.IsGuestNow && session.ClassicKnown && session.ClassicHosted != null;

        bool InBackoff(ClassicHosted h) => h != null && failedKey == h.Sha256 && session.Clock() - failedAt < Backoff;

        // the Lodge can hand the data out right now (a click on "Get Classic data" goes there instead of the internet)
        public bool CanProvide
        {
            get
            {
                if (!Usable || fetching) return false;
                var s = Attach();
                return s != null && !s.Busy && !InBackoff(session.ClassicHosted);
            }
        }

        string Local(ClassicSetup s) => s.Backend.State() == ClassicState.NotDownloaded ? null : ClassicBundle.LocalVersion(s.Backend.Folder());

        // ---- members: fetch

        bool ShouldFetch(ClassicSetup s)
        {
            if (settings.ClassicFromLodgeOff || settings.ClassicLodgePaused) return false;
            if (!Usable || s.Busy || fetching || sharing) return false;
            var h = session.ClassicHosted;
            if (InBackoff(h)) return false;
            if (s.Backend.State() != ClassicState.NotDownloaded)
            {
                var local = ClassicBundle.LocalVersion(s.Backend.Folder());
                if (local == null || !ClassicBundle.IsNewer(h.Version, local)) return false;   // the same or a newer release is here already
            }
            return true;
        }

        public async Task EvaluateAsync()
        {
            for (int round = 0; round < 2; round++)
            {
                if (Suspended || settings.ClassicFromLodgeOff || settings.ClassicLodgePaused || !Usable) return;   // the cheap checks first: nothing is loaded for a Lodge that has nothing for us
                var s = Attach();
                if (s == null || !ShouldFetch(s)) return;
                await FetchAsync(s, session.ClassicHosted).ConfigureAwait(false);
            }
        }

        // the person asked for the data and the Lodge has it: no waiting for the automatic check, no matter the switch
        public async Task<bool> GetNowAsync()
        {
            var s = Attach();
            if (s == null || !CanProvide) return false;
            settings.ClassicLodgePaused = false; save();
            return await FetchAsync(s, session.ClassicHosted).ConfigureAwait(false);
        }

        async Task<bool> FetchAsync(ClassicSetup s, ClassicHosted hosted)
        {
            lock (gate) { if (fetching) return false; fetching = true; }
            Problem = "";
            Changed?.Invoke();
            try
            {
                var folder = s.Backend.Folder();
                bool ok = await s.StartFromAsync("Getting Classic data from your Lodge", async (progress, ct) =>
                {
                    var stage = Path.Combine(folder, ".lodge-staging");
                    try
                    {
                        if (Directory.Exists(stage)) Util.DeleteDir(stage);
                        Directory.CreateDirectory(stage);
                        var zip = Path.Combine(stage, "bundle.zip");
                        var err = await session.FetchClassicAsync(zip, hosted, f => progress.Report(new ClassicDownloadProgress { BytesDone = (long)(f * 1000), BytesTotal = 1000 }), ct).ConfigureAwait(false);
                        if (err != null) throw new ClassicDownloadException(Reason(err));
                        ct.ThrowIfCancellationRequested();
                        var files = Path.Combine(stage, "files");
                        err = ClassicBundle.Unpack(zip, files);
                        if (err != null) throw new ClassicDownloadException(Reason(err));
                        var dump = Directory.GetFiles(files, "ClassicDB_*.sql.gz").FirstOrDefault();
                        string gerr = null;
                        if (dump == null || !ClassicDownloader.IsGzipValid(dump, out gerr)) throw new ClassicDownloadException(Reason("the dump is not usable" + (gerr == null ? "" : " (" + gerr + ")")));
                        ct.ThrowIfCancellationRequested();
                        ClassicBundle.Install(files, folder, session.LodgeName);
                    }
                    finally { try { Util.DeleteDir(stage); } catch { } }
                }).ConfigureAwait(false);
                if (!ok && s.LastFailure.Length > 0)
                {
                    failedKey = hosted.Sha256; failedAt = session.Clock();
                    Problem = s.LastFailure;
                }
                return ok;
            }
            finally { lock (gate) fetching = false; Changed?.Invoke(); }
        }

        static string Reason(string why) => "Couldn't get the Classic data from your Lodge: " + (why ?? "").TrimEnd('.') + ". You can download it from the public source instead.";

        // "Remove from this PC": the person does not want it, so it is not fetched again behind their back
        public void NoteUserRemoved()
        {
            if (settings.ClassicLodgePaused) return;
            settings.ClassicLodgePaused = true; save();
            Changed?.Invoke();
        }

        // the switch in Settings changed
        public void SettingsChanged()
        {
            if (!settings.ClassicFromLodgeOff && settings.ClassicLodgePaused) { settings.ClassicLodgePaused = false; save(); }
            Changed?.Invoke();
            _ = EvaluateAsync();
        }

        // ---- owner and officers: share

        // the button "Share my Classic data with the Lodge" is shown: allowed, the Lodge has nothing or an older release, and this PC has a complete newer-or-first set
        public bool ShareAvailable
        {
            get
            {
                if (!session.SupportsClassic || !session.CanShareClassic || !session.ClassicKnown || sharing || fetching) return false;
                var s = Attach();
                if (s == null || s.Busy || s.Backend.State() != ClassicState.Ready) return false;
                var local = ClassicBundle.LocalVersion(s.Backend.Folder());
                if (local == null) return false;
                var h = session.ClassicHosted;
                return h == null || ClassicBundle.IsNewer(local, h.Version);
            }
        }

        // the quiet question after the owner's or an officer's own download: asked once per release
        public bool ShareOfferPending
        {
            get
            {
                if (!offerArmed || !ShareAvailable) return false;
                var local = ClassicBundle.LocalVersion(Attach().Backend.Folder());
                return local != null && settings.ClassicShareOffered != local;
            }
        }

        public void DeclineOffer()
        {
            offerArmed = false;
            var s = Attach();
            var local = s == null ? null : ClassicBundle.LocalVersion(s.Backend.Folder());
            if (local != null) { settings.ClassicShareOffered = local; save(); }
            Changed?.Invoke();
        }

        public async Task<bool> ShareAsync()
        {
            var s = Attach();
            if (s == null || !ShareAvailable) return false;
            lock (gate) { if (sharing) return false; sharing = true; }
            ShareProblem = ""; ShareFraction = 0; offerArmed = false;
            Changed?.Invoke();
            var folder = s.Backend.Folder();
            var work = folder.TrimEnd('\\', '/') + ".share";
            try
            {
                var zip = Path.Combine(work, "classic-bundle.zip");
                string version = null, sha = null;
                var err = await Task.Run(() =>
                {
                    var e = ClassicBundle.Pack(folder, zip, out version);
                    if (e == null) sha = LodgeFiles.Sha256(zip);
                    return e;
                }).ConfigureAwait(false);
                if (err == null) err = await session.ShareClassicAsync(zip, version, sha, f => { ShareFraction = f; Changed?.Invoke(); }).ConfigureAwait(false);
                if (err != null) { ShareProblem = err; return false; }
                settings.ClassicShareOffered = version; save();
                return true;
            }
            catch (Exception e)
            {
                Util.Log("classic share: " + e);
                ShareProblem = "Could not share the Classic data: " + e.Message;
                return false;
            }
            finally
            {
                try { if (Directory.Exists(work)) Util.DeleteDir(work); } catch { }
                lock (gate) sharing = false;
                Changed?.Invoke();
            }
        }

        public string Unshare()
        {
            var err = session.UnshareClassic();
            if (err != null) ShareProblem = err;
            Changed?.Invoke();
            return err;
        }

        // ---- the line in Settings > Lodge
        public string StatusText
        {
            get
            {
                if (!session.Connected && !session.TestFed) return "";
                if (!session.SupportsClassic)
                {
                    var v = LodgeSession.ParseVersion(session.ServerVersion);
                    if (v != null && v >= LodgeSession.ClassicServer) return "The Lodge owner has switched hosting the Classic data off. You can download it from the Atlas page.";
                    return session.CanUpdateServer ? "This Lodge's server is older than 2.17, so it can't host the Classic data yet. Update it to share the data with your members."
                        : "This Lodge's server is older than 2.17, so it can't host the Classic data yet. You can download it from the Atlas page.";
                }
                if (session.IsGuestNow) return "Guests download the Classic data from the Atlas page.";
                if (!session.ClassicKnown) return "Asking your Lodge...";
                var s = Attach();
                bool have = s != null && s.Backend.State() != ClassicState.NotDownloaded;
                if (fetching) return "Getting the Classic data from your Lodge...";
                if (sharing) return "Sharing the Classic data with your Lodge... " + Math.Round(ShareFraction * 100) + " %";
                if (ShareProblem.Length > 0) return ShareProblem;
                if (Problem.Length > 0) return Problem;
                var h = session.ClassicHosted;
                if (h == null)
                    return session.CanShareClassic
                        ? (have ? "Your Lodge doesn't host the Classic data yet. Share yours and every member gets it automatically."
                                : "Your Lodge doesn't host the Classic data yet. Download it on the Atlas page first, then share it here so members get it automatically.")
                        : "Your Lodge doesn't host the Classic data. You can download it from the Atlas page.";
                var hosting = "Your Lodge hosts the Classic data: " + ClassicBundle.Describe(h.Version)
                    + (string.IsNullOrEmpty(h.UploadedBy) ? "" : ", shared by " + h.UploadedBy) + ".";
                if (have) return hosting + (s.ViaLodge ? " This PC got it from the Lodge." : " This PC has it.");
                if (settings.ClassicFromLodgeOff) return hosting + " Automatic download is off: use the button on the Atlas page.";
                if (settings.ClassicLodgePaused) return hosting + " You removed it from this PC, so it isn't fetched again unless you ask on the Atlas page.";
                return hosting + " This PC gets it automatically.";
            }
        }
    }
}
