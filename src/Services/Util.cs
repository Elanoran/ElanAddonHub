using System;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;

namespace ElansAddonHub.Services
{
    public static class Util
    {
        // ELANSHUB_DATA overrides the folder (the self-test uses its own, never the real settings)
        public static readonly string DataDir =
            Environment.GetEnvironmentVariable("ELANSHUB_DATA") is string d && d.Length > 0 ? d
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ElansAddonHub");

        static Util() { Directory.CreateDirectory(DataDir); }

        public static void Log(string msg)
        {
            try
            {
                var path = Path.Combine(DataDir, "hub.log");
                if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024) File.Delete(path);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {msg}\r\n");
            }
            catch { }
        }

        public static T FromJson<T>(string json)
        {
            var ser = new DataContractJsonSerializer(typeof(T));
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json))) return (T)ser.ReadObject(ms);
        }

        public static string ToJson<T>(T obj)
        {
            var ser = new DataContractJsonSerializer(typeof(T));
            using (var ms = new MemoryStream())
            {
                ser.WriteObject(ms, obj);
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        public static string Sha256(string file)
        {
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(file))
            {
                var sb = new StringBuilder();
                foreach (var b in sha.ComputeHash(fs)) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        // "1.17.0" > "1.9.2"; missing parts count as 0; anything unparsable compares as text
        public static int CompareVersions(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return string.IsNullOrEmpty(b) ? 0 : -1;
            if (string.IsNullOrEmpty(b)) return 1;
            var pa = a.Trim().TrimStart('v').Split('.');
            var pb = b.Trim().TrimStart('v').Split('.');
            for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
            {
                var sa = i < pa.Length ? pa[i] : "0";
                var sb = i < pb.Length ? pb[i] : "0";
                if (int.TryParse(sa, out var na) && int.TryParse(sb, out var nb))
                {
                    if (na != nb) return na.CompareTo(nb);
                }
                else
                {
                    var c = string.CompareOrdinal(sa, sb);
                    if (c != 0) return c;
                }
            }
            return 0;
        }

        public static void CopyDir(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
            foreach (var d in Directory.GetDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
        }

        public static void DeleteDir(string dir)
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(dir, true);
        }

        public static bool IsUrl(string s) =>
            s != null && (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
    }
}
