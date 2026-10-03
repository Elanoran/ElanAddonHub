namespace Lodge;

public class LodgeConfig
{
    public string Code { get; init; }          // optional shared invite code
    public string CodesFile { get; init; }     // personal codes, "Name:code" per line
    public string Urls { get; init; }
    public string DataDir { get; init; }
    public string PathBase { get; init; }
    public int MaxFileMb { get; init; }
    public long MaxFileBytes => MaxFileMb * 1024L * 1024L;
    public int FileDays { get; init; }
    public int MaxUsers { get; init; }
    public string[] TrustedProxies { get; init; } // extra proxy networks (CIDR) besides localhost

    static string Env(string key, string fallback)
    {
        var v = Environment.GetEnvironmentVariable(key);
        return v == null ? fallback : v.Trim();
    }

    public static LodgeConfig FromEnv()
    {
        var code = Env("LODGE_CODE", "");
        var c = new LodgeConfig
        {
            Code = code.Length >= 6 ? code : "",
            CodesFile = Env("LODGE_CODES_FILE", "/etc/lodge/codes"),
            Urls = Env("LODGE_URLS", "http://127.0.0.1:5280"),
            DataDir = Path.GetFullPath(Env("LODGE_DATA", "data")),
            PathBase = Env("LODGE_PATHBASE", "/lodge").TrimEnd('/'),
            MaxFileMb = int.TryParse(Env("LODGE_MAX_FILE_MB", "25"), out var mb) ? mb : 25,
            FileDays = int.TryParse(Env("LODGE_FILE_DAYS", "30"), out var d) ? d : 30,
            MaxUsers = int.TryParse(Env("LODGE_MAX_USERS", "25"), out var u) ? u : 25,
            TrustedProxies = Env("LODGE_TRUSTED_PROXIES", "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        };
        Directory.CreateDirectory(c.DataDir);
        return c;
    }
}
