namespace Lodge;

public class LodgeConfig
{
    public string Code { get; init; }          // optional shared invite code
    public string CodesFile { get; set; }      // personal codes, "Name:code:role" per line
    public string Urls { get; init; }
    public string DataDir { get; init; }
    public string PathBase { get; init; }
    public int MaxFileMb { get; init; }
    public long MaxFileBytes => MaxFileMb * 1024L * 1024L;
    public int FileDays { get; init; }
    public int MaxUsers { get; init; }
    public string[] TrustedProxies { get; init; } // extra proxy networks (CIDR) besides localhost

    // ---- bounds (see server/deploy/lodge.env.example)
    public long MaxStorageBytes { get; init; }    // all uploads together
    public int MaxFiles { get; init; }             // number of uploads kept
    public int MaxConcurrentUploads { get; init; }
    public int UploadsPer10Min { get; init; }      // per person (guests: per IP)
    public long HistoryMaxBytes { get; init; }     // per channel history file
    public int HistoryMax { get; set; } = 2000;     // messages kept per channel (2.9; was a fixed 200)
    public int MaxTrackedIps { get; init; }        // wrong-code bookkeeping
    public int HttpPerMinute { get; init; }        // per IP, all HTTP routes
    public bool AllowQueryCode { get; init; }      // legacy ?code= for 1.x clients (off by default since 2.8)
    public int MaxUploadsPerIdentity { get; set; } = 2;     // uploads running at once per person (guests: per IP)
    public int UploadTimeoutSeconds { get; set; } = 120;    // one upload may take this long at most
    public int IdleTimeoutSeconds { get; set; } = 75;       // a client that sends app-level pings and then goes silent is dropped
    public int InviteDefaultHours { get; set; } = 168;       // one-time invites live 7 days unless the creator picks 1 h - 30 d
    public int MaxPendingInvites { get; set; } = 200;
    public int MaxDevicesPerPerson { get; set; } = 10;       // device tokens per personal identity
    public int MaxDevicesTotal { get; set; } = 5000;
    public int HistoryFlushSeconds { get; set; } = 5;       // edits/reactions/deletes reach the disk after this long
    // ---- shared files (2.14, "files" feature): named slots with up to 3 kept versions each
    public bool FilesEnabled { get; set; } = true;          // the owner can switch the feature off (it then isn't advertised and answers 403)
    public int FilesMaxMb { get; set; } = 50;               // per file
    public long FilesMaxBytes => FilesMaxMb * 1024L * 1024L;
    public int FilesTotalMb { get; set; } = 500;            // all kept versions of all slots together
    public long FilesTotalBytes => FilesTotalMb * 1024L * 1024L;
    public int FilesUploadsPer10Min { get; set; } = 20;     // per person
    // ---- Atlas pool (2.15, "atlas" feature)
    public bool ArmoryEnabled { get; set; } = true;         // 2.16 "armory" feature: members may share characters; off = not advertised, put answers why "disabled", stored data stays on disk
    public bool ArmoryRepsEnabled { get; set; } = true;     // 2.27 "reputations" (part of the armory): reputations in the snapshots of members who switched it on, and "standing with a faction"; off = not advertised, reputations are dropped from new snapshots and never served
    public bool AtlasEnabled { get; set; } = true;          // the owner can switch the feature off (it then isn't advertised; share/get answer "disabled"; stored data stays on disk)
    // ---- hosted datasets (2.17, "classic" feature): the Classic baseline files, stored once for the whole lodge
    public bool ClassicEnabled { get; set; } = true;        // the owner can switch it off (not advertised; upload / download / delete answer 403; the stored bundle stays on disk)
    public int ClassicMaxMb { get; set; } = 40;             // the bundle (the real one is about 15 MB)
    public int ClassicDownloadsPerHour { get; set; } = 6;   // per member
    // ---- badges and the ladder (2.18, "badges" feature)
    public bool BadgesEnabled { get; set; } = true;         // the server operator can switch it off (not advertised, requests answer "disabled", the data stays on disk); the Guild Master has a second switch
    // ---- realm status (2.20, "realms" feature): the lodge asks the Blizzard Game Data API and tells the members. The secret never leaves this process.
    public string BnetClientId { get; set; } = "";
    public string BnetClientSecret { get; set; } = "";      // never logged, never sent to a client, never in an error
    public string BnetRegion { get; set; } = "eu";
    public int RealmsPollSeconds { get; set; } = 120;       // 60 - 900
    public string[] RealmsNamespaces { get; set; } = Array.Empty<string>();   // optional override of the namespaces probed
    public string[] RealmsMatch { get; set; } = new[] { "forever" };          // words / realm names / slugs that mark a WoW Forever realm (LODGE_REALMS_MATCH)
    public bool RealmsConfigured => BnetClientId.Length > 0 && BnetClientSecret.Length > 0;
    // ---- contribute to the Cairn (opt-in, off by default; Cairn.cs): the Lodge's pooled Atlas numbers (ids and counts only) go to an open commons
    public bool CairnEnabled { get; set; }                  // LODGE_CAIRN_ENABLED=1, and only with a valid URL and key
    public string CairnUrl { get; set; } = "";              // the Cairn's base URL (https), e.g. https://example.org/cairn - nothing is built in
    public string CairnKey { get; set; } = "";              // this Lodge's Cairn key (LODGE_CAIRN_KEY, or a file named by LODGE_CAIRN_KEY_FILE); never logged
    public int CairnEveryMinutes { get; set; } = 60;        // 15 - 1440
    public int CairnGameBuild { get; set; }                 // 0 = the newest build the members' finds report
    public int CairnFetchHours { get; set; } = 6;           // give to get: how often the Cairn's manifest is checked (2 - 48 hours; the file only when it changed)

    static string Env(string key, string fallback)
    {
        var v = Environment.GetEnvironmentVariable(key);
        return v == null ? fallback : v.Trim();
    }

    static int Int(string key, int fallback, int min, int max) =>
        int.TryParse(Env(key, ""), out var v) ? Math.Clamp(v, min, max) : fallback;

    // eu / us / kr / tw (two or three lowercase letters); anything else falls back to eu so a typo can't build a strange host name
    public static string CleanRegion(string r)
    {
        r = (r ?? "").Trim().ToLowerInvariant();
        return r.Length is 2 or 3 && r.All(c => c is >= 'a' and <= 'z') ? r : "eu";
    }

    public static LodgeConfig FromEnv()
    {
        var code = Env("LODGE_CODE", "");
        var c = new LodgeConfig
        {
            Code = code.Length >= 6 ? code : "",
            CodesFile = Env("LODGE_CODES_FILE", ""),
            Urls = Env("LODGE_URLS", "http://127.0.0.1:5280"),
            DataDir = Path.GetFullPath(Env("LODGE_DATA", "data")),
            PathBase = Env("LODGE_PATHBASE", "/lodge").TrimEnd('/'),
            MaxFileMb = Int("LODGE_MAX_FILE_MB", 25, 1, 1024),
            FileDays = Int("LODGE_FILE_DAYS", 30, 1, 3650),
            MaxUsers = Int("LODGE_MAX_USERS", 25, 1, 1000),
            TrustedProxies = Env("LODGE_TRUSTED_PROXIES", "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            MaxStorageBytes = Int("LODGE_MAX_STORAGE_MB", 2048, 1, 1024 * 1024) * 1024L * 1024L,
            MaxFiles = Int("LODGE_MAX_FILES", 2000, 1, 1_000_000),
            MaxConcurrentUploads = Int("LODGE_MAX_CONCURRENT_UPLOADS", 4, 1, 100),
            UploadsPer10Min = Int("LODGE_UPLOADS_PER_10MIN", 30, 1, 10000),
            HistoryMaxBytes = Int("LODGE_HISTORY_MAX_KB", 4096, 16, 1024 * 1024) * 1024L,
            HistoryMax = Int("LODGE_HISTORY_MAX", 2000, 50, 50000),
            MaxTrackedIps = Int("LODGE_MAX_TRACKED_IPS", 10000, 10, 1_000_000),
            HttpPerMinute = Int("LODGE_HTTP_PER_MINUTE", 240, 10, 100000),
            MaxUploadsPerIdentity = Int("LODGE_UPLOADS_PER_USER", 2, 1, 100),
            UploadTimeoutSeconds = Int("LODGE_UPLOAD_TIMEOUT_S", 120, 5, 3600),
            IdleTimeoutSeconds = Int("LODGE_IDLE_TIMEOUT_S", 75, 30, 3600),
            HistoryFlushSeconds = Int("LODGE_HISTORY_FLUSH_S", 5, 1, 600),
            InviteDefaultHours = Int("LODGE_INVITE_HOURS", 168, 1, 720),
            MaxPendingInvites = Int("LODGE_MAX_PENDING_INVITES", 200, 1, 10000),
            MaxDevicesPerPerson = Int("LODGE_MAX_DEVICES_PER_PERSON", 10, 1, 100),
            AllowQueryCode = string.Equals(Env("LODGE_ALLOW_QUERY_CODE", "false"), "true", StringComparison.OrdinalIgnoreCase),
            FilesEnabled = !(Env("LODGE_FILES_ENABLED", "1") is "0" or "false" or "no" or "off"),
            FilesMaxMb = Int("LODGE_FILES_MAX_MB", 50, 1, 1024),
            FilesTotalMb = Int("LODGE_FILES_TOTAL_MB", 500, 1, 1024 * 1024),
            FilesUploadsPer10Min = Int("LODGE_FILES_UPLOADS_PER_10MIN", 20, 1, 1000),
            ArmoryEnabled = !(Env("LODGE_ARMORY_ENABLED", "1") is "0" or "false" or "no" or "off"),
            ArmoryRepsEnabled = !(Env("LODGE_REPUTATIONS_ENABLED", "1") is "0" or "false" or "no" or "off"),
            AtlasEnabled = !(Env("LODGE_ATLAS_ENABLED", "1") is "0" or "false" or "no" or "off"),
            ClassicEnabled = !(Env("LODGE_CLASSIC_ENABLED", "1") is "0" or "false" or "no" or "off"),
            ClassicMaxMb = Int("LODGE_CLASSIC_MAX_MB", 40, 1, 1024),
            ClassicDownloadsPerHour = Int("LODGE_CLASSIC_DOWNLOADS_PER_HOUR", 6, 1, 1000),
            BadgesEnabled = !(Env("LODGE_BADGES_ENABLED", "1") is "0" or "false" or "no" or "off"),
            BnetClientId = Env("LODGE_BNET_CLIENT_ID", ""),
            BnetClientSecret = Env("LODGE_BNET_CLIENT_SECRET", ""),
            BnetRegion = CleanRegion(Env("LODGE_BNET_REGION", "eu")),
            RealmsPollSeconds = Int("LODGE_REALMS_POLL_SECONDS", 120, 60, 900),
            RealmsNamespaces = Env("LODGE_REALMS_NAMESPACES", "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            CairnEnabled = Env("LODGE_CAIRN_ENABLED", "0") is "1" or "true" or "yes" or "on",
            CairnUrl = Env("LODGE_CAIRN_URL", ""),
            CairnKey = CairnKeyFromEnv(),
            CairnEveryMinutes = Int("LODGE_CAIRN_EVERY_MINUTES", 60, 15, 1440),
            CairnGameBuild = Int("LODGE_CAIRN_GAME_BUILD", 0, 0, 9_999_999),
            CairnFetchHours = Int("LODGE_CAIRN_FETCH_HOURS", 6, 2, 48),
            RealmsMatch = Env("LODGE_REALMS_MATCH", "forever").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is { Length: > 0 } rm ? rm : new[] { "forever" },
        };
        Directory.CreateDirectory(c.DataDir);
        if (string.IsNullOrEmpty(c.CodesFile))
        {
            // codes live in the data folder so the hub's Guild Master panel can edit them;
            // 1.x installs had them in /etc/lodge (install.sh moves them)
            var inData = Path.Combine(c.DataDir, "codes");
            c.CodesFile = File.Exists(inData) || !File.Exists("/etc/lodge/codes") ? inData : "/etc/lodge/codes";
        }
        return c;
    }

    static string CairnKeyFromEnv()
    {
        var k = Env("LODGE_CAIRN_KEY", "");
        var file = Env("LODGE_CAIRN_KEY_FILE", "");
        if (k.Length == 0 && file.Length > 0) { try { k = File.ReadAllText(file).Trim(); } catch { k = ""; } }
        return k;
    }

    // tests build configs directly
    public static LodgeConfig ForTests(string dataDir, Action<LodgeConfig> tweak = null) => new()
    {
        Code = "", CodesFile = Path.Combine(dataDir, "codes"), Urls = "http://127.0.0.1:0", DataDir = dataDir, PathBase = "",
        MaxFileMb = 25, FileDays = 30, MaxUsers = 25, TrustedProxies = Array.Empty<string>(),
        MaxStorageBytes = 2048L * 1024 * 1024, MaxFiles = 2000, MaxConcurrentUploads = 4, UploadsPer10Min = 30,
        HistoryMaxBytes = 1024 * 1024, HistoryMax = 200, MaxTrackedIps = 10000, HttpPerMinute = 240, AllowQueryCode = true,
    };
}
