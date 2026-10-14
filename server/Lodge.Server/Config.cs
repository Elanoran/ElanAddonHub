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
    public bool AtlasEnabled { get; set; } = true;          // the owner can switch the feature off (it then isn't advertised; share/get answer "disabled"; stored data stays on disk)

    static string Env(string key, string fallback)
    {
        var v = Environment.GetEnvironmentVariable(key);
        return v == null ? fallback : v.Trim();
    }

    static int Int(string key, int fallback, int min, int max) =>
        int.TryParse(Env(key, ""), out var v) ? Math.Clamp(v, min, max) : fallback;

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
            AtlasEnabled = !(Env("LODGE_ATLAS_ENABLED", "1") is "0" or "false" or "no" or "off"),
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

    // tests build configs directly
    public static LodgeConfig ForTests(string dataDir, Action<LodgeConfig> tweak = null) => new()
    {
        Code = "", CodesFile = Path.Combine(dataDir, "codes"), Urls = "http://127.0.0.1:0", DataDir = dataDir, PathBase = "",
        MaxFileMb = 25, FileDays = 30, MaxUsers = 25, TrustedProxies = Array.Empty<string>(),
        MaxStorageBytes = 2048L * 1024 * 1024, MaxFiles = 2000, MaxConcurrentUploads = 4, UploadsPer10Min = 30,
        HistoryMaxBytes = 1024 * 1024, HistoryMax = 200, MaxTrackedIps = 10000, HttpPerMinute = 240, AllowQueryCode = true,
    };
}
