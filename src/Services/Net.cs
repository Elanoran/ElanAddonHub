using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ElansAddonHub.Services
{
    public static class Net
    {
        internal static readonly HttpClient Http;

        static Net()
        {
            // 3.1 (.NET 10): TLS 1.2 / 1.3 are negotiated by the system by default (the old ServicePointManager switch is obsolete)
            Http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromMinutes(5) };
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("ElansAddonHub/" + App.Version);
        }

        // ---- where an UPDATE may come from (the Outpost's own exe, the addon zips of the manifest). Normal downloads (third-party addons,
        // CurseForge, WoWInterface...) are not restricted here; the update paths pass trustedOnly = true.
        // Rule: https, and a GitHub host (the release page and the CDN it redirects to). Checked on the address in the manifest AND on the
        // address the download really came from after redirects. A developer / the self-test can switch it off (see AllowDevSources).
        public static bool AllowDevSources = string.Equals(Environment.GetEnvironmentVariable("ELANSHUB_DEV_SOURCES"), "1", StringComparison.Ordinal);

        // null = fine, otherwise a plain-language reason
        public static string UpdateUrlProblem(string url)
        {
            if (AllowDevSources) return null;
            if (string.IsNullOrWhiteSpace(url)) return "The update has no download address.";
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u)) return "The update address is not a web address.";
            if (!string.Equals(u.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return "The update address is not https, so it was refused.";
            var host = u.Host.ToLowerInvariant();
            if (host == "github.com" || host == "www.github.com" || host.EndsWith(".githubusercontent.com", StringComparison.Ordinal)) return null;
            return "The update comes from " + host + ", which is not a GitHub address, so it was refused.";
        }

        // "dd..." exactly 64 hex characters
        public static bool IsSha256(string s)
        {
            if (s == null || s.Length != 64) return false;
            foreach (var c in s) if (!Uri.IsHexDigit(c)) return false;
            return true;
        }

        // Works with http(s) URLs and, for testing, plain file paths.
        public static async Task<string> GetText(string url)
        {
            if (!Util.IsUrl(url)) return File.ReadAllText(url.Replace("file:///", ""));
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            {
                var sep = url.Contains("?") ? "&" : "?";
                var resp = await Http.GetAsync(url + sep + "t=" + DateTime.UtcNow.Ticks, cts.Token); // skip caches
                resp.EnsureSuccessStatusCode();
                return await resp.Content.ReadAsStringAsync();
            }
        }

        // 2.99: the exact bytes (the release signature is over them), with the same cache skip as GetText
        public static async Task<byte[]> GetBytes(string url)
        {
            if (!Util.IsUrl(url)) return File.ReadAllBytes(url.Replace("file:///", ""));
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            {
                var sep = url.Contains("?") ? "&" : "?";
                var resp = await Http.GetAsync(url + sep + "t=" + DateTime.UtcNow.Ticks, cts.Token);
                resp.EnsureSuccessStatusCode();
                return await resp.Content.ReadAsByteArrayAsync();
            }
        }

        // the text, or null when it is not there (404, or no such file); other failures throw like GetText
        public static async Task<string> GetTextOrNull(string url)
        {
            if (!Util.IsUrl(url)) { var f = url.Replace("file:///", ""); return File.Exists(f) ? File.ReadAllText(f) : null; }
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            {
                var sep = url.Contains("?") ? "&" : "?";
                var resp = await Http.GetAsync(url + sep + "t=" + DateTime.UtcNow.Ticks, cts.Token);
                if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
                resp.EnsureSuccessStatusCode();
                return await resp.Content.ReadAsStringAsync();
            }
        }

        public static async Task Download(string url, string toFile, IProgress<double> progress, bool trustedOnly = false)
        {
            if (trustedOnly) { var why = UpdateUrlProblem(url); if (why != null) throw new InvalidOperationException(why); }
            if (!Util.IsUrl(url))
            {
                File.Copy(url.Replace("file:///", ""), toFile, true);
                progress?.Report(1);
                return;
            }
            using (var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                if (trustedOnly) { var why = UpdateUrlProblem(resp.RequestMessage?.RequestUri?.ToString()); if (why != null) throw new InvalidOperationException(why); }   // after redirects too
                var total = resp.Content.Headers.ContentLength ?? -1;
                using (var src = await resp.Content.ReadAsStreamAsync())
                using (var dst = File.Create(toFile))
                {
                    var buf = new byte[81920];
                    long done = 0;
                    int n;
                    while ((n = await src.ReadAsync(buf, 0, buf.Length)) > 0)
                    {
                        await dst.WriteAsync(buf, 0, n);
                        done += n;
                        if (total > 0) progress?.Report((double)done / total);
                    }
                }
            }
            progress?.Report(1);
        }
    }
}
