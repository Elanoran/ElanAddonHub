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
    public int MaxTrackedIps { get; init; }        // wrong-code bookkeeping
    public int HttpPerMinute { get; init; }        // per IP, all HTTP routes
    public bool AllowQueryCode { get; init; }      // legacy ?code= for 1.x clients

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
            HistoryMaxBytes = Int("LODGE_HISTORY_MAX_KB", 1024, 16, 1024 * 1024) * 1024L,
            MaxTrackedIps = Int("LODGE_MAX_TRACKED_IPS", 10000, 10, 1_000_000),
            HttpPerMinute = Int("LODGE_HTTP_PER_MINUTE", 240, 10, 100000),
            AllowQueryCode = !string.Equals(Env("LODGE_ALLOW_QUERY_CODE", "true"), "false", StringComparison.OrdinalIgnoreCase),
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
        HistoryMaxBytes = 1024 * 1024, MaxTrackedIps = 10000, HttpPerMinute = 240, AllowQueryCode = true,
    };
}
