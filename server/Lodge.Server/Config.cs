using System.Security.Cryptography;
using System.Text;

namespace Lodge;

public class LodgeConfig
{
    public string Code { get; init; }
    public string Urls { get; init; }
    public string DataDir { get; init; }
    public string PathBase { get; init; }
    public int MaxFileMb { get; init; }
    public long MaxFileBytes => MaxFileMb * 1024L * 1024L;
    public int FileDays { get; init; }
    public int MaxUsers { get; init; }

    static string Env(string key, string fallback)
    {
        var v = Environment.GetEnvironmentVariable(key);
        return v == null ? fallback : v.Trim();
    }

    public static LodgeConfig FromEnv()
    {
        var code = Env("LODGE_CODE", "");
        if (code.Length < 6)
            throw new InvalidOperationException("Set LODGE_CODE (the invite code, at least 6 characters).");
        var c = new LodgeConfig
        {
            Code = code,
            Urls = Env("LODGE_URLS", "http://127.0.0.1:5280"),
            DataDir = Path.GetFullPath(Env("LODGE_DATA", "data")),
            PathBase = Env("LODGE_PATHBASE", "/lodge").TrimEnd('/'),
            MaxFileMb = int.TryParse(Env("LODGE_MAX_FILE_MB", "25"), out var mb) ? mb : 25,
            FileDays = int.TryParse(Env("LODGE_FILE_DAYS", "30"), out var d) ? d : 30,
            MaxUsers = int.TryParse(Env("LODGE_MAX_USERS", "25"), out var u) ? u : 25,
        };
        Directory.CreateDirectory(c.DataDir);
        return c;
    }

    // constant-time compare (hash both so different lengths don't leak either)
    public bool CodeOk(string given)
    {
        if (string.IsNullOrEmpty(given)) return false;
        var a = SHA256.HashData(Encoding.UTF8.GetBytes(given.Trim()));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(Code));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
