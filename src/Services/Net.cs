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
        static readonly HttpClient Http;

        static Net()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; // GitHub needs TLS 1.2
            Http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromMinutes(5) };
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("ElansAddonHub/" + App.Version);
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

        public static async Task Download(string url, string toFile, IProgress<double> progress)
        {
            if (!Util.IsUrl(url))
            {
                File.Copy(url.Replace("file:///", ""), toFile, true);
                progress?.Report(1);
                return;
            }
            using (var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
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
